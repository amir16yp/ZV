using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace ZV.Compiler.Lexer;

public class Lexer
{
    // Returns the default system include search paths used for #include <...>.
    // The environment variable ZV_INCLUDE_PATH can override/add entries using
    // the platform path separator. After that, the following are checked in
    // order: a portable install (lib/ next to the compiler binary), the
    // per-user install location, and common Unix system-wide install locations.
    public static IReadOnlyList<string> GetDefaultSystemIncludePaths()
    {
        var paths = new List<string>();

        string? envVar = Environment.GetEnvironmentVariable("ZV_INCLUDE_PATH");
        if (!string.IsNullOrEmpty(envVar))
        {
            foreach (var raw in envVar.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            {
                paths.Add(Path.GetFullPath(raw.Trim()));
            }
        }

        string? exeDir = Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location);
        if (!string.IsNullOrEmpty(exeDir) && Directory.Exists(Path.Combine(exeDir, "lib")))
        {
            paths.Add(exeDir);
        }

        // Per-user install location.
        if (OperatingSystem.IsWindows())
        {
            string userRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ZV");
            if (Directory.Exists(Path.Combine(userRoot, "lib")))
            {
                paths.Add(userRoot);
            }
        }
        else
        {
            string userRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share", "zv");
            if (Directory.Exists(Path.Combine(userRoot, "lib")))
            {
                paths.Add(userRoot);
            }

            // System-wide install locations (Unix package managers / make install).
            foreach (var root in new[] { "/usr/local/lib/zv", "/usr/lib/zv", "/usr/local/share/zv", "/usr/share/zv" })
            {
                if (Directory.Exists(Path.Combine(root, "lib")))
                {
                    paths.Add(root);
                }
            }
        }

        return paths.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private readonly string _source;
    private readonly string? _fileName;
    private readonly List<Token> _tokens = new();
    private readonly HashSet<string> _includedFiles;
    private readonly Dictionary<string, string> _defines;
    private readonly Func<string, string?>? _fileProvider;
    private readonly IReadOnlyList<string> _systemIncludePaths;
    private int _start = 0;
    private int _current = 0;
    private int _line = 1;
    private int _column = 1;

    private static readonly Dictionary<string, TokenType> Keywords = new()
    {
        { "type", TokenType.Type },
        { "newtype", TokenType.Newtype },
        { "struct", TokenType.Struct },
        { "if", TokenType.If },
        { "else", TokenType.Else },
        { "while", TokenType.While },
        { "for", TokenType.For },
        { "return", TokenType.Return },
        { "break", TokenType.Break },
        { "continue", TokenType.Continue },
        { "try", TokenType.Try },
        { "catch", TokenType.Catch },
        { "throw", TokenType.Throw },
        { "exception", TokenType.ExceptionDecl },
        { "free", TokenType.Free },
        { "true", TokenType.True },
        { "false", TokenType.False },
        { "null", TokenType.Null },
        { "extern", TokenType.Extern },
        { "export", TokenType.Export },
        { "as", TokenType.As },
        { "packed", TokenType.Packed },
        { "CONST", TokenType.Const },
        { "const", TokenType.Const },
        { "unsafe", TokenType.Unsafe },
        { "string", TokenType.STRING },
        { "STRING", TokenType.STRING },
        { "cstring", TokenType.CSTRING },
        { "CSTRING", TokenType.CSTRING },
        { "wstring", TokenType.WSTRING },
        { "WSTRING", TokenType.WSTRING },
        { "INT8", TokenType.INT8 },
        { "INT16", TokenType.INT16 },
        { "INT32", TokenType.INT32 },
        { "INT64", TokenType.INT64 },
        { "INT128", TokenType.INT128 },
        { "UINT8", TokenType.UINT8 },
        { "UINT16", TokenType.UINT16 },
        { "UINT32", TokenType.UINT32 },
        { "UINT64", TokenType.UINT64 },
        { "UINT128", TokenType.UINT128 },
        { "FLOAT32", TokenType.FLOAT32 },
        { "FLOAT64", TokenType.FLOAT64 },
        { "BOOL", TokenType.BOOL },
        { "CHAR", TokenType.CHAR },
        { "VOID", TokenType.VOID },
        { "PTR", TokenType.PTR },
        { "int8", TokenType.INT8 },
        { "int16", TokenType.INT16 },
        { "int32", TokenType.INT32 },
        { "int64", TokenType.INT64 },
        { "int128", TokenType.INT128 },
        { "uint8", TokenType.UINT8 },
        { "uint16", TokenType.UINT16 },
        { "uint32", TokenType.UINT32 },
        { "uint64", TokenType.UINT64 },
        { "uint128", TokenType.UINT128 },
        { "float32", TokenType.FLOAT32 },
        { "float64", TokenType.FLOAT64 },
        { "bool", TokenType.BOOL },
        { "char", TokenType.CHAR },
        { "void", TokenType.VOID },
        { "Exception", TokenType.EXCEPTION },
        { "EXCEPTION", TokenType.EXCEPTION },
        { "PROCESS", TokenType.PROCESS }
    };

    public Lexer(string source, string? fileName = null, HashSet<string>? includedFiles = null, Dictionary<string, string>? defines = null, Func<string, string?>? fileProvider = null, IReadOnlyList<string>? systemIncludePaths = null)
    {
        _source = source;
        _fileName = fileName;
        _includedFiles = includedFiles ?? new HashSet<string>();
        _defines = defines ?? new Dictionary<string, string>();
        _fileProvider = fileProvider;
        _systemIncludePaths = systemIncludePaths ?? new List<string>();
        if (_fileName != null) _includedFiles.Add(Path.GetFullPath(_fileName));
    }

    public IReadOnlyCollection<string> IncludedFiles => _includedFiles;

    public List<Token> ScanTokens()
    {
        while (!IsAtEnd())
        {
            _start = _current;
            ScanToken();
        }

        _tokens.Add(new Token(TokenType.EndOfFile, "", null, GetLocation()));
        return _tokens;
    }

    private void ScanToken()
    {
        char c = Advance();
        switch (c)
        {
            case '(': AddToken(TokenType.LeftParen); break;
            case ')': AddToken(TokenType.RightParen); break;
            case '{': AddToken(TokenType.LeftBrace); break;
            case '}': AddToken(TokenType.RightBrace); break;
            case '[': AddToken(TokenType.LeftBracket); break;
            case ']': AddToken(TokenType.RightBracket); break;
            case ',': AddToken(TokenType.Comma); break;
            case '.': AddToken(TokenType.Dot); break;
            case ';': AddToken(TokenType.Semicolon); break;
            case '+':
                if (Match('+')) AddToken(TokenType.PlusPlus);
                else if (Match('=')) AddToken(TokenType.PlusEquals);
                else AddToken(TokenType.Plus);
                break;
            case '-':
                if (Match('-')) AddToken(TokenType.MinusMinus);
                else if (Match('=')) AddToken(TokenType.MinusEquals);
                else if (Match('>')) AddToken(TokenType.Arrow);
                else AddToken(TokenType.Minus);
                break;
            case '*':
                AddToken(Match('=') ? TokenType.StarEquals : TokenType.Star);
                break;
            case '/':
                if (Match('/'))
                {
                    // A comment goes until the end of the line.
                    while (Peek() != '\n' && !IsAtEnd()) Advance();
                }
                else if (Match('*'))
                {
                    BlockComment();
                }
                else
                {
                    AddToken(Match('=') ? TokenType.SlashEquals : TokenType.Slash);
                }
                break;
            case '%': AddToken(TokenType.Percent); break;
            case '=':
                AddToken(Match('=') ? TokenType.EqualsEquals : TokenType.Equals);
                break;
            case '!':
                AddToken(Match('=') ? TokenType.BangEquals : TokenType.Bang);
                break;
            case '<':
                if (Match('<')) AddToken(TokenType.LessLess);
                else AddToken(Match('=') ? TokenType.LessEquals : TokenType.Less);
                break;
            case '>':
                // Deliberately NOT lexing '>>' as a single token here: nested generics like
                // PTR<PTR<VOID>> rely on each '>' being its own token so ParseType can consume
                // them one at a time. The parser synthesizes a right-shift token from two
                // adjacent '>' tokens only in binary-expression context (see Parser.Factor).
                AddToken(Match('=') ? TokenType.GreaterEquals : TokenType.Greater);
                break;
            case '&':
                AddToken(Match('&') ? TokenType.AmpersandAmpersand : TokenType.Ampersand);
                break;
            case '|':
                AddToken(Match('|') ? TokenType.PipePipe : TokenType.Pipe);
                break;
            case '^': AddToken(TokenType.Caret); break;
            case '~': AddToken(TokenType.Tilde); break;
            case '?': AddToken(TokenType.Question); break;
            case ':': AddToken(TokenType.Colon); break;
            case '#': HandleDirective(); break;
            case '@': HandleAttribute(); break;

            case ' ':
            case '\r':
            case '\t':
                // Ignore whitespace.
                break;

            case '\n':
                _line++;
                _column = 1;
                break;

            case '"': String(); break;
            case '\'': Character(); break;

            default:
                if (IsDigit(c))
                {
                    Number();
                }
                else if (IsAlpha(c))
                {
                    Identifier();
                }
                else
                {
                    // Handle unexpected character (should probably report an error)
                }
                break;
        }
    }

    private void HandleDirective()
    {
        while (IsAlphaNumeric(Peek())) Advance();
        string directive = _source[(_start + 1).._current];

        if (directive == "include")
        {
            // Skip whitespace between "include" and the path delimiter.
            while (!IsAtEnd() && (Peek() == ' ' || Peek() == '\t')) Advance();

            // #include can use either "path" (local search) or <path> (system search).
            char open = Peek();
            if (open != '"' && open != '<') return;

            char close = open == '"' ? '"' : '>';
            int pathStart = _current + 1;
            Advance(); // Consume opening delimiter
            while (Peek() != close && !IsAtEnd()) Advance();
            if (IsAtEnd()) return;

            string includePath = _source[pathStart.._current];
            Advance(); // Consume closing delimiter

            string? fullPath = ResolveIncludePath(includePath, open == '<');
            if (fullPath == null) return;

            if (!_includedFiles.Contains(fullPath))
            {
                string? includedSource = _fileProvider != null
                    ? _fileProvider(fullPath)
                    : (File.Exists(fullPath) ? File.ReadAllText(fullPath) : null);

                if (includedSource != null)
                {
                    _includedFiles.Add(fullPath);
                    var lexer = new Lexer(includedSource, fullPath, _includedFiles, _defines, _fileProvider, _systemIncludePaths);
                    var includedTokens = lexer.ScanTokens();
                    // Remove EndOfFile token from included tokens
                    if (includedTokens.Count > 0 && includedTokens[^1].Type == TokenType.EndOfFile)
                    {
                        includedTokens.RemoveAt(includedTokens.Count - 1);
                    }
                    _tokens.AddRange(includedTokens);
                }
            }
        }
        else if (directive == "define")
        {
            // Skip whitespace after #define
            while (!IsAtEnd() && (Peek() == ' ' || Peek() == '\t')) Advance();

            // Read the macro name
            int nameStart = _current;
            while (!IsAtEnd() && IsAlphaNumeric(Peek())) Advance();
            string macroName = _source[nameStart.._current];

            // Skip whitespace between name and value
            while (!IsAtEnd() && (Peek() == ' ' || Peek() == '\t')) Advance();

            // Read the rest of the line as the value
            int valueStart = _current;
            while (!IsAtEnd() && Peek() != '\n' && Peek() != '\r') Advance();
            string macroValue = _source[valueStart.._current].Trim();

            _defines[macroName] = macroValue;
        }
        else
        {
            // Backtrack if not a known directive so it can be handled as something else or ignored
            // For now, ZV seems to only use # for directives.
        }
    }

    public static string? ResolveIncludePath(string includePath, bool systemInclude, string? currentFilePath, IReadOnlyList<string> systemIncludePaths)
    {
        if (systemInclude)
        {
            foreach (var dir in systemIncludePaths)
            {
                string candidate = Path.GetFullPath(Path.Combine(dir, includePath));
                if (File.Exists(candidate)) return candidate;
            }
            // Fall back to the local directory if the file was not found in any
            // system include path, matching common C compiler behavior.
            string? localDir = currentFilePath != null ? Path.GetDirectoryName(currentFilePath) : Directory.GetCurrentDirectory();
            string localCandidate = Path.GetFullPath(Path.Combine(localDir ?? "", includePath));
            if (File.Exists(localCandidate)) return localCandidate;
            return null;
        }

        string? currentDir = currentFilePath != null ? Path.GetDirectoryName(currentFilePath) : Directory.GetCurrentDirectory();
        return Path.GetFullPath(Path.Combine(currentDir ?? "", includePath));
    }

    private string? ResolveIncludePath(string includePath, bool systemInclude)
    {
        return ResolveIncludePath(includePath, systemInclude, _fileName, _systemIncludePaths);
    }

    private void HandleAttribute()
    {
        while (IsAlphaNumeric(Peek())) Advance();
        string attrName = _source[(_start + 1).._current];
        if (string.IsNullOrEmpty(attrName))
        {
            // Bare '@' with no name - error, but just ignore for now
            return;
        }
        AddToken(TokenType.Attribute, attrName);
    }

    private void Identifier()
    {
        while (IsAlphaNumeric(Peek())) Advance();
        string text = _source[_start.._current];
        
        // Check if this identifier is a #define'd macro
        if (_defines.TryGetValue(text, out string? macroValue))
        {
            // Re-lex the macro value to produce the correct tokens
            var macroLexer = new Lexer(macroValue, _fileName, _includedFiles, _defines, systemIncludePaths: _systemIncludePaths);
            var macroTokens = macroLexer.ScanTokens();
            // Remove EndOfFile token
            if (macroTokens.Count > 0 && macroTokens[^1].Type == TokenType.EndOfFile)
            {
                macroTokens.RemoveAt(macroTokens.Count - 1);
            }
            _tokens.AddRange(macroTokens);
            return;
        }
        
        if (!Keywords.TryGetValue(text, out TokenType type))
        {
            type = TokenType.Identifier;
        }
        AddToken(type);
    }

    private void Number()
    {
        if (_source[_current - 1] == '0' && (Peek() == 'x' || Peek() == 'X'))
        {
            Advance(); // consume 'x'
            while (IsHexDigit(Peek()) || Peek() == '_') Advance();
            string value = _source[_start.._current].Replace("_", "");
            AddToken(TokenType.IntegerLiteral, Convert.ToInt64(value, 16));
            return;
        }

        while (IsDigit(Peek()) || Peek() == '_') Advance();

        // Look for a fractional part.
        if (Peek() == '.' && (IsDigit(PeekNext()) || PeekNext() == '_'))
        {
            // Consume the "."
            Advance();

            while (IsDigit(Peek()) || Peek() == '_') Advance();
            AddToken(TokenType.FloatLiteral, double.Parse(_source[_start.._current].Replace("_", "")));
        }
        else
        {
            AddToken(TokenType.IntegerLiteral, long.Parse(_source[_start.._current].Replace("_", "")));
        }
    }

    private bool IsHexDigit(char c)
    {
        return (c >= '0' && c <= '9') ||
               (c >= 'a' && c <= 'f') ||
               (c >= 'A' && c <= 'F');
    }

    private string UnescapeString(string value)
    {
        var sb = new System.Text.StringBuilder(value.Length);
        for (int i = 0; i < value.Length; i++)
        {
            if (value[i] == '\\' && i + 1 < value.Length)
            {
                char next = value[i + 1];
                if (next == 'n') { sb.Append('\n'); i++; }
                else if (next == 't') { sb.Append('\t'); i++; }
                else if (next == 'r') { sb.Append('\r'); i++; }
                else if (next == '\\') { sb.Append('\\'); i++; }
                else if (next == '"') { sb.Append('"'); i++; }
                else if (next == '0') { sb.Append('\0'); i++; }
                else { sb.Append(value[i]); }
            }
            else
            {
                sb.Append(value[i]);
            }
        }
        return sb.ToString();
    }

    private void String()
    {
        while (Peek() != '"' && !IsAtEnd())
        {
            if (Peek() == '\n')
            {
                _line++;
                _column = 1;
            }
            if (Peek() == '\\' && PeekNext() == '"')
            {
                Advance(); // Consume \
            }
            Advance();
        }

        if (IsAtEnd())
        {
            // Error: Unterminated string.
            return;
        }

        // The closing ".
        Advance();

        // Trim the surrounding quotes.
        string value = _source[(_start + 1)..(_current - 1)];
        // Unescape string literal value
        value = UnescapeString(value);
        AddToken(TokenType.StringLiteral, value);
    }

    private void Character()
    {
        // Simple character literal implementation
        if (Peek() != '\'' && !IsAtEnd())
        {
            if (Peek() == '\\') Advance(); // handle escape
            Advance();
        }

        if (Peek() == '\'')
        {
            Advance();
            string value = _source[(_start + 1)..(_current - 1)];
            value = UnescapeString(value);
            char charValue = value.Length > 0 ? value[0] : '\0';
            AddToken(TokenType.CharacterLiteral, charValue);
        }
        else
        {
            // Error: Unterminated character literal.
        }
    }

    private void BlockComment()
    {
        while (!IsAtEnd())
        {
            if (Peek() == '*' && PeekNext() == '/')
            {
                Advance(); // *
                Advance(); // /
                return;
            }
            if (Peek() == '\n')
            {
                _line++;
                _column = 1;
            }
            Advance();
        }
        // Error: Unterminated block comment.
    }

    private bool IsAtEnd() => _current >= _source.Length;

    private char Advance()
    {
        char c = _source[_current++];
        _column++;
        return c;
    }

    private bool Match(char expected)
    {
        if (IsAtEnd()) return false;
        if (_source[_current] != expected) return false;

        _current++;
        _column++;
        return true;
    }

    private char Peek()
    {
        if (IsAtEnd()) return '\0';
        return _source[_current];
    }

    private char PeekNext()
    {
        if (_current + 1 >= _source.Length) return '\0';
        return _source[_current + 1];
    }

    private bool IsAlpha(char c) => (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || c == '_';
    private bool IsDigit(char c) => c >= '0' && c <= '9';
    private bool IsAlphaNumeric(char c) => IsAlpha(c) || IsDigit(c);

    private void AddToken(TokenType type) => AddToken(type, null);

    private void AddToken(TokenType type, object? literal)
    {
        string text = _source[_start.._current];
        _tokens.Add(new Token(type, text, literal, GetLocation(_start)));
    }

    private SourceLocation GetLocation(int offset = -1)
    {
        if (offset == -1) offset = _current;
        // This is a bit simplified; real column tracking during ScanToken is better.
        // I've added _column and _line updates in Advance() and Match().
        // For the 'start' of a token, we'd need to calculate its column.
        int col = _column - (_current - _start);
        return new SourceLocation(_fileName, _line, col, _start);
    }
}
