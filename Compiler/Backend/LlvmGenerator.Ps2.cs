using System;
using System.Collections.Generic;
using LLVMSharp.Interop;
using ZV.Compiler.AST;

namespace ZV.Compiler.Backend;

// PS/2 controller (8042) helpers for freestanding/OS-dev targets: the keyboard (and
// mouse) sit behind the data port 0x60 and status/command port 0x64.
public partial class LlvmGenerator
{
    private const ushort Ps2DataPort = 0x60;
    private const ushort Ps2StatusPort = 0x64;

    // Lowercase US QWERTY "Scan Code Set 1" make-code -> ASCII table. Unmapped/non-printable
    // codes (shift, ctrl, function keys, etc.) map to 0. Index with (scancode & 0x7F).
    private static readonly byte[] Ps2ScancodeAsciiTable = BuildScancodeAsciiTable();

    private static byte[] BuildScancodeAsciiTable()
    {
        var table = new byte[128];
        void Set(int code, char c) => table[code] = (byte)c;

        Set(0x02, '1'); Set(0x03, '2'); Set(0x04, '3'); Set(0x05, '4'); Set(0x06, '5');
        Set(0x07, '6'); Set(0x08, '7'); Set(0x09, '8'); Set(0x0A, '9'); Set(0x0B, '0');
        Set(0x0C, '-'); Set(0x0D, '=');
        Set(0x0E, (char)8);  // backspace
        Set(0x0F, (char)9);  // tab
        Set(0x10, 'q'); Set(0x11, 'w'); Set(0x12, 'e'); Set(0x13, 'r'); Set(0x14, 't');
        Set(0x15, 'y'); Set(0x16, 'u'); Set(0x17, 'i'); Set(0x18, 'o'); Set(0x19, 'p');
        Set(0x1A, '['); Set(0x1B, ']');
        Set(0x1C, (char)10); // enter
        Set(0x1E, 'a'); Set(0x1F, 's'); Set(0x20, 'd'); Set(0x21, 'f'); Set(0x22, 'g');
        Set(0x23, 'h'); Set(0x24, 'j'); Set(0x25, 'k'); Set(0x26, 'l');
        Set(0x27, ';'); Set(0x28, '\''); Set(0x29, '`'); Set(0x2B, '\\');
        Set(0x2C, 'z'); Set(0x2D, 'x'); Set(0x2E, 'c'); Set(0x2F, 'v'); Set(0x30, 'b');
        Set(0x31, 'n'); Set(0x32, 'm'); Set(0x33, ','); Set(0x34, '.'); Set(0x35, '/');
        Set(0x39, ' '); // space

        return table;
    }

    private LLVMValueRef GetOrCreatePs2AsciiTableGlobal()
    {
        var existing = _module.GetNamedGlobal("ps2_scancode_ascii_table");
        if (existing.Handle != IntPtr.Zero) return existing;

        var i8 = GetInt8Type();
        var tableType = LLVMTypeRef.CreateArray(i8, (uint)Ps2ScancodeAsciiTable.Length);
        var global = _module.AddGlobal(tableType, "ps2_scancode_ascii_table");

        var values = new LLVMValueRef[Ps2ScancodeAsciiTable.Length];
        for (int i = 0; i < values.Length; i++)
        {
            values[i] = LLVMValueRef.CreateConstInt(i8, Ps2ScancodeAsciiTable[i]);
        }
        global.Initializer = LLVMValueRef.CreateConstArray(i8, values);
        global.Linkage = LLVMLinkage.LLVMPrivateLinkage;
        global.IsGlobalConstant = true;
        return global;
    }

    // Waits until the PS/2 controller's output buffer is full (data available to read).
    private void EmitPs2WaitOutputFull()
    {
        BuildSpinWhile(() =>
        {
            var status = EmitPortIn(LLVMValueRef.CreateConstInt(GetInt16Type(), Ps2StatusPort), 8);
            var masked = _builder.BuildAnd(status, LLVMValueRef.CreateConstInt(GetInt8Type(), 0x01), "ps2_obf");
            return _builder.BuildICmp(LLVMIntPredicate.LLVMIntEQ, masked, LLVMValueRef.CreateConstInt(GetInt8Type(), 0), "ps2_obf_not_set");
        });
    }

    // Waits until the PS/2 controller's input buffer is empty (safe to send a byte).
    private void EmitPs2WaitInputEmpty()
    {
        BuildSpinWhile(() =>
        {
            var status = EmitPortIn(LLVMValueRef.CreateConstInt(GetInt16Type(), Ps2StatusPort), 8);
            var masked = _builder.BuildAnd(status, LLVMValueRef.CreateConstInt(GetInt8Type(), 0x02), "ps2_ibf");
            return _builder.BuildICmp(LLVMIntPredicate.LLVMIntNE, masked, LLVMValueRef.CreateConstInt(GetInt8Type(), 0), "ps2_ibf_set");
        });
    }

    private LLVMValueRef GeneratePs2HasDataCall(List<Expression> arguments)
    {
        if (arguments.Count != 0)
            throw new Exception("ps2_has_data() takes no arguments.");

        var status = EmitPortIn(LLVMValueRef.CreateConstInt(GetInt16Type(), Ps2StatusPort), 8);
        var masked = _builder.BuildAnd(status, LLVMValueRef.CreateConstInt(GetInt8Type(), 0x01), "ps2_obf");
        return _builder.BuildICmp(LLVMIntPredicate.LLVMIntNE, masked, LLVMValueRef.CreateConstInt(GetInt8Type(), 0), "has_data");
    }

    private LLVMValueRef GeneratePs2ReadDataCall(List<Expression> arguments)
    {
        if (arguments.Count != 0)
            throw new Exception("ps2_read_data() takes no arguments.");

        EmitPs2WaitOutputFull();
        return EmitPortIn(LLVMValueRef.CreateConstInt(GetInt16Type(), Ps2DataPort), 8);
    }

    private LLVMValueRef GeneratePs2WriteDataCall(List<Expression> arguments)
    {
        if (arguments.Count != 1)
            throw new Exception("ps2_write_data() expects exactly 1 argument (byte).");

        var data = ConvertToType(VisitExpression(arguments[0]), GetInt8Type());
        EmitPs2WaitInputEmpty();
        return EmitPortOut(LLVMValueRef.CreateConstInt(GetInt16Type(), Ps2DataPort), data, 8);
    }

    private LLVMValueRef GeneratePs2SendCommandCall(List<Expression> arguments)
    {
        if (arguments.Count != 1)
            throw new Exception("ps2_send_command() expects exactly 1 argument (byte).");

        var cmd = ConvertToType(VisitExpression(arguments[0]), GetInt8Type());
        EmitPs2WaitInputEmpty();
        return EmitPortOut(LLVMValueRef.CreateConstInt(GetInt16Type(), Ps2StatusPort), cmd, 8);
    }

    private LLVMValueRef GeneratePs2ScancodeToAsciiCall(List<Expression> arguments)
    {
        if (arguments.Count != 1)
            throw new Exception("ps2_scancode_to_ascii() expects exactly 1 argument (scancode).");

        var i32 = GetInt32Type();
        var scancode = ConvertToType(VisitExpression(arguments[0]), i32);
        var index = _builder.BuildAnd(scancode, LLVMValueRef.CreateConstInt(i32, 0x7F), "ps2_ascii_idx");

        var table = GetOrCreatePs2AsciiTableGlobal();
        var tableType = LLVMTypeRef.CreateArray(GetInt8Type(), (uint)Ps2ScancodeAsciiTable.Length);
        var entryPtr = _builder.BuildGEP2(tableType, table, new[] { LLVMValueRef.CreateConstInt(i32, 0), index }, "ps2_ascii_ptr");
        return _builder.BuildLoad2(GetInt8Type(), entryPtr, "ps2_ascii");
    }

    // Blocks until a printable key is pressed (ignores key-release/break codes and keys
    // with no ASCII mapping, e.g. shift/ctrl/function keys) and returns its ASCII value.
    private LLVMValueRef GenerateKeyboardGetcharCall(List<Expression> arguments)
    {
        if (arguments.Count != 0)
            throw new Exception("keyboard_getchar() takes no arguments.");

        var i8 = GetInt8Type();
        var function = _builder.InsertBlock.Parent;
        var resultAlloca = _builder.BuildAlloca(i8, "getchar_result");

        var loopBB = _context.AppendBasicBlock(function, "getcharloop");
        var acceptBB = _context.AppendBasicBlock(function, "getcharaccept");
        var endBB = _context.AppendBasicBlock(function, "getcharend");

        _builder.BuildBr(loopBB);

        _builder.PositionAtEnd(loopBB);
        EmitPs2WaitOutputFull();
        var scancode = EmitPortIn(LLVMValueRef.CreateConstInt(GetInt16Type(), Ps2DataPort), 8);
        var isBreakCode = _builder.BuildICmp(LLVMIntPredicate.LLVMIntNE,
            _builder.BuildAnd(scancode, LLVMValueRef.CreateConstInt(i8, 0x80), "break_bit"),
            LLVMValueRef.CreateConstInt(i8, 0), "is_break");

        var asciiTable = GetOrCreatePs2AsciiTableGlobal();
        var tableType = LLVMTypeRef.CreateArray(i8, (uint)Ps2ScancodeAsciiTable.Length);
        var idx32 = _builder.BuildZExt(_builder.BuildAnd(scancode, LLVMValueRef.CreateConstInt(i8, 0x7F), "idx8"), GetInt32Type(), "idx32");
        var entryPtr = _builder.BuildGEP2(tableType, asciiTable, new[] { LLVMValueRef.CreateConstInt(GetInt32Type(), 0), idx32 }, "ascii_ptr");
        var ascii = _builder.BuildLoad2(i8, entryPtr, "ascii");
        var hasMapping = _builder.BuildICmp(LLVMIntPredicate.LLVMIntNE, ascii, LLVMValueRef.CreateConstInt(i8, 0), "has_mapping");

        var shouldAccept = _builder.BuildAnd(_builder.BuildNot(isBreakCode, "not_break"), hasMapping, "should_accept");
        _builder.BuildCondBr(shouldAccept, acceptBB, loopBB);

        _builder.PositionAtEnd(acceptBB);
        _builder.BuildStore(ascii, resultAlloca);
        _builder.BuildBr(endBB);

        _builder.PositionAtEnd(endBB);
        return _builder.BuildLoad2(i8, resultAlloca, "getchar_val");
    }
}
