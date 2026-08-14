using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using LLVMSharp.Interop;
using ZV.Compiler.AST;
using ZV.Compiler.Lexer;

namespace ZV.Compiler.Backend;

public partial class LlvmGenerator
{
    // Low-level kernel/freestanding-only builtins (CPU intrinsics, port I/O, VGA/serial/PS2/
    // framebuffer access, etc). These have no meaning on a hosted OS process and are rejected
    // outright unless compiling for a freestanding/kernel target - see IsFreestandingTarget and
    // CheckKernelBuiltinAvailable() below.
    private static readonly HashSet<string> KernelBuiltinNames = new()
    {
        "halt", "cli", "sti",
        "port_out8", "port_out16", "port_out32",
        "port_in8", "port_in16", "port_in32",
        "volatile_read", "volatile_write",
        "serial_init", "serial_write_char", "serial_write", "serial_read_char", "serial_has_data",
        "vga_putc", "vga_clear", "vga_print",
        "ps2_has_data", "ps2_read_data", "ps2_write_data", "ps2_send_command", "ps2_scancode_to_ascii",
        "keyboard_getchar",
        "fb_available", "fb_width", "fb_height", "fb_pitch", "fb_bpp", "fb_set_pixel", "fb_fill_rect", "fb_clear"
    };

    // Rejects kernel/freestanding-only builtins when compiling for a normal (hosted) target.
    private void CheckKernelBuiltinAvailable(string name)
    {
        if (!IsFreestandingTarget)
        {
            throw new Exception($"'{name}' is a kernel builtin and is only available when targeting a freestanding/kernel target " +
                                 "(e.g. 'os-x86'): it has no meaning on a hosted Windows/Linux process.");
        }
    }

    private LLVMValueRef GenerateBuiltinCall(string name, List<Expression> arguments)
    {
        if (KernelBuiltinNames.Contains(name))
            CheckKernelBuiltinAvailable(name);

        return name switch
        {
            "print" => GeneratePrintCall(arguments),
            "copy" => GenerateCopyCall(arguments),
            "move" => GenerateMoveCall(arguments),
            "fopen" => GenerateFopenCall(arguments),
            "fclose" => GenerateFcloseCall(arguments),
            "fread" => GenerateFreadCall(arguments),
            "fwrite" => GenerateFwriteCall(arguments),
            "fseek" => GenerateFseekCall(arguments),
            "ftell" => GenerateFtellCall(arguments),
            "remove" => GenerateRemoveCall(arguments),
            "rename" => GenerateRenameCall(arguments),
            "mkdir" => GenerateMkdirCall(arguments),
            "rmdir" => GenerateRmdirCall(arguments),
            "len" => GenerateLenCall(arguments),
            "cstr" => GenerateCstrCall(arguments),
            "wstr" => GenerateWstrCall(arguments),
            "array_copy" => GenerateArrayCopyCall(arguments),
            "alloc" => GenerateAllocCall(arguments),
            "realloc" => GenerateReallocCall(arguments),
            "get_timestamp" => GenerateGetTimestampCall(arguments),
            "get_timestamp_ms" => GenerateGetTimestampMsCall(arguments),
            "halt" => GenerateHaltCall(),
            "cli" => GenerateCliCall(),
            "sti" => GenerateStiCall(),
            "port_out8" => GeneratePortOutCall(arguments, 8),
            "port_out16" => GeneratePortOutCall(arguments, 16),
            "port_out32" => GeneratePortOutCall(arguments, 32),
            "port_in8" => GeneratePortInCall(arguments, 8),
            "port_in16" => GeneratePortInCall(arguments, 16),
            "port_in32" => GeneratePortInCall(arguments, 32),
            "volatile_read" => GenerateVolatileReadCall(arguments),
            "volatile_write" => GenerateVolatileWriteCall(arguments),
            "serial_init" => GenerateSerialInitCall(arguments),
            "serial_write_char" => GenerateSerialWriteCharCall(arguments),
            "serial_write" => GenerateSerialWriteCall(arguments),
            "serial_read_char" => GenerateSerialReadCharCall(arguments),
            "serial_has_data" => GenerateSerialHasDataCall(arguments),
            "vga_putc" => GenerateVgaPutcCall(arguments),
            "vga_clear" => GenerateVgaClearCall(arguments),
            "vga_print" => GenerateVgaPrintCall(arguments),
            "ps2_has_data" => GeneratePs2HasDataCall(arguments),
            "ps2_read_data" => GeneratePs2ReadDataCall(arguments),
            "ps2_write_data" => GeneratePs2WriteDataCall(arguments),
            "ps2_send_command" => GeneratePs2SendCommandCall(arguments),
            "ps2_scancode_to_ascii" => GeneratePs2ScancodeToAsciiCall(arguments),
            "keyboard_getchar" => GenerateKeyboardGetcharCall(arguments),
            "fb_available" => GenerateFbAvailableCall(arguments),
            "fb_width" => GenerateFbWidthCall(arguments),
            "fb_height" => GenerateFbHeightCall(arguments),
            "fb_pitch" => GenerateFbPitchCall(arguments),
            "fb_bpp" => GenerateFbBppCall(arguments),
            "fb_set_pixel" => GenerateFbSetPixelCall(arguments),
            "fb_fill_rect" => GenerateFbFillRectCall(arguments),
            "fb_clear" => GenerateFbClearCall(arguments),
            "strlen" => GenerateStrlenCall(arguments),
            "strcmp" => GenerateStrcmpCall(arguments),
            "strncmp" => GenerateStrncmpCall(arguments),
            "strcpy" => GenerateStrcpyCall(arguments),
            "strncpy" => GenerateStrncpyCall(arguments),
            "strcat" => GenerateStrcatCall(arguments),
            "strncat" => GenerateStrncatCall(arguments),
            "strchr" => GenerateStrchrCall(arguments),
            "strstr" => GenerateStrstrCall(arguments),
            "strdup" => GenerateStrdupCall(arguments),
            "str_concat" => GenerateStrConcatCall(arguments),
            "str_equals" => GenerateStrEqualsCall(arguments),
            "to_upper" => GenerateToUpperCall(arguments),
            "to_lower" => GenerateToLowerCall(arguments),
            "Exception" => GenerateExceptionConstructor(arguments, null),
            "respawn" => GenerateRespawnCall(arguments),
            "exit" => GenerateExitCall(arguments),
            _ when ParseBuiltinNames.Contains(name) => GenerateParseCall(name, arguments),
            _ when CursesBuiltinNames.Contains(name) => GenerateCursesBuiltinCall(name, arguments),
            _ when ThreadBuiltinNames.Contains(name) => GenerateThreadBuiltinCall(name, arguments),
            _ when AtomicBuiltinNames.Contains(name) => GenerateAtomicBuiltinCall(name, arguments),
            _ => throw new Exception($"Unknown builtin: {name}")
        };
    }

    private LLVMValueRef GenerateMoveCall(List<Expression> arguments)
    {
        if (arguments.Count != 1)
            throw new Exception("move() expects exactly 1 argument.");

        return VisitMove(arguments[0]);
    }

    private LLVMValueRef GenerateCopyCall(List<Expression> arguments)
    {
        if (arguments.Count != 1)
            throw new Exception("copy() expects exactly 1 argument.");

        return VisitCopy(arguments[0]);
    }

    private LLVMValueRef GenerateLenCall(List<Expression> arguments)
    {
        if (arguments.Count != 1)
            throw new Exception("len() expects exactly 1 argument.");

        // Fixed-size arrays have a compile-time-known length (the outermost dimension).
        if (TryGetFixedArrayType(arguments[0], out var fixedArrayType))
        {
            return LLVMValueRef.CreateConstInt(GetInt64Type(), (ulong)fixedArrayType.ArrayLength);
        }

        var arg = VisitExpression(arguments[0]);
        if (arg.TypeOf.Kind == LLVMTypeKind.LLVMStructTypeKind)
        {
            // Array struct { T*, i64 } or STRING { i8*, i64 }
            return _builder.BuildExtractValue(arg, 1, "lentmp");
        }

        throw new Exception("len() argument must be an array or STRING.");
    }

    // array_copy(destination, source): copies the whole of `source` into the start of
    // `destination`, using memmove so overlapping/aliased arrays copy correctly.
    // array_copy(destination, dest_offset, source, src_offset, count): copies `count`
    // elements starting at `src_offset` in `source` into `destination` starting at
    // `dest_offset`.
    //
    // Nonsensical copies are rejected at compile time whenever they can be proven from the
    // call site alone (mismatched element types; negative/overflowing constant offsets or
    // count; a statically-known array literal that is provably too short) and are otherwise
    // guarded with a runtime bounds check that throws ArrayCopyException.
    private LLVMValueRef GenerateArrayCopyCall(List<Expression> arguments)
    {
        if (_builder.InsertBlock.Handle == IntPtr.Zero)
            throw new Exception("array_copy() is not allowed at global scope.");

        return arguments.Count switch
        {
            2 => GenerateArrayCopyWhole(arguments[0], arguments[1]),
            5 => GenerateArrayCopyRange(arguments[0], arguments[1], arguments[2], arguments[3], arguments[4]),
            _ => throw new Exception("array_copy() expects either 2 arguments (destination, source) or " +
                                      "5 arguments (destination, dest_offset, source, src_offset, count).")
        };
    }

    private LLVMValueRef GenerateArrayCopyWhole(Expression destExpr, Expression srcExpr)
    {
        var dest = GetArrayCopyOperand(destExpr, "destination");
        var src = GetArrayCopyOperand(srcExpr, "source");

        if (dest.ElementType.Handle != src.ElementType.Handle)
            throw new CompileException(destExpr.Location,
                "array_copy(): destination and source arrays have different element types; cannot copy between them.");

        var destStaticLen = TryGetStaticArrayLength(destExpr);
        var srcStaticLen = TryGetStaticArrayLength(srcExpr);
        if (destStaticLen.HasValue && srcStaticLen.HasValue && srcStaticLen.Value > destStaticLen.Value)
        {
            throw new CompileException(destExpr.Location,
                $"array_copy(): source array has {srcStaticLen.Value} element(s) but destination only has " +
                $"{destStaticLen.Value}; copy would overflow the destination.");
        }

        // Runtime guard: the source must fit within the destination.
        var fits = _builder.BuildICmp(LLVMIntPredicate.LLVMIntULE, src.Length, dest.Length, "arrcpy_fits");
        var overflow = _builder.BuildNot(fits, "arrcpy_overflow");
        EmitCondThrow(overflow, "ArrayCopyException: source array is longer than the destination array");

        EmitArrayMemmove(dest.DataPtr, src.DataPtr, src.Length, dest.ElementType);
        return LLVMValueRef.CreateConstInt(GetInt32Type(), 0);
    }

    private LLVMValueRef GenerateArrayCopyRange(Expression destExpr, Expression destOffsetExpr, Expression srcExpr, Expression srcOffsetExpr, Expression countExpr)
    {
        var dest = GetArrayCopyOperand(destExpr, "destination");
        var src = GetArrayCopyOperand(srcExpr, "source");

        if (dest.ElementType.Handle != src.ElementType.Handle)
            throw new CompileException(destExpr.Location,
                "array_copy(): destination and source arrays have different element types; cannot copy between them.");

        var destOffsetConst = TryGetStaticIntLiteral(destOffsetExpr);
        var srcOffsetConst = TryGetStaticIntLiteral(srcOffsetExpr);
        var countConst = TryGetStaticIntLiteral(countExpr);

        if (destOffsetConst is < 0)
            throw new CompileException(destOffsetExpr.Location, "array_copy(): dest_offset cannot be negative.");
        if (srcOffsetConst is < 0)
            throw new CompileException(srcOffsetExpr.Location, "array_copy(): src_offset cannot be negative.");
        if (countConst is < 0)
            throw new CompileException(countExpr.Location, "array_copy(): count cannot be negative.");

        var destStaticLen = TryGetStaticArrayLength(destExpr);
        var srcStaticLen = TryGetStaticArrayLength(srcExpr);
        if (countConst.HasValue)
        {
            if (destStaticLen.HasValue && destOffsetConst.HasValue && destOffsetConst.Value + countConst.Value > destStaticLen.Value)
            {
                throw new CompileException(destExpr.Location,
                    $"array_copy(): copying {countConst.Value} element(s) starting at destination offset " +
                    $"{destOffsetConst.Value} would overflow the destination array of length {destStaticLen.Value}.");
            }
            if (srcStaticLen.HasValue && srcOffsetConst.HasValue && srcOffsetConst.Value + countConst.Value > srcStaticLen.Value)
            {
                throw new CompileException(srcExpr.Location,
                    $"array_copy(): copying {countConst.Value} element(s) starting at source offset " +
                    $"{srcOffsetConst.Value} would read past the end of the source array of length {srcStaticLen.Value}.");
            }
        }

        var destOffsetValue = ConvertToType(VisitExpression(destOffsetExpr), GetInt64Type());
        var srcOffsetValue = ConvertToType(VisitExpression(srcOffsetExpr), GetInt64Type());
        var countValue = ConvertToType(VisitExpression(countExpr), GetInt64Type());

        EmitArrayCopyRangeBoundsCheck(destOffsetValue, countValue, dest.Length);
        EmitArrayCopyRangeBoundsCheck(srcOffsetValue, countValue, src.Length);

        var destPtr = _builder.BuildGEP2(dest.ElementType, dest.DataPtr, new[] { destOffsetValue }, "arrcpy_destptr");
        var srcPtr = _builder.BuildGEP2(src.ElementType, src.DataPtr, new[] { srcOffsetValue }, "arrcpy_srcptr");

        EmitArrayMemmove(destPtr, srcPtr, countValue, dest.ElementType);
        return LLVMValueRef.CreateConstInt(GetInt32Type(), 0);
    }

    // Extracts the data pointer, element type and length of an array_copy() operand.
    // Accepts both dynamic arrays (T[]) and fixed-size arrays (T[N] / T[N][M]...).
    private (LLVMValueRef Value, LLVMTypeRef ElementType, LLVMValueRef DataPtr, LLVMValueRef Length) GetArrayCopyOperand(Expression expr, string role)
    {
        if (TryGetFixedArrayInfo(expr, out var fixedArrayType, out var fixedArrayPtr))
        {
            var (innerType, totalCount) = GetFlattenedArrayInfo(fixedArrayType);
            var fixedDataPtr = _builder.BuildBitCast(fixedArrayPtr, GetPointerType(innerType), "fixed_arrcpy_data");
            var fixedLength = LLVMValueRef.CreateConstInt(GetInt64Type(), (ulong)totalCount);
            return (default, innerType, fixedDataPtr, fixedLength);
        }

        var value = VisitExpression(expr);
        if (value.TypeOf.Kind != LLVMTypeKind.LLVMStructTypeKind || IsStringStructType(value.TypeOf) ||
            !_arrayElementTypes.TryGetValue(value.TypeOf, out var elementType))
        {
            throw new CompileException(expr.Location, $"array_copy() {role} argument must be an array.");
        }

        var dataPtr = _builder.BuildExtractValue(value, 0, "arrcpy_data");
        var length = _builder.BuildExtractValue(value, 1, "arrcpy_len");
        return (value, elementType, dataPtr, length);
    }

    // Best-effort compile-time array length: known for array literals, heap allocations with
    // a constant size, and fixed-size arrays (the flattened element count).
    private long? TryGetStaticArrayLength(Expression expr)
    {
        if (TryGetFixedArrayType(expr, out var fixedArrayType))
        {
            return GetFlattenedArrayInfo(fixedArrayType).TotalCount;
        }

        return expr switch
        {
            ArrayInitExpr init => init.Elements.Count,
            ArrayAllocExpr { Size: LiteralExpr { Type: TokenType.IntegerLiteral, Value: not null } lit } when long.TryParse(lit.Value!.ToString(), out var n) => n,
            _ => null
        };
    }

    // Best-effort compile-time integer constant, including simple negated literals (-1).
    private long? TryGetStaticIntLiteral(Expression expr)
    {
        if (expr is LiteralExpr { Type: TokenType.IntegerLiteral, Value: not null } lit && long.TryParse(lit.Value!.ToString(), out var n))
            return n;
        if (expr is UnaryExpr { Operator.Type: TokenType.Minus } unary && TryGetStaticIntLiteral(unary.Right) is long inner)
            return -inner;
        return null;
    }

    // Runtime check that [offset, offset + count) fits within [0, length), using unsigned
    // comparisons so a negative (i.e. huge-unsigned) offset or count is also caught. Computing
    // `length - offset` instead of `offset + count` avoids a false negative from `offset +
    // count` overflowing back into range.
    private void EmitArrayCopyRangeBoundsCheck(LLVMValueRef offset, LLVMValueRef count, LLVMValueRef length)
    {
        var offsetTooBig = _builder.BuildICmp(LLVMIntPredicate.LLVMIntUGT, offset, length, "arrcpy_offbad");
        var remaining = _builder.BuildSub(length, offset, "arrcpy_remaining");
        var countTooBig = _builder.BuildICmp(LLVMIntPredicate.LLVMIntUGT, count, remaining, "arrcpy_cntbad");
        var bad = _builder.BuildOr(offsetTooBig, countTooBig, "arrcpy_bad");
        EmitCondThrow(bad, "ArrayCopyException: array_copy() range is out of bounds");
    }

    private void EmitArrayMemmove(LLVMValueRef destElementPtr, LLVMValueRef srcElementPtr, LLVMValueRef countValue, LLVMTypeRef elementType)
    {
        var elementSize = GetElementSize(elementType);
        var totalBytes = _builder.BuildMul(countValue, elementSize, "arrcpy_bytes");

        var destI8 = _builder.BuildBitCast(destElementPtr, GetPointerType(GetInt8Type()), "arrcpy_dest_i8");
        var srcI8 = _builder.BuildBitCast(srcElementPtr, GetPointerType(GetInt8Type()), "arrcpy_src_i8");

        var memmove = GetOrAddFunction("memmove", GetPointerType(GetInt8Type()),
            new[] { GetPointerType(GetInt8Type()), GetPointerType(GetInt8Type()), GetInt64Type() });
        _builder.BuildCall2(_functionTypes["memmove"], memmove, new[] { destI8, srcI8, totalBytes }, "");
    }

    // Converts a STRING to a CSTRING by producing a fresh, NUL-terminated heap copy of its
    // bytes (STRING does not guarantee NUL termination, e.g. after concatenation). This
    // allocation is a ZV-managed temporary: bound directly to a CSTRING variable
    // (`CSTRING p = cstr(s);`) it becomes owned and is freed automatically at the end of that
    // variable's scope, same as strdup()/str_concat(); used inline as a call argument, it is
    // freed automatically once the enclosing statement finishes evaluating (see
    // _pendingCstrTemps / FreeUnclaimedCstrTemps). Throws OutOfMemoryException on allocation
    // failure.
    private LLVMValueRef GenerateCstrCall(List<Expression> arguments)
    {
        if (arguments.Count != 1)
            throw new Exception("cstr() expects exactly 1 argument.");

        // Note: cstr() always returns a fresh heap allocation for a STRING argument (never
        // the source's own buffer), even for a STRING literal whose bytes already live in a
        // NUL-terminated global constant - callers (e.g. `CSTRING s = cstr("literal");`)
        // uniformly treat the result as an owned, freeable buffer and generate a matching
        // destructor/free at scope exit, so handing out the global pointer directly would
        // cause a free() on non-heap memory.
        var arg = VisitExpression(arguments[0]);
        if (IsStringStructType(arg.TypeOf))
        {
            var data = _builder.BuildExtractValue(arg, 0, "cstr_data");
            var length = _builder.BuildExtractValue(arg, 1, "cstr_len");
            var size = _builder.BuildAdd(length, LLVMValueRef.CreateConstInt(GetInt64Type(), 1), "cstr_size");

            var malloc = GetOrAddFunction("malloc", GetPointerType(GetInt8Type()), new[] { GetInt64Type() });
            var buffer = _builder.BuildCall2(_functionTypes["malloc"], malloc, new[] { size }, "cstrtmp");
            EmitNullCheckOrThrow(buffer, "OutOfMemoryException: memory allocation failed");

            var memcpy = GetOrAddFunction("memcpy", GetPointerType(GetInt8Type()), new[] { GetPointerType(GetInt8Type()), GetPointerType(GetInt8Type()), GetInt64Type() });
            _builder.BuildCall2(_functionTypes["memcpy"], memcpy, new[] { buffer, data, length }, "cstr_copy");

            var nulPtr = _builder.BuildGEP2(GetInt8Type(), buffer, new[] { length }, "cstr_nul_ptr");
            _builder.BuildStore(LLVMValueRef.CreateConstInt(GetInt8Type(), 0), nulPtr);

            _pendingCstrTemps.Add(buffer);
            return buffer;
        }

        if (arg.TypeOf.Kind == LLVMTypeKind.LLVMPointerTypeKind)
        {
            // Already a CSTRING / raw pointer; return as-is (non-owning, nothing to free).
            return arg;
        }

        throw new Exception("cstr() argument must be a STRING or CSTRING.");
    }

    // Converts a STRING to a WSTRING (NUL-terminated UTF-16) by producing a fresh heap copy
    // of its bytes. On Windows this uses MultiByteToWideChar(CP_UTF8); the resulting
    // allocation is a ZV-managed temporary just like cstr(). On non-Windows hosts this
    // builtin is currently unsupported.
    private LLVMValueRef GenerateWstrCall(List<Expression> arguments)
    {
        if (arguments.Count != 1)
            throw new Exception("wstr() expects exactly 1 argument.");

        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            throw new Exception("wstr() is currently only supported on Windows.");

        var arg = VisitExpression(arguments[0]);

        // CSTRING / WSTRING / raw pointer: pass through unchanged (non-owning).
        if (arg.TypeOf.Kind == LLVMTypeKind.LLVMPointerTypeKind)
        {
            // If it's a CSTRING (i8*) we still need to convert to UTF-16; a WSTRING (i16*)
            // can be returned as-is. Distinguish by element type.
            var elemType = arg.TypeOf.ElementType;
            if (elemType.Kind == LLVMTypeKind.LLVMIntegerTypeKind && elemType.IntWidth == 16)
            {
                // Already a wide pointer (WSTRING or i16*).
                return arg;
            }
        }

        // We need an i8* NUL-terminated source buffer for MultiByteToWideChar.
        // For STRING, build one; for CSTRING/raw pointer, use it directly.
        LLVMValueRef utf8Ptr;
        bool freeUtf8Source = false;
        if (IsStringStructType(arg.TypeOf))
        {
            var data = _builder.BuildExtractValue(arg, 0, "wstr_data");
            var length = _builder.BuildExtractValue(arg, 1, "wstr_len");
            var size = _builder.BuildAdd(length, LLVMValueRef.CreateConstInt(GetInt64Type(), 1), "wstr_src_size");

            var malloc = GetOrAddFunction("malloc", GetPointerType(GetInt8Type()), new[] { GetInt64Type() });
            var temp = _builder.BuildCall2(_functionTypes["malloc"], malloc, new[] { size }, "wstr_src_tmp");
            EmitNullCheckOrThrow(temp, "OutOfMemoryException: memory allocation failed");

            var memcpy = GetOrAddFunction("memcpy", GetPointerType(GetInt8Type()),
                new[] { GetPointerType(GetInt8Type()), GetPointerType(GetInt8Type()), GetInt64Type() });
            _builder.BuildCall2(_functionTypes["memcpy"], memcpy, new[] { temp, data, length }, "wstr_src_copy");

            var nulPtr = _builder.BuildGEP2(GetInt8Type(), temp, new[] { length }, "wstr_src_nul_ptr");
            _builder.BuildStore(LLVMValueRef.CreateConstInt(GetInt8Type(), 0), nulPtr);

            utf8Ptr = temp;
            freeUtf8Source = true;
        }
        else if (arg.TypeOf.Kind == LLVMTypeKind.LLVMPointerTypeKind)
        {
            // CSTRING or other i8* pointer (assumed NUL-terminated).
            utf8Ptr = arg;
        }
        else
        {
            throw new Exception("wstr() argument must be a STRING, CSTRING, or WSTRING.");
        }

        // MultiByteToWideChar(UINT32 CodePage, DWORD dwFlags, LPCCH lpMultiByteStr,
        //                     int cbMultiByte, LPWSTR lpWideCharStr, int cchWideChar)
        var multiByteToWideChar = GetOrAddFunction("MultiByteToWideChar", GetInt32Type(),
            new[] { GetInt32Type(), GetInt32Type(), GetPointerType(GetInt8Type()), GetInt32Type(), GetPointerType(GetInt16Type()), GetInt32Type() });

        // First call: get required count (NUL-terminated input).
        var cpUtf8 = LLVMValueRef.CreateConstInt(GetInt32Type(), 65001);
        var zeroFlags = LLVMValueRef.CreateConstInt(GetInt32Type(), 0);
        var minusOne = LLVMValueRef.CreateConstInt(GetInt32Type(), uint.MaxValue); // -1 as i32
        var nullWide = LLVMValueRef.CreateConstPointerNull(GetPointerType(GetInt16Type()));
        var zeroInt = LLVMValueRef.CreateConstInt(GetInt32Type(), 0);

        var requiredCount = _builder.BuildCall2(_functionTypes["MultiByteToWideChar"], multiByteToWideChar,
            new[] { cpUtf8, zeroFlags, utf8Ptr, minusOne, nullWide, zeroInt }, "wstr_count");

        // MultiByteToWideChar returns 0 on failure (e.g. invalid UTF-8).
        var countFailed = _builder.BuildICmp(LLVMIntPredicate.LLVMIntSLE, requiredCount, zeroInt, "wstr_count_fail");
        EmitCondThrow(countFailed, "OutOfMemoryException: failed to convert string to UTF-16");

        // Allocate (count * 2) bytes and convert.
        var count64 = _builder.BuildSExt(requiredCount, GetInt64Type(), "wstr_count_i64");
        var two16 = LLVMValueRef.CreateConstInt(GetInt64Type(), 2);
        var allocBytes = _builder.BuildMul(count64, two16, "wstr_alloc_bytes");
        var mallocWide = GetOrAddFunction("malloc", GetPointerType(GetInt8Type()), new[] { GetInt64Type() });
        var rawWideBuf = _builder.BuildCall2(_functionTypes["malloc"], mallocWide, new[] { allocBytes }, "wstr_raw_buf");
        EmitNullCheckOrThrow(rawWideBuf, "OutOfMemoryException: memory allocation failed");
        var wideBufTyped = _builder.BuildBitCast(rawWideBuf, GetPointerType(GetInt16Type()), "wstr_buf");
        _builder.BuildCall2(_functionTypes["MultiByteToWideChar"], multiByteToWideChar,
            new[] { cpUtf8, zeroFlags, utf8Ptr, minusOne, wideBufTyped, requiredCount }, "wstr_convert");

        if (freeUtf8Source)
        {
            var freeFunc = GetOrAddFunction("free", GetVoidType(), new[] { GetPointerType(GetInt8Type()) });
            _builder.BuildCall2(_functionTypes["free"], freeFunc, new[] { utf8Ptr });
        }

        // Track the wide buffer as a temporary (using the original i8* allocation so free() works).
        _pendingCstrTemps.Add(rawWideBuf);
        return wideBufTyped;
    }

    private LLVMValueRef GenerateFopenCall(List<Expression> arguments)
    {
        var fopen = GetOrAddFunction("fopen", GetPointerType(GetInt8Type()), new[] { GetPointerType(GetInt8Type()), GetPointerType(GetInt8Type()) });
        var args = arguments.ConvertAll(arg => ConvertToType(VisitExpression(arg), GetPointerType(GetInt8Type()))).ToArray();
        var result = _builder.BuildCall2(_functionTypes["fopen"], fopen, args, "fopentmp");

        // Throw FileOpenException if fopen returns null
        EmitNullCheckOrThrow(result, "FileOpenException: failed to open file");

        return result;
    }

    private LLVMValueRef GenerateFcloseCall(List<Expression> arguments)
    {
        var fclose = GetOrAddFunction("fclose", GetInt32Type(), new[] { GetPointerType(GetInt8Type()) });
        var args = arguments.ConvertAll(VisitExpression).ToArray();
        var result = _builder.BuildCall2(_functionTypes["fclose"], fclose, args, "fclosetmp");

        // Throw FileCloseException if fclose returns non-zero
        EmitNonZeroCheckOrThrow(result, "FileCloseException: failed to close file");

        return result;
    }

    private LLVMValueRef GenerateFreadCall(List<Expression> arguments)
    {
        var fread = GetOrAddFunction("fread", GetInt64Type(), new[] { GetPointerType(GetInt8Type()), GetInt64Type(), GetInt64Type(), GetPointerType(GetInt8Type()) });

        if (arguments.Count != 4)
            throw new Exception("fread() expects exactly 4 arguments.");

        var args = new LLVMValueRef[]
        {
            ConvertToType(VisitExpression(arguments[0]), GetPointerType(GetInt8Type())),
            ConvertToType(VisitExpression(arguments[1]), GetInt64Type()),
            ConvertToType(VisitExpression(arguments[2]), GetInt64Type()),
            ConvertToType(VisitExpression(arguments[3]), GetPointerType(GetInt8Type()))
        };

        return _builder.BuildCall2(_functionTypes["fread"], fread, args, "freadtmp");
    }

    private LLVMValueRef GenerateFwriteCall(List<Expression> arguments)
    {
        var fwrite = GetOrAddFunction("fwrite", GetInt64Type(), new[] { GetPointerType(GetInt8Type()), GetInt64Type(), GetInt64Type(), GetPointerType(GetInt8Type()) });

        if (arguments.Count != 4)
            throw new Exception("fwrite() expects exactly 4 arguments.");

        var args = new LLVMValueRef[]
        {
            ConvertToType(VisitExpression(arguments[0]), GetPointerType(GetInt8Type())),
            ConvertToType(VisitExpression(arguments[1]), GetInt64Type()),
            ConvertToType(VisitExpression(arguments[2]), GetInt64Type()),
            ConvertToType(VisitExpression(arguments[3]), GetPointerType(GetInt8Type()))
        };

        return _builder.BuildCall2(_functionTypes["fwrite"], fwrite, args, "fwritetmp");
    }

    private LLVMValueRef GenerateFseekCall(List<Expression> arguments)
    {
        var fseek = GetOrAddFunction("fseek", GetInt32Type(), new[] { GetPointerType(GetInt8Type()), GetInt64Type(), GetInt32Type() });

        if (arguments.Count != 3)
            throw new Exception("fseek() expects exactly 3 arguments.");

        var args = new LLVMValueRef[]
        {
            ConvertToType(VisitExpression(arguments[0]), GetPointerType(GetInt8Type())),
            ConvertToType(VisitExpression(arguments[1]), GetInt64Type()),
            ConvertToType(VisitExpression(arguments[2]), GetInt32Type())
        };

        var result = _builder.BuildCall2(_functionTypes["fseek"], fseek, args, "fseektmp");

        // Throw FileSeekException if fseek returns non-zero
        EmitNonZeroCheckOrThrow(result, "FileSeekException: fseek failed");

        return result;
    }

    private LLVMValueRef GenerateFtellCall(List<Expression> arguments)
    {
        var ftell = GetOrAddFunction("ftell", GetInt64Type(), new[] { GetPointerType(GetInt8Type()) });
        var args = arguments.ConvertAll(VisitExpression).ToArray();
        var result = _builder.BuildCall2(_functionTypes["ftell"], ftell, args, "ftelltmp");

        // Throw FileException if ftell returns -1
        var isError = _builder.BuildICmp(LLVMIntPredicate.LLVMIntEQ, result,
            LLVMValueRef.CreateConstInt(GetInt64Type(), unchecked((ulong)-1L)), "ftell_failed");
        EmitCondThrow(isError, "FileException: ftell failed");

        return result;
    }

    private LLVMValueRef GenerateRemoveCall(List<Expression> arguments)
    {
        var remove = GetOrAddFunction("remove", GetInt32Type(), new[] { GetPointerType(GetInt8Type()) });
        var args = arguments.ConvertAll(VisitExpression).ToArray();
        var result = _builder.BuildCall2(_functionTypes["remove"], remove, args, "removetmp");

        // Throw FileRemoveException if remove returns non-zero
        EmitNonZeroCheckOrThrow(result, "FileRemoveException: failed to remove file");

        return result;
    }

    private LLVMValueRef GenerateRenameCall(List<Expression> arguments)
    {
        var rename = GetOrAddFunction("rename", GetInt32Type(), new[] { GetPointerType(GetInt8Type()), GetPointerType(GetInt8Type()) });
        var args = arguments.ConvertAll(VisitExpression).ToArray();
        var result = _builder.BuildCall2(_functionTypes["rename"], rename, args, "renametmp");

        // Throw FileRenameException if rename returns non-zero
        EmitNonZeroCheckOrThrow(result, "FileRenameException: failed to rename file");

        return result;
    }

    private LLVMValueRef GenerateAllocCall(List<Expression> arguments)
    {
        if (arguments.Count != 1)
            throw new Exception("alloc() expects exactly 1 argument.");

        var malloc = GetOrAddFunction("malloc", GetPointerType(GetInt8Type()), new[] { GetInt64Type() });
        var args = arguments.ConvertAll(VisitExpression).ToArray();
        var result = _builder.BuildCall2(_functionTypes["malloc"], malloc, args, "alloctmp");

        // Throw OutOfMemoryException if malloc returns null
        EmitNullCheckOrThrow(result, "OutOfMemoryException: memory allocation failed");

        return result;
    }

    private LLVMValueRef GenerateReallocCall(List<Expression> arguments)
    {
        if (arguments.Count != 2)
            throw new Exception("realloc() expects exactly 2 arguments (pointer, new_size).");

        var realloc = GetOrAddFunction("realloc", GetPointerType(GetInt8Type()),
            new[] { GetPointerType(GetInt8Type()), GetInt64Type() });
        var args = arguments.ConvertAll(VisitExpression).ToArray();
        var result = _builder.BuildCall2(_functionTypes["realloc"], realloc, args, "realloctmp");

        // Throw OutOfMemoryException if realloc returns null
        EmitNullCheckOrThrow(result, "OutOfMemoryException: memory reallocation failed");

        return result;
    }

    private LLVMValueRef GenerateMkdirCall(List<Expression> arguments)
    {
        var mkdir = GetOrAddFunction("mkdir", GetInt32Type(), new[] { GetPointerType(GetInt8Type()), GetInt32Type() });
        var args = arguments.ConvertAll(VisitExpression).ToArray();
        var result = _builder.BuildCall2(_functionTypes["mkdir"], mkdir, args, "mkdirtmp");

        // Throw DirectoryException if mkdir returns non-zero
        EmitNonZeroCheckOrThrow(result, "DirectoryException: failed to create directory");

        return result;
    }

    private LLVMValueRef GenerateRmdirCall(List<Expression> arguments)
    {
        var rmdir = GetOrAddFunction("rmdir", GetInt32Type(), new[] { GetPointerType(GetInt8Type()) });
        var args = arguments.ConvertAll(VisitExpression).ToArray();
        var result = _builder.BuildCall2(_functionTypes["rmdir"], rmdir, args, "rmdirtmp");

        // Throw DirectoryException if rmdir returns non-zero
        EmitNonZeroCheckOrThrow(result, "DirectoryException: failed to remove directory");

        return result;
    }

    /// <summary>
    /// Emits a runtime check: if the pointer value is null, throw a runtime exception.
    /// </summary>
    private void EmitNullCheckOrThrow(LLVMValueRef ptrValue, string message)
    {
        var isNull = _builder.BuildICmp(LLVMIntPredicate.LLVMIntEQ, ptrValue,
            LLVMValueRef.CreateConstNull(ptrValue.TypeOf), "is_null");
        EmitCondThrow(isNull, message);
    }

    /// <summary>
    /// Emits a runtime check: if the i32 value is non-zero, throw a runtime exception.
    /// </summary>
    private void EmitNonZeroCheckOrThrow(LLVMValueRef intValue, string message)
    {
        var isFailed = _builder.BuildICmp(LLVMIntPredicate.LLVMIntNE, intValue,
            LLVMValueRef.CreateConstInt(intValue.TypeOf, 0), "is_failed");
        EmitCondThrow(isFailed, message);
    }

    /// <summary>
    /// Emits a conditional throw: if condition is true, throw a runtime exception with the given message.
    /// If no try/catch is active, prints and exits. Otherwise longjmps to the handler.
    /// Delegates to the shared __zv_throw_cond runtime function (see
    /// GetOrCreateZvThrowCondFunction) instead of inlining the full dispatch control flow
    /// (branch + cleanup + longjmp/abort, several basic blocks) at every check site.
    /// `message` is always a compile-time literal following the "TypeName: description"
    /// convention (every call site in this compiler follows it), so the exception's type id
    /// for catch dispatch (see GetExceptionTypeId) can be resolved at compile time too.
    /// </summary>
    private void EmitCondThrow(LLVMValueRef condition, string message)
    {
        EnsureExceptionGlobals();

        var throwCond = GetOrCreateZvThrowCondFunction();
        var msgPtr = GetOrCreateGlobalStringPtr(message, "exc_msg");
        int colonIndex = message.IndexOf(": ", StringComparison.Ordinal);
        string? typeName = colonIndex >= 0 ? message[..colonIndex] : null;
        var typeIdVal = LLVMValueRef.CreateConstInt(GetInt32Type(), (ulong)GetExceptionTypeId(typeName));
        _builder.BuildCall2(_functionTypes["__zv_throw_cond"], throwCond, new[] { condition, msgPtr, typeIdVal }, "");
    }

    private LLVMValueRef GenerateGetTimestampCall(List<Expression> arguments)
    {
        if (arguments.Count != 0)
            throw new Exception("get_timestamp() takes no arguments.");

        var timeFunc = GetOrAddFunction("time", GetInt64Type(), new[] { GetPointerType(GetInt64Type()) });
        var nullArg = LLVMValueRef.CreateConstNull(GetPointerType(GetInt64Type()));
        return _builder.BuildCall2(_functionTypes["time"], timeFunc, new[] { nullArg }, "timetmp");
    }

    private LLVMValueRef GenerateGetTimestampMsCall(List<Expression> arguments)
    {
        if (arguments.Count != 0)
            throw new Exception("get_timestamp_ms() takes no arguments.");

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            // Windows FILETIME is a 64-bit count of 100-nanosecond intervals since 1601-01-01 UTC.
            // Convert it to Unix epoch milliseconds.
            var fileTimeType = GetInt64Type();
            var fileTimePtr = BuildEntryAlloca(fileTimeType, "filetime");
            var getSystemTimeFunc = GetOrAddFunction("GetSystemTimeAsFileTime", GetVoidType(), new[] { GetPointerType(fileTimeType) });
            _builder.BuildCall2(_functionTypes["GetSystemTimeAsFileTime"], getSystemTimeFunc, new[] { fileTimePtr }, "");

            var fileTime = _builder.BuildLoad2(fileTimeType, fileTimePtr, "filetime_val");
            var epochOffset = LLVMValueRef.CreateConstInt(fileTimeType, 116444736000000000, false);
            var hundredNsSinceEpoch = _builder.BuildSub(fileTime, epochOffset, "hundred_ns_since_epoch");
            var msSinceEpoch = _builder.BuildUDiv(hundredNsSinceEpoch, LLVMValueRef.CreateConstInt(fileTimeType, 10000, false), "ms_since_epoch");
            return msSinceEpoch;
        }
        else
        {
            // POSIX: use clock_gettime(CLOCK_REALTIME, &ts) where timespec is { i64 tv_sec, i64 tv_nsec }.
            var timespecType = LLVMTypeRef.CreateStruct(new[] { GetInt64Type(), GetInt64Type() }, false);
            var tsPtr = BuildEntryAlloca(timespecType, "ts");
            var clockGettimeFunc = GetOrAddFunction("clock_gettime", GetInt32Type(), new[] { GetInt32Type(), GetPointerType(timespecType) });
            var realtime = LLVMValueRef.CreateConstInt(GetInt32Type(), 0);
            _builder.BuildCall2(_functionTypes["clock_gettime"], clockGettimeFunc, new[] { realtime, tsPtr }, "clocktmp");

            var secPtr = _builder.BuildStructGEP2(timespecType, tsPtr, 0, "sec_ptr");
            var nsecPtr = _builder.BuildStructGEP2(timespecType, tsPtr, 1, "nsec_ptr");
            var sec = _builder.BuildLoad2(GetInt64Type(), secPtr, "sec");
            var nsec = _builder.BuildLoad2(GetInt64Type(), nsecPtr, "nsec");
            var secToMs = _builder.BuildMul(sec, LLVMValueRef.CreateConstInt(GetInt64Type(), 1000, false), "sec_to_ms");
            var nsecToMs = _builder.BuildUDiv(nsec, LLVMValueRef.CreateConstInt(GetInt64Type(), 1000000, false), "nsec_to_ms");
            return _builder.BuildAdd(secToMs, nsecToMs, "ms_since_epoch");
        }
    }

    // Constructs an Exception value from a message expression. When `typeName` is given
    // (i.e. this is a call to a declared exception type like `MyError("...")`, as opposed
    // to the generic `Exception("...")`), the stored message is prefixed with
    // "typeName: " so `catch (MyError e)` can filter for it - see EmitExceptionTypeCheck.
    //
    // Called with zero arguments (`MyError()` or a bare `throw MyError;`), the type's
    // default message - registered via `exception MyError = <expr>;`, see
    // _exceptionDefaultMessages - is re-evaluated and used instead.
    private LLVMValueRef GenerateExceptionConstructor(List<Expression> arguments, string? typeName)
    {
        string ctorName = typeName ?? "Exception";

        Expression messageExpr;
        if (arguments.Count == 1)
        {
            messageExpr = arguments[0];
        }
        else if (arguments.Count == 0 && typeName != null && _exceptionDefaultMessages.TryGetValue(typeName, out var defaultExpr))
        {
            messageExpr = defaultExpr;
        }
        else
        {
            string hint = typeName != null && !_exceptionDefaultMessages.ContainsKey(typeName)
                ? $" (or declare a default with 'exception {typeName} = <message>;' to call {ctorName}() with no arguments)"
                : "";
            throw new Exception($"{ctorName}() expects exactly 1 argument (message string){hint}.");
        }

        var msg = VisitExpression(messageExpr);

        // If the message expression is itself an Exception value (e.g. the RHS of
        // `exception MyError = Exception("...")`), its message field is already a
        // NUL-terminated, owned buffer - reuse it directly instead of re-coercing.
        LLVMValueRef msgPtr = IsExceptionStructType(msg.TypeOf)
            ? _builder.BuildExtractValue(msg, 0, "exc_default_msg")
            : CoerceExceptionMessageToCString(msg, ctorName);

        if (typeName != null)
        {
            msgPtr = BuildExceptionPrefixedMessage(typeName, msgPtr);
        }

        // Build an Exception struct { i8* message, i32 type_id }
        var excType = GetExceptionType();
        var alloca = BuildEntryAlloca(excType, "exc_init");
        var msgFieldPtr = _builder.BuildStructGEP2(excType, alloca, 0, "exc_msg");
        _builder.BuildStore(msgPtr, msgFieldPtr);
        var typeIdFieldPtr = _builder.BuildStructGEP2(excType, alloca, 1, "exc_type_id");
        _builder.BuildStore(LLVMValueRef.CreateConstInt(GetInt32Type(), (ulong)GetExceptionTypeId(typeName)), typeIdFieldPtr);

        return _builder.BuildLoad2(excType, alloca, "exc_val");
    }

    private bool IsExceptionStructType(LLVMTypeRef type)
    {
        return _exceptionType.HasValue && type.Handle == _exceptionType.Value.Handle;
    }

    // Coerces an exception constructor's message argument to a NUL-terminated i8*. A
    // STRING is copied into a freshly malloc'd NUL-terminated buffer (STRING isn't
    // guaranteed to be NUL-terminated); a CSTRING/pointer is used as-is. The result is
    // intentionally not tracked as a temporary (unlike CoerceToCString) since it must
    // outlive the throwing statement - it becomes the exception's message.
    private LLVMValueRef CoerceExceptionMessageToCString(LLVMValueRef arg, string callerName)
    {
        if (IsStringStructType(arg.TypeOf))
        {
            var data = _builder.BuildExtractValue(arg, 0, "exc_msg_data");
            var length = _builder.BuildExtractValue(arg, 1, "exc_msg_len");
            var size = _builder.BuildAdd(length, LLVMValueRef.CreateConstInt(GetInt64Type(), 1), "exc_msg_size");

            var malloc = GetOrAddFunction("malloc", GetPointerType(GetInt8Type()), new[] { GetInt64Type() });
            var buffer = _builder.BuildCall2(_functionTypes["malloc"], malloc, new[] { size }, "exc_msg_buf");
            EmitNullCheckOrThrow(buffer, "OutOfMemoryException: memory allocation failed");

            var memcpy = GetOrAddFunction("memcpy", GetPointerType(GetInt8Type()),
                new[] { GetPointerType(GetInt8Type()), GetPointerType(GetInt8Type()), GetInt64Type() });
            _builder.BuildCall2(_functionTypes["memcpy"], memcpy, new[] { buffer, data, length }, "exc_msg_cpy");

            var nulPtr = _builder.BuildGEP2(GetInt8Type(), buffer, new[] { length }, "exc_msg_nul");
            _builder.BuildStore(LLVMValueRef.CreateConstInt(GetInt8Type(), 0), nulPtr);
            return buffer;
        }

        if (arg.TypeOf.Kind == LLVMTypeKind.LLVMPointerTypeKind)
            return arg;

        throw new Exception($"{callerName}() argument must be a string.");
    }

    // Builds a freshly-allocated "typeName: message" string at runtime.
    private LLVMValueRef BuildExceptionPrefixedMessage(string typeName, LLVMValueRef msgPtr)
    {
        string prefix = typeName + ": ";
        var prefixPtr = GetOrCreateGlobalStringPtr(prefix, "exc_type_prefix");

        var strlen = GetOrAddFunction("strlen", GetInt64Type(), new[] { GetPointerType(GetInt8Type()) });
        var msgLen = _builder.BuildCall2(_functionTypes["strlen"], strlen, new[] { msgPtr }, "exc_msg_len");
        var totalLen = _builder.BuildAdd(msgLen, LLVMValueRef.CreateConstInt(GetInt64Type(), (ulong)prefix.Length, false), "exc_total_len");
        var size = _builder.BuildAdd(totalLen, LLVMValueRef.CreateConstInt(GetInt64Type(), 1), "exc_prefixed_size");

        var malloc = GetOrAddFunction("malloc", GetPointerType(GetInt8Type()), new[] { GetInt64Type() });
        var buffer = _builder.BuildCall2(_functionTypes["malloc"], malloc, new[] { size }, "exc_prefixed_buf");
        EmitNullCheckOrThrow(buffer, "OutOfMemoryException: memory allocation failed");

        var strcpy = GetOrAddFunction("strcpy", GetPointerType(GetInt8Type()), new[] { GetPointerType(GetInt8Type()), GetPointerType(GetInt8Type()) });
        _builder.BuildCall2(_functionTypes["strcpy"], strcpy, new[] { buffer, prefixPtr }, "exc_prefixed_copy");

        var strcat = GetOrAddFunction("strcat", GetPointerType(GetInt8Type()), new[] { GetPointerType(GetInt8Type()), GetPointerType(GetInt8Type()) });
        _builder.BuildCall2(_functionTypes["strcat"], strcat, new[] { buffer, msgPtr }, "exc_prefixed_cat");

        return buffer;
    }

    // -----------------------------------------------------------------------
    // parse_* builtins — string-to-numeric conversion
    // -----------------------------------------------------------------------
    // All parse_* functions accept a single STRING or CSTRING argument and
    // return the corresponding numeric primitive. Internally they call C's
    // strtol / strtoll / strtoul / strtoull / strtod / strtof.

    /// <summary>
    /// Coerces a STRING or CSTRING value to a raw i8* (CSTRING). If the input
    /// is a STRING struct, allocates a NUL-terminated copy (added to
    /// _pendingCstrTemps so it is freed after the statement). If already a
    /// pointer (CSTRING), returns it directly.
    /// </summary>
    private LLVMValueRef CoerceToCString(LLVMValueRef arg, string callerName)
    {
        if (IsStringStructType(arg.TypeOf))
        {
            var data = _builder.BuildExtractValue(arg, 0, "parse_data");
            var length = _builder.BuildExtractValue(arg, 1, "parse_len");
            var size = _builder.BuildAdd(length, LLVMValueRef.CreateConstInt(GetInt64Type(), 1), "parse_size");

            var malloc = GetOrAddFunction("malloc", GetPointerType(GetInt8Type()), new[] { GetInt64Type() });
            var buffer = _builder.BuildCall2(_functionTypes["malloc"], malloc, new[] { size }, "parse_buf");
            EmitNullCheckOrThrow(buffer, "OutOfMemoryException: memory allocation failed");

            var memcpy = GetOrAddFunction("memcpy", GetPointerType(GetInt8Type()),
                new[] { GetPointerType(GetInt8Type()), GetPointerType(GetInt8Type()), GetInt64Type() });
            _builder.BuildCall2(_functionTypes["memcpy"], memcpy, new[] { buffer, data, length }, "parse_cpy");

            var nulPtr = _builder.BuildGEP2(GetInt8Type(), buffer, new[] { length }, "parse_nul");
            _builder.BuildStore(LLVMValueRef.CreateConstInt(GetInt8Type(), 0), nulPtr);

            _pendingCstrTemps.Add(buffer);
            return buffer;
        }

        if (arg.TypeOf.Kind == LLVMTypeKind.LLVMPointerTypeKind)
            return arg;

        throw new Exception($"{callerName}() argument must be a STRING or CSTRING.");
    }

    private static readonly HashSet<string> ParseBuiltinNames = new()
    {
        "parse_int8", "parse_uint8",
        "parse_int16", "parse_uint16",
        "parse_int32", "parse_uint32",
        "parse_int64", "parse_uint64",
        "parse_int128", "parse_uint128",
        "parse_float32", "parse_float64",
        "parse_bool",
    };

    private LLVMValueRef GenerateParseCall(string name, List<Expression> arguments)
    {
        if (arguments.Count != 1)
            throw new Exception($"{name}() expects exactly 1 argument.");

        var arg = VisitExpression(arguments[0]);
        var cstr = CoerceToCString(arg, name);

        return name switch
        {
            "parse_int8"    => TruncOrExtParsedInt(CallStrtol(cstr), GetInt8Type(), signed: true),
            "parse_uint8"   => TruncOrExtParsedInt(CallStrtoul(cstr), GetInt8Type(), signed: false),
            "parse_int16"   => TruncOrExtParsedInt(CallStrtol(cstr), GetInt16Type(), signed: true),
            "parse_uint16"  => TruncOrExtParsedInt(CallStrtoul(cstr), GetInt16Type(), signed: false),
            "parse_int32"   => TruncOrExtParsedInt(CallStrtol(cstr), GetInt32Type(), signed: true),
            "parse_uint32"  => TruncOrExtParsedInt(CallStrtoul(cstr), GetInt32Type(), signed: false),
            "parse_int64"   => CallStrtoll(cstr),
            "parse_uint64"  => CallStrtoull(cstr),
            "parse_int128"  => _builder.BuildSExt(CallStrtoll(cstr), GetInt128Type(), "sext128"),
            "parse_uint128" => _builder.BuildZExt(CallStrtoull(cstr), GetInt128Type(), "zext128"),
            "parse_float32" => CallStrtof(cstr),
            "parse_float64" => CallStrtod(cstr),
            "parse_bool"    => EmitParseBool(cstr),
            _ => throw new Exception($"Unknown parse builtin: {name}")
        };
    }

    private LLVMValueRef TruncOrExtParsedInt(LLVMValueRef value, LLVMTypeRef target, bool signed)
    {
        if (value.TypeOf.IntWidth == target.IntWidth) return value;
        if (value.TypeOf.IntWidth > target.IntWidth)
            return _builder.BuildTrunc(value, target, "parse_trunc");
        return signed
            ? _builder.BuildSExt(value, target, "parse_sext")
            : _builder.BuildZExt(value, target, "parse_zext");
    }

    // strtol(str, NULL, 10) -> long (i64 on 64-bit Windows/Linux)
    private LLVMValueRef CallStrtol(LLVMValueRef cstr)
    {
        var ptrType = GetPointerType(GetInt8Type());
        var func = GetOrAddFunction("strtol", GetInt64Type(), new[] { ptrType, ptrType, GetInt32Type() });
        var nullPtr = LLVMValueRef.CreateConstPointerNull(ptrType);
        var ten = LLVMValueRef.CreateConstInt(GetInt32Type(), 10);
        return _builder.BuildCall2(_functionTypes["strtol"], func, new[] { cstr, nullPtr, ten }, "strtol_res");
    }

    // strtoul(str, NULL, 10) -> unsigned long (i64 on 64-bit)
    private LLVMValueRef CallStrtoul(LLVMValueRef cstr)
    {
        var ptrType = GetPointerType(GetInt8Type());
        var func = GetOrAddFunction("strtoul", GetInt64Type(), new[] { ptrType, ptrType, GetInt32Type() });
        var nullPtr = LLVMValueRef.CreateConstPointerNull(ptrType);
        var ten = LLVMValueRef.CreateConstInt(GetInt32Type(), 10);
        return _builder.BuildCall2(_functionTypes["strtoul"], func, new[] { cstr, nullPtr, ten }, "strtoul_res");
    }

    // strtoll(str, NULL, 10) -> long long (i64)
    private LLVMValueRef CallStrtoll(LLVMValueRef cstr)
    {
        var ptrType = GetPointerType(GetInt8Type());
        var func = GetOrAddFunction("strtoll", GetInt64Type(), new[] { ptrType, ptrType, GetInt32Type() });
        var nullPtr = LLVMValueRef.CreateConstPointerNull(ptrType);
        var ten = LLVMValueRef.CreateConstInt(GetInt32Type(), 10);
        return _builder.BuildCall2(_functionTypes["strtoll"], func, new[] { cstr, nullPtr, ten }, "strtoll_res");
    }

    // strtoull(str, NULL, 10) -> unsigned long long (i64)
    private LLVMValueRef CallStrtoull(LLVMValueRef cstr)
    {
        var ptrType = GetPointerType(GetInt8Type());
        var func = GetOrAddFunction("strtoull", GetInt64Type(), new[] { ptrType, ptrType, GetInt32Type() });
        var nullPtr = LLVMValueRef.CreateConstPointerNull(ptrType);
        var ten = LLVMValueRef.CreateConstInt(GetInt32Type(), 10);
        return _builder.BuildCall2(_functionTypes["strtoull"], func, new[] { cstr, nullPtr, ten }, "strtoull_res");
    }

    // strtod(str, NULL) -> double (f64)
    private LLVMValueRef CallStrtod(LLVMValueRef cstr)
    {
        var ptrType = GetPointerType(GetInt8Type());
        var func = GetOrAddFunction("strtod", GetDoubleType(), new[] { ptrType, ptrType });
        var nullPtr = LLVMValueRef.CreateConstPointerNull(ptrType);
        return _builder.BuildCall2(_functionTypes["strtod"], func, new[] { cstr, nullPtr }, "strtod_res");
    }

    // strtof(str, NULL) -> float (f32)
    private LLVMValueRef CallStrtof(LLVMValueRef cstr)
    {
        var ptrType = GetPointerType(GetInt8Type());
        var func = GetOrAddFunction("strtof", GetFloatType(), new[] { ptrType, ptrType });
        var nullPtr = LLVMValueRef.CreateConstPointerNull(ptrType);
        return _builder.BuildCall2(_functionTypes["strtof"], func, new[] { cstr, nullPtr }, "strtof_res");
    }

    // parse_bool: "true" => i1 1, "false" => i1 0, anything else => runtime error
    private LLVMValueRef EmitParseBool(LLVMValueRef cstr)
    {
        var ptrType = GetPointerType(GetInt8Type());
        var strcmpFunc = GetOrAddFunction("strcmp", GetInt32Type(), new[] { ptrType, ptrType });
        var zero = LLVMValueRef.CreateConstInt(GetInt32Type(), 0);

        var function = _builder.InsertBlock.Parent;
        var isTrueBB     = _context.AppendBasicBlock(function, "parse_bool_is_true");
        var checkFalseBB = _context.AppendBasicBlock(function, "parse_bool_check_false");
        var isFalseBB    = _context.AppendBasicBlock(function, "parse_bool_is_false");
        var mergeBB      = _context.AppendBasicBlock(function, "parse_bool_merge");

        // Compare with "true"
        var trueStr = GetOrCreateGlobalStringPtr("true", "parse_bool_true");
        var cmpTrue = _builder.BuildCall2(_functionTypes["strcmp"], strcmpFunc, new[] { cstr, trueStr }, "cmp_true");
        var isTrue = _builder.BuildICmp(LLVMIntPredicate.LLVMIntEQ, cmpTrue, zero, "is_true");
        _builder.BuildCondBr(isTrue, isTrueBB, checkFalseBB);

        // "true" path
        _builder.PositionAtEnd(isTrueBB);
        _builder.BuildBr(mergeBB);

        // Compare with "false"
        _builder.PositionAtEnd(checkFalseBB);
        var falseStr = GetOrCreateGlobalStringPtr("false", "parse_bool_false");
        var cmpFalse = _builder.BuildCall2(_functionTypes["strcmp"], strcmpFunc, new[] { cstr, falseStr }, "cmp_false");
        var isNotFalse = _builder.BuildICmp(LLVMIntPredicate.LLVMIntNE, cmpFalse, zero, "is_not_false");

        // Throw if neither "true" nor "false" — EmitCondThrow creates its own
        // throw/continue blocks and leaves the builder positioned at the continue block.
        EmitCondThrow(isNotFalse, "ParseException: parse_bool expected \"true\" or \"false\"");
        // If we get here, it was "false" — branch to merge
        _builder.BuildBr(isFalseBB);

        // "false" path
        _builder.PositionAtEnd(isFalseBB);
        _builder.BuildBr(mergeBB);

        // Merge — phi node: true from isTrueBB, false from isFalseBB
        _builder.PositionAtEnd(mergeBB);
        var phi = _builder.BuildPhi(GetInt1Type(), "parse_bool_res");
        phi.AddIncoming(
            new[] { LLVMValueRef.CreateConstInt(GetInt1Type(), 1), LLVMValueRef.CreateConstInt(GetInt1Type(), 0) },
            new[] { isTrueBB, isFalseBB },
            2);
        return phi;
    }

    private LLVMValueRef GetOrAddFunction(string name, LLVMTypeRef returnType, LLVMTypeRef[] paramTypes, bool isVarArg = false)
    {
        var func = _module.GetNamedFunction(name);
        if (func.Handle == IntPtr.Zero)
        {
            var funcType = LLVMTypeRef.CreateFunction(returnType, paramTypes, isVarArg);
            func = _module.AddFunction(name, funcType);
            _functionTypes[name] = funcType;
        }
        return func;
    }
}
