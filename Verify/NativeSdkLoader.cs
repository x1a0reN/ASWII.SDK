using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace ASWDEBUG.Verify
{
    internal static class NativeSdkLoader
    {
        private const string ResourceName = "ASWDEBUG.Native.verigate_sdk.dll";
        private static readonly object Sync = new object();
        private static IntPtr _module;

        internal static void EnsureLoaded()
        {
            if (_module != IntPtr.Zero) return;

            lock (Sync)
            {
                if (_module != IntPtr.Zero) return;

                byte[] image = ReadEmbeddedImage();
                string digest = ComputeSha256(image);
                string directory = Path.Combine(
                    Path.Combine(
                        Path.Combine(
                            Path.Combine(
                                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                                "ASWII"),
                            "VeriGate"),
                        "Native"),
                    digest);
                Directory.CreateDirectory(directory);

                string path = Path.Combine(directory, "verigate_sdk.dll");
                EnsureImage(path, image, digest);
                _module = LoadLibrary(path);
                Array.Clear(image, 0, image.Length);
                if (_module == IntPtr.Zero)
                {
                    throw new Win32Exception(
                        Marshal.GetLastWin32Error(),
                        "无法加载内嵌的 VeriGate 客户端组件。");
                }
            }
        }

        private static byte[] ReadEmbeddedImage()
        {
            Assembly assembly = Assembly.GetExecutingAssembly();
            using (Stream stream = assembly.GetManifestResourceStream(ResourceName))
            {
                if (stream == null)
                    throw new InvalidOperationException("ASWDEBUG.dll 中缺少 VeriGate 客户端组件。");

                using (MemoryStream memory = new MemoryStream())
                {
                    byte[] buffer = new byte[81920];
                    int read;
                    while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
                        memory.Write(buffer, 0, read);
                    return memory.ToArray();
                }
            }
        }

        private static void EnsureImage(string path, byte[] expected, string expectedDigest)
        {
            if (File.Exists(path))
            {
                using (FileStream existing = File.OpenRead(path))
                {
                    if (ComputeSha256(existing) != expectedDigest)
                        throw new InvalidDataException("VeriGate 客户端缓存文件校验失败。");
                }
                return;
            }

            string temporary = path + ".tmp." + Process.GetCurrentProcess().Id;
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
    }
}
