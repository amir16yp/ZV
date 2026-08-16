using System;
using System.Collections.Generic;
using ZV.Compiler.Lexer;

namespace ZV.Compiler.Target;

/// <summary>
/// Describes the size and alignment of primitive types for a specific target.
/// Fixed-width integer types have the same bit width on every target; only
/// their alignment, pointer widths and ISIZE/USIZE change.
/// </summary>
public sealed class DataLayout
{
    private readonly Dictionary<TokenType, (int SizeBits, int AlignmentBits)> _primitiveLayout;

    public int PointerSizeBits { get; }
    public int PointerAlignmentBits { get; }
    public int FunctionPointerSizeBits { get; }
    public int SizeTypeBits { get; }

    public static DataLayout ForTarget(TargetInfo target) => target.Architecture switch
    {
        TargetArchitecture.X86_16 => X86_16,
        TargetArchitecture.X86_32 => X86_32,
        TargetArchitecture.Amd64 => Amd64,
        _ => throw new NotSupportedException($"No data layout for architecture '{target.Architecture}'.")
    };

    public static DataLayout X86_16 { get; } = new DataLayout(
        pointerSizeBits: 16,
        pointerAlignmentBits: 16,
        functionPointerSizeBits: 16,
        sizeTypeBits: 16,
        new Dictionary<TokenType, (int, int)>
        {
            { TokenType.BOOL, (8, 8) },
            { TokenType.CHAR, (8, 8) },
            { TokenType.INT8, (8, 8) },
            { TokenType.UINT8, (8, 8) },
            { TokenType.INT16, (16, 16) },
            { TokenType.UINT16, (16, 16) },
            { TokenType.INT32, (32, 16) },
            { TokenType.UINT32, (32, 16) },
            { TokenType.INT64, (64, 16) },
            { TokenType.UINT64, (64, 16) },
            { TokenType.INT128, (128, 16) },
            { TokenType.UINT128, (128, 16) },
            { TokenType.FLOAT32, (32, 16) },
            { TokenType.FLOAT64, (64, 16) },
            { TokenType.VOID, (0, 8) }
        });

    public static DataLayout X86_32 { get; } = new DataLayout(
        pointerSizeBits: 32,
        pointerAlignmentBits: 32,
        functionPointerSizeBits: 32,
        sizeTypeBits: 32,
        new Dictionary<TokenType, (int, int)>
        {
            { TokenType.BOOL, (8, 8) },
            { TokenType.CHAR, (8, 8) },
            { TokenType.INT8, (8, 8) },
            { TokenType.UINT8, (8, 8) },
            { TokenType.INT16, (16, 16) },
            { TokenType.UINT16, (16, 16) },
            { TokenType.INT32, (32, 32) },
            { TokenType.UINT32, (32, 32) },
            { TokenType.INT64, (64, 32) },
            { TokenType.UINT64, (64, 32) },
            { TokenType.INT128, (128, 32) },
            { TokenType.UINT128, (128, 32) },
            { TokenType.FLOAT32, (32, 32) },
            { TokenType.FLOAT64, (64, 32) },
            { TokenType.VOID, (0, 8) }
        });

    public static DataLayout Amd64 { get; } = new DataLayout(
        pointerSizeBits: 64,
        pointerAlignmentBits: 64,
        functionPointerSizeBits: 64,
        sizeTypeBits: 64,
        new Dictionary<TokenType, (int, int)>
        {
            { TokenType.BOOL, (8, 8) },
            { TokenType.CHAR, (8, 8) },
            { TokenType.INT8, (8, 8) },
            { TokenType.UINT8, (8, 8) },
            { TokenType.INT16, (16, 16) },
            { TokenType.UINT16, (16, 16) },
            { TokenType.INT32, (32, 32) },
            { TokenType.UINT32, (32, 32) },
            { TokenType.INT64, (64, 64) },
            { TokenType.UINT64, (64, 64) },
            { TokenType.INT128, (128, 64) },
            { TokenType.UINT128, (128, 64) },
            { TokenType.FLOAT32, (32, 32) },
            { TokenType.FLOAT64, (64, 64) },
            { TokenType.VOID, (0, 8) }
        });

    private DataLayout(
        int pointerSizeBits,
        int pointerAlignmentBits,
        int functionPointerSizeBits,
        int sizeTypeBits,
        Dictionary<TokenType, (int, int)> primitiveLayout)
    {
        PointerSizeBits = pointerSizeBits;
        PointerAlignmentBits = pointerAlignmentBits;
        FunctionPointerSizeBits = functionPointerSizeBits;
        SizeTypeBits = sizeTypeBits;
        _primitiveLayout = primitiveLayout;
    }

    public bool IsPrimitiveSupported(TokenType primitiveType)
    {
        return primitiveType is TokenType.BOOL or TokenType.CHAR or TokenType.VOID
            || (primitiveType >= TokenType.INT8 && primitiveType <= TokenType.UINT128)
            || primitiveType == TokenType.FLOAT32
            || primitiveType == TokenType.FLOAT64;
    }

    public int GetSizeBits(TokenType primitiveType)
    {
        if (_primitiveLayout.TryGetValue(primitiveType, out var info))
            return info.SizeBits;
        throw new NotSupportedException($"No size for primitive type '{primitiveType}' in this data layout.");
    }

    public int GetAlignmentBits(TokenType primitiveType)
    {
        if (_primitiveLayout.TryGetValue(primitiveType, out var info))
            return info.AlignmentBits;
        throw new NotSupportedException($"No alignment for primitive type '{primitiveType}' in this data layout.");
    }

    public int GetSizeBytes(TokenType primitiveType) => GetSizeBits(primitiveType) / 8;

    public int GetAlignmentBytes(TokenType primitiveType) => GetAlignmentBits(primitiveType) / 8;

    /// <summary>
    /// Rounds up <paramref name="offsetBits"/> to the next multiple of <paramref name="alignmentBits"/>.
    /// </summary>
    public static int AlignBits(int offsetBits, int alignmentBits)
    {
        if (alignmentBits <= 0) return offsetBits;
        int mod = offsetBits % alignmentBits;
        return mod == 0 ? offsetBits : offsetBits + (alignmentBits - mod);
    }

    public static int AlignBytes(int offsetBytes, int alignmentBytes)
    {
        if (alignmentBytes <= 0) return offsetBytes;
        int mod = offsetBytes % alignmentBytes;
        return mod == 0 ? offsetBytes : offsetBytes + (alignmentBytes - mod);
    }
}
