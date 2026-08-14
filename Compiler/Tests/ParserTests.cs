using Xunit;
using ZV.Compiler.Lexer;
using ZV.Compiler.Parser;
using ZV.Compiler.AST;
using System.Collections.Generic;
using System.Linq;

namespace ZV.Compiler.Tests;

public class ParserTests
{
    private List<Statement> Parse(string source)
    {
        var lexer = new ZV.Compiler.Lexer.Lexer(source);
        var tokens = lexer.ScanTokens();
        var parser = new ZV.Compiler.Parser.Parser(tokens);
        return parser.Parse();
    }

    [Fact]
    public void TestVariableDeclaration()
    {
        var source = "UINT32 x = 10;";
        var statements = Parse(source);

        Assert.Single(statements);
        var varDecl = Assert.IsType<VarDeclStmt>(statements[0]);
        var primitiveType = Assert.IsType<PrimitiveTypeNode>(varDecl.Type);
        Assert.Equal("UINT32", primitiveType.Type.Lexeme);
        Assert.Equal("x", varDecl.Name.Lexeme);
        
        var literal = Assert.IsType<LiteralExpr>(varDecl.Initializer);
        Assert.Equal(10L, literal.Value);
    }

    [Fact]
    public void TestBinaryExpression()
    {
        var source = "UINT32 x = 10 + 20;";
        var statements = Parse(source);

        Assert.Single(statements);
        var varDecl = Assert.IsType<VarDeclStmt>(statements[0]);
        var binary = Assert.IsType<BinaryExpr>(varDecl.Initializer);
        
        Assert.Equal("+", binary.Operator.Lexeme);
        var left = Assert.IsType<LiteralExpr>(binary.Left);
        Assert.Equal(10L, left.Value);
        var right = Assert.IsType<LiteralExpr>(binary.Right);
        Assert.Equal(20L, right.Value);
    }

    [Fact]
    public void TestStringVariable()
    {
        var source = "STRING message = \"hello\";";
        var statements = Parse(source);

        Assert.Single(statements);
        var varDecl = Assert.IsType<VarDeclStmt>(statements[0]);
        var primitiveType = Assert.IsType<PrimitiveTypeNode>(varDecl.Type);
        Assert.Equal("STRING", primitiveType.Type.Lexeme);
        var literal = Assert.IsType<LiteralExpr>(varDecl.Initializer);
        Assert.Equal("hello", literal.Value);
    }

    [Fact]
    public void TestBoolVariable()
    {
        var source = "BOOL enabled = true;";
        var statements = Parse(source);

        Assert.Single(statements);
        var varDecl = Assert.IsType<VarDeclStmt>(statements[0]);
        var primitiveType = Assert.IsType<PrimitiveTypeNode>(varDecl.Type);
        Assert.Equal("BOOL", primitiveType.Type.Lexeme);
        var literal = Assert.IsType<LiteralExpr>(varDecl.Initializer);
        Assert.Equal(true, literal.Value);
    }

    [Fact]
    public void TestIfStatement()
    {
        var source = "if (true) { return 1; } else { return 0; }";
        var statements = Parse(source);

        Assert.Single(statements);
        var ifStmt = Assert.IsType<IfStmt>(statements[0]);
        Assert.IsType<LiteralExpr>(ifStmt.Condition);
        Assert.IsType<BlockStmt>(ifStmt.ThenBranch);
        Assert.IsType<BlockStmt>(ifStmt.ElseBranch);
    }

    [Fact]
    public void TestWhileStatement()
    {
        var source = "while (x < 10) { x = x + 1; }";
        var statements = Parse(source);

        Assert.Single(statements);
        var whileStmt = Assert.IsType<WhileStmt>(statements[0]);
        Assert.IsType<BinaryExpr>(whileStmt.Condition);
        Assert.IsType<BlockStmt>(whileStmt.Body);
    }

    [Fact]
    public void TestFunctionDeclaration()
    {
        var source = "INT32 add(INT32 a, INT32 b) { return a + b; }";
        var statements = Parse(source);

        Assert.Single(statements);
        var funcDecl = Assert.IsType<FunctionDeclStmt>(statements[0]);
        Assert.Equal("add", funcDecl.Name.Lexeme);
        var returnType = Assert.IsType<PrimitiveTypeNode>(funcDecl.ReturnType);
        Assert.Equal("INT32", returnType.Type.Lexeme);
        Assert.Equal(2, funcDecl.Parameters.Count);
        Assert.Equal("a", funcDecl.Parameters[0].Name.Lexeme);
        Assert.Equal("b", funcDecl.Parameters[1].Name.Lexeme);
    }

    [Fact]
    public void TestStructDeclaration()
    {
        var source = "struct Point { INT32 x; INT32 y; }";
        var statements = Parse(source);

        Assert.Single(statements);
        var structDecl = Assert.IsType<StructDeclStmt>(statements[0]);
        Assert.Equal("Point", structDecl.Name.Lexeme);
        Assert.Equal(2, structDecl.Fields.Count);
        Assert.Equal("x", structDecl.Fields[0].Name.Lexeme);
        Assert.Equal("y", structDecl.Fields[1].Name.Lexeme);
    }

    [Fact]
    public void TestFreeStatement()
    {
        var source = "free(a, b);";
        var statements = Parse(source);

        Assert.Single(statements);
        var freeStmt = Assert.IsType<FreeStmt>(statements[0]);
        Assert.Equal(2, freeStmt.Values.Count);
    }

    [Fact]
    public void TestMoveExpression()
    {
        var source = "STRING b = move(a);";
        var statements = Parse(source);

        Assert.Single(statements);
        var varDecl = Assert.IsType<VarDeclStmt>(statements[0]);
        var call = Assert.IsType<CallExpr>(varDecl.Initializer);
        var callee = Assert.IsType<VariableExpr>(call.Callee);
        Assert.Equal("move", callee.Name);
        Assert.Single(call.Arguments);
        var variable = Assert.IsType<VariableExpr>(call.Arguments[0]);
        Assert.Equal("a", variable.Name);
    }

    [Fact]
    public void TestExpressionPrecedence()
    {
        var source = "UINT32 x = 1 + 2 * 3;";
        var statements = Parse(source);

        Assert.Single(statements);
        var varDecl = Assert.IsType<VarDeclStmt>(statements[0]);
        var plus = Assert.IsType<BinaryExpr>(varDecl.Initializer);
        Assert.Equal("+", plus.Operator.Lexeme);
        
        Assert.IsType<LiteralExpr>(plus.Left);
        var star = Assert.IsType<BinaryExpr>(plus.Right);
        Assert.Equal("*", star.Operator.Lexeme);
    }

    [Fact]
    public void TestParseError()
    {
        var source = "UINT32 x = ;";
        var lexer = new ZV.Compiler.Lexer.Lexer(source);
        var tokens = lexer.ScanTokens();
        var parser = new ZV.Compiler.Parser.Parser(tokens);
        
        // The parser catches ParseError and synchronizes, returning null for the failed declaration.
        // It doesn't currently expose errors in a way that Parse() throws.
        var statements = parser.Parse();
        Assert.Empty(statements);
    }

    [Fact]
    public void TestFixedSizeStackArrayDeclaration()
    {
        var source = "INT32[64] numbers;";
        var statements = Parse(source);

        Assert.Single(statements);
        var varDecl = Assert.IsType<VarDeclStmt>(statements[0]);
        var fixedArrayType = Assert.IsType<FixedSizeArrayTypeNode>(varDecl.Type);
        Assert.IsType<PrimitiveTypeNode>(fixedArrayType.BaseType);
        var size = Assert.IsType<LiteralExpr>(fixedArrayType.Size);
        Assert.Equal(64L, size.Value);
        Assert.Equal("numbers", varDecl.Name.Lexeme);
        Assert.Null(varDecl.Initializer);
    }

    [Fact]
    public void TestFixedSizeStackArrayFillInitialization()
    {
        var source = "INT32[64] values = 5;";
        var statements = Parse(source);

        Assert.Single(statements);
        var varDecl = Assert.IsType<VarDeclStmt>(statements[0]);
        var fixedArrayType = Assert.IsType<FixedSizeArrayTypeNode>(varDecl.Type);
        Assert.Equal(64L, Assert.IsType<LiteralExpr>(fixedArrayType.Size).Value);
        var fillValue = Assert.IsType<LiteralExpr>(varDecl.Initializer);
        Assert.Equal(5L, fillValue.Value);
    }

    [Fact]
    public void TestFixedSizeStackArrayExplicitInitialization()
    {
        var source = "INT32[4] values = [1, 2, 3, 4];";
        var statements = Parse(source);

        Assert.Single(statements);
        var varDecl = Assert.IsType<VarDeclStmt>(statements[0]);
        var fixedArrayType = Assert.IsType<FixedSizeArrayTypeNode>(varDecl.Type);
        Assert.Equal(4L, Assert.IsType<LiteralExpr>(fixedArrayType.Size).Value);
        var arrayInit = Assert.IsType<ArrayInitExpr>(varDecl.Initializer);
        Assert.Equal(4, arrayInit.Elements.Count);
    }

    [Fact]
    public void TestDynamicHeapArrayAllocation()
    {
        var source = "INT32[] numbers = INT32[64];";
        var statements = Parse(source);

        Assert.Single(statements);
        var varDecl = Assert.IsType<VarDeclStmt>(statements[0]);
        Assert.IsType<ArrayTypeNode>(varDecl.Type);
        var allocExpr = Assert.IsType<ArrayAllocExpr>(varDecl.Initializer);
        Assert.IsType<PrimitiveTypeNode>(allocExpr.ElementType);
        Assert.Equal(64L, Assert.IsType<LiteralExpr>(allocExpr.Size).Value);
        Assert.Null(allocExpr.FillValue);
    }

    [Fact]
    public void TestDynamicHeapArrayFillAllocation()
    {
        var source = "INT32[] values = INT32[64](5);";
        var statements = Parse(source);

        Assert.Single(statements);
        var varDecl = Assert.IsType<VarDeclStmt>(statements[0]);
        Assert.IsType<ArrayTypeNode>(varDecl.Type);
        var allocExpr = Assert.IsType<ArrayAllocExpr>(varDecl.Initializer);
        Assert.Equal(64L, Assert.IsType<LiteralExpr>(allocExpr.Size).Value);
        var fillValue = Assert.IsType<LiteralExpr>(allocExpr.FillValue);
        Assert.Equal(5L, fillValue.Value);
    }

    [Fact]
    public void TestExternDeclaration()
    {
        var source = "extern \"user32.dll\" { INT32 MessageBoxA(PTR<VOID> hwnd, CSTRING text, CSTRING caption, UINT32 type_val); INT32 message_box(PTR<VOID> hwnd, CSTRING text, CSTRING caption, UINT32 type_val) = \"MessageBoxA\"; }";
        var lexer = new ZV.Compiler.Lexer.Lexer(source);
        var tokens = lexer.ScanTokens();
        
        var parser = new ZV.Compiler.Parser.Parser(tokens);
        var statements = parser.Parse();

        Assert.Single(statements);
        var externDecl = Assert.IsType<ExternDeclStmt>(statements[0]);
        Assert.Equal("user32.dll", externDecl.LibraryName.Literal);
        Assert.Equal(2, externDecl.Functions.Count);

        var func1 = externDecl.Functions[0];
        Assert.Equal("MessageBoxA", func1.Name.Lexeme);
        var returnType = Assert.IsType<PrimitiveTypeNode>(func1.ReturnType);
        Assert.Equal("INT32", returnType.Type.Lexeme);
        Assert.Equal(4, func1.Parameters.Count);
        Assert.Null(func1.NativeSymbol);

        var func2 = externDecl.Functions[1];
        Assert.Equal("message_box", func2.Name.Lexeme);
        Assert.Equal("MessageBoxA", func2.NativeSymbol.Literal);
    }

    [Fact]
    public void TestUnsafeStatement()
    {
        var source = "unsafe { INT32 x = 1; }";
        var statements = Parse(source);

        Assert.Single(statements);
        var unsafeStmt = Assert.IsType<UnsafeStmt>(statements[0]);
        var block = Assert.IsType<BlockStmt>(unsafeStmt.Body);
        Assert.Single(block.Statements);
        Assert.IsType<VarDeclStmt>(block.Statements[0]);
    }

    [Fact]
    public void TestIntegerLiteralWithUnderscores()
    {
        var source = "UINT32 x = 1_000_000;";
        var statements = Parse(source);

        Assert.Single(statements);
        var varDecl = Assert.IsType<VarDeclStmt>(statements[0]);
        var literal = Assert.IsType<LiteralExpr>(varDecl.Initializer);
        Assert.Equal(1000000L, literal.Value);
    }

    [Fact]
    public void TestFloatLiteralWithUnderscores()
    {
        var source = "FLOAT64 f = 1_000.000_001;";
        var statements = Parse(source);

        Assert.Single(statements);
        var varDecl = Assert.IsType<VarDeclStmt>(statements[0]);
        var literal = Assert.IsType<LiteralExpr>(varDecl.Initializer);
        Assert.Equal(1000.000001, literal.Value);
    }

    [Fact]
    public void TestHexLiteralWithUnderscores()
    {
        var source = "UINT32 x = 0xFF_FF;";
        var statements = Parse(source);

        Assert.Single(statements);
        var varDecl = Assert.IsType<VarDeclStmt>(statements[0]);
        var literal = Assert.IsType<LiteralExpr>(varDecl.Initializer);
        Assert.Equal(65535L, literal.Value);
    }

    [Fact]
    public void TestCompoundAssignment()
    {
        var source = "UINT32 x = 10; x += 5;";
        var statements = Parse(source);

        Assert.Equal(2, statements.Count);
        var exprStmt = Assert.IsType<ExpressionStmt>(statements[1]);
        var assign = Assert.IsType<BinaryExpr>(exprStmt.Expression);
        Assert.Equal(TokenType.Equals, assign.Operator.Type);
        var add = Assert.IsType<BinaryExpr>(assign.Right);
        Assert.Equal(TokenType.Plus, add.Operator.Type);
    }

    [Fact]
    public void TestBitwiseNot()
    {
        var source = "UINT32 x = ~0xFF;";
        var statements = Parse(source);

        Assert.Single(statements);
        var varDecl = Assert.IsType<VarDeclStmt>(statements[0]);
        var not = Assert.IsType<UnaryExpr>(varDecl.Initializer);
        Assert.Equal(TokenType.Tilde, not.Operator.Type);
    }
}
