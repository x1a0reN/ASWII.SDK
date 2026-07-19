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
            GmStopRounds
        }

        private const float WindowWidth = 388f;
        private const float WindowHeight = 402f;
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
            string currentRole = SurvivalBotManager.CombatTestEnabled
                ? AutoBattleManager.CurrentRole
                : SurvivalCombatAdapter.CurrentRole;
            GUI.Label(new Rect(summary.x + 7f, summary.y, summary.width * 0.62f, summary.height),
                "阶段  " + phase + "  |  职业  " + currentRole, _secondaryLabelStyle);
            GUI.Label(new Rect(summary.x + summary.width * 0.62f, summary.y, summary.width * 0.38f - 7f, summary.height),
                "初始/存活  " + players, _secondaryLabelStyle);
            y += 29f;

            const float buttonGap = 6f;
            float modeButtonWidth = (width - buttonGap) * 0.5f;
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
            y += 33f;

            float statusTop = _window.yMax - 53f;
            _rowStride = Mathf.Clamp((statusTop - y - 2f) / 11f, 17f, RowHeight + 1f);

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

        private static void DrawDropdownRow(ref float y, string label, DropdownId id, string[] options, int selected)
        {
            selected = Clamp(selected, 0, options.Length - 1);
            float rowHeight = Mathf.Max(17f, _rowStride - 1f);
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
                }
            }
            y += _rowStride;
        }

        private static void DrawDropdownOverlay()
        {
            if (_openDropdown == DropdownId.None || _dropdownOptions == null || _dropdownOptions.Length == 0) return;

            float rowHeight = 24f;
            float height = _dropdownOptions.Length * rowHeight + 8f;
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

            DrawPanel(popup, _popupTexture, _popupBorderTexture);
            for (int i = 0; i < _dropdownOptions.Length; i++)
            {
                Rect row = new Rect(popup.x + 4f, popup.y + 4f + i * rowHeight, popup.width - 8f, rowHeight - 1f);
                string text = i == _dropdownSelected ? "<color=#E71200>●</color>  " + _dropdownOptions[i] : _dropdownOptions[i];
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
            return "已停止";
        }
    }
}
