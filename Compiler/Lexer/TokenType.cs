namespace ZV.Compiler.Lexer;

public enum TokenType
{
    // Single-character tokens
    LeftParen, RightParen,
    LeftBrace, RightBrace,
    LeftBracket, RightBracket,
    Comma, Dot, Semicolon,
    Plus, Minus, Star, Slash, Percent,
    Equals, Ampersand, Pipe, Caret, Tilde,
    Bang, Less, Greater, Question, Colon,

    // Two or three character tokens
    EqualsEquals, BangEquals,
    LessEquals, GreaterEquals,
    PlusEquals, MinusEquals, StarEquals, SlashEquals,
    PlusPlus, MinusMinus,
    Arrow, // ->
    AmpersandAmpersand, PipePipe,
    LessLess, GreaterGreater, // <<, >> (bitwise shifts)

    // Literals
    Identifier,
    StringLiteral,
    CharacterLiteral,
    IntegerLiteral,
    FloatLiteral,

    // Keywords
    Type,       // type
    Newtype,    // newtype
    Struct,     // struct
    If,         // if
    Else,       // else
    While,      // while
    For,        // for
    Return,     // return
    Break,      // break
    Continue,   // continue
    Try,        // try
    Catch,      // catch
    Throw,      // throw
    Switch,     // switch
    Case,       // case
    Default,    // default
    Fallthrough, // fallthrough
    ExceptionDecl, // exception (declares a custom exception type; distinct from the
                   // "Exception"/"EXCEPTION" builtin type keyword, EXCEPTION below)
    Free,       // free
    True,       // true
    False,      // false
    Null,       // null
    Extern,     // extern
    Export,     // export
    As,         // as
    Packed,     // packed
    Const,      // CONST
    Unsafe,     // unsafe
    DefineDirective,
    Attribute,        // @entry, @export, @packed, etc.

    // Primitive Types
    INT8, INT16, INT32, INT64, INT128,
    UINT8, UINT16, UINT32, UINT64, UINT128,
    FLOAT32, FLOAT64,
    BOOL, CHAR, VOID,
    STRING, CSTRING, WSTRING,
    PTR,
    FuncPtr,    // FUNCPTR<ReturnType(ParamType, ...)> - a pointer to a function of that signature
    EXCEPTION,
    PROCESS,
    EndOfFile
}
