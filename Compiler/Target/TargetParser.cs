using System;
using System.Linq;

namespace ZV.Compiler.Target;

public static class TargetParser
{
    public static TargetInfo Parse(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            throw new ArgumentException("Target string is empty.", nameof(text));

        var parts = text.Split('-', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
            throw new ArgumentException($"Invalid target '{text}'.", nameof(text));

        // The architecture name may itself contain a hyphen (e.g. "x86-16" or "x86-32"),
        // so consume as many leading parts as necessary to form a known architecture.
        // Prefer the longest match so "x86-32-hosted" is parsed as x86-32, not x86 + "32".
        int consumed = 0;
        TargetArchitecture? parsedArchitecture = null;
        for (int i = 1; i <= parts.Length; i++)
        {
            var candidate = TryParseArchitecture(string.Join("-", parts.Take(i)));
            if (candidate != null)
            {
                parsedArchitecture = candidate;
                consumed = i;
            }
        }
        if (parsedArchitecture == null)
            throw new ArgumentException($"Unknown architecture in target '{text}'.", nameof(text));
        TargetArchitecture architecture = parsedArchitecture.Value;

        // Defaults derived from architecture.
        var environment = architecture == TargetArchitecture.X86_16
            ? TargetEnvironment.BareMetal
            : TargetEnvironment.Hosted;
        var outputFormat = environment == TargetEnvironment.BareMetal
            ? OutputFormat.RawImage
            : OutputFormat.Executable;
        var bootMethod = environment == TargetEnvironment.BareMetal && architecture == TargetArchitecture.X86_16
            ? BootMethod.MbrRawBoot16
            : BootMethod.None;
        var abi = InferDefaultAbi(architecture, environment);
        bool abiExplicit = false;

        for (int i = consumed; i < parts.Length; i++)
        {
            var part = parts[i].ToLowerInvariant();
            switch (part)
            {
                case "hosted":
                    environment = TargetEnvironment.Hosted;
                    if (outputFormat == OutputFormat.RawImage)
                        outputFormat = OutputFormat.Executable;
                    bootMethod = BootMethod.None;
                    break;
                case "baremetal":
                case "bare":
                case "bare-metal":
                    environment = TargetEnvironment.BareMetal;
                    if (outputFormat == OutputFormat.Executable || outputFormat == OutputFormat.SharedLibrary)
                        outputFormat = OutputFormat.RawImage;
                    if (architecture == TargetArchitecture.X86_16)
                        bootMethod = BootMethod.MbrRawBoot16;
                    break;
                case "raw":
                case "rawimage":
                case "img":
                    outputFormat = OutputFormat.RawImage;
                    break;
                case "exe":
                case "executable":
                    outputFormat = OutputFormat.Executable;
                    break;
                case "lib":
                case "library":
                case "dll":
                case "so":
                    outputFormat = OutputFormat.SharedLibrary;
                    break;
                case "elf":
                    outputFormat = OutputFormat.Elf;
                    break;
                case "mbr":
                    bootMethod = BootMethod.MbrRawBoot16;
                    break;
                case "multiboot":
                    bootMethod = BootMethod.Multiboot1;
                    if (outputFormat == OutputFormat.RawImage)
                        outputFormat = OutputFormat.Elf;
                    break;
                case "multiboot2":
                    bootMethod = BootMethod.Multiboot2;
                    if (outputFormat == OutputFormat.RawImage)
                        outputFormat = OutputFormat.Elf;
                    break;
                case "cdecl":
                    abi = TargetAbi.Cdecl;
                    abiExplicit = true;
                    break;
                case "sysv":
                    abi = TargetAbi.SysV;
                    abiExplicit = true;
                    break;
                case "ms":
                case "microsoft":
                case "win64":
                    abi = TargetAbi.MicrosoftX64;
                    abiExplicit = true;
                    break;
                case "bare16":
                    abi = TargetAbi.BareMetalX86_16;
                    abiExplicit = true;
                    break;
                case "bare32":
                    abi = TargetAbi.BareMetalX86_32;
                    abiExplicit = true;
                    break;
                case "bare64":
                    abi = TargetAbi.BareMetalAmd64;
                    abiExplicit = true;
                    break;
                default:
                    throw new ArgumentException($"Unknown target component '{parts[i]}' in '{text}'.", nameof(text));
            }
        }

        if (!abiExplicit)
        {
            abi = InferDefaultAbi(architecture, environment);
        }

        ValidateCombination(architecture, environment, abi, outputFormat, bootMethod);

        return new TargetInfo(architecture, environment, abi, outputFormat, bootMethod);
    }

    private static TargetArchitecture? TryParseArchitecture(string text)
    {
        return text.ToLowerInvariant() switch
        {
            "x86-16" or "x86_16" or "i8086" or "8086" or "i86" => TargetArchitecture.X86_16,
            "x86-32" or "x86_32" or "i686" or "i386" or "x86" => TargetArchitecture.X86_32,
            "amd64" or "x86-64" or "x86_64" or "x64" => TargetArchitecture.Amd64,
            _ => null
        };
    }

    private static TargetArchitecture ParseArchitecture(string text)
    {
        return TryParseArchitecture(text) ?? throw new ArgumentException($"Unknown architecture '{text}'.", nameof(text));
    }

    private static TargetAbi InferDefaultAbi(TargetArchitecture arch, TargetEnvironment env) => (arch, env) switch
    {
        (TargetArchitecture.X86_16, TargetEnvironment.BareMetal) => TargetAbi.BareMetalX86_16,
        (TargetArchitecture.X86_32, TargetEnvironment.BareMetal) => TargetAbi.BareMetalX86_32,
        (TargetArchitecture.Amd64, TargetEnvironment.BareMetal) => TargetAbi.BareMetalAmd64,
        (TargetArchitecture.X86_32, TargetEnvironment.Hosted) => TargetAbi.Cdecl,
        (TargetArchitecture.Amd64, TargetEnvironment.Hosted) => OperatingSystem.IsWindows()
            ? TargetAbi.MicrosoftX64
            : TargetAbi.SysV,
        (TargetArchitecture.X86_16, TargetEnvironment.Hosted) =>
            throw new ArgumentException("x86-16 is only supported as a bare-metal target."),
        _ => throw new ArgumentException($"Unsupported target combination {arch}/{env}.")
    };

    private static void ValidateCombination(
        TargetArchitecture arch,
        TargetEnvironment env,
        TargetAbi abi,
        OutputFormat output,
        BootMethod boot)
    {
        if (env == TargetEnvironment.Hosted && boot != BootMethod.None)
            throw new ArgumentException($"Boot method '{boot}' is only valid for bare-metal targets.");

        if (env == TargetEnvironment.BareMetal && output != OutputFormat.RawImage && output != OutputFormat.Elf)
            throw new ArgumentException($"Bare-metal target must produce a raw image or ELF output, not '{output}'.");

        if (arch == TargetArchitecture.X86_16 && env == TargetEnvironment.Hosted)
            throw new ArgumentException("x86-16 is only supported as a bare-metal target.");

        if (boot == BootMethod.MbrRawBoot16 && arch != TargetArchitecture.X86_16)
            throw new ArgumentException("Raw MBR boot is only supported for x86-16.");
        if (boot == BootMethod.Multiboot1 && arch != TargetArchitecture.X86_32)
            throw new ArgumentException("Multiboot v1 is only supported for x86-32.");
        if (boot == BootMethod.Multiboot2 && arch != TargetArchitecture.Amd64)
            throw new ArgumentException("Multiboot v2 is only supported for AMD64.");

        var expectedAbi = env == TargetEnvironment.BareMetal
            ? arch switch
            {
                TargetArchitecture.X86_16 => TargetAbi.BareMetalX86_16,
                TargetArchitecture.X86_32 => TargetAbi.BareMetalX86_32,
                TargetArchitecture.Amd64 => TargetAbi.BareMetalAmd64,
                _ => abi
            }
            : arch switch
            {
                TargetArchitecture.X86_32 => TargetAbi.Cdecl,
                TargetArchitecture.Amd64 => OperatingSystem.IsWindows() ? TargetAbi.MicrosoftX64 : TargetAbi.SysV,
                _ => abi
            };

        if (abi != expectedAbi)
        {
            // Allow the ABI to be consistent; if it is not, this is only a warning-level concern
            // for future targets. For now reject mismatches to keep diagnostics clear.
            throw new ArgumentException($"ABI '{abi}' is not valid for {arch}/{env}. Expected '{expectedAbi}'.");
        }
    }
}
