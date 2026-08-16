using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ZV.Compiler.Backend;
using ZV.Compiler.AST;

namespace ZV.Compiler.LanguageServer;

public class CompilationResult
{
    public bool Success { get; set; }
    public string Output { get; set; } = "";
    public string? LlvmIr { get; set; }
    public IReadOnlyList<Diagnostic> Diagnostics { get; set; } = new List<Diagnostic>();
}

public class Diagnostic
{
    public string File { get; set; } = "";
    public int Line { get; set; }
    public int Column { get; set; }
    public string Message { get; set; } = "";
    public string Severity { get; set; } = "error";
}

public static class CompilationService
{
    public static CompilationResult CompileDirectory(string inputPath, string? outputPath = null)
    {
        var result = new CompilationResult();
        var messages = new List<string>();

        if (!Directory.Exists(inputPath))
        {
            result.Output = $"Error: Project path '{inputPath}' does not exist.";
            result.Success = false;
            return result;
        }

        var filesToProcess = Directory.GetFiles(inputPath, "*.zv", SearchOption.AllDirectories).ToList();
        if (filesToProcess.Count == 0)
        {
            result.Output = "No .zv files found to compile.";
            result.Success = false;
            return result;
        }

        string llPath = !string.IsNullOrEmpty(outputPath)
            ? outputPath
            : Path.Combine(Path.GetTempPath(), "zv-build", Guid.NewGuid().ToString("N"), "output.ll");

        try
        {
            var allStatements = new List<Statement>();
            var includedFiles = new HashSet<string>();
            var parseDiagnostics = new List<Diagnostic>();

            foreach (var file in filesToProcess)
            {
                string fullPath = Path.GetFullPath(file);
                if (includedFiles.Contains(fullPath)) continue;

                string source = File.ReadAllText(fullPath);
                var lexer = new global::ZV.Compiler.Lexer.Lexer(source, fullPath, includedFiles, systemIncludePaths: global::ZV.Compiler.Lexer.Lexer.GetDefaultSystemIncludePaths());
                var tokens = lexer.ScanTokens();
                var parser = new global::ZV.Compiler.Parser.Parser(tokens, fullPath);
                var statements = parser.Parse();
                includedFiles.Add(fullPath);

                if (parser.HadError)
                {
                    parseDiagnostics.AddRange(ToDiagnostics(parser.Errors, fullPath));
                    continue;
                }

                allStatements.AddRange(statements);
            }

            if (parseDiagnostics.Count > 0)
            {
                result.Diagnostics = parseDiagnostics;
                messages.AddRange(parseDiagnostics.Select(FormatDiagnostic));
                messages.Add("Compilation failed due to syntax errors.");
                result.Success = false;
                result.Output = string.Join("\n", messages);
                return result;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(llPath)!);

            using var generator = new LlvmGenerator("zv_module");
            generator.Generate(allStatements);
            generator.EmitToFile(llPath);

            string ir = generator.EmitToString();
            result.LlvmIr = ir;
            var warningDiagnostics = WarningsToDiagnostics(generator.Warnings, inputPath);
            result.Diagnostics = warningDiagnostics;
            messages.AddRange(warningDiagnostics.Select(FormatDiagnostic));
            messages.Add($"LLVM IR written to {llPath}");
            messages.Add("Compilation successful.");
            result.Success = true;
        }
        catch (CompileException ex)
        {
            result.Diagnostics = new List<Diagnostic>
            {
                new Diagnostic
                {
                    File = ex.Location?.File ?? inputPath,
                    Line = ex.Location?.Line ?? 0,
                    Column = ex.Location?.Column ?? 0,
                    Message = ex.Message,
                    Severity = "error"
                }
            };
            messages.Add($"Compile error: {ex.Message}");
            result.Success = false;
        }
        catch (Exception ex)
        {
            messages.Add($"Internal compiler error: {ex.Message}");
            result.Success = false;
        }

        result.Output = string.Join("\n", messages);
        return result;
    }

    public static CompilationResult CompileSource(string source, string? fileName = null)
    {
        var result = new CompilationResult();
        var messages = new List<string>();

        try
        {
            var includedFiles = new HashSet<string>();
            var lexer = new global::ZV.Compiler.Lexer.Lexer(source, fileName, includedFiles, systemIncludePaths: global::ZV.Compiler.Lexer.Lexer.GetDefaultSystemIncludePaths());
            var tokens = lexer.ScanTokens();
            var parser = new global::ZV.Compiler.Parser.Parser(tokens, fileName);
            var statements = parser.Parse();

            if (parser.HadError)
            {
                var parseDiagnostics = ToDiagnostics(parser.Errors, fileName ?? "");
                result.Diagnostics = parseDiagnostics;
                messages.AddRange(parseDiagnostics.Select(FormatDiagnostic));
                messages.Add("Compilation failed due to syntax errors.");
                result.Success = false;
                result.Output = string.Join("\n", messages);
                return result;
            }

            using var generator = new LlvmGenerator("zv_module");
            generator.Generate(statements);

            string ir = generator.EmitToString();
            result.LlvmIr = ir;
            var warningDiagnostics = WarningsToDiagnostics(generator.Warnings, fileName ?? "");
            result.Diagnostics = warningDiagnostics;
            messages.AddRange(warningDiagnostics.Select(FormatDiagnostic));
            messages.Add("Compilation successful.");
            result.Success = true;
        }
        catch (CompileException ex)
        {
            result.Diagnostics = new List<Diagnostic>
            {
                new Diagnostic
                {
                    File = ex.Location?.File ?? fileName ?? "",
                    Line = ex.Location?.Line ?? 0,
                    Column = ex.Location?.Column ?? 0,
                    Message = ex.Message,
                    Severity = "error"
                }
            };
            messages.Add($"Compile error: {ex.Message}");
            result.Success = false;
        }
        catch (Exception ex)
        {
            messages.Add($"Internal compiler error: {ex.Message}");
            result.Success = false;
        }

        result.Output = string.Join("\n", messages);
        return result;
    }

    public static List<Diagnostic> LintSource(string source, string? fileName = null, Func<string, string?>? fileProvider = null)
    {
        var diagnostics = new List<Diagnostic>();
        try
        {
            var includedFiles = new HashSet<string>();
            var lexer = new global::ZV.Compiler.Lexer.Lexer(source, fileName, includedFiles, fileProvider: fileProvider, systemIncludePaths: global::ZV.Compiler.Lexer.Lexer.GetDefaultSystemIncludePaths());
            var tokens = lexer.ScanTokens();
            var parser = new global::ZV.Compiler.Parser.Parser(tokens, fileName);
            var statements = parser.Parse();

            if (parser.HadError)
            {
                diagnostics.AddRange(ToDiagnostics(parser.Errors, fileName ?? ""));
                return diagnostics;
            }

            using var generator = new LlvmGenerator("zv_module");
            generator.Generate(statements);
            diagnostics.AddRange(WarningsToDiagnostics(generator.Warnings, fileName ?? ""));
        }
        catch (CompileException ex)
        {
            diagnostics.Add(new Diagnostic
            {
                File = ex.Location?.File ?? fileName ?? "",
                Line = ex.Location?.Line ?? 0,
                Column = ex.Location?.Column ?? 0,
                Message = ex.Message,
                Severity = "error"
            });
        }
        return diagnostics;
    }

    private static List<Diagnostic> ToDiagnostics(
        IEnumerable<(global::ZV.Compiler.Lexer.SourceLocation Location, string Message)> errors,
        string fallbackFile)
    {
        return errors.Select(e => new Diagnostic
        {
            File = e.Location.File ?? fallbackFile,
            Line = e.Location.Line,
            Column = e.Location.Column,
            Message = e.Message,
            Severity = "error"
        }).ToList();
    }

    // Non-fatal compiler diagnostics (see LlvmGenerator.Warnings) don't always have a
    // location (module-wide notes would not), so this takes a nullable one. Named
    // differently from ToDiagnostics because SourceLocation is a reference type, so
    // `SourceLocation` and `SourceLocation?` erase to the same overload signature.
    private static List<Diagnostic> WarningsToDiagnostics(
        IEnumerable<(global::ZV.Compiler.Lexer.SourceLocation? Location, string Message)> warnings,
        string fallbackFile)
    {
        return warnings.Select(w => new Diagnostic
        {
            File = w.Location?.File ?? fallbackFile,
            Line = w.Location?.Line ?? 0,
            Column = w.Location?.Column ?? 0,
            Message = w.Message,
            Severity = "warning"
        }).ToList();
    }

    private static string FormatDiagnostic(Diagnostic d)
    {
        string file = string.IsNullOrEmpty(d.File) ? "?" : Path.GetFileName(d.File);
        return $"{file}:{d.Line}:{d.Column}: {d.Message}";
    }
}
