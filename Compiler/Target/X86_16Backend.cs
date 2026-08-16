using System;
using System.Collections.Generic;
using System.Linq;
using ZV.Compiler.AST;
using ZV.Compiler.Lexer;

namespace ZV.Compiler.Target;

/// <summary>
/// Direct AST-to-16-bit-x86 backend for a deliberately small subset of ZV sufficient for a
/// first bare-metal kernel entry point. It is intentionally simple: no IR, no optimizer.
/// </summary>
public sealed class X86_16Backend
{
    private const ushort KernelBase = 0x7E00;

    private readonly X86_16Assembler _asm = new(KernelBase);
    private readonly List<Statement> _statements;
    private readonly IReadOnlyList<EmbedInfo> _embeds;
    private readonly HashSet<string> _runtimeFunctions = new();
    private readonly Dictionary<string, string> _stringLabels = new();
    private int _stringCounter;

    public X86_16Backend(List<Statement> statements, IReadOnlyList<EmbedInfo>? embeds = null)
    {
        _statements = statements;
        _embeds = embeds ?? Array.Empty<EmbedInfo>();
    }

    public byte[] Compile()
    {
        CollectRuntimeFunctions();
        EmitStartup();

        var userFunctions = _statements.OfType<FunctionDeclStmt>().ToList();
        if (userFunctions.Count == 0)
            throw new CompileException(null, "x86-16 bare-metal target requires at least one function (the kernel entry point).");

        foreach (var fn in userFunctions)
        {
            _asm.DefineLabel(FunctionLabel(fn.Name.Lexeme));
            EmitFunctionBody(fn.Body);
        }

        EmitRuntimeFunctions();
        EmitStringData();
        EmitEmbedLayout();

        return _asm.Build();
    }

    private void CollectRuntimeFunctions()
    {
        foreach (var externDecl in _statements.OfType<ExternDeclStmt>())
        {
            foreach (var fn in externDecl.Functions)
            {
                _runtimeFunctions.Add(fn.Name.Lexeme);
            }
        }
    }

    private void EmitStartup()
    {
        // Kernel entry point. Set up segment registers and a stack, then call the user's
        // entry function. If it returns, hang.
        _asm.EmitCli();
        _asm.EmitByte(0x31); _asm.EmitByte(0xC0); // xor ax, ax
        _asm.EmitByte(0x8E); _asm.EmitByte(0xD8); // mov ds, ax
        _asm.EmitByte(0x8E); _asm.EmitByte(0xC0); // mov es, ax
        _asm.EmitByte(0x8E); _asm.EmitByte(0xD0); // mov ss, ax
        _asm.EmitMovRegImm16(X86_16Register.SP, 0x7C00);
        _asm.EmitSti();

        var entry = FindEntryFunction();
        _asm.EmitCall(FunctionLabel(entry.Name.Lexeme));

        _asm.DefineLabel("__kernel_hang");
        _asm.EmitHlt();
        _asm.EmitJmpShort("__kernel_hang");
    }

    private FunctionDeclStmt FindEntryFunction()
    {
        var entry = _statements.OfType<FunctionDeclStmt>().FirstOrDefault(f => f.IsEntry)
            ?? _statements.OfType<FunctionDeclStmt>().FirstOrDefault(f => f.Name.Lexeme.Equals("kmain", StringComparison.OrdinalIgnoreCase))
            ?? _statements.OfType<FunctionDeclStmt>().First();
        return entry;
    }

    private void EmitFunctionBody(BlockStmt body)
    {
        foreach (var stmt in body.Statements)
        {
            EmitStatement(stmt);
        }
    }

    private void EmitStatement(Statement stmt)
    {
        switch (stmt)
        {
            case ExpressionStmt exprStmt:
                EmitExpression(exprStmt.Expression, discardResult: true);
                break;
            case ReturnStmt ret:
                if (ret.Value != null)
                    throw new CompileException(ret.Location, "x86-16 backend only supports VOID return.");
                _asm.EmitRet();
                break;
            default:
                throw new CompileException(stmt.Location, $"x86-16 backend does not support statement '{stmt.GetType().Name}'.");
        }
    }

    private void EmitExpression(Expression expr, bool discardResult)
    {
        if (expr is CallExpr call)
        {
            EmitCall(call);
            return;
        }

        if (expr is LiteralExpr lit && lit.Type == TokenType.StringLiteral)
        {
            _asm.EmitMovRegImm16(X86_16Register.SI, StringLabel((string)lit.Value!));
            return;
        }

        throw new CompileException(expr.Location, $"x86-16 backend does not support expression '{expr.GetType().Name}'.");
    }

    private void EmitCall(CallExpr call)
    {
        if (call.Callee is not VariableExpr calleeVar)
            throw new CompileException(call.Location, "x86-16 backend only supports direct function calls.");

        string name = calleeVar.Name;
        bool isRuntime = _runtimeFunctions.Contains(name);

        // Only a single CSTRING argument is supported.
        if (call.Arguments.Count > 1)
            throw new CompileException(call.Location, "x86-16 backend supports at most one argument per call.");

        if (call.Arguments.Count == 1)
        {
            var arg = call.Arguments[0];
            if (arg is LiteralExpr lit && lit.Type == TokenType.StringLiteral)
            {
                _asm.EmitMovRegImm16(X86_16Register.SI, StringLabel((string)lit.Value!));
            }
            else if (arg is VariableExpr varExpr)
            {
                _asm.EmitMovRegImm16(X86_16Register.SI, VarLabel(varExpr.Name));
            }
            else
            {
                throw new CompileException(arg.Location, "x86-16 backend only supports CSTRING variables or string literals as arguments.");
            }
        }

        _asm.EmitCall(isRuntime ? RuntimeLabel(name) : FunctionLabel(name));
    }

    private void EmitRuntimeFunctions()
    {
        foreach (var name in _runtimeFunctions)
        {
            _asm.DefineLabel(RuntimeLabel(name));
            switch (name.ToLowerInvariant())
            {
                case "print":
                    EmitRuntimePrint();
                    break;
                case "halt":
                case "hlt":
                    EmitRuntimeHalt();
                    break;
                default:
                    throw new CompileException(null, $"Unknown x86-16 runtime function '{name}'.");
            }
        }
    }

    private void EmitRuntimePrint()
    {
        _asm.DefineLabel("__runtime_print_loop");
        _asm.EmitLodsb();
        _asm.EmitOrAlAl();
        _asm.EmitJeShort("__runtime_print_done");
        _asm.EmitMovAhImm8(0x0E);
        _asm.EmitInt(0x10);
        _asm.EmitJmpShort("__runtime_print_loop");
        _asm.DefineLabel("__runtime_print_done");
        _asm.EmitRet();
    }

    private void EmitRuntimeHalt()
    {
        _asm.DefineLabel("__runtime_halt_loop");
        _asm.EmitHlt();
        _asm.EmitJmpShort("__runtime_halt_loop");
    }

    private void EmitStringData()
    {
        foreach (var item in _stringLabels)
        {
            _asm.DefineLabel(item.Value);
            _asm.EmitDataString(item.Key);
        }
    }

    private string FunctionLabel(string name) => "fn_" + name;
    private string RuntimeLabel(string name) => "rt_" + name;
    private string VarLabel(string name) => "var_" + name;

    private void EmitEmbedLayout()
    {
        if (_embeds.Count == 0) return;

        _asm.DefineLabel("__zv_embed_start");
        _asm.EmitBytes(EmbedLayout.Build(_embeds));
    }

    private string StringLabel(string text)
    {
        if (!_stringLabels.TryGetValue(text, out var label))
        {
            label = $"str_{_stringCounter++}";
            _stringLabels[text] = label;
        }
        return label;
    }
}
