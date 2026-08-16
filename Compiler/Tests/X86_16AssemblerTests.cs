using System;
using System.Linq;
using Xunit;
using ZV.Compiler.Target;
using ZV.Compiler.Target.X86_16;

namespace ZV.Compiler.Tests;

public class X86_16AssemblerTests
{
    [Fact]
    public void PushPopGpRegisters()
    {
        var asm = new X86_16Assembler(0);
        foreach (var reg in Enum.GetValues<X86_16Register>())
        {
            asm.EmitPush(reg);
            asm.EmitPop(reg);
        }
        var bytes = asm.Build();
        for (int i = 0; i < 8; i++)
        {
            Assert.Equal((byte)(0x50 + i), bytes[2 * i]);
            Assert.Equal((byte)(0x58 + i), bytes[2 * i + 1]);
        }
    }

    [Fact]
    public void PushPopSegmentRegisters()
    {
        var asm = new X86_16Assembler(0);
        asm.EmitPush(X86_16SegmentRegister.ES);
        asm.EmitPush(X86_16SegmentRegister.CS);
        asm.EmitPush(X86_16SegmentRegister.SS);
        asm.EmitPush(X86_16SegmentRegister.DS);
        asm.EmitPop(X86_16SegmentRegister.ES);
        asm.EmitPop(X86_16SegmentRegister.SS);
        asm.EmitPop(X86_16SegmentRegister.DS);

        var bytes = asm.Build();
        Assert.Equal(new byte[] { 0x06, 0x0E, 0x16, 0x1E, 0x07, 0x17, 0x1F }, bytes);
    }

    [Fact]
    public void PopCsThrows()
    {
        var asm = new X86_16Assembler(0);
        Assert.Throws<InvalidOperationException>(() => asm.EmitPop(X86_16SegmentRegister.CS));
    }

    [Fact]
    public void MovRegImm()
    {
        var asm = new X86_16Assembler(0);
        asm.EmitMov(X86_16Register.AX, (ushort)0x1234);
        asm.EmitMov(X86_16Register8.CL, (byte)0x56);
        var bytes = asm.Build();
        Assert.Equal(new byte[] { 0xB8, 0x34, 0x12, 0xB1, 0x56 }, bytes);
    }

    [Fact]
    public void MovRegReg()
    {
        var asm = new X86_16Assembler(0);
        asm.EmitMov(Operand.Reg(X86_16Register.AX), Operand.Reg(X86_16Register.BX));
        asm.EmitMov(Operand.Reg(X86_16Register8.AH), Operand.Reg(X86_16Register8.BL));
        var bytes = asm.Build();
        // MOV AX, BX => 89 C3
        Assert.Equal(0x89, bytes[0]);
        Assert.Equal(0xC3, bytes[1]);
        // MOV AH, BL => 88 E3
        Assert.Equal(0x88, bytes[2]);
        Assert.Equal(0xE3, bytes[3]);
    }

    [Fact]
    public void MovRegMem()
    {
        var asm = new X86_16Assembler(0);
        asm.EmitMov(Operand.Reg(X86_16Register.AX), Operand.Memory(0x1234));
        var bytes = asm.Build();
        // MOV AX, [1234] => 8B 06 34 12
        Assert.Equal(new byte[] { 0x8B, 0x06, 0x34, 0x12 }, bytes);
    }

    [Fact]
    public void MovMemReg()
    {
        var asm = new X86_16Assembler(0);
        asm.EmitMov(Operand.Memory(0x1234), Operand.Reg(X86_16Register.DX));
        var bytes = asm.Build();
        // MOV [1234], DX => 89 16 34 12
        Assert.Equal(new byte[] { 0x89, 0x16, 0x34, 0x12 }, bytes);
    }

    [Fact]
    public void MovRegIndirect()
    {
        var asm = new X86_16Assembler(0);
        asm.EmitMov(Operand.Reg(X86_16Register.AX), Operand.Memory(X86_16Register.BX));
        var bytes = asm.Build();
        // MOV AX, [BX] => 8B 07
        Assert.Equal(new byte[] { 0x8B, 0x07 }, bytes);
    }

    [Fact]
    public void MovBpWithZeroDisplacement()
    {
        // [BP] with no displacement requires mod=01 disp8=0.
        var asm = new X86_16Assembler(0);
        asm.EmitMov(Operand.Reg(X86_16Register.AX), Operand.Memory(X86_16Register.BP));
        var bytes = asm.Build();
        // MOV AX, [BP] => 8B 46 00
        Assert.Equal(new byte[] { 0x8B, 0x46, 0x00 }, bytes);
    }

    [Fact]
    public void MovBasedIndexedWithDisplacement()
    {
        var asm = new X86_16Assembler(0);
        asm.EmitMov(Operand.Memory(X86_16Register.BP, X86_16Register.SI, 0x12), Operand.Reg(X86_16Register.AX));
        var bytes = asm.Build();
        // MOV [BP+SI+12], AX => 89 42 12
        Assert.Equal(new byte[] { 0x89, 0x42, 0x12 }, bytes);
    }

    [Fact]
    public void MovBasedIndexedWith16BitDisplacement()
    {
        var asm = new X86_16Assembler(0);
        asm.EmitMov(Operand.Memory(X86_16Register.BX, X86_16Register.SI, 0x1234), Operand.Reg(X86_16Register.CX));
        var bytes = asm.Build();
        // MOV [BX+SI+1234], CX => 89 88 34 12
        Assert.Equal(new byte[] { 0x89, 0x88, 0x34, 0x12 }, bytes);
    }

    [Fact]
    public void AddRegImm16()
    {
        var asm = new X86_16Assembler(0);
        asm.EmitAddRegImm16(X86_16Register.BX, 0x1234);
        var bytes = asm.Build();
        Assert.Equal(new byte[] { 0x81, 0xC3, 0x34, 0x12 }, bytes);
    }

    [Fact]
    public void AddMemImmChoosesShortForm()
    {
        var asm = new X86_16Assembler(0);
        asm.EmitAdd(Operand.Memory(0x100), (byte)1);
        var bytes = asm.Build();
        // ADD word [0100], 1 => 83 06 01 00 01
        Assert.Equal(new byte[] { 0x83, 0x06, 0x00, 0x01, 0x01 }, bytes);
    }

    [Fact]
    public void AddMemImmUsesWordFormWhenByteDoesNotFitSByte()
    {
        var asm = new X86_16Assembler(0);
        asm.EmitAdd(Operand.Memory(0x100), (byte)0x80);
        var bytes = asm.Build();
        // ADD word [0100], 0080 => 81 06 00 01 80 00
        Assert.Equal(new byte[] { 0x81, 0x06, 0x00, 0x01, 0x80, 0x00 }, bytes);
    }

    [Fact]
    public void SubRegReg()
    {
        var asm = new X86_16Assembler(0);
        asm.EmitSub(Operand.Reg(X86_16Register.CX), Operand.Reg(X86_16Register.DX));
        var bytes = asm.Build();
        // SUB CX, DX => 2B CA (direction=1, destination in reg field)
        Assert.Equal(new byte[] { 0x2B, 0xCA }, bytes);
    }

    [Fact]
    public void CmpMemReg()
    {
        var asm = new X86_16Assembler(0);
        asm.EmitCmp(Operand.Memory(0x200), Operand.Reg(X86_16Register8.AL));
        var bytes = asm.Build();
        // CMP byte [0200], AL => 38 06 00 02
        Assert.Equal(new byte[] { 0x38, 0x06, 0x00, 0x02 }, bytes);
    }

    [Fact]
    public void IncDecReg()
    {
        var asm = new X86_16Assembler(0);
        asm.EmitInc(X86_16Register.AX);
        asm.EmitDec(X86_16Register.BP);
        var bytes = asm.Build();
        Assert.Equal(new byte[] { 0x40, 0x4D }, bytes);
    }

    [Fact]
    public void IncDecMem()
    {
        var asm = new X86_16Assembler(0);
        asm.EmitInc(Operand.Memory(0x100));
        asm.EmitDec(Operand.Memory(0x100));
        var bytes = asm.Build();
        // INC word [0100] => FF 06 00 01
        // DEC word [0100] => FF 0E 00 01
        Assert.Equal(new byte[] { 0xFF, 0x06, 0x00, 0x01, 0xFF, 0x0E, 0x00, 0x01 }, bytes);
    }

    [Fact]
    public void ShiftByOneAndByCl()
    {
        var asm = new X86_16Assembler(0);
        asm.EmitShl(Operand.Reg(X86_16Register.AX), 1);
        asm.EmitShrCl(Operand.Reg(X86_16Register.AX));
        var bytes = asm.Build();
        // SHL AX, 1 => D1 E0
        // SHR AX, CL => D3 E8
        Assert.Equal(new byte[] { 0xD1, 0xE0, 0xD3, 0xE8 }, bytes);
    }

    [Fact]
    public void ShiftByImmediate286()
    {
        var asm = new X86_16Assembler(0);
        asm.EmitSar(Operand.Reg(X86_16Register.BX), 4);
        var bytes = asm.Build();
        // SAR BX, 4 => C1 FB 04
        Assert.Equal(new byte[] { 0xC1, 0xFB, 0x04 }, bytes);
    }

    [Fact]
    public void XchgAxOptimized()
    {
        var asm = new X86_16Assembler(0);
        asm.EmitXchg(X86_16Register.AX, X86_16Register.BX);
        var bytes = asm.Build();
        Assert.Equal(new byte[] { 0x93 }, bytes);
    }

    [Fact]
    public void XchgRegRegNonAx()
    {
        var asm = new X86_16Assembler(0);
        asm.EmitXchg(X86_16Register.CX, X86_16Register.DX);
        var bytes = asm.Build();
        // XCHG CX, DX => 87 CA
        Assert.Equal(new byte[] { 0x87, 0xCA }, bytes);
    }

    [Fact]
    public void CallAndJmp()
    {
        var asm = new X86_16Assembler(0x7C00);
        asm.DefineLabel("target");
        asm.EmitNop();
        asm.EmitCall("target");
        asm.EmitJmpShort("target");
        asm.EmitJmpNear("target");
        var bytes = asm.Build();

        // target is at offset 0 (label defined before NOP).
        // CALL is at offset 1, 3 bytes long, so rel16 to offset 0 = -4 = FFFC.
        Assert.Equal(0xE8, bytes[1]);
        Assert.Equal(0xFC, bytes[2]);
        Assert.Equal(0xFF, bytes[3]);

        // JMP SHORT is at offset 4, 2 bytes long, so rel8 to offset 0 = -6 = FA.
        Assert.Equal(0xEB, bytes[4]);
        Assert.Equal(0xFA, bytes[5]);

        // JMP NEAR is at offset 6, 3 bytes long, so rel16 to offset 0 = -9 = FFF7.
        Assert.Equal(0xE9, bytes[6]);
        Assert.Equal(0xF7, bytes[7]);
        Assert.Equal(0xFF, bytes[8]);
    }

    [Fact]
    public void ConditionalJumpShort()
    {
        var asm = new X86_16Assembler(0);
        asm.DefineLabel("a");
        asm.EmitNop();
        asm.EmitJeShort("a");
        asm.EmitJneShort("a");
        asm.EmitJcShort("a");
        asm.EmitJncShort("a");
        asm.EmitJaShort("a");
        asm.EmitJlShort("a");
        var bytes = asm.Build();

        // a is at offset 0; each jump returns to the NOP, so displacements are -3, -5, -7, ...
        Assert.Equal(new byte[] { 0x90, 0x74, 0xFD, 0x75, 0xFB, 0x72, 0xF9, 0x73, 0xF7, 0x77, 0xF5, 0x7C, 0xF3 }, bytes);
    }

    [Fact]
    public void AbsoluteLabelFixup()
    {
        var asm = new X86_16Assembler(0x7E00);
        asm.EmitMovRegImm16(X86_16Register.SI, "data");
        asm.DefineLabel("data");
        asm.EmitDataString("hi");
        var bytes = asm.Build();

        // MOV SI, data => BE 00 7E (label at offset 3, base 7E00 => 7E03)
        Assert.Equal(0xBE, bytes[0]);
        Assert.Equal(0x03, bytes[1]);
        Assert.Equal(0x7E, bytes[2]);
    }

    [Fact]
    public void FarJumpEncoding()
    {
        var asm = new X86_16Assembler(0);
        asm.EmitJmpAbsoluteFar(0x1234, 0x5678);
        var bytes = asm.Build();
        Assert.Equal(new byte[] { 0xEA, 0x78, 0x56, 0x34, 0x12 }, bytes);
    }

    [Fact]
    public void SegmentOverrides()
    {
        var asm = new X86_16Assembler(0);
        asm.EmitSegmentOverride(X86_16SegmentRegister.ES);
        asm.EmitSegmentOverride(X86_16SegmentRegister.CS);
        asm.EmitSegmentOverride(X86_16SegmentRegister.SS);
        asm.EmitSegmentOverride(X86_16SegmentRegister.DS);
        var bytes = asm.Build();
        Assert.Equal(new byte[] { 0x26, 0x2E, 0x36, 0x3E }, bytes);
    }

    [Fact]
    public void InOutEncoding()
    {
        var asm = new X86_16Assembler(0);
        asm.EmitInAl(0x60);
        asm.EmitInAx(0x60);
        asm.EmitOut(0x60, X86_16Register8.AL);
        asm.EmitOut(0x60, X86_16Register.AX);
        var bytes = asm.Build();
        Assert.Equal(new byte[] { 0xE4, 0x60, 0xE5, 0x60, 0xE6, 0x60, 0xE7, 0x60 }, bytes);
    }

    [Fact]
    public void DataDirectives()
    {
        var asm = new X86_16Assembler(0);
        asm.EmitDataByte(0x12);
        asm.EmitDataWord(0x3456);
        asm.EmitDataDword(0x789ABCDE);
        asm.EmitDataString("A");
        asm.ReserveBytes(2);
        var bytes = asm.Build();
        Assert.Equal(new byte[]
        {
            0x12, 0x56, 0x34, 0xDE, 0xBC, 0x9A, 0x78, 0x41, 0x00, 0x00, 0x00
        }, bytes);
    }

    [Fact]
    public void LeaEncoding()
    {
        var asm = new X86_16Assembler(0);
        asm.EmitLea(X86_16Register.BX, Operand.Memory(X86_16Register.BP, 0x10));
        var bytes = asm.Build();
        // LEA BX, [BP+10] => 8D 5E 10
        Assert.Equal(new byte[] { 0x8D, 0x5E, 0x10 }, bytes);
    }

    [Fact]
    public void TestRegReg()
    {
        var asm = new X86_16Assembler(0);
        asm.EmitTest(Operand.Reg(X86_16Register8.AL), Operand.Reg(X86_16Register8.BL));
        var bytes = asm.Build();
        // TEST AL, BL => 84 C3
        Assert.Equal(new byte[] { 0x84, 0xC3 }, bytes);
    }

    [Fact]
    public void MulDivEncoding()
    {
        var asm = new X86_16Assembler(0);
        asm.EmitMul(Operand.Reg(X86_16Register.AX));
        asm.EmitDiv(Operand.Reg(X86_16Register.BX));
        asm.EmitImul(Operand.Reg(X86_16Register8.AL));
        var bytes = asm.Build();
        // MUL AX => F7 E0
        // DIV BX => F7 F3
        // IMUL AL => F6 E8
        Assert.Equal(new byte[] { 0xF7, 0xE0, 0xF7, 0xF3, 0xF6, 0xE8 }, bytes);
    }

    [Fact]
    public void OneByteFlagAndBcdInstructions()
    {
        var asm = new X86_16Assembler(0);
        asm.EmitPushf();
        asm.EmitPopf();
        asm.EmitLahf();
        asm.EmitSahf();
        asm.EmitXlatb();
        asm.EmitDaa();
        asm.EmitDas();
        asm.EmitAaa();
        asm.EmitAas();
        asm.EmitAam(10);
        asm.EmitAad(10);
        var bytes = asm.Build();
        Assert.Equal(new byte[] { 0x9C, 0x9D, 0x9F, 0x9E, 0xD7, 0x27, 0x2F, 0x37, 0x3F, 0xD4, 0x0A, 0xD5, 0x0A }, bytes);
    }

    [Fact]
    public void RepPrefix()
    {
        var asm = new X86_16Assembler(0);
        asm.EmitRep();
        asm.EmitMovsb();
        var bytes = asm.Build();
        Assert.Equal(new byte[] { 0xF3, 0xA4 }, bytes);
    }

    [Fact]
    public void InOutWithDx()
    {
        var asm = new X86_16Assembler(0);
        asm.EmitInAlDx();
        asm.EmitInAxDx();
        asm.EmitOutDxAl();
        asm.EmitOutDxAx();
        var bytes = asm.Build();
        Assert.Equal(new byte[] { 0xEC, 0xED, 0xEE, 0xEF }, bytes);
    }

    [Fact]
    public void SegmentMov()
    {
        var asm = new X86_16Assembler(0);
        asm.EmitMov(X86_16Register.AX, X86_16SegmentRegister.DS);
        asm.EmitMov(X86_16SegmentRegister.ES, X86_16Register.BX);
        var bytes = asm.Build();
        // MOV AX, DS => 8C D8
        // MOV ES, BX => 8E C3
        Assert.Equal(new byte[] { 0x8C, 0xD8, 0x8E, 0xC3 }, bytes);
    }

    [Fact]
    public void LdsLesEncoding()
    {
        var asm = new X86_16Assembler(0);
        asm.EmitLds(X86_16Register.SI, Operand.Memory(0x300));
        asm.EmitLes(X86_16Register.DI, Operand.Memory(0x300));
        var bytes = asm.Build();
        // LDS SI, [0300] => C5 36 00 03
        // LES DI, [0300] => C4 3E 00 03
        Assert.Equal(new byte[] { 0xC5, 0x36, 0x00, 0x03, 0xC4, 0x3E, 0x00, 0x03 }, bytes);
    }

    [Fact]
    public void ConditionalNearJumpEncoding()
    {
        var asm = new X86_16Assembler(0);
        asm.DefineLabel("target");
        asm.EmitJccNear(X86_16ConditionCode.Equal, "target");
        var bytes = asm.Build();
        Assert.Equal(new byte[] { 0x0F, 0x84, 0xFC, 0xFF }, bytes);
    }
}
