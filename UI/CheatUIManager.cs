using ASWDEBUG.Cheats.AimTrack;
using ASWDEBUG.Cheats.AutoAim;
using ASWDEBUG.Cheats.ESP;
using ASWDEBUG.Cheats.Other;
using ASWDEBUG.Cheats.Player;
using ASWDEBUG.Global;
using ASWDEBUG.Main;
using ASWDEBUG.Verify;
using PluginTool;
using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;


namespace ASWDEBUG.UI
{
    public static class GlobalHotkeys
    {
        // 全局变量：把用户选的按键写到这里
        public static KeyCode PlayerKey = KeyCode.Mouse1;
    }

    public class CheatUIManager
    {
        public static bool MenuVisible;
        public static bool SpriteMenuVisible;

        // 是否在“等待按键”模式
        private static bool _waitingForKey;
        // 记录进入等待模式的帧，用来防止把点击按钮的那一下当作绑定
        private static int _armFrame;

        static readonly List<string> _cardSnapshot = new List<string>();
        static readonly List<CardInfo> _cardSnapshotInfo = new List<CardInfo>(16);

        // ======= 卡片小部件：一个格子包含 4 个组件 =======
        class CardWidget
        {
            public GameObject go;          // 根节点（挂在 _cardIconRoot 下面）
            public UISprite quality;       // 品质框
            public UISprite icon;          // 物品图标
            public UILabel number;        // 数量
            public UISprite star;          // 星级

            // 缓存上一次设置，避免重复改图引起重建
            public string lastIconName;
            public int lastType;
            public string lastItemId;
            public int lastUnitType;
            public int lastNum;
            public int lastQuality;
            public int lastTargetReachCount;
        }

        static readonly List<CardWidget> _cardWidgets = new List<CardWidget>(32);
        static Transform _cardIconRoot;
        static Rect _cardAreaLast;
        static bool _cardAreaValid;

        // 布局：每行 3 个
        const int ICONS_PER_ROW = 3;
        const int CELL_SIZE = 64;   // 整个格子宽高（包含品质框、icon）
        const int CELL_GAP = 8;    // 格子间距
        const int ICON_SIZE = 48;   // 实际 icon 尺寸（放在品质框里居中）

        // ===== 新增缓存 =====
        static Camera _uiCamera;
        static UIPanel _uiPanel;
        static int _uiLayer = -1;
        static bool _uiReady;

        // 可选：单独开关（默认开）
        public static bool CardIconsEnabled = true;

        // 统一隐藏（在大厅或关开关时调用）
        static void HideAllCardIcons()
        {
            for (int i = 0; i < _cardWidgets.Count; i++)
                SetCardWidgetActive(_cardWidgets[i], false);
            _cardAreaValid = false;
        }


        public static void Display()
        {
            var et = Event.current != null ? Event.current.type : EventType.Repaint;

            if (ESP.Enabled){ ESP.Enable(); } else{ESP.Disable();}

            if (!MenuVisible) return;

            GUI.color = Color.white;

            // Player
            UIHelper.Begin("玩家", 10, 10, 150, 170, 0, 22, 0);
            UIHelper.Button("红名透视", HealthBarDisplay.Enabled, HealthBarDisplay.Toggle);
            UIHelper.Button("爆炸免伤", GrenadeNotHurt.Enabled, GrenadeNotHurt.Toggle);
            UIHelper.Button("自动扳机", AutoFire.Enabled, AutoFire.Toggle);
            UIHelper.Button("自动攻击", AutoFire.AutoFireAllowed, AutoFire.ToggleAutoFireAllowed);
            UIHelper.Button("无视挂机", NotKick.Enabled, NotKick.Toggle);
            UIHelper.Button("爆炸半伤", GrenadeHalfHurt.Enabled, GrenadeHalfHurt.Toggle);
            UIHelper.Button("子弹直线", BulletNoRecoil.Enabled, BulletNoRecoil.Toggle);
            //UIHelper.Button("子弹速射", WeaponNotCD.Enabled, WeaponNotCD.Toggle);
            //UIHelper.Button("瞬移秒杀", Aike.Enabled, Aike.Toggle);
            //UIHelper.Button("自动锁血", AutoLockHP.Enabled, AutoLockHP.Toggle);
            UIHelper.Button("超级大陀螺", SpinTop.Enabled, SpinTop.Toggle);
            //UIHelper.LabelAuto("到期时间: " + EyAuthManager.Instance.StaticExpiredText);

            // ===== Layout 阶段：做快照/决定隐藏 =====
            if (et == EventType.Layout)
            {
                _cardSnapshotInfo.Clear();

                bool canShow = (CheatMain.inChannel && OtherC.Enabled && CardIconsEnabled);
                if (canShow && CheatMain.CardData != null)
                {
                    _cardSnapshotInfo.AddRange(CheatMain.CardData);
                }
                else
                {
                    HideAllCardIcons(); // 在大厅 / 关开关 / 无数据 时，直接全隐藏
                }
            }

            // ===== 绘制“翻牌奖励” =====
            if (CheatMain.inChannel && OtherC.Enabled && CardIconsEnabled)
            {
                UIHelper.LabelAuto("翻牌奖励:");

                int count = _cardSnapshotInfo.Count;
                if (count <= 0)
                {
                    HideAllCardIcons();
                }
                else
                {
                    int rows = Mathf.Max(1, Mathf.CeilToInt(count / (float)ICONS_PER_ROW));
                    float blockH = rows * CELL_SIZE + (rows - 1) * CELL_GAP;
                    Rect area = UIHelper.NextRectFlexible(blockH);

                    if (et == EventType.Layout)
                    {
                        EnsureCardIconRoot();     // 这里也会顺带保证 UI 相机就绪
                        EnsureWidgetPool(count);

                        for (int i = 0; i < _cardWidgets.Count; i++)
                        {
                            bool active = (i < count);
                            SetCardWidgetActive(_cardWidgets[i], active);
                            if (active)
                                UpdateWidgetFromInfo(_cardWidgets[i], _cardSnapshotInfo[i]);
                        }

                        _cardAreaLast = area;
                        _cardAreaValid = true;
                    }

                    if (et == EventType.Repaint && _cardAreaValid)
                        LayoutWidgetsInArea(_cardAreaLast);
                }
            }



            // AutoAim
            UIHelper.Begin("自瞄设置", 165, 10, 165, 88, 0, 22, 0);
            UIHelper.Button("开启", AutoAim.Enabled, AutoAim.ToggleEnabled);
            UIHelper.Button("是否判断墙体", AutoAim.Wall, AutoAim.ToggleWall);
            UIHelper.Button("是否判断盾牌", AutoAim.Shield, AutoAim.ToggleShield);
            UIHelper.Button("是否判断隐身", AutoAim.Hidden, AutoAim.ToggleHidden);
            string btnText = _waitingForKey
                ? "设置按键"
                : $"{GetKeyDisplayName(GlobalHotkeys.PlayerKey)}";

            if (UIHelper.Button(btnText))
            {
                _waitingForKey = true;
                _armFrame = Time.frameCount; // ★ 这一帧不捕获
            }

            if (_waitingForKey)
            {
                Event e = Event.current;

                // 仅从下一帧开始捕获，避免把点击按钮的 MouseUp/Down 吃掉
                if (Time.frameCount > _armFrame)
                {
                    // 键盘
                    if (e.type == EventType.KeyDown && e.keyCode != KeyCode.None)
                    {
                        if (e.keyCode == KeyCode.Escape) // 可选：Esc 取消
                        {
                            _waitingForKey = false;
                            e.Use();
                            return;
                        }

                        GlobalHotkeys.PlayerKey = e.keyCode;
                        _waitingForKey = false;
                        e.Use();
                        return;
                    }

                    // ★ 新增：鼠标左/右/中键
                    if (e.type == EventType.MouseDown)
                    {
                        switch (e.button)
                        {
                            case 0: GlobalHotkeys.PlayerKey = KeyCode.Mouse0; break; // 左键
                            case 1: GlobalHotkeys.PlayerKey = KeyCode.Mouse1; break; // 右键
                            case 2: GlobalHotkeys.PlayerKey = KeyCode.Mouse2; break; // 中键
                            default: return; // 其它按键（侧键）这里先不处理
                        }
                        _waitingForKey = false;
                        e.Use();
                        return;
                    }
                }
            }

            // AimTrack
            UIHelper.Begin("子弹追踪", 165, 145, 165, 80, 0, 20, 0);
            UIHelper.Button("开启", AimTrack.Enabled, AimTrack.ToggleEnabled);
            UIHelper.Button("是否判断墙体", AimTrack.Wall, AimTrack.ToggleWall);
            UIHelper.Button("是否判断隐身", AimTrack.Hidden, AimTrack.ToggleHidden);
            UIHelper.Button("是否判断盾牌朝向", AimTrack.Shield, AimTrack.ToggleShield);
            ESP.CircleRadius = UIHelper.SliderRow("范围半径", ESP.CircleRadius, 0f, 800f, 0);

            // ESP
            UIHelper.Begin("ESP", 335, 10, 165, 120, 0, 22, 0);
            UIHelper.Button("开启", ESP.Enabled, ESP.ToggleEnabled);
            UIHelper.Button("信息卡片", ESP.InfoEsp, ESP.ToggleInfoEsp);
            UIHelper.Button("骨骼方框", ESP.D3BoxEsp, ESP.ToggleD3BoxEsp);
            UIHelper.Button("绘制十字", ESP.CrossEsp, ESP.ToggleCrossEsp);
            UIHelper.Button("绘制圆心", ESP.CircleEsp, ESP.ToggleCircleEsp);
            UIHelper.Button("绘制射线", ESP.LineEsp, ESP.ToggleLineEsp);

            // Other
            UIHelper.Begin("其他", 505, 10, 165, 60, 0, 22, 0);
            UIHelper.Button("自动防踢", AutoKick.Enabled, AutoKick.Toggle);
            //UIHelper.Button("自动拉对局频道", AutoInterface.Enabled, AutoInterface.Toggle);
            UIHelper.Button("屏蔽所有弹窗", HookMsgbox.Enabled, HookMsgbox.Toggle);
            UIHelper.Button("翻牌透视", OtherC.Enabled, OtherC.Toggle);
            UIHelper.Button("取消验证", OtherC.EnabledVeryify, OtherC.ToggleEnabledVeryify);
            UIHelper.Button("锁定BOSS", OtherC.BossEnabled, OtherC.ToggleBossEnabled);
            UIHelper.Button("叠刀BOSS", OtherC.KnifeEnabled, OtherC.ToggleKnifeEnabled);
            UIHelper.Button("秒过小关(同时开上两个功能即可)");
            //if (UIHelper.Button("关闭服务器"))
            //{
            //    OtherC.Boom();
            //}

            //AuctionItemDrawer.Draw(10, 320, 600, 400);

            if (!SpriteMenuVisible) return;
            //SpriteListDrawer.DrawSpriteList(700, 10, 520, 620);
        }
        private static string GetKeyDisplayName(KeyCode key)
        {
            switch (key)
            {
                // ★ 新增：鼠标友好名
                case KeyCode.Mouse0: return "鼠标左键";
                case KeyCode.Mouse1: return "鼠标右键";
                case KeyCode.Mouse2: return "鼠标中键";

                case KeyCode.Space: return "Space";
                case KeyCode.Return: return "Enter";
                case KeyCode.Tab: return "Tab";
                case KeyCode.BackQuote: return "`";
                case KeyCode.Escape: return "Esc";
                case KeyCode.LeftShift: return "Left Shift";
                case KeyCode.RightShift: return "Right Shift";
                case KeyCode.LeftControl: return "Left Ctrl";
                case KeyCode.RightControl: return "Right Ctrl";
                case KeyCode.LeftAlt: return "Left Alt";
                case KeyCode.RightAlt: return "Right Alt";
                case KeyCode.UpArrow: return "↑";
                case KeyCode.DownArrow: return "↓";
                case KeyCode.LeftArrow: return "←";
                case KeyCode.RightArrow: return "→";
                default: return key.ToString();
            }
        }

        static void EnsureCardIconRoot()
        {
            if (_uiReady && _cardIconRoot && _uiCamera) return;

            if (_uiLayer == -1) _uiLayer = LayerMask.NameToLayer("UI");
            if (_uiLayer < 0) _uiLayer = 5; // 兜底用默认 UI 层（5）

            // 1) 根节点（不挂 UIRoot）
            var rootGo = GameObject.Find("CheatPanelNGUI");
            if (!rootGo) rootGo = new GameObject("CheatPanelNGUI");
            _cardIconRoot = rootGo.transform;

            // 2) UIPanel（让 NGUI 能画）
            _uiPanel = rootGo.GetComponent<UIPanel>();
            if (_uiPanel == null) _uiPanel = rootGo.AddComponent<UIPanel>();

            // 3) UI 摄像机
            Camera[] cams = rootGo.GetComponentsInChildren<Camera>(true);
            _uiCamera = (cams != null && cams.Length > 0) ? cams[0] : null;
            if (_uiCamera == null)
            {
                var camGo = new GameObject("CheatPanelUICamera");
                camGo.transform.SetParent(_cardIconRoot, false);
                _uiCamera = camGo.AddComponent<Camera>();
            }

            _uiCamera.orthographic = true;
            _uiCamera.orthographicSize = Screen.height * 0.5f; // 1单位=1像素
            _uiCamera.cullingMask = 1 << _uiLayer;
            _uiCamera.clearFlags = CameraClearFlags.Nothing; // 避免黑屏/闪烁
            _uiCamera.backgroundColor = Color.clear;
            _uiCamera.depth = 1000f;
            _uiCamera.nearClipPlane = -1000f;
            _uiCamera.farClipPlane = 1000f;
            _uiCamera.useOcclusionCulling = false;

            // 面板渲染队列固定在后面，避免和别的 NGUI 面板抢顺序
            _uiPanel.renderQueue = UIPanel.RenderQueue.StartAt;
            _uiPanel.startingRenderQueue = 4000;

            // 4) 所有子节点放 UI 层
            SetLayerRecursively(_cardIconRoot.gameObject, _uiLayer);

            _uiReady = true;
        }

        static void SetLayerRecursively(GameObject go, int layer)
        {
            go.layer = layer;
            for (int i = 0; i < go.transform.childCount; i++)
                SetLayerRecursively(go.transform.GetChild(i).gameObject, layer);
        }

        static void EnsureWidgetPool(int needCount)
        {
            // 扩容
            while (_cardWidgets.Count < needCount)
            {
                _cardWidgets.Add(CreateCardWidget());
            }
            // 收缩时不销毁，只隐藏在 Update 阶段做
        }

        static CardWidget CreateCardWidget()
        {
            var go = new GameObject("CardWidget");
            go.transform.SetParent(_cardIconRoot, false);

            // 品质框
            var qualityGO = new GameObject("Quality");
            qualityGO.transform.SetParent(go.transform, false);
            var quality = qualityGO.AddComponent<UISprite>();

            // 物品图标
            var iconGO = new GameObject("Icon");
            iconGO.transform.SetParent(go.transform, false);
            var icon = iconGO.AddComponent<UISprite>();

            // 数量
            var numGO = new GameObject("Num");
            numGO.transform.SetParent(go.transform, false);
            var num = numGO.AddComponent<UILabel>();
            num.overflowMethod = UILabel.Overflow.ShrinkContent;
            num.alignment = NGUIText.Alignment.Center;
            num.fontSize = 18;
            num.color = Color.white;
            AssignDefaultFont(num);                 // ★★★ 绑定一个可用字体

            // 星级
            var starGO = new GameObject("Star");
            starGO.transform.SetParent(go.transform, false);
            var star = starGO.AddComponent<UISprite>();

            // 初始尺寸
            quality.width = quality.height = CELL_SIZE - 2;
            icon.width = icon.height = ICON_SIZE;

            // 去掉所有碰撞体，避免点击闪烁
            RemoveCollidersRecursive(go.transform);

            var w = new CardWidget { go = go, quality = quality, icon = icon, number = num, star = star };
            go.SetActive(false);
            return w;
        }

        static void AssignDefaultFont(UILabel lbl)
        {
            if (lbl == null) return;

            // 先找场景里任意一个已有字体的 UILabel，借用它的字体
            UILabel any = GameObject.FindObjectOfType(typeof(UILabel)) as UILabel;
            if (any != null)
            {
                if (any.trueTypeFont != null)
                {
                    lbl.trueTypeFont = any.trueTypeFont;
                    return;
                }
                if (any.bitmapFont != null)
                {
                    lbl.bitmapFont = any.bitmapFont;
                    return;
                }
                if (any.ambigiousFont != null)   // 注意这里是 ambigiousFont
                {
                    lbl.ambigiousFont = any.ambigiousFont;
                    return;
                }
            }

            // 再兜底到 Resources 里找
            UnityEngine.Object[] all = Resources.FindObjectsOfTypeAll(typeof(UILabel));
            for (int i = 0; i < all.Length; i++)
            {
                var u = all[i] as UILabel;
                if (u == null) continue;

                if (u.trueTypeFont != null) { lbl.trueTypeFont = u.trueTypeFont; return; }
                if (u.bitmapFont != null) { lbl.bitmapFont = u.bitmapFont; return; }
                if (u.ambigiousFont != null) { lbl.ambigiousFont = u.ambigiousFont; return; }
            }

            // 如果你们有固定 UIFont 名称，也可以在这里 Resources.Load 一个默认字体
        }

        static void RemoveCollidersRecursive(Transform t)
        {
            var col = t.GetComponent<Collider>();
            if (col) GameObject.Destroy(col);
            for (int i = 0; i < t.childCount; i++)
                RemoveCollidersRecursive(t.GetChild(i));
        }

        static void UpdateWidgetFromInfo(CardWidget w, CardInfo ci)
        {
            // —— 1) 品质框 ——
            string gradeName;
            try { gradeName = ((ItemGrade)ci.quality).ToString(); }
            catch { gradeName = ci.quality.ToString(); }
            TrySetSprite(w.quality, gradeName);

            // —— 2) iconName 特例 ——
            string iconName = ci.iconName ?? string.Empty;
            if (iconName.Contains("wing"))
            {
                var parts = iconName.Split(',');
                if (parts.Length > 0) iconName = parts[0].Replace("'", string.Empty);
            }

            // —— 3) 深度 ——
            if (w.quality != null) w.quality.depth = 0;
            if (w.icon != null) w.icon.depth = 1;
            if (w.star != null) w.star.depth = 2;
            if (w.number != null) w.number.depth = 3;

            // —— 4) 图标类型 ——
            try
            {
                if (ci.type == 5)
                {
                    TrySetSprite(w.icon, "humancard");
                }
                else if (ci.type == 7)
                {
                    // 金币 / 货币
                    try { UITools.ShowMoney(w.icon, ci.itemId, w.go, true); } catch { }

                    // 关键：ShowMoney 只改了 spriteName，这里把 atlas 切到真正包含这个名字的图集
                    EnsureAtlasForSpriteName(w.icon, w.icon.spriteName);   // ★★★

                    try { w.icon.MakePixelPerfect(); } catch { }

                    // 货币：有数量就显示
                    bool qty = ci.num > 0;
                    if (qty)
                    {
                        if (!w.number.gameObject.activeSelf) w.number.gameObject.SetActive(true);
                        w.number.text = ci.num.ToString();
                    }
                    else if (w.number.gameObject.activeSelf) w.number.gameObject.SetActive(false);
                }
                else
                {
                    try { UITools.SetItemIcon(w.icon, iconName); }
                    catch { TrySetItemIconFallback(w.icon, iconName); }
                }
            }
            catch { }

            try { w.icon.MakePixelPerfect(); } catch { }

            // —— 5) 数量显示 ——
            bool showQty = (ci.type == 7) ? (ci.num > 0)
                : ((ci.unitType == 0 || ci.unitType == 3) && ci.num > 1);

            if (showQty)
            {
                if (!w.number.gameObject.activeSelf) w.number.gameObject.SetActive(true);
                w.number.text = ci.num.ToString();
            }
            else
            {
                if (w.number.gameObject.activeSelf) w.number.gameObject.SetActive(false);
            }

            // —— 6) 星级 ——
            int targetReachCount = 0; // ci.targetReachCount 可替换
            if (targetReachCount <= 0)
            {
                if (w.star != null)
                {
                    w.star.enabled = false;
                    w.star.gameObject.SetActive(false);
                }
            }
            else
            {
                if (w.star != null)
                {
                    w.star.enabled = true;
                    switch (targetReachCount)
                    {
                        case 1: TrySetSprite(w.star, "skin_jiesuan_card_1star"); break;
                        case 2: TrySetSprite(w.star, "skin_jiesuan_card_2star"); break;
                        case 3: TrySetSprite(w.star, "skin_jiesuan_card_3star"); break;
                        default: w.star.enabled = false; break;
                    }
                    try { w.star.MakePixelPerfect(); } catch { }
                    w.star.gameObject.SetActive(w.star.enabled);
                }
            }
        }

        static readonly string[] _atlasTryOrder = {
            "UICommonAtlas", "Item Atlas", "AvatarPartAtlas", "ItemIconAtlas", "IconAtlas"
            };

        static void FixAtlasForCurrentSprite(UISprite sp)
        {
            if (sp == null) return;
            string sn = sp.spriteName;
            if (string.IsNullOrEmpty(sn)) return;

            // atlas 已经含有该 sprite 就不动
            if (sp.atlas != null && sp.atlas.GetSprite(sn) != null) return;

            for (int i = 0; i < _atlasTryOrder.Length; i++)
            {
                var a = AtlasManager.Instance.GetAtlas(_atlasTryOrder[i]);
                if (a != null && a.GetSprite(sn) != null)
                {
                    sp.atlas = a;
                    return;
                }
            }
        }

        static readonly string[] _moneyAtlasTry =
        {
            "UICommonAtlas", "Item Atlas", "AvatarPartAtlas", "ItemIconAtlas", "IconAtlas"
        };

        static void EnsureAtlasForSpriteName(UISprite sp, string spriteName)
        {
            if (sp == null || string.IsNullOrEmpty(spriteName)) return;

            // 当前 atlas 里已有就不动
            if (sp.atlas != null && sp.atlas.GetSprite(spriteName) != null) return;

            // 先按常见顺序尝试
            for (int i = 0; i < _moneyAtlasTry.Length; i++)
            {
                var a = AtlasManager.Instance.GetAtlas(_moneyAtlasTry[i]);
                if (a != null && a.GetSprite(spriteName) != null)
                {
                    sp.atlas = a;
                    return;
                }
            }

            // 兜底：全局搜一遍已加载的 UIAtlas（只在 Layout 时调用，频率低）
            UnityEngine.Object[] all = Resources.FindObjectsOfTypeAll(typeof(UIAtlas));
            for (int i = 0; i < all.Length; i++)
            {
                var a = all[i] as UIAtlas;
                if (a != null && a.GetSprite(spriteName) != null)
                {
                    sp.atlas = a;
                    return;
                }
            }
        }

        static bool TrySetSprite(UISprite sp, string spriteName)
        {
            if (string.IsNullOrEmpty(spriteName) || sp == null) return false;

            if (sp.atlas != null && sp.atlas.GetSprite(spriteName) != null)
            {
                sp.spriteName = spriteName;
                return true;
            }

            var atlasItem = AtlasManager.Instance.GetAtlas("Item Atlas");
            var atlasPart = AtlasManager.Instance.GetAtlas("AvatarPartAtlas");

            if (atlasItem != null && atlasItem.GetSprite(spriteName) != null)
            {
                sp.atlas = atlasItem;
                sp.spriteName = spriteName;
                return true;
            }
            if (atlasPart != null && atlasPart.GetSprite(spriteName) != null)
            {
                sp.atlas = atlasPart;
                sp.spriteName = spriteName;
                return true;
            }
            return false;
        }

        static void TrySetItemIconFallback(UISprite icon, string iconName)
        {
            var atlasItem = AtlasManager.Instance.GetAtlas("Item Atlas");
            var atlasPart = AtlasManager.Instance.GetAtlas("AvatarPartAtlas");
            var curAtlas = icon.atlas;
            int w = icon.width, h = icon.height;
            UIAtlas pick = null;

            if (atlasItem != null && atlasItem.GetSprite(iconName) != null) pick = atlasItem;
            else if (atlasPart != null && atlasPart.GetSprite(iconName) != null) pick = atlasPart;

            if (pick == atlasItem && curAtlas == atlasPart) { w += 10; h += 10; }
            else if (pick == atlasPart && curAtlas == atlasItem) { w -= 10; h -= 10; }

            if (pick != null)
            {
                icon.atlas = pick;
                icon.spriteName = iconName;
                icon.width = w;
                icon.height = h;
            }
        }

        static void LayoutWidgetsInArea(Rect area)
        {
            if (_cardIconRoot == null || _uiCamera == null) return;

            int total = 0;
            for (int i = 0; i < _cardWidgets.Count; i++)
                if (_cardWidgets[i].go.activeSelf) total++;

            int rows = Mathf.Max(1, Mathf.CeilToInt(total / (float)ICONS_PER_ROW));
            float leftX = area.xMin;
            float topY = area.yMin;
            int idx = 0;

            for (int row = 0; row < rows; row++)
            {
                int start = row * ICONS_PER_ROW;
                int nThis = Mathf.Min(ICONS_PER_ROW, total - start);
                float rowW = nThis * CELL_SIZE + (nThis - 1) * CELL_GAP;
                float rowXStart = leftX + ((area.width >= rowW) ? (area.width - rowW) * 0.5f : 0f);
                float rowYTop = topY + row * (CELL_SIZE + CELL_GAP);

                for (int col = 0; col < nThis; col++)
                {
                    while (idx < _cardWidgets.Count && (_cardWidgets[idx] == null || !_cardWidgets[idx].go.activeSelf)) idx++;
                    if (idx >= _cardWidgets.Count) break;

                    var w = _cardWidgets[idx++];
                    float cx = rowXStart + col * (CELL_SIZE + CELL_GAP) + CELL_SIZE * 0.5f;
                    float cy = rowYTop + CELL_SIZE * 0.5f;

                    float screenY = Screen.height - cy;
                    var world = _uiCamera.ScreenToWorldPoint(new Vector3(cx, screenY, 0f));
                    var local = _cardIconRoot.InverseTransformPoint(world);
                    w.go.transform.localPosition = new Vector3(local.x, local.y, 0f);

                    if (w.quality) w.quality.transform.localPosition = Vector3.zero;
                    if (w.icon) w.icon.transform.localPosition = Vector3.zero;
                    if (w.number)
                    {
                        float px = CELL_SIZE * 0.5f - 12f;
                        float py = -CELL_SIZE * 0.5f + 12f;
                        w.number.transform.localPosition = new Vector3(px, py, 0f);
                    }
                    if (w.star)
                    {
                        float px = -CELL_SIZE * 0.5f + 12f;
                        float py = CELL_SIZE * 0.5f - 12f;
                        w.star.transform.localPosition = new Vector3(px, py, 0f);
                    }

                    if (w.quality) w.quality.width = w.quality.height = CELL_SIZE - 2;
                    if (w.icon) w.icon.width = w.icon.height = ICON_SIZE;
                }
            }
        }

        static void SetCardWidgetActive(CardWidget w, bool on)
        {
            if (w == null || w.go == null) return;

            if (w.go.activeSelf != on) w.go.SetActive(on);
            // 或者项目扩展：
            // GameTools.SetActive(w.go.transform, on);
        }

    }
}
