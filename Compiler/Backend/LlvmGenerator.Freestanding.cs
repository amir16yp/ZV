using System;
using LLVMSharp.Interop;

namespace ZV.Compiler.Backend;

public partial class LlvmGenerator
{
    /// <summary>
    /// Emits a Multiboot v1 header and a "_start" entry point that calls the ZV "main"
    /// function and then halts, so the module can be linked into a bootable x86 kernel.
    /// Must be called after Generate() has produced the "main" function.
    /// </summary>
    public void GenerateFreestandingEntry()
    {
        EmitMultibootHeader();
        EmitStartFunction();
    }

    // Requested linear framebuffer mode (Multiboot header fields, honored by GRUB; QEMU's
    // built-in "-kernel" multiboot loader does not set up VBE, so fb_available() will be
    // false there - this is meant for booting through a real bootloader like GRUB).
    private const uint RequestedFbWidth = 800;
    private const uint RequestedFbHeight = 600;
    private const uint RequestedFbDepth = 32;

    private void EmitMultibootHeader()
    {
        const uint magic = 0x1BADB002;
        const uint flags = 0x4; // bit 2: request a video mode (see mode_type/width/height/depth below)
        uint checksum = unchecked((uint)-(long)(magic + flags));

        var i32 = GetInt32Type();
        var headerType = LLVMTypeRef.CreateArray(i32, 7);
        var header = _module.AddGlobal(headerType, "multiboot_header");
        header.Initializer = LLVMValueRef.CreateConstArray(i32, new[]
        {
            LLVMValueRef.CreateConstInt(i32, magic),
            LLVMValueRef.CreateConstInt(i32, flags),
            LLVMValueRef.CreateConstInt(i32, checksum),
            LLVMValueRef.CreateConstInt(i32, 0), // mode_type: 0 = linear graphics
            LLVMValueRef.CreateConstInt(i32, RequestedFbWidth),
            LLVMValueRef.CreateConstInt(i32, RequestedFbHeight),
            LLVMValueRef.CreateConstInt(i32, RequestedFbDepth)
        });
        header.Linkage = LLVMLinkage.LLVMExternalLinkage;
        header.IsGlobalConstant = true;
        header.Section = ".multiboot";
    }

    private void EmitStartFunction()
    {
        if (_entryFunction.Handle == IntPtr.Zero)
            throw new Exception("Freestanding os-x86 target requires an entry function (main).");

        var startType = LLVMTypeRef.CreateFunction(GetVoidType(), Array.Empty<LLVMTypeRef>());
        var startFunc = _module.AddFunction("_start", startType);

        var entry = _context.AppendBasicBlock(startFunc, "entry");
        var hang = _context.AppendBasicBlock(startFunc, "hang");

        _builder.PositionAtEnd(entry);

        // Capture EBX (the Multiboot info struct pointer set by the bootloader before jumping
        // to our entry point) as the very first thing we do, before it can be clobbered.
        var captureType = LLVMTypeRef.CreateFunction(GetInt32Type(), Array.Empty<LLVMTypeRef>());
        var mbInfoPtrValue = BuildAsmCall(captureType, "movl %ebx, $0", "=r", Array.Empty<LLVMValueRef>(), "mbinfo_ptr");
        _builder.BuildStore(mbInfoPtrValue, GetOrCreateMultibootInfoPtrGlobal());

        EmitParseMultibootFramebufferInfo(mbInfoPtrValue);

        var argc = LLVMValueRef.CreateConstInt(GetInt32Type(), 0);
        var argv = LLVMValueRef.CreateConstNull(GetPointerType(GetPointerType(GetInt8Type())));
        _builder.BuildCall2(_entryFunctionType, _entryFunction, new[] { argc, argv }, "");
        _builder.BuildBr(hang);

        _builder.PositionAtEnd(hang);
        _builder.BuildBr(hang);
    }
}
