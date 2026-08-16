using System;
using System.IO;
using System.Text;
using Xunit;
using ZV.Compiler.AST;
using ZV.Compiler.Target;

namespace ZV.Compiler.Tests;

public class X86_16BackendTests
{
    private static System.Collections.Generic.List<Statement> Parse(string source)
    {
        var lexer = new ZV.Compiler.Lexer.Lexer(source);
        var tokens = lexer.ScanTokens();
        var parser = new ZV.Compiler.Parser.Parser(tokens);
        return parser.Parse();
    }

    [Fact]
    public void CompilesMinimalKernel()
    {
        var source = @"
extern """" {
    VOID print(CSTRING s);
    VOID halt();
}

@entry
VOID kmain() {
    print(""Hello from ZV"");
    halt();
}
";
        var stmts = Parse(source);
        var kernel = new X86_16Backend(stmts).Compile();

        Assert.True(kernel.Length > 0);
        // Kernel string should be present in the binary.
        var text = Encoding.ASCII.GetString(kernel);
        Assert.Contains("Hello from ZV", text);
    }

    [Fact]
    public void BuildsBootableImage()
    {
        string dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(dir);
        try
        {
            var source = @"
extern """" {
    VOID print(CSTRING s);
    VOID halt();
}

@entry
VOID kmain() {
    print(""ZV OS"");
    halt();
}
";
            var stmts = Parse(source);
            string imagePath = Path.Combine(dir, "test.img");
            var pipeline = new X86_16BareMetalPipeline(TargetParser.Parse("x86-16-baremetal"), verbose: false);
            pipeline.Build(stmts, new System.Collections.Generic.List<EmbedInfo>(), imagePath);

            var image = File.ReadAllBytes(imagePath);
            Assert.True(image.Length >= 512);
            Assert.Equal(0x55, image[510]);
            Assert.Equal(0xAA, image[511]);

            // The kernel string should be present in the image (after the boot sector).
            var text = Encoding.ASCII.GetString(image);
            Assert.Contains("Loading ZV...", text);
            Assert.Contains("ZV OS", text);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
