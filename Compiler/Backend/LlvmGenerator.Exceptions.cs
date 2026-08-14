using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using LLVMSharp.Interop;
using ZV.Compiler.AST;
using ZV.Compiler.Lexer;

namespace ZV.Compiler.Backend;

public partial class LlvmGenerator
{
    // Exception is a struct { i8* message }
    private LLVMTypeRef GetExceptionType()
    {
        if (_exceptionType.HasValue)
            return _exceptionType.Value;

        var excType = _context.CreateNamedStruct("Exception");
        excType.StructSetBody(new[] { GetPointerType(GetInt8Type()) }, false);
        _exceptionType = excType;

        // Register it as a known struct so field access works
        _structTypes["Exception"] = excType;
        _structFieldNames["Exception"] = new List<string> { "message" };
        _structFieldTypes["Exception"] = new List<LLVMTypeRef> { GetPointerType(GetInt8Type()) };

        return excType;
    }

    private void EnsureExceptionGlobals()
    {
        if (_exceptionGlobalsInitialized) return;
        _exceptionGlobalsInitialized = true;

        // Global: i8* __zv_exception_msg (holds message of current exception)
        _globalExceptionMsg = _module.AddGlobal(GetPointerType(GetInt8Type()), "__zv_exception_msg");
        _globalExceptionMsg.Initializer = LLVMValueRef.CreateConstNull(GetPointerType(GetInt8Type()));
        _globalExceptionMsg.Linkage = LLVMLinkage.LLVMInternalLinkage;

        // Global: i1 __zv_exception_active (whether an exception is in-flight)
        _globalExceptionActive = _module.AddGlobal(GetInt1Type(), "__zv_exception_active");
        _globalExceptionActive.Initializer = LLVMValueRef.CreateConstInt(GetInt1Type(), 0);
        _globalExceptionActive.Linkage = LLVMLinkage.LLVMInternalLinkage;
    }

    // setjmp buffer type: platform-dependent but we use a large enough i8 array
    // Windows x64 MSVC: _JUMP_BUFFER is 256 bytes; Linux x64: 200 bytes; i386: ~156 bytes
    // We'll use 512 bytes to be safe on all platforms.
    private LLVMTypeRef GetJmpBufType()
    {
        return LLVMTypeRef.CreateArray(GetInt8Type(), 512);
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
    // We use a global linked-list approach: a global pointer to current jmp_buf
    private LLVMValueRef GetGlobalJmpBufPtr()
    {
        var existing = _module.GetNamedGlobal("__zv_jmpbuf_ptr");
        if (existing.Handle != IntPtr.Zero)
            return existing;

        // Global: i8* __zv_jmpbuf_ptr (pointer to current jmp_buf on the stack)
        var global = _module.AddGlobal(GetPointerType(GetInt8Type()), "__zv_jmpbuf_ptr");
        global.Initializer = LLVMValueRef.CreateConstNull(GetPointerType(GetInt8Type()));
        global.Linkage = LLVMLinkage.LLVMInternalLinkage;
        return global;
    }

    // Global to save the previous jmp_buf pointer for nesting
    private LLVMValueRef GetGlobalPrevJmpBufPtr()
    {
        var existing = _module.GetNamedGlobal("__zv_prev_jmpbuf_ptr");
        if (existing.Handle != IntPtr.Zero)
            return existing;

        var global = _module.AddGlobal(GetPointerType(GetInt8Type()), "__zv_prev_jmpbuf_ptr");
        global.Initializer = LLVMValueRef.CreateConstNull(GetPointerType(GetInt8Type()));
        global.Linkage = LLVMLinkage.LLVMInternalLinkage;
        return global;
    }

    private void VisitTryCatch(TryCatchStmt stmt)
    {
        EnsureExceptionGlobals();

        var function = _builder.InsertBlock.Parent;
        var jmpBufType = GetJmpBufType();

        // Allocate a jmp_buf on the stack
        var jmpBuf = _builder.BuildAlloca(jmpBufType, "jmpbuf");
        var jmpBufPtr = _builder.BuildBitCast(jmpBuf, GetPointerType(GetInt8Type()), "jmpbuf_ptr");

        // Save the previous jmp_buf pointer (for nesting)
        var globalJmpBuf = GetGlobalJmpBufPtr();
        var prevJmpBuf = _builder.BuildLoad2(GetPointerType(GetInt8Type()), globalJmpBuf, "prev_jmpbuf");

        // Set current jmp_buf as the active one
        _builder.BuildStore(jmpBufPtr, globalJmpBuf);

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

        // Dispatch: restore the previous jmp_buf, then test the runtime exception's type
        // name (the "TypeName: ..." message prefix convention) against each catch clause
        // in source order, running the first one that matches.
        _builder.PositionAtEnd(dispatchBB);
        _builder.BuildStore(prevJmpBuf, globalJmpBuf);
        var msgVal = _builder.BuildLoad2(GetPointerType(GetInt8Type()), _globalExceptionMsg, "exc_msg");

        var currentCheckBB = dispatchBB;
        bool endedInCatchAll = false;

        for (int i = 0; i < stmt.CatchClauses.Count; i++)
        {
            var clause = stmt.CatchClauses[i];
            bool isCatchAll = clause.ExceptionTypeName == null || clause.ExceptionTypeName == "Exception";

            if (!isCatchAll && !_declaredExceptionTypes.Contains(clause.ExceptionTypeName!))
            {
                throw new CompileException(clause.Location,
                    $"Unknown exception type '{clause.ExceptionTypeName}' in catch clause. Declare it first with 'exception {clause.ExceptionTypeName};'.");
            }

            _builder.PositionAtEnd(currentCheckBB);
            var catchBodyBB = _context.AppendBasicBlock(function, $"catch_body_{i}");

            if (isCatchAll)
            {
                _builder.BuildBr(catchBodyBB);
                EmitCatchClauseBody(clause, catchBodyBB, mergeBB, msgVal);
                endedInCatchAll = true;
                break;
            }

            var nextCheckBB = _context.AppendBasicBlock(function, $"catch_check_{i + 1}");
            EmitExceptionTypeCheck(msgVal, clause.ExceptionTypeName!, catchBodyBB, nextCheckBB);
            EmitCatchClauseBody(clause, catchBodyBB, mergeBB, msgVal);
            currentCheckBB = nextCheckBB;
        }

        // No clause matched: propagate the exception to the enclosing handler (or abort if
        // there is none), exactly like an uncaught throw would.
        if (!endedInCatchAll)
        {
            _builder.PositionAtEnd(currentCheckBB);
            EmitAbortOrLongjmp(msgVal, prevJmpBuf);
        }

        // Merge block
        _builder.PositionAtEnd(mergeBB);
    }

    // Emits a runtime check for whether `msgVal` (the exception message) starts with the
    // "typeName: " prefix, branching to `matchBB` if so and `noMatchBB` otherwise. This is
    // how catch clauses filter by exception type: every runtime exception - built-in or
    // user-thrown - is a message string that follows the "TypeName: description" convention.
    private void EmitExceptionTypeCheck(LLVMValueRef msgVal, string typeName, LLVMBasicBlockRef matchBB, LLVMBasicBlockRef noMatchBB)
    {
        string prefix = typeName + ": ";
        var prefixPtr = GetOrCreateGlobalStringPtr(prefix, "catch_type_prefix");
        var strncmpFunc = GetOrAddFunction("strncmp", GetInt32Type(),
            new[] { GetPointerType(GetInt8Type()), GetPointerType(GetInt8Type()), GetInt64Type() });
        var cmpResult = _builder.BuildCall2(_functionTypes["strncmp"], strncmpFunc,
            new[] { msgVal, prefixPtr, LLVMValueRef.CreateConstInt(GetInt64Type(), (ulong)prefix.Length, false) }, "type_cmp");
        var matches = _builder.BuildICmp(LLVMIntPredicate.LLVMIntEQ, cmpResult, LLVMValueRef.CreateConstInt(GetInt32Type(), 0), "type_match");
        _builder.BuildCondBr(matches, matchBB, noMatchBB);
    }

    // Populates the exception variable and runs one catch clause's body in `catchBodyBB`,
    // branching to `mergeBB` afterwards (unless the body already terminates, e.g. via return).
    private void EmitCatchClauseBody(CatchClause clause, LLVMBasicBlockRef catchBodyBB, LLVMBasicBlockRef mergeBB, LLVMValueRef msgVal)
    {
        _builder.PositionAtEnd(catchBodyBB);

        var excType = GetExceptionType();
        var excAlloca = _builder.BuildAlloca(excType, clause.ExceptionName.Lexeme);
        var msgFieldPtr = _builder.BuildStructGEP2(excType, excAlloca, 0, "exc_msg_field");
        _builder.BuildStore(msgVal, msgFieldPtr);
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
        
        // Extract message from the exception struct and store in global
        LLVMValueRef msgPtr;
        if (IsStringStructType(exceptionValue.TypeOf))
        {
            // STRING values carry a NUL-terminated data pointer; use it as the message.
            msgPtr = _builder.BuildExtractValue(exceptionValue, 0, "throw_msg");
        }
        else if (exceptionValue.TypeOf.Kind == LLVMTypeKind.LLVMStructTypeKind)
        {
            // It's an Exception struct, extract the message field
            msgPtr = _builder.BuildExtractValue(exceptionValue, 0, "throw_msg");
        }
        else if (exceptionValue.TypeOf.Kind == LLVMTypeKind.LLVMPointerTypeKind)
        {
            // It's a CSTRING / raw pointer directly
            msgPtr = exceptionValue;
        }
        else
        {
            // Create a generic message
            msgPtr = GetOrCreateGlobalStringPtr("unknown exception", "unknown_exc_msg");
        }

        _builder.BuildStore(msgPtr, _globalExceptionMsg);
        _builder.BuildStore(LLVMValueRef.CreateConstInt(GetInt1Type(), 1), _globalExceptionActive);

        // Free any owned allocations before unwinding, so a thrown exception does not leak.
        CleanupAllOpenScopes();

        // Load the current jmp_buf pointer and either longjmp to it or abort if unhandled.
        var globalJmpBuf = GetGlobalJmpBufPtr();
        var currentJmpBuf = _builder.BuildLoad2(GetPointerType(GetInt8Type()), globalJmpBuf, "current_jmpbuf");
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
                    _structFieldNames[stmt.Name.Lexeme] = _structFieldNames[origName];
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
