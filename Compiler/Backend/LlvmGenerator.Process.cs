using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using LLVMSharp.Interop;
using ZV.Compiler.AST;

namespace ZV.Compiler.Backend;

public partial class LlvmGenerator
{
    // PROCESS is a builtin struct { BOOL child }, produced by respawn(). It intentionally
    // does not carry a pid or any other OS handle: see the "respawn()" notes below for why.
    private LLVMTypeRef? _processType;

    // respawn() cannot be a real fork() on Windows (there's no way to duplicate a running
    // process's address space there), so instead of giving POSIX and Windows different
    // semantics, respawn() uses the *same* strategy everywhere: it launches a brand new OS
    // process that re-runs this same executable with its original command-line arguments
    // plus a hidden marker argument, and returns PROCESS{child:false} to the caller. The
    // newly launched process runs the program from its entry point again; when *its* code
    // reaches a respawn() call, the marker is detected and it returns PROCESS{child:true}
    // immediately, without spawning yet another process.
    //
    // Consequence: unlike a real fork(), everything the program does *before* the respawn()
    // call runs twice (once in the original process, once again in the freshly-started
    // child) - the child does not inherit the parent's local variables, open sockets, or
    // call stack at the point of the call. respawn() is meant for "relaunch myself as an
    // isolated worker" patterns (e.g. redo the risky/heavy part of the program in a separate
    // process), not for forking off a child mid-loop to inherit already-accepted work.
    private const string RespawnSentinelArg = "--zv-respawn-child";

    private LLVMValueRef _globalRespawnArgc;
    private LLVMValueRef _globalRespawnArgv;
    private LLVMValueRef _globalRespawnIsChild;
    private bool _respawnGlobalsInitialized;

    private LLVMTypeRef GetProcessType()
    {
        if (_processType.HasValue)
            return _processType.Value;

        var processType = _context.CreateNamedStruct("PROCESS");
        processType.StructSetBody(new[] { GetInt1Type() }, false);
        _processType = processType;

        _structTypes["PROCESS"] = processType;
        RegisterStructFields("PROCESS", new List<string> { "child" });
        _structFieldTypes["PROCESS"] = new List<LLVMTypeRef> { GetInt1Type() };

        return processType;
    }

    private void EnsureRespawnGlobals()
    {
        if (_respawnGlobalsInitialized) return;
        _respawnGlobalsInitialized = true;

        var i8PtrPtr = GetPointerType(GetPointerType(GetInt8Type()));

        _globalRespawnArgc = _module.AddGlobal(GetInt32Type(), "__zv_respawn_argc");
        _globalRespawnArgc.Initializer = LLVMValueRef.CreateConstInt(GetInt32Type(), 0);
        _globalRespawnArgc.Linkage = LLVMLinkage.LLVMInternalLinkage;

        _globalRespawnArgv = _module.AddGlobal(i8PtrPtr, "__zv_respawn_argv");
        _globalRespawnArgv.Initializer = LLVMValueRef.CreateConstNull(i8PtrPtr);
        _globalRespawnArgv.Linkage = LLVMLinkage.LLVMInternalLinkage;

        _globalRespawnIsChild = _module.AddGlobal(GetInt1Type(), "__zv_respawn_is_child");
        _globalRespawnIsChild.Initializer = LLVMValueRef.CreateConstInt(GetInt1Type(), 0);
        _globalRespawnIsChild.Linkage = LLVMLinkage.LLVMInternalLinkage;
    }

    // Called once, at the very start of @entry, with the raw argc/argv the OS gave this
    // process. Stashes them in globals (so respawn() can reach them from anywhere in the
    // program, not just from entry's locals) and detects whether this process is itself a
    // respawned child (its last argv entry is the internal marker). Returns that i1 flag so
    // the caller can also hide the marker from the user-visible args array/length.
    private LLVMValueRef SetupRespawnEntryState(LLVMValueRef argcValue, LLVMValueRef argvValue)
    {
        EnsureRespawnGlobals();

        var ptrType = GetPointerType(GetInt8Type());
        _builder.BuildStore(argcValue, _globalRespawnArgc);
        _builder.BuildStore(argvValue, _globalRespawnArgv);

        var function = _builder.InsertBlock.Parent;
        var checkBB = _context.AppendBasicBlock(function, "respawn_check_sentinel");
        var mergeBB = _context.AppendBasicBlock(function, "respawn_check_done");

        var isChildAlloca = BuildEntryAlloca(GetInt1Type(), "respawn_is_child_tmp");
        _builder.BuildStore(LLVMValueRef.CreateConstInt(GetInt1Type(), 0), isChildAlloca);

        var zero = LLVMValueRef.CreateConstInt(GetInt32Type(), 0);
        var hasArgs = _builder.BuildICmp(LLVMIntPredicate.LLVMIntSGT, argcValue, zero, "respawn_has_args");
        _builder.BuildCondBr(hasArgs, checkBB, mergeBB);

        _builder.PositionAtEnd(checkBB);
        var argcMinus1 = _builder.BuildSub(argcValue, LLVMValueRef.CreateConstInt(GetInt32Type(), 1), "argc_m1");
        var lastArgSlot = _builder.BuildGEP2(ptrType, argvValue, new[] { argcMinus1 }, "last_arg_slot");
        var lastArg = _builder.BuildLoad2(ptrType, lastArgSlot, "last_arg");
        var sentinel = GetOrCreateGlobalStringPtr(RespawnSentinelArg, "respawn_sentinel");
        var strcmpFunc = GetOrAddFunction("strcmp", GetInt32Type(), new[] { ptrType, ptrType });
        var cmp = _builder.BuildCall2(_functionTypes["strcmp"], strcmpFunc, new[] { lastArg, sentinel }, "sentinel_cmp");
        var isMatch = _builder.BuildICmp(LLVMIntPredicate.LLVMIntEQ, cmp, LLVMValueRef.CreateConstInt(GetInt32Type(), 0), "sentinel_match");
        _builder.BuildStore(isMatch, isChildAlloca);
        _builder.BuildBr(mergeBB);

        _builder.PositionAtEnd(mergeBB);
        var isChild = _builder.BuildLoad2(GetInt1Type(), isChildAlloca, "respawn_is_child");
        _builder.BuildStore(isChild, _globalRespawnIsChild);
        return isChild;
    }

    // respawn()-only builtins (and exit()) need a real OS process to make sense; reject them
    // outright on a freestanding/kernel target rather than emitting nonsensical IR.
    private void CheckHostedBuiltinAvailable(string name)
    {
        if (IsFreestandingTarget)
        {
            throw new Exception($"'{name}' requires a hosted OS process and is not available when targeting a " +
                                 "freestanding/kernel target (e.g. 'os-x86').");
        }
    }

    private LLVMValueRef GenerateRespawnCall(List<Expression> arguments)
    {
        CheckHostedBuiltinAvailable("respawn");
        if (arguments.Count != 0)
            throw new Exception("respawn() takes no arguments.");

        EnsureRespawnGlobals();
        var processType = GetProcessType();
        var resultAlloca = BuildEntryAlloca(processType, "respawn_result");
        var isChild = _builder.BuildLoad2(GetInt1Type(), _globalRespawnIsChild, "respawn_is_child_flag");

        var function = _builder.InsertBlock.Parent;
        var childBB = _context.AppendBasicBlock(function, "respawn_is_child");
        var parentBB = _context.AppendBasicBlock(function, "respawn_is_parent");
        var mergeBB = _context.AppendBasicBlock(function, "respawn_done");

        _builder.BuildCondBr(isChild, childBB, parentBB);

        _builder.PositionAtEnd(childBB);
        StoreProcessChildFlag(resultAlloca, processType, true);
        _builder.BuildBr(mergeBB);

        _builder.PositionAtEnd(parentBB);
        EmitSpawnChildProcess();
        StoreProcessChildFlag(resultAlloca, processType, false);
        _builder.BuildBr(mergeBB);

        _builder.PositionAtEnd(mergeBB);
        return _builder.BuildLoad2(processType, resultAlloca, "respawn_result_val");
    }

    private void StoreProcessChildFlag(LLVMValueRef processAlloca, LLVMTypeRef processType, bool isChild)
    {
        var fieldPtr = _builder.BuildStructGEP2(processType, processAlloca, 0, "process_child_field");
        _builder.BuildStore(LLVMValueRef.CreateConstInt(GetInt1Type(), isChild ? 1u : 0u), fieldPtr);
    }

    // Builds a copy of the original argv with the respawn marker appended, then launches a
    // new process running this same executable with it - via fork()+execvp() on POSIX, or
    // _spawnvp() (which does the CreateProcess dance internally) on Windows. Both take the
    // same NULL-terminated argv array, so the array-building step is shared.
    private void EmitSpawnChildProcess()
    {
        var ptrType = GetPointerType(GetInt8Type());
        var argvPtrType = GetPointerType(ptrType);

        var argc = _builder.BuildLoad2(GetInt32Type(), _globalRespawnArgc, "orig_argc");
        var argv = _builder.BuildLoad2(argvPtrType, _globalRespawnArgv, "orig_argv");
        var argc64 = _builder.BuildSExt(argc, GetInt64Type(), "orig_argc64");

        // New argv: [argv[0..argc-1], "--zv-respawn-child", NULL] -> argc + 2 entries.
        var newCount = _builder.BuildAdd(argc64, LLVMValueRef.CreateConstInt(GetInt64Type(), 2), "new_argv_count");
        var elemSize = GetElementSize(ptrType);
        var totalBytes = _builder.BuildMul(newCount, elemSize, "new_argv_bytes");

        var malloc = GetOrAddFunction("malloc", ptrType, new[] { GetInt64Type() });
        var rawPtr = _builder.BuildCall2(_functionTypes["malloc"], malloc, new[] { totalBytes }, "new_argv_raw");
        EmitNullCheckOrThrow(rawPtr, "OutOfMemoryException: memory allocation failed");
        var newArgv = _builder.BuildBitCast(rawPtr, argvPtrType, "new_argv");

        var copyBytes = _builder.BuildMul(argc64, elemSize, "copy_bytes");
        var memcpy = GetOrAddFunction("memcpy", ptrType, new[] { ptrType, ptrType, GetInt64Type() });
        var argvI8 = _builder.BuildBitCast(argv, ptrType, "argv_i8");
        _builder.BuildCall2(_functionTypes["memcpy"], memcpy, new[] { rawPtr, argvI8, copyBytes }, "");

        var sentinelPtr = GetOrCreateGlobalStringPtr(RespawnSentinelArg, "respawn_sentinel_arg");
        var sentinelSlot = _builder.BuildGEP2(ptrType, newArgv, new[] { argc64 }, "new_argv_sentinel_slot");
        _builder.BuildStore(sentinelPtr, sentinelSlot);

        var nullIndex = _builder.BuildAdd(argc64, LLVMValueRef.CreateConstInt(GetInt64Type(), 1), "new_argv_null_index");
        var nullSlot = _builder.BuildGEP2(ptrType, newArgv, new[] { nullIndex }, "new_argv_null_slot");
        _builder.BuildStore(LLVMValueRef.CreateConstNull(ptrType), nullSlot);

        var arg0Slot = _builder.BuildGEP2(ptrType, argv, new[] { LLVMValueRef.CreateConstInt(GetInt64Type(), 0) }, "argv0_slot");
        var arg0 = _builder.BuildLoad2(ptrType, arg0Slot, "argv0");

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            // intptr_t _spawnvp(int mode, const char *cmdname, const char *const *argv);
            // _P_NOWAIT == 1: start the new process and return immediately.
            var spawnvpFunc = GetOrAddFunction("_spawnvp", GetInt64Type(), new[] { GetInt32Type(), ptrType, argvPtrType });
            var modeNoWait = LLVMValueRef.CreateConstInt(GetInt32Type(), 1);
            _builder.BuildCall2(_functionTypes["_spawnvp"], spawnvpFunc, new[] { modeNoWait, arg0, newArgv }, "spawnvp_result");
        }
        else
        {
            var forkFunc = GetOrAddFunction("fork", GetInt32Type(), Array.Empty<LLVMTypeRef>());
            var pid = _builder.BuildCall2(_functionTypes["fork"], forkFunc, Array.Empty<LLVMValueRef>(), "fork_pid");

            var function = _builder.InsertBlock.Parent;
            var forkChildBB = _context.AppendBasicBlock(function, "respawn_fork_child");
            var forkParentBB = _context.AppendBasicBlock(function, "respawn_fork_parent");

            var isForkChild = _builder.BuildICmp(LLVMIntPredicate.LLVMIntEQ, pid, LLVMValueRef.CreateConstInt(GetInt32Type(), 0), "is_fork_child");
            _builder.BuildCondBr(isForkChild, forkChildBB, forkParentBB);

            _builder.PositionAtEnd(forkChildBB);
            var execvpFunc = GetOrAddFunction("execvp", GetInt32Type(), new[] { ptrType, argvPtrType });
            _builder.BuildCall2(_functionTypes["execvp"], execvpFunc, new[] { arg0, newArgv }, "execvp_result");
            // execvp only returns on failure - bail out rather than continuing as a
            // duplicate, un-exec'd copy of the parent.
            var exitFunc = GetOrAddFunction("exit", GetVoidType(), new[] { GetInt32Type() });
            _builder.BuildCall2(_functionTypes["exit"], exitFunc, new[] { LLVMValueRef.CreateConstInt(GetInt32Type(), 127) }, "");
            _builder.BuildUnreachable();

            // Continue as the real parent.
            _builder.PositionAtEnd(forkParentBB);
        }

        EmitRawFreeCall(newArgv);
    }

    private LLVMValueRef GenerateExitCall(List<Expression> arguments)
    {
        CheckHostedBuiltinAvailable("exit");
        if (arguments.Count != 1)
            throw new Exception("exit() expects exactly 1 argument (exit code).");

        var code = VisitExpression(arguments[0]);
        code = ConvertToType(code, GetInt32Type());
        var exitFunc = GetOrAddFunction("exit", GetVoidType(), new[] { GetInt32Type() });
        _builder.BuildCall2(_functionTypes["exit"], exitFunc, new[] { code }, "");
        _builder.BuildUnreachable();

        // Unreachable, but callers of VisitExpression need some value back.
        return LLVMValueRef.CreateConstInt(GetInt32Type(), 0);
    }
}
