using System;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace ASWDEBUG.Logger 
{
    public static class FileLogger
    {
        static readonly object _lock = new object();
        static string _path;
        static long _rotateBytes = 5L * 1024 * 1024; // 5MB

        public static void Init(string path, bool rotate = true)
        {
            _path = path;
            Directory.CreateDirectory(Path.GetDirectoryName(_path));
            if (rotate && File.Exists(_path) && new FileInfo(_path).Length > _rotateBytes)
                Rotate();
            WriteLine("=== Log start " + DateTime.Now.ToString("O") + " ===");
        }

        static void Rotate()
        {
            string dir = Path.GetDirectoryName(_path);
            string name = Path.GetFileNameWithoutExtension(_path);
            string ext = Path.GetExtension(_path);
            string ts = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            File.Move(_path, Path.Combine(dir, name + "_" + ts + ext));
        }

        public static void WriteLine(string line)
        {
            if (string.IsNullOrEmpty(_path)) return;
            lock (_lock)
            {
                File.AppendAllText(_path, line + Environment.NewLine, Encoding.UTF8);
            }
        }

        public static void Log(string tag, string msg)
        {
            WriteLine("[" + DateTime.Now.ToString("HH:mm:ss.fff") + "][pid=" + CurrentPid() + "][" + tag + "] " + msg);
        }

        public static void LogException(string msg, string stack)
        {
            Log("EXCEPTION", msg + (string.IsNullOrEmpty(stack) ? "" : ("\n" + stack)));
        }

        static int CurrentPid()
        {
            try
            {
                return Process.GetCurrentProcess().Id;
            }
            catch
            {
                return -1;
            }
        }
    }

}
