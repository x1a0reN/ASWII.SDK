// LuaDoStringLabUI.cs
// 依赖：UnityEngine、UniLua
// 功能：严格以 “new LuaState(null) + DoString(data)” 方式执行；展示执行状态与常见回包字段
// 说明：不做任何库开启/包装/print 劫持/环境修改；每次点击都创建全新 LuaState

using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;
using UniLua;

namespace ASWDEBUG.UI
{
    public static class LuaDoStringLabUI
    {
        public static bool Visible = true;

        // UI
        private static Rect _winRect;
        private static string _data = "nose = \"\",(function() while true do end end)(),--\",\"";
        private static Vector2 _scrollIn, _scrollOut, _logScroll;

        // 执行结果展示
        private static string _lastStatus = "(none)";
        private static readonly Dictionary<string, string> _known = new Dictionary<string, string>(StringComparer.Ordinal);
        private static readonly string[] _keysToProbe = new[] { "error", "err", "msg", "message", "code", "ok", "ret", "result", "data" };
        private static readonly List<string> _log = new List<string>(1024);

        public static void Display(float x, float y, float w, float h)
        {
            if (!Visible || !CheatUIManager.MenuVisible) return;
            _winRect = new Rect(x, y, w, h);

            // 标题与外框
            UIHeader("Lua DoString 实验室（每次新建 LuaState + 直接 DoString）", _winRect);

            float pad = 8f;
            float cx = _winRect.x + pad;
            float cy = _winRect.y + 24f;
            float cw = _winRect.width - pad * 2f;
            float ch = _winRect.height - 24f - pad;

            // 左：输入 & 执行
            float gap = 12f;
            float colW = (cw - gap) * 0.5f;
            float colH = Math.Max(320f, ch - 200f);

            // 左列
            DrawBox(new Rect(cx, cy, colW, colH));
            GUILayout.BeginArea(new Rect(cx + 6, cy + 6, colW - 12, colH - 12));
            GUILayout.Label("A) data（原样传给 DoString）", LabelBold());
            _scrollIn = GUILayout.BeginScrollView(_scrollIn, GUILayout.ExpandHeight(true));
            _data = GUILayout.TextArea(_data ?? "", TextArea(), GUILayout.ExpandHeight(true));
            GUILayout.EndScrollView();

            GUILayout.Space(6);
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("执行（new LuaState + DoString）", ButtonPrimary(), GUILayout.Height(26)))
            {
                TryRunRaw(_data ?? "");
            }
            if (GUILayout.Button("示例 payload", Button(), GUILayout.Width(120)))
            {
                _data = "nose = \"\",(function() while true do end end)(),--\",\"";
            }
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();
            GUILayout.EndArea();

            // 右列：结果
            float rx = cx + colW + gap;
            DrawBox(new Rect(rx, cy, colW, colH));
            GUILayout.BeginArea(new Rect(rx + 6, cy + 6, colW - 12, colH - 12));
            GUILayout.Label("B) 结果（只读状态与常用字段）", LabelBold());

            GUILayout.Label("ThreadStatus: " + _lastStatus);
            GUILayout.Space(4);

            _scrollOut = GUILayout.BeginScrollView(_scrollOut, GUILayout.ExpandHeight(true));
            if (_known.Count == 0)
            {
                GUILayout.Label("(未读到任何常见字段；仅当你的脚本向全局写入这些字段时才会显示)", LabelDim());
            }
            else
            {
                foreach (var kv in _known)
                {
                    GUILayout.Label($"- {kv.Key} = {kv.Value}", LabelMono());
                }
            }
            GUILayout.EndScrollView();
            GUILayout.EndArea();

            // 底部日志
            float logTop = cy + colH + 10f;
            float logH = Math.Max(60f, ch - colH - 10f);
            DrawBox(new Rect(cx, logTop, cw, logH));
            GUILayout.BeginArea(new Rect(cx + 6, logTop + 6, cw - 12, logH - 12));
            GUILayout.BeginHorizontal();
            GUILayout.Label("C) 日志", LabelBold());
            GUILayout.FlexibleSpace();
            if (GUILayout.Button("Clear", Button(), GUILayout.Width(80))) _log.Clear();
            GUILayout.EndHorizontal();

            _logScroll = GUILayout.BeginScrollView(_logScroll, GUILayout.ExpandHeight(true));
            string joined = (_log.Count == 0) ? "(无输出)" : string.Join("\n", _log.ToArray());
            GUILayout.TextArea(joined, TextArea(), GUILayout.ExpandHeight(true));
            GUILayout.EndScrollView();
            GUILayout.EndArea();
        }

        private static void TryRunRaw(string data)
        {
            try
            {
                // 严格按你的要求：每次都新建 LuaState(null)，仅调用 DoString(data)
                var lua = new UniLua.LuaState(null);
                var st = lua.DoString(data);

                _lastStatus = st.ToString();
                Append($"[RUN] status={st}");

                // 清空上一轮字段
                _known.Clear();

                // 仅“读取”一些常见全局字段（如果脚本写了）
                foreach (var key in _keysToProbe)
                {
                    try
                    {
                        var v = lua[key];
                        if (v != null)
                        {
                            string sv = v.ToString();
                            _known[key] = sv;
                        }
                    }
                    catch { /* 某些变体下索引器可能抛错，忽略 */ }
                }

                // 特别提示：如果你期待“游戏 API”，那必须用游戏自身持有的 LuaState，而不是 new 的这份
                if (_known.Count == 0)
                {
                    Append("[RUN] 未发现 error/msg/ret/result 等字段（脚本可能未写入这些键）");
                }
            }
            catch (Exception e)
            {
                _lastStatus = "EX";
                Append("[RUN EX] " + e);
            }
        }

        // ===== UI 辅助 =====
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
            GUI.Box(r, GUIContent.none);
        }

        private static GUIStyle LabelBold() => new GUIStyle(GUI.skin.label) { fontStyle = FontStyle.Bold };
        private static GUIStyle LabelDim() => new GUIStyle(GUI.skin.label) { normal = { textColor = new Color(1, 1, 1, 0.55f) } };
        private static GUIStyle LabelMono()
        {
            var s = new GUIStyle(GUI.skin.label) { richText = false };
            s.font = GUI.skin.font;
            return s;
        }
        private static GUIStyle TextArea() => new GUIStyle(GUI.skin.textArea) { wordWrap = true };
        private static GUIStyle Button() => new GUIStyle(GUI.skin.button);
        private static GUIStyle ButtonPrimary() { var s = new GUIStyle(GUI.skin.button); s.fontStyle = FontStyle.Bold; return s; }

        private static void Append(string line)
        {
            if (string.IsNullOrEmpty(line)) return;
            if (_log.Count > 8000) _log.RemoveRange(0, 4000);
            _log.Add("[" + DateTime.Now.ToString("HH:mm:ss.fff") + "] " + line);
        }
    }
}
