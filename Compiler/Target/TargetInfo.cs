using System;
using System.Runtime.InteropServices;

namespace ZV.Compiler.Target;

public sealed record TargetInfo(
    TargetArchitecture Architecture,
    TargetEnvironment Environment,
    TargetAbi Abi,
    OutputFormat OutputFormat,
    BootMethod BootMethod)
{
    public bool IsHosted => Environment == TargetEnvironment.Hosted;
    public bool IsBareMetal => Environment == TargetEnvironment.BareMetal;

    public DataLayout DataLayout => DataLayout.ForTarget(this);

    public string ShortName => $"{Architecture}-{Environment}".ToLowerInvariant();

    public string LlvmTriple => (Architecture, Environment) switch
    {
        (TargetArchitecture.X86_32, TargetEnvironment.Hosted) => OperatingSystemTriple("i686"),
        (TargetArchitecture.X86_32, TargetEnvironment.BareMetal) => "i686-unknown-none-elf",
        (TargetArchitecture.Amd64, TargetEnvironment.Hosted) => OperatingSystemTriple("x86_64"),
        (TargetArchitecture.Amd64, TargetEnvironment.BareMetal) => "x86_64-unknown-none-elf",
        _ => throw new NotSupportedException($"Target '{Architecture}/{Environment}' is not supported by the LLVM backend.")
    };

    public string LlvmDataLayout => Architecture switch
    {
        TargetArchitecture.X86_32 => "e-m:e-p:32:32-p270:32:32-p271:32:32-p272:64:64-i128:128-f64:32:64-f80:32-n8:16:32-S128",
        TargetArchitecture.Amd64 => RuntimeInformation.IsOSPlatform(System.Runtime.InteropServices.OSPlatform.Windows)
            ? "e-m:w-p270:32:32-p271:32:32-p272:64:64-i64:64-i128:128-f80:128-n8:16:32:64-S128"
            : "e-m:e-p270:32:32-p271:32:32-p272:64:64-i64:64-i128:128-f80:128-n8:16:32:64-S128",
        _ => throw new NotSupportedException($"No LLVM data layout for architecture '{Architecture}'.")
    };

    private static string OperatingSystemTriple(string arch) => OperatingSystem.IsWindows()
        ? $"{arch}-pc-windows-msvc"
        : OperatingSystem.IsLinux()
            ? $"{arch}-pc-linux-gnu"
            : OperatingSystem.IsMacOS()
                ? $"{arch}-apple-darwin"
                : throw new NotSupportedException("Unsupported host OS for hosted LLVM target.");
}
