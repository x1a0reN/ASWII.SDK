using ASWDEBUG.UI;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;              // 反射调用 AuctionMonitor.SetTypeHint/TryGetTypeHint（备用）
using System.Text.RegularExpressions; // 解析 t/st/watch
using UnityEngine;

public static class AuctionItemDrawer
{
    // 供外部挂业务：点击“开始监控”
    public static System.Action OnStartMonitor;

    private const string CoinName = "金币"; // ★ 特殊项：金币

    [Serializable]
    private class ItemRow
    {
        public string Id;
        public string NameCN;
        public string PriceInput;

        // ★ 类型 hint（未知用 -1）
        public int T = -1;
        public int ST = -1;

        // ★ 是否在监控列表（用于UI显示；真正写盘时以 AuctionWatchList 为准）
        public bool Watched = false;

        public ItemRow(string id, string name, float price, int t = -1, int st = -1, bool watched = false)
        {
            Id = id;
            NameCN = name;
            PriceInput = FormatPrice(price);
            T = t; ST = st;
            Watched = watched;
        }
    }

    // ===== 内部状态 =====
    private static readonly List<ItemRow> _rows = new List<ItemRow>();
    private static readonly List<ItemRow> _view = new List<ItemRow>();
    private static Vector2 _scroll = Vector2.zero;
    private static string _status = "";
    private static bool _loaded = false;
    private static string _search = "";

    // 搜索框控件名（IME 需要）
    private const string SearchCtrlName = "AuctionSearchBox";

    // ===== 布局常量（可按需微调）=====
    private const int TitleBarH = 22;   // UIHelper.Begin 的标题条高度
    private const int FramePadL = 12;   // 左内边距（与标题条视觉对齐）
    private const int FramePadR = 8;
    private const int FramePadT = 6;    // 标题条下内边距
    private const int FramePadB = 6;

    private const float TopBtnW = 78f;   // 顶部按钮更窄
    private const float RowBtnW = 96f;   // 行内按钮更窄
    private const float PriceW = 76f;    // 价格输入宽
    private const float RowRightPadding = 8f; // 行尾与滚动条间距

    // 同步按钮/编辑框高度
    private static float TextFieldHeight
    {
        get
        {
            float h = (UIHelper.TextFieldStyle != null && UIHelper.TextFieldStyle.fixedHeight > 0)
                ? UIHelper.TextFieldStyle.fixedHeight
                : (GUI.skin.textField.fixedHeight > 0 ? GUI.skin.textField.fixedHeight : 22f);
            return h;
        }
    }

    // ===== 滚动条（灰色方角，track 与 thumb 宽度完全一致）=====
    private const float ScrollbarW = 12f; // 轨与拇指同宽
    private static Texture2D _texGrayTrack, _texGrayThumb, _texPanelBg;
    private static GUIStyle _vBarGray, _vThumbGray, _hBarGray, _hThumbGray, _scrollBgNone;

    public static Rect DefaultRect = new Rect(10, 10, 600, 400);

    public static void Draw(int x, int y, int w, int h) { Draw(new Rect(x, y, w, h), "拍卖行"); }
    public static void Draw(Rect rect) { Draw(rect, "拍卖行"); }

    public static void Draw(Rect rect, string title)
    {
        EnsureLoaded();
        EnsureStylesAndTextures();

        // 开启 IME（旧版 Unity 走 Input.*）
        Input.imeCompositionMode = IMECompositionMode.On;

        // 1) 外框（所有控件都在 Begin 之后）
        UIHelper.Begin(title, (int)rect.x, (int)rect.y, (int)rect.width, (int)rect.height, 0, TitleBarH, 0);

        // 2) 半透明灰背景（仅内容区，避免盖住标题文字）
        Rect contentRect = new Rect(
            rect.x + FramePadL,
            rect.y + TitleBarH + FramePadT,
            rect.width - FramePadL - FramePadR,
            rect.height - TitleBarH - FramePadT - FramePadB
        );
        GUI.DrawTexture(contentRect, _texPanelBg);

        // 3) 内容严格受区域裁剪
        GUILayout.BeginArea(contentRect);

        // 顶部工具条：开始/停止监控 + 全部监控 / 全部移除 / 刷新 / 保存 / 读取类型 + 搜索
        GUILayout.BeginHorizontal();

        string monText = ASWDEBUG.UI.AuctionMonitor.IsRunning ? "停止监控" : "开始监控";
        if (GUILayout.Button(monText, UIHelper.ButtonStyle,
            GUILayout.Width(TopBtnW), GUILayout.Height(TextFieldHeight)))
        {
            ASWDEBUG.UI.AuctionMonitor.Toggle();
        }

        if (GUILayout.Button("全部监控", UIHelper.ButtonStyle,
            GUILayout.Width(TopBtnW), GUILayout.Height(TextFieldHeight)))
        {
            BulkAddVisible();
        }

        if (GUILayout.Button("全部移除", UIHelper.ButtonStyle,
            GUILayout.Width(TopBtnW), GUILayout.Height(TextFieldHeight)))
        {
            BulkRemoveVisible();
        }

        if (GUILayout.Button("刷新文件", UIHelper.ButtonStyle,
            GUILayout.Width(TopBtnW), GUILayout.Height(TextFieldHeight)))
        {
            try { LoadFromFile(); ApplyFilter(); _status = "已重新加载：" + _rows.Count + " 条"; }
            catch (Exception ex) { _status = "刷新失败：" + ex.Message; }
        }

        // 保存（价格+类型+watch）
        if (GUILayout.Button("保存", UIHelper.ButtonStyle,
            GUILayout.Width(TopBtnW), GUILayout.Height(TextFieldHeight)))
        {
            try
            {
                SaveToFile();
                _status = "已保存：" + _rows.Count + " 条 -> " + GetItemsFilePath();
            }
            catch (Exception ex)
            {
                _status = "保存失败：" + ex.Message;
            }
        }

        // 读取类型（逐个发小查询，得到 t/st 后落盘）—— 跳过金币
        if (GUILayout.Button("读取类型", UIHelper.ButtonStyle,
            GUILayout.Width(TopBtnW), GUILayout.Height(TextFieldHeight)))
        {
            BeginReadTypes();
        }

        // 搜索框（放在右侧，实时过滤，IME 友好）
        GUI.SetNextControlName(SearchCtrlName);
        string typed = GUILayout.TextField(_search ?? "", UIHelper.TextFieldStyle,
            GUILayout.MinWidth(160), GUILayout.ExpandWidth(true), GUILayout.Height(TextFieldHeight));

        bool composing = !string.IsNullOrEmpty(Input.compositionString);
        if (!composing && typed != _search) { _search = typed; ApplyFilter(); }
        else { _search = typed; }

        // 让 IME 候选框跟随输入框（左下角）
        if (GUI.GetNameOfFocusedControl() == SearchCtrlName)
        {
            Rect last = GUILayoutUtility.GetLastRect();
            Vector2 pos = new Vector2(contentRect.x + last.x + 4f,
                                      contentRect.y + last.y + last.height - 2f);
            Input.compositionCursorPos = pos;
        }

        GUILayout.EndHorizontal();

        GUILayout.Space(4);
        GUILayout.Label(_status);
        GUILayout.Space(2);

        // 4) 临时替换滚动条皮肤为灰色方角（宽度与拇指完全一致）
        GUISkin skin = GUI.skin;
        GUIStyle prevV = skin.verticalScrollbar; GUIStyle prevVT = skin.verticalScrollbarThumb;
        GUIStyle prevH = skin.horizontalScrollbar; GUIStyle prevHT = skin.horizontalScrollbarThumb;
        skin.verticalScrollbar = _vBarGray;
        skin.verticalScrollbarThumb = _vThumbGray;
        skin.horizontalScrollbar = _hBarGray;
        skin.horizontalScrollbarThumb = _hThumbGray;

        // 5) 列表
        _scroll = GUILayout.BeginScrollView(_scroll, false, true, _hBarGray, _vBarGray, _scrollBgNone,
                                            GUILayout.ExpandWidth(true), GUILayout.ExpandHeight(true));

        for (int i = 0; i < _view.Count; i++)
        {
            ItemRow r = _view[i];
            GUILayout.BeginHorizontal();

            // 中文名
            GUILayout.Label(r.NameCN, GUILayout.ExpandWidth(true), GUILayout.Height(TextFieldHeight));

            // 价格输入（两位小数；非法 -> 0.00）
            string newPrice = GUILayout.TextField(r.PriceInput ?? "0.00", UIHelper.TextFieldStyle,
                                                  GUILayout.Width(PriceW), GUILayout.Height(TextFieldHeight));
            r.PriceInput = AutoFormatPrice(newPrice == r.PriceInput ? r.PriceInput : newPrice);

            // 监控按钮（加入/移除）
            bool watchedLive = AuctionWatchList.Contains(r.Id);
            string btnText = watchedLive ? "移除监控列表" : "加入监控列表";
            if (GUILayout.Button(btnText, UIHelper.ButtonStyle,
                GUILayout.Width(RowBtnW), GUILayout.Height(TextFieldHeight)))
            {
                if (watchedLive)
                {
                    if (AuctionWatchList.Remove(r.Id))
                    {
                        r.Watched = false;
                        _status = "已移除：" + r.NameCN;
                    }
                }
                else
                {
                    r.PriceInput = AutoFormatPrice(r.PriceInput);
                    AuctionWatchList.AddOrUpdate(r.Id, r.NameCN, r.PriceInput);
                    r.Watched = true;
                    _status = "已加入：" + r.NameCN;
                }
            }

            // ★ 单独监控按钮（取消时会把全部监控也停掉）
            bool isSingleThis = ASWDEBUG.UI.AuctionMonitor.IsSingleMode && ASWDEBUG.UI.AuctionMonitor.SingleId == r.Id;
            string singleBtnText = isSingleThis ? "取消单独监控" : "单独监控";
            if (GUILayout.Button(singleBtnText, UIHelper.ButtonStyle,
                GUILayout.Width(RowBtnW), GUILayout.Height(TextFieldHeight)))
            {
                if (isSingleThis)
                {
                    // ★ StopSingleMonitor 会 STOP ALL
                    ASWDEBUG.UI.AuctionMonitor.StopSingleMonitor();
                    _status = "已取消单独监控，并已停止全部监控";
                }
                else
                {
                    float want = 0f;
                    AuctionWatchList.TryParsePrice(r.PriceInput, out want);
                    // 对“金币”也走同一入口（监控层会识别名字为金币并走 currency RPC）
                    ASWDEBUG.UI.AuctionMonitor.StartSingleMonitor(r.Id, r.NameCN, want, r.T, r.ST);
                    _status = "已开启单独监控：" + r.NameCN;
                }
            }

            // 与滚动条留出距离
            GUILayout.Space(RowRightPadding);

            GUILayout.EndHorizontal();
        }

        GUILayout.EndScrollView();

        // 恢复皮肤
        skin.verticalScrollbar = prevV;
        skin.verticalScrollbarThumb = prevVT;
        skin.horizontalScrollbar = prevH;
        skin.horizontalScrollbarThumb = prevHT;

        GUILayout.EndArea();
        // 无 UIHelper.End()
    }

    public static void ForceReload() { _loaded = false; _status = "等待重新加载..."; }

    // ===== 加载 & 过滤 =====
    private static void EnsureLoaded()
    {
        if (_loaded) return;
        try { LoadFromFile(); ApplyFilter(); _status = "已加载条目：" + _rows.Count; _loaded = true; }
        catch (Exception ex) { _status = "加载失败：" + ex.Message; _loaded = true; }
    }

    private static string GetItemsFilePath()
    {
        return Path.Combine(Path.Combine(Application.persistentDataPath, "Items"), "ASW_Items.txt");
    }

    private static void LoadFromFile()
    {
        _rows.Clear();
        string file = GetItemsFilePath();
        if (!File.Exists(file)) throw new FileNotFoundException("未找到文件", file);

        using (FileStream fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        using (StreamReader sr = new StreamReader(fs))
        {
            string line;
            while ((line = sr.ReadLine()) != null)
            {
                line = (line ?? "").Trim();
                if (line.Length == 0) continue;

                string id, name; float price;
                if (TryParseLineRobust(line, out id, out name, out price))
                {
                    // 解析 t/st/watch
                    int t, st; ExtractTypeHints(line, out t, out st);
                    bool watched = ExtractWatchFlag(line);

                    int idx = _rows.FindIndex(delegate (ItemRow rr) { return rr.Id == id; });
                    if (idx >= 0) _rows.RemoveAt(idx);
                    _rows.Add(new ItemRow(id, name, price, t, st, watched));

                    // 类型 hint 喂给 AuctionMonitor（若有）；金币无须 hint
                    if (!string.Equals(name, CoinName, StringComparison.Ordinal))
                    {
                        if (t >= 0 || st >= 0) PushTypeHintToMonitor(id, t, st);
                    }

                    // 同步监控列表（以文件为准）
                    if (watched)
                        AuctionWatchList.AddOrUpdate(id, name, FormatPrice(price));
                    else
                        AuctionWatchList.Remove(id);
                }
            }
        }
    }

    private static void SaveToFile()
    {
        string file = GetItemsFilePath();
        string dir = Path.GetDirectoryName(file);
        if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);

        // 保存前确保所有价格都为两位小数，并同步 Watched 为当前列表状态
        for (int i = 0; i < _rows.Count; i++)
        {
            ItemRow r = _rows[i];
            r.PriceInput = AutoFormatPrice(r.PriceInput);
            r.Watched = AuctionWatchList.Contains(r.Id); // 以实时列表为准
        }

        using (var sw = new StreamWriter(file, false))
        {
            // 一行一个：id => 中文名 => 价格 => t=.., st=.., watch=0/1
            for (int i = 0; i < _rows.Count; i++)
            {
                ItemRow r = _rows[i];
                int t = r.T, st = r.ST;

                // “金币”没有 t/st 概念，永远写 -1/-1
                if (!string.Equals(r.NameCN, CoinName, StringComparison.Ordinal))
                {
                    // 尝试从监控器拉最新 hint
                    int tt, sst;
                    if (PullTypeHintFromMonitor(r.Id, out tt, out sst))
                    {
                        t = tt; st = sst;
                        r.T = t; r.ST = st; // 回填
                    }
                }
                else
                {
                    t = -1; st = -1;
                }

                if (t < 0) t = -1;
                if (st < 0) st = -1;

                string line = string.Concat(
                    r.Id, " => ", r.NameCN, " => ", r.PriceInput,
                    " => t=", t.ToString(CultureInfo.InvariantCulture),
                    ", st=", st.ToString(CultureInfo.InvariantCulture),
                    ", watch=", (r.Watched ? "1" : "0")
                );

                sw.WriteLine(line);
            }
        }
    }

    private static void ApplyFilter()
    {
        _view.Clear();
        string key = (_search ?? "").Trim();
        if (key.Length == 0) { _view.AddRange(_rows); return; }

        for (int i = 0; i < _rows.Count; i++)
        {
            ItemRow r = _rows[i];
            if (r != null && r.NameCN != null &&
                r.NameCN.IndexOf(key, StringComparison.OrdinalIgnoreCase) >= 0)
            {
                _view.Add(r);
            }
        }
    }

    // ===== 批量操作 =====
    private static void BulkAddVisible()
    {
        for (int i = 0; i < _view.Count; i++)
        {
            ItemRow r = _view[i];
            if (r == null) continue;
            r.PriceInput = AutoFormatPrice(r.PriceInput);
            AuctionWatchList.AddOrUpdate(r.Id, r.NameCN, r.PriceInput);
            r.Watched = true;
        }
        _status = "已加入监控（当前列表）：" + _view.Count + " 条";
    }

    private static void BulkRemoveVisible()
    {
        int cnt = 0;
        for (int i = 0; i < _view.Count; i++)
        {
            ItemRow r = _view[i];
            if (r == null) continue;
            if (AuctionWatchList.Remove(r.Id)) cnt++;
            r.Watched = false;
        }
        _status = "已移除监控（当前列表）：" + cnt + " 条";
    }

    // ===== “读取类型”按钮逻辑 =====
    private static void BeginReadTypes()
    {
        try
        {
            _status = "读取类型中...（后台执行，完成后自动保存）";
            var list = new List<ASWDEBUG.UI.AuctionMonitor.NamedId>(_rows.Count);
            for (int i = 0; i < _rows.Count; i++)
            {
                ItemRow r = _rows[i];
                if (r == null) continue;
                // ★ 跳过金币（它不走 t/st）
                if (string.Equals(r.NameCN, CoinName, StringComparison.Ordinal)) continue;
                list.Add(new ASWDEBUG.UI.AuctionMonitor.NamedId { Id = r.Id, Name = r.NameCN });
            }

            ASWDEBUG.UI.AuctionMonitor.ResolveTypeHintsAsync(
                list,
                3500,
                // 每条回调：更新内存
                delegate (string id, int t, int st)
                {
                    int idx = _rows.FindIndex(rr => rr.Id == id);
                    if (idx >= 0)
                    {
                        _rows[idx].T = t;
                        _rows[idx].ST = st;
                    }
                },
                // 完成：落盘
                delegate ()
                {
                    try
                    {
                        SaveToFile();
                        _status = "读取类型完成，并已保存到：" + GetItemsFilePath();
                    }
                    catch (Exception ex)
                    {
                        _status = "读取类型完成，但保存失败：" + ex.Message;
                    }
                }
            );
        }
        catch (Exception ex)
        {
            _status = "读取类型启动失败：" + ex.Message;
        }
    }

    // ===== 解析（更健壮）：从右往左找“价格”；支持尾部扩展字段 =====
    private static bool TryParseLineRobust(string line, out string id, out string name, out float price)
    {
        id = null; name = null; price = 0f;

        string[] parts = line.Split(new string[] { "=>" }, StringSplitOptions.None);
        if (parts == null || parts.Length < 2) return false;
        for (int i = 0; i < parts.Length; i++) parts[i] = (parts[i] ?? "").Trim();

        // 从右往左找第一个像价格的字段
        int priceIdx = -1; float p = 0f;
        for (int i = parts.Length - 1; i >= 0; i--)
        {
            if (AuctionWatchList.TryParsePrice(parts[i], out p))
            {
                priceIdx = i; break;
            }
        }

        if (priceIdx >= 2)
        {
            price = p;
            name = parts[priceIdx - 1];
            id = parts[priceIdx - 2];

            // 兼容“前缀日志序号”导致 id 落成数字的情况：向前再看一格
            if (IsPureNumber(id) && priceIdx - 3 >= 0)
            {
                string maybeId = parts[priceIdx - 3];
                if (!IsPureNumber(maybeId)) id = maybeId;
            }
            return !string.IsNullOrEmpty(id) && !string.IsNullOrEmpty(name);
        }

        // 没有价格：尽力按末两段回推（老格式）
        if (parts.Length >= 2)
        {
            name = parts[parts.Length - 1];
            id = parts[parts.Length - 2];

            if (IsPureNumber(id) && parts.Length - 3 >= 0)
            {
                string maybeId = parts[parts.Length - 3];
                if (!IsPureNumber(maybeId)) id = maybeId;
            }
            price = 0f;
            return !string.IsNullOrEmpty(id) && !string.IsNullOrEmpty(name);
        }

        return false;
    }

    private static bool IsPureNumber(string s)
    {
        if (string.IsNullOrEmpty(s)) return false;
        for (int i = 0; i < s.Length; i++) if (s[i] < '0' || s[i] > '9') return false;
        return true;
    }

    // ===== 价格格式化 =====
    private static string AutoFormatPrice(string input)
    {
        float f; if (AuctionWatchList.TryParsePrice(input, out f)) return FormatPrice(f);
        return "0.00";
    }
    private static string FormatPrice(float f) { return f.ToString("0.00", CultureInfo.InvariantCulture); }

    // ===== 从一行文本中提取 t/st（允许多种写法：t=3, st=400 或 t:2）=====
    private static bool ExtractTypeHints(string line, out int t, out int st)
    {
        t = -1; st = -1;
        if (string.IsNullOrEmpty(line)) return false;

        var mt = Regex.Match(line, @"\bt\s*[:=]\s*(\d+)", RegexOptions.IgnoreCase);
        var mst = Regex.Match(line, @"\bst\s*[:=]\s*(\d+)", RegexOptions.IgnoreCase);

        if (mt.Success) int.TryParse(mt.Groups[1].Value, out t);
        if (mst.Success) int.TryParse(mst.Groups[1].Value, out st);

        if (t != 2 && t != 3) t = -1; // 只认 2/3（金币不走这里）

        return (t >= 0) || (st >= 0);
    }

    // ===== 提取 watch 标记（watch=1/0/true/false/yes/no/y/n；默认 false）=====
    private static bool ExtractWatchFlag(string line)
    {
        if (string.IsNullOrEmpty(line)) return false;
        var m = Regex.Match(line, @"\bwatch\s*[:=]\s*(\w+)", RegexOptions.IgnoreCase);
        if (!m.Success) return false;
        string v = (m.Groups[1].Value ?? "").Trim().ToLowerInvariant();
        return v == "1" || v == "true" || v == "yes" || v == "y";
    }

    // ===== 与 AuctionMonitor 的“软依赖”桥接 =====
    private static void PushTypeHintToMonitor(string id, int t, int st)
    {
        try
        {
            var monType = typeof(ASWDEBUG.UI.AuctionMonitor);
            var mi = monType.GetMethod("SetTypeHint", BindingFlags.Public | BindingFlags.Static);
            if (mi != null)
            {
                mi.Invoke(null, new object[] { id, t, st });
            }
        }
        catch { /* 忽略 */ }
    }

    private static bool PullTypeHintFromMonitor(string id, out int t, out int st)
    {
        t = -1; st = -1;
        try
        {
            var monType = typeof(ASWDEBUG.UI.AuctionMonitor);
            var mi = monType.GetMethod("TryGetTypeHint", BindingFlags.Public | BindingFlags.Static);
            if (mi != null)
            {
                object[] args = new object[] { id, 0, 0 }; // id, out t, out st
                bool ok = (bool)mi.Invoke(null, args);
                if (ok)
                {
                    t = (int)args[1];
                    st = (int)args[2];
                    return true;
                }
            }
        }
        catch { }
        return false;
    }

    // ===== 样式 & 纹理 =====
    private static void EnsureStylesAndTextures()
    {
        if (_texPanelBg == null) _texPanelBg = MakeSolidTex(new Color(0f, 0f, 0f, 0.35f)); // 半透明灰
        if (_texGrayTrack == null) _texGrayTrack = MakeSolidTex(new Color(0.33f, 0.33f, 0.33f, 1f));
        if (_texGrayThumb == null) _texGrayThumb = MakeSolidTex(new Color(0.55f, 0.55f, 0.55f, 1f));
        if (_scrollBgNone == null) _scrollBgNone = GUIStyle.none;

        if (_vBarGray == null)
        {
            _vBarGray = new GUIStyle(GUI.skin.verticalScrollbar);
            _vBarGray.normal.background = _texGrayTrack;
            _vBarGray.hover.background = _texGrayTrack;
            _vBarGray.active.background = _texGrayTrack;
            _vBarGray.focused.background = _texGrayTrack;
            _vBarGray.fixedWidth = ScrollbarW;
            _vBarGray.margin = new RectOffset(0, 0, 0, 0);
            _vBarGray.padding = new RectOffset(0, 0, 0, 0);
            _vBarGray.border = new RectOffset(0, 0, 0, 0);
        }
        if (_vThumbGray == null)
        {
            _vThumbGray = new GUIStyle(GUI.skin.verticalScrollbarThumb);
            _vThumbGray.normal.background = _texGrayThumb;
            _vThumbGray.hover.background = _texGrayThumb;
            _vThumbGray.active.background = _texGrayThumb;
            _vThumbGray.focused.background = _texGrayThumb;
            _vThumbGray.fixedWidth = ScrollbarW;
            _vThumbGray.margin = new RectOffset(0, 0, 0, 0);
            _vThumbGray.padding = new RectOffset(0, 0, 0, 0);
            _vThumbGray.border = new RectOffset(0, 0, 0, 0);
        }
        if (_hBarGray == null)
        {
            _hBarGray = new GUIStyle(GUI.skin.horizontalScrollbar);
            _hBarGray.normal.background = _texGrayTrack;
            _hBarGray.hover.background = _texGrayTrack;
            _hBarGray.active.background = _texGrayTrack;
            _hBarGray.focused.background = _texGrayTrack;
            _hBarGray.fixedHeight = ScrollbarW;
            _hBarGray.margin = new RectOffset(0, 0, 0, 0);
            _hBarGray.padding = new RectOffset(0, 0, 0, 0);
            _hBarGray.border = new RectOffset(0, 0, 0, 0);
        }
        if (_hThumbGray == null)
        {
            _hThumbGray = new GUIStyle(GUI.skin.horizontalScrollbarThumb);
            _hThumbGray.normal.background = _texGrayThumb;
            _hThumbGray.hover.background = _texGrayThumb;
            _hThumbGray.active.background = _texGrayThumb;
            _hThumbGray.focused.background = _texGrayThumb;
            _hThumbGray.fixedHeight = ScrollbarW;
            _hThumbGray.margin = new RectOffset(0, 0, 0, 0);
            _hThumbGray.padding = new RectOffset(0, 0, 0, 0);
            _hThumbGray.border = new RectOffset(0, 0, 0, 0);
        }
    }

    private static Texture2D MakeSolidTex(Color c)
    {
        Texture2D t = new Texture2D(1, 1, TextureFormat.RGBA32, false);
        t.SetPixel(0, 0, c);
        t.Apply();
        t.wrapMode = TextureWrapMode.Clamp;
        t.filterMode = FilterMode.Point;
        return t;
    }
}
