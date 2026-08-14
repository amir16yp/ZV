using System;
using System.Collections.Generic;
using LLVMSharp.Interop;
using ZV.Compiler.AST;

namespace ZV.Compiler.Backend;

// Low-level intrinsics useful for freestanding/OS-dev targets: port I/O, interrupt
// control and volatile memory access. These lower directly to x86 inline assembly,
// so they only make sense when targeting x86 (e.g. the "os-x86" target).
public partial class LlvmGenerator
{
    private LLVMValueRef BuildAsmCall(LLVMTypeRef asmType, string asm, string constraints, LLVMValueRef[] args, string name = "")
    {
        var inlineAsm = LLVMValueRef.CreateConstInlineAsm(asmType, asm, constraints, true, false);
        return _builder.BuildCall2(asmType, inlineAsm, args, name);
    }

    private LLVMValueRef GenerateHaltCall()
    {
        var asmType = LLVMTypeRef.CreateFunction(GetVoidType(), Array.Empty<LLVMTypeRef>());
        return BuildAsmCall(asmType, "hlt", "", Array.Empty<LLVMValueRef>());
    }

    private LLVMValueRef GenerateCliCall()
    {
        var asmType = LLVMTypeRef.CreateFunction(GetVoidType(), Array.Empty<LLVMTypeRef>());
        return BuildAsmCall(asmType, "cli", "", Array.Empty<LLVMValueRef>());
    }

    private LLVMValueRef GenerateStiCall()
    {
        var asmType = LLVMTypeRef.CreateFunction(GetVoidType(), Array.Empty<LLVMTypeRef>());
        return BuildAsmCall(asmType, "sti", "", Array.Empty<LLVMValueRef>());
    }

    // Low-level port I/O, taking already-visited LLVM values. Shared by the port_in/port_out
    // builtins as well as the higher-level serial/VGA builtins.
    private LLVMValueRef EmitPortOut(LLVMValueRef port, LLVMValueRef value, int width)
    {
        var valueType = width switch { 8 => GetInt8Type(), 16 => GetInt16Type(), 32 => GetInt32Type(), _ => throw new Exception("Invalid port width.") };
        var mnemonic = width switch { 8 => "outb %al, %dx", 16 => "outw %ax, %dx", 32 => "outl %eax, %dx", _ => throw new Exception("Invalid port width.") };
        var inReg = width switch { 8 => "al", 16 => "ax", 32 => "eax", _ => "al" };

        port = ConvertToType(port, GetInt16Type());
        value = ConvertToType(value, valueType);

        var asmType = LLVMTypeRef.CreateFunction(GetVoidType(), new[] { GetInt16Type(), valueType });
        return BuildAsmCall(asmType, mnemonic, $"{{dx}},{{{inReg}}}", new[] { port, value });
    }

    private LLVMValueRef EmitPortIn(LLVMValueRef port, int width)
    {
        var valueType = width switch { 8 => GetInt8Type(), 16 => GetInt16Type(), 32 => GetInt32Type(), _ => throw new Exception("Invalid port width.") };
        var mnemonic = width switch { 8 => "inb %dx, %al", 16 => "inw %dx, %ax", 32 => "inl %dx, %eax", _ => throw new Exception("Invalid port width.") };
        var outReg = width switch { 8 => "al", 16 => "ax", 32 => "eax", _ => "al" };

        port = ConvertToType(port, GetInt16Type());

        var asmType = LLVMTypeRef.CreateFunction(valueType, new[] { GetInt16Type() });
        return BuildAsmCall(asmType, mnemonic, $"={{{outReg}}},{{dx}}", new[] { port }, "portin");
    }

    private LLVMValueRef GeneratePortOutCall(List<Expression> arguments, int width)
    {
        if (arguments.Count != 2)
            throw new Exception($"port_out{width}() expects exactly 2 arguments (port, value).");

        return EmitPortOut(VisitExpression(arguments[0]), VisitExpression(arguments[1]), width);
    }

    private LLVMValueRef GeneratePortInCall(List<Expression> arguments, int width)
    {
        if (arguments.Count != 1)
            throw new Exception($"port_in{width}() expects exactly 1 argument (port).");

        return EmitPortIn(VisitExpression(arguments[0]), width);
    }

    private LLVMValueRef GenerateVolatileReadCall(List<Expression> arguments)
    {
        if (arguments.Count != 1)
            throw new Exception("volatile_read() expects exactly 1 argument (pointer).");

        var ptr = VisitExpression(arguments[0]);
        if (ptr.TypeOf.Kind != LLVMTypeKind.LLVMPointerTypeKind)
            throw new Exception("volatile_read() argument must be a pointer.");

        var elementType = ptr.TypeOf.ElementType;
        var load = _builder.BuildLoad2(elementType, ptr, "vread");
        load.Volatile = true;
        return load;
    }

    private LLVMValueRef GenerateVolatileWriteCall(List<Expression> arguments)
    {
        if (arguments.Count != 2)
            throw new Exception("volatile_write() expects exactly 2 arguments (pointer, value).");

        var ptr = VisitExpression(arguments[0]);
        if (ptr.TypeOf.Kind != LLVMTypeKind.LLVMPointerTypeKind)
            throw new Exception("volatile_write() first argument must be a pointer.");

        var elementType = ptr.TypeOf.ElementType;
        var value = ConvertToType(VisitExpression(arguments[1]), elementType);
        var store = _builder.BuildStore(value, ptr);
        store.Volatile = true;
        return store;
    }
}
