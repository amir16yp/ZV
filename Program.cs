using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using ZV.Compiler.Lexer;
using ZV.Compiler.Parser;
using ZV.Compiler.Backend;
using ZV.Compiler.AST;
using ZV.Compiler.Target;

namespace ZV;

public class Program
{
    // Set by -v/--verbose. When true, LogVerbose() prints a play-by-play of each compiler
    // stage (lexing/parsing per file, codegen, optimization passes, emission, linking) with
    // timing, in addition to the plain status lines that are always printed.
    private static bool Verbose = false;

    // Optimization levels accepted by -copt, passed straight through to clang as -O<level>
    // (see CompileWithClang). "2" is clang's own default for typical release builds.
    private static readonly string[] ClangOptLevels = { "0", "1", "2", "3", "s", "z" };
    private const string DefaultClangOptLevel = "2";

    private static void PrintClangOptLevels()
    {
        Console.WriteLine("Available -copt levels (passed to clang as -O<level>):");
        foreach (var level in ClangOptLevels)
        {
            string marker = level == DefaultClangOptLevel ? " (default)" : "";
            Console.WriteLine($"  O{level}{marker}");
        }
    }

    private static void LogVerbose(string message)
    {
        if (Verbose)
        {
            Console.WriteLine($"[verbose] {message}");
        }
    }

    // Prints non-fatal compiler diagnostics (see LlvmGenerator.Warnings): things worth the
    // programmer's attention (unreachable code, a non-void function that can fall off its
    // end, ...) that don't block compilation the way a CompileException does.
    private static void PrintWarnings(IEnumerable<(ZV.Compiler.Lexer.SourceLocation? Location, string Message)> warnings)
    {
        foreach (var (location, message) in warnings)
        {
            string prefix = location != null ? $"[{location.File}:{location.Line}:{location.Column}] " : "";
            Console.WriteLine($"warning: {prefix}{message}");
        }
    }

    public static async System.Threading.Tasks.Task Main(string[] args)
    {
        if (args.Length == 0)
        {
            Console.WriteLine("Usage: ZV <file or directory> [-o output] [-target <target>] [-L libdir]... [-run] [-O|--optimize] [-copt O0|O1|O2|O3|Os|Oz|list] [-v|--verbose]");
            Console.WriteLine("  <target> examples: x86-16-baremetal, x86-32-hosted, x86-32-baremetal, amd64-hosted");
            Console.WriteLine("       ZV checkdeps");
            Console.WriteLine("       ZV --lsp");
            return;
        }

        if (string.Equals(args[0], "checkdeps", StringComparison.OrdinalIgnoreCase))
        {
            RunCheckDeps();
            return;
        }

        if (string.Equals(args[0], "--lsp", StringComparison.OrdinalIgnoreCase))
        {
            var server = new ZV.Compiler.LanguageServer.LanguageServer(Console.OpenStandardInput(), Console.OpenStandardOutput());
            await server.RunAsync();
            return;
        }

        string inputPath = args[0];
        string? outputPath = null;
        string? targetText = null;
        bool runAfterBuild = false;
        bool optimize = false;
        string clangOptLevel = DefaultClangOptLevel;
        List<string> libSearchDirs = new List<string>();

        for (int i = 1; i < args.Length; i++)
        {
            if (args[i] == "-o" && i + 1 < args.Length)
            {
                outputPath = args[i + 1];
                i++;
            }
            else if (args[i] == "-target" && i + 1 < args.Length)
            {
                targetText = args[i + 1];
                i++;
            }
            else if (args[i] == "-L" && i + 1 < args.Length)
            {
                // Extra directory to search for non-standard DLLs/import libraries/.so
                // files referenced by `extern "name.dll" { ... }` blocks.
                libSearchDirs.Add(args[i + 1]);
                i++;
            }
            else if (args[i] == "-run")
            {
                runAfterBuild = true;
            }
            else if (args[i] == "-O" || args[i] == "--optimize")
            {
                // In-process LLVM optimization pipeline (mem2reg, instcombine, simplifycfg,
                // reassociate, gvn) is opt-in: on some setups it can be slow or hang, so it
                // shouldn't run by default.
                optimize = true;
            }
            else if (args[i] == "-copt" && i + 1 < args.Length)
            {
                // Optimization level passed to clang's own linking/codegen pass (-O<level>).
                // Independent of -O/--optimize above, which toggles ZV's own in-process LLVM
                // pass pipeline.
                string requested = args[i + 1];
                i++;
                if (string.Equals(requested, "list", StringComparison.OrdinalIgnoreCase))
                {
                    PrintClangOptLevels();
                    return;
                }

                string normalized = requested.TrimStart('-', 'O', 'o');
                if (!ClangOptLevels.Contains(normalized, StringComparer.OrdinalIgnoreCase))
                {
                    Console.WriteLine($"Error: Unknown -copt level '{requested}'.");
                    PrintClangOptLevels();
                    Environment.ExitCode = 1;
                    return;
                }
                clangOptLevel = normalized.ToLowerInvariant();
            }
            else if (args[i] == "-v" || args[i] == "--verbose")
            {
                Verbose = true;
            }
        }

        LogVerbose($"input='{inputPath}', output='{outputPath ?? "(default)"}', target='{targetText ?? "(default)"}', run={runAfterBuild}, optimize={optimize}, clangOptLevel=O{clangOptLevel}");

        // Always include the input directory in the library search path so local
        // DLLs/.lib files next to the source can be linked without extra -L flags.
        string? inputDir = null;
        if (File.Exists(inputPath))
        {
            inputDir = Path.GetDirectoryName(Path.GetFullPath(inputPath));
        }
        else if (Directory.Exists(inputPath))
        {
            inputDir = Path.GetFullPath(inputPath);
        }
        if (!string.IsNullOrEmpty(inputDir))
        {
            // Normalize all search dirs to full paths and remove duplicates.
            for (int i = 0; i < libSearchDirs.Count; i++)
            {
                libSearchDirs[i] = Path.GetFullPath(libSearchDirs[i]);
            }
            libSearchDirs = libSearchDirs.Distinct(StringComparer.OrdinalIgnoreCase).ToList();

            if (!libSearchDirs.Contains(inputDir, StringComparer.OrdinalIgnoreCase))
            {
                libSearchDirs.Add(inputDir);
            }
        }

        LogVerbose($"Library search paths: [{string.Join(", ", libSearchDirs)}]");

        TargetInfo target;
        try
        {
            if (string.IsNullOrEmpty(targetText))
            {
                string defaultArch = RuntimeInformation.ProcessArchitecture switch
                {
                    Architecture.X64 => "amd64",
                    Architecture.X86 => "x86-32",
                    _ => "amd64"
                };
                target = TargetParser.Parse($"{defaultArch}-hosted");
            }
            else
            {
                target = TargetParser.Parse(targetText);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error: {ex.Message}");
            Environment.ExitCode = 1;
            return;
        }

        bool isExecutable = target.OutputFormat == OutputFormat.Executable;
        bool isLibraryTarget = target.OutputFormat == OutputFormat.SharedLibrary;
        bool isBareMetalImage = target.IsBareMetal && target.OutputFormat == OutputFormat.RawImage;

        if (string.IsNullOrEmpty(outputPath))
        {
            outputPath = target.OutputFormat switch
            {
                OutputFormat.RawImage => Path.ChangeExtension(inputPath, ".img"),
                OutputFormat.SharedLibrary => Path.ChangeExtension(inputPath, OperatingSystem.IsWindows() ? ".dll" : ".so"),
                _ => inputPath
            };
        }

        string outputFile = outputPath;
        string bcPath = (isExecutable || isLibraryTarget)
            ? Path.ChangeExtension(outputFile, ".bc")
            : (outputFile + ".bc");

        List<string> filesToProcess = new List<string>();
        if (File.Exists(inputPath))
        {
            filesToProcess.Add(inputPath);
        }
        else if (Directory.Exists(inputPath))
        {
            filesToProcess.AddRange(Directory.GetFiles(inputPath, "*.zv", SearchOption.AllDirectories));
        }
        else
        {
            Console.WriteLine($"Error: Input path '{inputPath}' does not exist.");
            return;
        }

        if (filesToProcess.Count == 0)
        {
            Console.WriteLine("No .zv files found to compile.");
            return;
        }

        LogVerbose($"Discovered {filesToProcess.Count} source file(s): {string.Join(", ", filesToProcess)}");

        try
        {
            List<Statement> allStatements = new List<Statement>();
            HashSet<string> includedFiles = new HashSet<string>();
            bool hadParseError = false;

            var lexParseStopwatch = System.Diagnostics.Stopwatch.StartNew();
            foreach (var file in filesToProcess)
            {
                string fullPath = Path.GetFullPath(file);
                if (includedFiles.Contains(fullPath))
                {
                    LogVerbose($"Skipping '{fullPath}' (already included, e.g. via #include).");
                    continue;
                }

                LogVerbose($"Lexing and parsing '{fullPath}'...");
                var fileStopwatch = System.Diagnostics.Stopwatch.StartNew();

                string source = File.ReadAllText(fullPath);
                var lexer = new Lexer(source, fullPath, includedFiles, systemIncludePaths: Lexer.GetDefaultSystemIncludePaths());
                var tokens = lexer.ScanTokens();
                var parser = new Parser(tokens, fullPath);
                var statements = parser.Parse();
                includedFiles.Add(fullPath);

                LogVerbose($"'{fullPath}': {tokens.Count} tokens, {statements.Count} top-level statement(s) ({fileStopwatch.ElapsedMilliseconds}ms).");

                if (parser.HadError)
                {
                    LogVerbose($"'{fullPath}' had parse errors; skipping codegen for this run.");
                    hadParseError = true;
                    continue;
                }

                allStatements.AddRange(statements);
            }
            LogVerbose($"Lexing/parsing complete: {allStatements.Count} total statement(s) across all files ({lexParseStopwatch.ElapsedMilliseconds}ms).");

            if (hadParseError)
            {
                Console.Error.WriteLine("Compilation failed due to syntax errors.");
                Environment.ExitCode = 1;
                return;
            }

            LogVerbose($"Selected target: {target.ShortName} ({target.OutputFormat}, ABI {target.Abi})");

            // Collect binary embed directives before code generation so both the compiler
            // backend and the image builder can see them.
            var embeds = EmbedCollector.Collect(allStatements, inputDir ?? Path.GetFullPath(inputPath));
            if (embeds.Count > 0)
            {
                LogVerbose($"Discovered {embeds.Count} embed directive(s).");
            }

            if (target.Architecture == TargetArchitecture.X86_16)
            {
                LogVerbose("Generating x86-16 bare-metal image...");
                var codegenStopwatch = System.Diagnostics.Stopwatch.StartNew();
                var x86Pipeline = new X86_16BareMetalPipeline(target, Verbose);
                x86Pipeline.Build(allStatements, embeds, outputFile);
                LogVerbose($"Image build complete ({codegenStopwatch.ElapsedMilliseconds}ms).");

                if (runAfterBuild)
                {
                    QemuLauncher.RunImage(outputFile);
                }
            }
            else
            {
                LogVerbose("Generating LLVM IR...");
                var codegenStopwatch = System.Diagnostics.Stopwatch.StartNew();

                using var generator = new LlvmGenerator("zv_module") { Target = target, Verbose = Verbose };

                if (target.Environment == TargetEnvironment.Hosted)
                {
                    generator.EmitHostedEmbeds(embeds);
                }
                else if (target.Environment == TargetEnvironment.BareMetal)
                {
                    generator.EmitBareMetalEmbeds(embeds);
                }

                generator.Generate(allStatements);

                LogVerbose($"LLVM IR generation complete ({codegenStopwatch.ElapsedMilliseconds}ms).");
                PrintWarnings(generator.Warnings);

                // Run LLVM's optimization pipeline in-process before emitting. This generator
                // never emits SSA directly (every local is an alloca/load/store), so mem2reg
                // (and everything that only becomes effective after it) matters regardless of
                // whether the subsequent clang invocation below also optimizes - it's the only
                // optimization that happens at all for outputs that skip clang entirely. Opt-in
                // via -O/--optimize since this pass pipeline can be slow (or hang) on some setups.
                if (optimize)
                {
                    LogVerbose("Running in-process LLVM optimization passes (mem2reg, instcombine, simplifycfg, reassociate, gvn)...");
                    var optStopwatch = System.Diagnostics.Stopwatch.StartNew();
                    generator.RunOptimizationPasses();
                    LogVerbose($"Optimization passes complete ({optStopwatch.ElapsedMilliseconds}ms).");
                }
                else
                {
                    LogVerbose("Skipping in-process LLVM optimization passes (pass -O/--optimize to enable).");
                }

                generator.EmitToFile(bcPath);
                Console.WriteLine($"Bitcode written to {bcPath}");
                if (Verbose && File.Exists(bcPath))
                {
                    LogVerbose($"Bitcode file size: {new FileInfo(bcPath).Length} bytes.");
                }

                if (target.Environment == TargetEnvironment.BareMetal)
                {
                    if (target.BootMethod == BootMethod.Multiboot1)
                    {
                        LogVerbose("Building x86-32 Multiboot v1 ELF kernel...");
                        BuildMultiboot1Kernel(bcPath, outputFile);

                        if (runAfterBuild)
                        {
                            QemuLauncher.RunKernel(outputFile);
                        }
                    }
                    else if (target.BootMethod == BootMethod.Multiboot2)
                    {
                        throw new NotSupportedException("AMD64 Multiboot v2 bootable image is not implemented yet.");
                    }
                    else
                    {
                        Console.WriteLine("Compilation successful. Bitcode written; no bootable image requested.");
                    }
                }
                else if (isLibraryTarget)
                {
                    LogVerbose($"Linking as a shared library via clang -> '{outputFile}'...");
                    CompileWithClang(bcPath, outputFile, generator.GetExternalLibraries(), libSearchDirs, shared: true, optLevel: clangOptLevel);
                }
                else if (isExecutable)
                {
                    LogVerbose($"Linking as an executable via clang -> '{outputFile}'...");
                    CompileWithClang(bcPath, outputFile, generator.GetExternalLibraries(), libSearchDirs, optLevel: clangOptLevel);
                }
                else
                {
                    Console.WriteLine("Compilation successful.");
                }
            }
        }
        catch (CompileException ex)
        {
            Console.Error.WriteLine($"Compile error: {ex.Message}");
            Environment.ExitCode = 1;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Internal compiler error: {ex.Message}");
            Environment.ExitCode = 1;
        }
    }

    // Checks whether the external tools the compiler shells out to (Clang, the LLVM
    // linker/binutils, QEMU) are reachable on PATH, without actually invoking them.
    private static void RunCheckDeps()
    {
        var deps = new (string FileName, string Description, bool Required)[]
        {
            ("clang", "Compiles LLVM IR and links hosted exe/lib targets", true),
            (OperatingSystem.IsWindows() ? "ld.lld.exe" : "ld.lld", "Links x86-32 bare-metal Multiboot kernels", false),
            ("llvm-readobj", "Reads a DLL's export table to auto-generate an import library", false),
            ("llvm-dlltool", "Generates a Windows import library (.lib) from a DLL's exports", false),
            ("qemu-system-i386", "Boots x86-16 raw images and x86-32 Multiboot kernels when using -run", false),
        };

        Console.WriteLine("Checking ZV toolchain dependencies...");
        Console.WriteLine();

        bool missingRequired = false;
        foreach (var (fileName, description, required) in deps)
        {
            string? location = FindInPath(fileName);
            bool found = location != null;
            missingRequired |= required && !found;

            string status = found ? "found" : (required ? "MISSING (required)" : "missing (optional)");
            Console.WriteLine($"[{(found ? "x" : " ")}] {fileName,-18} {status}");
            Console.WriteLine($"      {description}{(found ? $" ({location})" : "")}");
        }

        Console.WriteLine();
        if (missingRequired)
        {
            Console.WriteLine("Some required dependencies are missing. Install them and make sure they're on PATH.");
            Environment.ExitCode = 1;
        }
        else
        {
            Console.WriteLine("All required dependencies were found.");
        }
    }

    // Resolves fileName against PATH the same way the OS/.NET's process launcher would,
    // without actually starting the process. Returns the full path if found, else null.
    private static string? FindInPath(string fileName)
    {
        string? pathVar = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(pathVar)) return null;

        string[] extensions = OperatingSystem.IsWindows() && !Path.HasExtension(fileName)
            ? new[] { ".exe", ".cmd", ".bat", "" }
            : new[] { "" };

        foreach (var dir in pathVar.Split(Path.PathSeparator))
        {
            if (string.IsNullOrWhiteSpace(dir)) continue;
            string trimmedDir = dir.Trim().Trim('"');

            foreach (var ext in extensions)
            {
                string candidate;
                try
                {
                    candidate = Path.Combine(trimmedDir, fileName + ext);
                }
                catch (ArgumentException)
                {
                    continue; // malformed PATH entry
                }

                if (File.Exists(candidate)) return candidate;
            }
        }
        return null;
    }

    public static void CompileWithClang(string inputLl, string outputExe, IEnumerable<string> libraries, List<string>? libSearchDirs = null, bool shared = false, string optLevel = DefaultClangOptLevel)
    {
        Console.WriteLine(shared ? "Detecting Clang (building shared library)..." : "Detecting Clang...");
        try
        {
            // Default platform libraries needed for the hosted runtime (Windows only;
            // these are Win32/CRT import libraries and don't exist on other platforms).
            List<string> libArgs = new List<string>();
            if (OperatingSystem.IsWindows())
            {
                libArgs.AddRange(new[] { "-luser32", "-lkernel32", "-lmsvcrt" });
                // legacy_stdio_definitions.lib for printf on modern MSVC
                libArgs.Add("-llegacy_stdio_definitions");
            }
            else
            {
                // pthreads used by the threading builtins on POSIX targets.
                libArgs.Add("-lpthread");
            }

            // Extra directories to search for non-standard DLLs/import libraries/.so
            // files, passed with -L (e.g. via `-L <dir>` on the ZV command line).
            List<string> searchDirArgs = new List<string>();
            foreach (var dir in libSearchDirs ?? new List<string>())
            {
                string fullDir = Path.GetFullPath(dir);
                searchDirArgs.Add($"-L\"{fullDir}\"");
                Console.WriteLine($"Adding library search path '{fullDir}'");
            }

            // Explicitly report every native library requested via `extern "..."` blocks,
            // and the exact linker flag/path it is translated to, so it's clear at build
            // time which DLLs/.so files the output will depend on.
            //
            // A library name that looks like a path (contains a directory separator) is
            // passed straight to the linker as a file, instead of being turned into
            // `-l<name>` and relying on the default/`-L` search paths. This is how you
            // link against a non-standard DLL that isn't installed anywhere on the
            // system's library search path, e.g. `extern "./vendor/mylib.dll" { ... }`
            // or `extern "C:/libs/mylib.lib" { ... }`.
            List<string> externLibArgs = new List<string>();
            List<string> directLibArgs = new List<string>();
            foreach (var lib in libraries)
            {
                // On Windows, a bare .dll/.lib name that matches a real file in a search
                // directory or the current directory should be linked directly. This lets
                // `extern "raylib.dll"` work when only the DLL (no import .lib) is present,
                // by auto-generating an import library from the DLL's export table.
                string? resolvedPath = null;
                if (OperatingSystem.IsWindows() && !lib.Contains('/') && !lib.Contains('\\'))
                {
                    string lower = lib.ToLowerInvariant();
                    if (lower.EndsWith(".dll") || lower.EndsWith(".lib"))
                    {
                        var libSearchPaths = new List<string>(libSearchDirs ?? new List<string>());
                        string cwd = Path.GetFullPath(Environment.CurrentDirectory);
                        if (!libSearchPaths.Any(d => string.Equals(Path.GetFullPath(d), cwd, StringComparison.OrdinalIgnoreCase)))
                        {
                            libSearchPaths.Add(cwd);
                        }

                        foreach (var dir in libSearchPaths)
                        {
                            string candidate = Path.Combine(Path.GetFullPath(dir), lib);
                            if (File.Exists(candidate))
                            {
                                resolvedPath = candidate;
                                break;
                            }
                        }
                    }
                }

                if (!string.IsNullOrEmpty(resolvedPath) || lib.Contains('/') || lib.Contains('\\'))
                {
                    string fullPath = Path.GetFullPath(resolvedPath ?? lib);
                    if (!File.Exists(fullPath))
                    {
                        Console.WriteLine($"Warning: library file '{fullPath}' was not found; the linker may fail.");
                    }
                    string linkPath = ResolveDirectLinkPath(fullPath);
                    string directArg = $"\"{linkPath}\"";
                    if (!directLibArgs.Contains(directArg))
                    {
                        directLibArgs.Add(directArg);
                        Console.WriteLine($"Linking directly against library file '{linkPath}'");
                    }
                    continue;
                }

                string cleanLib = lib.ToLower();
                if (cleanLib.EndsWith(".dll")) cleanLib = cleanLib.Substring(0, cleanLib.Length - 4);
                if (cleanLib.EndsWith(".so")) cleanLib = cleanLib.Substring(0, cleanLib.Length - 3);
                if (cleanLib.StartsWith("lib") && cleanLib.EndsWith(".lib")) cleanLib = cleanLib.Substring(3, cleanLib.Length - 7);
                else if (cleanLib.EndsWith(".lib")) cleanLib = cleanLib.Substring(0, cleanLib.Length - 4);

                string arg = "-l" + cleanLib;
                if (!libArgs.Contains(arg) && !externLibArgs.Contains(arg))
                {
                    externLibArgs.Add(arg);
                    Console.WriteLine($"Linking against native library '{lib}' ({arg})");
                }
            }
            libArgs.AddRange(externLibArgs);

            string libs = string.Join(" ", libArgs);
            string searchDirs = string.Join(" ", searchDirArgs);
            string directLibs = string.Join(" ", directLibArgs);
            string sharedFlags = shared ? (OperatingSystem.IsWindows() ? "-shared" : "-shared -fPIC") : "";
            string optFlags = $"-O{optLevel}";
            string clangArguments = $"\"{inputLl}\" {optFlags} -o \"{outputExe}\" {sharedFlags} {searchDirs} {libs} {directLibs}".Trim();

            Console.WriteLine($"Running: clang {clangArguments}");
            var clangStopwatch = System.Diagnostics.Stopwatch.StartNew();

            var startInfo = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "clang",
                Arguments = clangArguments,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = System.Diagnostics.Process.Start(startInfo);
            if (process == null)
            {
                Console.WriteLine("Error: Could not start clang. Make sure it is in your PATH.");
                return;
            }

            process.WaitForExit();
            string output = process.StandardOutput.ReadToEnd();
            string error = process.StandardError.ReadToEnd();
            LogVerbose($"clang exited with code {process.ExitCode} ({clangStopwatch.ElapsedMilliseconds}ms).");

            if (process.ExitCode == 0)
            {
                Console.WriteLine($"Successfully compiled to {outputExe}");
            }
            else
            {
                Console.WriteLine("Clang compilation failed:");
                Console.WriteLine(error);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error invoking Clang: {ex.Message}");
            Console.WriteLine("Make sure Clang is installed and available in your PATH.");
        }
    }

    private static void BuildMultiboot1Kernel(string inputLl, string outputElf)
    {
        Console.WriteLine("Building x86-32 Multiboot v1 kernel...");
        try
        {
            string basePath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
            Directory.CreateDirectory(basePath);
            try
            {
                string bootAsm = Path.Combine(basePath, "boot.S");
                string linkerScript = Path.Combine(basePath, "kernel.ld");
                string bootObj = Path.Combine(basePath, "boot.o");
                string kernelObj = Path.Combine(basePath, "kernel.o");

                File.WriteAllText(bootAsm, @"
.set ALIGN,    1<<0
.set MEMINFO, 1<<1
.set FLAGS,    ALIGN | MEMINFO
.set MAGIC,    0x1BADB002
.set CHECKSUM, -(MAGIC + FLAGS)

.section .multiboot
.align 4
.long MAGIC
.long FLAGS
.long CHECKSUM

.section .bss
.align 16
stack_bottom:
.skip 16384
stack_top:

.section .text
.global _start
_start:
    movl $stack_top, %esp
    pushl $0
    pushl $0
    call main
    addl $8, %esp
    cli
    hlt
    jmp _start
");

                File.WriteAllText(linkerScript, @"
ENTRY(_start)
SECTIONS
{
    . = 1M;
    .text : ALIGN(4K)
    {
        *(.multiboot)
        *(.text)
    }
    .rodata : ALIGN(4K) { *(.rodata*) }
    .data : ALIGN(4K) { *(.data) }
    .bss : ALIGN(4K) { *(COMMON) *(.bss) }
    /DISCARD/ : { *(.eh_frame*) *(.note.*) *(.comment) }
}
");

                // Compile the ZV module to a 32-bit freestanding ELF object.
                string compileArgs = $"-target i686-unknown-none-elf -O2 -ffreestanding -fno-stack-protector -fno-pic -fno-pie -fno-asynchronous-unwind-tables -m32 -c \"{inputLl}\" -o \"{kernelObj}\"";
                if (!RunProcess("clang", compileArgs, out string compileError))
                {
                    Console.WriteLine("Clang compilation failed:");
                    Console.WriteLine(compileError);
                    return;
                }

                // Assemble the tiny boot stub.
                string asmArgs = $"-target i686-unknown-none-elf -c \"{bootAsm}\" -o \"{bootObj}\"";
                if (!RunProcess("clang", asmArgs, out string asmError))
                {
                    Console.WriteLine("Assembler failed:");
                    Console.WriteLine(asmError);
                    return;
                }

                // Link with lld directly against our own linker script.
                string linkArgs = $"-m elf_i386 -T \"{linkerScript}\" \"{bootObj}\" \"{kernelObj}\" -o \"{outputElf}\"";
                if (!RunProcess(OperatingSystem.IsWindows() ? "ld.lld.exe" : "ld.lld", linkArgs, out string linkError))
                {
                    Console.WriteLine("Linking failed:");
                    Console.WriteLine(linkError);
                    return;
                }

                Console.WriteLine($"Successfully built bootable kernel: {outputElf}");
            }
            finally
            {
                Directory.Delete(basePath, recursive: true);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error building Multiboot kernel: {ex.Message}");
        }
    }

    private static bool RunProcess(string fileName, string arguments, out string error)
        => RunProcess(fileName, arguments, out _, out error);

    private static bool RunProcess(string fileName, string arguments, out string output, out string error)
    {
        var startInfo = new System.Diagnostics.ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = System.Diagnostics.Process.Start(startInfo);
        if (process == null)
        {
            output = "";
            error = $"Could not start {fileName}. Make sure it is in your PATH.";
            return false;
        }

        process.WaitForExit();
        output = process.StandardOutput.ReadToEnd();
        error = process.StandardError.ReadToEnd();
        return process.ExitCode == 0;
    }

    // Windows linkers (lld-link/link.exe) can't link directly against a DLL - they need
    // a companion import library (.lib) built from the DLL's export table. This makes
    // plain `.dll` paths in `extern "..."` blocks "just work" like `.lib`/`.so` paths do,
    // by generating that import library automatically (via llvm-readobj + llvm-dlltool)
    // when one isn't already sitting next to the DLL.
    private static string ResolveDirectLinkPath(string fullPath)
    {
        if (!OperatingSystem.IsWindows() || !fullPath.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) || !File.Exists(fullPath))
        {
            return fullPath;
        }

        string sideBySideLib = Path.ChangeExtension(fullPath, ".lib");
        if (File.Exists(sideBySideLib))
        {
            Console.WriteLine($"Using import library '{sideBySideLib}' found alongside '{fullPath}'.");
            return sideBySideLib;
        }

        string generatedLib = fullPath + ".generated.lib";
        if (File.Exists(generatedLib) && File.GetLastWriteTimeUtc(generatedLib) >= File.GetLastWriteTimeUtc(fullPath))
        {
            return generatedLib;
        }

        Console.WriteLine($"No import library found for '{fullPath}'; generating one from its export table...");
        if (!TryGenerateImportLibrary(fullPath, generatedLib))
        {
            Console.WriteLine($"Warning: could not auto-generate an import library for '{fullPath}'; linking may fail. " +
                               "Provide a '.lib' import library alongside the DLL, or make sure 'llvm-readobj' and 'llvm-dlltool' are in your PATH.");
            return fullPath;
        }

        Console.WriteLine($"Generated import library '{generatedLib}'.");
        return generatedLib;
    }

    private static bool TryGenerateImportLibrary(string dllPath, string outputLibPath)
    {
        try
        {
            if (!RunProcess("llvm-readobj", $"--coff-exports \"{dllPath}\"", out string exportsOutput, out string readError))
            {
                Console.WriteLine("Could not read the DLL's export table (llvm-readobj failed):");
                Console.WriteLine(readError);
                return false;
            }

            var exportNames = new List<string>();
            string machine = "i386:x86-64";
            foreach (var rawLine in exportsOutput.Split('\n'))
            {
                string line = rawLine.Trim();
                var archMatch = System.Text.RegularExpressions.Regex.Match(line, @"^Arch:\s*(\S+)");
                if (archMatch.Success)
                {
                    machine = archMatch.Groups[1].Value switch
                    {
                        "x86_64" => "i386:x86-64",
                        "i386" => "i386",
                        "aarch64" => "arm64",
                        _ => "i386:x86-64"
                    };
                    continue;
                }

                var nameMatch = System.Text.RegularExpressions.Regex.Match(line, @"^Name:\s*(\S+)");
                if (nameMatch.Success)
                {
                    exportNames.Add(nameMatch.Groups[1].Value);
                }
            }

            if (exportNames.Count == 0)
            {
                Console.WriteLine($"Warning: '{dllPath}' does not export any symbols (or its export table could not be parsed).");
                return false;
            }

            string defPath = outputLibPath + ".def";
            File.WriteAllText(defPath, "EXPORTS\n" + string.Join("\n", exportNames) + "\n");

            try
            {
                string dlltoolArgs = $"-d \"{defPath}\" -D \"{Path.GetFileName(dllPath)}\" -l \"{outputLibPath}\" -m {machine}";
                if (!RunProcess("llvm-dlltool", dlltoolArgs, out string dlltoolError))
                {
                    Console.WriteLine("Failed to generate import library (llvm-dlltool failed):");
                    Console.WriteLine(dlltoolError);
                    return false;
                }
            }
            finally
            {
                File.Delete(defPath);
            }

            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error generating import library: {ex.Message}");
            return false;
        }
    }

}