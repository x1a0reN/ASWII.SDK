using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using ASWDEBUG.Logger;

namespace ASWDEBUG.Verify
{
    internal sealed class VeriGateClientSnapshot
    {
        internal string InstanceID;
        internal string ClientKind;
        internal uint ProcessID;
        internal string ClientVersion;
        internal string RuntimeVersion;
        internal string ModuleVersion;
        internal string GameVersion;
        internal string OSVersion;
        internal string MachineName;
        internal string PlayerID;
        internal string PlayerName;
        internal string ServerName;
        internal string SceneName;
        internal DateTime StartedAt;
        internal IDictionary<string, string> Metadata;
    }

    internal sealed class VeriGateRemoteCommand
    {
        internal string CommandID;
        internal string CommandType;
        internal string Target;
        internal string Payload;
        internal DateTime ExpiresAt;
    }

    internal sealed class VeriGateCommandResult
    {
        internal string CommandID;
        internal string Status;
        internal string Result;
        internal string ErrorCode;
    }

    internal sealed class VeriGateAuthorization
    {
        internal bool Allowed;
        internal string ReasonCode;
        internal string DeviceID;
        internal string SessionID;
        internal DateTime SessionExpiresAt;
        internal bool Terminate;
        internal string TerminationReason;
        internal VeriGateRemoteCommand[] Commands;
    }

    internal sealed class VeriGateClient : IDisposable
    {
        private const int TransientAttemptCount = 3;
        private static readonly Mutex ProcessMutex =
            new Mutex(false, VeriGateOptions.ProcessMutexName);
        private readonly object _sync = new object();
        private readonly string _instanceID;
        private readonly DateTime _startedAt = DateTime.UtcNow;
        private IntPtr _context;

        private VeriGateClient(IntPtr context, string instanceID)
        {
            _context = context;
            _instanceID = instanceID;
        }

        internal static VeriGateClient Open(string directCard)
        {
            if (string.IsNullOrEmpty(directCard) || directCard.Trim().Length != 78)
                throw new VeriGateException(1);

            using (EnterProcessLock())
            {
                NativeSdkLoader.EnsureLoaded();
                string instanceID = Guid.NewGuid().ToString("D");
                string sessionScope = "aswdebug-" +
                    System.Diagnostics.Process.GetCurrentProcess().Id + "-" +
                    instanceID.Replace("-", string.Empty);
                string config = "{" +
                    "\"origin\":\"" + JsonEscape(VeriGateOptions.Origin) + "\"," +
                    "\"tenant_id\":\"" + VeriGateOptions.TenantId + "\"," +
                    "\"application_id\":\"" + VeriGateOptions.ApplicationId + "\"," +
                    "\"environment_id\":\"" + VeriGateOptions.EnvironmentId + "\"," +
                    "\"storage_root\":\"" +
                    JsonEscape(VeriGateCredentialStore.StorageRoot) + "\"," +
                    "\"session_scope\":\"" + sessionScope + "\"," +
                    "\"client_name\":\"" + VeriGateOptions.ClientName + "\"," +
                    "\"timeout_seconds\":20}";

                using (Utf8Slice configSlice = new Utf8Slice(config, false))
                using (Utf8Slice cardSlice = new Utf8Slice(directCard.Trim(), true))
                {
                    IntPtr context;
                    uint result = NativeMethods.vg_sdk_windows_client_new(
                        configSlice.Value,
                        cardSlice.Value,
                        out context);
                    ThrowIfFailed(result);
                    if (context == IntPtr.Zero) throw new VeriGateException(7);
                    return new VeriGateClient(context, instanceID);
                }
            }
        }

        internal VeriGateAuthorization Authorize(VeriGateClientSnapshot snapshot)
        {
            lock (_sync)
            using (EnterProcessLock())
            {
                EnsureNotDisposed();
                string activation = CallJsonWithRetry(
                    "activate",
                    NativeMethods.vg_sdk_client_activate);
                string deviceID = ExtractString(activation, "device_id");
                if (string.IsNullOrEmpty(deviceID))
                    throw ProtocolFailure("activate.device_id");

                // Every injected process uses a fresh session scope, so it cannot
                // contain a refresh token. Avoid the guaranteed failed refresh.
                string session = CallJsonWithRetry(
                    "create_session",
                    NativeMethods.vg_sdk_client_create_session);

                VeriGateAuthorization authorization = VerifyCore(snapshot, null);
                authorization.DeviceID = deviceID;
                authorization.SessionID = ExtractString(session, "session_id");
                authorization.SessionExpiresAt = ParseDate(
                    ExtractString(session, "session_expires_at"),
                    "create_session.session_expires_at");
                if (string.IsNullOrEmpty(authorization.SessionID))
                    throw ProtocolFailure("create_session.session_id");
                return authorization;
            }
        }

        internal VeriGateAuthorization Heartbeat(
            VeriGateClientSnapshot snapshot,
            IList<VeriGateCommandResult> commandResults)
        {
            lock (_sync)
            using (EnterProcessLock())
            {
                EnsureNotDisposed();
                try
                {
                    return VerifyCore(snapshot, commandResults);
                }
                catch (VeriGateException error)
                {
                    if (!error.IsAuthenticationFailure) throw;
                    CallJsonWithRetry(
                        "heartbeat.refresh",
                        NativeMethods.vg_sdk_client_refresh);
                    return VerifyCore(snapshot, commandResults);
                }
            }
        }

        internal void Logout()
        {
            lock (_sync)
            using (EnterProcessLock())
            {
                EnsureNotDisposed();
                ThrowIfFailed(NativeMethods.vg_sdk_client_logout(_context));
            }
        }

        public void Dispose()
        {
            lock (_sync)
            {
                if (_context == IntPtr.Zero) return;
                NativeMethods.vg_sdk_client_free(_context);
                _context = IntPtr.Zero;
            }
        }

        private static IDisposable EnterProcessLock()
        {
            bool acquired = false;
            try
            {
                acquired = ProcessMutex.WaitOne(30000, false);
            }
            catch (AbandonedMutexException)
            {
                acquired = true;
            }
            if (!acquired)
                throw new InvalidOperationException("等待同机网络验证会话超时。");
            return new ProcessLock();
        }

        private VeriGateAuthorization VerifyCore(
            VeriGateClientSnapshot snapshot,
            IList<VeriGateCommandResult> commandResults)
        {
            string request = BuildVerifyRequest(snapshot, commandResults);
            string response = VerifyWithRetry(request);

            string decision = FirstObject(ExtractContainer(response, "decisions", '[', ']'));
            bool allowed;
            if (string.IsNullOrEmpty(decision) ||
                !TryExtractBoolean(decision, "allowed", out allowed))
                throw ProtocolFailure("verify.decisions");
            string reasonCode = ExtractString(decision, "reason_code");
            string expiresAt = ExtractString(decision, "expires_at");
            if (string.IsNullOrEmpty(reasonCode) || string.IsNullOrEmpty(expiresAt))
                throw ProtocolFailure("verify.decision_fields");
            if (!allowed) throw new VeriGateException(3);

            string control = ExtractContainer(response, "client_control", '{', '}');
            bool terminate = false;
            if (!string.IsNullOrEmpty(control))
                TryExtractBoolean(control, "terminate", out terminate);

            return new VeriGateAuthorization
            {
                Allowed = true,
                ReasonCode = reasonCode,
                SessionExpiresAt = ParseDate(expiresAt, "verify.expires_at"),
                Terminate = terminate,
                TerminationReason = string.IsNullOrEmpty(control)
                    ? null
                    : ExtractString(control, "reason_code"),
                Commands = ParseCommands(response)
            };
        }

        private string BuildVerifyRequest(
            VeriGateClientSnapshot snapshot,
            IList<VeriGateCommandResult> commandResults)
        {
            if (snapshot == null) snapshot = new VeriGateClientSnapshot();
            snapshot.InstanceID = _instanceID;
            snapshot.StartedAt = _startedAt;
            snapshot.ClientKind = "aswdebug";
            snapshot.ProcessID = (uint)System.Diagnostics.Process.GetCurrentProcess().Id;
            snapshot.ClientVersion = VeriGateOptions.ClientVersion;

            StringBuilder json = new StringBuilder(1024);
            json.Append("{\"client_version\":\"")
                .Append(JsonEscape(VeriGateOptions.ClientVersion))
                .Append("\",\"checks\":[{\"capability\":\"")
                .Append(JsonEscape(VeriGateOptions.Capability))
                .Append("\",\"resource\":\"")
                .Append(JsonEscape(VeriGateOptions.Resource))
                .Append("\"}],\"client_instance\":{")
                .Append("\"instance_id\":\"").Append(snapshot.InstanceID)
                .Append("\",\"client_kind\":\"aswdebug\",\"process_id\":")
                .Append(snapshot.ProcessID)
                .Append(",\"client_version\":\"")
                .Append(JsonEscape(snapshot.ClientVersion))
                .Append("\",\"started_at\":\"")
                .Append(snapshot.StartedAt.ToUniversalTime().ToString(
                    "yyyy-MM-dd'T'HH:mm:ss.fff'Z'",
                    CultureInfo.InvariantCulture))
                .Append("\"");
            AppendOptional(json, "runtime_version", snapshot.RuntimeVersion);
            AppendOptional(json, "module_version", snapshot.ModuleVersion);
            AppendOptional(json, "game_version", snapshot.GameVersion);
            AppendOptional(json, "os_version", snapshot.OSVersion);
            AppendOptional(json, "machine_name", snapshot.MachineName);
            AppendOptional(json, "player_id", snapshot.PlayerID);
            AppendOptional(json, "player_name", snapshot.PlayerName);
            AppendOptional(json, "server_name", snapshot.ServerName);
            AppendOptional(json, "scene_name", snapshot.SceneName);
            if (snapshot.Metadata != null && snapshot.Metadata.Count > 0)
            {
                json.Append(",\"metadata\":{");
                bool first = true;
                foreach (KeyValuePair<string, string> item in snapshot.Metadata)
                {
                    if (!first) json.Append(',');
                    first = false;
                    json.Append('"').Append(JsonEscape(item.Key)).Append("\":\"")
                        .Append(JsonEscape(item.Value)).Append('"');
                }
                json.Append('}');
            }
            if (commandResults != null && commandResults.Count > 0)
            {
                json.Append(",\"command_results\":[");
                for (int i = 0; i < commandResults.Count; i++)
                {
                    VeriGateCommandResult result = commandResults[i];
                    if (i > 0) json.Append(',');
                    json.Append("{\"command_id\":\"")
                        .Append(JsonEscape(result.CommandID))
                        .Append("\",\"status\":\"")
                        .Append(JsonEscape(result.Status))
                        .Append('"');
                    if (!string.IsNullOrEmpty(result.Result))
                    {
                        json.Append(",\"result_base64\":\"")
                            .Append(Convert.ToBase64String(
                                Encoding.UTF8.GetBytes(result.Result)))
                            .Append('"');
                    }
                    if (!string.IsNullOrEmpty(result.ErrorCode))
                    {
                        json.Append(",\"error_code\":\"")
                            .Append(JsonEscape(result.ErrorCode))
                            .Append('"');
                    }
                    json.Append('}');
                }
                json.Append(']');
            }
            json.Append("}}");
            return json.ToString();
        }

        private static void AppendOptional(
            StringBuilder json,
            string key,
            string value)
        {
            if (string.IsNullOrEmpty(value)) return;
            json.Append(",\"").Append(key).Append("\":\"")
                .Append(JsonEscape(value)).Append('"');
        }

        private static VeriGateRemoteCommand[] ParseCommands(string json)
        {
            string array = ExtractContainer(json, "client_commands", '[', ']');
            if (string.IsNullOrEmpty(array)) return new VeriGateRemoteCommand[0];

            List<string> objects = SplitObjects(array);
            List<VeriGateRemoteCommand> commands =
                new List<VeriGateRemoteCommand>(objects.Count);
            for (int i = 0; i < objects.Count; i++)
            {
                string item = objects[i];
                string commandID = ExtractString(item, "command_id");
                string commandType = ExtractString(item, "command_type");
                string target = ExtractString(item, "target");
                string payloadBase64 = ExtractString(item, "payload_base64");
                string expiresAt = ExtractString(item, "expires_at");
                if (string.IsNullOrEmpty(commandID) ||
                    string.IsNullOrEmpty(commandType) ||
                    string.IsNullOrEmpty(target) ||
                    string.IsNullOrEmpty(expiresAt))
                    throw ProtocolFailure("verify.client_command_fields");

                string payload;
                try
                {
                    payload = string.IsNullOrEmpty(payloadBase64)
                        ? string.Empty
                        : Encoding.UTF8.GetString(Convert.FromBase64String(payloadBase64));
                }
                catch
                {
                    throw ProtocolFailure("verify.client_command_payload");
                }
                commands.Add(new VeriGateRemoteCommand
                {
                    CommandID = commandID,
                    CommandType = commandType,
                    Target = target,
                    Payload = payload,
                    ExpiresAt = ParseDate(
                        expiresAt,
                        "verify.client_command_expires_at")
                });
            }
            return commands.ToArray();
        }

        private string CallJsonWithRetry(
            string stage,
            NativeJsonOperation operation)
        {
            for (int attempt = 1; attempt <= TransientAttemptCount; attempt++)
            {
                NativeBuffer output;
                uint result = operation(_context, out output);
                if (result == 0)
                {
                    return ReadAndFree(output, stage);
                }

                if (!IsTransientError(result) ||
                    attempt == TransientAttemptCount)
                {
                    FileLogger.Log(
                        "AUTH",
                        "SDK stage failed: " + stage + " code=" + result +
                        " attempt=" + attempt);
                    throw new VeriGateException(result);
                }

                LogTransientRetry(stage, attempt, result);
                SleepBeforeRetry(attempt);
            }
            throw ProtocolFailure(stage + ".retry_exhausted");
        }

        private string VerifyWithRetry(string request)
        {
            for (int attempt = 1; attempt <= TransientAttemptCount; attempt++)
            {
                using (Utf8Slice requestSlice = new Utf8Slice(request, false))
                {
                    NativeBuffer output;
                    uint result = NativeMethods.vg_sdk_client_verify(
                        _context,
                        requestSlice.Value,
                        out output);
                    if (result == 0)
                    {
                        return ReadAndFree(output, "verify");
                    }

                    if (!IsTransientError(result) ||
                        attempt == TransientAttemptCount)
                    {
                        FileLogger.Log(
                            "AUTH",
                            "SDK stage failed: verify code=" + result +
                            " attempt=" + attempt);
                        throw new VeriGateException(result);
                    }
                    LogTransientRetry("verify", attempt, result);
                }
                SleepBeforeRetry(attempt);
            }
            throw ProtocolFailure("verify.retry_exhausted");
        }

        private static string ReadAndFree(
            NativeBuffer output,
            string stage)
        {
            try
            {
                ulong length = output.Length.ToUInt64();
                if (output.Data == IntPtr.Zero || length == 0 || length > 1024 * 1024)
                    throw ProtocolFailure(stage + ".buffer");
                byte[] bytes = new byte[(int)length];
                Marshal.Copy(output.Data, bytes, 0, bytes.Length);
                FileLogger.Log(
                    "AUTH",
                    "SDK stage succeeded: " + stage + " response_bytes=" +
                    bytes.Length);
                return Encoding.UTF8.GetString(bytes);
            }
            finally
            {
                NativeMethods.vg_sdk_buffer_free(output);
            }
        }

        private static string ExtractString(string json, string key)
        {
            int index = FindValue(json, key);
            if (index < 0 || index >= json.Length || json[index] != '"') return null;
            StringBuilder value = new StringBuilder();
            bool escaped = false;
            for (int i = index + 1; i < json.Length; i++)
            {
                char current = json[i];
                if (escaped)
                {
                    switch (current)
                    {
                        case '"': value.Append('"'); break;
                        case '\\': value.Append('\\'); break;
                        case '/': value.Append('/'); break;
                        case 'b': value.Append('\b'); break;
                        case 'f': value.Append('\f'); break;
                        case 'n': value.Append('\n'); break;
                        case 'r': value.Append('\r'); break;
                        case 't': value.Append('\t'); break;
                        case 'u':
                            if (i + 4 >= json.Length) return null;
                            int code;
                            if (!int.TryParse(
                                json.Substring(i + 1, 4),
                                NumberStyles.HexNumber,
                                CultureInfo.InvariantCulture,
                                out code))
                                return null;
                            value.Append((char)code);
                            i += 4;
                            break;
                        default:
                            return null;
                    }
                    escaped = false;
                }
                else if (current == '\\')
                {
                    escaped = true;
                }
                else if (current == '"')
                {
                    return value.ToString();
                }
                else
                {
                    value.Append(current);
                }
            }
            return null;
        }

        private static bool TryExtractBoolean(string json, string key, out bool value)
        {
            value = false;
            int index = FindValue(json, key);
            if (index < 0) return false;
            if (json.Length - index >= 4 &&
                string.CompareOrdinal(json, index, "true", 0, 4) == 0)
            {
                value = true;
                return true;
            }
            return json.Length - index >= 5 &&
                string.CompareOrdinal(json, index, "false", 0, 5) == 0;
        }

        private static int FindValue(string json, string key)
        {
            if (string.IsNullOrEmpty(json) || string.IsNullOrEmpty(key)) return -1;
            string marker = "\"" + key + "\"";
            int index = json.IndexOf(marker, StringComparison.Ordinal);
            if (index < 0) return -1;
            index = json.IndexOf(':', index + marker.Length);
            if (index < 0) return -1;
            index++;
            while (index < json.Length && char.IsWhiteSpace(json[index])) index++;
            return index;
        }

        private static string ExtractContainer(
            string json,
            string key,
            char open,
            char close)
        {
            int start = FindValue(json, key);
            if (start < 0 || start >= json.Length || json[start] != open) return null;
            bool inString = false;
            bool escaped = false;
            int depth = 0;
            for (int i = start; i < json.Length; i++)
            {
                char current = json[i];
                if (inString)
                {
                    if (escaped) escaped = false;
                    else if (current == '\\') escaped = true;
                    else if (current == '"') inString = false;
                    continue;
                }
                if (current == '"')
                {
                    inString = true;
                    continue;
                }
                if (current == open) depth++;
                else if (current == close && --depth == 0)
                    return json.Substring(start, i - start + 1);
            }
            return null;
        }

        private static string FirstObject(string array)
        {
            List<string> values = SplitObjects(array);
            return values.Count == 0 ? null : values[0];
        }

        private static List<string> SplitObjects(string array)
        {
            List<string> values = new List<string>();
            if (string.IsNullOrEmpty(array)) return values;
            bool inString = false;
            bool escaped = false;
            int depth = 0;
            int start = -1;
            for (int i = 0; i < array.Length; i++)
            {
                char current = array[i];
                if (inString)
                {
                    if (escaped) escaped = false;
                    else if (current == '\\') escaped = true;
                    else if (current == '"') inString = false;
                    continue;
                }
                if (current == '"')
                {
                    inString = true;
                    continue;
                }
                if (current == '{')
                {
                    if (depth == 0) start = i;
                    depth++;
                }
                else if (current == '}' && depth > 0)
                {
                    depth--;
                    if (depth == 0 && start >= 0)
                    {
                        values.Add(array.Substring(start, i - start + 1));
                        start = -1;
                    }
                }
            }
            return values;
        }

        private static DateTime ParseDate(string value, string stage)
        {
            DateTime parsed;
            if (string.IsNullOrEmpty(value) ||
                (!TryParseRfc3339Utc(value, out parsed) &&
                 !DateTime.TryParse(
                     value,
                     CultureInfo.InvariantCulture,
                     DateTimeStyles.AdjustToUniversal,
                     out parsed)))
                throw ProtocolFailure(stage);
            return parsed.ToUniversalTime();
        }

        private static bool TryParseRfc3339Utc(
            string value,
            out DateTime parsed)
        {
            parsed = default(DateTime);
            if (string.IsNullOrEmpty(value) || value.Length < 20 ||
                value[4] != '-' || value[7] != '-' ||
                (value[10] != 'T' && value[10] != 't') ||
                value[13] != ':' || value[16] != ':')
                return false;

            int year;
            int month;
            int day;
            int hour;
            int minute;
            int second;
            if (!TryParseDigits(value, 0, 4, out year) ||
                !TryParseDigits(value, 5, 2, out month) ||
                !TryParseDigits(value, 8, 2, out day) ||
                !TryParseDigits(value, 11, 2, out hour) ||
                !TryParseDigits(value, 14, 2, out minute) ||
                !TryParseDigits(value, 17, 2, out second))
                return false;

            int index = 19;
            long fractionTicks = 0;
            if (index < value.Length && value[index] == '.')
            {
                index++;
                int digits = 0;
                while (index < value.Length &&
                    value[index] >= '0' && value[index] <= '9')
                {
                    if (digits < 7)
                        fractionTicks =
                            (fractionTicks * 10) + (value[index] - '0');
                    digits++;
                    index++;
                }
                if (digits == 0) return false;
                for (int i = digits; i < 7; i++) fractionTicks *= 10;
            }

            int offsetMinutes = 0;
            if (index < value.Length &&
                (value[index] == 'Z' || value[index] == 'z'))
            {
                index++;
            }
            else
            {
                if (index + 6 != value.Length ||
                    (value[index] != '+' && value[index] != '-') ||
                    value[index + 3] != ':')
                    return false;
                int offsetHours;
                int offsetMinutePart;
                if (!TryParseDigits(value, index + 1, 2, out offsetHours) ||
                    !TryParseDigits(value, index + 4, 2, out offsetMinutePart) ||
                    offsetHours > 14 || offsetMinutePart > 59 ||
                    (offsetHours == 14 && offsetMinutePart != 0))
                    return false;
                offsetMinutes = (offsetHours * 60) + offsetMinutePart;
                if (value[index] == '-') offsetMinutes = -offsetMinutes;
                index += 6;
            }
            if (index != value.Length) return false;

            try
            {
                DateTime local = new DateTime(
                    year,
                    month,
                    day,
                    hour,
                    minute,
                    second,
                    DateTimeKind.Unspecified).AddTicks(fractionTicks);
                parsed = DateTime.SpecifyKind(
                    local.AddMinutes(-offsetMinutes),
                    DateTimeKind.Utc);
                return true;
            }
            catch (ArgumentOutOfRangeException)
            {
                return false;
            }
        }

        private static bool TryParseDigits(
            string value,
            int start,
            int count,
            out int parsed)
        {
            parsed = 0;
            if (value == null || start < 0 || count <= 0 ||
                start + count > value.Length)
                return false;
            for (int i = 0; i < count; i++)
            {
                char current = value[start + i];
                if (current < '0' || current > '9') return false;
                parsed = (parsed * 10) + (current - '0');
            }
            return true;
        }

        private static bool IsTransientError(uint errorCode)
        {
            return errorCode == 7 || errorCode == 9;
        }

        private static void LogTransientRetry(
            string stage,
            int attempt,
            uint errorCode)
        {
            FileLogger.Log(
                "AUTH",
                "SDK transient failure: " + stage + " code=" + errorCode +
                " attempt=" + attempt + "/" + TransientAttemptCount);
        }

        private static void SleepBeforeRetry(int attempt)
        {
            Thread.Sleep(400 * attempt);
        }

        private static VeriGateException ProtocolFailure(string stage)
        {
            FileLogger.Log("AUTH", "Protocol validation failed: " + stage);
            return new VeriGateException(9);
        }

        private static string JsonEscape(string value)
        {
            return (value ?? string.Empty)
                .Replace("\\", "\\\\")
                .Replace("\"", "\\\"");
        }

        private static void ThrowIfFailed(uint errorCode)
        {
            if (errorCode != 0) throw new VeriGateException(errorCode);
        }

        private void EnsureNotDisposed()
        {
            if (_context == IntPtr.Zero)
                throw new ObjectDisposedException("VeriGateClient");
        }

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate uint NativeJsonOperation(IntPtr context, out NativeBuffer output);

        [StructLayout(LayoutKind.Sequential)]
        private struct NativeSlice
        {
            internal IntPtr Data;
            internal UIntPtr Length;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct NativeBuffer
        {
            internal IntPtr Data;
            internal UIntPtr Length;
            internal UIntPtr Capacity;
        }

        private sealed class Utf8Slice : IDisposable
        {
            private readonly bool _sensitive;
            private IntPtr _data;
            private int _length;

            internal Utf8Slice(string value, bool sensitive)
            {
                byte[] bytes = Encoding.UTF8.GetBytes(value);
                _length = bytes.Length;
                _sensitive = sensitive;
                _data = Marshal.AllocHGlobal(_length);
                Marshal.Copy(bytes, 0, _data, _length);
                if (sensitive) Array.Clear(bytes, 0, bytes.Length);
            }

            internal NativeSlice Value
            {
                get
                {
                    NativeSlice value;
                    value.Data = _data;
                    value.Length = new UIntPtr((uint)_length);
                    return value;
                }
            }

            public void Dispose()
            {
                if (_data == IntPtr.Zero) return;
                if (_sensitive)
                {
                    byte[] zeros = new byte[_length];
                    Marshal.Copy(zeros, 0, _data, zeros.Length);
                }
                Marshal.FreeHGlobal(_data);
                _data = IntPtr.Zero;
                _length = 0;
            }
        }

        private sealed class ProcessLock : IDisposable
        {
            private bool _released;

            public void Dispose()
            {
                if (_released) return;
                ProcessMutex.ReleaseMutex();
                _released = true;
            }
        }

        private static class NativeMethods
        {
            [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
            internal delegate uint WindowsClientNew(
                NativeSlice configJson,
                NativeSlice directCard,
                out IntPtr output);

            [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
            internal delegate void ClientFree(IntPtr context);

            [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
            internal delegate uint ClientVerify(
                IntPtr context,
                NativeSlice requestJson,
                out NativeBuffer output);

            [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
            internal delegate uint ClientLogout(IntPtr context);

            [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
            internal delegate void BufferFree(NativeBuffer buffer);

            internal static readonly WindowsClientNew vg_sdk_windows_client_new =
                NativeSdkLoader.GetFunction<WindowsClientNew>(
                    "vg_sdk_windows_client_new");
            internal static readonly ClientFree vg_sdk_client_free =
                NativeSdkLoader.GetFunction<ClientFree>("vg_sdk_client_free");
            internal static readonly NativeJsonOperation vg_sdk_client_activate =
                NativeSdkLoader.GetFunction<NativeJsonOperation>(
                    "vg_sdk_client_activate");
            internal static readonly NativeJsonOperation vg_sdk_client_create_session =
                NativeSdkLoader.GetFunction<NativeJsonOperation>(
                    "vg_sdk_client_create_session");
            internal static readonly NativeJsonOperation vg_sdk_client_refresh =
                NativeSdkLoader.GetFunction<NativeJsonOperation>(
                    "vg_sdk_client_refresh");
            internal static readonly ClientVerify vg_sdk_client_verify =
                NativeSdkLoader.GetFunction<ClientVerify>(
                    "vg_sdk_client_verify");
            internal static readonly ClientLogout vg_sdk_client_logout =
                NativeSdkLoader.GetFunction<ClientLogout>(
                    "vg_sdk_client_logout");
            internal static readonly BufferFree vg_sdk_buffer_free =
                NativeSdkLoader.GetFunction<BufferFree>(
                    "vg_sdk_buffer_free");
        }
    }
}
