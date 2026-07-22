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
        static FileStream _stream;
        static StreamWriter _writer;
        static long _nextFlushTicks;
        static int _pendingLines;
        static bool _exitHooked;
        const int FlushIntervalMs = 250;
        const int FlushLineThreshold = 32;

        public static void Init(string path, bool rotate = true)
        {
            lock (_lock)
            {
                CloseWriter();
                _path = path;
                Directory.CreateDirectory(Path.GetDirectoryName(_path));
                if (rotate && File.Exists(_path) && new FileInfo(_path).Length > _rotateBytes)
                    Rotate();
                OpenWriter();
                if (!_exitHooked)
                {
                    AppDomain.CurrentDomain.ProcessExit += OnProcessExit;
                    _exitHooked = true;
                }
            }
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
                try
                {
                    if (_writer == null) OpenWriter();
                    _writer.WriteLine(line);
                    _pendingLines++;
                    long now = DateTime.UtcNow.Ticks;
                    if (_pendingLines >= FlushLineThreshold || now >= _nextFlushTicks)
                    {
                        _writer.Flush();
                        _pendingLines = 0;
                        _nextFlushTicks = now + FlushIntervalMs * TimeSpan.TicksPerMillisecond;
                    }
                }
                catch
                {
                    CloseWriter();
                    try { File.AppendAllText(_path, line + Environment.NewLine, Encoding.UTF8); }
                    catch { }
                }
            }
        }

        static void OpenWriter()
        {
            _stream = new FileStream(_path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
            _writer = new StreamWriter(_stream, Encoding.UTF8);
            _writer.AutoFlush = false;
            _pendingLines = 0;
            _nextFlushTicks = DateTime.UtcNow.Ticks + FlushIntervalMs * TimeSpan.TicksPerMillisecond;
        }

        static void CloseWriter()
        {
            try { if (_writer != null) _writer.Flush(); }
            catch { }
            try { if (_writer != null) _writer.Close(); }
            catch { }
            try { if (_stream != null) _stream.Close(); }
            catch { }
            _writer = null;
            _stream = null;
            _pendingLines = 0;
            _nextFlushTicks = 0L;
        }

        static void OnProcessExit(object sender, EventArgs args)
        {
            lock (_lock)
            {
                CloseWriter();
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
