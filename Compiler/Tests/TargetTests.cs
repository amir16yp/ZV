using System;
using Xunit;
using ZV.Compiler.Target;

namespace ZV.Compiler.Tests;

public class TargetTests
{
    [Theory]
    [InlineData("x86-16", TargetArchitecture.X86_16, TargetEnvironment.BareMetal, OutputFormat.RawImage, BootMethod.MbrRawBoot16)]
    [InlineData("x86-16-baremetal", TargetArchitecture.X86_16, TargetEnvironment.BareMetal, OutputFormat.RawImage, BootMethod.MbrRawBoot16)]
    [InlineData("x86-32-hosted", TargetArchitecture.X86_32, TargetEnvironment.Hosted, OutputFormat.Executable, BootMethod.None)]
    [InlineData("x86-32-baremetal", TargetArchitecture.X86_32, TargetEnvironment.BareMetal, OutputFormat.RawImage, BootMethod.None)]
    [InlineData("x86-32-baremetal-multiboot", TargetArchitecture.X86_32, TargetEnvironment.BareMetal, OutputFormat.Elf, BootMethod.Multiboot1)]
    [InlineData("amd64-hosted", TargetArchitecture.Amd64, TargetEnvironment.Hosted, OutputFormat.Executable, BootMethod.None)]
    [InlineData("amd64-baremetal-multiboot2", TargetArchitecture.Amd64, TargetEnvironment.BareMetal, OutputFormat.Elf, BootMethod.Multiboot2)]
    [InlineData("x86-32-baremetal-elf", TargetArchitecture.X86_32, TargetEnvironment.BareMetal, OutputFormat.Elf, BootMethod.None)]
    public void CanParseTargets(string text, TargetArchitecture arch, TargetEnvironment env, OutputFormat output, BootMethod boot)
    {
        var target = TargetParser.Parse(text);
        Assert.Equal(arch, target.Architecture);
        Assert.Equal(env, target.Environment);
        Assert.Equal(output, target.OutputFormat);
        Assert.Equal(boot, target.BootMethod);
    }

    [Theory]
    [InlineData("x86-16-hosted")]
    [InlineData("amd64-baremetal-exe")]
    [InlineData("unknown-hosted")]
    public void InvalidTargetsAreRejected(string text)
    {
        Assert.Throws<ArgumentException>(() => TargetParser.Parse(text));
    }

    [Theory]
    [InlineData(TargetArchitecture.X86_16, 16, 16, 16)]
    [InlineData(TargetArchitecture.X86_32, 32, 32, 32)]
    [InlineData(TargetArchitecture.Amd64, 64, 64, 64)]
    public void DataLayoutMatchesArchitecture(TargetArchitecture arch, int pointerBits, int sizeTypeBits, int funcPtrBits)
    {
        var target = new TargetInfo(arch, TargetEnvironment.BareMetal, TargetAbi.BareMetalX86_16, OutputFormat.RawImage, BootMethod.MbrRawBoot16);
        var layout = target.DataLayout;
        Assert.Equal(pointerBits, layout.PointerSizeBits);
        Assert.Equal(sizeTypeBits, layout.SizeTypeBits);
        Assert.Equal(funcPtrBits, layout.FunctionPointerSizeBits);

        // Fixed-width types are the same width on every target.
        Assert.Equal(8, layout.GetSizeBits(ZV.Compiler.Lexer.TokenType.INT8));
        Assert.Equal(128, layout.GetSizeBits(ZV.Compiler.Lexer.TokenType.INT128));
    }

    [Fact]
    public void FixedWidthTypesAreIndependentOfTarget()
    {
        var x86_16 = DataLayout.ForTarget(TargetParser.Parse("x86-16"));
        var amd64 = DataLayout.ForTarget(TargetParser.Parse("amd64-hosted"));

        Assert.Equal(x86_16.GetSizeBits(ZV.Compiler.Lexer.TokenType.INT32), amd64.GetSizeBits(ZV.Compiler.Lexer.TokenType.INT32));
        Assert.Equal(32, x86_16.GetSizeBits(ZV.Compiler.Lexer.TokenType.INT32));
    }

    [Fact]
    public void PointerSizeVariesByTarget()
    {
        var x86_16 = DataLayout.ForTarget(TargetParser.Parse("x86-16"));
        var x86_32 = DataLayout.ForTarget(TargetParser.Parse("x86-32-hosted"));
        var amd64 = DataLayout.ForTarget(TargetParser.Parse("amd64-hosted"));

        Assert.Equal(16, x86_16.PointerSizeBits);
        Assert.Equal(32, x86_32.PointerSizeBits);
        Assert.Equal(64, amd64.PointerSizeBits);
    }
}
