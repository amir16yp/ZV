using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ZV.Compiler.AST;

namespace ZV.Compiler.Target;

/// <summary>
/// Entry point for the x86-16 bare-metal target. Compiles the ZV AST directly to 16-bit
/// real-mode machine code, packages it with a boot loader and any embedded image files,
/// and writes a raw bootable disk image.
/// </summary>
public sealed class X86_16BareMetalPipeline
{
    private readonly TargetInfo _target;
    private readonly bool _verbose;

    public X86_16BareMetalPipeline(TargetInfo target, bool verbose)
    {
        if (target.Architecture != TargetArchitecture.X86_16)
            throw new ArgumentException("x86-16 pipeline requires an x86-16 target.", nameof(target));
        _target = target;
        _verbose = verbose;
    }

    public void Build(List<Statement> statements, List<EmbedInfo> embeds, string outputImagePath)
    {
        var kernel = new X86_16Backend(statements).Compile();
        int kernelSectors = (kernel.Length + ImageBuilder.SectorSize - 1) / ImageBuilder.SectorSize;

        if (_verbose)
        {
            Console.WriteLine($"[verbose] Kernel size: {kernel.Length} bytes ({kernelSectors} sectors).");
        }

        var builder = new ImageBuilder();
        builder.SetBootSector(BootSectorGenerator.GenerateLoader(kernelSectors));
        builder.SetKernel(kernel);

        foreach (var embed in embeds)
        {
            byte[] data = File.ReadAllBytes(embed.SourcePath);
            if (embed.Kind == EmbedKind.File)
            {
                builder.AddImageFile(embed.DestinationPath ?? Path.GetFileName(embed.SourcePath), data);
            }
            else if (embed.Kind == EmbedKind.Resource)
            {
                // Resources are currently appended to the kernel binary. Runtime access to
                // resources via a stable handle will be added once the backend supports it.
                var extended = new List<byte>(kernel);
                extended.AddRange(data);
                kernel = extended.ToArray();
                kernelSectors = (kernel.Length + ImageBuilder.SectorSize - 1) / ImageBuilder.SectorSize;
                builder.SetKernel(kernel);
                // The boot sector was already generated with the smaller count; rebuild it.
                builder.SetBootSector(BootSectorGenerator.GenerateLoader(kernelSectors));
            }
        }

        byte[] image = builder.BuildImage();
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputImagePath)) ?? "");
        File.WriteAllBytes(outputImagePath, image);

        if (_verbose)
        {
            Console.WriteLine($"[verbose] Wrote {image.Length} byte raw image to '{outputImagePath}'.");
        }

        EmitIntermediateArtifacts(outputImagePath, kernel);
    }

    private void EmitIntermediateArtifacts(string outputImagePath, byte[] kernel)
    {
        string? dir = Path.GetDirectoryName(Path.GetFullPath(outputImagePath));
        if (string.IsNullOrEmpty(dir)) return;

        string kernelBin = Path.Combine(dir, Path.GetFileNameWithoutExtension(outputImagePath) + ".kernel.bin");
        File.WriteAllBytes(kernelBin, kernel);
    }
}
