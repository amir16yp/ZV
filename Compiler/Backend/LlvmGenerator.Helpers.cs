using System;
using System.Collections.Generic;
using System.Linq;
using LLVMSharp.Interop;
using ZV.Compiler.AST;
using ZV.Compiler.Lexer;

namespace ZV.Compiler.Backend;

public partial class LlvmGenerator
{
    private LLVMValueRef GeneratePrintCall(List<Expression> arguments)
    {
        var printf = GetOrAddFunction("printf", GetInt32Type(), new[] { GetPointerType(GetInt8Type()) }, true);
        var printfType = _functionTypes["printf"];

        var args = new List<LLVMValueRef>();

        if (arguments.Count > 0 && arguments[0] is LiteralExpr { Type: TokenType.StringLiteral } fmtLiteral)
        {
            // User-provided format string; treat remaining arguments as printf values.
            var fmtStr = (string)fmtLiteral.Value!;
            var fmtStrPtr = GetOrCreateGlobalStringPtr(fmtStr, "fmt");
            args.Add(fmtStrPtr);

            // C's variadic calling convention promotes any integer narrower than `int` (and
            // also `float`) before the call. Without that promotion printf reads undefined
            // bits for the wider slot, producing garbage (observed e.g. UINT8 values
            // printing as 4294967xxx or random 32-bit values). Match each argument to the
            // corresponding format specifier so unsigned conversions (%u/%x/etc.) are
            // zero-extended while signed ones (%d/%i) are sign-extended.
            var roles = ParsePrintfArgRoles(fmtStr);

            // Catch the classic C printf footgun at compile time instead of at runtime:
            // a mismatched argument count or an argument whose type doesn't match its
            // format specifier both produce garbage output (or a crash) in plain C. Since
            // print() already parses the format string above for ABI-promotion purposes,
            // validating it costs nothing extra and fits the same "catch it structurally"
            // philosophy as array bounds checks and use-after-free tracking.
            int suppliedValueArgs = arguments.Count - 1;
            if (suppliedValueArgs != roles.Count)
            {
                throw new CompileException(arguments[0].Location,
                    $"print() format string \"{fmtStr}\" expects {roles.Count} argument(s) but {suppliedValueArgs} were provided.");
            }

            int roleIndex = 0;
            for (int i = 1; i < arguments.Count; i++)
            {
                var val = VisitExpression(arguments[i]);
                char specifier = '\0';
                if (roleIndex < roles.Count)
                {
                    var (kind, spec) = roles[roleIndex];
                    specifier = kind == PrintfArgRoleKind.Value ? spec : 'd'; // width/precision '*' consume a signed int
                    if (kind == PrintfArgRoleKind.Value)
                    {
                        string? mismatch = DescribePrintfSpecifierMismatch(spec, val.TypeOf);
                        if (mismatch != null)
                        {
                            throw new CompileException(arguments[i].Location, mismatch);
                        }
                    }
                    roleIndex++;
                }
                args.Add(PromotePrintfArg(val, specifier));
            }

            return _builder.BuildCall2(printfType, printf, args.ToArray(), "printtmp");
        }

        // Construct format string based on argument types
        // Note: In a full compiler, we'd have type information from semantic analysis.
        // For now, we'll do a simple heuristic or default to %d/%u.

        string fmt = "";
        var values = new List<LLVMValueRef>();
        foreach (var arg in arguments)
        {
            var val = VisitExpression(arg);

            var type = val.TypeOf;
            if (IsStringStructType(type))
            {
                // STRING values print as length-delimited UTF-8 bytes.
                fmt += "%.*s";
                var dataPtr = _builder.BuildExtractValue(val, 0, "str_data");
                var length = _builder.BuildExtractValue(val, 1, "str_len");
                // printf's %.*s expects an int precision argument.
                var lengthI32 = _builder.BuildTrunc(length, GetInt32Type(), "str_len_i32");
                values.Add(lengthI32);
                values.Add(dataPtr);
            }
            else
            {
                // C's variadic calling convention promotes any integer narrower than `int`
                // (bool, char, short) to `int` at the call site; printf/vprintf then always
                // reads a full int-sized slot for %d/%c. Passing the raw i1/i8/i16 value
                // without that promotion leaves the upper bits of the slot undefined, which
                // printf then reads as garbage. BOOL (i1) and any width < 32 bits (INT16/
                // UINT16; INT8/UINT8/CHAR are also promoted for ABI-safety even though %c
                // only reads the low byte) are sign-extended to i32 here to match. There's
                // no separate UINT16 LLVM type to zero-extend instead (see MapType), so this
                // is sign-extension for both signed and unsigned 16-bit values.
                //
                // For 32/64-bit values we pick %u/%llu for unsigned expressions so UINT32 and
                // UINT64 print as their full unsigned value rather than as signed.
                if (type.Kind == LLVMTypeKind.LLVMIntegerTypeKind)
                {
                    int width = (int)type.IntWidth;
                    bool unsigned = IsUnsignedExpression(arg);
                    if (width == 1)
                    {
                        values.Add(_builder.BuildZExt(val, GetInt32Type(), "print_promote"));
                        fmt += "%d";
                    }
                    else if (width == 8)
                    {
                        values.Add(_builder.BuildSExt(val, GetInt32Type(), "print_promote"));
                        fmt += "%c";
                    }
                    else if (width == 16)
                    {
                        values.Add(_builder.BuildSExt(val, GetInt32Type(), "print_promote"));
                        fmt += "%d";
                    }
                    else if (width == 32)
                    {
                        values.Add(val);
                        fmt += unsigned ? "%u" : "%d";
                    }
                    else if (width == 64)
                    {
                        values.Add(val);
                        fmt += unsigned ? "%llu" : "%lld";
                    }
                    else
                    {
                        values.Add(val);
                        fmt += "%d";
                    }
                }
                else if (type.Kind == LLVMTypeKind.LLVMFloatTypeKind)
                {
                    values.Add(val);
                    fmt += "%f";
                }
                else if (type.Kind == LLVMTypeKind.LLVMDoubleTypeKind)
                {
                    values.Add(val);
                    fmt += "%f";
                }
                else if (type.Kind == LLVMTypeKind.LLVMPointerTypeKind)
                {
                    values.Add(val);
                    fmt += "%s";
                }
                else
                {
                    values.Add(val);
                    fmt += "%d";
                }
            }
        }
        fmt += "\n";

        var autoFmtStr = GetOrCreateGlobalStringPtr(fmt, "fmt");
        args.Add(autoFmtStr);
        args.AddRange(values);

        return _builder.BuildCall2(printfType, printf, args.ToArray(), "printtmp");
    }

    private enum PrintfArgRoleKind { Width, Precision, Value }

    /// <summary>
    /// Scans a printf-style format string and returns, in order, the role of each variadic
    /// argument it consumes. Width/precision stars consume a signed INT32 argument; value
    /// specifiers carry their conversion character so the caller can decide how to promote
    /// the matching expression. Literal "%%" sequences are skipped.
    /// </summary>
    private static List<(PrintfArgRoleKind Kind, char Specifier)> ParsePrintfArgRoles(string format)
    {
        var roles = new List<(PrintfArgRoleKind, char)>();
        for (int i = 0; i < format.Length; i++)
        {
            if (format[i] != '%') continue;
            i++;
            if (i >= format.Length) break;
            if (format[i] == '%') continue; // literal %

            // Skip flags.
            while (i < format.Length && "-+ #0".Contains(format[i])) i++;

            // Width.
            if (i < format.Length && format[i] == '*')
            {
                roles.Add((PrintfArgRoleKind.Width, '\0'));
                i++;
            }
            else
            {
                while (i < format.Length && char.IsDigit(format[i])) i++;
            }

            // Precision.
            if (i < format.Length && format[i] == '.')
            {
                i++;
                if (i < format.Length && format[i] == '*')
                {
                    roles.Add((PrintfArgRoleKind.Precision, '\0'));
                    i++;
                }
                else
                {
                    while (i < format.Length && char.IsDigit(format[i])) i++;
                }
            }

            // Length modifiers.
            while (i < format.Length && "hlLjzt".Contains(format[i])) i++;

            // Conversion specifier.
            if (i >= format.Length) break;
            roles.Add((PrintfArgRoleKind.Value, format[i]));
        }
        return roles;
    }

    /// <summary>
    /// Applies C's default argument promotions for variadic calls: integers narrower than
    /// INT32 are extended to INT32, and FLOAT32 is promoted to FLOAT64. Unsigned conversions
    /// (%u/%x/etc.) are zero-extended; signed conversions (%d/%i) are sign-extended. Width/
    /// precision arguments (from "*") are always sign-extended to INT32.
    /// </summary>
    private LLVMValueRef PromotePrintfArg(LLVMValueRef value, char specifier)
    {
        var type = value.TypeOf;

        if (type.Kind == LLVMTypeKind.LLVMIntegerTypeKind && type.IntWidth < 32)
        {
            if (type.IntWidth == 1)
            {
                // BOOL is only 0 or 1; zero-extend to match the automatic-format path.
                return _builder.BuildZExt(value, GetInt32Type(), "print_promote");
            }

            bool unsignedConversion = specifier is 'u' or 'o' or 'x' or 'X' or 'c';
            return unsignedConversion
                ? _builder.BuildZExt(value, GetInt32Type(), "print_promote")
                : _builder.BuildSExt(value, GetInt32Type(), "print_promote");
        }

        if (type.Kind == LLVMTypeKind.LLVMFloatTypeKind)
        {
            return _builder.BuildFPCast(value, GetDoubleType(), "print_fpromote");
        }

        return value;
    }

    /// <summary>
    /// Returns a diagnostic message if <paramref name="type"/> can't plausibly satisfy the
    /// given printf conversion specifier, or null if it's compatible. This is a best-effort
    /// check (it doesn't distinguish e.g. INT32 from INT64), but it catches the common
    /// mistakes: passing a STRING/struct where an integer or float is expected, passing a
    /// pointer to "%d", passing a raw STRING (instead of cstr()) to "%s", etc.
    /// </summary>
    private string? DescribePrintfSpecifierMismatch(char specifier, LLVMTypeRef type)
    {
        bool isInt = type.Kind == LLVMTypeKind.LLVMIntegerTypeKind;
        bool isFloat = IsFloatingType(type);
        bool isPointer = type.Kind == LLVMTypeKind.LLVMPointerTypeKind;

        switch (specifier)
        {
            case 'd': case 'i': case 'u': case 'o': case 'x': case 'X': case 'c':
                if (!isInt)
                    return $"print() format specifier '%{specifier}' expects an integer argument, but got {DescribeTypeForDiagnostic(type)}.";
                break;
            case 'f': case 'F': case 'e': case 'E': case 'g': case 'G': case 'a': case 'A':
                if (!isFloat)
                    return $"print() format specifier '%{specifier}' expects a floating-point argument, but got {DescribeTypeForDiagnostic(type)}.";
                break;
            case 's':
                if (IsStringStructType(type))
                    return "print() format specifier '%s' does not accept a STRING value directly; use '%.*s', or convert it with cstr() first.";
                if (!isPointer)
                    return $"print() format specifier '%s' expects a CSTRING argument, but got {DescribeTypeForDiagnostic(type)}.";
                break;
            case 'p':
                if (!isPointer)
                    return $"print() format specifier '%p' expects a pointer argument, but got {DescribeTypeForDiagnostic(type)}.";
                break;
            default:
                // Unrecognized/rare conversions (e.g. 'n') aren't validated further.
                break;
        }
        return null;
    }

    private static string DescribeTypeForDiagnostic(LLVMTypeRef type)
    {
        return type.Kind switch
        {
            LLVMTypeKind.LLVMIntegerTypeKind => type.IntWidth == 1 ? "a BOOL" : $"a {type.IntWidth}-bit integer",
            LLVMTypeKind.LLVMFloatTypeKind => "a FLOAT32",
            LLVMTypeKind.LLVMDoubleTypeKind => "a FLOAT64",
            LLVMTypeKind.LLVMPointerTypeKind => "a pointer",
            LLVMTypeKind.LLVMStructTypeKind => "a struct/STRING value",
            _ => type.Kind.ToString(),
        };
    }

    private LLVMValueRef ConvertToType(LLVMValueRef value, LLVMTypeRef targetType)
    {
        if (value.TypeOf.Handle == targetType.Handle) return value;

        if (value.TypeOf.Kind == LLVMTypeKind.LLVMIntegerTypeKind && targetType.Kind == LLVMTypeKind.LLVMIntegerTypeKind)
        {
            if (value.TypeOf.IntWidth > targetType.IntWidth)
                return _builder.BuildTrunc(value, targetType, "argtrunc");
            return _builder.BuildSExt(value, targetType, "argsext");
        }

        if (value.TypeOf.Kind == LLVMTypeKind.LLVMPointerTypeKind && targetType.Kind == LLVMTypeKind.LLVMPointerTypeKind)
        {
            if (value.TypeOf.Handle != targetType.Handle)
                return _builder.BuildBitCast(value, targetType, "argbitcast");
            return value;
        }

        // Floating-point conversions (and integer <-> float for function args/assignment)
        if (IsFloatingType(value.TypeOf) && IsFloatingType(targetType))
            return _builder.BuildFPCast(value, targetType, "argfpcast");
        if (value.TypeOf.Kind == LLVMTypeKind.LLVMIntegerTypeKind && IsFloatingType(targetType))
            return _builder.BuildSIToFP(value, targetType, "argsitofp");
        if (IsFloatingType(value.TypeOf) && targetType.Kind == LLVMTypeKind.LLVMIntegerTypeKind)
            return _builder.BuildFPToSI(value, targetType, "argfptosi");

        if (value.TypeOf.Kind == LLVMTypeKind.LLVMStructTypeKind && targetType.Kind == LLVMTypeKind.LLVMStructTypeKind)
        {
            // Allow struct-to-struct assignment if the element types match structurally.
            var sourceElements = value.TypeOf.GetStructElementTypes();
            var targetElements = targetType.GetStructElementTypes();
            if (sourceElements.Length == targetElements.Length)
            {
                bool match = true;
                for (int i = 0; i < sourceElements.Length; i++)
                {
                    if (sourceElements[i].Handle != targetElements[i].Handle)
                    {
                        match = false;
                        break;
                    }
                }
                if (match) return value;
            }
            throw new NotImplementedException($"Conversion from {value.TypeOf} to {targetType} not implemented.");
        }

        if (value.TypeOf.Kind == LLVMTypeKind.LLVMStructTypeKind && targetType.Kind == LLVMTypeKind.LLVMPointerTypeKind)
        {
            // Array struct to pointer decay, or STRING to CSTRING (extract data pointer).
            var dataPtr = _builder.BuildExtractValue(value, 0, "struct_decay");
            if (dataPtr.TypeOf.Handle != targetType.Handle)
                return _builder.BuildBitCast(dataPtr, targetType, "argbitcast");
            return dataPtr;
        }

        if (value.TypeOf.Kind == LLVMTypeKind.LLVMPointerTypeKind && IsStringStructType(targetType))
        {
            // CSTRING to STRING: wrap pointer with its NUL-terminated length via strlen.
            var strlenFunc = GetOrAddFunction("strlen", GetInt64Type(), new[] { GetPointerType(GetInt8Type()) });
            var length = _builder.BuildCall2(_functionTypes["strlen"], strlenFunc, new[] { value }, "cstr_len");
            var strType = GetStringStructType();
            var baseVal = LLVMValueRef.CreateConstNull(strType);
            var withData = _builder.BuildInsertValue(baseVal, value, 0, "cstr_to_str_data");
            return _builder.BuildInsertValue(withData, length, 1, "cstr_to_str");
        }

        throw new NotImplementedException($"Conversion from {value.TypeOf} (kind={value.TypeOf.Kind}) to {targetType} (kind={targetType.Kind}) not implemented.");
    }

    private static bool IsFloatingType(LLVMTypeRef type)
    {
        return type.Kind == LLVMTypeKind.LLVMFloatTypeKind || type.Kind == LLVMTypeKind.LLVMDoubleTypeKind;
    }

    private LLVMTypeRef MapTypeNode(TypeNode typeNode)
    {
        if (typeNode is PrimitiveTypeNode primitive)
        {
            return MapType(primitive.Type.Type);
        }
        else if (typeNode is ArrayTypeNode array)
        {
            return GetArrayStructType(MapTypeNode(array.BaseType));
        }
        else if (typeNode is FixedSizeArrayTypeNode fixedArray)
        {
            var elementType = MapTypeNode(fixedArray.BaseType);
            var length = EvaluateConstantSize(fixedArray.Size);
            return LLVMTypeRef.CreateArray(elementType, (uint)length);
        }
        else if (typeNode is PointerTypeNode pointer)
        {
            // PTR<VOID> maps to i8* (opaque pointer)
            if (pointer.BaseType is PrimitiveTypeNode pt && pt.Type.Type == TokenType.VOID)
            {
                return GetPointerType(GetInt8Type());
            }
            var pointeeType = MapTypeNode(pointer.BaseType);
            return GetPointerType(pointeeType);
        }
        else if (typeNode is FunctionPointerTypeNode funcPtr)
        {
            return GetPointerType(GetFunctionPointerFunctionType(funcPtr));
        }
        else if (typeNode is UserTypeNode user)
        {
            if (_typeAliases.TryGetValue(user.Name.Lexeme, out var aliasedType))
            {
                return aliasedType;
            }
            if (_structTypes.TryGetValue(user.Name.Lexeme, out var structType))
            {
                return structType;
            }
            throw new Exception($"Unknown type: {user.Name.Lexeme}");
        }
        throw new NotImplementedException($"Type mapping for {typeNode.GetType().Name} not implemented.");
    }

    // The underlying `Ret (Params...)` function type of a FUNCPTR<...> type node - needed
    // both to build its pointer type (MapTypeNode above) and, at an indirect-call site, to
    // know how to call through a value of that pointer type (see VisitCall).
    private LLVMTypeRef GetFunctionPointerFunctionType(FunctionPointerTypeNode funcPtr)
    {
        var returnType = MapTypeNode(funcPtr.ReturnType);
        var paramTypes = funcPtr.ParamTypes.Select(MapTypeNode).ToArray();
        return LLVMTypeRef.CreateFunction(returnType, paramTypes);
    }

    // Returns the newtype name declared by `type`, or null if `type` does not refer to a
    // name declared with `newtype` (e.g. a primitive, struct, or transparent `type` alias).
    private string? GetDeclaredNewtypeName(TypeNode type)
    {
        return type is UserTypeNode user && _newtypeNames.Contains(user.Name.Lexeme) ? user.Name.Lexeme : null;
    }

    // The struct name backing a declared type, for field-access purposes (_structTypes /
    // _structFieldNames / _structFieldTypes are keyed by this name). Covers plain
    // user-defined structs as well as the builtin struct-shaped primitive keyword types
    // (EXCEPTION -> "Exception", PROCESS -> "PROCESS") that are registered under those
    // names the same way user structs are (see GetExceptionType/GetProcessType).
    private string? GetStructNameForTypeNode(TypeNode type)
    {
        return type switch
        {
            UserTypeNode user => user.Name.Lexeme,
            PrimitiveTypeNode { Type.Type: TokenType.EXCEPTION } => "Exception",
            PrimitiveTypeNode { Type.Type: TokenType.PROCESS } => "PROCESS",
            _ => null,
        };
    }

    // Expressions that directly construct a value (literals, struct/array initializers)
    // aren't "values of an existing type" the way a variable read is - the compiler can
    // build them directly as the target newtype, so they're exempt from the newtype
    // implicit-conversion restriction. This lets `Celsius temp = 36.5;` work without a cast.
    private static bool IsConstructorLikeExpression(Expression expr)
    {
        return expr is LiteralExpr or StructInitExpr or ArrayInitExpr or ArrayAllocExpr;
    }

    // Best-effort static inference of the newtype identity of an expression's result,
    // without a full type-inference pass. Returns null when the expression isn't known to
    // produce a specific newtype (plain values, arithmetic, field/index access, etc.).
    private string? InferNewtypeName(Expression expr)
    {
        switch (expr)
        {
            case VariableExpr v:
                return _variableNewtypeNames.TryGetValue(v.Name, out var varNewtype) ? varNewtype : null;
            case GroupingExpr g:
                return InferNewtypeName(g.Expression);
            case CastExpr c:
                return GetDeclaredNewtypeName(c.TargetType);
            case CallExpr call when call.Callee is VariableExpr fn:
                return _functionReturnNewtype.TryGetValue(fn.Name, out var retNewtype) ? retNewtype : null;
            default:
                return null;
        }
    }

    // True if `type` is an unsigned primitive integer type (UINT8..UINT128). Used to recover
    // signedness for `as`-cast extension and signed vs. unsigned arithmetic/comparison, since
    // the mapped LLVMTypeRef alone can't distinguish e.g. UINT8 from INT8 (both are i8).
    private static bool IsUnsignedPrimitiveTypeNode(TypeNode? type)
    {
        return type is PrimitiveTypeNode p && p.Type.Type is TokenType.UINT8 or TokenType.UINT16
            or TokenType.UINT32 or TokenType.UINT64 or TokenType.UINT128;
    }

    // Best-effort check to recover the signedness of an integer expression for printing.
    // Falls back to signed when the expression type cannot be statically inferred.
    private bool IsUnsignedExpression(Expression expr)
    {
        if (expr is LiteralExpr lit)
        {
            return lit.Type is TokenType.UINT8 or TokenType.UINT16 or TokenType.UINT32
                or TokenType.UINT64 or TokenType.UINT128;
        }
        return IsUnsignedPrimitiveTypeNode(InferExprTypeNode(expr));
    }

    private static int PrimitiveIntWidth(TypeNode? type)
    {
        if (type is not PrimitiveTypeNode p) return 0;
        return p.Type.Type switch
        {
            TokenType.INT8 or TokenType.UINT8 => 8,
            TokenType.INT16 or TokenType.UINT16 => 16,
            TokenType.INT32 or TokenType.UINT32 => 32,
            TokenType.INT64 or TokenType.UINT64 => 64,
            TokenType.INT128 or TokenType.UINT128 => 128,
            _ => 0,
        };
    }

    private static bool IsIntegerPreservingOperator(TokenType op)
    {
        return op is TokenType.Plus or TokenType.Minus or TokenType.Star
            or TokenType.Ampersand or TokenType.Pipe or TokenType.Caret
            or TokenType.LessLess or TokenType.GreaterGreater;
    }

    // Best-effort static inference of the declared ZV TypeNode an expression evaluates to,
    // used only to recover primitive signedness (see IsUnsignedPrimitiveTypeNode) - this is
    // not a full type-inference pass, and returns null for anything it can't determine this
    // way. Callers must fall back to the previous default (signed) behavior when null.
    // Best-effort inference of the element type of a raw pointer expression.
    // Needed because LLVM opaque pointers don't expose a reliable ElementType.
    private LLVMTypeRef? InferPointerElementType(Expression ptrExpr)
    {
        var typeNode = InferExprTypeNode(ptrExpr);
        if (typeNode is PointerTypeNode pointer)
        {
            if (pointer.BaseType is PrimitiveTypeNode pt && pt.Type.Type == TokenType.VOID)
            {
                return GetInt8Type();
            }
            return MapTypeNode(pointer.BaseType);
        }
        return null;
    }

    private TypeNode? InferExprTypeNode(Expression expr)
    {
        switch (expr)
        {
            case VariableExpr v:
                return _variableDeclaredTypeNodes.TryGetValue(v.Name, out var vt) ? vt : null;

            case GroupingExpr g:
                return InferExprTypeNode(g.Expression);

            case CastExpr c:
                return c.TargetType;

            case UnaryExpr u:
                return InferExprTypeNode(u.Right);

            case PostfixExpr pf:
                return InferExprTypeNode(pf.Left);

            case CallExpr call when call.Callee is VariableExpr fn:
                return _functionReturnTypeNodes.TryGetValue(fn.Name, out var rt) ? rt : null;

            case GetExpr get:
                return InferStructFieldTypeNode(get);

            case IndexExpr idx:
                return InferExprTypeNode(idx.Target) switch
                {
                    ArrayTypeNode arr => arr.BaseType,
                    FixedSizeArrayTypeNode fixedArr => fixedArr.BaseType,
                    _ => null,
                };

            case BinaryExpr bin when IsIntegerPreservingOperator(bin.Operator.Type):
                var leftType = InferExprTypeNode(bin.Left);
                var rightType = InferExprTypeNode(bin.Right);
                if (!IsUnsignedPrimitiveTypeNode(leftType) || !IsUnsignedPrimitiveTypeNode(rightType))
                {
                    return null;
                }
                return PrimitiveIntWidth(leftType) >= PrimitiveIntWidth(rightType) ? leftType : rightType;

            default:
                return null;
        }
    }

    // Resolves the declared TypeNode of `object.field`. Only handles the common case where
    // the struct instance is a plain variable/parameter (covers the vast majority of field
    // reads); anything more exotic (e.g. chained field access) falls back to null.
    private TypeNode? InferStructFieldTypeNode(GetExpr get)
    {
        string? structName = get.Object is VariableExpr objVar && _namedValues.TryGetValue(objVar.Name, out var entry)
            ? entry.StructName
            : null;

        if (structName == null) return null;
        if (!_structFieldNames.ContainsKey(structName)) return null;

        int fieldIndex = GetStructFieldIndex(structName, get.Name.Lexeme);
        if (fieldIndex == -1) return null;
        if (!_structFieldTypeNodes.TryGetValue(structName, out var fieldTypeNodes)) return null;
        if (fieldIndex >= fieldTypeNodes.Count) return null;

        return fieldTypeNodes[fieldIndex];
    }

    // Enforces newtype strictness: a value may only flow into a newtype-typed slot if it is
    // already that same newtype (or a literal/constructor that can be built directly as it),
    // and a newtype value may not implicitly flow into a non-newtype (or differently-typed
    // newtype) slot either. Either direction requires an explicit `as` cast.
    private void CheckNewtypeAssignable(string? targetNewtype, Expression sourceExpr, SourceLocation location)
    {
        if (IsConstructorLikeExpression(sourceExpr)) return;

        var sourceNewtype = InferNewtypeName(sourceExpr);
        if (sourceNewtype == targetNewtype) return;

        string sourceDesc = sourceNewtype != null ? $"newtype '{sourceNewtype}'" : "a non-newtype value";
        string targetDesc = targetNewtype != null ? $"newtype '{targetNewtype}'" : "a non-newtype target";
        throw new CompileException(location, $"Cannot implicitly convert {sourceDesc} to {targetDesc}; use an explicit 'as' cast.");
    }

    private LLVMTypeRef MapType(TokenType type)
    {
        return type switch
        {
            TokenType.INT8 => GetInt8Type(),
            TokenType.UINT8 => GetInt8Type(),
            // Must use context-bound helpers (not the static LLVMTypeRef.CreateInt(width),
            // which creates the type in LLVM's global/default context) - a type from a
            // different context than the module/functions using it trips LLVM's verifier
            // with "Function context does not match Module context!".
            TokenType.INT16 => GetInt16Type(),
            TokenType.UINT16 => GetInt16Type(),
            TokenType.INT32 => GetInt32Type(),
            TokenType.UINT32 => GetInt32Type(),
            TokenType.INT64 => GetInt64Type(),
            TokenType.UINT64 => GetInt64Type(),
            TokenType.INT128 => GetInt128Type(),
            TokenType.UINT128 => GetInt128Type(),
            TokenType.FLOAT32 => GetFloatType(),
            TokenType.FLOAT64 => GetDoubleType(),
            TokenType.BOOL => GetInt1Type(),
            TokenType.CHAR => GetInt8Type(),
            TokenType.STRING => GetStringStructType(),
            TokenType.CSTRING => GetPointerType(GetInt8Type()),
            TokenType.WSTRING => GetPointerType(GetInt16Type()),

            TokenType.PTR => GetPointerType(GetInt8Type()), // bare PTR: opaque pointer
            TokenType.VOID => GetVoidType(),
            TokenType.EXCEPTION => GetExceptionType(),
            TokenType.PROCESS => GetProcessType(),
            _ => throw new NotImplementedException($"Type mapping for {type} not implemented.")
        };
    }

    private int EvaluateConstantSize(Expression sizeExpr)
    {
        if (sizeExpr is LiteralExpr { Type: TokenType.IntegerLiteral } lit && lit.Value != null)
        {
            if (int.TryParse(lit.Value.ToString(), out int size) && size > 0)
            {
                return size;
            }
        }

        throw new CompileException(sizeExpr.Location, "Fixed-size array dimension must be a positive constant integer.");
    }

    // Computes the innermost scalar element type and total number of scalar elements
    // for a (possibly nested) fixed-size LLVM array type such as [2 x [3 x i32]].
    private (LLVMTypeRef InnermostType, long TotalCount) GetFlattenedArrayInfo(LLVMTypeRef arrayType)
    {
        long count = 1;
        var type = arrayType;
        while (type.Kind == LLVMTypeKind.LLVMArrayTypeKind)
        {
            count *= (int)type.ArrayLength;
            type = type.ElementType;
        }
        return (type, count);
    }

    private LLVMValueRef GetElementSize(LLVMTypeRef elementType)
    {
        var sizeInBytes = GetTypeSizeInBytes(elementType);
        return LLVMValueRef.CreateConstInt(GetInt64Type(), sizeInBytes);
    }

    private ulong GetTypeSizeInBytes(LLVMTypeRef type)
    {
        return type.Kind switch
        {
            LLVMTypeKind.LLVMIntegerTypeKind => (ulong)((type.IntWidth + 7) / 8),
            LLVMTypeKind.LLVMFloatTypeKind => 4,
            LLVMTypeKind.LLVMDoubleTypeKind => 8,
            LLVMTypeKind.LLVMPointerTypeKind => 8,
            LLVMTypeKind.LLVMArrayTypeKind => (ulong)type.ArrayLength * GetTypeSizeInBytes(type.ElementType),
            LLVMTypeKind.LLVMStructTypeKind => ComputeStructSize(type),
            _ => throw new NotImplementedException($"Cannot compute size for LLVM type kind {type.Kind}")
        };
    }

    private ulong ComputeStructSize(LLVMTypeRef structType)
    {
        ulong size = 0;
        foreach (var fieldType in structType.GetStructElementTypes())
        {
            size += GetTypeSizeInBytes(fieldType);
        }
        return size;
    }

    /// <summary>
    /// Verifies a constant (literal) index against a fixed-size array's known length at
    /// compile time. This is always enforced, even inside `unsafe { }`, since an
    /// out-of-range constant index is simply a bug rather than a deliberate unsafe operation.
    /// </summary>
    private void CheckConstantIndexInBounds(Expression indexExpr, int length, SourceLocation location)
    {
        if (indexExpr is LiteralExpr { Type: TokenType.IntegerLiteral } lit && lit.Value != null &&
            long.TryParse(lit.Value.ToString(), out long constIndex))
        {
            if (constIndex < 0 || constIndex >= length)
            {
                throw new CompileException(location, $"Array index {constIndex} is out of bounds for array of length {length}.");
            }
        }
    }

    /// <summary>
    /// Emits a runtime bounds check (index &gt;= length, using an unsigned comparison so
    /// negative indices are also caught) that throws an IndexOutOfBoundsException.
    /// Skipped entirely inside `unsafe { }` blocks, matching the design goal that safe ZV
    /// code can never perform an unchecked array access.
    /// </summary>
    private void EmitBoundsCheck(LLVMValueRef index, LLVMValueRef length, SourceLocation location)
    {
        if (InUnsafeContext) return;

        var indexWidth = index.TypeOf.IntWidth;
        var lengthWidth = length.TypeOf.IntWidth;
        LLVMValueRef indexNorm = index;
        LLVMValueRef lengthNorm = length;
        if (indexWidth < lengthWidth) indexNorm = _builder.BuildSExt(index, length.TypeOf, "bc_idx");
        else if (indexWidth > lengthWidth) lengthNorm = _builder.BuildSExt(length, index.TypeOf, "bc_len");

        var outOfBounds = _builder.BuildICmp(LLVMIntPredicate.LLVMIntUGE, indexNorm, lengthNorm, "bc_oob");
        EmitCondThrow(outOfBounds, "IndexOutOfBoundsException: array index out of bounds");
    }

    /// <summary>
    /// Raw pointers have no length metadata, so indexing them can never be bounds-checked.
    /// Require an explicit `unsafe { }` block so unchecked access is visible in source.
    /// </summary>
    /// <summary>
    /// GEP for an array/slice element access whose index has just been (or is about to be)
    /// bounds-checked. Outside `unsafe { }`, EmitBoundsCheck guarantees the index is in
    /// range, so the access can use an inbounds GEP: this gives LLVM's alias analysis and
    /// loop vectorizer much better information than a plain GEP. Inside `unsafe { }` the
    /// bounds check is skipped (see EmitBoundsCheck), so the index may legitimately be out
    /// of range and a plain (non-inbounds) GEP must be used to avoid undefined behavior.
    /// </summary>
    private LLVMValueRef BuildBoundsCheckedGEP2(LLVMTypeRef elementType, LLVMValueRef pointer, LLVMValueRef[] indices, string name)
    {
        return InUnsafeContext
            ? _builder.BuildGEP2(elementType, pointer, indices, name)
            : _builder.BuildInBoundsGEP2(elementType, pointer, indices, name);
    }

    private void RequireUnsafeForRawPointerIndex(SourceLocation location)
    {
        if (!InUnsafeContext)
        {
            throw new CompileException(location, "Indexing a raw pointer requires an 'unsafe' block.");
        }
    }

    private void BuildArrayFillLoop(LLVMTypeRef elementType, LLVMValueRef dataPtr, LLVMValueRef lengthValue, LLVMValueRef? fillValue)
    {
        if (fillValue == null)
        {
            // Zero-fill: a single memset over the whole allocation is far smaller IR than a
            // per-element store loop and vectorizes/optimizes trivially, unlike the loop.
            var elementSize = LLVMValueRef.CreateConstInt(GetInt64Type(), GetTypeSizeInBytes(elementType));
            var totalBytes = _builder.BuildMul(lengthValue, elementSize, "fill_bytes");
            var destI8 = _builder.BuildBitCast(dataPtr, GetPointerType(GetInt8Type()), "fill_dest_i8");
            var memset = GetOrAddFunction("memset", GetPointerType(GetInt8Type()),
                new[] { GetPointerType(GetInt8Type()), GetInt32Type(), GetInt64Type() });
            _builder.BuildCall2(_functionTypes["memset"], memset,
                new[] { destI8, LLVMValueRef.CreateConstInt(GetInt32Type(), 0), totalBytes }, "");
            return;
        }

        var func = _builder.InsertBlock.Parent;
        var condBB = _context.AppendBasicBlock(func, "fillcond");
        var bodyBB = _context.AppendBasicBlock(func, "fillbody");
        var endBB = _context.AppendBasicBlock(func, "fillend");

        var iAlloca = BuildEntryAlloca(GetInt64Type(), "fill_i");
        _builder.BuildStore(LLVMValueRef.CreateConstInt(GetInt64Type(), 0), iAlloca);

        _builder.BuildBr(condBB);
        _builder.PositionAtEnd(condBB);

        var iVal = _builder.BuildLoad2(GetInt64Type(), iAlloca, "fill_iv");
        var cond = _builder.BuildICmp(LLVMIntPredicate.LLVMIntSLT, iVal, lengthValue, "fill_cmp");
        _builder.BuildCondBr(cond, bodyBB, endBB);

        _builder.PositionAtEnd(bodyBB);
        var elPtr = _builder.BuildGEP2(elementType, dataPtr, new[] { iVal }, "fill_elptr");
        var value = ConvertToType(fillValue.Value, elementType);
        _builder.BuildStore(value, elPtr);

        var next = _builder.BuildAdd(iVal, LLVMValueRef.CreateConstInt(GetInt64Type(), 1), "fill_next");
        _builder.BuildStore(next, iAlloca);
        _builder.BuildBr(condBB);

        _builder.PositionAtEnd(endBB);
    }

    // Copies a fixed-size array (including nested/multi-dimensional arrays) from srcPtr to
    // destPtr using memmove. Both pointers must point to allocations of the same LLVM
    // array type given by arrayType.
    private void EmitFixedArrayCopy(LLVMValueRef destPtr, LLVMValueRef srcPtr, LLVMTypeRef arrayType)
    {
        var destI8 = _builder.BuildBitCast(destPtr, GetPointerType(GetInt8Type()), "copy_dest_i8");
        var srcI8 = _builder.BuildBitCast(srcPtr, GetPointerType(GetInt8Type()), "copy_src_i8");
        var sizeBytes = LLVMValueRef.CreateConstInt(GetInt64Type(), GetTypeSizeInBytes(arrayType));
        var memmove = GetOrAddFunction("memmove", GetPointerType(GetInt8Type()),
            new[] { GetPointerType(GetInt8Type()), GetPointerType(GetInt8Type()), GetInt64Type() });
        _builder.BuildCall2(_functionTypes["memmove"], memmove, new[] { destI8, srcI8, sizeBytes }, "");
    }
}
