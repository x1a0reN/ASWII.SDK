using ASWDEBUG.Logger;
using Harmony;
using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace ASWDEBUG.Cheats.AutoAim
{
    internal static class MotherBossStageController
    {
#if false
        private const float ShotIntervalSeconds = 1f;
        private const float MotherDistanceFromPlayer = 5f;

        private sealed class SuppressedFreedomBossState
        {
            internal BaseBoss Boss;
            internal bool HasBornPoint;
            internal Vector3 BornPoint;
        }

        private static readonly List<SuppressedFreedomBossState> SuppressedFreedomBosses =
            new List<SuppressedFreedomBossState>();

        private static Level _trackedLevel;
        private static int _trackedStage = -1;
        private static BaseBoss _trackedMother;
        private static int _trackedMotherUid;
        private static int _shotsSent;
        private static int _lastKnownHp = int.MinValue;
        private static float _nextShotTime;
        private static bool _motherPinned;
        private static bool _waitingForMotherLogged;
        private static int _blockedAttackCount;

        internal static void OnToggle(bool enabled)
        {
            if (!enabled)
            {
                RestoreSuppressedFreedomBosses();
            }

            ResetRuntimeState();
        }

        internal static bool Tick(Level level, Character player)
        {
            if (!MotherBossAutoClear.Enabled)
            {
                ResetRuntimeState();
                return true;
            }

            if (!IsBossLevel(level))
            {
                if (level == null || _trackedLevel != level) ResetRuntimeState();
                return true;
            }

            EnsureStageContext(level);
            CaptureAndSuppressExistingFreedomBosses(level);
            if (!IsExpeditionReady(level, player)) return true;

            BaseBoss mother;
            if (!TryResolveMother(level, out mother))
            {
                if (SuppressedFreedomBosses.Count != 0 && !_waitingForMotherLogged)
                {
                    _waitingForMotherLogged = true;
                    FileLogger.Log("MOTHER-CLEAR",
                        "waiting for live server freedom boss stage=" + _trackedStage +
                        " registered=" + SuppressedFreedomBosses.Count);
                }
                return true;
            }

            TrackMother(mother);

            BossColliderData hitData;
            Vector3 hitPoint;
            if (!TryPinMotherInFrontOfPlayer(mother, player, out hitData, out hitPoint))
            {
                return true;
            }

            TrackMotherHealth(mother);

            float now = Time.realtimeSinceStartup;
            if (now < _nextShotTime) return true;

            ChannelConnection connection;
            byte slot;
            int spreadIndex;
            if (!TryPrepareHitscanShot(player, out connection, out slot, out spreadIndex))
            {
                _nextShotTime = now + ShotIntervalSeconds;
                return true;
            }

            BossImpl colliderOwner = hitData.owner;
            int colliderOwnerUid = colliderOwner == null
                ? 0
                : unchecked((int)colliderOwner.GetUid());
            if (colliderOwnerUid == 0 || colliderOwnerUid != _trackedMotherUid)
            {
                FileLogger.Log("MOTHER-CLEAR",
                    "blocked inconsistent hit tuple motherUid=" + _trackedMotherUid +
                    " colliderOwnerUid=" + colliderOwnerUid +
                    " part=" + hitData.id);
                _nextShotTime = now + ShotIntervalSeconds;
                return true;
            }

            Vector3 origin = player.transform.position + Vector3.up;
            Vector3 direction = hitPoint - origin;
            float worldDistance = direction.magnitude;
            if (worldDistance <= 0.001f)
            {
                _nextShotTime = now + ShotIntervalSeconds;
                return true;
            }
            direction /= worldDistance;

            HitBossMessage hit = new HitBossMessage();
            hit.boss_type = (byte)CharacterType.kFreedomBossType;
            hit.stage = level.GetActiveBossStage();
            hit.uid = colliderOwnerUid ^ spreadIndex;
            hit.distance = ClampDistance(worldDistance);
            hit.position = hitPoint;
            hit.part = (byte)Mathf.Clamp(hitData.id, 0, 255);
            hit.damage_level = (byte)Mathf.Clamp(hitData.damage_level, 0, 255);

            bool sent = false;
            try
            {
                MotherBossAutoClear.SetDirectShotState(true);
                connection.ShootBoss(origin, direction, hit, slot, false);
                sent = true;
            }
            catch (Exception e)
            {
                FileLogger.Log("MOTHER-CLEAR", "ShootBoss failed: " + e.Message);
            }
            finally
            {
                MotherBossAutoClear.SetDirectShotState(false);
            }

            _nextShotTime = now + ShotIntervalSeconds;
            if (sent)
            {
                _shotsSent++;
                if (_shotsSent == 1 || (_shotsSent % 5) == 0)
                {
                    FileLogger.Log("MOTHER-CLEAR",
                        "direct ShootBoss uid=" + colliderOwnerUid +
                        " encodedUid=" + hit.uid +
                        " part=" + hit.part +
                        " damage=" + hit.damage_level +
                        " slot=" + slot +
                        " count=" + _shotsSent +
                        " interval=1.0s");
                }
            }

            return true;
        }
#endif

        internal static void TrackFreedomBossRegistration(BaseBoss boss)
        {
            ExpeditionBossLockController.TrackFreedomBossRegistration(boss);
        }

        internal static void TrackFreedomBossBorn(BossImpl boss, Vector3 point)
        {
            ExpeditionBossLockController.TrackBossBorn(boss, point);
        }

        internal static void ApplyFreedomBossState(BaseBoss boss)
        {
            ExpeditionBossLockController.ApplyFreedomBossState(boss);
        }

        internal static bool ShouldBlockBossAttack(BaseBoss boss)
        {
            return ExpeditionBossLockController.ShouldBlockBossAttack(boss);
        }

        internal static bool IsTrackedMotherUid(long uid)
        {
            return ExpeditionBossLockController.IsManagedBossUid(uid);
        }

        internal static void NotifyBlockedBossAttack(BaseBoss boss, string source)
        {
            ExpeditionBossLockController.NotifyBlockedBossAttack(boss, source);
        }

        internal static void NotifyMotherHealthChanged(BossImpl boss, int oldHp, int newHp)
        {
            ExpeditionBossLockController.NotifyHealthChanged(boss, oldHp, newHp);
        }

        internal static void NotifyMotherDied(BossImpl boss)
        {
            ExpeditionBossLockController.NotifyBossDied(boss);
        }

#if false
        private static void EnsureStageContext(Level level)
        {
            if (level == null) return;

            int activeStage = level.GetActiveBossStage();
            if (_trackedLevel == level && _trackedStage == activeStage) return;

            _trackedLevel = level;
            _trackedStage = activeStage;
            ClearStageState();
            FileLogger.Log("MOTHER-CLEAR", "stage context=" + activeStage);
        }

        private static void ClearStageState()
        {
            _trackedMother = null;
            _trackedMotherUid = 0;
            _shotsSent = 0;
            _lastKnownHp = int.MinValue;
            _nextShotTime = 0f;
            _motherPinned = false;
            _waitingForMotherLogged = false;
            _blockedAttackCount = 0;
            SuppressedFreedomBosses.Clear();
        }

        private static void ResetRuntimeState()
        {
            MotherBossAutoClear.SetDirectShotState(false);
            _trackedLevel = null;
            _trackedStage = -1;
            ClearStageState();
        }

        private static void CaptureAndSuppressExistingFreedomBosses(Level level)
        {
            List<BaseBoss> bosses = level.freedom_boss_manager.GetBosses();
            if (bosses == null) return;

            for (int i = 0; i < bosses.Count; i++)
            {
                BaseBoss boss = bosses[i];
                if (!IsFreedomBoss(boss)) continue;

                bool suppress = TrySuppressFreedomBoss(boss);
                ApplyFreedomBossSuppression(boss, suppress);
            }
        }

        private static SuppressedFreedomBossState GetOrAddSuppressedState(BaseBoss boss)
        {
            SuppressedFreedomBossState state = FindSuppressedState(boss);
            if (state != null) return state;

            state = new SuppressedFreedomBossState();
            state.Boss = boss;
            SuppressedFreedomBosses.Add(state);
            FileLogger.Log("MOTHER-CLEAR",
                "register server freedom boss uid=" + SafeGetUid(boss) +
                " bossId=" + boss.boss_id +
                " stage=" + boss.stage_id);
            return state;
        }

        private static SuppressedFreedomBossState FindSuppressedState(BaseBoss boss)
        {
            if (boss == null) return null;

            long uid = SafeGetUid(boss);
            for (int i = 0; i < SuppressedFreedomBosses.Count; i++)
            {
                BaseBoss existing = SuppressedFreedomBosses[i].Boss;
                if (object.ReferenceEquals(existing, boss) ||
                    (uid != 0 && existing != null && SafeGetUid(existing) == uid))
                {
                    return SuppressedFreedomBosses[i];
                }
            }
            return null;
        }

        private static void SuppressFreedomBoss(BaseBoss boss)
        {
            if (boss == null) return;
            try
            {
                boss.start_sync_boss_data = false;
                if (boss.checkResourceLoad()) boss.SetActive(false);
            }
            catch (Exception e)
            {
                FileLogger.Log("MOTHER-CLEAR", "suppress freedom boss failed: " + e.Message);
            }
        }

        private static void RestoreSuppressedFreedomBosses()
        {
            Level level = _trackedLevel ?? GetCurrentLevel();
            if (level == null || level.freedom_boss_manager == null) return;

            int restored = 0;
            List<BaseBoss> registered = level.freedom_boss_manager.GetBosses();
            for (int i = 0; i < SuppressedFreedomBosses.Count; i++)
            {
                SuppressedFreedomBossState state = SuppressedFreedomBosses[i];
                BossImpl boss = state.Boss as BossImpl;
                if (boss == null) continue;
                if (!ContainsBoss(registered, boss)) continue;

                try
                {
                    if (state.HasBornPoint)
                    {
                        boss.RefreshBornPoint(state.BornPoint);
                    }
                    else
                    {
                        boss.start_sync_boss_data = true;
                    }
                    boss.SetUpdatePostion(true);
                    boss.UseGravity(true, true);
                    boss.SetWeaponEnable(true);
                    if (boss.checkResourceLoad()) boss.SetActive(true);
                    restored++;
                }
                catch (Exception e)
                {
                    FileLogger.Log("MOTHER-CLEAR", "restore freedom boss failed: " + e.Message);
                }
            }

            if (restored > 0)
            {
                FileLogger.Log("MOTHER-CLEAR", "restored freedom bosses=" + restored);
            }
        }

        private static bool TryResolveMother(Level level, out BaseBoss mother)
        {
            mother = null;
            List<BaseBoss> bosses = level.freedom_boss_manager.GetBosses();
            if (bosses == null) return false;

            if (IsMotherCandidate(_trackedMother) && ContainsBoss(bosses, _trackedMother))
            {
                mother = _trackedMother;
                return true;
            }

            for (int i = 0; i < bosses.Count; i++)
            {
                BaseBoss candidate = bosses[i];
                if (IsMotherCandidate(candidate))
                {
                    mother = candidate;
                    return true;
                }
            }
            return false;
        }

        private static bool IsMotherCandidate(BaseBoss boss)
        {
            try
            {
                return IsFreedomBoss(boss) && !boss.iscached && boss.hp > 0;
            }
            catch
            {
                return false;
            }
        }

        private static void TrackMother(BaseBoss mother)
        {
            int uid = unchecked((int)mother.GetUid());
            if (_trackedMotherUid == uid && object.ReferenceEquals(_trackedMother, mother)) return;

            _trackedMother = mother;
            _trackedMotherUid = uid;
            _shotsSent = 0;
            _lastKnownHp = mother.hp;
            _nextShotTime = 0f;
            _motherPinned = false;
            _waitingForMotherLogged = false;
            SuppressNonProxyFreedomBosses(mother);
            FileLogger.Log("MOTHER-CLEAR",
                "target server freedom proxy uid=" + uid +
                " bossId=" + mother.boss_id +
                " stage=" + mother.stage_id +
                " hp=" + mother.hp + "/" + mother.max_hp);
        }

        private static void SuppressNonProxyFreedomBosses(BaseBoss proxy)
        {
            for (int i = 0; i < SuppressedFreedomBosses.Count; i++)
            {
                BaseBoss boss = SuppressedFreedomBosses[i].Boss;
                if (boss != null && !object.ReferenceEquals(boss, proxy) &&
                    SafeGetUid(boss) != SafeGetUid(proxy))
                {
                    SuppressFreedomBoss(boss);
                }
            }
        }

        private static bool ContainsBoss(List<BaseBoss> bosses, BaseBoss target)
        {
            if (bosses == null || target == null) return false;

            long uid = SafeGetUid(target);
            for (int i = 0; i < bosses.Count; i++)
            {
                BaseBoss boss = bosses[i];
                if (object.ReferenceEquals(boss, target) ||
                    (uid != 0 && boss != null && SafeGetUid(boss) == uid))
                {
                    return true;
                }
            }
            return false;
        }

        private static bool TryPinMotherInFrontOfPlayer(
            BaseBoss mother,
            Character player,
            out BossColliderData hitData,
            out Vector3 hitPoint)
        {
            hitData = null;
            hitPoint = Vector3.zero;
            if (mother == null || player == null || !mother.checkResourceLoad()) return false;

            Transform motherTransform = mother.getTransfrom();
            if (motherTransform == null) return false;

            if (!_motherPinned)
            {
                mother.SetUpdatePostion(false);
                mother.UseGravity(false, false);
                _motherPinned = true;
                FileLogger.Log("MOTHER-CLEAR",
                    "mother pinned and attack disabled uid=" + _trackedMotherUid);
            }

            mother.start_sync_boss_data = true;
            if (!mother.GetActive()) mother.SetActive(true);
            mother.SetWeaponEnable(false);
            ClearBossAttackState(mother as BossImpl);

            Vector3 forward = GetHorizontalForward(player);
            Vector3 desiredPosition = player.transform.position +
                                      forward * MotherDistanceFromPlayer;
            motherTransform.position = desiredPosition;

            Vector3 facePlayer = player.transform.position - desiredPosition;
            facePlayer.y = 0f;
            if (facePlayer.sqrMagnitude > 0.0001f)
            {
                motherTransform.rotation = Quaternion.LookRotation(facePlayer.normalized, Vector3.up);
            }

            return TryGetStrongestCollider(mother, out hitData, out hitPoint);
        }

        private static Vector3 GetHorizontalForward(Character player)
        {
            Vector3 forward = Vector3.zero;
            Camera camera = Camera.main;
            if (camera != null) forward = camera.transform.forward;
            if (forward.sqrMagnitude <= 0.0001f) forward = player.transform.forward;
            forward.y = 0f;
            if (forward.sqrMagnitude <= 0.0001f) forward = Vector3.forward;
            return forward.normalized;
        }

        private static void ClearBossAttackState(BossImpl boss)
        {
            if (boss == null) return;
            for (int i = 0; i < 12; i++)
            {
                boss.SetSlotAttackStatusAndPosition(
                    i,
                    AttackStatus.kAttackOff,
                    Vector3.zero);
            }
        }

        private static bool TryGetStrongestCollider(
            BaseBoss boss,
            out BossColliderData hitData,
            out Vector3 hitPoint)
        {
            hitData = null;
            hitPoint = Vector3.zero;
            List<BossColliderData> colliders = boss.colliderList;
            if (colliders == null) return false;

            int bestDamage = int.MinValue;
            for (int i = 0; i < colliders.Count; i++)
            {
                BossColliderData data = colliders[i];
                if (data == null || data.owner == null || data.collider == null ||
                    !data.collider.enabled || !data.collider.gameObject.activeInHierarchy)
                {
                    continue;
                }
                if (SafeGetUid(data.owner) != SafeGetUid(boss)) continue;
                if (data.damage_level < bestDamage) continue;

                bestDamage = data.damage_level;
                hitData = data;
                hitPoint = data.collider.bounds.center;
            }
            return hitData != null;
        }

        private static bool TryPrepareHitscanShot(
            Character player,
            out ChannelConnection connection,
            out byte slot,
            out int spreadIndex)
        {
            connection = null;
            slot = 0;
            spreadIndex = 0;

            GameApp app = GameApp.Instance;
            connection = app == null ? null : app.channel_connection;
            WeaponBase weapon = player == null ? null : player.mWeapon;
            GunInfo gunInfo = weapon == null ? null : weapon.info as GunInfo;
            if (connection == null || weapon == null || gunInfo == null) return false;

            switch (weapon.GetWeaponType())
            {
                case WeaponType.kWeaponTypeSubMachineGun:
                case WeaponType.kWeaponTypeSniperGun:
                case WeaponType.kWeaponTypeMachineGun:
                case WeaponType.kWeaponTypeShotGun:
                case WeaponType.kWeaponTypePistol:
                    break;
                default:
                    return false;
            }

            int slotValue = weapon.info.id + 1;
            if (slotValue <= 0 || slotValue > byte.MaxValue) return false;

            float shotSpread = gunInfo.shot_spread;
            player.GetSpread(shotSpread);
            player.GetSpread(shotSpread);
            player.GetSpread(shotSpread);
            player.GetSpread(shotSpread);

            slot = (byte)slotValue;
            spreadIndex = player.currentSpreadIndex;
            return true;
        }

        private static void TrackMotherHealth(BaseBoss mother)
        {
            if (_lastKnownHp == int.MinValue)
            {
                _lastKnownHp = mother.hp;
                return;
            }
            if (_lastKnownHp == mother.hp) return;

            FileLogger.Log("MOTHER-CLEAR",
                "health observed uid=" + _trackedMotherUid +
                " hp=" + _lastKnownHp + "->" + mother.hp +
                " max=" + mother.max_hp);
            _lastKnownHp = mother.hp;
        }

        private static bool IsTrackedMother(BaseBoss boss)
        {
            if (boss == null || _trackedMother == null) return false;
            return object.ReferenceEquals(boss, _trackedMother) ||
                   (_trackedMotherUid != 0 && SafeGetUid(boss) == _trackedMotherUid);
        }

        private static bool IsFreedomBoss(BaseBoss boss)
        {
            try
            {
                return boss != null &&
                       boss.GetBossType() == CharacterType.kFreedomBossType &&
                       boss.GetUid() != 0;
            }
            catch
            {
                return false;
            }
        }

        private static bool IsBossLevel(Level level)
        {
            return level != null &&
                   level.game_type == RoomInfo.GameType.kGameTypeBoss &&
                   level.boss_manager != null &&
                   level.freedom_boss_manager != null;
        }

        private static bool IsExpeditionReady(Level level, Character player)
        {
            if (!IsBossLevel(level) || player == null) return false;

            GameApp app = GameApp.Instance;
            ChannelConnection connection = app == null ? null : app.channel_connection;
            return connection != null &&
                   connection.state == ChannelConnection.State.kInGame &&
                   connection.game_state != ChannelConnection.GameState.kGameLeaving &&
                   connection.game_state != ChannelConnection.GameState.kGameEnd;
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

        private static long SafeGetUid(BaseBoss boss)
        {
            try
            {
                return boss == null ? 0 : boss.GetUid();
            }
            catch
            {
                return 0;
            }
        }

        private static short ClampDistance(float value)
        {
            if (float.IsNaN(value) || float.IsInfinity(value) || value <= 0f) return 0;
            if (value >= short.MaxValue) return short.MaxValue;
            return (short)value;
        }
#endif
    }

    [HarmonyPatch]
    [Obfuscation(Exclude = true, ApplyToMembers = true, Feature = "-rename", StripAfterObfuscation = false)]
    public static class Patch_Level_AddFreedomBoss_MotherClear
    {
        static MethodBase TargetMethod()
        {
            return AccessTools.Method(typeof(Level), "AddFreedomBoss",
                new Type[] { typeof(BaseBoss), typeof(bool) });
        }

        static void Prefix(BaseBoss b)
        {
            MotherBossStageController.TrackFreedomBossRegistration(b);
        }

        static void Postfix(BaseBoss b)
        {
            MotherBossStageController.ApplyFreedomBossState(b);
        }
    }

    [HarmonyPatch]
    [Obfuscation(Exclude = true, ApplyToMembers = true, Feature = "-rename", StripAfterObfuscation = false)]
    public static class Patch_BossImpl_RefreshBornPoint_MotherClear
    {
        static MethodBase TargetMethod()
        {
            return AccessTools.Method(typeof(BossImpl), "RefreshBornPoint",
                new Type[] { typeof(Vector3) });
        }

        static void Prefix(BossImpl __instance, Vector3 point)
        {
            MotherBossStageController.TrackFreedomBossBorn(__instance, point);
        }
    }

    [HarmonyPatch]
    [Obfuscation(Exclude = true, ApplyToMembers = true, Feature = "-rename", StripAfterObfuscation = false)]
    public static class Patch_BossImpl_SetSlotAttackStatus_MotherClear
    {
        static MethodBase TargetMethod()
        {
            return AccessTools.Method(typeof(BossImpl), "SetSlotAttackStatusAndPosition",
                new Type[] { typeof(int), typeof(AttackStatus), typeof(Vector3) });
        }

        static bool Prefix(BossImpl __instance, AttackStatus status)
        {
            if (status == AttackStatus.kAttackOff ||
                !MotherBossStageController.ShouldBlockBossAttack(__instance))
            {
                return true;
            }

            MotherBossStageController.NotifyBlockedBossAttack(__instance, "SetSlotAttackStatus");
            return false;
        }
    }

    [HarmonyPatch]
    [Obfuscation(Exclude = true, ApplyToMembers = true, Feature = "-rename", StripAfterObfuscation = false)]
    public static class Patch_BossImpl_Fire_MotherClear
    {
        static MethodBase TargetMethod()
        {
            return AccessTools.Method(typeof(BossImpl), "Fire", new Type[] { typeof(int) });
        }

        static bool Prefix(BossImpl __instance, ref bool __result)
        {
            if (!MotherBossStageController.ShouldBlockBossAttack(__instance)) return true;

            __result = false;
            MotherBossStageController.NotifyBlockedBossAttack(__instance, "Fire");
            return false;
        }
    }

    [HarmonyPatch]
    [Obfuscation(Exclude = true, ApplyToMembers = true, Feature = "-rename", StripAfterObfuscation = false)]
    public static class Patch_BossImpl_Shoot_MotherLock
    {
        static MethodBase TargetMethod()
        {
            return AccessTools.Method(typeof(BossImpl), "Shoot",
                new Type[] { typeof(int), typeof(bool) });
        }

        static bool Prefix(BossImpl __instance)
        {
            if (!MotherBossStageController.ShouldBlockBossAttack(__instance)) return true;

            MotherBossStageController.NotifyBlockedBossAttack(__instance, "Shoot");
            return false;
        }
    }

    [HarmonyPatch]
    [Obfuscation(Exclude = true, ApplyToMembers = true, Feature = "-rename", StripAfterObfuscation = false)]
    public static class Patch_BossImpl_DoThrow_MotherLock
    {
        static MethodBase TargetMethod()
        {
            return AccessTools.Method(typeof(BossImpl), "DoThrow", Type.EmptyTypes);
        }

        static bool Prefix(BossImpl __instance)
        {
            if (!MotherBossStageController.ShouldBlockBossAttack(__instance)) return true;

            MotherBossStageController.NotifyBlockedBossAttack(__instance, "DoThrow");
            return false;
        }
    }

    [HarmonyPatch]
    [Obfuscation(Exclude = true, ApplyToMembers = true, Feature = "-rename", StripAfterObfuscation = false)]
    public static class Patch_ChannelConnection_TakeEffectFromBoss_MotherClear
    {
        static MethodBase TargetMethod()
        {
            return AccessTools.Method(typeof(ChannelConnection), "TakeEffectFromBoss",
                new Type[]
                {
                    typeof(int), typeof(short), typeof(BaseBoss), typeof(Character),
                    typeof(byte), typeof(HitType), typeof(byte)
                });
        }

        static bool Prefix(BaseBoss from)
        {
            if (!MotherBossStageController.ShouldBlockBossAttack(from)) return true;

            MotherBossStageController.NotifyBlockedBossAttack(from, "TakeEffectFromBoss");
            return false;
        }
    }

    [HarmonyPatch]
    [Obfuscation(Exclude = true, ApplyToMembers = true, Feature = "-rename", StripAfterObfuscation = false)]
    public static class Patch_BossImpl_HealthChange_MotherClear
    {
        static MethodBase TargetMethod()
        {
            return AccessTools.Method(typeof(BossImpl), "HealthChange",
                new Type[] { typeof(HitInfo) });
        }

        static void Prefix(BossImpl __instance, ref int __state)
        {
            __state = __instance == null ? 0 : __instance.hp;
        }

        static void Postfix(BossImpl __instance, int __state)
        {
            if (__instance != null)
            {
                MotherBossStageController.NotifyMotherHealthChanged(
                    __instance,
                    __state,
                    __instance.hp);
            }
        }
    }

    [HarmonyPatch]
    [Obfuscation(Exclude = true, ApplyToMembers = true, Feature = "-rename", StripAfterObfuscation = false)]
    public static class Patch_BossImpl_OnDied_MotherClear
    {
        static MethodBase TargetMethod()
        {
            return AccessTools.Method(typeof(BossImpl), "OnDied", Type.EmptyTypes);
        }

        static void Prefix(BossImpl __instance)
        {
            MotherBossStageController.NotifyMotherDied(__instance);
        }
    }
}
