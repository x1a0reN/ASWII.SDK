// File: StructuredILDump_v2.cs
// Target: .NET Framework 3.5（Unity/Mono 2.x 兼容）

using System;
using System.IO;
using System.Text;
using System.Reflection;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Threading;
using ASWDEBUG.Logger;
using UnityEngine;

public static class StructuredILDump
{
    public static string TARGET_ASSEMBLY_NAME = "Assembly-CSharp";
    public static int WAIT_SECONDS = 120;
    public static int EXTRA_DELAY_MS = 5000;
    public static bool DUMP_PROTECTED_ASSEMBLY_BATCH;
    public static string[] BATCH_TARGET_ASSEMBLY_NAMES = new string[0];
    public static string[] REPACK_ASSEMBLY_NAMES = new string[0];

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
        string persistentDir = SafePersistentDir();
        string tempRoot = Path.Combine(Path.GetTempPath(), "IL_Dump");
        SafeMkDir(tempRoot);

        Assembly target = WaitForAssembly(TARGET_ASSEMBLY_NAME, WAIT_SECONDS);
        if (target == null)
        {
            SafeWrite(tempRoot, "structured_error.txt", "Assembly '" + TARGET_ASSEMBLY_NAME + "' not found.");
            return;
        }

        if (EXTRA_DELAY_MS > 0) Thread.Sleep(EXTRA_DELAY_MS);

        if (DUMP_PROTECTED_ASSEMBLY_BATCH)
        {
            DumpProtectedAssemblyBatch(persistentDir, tempRoot);
            return;
        }

        string tag = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        string mvid = SafeMvid(target);
        string packName = TARGET_ASSEMBLY_NAME + "__" + mvid + "__" + tag;

        string outTemp = Path.Combine(tempRoot, "STRUCTURED_" + packName);
        SafeMkDir(outTemp);

        string outGame = null;
        if (!string.IsNullOrEmpty(persistentDir))
        {
            outGame = Path.Combine(Path.Combine(persistentDir, "IL_Dump"), "STRUCTURED_" + packName);
            SafeMkDir(outGame);
        }
        else if (!string.IsNullOrEmpty(baseDir))
        {
            outGame = Path.Combine(Path.Combine(baseDir, "IL_Dump"), "STRUCTURED_" + packName);
            SafeMkDir(outGame);
        }

        DumpAssembly(target, outTemp, outGame, mvid);
    }

    static void DumpProtectedAssemblyBatch(string persistentDir, string tempRoot)
    {
        string tag = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        string parent = !string.IsNullOrEmpty(persistentDir)
            ? Path.Combine(persistentDir, "Managed_Dump")
            : tempRoot;
        string batchRoot = Path.Combine(parent, "BATCH_" + tag);
        SafeMkDir(batchRoot);

        var manifest = new StringBuilder();
        manifest.AppendLine("Time=" + DateTime.Now.ToString("O"));
        manifest.AppendLine("TargetCount=" + (BATCH_TARGET_ASSEMBLY_NAMES == null ? 0 : BATCH_TARGET_ASSEMBLY_NAMES.Length));
        manifest.AppendLine("Format=Name|Loaded|MVID|Location|RuntimeRead|RuntimeReadMZ|RuntimeReadSHA256|IL_OK|IL_SKIP|IL_ERR|Repack|Detail");
        SafeWrite(batchRoot, "batch_manifest.txt", manifest.ToString());

        string[] names = BATCH_TARGET_ASSEMBLY_NAMES ?? new string[0];
        var targets = new List<Assembly>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < names.Length; i++)
        {
            string name = names[i];
            if (string.IsNullOrEmpty(name) || !seen.Add(name)) continue;

            Assembly assembly = FindAssembly(name);
            if (assembly == null)
            {
                SafeAppend(batchRoot, "batch_manifest.txt",
                    name + "|False|||||||||not-loaded|Assembly was not present in AppDomain\r\n");
                continue;
            }
            targets.Add(assembly);
        }

        var roots = new Dictionary<Assembly, string>();
        var runtimeTemplates = new Dictionary<Assembly, RuntimeReadResult>();

        // Save every readable runtime image first, so a later heavy IL walk cannot lose easy dumps.
        for (int i = 0; i < targets.Count; i++)
        {
            Assembly assembly = targets[i];
            string name = SafeAssemblyName(assembly);
            string mvid = SafeMvid(assembly);
            string root = Path.Combine(batchRoot, "STRUCTURED_" + SafeFilePart(name) + "__" + mvid);
            SafeMkDir(root);
            roots[assembly] = root;

            RuntimeReadResult read = DumpRuntimeReadableImage(assembly, root);
            runtimeTemplates[assembly] = read;
            FileLogger.Log("DUMP", "[BATCH] runtime-read " + name + " mz=" + read.IsMz + " bytes=" + read.Length + " detail=" + read.Detail);
        }

        for (int i = 0; i < targets.Count; i++)
        {
            Assembly assembly = targets[i];
            string name = SafeAssemblyName(assembly);
            string mvid = SafeMvid(assembly);
            string root = roots[assembly];
            RuntimeReadResult read = runtimeTemplates[assembly];
            bool repackRequested = ShouldRepack(name);
            DumpResult dump = new DumpResult();

            string repackState = "not-requested";
            string detail = read.Detail;
            // A readable MZ image is already a complete decrypted assembly. Walking every
            // framework method and loading every image into Cecil needlessly exhausts the
            // 32-bit address space. Keep the expensive reflection pass only for selected
            // game assemblies, or as a fallback when the runtime image is still opaque.
            if (repackRequested || !read.IsMz)
            {
                dump = DumpAssembly(assembly, root, null, mvid);
            }
            else
            {
                detail = AppendDetail(detail, "structured=skipped-runtime-image");
            }

            if (repackRequested)
            {
                string template = read.IsMz ? read.OutputPath : SafeLocation(assembly);
                string output = Path.Combine(root, SafeFilePart(name) + ".deobf.dll");
                try
                {
                    FileLogger.Log("DUMP", "[BATCH] repack begin " + name + " template=" + template);
                    bool previousVerbose = CecilRepacker.VerboseMethodLogging;
                    try
                    {
                        CecilRepacker.VerboseMethodLogging = false;
                        CecilRepacker.RepackFromLiveIL(assembly, template, output);
                    }
                    finally
                    {
                        CecilRepacker.VerboseMethodLogging = previousVerbose;
                    }
                    repackState = File.Exists(output) ? "ok" : "no-output";
                }
                catch (Exception ex)
                {
                    repackState = "failed";
                    detail = AppendDetail(detail, "repack=" + ex.GetType().Name + ":" + ex.Message);
                    SafeWrite(root, "__repack_error.txt", ex.ToString());
                }
            }

            SafeAppend(batchRoot, "batch_manifest.txt",
                name + "|True|" + mvid + "|" + SafeLocation(assembly) + "|" + read.OutputPath + "|" + read.IsMz + "|" +
                read.Sha256 + "|" + dump.Ok + "|" + dump.Skip + "|" + dump.Error + "|" + repackState + "|" + detail + "\r\n");
            FileLogger.Log("DUMP", "[BATCH] complete " + name + " ok=" + dump.Ok + " skip=" + dump.Skip + " err=" + dump.Error + " repack=" + repackState);

            try
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();
            }
            catch { }
        }

        SafeWrite(batchRoot, "__complete.txt", "Completed=" + DateTime.Now.ToString("O"));
        FileLogger.Log("DUMP", "[BATCH] ALL DONE root=" + batchRoot);
    }

    static DumpResult DumpAssembly(Assembly target, string outPrimary, string outMirror, string mvid)
    {
        var result = new DumpResult();
        var man = new StringBuilder();
        try
        {
            man.AppendLine("AssemblyFullName: " + target.FullName);
            man.AppendLine("Location        : " + SafeLocation(target));
            man.AppendLine("MVID            : " + mvid);
            man.AppendLine("Time            : " + DateTime.Now.ToString("O"));
        }
        catch { }
        SafeWrite(outPrimary, "manifest.txt", man.ToString());
        if (outMirror != null) SafeWrite(outMirror, "manifest.txt", man.ToString());

        try
        {
            IList<Type> types = SafeGetTypes(target);
            for (int ti = 0; ti < types.Count; ti++)
            {
                Type t = types[ti];
                if (t == null) continue;

                MethodBase[] methods = SafeGetMethods(t);
                for (int mi = 0; mi < methods.Length; mi++)
                {
                    MethodBase m = methods[mi];
                    if (m == null) { result.Skip++; continue; }

                    MethodBody body = null;
                    try { body = m.GetMethodBody(); } catch { body = null; }
                    if (body == null) { result.Skip++; continue; }

                    byte[] il = null;
                    try { il = body.GetILAsByteArray(); } catch { il = null; }
                    if (il == null || il.Length == 0) { result.Skip++; continue; }

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
                        string primaryFile = Path.Combine(outPrimary, file);
                        using (var fs = new FileStream(primaryFile, FileMode.Create, FileAccess.Write, FileShare.Read))
                        using (var bw = new BinaryWriter(fs, Encoding.UTF8))
                        {
                            WriteMethodBin_V2(bw, m, body, il, localSig);
                        }
                        if (outMirror != null)
                        {
                            try { File.Copy(primaryFile, Path.Combine(outMirror, file), true); } catch { }
                        }
                        result.Ok++;
                    }
                    catch { result.Error++; }
                }
            }
        }
        catch (Exception ex)
        {
            SafeWrite(outPrimary, "__error.txt", ex.ToString());
            if (outMirror != null) SafeWrite(outMirror, "__error.txt", ex.ToString());
            result.Error++;
        }

        string summary = "OK=" + result.Ok + ", SKIP=" + result.Skip + ", ERR=" + result.Error;
        SafeWrite(outPrimary, "__summary.txt", summary);
        if (outMirror != null) SafeWrite(outMirror, "__summary.txt", summary);
        return result;
    }

    static RuntimeReadResult DumpRuntimeReadableImage(Assembly assembly, string outputRoot)
    {
        var result = new RuntimeReadResult();
        try
        {
            string location = SafeLocation(assembly);
            if (string.IsNullOrEmpty(location) || !File.Exists(location))
            {
                result.Detail = "location-missing";
                return result;
            }

            byte[] bytes = File.ReadAllBytes(location);
            result.Length = bytes == null ? 0 : bytes.Length;
            result.IsMz = bytes != null && bytes.Length >= 2 && bytes[0] == 0x4D && bytes[1] == 0x5A;
            result.Sha256 = ComputeSha256(bytes);
            string suffix = result.IsMz ? ".runtime-read.dll" : ".runtime-read.bin";
            result.OutputPath = Path.Combine(outputRoot, SafeFilePart(SafeAssemblyName(assembly)) + suffix);
            File.WriteAllBytes(result.OutputPath, bytes ?? new byte[0]);
            result.Detail = result.IsMz ? "managed-image" : "encrypted-or-unreadable-image";
        }
        catch (Exception ex)
        {
            result.Detail = ex.GetType().Name + ":" + ex.Message;
        }
        SafeWrite(outputRoot, "__runtime_read.txt",
            "Path=" + result.OutputPath + "\r\nLength=" + result.Length + "\r\nMZ=" + result.IsMz +
            "\r\nSHA256=" + result.Sha256 + "\r\nDetail=" + result.Detail);
        return result;
    }

    static bool ShouldRepack(string assemblyName)
    {
        string[] names = REPACK_ASSEMBLY_NAMES ?? new string[0];
        for (int i = 0; i < names.Length; i++)
            if (string.Equals(names[i], assemblyName, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    static string ComputeSha256(byte[] bytes)
    {
        try
        {
            using (SHA256Managed sha = new SHA256Managed())
            {
                byte[] hash = sha.ComputeHash(bytes ?? new byte[0]);
                var sb = new StringBuilder(hash.Length * 2);
                for (int i = 0; i < hash.Length; i++) sb.Append(hash[i].ToString("X2"));
                return sb.ToString();
            }
        }
        catch { return string.Empty; }
    }

    static string SafeAssemblyName(Assembly assembly)
    {
        try { return assembly.GetName().Name; } catch { return "unknown"; }
    }

    static string SafeMvid(Assembly assembly)
    {
        try { return assembly.ManifestModule.ModuleVersionId.ToString(); } catch { return "unknown"; }
    }

    static string SafeFilePart(string value)
    {
        if (string.IsNullOrEmpty(value)) return "unknown";
        char[] chars = value.ToCharArray();
        char[] invalid = Path.GetInvalidFileNameChars();
        for (int i = 0; i < chars.Length; i++)
            for (int j = 0; j < invalid.Length; j++)
                if (chars[i] == invalid[j]) { chars[i] = '_'; break; }
        return new string(chars);
    }

    static string AppendDetail(string current, string value)
    {
        if (string.IsNullOrEmpty(current)) return value ?? string.Empty;
        if (string.IsNullOrEmpty(value)) return current;
        return current + ";" + value;
    }

    sealed class DumpResult
    {
        public int Ok;
        public int Skip;
        public int Error;
    }

    sealed class RuntimeReadResult
    {
        public string OutputPath = string.Empty;
        public string Sha256 = string.Empty;
        public string Detail = string.Empty;
        public int Length;
        public bool IsMz;
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
    static void WriteMethodBin_V2(BinaryWriter bw, MethodBase m, MethodBody body, byte[] il, byte[] localSig)
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
    static MethodBase[] SafeGetMethods(Type t)
    {
        var methods = new List<MethodBase>();
        var seen = new HashSet<int>();
        const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic |
                                   BindingFlags.Instance | BindingFlags.Static |
                                   BindingFlags.DeclaredOnly;
        try
        {
            MethodInfo[] declaredMethods = t.GetMethods(flags);
            for (int i = 0; i < declaredMethods.Length; i++)
            {
                MethodInfo method = declaredMethods[i];
                if (method != null && seen.Add(method.MetadataToken)) methods.Add(method);
            }
        }
        catch { }
        try
        {
            ConstructorInfo[] constructors = t.GetConstructors(flags);
            for (int i = 0; i < constructors.Length; i++)
            {
                ConstructorInfo constructor = constructors[i];
                if (constructor != null && seen.Add(constructor.MetadataToken)) methods.Add(constructor);
            }
        }
        catch { }
        try
        {
            ConstructorInfo typeInitializer = t.TypeInitializer;
            if (typeInitializer != null && seen.Add(typeInitializer.MetadataToken)) methods.Add(typeInitializer);
        }
        catch { }
        return methods.ToArray();
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
    static string SafePersistentDir()
    {
        try { return Application.persistentDataPath; }
        catch { return null; }
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

    static void SafeAppend(string dir, string name, string content)
    {
        try
        {
            if (string.IsNullOrEmpty(dir)) return;
            SafeMkDir(dir);
            File.AppendAllText(Path.Combine(dir, name), content ?? string.Empty, Encoding.UTF8);
        }
        catch { }
    }
}
