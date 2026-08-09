using ASWDEBUG.Logger;
using System;
using System.Collections.Generic;
using UnityEngine;

namespace ASWDEBUG.Cheats.AutoAim
{
    internal static class ExpeditionBossLockController
    {
        private const float LockDistanceFromPlayer = 6f;

        private sealed class ManagedBossState
        {
            internal BaseBoss Boss;
            internal bool IsFreedomBoss;
            internal bool RegistrationAccepted;
            internal bool ServerDied;
            internal bool OriginalCaptured;
            internal bool OriginalActive;
            internal bool OriginalStartSync;
            internal bool HasOriginalTransform;
            internal Vector3 OriginalPosition;
            internal Quaternion OriginalRotation;
            internal bool HasBornPoint;
            internal Vector3 BornPoint;
        }

        private static readonly List<ManagedBossState> ManagedBosses =
            new List<ManagedBossState>();

        private static Level _trackedLevel;
        private static int _blockedAttackCount;
        private static int _registeredFreedomCount;
        private static int _bornPointCount;

        internal static void OnToggle(bool enabled)
        {
            MotherBossAutoClear.SetDirectShotState(false);
            if (!enabled)
            {
                RestoreManagedBosses();
            }

            ResetRuntimeState();
        }

        internal static bool Tick(Level level, Character player)
        {
            MotherBossAutoClear.SetDirectShotState(false);
            if (!MotherBossAutoClear.Enabled)
            {
                if (ManagedBosses.Count != 0)
                {
                    RestoreManagedBosses();
                    ResetRuntimeState();
                }
                return true;
            }

            if (!IsBossLevel(level))
            {
                if (_trackedLevel != null)
                {
                    RestoreManagedBosses();
                    ResetRuntimeState();
                }
                return true;
            }

            EnsureLevelContext(level);
            CaptureActiveStageBoss(level);
            CaptureExistingFreedomBosses(level);
            if (player == null) return true;

            Vector3 lockPoint = GetLockPoint(player);
            for (int i = 0; i < ManagedBosses.Count; i++)
            {
                ManagedBossState state = ManagedBosses[i];
                if (state == null || state.Boss == null) continue;

                if (!state.ServerDied) PinAndDisarmBoss(state, player, lockPoint);
            }

            return true;
        }

        internal static void TrackFreedomBossRegistration(BaseBoss boss)
        {
            if (!MotherBossAutoClear.Enabled || !IsFreedomBoss(boss)) return;

            Level level = GetCurrentLevel();
            if (!IsBossLevel(level)) return;
            EnsureLevelContext(level);

            ManagedBossState existing = FindManagedState(boss);
            ManagedBossState state = existing ?? GetOrAddState(boss, true);
            state.IsFreedomBoss = true;
            state.RegistrationAccepted = true;
            state.ServerDied = false;
            if (existing == null)
            {
                _registeredFreedomCount++;
                FileLogger.Log(
                    "MOTHER-LOCK",
                    "allow server freedom instance uid=" + SafeGetUid(boss) +
                    " bossId=" + boss.boss_id +
                    " stage=" + boss.stage_id +
                    " count=" + _registeredFreedomCount);
            }

            // Each uid is a server-side combat instance. boss_id only identifies the
            // shared template, so suppressing later instances stalls the server wave.
        }

        internal static void TrackBossBorn(BossImpl boss, Vector3 point)
        {
            if (!MotherBossAutoClear.Enabled || boss == null) return;

            Level level = GetCurrentLevel();
            if (!IsBossLevel(level)) return;
            EnsureLevelContext(level);

            bool freedom = IsFreedomBoss(boss);
            ManagedBossState state = GetOrAddState(boss, freedom);
            state.HasBornPoint = true;
            state.BornPoint = point;
            if (freedom)
            {
                state.RegistrationAccepted = true;
                state.ServerDied = false;
                _bornPointCount++;
                if (_bornPointCount <= 8 || (_bornPointCount % 50) == 0)
                {
                    FileLogger.Log(
                        "MOTHER-LOCK",
                        "allow server born point uid=" + SafeGetUid(boss) +
                        " bossId=" + boss.boss_id +
                        " count=" + _bornPointCount);
                }
            }

            // RefreshBornPoint establishes born_pos and start_sync_boss_data. The
            // lock is applied on the next Tick after the original method completes.
        }

        internal static void ApplyFreedomBossState(BaseBoss boss)
        {
            if (!MotherBossAutoClear.Enabled || boss == null) return;

            Level level = GetCurrentLevel();
            if (!IsBossLevel(level)) return;
            EnsureLevelContext(level);

            ManagedBossState state = GetOrAddState(boss, true);
            state.IsFreedomBoss = true;
            state.RegistrationAccepted = true;

            Character player = level == null ? null : level.GetPlayer();
            if (player != null && boss.start_sync_boss_data)
            {
                PinAndDisarmBoss(state, player, GetLockPoint(player));
            }
        }

        internal static bool ShouldBlockBossAttack(BaseBoss boss)
        {
            if (!MotherBossAutoClear.Enabled || boss == null) return false;
            if (!IsBossLevel(GetCurrentLevel())) return false;

            Level level = GetCurrentLevel();
            bool freedom = IsFreedomBoss(boss);
            if (!freedom && !object.ReferenceEquals(level.GetActiveStageBoss(), boss))
            {
                return false;
            }
            if (!boss.start_sync_boss_data || boss.hp <= 0) return false;

            ManagedBossState state = GetOrAddState(boss, freedom);
            state.RegistrationAccepted = true;
            if (state.ServerDied) return false;
            return true;
        }

        internal static bool IsManagedBossUid(long uid)
        {
            if (!MotherBossAutoClear.Enabled || uid == 0) return false;

            for (int i = 0; i < ManagedBosses.Count; i++)
            {
                ManagedBossState state = ManagedBosses[i];
                BaseBoss boss = state.Boss;
                if (boss != null && state.RegistrationAccepted && !state.ServerDied &&
                    boss.start_sync_boss_data && boss.hp > 0 &&
                    SafeGetUid(boss) == uid)
                {
                    return true;
                }
            }
            return false;
        }

        internal static void NotifyBlockedBossAttack(BaseBoss boss, string source)
        {
            _blockedAttackCount++;
            if (_blockedAttackCount <= 8 || (_blockedAttackCount % 50) == 0)
            {
                FileLogger.Log(
                    "MOTHER-LOCK",
                    "blocked boss attack source=" + source +
                    " uid=" + SafeGetUid(boss) +
                    " count=" + _blockedAttackCount);
            }
        }

        internal static void NotifyHealthChanged(BossImpl boss, int oldHp, int newHp)
        {
            if (boss == null || oldHp == newHp) return;

            ManagedBossState state = FindManagedState(boss);
            if (state == null || !state.RegistrationAccepted) return;

            FileLogger.Log(
                "MOTHER-LOCK",
                "server health uid=" + SafeGetUid(boss) +
                " hp=" + oldHp + "->" + newHp +
                " max=" + boss.max_hp);

            if (newHp <= 0)
            {
                state.ServerDied = true;
            }
        }

        internal static void NotifyBossDied(BossImpl boss)
        {
            ManagedBossState state = FindManagedState(boss);
            if (state == null || !state.RegistrationAccepted) return;

            state.ServerDied = true;
            FileLogger.Log(
                "MOTHER-LOCK",
                "server boss died uid=" + SafeGetUid(boss) +
                " bossId=" + boss.boss_id +
                " freedom=" + state.IsFreedomBoss);
        }

        private static void EnsureLevelContext(Level level)
        {
            if (object.ReferenceEquals(_trackedLevel, level)) return;

            if (_trackedLevel != null)
            {
                RestoreManagedBosses();
            }

            ResetRuntimeState();
            _trackedLevel = level;
            FileLogger.Log("MOTHER-LOCK", "boss level context captured");
        }

        private static void CaptureActiveStageBoss(Level level)
        {
            BaseBoss boss = level == null ? null : level.GetActiveStageBoss();
            if (boss == null || !boss.start_sync_boss_data || boss.hp <= 0) return;

            ManagedBossState state = GetOrAddState(boss, false);
            state.IsFreedomBoss = false;
            state.RegistrationAccepted = true;
        }

        private static void CaptureExistingFreedomBosses(Level level)
        {
            List<BaseBoss> bosses = level == null || level.freedom_boss_manager == null
                ? null
                : level.freedom_boss_manager.GetBosses();
            if (bosses == null) return;

            for (int i = 0; i < bosses.Count; i++)
            {
                BaseBoss boss = bosses[i];
                if (!IsFreedomBoss(boss)) continue;

                ManagedBossState state = FindManagedState(boss);
                if (state == null)
                {
                    state = GetOrAddState(boss, true);
                    _registeredFreedomCount++;
                    FileLogger.Log(
                        "MOTHER-LOCK",
                        "capture server freedom instance uid=" + SafeGetUid(boss) +
                        " bossId=" + boss.boss_id +
                        " stage=" + boss.stage_id +
                        " startSync=" + boss.start_sync_boss_data +
                        " count=" + _registeredFreedomCount);
                }
                state.IsFreedomBoss = true;
                state.RegistrationAccepted = true;
            }
        }

        private static ManagedBossState GetOrAddState(BaseBoss boss, bool freedom)
        {
            ManagedBossState state = FindManagedState(boss);
            if (state != null) return state;

            state = new ManagedBossState();
            state.Boss = boss;
            state.IsFreedomBoss = freedom;
            ManagedBosses.Add(state);
            return state;
        }

        private static ManagedBossState FindManagedState(BaseBoss boss)
        {
            if (boss == null) return null;

            long uid = SafeGetUid(boss);
            for (int i = 0; i < ManagedBosses.Count; i++)
            {
                ManagedBossState state = ManagedBosses[i];
                BaseBoss existing = state.Boss;
                if (object.ReferenceEquals(existing, boss) ||
                    (uid != 0 && existing != null && SafeGetUid(existing) == uid))
                {
                    return state;
                }
            }
            return null;
        }

        private static void CaptureOriginalState(ManagedBossState state)
        {
            if (state == null || state.OriginalCaptured || state.Boss == null) return;

            BaseBoss boss = state.Boss;
            if (!boss.checkResourceLoad()) return;

            state.OriginalCaptured = true;
            state.OriginalStartSync = boss.start_sync_boss_data;
            state.OriginalActive = boss.GetActive();
            Transform transform = boss.getTransfrom();
            if (transform != null)
            {
                state.HasOriginalTransform = true;
                state.OriginalPosition = transform.position;
                state.OriginalRotation = transform.rotation;
            }
        }

        private static void PinAndDisarmBoss(
            ManagedBossState state,
            Character player,
            Vector3 lockPoint)
        {
            BaseBoss boss = state == null ? null : state.Boss;
            if (boss == null || player == null || !state.RegistrationAccepted ||
                !boss.start_sync_boss_data || boss.hp <= 0 ||
                !boss.checkResourceLoad() || !boss.GetActive())
            {
                return;
            }

            CaptureOriginalState(state);
            try
            {
                boss.SetUpdatePostion(false);
                boss.UseGravity(false, false);
                boss.SetWeaponEnable(false);
                ClearAttackState(boss as BossImpl);

                Transform transform = boss.getTransfrom();
                if (transform == null) return;
                boss.SetPosition(lockPoint);
                transform.position = lockPoint;

                Vector3 facePlayer = player.transform.position - lockPoint;
                facePlayer.y = 0f;
                if (facePlayer.sqrMagnitude > 0.0001f)
                {
                    transform.rotation = Quaternion.LookRotation(
                        facePlayer.normalized,
                        Vector3.up);
                }
            }
            catch (Exception e)
            {
                FileLogger.Log(
                    "MOTHER-LOCK",
                    "pin boss failed uid=" + SafeGetUid(boss) + " error=" + e.Message);
            }
        }

        private static void ClearAttackState(BossImpl boss)
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

        private static Vector3 GetLockPoint(Character player)
        {
            Vector3 forward = player.transform.forward;
            forward.y = 0f;
            if (forward.sqrMagnitude <= 0.0001f && Camera.main != null)
            {
                forward = Camera.main.transform.forward;
            }
            forward.y = 0f;
            if (forward.sqrMagnitude <= 0.0001f) forward = Vector3.forward;
            return player.transform.position + forward.normalized * LockDistanceFromPlayer;
        }

        private static void RestoreManagedBosses()
        {
            int restored = 0;
            for (int i = 0; i < ManagedBosses.Count; i++)
            {
                ManagedBossState state = ManagedBosses[i];
                BaseBoss boss = state == null ? null : state.Boss;
                if (boss == null || !state.OriginalCaptured) continue;

                try
                {
                    if (state.ServerDied) continue;

                    boss.SetUpdatePostion(true);
                    boss.UseGravity(true, true);
                    boss.start_sync_boss_data = state.OriginalStartSync;
                    if (state.HasOriginalTransform && boss.getTransfrom() != null)
                    {
                        boss.getTransfrom().position = state.OriginalPosition;
                        boss.getTransfrom().rotation = state.OriginalRotation;
                    }
                    if (boss.checkResourceLoad())
                    {
                        boss.SetActive(state.OriginalActive);
                        boss.SetWeaponEnable(state.OriginalActive);
                    }
                    restored++;
                }
                catch (Exception e)
                {
                    FileLogger.Log(
                        "MOTHER-LOCK",
                        "restore boss failed uid=" + SafeGetUid(boss) +
                        " error=" + e.Message);
                }
            }

            if (restored != 0)
            {
                FileLogger.Log("MOTHER-LOCK", "restored bosses=" + restored);
            }
        }

        private static void ResetRuntimeState()
        {
            ManagedBosses.Clear();
            _trackedLevel = null;
            _blockedAttackCount = 0;
            _registeredFreedomCount = 0;
            _bornPointCount = 0;
        }

        private static bool IsBossLevel(Level level)
        {
            return level != null &&
                   level.game_type == RoomInfo.GameType.kGameTypeBoss;
        }

        private static bool IsFreedomBoss(BaseBoss boss)
        {
            try
            {
                return boss != null &&
                       boss.GetBossType() == CharacterType.kFreedomBossType;
            }
            catch
            {
                return false;
            }
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
    }

}
