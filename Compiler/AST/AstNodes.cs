using System;
using ZV.Compiler.Lexer;

namespace ZV.Compiler.AST;

public class CompileException : Exception
{
    public SourceLocation? Location { get; }

    public CompileException(SourceLocation? location, string message, Exception? innerException = null)
        : base(message, innerException)
    {
        Location = location;
    }

    public override string Message => Location != null
        ? $"[{Location.File}:{Location.Line}:{Location.Column}] {base.Message}"
        : base.Message;
}

public abstract record AstNode(SourceLocation Location);

public abstract record Expression(SourceLocation Location) : AstNode(Location);

public abstract record Statement(SourceLocation Location) : AstNode(Location);
