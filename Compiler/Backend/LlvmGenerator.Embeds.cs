using System;
using System.Collections.Generic;
using System.Linq;
using LLVMSharp.Interop;
using ZV.Compiler.AST;

namespace ZV.Compiler.Backend;

public partial class LlvmGenerator
{
    // Hosted-only embed builtins. These access the typed global arrays emitted by
    // EmitHostedEmbeds. They return sensible defaults (0 / null) when no matching
    // embed exists.

    private LLVMValueRef GenerateResourceCountCall(List<Expression> arguments)
    {
        if (arguments.Count != 0)
            throw new Exception("resource_count() takes no arguments.");
        if (_resourceCountGlobal == null)
            return LLVMValueRef.CreateConstInt(GetInt32Type(), 0);
        return _builder.BuildLoad2(GetInt32Type(), _resourceCountGlobal.Value, "resource_count");
    }

    private LLVMValueRef GenerateResourceNameCall(List<Expression> arguments)
    {
        return BuildEmbedElementAccess(arguments, "resource_name", _resourceNameArray, GetInt32Type(), GetPointerType(GetInt8Type()),
            () => LLVMValueRef.CreateConstNull(GetPointerType(GetInt8Type())));
    }

    private LLVMValueRef GenerateResourcePtrCall(List<Expression> arguments)
    {
        return BuildEmbedElementAccess(arguments, "resource_ptr", _resourceDataArray, GetInt32Type(), GetPointerType(GetInt8Type()),
            () => LLVMValueRef.CreateConstNull(GetPointerType(GetInt8Type())));
    }

    private LLVMValueRef GenerateResourceSizeCall(List<Expression> arguments)
    {
        return BuildEmbedElementAccess(arguments, "resource_size", _resourceSizeArray, GetInt32Type(), GetSizeType(),
            () => LLVMValueRef.CreateConstInt(GetSizeType(), 0));
    }

    private LLVMValueRef GenerateFileCountCall(List<Expression> arguments)
    {
        if (arguments.Count != 0)
            throw new Exception("file_count() takes no arguments.");
        if (_fileCountGlobal == null)
            return LLVMValueRef.CreateConstInt(GetInt32Type(), 0);
        return _builder.BuildLoad2(GetInt32Type(), _fileCountGlobal.Value, "file_count");
    }

    private LLVMValueRef GenerateFileNameCall(List<Expression> arguments)
    {
        return BuildEmbedElementAccess(arguments, "file_name", _fileNameArray, GetInt32Type(), GetPointerType(GetInt8Type()),
            () => LLVMValueRef.CreateConstNull(GetPointerType(GetInt8Type())));
    }

    private LLVMValueRef GenerateFilePtrCall(List<Expression> arguments)
    {
        return BuildEmbedElementAccess(arguments, "file_ptr", _fileDataArray, GetInt32Type(), GetPointerType(GetInt8Type()),
            () => LLVMValueRef.CreateConstNull(GetPointerType(GetInt8Type())));
    }

    private LLVMValueRef GenerateFileSizeCall(List<Expression> arguments)
    {
        return BuildEmbedElementAccess(arguments, "file_size", _fileSizeArray, GetInt32Type(), GetSizeType(),
            () => LLVMValueRef.CreateConstInt(GetSizeType(), 0));
    }

    private LLVMValueRef GenerateFindFileCall(List<Expression> arguments)
    {
        if (arguments.Count != 3)
            throw new Exception("find_file(name, outPtr, outSize) expects 3 arguments.");

        var name = ConvertToType(VisitExpression(arguments[0]), GetPointerType(GetInt8Type()));
        var outPtr = ConvertToType(VisitExpression(arguments[1]), GetPointerType(GetPointerType(GetInt8Type())));
        var outSize = ConvertToType(VisitExpression(arguments[2]), GetPointerType(GetSizeType()));

        if (_fileNameArray == null || _fileCountGlobal == null)
        {
            return LLVMValueRef.CreateConstInt(GetInt32Type(), 0);
        }

        var i8Ptr = GetPointerType(GetInt8Type());
        var sizeType = GetSizeType();
        var funcType = LLVMTypeRef.CreateFunction(GetInt32Type(), new[]
        {
            i8Ptr,
            GetPointerType(i8Ptr),
            GetPointerType(sizeType)
        });
        var func = GetOrAddFunction("__zv_find_file", GetInt32Type(), new[] { i8Ptr, GetPointerType(i8Ptr), GetPointerType(sizeType) });
        return _builder.BuildCall2(funcType, func, new[] { name, outPtr, outSize }, "find_file");
    }

    private LLVMValueRef BuildEmbedElementAccess(
        List<Expression> arguments,
        string builtinName,
        LLVMValueRef? arrayGlobal,
        LLVMTypeRef indexType,
        LLVMTypeRef elementType,
        System.Func<LLVMValueRef> defaultValue)
    {
        if (arguments.Count != 1)
            throw new Exception($"{builtinName}() expects exactly 1 argument (index).");

        if (arrayGlobal == null)
            return defaultValue();

        var index = ConvertToType(VisitExpression(arguments[0]), indexType);
        var arrayType = arrayGlobal.Value.TypeOf;
        var elementPtr = _builder.BuildGEP2(arrayType, arrayGlobal.Value,
            new[] { LLVMValueRef.CreateConstInt(GetInt32Type(), 0), index }, $"{builtinName}_ptr");
        return _builder.BuildLoad2(elementType, elementPtr, builtinName);
    }
}
