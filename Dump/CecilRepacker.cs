// CecilRepacker.cs  (Mono.Cecil 0.9.6 / .NET 3.5 兼容)
// 作用：以磁盘模板 DLL 为蓝本，用运行期反射到的 IL 重建全部托管方法体。
// 重点修复：
//  1) 分支/开关回填使用尾部 NOP 哨兵，避免 null 目标与越界
//  2) EH 仅删除非法项，不把边界拉到方法头/尾
//  3) InlineTok/InlineSig 严格解析+Import（含 TypeSpec/MethodSpec/FieldSpec）
//  4) 写盘前全模块体检 + Sanitize（失败即 stub）
//  5) 对未解析/重建失败的方法，尝试从运行期 IL 重发射；仍不行落回最小 Ret 桩

using System;
using System.IO;
using System.Collections.Generic;
using System.Reflection;
using Mono.Cecil;
using Mono.Cecil.Cil;
using MethodAttributes = Mono.Cecil.MethodAttributes;
using MethodBody = Mono.Cecil.Cil.MethodBody;
using MethodImplAttributes = Mono.Cecil.MethodImplAttributes;
using ASWDEBUG.Logger;

public static class CecilRepacker
{
    public static void RepackFromLiveIL(Assembly liveAsm, string templateDllPath, string outputDllPath)
    {
        if (liveAsm == null) throw new ArgumentNullException("liveAsm");
        if (string.IsNullOrEmpty(templateDllPath)) throw new ArgumentNullException("templateDllPath");
        if (string.IsNullOrEmpty(outputDllPath)) throw new ArgumentNullException("outputDllPath");

        FileLogger.Log("MARK", "Repack ENTER");
        var rp = new ReaderParameters { ReadingMode = ReadingMode.Immediate };
        var wp = new WriterParameters { WriteSymbols = false };

        ModuleDefinition module = ModuleDefinition.ReadModule(templateDllPath, rp);
        FileLogger.Log("MARK", "Module loaded");

        var defByToken = new Dictionary<int, MethodDefinition>(4096);
        foreach (var td in module.Types) IndexType(td, defByToken);
        FileLogger.Log("MARK", "IndexType done, methods=" + defByToken.Count);

        System.Reflection.Module liveMod = liveAsm.ManifestModule;
        int done = 0, skipped = 0, failed = 0;

        foreach (var t in SafeGetTypes(liveAsm))
        {
            var methods = SafeGetDeclaredMethods(t);
            for (int i = 0; i < methods.Length; i++)
            {
                var mi = methods[i];

                System.Reflection.MethodBody rb = null;
                try { rb = mi.GetMethodBody(); } catch { rb = null; }
                if (rb == null) { skipped++; continue; }

                byte[] ilBytes = null;
                try { ilBytes = rb.GetILAsByteArray(); } catch { ilBytes = null; }
                if (ilBytes == null || ilBytes.Length == 0) { skipped++; continue; }

                MethodDefinition md;
                if (!defByToken.TryGetValue(mi.MetadataToken, out md)) { skipped++; continue; }

                var impl = md.ImplAttributes; var attr = md.Attributes;
                if (!md.HasBody ||
                    (impl & MethodImplAttributes.InternalCall) != 0 ||
                    (attr & MethodAttributes.PInvokeImpl) != 0 ||
                    (attr & MethodAttributes.Abstract) != 0 ||
                    (impl & MethodImplAttributes.Native) != 0 ||
                    (impl & MethodImplAttributes.Runtime) != 0)
                { skipped++; continue; }

                try
                {
                    FileLogger.Log("WORK", "-> " + md.FullName + " tok=0x" + md.MetadataToken.ToInt32().ToString("X8"));

                    ReplaceBodyWithLiveIL(module, md, liveMod, mi, rb, ilBytes);

                    // 轻量修补（不改 EH 边界）
                    NormalizeBranchesNoEH(md);
                    DropInvalidExceptionHandlers(md);
                    //LogCrossProtectedBranchIfAny(md);

                    done++;
                    if ((done % 200) == 0) FileLogger.Log("MARK", "processed " + done);
                }
                catch (Exception ex)
                {
                    failed++;
                    FileLogger.Log("WARN", "FAIL " + md.FullName + " tok=0x" +
                        md.MetadataToken.ToInt32().ToString("X8") + " : " + ex.Message + " -> stub");
                    try { EmitRetStub(md); } catch { }
                }
            }
        }

        FileLogger.Log("MARK", "Repack DONE: ok=" + done + " skip=" + skipped + " fail=" + failed);

        // 对还没成功解析/重建的 body，再尝试一次用运行期 IL 重发射
        TryReemitAllUnparsedBodiesFromLive(module, liveAsm);

        // 触发 Cecil 解析所有方法体，并记录解析失败（不会抛出）
        ForceResolveAllBodies(module);

        // 模块级兜底体检：只修分支、清理无效 EH + Sanitize 或 Stub
        PreflightValidateModule(module);

        // 清洗方法实现标记：去掉混淆残留（例如 0x8000），统一可执行托管方法为 IL+managed
        int implFixed = SanitizeAllMethodImplFlags(module);
        FileLogger.Log("MARK", "Impl sanitize fixed=" + implFixed);

        Directory.CreateDirectory(Path.GetDirectoryName(outputDllPath));
        module.Write(outputDllPath, wp);
        FileLogger.Log("INFO", "WRITE DONE -> " + outputDllPath);
    }

    static void IndexType(TypeDefinition td, Dictionary<int, MethodDefinition> map)
    {
        for (int i = 0; i < td.Methods.Count; i++)
        {
            MethodDefinition md = td.Methods[i];
            map[md.MetadataToken.ToInt32()] = md;
        }
        for (int i = 0; i < td.NestedTypes.Count; i++)
            IndexType(td.NestedTypes[i], map);
    }

    static int SanitizeAllMethodImplFlags(ModuleDefinition module)
    {
        if (module == null) return 0;
        int fixedCount = 0;
        for (int i = 0; i < module.Types.Count; i++)
            fixedCount += SanitizeTypeMethodImplFlags(module.Types[i]);
        return fixedCount;
    }

    static int SanitizeTypeMethodImplFlags(TypeDefinition td)
    {
        if (td == null) return 0;
        int fixedCount = 0;

        for (int i = 0; i < td.Methods.Count; i++)
        {
            MethodDefinition md = td.Methods[i];
            if (NormalizeManagedILMethodImplFlags(md)) fixedCount++;
        }

        for (int i = 0; i < td.NestedTypes.Count; i++)
            fixedCount += SanitizeTypeMethodImplFlags(td.NestedTypes[i]);

        return fixedCount;
    }

    static bool NormalizeManagedILMethodImplFlags(MethodDefinition md)
    {
        if (md == null || !md.HasBody) return false;

        bool changed = false;

        // 对“有方法体”的托管方法，只保留不会破坏语义的通用 flag。
        // 其余（含 0x8000 等混淆位）全部清掉，强制回到 IL + managed。
        const int keepOptionalImpl =
            0x0008 | // NoInlining
            0x0020 | // Synchronized
            0x0040 | // NoOptimization
            0x0080 | // PreserveSig
            0x0100 | // AggressiveInlining
            0x0200 | // AggressiveOptimization
            0x0400;  // SecurityMitigations

        int rawImpl = (int)md.ImplAttributes;
        int cleanImpl = rawImpl & keepOptionalImpl; // IL(0) + managed(0) + optional flags
        if (cleanImpl != rawImpl)
        {
            md.ImplAttributes = (MethodImplAttributes)cleanImpl;
            changed = true;
        }

        MethodAttributes rawAttr = md.Attributes;
        MethodAttributes cleanAttr = rawAttr;
        if ((cleanAttr & MethodAttributes.PInvokeImpl) != 0) cleanAttr &= ~MethodAttributes.PInvokeImpl;
        if ((cleanAttr & MethodAttributes.Abstract) != 0) cleanAttr &= ~MethodAttributes.Abstract;
        if (cleanAttr != rawAttr)
        {
            md.Attributes = cleanAttr;
            changed = true;
        }

        if (StripBogusMethodImplCustomAttributes(md)) changed = true;
        return changed;
    }

    static bool StripBogusMethodImplCustomAttributes(MethodDefinition md)
    {
        if (md == null || !md.HasCustomAttributes) return false;
        bool changed = false;

        const int knownMethodImplOptions =
            0x0004 | // Unmanaged
            0x0008 | // NoInlining
            0x0010 | // ForwardRef
            0x0020 | // Synchronized
            0x0040 | // NoOptimization
            0x0080 | // PreserveSig
            0x0100 | // AggressiveInlining
            0x0200 | // AggressiveOptimization
            0x0400;  // SecurityMitigations

        for (int i = md.CustomAttributes.Count - 1; i >= 0; i--)
        {
            CustomAttribute ca = md.CustomAttributes[i];
            if (ca == null || ca.AttributeType == null) continue;
            if (ca.AttributeType.FullName != "System.Runtime.CompilerServices.MethodImplAttribute") continue;

            bool hasValue = false;
            int v = 0;
            if (ca.ConstructorArguments != null && ca.ConstructorArguments.Count > 0)
            {
                object arg = ca.ConstructorArguments[0].Value;
                if (arg is int) { v = (int)arg; hasValue = true; }
                else if (arg is short) { v = (short)arg; hasValue = true; }
                else if (arg is byte) { v = (byte)arg; hasValue = true; }
            }

            bool remove = false;
            if (!hasValue) remove = true;
            else if ((v & ~knownMethodImplOptions) != 0) remove = true; // 包含未知位（如 0x8000）

            if (remove)
            {
                md.CustomAttributes.RemoveAt(i);
                changed = true;
            }
        }

        return changed;
    }

    static IEnumerable<Type> SafeGetTypes(Assembly a)
    {
        try { return a.GetTypes(); }
        catch (ReflectionTypeLoadException ex)
        {
            List<Type> list = new List<Type>();
            if (ex.Types != null)
                for (int i = 0; i < ex.Types.Length; i++)
                    if (ex.Types[i] != null) list.Add(ex.Types[i]);
            return list;
        }
    }

    static MethodInfo[] SafeGetDeclaredMethods(Type t)
    {
        try
        {
            return t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly);
        }
        catch { return new MethodInfo[0]; }
    }

    static Instruction FindByOffset(Dictionary<int, Instruction> map, int ofs)
    {
        Instruction i;
        if (!map.TryGetValue(ofs, out i))
            throw new InvalidOperationException("Cannot map IL offset " + ofs);
        return i;
    }

    static Instruction FindByOffsetOrNext(Dictionary<int, Instruction> map, int ofs)
    {
        Instruction i;
        if (map.TryGetValue(ofs, out i)) return i;
        int minDelta = int.MaxValue; Instruction best = null;
        foreach (KeyValuePair<int, Instruction> kv in map)
        {
            int d = kv.Key - ofs;
            if (d >= 0 && d < minDelta) { minDelta = d; best = kv.Value; }
        }
        if (best != null) return best;
        int max = -1; Instruction last = null;
        foreach (KeyValuePair<int, Instruction> kv in map)
            if (kv.Key > max) { max = kv.Key; last = kv.Value; }
        if (last != null) return last;
        throw new InvalidOperationException("Cannot map IL offset " + ofs);
    }

    static bool IsArgOp(OpCode op)
    {
        switch (op.Code)
        {
            case Code.Ldarg:
            case Code.Ldarg_S:
            case Code.Ldarga:
            case Code.Ldarga_S:
            case Code.Starg:
            case Code.Starg_S:
                return true;
            default:
                return false;
        }
    }

    // —— 核心：把运行期 IL 解析并重建到 Cecil 的 MethodBody —— //
    static void ReplaceBodyWithLiveIL(ModuleDefinition targetModule, MethodDefinition md,
                                      System.Reflection.Module liveMod, MethodBase liveMethod,
                                      System.Reflection.MethodBody liveBody, byte[] ilBytes)
    {
        md.Body = new Mono.Cecil.Cil.MethodBody(md);
        MethodBody body = md.Body;

        body.InitLocals = liveBody.InitLocals;
        body.MaxStackSize = liveBody.MaxStackSize;

        body.Variables.Clear();
        foreach (LocalVariableInfo lv in liveBody.LocalVariables)
            body.Variables.Add(new VariableDefinition(ImportTypeCtx(targetModule, lv.LocalType, md)));

        ILProcessor il = body.GetILProcessor();
        Instruction tailNop = Instruction.Create(OpCodes.Nop);
        ILReader reader = new ILReader(ilBytes);
        Dictionary<int, Instruction> offset2Inst = new Dictionary<int, Instruction>(ilBytes != null ? ilBytes.Length / 2 : 16);
        List<Action> pendingFix = new List<Action>(64);

        while (reader.HasNext)
        {
            int ofs = reader.Offset;
            OpCode op; object operand;
            reader.Read(out op, out operand);
            Instruction inst;

            switch (op.OperandType)
            {
                case OperandType.InlineNone:
                    inst = Instruction.Create(op);
                    break;

                case OperandType.ShortInlineI: inst = Instruction.Create(op, (sbyte)operand); break;
                case OperandType.InlineI: inst = Instruction.Create(op, (int)operand); break;
                case OperandType.InlineI8: inst = Instruction.Create(op, (long)operand); break;
                case OperandType.ShortInlineR: inst = Instruction.Create(op, (float)operand); break;
                case OperandType.InlineR: inst = Instruction.Create(op, (double)operand); break;

                case OperandType.ShortInlineArg:
                case OperandType.InlineArg:
                    {
                        int idx;
                        if (operand is int) idx = (int)operand;
                        else if (operand is byte) idx = (byte)operand;
                        else if (operand is ushort) idx = (ushort)operand;
                        else idx = Convert.ToInt32(operand);

                        if (md.HasThis && idx == 0)
                        {
                            if (op.Code == Code.Ldarg || op.Code == Code.Ldarg_S)
                            { inst = Instruction.Create(OpCodes.Ldarg_0); break; }

                            if (op.Code == Code.Ldarga || op.Code == Code.Ldarga_S)
                            {
                                ParameterDefinition thisParam = body.ThisParameter;
                                if (thisParam == null)
                                    throw new NotSupportedException("ldarga on 'this' but Body.ThisParameter is null");
                                inst = Instruction.Create(OpCodes.Ldarga, thisParam);
                                break;
                            }

                            if (op.Code == Code.Starg || op.Code == Code.Starg_S)
                                throw new NotSupportedException("starg on 'this' is invalid IL");

                            inst = Instruction.Create(OpCodes.Ldarg_0);
                            break;
                        }

                        int pIndex = md.HasThis ? (idx - 1) : idx;
                        if (pIndex < 0 || pIndex >= md.Parameters.Count)
                            throw new IndexOutOfRangeException("arg index out of range: " + idx + " / " + md.Parameters.Count + " (HasThis=" + md.HasThis + ")");

                        ParameterDefinition pd = md.Parameters[pIndex];

                        if (op.Code == Code.Ldarg_S) op = OpCodes.Ldarg;
                        else if (op.Code == Code.Starg_S) op = OpCodes.Starg;
                        else if (op.Code == Code.Ldarga_S) op = OpCodes.Ldarga;

                        inst = Instruction.Create(op, pd);
                        break;
                    }

                case OperandType.ShortInlineVar:
                case OperandType.InlineVar:
                    {
                        int idx;
                        if (operand is int) idx = (int)operand;
                        else if (operand is byte) idx = (byte)operand;
                        else if (operand is ushort) idx = (ushort)operand;
                        else idx = Convert.ToInt32(operand);

                        if (IsArgOp(op))
                        {
                            ParameterDefinition pd2;
                            if (md.HasThis)
                            {
                                if (idx == 0) pd2 = body.ThisParameter;
                                else pd2 = md.Parameters[idx - 1];
                            }
                            else
                            {
                                pd2 = md.Parameters[idx];
                            }
                            inst = Instruction.Create(op, pd2);
                        }
                        else
                        {
                            if (idx < 0 || idx >= body.Variables.Count)
                                throw new IndexOutOfRangeException("ldloc/stloc index out of range: " + idx + " / " + body.Variables.Count);
                            inst = Instruction.Create(op, body.Variables[idx]);
                        }
                        break;
                    }

                case OperandType.InlineString:
                    {
                        string s = liveMod.ResolveString((int)operand);
                        inst = Instruction.Create(op, s);
                        break;
                    }

                case OperandType.InlineType:
                    {
                        Type rt = ResolveTypeCtx(liveMod, (int)operand, liveMethod);
                        inst = Instruction.Create(op, ImportTypeCtx(targetModule, rt, md));
                        break;
                    }

                case OperandType.InlineField:
                    {
                        FieldInfo rf = ResolveFieldCtx(liveMod, (int)operand, liveMethod);
                        inst = Instruction.Create(op, ImportFieldCtx(targetModule, rf, md));
                        break;
                    }

                case OperandType.InlineMethod:
                    {
                        MethodBase rm = ResolveMethodCtx(liveMod, (int)operand, liveMethod);
                        inst = Instruction.Create(op, ImportMethodCtx(targetModule, rm, md));
                        break;
                    }

                case OperandType.InlineTok:
                    {
                        int tok = (int)operand;

                        Type tRes = ResolveTypeCtx(liveMod, tok, liveMethod);
                        if (tRes != null) { inst = Instruction.Create(op, ImportTypeCtx(targetModule, tRes, md)); break; }

                        FieldInfo fRes = ResolveFieldCtx(liveMod, tok, liveMethod);
                        if (fRes != null) { inst = Instruction.Create(op, ImportFieldCtx(targetModule, fRes, md)); break; }

                        MethodBase mRes = ResolveMethodCtx(liveMod, tok, liveMethod);
                        if (mRes != null) { inst = Instruction.Create(op, ImportMethodCtx(targetModule, mRes, md)); break; }

                        try
                        {
                            Type t2 = liveMod.ResolveType(tok);
                            TypeReference tr2 = ImportTypeCtx(targetModule, t2, md);
                            inst = Instruction.Create(op, tr2);
                            break;
                        }
                        catch { }

                        throw new NotSupportedException("InlineTok unresolved token=0x" + tok.ToString("X8"));
                    }

                case OperandType.ShortInlineBrTarget:
                    {
                        int targetOfs = reader.CurrentOffset + (sbyte)operand;
                        inst = Instruction.Create(op, tailNop);
                        pendingFix.Add(delegate ()
                        {
                            Instruction t;
                            if (!offset2Inst.TryGetValue(targetOfs, out t)) t = tailNop;
                            inst.Operand = t;
                        });
                        break;
                    }
                case OperandType.InlineBrTarget:
                    {
                        int targetOfs = reader.CurrentOffset + (int)operand;
                        inst = Instruction.Create(op, tailNop);
                        pendingFix.Add(delegate ()
                        {
                            Instruction t;
                            if (!offset2Inst.TryGetValue(targetOfs, out t)) t = tailNop;
                            inst.Operand = t;
                        });
                        break;
                    }

                case OperandType.InlineSwitch:
                    {
                        int[] deltas = (int[])operand;
                        Instruction[] placeholders = new Instruction[deltas.Length];
                        for (int i = 0; i < deltas.Length; i++) placeholders[i] = tailNop;
                        inst = Instruction.Create(op, placeholders);
                        int baseOfs = reader.CurrentOffset;
                        pendingFix.Add(delegate ()
                        {
                            Instruction[] targets = new Instruction[deltas.Length];
                            for (int i = 0; i < deltas.Length; i++)
                            {
                                Instruction t;
                                if (!offset2Inst.TryGetValue(baseOfs + deltas[i], out t)) t = tailNop;
                                targets[i] = t;
                            }
                            inst.Operand = targets;
                        });
                        break;
                    }

                case OperandType.InlineSig:
                    {
                        int sigTok = (int)operand;
                        CallSite cs = null;
                        try { cs = SigUtil.ParseCallSite(targetModule, liveMod, sigTok); }
                        catch { cs = null; }

                        if (cs == null)
                            throw new NotSupportedException("InlineSig parse failed sigTok=0x" + sigTok.ToString("X8"));

                        inst = Instruction.Create(op, cs);
                        break;
                    }

                default:
                    FileLogger.Log("ERROR", "Unsupported operand " + op.OperandType + " op=" + op.Code + " ofs=" + ofs);
                    throw new NotSupportedException("Unsupported operand " + op.OperandType);
            }

            offset2Inst[ofs] = inst;
            il.Append(inst);
        }

        // 末尾哨兵
        il.Append(tailNop);
        if (ilBytes != null) offset2Inst[ilBytes.Length] = tailNop;

        // 分支回填
        for (int i = 0; i < pendingFix.Count; i++) pendingFix[i]();

        // 异常表
        IList<ExceptionHandlingClause> ehs = liveBody.ExceptionHandlingClauses;
        for (int i = 0; i < ehs.Count; i++)
        {
            ExceptionHandlingClause eh = ehs[i];
            ExceptionHandler ch = new ExceptionHandler((ExceptionHandlerType)eh.Flags);
            ch.TryStart = FindByOffsetOrNext(offset2Inst, eh.TryOffset);
            ch.TryEnd = FindByOffsetOrNext(offset2Inst, eh.TryOffset + eh.TryLength);
            ch.HandlerStart = FindByOffsetOrNext(offset2Inst, eh.HandlerOffset);
            ch.HandlerEnd = FindByOffsetOrNext(offset2Inst, eh.HandlerOffset + eh.HandlerLength);
            if (eh.Flags == ExceptionHandlingClauseOptions.Filter)
                ch.FilterStart = FindByOffsetOrNext(offset2Inst, eh.FilterOffset);
            if (eh.Flags == ExceptionHandlingClauseOptions.Clause && eh.CatchType != null)
                ch.CatchType = targetModule.Import(eh.CatchType);
            body.ExceptionHandlers.Add(ch);
        }

        // 统一短格式
        NormalizeShortVarAndArgForms(md);

        // 关键：严格 EH 检测；一旦存在跨保护区分支/开关，抛出异常让上层打桩
        if (!LegalizeProtectedRegionBranches(md))
            throw new InvalidOperationException("Cross-EH branch detected after rebuild: " + md.FullName);

        // 轻量收尾（不改 EH 边界）
        NormalizeBranchesNoEH(md);
        DropInvalidExceptionHandlers(md);
        // 可选诊断
        LogCrossProtectedBranchIfAny(md);
    }



    // 运行期 System.Type -> Cecil.TypeReference（带泛型上下文）
    static TypeReference ImportTypeCtx(ModuleDefinition target, Type t, MethodDefinition md)
    {
        if (t == null) return target.TypeSystem.Object;

        if (t.IsGenericParameter)
        {
            int pos = t.GenericParameterPosition;
            if (t.DeclaringMethod != null)
            {
                if (pos >= 0 && pos < md.GenericParameters.Count) return md.GenericParameters[pos];
                return target.TypeSystem.Object;
            }
            if (md.DeclaringType != null && pos >= 0 && pos < md.DeclaringType.GenericParameters.Count)
                return md.DeclaringType.GenericParameters[pos];
            return target.TypeSystem.Object;
        }

        if (t.IsByRef) return new ByReferenceType(ImportTypeCtx(target, t.GetElementType(), md));
        if (t.IsPointer) return new PointerType(ImportTypeCtx(target, t.GetElementType(), md));
        if (t.IsArray)
        {
            TypeReference et = ImportTypeCtx(target, t.GetElementType(), md);
            int rank = t.GetArrayRank();
            return rank == 1 ? new ArrayType(et) : new ArrayType(et, rank);
        }

        if (t.IsGenericType && t.ContainsGenericParameters)
        {
            Type def = t.GetGenericTypeDefinition();
            TypeReference defRef = target.Import(def);
            GenericInstanceType git = new GenericInstanceType(defRef);
            Type[] args = t.GetGenericArguments();
            for (int i = 0; i < args.Length; i++)
                git.GenericArguments.Add(ImportTypeCtx(target, args[i], md));
            return git;
        }

        return target.Import(t);
    }

    // 运行期 FieldInfo -> Cecil.FieldReference（带泛型上下文）
    static FieldReference ImportFieldCtx(ModuleDefinition target, FieldInfo fi, MethodDefinition md)
    {
        TypeReference decl = ImportTypeCtx(target, fi.DeclaringType, md);
        TypeReference fty = ImportTypeCtx(target, fi.FieldType, md);
        return new FieldReference(fi.Name, fty, decl);
    }

    // 运行期 MethodBase -> Cecil.MethodReference（带泛型上下文）
    static MethodReference ImportMethodCtx(ModuleDefinition target, MethodBase mb, MethodDefinition md)
    {
        if (mb == null) return null;

        MethodInfo mi = mb as MethodInfo;
        if (mi != null && mi.IsGenericMethod && !mi.IsGenericMethodDefinition)
        {
            MethodInfo def = mi.GetGenericMethodDefinition();
            MethodReference defRef = ImportMethodCtxCore(target, def, md);
            GenericInstanceMethod gim = new GenericInstanceMethod(defRef);
            Type[] args = mi.GetGenericArguments();
            for (int i = 0; i < args.Length; i++)
                gim.GenericArguments.Add(ImportTypeCtx(target, args[i], md));
            return gim;
        }

        return ImportMethodCtxCore(target, mb, md);
    }

    static MethodReference ImportMethodCtxCore(ModuleDefinition target, MethodBase mb, MethodDefinition md)
    {
        TypeReference decl = ImportTypeCtx(target, mb.DeclaringType, md);
        TypeReference ret = (mb is MethodInfo) ? ImportTypeCtx(target, ((MethodInfo)mb).ReturnType, md)
                                                : target.TypeSystem.Void;
        MethodReference mr = new MethodReference(mb.Name, ret, decl);
        mr.HasThis = !mb.IsStatic;
        mr.ExplicitThis = false;
        mr.CallingConvention = MethodCallingConvention.Default;

        ParameterInfo[] pars = mb.GetParameters();
        for (int i = 0; i < pars.Length; i++)
            mr.Parameters.Add(new ParameterDefinition(ImportTypeCtx(target, pars[i].ParameterType, md)));

        MethodInfo mi = mb as MethodInfo;
        if (mi != null && mi.IsGenericMethodDefinition)
        {
            Type[] gps = mi.GetGenericArguments();
            for (int i = 0; i < gps.Length; i++)
                mr.GenericParameters.Add(new GenericParameter(gps[i].Name, mr));
        }

        return mr;
    }

    static readonly Type[] EmptyTypes = new Type[0];

    static Type ResolveTypeCtx(System.Reflection.Module mod, int token, MethodBase ctx)
    {
        try
        {
            Type[] typeArgs = (ctx != null && ctx.DeclaringType != null && ctx.DeclaringType.IsGenericType)
                ? ctx.DeclaringType.GetGenericArguments() : EmptyTypes;
            Type[] methodArgs = (ctx != null && ctx.IsGenericMethod)
                ? ctx.GetGenericArguments() : EmptyTypes;
            return mod.ResolveType(token, typeArgs, methodArgs);
        }
        catch
        {
            try { return mod.ResolveType(token); } catch { return null; }
        }
    }

    static FieldInfo ResolveFieldCtx(System.Reflection.Module mod, int token, MethodBase ctx)
    {
        try
        {
            Type[] typeArgs = (ctx != null && ctx.DeclaringType != null && ctx.DeclaringType.IsGenericType)
                ? ctx.DeclaringType.GetGenericArguments() : EmptyTypes;
            Type[] methodArgs = (ctx != null && ctx.IsGenericMethod)
                ? ctx.GetGenericArguments() : EmptyTypes;
            return mod.ResolveField(token, typeArgs, methodArgs);
        }
        catch
        {
            try { return mod.ResolveField(token); } catch { return null; }
        }
    }

    static MethodBase ResolveMethodCtx(System.Reflection.Module mod, int token, MethodBase ctx)
    {
        try
        {
            Type[] typeArgs = (ctx != null && ctx.DeclaringType != null && ctx.DeclaringType.IsGenericType)
                ? ctx.DeclaringType.GetGenericArguments() : EmptyTypes;
            Type[] methodArgs = (ctx != null && ctx.IsGenericMethod)
                ? ctx.GetGenericArguments() : EmptyTypes;
            return mod.ResolveMethod(token, typeArgs, methodArgs);
        }
        catch
        {
            try { return mod.ResolveMethod(token); } catch { return null; }
        }
    }

    static void ForceResolveAllBodies(ModuleDefinition module)
    {
        void TouchType(TypeDefinition td)
        {
            for (int i = 0; i < td.Methods.Count; i++)
            {
                var m = td.Methods[i];
                if (!m.HasBody) continue;
                try
                {
                    var _ = m.Body.Instructions.Count; // 触发解析
                }
                catch (Exception ex)
                {
                    FileLogger.Log("WARN", "Force body failed: " + m.FullName + " : " + ex.Message + " -> stub");
                    try { EmitRetStub(m); } catch { /* 忽略 */ }
                }
            }
            for (int i = 0; i < td.NestedTypes.Count; i++)
                TouchType(td.NestedTypes[i]);
        }

        for (int i = 0; i < module.Types.Count; i++)
            TouchType(module.Types[i]);
    }

    static void TryReemitAllUnparsedBodiesFromLive(ModuleDefinition module, Assembly liveAsm)
    {
        var liveMod = liveAsm.ManifestModule;
        int reemitOk = 0, reemitMiss = 0, reemitStub = 0;

        for (int i = 0; i < module.Types.Count; i++)
            ReemitInType(module.Types[i], module, liveAsm, liveMod, ref reemitOk, ref reemitMiss, ref reemitStub);

        FileLogger.Log("MARK", $"Reemit pass: ok={reemitOk} miss={reemitMiss} stub={reemitStub}");
    }

    static void ReemitInType(
        TypeDefinition td,
        ModuleDefinition module,
        Assembly liveAsm,
        System.Reflection.Module liveMod,
        ref int ok, ref int miss, ref int stub)
    {
        for (int i = 0; i < td.Methods.Count; i++)
        {
            var md = td.Methods[i];
            if (!md.HasBody) continue;

            bool parsed;
            try { int c = md.Body.Instructions.Count; parsed = (md.Body.CodeSize > 0 && c > 0); }
            catch { parsed = false; }

            if (parsed) continue;

            var lt = FindLiveTypeByFullName(liveAsm, md.DeclaringType);
            if (lt == null)
            {
                miss++;
                try { EmitRetStub(md); stub++; } catch (Exception ex) { FileLogger.Log("WARN", "Emit stub fail: " + md.FullName + " :: " + ex.Message); }
                continue;
            }

            var lmb = FindLiveMethodLike(lt, md);
            if (lmb == null)
            {
                miss++;
                try { EmitRetStub(md); stub++; } catch (Exception ex) { FileLogger.Log("WARN", "Emit stub fail: " + md.FullName + " :: " + ex.Message); }
                continue;
            }

            System.Reflection.MethodBody rb = null;
            try { rb = lmb.GetMethodBody(); } catch { rb = null; }

            if (rb != null)
            {
                byte[] ilBytes = null;
                try { ilBytes = rb.GetILAsByteArray(); } catch { ilBytes = null; }

                if (ilBytes != null && ilBytes.Length > 0)
                {
                    try
                    {
                        ReplaceBodyWithLiveIL(module, md, liveMod, lmb, rb, ilBytes);
                        ok++;
                        continue;
                    }
                    catch (Exception ex)
                    {
                        FileLogger.Log("WARN", "Reemit fail: " + md.FullName + " :: " + ex.Message);
                    }
                }
            }

            try { EmitRetStub(md); stub++; }
            catch (Exception ex) { FileLogger.Log("WARN", "Emit stub fail: " + md.FullName + " :: " + ex.Message); miss++; }
        }

        for (int i = 0; i < td.NestedTypes.Count; i++)
            ReemitInType(td.NestedTypes[i], module, liveAsm, liveMod, ref ok, ref miss, ref stub);
    }

    static Type FindLiveTypeByFullName(Assembly liveAsm, TypeReference cecilType)
    {
        string full = cecilType.FullName;         // Namespace.Outer/Inner
        string refl = full.Replace('/', '+');     // Namespace.Outer+Inner
        try
        {
            Type t = liveAsm.GetType(refl, false);
            if (t != null) return t;
            Type[] all = liveAsm.GetTypes();
            for (int i = 0; i < all.Length; i++)
                if (all[i] != null && all[i].FullName == refl) return all[i];
        }
        catch { }
        return null;
    }

    static MethodBase FindLiveMethodLike(Type liveType, MethodDefinition md)
    {
        string name = md.Name;
        int gen = md.GenericParameters != null ? md.GenericParameters.Count : 0;
        int argc = md.Parameters != null ? md.Parameters.Count : 0;

        BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;

        if (name == ".ctor")
        {
            foreach (var ci in liveType.GetConstructors(flags))
                if ((ci.IsGenericMethodDefinition ? ci.GetGenericArguments().Length == gen : gen == 0)
                    && ci.GetParameters().Length == argc)
                    return ci;
            return null;
        }
        else if (name == ".cctor")
        {
            var cctor = liveType.TypeInitializer;
            if (cctor != null) return cctor;

            foreach (var m in liveType.GetMethods(flags))
                if (m.IsStatic && m.IsSpecialName && m.Name == ".cctor")
                    return m;
            return null;
        }
        else
        {
            foreach (var m in liveType.GetMethods(flags))
            {
                if (m.Name != name) continue;
                if (m.IsGenericMethodDefinition ? m.GetGenericArguments().Length != gen : gen != 0) continue;
                if (m.GetParameters().Length != argc) continue;
                if (LooseParamsMatch(m, md)) return m;
            }
            return null;
        }
    }

    static bool LooseParamsMatch(MethodBase m, MethodDefinition md)
    {
        ParameterInfo[] ps1 = m.GetParameters();
        Mono.Collections.Generic.Collection<ParameterDefinition> ps2 = md.Parameters;
        if (ps1.Length != ps2.Count) return false;

        for (int i = 0; i < ps1.Length; i++)
        {
            Type t1 = ps1[i].ParameterType;
            TypeReference t2 = ps2[i].ParameterType;

            if (t1.IsGenericParameter || t2.IsGenericParameter) continue;

            string n1 = (t1.FullName ?? t1.Name).Replace('+', '/');
            string n2 = t2.FullName;
            if (t1.IsByRef != t2.IsByReference) return false;

            int br = n2.IndexOf('[');
            string n2Base = br >= 0 ? n2.Substring(0, br) : n2;
            if (!n1.StartsWith(n2Base)) return false;
        }
        return true;
    }

    static void EmitRetStub(MethodDefinition md)
    {
        md.Body = new Mono.Cecil.Cil.MethodBody(md);
        ILProcessor il = md.Body.GetILProcessor();

        TypeReference retType = md.ReturnType;
        if (retType.MetadataType == MetadataType.Void)
        {
            il.Emit(OpCodes.Ret);
            return;
        }

        if (retType.IsValueType || retType.IsGenericParameter)
        {
            VariableDefinition tmp = new VariableDefinition(retType);
            md.Body.Variables.Add(tmp);
            md.Body.InitLocals = true;
            il.Emit(OpCodes.Ldloca_S, tmp);
            il.Emit(OpCodes.Initobj, retType);
            il.Emit(OpCodes.Ldloc_0);
            il.Emit(OpCodes.Ret);
            return;
        }

        il.Emit(OpCodes.Ldnull);
        il.Emit(OpCodes.Ret);
    }

    static void PreflightValidateModule(ModuleDefinition module)
    {
        if (module == null) return;
        for (int i = 0; i < module.Types.Count; i++)
            WalkTypeForPreflight(module.Types[i], module);

        FileLogger.Log("MARK", "PreflightValidateModule DONE");
    }

    // 预检：触发解析 + 最终硬化（规范分支/导入操作数/清理非法 EH/失败就打桩）
    static void WalkTypeForPreflight(TypeDefinition td, ModuleDefinition module)
    {
        if (td == null) return;

        // methods
        for (int i = 0; i < td.Methods.Count; i++)
        {
            var md = td.Methods[i];
            if (!md.HasBody) continue;

            // 触发解析；失败直接打桩
            try
            {
                var _ = md.Body.Instructions.Count;
            }
            catch (Exception ex)
            {
                FileLogger.Log("WARN", "Preflight read body failed: " + md.FullName + " : " + ex.Message + " -> stub");
                try { EmitRetStub(md); } catch { }
                continue;
            }

            try
            {
                // 先统一短格式与分支，再合法化跨保护区跳转，然后清 EH/导入操作数
                NormalizeShortVarAndArgForms(md);
                NormalizeBranchesNoEH(md);
                LegalizeProtectedRegionBranches(md);   // << 新增调用

                // 写盘前的最终硬化：导入/校验所有操作数 + 再次规范分支 + 清理非法 EH
                SanitizeOrStub(md, module);

                // 可选：记录跨保护区分支（只打日志）
                // LogCrossProtectedBranchIfAny(md);
            }
            catch (Exception ex)
            {
                FileLogger.Log("WARN", "Preflight fix failed: " + md.FullName + " : " + ex.Message);
            }
        }

        // nested types
        for (int i = 0; i < td.NestedTypes.Count; i++)
            WalkTypeForPreflight(td.NestedTypes[i], module);
    }


    // —— 分支标准化：把所有短跳转改成长跳转；修复空/越界目标；不改 EH —— //
    static void NormalizeBranchesNoEH(MethodDefinition md)
    {
        if (md == null || !md.HasBody) return;

        var body = md.Body;
        var list = body.Instructions;
        if (list == null) return;

        if (list.Count == 0)
            list.Add(Instruction.Create(OpCodes.Nop));

        Instruction tail = list[list.Count - 1];
        if (tail.OpCode != OpCodes.Nop)
        {
            tail = Instruction.Create(OpCodes.Nop);
            list.Add(tail);
        }

        for (int i = 0; i < list.Count; i++)
        {
            Instruction ins = list[i];
            OperandType ot = ins.OpCode.OperandType;

            if (ot == OperandType.InlineSwitch)
            {
                Instruction[] arr = ins.Operand as Instruction[];
                if (arr == null || arr.Length == 0)
                {
                    ins.OpCode = OpCodes.Br;
                    ins.Operand = tail;
                    continue;
                }
                for (int k = 0; k < arr.Length; k++)
                {
                    Instruction t = arr[k];
                    if (t == null || !ContainsInstruction(list, t)) arr[k] = tail;
                }
                ins.Operand = arr;
                continue;
            }

            if (ot == OperandType.ShortInlineBrTarget || ot == OperandType.InlineBrTarget)
            {
                Instruction tgt = ins.Operand as Instruction;
                if (tgt == null || !ContainsInstruction(list, tgt))
                    tgt = tail;

                if (ot == OperandType.ShortInlineBrTarget)
                {
                    OpCode longOp = ToLongForm(ins.OpCode);
                    if (longOp.Code != ins.OpCode.Code)
                        ins.OpCode = longOp;
                }

                ins.Operand = tgt;
                continue;
            }
        }
    }

    // —— 只删除非法 EH；不把边界改成 head/tail —— //
    static void DropInvalidExceptionHandlers(MethodDefinition md)
    {
        if (md == null || !md.HasBody) return;

        var body = md.Body;
        var list = body.Instructions;
        if (list == null || list.Count == 0) return;

        var ehs = body.ExceptionHandlers;
        if (ehs == null || ehs.Count == 0) return;

        Dictionary<Instruction, int> index = new Dictionary<Instruction, int>(list.Count);
        for (int i = 0; i < list.Count; i++) index[list[i]] = i;

        List<ExceptionHandler> toRemove = new List<ExceptionHandler>();

        for (int i = 0; i < ehs.Count; i++)
        {
            var eh = ehs[i];

            if (eh.TryStart == null || eh.TryEnd == null || eh.HandlerStart == null || eh.HandlerEnd == null)
            { toRemove.Add(eh); continue; }

            int ts, te, hs, he;
            if (!index.TryGetValue(eh.TryStart, out ts)) { toRemove.Add(eh); continue; }
            if (!index.TryGetValue(eh.TryEnd, out te)) { toRemove.Add(eh); continue; }
            if (!index.TryGetValue(eh.HandlerStart, out hs)) { toRemove.Add(eh); continue; }
            if (!index.TryGetValue(eh.HandlerEnd, out he)) { toRemove.Add(eh); continue; }

            if (te <= ts || he <= hs) { toRemove.Add(eh); continue; }

            if (eh.FilterStart != null && !index.ContainsKey(eh.FilterStart)) { toRemove.Add(eh); continue; }

            if (eh.HandlerType != ExceptionHandlerType.Catch)
            {
                if (eh.CatchType != null) eh.CatchType = null;
            }
            else
            {
                if (eh.CatchType == null) { toRemove.Add(eh); continue; }
            }
        }

        for (int i = 0; i < toRemove.Count; i++)
            ehs.Remove(toRemove[i]);
    }

    // —— 诊断日志：发现“跨保护区”的分支（只打日志，不做修改） —— //
    static void LogCrossProtectedBranchIfAny(MethodDefinition md)
    {
        if (md == null || !md.HasBody) return;

        var body = md.Body;
        var list = body.Instructions;
        if (list == null || list.Count == 0) return;

        var ehs = body.ExceptionHandlers;
        if (ehs == null || ehs.Count == 0) return;

        Dictionary<Instruction, int> index = new Dictionary<Instruction, int>(list.Count);
        for (int i = 0; i < list.Count; i++) index[list[i]] = i;

        for (int i = 0; i < list.Count; i++)
        {
            var ins = list[i];
            var ot = ins.OpCode.OperandType;

            if (ot == OperandType.InlineBrTarget || ot == OperandType.ShortInlineBrTarget)
            {
                var tgt = ins.Operand as Instruction;
                int ti;
                if (tgt == null || !index.TryGetValue(tgt, out ti)) continue;

                string srcRegion = GetRegionOfInstruction(i, ehs, index);
                string dstRegion = GetRegionOfInstruction(ti, ehs, index);
                if (srcRegion != dstRegion)
                {
                    FileLogger.Log("WARN", "CrossEH: " + md.FullName + " " + ins.OpCode.Code +
                        " src[" + srcRegion + "] -> dst[" + dstRegion + "]");
                }
            }
            else if (ot == OperandType.InlineSwitch)
            {
                var arr = ins.Operand as Instruction[];
                if (arr == null || arr.Length == 0) continue;

                string srcRegion = GetRegionOfInstruction(i, ehs, index);
                for (int k = 0; k < arr.Length; k++)
                {
                    var t = arr[k];
                    int ti2;
                    if (t == null || !index.TryGetValue(t, out ti2)) continue;
                    string dstRegion = GetRegionOfInstruction(ti2, ehs, index);
                    if (srcRegion != dstRegion)
                    {
                        FileLogger.Log("WARN", "CrossEH: " + md.FullName + " switch#" + k +
                            " src[" + srcRegion + "] -> dst[" + dstRegion + "]");
                    }
                }
            }
        }
    }

    static string GetRegionOfInstruction(int insIndex, IList<ExceptionHandler> ehs, Dictionary<Instruction, int> indexMap)
    {
        for (int i = 0; i < ehs.Count; i++)
        {
            var eh = ehs[i];

            int ts, te, hs, he;
            if (!indexMap.TryGetValue(eh.TryStart, out ts)) continue;
            if (!indexMap.TryGetValue(eh.TryEnd, out te)) continue;
            if (!indexMap.TryGetValue(eh.HandlerStart, out hs)) continue;
            if (!indexMap.TryGetValue(eh.HandlerEnd, out he)) continue;

            // [Start, End)
            if (insIndex >= ts && insIndex < te) return "T#" + i;
            if (insIndex >= hs && insIndex < he) return "H#" + i;
        }
        return "U";
    }

    static OpCode ToLongForm(OpCode op)
    {
        switch (op.Code)
        {
            case Code.Br_S: return OpCodes.Br;
            case Code.Brfalse_S: return OpCodes.Brfalse;
            case Code.Brtrue_S: return OpCodes.Brtrue;
            case Code.Beq_S: return OpCodes.Beq;
            case Code.Bge_S: return OpCodes.Bge;
            case Code.Bgt_S: return OpCodes.Bgt;
            case Code.Ble_S: return OpCodes.Ble;
            case Code.Blt_S: return OpCodes.Blt;
            case Code.Bge_Un_S: return OpCodes.Bge_Un;
            case Code.Bgt_Un_S: return OpCodes.Bgt_Un;
            case Code.Ble_Un_S: return OpCodes.Ble_Un;
            case Code.Blt_Un_S: return OpCodes.Blt_Un;
            case Code.Bne_Un_S: return OpCodes.Bne_Un;
            case Code.Leave_S: return OpCodes.Leave;
            default: return op;
        }
    }

    static bool ContainsInstruction(IList<Instruction> list, Instruction ins)
    {
        if (ins == null || list == null) return false;
        for (int i = 0; i < list.Count; i++) if (object.ReferenceEquals(list[i], ins)) return true;
        return false;
    }

    // —— 写盘前最终 Sanitize：失败就 stub —— //
    static void SanitizeOrStub(MethodDefinition md, ModuleDefinition module)
    {
        FileLogger.Log("MARK", "Sanitize enter: " + md.FullName);
        bool ok = true;
        try { ok = SanitizeOperandsForWriter(md, module); }
        catch (Exception ex) { ok = false; FileLogger.Log("WARN", "Sanitize ex: " + md.FullName + " :: " + ex.Message); }
        if (!ok) { FileLogger.Log("WARN", "Sanitize fail -> stub : " + md.FullName); EmitRetStub(md); }
        else { FileLogger.Log("MARK", "Sanitize ok: " + md.FullName); }
    }

    // 把所有“带元数据 token”的操作数强制归属于当前 module；
    // 同时验证/修复分支与 switch 操作数；如发现无法修复的异常，返回 false。
    static bool SanitizeOperandsForWriter(MethodDefinition md, ModuleDefinition module)
    {
        if (md == null || !md.HasBody) return true;

        var body = md.Body;
        var list = body.Instructions;
        if (list == null) return true;

        // 1) 统一分支、补尾 NOP（不动 EH）
        NormalizeBranchesNoEH(md);

        // 2) 清理非法 EH（不去把边界拉到 head/tail）
        DropInvalidExceptionHandlers(md);

        // 3) 把所有操作数 re-import 到当前 module；校验类型是否匹配
        for (int i = 0; i < list.Count; i++)
        {
            var ins = list[i];
            var ot = ins.OpCode.OperandType;
            object opnd = ins.Operand;

            try
            {
                switch (ot)
                {
                    case OperandType.InlineNone:
                        if (opnd != null) ins.Operand = null;
                        break;

                    case OperandType.InlineType:
                        {
                            var tr = opnd as TypeReference;
                            if (tr == null) return false;
                            if (tr.Module != module) ins.Operand = module.Import(tr);
                            break;
                        }

                    case OperandType.InlineField:
                        {
                            var fr = opnd as FieldReference;
                            if (fr == null) return false;
                            if (fr.Module != module) ins.Operand = module.Import(fr);
                            break;
                        }

                    case OperandType.InlineMethod:
                        {
                            var mr = opnd as MethodReference;
                            if (mr == null) return false;
                            if (mr.Module != module) ins.Operand = module.Import(mr);
                            break;
                        }

                    case OperandType.InlineTok:
                        {
                            var trTok = opnd as TypeReference;
                            if (trTok != null)
                            {
                                if (trTok.Module != module) ins.Operand = module.Import(trTok);
                                break;
                            }
                            var frTok = opnd as FieldReference;
                            if (frTok != null)
                            {
                                if (frTok.Module != module) ins.Operand = module.Import(frTok);
                                break;
                            }
                            var mrTok = opnd as MethodReference;
                            if (mrTok != null)
                            {
                                if (mrTok.Module != module) ins.Operand = module.Import(mrTok);
                                break;
                            }
                            // InlineTok 但操作数不是 Type/Field/Method，引擎无法写
                            return false;
                        }

                    case OperandType.InlineSig:
                        {
                            var cs = opnd as CallSite;
                            if (cs == null) return false;

                            bool needRebuild = (cs.Module != module);
                            if (!needRebuild)
                            {
                                if (cs.ReturnType == null || cs.ReturnType.Module != module) needRebuild = true;
                                else
                                {
                                    for (int p = 0; p < cs.Parameters.Count && !needRebuild; p++)
                                    {
                                        if (cs.Parameters[p] == null ||
                                            cs.Parameters[p].ParameterType == null ||
                                            cs.Parameters[p].ParameterType.Module != module)
                                            needRebuild = true;
                                    }
                                }
                            }

                            if (needRebuild)
                            {
                                var cs2 = new CallSite(module.Import(cs.ReturnType));
                                cs2.HasThis = cs.HasThis;
                                cs2.ExplicitThis = cs.ExplicitThis;
                                cs2.CallingConvention = cs.CallingConvention;
                                for (int p = 0; p < cs.Parameters.Count; p++)
                                {
                                    var pt = cs.Parameters[p] != null ? cs.Parameters[p].ParameterType : null;
                                    if (pt == null) return false;
                                    cs2.Parameters.Add(new ParameterDefinition(module.Import(pt)));
                                }
                                ins.Operand = cs2;
                            }
                            break;
                        }

                    case OperandType.InlineSwitch:
                        {
                            var arr = opnd as Instruction[];
                            if (arr == null || arr.Length == 0)
                            {
                                // 退化为 br tail（确保尾 NOP 存在）
                                var tail = list[list.Count - 1];
                                if (tail.OpCode != OpCodes.Nop) { tail = Instruction.Create(OpCodes.Nop); list.Add(tail); }
                                ins.OpCode = OpCodes.Br;
                                ins.Operand = tail;
                            }
                            break;
                        }

                    case OperandType.InlineBrTarget:
                    case OperandType.ShortInlineBrTarget:
                    case OperandType.ShortInlineI:
                    case OperandType.InlineI:
                    case OperandType.InlineI8:
                    case OperandType.ShortInlineR:
                    case OperandType.InlineR:
                    case OperandType.InlineString:
                    case OperandType.InlineVar:
                    case OperandType.ShortInlineVar:
                    case OperandType.InlineArg:
                    case OperandType.ShortInlineArg:
                        // 这些在 ReplaceBody/Normalize 中已处理；留到 HardValidate 最终把关
                        break;

                    default:
                        return false;
                }
            }
            catch
            {
                return false;
            }
        }

        // 4) 再做一次短格式统一 & 分支标准化
        NormalizeShortVarAndArgForms(md);
        NormalizeBranchesNoEH(md);

        // 5) 严格 EH 检测（跨保护区分支/开关直接宣告失败，交给上层打桩）
        if (!LegalizeProtectedRegionBranches(md))
            return false;

        // 6) 最后做一次“写盘视角”的硬校验，修不动就让上层打桩
        if (!HardValidateForCecilWriter(md, module))
            return false;

        return true;
    }

    // 把所有 .s 短格式（arg/var/leave）一律换成长格式
    static void NormalizeShortVarAndArgForms(MethodDefinition md)
    {
        if (md == null || !md.HasBody) return;
        var ilist = md.Body.Instructions;
        if (ilist == null || ilist.Count == 0) return;

        for (int i = 0; i < ilist.Count; i++)
        {
            var ins = ilist[i];
            switch (ins.OpCode.Code)
            {
                case Code.Ldloc_S: ins.OpCode = OpCodes.Ldloc; break;
                case Code.Stloc_S: ins.OpCode = OpCodes.Stloc; break;
                case Code.Ldloca_S: ins.OpCode = OpCodes.Ldloca; break;

                case Code.Ldarg_S: ins.OpCode = OpCodes.Ldarg; break;
                case Code.Starg_S: ins.OpCode = OpCodes.Starg; break;
                case Code.Ldarga_S: ins.OpCode = OpCodes.Ldarga; break;

                case Code.Leave_S: ins.OpCode = OpCodes.Leave; break;
            }
        }
    }

    // 步骤 2 的核心：确保 ldarg/ldarga/starg 使用“本方法”的 ParameterDefinition/ThisParameter；
    // ldloc/stloc/ldloca 使用“本 Body”的 VariableDefinition。
    static bool FixArgVarOperandsForWriter(MethodDefinition md)
    {
        if (md == null || !md.HasBody) return true;
        var body = md.Body;
        var list = body.Instructions;
        if (list == null) return true;

        for (int i = 0; i < list.Count; i++)
        {
            var ins = list[i];
            var ot = ins.OpCode.OperandType;
            var opnd = ins.Operand;

            try
            {
                // 参数操作数（InlineArg / ShortInlineArg），或者 Cecil 把 arg 编到 InlineVar 也要照顾
                if (ot == OperandType.InlineArg || ot == OperandType.ShortInlineArg ||
                    ((ot == OperandType.InlineVar || ot == OperandType.ShortInlineVar) && IsArgOp(ins.OpCode)))
                {
                    var pd = opnd as ParameterDefinition;
                    if (pd == null) return false;

                    // this 参数
                    int pdIdx = GetParamIndexSafe(pd);
                    if (md.HasThis && pdIdx == 0)
                    {
                        if (body.ThisParameter == null) return false;
                        ins.Operand = body.ThisParameter;
                        continue;
                    }

                    // 非 this：必须是当前 md 的 Parameters 里的实例
                    if (pd.Method != md)
                    {
                        int idx = pdIdx;
                        if (idx < 0) return false;
                        if (md.HasThis) idx = idx - 1; // Cecil 索引 0 是 this，Parameters 从实参 0 开始
                        if (idx < 0 || idx >= md.Parameters.Count) return false;
                        ins.Operand = md.Parameters[idx];
                    }
                    else
                    {
                        // 虽然属于当前方法，但如果它其实是 this，也要换成 ThisParameter
                        if (md.HasThis && pdIdx == 0)
                        {
                            if (body.ThisParameter == null) return false;
                            ins.Operand = body.ThisParameter;
                        }
                    }
                    continue;
                }

                // 局部变量操作数（InlineVar / ShortInlineVar 且不是 arg 指令）
                if ((ot == OperandType.InlineVar || ot == OperandType.ShortInlineVar) && !IsArgOp(ins.OpCode))
                {
                    var vd = opnd as VariableDefinition;
                    if (vd == null) return false;

                    // 变量必须来自当前 Body
                    bool sameOwner = false;
                    var vars = body.Variables;
                    for (int k = 0; k < vars.Count; k++)
                        if (object.ReferenceEquals(vars[k], vd)) { sameOwner = true; break; }

                    if (!sameOwner)
                    {
                        int vidx = GetVarIndexSafe(vd);
                        if (vidx < 0 || vidx >= vars.Count) return false;
                        ins.Operand = vars[vidx];
                    }
                    continue;
                }

                // 其他操作数：不处理
            }
            catch
            {
                return false;
            }
        }

        return true;
    }

    // 兼容 0.9.6：ParameterDefinition 可能有 Index 或 Sequence；拿不到就在线性搜索 Method.Parameters
    static int GetParamIndexSafe(ParameterDefinition p)
    {
        if (p == null) return -1;
        try
        {
            var tp = typeof(ParameterDefinition);
            var pi = tp.GetProperty("Index") ?? tp.GetProperty("Sequence");
            if (pi != null)
            {
                object v = pi.GetValue(p, null);
                if (v is int) return (int)v;
            }
        }
        catch { }
        try
        {
            var pars = p.Method?.Parameters;
            if (pars != null)
            {
                for (int i = 0; i < pars.Count; i++)
                    if (object.ReferenceEquals(pars[i], p)) return i;
            }
        }
        catch { }
        return -1;
    }

    // 兼容 0.9.6：VariableDefinition 一般有 Index，拿不到就返回 -1
    static int GetVarIndexSafe(VariableDefinition v)
    {
        if (v == null) return -1;
        try
        {
            var tv = typeof(VariableDefinition);
            var pi = tv.GetProperty("Index");
            if (pi != null)
            {
                object val = pi.GetValue(v, null);
                if (val is int) return (int)val;
            }
        }
        catch { }
        return -1;
    }

    // —— 仅检测：若存在跨受保护区分支/开关目标，返回 false；不做任何修补 —— //
    static bool LegalizeProtectedRegionBranches(MethodDefinition md)
    {
        if (md == null || !md.HasBody) return true;

        var body = md.Body;
        var list = body.Instructions;
        if (list == null || list.Count == 0) return true;

        var ehs = body.ExceptionHandlers;
        // 指令 -> 索引
        Dictionary<Instruction, int> index = new Dictionary<Instruction, int>(list.Count);
        for (int i = 0; i < list.Count; i++) index[list[i]] = i;

        // 判断某条指令是否在体内
        Func<Instruction, bool> inBody = delegate (Instruction ins)
        {
            if (ins == null) return false;
            int _; return index.TryGetValue(ins, out _);
        };

        // 按行扫描，遇到 br*/switch 就做区域一致性检查（leave/leave.s 忽略）
        for (int i = 0; i < list.Count; i++)
        {
            Instruction ins = list[i];
            OperandType ot = ins.OpCode.OperandType;

            // leave 合法离开保护区，直接跳过
            if (ins.OpCode.Code == Code.Leave || ins.OpCode.Code == Code.Leave_S)
                continue;

            if (ot == OperandType.InlineBrTarget || ot == OperandType.ShortInlineBrTarget)
            {
                Instruction tgt = ins.Operand as Instruction;
                if (!inBody(tgt)) return false; // 目标不在方法体内

                string srcRegion = GetRegionOfInstruction(i, ehs, index);
                int ti;
                if (!index.TryGetValue(tgt, out ti)) return false;
                string dstRegion = GetRegionOfInstruction(ti, ehs, index);

                if (srcRegion != dstRegion)
                {
                    // 任何 br*/bxx 跨区域（U/T#/H#）都宣告不安全
                    return false;
                }
            }
            else if (ot == OperandType.InlineSwitch)
            {
                Instruction[] arr = ins.Operand as Instruction[];
                if (arr == null || arr.Length == 0) continue; // 空表后面会被 Normalize 处理

                string srcRegion = GetRegionOfInstruction(i, ehs, index);
                for (int k = 0; k < arr.Length; k++)
                {
                    Instruction t = arr[k];
                    if (!inBody(t)) return false;
                    int ti2;
                    if (!index.TryGetValue(t, out ti2)) return false;
                    string dstRegion = GetRegionOfInstruction(ti2, ehs, index);
                    if (srcRegion != dstRegion)
                    {
                        return false;
                    }
                }
            }
        }

        return true;
    }

    // —— 以 Cecil 写盘视角做强校验：
    //  1) InlineVar 必须指向当前 body.Variables；否则尽力修正为索引处变量
    //  2) InlineArg 必须指向当前 md.Parameters 或 body.ThisParameter；否则尽力按 Sequence/索引修正
    //  3) 所有分支 / switch 目标必须在体内
    //  无法修正则返回 false（上层会打桩） —— //
    static bool HardValidateForCecilWriter(MethodDefinition md, ModuleDefinition module)
    {
        if (md == null || !md.HasBody) return true;

        var body = md.Body;
        var ilist = body.Instructions;
        if (ilist == null || ilist.Count == 0) return true;

        var vars = body.Variables;
        var pars = md.Parameters;
        var thisParam = body.ThisParameter;

        // 工具：检查某条指令是否在体内
        Func<Instruction, bool> inBody = delegate (Instruction ins)
        {
            if (ins == null) return false;
            for (int i = 0; i < ilist.Count; i++) if (object.ReferenceEquals(ilist[i], ins)) return true;
            return false;
        };

        // 工具：查找变量索引
        Func<VariableDefinition, int> findVarIndex = delegate (VariableDefinition vd)
        {
            if (vd == null) return -1;
            for (int i = 0; i < vars.Count; i++) if (object.ReferenceEquals(vars[i], vd)) return i;

            // 兜底：尝试用反射拿 Index/VariableDefinition.Index
            try
            {
                var pi = typeof(VariableDefinition).GetProperty("Index", BindingFlags.Public | BindingFlags.Instance);
                if (pi != null)
                {
                    object val = pi.GetValue(vd, null);
                    if (val is int)
                    {
                        int idx = (int)val;
                        if (idx >= 0 && idx < vars.Count) return idx;
                    }
                }
            }
            catch { }

            // 再兜底：按类型第一个匹配（不保证 100% 准确，但能救一批）
            try
            {
                string tn = vd.VariableType != null ? vd.VariableType.FullName : null;
                if (tn != null)
                {
                    for (int i = 0; i < vars.Count; i++)
                    {
                        var vi = vars[i];
                        if (vi != null && vi.VariableType != null && vi.VariableType.FullName == tn)
                            return i;
                    }
                }
            }
            catch { }
            return -1;
        };

        // 工具：查找参数索引（不含 this）
        Func<ParameterDefinition, int> findParamIndex = delegate (ParameterDefinition pd)
        {
            if (pd == null) return -1;
            for (int i = 0; i < pars.Count; i++) if (object.ReferenceEquals(pars[i], pd)) return i;

            // 兜底：Sequence（Cecil 0.9.6 有 Sequence 属性）
            try
            {
                var prop = typeof(ParameterDefinition).GetProperty("Sequence", BindingFlags.Public | BindingFlags.Instance);
                if (prop != null)
                {
                    object val = prop.GetValue(pd, null);
                    if (val is int)
                    {
                        int seq = (int)val; // 含隐式 this（非静态方法时）
                        int idx = md.HasThis ? (seq - 1) : seq;
                        if (!md.HasThis && seq == 0) idx = 0;
                        if (idx >= 0 && idx < pars.Count) return idx;
                        // seq == 0 且 HasThis，则可能是 thisParameter
                        if (md.HasThis && seq == 0 && thisParam != null && object.ReferenceEquals(pd, thisParam))
                            return -2; // 特殊标记：this
                    }
                }
            }
            catch { }

            // 再兜底：按类型匹配第一个
            try
            {
                string tn = pd.ParameterType != null ? pd.ParameterType.FullName : null;
                if (tn != null)
                {
                    for (int i = 0; i < pars.Count; i++)
                        if (pars[i] != null && pars[i].ParameterType != null && pars[i].ParameterType.FullName == tn)
                            return i;
                }
            }
            catch { }
            return -1;
        };

        // 确保尾部 NOP，方便退化分支时使用
        Instruction tail = ilist[ilist.Count - 1];
        if (tail.OpCode != OpCodes.Nop)
        {
            tail = Instruction.Create(OpCodes.Nop);
            ilist.Add(tail);
        }

        for (int i = 0; i < ilist.Count; i++)
        {
            var ins = ilist[i];
            var ot = ins.OpCode.OperandType;

            // 分支 / switch 目标必须在体内
            if (ot == OperandType.InlineBrTarget || ot == OperandType.ShortInlineBrTarget)
            {
                var tgt = ins.Operand as Instruction;
                if (!inBody(tgt)) return false;
            }
            else if (ot == OperandType.InlineSwitch)
            {
                var arr = ins.Operand as Instruction[];
                if (arr == null || arr.Length == 0)
                {
                    // 退化为 br tail
                    ins.OpCode = OpCodes.Br;
                    ins.Operand = tail;
                }
                else
                {
                    for (int k = 0; k < arr.Length; k++)
                    {
                        if (!inBody(arr[k])) return false;
                    }
                }
            }
            else if (ot == OperandType.InlineVar || ot == OperandType.ShortInlineVar)
            {
                // 必须是当前 body 的 VariableDefinition
                var vd = ins.Operand as VariableDefinition;
                if (vd == null) return false;
                int idx = findVarIndex(vd);
                if (idx < 0) return false;
                // 统一替换为“当前 body 的变量实例”
                if (!object.ReferenceEquals(vars[idx], vd))
                    ins.Operand = vars[idx];
            }
            else if (ot == OperandType.InlineArg || ot == OperandType.ShortInlineArg)
            {
                // 必须是当前 md 的 ParameterDefinition 或 thisParameter
                var pd = ins.Operand as ParameterDefinition;
                if (pd == null) return false;

                // this 参数单独通过
                if (thisParam != null && object.ReferenceEquals(pd, thisParam))
                    continue;

                int pidx = findParamIndex(pd);
                if (pidx == -2)
                {
                    // -2：表示 this
                    if (thisParam == null) return false;
                    ins.Operand = thisParam;
                }
                else if (pidx >= 0)
                {
                    if (!object.ReferenceEquals(pars[pidx], pd))
                        ins.Operand = pars[pidx];
                }
                else
                {
                    return false;
                }
            }
            // 其它操作数类型不在这里处理（前面 Sanitize 已经 import/规范化）
        }

        return true;
    }


    // —— 极简 IL Reader（只使用 Cecil 的 OpCodes，支持 Short/InlineVar/Arg） —— //
    class ILReader
    {
        static readonly OpCode[] OneByte = new OpCode[0x100];
        static readonly OpCode[] TwoByte = new OpCode[0x100];

        static ILReader()
        {
            FieldInfo[] fields = typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static);
            for (int i = 0; i < fields.Length; i++)
            {
                OpCode op = (OpCode)fields[i].GetValue(null);
                ushort v = (ushort)op.Value;
                if (v < 0x100) OneByte[v] = op;
                else if ((v & 0xff00) == 0xfe00) TwoByte[v & 0xff] = op;
            }
        }

        readonly byte[] _il; int _p;
        public ILReader(byte[] il) { _il = il ?? new byte[0]; _p = 0; }
        public bool HasNext { get { return _p < _il.Length; } }
        public int Offset { get { return _p; } }
        public int CurrentOffset { get { return _il != null ? _p : 0; } }

        public void Read(out OpCode op, out object operand)
        {
            byte b = _il[_p++];
            if (b != 0xFE) op = OneByte[b];
            else { byte b2 = _il[_p++]; op = TwoByte[b2]; }

            operand = null;
            switch (op.OperandType)
            {
                case OperandType.InlineNone: break;
                case OperandType.ShortInlineI: operand = (sbyte)_il[_p++]; break;
                case OperandType.InlineI: operand = ReadInt32(); break;
                case OperandType.InlineI8: operand = ReadInt64(); break;
                case OperandType.ShortInlineR: operand = ReadSingle(); break;
                case OperandType.InlineR: operand = ReadDouble(); break;
                case OperandType.InlineString: operand = ReadInt32(); break;
                case OperandType.InlineField: operand = ReadInt32(); break;
                case OperandType.InlineMethod: operand = ReadInt32(); break;
                case OperandType.InlineType: operand = ReadInt32(); break;
                case OperandType.InlineTok: operand = ReadInt32(); break;
                case OperandType.InlineSig: operand = ReadInt32(); break;
                case OperandType.ShortInlineBrTarget: operand = (sbyte)_il[_p++]; break;
                case OperandType.InlineBrTarget: operand = ReadInt32(); break;
                case OperandType.InlineSwitch:
                    int n = ReadInt32();
                    int[] arr = new int[n];
                    for (int i = 0; i < n; i++) arr[i] = ReadInt32();
                    operand = arr; break;
                case OperandType.ShortInlineVar: operand = (byte)_il[_p++]; break;
                case OperandType.InlineVar: operand = ReadUInt16(); break;
                case OperandType.ShortInlineArg: operand = (byte)_il[_p++]; break;
                case OperandType.InlineArg: operand = ReadUInt16(); break;
                default:
                    throw new NotSupportedException("Unsupported " + op.OperandType);
            }
        }

        int ReadInt32() { int v = BitConverter.ToInt32(_il, _p); _p += 4; return v; }
        ushort ReadUInt16() { ushort v = BitConverter.ToUInt16(_il, _p); _p += 2; return v; }
        long ReadInt64() { long v = BitConverter.ToInt64(_il, _p); _p += 8; return v; }
        float ReadSingle() { float v = BitConverter.ToSingle(_il, _p); _p += 4; return v; }
        double ReadDouble() { double v = BitConverter.ToDouble(_il, _p); _p += 8; return v; }
    }
}

// —— 签名解析工具：把 ResolveSignature(sigTok) 的 blob 解析为 Cecil.CallSite —— //
internal static class SigUtil
{
    // ECMA-335 元素类型常量（常用）
    const byte ELEMENT_TYPE_END = 0x00;
    const byte ELEMENT_TYPE_VOID = 0x01;
    const byte ELEMENT_TYPE_BOOLEAN = 0x02;
    const byte ELEMENT_TYPE_CHAR = 0x03;
    const byte ELEMENT_TYPE_I1 = 0x04;
    const byte ELEMENT_TYPE_U1 = 0x05;
    const byte ELEMENT_TYPE_I2 = 0x06;
    const byte ELEMENT_TYPE_U2 = 0x07;
    const byte ELEMENT_TYPE_I4 = 0x08;
    const byte ELEMENT_TYPE_U4 = 0x09;
    const byte ELEMENT_TYPE_I8 = 0x0a;
    const byte ELEMENT_TYPE_U8 = 0x0b;
    const byte ELEMENT_TYPE_R4 = 0x0c;
    const byte ELEMENT_TYPE_R8 = 0x0d;
    const byte ELEMENT_TYPE_STRING = 0x0e;
    const byte ELEMENT_TYPE_PTR = 0x0f;
    const byte ELEMENT_TYPE_BYREF = 0x10;
    const byte ELEMENT_TYPE_VALUETYPE = 0x11;
    const byte ELEMENT_TYPE_CLASS = 0x12;
    const byte ELEMENT_TYPE_VAR = 0x13;
    const byte ELEMENT_TYPE_ARRAY = 0x14;
    const byte ELEMENT_TYPE_GENERICINST = 0x15;
    const byte ELEMENT_TYPE_TYPEDBYREF = 0x16;
    const byte ELEMENT_TYPE_I = 0x18;
    const byte ELEMENT_TYPE_U = 0x19;
    const byte ELEMENT_TYPE_FNPTR = 0x1b;
    const byte ELEMENT_TYPE_OBJECT = 0x1c;
    const byte ELEMENT_TYPE_SZARRAY = 0x1d;
    const byte ELEMENT_TYPE_MVAR = 0x1e;
    const byte ELEMENT_TYPE_CMOD_REQD = 0x1f;
    const byte ELEMENT_TYPE_CMOD_OPT = 0x20;
    const byte ELEMENT_TYPE_SENTINEL = 0x41;

    // TypeDefOrRefOrSpecEncoded tag
    const int TAG_TYPEDEF = 0;
    const int TAG_TYPEREF = 1;
    const int TAG_TYPESPEC = 2;

    public static CallSite ParseCallSite(ModuleDefinition targetModule, System.Reflection.Module liveMod, int sigToken)
    {
        byte[] blob = liveMod.ResolveSignature(sigToken);
        if (blob == null || blob.Length == 0) return null;

        var r = new BlobReader(blob);

        byte first = r.ReadU1();
        bool hasThis = (first & 0x20) != 0;
        bool explicitThis = (first & 0x40) != 0;
        byte convLow = (byte)(first & 0x0F);
        bool isGenericSig = (first & 0x10) != 0;
        if (isGenericSig) ReadCompressedUInt(ref r); // 跳过泛型参数个数

        uint paramCount = ReadCompressedUInt(ref r);

        var retType = ReadType(ref r, targetModule, liveMod);

        var cs = new CallSite(retType);
        cs.HasThis = hasThis;
        cs.ExplicitThis = explicitThis;
        cs.CallingConvention = MapCallingConvention(convLow);

        bool afterSentinel = false;
        for (uint i = 0; i < paramCount; i++)
        {
            byte peek = r.PeekU1();
            if (peek == ELEMENT_TYPE_SENTINEL) { r.ReadU1(); afterSentinel = true; }

            var pType = ReadType(ref r, targetModule, liveMod);
            if (afterSentinel) pType = new SentinelType(pType);
            cs.Parameters.Add(new ParameterDefinition(pType));
        }

        return cs;
    }

    static MethodCallingConvention MapCallingConvention(byte convLow)
    {
        switch (convLow)
        {
            case 0x00: return MethodCallingConvention.Default;
            case 0x01: return MethodCallingConvention.C;
            case 0x02: return MethodCallingConvention.StdCall;
            case 0x03: return MethodCallingConvention.ThisCall;
            case 0x04: return MethodCallingConvention.FastCall;
            case 0x05: return MethodCallingConvention.VarArg;
            default: return MethodCallingConvention.Default;
        }
    }

    static TypeReference ReadType(ref BlobReader r, ModuleDefinition target, System.Reflection.Module liveMod)
    {
        byte et = r.ReadU1();

        // 跳过自定义修饰符（可重复）
        while (et == ELEMENT_TYPE_CMOD_REQD || et == ELEMENT_TYPE_CMOD_OPT)
        {
            DecodeTypeDefOrRefOrSpec(ref r, liveMod); // 忽略修饰符类型
            et = r.ReadU1();
        }

        switch (et)
        {
            case ELEMENT_TYPE_VOID: return target.TypeSystem.Void;
            case ELEMENT_TYPE_BOOLEAN: return target.TypeSystem.Boolean;
            case ELEMENT_TYPE_CHAR: return target.TypeSystem.Char;
            case ELEMENT_TYPE_I1: return target.TypeSystem.SByte;
            case ELEMENT_TYPE_U1: return target.TypeSystem.Byte;
            case ELEMENT_TYPE_I2: return target.TypeSystem.Int16;
            case ELEMENT_TYPE_U2: return target.TypeSystem.UInt16;
            case ELEMENT_TYPE_I4: return target.TypeSystem.Int32;
            case ELEMENT_TYPE_U4: return target.TypeSystem.UInt32;
            case ELEMENT_TYPE_I8: return target.TypeSystem.Int64;
            case ELEMENT_TYPE_U8: return target.TypeSystem.UInt64;
            case ELEMENT_TYPE_R4: return target.TypeSystem.Single;
            case ELEMENT_TYPE_R8: return target.TypeSystem.Double;
            case ELEMENT_TYPE_STRING: return target.TypeSystem.String;
            case ELEMENT_TYPE_OBJECT: return target.TypeSystem.Object;
            case ELEMENT_TYPE_I: return target.TypeSystem.IntPtr;
            case ELEMENT_TYPE_U: return target.TypeSystem.UIntPtr;

            case ELEMENT_TYPE_BYREF: return new ByReferenceType(ReadType(ref r, target, liveMod));
            case ELEMENT_TYPE_PTR: return new PointerType(ReadType(ref r, target, liveMod));

            case ELEMENT_TYPE_SZARRAY: return new ArrayType(ReadType(ref r, target, liveMod));

            case ELEMENT_TYPE_ARRAY:
                {
                    var elem = ReadType(ref r, target, liveMod);
                    uint rank = ReadCompressedUInt(ref r);
                    uint numsizes = ReadCompressedUInt(ref r);
                    for (uint i = 0; i < numsizes; i++) ReadCompressedUInt(ref r);
                    uint numlb = ReadCompressedUInt(ref r);
                    for (uint i = 0; i < numlb; i++) ReadCompressedUInt(ref r);
                    return new ArrayType(elem, (int)rank);
                }

            case ELEMENT_TYPE_CLASS:
            case ELEMENT_TYPE_VALUETYPE:
                {
                    int mdToken = DecodeTypeDefOrRefOrSpec(ref r, liveMod);
                    var rt = liveMod.ResolveType(mdToken);
                    return target.Import(rt);
                }

            case ELEMENT_TYPE_GENERICINST:
                {
                    byte next = r.ReadU1(); // class/valuetype
                    int mdToken = DecodeTypeDefOrRefOrSpec(ref r, liveMod);
                    var rawType = target.Import(liveMod.ResolveType(mdToken));
                    var git = new GenericInstanceType(rawType);
                    uint argc = ReadCompressedUInt(ref r);
                    for (uint i = 0; i < argc; i++)
                        git.GenericArguments.Add(ReadType(ref r, target, liveMod));
                    return git;
                }

            case ELEMENT_TYPE_FNPTR:
                {
                    var nested = ParseNestedCallSite(ref r, target, liveMod);
                    var fpt = new FunctionPointerType();
                    var tp = typeof(FunctionPointerType);
                    var prop = tp.GetProperty("CallSite") ?? tp.GetProperty("Function");
                    if (prop == null) throw new NotSupportedException("FunctionPointerType 缺少 CallSite/Function 属性。");
                    prop.SetValue(fpt, nested, null);
                    return fpt;
                }

            case ELEMENT_TYPE_VAR:
            case ELEMENT_TYPE_MVAR:
                {
                    ReadCompressedUInt(ref r); // 跳过索引
                    return target.TypeSystem.Object; // 无上下文，退化为 object
                }

            case ELEMENT_TYPE_TYPEDBYREF:
                return target.TypeSystem.Object;

            default:
                return target.TypeSystem.Object;
        }
    }

    static CallSite ParseNestedCallSite(ref BlobReader r, ModuleDefinition target, System.Reflection.Module liveMod)
    {
        byte first = r.ReadU1();
        bool hasThis = (first & 0x20) != 0;
        bool explicitThis = (first & 0x40) != 0;
        byte convLow = (byte)(first & 0x0F);
        bool isGenericSig = (first & 0x10) != 0;
        if (isGenericSig) ReadCompressedUInt(ref r);

        uint paramCount = ReadCompressedUInt(ref r);
        var retType = ReadType(ref r, target, liveMod);
        var cs = new CallSite(retType);
        cs.HasThis = hasThis;
        cs.ExplicitThis = explicitThis;
        cs.CallingConvention = MapCallingConvention(convLow);

        bool afterSentinel = false;
        for (uint i = 0; i < paramCount; i++)
        {
            byte peek = r.PeekU1();
            if (peek == ELEMENT_TYPE_SENTINEL) { r.ReadU1(); afterSentinel = true; }
            var pType = ReadType(ref r, target, liveMod);
            if (afterSentinel) pType = new SentinelType(pType);
            cs.Parameters.Add(new ParameterDefinition(pType));
        }
        return cs;
    }

    // TypeDefOrRefOrSpecEncoded（压缩） -> 元数据 token
    static int DecodeTypeDefOrRefOrSpec(ref BlobReader r, System.Reflection.Module liveMod)
    {
        uint coded = ReadCompressedUInt(ref r);
        int tag = (int)(coded & 0x3);
        uint rid = (coded >> 2);

        int token;
        switch (tag)
        {
            case TAG_TYPEDEF: token = unchecked((int)(0x02000000 | rid)); break; // TypeDef
            case TAG_TYPEREF: token = unchecked((int)(0x01000000 | rid)); break; // TypeRef
            case TAG_TYPESPEC: token = unchecked((int)(0x1B000000 | rid)); break; // TypeSpec
            default: token = 0; break;
        }
        return token;
    }

    // ECMA-335 压缩无符号整数
    static uint ReadCompressedUInt(ref BlobReader r)
    {
        byte b1 = r.ReadU1();
        if ((b1 & 0x80) == 0) return b1;
        if ((b1 & 0xC0) == 0x80)
        {
            byte b2 = r.ReadU1();
            return (uint)(((b1 & 0x3F) << 8) | b2);
        }
        byte b2_ = r.ReadU1();
        byte b3_ = r.ReadU1();
        byte b4_ = r.ReadU1();
        return (uint)(((b1 & 0x1F) << 24) | (b2_ << 16) | (b3_ << 8) | b4_);
    }

    // 简单 blob reader
    internal struct BlobReader
    {
        public readonly byte[] Data;
        int p;
        public BlobReader(byte[] d) { Data = d; p = 0; }
        public byte ReadU1() { return Data[p++]; }
        public byte PeekU1() { return Data[p]; }
    }
}
