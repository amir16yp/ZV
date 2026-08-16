using System;
using System.Collections.Generic;
using System.IO;
using ZV.Compiler.AST;
using ZV.Compiler.Lexer;

namespace ZV.Compiler.Target;

public static class EmbedCollector
{
    public static List<EmbedInfo> Collect(IEnumerable<Statement> statements, string baseDirectory)
    {
        var result = new List<EmbedInfo>();
        foreach (var stmt in statements)
        {
            if (stmt is EmbedStmt embed)
            {
                var info = Resolve(embed, baseDirectory);
                result.Add(info);
            }
        }
        return result;
    }

    private static EmbedInfo Resolve(EmbedStmt embed, string baseDirectory)
    {
        string path = (string)embed.Path.Literal!;
        string resolved = Path.GetFullPath(path, baseDirectory);
        if (!File.Exists(resolved))
        {
            throw new CompileException(embed.Path.Location, $"Embedded file '{path}' not found (resolved to '{resolved}').");
        }

        var kindText = embed.Kind.Lexeme.ToLowerInvariant();
        EmbedKind kind = kindText switch
        {
            "resource" => EmbedKind.Resource,
            "file" => EmbedKind.File,
            _ => throw new CompileException(embed.Kind.Location, $"Unknown embed kind '{embed.Kind.Lexeme}'. Expected 'resource' or 'file'.")
        };

        string? destination = null;
        if (kind == EmbedKind.File)
        {
            destination = embed.DestinationPath != null
                ? (string)embed.DestinationPath.Literal!
                : path;
        }

        long size = new FileInfo(resolved).Length;
        return new EmbedInfo(resolved, kind, destination, embed.Location, size);
    }
}
