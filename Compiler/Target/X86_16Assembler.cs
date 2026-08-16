using System;
using System.Collections.Generic;
using System.Linq;

namespace ZV.Compiler.Target;

public enum X86_16Register
{
    AX, CX, DX, BX, SP, BP, SI, DI
}

/// <summary>
/// A tiny 16-bit x86 assembler used by the x86-16 bare-metal backend. It supports just
/// enough instructions and addressing modes to compile a minimal ZV kernel entry point.
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

    public void EmitNop() => EmitByte(0x90);
    public void EmitHlt() => EmitByte(0xF4);
    public void EmitCli() => EmitByte(0xFA);
    public void EmitSti() => EmitByte(0xFB);
    public void EmitLodsb() => EmitByte(0xAC);
    public void EmitRet() => EmitByte(0xC3);
    public void EmitInt(byte vector) { EmitByte(0xCD); EmitByte(vector); }

    public void EmitPush(X86_16Register reg) => EmitByte((byte)(0x50 + (int)reg));
    public void EmitPop(X86_16Register reg) => EmitByte((byte)(0x58 + (int)reg));

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
        // Only works for AX..DI high/low? We only need AH/BH/CH/DH here.
        // For simplicity restrict to full register mov with imm8 via AH encoding.
        throw new NotSupportedException("Use EmitMovAhImm8 etc.");
    }

    public void EmitMovAhImm8(byte value) { EmitByte(0xB4); EmitByte(value); }
    public void EmitMovAlImm8(byte value) { EmitByte(0xB0); EmitByte(value); }

    public void EmitCmpAlImm8(byte value) { EmitByte(0x3C); EmitByte(value); }
    public void EmitOrAlAl() { EmitByte(0x0A); EmitByte(0xC0); }

    public void EmitAddRegImm16(X86_16Register reg, ushort value)
    {
        EmitByte(0x81);
        EmitByte((byte)(0xC0 + (int)reg)); // ADD r/m16, imm16
        EmitUshort(value);
    }

    public void EmitDecReg16(X86_16Register reg) => EmitByte((byte)(0x48 + (int)reg));

    public void EmitJneShort(string label)
    {
        EmitByte(0x75);
        _fixups.Add(new Fixup(CurrentOffset, label, FixupKind.Rel8));
        EmitByte(0);
    }

    public void EmitJcShort(string label)
    {
        EmitByte(0x72);
        _fixups.Add(new Fixup(CurrentOffset, label, FixupKind.Rel8));
        EmitByte(0);
    }

    public void EmitMovMem8Dl(ushort address)
    {
        // mov [disp16], dl  => 88 16 disp16
        EmitByte(0x88);
        EmitByte(0x16);
        EmitUshort(address);
    }

    public void EmitCall(string label)
    {
        EmitByte(0xE8);
        _fixups.Add(new Fixup(CurrentOffset, label, FixupKind.Rel16));
        EmitUshort(0);
    }

    public void EmitJmpShort(string label)
    {
        EmitByte(0xEB);
        _fixups.Add(new Fixup(CurrentOffset, label, FixupKind.Rel8));
        EmitByte(0);
    }

    public void EmitJeShort(string label)
    {
        EmitByte(0x74);
        _fixups.Add(new Fixup(CurrentOffset, label, FixupKind.Rel8));
        EmitByte(0);
    }

    public void EmitJmpAbsoluteFar(ushort segment, ushort offset)
    {
        // EA ip cs - far jmp to segment:offset
        EmitByte(0xEA);
        EmitUshort(offset);
        EmitUshort(segment);
    }

    public void EmitDataString(string text)
    {
        foreach (var ch in text)
            EmitByte((byte)ch);
        EmitByte(0);
    }

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
                output[fixup.Position + 1] = (byte)((value >> 8) & 0xFF);
        }
        return output.ToArray();
    }

    private void EmitUshort(ushort value)
    {
        EmitByte((byte)(value & 0xFF));
        EmitByte((byte)((value >> 8) & 0xFF));
    }

    private enum FixupKind { Rel16, Rel8, Absolute16 }
    private record Fixup(int Position, string Label, FixupKind Kind);
}
