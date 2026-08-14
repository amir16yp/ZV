using ZV.Compiler.Lexer;
using System.Collections.Generic;

namespace ZV.Compiler.AST;

public record LiteralExpr(object? Value, TokenType Type, SourceLocation Location) : Expression(Location);

public record VariableExpr(string Name, SourceLocation Location) : Expression(Location);

public record BinaryExpr(Expression Left, Token Operator, Expression Right, SourceLocation Location) : Expression(Location);

public record UnaryExpr(Token Operator, Expression Right, SourceLocation Location) : Expression(Location);

public record GroupingExpr(Expression Expression, SourceLocation Location) : Expression(Location);

public record CallExpr(Expression Callee, List<Expression> Arguments, SourceLocation Location) : Expression(Location);

public record GetExpr(Expression Object, Token Name, SourceLocation Location) : Expression(Location);

public record SetExpr(Expression Object, Token Name, Expression Value, SourceLocation Location) : Expression(Location);

public record SetIndexExpr(Expression Target, Expression Index, Expression Value, SourceLocation Location) : Expression(Location);

// TypeName is set when the struct literal names its type explicitly (`Point { x = 1, y = 2 }`);
// it is null for the bare-brace form (`{ x = 1, y = 2 }`), whose type must be inferred from
// the surrounding context (e.g. a variable's declared type or a field's declared type).
public record StructInitExpr(List<(Token Name, Expression Value)> Fields, SourceLocation Location, Token? TypeName = null) : Expression(Location);

public record IndexExpr(Expression Target, Expression Index, SourceLocation Location) : Expression(Location);

public record ArrayInitExpr(List<Expression> Elements, SourceLocation Location) : Expression(Location);

public record ArrayAllocExpr(TypeNode ElementType, Expression Size, Expression? FillValue, SourceLocation Location) : Expression(Location);

public record CastExpr(Expression Expression, TypeNode TargetType, SourceLocation Location) : Expression(Location);

public record TernaryExpr(Expression Condition, Expression ThenBranch, Expression ElseBranch, SourceLocation Location) : Expression(Location);

public record PostfixExpr(Expression Left, Token Operator, SourceLocation Location) : Expression(Location);
