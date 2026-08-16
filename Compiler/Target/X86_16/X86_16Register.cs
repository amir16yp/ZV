namespace ZV.Compiler.Target.X86_16;

/// <summary>
/// General-purpose 16-bit x86 registers (AX..DI). The numeric values match the x86
/// register field encoding used in ModR/M and one-byte push/pop/inc/dec opcodes.
/// </summary>
public enum X86_16Register
{
    AX = 0, CX = 1, DX = 2, BX = 3, SP = 4, BP = 5, SI = 6, DI = 7
}

/// <summary>
/// 8-bit x86 registers. The numeric values match the x86 register field encoding.
/// </summary>
public enum X86_16Register8
{
    AL = 0, CL = 1, DL = 2, BL = 3,
    AH = 4, CH = 5, DH = 6, BH = 7
}

/// <summary>
/// x86 segment registers. The numeric values match the x86 segment register field.
/// </summary>
public enum X86_16SegmentRegister
{
    ES = 0, CS = 1, SS = 2, DS = 3
}

/// <summary>
/// x86 condition codes used by conditional jumps and (on 386+) SETcc/CMOVcc.
/// </summary>
public enum X86_16ConditionCode
{
    Overflow = 0,
    NotOverflow = 1,
    Below = 2, Carry = 2,
    NotBelow = 3, NotCarry = 3,
    Equal = 4, Zero = 4,
    NotEqual = 5, NotZero = 5,
    NotAbove = 6,
    Above = 7,
    Sign = 8,
    NotSign = 9,
    Parity = 10,
    NotParity = 11,
    Less = 12,
    GreaterOrEqual = 13,
    LessOrEqual = 14,
    Greater = 15
}
