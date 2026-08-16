using System;
using System.Collections.Generic;
using ZV.Compiler.Lexer;
using ZV.Compiler.AST;

namespace ZV.Compiler.Parser;

public class Parser
{
    private class ParseError : Exception {}

    private readonly List<Token> tokens;
    private int current = 0;
    private readonly string? fileName;

    public bool HadError { get; private set; }
    public List<(SourceLocation Location, string Message)> Errors { get; } = new();

    public Parser(List<Token> tokens, string? fileName = null)
    {
        this.tokens = tokens;
        this.fileName = fileName;
    }

    public List<Statement> Parse()
    {
        List<Statement> statements = new List<Statement>();
        while (!IsAtEnd())
        {
            var decl = Declaration();
            if (decl != null)
            {
                statements.Add(decl);
            }
        }
        return statements;
    }

    private Statement? Declaration()
    {
        try
        {
            bool isEntry = false;
            bool isExported = Match(TokenType.Export);
            bool isPacked = false;

            // Handle @attribute syntax
            while (Check(TokenType.Attribute))
            {
                var attrToken = Advance();
                string attrName = (string)attrToken.Literal!;
                switch (attrName)
                {
                    case "entry":
                        isEntry = true;
                        break;
                    case "export":
                        isExported = true;
                        break;
                    case "packed":
                        isPacked = true;
                        break;
                    default:
                        throw Error(attrToken, $"Unknown attribute '@{attrName}'.");
                }
            }

            if (Match(TokenType.EmbedDirective)) return EmbedDirective();
            if (Check(TokenType.Extern)) return ExternDeclaration();
            if (Check(TokenType.Packed) || Check(TokenType.Struct)) return StructDeclaration(isPacked);
            if (Match(TokenType.Type)) return TypeAliasStatement(false);
            if (Match(TokenType.Newtype)) return TypeAliasStatement(true);
            if (Match(TokenType.ExceptionDecl)) return ExceptionTypeDeclaration();
            
            // Check for CONST qualifier
            bool isConst = Check(TokenType.Const);
            int lookahead = current;
            if (isConst) lookahead++; // skip past CONST for lookahead
            
            // In ZV, a declaration starts with a type.
            // A type can be a primitive (INT32, etc.) or an identifier (struct name).
            // We need to look ahead to see if it's a declaration.
            if (IsType(tokens[lookahead]))
            {
                lookahead++;
                // Skip PTR<...>/FUNCPTR<...> angle brackets (possibly nested, e.g. PTR<PTR<T>>
                // or FUNCPTR<PTR<VOID>(INT32)>). The parens inside a FUNCPTR<...> don't need
                // special handling here since this loop only tracks angle-bracket depth.
                if (lookahead < tokens.Count &&
                    (tokens[lookahead - 1].Type == TokenType.PTR || tokens[lookahead - 1].Type == TokenType.FuncPtr) &&
                    tokens[lookahead].Type == TokenType.Less)
                {
                    lookahead++; // skip '<'
                    int angleDepth = 1;
                    while (lookahead < tokens.Count && angleDepth > 0)
                    {
                        if (tokens[lookahead].Type == TokenType.Less) angleDepth++;
                        else if (tokens[lookahead].Type == TokenType.Greater) angleDepth--;
                        lookahead++;
                    }
                }
                // Skip array brackets, including fixed-size array dimensions (e.g. INT32[64])
                while (lookahead < tokens.Count && tokens[lookahead].Type == TokenType.LeftBracket)
                {
                    lookahead++;
                    int bracketDepth = 1;
                    while (lookahead < tokens.Count && bracketDepth > 0)
                    {
                        var t = tokens[lookahead];
                        if (t.Type == TokenType.LeftBracket) bracketDepth++;
                        else if (t.Type == TokenType.RightBracket) bracketDepth--;
                        lookahead++;
                    }
                }
                
                if (lookahead < tokens.Count && tokens[lookahead].Type == TokenType.Identifier)
                {
                    // Check if it's a function declaration: TYPE NAME (
                    if (lookahead + 1 < tokens.Count && tokens[lookahead + 1].Type == TokenType.LeftParen)
                    {
                        return FunctionDeclaration(isEntry, isExported);
                    }
                    // Otherwise it's a variable declaration: TYPE NAME ...
                    if (isConst) Advance(); // consume the CONST token
                    return VarDeclaration(isConst);
                }
            }

            if (isExported)
            {
                throw Error(Peek(), "Expect function declaration after 'export'.");
            }

            return Statement();
        }
        catch (ParseError)
        {
            Synchronize();
            return null;
        }
    }

    private TypeNode ParseType()
    {
        TypeNode type;
        if (IsType(Peek()))
        {
            Token token = Advance();
            if (token.Type == TokenType.PTR)
            {
                if (Match(TokenType.Less))
                {
                    // PTR<T> syntax - parse the inner type
                    TypeNode innerType = ParseType();
                    Consume(TokenType.Greater, "Expect '>' after pointer element type.");
                    type = new PointerTypeNode(innerType, token.Location);
                }
                else
                {
                    // Bare PTR without <T> - opaque pointer to void
                    var voidToken = new Token(TokenType.VOID, "VOID", null, token.Location);
                    type = new PointerTypeNode(new PrimitiveTypeNode(voidToken, token.Location), token.Location);
                }
            }
            else if (token.Type == TokenType.FuncPtr)
            {
                // FUNCPTR<ReturnType(ParamType, ParamType, ...)>
                Consume(TokenType.Less, "Expect '<' after 'FUNCPTR'.");
                TypeNode returnType = ParseType();
                Consume(TokenType.LeftParen, "Expect '(' after function pointer return type.");
                List<TypeNode> paramTypes = new List<TypeNode>();
                if (!Check(TokenType.RightParen))
                {
                    do
                    {
                        paramTypes.Add(ParseType());
                    } while (Match(TokenType.Comma));
                }
                Consume(TokenType.RightParen, "Expect ')' after function pointer parameter types.");
                Consume(TokenType.Greater, "Expect '>' after function pointer type.");
                type = new FunctionPointerTypeNode(returnType, paramTypes, token.Location);
            }
            else if (token.Type == TokenType.Identifier)
            {
                type = new UserTypeNode(token, token.Location);
            }
            else if (token.Type == TokenType.EXCEPTION)
            {
                type = new PrimitiveTypeNode(token, token.Location);
            }
            else
            {
                type = new PrimitiveTypeNode(token, token.Location);
            }
        }
        else
        {
            throw Error(Peek(), "Expect type.");
        }

        while (Match(TokenType.LeftBracket))
        {
            SourceLocation bracketLoc = Previous().Location;
            if (Match(TokenType.RightBracket))
            {
                type = new ArrayTypeNode(type, type.Location);
            }
            else
            {
                var sizeExpr = Expression();
                Consume(TokenType.RightBracket, "Expect ']' after array size.");
                type = new FixedSizeArrayTypeNode(type, sizeExpr, bracketLoc);
            }
        }

        return type;
    }

    private Statement EmbedDirective()
    {
        Token directive = Previous();
        Token path = Consume(TokenType.StringLiteral, "Expect string path after '#embed'.");
        Token kind = Consume(TokenType.Identifier, "Expect embed kind ('resource' or 'file') after path.");

        Token? destination = null;
        string kindText = kind.Lexeme.ToLowerInvariant();
        if (kindText == "file")
        {
            if (Check(TokenType.StringLiteral))
            {
                destination = Advance();
            }
        }
        else if (kindText != "resource")
        {
            throw Error(kind, "Embed kind must be 'resource' or 'file'.");
        }

        return new EmbedStmt(path, kind, destination, directive.Location);
    }

    private Statement StructDeclaration(bool packedFromAttr = false)
    {
        bool isPacked = packedFromAttr || Match(TokenType.Packed);
        Token structToken = Consume(TokenType.Struct, "Expect 'struct'.");
        Token name = Consume(TokenType.Identifier, "Expect struct name.");
        Consume(TokenType.LeftBrace, "Expect '{' before struct body.");

        List<StructField> fields = new List<StructField>();
        while (!Check(TokenType.RightBrace) && !IsAtEnd())
        {
            TypeNode type = ParseType();
            Token fieldName = Consume(TokenType.Identifier, "Expect field name.");
            Consume(TokenType.Semicolon, "Expect ';' after field declaration.");
            fields.Add(new StructField(type, fieldName));
        }

        Consume(TokenType.RightBrace, "Expect '}' after struct body.");
        return new StructDeclStmt(name, fields, structToken.Location, isPacked);
    }

    private Statement ExternDeclaration()
    {
        Token externToken = Consume(TokenType.Extern, "Expect 'extern'.");
        Token libraryName = Consume(TokenType.StringLiteral, "Expect library name (string literal) after 'extern'.");
        Consume(TokenType.LeftBrace, "Expect '{' after extern library declaration.");

        List<ExternFunctionDecl> functions = new List<ExternFunctionDecl>();
        while (!Check(TokenType.RightBrace) && !IsAtEnd())
        {
            functions.Add(ExternFunctionDeclaration());
        }

        Consume(TokenType.RightBrace, "Expect '}' after extern block.");
        return new ExternDeclStmt(libraryName, functions, externToken.Location);
    }

    private ExternFunctionDecl ExternFunctionDeclaration()
    {
        TypeNode returnType = ParseType();
        Token name = Consume(TokenType.Identifier, "Expect function name.");
        Consume(TokenType.LeftParen, "Expect '(' after function name.");

        List<Parameter> parameters = new List<Parameter>();
        if (!Check(TokenType.RightParen))
        {
            do
            {
                TypeNode paramType = ParseType();
                Token paramName = Consume(TokenType.Identifier, "Expect parameter name.");
                parameters.Add(new Parameter(paramType, paramName));
            } while (Match(TokenType.Comma));
        }
        Consume(TokenType.RightParen, "Expect ')' after parameters.");

        Token? nativeSymbol = null;
        if (Match(TokenType.Equals))
        {
            nativeSymbol = Consume(TokenType.StringLiteral, "Expect native symbol name (string literal) after '='.");
        }

        Consume(TokenType.Semicolon, "Expect ';' after external function declaration.");
        return new ExternFunctionDecl(returnType, name, parameters, nativeSymbol, returnType.Location);
    }

    private Statement FunctionDeclaration(bool isEntry = false, bool isExported = false)
    {
        TypeNode returnType = ParseType();
        Token name = Consume(TokenType.Identifier, "Expect function name.");
        Consume(TokenType.LeftParen, "Expect '(' after function name.");

        List<Parameter> parameters = new List<Parameter>();
        if (!Check(TokenType.RightParen))
        {
            do
            {
                TypeNode paramType = ParseType();
                Token paramName = Consume(TokenType.Identifier, "Expect parameter name.");
                parameters.Add(new Parameter(paramType, paramName));
            } while (Match(TokenType.Comma));
        }
        Consume(TokenType.RightParen, "Expect ')' after parameters.");

        Consume(TokenType.LeftBrace, "Expect '{' before function body.");
        BlockStmt body = (BlockStmt)Block();

        return new FunctionDeclStmt(returnType, name, parameters, body, returnType.Location, isEntry, isExported);
    }

    private Statement VarDeclaration(bool isConst = false)
    {
        TypeNode type = ParseType();
        Token name = Consume(TokenType.Identifier, "Expect variable name.");

        Expression? initializer = null;
        if (Match(TokenType.Equals))
        {
            initializer = Expression();
        }

        if (isConst && initializer == null)
        {
            throw Error(name, "CONST variable must have an initializer.");
        }

        Consume(TokenType.Semicolon, "Expect ';' after variable declaration.");
        return new VarDeclStmt(type, name, initializer, type.Location, isConst);
    }

    private Statement Statement()
    {
        if (Match(TokenType.If)) return IfStatement();
        if (Match(TokenType.Return)) return ReturnStatement();
        if (Match(TokenType.While)) return WhileStatement();
        if (Match(TokenType.For)) return ForStatement();
        if (Match(TokenType.Break)) return BreakStatement();
        if (Match(TokenType.Continue)) return ContinueStatement();
        if (Match(TokenType.Free)) return FreeStatement();
        if (Match(TokenType.Try)) return TryCatchStatement();
        if (Match(TokenType.Throw)) return ThrowStatement();
        if (Match(TokenType.Switch)) return SwitchStatement();
        if (Match(TokenType.Fallthrough)) return FallthroughStatement();
        if (Match(TokenType.Type)) return TypeAliasStatement(false);
        if (Match(TokenType.Newtype)) return TypeAliasStatement(true);
        if (Match(TokenType.ExceptionDecl)) return ExceptionTypeDeclaration();
        if (Match(TokenType.Unsafe)) return UnsafeStatement();
        if (Match(TokenType.LeftBrace)) return Block();

        return ExpressionStatement();
    }

    private Statement UnsafeStatement()
    {
        Token keyword = Previous();
        Consume(TokenType.LeftBrace, "Expect '{' after 'unsafe'.");
        Statement body = Block();
        return new UnsafeStmt(body, keyword.Location);
    }

    private Statement TryCatchStatement()
    {
        SourceLocation loc = Previous().Location;
        Consume(TokenType.LeftBrace, "Expect '{' after 'try'.");
        Statement tryBody = Block();

        var clauses = new List<CatchClause>();
        bool sawCatchAll = false;

        Consume(TokenType.Catch, "Expect 'catch' after try block.");
        do
        {
            SourceLocation catchLoc = Previous().Location;
            Consume(TokenType.LeftParen, "Expect '(' after 'catch'.");

            // Two forms are accepted: `catch (name)` (catches any exception) and
            // `catch (TypeName name)` (only catches exceptions whose runtime type name -
            // the "TypeName: ..." prefix convention - matches). We disambiguate by looking
            // one token ahead: a type token immediately followed by an identifier means the
            // first token is a type filter.
            string? exceptionTypeName = null;
            Token exceptionName;
            if (IsType(Peek()) && PeekNext().Type == TokenType.Identifier)
            {
                Token typeToken = Advance();
                exceptionTypeName = typeToken.Type == TokenType.EXCEPTION ? "Exception" : typeToken.Lexeme;
                exceptionName = Consume(TokenType.Identifier, "Expect exception variable name.");
            }
            else
            {
                exceptionName = Consume(TokenType.Identifier, "Expect exception variable name.");
            }

            Consume(TokenType.RightParen, "Expect ')' after catch clause.");
            Consume(TokenType.LeftBrace, "Expect '{' before catch body.");
            Statement catchBody = Block();

            if (sawCatchAll)
            {
                throw Error(Previous(), "A catch-all clause (with no type, or type 'Exception') must be the last 'catch' clause.");
            }

            bool isCatchAll = exceptionTypeName == null || exceptionTypeName == "Exception";
            if (isCatchAll) sawCatchAll = true;

            clauses.Add(new CatchClause(exceptionTypeName, exceptionName, catchBody, catchLoc));
        } while (Match(TokenType.Catch));

        return new TryCatchStmt(tryBody, clauses, loc);
    }

    private Statement ThrowStatement()
    {
        Token keyword = Previous();
        Expression value = Expression();
        Consume(TokenType.Semicolon, "Expect ';' after throw expression.");
        return new ThrowStmt(value, keyword.Location);
    }

    // `switch (expr) { case C1: ...; case C2: case C3: ...; default: ...; }`. Stacked case
    // labels (no statements between them) share a single body. Unlike C there is no
    // implicit fallthrough - see SwitchStmt/FallthroughStmt.
    private Statement SwitchStatement()
    {
        SourceLocation loc = Previous().Location;
        Consume(TokenType.LeftParen, "Expect '(' after 'switch'.");
        Expression discriminant = Expression();
        Consume(TokenType.RightParen, "Expect ')' after switch expression.");
        Consume(TokenType.LeftBrace, "Expect '{' before switch body.");

        var cases = new List<SwitchCase>();
        bool sawDefault = false;

        while (!Check(TokenType.RightBrace) && !IsAtEnd())
        {
            SourceLocation caseLoc = Peek().Location;
            var values = new List<Expression>();
            bool isDefault = false;

            // Collect every stacked `case`/`default` label before the first statement.
            while (Check(TokenType.Case) || Check(TokenType.Default))
            {
                if (Match(TokenType.Case))
                {
                    values.Add(Expression());
                    Consume(TokenType.Colon, "Expect ':' after case value.");
                }
                else
                {
                    Advance(); // consume 'default'
                    if (sawDefault)
                    {
                        throw Error(Previous(), "A 'switch' may only have one 'default' clause.");
                    }
                    isDefault = true;
                    sawDefault = true;
                    Consume(TokenType.Colon, "Expect ':' after 'default'.");
                }
            }

            if (values.Count == 0 && !isDefault)
            {
                throw Error(Peek(), "Expect 'case' or 'default' in switch body.");
            }

            var body = new List<Statement>();
            while (!Check(TokenType.Case) && !Check(TokenType.Default) && !Check(TokenType.RightBrace) && !IsAtEnd())
            {
                var decl = Declaration();
                if (decl != null) body.Add(decl);
            }

            cases.Add(new SwitchCase(values, body, isDefault, caseLoc));
        }

        Consume(TokenType.RightBrace, "Expect '}' after switch body.");
        return new SwitchStmt(discriminant, cases, loc);
    }

    private Statement FallthroughStatement()
    {
        Token keyword = Previous();
        Consume(TokenType.Semicolon, "Expect ';' after 'fallthrough'.");
        return new FallthroughStmt(keyword.Location);
    }

    // `exception Name;` declares a custom, nominally-named runtime exception type. Once
    // declared, `Name("description")` constructs a tagged exception (like the built-in
    // `Exception("...")`) and `catch (Name e)` filters for it. An optional
    // `= <default message expression>` (e.g. `exception PoopException = Exception("the
    // program shitted itself");`) lets `PoopException()` / a bare `throw PoopException;`
    // be used without repeating the message. See ExceptionTypeDeclStmt.
    private Statement ExceptionTypeDeclaration()
    {
        Token keyword = Previous();
        Token name = Consume(TokenType.Identifier, "Expect exception type name.");

        Expression? defaultMessage = null;
        if (Match(TokenType.Equals))
        {
            defaultMessage = Expression();
        }

        Consume(TokenType.Semicolon, "Expect ';' after exception type declaration.");
        return new ExceptionTypeDeclStmt(name, defaultMessage, keyword.Location);
    }

    private Statement TypeAliasStatement(bool isNewtype)
    {
        Token keyword = Previous();
        Token name = Consume(TokenType.Identifier, "Expect type alias name.");
        Consume(TokenType.Equals, "Expect '=' after type alias name.");
        TypeNode aliasedType = ParseType();
        Consume(TokenType.Semicolon, "Expect ';' after type alias declaration.");
        return new TypeAliasStmt(name, aliasedType, isNewtype, keyword.Location);
    }

    private Statement IfStatement()
    {
        SourceLocation loc = Previous().Location;
        Consume(TokenType.LeftParen, "Expect '(' after 'if'.");
        Expression condition = Expression();
        Consume(TokenType.RightParen, "Expect ')' after if condition.");

        Statement thenBranch = Statement();
        Statement? elseBranch = null;
        if (Match(TokenType.Else))
        {
            elseBranch = Statement();
        }

        return new IfStmt(condition, thenBranch, elseBranch, loc);
    }

    private Statement ReturnStatement()
    {
        Token keyword = Previous();
        Expression? value = null;
        if (!Check(TokenType.Semicolon))
        {
            value = Expression();
        }

        Consume(TokenType.Semicolon, "Expect ';' after return value.");
        return new ReturnStmt(value, keyword.Location);
    }

    private Statement BreakStatement()
    {
        Token keyword = Previous();
        Consume(TokenType.Semicolon, "Expect ';' after 'break'.");
        return new BreakStmt(keyword.Location);
    }

    private Statement ContinueStatement()
    {
        Token keyword = Previous();
        Consume(TokenType.Semicolon, "Expect ';' after 'continue'.");
        return new ContinueStmt(keyword.Location);
    }

    private Statement WhileStatement()
    {
        SourceLocation loc = Previous().Location;
        Consume(TokenType.LeftParen, "Expect '(' after 'while'.");
        Expression condition = Expression();
        Consume(TokenType.RightParen, "Expect ')' after while condition.");
        Statement body = Statement();

        return new WhileStmt(condition, body, loc);
    }

    private Statement ForStatement()
    {
        SourceLocation loc = Previous().Location;
        Consume(TokenType.LeftParen, "Expect '(' after 'for'.");

        Statement? initializer;
        if (Match(TokenType.Semicolon))
        {
            initializer = null;
        }
        else if (IsType(Peek()))
        {
            initializer = VarDeclaration();
        }
        else
        {
            initializer = ExpressionStatement();
        }

        Expression? condition = null;
        if (!Check(TokenType.Semicolon))
        {
            condition = Expression();
        }
        Consume(TokenType.Semicolon, "Expect ';' after loop condition.");

        Expression? increment = null;
        if (!Check(TokenType.RightParen))
        {
            increment = Expression();
        }
        Consume(TokenType.RightParen, "Expect ')' after for clauses.");

        Statement body = Statement();

        return new ForStmt(initializer, condition, increment, body, loc);
    }

    private Statement FreeStatement()
    {
        Token keyword = Previous();
        List<Expression> values = new List<Expression>();
        
        Consume(TokenType.LeftParen, "Expect '(' after 'free'.");
        if (!Check(TokenType.RightParen))
        {
            do
            {
                values.Add(Expression());
            } while (Match(TokenType.Comma));
        }
        Consume(TokenType.RightParen, "Expect ')' after arguments.");
        Consume(TokenType.Semicolon, "Expect ';' after free statement.");
        
        return new FreeStmt(values, keyword.Location);
    }

    private Statement Block()
    {
        SourceLocation loc = Previous().Location;
        List<Statement> statements = new List<Statement>();

        while (!Check(TokenType.RightBrace) && !IsAtEnd())
        {
            var decl = Declaration();
            if (decl != null) statements.Add(decl);
        }

        Consume(TokenType.RightBrace, "Expect '}' after block.");
        return new BlockStmt(statements, loc);
    }

    private Statement ExpressionStatement()
    {
        Expression expr = Expression();
        Consume(TokenType.Semicolon, "Expect ';' after expression.");
        return new ExpressionStmt(expr, expr.Location);
    }

    private Expression Expression()
    {
        return Assignment();
    }

    private Expression Conditional()
    {
        Expression expr = LogicalOr();

        if (Match(TokenType.Question))
        {
            Expression thenBranch = Conditional();
            Token colon = Consume(TokenType.Colon, "Expect ':' after '?' branch.");
            Expression elseBranch = Conditional();
            expr = new TernaryExpr(expr, thenBranch, elseBranch, expr.Location);
        }

        return expr;
    }

    private Expression Assignment()
    {
        Expression expr = Conditional();

        if (Match(TokenType.Equals, TokenType.PlusEquals, TokenType.MinusEquals, TokenType.StarEquals, TokenType.SlashEquals))
        {
            Token equals = Previous();
            Expression value = Assignment();

            if (equals.Type != TokenType.Equals)
            {
                value = MakeCompoundValue(expr, equals, value);
            }

            if (expr is VariableExpr)
            {
                // In a real ZV compiler, we'd have an AssignExpr.
                // Design doc shows UINT32 x = 10; as VarDecl.
                // Assignment x = 20; should be an expression.
                return new BinaryExpr(expr, new Token(TokenType.Equals, "=", null, equals.Location), value, expr.Location);
            }
            else if (expr is GetExpr getExpr)
            {
                return new SetExpr(getExpr.Object, getExpr.Name, value, expr.Location);
            }
            else if (expr is IndexExpr indexExpr)
            {
                return new SetIndexExpr(indexExpr.Target, indexExpr.Index, value, expr.Location);
            }

            Error(equals, "Invalid assignment target.");
        }

        return expr;
    }

    private Expression MakeCompoundValue(Expression target, Token op, Expression value)
    {
        TokenType binaryType = op.Type switch
        {
            TokenType.PlusEquals => TokenType.Plus,
            TokenType.MinusEquals => TokenType.Minus,
            TokenType.StarEquals => TokenType.Star,
            TokenType.SlashEquals => TokenType.Slash,
            _ => throw new Exception($"Unknown compound assignment operator: {op.Type}")
        };
        var binaryOp = new Token(binaryType, op.Lexeme.TrimEnd('='), null, op.Location);
        return new BinaryExpr(target, binaryOp, value, target.Location);
    }

    private Expression LogicalOr()
    {
        Expression expr = LogicalAnd();

        while (Match(TokenType.PipePipe))
        {
            Token op = Previous();
            Expression right = LogicalAnd();
            expr = new BinaryExpr(expr, op, right, expr.Location);
        }

        return expr;
    }

    private Expression LogicalAnd()
    {
        Expression expr = Equality();

        while (Match(TokenType.AmpersandAmpersand))
        {
            Token op = Previous();
            Expression right = Equality();
            expr = new BinaryExpr(expr, op, right, expr.Location);
        }

        return expr;
    }

    private Expression Equality()
    {
        Expression expr = Comparison();

        while (Match(TokenType.BangEquals, TokenType.EqualsEquals))
        {
            Token op = Previous();
            Expression right = Comparison();
            expr = new BinaryExpr(expr, op, right, expr.Location);
        }

        return expr;
    }

    private Expression Comparison()
    {
        Expression expr = Term();

        while (Match(TokenType.Greater, TokenType.GreaterEquals, TokenType.Less, TokenType.LessEquals))
        {
            Token op = Previous();
            Expression right = Term();
            expr = new BinaryExpr(expr, op, right, expr.Location);
        }

        return expr;
    }

    private Expression Term()
    {
        Expression expr = Factor();

        while (Match(TokenType.Minus, TokenType.Plus))
        {
            Token op = Previous();
            Expression right = Factor();
            expr = new BinaryExpr(expr, op, right, expr.Location);
        }

        return expr;
    }

    private Expression Factor()
    {
        Expression expr = Unary();

        while (true)
        {
            Token op;
            if (Match(TokenType.Slash, TokenType.Star, TokenType.Percent, TokenType.Ampersand, TokenType.Pipe, TokenType.Caret, TokenType.LessLess))
            {
                op = Previous();
            }
            else if (TryMatchRightShift(out op))
            {
                // consumed by TryMatchRightShift
            }
            else
            {
                break;
            }

            Expression right = Unary();
            expr = new BinaryExpr(expr, op, right, expr.Location);
        }

        return expr;
    }

    // '>>' is never lexed as a single token (see Lexer) because nested generics like
    // PTR<PTR<VOID>> need each '>' to be its own token. Here, in ordinary binary-expression
    // context, two adjacent '>' tokens with no gap between them (same line, consecutive
    // columns) are treated as a right-shift operator instead.
    private bool TryMatchRightShift(out Token op)
    {
        op = null!;
        if (!Check(TokenType.Greater) || PeekNext().Type != TokenType.Greater)
        {
            return false;
        }

        Token first = Peek();
        Token second = PeekNext();
        if (second.Location.Line != first.Location.Line || second.Location.Column != first.Location.Column + 1)
        {
            return false;
        }

        Advance();
        Advance();
        op = new Token(TokenType.GreaterGreater, ">>", null, first.Location);
        return true;
    }

    private Expression Unary()
    {
        if (Match(TokenType.Bang, TokenType.Minus, TokenType.Tilde, TokenType.PlusPlus, TokenType.MinusMinus))
        {
            Token op = Previous();
            Expression right = Unary();
            return new UnaryExpr(op, right, op.Location);
        }

        Expression expr = Call();

        if (Match(TokenType.As))
        {
            TypeNode type = ParseType();
            expr = new CastExpr(expr, type, expr.Location);
        }

        return expr;
    }

    private Expression Call()
    {
        Expression expr = Primary();

        while (true)
        {
            if (Match(TokenType.LeftParen))
            {
                expr = FinishCall(expr);
            }
            else if (Match(TokenType.Dot))
            {
                Token name = Consume(TokenType.Identifier, "Expect property name after '.'.");
                expr = new GetExpr(expr, name, expr.Location);
            }
            else if (Match(TokenType.LeftBracket))
            {
                Expression index = Expression();
                Consume(TokenType.RightBracket, "Expect ']' after index.");
                expr = new IndexExpr(expr, index, expr.Location);
            }
            else
            {
                break;
            }
        }

        if (Match(TokenType.PlusPlus, TokenType.MinusMinus))
        {
            Token op = Previous();
            expr = new PostfixExpr(expr, op, expr.Location);
        }

        return expr;
    }

    private Expression FinishCall(Expression callee)
    {
        List<Expression> arguments = new List<Expression>();
        if (!Check(TokenType.RightParen))
        {
            do
            {
                arguments.Add(Expression());
            } while (Match(TokenType.Comma));
        }

        Token paren = Consume(TokenType.RightParen, "Expect ')' after arguments.");

        return new CallExpr(callee, arguments, callee.Location);
    }

    private Expression Primary()
    {
        if (Match(TokenType.False)) return new LiteralExpr(false, TokenType.False, Previous().Location);
        if (Match(TokenType.True)) return new LiteralExpr(true, TokenType.True, Previous().Location);
        if (Match(TokenType.Null)) return new LiteralExpr(null, TokenType.Null, Previous().Location);

        if (Match(TokenType.IntegerLiteral, TokenType.FloatLiteral, TokenType.StringLiteral, TokenType.CharacterLiteral))
        {
            return new LiteralExpr(Previous().Literal, Previous().Type, Previous().Location);
        }

        if (Match(TokenType.Identifier))
        {
            Token identifierToken = Previous();

            // Explicitly-typed struct literal: `Point { x = 1, y = 2 }`. Safe to recognize
            // here because every other construct that puts a block right after an
            // identifier-like token (struct/extern bodies, function/if/while/for bodies,
            // catch blocks, ...) consumes its own '{' directly at the statement level
            // rather than through an expression, so there's no ambiguity with a bare
            // identifier expression immediately followed by '{'.
            if (Match(TokenType.LeftBrace))
            {
                return FinishStructInit(identifierToken);
            }

            return new VariableExpr(identifierToken.Lexeme, identifierToken.Location);
        }

        if (Match(TokenType.EXCEPTION))
        {
            return new VariableExpr("Exception", Previous().Location);
        }

        // Heap array allocation expression: T[N] or T[N](value) (primitive types only to avoid ambiguity)
        if (IsType(Peek()) && Peek().Type != TokenType.Identifier && PeekNext().Type == TokenType.LeftBracket)
        {
            var type = ParseType();
            if (type is FixedSizeArrayTypeNode fixedArr)
            {
                Expression? fillValue = null;
                if (Match(TokenType.LeftParen))
                {
                    fillValue = Expression();
                    Consume(TokenType.RightParen, "Expect ')' after fill value.");
                }
                return new ArrayAllocExpr(fixedArr.BaseType, fixedArr.Size, fillValue, type.Location);
            }
            throw Error(Previous(), "Unexpected type in expression.");
        }

        if (Match(TokenType.LeftParen))
        {
            Expression expr = Expression();
            Consume(TokenType.RightParen, "Expect ')' after expression.");
            return new GroupingExpr(expr, Previous().Location);
        }

        if (Match(TokenType.LeftBracket))
        {
            SourceLocation loc = Previous().Location;
            List<Expression> elements = new List<Expression>();
            if (!Check(TokenType.RightBracket))
            {
                do
                {
                    elements.Add(Expression());
                } while (Match(TokenType.Comma));
            }
            Consume(TokenType.RightBracket, "Expect ']' after array initializer.");
            return new ArrayInitExpr(elements, loc);
        }

        if (Match(TokenType.LeftBrace))
        {
            return FinishStructInit(null);
        }

        throw Error(Peek(), "Expect expression.");
    }

    // Parses the body of a struct literal (`{ name = value, ... }`) with the opening '{'
    // already consumed. `typeName` is the explicit type token for `Type { ... }` literals,
    // or null for the bare-brace form.
    private Expression FinishStructInit(Token? typeName)
    {
        SourceLocation loc = typeName?.Location ?? Previous().Location;
        List<(Token Name, Expression Value)> fields = new();
        if (!Check(TokenType.RightBrace))
        {
            do
            {
                Token name = Consume(TokenType.Identifier, "Expect field name in struct initializer.");
                Consume(TokenType.Equals, "Expect '=' after field name.");
                Expression value = Expression();
                fields.Add((name, value));
            } while (Match(TokenType.Comma));
        }
        Consume(TokenType.RightBrace, "Expect '}' after struct initializer.");
        return new StructInitExpr(fields, loc, typeName);
    }

    private bool Match(params TokenType[] types)
    {
        foreach (TokenType type in types)
        {
            if (Check(type))
            {
                Advance();
                return true;
            }
        }
        return false;
    }

    private Token Consume(TokenType type, string message)
    {
        if (Check(type)) return Advance();
        throw Error(Peek(), message);
    }

    private Token ConsumeType(string message)
    {
        if (IsType(Peek())) return Advance();
        throw Error(Peek(), message);
    }

    private bool IsType(Token token)
    {
        if (token.Type >= TokenType.INT8 && token.Type <= TokenType.PROCESS) return true;
        if (token.Type == TokenType.Identifier) return true;
        return false;
    }

    private bool Check(TokenType type)
    {
        if (IsAtEnd()) return false;
        return Peek().Type == type;
    }

    private Token Advance()
    {
        if (!IsAtEnd()) current++;
        return Previous();
    }

    private bool IsAtEnd()
    {
        return Peek().Type == TokenType.EndOfFile;
    }

    private Token Peek()
    {
        return tokens[current];
    }

    private Token PeekNext()
    {
        if (current + 1 >= tokens.Count) return tokens[tokens.Count - 1];
        return tokens[current + 1];
    }
    
    private Token PeekNextNext()
    {
        if (current + 2 >= tokens.Count) return tokens[tokens.Count - 1];
        return tokens[current + 2];
    }

    private Token Previous()
    {
        return tokens[current - 1];
    }

    private ParseError Error(Token token, string message)
    {
        ReportError(token, message);
        return new ParseError();
    }

    private void ReportError(Token token, string message)
    {
        string location = token.Type == TokenType.EndOfFile ? "at end" : $"at '{token.Lexeme}'";
        Console.Error.WriteLine($"[{token.Location.File ?? fileName}:{token.Location.Line}:{token.Location.Column}] Error {location}: {message}");
        HadError = true;
        Errors.Add((token.Location, $"Error {location}: {message}"));
    }

    private void Synchronize()
    {
        Advance();

        while (!IsAtEnd())
        {
            if (Previous().Type == TokenType.Semicolon) return;

            switch (Peek().Type)
            {
                // We don't have Class yet, but Struct
                case TokenType.Struct:
                case TokenType.If:
                case TokenType.While:
                case TokenType.For:
                case TokenType.Return:
                case TokenType.Break:
                case TokenType.Continue:
                case TokenType.Try:
                case TokenType.Throw:
                case TokenType.Type:
                case TokenType.Newtype:
                case TokenType.Free:
                case TokenType.Unsafe:
                    return;
            }

            if (IsType(Peek())) return;

            Advance();
        }
    }
}
