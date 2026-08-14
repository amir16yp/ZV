using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using LLVMSharp.Interop;
using ZV.Compiler.AST;
using ZV.Compiler.Lexer;

namespace ZV.Compiler.Backend;

// Cross-platform multithreading and concurrency builtins.
//
// Windows uses native Win32 threads (CreateThread / WaitForSingleObject / Sleep / mutex
// primitives). POSIX uses pthreads. The user-visible API is the same everywhere; the
// platform-specific code is selected when the compiler emits the IR.
//
// Thread and mutex handles are opaque PTR (void*) values that wrap a small heap-allocated
// platform object. Handles must be released explicitly with thread_join()/mutex_destroy().
//
// These require a hosted OS process, so they are rejected when targeting a freestanding
// kernel (e.g. 'os-x86').
public partial class LlvmGenerator
{
    private static readonly HashSet<string> ThreadBuiltinNames = new()
    {
        "thread_spawn", "thread_join", "thread_sleep_ms",
        "mutex_create", "mutex_lock", "mutex_unlock", "mutex_destroy"
    };

    private static readonly HashSet<string> AtomicBuiltinNames = BuildAtomicBuiltinNames();

    private static HashSet<string> BuildAtomicBuiltinNames()
    {
        var set = new HashSet<string>();
        var prefixes = new[] { "atomic_load_", "atomic_store_", "atomic_add_" };
        var suffixes = new[]
        {
            "int8", "uint8", "int16", "uint16", "int32", "uint32",
            "int64", "uint64", "int128", "uint128"
        };
        foreach (var prefix in prefixes)
        foreach (var suffix in suffixes)
            set.Add(prefix + suffix);
        return set;
    }

    private readonly Dictionary<string, LLVMValueRef> _threadWrappers = new();

    private LLVMValueRef GenerateThreadBuiltinCall(string name, List<Expression> arguments)
    {
        return name switch
        {
            "thread_spawn" => GenerateThreadSpawnCall(arguments),
            "thread_join" => GenerateThreadJoinCall(arguments),
            "thread_sleep_ms" => GenerateThreadSleepMsCall(arguments),
            "mutex_create" => GenerateMutexCreateCall(arguments),
            "mutex_lock" => GenerateMutexLockCall(arguments),
            "mutex_unlock" => GenerateMutexUnlockCall(arguments),
            "mutex_destroy" => GenerateMutexDestroyCall(arguments),
            _ when AtomicBuiltinNames.Contains(name) => GenerateAtomicBuiltinCall(name, arguments),
            _ => throw new Exception($"Unknown threading builtin: {name}")
        };
    }

    // thread_spawn(fn_name, arg) -> PTR
    //
    // Spawns a new OS thread that starts by calling the ZV function named `fn_name`.
    // The ZV function must have the signature:
    //     VOID fn_name(PTR arg)
    // `arg` is an opaque pointer passed unchanged to the worker. The returned handle must
    // be passed to thread_join() to reap the thread.
    private LLVMValueRef GenerateThreadSpawnCall(List<Expression> arguments)
    {
        CheckHostedBuiltinAvailable("thread_spawn");

        if (arguments.Count != 2)
            throw new Exception("thread_spawn() expects exactly 2 arguments: thread_spawn(\"function_name\", arg).");

        if (_builder.InsertBlock.Handle == IntPtr.Zero)
            throw new Exception("thread_spawn() is not allowed at global scope.");

        var fnNameExpr = arguments[0];
        if (fnNameExpr is not LiteralExpr { Type: TokenType.StringLiteral, Value: not null } lit)
            throw new Exception("thread_spawn() function name must be a string literal.");

        string fnName = lit.Value.ToString() ?? "";
        if (string.IsNullOrEmpty(fnName) || !_functionValues.TryGetValue(fnName, out var userFunc))
            throw new Exception($"thread_spawn(): unknown function '{fnName}'.");

        var userFuncType = _functionTypes[fnName];
        var userParamTypes = userFuncType.GetParamTypes();
        if (userParamTypes.Length != 1 || userParamTypes[0].Kind != LLVMTypeKind.LLVMPointerTypeKind)
            throw new Exception($"thread_spawn(): worker function '{fnName}' must take exactly one PTR argument.");

        var arg = VisitExpression(arguments[1]);
        arg = ConvertToType(arg, GetPointerType(GetInt8Type()));

        string wrapperName = $"__zv_thread_wrapper_{fnName}";
        var wrapper = GetOrAddThreadWrapper(fnName, userFunc, userFuncType, wrapperName);

        var ptrType = GetPointerType(GetInt8Type());
        var i32Type = GetInt32Type();
        var i64Type = GetInt64Type();

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            // HANDLE CreateThread(
            //   LPSECURITY_ATTRIBUTES lpThreadAttributes,
            //   SIZE_T dwStackSize,
            //   LPTHREAD_START_ROUTINE lpStartAddress,
            //   LPVOID lpParameter,
            //   DWORD dwCreationFlags,
            //   LPDWORD lpThreadId);
            var createThread = GetOrAddFunction("CreateThread", ptrType,
                new[] { ptrType, i64Type, ptrType, ptrType, i32Type, ptrType });

            var handlePtr = BuildHeapPointer(8, "thread_handle_ptr");

            var threadHandle = _builder.BuildCall2(
                _functionTypes["CreateThread"],
                createThread,
                new[]
                {
                    LLVMValueRef.CreateConstNull(ptrType),           // lpThreadAttributes
                    LLVMValueRef.CreateConstInt(i64Type, 0),         // dwStackSize
                    wrapper,                                          // lpStartAddress
                    arg,                                              // lpParameter
                    LLVMValueRef.CreateConstInt(i32Type, 0),         // dwCreationFlags
                    LLVMValueRef.CreateConstNull(ptrType)            // lpThreadId
                },
                "thread_handle");

            var threadNull = _builder.BuildICmp(LLVMIntPredicate.LLVMIntEQ, threadHandle,
                LLVMValueRef.CreateConstNull(ptrType), "thread_null");
            EmitCondThrow(threadNull, "ThreadException: failed to create thread");

            _builder.BuildStore(threadHandle, handlePtr);
            return handlePtr;
        }
        else
        {
            // int pthread_create(pthread_t* thread, const pthread_attr_t* attr,
            //                    void* (*start_routine)(void*), void* arg);
            var pthreadCreate = GetOrAddFunction("pthread_create", i32Type,
                new[] { ptrType, ptrType, ptrType, ptrType });

            var threadPtr = BuildHeapPointer(8, "pthread_t_ptr");

            var result = _builder.BuildCall2(
                _functionTypes["pthread_create"],
                pthreadCreate,
                new[]
                {
                    threadPtr,
                    LLVMValueRef.CreateConstNull(ptrType), // attr
                    wrapper,                                  // start_routine
                    arg                                       // arg
                },
                "pthread_create_result");

            EmitNonZeroCheckOrThrow(result, "ThreadException: failed to create thread");
            return threadPtr;
        }
    }

    private LLVMValueRef GetOrAddThreadWrapper(string fnName, LLVMValueRef userFunc,
        LLVMTypeRef userFuncType, string wrapperName)
    {
        if (_threadWrappers.TryGetValue(wrapperName, out var cached))
            return cached;

        var ptrType = GetPointerType(GetInt8Type());
        var i32Type = GetInt32Type();

        bool windows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
        LLVMTypeRef wrapperType = windows
            ? LLVMTypeRef.CreateFunction(i32Type, new[] { ptrType })
            : LLVMTypeRef.CreateFunction(ptrType, new[] { ptrType });

        var wrapper = _module.AddFunction(wrapperName, wrapperType);
        _functionTypes[wrapperName] = wrapperType;

        var savedBlock = _builder.InsertBlock;
        var entry = _context.AppendBasicBlock(wrapper, "entry");
        _builder.PositionAtEnd(entry);

        var arg = wrapper.GetParam(0);
        arg.Name = "arg";

        var userParamTypes = userFuncType.GetParamTypes();
        LLVMValueRef callArg = arg;
        if (userParamTypes[0].Handle != ptrType.Handle)
            callArg = _builder.BuildBitCast(arg, userParamTypes[0], "arg_cast");

        _builder.BuildCall2(userFuncType, userFunc, new[] { callArg }, "");

        if (windows)
            _builder.BuildRet(LLVMValueRef.CreateConstInt(i32Type, 0));
        else
            _builder.BuildRet(LLVMValueRef.CreateConstNull(ptrType));

        if (savedBlock.Handle != IntPtr.Zero)
            _builder.PositionAtEnd(savedBlock);

        _threadWrappers[wrapperName] = wrapper;
        return wrapper;
    }

    private LLVMValueRef GenerateThreadJoinCall(List<Expression> arguments)
    {
        CheckHostedBuiltinAvailable("thread_join");

        if (arguments.Count != 1)
            throw new Exception("thread_join() expects exactly 1 argument (thread handle).");

        var handle = VisitExpression(arguments[0]);
        handle = ConvertToType(handle, GetPointerType(GetInt8Type()));

        var ptrType = GetPointerType(GetInt8Type());
        var i32Type = GetInt32Type();

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            var wait = GetOrAddFunction("WaitForSingleObject", i32Type, new[] { ptrType, i32Type });
            var loaded = _builder.BuildLoad2(ptrType, handle, "thread_handle");
            var result = _builder.BuildCall2(
                _functionTypes["WaitForSingleObject"],
                wait,
                new[] { loaded, LLVMValueRef.CreateConstInt(i32Type, unchecked((uint)-1)) }, // INFINITE
                "wait_result");

            var isFailed = _builder.BuildICmp(LLVMIntPredicate.LLVMIntNE, result,
                LLVMValueRef.CreateConstInt(i32Type, 0), "join_failed");
            EmitCondThrow(isFailed, "ThreadException: failed to join thread");

            var closeHandle = GetOrAddFunction("CloseHandle", i32Type, new[] { ptrType });
            var closeResult = _builder.BuildCall2(
                _functionTypes["CloseHandle"], closeHandle, new[] { loaded }, "close_result");
            var closeFailed = _builder.BuildICmp(LLVMIntPredicate.LLVMIntEQ, closeResult,
                LLVMValueRef.CreateConstInt(i32Type, 0), "close_failed");
            EmitCondThrow(closeFailed, "ThreadException: failed to close thread handle");
        }
        else
        {
            var pthreadJoin = GetOrAddFunction("pthread_join", i32Type, new[] { ptrType, ptrType });
            var loaded = _builder.BuildLoad2(ptrType, handle, "pthread_t");
            var result = _builder.BuildCall2(
                _functionTypes["pthread_join"],
                pthreadJoin,
                new[] { loaded, LLVMValueRef.CreateConstNull(ptrType) },
                "pthread_join_result");

            EmitNonZeroCheckOrThrow(result, "ThreadException: failed to join thread");
        }

        EmitRawFreeCall(handle);
        return LLVMValueRef.CreateConstInt(GetInt32Type(), 0);
    }

    private LLVMValueRef GenerateThreadSleepMsCall(List<Expression> arguments)
    {
        CheckHostedBuiltinAvailable("thread_sleep_ms");

        if (arguments.Count != 1)
            throw new Exception("thread_sleep_ms() expects exactly 1 argument (milliseconds).");

        var ms = VisitExpression(arguments[0]);
        ms = ConvertToType(ms, GetInt32Type());

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            var sleep = GetOrAddFunction("Sleep", GetVoidType(), new[] { GetInt32Type() });
            _builder.BuildCall2(_functionTypes["Sleep"], sleep, new[] { ms }, "");
        }
        else
        {
            var usleep = GetOrAddFunction("usleep", GetInt32Type(), new[] { GetInt32Type() });
            var usec = _builder.BuildMul(ms, LLVMValueRef.CreateConstInt(GetInt32Type(), 1000), "usec");
            _builder.BuildCall2(_functionTypes["usleep"], usleep, new[] { usec }, "");
        }

        return LLVMValueRef.CreateConstInt(GetInt32Type(), 0);
    }

    private LLVMValueRef GenerateMutexCreateCall(List<Expression> arguments)
    {
        CheckHostedBuiltinAvailable("mutex_create");

        if (arguments.Count != 0)
            throw new Exception("mutex_create() takes no arguments.");

        var ptrType = GetPointerType(GetInt8Type());
        var i32Type = GetInt32Type();

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            var createMutex = GetOrAddFunction("CreateMutexA", ptrType,
                new[] { ptrType, i32Type, ptrType });

            var handlePtr = BuildHeapPointer(8, "mutex_handle_ptr");
            var mutexHandle = _builder.BuildCall2(
                _functionTypes["CreateMutexA"],
                createMutex,
                new[]
                {
                    LLVMValueRef.CreateConstNull(ptrType), // lpMutexAttributes
                    LLVMValueRef.CreateConstInt(i32Type, 0), // bInitialOwner
                    LLVMValueRef.CreateConstNull(ptrType)  // lpName
                },
                "mutex_handle");

            var mutexNull = _builder.BuildICmp(LLVMIntPredicate.LLVMIntEQ, mutexHandle,
                LLVMValueRef.CreateConstNull(ptrType), "mutex_null");
            EmitCondThrow(mutexNull, "MutexException: failed to create mutex");

            _builder.BuildStore(mutexHandle, handlePtr);
            return handlePtr;
        }
        else
        {
            // pthread_mutex_t is small (<= 64 bytes on common ABIs); over-allocate so the
            // same IR works across Linux, macOS, etc.
            var mutexPtr = BuildHeapPointer(128, "pthread_mutex_ptr");
            var pthreadMutexInit = GetOrAddFunction("pthread_mutex_init", i32Type,
                new[] { ptrType, ptrType });

            var result = _builder.BuildCall2(
                _functionTypes["pthread_mutex_init"],
                pthreadMutexInit,
                new[] { mutexPtr, LLVMValueRef.CreateConstNull(ptrType) },
                "pthread_mutex_init_result");

            EmitNonZeroCheckOrThrow(result, "MutexException: failed to create mutex");
            return mutexPtr;
        }
    }

    private LLVMValueRef GenerateMutexLockCall(List<Expression> arguments)
    {
        CheckHostedBuiltinAvailable("mutex_lock");

        if (arguments.Count != 1)
            throw new Exception("mutex_lock() expects exactly 1 argument (mutex handle).");

        var handle = VisitExpression(arguments[0]);
        handle = ConvertToType(handle, GetPointerType(GetInt8Type()));

        var ptrType = GetPointerType(GetInt8Type());
        var i32Type = GetInt32Type();

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            var loaded = _builder.BuildLoad2(ptrType, handle, "mutex_handle");
            var wait = GetOrAddFunction("WaitForSingleObject", i32Type, new[] { ptrType, i32Type });
            var result = _builder.BuildCall2(
                _functionTypes["WaitForSingleObject"],
                wait,
                new[] { loaded, LLVMValueRef.CreateConstInt(i32Type, unchecked((uint)-1)) }, // INFINITE
                "mutex_lock_result");

            var isFailed = _builder.BuildICmp(LLVMIntPredicate.LLVMIntNE, result,
                LLVMValueRef.CreateConstInt(i32Type, 0), "mutex_lock_failed");
            EmitCondThrow(isFailed, "MutexException: failed to lock mutex");
        }
        else
        {
            var pthreadMutexLock = GetOrAddFunction("pthread_mutex_lock", i32Type, new[] { ptrType });
            var result = _builder.BuildCall2(
                _functionTypes["pthread_mutex_lock"],
                pthreadMutexLock,
                new[] { handle },
                "pthread_mutex_lock_result");

            EmitNonZeroCheckOrThrow(result, "MutexException: failed to lock mutex");
        }

        return LLVMValueRef.CreateConstInt(GetInt32Type(), 0);
    }

    private LLVMValueRef GenerateMutexUnlockCall(List<Expression> arguments)
    {
        CheckHostedBuiltinAvailable("mutex_unlock");

        if (arguments.Count != 1)
            throw new Exception("mutex_unlock() expects exactly 1 argument (mutex handle).");

        var handle = VisitExpression(arguments[0]);
        handle = ConvertToType(handle, GetPointerType(GetInt8Type()));

        var ptrType = GetPointerType(GetInt8Type());
        var i32Type = GetInt32Type();

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            var loaded = _builder.BuildLoad2(ptrType, handle, "mutex_handle");
            var release = GetOrAddFunction("ReleaseMutex", i32Type, new[] { ptrType });
            var result = _builder.BuildCall2(
                _functionTypes["ReleaseMutex"],
                release,
                new[] { loaded },
                "mutex_unlock_result");

            var unlockFailed = _builder.BuildICmp(LLVMIntPredicate.LLVMIntEQ, result,
                LLVMValueRef.CreateConstInt(i32Type, 0), "mutex_unlock_failed");
            EmitCondThrow(unlockFailed, "MutexException: failed to unlock mutex");
        }
        else
        {
            var pthreadMutexUnlock = GetOrAddFunction("pthread_mutex_unlock", i32Type, new[] { ptrType });
            var result = _builder.BuildCall2(
                _functionTypes["pthread_mutex_unlock"],
                pthreadMutexUnlock,
                new[] { handle },
                "pthread_mutex_unlock_result");

            EmitNonZeroCheckOrThrow(result, "MutexException: failed to unlock mutex");
        }

        return LLVMValueRef.CreateConstInt(GetInt32Type(), 0);
    }

    private LLVMValueRef GenerateMutexDestroyCall(List<Expression> arguments)
    {
        CheckHostedBuiltinAvailable("mutex_destroy");

        if (arguments.Count != 1)
            throw new Exception("mutex_destroy() expects exactly 1 argument (mutex handle).");

        var handle = VisitExpression(arguments[0]);
        handle = ConvertToType(handle, GetPointerType(GetInt8Type()));

        var ptrType = GetPointerType(GetInt8Type());
        var i32Type = GetInt32Type();

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            var loaded = _builder.BuildLoad2(ptrType, handle, "mutex_handle");
            var closeHandle = GetOrAddFunction("CloseHandle", i32Type, new[] { ptrType });
            var result = _builder.BuildCall2(
                _functionTypes["CloseHandle"],
                closeHandle,
                new[] { loaded },
                "mutex_close_result");

            var destroyFailed = _builder.BuildICmp(LLVMIntPredicate.LLVMIntEQ, result,
                LLVMValueRef.CreateConstInt(i32Type, 0), "mutex_destroy_failed");
            EmitCondThrow(destroyFailed, "MutexException: failed to destroy mutex");
        }
        else
        {
            var pthreadMutexDestroy = GetOrAddFunction("pthread_mutex_destroy", i32Type, new[] { ptrType });
            var result = _builder.BuildCall2(
                _functionTypes["pthread_mutex_destroy"],
                pthreadMutexDestroy,
                new[] { handle },
                "pthread_mutex_destroy_result");

            EmitNonZeroCheckOrThrow(result, "MutexException: failed to destroy mutex");
        }

        EmitRawFreeCall(handle);
        return LLVMValueRef.CreateConstInt(GetInt32Type(), 0);
    }

    // Low-level atomic operations on all integer types.
    // Supported names: atomic_load_<type>, atomic_store_<type>, atomic_add_<type>
    // where <type> is one of: int8, uint8, int16, uint16, int32, uint32,
    // int64, uint64, int128, uint128.
    //
    // The pointer argument must point to a naturally-aligned value of that type.
    // These compile to LLVM atomicrmw instructions and work on both Windows and POSIX.
    private LLVMValueRef GenerateAtomicBuiltinCall(string name, List<Expression> arguments)
    {
        var prefix = name.StartsWith("atomic_load_") ? "atomic_load_"
                   : name.StartsWith("atomic_store_") ? "atomic_store_"
                   : name.StartsWith("atomic_add_") ? "atomic_add_"
                   : throw new Exception($"Unknown atomic builtin: {name}");

        var suffix = name[prefix.Length..];
        var elementType = GetAtomicTypeFromSuffix(suffix);
        var typedPtrType = GetPointerType(elementType);

        switch (prefix)
        {
            case "atomic_load_":
            {
                if (arguments.Count != 1)
                    throw new Exception($"{name}() expects exactly 1 argument (pointer).");

                var ptr = VisitExpression(arguments[0]);
                ptr = ConvertToType(ptr, GetPointerType(GetInt8Type()));
                var typedPtr = _builder.BuildBitCast(ptr, typedPtrType, "atomic_ptr");
                var zero = LLVMValueRef.CreateConstInt(elementType, 0);

                return _builder.BuildAtomicRMW(
                    LLVMAtomicRMWBinOp.LLVMAtomicRMWBinOpAdd,
                    typedPtr,
                    zero,
                    LLVMAtomicOrdering.LLVMAtomicOrderingSequentiallyConsistent,
                    false);
            }
            case "atomic_store_":
            {
                if (arguments.Count != 2)
                    throw new Exception($"{name}() expects exactly 2 arguments (pointer, value).");

                var ptr = VisitExpression(arguments[0]);
                ptr = ConvertToType(ptr, GetPointerType(GetInt8Type()));
                var typedPtr = _builder.BuildBitCast(ptr, typedPtrType, "atomic_ptr");

                var value = VisitExpression(arguments[1]);
                value = ConvertToType(value, elementType);

                _builder.BuildAtomicRMW(
                    LLVMAtomicRMWBinOp.LLVMAtomicRMWBinOpXchg,
                    typedPtr,
                    value,
                    LLVMAtomicOrdering.LLVMAtomicOrderingSequentiallyConsistent,
                    false);

                return LLVMValueRef.CreateConstInt(GetInt32Type(), 0);
            }
            case "atomic_add_":
            {
                if (arguments.Count != 2)
                    throw new Exception($"{name}() expects exactly 2 arguments (pointer, value).");

                var ptr = VisitExpression(arguments[0]);
                ptr = ConvertToType(ptr, GetPointerType(GetInt8Type()));
                var typedPtr = _builder.BuildBitCast(ptr, typedPtrType, "atomic_ptr");

                var value = VisitExpression(arguments[1]);
                value = ConvertToType(value, elementType);

                return _builder.BuildAtomicRMW(
                    LLVMAtomicRMWBinOp.LLVMAtomicRMWBinOpAdd,
                    typedPtr,
                    value,
                    LLVMAtomicOrdering.LLVMAtomicOrderingSequentiallyConsistent,
                    false);
            }
            default:
                throw new Exception($"Unknown atomic builtin: {name}");
        }
    }

    private LLVMTypeRef GetAtomicTypeFromSuffix(string suffix)
    {
        return suffix switch
        {
            "int8" or "uint8" => GetInt8Type(),
            "int16" or "uint16" => GetInt16Type(),
            "int32" or "uint32" => GetInt32Type(),
            "int64" or "uint64" => GetInt64Type(),
            "int128" or "uint128" => GetInt128Type(),
            _ => throw new Exception($"Unsupported atomic type suffix: {suffix}")
        };
    }

    // Allocates `bytes` bytes from the C heap and returns the resulting i8* pointer.
    private LLVMValueRef BuildHeapPointer(ulong bytes, string name)
    {
        var malloc = GetOrAddFunction("malloc", GetPointerType(GetInt8Type()),
            new[] { GetInt64Type() });
        var size = LLVMValueRef.CreateConstInt(GetInt64Type(), bytes);
        var ptr = _builder.BuildCall2(_functionTypes["malloc"], malloc, new[] { size }, name);
        EmitNullCheckOrThrow(ptr, "OutOfMemoryException: failed to allocate threading handle");
        return ptr;
    }
}
