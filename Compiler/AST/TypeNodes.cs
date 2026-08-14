using ZV.Compiler.Lexer;

namespace ZV.Compiler.AST;

public abstract record TypeNode(SourceLocation Location) : AstNode(Location);

public record PrimitiveTypeNode(Token Type, SourceLocation Location) : TypeNode(Location);

public record ArrayTypeNode(TypeNode BaseType, SourceLocation Location) : TypeNode(Location);

public record FixedSizeArrayTypeNode(TypeNode BaseType, Expression Size, SourceLocation Location) : TypeNode(Location);

public record UserTypeNode(Token Name, SourceLocation Location) : TypeNode(Location);

public record PointerTypeNode(TypeNode BaseType, SourceLocation Location) : TypeNode(Location);
