using System;
using System.ComponentModel;
#if DOORSTOP_BUILD
using System.Diagnostics;
#endif
using System.IO;
#if DOORSTOP_BUILD
using System.Reflection;
#endif
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace ASWDEBUG.Verify
{
    internal static class NativeSdkLoader
    {
#if DOORSTOP_BUILD
        private const string ResourceName =
            "ASWDEBUG.Native.verigate_sdk.dll";
#else
        private const string CurrentDigestFileName = "current.sha256";
        private const string FallbackDigest =
            "36d3db63260912db4372258b866bb43447c5cacff2853bfedb2b256a4bd454cf";
#endif
        private static readonly object Sync = new object();
        private static IntPtr _module;

        internal static void EnsureLoaded()
        {
            if (_module != IntPtr.Zero) return;

            lock (Sync)
            {
                if (_module != IntPtr.Zero) return;

                string path = ResolveNativeImage();
                _module = LoadLibrary(path);
                if (_module == IntPtr.Zero)
                {
                    throw new Win32Exception(
                        Marshal.GetLastWin32Error(),
#if DOORSTOP_BUILD
                        "无法加载内嵌的 VeriGate 客户端组件。");
#else
                        "无法加载登录器提供的 VeriGate 客户端组件。");
#endif
                }
            }
        }

        internal static T GetFunction<T>(string name) where T : class
        {
            EnsureLoaded();

            IntPtr address = GetProcAddress(_module, name);
            if (address == IntPtr.Zero)
                throw new EntryPointNotFoundException(
                    "VeriGate 客户端组件缺少导出函数：" + name);

            T function = Marshal.GetDelegateForFunctionPointer(
                address,
                typeof(T)) as T;
            if (function == null)
                throw new InvalidOperationException(
                    "无法绑定 VeriGate 客户端函数：" + name);
            return function;
        }

        private static string ResolveNativeImage()
        {
#if DOORSTOP_BUILD
            byte[] image = ReadEmbeddedImage();
            try
            {
                string digest = ComputeSha256(image);
                string directory = Path.Combine(
                    Path.Combine(
                        Path.Combine(
                            Path.Combine(
                                Environment.GetFolderPath(
                                    Environment.SpecialFolder.LocalApplicationData),
                                "ASWII"),
                            "VeriGate"),
                        "Native"),
                    digest);
                Directory.CreateDirectory(directory);

                string path = Path.Combine(
                    directory,
                    "verigate_sdk.dll");
                EnsureImage(path, image, digest);
                return path;
            }
            finally
            {
                Array.Clear(image, 0, image.Length);
            }
#else
            string root = Path.Combine(
                Path.Combine(
                    Environment.GetFolderPath(
                        Environment.SpecialFolder.LocalApplicationData),
                    "x1a0reN.Launcher"),
                "Native");
            string digestPath = Path.Combine(root, CurrentDigestFileName);
            string digest = FallbackDigest;
            if (File.Exists(digestPath))
            {
                digest = File.ReadAllText(digestPath).Trim().ToLowerInvariant();
                if (!IsSha256(digest))
                    throw new InvalidDataException(
                        "登录器的 VeriGate 客户端版本标记无效。");
            }

            string path = Path.Combine(
                Path.Combine(root, digest),
                "verigate_sdk.dll");
            if (!File.Exists(path))
                throw new FileNotFoundException(
                    "登录器尚未释放 VeriGate 客户端组件。",
                    path);

            using (FileStream stream = File.OpenRead(path))
            {
                if (!string.Equals(
                    ComputeSha256(stream),
                    digest,
                    StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException(
                        "登录器提供的 VeriGate 客户端组件校验失败。");
            }
            return path;
#endif
        }

#if !DOORSTOP_BUILD
        private static bool IsSha256(string value)
        {
            if (string.IsNullOrEmpty(value) || value.Length != 64)
                return false;
            for (int i = 0; i < value.Length; i++)
            {
                char current = value[i];
                if ((current < '0' || current > '9') &&
                    (current < 'a' || current > 'f'))
                    return false;
            }
            return true;
        }
#else
        private static byte[] ReadEmbeddedImage()
        {
            Assembly assembly = Assembly.GetExecutingAssembly();
            using (Stream stream =
                assembly.GetManifestResourceStream(ResourceName))
            {
                if (stream == null)
                {
                    throw new InvalidOperationException(
                        "ASWDEBUG.dll 中缺少 VeriGate 客户端组件。");
                }

                using (MemoryStream memory = new MemoryStream())
                {
                    byte[] buffer = new byte[81920];
                    int read;
                    while ((read = stream.Read(
                        buffer,
                        0,
                        buffer.Length)) > 0)
                    {
                        memory.Write(buffer, 0, read);
                    }
                    return memory.ToArray();
                }
            }
        }

        private static void EnsureImage(
            string path,
            byte[] expected,
            string expectedDigest)
        {
            if (File.Exists(path))
            {
                using (FileStream existing = File.OpenRead(path))
                {
                    if (ComputeSha256(existing) != expectedDigest)
                    {
                        throw new InvalidDataException(
                            "VeriGate 客户端缓存文件校验失败。");
                    }
                }
                return;
            }

            string temporary =
                path + ".tmp." + Process.GetCurrentProcess().Id;
            using (FileStream output = new FileStream(
                temporary,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None))
            {
                output.Write(expected, 0, expected.Length);
                output.Flush();
            }

            try
            {
                File.Move(temporary, path);
            }
            catch (IOException)
            {
                if (!File.Exists(path)) throw;
                try { File.Delete(temporary); } catch { }
            }
        }

        private static string ComputeSha256(byte[] value)
        {
            using (SHA256 algorithm = SHA256.Create())
                return ToHex(algorithm.ComputeHash(value));
        }
#endif

        private static string ComputeSha256(Stream value)
        {
            using (SHA256 algorithm = SHA256.Create())
                return ToHex(algorithm.ComputeHash(value));
        }

        private static string ToHex(byte[] value)
        {
            return BitConverter.ToString(value).Replace("-", string.Empty).ToLowerInvariant();
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr LoadLibrary(string fileName);

        [DllImport("kernel32.dll", CharSet = CharSet.Ansi, SetLastError = true)]
        private static extern IntPtr GetProcAddress(IntPtr module, string name);
    }
}
