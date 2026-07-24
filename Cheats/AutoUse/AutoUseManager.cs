using ASWDEBUG.Logger;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;
using AutoBattleState = ASWDEBUG.Cheats.AutoBattle.AutoBattleState;

namespace ASWDEBUG.Cheats.AutoUse
{
    public enum AutoUseActionKind
    {
        UseSlot = 0,
        UseFirstMatching = 1,
        Reload = 2,
        ChangeWeapon = 3,
        SpecialAction = 4,
        PickUpDropItem = 5,
        UseBestHealItem = 6,
        UseReadySkill = 7,
        UseReadyItem = 8,
        UseReviveItem = 9,
        RequestRevive = 10
    }

    public enum AutoUseConditionKind
    {
        Always = 0,
        PlayerHpBelow = 1,
        PlayerShieldBelow = 2,
        AmmoBelow = 3,
        EnemyNear = 4,
        EnemyCountAtLeast = 5,
        EnemyLowHpBelow = 6,
        BossExists = 7,
        BossHpBelow = 8,
        AimTargetHpBelow = 9,
        BossTargetHpBelow = 10,
        PlayerDead = 11,
        SlotReady = 12,
        ItemSubtypeReady = 13,
        SafeAmmoBelow = 14
    }

    public class AutoUseRule
    {
        public bool Enabled;
        public string Name = "新规则";
        public string Expression = "hp_pct > 0 && hp_pct <= 45";
        public AutoUseActionKind ActionKind = AutoUseActionKind.UseFirstMatching;
        public int Slot = 1;
        public int TypeFilter = -1;
        public int SubTypeFilter = -1;
        public string NameContains = string.Empty;
        public int CooldownMs = 1200;
        public bool OnlyInGame = true;
        public bool OnlyAlive = true;
        public bool AdvancedMode;
        public AutoUseConditionKind ConditionKind = AutoUseConditionKind.PlayerHpBelow;
        public int ConditionValue = 45;

        internal long LastFireTicks;
        internal string LastError = string.Empty;
        internal string LastResult = string.Empty;

        public string Summary
        {
            get
            {
                return (Enabled ? "[ON] " : "[OFF] ") + Name + " -> " + AutoUseManager.GetActionName(ActionKind);
            }
        }

        public AutoUseRule Clone()
        {
            return new AutoUseRule
            {
                Enabled = Enabled,
                Name = Name + "_copy",
                Expression = Expression,
                ActionKind = ActionKind,
                Slot = Slot,
                TypeFilter = TypeFilter,
                SubTypeFilter = SubTypeFilter,
                NameContains = NameContains,
                CooldownMs = CooldownMs,
                OnlyInGame = OnlyInGame,
                OnlyAlive = OnlyAlive,
                AdvancedMode = AdvancedMode,
                ConditionKind = ConditionKind,
                ConditionValue = ConditionValue
            };
        }
    }

    public static class AutoUseManager
    {
        public static bool Enabled;
        public static readonly List<AutoUseRule> Rules = new List<AutoUseRule>(16);
        public static string LastStatus = "未加载";
        public static string LastConfigPath = string.Empty;

        private const long TickIntervalTicks = TimeSpan.TicksPerMillisecond * 100L;
        private const int MaxRules = 32;
        private static readonly List<Character> CharacterCache = new List<Character>(32);
        private static readonly List<BaseBoss> BossCache = new List<BaseBoss>(16);
        private static long _lastTickTicks;
        private static bool _loaded;

        public static string VariableHelp =
            "总开关关闭时不会执行任何规则；每条规则还可以单独启用/停用。\n" +
            "常用: hp/max_hp/hp_pct/shield/shield_pct/alive/dead/clip/clip_max/clip_pct/reloading/weapon_slot/can_fire/heal_action_ready/is_ground/is_crouch/speed/ping\n" +
            "地图/状态: in_game/in_channel/in_room/channel_state/game_state/game_type/map_id/team_type/level_state/special_pickup_active/special_lay_mines_active\n" +
            "战术: danger_score/combat_pressure/need_escape/need_speed/need_chase/traveling/auto_battle_state/enemy_los_count/enemy_aiming_count\n" +
            "敌人/队友: enemy_alive_count/enemy_low_hp_25_count/enemy_min_hp_pct/enemy_nearest_hp_pct/enemy_near_5/10/20/30/enemy_near_dist/ally_alive_count/ally_low_hp_50_count\n" +
            "BOSS: is_boss_mode/boss_alive_count/boss_hp_pct/boss_min_hp_pct/boss_total_hp/boss_near_10/boss_near_dist/boss_weak_count\n" +
            "槽位/物品: slot1_ready..slot36_ready, slot1_type, slot1_subtype, slot1_count, skill_ready_count, item_ready_count, item_first_aid_ready, item_lobster_ready\n" +
            "自瞄目标: aim_target_exists/aim_target_hp_pct/aim_target_dist/boss_target_exists/boss_target_hp_pct\n" +
            "常量: type_skill=1 type_item=3 item_bandage=0 item_first_aid=4 item_cookie=5 item_ham=6 item_lobster=7 item_revive=10";

        public static void Toggle()
        {
            Enabled = !Enabled;
            EnsureLoaded();
            FileLogger.Log("AUTO-USE", "enabled=" + Enabled);
        }

        public static void EnsureLoaded()
        {
            if (_loaded) return;
            _loaded = true;
            Load();
        }

        public static AutoUseRule AddDefaultRule()
        {
            EnsureLoaded();
            if (Rules.Count >= MaxRules)
            {
                LastStatus = "规则数量已达上限 " + MaxRules;
                return null;
            }

            AutoUseRule rule = new AutoUseRule();
            rule.Enabled = false;
            rule.Name = "低血自动吃药";
            rule.Expression = "hp_pct > 0 && hp_pct <= 45 && heal_item_ready_count > 0";
            rule.ActionKind = AutoUseActionKind.UseBestHealItem;
            rule.TypeFilter = 3;
            rule.SubTypeFilter = -1;
            rule.CooldownMs = 1500;
            rule.AdvancedMode = false;
            rule.ConditionKind = AutoUseConditionKind.PlayerHpBelow;
            rule.ConditionValue = 45;
            Rules.Add(rule);
            return rule;
        }

        public static string GetRuleExpression(AutoUseRule rule)
        {
            if (rule == null) return "false";
            if (rule.AdvancedMode) return string.IsNullOrEmpty(rule.Expression) ? "false" : rule.Expression;
            return BuildConditionExpression(rule.ConditionKind, rule.ConditionValue);
        }

        public static string GetConditionName(AutoUseConditionKind kind)
        {
            switch (kind)
            {
                case AutoUseConditionKind.Always: return "始终触发";
                case AutoUseConditionKind.PlayerHpBelow: return "我的血量低于";
                case AutoUseConditionKind.PlayerShieldBelow: return "我的护盾低于";
                case AutoUseConditionKind.AmmoBelow: return "当前子弹低于";
                case AutoUseConditionKind.SafeAmmoBelow: return "弹药低且附近安全";
                case AutoUseConditionKind.EnemyNear: return "附近有敌人";
                case AutoUseConditionKind.EnemyCountAtLeast: return "敌人数量至少";
                case AutoUseConditionKind.EnemyLowHpBelow: return "有敌人低血";
                case AutoUseConditionKind.BossExists: return "场上有BOSS";
                case AutoUseConditionKind.BossHpBelow: return "BOSS血量低于";
                case AutoUseConditionKind.AimTargetHpBelow: return "自瞄目标血量低于";
                case AutoUseConditionKind.BossTargetHpBelow: return "BOSS目标血量低于";
                case AutoUseConditionKind.PlayerDead: return "我已死亡";
                case AutoUseConditionKind.SlotReady: return "指定槽位可用";
                case AutoUseConditionKind.ItemSubtypeReady: return "指定道具可用";
                default: return kind.ToString();
            }
        }

        public static string GetConditionDescription(AutoUseRule rule)
        {
            if (rule == null) return "没有规则";
            if (rule.AdvancedMode) return "高级表达式成立";

            int v = NormalizeConditionValue(rule.ConditionKind, rule.ConditionValue);
            switch (rule.ConditionKind)
            {
                case AutoUseConditionKind.Always: return "始终成立";
                case AutoUseConditionKind.PlayerHpBelow: return "我的血量百分比 <= " + v + "%";
                case AutoUseConditionKind.PlayerShieldBelow: return "我的护盾百分比 <= " + v + "%";
                case AutoUseConditionKind.AmmoBelow: return "当前弹匣子弹百分比 <= " + v + "%";
                case AutoUseConditionKind.SafeAmmoBelow: return "当前弹匣子弹百分比 <= " + v + "%，且 10 米内没有敌人";
                case AutoUseConditionKind.EnemyNear: return "距离我 " + v + " 米内至少有一个敌人";
                case AutoUseConditionKind.EnemyCountAtLeast: return "存活敌人数量 >= " + v;
                case AutoUseConditionKind.EnemyLowHpBelow: return "至少一个敌人血量百分比 <= " + v + "%";
                case AutoUseConditionKind.BossExists: return "场上存在存活 BOSS";
                case AutoUseConditionKind.BossHpBelow: return "最低血量 BOSS 的血量百分比 <= " + v + "%";
                case AutoUseConditionKind.AimTargetHpBelow: return "当前自瞄目标血量百分比 <= " + v + "%";
                case AutoUseConditionKind.BossTargetHpBelow: return "当前 BOSS 自瞄目标血量百分比 <= " + v + "%";
                case AutoUseConditionKind.PlayerDead: return "当前角色处于死亡状态";
                case AutoUseConditionKind.SlotReady: return "槽位 " + v + " 已就绪并可使用";
                case AutoUseConditionKind.ItemSubtypeReady: return GetItemSubtypeName(v) + " 已就绪并可使用";
                default: return "未知条件";
            }
        }

        public static string GetActionDescription(AutoUseRule rule)
        {
            if (rule == null) return "没有动作";
            switch (rule.ActionKind)
            {
                case AutoUseActionKind.UseSlot: return "使用槽位 " + rule.Slot + DescribeCurrentSlotSuffix(rule.Slot);
                case AutoUseActionKind.UseFirstMatching: return "自动匹配并使用：" + DescribeObjectFilter(rule.TypeFilter, rule.SubTypeFilter, rule.NameContains);
                case AutoUseActionKind.Reload: return "给当前枪械换弹";
                case AutoUseActionKind.ChangeWeapon: return "切换到武器槽位 " + rule.Slot;
                case AutoUseActionKind.SpecialAction: return "发送特殊动作 " + rule.Slot + "（0拾取箱子/1埋雷/2拆雷）";
                case AutoUseActionKind.PickUpDropItem: return rule.Slot < 0 ? "自动拾取离我最近的掉落物" : "拾取掉落物 " + rule.Slot;
                case AutoUseActionKind.UseBestHealItem: return "按急救包/龙虾/火腿/饼干/绷带等优先级自动吃药";
                case AutoUseActionKind.UseReadySkill:
                    return rule.Slot > 0 ? ("使用槽位 " + rule.Slot + DescribeCurrentSlotSuffix(rule.Slot) + " 的技能") : "使用第一个符合筛选且当前可执行的技能";
                case AutoUseActionKind.UseReadyItem: return rule.SubTypeFilter >= 0 ? ("使用第一个可用的" + GetItemSubtypeName(rule.SubTypeFilter)) : "使用第一个可用道具";
                case AutoUseActionKind.UseReviveItem: return "使用可用复活类道具";
                case AutoUseActionKind.RequestRevive: return "向服务端请求免费复活";
                default: return rule.ActionKind.ToString();
            }
        }

        public static string GetItemSubtypeName(int subType)
        {
            switch (subType)
            {
                case 0: return "绷带";
                case 1: return "高级绷带";
                case 2: return "强心针";
                case 3: return "血清";
                case 4: return "急救包";
                case 5: return "饼干";
                case 6: return "火腿";
                case 7: return "龙虾";
                case 8: return "侦测道具";
                case 9: return "月饼";
                case 10: return "复活道具";
                case 11: return "全体复活道具";
                default: return "未知道具类型 " + subType;
            }
        }

        private static string DescribeObjectFilter(int typeFilter, int subTypeFilter, string nameContains)
        {
            string typeName = typeFilter == 1 ? "技能" : (typeFilter == 3 ? "道具" : "任意技能/道具");
            string subName = string.Empty;
            if (subTypeFilter >= 0)
                subName = typeFilter == 3 ? ("，类型为" + GetItemSubtypeName(subTypeFilter)) : ("，同类为" + GetSkillSubtypeName(subTypeFilter));
            string name = string.IsNullOrEmpty(nameContains) ? string.Empty : ("，名称包含“" + nameContains + "”");
            return typeName + subName + name;
        }

        private static string GetSkillSubtypeName(int subType)
        {
            try
            {
                Level level = ASSingleton<Level>.Instance;
                Character player = level != null ? level.GetPlayer() : null;
                ObjectBaseInfo[] slots = GetSlots(player);
                if (slots != null)
                {
                    for (int i = 0; i < slots.Length; i++)
                    {
                        ObjectBaseInfo info = slots[i];
                        if (info is SkillInfo && info.sub_type == (byte)subType)
                        {
                            string name = ResolveObjectDisplayName(info);
                            if (!string.IsNullOrEmpty(name)) return name;
                        }
                    }
                }
            }
            catch
            {
            }
            return "当前技能类型 " + subType;
        }

        private static string DescribeCurrentSlotSuffix(int slot)
        {
            try
            {
                Level level = ASSingleton<Level>.Instance;
                Character player = level != null ? level.GetPlayer() : null;
                ObjectBaseInfo info = GetSlotInfo(new AutoUseContext { Slots = GetSlots(player) }, slot);
                string name = ResolveObjectDisplayName(info);
                return string.IsNullOrEmpty(name) ? string.Empty : "（" + name + "）";
            }
            catch
            {
                return string.Empty;
            }
        }

        private static string ResolveObjectDisplayName(ObjectBaseInfo info)
        {
            if (info == null) return string.Empty;
            string key = !string.IsNullOrEmpty(info.display_name) ? info.display_name : info.name;
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

        public static int NormalizeConditionValue(AutoUseConditionKind kind, int value)
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
                    return Mathf.Clamp(value, 1, 100);
                case AutoUseConditionKind.EnemyNear:
                    return Mathf.Clamp(value, 1, 100);
                case AutoUseConditionKind.EnemyCountAtLeast:
                    return Mathf.Clamp(value, 1, 32);
                case AutoUseConditionKind.SlotReady:
                    return Mathf.Clamp(value, 1, 36);
                case AutoUseConditionKind.ItemSubtypeReady:
                    return Mathf.Clamp(value, 0, 11);
                default:
                    return value;
            }
        }

        private static string BuildConditionExpression(AutoUseConditionKind kind, int value)
        {
            int v = NormalizeConditionValue(kind, value);
            switch (kind)
            {
                case AutoUseConditionKind.Always:
                    return "true";
                case AutoUseConditionKind.PlayerHpBelow:
                    return "hp_pct > 0 && hp_pct <= " + v;
                case AutoUseConditionKind.PlayerShieldBelow:
                    return "max_shield > 0 && shield_pct <= " + v;
                case AutoUseConditionKind.AmmoBelow:
                    return "clip_max > 0 && clip_pct <= " + v;
                case AutoUseConditionKind.SafeAmmoBelow:
                    return "clip_max > 0 && clip_pct <= " + v + " && enemy_near_10 == 0";
                case AutoUseConditionKind.EnemyNear:
                    return "enemy_near_dist <= " + v;
                case AutoUseConditionKind.EnemyCountAtLeast:
                    return "enemy_alive_count >= " + v;
                case AutoUseConditionKind.EnemyLowHpBelow:
                    return "enemy_min_hp_pct > 0 && enemy_min_hp_pct <= " + v;
                case AutoUseConditionKind.BossExists:
                    return "boss_alive_count > 0";
                case AutoUseConditionKind.BossHpBelow:
                    return "boss_alive_count > 0 && boss_min_hp_pct <= " + v;
                case AutoUseConditionKind.AimTargetHpBelow:
                    return "aim_target_exists && aim_target_hp_pct <= " + v;
                case AutoUseConditionKind.BossTargetHpBelow:
                    return "boss_target_exists && boss_target_hp_pct <= " + v;
                case AutoUseConditionKind.PlayerDead:
                    return "dead";
                case AutoUseConditionKind.SlotReady:
                    return "slot" + v + "_ready";
                case AutoUseConditionKind.ItemSubtypeReady:
                    return "item_subtype_" + v + "_ready > 0";
                default:
                    return "false";
            }
        }

        public static void RemoveRuleAt(int index)
        {
            EnsureLoaded();
            if (index < 0 || index >= Rules.Count) return;
            Rules.RemoveAt(index);
            LastStatus = "已移除内存规则，保存后写入配置";
        }

        public static void MoveRule(int index, int delta)
        {
            EnsureLoaded();
            int target = index + delta;
            if (index < 0 || index >= Rules.Count || target < 0 || target >= Rules.Count) return;
            AutoUseRule temp = Rules[index];
            Rules[index] = Rules[target];
            Rules[target] = temp;
        }

        public static string GetActionName(AutoUseActionKind kind)
        {
            switch (kind)
            {
                case AutoUseActionKind.UseSlot: return "使用槽位";
                case AutoUseActionKind.UseFirstMatching: return "自动匹配";
                case AutoUseActionKind.Reload: return "自动换弹";
                case AutoUseActionKind.ChangeWeapon: return "切换武器";
                case AutoUseActionKind.SpecialAction: return "特殊动作";
                case AutoUseActionKind.PickUpDropItem: return "拾取掉落";
                case AutoUseActionKind.UseBestHealItem: return "治疗道具";
                case AutoUseActionKind.UseReadySkill: return "自动使用技能";
                case AutoUseActionKind.UseReadyItem: return "可用道具";
                case AutoUseActionKind.UseReviveItem: return "复活道具";
                case AutoUseActionKind.RequestRevive: return "请求复活";
                default: return kind.ToString();
            }
        }

        public static void Tick(Level level, Character player)
        {
            EnsureLoaded();
            if (!Enabled) return;

            long now = DateTime.UtcNow.Ticks;
            if (now - _lastTickTicks < TickIntervalTicks) return;
            _lastTickTicks = now;

            if (Rules.Count == 0) return;
            AutoUseContext context = BuildContext(level, player);

            for (int i = 0; i < Rules.Count; i++)
            {
                AutoUseRule rule = Rules[i];
                if (rule == null || !rule.Enabled) continue;
                if (rule.OnlyInGame && context.Get("in_game") < 0.5d) continue;
                if (rule.OnlyAlive && context.Get("alive") < 0.5d) continue;

                int cd = Math.Max(100, rule.CooldownMs);
                if (now - rule.LastFireTicks < TimeSpan.TicksPerMillisecond * (long)cd) continue;

                string error;
                bool matched = Evaluate(GetRuleExpression(rule), context.Variables, out error);
                if (!matched)
                {
                    rule.LastError = error;
                    continue;
                }

                string result;
                bool fired = Execute(rule, context, out result);
                rule.LastResult = result;
                if (fired)
                {
                    rule.LastFireTicks = now;
                    LastStatus = rule.Name + ": " + result;
                    FileLogger.Log("AUTO-USE", "rule=" + rule.Name + " action=" + GetActionName(rule.ActionKind) + " result=" + result);
                    return;
                }
            }
        }

        public static bool TestExpression(AutoUseRule rule, out string message)
        {
            EnsureLoaded();
            try
            {
                Level level = ASSingleton<Level>.Instance;
                Character player = level != null ? level.GetPlayer() : null;
                AutoUseContext context = BuildContext(level, player);
                string error;
                bool value = Evaluate(GetRuleExpression(rule), context.Variables, out error);
                if (!string.IsNullOrEmpty(error))
                    message = error;
                else if (value)
                    message = "当前规则成立；会尝试执行：" + GetActionDescription(rule);
                else
                    message = "当前规则不成立；条件还没满足。";
                return value;
            }
            catch (Exception ex)
            {
                message = ex.Message;
                return false;
            }
        }

        public static void Load()
        {
            Rules.Clear();
            LastConfigPath = GetConfigPath();

            try
            {
                if (File.Exists(LastConfigPath))
                {
                    string[] lines = File.ReadAllLines(LastConfigPath, Encoding.UTF8);
                    for (int i = 0; i < lines.Length && Rules.Count < MaxRules; i++)
                    {
                        AutoUseRule rule = Deserialize(lines[i]);
                        if (rule != null) Rules.Add(rule);
                    }
                }
            }
            catch (Exception ex)
            {
                LastStatus = "读取配置失败: " + ex.Message;
                FileLogger.Log("AUTO-USE", LastStatus);
            }

            if (Rules.Count == 0)
            {
                AddBuiltInExamples();
            }

            LastStatus = "已加载规则 " + Rules.Count;
        }

        public static void Save()
        {
            EnsureLoaded();
            LastConfigPath = GetConfigPath();
            try
            {
                string dir = Path.GetDirectoryName(LastConfigPath);
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);

                List<string> lines = new List<string>(Rules.Count);
                for (int i = 0; i < Rules.Count; i++)
                    lines.Add(Serialize(Rules[i]));

                File.WriteAllLines(LastConfigPath, lines.ToArray(), Encoding.UTF8);
                LastStatus = "已保存 " + Rules.Count + " 条规则";
                FileLogger.Log("AUTO-USE", LastStatus + " path=" + LastConfigPath);
            }
            catch (Exception ex)
            {
                LastStatus = "保存失败: " + ex.Message;
                FileLogger.Log("AUTO-USE", LastStatus);
            }
        }

        private static void AddBuiltInExamples()
        {
            Rules.Add(new AutoUseRule
            {
                Enabled = false,
                Name = "低血优先吃药",
                Expression = "hp_pct > 0 && hp_pct <= 45 && heal_item_ready_count > 0",
                ActionKind = AutoUseActionKind.UseBestHealItem,
                TypeFilter = 3,
                SubTypeFilter = -1,
                CooldownMs = 1500,
                AdvancedMode = false,
                ConditionKind = AutoUseConditionKind.PlayerHpBelow,
                ConditionValue = 45
            });
            Rules.Add(new AutoUseRule
            {
                Enabled = false,
                Name = "空弹自动换弹",
                Expression = "clip_max > 0 && clip_pct <= 10 && enemy_near_10 == 0",
                ActionKind = AutoUseActionKind.Reload,
                CooldownMs = 1200,
                AdvancedMode = false,
                ConditionKind = AutoUseConditionKind.SafeAmmoBelow,
                ConditionValue = 10
            });
            Rules.Add(new AutoUseRule
            {
                Enabled = false,
                Name = "近敌释放技能槽5",
                Expression = "enemy_near_10 > 0 && slot5_ready",
                ActionKind = AutoUseActionKind.UseSlot,
                Slot = 5,
                CooldownMs = 1800,
                AdvancedMode = false,
                ConditionKind = AutoUseConditionKind.EnemyNear,
                ConditionValue = 10
            });
            Rules.Add(new AutoUseRule
            {
                Enabled = false,
                Name = "BOSS低血技能槽6",
                Expression = "is_boss_mode && boss_alive_count > 0 && boss_hp_pct <= 35 && slot6_ready",
                ActionKind = AutoUseActionKind.UseSlot,
                Slot = 6,
                CooldownMs = 2200,
                AdvancedMode = false,
                ConditionKind = AutoUseConditionKind.BossHpBelow,
                ConditionValue = 35
            });
        }

        private static bool Execute(AutoUseRule rule, AutoUseContext context, out string result)
        {
            result = string.Empty;
            if (rule == null)
            {
                result = "rule=null";
                return false;
            }

            try
            {
                switch (rule.ActionKind)
                {
                    case AutoUseActionKind.UseSlot:
                        return TryUseSlot(context, rule.Slot, out result);
                    case AutoUseActionKind.UseFirstMatching:
                        return TryUseFirstMatching(context, rule, out result);
                    case AutoUseActionKind.Reload:
                        return TryReload(context, out result);
                    case AutoUseActionKind.ChangeWeapon:
                        return TryChangeWeapon(context, rule.Slot, out result);
                    case AutoUseActionKind.SpecialAction:
                        return TrySpecialAction(rule.Slot, out result);
                    case AutoUseActionKind.PickUpDropItem:
                        return TryPickUpDropItem(context, rule.Slot, out result);
                    case AutoUseActionKind.UseBestHealItem:
                        return TryUseBestHealItem(context, out result);
                    case AutoUseActionKind.UseReadySkill:
                        return TryUseReadySkill(context, rule, out result);
                    case AutoUseActionKind.UseReadyItem:
                        return TryUseTypedObject(context, rule, 3, out result);
                    case AutoUseActionKind.UseReviveItem:
                        return TryUseReviveItem(context, out result);
                    case AutoUseActionKind.RequestRevive:
                        return TryUseReviveItem(context, out result);
                }
            }
            catch (Exception ex)
            {
                result = "执行异常: " + ex.Message;
                rule.LastError = result;
                FileLogger.Log("AUTO-USE", result);
            }

            result = "未知动作";
            return false;
        }

        private static bool TryUseSlot(AutoUseContext context, int slot, out string result)
        {
            ObjectBaseInfo info = GetSlotInfo(context, slot);
            if (info == null)
            {
                result = "槽位无对象 slot=" + slot;
                return false;
            }
            SkillInfo skill = info as SkillInfo;
            if (skill != null)
            {
                return TryUseObject(context, skill, out result);
            }
            return TryUseObject(context, info, out result);
        }

        private static bool TryUseFirstMatching(AutoUseContext context, AutoUseRule rule, out string result)
        {
            return TryUseTypedObject(context, rule, -1, out result);
        }

        private static bool TryUseReadySkill(AutoUseContext context, AutoUseRule rule, out string result)
        {
            if (rule != null && rule.Slot > 0)
            {
                ObjectBaseInfo info = GetSlotInfo(context, rule.Slot);
                SkillInfo skill = info as SkillInfo;
                if (skill != null)
                {
                    return TryUseObject(context, skill, out result);
                }
                result = "指定槽位当前不是技能 slot=" + rule.Slot;
                return false;
            }

            return TryUseTypedObject(context, rule, 1, out result);
        }

        private static bool TryUseTypedObject(AutoUseContext context, AutoUseRule rule, int forcedType, out string result)
        {
            ObjectBaseInfo[] slots = context.Slots;
            if (slots == null)
            {
                result = "槽位数据为空";
                return false;
            }

            bool skillOnly = forcedType == 1 || (forcedType < 0 && rule != null && rule.TypeFilter == 1);
            if (skillOnly)
            {
                return TryUseFirstReadySkill(context, rule, out result);
            }

            for (int i = 0; i < slots.Length; i++)
            {
                ObjectBaseInfo info = slots[i];
                if (info == null) continue;
                if (forcedType >= 0 && info.type != (byte)forcedType) continue;
                if (forcedType < 0 && rule.TypeFilter >= 0 && info.type != (byte)rule.TypeFilter) continue;
                if (rule.SubTypeFilter >= 0 && info.sub_type != (byte)rule.SubTypeFilter) continue;
                if (!string.IsNullOrEmpty(rule.NameContains))
                {
                    string n = (info.display_name ?? string.Empty) + "|" + (info.name ?? string.Empty);
                    if (n.IndexOf(rule.NameContains, StringComparison.OrdinalIgnoreCase) < 0) continue;
                }

                if (TryUseObject(context, info, out result)) return true;
            }

            result = "没有可用匹配对象 type=" + rule.TypeFilter + " subtype=" + rule.SubTypeFilter;
            return false;
        }

        private static bool TryUseFirstReadySkill(AutoUseContext context, AutoUseRule rule, out string result)
        {
            ObjectBaseInfo[] slots = context.Slots;
            if (slots == null)
            {
                result = "槽位数据为空";
                return false;
            }

            SkillInfo selected = null;
            for (int i = 0; i < slots.Length; i++)
            {
                SkillInfo skill = slots[i] as SkillInfo;
                if (skill == null) continue;
                if (rule != null && rule.SubTypeFilter >= 0 && skill.sub_type != (byte)rule.SubTypeFilter) continue;
                if (rule != null && !string.IsNullOrEmpty(rule.NameContains))
                {
                    string n = (skill.display_name ?? string.Empty) + "|" + (skill.name ?? string.Empty);
                    if (n.IndexOf(rule.NameContains, StringComparison.OrdinalIgnoreCase) < 0) continue;
                }
                if (!IsObjectReady(skill)) continue;
                selected = skill;
                break;
            }

            if (selected == null)
            {
                result = "没有符合筛选且可执行的技能";
                return false;
            }

            bool ok = TryUseObject(context, selected, out result);
            result = result + " selected_skill=" + GetSkillTypeName(selected.sub_type);
            return ok;
        }

        private static bool TryUseSkillIfWorth(AutoUseContext context, SkillInfo skill, out string result)
        {
            if (skill == null)
            {
                result = "技能为空";
                return false;
            }

            return TryUseObject(context, skill, out result);
        }

        private static bool TryUseBestHealItem(AutoUseContext context, out string result)
        {
            return TryUseItemBySubTypes(context, out result, 4, 7, 6, 5, 1, 0, 3, 2, 9);
        }

        private static bool TryUseReviveItem(AutoUseContext context, out string result)
        {
            return TryUseItemBySubTypes(context, out result, 11, 10);
        }

        private static bool TryUseItemBySubTypes(AutoUseContext context, out string result, params int[] subTypes)
        {
            ObjectBaseInfo[] slots = context.Slots;
            if (slots == null)
            {
                result = "槽位数据为空";
                return false;
            }

            for (int k = 0; k < subTypes.Length; k++)
            {
                int wanted = subTypes[k];
                for (int i = 0; i < slots.Length; i++)
                {
                    ItemInfo item = slots[i] as ItemInfo;
                    if (item == null || item.sub_type != (byte)wanted) continue;
                    if (TryUseObject(context, item, out result)) return true;
                }
            }

            result = "没有可用道具 subtype=" + JoinInts(subTypes);
            return false;
        }

        private static bool TryUseObject(AutoUseContext context, ObjectBaseInfo info,
            out string result)
        {
            result = string.Empty;
            if (info == null)
            {
                result = "对象为空";
                return false;
            }

            ItemInfo item = info as ItemInfo;
            if (item != null)
            {
                if ((short)item.count <= 0)
                {
                    result = "物品数量为0 slot=" + info.slot;
                    return false;
                }
                if ((float)item.cooling > 0f)
                {
                    result = "物品冷却中 slot=" + info.slot;
                    return false;
                }
                string unavailableReason;
                if (IsHealItemSubType(item.sub_type) &&
                    !CanUseHealItemNow(context, out unavailableReason))
                {
                    result = "heal_item_blocked=" + unavailableReason +
                        " slot=" + info.slot;
                    return false;
                }
            }
            else if (!info.cool_down_ready)
            {
                result = "冷却未就绪 slot=" + info.slot;
                return false;
            }

            if (!info.CanAction())
            {
                result = "CanAction=false slot=" + info.slot;
                return false;
            }

            bool ok = info.Action();
            result = "slot=" + info.slot + " type=" + info.type + " subtype=" + info.sub_type + " ok=" + ok;
            return ok;
        }

#if false
        // Legacy tactical scoring is preserved for reference only. Configured rules no longer call it.
        private static SkillDecision EvaluateSkillUse(AutoUseContext context, SkillInfo skill)
        {
            if (context == null || skill == null) return SkillDecision.No("context_or_skill_null");
            if (!IsObjectReady(skill)) return SkillDecision.No("not_ready");

            Character player = context.Player;
            int subType = skill.sub_type;
            double hpPct = context.Get("hp_pct");
            double shieldPct = context.Get("shield_pct");
            double danger = context.Get("danger_score");
            double pressure = context.Get("combat_pressure");
            double enemyNear = context.Get("enemy_near_dist");
            double enemyNear5 = context.Get("enemy_near_5");
            double enemyNear10 = context.Get("enemy_near_10");
            double enemyNear20 = context.Get("enemy_near_20");
            double enemyAiming = context.Get("enemy_aiming_count");
            double enemyLos = context.Get("enemy_los_count");
            double enemyLow50 = context.Get("enemy_low_hp_50_count");
            double enemyHidden = context.Get("enemy_hidden_count");
            double allyLow50 = context.Get("ally_low_hp_50_count");
            double allyNear10 = context.Get("ally_near_10");
            bool bossAlive = context.Get("boss_alive_count") > 0.5d;
            bool bossNear = context.Get("boss_near_20") > 0.5d || context.Get("boss_target_exists") > 0.5d;
            bool inCombat = context.Get("in_combat") > 0.5d;
            bool needEscape = context.Get("need_escape") > 0.5d;
            bool needSpeed = context.Get("need_speed") > 0.5d;
            bool needChase = context.Get("need_chase") > 0.5d;
            bool traveling = context.Get("traveling") > 0.5d;
            bool reloading = context.Get("reloading") > 0.5d;
            bool hidden = player != null && SafeBool(delegate { return player.GetHidden(); });

            switch (subType)
            {
                case 0: // cure / kSkillHeal
                    if (hpPct <= 0d) return SkillDecision.No("hp_unknown");
                    if (hpPct <= 55d || (hpPct <= 75d && (danger >= 2d || allyLow50 > 0d)))
                        return SkillDecision.Yes((float)(120d - hpPct + danger * 8d + allyLow50 * 10d), "低血/危险治疗");
                    return SkillDecision.No("血量不低");

                case 1: // shield / kSkillShield
                    if (IsBuffActive(player, BuffType.kBuffTypeEnergyShield)) return SkillDecision.No("护盾已存在");
                    if (shieldPct <= 30d && (danger >= 2d || enemyLos > 0d || bossNear))
                        return SkillDecision.Yes((float)(72d - shieldPct + danger * 10d), "危险时补护盾");
                    if (hpPct > 0d && hpPct <= 60d && inCombat)
                        return SkillDecision.Yes((float)(90d - hpPct + pressure * 6d), "低血接战护盾");
                    return SkillDecision.No("护盾收益低");

                case 2: // latent / kSkillHidden
                    if (hidden || IsBuffActive(player, BuffType.kBuffTypeLurk)) return SkillDecision.No("已经隐身");
                    if (needEscape || (hpPct > 0d && hpPct <= 55d && (enemyAiming > 0d || enemyLos > 0d || enemyNear10 > 0d)))
                        return SkillDecision.Yes((float)(95d - hpPct + danger * 12d), "低血/被瞄准隐身脱战");
                    if (reloading && enemyLos > 0d && hpPct <= 70d)
                        return SkillDecision.Yes((float)(55d + danger * 8d), "换弹暴露隐身");
                    return SkillDecision.No("不需要隐身");

                case 4: // gallop / kSkillGallop
                    if (IsBuffActive(player, BuffType.kBuffTypeGallop)) return SkillDecision.No("疾跑已存在");
                    if (needSpeed || traveling)
                        return SkillDecision.Yes((float)(45d + (traveling ? 18d : 0d) + (needEscape ? 25d : 0d) + (needChase ? 18d : 0d)), "赶路/追击/脱战加速");
                    return SkillDecision.No("当前不需要加速");

                case 11: // spurt / kSkillSpurt
                    if (IsBuffActive(player, BuffType.kBuffTypeCelerity)) return SkillDecision.No("冲刺已存在");
                    if (hidden && hpPct <= 70d) return SkillDecision.No("隐身保命中，不冲刺破隐");
                    if (needEscape || (needChase && enemyNear <= 12d) || (traveling && enemyNear10 <= 0d))
                        return SkillDecision.Yes((float)(42d + (needEscape ? 35d : 0d) + (needChase ? 24d : 0d) + (traveling ? 12d : 0d)), "冲刺位移收益高");
                    return SkillDecision.No("冲刺时机不足");

                case 14: // energy / kSkillEnergy
                    if ((hpPct > 0d && hpPct <= 72d && (inCombat || danger >= 2d)) || allyLow50 > 0d || (bossNear && hpPct <= 85d))
                        return SkillDecision.Yes((float)(70d - Math.Min(70d, hpPct) + pressure * 8d + allyLow50 * 15d + allyNear10 * 3d), "治疗信标收益");
                    return SkillDecision.No("能量信标收益低");

                case 3: // shock wave
                case 64: // shock
                    if (enemyNear5 > 0d || (bossNear && enemyNear <= 8d))
                        return SkillDecision.Yes((float)(50d + enemyNear5 * 18d + pressure * 5d), "近身震荡");
                    return SkillDecision.No("没有近身目标");

                case 9: // arrow rain
                case 61: // nuclear
                case 62: // blare field
                case 65: // immediately
                    if (bossAlive || enemyNear10 >= 2d || context.Get("aim_target_exists") > 0.5d)
                        return SkillDecision.Yes((float)(45d + enemyNear10 * 10d + (bossAlive ? 30d : 0d) + enemyLow50 * 6d), "范围/爆发输出");
                    return SkillDecision.No("范围输出目标不足");

                case 13: // snare
                    if (enemyNear <= 8d || enemyNear10 >= 2d || (bossNear && enemyNear <= 12d))
                        return SkillDecision.Yes((float)(48d + enemyNear10 * 12d + (bossNear ? 12d : 0d)), "近敌/卡点放雷");
                    return SkillDecision.No("没有合适放雷目标");

                case 56: // perception
                    if (enemyHidden > 0d)
                        return SkillDecision.Yes((float)(50d + enemyHidden * 15d), "侦测隐身目标");
                    return SkillDecision.No("没有隐身目标");

                case 5:  // pierce
                case 6:  // vitals
                case 8:  // poison
                case 10: // heavy
                case 16: // quick machinegun
                case 17: // quick rpg
                case 18: // deadly rpg
                case 19: // cooldown rpg
                case 21: // vitals all
                case 22: // hold up
                case 23: // cooldown bow
                case 24: // quick bow
                case 38: // conduction
                case 39: // suck blood
                case 40: // plague
                case 41: // gas bomb
                case 43: // ballistic
                case 44: // tread
                case 45: // maskant
                case 46: // decelerate
                case 48: // wild
                case 49: // outcry
                case 52: // blast
                case 53: // dead blast
                case 54: // revenge anger
                case 55: // blood poison
                case 57: // bolt
                case 58: // rocket up
                case 59: // shoot up
                case 60: // awp up
                case 63: // blood fire
                case 66: // small boss bomb
                case 67: // small boss moderate
                case 68: // boss bomb goon
                    if (bossAlive || context.Get("aim_target_exists") > 0.5d || enemyNear20 > 0d)
                        return SkillDecision.Yes((float)(35d + pressure * 8d + (bossAlive ? 24d : 0d) + enemyLow50 * 8d), "接战输出/控制");
                    return SkillDecision.No("没有输出目标");

                case 7:  // tenacity
                case 20: // shield boss
                case 50: // recovery
                case 51: // sober fish
                case 71: // werewolf help
                case 73: // werewolf resilience
                    if (needEscape || danger >= 3d || (hpPct > 0d && hpPct <= 60d))
                        return SkillDecision.Yes((float)(55d + danger * 8d + (100d - hpPct) * 0.35d), "防御/恢复收益");
                    return SkillDecision.No("防御技能收益低");

                case 69: // werewolf smell
                case 74: // werewolf speed
                case 75: // werewolf spurt
                case 79: // werewolf howl
                    if (needSpeed || needChase || needEscape || enemyNear20 > 0d)
                        return SkillDecision.Yes((float)(38d + (needSpeed ? 18d : 0d) + (needEscape ? 24d : 0d) + enemyNear20 * 4d), "移动/追击收益");
                    return SkillDecision.No("移动收益低");
            }

            if (bossAlive || inCombat)
                return SkillDecision.Yes((float)(28d + pressure * 5d + (bossAlive ? 12d : 0d)), "未知技能接战兜底");
            return SkillDecision.No("未知技能且当前不接战");
        }
#endif

        private static string GetSkillTypeName(int subType)
        {
            switch (subType)
            {
                case 0: return "治疗";
                case 1: return "护盾";
                case 2: return "隐身";
                case 3: return "震荡波";
                case 4: return "疾跑";
                case 9: return "箭雨";
                case 11: return "冲刺";
                case 13: return "陷阱/雷";
                case 14: return "能量信标";
                case 56: return "侦测";
                default: return "技能" + subType.ToString(CultureInfo.InvariantCulture);
            }
        }

        private static bool TryReload(AutoUseContext context, out string result)
        {
            Character player = context.Player;
            if (player == null || player.mWeapon == null)
            {
                result = "无当前武器";
                return false;
            }
            GunInfo gun = player.mWeapon.info as GunInfo;
            if (gun == null)
            {
                result = "当前武器不是枪械";
                return false;
            }
            if (player.mWeapon.reloading)
            {
                result = "正在换弹";
                return false;
            }
            if (player.mWeapon.clip >= (int)gun.ammo_one_clip)
            {
                result = "弹匣已满";
                return false;
            }
            player.mWeapon.Reload();
            result = "reload weapon_slot=" + context.Get("weapon_slot");
            return true;
        }

        private static bool TryChangeWeapon(AutoUseContext context, int slot, out string result)
        {
            Character player = context.Player;
            if (player == null)
            {
                result = "player=null";
                return false;
            }
            if (slot <= 0)
            {
                result = "slot非法";
                return false;
            }
            if (player.mWeapon != null && player.mWeapon.info != null && player.mWeapon.info.slot == (byte)slot)
            {
                result = "已经是当前武器 slot=" + slot;
                return false;
            }
            bool found = false;
            if (player.weaponlist != null)
            {
                for (int i = 0; i < player.weaponlist.Count; i++)
                {
                    WeaponBase weapon = player.weaponlist[i];
                    if (weapon != null && weapon.info != null && weapon.info.slot == (byte)slot)
                    {
                        found = true;
                        break;
                    }
                }
            }
            if (!found)
            {
                result = "未找到武器 slot=" + slot;
                return false;
            }
            player.ChangeWeapon(slot);
            result = "change_weapon slot=" + slot;
            return true;
        }

        private static bool TrySpecialAction(int action, out string result)
        {
            if (GameApp.Instance == null || GameApp.Instance.channel_connection == null)
            {
                result = "channel_connection=null";
                return false;
            }
            GameApp.Instance.channel_connection.SpecialAction((byte)Mathf.Clamp(action, 0, 255));
            result = "special_action=" + action;
            return true;
        }

        private static bool TryPickUpDropItem(AutoUseContext context, int id, out string result)
        {
            if (GameApp.Instance == null || GameApp.Instance.channel_connection == null)
            {
                result = "channel_connection=null";
                return false;
            }

            if (id < 0)
            {
                DropItem nearest = FindNearestDropItem(context);
                if (nearest == null)
                {
                    result = "当前没有可拾取掉落物";
                    return false;
                }
                id = nearest.GetID();
            }

            GameApp.Instance.channel_connection.PickUpDropItem((byte)Mathf.Clamp(id, 0, 255));
            result = "pickup_id=" + id;
            return true;
        }

        private static DropItem FindNearestDropItem(AutoUseContext context)
        {
            if (context == null || context.Level == null || context.Player == null || context.Level.drop_item_set == null)
                return null;

            DropItem best = null;
            float bestDist = float.MaxValue;
            Vector3 playerPos = context.Player.transform != null ? context.Player.transform.position : Vector3.zero;
            for (int i = 0; i < context.Level.drop_item_set.Count; i++)
            {
                DropItem item = context.Level.drop_item_set[i];
                if (item == null || item.isDestroy) continue;
                try
                {
                    float dist = Vector3.Distance(playerPos, item.GetPosition());
                    if (dist < bestDist)
                    {
                        best = item;
                        bestDist = dist;
                    }
                }
                catch
                {
                }
            }
            return best;
        }

        private static bool TryRequestRevive(out string result)
        {
            if (GameApp.Instance == null || GameApp.Instance.channel_connection == null)
            {
                result = "channel_connection=null";
                return false;
            }
            GameApp.Instance.channel_connection.RequestRevive();
            result = "request_revive";
            return true;
        }

        private static string JoinInts(int[] values)
        {
            if (values == null || values.Length == 0) return string.Empty;
            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < values.Length; i++)
            {
                if (i > 0) sb.Append(",");
                sb.Append(values[i].ToString(CultureInfo.InvariantCulture));
            }
            return sb.ToString();
        }

        private static ObjectBaseInfo GetSlotInfo(AutoUseContext context, int slot)
        {
            if (context == null || context.Slots == null || slot <= 0 || slot > context.Slots.Length) return null;
            return context.Slots[slot - 1];
        }

        private static AutoUseContext BuildContext(Level level, Character player)
        {
            AutoUseContext c = new AutoUseContext();
            c.Level = level;
            c.Player = player;
            c.Slots = GetSlots(player);

            bool inGame = IsChannelInGame();
            c.Set("true", 1);
            c.Set("false", 0);
            c.Set("in_game", inGame ? 1 : 0);
            c.Set("has_level", level != null ? 1 : 0);
            c.Set("has_player", player != null ? 1 : 0);
            c.Set("time", Time.time);
            c.Set("frame", Time.frameCount);

            AddConstants(c);
            AddChannelVars(c);
            AddLevelVars(c, level);
            AddPlayerVars(c, player);
            AddSlotVars(c, c.Slots);
            AddCharacterVars(c, level, player);
            AddBossVars(c, level, player);
            AddAimVars(c, player);
            AddTacticalVars(c, player);
            return c;
        }

        private static void AddConstants(AutoUseContext c)
        {
            c.Set("type_skill", 1);
            c.Set("type_item", 3);
            c.Set("item_bandage", 0);
            c.Set("item_bandage2", 1);
            c.Set("item_cardiac", 2);
            c.Set("item_blood_serum", 3);
            c.Set("item_first_aid", 4);
            c.Set("item_cookie", 5);
            c.Set("item_ham", 6);
            c.Set("item_lobster", 7);
            c.Set("item_detect", 8);
            c.Set("item_mooncake", 9);
            c.Set("item_revive", 10);
            c.Set("item_revive_all", 11);
        }

        private static void AddChannelVars(AutoUseContext c)
        {
            ChannelConnection channel = null;
            try
            {
                channel = GameApp.Instance != null ? GameApp.Instance.channel_connection : null;
            }
            catch
            {
                channel = null;
            }

            if (channel == null)
            {
                c.Set("channel_state", -1);
                c.Set("game_state", -1);
                c.Set("in_channel", 0);
                c.Set("in_room", 0);
                return;
            }

            c.Set("channel_state", (int)channel.state);
            c.Set("game_state", (int)channel.game_state);
            c.Set("in_channel", channel.state == ChannelConnection.State.kInChannel ? 1 : 0);
            c.Set("in_room", channel.state == ChannelConnection.State.kInRoom ? 1 : 0);
            c.Set("game_alive_state", channel.game_state == ChannelConnection.GameState.kAlive ? 1 : 0);
            c.Set("game_died_state", channel.game_state == ChannelConnection.GameState.kDied ? 1 : 0);
            c.Set("game_leaving_state", channel.game_state == ChannelConnection.GameState.kGameLeaving ? 1 : 0);
        }

        private static void AddLevelVars(AutoUseContext c, Level level)
        {
            if (level == null)
            {
                c.Set("game_type", -1);
                c.Set("map_id", 0);
                c.Set("level_state", -1);
                return;
            }

            c.Set("game_type", (int)level.game_type);
            c.Set("map_id", (double)(ulong)level.map_id);
            c.Set("level_state", (int)level.state);
            c.Set("team_type", level.team_type);
            c.Set("team_hurt", level.team_hurt ? 1 : 0);
            c.Set("lock_move", level.lockMove ? 1 : 0);
            c.Set("in_special_range", level.in_special_range ? 1 : 0);
            c.Set("spawn_time", level.spawn_time);
            c.Set("water_factor_speed", level.water_factor_speed);
            c.Set("active_boss_stage", SafeInt(delegate { return level.GetActiveBossStage(); }));
            c.Set("max_boss_stage", SafeInt(delegate { return level.GetMaxBossStageID(); }));
            c.Set("freedom_boss_ready", SafeBool(delegate { return level.AllFreedBossReady(); }) ? 1 : 0);
            c.Set("special_pickup_active", SafeBool(delegate { return level.IsSpecialActing(SpecialActionType.pick_up_chest); }) ? 1 : 0);
            c.Set("special_lay_mines_active", SafeBool(delegate { return level.IsSpecialActing(SpecialActionType.lay_mines); }) ? 1 : 0);
            c.Set("special_remove_mines_active", SafeBool(delegate { return level.IsSpecialActing(SpecialActionType.remove_mines); }) ? 1 : 0);
        }

        private static void AddPlayerVars(AutoUseContext c, Character player)
        {
            if (player == null)
            {
                c.Set("alive", 0);
                c.Set("dead", 1);
                return;
            }

            int maxHp = Math.Max(player.max_health, player.character_info != null ? player.character_info.max_health : 0);
            c.Set("alive", !player.IsDied ? 1 : 0);
            c.Set("dead", player.IsDied ? 1 : 0);
            c.Set("hp", player.hp);
            c.Set("max_hp", maxHp);
            c.Set("hp_pct", Percent(player.hp, maxHp));
            c.Set("shield", player.shield);
            c.Set("max_shield", player.max_shield);
            c.Set("shield_pct", Percent(player.shield, player.max_shield));
            c.Set("anger", player.anger);
            c.Set("team", player.GetTeam());
            c.Set("uid", player.uid);
            c.Set("ping", player.ping);
            c.Set("kills", player.num_killed);
            c.Set("deaths", player.num_died);
            c.Set("combo", player.combo);
            c.Set("max_combo", player.max_combo);
            c.Set("holding_attack_count", player.holding_attack_count);
            c.Set("total_output", player.total_output);
            c.Set("invincible_time", player.invincible_time);
            c.Set("can_select", player.can_select ? 1 : 0);
            c.Set("is_player", player.IsPlayer ? 1 : 0);
            c.Set("is_robot", player.IsRobot ? 1 : 0);
            c.Set("is_human", player.IsHuman ? 1 : 0);
            c.Set("is_viewer", player.Is_Viewer ? 1 : 0);
            c.Set("is_hidden", SafeBool(delegate { return player.GetHidden(); }) ? 1 : 0);
            c.Set("is_flying", SafeBool(delegate { return player.IsFlying(); }) ? 1 : 0);
            c.Set("is_jump", SafeBool(delegate { return player.IsJumping(); }) ? 1 : 0);
            c.Set("is_jump2", SafeBool(delegate { return player.IsJump2(); }) ? 1 : 0);
            c.Set("is_ground", SafeBool(delegate { return player.IsOnGround(); }) ? 1 : 0);
            c.Set("is_crouch", SafeBool(delegate { return player.GetCrouch(); }) ? 1 : 0);
            c.Set("can_fire", SafeBool(delegate { return player.CanFire(); }) ? 1 : 0);
            c.Set("rolling", player.rolling ? 1 : 0);
            c.Set("shooting", player.shooting ? 1 : 0);
            c.Set("grenade_throw_out", player.grenade_throw_out ? 1 : 0);
            c.Set("stabing", player.stabing ? 1 : 0);
            c.Set("special_action_on", player.special_action_on ? 1 : 0);
            c.Set("treasure_chest_id", player.treasure_chest_id);
            c.Set("explosive_id", player.explosive_id);
            c.Set("have_wing", player.haveWing ? 1 : 0);
            c.Set("camera_fov", player.camera_fov);
            c.Set("mouse_sensitivity", player.mouse_sensitivity);
            c.Set("x", player.transform != null ? player.transform.position.x : 0f);
            c.Set("y", player.transform != null ? player.transform.position.y : 0f);
            c.Set("z", player.transform != null ? player.transform.position.z : 0f);

            if (player.motor1 != null)
            {
                Vector3 speed = player.motor1.current_speed;
                c.Set("speed", speed.magnitude);
                c.Set("speed_x", speed.x);
                c.Set("speed_y", speed.y);
                c.Set("speed_z", speed.z);
                c.Set("move_speed", (float)player.motor1.move_speed);
                c.Set("vertical_speed", (float)player.motor1.vertical_speed);
                c.Set("wing_cooldown", player.motor1.wing_cool_down_time);
                c.Set("motor_can_control", player.motor1.canControl ? 1 : 0);
                c.Set("motor_in_water", player.motor1.in_water ? 1 : 0);
                c.Set("motor_spurt", player.motor1.spurt ? 1 : 0);
                c.Set("motor_fall", player.motor1.fall ? 1 : 0);
            }

            WeaponBase weapon = player.mWeapon;
            if (weapon != null)
            {
                int clip = weapon.clip;
                int clipMax = 0;
                GunInfo gun = weapon.info as GunInfo;
                if (gun != null) clipMax = (int)gun.ammo_one_clip;

                c.Set("clip", clip);
                c.Set("clip_max", clipMax);
                c.Set("clip_pct", Percent(clip, clipMax));
                c.Set("reloading", weapon.reloading ? 1 : 0);
                c.Set("weapon_slot", weapon.info != null ? weapon.info.slot : 0);
                c.Set("weapon_type", weapon.info != null ? weapon.info.type : 0);
                c.Set("weapon_subtype", weapon.info != null ? weapon.info.sub_type : 0);
                c.Set("weapon_ready", weapon.info != null && weapon.info.cool_down_ready ? 1 : 0);
                c.Set("weapon_cooling", weapon.info != null ? (float)weapon.info.cooling : 0f);
            }
        }

        private static void AddSlotVars(AutoUseContext c, ObjectBaseInfo[] slots)
        {
            int skillReady = 0;
            int itemReady = 0;
            int healItemReady = 0;
            int reviveItemReady = 0;
            int itemTotalCount = 0;
            int[] itemReadyBySubType = new int[12];
            int[] itemCountBySubType = new int[12];
            int[] skillReadyBySubType = new int[64];
            string healUnavailableReason;
            bool healActionReady = CanUseHealItemNow(c, out healUnavailableReason);
            c.Set("heal_action_ready", healActionReady ? 1 : 0);

            for (int i = 1; i <= 36; i++)
            {
                ObjectBaseInfo info = slots != null && i <= slots.Length ? slots[i - 1] : null;
                string p = "slot" + i.ToString(CultureInfo.InvariantCulture);
                ItemInfo item = info as ItemInfo;
                bool objectReady = IsObjectReady(info) &&
                    !(item != null && IsHealItemSubType(item.sub_type) &&
                      !healActionReady);

                c.Set(p + "_exists", info != null ? 1 : 0);
                c.Set(p + "_ready", objectReady ? 1 : 0);
                c.Set(p + "_type", info != null ? info.type : 0);
                c.Set(p + "_subtype", info != null ? info.sub_type : 0);
                c.Set(p + "_cooling", info != null ? (float)info.cooling : 0f);
                c.Set(p + "_cooldown", info != null ? (float)info.cool_down : 0f);
                c.Set(p + "_is_skill", info is SkillInfo ? 1 : 0);
                c.Set(p + "_is_item", info is ItemInfo ? 1 : 0);

                int count = item != null ? (short)item.count : 0;
                c.Set(p + "_count", count);
                itemTotalCount += count;

                if (info is SkillInfo && objectReady)
                {
                    skillReady++;
                    if (info.sub_type < skillReadyBySubType.Length)
                        skillReadyBySubType[info.sub_type]++;
                }
                if (item != null)
                {
                    if (item.sub_type < itemCountBySubType.Length)
                        itemCountBySubType[item.sub_type] += count;
                    if (objectReady)
                    {
                        itemReady++;
                        if (item.sub_type < itemReadyBySubType.Length)
                            itemReadyBySubType[item.sub_type]++;
                        if (IsHealItemSubType(item.sub_type)) healItemReady++;
                        if (item.sub_type == 10 || item.sub_type == 11) reviveItemReady++;
                    }
                }
            }

            c.Set("skill_ready_count", skillReady);
            c.Set("item_ready_count", itemReady);
            c.Set("heal_item_ready_count", healItemReady);
            c.Set("revive_item_ready_count", reviveItemReady);
            c.Set("item_total_count", itemTotalCount);
            for (int i = 0; i < itemReadyBySubType.Length; i++)
            {
                c.Set("item_subtype_" + i + "_ready", itemReadyBySubType[i]);
                c.Set("item_subtype_" + i + "_count", itemCountBySubType[i]);
            }
            for (int i = 0; i < skillReadyBySubType.Length; i++)
                c.Set("skill_subtype_" + i + "_ready", skillReadyBySubType[i]);

            c.Set("item_bandage_ready", itemReadyBySubType[0] + itemReadyBySubType[1]);
            c.Set("item_first_aid_ready", itemReadyBySubType[4]);
            c.Set("item_cookie_ready", itemReadyBySubType[5]);
            c.Set("item_ham_ready", itemReadyBySubType[6]);
            c.Set("item_lobster_ready", itemReadyBySubType[7]);
            c.Set("item_detect_ready", itemReadyBySubType[8]);
            c.Set("item_revive_ready", itemReadyBySubType[10] + itemReadyBySubType[11]);
            c.Set("item_bandage_count", itemCountBySubType[0] + itemCountBySubType[1]);
            c.Set("item_first_aid_count", itemCountBySubType[4]);
            c.Set("item_lobster_count", itemCountBySubType[7]);
        }

        private static void AddCharacterVars(AutoUseContext c, Level level, Character player)
        {
            CharacterCache.Clear();
            try
            {
                if (level != null && level.GetCharacters() != null)
                    CharacterCache.AddRange(level.GetCharacters());
            }
            catch
            {
            }

            int enemyCount = 0;
            int enemyAlive = 0;
            int enemyHidden = 0;
            int enemyLow25 = 0;
            int enemyLow50 = 0;
            int enemyNear5 = 0;
            int enemyNear10 = 0;
            int enemyNear20 = 0;
            int enemyNear30 = 0;
            int enemyLos = 0;
            int enemyAiming = 0;
            int allyAlive = 0;
            int allyCount = 0;
            int allyLow25 = 0;
            int allyLow50 = 0;
            int allyNear10 = 0;
            double enemyHpSum = 0;
            double enemyPctSum = 0;
            double enemyMinHp = 0;
            double enemyMinPct = 0;
            double enemyMaxHp = 0;
            double nearestEnemyHp = 0;
            double nearestEnemyHpPct = 0;
            double nearestEnemyUid = 0;
            double allyMinHpPct = 0;
            float nearestEnemy = float.MaxValue;

            int playerTeam = player != null ? player.GetTeam() : -999;
            Vector3 playerPos = player != null && player.transform != null ? player.transform.position : Vector3.zero;

            for (int i = 0; i < CharacterCache.Count; i++)
            {
                Character ch = CharacterCache[i];
                if (ch == null || ch == player || ch.Is_Viewer) continue;

                bool isEnemy = player == null || ch.GetTeam() != playerTeam;
                bool alive = !ch.IsDied && ch.hp > 0;
                int maxHp = Math.Max(ch.max_health, ch.character_info != null ? ch.character_info.max_health : 0);
                double hpPct = Percent(ch.hp, maxHp);

                if (isEnemy)
                {
                    enemyCount++;
                    if (alive)
                    {
                        enemyAlive++;
                        enemyHpSum += ch.hp;
                        enemyPctSum += hpPct;
                        if (enemyAlive == 1 || ch.hp < enemyMinHp) enemyMinHp = ch.hp;
                        if (enemyAlive == 1 || hpPct < enemyMinPct) enemyMinPct = hpPct;
                        if (ch.hp > enemyMaxHp) enemyMaxHp = ch.hp;
                        if (hpPct <= 25d) enemyLow25++;
                        if (hpPct <= 50d) enemyLow50++;
                        bool hidden = SafeBool(delegate { return ch.GetHidden(); });
                        if (hidden) enemyHidden++;

                        if (player != null && ch.transform != null)
                        {
                            float d = Vector3.Distance(playerPos, ch.transform.position);
                            if (d < nearestEnemy) nearestEnemy = d;
                            if (d <= nearestEnemy)
                            {
                                nearestEnemyHp = ch.hp;
                                nearestEnemyHpPct = hpPct;
                                nearestEnemyUid = ch.uid;
                            }
                            if (d <= 5f) enemyNear5++;
                            if (d <= 10f) enemyNear10++;
                            if (d <= 20f) enemyNear20++;
                            if (d <= 30f) enemyNear30++;

                            bool visibleByGame = !hidden || IsVisibleByGame(player, ch);
                            bool los = visibleByGame && HasLineOfSightToCharacter(player, ch);
                            if (los) enemyLos++;
                            if (los && IsFacingPoint(ch, playerPos, 0.72f)) enemyAiming++;
                        }
                    }
                }
                else
                {
                    allyCount++;
                    if (alive)
                    {
                        allyAlive++;
                        if (allyAlive == 1 || hpPct < allyMinHpPct) allyMinHpPct = hpPct;
                        if (hpPct <= 25d) allyLow25++;
                        if (hpPct <= 50d) allyLow50++;
                        if (player != null && ch.transform != null &&
                            Vector3.Distance(playerPos, ch.transform.position) <= 10f)
                        {
                            allyNear10++;
                        }
                    }
                }
            }

            c.Set("enemy_count", enemyCount);
            c.Set("enemy_alive_count", enemyAlive);
            c.Set("enemy_dead_count", Math.Max(0, enemyCount - enemyAlive));
            c.Set("enemy_hidden_count", enemyHidden);
            c.Set("enemy_low_hp_25_count", enemyLow25);
            c.Set("enemy_low_hp_50_count", enemyLow50);
            c.Set("enemy_min_hp", enemyMinHp);
            c.Set("enemy_max_hp", enemyMaxHp);
            c.Set("enemy_avg_hp", enemyAlive > 0 ? enemyHpSum / enemyAlive : 0);
            c.Set("enemy_avg_hp_pct", enemyAlive > 0 ? enemyPctSum / enemyAlive : 0);
            c.Set("enemy_min_hp_pct", enemyMinPct);
            c.Set("enemy_nearest_hp", nearestEnemyHp);
            c.Set("enemy_nearest_hp_pct", nearestEnemyHpPct);
            c.Set("enemy_nearest_uid", nearestEnemyUid);
            c.Set("enemy_near_5", enemyNear5);
            c.Set("enemy_near_10", enemyNear10);
            c.Set("enemy_near_20", enemyNear20);
            c.Set("enemy_near_30", enemyNear30);
            c.Set("enemy_near_dist", nearestEnemy < float.MaxValue ? nearestEnemy : 99999f);
            c.Set("enemy_visible_count", enemyLos);
            c.Set("enemy_los_count", enemyLos);
            c.Set("enemy_aiming_count", enemyAiming);
            c.Set("ally_count", allyCount);
            c.Set("ally_alive_count", allyAlive);
            c.Set("ally_low_hp_25_count", allyLow25);
            c.Set("ally_low_hp_50_count", allyLow50);
            c.Set("ally_min_hp_pct", allyMinHpPct);
            c.Set("ally_near_10", allyNear10);
        }

        private static void AddBossVars(AutoUseContext c, Level level, Character player)
        {
            BossCache.Clear();
            if (level != null)
            {
                AddBosses(level.boss_manager);
                AddBosses(level.freedom_boss_manager);
                try
                {
                    if (level.bossStorageList != null)
                    {
                        for (int i = 0; i < level.bossStorageList.Count; i++)
                            AddBoss(level.bossStorageList[i]);
                    }
                }
                catch
                {
                }
            }

            int aliveCount = 0;
            int near10 = 0;
            int near20 = 0;
            int weakCount = 0;
            int flyingCount = 0;
            int puppetCount = 0;
            double hp = 0;
            double maxHp = 0;
            double minPct = 0;
            double maxPct = 0;
            double totalHp = 0;
            double totalMaxHp = 0;
            float near = float.MaxValue;
            Vector3 playerPos = player != null && player.transform != null ? player.transform.position : Vector3.zero;

            for (int i = 0; i < BossCache.Count; i++)
            {
                BaseBoss boss = BossCache[i];
                if (!IsBossAlive(boss)) continue;
                aliveCount++;
                double pct = Percent(boss.hp, boss.max_hp);
                totalHp += boss.hp;
                totalMaxHp += boss.max_hp;
                if (boss.isWeak) weakCount++;
                if (boss.flying) flyingCount++;
                if (boss.isPuppet) puppetCount++;
                if (aliveCount == 1 || Percent(boss.hp, boss.max_hp) < minPct)
                {
                    hp = boss.hp;
                    maxHp = boss.max_hp;
                    minPct = pct;
                }
                if (aliveCount == 1 || pct > maxPct) maxPct = pct;
                if (player != null)
                {
                    try
                    {
                        float d = Vector3.Distance(playerPos, boss.GetPosition());
                        if (d < near) near = d;
                        if (d <= 10f) near10++;
                        if (d <= 20f) near20++;
                    }
                    catch
                    {
                    }
                }
            }

            c.Set("is_boss_mode", level != null && level.game_type == RoomInfo.GameType.kGameTypeBoss ? 1 : 0);
            c.Set("boss_count", BossCache.Count);
            c.Set("boss_alive_count", aliveCount);
            c.Set("boss_hp", hp);
            c.Set("boss_max_hp", maxHp);
            c.Set("boss_hp_pct", minPct);
            c.Set("boss_min_hp_pct", minPct);
            c.Set("boss_max_hp_pct", maxPct);
            c.Set("boss_total_hp", totalHp);
            c.Set("boss_total_max_hp", totalMaxHp);
            c.Set("boss_total_hp_pct", Percent(totalHp, totalMaxHp));
            c.Set("boss_near_10", near10);
            c.Set("boss_near_20", near20);
            c.Set("boss_weak_count", weakCount);
            c.Set("boss_flying_count", flyingCount);
            c.Set("boss_puppet_count", puppetCount);
            c.Set("boss_near_dist", near < float.MaxValue ? near : 99999f);
        }

        private static void AddAimVars(AutoUseContext c, Character player)
        {
            Character aimTarget = global::ASWDEBUG.Cheats.AutoAim.AutoAim.bestTarget != null
                ? global::ASWDEBUG.Cheats.AutoAim.AutoAim.bestTarget
                : global::ASWDEBUG.Cheats.AutoAim.AutoAim.currentTarget;
            c.Set("aim_target_exists", aimTarget != null ? 1 : 0);
            if (aimTarget != null)
            {
                int maxHp = Math.Max(aimTarget.max_health, aimTarget.character_info != null ? aimTarget.character_info.max_health : 0);
                c.Set("aim_target_uid", aimTarget.uid);
                c.Set("aim_target_hp", aimTarget.hp);
                c.Set("aim_target_max_hp", maxHp);
                c.Set("aim_target_hp_pct", Percent(aimTarget.hp, maxHp));
                c.Set("aim_target_dist", DistanceTo(player, aimTarget.transform));
                c.Set("aim_target_team", aimTarget.GetTeam());
                c.Set("aim_target_hidden", SafeBool(delegate { return aimTarget.GetHidden(); }) ? 1 : 0);
            }

            BaseBoss bossTarget = global::ASWDEBUG.Cheats.AutoAim.BossAutoAim.bestTarget != null
                ? global::ASWDEBUG.Cheats.AutoAim.BossAutoAim.bestTarget
                : global::ASWDEBUG.Cheats.AutoAim.BossAutoAim.currentTarget;
            c.Set("boss_target_exists", bossTarget != null ? 1 : 0);
            if (bossTarget != null)
            {
                c.Set("boss_target_uid", SafeInt(delegate { return unchecked((int)bossTarget.GetUid()); }));
                c.Set("boss_target_hp", bossTarget.hp);
                c.Set("boss_target_max_hp", bossTarget.max_hp);
                c.Set("boss_target_hp_pct", Percent(bossTarget.hp, bossTarget.max_hp));
                c.Set("boss_target_dist", DistanceTo(player, bossTarget.getTransfrom()));
                c.Set("boss_target_weak", bossTarget.isWeak ? 1 : 0);
            }
        }

        private static void AddTacticalVars(AutoUseContext c, Character player)
        {
            double hpPct = c.Get("hp_pct");
            double shieldPct = c.Get("shield_pct");
            double enemyNear = c.Get("enemy_near_dist");
            double enemyNear10 = c.Get("enemy_near_10");
            double enemyNear20 = c.Get("enemy_near_20");
            double enemyLos = c.Get("enemy_los_count");
            double enemyAiming = c.Get("enemy_aiming_count");
            double bossAlive = c.Get("boss_alive_count");
            double bossNear = c.Get("boss_near_dist");
            double aimTargetDist = c.Get("aim_target_dist");
            double bossTargetDist = c.Get("boss_target_dist");

            int autoBattleState = -1;
            bool autoBattleSeeking = false;
            try
            {
                AutoBattleState state = global::ASWDEBUG.Cheats.AutoBattle.AutoBattleManager.State;
                autoBattleState = (int)state;
                autoBattleSeeking = state == AutoBattleState.Seek ||
                                    state == AutoBattleState.RouteToEngage ||
                                    state == AutoBattleState.StuckRecovery;
            }
            catch
            {
            }

            bool targetFar = (c.Get("aim_target_exists") > 0.5d && aimTargetDist > 12d) ||
                             (c.Get("boss_target_exists") > 0.5d && bossTargetDist > 14d);
            bool combat = enemyNear20 > 0d || enemyLos > 0d || bossAlive > 0d;
            double combatPressure = enemyNear10 * 2d + enemyLos + enemyAiming * 2.5d +
                                    ((bossAlive > 0d && bossNear <= 18d) ? 2d : 0d);
            double danger = combatPressure;
            if (hpPct > 0d && hpPct <= 55d) danger += 2d;
            if (hpPct > 0d && hpPct <= 32d) danger += 3d;
            if (shieldPct > 0d && shieldPct <= 20d) danger += 1d;

            bool traveling = autoBattleSeeking ||
                             (combatPressure <= 0.5d && enemyNear > 16d && c.Get("speed") > 1.2d) ||
                             (targetFar && enemyNear10 <= 0d);
            bool needEscape = danger >= 4d || (hpPct > 0d && hpPct <= 45d && (enemyNear10 > 0d || enemyLos > 0d));
            bool needSpeed = traveling || needEscape || targetFar;
            bool needChase = c.Get("aim_target_exists") > 0.5d && aimTargetDist > 6d && aimTargetDist <= 22d &&
                             enemyAiming <= 0d && hpPct >= 35d;

            c.Set("auto_battle_state", autoBattleState);
            c.Set("auto_battle_seeking", autoBattleSeeking ? 1 : 0);
            c.Set("combat_pressure", combatPressure);
            c.Set("danger_score", danger);
            c.Set("in_combat", combat ? 1 : 0);
            c.Set("traveling", traveling ? 1 : 0);
            c.Set("need_escape", needEscape ? 1 : 0);
            c.Set("need_speed", needSpeed ? 1 : 0);
            c.Set("need_chase", needChase ? 1 : 0);
            c.Set("low_hp", hpPct > 0d && hpPct <= 55d ? 1 : 0);
            c.Set("very_low_hp", hpPct > 0d && hpPct <= 32d ? 1 : 0);
        }

        private static ObjectBaseInfo[] GetSlots(Character player)
        {
            try
            {
                if (player == null || player.character_info == null || player.character_info.slots_info == null)
                    return null;
                return player.character_info.slots_info.object_info;
            }
            catch
            {
                return null;
            }
        }

        private static bool CanUseHealItemNow(AutoUseContext context,
            out string reason)
        {
            reason = string.Empty;
            Character player = context != null ? context.Player : null;
            if (context == null || context.Get("in_game") < 0.5d)
            {
                reason = "not_in_game";
                return false;
            }
            if (player == null)
            {
                reason = "player_null";
                return false;
            }
            if (player.IsDied)
            {
                reason = "player_dead";
                return false;
            }
            if (player.Is_Viewer && !player.Is_GP)
            {
                reason = "viewer";
                return false;
            }

            try
            {
                if (player.IsFlying())
                {
                    reason = "flying";
                    return false;
                }
            }
            catch
            {
                reason = "flying_state_unknown";
                return false;
            }

            try
            {
                if (player.motor1 != null && !player.motor1.canControl)
                {
                    reason = "movement_control_locked";
                    return false;
                }
                if (player.special_action_on)
                {
                    reason = "special_action";
                    return false;
                }
            }
            catch
            {
                reason = "action_state_unknown";
                return false;
            }

            return true;
        }

        private static bool IsObjectReady(ObjectBaseInfo info)
        {
            if (info == null) return false;
            ItemInfo item = info as ItemInfo;
            if (item != null)
            {
                return (short)item.count > 0 && (float)item.cooling <= 0f && item.CanAction();
            }
            return info.cool_down_ready && info.CanAction();
        }

        private static bool IsHealItemSubType(int subType)
        {
            return subType == 0 || subType == 1 || subType == 2 || subType == 3 ||
                subType == 4 || subType == 5 || subType == 6 || subType == 7 || subType == 9;
        }

        private static bool IsChannelInGame()
        {
            try
            {
                return GameApp.Instance != null &&
                    GameApp.Instance.channel_connection != null &&
                    GameApp.Instance.channel_connection.state == ChannelConnection.State.kInGame;
            }
            catch
            {
                return false;
            }
        }

        private static void AddBosses(BossManager manager)
        {
            if (manager == null) return;
            try
            {
                List<BaseBoss> bosses = manager.GetBosses();
                if (bosses == null) return;
                for (int i = 0; i < bosses.Count; i++) AddBoss(bosses[i]);
            }
            catch
            {
            }
        }

        private static void AddBoss(BaseBoss boss)
        {
            if (boss == null || BossCache.Contains(boss)) return;
            BossCache.Add(boss);
        }

        private static bool IsBossAlive(BaseBoss boss)
        {
            try
            {
                return boss != null && boss.GetActive() && boss.hp > 0 && boss.max_hp > 0f;
            }
            catch
            {
                return boss != null && boss.hp > 0;
            }
        }

        private static double Percent(double value, double max)
        {
            if (max <= 0d) return 0d;
            return Math.Max(0d, Math.Min(100d, value * 100d / max));
        }

        private delegate bool BoolGetter();
        private delegate int IntGetter();

        private static bool SafeBool(BoolGetter getter)
        {
            try
            {
                return getter != null && getter();
            }
            catch
            {
                return false;
            }
        }

        private static int SafeInt(IntGetter getter)
        {
            try
            {
                return getter != null ? getter() : 0;
            }
            catch
            {
                return 0;
            }
        }

        private static double DistanceTo(Character player, Transform target)
        {
            try
            {
                if (player == null || player.transform == null || target == null) return 99999d;
                return Vector3.Distance(player.transform.position, target.position);
            }
            catch
            {
                return 99999d;
            }
        }

        private static bool IsBuffActive(Character player, BuffType type)
        {
            try
            {
                int index = (int)type;
                return player != null &&
                    player.buff_state != null &&
                    index >= 0 &&
                    index < player.buff_state.Length &&
                    player.buff_state[index] != null &&
                    player.buff_state[index].enable;
            }
            catch
            {
                return false;
            }
        }

        private static bool IsVisibleByGame(Character player, Character target)
        {
            try
            {
                if (target == null) return false;
                if (!target.GetHidden()) return true;
                return player != null && player.SeeEffect(target) >= 0.99f;
            }
            catch
            {
                return false;
            }
        }

        private static bool HasLineOfSightToCharacter(Character player, Character target)
        {
            try
            {
                if (player == null || target == null || player.transform == null || target.transform == null) return false;
                Vector3 origin = player.transform.position + Vector3.up * 1.25f;
                Vector3 targetPoint = target.transform.position + Vector3.up * 1.15f;
                Transform ignoreRoot = player.transform.root;
                return HasClearSegment(origin, targetPoint, target, ignoreRoot);
            }
            catch
            {
                return false;
            }
        }

        private static bool IsFacingPoint(Character character, Vector3 point, float threshold)
        {
            try
            {
                if (character == null || character.transform == null) return false;
                Vector3 toPoint = point - character.transform.position;
                toPoint.y = 0f;
                if (toPoint.sqrMagnitude < 0.01f) return false;
                toPoint.Normalize();
                Vector3 forward = character.transform.forward;
                forward.y = 0f;
                if (forward.sqrMagnitude < 0.01f) return false;
                forward.Normalize();
                return Vector3.Dot(forward, toPoint) >= threshold;
            }
            catch
            {
                return false;
            }
        }

        private static bool HasClearSegment(Vector3 origin, Vector3 targetPoint, Character expectedTarget, Transform ignoreRoot)
        {
            Vector3 dir = targetPoint - origin;
            float dist = dir.magnitude;
            if (dist <= 0.05f) return true;
            dir /= dist;

            int mask = LayerMask.GetMask(new string[] { "kPlayer", "Terrarin", "kController", "Weapon" });
            RaycastHit[] hits = mask != 0
                ? Physics.RaycastAll(origin, dir, dist + 0.15f, mask)
                : Physics.RaycastAll(origin, dir, dist + 0.15f);
            if (hits == null || hits.Length == 0) return true;

            Array.Sort(hits, CompareRaycastHitDistance);
            for (int i = 0; i < hits.Length; i++)
            {
                Transform hitTransform = hits[i].transform;
                if (ShouldIgnoreHit(hitTransform, ignoreRoot)) continue;
                if (expectedTarget != null && IsHitExpectedTarget(hitTransform, expectedTarget)) return true;
                return false;
            }

            return true;
        }

        private static int CompareRaycastHitDistance(RaycastHit a, RaycastHit b)
        {
            return a.distance.CompareTo(b.distance);
        }

        private static bool ShouldIgnoreHit(Transform hitTransform, Transform ignoreRoot)
        {
            if (hitTransform == null || ignoreRoot == null) return false;
            try
            {
                Transform root = hitTransform.root;
                if (root != null && root == ignoreRoot) return true;
                Transform t = hitTransform;
                while (t != null)
                {
                    if (t == ignoreRoot) return true;
                    t = t.parent;
                }
            }
            catch
            {
            }
            return false;
        }

        private static bool IsHitExpectedTarget(Transform hitTransform, Character expectedTarget)
        {
            try
            {
                if (hitTransform == null || expectedTarget == null || expectedTarget.transform == null) return false;
                Transform targetRoot = expectedTarget.transform.root;
                Transform root = hitTransform.root;
                if (root != null && targetRoot != null && root == targetRoot) return true;

                string expectedName = expectedTarget.baseName;
                if (!string.IsNullOrEmpty(expectedName))
                {
                    if (root != null && root.name == expectedName) return true;
                    Transform t = hitTransform;
                    while (t != null)
                    {
                        if (t.name == expectedName) return true;
                        t = t.parent;
                    }
                }
            }
            catch
            {
            }

            return false;
        }

        private static bool Evaluate(string expression, Dictionary<string, double> variables, out string error)
        {
            error = string.Empty;
            try
            {
                if (string.IsNullOrEmpty(expression)) return false;
                ExpressionParser parser = new ExpressionParser(expression, variables);
                double value = parser.Parse();
                return Math.Abs(value) > 0.000001d;
            }
            catch (Exception ex)
            {
                error = "表达式错误: " + ex.Message;
                return false;
            }
        }

        private static string GetConfigPath()
        {
            try
            {
                return Path.Combine(Application.persistentDataPath, "ASW_AutoUseRules.txt");
            }
            catch
            {
                string root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                return Path.Combine(root, "ASW_AutoUseRules.txt");
            }
        }

        private static string Serialize(AutoUseRule r)
        {
            if (r == null) r = new AutoUseRule();
            string[] parts =
            {
                r.Enabled ? "1" : "0",
                Encode(r.Name),
                Encode(r.Expression),
                ((int)r.ActionKind).ToString(CultureInfo.InvariantCulture),
                r.Slot.ToString(CultureInfo.InvariantCulture),
                r.TypeFilter.ToString(CultureInfo.InvariantCulture),
                r.SubTypeFilter.ToString(CultureInfo.InvariantCulture),
                Encode(r.NameContains),
                r.CooldownMs.ToString(CultureInfo.InvariantCulture),
                r.OnlyInGame ? "1" : "0",
                r.OnlyAlive ? "1" : "0",
                r.AdvancedMode ? "1" : "0",
                ((int)r.ConditionKind).ToString(CultureInfo.InvariantCulture),
                r.ConditionValue.ToString(CultureInfo.InvariantCulture)
            };
            return string.Join("|", parts);
        }

        private static AutoUseRule Deserialize(string line)
        {
            if (string.IsNullOrEmpty(line)) return null;
            string[] p = line.Split('|');
            if (p.Length < 9) return null;

            AutoUseRule r = new AutoUseRule();
            r.Enabled = p[0] == "1";
            r.Name = Decode(p[1]);
            r.Expression = Decode(p[2]);
            r.ActionKind = (AutoUseActionKind)ParseInt(p[3], 0);
            if (r.ActionKind == AutoUseActionKind.RequestRevive)
                r.ActionKind = AutoUseActionKind.UseReviveItem;
            r.Slot = ParseInt(p[4], 1);
            r.TypeFilter = ParseInt(p[5], -1);
            r.SubTypeFilter = ParseInt(p[6], -1);
            r.NameContains = Decode(p[7]);
            r.CooldownMs = ParseInt(p[8], 1200);
            if (p.Length > 9) r.OnlyInGame = p[9] == "1";
            if (p.Length > 10) r.OnlyAlive = p[10] == "1";
            if (p.Length > 13)
            {
                r.AdvancedMode = p[11] == "1";
                r.ConditionKind = (AutoUseConditionKind)ParseInt(p[12], (int)AutoUseConditionKind.PlayerHpBelow);
                r.ConditionValue = ParseInt(p[13], 45);
            }
            else
            {
                r.AdvancedMode = true;
                r.ConditionKind = AutoUseConditionKind.PlayerHpBelow;
                r.ConditionValue = 45;
                InferFriendlyCondition(r);
            }
            return r;
        }

        private static void InferFriendlyCondition(AutoUseRule r)
        {
            if (r == null || string.IsNullOrEmpty(r.Expression)) return;
            string compact = r.Expression.Replace(" ", string.Empty).Replace("\t", string.Empty).ToLowerInvariant();

            if (compact == "hp_pct>0&&hp_pct<=45&&item_ready_count>0" ||
                compact == "hp_pct>0&&hp_pct<=45&&heal_item_ready_count>0")
            {
                r.AdvancedMode = false;
                r.ConditionKind = AutoUseConditionKind.PlayerHpBelow;
                r.ConditionValue = 45;
                return;
            }

            if (compact == "clip_max>0&&clip_pct<=10&&enemy_near_10==0")
            {
                r.AdvancedMode = false;
                r.ConditionKind = AutoUseConditionKind.SafeAmmoBelow;
                r.ConditionValue = 10;
                return;
            }

            if (compact == "enemy_near_10>0&&slot5_ready")
            {
                r.AdvancedMode = false;
                r.ConditionKind = AutoUseConditionKind.EnemyNear;
                r.ConditionValue = 10;
                return;
            }

            if (compact == "is_boss_mode&&boss_alive_count>0&&boss_hp_pct<=35&&slot6_ready")
            {
                r.AdvancedMode = false;
                r.ConditionKind = AutoUseConditionKind.BossHpBelow;
                r.ConditionValue = 35;
            }
        }

        private static string Encode(string value)
        {
            if (value == null) value = string.Empty;
            return Convert.ToBase64String(Encoding.UTF8.GetBytes(value));
        }

        private static string Decode(string value)
        {
            try
            {
                return Encoding.UTF8.GetString(Convert.FromBase64String(value ?? string.Empty));
            }
            catch
            {
                return string.Empty;
            }
        }

        private static int ParseInt(string value, int fallback)
        {
            int parsed;
            return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed) ? parsed : fallback;
        }

        private sealed class AutoUseContext
        {
            public readonly Dictionary<string, double> Variables = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            public Level Level;
            public Character Player;
            public ObjectBaseInfo[] Slots;

            public void Set(string name, double value)
            {
                Variables[name] = value;
            }

            public double Get(string name)
            {
                double value;
                return Variables.TryGetValue(name, out value) ? value : 0d;
            }
        }

        private struct SkillDecision
        {
            public bool ShouldUse;
            public float Score;
            public string Reason;

            public static SkillDecision Yes(float score, string reason)
            {
                return new SkillDecision
                {
                    ShouldUse = score >= 25f,
                    Score = score,
                    Reason = reason ?? string.Empty
                };
            }

            public static SkillDecision No(string reason)
            {
                return new SkillDecision
                {
                    ShouldUse = false,
                    Score = 0f,
                    Reason = reason ?? string.Empty
                };
            }
        }

        private enum TokenKind
        {
            End,
            Number,
            Identifier,
            Operator,
            LeftParen,
            RightParen
        }

        private struct Token
        {
            public TokenKind Kind;
            public string Text;
            public double Number;
        }

        private sealed class ExpressionParser
        {
            private readonly List<Token> _tokens = new List<Token>(64);
            private readonly Dictionary<string, double> _variables;
            private int _pos;

            public ExpressionParser(string expression, Dictionary<string, double> variables)
            {
                _variables = variables ?? new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
                Tokenize(expression ?? string.Empty);
            }

            public double Parse()
            {
                double value = ParseOr();
                if (Peek().Kind != TokenKind.End)
                    throw new InvalidOperationException("多余 token: " + Peek().Text);
                return value;
            }

            private void Tokenize(string expression)
            {
                int i = 0;
                while (i < expression.Length)
                {
                    char ch = expression[i];
                    if (char.IsWhiteSpace(ch))
                    {
                        i++;
                        continue;
                    }

                    if (char.IsDigit(ch) || ch == '.')
                    {
                        int start = i;
                        i++;
                        while (i < expression.Length && (char.IsDigit(expression[i]) || expression[i] == '.')) i++;
                        string text = expression.Substring(start, i - start);
                        double number;
                        if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out number))
                            throw new InvalidOperationException("数字格式错误: " + text);
                        _tokens.Add(new Token { Kind = TokenKind.Number, Text = text, Number = number });
                        continue;
                    }

                    if (char.IsLetter(ch) || ch == '_')
                    {
                        int start = i;
                        i++;
                        while (i < expression.Length && (char.IsLetterOrDigit(expression[i]) || expression[i] == '_')) i++;
                        string text = expression.Substring(start, i - start);
                        _tokens.Add(new Token { Kind = TokenKind.Identifier, Text = text });
                        continue;
                    }

                    if (ch == '(')
                    {
                        _tokens.Add(new Token { Kind = TokenKind.LeftParen, Text = "(" });
                        i++;
                        continue;
                    }

                    if (ch == ')')
                    {
                        _tokens.Add(new Token { Kind = TokenKind.RightParen, Text = ")" });
                        i++;
                        continue;
                    }

                    string two = i + 1 < expression.Length ? expression.Substring(i, 2) : string.Empty;
                    if (two == "&&" || two == "||" || two == ">=" || two == "<=" || two == "==" || two == "!=")
                    {
                        _tokens.Add(new Token { Kind = TokenKind.Operator, Text = two });
                        i += 2;
                        continue;
                    }

                    if ("+-*/%!<>".IndexOf(ch) >= 0)
                    {
                        _tokens.Add(new Token { Kind = TokenKind.Operator, Text = ch.ToString() });
                        i++;
                        continue;
                    }

                    throw new InvalidOperationException("不支持字符: " + ch);
                }

                _tokens.Add(new Token { Kind = TokenKind.End, Text = string.Empty });
            }

            private double ParseOr()
            {
                double left = ParseAnd();
                while (MatchOperator("||") || MatchIdentifier("or"))
                {
                    double right = ParseAnd();
                    left = IsTrue(left) || IsTrue(right) ? 1d : 0d;
                }
                return left;
            }

            private double ParseAnd()
            {
                double left = ParseEquality();
                while (MatchOperator("&&") || MatchIdentifier("and"))
                {
                    double right = ParseEquality();
                    left = IsTrue(left) && IsTrue(right) ? 1d : 0d;
                }
                return left;
            }

            private double ParseEquality()
            {
                double left = ParseRelational();
                while (true)
                {
                    if (MatchOperator("=="))
                    {
                        double right = ParseRelational();
                        left = Math.Abs(left - right) <= 0.000001d ? 1d : 0d;
                    }
                    else if (MatchOperator("!="))
                    {
                        double right = ParseRelational();
                        left = Math.Abs(left - right) > 0.000001d ? 1d : 0d;
                    }
                    else
                    {
                        return left;
                    }
                }
            }

            private double ParseRelational()
            {
                double left = ParseAdditive();
                while (true)
                {
                    if (MatchOperator(">="))
                    {
                        double right = ParseAdditive();
                        left = left >= right ? 1d : 0d;
                    }
                    else if (MatchOperator("<="))
                    {
                        double right = ParseAdditive();
                        left = left <= right ? 1d : 0d;
                    }
                    else if (MatchOperator(">"))
                    {
                        double right = ParseAdditive();
                        left = left > right ? 1d : 0d;
                    }
                    else if (MatchOperator("<"))
                    {
                        double right = ParseAdditive();
                        left = left < right ? 1d : 0d;
                    }
                    else
                    {
                        return left;
                    }
                }
            }

            private double ParseAdditive()
            {
                double left = ParseMultiplicative();
                while (true)
                {
                    if (MatchOperator("+")) left += ParseMultiplicative();
                    else if (MatchOperator("-")) left -= ParseMultiplicative();
                    else return left;
                }
            }

            private double ParseMultiplicative()
            {
                double left = ParseUnary();
                while (true)
                {
                    if (MatchOperator("*")) left *= ParseUnary();
                    else if (MatchOperator("/"))
                    {
                        double right = ParseUnary();
                        left = Math.Abs(right) <= 0.000001d ? 0d : left / right;
                    }
                    else if (MatchOperator("%"))
                    {
                        double right = ParseUnary();
                        left = Math.Abs(right) <= 0.000001d ? 0d : left % right;
                    }
                    else return left;
                }
            }

            private double ParseUnary()
            {
                if (MatchOperator("!") || MatchIdentifier("not")) return IsTrue(ParseUnary()) ? 0d : 1d;
                if (MatchOperator("-")) return -ParseUnary();
                if (MatchOperator("+")) return ParseUnary();
                return ParsePrimary();
            }

            private double ParsePrimary()
            {
                Token token = Peek();
                if (token.Kind == TokenKind.Number)
                {
                    _pos++;
                    return token.Number;
                }
                if (token.Kind == TokenKind.Identifier)
                {
                    _pos++;
                    if (string.Equals(token.Text, "true", StringComparison.OrdinalIgnoreCase)) return 1d;
                    if (string.Equals(token.Text, "false", StringComparison.OrdinalIgnoreCase)) return 0d;
                    double value;
                    return _variables.TryGetValue(token.Text, out value) ? value : 0d;
                }
                if (token.Kind == TokenKind.LeftParen)
                {
                    _pos++;
                    double value = ParseOr();
                    if (Peek().Kind != TokenKind.RightParen) throw new InvalidOperationException("缺少右括号");
                    _pos++;
                    return value;
                }
                throw new InvalidOperationException("缺少表达式值: " + token.Text);
            }

            private bool MatchOperator(string text)
            {
                Token token = Peek();
                if (token.Kind == TokenKind.Operator && token.Text == text)
                {
                    _pos++;
                    return true;
                }
                return false;
            }

            private bool MatchIdentifier(string text)
            {
                Token token = Peek();
                if (token.Kind == TokenKind.Identifier && string.Equals(token.Text, text, StringComparison.OrdinalIgnoreCase))
                {
                    _pos++;
                    return true;
                }
                return false;
            }

            private Token Peek()
            {
                return _pos < _tokens.Count ? _tokens[_pos] : new Token { Kind = TokenKind.End, Text = string.Empty };
            }

            private static bool IsTrue(double value)
            {
                return Math.Abs(value) > 0.000001d;
            }
        }
    }
}
