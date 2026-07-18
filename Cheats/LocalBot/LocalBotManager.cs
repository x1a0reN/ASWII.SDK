using ASWDEBUG.Cheats.AutoBattle;
using ASWDEBUG.Logger;
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using RAIN.Core;
using UnityEngine;

namespace ASWDEBUG.Cheats.LocalBot
{
    internal enum LocalBotMovementMode
    {
        Stationary = 0,
        NativeAI = 1,
        FollowPlayer = 2,
        MoveToPoint = 3,
        Wander = 4
    }

    internal sealed class LocalBotSpawnOptions
    {
        public string NamePrefix = "PathBot";
        public int TeamMode;
        public int MaxHealth = 5000;
        public int Shield;
        public float InvincibleSeconds;
        public int LocalDamagePerHit = 250;
        public float HeadshotMultiplier = 1.5f;
        public float RunSpeed = 6f;
        public float JumpHeight = 1.2f;
        public float EyesDistance = 100f;
        public float FollowDistance = 10f;
        public float AttackSpread = 2f;
        public float MaxWeaponUseTime = 10f;
        public float AttackDistance = 100f;
        public float WanderRadius = 8f;
        public LocalBotMovementMode MovementMode = LocalBotMovementMode.Stationary;
        public bool AllowAttack;
        public bool FacePlayer = true;
        public bool SnapToGround = true;
        public bool Targetable = true;
    }

    internal sealed class LocalBotRecord
    {
        public int Sequence;
        public byte CharacterUid;
        public int RobotUid;
        public string DisplayName;
        public LocalBotMovementMode MovementMode;
        public LocalBotMovementMode LastMovingMode = LocalBotMovementMode.NativeAI;
        public Vector3 MovementTarget;
        public Vector3 MovementAnchor;
        public float NextMovementUpdate;
        public float JumpReleaseTime;
        public float ActionMoveUntil;
        public LocalBotMovementMode AppliedMovementMode = (LocalBotMovementMode)(-1);
        public Vector3 LastManagedPosition;
        public float LastManagedProgressTime;
        public bool HasManagedTarget;
        public float RunSpeed;
        public float JumpHeight;
        public float EyesDistance;
        public float FollowDistance;
        public float AttackSpread;
        public float MaxWeaponUseTime;
        public float AttackDistance;
        public float WanderRadius;
        public bool AllowAttack;
        public bool Targetable;
        public int LocalDamagePerHit;
        public float HeadshotMultiplier;
        public bool AnimationLocked;
        public string ManualAnimation;
        public Character Character;
    }

    internal sealed class LocalBotAppearanceChoice
    {
        public string Label;
        public string Data;
        public string Resource;

        public LocalBotAppearanceChoice(string label, string data)
            : this(label, data, string.Empty)
        {
        }

        public LocalBotAppearanceChoice(string label, string data, string resource)
        {
            Label = label;
            Data = data;
            Resource = resource;
        }
    }

    internal sealed class LocalBotWeaponChoice
    {
        public string Label;
        public string Resource;
        public string DisplayKey;
        public byte SubType;

        public LocalBotWeaponChoice(string label, string resource, string displayKey, byte subType)
        {
            Label = label;
            Resource = resource;
            DisplayKey = displayKey;
            SubType = subType;
        }
    }

    internal static class LocalBotManager
    {
        private const int MaxLocalBots = 16;
        private const int FirstLocalUid = 239;
        private const int LastLocalUid = 200;
        private const int LocalRobotUidBase = 910000;
        private static readonly List<LocalBotRecord> Bots = new List<LocalBotRecord>();
        private static readonly MethodInfo MemberwiseCloneMethod = typeof(object).GetMethod(
            "MemberwiseClone", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly MethodInfo SetPartolPositionMethod = typeof(RobotControl).GetMethod(
            "SetPartolPosition", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo HpAlphaField = typeof(Character).GetField(
            "hpAplha", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly string[] AppearancePartNames =
        {
            "皮肤", "眼睛", "嘴巴", "鼻子", "耳朵", "胡须", "头发", "头盔",
            "内衣", "上衣", "裤子", "手套", "鞋子", "贴花", "动态饰品",
            "固定饰品", "上层固定饰品", "下层固定饰品"
        };
        private static readonly List<LocalBotWeaponChoice> WeaponCatalog = new List<LocalBotWeaponChoice>();
        private static readonly List<LocalBotAppearanceChoice>[] AppearanceCatalog = CreateAppearanceCatalogs();
        private static readonly Dictionary<string, string> KnownWeaponDisplayKeys = CreateKnownWeaponDisplayKeys();
        private static bool _resourceCatalogLoaded;

        private static Character _sessionPlayer;
        private static int _nextSequence = 1;
        private static string _lastStatus = "等待进入战斗";

        internal static string LastStatus
        {
            get { return _lastStatus; }
        }

        internal static int Count
        {
            get { PruneDestroyed(); return Bots.Count; }
        }

        internal static List<LocalBotRecord> GetSnapshot()
        {
            PruneDestroyed();
            return new List<LocalBotRecord>(Bots);
        }

        internal static void Tick(Level level, Character player)
        {
            PruneDestroyed();

            if (player == null || level == null)
            {
                if (Bots.Count > 0) RemoveAll("leave_fight");
                _sessionPlayer = null;
                return;
            }

            if (_sessionPlayer != null && _sessionPlayer != player)
            {
                RemoveAll("player_changed");
            }
            _sessionPlayer = player;

            for (int i = 0; i < Bots.Count; i++)
            {
                LocalBotRecord record = Bots[i];
                RobotControl robot = record.Character as RobotControl;
                if (robot == null) continue;

                // Local test robots must never become network-owned robots.
                try { RobotManager.Instance.RemoveRobot(robot); } catch { }
                try { robot.SetAttackCheckDistance(record.AllowAttack ? record.AttackDistance : -1f); } catch { }
                UpdateMovement(record, robot, player);

                if (record.JumpReleaseTime > 0f && Time.time >= record.JumpReleaseTime)
                {
                    record.JumpReleaseTime = 0f;
                    try
                    {
                        robot.motor1.SetJump(false);
                        robot.SetAnimationState(8, false);
                    }
                    catch { }
                }
            }

            UpdateTargetedHealthBar(player);
        }

        internal static bool TrySpawn(
            Level level,
            Character player,
            Vector3 position,
            LocalBotSpawnOptions options,
            out LocalBotRecord record)
        {
            record = null;
            PruneDestroyed();

            if (level == null || player == null)
                return Fail("只能在已加载角色的战斗场景生成");
            if (GameApp.Instance == null || GameApp.Instance.baseobj == null)
                return Fail("角色基础 Prefab 尚未就绪");
            if (CharacterManager.Instance == null || CharacterManager.Instance.character_set == null)
                return Fail("CharacterManager 尚未就绪");
            if (Bots.Count >= MaxLocalBots)
                return Fail("本地 Bot 已达到上限 " + MaxLocalBots);
            if (options == null)
                return Fail("生成配置为空");

            byte uid;
            if (!TryAllocateCharacterUid(level, player, out uid))
                return Fail("没有可用的本地角色 UID");

            Vector3 spawnPosition = position;
            if (options.SnapToGround)
            {
                Vector3 grounded;
                if (TryProjectToGround(position, out grounded)) spawnPosition = grounded;
            }

            GameObject gameObject = null;
            Character bot = null;
            try
            {
                gameObject = GameApp.Instance.getBaseMode(false, CharacterControlType.Robot);
                if (gameObject == null) return Fail("创建角色 Prefab 失败");
                gameObject.SetActive(false);

                bot = gameObject.GetComponent<Character>();
                if (bot == null)
                {
                    UnityEngine.Object.Destroy(gameObject);
                    return Fail("角色 Prefab 缺少 Character 组件");
                }

                int sequence = _nextSequence++;
                int robotUid = LocalRobotUidBase + sequence;
                int team = ResolveTeam(player, options.TeamMode);
                string displayName = BuildDisplayName(options.NamePrefix, sequence);

                CharacterInfoData info;
                string infoError;
                if (!TryBuildCharacterInfo(player, uid, team, displayName, options, out info, out infoError))
                {
                    UnityEngine.Object.Destroy(gameObject);
                    return Fail(infoError);
                }

                bot.uid = uid;
                bot.robot_uid = robotUid;
                bot.baseName = "BaseBody" + uid;
                bot.hpName = "BaseBodyHP" + uid;
                bot.hpBGName = "BaseBodyHPBG" + uid;
                bot.hpLabelName = "BaseBodyHPLabel" + uid;
                gameObject.name = bot.baseName;
                bot.SetTeam(team);
                bot.SetCharacterInfo(info);
                bot.InitBuff();
                bot.SetPhysxControl(false);
                bot.transform.position = new Vector3(-10000f, -10000f, -10000f);

                CharacterManager.Instance.AddCharacter(bot);
                try { RobotManager.Instance.RemoveRobot(bot); } catch { }

                bot.ready = true;
                bot.connected = true;
                bot.playing = true;
                bot.can_select = options.Targetable;
                bot.max_health = options.MaxHealth;
                bot.primary_max_health = options.MaxHealth;
                bot.InitializeObjectBase();

                Quaternion rotation = options.FacePlayer
                    ? FaceTowards(spawnPosition, player.transform.position)
                    : Quaternion.identity;
                RobotControl robot = bot as RobotControl;
                bot.Rebirth(options.MaxHealth, (short)options.Shield, spawnPosition, rotation);
                if (robot == null) bot.transform.position = spawnPosition;
                bot.transform.rotation = rotation;
                bot.can_select = options.Targetable;
                bot.invincible_time = Mathf.Max(0f, options.InvincibleSeconds);
                bot.ActivateObjects();

                if (robot != null)
                {
                    robot.SetAttackCheckDistance(options.AllowAttack
                        ? Mathf.Clamp(options.AttackDistance, 0.5f, 250f)
                        : -1f);
                    try { RobotManager.Instance.RemoveRobot(robot); } catch { }
                }

                LocalBotRecord created = new LocalBotRecord();
                created.Sequence = sequence;
                created.CharacterUid = uid;
                created.RobotUid = robotUid;
                created.DisplayName = displayName;
                created.MovementMode = options.MovementMode;
                created.LastMovingMode = options.MovementMode == LocalBotMovementMode.Stationary
                    ? LocalBotMovementMode.NativeAI
                    : options.MovementMode;
                created.MovementTarget = spawnPosition;
                created.MovementAnchor = spawnPosition;
                created.RunSpeed = Mathf.Clamp(options.RunSpeed, 0.5f, 20f);
                created.JumpHeight = Mathf.Clamp(options.JumpHeight, 0f, 8f);
                created.EyesDistance = Mathf.Clamp(options.EyesDistance, 1f, 250f);
                created.FollowDistance = Mathf.Clamp(options.FollowDistance, 0.5f, 100f);
                created.AttackSpread = Mathf.Clamp(options.AttackSpread, 0f, 30f);
                created.MaxWeaponUseTime = Mathf.Clamp(options.MaxWeaponUseTime, 0.5f, 120f);
                created.AttackDistance = Mathf.Clamp(options.AttackDistance, 0.5f, 250f);
                created.WanderRadius = Mathf.Clamp(options.WanderRadius, 1f, 80f);
                created.AllowAttack = options.AllowAttack;
                created.Targetable = options.Targetable;
                created.LocalDamagePerHit = Mathf.Clamp(options.LocalDamagePerHit, 1, 1000000);
                created.HeadshotMultiplier = Mathf.Clamp(options.HeadshotMultiplier, 1f, 10f);
                created.Character = bot;
                created.LastManagedPosition = spawnPosition;
                created.LastManagedProgressTime = Time.time;
                Bots.Add(created);
                record = created;

                _lastStatus = "已生成 " + displayName + " uid=" + uid + " pos=" + FormatVector(bot.transform.position);
                FileLogger.Log("LOCAL-BOT", _lastStatus + " movement=" + options.MovementMode + " team=" + team);
                return true;
            }
            catch (Exception e)
            {
                try
                {
                    if (bot != null)
                    {
                        RobotManager.Instance.RemoveRobot(bot);
                        if (CharacterManager.Instance.character_set.Contains(bot))
                            CharacterManager.Instance.RemoveCharacter(bot.uid);
                        else
                            UnityEngine.Object.Destroy(bot.gameObject);
                    }
                    else if (gameObject != null)
                    {
                        UnityEngine.Object.Destroy(gameObject);
                    }
                }
                catch { }
                return Fail("生成失败: " + e.GetType().Name + " " + e.Message);
            }
        }

        internal static bool TryApplySettings(
            LocalBotRecord record,
            Character player,
            string displayName,
            LocalBotSpawnOptions options)
        {
            Character bot = record == null ? null : record.Character;
            if (bot == null || options == null) return Fail("未选择有效 Bot");

            try
            {
                string safeName = NormalizeDisplayName(displayName, record.Sequence);
                int team = ResolveTeam(player, options.TeamMode);
                CharacterInfoData info = bot.character_info;

                record.DisplayName = safeName;
                record.Targetable = options.Targetable;
                record.LocalDamagePerHit = Mathf.Clamp(options.LocalDamagePerHit, 1, 1000000);
                record.HeadshotMultiplier = Mathf.Clamp(options.HeadshotMultiplier, 1f, 10f);
                record.RunSpeed = Mathf.Clamp(options.RunSpeed, 0.5f, 20f);
                record.JumpHeight = Mathf.Clamp(options.JumpHeight, 0f, 8f);
                record.EyesDistance = Mathf.Clamp(options.EyesDistance, 1f, 250f);
                record.FollowDistance = Mathf.Clamp(options.FollowDistance, 0.5f, 100f);
                record.AttackSpread = Mathf.Clamp(options.AttackSpread, 0f, 30f);
                record.MaxWeaponUseTime = Mathf.Clamp(options.MaxWeaponUseTime, 0.5f, 120f);
                record.AttackDistance = Mathf.Clamp(options.AttackDistance, 0.5f, 250f);
                record.WanderRadius = Mathf.Clamp(options.WanderRadius, 1f, 80f);
                record.AllowAttack = options.AllowAttack;

                bot.SetTeam(team);
                bot.can_select = record.Targetable;
                bot.max_health = Mathf.Max(1, options.MaxHealth);
                bot.primary_max_health = bot.max_health;
                if (info != null)
                {
                    info.name = safeName;
                    info.team = (byte)team;
                    info.max_health = bot.max_health;
                    info.run_speed = record.RunSpeed;
                    info.primary_run_speed = record.RunSpeed;
                    info.jump_height = record.JumpHeight;
                    info.primary_jump_height = record.JumpHeight;
                    info.jump_velocity = record.JumpHeight > 0f ? Mathf.Sqrt(record.JumpHeight * 39.2f) : 0f;
                    info.eyes_distance = record.EyesDistance;
                    info.follow_distance = record.FollowDistance;
                    info.attack_spread = record.AttackSpread;
                    info.max_weapon_use_time = record.MaxWeaponUseTime;
                }
                if (bot.motor1 != null)
                {
                    bot.motor1.SetRunSpeed(record.RunSpeed);
                    if (record.JumpHeight > 0f)
                    {
                        bot.motor1.SetJumpHeight(record.JumpHeight);
                    }
                    else
                    {
                        bot.motor1.move_info.jump_height = 0f;
                        bot.motor1.move_info.jump_velocity = 0f;
                    }
                }

                RobotControl robot = bot as RobotControl;
                if (robot != null)
                    robot.SetAttackCheckDistance(record.AllowAttack ? record.AttackDistance : -1f);

                TrySetMovement(record, options.MovementMode, record.MovementTarget, false);
                _lastStatus = "已应用 " + safeName;
                FileLogger.Log("LOCAL-BOT", _lastStatus);
                return true;
            }
            catch (Exception e)
            {
                return Fail("应用配置失败: " + e.Message);
            }
        }

        internal static bool TrySetMovement(
            LocalBotRecord record,
            LocalBotMovementMode mode,
            Vector3 target,
            bool updateStatus)
        {
            RobotControl robot = record == null ? null : record.Character as RobotControl;
            if (robot == null) return Fail("未选择有效 Bot");

            record.MovementMode = mode;
            if (mode != LocalBotMovementMode.Stationary) record.LastMovingMode = mode;
            record.MovementTarget = target;
            if (mode == LocalBotMovementMode.Wander) record.MovementAnchor = target;
            record.NextMovementUpdate = 0f;
            record.AppliedMovementMode = (LocalBotMovementMode)(-1);
            record.HasManagedTarget = mode == LocalBotMovementMode.FollowPlayer || mode == LocalBotMovementMode.MoveToPoint;
            record.LastManagedPosition = robot.transform.position;
            record.LastManagedProgressTime = Time.time;

            try
            {
                UpdateMovement(record, robot, CurrentPlayer());
                if (updateStatus)
                {
                    _lastStatus = record.DisplayName + " 移动=" + MovementName(mode);
                    FileLogger.Log("LOCAL-BOT", _lastStatus);
                }
                return true;
            }
            catch (Exception e)
            {
                return Fail("切换移动失败: " + e.Message);
            }
        }

        internal static bool TryCopyPlayerAppearance(LocalBotRecord record, Character player)
        {
            Character bot = record == null ? null : record.Character;
            CharacterInfoData source = player == null ? null : player.character_info;
            CharacterInfoData info = bot == null ? null : bot.character_info;
            if (bot == null || source == null || info == null) return Fail("角色外观尚未就绪");

            try
            {
                info.avatarId = source.avatarId ?? string.Empty;
                info.avatar_part = CopyStringArray(source.avatar_part, 18);
                info.temp_avatar = CopyStringArray(source.temp_avatar, 18);
                info.gesture = CopyStringArray(source.gesture, 6);
                info.independ_info = CopyStringArray(source.independ_info, 5);
                info.primary_independ_info = CopyStringArray(source.primary_independ_info, 5);
                info.wing_params = source.wing_params == null ? new float[0] : (float[])source.wing_params.Clone();
                info.gender = source.gender;
                info.career = source.career;
                RefreshAppearance(bot, record);
                _lastStatus = "已复制玩家外观";
                FileLogger.Log("LOCAL-BOT", _lastStatus + " bot=" + record.DisplayName);
                return true;
            }
            catch (Exception e)
            {
                return Fail("复制外观失败: " + e.Message);
            }
        }

        internal static bool TryApplyAppearance(
            LocalBotRecord record,
            string avatarId,
            int partIndex,
            string partData)
        {
            Character bot = record == null ? null : record.Character;
            CharacterInfoData info = bot == null ? null : bot.character_info;
            if (bot == null || info == null) return Fail("未选择有效 Bot");

            try
            {
                if (avatarId != null) info.avatarId = avatarId.Trim();
                if (partIndex >= 0 && partIndex < 18)
                {
                    if (info.avatar_part == null || info.avatar_part.Length != 18)
                        info.avatar_part = CopyStringArray(info.avatar_part, 18);
                    info.avatar_part[partIndex] = partData ?? string.Empty;
                }
                RefreshAppearance(bot, record);
                _lastStatus = "已更新外观";
                FileLogger.Log("LOCAL-BOT", _lastStatus + " bot=" + record.DisplayName + " part=" + partIndex);
                return true;
            }
            catch (Exception e)
            {
                return Fail("更新外观失败: " + e.Message);
            }
        }

        internal static bool TryCopyPlayerWeapons(LocalBotRecord record, Character player)
        {
            Character bot = record == null ? null : record.Character;
            if (bot == null || player == null) return Fail("未选择有效 Bot");

            try
            {
                SlotInfo slots = CreateEmptySlots();
                SlotInfo primary = CreateEmptySlots();
                int copied = ClonePlayerWeapons(player, slots, primary);
                if (copied == 0) return Fail("玩家武器栏为空");

                bot.character_info.slots_info = slots;
                bot.character_info.primary_slots_info = primary;
                bot.need_change = true;
                bot.InitializeObjectBase();
                bot.need_change = false;
                bot.ActivateObjects();
                _lastStatus = "已复制武器栏 " + copied;
                FileLogger.Log("LOCAL-BOT", _lastStatus + " bot=" + record.DisplayName);
                return true;
            }
            catch (Exception e)
            {
                return Fail("复制武器失败: " + e.Message);
            }
        }

        internal static bool TryCycleWeapon(LocalBotRecord record, int direction)
        {
            Character bot = record == null ? null : record.Character;
            if (bot == null || bot.weaponlist == null || bot.weaponlist.Count == 0)
                return Fail("Bot 没有可切换武器");

            try
            {
                int index = bot.mWeapon == null ? 0 : bot.weaponlist.IndexOf(bot.mWeapon);
                if (index < 0) index = 0;
                index = (index + (direction < 0 ? -1 : 1) + bot.weaponlist.Count) % bot.weaponlist.Count;
                bot.ChangeWeapon(bot.weaponlist[index]);
                _lastStatus = "武器 " + CurrentWeaponName(record);
                return true;
            }
            catch (Exception e)
            {
                return Fail("切换武器失败: " + e.Message);
            }
        }

        internal static string CurrentWeaponName(LocalBotRecord record)
        {
            try
            {
                WeaponBase weapon = record == null || record.Character == null ? null : record.Character.mWeapon;
                if (weapon == null || weapon.info == null) return "无";
                string resource = weapon.info.name;
                EnsureResourceCatalogs();
                for (int i = 0; i < WeaponCatalog.Count; i++)
                    if (string.Equals(WeaponCatalog[i].Resource, resource, StringComparison.OrdinalIgnoreCase))
                        return WeaponCatalog[i].Label;
                return string.IsNullOrEmpty(weapon.info.display_name)
                    ? (string.IsNullOrEmpty(resource) ? weapon.GetType().Name : resource)
                    : SafeLocalizedName(weapon.info.display_name);
            }
            catch { return "无"; }
        }

        internal static List<string> GetWeaponChoices(LocalBotRecord record)
        {
            EnsureResourceCatalogs();
            List<string> result = new List<string>();
            for (int i = 0; i < WeaponCatalog.Count; i++) result.Add(WeaponCatalog[i].Label);
            return result;
        }

        internal static int CurrentWeaponIndex(LocalBotRecord record)
        {
            EnsureResourceCatalogs();
            Character bot = record == null ? null : record.Character;
            string resource = bot == null || bot.mWeapon == null || bot.mWeapon.info == null
                ? string.Empty
                : bot.mWeapon.info.name;
            for (int i = 0; i < WeaponCatalog.Count; i++)
                if (string.Equals(WeaponCatalog[i].Resource, resource, StringComparison.OrdinalIgnoreCase)) return i;
            return 0;
        }

        internal static bool TrySelectWeapon(LocalBotRecord record, int index)
        {
            EnsureResourceCatalogs();
            Character bot = record == null ? null : record.Character;
            if (bot == null || bot.character_info == null || WeaponCatalog.Count == 0)
                return Fail("Bot 没有可选择武器");
            index = Mathf.Clamp(index, 0, WeaponCatalog.Count - 1);
            try
            {
                LocalBotWeaponChoice choice = WeaponCatalog[index];
                ObjectBaseInfo weaponInfo = CreateWeaponInfo(choice);
                if (weaponInfo == null) return Fail("不支持该武器: " + choice.Resource);

                SlotInfo slots = CreateEmptySlots();
                SlotInfo primary = CreateEmptySlots();
                slots.object_info[0] = weaponInfo;
                primary.object_info[0] = CloneWeaponInfo(weaponInfo);
                bot.character_info.slots_info = slots;
                bot.character_info.primary_slots_info = primary;
                bot.need_change = true;
                bot.InitializeObjectBase();
                bot.need_change = false;
                bot.ActivateObjects();
                if (bot.weaponlist != null && bot.weaponlist.Count > 0) bot.ChangeWeapon(bot.weaponlist[0]);
                _lastStatus = "武器 " + CurrentWeaponName(record);
                FileLogger.Log("LOCAL-BOT", _lastStatus + " resource=" + choice.Resource + " subtype=" + choice.SubType);
                return true;
            }
            catch (Exception e) { return Fail("切换武器失败: " + e.Message); }
        }

        internal static List<string> GetAnimationChoices(LocalBotRecord record)
        {
            List<string> result = new List<string>();
            AddUnique(result, "idle");
            AddUnique(result, "run");
            AddUnique(result, "jump");
            AddUnique(result, "spurt");
            AddUnique(result, "rollforward");
            AddUnique(result, "hit");

            Character bot = record == null ? null : record.Character;
            if (bot == null) return result;
            try
            {
                Animation[] animations = bot.GetComponentsInChildren<Animation>(true);
                for (int i = 0; i < animations.Length; i++)
                {
                    foreach (AnimationState state in animations[i])
                    {
                        if (state != null) AddUnique(result, state.name);
                    }
                }
            }
            catch { }
            result.Sort(StringComparer.OrdinalIgnoreCase);
            return result;
        }

        internal static string[] GetAppearancePartNames()
        {
            return (string[])AppearancePartNames.Clone();
        }

        internal static List<LocalBotAppearanceChoice> GetAppearanceChoices(int partIndex)
        {
            EnsureResourceCatalogs();
            List<LocalBotAppearanceChoice> result = new List<LocalBotAppearanceChoice>();
            result.Add(new LocalBotAppearanceChoice("无", string.Empty, string.Empty));
            if (partIndex < 0 || partIndex >= AppearancePartNames.Length) return result;
            result.AddRange(AppearanceCatalog[partIndex]);
            return result;
        }

        internal static string GetAppearanceResource(LocalBotRecord record, int partIndex)
        {
            return ExtractAppearanceResource(GetAppearanceData(record, partIndex));
        }

        internal static bool TryApplyAppearanceChoice(LocalBotRecord record, int partIndex, LocalBotAppearanceChoice choice)
        {
            if (choice == null) return false;
            if (string.IsNullOrEmpty(choice.Resource))
                return TryApplyAppearance(record, null, partIndex, choice.Data ?? string.Empty);

            string current = GetAppearanceData(record, partIndex);
            string template = current;
            if (string.IsNullOrEmpty(template)) template = FindAppearanceTemplate(partIndex);
            string updated = ReplaceAppearanceResource(template, partIndex, choice.Resource);
            return TryApplyAppearance(record, null, partIndex, updated);
        }

        internal static string GetAppearanceData(LocalBotRecord record, int partIndex)
        {
            CharacterInfoData info = record == null || record.Character == null ? null : record.Character.character_info;
            if (info == null || info.avatar_part == null || partIndex < 0 || partIndex >= info.avatar_part.Length)
                return string.Empty;
            return info.avatar_part[partIndex] ?? string.Empty;
        }

        internal static bool TryGetAppearanceOffset(LocalBotRecord record, int partIndex, out Vector3 offset)
        {
            offset = Vector3.zero;
            string data = GetAppearanceData(record, partIndex);
            string[] tokens;
            int start;
            int end;
            if (!TryReadFirstLuaObject(data, out tokens, out start, out end)) return false;
            int xIndex;
            int yIndex;
            int zIndex;
            if (!GetOffsetTokenIndices(partIndex, out xIndex, out yIndex, out zIndex)) return false;
            float x;
            float y;
            float z = 0f;
            if (xIndex >= tokens.Length || yIndex >= tokens.Length ||
                !float.TryParse(tokens[xIndex], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out x) ||
                !float.TryParse(tokens[yIndex], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out y))
                return false;
            if (zIndex >= 0 && (zIndex >= tokens.Length ||
                !float.TryParse(tokens[zIndex], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out z)))
                return false;
            offset = new Vector3(x, y, z);
            return true;
        }

        internal static bool TryApplyAppearanceOffset(LocalBotRecord record, int partIndex, Vector3 offset)
        {
            string data = GetAppearanceData(record, partIndex);
            string[] tokens;
            int start;
            int end;
            if (!TryReadFirstLuaObject(data, out tokens, out start, out end))
                return Fail("当前部位没有可编辑坐标");
            int xIndex;
            int yIndex;
            int zIndex;
            if (!GetOffsetTokenIndices(partIndex, out xIndex, out yIndex, out zIndex))
                return Fail("该部位由骨骼固定，不包含坐标");

            tokens[xIndex] = offset.x.ToString("0.######", System.Globalization.CultureInfo.InvariantCulture);
            tokens[yIndex] = offset.y.ToString("0.######", System.Globalization.CultureInfo.InvariantCulture);
            if (zIndex >= 0) tokens[zIndex] = offset.z.ToString("0.######", System.Globalization.CultureInfo.InvariantCulture);
            string replacement = "{" + string.Join(",", tokens) + "}";
            string updated = data.Substring(0, start) + replacement + data.Substring(end + 1);
            return TryApplyAppearance(record, null, partIndex, updated);
        }

        internal static bool TryJump(LocalBotRecord record)
        {
            Character bot = record == null ? null : record.Character;
            if (bot == null || bot.motor1 == null) return Fail("未选择有效 Bot");
            try
            {
                SetManualAnimationLock(bot, false);
                record.AnimationLocked = false;
                record.ManualAnimation = string.Empty;
                bot.motor1.SetJump(true);
                bot.SetAnimationState(8, true);
                record.JumpReleaseTime = Time.time + 0.1f;
                if (record.MovementMode == LocalBotMovementMode.Stationary)
                    record.ActionMoveUntil = Time.time + 0.55f;
                _lastStatus = "跳跃";
                return true;
            }
            catch (Exception e) { return Fail("跳跃失败: " + e.Message); }
        }

        internal static bool TrySpurt(LocalBotRecord record)
        {
            Character bot = record == null ? null : record.Character;
            if (bot == null) return Fail("未选择有效 Bot");
            try
            {
                SetManualAnimationLock(bot, false);
                record.AnimationLocked = false;
                record.ManualAnimation = string.Empty;
                bot.SetSpurt(0.35f, false);
                record.ActionMoveUntil = Time.time + 1.2f;
                _lastStatus = "滑步";
                return true;
            }
            catch (Exception e) { return Fail("滑步失败: " + e.Message); }
        }

        internal static bool TryFacePlayer(LocalBotRecord record, Character player)
        {
            Character bot = record == null ? null : record.Character;
            if (bot == null || player == null) return Fail("角色尚未就绪");
            try
            {
                bot.transform.rotation = FaceTowards(bot.transform.position, player.transform.position);
                bot.SetLookDir(bot.transform.eulerAngles);
                _lastStatus = "已朝向玩家";
                return true;
            }
            catch (Exception e) { return Fail("朝向失败: " + e.Message); }
        }

        internal static bool TryPlayAnimation(LocalBotRecord record, string animation)
        {
            Character bot = record == null ? null : record.Character;
            string name = string.IsNullOrEmpty(animation) ? "idle" : animation.Trim();
            if (bot == null) return Fail("未选择有效 Bot");
            try
            {
                SetManualAnimationLock(bot, true);
                bot.PlayAnimation(name, -1f);
                record.AnimationLocked = true;
                record.ManualAnimation = name;
                _lastStatus = "动作 " + name;
                return true;
            }
            catch (Exception e) { return Fail("动作失败: " + e.Message); }
        }

        internal static bool TryStopAnimation(LocalBotRecord record, string animation)
        {
            Character bot = record == null ? null : record.Character;
            if (bot == null) return Fail("未选择有效 Bot");
            try
            {
                if (!string.IsNullOrEmpty(animation)) bot.StopAnimation(animation.Trim());
                if (bot.avatar != null) bot.avatar.resetPose();
                record.ManualAnimation = string.Empty;
                if (record.MovementMode == LocalBotMovementMode.Stationary)
                {
                    SetManualAnimationLock(bot, true);
                    bot.PlayAnimation("idle", -1f);
                    record.AnimationLocked = true;
                }
                else
                {
                    SetManualAnimationLock(bot, false);
                    record.AnimationLocked = false;
                }
                _lastStatus = "动作复位";
                return true;
            }
            catch (Exception e) { return Fail("动作复位失败: " + e.Message); }
        }

        internal static bool TryReload(LocalBotRecord record)
        {
            WeaponBase weapon = record == null || record.Character == null ? null : record.Character.mWeapon;
            if (weapon == null) return Fail("Bot 没有武器");
            try
            {
                weapon.Reload();
                _lastStatus = "装弹";
                return true;
            }
            catch (Exception e) { return Fail("装弹失败: " + e.Message); }
        }

        internal static bool TryMove(LocalBotRecord record, Vector3 position, bool snapToGround, bool facePlayer)
        {
            Character bot = record == null ? null : record.Character;
            if (bot == null) return Fail("未选择有效 Bot");

            Vector3 destination = position;
            if (snapToGround)
            {
                Vector3 grounded;
                if (TryProjectToGround(position, out grounded)) destination = grounded;
            }

            try
            {
                Character player = ASSingleton<Level>.Instance == null ? null : ASSingleton<Level>.Instance.GetPlayer();
                Quaternion rotation = facePlayer && player != null
                    ? FaceTowards(destination, player.transform.position)
                    : bot.transform.rotation;
                int health = Mathf.Max(1, bot.hp);
                RobotControl robot = bot as RobotControl;
                bot.Rebirth(health, bot.shield, destination, rotation);
                if (robot == null) bot.transform.position = destination;
                bot.transform.rotation = rotation;
                bot.can_select = record.Targetable;
                bot.ready = true;
                bot.connected = true;
                bot.playing = true;
                bot.ActivateObjects();

                if (robot != null)
                {
                    robot.SetAttackCheckDistance(record.AllowAttack ? record.AttackDistance : -1f);
                    try { RobotManager.Instance.RemoveRobot(robot); } catch { }
                }

                record.MovementTarget = destination;
                record.MovementAnchor = destination;
                record.NextMovementUpdate = 0f;

                _lastStatus = "已移动 " + record.DisplayName + " 到 " + FormatVector(bot.transform.position);
                FileLogger.Log("LOCAL-BOT", _lastStatus);
                return true;
            }
            catch (Exception e)
            {
                return Fail("移动失败: " + e.Message);
            }
        }

        internal static bool TryRestore(LocalBotRecord record, int health, int shield, float invincibleSeconds)
        {
            Character bot = record == null ? null : record.Character;
            if (bot == null) return Fail("未选择有效 Bot");

            try
            {
                Vector3 position = bot.transform.position;
                Quaternion rotation = bot.transform.rotation;
                RobotControl robot = bot as RobotControl;
                Vector3 rebirthPosition = robot == null ? position : position - Vector3.up;
                bot.max_health = Mathf.Max(1, health);
                bot.primary_max_health = bot.max_health;
                bot.Rebirth(bot.max_health, (short)Mathf.Clamp(shield, 0, short.MaxValue), rebirthPosition, rotation);
                bot.transform.position = position;
                bot.can_select = record.Targetable;
                bot.invincible_time = Mathf.Max(0f, invincibleSeconds);
                bot.ready = true;
                bot.connected = true;
                bot.playing = true;
                bot.ActivateObjects();

                if (robot != null)
                {
                    robot.SetAttackCheckDistance(record.AllowAttack ? record.AttackDistance : -1f);
                    try { RobotManager.Instance.RemoveRobot(robot); } catch { }
                }

                _lastStatus = "已恢复 " + record.DisplayName;
                FileLogger.Log("LOCAL-BOT", _lastStatus);
                return true;
            }
            catch (Exception e)
            {
                return Fail("恢复失败: " + e.Message);
            }
        }

        internal static bool Remove(LocalBotRecord record)
        {
            if (record == null) return Fail("未选择 Bot");
            Bots.Remove(record);
            DestroyRecord(record);
            _lastStatus = "已移除 " + (record.DisplayName ?? "Bot");
            FileLogger.Log("LOCAL-BOT", _lastStatus);
            return true;
        }

        internal static void RemoveAll(string reason)
        {
            for (int i = Bots.Count - 1; i >= 0; i--)
            {
                DestroyRecord(Bots[i]);
            }
            Bots.Clear();
            _lastStatus = "已清理全部本地 Bot" + (string.IsNullOrEmpty(reason) ? string.Empty : " (" + reason + ")");
            FileLogger.Log("LOCAL-BOT", _lastStatus);
        }

        internal static bool Contains(Character character)
        {
            if (character == null) return false;
            PruneDestroyed();
            for (int i = 0; i < Bots.Count; i++)
            {
                if (Bots[i].Character == character) return true;
            }
            return false;
        }

        internal static bool IsLocalCharacterUid(int uid)
        {
            PruneDestroyed();
            if (uid < byte.MinValue || uid > byte.MaxValue) return false;
            byte value = (byte)uid;
            for (int i = 0; i < Bots.Count; i++)
            {
                if (Bots[i].CharacterUid == value) return true;
            }
            return false;
        }

        internal static bool TryApplyShot(HitMessage hitMessage)
        {
            if (hitMessage == null) return false;

            LocalBotRecord target = FindByCharacterUid(hitMessage.uid);
            if (target == null) return false;

            Character attacker = ResolveShotAttacker(hitMessage);
            if (attacker != null &&
                (attacker.mWeapon is BowController || attacker.mWeapon is RPGController || attacker.mWeapon is GrenadeController))
            {
                // Projectile weapons are applied at ArrowHit/GrenadeHurt, not when the projectile is launched.
                return true;
            }

            int part = Mathf.Clamp((int)hitMessage.part, 0, 18);
            return ApplyLocalDamage(target, attacker, target.LocalDamagePerHit, part, 2, part == 4, "shot");
        }

        internal static bool TryApplyArrowHit(byte targetUid, byte part, Vector3 hitPosition)
        {
            LocalBotRecord target = FindByCharacterUid(targetUid);
            if (target == null) return false;

            Character attacker = CurrentPlayer();
            int safePart = Mathf.Clamp((int)part, 0, 18);
            return ApplyLocalDamage(target, attacker, target.LocalDamagePerHit, safePart, 2, safePart == 4, "arrow");
        }

        internal static bool TryApplyExplosionHit(Character attacker, byte targetUid, bool halfDamage, Vector3 hitPosition)
        {
            LocalBotRecord target = FindByCharacterUid(targetUid);
            if (target == null) return false;

            int damage = target.LocalDamagePerHit;
            if (halfDamage) damage = Mathf.Max(1, Mathf.RoundToInt(damage * 0.5f));
            return ApplyLocalDamage(target, attacker ?? CurrentPlayer(), damage, 0, 3, false, "explosion");
        }

        internal static bool IsLocalRobotUid(int robotUid)
        {
            PruneDestroyed();
            for (int i = 0; i < Bots.Count; i++)
            {
                if (Bots[i].RobotUid == robotUid) return true;
            }
            return false;
        }

        internal static bool ShouldSuppressShot(HitMessage hitMessage)
        {
            if (hitMessage == null || Bots.Count == 0) return false;

            try
            {
                if (IsLocalRobotUid((int)hitMessage.robot_uid)) return true;
                if (IsLocalCharacterUid(hitMessage.uid)) return true;
                if (IsLocalCharacterUid((byte)hitMessage.aim_target_uid)) return true;

                Level level = ASSingleton<Level>.Instance;
                Character player = level == null ? null : level.GetPlayer();
                if (player != null && IsLocalCharacterUid(hitMessage.uid ^ player.currentSpreadIndex)) return true;

                Character autoTarget = AutoBattleManager.CurrentTarget;
                if (autoTarget != null && Contains(autoTarget)) return true;
            }
            catch { }
            return false;
        }

        private static bool ApplyLocalDamage(
            LocalBotRecord record,
            Character attacker,
            int baseDamage,
            int part,
            byte hitType,
            bool headshot,
            string source)
        {
            Character target = record == null ? null : record.Character;
            if (target == null || target.IsDied) return false;

            if (target.invincible_time > 0.01f)
            {
                _lastStatus = "命中 " + record.DisplayName + "，当前处于无敌时间";
                FileLogger.Log("LOCAL-BOT", _lastStatus + " source=" + source);
                return true;
            }

            int damage = Mathf.Max(1, baseDamage);
            if (headshot)
                damage = Mathf.Max(1, Mathf.RoundToInt(damage * Mathf.Clamp(record.HeadshotMultiplier, 1f, 10f)));

            int oldHp = Mathf.Max(0, target.hp);
            int oldShield = Mathf.Max(0, (int)target.shield);
            int shieldDamage = Mathf.Min(oldShield, damage);
            int hpDamage = Mathf.Min(oldHp, Mathf.Max(0, damage - shieldDamage));
            int newShield = oldShield - shieldDamage;
            int newHp = oldHp - hpDamage;
            byte safePart = (byte)Mathf.Clamp(part, 0, 18);
            byte shotState = headshot ? (byte)2 : (byte)0;

            try
            {
                ChannelConnection connection = GameApp.Instance == null ? null : GameApp.Instance.channel_connection;
                if (connection != null && attacker != null)
                {
                    connection.TakeEffect(
                        newHp,
                        (short)Mathf.Clamp(newShield, 0, short.MaxValue),
                        attacker,
                        target,
                        hitType,
                        shotState,
                        safePart,
                        attacker.mWeapon);
                }
                else
                {
                    HitInfo info = BuildHitInfo(attacker, target, newHp, newShield, hitType, shotState, safePart);
                    target.HealthChange(info);
                    if (newHp <= 0 && !target.IsDied) target.Die(info);
                }

                _lastStatus = "本地命中 " + record.DisplayName + " -" + damage +
                    " HP=" + target.hp + " Shield=" + target.shield + (target.IsDied ? " 已死亡" : string.Empty);
                FileLogger.Log("LOCAL-BOT", _lastStatus + " source=" + source + " part=" + safePart);
                return true;
            }
            catch (Exception e)
            {
                return Fail("本地伤害结算失败: " + e.GetType().Name + " " + e.Message);
            }
        }

        private static HitInfo BuildHitInfo(
            Character attacker,
            Character target,
            int hp,
            int shield,
            byte hitType,
            byte shotState,
            byte part)
        {
            HitInfo info = new HitInfo();
            info.hp = hp;
            info.shield = shield;
            info.type = hitType;
            info.shot_state = shotState;
            info.time = 1f;
            info.from_uid = attacker == null ? (byte)0 : attacker.uid;
            info.to_uid = target == null ? (byte)0 : target.uid;
            info.part = part;
            info.pos = attacker == null ? Vector3.zero : attacker.transform.position;
            if (attacker != null && target != null)
            {
                Vector3 direction = target.transform.position - attacker.transform.position;
                info.hitDir = direction.sqrMagnitude > 0.0001f ? direction.normalized : Vector3.forward;
            }
            else
            {
                info.hitDir = Vector3.forward;
            }
            return info;
        }

        private static Character ResolveShotAttacker(HitMessage hitMessage)
        {
            int robotUid = hitMessage == null ? 0 : (int)hitMessage.robot_uid;
            if (robotUid != 0)
            {
                for (int i = 0; i < Bots.Count; i++)
                {
                    if (Bots[i].RobotUid == robotUid) return Bots[i].Character;
                }
            }
            return CurrentPlayer();
        }

        private static Character CurrentPlayer()
        {
            try
            {
                Level level = ASSingleton<Level>.Instance;
                return level == null ? null : level.GetPlayer();
            }
            catch
            {
                return null;
            }
        }

        private static LocalBotRecord FindByCharacterUid(int uid)
        {
            if (uid < byte.MinValue || uid > byte.MaxValue) return null;
            byte value = (byte)uid;
            PruneDestroyed();
            for (int i = 0; i < Bots.Count; i++)
            {
                if (Bots[i].CharacterUid == value) return Bots[i];
            }
            return null;
        }

        internal static bool TryGetCrosshairPoint(Camera camera, out Vector3 point)
        {
            point = Vector3.zero;
            if (camera == null) return Fail("没有可用相机");

            try
            {
                Ray ray = camera.ScreenPointToRay(new Vector3(Screen.width * 0.5f, Screen.height * 0.5f, 0f));
                RaycastHit hit;
                if (!Physics.Raycast(ray, out hit, 250f)) return Fail("准星方向没有命中场景");

                point = hit.point;
                if (hit.normal.y < 0.35f)
                {
                    Vector3 wallSafe = hit.point - ray.direction.normalized * 0.75f;
                    Vector3 grounded;
                    if (TryProjectToGround(wallSafe, out grounded)) point = grounded;
                    else point = wallSafe;
                }
                else
                {
                    point += Vector3.up * 0.04f;
                }
                _lastStatus = "准星落点 " + FormatVector(point);
                return true;
            }
            catch (Exception e)
            {
                return Fail("准星检测失败: " + e.Message);
            }
        }

        internal static bool TryProjectToGround(Vector3 position, out Vector3 grounded)
        {
            grounded = position;
            try
            {
                int mask = LayerMask.GetMask(new string[] { "Terrarin" });
                RaycastHit hit;
                Vector3 origin = position + Vector3.up * 4f;
                bool found = mask != 0
                    ? Physics.Raycast(origin, Vector3.down, out hit, 16f, mask)
                    : Physics.Raycast(origin, Vector3.down, out hit, 16f);
                if (!found) return false;
                grounded = hit.point + Vector3.up * 0.04f;
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static bool TryBuildCharacterInfo(
            Character player,
            byte uid,
            int team,
            string displayName,
            LocalBotSpawnOptions options,
            out CharacterInfoData info,
            out string error)
        {
            info = null;
            error = string.Empty;
            CharacterInfoData source = player.character_info;
            if (source == null)
            {
                error = "玩家 CharacterInfo 尚未就绪";
                return false;
            }

            try
            {
                CharacterInfoData clone = new CharacterInfoData();
                clone.is_primary_card = source.is_primary_card;
                clone.run_speed = Mathf.Clamp(options.RunSpeed, 0.5f, 20f);
                clone.primary_run_speed = clone.run_speed;
                clone.run_acceleration = source.run_acceleration > 0f ? source.run_acceleration : 8f;
                clone.primary_run_acceleration = clone.run_acceleration;
                clone.roll_speed = source.roll_speed;
                clone.primary_roll_speed = source.primary_roll_speed;
                clone.roll_acceleration = source.roll_acceleration;
                clone.roll_start_frozen_time = source.roll_start_frozen_time;
                clone.jump_height = Mathf.Clamp(options.JumpHeight, 0f, 8f);
                clone.primary_jump_height = clone.jump_height;
                clone.jump_velocity = clone.jump_height > 0f ? Mathf.Sqrt(clone.jump_height * 39.2f) : 0f;
                clone.throw_velocity = source.throw_velocity;
                clone.shot_height = source.shot_height;
                clone.gender = source.gender;
                clone.character_id = 0x7FFF00000000UL + uid;
                clone.character_level = source.character_level;
                clone.rank_type = source.rank_type;
                clone.rank_level = source.rank_level;
                clone.ladderLevel = source.ladderLevel;
                clone.server_name = "LOCAL";
                clone.vip_level = source.vip_level;
                clone.name = displayName;
                clone.team = (byte)team;
                clone.is_haspet = false;
                clone.eyes_distance = Mathf.Clamp(options.EyesDistance, 1f, 250f);
                clone.attack_spread = Mathf.Clamp(options.AttackSpread, 0f, 30f);
                clone.follow_distance = Mathf.Clamp(options.FollowDistance, 0.5f, 100f);
                clone.max_weapon_use_time = Mathf.Clamp(options.MaxWeaponUseTime, 0.5f, 120f);
                clone.career = source.career;
                clone.max_health = Mathf.Max(1, options.MaxHealth);
                clone.avatarId = source.avatarId ?? string.Empty;
                clone.cool_down_addition = source.cool_down_addition;
                clone.move_speed_addition = 0f;
                clone.primary_move_speed_addition = 0f;
                clone.shoot_spread_addition = source.shoot_spread_addition;
                clone.shoot_speed_addition = source.shoot_speed_addition;

                clone.avatar_part = CopyStringArray(source.avatar_part, 18);
                clone.temp_avatar = CopyStringArray(source.temp_avatar, 18);
                clone.gesture = CopyStringArray(source.gesture, 6);
                clone.independ_info = CopyStringArray(source.independ_info, 5);
                clone.primary_independ_info = CopyStringArray(source.primary_independ_info, 5);
                clone.wing_params = source.wing_params == null ? new float[0] : (float[])source.wing_params.Clone();

                // Character.InitializeObjectBase/ActivateObjects always iterate 36 slots.
                clone.slots_info = CreateEmptySlots();
                clone.primary_slots_info = CreateEmptySlots();
                int copiedWeapons = ClonePlayerWeapons(player, clone.slots_info, clone.primary_slots_info);
                if (copiedWeapons == 0)
                {
                    error = "生成 RobotControl 需要至少一把玩家武器";
                    return false;
                }
                clone.ParsePartInfo();
                info = clone;
                return true;
            }
            catch (Exception e)
            {
                error = "复制角色外观/配置失败: " + e.Message;
                return false;
            }
        }

        private static bool TryCloneCurrentWeapon(Character player, out ObjectBaseInfo clone, out int index)
        {
            clone = null;
            index = -1;
            CharacterInfoData info = player == null ? null : player.character_info;
            ObjectBaseInfo[] objects = info == null || info.slots_info == null ? null : info.slots_info.object_info;
            if (objects == null) return false;

            ObjectBaseInfo source = player.mWeapon == null ? null : player.mWeapon.info;
            if (source != null)
            {
                for (int i = 0; i < objects.Length; i++)
                {
                    if (objects[i] == source)
                    {
                        index = i;
                        break;
                    }
                }
            }

            if (source == null || index < 0)
            {
                for (int i = 0; i < objects.Length; i++)
                {
                    if (objects[i] != null && objects[i].type == 2)
                    {
                        source = objects[i];
                        index = i;
                        break;
                    }
                }
            }
            if (source == null || index < 0) return false;
            clone = CloneWeaponInfo(source);
            return clone != null;
        }

        private static int ClonePlayerWeapons(Character player, SlotInfo slots, SlotInfo primary)
        {
            CharacterInfoData info = player == null ? null : player.character_info;
            ObjectBaseInfo[] source = info == null || info.slots_info == null
                ? null
                : info.slots_info.object_info;
            if (source == null || slots == null || primary == null) return 0;

            int count = 0;
            int length = Mathf.Min(36, source.Length);
            for (int i = 0; i < length; i++)
            {
                if (source[i] == null || source[i].type != 2) continue;
                ObjectBaseInfo copy = CloneWeaponInfo(source[i]);
                if (copy == null) continue;
                slots.object_info[i] = copy;
                primary.object_info[i] = copy;
                count++;
            }
            return count;
        }

        private static ObjectBaseInfo CloneWeaponInfo(ObjectBaseInfo source)
        {
            if (source == null || MemberwiseCloneMethod == null) return null;
            ObjectBaseInfo clone = MemberwiseCloneMethod.Invoke(source, null) as ObjectBaseInfo;
            if (clone == null) return null;
            clone.owner = null;
            clone.owner_boss = null;
            clone.cast_effectData = source.cast_effectData == null
                ? new List<string>()
                : new List<string>(source.cast_effectData);
            clone.cooling = -1f;
            clone.stop_cooling = false;
            clone.incool = false;
            clone.cool_down_ready = true;
            return clone;
        }

        private static SlotInfo CreateEmptySlots()
        {
            SlotInfo slots = new SlotInfo();
            slots.object_info = new ObjectBaseInfo[36];
            return slots;
        }

        private static List<LocalBotAppearanceChoice>[] CreateAppearanceCatalogs()
        {
            List<LocalBotAppearanceChoice>[] result = new List<LocalBotAppearanceChoice>[18];
            for (int i = 0; i < result.Length; i++) result[i] = new List<LocalBotAppearanceChoice>();
            return result;
        }

        private static Dictionary<string, string> CreateKnownWeaponDisplayKeys()
        {
            Dictionary<string, string> result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            result["bow_01"] = "id_datalist_Simple_Compound_Bow";
            result["grenade_01"] = "id_datalist_STG39_Wooden_Handle_Grenade";
            result["knives_01"] = "id_datalist_Rusty_Knife";
            result["machinegun_01"] = "id_datalist_DP";
            result["machinegun_51"] = "id_weapon_machinegun_51";
            result["pistol_01"] = "id_datalist_TARGET";
            result["rpg_01"] = "id_datalist_Recoilless_Artillery";
            result["shield_01"] = "id_datalist_Buckler_Bat";
            result["shotgun_01"] = "id_datalist_M37";
            result["smg_01"] = "id_datalist_AK74";
            result["smg_51"] = "id_weapon_smg_51";
            result["sniperrifle_01"] = "id_datalist_M200";
            result["sniperrifle_51"] = "id_weapon_sniperrifle_51";
            return result;
        }

        private static void EnsureResourceCatalogs()
        {
            if (_resourceCatalogLoaded) return;
            _resourceCatalogLoaded = true;
            try
            {
                string manifest = Path.Combine(Application.dataPath, "FileInfo.xml");
                if (!File.Exists(manifest))
                {
                    FileLogger.Log("LOCAL-BOT", "resource catalog missing path=" + manifest);
                    return;
                }

                string[] lines = File.ReadAllLines(manifest);
                for (int i = 0; i < lines.Length; i++)
                {
                    string path = ReadXmlAttribute(lines[i], "FilePath");
                    string file = ReadXmlAttribute(lines[i], "FileName");
                    if (string.IsNullOrEmpty(path) || string.IsNullOrEmpty(file)) continue;
                    RegisterWeaponResource(path, file);
                    RegisterAppearanceResource(path, file);
                }

                WeaponCatalog.Sort(delegate(LocalBotWeaponChoice a, LocalBotWeaponChoice b)
                {
                    int type = a.SubType.CompareTo(b.SubType);
                    return type != 0 ? type : string.Compare(a.Resource, b.Resource, StringComparison.OrdinalIgnoreCase);
                });
                for (int i = 0; i < AppearanceCatalog.Length; i++)
                {
                    AppearanceCatalog[i].Sort(delegate(LocalBotAppearanceChoice a, LocalBotAppearanceChoice b)
                    {
                        return string.Compare(a.Resource, b.Resource, StringComparison.OrdinalIgnoreCase);
                    });
                }
                FileLogger.Log("LOCAL-BOT", "resource catalog weapons=" + WeaponCatalog.Count +
                    " skin=" + AppearanceCatalog[0].Count + " hair=" + AppearanceCatalog[6].Count +
                    " dress=" + (AppearanceCatalog[8].Count + AppearanceCatalog[9].Count + AppearanceCatalog[10].Count + AppearanceCatalog[11].Count + AppearanceCatalog[12].Count));
            }
            catch (Exception e)
            {
                FileLogger.Log("LOCAL-BOT", "resource catalog failed: " + e.Message);
            }
        }

        private static string ReadXmlAttribute(string line, string name)
        {
            string marker = name + "=\"";
            int start = line.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (start < 0) return string.Empty;
            start += marker.Length;
            int end = line.IndexOf('"', start);
            return end > start ? line.Substring(start, end - start) : string.Empty;
        }

        private static void RegisterWeaponResource(string path, string file)
        {
            if (!path.StartsWith("Prefab/Weapon/", StringComparison.OrdinalIgnoreCase) ||
                !file.EndsWith(".weapon", StringComparison.OrdinalIgnoreCase)) return;
            string resource = Path.GetFileNameWithoutExtension(file);
            byte subtype;
            string family;
            if (!TryGetWeaponFamily(resource, out subtype, out family)) return;
            for (int i = 0; i < WeaponCatalog.Count; i++)
                if (string.Equals(WeaponCatalog[i].Resource, resource, StringComparison.OrdinalIgnoreCase)) return;

            string displayKey = ResolveWeaponDisplayKey(resource);
            string localized = ResolveLocalizedValue(displayKey);
            string fallback = family + " " + FriendlyResourceSuffix(resource);
            string label = (string.IsNullOrEmpty(localized) ? fallback : localized) + "  [" + resource + "]";
            WeaponCatalog.Add(new LocalBotWeaponChoice(label, resource, displayKey, subtype));
        }

        private static bool TryGetWeaponFamily(string resource, out byte subtype, out string family)
        {
            string lower = (resource ?? string.Empty).ToLowerInvariant();
            subtype = 0;
            family = string.Empty;
            if (lower.StartsWith("smg_")) { subtype = 1; family = "冲锋枪"; }
            else if (lower.StartsWith("sniperrifle_")) { subtype = 2; family = "狙击枪"; }
            else if (lower.StartsWith("machinegun_")) { subtype = 3; family = "机枪"; }
            else if (lower.StartsWith("shotgun_")) { subtype = 4; family = "霰弹枪"; }
            else if (lower.StartsWith("pistol_")) { subtype = 5; family = "手枪"; }
            else if (lower.StartsWith("knives_") || lower == "heroweapon" || lower == "stick") { subtype = 6; family = "近战"; }
            else if (lower.StartsWith("grenade_")) { subtype = 10; family = "手雷"; }
            else if (lower.StartsWith("rpg_") || lower.StartsWith("grenadelauncher_")) { subtype = 11; family = "重型武器"; }
            else if (lower.StartsWith("bow_") || lower.StartsWith("crossbow_")) { subtype = 12; family = "弓弩"; }
            else if (lower.StartsWith("shield_")) { subtype = 13; family = "盾牌"; }
            return subtype != 0;
        }

        private static string ResolveWeaponDisplayKey(string resource)
        {
            string key;
            if (KnownWeaponDisplayKeys.TryGetValue(resource, out key)) return key;
            string candidate = "id_weapon_" + resource;
            if (!string.IsNullOrEmpty(ResolveLocalizedValue(candidate))) return candidate;
            candidate = "id_datalist_" + resource;
            return string.IsNullOrEmpty(ResolveLocalizedValue(candidate)) ? string.Empty : candidate;
        }

        private static string ResolveLocalizedValue(string key)
        {
            if (string.IsNullOrEmpty(key)) return string.Empty;
            string value = SafeLocalizedName(key);
            return string.IsNullOrEmpty(value) || string.Equals(value, key, StringComparison.OrdinalIgnoreCase)
                ? string.Empty
                : value;
        }

        private static string FriendlyResourceSuffix(string resource)
        {
            int split = resource == null ? -1 : resource.IndexOf('_');
            if (split < 0 || split + 1 >= resource.Length) return resource ?? string.Empty;
            return resource.Substring(split + 1).Replace('_', ' ');
        }

        private static ObjectBaseInfo CreateWeaponInfo(LocalBotWeaponChoice choice)
        {
            if (choice == null) return null;
            ObjectBaseInfo info;
            switch (choice.SubType)
            {
                case 1: info = new SubMachineGunInfo(); break;
                case 2: info = new SniperGunInfo(); break;
                case 3: info = new MachineGunInfo(); break;
                case 4: info = new ShotGunInfo(); break;
                case 5: info = new PistolInfo(); break;
                case 6: info = new KnifeInfo(); break;
                case 10: info = new GrenadeInfo(); break;
                case 11: info = new RPGInfo(); break;
                case 12: info = new BowInfo(); break;
                case 13: info = new DualWeaponInfo(); break;
                default: return null;
            }

            info.name = choice.Resource;
            info.LoadLua(choice.Resource);
            info.name = choice.Resource;
            info.display_name = string.IsNullOrEmpty(choice.DisplayKey) ? choice.Label : choice.DisplayKey;
            info.id = 0;
            info.slot = 1;
            info.type = 2;
            info.sub_type = choice.SubType;
            info.cool_down_origial = info.cool_down;
            info.range_origial = info.range;
            info.cooling = -1f;
            info.stop_cooling = false;
            info.incool = false;
            info.cool_down_ready = true;
            return info;
        }

        private static void RegisterAppearanceResource(string path, string file)
        {
            string lowerPath = path.ToLowerInvariant();
            string lowerFile = file.ToLowerInvariant();
            int part = -1;
            string resource = string.Empty;
            if (lowerPath.StartsWith("texture/character/dress/skin/") && lowerFile.EndsWith(".skin")) part = 0;
            else if (lowerPath.StartsWith("texture/character/face/eye/") && lowerFile.EndsWith(".eye")) part = 1;
            else if (lowerPath.StartsWith("texture/character/face/mouth/") && lowerFile.EndsWith(".mouth")) part = 2;
            else if (lowerPath.StartsWith("texture/character/face/nose/") && lowerFile.EndsWith(".nose")) part = 3;
            else if (lowerPath.StartsWith("prefab/avatar/move/") && lowerFile.EndsWith("_ear_left.move"))
            {
                part = 4;
                resource = file.Substring(0, file.Length - "_left.move".Length);
            }
            else if (lowerFile.EndsWith(".beard")) part = 5;
            else if (lowerFile.EndsWith(".hair")) part = 6;
            else if (lowerFile.EndsWith(".helmet")) part = 7;
            else if (lowerFile.EndsWith(".currpartdress"))
            {
                if (lowerFile.IndexOf("_underwear", StringComparison.Ordinal) >= 0) part = 8;
                else if (lowerFile.IndexOf("_outerwear", StringComparison.Ordinal) >= 0) part = 9;
                else if (lowerFile.IndexOf("_trousers", StringComparison.Ordinal) >= 0) part = 10;
                else if (lowerFile.IndexOf("_glove", StringComparison.Ordinal) >= 0) part = 11;
                else if (lowerFile.IndexOf("_shoes", StringComparison.Ordinal) >= 0) part = 12;
            }
            else if (lowerFile.EndsWith(".decal")) part = 13;
            else if (lowerFile.EndsWith(".movable")) part = 14;
            else if (lowerFile.EndsWith(".immobiledown")) part = 17;
            if (part < 0) return;
            if (string.IsNullOrEmpty(resource)) resource = NormalizeAppearanceFileName(file);
            AddAppearanceResource(part, resource);
            if (part == 17)
            {
                AddAppearanceResource(15, resource);
                AddAppearanceResource(16, resource);
            }
        }

        private static string NormalizeAppearanceFileName(string file)
        {
            string value = Path.GetFileNameWithoutExtension(file);
            if (value.StartsWith("Tex_", StringComparison.OrdinalIgnoreCase)) value = value.Substring(4);
            if (value.EndsWith("_c", StringComparison.OrdinalIgnoreCase) || value.EndsWith("_m", StringComparison.OrdinalIgnoreCase))
                value = value.Substring(0, value.Length - 2);
            return value;
        }

        private static void AddAppearanceResource(int part, string resource)
        {
            if (part < 0 || part >= AppearanceCatalog.Length || string.IsNullOrEmpty(resource)) return;
            List<LocalBotAppearanceChoice> list = AppearanceCatalog[part];
            for (int i = 0; i < list.Count; i++)
                if (string.Equals(list[i].Resource, resource, StringComparison.OrdinalIgnoreCase)) return;
            list.Add(new LocalBotAppearanceChoice(AppearancePartNames[part] + " " + resource, string.Empty, resource));
        }

        private static string ExtractAppearanceResource(string data)
        {
            string[] tokens;
            int start;
            int end;
            if (!TryReadFirstLuaObject(data, out tokens, out start, out end) || tokens.Length == 0) return string.Empty;
            return tokens[0].Trim().Trim('\'').Trim('"');
        }

        private static string FindAppearanceTemplate(int partIndex)
        {
            try
            {
                CharacterInfoData info = _sessionPlayer == null ? null : _sessionPlayer.character_info;
                if (info != null && info.avatar_part != null && partIndex < info.avatar_part.Length &&
                    !string.IsNullOrEmpty(info.avatar_part[partIndex])) return info.avatar_part[partIndex];
                List<Character> characters = CharacterManager.Instance.GetCharacters();
                for (int i = 0; i < characters.Count; i++)
                {
                    info = characters[i] == null ? null : characters[i].character_info;
                    if (info != null && info.avatar_part != null && partIndex < info.avatar_part.Length &&
                        !string.IsNullOrEmpty(info.avatar_part[partIndex])) return info.avatar_part[partIndex];
                }
            }
            catch { }
            return BuildDefaultAppearanceData(partIndex, "placeholder");
        }

        private static string ReplaceAppearanceResource(string template, int partIndex, string resource)
        {
            if (string.IsNullOrEmpty(template) || template == "{}") template = BuildDefaultAppearanceData(partIndex, resource);
            string[] tokens;
            int start;
            int end;
            if (!TryReadFirstLuaObject(template, out tokens, out start, out end))
                return BuildDefaultAppearanceData(partIndex, resource);
            tokens[0] = "'" + resource.Replace("'", string.Empty) + "'";
            string replacement = "{" + string.Join(",", tokens) + "}";
            return template.Substring(0, start) + replacement + template.Substring(end + 1);
        }

        private static string BuildDefaultAppearanceData(int part, string resource)
        {
            string name = "'" + (resource ?? string.Empty).Replace("'", string.Empty) + "'";
            switch (part)
            {
                case 0: return "{" + name + ",1,-1,3,127.5,127.5,127.5,127.5,127.5,127.5,127.5,127.5,127.5}";
                case 1: return "{" + name + ",2,0.141128,-0.165493,0,0.959632,0.959632,-0.141128,-0.165493,0,0.959632,0.959632,3,127.5,127.5,127.5,127.5,127.5,127.5,127.5,127.5,127.5}";
                case 2: return "{" + name + ",3,0,-0.04,0,1,1,3,127.5,127.5,127.5,127.5,127.5,127.5,127.5,127.5,127.5}";
                case 3: return "{" + name + ",4,0,0,0,1,1,3,127.5,127.5,127.5,127.5,127.5,127.5,127.5,127.5,127.5}";
                case 4: return "{" + name + ",5,0,0,0,0,0,1,0,0,0,0,0,0,1,1,0,0,0,0,0,0,0,1,1,0,0,0,3,127.5,127.5,127.5,127.5,127.5,127.5,127.5,127.5,127.5}";
                case 5: return "{" + name + ",6,0,3,127.5,127.5,127.5,127.5,127.5,127.5,127.5,127.5,127.5}";
                case 6: return "{" + name + ",7,-1,3,127.5,127.5,127.5,127.5,127.5,127.5,127.5,127.5,127.5}";
                case 7: return "{" + name + ",8,0,3,127.5,127.5,127.5,127.5,127.5,127.5,127.5,127.5,127.5}";
                case 8: case 9: case 10: case 11: case 12:
                    return "{{" + name + "," + (part + 1) + ",0,3,127.5,127.5,127.5,127.5,127.5,127.5,127.5,127.5,127.5},}";
                case 13:
                    return "{{" + name + ",14,0,0,0,1,0,0,1,0,0,0,0,0,0,0,1,'idle',0,3,127.5,127.5,127.5,127.5,127.5,127.5,127.5,127.5,127.5},}";
                case 14:
                    return "{{" + name + ",15,0,0,0,0,0,0,0,1,1,0,0,0,0,0,1,3,127.5,127.5,127.5,127.5,127.5,127.5,127.5,127.5,127.5},}";
                case 15: case 16: case 17:
                    return "{{" + name + "," + (part + 1) + ",0,3,127.5,127.5,127.5,127.5,127.5,127.5,127.5,127.5,127.5},}";
                default: return "{}";
            }
        }

        private static void UpdateMovement(LocalBotRecord record, RobotControl robot, Character player)
        {
            if (record == null || robot == null || robot.IsDied) return;

            if (record.AppliedMovementMode != record.MovementMode)
                EnterMovementMode(record, robot);

            if (record.MovementMode == LocalBotMovementMode.Stationary)
            {
                if (record.ActionMoveUntil > 0f && Time.time >= record.ActionMoveUntil)
                {
                    record.ActionMoveUntil = 0f;
                    StopRobot(robot);
                    if (string.IsNullOrEmpty(record.ManualAnimation))
                    {
                        SetManualAnimationLock(robot, true);
                        robot.PlayAnimation("idle", -1f);
                        record.AnimationLocked = true;
                    }
                }
                return;
            }

            if (record.MovementMode == LocalBotMovementMode.NativeAI)
            {
                if (record.AnimationLocked)
                {
                    SetManualAnimationLock(robot, false);
                    record.AnimationLocked = false;
                    record.ManualAnimation = string.Empty;
                }
                SetAiActive(robot, true);
                robot.SetState(2, true);
                return;
            }

            float now = Time.time;
            if (record.MovementMode == LocalBotMovementMode.FollowPlayer)
            {
                if (player == null) return;
                float distance = Vector3.Distance(robot.transform.position, player.transform.position);
                if (distance <= record.FollowDistance)
                {
                    StopRobot(robot);
                    return;
                }
                record.MovementTarget = player.transform.position;
                AdvanceManagedMovement(record, robot, record.MovementTarget, record.FollowDistance);
                return;
            }

            if (record.MovementMode == LocalBotMovementMode.MoveToPoint)
            {
                if (!AdvanceManagedMovement(record, robot, record.MovementTarget, 0.55f))
                {
                    StopRobot(robot);
                }
                return;
            }

            if (record.MovementMode == LocalBotMovementMode.Wander)
            {
                float remaining = XZDistance(robot.transform.position, record.MovementTarget);
                bool stuck = record.HasManagedTarget && now - record.LastManagedProgressTime > 1.1f;
                if (!record.HasManagedTarget || remaining <= 0.7f || stuck || now >= record.NextMovementUpdate)
                    PickWanderTarget(record, robot, now);

                if (record.HasManagedTarget && !AdvanceManagedMovement(record, robot, record.MovementTarget, 0.55f))
                {
                    record.HasManagedTarget = false;
                    StopRobot(robot);
                }
            }
        }

        private static void EnterMovementMode(LocalBotRecord record, RobotControl robot)
        {
            record.AppliedMovementMode = record.MovementMode;
            record.LastManagedPosition = robot.transform.position;
            record.LastManagedProgressTime = Time.time;

            if (record.MovementMode == LocalBotMovementMode.NativeAI)
            {
                SetManualAnimationLock(robot, false);
                record.AnimationLocked = false;
                record.ManualAnimation = string.Empty;
                SetAiActive(robot, true);
                try { robot.SetState(32, true); } catch { }
                try { robot.SetState(2, true); } catch { }
                return;
            }

            SetAiActive(robot, false);
            StopRobot(robot);
            if (record.MovementMode == LocalBotMovementMode.Stationary)
            {
                record.HasManagedTarget = false;
                try
                {
                    robot.direction = Vector2.zero;
                    if (robot.avatar != null) robot.avatar.resetPose();
                    SetManualAnimationLock(robot, true);
                    robot.PlayAnimation("idle", -1f);
                    record.AnimationLocked = true;
                    record.ManualAnimation = string.Empty;
                }
                catch { }
            }
            else
            {
                SetManualAnimationLock(robot, false);
                record.AnimationLocked = false;
                record.ManualAnimation = string.Empty;
                if (record.MovementMode == LocalBotMovementMode.Wander)
                {
                    record.HasManagedTarget = false;
                    record.NextMovementUpdate = 0f;
                }
            }
        }

        private static void SetAiActive(RobotControl robot, bool active)
        {
            try
            {
                AIRig rig = robot == null ? null : robot.aiRig;
                if (rig != null && rig.AI != null) rig.AI.IsActive = active;
            }
            catch { }
        }

        private static bool AdvanceManagedMovement(LocalBotRecord record, RobotControl robot, Vector3 destination, float stopDistance)
        {
            Vector3 delta = destination - robot.transform.position;
            delta.y = 0f;
            float distance = delta.magnitude;
            if (distance <= stopDistance) return false;

            Vector3 before = robot.transform.position;
            Vector3 direction = delta / distance;
            float step = Mathf.Min(distance - stopDistance, record.RunSpeed * Mathf.Max(0.001f, Time.deltaTime));
            try
            {
                robot.transform.rotation = Quaternion.RotateTowards(
                    robot.transform.rotation,
                    Quaternion.LookRotation(direction, Vector3.up),
                    540f * Time.deltaTime);
                CharacterController controller = robot.GetComponent<CharacterController>();
                if (controller != null && controller.enabled)
                    controller.Move(direction * step);
                else
                    robot.transform.position += direction * step;
                robot.direction = Vector2.up;
                robot.SetState(2, true);
            }
            catch { return false; }

            if (XZDistance(before, robot.transform.position) > 0.025f)
            {
                record.LastManagedPosition = robot.transform.position;
                record.LastManagedProgressTime = Time.time;
            }
            return true;
        }

        private static void PickWanderTarget(LocalBotRecord record, RobotControl robot, float now)
        {
            record.HasManagedTarget = false;
            for (int attempt = 0; attempt < 8; attempt++)
            {
                float angle = UnityEngine.Random.Range(0f, 6.2831855f);
                float radius = UnityEngine.Random.Range(record.WanderRadius * 0.35f, record.WanderRadius);
                Vector3 destination = record.MovementAnchor +
                    new Vector3(Mathf.Cos(angle) * radius, 0f, Mathf.Sin(angle) * radius);
                Vector3 grounded;
                if (!TryProjectToGround(destination, out grounded)) continue;
                if (!ManagedSegmentClear(robot, grounded)) continue;
                record.MovementTarget = grounded;
                record.HasManagedTarget = true;
                record.LastManagedProgressTime = now;
                record.LastManagedPosition = robot.transform.position;
                record.NextMovementUpdate = now + UnityEngine.Random.Range(4f, 7f);
                break;
            }
        }

        private static bool ManagedSegmentClear(RobotControl robot, Vector3 destination)
        {
            Vector3 from = robot.transform.position + Vector3.up * 0.8f;
            Vector3 delta = destination - robot.transform.position;
            delta.y = 0f;
            float distance = delta.magnitude;
            if (distance < 0.4f) return true;
            RaycastHit[] hits = Physics.SphereCastAll(from, 0.24f, delta / distance, distance);
            for (int i = 0; i < hits.Length; i++)
            {
                Transform hit = hits[i].transform;
                if (hit == null || hit.root == robot.transform.root) continue;
                Collider collider = hits[i].collider;
                if (collider != null && collider.isTrigger) continue;
                return false;
            }
            return true;
        }

        private static void StopRobot(RobotControl robot)
        {
            if (robot == null) return;
            try { robot.SetState(2, false); } catch { }
            try { robot.direction = Vector2.zero; } catch { }
            try
            {
                if (robot.aiRig != null && robot.aiRig.AI != null && robot.aiRig.AI.Motor != null)
                    robot.aiRig.AI.Motor.Stop();
            }
            catch { }
        }

        private static void SetManualAnimationLock(Character character, bool locked)
        {
            if (character == null) return;
            try { character.SetAnimationState(2048, locked); } catch { }
            try
            {
                if (character.motor1 != null) character.motor1.MoveLock(locked);
            }
            catch { }
        }

        private static void CommandRobotMove(RobotControl robot, Vector3 destination)
        {
            if (robot == null) return;
            bool accepted = false;
            try
            {
                if (SetPartolPositionMethod != null)
                {
                    object result = SetPartolPositionMethod.Invoke(
                        robot,
                        new object[] { RobotControl.PartolType.FixedPosition, destination });
                    accepted = result is bool && (bool)result;
                }
            }
            catch { }

            try
            {
                robot.SetState(2, true);
                if (!accepted && robot.aiRig != null && robot.aiRig.AI != null && robot.aiRig.AI.Motor != null)
                    robot.aiRig.AI.Motor.MoveTo(destination);
            }
            catch { }
        }

        private static void RefreshAppearance(Character bot, LocalBotRecord record)
        {
            CharacterInfoData info = bot == null ? null : bot.character_info;
            if (bot == null || info == null) return;
            info.ParsePartInfo();
            bot.SetCharacterInfo(info);
            bot.can_select = record != null && record.Targetable;
            bot.ActivateObjects();
            if (record != null && (record.AnimationLocked || record.MovementMode == LocalBotMovementMode.Stationary))
            {
                SetManualAnimationLock(bot, true);
                bot.PlayAnimation(string.IsNullOrEmpty(record.ManualAnimation) ? "idle" : record.ManualAnimation, -1f);
                record.AnimationLocked = true;
            }
        }

        private static void UpdateTargetedHealthBar(Character player)
        {
            if (player == null || Bots.Count == 0 || HpAlphaField == null) return;
            Camera camera = Camera.main;
            if (camera == null) return;

            try
            {
                Ray ray = camera.ScreenPointToRay(new Vector3(Screen.width * 0.5f, Screen.height * 0.5f, 0f));
                RaycastHit[] hits = Physics.RaycastAll(ray, 250f);
                LocalBotRecord target = null;
                float targetDistance = float.MaxValue;
                float blockerDistance = float.MaxValue;
                for (int i = 0; i < hits.Length; i++)
                {
                    Collider collider = hits[i].collider;
                    Transform hit = hits[i].transform;
                    if (hit == null || (collider != null && collider.isTrigger)) continue;
                    Transform root = hit.root;
                    if (root == player.transform.root) continue;

                    LocalBotRecord local = FindByRoot(root);
                    if (local != null)
                    {
                        if (hits[i].distance < targetDistance)
                        {
                            target = local;
                            targetDistance = hits[i].distance;
                        }
                    }
                    else if (hits[i].distance < blockerDistance)
                    {
                        blockerDistance = hits[i].distance;
                    }
                }

                if (target != null && targetDistance <= blockerDistance + 0.05f && target.Targetable &&
                    target.Character != null && !target.Character.IsDied)
                {
                    HpAlphaField.SetValue(target.Character, 1.25f);
                }
            }
            catch { }
        }

        private static LocalBotRecord FindByRoot(Transform root)
        {
            if (root == null) return null;
            for (int i = 0; i < Bots.Count; i++)
            {
                Character character = Bots[i] == null ? null : Bots[i].Character;
                if (character != null && character.transform.root == root) return Bots[i];
            }
            return null;
        }

        private static bool GetOffsetTokenIndices(int partIndex, out int x, out int y, out int z)
        {
            x = y = z = -1;
            switch (partIndex)
            {
                case 1:
                case 2:
                case 3:
                    x = 2; y = 3; return true;
                case 4:
                    x = 8; y = 9; z = 10; return true;
                case 13:
                    x = 2; y = 3; z = 4; return true;
                case 14:
                    x = 3; y = 4; z = 5; return true;
                default:
                    return false;
            }
        }

        private static bool TryReadFirstLuaObject(string data, out string[] tokens, out int start, out int end)
        {
            tokens = null;
            start = -1;
            end = -1;
            if (string.IsNullOrEmpty(data)) return false;
            start = data.IndexOf('{');
            while (start >= 0 && start + 1 < data.Length && data[start + 1] == '{') start++;
            if (start < 0) return false;
            end = data.IndexOf('}', start + 1);
            if (end <= start + 1) return false;
            string body = data.Substring(start + 1, end - start - 1);
            tokens = body.Split(',');
            return tokens.Length >= 2;
        }

        private static bool ContainsAppearanceData(List<LocalBotAppearanceChoice> choices, string data)
        {
            for (int i = 0; i < choices.Count; i++)
                if (string.Equals(choices[i].Data, data, StringComparison.Ordinal)) return true;
            return false;
        }

        private static string DescribeAppearanceData(string data)
        {
            string[] tokens;
            int start;
            int end;
            if (!TryReadFirstLuaObject(data, out tokens, out start, out end) || tokens.Length == 0)
                return "空数据";
            string name = tokens[0].Trim().Trim('\'').Trim('"');
            int objects = 0;
            for (int i = 0; i < data.Length; i++)
                if (data[i] == '{' && i + 1 < data.Length && data[i + 1] != '{') objects++;
            return objects > 1 ? name + " (" + objects + "层)" : name;
        }

        private static string SafeLocalizedName(string key)
        {
            if (string.IsNullOrEmpty(key)) return string.Empty;
            try
            {
                string localized = key.valueByThisKey();
                return string.IsNullOrEmpty(localized) ? key : localized;
            }
            catch { return key; }
        }

        private static void AddUnique(List<string> values, string value)
        {
            if (string.IsNullOrEmpty(value)) return;
            for (int i = 0; i < values.Count; i++)
                if (string.Equals(values[i], value, StringComparison.OrdinalIgnoreCase)) return;
            values.Add(value);
        }

        private static float XZDistance(Vector3 a, Vector3 b)
        {
            float x = a.x - b.x;
            float z = a.z - b.z;
            return Mathf.Sqrt(x * x + z * z);
        }

        private static bool TryAllocateCharacterUid(Level level, Character player, out byte uid)
        {
            for (int candidate = FirstLocalUid; candidate >= LastLocalUid; candidate--)
            {
                if (player != null && player.uid == candidate) continue;
                Character existing = null;
                try { existing = level.GetCharacter(candidate); } catch { }
                if (existing == null)
                {
                    uid = (byte)candidate;
                    return true;
                }
            }
            uid = 0;
            return false;
        }

        private static int ResolveTeam(Character player, int mode)
        {
            if (mode == 1) return 0;
            if (mode == 2) return 1;
            int playerTeam = 0;
            try { playerTeam = player.GetTeam(); } catch { }
            return playerTeam == 0 ? 1 : 0;
        }

        private static Quaternion FaceTowards(Vector3 from, Vector3 to)
        {
            Vector3 direction = to - from;
            direction.y = 0f;
            return direction.sqrMagnitude < 0.0001f
                ? Quaternion.identity
                : Quaternion.LookRotation(direction.normalized, Vector3.up);
        }

        private static string[] CopyStringArray(string[] source, int length)
        {
            string[] copy = new string[length];
            for (int i = 0; i < copy.Length; i++)
            {
                copy[i] = source != null && i < source.Length && source[i] != null ? source[i] : string.Empty;
            }
            return copy;
        }

        private static string BuildDisplayName(string prefix, int sequence)
        {
            string safePrefix = string.IsNullOrEmpty(prefix) ? "PathBot" : prefix.Trim();
            if (safePrefix.Length > 18) safePrefix = safePrefix.Substring(0, 18);
            return safePrefix + "-" + sequence.ToString("00");
        }

        private static string NormalizeDisplayName(string name, int sequence)
        {
            string safe = string.IsNullOrEmpty(name) ? "PathBot-" + sequence.ToString("00") : name.Trim();
            return safe.Length > 24 ? safe.Substring(0, 24) : safe;
        }

        private static string MovementName(LocalBotMovementMode mode)
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

        private static void DestroyRecord(LocalBotRecord record)
        {
            Character bot = record == null ? null : record.Character;
            if (bot == null) return;

            try { RobotManager.Instance.RemoveRobot(bot); } catch { }
            try
            {
                CharacterManager manager = CharacterManager.Instance;
                if (manager != null && manager.character_set != null && manager.character_set.Contains(bot))
                {
                    manager.RemoveCharacter(bot.uid);
                }
                else if (bot.gameObject != null)
                {
                    UnityEngine.Object.Destroy(bot.gameObject);
                }
            }
            catch
            {
                try { if (bot.gameObject != null) UnityEngine.Object.Destroy(bot.gameObject); } catch { }
            }
            record.Character = null;
        }

        private static void PruneDestroyed()
        {
            for (int i = Bots.Count - 1; i >= 0; i--)
            {
                if (Bots[i] == null || Bots[i].Character == null) Bots.RemoveAt(i);
            }
        }

        private static bool Fail(string message)
        {
            _lastStatus = message;
            FileLogger.Log("LOCAL-BOT", message);
            return false;
        }

        private static string FormatVector(Vector3 value)
        {
            return string.Format("({0:0.00}, {1:0.00}, {2:0.00})", value.x, value.y, value.z);
        }
    }
}
