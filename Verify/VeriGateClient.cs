using System;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace ASWDEBUG.Verify
{
    internal sealed class VeriGateAuthorization
    {
        internal bool Allowed;
        internal string ReasonCode;
        internal string DeviceID;
        internal string SessionID;
        internal DateTime SessionExpiresAt;
    }

    internal sealed class VeriGateClient : IDisposable
    {
        private const string ProcessMutexName = "Local\\ASWDEBUG.VeriGate.Session";
        private static readonly Mutex ProcessMutex = new Mutex(false, ProcessMutexName);
        private readonly object _sync = new object();
        private IntPtr _context;

        private VeriGateClient(IntPtr context)
        {
            _context = context;
        }

        internal static VeriGateClient Open(string directCard)
        {
            if (string.IsNullOrEmpty(directCard) || directCard.Trim().Length != 78)
                throw new VeriGateException(1);

            NativeSdkLoader.EnsureLoaded();
            string storageRoot = Path.Combine(
                Path.Combine(
                    Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                        "ASWII"),
                    "VeriGate"),
                "State");
            string config = "{" +
                "\"origin\":\"" + JsonEscape(VeriGateOptions.Origin) + "\"," +
                "\"tenant_id\":\"" + VeriGateOptions.TenantId + "\"," +
                "\"application_id\":\"" + VeriGateOptions.ApplicationId + "\"," +
                "\"environment_id\":\"" + VeriGateOptions.EnvironmentId + "\"," +
                "\"storage_root\":\"" + JsonEscape(storageRoot) + "\"," +
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
                return new VeriGateClient(context);
            }
        }

        internal VeriGateAuthorization Authorize()
        {
            lock (_sync)
            using (EnterProcessLock())
            {
                EnsureNotDisposed();
                string activation = CallJson(NativeMethods.vg_sdk_client_activate);
                string deviceID = ExtractString(activation, "device_id");
                if (string.IsNullOrEmpty(deviceID)) throw new VeriGateException(9);

                string session;
                try
                {
                    session = CallJson(NativeMethods.vg_sdk_client_refresh);
                }
                catch (VeriGateException error)
                {
                    if (!error.IsAuthenticationFailure) throw;
                    session = CallJson(NativeMethods.vg_sdk_client_create_session);
                }

                VeriGateAuthorization authorization = VerifyCore();
                authorization.DeviceID = deviceID;
                authorization.SessionID = ExtractString(session, "session_id");
                authorization.SessionExpiresAt = ParseDate(
                    ExtractString(session, "session_expires_at"));
                if (string.IsNullOrEmpty(authorization.SessionID))
                    throw new VeriGateException(9);
                return authorization;
            }
        }

        internal VeriGateAuthorization Heartbeat()
        {
            lock (_sync)
            using (EnterProcessLock())
            {
                EnsureNotDisposed();
                try
                {
                    return VerifyCore();
                }
                catch (VeriGateException error)
                {
                    if (!error.IsAuthenticationFailure) throw;
                    CallJson(NativeMethods.vg_sdk_client_refresh);
                    return VerifyCore();
                }
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

        private VeriGateAuthorization VerifyCore()
        {
            string request = "{" +
                "\"client_version\":\"" + VeriGateOptions.ClientVersion + "\"," +
                "\"checks\":[{" +
                "\"capability\":\"" + VeriGateOptions.Capability + "\"," +
                "\"resource\":\"" + VeriGateOptions.Resource + "\"}]}";

            string response;
            using (Utf8Slice requestSlice = new Utf8Slice(request, false))
            {
                NativeBuffer output;
                uint result = NativeMethods.vg_sdk_client_verify(
                    _context,
                    requestSlice.Value,
                    out output);
                ThrowIfFailed(result);
                response = ReadAndFree(output);
            }

            bool allowed;
            if (!TryExtractBoolean(response, "allowed", out allowed))
                throw new VeriGateException(9);
            string reasonCode = ExtractString(response, "reason_code");
            string expiresAt = ExtractString(response, "expires_at");
            if (string.IsNullOrEmpty(reasonCode) || string.IsNullOrEmpty(expiresAt))
                throw new VeriGateException(9);
            if (!allowed) throw new VeriGateException(3);

            return new VeriGateAuthorization
            {
                Allowed = true,
                ReasonCode = reasonCode,
                SessionExpiresAt = ParseDate(expiresAt)
            };
        }

        private string CallJson(NativeJsonOperation operation)
        {
            NativeBuffer output;
            uint result = operation(_context, out output);
            ThrowIfFailed(result);
            return ReadAndFree(output);
        }

        private static string ReadAndFree(NativeBuffer output)
        {
            try
            {
                ulong length = output.Length.ToUInt64();
                if (output.Data == IntPtr.Zero || length == 0 || length > 1024 * 1024)
                    throw new VeriGateException(9);
                byte[] bytes = new byte[(int)length];
                Marshal.Copy(output.Data, bytes, 0, bytes.Length);
                return Encoding.UTF8.GetString(bytes);
            }
            finally
            {
                NativeMethods.vg_sdk_buffer_free(output);
            }
        }

        private static string ExtractString(string json, string key)
        {
            if (string.IsNullOrEmpty(json)) return null;
            Match match = Regex.Match(
                json,
                "\"" + Regex.Escape(key) + "\"\\s*:\\s*\"([^\"]*)\"",
                RegexOptions.CultureInvariant);
            return match.Success ? match.Groups[1].Value : null;
        }

        private static bool TryExtractBoolean(string json, string key, out bool value)
        {
            value = false;
            if (string.IsNullOrEmpty(json)) return false;
            Match match = Regex.Match(
                json,
                "\"" + Regex.Escape(key) + "\"\\s*:\\s*(true|false)",
                RegexOptions.CultureInvariant);
            if (!match.Success) return false;
            value = string.Equals(match.Groups[1].Value, "true", StringComparison.Ordinal);
            return true;
        }

        private static DateTime ParseDate(string value)
        {
            DateTime parsed;
            if (string.IsNullOrEmpty(value) ||
                !DateTime.TryParse(
                    value,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AdjustToUniversal,
                    out parsed))
                throw new VeriGateException(9);
            return parsed.ToUniversalTime();
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
            private const string Library = "verigate_sdk.dll";

            [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
            internal static extern uint vg_sdk_windows_client_new(
                NativeSlice configJson,
                NativeSlice directCard,
                out IntPtr output);

            [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
            internal static extern void vg_sdk_client_free(IntPtr context);

            [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
            internal static extern uint vg_sdk_client_activate(
                IntPtr context,
                out NativeBuffer output);

            [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
            internal static extern uint vg_sdk_client_create_session(
                IntPtr context,
                out NativeBuffer output);

            [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
            internal static extern uint vg_sdk_client_refresh(
                IntPtr context,
                out NativeBuffer output);

            [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
            internal static extern uint vg_sdk_client_verify(
                IntPtr context,
                NativeSlice requestJson,
                out NativeBuffer output);

            [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
            internal static extern void vg_sdk_buffer_free(NativeBuffer buffer);
        }
    }
}
