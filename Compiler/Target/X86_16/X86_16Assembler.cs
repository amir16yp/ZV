using System;
using System.Collections.Generic;
using System.Linq;

namespace ZV.Compiler.Target.X86_16;

/// <summary>
/// A full-featured 16-bit x86 assembler for real-mode code. It supports all common
/// 8086/186/286 instructions, addressing modes, and fixups required by the ZV x86-16
/// bare-metal backend, while remaining usable as a standalone assembler.
/// </summary>
public sealed class X86_16Assembler
{
    private readonly List<byte> _bytes = new();
    private readonly Dictionary<string, int> _labels = new();
    private readonly List<Fixup> _fixups = new();
    private readonly ushort _imageBase;

    public X86_16Assembler(ushort imageBase)
    {
        _imageBase = imageBase;
    }

    public int CurrentOffset => _bytes.Count;
    public byte[] GetBytes() => _bytes.ToArray();
    public int GetLabelOffset(string name) => _labels.TryGetValue(name, out var offset) ? offset : -1;

    public void DefineLabel(string name)
    {
        _labels[name] = CurrentOffset;
    }

    public void EmitByte(byte b) => _bytes.Add(b);
    public void EmitBytes(IEnumerable<byte> bytes) => _bytes.AddRange(bytes);

    #region Data directives

    public void EmitDataByte(byte b) => _bytes.Add(b);
    public void EmitDataWord(ushort value) => EmitUshort(value);
    public void EmitDataDword(uint value)
    {
        EmitUshort((ushort)(value & 0xFFFF));
        EmitUshort((ushort)(value >> 16));
    }

    public void EmitDataString(string text)
    {
        foreach (var ch in text)
            EmitByte((byte)ch);
        EmitByte(0);
    }

    public void EmitDataBytes(byte[] data) => _bytes.AddRange(data);
    public void EmitDataBytes(IEnumerable<byte> data) => _bytes.AddRange(data);

    public void ReserveBytes(int count)
    {
        if (count < 0) throw new ArgumentOutOfRangeException(nameof(count));
        for (int i = 0; i < count; i++)
            EmitByte(0);
    }

    #endregion

    #region One-byte instructions and prefixes

    public void EmitNop() => EmitByte(0x90);
    public void EmitHlt() => EmitByte(0xF4);
    public void EmitWait() => EmitByte(0x9B);
    public void EmitLock() => EmitByte(0xF0);
    public void EmitRep() => EmitByte(0xF3);
    public void EmitRepnz() => EmitByte(0xF2);

    public void EmitCli() => EmitByte(0xFA);
    public void EmitSti() => EmitByte(0xFB);
    public void EmitCld() => EmitByte(0xFC);
    public void EmitStd() => EmitByte(0xFD);
    public void EmitClc() => EmitByte(0xF8);
    public void EmitStc() => EmitByte(0xF9);
    public void EmitCmc() => EmitByte(0xF5);

    public void EmitLodsb() => EmitByte(0xAC);
    public void EmitLodsw() => EmitByte(0xAD);
    public void EmitStosb() => EmitByte(0xAA);
    public void EmitStosw() => EmitByte(0xAB);
    public void EmitMovsb() => EmitByte(0xA4);
    public void EmitMovsw() => EmitByte(0xA5);
    public void EmitCmpsb() => EmitByte(0xA6);
    public void EmitCmpsw() => EmitByte(0xA7);
    public void EmitScasb() => EmitByte(0xAE);
    public void EmitScasw() => EmitByte(0xAF);

    public void EmitInt(byte vector) { EmitByte(0xCD); EmitByte(vector); }
    public void EmitInt3() => EmitByte(0xCC);
    public void EmitInto() => EmitByte(0xCE);
    public void EmitRet() => EmitByte(0xC3);
    public void EmitRetf() => EmitByte(0xCB);
    public void EmitIret() => EmitByte(0xCF);
    public void EmitCbw() => EmitByte(0x98);
    public void EmitCwd() => EmitByte(0x99);
    public void EmitPusha() => EmitByte(0x60);
    public void EmitPopa() => EmitByte(0x61);
    public void EmitPushf() => EmitByte(0x9C);
    public void EmitPopf() => EmitByte(0x9D);
    public void EmitLahf() => EmitByte(0x9F);
    public void EmitSahf() => EmitByte(0x9E);
    public void EmitXlatb() => EmitByte(0xD7);
    public void EmitDaa() => EmitByte(0x27);
    public void EmitDas() => EmitByte(0x2F);
    public void EmitAaa() => EmitByte(0x37);
    public void EmitAas() => EmitByte(0x3F);
    public void EmitAam(byte @base = 10) { EmitByte(0xD4); EmitByte(@base); }
    public void EmitAad(byte @base = 10) { EmitByte(0xD5); EmitByte(@base); }

    public void EmitSegmentOverride(X86_16SegmentRegister seg) => EmitByte((byte)(0x26 + ((int)seg << 3)));
    public void EmitOperandSizeOverride() => EmitByte(0x66);
    public void EmitAddressSizeOverride() => EmitByte(0x67);

    #endregion

    #region PUSH / POP

    public void EmitPush(X86_16Register reg) => EmitByte((byte)(0x50 + (int)reg));
    public void EmitPop(X86_16Register reg) => EmitByte((byte)(0x58 + (int)reg));

    public void EmitPush(X86_16SegmentRegister reg) => EmitByte((byte)(0x06 + ((int)reg << 3)));
    public void EmitPop(X86_16SegmentRegister reg)
    {
        if (reg == X86_16SegmentRegister.CS)
            throw new InvalidOperationException("POP CS is not encodable.");
        EmitByte((byte)(0x07 + ((int)reg << 3)));
    }

    public void EmitPushImm8(byte value) { EmitByte(0x6A); EmitByte(value); }
    public void EmitPushImm16(ushort value) { EmitByte(0x68); EmitUshort(value); }

    public void EmitPush(Operand src) { EmitByte(0xFF); EmitModRM(6, src); }
    public void EmitPop(Operand dst) { EmitByte(0x8F); EmitModRM(0, dst); }

    #endregion

    #region MOV

    public void EmitMovRegImm16(X86_16Register reg, ushort value)
    {
        EmitByte((byte)(0xB8 + (int)reg));
        EmitUshort(value);
    }

    public void EmitMovRegImm16(X86_16Register reg, string label)
    {
        EmitByte((byte)(0xB8 + (int)reg));
        _fixups.Add(new Fixup(CurrentOffset, label, FixupKind.Absolute16));
        EmitUshort(0);
    }

    public void EmitMovRegImm8(X86_16Register reg, byte value)
    {
        _ = value;
        throw new NotSupportedException("Use EmitMov(X86_16Register8, byte) instead.");
    }

    public void EmitMovAhImm8(byte value) { EmitByte(0xB4); EmitByte(value); }
    public void EmitMovAlImm8(byte value) { EmitByte(0xB0); EmitByte(value); }

    public void EmitMov(X86_16Register8 dst, byte value)
    {
        EmitByte((byte)(0xB0 + (int)dst));
        EmitByte(value);
    }

    public void EmitMov(X86_16Register dst, ushort value) => EmitMovRegImm16(dst, value);

    public void EmitMov(Operand dst, Operand src)
    {
        if (dst.IsRegister && src.IsRegister && dst.Is8Bit != src.Is8Bit)
            throw new ArgumentException("Operand size mismatch in MOV.");

        if (dst.IsRegister && src.IsRegister)
        {
            EmitByte(dst.Is8Bit ? (byte)0x88 : (byte)0x89);
            EmitModRM(dst.RegisterNumber, src);
            return;
        }

        if (dst.IsMemory && src.IsRegister)
        {
            EmitByte(src.Is8Bit ? (byte)0x88 : (byte)0x89);
            EmitModRM(src.RegisterNumber, dst);
            return;
        }

        if (dst.IsRegister && src.IsMemory)
        {
            EmitByte(dst.Is8Bit ? (byte)0x8A : (byte)0x8B);
            EmitModRM(dst.RegisterNumber, src);
            return;
        }

        throw new ArgumentException("Unsupported MOV operand combination.");
    }

    public void EmitMov(X86_16Register dst, X86_16SegmentRegister src)
    {
        EmitByte(0x8C);
        EmitModRM((int)src, Operand.Reg(dst));
    }

    public void EmitMov(X86_16SegmentRegister dst, X86_16Register src)
    {
        if (dst == X86_16SegmentRegister.CS)
            throw new InvalidOperationException("MOV CS, reg is not encodable.");
        EmitByte(0x8E);
        EmitModRM((int)dst, Operand.Reg(src));
    }

    public void EmitMovAlMemory(ushort address) { EmitByte(0xA0); EmitUshort(address); }
    public void EmitMovAxMemory(ushort address) { EmitByte(0xA1); EmitUshort(address); }
    public void EmitMovMemoryAl(ushort address) { EmitByte(0xA2); EmitUshort(address); }
    public void EmitMovMemoryAx(ushort address) { EmitByte(0xA3); EmitUshort(address); }

    public void EmitMovMem8Dl(ushort address)
    {
        EmitByte(0x88);
        EmitByte(0x16);
        EmitUshort(address);
    }

    public void EmitMovImm(Operand dst, ushort value)
    {
        if (dst.IsRegister)
        {
            if (dst.Is8Bit)
                throw new ArgumentException("Use EmitMov(X86_16Register8, byte) for 8-bit registers.");
            EmitMovRegImm16((X86_16Register)dst.RegisterNumber, value);
            return;
        }

        if (dst.Is8Bit)
        {
            EmitByte(0xC6);
            EmitModRM(0, dst);
            EmitByte((byte)(value & 0xFF));
        }
        else
        {
            EmitByte(0xC7);
            EmitModRM(0, dst);
            EmitUshort(value);
        }
    }

    public void EmitLea(X86_16Register dst, Operand src)
    {
        if (!src.IsMemory)
            throw new ArgumentException("LEA requires a memory operand.", nameof(src));
        EmitByte(0x8D);
        EmitModRM((int)dst, src);
    }

    public void EmitLds(X86_16Register dst, Operand src)
    {
        if (!src.IsMemory)
            throw new ArgumentException("LDS requires a memory operand.", nameof(src));
        EmitByte(0xC5);
        EmitModRM((int)dst, src);
    }

    public void EmitLes(X86_16Register dst, Operand src)
    {
        if (!src.IsMemory)
            throw new ArgumentException("LES requires a memory operand.", nameof(src));
        EmitByte(0xC4);
        EmitModRM((int)dst, src);
    }

    #endregion

    #region XCHG

    public void EmitXchg(X86_16Register reg1, X86_16Register reg2)
    {
        if (reg1 == X86_16Register.AX)
        {
            EmitByte((byte)(0x90 + (int)reg2));
        }
        else if (reg2 == X86_16Register.AX)
        {
            EmitByte((byte)(0x90 + (int)reg1));
        }
        else
        {
            EmitByte(0x87);
            EmitModRM((int)reg1, Operand.Reg(reg2));
        }
    }

    public void EmitXchg(X86_16Register8 reg1, X86_16Register8 reg2)
    {
        EmitByte(0x86);
        EmitModRM((int)reg1, Operand.Reg(reg2));
    }

    public void EmitXchg(X86_16Register reg, Operand dst) => EmitXchg(dst, reg);
    public void EmitXchg(X86_16Register8 reg, Operand dst) => EmitXchg(dst, reg);

    public void EmitXchg(Operand dst, X86_16Register reg)
    {
        if (!dst.IsMemory)
            throw new ArgumentException("XCHG memory form requires a memory operand.");
        EmitByte(0x87);
        EmitModRM((int)reg, dst);
    }

    public void EmitXchg(Operand dst, X86_16Register8 reg)
    {
        if (!dst.IsMemory)
            throw new ArgumentException("XCHG memory form requires a memory operand.");
        EmitByte(0x86);
        EmitModRM((int)reg, dst);
    }

    #endregion

    #region Arithmetic: ADD/ADC/INC/SUB/SBB/DEC/NEG/CMP etc.

    public void EmitAdd(Operand dst, Operand src) => EmitArithmetic(0x00, dst, src);
    public void EmitAdc(Operand dst, Operand src) => EmitArithmetic(0x10, dst, src);
    public void EmitSub(Operand dst, Operand src) => EmitArithmetic(0x28, dst, src);
    public void EmitSbb(Operand dst, Operand src) => EmitArithmetic(0x18, dst, src);
    public void EmitAnd(Operand dst, Operand src) => EmitArithmetic(0x20, dst, src);
    public void EmitOr(Operand dst, Operand src) => EmitArithmetic(0x08, dst, src);
    public void EmitXor(Operand dst, Operand src) => EmitArithmetic(0x30, dst, src);
    public void EmitCmp(Operand dst, Operand src) => EmitArithmetic(0x38, dst, src);

    public void EmitTest(Operand dst, Operand src)
    {
        if (dst.IsRegister && src.IsRegister && dst.Is8Bit != src.Is8Bit)
            throw new ArgumentException("Operand size mismatch in TEST.");

        if (dst.IsRegister && src.IsRegister)
        {
            EmitByte(dst.Is8Bit ? (byte)0x84 : (byte)0x85);
            EmitModRM(dst.RegisterNumber, src);
            return;
        }

        if (dst.IsMemory && src.IsRegister)
        {
            EmitByte(src.Is8Bit ? (byte)0x84 : (byte)0x85);
            EmitModRM(src.RegisterNumber, dst);
            return;
        }

        if (dst.IsRegister && src.IsMemory)
        {
            EmitByte(dst.Is8Bit ? (byte)0x84 : (byte)0x85);
            EmitModRM(dst.RegisterNumber, src);
            return;
        }

        throw new ArgumentException("Unsupported TEST operand combination.");
    }

    public void EmitAdd(X86_16Register dst, ushort value) => EmitGroupImm(0x81, 0, Operand.Reg(dst), value);
    public void EmitAdc(X86_16Register dst, ushort value) => EmitGroupImm(0x81, 2, Operand.Reg(dst), value);
    public void EmitSub(X86_16Register dst, ushort value) => EmitGroupImm(0x81, 5, Operand.Reg(dst), value);
    public void EmitSbb(X86_16Register dst, ushort value) => EmitGroupImm(0x81, 3, Operand.Reg(dst), value);
    public void EmitAnd(X86_16Register dst, ushort value) => EmitGroupImm(0x81, 4, Operand.Reg(dst), value);
    public void EmitOr(X86_16Register dst, ushort value) => EmitGroupImm(0x81, 1, Operand.Reg(dst), value);
    public void EmitXor(X86_16Register dst, ushort value) => EmitGroupImm(0x81, 6, Operand.Reg(dst), value);
    public void EmitCmp(X86_16Register dst, ushort value) => EmitGroupImm(0x81, 7, Operand.Reg(dst), value);

    public void EmitAdd(X86_16Register8 dst, byte value) => EmitGroupImm(0x80, 0, Operand.Reg(dst), value);
    public void EmitAdc(X86_16Register8 dst, byte value) => EmitGroupImm(0x80, 2, Operand.Reg(dst), value);
    public void EmitSub(X86_16Register8 dst, byte value) => EmitGroupImm(0x80, 5, Operand.Reg(dst), value);
    public void EmitSbb(X86_16Register8 dst, byte value) => EmitGroupImm(0x80, 3, Operand.Reg(dst), value);
    public void EmitAnd(X86_16Register8 dst, byte value) => EmitGroupImm(0x80, 4, Operand.Reg(dst), value);
    public void EmitOr(X86_16Register8 dst, byte value) => EmitGroupImm(0x80, 1, Operand.Reg(dst), value);
    public void EmitXor(X86_16Register8 dst, byte value) => EmitGroupImm(0x80, 6, Operand.Reg(dst), value);
    public void EmitCmp(X86_16Register8 dst, byte value) => EmitGroupImm(0x80, 7, Operand.Reg(dst), value);

    public void EmitAddRegImm16(X86_16Register reg, ushort value) => EmitAdd(reg, value);
    public void EmitDecReg16(X86_16Register reg) => EmitByte((byte)(0x48 + (int)reg));

    public void EmitAdd(Operand dst, ushort value) => EmitGroupImm(0x81, 0, dst, value);
    public void EmitAdc(Operand dst, ushort value) => EmitGroupImm(0x81, 2, dst, value);
    public void EmitSub(Operand dst, ushort value) => EmitGroupImm(0x81, 5, dst, value);
    public void EmitSbb(Operand dst, ushort value) => EmitGroupImm(0x81, 3, dst, value);
    public void EmitAnd(Operand dst, ushort value) => EmitGroupImm(0x81, 4, dst, value);
    public void EmitOr(Operand dst, ushort value) => EmitGroupImm(0x81, 1, dst, value);
    public void EmitXor(Operand dst, ushort value) => EmitGroupImm(0x81, 6, dst, value);
    public void EmitCmp(Operand dst, ushort value) => EmitGroupImm(0x81, 7, dst, value);

    public void EmitAdd(Operand dst, byte value) => EmitArithmeticImm(0, dst, value);
    public void EmitAdc(Operand dst, byte value) => EmitArithmeticImm(2, dst, value);
    public void EmitSub(Operand dst, byte value) => EmitArithmeticImm(5, dst, value);
    public void EmitSbb(Operand dst, byte value) => EmitArithmeticImm(3, dst, value);
    public void EmitAnd(Operand dst, byte value) => EmitArithmeticImm(4, dst, value);
    public void EmitOr(Operand dst, byte value) => EmitArithmeticImm(1, dst, value);
    public void EmitXor(Operand dst, byte value) => EmitArithmeticImm(6, dst, value);
    public void EmitCmp(Operand dst, byte value) => EmitArithmeticImm(7, dst, value);

    public void EmitAddSignExtended(Operand dst, sbyte value) => EmitGroupImm(0x83, 0, dst, (byte)value);
    public void EmitAdcSignExtended(Operand dst, sbyte value) => EmitGroupImm(0x83, 2, dst, (byte)value);
    public void EmitSubSignExtended(Operand dst, sbyte value) => EmitGroupImm(0x83, 5, dst, (byte)value);
    public void EmitSbbSignExtended(Operand dst, sbyte value) => EmitGroupImm(0x83, 3, dst, (byte)value);
    public void EmitAndSignExtended(Operand dst, sbyte value) => EmitGroupImm(0x83, 4, dst, (byte)value);
    public void EmitOrSignExtended(Operand dst, sbyte value) => EmitGroupImm(0x83, 1, dst, (byte)value);
    public void EmitXorSignExtended(Operand dst, sbyte value) => EmitGroupImm(0x83, 6, dst, (byte)value);
    public void EmitCmpSignExtended(Operand dst, sbyte value) => EmitGroupImm(0x83, 7, dst, (byte)value);

    public void EmitInc(X86_16Register reg) => EmitByte((byte)(0x40 + (int)reg));
    public void EmitInc(X86_16Register8 reg) { EmitByte(0xFE); EmitModRM(0, Operand.Reg(reg)); }
    public void EmitInc(Operand op) { EmitByte(op.Is8Bit ? (byte)0xFE : (byte)0xFF); EmitModRM(0, op); }

    public void EmitDec(X86_16Register reg) => EmitByte((byte)(0x48 + (int)reg));
    public void EmitDec(X86_16Register8 reg) { EmitByte(0xFE); EmitModRM(1, Operand.Reg(reg)); }
    public void EmitDec(Operand op) { EmitByte(op.Is8Bit ? (byte)0xFE : (byte)0xFF); EmitModRM(1, op); }

    public void EmitNeg(Operand op) { EmitByte(op.Is8Bit ? (byte)0xF6 : (byte)0xF7); EmitModRM(3, op); }
    public void EmitNot(Operand op) { EmitByte(op.Is8Bit ? (byte)0xF6 : (byte)0xF7); EmitModRM(2, op); }

    public void EmitMul(Operand op) { EmitByte(op.Is8Bit ? (byte)0xF6 : (byte)0xF7); EmitModRM(4, op); }
    public void EmitImul(Operand op) { EmitByte(op.Is8Bit ? (byte)0xF6 : (byte)0xF7); EmitModRM(5, op); }
    public void EmitDiv(Operand op) { EmitByte(op.Is8Bit ? (byte)0xF6 : (byte)0xF7); EmitModRM(6, op); }
    public void EmitIdiv(Operand op) { EmitByte(op.Is8Bit ? (byte)0xF6 : (byte)0xF7); EmitModRM(7, op); }

    public void EmitCmpAlImm8(byte value) { EmitByte(0x3C); EmitByte(value); }
    public void EmitCmpAxImm16(ushort value) { EmitByte(0x3D); EmitUshort(value); }
    public void EmitOrAlAl() { EmitByte(0x0A); EmitByte(0xC0); }

    #endregion

    #region Shift/rotate

    public void EmitShl(Operand dst, byte count) => EmitShift(4, dst, count);
    public void EmitShr(Operand dst, byte count) => EmitShift(5, dst, count);
    public void EmitSar(Operand dst, byte count) => EmitShift(7, dst, count);
    public void EmitRol(Operand dst, byte count) => EmitShift(0, dst, count);
    public void EmitRor(Operand dst, byte count) => EmitShift(1, dst, count);
    public void EmitRcl(Operand dst, byte count) => EmitShift(2, dst, count);
    public void EmitRcr(Operand dst, byte count) => EmitShift(3, dst, count);

    public void EmitShlCl(Operand dst) => EmitShiftCl(4, dst);
    public void EmitShrCl(Operand dst) => EmitShiftCl(5, dst);
    public void EmitSarCl(Operand dst) => EmitShiftCl(7, dst);
    public void EmitRolCl(Operand dst) => EmitShiftCl(0, dst);
    public void EmitRorCl(Operand dst) => EmitShiftCl(1, dst);
    public void EmitRclCl(Operand dst) => EmitShiftCl(2, dst);
    public void EmitRcrCl(Operand dst) => EmitShiftCl(3, dst);

    #endregion

    #region Control flow

    public void EmitCall(string label)
    {
        EmitByte(0xE8);
        _fixups.Add(new Fixup(CurrentOffset, label, FixupKind.Rel16));
        EmitUshort(0);
    }

    public void EmitCallNear(Operand target)
    {
        if (!target.IsMemory && !target.IsRegister)
            throw new ArgumentException("CALL near requires a register or memory operand.");
        EmitByte(0xFF);
        EmitModRM(2, target);
    }

    public void EmitCallFar(ushort segment, ushort offset)
    {
        EmitByte(0x9A);
        EmitUshort(offset);
        EmitUshort(segment);
    }

    public void EmitJmpShort(string label)
    {
        EmitByte(0xEB);
        _fixups.Add(new Fixup(CurrentOffset, label, FixupKind.Rel8));
        EmitByte(0);
    }

    public void EmitJmpNear(string label)
    {
        EmitByte(0xE9);
        _fixups.Add(new Fixup(CurrentOffset, label, FixupKind.Rel16));
        EmitUshort(0);
    }

    public void EmitJmpNear(Operand target)
    {
        if (!target.IsMemory && !target.IsRegister)
            throw new ArgumentException("JMP near requires a register or memory operand.");
        EmitByte(0xFF);
        EmitModRM(4, target);
    }

    public void EmitJmpAbsoluteFar(ushort segment, ushort offset)
    {
        EmitByte(0xEA);
        EmitUshort(offset);
        EmitUshort(segment);
    }

    public void EmitJmpFar(Operand target)
    {
        if (!target.IsMemory)
            throw new ArgumentException("JMP far requires a memory operand.");
        EmitByte(0xFF);
        EmitModRM(5, target);
    }

    public void EmitRet(ushort popBytes) { EmitByte(0xC2); EmitUshort(popBytes); }
    public void EmitRetf(ushort popBytes) { EmitByte(0xCA); EmitUshort(popBytes); }

    public void EmitJeShort(string label) => EmitJccShort(X86_16ConditionCode.Equal, label);
    public void EmitJneShort(string label) => EmitJccShort(X86_16ConditionCode.NotEqual, label);
    public void EmitJcShort(string label) => EmitJccShort(X86_16ConditionCode.Below, label);
    public void EmitJncShort(string label) => EmitJccShort(X86_16ConditionCode.NotBelow, label);
    public void EmitJsShort(string label) => EmitJccShort(X86_16ConditionCode.Sign, label);
    public void EmitJnsShort(string label) => EmitJccShort(X86_16ConditionCode.NotSign, label);
    public void EmitJoShort(string label) => EmitJccShort(X86_16ConditionCode.Overflow, label);
    public void EmitJnoShort(string label) => EmitJccShort(X86_16ConditionCode.NotOverflow, label);
    public void EmitJpShort(string label) => EmitJccShort(X86_16ConditionCode.Parity, label);
    public void EmitJnpShort(string label) => EmitJccShort(X86_16ConditionCode.NotParity, label);
    public void EmitJaShort(string label) => EmitJccShort(X86_16ConditionCode.Above, label);
    public void EmitJnaShort(string label) => EmitJccShort(X86_16ConditionCode.NotAbove, label);
    public void EmitJlShort(string label) => EmitJccShort(X86_16ConditionCode.Less, label);
    public void EmitJgeShort(string label) => EmitJccShort(X86_16ConditionCode.GreaterOrEqual, label);
    public void EmitJleShort(string label) => EmitJccShort(X86_16ConditionCode.LessOrEqual, label);
    public void EmitJgShort(string label) => EmitJccShort(X86_16ConditionCode.Greater, label);

    public void EmitJccShort(X86_16ConditionCode cc, string label)
    {
        EmitByte((byte)(0x70 + (int)cc));
        _fixups.Add(new Fixup(CurrentOffset, label, FixupKind.Rel8));
        EmitByte(0);
    }

    /// <summary>
    /// Emits a 32-bit-mode-style near conditional jump (0F 8x rel16). Requires a 386+ CPU.
    /// </summary>
    public void EmitJccNear(X86_16ConditionCode cc, string label)
    {
        EmitByte(0x0F);
        EmitByte((byte)(0x80 + (int)cc));
        _fixups.Add(new Fixup(CurrentOffset, label, FixupKind.Rel16));
        EmitUshort(0);
    }

    public void EmitJcxzShort(string label) { EmitByte(0xE3); _fixups.Add(new Fixup(CurrentOffset, label, FixupKind.Rel8)); EmitByte(0); }
    public void EmitLoopShort(string label) { EmitByte(0xE2); _fixups.Add(new Fixup(CurrentOffset, label, FixupKind.Rel8)); EmitByte(0); }
    public void EmitLoopeShort(string label) { EmitByte(0xE1); _fixups.Add(new Fixup(CurrentOffset, label, FixupKind.Rel8)); EmitByte(0); }
    public void EmitLoopneShort(string label) { EmitByte(0xE0); _fixups.Add(new Fixup(CurrentOffset, label, FixupKind.Rel8)); EmitByte(0); }

    #endregion

    #region IN/OUT

    public void EmitInAl(byte port) { EmitByte(0xE4); EmitByte(port); }
    public void EmitInAx(byte port) { EmitByte(0xE5); EmitByte(port); }
    public void EmitInAlDx() => EmitByte(0xEC);
    public void EmitInAxDx() => EmitByte(0xED);

    public void EmitOut(byte port, X86_16Register8 value)
    {
        if (value != X86_16Register8.AL) throw new ArgumentException("OUT imm, AL/AX only supports AL.", nameof(value));
        EmitByte(0xE6); EmitByte(port);
    }

    public void EmitOut(byte port, X86_16Register value)
    {
        if (value != X86_16Register.AX) throw new ArgumentException("OUT imm, AL/AX only supports AX.", nameof(value));
        EmitByte(0xE7); EmitByte(port);
    }

    public void EmitOutDxAl() => EmitByte(0xEE);
    public void EmitOutDxAx() => EmitByte(0xEF);

    #endregion

    #region Build and fixups

    public byte[] Build()
    {
        var output = _bytes.ToList();
        foreach (var fixup in _fixups)
        {
            if (!_labels.TryGetValue(fixup.Label, out int labelOffset))
                throw new InvalidOperationException($"Unresolved label '{fixup.Label}'.");

            int value;
            switch (fixup.Kind)
            {
                case FixupKind.Rel16:
                    value = (labelOffset + _imageBase) - (fixup.Position + _imageBase + 2);
                    break;
                case FixupKind.Rel8:
                    value = labelOffset - (fixup.Position + 1);
                    break;
                case FixupKind.Absolute16:
                    value = labelOffset + _imageBase;
                    break;
                default:
                    throw new InvalidOperationException("Unknown fixup kind.");
            }

            if (fixup.Kind == FixupKind.Rel8 && (value < sbyte.MinValue || value > sbyte.MaxValue))
                throw new InvalidOperationException($"Short jump to '{fixup.Label}' out of range.");

            output[fixup.Position] = (byte)(value & 0xFF);
            if (fixup.Kind != FixupKind.Rel8)
            {
                output[fixup.Position + 1] = (byte)((value >> 8) & 0xFF);
            }
        }
        return output.ToArray();
    }

    #endregion

    #region Private helpers

    private void EmitUshort(ushort value)
    {
        EmitByte((byte)(value & 0xFF));
        EmitByte((byte)((value >> 8) & 0xFF));
    }

    private void EmitModRM(int reg, Operand rm)
    {
        if (rm.IsRegister)
        {
            if (rm.Is8Bit && !IsValid8BitRegister(rm.RegisterNumber))
                throw new InvalidOperationException($"Invalid 8-bit register encoding {rm.RegisterNumber}.");

            EmitByte((byte)(0xC0 | (reg << 3) | rm.RegisterNumber));
            return;
        }

        if (rm.IsDirectMemory)
        {
            // mod=00, r/m=110, followed by disp16.
            EmitByte((byte)((reg << 3) | 0x06));
            EmitUshort(rm.DirectAddress);
            return;
        }

        var (mod, disp) = ResolveDisplacement(rm);
        EmitByte((byte)((mod << 6) | (reg << 3) | (int)rm.AddressMode));

        if (mod == 1)
        {
            EmitByte((byte)(sbyte)disp);
        }
        else if (mod == 2)
        {
            EmitUshort((ushort)disp);
        }
    }

    private static (byte mod, short disp) ResolveDisplacement(Operand rm)
    {
        short displacement = rm.Displacement;
        bool isBpBase = rm.IsBpBase;

        if (displacement == 0 && !isBpBase)
            return (0, 0);

        if (displacement >= sbyte.MinValue && displacement <= sbyte.MaxValue)
            return (1, displacement);

        return (2, displacement);
    }

    private void EmitArithmetic(byte baseOpcode, Operand dst, Operand src)
    {
        if (dst.IsRegister && src.IsRegister && dst.Is8Bit != src.Is8Bit)
            throw new ArgumentException("Operand size mismatch.");

        if (src.IsRegister)
        {
            // reg field is the source when direction=0, or the destination when direction=1.
            bool direction = dst.IsRegister;
            byte opcode = (dst.Is8Bit || src.Is8Bit)
                ? (byte)(baseOpcode + (direction ? 2 : 0))
                : (byte)(baseOpcode + (direction ? 3 : 1));
            EmitByte(opcode);
            int regField = direction ? dst.RegisterNumber : src.RegisterNumber;
            EmitModRM(regField, direction ? src : dst);
            return;
        }

        if (dst.IsRegister && src.IsMemory)
        {
            byte opcode = dst.Is8Bit ? (byte)(baseOpcode + 2) : (byte)(baseOpcode + 3);
            EmitByte(opcode);
            EmitModRM(dst.RegisterNumber, src);
            return;
        }

        throw new ArgumentException($"Unsupported arithmetic operand combination: {dst} and {src}.");
    }

    private void EmitArithmeticImm(int groupExtension, Operand dst, byte value)
    {
        if (dst.Is8Bit)
        {
            EmitGroupImm(0x80, groupExtension, dst, value);
            return;
        }

        // For 16-bit destinations, prefer the sign-extended immediate form when it fits.
        if (value <= 127)
        {
            EmitGroupImm(0x83, groupExtension, dst, value);
        }
        else
        {
            EmitGroupImm(0x81, groupExtension, dst, value);
        }
    }

    private void EmitGroupImm(byte opcode, int groupExtension, Operand dst, ushort value)
    {
        EmitByte(opcode);
        EmitModRM(groupExtension, dst);
        if (dst.Is8Bit || opcode == 0x80)
        {
            EmitByte((byte)(value & 0xFF));
        }
        else if (opcode == 0x81)
        {
            EmitUshort(value);
        }
        else if (opcode == 0x83)
        {
            EmitByte((byte)(value & 0xFF));
        }
    }

    private void EmitShift(int operation, Operand dst, byte count)
    {
        if (count == 1)
        {
            EmitByte(dst.Is8Bit ? (byte)0xD0 : (byte)0xD1);
        }
        else
        {
            EmitByte(dst.Is8Bit ? (byte)0xC0 : (byte)0xC1);
        }
        EmitModRM(operation, dst);
        if (count != 1)
        {
            EmitByte(count);
        }
    }

    private void EmitShiftCl(int operation, Operand dst)
    {
        EmitByte(dst.Is8Bit ? (byte)0xD2 : (byte)0xD3);
        EmitModRM(operation, dst);
    }

    private static bool IsValid8BitRegister(int registerNumber) => registerNumber is >= 0 and <= 7;

    private enum FixupKind { Rel16, Rel8, Absolute16 }
    private record Fixup(int Position, string Label, FixupKind Kind);

    #endregion
}
