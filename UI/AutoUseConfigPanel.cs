using ASWDEBUG.Cheats.AutoUse;
using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace ASWDEBUG.UI
{
    public static class AutoUseConfigPanel
    {
        public static bool Visible;

        private static int _selected;
        private static Vector2 _ruleScroll;
        private static Vector2 _dropdownScroll;
        private static string _testResult = string.Empty;
        private static string _openDropdownId = string.Empty;
        private static bool _showHelp;
        private static int _textInputSeq;
        private static GUIStyle _ruleRowStyle;
        private static GUIStyle _rowLabelStyle;

        private static readonly int[] PercentValues = new int[] { 5, 10, 15, 20, 25, 30, 35, 40, 45, 50, 60, 70, 80, 90, 100 };
        private static readonly int[] DistanceValues = new int[] { 3, 5, 8, 10, 15, 20, 30, 50, 80, 100 };
        private static readonly int[] CountValues = new int[] { 1, 2, 3, 4, 5, 6, 7, 8, 10, 12, 16, 20 };
        private static readonly int[] CooldownValues = new int[] { 300, 500, 800, 1000, 1200, 1500, 2000, 3000, 5000, 8000, 10000 };
        private static readonly AutoUseActionKind[] VisibleActionKinds = new AutoUseActionKind[]
        {
            AutoUseActionKind.UseSlot,
            AutoUseActionKind.UseFirstMatching,
            AutoUseActionKind.Reload,
            AutoUseActionKind.ChangeWeapon,
            AutoUseActionKind.SpecialAction,
            AutoUseActionKind.PickUpDropItem,
            AutoUseActionKind.UseBestHealItem,
            AutoUseActionKind.UseReadySkill,
            AutoUseActionKind.UseReadyItem,
            AutoUseActionKind.UseReviveItem
        };

        public static void Display()
        {
            if (!Visible) return;

            AutoUseManager.EnsureLoaded();
            ClampSelected();
            _textInputSeq = 0;
            EnableImeForPanel();

            float width = Mathf.Min(620f, Mathf.Max(500f, Screen.width - 20f));
            float height = Mathf.Min(760f, Mathf.Max(520f, Screen.height - 20f));
            float x = Mathf.Min(675f, Mathf.Max(10f, Screen.width - width - 10f));
            float y = 10f;

            UIHelper.DrawPanel(
                new Rect(x, y, width, height),
                new Color(7f / 255f, 11f / 255f, 15f / 255f, 0.985f),
                new Color(42f / 255f, 59f / 255f, 68f / 255f, 0.92f),
                1f);
            UIHelper.Begin("自动使用配置", x, y, width, height, 6f, 21f, 3f);
            UIHelper.LabelAuto("状态: " + AutoUseManager.LastStatus);

            DrawTopButtons();
            DrawRuleList();
            DrawSelectedRuleEditor();

            if (!string.IsNullOrEmpty(_testResult))
                UIHelper.LabelAuto("当前规则测试: " + _testResult, 12);

            if (UIHelper.Button(_showHelp ? "隐藏变量说明" : "显示变量说明"))
                _showHelp = !_showHelp;
            if (_showHelp)
                UIHelper.LabelAuto(AutoUseManager.VariableHelp, 11);
        }

        private static void DrawTopButtons()
        {
            Rect r = UIHelper.NextRectFlexible(24f);
            float gap = 4f;
            float bw = (r.width - gap * 4f) / 5f;
            if (SmallButton(new Rect(r.x, r.y, bw, r.height), "新增"))
            {
                AutoUseRule rule = AutoUseManager.AddDefaultRule();
                if (rule != null) _selected = AutoUseManager.Rules.Count - 1;
            }
            if (SmallButton(new Rect(r.x + (bw + gap), r.y, bw, r.height), "复制"))
            {
                if (_selected >= 0 && _selected < AutoUseManager.Rules.Count && AutoUseManager.Rules.Count < 32)
                {
                    AutoUseManager.Rules.Insert(_selected + 1, AutoUseManager.Rules[_selected].Clone());
                    _selected++;
                }
            }
            if (SmallButton(new Rect(r.x + (bw + gap) * 2f, r.y, bw, r.height), "上移"))
            {
                AutoUseManager.MoveRule(_selected, -1);
                if (_selected > 0) _selected--;
            }
            if (SmallButton(new Rect(r.x + (bw + gap) * 3f, r.y, bw, r.height), "下移"))
            {
                AutoUseManager.MoveRule(_selected, 1);
                if (_selected < AutoUseManager.Rules.Count - 1) _selected++;
            }
            if (SmallButton(new Rect(r.x + (bw + gap) * 4f, r.y, bw, r.height), "移除"))
            {
                AutoUseManager.RemoveRuleAt(_selected);
                ClampSelected();
            }

            Rect r2 = UIHelper.NextRectFlexible(24f);
            float bw2 = (r2.width - gap * 3f) / 4f;
            if (SmallButton(new Rect(r2.x, r2.y, bw2, r2.height), "保存配置"))
                AutoUseManager.Save();
            if (SmallButton(new Rect(r2.x + bw2 + gap, r2.y, bw2, r2.height), "重新加载"))
            {
                AutoUseManager.Load();
                ClampSelected();
            }
            if (SmallButton(new Rect(r2.x + (bw2 + gap) * 2f, r2.y, bw2, r2.height), "测试当前规则"))
                TestSelected();
            if (SmallButton(new Rect(r2.x + (bw2 + gap) * 3f, r2.y, bw2, r2.height), "关闭面板"))
                Visible = false;
        }

        private static void DrawRuleList()
        {
            _selected = UIHelper.ListBoxCustom<AutoUseRule>(
                ref _ruleScroll,
                AutoUseManager.Rules,
                135f,
                _selected,
                24f,
                DrawRuleRow,
                null,
                null);
            ClampSelected();
        }

        private static void DrawRuleRow(Rect row, int index, AutoUseRule rule, bool selected)
        {
            if (rule == null) return;
            EnsureEditorStyles();
            string state = rule.Enabled ? "<color=#37CFC2>ON</color>" : "<color=#5B7079>OFF</color>";
            string text = state + "  " + rule.Name + "  <color=#94A6AE>" + AutoUseManager.GetActionName(rule.ActionKind) + "</color>";
            GUI.Label(
                new Rect(row.x + 8f, row.y, row.width - 16f, row.height),
                text,
                _ruleRowStyle);
        }

        private static void DrawSelectedRuleEditor()
        {
            if (_selected < 0 || _selected >= AutoUseManager.Rules.Count)
            {
                UIHelper.LabelAuto("当前没有规则。");
                return;
            }

            AutoUseRule rule = AutoUseManager.Rules[_selected];
            if (rule == null) return;
            if (rule.ActionKind == AutoUseActionKind.RequestRevive)
                rule.ActionKind = AutoUseActionKind.UseReviveItem;

            UIHelper.LabelAuto("编辑规则 #" + (_selected + 1), 13);
            UIHelper.Button("启用规则", rule.Enabled, delegate { rule.Enabled = !rule.Enabled; });
            UIHelper.Button("高级表达式模式", rule.AdvancedMode, delegate { rule.AdvancedMode = !rule.AdvancedMode; });
            UIHelper.Button("仅对局内触发", rule.OnlyInGame, delegate { rule.OnlyInGame = !rule.OnlyInGame; });
            UIHelper.Button("仅存活时触发", rule.OnlyAlive, delegate { rule.OnlyAlive = !rule.OnlyAlive; });

            rule.Name = TextFieldRow("名称", rule.Name);

            if (rule.AdvancedMode)
            {
                rule.Expression = TextAreaRow("高级表达式", rule.Expression, 46f);
            }
            else
            {
                UIHelper.LabelAuto("当 " + AutoUseManager.GetConditionDescription(rule) + " 时，执行 " + AutoUseManager.GetActionDescription(rule), 12);
                rule.ConditionKind = ConditionKindRow("当", rule.ConditionKind);
                rule.ConditionValue = ConditionValueRow(rule.ConditionKind, rule.ConditionValue);
                rule.Expression = AutoUseManager.GetRuleExpression(rule);
            }

            rule.ActionKind = ActionKindRow("执行", rule.ActionKind);
            DrawActionOptions(rule);
            rule.CooldownMs = CooldownRow("触发间隔", rule.CooldownMs);

            UIHelper.LabelAuto("实际功能: 当 " + AutoUseManager.GetConditionDescription(rule) + " 时，" + AutoUseManager.GetActionDescription(rule), 11);
            UIHelper.LabelAuto("实际表达式: " + AutoUseManager.GetRuleExpression(rule), 11);

            if (!string.IsNullOrEmpty(rule.LastError))
                UIHelper.LabelAuto("上次错误: " + rule.LastError, 11);
            if (!string.IsNullOrEmpty(rule.LastResult))
                UIHelper.LabelAuto("上次动作: " + rule.LastResult, 11);
        }

        private static void TestSelected()
        {
            _testResult = string.Empty;
            if (_selected < 0 || _selected >= AutoUseManager.Rules.Count) return;
            string message;
            AutoUseManager.TestExpression(AutoUseManager.Rules[_selected], out message);
            _testResult = message;
        }

        private static string TextFieldRow(string label, string value)
        {
            Rect r = UIHelper.NextRectFlexible(24f);
            DrawRowLabel(r, label);
            Rect field = new Rect(r.x + 130f, r.y + 1f, r.width - 130f, r.height - 2f);
            string controlName = NextTextControlName(label);
            GUI.SetNextControlName(controlName);
            string next = GUI.TextField(field, value ?? string.Empty, UIHelper.TextFieldStyle);
            UpdateImeCursor(controlName, field);
            return next;
        }

        private static string TextAreaRow(string label, string value, float areaHeight)
        {
            Rect r = UIHelper.NextRectFlexible(areaHeight);
            DrawRowLabel(new Rect(r.x, r.y, r.width, 24f), label);
            Rect field = new Rect(r.x + 130f, r.y + 1f, r.width - 130f, r.height - 2f);
            string controlName = NextTextControlName(label);
            GUI.SetNextControlName(controlName);
            string next = GUI.TextArea(field, value ?? string.Empty, UIHelper.TextFieldStyle);
            UpdateImeCursor(controlName, field);
            return next;
        }

        private static int IntFieldRow(string label, int value)
        {
            string text = TextFieldRow(label, value.ToString(CultureInfo.InvariantCulture));
            int parsed;
            if (int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed))
                return parsed;
            return value;
        }

        private static string NextTextControlName(string label)
        {
            string safe = string.IsNullOrEmpty(label) ? "field" : label;
            return "AutoUseText_" + _selected + "_" + (_textInputSeq++).ToString(CultureInfo.InvariantCulture) + "_" + safe;
        }

        private static void EnableImeForPanel()
        {
            try
            {
                Input.imeCompositionMode = IMECompositionMode.On;
            }
            catch
            {
            }
        }

        private static void UpdateImeCursor(string controlName, Rect field)
        {
            try
            {
                if (GUI.GetNameOfFocusedControl() != controlName) return;
                Input.imeCompositionMode = IMECompositionMode.On;
                Input.compositionCursorPos = new Vector2(field.x + 6f, field.y + field.height - 2f);
            }
            catch
            {
            }
        }

        private static AutoUseConditionKind ConditionKindRow(string label, AutoUseConditionKind value)
        {
            Array values = Enum.GetValues(typeof(AutoUseConditionKind));
            string[] names = new string[values.Length];
            int selected = 0;
            for (int i = 0; i < values.Length; i++)
            {
                AutoUseConditionKind k = (AutoUseConditionKind)values.GetValue(i);
                names[i] = AutoUseManager.GetConditionName(k);
                if (k == value) selected = i;
            }
            selected = DropdownRow(label, names, selected, "condition_kind", 10);
            return (AutoUseConditionKind)values.GetValue(Mathf.Clamp(selected, 0, values.Length - 1));
        }

        private static int ConditionValueRow(AutoUseConditionKind kind, int value)
        {
            switch (kind)
            {
                case AutoUseConditionKind.PlayerHpBelow:
                case AutoUseConditionKind.PlayerShieldBelow:
                case AutoUseConditionKind.AmmoBelow:
                case AutoUseConditionKind.SafeAmmoBelow:
                case AutoUseConditionKind.EnemyLowHpBelow:
                case AutoUseConditionKind.BossHpBelow:
                case AutoUseConditionKind.AimTargetHpBelow:
                case AutoUseConditionKind.BossTargetHpBelow:
                    return ValueDropdownRow("条件数值", value, PercentValues, "%", "condition_value_pct");
                case AutoUseConditionKind.EnemyNear:
                    return ValueDropdownRow("距离", value, DistanceValues, "米", "condition_value_dist");
                case AutoUseConditionKind.EnemyCountAtLeast:
                    return ValueDropdownRow("数量", value, CountValues, "个", "condition_value_count");
                case AutoUseConditionKind.SlotReady:
                    return SlotDropdownRow("槽位", value, "condition_value_slot");
                case AutoUseConditionKind.ItemSubtypeReady:
                    return ItemSubtypeDropdownRow("道具类型", value, "condition_value_item", false);
                default:
                    UIHelper.LabelAuto("条件参数: 当前条件无需设置。", 11);
                    return value;
            }
        }

        private static AutoUseActionKind ActionKindRow(string label, AutoUseActionKind value)
        {
            if (value == AutoUseActionKind.RequestRevive)
                value = AutoUseActionKind.UseReviveItem;

            string[] names = new string[VisibleActionKinds.Length];
            int selected = 0;
            for (int i = 0; i < VisibleActionKinds.Length; i++)
            {
                AutoUseActionKind k = VisibleActionKinds[i];
                names[i] = AutoUseManager.GetActionName(k);
                if (k == value) selected = i;
            }
            selected = DropdownRow(label, names, selected, "action_kind", 10);
            return VisibleActionKinds[Mathf.Clamp(selected, 0, VisibleActionKinds.Length - 1)];
        }

        private static void DrawActionOptions(AutoUseRule rule)
        {
            switch (rule.ActionKind)
            {
                case AutoUseActionKind.UseSlot:
                    rule.Slot = SlotDropdownRow("使用槽位", rule.Slot, "action_slot");
                    break;
                case AutoUseActionKind.ChangeWeapon:
                    rule.Slot = SlotDropdownRow("武器槽位", rule.Slot, "action_weapon_slot");
                    break;
                case AutoUseActionKind.SpecialAction:
                    rule.Slot = SpecialActionDropdownRow("特殊动作", rule.Slot);
                    break;
                case AutoUseActionKind.PickUpDropItem:
                    rule.Slot = DropItemDropdownRow("掉落物", rule.Slot);
                    break;
                case AutoUseActionKind.UseFirstMatching:
                    rule.TypeFilter = TypeFilterDropdownRow("对象类型", rule.TypeFilter);
                    if (rule.TypeFilter == 1)
                        rule.SubTypeFilter = SkillSubtypeDropdownRow("技能", rule.SubTypeFilter, "match_skill_subtype");
                    else if (rule.TypeFilter == 3)
                        rule.SubTypeFilter = ItemSubtypeDropdownRow("道具类型", rule.SubTypeFilter, "match_item_subtype", true);
                    else
                    {
                        rule.SubTypeFilter = -1;
                        UIHelper.LabelAuto("对象类型不限: 会扫描全部可用技能/道具，需要更精确时先选择“技能”或“道具”。", 11);
                    }
                    rule.NameContains = TextFieldRow("名称包含", rule.NameContains);
                    break;
                case AutoUseActionKind.UseReadySkill:
                    rule.Slot = SkillSlotDropdownRow("技能选择", rule.Slot);
                    rule.SubTypeFilter = -1;
                    UIHelper.LabelAuto(rule.Slot > 0 ? "条件成立就尝试使用所选技能。" : "条件成立就使用第一个可执行技能。", 11);
                    break;
                case AutoUseActionKind.UseReadyItem:
                    rule.SubTypeFilter = ItemSubtypeDropdownRow("道具类型", rule.SubTypeFilter, "item_subtype", true);
                    break;
                case AutoUseActionKind.UseBestHealItem:
                    UIHelper.LabelAuto("动作参数: 自动从急救包、龙虾、火腿、饼干、绷带等治疗道具里选择可用项。", 11);
                    break;
                case AutoUseActionKind.UseReviveItem:
                    UIHelper.LabelAuto("动作参数: 自动选择可用复活道具。", 11);
                    break;
                case AutoUseActionKind.Reload:
                    UIHelper.LabelAuto("动作参数: 当前武器是枪械、未满弹、未换弹时才会执行。", 11);
                    break;
            }
        }

        private static int CooldownRow(string label, int value)
        {
            return ValueDropdownRow(label, value, CooldownValues, "ms", "cooldown");
        }

        private static int TypeFilterDropdownRow(string label, int value)
        {
            string[] names = new string[] { "不限（技能/道具都扫描）", "技能", "道具" };
            int[] values = new int[] { -1, 1, 3 };
            int selected = FindExactValueIndex(values, value);
            selected = DropdownRow(label, names, selected, "type_filter", names.Length);
            return values[Mathf.Clamp(selected, 0, values.Length - 1)];
        }

        private static int SkillSlotDropdownRow(string label, int value)
        {
            List<int> values = new List<int>();
            List<string> names = new List<string>();
            values.Add(0);
            names.Add("自动识别当前可用技能");

            ObjectBaseInfo[] slots = GetPlayerSlots();
            if (slots != null)
            {
                for (int i = 0; i < slots.Length; i++)
                {
                    ObjectBaseInfo info = slots[i];
                    if (!(info is SkillInfo)) continue;
                    int slot = info.slot > 0 ? info.slot : i + 1;
                    values.Add(slot);
                    names.Add(GetObjectDisplayName(info) + "（槽位 " + slot + "，" + (IsReadyForUi(info) ? "可用" : "冷却中") + "）");
                }
            }

            if (names.Count == 1)
                names[0] = "自动识别当前可用技能（当前未读取到技能栏）";

            int[] valueArray = values.ToArray();
            int selected = FindExactValueIndex(valueArray, value);
            selected = DropdownRow(label, names.ToArray(), selected, "skill_slot", Mathf.Min(10, names.Count));
            return valueArray[Mathf.Clamp(selected, 0, valueArray.Length - 1)];
        }

        private static int SkillSubtypeDropdownRow(string label, int value, string id)
        {
            List<int> values = new List<int>();
            List<string> names = new List<string>();
            values.Add(-1);
            names.Add("不限（任意当前技能）");

            ObjectBaseInfo[] slots = GetPlayerSlots();
            if (slots != null)
            {
                for (int i = 0; i < slots.Length; i++)
                {
                    ObjectBaseInfo info = slots[i];
                    if (!(info is SkillInfo)) continue;
                    int subType = info.sub_type;
                    if (values.Contains(subType)) continue;
                    values.Add(subType);
                    names.Add(GetObjectDisplayName(info) + "（同类技能）");
                }
            }

            if (value >= 0 && !values.Contains(value))
            {
                values.Add(value);
                names.Add("已保存的技能类型 " + value + "（当前未识别）");
            }

            int[] valueArray = values.ToArray();
            int selected = FindExactValueIndex(valueArray, value);
            selected = DropdownRow(label, names.ToArray(), selected, id, Mathf.Min(10, names.Count));
            return valueArray[Mathf.Clamp(selected, 0, valueArray.Length - 1)];
        }

        private static int DropItemDropdownRow(string label, int value)
        {
            List<int> values = new List<int>();
            List<string> names = new List<string>();
            values.Add(-1);
            names.Add("最近掉落物（自动识别）");

            Level level = GetCurrentLevel();
            Character player = GetCurrentPlayer(level);
            if (level != null && level.drop_item_set != null)
            {
                for (int i = 0; i < level.drop_item_set.Count; i++)
                {
                    DropItem item = level.drop_item_set[i];
                    if (item == null || item.isDestroy) continue;
                    int id = item.GetID();
                    if (values.Contains(id)) continue;

                    string distance = string.Empty;
                    try
                    {
                        if (player != null && player.transform != null)
                            distance = "，距离 " + Mathf.RoundToInt(Vector3.Distance(player.transform.position, item.GetPosition())) + " 米";
                    }
                    catch
                    {
                    }

                    values.Add(id);
                    names.Add(GetDropTypeName(item.GetDropType()) + "（ID " + id + distance + "）");
                }
            }

            int[] valueArray = values.ToArray();
            int selected = FindExactValueIndex(valueArray, value);
            selected = DropdownRow(label, names.ToArray(), selected, "pickup_id", Mathf.Min(10, names.Count));
            return valueArray[Mathf.Clamp(selected, 0, valueArray.Length - 1)];
        }

        private static int GenericSubtypeDropdownRow(string label, int value, bool itemNames, string id)
        {
            if (itemNames) return ItemSubtypeDropdownRow(label, value, id, true);

            int[] values = new int[22];
            string[] names = new string[22];
            values[0] = -1;
            names[0] = "不限";
            for (int i = 1; i < values.Length; i++)
            {
                values[i] = i - 1;
                names[i] = "子类型 " + (i - 1);
            }
            int selected = FindExactValueIndex(values, value);
            selected = DropdownRow(label, names, selected, id, 10);
            return values[Mathf.Clamp(selected, 0, values.Length - 1)];
        }

        private static int ItemSubtypeDropdownRow(string label, int value, string id, bool includeAny)
        {
            string[] itemNames = new string[]
            {
                "绷带", "高级绷带", "强心针", "血清", "急救包", "饼干",
                "火腿", "龙虾", "侦测", "月饼", "复活", "全体复活"
            };

            int extra = includeAny ? 1 : 0;
            int[] values = new int[itemNames.Length + extra];
            string[] names = new string[itemNames.Length + extra];
            int offset = 0;
            if (includeAny)
            {
                values[0] = -1;
                names[0] = "不限";
                offset = 1;
            }
            for (int i = 0; i < itemNames.Length; i++)
            {
                values[i + offset] = i;
                names[i + offset] = itemNames[i];
            }

            int selected = FindExactValueIndex(values, value);
            selected = DropdownRow(label, names, selected, id, 10);
            return values[Mathf.Clamp(selected, 0, values.Length - 1)];
        }

        private static int SpecialActionDropdownRow(string label, int value)
        {
            string[] names = new string[] { "拾取箱子", "埋雷", "拆雷" };
            int[] values = new int[] { 0, 1, 2 };
            int selected = FindExactValueIndex(values, value);
            selected = DropdownRow(label, names, selected, "special_action", names.Length);
            return values[Mathf.Clamp(selected, 0, values.Length - 1)];
        }

        private static int SlotDropdownRow(string label, int value, string id)
        {
            int[] values = BuildRange(1, 36);
            string[] names = new string[values.Length];
            for (int i = 0; i < values.Length; i++) names[i] = "槽位 " + values[i];
            int selected = FindValueIndex(values, value);
            selected = DropdownRow(label, names, selected, id, 10);
            return values[Mathf.Clamp(selected, 0, values.Length - 1)];
        }

        private static int ValueDropdownRow(string label, int value, int[] values, string suffix, string id)
        {
            string[] names = new string[values.Length];
            for (int i = 0; i < values.Length; i++) names[i] = values[i] + suffix;
            int selected = FindValueIndex(values, value);
            selected = DropdownRow(label, names, selected, id, Mathf.Min(10, values.Length));
            return values[Mathf.Clamp(selected, 0, values.Length - 1)];
        }

        private static int DropdownRow(string label, string[] options, int selected, string id, int maxVisibleRows)
        {
            if (options == null || options.Length == 0) return selected;
            selected = Mathf.Clamp(selected, 0, options.Length - 1);

            Rect r = UIHelper.NextRectFlexible(24f);
            DrawRowLabel(r, label);
            Rect button = new Rect(r.x + 130f, r.y + 1f, r.width - 130f, r.height - 2f);
            string caption = options[selected] + "  ▼";
            if (GUI.Button(button, caption, UIHelper.ButtonStyle))
            {
                _openDropdownId = _openDropdownId == id ? string.Empty : id;
                _dropdownScroll = Vector2.zero;
            }

            if (_openDropdownId != id) return selected;

            int rows = Mathf.Clamp(maxVisibleRows, 1, 12);
            float rowH = 22f;
            float h = Mathf.Min(options.Length, rows) * rowH + 8f;
            Rect area = UIHelper.NextRectFlexible(h);
            UIHelper.DrawPanel(area, new Color(0f, 0f, 0f, 0.88f), new Color(1f, 1f, 1f, 0.2f), 1f);

            Rect view = new Rect(area.x + 4f, area.y + 4f, area.width - 8f, area.height - 8f);
            Rect content = new Rect(0f, 0f, view.width - 16f, options.Length * rowH);
            _dropdownScroll = GUI.BeginScrollView(view, _dropdownScroll, content, false, true);
            for (int i = 0; i < options.Length; i++)
            {
                Rect row = new Rect(0f, i * rowH, content.width, rowH);
                string rowText = (i == selected ? "● " : "  ") + options[i];
                if (GUI.Button(row, rowText, UIHelper.ButtonStyle))
                {
                    selected = i;
                    _openDropdownId = string.Empty;
                }
            }
            GUI.EndScrollView();
            return selected;
        }

        private static Level GetCurrentLevel()
        {
            try
            {
                return ASSingleton<Level>.Instance;
            }
            catch
            {
                return null;
            }
        }

        private static Character GetCurrentPlayer(Level level)
        {
            try
            {
                return level != null ? level.GetPlayer() : null;
            }
            catch
            {
                return null;
            }
        }

        private static ObjectBaseInfo[] GetPlayerSlots()
        {
            try
            {
                Character player = GetCurrentPlayer(GetCurrentLevel());
                if (player == null || player.character_info == null || player.character_info.slots_info == null)
                    return null;
                return player.character_info.slots_info.object_info;
            }
            catch
            {
                return null;
            }
        }

        private static bool IsReadyForUi(ObjectBaseInfo info)
        {
            try
            {
                if (info == null) return false;
                ItemInfo item = info as ItemInfo;
                if (item != null)
                    return (short)item.count > 0 && (float)item.cooling <= 0f && item.CanAction();
                return info.cool_down_ready && info.CanAction();
            }
            catch
            {
                return false;
            }
        }

        private static string GetObjectDisplayName(ObjectBaseInfo info)
        {
            if (info == null) return "未知对象";
            string name = Localize(info.display_name);
            if (string.IsNullOrEmpty(name)) name = Localize(info.name);
            if (string.IsNullOrEmpty(name)) name = "槽位 " + info.slot;
            return name;
        }

        private static string Localize(string key)
        {
            if (string.IsNullOrEmpty(key)) return string.Empty;
            try
            {
                string text = TableManager.Instance != null ? TableManager.Instance.GetLabelText(key) : key;
                return string.IsNullOrEmpty(text) ? key : text;
            }
            catch
            {
                return key;
            }
        }

        private static string GetDropTypeName(uint type)
        {
            switch ((DropType)type)
            {
                case DropType.kDropItemTypeSnare: return "陷阱";
                case DropType.kDropItemTypeEnergy: return "能量";
                case DropType.kDropItemTypeTreasureChest: return "宝箱";
                case DropType.kDropItemTypeTreasureCoin: return "金币";
                case DropType.kDropItemTypeSprayerItem: return "喷射道具";
                case DropType.kDropItemTypeExplosive: return "雷包";
                default: return "掉落物";
            }
        }

        private static int FindExactValueIndex(int[] values, int value)
        {
            if (values == null || values.Length == 0) return 0;
            for (int i = 0; i < values.Length; i++)
            {
                if (values[i] == value) return i;
            }
            return 0;
        }

        private static int FindValueIndex(int[] values, int value)
        {
            if (values == null || values.Length == 0) return 0;
            int best = 0;
            int bestDelta = int.MaxValue;
            for (int i = 0; i < values.Length; i++)
            {
                int delta = Math.Abs(values[i] - value);
                if (delta < bestDelta)
                {
                    best = i;
                    bestDelta = delta;
                }
            }
            return best;
        }

        private static int[] BuildRange(int first, int last)
        {
            int count = Math.Max(0, last - first + 1);
            int[] result = new int[count];
            for (int i = 0; i < count; i++) result[i] = first + i;
            return result;
        }

        private static void DrawRowLabel(Rect r, string label)
        {
            EnsureEditorStyles();
            GUI.Label(
                new Rect(r.x + 4f, r.y, 124f, Mathf.Min(24f, r.height)),
                label,
                _rowLabelStyle);
        }

        private static void EnsureEditorStyles()
        {
            if (_ruleRowStyle != null) return;
            _ruleRowStyle = new GUIStyle(UIHelper.StringStyle ?? GUI.skin.label);
            _ruleRowStyle.alignment = TextAnchor.MiddleLeft;
            _ruleRowStyle.fontSize = 12;
            _ruleRowStyle.richText = true;
            _ruleRowStyle.normal.textColor = new Color(
                232f / 255f,
                241f / 255f,
                244f / 255f);

            _rowLabelStyle = new GUIStyle(_ruleRowStyle);
            _rowLabelStyle.richText = false;
            _rowLabelStyle.normal.textColor = new Color(
                148f / 255f,
                166f / 255f,
                174f / 255f);
        }

        private static bool SmallButton(Rect r, string text)
        {
            return GUI.Button(r, text, UIHelper.ButtonStyle);
        }

        private static void ClampSelected()
        {
            if (AutoUseManager.Rules.Count <= 0)
            {
                _selected = -1;
                return;
            }
            if (_selected < 0) _selected = 0;
            if (_selected >= AutoUseManager.Rules.Count) _selected = AutoUseManager.Rules.Count - 1;
        }
    }
}
