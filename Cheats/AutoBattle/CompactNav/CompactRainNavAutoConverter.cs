using ASWDEBUG.Logger;
using System;
using System.Diagnostics;
using System.IO;
using UnityEngine;

namespace ASWDEBUG.Cheats.AutoBattle.CompactNav
{
    internal enum CompactRainAutoConversionState
    {
        Idle,
        WaitingSources,
        Running,
        Ready,
        Failed
    }

    internal struct CompactRainAutoConversionSnapshot
    {
        public CompactRainAutoConversionState State;
        public string MapName;
        public string Detail;
        public string OutputFileName;
        public string OutputPath;
        public int AttemptCount;
        public long OutputBytes;

        public bool Ready
        {
            get { return State == CompactRainAutoConversionState.Ready && OutputBytes > 0L; }
        }
    }

    internal static class CompactRainNavAutoConverter
    {
        private const string SupportedMapName = "level33";
        private const int MaximumAttempts = 3;

        private static CompactRainAutoConversionState _state;
        private static string _mapName = string.Empty;
        private static string _detail = "idle";
        private static string _sourceKey = string.Empty;
        private static string _completedSourceKey = string.Empty;
        private static string _runningNavPath = string.Empty;
        private static string _runningMetaPath = string.Empty;
        private static string _outputPath = string.Empty;
        private static string _converterPath = string.Empty;
        private static int _attemptCount;
        private static float _nextAttemptAt;
        private static long _outputBytes;
        private static Process _process;

        internal static void Tick(string mapName, bool highDetail)
        {
            PollProcess();

            string normalized = (mapName ?? string.Empty).Trim().ToLowerInvariant();
            if (!highDetail || !string.Equals(normalized, SupportedMapName,
                StringComparison.OrdinalIgnoreCase)) return;
            _mapName = normalized;

            string navPath;
            string metaPath;
            string missingDetail;
            if (!TryResolveSourcePair(normalized, out navPath, out metaPath, out missingDetail))
            {
                if (_process == null) SetWaiting(missingDetail);
                return;
            }

            string converterPath = GetConverterPath();
            string outputPath = GetOutputPath(navPath);
            string sourceKey = BuildSourceKey(navPath, metaPath, converterPath);
            if (!string.Equals(_sourceKey, sourceKey, StringComparison.Ordinal))
            {
                _sourceKey = sourceKey;
                _attemptCount = 0;
                _nextAttemptAt = 0f;
                if (_process == null) _state = CompactRainAutoConversionState.WaitingSources;
            }

            if (_process != null) return;
            if (_state == CompactRainAutoConversionState.Ready &&
                string.Equals(_completedSourceKey, sourceKey, StringComparison.Ordinal) &&
                File.Exists(outputPath)) return;
            if (OutputIsFresh(outputPath, navPath, metaPath, converterPath))
            {
                MarkReady(sourceKey, outputPath, "fresh_output");
                return;
            }
            if (_state == CompactRainAutoConversionState.Failed &&
                _attemptCount >= MaximumAttempts) return;
            if (Time.realtimeSinceStartup < _nextAttemptAt) return;
            StartConverter(sourceKey, navPath, metaPath, outputPath, converterPath);
        }

        internal static CompactRainAutoConversionSnapshot GetSnapshot()
        {
            return new CompactRainAutoConversionSnapshot
            {
                State = _state,
                MapName = _mapName,
                Detail = _detail,
                OutputFileName = string.IsNullOrEmpty(_outputPath) ? "level33.aswnav" :
                    Path.GetFileName(_outputPath),
                OutputPath = _outputPath,
                AttemptCount = _attemptCount,
                OutputBytes = _outputBytes
            };
        }

        private static void StartConverter(string sourceKey, string navPath, string metaPath,
            string outputPath, string converterPath)
        {
            _attemptCount++;
            if (!File.Exists(converterPath))
            {
                Fail("converter_missing path=" + converterPath);
                return;
            }

            try
            {
                // Keep the conversion allocations outside the 32-bit Unity/Mono address space.
                ProcessStartInfo start = new ProcessStartInfo();
                start.FileName = converterPath;
                start.Arguments = Quote(navPath) + " " + Quote(metaPath) + " " + Quote(outputPath);
                start.WorkingDirectory = Path.GetDirectoryName(converterPath);
                start.UseShellExecute = false;
                start.CreateNoWindow = true;
                start.WindowStyle = ProcessWindowStyle.Hidden;
                start.RedirectStandardOutput = true;
                start.RedirectStandardError = true;
                Process process = new Process();
                process.StartInfo = start;
                if (!process.Start())
                {
                    process.Close();
                    Fail("converter_start_returned_false");
                    return;
                }

                _process = process;
                _sourceKey = sourceKey;
                _runningNavPath = navPath;
                _runningMetaPath = metaPath;
                _outputPath = outputPath;
                _converterPath = converterPath;
                _outputBytes = 0L;
                _state = CompactRainAutoConversionState.Running;
                _detail = "running pid=" + process.Id + " attempt=" + _attemptCount + "/" +
                    MaximumAttempts;
                FileLogger.Log("AUTO-BATTLE][ASWNAV", "converter_started map=" + _mapName +
                    " pid=" + process.Id + " attempt=" + _attemptCount +
                    " nav=" + navPath + " meta=" + metaPath + " output=" + outputPath);
            }
            catch (Exception ex)
            {
                Fail("converter_start_ex=" + ex.GetType().Name + ":" + Safe(ex.Message));
            }
        }

        private static void PollProcess()
        {
            if (_process == null) return;
            bool exited;
            try { exited = _process.HasExited; }
            catch (Exception ex)
            {
                CloseProcess();
                Fail("converter_poll_ex=" + ex.GetType().Name + ":" + Safe(ex.Message));
                return;
            }
            if (!exited) return;

            int exitCode;
            try { exitCode = _process.ExitCode; }
            catch { exitCode = -1; }
            string stdout;
            string stderr;
            try { stdout = _process.StandardOutput.ReadToEnd(); }
            catch { stdout = string.Empty; }
            try { stderr = _process.StandardError.ReadToEnd(); }
            catch { stderr = string.Empty; }
            CloseProcess();
            if (exitCode == 0 && OutputIsFresh(_outputPath, _runningNavPath,
                _runningMetaPath, _converterPath))
            {
                MarkReady(_sourceKey, _outputPath, "converted exit=0");
                FileLogger.Log("AUTO-BATTLE][ASWNAV", "converter_completed map=" + _mapName +
                    " bytes=" + _outputBytes + " output=" + _outputPath +
                    " result=" + SafeOutput(stdout));
                return;
            }
            Fail("converter_exit=" + exitCode + " error=" + Safe(stderr) +
                " output=" + _outputPath);
        }

        private static void MarkReady(string sourceKey, string outputPath, string detail)
        {
            FileInfo output = new FileInfo(outputPath);
            _completedSourceKey = sourceKey;
            _outputPath = outputPath;
            _outputBytes = output.Exists ? output.Length : 0L;
            _state = CompactRainAutoConversionState.Ready;
            _detail = detail + " bytes=" + _outputBytes;
        }

        private static void Fail(string detail)
        {
            _state = CompactRainAutoConversionState.Failed;
            _detail = detail;
            _outputBytes = 0L;
            _nextAttemptAt = Time.realtimeSinceStartup + (1 << Math.Min(_attemptCount + 1, 4));
            FileLogger.Log("AUTO-BATTLE][ASWNAV", "converter_failed map=" + _mapName + " " + detail);
        }

        private static void SetWaiting(string detail)
        {
            if (_state == CompactRainAutoConversionState.WaitingSources &&
                string.Equals(_detail, detail, StringComparison.Ordinal)) return;
            _state = CompactRainAutoConversionState.WaitingSources;
            _detail = detail;
            _outputBytes = 0L;
            FileLogger.Log("AUTO-BATTLE][ASWNAV", "converter_waiting map=" + _mapName + " " + detail);
        }

        private static void CloseProcess()
        {
            Process process = _process;
            _process = null;
            if (process == null) return;
            try { process.Close(); }
            catch { }
        }

        private static bool TryResolveSourcePair(string mapName, out string navPath,
            out string metaPath, out string detail)
        {
            navPath = RuntimeRainNavDiskCache.GetCachePath(mapName, true);
            metaPath = RuntimeRainNavDerivedDiskCache.GetCachePath(mapName, true);
            if (File.Exists(navPath) && File.Exists(metaPath))
            {
                detail = "max_sources_ready";
                return true;
            }

            string legacyNav = RuntimeRainNavDiskCache.GetCachePath(mapName);
            string legacyMeta = RuntimeRainNavDerivedDiskCache.GetCachePath(mapName);
            if (File.Exists(legacyNav) && File.Exists(legacyMeta))
            {
                navPath = legacyNav;
                metaPath = legacyMeta;
                detail = "legacy_sources_ready";
                return true;
            }

            detail = "waiting_sources nav=" + (File.Exists(navPath) ? "1" : "0") +
                " meta=" + (File.Exists(metaPath) ? "1" : "0");
            return false;
        }

        private static bool OutputIsFresh(string outputPath, string navPath, string metaPath,
            string converterPath)
        {
            try
            {
                FileInfo output = new FileInfo(outputPath);
                FileInfo nav = new FileInfo(navPath);
                FileInfo meta = new FileInfo(metaPath);
                FileInfo converter = new FileInfo(converterPath);
                if (!output.Exists || output.Length <= 0L || !nav.Exists || !meta.Exists ||
                    !converter.Exists) return false;
                DateTime required = nav.LastWriteTimeUtc > meta.LastWriteTimeUtc ?
                    nav.LastWriteTimeUtc : meta.LastWriteTimeUtc;
                if (converter.LastWriteTimeUtc > required) required = converter.LastWriteTimeUtc;
                return output.LastWriteTimeUtc >= required;
            }
            catch { return false; }
        }

        private static string BuildSourceKey(string navPath, string metaPath, string converterPath)
        {
            FileInfo nav = new FileInfo(navPath);
            FileInfo meta = new FileInfo(metaPath);
            FileInfo converter = new FileInfo(converterPath);
            return nav.FullName.ToLowerInvariant() + "|" + nav.Length + "|" +
                nav.LastWriteTimeUtc.Ticks + "|" + meta.FullName.ToLowerInvariant() + "|" +
                meta.Length + "|" + meta.LastWriteTimeUtc.Ticks + "|" +
                (converter.Exists ? converter.LastWriteTimeUtc.Ticks : 0L);
        }

        private static string GetConverterPath()
        {
            string dataPath = Application.dataPath;
            DirectoryInfo parent = string.IsNullOrEmpty(dataPath) ? null : Directory.GetParent(dataPath);
            string gameRoot = parent == null ? dataPath : parent.FullName;
            return Path.Combine(Path.Combine(gameRoot, "ASWDEBUG.Tools"),
                "CompactNavConverter.exe");
        }

        private static string GetOutputPath(string navPath)
        {
            return Path.Combine(Path.GetDirectoryName(navPath), "level33.aswnav");
        }

        private static string Quote(string value)
        {
            if (value == null || value.IndexOf('"') >= 0)
                throw new ArgumentException("invalid_process_argument");
            return "\"" + value + "\"";
        }

        private static string Safe(string value)
        {
            if (string.IsNullOrEmpty(value)) return "-";
            string safe = value.Replace('\r', ' ').Replace('\n', ' ').Trim();
            return safe.Length <= 140 ? safe : safe.Substring(0, 140);
        }

        private static string SafeOutput(string value)
        {
            if (string.IsNullOrEmpty(value)) return "-";
            string safe = value.Replace('\r', ' ').Replace('\n', ' ').Trim();
            return safe.Length <= 640 ? safe : safe.Substring(safe.Length - 640, 640);
        }
    }
}
