using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using LLVMSharp.Interop;
using ZV.Compiler.AST;
using ZV.Compiler.Lexer;

namespace ZV.Compiler.Backend;

// Universal curses builtins: a thin, cross-platform wrapper over the native curses
// library (ncurses on Linux/macOS, PDCurses on Windows) for building terminal UIs.
// These require a hosted OS with a real terminal, so they are rejected outright when
// compiling for a freestanding/kernel target (e.g. the "os-x86" target) - see
// LlvmGenerator.IsFreestandingTarget and CheckCursesAvailable() below.
public partial class LlvmGenerator
{
    private static readonly HashSet<string> CursesBuiltinNames = new()
    {
        "curses_init", "curses_end", "curses_refresh", "curses_clear", "curses_erase",
        "curses_move", "curses_printw", "curses_mvprintw", "curses_addch", "curses_getch",
        "curses_echo", "curses_noecho", "curses_cbreak", "curses_nocbreak", "curses_raw",
        "curses_curs_set", "curses_keypad", "curses_nodelay", "curses_start_color",
        "curses_init_pair", "curses_color_pair", "curses_attron", "curses_attroff",
        "curses_box", "curses_rows", "curses_cols"
    };

    private bool _cursesLibraryLinked;

    // Rejects curses builtins on freestanding/kernel targets and, otherwise, makes sure
    // the platform-appropriate curses library gets linked in.
    private void CheckCursesAvailable(string name)
    {
        if (IsFreestandingTarget)
        {
            throw new Exception($"'{name}' is a curses builtin and is not available when targeting 'os-x86': " +
                                 "kernel/freestanding builds have no hosted terminal for curses to drive.");
        }

        if (!_cursesLibraryLinked)
        {
            _cursesLibraryLinked = true;
            _externalLibraries.Add(RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "pdcurses" : "ncurses");
        }
    }

    // WINDOW* pointers are treated as opaque PTR<VOID> i8* handles, same as
    // FILE* in the fopen/fclose builtins.
    private LLVMValueRef GetStdscrPtr()
    {
        var windowPtrType = GetPointerType(GetInt8Type());
        var global = _module.GetNamedGlobal("stdscr");
        if (global.Handle == IntPtr.Zero)
        {
            global = _module.AddGlobal(windowPtrType, "stdscr");
            global.Linkage = LLVMLinkage.LLVMExternalLinkage;
        }
        return _builder.BuildLoad2(windowPtrType, global, "stdscr_val");
    }

    private LLVMValueRef GetCursesGlobalInt(string name)
    {
        var global = _module.GetNamedGlobal(name);
        if (global.Handle == IntPtr.Zero)
        {
            global = _module.AddGlobal(GetInt32Type(), name);
            global.Linkage = LLVMLinkage.LLVMExternalLinkage;
        }
        return _builder.BuildLoad2(GetInt32Type(), global, name.ToLowerInvariant() + "_val");
    }

    private LLVMValueRef GenerateCursesBuiltinCall(string name, List<Expression> arguments)
    {
        CheckCursesAvailable(name);

        return name switch
        {
            "curses_init" => GenerateCursesInitCall(arguments),
            "curses_end" => GenerateCursesEndCall(arguments),
            "curses_refresh" => GenerateCursesSimpleCall("refresh", arguments),
            "curses_clear" => GenerateCursesSimpleCall("clear", arguments),
            "curses_erase" => GenerateCursesSimpleCall("erase", arguments),
            "curses_echo" => GenerateCursesSimpleCall("echo", arguments),
            "curses_noecho" => GenerateCursesSimpleCall("noecho", arguments),
            "curses_cbreak" => GenerateCursesSimpleCall("cbreak", arguments),
            "curses_nocbreak" => GenerateCursesSimpleCall("nocbreak", arguments),
            "curses_raw" => GenerateCursesSimpleCall("raw", arguments),
            "curses_start_color" => GenerateCursesSimpleCall("start_color", arguments),
            "curses_move" => GenerateCursesMoveCall(arguments),
            "curses_printw" => GenerateCursesPrintwCall("printw", arguments, 0),
            "curses_mvprintw" => GenerateCursesPrintwCall("mvprintw", arguments, 2),
            "curses_addch" => GenerateCursesAddchCall(arguments),
            "curses_getch" => GenerateCursesGetchCall(arguments),
            "curses_curs_set" => GenerateCursesIntArgCall("curs_set", arguments),
            "curses_keypad" => GenerateCursesWindowBoolCall("keypad", arguments),
            "curses_nodelay" => GenerateCursesWindowBoolCall("nodelay", arguments),
            "curses_init_pair" => GenerateCursesInitPairCall(arguments),
            "curses_color_pair" => GenerateCursesColorPairCall(arguments),
            "curses_attron" => GenerateCursesAttrCall("attron", arguments),
            "curses_attroff" => GenerateCursesAttrCall("attroff", arguments),
            "curses_box" => GenerateCursesBoxCall(arguments),
            "curses_rows" => GetCursesGlobalInt("LINES"),
            "curses_cols" => GetCursesGlobalInt("COLS"),
            _ => throw new Exception($"Unknown curses builtin: {name}")
        };
    }

    private LLVMValueRef GenerateCursesInitCall(List<Expression> arguments)
    {
        if (arguments.Count != 0)
            throw new Exception("curses_init() takes no arguments.");

        var initscr = GetOrAddFunction("initscr", GetPointerType(GetInt8Type()), Array.Empty<LLVMTypeRef>());
        return _builder.BuildCall2(_functionTypes["initscr"], initscr, Array.Empty<LLVMValueRef>(), "cursesinittmp");
    }

    private LLVMValueRef GenerateCursesEndCall(List<Expression> arguments)
    {
        if (arguments.Count != 0)
            throw new Exception("curses_end() takes no arguments.");

        var endwin = GetOrAddFunction("endwin", GetInt32Type(), Array.Empty<LLVMTypeRef>());
        return _builder.BuildCall2(_functionTypes["endwin"], endwin, Array.Empty<LLVMValueRef>(), "cursesendtmp");
    }

    // Calls a niladic curses function that returns int (refresh, clear, erase, echo,
    // noecho, cbreak, nocbreak, raw, start_color).
    private LLVMValueRef GenerateCursesSimpleCall(string nativeName, List<Expression> arguments)
    {
        if (arguments.Count != 0)
            throw new Exception($"curses_{nativeName}() takes no arguments.");

        var func = GetOrAddFunction(nativeName, GetInt32Type(), Array.Empty<LLVMTypeRef>());
        return _builder.BuildCall2(_functionTypes[nativeName], func, Array.Empty<LLVMValueRef>(), nativeName + "tmp");
    }

    private LLVMValueRef GenerateCursesMoveCall(List<Expression> arguments)
    {
        if (arguments.Count != 2)
            throw new Exception("curses_move() expects exactly 2 arguments (row, col).");

        var move = GetOrAddFunction("move", GetInt32Type(), new[] { GetInt32Type(), GetInt32Type() });
        var args = new[]
        {
            ConvertToType(VisitExpression(arguments[0]), GetInt32Type()),
            ConvertToType(VisitExpression(arguments[1]), GetInt32Type())
        };
        return _builder.BuildCall2(_functionTypes["move"], move, args, "movetmp");
    }

    // Shared by curses_printw(fmt, ...) and curses_mvprintw(row, col, fmt, ...); leadingIntArgs
    // is 0 or 2 depending on whether the native call takes a leading (y, x) pair.
    private LLVMValueRef GenerateCursesPrintwCall(string nativeName, List<Expression> arguments, int leadingIntArgs)
    {
        if (arguments.Count < leadingIntArgs + 1)
            throw new Exception($"curses_{(leadingIntArgs == 0 ? "printw" : "mvprintw")}() expects a format string.");

        var paramTypes = new List<LLVMTypeRef>();
        for (int i = 0; i < leadingIntArgs; i++) paramTypes.Add(GetInt32Type());
        paramTypes.Add(GetPointerType(GetInt8Type()));

        var func = GetOrAddFunction(nativeName, GetInt32Type(), paramTypes.ToArray(), true);

        var callArgs = new List<LLVMValueRef>();
        for (int i = 0; i < leadingIntArgs; i++)
            callArgs.Add(ConvertToType(VisitExpression(arguments[i]), GetInt32Type()));

        int fmtIndex = leadingIntArgs;
        if (arguments[fmtIndex] is not LiteralExpr { Type: TokenType.StringLiteral } fmtLiteral)
            throw new Exception($"curses_{nativeName}() format argument must be a string literal.");

        var fmtStr = GetOrCreateGlobalStringPtr((string)fmtLiteral.Value!, "fmt");
        callArgs.Add(fmtStr);

        for (int i = fmtIndex + 1; i < arguments.Count; i++)
            callArgs.Add(VisitExpression(arguments[i]));

        return _builder.BuildCall2(_functionTypes[nativeName], func, callArgs.ToArray(), nativeName + "tmp");
    }

    private LLVMValueRef GenerateCursesAddchCall(List<Expression> arguments)
    {
        if (arguments.Count != 1)
            throw new Exception("curses_addch() expects exactly 1 argument (ch).");

        // chtype is a 64-bit "unsigned long" on 64-bit Linux/macOS ncurses, so widen to
        // i64 to stay ABI-compatible there (still fine for PDCurses' narrower chtype).
        var addch = GetOrAddFunction("addch", GetInt32Type(), new[] { GetInt64Type() });
        var ch = ConvertToType(VisitExpression(arguments[0]), GetInt64Type());
        return _builder.BuildCall2(_functionTypes["addch"], addch, new[] { ch }, "addchtmp");
    }

    private LLVMValueRef GenerateCursesGetchCall(List<Expression> arguments)
    {
        if (arguments.Count != 0)
            throw new Exception("curses_getch() takes no arguments.");

        var getch = GetOrAddFunction("getch", GetInt32Type(), Array.Empty<LLVMTypeRef>());
        return _builder.BuildCall2(_functionTypes["getch"], getch, Array.Empty<LLVMValueRef>(), "getchtmp");
    }

    private LLVMValueRef GenerateCursesIntArgCall(string nativeName, List<Expression> arguments)
    {
        if (arguments.Count != 1)
            throw new Exception($"curses_{nativeName}() expects exactly 1 argument.");

        var func = GetOrAddFunction(nativeName, GetInt32Type(), new[] { GetInt32Type() });
        var arg = ConvertToType(VisitExpression(arguments[0]), GetInt32Type());
        return _builder.BuildCall2(_functionTypes[nativeName], func, new[] { arg }, nativeName + "tmp");
    }

    // keypad(WINDOW*, bool) / nodelay(WINDOW*, bool), applied to stdscr.
    private LLVMValueRef GenerateCursesWindowBoolCall(string nativeName, List<Expression> arguments)
    {
        if (arguments.Count != 1)
            throw new Exception($"curses_{nativeName}() expects exactly 1 argument (enabled).");

        var func = GetOrAddFunction(nativeName, GetInt32Type(), new[] { GetPointerType(GetInt8Type()), GetInt32Type() });
        var enabled = ConvertToType(VisitExpression(arguments[0]), GetInt32Type());
        var args = new[] { GetStdscrPtr(), enabled };
        return _builder.BuildCall2(_functionTypes[nativeName], func, args, nativeName + "tmp");
    }

    private LLVMValueRef GenerateCursesInitPairCall(List<Expression> arguments)
    {
        if (arguments.Count != 3)
            throw new Exception("curses_init_pair() expects exactly 3 arguments (pair, fg, bg).");

        // short parameters on the native side; declaring them as i32 here is the safe
        // direction (the callee only reads the low 16 bits it actually expects).
        var initPair = GetOrAddFunction("init_pair", GetInt32Type(), new[] { GetInt32Type(), GetInt32Type(), GetInt32Type() });
        var args = new[]
        {
            ConvertToType(VisitExpression(arguments[0]), GetInt32Type()),
            ConvertToType(VisitExpression(arguments[1]), GetInt32Type()),
            ConvertToType(VisitExpression(arguments[2]), GetInt32Type())
        };
        return _builder.BuildCall2(_functionTypes["init_pair"], initPair, args, "initpairtmp");
    }

    // COLOR_PAIR() is a macro (not a real symbol), computed as (pair << 8) which matches
    // the attribute encoding used by both ncurses and PDCurses for the base 256 color pairs.
    private LLVMValueRef GenerateCursesColorPairCall(List<Expression> arguments)
    {
        if (arguments.Count != 1)
            throw new Exception("curses_color_pair() expects exactly 1 argument (pair).");

        var pair = ConvertToType(VisitExpression(arguments[0]), GetInt32Type());
        return _builder.BuildShl(pair, LLVMValueRef.CreateConstInt(GetInt32Type(), 8), "colorpairtmp");
    }

    private LLVMValueRef GenerateCursesAttrCall(string nativeName, List<Expression> arguments)
    {
        if (arguments.Count != 1)
            throw new Exception($"curses_{nativeName}() expects exactly 1 argument (attrs).");

        var func = GetOrAddFunction(nativeName, GetInt32Type(), new[] { GetInt32Type() });
        var attrs = ConvertToType(VisitExpression(arguments[0]), GetInt32Type());
        return _builder.BuildCall2(_functionTypes[nativeName], func, new[] { attrs }, nativeName + "tmp");
    }

    private LLVMValueRef GenerateCursesBoxCall(List<Expression> arguments)
    {
        if (arguments.Count != 2)
            throw new Exception("curses_box() expects exactly 2 arguments (verch, horch).");

        var box = GetOrAddFunction("box", GetInt32Type(), new[] { GetPointerType(GetInt8Type()), GetInt64Type(), GetInt64Type() });
        var args = new[]
        {
            GetStdscrPtr(),
            ConvertToType(VisitExpression(arguments[0]), GetInt64Type()),
            ConvertToType(VisitExpression(arguments[1]), GetInt64Type())
        };
        return _builder.BuildCall2(_functionTypes["box"], box, args, "boxtmp");
    }
}
