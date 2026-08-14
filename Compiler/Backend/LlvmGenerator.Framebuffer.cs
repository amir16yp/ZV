using System;
using System.Collections.Generic;
using LLVMSharp.Interop;
using ZV.Compiler.AST;

namespace ZV.Compiler.Backend;

// Linear framebuffer abstraction for freestanding/OS-dev targets, built on top of the
// Multiboot info structure the bootloader hands us in EBX at boot (see
// LlvmGenerator.Freestanding.cs). Requires booting through a Multiboot-compliant
// bootloader that actually sets up a video mode (e.g. GRUB); QEMU's own "-kernel"
// loader does not, so fb_available() will read back false there.
public partial class LlvmGenerator
{
    // Byte offsets into the Multiboot info structure (see the Multiboot 0.6.96 spec).
    private const uint MbiFlagsOffset = 0;
    private const uint MbiFramebufferAddrOffset = 88;   // uint64 (we only use the low 32 bits)
    private const uint MbiFramebufferPitchOffset = 96;  // uint32
    private const uint MbiFramebufferWidthOffset = 100; // uint32
    private const uint MbiFramebufferHeightOffset = 104; // uint32
    private const uint MbiFramebufferBppOffset = 108;   // uint8
    private const uint MbiFramebufferInfoFlagBit = 0x1000; // bit 12

    private LLVMValueRef GetOrCreateGlobalI32(string name)
    {
        var existing = _module.GetNamedGlobal(name);
        if (existing.Handle != IntPtr.Zero) return existing;

        var global = _module.AddGlobal(GetInt32Type(), name);
        global.Initializer = LLVMValueRef.CreateConstInt(GetInt32Type(), 0);
        global.Linkage = LLVMLinkage.LLVMPrivateLinkage;
        return global;
    }

    private LLVMValueRef GetOrCreateMultibootInfoPtrGlobal() => GetOrCreateGlobalI32("mb_info_ptr");
    private LLVMValueRef GetOrCreateFbAddrGlobal() => GetOrCreateGlobalI32("fb_addr");
    private LLVMValueRef GetOrCreateFbPitchGlobal() => GetOrCreateGlobalI32("fb_pitch");
    private LLVMValueRef GetOrCreateFbWidthGlobal() => GetOrCreateGlobalI32("fb_width");
    private LLVMValueRef GetOrCreateFbHeightGlobal() => GetOrCreateGlobalI32("fb_height");
    private LLVMValueRef GetOrCreateFbBppGlobal() => GetOrCreateGlobalI32("fb_bpp");

    // Reads the Multiboot info struct pointed to by `mbInfoPtr` and, if the bootloader
    // reports framebuffer info (flags bit 12), populates the fb_* globals from it.
    private void EmitParseMultibootFramebufferInfo(LLVMValueRef mbInfoPtr)
    {
        var i32 = GetInt32Type();
        var i8Ptr = GetPointerType(GetInt8Type());
        var basePtr = _builder.BuildIntToPtr(mbInfoPtr, i8Ptr, "mbi_ptr");

        LLVMValueRef FieldPtr(uint offset) =>
            _builder.BuildGEP2(GetInt8Type(), basePtr, new[] { LLVMValueRef.CreateConstInt(i32, offset) }, "mbi_field_ptr");

        LLVMValueRef LoadI32At(uint offset)
        {
            var fieldPtr = _builder.BuildBitCast(FieldPtr(offset), GetPointerType(i32), "mbi_field_i32");
            return _builder.BuildLoad2(i32, fieldPtr, "mbi_field_val");
        }

        var flags = LoadI32At(MbiFlagsOffset);
        var masked = _builder.BuildAnd(flags, LLVMValueRef.CreateConstInt(i32, MbiFramebufferInfoFlagBit), "mbi_fb_flag");
        var hasFramebuffer = _builder.BuildICmp(LLVMIntPredicate.LLVMIntNE, masked, LLVMValueRef.CreateConstInt(i32, 0), "has_fb");

        var function = _builder.InsertBlock.Parent;
        var thenBB = _context.AppendBasicBlock(function, "mbifbthen");
        var mergeBB = _context.AppendBasicBlock(function, "mbifbmerge");
        _builder.BuildCondBr(hasFramebuffer, thenBB, mergeBB);

        _builder.PositionAtEnd(thenBB);
        _builder.BuildStore(LoadI32At(MbiFramebufferAddrOffset), GetOrCreateFbAddrGlobal());
        _builder.BuildStore(LoadI32At(MbiFramebufferPitchOffset), GetOrCreateFbPitchGlobal());
        _builder.BuildStore(LoadI32At(MbiFramebufferWidthOffset), GetOrCreateFbWidthGlobal());
        _builder.BuildStore(LoadI32At(MbiFramebufferHeightOffset), GetOrCreateFbHeightGlobal());
        // The bpp/type bytes share a dword with the start of color_info; mask down to the low byte.
        var bppDword = LoadI32At(MbiFramebufferBppOffset);
        var bpp = _builder.BuildAnd(bppDword, LLVMValueRef.CreateConstInt(i32, 0xFF), "fb_bpp_val");
        _builder.BuildStore(bpp, GetOrCreateFbBppGlobal());
        _builder.BuildBr(mergeBB);

        _builder.PositionAtEnd(mergeBB);
    }

    private LLVMValueRef GenerateFbAvailableCall(List<Expression> arguments)
    {
        if (arguments.Count != 0)
            throw new Exception("fb_available() takes no arguments.");

        var width = _builder.BuildLoad2(GetInt32Type(), GetOrCreateFbWidthGlobal(), "fb_width_val");
        return _builder.BuildICmp(LLVMIntPredicate.LLVMIntNE, width, LLVMValueRef.CreateConstInt(GetInt32Type(), 0), "fb_available");
    }

    private LLVMValueRef GenerateFbWidthCall(List<Expression> arguments)
    {
        if (arguments.Count != 0) throw new Exception("fb_width() takes no arguments.");
        return _builder.BuildLoad2(GetInt32Type(), GetOrCreateFbWidthGlobal(), "fb_width_val");
    }

    private LLVMValueRef GenerateFbHeightCall(List<Expression> arguments)
    {
        if (arguments.Count != 0) throw new Exception("fb_height() takes no arguments.");
        return _builder.BuildLoad2(GetInt32Type(), GetOrCreateFbHeightGlobal(), "fb_height_val");
    }

    private LLVMValueRef GenerateFbPitchCall(List<Expression> arguments)
    {
        if (arguments.Count != 0) throw new Exception("fb_pitch() takes no arguments.");
        return _builder.BuildLoad2(GetInt32Type(), GetOrCreateFbPitchGlobal(), "fb_pitch_val");
    }

    private LLVMValueRef GenerateFbBppCall(List<Expression> arguments)
    {
        if (arguments.Count != 0) throw new Exception("fb_bpp() takes no arguments.");
        return _builder.BuildLoad2(GetInt32Type(), GetOrCreateFbBppGlobal(), "fb_bpp_val");
    }

    // Computes the byte address of pixel (x, y) using the runtime pitch/bpp reported by the
    // bootloader.
    private LLVMValueRef EmitFbPixelAddr(LLVMValueRef x, LLVMValueRef y)
    {
        var i32 = GetInt32Type();
        x = ConvertToType(x, i32);
        y = ConvertToType(y, i32);

        var addr = _builder.BuildLoad2(i32, GetOrCreateFbAddrGlobal(), "fb_addr_val");
        var pitch = _builder.BuildLoad2(i32, GetOrCreateFbPitchGlobal(), "fb_pitch_val");
        var bpp = _builder.BuildLoad2(i32, GetOrCreateFbBppGlobal(), "fb_bpp_val");
        var bytesPerPixel = _builder.BuildUDiv(bpp, LLVMValueRef.CreateConstInt(i32, 8), "fb_bypp");

        var rowOffset = _builder.BuildMul(y, pitch, "fb_row_off");
        var colOffset = _builder.BuildMul(x, bytesPerPixel, "fb_col_off");
        var totalOffset = _builder.BuildAdd(rowOffset, colOffset, "fb_off");
        var pixelAddr = _builder.BuildAdd(addr, totalOffset, "fb_pixel_addr");
        return _builder.BuildIntToPtr(pixelAddr, GetPointerType(GetInt8Type()), "fb_pixel_ptr");
    }

    // Writes `color` to pixel (x, y), truncated/laid out according to the runtime bpp
    // (supports the common 32bpp and 16bpp modes; other depths are a no-op).
    private void EmitFbSetPixel(LLVMValueRef x, LLVMValueRef y, LLVMValueRef color)
    {
        var i32 = GetInt32Type();
        color = ConvertToType(color, i32);
        var pixelPtr = EmitFbPixelAddr(x, y);
        var bpp = _builder.BuildLoad2(i32, GetOrCreateFbBppGlobal(), "fb_bpp_val");

        var function = _builder.InsertBlock.Parent;
        var is32BB = _context.AppendBasicBlock(function, "fbpx32");
        var check16BB = _context.AppendBasicBlock(function, "fbpxcheck16");
        var is16BB = _context.AppendBasicBlock(function, "fbpx16");
        var mergeBB = _context.AppendBasicBlock(function, "fbpxmerge");

        var isThirtyTwo = _builder.BuildICmp(LLVMIntPredicate.LLVMIntEQ, bpp, LLVMValueRef.CreateConstInt(i32, 32), "is32");
        _builder.BuildCondBr(isThirtyTwo, is32BB, check16BB);

        _builder.PositionAtEnd(is32BB);
        var ptr32 = _builder.BuildBitCast(pixelPtr, GetPointerType(i32), "fb_ptr32");
        var store32 = _builder.BuildStore(color, ptr32);
        store32.Volatile = true;
        _builder.BuildBr(mergeBB);

        _builder.PositionAtEnd(check16BB);
        var isSixteen = _builder.BuildICmp(LLVMIntPredicate.LLVMIntEQ, bpp, LLVMValueRef.CreateConstInt(i32, 16), "is16");
        _builder.BuildCondBr(isSixteen, is16BB, mergeBB);

        _builder.PositionAtEnd(is16BB);
        var i16 = GetInt16Type();
        var ptr16 = _builder.BuildBitCast(pixelPtr, GetPointerType(i16), "fb_ptr16");
        var color16 = _builder.BuildTrunc(color, i16, "fb_color16");
        var store16 = _builder.BuildStore(color16, ptr16);
        store16.Volatile = true;
        _builder.BuildBr(mergeBB);

        _builder.PositionAtEnd(mergeBB);
    }

    private LLVMValueRef GenerateFbSetPixelCall(List<Expression> arguments)
    {
        if (arguments.Count != 3)
            throw new Exception("fb_set_pixel() expects exactly 3 arguments (x, y, color).");

        var x = VisitExpression(arguments[0]);
        var y = VisitExpression(arguments[1]);
        var color = VisitExpression(arguments[2]);
        EmitFbSetPixel(x, y, color);
        return LLVMValueRef.CreateConstInt(GetInt32Type(), 0);
    }

    private LLVMValueRef GenerateFbFillRectCall(List<Expression> arguments)
    {
        if (arguments.Count != 5)
            throw new Exception("fb_fill_rect() expects exactly 5 arguments (x, y, w, h, color).");

        var i32 = GetInt32Type();
        var x0 = ConvertToType(VisitExpression(arguments[0]), i32);
        var y0 = ConvertToType(VisitExpression(arguments[1]), i32);
        var w = ConvertToType(VisitExpression(arguments[2]), i32);
        var h = ConvertToType(VisitExpression(arguments[3]), i32);
        var color = ConvertToType(VisitExpression(arguments[4]), i32);

        return EmitFbFillRect(x0, y0, w, h, color);
    }

    private LLVMValueRef GenerateFbClearCall(List<Expression> arguments)
    {
        if (arguments.Count != 1)
            throw new Exception("fb_clear() expects exactly 1 argument (color).");

        var i32 = GetInt32Type();
        var color = VisitExpression(arguments[0]);
        var width = _builder.BuildLoad2(i32, GetOrCreateFbWidthGlobal(), "fb_width_val");
        var height = _builder.BuildLoad2(i32, GetOrCreateFbHeightGlobal(), "fb_height_val");

        return EmitFbFillRect(LLVMValueRef.CreateConstInt(i32, 0), LLVMValueRef.CreateConstInt(i32, 0), width, height, color);
    }

    private LLVMValueRef EmitFbFillRect(LLVMValueRef x0, LLVMValueRef y0, LLVMValueRef w, LLVMValueRef h, LLVMValueRef color)
    {
        var i32 = GetInt32Type();
        var function = _builder.InsertBlock.Parent;
        var yAlloca = BuildEntryAlloca(i32, "fb_rect_y");
        _builder.BuildStore(LLVMValueRef.CreateConstInt(i32, 0), yAlloca);

        var yCondBB = _context.AppendBasicBlock(function, "fbrectycond");
        var yBodyBB = _context.AppendBasicBlock(function, "fbrectybody");
        var yEndBB = _context.AppendBasicBlock(function, "fbrectyend");

        _builder.BuildBr(yCondBB);
        _builder.PositionAtEnd(yCondBB);
        var yVal = _builder.BuildLoad2(i32, yAlloca, "y");
        var yCont = _builder.BuildICmp(LLVMIntPredicate.LLVMIntULT, yVal, h, "y_cont");
        _builder.BuildCondBr(yCont, yBodyBB, yEndBB);

        _builder.PositionAtEnd(yBodyBB);
        var xAlloca = BuildEntryAlloca(i32, "fb_rect_x");
        _builder.BuildStore(LLVMValueRef.CreateConstInt(i32, 0), xAlloca);

        var xCondBB = _context.AppendBasicBlock(function, "fbrectxcond");
        var xBodyBB = _context.AppendBasicBlock(function, "fbrectxbody");
        var xEndBB = _context.AppendBasicBlock(function, "fbrectxend");

        _builder.BuildBr(xCondBB);
        _builder.PositionAtEnd(xCondBB);
        var xVal = _builder.BuildLoad2(i32, xAlloca, "x");
        var xCont = _builder.BuildICmp(LLVMIntPredicate.LLVMIntULT, xVal, w, "x_cont");
        _builder.BuildCondBr(xCont, xBodyBB, xEndBB);

        _builder.PositionAtEnd(xBodyBB);
        var absX = _builder.BuildAdd(x0, xVal, "abs_x");
        var absY = _builder.BuildAdd(y0, yVal, "abs_y");
        EmitFbSetPixel(absX, absY, color);
        var nextX = _builder.BuildAdd(xVal, LLVMValueRef.CreateConstInt(i32, 1), "next_x");
        _builder.BuildStore(nextX, xAlloca);
        _builder.BuildBr(xCondBB);

        _builder.PositionAtEnd(xEndBB);
        var nextY = _builder.BuildAdd(yVal, LLVMValueRef.CreateConstInt(i32, 1), "next_y");
        _builder.BuildStore(nextY, yAlloca);
        _builder.BuildBr(yCondBB);

        _builder.PositionAtEnd(yEndBB);
        return LLVMValueRef.CreateConstInt(i32, 0);
    }
}
