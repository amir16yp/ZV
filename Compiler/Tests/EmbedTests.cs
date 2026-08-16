using System;
using System.IO;
using System.Linq;
using Xunit;
using ZV.Compiler.AST;
using ZV.Compiler.Target;

namespace ZV.Compiler.Tests;

public class EmbedTests
{
    private static System.Collections.Generic.List<Statement> Parse(string source)
    {
        var lexer = new ZV.Compiler.Lexer.Lexer(source);
        var tokens = lexer.ScanTokens();
        var parser = new ZV.Compiler.Parser.Parser(tokens);
        return parser.Parse();
    }

    [Fact]
    public void ParsesResourceEmbedDirective()
    {
        var stmts = Parse("#embed \"data.bin\" resource");
        var embed = Assert.Single(stmts) as EmbedStmt;
        Assert.NotNull(embed);
        Assert.Equal("data.bin", embed.Path.Literal);
        Assert.Equal("resource", embed.Kind.Literal);
        Assert.Null(embed.DestinationPath);
    }

    [Fact]
    public void ParsesFileEmbedDirectiveWithDestination()
    {
        var stmts = Parse("#embed \"logo.png\" file \"assets/logo.png\"");
        var embed = Assert.Single(stmts) as EmbedStmt;
        Assert.NotNull(embed);
        Assert.Equal("logo.png", embed.Path.Literal);
        Assert.Equal("file", embed.Kind.Literal);
        Assert.Equal("assets/logo.png", embed.DestinationPath?.Literal);
    }

    [Fact]
    public void ParsesFileEmbedDirectiveWithoutDestination()
    {
        var stmts = Parse("#embed \"logo.png\" file");
        var embed = Assert.Single(stmts) as EmbedStmt;
        Assert.NotNull(embed);
        Assert.Null(embed.DestinationPath);
    }

    [Fact]
    public void CollectsResourceAndFileEmbeds()
    {
        string dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllBytes(Path.Combine(dir, "a.bin"), new byte[] { 1, 2, 3 });
            File.WriteAllBytes(Path.Combine(dir, "b.bin"), new byte[] { 4, 5 });

            var stmts = Parse(@"
#embed ""a.bin"" resource
#embed ""b.bin"" file ""files/b.bin""
");
            var embeds = EmbedCollector.Collect(stmts, dir);
            Assert.Equal(2, embeds.Count);

            var resource = embeds.Single(e => e.Kind == EmbedKind.Resource);
            Assert.Equal(3, resource.Size);

            var file = embeds.Single(e => e.Kind == EmbedKind.File);
            Assert.Equal(2, file.Size);
            Assert.Equal("files/b.bin", file.DestinationPath);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void MissingEmbedFileProducesCompileError()
    {
        var stmts = Parse("#embed \"missing.bin\" resource");
        Assert.Throws<CompileException>(() => EmbedCollector.Collect(stmts, Path.GetTempPath()));
    }
}
