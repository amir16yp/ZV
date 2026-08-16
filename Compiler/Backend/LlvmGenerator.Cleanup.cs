using System;
using System.Collections.Generic;
using LLVMSharp.Interop;
using ZV.Compiler.AST;
using ZV.Compiler.Lexer;
using ZV.Compiler.Target;

namespace ZV.Compiler.Backend;

public partial class LlvmGenerator
{
    // -----------------------------------------------------------------------
    // Runtime cleanup stack (chunked)
    // -----------------------------------------------------------------------
    // Every owning variable pushes a cleanup record (object pointer + destructor
    // function pointer) onto a thread-local stack when it comes to life. The
    // stack grows as a linked list of fixed-size chunks. Scope exit, function
    // return, and exception unwinding pop records back to a previously saved top
    // and call the destructor for each popped record.

    private void EnsureCleanupGlobals()
    {
        if (_cleanupGlobalsInitialized) return;
        _cleanupGlobalsInitialized = true;

        var chunkType = GetCleanupChunkType();
        var chunkPtrType = GetPointerType(chunkType);

        _cleanupHeadGlobal = _module.AddGlobal(chunkPtrType, "__zv_cleanup_head");
        _cleanupHeadGlobal.Initializer = LLVMValueRef.CreateConstNull(chunkPtrType);
        _cleanupHeadGlobal.Linkage = LLVMLinkage.LLVMInternalLinkage;
        MakeThreadLocalIfSupported(_cleanupHeadGlobal);

        _cleanupUsedGlobal = _module.AddGlobal(GetInt32Type(), "__zv_cleanup_used");
        _cleanupUsedGlobal.Initializer = LLVMValueRef.CreateConstInt(GetInt32Type(), 0);
        _cleanupUsedGlobal.Linkage = LLVMLinkage.LLVMInternalLinkage;
        MakeThreadLocalIfSupported(_cleanupUsedGlobal);

        // Free list of empty chunks retained for reuse so pops don't repeatedly
        // malloc/free and tests can reason about user-visible free calls.
        _cleanupFreeGlobal = _module.AddGlobal(chunkPtrType, "__zv_cleanup_free");
        _cleanupFreeGlobal.Initializer = LLVMValueRef.CreateConstNull(chunkPtrType);
        _cleanupFreeGlobal.Linkage = LLVMLinkage.LLVMInternalLinkage;
        MakeThreadLocalIfSupported(_cleanupFreeGlobal);
    }

    private LLVMTypeRef GetCleanupChunkType()
    {
        if (!_cleanupChunkType.HasValue)
        {
            var ptrType = GetPointerType(GetInt8Type());
            var dtorFuncType = LLVMTypeRef.CreateFunction(GetVoidType(), new[] { ptrType });
            var dtorPtrType = GetPointerType(dtorFuncType);
            var objArrayType = LLVMTypeRef.CreateArray(ptrType, CleanupChunkCapacity);
            var dtorArrayType = LLVMTypeRef.CreateArray(dtorPtrType, CleanupChunkCapacity);

            var chunkType = _context.CreateNamedStruct("ZVCleanupChunk");
            chunkType.StructSetBody(new[] { objArrayType, dtorArrayType, GetPointerType(chunkType) }, Packed: false);
            _cleanupChunkType = chunkType;
        }
        return _cleanupChunkType.Value;
    }

    private void MakeThreadLocalIfSupported(LLVMValueRef global)
    {
        if (Target.Environment == TargetEnvironment.Hosted)
        {
            global.ThreadLocalMode = LLVMThreadLocalMode.LLVMGeneralDynamicTLSModel;
        }
    }

    private (LLVMValueRef Head, LLVMValueRef Used) BuildCleanupTopLoad()
    {
        EnsureCleanupGlobals();
        var head = _builder.BuildLoad2(GetPointerType(GetCleanupChunkType()), _cleanupHeadGlobal, "cleanup_head");
        var used = _builder.BuildLoad2(GetInt32Type(), _cleanupUsedGlobal, "cleanup_used");
        return (head, used);
    }

    private LLVMValueRef BuildCleanupObjectSlot(LLVMValueRef head, LLVMValueRef used)
    {
        var chunkType = GetCleanupChunkType();
        var objArrayPtr = _builder.BuildStructGEP2(chunkType, head, 0, "cleanup_objs");
        var slotPtr = _builder.BuildGEP2(
            LLVMTypeRef.CreateArray(GetPointerType(GetInt8Type()), CleanupChunkCapacity),
            objArrayPtr,
            new[] { LLVMValueRef.CreateConstInt(GetInt32Type(), 0), used },
            "cleanup_obj_slot");
        return slotPtr;
    }

    private LLVMValueRef BuildCleanupDtorSlot(LLVMValueRef head, LLVMValueRef used)
    {
        var chunkType = GetCleanupChunkType();
        var dtorArrayPtr = _builder.BuildStructGEP2(chunkType, head, 1, "cleanup_dtors");
        var dtorFuncType = LLVMTypeRef.CreateFunction(GetVoidType(), new[] { GetPointerType(GetInt8Type()) });
        var slotPtr = _builder.BuildGEP2(
            LLVMTypeRef.CreateArray(GetPointerType(dtorFuncType), CleanupChunkCapacity),
            dtorArrayPtr,
            new[] { LLVMValueRef.CreateConstInt(GetInt32Type(), 0), used },
            "cleanup_dtor_slot");
        return slotPtr;
    }

    private LLVMValueRef AllocateCleanupChunk()
    {
        var chunkType = GetCleanupChunkType();
        var nullPtr = LLVMValueRef.CreateConstNull(GetPointerType(chunkType));
        var one = LLVMValueRef.CreateConstInt(GetInt32Type(), 1);
        var endPtr = _builder.BuildGEP2(chunkType, nullPtr, new[] { one }, "cleanup_chunk_end");
        var size = _builder.BuildPtrToInt(endPtr, GetInt64Type(), "cleanup_chunk_size");
        var mallocFunc = GetOrAddFunction("malloc", GetPointerType(GetInt8Type()), new[] { GetInt64Type() });
        var chunk = _builder.BuildCall2(_functionTypes["malloc"], mallocFunc, new[] { size }, "cleanup_chunk_alloc");
        return chunk;
    }

    // Pushes a cleanup record for an owning variable. `objectPtr` is the alloca of
    // the variable (pointer to the value). `dtor` is a `void (ptr)` destructor.
    // Returns the chunk and index of the new record.
    private (LLVMValueRef Head, LLVMValueRef Used) BuildPushCleanupRecord(LLVMValueRef objectPtr, LLVMValueRef dtor)
    {
        var (head, used) = BuildCleanupTopLoad();
        var chunkType = GetCleanupChunkType();

        var zero = LLVMValueRef.CreateConstInt(GetInt32Type(), 0);
        var one = LLVMValueRef.CreateConstInt(GetInt32Type(), 1);
        var capacity = LLVMValueRef.CreateConstInt(GetInt32Type(), (uint)CleanupChunkCapacity);

        // Allocate or reuse a chunk if the stack is empty or the current chunk is full.
        var nullChunk = LLVMValueRef.CreateConstNull(GetPointerType(chunkType));
        var headIsNull = _builder.BuildICmp(LLVMIntPredicate.LLVMIntEQ, head, nullChunk, "cleanup_head_null");
        var usedFull = _builder.BuildICmp(LLVMIntPredicate.LLVMIntEQ, used, capacity, "cleanup_used_full");
        var needChunk = _builder.BuildOr(headIsNull, usedFull, "cleanup_need_chunk");

        var function = _builder.InsertBlock.Parent;
        var allocOrReuseBB = _context.AppendBasicBlock(function, "cleanup_alloc_or_reuse");
        var contBB = _context.AppendBasicBlock(function, "cleanup_cont");
        var preheader = _builder.InsertBlock;

        _builder.BuildCondBr(needChunk, allocOrReuseBB, contBB);

        _builder.PositionAtEnd(allocOrReuseBB);
        var freeListHead = _builder.BuildLoad2(GetPointerType(chunkType), _cleanupFreeGlobal, "cleanup_free_head");
        var freeListEmpty = _builder.BuildICmp(LLVMIntPredicate.LLVMIntEQ, freeListHead, nullChunk, "cleanup_free_empty");

        var mallocBB = _context.AppendBasicBlock(function, "cleanup_malloc");
        var freeListReuseBB = _context.AppendBasicBlock(function, "cleanup_free_list_reuse");
        var reuseBB = _context.AppendBasicBlock(function, "cleanup_reuse");
        _builder.BuildCondBr(freeListEmpty, mallocBB, freeListReuseBB);

        _builder.PositionAtEnd(mallocBB);
        var newChunkI8 = AllocateCleanupChunk();
        var newChunkFromMalloc = _builder.BuildBitCast(newChunkI8, GetPointerType(chunkType), "cleanup_new_chunk");
        _builder.BuildBr(reuseBB);

        // Only dereference the free list head's "next" slot once we know it is non-null;
        // freeListHead is null whenever the free list is empty (e.g. the very first cleanup
        // record ever pushed), and unconditionally loading through it would be a null
        // pointer dereference.
        _builder.PositionAtEnd(freeListReuseBB);
        var freeListNextSlot = _builder.BuildStructGEP2(chunkType, freeListHead, 2, "cleanup_free_next");
        var freeListNext = _builder.BuildLoad2(GetPointerType(chunkType), freeListNextSlot, "cleanup_free_next_val");
        _builder.BuildBr(reuseBB);

        _builder.PositionAtEnd(reuseBB);
        var newChunk = _builder.BuildPhi(GetPointerType(chunkType), "cleanup_new_chunk_phi");
        var nextFree = _builder.BuildPhi(GetPointerType(chunkType), "cleanup_next_free");
        newChunk.AddIncoming(new[] { newChunkFromMalloc, freeListHead }, new[] { mallocBB, freeListReuseBB }, 2);
        nextFree.AddIncoming(new[] { nullChunk, freeListNext }, new[] { mallocBB, freeListReuseBB }, 2);
        _builder.BuildStore(nextFree, _cleanupFreeGlobal);

        var nextSlot = _builder.BuildStructGEP2(chunkType, newChunk, 2, "cleanup_next_slot");
        _builder.BuildStore(head, nextSlot);
        _builder.BuildStore(newChunk, _cleanupHeadGlobal);
        _builder.BuildStore(zero, _cleanupUsedGlobal);
        _builder.BuildBr(contBB);

        _builder.PositionAtEnd(contBB);
        var headPhi = _builder.BuildPhi(GetPointerType(chunkType), "cleanup_head_phi");
        var usedPhi = _builder.BuildPhi(GetInt32Type(), "cleanup_used_phi");
        headPhi.AddIncoming(new[] { head, newChunk }, new[] { preheader, reuseBB }, 2);
        usedPhi.AddIncoming(new[] { used, zero }, new[] { preheader, reuseBB }, 2);

        var objPtrI8 = objectPtr;
        if (objectPtr.TypeOf.Kind != LLVMTypeKind.LLVMPointerTypeKind ||
            objectPtr.TypeOf.Handle != GetPointerType(GetInt8Type()).Handle)
        {
            objPtrI8 = _builder.BuildBitCast(objectPtr, GetPointerType(GetInt8Type()), "cleanup_obj");
        }

        var objSlot = BuildCleanupObjectSlot(headPhi, usedPhi);
        var dtorSlot = BuildCleanupDtorSlot(headPhi, usedPhi);
        _builder.BuildStore(objPtrI8, objSlot);
        _builder.BuildStore(dtor, dtorSlot);

        var newUsed = _builder.BuildAdd(usedPhi, one, "cleanup_new_used");
        _builder.BuildStore(newUsed, _cleanupUsedGlobal);

        return (headPhi, usedPhi);
    }

    // Pops and destroys all cleanup records until the stack top reaches
    // (targetHead, targetUsed). Empty non-target chunks are freed as we pass them.
    private void BuildPopCleanupRecordsTo(LLVMValueRef targetHead, LLVMValueRef targetUsed)
    {
        var function = _builder.InsertBlock.Parent;
        var loopBB = _context.AppendBasicBlock(function, "cleanup_loop");
        var endBB = _context.AppendBasicBlock(function, "cleanup_end");
        var preheader = _builder.InsertBlock;

        var (curHead, curUsed) = BuildCleanupTopLoad();

        var headMatch = _builder.BuildICmp(LLVMIntPredicate.LLVMIntEQ, curHead, targetHead, "cleanup_head_match");
        var usedMatch = _builder.BuildICmp(LLVMIntPredicate.LLVMIntEQ, curUsed, targetUsed, "cleanup_used_match");
        var done = _builder.BuildAnd(headMatch, usedMatch, "cleanup_done");
        _builder.BuildCondBr(done, endBB, loopBB);

        _builder.PositionAtEnd(loopBB);
        var headPhi = _builder.BuildPhi(GetPointerType(GetCleanupChunkType()), "cleanup_head");
        var usedPhi = _builder.BuildPhi(GetInt32Type(), "cleanup_used");
        headPhi.AddIncoming(new[] { curHead }, new[] { preheader }, 1);
        usedPhi.AddIncoming(new[] { curUsed }, new[] { preheader }, 1);

        var one = LLVMValueRef.CreateConstInt(GetInt32Type(), 1);
        var zero = LLVMValueRef.CreateConstInt(GetInt32Type(), 0);
        var capacity = LLVMValueRef.CreateConstInt(GetInt32Type(), (uint)CleanupChunkCapacity);

        var newUsed = _builder.BuildSub(usedPhi, one, "cleanup_new_used");

        var objSlot = BuildCleanupObjectSlot(headPhi, newUsed);
        var dtorSlot = BuildCleanupDtorSlot(headPhi, newUsed);
        var obj = _builder.BuildLoad2(GetPointerType(GetInt8Type()), objSlot, "cleanup_obj");
        var dtorType = LLVMTypeRef.CreateFunction(GetVoidType(), new[] { GetPointerType(GetInt8Type()) });
        var dtor = _builder.BuildLoad2(GetPointerType(dtorType), dtorSlot, "cleanup_dtor");
        _builder.BuildCall2(dtorType, dtor, new[] { obj }, "");

        var usedZero = _builder.BuildICmp(LLVMIntPredicate.LLVMIntEQ, newUsed, zero, "cleanup_used_zero");
        var notTarget = _builder.BuildICmp(LLVMIntPredicate.LLVMIntNE, headPhi, targetHead, "cleanup_not_target");
        var shouldMove = _builder.BuildAnd(usedZero, notTarget, "cleanup_should_move");

        var moveBB = _context.AppendBasicBlock(function, "cleanup_move_chunk");
        var contBB = _context.AppendBasicBlock(function, "cleanup_cont");

        _builder.BuildCondBr(shouldMove, moveBB, contBB);

        _builder.PositionAtEnd(moveBB);
        var chunkType = GetCleanupChunkType();
        var nextSlot = _builder.BuildStructGEP2(chunkType, headPhi, 2, "cleanup_next_slot");
        var nextChunk = _builder.BuildLoad2(GetPointerType(chunkType), nextSlot, "cleanup_next_chunk");
        // Move the empty chunk to the free list for reuse instead of freeing it.
        var freeList = _builder.BuildLoad2(GetPointerType(chunkType), _cleanupFreeGlobal, "cleanup_free_list");
        var emptyNextSlot = _builder.BuildStructGEP2(chunkType, headPhi, 2, "cleanup_empty_next_slot");
        _builder.BuildStore(freeList, emptyNextSlot);
        _builder.BuildStore(headPhi, _cleanupFreeGlobal);
        var nextNull = _builder.BuildICmp(LLVMIntPredicate.LLVMIntEQ, nextChunk,
            LLVMValueRef.CreateConstNull(GetPointerType(chunkType)), "cleanup_next_null");
        var usedAfterMove = _builder.BuildSelect(nextNull, zero, capacity, "cleanup_used_after_move");
        _builder.BuildBr(contBB);

        _builder.PositionAtEnd(contBB);
        var headAfter = _builder.BuildPhi(GetPointerType(GetCleanupChunkType()), "cleanup_head_after");
        var usedAfter = _builder.BuildPhi(GetInt32Type(), "cleanup_used_after");
        headAfter.AddIncoming(new[] { headPhi, nextChunk }, new[] { loopBB, moveBB }, 2);
        usedAfter.AddIncoming(new[] { newUsed, usedAfterMove }, new[] { loopBB, moveBB }, 2);

        _builder.BuildStore(headAfter, _cleanupHeadGlobal);
        _builder.BuildStore(usedAfter, _cleanupUsedGlobal);

        var headMatchLoop = _builder.BuildICmp(LLVMIntPredicate.LLVMIntEQ, headAfter, targetHead, "cleanup_head_match_loop");
        var usedMatchLoop = _builder.BuildICmp(LLVMIntPredicate.LLVMIntEQ, usedAfter, targetUsed, "cleanup_used_match_loop");
        var loopDone = _builder.BuildAnd(headMatchLoop, usedMatchLoop, "cleanup_loop_done");
        _builder.BuildCondBr(loopDone, endBB, loopBB);
        headPhi.AddIncoming(new[] { headAfter }, new[] { contBB }, 1);
        usedPhi.AddIncoming(new[] { usedAfter }, new[] { contBB }, 1);

        _builder.PositionAtEnd(endBB);
    }

    // -----------------------------------------------------------------------
    // Destructor generation
    // -----------------------------------------------------------------------
    // For every owning type we lazily emit a `void (ptr)` destructor that knows
    // how to free the heap memory owned by a value of that type. The cleanup
    // stack stores a pointer to the variable's alloca and the matching destructor.

    private LLVMValueRef GetOrCreateDestructor(TypeNode typeNode)
    {
        var key = GetDestructorKey(typeNode);
        if (_destructorFunctions.TryGetValue(key, out var existing)) return existing;

        var ptrType = GetPointerType(GetInt8Type());
        var funcType = LLVMTypeRef.CreateFunction(GetVoidType(), new[] { ptrType });
        var func = _module.AddFunction($"_zv_dtor_{key}", funcType);
        func.Linkage = LLVMLinkage.LLVMInternalLinkage;
        _destructorFunctions[key] = func;

        var entry = _context.AppendBasicBlock(func, "entry");
        var builder = _context.CreateBuilder();
        builder.PositionAtEnd(entry);

        var valuePtr = func.GetParam(0);
        EmitDestructorBody(builder, valuePtr, typeNode);

        builder.BuildRetVoid();
        builder.Dispose();

        return func;
    }

    private bool NeedsDestructor(TypeNode typeNode)
    {
        return typeNode switch
        {
            ArrayTypeNode => true,
            PrimitiveTypeNode p when p.Type.Type is TokenType.CSTRING or TokenType.WSTRING or TokenType.STRING or TokenType.PTR => true,
            UserTypeNode u => IsOwningStructType(u.Name.Lexeme),
            _ => false,
        };
    }

    private string GetDestructorKey(TypeNode typeNode)
    {
        return typeNode switch
        {
            ArrayTypeNode arr => $"arr_{GetDestructorKey(arr.BaseType)}",
            PrimitiveTypeNode p => GetPrimitiveDestructorKey(p.Type.Type),
            UserTypeNode u => $"struct_{u.Name.Lexeme}",
            _ => "unknown",
        };
    }

    private static string GetPrimitiveDestructorKey(TokenType type)
    {
        return type switch
        {
            TokenType.CSTRING => "cstring",
            TokenType.WSTRING => "wstring",
            TokenType.STRING => "string",
            TokenType.PTR => "ptr",
            TokenType.INT8 => "i8",
            TokenType.INT16 => "i16",
            TokenType.INT32 => "i32",
            TokenType.INT64 => "i64",
            TokenType.INT128 => "i128",
            TokenType.UINT8 => "u8",
            TokenType.UINT16 => "u16",
            TokenType.UINT32 => "u32",
            TokenType.UINT64 => "u64",
            TokenType.UINT128 => "u128",
            TokenType.ISIZE => "isize",
            TokenType.USIZE => "usize",
            TokenType.FLOAT32 => "f32",
            TokenType.FLOAT64 => "f64",
            TokenType.BOOL => "bool",
            TokenType.CHAR => "char",
            _ => "unknown",
        };
    }

    private void EmitDestructorBody(LLVMBuilderRef builder, LLVMValueRef valuePtr, TypeNode typeNode)
    {
        switch (typeNode)
        {
            case ArrayTypeNode arr:
                EmitArrayDestructorBody(builder, valuePtr, arr);
                break;
            case PrimitiveTypeNode { Type.Type: TokenType.CSTRING or TokenType.WSTRING or TokenType.PTR }:
                EmitPointerDestructorBody(builder, valuePtr);
                break;
            case PrimitiveTypeNode { Type.Type: TokenType.STRING }:
                EmitStringDestructorBody(builder, valuePtr);
                break;
            case UserTypeNode u when IsOwningStructType(u.Name.Lexeme):
                EmitStructDestructorBody(builder, valuePtr, u.Name.Lexeme);
                break;
        }
    }

    private void EmitPointerDestructorBody(LLVMBuilderRef builder, LLVMValueRef valuePtr)
    {
        var ptrType = GetPointerType(GetInt8Type());
        var ptr = builder.BuildLoad2(ptrType, valuePtr, "ptr_val");
        var freeFunc = GetOrAddFunction("free", GetVoidType(), new[] { ptrType });
        builder.BuildCall2(_functionTypes["free"], freeFunc, new[] { ptr }, "");
        builder.BuildStore(LLVMValueRef.CreateConstNull(ptrType), valuePtr);
    }

    private void EmitStringDestructorBody(LLVMBuilderRef builder, LLVMValueRef valuePtr)
    {
        var ptrType = GetPointerType(GetInt8Type());
        var strType = GetStringStructType();
        var strVal = builder.BuildLoad2(strType, valuePtr, "str_val");
        var dataPtr = builder.BuildExtractValue(strVal, 0, "str_data");
        var freeFunc = GetOrAddFunction("free", GetVoidType(), new[] { ptrType });
        builder.BuildCall2(_functionTypes["free"], freeFunc, new[] { dataPtr }, "");
        builder.BuildStore(LLVMValueRef.CreateConstNull(strType), valuePtr);
    }

    private void EmitArrayDestructorBody(LLVMBuilderRef builder, LLVMValueRef valuePtr, ArrayTypeNode arrType)
    {
        var ptrType = GetPointerType(GetInt8Type());
        var elementType = MapTypeNode(arrType.BaseType);
        var arrayStructType = GetArrayStructType(elementType);

        var arrVal = builder.BuildLoad2(arrayStructType, valuePtr, "arr_val");
        var dataPtr = builder.BuildExtractValue(arrVal, 0, "arr_data");
        var length = builder.BuildExtractValue(arrVal, 1, "arr_len");

        if (NeedsDestructor(arrType.BaseType))
        {
            EmitArrayElementDestructionLoop(builder, dataPtr, length, arrType.BaseType);
        }

        var freeFunc = GetOrAddFunction("free", GetVoidType(), new[] { ptrType });
        builder.BuildCall2(_functionTypes["free"], freeFunc, new[] { dataPtr }, "");
        builder.BuildStore(LLVMValueRef.CreateConstNull(arrayStructType), valuePtr);
    }

    private void EmitArrayElementDestructionLoop(LLVMBuilderRef builder, LLVMValueRef dataPtr, LLVMValueRef length, TypeNode elementType)
    {
        var ptrType = GetPointerType(GetInt8Type());
        var elementLlvmType = MapTypeNode(elementType);
        var dtor = GetOrCreateDestructor(elementType);
        var func = builder.InsertBlock.Parent;
        var preheader = builder.InsertBlock;

        var zero = LLVMValueRef.CreateConstInt(GetInt64Type(), 0);
        var one = LLVMValueRef.CreateConstInt(GetInt64Type(), 1);

        var condBB = _context.AppendBasicBlock(func, "dtor_cond");
        var bodyBB = _context.AppendBasicBlock(func, "dtor_body");
        var endBB = _context.AppendBasicBlock(func, "dtor_end");

        builder.BuildBr(condBB);

        builder.PositionAtEnd(condBB);
        var iPhi = builder.BuildPhi(GetInt64Type(), "dtor_i");
        iPhi.AddIncoming(new[] { zero }, new[] { preheader }, 1);
        var done = builder.BuildICmp(LLVMIntPredicate.LLVMIntUGE, iPhi, length, "dtor_done");
        builder.BuildCondBr(done, endBB, bodyBB);

        builder.PositionAtEnd(bodyBB);
        var elemPtr = builder.BuildGEP2(elementLlvmType, dataPtr, new[] { iPhi }, "dtor_elem");
        var elemPtrAsI8 = builder.BuildBitCast(elemPtr, ptrType, "dtor_elem_i8");
        builder.BuildCall2(LLVMTypeRef.CreateFunction(GetVoidType(), new[] { ptrType }), dtor, new[] { elemPtrAsI8 }, "");
        var iNext = builder.BuildAdd(iPhi, one, "dtor_i_next");
        var bodyBlock = builder.InsertBlock;
        builder.BuildBr(condBB);
        iPhi.AddIncoming(new[] { iNext }, new[] { bodyBlock }, 1);

        builder.PositionAtEnd(endBB);
    }

    private void EmitStructDestructorBody(LLVMBuilderRef builder, LLVMValueRef structPtr, string structName)
    {
        var fieldTypeNodes = _structFieldTypeNodes[structName];
        var fieldTypes = _structFieldTypes[structName];
        var structType = _structTypes[structName];

        for (int i = 0; i < fieldTypeNodes.Count; i++)
        {
            var fieldTypeNode = fieldTypeNodes[i];
            if (!IsOwningFieldTypeNode(fieldTypeNode)) continue;

            var fieldPtr = builder.BuildStructGEP2(structType, structPtr, (uint)i, "sd_field_ptr");
            if (fieldTypeNode is UserTypeNode nestedUser && IsOwningStructType(nestedUser.Name.Lexeme))
            {
                EmitStructDestructorBody(builder, fieldPtr, nestedUser.Name.Lexeme);
            }
            else
            {
                var dtor = GetOrCreateDestructor(fieldTypeNode);
                var fieldPtrAsI8 = builder.BuildBitCast(fieldPtr, GetPointerType(GetInt8Type()), "sd_field_i8");
                builder.BuildCall2(LLVMTypeRef.CreateFunction(GetVoidType(), new[] { GetPointerType(GetInt8Type()) }),
                    dtor, new[] { fieldPtrAsI8 }, "");
            }
        }

        builder.BuildStore(LLVMValueRef.CreateConstNull(structType), structPtr);
    }
}
