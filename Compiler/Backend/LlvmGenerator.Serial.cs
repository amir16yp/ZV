using System;
using System.Collections.Generic;
using LLVMSharp.Interop;
using ZV.Compiler.AST;

namespace ZV.Compiler.Backend;

// Higher-level serial (UART 8250/16550) helpers for freestanding/OS-dev targets, built
// on top of the raw port_in8/port_out8 intrinsics in LlvmGenerator.Cpu.cs.
public partial class LlvmGenerator
{
    private LLVMValueRef PortPlusOffset(LLVMValueRef port, uint offset)
    {
        var port16 = ConvertToType(port, GetInt16Type());
        return _builder.BuildAdd(port16, LLVMValueRef.CreateConstInt(GetInt16Type(), offset), "port_off");
    }

    // Busy-waits while `conditionBuilder()` (re-evaluated each iteration) returns a non-zero i1.
    private void BuildSpinWhile(Func<LLVMValueRef> conditionBuilder)
    {
        var function = _builder.InsertBlock.Parent;
        var condBB = _context.AppendBasicBlock(function, "spincond");
        var endBB = _context.AppendBasicBlock(function, "spinend");

        _builder.BuildBr(condBB);
        _builder.PositionAtEnd(condBB);
        var keepWaiting = conditionBuilder();
        _builder.BuildCondBr(keepWaiting, condBB, endBB);

        _builder.PositionAtEnd(endBB);
    }

    // Iterates over a NUL-terminated i8* string, invoking `body(charValue)` for each byte
    // before the terminator (the terminator itself is not visited).
    private void BuildCStringLoop(LLVMValueRef strPtr, Action<LLVMValueRef, LLVMValueRef> body)
    {
        var function = _builder.InsertBlock.Parent;
        var idxAlloca = BuildEntryAlloca(GetInt64Type(), "str_idx");
        _builder.BuildStore(LLVMValueRef.CreateConstInt(GetInt64Type(), 0), idxAlloca);

        var condBB = _context.AppendBasicBlock(function, "strcond");
        var bodyBB = _context.AppendBasicBlock(function, "strbody");
        var endBB = _context.AppendBasicBlock(function, "strend");

        _builder.BuildBr(condBB);

        _builder.PositionAtEnd(condBB);
        var idx = _builder.BuildLoad2(GetInt64Type(), idxAlloca, "idx");
        var charPtr = _builder.BuildGEP2(GetInt8Type(), strPtr, new[] { idx }, "charptr");
        var ch = _builder.BuildLoad2(GetInt8Type(), charPtr, "ch");
        var isTerminator = _builder.BuildICmp(LLVMIntPredicate.LLVMIntEQ, ch, LLVMValueRef.CreateConstInt(GetInt8Type(), 0), "isterm");
        _builder.BuildCondBr(isTerminator, endBB, bodyBB);

        _builder.PositionAtEnd(bodyBB);
        body(ch, idx);
        var next = _builder.BuildAdd(idx, LLVMValueRef.CreateConstInt(GetInt64Type(), 1), "next_idx");
        _builder.BuildStore(next, idxAlloca);
        _builder.BuildBr(condBB);

        _builder.PositionAtEnd(endBB);
    }

    // Standard 8250/16550 UART bring-up: 38400 baud, 8N1, FIFO enabled.
    private LLVMValueRef GenerateSerialInitCall(List<Expression> arguments)
    {
        if (arguments.Count != 1)
            throw new Exception("serial_init() expects exactly 1 argument (port).");

        var port = ConvertToType(VisitExpression(arguments[0]), GetInt16Type());
        var i8 = GetInt8Type();

        EmitPortOut(PortPlusOffset(port, 1), LLVMValueRef.CreateConstInt(i8, 0x00), 8); // disable interrupts
        EmitPortOut(PortPlusOffset(port, 3), LLVMValueRef.CreateConstInt(i8, 0x80), 8); // enable DLAB (set baud rate divisor)
        EmitPortOut(PortPlusOffset(port, 0), LLVMValueRef.CreateConstInt(i8, 0x03), 8); // divisor low byte (38400 baud)
        EmitPortOut(PortPlusOffset(port, 1), LLVMValueRef.CreateConstInt(i8, 0x00), 8); // divisor high byte
        EmitPortOut(PortPlusOffset(port, 3), LLVMValueRef.CreateConstInt(i8, 0x03), 8); // 8 bits, no parity, one stop bit
        EmitPortOut(PortPlusOffset(port, 2), LLVMValueRef.CreateConstInt(i8, 0xC7), 8); // enable FIFO, clear, 14-byte threshold
        return EmitPortOut(PortPlusOffset(port, 4), LLVMValueRef.CreateConstInt(i8, 0x0B), 8); // IRQs enabled, RTS/DSR set
    }

    private LLVMValueRef GenerateSerialWriteCharCall(List<Expression> arguments)
    {
        if (arguments.Count != 2)
            throw new Exception("serial_write_char() expects exactly 2 arguments (port, char).");

        var port = ConvertToType(VisitExpression(arguments[0]), GetInt16Type());
        var ch = ConvertToType(VisitExpression(arguments[1]), GetInt8Type());
        return EmitSerialWriteChar(port, ch);
    }

    private LLVMValueRef EmitSerialWriteChar(LLVMValueRef port, LLVMValueRef ch)
    {
        // Wait for the Transmitter Holding Register Empty bit (bit 5 of the Line Status Register).
        BuildSpinWhile(() =>
        {
            var lsr = EmitPortIn(PortPlusOffset(port, 5), 8);
            var masked = _builder.BuildAnd(lsr, LLVMValueRef.CreateConstInt(GetInt8Type(), 0x20), "lsr_thre");
            return _builder.BuildICmp(LLVMIntPredicate.LLVMIntEQ, masked, LLVMValueRef.CreateConstInt(GetInt8Type(), 0), "thre_not_set");
        });
        return EmitPortOut(port, ch, 8);
    }

    private LLVMValueRef GenerateSerialWriteCall(List<Expression> arguments)
    {
        if (arguments.Count != 2)
            throw new Exception("serial_write() expects exactly 2 arguments (port, string).");

        var port = ConvertToType(VisitExpression(arguments[0]), GetInt16Type());
        var str = VisitExpression(arguments[1]);
        if (str.TypeOf.Kind != LLVMTypeKind.LLVMPointerTypeKind)
            throw new Exception("serial_write() second argument must be a string.");

        BuildCStringLoop(str, (ch, _) => EmitSerialWriteChar(port, ch));
        return LLVMValueRef.CreateConstInt(GetInt32Type(), 0);
    }

    private LLVMValueRef GenerateSerialReadCharCall(List<Expression> arguments)
    {
        if (arguments.Count != 1)
            throw new Exception("serial_read_char() expects exactly 1 argument (port).");

        var port = ConvertToType(VisitExpression(arguments[0]), GetInt16Type());

        // Wait for the Data Ready bit (bit 0 of the Line Status Register).
        BuildSpinWhile(() =>
        {
            var lsr = EmitPortIn(PortPlusOffset(port, 5), 8);
            var masked = _builder.BuildAnd(lsr, LLVMValueRef.CreateConstInt(GetInt8Type(), 0x01), "lsr_dr");
            return _builder.BuildICmp(LLVMIntPredicate.LLVMIntEQ, masked, LLVMValueRef.CreateConstInt(GetInt8Type(), 0), "dr_not_set");
        });
        return EmitPortIn(port, 8);
    }

    private LLVMValueRef GenerateSerialHasDataCall(List<Expression> arguments)
    {
        if (arguments.Count != 1)
            throw new Exception("serial_has_data() expects exactly 1 argument (port).");

        var port = ConvertToType(VisitExpression(arguments[0]), GetInt16Type());
        var lsr = EmitPortIn(PortPlusOffset(port, 5), 8);
        var masked = _builder.BuildAnd(lsr, LLVMValueRef.CreateConstInt(GetInt8Type(), 0x01), "lsr_dr");
        return _builder.BuildICmp(LLVMIntPredicate.LLVMIntNE, masked, LLVMValueRef.CreateConstInt(GetInt8Type(), 0), "has_data");
    }
}
