// File: StructuredILDump_v2.cs
// Target: .NET Framework 3.5（Unity/Mono 2.x 兼容）

using System;
using System.IO;
using System.Text;
using System.Reflection;
using System.Collections.Generic;
using System.Threading;

public static class StructuredILDump
{
    public static string TARGET_ASSEMBLY_NAME = "Assembly-CSharp";
    public static int WAIT_SECONDS = 120;
    public static int EXTRA_DELAY_MS = 5000;

    static volatile bool _started;

    public static void Init()
    {
        if (_started) return;
        _started = true;
        new Thread(Worker) { IsBackground = true, Name = "StructuredIL-Dumper" }.Start();
    }

    static void Worker()
    {
        string baseDir = SafeBaseDir();
        string tempRoot = Path.Combine(Path.GetTempPath(), "IL_Dump");
        SafeMkDir(tempRoot);

        Assembly target = WaitForAssembly(TARGET_ASSEMBLY_NAME, WAIT_SECONDS);
        if (target == null)
        {
            SafeWrite(tempRoot, "structured_error.txt", "Assembly '" + TARGET_ASSEMBLY_NAME + "' not found.");
            return;
        }

        if (EXTRA_DELAY_MS > 0) Thread.Sleep(EXTRA_DELAY_MS);

        string tag = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        string mvid = "<unknown>";
        try { mvid = target.ManifestModule.ModuleVersionId.ToString(); } catch { }
        string packName = TARGET_ASSEMBLY_NAME + "__" + mvid + "__" + tag;

        string outTemp = Path.Combine(tempRoot, "STRUCTURED_" + packName);
        SafeMkDir(outTemp);

        string outGame = null;
        if (!string.IsNullOrEmpty(baseDir))
        {
            outGame = Path.Combine(Path.Combine(baseDir, "IL_Dump"), "STRUCTURED_" + packName);
            SafeMkDir(outGame);
        }

        var man = new StringBuilder();
        try
        {
            man.AppendLine("AssemblyFullName: " + target.FullName);
            man.AppendLine("Location        : " + SafeLocation(target));
            man.AppendLine("MVID            : " + mvid);
            man.AppendLine("Time            : " + DateTime.Now.ToString("O"));
        }
        catch { }
        SafeWrite(outTemp, "manifest.txt", man.ToString());
        if (outGame != null) SafeWrite(outGame, "manifest.txt", man.ToString());

        int ok = 0, skip = 0, err = 0;
        try
        {
            IList<Type> types = SafeGetTypes(target);
            for (int ti = 0; ti < types.Count; ti++)
            {
                Type t = types[ti];
                if (t == null) continue;

                MethodInfo[] methods = SafeGetMethods(t);
                for (int mi = 0; mi < methods.Length; mi++)
                {
                    MethodInfo m = methods[mi];
                    if (m == null) { skip++; continue; }

                    MethodBody body = null;
                    try { body = m.GetMethodBody(); } catch { body = null; }
                    if (body == null) { skip++; continue; }

                    byte[] il = null;
                    try { il = body.GetILAsByteArray(); } catch { il = null; }
                    if (il == null || il.Length == 0) { skip++; continue; }

                    // LocalSig blob
                    byte[] localSig = null;
                    try
                    {
                        int tok = -1;
                        try { tok = body.LocalSignatureMetadataToken; } catch { tok = -1; }
                        if (tok > 0)
                            try { localSig = target.ManifestModule.ResolveSignature(tok); } catch { localSig = null; }
                    }
                    catch { localSig = null; }

                    string file = "0x" + m.MetadataToken.ToString("X8") + ".bin";
                    try
                    {
                        using (var fs = new FileStream(Path.Combine(outTemp, file), FileMode.Create, FileAccess.Write, FileShare.Read))
                        using (var bw = new BinaryWriter(fs, Encoding.UTF8))
                        {
                            WriteMethodBin_V2(bw, m, body, il, localSig);
                        }
                        if (outGame != null)
                        {
                            try { File.Copy(Path.Combine(outTemp, file), Path.Combine(outGame, file), true); } catch { }
                        }
                        ok++;
                    }
                    catch { err++; }
                }
            }
        }
        catch (Exception ex)
        {
            SafeWrite(outTemp, "__error.txt", ex.ToString());
            if (outGame != null) SafeWrite(outGame, "__error.txt", ex.ToString());
        }

        SafeWrite(outTemp, "__summary.txt", "OK=" + ok + ", SKIP=" + skip + ", ERR=" + err);
        if (outGame != null) SafeWrite(outGame, "__summary.txt", "OK=" + ok + ", SKIP=" + skip + ", ERR=" + err);
    }

    // v2 bin 格式（带魔数与 LocalSig）
    // [u32 ] 'SIL2' (0x324C4953)
    // [i32 ] MetadataToken
    // [i32 ] MaxStack
    // [u8  ] InitLocals (0/1)
    // [i32 ] nLocals
    //   (IsPinned[u8], AQN[len+i32]) * nLocals
    // [i32 ] nEH
    //   (Flags, TryOfs, TryLen, HOfs, HLen, FilterOfs, CatchTypeAQN[len+i32]) * nEH
    // [i32 ] LocalSigLen
    // [u8* ] LocalSig
    // [i32 ] ILSize
    // [u8* ] IL
    static void WriteMethodBin_V2(BinaryWriter bw, MethodInfo m, MethodBody body, byte[] il, byte[] localSig)
    {
        bw.Write((uint)0x324C4953); // 'S''I''L''2'
        bw.Write(m.MetadataToken);
        bw.Write(body.MaxStackSize);
        bw.Write(body.InitLocals ? (byte)1 : (byte)0);

        IList<LocalVariableInfo> locals = new List<LocalVariableInfo>();
        try { locals = body.LocalVariables; } catch { }
        bw.Write(locals != null ? locals.Count : 0);
        if (locals != null)
        {
            for (int i = 0; i < locals.Count; i++)
            {
                LocalVariableInfo lv = locals[i];
                bw.Write(lv.IsPinned ? (byte)1 : (byte)0);
                string tn = null;
                try { tn = (lv.LocalType != null) ? lv.LocalType.AssemblyQualifiedName : null; } catch { }
                WriteStr(bw, tn);
            }
        }

        IList<ExceptionHandlingClause> ehs = new List<ExceptionHandlingClause>();
        try { ehs = body.ExceptionHandlingClauses; } catch { }
        bw.Write(ehs != null ? ehs.Count : 0);
        if (ehs != null)
        {
            for (int i = 0; i < ehs.Count; i++)
            {
                ExceptionHandlingClause c = ehs[i];
                int flags = (int)c.Flags;
                bw.Write(flags);
                bw.Write(c.TryOffset);
                bw.Write(c.TryLength);
                bw.Write(c.HandlerOffset);
                bw.Write(c.HandlerLength);
                int filterOfs = -1;
                try { filterOfs = c.FilterOffset; } catch { filterOfs = -1; }
                bw.Write(filterOfs);
                string catchType = null;
                try { catchType = (c.CatchType != null) ? c.CatchType.AssemblyQualifiedName : null; } catch { }
                WriteStr(bw, catchType);
            }
        }

        if (localSig != null && localSig.Length > 0) { bw.Write(localSig.Length); bw.Write(localSig); }
        else bw.Write(0);

        bw.Write(il.Length);
        bw.Write(il);
    }

    // Helpers
    static Assembly WaitForAssembly(string name, int waitSec)
    {
        for (int i = 0; i < waitSec; i++)
        {
            Assembly a = FindAssembly(name);
            if (a != null) return a;
            Thread.Sleep(1000);
        }
        return null;
    }
    static Assembly FindAssembly(string simpleName)
    {
        Assembly[] asms = AppDomain.CurrentDomain.GetAssemblies();
        for (int i = 0; i < asms.Length; i++)
        {
            try { if (asms[i].GetName().Name == simpleName) return asms[i]; } catch { }
        }
        return null;
    }
    static IList<Type> SafeGetTypes(Assembly asm)
    {
        try { return asm.GetTypes(); }
        catch (ReflectionTypeLoadException ex)
        {
            List<Type> list = new List<Type>();
            if (ex != null && ex.Types != null)
                for (int i = 0; i < ex.Types.Length; i++) if (ex.Types[i] != null) list.Add(ex.Types[i]);
            return list;
        }
        catch { return new List<Type>(); }
    }
    static MethodInfo[] SafeGetMethods(Type t)
    {
        try
        {
            return t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly);
        }
        catch { return new MethodInfo[0]; }
    }
    static void WriteStr(BinaryWriter bw, string s)
    {
        if (string.IsNullOrEmpty(s)) { bw.Write(0); return; }
        byte[] b = Encoding.UTF8.GetBytes(s);
        bw.Write(b.Length);
        bw.Write(b);
    }
    static string SafeBaseDir()
    {
        try { return AppDomain.CurrentDomain.BaseDirectory; }
        catch { return null; }
    }
    static string SafeLocation(Assembly a)
    {
        try { return a.Location; } catch { return null; }
    }
    static void SafeMkDir(string d)
    {
        try { if (!string.IsNullOrEmpty(d) && !Directory.Exists(d)) Directory.CreateDirectory(d); }
        catch { }
    }
    static void SafeWrite(string dir, string name, string content)
    {
        try
        {
            if (string.IsNullOrEmpty(dir)) return;
            SafeMkDir(dir);
            File.WriteAllText(Path.Combine(dir, name), content ?? "", Encoding.UTF8);
        }
        catch { }
    }
}
