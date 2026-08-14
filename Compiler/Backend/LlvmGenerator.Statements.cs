using System;
using System.Collections.Generic;
using System.Linq;
using LLVMSharp.Interop;
using ZV.Compiler.AST;
using ZV.Compiler.Lexer;

namespace ZV.Compiler.Backend;

public partial class LlvmGenerator
{
    private void VisitFunctionDecl(FunctionDeclStmt stmt)
    {
        var paramTypes = new List<LLVMTypeRef>();
        if (stmt.IsEntry)
        {
            // Standard main signature: int main(int argc, char** argv)
            paramTypes.Add(GetInt32Type()); // argc
            paramTypes.Add(GetPointerType(GetPointerType(GetInt8Type()))); // argv
        }
        else
        {
            foreach (var param in stmt.Parameters)
            {
                var mappedType = MapTypeNode(param.Type);
                // Fixed-size arrays are passed by reference (pointer to the array),
                // then copied into a local stack allocation inside the callee.
                if (param.Type is FixedSizeArrayTypeNode)
                {
                    paramTypes.Add(GetPointerType(mappedType));
                }
                else
                {
                    paramTypes.Add(mappedType);
                }
            }
        }

        if (stmt.ReturnType is FixedSizeArrayTypeNode)
        {
            throw new CompileException(stmt.Location, "Functions cannot return fixed-size arrays. Use a dynamic array (T[]) or a pointer instead.");
        }

        var returnType = (stmt.ReturnType is PrimitiveTypeNode p && p.Type.Type == TokenType.VOID) 
            ? GetVoidType() 
            : MapTypeNode(stmt.ReturnType);

        string functionName = stmt.IsEntry ? "main" : stmt.Name.Lexeme;

        _functionParamNewtypes[stmt.Name.Lexeme] = stmt.Parameters.Select(p => GetDeclaredNewtypeName(p.Type)).ToList();
        _functionReturnNewtype[stmt.Name.Lexeme] = GetDeclaredNewtypeName(stmt.ReturnType);
        _functionReturnTypeNodes[stmt.Name.Lexeme] = stmt.ReturnType;

        var funcType = LLVMTypeRef.CreateFunction(returnType, paramTypes.ToArray());
        _functionTypes[stmt.Name.Lexeme] = funcType;
        var function = _module.AddFunction(functionName, funcType);
        _functionValues[stmt.Name.Lexeme] = function;

        if (stmt.IsEntry)
        {
            _entryFunction = function;
            _entryFunctionType = funcType;
        }

        if (IsLibraryTarget && !stmt.IsEntry)
        {
            if (stmt.IsExported)
            {
                function.Linkage = LLVMLinkage.LLVMExternalLinkage;
                function.DLLStorageClass = LLVMDLLStorageClass.LLVMDLLExportStorageClass;
            }
            else
            {
                function.Linkage = LLVMLinkage.LLVMInternalLinkage;
            }
        }

        var entry = _context.AppendBasicBlock(function, "entry");
        var savedBuilderBlock = _builder.InsertBlock;
        _builder.PositionAtEnd(entry);

        // Ownership and lifetime state is function-local.
        _ownedVariables.Clear();
        _deadVariables.Clear();
        _variableDeclScope.Clear();
        _scopes.Clear();

        // We don't restore _namedValues, we want function variables to stay during body generation.
        // But we should scope them if we had nested functions. ZV doesn't seem to have them yet.
        
        if (stmt.IsEntry)
        {
            // argc/argv are always available as the underlying LLVM function's parameters,
            // regardless of whether the ZV entry function declares a CSTRING[] parameter -
            // respawn() needs them from entry onward either way (see LlvmGenerator.Process.cs).
            var entryArgcValue = function.GetParam(0);
            var entryArgvValue = function.GetParam(1);
            var respawnIsChild = SetupRespawnEntryState(entryArgcValue, entryArgvValue);

            // For main, we map the ZV parameter (if any) to argv
            if (stmt.Parameters.Count > 0)
            {
                var paramName = stmt.Parameters[0].Name.Lexeme;
                var argcValue = entryArgcValue;
                var argvValue = entryArgvValue;
                argvValue.Name = paramName + "_raw";

                // In ZV, the parameter is CSTRING[] which maps to a struct { i8**, i64 }
                var arrayStructType = GetArrayStructType(GetPointerType(GetInt8Type()));
                
                // Construct the struct
                var structAlloca = _builder.BuildAlloca(arrayStructType, paramName + ".struct");
                var dataFieldPtr = _builder.BuildStructGEP2(arrayStructType, structAlloca, 0, "data");
                var lengthFieldPtr = _builder.BuildStructGEP2(arrayStructType, structAlloca, 1, "len");
                
                _builder.BuildStore(argvValue, dataFieldPtr);
                // Convert argc to i64 for the length field, hiding respawn()'s internal
                // marker argument from user-visible code when this process is a
                // respawned child.
                var argc64 = _builder.BuildSExt(argcValue, GetInt64Type(), "argc64");
                var argc64MinusOne = _builder.BuildSub(argc64, LLVMValueRef.CreateConstInt(GetInt64Type(), 1), "argc64_m1");
                var visibleArgc64 = _builder.BuildSelect(respawnIsChild, argc64MinusOne, argc64, "argc64_visible");
                _builder.BuildStore(visibleArgc64, lengthFieldPtr);
                
                // Track the alloca of the struct
                var alloca = _builder.BuildAlloca(arrayStructType, paramName + ".addr");
                _builder.BuildStore(_builder.BuildLoad2(arrayStructType, structAlloca, "tmp"), alloca);
                _namedValues[paramName] = (alloca, arrayStructType, null);
                _variableDeclScope[paramName] = _scopes.Count;
            }
        }
        else
        {
            for (int i = 0; i < stmt.Parameters.Count; i++)
            {
                var paramName = stmt.Parameters[i].Name.Lexeme;
                var paramValue = function.GetParam((uint)i);
                paramValue.Name = paramName;

                // Allocate space on stack for the parameter
                var type = MapTypeNode(stmt.Parameters[i].Type);
                string? structName = GetStructNameForTypeNode(stmt.Parameters[i].Type);

                LLVMValueRef alloca;
                if (stmt.Parameters[i].Type is FixedSizeArrayTypeNode)
                {
                    // Fixed-size arrays are passed by reference; copy into a local allocation
                    // so indexing and bounds-checking inside the function work the same way
                    // as for a local fixed-size array.
                    alloca = _builder.BuildAlloca(type, paramName + ".addr");
                    var destPtr = _builder.BuildBitCast(alloca, GetPointerType(GetInt8Type()), "param_copy_dest");
                    var srcPtr = _builder.BuildBitCast(paramValue, GetPointerType(GetInt8Type()), "param_copy_src");
                    var sizeBytes = LLVMValueRef.CreateConstInt(GetInt64Type(), GetTypeSizeInBytes(type));
                    var memmove = GetOrAddFunction("memmove", GetPointerType(GetInt8Type()),
                        new[] { GetPointerType(GetInt8Type()), GetPointerType(GetInt8Type()), GetInt64Type() });
                    _builder.BuildCall2(_functionTypes["memmove"], memmove,
                        new[] { destPtr, srcPtr, sizeBytes }, "");
                }
                else
                {
                    alloca = _builder.BuildAlloca(type, paramName + ".addr");
                    _builder.BuildStore(paramValue, alloca);
                }
                _namedValues[paramName] = (alloca, type, structName);
                _variableDeclScope[paramName] = _scopes.Count;
                _variableNewtypeNames[paramName] = GetDeclaredNewtypeName(stmt.Parameters[i].Type);
                _variableDeclaredTypeNodes[paramName] = stmt.Parameters[i].Type;
            }
        }

        var previousReturnNewtype = _currentFunctionReturnNewtype;
        _currentFunctionReturnNewtype = GetDeclaredNewtypeName(stmt.ReturnType);

        _inFunctionBody = true;
        VisitStatement(stmt.Body);
        _inFunctionBody = false;

        _currentFunctionReturnNewtype = previousReturnNewtype;

        // Ensure a return if void and block not terminated
        if (returnType == GetVoidType())
        {
            var lastInst = _builder.InsertBlock.LastInstruction;
            if (lastInst.Handle == IntPtr.Zero || lastInst.IsATerminatorInst.Handle == IntPtr.Zero)
            {
                _builder.BuildRetVoid();
            }
        }
        
        if (savedBuilderBlock.Handle != IntPtr.Zero)
        {
            _builder.PositionAtEnd(savedBuilderBlock);
        }
    }

    private void VisitIf(IfStmt stmt)
    {
        int cstrMark = _pendingCstrTemps.Count;
        var condition = VisitExpression(stmt.Condition);
        FreeUnclaimedCstrTemps(cstrMark);

        // Convert to i1 if it's not already
        if (condition.TypeOf != GetInt1Type())
        {
            condition = _builder.BuildICmp(LLVMIntPredicate.LLVMIntNE, condition, LLVMValueRef.CreateConstInt(condition.TypeOf, 0), "ifcond");
        }

        var function = _builder.InsertBlock.Parent;
        var thenBB = _context.AppendBasicBlock(function, "then");
        var elseBB = _context.AppendBasicBlock(function, "else");
        var mergeBB = _context.AppendBasicBlock(function, "ifcont");

        _builder.BuildCondBr(condition, thenBB, elseBB);

        // Ownership/lifetime state (_deadVariables / _ownedVariables) is flow-sensitive
        // across the two branches: each branch is generated against its own copy of the
        // pre-if state, and the results are merged afterward (see below), instead of one
        // branch's free()/move() calls leaking into the other.
        var deadBeforeIf = new HashSet<string>(_deadVariables);
        var ownedBeforeIf = new HashSet<string>(_ownedVariables);

        // Then branch
        _builder.PositionAtEnd(thenBB);
        VisitStatement(stmt.ThenBranch);
        bool thenTerminated = _builder.InsertBlock.LastInstruction.Handle != IntPtr.Zero && _builder.InsertBlock.LastInstruction.IsATerminatorInst.Handle != IntPtr.Zero;
        if (!thenTerminated)
        {
            _builder.BuildBr(mergeBB);
        }
        var deadAfterThen = _deadVariables;
        var ownedAfterThen = _ownedVariables;

        // Else branch, generated against the pre-if state (not whatever the then branch left behind).
        _deadVariables = new HashSet<string>(deadBeforeIf);
        _ownedVariables = new HashSet<string>(ownedBeforeIf);
        _builder.PositionAtEnd(elseBB);
        if (stmt.ElseBranch != null)
        {
            VisitStatement(stmt.ElseBranch);
        }
        bool elseTerminated = _builder.InsertBlock.LastInstruction.Handle != IntPtr.Zero && _builder.InsertBlock.LastInstruction.IsATerminatorInst.Handle != IntPtr.Zero;
        if (!elseTerminated)
        {
            _builder.BuildBr(mergeBB);
        }
        var deadAfterElse = _deadVariables;
        var ownedAfterElse = _ownedVariables;

        // Merge: a branch that terminated (return/break/continue/throw) never reaches the
        // merge point, so its state shouldn't be considered when both branches are live.
        // A variable is dead after the if if either surviving branch could have killed it
        // (conservative, rejects use-after-free/move on any path); it's still owned only if
        // every surviving branch agrees it's still owned (otherwise cleanup at scope-exit
        // could double-free on one path or leak on the other).
        if (thenTerminated && elseTerminated)
        {
            _deadVariables = deadBeforeIf;
            _ownedVariables = ownedBeforeIf;
        }
        else if (thenTerminated)
        {
            _deadVariables = deadAfterElse;
            _ownedVariables = ownedAfterElse;
        }
        else if (elseTerminated)
        {
            _deadVariables = deadAfterThen;
            _ownedVariables = ownedAfterThen;
        }
        else
        {
            _deadVariables = new HashSet<string>(deadAfterThen);
            _deadVariables.UnionWith(deadAfterElse);
            _ownedVariables = new HashSet<string>(ownedAfterThen);
            _ownedVariables.IntersectWith(ownedAfterElse);
        }

        // Merge block
        _builder.PositionAtEnd(mergeBB);
    }

    private void VisitWhile(WhileStmt stmt)
    {
        var function = _builder.InsertBlock.Parent;
        var condBB = _context.AppendBasicBlock(function, "whilecond");
        var bodyBB = _context.AppendBasicBlock(function, "whilebody");
        var endBB = _context.AppendBasicBlock(function, "whileend");

        _loopTargets.Push((endBB, condBB));
        EnterScope();
        _loopStartScopeDepths.Add(_scopes.Count - 1);

        _builder.BuildBr(condBB);

        // Condition block
        _builder.PositionAtEnd(condBB);
        int cstrMark = _pendingCstrTemps.Count;
        var condition = VisitExpression(stmt.Condition);
        FreeUnclaimedCstrTemps(cstrMark);
        if (condition.TypeOf != GetInt1Type())
        {
            condition = _builder.BuildICmp(LLVMIntPredicate.LLVMIntNE, condition, LLVMValueRef.CreateConstInt(condition.TypeOf, 0), "whilecondbool");
        }
        _builder.BuildCondBr(condition, bodyBB, endBB);

        // Body block
        _builder.PositionAtEnd(bodyBB);
        VisitStatement(stmt.Body);
        if (_builder.InsertBlock.LastInstruction.Handle == IntPtr.Zero || _builder.InsertBlock.LastInstruction.IsATerminatorInst.Handle == IntPtr.Zero)
        {
            _builder.BuildBr(condBB);
        }

        // End block
        _builder.PositionAtEnd(endBB);

        LeaveScope();
        _loopStartScopeDepths.RemoveAt(_loopStartScopeDepths.Count - 1);
        _loopTargets.Pop();
    }

    private void VisitFor(ForStmt stmt)
    {
        var function = _builder.InsertBlock.Parent;

        if (stmt.Initializer != null)
        {
            VisitStatement(stmt.Initializer);
        }

        var condBB = _context.AppendBasicBlock(function, "forcond");
        var bodyBB = _context.AppendBasicBlock(function, "forbody");
        var incBB = _context.AppendBasicBlock(function, "forinc");
        var endBB = _context.AppendBasicBlock(function, "forend");

        _loopTargets.Push((endBB, incBB));
        EnterScope();
        _loopStartScopeDepths.Add(_scopes.Count - 1);

        _builder.BuildBr(condBB);

        // Condition
        _builder.PositionAtEnd(condBB);
        if (stmt.Condition != null)
        {
            int cstrMark = _pendingCstrTemps.Count;
            var condition = VisitExpression(stmt.Condition);
            FreeUnclaimedCstrTemps(cstrMark);
            if (condition.TypeOf != GetInt1Type())
            {
                condition = _builder.BuildICmp(LLVMIntPredicate.LLVMIntNE, condition, LLVMValueRef.CreateConstInt(condition.TypeOf, 0), "forcondbool");
            }
            _builder.BuildCondBr(condition, bodyBB, endBB);
        }
        else
        {
            _builder.BuildBr(bodyBB);
        }

        // Body
        _builder.PositionAtEnd(bodyBB);
        VisitStatement(stmt.Body);
        if (_builder.InsertBlock.LastInstruction.Handle == IntPtr.Zero || _builder.InsertBlock.LastInstruction.IsATerminatorInst.Handle == IntPtr.Zero)
        {
            _builder.BuildBr(incBB);
        }

        // Increment
        _builder.PositionAtEnd(incBB);
        if (stmt.Increment != null)
        {
            int incCstrMark = _pendingCstrTemps.Count;
            VisitExpression(stmt.Increment);
            FreeUnclaimedCstrTemps(incCstrMark);
        }
        _builder.BuildBr(condBB);

        // End
        _builder.PositionAtEnd(endBB);

        LeaveScope();
        _loopStartScopeDepths.RemoveAt(_loopStartScopeDepths.Count - 1);
        _loopTargets.Pop();
    }

    private void VisitStructDecl(StructDeclStmt stmt)
    {
        var fieldTypes = new List<LLVMTypeRef>();
        var fieldNames = new List<string>();
        foreach (var field in stmt.Fields)
        {
            fieldTypes.Add(MapTypeNode(field.Type));
            fieldNames.Add(field.Name.Lexeme);
        }

        var structType = _context.CreateNamedStruct(stmt.Name.Lexeme);
        structType.StructSetBody(fieldTypes.ToArray(), stmt.IsPacked);
        
        _structTypes[stmt.Name.Lexeme] = structType;
        _structFieldNames[stmt.Name.Lexeme] = fieldNames;
        _structFieldTypes[stmt.Name.Lexeme] = fieldTypes;
        _structFieldTypeNodes[stmt.Name.Lexeme] = stmt.Fields.Select(f => f.Type).ToList();

        // A struct transitively owns heap memory if any field is a dynamic array (T[]), a
        // CSTRING, or another owning struct (structs must be declared before use, so nested
        // struct fields are already classified by the time we get here).
        if (stmt.Fields.Any(f => IsOwningFieldTypeNode(f.Type)))
        {
            _owningStructTypes.Add(stmt.Name.Lexeme);
        }
    }

    private void VisitFree(FreeStmt stmt)
    {
        foreach (var valExpr in stmt.Values)
        {
            FreeExpression(valExpr);
        }
    }

    private void VisitVarDecl(VarDeclStmt stmt)
    {
        // A fresh declaration revives the name, in case it was previously freed or moved.
        _deadVariables.Remove(stmt.Name.Lexeme);

        // Track the scope in which this variable is declared so cleanup is scheduled
        // at the correct scope end (the declaration scope, not an inner block).
        _variableDeclScope[stmt.Name.Lexeme] = _scopes.Count - 1;

        var type = MapTypeNode(stmt.Type);
        string? structName = GetStructNameForTypeNode(stmt.Type);
        var declaredNewtype = GetDeclaredNewtypeName(stmt.Type);
        _variableNewtypeNames[stmt.Name.Lexeme] = declaredNewtype;
        _variableDeclaredTypeNodes[stmt.Name.Lexeme] = stmt.Type;
        if (stmt.Initializer != null)
        {
            CheckNewtypeAssignable(declaredNewtype, stmt.Initializer, stmt.Location);
        }

        if (stmt.IsConst)
        {
            _constVariables.Add(stmt.Name.Lexeme);
        }

        // Fixed-size stack arrays (T[N]) need special initialization and lifetime handling.
        if (stmt.Type is FixedSizeArrayTypeNode fixedArrayType)
        {
            VisitFixedSizeArrayDecl(stmt, fixedArrayType, type);
            return;
        }


        // Detect whether we're inside a function (builder has an insertion block)
        var insertBlock = _builder.InsertBlock;
        if (insertBlock.Handle == IntPtr.Zero)
        {
            // Module-scope variable: emit as a global instead of a stack allocation
            var global = _module.AddGlobal(type, stmt.Name.Lexeme);

            if (stmt.IsConst)
            {
                global.IsGlobalConstant = true;
            }

            if (stmt.Initializer != null)
            {
                // Try to evaluate the initializer as a constant
                var value = VisitExpression(stmt.Initializer);
                if (value.IsConstant)
                {
                    if (value.TypeOf.Handle == type.Handle) { global.Initializer = value; } else { global.Initializer = LLVMValueRef.CreateConstNull(type); }
                }
                else
                {
                    global.Initializer = LLVMValueRef.CreateConstNull(type);
                }
            }
            else
            {
                global.Initializer = LLVMValueRef.CreateConstNull(type);
            }

            // Track it in named values so later references can find it.
            _namedValues[stmt.Name.Lexeme] = (global, type, structName);
            return;
        }

        // Function-scope variable: allocate on the stack
        var alloca = _builder.BuildAlloca(type, stmt.Name.Lexeme);
        _namedValues[stmt.Name.Lexeme] = (alloca, type, structName);

        if (stmt.Initializer == null)
        {
            // Zero-initialize struct/STRING locals declared without an initializer (matches
            // the zero-initialization already done for module-scope globals). This matters
            // for owning structs in particular: an owning field left as uninitialized stack
            // garbage would crash when the compiler later frees it at scope exit, whereas a
            // null pointer / zero length is always safe to free (free(NULL) is a no-op).
            if (type.Kind == LLVMTypeKind.LLVMStructTypeKind)
            {
                _builder.BuildStore(LLVMValueRef.CreateConstNull(type), alloca);
            }

            if (IsOwningStructType(structName))
            {
                AddOwnedVariable(stmt.Name.Lexeme);
            }
            return;
        }

        {
            int cstrTempMark = _pendingCstrTemps.Count;
            LLVMValueRef value;
            if (stmt.Initializer is StructInitExpr structInit && structName != null)
            {
                value = VisitStructInit(structInit, structName);
            }
            else if (IsOwnedFieldAccess(stmt.Initializer))
            {
                // Moving an owning field out of an owned struct zeros the source field and
                // transfers ownership to the new variable.
                value = VisitMove(stmt.Initializer);
            }
            else
            {
                value = VisitExpression(stmt.Initializer);
            }
            
            // If it's a struct initializer, we might need to handle it differently
            // but for now BuildStore should work if types match.
            value = ConvertToType(value, type);
            _builder.BuildStore(value, alloca);

            // cstr()/wstr() only allocate a fresh temporary when converting a STRING; detect
            // that precisely by checking whether evaluating the initializer pushed a new pending
            // temp. A bare CSTRING/WSTRING passthrough pushes nothing and stays non-owning.
            bool ownsCstrTemp = _pendingCstrTemps.Count > cstrTempMark;
            bool initializerIsDirectCstrOrWstr = stmt.Initializer is CallExpr
            {
                Callee: VariableExpr { Name: "cstr" or "wstr" }
            };

            // Only claim the cstr/wstr allocation if the variable is directly receiving it.
            // If the cstr()/wstr() temp was used as an argument to a larger expression (e.g.
            // an extern function call whose return value is stored in this variable), leave it
            // pending so it is freed at the end of the statement.
            if (ownsCstrTemp && initializerIsDirectCstrOrWstr)
            {
                ClaimCstrTemps(cstrTempMark);
            }

            // Binding an existing owned variable or owning field to a new variable is an
            // implicit move(); no shallow alias of owned memory is ever created.
            bool transferredOwnership = TryTransferOwnership(stmt.Initializer);
            bool movedOwningField = IsOwnedFieldAccess(stmt.Initializer);

            if (transferredOwnership || movedOwningField ||
                (ownsCstrTemp && initializerIsDirectCstrOrWstr) ||
                IsOwnedExpression(stmt.Initializer, value) || IsOwningStructConstruction(stmt.Initializer, structName))
            {
                AddOwnedVariable(stmt.Name.Lexeme);
            }
        }
    }

    private void VisitFixedSizeArrayDecl(VarDeclStmt stmt, FixedSizeArrayTypeNode fixedArrayType, LLVMTypeRef arrayType)
    {

        var elementType = MapTypeNode(fixedArrayType.BaseType);
        int length = EvaluateConstantSize(fixedArrayType.Size);

        if (stmt.Initializer is ArrayInitExpr arrayInit)
        {
            CheckFixedArrayInitShape(arrayType, arrayInit, stmt.Location);
        }

        var insertBlock = _builder.InsertBlock;
        if (insertBlock.Handle == IntPtr.Zero)
        {
            // Module-scope fixed-size array: emit as a global.
            var global = _module.AddGlobal(arrayType, stmt.Name.Lexeme);
            if (stmt.IsConst)
            {
                global.IsGlobalConstant = true;
            }

            global.Initializer = BuildFixedArrayInitializer(elementType, length, stmt.Initializer);
            _namedValues[stmt.Name.Lexeme] = (global, arrayType, null);
            return;
        }

        // Function-scope fixed-size array: allocate on the stack as [N x T].
        var alloca = _builder.BuildAlloca(arrayType, stmt.Name.Lexeme);
        _namedValues[stmt.Name.Lexeme] = (alloca, arrayType, null);

        if (stmt.Initializer is ArrayInitExpr initExpr)
        {
            StoreFixedArrayElements(arrayType, alloca, initExpr);
        }
        else if (stmt.Initializer != null &&
                 TryGetFixedArrayInfo(stmt.Initializer, out var srcArrayType, out var srcArrayPtr) &&
                 srcArrayType.Handle == arrayType.Handle)
        {
            EmitFixedArrayCopy(alloca, srcArrayPtr, arrayType);
        }
        else
        {
            LLVMValueRef? fillValue = null;
            if (stmt.Initializer != null)
            {
                fillValue = VisitExpression(stmt.Initializer);
            }
            var lengthValue = LLVMValueRef.CreateConstInt(GetInt64Type(), (ulong)length);
            var dataPtr = _builder.BuildBitCast(alloca, GetPointerType(elementType), "arraydata");
            if (arrayType.ElementType.Kind == LLVMTypeKind.LLVMArrayTypeKind)
            {
                var (innerType, totalCount) = GetFlattenedArrayInfo(arrayType);
                var totalLengthValue = LLVMValueRef.CreateConstInt(GetInt64Type(), (ulong)totalCount);
                var flatPtr = _builder.BuildBitCast(alloca, GetPointerType(innerType), "arrayflatdata");
                if (fillValue != null) fillValue = ConvertToType(fillValue.Value, innerType);
                BuildArrayFillLoop(innerType, flatPtr, totalLengthValue, fillValue);
            }
            else
            {
                if (fillValue != null) fillValue = ConvertToType(fillValue.Value, elementType);
                BuildArrayFillLoop(elementType, dataPtr, lengthValue, fillValue);
            }
        }
    }

    // Recursively validates that a (possibly nested, for multidimensional T[N][M]...)
    // array initializer's shape fits within the declared array type's dimensions. Missing
    // elements are zero-filled, but too many elements is an error.
    private void CheckFixedArrayInitShape(LLVMTypeRef arrayType, ArrayInitExpr arrayInit, SourceLocation location)
    {
        int length = (int)arrayType.ArrayLength;
        if (arrayInit.Elements.Count > length)
        {
            throw new CompileException(location, $"Fixed-size array of length {length} expects at most {length} initializer elements, got {arrayInit.Elements.Count}.");
        }

        var elementType = arrayType.ElementType;
        if (elementType.Kind != LLVMTypeKind.LLVMArrayTypeKind) return;

        foreach (var element in arrayInit.Elements)
        {
            if (element is not ArrayInitExpr nestedInit)
            {
                throw new CompileException(element.Location, "Expected a nested array initializer for a multidimensional fixed-size array.");
            }
            CheckFixedArrayInitShape(elementType, nestedInit, element.Location);
        }
    }

    // Stores each element of a (possibly nested, possibly partial) array initializer into
    // `arrayPtr`, recursing into inner dimensions for multidimensional fixed-size arrays
    // (T[N][M]). Missing elements are zero-initialized.
    private void StoreFixedArrayElements(LLVMTypeRef arrayType, LLVMValueRef arrayPtr, ArrayInitExpr arrayInit)
    {
        var elementType = arrayType.ElementType;
        var zero = LLVMValueRef.CreateConstInt(GetInt32Type(), 0);
        int length = (int)arrayType.ArrayLength;

        for (int i = 0; i < length; i++)
        {
            var index = LLVMValueRef.CreateConstInt(GetInt32Type(), (ulong)i);
            var ptr = _builder.BuildGEP2(arrayType, arrayPtr, new[] { zero, index }, "arrayinitptr");

            if (i < arrayInit.Elements.Count)
            {
                if (elementType.Kind == LLVMTypeKind.LLVMArrayTypeKind && arrayInit.Elements[i] is ArrayInitExpr nestedInit)
                {
                    StoreFixedArrayElements(elementType, ptr, nestedInit);
                }
                else
                {
                    var value = VisitExpression(arrayInit.Elements[i]);
                    value = ConvertToType(value, elementType);
                    _builder.BuildStore(value, ptr);
                }
            }
            else
            {
                _builder.BuildStore(LLVMValueRef.CreateConstNull(elementType), ptr);
            }
        }
    }

    private LLVMValueRef BuildFixedArrayInitializer(LLVMTypeRef elementType, int length, Expression? initializer)
    {
        if (initializer is ArrayInitExpr arrayInit)
        {
            var values = new LLVMValueRef[length];
            int provided = arrayInit.Elements.Count;
            for (int i = 0; i < length; i++)
            {
                LLVMValueRef val;
                if (i < provided)
                {
                    if (elementType.Kind == LLVMTypeKind.LLVMArrayTypeKind && arrayInit.Elements[i] is ArrayInitExpr nestedInit)
                    {
                        val = BuildFixedArrayInitializer(elementType.ElementType, (int)elementType.ArrayLength, nestedInit);
                    }
                    else
                    {
                        val = VisitExpression(arrayInit.Elements[i]);
                        val = ConvertToType(val, elementType);
                        if (!val.IsConstant)
                        {
                            throw new CompileException(arrayInit.Location, "Global fixed-size array initializer must be constant.");
                        }
                    }
                }
                else
                {
                    val = LLVMValueRef.CreateConstNull(elementType);
                }
                values[i] = val;
            }
            return LLVMValueRef.CreateConstArray(elementType, values);
        }

        if (elementType.Kind == LLVMTypeKind.LLVMArrayTypeKind)
        {
            // Scalar fill for nested arrays: propagate the fill value into every inner array.
            int innerLength = (int)elementType.ArrayLength;
            var innerInit = BuildFixedArrayInitializer(elementType.ElementType, innerLength, initializer);
            var nestedRepeated = new LLVMValueRef[length];
            for (int i = 0; i < length; i++) nestedRepeated[i] = innerInit;
            return LLVMValueRef.CreateConstArray(elementType, nestedRepeated);
        }

        LLVMValueRef fillValue;
        if (initializer != null)
        {
            fillValue = VisitExpression(initializer);
            fillValue = ConvertToType(fillValue, elementType);
            if (!fillValue.IsConstant)
            {
                throw new CompileException(initializer.Location, "Global fixed-size array initializer must be constant.");
            }
        }
        else
        {
            fillValue = LLVMValueRef.CreateConstNull(elementType);
        }

        var repeated = new LLVMValueRef[length];
        for (int i = 0; i < length; i++) repeated[i] = fillValue;
        return LLVMValueRef.CreateConstArray(elementType, repeated);
    }

    private void VisitUnsafe(UnsafeStmt stmt)
    {
        _unsafeDepth++;
        try
        {
            VisitStatement(stmt.Body);
        }
        finally
        {
            _unsafeDepth--;
        }
    }

    private void VisitBreak(BreakStmt stmt)
    {
        if (_loopTargets.Count == 0)
            throw new Exception("'break' outside of loop.");

        CleanupToDepth(_loopStartScopeDepths[_loopStartScopeDepths.Count - 1]);
        _builder.BuildBr(_loopTargets.Peek().EndBlock);
    }

    private void VisitContinue(ContinueStmt stmt)
    {
        if (_loopTargets.Count == 0)
            throw new Exception("'continue' outside of loop.");

        CleanupToDepth(_loopStartScopeDepths[_loopStartScopeDepths.Count - 1]);
        _builder.BuildBr(_loopTargets.Peek().ContinueBlock);
    }

    private void VisitReturn(ReturnStmt stmt)
    {
        string? returnedVariable = null;
        if (stmt.Value != null)
        {
            // Returning a fixed-size stack array would return a pointer to dead stack space.
            if (stmt.Value is VariableExpr varExpr &&
                _namedValues.TryGetValue(varExpr.Name, out var entry) &&
                entry.Type.Kind == LLVMTypeKind.LLVMArrayTypeKind)
            {
                throw new CompileException(stmt.Location, "Cannot return a fixed-size stack array; its lifetime ends when the function returns.");
            }

            CheckNewtypeAssignable(_currentFunctionReturnNewtype, stmt.Value, stmt.Location);

            var value = VisitExpression(stmt.Value);

            // If we are returning an owned variable, transfer its ownership to the caller.
            // Zero the variable's storage before cleanup so the cleanup stack destructor
            // becomes a no-op and the heap memory survives the return.
            if (stmt.Value is VariableExpr retVar && _ownedVariables.Contains(retVar.Name))
            {
                ZeroOwnedVariable(retVar.Name);
                _ownedVariables.Remove(retVar.Name);
                returnedVariable = retVar.Name;
            }

            CleanupAllOpenScopes(returnedVariable);
            _builder.BuildRet(value);
        }
        else
        {
            CleanupAllOpenScopes();
            _builder.BuildRetVoid();
        }
    }

    private void VisitExternDecl(ExternDeclStmt stmt)
    {
        string libName = (string)stmt.LibraryName.Literal!;
        if (!string.IsNullOrEmpty(libName))
        {
            _externalLibraries.Add(libName);
        }

        // For now, we just visit all functions.
        // The library name is currently not passed to LLVM directly as metadata
        // because LLVM doesn't have a standard way to embed DLL dependencies in IR.
        // It's usually handled at link time.
        foreach (var func in stmt.Functions)
        {
            VisitExternFunctionDecl(func);
        }
    }

    private void VisitExternFunctionDecl(ExternFunctionDecl stmt)
    {
        var paramTypes = new List<LLVMTypeRef>();
        foreach (var param in stmt.Parameters)
        {
            paramTypes.Add(MapTypeNode(param.Type));
        }

        var returnType = (stmt.ReturnType is PrimitiveTypeNode p && p.Type.Type == TokenType.VOID) 
            ? GetVoidType() 
            : MapTypeNode(stmt.ReturnType);
        var funcType = LLVMTypeRef.CreateFunction(returnType, paramTypes.ToArray());
        
        string nativeName = stmt.NativeSymbol != null 
            ? (string)stmt.NativeSymbol.Literal! 
            : stmt.Name.Lexeme;

        _functionTypes[stmt.Name.Lexeme] = funcType;
        var function = _module.AddFunction(nativeName, funcType);
        _functionValues[stmt.Name.Lexeme] = function;

        // Track newtype identity for extern functions the same way as regular functions,
        // so callers can assign the return value to a newtype-typed variable directly.
        _functionParamNewtypes[stmt.Name.Lexeme] = stmt.Parameters.Select(p => GetDeclaredNewtypeName(p.Type)).ToList();
        _functionReturnNewtype[stmt.Name.Lexeme] = GetDeclaredNewtypeName(stmt.ReturnType);

        // C Calling Convention
        function.FunctionCallConv = 0; // LLVMCCallConv = 0
    }
}
