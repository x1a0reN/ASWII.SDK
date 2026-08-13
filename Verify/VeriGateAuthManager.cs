using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using ASWDEBUG.Logger;
using ASWDEBUG.Main;
using UnityEngine;

namespace ASWDEBUG.Verify
{
    public sealed class VeriGateAuthManager : MonoBehaviour
    {
        public static VeriGateAuthManager Instance { get; private set; }

        public volatile bool LoggedIn;
        public string LastError;
        public string StaticExpiredText;
        public string DeviceID;
        public string SessionID;

        private readonly Queue<Action> _mainQueue = new Queue<Action>();
        private readonly object _queueLock = new object();
        private readonly object _clientLock = new object();
        private readonly object _commandLock = new object();
        private readonly List<VeriGateCommandResult> _pendingResults =
            new List<VeriGateCommandResult>();
        private readonly HashSet<string> _knownCommands = new HashSet<string>();
        private readonly Queue<string> _commandOrder = new Queue<string>();
        private VeriGateClient _client;
        private Coroutine _heartbeatCoroutine;
        private volatile bool _heartbeatInFlight;
        private int _heartbeatFailures;

        private void Awake()
        {
            if (Instance != null && Instance != this)
            {
                Destroy(gameObject);
                return;
            }
            Instance = this;
            DontDestroyOnLoad(gameObject);
            gameObject.hideFlags = HideFlags.HideAndDontSave;
        }

        private void Update()
        {
            Action action = null;
            lock (_queueLock)
            {
                if (_mainQueue.Count > 0) action = _mainQueue.Dequeue();
            }
            if (action != null)
            {
                try { action(); } catch { }
            }
        }

        private void OnDestroy()
        {
            StopHeartbeat();
            lock (_clientLock)
            {
                if (_client != null)
                {
                    try { _client.Logout(); } catch { }
                    _client.Dispose();
                    _client = null;
                }
            }
            if (Instance == this) Instance = null;
        }

        public void RunAutoLogin(Action<bool, string> onDone)
        {
            Thread thread = new Thread(new ThreadStart(delegate
            {
                try
                {
                    if (!Doorstop.Entrypoint.WaitForAuthorizationHandoff(60000))
                        throw new InvalidOperationException(
                            "未收到登录器的授权交接，DLL 已停止登录。");
                    Doorstop.Entrypoint.LogInfo(
                        "VeriGate authorization handoff received");
                    string directCard = VeriGateCredentialStore.Load();
                    Doorstop.Entrypoint.LogInfo(
                        "VeriGate one-time credential loaded");
                    VeriGateClient client = VeriGateClient.Open(directCard);
                    Doorstop.Entrypoint.LogInfo(
                        "VeriGate native client opened");
                    VeriGateAuthorization authorization;
                    try
                    {
                        authorization = client.Authorize(
                            DllUsageTelemetry.Capture());
                        Doorstop.Entrypoint.LogInfo(
                            "VeriGate authorization completed allowed=" +
                            authorization.Allowed +
                            " terminate=" + authorization.Terminate);
                    }
                    catch
                    {
                        client.Dispose();
                        throw;
                    }

                    lock (_clientLock)
                    {
                        if (_client != null) _client.Dispose();
                        _client = client;
                    }
                    DeviceID = authorization.DeviceID;
                    SessionID = authorization.SessionID;
                    StaticExpiredText = authorization.PrincipalExpiresAt.ToLocalTime()
                        .ToString("yyyy-MM-dd HH:mm:ss");
                    LoggedIn = authorization.Allowed;
                    LastError = null;

                    PostMain(delegate
                    {
                        if (authorization.Terminate)
                        {
                            TerminateClient(
                                authorization.TerminationReason ??
                                "ADMIN_TERMINATED");
                            return;
                        }
                        DispatchCommands(authorization.Commands);
                        StartHeartbeat(10f);
                        if (onDone != null) onDone(true, null);
                    });
                }
                catch (Exception error)
                {
                    LoggedIn = false;
                    LastError = error.Message;
                    FileLogger.Log("AUTH", "VeriGate 登录失败：" + error.Message);
                    VeriGateException veriGateError =
                        error as VeriGateException;
                    Doorstop.Entrypoint.LogInfo(
                        "VeriGate authorization failed: " +
                        error.GetType().Name +
                        (veriGateError == null
                            ? string.Empty
                            : " error_code=" + veriGateError.ErrorCode) +
                        ": " + error.Message);
                    PostMain(delegate
                    {
                        if (onDone != null) onDone(false, error.Message);
                        TerminateClient("AUTHORIZATION_FAILED");
                    });
                }
            }));
            thread.IsBackground = true;
            thread.Name = "VeriGateAuthorize";
            thread.Start();
        }

        public void StartHeartbeat(float seconds)
        {
            StopHeartbeat();
            _heartbeatFailures = 0;
            _heartbeatCoroutine = StartCoroutine(HeartbeatLoop(seconds));
        }

        public void StopHeartbeat()
        {
            if (_heartbeatCoroutine != null)
            {
                StopCoroutine(_heartbeatCoroutine);
                _heartbeatCoroutine = null;
            }
            _heartbeatInFlight = false;
            _heartbeatFailures = 0;
        }

        private IEnumerator HeartbeatLoop(float interval)
        {
            float waitSeconds = Mathf.Max(10f, interval);
            Doorstop.Entrypoint.LogInfo(
                "VeriGate heartbeat scheduled interval_seconds=" + waitSeconds);
            while (true)
            {
                if (!_heartbeatInFlight && LoggedIn)
                {
                    _heartbeatInFlight = true;
                    ThreadPool.QueueUserWorkItem(delegate
                    {
                        bool ok = false;
                        bool terminate = false;
                        bool terminalFailure = false;
                        string failure = null;
                        string terminationReason = null;
                        VeriGateRemoteCommand[] commands = null;
                        List<VeriGateCommandResult> sentResults =
                            SnapshotCommandResults();
                        try
                        {
                            VeriGateAuthorization result;
                            lock (_clientLock)
                            {
                                if (_client == null)
                                    throw new InvalidOperationException("VeriGate 客户端未初始化。");
                                result = _client.Heartbeat(
                                    DllUsageTelemetry.Capture(),
                                    sentResults);
                            }
                            ok = result.Allowed;
                            terminate = result.Terminate;
                            terminationReason = result.TerminationReason;
                            commands = result.Commands;
                            RemoveAcknowledgedResults(sentResults);
                        }
                        catch (Exception error)
                        {
                            failure = error.Message;
                            terminalFailure = IsTerminalFailure(error);
                        }

                        PostMain(delegate
                        {
                            _heartbeatInFlight = false;
                            if (terminate)
                            {
                                TerminateClient(
                                    terminationReason ?? "ADMIN_TERMINATED");
                                return;
                            }
                            if (ok)
                            {
                                _heartbeatFailures = 0;
                                DispatchCommands(commands);
                                return;
                            }

                            _heartbeatFailures++;
                            LastError = failure ?? "心跳验证失败。";
                            FileLogger.Log(
                                "AUTH",
                                "VeriGate 心跳失败，连续次数=" + _heartbeatFailures +
                                "，原因=" + LastError);
                            if (!terminalFailure && _heartbeatFailures < 3) return;

                            TerminateClient(
                                terminalFailure
                                    ? "AUTHORIZATION_REVOKED"
                                    : "HEARTBEAT_UNAVAILABLE");
                        });
                    });
                }
                float waitUntil = Time.realtimeSinceStartup + waitSeconds;
                while (Time.realtimeSinceStartup < waitUntil)
                    yield return null;
            }
        }

        private void PostMain(Action action)
        {
            lock (_queueLock) _mainQueue.Enqueue(action);
        }

        private List<VeriGateCommandResult> SnapshotCommandResults()
        {
            lock (_commandLock)
                return new List<VeriGateCommandResult>(_pendingResults);
        }

        private void RemoveAcknowledgedResults(
            IList<VeriGateCommandResult> acknowledged)
        {
            if (acknowledged == null || acknowledged.Count == 0) return;
            lock (_commandLock)
            {
                for (int i = 0; i < acknowledged.Count; i++)
                {
                    string commandID = acknowledged[i].CommandID;
                    _pendingResults.RemoveAll(delegate(VeriGateCommandResult item)
                    {
                        return string.Equals(
                            item.CommandID,
                            commandID,
                            StringComparison.Ordinal);
                    });
                }
            }
        }

        private void DispatchCommands(VeriGateRemoteCommand[] commands)
        {
            if (commands == null || commands.Length == 0) return;
            for (int i = 0; i < commands.Length; i++)
            {
                VeriGateRemoteCommand command = commands[i];
                if (command == null || string.IsNullOrEmpty(command.CommandID))
                    continue;
                lock (_commandLock)
                {
                    if (_knownCommands.Contains(command.CommandID)) continue;
                    _knownCommands.Add(command.CommandID);
                    _commandOrder.Enqueue(command.CommandID);
                    while (_commandOrder.Count > 2048)
                        _knownCommands.Remove(_commandOrder.Dequeue());
                }
                PostMain(delegate { ExecuteCommand(command); });
            }
        }

        private void ExecuteCommand(VeriGateRemoteCommand command)
        {
            var result = new VeriGateCommandResult
            {
                CommandID = command.CommandID
            };
            try
            {
                result.Result = RemoteCommandExecutor.Execute(command);
                result.Status = "succeeded";
                FileLogger.Log(
                    "REMOTE",
                    "远程命令执行成功：" + command.CommandID + " " +
                    command.CommandType + " " + command.Target);
            }
            catch (RemoteCommandExecutionException error)
            {
                result.Status = "failed";
                result.ErrorCode = error.Code;
                result.Result = error.Message;
                FileLogger.Log(
                    "REMOTE",
                    "远程命令执行失败：" + command.CommandID + " " +
                    error.Code + " " + error.Message);
            }
            catch (Exception error)
            {
                result.Status = "failed";
                result.ErrorCode = "COMMAND_FAILED";
                result.Result = error.GetType().Name + ": " + error.Message;
                FileLogger.Log(
                    "REMOTE",
                    "远程命令执行异常：" + command.CommandID + " " +
                    result.Result);
            }
            lock (_commandLock) _pendingResults.Add(result);
        }

        private static bool IsTerminalFailure(Exception error)
        {
            VeriGateException verification = error as VeriGateException;
            return verification != null &&
                verification.ErrorCode >= 2 &&
                verification.ErrorCode <= 6;
        }

        private void TerminateClient(string reason)
        {
            if (!LoggedIn && string.Equals(
                LastError,
                "客户端已被强制关闭。",
                StringComparison.Ordinal))
                return;
            LoggedIn = false;
            LastError = "客户端已被强制关闭。";
            StopHeartbeat();
            FileLogger.Log(
                "AUTH",
                "VeriGate 终止客户端进程，原因=" + (reason ?? "UNKNOWN"));
            Doorstop.Entrypoint.LogInfo(
                "VeriGate terminating client process reason=" +
                (reason ?? "UNKNOWN"));
            try { Application.Quit(); } catch { }
            try { Process.GetCurrentProcess().Kill(); } catch { }
        }

    }
}
