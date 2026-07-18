using ASWDEBUG.Cheats.LocalBot;
using ASWDEBUG.Main;
using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

#if false
namespace ASWDEBUG.UI
{
    internal static class LocalBotPanel
    {
        private const int WindowId = 731942;
        private static Rect _window = new Rect(20f, 220f, 510f, 700f);
        private static Vector2 _scroll;
        private static LocalBotSpawnOptions _options = new LocalBotSpawnOptions();
        private static int _selectedSequence = -1;

        private static string _namePrefix = "PathBot";
        private static string _count = "1";
        private static string _spreadRadius = "0";
        private static string _x = "0";
        private static string _y = "0";
        private static string _z = "0";
        private static string _frontDistance = "8";
        private static string _health = "5000";
        private static string _shield = "0";
        private static string _invincible = "0";
        private static string _localDamage = "250";
        private static string _headshotMultiplier = "1.5";
        private static string _runSpeed = "6";
        private static string _jumpHeight = "1.2";
        private static string _eyesDistance = "100";
        private static string _followDistance = "10";
        private static string _attackSpread = "2";
        private static string _weaponUseTime = "10";

        internal static bool Visible;

        internal static void Display()
        {
            if (!Visible || !CheatUIManager.MenuVisible) return;

            float maxWidth = Mathf.Max(360f, Screen.width - 20f);
            float maxHeight = Mathf.Max(360f, Screen.height - 20f);
            _window.width = Mathf.Min(510f, maxWidth);
            _window.height = Mathf.Min(700f, maxHeight);
            _window.x = Mathf.Clamp(_window.x, 0f, Mathf.Max(0f, Screen.width - _window.width));
            _window.y = Mathf.Clamp(_window.y, 0f, Mathf.Max(0f, Screen.height - _window.height));
            _window = GUILayout.Window(WindowId, _window, DrawWindow, "本地 Bot 寻路测试", GUILayout.Width(_window.width), GUILayout.Height(_window.height));
        }

        private static void DrawWindow(int id)
        {
            _scroll = GUILayout.BeginScrollView(_scroll, false, true);

            GUILayout.Label("纯客户端对象：不向服务端注册，退出战斗时自动清理。", SmallLabel());
            GUILayout.Label("固定靶用于稳定测试；原生移动使用游戏 RobotControl，但网络攻击被屏蔽。", SmallLabel());

            DrawSection("生成模式");
            GUILayout.BeginHorizontal();
            GUILayout.Label("类型", GUILayout.Width(80f));
            _options.Mode = (LocalBotMode)GUILayout.SelectionGrid(
                (int)_options.Mode,
                new string[] { "固定靶", "原生移动" },
                2,
                GUILayout.Height(24f));
            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            GUILayout.Label("队伍", GUILayout.Width(80f));
            _options.TeamMode = GUILayout.SelectionGrid(
                Mathf.Clamp(_options.TeamMode, 0, 2),
                new string[] { "自动敌方", "队伍0", "队伍1" },
                3,
                GUILayout.Height(24f));
            GUILayout.EndHorizontal();

            _namePrefix = TextRow("名称前缀", _namePrefix);
            _count = TextRow("生成数量", _count);
            _spreadRadius = TextRow("散布半径", _spreadRadius);
            _options.FacePlayer = GUILayout.Toggle(_options.FacePlayer, "出生后朝向玩家");
            _options.SnapToGround = GUILayout.Toggle(_options.SnapToGround, "将生成位置投影到地面");
            _options.Targetable = GUILayout.Toggle(_options.Targetable, "允许作为可选目标");

            DrawSection("位置");
            GUILayout.BeginHorizontal();
            GUILayout.Label("X", GUILayout.Width(18f));
            _x = GUILayout.TextField(_x ?? string.Empty, GUILayout.Width(100f));
            GUILayout.Label("Y", GUILayout.Width(18f));
            _y = GUILayout.TextField(_y ?? string.Empty, GUILayout.Width(100f));
            GUILayout.Label("Z", GUILayout.Width(18f));
            _z = GUILayout.TextField(_z ?? string.Empty, GUILayout.Width(100f));
            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            if (GUILayout.Button("读取玩家坐标", GUILayout.Height(24f))) ReadPlayerPosition();
            if (GUILayout.Button("读取准星落点", GUILayout.Height(24f))) ReadCrosshairPosition();
            GUILayout.EndHorizontal();

            _frontDistance = TextRow("前方距离", _frontDistance);
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("指定坐标生成", PrimaryButton(), GUILayout.Height(28f))) SpawnAtConfiguredPosition();
            if (GUILayout.Button("准星落点生成", PrimaryButton(), GUILayout.Height(28f))) SpawnAtCrosshair();
            if (GUILayout.Button("玩家前方生成", PrimaryButton(), GUILayout.Height(28f))) SpawnInFrontOfPlayer();
            GUILayout.EndHorizontal();

            DrawSection("属性与原生移动 AI");
            _health = TextRow("生命", _health);
            _shield = TextRow("护盾", _shield);
            _invincible = TextRow("无敌秒数", _invincible);
            _localDamage = TextRow("本地单次伤害", _localDamage);
            _headshotMultiplier = TextRow("爆头伤害倍率", _headshotMultiplier);
            GUILayout.Label("本地 Bot 不存在服务端伤害实体；这里用可配置伤害驱动游戏原生 HealthChange/Die、伤害数字和死亡表现。", SmallLabel());
            _runSpeed = TextRow("移动速度", _runSpeed);
            _jumpHeight = TextRow("跳跃高度", _jumpHeight);
            _eyesDistance = TextRow("视野距离", _eyesDistance);
            _followDistance = TextRow("跟随距离", _followDistance);
            _attackSpread = TextRow("武器散布", _attackSpread);
            _weaponUseTime = TextRow("持武器时间", _weaponUseTime);
            GUILayout.Label("原生移动 Bot 会复制当前角色外观和当前武器，仅让游戏 AI 移动/寻路，不允许向网络发送攻击。", SmallLabel());

            DrawSection("已生成 Bot（" + LocalBotManager.Count + "/16）");
            List<LocalBotRecord> bots = LocalBotManager.GetSnapshot();
            if (bots.Count == 0)
            {
                GUILayout.Label("暂无本地 Bot", SmallLabel());
                _selectedSequence = -1;
            }
            else
            {
                for (int i = 0; i < bots.Count; i++)
                {
                    LocalBotRecord bot = bots[i];
                    Character character = bot.Character;
                    string position = character == null
                        ? "destroyed"
                        : FormatVector(character.transform.position);
                    string life = character == null
                        ? string.Empty
                        : (character.IsDied ? " DEAD" : " HP=" + character.hp + "/" + character.max_health + " S=" + character.shield);
                    string label = (bot.Sequence == _selectedSequence ? "● " : "○ ") +
                        bot.DisplayName + "  uid=" + bot.CharacterUid + "  " + bot.Mode + life + "  " + position;
                    if (GUILayout.Button(label, RowButton(), GUILayout.Height(23f)))
                        _selectedSequence = bot.Sequence;
                }
            }

            LocalBotRecord selected = FindSelected(bots);
            GUILayout.BeginHorizontal();
            GUI.enabled = selected != null;
            if (GUILayout.Button("移动到坐标", GUILayout.Height(25f))) MoveSelectedToConfigured(selected);
            if (GUILayout.Button("移动到准星", GUILayout.Height(25f))) MoveSelectedToCrosshair(selected);
            if (GUILayout.Button("恢复生命", GUILayout.Height(25f))) RestoreSelected(selected);
            if (GUILayout.Button("移除选中", GUILayout.Height(25f)))
            {
                LocalBotManager.Remove(selected);
                _selectedSequence = -1;
            }
            GUI.enabled = true;
            GUILayout.EndHorizontal();

            if (GUILayout.Button("移除全部本地 Bot", GUILayout.Height(26f)))
            {
                LocalBotManager.RemoveAll("manual");
                _selectedSequence = -1;
            }

            DrawSection("运行状态");
            GUILayout.Label(LocalBotManager.LastStatus, SmallLabel());

            GUILayout.EndScrollView();
            GUI.DragWindow(new Rect(0f, 0f, 10000f, 22f));
        }

        private static void SpawnAtConfiguredPosition()
        {
            Vector3 position;
            if (!TryReadPosition(out position)) return;
            SpawnBatch(position);
        }

        private static void SpawnAtCrosshair()
        {
            Vector3 point;
            if (!LocalBotManager.TryGetCrosshairPoint(CurrentCamera(), out point)) return;
            SetPositionFields(point);
            SpawnBatch(point);
        }

        private static void SpawnInFrontOfPlayer()
        {
            Character player = CurrentPlayer();
            if (player == null) return;
            float distance = ParseFloat(_frontDistance, 8f, 1f, 80f);
            Vector3 forward = player.transform.forward;
            forward.y = 0f;
            if (forward.sqrMagnitude < 0.001f) forward = Vector3.forward;
            Vector3 point = player.transform.position + forward.normalized * distance;
            SetPositionFields(point);
            SpawnBatch(point);
        }

        private static void SpawnBatch(Vector3 center)
        {
            Level level = CurrentLevel();
            Character player = CurrentPlayer();
            if (level == null || player == null) return;
            ReadOptions();

            int count = ParseInt(_count, 1, 1, 8);
            float radius = ParseFloat(_spreadRadius, 0f, 0f, 20f);
            for (int i = 0; i < count; i++)
            {
                Vector3 point = center;
                if (i > 0 && radius > 0.01f)
                {
                    float angle = i * 137.50776f * Mathf.Deg2Rad;
                    float distance = radius * Mathf.Sqrt((float)i / Mathf.Max(1f, count - 1f));
                    point += new Vector3(Mathf.Cos(angle) * distance, 0f, Mathf.Sin(angle) * distance);
                }

                LocalBotRecord ignored;
                if (!LocalBotManager.TrySpawn(level, player, point, _options, out ignored)) break;
            }
        }

        private static void MoveSelectedToConfigured(LocalBotRecord selected)
        {
            Vector3 position;
            if (!TryReadPosition(out position)) return;
            LocalBotManager.TryMove(selected, position, _options.SnapToGround, _options.FacePlayer);
        }

        private static void MoveSelectedToCrosshair(LocalBotRecord selected)
        {
            Vector3 point;
            if (!LocalBotManager.TryGetCrosshairPoint(CurrentCamera(), out point)) return;
            SetPositionFields(point);
            LocalBotManager.TryMove(selected, point, _options.SnapToGround, _options.FacePlayer);
        }

        private static void RestoreSelected(LocalBotRecord selected)
        {
            ReadOptions();
            LocalBotManager.TryRestore(selected, _options.MaxHealth, _options.Shield, _options.InvincibleSeconds);
        }

        private static void ReadPlayerPosition()
        {
            Character player = CurrentPlayer();
            if (player != null) SetPositionFields(player.transform.position);
        }

        private static void ReadCrosshairPosition()
        {
            Vector3 point;
            if (LocalBotManager.TryGetCrosshairPoint(CurrentCamera(), out point)) SetPositionFields(point);
        }

        private static void ReadOptions()
        {
            _options.NamePrefix = string.IsNullOrEmpty(_namePrefix) ? "PathBot" : _namePrefix.Trim();
            _options.MaxHealth = ParseInt(_health, 5000, 1, 1000000);
            _options.Shield = ParseInt(_shield, 0, 0, short.MaxValue);
            _options.InvincibleSeconds = ParseFloat(_invincible, 0f, 0f, 3600f);
            _options.LocalDamagePerHit = ParseInt(_localDamage, 250, 1, 1000000);
            _options.HeadshotMultiplier = ParseFloat(_headshotMultiplier, 1.5f, 1f, 10f);
            _options.RunSpeed = ParseFloat(_runSpeed, 6f, 0.5f, 20f);
            _options.JumpHeight = ParseFloat(_jumpHeight, 1.2f, 0f, 8f);
            _options.EyesDistance = ParseFloat(_eyesDistance, 100f, 1f, 250f);
            _options.FollowDistance = ParseFloat(_followDistance, 10f, 0.5f, 100f);
            _options.AttackSpread = ParseFloat(_attackSpread, 2f, 0f, 30f);
            _options.MaxWeaponUseTime = ParseFloat(_weaponUseTime, 10f, 0.5f, 120f);
        }

        private static bool TryReadPosition(out Vector3 position)
        {
            float x = 0f;
            float y = 0f;
            float z = 0f;
            bool validX = TryParseFloat(_x, out x);
            bool validY = TryParseFloat(_y, out y);
            bool validZ = TryParseFloat(_z, out z);
            bool valid = validX && validY && validZ;
            position = valid ? new Vector3(x, y, z) : Vector3.zero;
            return valid;
        }

        private static Level CurrentLevel()
        {
            try { return ASSingleton<Level>.Instance; }
            catch { return null; }
        }

        private static Character CurrentPlayer()
        {
            try
            {
                Level level = CurrentLevel();
                return level == null ? null : level.GetPlayer();
            }
            catch { return null; }
        }

        private static Camera CurrentCamera()
        {
            return CheatMain.CameraMain != null ? CheatMain.CameraMain : Camera.main;
        }

        private static LocalBotRecord FindSelected(List<LocalBotRecord> bots)
        {
            for (int i = 0; i < bots.Count; i++)
            {
                if (bots[i].Sequence == _selectedSequence) return bots[i];
            }
            return null;
        }

        private static void SetPositionFields(Vector3 position)
        {
            _x = position.x.ToString("0.###", CultureInfo.InvariantCulture);
            _y = position.y.ToString("0.###", CultureInfo.InvariantCulture);
            _z = position.z.ToString("0.###", CultureInfo.InvariantCulture);
        }

        private static string TextRow(string label, string value)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label(label, GUILayout.Width(100f));
            value = GUILayout.TextField(value ?? string.Empty, GUILayout.MinWidth(100f));
            GUILayout.EndHorizontal();
            return value;
        }

        private static void DrawSection(string title)
        {
            GUILayout.Space(6f);
            GUILayout.Label(title, SectionLabel());
        }

        private static int ParseInt(string text, int fallback, int min, int max)
        {
            int value;
            if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out value)) value = fallback;
            return Mathf.Clamp(value, min, max);
        }

        private static float ParseFloat(string text, float fallback, float min, float max)
        {
            float value;
            if (!TryParseFloat(text, out value)) value = fallback;
            return Mathf.Clamp(value, min, max);
        }

        private static bool TryParseFloat(string text, out float value)
        {
            return float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value) ||
                   float.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out value);
        }

        private static string FormatVector(Vector3 value)
        {
            return string.Format(CultureInfo.InvariantCulture, "({0:0.0},{1:0.0},{2:0.0})", value.x, value.y, value.z);
        }

        private static GUIStyle SmallLabel()
        {
            GUIStyle style = new GUIStyle(GUI.skin.label);
            style.fontSize = 11;
            style.wordWrap = true;
            return style;
        }

        private static GUIStyle SectionLabel()
        {
            GUIStyle style = new GUIStyle(GUI.skin.label);
            style.fontStyle = FontStyle.Bold;
            style.fontSize = 13;
            return style;
        }

        private static GUIStyle PrimaryButton()
        {
            GUIStyle style = new GUIStyle(GUI.skin.button);
            style.fontStyle = FontStyle.Bold;
            return style;
        }

        private static GUIStyle RowButton()
        {
            GUIStyle style = new GUIStyle(GUI.skin.button);
            style.alignment = TextAnchor.MiddleLeft;
            style.fontSize = 11;
            return style;
        }
    }
}
#endif

namespace ASWDEBUG.UI
{
    internal static class LocalBotPanel
    {
        private const float PanelWidth = 660f;
        private const float PanelHeight = 640f;
        private static readonly LocalBotSpawnOptions Options = new LocalBotSpawnOptions();
        private static Rect _window = new Rect(0f, 30f, PanelWidth, PanelHeight);
        private static Vector2 _botScroll;
        private static int _tab;
        private static int _selectedSequence = -1;
        private static int _loadedSequence = -1;
        private static bool _placed;
        private static bool _dragging;
        private static Vector2 _dragOffset;

        private static string _namePrefix = "PathBot";
        private static string _count = "1";
        private static string _spreadRadius = "0";
        private static string _frontDistance = "8";
        private static string _x = "0";
        private static string _y = "0";
        private static string _z = "0";
        private static string _health = "5000";
        private static string _shield = "0";
        private static string _invincible = "0";
        private static string _localDamage = "250";
        private static string _headMultiplier = "1.5";
        private static string _runSpeed = "6";
        private static string _jumpHeight = "1.2";
        private static string _eyesDistance = "100";
        private static string _followDistance = "10";
        private static string _attackSpread = "2";
        private static string _weaponUseTime = "10";
        private static string _attackDistance = "100";
        private static string _wanderRadius = "8";
        private static string _selectedName = string.Empty;
        private static string _animation = "idle";
        private static string _appearanceX = "0";
        private static string _appearanceY = "0";
        private static string _appearanceZ = "0";
        private static int _appearancePartIndex;
        private static int _appearanceChoiceIndex;
        private static int _weaponChoiceIndex;
        private static int _teamMode;

        private enum DropdownKind
        {
            None,
            AppearancePart,
            AppearanceChoice,
            Weapon,
            Animation,
            SpawnConfiguredKey,
            SpawnCrosshairKey,
            SpawnFrontKey,
            MoveCrosshairKey
        }

        private static DropdownKind _dropdownKind;
        private static Rect _dropdownAnchor;
        private static readonly List<string> DropdownLabels = new List<string>();
        private static Vector2 _dropdownScroll;
        private static string _dropdownSearch = string.Empty;
        private static readonly KeyCode[] HotkeyValues =
        {
            KeyCode.None, KeyCode.F6, KeyCode.F7, KeyCode.F8, KeyCode.F9, KeyCode.F10,
            KeyCode.F11, KeyCode.F12, KeyCode.Insert, KeyCode.Home, KeyCode.End,
            KeyCode.PageUp, KeyCode.PageDown, KeyCode.Alpha6, KeyCode.Alpha7,
            KeyCode.Alpha8, KeyCode.Alpha9
        };
        private static KeyCode _spawnConfiguredKey = KeyCode.F6;
        private static KeyCode _spawnCrosshairKey = KeyCode.F7;
        private static KeyCode _spawnFrontKey = KeyCode.F8;
        private static KeyCode _moveCrosshairKey = KeyCode.F9;

        private static GUIStyle _button;
        private static GUIStyle _buttonCenter;
        private static GUIStyle _label;
        private static GUIStyle _labelSmall;
        private static GUIStyle _field;

        internal static bool Visible;

        internal static void Display()
        {
            if (!Visible || !CheatUIManager.MenuVisible) return;
            EnsureStyles();

            _window.width = Mathf.Min(PanelWidth, Mathf.Max(420f, Screen.width - 20f));
            _window.height = Mathf.Min(PanelHeight, Mathf.Max(420f, Screen.height - 20f));
            if (!_placed)
            {
                _window.x = Mathf.Max(10f, Screen.width - _window.width - 10f);
                _window.y = 30f;
                _placed = true;
            }
            ClampWindow();

            UIHelper.DrawPanel(_window, new Color(0f, 0f, 0f, 0.78f), new Color(1f, 1f, 1f, 0.16f), 1f);
            UIHelper.Begin("本地 Bot", _window.x, _window.y, _window.width, _window.height, 0f, 20f, 0f);
            if (GUI.Button(R(_window.width - 24f, 1f, 22f, 18f), "×", _buttonCenter))
            {
                Visible = false;
                return;
            }
            HandleDrag();

            DrawTabs();
            List<LocalBotRecord> bots = LocalBotManager.GetSnapshot();
            LocalBotRecord selected = DrawBotList(bots);
            if (selected != null && selected.Sequence != _loadedSequence) LoadSelected(selected);

            Rect content = R(192f, 62f, _window.width - 202f, _window.height - 104f);
            UIHelper.DrawPanel(content, new Color(0.06f, 0.06f, 0.06f, 0.92f), new Color(1f, 1f, 1f, 0.12f), 1f);
            if (_tab == 0) DrawSpawn(content);
            else if (_tab == 1) DrawProperties(content, selected);
            else if (_tab == 2) DrawMovement(content, selected);
            else if (_tab == 3) DrawAppearance(content, selected);
            else DrawWeaponActions(content, selected);

            string status = Clip(LocalBotManager.LastStatus, 92);
            GUI.Label(R(8f, _window.height - 34f, _window.width - 16f, 24f), status, _labelSmall);
            DrawDropdownOverlay(selected);
        }

        internal static void TickHotkeys()
        {
            if (CurrentLevel() == null || CurrentPlayer() == null) return;
            if (Pressed(_spawnConfiguredKey)) SpawnConfigured();
            if (Pressed(_spawnCrosshairKey)) SpawnCrosshair();
            if (Pressed(_spawnFrontKey)) SpawnFront();
            if (Pressed(_moveCrosshairKey)) MoveSelectedToCrosshair();
        }

        private static void DrawTabs()
        {
            string[] tabs = { "生成", "属性", "移动", "外观", "武器/动作" };
            float x = 8f;
            float width = (_window.width - 16f) / tabs.Length;
            for (int i = 0; i < tabs.Length; i++)
            {
                if (Button(R(x + i * width, 28f, width - 2f, 26f), tabs[i], _tab == i)) _tab = i;
            }
        }

        private static LocalBotRecord DrawBotList(List<LocalBotRecord> bots)
        {
            Rect panel = R(8f, 62f, 176f, _window.height - 104f);
            UIHelper.DrawPanel(panel, new Color(0.04f, 0.04f, 0.04f, 0.94f), new Color(1f, 1f, 1f, 0.12f), 1f);
            GUI.Label(new Rect(panel.x + 6f, panel.y + 3f, panel.width - 12f, 20f), "Bot  " + bots.Count + "/16", _labelSmall);

            Rect scrollRect = new Rect(panel.x + 4f, panel.y + 24f, panel.width - 8f, panel.height - 84f);
            Rect view = new Rect(0f, 0f, scrollRect.width - 18f, Mathf.Max(scrollRect.height, bots.Count * 38f));
            _botScroll = GUI.BeginScrollView(scrollRect, _botScroll, view);
            for (int i = 0; i < bots.Count; i++)
            {
                LocalBotRecord bot = bots[i];
                Character c = bot.Character;
                string life = c == null ? "--" : (c.IsDied ? "DEAD" : c.hp + "/" + c.max_health);
                string label = bot.DisplayName + "\n" + MovementShort(bot.MovementMode) + "  " + life;
                if (Button(new Rect(0f, i * 38f, view.width, 35f), label, bot.Sequence == _selectedSequence))
                {
                    _selectedSequence = bot.Sequence;
                    _loadedSequence = -1;
                }
            }
            GUI.EndScrollView();

            LocalBotRecord selected = FindSelected(bots);
            bool oldEnabled = GUI.enabled;
            GUI.enabled = selected != null;
            if (GUI.Button(new Rect(panel.x + 4f, panel.yMax - 54f, (panel.width - 10f) * 0.5f, 24f), "移除", _buttonCenter))
            {
                LocalBotManager.Remove(selected);
                _selectedSequence = -1;
                _loadedSequence = -1;
            }
            if (GUI.Button(new Rect(panel.x + 6f + (panel.width - 10f) * 0.5f, panel.yMax - 54f, (panel.width - 10f) * 0.5f, 24f), "恢复", _buttonCenter))
            {
                ReadOptions(selected);
                LocalBotManager.TryRestore(selected, Options.MaxHealth, Options.Shield, Options.InvincibleSeconds);
            }
            GUI.enabled = oldEnabled;
            if (GUI.Button(new Rect(panel.x + 4f, panel.yMax - 27f, panel.width - 8f, 23f), "全部移除", _buttonCenter))
            {
                LocalBotManager.RemoveAll("manual");
                _selectedSequence = -1;
                _loadedSequence = -1;
            }
            return selected;
        }

        private static void DrawSpawn(Rect r)
        {
            float x = r.x + 10f;
            float y = r.y + 10f;
            float w = r.width - 20f;
            float half = (w - 8f) * 0.5f;

            Field(new Rect(x, y, half, 24f), "名称", ref _namePrefix, 42f);
            Field(new Rect(x + half + 8f, y, half, 24f), "数量", ref _count, 42f);
            y += 29f;
            Field(new Rect(x, y, half, 24f), "散布", ref _spreadRadius, 42f);
            Field(new Rect(x + half + 8f, y, half, 24f), "前距", ref _frontDistance, 42f);
            y += 31f;

            TeamButtons(x, y, w, ref _teamMode);
            y += 31f;
            PositionFields(x, y, w);
            y += 33f;

            Field(new Rect(x, y, half, 24f), "生命", ref _health, 42f);
            Field(new Rect(x + half + 8f, y, half, 24f), "护盾", ref _shield, 42f);
            y += 29f;
            Field(new Rect(x, y, half, 24f), "无敌", ref _invincible, 42f);
            Field(new Rect(x + half + 8f, y, half, 24f), "伤害", ref _localDamage, 42f);
            y += 33f;

            Options.SnapToGround = Toggle(new Rect(x, y, half, 25f), "贴地", Options.SnapToGround);
            Options.FacePlayer = Toggle(new Rect(x + half + 8f, y, half, 25f), "朝向玩家", Options.FacePlayer);
            y += 30f;
            Options.Targetable = Toggle(new Rect(x, y, half, 25f), "可锁定", Options.Targetable);
            Options.AllowAttack = Toggle(new Rect(x + half + 8f, y, half, 25f), "允许攻击", Options.AllowAttack);
            y += 38f;

            float third = (w - 12f) / 3f;
            if (Button(new Rect(x, y, third, 32f), "坐标生成", true)) SpawnConfigured();
            if (Button(new Rect(x + third + 6f, y, third, 32f), "准星生成", true)) SpawnCrosshair();
            if (Button(new Rect(x + (third + 6f) * 2f, y, third, 32f), "前方生成", true)) SpawnFront();
            y += 42f;

            GUI.Label(new Rect(x, y, w, 22f), "快捷键", _labelSmall);
            y += 24f;
            HotkeyField(new Rect(x, y, half, 25f), "坐标", _spawnConfiguredKey, DropdownKind.SpawnConfiguredKey);
            HotkeyField(new Rect(x + half + 8f, y, half, 25f), "准星", _spawnCrosshairKey, DropdownKind.SpawnCrosshairKey);
            y += 29f;
            HotkeyField(new Rect(x, y, half, 25f), "前方", _spawnFrontKey, DropdownKind.SpawnFrontKey);
            HotkeyField(new Rect(x + half + 8f, y, half, 25f), "选中Bot移至准星", _moveCrosshairKey, DropdownKind.MoveCrosshairKey);
        }

        private static void DrawProperties(Rect r, LocalBotRecord selected)
        {
            if (!RequireSelected(r, selected)) return;
            float x = r.x + 10f;
            float y = r.y + 10f;
            float w = r.width - 20f;
            float half = (w - 8f) * 0.5f;

            Field(new Rect(x, y, w, 24f), "名称", ref _selectedName, 42f);
            y += 30f;
            TeamButtons(x, y, w, ref _teamMode);
            y += 31f;
            Options.Targetable = Toggle(new Rect(x, y, half, 25f), "可锁定", Options.Targetable);
            Options.AllowAttack = Toggle(new Rect(x + half + 8f, y, half, 25f), "允许攻击", Options.AllowAttack);
            y += 31f;

            PairFields(x, y, half, "生命", ref _health, "护盾", ref _shield); y += 29f;
            PairFields(x, y, half, "无敌", ref _invincible, "伤害", ref _localDamage); y += 29f;
            PairFields(x, y, half, "爆头", ref _headMultiplier, "攻距", ref _attackDistance); y += 29f;
            PairFields(x, y, half, "速度", ref _runSpeed, "跳高", ref _jumpHeight); y += 29f;
            PairFields(x, y, half, "视野", ref _eyesDistance, "跟距", ref _followDistance); y += 29f;
            PairFields(x, y, half, "散布", ref _attackSpread, "持枪", ref _weaponUseTime); y += 29f;
            Field(new Rect(x, y, half, 24f), "巡游", ref _wanderRadius, 42f);
            y += 36f;

            if (Button(new Rect(x, y, half, 30f), "应用", true))
            {
                ReadOptions(selected);
                LocalBotManager.TryApplySettings(selected, CurrentPlayer(), _selectedName, Options);
            }
            if (Button(new Rect(x + half + 8f, y, half, 30f), "应用并恢复", true))
            {
                ReadOptions(selected);
                if (LocalBotManager.TryApplySettings(selected, CurrentPlayer(), _selectedName, Options))
                    LocalBotManager.TryRestore(selected, Options.MaxHealth, Options.Shield, Options.InvincibleSeconds);
            }
        }

        private static void DrawMovement(Rect r, LocalBotRecord selected)
        {
            if (!RequireSelected(r, selected)) return;
            float x = r.x + 10f;
            float y = r.y + 10f;
            float w = r.width - 20f;
            string[] names = { "停止", "原生", "跟随", "定点", "巡游" };
            float bw = (w - 8f) / names.Length;
            for (int i = 0; i < names.Length; i++)
            {
                LocalBotMovementMode mode = (LocalBotMovementMode)i;
                if (Button(new Rect(x + i * (bw + 2f), y, bw, 27f), names[i], selected.MovementMode == mode))
                {
                    Vector3 target;
                    if (!TryReadPosition(out target)) target = selected.MovementTarget;
                    LocalBotManager.TrySetMovement(selected, mode, target, true);
                    Options.MovementMode = mode;
                }
            }
            y += 39f;
            PositionFields(x, y, w);
            y += 31f;

            float half = (w - 8f) * 0.5f;
            if (Button(new Rect(x, y, half, 26f), "目标=玩家", false)) SetPosition(CurrentPlayerPosition());
            if (Button(new Rect(x + half + 8f, y, half, 26f), "目标=准星", false)) ReadCrosshair();
            y += 34f;
            PairFields(x, y, half, "跟距", ref _followDistance, "巡游", ref _wanderRadius); y += 34f;

            Vector3 configured;
            bool valid = TryReadPosition(out configured);
            if (Button(new Rect(x, y, half, 29f), "前往坐标", true) && valid)
                LocalBotManager.TrySetMovement(selected, LocalBotMovementMode.MoveToPoint, configured, true);
            if (Button(new Rect(x + half + 8f, y, half, 29f), "围绕坐标", true) && valid)
            {
                ReadOptions(selected);
                LocalBotManager.TryApplySettings(selected, CurrentPlayer(), _selectedName, Options);
                LocalBotManager.TrySetMovement(selected, LocalBotMovementMode.Wander, configured, true);
            }
            y += 37f;
            if (Button(new Rect(x, y, half, 27f), "传送坐标", false) && valid)
                LocalBotManager.TryMove(selected, configured, Options.SnapToGround, Options.FacePlayer);
            if (Button(new Rect(x + half + 8f, y, half, 27f), "传送准星", false))
            {
                Vector3 point;
                if (LocalBotManager.TryGetCrosshairPoint(CurrentCamera(), out point))
                {
                    SetPosition(point);
                    LocalBotManager.TryMove(selected, point, Options.SnapToGround, Options.FacePlayer);
                }
            }
            y += 39f;
            Character c = selected.Character;
            if (c != null)
                GUI.Label(new Rect(x, y, w, 24f), "位置 " + FormatVector(c.transform.position), _labelSmall);
        }

        private static void DrawAppearance(Rect r, LocalBotRecord selected)
        {
            if (!RequireSelected(r, selected)) return;
            float x = r.x + 10f;
            float y = r.y + 10f;
            float w = r.width - 20f;
            float half = (w - 8f) * 0.5f;

            if (Button(new Rect(x, y, w, 30f), "复制玩家外观", true))
            {
                LocalBotManager.TryCopyPlayerAppearance(selected, CurrentPlayer());
                LoadSelected(selected);
            }
            y += 40f;
            string[] partNames = LocalBotManager.GetAppearancePartNames();
            string partName = _appearancePartIndex >= 0 && _appearancePartIndex < partNames.Length
                ? partNames[_appearancePartIndex]
                : "皮肤";
            DropdownField(new Rect(x, y, w, 26f), "部位", partName, DropdownKind.AppearancePart, ToList(partNames), 42f);
            y += 34f;

            List<LocalBotAppearanceChoice> choices = LocalBotManager.GetAppearanceChoices(_appearancePartIndex);
            _appearanceChoiceIndex = Mathf.Clamp(_appearanceChoiceIndex, 0, Mathf.Max(0, choices.Count - 1));
            string appearanceName = choices.Count == 0 ? "无" : choices[_appearanceChoiceIndex].Label;
            DropdownField(new Rect(x, y, w, 26f), "外观", appearanceName, DropdownKind.AppearanceChoice, AppearanceLabels(choices), 42f);
            y += 38f;

            GUI.Label(new Rect(x, y, w, 22f), "部位坐标（骨骼固定部位不提供坐标）", _labelSmall);
            y += 24f;
            float third = (w - 8f) / 3f;
            Field(new Rect(x, y, third, 24f), "X", ref _appearanceX, 16f);
            Field(new Rect(x + third + 4f, y, third, 24f), "Y", ref _appearanceY, 16f);
            Field(new Rect(x + (third + 4f) * 2f, y, third, 24f), "Z", ref _appearanceZ, 16f);
            y += 34f;
            if (Button(new Rect(x, y, half, 30f), "应用坐标", true)) ApplyAppearanceOffset(selected);
            if (Button(new Rect(x + half + 8f, y, half, 30f), "重读当前部位", false)) LoadAppearanceState(selected);
        }

        private static void DrawWeaponActions(Rect r, LocalBotRecord selected)
        {
            if (!RequireSelected(r, selected)) return;
            float x = r.x + 10f;
            float y = r.y + 10f;
            float w = r.width - 20f;
            float half = (w - 8f) * 0.5f;

            List<string> weapons = LocalBotManager.GetWeaponChoices(selected);
            _weaponChoiceIndex = Mathf.Clamp(LocalBotManager.CurrentWeaponIndex(selected), 0, Mathf.Max(0, weapons.Count - 1));
            string weaponName = weapons.Count == 0 ? "无" : weapons[_weaponChoiceIndex];
            DropdownField(new Rect(x, y, w, 26f), "武器", weaponName, DropdownKind.Weapon, weapons, 42f);
            y += 38f;
            if (Button(new Rect(x, y, w, 28f), "从玩家武器栏重新枚举", true))
            {
                LocalBotManager.TryCopyPlayerWeapons(selected, CurrentPlayer());
                _weaponChoiceIndex = LocalBotManager.CurrentWeaponIndex(selected);
            }
            y += 38f;

            string[] actions = { "跳跃", "滑步", "朝向我", "装弹", "翻滚", "受击" };
            float third = (w - 8f) / 3f;
            for (int i = 0; i < actions.Length; i++)
            {
                int row = i / 3;
                int col = i % 3;
                if (!Button(new Rect(x + col * (third + 4f), y + row * 31f, third, 27f), actions[i], false)) continue;
                if (i == 0) LocalBotManager.TryJump(selected);
                else if (i == 1) LocalBotManager.TrySpurt(selected);
                else if (i == 2) LocalBotManager.TryFacePlayer(selected, CurrentPlayer());
                else if (i == 3) LocalBotManager.TryReload(selected);
                else if (i == 4) LocalBotManager.TryPlayAnimation(selected, "rollforward");
                else LocalBotManager.TryPlayAnimation(selected, "hit");
            }
            y += 73f;
            List<string> animations = LocalBotManager.GetAnimationChoices(selected);
            DropdownField(new Rect(x, y, w, 26f), "动作", _animation, DropdownKind.Animation, animations, 42f);
            y += 33f;
            if (Button(new Rect(x, y, half, 29f), "播放", true))
                LocalBotManager.TryPlayAnimation(selected, _animation);
            if (Button(new Rect(x + half + 8f, y, half, 29f), "复位", false))
                LocalBotManager.TryStopAnimation(selected, _animation);
        }

        private static bool RequireSelected(Rect r, LocalBotRecord selected)
        {
            if (selected != null) return true;
            GUI.Label(new Rect(r.x + 12f, r.y + 12f, r.width - 24f, 24f), "先选择 Bot", _label);
            return false;
        }

        private static void LoadSelected(LocalBotRecord record)
        {
            if (record == null || record.Character == null) return;
            Character bot = record.Character;
            _loadedSequence = record.Sequence;
            _selectedName = record.DisplayName ?? string.Empty;
            _teamMode = bot.GetTeam() == 0 ? 1 : 2;
            _health = bot.max_health.ToString(CultureInfo.InvariantCulture);
            _shield = bot.shield.ToString(CultureInfo.InvariantCulture);
            _invincible = Mathf.Max(0f, bot.invincible_time).ToString("0.##", CultureInfo.InvariantCulture);
            _localDamage = record.LocalDamagePerHit.ToString(CultureInfo.InvariantCulture);
            _headMultiplier = record.HeadshotMultiplier.ToString("0.##", CultureInfo.InvariantCulture);
            _runSpeed = record.RunSpeed.ToString("0.##", CultureInfo.InvariantCulture);
            _jumpHeight = record.JumpHeight.ToString("0.##", CultureInfo.InvariantCulture);
            _eyesDistance = record.EyesDistance.ToString("0.##", CultureInfo.InvariantCulture);
            _followDistance = record.FollowDistance.ToString("0.##", CultureInfo.InvariantCulture);
            _attackSpread = record.AttackSpread.ToString("0.##", CultureInfo.InvariantCulture);
            _weaponUseTime = record.MaxWeaponUseTime.ToString("0.##", CultureInfo.InvariantCulture);
            _attackDistance = record.AttackDistance.ToString("0.##", CultureInfo.InvariantCulture);
            _wanderRadius = record.WanderRadius.ToString("0.##", CultureInfo.InvariantCulture);
            Options.Targetable = record.Targetable;
            Options.AllowAttack = record.AllowAttack;
            Options.MovementMode = record.MovementMode;
            _weaponChoiceIndex = LocalBotManager.CurrentWeaponIndex(record);
            LoadAppearanceState(record);
            SetPosition(record.MovementTarget);
        }

        private static void ReadOptions(LocalBotRecord selected)
        {
            Options.NamePrefix = string.IsNullOrEmpty(_namePrefix) ? "PathBot" : _namePrefix.Trim();
            Options.TeamMode = Mathf.Clamp(_teamMode, 0, 2);
            Options.MaxHealth = ParseInt(_health, 5000, 1, 1000000);
            Options.Shield = ParseInt(_shield, 0, 0, short.MaxValue);
            Options.InvincibleSeconds = ParseFloat(_invincible, 0f, 0f, 3600f);
            Options.LocalDamagePerHit = ParseInt(_localDamage, 250, 1, 1000000);
            Options.HeadshotMultiplier = ParseFloat(_headMultiplier, 1.5f, 1f, 10f);
            Options.RunSpeed = ParseFloat(_runSpeed, 6f, 0.5f, 20f);
            Options.JumpHeight = ParseFloat(_jumpHeight, 1.2f, 0f, 8f);
            Options.EyesDistance = ParseFloat(_eyesDistance, 100f, 1f, 250f);
            Options.FollowDistance = ParseFloat(_followDistance, 10f, 0.5f, 100f);
            Options.AttackSpread = ParseFloat(_attackSpread, 2f, 0f, 30f);
            Options.MaxWeaponUseTime = ParseFloat(_weaponUseTime, 10f, 0.5f, 120f);
            Options.AttackDistance = ParseFloat(_attackDistance, 100f, 0.5f, 250f);
            Options.WanderRadius = ParseFloat(_wanderRadius, 8f, 1f, 80f);
            if (selected != null) Options.MovementMode = selected.MovementMode;
        }

        private static void SpawnConfigured()
        {
            Vector3 point;
            if (TryReadPosition(out point)) SpawnBatch(point);
        }

        private static void SpawnCrosshair()
        {
            Vector3 point;
            if (!LocalBotManager.TryGetCrosshairPoint(CurrentCamera(), out point)) return;
            SetPosition(point);
            SpawnBatch(point);
        }

        private static void SpawnFront()
        {
            Character player = CurrentPlayer();
            if (player == null) return;
            Vector3 forward = player.transform.forward;
            forward.y = 0f;
            if (forward.sqrMagnitude < 0.001f) forward = Vector3.forward;
            Vector3 point = player.transform.position + forward.normalized * ParseFloat(_frontDistance, 8f, 1f, 80f);
            SetPosition(point);
            SpawnBatch(point);
        }

        private static void SpawnBatch(Vector3 center)
        {
            Level level = CurrentLevel();
            Character player = CurrentPlayer();
            if (level == null || player == null) return;
            ReadOptions(null);
            Options.MovementMode = LocalBotMovementMode.Stationary;

            int count = ParseInt(_count, 1, 1, 8);
            float radius = ParseFloat(_spreadRadius, 0f, 0f, 20f);
            for (int i = 0; i < count; i++)
            {
                Vector3 point = center;
                if (i > 0 && radius > 0.01f)
                {
                    float angle = i * 137.50776f * Mathf.Deg2Rad;
                    float distance = radius * Mathf.Sqrt((float)i / Mathf.Max(1f, count - 1f));
                    point += new Vector3(Mathf.Cos(angle) * distance, 0f, Mathf.Sin(angle) * distance);
                }
                LocalBotRecord created;
                if (!LocalBotManager.TrySpawn(level, player, point, Options, out created)) break;
                _selectedSequence = created.Sequence;
                _loadedSequence = -1;
            }
        }

        private static void MoveSelectedToCrosshair()
        {
            LocalBotRecord selected = FindSelected(LocalBotManager.GetSnapshot());
            if (selected == null) return;
            Vector3 point;
            if (!LocalBotManager.TryGetCrosshairPoint(CurrentCamera(), out point)) return;
            LocalBotManager.TryMove(selected, point, Options.SnapToGround, Options.FacePlayer);
            SetPosition(point);
        }

        private static bool Pressed(KeyCode key)
        {
            if (key == KeyCode.None) return false;
            try { return Input.GetKeyDown(key); }
            catch { return false; }
        }

        private static void LoadAppearanceState(LocalBotRecord selected)
        {
            if (selected == null) return;
            List<LocalBotAppearanceChoice> choices = LocalBotManager.GetAppearanceChoices(_appearancePartIndex);
            string current = LocalBotManager.GetAppearanceResource(selected, _appearancePartIndex);
            _appearanceChoiceIndex = 0;
            for (int i = 0; i < choices.Count; i++)
            {
                if (string.Equals(choices[i].Resource, current, StringComparison.OrdinalIgnoreCase))
                {
                    _appearanceChoiceIndex = i;
                    break;
                }
            }

            Vector3 offset;
            if (!LocalBotManager.TryGetAppearanceOffset(selected, _appearancePartIndex, out offset))
                offset = Vector3.zero;
            _appearanceX = offset.x.ToString("0.######", CultureInfo.InvariantCulture);
            _appearanceY = offset.y.ToString("0.######", CultureInfo.InvariantCulture);
            _appearanceZ = offset.z.ToString("0.######", CultureInfo.InvariantCulture);
        }

        private static void ApplyAppearanceOffset(LocalBotRecord selected)
        {
            float x;
            float y;
            float z;
            if (!TryParseFloat(_appearanceX, out x) || !TryParseFloat(_appearanceY, out y) || !TryParseFloat(_appearanceZ, out z))
                return;
            if (LocalBotManager.TryApplyAppearanceOffset(selected, _appearancePartIndex, new Vector3(x, y, z)))
                LoadAppearanceState(selected);
        }

        private static void DropdownField(Rect rect, string label, string value, DropdownKind kind, List<string> labels, float labelWidth)
        {
            GUI.Label(new Rect(rect.x, rect.y, labelWidth, rect.height), label, _labelSmall);
            Rect buttonRect = new Rect(rect.x + labelWidth, rect.y, rect.width - labelWidth, rect.height);
            if (GUI.Button(buttonRect, Clip(string.IsNullOrEmpty(value) ? "无" : value, 34) + "  ▼", _buttonCenter))
            {
                if (_dropdownKind == kind)
                {
                    _dropdownKind = DropdownKind.None;
                    return;
                }
                _dropdownKind = kind;
                _dropdownAnchor = buttonRect;
                _dropdownScroll = Vector2.zero;
                _dropdownSearch = string.Empty;
                DropdownLabels.Clear();
                if (labels != null) DropdownLabels.AddRange(labels);
                if (DropdownLabels.Count == 0) DropdownLabels.Add("无");
            }
        }

        private static void HotkeyField(Rect rect, string label, KeyCode value, DropdownKind kind)
        {
            List<string> labels = new List<string>();
            for (int i = 0; i < HotkeyValues.Length; i++) labels.Add(HotkeyLabel(HotkeyValues[i]));
            DropdownField(rect, label, HotkeyLabel(value), kind, labels, label.Length > 6 ? 108f : 42f);
        }

        private static void DrawDropdownOverlay(LocalBotRecord selected)
        {
            if (_dropdownKind == DropdownKind.None) return;
            float rowHeight = 24f;
            bool searchable = DropdownLabels.Count > 16;
            List<int> visible = new List<int>();
            for (int i = 0; i < DropdownLabels.Count; i++)
            {
                if (!searchable || string.IsNullOrEmpty(_dropdownSearch) ||
                    DropdownLabels[i].IndexOf(_dropdownSearch, StringComparison.OrdinalIgnoreCase) >= 0)
                    visible.Add(i);
            }
            float searchHeight = searchable ? 30f : 0f;
            float height = Mathf.Min(250f, visible.Count * rowHeight + 6f + searchHeight);
            if (height < 58f) height = 58f;
            float y = _dropdownAnchor.yMax + 2f;
            if (y + height > Screen.height - 4f) y = Mathf.Max(4f, _dropdownAnchor.y - height - 2f);
            float width = Mathf.Min(Screen.width - _dropdownAnchor.x - 4f, Mathf.Max(300f, _dropdownAnchor.width));
            Rect popup = new Rect(_dropdownAnchor.x, y, width, height);
            UIHelper.DrawPanel(popup, new Color(0.035f, 0.035f, 0.035f, 0.98f), new Color(1f, 1f, 1f, 0.25f), 1f);
            if (searchable)
                _dropdownSearch = GUI.TextField(new Rect(popup.x + 4f, popup.y + 3f, popup.width - 8f, 24f), _dropdownSearch ?? string.Empty, _field);
            Rect scroll = new Rect(popup.x + 2f, popup.y + 2f + searchHeight, popup.width - 4f, popup.height - 4f - searchHeight);
            Rect view = new Rect(0f, 0f, scroll.width - 16f, Mathf.Max(scroll.height, visible.Count * rowHeight));
            _dropdownScroll = GUI.BeginScrollView(scroll, _dropdownScroll, view);
            for (int i = 0; i < visible.Count; i++)
            {
                int sourceIndex = visible[i];
                if (!GUI.Button(new Rect(0f, i * rowHeight, view.width, rowHeight - 1f), DropdownLabels[sourceIndex], _buttonCenter)) continue;
                ApplyDropdownSelection(selected, sourceIndex);
                _dropdownKind = DropdownKind.None;
                break;
            }
            GUI.EndScrollView();
        }

        private static void ApplyDropdownSelection(LocalBotRecord selected, int index)
        {
            if (_dropdownKind == DropdownKind.AppearancePart)
            {
                _appearancePartIndex = Mathf.Clamp(index, 0, 17);
                LoadAppearanceState(selected);
            }
            else if (_dropdownKind == DropdownKind.AppearanceChoice)
            {
                List<LocalBotAppearanceChoice> choices = LocalBotManager.GetAppearanceChoices(_appearancePartIndex);
                if (selected != null && index >= 0 && index < choices.Count &&
                    LocalBotManager.TryApplyAppearanceChoice(selected, _appearancePartIndex, choices[index]))
                {
                    _appearanceChoiceIndex = index;
                    LoadAppearanceState(selected);
                }
            }
            else if (_dropdownKind == DropdownKind.Weapon)
            {
                if (LocalBotManager.TrySelectWeapon(selected, index)) _weaponChoiceIndex = index;
            }
            else if (_dropdownKind == DropdownKind.Animation)
            {
                if (index >= 0 && index < DropdownLabels.Count) _animation = DropdownLabels[index];
            }
            else
            {
                KeyCode key = index >= 0 && index < HotkeyValues.Length ? HotkeyValues[index] : KeyCode.None;
                if (_dropdownKind == DropdownKind.SpawnConfiguredKey) _spawnConfiguredKey = key;
                else if (_dropdownKind == DropdownKind.SpawnCrosshairKey) _spawnCrosshairKey = key;
                else if (_dropdownKind == DropdownKind.SpawnFrontKey) _spawnFrontKey = key;
                else if (_dropdownKind == DropdownKind.MoveCrosshairKey) _moveCrosshairKey = key;
            }
        }

        private static List<string> ToList(string[] values)
        {
            return values == null ? new List<string>() : new List<string>(values);
        }

        private static List<string> AppearanceLabels(List<LocalBotAppearanceChoice> choices)
        {
            List<string> labels = new List<string>();
            if (choices == null) return labels;
            for (int i = 0; i < choices.Count; i++) labels.Add(choices[i].Label);
            return labels;
        }

        private static string HotkeyLabel(KeyCode key)
        {
            return key == KeyCode.None ? "未设置" : key.ToString();
        }

        private static void TeamButtons(float x, float y, float width, ref int value)
        {
            string[] names = { "自动敌方", "队伍0", "队伍1" };
            float w = (width - 4f) / 3f;
            for (int i = 0; i < 3; i++)
            {
                if (Button(new Rect(x + i * (w + 2f), y, w, 26f), names[i], value == i)) value = i;
            }
        }

        private static void PositionFields(float x, float y, float width)
        {
            float w = (width - 8f) / 3f;
            Field(new Rect(x, y, w, 24f), "X", ref _x, 16f);
            Field(new Rect(x + w + 4f, y, w, 24f), "Y", ref _y, 16f);
            Field(new Rect(x + (w + 4f) * 2f, y, w, 24f), "Z", ref _z, 16f);
        }

        private static void PairFields(float x, float y, float half, string a, ref string av, string b, ref string bv)
        {
            Field(new Rect(x, y, half, 24f), a, ref av, 42f);
            Field(new Rect(x + half + 8f, y, half, 24f), b, ref bv, 42f);
        }

        private static void Field(Rect rect, string label, ref string value, float labelWidth)
        {
            GUI.Label(new Rect(rect.x, rect.y, labelWidth, rect.height), label, _labelSmall);
            value = GUI.TextField(
                new Rect(rect.x + labelWidth, rect.y, rect.width - labelWidth, rect.height),
                value ?? string.Empty,
                _field);
        }

        private static bool Toggle(Rect rect, string text, bool value)
        {
            if (Button(rect, (value ? "● " : "○ ") + text, value)) value = !value;
            return value;
        }

        private static bool Button(Rect rect, string text, bool active)
        {
            string label = active ? "<color=#E71200>●</color> " + text : text;
            return GUI.Button(rect, label, _buttonCenter);
        }

        private static void EnsureStyles()
        {
            if (_button != null) return;
            _button = new GUIStyle(UIHelper.ButtonStyle ?? GUI.skin.button);
            _button.richText = true;
            _button.fontSize = 12;
            _buttonCenter = new GUIStyle(_button);
            _buttonCenter.alignment = TextAnchor.MiddleCenter;
            _label = new GUIStyle(UIHelper.StringStyle ?? GUI.skin.label);
            _label.normal.textColor = Color.white;
            _label.fontSize = 13;
            _labelSmall = new GUIStyle(_label);
            _labelSmall.fontSize = 11;
            _labelSmall.alignment = TextAnchor.MiddleLeft;
            _field = new GUIStyle(UIHelper.TextFieldStyle ?? GUI.skin.textField);
            _field.fontSize = 12;
        }

        private static void HandleDrag()
        {
            Event e = Event.current;
            Rect title = R(0f, 0f, _window.width - 28f, 20f);
            if (e.type == EventType.MouseDown && e.button == 0 && title.Contains(e.mousePosition))
            {
                _dragging = true;
                _dragOffset = e.mousePosition - new Vector2(_window.x, _window.y);
                e.Use();
            }
            else if (e.type == EventType.MouseDrag && _dragging)
            {
                _window.x = e.mousePosition.x - _dragOffset.x;
                _window.y = e.mousePosition.y - _dragOffset.y;
                ClampWindow();
                e.Use();
            }
            else if (e.type == EventType.MouseUp) _dragging = false;
        }

        private static void ClampWindow()
        {
            _window.x = Mathf.Clamp(_window.x, 0f, Mathf.Max(0f, Screen.width - _window.width));
            _window.y = Mathf.Clamp(_window.y, 0f, Mathf.Max(0f, Screen.height - _window.height));
        }

        private static Rect R(float x, float y, float width, float height)
        {
            return new Rect(_window.x + x, _window.y + y, width, height);
        }

        private static LocalBotRecord FindSelected(List<LocalBotRecord> bots)
        {
            for (int i = 0; i < bots.Count; i++)
                if (bots[i].Sequence == _selectedSequence) return bots[i];
            if (bots.Count > 0)
            {
                _selectedSequence = bots[0].Sequence;
                _loadedSequence = -1;
                return bots[0];
            }
            return null;
        }

        private static void ReadCrosshair()
        {
            Vector3 point;
            if (LocalBotManager.TryGetCrosshairPoint(CurrentCamera(), out point)) SetPosition(point);
        }

        private static Vector3 CurrentPlayerPosition()
        {
            Character player = CurrentPlayer();
            return player == null ? Vector3.zero : player.transform.position;
        }

        private static Level CurrentLevel()
        {
            try { return ASSingleton<Level>.Instance; }
            catch { return null; }
        }

        private static Character CurrentPlayer()
        {
            Level level = CurrentLevel();
            try { return level == null ? null : level.GetPlayer(); }
            catch { return null; }
        }

        private static Camera CurrentCamera()
        {
            return CheatMain.CameraMain != null ? CheatMain.CameraMain : Camera.main;
        }

        private static void SetPosition(Vector3 value)
        {
            _x = value.x.ToString("0.###", CultureInfo.InvariantCulture);
            _y = value.y.ToString("0.###", CultureInfo.InvariantCulture);
            _z = value.z.ToString("0.###", CultureInfo.InvariantCulture);
        }

        private static bool TryReadPosition(out Vector3 value)
        {
            float x = 0f;
            float y = 0f;
            float z = 0f;
            bool valid = TryParseFloat(_x, out x) && TryParseFloat(_y, out y) && TryParseFloat(_z, out z);
            value = valid ? new Vector3(x, y, z) : Vector3.zero;
            return valid;
        }

        private static int ParseInt(string text, int fallback, int min, int max)
        {
            int value;
            if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out value)) value = fallback;
            return Mathf.Clamp(value, min, max);
        }

        private static float ParseFloat(string text, float fallback, float min, float max)
        {
            float value;
            if (!TryParseFloat(text, out value)) value = fallback;
            return Mathf.Clamp(value, min, max);
        }

        private static bool TryParseFloat(string text, out float value)
        {
            return float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value) ||
                   float.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out value);
        }

        private static string FormatVector(Vector3 value)
        {
            return string.Format(CultureInfo.InvariantCulture, "({0:0.0}, {1:0.0}, {2:0.0})", value.x, value.y, value.z);
        }

        private static string MovementShort(LocalBotMovementMode mode)
        {
            switch (mode)
            {
                case LocalBotMovementMode.NativeAI: return "原生";
                case LocalBotMovementMode.FollowPlayer: return "跟随";
                case LocalBotMovementMode.MoveToPoint: return "定点";
                case LocalBotMovementMode.Wander: return "巡游";
                default: return "停止";
            }
        }

        private static string Clip(string value, int max)
        {
            if (string.IsNullOrEmpty(value) || value.Length <= max) return value ?? string.Empty;
            return value.Substring(0, max - 1) + "…";
        }
    }
}
