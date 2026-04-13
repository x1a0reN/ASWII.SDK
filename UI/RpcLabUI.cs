// RpcLabUI.cs
// 需要：UnityEngine、Assembly-CSharp（LobbyConnection 等）、UniLua（用于判错）
// 把本文件放进你的工程（比如和 SearchPanel 同目录）

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
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
        private static Vector2 _logScroll;

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
            if (!Visible || !CheatUIManager.MenuVisible) return;
            _winRect = new Rect(x, y, w, h);

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
            float minColH = 320f;
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

            // ===== 底部：大日志区（随 A/B 高度下移 & 占满剩余） =====
            float logTop = cy + colH + 10f;
            float logH = Mathf.Max(60f, ch - colH - 10f);
            DrawBox(new Rect(cx, logTop, cw, logH));
            GUILayout.BeginArea(new Rect(cx + 6, logTop + 6, cw - 12, logH - 12));
            GUILayout.BeginHorizontal();
            GUILayout.Label("C) 实时日志", LabelBold());
            GUILayout.FlexibleSpace();
            if (GUILayout.Button("Clear", Button(), GUILayout.Width(80))) _log.Clear();
            GUILayout.EndHorizontal();

            GUILayout.Space(4);
            _logScroll = GUILayout.BeginScrollView(_logScroll, GUILayout.ExpandHeight(true));
            string joined = (_log.Count == 0) ? "(无输出)" : string.Join("\n", _log.ToArray());
            GUILayout.TextArea(joined, TextArea(), GUILayout.ExpandHeight(true));
            GUILayout.EndScrollView();
            GUILayout.EndArea();
        }

        // ====== 对外：追加日志 ======
        public static void Append(string line)
        {
            if (string.IsNullOrEmpty(line)) return;
            if (_log.Count > 8000) _log.RemoveRange(0, 4000);
            _log.Add("[" + DateTime.Now.ToString("HH:mm:ss.fff") + "] " + line);
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
