using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using LLVMSharp.Interop;
using ZV.Compiler.AST;
using ZV.Compiler.Lexer;

namespace ZV.Compiler.Backend;

public partial class LlvmGenerator
{
    // Exception is a struct { i8* message, i32 type_id }. type_id is a compile-time-assigned
    // numeric id for the exception's declared type name (see GetExceptionTypeId), used by
    // VisitTryCatch to dispatch to the matching catch clause with a single `switch`
    // instruction instead of a cascade of runtime strncmp calls against the message text.
    private LLVMTypeRef GetExceptionType()
    {
        if (_exceptionType.HasValue)
            return _exceptionType.Value;

        var excType = _context.CreateNamedStruct("Exception");
        excType.StructSetBody(new[] { GetPointerType(GetInt8Type()), GetInt32Type() }, false);
        _exceptionType = excType;

        // Register it as a known struct so field access works
        _structTypes["Exception"] = excType;
        RegisterStructFields("Exception", new List<string> { "message", "type_id" });
        _structFieldTypes["Exception"] = new List<LLVMTypeRef> { GetPointerType(GetInt8Type()), GetInt32Type() };

        return excType;
    }

    // Assigns a stable (for the lifetime of this compiler instance/module) numeric id to
    // each declared exception type name, lazily on first use. Id 0 is reserved for the
    // catch-all "Exception" type and for exception values with no statically-known declared
    // type (e.g. `throw "some raw string";`), so a specific `catch (Foo e)` never matches
    // those by id - it only matches exceptions constructed via `Foo(...)`/`throw Foo;`.
    private readonly Dictionary<string, int> _exceptionTypeIds = new();

    private int GetExceptionTypeId(string? typeName)
    {
        if (typeName == null || typeName == "Exception") return 0;
        if (!_exceptionTypeIds.TryGetValue(typeName, out var id))
        {
            id = _exceptionTypeIds.Count + 1;
            _exceptionTypeIds[typeName] = id;
        }
        return id;
    }

    private void EnsureExceptionGlobals()
    {
        if (_exceptionGlobalsInitialized) return;
        _exceptionGlobalsInitialized = true;

        // Global: i8* __zv_exception_msg (holds message of current exception)
        _globalExceptionMsg = _module.AddGlobal(GetPointerType(GetInt8Type()), "__zv_exception_msg");
        _globalExceptionMsg.Initializer = LLVMValueRef.CreateConstNull(GetPointerType(GetInt8Type()));
        _globalExceptionMsg.Linkage = LLVMLinkage.LLVMInternalLinkage;
        MakeThreadLocalIfSupported(_globalExceptionMsg);

        // Global: i32 __zv_exception_type_id (holds the type id of the current exception)
        _globalExceptionTypeId = _module.AddGlobal(GetInt32Type(), "__zv_exception_type_id");
        _globalExceptionTypeId.Initializer = LLVMValueRef.CreateConstInt(GetInt32Type(), 0);
        _globalExceptionTypeId.Linkage = LLVMLinkage.LLVMInternalLinkage;
        MakeThreadLocalIfSupported(_globalExceptionTypeId);

        // Global: i1 __zv_exception_active (whether an exception is in-flight)
        _globalExceptionActive = _module.AddGlobal(GetInt1Type(), "__zv_exception_active");
        _globalExceptionActive.Initializer = LLVMValueRef.CreateConstInt(GetInt1Type(), 0);
        _globalExceptionActive.Linkage = LLVMLinkage.LLVMInternalLinkage;
        MakeThreadLocalIfSupported(_globalExceptionActive);
    }

    // jmp_buf layout: enough bytes for the platform setjmp buffer followed by the
    // saved chunked cleanup-stack top (head chunk pointer + used count). The
    // setjmp/longjmp calls receive a pointer to the buffer portion (field 0); the cleanup
    // top is read/written separately so exception unwinding knows how far to pop the
    // cleanup stack.
    private LLVMTypeRef GetJmpBufType()
    {
        if (!_jmpBufType.HasValue)
        {
            _jmpBufType = LLVMTypeRef.CreateStruct(new[]
            {
                LLVMTypeRef.CreateArray(GetInt8Type(), 480),
                GetPointerType(GetInt8Type()),
                GetInt32Type()
            }, Packed: false);
        }
        return _jmpBufType.Value;
    }

    private LLVMValueRef GetJmpBufSavedHeadPtr(LLVMValueRef jmpBufPtr)
    {
        var jmpBufType = GetJmpBufType();
        var typedPtr = _builder.BuildBitCast(jmpBufPtr, GetPointerType(jmpBufType), "jmpbuf_typed");
        return _builder.BuildStructGEP2(jmpBufType, typedPtr, 1, "jmpbuf_saved_head_ptr");
    }

    private LLVMValueRef GetJmpBufSavedUsedPtr(LLVMValueRef jmpBufPtr)
    {
        var jmpBufType = GetJmpBufType();
        var typedPtr = _builder.BuildBitCast(jmpBufPtr, GetPointerType(jmpBufType), "jmpbuf_typed");
        return _builder.BuildStructGEP2(jmpBufType, typedPtr, 2, "jmpbuf_saved_used_ptr");
    }

    private bool IsWindows => RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

    private LLVMValueRef GetSetjmpFunction()
    {
        // On Windows MSVC, setjmp is _setjmp(jmp_buf, frame_ptr)
        string name = IsWindows ? "_setjmp" : "setjmp";
        var func = _module.GetNamedFunction(name);
        if (func.Handle == IntPtr.Zero)
        {
            LLVMTypeRef funcType;
            if (IsWindows)
            {
                // int _setjmp(jmp_buf env, void* frame)
                funcType = LLVMTypeRef.CreateFunction(GetInt32Type(), new[] { GetPointerType(GetInt8Type()), GetPointerType(GetInt8Type()) });
            }
            else
            {
                // int setjmp(jmp_buf env)
                funcType = LLVMTypeRef.CreateFunction(GetInt32Type(), new[] { GetPointerType(GetInt8Type()) });
            }
            func = _module.AddFunction(name, funcType);
            _functionTypes[name] = funcType;
        }
        return func;
    }

    private LLVMValueRef CallSetjmp(LLVMValueRef jmpBufPtr)
    {
        if (IsWindows)
        {
            string name = "_setjmp";
            var func = GetSetjmpFunction();
            var nullFrame = LLVMValueRef.CreateConstNull(GetPointerType(GetInt8Type()));
            return _builder.BuildCall2(_functionTypes[name], func, new[] { jmpBufPtr, nullFrame }, "setjmp_result");
        }
        else
        {
            string name = "setjmp";
            var func = GetSetjmpFunction();
            return _builder.BuildCall2(_functionTypes[name], func, new[] { jmpBufPtr }, "setjmp_result");
        }
    }

    private LLVMValueRef GetLongjmpFunction()
    {
        var func = _module.GetNamedFunction("longjmp");
        if (func.Handle == IntPtr.Zero)
        {
            // void longjmp(jmp_buf env, int val)
            var funcType = LLVMTypeRef.CreateFunction(GetVoidType(), new[] { GetPointerType(GetInt8Type()), GetInt32Type() });
            func = _module.AddFunction("longjmp", funcType);
            _functionTypes["longjmp"] = funcType;
        }
        return func;
    }

    // Global jmp_buf stack for nested try/catch
    // We use a global linked-list approach: a global pointer to current jmp_buf.
    // This is thread-local in hosted targets so nested try/catch in one thread
    // does not corrupt the handler chain of another thread.
    private LLVMValueRef GetGlobalJmpBufPtr()
    {
        var existing = _module.GetNamedGlobal("__zv_jmpbuf_ptr");
        if (existing.Handle != IntPtr.Zero)
            return existing;

        // Global: i8* __zv_jmpbuf_ptr (pointer to current jmp_buf on the stack)
        var global = _module.AddGlobal(GetPointerType(GetInt8Type()), "__zv_jmpbuf_ptr");
        global.Initializer = LLVMValueRef.CreateConstNull(GetPointerType(GetInt8Type()));
        global.Linkage = LLVMLinkage.LLVMInternalLinkage;
        MakeThreadLocalIfSupported(global);
        return global;
    }

    private void VisitTryCatch(TryCatchStmt stmt)
    {
        EnsureExceptionGlobals();

        var function = _builder.InsertBlock.Parent;
        var jmpBufType = GetJmpBufType();

        // Allocate a jmp_buf on the stack. Windows _setjmp stores aligned XMM state, so
        // the buffer must be at least 16-byte aligned.
        var jmpBuf = BuildEntryAlloca(jmpBufType, "jmpbuf");
        jmpBuf.SetAlignment(16);
        var jmpBufPtr = _builder.BuildBitCast(jmpBuf, GetPointerType(GetInt8Type()), "jmpbuf_ptr");

        // Save the previous jmp_buf pointer (for nesting)
        var globalJmpBuf = GetGlobalJmpBufPtr();
        var prevJmpBuf = _builder.BuildLoad2(GetPointerType(GetInt8Type()), globalJmpBuf, "prev_jmpbuf");

        // Set current jmp_buf as the active one
        _builder.BuildStore(jmpBufPtr, globalJmpBuf);

        // Record the current cleanup-stack top (chunk head + used count) so a throw can
        // pop everything pushed inside the try block before longjmp'ing back here.
        var (savedHead, savedUsed) = BuildCleanupTopLoad();
        _builder.BuildStore(savedHead, GetJmpBufSavedHeadPtr(jmpBufPtr));
        _builder.BuildStore(savedUsed, GetJmpBufSavedUsedPtr(jmpBufPtr));

        // Call setjmp
        var setjmpResult = CallSetjmp(jmpBufPtr);

        // If setjmp returns 0, execute try body; otherwise, dispatch to the matching catch clause
        var isZero = _builder.BuildICmp(LLVMIntPredicate.LLVMIntEQ, setjmpResult, LLVMValueRef.CreateConstInt(GetInt32Type(), 0), "is_try");

        var tryBB = _context.AppendBasicBlock(function, "try_body");
        var dispatchBB = _context.AppendBasicBlock(function, "catch_dispatch");
        var mergeBB = _context.AppendBasicBlock(function, "try_merge");

        _builder.BuildCondBr(isZero, tryBB, dispatchBB);

        // Try body
        _builder.PositionAtEnd(tryBB);
        VisitStatement(stmt.TryBody);
        // After try body, restore previous jmp_buf and branch to merge
        if (_builder.InsertBlock.LastInstruction.Handle == IntPtr.Zero || _builder.InsertBlock.LastInstruction.IsATerminatorInst.Handle == IntPtr.Zero)
        {
            _builder.BuildStore(prevJmpBuf, globalJmpBuf);
            _builder.BuildBr(mergeBB);
        }

        // Dispatch: restore the previous jmp_buf, then switch on the exception's numeric
        // type id (see GetExceptionTypeId) to jump directly to the matching catch clause.
        // This replaces a cascade of per-clause runtime strncmp calls against the message
        // text with a single `switch` instruction - O(1) instead of O(clauses), and much
        // smaller/faster generated code.
        _builder.PositionAtEnd(dispatchBB);
        _builder.BuildStore(prevJmpBuf, globalJmpBuf);
        var msgVal = _builder.BuildLoad2(GetPointerType(GetInt8Type()), _globalExceptionMsg, "exc_msg");
        var typeIdVal = _builder.BuildLoad2(GetInt32Type(), _globalExceptionTypeId, "exc_type_id");

        CatchClause? catchAllClause = null;
        var typedCases = new List<(CatchClause Clause, LLVMBasicBlockRef BodyBB)>();
        LLVMBasicBlockRef? catchAllBodyBB = null;

        for (int i = 0; i < stmt.CatchClauses.Count; i++)
        {
            var clause = stmt.CatchClauses[i];
            bool isCatchAll = clause.ExceptionTypeName == null || clause.ExceptionTypeName == "Exception";

            if (!isCatchAll && !_declaredExceptionTypes.Contains(clause.ExceptionTypeName!))
            {
                throw new CompileException(clause.Location,
                    $"Unknown exception type '{clause.ExceptionTypeName}' in catch clause. Declare it first with 'exception {clause.ExceptionTypeName};'.");
            }

            var catchBodyBB = _context.AppendBasicBlock(function, $"catch_body_{i}");
            if (isCatchAll)
            {
                catchAllClause = clause;
                catchAllBodyBB = catchBodyBB;
                break; // Catch-all must be the last clause (enforced by the parser).
            }

            typedCases.Add((clause, catchBodyBB));
        }

        // No catch-all: unmatched types fall through to a block that propagates the
        // exception to the enclosing handler (or aborts if there is none).
        var noMatchBB = catchAllBodyBB ?? _context.AppendBasicBlock(function, "catch_no_match");

        var switchInst = _builder.BuildSwitch(typeIdVal, noMatchBB, (uint)typedCases.Count);
        foreach (var (clause, bodyBB) in typedCases)
        {
            var caseId = LLVMValueRef.CreateConstInt(GetInt32Type(), (ulong)GetExceptionTypeId(clause.ExceptionTypeName));
            switchInst.AddCase(caseId, bodyBB);
        }

        foreach (var (clause, bodyBB) in typedCases)
        {
            EmitCatchClauseBody(clause, bodyBB, mergeBB, msgVal, typeIdVal);
        }

        if (catchAllClause != null)
        {
            EmitCatchClauseBody(catchAllClause, catchAllBodyBB!.Value, mergeBB, msgVal, typeIdVal);
        }
        else
        {
            _builder.PositionAtEnd(noMatchBB);
            EmitAbortOrLongjmp(msgVal, prevJmpBuf);
        }

        // Merge block
        _builder.PositionAtEnd(mergeBB);
    }

    // Populates the exception variable and runs one catch clause's body in `catchBodyBB`,
    // branching to `mergeBB` afterwards (unless the body already terminates, e.g. via return).
    private void EmitCatchClauseBody(CatchClause clause, LLVMBasicBlockRef catchBodyBB, LLVMBasicBlockRef mergeBB, LLVMValueRef msgVal, LLVMValueRef typeIdVal)
    {
        _builder.PositionAtEnd(catchBodyBB);

        var excType = GetExceptionType();
        var excAlloca = BuildEntryAlloca(excType, clause.ExceptionName.Lexeme);
        var msgFieldPtr = _builder.BuildStructGEP2(excType, excAlloca, 0, "exc_msg_field");
        _builder.BuildStore(msgVal, msgFieldPtr);
        var typeIdFieldPtr = _builder.BuildStructGEP2(excType, excAlloca, 1, "exc_type_id_field");
        _builder.BuildStore(typeIdVal, typeIdFieldPtr);
        _namedValues[clause.ExceptionName.Lexeme] = (excAlloca, excType, "Exception");

        // Clear the exception active flag
        _builder.BuildStore(LLVMValueRef.CreateConstInt(GetInt1Type(), 0), _globalExceptionActive);

        VisitStatement(clause.Body);
        if (_builder.InsertBlock.LastInstruction.Handle == IntPtr.Zero || _builder.InsertBlock.LastInstruction.IsATerminatorInst.Handle == IntPtr.Zero)
        {
            _builder.BuildBr(mergeBB);
        }
    }

    private void VisitThrow(ThrowStmt stmt)
    {
        EnsureExceptionGlobals();

        // A bare `throw MyError;` (no call) refers to a declared exception type by name -
        // construct it with zero arguments, i.e. its default message (see
        // GenerateExceptionConstructor / _exceptionDefaultMessages).
        LLVMValueRef exceptionValue;
        if (stmt.Value is VariableExpr ve && ve.Name != "Exception" && _declaredExceptionTypes.Contains(ve.Name))
        {
            exceptionValue = GenerateExceptionConstructor(new List<Expression>(), ve.Name);
        }
        else
        {
            exceptionValue = VisitExpression(stmt.Value);
        }
        
        // Extract message (and, for an Exception struct, its declared type id) and store in
        // the exception globals. Values not statically known to be a specific declared
        // exception type (raw strings/pointers) get type id 0 (see GetExceptionTypeId), so
        // they never spuriously match a specific `catch (Foo e)` by id.
        LLVMValueRef msgPtr;
        LLVMValueRef typeIdVal;
        if (IsStringStructType(exceptionValue.TypeOf))
        {
            // STRING values carry a NUL-terminated data pointer; use it as the message.
            msgPtr = _builder.BuildExtractValue(exceptionValue, 0, "throw_msg");
            typeIdVal = LLVMValueRef.CreateConstInt(GetInt32Type(), 0);
        }
        else if (exceptionValue.TypeOf.Kind == LLVMTypeKind.LLVMStructTypeKind)
        {
            // It's an Exception struct, extract the message and type id fields
            msgPtr = _builder.BuildExtractValue(exceptionValue, 0, "throw_msg");
            typeIdVal = _builder.BuildExtractValue(exceptionValue, 1, "throw_type_id");
        }
        else if (exceptionValue.TypeOf.Kind == LLVMTypeKind.LLVMPointerTypeKind)
        {
            // It's a CSTRING / raw pointer directly
            msgPtr = exceptionValue;
            typeIdVal = LLVMValueRef.CreateConstInt(GetInt32Type(), 0);
        }
        else
        {
            // Create a generic message
            msgPtr = GetOrCreateGlobalStringPtr("unknown exception", "unknown_exc_msg");
            typeIdVal = LLVMValueRef.CreateConstInt(GetInt32Type(), 0);
        }

        EmitUnconditionalThrow(msgPtr, typeIdVal);
    }

    // Explicit `throw` statement (as opposed to a runtime check's EmitCondThrow): always
    // throws, so it delegates to the shared __zv_throw_uncond runtime function rather than
    // inlining the dispatch control flow at every throw site, then marks the current block
    // unreachable (the call never actually returns - it either longjmps or aborts).
    private void EmitUnconditionalThrow(LLVMValueRef msgPtr, LLVMValueRef typeIdVal)
    {
        EnsureExceptionGlobals();
        var throwUncond = GetOrCreateZvThrowUncondFunction();
        _builder.BuildCall2(_functionTypes["__zv_throw_uncond"], throwUncond, new[] { msgPtr, typeIdVal }, "");
        _builder.BuildUnreachable();
    }

    // Generates (once per module) a shared `void __zv_throw_uncond(i8* msg, i32 type_id)`
    // function that performs the full exception dispatch (store message/type id, pop
    // cleanup stack, longjmp/abort). Every unconditional `throw` statement calls this
    // instead of inlining the dispatch's several basic blocks at each throw site, which
    // previously duplicated that control flow (and every check inside it) once per call site.
    private LLVMValueRef GetOrCreateZvThrowUncondFunction()
    {
        const string name = "__zv_throw_uncond";
        var existing = _module.GetNamedFunction(name);
        if (existing.Handle != IntPtr.Zero) return existing;

        var funcType = LLVMTypeRef.CreateFunction(GetVoidType(), new[] { GetPointerType(GetInt8Type()), GetInt32Type() });
        var func = _module.AddFunction(name, funcType);
        func.Linkage = LLVMLinkage.LLVMInternalLinkage;
        _functionTypes[name] = funcType;

        var savedBlock = _builder.InsertBlock;
        var entry = _context.AppendBasicBlock(func, "entry");
        _builder.PositionAtEnd(entry);

        // EmitExceptionDispatch always terminates every path it creates (longjmp/abort both
        // end in `unreachable`), so no explicit return is needed here.
        EmitExceptionDispatch(func.GetParam(0), func.GetParam(1));

        if (savedBlock.Handle != IntPtr.Zero)
        {
            _builder.PositionAtEnd(savedBlock);
        }
        return func;
    }

    // Generates (once per module) a shared `void __zv_throw_cond(i1 cond, i8* msg, i32
    // type_id)` function that throws (via __zv_throw_uncond) only if `cond` is true,
    // otherwise returns. Every runtime bounds/null/failure check calls this instead of
    // inlining its own branch + full dispatch control flow at each check site (see
    // EmitCondThrow).
    private LLVMValueRef GetOrCreateZvThrowCondFunction()
    {
        const string name = "__zv_throw_cond";
        var existing = _module.GetNamedFunction(name);
        if (existing.Handle != IntPtr.Zero) return existing;

        var throwUncond = GetOrCreateZvThrowUncondFunction();

        var funcType = LLVMTypeRef.CreateFunction(GetVoidType(), new[] { GetInt1Type(), GetPointerType(GetInt8Type()), GetInt32Type() });
        var func = _module.AddFunction(name, funcType);
        func.Linkage = LLVMLinkage.LLVMInternalLinkage;
        _functionTypes[name] = funcType;

        var savedBlock = _builder.InsertBlock;
        var entry = _context.AppendBasicBlock(func, "entry");
        _builder.PositionAtEnd(entry);

        var cond = func.GetParam(0);
        var msg = func.GetParam(1);
        var typeId = func.GetParam(2);

        var throwBB = _context.AppendBasicBlock(func, "throw_exc");
        var contBB = _context.AppendBasicBlock(func, "no_exc");
        _builder.BuildCondBr(cond, throwBB, contBB);

        _builder.PositionAtEnd(throwBB);
        _builder.BuildCall2(_functionTypes["__zv_throw_uncond"], throwUncond, new[] { msg, typeId }, "");
        _builder.BuildUnreachable();

        _builder.PositionAtEnd(contBB);
        _builder.BuildRetVoid();

        if (savedBlock.Handle != IntPtr.Zero)
        {
            _builder.PositionAtEnd(savedBlock);
        }
        return func;
    }

    // Common exception dispatch: store the exception message and type id, pop the cleanup
    // stack to the top saved by the active try block, then either longjmp to the handler or
    // abort.
    private void EmitExceptionDispatch(LLVMValueRef msgPtr, LLVMValueRef typeIdVal)
    {
        EnsureExceptionGlobals();

        _builder.BuildStore(msgPtr, _globalExceptionMsg);
        _builder.BuildStore(typeIdVal, _globalExceptionTypeId);
        _builder.BuildStore(LLVMValueRef.CreateConstInt(GetInt1Type(), 1), _globalExceptionActive);

        var globalJmpBuf = GetGlobalJmpBufPtr();
        var currentJmpBuf = _builder.BuildLoad2(GetPointerType(GetInt8Type()), globalJmpBuf, "current_jmpbuf");

        // Pop every cleanup record pushed since the matching try block began. If no handler
        // is active, the cleanup stack top is left unchanged (the abort path exits anyway).
        var hasHandler = _builder.BuildICmp(LLVMIntPredicate.LLVMIntNE, currentJmpBuf,
            LLVMValueRef.CreateConstNull(GetPointerType(GetInt8Type())), "has_handler");
        var function = _builder.InsertBlock.Parent;
        var cleanupBB = _context.AppendBasicBlock(function, "exc_cleanup");
        var dispatchBB = _context.AppendBasicBlock(function, "exc_dispatch");
        _builder.BuildCondBr(hasHandler, cleanupBB, dispatchBB);

        _builder.PositionAtEnd(cleanupBB);
        var savedHead = _builder.BuildLoad2(GetPointerType(GetInt8Type()), GetJmpBufSavedHeadPtr(currentJmpBuf), "jmpbuf_saved_head");
        var savedUsed = _builder.BuildLoad2(GetInt32Type(), GetJmpBufSavedUsedPtr(currentJmpBuf), "jmpbuf_saved_used");
        BuildPopCleanupRecordsTo(savedHead, savedUsed);
        _builder.BuildBr(dispatchBB);

        _builder.PositionAtEnd(dispatchBB);
        EmitAbortOrLongjmp(msgPtr, currentJmpBuf);
    }

    // Shared tail of every throw path (explicit `throw`, a failed runtime check, or a
    // rethrow when no catch clause matches): if `jmpBufPtr` is null there is no active
    // handler, so print the message and exit; otherwise longjmp to it.
    private void EmitAbortOrLongjmp(LLVMValueRef msgPtr, LLVMValueRef jmpBufPtr)
    {
        var isNull = _builder.BuildICmp(LLVMIntPredicate.LLVMIntEQ, jmpBufPtr,
            LLVMValueRef.CreateConstNull(GetPointerType(GetInt8Type())), "jmpbuf_null");

        var function = _builder.InsertBlock.Parent;
        var abortBB = _context.AppendBasicBlock(function, "unhandled_exc");
        var longjmpBB = _context.AppendBasicBlock(function, "do_longjmp");

        _builder.BuildCondBr(isNull, abortBB, longjmpBB);

        // Unhandled exception: print message and exit
        _builder.PositionAtEnd(abortBB);
        var printfFunc = GetOrAddFunction("printf", GetInt32Type(), new[] { GetPointerType(GetInt8Type()) }, true);
        var fmtStr = GetOrCreateGlobalStringPtr("Unhandled exception: %s\n", "unhandled_fmt");
        _builder.BuildCall2(_functionTypes["printf"], printfFunc, new[] { fmtStr, msgPtr }, "");
        var exitFunc = GetOrAddFunction("exit", GetVoidType(), new[] { GetInt32Type() });
        _builder.BuildCall2(_functionTypes["exit"], exitFunc, new[] { LLVMValueRef.CreateConstInt(GetInt32Type(), 1) }, "");
        _builder.BuildUnreachable();

        // longjmp path
        _builder.PositionAtEnd(longjmpBB);
        var longjmp = GetLongjmpFunction();
        _builder.BuildCall2(_functionTypes["longjmp"], longjmp,
            new[] { jmpBufPtr, LLVMValueRef.CreateConstInt(GetInt32Type(), 1) }, "");
        _builder.BuildUnreachable();
    }

    private void VisitTypeAlias(TypeAliasStmt stmt)
    {
        var aliasedType = MapTypeNode(stmt.AliasedType);
        _typeAliases[stmt.Name.Lexeme] = aliasedType;

        if (stmt.IsNewtype)
        {
            _newtypeNames.Add(stmt.Name.Lexeme);
        }

        // Register it as a struct type alias so UserTypeNode lookups work
        if (!_structTypes.ContainsKey(stmt.Name.Lexeme))
        {
            _structTypes[stmt.Name.Lexeme] = aliasedType;
            
            // If the aliased type is a struct, also copy field info
            if (aliasedType.Kind == LLVMTypeKind.LLVMStructTypeKind)
            {
                // Find the original struct name
                string? origName = null;
                foreach (var kvp in _structTypes)
                {
                    if (kvp.Value.Handle == aliasedType.Handle && kvp.Key != stmt.Name.Lexeme)
                    {
                        origName = kvp.Key;
                        break;
                    }
                }
                if (origName != null && _structFieldNames.ContainsKey(origName))
                {
                    RegisterStructFields(stmt.Name.Lexeme, _structFieldNames[origName]);
                    _structFieldTypes[stmt.Name.Lexeme] = _structFieldTypes[origName];
                }
            }
        }
    }

    // `exception Name;` (optionally `exception Name = <default message>;`) - registers
    // Name as a constructible, catchable exception type. See _declaredExceptionTypes,
    // _exceptionDefaultMessages, GenerateExceptionConstructor, and VisitTryCatch.
    private void VisitExceptionTypeDecl(ExceptionTypeDeclStmt stmt)
    {
        if (stmt.Name.Lexeme == "Exception")
        {
            throw new CompileException(stmt.Location, "'Exception' is already the built-in catch-all exception type and cannot be redeclared.");
        }
        _declaredExceptionTypes.Add(stmt.Name.Lexeme);
        if (stmt.DefaultMessage != null)
        {
            _exceptionDefaultMessages[stmt.Name.Lexeme] = stmt.DefaultMessage;
        }
    }
}
