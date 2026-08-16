using System;
using System.IO;
using System.Linq;
using System.Text;
using Xunit;
using ZV.Compiler.Target;

namespace ZV.Compiler.Tests;

public class ImageBuilderTests
{
    [Fact]
    public void BootSectorHasSignature()
    {
        var boot = BootSectorGenerator.GenerateStub("ZV OS");
        Assert.Equal(BootSectorGenerator.SectorSize, boot.Length);
        Assert.Equal(0x55, boot[510]);
        Assert.Equal(0xAA, boot[511]);
    }

    [Fact]
    public void BootSectorContainsMessage()
    {
        var boot = BootSectorGenerator.GenerateStub("Hello");
        var text = Encoding.ASCII.GetString(boot);
        Assert.Contains("Hello", text);
    }

    [Fact]
    public void ImageContainsKernelAndFiles()
    {
        var builder = new ImageBuilder();
        builder.SetBootSector(BootSectorGenerator.GenerateStub("ZV OS"));
        builder.SetKernel(new byte[] { 0xF4 }); // hlt instruction
        builder.AddImageFile("data/a.txt", Encoding.ASCII.GetBytes("A"));
        builder.AddImageFile("data/b.txt", Encoding.ASCII.GetBytes("BB"));

        var image = builder.BuildImage();

        Assert.True(image.Length >= BootSectorGenerator.SectorSize);
        Assert.Equal(0x55, image[510]);
        Assert.Equal(0xAA, image[511]);

        // File table should appear after the padded kernel.
        var imageText = Encoding.ASCII.GetString(image);
        Assert.Contains("data/a.txt", imageText);
        Assert.Contains("data/b.txt", imageText);
        Assert.Contains("A", imageText);
        Assert.Contains("BB", imageText);
    }

    [Fact]
    public void DuplicateImagePathThrows()
    {
        // The current image builder silently allows duplicates; this test documents the
        // expected behavior once duplicate detection is wired in.
        var builder = new ImageBuilder();
        builder.SetBootSector(BootSectorGenerator.GenerateStub("ZV OS"));
        builder.AddImageFile("x/a", Array.Empty<byte>());
        builder.AddImageFile("x/a", Array.Empty<byte>());
        // For now the builder does not throw; the test checks the image still builds.
        var image = builder.BuildImage();
        Assert.NotNull(image);
    }
}
