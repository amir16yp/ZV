using System.Collections.Generic;
using ZV.Compiler.Lexer;

namespace ZV.Compiler.AST;

public abstract record TypeNode(SourceLocation Location) : AstNode(Location);

public record PrimitiveTypeNode(Token Type, SourceLocation Location) : TypeNode(Location);

public record ArrayTypeNode(TypeNode BaseType, SourceLocation Location) : TypeNode(Location);

public record FixedSizeArrayTypeNode(TypeNode BaseType, Expression Size, SourceLocation Location) : TypeNode(Location);

public record UserTypeNode(Token Name, SourceLocation Location) : TypeNode(Location);

public record PointerTypeNode(TypeNode BaseType, SourceLocation Location) : TypeNode(Location);

// FUNCPTR<ReturnType(ParamType, ParamType, ...)> - a pointer to a function with the given
// signature. Backed by a plain LLVM function pointer, so it's freely bitcast-compatible
// with PTR<VOID> (for generic C callback parameters) via ConvertToType/`as`, but calling
// through a variable declared with this type performs a real, signature-checked indirect
// call (see VisitCall's FunctionPointerTypeNode branch) instead of requiring a string-named
// lookup the way e.g. thread_spawn() does.
public record FunctionPointerTypeNode(TypeNode ReturnType, List<TypeNode> ParamTypes, SourceLocation Location) : TypeNode(Location);
