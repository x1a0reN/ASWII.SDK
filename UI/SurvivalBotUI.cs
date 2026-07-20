using ASWDEBUG.Cheats.AutoBattle;
using ASWDEBUG.Cheats.SurvivalBot;
using ASWDEBUG.Patch;
using UnityEngine;

namespace ASWDEBUG.UI
{
    public static class SurvivalBotUI
    {
        private enum DropdownId
        {
            None,
            Tactics,
            Role,
            EnemyEsp,
            Defense,
            MatchTimeout,
            ParticipantCapture,
            Separation,
            EmergencyDistance,
            SafePointRefresh,
            SuicideFallback,
            GmStopRounds,
            MapBakeTarget
        }

        private const float WindowWidth = 388f;
        private const float WindowHeight = 520f;
        private const float RowHeight = 26f;

        private static readonly string[] TacticsNames = { "稳健", "标准", "激进" };
        private static readonly string[] RoleNames = { "自动", "通用" };
        private static readonly string[] EnabledNames = { "开启", "关闭" };
        private static readonly string[] DefenseNames = { "自动", "隐身优先", "护盾优先", "关闭" };
        private static readonly string[] MatchTimeoutNames = { "5 分钟", "10 分钟", "15 分钟" };
        private static readonly string[] ParticipantCaptureNames = { "3 秒", "5 秒", "8 秒" };
        private static readonly string[] SeparationNames = { "9 米", "11 米", "13 米", "15 米", "18 米" };
        private static readonly string[] EmergencyDistanceNames = { "6 米", "8 米", "10 米", "12 米" };
        private static readonly string[] SafePointRefreshNames = { "0.8 秒", "1.0 秒", "1.35 秒", "2.0 秒" };
        private static readonly string[] SuicideFallbackNames = { "15 秒", "25 秒", "40 秒" };
        private static readonly string[] GmStopRoundNames = { "1 局", "2 局", "3 局" };

        private static readonly float[] MatchTimeoutValues = { 300f, 600f, 900f };
        private static readonly float[] ParticipantCaptureValues = { 3f, 5f, 8f };
        private static readonly float[] SeparationValues = { 9f, 11f, 13f, 15f, 18f };
        private static readonly float[] EmergencyDistanceValues = { 6f, 8f, 10f, 12f };
        private static readonly float[] SafePointRefreshValues = { 0.8f, 1f, 1.35f, 2f };
        private static readonly float[] SuicideFallbackValues = { 15f, 25f, 40f };

        private static Rect _window = new Rect(12f, 12f, WindowWidth, WindowHeight);
        private static bool _placed;
        private static bool _dragging;
        private static Vector2 _dragOffset;
        private static int _deleteFrame = -1;
        private static DropdownId _openDropdown;
        private static Rect _dropdownAnchor;
        private static string[] _dropdownOptions;
        private static int _dropdownSelected;
        private static int _dropdownFirstVisible;
        private static int _dropdownMapOptionsVersion;
        private static float _rowStride = RowHeight + 1f;

        private static GUIStyle _titleStyle;
        private static GUIStyle _labelStyle;
        private static GUIStyle _secondaryLabelStyle;
        private static GUIStyle _buttonStyle;
        private static GUIStyle _buttonCenterStyle;
        private static Texture2D _panelTexture;
        private static Texture2D _panelInnerTexture;
        private static Texture2D _popupTexture;
        private static Texture2D _borderTexture;
        private static Texture2D _popupBorderTexture;
        private static Texture2D _titleTexture;
        private static Texture2D _accentTexture;

        public static bool Visible = true;

        public static void Display()
        {
            HandleVisibilityHotkey();
            if (!Visible) return;

            EnsureStyles();
            SurvivalBotSettings.EnsureLoaded();
            PlaceAndClampWindow();

            bool oldEnabled = GUI.enabled;
            if (_openDropdown != DropdownId.None) GUI.enabled = false;

            DrawPanel(_window, _panelTexture, _borderTexture);
            GUI.DrawTexture(new Rect(_window.x, _window.y, _window.width, 22f), _titleTexture);
            GUI.Label(new Rect(_window.x + 8f, _window.y, _window.width - 42f, 22f), "生存机器人", _titleStyle);

            if (GUI.Button(new Rect(_window.xMax - 27f, _window.y + 1f, 24f, 20f), "×", _buttonCenterStyle))
            {
                Visible = false;
                _openDropdown = DropdownId.None;
            }

            HandleWindowDrag();

            float x = _window.x + 8f;
            float width = _window.width - 16f;
            float y = _window.y + 29f;

            Rect summary = new Rect(x, y, width, 24f);
            DrawPanel(summary, _panelInnerTexture, _borderTexture);
            string phase = PhaseName(SurvivalBotManager.Phase);
            string players = SurvivalBotManager.InitialPlayers + " / " + SurvivalBotManager.RemainingPlayers;
            string currentRole = SurvivalBotManager.MapBakeEnabled ? "不接管" :
                SurvivalBotManager.CombatTestEnabled || SurvivalBotManager.RoomTestEnabled
                    ? AutoBattleManager.CurrentRole
                    : SurvivalCombatAdapter.CurrentRole;
            GUI.Label(new Rect(summary.x + 7f, summary.y, summary.width * 0.62f, summary.height),
                "阶段  " + phase + "  |  职业  " + currentRole, _secondaryLabelStyle);
            GUI.Label(new Rect(summary.x + summary.width * 0.62f, summary.y, summary.width * 0.38f - 7f, summary.height),
                "初始/存活  " + players, _secondaryLabelStyle);
            y += 29f;

            const float buttonGap = 4f;
            float modeButtonWidth = (width - buttonGap * 3f) / 4f;
            Rect runButton = new Rect(x, y, modeButtonWidth, 27f);
            string runText = SurvivalBotManager.Enabled ? "生存循环：开" : "生存循环：关";
            if (GUI.Button(runButton, runText, _buttonCenterStyle))
            {
                SurvivalBotManager.SetEnabled(!SurvivalBotManager.Enabled, "ui");
            }
            if (SurvivalBotManager.Enabled)
                GUI.DrawTexture(new Rect(runButton.xMax - 5f, runButton.y + 3f, 3f, runButton.height - 6f), _accentTexture);

            Rect combatTestButton = new Rect(runButton.xMax + buttonGap, y, modeButtonWidth, 27f);
            string combatTestText = SurvivalBotManager.CombatTestEnabled ? "战斗测试：开" : "战斗测试：关";
            if (GUI.Button(combatTestButton, combatTestText, _buttonCenterStyle))
            {
                SurvivalBotManager.SetCombatTestEnabled(!SurvivalBotManager.CombatTestEnabled, "ui");
            }
            if (SurvivalBotManager.CombatTestEnabled)
                GUI.DrawTexture(new Rect(combatTestButton.xMax - 5f, combatTestButton.y + 3f, 3f,
                    combatTestButton.height - 6f), _accentTexture);

            Rect roomTestButton = new Rect(combatTestButton.xMax + buttonGap, y, modeButtonWidth, 27f);
            string roomTestText = SurvivalBotManager.RoomTestEnabled ? "开房测试：开" : "开房测试：关";
            if (GUI.Button(roomTestButton, roomTestText, _buttonCenterStyle))
            {
                SurvivalBotManager.SetRoomTestEnabled(!SurvivalBotManager.RoomTestEnabled, "ui");
            }
            if (SurvivalBotManager.RoomTestEnabled)
                GUI.DrawTexture(new Rect(roomTestButton.xMax - 5f, roomTestButton.y + 3f, 3f,
                    roomTestButton.height - 6f), _accentTexture);

            Rect mapBakeButton = new Rect(roomTestButton.xMax + buttonGap, y, modeButtonWidth, 27f);
            string mapBakeText = SurvivalBotManager.MapBakeEnabled ? "地图建图：开" : "地图建图：关";
            if (GUI.Button(mapBakeButton, mapBakeText, _buttonCenterStyle))
            {
                SurvivalBotManager.SetMapBakeEnabled(!SurvivalBotManager.MapBakeEnabled, "ui");
            }
            if (SurvivalBotManager.MapBakeEnabled)
                GUI.DrawTexture(new Rect(mapBakeButton.xMax - 5f, mapBakeButton.y + 3f, 3f,
                    mapBakeButton.height - 6f), _accentTexture);
            y += 33f;

            DrawMapBakeLaunchRow(ref y, x, width);

            float statusTop = _window.yMax - 53f;
            bool compactNavigation = _window.height < 470f;
            float navigationHeight = compactNavigation ? 52f : 112f;
            float navigationTop = statusTop - navigationHeight - 5f;
            _rowStride = Mathf.Clamp((navigationTop - y - 2f) / 11f,
                compactNavigation ? 13f : 17f, RowHeight + 1f);

            DrawDropdownRow(ref y, "战术", DropdownId.Tactics, TacticsNames, SurvivalBotSettings.TacticsMode);
            DrawDropdownRow(ref y, "职业策略", DropdownId.Role, RoleNames, SurvivalBotSettings.RoleStrategyEnabled ? 0 : 1);
            DrawDropdownRow(ref y, "敌人 ESP", DropdownId.EnemyEsp, EnabledNames, SurvivalBotSettings.EnemyEspEnabled ? 0 : 1);
            DrawDropdownRow(ref y, "保命技能", DropdownId.Defense, DefenseNames, SurvivalBotSettings.DefenseMode);
            DrawDropdownRow(ref y, "匹配超时", DropdownId.MatchTimeout, MatchTimeoutNames, FindNearest(MatchTimeoutValues, SurvivalBotSettings.MatchTimeoutSeconds));
            DrawDropdownRow(ref y, "人数锁定", DropdownId.ParticipantCapture, ParticipantCaptureNames, FindNearest(ParticipantCaptureValues, SurvivalBotSettings.ParticipantCaptureSeconds));
            DrawDropdownRow(ref y, "躲避距离", DropdownId.Separation, SeparationNames, FindNearest(SeparationValues, SurvivalBotSettings.DesiredSeparation));
            DrawDropdownRow(ref y, "近敌反击", DropdownId.EmergencyDistance, EmergencyDistanceNames, FindNearest(EmergencyDistanceValues, SurvivalBotSettings.EmergencyDistance));
            DrawDropdownRow(ref y, "躲避刷新", DropdownId.SafePointRefresh, SafePointRefreshNames, FindNearest(SafePointRefreshValues, SurvivalBotSettings.SafePointRefreshSeconds));
            DrawDropdownRow(ref y, "自杀兜底", DropdownId.SuicideFallback, SuicideFallbackNames, FindNearest(SuicideFallbackValues, SurvivalBotSettings.SuicideFallbackSeconds));
            DrawDropdownRow(ref y, "GM 停机", DropdownId.GmStopRounds, GmStopRoundNames, SurvivalBotSettings.GmStopRounds - 1);

            DrawNavigationPanel(new Rect(x, navigationTop, width, navigationHeight), compactNavigation);

            Rect status = new Rect(x, _window.yMax - 53f, width, 45f);
            DrawPanel(status, _panelInnerTexture, _borderTexture);
            GUI.Label(new Rect(status.x + 7f, status.y + 1f, status.width - 14f, 21f),
                ClipToWidth("网络  " + NetworkRouteManager.StatusText, _secondaryLabelStyle, status.width - 14f),
                _secondaryLabelStyle);
            GUI.Label(new Rect(status.x + 7f, status.y + 20f, status.width - 14f, 23f),
                ClipToWidth("状态  " + SurvivalBotManager.StatusText, _secondaryLabelStyle, status.width - 14f),
                _secondaryLabelStyle);

            GUI.enabled = oldEnabled;
            DrawDropdownOverlay();
        }

        private static void DrawNavigationPanel(Rect panel, bool compact)
        {
            RuntimeRainNavSnapshot snapshot = RuntimeRainNavMesh.GetStatusSnapshot();
            DrawPanel(panel, _panelInnerTexture, _borderTexture);

            float textX = panel.x + 7f;
            float textWidth = panel.width - 14f;
            string header = "导航  " + NavigationStageName(snapshot) + "  " +
                (snapshot.Progress01 * 100f).ToString("0.0") + "%  |  " +
                (string.IsNullOrEmpty(snapshot.MapName) ? "-" :
                    MapBakeSceneLoader.DisplayNameForRuntimeMap(snapshot.MapName)) + "  #" + snapshot.Generation;
            GUI.Label(new Rect(textX, panel.y + 1f, textWidth, 18f),
                ClipToWidth(header, _secondaryLabelStyle, textWidth), _secondaryLabelStyle);

            Rect progressTrack = new Rect(textX, panel.y + 20f, textWidth, 5f);
            GUI.DrawTexture(progressTrack, _borderTexture);
            float fillWidth = Mathf.Clamp(progressTrack.width * snapshot.Progress01, 0f, progressTrack.width);
            if (fillWidth > 0f)
                GUI.DrawTexture(new Rect(progressTrack.x, progressTrack.y, fillWidth, progressTrack.height), _accentTexture);

            string cacheLine = "缓存  " + CacheStateName(snapshot) + "  |  " +
                FormatBytes(snapshot.CacheBytes) + "  |  " +
                MapBakeSceneLoader.DisplayNameForRuntimeMap(snapshot.MapName) +
                "  |  内存 " + snapshot.CacheCount;
            if (compact)
            {
                GUI.Label(new Rect(textX, panel.y + 28f, textWidth, 20f),
                    ClipToWidth(cacheLine, _secondaryLabelStyle, textWidth), _secondaryLabelStyle);
                return;
            }

            string timeLimit = snapshot.TimeoutSeconds <= 0f
                ? snapshot.ElapsedSeconds.ToString("0.0") + " 秒 / 不限时"
                : snapshot.ElapsedSeconds.ToString("0.0") + " / " +
                    snapshot.TimeoutSeconds.ToString("0") + " 秒";
            string buildLine = "构建  碰撞体 " + snapshot.ColliderCount + "  |  节点 " + snapshot.GraphSize +
                "  |  " + timeLimit;
            string boundsLine = "参数  范围 " + FormatBounds(snapshot.BoundsSize) + "  |  网格 " +
                snapshot.CellSize.ToString("0.00") + " 米  |  Worker " + snapshot.WorkerCount +
                "  |  " + (snapshot.Profile == "max_detail" ? "极限精度" : "运行精度");
            bool directCombatTest = SurvivalBotManager.CombatTestEnabled || SurvivalBotManager.RoomTestEnabled;
            string provider = directCombatTest
                ? AutoBattleManager.LastPathProvider
                : SurvivalCombatAdapter.LastPathProvider;
            string intent = directCombatTest
                ? AutoBattleManager.State.ToString()
                : SurvivalCombatAdapter.LastPathIntent;
            string path = directCombatTest
                ? AutoBattleManager.LastPath
                : SurvivalCombatAdapter.LastPath;
            string pathLine = "路径  " + provider + "  |  导航点 " +
                NavigationPathVisualizer.VisiblePointCount + "  |  " + intent + "  |  " + path;
            string detailLine = "细节  " + (string.IsNullOrEmpty(snapshot.Detail) ? "-" : snapshot.Detail);

            DrawClippedLine(panel.y + 27f, buildLine, textX, textWidth);
            DrawClippedLine(panel.y + 44f, boundsLine, textX, textWidth);
            DrawClippedLine(panel.y + 61f, cacheLine, textX, textWidth);
            DrawClippedLine(panel.y + 78f, pathLine, textX, textWidth);
            DrawClippedLine(panel.y + 95f, detailLine, textX, textWidth);
        }

        private static void DrawClippedLine(float y, string text, float x, float width)
        {
            GUI.Label(new Rect(x, y, width, 17f),
                ClipToWidth(text, _secondaryLabelStyle, width), _secondaryLabelStyle);
        }

        private static void DrawDropdownRow(ref float y, string label, DropdownId id, string[] options, int selected)
        {
            selected = Clamp(selected, 0, options.Length - 1);
            float rowHeight = Mathf.Max(12f, _rowStride - 1f);
            Rect row = new Rect(_window.x + 8f, y, _window.width - 16f, rowHeight);
            GUI.Label(new Rect(row.x + 4f, row.y, 88f, row.height), label, _labelStyle);

            Rect button = new Rect(row.x + 94f, row.y + 1f, row.width - 98f, row.height - 2f);
            if (GUI.Button(button, options[selected] + "  ▼", _buttonStyle))
            {
                if (_openDropdown == id)
                {
                    _openDropdown = DropdownId.None;
                }
                else
                {
                    _openDropdown = id;
                    _dropdownAnchor = button;
                    _dropdownOptions = options;
                    _dropdownSelected = selected;
                    _dropdownFirstVisible = Mathf.Max(0, selected - 6);
                }
            }
            y += _rowStride;
        }

        private static void DrawMapBakeLaunchRow(ref float y, float x, float width)
        {
            string[] maps = MapBakeSceneLoader.AvailableMapDisplayNames;
            int selected = MapBakeSceneLoader.SelectedMapIndex();
            Rect row = new Rect(x, y, width, 27f);
            GUI.Label(new Rect(row.x + 4f, row.y, 58f, row.height), "建图地图", _labelStyle);
            Rect selector = new Rect(row.x + 64f, row.y + 1f, row.width - 174f, row.height - 2f);
            string selectedName = maps.Length == 0 ? "-" : maps[Mathf.Clamp(selected, 0, maps.Length - 1)];
            if (GUI.Button(selector, selectedName + "  ▼", _buttonStyle))
            {
                if (_openDropdown == DropdownId.MapBakeTarget)
                {
                    _openDropdown = DropdownId.None;
                }
                else
                {
                    _openDropdown = DropdownId.MapBakeTarget;
                    _dropdownAnchor = selector;
                    _dropdownOptions = maps;
                    _dropdownSelected = selected;
                    _dropdownFirstVisible = Mathf.Max(0, selected - 6);
                    _dropdownMapOptionsVersion = MapBakeSceneLoader.MapOptionsVersion;
                }
            }

            Rect launch = new Rect(selector.xMax + 4f, row.y + 1f, row.xMax - selector.xMax - 4f,
                row.height - 2f);
            if (GUI.Button(launch, "直接加载并建图", _buttonCenterStyle))
                SurvivalBotManager.RequestDirectMapBake("ui");
            y += 31f;
        }

        private static void DrawDropdownOverlay()
        {
            if (_openDropdown == DropdownId.None || _dropdownOptions == null || _dropdownOptions.Length == 0) return;
            if (_openDropdown == DropdownId.MapBakeTarget &&
                _dropdownMapOptionsVersion != MapBakeSceneLoader.MapOptionsVersion)
            {
                _openDropdown = DropdownId.None;
                return;
            }

            float rowHeight = 24f;
            int maxVisible = Mathf.Max(1, Mathf.FloorToInt((Screen.height - 24f) / rowHeight));
            int visibleCount = Mathf.Min(_dropdownOptions.Length, maxVisible);
            int maxFirst = Mathf.Max(0, _dropdownOptions.Length - visibleCount);
            _dropdownFirstVisible = Mathf.Clamp(_dropdownFirstVisible, 0, maxFirst);
            float height = visibleCount * rowHeight + 8f;
            float y = _dropdownAnchor.yMax + 2f;
            if (y + height > Screen.height - 4f) y = Mathf.Max(4f, _dropdownAnchor.y - height - 2f);
            float x = Mathf.Clamp(_dropdownAnchor.x, 4f, Mathf.Max(4f, Screen.width - _dropdownAnchor.width - 4f));
            Rect popup = new Rect(x, y, _dropdownAnchor.width, height);

            Event current = Event.current;
            if (current != null && current.type == EventType.KeyDown && current.keyCode == KeyCode.Escape)
            {
                _openDropdown = DropdownId.None;
                current.Use();
                return;
            }
            if (current != null && current.type == EventType.MouseDown && _dropdownAnchor.Contains(current.mousePosition))
            {
                _openDropdown = DropdownId.None;
                current.Use();
                return;
            }
            if (current != null && current.type == EventType.MouseDown &&
                !popup.Contains(current.mousePosition) && !_dropdownAnchor.Contains(current.mousePosition))
            {
                _openDropdown = DropdownId.None;
                current.Use();
                return;
            }
            if (current != null && current.type == EventType.ScrollWheel && popup.Contains(current.mousePosition) &&
                _dropdownOptions.Length > visibleCount)
            {
                int direction = current.delta.y > 0f ? 3 : -3;
                _dropdownFirstVisible = Mathf.Clamp(_dropdownFirstVisible + direction, 0, maxFirst);
                current.Use();
            }

            DrawPanel(popup, _popupTexture, _popupBorderTexture);
            for (int rowIndex = 0; rowIndex < visibleCount; rowIndex++)
            {
                int i = _dropdownFirstVisible + rowIndex;
                Rect row = new Rect(popup.x + 4f, popup.y + 4f + rowIndex * rowHeight,
                    popup.width - 8f, rowHeight - 1f);
                string text = i == _dropdownSelected ? "<color=#E71200>●</color>  " + _dropdownOptions[i] :
                    _dropdownOptions[i];
                if (!GUI.Button(row, text, _buttonStyle)) continue;
                ApplyDropdownSelection(_openDropdown, i);
                _openDropdown = DropdownId.None;
                break;
            }
        }

        private static void ApplyDropdownSelection(DropdownId id, int selected)
        {
            if (id == DropdownId.Tactics) SurvivalBotSettings.SetTacticsMode(selected);
            else if (id == DropdownId.Role) SurvivalBotSettings.SetRoleStrategyEnabled(selected == 0);
            else if (id == DropdownId.EnemyEsp) SurvivalBotSettings.SetEnemyEspEnabled(selected == 0);
            else if (id == DropdownId.Defense) SurvivalBotSettings.SetDefenseMode(selected);
            else if (id == DropdownId.MatchTimeout) SurvivalBotSettings.SetMatchTimeoutSeconds(MatchTimeoutValues[Clamp(selected, 0, MatchTimeoutValues.Length - 1)]);
            else if (id == DropdownId.ParticipantCapture) SurvivalBotSettings.SetParticipantCaptureSeconds(ParticipantCaptureValues[Clamp(selected, 0, ParticipantCaptureValues.Length - 1)]);
            else if (id == DropdownId.Separation) SurvivalBotSettings.SetDesiredSeparation(SeparationValues[Clamp(selected, 0, SeparationValues.Length - 1)]);
            else if (id == DropdownId.EmergencyDistance) SurvivalBotSettings.SetEmergencyDistance(EmergencyDistanceValues[Clamp(selected, 0, EmergencyDistanceValues.Length - 1)]);
            else if (id == DropdownId.SafePointRefresh) SurvivalBotSettings.SetSafePointRefreshSeconds(SafePointRefreshValues[Clamp(selected, 0, SafePointRefreshValues.Length - 1)]);
            else if (id == DropdownId.SuicideFallback) SurvivalBotSettings.SetSuicideFallbackSeconds(SuicideFallbackValues[Clamp(selected, 0, SuicideFallbackValues.Length - 1)]);
            else if (id == DropdownId.GmStopRounds) SurvivalBotSettings.SetGmStopRounds(Clamp(selected, 0, 2) + 1);
            else if (id == DropdownId.MapBakeTarget) MapBakeSceneLoader.SelectMap(selected);
        }

        private static void HandleVisibilityHotkey()
        {
            if (Time.frameCount == _deleteFrame || !Input.GetKeyDown(KeyCode.Delete)) return;
            _deleteFrame = Time.frameCount;
            Visible = !Visible;
            _openDropdown = DropdownId.None;
        }

        private static void HandleWindowDrag()
        {
            if (_openDropdown != DropdownId.None) return;
            Event current = Event.current;
            if (current == null) return;

            Rect title = new Rect(_window.x, _window.y, _window.width - 30f, 22f);
            if (current.type == EventType.MouseDown && current.button == 0 && title.Contains(current.mousePosition))
            {
                _dragging = true;
                _dragOffset = current.mousePosition - new Vector2(_window.x, _window.y);
                current.Use();
            }
            else if (current.type == EventType.MouseDrag && _dragging)
            {
                _window.x = current.mousePosition.x - _dragOffset.x;
                _window.y = current.mousePosition.y - _dragOffset.y;
                PlaceAndClampWindow();
                current.Use();
            }
            else if (current.type == EventType.MouseUp && _dragging)
            {
                _dragging = false;
                current.Use();
            }
        }

        private static void PlaceAndClampWindow()
        {
            _window.width = Mathf.Min(WindowWidth, Mathf.Max(300f, Screen.width - 16f));
            _window.height = Mathf.Min(WindowHeight, Mathf.Max(300f, Screen.height - 16f));
            if (!_placed)
            {
                _window.x = Mathf.Max(8f, Screen.width - _window.width - 12f);
                _window.y = 12f;
                _placed = true;
            }
            _window.x = Mathf.Clamp(_window.x, 4f, Mathf.Max(4f, Screen.width - _window.width - 4f));
            _window.y = Mathf.Clamp(_window.y, 4f, Mathf.Max(4f, Screen.height - _window.height - 4f));
        }

        private static void EnsureStyles()
        {
            if (_buttonStyle != null) return;

            _panelTexture = MakeTexture(new Color(0f, 0f, 0f, 0.78f));
            _panelInnerTexture = MakeTexture(new Color(0.06f, 0.06f, 0.06f, 0.92f));
            _popupTexture = MakeTexture(new Color(0.035f, 0.035f, 0.035f, 0.98f));
            _borderTexture = MakeTexture(new Color(1f, 1f, 1f, 0.16f));
            _popupBorderTexture = MakeTexture(new Color(1f, 1f, 1f, 0.25f));
            _titleTexture = MakeTexture(new Color(231f / 255f, 18f / 255f, 0f));
            _accentTexture = _titleTexture;

            Texture2D normal = MakeTexture(new Color(42f / 255f, 42f / 255f, 42f / 255f));
            Texture2D hover = MakeTexture(new Color(50f / 255f, 50f / 255f, 50f / 255f));
            Texture2D active = MakeTexture(new Color(60f / 255f, 60f / 255f, 60f / 255f));

            _buttonStyle = new GUIStyle(GUI.skin.button);
            _buttonStyle.alignment = TextAnchor.MiddleLeft;
            _buttonStyle.padding = new RectOffset(8, 8, 0, 0);
            _buttonStyle.fontSize = 12;
            _buttonStyle.richText = true;
            _buttonStyle.normal.background = normal;
            _buttonStyle.hover.background = hover;
            _buttonStyle.active.background = active;
            _buttonStyle.normal.textColor = Color.white;
            _buttonStyle.hover.textColor = Color.white;
            _buttonStyle.active.textColor = Color.white;
            _buttonStyle.focused.textColor = Color.white;

            _buttonCenterStyle = new GUIStyle(_buttonStyle);
            _buttonCenterStyle.alignment = TextAnchor.MiddleCenter;

            _titleStyle = new GUIStyle(GUI.skin.label);
            _titleStyle.alignment = TextAnchor.MiddleCenter;
            _titleStyle.fontSize = 13;
            _titleStyle.fontStyle = FontStyle.Bold;
            _titleStyle.normal.textColor = Color.white;

            _labelStyle = new GUIStyle(GUI.skin.label);
            _labelStyle.alignment = TextAnchor.MiddleLeft;
            _labelStyle.fontSize = 12;
            _labelStyle.normal.textColor = Color.white;

            _secondaryLabelStyle = new GUIStyle(_labelStyle);
            _secondaryLabelStyle.fontSize = 11;
            _secondaryLabelStyle.normal.textColor = new Color(0.78f, 0.78f, 0.78f);
        }

        private static void DrawPanel(Rect rect, Texture2D background, Texture2D border)
        {
            GUI.DrawTexture(rect, background);
            GUI.DrawTexture(new Rect(rect.x, rect.y, rect.width, 1f), border);
            GUI.DrawTexture(new Rect(rect.xMax - 1f, rect.y, 1f, rect.height), border);
            GUI.DrawTexture(new Rect(rect.x, rect.yMax - 1f, rect.width, 1f), border);
            GUI.DrawTexture(new Rect(rect.x, rect.y, 1f, rect.height), border);
        }

        private static Texture2D MakeTexture(Color color)
        {
            Texture2D texture = new Texture2D(1, 1, TextureFormat.RGBA32, false);
            texture.SetPixel(0, 0, color);
            texture.Apply();
            texture.hideFlags = HideFlags.HideAndDontSave;
            texture.wrapMode = TextureWrapMode.Clamp;
            texture.filterMode = FilterMode.Point;
            return texture;
        }

        private static int FindNearest(float[] values, float value)
        {
            int best = 0;
            float bestDistance = Mathf.Abs(values[0] - value);
            for (int i = 1; i < values.Length; i++)
            {
                float distance = Mathf.Abs(values[i] - value);
                if (distance >= bestDistance) continue;
                best = i;
                bestDistance = distance;
            }
            return best;
        }

        private static int Clamp(int value, int min, int max)
        {
            if (value < min) return min;
            if (value > max) return max;
            return value;
        }

        private static string ClipToWidth(string text, GUIStyle style, float maxWidth)
        {
            if (string.IsNullOrEmpty(text)) return "--";
            if (style.CalcSize(new GUIContent(text)).x <= maxWidth) return text;
            const string suffix = "...";
            int low = 0;
            int high = text.Length;
            while (low < high)
            {
                int middle = (low + high + 1) / 2;
                string candidate = text.Substring(0, middle) + suffix;
                if (style.CalcSize(new GUIContent(candidate)).x <= maxWidth) low = middle;
                else high = middle - 1;
            }
            return text.Substring(0, low) + suffix;
        }

        private static string NavigationStageName(RuntimeRainNavSnapshot snapshot)
        {
            if (snapshot.CacheSource == "native") return "原生资源";
            if (snapshot.State == RuntimeRainNavState.Building) return "生成中";
            if (snapshot.State == RuntimeRainNavState.Ready) return "已就绪";
            if (snapshot.State == RuntimeRainNavState.Failed) return "生成失败";
            if (snapshot.State == RuntimeRainNavState.WaitingScene)
            {
                if (snapshot.CacheSource == "memory" || snapshot.CacheSource == "disk") return "等待注册";
                if (snapshot.Detail == "waiting_activation") return "等待启用";
                if (snapshot.Detail.StartsWith("waiting_level")) return "等待场景";
                if (snapshot.Detail == "waiting_terrain_colliders") return "收集碰撞体";
                return "准备中";
            }
            return "未启用";
        }

        private static string CacheStateName(RuntimeRainNavSnapshot snapshot)
        {
            if (snapshot.CacheSource == "native") return "原生资源·无需生成";
            if (snapshot.CacheSource == "memory") return "内存命中";
            if (snapshot.CacheSource == "disk") return "磁盘命中";
            if (snapshot.CacheStatus == "saved") return "已写入磁盘";
            if (snapshot.CacheStatus == "saving") return "正在写入";
            if (snapshot.CacheStatus == "building") return "未命中·实时生成";
            if (snapshot.CacheStatus == "checking") return "检查中";
            if (snapshot.CacheStatus == "miss") return "无缓存";
            if (snapshot.CacheStatus == "content_changed") return "地图已更新·重建";
            if (snapshot.CacheStatus == "settings_changed") return "参数已更新·重建";
            if (snapshot.CacheStatus == "rain_changed") return "RAIN 已更新·重建";
            if (snapshot.CacheStatus != null &&
                (snapshot.CacheStatus.StartsWith("invalid_") ||
                 snapshot.CacheStatus.StartsWith("payload_") ||
                 snapshot.CacheStatus.StartsWith("deserialize_ex")))
                return "校验失败·重建";
            return string.IsNullOrEmpty(snapshot.CacheStatus) ? "-" : snapshot.CacheStatus;
        }

        private static string FormatBounds(Vector3 size)
        {
            if (size.sqrMagnitude <= 0.001f) return "-";
            return size.x.ToString("0") + " x " + size.y.ToString("0") + " x " +
                size.z.ToString("0") + " 米";
        }

        private static string FormatBytes(long bytes)
        {
            if (bytes <= 0L) return "-";
            if (bytes >= 1024L * 1024L) return (bytes / (1024f * 1024f)).ToString("0.0") + " MB";
            if (bytes >= 1024L) return (bytes / 1024f).ToString("0.0") + " KB";
            return bytes + " B";
        }

        private static string PhaseName(SurvivalBotPhase phase)
        {
            if (phase == SurvivalBotPhase.Lobby) return "大厅";
            if (phase == SurvivalBotPhase.Matching) return "匹配";
            if (phase == SurvivalBotPhase.CaptureParticipants) return "人数锁定";
            if (phase == SurvivalBotPhase.Hide) return "躲避";
            if (phase == SurvivalBotPhase.Emergency) return "近敌反击";
            if (phase == SurvivalBotPhase.Attack) return "攻击";
            if (phase == SurvivalBotPhase.Suicide) return "结束对局";
            if (phase == SurvivalBotPhase.Balance) return "结算";
            if (phase == SurvivalBotPhase.GmExit) return "GM 退出";
            if (phase == SurvivalBotPhase.CombatTest) return "战斗测试";
            if (phase == SurvivalBotPhase.RoomTest) return "开房测试";
            if (phase == SurvivalBotPhase.MapBake) return "地图建图";
            return "已停止";
        }
    }
}
