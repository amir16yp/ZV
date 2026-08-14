using System;
using System.Collections.Generic;
using LLVMSharp.Interop;
using ZV.Compiler.AST;

namespace ZV.Compiler.Backend;

// C-like string manipulation builtins. CSTRING values are plain NUL-terminated i8*,
// so most of these are thin wrappers around the C
// runtime's <string.h> functions (already available since msvcrt/libc is linked for the
// hosted 'exe' target). A few (strdup, str_concat, str_equals, to_upper, to_lower) are
// ZV-native conveniences built on top of malloc/strcmp rather than direct libc calls.
public partial class LlvmGenerator
{
    private LLVMValueRef RequireStringArg(List<Expression> arguments, int index, string builtinName)
    {
        var value = VisitExpression(arguments[index]);
        if (value.TypeOf.Kind != LLVMTypeKind.LLVMPointerTypeKind)
            throw new Exception($"{builtinName}() argument {index + 1} must be a string.");
        return value;
    }

    private LLVMValueRef GenerateStrlenCall(List<Expression> arguments)
    {
        if (arguments.Count != 1)
            throw new Exception("strlen() expects exactly 1 argument (string).");

        var strlen = GetOrAddFunction("strlen", GetInt64Type(), new[] { GetPointerType(GetInt8Type()) });
        var str = RequireStringArg(arguments, 0, "strlen");
        return _builder.BuildCall2(_functionTypes["strlen"], strlen, new[] { str }, "strlentmp");
    }

    private LLVMValueRef GenerateStrcmpCall(List<Expression> arguments)
    {
        if (arguments.Count != 2)
            throw new Exception("strcmp() expects exactly 2 arguments (a, b).");

        var strcmp = GetOrAddFunction("strcmp", GetInt32Type(), new[] { GetPointerType(GetInt8Type()), GetPointerType(GetInt8Type()) });
        var a = RequireStringArg(arguments, 0, "strcmp");
        var b = RequireStringArg(arguments, 1, "strcmp");
        return _builder.BuildCall2(_functionTypes["strcmp"], strcmp, new[] { a, b }, "strcmptmp");
    }

    private LLVMValueRef GenerateStrncmpCall(List<Expression> arguments)
    {
        if (arguments.Count != 3)
            throw new Exception("strncmp() expects exactly 3 arguments (a, b, n).");

        var strncmp = GetOrAddFunction("strncmp", GetInt32Type(), new[] { GetPointerType(GetInt8Type()), GetPointerType(GetInt8Type()), GetInt64Type() });
        var args = new[]
        {
            RequireStringArg(arguments, 0, "strncmp"),
            RequireStringArg(arguments, 1, "strncmp"),
            ConvertToType(VisitExpression(arguments[2]), GetInt64Type())
        };
        return _builder.BuildCall2(_functionTypes["strncmp"], strncmp, args, "strncmptmp");
    }

    private LLVMValueRef GenerateStrcpyCall(List<Expression> arguments)
    {
        if (arguments.Count != 2)
            throw new Exception("strcpy() expects exactly 2 arguments (dest, src).");

        var strcpy = GetOrAddFunction("strcpy", GetPointerType(GetInt8Type()), new[] { GetPointerType(GetInt8Type()), GetPointerType(GetInt8Type()) });
        var dest = RequireStringArg(arguments, 0, "strcpy");
        var src = RequireStringArg(arguments, 1, "strcpy");
        return _builder.BuildCall2(_functionTypes["strcpy"], strcpy, new[] { dest, src }, "strcpytmp");
    }

    private LLVMValueRef GenerateStrncpyCall(List<Expression> arguments)
    {
        if (arguments.Count != 3)
            throw new Exception("strncpy() expects exactly 3 arguments (dest, src, n).");

        var strncpy = GetOrAddFunction("strncpy", GetPointerType(GetInt8Type()), new[] { GetPointerType(GetInt8Type()), GetPointerType(GetInt8Type()), GetInt64Type() });
        var args = new[]
        {
            RequireStringArg(arguments, 0, "strncpy"),
            RequireStringArg(arguments, 1, "strncpy"),
            ConvertToType(VisitExpression(arguments[2]), GetInt64Type())
        };
        return _builder.BuildCall2(_functionTypes["strncpy"], strncpy, args, "strncpytmp");
    }

    private LLVMValueRef GenerateStrcatCall(List<Expression> arguments)
    {
        if (arguments.Count != 2)
            throw new Exception("strcat() expects exactly 2 arguments (dest, src).");

        var strcat = GetOrAddFunction("strcat", GetPointerType(GetInt8Type()), new[] { GetPointerType(GetInt8Type()), GetPointerType(GetInt8Type()) });
        var dest = RequireStringArg(arguments, 0, "strcat");
        var src = RequireStringArg(arguments, 1, "strcat");
        return _builder.BuildCall2(_functionTypes["strcat"], strcat, new[] { dest, src }, "strcattmp");
    }

    private LLVMValueRef GenerateStrncatCall(List<Expression> arguments)
    {
        if (arguments.Count != 3)
            throw new Exception("strncat() expects exactly 3 arguments (dest, src, n).");

        var strncat = GetOrAddFunction("strncat", GetPointerType(GetInt8Type()), new[] { GetPointerType(GetInt8Type()), GetPointerType(GetInt8Type()), GetInt64Type() });
        var args = new[]
        {
            RequireStringArg(arguments, 0, "strncat"),
            RequireStringArg(arguments, 1, "strncat"),
            ConvertToType(VisitExpression(arguments[2]), GetInt64Type())
        };
        return _builder.BuildCall2(_functionTypes["strncat"], strncat, args, "strncattmp");
    }

    private LLVMValueRef GenerateStrchrCall(List<Expression> arguments)
    {
        if (arguments.Count != 2)
            throw new Exception("strchr() expects exactly 2 arguments (str, ch).");

        var strchr = GetOrAddFunction("strchr", GetPointerType(GetInt8Type()), new[] { GetPointerType(GetInt8Type()), GetInt32Type() });
        var str = RequireStringArg(arguments, 0, "strchr");
        var ch = ConvertToType(VisitExpression(arguments[1]), GetInt32Type());
        return _builder.BuildCall2(_functionTypes["strchr"], strchr, new[] { str, ch }, "strchrtmp");
    }

    private LLVMValueRef GenerateStrstrCall(List<Expression> arguments)
    {
        if (arguments.Count != 2)
            throw new Exception("strstr() expects exactly 2 arguments (haystack, needle).");

        var strstr = GetOrAddFunction("strstr", GetPointerType(GetInt8Type()), new[] { GetPointerType(GetInt8Type()), GetPointerType(GetInt8Type()) });
        var haystack = RequireStringArg(arguments, 0, "strstr");
        var needle = RequireStringArg(arguments, 1, "strstr");
        return _builder.BuildCall2(_functionTypes["strstr"], strstr, new[] { haystack, needle }, "strstrtmp");
    }

    // Allocates a new heap copy of `str` (malloc(strlen(str) + 1) + strcpy), since ZV does not
    // rely on the platform's strdup (not reliably exported cross-platform). Throws
    // OutOfMemoryException if the allocation fails; the caller owns the returned pointer.
    private LLVMValueRef GenerateStrdupCall(List<Expression> arguments)
    {
        if (arguments.Count != 1)
            throw new Exception("strdup() expects exactly 1 argument (string).");

        var str = RequireStringArg(arguments, 0, "strdup");

        var strlen = GetOrAddFunction("strlen", GetInt64Type(), new[] { GetPointerType(GetInt8Type()) });
        var length = _builder.BuildCall2(_functionTypes["strlen"], strlen, new[] { str }, "strdup_len");
        var size = _builder.BuildAdd(length, LLVMValueRef.CreateConstInt(GetInt64Type(), 1), "strdup_size");

        var malloc = GetOrAddFunction("malloc", GetPointerType(GetInt8Type()), new[] { GetInt64Type() });
        var buffer = _builder.BuildCall2(_functionTypes["malloc"], malloc, new[] { size }, "strduptmp");
        EmitNullCheckOrThrow(buffer, "OutOfMemoryException: memory allocation failed");

        var strcpy = GetOrAddFunction("strcpy", GetPointerType(GetInt8Type()), new[] { GetPointerType(GetInt8Type()), GetPointerType(GetInt8Type()) });
        _builder.BuildCall2(_functionTypes["strcpy"], strcpy, new[] { buffer, str }, "strdup_copy");

        return buffer;
    }

    // Allocates a new heap string containing `a` followed by `b`
    // (malloc(strlen(a) + strlen(b) + 1) + strcpy + strcat). Throws OutOfMemoryException if
    // the allocation fails; the caller owns the returned pointer.
    private LLVMValueRef GenerateStrConcatCall(List<Expression> arguments)
    {
        if (arguments.Count != 2)
            throw new Exception("str_concat() expects exactly 2 arguments (a, b).");

        var a = RequireStringArg(arguments, 0, "str_concat");
        var b = RequireStringArg(arguments, 1, "str_concat");

        var strlen = GetOrAddFunction("strlen", GetInt64Type(), new[] { GetPointerType(GetInt8Type()) });
        var lenA = _builder.BuildCall2(_functionTypes["strlen"], strlen, new[] { a }, "concat_lena");
        var lenB = _builder.BuildCall2(_functionTypes["strlen"], strlen, new[] { b }, "concat_lenb");
        var totalLen = _builder.BuildAdd(lenA, lenB, "concat_len");
        var size = _builder.BuildAdd(totalLen, LLVMValueRef.CreateConstInt(GetInt64Type(), 1), "concat_size");

        var malloc = GetOrAddFunction("malloc", GetPointerType(GetInt8Type()), new[] { GetInt64Type() });
        var buffer = _builder.BuildCall2(_functionTypes["malloc"], malloc, new[] { size }, "concattmp");
        EmitNullCheckOrThrow(buffer, "OutOfMemoryException: memory allocation failed");

        var strcpy = GetOrAddFunction("strcpy", GetPointerType(GetInt8Type()), new[] { GetPointerType(GetInt8Type()), GetPointerType(GetInt8Type()) });
        _builder.BuildCall2(_functionTypes["strcpy"], strcpy, new[] { buffer, a }, "concat_copy");

        var strcat = GetOrAddFunction("strcat", GetPointerType(GetInt8Type()), new[] { GetPointerType(GetInt8Type()), GetPointerType(GetInt8Type()) });
        _builder.BuildCall2(_functionTypes["strcat"], strcat, new[] { buffer, b }, "concat_cat");

        return buffer;
    }

    // BOOL convenience wrapper around strcmp(a, b) == 0.
    private LLVMValueRef GenerateStrEqualsCall(List<Expression> arguments)
    {
        if (arguments.Count != 2)
            throw new Exception("str_equals() expects exactly 2 arguments (a, b).");

        var strcmp = GetOrAddFunction("strcmp", GetInt32Type(), new[] { GetPointerType(GetInt8Type()), GetPointerType(GetInt8Type()) });
        var a = RequireStringArg(arguments, 0, "str_equals");
        var b = RequireStringArg(arguments, 1, "str_equals");
        var result = _builder.BuildCall2(_functionTypes["strcmp"], strcmp, new[] { a, b }, "str_equals_cmp");
        return _builder.BuildICmp(LLVMIntPredicate.LLVMIntEQ, result, LLVMValueRef.CreateConstInt(GetInt32Type(), 0), "str_equals");
    }

    // Mutates `str` in place, converting ASCII lowercase letters to uppercase, and returns it.
    private LLVMValueRef GenerateToUpperCall(List<Expression> arguments)
    {
        if (arguments.Count != 1)
            throw new Exception("to_upper() expects exactly 1 argument (string).");

        var str = RequireStringArg(arguments, 0, "to_upper");
        EmitAsciiCaseConvertLoop(str, isUpper: true);
        return str;
    }

    // Mutates `str` in place, converting ASCII uppercase letters to lowercase, and returns it.
    private LLVMValueRef GenerateToLowerCall(List<Expression> arguments)
    {
        if (arguments.Count != 1)
            throw new Exception("to_lower() expects exactly 1 argument (string).");

        var str = RequireStringArg(arguments, 0, "to_lower");
        EmitAsciiCaseConvertLoop(str, isUpper: false);
        return str;
    }

    private void EmitAsciiCaseConvertLoop(LLVMValueRef str, bool isUpper)
    {
        var i8 = GetInt8Type();
        char rangeStart = isUpper ? 'a' : 'A';
        char rangeEnd = isUpper ? 'z' : 'Z';
        int delta = isUpper ? -32 : 32;

        BuildCStringLoop(str, (ch, idx) =>
        {
            var inRange = _builder.BuildAnd(
                _builder.BuildICmp(LLVMIntPredicate.LLVMIntSGE, ch, LLVMValueRef.CreateConstInt(i8, rangeStart), "case_ge"),
                _builder.BuildICmp(LLVMIntPredicate.LLVMIntSLE, ch, LLVMValueRef.CreateConstInt(i8, rangeEnd), "case_le"),
                "case_in_range");

            var converted = _builder.BuildAdd(ch, LLVMValueRef.CreateConstInt(i8, unchecked((ulong)(long)delta)), "case_converted");
            var newCh = _builder.BuildSelect(inRange, converted, ch, "case_new");

            var charPtr = _builder.BuildGEP2(i8, str, new[] { idx }, "case_charptr");
            _builder.BuildStore(newCh, charPtr);
        });
    }

    // Builds a new STRING value by concatenating two STRING values. The resulting data buffer
    // is freshly allocated and must be freed by the caller if the value is kept.
    private LLVMValueRef BuildStringConcat(LLVMValueRef a, LLVMValueRef b)
    {
        var strType = GetStringStructType();
        var dataA = _builder.BuildExtractValue(a, 0, "concat_data_a");
        var lenA = _builder.BuildExtractValue(a, 1, "concat_len_a");
        var dataB = _builder.BuildExtractValue(b, 0, "concat_data_b");
        var lenB = _builder.BuildExtractValue(b, 1, "concat_len_b");
        var totalLen = _builder.BuildAdd(lenA, lenB, "concat_total_len");

        var malloc = GetOrAddFunction("malloc", GetPointerType(GetInt8Type()), new[] { GetInt64Type() });
        var buffer = _builder.BuildCall2(_functionTypes["malloc"], malloc, new[] { totalLen }, "concat_buf");
        EmitNullCheckOrThrow(buffer, "OutOfMemoryException: memory allocation failed");

        var memcpy = GetOrAddFunction("memcpy", GetPointerType(GetInt8Type()), new[] { GetPointerType(GetInt8Type()), GetPointerType(GetInt8Type()), GetInt64Type() });
        _builder.BuildCall2(_functionTypes["memcpy"], memcpy, new[] { buffer, dataA, lenA }, "concat_copy_a");

        var destB = _builder.BuildGEP2(GetInt8Type(), buffer, new[] { lenA }, "concat_dest_b");
        _builder.BuildCall2(_functionTypes["memcpy"], memcpy, new[] { destB, dataB, lenB }, "concat_copy_b");

        var baseVal = LLVMValueRef.CreateConstNull(strType);
        var withData = _builder.BuildInsertValue(baseVal, buffer, 0, "concat_str_data");
        return _builder.BuildInsertValue(withData, totalLen, 1, "concat_str");
    }

    // Builds a boolean result indicating whether two STRING values have identical content.
    private LLVMValueRef BuildStringEquals(LLVMValueRef a, LLVMValueRef b)
    {
        var lenA = _builder.BuildExtractValue(a, 1, "eq_len_a");
        var lenB = _builder.BuildExtractValue(b, 1, "eq_len_b");
        var lenDiff = _builder.BuildICmp(LLVMIntPredicate.LLVMIntNE, lenA, lenB, "eq_len_diff");

        var func = _builder.InsertBlock.Parent;
        var cmpBB = _context.AppendBasicBlock(func, "str_eq_cmp");
        var falseBB = _context.AppendBasicBlock(func, "str_eq_false");
        var mergeBB = _context.AppendBasicBlock(func, "str_eq_merge");

        _builder.BuildCondBr(lenDiff, falseBB, cmpBB);

        _builder.PositionAtEnd(falseBB);
        _builder.BuildBr(mergeBB);

        _builder.PositionAtEnd(cmpBB);
        var dataA = _builder.BuildExtractValue(a, 0, "eq_data_a");
        var dataB = _builder.BuildExtractValue(b, 0, "eq_data_b");
        var memcmp = GetOrAddFunction("memcmp", GetInt32Type(), new[] { GetPointerType(GetInt8Type()), GetPointerType(GetInt8Type()), GetInt64Type() });
        var cmpResult = _builder.BuildCall2(_functionTypes["memcmp"], memcmp, new[] { dataA, dataB, lenA }, "eq_cmp");
        var isEqual = _builder.BuildICmp(LLVMIntPredicate.LLVMIntEQ, cmpResult, LLVMValueRef.CreateConstInt(GetInt32Type(), 0), "eq_result");
        _builder.BuildBr(mergeBB);

        _builder.PositionAtEnd(mergeBB);
        var phi = _builder.BuildPhi(GetInt1Type(), "str_eq");
        phi.AddIncoming(new[] { LLVMValueRef.CreateConstInt(GetInt1Type(), 0), isEqual }, new[] { falseBB, cmpBB }, 2);
        return phi;
    }
}
