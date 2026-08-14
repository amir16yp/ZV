using System;
using System.Collections.Generic;
using LLVMSharp.Interop;
using ZV.Compiler.AST;

namespace ZV.Compiler.Backend;

// VGA text-mode helpers for freestanding/OS-dev targets: writes directly to the
// memory-mapped text buffer at 0xB8000 (the standard location under BIOS/legacy VGA).
public partial class LlvmGenerator
{
    private const uint VgaBufferAddress = 0xB8000;
    private const uint VgaWidth = 80;
    private const uint VgaHeight = 25;

    private LLVMValueRef VgaCellPtr(LLVMValueRef col, LLVMValueRef row)
    {
        var i32 = GetInt32Type();
        col = ConvertToType(col, i32);
        row = ConvertToType(row, i32);

        var rowWidth = _builder.BuildMul(row, LLVMValueRef.CreateConstInt(i32, VgaWidth), "vga_row_off");
        var cellIndex = _builder.BuildAdd(rowWidth, col, "vga_cell_idx");
        var byteOffset = _builder.BuildMul(cellIndex, LLVMValueRef.CreateConstInt(i32, 2), "vga_byte_off");
        var baseAddr = LLVMValueRef.CreateConstInt(i32, VgaBufferAddress);
        var addr = _builder.BuildAdd(baseAddr, byteOffset, "vga_addr");
        return _builder.BuildIntToPtr(addr, GetPointerType(GetInt8Type()), "vga_ptr");
    }

    private LLVMValueRef EmitVgaWriteCell(LLVMValueRef col, LLVMValueRef row, LLVMValueRef ch, LLVMValueRef color)
    {
        var cellPtr = VgaCellPtr(col, row);
        var charStore = _builder.BuildStore(ConvertToType(ch, GetInt8Type()), cellPtr);
        charStore.Volatile = true;

        var colorPtr = _builder.BuildGEP2(GetInt8Type(), cellPtr, new[] { LLVMValueRef.CreateConstInt(GetInt32Type(), 1) }, "vga_color_ptr");
        var colorStore = _builder.BuildStore(ConvertToType(color, GetInt8Type()), colorPtr);
        colorStore.Volatile = true;
        return colorStore;
    }

    private LLVMValueRef GenerateVgaPutcCall(List<Expression> arguments)
    {
        if (arguments.Count != 4)
            throw new Exception("vga_putc() expects exactly 4 arguments (col, row, char, color).");

        var col = VisitExpression(arguments[0]);
        var row = VisitExpression(arguments[1]);
        var ch = VisitExpression(arguments[2]);
        var color = VisitExpression(arguments[3]);
        return EmitVgaWriteCell(col, row, ch, color);
    }

    private LLVMValueRef GenerateVgaClearCall(List<Expression> arguments)
    {
        if (arguments.Count != 1)
            throw new Exception("vga_clear() expects exactly 1 argument (color).");

        var i32 = GetInt32Type();
        var color = ConvertToType(VisitExpression(arguments[0]), GetInt8Type());

        var function = _builder.InsertBlock.Parent;
        var idxAlloca = _builder.BuildAlloca(GetInt64Type(), "vga_clear_idx");
        _builder.BuildStore(LLVMValueRef.CreateConstInt(GetInt64Type(), 0), idxAlloca);

        var condBB = _context.AppendBasicBlock(function, "vgaclearcond");
        var bodyBB = _context.AppendBasicBlock(function, "vgaclearbody");
        var endBB = _context.AppendBasicBlock(function, "vgaclearend");

        _builder.BuildBr(condBB);

        _builder.PositionAtEnd(condBB);
        var idx = _builder.BuildLoad2(GetInt64Type(), idxAlloca, "idx");
        var cellCount = LLVMValueRef.CreateConstInt(GetInt64Type(), VgaWidth * VgaHeight);
        var cont = _builder.BuildICmp(LLVMIntPredicate.LLVMIntULT, idx, cellCount, "vga_clear_cont");
        _builder.BuildCondBr(cont, bodyBB, endBB);

        _builder.PositionAtEnd(bodyBB);
        var idx32 = _builder.BuildTrunc(idx, i32, "idx32");
        var col = _builder.BuildURem(idx32, LLVMValueRef.CreateConstInt(i32, VgaWidth), "vga_col");
        var row = _builder.BuildUDiv(idx32, LLVMValueRef.CreateConstInt(i32, VgaWidth), "vga_row");
        EmitVgaWriteCell(col, row, LLVMValueRef.CreateConstInt(GetInt8Type(), (byte)' '), color);
        var next = _builder.BuildAdd(idx, LLVMValueRef.CreateConstInt(GetInt64Type(), 1), "next_idx");
        _builder.BuildStore(next, idxAlloca);
        _builder.BuildBr(condBB);

        _builder.PositionAtEnd(endBB);
        return LLVMValueRef.CreateConstInt(GetInt32Type(), 0);
    }

    private LLVMValueRef GenerateVgaPrintCall(List<Expression> arguments)
    {
        if (arguments.Count != 4)
            throw new Exception("vga_print() expects exactly 4 arguments (col, row, string, color).");

        var i32 = GetInt32Type();
        var colStart = ConvertToType(VisitExpression(arguments[0]), i32);
        var rowStart = ConvertToType(VisitExpression(arguments[1]), i32);
        var str = VisitExpression(arguments[2]);
        var color = ConvertToType(VisitExpression(arguments[3]), GetInt8Type());

        if (str.TypeOf.Kind != LLVMTypeKind.LLVMPointerTypeKind)
            throw new Exception("vga_print() string argument must be a string.");

        var colAlloca = _builder.BuildAlloca(i32, "vga_print_col");
        var rowAlloca = _builder.BuildAlloca(i32, "vga_print_row");
        _builder.BuildStore(colStart, colAlloca);
        _builder.BuildStore(rowStart, rowAlloca);

        BuildCStringLoop(str, (ch, _) =>
        {
            var col = _builder.BuildLoad2(i32, colAlloca, "col");
            var row = _builder.BuildLoad2(i32, rowAlloca, "row");
            EmitVgaWriteCell(col, row, ch, color);

            var nextCol = _builder.BuildAdd(col, LLVMValueRef.CreateConstInt(i32, 1), "next_col");
            var wrapped = _builder.BuildICmp(LLVMIntPredicate.LLVMIntUGE, nextCol, LLVMValueRef.CreateConstInt(i32, VgaWidth), "wrapped");
            var nextRow = _builder.BuildSelect(wrapped, _builder.BuildAdd(row, LLVMValueRef.CreateConstInt(i32, 1), "row_plus1"), row, "next_row");
            var wrappedCol = _builder.BuildSelect(wrapped, LLVMValueRef.CreateConstInt(i32, 0), nextCol, "wrapped_col");

            _builder.BuildStore(wrappedCol, colAlloca);
            _builder.BuildStore(nextRow, rowAlloca);
        });

        return LLVMValueRef.CreateConstInt(GetInt32Type(), 0);
    }
}
