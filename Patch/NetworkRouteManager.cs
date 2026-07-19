using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using ASWDEBUG.Logger;
using UnityEngine;

namespace ASWDEBUG.Patch
{
    public enum NetworkRouteState
    {
        Direct,
        ProxyStarting,
        ProxyReady,
        ProxyError
    }

    public static class NetworkRouteManager
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct DataBlob
        {
            public int Length;
            public IntPtr Data;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct JobBasicLimitInformation
        {
            public long PerProcessUserTimeLimit;
            public long PerJobUserTimeLimit;
            public uint LimitFlags;
            public UIntPtr MinimumWorkingSetSize;
            public UIntPtr MaximumWorkingSetSize;
            public uint ActiveProcessLimit;
            public UIntPtr Affinity;
            public uint PriorityClass;
            public uint SchedulingClass;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct IoCounters
        {
            public ulong ReadOperationCount;
            public ulong WriteOperationCount;
            public ulong OtherOperationCount;
            public ulong ReadTransferCount;
            public ulong WriteTransferCount;
            public ulong OtherTransferCount;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct JobExtendedLimitInformation
        {
            public JobBasicLimitInformation BasicLimitInformation;
            public IoCounters IoInfo;
            public UIntPtr ProcessMemoryLimit;
            public UIntPtr JobMemoryLimit;
            public UIntPtr PeakProcessMemoryUsed;
            public UIntPtr PeakJobMemoryUsed;
        }

        [DllImport("crypt32.dll", SetLastError = true)]
        private static extern bool CryptUnprotectData(
            ref DataBlob input,
            IntPtr description,
            IntPtr optionalEntropy,
            IntPtr reserved,
            IntPtr prompt,
            int flags,
            out DataBlob output);

        [DllImport("kernel32.dll")]
        private static extern IntPtr LocalFree(IntPtr memory);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr CreateJobObject(IntPtr jobAttributes, string name);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetInformationJobObject(IntPtr job, int informationClass, IntPtr information, uint informationLength);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr handle);

        private sealed class ProxyConfig
        {
            public bool Enabled;
            public string ServerHost;
            public int ServerPort;
            public string SshUser;
            public string SshKeyPath;
            public string SshKnownHostsPath;
            public int RemoteSocksPort;
            public int RemoteHttpPort;
            public int RemoteDnsPort;
            public string ProxyUser;
            public string ProxyPassword;
            public int ConnectTimeoutMs;
        }

        private sealed class SocksTargetException : IOException
        {
            public SocksTargetException(string message) : base(message) { }
        }

        private static readonly object Sync = new object();
        private static readonly object RelaySync = new object();
        private static readonly Dictionary<string, string> PendingWwwUrls = new Dictionary<string, string>();
        private static bool _prepared;
        private static bool _initialized;
        private static volatile bool _shuttingDown;
        [ThreadStatic]
        private static bool _internalWebRequest;
        private static Process _sshProcess;
        private static IntPtr _sshJob;
        private static TcpListener _wwwRelay;
        private static Thread _wwwRelayThread;
        private static volatile bool _relayStopping;
        private static ProxyConfig _config;
        private static int _localSocksPort;
        private static int _localHttpPort;
        private static int _localDnsPort;
        private static string _errorReason;

        public static bool ProxyRequired { get; private set; }
        public static NetworkRouteState State { get; private set; }
        public static bool HasError { get { return State == NetworkRouteState.ProxyError; } }

        public static string StatusText
        {
            get
            {
                if (State == NetworkRouteState.ProxyReady) return "客户端 2 · 服务器转发";
                if (State == NetworkRouteState.ProxyStarting) return "客户端 2 · 转发启动中";
                if (State == NetworkRouteState.ProxyError) return "客户端 2 · 转发失败（已阻断直连）";
                return "客户端 1 · 本机直连";
            }
        }

        public static void PrepareClientRole()
        {
            lock (Sync)
            {
                if (_prepared) return;
                _prepared = true;
                State = NetworkRouteState.Direct;

                bool reliable;
                int rank = GetCurrentClientRank(out reliable);
                if (!reliable)
                {
                    ProxyRequired = true;
                    SetError("classification", new InvalidOperationException("client rank unavailable"));
                    return;
                }
                ProxyRequired = rank == 1;
                FileLogger.Log("NETWORK", "client rank=" + (rank + 1) + " route=" + (ProxyRequired ? "proxy" : "direct"));
                if (ProxyRequired) State = NetworkRouteState.ProxyStarting;
            }
        }

        public static void Initialize()
        {
            lock (Sync)
            {
                if (_initialized) return;
                _initialized = true;
                if (!_prepared) PrepareClientRole();
                if (!ProxyRequired || State == NetworkRouteState.ProxyError) return;

                string configPath = GetConfigPath();
                try
                {
                    _config = LoadConfig(configPath);
                    if (!_config.Enabled) throw new InvalidOperationException("proxy config is disabled");
                    StartSshTunnel();
                    VerifyRemoteServices();
                    ConfigureManagedHttpProxy();
                    StartWwwRelay();
                    State = NetworkRouteState.ProxyReady;
                    AppDomain.CurrentDomain.ProcessExit += OnProcessExit;
                    FileLogger.Log("NETWORK", "second-client forwarding ready");
                }
                catch (Exception ex)
                {
                    SetError("startup", ex);
                }
            }
        }

        public static void ReportHookFailure()
        {
            if (!ProxyRequired) return;
            SetError("hooks", new InvalidOperationException("network hook installation failed"));
        }

        public static void Shutdown()
        {
            lock (Sync)
            {
                _shuttingDown = true;
                _relayStopping = true;
                TcpListener relay = _wwwRelay;
                Thread relayThread = _wwwRelayThread;
                _wwwRelay = null;
                _wwwRelayThread = null;
                try { if (relay != null) relay.Stop(); } catch { }
                if (relayThread != null && relayThread != Thread.CurrentThread)
                {
                    try { relayThread.Join(1000); } catch { }
                }
                lock (RelaySync) { PendingWwwUrls.Clear(); }

                Process ssh = _sshProcess;
                _sshProcess = null;
                IntPtr job = _sshJob;
                _sshJob = IntPtr.Zero;
                if (job != IntPtr.Zero)
                {
                    try { CloseHandle(job); } catch { }
                }
                if (ssh == null) return;
                try
                {
                    if (!ssh.HasExited) ssh.Kill();
                }
                catch { }
                try { ssh.WaitForExit(2000); } catch { }
                try { ssh.Close(); } catch { }
            }
        }

        public static string RewriteWwwUrl(string url)
        {
            if (!ProxyRequired) return url;
            Uri parsed;
            if (!Uri.TryCreate(url, UriKind.Absolute, out parsed)) return url;
            if (!string.Equals(parsed.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(parsed.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)) return url;
            if (State != NetworkRouteState.ProxyReady || _wwwRelay == null)
                throw new WebException("server forwarding is not ready");

            string token = Guid.NewGuid().ToString("N");
            lock (RelaySync) { PendingWwwUrls[token] = url; }
            int port = ((IPEndPoint)_wwwRelay.LocalEndpoint).Port;
            return "http://127.0.0.1:" + port + "/" + token;
        }

        public static void GuardWebRequest(string requestUri)
        {
            if (!ProxyRequired || _internalWebRequest) return;
            Uri uri;
            if (!Uri.TryCreate(requestUri, UriKind.Absolute, out uri)) return;
            if (!string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)) return;
            if (IsLoopback(uri.Host)) return;
            if (State != NetworkRouteState.ProxyReady)
                throw new WebException("server forwarding is not ready");
        }

        public static bool RouteSocketConnect(Socket socket, string host, int port)
        {
            if (!ProxyRequired || IsLoopback(host)) return true;
            if (State != NetworkRouteState.ProxyReady || _config == null)
                throw new SocketException((int)SocketError.NetworkUnreachable);
            if (_sshProcess == null || HasProcessExited(_sshProcess))
            {
                SetError("tunnel", new IOException("SSH tunnel is not running"));
                throw new SocketException((int)SocketError.NetworkUnreachable);
            }

            try
            {
                ConnectThroughSocks(socket, host, port);
                return false;
            }
            catch (SocksTargetException)
            {
                try { socket.Close(); } catch { }
                FileLogger.Log("NETWORK", "proxied target rejected on port " + port);
                throw new SocketException((int)SocketError.ConnectionRefused);
            }
            catch (Exception ex)
            {
                try { socket.Close(); } catch { }
                SetError("socket", ex);
                throw new SocketException((int)SocketError.NetworkUnreachable);
            }
        }

        public static bool RouteDnsGetHostEntry(string host, ref IPHostEntry result)
        {
            if (!ProxyRequired) return true;
            IPAddress address;
            if (IPAddress.TryParse(host, out address) || IsLoopback(host))
            {
                result = CreateHostEntry(host, new IPAddress[] { address ?? IPAddress.Loopback });
                return false;
            }
            result = ResolveHostThroughServer(host);
            return false;
        }

        public static bool RouteDnsGetHostAddresses(string host, ref IPAddress[] result)
        {
            if (!ProxyRequired) return true;
            IPHostEntry entry = null;
            RouteDnsGetHostEntry(host, ref entry);
            result = entry.AddressList;
            return false;
        }

        private static int GetCurrentClientRank(out bool reliable)
        {
            reliable = false;
            try
            {
                Process current = Process.GetCurrentProcess();
                string currentPath = current.MainModule.FileName;
                List<Process> peers = new List<Process>();
                Process[] candidates = Process.GetProcessesByName(current.ProcessName);
                bool inaccessiblePeer = false;
                for (int i = 0; i < candidates.Length; i++)
                {
                    try
                    {
                        if (string.Equals(candidates[i].MainModule.FileName, currentPath, StringComparison.OrdinalIgnoreCase))
                            peers.Add(candidates[i]);
                    }
                    catch { inaccessiblePeer = true; }
                }

                if (inaccessiblePeer) return -1;

                peers.Sort(delegate(Process left, Process right)
                {
                    long leftTicks;
                    long rightTicks;
                    if (!TryGetStartTicks(left, out leftTicks) || !TryGetStartTicks(right, out rightTicks))
                        return left.Id.CompareTo(right.Id);
                    int order = leftTicks.CompareTo(rightTicks);
                    return order != 0 ? order : left.Id.CompareTo(right.Id);
                });

                for (int i = 0; i < peers.Count; i++)
                {
                    long ticks;
                    if (!TryGetStartTicks(peers[i], out ticks)) return -1;
                    if (peers[i].Id == current.Id)
                    {
                        reliable = true;
                        return i;
                    }
                }
            }
            catch
            {
                FileLogger.Log("NETWORK", "process rank detection failed");
            }
            return -1;
        }

        private static bool TryGetStartTicks(Process process, out long ticks)
        {
            try
            {
                ticks = process.StartTime.ToUniversalTime().Ticks;
                return true;
            }
            catch
            {
                ticks = 0;
                return false;
            }
        }

        private static string GetConfigPath()
        {
            string overridden = Environment.GetEnvironmentVariable("ASWII_SURVIVAL_PROXY_CONFIG");
            if (!string.IsNullOrEmpty(overridden)) return Environment.ExpandEnvironmentVariables(overridden);
            return Path.Combine(Path.Combine(Application.persistentDataPath, "Config"), "proxy.local.ini");
        }

        private static ProxyConfig LoadConfig(string path)
        {
            if (!File.Exists(path)) throw new FileNotFoundException("proxy config not found", path);
            Dictionary<string, string> values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            string[] lines = File.ReadAllLines(path, Encoding.UTF8);
            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i].Trim();
                if (line.Length == 0 || line.StartsWith("#") || line.StartsWith(";")) continue;
                int separator = line.IndexOf('=');
                if (separator <= 0) continue;
                values[line.Substring(0, separator).Trim()] = line.Substring(separator + 1).Trim();
            }

            ProxyConfig config = new ProxyConfig();
            config.Enabled = ReadBool(values, "enabled", true);
            config.ServerHost = Require(values, "server_host");
            config.ServerPort = ReadInt(values, "server_port", 22, 1, 65535);
            config.SshUser = Require(values, "ssh_user");
            config.SshKeyPath = Environment.ExpandEnvironmentVariables(Require(values, "ssh_key"));
            config.SshKnownHostsPath = Environment.ExpandEnvironmentVariables(Require(values, "ssh_known_hosts"));
            config.RemoteSocksPort = ReadInt(values, "remote_socks_port", 39080, 1, 65535);
            config.RemoteHttpPort = ReadInt(values, "remote_http_port", 39081, 1, 65535);
            config.RemoteDnsPort = ReadInt(values, "remote_dns_port", 39082, 1, 65535);
            config.ProxyUser = Require(values, "proxy_username");
            config.ProxyPassword = ReadSecret(values);
            if (config.ProxyPassword.Length == 0) throw new InvalidOperationException("proxy password is empty");
            config.ConnectTimeoutMs = ReadInt(values, "connect_timeout_ms", 7000, 1000, 30000);

            if (!File.Exists(config.SshKeyPath)) throw new FileNotFoundException("SSH key not found", config.SshKeyPath);
            if (!File.Exists(config.SshKnownHostsPath)) throw new FileNotFoundException("SSH known-hosts file not found", config.SshKnownHostsPath);
            return config;
        }

        private static string ReadSecret(Dictionary<string, string> values)
        {
            string environmentName;
            if (values.TryGetValue("proxy_password_env", out environmentName) && !string.IsNullOrEmpty(environmentName))
            {
                string fromEnvironment = Environment.GetEnvironmentVariable(environmentName);
                if (!string.IsNullOrEmpty(fromEnvironment)) return fromEnvironment;
            }

            string protectedValue;
            if (!values.TryGetValue("proxy_password_dpapi", out protectedValue) || string.IsNullOrEmpty(protectedValue))
                throw new InvalidOperationException("proxy password is not configured");
            byte[] cipher = Convert.FromBase64String(protectedValue);
            byte[] plain = UnprotectCurrentUser(cipher);
            return Encoding.UTF8.GetString(plain);
        }

        private static byte[] UnprotectCurrentUser(byte[] cipher)
        {
            DataBlob input = new DataBlob();
            DataBlob output;
            input.Length = cipher.Length;
            input.Data = Marshal.AllocHGlobal(cipher.Length);
            try
            {
                Marshal.Copy(cipher, 0, input.Data, cipher.Length);
                if (!CryptUnprotectData(ref input, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 0, out output))
                    throw new InvalidOperationException("DPAPI decrypt failed: " + Marshal.GetLastWin32Error());
                try
                {
                    byte[] plain = new byte[output.Length];
                    Marshal.Copy(output.Data, plain, 0, output.Length);
                    return plain;
                }
                finally
                {
                    if (output.Data != IntPtr.Zero) LocalFree(output.Data);
                }
            }
            finally
            {
                Marshal.FreeHGlobal(input.Data);
            }
        }

        private static void StartSshTunnel()
        {
            _localSocksPort = FindFreePort(0);
            _localHttpPort = FindFreePort(_localSocksPort);
            _localDnsPort = FindFreePort(_localHttpPort);

            string sshPath = FindSshPath();

            ProcessStartInfo start = new ProcessStartInfo();
            start.FileName = sshPath;
            start.UseShellExecute = false;
            start.CreateNoWindow = true;
            start.WindowStyle = ProcessWindowStyle.Hidden;
            start.Arguments = BuildSshArguments();
            _shuttingDown = false;
            _sshProcess = Process.Start(start);
            if (_sshProcess == null) throw new InvalidOperationException("failed to start SSH tunnel");
            AttachSshJob(_sshProcess);
            _sshProcess.EnableRaisingEvents = true;
            _sshProcess.Exited += OnSshExited;

            DateTime deadline = DateTime.UtcNow.AddMilliseconds(_config.ConnectTimeoutMs);
            while (DateTime.UtcNow < deadline)
            {
                if (_sshProcess.HasExited)
                    throw new InvalidOperationException("SSH tunnel exited with code " + _sshProcess.ExitCode);
                if (CanConnectLocal(_localSocksPort) && CanConnectLocal(_localHttpPort) && CanConnectLocal(_localDnsPort)) return;
                Thread.Sleep(100);
            }
            throw new TimeoutException("SSH tunnel startup timed out");
        }

        private static string BuildSshArguments()
        {
            StringBuilder args = new StringBuilder();
            args.Append("-N -T -i ").Append(Quote(_config.SshKeyPath));
            args.Append(" -p ").Append(_config.ServerPort);
            args.Append(" -o BatchMode=yes -o IdentitiesOnly=yes -o StrictHostKeyChecking=yes");
            args.Append(" -o UserKnownHostsFile=").Append(Quote(_config.SshKnownHostsPath));
            args.Append(" -o ExitOnForwardFailure=yes -o ServerAliveInterval=15 -o ServerAliveCountMax=2");
            args.Append(" -L 127.0.0.1:").Append(_localSocksPort).Append(":127.0.0.1:").Append(_config.RemoteSocksPort);
            args.Append(" -L 127.0.0.1:").Append(_localHttpPort).Append(":127.0.0.1:").Append(_config.RemoteHttpPort);
            args.Append(" -L 127.0.0.1:").Append(_localDnsPort).Append(":127.0.0.1:").Append(_config.RemoteDnsPort);
            args.Append(' ').Append(Quote(_config.SshUser + "@" + _config.ServerHost));
            return args.ToString();
        }

        private static void VerifyRemoteServices()
        {
            _internalWebRequest = true;
            try
            {
                Socket socksProbe = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                try { ConnectThroughSocks(socksProbe, "127.0.0.1", _config.RemoteDnsPort); }
                finally { try { socksProbe.Close(); } catch { } }

                WebProxy httpProxy = new WebProxy("http://127.0.0.1:" + _localHttpPort, false);
                httpProxy.Credentials = new NetworkCredential(_config.ProxyUser, _config.ProxyPassword);
                HttpWebRequest httpProbe = (HttpWebRequest)WebRequest.Create(
                    "http://asw-dns-relay.internal:" + _config.RemoteDnsPort + "/resolve?host=localhost");
                httpProbe.Proxy = httpProxy;
                httpProbe.Timeout = _config.ConnectTimeoutMs;
                ReadProbeResponse(httpProbe);

                HttpWebRequest dnsProbe = (HttpWebRequest)WebRequest.Create(
                    "http://127.0.0.1:" + _localDnsPort + "/resolve?host=localhost");
                dnsProbe.Proxy = null;
                dnsProbe.Timeout = _config.ConnectTimeoutMs;
                ReadProbeResponse(dnsProbe);
            }
            finally
            {
                _internalWebRequest = false;
            }
        }

        private static void ReadProbeResponse(HttpWebRequest request)
        {
            HttpWebResponse response = null;
            StreamReader reader = null;
            try
            {
                response = (HttpWebResponse)request.GetResponse();
                reader = new StreamReader(response.GetResponseStream(), Encoding.ASCII);
                if (reader.ReadToEnd().IndexOf("127.0.0.1") < 0)
                    throw new IOException("proxy service probe returned an invalid response");
            }
            finally
            {
                if (reader != null) reader.Close();
                if (response != null) response.Close();
            }
        }

        private static void AttachSshJob(Process process)
        {
            IntPtr job = CreateJobObject(IntPtr.Zero, null);
            if (job == IntPtr.Zero) return;
            IntPtr information = IntPtr.Zero;
            try
            {
                JobExtendedLimitInformation limits = new JobExtendedLimitInformation();
                limits.BasicLimitInformation.LimitFlags = 0x00002000;
                int size = Marshal.SizeOf(typeof(JobExtendedLimitInformation));
                information = Marshal.AllocHGlobal(size);
                Marshal.StructureToPtr(limits, information, false);
                if (SetInformationJobObject(job, 9, information, (uint)size) &&
                    AssignProcessToJobObject(job, process.Handle))
                {
                    _sshJob = job;
                    return;
                }
                FileLogger.Log("NETWORK", "SSH job attachment unavailable");
            }
            finally
            {
                if (information != IntPtr.Zero) Marshal.FreeHGlobal(information);
                if (_sshJob != job) CloseHandle(job);
            }
        }

        private static void ConfigureManagedHttpProxy()
        {
            WebProxy proxy = new WebProxy("http://127.0.0.1:" + _localHttpPort, true);
            proxy.Credentials = new NetworkCredential(_config.ProxyUser, _config.ProxyPassword);
            proxy.BypassList = new string[] { "^localhost$", "^127\\.", "^\\[?::1\\]?$" };
            WebRequest.DefaultWebProxy = proxy;

        }

        private static IPHostEntry ResolveHostThroughServer(string host)
        {
            if (State != NetworkRouteState.ProxyReady || _sshProcess == null || HasProcessExited(_sshProcess))
                throw new SocketException((int)SocketError.NetworkUnreachable);

            HttpWebResponse response = null;
            StreamReader reader = null;
            try
            {
                string url = "http://127.0.0.1:" + _localDnsPort + "/resolve?host=" + Uri.EscapeDataString(host);
                HttpWebRequest request = (HttpWebRequest)WebRequest.Create(url);
                request.Proxy = null;
                request.Timeout = _config.ConnectTimeoutMs;
                response = (HttpWebResponse)request.GetResponse();
                reader = new StreamReader(response.GetResponseStream(), Encoding.ASCII);
                string[] lines = reader.ReadToEnd().Split(new char[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                List<IPAddress> addresses = new List<IPAddress>();
                for (int i = 0; i < lines.Length; i++)
                {
                    IPAddress address;
                    if (IPAddress.TryParse(lines[i].Trim(), out address) && address.AddressFamily == AddressFamily.InterNetwork)
                        addresses.Add(address);
                }
                if (addresses.Count == 0) throw new SocketException((int)SocketError.HostNotFound);
                return CreateHostEntry(host, addresses.ToArray());
            }
            catch (WebException ex)
            {
                if (ex.Response != null) throw new SocketException((int)SocketError.HostNotFound);
                SetError("dns", ex);
                throw new SocketException((int)SocketError.NetworkUnreachable);
            }
            finally
            {
                if (reader != null) reader.Close();
                if (response != null) response.Close();
            }
        }

        private static IPHostEntry CreateHostEntry(string host, IPAddress[] addresses)
        {
            IPHostEntry entry = new IPHostEntry();
            entry.HostName = host;
            entry.Aliases = new string[0];
            entry.AddressList = addresses;
            return entry;
        }

        private static void StartWwwRelay()
        {
            _relayStopping = false;
            _wwwRelay = new TcpListener(IPAddress.Loopback, 0);
            _wwwRelay.Start();
            _wwwRelayThread = new Thread(WwwRelayLoop);
            _wwwRelayThread.IsBackground = true;
            _wwwRelayThread.Name = "ASWII WWW proxy relay";
            _wwwRelayThread.Start();
        }

        private static void WwwRelayLoop()
        {
            while (!_relayStopping)
            {
                TcpClient client = null;
                try
                {
                    client = _wwwRelay.AcceptTcpClient();
                    ThreadPool.QueueUserWorkItem(HandleWwwRelayClient, client);
                }
                catch
                {
                    if (client != null) try { client.Close(); } catch { }
                    if (!_relayStopping) Thread.Sleep(100);
                }
            }
        }

        private static void HandleWwwRelayClient(object state)
        {
            TcpClient client = state as TcpClient;
            if (client == null) return;
            try
            {
                System.Net.Sockets.NetworkStream stream = client.GetStream();
                stream.ReadTimeout = _config.ConnectTimeoutMs;
                string token = ReadRelayToken(stream);
                string targetUrl;
                lock (RelaySync)
                {
                    if (!PendingWwwUrls.TryGetValue(token, out targetUrl))
                    {
                        WriteRelayError(stream, 404, "Not Found");
                        return;
                    }
                    PendingWwwUrls.Remove(token);
                }

                HttpWebRequest request = (HttpWebRequest)WebRequest.Create(targetUrl);
                request.Method = "GET";
                request.Proxy = WebRequest.DefaultWebProxy;
                request.AllowAutoRedirect = true;
                request.Timeout = _config.ConnectTimeoutMs;
                request.ReadWriteTimeout = _config.ConnectTimeoutMs;

                HttpWebResponse response = null;
                try
                {
                    response = (HttpWebResponse)request.GetResponse();
                    WriteRelayResponse(stream, response);
                }
                catch (WebException ex)
                {
                    response = ex.Response as HttpWebResponse;
                    if (response != null) WriteRelayResponse(stream, response);
                    else WriteRelayError(stream, 502, "Bad Gateway");
                }
                finally
                {
                    if (response != null) response.Close();
                }
            }
            catch (Exception ex)
            {
                FileLogger.Log("NETWORK", "WWW relay failed: " + ex.GetType().Name);
            }
            finally
            {
                try { client.Close(); } catch { }
            }
        }

        private static string ReadRelayToken(System.Net.Sockets.NetworkStream stream)
        {
            StringBuilder header = new StringBuilder();
            int matched = 0;
            while (header.Length < 16384)
            {
                int value = stream.ReadByte();
                if (value < 0) break;
                char character = (char)value;
                header.Append(character);
                if ((matched == 0 || matched == 2) && character == '\r') matched++;
                else if ((matched == 1 || matched == 3) && character == '\n') matched++;
                else matched = character == '\r' ? 1 : 0;
                if (matched == 4) break;
            }

            string[] firstLine = header.ToString().Split(new string[] { "\r\n" }, StringSplitOptions.None)[0].Split(' ');
            if (firstLine.Length < 2 || !string.Equals(firstLine[0], "GET", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("invalid WWW relay request");
            return firstLine[1].Trim('/');
        }

        private static void WriteRelayResponse(System.Net.Sockets.NetworkStream output, HttpWebResponse response)
        {
            string contentType = response.ContentType;
            if (string.IsNullOrEmpty(contentType) || contentType.IndexOfAny(new char[] { '\r', '\n' }) >= 0)
                contentType = "application/octet-stream";
            WriteAscii(output,
                "HTTP/1.0 " + (int)response.StatusCode + " " + response.StatusDescription + "\r\n" +
                "Content-Type: " + contentType + "\r\n" +
                "Connection: close\r\n\r\n");

            Stream input = response.GetResponseStream();
            if (input == null) return;
            byte[] buffer = new byte[32768];
            int count;
            while ((count = input.Read(buffer, 0, buffer.Length)) > 0)
                output.Write(buffer, 0, count);
        }

        private static void WriteRelayError(System.Net.Sockets.NetworkStream output, int status, string reason)
        {
            byte[] body = Encoding.UTF8.GetBytes(reason);
            WriteAscii(output,
                "HTTP/1.0 " + status + " " + reason + "\r\n" +
                "Content-Type: text/plain; charset=utf-8\r\n" +
                "Content-Length: " + body.Length + "\r\n" +
                "Connection: close\r\n\r\n");
            output.Write(body, 0, body.Length);
        }

        private static void WriteAscii(System.Net.Sockets.NetworkStream stream, string text)
        {
            byte[] data = Encoding.ASCII.GetBytes(text);
            stream.Write(data, 0, data.Length);
        }

        private static void ConnectThroughSocks(Socket socket, string host, int port)
        {
            int oldReceiveTimeout = socket.ReceiveTimeout;
            int oldSendTimeout = socket.SendTimeout;
            socket.ReceiveTimeout = _config.ConnectTimeoutMs;
            socket.SendTimeout = _config.ConnectTimeoutMs;

            IAsyncResult pending = socket.BeginConnect(IPAddress.Loopback, _localSocksPort, null, null);
            try
            {
                if (!pending.AsyncWaitHandle.WaitOne(_config.ConnectTimeoutMs, false))
                    throw new TimeoutException("SOCKS endpoint connection timed out");
                socket.EndConnect(pending);
            }
            finally
            {
                try { pending.AsyncWaitHandle.Close(); } catch { }
            }

            byte[] user = Encoding.UTF8.GetBytes(_config.ProxyUser);
            byte[] password = Encoding.UTF8.GetBytes(_config.ProxyPassword);
            if (user.Length == 0 || password.Length == 0 || user.Length > 255 || password.Length > 255)
                throw new InvalidOperationException("SOCKS credentials have an invalid length");

            SendAll(socket, new byte[] { 5, 1, 2 });
            byte[] greeting = ReadExact(socket, 2);
            if (greeting[0] != 5 || greeting[1] != 2) throw new InvalidOperationException("SOCKS username authentication was not accepted");

            byte[] auth = new byte[3 + user.Length + password.Length];
            auth[0] = 1;
            auth[1] = (byte)user.Length;
            Buffer.BlockCopy(user, 0, auth, 2, user.Length);
            auth[2 + user.Length] = (byte)password.Length;
            Buffer.BlockCopy(password, 0, auth, 3 + user.Length, password.Length);
            SendAll(socket, auth);
            byte[] authReply = ReadExact(socket, 2);
            if (authReply[0] != 1 || authReply[1] != 0) throw new InvalidOperationException("SOCKS authentication failed");

            byte[] target = Encoding.ASCII.GetBytes(host);
            if (target.Length == 0 || target.Length > 255) throw new InvalidOperationException("SOCKS target host is invalid");
            byte[] request = new byte[7 + target.Length];
            request[0] = 5;
            request[1] = 1;
            request[2] = 0;
            request[3] = 3;
            request[4] = (byte)target.Length;
            Buffer.BlockCopy(target, 0, request, 5, target.Length);
            request[5 + target.Length] = (byte)(port >> 8);
            request[6 + target.Length] = (byte)port;
            SendAll(socket, request);

            byte[] reply = ReadExact(socket, 4);
            if (reply[0] != 5 || reply[2] != 0) throw new InvalidOperationException("SOCKS reply is invalid");
            if (reply[1] != 0) throw new SocksTargetException("SOCKS code " + reply[1]);
            int addressLength;
            if (reply[3] == 1) addressLength = 4;
            else if (reply[3] == 4) addressLength = 16;
            else if (reply[3] == 3) addressLength = ReadExact(socket, 1)[0];
            else throw new InvalidOperationException("SOCKS reply address type is invalid");
            ReadExact(socket, addressLength + 2);

            socket.ReceiveTimeout = oldReceiveTimeout;
            socket.SendTimeout = oldSendTimeout;
        }

        private static string FindSshPath()
        {
            string windows = Environment.GetEnvironmentVariable("WINDIR");
            if (string.IsNullOrEmpty(windows)) windows = "C:\\Windows";

            if (IntPtr.Size == 4 && !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("PROCESSOR_ARCHITEW6432")))
            {
                string sysnative = Path.Combine(windows, "Sysnative\\OpenSSH\\ssh.exe");
                if (File.Exists(sysnative)) return sysnative;
            }

            string system32 = Path.Combine(windows, "System32\\OpenSSH\\ssh.exe");
            if (File.Exists(system32)) return system32;
            throw new FileNotFoundException("Windows OpenSSH client not found", system32);
        }

        private static bool HasProcessExited(Process process)
        {
            try { return process.HasExited; }
            catch { return true; }
        }

        private static void SendAll(Socket socket, byte[] data)
        {
            int sent = 0;
            while (sent < data.Length)
            {
                int count = socket.Send(data, sent, data.Length - sent, SocketFlags.None);
                if (count <= 0) throw new IOException("SOCKS connection closed while sending");
                sent += count;
            }
        }

        private static byte[] ReadExact(Socket socket, int length)
        {
            byte[] data = new byte[length];
            int received = 0;
            while (received < length)
            {
                int count = socket.Receive(data, received, length - received, SocketFlags.None);
                if (count <= 0) throw new IOException("SOCKS connection closed while receiving");
                received += count;
            }
            return data;
        }

        private static int FindFreePort(int avoid)
        {
            for (int attempt = 0; attempt < 8; attempt++)
            {
                TcpListener listener = new TcpListener(IPAddress.Loopback, 0);
                listener.Start();
                int port = ((IPEndPoint)listener.LocalEndpoint).Port;
                listener.Stop();
                if (port != avoid) return port;
            }
            throw new InvalidOperationException("could not allocate local proxy port");
        }

        private static bool CanConnectLocal(int port)
        {
            TcpClient client = new TcpClient();
            try
            {
                IAsyncResult pending = client.BeginConnect(IPAddress.Loopback, port, null, null);
                bool connected = pending.AsyncWaitHandle.WaitOne(250, false);
                if (connected) client.EndConnect(pending);
                try { pending.AsyncWaitHandle.Close(); } catch { }
                return connected;
            }
            catch { return false; }
            finally { try { client.Close(); } catch { } }
        }

        private static bool IsLoopback(string host)
        {
            if (string.IsNullOrEmpty(host)) return false;
            if (string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase)) return true;
            IPAddress address;
            return IPAddress.TryParse(host, out address) && IPAddress.IsLoopback(address);
        }

        private static string Require(Dictionary<string, string> values, string key)
        {
            string value;
            if (!values.TryGetValue(key, out value) || string.IsNullOrEmpty(value))
                throw new InvalidOperationException("missing proxy setting: " + key);
            return value;
        }

        private static int ReadInt(Dictionary<string, string> values, string key, int fallback, int minimum, int maximum)
        {
            string text;
            int value;
            if (!values.TryGetValue(key, out text) || !int.TryParse(text, out value)) return fallback;
            if (value < minimum || value > maximum) throw new InvalidOperationException("invalid proxy setting: " + key);
            return value;
        }

        private static bool ReadBool(Dictionary<string, string> values, string key, bool fallback)
        {
            string text;
            bool value;
            return values.TryGetValue(key, out text) && bool.TryParse(text, out value) ? value : fallback;
        }

        private static string Quote(string value)
        {
            return "\"" + value.Replace("\"", "\\\"") + "\"";
        }

        private static void SetError(string stage, Exception ex)
        {
            lock (Sync)
            {
                if (State == NetworkRouteState.ProxyError) return;
                State = NetworkRouteState.ProxyError;
                _errorReason = stage + ": " + ex.GetType().Name;
                FileLogger.Log("NETWORK", "forwarding failed at " + _errorReason);
                Shutdown();
                if (ProxyRequired)
                {
                    try { Application.Quit(); } catch { }
                }
            }
        }

        private static void OnProcessExit(object sender, EventArgs args)
        {
            Shutdown();
        }

        private static void OnSshExited(object sender, EventArgs args)
        {
            if (_shuttingDown || !ProxyRequired) return;
            SetError("tunnel", new IOException("SSH tunnel exited"));
        }
    }
}
