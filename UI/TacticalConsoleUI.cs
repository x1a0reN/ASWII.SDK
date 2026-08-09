using ASWDEBUG.Cheats.AimTrack;
using ASWDEBUG.Cheats.AutoAim;
using ASWDEBUG.Cheats.AutoBattle;
using ASWDEBUG.Cheats.AutoUse;
using ASWDEBUG.Cheats.ESP;
using ASWDEBUG.Cheats.Other;
using ASWDEBUG.Cheats.Player;
using ASWDEBUG.Global;
using ASWDEBUG.Verify;
using System;
using System.Globalization;
using UnityEngine;

namespace ASWDEBUG.UI
{
    internal static class TacticalConsoleUI
    {
        private enum ConsoleTab
        {
            Overview,
            Visual,
            Ballistics,
            Tracking,
            Automation,
            Utility,
            Access
        }

        private const int WindowId = 741902;
        private const float RailWidth = 164f;
        private static readonly string[] NavNames = new string[]
        {
            "总览", "视觉", "弹道", "追踪", "自动化", "实用", "授权"
        };
        private static readonly string[] NavCodes = new string[]
        {
            "01", "02", "03", "04", "05", "06", "07"
        };
        private static readonly string[] PageTitles = new string[]
        {
            "运行总览", "视觉识别", "弹道控制", "概率追踪", "自动执行", "对局实用", "网络授权"
        };
        private static readonly string[] PageDescriptions = new string[]
        {
            "关键状态集中展示；只保留需要立即判断的信息。",
            "人物信息采用独立图层，避免骨骼、方框和卡片相互绑定。",
            "直接缩放游戏原生散布输入，0 为直线，1 为原始手感。",
            "仅接管自然未命中的子弹；自然命中不会进入概率计算。",
            "管理自动使用规则与辅助执行状态。",
            "集中管理翻牌信息、自动防踢与游戏内房间校验。",
            "验证失败即关闭功能；凭据与 SDK 均不写入核心 DLL。"
        };
        private static readonly float[] SpreadPresetValues = new float[]
        {
            0f, 0.25f, 0.5f, 1f, 1.5f
        };
        private static readonly string[] SpreadPresetNames = new string[]
        {
            "直线", "极低", "低", "原生", "高"
        };

        private static readonly Vector2[] Scroll = new Vector2[7];
        private static Rect _window = new Rect(24f, 24f, 760f, 690f);
        private static ConsoleTab _tab = ConsoleTab.Overview;

        private static Texture2D _white;
        private static GUIStyle _windowStyle;
        private static GUIStyle _titleStyle;
        private static GUIStyle _pageTitleStyle;
        private static GUIStyle _bodyStyle;
        private static GUIStyle _smallStyle;
        private static GUIStyle _microStyle;
        private static GUIStyle _wrappedMicroStyle;
        private static GUIStyle _navStyle;
        private static GUIStyle _navActiveStyle;
        private static GUIStyle _buttonStyle;
        private static GUIStyle _buttonQuietStyle;
        private static GUIStyle _invisibleButton;
        private static GUIStyle _sliderStyle;
        private static GUIStyle _thumbStyle;

        private static readonly Color Window = Rgb(7, 11, 15, 0.985f);
        private static readonly Color Header = Rgb(10, 16, 21, 0.995f);
        private static readonly Color Rail = Rgb(9, 14, 19, 0.995f);
        private static readonly Color Surface = Rgb(14, 21, 27, 0.98f);
        private static readonly Color SurfaceRaised = Rgb(18, 27, 34, 0.99f);
        private static readonly Color SurfaceHover = Rgb(22, 34, 41, 1f);
        private static readonly Color Border = Rgb(42, 59, 68, 0.78f);
        private static readonly Color BorderSoft = Rgb(31, 45, 53, 0.72f);
        private static readonly Color Text = Rgb(232, 241, 244, 1f);
        private static readonly Color TextSecondary = Rgb(148, 166, 174, 1f);
        private static readonly Color TextMuted = Rgb(91, 112, 121, 1f);
        private static readonly Color Accent = Rgb(55, 207, 194, 1f);
        private static readonly Color AccentSoft = Rgb(35, 116, 111, 0.42f);
        private static readonly Color Amber = Rgb(244, 184, 96, 1f);
        private static readonly Color Danger = Rgb(255, 105, 105, 1f);

        internal static void Display()
        {
            EnsureStyles();

            float width = Mathf.Clamp(Screen.width - 32f, 580f, 780f);
            float height = Mathf.Clamp(Screen.height - 32f, 540f, 730f);
            _window.width = width;
            _window.height = height;
            _window.x = Mathf.Clamp(_window.x, 8f, Mathf.Max(8f, Screen.width - width - 8f));
            _window.y = Mathf.Clamp(_window.y, 8f, Mathf.Max(8f, Screen.height - height - 8f));

            Color oldColor = GUI.color;
            Color oldContent = GUI.contentColor;
            Color oldBackground = GUI.backgroundColor;
            _window = GUI.Window(
                WindowId,
                _window,
                DrawWindow,
                string.Empty,
                _windowStyle);
            GUI.color = oldColor;
            GUI.contentColor = oldContent;
            GUI.backgroundColor = oldBackground;
        }

        private static void DrawWindow(int id)
        {
            float width = _window.width;
            float height = _window.height;
            DrawRect(new Rect(0f, 0f, width, height), Window);
            DrawBorder(new Rect(0f, 0f, width, height), Border, 1f);
            DrawHeader(width);
            DrawRail(height);
            DrawContent(width, height);

            if (GUI.Button(
                new Rect(width - 42f, 13f, 28f, 28f),
                "×",
                _buttonQuietStyle))
            {
                CheatUIManager.MenuVisible = false;
            }

            GUI.DragWindow(new Rect(0f, 0f, width - 52f, 54f));
        }

        private static void DrawHeader(float width)
        {
            DrawRect(new Rect(1f, 1f, width - 2f, 54f), Header);
            DrawRect(new Rect(0f, 54f, width, 1f), Border);
            DrawRect(new Rect(18f, 15f, 3f, 24f), Accent);
            Label(new Rect(30f, 8f, 280f, 25f), "VECTOR / FIELD CONTROL", _titleStyle, Text);
            Label(new Rect(30f, 31f, 310f, 16f), "PRECISION SUITE  ·  DOORSTOP RUNTIME", _microStyle, TextMuted);

            bool online = VeriGateAuthManager.Instance != null &&
                          VeriGateAuthManager.Instance.LoggedIn;
            Color state = online ? Accent : Amber;
            DrawRect(new Rect(width - 222f, 17f, 7f, 7f), state);
            Label(
                new Rect(width - 207f, 9f, 148f, 24f),
                online ? "AUTH / ONLINE" : "AUTH / PENDING",
                _smallStyle,
                state);
            Label(
                new Rect(width - 207f, 29f, 148f, 15f),
                online ? "heartbeat active" : "fail-closed gate",
                _microStyle,
                TextMuted);
        }

        private static void DrawRail(float height)
        {
            DrawRect(new Rect(1f, 55f, RailWidth, height - 56f), Rail);
            DrawRect(new Rect(RailWidth, 55f, 1f, height - 56f), BorderSoft);
            Label(new Rect(18f, 72f, 120f, 18f), "CONTROL DOMAINS", _microStyle, TextMuted);

            float y = 101f;
            for (int i = 0; i < NavNames.Length; i++)
            {
                Rect row = new Rect(8f, y, RailWidth - 16f, 42f);
                bool selected = (int)_tab == i;
                if (selected)
                {
                    DrawRect(row, SurfaceRaised);
                    DrawRect(new Rect(row.x, row.y, 3f, row.height), Accent);
                }
                if (GUI.Button(
                    row,
                    NavCodes[i] + "    " + NavNames[i],
                    selected ? _navActiveStyle : _navStyle))
                {
                    _tab = (ConsoleTab)i;
                }
                y += 47f;
            }

            DrawRect(new Rect(14f, height - 73f, RailWidth - 28f, 1f), BorderSoft);
            Label(new Rect(18f, height - 59f, 128f, 16f), "DELETE  隐藏界面", _microStyle, TextMuted);
            Label(new Rect(18f, height - 39f, 128f, 16f), "PROFILE / AUTO SAVE", _microStyle, Accent);
        }

        private static void DrawContent(float width, float height)
        {
            Rect area = new Rect(
                RailWidth + 23f,
                72f,
                width - RailWidth - 42f,
                height - 88f);
            GUI.BeginGroup(area);

            int index = (int)_tab;
            Label(new Rect(0f, 0f, area.width, 27f), PageTitles[index], _pageTitleStyle, Text);
            Label(new Rect(0f, 29f, area.width, 22f), PageDescriptions[index], _smallStyle, TextSecondary);
            DrawRect(new Rect(0f, 56f, 42f, 2f), Accent);
            DrawRect(new Rect(48f, 56f, area.width - 48f, 1f), BorderSoft);

            Rect viewport = new Rect(0f, 69f, area.width, area.height - 69f);
            Rect content = new Rect(0f, 0f, area.width - 18f, ContentHeight(_tab));
            Scroll[index] = GUI.BeginScrollView(
                viewport,
                Scroll[index],
                content,
                false,
                true);

            float y = 4f;
            switch (_tab)
            {
                case ConsoleTab.Overview: DrawOverview(ref y, content.width); break;
                case ConsoleTab.Visual: DrawVisual(ref y, content.width); break;
                case ConsoleTab.Ballistics: DrawBallistics(ref y, content.width); break;
                case ConsoleTab.Tracking: DrawTracking(ref y, content.width); break;
                case ConsoleTab.Automation: DrawAutomation(ref y, content.width); break;
                case ConsoleTab.Utility: DrawUtility(ref y, content.width); break;
                case ConsoleTab.Access: DrawAccess(ref y, content.width); break;
            }

            GUI.EndScrollView();
            GUI.EndGroup();
        }

        private static void DrawOverview(ref float y, float width)
        {
            float gap = 10f;
            float cardWidth = (width - gap) * 0.5f;
            MetricCard(
                new Rect(0f, y, cardWidth, 76f),
                "TRACKING",
                AimTrack.Enabled ? AimTrack.LastDecision : "OFFLINE",
                AimTrack.Enabled ? Accent : TextMuted);
            MetricCard(
                new Rect(cardWidth + gap, y, cardWidth, 76f),
                "SPREAD SCALE",
                BulletNoRecoil.Enabled
                    ? BulletNoRecoil.SpreadScale.ToString("0.00", CultureInfo.InvariantCulture) + "x"
                    : "NATIVE",
                BulletNoRecoil.Enabled ? Amber : TextSecondary);
            y += 86f;
            MetricCard(
                new Rect(0f, y, cardWidth, 76f),
                "VISUAL LAYERS",
                CountVisualLayers().ToString(CultureInfo.InvariantCulture) + " ACTIVE",
                ESP.Enabled ? Accent : TextMuted);
            MetricCard(
                new Rect(cardWidth + gap, y, cardWidth, 76f),
                "AUTO USE",
                AutoUseManager.Enabled ? "RUNNING" : "STANDBY",
                AutoUseManager.Enabled ? Accent : TextMuted);
            y += 98f;

            SectionLabel(ref y, width, "QUICK CONTROL", "常用功能");
            ApplyToggle(ref y, width, "ESP 主开关", "控制人物视觉图层，不影响追踪范围圆。", ESP.Enabled,
                delegate(bool value) { ESP.Enabled = value; });
            ApplyToggle(ref y, width, "概率追踪", "仅在原始子弹未命中时尝试重定向。", AimTrack.Enabled,
                delegate(bool value) { AimTrack.Enabled = value; });
            ApplyToggle(ref y, width, "散布控制", "启用后按下方弹道页中的倍率调整。", BulletNoRecoil.Enabled,
                delegate(bool value) { BulletNoRecoil.Enabled = value; });
            ApplyToggle(ref y, width, "自动使用", "执行已保存的药品、技能与动作规则。", AutoUseManager.Enabled,
                delegate(bool value) { AutoUseManager.Enabled = value; });
        }

        private static void DrawVisual(ref float y, float width)
        {
            SectionLabel(ref y, width, "ENTITY READOUT", "人物识别图层");
            ApplyToggle(ref y, width, "ESP 主开关", "关闭后不再遍历人物绘制；追踪范围圆仍可独立显示。", ESP.Enabled,
                delegate(bool value) { ESP.Enabled = value; });
            ApplyToggle(ref y, width, "人物骨骼", "细线主关节链与独立头部环，远距离自动减重。", ESP.SkeletonEsp,
                delegate(bool value) { ESP.SkeletonEsp = value; });
            ApplyToggle(ref y, width, "人物方框", "世界竖直角框，避免动画和斜坡造成盒体歪斜。", ESP.D3BoxEsp,
                delegate(bool value) { ESP.D3BoxEsp = value; });
            ApplyToggle(ref y, width, "信息卡片", "显示姓名、距离、生命、护盾与状态，使用不透明高对比底板。", ESP.InfoEsp,
                delegate(bool value) { ESP.InfoEsp = value; });

            SectionLabel(ref y, width, "AIM OVERLAY", "瞄准辅助图层");
            ApplyToggle(ref y, width, "中心准星", "在游戏准星上叠加轻量中心标记。", ESP.CrossEsp,
                delegate(bool value) { ESP.CrossEsp = value; });
            ApplyToggle(ref y, width, "目标连线", "从屏幕顶部连接至人物头部锚点。", ESP.LineEsp,
                delegate(bool value) { ESP.LineEsp = value; });
            ApplyToggle(ref y, width, "独立 ESP 范围圆", "追踪关闭时仍可显示 ESP 自身的圆形参考。", ESP.CircleEsp,
                delegate(bool value) { ESP.CircleEsp = value; });
        }

        private static void DrawBallistics(ref float y, float width)
        {
            SectionLabel(ref y, width, "NATIVE SPREAD PIPELINE", "原生散布输入");
            ApplyToggle(ref y, width, "启用散布控制", "只修改本地玩家 Character.GetSpread(float) 的输入，不影响敌人。", BulletNoRecoil.Enabled,
                delegate(bool value) { BulletNoRecoil.Enabled = value; });

            float scale = SliderRow(
                ref y,
                width,
                "扩散倍率",
                "0.00 为直线；1.00 为武器原始扩散；大于 1 会放大扩散。",
                BulletNoRecoil.SpreadScale,
                0f,
                3f,
                BulletNoRecoil.SpreadScale.ToString("0.00", CultureInfo.InvariantCulture) + "x");
            if (Mathf.Abs(scale - BulletNoRecoil.SpreadScale) > 0.0001f)
            {
                BulletNoRecoil.SpreadScale = scale;
                FeatureConfigStore.MarkDirty();
            }

            Label(new Rect(0f, y, width, 18f), "PRESETS", _microStyle, TextMuted);
            y += 24f;
            float buttonWidth = (width - 32f) / 5f;
            for (int i = 0; i < SpreadPresetValues.Length; i++)
            {
                Rect button = new Rect(i * (buttonWidth + 8f), y, buttonWidth, 34f);
                if (GUI.Button(button, SpreadPresetNames[i], _buttonQuietStyle))
                {
                    BulletNoRecoil.Enabled = true;
                    BulletNoRecoil.SpreadScale = SpreadPresetValues[i];
                    FeatureConfigStore.MarkDirty();
                }
            }
            y += 48f;

            InfoPanel(
                ref y,
                width,
                "COHERENT PATH",
                "散布倍率在游戏生成射线和 HitMessage.spread 之前生效，因此画面射线、命中包与散布证据保持同一来源。",
                Accent);
        }

        private static void DrawTracking(ref float y, float width)
        {
            SectionLabel(ref y, width, "MISS-ONLY REDIRECTION", "未命中追踪");
            ApplyToggle(ref y, width, "启用概率追踪", "自然命中直接放行；只有 uid=0 的自然未命中才进入追踪逻辑。", AimTrack.Enabled,
                delegate(bool value) { AimTrack.Enabled = value; });

            float radius = SliderRow(
                ref y,
                width,
                "追踪圆半径",
                "使用屏幕像素；目标的可射击身体点必须落在圆内。",
                AimTrack.RadiusPixels,
                24f,
                800f,
                Mathf.RoundToInt(AimTrack.RadiusPixels) + " px");
            if (Mathf.Abs(radius - AimTrack.RadiusPixels) > 0.01f)
            {
                AimTrack.RadiusPixels = radius;
                ESP.CircleRadius = radius;
                FeatureConfigStore.MarkDirty();
            }

            float probability = SliderRow(
                ref y,
                width,
                "追踪概率",
                "例如 50% 表示自然打空后有一半概率追踪；自然命中不掷骰。",
                AimTrack.TrackingProbability,
                0f,
                1f,
                Mathf.RoundToInt(AimTrack.TrackingProbability * 100f) + "%");
            if (Mathf.Abs(probability - AimTrack.TrackingProbability) > 0.0001f)
            {
                AimTrack.TrackingProbability = probability;
                FeatureConfigStore.MarkDirty();
            }

            ApplyToggle(ref y, width, "显示范围圆", "低亮青色圆环；锁定有效目标时切换为琥珀提示。", AimTrack.DrawFovCircle,
                delegate(bool value) { AimTrack.DrawFovCircle = value; });
            ApplyToggle(ref y, width, "墙体检测", "从真实射击起点扫描头、胸、髋和四肢；任一采样点暴露即可。", AimTrack.Wall,
                delegate(bool value) { AimTrack.Wall = value; });
            ApplyToggle(ref y, width, "隐身检测", "开启后排除处于隐藏状态的人物。", AimTrack.Hidden,
                delegate(bool value) { AimTrack.Hidden = value; });
            ApplyToggle(ref y, width, "盾牌检测", "开启后拒绝首个碰撞点为盾牌层的采样点，并继续寻找露出的身体。", AimTrack.Shield,
                delegate(bool value) { AimTrack.Shield = value; });

            SectionLabel(ref y, width, "LIVE RESOLUTION", "实时判定");
            string target = SafeTargetName(AimTrack.currentTarget);
            string roll = AimTrack.LastProbabilityRoll < 0f
                ? "--"
                : (AimTrack.LastProbabilityRoll * 100f).ToString("0.0", CultureInfo.InvariantCulture) + "%";
            InfoPanel(
                ref y,
                width,
                "TARGET  " + target,
                "state=" + AimTrack.LastDecision +
                "   part=" + AimTrack.CurrentHitPart +
                "   last_roll=" + roll,
                AimTrack.currentTarget == null ? TextMuted : Accent);

            SectionLabel(ref y, width, "CAMERA AIM", "镜头自瞄");
            ApplyToggle(ref y, width, "镜头自瞄", "按住已绑定按键时移动镜头；与概率追踪互相独立。", AutoAim.Enabled,
                delegate(bool value) { AutoAim.Enabled = value; });
            ApplyToggle(ref y, width, "镜头墙体检测", "镜头选取时排除遮挡目标。", AutoAim.Wall,
                delegate(bool value) { AutoAim.Wall = value; });
        }

        private static void DrawAutomation(ref float y, float width)
        {
            SectionLabel(ref y, width, "RULE ENGINE", "自动使用规则");
            ApplyToggle(ref y, width, "启用自动使用", "每 100ms 评估已保存规则；具体规则由独立编辑器管理。", AutoUseManager.Enabled,
                delegate(bool value) { AutoUseManager.Enabled = value; });

            InfoPanel(
                ref y,
                width,
                "RULESET  " + AutoUseManager.Rules.Count + " 条",
                string.IsNullOrEmpty(AutoUseManager.LastStatus)
                    ? "配置尚未加载"
                    : AutoUseManager.LastStatus,
                AutoUseManager.Enabled ? Accent : TextSecondary);

            if (ActionButton(ref y, width, "打开规则编辑器", true))
                AutoUseConfigPanel.Visible = !AutoUseConfigPanel.Visible;
            if (ActionButton(ref y, width, "保存规则配置", false))
                AutoUseManager.Save();
            if (ActionButton(ref y, width, "重新载入规则", false))
                AutoUseManager.Load();

            SectionLabel(ref y, width, "FIRE SUPPORT", "辅助执行");
            ApplyToggle(ref y, width, "自动扳机", "保留主分支自动扳机逻辑。", AutoFire.Enabled,
                delegate(bool value) { AutoFire.Enabled = value; });
            ApplyToggle(ref y, width, "允许自动攻击", "作为自动扳机的独立执行许可。", AutoFire.AutoFireAllowed,
                delegate(bool value) { AutoFire.AutoFireAllowed = value; });
            ApplyToggle(ref y, width, "AI 接管", "启用现有自动战斗管理器；手动输入仍优先。", Settings.AutoBattleEnabled,
                delegate(bool value)
                {
                    if (Settings.AutoBattleEnabled != value) AutoBattleManager.ToggleEnabled();
                });
        }

        private static void DrawUtility(ref float y, float width)
        {
            SectionLabel(ref y, width, "MATCH INTELLIGENCE", "对局信息");
            ApplyToggle(
                ref y,
                width,
                "翻牌透视",
                "解析结算奖励，并在右侧独立浮层中提前显示物品图标与数量。",
                OtherC.Enabled,
                delegate(bool value) { OtherC.Enabled = value; });

            int cardCount = global::ASWDEBUG.Main.CheatMain.CardData == null
                ? 0
                : global::ASWDEBUG.Main.CheatMain.CardData.Count;
            InfoPanel(
                ref y,
                width,
                "CARD REVEAL  " + cardCount + " ITEMS",
                OtherC.Enabled
                    ? (cardCount > 0 ? "奖励数据已捕获。" : "等待结算奖励数据。")
                    : "功能未启用。",
                OtherC.Enabled ? Accent : TextMuted);

            SectionLabel(ref y, width, "SESSION CONTROL", "对局控制");
            ApplyToggle(
                ref y,
                width,
                "自动防踢",
                "复用主分支轮询与目标冷却逻辑，避免在每帧重复发起请求。",
                AutoKick.Enabled,
                delegate(bool value) { AutoKick.Enabled = value; });
            ApplyToggle(
                ref y,
                width,
                "无视对局验证",
                "仅跳过游戏 NewUIRoom.openMatchCheck；不影响 VeriGate 授权与心跳。",
                OtherC.EnabledVeryify,
                delegate(bool value)
                {
                    if (OtherC.EnabledVeryify != value) OtherC.ToggleEnabledVeryify();
                });

            InfoPanel(
                ref y,
                width,
                "AUTH BOUNDARY",
                "此开关只处理游戏房间挑战。核心 DLL 仍必须通过网络授权，授权失效时功能补丁不会安装。",
                Amber);
        }

        private static void DrawAccess(ref float y, float width)
        {
            VeriGateAuthManager auth = VeriGateAuthManager.Instance;
            bool online = auth != null && auth.LoggedIn;
            SectionLabel(ref y, width, "FAIL-CLOSED ACCESS", "网络授权状态");
            InfoPanel(
                ref y,
                width,
                online ? "AUTHORIZED" : "NOT AUTHORIZED",
                online
                    ? "授权通过；功能补丁与心跳已启用。"
                    : "验证未完成或已失效；功能补丁不会安装。",
                online ? Accent : Danger);

            MetricCard(
                new Rect(0f, y, width, 76f),
                "ENTITLEMENT EXPIRY",
                auth == null || string.IsNullOrEmpty(auth.StaticExpiredText)
                    ? "PENDING"
                    : auth.StaticExpiredText,
                online ? Accent : TextMuted);
            y += 88f;

            InfoPanel(
                ref y,
                width,
                "SDK / HASH-PINNED",
                "从 Launcher/Native/<sha256>/verigate_sdk.dll 加载并核对 SHA-256；核心 DLL 不释放或携带 SDK。",
                Accent);
            InfoPanel(
                ref y,
                width,
                "CREDENTIAL / DPAPI",
                "直登凭据由当前 Windows 用户的 DPAPI 封存；后台撤销后，心跳会停止当前运行时。",
                Amber);

            if (auth != null && !string.IsNullOrEmpty(auth.LastError))
            {
                InfoPanel(ref y, width, "LAST ERROR", auth.LastError, Danger);
            }

            if (ActionButton(ref y, width, "立即保存本地功能配置", false))
                FeatureConfigStore.SaveNow();
            Label(new Rect(0f, y, width, 36f), FeatureConfigStore.ConfigPath, _microStyle, TextMuted);
            y += 42f;
        }

        private static void ApplyToggle(
            ref float y,
            float width,
            string title,
            string description,
            bool value,
            Action<bool> apply)
        {
            bool next = ToggleRow(ref y, width, title, description, value);
            if (next == value) return;
            apply(next);
            FeatureConfigStore.MarkDirty();
        }

        private static bool ToggleRow(
            ref float y,
            float width,
            string title,
            string description,
            bool value)
        {
            Rect row = new Rect(0f, y, width, 54f);
            bool hovered = row.Contains(Event.current.mousePosition);
            DrawRect(row, hovered ? SurfaceHover : Surface);
            DrawBorder(row, hovered ? Border : BorderSoft, 1f);
            if (GUI.Button(row, string.Empty, _invisibleButton)) value = !value;

            Label(new Rect(14f, y + 7f, width - 92f, 20f), title, _bodyStyle, Text);
            Label(new Rect(14f, y + 29f, width - 92f, 17f), description, _microStyle, TextSecondary);

            Rect track = new Rect(width - 64f, y + 16f, 44f, 22f);
            DrawRect(track, value ? AccentSoft : Rgb(37, 49, 55, 1f));
            DrawBorder(track, value ? Accent : Border, 1f);
            Rect knob = new Rect(value ? track.x + 25f : track.x + 4f, track.y + 4f, 14f, 14f);
            DrawRect(knob, value ? Accent : TextMuted);
            y += 62f;
            return value;
        }

        private static float SliderRow(
            ref float y,
            float width,
            string title,
            string description,
            float value,
            float minimum,
            float maximum,
            string displayValue)
        {
            Rect row = new Rect(0f, y, width, 78f);
            DrawRect(row, Surface);
            DrawBorder(row, BorderSoft, 1f);
            Label(new Rect(14f, y + 7f, width - 100f, 20f), title, _bodyStyle, Text);
            Label(new Rect(width - 92f, y + 7f, 72f, 20f), displayValue, _smallStyle, Accent);
            Label(new Rect(14f, y + 29f, width - 28f, 17f), description, _microStyle, TextSecondary);
            value = GUI.HorizontalSlider(
                new Rect(14f, y + 56f, width - 28f, 14f),
                value,
                minimum,
                maximum,
                _sliderStyle,
                _thumbStyle);
            y += 86f;
            return value;
        }

        private static void SectionLabel(
            ref float y,
            float width,
            string code,
            string title)
        {
            Label(new Rect(0f, y, width * 0.55f, 18f), code, _microStyle, Accent);
            Label(new Rect(width * 0.55f, y, width * 0.45f, 18f), title, _smallStyle, TextSecondary);
            y += 25f;
        }

        private static void MetricCard(
            Rect rect,
            string title,
            string value,
            Color accent)
        {
            DrawRect(rect, Surface);
            DrawBorder(rect, BorderSoft, 1f);
            DrawRect(new Rect(rect.x, rect.y, 3f, rect.height), accent);
            Label(new Rect(rect.x + 15f, rect.y + 11f, rect.width - 26f, 16f), title, _microStyle, TextMuted);
            Label(new Rect(rect.x + 15f, rect.y + 34f, rect.width - 26f, 25f), value, _bodyStyle, accent);
        }

        private static void InfoPanel(
            ref float y,
            float width,
            string title,
            string description,
            Color accent)
        {
            Rect rect = new Rect(0f, y, width, 72f);
            DrawRect(rect, Surface);
            DrawBorder(rect, BorderSoft, 1f);
            DrawRect(new Rect(0f, y, 3f, 72f), accent);
            Label(new Rect(14f, y + 9f, width - 28f, 19f), title, _smallStyle, accent);
            Label(
                new Rect(14f, y + 31f, width - 28f, 33f),
                description,
                _wrappedMicroStyle,
                TextSecondary);
            y += 80f;
        }

        private static bool ActionButton(ref float y, float width, string text, bool primary)
        {
            bool clicked = GUI.Button(
                new Rect(0f, y, width, 36f),
                text,
                primary ? _buttonStyle : _buttonQuietStyle);
            y += 44f;
            return clicked;
        }

        private static int CountVisualLayers()
        {
            int count = 0;
            if (ESP.SkeletonEsp) count++;
            if (ESP.D3BoxEsp) count++;
            if (ESP.InfoEsp) count++;
            if (ESP.LineEsp) count++;
            if (ESP.CrossEsp) count++;
            return count;
        }

        private static string SafeTargetName(Character target)
        {
            if (target == null) return "NONE";
            try
            {
                string name = target.GetName();
                return string.IsNullOrEmpty(name) ? "UID " + target.uid : name;
            }
            catch
            {
                return "UID " + target.uid;
            }
        }

        private static float ContentHeight(ConsoleTab tab)
        {
            switch (tab)
            {
                case ConsoleTab.Overview: return 620f;
                case ConsoleTab.Visual: return 690f;
                case ConsoleTab.Ballistics: return 520f;
                case ConsoleTab.Tracking: return 1030f;
                case ConsoleTab.Automation: return 790f;
                case ConsoleTab.Utility: return 700f;
                case ConsoleTab.Access: return 690f;
                default: return 620f;
            }
        }

        private static void EnsureStyles()
        {
            if (_windowStyle != null) return;
            _white = new Texture2D(1, 1, TextureFormat.ARGB32, false);
            _white.hideFlags = HideFlags.HideAndDontSave;
            _white.SetPixel(0, 0, Color.white);
            _white.Apply();

            _windowStyle = new GUIStyle(GUI.skin.window);
            _windowStyle.normal.background = _white;
            _windowStyle.padding = new RectOffset(0, 0, 0, 0);
            _windowStyle.border = new RectOffset(0, 0, 0, 0);

            _titleStyle = LabelStyle(17, FontStyle.Bold, TextAnchor.MiddleLeft);
            _pageTitleStyle = LabelStyle(20, FontStyle.Bold, TextAnchor.MiddleLeft);
            _bodyStyle = LabelStyle(14, FontStyle.Normal, TextAnchor.MiddleLeft);
            _smallStyle = LabelStyle(12, FontStyle.Normal, TextAnchor.MiddleLeft);
            _microStyle = LabelStyle(11, FontStyle.Normal, TextAnchor.MiddleLeft);
            _wrappedMicroStyle = new GUIStyle(_microStyle);
            _wrappedMicroStyle.wordWrap = true;

            _navStyle = new GUIStyle(GUI.skin.button);
            ConfigureButton(_navStyle, Color.clear, Color.clear, TextSecondary, 13, TextAnchor.MiddleLeft);
            _navStyle.padding = new RectOffset(16, 8, 0, 0);
            _navActiveStyle = new GUIStyle(_navStyle);
            _navActiveStyle.normal.textColor = Text;
            _navActiveStyle.hover.textColor = Text;

            _buttonStyle = new GUIStyle(GUI.skin.button);
            ConfigureButton(_buttonStyle, AccentSoft, Rgb(40, 139, 131, 0.62f), Text, 13, TextAnchor.MiddleCenter);
            _buttonStyle.border = new RectOffset(0, 0, 0, 0);
            _buttonQuietStyle = new GUIStyle(GUI.skin.button);
            ConfigureButton(_buttonQuietStyle, SurfaceRaised, SurfaceHover, TextSecondary, 13, TextAnchor.MiddleCenter);
            _buttonQuietStyle.border = new RectOffset(0, 0, 0, 0);

            _invisibleButton = new GUIStyle(GUI.skin.button);
            ConfigureButton(_invisibleButton, Color.clear, Color.clear, Color.clear, 1, TextAnchor.MiddleCenter);

            _sliderStyle = new GUIStyle(GUI.skin.horizontalSlider);
            _sliderStyle.normal.background = SolidTexture(Rgb(36, 49, 56, 1f));
            _sliderStyle.fixedHeight = 3f;
            _sliderStyle.margin = new RectOffset(0, 0, 5, 5);
            _thumbStyle = new GUIStyle(GUI.skin.horizontalSliderThumb);
            _thumbStyle.normal.background = SolidTexture(Accent);
            _thumbStyle.hover.background = SolidTexture(Rgb(92, 232, 218, 1f));
            _thumbStyle.active.background = SolidTexture(Amber);
            _thumbStyle.fixedWidth = 12f;
            _thumbStyle.fixedHeight = 18f;
        }

        private static GUIStyle LabelStyle(int size, FontStyle fontStyle, TextAnchor alignment)
        {
            GUIStyle style = new GUIStyle(GUI.skin.label);
            style.fontSize = size;
            style.fontStyle = fontStyle;
            style.alignment = alignment;
            style.clipping = TextClipping.Clip;
            style.wordWrap = false;
            style.richText = false;
            return style;
        }

        private static void ConfigureButton(
            GUIStyle style,
            Color normal,
            Color hover,
            Color text,
            int size,
            TextAnchor alignment)
        {
            style.normal.background = SolidTexture(normal);
            style.hover.background = SolidTexture(hover);
            style.active.background = SolidTexture(hover);
            style.focused.background = SolidTexture(normal);
            style.normal.textColor = text;
            style.hover.textColor = Text;
            style.active.textColor = Text;
            style.focused.textColor = text;
            style.fontSize = size;
            style.alignment = alignment;
            style.fontStyle = FontStyle.Normal;
        }

        private static Texture2D SolidTexture(Color color)
        {
            Texture2D texture = new Texture2D(1, 1, TextureFormat.ARGB32, false);
            texture.hideFlags = HideFlags.HideAndDontSave;
            texture.SetPixel(0, 0, color);
            texture.Apply();
            return texture;
        }

        private static void DrawRect(Rect rect, Color color)
        {
            Color old = GUI.color;
            GUI.color = color;
            GUI.DrawTexture(rect, _white);
            GUI.color = old;
        }

        private static void DrawBorder(Rect rect, Color color, float width)
        {
            DrawRect(new Rect(rect.x, rect.y, rect.width, width), color);
            DrawRect(new Rect(rect.x, rect.yMax - width, rect.width, width), color);
            DrawRect(new Rect(rect.x, rect.y, width, rect.height), color);
            DrawRect(new Rect(rect.xMax - width, rect.y, width, rect.height), color);
        }

        private static void Label(Rect rect, string text, GUIStyle style, Color color)
        {
            Color old = GUI.contentColor;
            GUI.contentColor = color;
            GUI.Label(rect, text ?? string.Empty, style);
            GUI.contentColor = old;
        }

        private static Color Rgb(int r, int g, int b, float alpha)
        {
            return new Color(r / 255f, g / 255f, b / 255f, alpha);
        }
    }
}
