using ZV.Compiler.Lexer;

namespace ZV.Compiler.Target;

public enum EmbedKind
{
    Resource,
    File
}

public sealed record EmbedInfo(
    string SourcePath,
    EmbedKind Kind,
    string? DestinationPath,
    SourceLocation Location,
    long Size);
