using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using ASWDEBUG.Logger;
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
                    string directCard = VeriGateCredentialStore.Load();
                    VeriGateClient client = VeriGateClient.Open(directCard);
                    VeriGateAuthorization authorization;
                    try
                    {
                        authorization = client.Authorize();
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
                    StaticExpiredText = authorization.SessionExpiresAt.ToLocalTime()
                        .ToString("yyyy-MM-dd HH:mm:ss");
                    LoggedIn = authorization.Allowed;
                    LastError = null;

                    PostMain(delegate
                    {
                        StartHeartbeat(30f);
                        if (onDone != null) onDone(true, null);
                    });
                }
                catch (Exception error)
                {
                    LoggedIn = false;
                    LastError = error.Message;
                    FileLogger.Log("AUTH", "VeriGate 登录失败：" + error.Message);
                    PostMain(delegate
                    {
                        if (onDone != null) onDone(false, error.Message);
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
            while (true)
            {
                if (!_heartbeatInFlight && LoggedIn)
                {
                    _heartbeatInFlight = true;
                    ThreadPool.QueueUserWorkItem(delegate
                    {
                        bool ok = false;
                        string failure = null;
                        try
                        {
                            VeriGateAuthorization result;
                            lock (_clientLock)
                            {
                                if (_client == null)
                                    throw new InvalidOperationException("VeriGate 客户端未初始化。");
                                result = _client.Heartbeat();
                            }
                            ok = result.Allowed;
                        }
                        catch (Exception error)
                        {
                            failure = error.Message;
                        }

                        PostMain(delegate
                        {
                            _heartbeatInFlight = false;
                            if (ok)
                            {
                                _heartbeatFailures = 0;
                                return;
                            }

                            _heartbeatFailures++;
                            LastError = failure ?? "心跳验证失败。";
                            FileLogger.Log(
                                "AUTH",
                                "VeriGate 心跳失败，连续次数=" + _heartbeatFailures +
                                "，原因=" + LastError);
                            if (_heartbeatFailures < 3) return;

                            LoggedIn = false;
                            FileLogger.Log("AUTH", "VeriGate 心跳连续失败，终止当前进程。");
                            try { Application.Quit(); } catch { }
                            try { Process.GetCurrentProcess().Kill(); } catch { }
                        });
                    });
                }
                yield return WaitRealtime(waitSeconds);
            }
        }

        private IEnumerator WaitRealtime(float seconds)
        {
            float end = Time.realtimeSinceStartup + Mathf.Max(0f, seconds);
            while (Time.realtimeSinceStartup < end) yield return null;
        }

        private void PostMain(Action action)
        {
            lock (_queueLock) _mainQueue.Enqueue(action);
        }

    }
}
