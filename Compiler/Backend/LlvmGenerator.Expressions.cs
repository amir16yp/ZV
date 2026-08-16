using System;
using System.Collections.Generic;
using LLVMSharp.Interop;
using ZV.Compiler.AST;
using ZV.Compiler.Lexer;

namespace ZV.Compiler.Backend;

public partial class LlvmGenerator
{
    private LLVMValueRef VisitSet(SetExpr expr)
    {
        var obj = VisitExpressionForPointer(expr.Object);
        var structPtr = obj.Pointer;
        var structType = obj.Type;
        var structName = obj.StructName;

        if (string.IsNullOrEmpty(structName) || !_structFieldNames.ContainsKey(structName))
        {
            throw new Exception("Cannot access field on non-struct type.");
        }

        int fieldIndex = GetStructFieldIndex(structName, expr.Name.Lexeme);
        if (fieldIndex == -1)
        {
            throw new Exception($"Struct {structName} does not have field {expr.Name.Lexeme}");
        }

        var fieldPtr = _builder.BuildStructGEP2(structType, structPtr, (uint)fieldIndex, "fieldptr");
        var fieldType = _structFieldTypes[structName][fieldIndex];
        string? fieldStructName = GetStructNameForLlvmType(fieldType);
        int cstrTempMark = _pendingCstrTemps.Count;
        LLVMValueRef value;
        if (expr.Value is StructInitExpr rightStructInit && fieldStructName != null)
        {
            value = VisitStructInit(rightStructInit, fieldStructName);
        }
        else if (IsOwnedFieldAccess(expr.Value))
        {
            // Moving an owning field out of an owned struct zeros the source field and
            // transfers ownership to the destination field.
            value = VisitMove(expr.Value);
        }
        else
        {
            value = VisitExpression(expr.Value);
        }
        value = ConvertToType(value, fieldType);

        // If the right-hand side is an owned variable, assigning it into an owning
        // field transfers ownership to the containing struct, so the source is dead.
        TryTransferOwnership(expr.Value);

        _builder.BuildStore(value, fieldPtr);
        ClaimCstrTempIfOwningField(structName, fieldIndex, cstrTempMark);
        return value;
    }

    // A cstr() temporary assigned into a struct field would otherwise be freed as an
    // "unclaimed" temporary at the end of the statement (see FreeUnclaimedCstrTemps),
    // immediately followed by a double-free when the owning struct's field is later
    // destroyed at scope exit. Claim it here instead, the same way a direct variable
    // binding does, so the struct field becomes the sole owner.
    private void ClaimCstrTempIfOwningField(string structName, int fieldIndex, int cstrTempMark)
    {
        if (_pendingCstrTemps.Count <= cstrTempMark) return;
        var fieldTypeNode = _structFieldTypeNodes[structName][fieldIndex];
        if (fieldTypeNode is PrimitiveTypeNode p && p.Type.Type == TokenType.CSTRING)
        {
            ClaimCstrTemps(cstrTempMark);
        }
    }

    private LLVMValueRef VisitLiteral(LiteralExpr expr)
    {
        // Integer literals were historically always emitted as i32, which truncates
        // 64-bit hex constants and mis-parses values above uint.MaxValue. Emit them
        // as i64 when they don't fit in a signed 32-bit value.
        if (expr.Type == TokenType.IntegerLiteral)
        {
            if (expr.Value is long longVal)
            {
                if (longVal < 0 || (ulong)longVal > uint.MaxValue)
                {
                    return LLVMValueRef.CreateConstInt(GetInt64Type(), (ulong)longVal, false);
                }
                return LLVMValueRef.CreateConstInt(GetInt32Type(), (ulong)longVal, false);
            }
            return LLVMValueRef.CreateConstInt(GetInt32Type(), ulong.Parse(expr.Value?.ToString() ?? "0"));
        }

        return expr.Type switch
        {
            TokenType.FloatLiteral => LLVMValueRef.CreateConstReal(GetDoubleType(), double.Parse(expr.Value?.ToString() ?? "0")),
            TokenType.True => LLVMValueRef.CreateConstInt(GetInt1Type(), 1),
            TokenType.False => LLVMValueRef.CreateConstInt(GetInt1Type(), 0),
            TokenType.StringLiteral => BuildStringLiteral(expr.Value?.ToString() ?? ""),
            TokenType.CharacterLiteral => LLVMValueRef.CreateConstInt(GetInt8Type(), (ulong)(char)expr.Value!),
            TokenType.Null => LLVMValueRef.CreateConstNull(GetPointerType(GetInt8Type())),
            _ => throw new NotImplementedException($"Literal type {expr.Type} not implemented.")
        };
    }

    private LLVMValueRef BuildStringLiteral(string text)
    {
        var dataPtr = GetOrCreateGlobalStringPtr(text, "strlit");
        var length = LLVMValueRef.CreateConstInt(GetInt64Type(), (ulong)System.Text.Encoding.UTF8.GetByteCount(text), false);
        var strType = GetStringStructType();
        return LLVMValueRef.CreateConstNamedStruct(strType, new[] { dataPtr, length });
    }

    private LLVMValueRef VisitBinary(BinaryExpr expr)
    {
        if (expr.Operator.Type == TokenType.Equals)
        {
            if (expr.Left is VariableExpr varExpr)
            {
                if (_constVariables.Contains(varExpr.Name))
                {
                    throw new Exception($"Cannot assign to CONST variable '{varExpr.Name}'.");
                }

                if (!_namedValues.TryGetValue(varExpr.Name, out var entry))
                {
                    throw new Exception($"Unknown variable name: {varExpr.Name}");
                }

                var targetNewtype = _variableNewtypeNames.TryGetValue(varExpr.Name, out var tn) ? tn : null;
                CheckNewtypeAssignable(targetNewtype, expr.Right, expr.Location);

                LLVMValueRef rightVal;
                if (expr.Right is StructInitExpr structInit && entry.StructName != null)
                {
                    rightVal = VisitStructInit(structInit, entry.StructName);
                }
                else if (IsOwnedFieldAccess(expr.Right))
                {
                    // Moving an owning field out of an owned struct zeros the source field and
                    // transfers ownership to the left-hand side.
                    rightVal = VisitMove(expr.Right);
                }
                else
                {
                    rightVal = VisitExpression(expr.Right);
                }

                // Fixed-size arrays are copied by value (memmove) rather than stored as pointers.
                if (entry.Type.Kind == LLVMTypeKind.LLVMArrayTypeKind &&
                    TryGetFixedArrayInfo(expr.Right, out var srcArrayType, out var srcArrayPtr) &&
                    srcArrayType.Handle == entry.Type.Handle)
                {
                    EmitFixedArrayCopy(entry.Value, srcArrayPtr, entry.Type);
                    return rightVal;
                }

                rightVal = ConvertToType(rightVal, entry.Type);

                bool lhsWasOwned = _ownedVariables.Contains(varExpr.Name);

                // Plain assignment from an owned variable transfers ownership implicitly
                // (same as move()), so assigning an owning value never creates a shallow
                // alias that could dangle when the source is destroyed.
                bool rhsTransferredOwnership = TryTransferOwnership(expr.Right, expr.Left);
                bool rhsIsOwned = rhsTransferredOwnership ||
                                  IsOwnedFieldAccess(expr.Right) ||
                                  IsOwnedExpression(expr.Right, rightVal) ||
                                  IsOwningStructConstruction(expr.Right, entry.StructName);

                // If the left side currently owns heap memory and we are overwriting it
                // with a new owned value, free the old memory first to avoid a leak.
                if (lhsWasOwned && rhsIsOwned && !IsSameVariable(expr.Left, expr.Right))
                {
                    FreeExpression(new VariableExpr(varExpr.Name, new SourceLocation(null, 0, 0, 0)));
                }

                _builder.BuildStore(rightVal, entry.Value);
                // Reassigning a variable gives it a new value, reviving it if it was
                // previously freed or moved away.
                _deadVariables.Remove(varExpr.Name);

                if (rhsIsOwned)
                {
                    AddOwnedVariable(varExpr.Name);
                }

                return rightVal;
            }
            if (expr.Left is GetExpr getExpr)
            {
                var obj = VisitExpressionForPointer(getExpr.Object);
                var structPtr = obj.Pointer;
                var structType = obj.Type;
                var structName = obj.StructName;

                if (string.IsNullOrEmpty(structName) || !_structFieldNames.ContainsKey(structName))
                {
                    throw new Exception("Cannot access field on non-struct type.");
                }

                int fieldIndex = GetStructFieldIndex(structName, getExpr.Name.Lexeme);
                if (fieldIndex == -1)
                {
                    throw new Exception($"Struct {structName} does not have field {getExpr.Name.Lexeme}");
                }

                var fieldPtr = _builder.BuildStructGEP2(structType, structPtr, (uint)fieldIndex, "fieldptr");
                
                LLVMValueRef value;
                var fieldType = _structFieldTypes[structName][fieldIndex];
                
                string? fieldStructName = GetStructNameForLlvmType(fieldType);

                int cstrTempMark = _pendingCstrTemps.Count;
                if (expr.Right is StructInitExpr rightStructInit && fieldStructName != null)
                {
                    value = VisitStructInit(rightStructInit, fieldStructName);
                }
                else
                {
                    value = VisitExpression(expr.Right);
                }
                
                value = ConvertToType(value, fieldType);

                // If the right-hand side is an owned variable, assigning it into an owning
                // field transfers ownership to the containing struct, so the source is dead.
                TryTransferOwnership(expr.Right);

                _builder.BuildStore(value, fieldPtr);
                ClaimCstrTempIfOwningField(structName, fieldIndex, cstrTempMark);
                return value;
            }
            throw new NotImplementedException("Assignment only supported for variables and fields.");
        }

        if (expr.Operator.Type == TokenType.AmpersandAmpersand)
        {
            var left = VisitExpression(expr.Left);
            var currentFunc = _builder.InsertBlock.Parent;
            var rhsBlock = _context.AppendBasicBlock(currentFunc, "and_rhs");
            var mergeBlock = _context.AppendBasicBlock(currentFunc, "and_merge");
            
            _builder.BuildCondBr(left, rhsBlock, mergeBlock);
            
            var leftBlock = _builder.InsertBlock;
            _builder.PositionAtEnd(rhsBlock);
            var right = VisitExpression(expr.Right);
            _builder.BuildBr(mergeBlock);
            var rightBlock = _builder.InsertBlock;
            
            _builder.PositionAtEnd(mergeBlock);
            var phi = _builder.BuildPhi(GetInt1Type(), "and_tmp");
            phi.AddIncoming(new[] { left, right }, new[] { leftBlock, rightBlock }, 2);
            return phi;
        }

        if (expr.Operator.Type == TokenType.PipePipe)
        {
            var left = VisitExpression(expr.Left);
            var currentFunc = _builder.InsertBlock.Parent;
            var rhsBlock = _context.AppendBasicBlock(currentFunc, "or_rhs");
            var mergeBlock = _context.AppendBasicBlock(currentFunc, "or_merge");
            
            _builder.BuildCondBr(left, mergeBlock, rhsBlock);
            
            var leftBlock = _builder.InsertBlock;
            _builder.PositionAtEnd(rhsBlock);
            var right = VisitExpression(expr.Right);
            _builder.BuildBr(mergeBlock);
            var rightBlock = _builder.InsertBlock;
            
            _builder.PositionAtEnd(mergeBlock);
            var phi = _builder.BuildPhi(GetInt1Type(), "or_tmp");
            phi.AddIncoming(new[] { left, right }, new[] { leftBlock, rightBlock }, 2);
            return phi;
        }

        var leftValNorm = VisitExpression(expr.Left);
        var rightValNorm = VisitExpression(expr.Right);

        // Operand signedness, inferred from the declared ZV types on each side (see
        // InferExprTypeNode). Only integer ops that care about signedness (/, %, and the
        // ordered comparisons) use this; bitwise ops and equality are bit-pattern-exact
        // regardless of signedness.
        bool isUnsignedOp = IsUnsignedPrimitiveTypeNode(InferExprTypeNode(expr.Left)) &&
                             IsUnsignedPrimitiveTypeNode(InferExprTypeNode(expr.Right));

        (leftValNorm, rightValNorm) = PromoteBinaryOperands(leftValNorm, rightValNorm, isUnsignedOp);

        // STRING concatenation
        if (expr.Operator.Type == TokenType.Plus && IsStringStructType(leftValNorm.TypeOf) && IsStringStructType(rightValNorm.TypeOf))
        {
            return BuildStringConcat(leftValNorm, rightValNorm);
        }

        // STRING content equality
        if ((expr.Operator.Type == TokenType.EqualsEquals || expr.Operator.Type == TokenType.BangEquals) &&
            IsStringStructType(leftValNorm.TypeOf) && IsStringStructType(rightValNorm.TypeOf))
        {
            var areEqual = BuildStringEquals(leftValNorm, rightValNorm);
            if (expr.Operator.Type == TokenType.EqualsEquals) return areEqual;
            return _builder.BuildNot(areEqual, "str_ne");
        }

        bool isFloat = IsFloatingType(leftValNorm.TypeOf) || IsFloatingType(rightValNorm.TypeOf);

        if (isFloat)
        {
            return expr.Operator.Type switch
            {
                TokenType.Plus => _builder.BuildFAdd(leftValNorm, rightValNorm, "faddtmp"),
                TokenType.Minus => _builder.BuildFSub(leftValNorm, rightValNorm, "fsubtmp"),
                TokenType.Star => _builder.BuildFMul(leftValNorm, rightValNorm, "fmultmp"),
                TokenType.Slash => _builder.BuildFDiv(leftValNorm, rightValNorm, "fdivtmp"),
                TokenType.Percent => _builder.BuildFRem(leftValNorm, rightValNorm, "fremtmp"),
                TokenType.Less => _builder.BuildFCmp(LLVMRealPredicate.LLVMRealOLT, leftValNorm, rightValNorm, "flttmp"),
                TokenType.Greater => _builder.BuildFCmp(LLVMRealPredicate.LLVMRealOGT, leftValNorm, rightValNorm, "fgttmp"),
                TokenType.LessEquals => _builder.BuildFCmp(LLVMRealPredicate.LLVMRealOLE, leftValNorm, rightValNorm, "fletmp"),
                TokenType.GreaterEquals => _builder.BuildFCmp(LLVMRealPredicate.LLVMRealOGE, leftValNorm, rightValNorm, "fgetmp"),
                TokenType.EqualsEquals => _builder.BuildFCmp(LLVMRealPredicate.LLVMRealOEQ, leftValNorm, rightValNorm, "feqtmp"),
                TokenType.BangEquals => _builder.BuildFCmp(LLVMRealPredicate.LLVMRealONE, leftValNorm, rightValNorm, "fnetmp"),
                _ => throw new NotImplementedException($"Operator {expr.Operator.Type} not implemented for floating-point operands.")
            };
        }

        return expr.Operator.Type switch
        {
            TokenType.Plus => _builder.BuildAdd(leftValNorm, rightValNorm, "addtmp"),
            TokenType.Minus => _builder.BuildSub(leftValNorm, rightValNorm, "subtmp"),
            TokenType.Star => _builder.BuildMul(leftValNorm, rightValNorm, "multmp"),
            TokenType.Slash => isUnsignedOp
                ? _builder.BuildUDiv(leftValNorm, rightValNorm, "divtmp")
                : _builder.BuildSDiv(leftValNorm, rightValNorm, "divtmp"),
            TokenType.Percent => isUnsignedOp
                ? _builder.BuildURem(leftValNorm, rightValNorm, "remtmp")
                : _builder.BuildSRem(leftValNorm, rightValNorm, "remtmp"),
            TokenType.Ampersand => _builder.BuildAnd(leftValNorm, rightValNorm, "andtmp"),
            TokenType.Pipe => _builder.BuildOr(leftValNorm, rightValNorm, "ortmp"),
            TokenType.Caret => _builder.BuildXor(leftValNorm, rightValNorm, "xortmp"),
            // Logical (unsigned) shift for `>>`. This is correct for both signed and unsigned
            // operands in practice for this language (there's no separate arithmetic right
            // shift operator), so it's left as-is regardless of isUnsignedOp.
            TokenType.LessLess => _builder.BuildShl(leftValNorm, rightValNorm, "shltmp"),
            TokenType.GreaterGreater => _builder.BuildLShr(leftValNorm, rightValNorm, "shrtmp"),
            TokenType.Less => _builder.BuildICmp(isUnsignedOp ? LLVMIntPredicate.LLVMIntULT : LLVMIntPredicate.LLVMIntSLT, leftValNorm, rightValNorm, "lttmp"),
            TokenType.Greater => _builder.BuildICmp(isUnsignedOp ? LLVMIntPredicate.LLVMIntUGT : LLVMIntPredicate.LLVMIntSGT, leftValNorm, rightValNorm, "gttmp"),
            TokenType.LessEquals => _builder.BuildICmp(isUnsignedOp ? LLVMIntPredicate.LLVMIntULE : LLVMIntPredicate.LLVMIntSLE, leftValNorm, rightValNorm, "letmp"),
            TokenType.GreaterEquals => _builder.BuildICmp(isUnsignedOp ? LLVMIntPredicate.LLVMIntUGE : LLVMIntPredicate.LLVMIntSGE, leftValNorm, rightValNorm, "getmp"),
            TokenType.EqualsEquals => _builder.BuildICmp(LLVMIntPredicate.LLVMIntEQ, leftValNorm, rightValNorm, "eqtmp"),
            TokenType.BangEquals => _builder.BuildICmp(LLVMIntPredicate.LLVMIntNE, leftValNorm, rightValNorm, "netmp"),
            _ => throw new NotImplementedException($"Operator {expr.Operator.Type} not implemented.")
        };
    }

    private (LLVMValueRef Left, LLVMValueRef Right) PromoteBinaryOperands(LLVMValueRef left, LLVMValueRef right, bool isUnsigned = false)
    {
        bool leftFloat = IsFloatingType(left.TypeOf);
        bool rightFloat = IsFloatingType(right.TypeOf);

        if (left.TypeOf.Kind == LLVMTypeKind.LLVMIntegerTypeKind && right.TypeOf.Kind == LLVMTypeKind.LLVMIntegerTypeKind)
        {
            if (left.TypeOf.IntWidth != right.TypeOf.IntWidth)
            {
                var targetType = left.TypeOf.IntWidth > right.TypeOf.IntWidth ? left.TypeOf : right.TypeOf;
                if (left.TypeOf.IntWidth < targetType.IntWidth)
                    left = isUnsigned ? _builder.BuildZExt(left, targetType, "promote") : _builder.BuildSExt(left, targetType, "promote");
                if (right.TypeOf.IntWidth < targetType.IntWidth)
                    right = isUnsigned ? _builder.BuildZExt(right, targetType, "promote") : _builder.BuildSExt(right, targetType, "promote");
            }
        }
        else if (leftFloat && rightFloat)
        {
            // Promote narrower float to wider float.
            LLVMTypeRef targetType;
            if (left.TypeOf.Kind == LLVMTypeKind.LLVMDoubleTypeKind || right.TypeOf.Kind == LLVMTypeKind.LLVMDoubleTypeKind)
                targetType = GetDoubleType();
            else
                targetType = GetFloatType();
            if (left.TypeOf.Handle != targetType.Handle)
                left = _builder.BuildFPCast(left, targetType, "fpromote");
            if (right.TypeOf.Handle != targetType.Handle)
                right = _builder.BuildFPCast(right, targetType, "fpromote");
        }
        else if (leftFloat && right.TypeOf.Kind == LLVMTypeKind.LLVMIntegerTypeKind)
        {
            right = _builder.BuildSIToFP(right, left.TypeOf, "sitofp");
        }
        else if (rightFloat && left.TypeOf.Kind == LLVMTypeKind.LLVMIntegerTypeKind)
        {
            left = _builder.BuildSIToFP(left, right.TypeOf, "sitofp");
        }
        else if (left.TypeOf.Kind == LLVMTypeKind.LLVMPointerTypeKind && right.TypeOf.Kind == LLVMTypeKind.LLVMIntegerTypeKind)
        {
            // Pointer/integer comparison (e.g., ptr == 0). Treat the integer as a null pointer.
            right = LLVMValueRef.CreateConstNull(left.TypeOf);
        }
        else if (left.TypeOf.Kind == LLVMTypeKind.LLVMIntegerTypeKind && right.TypeOf.Kind == LLVMTypeKind.LLVMPointerTypeKind)
        {
            left = LLVMValueRef.CreateConstNull(right.TypeOf);
        }

        return (left, right);
    }

    private LLVMValueRef VisitUnary(UnaryExpr expr)
    {
        return expr.Operator.Type switch
        {
            TokenType.Minus => IsFloatUnary(VisitExpression(expr.Right), out var negVal)
                ? _builder.BuildFNeg(negVal, "fnegtmp")
                : _builder.BuildNeg(negVal, "negtmp"),
            TokenType.Bang => _builder.BuildNot(VisitExpression(expr.Right), "nottmp"),
            TokenType.Tilde => _builder.BuildNot(VisitExpression(expr.Right), "bitnottmp"),
            TokenType.PlusPlus => IncrementValue(expr.Right, expr.Operator.Type, returnOld: false),
            TokenType.MinusMinus => IncrementValue(expr.Right, expr.Operator.Type, returnOld: false),
            _ => throw new NotImplementedException($"Unary operator {expr.Operator.Type} not implemented.")
        };
    }

    private LLVMValueRef VisitMove(Expression operand)
    {
        // move(x): transfers ownership of x's value to the caller.
        // The language-level guarantee is compile-time invalidation: the source variable
        // is conceptually invalid after a move and cannot be used again until reassigned.
        // Runtime zeroing is an implementation detail for debugging safety, NOT the
        // semantic meaning - a moved-from variable is invalid, not zero-valued.
        var ptrInfo = VisitExpressionForPointer(operand);
        var value = _builder.BuildLoad2(ptrInfo.Type, ptrInfo.Pointer, "move_val");
        
        // Runtime zeroing (implementation detail, aids debugging/crash-safety)
        var nullVal = LLVMValueRef.CreateConstNull(ptrInfo.Type);
        _builder.BuildStore(nullVal, ptrInfo.Pointer);

        // Compile-time invalidation: the source is dead after move.
        if (operand is VariableExpr movedVar)
        {
            _deadVariables.Add(movedVar.Name);
            _ownedVariables.Remove(movedVar.Name);
        }
        
        return value;
    }

    private LLVMValueRef VisitCopy(Expression operand)
    {
        // copy() performs a bitwise copy. It is only valid for trivially-copyable
        // (non-owning) values. Copying an owned resource (heap array, moved-from
        // allocation) would create two owners of the same memory, causing double-free.
        if (operand is VariableExpr copyVar)
        {
            if (_ownedVariables.Contains(copyVar.Name))
            {
                throw new CompileException(operand.Location,
                    $"Cannot copy() owned variable '{copyVar.Name}'. " +
                    "Use move() to transfer ownership, or allocate a new copy explicitly.");
            }

            if (_namedValues.TryGetValue(copyVar.Name, out var entry) && IsOwningStructType(entry.StructName))
            {
                throw new CompileException(operand.Location,
                    $"Cannot copy() '{copyVar.Name}': struct '{entry.StructName}' transitively owns heap memory " +
                    "(it has a dynamic array, CSTRING, or owning struct field). Bitwise-copying it would create a " +
                    "second owner of the same memory, causing a double free. Use move() to transfer ownership, " +
                    "or allocate new owned fields explicitly.");
            }
        }
        return VisitExpression(operand);
    }

    private static bool IsFloatUnary(LLVMValueRef value, out LLVMValueRef same)
    {
        same = value;
        return IsFloatingType(value.TypeOf);
    }

    private LLVMValueRef VisitPostfix(PostfixExpr expr)
    {
        return expr.Operator.Type switch
        {
            TokenType.PlusPlus => IncrementValue(expr.Left, expr.Operator.Type, returnOld: true),
            TokenType.MinusMinus => IncrementValue(expr.Left, expr.Operator.Type, returnOld: true),
            _ => throw new NotImplementedException($"Postfix operator {expr.Operator.Type} not implemented.")
        };
    }

    private LLVMValueRef IncrementValue(Expression operand, TokenType op, bool returnOld)
    {
        var ptrInfo = VisitExpressionForPointer(operand);
        var current = _builder.BuildLoad2(ptrInfo.Type, ptrInfo.Pointer, "incval");
        var one = LLVMValueRef.CreateConstInt(ptrInfo.Type, 1);
        var result = op == TokenType.PlusPlus
            ? _builder.BuildAdd(current, one, "inctmp")
            : _builder.BuildSub(current, one, "dectmp");
        _builder.BuildStore(result, ptrInfo.Pointer);
        return returnOld ? current : result;
    }

    private LLVMValueRef VisitIndex(IndexExpr expr)
    {
        // Fixed-size stack arrays are referenced by alloca/global pointer to [N x T].
        if (TryGetFixedArrayInfo(expr.Target, out var fixedArrayType, out var fixedArrayPtr))
        {
            int fixedLength = (int)fixedArrayType.ArrayLength;
            CheckConstantIndexInBounds(expr.Index, fixedLength, expr.Location);

            var fixedIndex = VisitExpression(expr.Index);
            var fixedElementType = fixedArrayType.ElementType;
            if (_builder.InsertBlock.Handle != IntPtr.Zero)
            {
                EmitBoundsCheck(fixedIndex, LLVMValueRef.CreateConstInt(GetInt64Type(), (ulong)fixedLength), expr.Location);
            }
            var fixedZero = LLVMValueRef.CreateConstInt(GetInt32Type(), 0);
            var fixedGepPtr = BuildBoundsCheckedGEP2(fixedArrayType, fixedArrayPtr, new[] { fixedZero, fixedIndex }, "indexptr");
            return _builder.BuildLoad2(fixedElementType, fixedGepPtr, "indexval");
        }

        var target = VisitExpression(expr.Target);
        var index = VisitExpression(expr.Index);
        
        LLVMValueRef dataPtr;
        LLVMTypeRef elementType;

        if (target.TypeOf.Kind == LLVMTypeKind.LLVMStructTypeKind)
        {
            // Assume it's an array struct { T*, i64 }
            var structType = target.TypeOf;
            
            if (_builder.InsertBlock.Handle == IntPtr.Zero)
            {
                dataPtr = target.GetAggregateElement(0);
            }
            else
            {
                dataPtr = _builder.BuildExtractValue(target, 0, "dataptr");
                var lengthField = _builder.BuildExtractValue(target, 1, "lenval");
                EmitBoundsCheck(index, lengthField, expr.Location);
            }
            
            if (!_arrayElementTypes.TryGetValue(structType, out elementType))
            {
                 // Fallback if not tracked (e.g. nested or something else)
                 elementType = structType.GetStructElementTypes()[0].ElementType;
            }
        }
        else if (target.TypeOf.Kind == LLVMTypeKind.LLVMPointerTypeKind)
        {
            RequireUnsafeForRawPointerIndex(expr.Location);
            dataPtr = target;
            // Opaque pointers don't expose a reliable element type, so infer it from
            // the ZV declared type of the pointer expression when possible.
            elementType = InferPointerElementType(expr.Target)
                          ?? (target.TypeOf.ElementType.Handle != IntPtr.Zero ? target.TypeOf.ElementType : GetInt8Type());
        }
        else
        {
            throw new Exception("Index access on non-array/non-pointer type.");
        }

        if (_builder.InsertBlock.Handle == IntPtr.Zero)
        {
            // Global scope constant GEP
            var ptr = LLVMValueRef.CreateConstGEP2(elementType, dataPtr, new[] { index });
            // We can't really "load" a value from a pointer at global scope initializer 
            // unless it's a constant expression that LLVM supports (like bitcast or GEP).
            // But usually indexing into a global array to initialize another global is done via constant expr.
            return ptr; 
        }

        var gepPtr = BuildBoundsCheckedGEP2(elementType, dataPtr, new[] { index }, "indexptr");
        // Dynamic arrays whose elements are fixed-size arrays (e.g. INT32[3][]) are used as
        // contiguous multi-dimensional arrays: indexing returns a pointer to the row, not
        // a loaded array value.
        if (elementType.Kind == LLVMTypeKind.LLVMArrayTypeKind)
        {
            return gepPtr;
        }
        return _builder.BuildLoad2(elementType, gepPtr, "indexval");
    }

    private bool TryGetFixedArrayInfo(Expression expr, out LLVMTypeRef arrayType, out LLVMValueRef arrayPtr)
    {
        arrayType = default;
        arrayPtr = default;

        if (expr is VariableExpr varExpr)
        {
            if (!_namedValues.TryGetValue(varExpr.Name, out var entry)) return false;
            if (entry.Type.Kind != LLVMTypeKind.LLVMArrayTypeKind) return false;

            arrayType = entry.Type;
            arrayPtr = entry.Value;
            return true;
        }

        if (expr is IndexExpr indexExpr)
        {
            // Dynamic array whose element type is a fixed-size array: indexing returns a
            // pointer to a row, so rows[row][col] can be treated as a contiguous 2D array.
            if (indexExpr.Target is VariableExpr targetVar &&
                _namedValues.TryGetValue(targetVar.Name, out var dynEntry) &&
                dynEntry.Type.Kind == LLVMTypeKind.LLVMStructTypeKind &&
                _arrayElementTypes.TryGetValue(dynEntry.Type, out var dynElementType) &&
                dynElementType.Kind == LLVMTypeKind.LLVMArrayTypeKind)
            {
                var structVal = _builder.BuildLoad2(dynEntry.Type, dynEntry.Value, targetVar.Name);
                var dataPtr = _builder.BuildExtractValue(structVal, 0, "rowdata");
                var rowIdx = VisitExpression(indexExpr.Index);
                var length = _builder.BuildExtractValue(structVal, 1, "rowlen");
                EmitBoundsCheck(rowIdx, length, indexExpr.Location);
                arrayPtr = _builder.BuildGEP2(dynElementType, dataPtr, new[] { rowIdx }, "rowptr");
                arrayType = dynElementType;
                return true;
            }

            // Multidimensional fixed-size arrays (e.g. T[N][M]) are represented as nested
            // LLVM array types ([N x [M x T]]). Indexing one dimension (matrix[i]) must
            // resolve to an addressable pointer to the inner [M x T] row - without loading
            // it - so that a further index (matrix[i][j]) can keep resolving recursively
            // all the way down to the scalar element, however many dimensions deep.
            if (!TryGetFixedArrayInfo(indexExpr.Target, out var outerType, out var outerPtr)) return false;
            if (outerType.ElementType.Kind != LLVMTypeKind.LLVMArrayTypeKind) return false;

            int outerLength = (int)outerType.ArrayLength;
            CheckConstantIndexInBounds(indexExpr.Index, outerLength, indexExpr.Location);

            var idx = VisitExpression(indexExpr.Index);
            if (_builder.InsertBlock.Handle != IntPtr.Zero)
            {
                EmitBoundsCheck(idx, LLVMValueRef.CreateConstInt(GetInt64Type(), (ulong)outerLength), indexExpr.Location);
            }
            var zero = LLVMValueRef.CreateConstInt(GetInt32Type(), 0);
            arrayPtr = BuildBoundsCheckedGEP2(outerType, outerPtr, new[] { zero, idx }, "indexptr");
            arrayType = outerType.ElementType;
            return true;
        }

        return false;
    }

    // Returns the LLVM array type of a fixed-size array expression without generating
    // any code. Used for compile-time queries like len().
    private bool TryGetFixedArrayType(Expression expr, out LLVMTypeRef arrayType)
    {
        arrayType = default;

        if (expr is VariableExpr varExpr)
        {
            if (!_namedValues.TryGetValue(varExpr.Name, out var entry)) return false;
            if (entry.Type.Kind != LLVMTypeKind.LLVMArrayTypeKind) return false;

            arrayType = entry.Type;
            return true;
        }

        if (expr is IndexExpr indexExpr)
        {
            // Dynamic array of fixed-size arrays: rows[i] has fixed-size array type.
            if (indexExpr.Target is VariableExpr targetVar &&
                _namedValues.TryGetValue(targetVar.Name, out var dynEntry) &&
                dynEntry.Type.Kind == LLVMTypeKind.LLVMStructTypeKind &&
                _arrayElementTypes.TryGetValue(dynEntry.Type, out var dynElementType) &&
                dynElementType.Kind == LLVMTypeKind.LLVMArrayTypeKind)
            {
                arrayType = dynElementType;
                return true;
            }

            if (!TryGetFixedArrayType(indexExpr.Target, out var outerType)) return false;
            if (outerType.ElementType.Kind != LLVMTypeKind.LLVMArrayTypeKind) return false;

            arrayType = outerType.ElementType;
            return true;
        }

        return false;
    }

    private LLVMValueRef VisitSetIndex(SetIndexExpr expr)
    {
        // Fixed-size stack arrays are referenced by alloca/global pointer to [N x T].
        if (TryGetFixedArrayInfo(expr.Target, out var fixedArrayType, out var fixedArrayPtr))
        {
            int fixedLength = (int)fixedArrayType.ArrayLength;
            CheckConstantIndexInBounds(expr.Index, fixedLength, expr.Location);

            var fixedIndex = VisitExpression(expr.Index);
            var fixedValue = VisitExpression(expr.Value);
            var fixedElementType = fixedArrayType.ElementType;
            if (_builder.InsertBlock.Handle != IntPtr.Zero)
            {
                EmitBoundsCheck(fixedIndex, LLVMValueRef.CreateConstInt(GetInt64Type(), (ulong)fixedLength), expr.Location);
            }
            var fixedZero = LLVMValueRef.CreateConstInt(GetInt32Type(), 0);
            var fixedGepPtr = BuildBoundsCheckedGEP2(fixedArrayType, fixedArrayPtr, new[] { fixedZero, fixedIndex }, "indexptr");
            fixedValue = ConvertToType(fixedValue, fixedElementType);
            _builder.BuildStore(fixedValue, fixedGepPtr);
            return fixedValue;
        }

        var target = VisitExpression(expr.Target);
        var index = VisitExpression(expr.Index);
        var value = VisitExpression(expr.Value);

        LLVMValueRef dataPtr;
        LLVMTypeRef elementType;

        if (target.TypeOf.Kind == LLVMTypeKind.LLVMStructTypeKind)
        {
            var structType = target.TypeOf;
            if (_builder.InsertBlock.Handle == IntPtr.Zero)
            {
                dataPtr = target.GetAggregateElement(0);
            }
            else
            {
                dataPtr = _builder.BuildExtractValue(target, 0, "dataptr");
                var lengthField = _builder.BuildExtractValue(target, 1, "lenval");
                EmitBoundsCheck(index, lengthField, expr.Location);
            }

            if (!_arrayElementTypes.TryGetValue(structType, out elementType))
            {
                 elementType = structType.GetStructElementTypes()[0].ElementType;
            }
        }
        else if (target.TypeOf.Kind == LLVMTypeKind.LLVMPointerTypeKind)
        {
            RequireUnsafeForRawPointerIndex(expr.Location);
            dataPtr = target;
            elementType = InferPointerElementType(expr.Target)
                          ?? (target.TypeOf.ElementType.Handle != IntPtr.Zero ? target.TypeOf.ElementType : GetInt8Type());
        }
        else
        {
            throw new Exception("Index access on non-array/non-pointer type.");
        }

        if (_builder.InsertBlock.Handle == IntPtr.Zero)
        {
            throw new Exception("Set index not supported at global scope.");
        }

        var ptr = BuildBoundsCheckedGEP2(elementType, dataPtr, new[] { index }, "indexptr");
        value = ConvertToType(value, elementType);
        _builder.BuildStore(value, ptr);
        return value;
    }

    private LLVMValueRef VisitArrayInit(ArrayInitExpr expr)
    {
        if (expr.Elements.Count == 0)
        {
            var dummyType = GetArrayStructType(GetInt8Type());
            return LLVMValueRef.CreateConstNull(dummyType);
        }
    
        var elements = new List<LLVMValueRef>();
        foreach (var el in expr.Elements) elements.Add(VisitExpression(el));
        
        var elementType = elements[0].TypeOf;
        var arrayType = LLVMTypeRef.CreateArray(elementType, (uint)expr.Elements.Count);
        var arrayStructType = GetArrayStructType(elementType);
    
        var insertBlock = _builder.InsertBlock;
        if (insertBlock.Handle == IntPtr.Zero)
        {
            // Global scope: Create a global constant for elements and a global struct
            var globalElements = _module.AddGlobal(arrayType, "global_array_elements");
            globalElements.IsGlobalConstant = true;
            globalElements.Linkage = LLVMLinkage.LLVMInternalLinkage;
            globalElements.Initializer = LLVMValueRef.CreateConstArray(elementType, elements.ToArray());
    
            var dataPtr = globalElements;
            // Decay to pointer to first element
            var decayPtr = LLVMValueRef.CreateConstGEP2(arrayType, dataPtr, new[] { LLVMValueRef.CreateConstInt(GetInt32Type(), 0), LLVMValueRef.CreateConstInt(GetInt32Type(), 0) });
            var length = LLVMValueRef.CreateConstInt(GetInt64Type(), (ulong)expr.Elements.Count);
            
            return LLVMValueRef.CreateConstNamedStruct(arrayStructType, new[] { decayPtr, length });
        }
        
        // Allocate on stack for elements
        var elementsAlloca = BuildEntryAlloca(arrayType, "arrayinit_elements");
        
        for (int i = 0; i < expr.Elements.Count; i++)
        {
            var elementValue = elements[i];
            var index = LLVMValueRef.CreateConstInt(GetInt32Type(), (ulong)i);
            var ptr = _builder.BuildGEP2(arrayType, elementsAlloca, new[] { LLVMValueRef.CreateConstInt(GetInt32Type(), 0), index }, "arrayelptr");
            _builder.BuildStore(elementValue, ptr);
        }
    
        // Return struct { T*, i64 }
        var dataPtrLocal = _builder.BuildGEP2(arrayType, elementsAlloca, new[] { LLVMValueRef.CreateConstInt(GetInt32Type(), 0), LLVMValueRef.CreateConstInt(GetInt32Type(), 0) }, "arrayptr");
        var lengthLocal = LLVMValueRef.CreateConstInt(GetInt64Type(), (ulong)expr.Elements.Count);
        
        // Create struct on stack to return
        var structAlloca = BuildEntryAlloca(arrayStructType, "arraystruct");
        var dataFieldPtr = _builder.BuildStructGEP2(arrayStructType, structAlloca, 0, "datashape");
        var lengthFieldPtr = _builder.BuildStructGEP2(arrayStructType, structAlloca, 1, "lenfield");
        
        _builder.BuildStore(dataPtrLocal, dataFieldPtr);
        _builder.BuildStore(lengthLocal, lengthFieldPtr);
        
        return _builder.BuildLoad2(arrayStructType, structAlloca, "arrayinit_res");
    }

    private LLVMValueRef VisitArrayAlloc(ArrayAllocExpr expr)
    {
        if (_builder.InsertBlock.Handle == IntPtr.Zero)
        {
            throw new CompileException(expr.Location, "Heap array allocation is not allowed at global scope.");
        }

        var elementType = MapTypeNode(expr.ElementType);
        var lengthValue = VisitExpression(expr.Size);
        if (lengthValue.TypeOf.Kind != LLVMTypeKind.LLVMIntegerTypeKind)
        {
            throw new CompileException(expr.Location, "Array allocation size must be an integer.");
        }
        var length64 = lengthValue.TypeOf.IntWidth != 64
            ? _builder.BuildSExt(lengthValue, GetInt64Type(), "len64")
            : lengthValue;

        var elementSizeValue = GetElementSize(elementType);
        var totalBytes = _builder.BuildMul(length64, elementSizeValue, "allocbytes");

        var malloc = GetOrAddFunction("malloc", GetPointerType(GetInt8Type()), new[] { GetInt64Type() });
        var rawPtr = _builder.BuildCall2(_functionTypes["malloc"], malloc, new[] { totalBytes }, "alloctmp");

        // Throw OutOfMemoryException if malloc returns null
        EmitNullCheckOrThrow(rawPtr, "OutOfMemoryException: memory allocation failed");

        var dataPtr = _builder.BuildBitCast(rawPtr, GetPointerType(elementType), "arraydata");

        // Zero-initialize or fill the allocated memory
        LLVMValueRef? fillValue = null;
        if (expr.FillValue != null)
        {
            fillValue = VisitExpression(expr.FillValue);
        }
        BuildArrayFillLoop(elementType, dataPtr, length64, fillValue);

        var arrayStructType = GetArrayStructType(elementType);
        var structAlloca = BuildEntryAlloca(arrayStructType, "arraystruct");
        var dataFieldPtr = _builder.BuildStructGEP2(arrayStructType, structAlloca, 0, "datashape");
        var lengthFieldPtr = _builder.BuildStructGEP2(arrayStructType, structAlloca, 1, "lenfield");

        _builder.BuildStore(dataPtr, dataFieldPtr);
        _builder.BuildStore(length64, lengthFieldPtr);

        return _builder.BuildLoad2(arrayStructType, structAlloca, "arrayalloc_res");
    }

    private LLVMValueRef VisitCast(CastExpr expr)
    {
        var castTargetNewtype = GetDeclaredNewtypeName(expr.TargetType);
        var castSourceNewtype = InferNewtypeName(expr.Expression);
        if (castTargetNewtype != null && castSourceNewtype != null && castTargetNewtype != castSourceNewtype)
        {
            throw new CompileException(expr.Location,
                $"Cannot cast directly between distinct newtypes '{castSourceNewtype}' and '{castTargetNewtype}'; write a conversion function instead.");
        }

        var val = VisitExpression(expr.Expression);
        var targetType = MapTypeNode(expr.TargetType);

        if (val.TypeOf.Handle == targetType.Handle) return val;

        var sourceKind = val.TypeOf.Kind;
        var targetKind = targetType.Kind;

        // Pointers: bitcast to other pointer, test for null to bool, or convert to integer in unsafe.
        if (sourceKind == LLVMTypeKind.LLVMPointerTypeKind)
        {
            if (targetKind == LLVMTypeKind.LLVMPointerTypeKind)
            {
                return _builder.BuildBitCast(val, targetType, "casttmp");
            }

            if (IsBoolType(targetType))
            {
                var nullPtr = LLVMValueRef.CreateConstPointerNull(val.TypeOf);
                return _builder.BuildICmp(LLVMIntPredicate.LLVMIntNE, val, nullPtr, "booltmp");
            }

            if (targetKind == LLVMTypeKind.LLVMIntegerTypeKind)
            {
                if (!InUnsafeContext)
                {
                    throw new CompileException(expr.Location, "Converting a pointer to an integer requires an 'unsafe' block.");
                }
                return _builder.BuildPtrToInt(val, targetType, "ptrtoint");
            }

            if (targetKind == LLVMTypeKind.LLVMStructTypeKind && IsStringStructType(targetType))
            {
                // CSTRING -> STRING: wrap the NUL-terminated pointer with its length via strlen.
                var strlenFunc = GetOrAddFunction("strlen", GetInt64Type(), new[] { GetPointerType(GetInt8Type()) });
                var length = _builder.BuildCall2(_functionTypes["strlen"], strlenFunc, new[] { val }, "cast_cstr_len");
                var strType = GetStringStructType();
                var baseVal = LLVMValueRef.CreateConstNull(strType);
                var withData = _builder.BuildInsertValue(baseVal, val, 0, "cast_cstr_to_str_data");
                return _builder.BuildInsertValue(withData, length, 1, "cast_cstr_to_str");
            }
        }

        // Integers: truncate/extend, convert to float, cast to pointer in unsafe, or test for zero to bool.
        if (sourceKind == LLVMTypeKind.LLVMIntegerTypeKind)
        {
            if (targetKind == LLVMTypeKind.LLVMIntegerTypeKind)
            {
                if (IsBoolType(targetType))
                {
                    var zero = LLVMValueRef.CreateConstInt(val.TypeOf, 0);
                    return _builder.BuildICmp(LLVMIntPredicate.LLVMIntNE, val, zero, "booltmp");
                }

                if (IsBoolType(val.TypeOf))
                    return _builder.BuildZExt(val, targetType, "zexttmp");

                if (val.TypeOf.IntWidth > targetType.IntWidth)
                    return _builder.BuildTrunc(val, targetType, "trunctmp");
                if (val.TypeOf.IntWidth < targetType.IntWidth)
                {
                    // Zero-extend when the source's declared ZV type is unsigned (UINT8..UINT128);
                    // sign-extend otherwise (signed primitives, or when the source type can't be
                    // determined - preserves the previous default behavior).
                    var sourceTypeNode = InferExprTypeNode(expr.Expression);
                    return IsUnsignedPrimitiveTypeNode(sourceTypeNode)
                        ? _builder.BuildZExt(val, targetType, "zexttmp")
                        : _builder.BuildSExt(val, targetType, "sexttmp");
                }
                return val;
            }

            if (targetKind == LLVMTypeKind.LLVMDoubleTypeKind || targetKind == LLVMTypeKind.LLVMFloatTypeKind)
            {
                if (IsBoolType(val.TypeOf))
                {
                    var extended = _builder.BuildZExt(val, GetInt32Type(), "zexttmp");
                    return _builder.BuildSIToFP(extended, targetType, "sitofptmp");
                }

                return _builder.BuildSIToFP(val, targetType, "sitofptmp");
            }

            if (targetKind == LLVMTypeKind.LLVMPointerTypeKind)
            {
                if (!InUnsafeContext)
                {
                    throw new CompileException(expr.Location, "Converting an integer to a pointer requires an 'unsafe' block.");
                }
                return _builder.BuildIntToPtr(val, targetType, "inttoptr");
            }
        }

        // Floats: cast between float widths, convert to signed integer, or test for non-zero to bool.
        if (sourceKind == LLVMTypeKind.LLVMDoubleTypeKind || sourceKind == LLVMTypeKind.LLVMFloatTypeKind)
        {
            if (targetKind == LLVMTypeKind.LLVMDoubleTypeKind || targetKind == LLVMTypeKind.LLVMFloatTypeKind)
            {
                return _builder.BuildFPCast(val, targetType, "fpcasttmp");
            }

            if (targetKind == LLVMTypeKind.LLVMIntegerTypeKind)
            {
                if (IsBoolType(targetType))
                {
                    var zero = LLVMValueRef.CreateConstReal(val.TypeOf, 0.0);
                    return _builder.BuildFCmp(LLVMRealPredicate.LLVMRealONE, val, zero, "booltmp");
                }

                return _builder.BuildFPToSI(val, targetType, "fptositmp");
            }
        }

        // Array struct types: decay to pointer or cast to another array struct.
        if (sourceKind == LLVMTypeKind.LLVMStructTypeKind)
        {
            if (targetKind == LLVMTypeKind.LLVMPointerTypeKind)
            {
                var dataPtr = _builder.BuildExtractValue(val, 0, "array_decay");
                if (dataPtr.TypeOf.Handle != targetType.Handle)
                {
                    return _builder.BuildBitCast(dataPtr, targetType, "casttmp");
                }
                return dataPtr;
            }

            if (targetKind == LLVMTypeKind.LLVMStructTypeKind)
            {
                var data = _builder.BuildExtractValue(val, 0, "data");
                var len = _builder.BuildExtractValue(val, 1, "len");

                var structAlloca = BuildEntryAlloca(targetType, "cast_arraystruct");
                var dataFieldPtr = _builder.BuildStructGEP2(targetType, structAlloca, 0, "data");
                var lengthFieldPtr = _builder.BuildStructGEP2(targetType, structAlloca, 1, "len");

                var expectedPtrType = targetType.GetStructElementTypes()[0];
                var castedData = data;
                if (data.TypeOf.Handle != expectedPtrType.Handle)
                {
                    castedData = _builder.BuildBitCast(data, expectedPtrType, "ptr_cast");
                }

                _builder.BuildStore(castedData, dataFieldPtr);
                _builder.BuildStore(len, lengthFieldPtr);
                return _builder.BuildLoad2(targetType, structAlloca, "cast_res");
            }
        }

        throw new CompileException(
            expr.Location,
            $"Invalid cast: cannot convert from {LlvmTypeName(val.TypeOf)} to {LlvmTypeName(targetType)}.");
    }

    private static bool IsBoolType(LLVMTypeRef type)
    {
        return type.Kind == LLVMTypeKind.LLVMIntegerTypeKind && type.IntWidth == 1;
    }

    private static string LlvmTypeName(LLVMTypeRef type)
    {
        if (type.Kind == LLVMTypeKind.LLVMIntegerTypeKind)
        {
            return type.IntWidth == 1 ? "bool" : $"integer ({type.IntWidth}-bit)";
        }

        return type.Kind switch
        {
            LLVMTypeKind.LLVMDoubleTypeKind => "float64",
            LLVMTypeKind.LLVMFloatTypeKind => "float32",
            LLVMTypeKind.LLVMPointerTypeKind => "pointer",
            LLVMTypeKind.LLVMStructTypeKind => "array/struct",
            LLVMTypeKind.LLVMArrayTypeKind => "fixed-size array",
            LLVMTypeKind.LLVMVoidTypeKind => "void",
            _ => type.Kind.ToString()
        };
    }
    
    private LLVMValueRef VisitTernary(TernaryExpr expr)
    {
        var condition = VisitExpression(expr.Condition);
        var currentFunc = _builder.InsertBlock.Parent;
        var thenBlock = _context.AppendBasicBlock(currentFunc, "tern_then");
        var elseBlock = _context.AppendBasicBlock(currentFunc, "tern_else");
        var mergeBlock = _context.AppendBasicBlock(currentFunc, "tern_merge");

        _builder.BuildCondBr(condition, thenBlock, elseBlock);

        _builder.PositionAtEnd(thenBlock);
        var thenVal = VisitExpression(expr.ThenBranch);
        _builder.BuildBr(mergeBlock);
        var thenEndBlock = _builder.InsertBlock;

        _builder.PositionAtEnd(elseBlock);
        var elseVal = VisitExpression(expr.ElseBranch);
        _builder.BuildBr(mergeBlock);
        var elseEndBlock = _builder.InsertBlock;

        _builder.PositionAtEnd(mergeBlock);
        var phi = _builder.BuildPhi(thenVal.TypeOf, "tern_res");
        phi.AddIncoming(new[] { thenVal, elseVal }, new[] { thenEndBlock, elseEndBlock }, 2);
        return phi;
    }

    private LLVMValueRef VisitGet(GetExpr expr)
    {
        var obj = VisitExpressionForPointer(expr.Object);
        var structPtr = obj.Pointer;
        var structType = obj.Type;
        var structName = obj.StructName;

        if (string.IsNullOrEmpty(structName) || !_structFieldNames.ContainsKey(structName))
        {
            throw new Exception("Cannot access field on non-struct type or unknown struct.");
        }

        int fieldIndex = GetStructFieldIndex(structName, expr.Name.Lexeme);
        if (fieldIndex == -1)
        {
            throw new Exception($"Struct {structName} does not have field {expr.Name.Lexeme}");
        }

        var fieldPtr = _builder.BuildStructGEP2(structType, structPtr, (uint)fieldIndex, "fieldptr");
        var fieldType = _structFieldTypes[structName][fieldIndex];
        return _builder.BuildLoad2(fieldType, fieldPtr, expr.Name.Lexeme);
    }

    private LLVMValueRef VisitStructInit(StructInitExpr expr, string structName)
    {
        if (expr.TypeName != null && expr.TypeName.Lexeme != structName)
        {
            throw new CompileException(expr.TypeName.Location,
                $"Struct literal is annotated as '{expr.TypeName.Lexeme}' but is used where a '{structName}' is expected.");
        }

        if (!_structTypes.TryGetValue(structName, out var structType))
        {
            throw new Exception($"Unknown struct type: {structName}");
        }

        var fieldTypes = _structFieldTypes[structName];

        // Allocate local space for the struct
        var alloca = BuildEntryAlloca(structType, "structinit");

        foreach (var field in expr.Fields)
        {
            int fieldIndex = GetStructFieldIndex(structName, field.Name.Lexeme);
            if (fieldIndex == -1)
            {
                throw new Exception($"Struct {structName} does not have field {field.Name.Lexeme}");
            }

            var fieldPtr = _builder.BuildStructGEP2(structType, alloca, (uint)fieldIndex, "fieldptr");
            var fieldType = fieldTypes[fieldIndex];

            int cstrTempMark = _pendingCstrTemps.Count;
            LLVMValueRef val;
            if (field.Value is StructInitExpr nestedInit)
            {
                var nestedStructName = nestedInit.TypeName?.Lexeme ?? GetStructNameForLlvmType(fieldType);
                if (nestedStructName == null)
                {
                    throw new CompileException(field.Value.Location,
                        $"Cannot infer the struct type of field '{field.Name.Lexeme}'; annotate the literal explicitly, e.g. 'TypeName {{ ... }}'.");
                }
                val = VisitStructInit(nestedInit, nestedStructName);
            }
            else
            {
                val = VisitExpression(field.Value);
            }

            val = ConvertToType(val, fieldType);
            _builder.BuildStore(val, fieldPtr);
            ClaimCstrTempIfOwningField(structName, fieldIndex, cstrTempMark);
        }

        return _builder.BuildLoad2(structType, alloca, "structinit_load");
    }

    private LLVMValueRef VisitStructInit(StructInitExpr expr)
    {
        if (expr.TypeName != null)
        {
            return VisitStructInit(expr, expr.TypeName.Lexeme);
        }
        throw new NotImplementedException(
            "Cannot infer the type of a bare struct literal '{ ... }' in this position. " +
            "Either annotate it explicitly (e.g. 'TypeName { ... }'), or use it directly to initialize/assign a variable or field whose declared type is known.");
    }

    // Resolves the struct name whose mapped LLVM type is `type`, or null if `type` isn't a
    // known user-defined struct (e.g. it's the internal array/STRING fat-pointer struct).
    private string? GetStructNameForLlvmType(LLVMTypeRef type)
    {
        if (type.Kind != LLVMTypeKind.LLVMStructTypeKind) return null;
        foreach (var kvp in _structTypes)
        {
            if (kvp.Value.Handle == type.Handle) return kvp.Key;
        }
        return null;
    }

    private (LLVMValueRef Pointer, LLVMTypeRef Type, string? StructName) VisitExpressionForPointer(Expression expr)
    {
        if (expr is VariableExpr varExpr)
        {
            if (!_namedValues.TryGetValue(varExpr.Name, out var entry))
            {
                throw new Exception($"Unknown variable name: {varExpr.Name}");
            }
            CheckVariableAlive(varExpr.Name, varExpr.Location);
            return entry;
        }

        if (expr is GetExpr getExpr)
        {
            var obj = VisitExpressionForPointer(getExpr.Object);
            var structPtr = obj.Pointer;
            var structType = obj.Type;
            var structName = obj.StructName;
            
            if (string.IsNullOrEmpty(structName) || !_structFieldNames.ContainsKey(structName))
            {
                throw new Exception("Cannot access field on non-struct type or unknown struct.");
            }

            int fieldIndex = GetStructFieldIndex(structName, getExpr.Name.Lexeme);
            if (fieldIndex == -1)
            {
                throw new Exception($"Struct {structName} does not have field {getExpr.Name.Lexeme}");
            }

            var fieldPtr = _builder.BuildStructGEP2(structType, structPtr, (uint)fieldIndex, "fieldptr");
            var fieldType = _structFieldTypes[structName][fieldIndex];
            
            string? fieldStructName = null;
            // If the field itself is a struct, we need to find its name
            if (fieldType.Kind == LLVMTypeKind.LLVMStructTypeKind)
            {
                foreach (var kvp in _structTypes)
                {
                    if (kvp.Value.Handle == fieldType.Handle)
                    {
                        fieldStructName = kvp.Key;
                        break;
                    }
                }
            }

            return (fieldPtr, fieldType, fieldStructName);
        }

        if (expr is IndexExpr indexExpr)
        {
            // Fixed-size stack arrays
            if (TryGetFixedArrayInfo(indexExpr.Target, out var fixedArrayType, out var fixedArrayPtr))
            {
                int fixedLength = (int)fixedArrayType.ArrayLength;
                CheckConstantIndexInBounds(indexExpr.Index, fixedLength, indexExpr.Location);

                var fixedIndex = VisitExpression(indexExpr.Index);
                var fixedElementType = fixedArrayType.ElementType;
                if (_builder.InsertBlock.Handle != IntPtr.Zero)
                {
                    EmitBoundsCheck(fixedIndex, LLVMValueRef.CreateConstInt(GetInt64Type(), (ulong)fixedLength), indexExpr.Location);
                }
                var fixedZero = LLVMValueRef.CreateConstInt(GetInt32Type(), 0);
                var fixedPtr = BuildBoundsCheckedGEP2(fixedArrayType, fixedArrayPtr, new[] { fixedZero, fixedIndex }, "indexptr");

                string? fixedStructName = null;
                if (fixedElementType.Kind == LLVMTypeKind.LLVMStructTypeKind)
                {
                    foreach (var kvp in _structTypes)
                    {
                        if (kvp.Value.Handle == fixedElementType.Handle)
                        {
                            fixedStructName = kvp.Key;
                            break;
                        }
                    }
                }

                return (fixedPtr, fixedElementType, fixedStructName);
            }

            var target = VisitExpression(indexExpr.Target);
            var index = VisitExpression(indexExpr.Index);
            
            if (target.TypeOf.Kind != LLVMTypeKind.LLVMPointerTypeKind)
            {
                throw new Exception("Index access on non-pointer type.");
            }

            RequireUnsafeForRawPointerIndex(indexExpr.Location);

            var elementType = target.TypeOf.ElementType;
            var ptr = _builder.BuildGEP2(elementType, target, new[] { index }, "indexptr");
            
            string? structName = null;
            if (elementType.Kind == LLVMTypeKind.LLVMStructTypeKind)
            {
                foreach (var kvp in _structTypes)
                {
                    if (kvp.Value.Handle == elementType.Handle)
                    {
                        structName = kvp.Key;
                        break;
                    }
                }
            }

            return (ptr, elementType, structName);
        }

        throw new NotImplementedException($"Pointer access for {expr.GetType().Name} not implemented.");
    }

    private LLVMValueRef VisitVariable(VariableExpr expr)
    {
        if (!_namedValues.TryGetValue(expr.Name, out var entry))
        {
            // Not a local/global variable - if it names a top-level function instead, treat
            // a bare reference to it (not immediately called) as taking its address. LLVM
            // function values are already pointer-typed, so this "just works" as a function
            // pointer: it can be stored in a FUNCPTR<...>-typed variable (bitcast to the
            // exact signature) or passed straight through to a PTR<VOID> callback parameter,
            // the same way thread_spawn() does internally but without needing a string name.
            if (_functionValues.TryGetValue(expr.Name, out var function))
            {
                return function;
            }

            throw new Exception($"Unknown variable name: {expr.Name}");
        }

        CheckVariableAlive(expr.Name, expr.Location);

        // Fixed-size arrays are stack/global allocations of [N x T]; pass the pointer directly.
        if (entry.Type.Kind == LLVMTypeKind.LLVMArrayTypeKind)
        {
            return entry.Value;
        }

        var insertBlock = _builder.InsertBlock;
        if (insertBlock.Handle == IntPtr.Zero || entry.Value.IsAGlobalVariable.Handle != IntPtr.Zero)
        {
            // If we're at global scope or it's a global variable, return the value directly or load it.
            if (entry.Value.IsAGlobalVariable.Handle != IntPtr.Zero)
            {
                if (insertBlock.Handle == IntPtr.Zero)
                {
                    // Global scope, likely an initializer. Return the global pointer/constant?
                    // Actually, for global initializers, we usually need the constant value.
                    // But if it's being used as an rvalue in another global initializer, it must be constant.
                    return entry.Value.Initializer;
                }
                return _builder.BuildLoad2(entry.Type, entry.Value, expr.Name);
            }
            return entry.Value;
        }

        // We stored the alloca/pointer in entry.Value and the allocated type in entry.Type
        return _builder.BuildLoad2(entry.Type, entry.Value, expr.Name);
    }

    private LLVMValueRef VisitCall(CallExpr expr)
    {
        if (expr.Callee is not VariableExpr varExpr)
            throw new NotImplementedException("Complex callees not supported yet.");

        string calleeName = varExpr.Name;

        // A call to a declared exception type name, e.g. `MyError("description")`, builds
        // a tagged Exception value the same way the generic `Exception("...")` builtin does.
        // See VisitExceptionTypeDecl / GenerateExceptionConstructor / _declaredExceptionTypes.
        if (calleeName != "Exception" && _declaredExceptionTypes.Contains(calleeName))
        {
            return GenerateExceptionConstructor(expr.Arguments, calleeName);
        }

        // Check for built-ins
        var builtins = new HashSet<string>
        {
            "print", "copy", "move", "fopen", "fclose", "fread", "fwrite", "fseek", "ftell", "feof", "ferror", "fgets", "fputs", "tmpfile", "memcpy", "memset", "remove", "rename", "mkdir", "rmdir", "len", "alloc", "realloc", "cstr", "wstr", "array_copy", "get_timestamp", "get_timestamp_ms",
            "strlen", "strcmp", "strncmp", "strcpy", "strncpy", "strcat", "strncat", "strchr", "strstr",
            "strdup", "str_concat", "str_equals", "to_upper", "to_lower",
            "Exception", "respawn", "exit",
            "thread_spawn", "thread_join", "thread_sleep_ms",
            "mutex_create", "mutex_lock", "mutex_unlock", "mutex_destroy"
        };
        // An explicit user/extern declaration of the same name (e.g. a raw `extern "" { fopen(...); }`
        // binding in lib/lin) always shadows a builtin: _functionValues is only ever populated by
        // FunctionDeclStmt/ExternFunctionDecl, never by the builtin codegen helpers, so this check
        // has no effect unless the user actually declared that name themselves.
        if (!_functionValues.ContainsKey(calleeName) &&
            (builtins.Contains(calleeName) || ParseBuiltinNames.Contains(calleeName) || CursesBuiltinNames.Contains(calleeName) || ThreadBuiltinNames.Contains(calleeName) || AtomicBuiltinNames.Contains(calleeName)))
        {
            return GenerateBuiltinCall(calleeName, expr.Arguments);
        }

        if (!_functionValues.TryGetValue(calleeName, out var function))
        {
            // Not a top-level function - if it's a local/global variable declared as
            // FUNCPTR<...>, this is a call *through* a function pointer value rather than a
            // direct call to a named function.
            if (_namedValues.ContainsKey(calleeName) &&
                _variableDeclaredTypeNodes.TryGetValue(calleeName, out var declaredType) &&
                declaredType is FunctionPointerTypeNode funcPtrType)
            {
                return VisitIndirectCall(varExpr, funcPtrType, expr.Arguments);
            }

            throw new Exception($"Unknown function referenced: {calleeName}");
        }

        // For BuildCall2, we need the function type, not the function pointer type.
        var functionType = _functionTypes[calleeName];
        var paramTypes = functionType.GetParamTypes();
        uint paramCount = (uint)paramTypes.Length;

        var paramNewtypes = _functionParamNewtypes.TryGetValue(calleeName, out var pnt) ? pnt : null;

        var args = new List<LLVMValueRef>();
        for (int i = 0; i < expr.Arguments.Count; i++)
        {
            if (paramNewtypes != null && i < paramNewtypes.Count)
            {
                CheckNewtypeAssignable(paramNewtypes[i], expr.Arguments[i], expr.Location);
            }

            LLVMValueRef val;
            // Fixed-size array arguments (a local variable or a row slice like matrix[i])
            // are passed by reference; use the underlying allocation pointer directly instead
            // of loading the array value.
            if (i < paramCount && paramTypes[i].Kind == LLVMTypeKind.LLVMPointerTypeKind &&
                TryGetFixedArrayInfo(expr.Arguments[i], out _, out var fixedArrayPtr))
            {
                val = fixedArrayPtr;
            }
            else if (i < paramCount && paramTypes[i].Kind == LLVMTypeKind.LLVMStructTypeKind &&
                     TryGetFixedArrayInfo(expr.Arguments[i], out _, out _))
            {
                // A fixed-size array (T[N]) can't be passed where a dynamic array (T[]) is
                // expected - they have incompatible runtime representations (a raw stack
                // allocation vs. a { ptr, i64 } fat pointer). Report a clear compile error
                // instead of emitting invalid IR.
                throw new CompileException(expr.Arguments[i].Location,
                    "Cannot pass a fixed-size array where a dynamic array (T[]) is expected. " +
                    "Use a dynamic array, or convert explicitly.");
            }
            else
            {
                val = VisitExpression(expr.Arguments[i]);
            }

            // Auto-decay array struct to pointer if the function expects a pointer
            if (i < paramCount && val.TypeOf.Kind == LLVMTypeKind.LLVMStructTypeKind && paramTypes[i].Kind == LLVMTypeKind.LLVMPointerTypeKind)
            {
                // Assume it's an array struct and extract the pointer
                val = _builder.BuildExtractValue(val, 0, "array_decay");
            }

            if (i < paramCount)
            {
                var paramType = paramTypes[i];

                if (val.TypeOf.Kind == LLVMTypeKind.LLVMPointerTypeKind && paramType.Kind == LLVMTypeKind.LLVMPointerTypeKind)
                {
                    if (val.TypeOf.Handle != paramType.Handle)
                    {
                        val = _builder.BuildBitCast(val, paramType, "ptr_cast");
                    }
                }
                else if (val.TypeOf.Kind == LLVMTypeKind.LLVMIntegerTypeKind && paramType.Kind == LLVMTypeKind.LLVMIntegerTypeKind)
                {
                    if (val.TypeOf.IntWidth != paramType.IntWidth)
                    {
                        if (val.TypeOf.IntWidth > paramType.IntWidth)
                            val = _builder.BuildTrunc(val, paramType, "argtrunc");
                        else
                            val = _builder.BuildSExt(val, paramType, "argsext");
                    }
                }
            }

            args.Add(val);
        }

        // Void calls cannot be given a result name in LLVM.
        if (functionType.ReturnType.Kind == LLVMTypeKind.LLVMVoidTypeKind)
        {
            _builder.BuildCall2(functionType, function, args.ToArray(), "");
            return default;
        }

        return _builder.BuildCall2(functionType, function, args.ToArray(), "calltmp");
    }

    // Calls through a FUNCPTR<...>-typed variable, e.g. `callback(1, 2)` where `callback`
    // was declared `FUNCPTR<INT32(INT32, INT32)>`. This is a real indirect call (LLVM loads
    // the stored function pointer and calls through it), checked against the declared
    // signature the same way a direct call is checked against a function's parameter list.
    private LLVMValueRef VisitIndirectCall(VariableExpr callee, FunctionPointerTypeNode funcPtrType, List<Expression> arguments)
    {
        var functionType = GetFunctionPointerFunctionType(funcPtrType);
        var paramTypes = functionType.GetParamTypes();

        if (arguments.Count != paramTypes.Length)
        {
            throw new CompileException(callee.Location,
                $"Function pointer '{callee.Name}' expects {paramTypes.Length} argument(s) but {arguments.Count} were provided.");
        }

        var funcPtrValue = VisitVariable(callee);

        var args = new List<LLVMValueRef>();
        for (int i = 0; i < arguments.Count; i++)
        {
            var val = VisitExpression(arguments[i]);
            args.Add(ConvertToType(val, paramTypes[i]));
        }

        if (functionType.ReturnType.Kind == LLVMTypeKind.LLVMVoidTypeKind)
        {
            _builder.BuildCall2(functionType, funcPtrValue, args.ToArray(), "");
            return default;
        }

        return _builder.BuildCall2(functionType, funcPtrValue, args.ToArray(), "indirect_calltmp");
    }
}
