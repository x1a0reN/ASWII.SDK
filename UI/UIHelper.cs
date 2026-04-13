using System;
using UnityEngine;
using System.Collections.Generic;
using Action = System.Action;

namespace ASWDEBUG.UI
{
    public class UIHelper
    {
        // 不使用 C# 9 的 target-typed new，也避免在字段初始化阶段访问 GUI.skin
        public static GUIStyle StringStyle { get; set; }

        private static float
            x, y,
            width, height,
            margin,
            controlHeight,
            controlDist,
            nextControlY;

        private static Texture2D _panelOff;
        private static Texture2D _panelOn;

        public static GUIStyle SliderStyle;
        public static GUIStyle SliderThumbStyle;
        private static GUIStyle _boxStyle;
        private static GUIStyle _titlebarStyle;
        public static GUIStyle TextFieldStyle;
        private static GUIStyle _centeredLabelStyle;
        public static GUIStyle ButtonStyle;
        public static GUIStyle SpawnerButtonStyle;
        public static GUIStyle PanelStyle;

        // 读取 UIHelper 原始前景色（若没初始化则退回到 GUI.skin）
        private static Color UiTextColor =>
            (UIHelper.StringStyle != null && UIHelper.StringStyle.normal != null)
                ? UIHelper.StringStyle.normal.textColor
                : GUI.skin.label.normal.textColor;

        // —— 统一主题色（保持你原样）——
        static readonly Color ThemeBg = new Color(42f / 255f, 42f / 255f, 42f / 255f);
        static readonly Color ThemeBgHover = new Color(50f / 255f, 50f / 255f, 50f / 255f);
        static readonly Color ThemeBgActive = new Color(60f / 255f, 60f / 255f, 60f / 255f);

        static Color onColor = new Color(231f / 255f, 18f / 255f, 0f / 255f);
        static Color offColor = Color.white;

        // ===== 零污染绘制：彩色 1×1 纹理缓存 =====
        private static readonly Dictionary<Color32, Texture2D> _solidTexCache = new Dictionary<Color32, Texture2D>(64);
        private static Texture2D SolidTex(Color c)
        {
            var k = (Color32)c;
            if (_solidTexCache.TryGetValue(k, out var tex) && tex) return tex;

            tex = new Texture2D(1, 1, TextureFormat.RGBA32, false);
            tex.SetPixel(0, 0, c);
            tex.Apply();
            tex.hideFlags = HideFlags.HideAndDontSave;
            tex.wrapMode = TextureWrapMode.Clamp;
            tex.filterMode = FilterMode.Point;
            _solidTexCache[k] = tex;
            return tex;
        }

        public static void InitializeStyles()
        {
            var texBg = MakeTex(1, 1, ThemeBg); texBg.hideFlags = HideFlags.HideAndDontSave;
            var texBgHover = MakeTex(1, 1, ThemeBgHover); texBgHover.hideFlags = HideFlags.HideAndDontSave;
            var texBgActive = MakeTex(1, 1, ThemeBgActive); texBgActive.hideFlags = HideFlags.HideAndDontSave;
            var texTransparent = MakeTex(1, 1, new Color(0f, 0f, 0f, 0f)); // A=0

            // 纹理全部走自建，避免直接改动内置 blackTexture 的 hideFlags（可能产生警告）
            _panelOff = MakeTex(1, 1, new Color(82f / 255f, 82f / 255f, 82f / 255f));
            _panelOff.hideFlags = HideFlags.HideAndDontSave;

            _panelOn = MakeTex(1, 1, new Color(231f / 255f, 18f / 255f, 0f / 255f));
            _panelOn.hideFlags = HideFlags.HideAndDontSave;

            PanelStyle = new GUIStyle(GUI.skin.label);
            PanelStyle.normal.background = _panelOff;

            _boxStyle = new GUIStyle(GUI.skin.box);
            _boxStyle.normal.background = texTransparent;
            _boxStyle.normal.background.hideFlags = HideFlags.HideAndDontSave;
            _boxStyle.border = new RectOffset(0, 0, 0, 0); // 防止九宫格边框留痕

            _titlebarStyle = new GUIStyle(GUI.skin.box);
            _titlebarStyle.normal.background = MakeTex(1, 1, new Color(231f / 255f, 18f / 255f, 0f / 255f));
            _titlebarStyle.normal.background.hideFlags = HideFlags.HideAndDontSave;

            _centeredLabelStyle = new GUIStyle(GUI.skin.label);
            _centeredLabelStyle.alignment = TextAnchor.MiddleCenter;

            SpawnerButtonStyle = new GUIStyle(GUI.skin.button);
            // 用我们自己的深色背景，而不是直接修改 blackTexture 的 hideFlags
            SpawnerButtonStyle.normal.background = MakeTex(1, 1, Color.black);
            SpawnerButtonStyle.hover.background = MakeTex(1, 1, new Color(18f / 255f, 18f / 255f, 18f / 255f));
            SpawnerButtonStyle.active.background = MakeTex(1, 1, new Color(18f / 255f, 18f / 255f, 18f / 255f));

            ButtonStyle = new GUIStyle(GUI.skin.button);
            ButtonStyle.alignment = TextAnchor.MiddleLeft;
            ButtonStyle.padding = new RectOffset(4, 0, 0, 0);
            ButtonStyle.normal.background = texBg;
            ButtonStyle.hover.background = texBgHover;
            ButtonStyle.active.background = texBgActive;

            TextFieldStyle = new GUIStyle(GUI.skin.textField);
            TextFieldStyle.normal.background = MakeTex(2, 2, new Color(28f / 255f, 28f / 255f, 28f / 255f));
            TextFieldStyle.hover.background = MakeTex(2, 2, new Color(18f / 255f, 18f / 255f, 18f / 255f));
            TextFieldStyle.focused.background = MakeTex(2, 2, new Color(28f / 255f, 28f / 255f, 28f / 255f));

            // —— Slider —— 与按钮同一套深色主题
            var texTrack = MakeTex(1, 6, ThemeBg);
            var texTrackHover = MakeTex(1, 6, ThemeBgHover);
            var texTrackDown = MakeTex(1, 6, ThemeBgActive);

            SliderStyle = new GUIStyle(GUI.skin.horizontalSlider);
            SliderStyle.normal.background = texTrack;
            SliderStyle.hover.background = texTrackHover;
            SliderStyle.active.background = texTrackDown;
            SliderStyle.fixedHeight = 14;                 // 轨道高度
            SliderStyle.border = new RectOffset(2, 2, 2, 2);

            SliderThumbStyle = new GUIStyle(GUI.skin.horizontalSliderThumb);
            SliderThumbStyle.normal.background = MakeTex(8, 16, offColor);
            SliderThumbStyle.hover.background = MakeTex(8, 16, Color.Lerp(offColor, Color.white, 0.08f));
            SliderThumbStyle.active.background = MakeTex(8, 16, onColor);
            SliderThumbStyle.fixedWidth = 10;
            SliderThumbStyle.fixedHeight = 16;
            SliderThumbStyle.border = new RectOffset(2, 2, 2, 2);


            if (StringStyle == null)
                StringStyle = new GUIStyle(GUI.skin.label);
        }

        public static void Begin(string text, float _x, float _y, float _width, float _height, float _margin, float _controlHeight, float _controlDist)
        {
            x = _x;
            y = _y;
            width = _width;
            height = _height;
            margin = _margin;
            controlHeight = _controlHeight;
            controlDist = _controlDist;
            nextControlY = 20f;

            var s = new GUIStyle(UIHelper.StringStyle ?? GUI.skin.label);
            s.alignment = TextAnchor.MiddleLeft;
            s.fontSize = 13;
            s.normal.textColor = UiTextColor;

            GUI.Box(new Rect(x, y, width, height), string.Empty, _boxStyle);
            GUI.Box(new Rect(x, y, width, 20f), string.Empty, _titlebarStyle);
            GUI.Label(new Rect(x, y, width, 20f), "<b>" + text + "</b>", _centeredLabelStyle);
        }

        private static Rect NextControlRect()
        {
            Rect r = new Rect(x + margin, nextControlY + y, width - margin * 2f, controlHeight);
            nextControlY += controlHeight + controlDist;
            return r;
        }

        private static string MakeEnable(string text, bool state)
        {
            PanelStyle.normal.background = state ? _panelOn : _panelOff;

            Color textColor = state ? onColor : offColor;

            int r = (int)(textColor.r * 255f);
            int g = (int)(textColor.g * 255f);
            int b = (int)(textColor.b * 255f);

            string colorTag = "#" + r.ToString("X2") + g.ToString("X2") + b.ToString("X2");

            return "<color=" + colorTag + ">" + text + "</color>";
        }

        public static void Button(string text, bool state, Action function)
        {
            Rect nextControlRect = NextControlRect();
            bool clicked = GUI.Button(nextControlRect, MakeEnable(text, state), ButtonStyle);
            // 右侧小色条（贴图绘制，不改 GUI.color）
            var barTex = PanelStyle.normal.background != null ? PanelStyle.normal.background : SolidTex(Color.white);
            GUI.DrawTexture(new Rect(nextControlRect.xMax - 6f, nextControlRect.y + 3f, 3f, nextControlRect.height - 6f), barTex, ScaleMode.StretchToFill);
            if (clicked && function != null) function();
        }

        public static bool Button(string text)
        {
            return GUI.Button(NextControlRect(), text, ButtonStyle);
        }
        public static float SliderRow(string label, float value, float min, float max, int decimals = 0)
        {
            // 两行高度：第一行文字/数值，第二行滑块
            float row1 = controlHeight;
            float row2 = Mathf.Max(14f, controlHeight - 6f);

            Rect r = NextRectFlexible(row1 + row2 + controlDist);

            // 顶部：左侧标签，右侧数值
            var left = new GUIStyle(StringStyle ?? GUI.skin.label) { alignment = TextAnchor.MiddleLeft, fontSize = 13 };
            var right = new GUIStyle(left) { alignment = TextAnchor.MiddleRight };

            Rect rTop = new Rect(r.x, r.y, r.width, row1);
            GUI.Label(new Rect(rTop.x + 8f, rTop.y, rTop.width * 0.6f - 8f, rTop.height), label, left);
            GUI.Label(new Rect(rTop.x + rTop.width * 0.6f, rTop.y, rTop.width * 0.4f - 8f, rTop.height),
                      Math.Round(value, decimals).ToString(), right);

            // 底部：滑块（含进度填充，颜色与 onColor 协调）
            float pad = 8f;
            Rect rSlider = new Rect(r.x + pad, r.y + row1 + (row2 - SliderStyle.fixedHeight) * 0.5f,
                                    r.width - pad * 2f, SliderStyle.fixedHeight);

            // 进度条填充（贴图，不改 GUI.color）
            float t = Mathf.InverseLerp(min, max, value);
            var fillTex = SolidTex(new Color(onColor.r, onColor.g, onColor.b, 0.35f));
            float fillH = 6f;
            GUI.DrawTexture(new Rect(rSlider.x, rSlider.y + (rSlider.height - fillH) * 0.5f, rSlider.width * t, fillH), fillTex);

            // 真正的滑块
            value = GUI.HorizontalSlider(rSlider, value, min, max, SliderStyle, SliderThumbStyle);
            return value;
        }

        public static void Label(string text, float value, int decimals = 2)
        {
            Label(string.Format("{0}{1}", text, Math.Round(value, decimals).ToString()));
        }

        public static void Label(string text)
        {
            GUI.Label(NextControlRect(), text, _centeredLabelStyle);
        }

        public static void LabelAuto(string text, int fontSize = 13, TextAnchor align = TextAnchor.MiddleLeft, bool richText = true)
        {
            var style = new GUIStyle(_centeredLabelStyle ?? GUI.skin.label)
            {
                alignment = align,
                fontSize = fontSize,
                wordWrap = true,
                richText = richText,
                clipping = TextClipping.Overflow
            };

            // 先占位一个超小高度，算出真实高度
            Rect r = new Rect(x + margin, y + nextControlY, width - margin * 2f, 10f);
            float needH = style.CalcHeight(new GUIContent(text ?? string.Empty), r.width);
            r.height = needH;

            // 推进布局
            nextControlY += needH + controlDist;

            GUI.Label(r, text ?? string.Empty, style);
        }

        public static float Slider(float val, float min, float max)
        {
            return GUI.HorizontalSlider(NextControlRect(), val, min, max);
        }

        /// <summary>
        /// Draw string on screen
        /// </summary>
        public static bool DrawString(Vector2 position, string label, Color color, int fontSize, bool centered = true)
        {
            // 基样式：用你自定义的 StringStyle；没有就用 skin 的 label
            var baseStyle = StringStyle ?? GUI.skin.label;

            // 克隆一个临时样式，避免改到全局
            var style = new GUIStyle(baseStyle)
            {
                fontSize = fontSize,
                alignment = centered ? TextAnchor.MiddleCenter : TextAnchor.UpperLeft,
                richText = baseStyle.richText,   // 保持原有 richText 设置
                wordWrap = baseStyle.wordWrap
            };

            // 关键：给 normal 换一份新的 state，只设置文本颜色，不碰到全局的 state
            style.normal = new GUIStyleState
            {
                background = null,    // label 无需背景
                textColor = color
            };

            var gc = new GUIContent(label ?? string.Empty);
            var size = style.CalcSize(gc);
            Vector2 pos = centered ? (position - size / 2f) : position;

            GUI.Label(new Rect(pos.x, pos.y, size.x, size.y), gc, style);
            return true;
        }


        // ======== 零污染绘制替换：不再改 GUI.color ========

        public static void DrawLine(Vector2 start, Vector2 end, Color color, float lineWidth)
        {
            if (Event.current.type != EventType.Repaint) return;

            const float Rad2Deg = 57.29577951308232f;
            Vector2 v = end - start;
            if (v.sqrMagnitude <= 0.000001f) return;

            float ang = Rad2Deg * Mathf.Atan2(v.y, v.x);
            int half = Mathf.CeilToInt(lineWidth * 0.5f);

            // 记录并恢复 GUI.depth
            int prevDepth = GUI.depth;
            GUI.depth = 0;

            GUIUtility.RotateAroundPivot(ang, start);
            try
            {
                var tex = SolidTex(color);
                GUI.DrawTexture(new Rect(start.x, start.y - half, v.magnitude, lineWidth), tex, ScaleMode.StretchToFill);
            }
            finally
            {
                GUIUtility.RotateAroundPivot(-ang, start);
                GUI.depth = prevDepth;
            }
        }

        public static void DrawBox(Vector2 position, Vector2 size, Color color, bool centered = true)
        {
            if (Event.current.type != EventType.Repaint) return;

            Vector2 pos = centered ? (position - size / 2f) : position;
            var tex = SolidTex(color);
            GUI.DrawTexture(new Rect(pos.x, pos.y, size.x, size.y), tex, ScaleMode.StretchToFill);
        }

        public static void DrawBoxOutline(Vector2 point, float w, float h, Color color)
        {
            var tex = SolidTex(color);
            // 上
            GUI.DrawTexture(new Rect(point.x, point.y, w, 2f), tex);
            // 左
            GUI.DrawTexture(new Rect(point.x, point.y, 2f, h), tex);
            // 右
            GUI.DrawTexture(new Rect(point.x + w - 2f, point.y, 2f, h), tex);
            // 下
            GUI.DrawTexture(new Rect(point.x, point.y + h - 2f, w, 2f), tex);
        }

        /// <summary>
        /// 在 IMGUI（OnGUI）层从 8 个世界坐标点绘制 3D 盒子的 12 条边
        /// </summary>
        public static void Draw3DBox(Vector3[] worldCorners, Camera camera, Color color, float lineWidth)
        {
            if (Event.current.type != EventType.Repaint) return;

            if (camera == null || worldCorners == null || worldCorners.Length != 8) return;

            Vector2[] screen = new Vector2[8];
            bool[] visible = new bool[8];

            for (int i = 0; i < 8; i++)
            {
                Vector3 sp = camera.WorldToScreenPoint(worldCorners[i]);
                if (sp.z > 0f) // 在相机前方才绘制
                {
                    sp.y = (float)Screen.height - sp.y; // 转为 IMGUI 的屏幕坐标（y 翻转）
                    screen[i] = new Vector2(sp.x, sp.y);
                    visible[i] = true;
                }
                else
                {
                    visible[i] = false;
                }
            }

            DrawEdge(0, 1, screen, visible, color, lineWidth);
            DrawEdge(1, 2, screen, visible, color, lineWidth);
            DrawEdge(2, 3, screen, visible, color, lineWidth);
            DrawEdge(3, 0, screen, visible, color, lineWidth);

            DrawEdge(4, 5, screen, visible, color, lineWidth);
            DrawEdge(5, 6, screen, visible, color, lineWidth);
            DrawEdge(6, 7, screen, visible, color, lineWidth);
            DrawEdge(7, 4, screen, visible, color, lineWidth);

            DrawEdge(0, 4, screen, visible, color, lineWidth);
            DrawEdge(1, 5, screen, visible, color, lineWidth);
            DrawEdge(2, 6, screen, visible, color, lineWidth);
            DrawEdge(3, 7, screen, visible, color, lineWidth);
        }

        /// <summary>
        /// 从世界空间 Bounds（一般用 renderer.bounds）直接绘制 3D 盒子
        /// </summary>
        public static void Draw3DBoxFromBounds(Bounds worldBounds, Camera camera, Color color, float lineWidth)
        {
            Vector3 c = worldBounds.center;
            Vector3 e = worldBounds.extents;

            // 8 个角点（世界坐标）
            Vector3[] corners = new Vector3[8];
            corners[0] = c + new Vector3(-e.x, e.y, -e.z);
            corners[1] = c + new Vector3(e.x, e.y, -e.z);
            corners[2] = c + new Vector3(e.x, e.y, e.z);
            corners[3] = c + new Vector3(-e.x, e.y, e.z);

            corners[4] = c + new Vector3(-e.x, -e.y, -e.z);
            corners[5] = c + new Vector3(e.x, -e.y, -e.z);
            corners[6] = c + new Vector3(e.x, -e.y, e.z);
            corners[7] = c + new Vector3(-e.x, -e.y, e.z);

            Draw3DBox(corners, camera, color, lineWidth);
        }

        private static void DrawEdge(int a, int b, Vector2[] screen, bool[] visible, Color color, float lineWidth)
        {
            if (visible[a] && visible[b])
            {
                DrawLine(screen[a], screen[b], color, lineWidth);
            }
        }

        public static void DrawBone(Transform a, Transform b)
        {
            if (a == null || b == null) return;

            Vector3 v1 = Camera.main.WorldToScreenPoint(a.position);
            Vector3 v2 = Camera.main.WorldToScreenPoint(b.position);
            if (v1.z > 0f && v2.z > 0f)
            {
                v1.y = (float)Screen.height - v1.y;
                v2.y = (float)Screen.height - v2.y;
                DrawLine(v1, v2, Color.white, 1f);
            }
        }

        public static void DrawBone(Transform a, Transform b, Color color)
        {
            if (a == null || b == null) return;

            Vector3 v1 = Camera.main.WorldToScreenPoint(a.position);
            Vector3 v2 = Camera.main.WorldToScreenPoint(b.position);
            if (v1.z > 0f && v2.z > 0f)
            {
                v1.y = (float)Screen.height - v1.y;
                v2.y = (float)Screen.height - v2.y;
                DrawLine(v1, v2, color, 1f);
            }
        }

        public static void DrawCrosshair(Vector2 position, float size, Color color, float thickness)
        {
            if (Event.current.type != EventType.Repaint) return;

            var tex = SolidTex(color);
            GUI.DrawTexture(new Rect(position.x - size, position.y, size * 2f + thickness, thickness), tex);
            GUI.DrawTexture(new Rect(position.x, position.y - size, thickness, size * 2f + thickness), tex);
        }

        public static void DrawCircle(Vector2 center, float radius, Color color, float width, int segmentsPerQuarter)
        {
            if (Event.current.type != EventType.Repaint) return;

            int num = Mathf.Clamp(Mathf.RoundToInt(radius * 0.25f), 16, 96);
            float inv = 1f / num;
            Vector2 start = center + new Vector2(radius, 0f);
            for (int i = 1; i <= num; i++)
            {
                float t = i * inv;
                Vector2 p = center + new Vector2(radius * Mathf.Cos(6.2831855f * t), radius * Mathf.Sin(6.2831855f * t));
                DrawLine(start, p, color, width);
                start = p;
            }
        }

        private static Texture2D MakeTex(int w, int h, Color col)
        {
            Color[] pix = new Color[w * h];
            for (int i = 0; i < pix.Length; ++i) pix[i] = col;
            var tex = new Texture2D(w, h);
            tex.SetPixels(pix);
            tex.Apply();
            tex.hideFlags = HideFlags.HideAndDontSave;
            tex.wrapMode = TextureWrapMode.Clamp;
            tex.filterMode = FilterMode.Point;
            return tex;
        }

        // ====== 1) 取“自定义高度”的区域，并推进纵向布局 ======
        public static Rect NextRectFlexible(float customHeight)
        {
            Rect r = new Rect(x + margin, y + nextControlY, width - margin * 2f, customHeight);
            nextControlY += customHeight + controlDist;
            return r;
        }

        // ====== 2) 画一个面板（背景 + 1px 边框）– 零污染 ======
        public static void DrawPanel(Rect r, Color bg, Color border, float borderWidth = 1f)
        {
            // 背景
            DrawBox(new Vector2(r.x, r.y), new Vector2(r.width, r.height), bg, centered: false);

            // 边框（上右下左）
            var tex = SolidTex(border);
            // 上
            GUI.DrawTexture(new Rect(r.x, r.y, r.width, borderWidth), tex);
            // 右
            GUI.DrawTexture(new Rect(r.x + r.width - borderWidth, r.y, borderWidth, r.height), tex);
            // 下
            GUI.DrawTexture(new Rect(r.x, r.y + r.height - borderWidth, r.width, borderWidth), tex);
            // 左
            GUI.DrawTexture(new Rect(r.x, r.y, borderWidth, r.height), tex);
        }

        // ====== 3) 左对齐纯按钮（无右侧色条），占一行并推进布局 ======
        public static bool ButtonL(string text, Action onClick = null)
        {
            Rect r = new Rect(x + margin, y + nextControlY, width - margin * 2f, controlHeight);
            nextControlY += controlHeight + controlDist;

            var style = new GUIStyle(ButtonStyle)
            {
                alignment = TextAnchor.MiddleLeft,
                padding = new RectOffset(8, 8, 0, 0)
            };

            bool clicked = GUI.Button(r, text, style);
            if (clicked) onClick?.Invoke();
            return clicked;
        }

        // ====== 4) 带滚动的列表框（自动布局 + 可选右键菜单）– 零污染版本 ======
        public static int ListBox(
            ref Vector2 scroll,
            IList<string> items,
            float height,
            int selectedIndex,
            float rowHeight = -1f,
            Color? bg = null,
            Color? border = null,
            Func<int, IList<string>> onRightClickMenu = null,
            Action<int, int, string> onRightClickSelect = null
        )
        {
            if (items == null) items = new List<string>(0);
            if (rowHeight <= 0f) rowHeight = Mathf.Max(16f, controlHeight - 4f);

            Rect listRect = NextRectFlexible(height);

            // 背景/边框
            Color bgCol = bg ?? new Color(0f, 0f, 0f, 0.25f);
            Color bdCol = border ?? new Color(1f, 1f, 1f, 0.15f);
            DrawPanel(listRect, bgCol, bdCol, 1f);

            const float pad = 4f;
            Rect viewRect = new Rect(listRect.x + pad, listRect.y + pad, listRect.width - pad * 2f, listRect.height - pad * 2f);
            float contentH = Mathf.Max(viewRect.height, items.Count * rowHeight);
            Rect contentRect = new Rect(0, 0, viewRect.width - 16f, contentH);

            scroll = GUI.BeginScrollView(viewRect, scroll, contentRect, false, true);

            var labelStyle = StringStyle ?? new GUIStyle(GUI.skin.label);
            labelStyle.alignment = TextAnchor.MiddleLeft;
            labelStyle.fontSize = Mathf.Clamp(Mathf.RoundToInt(rowHeight * 0.5f), 11, 16);
            // labelStyle.normal.textColor = 保持默认或外部指定

            int newSelected = selectedIndex;
            float cy = 0f;

            Event e = Event.current;
            bool openCtx = false;
            Vector2 ctxPos = default;
            int ctxRow = -1;
            IList<string> ctxItems = null;

            var altTex = SolidTex(new Color(1f, 1f, 1f, 0.06f));
            var selTex = SolidTex(new Color(1f, 1f, 1f, 0.10f));

            for (int i = 0; i < items.Count; i++)
            {
                Rect row = new Rect(0, cy, contentRect.width, rowHeight);

                // 交替行淡底（贴图）
                if ((i & 1) == 1)
                    GUI.DrawTexture(row, altTex);

                // 选中高亮（贴图）
                if (i == selectedIndex)
                    GUI.DrawTexture(row, selTex);

                // 左键选中整行
                if (GUI.Button(row, GUIContent.none, GUIStyle.none))
                    newSelected = i;

                // 右键菜单触发
                if (onRightClickMenu != null &&
                    (e.type == EventType.ContextClick || (e.type == EventType.MouseDown && e.button == 1)))
                {
                    Vector2 mp = e.mousePosition;
                    if (row.Contains(mp))
                    {
                        ctxItems = onRightClickMenu(i);
                        if (ctxItems != null && ctxItems.Count > 0)
                        {
                            openCtx = true;
                            ctxRow = i;
                            ctxPos = mp;
                            e.Use();
                        }
                    }
                }

                // 文本
                GUI.Label(new Rect(row.x + 8f, row.y, row.width - 16f, row.height), items[i] ?? string.Empty, labelStyle);
                cy += rowHeight;
            }

            GUI.EndScrollView();

            // 右键菜单
            if (openCtx && ctxItems != null && ctxItems.Count > 0 && onRightClickSelect != null)
            {
                Vector2 global = new Vector2(viewRect.x + ctxPos.x, viewRect.y + ctxPos.y);

                float menuW = Mathf.Max(120f, viewRect.width * 0.5f);
                float itemH = Mathf.Max(18f, controlHeight - 2f);
                float menuH = ctxItems.Count * itemH + 8f;

                if (global.x + menuW > x + width - margin) global.x = x + width - margin - menuW;
                if (global.y + menuH > y + height - margin) global.y = y + height - margin - menuH;

                Rect menuRect = new Rect(global.x, global.y, menuW, menuH);
                DrawPanel(menuRect, new Color(0f, 0f, 0f, 0.85f), new Color(1f, 1f, 1f, 0.15f), 1f);

                float yy = menuRect.y + 4f;
                for (int k = 0; k < ctxItems.Count; k++)
                {
                    Rect r = new Rect(menuRect.x + 4f, yy, menuW - 8f, itemH);
                    if (GUI.Button(r, ctxItems[k], ButtonStyle))
                    {
                        onRightClickSelect(ctxRow, k, ctxItems[k]);
                    }
                    yy += itemH;
                }
            }

            return newSelected;
        }

        // ====== 5) 自绘行版本：同样零污染 ======
        public static int ListBoxCustom<T>(
            ref Vector2 scroll,
            IList<T> items,
            float height,
            int selectedIndex,
            float rowHeight,
            Action<Rect, int, T, bool> onRowGUI,
            Func<int, IList<string>> onRightClickMenu = null,
            Action<int, int, string> onRightClickSelect = null
        )
        {
            if (items == null) items = new List<T>(0);
            if (rowHeight <= 0f) rowHeight = Mathf.Max(16f, controlHeight - 4f);

            Rect listRect = NextRectFlexible(height);

            Color bgCol = new Color(0f, 0f, 0f, 0.25f);
            Color bdCol = new Color(1f, 1f, 1f, 0.15f);
            DrawPanel(listRect, bgCol, bdCol, 1f);

            const float pad = 4f;
            Rect viewRect = new Rect(listRect.x + pad, listRect.y + pad, listRect.width - pad * 2f, listRect.height - pad * 2f);
            float contentH = Mathf.Max(viewRect.height, items.Count * rowHeight);
            Rect contentRect = new Rect(0, 0, viewRect.width - 16f, contentH);

            scroll = GUI.BeginScrollView(viewRect, scroll, contentRect, false, true);

            Event e = Event.current;
            bool openCtx = false;
            Vector2 ctxPos = default;
            int ctxRow = -1;
            IList<string> ctxItems = null;

            var altTex = SolidTex(new Color(1f, 1f, 1f, 0.06f));
            var selTex = SolidTex(new Color(1f, 1f, 1f, 0.14f));

            float cy = 0f;
            for (int i = 0; i < items.Count; i++)
            {
                Rect row = new Rect(0, cy, contentRect.width, rowHeight);

                if ((i & 1) == 1) GUI.DrawTexture(row, altTex);
                if (i == selectedIndex) GUI.DrawTexture(row, selTex);

                if (GUI.Button(row, GUIContent.none, GUIStyle.none))
                    selectedIndex = i;

                if (onRightClickMenu != null &&
                    (e.type == EventType.ContextClick || (e.type == EventType.MouseDown && e.button == 1)))
                {
                    Vector2 mp = e.mousePosition;
                    if (row.Contains(mp))
                    {
                        ctxItems = onRightClickMenu(i);
                        if (ctxItems != null && ctxItems.Count > 0)
                        {
                            openCtx = true;
                            ctxRow = i;
                            ctxPos = mp;
                            e.Use();
                        }
                    }
                }

                onRowGUI?.Invoke(row, i, items[i], i == selectedIndex);

                cy += rowHeight;
            }

            GUI.EndScrollView();

            if (openCtx && ctxItems != null && ctxItems.Count > 0 && onRightClickSelect != null)
            {
                Vector2 global = new Vector2(viewRect.x + ctxPos.x, viewRect.y + ctxPos.y);

                float menuW = Mathf.Max(120f, viewRect.width * 0.5f);
                float itemH = Mathf.Max(18f, controlHeight - 2f);
                float menuH = ctxItems.Count * itemH + 8f;

                if (global.x + menuW > x + width - margin) global.x = x + width - margin - menuW;
                if (global.y + menuH > y + height - margin) global.y = y + height - margin - menuH;

                Rect menuRect = new Rect(global.x, global.y, menuW, menuH);
                DrawPanel(menuRect, new Color(0f, 0f, 0f, 0.85f), new Color(1f, 1f, 1f, 0.15f), 1f);

                float yy = menuRect.y + 4f;
                for (int k = 0; k < ctxItems.Count; k++)
                {
                    Rect r = new Rect(menuRect.x + 4f, yy, menuW - 8f, itemH);
                    if (GUI.Button(r, ctxItems[k], ButtonStyle))
                    {
                        onRightClickSelect(ctxRow, k, ctxItems[k]);
                    }
                    yy += itemH;
                }
            }

            return selectedIndex;
        }
    }
}
