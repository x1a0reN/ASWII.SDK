using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace ASWDEBUG.UI
{
    public static class SpriteListDrawer
    {
        // === 面板级半透明背景（只影响本面板） ===
        public static bool UseOuterBg = true;
        public static Color OuterBg = new Color(0f, 0f, 0f, 0.9f);
        public static Color OuterBorder = new Color(1f, 1f, 1f, 0.30f);

        public static Color GroupHeaderBg = new Color(0f, 0f, 0f, 0.92f);
        public static Color GroupHeaderBd = new Color(1f, 1f, 1f, 0.25f);

        public static Color RowAltBg = new Color(1f, 1f, 1f, 0.08f);
        public static Color RowSelectedBg = new Color(1f, 1f, 1f, 0.20f);

        // 固定：只从这 9 个图集收集（英文内部名）
        static readonly string[] AtlasNames =
        {
            "ButtonAtlas",
            "Item Atlas",
            "CommonBgAtlas",
            "MapPreviewAtlas",
            "PictureWordAtlas",
            "PictureWordAtlas2",
            "TencentAtlas",
            "HDIconAtlas",
            "AvatarPartAtlas"
        };

        // —— 中文显示名映射（仅用于展示，不影响搜索/内部逻辑）——
        static readonly Dictionary<string, string> AtlasDisplayCN =
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                { "ButtonAtlas",       "按钮图集" },
                { "Item Atlas",        "物品图集" },
                { "CommonBgAtlas",     "通用背景图集" },
                { "MapPreviewAtlas",   "地图预览图集" },
                { "PictureWordAtlas",  "图文图集（Ⅰ）" },
                { "PictureWordAtlas2", "图文图集（Ⅱ）" },
                { "TencentAtlas",      "腾讯样式图集" },
                { "HDIconAtlas",       "高清图标图集" },
                { "AvatarPartAtlas",   "头像部件图集" }
            };

        static string DisplayName(string key)
        {
            string v;
            return AtlasDisplayCN.TryGetValue(key, out v) ? v : key;
        }

        struct Entry
        {
            public string atlasName;     // 英文内部名
            public global::UISpriteData sd;
            public Texture2D tex;
            public Rect uv;
            public int w, h;
        }

        // 数据：按 AtlasName（英文内部名）分组
        static readonly Dictionary<string, List<Entry>> _groups =
            new Dictionary<string, List<Entry>>(StringComparer.Ordinal);

        static bool _built;

        // UI 状态
        static string _search = "";
        static Vector2 _scrollAll;
        static readonly Dictionary<string, bool> _folded =
            new Dictionary<string, bool>(StringComparer.Ordinal);   // true=折叠
        static readonly Dictionary<string, int> _selectedIndex =
            new Dictionary<string, int>(StringComparer.Ordinal);

        // 布局常量
        const float HeaderH = 24f;
        const float RowH = 52f;
        const float Pad = 0f;
        const float CtrlH = 22f;
        const float CtrlDist = 0f;

        static readonly List<Entry> s_emptyList = new List<Entry>(0);

        /// 在任意激活脚本的 OnGUI() 中调用
        public static void DrawSpriteList(float x, float y, float width, float height)
        {
            if (UIHelper.ButtonStyle == null) UIHelper.InitializeStyles();
            if (!_built) BuildEntries(true);

            if (UseOuterBg)
                UIHelper.DrawPanel(new Rect(x, y, width, height), OuterBg, OuterBorder, 1f);

            UIHelper.Begin("图片发送器", x, y, width, height, Pad, CtrlH, CtrlDist);

            // 顶部工具条
            DrawToolbar(width);

            // 列表视区
            float listH = Mathf.Max(120f, height - 20f - (CtrlH + CtrlDist) - Pad);
            Rect viewRect = UIHelper.NextRectFlexible(listH);

            // 过滤分组 & 内容高度
            var viewGroups = FilteredGroups();
            float contentH = CalcContentHeight(viewGroups);

            // 防抖动：先夹紧滚动位置
            ClampScroll(ref _scrollAll, viewRect.height, contentH);

            bool needVBar = contentH > viewRect.height;
            Rect contentRect = new Rect(0, 0, viewRect.width - (needVBar ? 16f : 0f), Mathf.Max(1f, contentH));
            _scrollAll = GUI.BeginScrollView(viewRect, _scrollAll, contentRect, false, needVBar);

            float cy = 0f;

            for (int ai = 0; ai < AtlasNames.Length; ai++)
            {
                string name = AtlasNames[ai];
                List<Entry> list;
                if (!viewGroups.TryGetValue(name, out list) || list == null) list = s_emptyList;

                // 组头（中文显示名）
                Rect header = new Rect(0, cy, contentRect.width, HeaderH);
                DrawSectionHeader(header, name, list.Count);
                cy += HeaderH;

                if (IsFolded(name)) continue;

                for (int i = 0; i < list.Count; i++)
                {
                    var e = list[i];
                    Rect row = new Rect(0, cy, contentRect.width, RowH);

                    if ((i & 1) == 1)
                        UIHelper.DrawBox(new Vector2(row.x, row.y), new Vector2(row.width, row.height), RowAltBg, false);
                    if (GetSelected(name) == i)
                        UIHelper.DrawBox(new Vector2(row.x, row.y), new Vector2(row.width, row.height), RowSelectedBg, false);

                    // 先画行内容（里面会画“发送”按钮）
                    DrawRow(row, e);

                    // 再画行点击，但避开右侧发送按钮区域（按钮宽 64，加右侧 12 间距）
                    float sendBtnW = 64f;
                    Rect hitRow = new Rect(row.x, row.y, row.width - (sendBtnW + 12f), row.height);
                    if (GUI.Button(hitRow, GUIContent.none, GUIStyle.none))
                        _selectedIndex[name] = i;

                    cy += RowH;
                }

                cy += 6f; // 组间距
            }

            GUI.EndScrollView();
        }

        static void DrawToolbar(float panelW)
        {
            Rect r = UIHelper.NextRectFlexible(CtrlH);

            var tf = new GUIStyle(UIHelper.TextFieldStyle ?? GUI.skin.textField)
            {
                alignment = TextAnchor.MiddleLeft,
                fontSize = 13,
                padding = new RectOffset(6, 6, 0, 0)
            };

            Rect rSearch = new Rect(r.x, r.y, r.width - 84f - 8f, r.height);
            // 注意：搜索仍使用英文内部名，不做中文映射
            _search = GUI.TextField(rSearch, _search ?? "", tf);

            float bx = rSearch.xMax + 8f;
            if (GUI.Button(new Rect(bx, r.y, 84f, r.height), "重建", UIHelper.ButtonStyle))
                BuildEntries(true);
        }

        // 组头：中文显示
        static void DrawSectionHeader(Rect r, string atlasName, int count)
        {
            UIHelper.DrawPanel(r, GroupHeaderBg, GroupHeaderBd, 1f);

            bool folded = IsFolded(atlasName);
            string arrow = folded ? "▶" : "▼";
            string title = string.Format("{0}  {1}    <size=11>({2})</size>", arrow, DisplayName(atlasName), count);

            var style = new GUIStyle(UIHelper.StringStyle ?? GUI.skin.label)
            {
                alignment = TextAnchor.MiddleLeft,
                fontSize = 13,
                richText = true,
                padding = new RectOffset(8, 8, 0, 0)
            };

            if (GUI.Button(r, GUIContent.none, GUIStyle.none))
            {
                _folded[atlasName] = !folded;
            }

            GUI.Label(r, title, style);
        }

        // 行：预览 + 文本（Atlas 中文显示）+ “发送”按钮
        static void DrawRow(Rect row, Entry e)
        {
            float pv = row.height - 8f;   // 预览尺寸
            float padX = 6f;

            Rect rp = new Rect(row.x + padX, row.y + (row.height - pv) * 0.5f, pv, pv);
            if (e.tex)
            {
                UIHelper.DrawBox(rp.center, rp.size, new Color(1f, 1f, 1f, 0.05f), true);
                GUI.DrawTextureWithTexCoords(rp, e.tex, e.uv, true);
                UIHelper.DrawBoxOutline(new Vector2(rp.x, rp.y), rp.width, rp.height, new Color(1f, 1f, 1f, 0.12f));
            }

            float textLeft = rp.xMax + 8f;
            float btnW = 64f;
            Rect rText = new Rect(textLeft, row.y + 4f, row.width - (textLeft - row.x) - btnW - 12f, row.height - 8f);

            var style = new GUIStyle(UIHelper.StringStyle ?? GUI.skin.label)
            {
                alignment = TextAnchor.UpperLeft,
                fontSize = 13,
                richText = true,
                wordWrap = false
            };

            // 展示用中文 Atlas 名称；内部仍保留英文 atlasName 用于搜索/发送
            string line = string.Format("<b>{0}</b>  <size=11>{1}×{2}</size>\n<size=11>图集: {3}</size>",
                                        e.sd.name, e.w, e.h, DisplayName(e.atlasName));
            GUI.Label(rText, line, style);

            Rect rBtn = new Rect(row.xMax - btnW - 6f, row.y + (row.height - 22f) * 0.5f, btnW, 22f);
            if (GUI.Button(rBtn, " 发送", UIHelper.ButtonStyle))
            {
                // 仍发送英文内部名 + 精灵名
                GameApp.Instance.channel_connection.RequestChat("", string.Format("%p{0}${1}$", e.atlasName, e.sd.name));
            }
        }

        // 收集：AtlasManager + 指定英文名
        static void BuildEntries(bool force)
        {
            if (_built && !force) return;

            _groups.Clear();

            for (int idx = 0; idx < AtlasNames.Length; idx++)
            {
                string name = AtlasNames[idx];
                var list = new List<Entry>(256);
                _groups[name] = list;

                if (!_folded.ContainsKey(name)) _folded[name] = false;
                if (!_selectedIndex.ContainsKey(name)) _selectedIndex[name] = -1;

                var atlas = AtlasManager.Instance.GetAtlas(name);
                if (atlas == null) continue;

                while (atlas.replacement != null) atlas = atlas.replacement;

                Texture2D tex = null;
                if (atlas.spriteMaterial != null && atlas.spriteMaterial.mainTexture != null)
                    tex = atlas.spriteMaterial.mainTexture as Texture2D;
                if (tex == null && atlas.texture != null)
                    tex = atlas.texture as Texture2D;
                if (tex == null) continue;

                var sprites = atlas.spriteList;
                if (sprites == null || sprites.Count == 0) continue;

                int tw = tex.width, th = tex.height;

                for (int i = 0; i < sprites.Count; i++)
                {
                    var sd = sprites[i];
                    if (sd == null) continue;

                    float u = sd.x / (float)tw;
                    float v = 1f - (sd.y + sd.height) / (float)th; // NGUI 顶左 -> Unity UV（翻 Y）
                    float uw = sd.width / (float)tw;
                    float vh = sd.height / (float)th;

                    list.Add(new Entry
                    {
                        atlasName = name, // 仍保存英文内部名
                        sd = sd,
                        tex = tex,
                        uv = new Rect(u, v, uw, vh),
                        w = sd.width,
                        h = sd.height
                    });
                }

                list.Sort((A, B) => string.Compare(A.sd.name, B.sd.name, StringComparison.Ordinal));
            }

            _built = true;
        }

        // 过滤：搜索仍用英文内部名（以及精灵名）
        static Dictionary<string, List<Entry>> FilteredGroups()
        {
            if (string.IsNullOrEmpty(_search))
                return _groups;

            var map = new Dictionary<string, List<Entry>>(StringComparer.Ordinal);
            string s = _search.Trim();
            bool hasColon = s.IndexOf(':') >= 0;

            for (int i = 0; i < AtlasNames.Length; i++)
            {
                string name = AtlasNames[i];
                List<Entry> src;
                if (!_groups.TryGetValue(name, out src) || src == null)
                {
                    map[name] = s_emptyList;
                    continue;
                }

                if (!hasColon)
                {
                    map[name] = src.Where(e =>
                        e.sd.name.IndexOf(s, StringComparison.OrdinalIgnoreCase) >= 0 ||
                        e.atlasName.IndexOf(s, StringComparison.OrdinalIgnoreCase) >= 0  // 仅英文内部名
                    ).ToList();
                }
                else
                {
                    var sp = s.Split(new[] { ':' }, 2);
                    string a = sp[0];
                    string n = sp.Length > 1 ? sp[1] : "";
                    map[name] = src.Where(e =>
                        e.atlasName.IndexOf(a, StringComparison.OrdinalIgnoreCase) >= 0 && // 仅英文内部名
                        e.sd.name.IndexOf(n, StringComparison.OrdinalIgnoreCase) >= 0
                    ).ToList();
                }
            }
            return map;
        }

        static float CalcContentHeight(Dictionary<string, List<Entry>> viewGroups)
        {
            float h = 0f;
            for (int i = 0; i < AtlasNames.Length; i++)
            {
                string name = AtlasNames[i];
                List<Entry> list;
                if (!viewGroups.TryGetValue(name, out list) || list == null) list = s_emptyList;

                h += HeaderH;
                if (!IsFolded(name))
                {
                    h += list.Count * RowH;
                    h += 6f;
                }
            }
            return h;
        }

        static bool IsFolded(string atlas)
        {
            bool f;
            return _folded.TryGetValue(atlas, out f) && f;
        }

        static int GetSelected(string atlas)
        {
            int i;
            return _selectedIndex.TryGetValue(atlas, out i) ? i : -1;
        }

        // 抖动修复：夹紧滚动位置
        static void ClampScroll(ref Vector2 scroll, float viewH, float contentH)
        {
            if (float.IsNaN(scroll.y) || float.IsInfinity(scroll.y)) scroll.y = 0f;

            float maxY = Mathf.Max(0f, contentH - viewH);
            if (maxY <= 0f) { scroll.y = 0f; return; }

            if (scroll.y > maxY) scroll.y = maxY;
            if (scroll.y < 0f) scroll.y = 0f;
        }
    }
}
