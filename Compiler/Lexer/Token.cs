namespace ZV.Compiler.Lexer;

public record SourceLocation(string? File, int Line, int Column, int Offset);

public record Token(TokenType Type, string Lexeme, object? Literal, SourceLocation Location);
