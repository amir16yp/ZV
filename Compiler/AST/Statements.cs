using ZV.Compiler.Lexer;
using System.Collections.Generic;

namespace ZV.Compiler.AST;

public record ExpressionStmt(Expression Expression, SourceLocation Location) : Statement(Location);

public record VarDeclStmt(TypeNode Type, Token Name, Expression? Initializer, SourceLocation Location, bool IsConst = false) : Statement(Location);

public record BlockStmt(List<Statement> Statements, SourceLocation Location) : Statement(Location);

public record IfStmt(Expression Condition, Statement ThenBranch, Statement? ElseBranch, SourceLocation Location) : Statement(Location);

public record WhileStmt(Expression Condition, Statement Body, SourceLocation Location) : Statement(Location);

public record ForStmt(Statement? Initializer, Expression? Condition, Expression? Increment, Statement Body, SourceLocation Location) : Statement(Location);

public record ReturnStmt(Expression? Value, SourceLocation Location) : Statement(Location);

public record BreakStmt(SourceLocation Location) : Statement(Location);

public record ContinueStmt(SourceLocation Location) : Statement(Location);

public record FreeStmt(List<Expression> Values, SourceLocation Location) : Statement(Location);

public record Parameter(TypeNode Type, Token Name);

public record FunctionDeclStmt(TypeNode ReturnType, Token Name, List<Parameter> Parameters, BlockStmt Body, SourceLocation Location, bool IsEntry = false, bool IsExported = false) : Statement(Location);

public record StructField(TypeNode Type, Token Name);

public record StructDeclStmt(Token Name, List<StructField> Fields, SourceLocation Location, bool IsPacked = false) : Statement(Location);

public record ExternFunctionDecl(TypeNode ReturnType, Token Name, List<Parameter> Parameters, Token? NativeSymbol, SourceLocation Location);

public record ExternDeclStmt(Token LibraryName, List<ExternFunctionDecl> Functions, SourceLocation Location) : Statement(Location);

// A single `catch` clause attached to a `try`. `ExceptionTypeName` is the type name the
// clause filters on (e.g. "IndexOutOfBoundsException", or a user-chosen name such as
// "MyError"), matched at runtime against the "TypeName: ..." prefix convention used by
// both built-in runtime exceptions and user `throw` statements. A null (or "Exception")
// type name means the clause is a catch-all that matches any exception.
public record CatchClause(string? ExceptionTypeName, Token ExceptionName, Statement Body, SourceLocation Location);

public record TryCatchStmt(Statement TryBody, List<CatchClause> CatchClauses, SourceLocation Location) : Statement(Location);

public record ThrowStmt(Expression Value, SourceLocation Location) : Statement(Location);

// Declares a custom, nominally-named runtime exception type, e.g. `exception MyError;`.
// This registers `Name` as a constructible exception "kind": `MyError("description")`
// builds an Exception value tagged with the "MyError: " prefix, and `catch (MyError e)`
// filters for that tag. See CatchClause.
//
// `DefaultMessage`, if present (`exception MyError = Exception("description");` or
// `exception MyError = "description";`), lets `MyError()` / a bare `throw MyError;` be
// used without repeating the message every time.
public record ExceptionTypeDeclStmt(Token Name, Expression? DefaultMessage, SourceLocation Location) : Statement(Location);

public record TypeAliasStmt(Token Name, TypeNode AliasedType, bool IsNewtype, SourceLocation Location) : Statement(Location);

public record UnsafeStmt(Statement Body, SourceLocation Location) : Statement(Location);

// One `case`/`default` group within a `switch`. `Values` holds every constant expression
// stacked onto this group (`case 1: case 2: ...` shares a single Body); it is empty when
// `IsDefault` is true. See SwitchStmt.
public record SwitchCase(List<Expression> Values, List<Statement> Body, bool IsDefault, SourceLocation Location);

// `switch (Discriminant) { case ...: ...; default: ...; }`. Unlike C, a case does NOT fall
// through into the next one by default - each case implicitly "breaks" at the end of its
// body unless it ends with an explicit `fallthrough;` statement. This keeps the C-familiar
// syntax while removing the classic missing-`break` footgun.
public record SwitchStmt(Expression Discriminant, List<SwitchCase> Cases, SourceLocation Location) : Statement(Location);

// Explicit opt-in to fall through from one `switch` case into the next one's body. Only
// legal as (effectively) the last statement of a case body; see VisitFallthrough.
public record FallthroughStmt(SourceLocation Location) : Statement(Location);
