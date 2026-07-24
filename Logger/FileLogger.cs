using System;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace ASWDEBUG.Logger 
{
    public static class FileLogger
    {
        static readonly object _lock = new object();
        static readonly Encoding _utf8NoBom = new UTF8Encoding(false);
        static readonly int _pid = ResolveCurrentPid();
        static string _path;
        static long _rotateBytes = 5L * 1024 * 1024; // 5MB
        static StreamWriter _writer;

        public static void Init(string path, bool rotate = true)
        {
            lock (_lock)
            {
                CloseWriter();
                _path = path;
                try
                {
                    string directory = Path.GetDirectoryName(_path);
                    if (!string.IsNullOrEmpty(directory))
                        Directory.CreateDirectory(directory);
                    if (rotate &&
                        File.Exists(_path) &&
                        new FileInfo(_path).Length > _rotateBytes)
                    {
                        Rotate();
                    }

                    EnsureWriter();
                    _writer.WriteLine(
                        "=== Log start " +
                        DateTime.Now.ToString("O") +
                        " ===");
                }
                catch
                {
                    CloseWriter();
                }
            }
        }

        static void Rotate()
        {
            CloseWriter();
            if (string.IsNullOrEmpty(_path) || !File.Exists(_path))
                return;

            string dir = Path.GetDirectoryName(_path);
            string name = Path.GetFileNameWithoutExtension(_path);
            string ext = Path.GetExtension(_path);
            string ts = DateTime.Now.ToString("yyyyMMdd_HHmmss_fff");
            string rotatedPath =
                Path.Combine(dir, name + "_" + ts + ext);
            int suffix = 1;
            while (File.Exists(rotatedPath))
            {
                rotatedPath = Path.Combine(
                    dir,
                    name + "_" + ts + "_" + suffix + ext);
                suffix++;
            }
            File.Move(_path, rotatedPath);
        }

        public static void WriteLine(string line)
        {
            if (string.IsNullOrEmpty(_path)) return;
            lock (_lock)
            {
                try
                {
                    EnsureWriter();
                    if (_writer.BaseStream.Length >= _rotateBytes)
                    {
                        Rotate();
                        EnsureWriter();
                    }
                    _writer.WriteLine(line);
                }
                catch
                {
                    // 日志失败不得把游戏主线程一并带崩；后续写入会重新尝试打开。
                    CloseWriter();
                }
            }
        }

        public static void Log(string tag, string msg)
        {
            WriteLine("[" + DateTime.Now.ToString("HH:mm:ss.fff") + "][pid=" + _pid + "][" + tag + "] " + msg);
        }

        public static void LogException(string msg, string stack)
        {
            Log("EXCEPTION", msg + (string.IsNullOrEmpty(stack) ? "" : ("\n" + stack)));
        }

        static void EnsureWriter()
        {
            if (_writer != null) return;
            var stream = new FileStream(
                _path,
                FileMode.Append,
                FileAccess.Write,
                FileShare.ReadWrite);
            _writer = new StreamWriter(stream, _utf8NoBom);
            _writer.AutoFlush = true;
        }

        static void CloseWriter()
        {
            if (_writer == null) return;
            try { _writer.Close(); } catch { }
            _writer = null;
        }

        static int ResolveCurrentPid()
        {
            try
            {
                using (Process process = Process.GetCurrentProcess())
                {
                    return process.Id;
                }
            }
            catch
            {
                return -1;
            }
        }
    }

}
