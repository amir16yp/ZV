using System;

namespace ZV.Compiler.Target.X86_16;

/// <summary>
/// Describes a 16-bit real-mode addressing mode used by ModR/M.
/// </summary>
internal enum X86_16AddressMode : byte
{
    BxSi = 0,
    BxDi = 1,
    BpSi = 2,
    BpDi = 3,
    Si = 4,
    Di = 5,
    Disp16 = 6,
    Bx = 7
}

/// <summary>
/// An operand for a 16-bit x86 instruction: either an 8-bit/16-bit register or a
/// memory effective address.
/// </summary>
public readonly record struct Operand
{
    private readonly bool _isRegister;
    private readonly bool _is8Bit;
    private readonly int _register;

    private readonly bool _isDirectMemory;
    private readonly ushort _directAddress;

    private readonly X86_16AddressMode _addressMode;
    private readonly short _displacement;
    private readonly bool _isBpBase;

    private Operand(int register, bool is8Bit)
    {
        _isRegister = true;
        _is8Bit = is8Bit;
        _register = register;
        _isDirectMemory = false;
        _directAddress = 0;
        _addressMode = 0;
        _displacement = 0;
        _isBpBase = false;
    }

    private Operand(ushort directAddress)
    {
        _isRegister = false;
        _is8Bit = false;
        _register = -1;
        _isDirectMemory = true;
        _directAddress = directAddress;
        _addressMode = X86_16AddressMode.Disp16;
        _displacement = 0;
        _isBpBase = false;
    }

    private Operand(X86_16AddressMode mode, short displacement, bool isBpBase = false)
    {
        _isRegister = false;
        _is8Bit = false;
        _register = -1;
        _isDirectMemory = false;
        _directAddress = 0;
        _addressMode = mode;
        _displacement = displacement;
        _isBpBase = isBpBase;
    }

    public bool IsRegister => _isRegister;
    public bool IsMemory => !_isRegister;
    public bool Is8Bit => _is8Bit;
    public bool Is16Bit => _isRegister && !_is8Bit;
    internal bool IsDirectMemory => _isDirectMemory;
    internal bool IsBpBase => _isBpBase;
    internal int RegisterNumber => _register;
    internal ushort DirectAddress => _directAddress;
    internal X86_16AddressMode AddressMode => _addressMode;
    internal short Displacement => _displacement;

    public static Operand Reg(X86_16Register register) => new((int)register, false);
    public static Operand Reg(X86_16Register8 register) => new((int)register, true);

    /// <summary>Absolute memory operand [address].</summary>
    public static Operand Memory(ushort address) => new(address);

    /// <summary>Register indirect memory operand [reg] or [reg+disp].</summary>
    public static Operand Memory(X86_16Register baseRegister, short displacement = 0)
    {
        X86_16AddressMode mode = baseRegister switch
        {
            X86_16Register.BX => X86_16AddressMode.Bx,
            X86_16Register.BP => X86_16AddressMode.Disp16,
            X86_16Register.SI => X86_16AddressMode.Si,
            X86_16Register.DI => X86_16AddressMode.Di,
            _ => throw new ArgumentException($"Unsupported base register '{baseRegister}'.", nameof(baseRegister))
        };
        return new Operand(mode, displacement, isBpBase: baseRegister == X86_16Register.BP);
    }

    /// <summary>Based-indexed memory operand [base+index+disp].</summary>
    public static Operand Memory(X86_16Register baseRegister, X86_16Register indexRegister, short displacement = 0)
    {
        X86_16AddressMode mode = (baseRegister, indexRegister) switch
        {
            (X86_16Register.BX, X86_16Register.SI) => X86_16AddressMode.BxSi,
            (X86_16Register.BX, X86_16Register.DI) => X86_16AddressMode.BxDi,
            (X86_16Register.BP, X86_16Register.SI) => X86_16AddressMode.BpSi,
            (X86_16Register.BP, X86_16Register.DI) => X86_16AddressMode.BpDi,
            _ => throw new ArgumentException($"Unsupported base/index pair '{baseRegister}/{indexRegister}'.", nameof(indexRegister))
        };
        return new Operand(mode, displacement, isBpBase: baseRegister == X86_16Register.BP);
    }
}
