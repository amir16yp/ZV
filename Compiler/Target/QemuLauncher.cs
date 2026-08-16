using System;
using System.Diagnostics;

namespace ZV.Compiler.Target;

public static class QemuLauncher
{
    public static void RunImage(string imagePath)
    {
        Run("Launching QEMU for x86-16 image", $"-fda \"{imagePath}\" -boot a");
    }

    public static void RunKernel(string kernelPath)
    {
        Run("Launching QEMU for Multiboot kernel", $"-kernel \"{kernelPath}\" -serial stdio -m 128");
    }

    private static void Run(string message, string arguments)
    {
        Console.WriteLine($"{message}...");
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = "qemu-system-i386",
                Arguments = arguments,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            using var process = Process.Start(startInfo);
            if (process == null)
            {
                Console.WriteLine("Error: Could not start qemu-system-i386. Make sure it is in your PATH.");
                return;
            }

            process.WaitForExit(30000);
            string output = process.StandardOutput.ReadToEnd();
            string error = process.StandardError.ReadToEnd();
            if (!string.IsNullOrWhiteSpace(output)) Console.WriteLine(output);
            if (!string.IsNullOrWhiteSpace(error)) Console.WriteLine(error);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error launching QEMU: {ex.Message}");
            Console.WriteLine("Make sure QEMU (qemu-system-i386) is installed and available in your PATH.");
        }
    }
}
