using System;
using System.Collections.Generic;
using LLVMSharp.Interop;
using ZV.Compiler.AST;
using ZV.Compiler.Lexer;

namespace ZV.Compiler.Backend;

public partial class LlvmGenerator : IDisposable
{
    private readonly LLVMContextRef _context;
    private readonly LLVMModuleRef _module;
    private readonly LLVMBuilderRef _builder;
    private readonly Dictionary<string, (LLVMValueRef Value, LLVMTypeRef Type, string? StructName)> _namedValues = new();
    private readonly Dictionary<string, LLVMTypeRef> _functionTypes = new();
    private readonly Dictionary<string, LLVMValueRef> _functionValues = new();
    private readonly Dictionary<string, LLVMTypeRef> _structTypes = new();
    private readonly Dictionary<string, List<string>> _structFieldNames = new();
    private readonly Dictionary<string, List<LLVMTypeRef>> _structFieldTypes = new();

    // The declared TypeNode of each struct's fields, kept alongside the mapped LLVMTypeRef
    // because the LLVM type alone can't distinguish an owning kind (e.g. CSTRING) from a
    // structurally-identical non-owning one (e.g. PTR<VOID>) - both map to i8*.
    private readonly Dictionary<string, List<TypeNode>> _structFieldTypeNodes = new();

    // Struct names that transitively own heap memory: any field that is a dynamic array
    // (T[]), a CSTRING, or another owning struct. Assigning into such a field is treated
    // as an ownership transfer (like move()), so these fields are recursively destroyed
    // when the owning struct variable's scope ends, and copy() of such a struct is rejected
    // (see DestroyStructFields / VisitCopy).
    private readonly HashSet<string> _owningStructTypes = new();
    private readonly Dictionary<LLVMTypeRef, LLVMTypeRef> _arrayElementTypes = new();
    private readonly Dictionary<LLVMTypeRef, LLVMTypeRef> _arrayStructTypes = new();
    private readonly HashSet<string> _constVariables = new();
    private readonly HashSet<string> _externalLibraries = new();
    private readonly Stack<(LLVMBasicBlockRef EndBlock, LLVMBasicBlockRef ContinueBlock)> _loopTargets = new();
    private readonly Dictionary<string, LLVMTypeRef> _typeAliases = new();

    // Names of runtime exception "types" that may be used as a catch-clause filter and,
    // for user-declared ones, constructed via `Name("description")`. Pre-populated with
    // every exception name the compiler itself throws (see EmitCondThrow/EmitNullCheckOrThrow
    // call sites), plus anything declared with `exception Name;`.
    private readonly HashSet<string> _declaredExceptionTypes = new()
    {
        "IndexOutOfBoundsException",
        "OutOfMemoryException",
        "FileOpenException",
        "FileCloseException",
        "FileSeekException",
        "FileException",
        "FileRemoveException",
        "FileRenameException",
        "DirectoryException",
        "ArrayCopyException",
        "ThreadException",
        "MutexException",
    };

    // Default message expression for exception types declared with `exception Name =
    // <expr>;`, re-evaluated each time `Name()` or a bare `throw Name;` is used without
    // an explicit message. Keyed by declared exception type name.
    private readonly Dictionary<string, Expression> _exceptionDefaultMessages = new();
    private LLVMValueRef _entryFunction;
    private LLVMTypeRef _entryFunctionType;
    private LLVMTypeRef? _stringStructType;

    // Names declared via `newtype` (as opposed to the transparent `type` alias). A newtype
    // is backed by the same LLVM representation as its underlying type, but the compiler
    // enforces that it is not implicitly interchangeable with that underlying type or with
    // any other newtype - conversions across the boundary require an explicit `as` cast.
    private readonly HashSet<string> _newtypeNames = new();

    // Tracks the declared newtype identity (if any) of every variable/parameter currently
    // in scope, keyed by name. Null means "not a newtype" (plain primitive, struct, or
    // transparent `type` alias), which is not restricted.
    private readonly Dictionary<string, string?> _variableNewtypeNames = new();

    // Declared newtype identity of each function's parameters/return type, keyed by
    // function name, used to check call sites without re-parsing the declaration.
    private readonly Dictionary<string, List<string?>> _functionParamNewtypes = new();
    private readonly Dictionary<string, string?> _functionReturnNewtype = new();

    // The declared newtype identity of the return type of the function currently being
    // generated, used to check `return` statements.
    private string? _currentFunctionReturnNewtype;

    // Declared TypeNode of every variable/parameter currently in scope, keyed by name, and
    // declared return TypeNode of every function, keyed by function name. LLVM's integer
    // types (i8, i16, ...) don't carry signedness - UINT8 and INT8 both map to i8 - so this
    // is the only way to recover whether a given value's *declared ZV type* is unsigned,
    // which matters for `as`-cast extension (zext vs. sext) and for signed vs. unsigned
    // division/remainder/comparison. See InferExprTypeNode/IsUnsignedPrimitiveTypeNode.
    private readonly Dictionary<string, TypeNode> _variableDeclaredTypeNodes = new();
    private readonly Dictionary<string, TypeNode> _functionReturnTypeNodes = new();

    // Scope-based deterministic cleanup: each scope tracks local variables that own
    // a heap allocation. When the scope ends, those allocations are freed automatically.
    // This is C++-style RAII, not garbage collection.
    private class Scope
    {
        public List<string> OwnedVariables { get; } = new();

        // Runtime values of the cleanup-stack top at the point this scope was entered,
        // stored in allocas so they survive to scope exit even with control flow.
        public LLVMValueRef? SavedCleanupHead { get; set; }
        public LLVMValueRef? SavedCleanupUsed { get; set; }
    }

    private readonly List<Scope> _scopes = new();
    private readonly List<int> _loopStartScopeDepths = new();

    // Names of variables that currently own a heap allocation and need to be freed
    // when their scope ends (or when ownership is transferred/returned). Not readonly:
    // VisitIf swaps this out per-branch and merges the results afterward (see VisitIf).
    private HashSet<string> _ownedVariables = new();

    // Maps a variable name to the index of the scope where it was declared.
    // Owned allocations are tracked in their declaration scope, not the scope where
    // an assignment happens, so a variable assigned inside an inner block is not
    // freed prematurely when that inner block ends.
    private readonly Dictionary<string, int> _variableDeclScope = new();

    // Anonymous CSTRING allocations produced by cstr() while evaluating the statement
    // currently being generated. If a cstr() result is bound directly to a variable it is
    // "claimed" (removed from this list) and tracked as an owned variable instead; anything
    // left over once the statement finishes is an unclaimed temporary and is freed
    // automatically (see FreeUnclaimedCstrTemps).
    private readonly List<LLVMValueRef> _pendingCstrTemps = new();

    // Cache of compiler-generated NUL-terminated string globals so that identical strings
    // (runtime error messages, exception type prefixes, etc.) are emitted only once per
    // module instead of once per use site.
    private readonly Dictionary<string, LLVMValueRef> _globalStringCache = new();

    // True while generating a function body. Used to avoid treating the module-level
    // builder state as dead code after a function has been emitted.
    private bool _inFunctionBody;

    // Tracks nesting depth of `unsafe { }` blocks. While > 0, operations that are
    // normally rejected or bounds-checked (raw pointer indexing, pointer<->integer
    // casts, unchecked array access) are permitted without a runtime/compile-time guard.
    private int _unsafeDepth;
    private bool InUnsafeContext => _unsafeDepth > 0;

    // Basic ownership/lifetime tracking: names of variables that have been free()'d or
    // move()'d away. Using one afterwards is a compile error. Not a full borrow checker,
    // but it catches straight-line use-after-free/double-free/double-move bugs, and
    // VisitIf gives it basic per-branch dataflow merging across if/else (see VisitIf).
    // Loops are still flow-insensitive: anything freed/moved inside a loop body stays
    // dead afterward, which is the conservative-but-correct answer for a body that may
    // run zero or more times. Reassigning a variable revives it.
    // Not readonly: VisitIf swaps this out per-branch and merges the results afterward.
    private HashSet<string> _deadVariables = new();

    private void CheckVariableAlive(string name, SourceLocation location)
    {
        if (_deadVariables.Contains(name))
        {
            throw new CompileException(location, $"'{name}' was already freed or moved and cannot be used again.");
        }
    }

    // Exception handling runtime state
    private LLVMTypeRef? _exceptionType;
    private LLVMValueRef _globalExceptionMsg;
    private LLVMValueRef _globalExceptionActive;
    private bool _exceptionGlobalsInitialized;

    // Thread-local chunked cleanup stack for RAII. Each chunk holds object/destructor pairs;
    // used both for normal scope exit and for exception unwinding across function frames.
    private const int CleanupChunkCapacity = 256;
    private LLVMTypeRef? _cleanupChunkType;
    private LLVMValueRef _cleanupHeadGlobal;
    private LLVMValueRef _cleanupUsedGlobal;
    private LLVMValueRef _cleanupFreeGlobal;
    private bool _cleanupGlobalsInitialized;

    // jmp_buf augmented with the cleanup-stack top (chunk head + used count) at the time of
    // setjmp, so a throw can destroy everything pushed since the matching try block began.
    private LLVMTypeRef? _jmpBufType;

    // Cache of generated destructor functions keyed by a canonical type identifier.
    private readonly Dictionary<string, LLVMValueRef> _destructorFunctions = new();

    // Runtime cleanup-stack location (chunk head + index within chunk) for each owning
    // variable currently in scope. Used to skip the destructor call when ownership is
    // returned to the caller.
    private readonly Dictionary<string, (LLVMValueRef Head, LLVMValueRef Used)> _variableCleanupIndices = new();

    // Set by the driver (Program.cs) when compiling for a freestanding/kernel target
    // (e.g. "os-x86"). Builtins that require a hosted OS (like curses) are rejected
    // when this is set.
    public bool IsFreestandingTarget { get; set; }

    // Set by the driver (Program.cs) when compiling for a shared library target
    // (e.g. "lib"). Functions not marked with the `export` keyword get internal
    // linkage so they aren't visible outside the resulting DLL/SO, while exported
    // functions get external linkage plus a DLL export storage class (honored on
    // Windows/PE, ignored on ELF).
    public bool IsLibraryTarget { get; set; }

    public LlvmGenerator(string moduleName)
    {
        _context = LLVMContextRef.Create();
        _module = _context.CreateModuleWithName(moduleName);
        _builder = _context.CreateBuilder();
    }

    private LLVMTypeRef GetVoidType() => _context.VoidType;
    private LLVMTypeRef GetInt1Type() => _context.Int1Type;
    private LLVMTypeRef GetInt8Type() => _context.Int8Type;
    private LLVMTypeRef GetInt16Type() => _context.Int16Type;
    private LLVMTypeRef GetInt32Type() => _context.Int32Type;
    private LLVMTypeRef GetInt64Type() => _context.Int64Type;
    private LLVMTypeRef GetInt128Type() => _context.GetIntType(128);
    private LLVMTypeRef GetFloatType() => _context.FloatType;
    private LLVMTypeRef GetDoubleType() => _context.DoubleType;
    private LLVMTypeRef GetPointerType(LLVMTypeRef elementType) => LLVMTypeRef.CreatePointer(elementType, 0);

    private LLVMTypeRef GetArrayStructType(LLVMTypeRef elementType)
    {
        // { T*, i64 }
        if (_arrayStructTypes.TryGetValue(elementType, out var cached))
        {
            return cached;
        }

        var elementPtrType = GetPointerType(elementType);
        var lengthType = GetInt64Type();
        var structType = _context.CreateNamedStruct("array_" + elementType.Handle.ToString("X"));
        if (structType.StructElementTypesCount == 0)
        {
            structType.StructSetBody(new[] { elementPtrType, lengthType }, false);
        }
        _arrayElementTypes[structType] = elementType;
        _arrayStructTypes[elementType] = structType;
        return structType;
    }

    private LLVMTypeRef GetStringStructType()
    {
        if (_stringStructType.HasValue)
            return _stringStructType.Value;

        var dataPtrType = GetPointerType(GetInt8Type());
        var lengthType = GetInt64Type();
        var structType = _context.CreateNamedStruct("STRING");
        if (structType.StructElementTypesCount == 0)
        {
            structType.StructSetBody(new[] { dataPtrType, lengthType }, false);
        }
        _stringStructType = structType;
        return structType;
    }

    private bool IsStringStructType(LLVMTypeRef type)
    {
        return type.Kind == LLVMTypeKind.LLVMStructTypeKind &&
               _stringStructType.HasValue &&
               type.Handle == _stringStructType.Value.Handle;
    }

    public void Generate(List<Statement> statements)
    {
        foreach (var statement in statements)
        {
            VisitStatement(statement);
        }
    }

    public void EmitToFile(string fileName)
    {
        _module.Verify(LLVMVerifierFailureAction.LLVMPrintMessageAction);
        _module.PrintToFile(fileName);
    }

    public string EmitToString() => _module.PrintToString();

    // Runs LLVM's module verifier without printing to stderr or aborting, for callers
    // (tests) that want to assert on validity directly rather than just inspecting the
    // textual IR - EmitToString() alone won't catch things like a type created in the
    // wrong LLVMContextRef, which only the verifier flags.
    public bool TryVerify(out string message) => _module.TryVerify(LLVMVerifierFailureAction.LLVMReturnStatusAction, out message);

    public IEnumerable<string> GetExternalLibraries() => _externalLibraries;

    private void VisitStatement(Statement stmt)
    {
        // Skip unreachable statements after the current basic block has been terminated.
        if (_inFunctionBody)
        {
            var currentBlock = _builder.InsertBlock;
            if (currentBlock.Handle != IntPtr.Zero)
            {
                var lastInst = currentBlock.LastInstruction;
                if (lastInst.Handle != IntPtr.Zero && lastInst.IsATerminatorInst.Handle != IntPtr.Zero)
                {
                    return;
                }
            }
        }

        try
        {
            int cstrMark = _pendingCstrTemps.Count;
            switch (stmt)
            {
            case ExternDeclStmt externDecl:
                VisitExternDecl(externDecl);
                break;
            case FunctionDeclStmt funcDecl:
                VisitFunctionDecl(funcDecl);
                break;
            case ExpressionStmt exprStmt:
                VisitExpression(exprStmt.Expression);
                break;
            case VarDeclStmt varDecl:
                VisitVarDecl(varDecl);
                break;
            case IfStmt ifStmt:
                VisitIf(ifStmt);
                break;
            case WhileStmt whileStmt:
                VisitWhile(whileStmt);
                break;
            case ForStmt forStmt:
                VisitFor(forStmt);
                break;
            case StructDeclStmt structDecl:
                VisitStructDecl(structDecl);
                break;
            case FreeStmt freeStmt:
                VisitFree(freeStmt);
                break;
            case ReturnStmt retStmt:
                VisitReturn(retStmt);
                break;
            case BreakStmt breakStmt:
                VisitBreak(breakStmt);
                break;
            case ContinueStmt continueStmt:
                VisitContinue(continueStmt);
                break;
            case BlockStmt blockStmt:
                EnterScope();
                foreach (var s in blockStmt.Statements) VisitStatement(s);
                LeaveScope();
                break;
            case TryCatchStmt tryCatch:
                VisitTryCatch(tryCatch);
                break;
            case ThrowStmt throwStmt:
                VisitThrow(throwStmt);
                break;
            case TypeAliasStmt typeAlias:
                VisitTypeAlias(typeAlias);
                break;
            case ExceptionTypeDeclStmt exceptionTypeDecl:
                VisitExceptionTypeDecl(exceptionTypeDecl);
                break;
            case UnsafeStmt unsafeStmt:
                VisitUnsafe(unsafeStmt);
                break;
            default:
                throw new NotImplementedException($"Statement type {stmt.GetType().Name} not implemented.");
        }
            FreeUnclaimedCstrTemps(cstrMark);
        }
        catch (CompileException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new CompileException(stmt.Location, ex.Message, ex);
        }
    }

    private void EnterScope()
    {
        var savedHead = _builder.BuildAlloca(GetPointerType(GetInt8Type()), "cleanup_head_save");
        var savedUsed = _builder.BuildAlloca(GetInt32Type(), "cleanup_used_save");
        var (head, used) = BuildCleanupTopLoad();
        _builder.BuildStore(head, savedHead);
        _builder.BuildStore(used, savedUsed);
        _scopes.Add(new Scope { SavedCleanupHead = savedHead, SavedCleanupUsed = savedUsed });
    }

    private void LeaveScope()
    {
        if (_scopes.Count == 0) return;
        var scope = _scopes[_scopes.Count - 1];
        _scopes.RemoveAt(_scopes.Count - 1);

        bool canEmit = true;
        var currentBlock = _builder.InsertBlock;
        if (currentBlock.Handle != IntPtr.Zero)
        {
            var lastInst = currentBlock.LastInstruction;
            if (lastInst.Handle != IntPtr.Zero && lastInst.IsATerminatorInst.Handle != IntPtr.Zero)
            {
                canEmit = false;
            }
        }

        if (canEmit && scope.SavedCleanupHead.HasValue && scope.SavedCleanupUsed.HasValue)
        {
            var savedHead = _builder.BuildLoad2(GetPointerType(GetInt8Type()), scope.SavedCleanupHead.Value, "saved_cleanup_head");
            var savedUsed = _builder.BuildLoad2(GetInt32Type(), scope.SavedCleanupUsed.Value, "saved_cleanup_used");
            BuildPopCleanupRecordsTo(savedHead, savedUsed);
        }

        for (int i = scope.OwnedVariables.Count - 1; i >= 0; i--)
        {
            var name = scope.OwnedVariables[i];
            if (_deadVariables.Contains(name)) continue;
            if (!_ownedVariables.Contains(name)) continue;
            _ownedVariables.Remove(name);
            _deadVariables.Add(name);
        }
    }

    private void CleanupToDepth(int targetDepth, string? skipVariable = null)
    {
        if (targetDepth < _scopes.Count)
        {
            var scope = _scopes[targetDepth];
            if (scope.SavedCleanupHead.HasValue && scope.SavedCleanupUsed.HasValue)
            {
                var savedHead = _builder.BuildLoad2(GetPointerType(GetInt8Type()), scope.SavedCleanupHead.Value, "saved_cleanup_head");
                var savedUsed = _builder.BuildLoad2(GetInt32Type(), scope.SavedCleanupUsed.Value, "saved_cleanup_used");
                BuildPopCleanupRecordsTo(savedHead, savedUsed);
            }
        }

        UpdateOwnershipToDepth(targetDepth, skipVariable);
    }

    private void UpdateOwnershipToDepth(int targetDepth, string? skipVariable = null)
    {
        for (int i = _scopes.Count - 1; i >= targetDepth; i--)
        {
            var scope = _scopes[i];
            for (int j = scope.OwnedVariables.Count - 1; j >= 0; j--)
            {
                var name = scope.OwnedVariables[j];
                if (name == skipVariable) continue;
                if (_deadVariables.Contains(name)) continue;
                if (!_ownedVariables.Contains(name)) continue;
                _ownedVariables.Remove(name);
                _deadVariables.Add(name);
            }
        }
    }

    // Returns true if `structName` is a struct type that transitively owns heap memory
    // (see _owningStructTypes).
    private bool IsOwningStructType(string? structName) => structName != null && _owningStructTypes.Contains(structName);

    // A field is considered "owning" if it is a dynamic array (T[]), a CSTRING, or another
    // owning struct. Assigning into such a field is treated as an ownership transfer, so it
    // is always recursively destroyed along with its containing struct.
    private bool IsOwningFieldTypeNode(TypeNode fieldType)
    {
        return fieldType switch
        {
            ArrayTypeNode => true,
            PrimitiveTypeNode p when p.Type.Type is TokenType.CSTRING or TokenType.WSTRING or TokenType.STRING => true,
            UserTypeNode u => IsOwningStructType(u.Name.Lexeme),
            _ => false,
        };
    }

    // Returns true if `expr` is a field access (possibly nested) on an owned struct
    // variable where the final accessed field is itself an owning type. Such an expression
    // is moved out of its source field when used as the right-hand side of an assignment,
    // so ownership transfers to the destination and the source field is zeroed.
    private bool IsOwnedFieldAccess(Expression expr)
    {
        return expr is GetExpr getExpr && IsOwnedFieldAccess(getExpr, out _);
    }

    private bool IsOwnedFieldAccess(GetExpr expr, out string? structName)
    {
        structName = null;

        if (expr.Object is VariableExpr objVar)
        {
            if (!_ownedVariables.Contains(objVar.Name)) return false;
            if (!_namedValues.TryGetValue(objVar.Name, out var entry)) return false;
            structName = entry.StructName;
        }
        else if (expr.Object is GetExpr nestedGet)
        {
            if (!IsOwnedFieldAccess(nestedGet, out structName)) return false;
        }
        else
        {
            return false;
        }

        if (string.IsNullOrEmpty(structName) || !_structFieldNames.TryGetValue(structName, out var fieldNames))
            return false;

        int idx = fieldNames.IndexOf(expr.Name.Lexeme);
        if (idx < 0) return false;

        return IsOwningFieldTypeNode(_structFieldTypeNodes[structName][idx]);
    }

    // Frees the heap memory owned by a variable at the end of its lifetime (scope exit,
    // explicit free(), or overwrite of a previously-owned value). For an owning struct
    // this recursively destroys its owning fields instead of trying to free the struct's
    // own (stack-allocated) storage.
    private void DestroyOwnedValue(string name)
    {
        if (!_namedValues.TryGetValue(name, out var entry)) return;
        if (IsOwningStructType(entry.StructName))
        {
            DestroyStructFields(entry.Value, entry.Type, entry.StructName!);
        }
        else
        {
            FreeCall(new VariableExpr(name, new SourceLocation(null, 0, 0, 0)));
        }
    }

    // Zeroes the storage of an owning variable so that any later cleanup-stack destructor
    // call becomes a no-op (free(NULL) is safe). Used when ownership is explicitly freed,
    // transferred away, or returned to the caller.
    private void ZeroOwnedVariable(string name)
    {
        if (!_namedValues.TryGetValue(name, out var entry)) return;
        _builder.BuildStore(LLVMValueRef.CreateConstNull(entry.Type), entry.Value);
    }

    // Recursively frees every owning field of the struct at `structPtr` (of `structType` /
    // `structName`), descending into nested owning structs. Fields that were never assigned
    // a heap allocation are null/zero (structs are zero-initialized when declared without an
    // initializer), and free(NULL) is a safe no-op, so this is safe to run unconditionally.
    private void DestroyStructFields(LLVMValueRef structPtr, LLVMTypeRef structType, string structName)
    {
        var fieldTypeNodes = _structFieldTypeNodes[structName];
        var fieldTypes = _structFieldTypes[structName];

        for (int i = 0; i < fieldTypeNodes.Count; i++)
        {
            var fieldTypeNode = fieldTypeNodes[i];
            if (!IsOwningFieldTypeNode(fieldTypeNode)) continue;

            var fieldPtr = _builder.BuildStructGEP2(structType, structPtr, (uint)i, "owned_field_ptr");

            if (fieldTypeNode is UserTypeNode nestedUser)
            {
                DestroyStructFields(fieldPtr, fieldTypes[i], nestedUser.Name.Lexeme);
                continue;
            }

            var fieldValue = _builder.BuildLoad2(fieldTypes[i], fieldPtr, "owned_field_val");
            if (fieldTypeNode is ArrayTypeNode)
            {
                var dataPtr = _builder.BuildExtractValue(fieldValue, 0, "owned_field_data");
                EmitRawFreeCall(dataPtr);
            }
            else
            {
                // CSTRING field: already a raw i8* pointer.
                EmitRawFreeCall(fieldValue);
            }
        }
    }

    // Emits a call to free() on a raw pointer value, bit-casting to i8* first if needed.
    private void EmitRawFreeCall(LLVMValueRef ptr)
    {
        var freeFunc = GetOrAddFunction("free", GetVoidType(), new[] { GetPointerType(GetInt8Type()) });
        var ptrType = GetPointerType(GetInt8Type());
        if (ptr.TypeOf.Handle != ptrType.Handle)
        {
            ptr = _builder.BuildBitCast(ptr, ptrType, "free_ptr");
        }
        _builder.BuildCall2(_functionTypes["free"], freeFunc, new[] { ptr });
    }

    // True if `expr` freshly constructs an owning struct value (a struct literal, or a
    // move() of one) - i.e. the resulting variable/slot should be considered the owner.
    // Plain variable-to-variable assignment of an owning value is handled as an implicit
    // move() elsewhere, so it never produces a shallow alias.
    private bool IsOwningStructConstruction(Expression expr, string? structName)
    {
        if (!IsOwningStructType(structName)) return false;
        if (expr is StructInitExpr) return true;
        if (expr is CallExpr call && call.Callee is VariableExpr { Name: "move" }) return true;
        return false;
    }

    private void CleanupAllOpenScopes(string? skipVariable = null)
    {
        if (_scopes.Count == 0)
        {
            UpdateOwnershipToDepth(0, skipVariable);
            return;
        }

        var targetHead = _builder.BuildLoad2(GetPointerType(GetInt8Type()), _scopes[0].SavedCleanupHead!.Value, "cleanup_target_head");
        var targetUsed = _builder.BuildLoad2(GetInt32Type(), _scopes[0].SavedCleanupUsed!.Value, "cleanup_target_used");

        // If we are returning a specific owned variable, preserve its heap memory by
        // skipping its destructor call. We pop everything above it, zero its record so
        // the destructor becomes a no-op, then pop down to the function entry top.
        if (skipVariable != null &&
            _variableCleanupIndices.TryGetValue(skipVariable, out var skipLoc) &&
            _ownedVariables.Contains(skipVariable))
        {
            var one = LLVMValueRef.CreateConstInt(GetInt32Type(), 1);
            var skipUsedPlusOne = _builder.BuildAdd(skipLoc.Used, one, "cleanup_skip_used_plus_one");
            BuildPopCleanupRecordsTo(skipLoc.Head, skipUsedPlusOne);

            var objSlot = BuildCleanupObjectSlot(skipLoc.Head, skipLoc.Used);
            _builder.BuildStore(LLVMValueRef.CreateConstNull(GetPointerType(GetInt8Type())), objSlot);

            BuildPopCleanupRecordsTo(targetHead, targetUsed);
        }
        else
        {
            BuildPopCleanupRecordsTo(targetHead, targetUsed);
        }

        UpdateOwnershipToDepth(0, skipVariable);
    }

    // Frees any cstr() temporaries created (and not claimed by a variable declaration)
    // since `mark` was captured, then drops them from _pendingCstrTemps. `mark` should be
    // the value of _pendingCstrTemps.Count taken before evaluating the statement.
    private void FreeUnclaimedCstrTemps(int mark)
    {
        if (_pendingCstrTemps.Count <= mark) return;

        bool canEmit = true;
        var currentBlock = _builder.InsertBlock;
        if (currentBlock.Handle != IntPtr.Zero)
        {
            var lastInst = currentBlock.LastInstruction;
            if (lastInst.Handle != IntPtr.Zero && lastInst.IsATerminatorInst.Handle != IntPtr.Zero)
            {
                canEmit = false;
            }
        }

        if (canEmit)
        {
            var freeFunc = GetOrAddFunction("free", GetVoidType(), new[] { GetPointerType(GetInt8Type()) });
            for (int i = _pendingCstrTemps.Count - 1; i >= mark; i--)
            {
                _builder.BuildCall2(_functionTypes["free"], freeFunc, new[] { _pendingCstrTemps[i] });
            }
        }

        _pendingCstrTemps.RemoveRange(mark, _pendingCstrTemps.Count - mark);
    }

    // Removes a range of trailing cstr() temporaries from tracking without freeing them,
    // because ownership of that allocation has been transferred elsewhere (e.g. bound to a
    // variable, or returned to the caller).
    private void ClaimCstrTemps(int mark)
    {
        if (_pendingCstrTemps.Count > mark)
        {
            _pendingCstrTemps.RemoveRange(mark, _pendingCstrTemps.Count - mark);
        }
    }

    private void AddOwnedVariable(string name)
    {
        _ownedVariables.Add(name);
        if (_scopes.Count > 0)
        {
            int scopeIndex = _variableDeclScope.TryGetValue(name, out var idx) ? idx : _scopes.Count - 1;
            if (scopeIndex >= 0 && scopeIndex < _scopes.Count)
            {
                var scope = _scopes[scopeIndex];
                if (!scope.OwnedVariables.Contains(name))
                {
                    scope.OwnedVariables.Add(name);
                }
            }
        }

        if (_namedValues.TryGetValue(name, out var entry) && _variableDeclaredTypeNodes.TryGetValue(name, out var typeNode))
        {
            var dtor = GetOrCreateDestructor(typeNode);
            var (head, used) = BuildPushCleanupRecord(entry.Value, dtor);
            _variableCleanupIndices[name] = (head, used);
        }
    }

    private bool IsOwnedExpression(Expression? expr, LLVMValueRef? value = null)
    {
        if (expr is ArrayAllocExpr) return true;
        if (expr is CallExpr call && call.Callee is VariableExpr { Name: "move" }) return true;
        if (expr is CallExpr allocCall && allocCall.Callee is VariableExpr { Name: "alloc" }) return true;

        // C-string builtins that return a freshly malloc'd buffer.
        if (expr is CallExpr cstrCall && cstrCall.Callee is VariableExpr { Name: "strdup" or "str_concat" }) return true;

        // cstr() only allocates when converting a STRING (it passes an existing CSTRING
        // through unchanged); ownership is determined by whether it actually pushed a new
        // temporary, tracked precisely via _pendingCstrTemps rather than by expression shape.

        // STRING concatenation produces a newly allocated STRING value.
        if (expr is BinaryExpr { Operator.Type: TokenType.Plus } && value.HasValue && IsStringStructType(value.Value.TypeOf))
            return true;

        return false;
    }

    private bool IsSameVariable(Expression a, Expression b)
    {
        return a is VariableExpr va && b is VariableExpr vb && va.Name == vb.Name;
    }

    // If `expr` is a variable that currently owns a resource, transfer that ownership to
    // the caller. The source variable is marked dead and removed from the owned set.
    // Returns true if a transfer happened. Used to make plain assignment of an owning
    // value behave like an implicit move() rather than creating a shallow alias.
    private bool TryTransferOwnership(Expression expr, Expression? excludeLhs = null)
    {
        if (excludeLhs != null && IsSameVariable(expr, excludeLhs)) return false;
        if (expr is VariableExpr varExpr && _ownedVariables.Contains(varExpr.Name))
        {
            ZeroOwnedVariable(varExpr.Name);
            _ownedVariables.Remove(varExpr.Name);
            _deadVariables.Add(varExpr.Name);
            return true;
        }
        return false;
    }

    // Returns an i8* global pointer to a NUL-terminated copy of `text`, creating and caching
    // it on first use so identical compiler-generated strings are emitted only once.
    private LLVMValueRef GetOrCreateGlobalStringPtr(string text, string nameHint)
    {
        if (_globalStringCache.TryGetValue(text, out var cached))
            return cached;

        var ptr = _builder.BuildGlobalStringPtr(text, nameHint);
        _globalStringCache[text] = ptr;
        return ptr;
    }

    private void FreeCall(Expression expr)
    {
        var freeFunc = _module.GetNamedFunction("free");
        if (freeFunc.Handle == IntPtr.Zero)
        {
            var freeType = LLVMTypeRef.CreateFunction(GetVoidType(), new[] { GetPointerType(GetInt8Type()) });
            freeFunc = _module.AddFunction("free", freeType);
            _functionTypes["free"] = freeType;
        }

        var value = VisitExpression(expr);
        var ptrType = GetPointerType(GetInt8Type());
        LLVMValueRef ptr;
        if (value.TypeOf.Kind == LLVMTypeKind.LLVMPointerTypeKind)
            ptr = _builder.BuildBitCast(value, ptrType, "tmp_ptr");
        else if (value.TypeOf.Kind == LLVMTypeKind.LLVMIntegerTypeKind)
            ptr = _builder.BuildIntToPtr(value, ptrType, "tmp_ptr");
        else if (value.TypeOf.Kind == LLVMTypeKind.LLVMStructTypeKind)
        {
            ptr = _builder.BuildExtractValue(value, 0, "array_data_ptr");
            ptr = _builder.BuildBitCast(ptr, ptrType, "free_ptr");
        }
        else
            throw new Exception($"Cannot free a value of type {value.TypeOf}");
        _builder.BuildCall2(_functionTypes["free"], freeFunc, new[] { ptr });
    }

    private void FreeExpression(Expression expr)
    {
        if (expr is VariableExpr freedStructVar &&
            _namedValues.TryGetValue(freedStructVar.Name, out var freedEntry) &&
            IsOwningStructType(freedEntry.StructName))
        {
            DestroyStructFields(freedEntry.Value, freedEntry.Type, freedEntry.StructName!);
        }
        else
        {
            FreeCall(expr);
        }

        if (expr is VariableExpr freedVar)
        {
            ZeroOwnedVariable(freedVar.Name);
            _deadVariables.Add(freedVar.Name);
            _ownedVariables.Remove(freedVar.Name);
        }
    }

    private LLVMValueRef VisitExpression(Expression expr)
    {
        try
        {
            return expr switch
            {
            LiteralExpr literal => VisitLiteral(literal),
            BinaryExpr binary => VisitBinary(binary),
            VariableExpr variable => VisitVariable(variable),
            CallExpr call => VisitCall(call),
            GroupingExpr grouping => VisitExpression(grouping.Expression),
            UnaryExpr unary => VisitUnary(unary),
            GetExpr get => VisitGet(get),
            SetExpr set => VisitSet(set),
            IndexExpr index => VisitIndex(index),
            SetIndexExpr setIndex => VisitSetIndex(setIndex),
            ArrayInitExpr arrayInit => VisitArrayInit(arrayInit),
            ArrayAllocExpr arrayAlloc => VisitArrayAlloc(arrayAlloc),
            CastExpr cast => VisitCast(cast),
            StructInitExpr structInit => VisitStructInit(structInit),
            TernaryExpr ternary => VisitTernary(ternary),
            PostfixExpr postfix => VisitPostfix(postfix),
            _ => throw new NotImplementedException($"Expression type {expr.GetType().Name} not implemented.")
        };
        }
        catch (CompileException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new CompileException(expr.Location, ex.Message, ex);
        }
    }

    public void Dispose()
    {
        _builder.Dispose();
        _module.Dispose();
        _context.Dispose();
    }
}
