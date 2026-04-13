//using System;
//using System.Collections.Generic;
//using System.Collections.Specialized;
//using System.Reflection;
//using UniLua;
//using UnityEngine;
//using ASWDEBUG.UI;
//using System.IO;
//using System.Linq;
//using ASWDEBUG.Cheats.Other;
//using Harmony;
//using ASWDEBUG.Logger;
//using System.Text;
//using Pathfinding.RVO;
//using static BossRoomSearchControl;
//using static SkillBarItem;

//public static class SearchPanel
//{
//    // —— UI 状态 —— 
//    public static string _query = "";
//    private static string _query2 = "";
//    private static string _query3 = "";
//    private static Vector2 _scroll;
//    private static int _selectedIndex = -1;

//    // 分页
//    private static int _currentPage = 1;
//    private const int _maxPages = 5;
//    private const int _pageSize = 10;
//    private static int _totalPages = 1;     // 从 friends.pageNum 赋值
//    private const int _pagerWindowSize = 7;  // 固定显示 7 个页码
//    private static int _pageWindowStart = 1; // 当前滑窗起始页

//    // —— 主列表菜单 —— 
//    private static bool _menuOpenMain;
//    private static Rect _menuRectMain;
//    private static int _menuRowMain = -1;
//    private static readonly string[] _menuItemsMain = { "添加好友", "复制名字", "复制ID", "从列表移除", "添加到黑名单" };

//    // —— 黑名单菜单 —— 
//    private static bool _menuOpenBL;
//    private static Rect _menuRectBL;
//    private static int _menuRowBL = -1;
//    private static readonly string[] _menuItemsBL = { "添加好友", "复制名字", "复制ID", "从黑名单移除" };

//    // —— 工具：是否任一菜单打开 —— 
//    private static bool AnyMenuOpen => _menuOpenMain || _menuOpenBL;

//    private static bool _ingestingBL = false;

//    // —— 黑名单持久化 ——（放在类字段区）
//    private static bool _blacklistLoaded = false;
//    private static string BlacklistFilePath =>
//        Path.Combine(Application.persistentDataPath, "blacklist.txt");

//    // —— RPC 测试器 —— //
//    private static bool _rpcVisible = true;                  // 是否显示测试器窗口
//    public static float RpcH = 260f;                        // 窗口高度
//    private static string _rpcName = "";                     // RPC 名
//    private class ParamKV { public string key = ""; public string val = ""; }
//    private static readonly List<ParamKV> _rpcParams = new List<ParamKV>(); // 参数列表
//    private static readonly List<string> _rpcLog = new List<string>(256);   // 输出日志
//    private static Vector2 _rpcLogScroll;                    // 输出滚动


//    // 返回的玩家条目
//    private class PlayerRow
//    {
//        public string playerId;
//        public string playerName;
//        public int playerLevel;
//        public int playerState;     // 1离线 2在线 3游戏中
//        public int playerVipLevel;
//        public int rankLevel;
//        public int rankType;
//        public int occupation;

//        // 预先算好的 sprite 名字
//        public string stateSprite;
//        public string occSprite;
//        public string vipSprite;
//        public string rankSprite;
//    }
//    private static readonly List<PlayerRow> _players = new List<PlayerRow>();

//    // —— 面板参数 —— 
//    public static bool Visible = true;
//    public static float X = 10, Y = 250, W = 400, H = 300;
//    public static float RowH = 24f;
//    public static float RowGap = 8f;

//    // 颜色（保留使用，但不再污染：UIHelper.DrawBox 已修）
//    private static readonly Color ListBg = new Color(0f, 0f, 0f, 0.7f);
//    private static readonly Color ListBorder = new Color(1f, 1f, 1f, 0.15f);
//    private static readonly Color RowAlt = new Color(1f, 1f, 1f, 0.06f);
//    private static readonly Color RowSel = new Color(1f, 1f, 1f, 0.14f);

//    // —— 黑名单面板 —— 
//    private static readonly List<PlayerRow> _blacklist = new List<PlayerRow>();
//    private static Vector2 _blScroll;
//    private static int _blackSel = -1;
//    public static float BlackH = 220f; // 黑名单窗口高度，可按需调整

//    // IME
//    private const string QueryCtrl = "SearchPanel.Query";


//    private static void EnsureBlacklistLoaded()
//    {
//        if (_blacklistLoaded) return;
//        try { LoadBlacklistFromDisk(); }
//        catch { }
//        _blacklistLoaded = true;
//    }

//    public static void Display()
//    {
//        if (!Visible || !CheatUIManager.MenuVisible) return;
//        EnsureBlacklistLoaded();
//        // 标题条
//        const float TitleH = 20f;
//        UIHelper.Begin("玩家搜索", X, Y, W, H, 0f, RowH, RowGap);

//        // —— 模态化捕获：菜单打开时先拦截鼠标 ——
//        // 放在 UIHelper.Begin(...) 之后，绘制其它控件之前
//        if (AnyMenuOpen && Event.current.type == EventType.MouseDown)
//        {
//            Rect activeRect = _menuOpenMain ? _menuRectMain : (_menuOpenBL ? _menuRectBL : new Rect());
//            if (!activeRect.Contains(Event.current.mousePosition))
//            {
//                CloseAllMenus();
//                Event.current.Use();
//            }
//        }

//        float contentLeft = X;        // 不额外留 margin
//        float contentWidth = W;

//        // —— 第一行：输入框 + 右侧“搜索”按钮 —— 
//        const float BtnW = 74f;
//        const float Space = 8f;
//        float curY = Y + TitleH + RowGap;

//        Rect inputRect = new Rect(contentLeft, curY, contentWidth - BtnW - Space, RowH);
//        Rect btnRect = new Rect(inputRect.xMax + Space, curY, BtnW, RowH);

//        // ① 鼠标点到输入框时，立刻设焦点 + 开 IME + 设置候选位置
//        if (!AnyMenuOpen && Event.current.type == EventType.MouseDown && inputRect.Contains(Event.current.mousePosition))
//        {
//            GUI.FocusControl(QueryCtrl);
//            Input.imeCompositionMode = IMECompositionMode.On;
//            Input.compositionCursorPos = new Vector2(
//                inputRect.x + 8f,
//                inputRect.y + inputRect.height * 0.5f
//            );
//        }

//        // ② 再给下一个控件起名并绘制文本框
//        GUI.SetNextControlName(QueryCtrl);
//        //_query = GUI.TextField(inputRect, _query ?? "", UIHelper.TextFieldStyle);
//        _query = GUI.TextField(new Rect(contentLeft, curY, 310, RowH), _query ?? "", UIHelper.TextFieldStyle);
//        //_query2 = GUI.TextField(new Rect(contentLeft + 110, curY, 100, RowH), _query2 ?? "", UIHelper.TextFieldStyle);
//        //_query3 = GUI.TextField(new Rect(contentLeft + 220, curY, 100, RowH), _query3 ?? "", UIHelper.TextFieldStyle);

//        // ③ 搜索按钮
//        if (GUI.Button(btnRect, "搜索", UIHelper.ButtonStyle))
//        {
//            _currentPage = 1;
//            _pageWindowStart = 1; // 让滑窗回到 1..7
//            DoSearch(_query, _currentPage, _pageSize);
//            //DoSearch2(_query, _query2, _query3);

//            CloseAllMenus();
//        }

//        // —— 列表：紧随输入行（不再有“添加好友”行）——
//        float pagerH = RowH; // 预留分页一行
//        float listTop = inputRect.yMax + RowGap;
//        float listH = Mathf.Max(60f, Y + H - listTop - pagerH - RowGap);
//        Rect listRect = new Rect(contentLeft, listTop, contentWidth, listH);

//        // 背景 + 边框（DrawBox 不再污染颜色）
//        UIHelper.DrawBox(new Vector2(listRect.x, listRect.y), new Vector2(listRect.width, listRect.height), ListBg, centered: false);
//        DrawRectBorder(listRect, ListBorder, 1f);

//        // 滚动内容
//        float itemH = RowH;
//        float contentH = Mathf.Max(listRect.height, _players.Count * itemH);
//        Rect contentRect = new Rect(0, 0, listRect.width - 16f, contentH);

//        _scroll = GUI.BeginScrollView(listRect, _scroll, contentRect, false, true);

//        // 鼠标：内容坐标 / 视口坐标
//        Vector2 mpContent = Event.current.mousePosition;         // 在内容坐标系（contentRect）里
//        Vector2 mpView = mpContent - new Vector2(0f, _scroll.y); // 可视区域（view）坐标

//        float y = 0f;
//        for (int i = 0; i < _players.Count; i++)
//        {
//            Rect r = new Rect(0, y, contentRect.width, itemH);

//            // 背景：选中 / 交替行
//            var prev = GUI.color;
//            if (_selectedIndex == i)
//            {
//                GUI.color = RowSel;
//                GUI.DrawTexture(r, Texture2D.whiteTexture, ScaleMode.StretchToFill, true);
//            }
//            else if ((i & 1) == 1)
//            {
//                GUI.color = RowAlt;
//                GUI.DrawTexture(r, Texture2D.whiteTexture, ScaleMode.StretchToFill, true);
//            }
//            GUI.color = prev;

//            // 点击选择（用内容坐标判定）
//            if (!AnyMenuOpen && Event.current.type == EventType.MouseDown && r.Contains(mpContent) && Event.current.button == 0)
//            {
//                _selectedIndex = i;
//                CloseAllMenus();
//            }

//            // 右键菜单（位置用“视口坐标”+ listRect 左上）
//            if (!AnyMenuOpen && Event.current.type == EventType.MouseDown && r.Contains(mpContent) && Event.current.button == 1)
//            {
//                _selectedIndex = i;
//                Vector2 menuPos = new Vector2(listRect.x + mpView.x, listRect.y + mpView.y); // ✅ 修正：随滚动不偏移
//                OpenMenuMain(i, menuPos);
//                Event.current.Use();
//            }

//            // 双击添加
//            if (!AnyMenuOpen && Event.current.type == EventType.MouseDown && r.Contains(mpContent) && Event.current.button == 0 && Event.current.clickCount == 2)
//            {
//                _selectedIndex = i;
//                TryAddFriendSelected();
//                CloseAllMenus();
//                Event.current.Use();
//            }


//            // 行内容：图标 + 文本
//            DrawPlayerRow(r, i, _players[i]);

//            y += itemH;
//        }
//        GUI.EndScrollView();

//        // 分页条（默认样式）
//        DrawPager(new Rect(contentLeft, listRect.yMax + RowGap * 0.5f, contentWidth, pagerH));

//        float blY = Y + H + RowGap;     // 紧贴第一个窗口下方
//        float blX = X;
//        float blW = W;
//        float blH = BlackH;

//        // 标题条
//        UIHelper.Begin("黑名单", blX, blY, blW, blH, 0f, RowH, RowGap);

//        float blContentLeft = blX;
//        float blContentWidth = blW;

//        // 列表区域（保留一行按钮高度 + 间距）
//        float blBtnH = RowH;
//        float blListTop = blY + TitleH + RowGap;
//        float blListH = Mathf.Max(60f, blY + blH - blListTop - blBtnH - RowGap);
//        Rect blListRect = new Rect(blContentLeft, blListTop, blContentWidth, blListH);

//        // 背景+边框（使用 ListBg，不会再污染颜色）
//        UIHelper.DrawBox(new Vector2(blListRect.x, blListRect.y), new Vector2(blListRect.width, blListRect.height), ListBg, centered: false);
//        DrawRectBorder(blListRect, ListBorder, 1f);

//        // 滚动内容
//        float blItemH = RowH;
//        float blContentH = Mathf.Max(blListRect.height, _blacklist.Count * blItemH);
//        Rect blContentRect = new Rect(0, 0, blListRect.width - 16f, blContentH);

//        _blScroll = GUI.BeginScrollView(blListRect, _blScroll, blContentRect, false, true);

//        // 鼠标（内容坐标）
//        Vector2 blMpContent = Event.current.mousePosition;

//        float yy = 0f;
//        for (int i = 0; i < _blacklist.Count; i++)
//        {
//            Rect rr = new Rect(0, yy, blContentRect.width, blItemH);

//            // 行底色（交替/选中）
//            var prev = GUI.color;
//            if (_blackSel == i)
//            {
//                GUI.color = RowSel;
//                GUI.DrawTexture(rr, Texture2D.whiteTexture, ScaleMode.StretchToFill, true);
//            }
//            else if ((i & 1) == 1)
//            {
//                GUI.color = RowAlt;
//                GUI.DrawTexture(rr, Texture2D.whiteTexture, ScaleMode.StretchToFill, true);
//            }
//            GUI.color = prev;

//            // ★ 这里插入右键菜单触发（黑名单列表）
//            if (!AnyMenuOpen && Event.current.type == EventType.MouseDown && rr.Contains(blMpContent) && Event.current.button == 1)
//            {
//                _blackSel = i;

//                // 将滚动内坐标换算为屏幕坐标，避免滚动错位
//                Vector2 blMpView = Event.current.mousePosition - new Vector2(0f, _blScroll.y);
//                Vector2 menuPos = new Vector2(blListRect.x + blMpView.x, blListRect.y + blMpView.y);
//                OpenMenuBL(i, menuPos);
//                Event.current.Use();
//            }

//            // 左键选择
//            if (Event.current.type == EventType.MouseDown && rr.Contains(blMpContent) && Event.current.button == 0 && !AnyMenuOpen)
//            {
//                _blackSel = i;
//            }

//            // 双击可快捷移除
//            if (Event.current.type == EventType.MouseDown && rr.Contains(blMpContent) && Event.current.button == 0 && Event.current.clickCount == 2 && !AnyMenuOpen)
//            {
//                _blackSel = i;
//                TryRemoveFromBlacklistSelected();
//                Event.current.Use();
//            }

//            // 行内容（与上方一致；离线时你之前要求不画状态图标）
//            DrawBlacklistRow(rr, i, _blacklist[i]);

//            yy += blItemH;
//        }
//        GUI.EndScrollView();

//        const float BtnGap = 8f;
//        float BtnH = blBtnH;

//        const float BlBtnW = 100f;
//        Rect blBtnRect = new Rect(blListRect.xMax - BlBtnW, blListRect.yMax + RowGap * 0.5f, BlBtnW, blBtnH);
//        // 右侧第二个（在开始左侧）：保存黑名单
//        Rect btnSaveRect = new Rect(blBtnRect.x - BtnGap - BtnW, blBtnRect.y, BtnW, BtnH);

//        bool prevEnabled = GUI.enabled;
//        GUI.enabled = _blacklist.Count > 0 && !AnyMenuOpen;

//        // 保存黑名单
//        if (GUI.Button(btnSaveRect, "保存黑名单", UIHelper.ButtonStyle))
//        {
//            SaveBlacklistToDisk();
//        }

//        string blBtnText = _ingestingBL ? "停止" : "开始";
//        if (GUI.Button(blBtnRect, blBtnText, UIHelper.ButtonStyle))
//        {
//            // 需要 LINQ：在文件顶部加 using System.Linq;
//            var ids = _blacklist
//                .Select(r => (r?.playerId ?? "").Trim())
//                .Select(s => ulong.TryParse(s, out var v) ? (ulong?)v : null)
//                .Where(v => v.HasValue && v.Value != 0UL)
//                .Select(v => v.Value)
//                .Distinct()
//                .ToList();

//            if (!_ingestingBL)
//            {
//                AutoInterface.characterIds = ids;
//                AutoInterface.BlackListEnabled = true;
//                _ingestingBL = true;   // 切到“停止”
//            }
//            else
//            {
//                AutoInterface.BlackListEnabled = false;
//                AutoInterface.TryStopIngestPlayers();
//                _ingestingBL = false;  // 切回“开始”
//            }
//        }

//        GUI.enabled = prevEnabled;

//        //DrawRpcTester(rpcX, rpcY, rpcW, RpcH);

//        // 顶层绘制右键菜单
//        if (_menuOpenMain) DrawContextMenuMain();
//        if (_menuOpenBL) DrawContextMenuBL();
//    }

//    /// <summary>绘制 RPC 测试器窗口</summary>
//    private static void DrawRpcTester(float x, float y, float w, float h)
//    {
//        if (!_rpcVisible) return;

//        const float TitleH = 20f;
//        const float RowHgt = 24f;
//        const float Gap = 8f;

//        UIHelper.Begin("RPC 调用", x, y, w, h, 0f, RowHgt, Gap);

//        float curY = y + TitleH + Gap;
//        float left = x;
//        float width = w;

//        // 1) 第一行：RPC 名称 + 按钮
//        float btnW = 74f;
//        float addBtnW = 90f;
//        Rect nameRect = new Rect(left, curY, width - btnW - addBtnW - Gap * 2, RowHgt);
//        Rect addParamRect = new Rect(nameRect.xMax + Gap, curY, addBtnW, RowHgt);
//        Rect callRect = new Rect(addParamRect.xMax + Gap, curY, btnW, RowHgt);

//        // 文本框：RPC 名
//        _rpcName = GUI.TextField(nameRect, _rpcName ?? "", UIHelper.TextFieldStyle);

//        // 添加参数行
//        if (GUI.Button(addParamRect, "添加参数", UIHelper.ButtonStyle))
//        {
//            _rpcParams.Add(new ParamKV());
//        }

//        // 发起调用
//        if (GUI.Button(callRect, "调用", UIHelper.ButtonStyle))
//        {
//            TryCallRpc(_rpcName, _rpcParams);
//        }

//        curY += RowHgt + Gap;

//        // 2) 参数列表（每行：key : value  [删除]）
//        const float keyW = 140f;
//        const float sepW = 14f;
//        const float delW = 52f;

//        for (int i = 0; i < _rpcParams.Count; i++)
//        {
//            var p = _rpcParams[i];

//            Rect keyRect = new Rect(left, curY, keyW, RowHgt);
//            Rect sepRect = new Rect(keyRect.xMax + 2f, curY, sepW, RowHgt);
//            Rect valRect = new Rect(sepRect.xMax + 2f, curY, width - (keyW + sepW + delW + Gap * 2), RowHgt);
//            Rect delRect = new Rect(valRect.xMax + Gap, curY, delW, RowHgt);

//            p.key = GUI.TextField(keyRect, p.key ?? "", UIHelper.TextFieldStyle);
//            GUI.Label(sepRect, ":", UIHelper.StringStyle ?? GUI.skin.label);
//            p.val = GUI.TextField(valRect, p.val ?? "", UIHelper.TextFieldStyle);

//            if (GUI.Button(delRect, "删除", UIHelper.ButtonStyle))
//            {
//                _rpcParams.RemoveAt(i);
//                i--;
//                continue;
//            }

//            curY += RowHgt + 4f;
//        }

//        curY += 2f;

//        // 3) 输出区 + 清空按钮
//        const float clrW = 74f;
//        Rect clearRect = new Rect(left + width - clrW, curY, clrW, RowHgt);

//        if (GUI.Button(clearRect, "Clear", UIHelper.ButtonStyle))
//        {
//            _rpcLog.Clear();
//        }

//        curY += RowHgt + 4f;

//        // 输出滚动区域
//        float outH = Math.Max(60f, y + h - curY - Gap);
//        Rect outRect = new Rect(left, curY, width, outH);

//        UIHelper.DrawBox(new Vector2(outRect.x, outRect.y), new Vector2(outRect.width, outRect.height),
//                         new Color(0f, 0f, 0f, 0.7f), centered: false);
//        DrawRectBorder(outRect, new Color(1f, 1f, 1f, 0.15f), 1f);

//        // 把日志拼成一大段（逐行绘制也可以，这里简单一些）
//        var outStyle = UIHelper.StringStyle ?? new GUIStyle(GUI.skin.label);
//        outStyle.alignment = TextAnchor.UpperLeft;
//        outStyle.fontSize = 12;
//        outStyle.wordWrap = true;

//        string joined = (_rpcLog == null || _rpcLog.Count == 0)
//            ? "(无输出)"
//            : string.Join("\n", _rpcLog.ToArray());

//        // 内容高度大致估算（IMGUI 没有自动高度，这里给出比较大的虚拟高度）
//        Rect contentRect = new Rect(0, 0, outRect.width - 16f, Math.Max(outRect.height, _rpcLog.Count * 18f + 20f));
//        _rpcLogScroll = GUI.BeginScrollView(outRect, _rpcLogScroll, contentRect, false, true);
//        GUI.Label(new Rect(4, 4, contentRect.width - 8f, contentRect.height - 8f), joined, outStyle);
//        GUI.EndScrollView();
//    }

//    private static void TryCallRpc(string rpcName, List<ParamKV> ps)
//    {
//        var conn = GameApp.Instance != null ? GameApp.Instance.lobby_connection : null;
//        if (conn == null)
//        {
//            AppendRpcLog("[error] lobby_connection 为空，无法调用");
//            return;
//        }

//        rpcName = (rpcName ?? "").Trim();
//        if (rpcName.Length == 0)
//        {
//            AppendRpcLog("[error] 请输入 RPC 名称");
//            return;
//        }

//        var args = new Dictionary<string, string>();
//        if (ps != null)
//        {
//            foreach (var p in ps)
//            {
//                if (p == null) continue;
//                var k = (p.key ?? "").Trim();
//                if (k.Length == 0) continue;
//                args[k] = p.val ?? "";
//            }
//        }

//        AppendRpcLog("[call] " + rpcName + "  { " +
//                     string.Join(", ", args.Select(kv => kv.Key + ":" + kv.Value).ToArray()) + " }");

//        try
//        {
//            conn.AddTextRpc(rpcName, new LobbyConnection.RpcCallback(OnRpcResult), args);
//        }
//        catch (Exception ex)
//        {
//            AppendRpcLog("[error] AddTextRpc 失败: " + ex);
//        }
//    }

//    // 统一 RPC 回调：原样输出 + 尝试用 UniLua 解析 error
//    private static void OnRpcResult(string data)
//    {
//        try
//        {
//            if (string.IsNullOrEmpty(data))
//            {
//                AppendRpcLog("[result] (空)");
//                return;
//            }

//            // 先把原始内容逐行打印
//            AppendRpcLog("[raw]");
//            string[] lines = data.Split(new string[] { "\r\n", "\n" }, StringSplitOptions.None);
//            for (int i = 0; i < lines.Length; i++)
//            {
//                AppendRpcLog(lines[i]);
//            }

//            // 再尝试用 UniLua 解析
//            UniLua.LuaState L = new UniLua.LuaState(null);
//            L.DoString(data);

//            object errObj = L["error"];
//            if (errObj != null)
//            {
//                string s = errObj.ToString();
//                if (!string.IsNullOrEmpty(s))
//                {
//                    s = s.valueByThisKey();
//                    AppendRpcLog("[error] " + s);
//                    return;
//                }
//            }

//            // 没有 error 或为空
//            AppendRpcLog("[ok] 无 error 字段或为空。");
//        }
//        catch (Exception ex)
//        {
//            AppendRpcLog("[error] 解析回包异常: " + ex);
//        }
//    }

//    // 追加到 RPC 输出区域（带时间戳，限制最大行数）
//    private static void AppendRpcLog(string line)
//    {
//        try
//        {
//            if (line == null) line = "";
//            if (_rpcLog == null) return;
//            _rpcLog.Add("[" + DateTime.Now.ToString("HH:mm:ss.fff") + "] " + line);

//            const int Max = 800;
//            if (_rpcLog.Count > Max)
//            {
//                _rpcLog.RemoveRange(0, _rpcLog.Count - Max);
//            }
//        }
//        catch
//        {
//            // 忽略极端 UI 异常
//        }
//    }

//    private static void DrawContextMenuMain()
//    {
//        // 背板
//        UIHelper.DrawBox(new Vector2(_menuRectMain.x, _menuRectMain.y), new Vector2(_menuRectMain.width, _menuRectMain.height), new Color(0f, 0f, 0f, 0.85f), centered: false);
//        DrawRectBorder(_menuRectMain, new Color(1f, 1f, 1f, 0.2f), 1f);

//        float y = _menuRectMain.y + 1f;
//        for (int i = 0; i < _menuItemsMain.Length; i++)
//        {
//            Rect r = new Rect(_menuRectMain.x + 1f, y, _menuRectMain.width - 2f, 22f);
//            if (GUI.Button(r, _menuItemsMain[i], UIHelper.ButtonStyle))
//            {
//                OnContextMenuClickMain(i);
//                CloseAllMenus();
//                Event.current.Use(); // 防穿透
//            }
//            y += 22f;
//        }
//    }
//    private static void DrawContextMenuBL()
//    {
//        UIHelper.DrawBox(new Vector2(_menuRectBL.x, _menuRectBL.y), new Vector2(_menuRectBL.width, _menuRectBL.height), new Color(0f, 0f, 0f, 0.85f), centered: false);
//        DrawRectBorder(_menuRectBL, new Color(1f, 1f, 1f, 0.2f), 1f);

//        float y = _menuRectBL.y + 1f;
//        for (int i = 0; i < _menuItemsBL.Length; i++)
//        {
//            Rect r = new Rect(_menuRectBL.x + 1f, y, _menuRectBL.width - 2f, 22f);
//            if (GUI.Button(r, _menuItemsBL[i], UIHelper.ButtonStyle))
//            {
//                OnContextMenuClickBL(i);
//                CloseAllMenus();
//                Event.current.Use();
//            }
//            y += 22f;
//        }
//    }
//    private static void OnContextMenuClickMain(int idx)
//    {
//        if (_menuRowMain < 0 || _menuRowMain >= _players.Count) return;
//        var p = _players[_menuRowMain];

//        switch (idx)
//        {
//            case 0: TryAddFriendRow(p); break;                 // 添加好友
//            case 1: Clip.Set(p.playerName ?? ""); break;       // 复制名字
//            case 2: Clip.Set(p.playerId ?? ""); break;         // 复制ID
//            case 3:                                          // 从列表移除（主列表）
//                _players.RemoveAt(_menuRowMain);
//                if (_selectedIndex >= _players.Count) _selectedIndex = _players.Count - 1;
//                break;
//            case 4: AddToBlacklist(p); break;                 // 添加到黑名单（去重）
//        }
//    }

//    private static void OnContextMenuClickBL(int idx)
//    {
//        if (_menuRowBL < 0 || _menuRowBL >= _blacklist.Count) return;
//        var p = _blacklist[_menuRowBL];

//        switch (idx)
//        {
//            case 0: TryAddFriendRow(p); break;                 // 添加好友
//            case 1: Clip.Set(p.playerName ?? ""); break;       // 复制名字
//            case 2: Clip.Set(p.playerId ?? ""); break;         // 复制ID
//            case 3:                                          // 从列表移除（黑名单列表）
//                _blacklist.RemoveAt(_menuRowBL);
//                if (_blackSel >= _blacklist.Count) _blackSel = _blacklist.Count - 1;
//                break;
//        }
//    }
//    // 从任意 PlayerRow 直接发“加好友”
//    private static void TryAddFriendRow(PlayerRow p)
//    {
//        var chat = GameApp.Instance?.chat_connection;
//        if (chat == null || p == null) return;

//        try
//        {
//            chat.SearchAddToFriend(
//                ulong.Parse(p.playerId ?? "0"),
//                p.playerName,
//                p.playerLevel,
//                p.playerState,
//                p.playerVipLevel,
//                p.rankLevel,
//                p.rankType,
//                (byte)p.occupation
//            );
//        }
//        catch { /* 忽略 */ }
//    }

//    // 去重添加到黑名单：按 playerId
//    private static void AddToBlacklist(PlayerRow src)
//    {
//        if (src == null) return;

//        for (int i = 0; i < _blacklist.Count; i++)
//        {
//            if (string.Equals(_blacklist[i].playerId, src.playerId, StringComparison.Ordinal))
//            {
//                _blackSel = i; // 已存在 → 选中
//                return;
//            }
//        }

//        // 深拷贝
//        var copy = new PlayerRow
//        {
//            playerId = src.playerId,
//            playerName = src.playerName,
//            playerLevel = src.playerLevel,
//            playerState = src.playerState,
//            playerVipLevel = src.playerVipLevel,
//            rankLevel = src.rankLevel,
//            rankType = src.rankType,
//            occupation = src.occupation,

//            stateSprite = src.stateSprite,
//            occSprite = src.occSprite,
//            vipSprite = src.vipSprite,
//            rankSprite = src.rankSprite,
//        };

//        _blacklist.Add(copy);
//        _blackSel = _blacklist.Count - 1;
//    }

//    private static void SaveBlacklistToDisk()
//    {
//        try
//        {
//            // 去重：以 playerId 为准
//            var lines = _blacklist
//                .Where(r => r != null && !string.IsNullOrEmpty(r.playerId))
//                .GroupBy(r => r.playerId.Trim())
//                .Select(g =>
//                {
//                    var r = g.First();
//                    var id = r.playerId.Trim();
//                    var name = (r.playerName ?? "").Replace("\t", " "); // 避免制表符混淆
//                    return $"{id}\t{name}";
//                })
//                .ToArray();

//            var dir = Path.GetDirectoryName(BlacklistFilePath);
//            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);

//            File.WriteAllLines(BlacklistFilePath, lines, System.Text.Encoding.UTF8);
//            FileLogger.Log("SearchPanel", $"黑名单已保存: {BlacklistFilePath} (共 {lines.Length} 条)");
//        }
//        catch (System.Exception ex)
//        {
//            FileLogger.Log("SearchPanel", $"保存黑名单失败: {ex}");
//        }
//    }

//    private static void LoadBlacklistFromDisk()
//    {
//        try
//        {
//            _blacklist.Clear();
//            if (!File.Exists(BlacklistFilePath))
//            {
//                Debug.Log($"[SearchPanel] 未找到黑名单文件，将在保存时创建: {BlacklistFilePath}");
//                return;
//            }

//            var lines = File.ReadAllLines(BlacklistFilePath, System.Text.Encoding.UTF8);
//            foreach (var raw in lines)
//            {
//                if (string.IsNullOrEmpty(raw)) continue;
//                if (raw.StartsWith("#")) continue; // 支持注释行

//                var parts = raw.Split('\t');
//                var idStr = parts.Length > 0 ? parts[0].Trim() : "";
//                if (string.IsNullOrEmpty(idStr)) continue;

//                // 可选：校验 id 是否为 ulong
//                if (!ulong.TryParse(idStr, out var _)) continue;

//                var name = parts.Length > 1 ? parts[1] : "(未知)";
//                AddToBlacklistByIdName(idStr, name);
//            }

//            Debug.Log($"[SearchPanel] 黑名单已加载，共 {_blacklist.Count} 条");
//        }
//        catch (System.Exception ex)
//        {
//            Debug.LogError($"[SearchPanel] 读取黑名单失败: {ex}");
//        }
//    }

//    // 仅通过 id+name 添加到黑名单（去重）
//    private static void AddToBlacklistByIdName(string id, string name)
//    {
//        if (string.IsNullOrEmpty(id)) return;
//        string norm = id.Trim();

//        for (int i = 0; i < _blacklist.Count; i++)
//        {
//            if (string.Equals(_blacklist[i].playerId, norm, System.StringComparison.Ordinal))
//            {
//                // 已存在则只更新名字（如果原来没有/是默认），可选
//                if (string.IsNullOrEmpty(_blacklist[i].playerName) || _blacklist[i].playerName == "(未知)")
//                    _blacklist[i].playerName = name ?? _blacklist[i].playerName;
//                return;
//            }
//        }

//        var row = new PlayerRow
//        {
//            playerId = norm,
//            playerName = string.IsNullOrEmpty(name) ? "(未知)" : name,
//            // 其它数值默认 0；图标留空即可
//            playerLevel = 0,
//            playerState = 1, // 默认当作离线
//        };

//        _blacklist.Add(row);
//        _blackSel = _blacklist.Count - 1;
//    }


//    private static void DrawBlacklistRow(Rect rowContentRect, int index, PlayerRow p)
//    {
//        float iconSize = rowContentRect.height - 6f;
//        float x = rowContentRect.x + 6f;

//        bool isOffline = (p.playerState == 1);

//        // 状态图标：离线不绘制
//        if (!isOffline && !string.IsNullOrEmpty(p.stateSprite))
//            NguiIcon.DrawSprite(rowContentRect, ref x, iconSize, p.stateSprite);

//        if (!isOffline && !string.IsNullOrEmpty(p.occSprite))
//            NguiIcon.DrawSprite(rowContentRect, ref x, iconSize, p.occSprite);

//        if (!isOffline && !string.IsNullOrEmpty(p.vipSprite))
//            NguiIcon.DrawSprite(rowContentRect, ref x, iconSize, p.vipSprite);

//        if (!isOffline && !string.IsNullOrEmpty(p.rankSprite))
//            NguiIcon.DrawSprite(rowContentRect, ref x, iconSize, p.rankSprite);

//        x += 8f;

//        var s = UIHelper.StringStyle ?? new GUIStyle(GUI.skin.label);
//        s.alignment = TextAnchor.MiddleLeft;
//        s.fontSize = 13;

//        string stateStr = isOffline ? "离线" : (p.playerState == 3 ? "游戏中" : "在线");
//        string line = $"{p.playerName}    Lv.{p.playerLevel}    [{stateStr}]";

//        GUI.Label(new Rect(x, rowContentRect.y, rowContentRect.width - (x - rowContentRect.x) - 8f, rowContentRect.height), line, s);
//    }

//    private static void TryRemoveFromBlacklistSelected()
//    {
//        if (_blackSel < 0 || _blackSel >= _blacklist.Count) return;
//        _blacklist.RemoveAt(_blackSel);
//        if (_blackSel >= _blacklist.Count) _blackSel = _blacklist.Count - 1;
//    }

//    private static void DrawPager(Rect area)
//    {
//        int start = _pageWindowStart;
//        int end = Mathf.Min(_pageWindowStart + _pagerWindowSize - 1, _totalPages);

//        int buttonCount = (end - start + 1) + 2; // 数字页 + 左右箭头
//        float gap = 6f;
//        float btnW = Mathf.Floor((area.width - gap * (buttonCount - 1)) / buttonCount);
//        float x = area.x;
//        float y = area.y;
//        float h = area.height;

//        // ← 上一页
//        bool canPrev = _currentPage > 1;
//        bool saved = GUI.enabled;
//        GUI.enabled = canPrev;
//        if (GUI.Button(new Rect(x, y, btnW, h), "←", UIHelper.ButtonStyle))
//        {
//            _currentPage = Mathf.Max(1, _currentPage - 1);
//            EnsurePagerWindow();
//            DoSearch(_query, _currentPage, _pageSize);
//            CloseAllMenus();
//        }
//        GUI.enabled = saved;
//        x += btnW + gap;

//        // 数字页（7 格滑窗）
//        for (int p = start; p <= end; p++)
//        {
//            bool isCur = (p == _currentPage);
//            var style = new GUIStyle(UIHelper.ButtonStyle);
//            if (isCur) style.fontStyle = FontStyle.Bold;

//            if (GUI.Button(new Rect(x, y, btnW, h), p.ToString(), style))
//            {
//                if (_currentPage != p)
//                {
//                    _currentPage = p;
//                    EnsurePagerWindow();
//                    DoSearch(_query, _currentPage, _pageSize);
//                    CloseAllMenus();
//                }
//            }
//            x += btnW + gap;
//        }

//        // → 下一页
//        bool canNext = _currentPage < _totalPages;
//        saved = GUI.enabled;
//        GUI.enabled = canNext;
//        if (GUI.Button(new Rect(x, y, btnW, h), "→", UIHelper.ButtonStyle))
//        {
//            _currentPage = Mathf.Min(_totalPages, _currentPage + 1);
//            EnsurePagerWindow();
//            DoSearch(_query, _currentPage, _pageSize);
//            CloseAllMenus();
//        }
//        GUI.enabled = saved;
//    }
//    private static void DrawPlayerRow(Rect rowContentRect, int index, PlayerRow p)
//    {
//        float iconSize = rowContentRect.height - 6f;
//        float x = rowContentRect.x + 6f;

//        bool isOffline = (p.playerState == 1);
//        if (!isOffline && !string.IsNullOrEmpty(p.stateSprite))
//            NguiIcon.DrawSprite(rowContentRect, ref x, iconSize, p.stateSprite);

//        if (!string.IsNullOrEmpty(p.occSprite))
//            NguiIcon.DrawSprite(rowContentRect, ref x, iconSize, p.occSprite);

//        if (!string.IsNullOrEmpty(p.vipSprite))
//            NguiIcon.DrawSprite(rowContentRect, ref x, iconSize, p.vipSprite);

//        if (!string.IsNullOrEmpty(p.rankSprite))
//            NguiIcon.DrawSprite(rowContentRect, ref x, iconSize, p.rankSprite);

//        x += 8f;

//        var s = UIHelper.StringStyle ?? new GUIStyle(GUI.skin.label);
//        s.alignment = TextAnchor.MiddleLeft;
//        s.fontSize = 13;
//        s.normal.textColor = Color.white;

//        string stateStr = (p.playerState == 1) ? "离线" : (p.playerState == 3 ? "游戏中" : "在线");
//        string line = $"{p.playerName}    Lv.{p.playerLevel}    [{stateStr}]";

//        GUI.Label(new Rect(x, rowContentRect.y, rowContentRect.width - (x - rowContentRect.x) - 8f, rowContentRect.height), line, s);
//    }

//    private static void OpenMenuMain(int row, Vector2 menuTopLeft)
//    {
//        _menuRowMain = row;
//        float itemH = 22f;
//        float w = 140f;
//        float h = _menuItemsMain.Length * itemH + 2f;
//        _menuRectMain = new Rect(menuTopLeft.x, menuTopLeft.y, w, h);

//        // 只留主菜单，关掉黑名单菜单
//        _menuOpenBL = false;
//        _menuOpenMain = true;
//    }

//    private static void OpenMenuBL(int row, Vector2 menuTopLeft)
//    {
//        _menuRowBL = row;
//        float itemH = 22f;
//        float w = 140f;
//        float h = _menuItemsBL.Length * itemH + 2f;
//        _menuRectBL = new Rect(menuTopLeft.x, menuTopLeft.y, w, h);

//        // 只留黑名单菜单，关掉主菜单
//        _menuOpenMain = false;
//        _menuOpenBL = true;
//    }

//    private static void CloseAllMenus()
//    {
//        _menuOpenMain = false; _menuRowMain = -1;
//        _menuOpenBL = false; _menuRowBL = -1;
//    }

//    private static void TryAddFriendSelected()
//    {
//        if (_selectedIndex < 0 || _selectedIndex >= _players.Count) return;
//        TryAddFriend(_selectedIndex);
//    }

//    private static void TryAddFriend(int idx)
//    {
//        var p = _players[idx];
//        var chat = GameApp.Instance?.chat_connection;
//        if (chat == null) return;

//        try
//        {
//            chat.SearchAddToFriend(
//                ulong.Parse(p.playerId),
//                p.playerName,
//                p.playerLevel,
//                p.playerState,
//                p.playerVipLevel,
//                p.rankLevel,
//                p.rankType,
//                (byte)p.occupation
//            );
//        }
//        catch { /* 忽略 */ }
//    }
//    public static void DoSearchFromFileRaw(
//    string path = @"C:\Users\x1a0reN\Desktop\output_log.txt",
//    string page = "1",
//    string pageSize = "10")
//    {
//        string fileText;
//        try
//        {
//            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
//            using (var sr = new StreamReader(fs, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, bufferSize: 8192))
//            {
//                fileText = sr.ReadToEnd(); // 原样读取，不做任何清洗/截断/替换
//            }
//        }
//        catch (System.Exception ex)
//        {
//            UnityEngine.Debug.LogError($"[SearchPanel] 读取文件失败: {path}\n{ex}");
//            return;
//        }

//        DoSearch2(fileText ?? string.Empty, page, pageSize);
//    }
//    // 发送搜索 RPC
//    private static void DoSearch(string key, int page, int pageSize)
//    {
//        _players.Clear();
//        _selectedIndex = -1;
//        CloseAllMenus();

//        var conn = GameApp.Instance?.lobby_connection;
//        if (conn == null) return;

//        var args = new Dictionary<string, string>
//        {
//            { "currentPage", page.ToString() },
//            { "name", key ?? string.Empty },
//            { "pageSize", pageSize.ToString() }
//        };

//        conn.AddTextRpc("friend_search", new LobbyConnection.RpcCallback(OnSearchResult), args);
//    }
//    private static void DoSearch2(string key, string page, string pageSize)
//    {
//        _players.Clear();
//        _selectedIndex = -1;
//        CloseAllMenus();

//        var conn = GameApp.Instance?.lobby_connection;
//        if (conn == null) return;

//        var args = new Dictionary<string, string>
//        {
//            { "currentPage", page },
//            { "name", key },
//            { "pageSize", pageSize }
//        };

//        conn.AddTextRpc("friend_search", new LobbyConnection.RpcCallback(OnSearchResult), args);
//    }
//    // 分页：依赖于类字段 _currentPage / _totalPages / _pagerWindowSize(=7) / _pageWindowStart
//    private static void OnSearchResult(string data)
//    {
//        try
//        {
//            var L = new LuaState(null);
//            L.DoString(data);

//            // 错误检查
//            var errObj = L["error"];
//            if (errObj != null && !string.IsNullOrEmpty(errObj.ToString()))
//            {
//                FileLogger.Log("error", errObj.ToString());
//                return;
//            }


//            // friends 表
//            var friends = L.GetTable("friends");
//            if (friends == null) return;

//            // —— 分页信息（使用 pageNum 作为总页数）——
//            int cur = SafeInt(friends["currentPage"]);
//            int pageNum = SafeInt(friends["pageNum"]);
//            if (cur <= 0) cur = 1;
//            if (pageNum <= 0) pageNum = 1;

//            _totalPages = Mathf.Max(1, pageNum);
//            _currentPage = Mathf.Clamp(cur, 1, _totalPages);

//            // —— 读取列表 —— 
//            var listTable = friends["list"] as LuaTable;
//            if (listTable == null)
//            {
//                _players.Clear();
//                _selectedIndex = -1;
//                return;
//            }

//            ListDictionary dict = L.GetTableDict(listTable);

//            _players.Clear();
//            foreach (object v in dict.Values)
//            {
//                var t = v as LuaTable;
//                if (t == null) continue;

//                var row = new PlayerRow
//                {
//                    playerId = t["playerId"]?.ToString() ?? "0",
//                    playerName = t["playerName"]?.ToString() ?? "(未知)",
//                    playerLevel = SafeInt(t["playerLevel"]),
//                    playerState = SafeInt(t["playerState"]),
//                    playerVipLevel = SafeInt(t["playerVipLevel"]),
//                    rankLevel = SafeInt(t["rankLevel"]),
//                    rankType = SafeInt(t["rankType"]),
//                    occupation = SafeInt(t["occupation"]),
//                };

//                // 预计算 sprite 名称（保持你原逻辑）
//                row.stateSprite = (row.playerState == 3) ? "skin_gam_playicon" : "skin_gam_humanicon_normal";
//                row.occSprite = "skin_common_icon0" + (row.occupation + 1);
//                row.vipSprite = (row.playerVipLevel == 0) ? null :
//                                  (row.playerVipLevel != -1 ? ("skin_vip_lv" + row.playerVipLevel) : "skin_vip_temp");
//                try
//                {
//                    row.rankSprite = global::UITools.GetRankSpriteName(row.rankType, row.rankLevel);
//                }
//                catch { row.rankSprite = null; }

//                _players.Add(row);
//            }

//            // 默认选中第一项（若有）
//            _selectedIndex = (_players.Count > 0) ? 0 : -1;

//            // —— 7 格滑窗分页校准 —— 
//            // 允许的最大起点：确保还能显示 7 个按钮
//            int maxStart = Mathf.Max(1, _totalPages - _pagerWindowSize + 1);

//            // 当前页跑到滑窗左侧之外 → 左移
//            if (_currentPage < _pageWindowStart)
//                _pageWindowStart = _currentPage;

//            // 当前页跑到滑窗右侧之外 → 右移
//            if (_currentPage > _pageWindowStart + _pagerWindowSize - 1)
//                _pageWindowStart = _currentPage - _pagerWindowSize + 1;

//            _pageWindowStart = Mathf.Clamp(_pageWindowStart, 1, maxStart);
//        }
//        catch
//        {
//            // 忽略解析异常
//        }
//    }

//    private static void EnsurePagerWindow()
//    {
//        // 滑窗允许的最大起点：确保还能放下 7 个按钮
//        int maxStart = Mathf.Max(1, _totalPages - _pagerWindowSize + 1);

//        // 若当前页在滑窗左边之外 → 左移
//        if (_currentPage < _pageWindowStart)
//            _pageWindowStart = _currentPage;

//        // 若当前页在滑窗右边之外 → 右移
//        if (_currentPage > _pageWindowStart + _pagerWindowSize - 1)
//            _pageWindowStart = _currentPage - _pagerWindowSize + 1;

//        _pageWindowStart = Mathf.Clamp(_pageWindowStart, 1, maxStart);
//    }

//    // —— 工具们 —— 
//    private static int SafeInt(object o)
//    {
//        if (o == null) return 0;
//        int v; return int.TryParse(o.ToString(), out v) ? v : 0;
//    }

//    private static void DrawRectBorder(Rect r, Color col, float width)
//    {
//        Vector2 a = new Vector2(r.x, r.y);
//        Vector2 b = new Vector2(r.x + r.width, r.y);
//        Vector2 c = new Vector2(r.x + r.width, r.y + r.height);
//        Vector2 d = new Vector2(r.x, r.y + r.height);
//        UIHelper.DrawLine(a, b, col, width);
//        UIHelper.DrawLine(b, c, col, width);
//        UIHelper.DrawLine(c, d, col, width);
//        UIHelper.DrawLine(d, a, col, width);
//    }

//    // 兼容所有 Unity 版本的剪贴板设置（保留）
//    private static class Clip
//    {
//        public static void Set(string s)
//        {
//            s = s ?? string.Empty;

//            // 1) GUIUtility.systemCopyBuffer
//            try
//            {
//                var prop = typeof(GUIUtility).GetProperty("systemCopyBuffer",
//                    BindingFlags.Public | BindingFlags.Static);
//                if (prop != null && prop.CanWrite)
//                {
//                    prop.SetValue(null, s, null);
//                    return;
//                }
//            }
//            catch { /* ignore */ }

//            // 2) Windows P/Invoke 兜底
//            try
//            {
//                var bytes = System.Text.Encoding.Unicode.GetBytes(s + "\0");
//                System.UIntPtr size = (System.UIntPtr)(uint)bytes.Length;
//                var hGlobal = GlobalAlloc(GMEM_MOVEABLE, size);
//                if (hGlobal == System.IntPtr.Zero) return;

//                var target = GlobalLock(hGlobal);
//                if (target == System.IntPtr.Zero) return;

//                System.Runtime.InteropServices.Marshal.Copy(bytes, 0, target, bytes.Length);
//                GlobalUnlock(hGlobal);

//                if (!OpenClipboard(System.IntPtr.Zero)) return;
//                EmptyClipboard();
//                SetClipboardData(CF_UNICODETEXT, hGlobal);
//                CloseClipboard();
//                return;
//            }
//            catch { }

//            Debug.LogWarning("Clipboard copy failed.");
//        }

//        private const uint CF_UNICODETEXT = 13;
//        private const uint GMEM_MOVEABLE = 0x0002;

//        [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
//        private static extern bool OpenClipboard(System.IntPtr hWndNewOwner);

//        [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
//        private static extern bool CloseClipboard();

//        [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
//        private static extern bool EmptyClipboard();

//        [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
//        private static extern System.IntPtr SetClipboardData(uint uFormat, System.IntPtr hMem);

//        [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
//        private static extern System.IntPtr GlobalAlloc(uint uFlags, System.UIntPtr dwBytes);

//        [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
//        private static extern System.IntPtr GlobalLock(System.IntPtr hMem);

//        [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
//        private static extern bool GlobalUnlock(System.IntPtr hMem);
//    }

//    // 在 IMGUI 里从 NGUI Atlas 画 sprite
//    private static class NguiIcon
//    {
//        private struct Entry
//        {
//            public Texture tex;  // atlas 的 mainTexture
//            public Rect uv;      // 该 sprite 的 UV
//        }
//        private static readonly Dictionary<string, Entry> _cache = new Dictionary<string, Entry>(128);

//        // rowRect：内容坐标；x：当前绘制起点（会被推进）；size：正方形像素
//        public static void DrawSprite(Rect rowRect, ref float x, float size, string spriteName, bool dim = false, float dimMul = 0.5f, float alpha = 1f)
//        {
//            if (string.IsNullOrEmpty(spriteName)) return;

//            if (!TryGet(spriteName, out var e))
//            {
//                // 找不到：占位（保持你原有逻辑，注意有保存/还原 GUI.color，不污染）
//                var prev = GUI.color;
//                GUI.color = new Color(1f, 1f, 1f, 0.06f);
//                GUI.DrawTexture(new Rect(x, rowRect.y + (rowRect.height - size) * 0.5f, size, size), Texture2D.whiteTexture);
//                GUI.color = prev;

//                var s = UIHelper.StringStyle ?? new GUIStyle(GUI.skin.label);
//                s.alignment = TextAnchor.MiddleCenter;
//                s.fontSize = Mathf.Clamp(Mathf.RoundToInt(size * 0.6f), 10, 16);
//                s.normal.textColor = Color.white;

//                string abbr = spriteName.Substring(0, 1).ToUpperInvariant();
//                GUI.Label(new Rect(x, rowRect.y, size, rowRect.height), abbr, s);

//                x += size + 4f;
//                return;
//            }

//            Rect dst = new Rect(x, rowRect.y + (rowRect.height - size) * 0.5f, size, size);

//            // ✅ 仅此处局部调色并立即还原，不会污染其它 UI
//            var prevColor = GUI.color;
//            if (dim)
//            {
//                // dimMul 越小越暗，alpha 控制透明度；这里默认 0.5 比较像“灰掉”
//                GUI.color = new Color(dimMul, dimMul, dimMul, alpha);
//            }

//            GUI.DrawTextureWithTexCoords(dst, e.tex, e.uv);
//            GUI.color = prevColor;

//            x += size + 4f;
//        }


//        private static bool TryGet(string name, out Entry ent)
//        {
//            if (_cache.TryGetValue(name, out ent)) return true;

//            var atlases = Resources.FindObjectsOfTypeAll<global::UIAtlas>();
//            for (int i = 0; i < atlases.Length; i++)
//            {
//                var atlas = atlases[i];
//                if (atlas == null) continue;

//                global::UISpriteData sd = atlas.GetSprite(name);
//                if (sd == null) continue;

//                Texture tex = null;
//                try { tex = atlas.spriteMaterial != null ? atlas.spriteMaterial.mainTexture : null; } catch { }
//                if (tex == null || tex.width == 0 || tex.height == 0) continue;

//                float tw = tex.width;
//                float th = tex.height;

//                // NGUI 左上为原点，IMGUI 左下为原点，需要翻转 Y
//                float u = sd.x / tw;
//                float v = 1f - (sd.y + sd.height) / th;
//                float w = sd.width / tw;
//                float h = sd.height / th;

//                ent = new Entry { tex = tex, uv = new Rect(u, v, w, h) };
//                _cache[name] = ent;
//                return true;
//            }

//            ent = default;
//            return false;
//        }
//    }
//}
