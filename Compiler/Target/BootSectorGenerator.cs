using System;
using System.Text;

namespace ZV.Compiler.Target;

/// <summary>
/// Generates an x86-16 real-mode boot sector. The default stub prints a message and hangs;
/// <see cref="GenerateLoader"/> produces a boot sector that reads the kernel from the
/// disk image and jumps to it.
/// </summary>
public static class BootSectorGenerator
{
    public const int SectorSize = 512;
    public const ushort BootSignature = 0xAA55;
    public const ushort KernelLoadAddress = 0x7E00;

    /// <summary>
    /// Builds a simple boot sector that prints <paramref name="message"/> and hangs.
    /// </summary>
    public static byte[] GenerateStub(string message)
    {
        var boot = new X86_16Assembler(0x7C00);

        boot.DefineLabel("start");
        boot.EmitJmpShort("setup");

        boot.DefineLabel("msg");
        boot.EmitDataString(message);

        boot.DefineLabel("setup");
        boot.EmitByte(0xB8); boot.EmitByte(0x00); boot.EmitByte(0x00); // mov ax, 0
        boot.EmitByte(0x8E); boot.EmitByte(0xD8);                      // mov ds, ax
        boot.EmitMovRegImm16(X86_16Register.SI, "msg");
        boot.EmitCall("print");
        boot.DefineLabel("hang");
        boot.EmitHlt();
        boot.EmitJmpShort("hang");

        boot.DefineLabel("print");
        boot.EmitLodsb();
        boot.EmitOrAlAl();
        boot.EmitJeShort("print_done");
        boot.EmitMovAhImm8(0x0E);
        boot.EmitInt(0x10);
        boot.EmitJmpShort("print");
        boot.DefineLabel("print_done");
        boot.EmitRet();

        return Finalize(boot.Build());
    }

    /// <summary>
    /// Builds a boot sector that loads <paramref name="kernelSectorCount"/> sectors
    /// starting at LBA 1 (sector 2) to <see cref="KernelLoadAddress"/> and jumps there.
    /// The first byte of the boot sector is patched with the actual kernel sector count.
    /// </summary>
    public static byte[] GenerateLoader(int kernelSectorCount)
    {
        if (kernelSectorCount <= 0 || kernelSectorCount > 127)
            throw new ArgumentOutOfRangeException(nameof(kernelSectorCount), "Kernel must fit in 1..127 sectors for the initial boot loader.");

        var boot = new X86_16Assembler(0x7C00);

        // The first instruction is patched by the image builder to store the sector count
        // as an immediate word. We reserve space for "mov cx, <count>" (3 bytes) then jump
        // over the data.
        boot.DefineLabel("start");
        // placeholder: mov cx, 0  (B9 00 00)
        boot.EmitByte(0xB9);
        boot.EmitUshortPlaceholder();
        boot.EmitJmpShort("setup");

        boot.DefineLabel("loading_msg");
        boot.EmitDataString("Loading ZV...");
        boot.DefineLabel("error_msg");
        boot.EmitDataString("Disk error");

        boot.DefineLabel("setup");
        boot.EmitCli();
        boot.EmitByte(0x31); boot.EmitByte(0xC0); // xor ax, ax
        boot.EmitByte(0x8E); boot.EmitByte(0xD8); // mov ds, ax
        boot.EmitByte(0x8E); boot.EmitByte(0xC0); // mov es, ax
        boot.EmitByte(0x8E); boot.EmitByte(0xD0); // mov ss, ax
        boot.EmitMovRegImm16(X86_16Register.SP, 0x7C00);
        boot.EmitSti();

        boot.EmitMovRegImm16(X86_16Register.SI, "loading_msg");
        boot.EmitCall("print");

        // Read kernel sectors: AH=2, AL=count, CH=0, CL=2, DH=0, DL=original drive.
        boot.EmitMovRegImm16(X86_16Register.AX, (ushort)(0x0200 | kernelSectorCount));
        boot.EmitMovRegImm16(X86_16Register.BX, KernelLoadAddress);
        boot.EmitMovRegImm16(X86_16Register.CX, 0x0002); // cylinder 0, sector 2
        boot.EmitByte(0x30); boot.EmitByte(0xF6);        // xor dh, dh
        boot.EmitInt(0x13);
        boot.EmitJcShort("disk_error");

        // Jump to the kernel with a far jump to 0x0000:0x7E00.
        boot.EmitJmpAbsoluteFar(0x0000, KernelLoadAddress);

        boot.DefineLabel("disk_error");
        boot.EmitMovRegImm16(X86_16Register.SI, "error_msg");
        boot.EmitCall("print");
        boot.DefineLabel("error_hang");
        boot.EmitHlt();
        boot.EmitJmpShort("error_hang");

        boot.DefineLabel("print");
        boot.EmitLodsb();
        boot.EmitOrAlAl();
        boot.EmitJeShort("print_done");
        boot.EmitMovAhImm8(0x0E);
        boot.EmitInt(0x10);
        boot.EmitJmpShort("print");
        boot.DefineLabel("print_done");
        boot.EmitRet();

        var bytes = boot.Build();
        PatchSectorCount(ref bytes, kernelSectorCount);
        return Finalize(bytes);
    }

    private static void PatchSectorCount(ref byte[] bytes, int count)
    {
        // The loader starts with "mov cx, <count>" at offset 0: B9 lo hi.
        bytes[1] = (byte)(count & 0xFF);
        bytes[2] = (byte)((count >> 8) & 0xFF);
    }

    private static void EmitUshortPlaceholder(this X86_16Assembler asm)
    {
        asm.EmitByte(0);
        asm.EmitByte(0);
    }

    private static byte[] Finalize(byte[] code)
    {
        if (code.Length > SectorSize - 2)
            throw new InvalidOperationException($"Boot sector overflow: {code.Length} bytes.");

        var result = new byte[SectorSize];
        Buffer.BlockCopy(code, 0, result, 0, code.Length);
        result[SectorSize - 2] = (byte)(BootSignature & 0xFF);
        result[SectorSize - 1] = (byte)((BootSignature >> 8) & 0xFF);
        return result;
    }
}
