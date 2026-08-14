using System.Collections.Generic;
using ZV.Compiler.AST;
using ZV.Compiler.Lexer;

namespace ZV.Compiler.LanguageServer;

public enum SymbolKind
{
    Declaration,
    Reference
}

public class SymbolOccurrence
{
    public string Name { get; }
    public SymbolKind Kind { get; }
    public bool IsType { get; }
    public SourceLocation Location { get; }

    public SymbolOccurrence(string name, SymbolKind kind, SourceLocation location, bool isType = false)
    {
        Name = name;
        Kind = kind;
        IsType = isType;
        Location = location;
    }
}

public class SymbolIndex
{
    private readonly List<SymbolOccurrence> _occurrences = new();

    public IReadOnlyList<SymbolOccurrence> Occurrences => _occurrences;

    public static SymbolIndex Build(IEnumerable<Statement> statements)
    {
        var index = new SymbolIndex();
        foreach (var statement in statements)
        {
            index.VisitStatement(statement);
        }
        return index;
    }

    private void VisitStatement(Statement? statement)
    {
        if (statement == null) return;

        switch (statement)
        {
            case FunctionDeclStmt func:
                AddDeclaration(func.Name);
                foreach (var parameter in func.Parameters)
                {
                    AddDeclaration(parameter.Name);
                    VisitTypeNode(parameter.Type);
                }
                VisitStatement(func.Body);
                break;

            case VarDeclStmt varDecl:
                VisitTypeNode(varDecl.Type);
                AddDeclaration(varDecl.Name);
                if (varDecl.Initializer != null) VisitExpression(varDecl.Initializer);
                break;

            case StructDeclStmt structDecl:
                AddDeclaration(structDecl.Name, isType: true);
                foreach (var field in structDecl.Fields)
                {
                    AddDeclaration(field.Name);
                    VisitTypeNode(field.Type);
                }
                break;

            case ExternDeclStmt externDecl:
                foreach (var func in externDecl.Functions)
                {
                    AddDeclaration(func.Name);
                    foreach (var parameter in func.Parameters)
                    {
                        AddDeclaration(parameter.Name);
                        VisitTypeNode(parameter.Type);
                    }
                    VisitTypeNode(func.ReturnType);
                }
                break;

            case TypeAliasStmt alias:
                AddDeclaration(alias.Name, isType: true);
                VisitTypeNode(alias.AliasedType);
                break;

            case ExceptionTypeDeclStmt exceptionTypeDecl:
                AddDeclaration(exceptionTypeDecl.Name, isType: true);
                VisitExpression(exceptionTypeDecl.DefaultMessage);
                break;

            case BlockStmt block:
                foreach (var s in block.Statements) VisitStatement(s);
                break;

            case IfStmt ifStmt:
                VisitExpression(ifStmt.Condition);
                VisitStatement(ifStmt.ThenBranch);
                VisitStatement(ifStmt.ElseBranch);
                break;

            case WhileStmt whileStmt:
                VisitExpression(whileStmt.Condition);
                VisitStatement(whileStmt.Body);
                break;

            case ForStmt forStmt:
                VisitStatement(forStmt.Initializer);
                VisitExpression(forStmt.Condition);
                VisitExpression(forStmt.Increment);
                VisitStatement(forStmt.Body);
                break;

            case ReturnStmt ret:
                VisitExpression(ret.Value);
                break;

            case FreeStmt free:
                foreach (var value in free.Values) VisitExpression(value);
                break;

            case ExpressionStmt exprStmt:
                VisitExpression(exprStmt.Expression);
                break;

            case TryCatchStmt tryCatch:
                VisitStatement(tryCatch.TryBody);
                foreach (var clause in tryCatch.CatchClauses)
                {
                    AddDeclaration(clause.ExceptionName);
                    VisitStatement(clause.Body);
                }
                break;

            case ThrowStmt throwStmt:
                VisitExpression(throwStmt.Value);
                break;

            case UnsafeStmt unsafeStmt:
                VisitStatement(unsafeStmt.Body);
                break;
        }
    }

    private void VisitExpression(Expression? expression)
    {
        if (expression == null) return;

        switch (expression)
        {
            case VariableExpr variable:
                AddReference(variable.Name, variable.Location);
                break;

            case BinaryExpr binary:
                VisitExpression(binary.Left);
                VisitExpression(binary.Right);
                break;

            case UnaryExpr unary:
                VisitExpression(unary.Right);
                break;

            case GroupingExpr grouping:
                VisitExpression(grouping.Expression);
                break;

            case CallExpr call:
                if (call.Callee is VariableExpr calleeVariable)
                {
                    AddReference(calleeVariable.Name, calleeVariable.Location);
                }
                else
                {
                    VisitExpression(call.Callee);
                }
                foreach (var arg in call.Arguments) VisitExpression(arg);
                break;

            case GetExpr getExpr:
                VisitExpression(getExpr.Object);
                AddReference(getExpr.Name.Lexeme, getExpr.Name.Location);
                break;

            case SetExpr setExpr:
                VisitExpression(setExpr.Object);
                AddReference(setExpr.Name.Lexeme, setExpr.Name.Location);
                VisitExpression(setExpr.Value);
                break;

            case IndexExpr index:
                VisitExpression(index.Target);
                VisitExpression(index.Index);
                break;

            case SetIndexExpr setIndex:
                VisitExpression(setIndex.Target);
                VisitExpression(setIndex.Index);
                VisitExpression(setIndex.Value);
                break;

            case ArrayInitExpr arrayInit:
                foreach (var element in arrayInit.Elements) VisitExpression(element);
                break;

            case ArrayAllocExpr arrayAlloc:
                VisitTypeNode(arrayAlloc.ElementType);
                VisitExpression(arrayAlloc.Size);
                if (arrayAlloc.FillValue != null) VisitExpression(arrayAlloc.FillValue);
                break;

            case CastExpr cast:
                VisitExpression(cast.Expression);
                VisitTypeNode(cast.TargetType);
                break;

            case TernaryExpr ternary:
                VisitExpression(ternary.Condition);
                VisitExpression(ternary.ThenBranch);
                VisitExpression(ternary.ElseBranch);
                break;

            case StructInitExpr structInit:
                foreach (var (name, value) in structInit.Fields)
                {
                    AddReference(name.Lexeme, name.Location);
                    VisitExpression(value);
                }
                break;

            case LiteralExpr:
            case PostfixExpr:
                break;
        }
    }

    private void VisitTypeNode(TypeNode? typeNode)
    {
        if (typeNode == null) return;

        switch (typeNode)
        {
            case UserTypeNode userType:
                AddReference(userType.Name.Lexeme, userType.Name.Location, isType: true);
                break;
            case ArrayTypeNode arrayType:
                VisitTypeNode(arrayType.BaseType);
                break;
            case FixedSizeArrayTypeNode fixedArray:
                VisitTypeNode(fixedArray.BaseType);
                VisitExpression(fixedArray.Size);
                break;
            case PointerTypeNode pointerType:
                VisitTypeNode(pointerType.BaseType);
                break;
        }
    }

    private void AddDeclaration(Token token, bool isType = false)
    {
        _occurrences.Add(new SymbolOccurrence(token.Lexeme, SymbolKind.Declaration, token.Location, isType));
    }

    private void AddReference(string name, SourceLocation location, bool isType = false)
    {
        _occurrences.Add(new SymbolOccurrence(name, SymbolKind.Reference, location, isType));
    }
}
