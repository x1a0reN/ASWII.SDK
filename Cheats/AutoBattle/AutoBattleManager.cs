using System;
using System.Collections.Generic;
using ASWDEBUG.Cheats.Player;
using ASWDEBUG.Cheats.SurvivalBot;
using ASWDEBUG.Logger;
using UnityEngine;

namespace ASWDEBUG.Cheats.AutoBattle
{
    public static class SurvivalCombatAdapter
    {
        private const float CameraPitchOffset = -11.309932f;
        private const float CornerReachDistance = 0.65f;
        private static readonly List<Vector3> Path = new List<Vector3>(48);
        private static readonly List<bool> JumpFlags = new List<bool>(48);
        private static int _pathIndex;
        private static Vector3 _destination;
        private static bool _hasDestination;
        private static float _nextRepath;
        private static float _nextJumpAt;
        private static float _nextFireAt;
        private static float _nextSkillAt;
        private static float _nextWeaponSwitchAt;
        private static float _nextRoleSpecialAt;
        private static float _nextScopeAt;
        private static float _nextCombatTraceAt;
        private static Vector3 _lastPathProgressPosition;
        private static float _lastPathProgressAt;
        private static float _lastActualPathProgressAt;
        private static bool _pathSearchPending;
        private static bool _currentPathPartial;
        private static float _currentPathResidual;
        private static string _navigationIntent;
        private static int _destinationRevision;
        private static int _pathDestinationRevision;
        private static int _stuckRecoveryCount;
        private static float _nextStuckRecoveryAt;
        private static float _nextWallRecoveryAt;
        private static int _wallRecoveryCount;
        private static int _recoverySideSign = 1;
        private static float _recoverySideUntil;
        private static Vector3 _wallRecoveryDirection;
        private static int _pendingSideSign = 1;
        private static float _pendingSideUntil;
        private static float _pendingLocalStartedAt;
        private static Vector3 _pendingLocalOrigin;
        private static int _lastProgressPathIndex;
        private static float _lastWaypointDistance;
        private static Character _aimTarget;
        private static int _aimPreparedFrame;
        private static float _aimPreparedAt;
        private static WeaponBase _observedWeapon;
        private static int _observedClip;
        private static float _weaponUnavailableSince;
        private static WeaponBase _fireRequestWeapon;
        private static float _fireRequestStartedAt;
        private static float _fireRequestFirstAt;
        private static WeaponBase _temporarilyBlockedWeapon;
        private static float _blockedWeaponUntil;
        private static float _ignoreActualShotUntil;
        private static WeaponBase _aimWeapon;
        private static Vector3 _lastAimPoint;
        private static Character _bodyConfirmTarget;
        private static float _bodyConfirmStartedAt;
        private static WeaponBase _scopeWeapon;
        private static bool _scopeRequestPending;
        private static bool _scopeRequestedOpen;
        private static float _scopeRequestedAt;

        public static string LastPath = "-";
        public static string LastPathProvider = "-";
        public static string LastPathIntent = "-";
        public static string LastAction = "-";
        public static string CurrentRole = "通用";
        public static float LastActualPathProgressAt
        {
            get { return _lastActualPathProgressAt; }
        }

        internal static bool CopyActiveRoute(List<Vector3> output)
        {
            if (output == null) return false;
            output.Clear();
            int start = Mathf.Clamp(_pathIndex, 0, Path.Count);
            for (int i = start; i < Path.Count; i++) output.Add(Path[i]);
            if (_hasDestination &&
                (output.Count == 0 || XzDistance(output[output.Count - 1], _destination) > 0.20f))
                output.Add(_destination);
            return output.Count > 0;
        }

        public static void ResetSurvivalRuntime(string reason)
        {
            AutoBattleInput.ClearAll();
            Path.Clear();
            JumpFlags.Clear();
            _pathIndex = 0;
            _destination = Vector3.zero;
            _hasDestination = false;
            _nextRepath = 0f;
            _nextJumpAt = 0f;
            _nextFireAt = 0f;
            _nextSkillAt = 0f;
            _nextWeaponSwitchAt = 0f;
            _nextRoleSpecialAt = 0f;
            _nextScopeAt = 0f;
            _nextCombatTraceAt = 0f;
            _lastPathProgressPosition = Vector3.zero;
            _lastPathProgressAt = 0f;
            _lastActualPathProgressAt = 0f;
            _pathSearchPending = false;
            _currentPathPartial = false;
            _currentPathResidual = 0f;
            _navigationIntent = null;
            _destinationRevision = 0;
            _pathDestinationRevision = -1;
            _stuckRecoveryCount = 0;
            _nextStuckRecoveryAt = 0f;
            _nextWallRecoveryAt = 0f;
            _wallRecoveryCount = 0;
            _recoverySideSign = 1;
            _recoverySideUntil = 0f;
            _wallRecoveryDirection = Vector3.zero;
            _pendingSideSign = 1;
            _pendingSideUntil = 0f;
            _pendingLocalStartedAt = 0f;
            _pendingLocalOrigin = Vector3.zero;
            _lastProgressPathIndex = -1;
            _lastWaypointDistance = float.MaxValue;
            _aimTarget = null;
            _aimPreparedFrame = -1;
            _aimPreparedAt = 0f;
            _observedWeapon = null;
            _observedClip = -1;
            _weaponUnavailableSince = 0f;
            _fireRequestWeapon = null;
            _fireRequestStartedAt = 0f;
            _fireRequestFirstAt = 0f;
            _temporarilyBlockedWeapon = null;
            _blockedWeaponUntil = 0f;
            _ignoreActualShotUntil = 0f;
            _aimWeapon = null;
            _lastAimPoint = Vector3.zero;
            _bodyConfirmTarget = null;
            _bodyConfirmStartedAt = 0f;
            _scopeWeapon = null;
            _scopeRequestPending = false;
            _scopeRequestedOpen = false;
            _scopeRequestedAt = 0f;
            LastPath = reason;
            LastPathProvider = "-";
            LastPathIntent = "-";
            LastAction = reason;
        }

        public static void MarkSurvivalActivity(Character player)
        {
            AutoBattleInput.MarkActivity(0.35f);
            CurrentRole = SurvivalBotSettings.RoleStrategyEnabled ? DetectRole(player) : "通用";
            TryUseRoleMaintenance(player);
            try { if (player != null) player.ResetIdleMenu(); } catch { }
        }

        public static Vector3 NavigateSurvival(Character player, Vector3 destination, bool tacticalMove, string intent)
        {
            if (player == null || player.transform == null) return Vector3.zero;
            Vector3 playerPosition = player.transform.position;
            intent = string.IsNullOrEmpty(intent) ? "survival" : intent;
            if (!string.Equals(_navigationIntent, intent, StringComparison.Ordinal))
            {
                ClearPath();
                _navigationIntent = intent;
                _hasDestination = false;
                _nextRepath = 0f;
                LastPath = "intent_changed";
            }
            LastPathIntent = intent;

            bool firstDestination = !_hasDestination;
            bool attackChase = string.Equals(intent, "attack_chase", StringComparison.Ordinal);
            float destinationDelta = firstDestination ? float.MaxValue : XzDistance(_destination, destination);
            float destinationYDelta = firstDestination ? float.MaxValue : Mathf.Abs(_destination.y - destination.y);
            float softDestinationThreshold = attackChase ? 4.5f : (tacticalMove ? 2f : 2.5f);
            float hardDestinationThreshold = attackChase ? 11f : 6f;
            bool softDestinationChanged = !firstDestination &&
                (destinationDelta > softDestinationThreshold || destinationYDelta > 1.25f);
            bool hardDestinationChanged = firstDestination || destinationDelta > hardDestinationThreshold || destinationYDelta > 2.5f;
            bool pendingWithoutPath = attackChase && _pathSearchPending &&
                (Path.Count == 0 || _pathIndex >= Path.Count);
            bool commitDestination = firstDestination || hardDestinationChanged ||
                (softDestinationChanged && !pendingWithoutPath && Time.time >= _nextRepath);
            if (hardDestinationChanged)
            {
                ClearPath();
                _nextRepath = 0f;
                _lastPathProgressPosition = playerPosition;
                _lastPathProgressAt = Time.time;
                _lastActualPathProgressAt = Time.time;
            }
            if (commitDestination)
            {
                if (firstDestination || destinationDelta > 0.2f || destinationYDelta > 0.2f)
                    _destinationRevision++;
                _destination = destination;
                _hasDestination = true;
            }
            else if (_hasDestination)
            {
                destination = _destination;
            }

            bool needRepath = Path.Count == 0 || _pathIndex >= Path.Count;
            if (!needRepath && softDestinationChanged && commitDestination) needRepath = true;
            if ((_pathSearchPending || needRepath) && Time.time >= _nextRepath)
            {
                _nextRepath = _pathSearchPending
                    ? Time.time
                    : Time.time + (attackChase ? 0.70f : (tacticalMove ? 0.24f : 0.36f));
                BuildPath(player, playerPosition, _destination);
            }

            if (Path.Count == 0 || _pathIndex >= Path.Count)
            {
                if (_pathSearchPending)
                {
                    if (_pendingLocalStartedAt <= 0f)
                    {
                        _pendingLocalStartedAt = Time.time;
                        _pendingLocalOrigin = playerPosition;
                    }
                    float pendingAge = Time.time - _pendingLocalStartedAt;
                    float pendingTravel = XzDistance(_pendingLocalOrigin, playerPosition);
                    if (pendingAge >= 1.25f)
                    {
                        ClearPath();
                        _nextRepath = Time.time + 0.2f;
                        LastPath = "path_pending_timeout";
                        return Vector3.zero;
                    }
                    if (pendingAge <= 0.48f && pendingTravel <= 1.6f)
                    {
                        Vector3 localAdvance = TryPendingLocalAdvance(player, _destination);
                        if (localAdvance.sqrMagnitude > 0.01f)
                        {
                            LastPath = "path_pending_local";
                            return localAdvance;
                        }
                    }
                    LastPath = "path_pending_hold";
                }
                return Vector3.zero;
            }
            _pendingLocalStartedAt = 0f;
            Vector3 next = Path[_pathIndex];
            while (_pathIndex < Path.Count - 1 && XzDistance(playerPosition, next) <= CornerReachDistance)
            {
                _pathIndex++;
                next = Path[_pathIndex];
            }

            float distance = XzDistance(playerPosition, next);
            if (_pathIndex == Path.Count - 1 && distance <= CornerReachDistance)
            {
                if (_currentPathPartial || _currentPathResidual > CornerReachDistance)
                {
                    _pathSearchPending = true;
                    _nextRepath = 0f;
                    LastPath = "path_partial_continue";
                }
                else
                {
                    LastPath = "arrived";
                }
                return Vector3.zero;
            }

            Vector3 direction = next - playerPosition;
            direction.y = 0f;
            if (direction.sqrMagnitude < 0.01f) return Vector3.zero;
            direction.Normalize();

            bool jump = _pathIndex < JumpFlags.Count && JumpFlags[_pathIndex];
            if (jump && distance <= 4.3f && Time.time >= _nextJumpAt)
            {
                if (!AutoBattleRoutePlanner.CanExecuteJump(playerPosition, next, CreateCapabilities(player), player.transform.root))
                {
                    ClearPath();
                    _nextRepath = 0f;
                    LastPath = "jump_lane_blocked";
                    return Vector3.zero;
                }
                AutoBattleInput.PressAction(ActionType.kActionJump, 0.11f);
                AutoBattleInput.HoldAction(ActionType.kActionJump, 0.26f);
                _nextJumpAt = Time.time + 0.5f;
            }

            if (!jump && AutoBattleRoutePlanner.HasForwardBlock(playerPosition, direction, player.transform.root))
            {
                if (Time.time >= _nextWallRecoveryAt || _wallRecoveryDirection.sqrMagnitude < 0.01f)
                {
                    _nextWallRecoveryAt = Time.time + 0.35f;
                    _wallRecoveryCount++;
                    ClearPath();
                    _nextRepath = 0f;
                    _wallRecoveryDirection = BuildStableRecoveryDirection(player, direction, 0.72f);
                }
                LastPath = "wall_repath";
                return _wallRecoveryDirection;
            }
            _wallRecoveryCount = 0;
            _wallRecoveryDirection = Vector3.zero;

            bool waypointProgress = _lastProgressPathIndex != _pathIndex ||
                distance + 0.25f < _lastWaypointDistance;
            if (waypointProgress)
            {
                _lastPathProgressPosition = playerPosition;
                _lastPathProgressAt = Time.time;
                _lastActualPathProgressAt = Time.time;
                _lastProgressPathIndex = _pathIndex;
                _lastWaypointDistance = distance;
                _stuckRecoveryCount = 0;
            }
            else if (Time.time - _lastPathProgressAt >= 0.65f && Time.time >= _nextStuckRecoveryAt)
            {
                _stuckRecoveryCount++;
                _nextStuckRecoveryAt = Time.time + 0.45f;
                _lastPathProgressPosition = playerPosition;
                _lastPathProgressAt = Time.time;
                Vector3 forward = player.transform.forward;
                forward.y = 0f;
                if (forward.sqrMagnitude < 0.01f) forward = Vector3.forward;
                forward.Normalize();
                if (_stuckRecoveryCount >= 2)
                {
                    _recoverySideSign = -_recoverySideSign;
                    _recoverySideUntil = Time.time + 0.7f;
                }
                if (_stuckRecoveryCount >= 3)
                {
                    ClearPath();
                    _nextRepath = 0f;
                    _stuckRecoveryCount = 0;
                    LastPath = "stuck_repath";
                }
                else
                {
                    LastPath = "stuck_local_recovery#" + _stuckRecoveryCount;
                }
                if (AutoBattleRoutePlanner.ShouldJumpForwardObstacle(playerPosition, forward, player.transform.root))
                {
                    AutoBattleInput.PressAction(ActionType.kActionJump, 0.11f);
                    AutoBattleInput.HoldAction(ActionType.kActionJump, 0.22f);
                }
                return BuildStableRecoveryDirection(player, forward, 1f);
            }

            LastPath = (_pathSearchPending ? "path_pending_follow " : "path ") +
                (_pathIndex + 1) + "/" + Path.Count + (jump ? " jump" : string.Empty);
            return ApplyLocalAvoidance(player, direction);
        }

        public static Vector3 NavigatePursuit(Character player, Vector3 liveTargetPosition)
        {
            if (player == null || player.transform == null) return Vector3.zero;
            Vector3 playerPosition = player.transform.position;
            Vector3 direction = liveTargetPosition - playerPosition;
            float verticalDelta = Mathf.Abs(direction.y);
            direction.y = 0f;
            float distance = direction.magnitude;
            if (distance <= 0.65f)
            {
                LastPathIntent = "attack_chase";
                LastPath = "attack_chase_face_range";
                return Vector3.zero;
            }
            direction /= distance;

            bool directAdvance = verticalDelta <= 1.25f &&
                !AutoBattleRoutePlanner.HasForwardBlock(playerPosition, direction, player.transform.root);
            if (!directAdvance)
                return NavigateSurvival(player, liveTargetPosition, false, "attack_chase");

            if (!string.Equals(_navigationIntent, "attack_chase", StringComparison.Ordinal))
            {
                ClearPath();
                _navigationIntent = "attack_chase";
                _lastPathProgressPosition = playerPosition;
                _lastPathProgressAt = Time.time;
                _lastActualPathProgressAt = Time.time;
            }
            else if (Path.Count > 0 || _pathSearchPending)
            {
                ClearPath();
            }

            if (XzDistance(_lastPathProgressPosition, playerPosition) >= 0.35f)
            {
                _lastPathProgressPosition = playerPosition;
                _lastPathProgressAt = Time.time;
                _lastActualPathProgressAt = Time.time;
            }
            _destination = liveTargetPosition;
            _hasDestination = true;
            _nextRepath = 0f;
            LastPathIntent = "attack_chase";
            LastPathProvider = "direct_pursuit";
            LastPath = "attack_chase_direct";
            return direction;
        }

        private static Vector3 ApplyLocalAvoidance(Character player, Vector3 desired)
        {
            if (player == null || player.transform == null || desired.sqrMagnitude < 0.01f) return desired;
            Vector3 correction = Vector3.zero;
            try
            {
                Level level = ASSingleton<Level>.Instance;
                List<Character> characters = level == null ? null : level.GetCharacters();
                if (characters == null) return desired;
                Vector3 origin = player.transform.position;
                for (int i = 0; i < characters.Count; i++)
                {
                    Character other = characters[i];
                    if (other == null || other == player || other.transform == null || other.IsDied) continue;
                    Vector3 offset = origin - other.transform.position;
                    offset.y = 0f;
                    float distance = offset.magnitude;
                    if (distance < 0.05f || distance > 2.1f) continue;
                    Vector3 towardOther = -offset / distance;
                    if (Vector3.Dot(desired, towardOther) < 0.15f) continue;
                    correction += offset.normalized * ((2.1f - distance) / 2.1f);
                }
            }
            catch { }
            if (correction.sqrMagnitude < 0.001f) return desired;
            Vector3 result = desired + correction * 1.25f;
            result.y = 0f;
            return result.sqrMagnitude < 0.01f ? desired : result.normalized;
        }

        private static Vector3 TryPendingLocalAdvance(Character player, Vector3 destination)
        {
            Vector3 forward = destination - player.transform.position;
            forward.y = 0f;
            if (forward.sqrMagnitude < 0.01f) return Vector3.zero;
            forward.Normalize();
            UpdatePendingSide(player, forward);
            float[] offsets = { 0f, 28f, 50f, 72f, -28f, -50f, -72f };
            for (int i = 0; i < offsets.Length; i++)
            {
                Vector3 candidate = Quaternion.AngleAxis(offsets[i] * _pendingSideSign, Vector3.up) * forward;
                candidate.y = 0f;
                if (candidate.sqrMagnitude < 0.01f) continue;
                candidate.Normalize();
                if (!AutoBattleRoutePlanner.HasForwardBlock(player.transform.position, candidate, player.transform.root))
                    return candidate;
            }
            return Vector3.zero;
        }

        private static Vector3 BuildStableRecoveryDirection(Character player, Vector3 forward, float sideWeight)
        {
            forward.y = 0f;
            if (forward.sqrMagnitude < 0.01f) forward = player.transform.forward;
            forward.y = 0f;
            if (forward.sqrMagnitude < 0.01f) forward = Vector3.forward;
            forward.Normalize();
            UpdateRecoverySide(player, forward);
            Vector3 side = Vector3.Cross(Vector3.up, forward) * _recoverySideSign;
            Vector3[] candidates =
            {
                forward * 0.7f + side * sideWeight,
                forward * 0.7f - side * sideWeight,
                side,
                -side
            };
            for (int i = 0; i < candidates.Length; i++)
            {
                Vector3 result = candidates[i];
                result.y = 0f;
                if (result.sqrMagnitude < 0.01f) continue;
                result.Normalize();
                if (AutoBattleRoutePlanner.HasForwardBlock(player.transform.position, result, player.transform.root))
                    continue;
                if (i == 1 || i == 3) _recoverySideSign = -_recoverySideSign;
                return result;
            }
            return Vector3.zero;
        }

        private static void UpdateRecoverySide(Character player, Vector3 forward)
        {
            if (Time.time < _recoverySideUntil) return;
            Vector3 left = Quaternion.AngleAxis(-48f, Vector3.up) * forward;
            Vector3 right = Quaternion.AngleAxis(48f, Vector3.up) * forward;
            bool leftBlocked = AutoBattleRoutePlanner.HasForwardBlock(player.transform.position, left, player.transform.root);
            bool rightBlocked = AutoBattleRoutePlanner.HasForwardBlock(player.transform.position, right, player.transform.root);
            if (leftBlocked != rightBlocked) _recoverySideSign = leftBlocked ? 1 : -1;
            _recoverySideUntil = Time.time + 1.15f;
        }

        private static void UpdatePendingSide(Character player, Vector3 forward)
        {
            if (Time.time < _pendingSideUntil) return;
            Vector3 left = Quaternion.AngleAxis(-48f, Vector3.up) * forward;
            Vector3 right = Quaternion.AngleAxis(48f, Vector3.up) * forward;
            bool leftBlocked = AutoBattleRoutePlanner.HasForwardBlock(player.transform.position, left, player.transform.root);
            bool rightBlocked = AutoBattleRoutePlanner.HasForwardBlock(player.transform.position, right, player.transform.root);
            if (leftBlocked != rightBlocked) _pendingSideSign = leftBlocked ? 1 : -1;
            _pendingSideUntil = Time.time + 0.65f;
        }

        public static bool TryUseSurvivalDefense(Character player, int defenseMode)
        {
            if (player == null || Time.time < _nextSkillAt) return false;
            CurrentRole = SurvivalBotSettings.RoleStrategyEnabled ? DetectRole(player) : "通用";
            if (defenseMode == 3) return false;

            if (defenseMode == 2)
            {
                if (TryUseSkill(player, 1, "shield")) return true;
                if (TryUseHidden(player)) return true;
            }
            else
            {
                if (TryUseHidden(player)) return true;
                if (TryUseSkill(player, 1, "shield")) return true;
            }

            if (CurrentRole == "医疗/守护")
            {
                float hp = HealthPercent(player);
                if (hp <= 58f && TryUseSkill(player, 0, "medic_heal_self")) return true;
                if (hp <= 72f && TryUseSkill(player, 14, "medic_capsule_self")) return true;
            }
            else if (CurrentRole == "重装")
            {
                if (TryUseSkill(player, 4, "heavy_gallop_contact")) return true;
                if (HealthPercent(player) <= 70f && TryUseSkill(player, 7, "heavy_tenacity_lowhp")) return true;
            }

            if (TryUseSkill(player, 4, "displace")) return true;
            return TryUseSkill(player, 11, "displace");
        }

        public static bool SurvivalHasStrictFireLine(Character player, Character target, Camera camera)
        {
            Vector3 aimPoint;
            return TryGetStrictAimPoint(player, target, camera, out aimPoint);
        }

        public static bool SurvivalHasEmergencyFireLine(Character player, Character target, Camera camera)
        {
            Vector3 aimPoint;
            return TryGetEmergencyAimPoint(player, target, camera, out aimPoint);
        }

        public static bool AttackSurvival(Character player, Character target, Camera camera, out bool strictLine, out float distance)
        {
            strictLine = false;
            distance = 99999f;
            bool actualShot = ObserveWeaponShot(player);
            if (player == null || target == null || camera == null || target.IsDied || target.Is_Viewer) return actualShot;
            try { if (target.GetHidden()) return actualShot; } catch { return actualShot; }

            distance = Vector3.Distance(player.transform.position, target.transform.position);
            Vector3 aimPoint;
            strictLine = TryGetStrictAimPoint(player, target, camera, out aimPoint);
            if (!strictLine)
            {
                LastAction = "strict_los_temporarily_blocked";
                AutoBattleInput.ClearFire();
                return actualShot;
            }

            CurrentRole = SurvivalBotSettings.RoleStrategyEnabled ? DetectRole(player) : "通用";
            EnsureRoleWeapon(player, distance);
            if (Time.time < _nextWeaponSwitchAt)
            {
                LastAction = "role_weapon_switch_wait";
                return actualShot;
            }
            if (IsTemporarilyBlocked(player.mWeapon))
            {
                LastAction = "role_weapon_retry_wait";
                return actualShot;
            }
            if (!IsOperationalGun(player.mWeapon))
            {
                HandleUnavailableWeapon(player);
                return actualShot;
            }
            _weaponUnavailableSince = 0f;
            if (!EnsureSniperScope(player.mWeapon)) return actualShot;

            if (!PrepareBodyAim(player, target, camera, aimPoint, false))
            {
                return actualShot;
            }

            bool exact = false;
            if (!ConfirmBodyAim(target, false, 0.12f, out exact))
            {
                LastAction = "body_confirm_wait";
                return actualShot;
            }
            if (Time.time < _nextFireAt) return actualShot;
            AutoBattleInput.RequestFire(0.14f);
            _nextFireAt = Time.time + 0.05f;
            TrackFireRequest(player.mWeapon);
            LastAction = exact ? "fire_request_exact_body" : "fire_request_strict_body_fallback";
            return actualShot;
        }

        public static bool AttackEmergency(Character player, Character target, Camera camera, out bool strictLine,
            out float distance)
        {
            strictLine = false;
            distance = 99999f;
            bool actualShot = ObserveWeaponShot(player);
            if (player == null || target == null || camera == null || target.IsDied || target.Is_Viewer) return actualShot;

            distance = Vector3.Distance(player.transform.position, target.transform.position);
            bool hidden;
            try { hidden = target.GetHidden(); }
            catch { return actualShot; }
            if (hidden && XzDistance(player.transform.position, target.transform.position) > 6f)
            {
                LastAction = "emergency_hidden_out_of_range";
                return actualShot;
            }
            CurrentRole = SurvivalBotSettings.RoleStrategyEnabled ? DetectRole(player) : "通用";

            Vector3 aimPoint;
            strictLine = TryGetEmergencyAimPoint(player, target, camera, out aimPoint);
            if (!strictLine)
            {
                LastAction = "emergency_strict_los_blocked";
                AutoBattleInput.ClearFire();
                return actualShot;
            }

            EnsureEmergencyWeapon(player, distance);
            if (Time.time < _nextWeaponSwitchAt)
            {
                LastAction = "emergency_weapon_switch_wait";
                return actualShot;
            }
            if (IsTemporarilyBlocked(player.mWeapon))
            {
                LastAction = "emergency_weapon_retry_wait";
                return actualShot;
            }
            if (!IsOperationalGun(player.mWeapon))
            {
                HandleUnavailableWeapon(player);
                return actualShot;
            }
            _weaponUnavailableSince = 0f;
            if (!EnsureSniperScope(player.mWeapon)) return actualShot;

            if (!PrepareBodyAim(player, target, camera, aimPoint, true))
            {
                return actualShot;
            }

            bool exact = hidden;
            if (!ConfirmBodyAim(target, hidden, 0.10f, out exact))
            {
                LastAction = "emergency_body_confirm_wait";
                return actualShot;
            }
            if (Time.time < _nextFireAt) return actualShot;
            AutoBattleInput.RequestFire(0.16f);
            _nextFireAt = Time.time + 0.04f;
            TrackFireRequest(player.mWeapon);
            LastAction = hidden ? "emergency_fire_request_hidden_body" : "emergency_fire_request_body";
            return actualShot;
        }

        public static void LogCombatState(Character player, Character target, bool strictLine, float distance, bool fired)
        {
            if (Time.time < _nextCombatTraceAt) return;
            _nextCombatTraceAt = Time.time + 1f;

            string weapon = "-";
            string scope = "-";
            string readiness = "-";
            try
            {
                weapon = GetWeaponType(player == null ? null : player.mWeapon).ToString();
                SniperGunController sniper = player == null ? null : player.mWeapon as SniperGunController;
                if (sniper != null) scope = sniper.currentSight.ToString();
                readiness = DescribeWeaponReadiness(player == null ? null : player.mWeapon);
            }
            catch { }

            string targetId = target == null ? "-" : target.uid.ToString();
            FileLogger.Log("AUTO-BATTLE][COMBAT", "role=" + CurrentRole + " target=" + targetId +
                " dist=" + distance.ToString("0.0") + " los=" + strictLine + " fired=" + fired +
                " weapon=" + weapon + " scope=" + scope + " ready=" + readiness +
                " action=" + LastAction + " path=" + LastPath);
        }

        private static string DescribeWeaponReadiness(WeaponBase weapon)
        {
            if (weapon == null) return "weapon_null";
            try
            {
                bool ready = weapon.Ready();
                bool infoReady = weapon.info != null && weapon.info.cool_down_ready;
                float cooling = weapon.info == null ? -1f : (float)weapon.info.cooling;
                return (ready ? "1" : "0") +
                    ",clip=" + weapon.clip +
                    ",reload=" + ((bool)weapon.reloading ? "1" : "0") +
                    ",cool=" + (weapon.cool_down_ready ? "1" : "0") +
                    ",infoCool=" + (infoReady ? "1" : "0") +
                    ",cooling=" + cooling.ToString("0.00") +
                    ",change=" + ((float)weapon.change_in_time).ToString("0.00");
            }
            catch (Exception ex)
            {
                return "error:" + ex.GetType().Name;
            }
        }

        public static void LookSurvival(Character player, Camera camera, Vector3 point)
        {
            if (player == null || camera == null || player.camera == null) return;
            ApplyLook(player, camera, point, 240f, 3.5f);
        }

        public static bool CloseSurvivalScope(Character player)
        {
            return SetSniperScope(player == null ? null : player.mWeapon, false);
        }

        public static void CancelSurvivalAttack()
        {
            bool hadPendingShot = _fireRequestWeapon != null;
            AutoBattleInput.ClearFire();
            _aimTarget = null;
            _aimWeapon = null;
            _lastAimPoint = Vector3.zero;
            _bodyConfirmTarget = null;
            _bodyConfirmStartedAt = 0f;
            _fireRequestWeapon = null;
            _fireRequestStartedAt = 0f;
            _fireRequestFirstAt = 0f;
            _observedWeapon = null;
            _observedClip = -1;
            if (hadPendingShot) _ignoreActualShotUntil = Time.time + 0.18f;
        }

        public static void SuspendSurvivalNavigation(string intent)
        {
            intent = string.IsNullOrEmpty(intent) ? "suspended" : intent;
            if (string.Equals(_navigationIntent, intent, StringComparison.Ordinal)) return;
            ClearPath();
            _navigationIntent = intent;
            _hasDestination = false;
            _nextRepath = 0f;
            LastPathIntent = intent;
            LastPath = "navigation_suspended";
        }

        private static void BuildPath(Character player, Vector3 from, Vector3 to)
        {
            bool hadUsablePath = Path.Count > 0 && _pathIndex >= 0 && _pathIndex < Path.Count;
            float previousResidual = _currentPathResidual;
            AutoBattleRouteCapabilities capabilities = CreateCapabilities(player);
            AutoBattleRouteResult route = AutoBattleRoutePlanner.BuildRoute(from, to, player.transform.root, capabilities);
            if (route == null)
            {
                if (!hadUsablePath) LastPath = "route_null";
                return;
            }

            LastPathProvider = route.Provider ?? "-";
            if (!route.Success)
            {
                if (route.Provider == "phys_grid_2_5d_pending")
                {
                    _pathSearchPending = true;
                    _nextRepath = Time.time;
                    LastPath = hadUsablePath ? "path_pending_follow" : "path_pending_hold";
                }
                else
                {
                    _pathSearchPending = false;
                    _nextRepath = Time.time + 0.6f;
                    if (!hadUsablePath) LastPath = "no_path";
                }
                return;
            }

            float candidateResidual = route.Corners.Count == 0
                ? XzDistance(from, to)
                : XzDistance(route.Corners[route.Corners.Count - 1], to);
            List<Vector3> candidatePath = new List<Vector3>(48);
            List<bool> candidateJumps = new List<bool>(48);
            AddPathPoints(route.Corners, route.JumpFlags, from, player.transform.root, candidatePath, candidateJumps);
            if (candidatePath.Count == 0)
            {
                float remaining = XzDistance(from, to);
                if (route.Partial)
                {
                    _pathSearchPending = true;
                    _nextRepath = Time.time;
                    LastPath = hadUsablePath ? "path_pending_follow" : "path_pending_hold";
                    return;
                }

                ClearPath();
                LastPath = remaining <= CornerReachDistance ? "holding_position" : "empty_route_repath";
                if (remaining > CornerReachDistance) _nextRepath = Time.time + 0.2f;
                return;
            }

            bool firstJump = candidateJumps.Count > 0 && candidateJumps[0];
            bool firstSegmentUsable = firstJump
                ? AutoBattleRoutePlanner.CanExecuteJump(from, candidatePath[0], capabilities, player.transform.root)
                : AutoBattleRoutePlanner.CanFollowSegment(from, candidatePath[0], player.transform.root);
            bool newDestinationRevision = _pathDestinationRevision != _destinationRevision;
            bool frontierImproved = newDestinationRevision || !route.Partial || !hadUsablePath || previousResidual <= 0f ||
                candidateResidual + 0.35f < previousResidual;
            if (!firstSegmentUsable || !frontierImproved)
            {
                _pathSearchPending = route.Partial;
                _nextRepath = route.Partial ? Time.time : Time.time + 0.2f;
                LastPath = hadUsablePath ? "path_candidate_keep_old" : "path_candidate_rejected";
                return;
            }

            Path.Clear();
            JumpFlags.Clear();
            Path.AddRange(candidatePath);
            JumpFlags.AddRange(candidateJumps);
            _pathIndex = 0;
            _currentPathPartial = route.Partial;
            _currentPathResidual = candidateResidual;
            _pathSearchPending = route.Partial;
            _pathDestinationRevision = _destinationRevision;
            _lastProgressPathIndex = 0;
            _lastWaypointDistance = XzDistance(from, Path[0]);
            if (!hadUsablePath || newDestinationRevision)
            {
                _lastPathProgressPosition = from;
                _lastPathProgressAt = Time.time;
                _lastActualPathProgressAt = Time.time;
            }
            _pendingLocalStartedAt = 0f;
            LastPath = route.Provider + (route.Partial ? " partial " : " ") + Path.Count + " pts";
        }

        private static void AddPathPoints(List<Vector3> points, List<bool> jumpFlags, Vector3 from,
            Transform ignoreRoot, List<Vector3> outputPath, List<bool> outputJumps)
        {
            if (points == null || points.Count == 0 || outputPath == null || outputJumps == null) return;
            if (points.Count == 1)
            {
                if (XzDistance(points[0], from) >= 0.5f)
                {
                    outputPath.Add(points[0]);
                    outputJumps.Add(jumpFlags != null && jumpFlags.Count > 0 && jumpFlags[0]);
                }
                return;
            }
            int startIndex = 0;
            float bestDistance = float.MaxValue;
            for (int i = 0; i + 1 < points.Count; i++)
            {
                Vector3 segment = points[i + 1] - points[i];
                Vector3 offset = from - points[i];
                segment.y = 0f;
                offset.y = 0f;
                if (segment.sqrMagnitude < 0.01f) continue;
                float t = Mathf.Clamp01(Vector3.Dot(offset, segment) / segment.sqrMagnitude);
                Vector3 projected = Vector3.Lerp(points[i], points[i + 1], t);
                float distance = XzDistance(projected, from);
                if (distance > 3.5f || distance > bestDistance + 0.05f) continue;
                if (!AutoBattleRoutePlanner.CanFollowSegment(from, projected, ignoreRoot)) continue;
                bestDistance = distance;
                startIndex = i + (t >= 0.30f ? 1 : 0);
            }

            if (bestDistance == float.MaxValue)
            {
                for (int i = 0; i < points.Count; i++)
                {
                    float distance = XzDistance(points[i], from);
                    if (distance >= bestDistance) continue;
                    bestDistance = distance;
                    startIndex = i;
                }
            }

            if (bestDistance > 4f) return;
            if (startIndex + 1 < points.Count)
            {
                Vector3 segment = points[startIndex + 1] - points[startIndex];
                Vector3 progressed = from - points[startIndex];
                segment.y = 0f;
                progressed.y = 0f;
                if (segment.sqrMagnitude > 0.01f &&
                    Vector3.Dot(progressed, segment) / segment.sqrMagnitude > 0.35f)
                    startIndex++;
            }

            for (int i = startIndex; i < points.Count && outputPath.Count < 48; i++)
            {
                Vector3 point = points[i];
                if (outputPath.Count == 0 && XzDistance(point, from) < 0.5f) continue;
                outputPath.Add(point);
                outputJumps.Add(jumpFlags != null && i < jumpFlags.Count && jumpFlags[i]);
            }
        }

        private static void ClearPath()
        {
            Path.Clear();
            JumpFlags.Clear();
            _pathIndex = 0;
            _pathSearchPending = false;
            _currentPathPartial = false;
            _currentPathResidual = 0f;
            _pendingLocalStartedAt = 0f;
        }

        private static AutoBattleRouteCapabilities CreateCapabilities(Character player)
        {
            AutoBattleRouteCapabilities capabilities = new AutoBattleRouteCapabilities();
            try
            {
                if (player.character_info != null)
                {
                    capabilities.JumpHeight = Mathf.Max(0.8f, player.character_info.jump_height);
                    capabilities.JumpVelocity = Mathf.Max(4f, player.character_info.jump_velocity);
                    capabilities.RunSpeed = Mathf.Max(3f, player.character_info.run_speed);
                }
            }
            catch { }
            return capabilities;
        }

        private static bool TryUseSkill(Character player, int subType, string reason)
        {
            try
            {
                if (player.character_info == null || player.character_info.slots_info == null) return false;
                ObjectBaseInfo[] slots = player.character_info.slots_info.object_info;
                if (slots == null) return false;
                for (int i = 0; i < slots.Length; i++)
                {
                    SkillInfo skill = slots[i] as SkillInfo;
                    if (skill == null || skill.sub_type != (byte)subType || !skill.cool_down_ready) continue;
                    if (!skill.CanAction() || !skill.Action()) continue;
                    skill.cool_down_ready = false;
                    _nextSkillAt = Time.time + 0.6f;
                    LastAction = reason;
                    FileLogger.Log("SURVIVAL", "defense skill=" + reason + " slot=" + skill.slot);
                    return true;
                }
            }
            catch (Exception ex)
            {
                FileLogger.Log("SURVIVAL", "skill failed: " + ex.Message);
            }
            return false;
        }

        private static bool TryUseHidden(Character player)
        {
            try
            {
                return player != null && !player.GetHidden() && TryUseSkill(player, 2, "hidden");
            }
            catch
            {
                return false;
            }
        }

        private static void TryUseRoleMaintenance(Character player)
        {
            if (player == null || Time.time < _nextSkillAt || !SurvivalBotSettings.RoleStrategyEnabled) return;
            float hp = HealthPercent(player);
            if (CurrentRole == "医疗/守护")
            {
                if (hp <= 58f && TryUseSkill(player, 0, "medic_heal_self")) return;
                if (hp <= 72f) TryUseSkill(player, 14, "medic_capsule_self");
            }
            else if (CurrentRole == "重装" && hp <= 70f)
            {
                TryUseSkill(player, 7, "heavy_tenacity_lowhp");
            }
        }

        private static bool TryGetStrictAimPoint(Character player, Character target, Camera camera, out Vector3 aimPoint)
        {
            return TryGetAimPoint(player, target, camera, false, out aimPoint);
        }

        private static bool TryGetEmergencyAimPoint(Character player, Character target, Camera camera,
            out Vector3 aimPoint)
        {
            return TryGetAimPoint(player, target, camera, true, out aimPoint);
        }

        private static bool TryGetAimPoint(Character player, Character target, Camera camera, bool allowHidden,
            out Vector3 aimPoint)
        {
            aimPoint = Vector3.zero;
            if (player == null || target == null || target.transform == null || camera == null) return false;
            try { if (!allowHidden && target.GetHidden()) return false; } catch { return false; }

            Vector3 origin = camera.transform.position;
            Vector3[] points =
            {
                target.transform.position + Vector3.up * 0.82f,
                target.transform.position + Vector3.up * 1.05f,
                target.transform.position + Vector3.up * 0.62f
            };
            for (int i = 0; i < points.Length; i++)
            {
                if (!HasClearTargetSegment(origin, points[i], player, target)) continue;
                Vector3 muzzle = player.transform.position + Vector3.up * 1.15f;
                if (!HasClearTargetSegment(muzzle, points[i], player, target)) continue;
                aimPoint = points[i];
                return true;
            }
            return false;
        }

        private static bool HasClearTargetSegment(Vector3 origin, Vector3 point, Character player, Character target)
        {
            Vector3 direction = point - origin;
            float distance = direction.magnitude;
            if (distance < 0.05f) return true;
            direction /= distance;
            RaycastHit[] hits = Physics.RaycastAll(origin, direction, distance + 0.2f, 35072);
            Array.Sort(hits, CompareHitDistance);
            Transform playerRoot = player.transform.root;
            Transform targetRoot = target.transform.root;
            for (int i = 0; i < hits.Length; i++)
            {
                Collider collider = hits[i].collider;
                if (collider == null || collider.isTrigger) continue;
                Transform root = collider.transform == null ? null : collider.transform.root;
                if (root == playerRoot) continue;
                if (root == targetRoot) return true;
                return hits[i].distance >= distance - 0.15f;
            }
            // The ray ends at a target body point, so no intervening collider means the lane is clear.
            return true;
        }

        private static int CompareHitDistance(RaycastHit a, RaycastHit b)
        {
            return a.distance.CompareTo(b.distance);
        }

        private static bool ApplyLook(Character player, Camera camera, Vector3 point, float speed, float tolerance)
        {
            if (player.camera == null) return false;
            Vector3 direction = point - camera.transform.position;
            if (direction.sqrMagnitude < 0.01f) return false;
            Vector3 desired = Quaternion.LookRotation(direction.normalized).eulerAngles;
            float desiredYaw = desired.y;
            float desiredPitch = Mathf.Clamp(Mathf.DeltaAngle(0f, desired.x), -59f, 63f);
            float currentYaw = player.camera.finalx;
            float currentPitch = Mathf.Clamp(-player.camera.finaly + CameraPitchOffset, -59f, 63f);
            float step = speed * Mathf.Clamp(Time.deltaTime, 0.001f, 0.05f);
            float nextYaw = Mathf.MoveTowardsAngle(currentYaw, desiredYaw, step);
            float nextPitch = Mathf.MoveTowards(currentPitch, desiredPitch, step * 0.72f);
            player.camera.finalx = nextYaw;
            player.camera.finaly = Mathf.Clamp(CameraPitchOffset - nextPitch, -75f, 48f);
            return Mathf.Abs(Mathf.DeltaAngle(nextYaw, desiredYaw)) <= tolerance &&
                   Mathf.Abs(nextPitch - desiredPitch) <= tolerance;
        }

        private static bool SnapLook(Character player, Camera camera, Vector3 point)
        {
            if (player == null || player.camera == null || camera == null) return false;
            Vector3 direction = point - camera.transform.position;
            if (direction.sqrMagnitude < 0.01f) return false;
            Vector3 desired = Quaternion.LookRotation(direction.normalized).eulerAngles;
            float desiredPitch = Mathf.Clamp(Mathf.DeltaAngle(0f, desired.x), -59f, 63f);
            player.camera.finalx = desired.y;
            player.camera.finaly = Mathf.Clamp(CameraPitchOffset - desiredPitch, -75f, 48f);
            return true;
        }

        private static bool PrepareBodyAim(Character player, Character target, Camera camera, Vector3 aimPoint,
            bool emergency)
        {
            if (!SnapLook(player, camera, aimPoint))
            {
                LastAction = emergency ? "emergency_body_lock_failed" : "body_lock_failed";
                return false;
            }

            bool aimRevision = _aimTarget != target || _aimWeapon != player.mWeapon ||
                (_lastAimPoint != Vector3.zero && Vector3.Distance(_lastAimPoint, aimPoint) >= 0.75f);
            _lastAimPoint = aimPoint;
            if (aimRevision)
            {
                _aimTarget = target;
                _aimWeapon = player.mWeapon;
                _aimPreparedFrame = Time.frameCount;
                _aimPreparedAt = Time.time;
                LastAction = emergency ? "emergency_body_lock_settle" : "body_lock_settle";
                return false;
            }

            // CameraObj publishes shootForward after the view state advances once.
            if (Time.frameCount <= _aimPreparedFrame)
            {
                LastAction = emergency ? "emergency_body_lock_settle" : "body_lock_settle";
                return false;
            }

            if (!emergency && Time.time - _aimPreparedAt < 0.016f)
            {
                LastAction = "body_lock_settle";
                return false;
            }
            return true;
        }

        private static bool ConfirmBodyAim(Character target, bool allowHidden, float fallbackSeconds, out bool exact)
        {
            exact = allowHidden;
            if (!allowHidden)
            {
                try { exact = AutoFire.IsCrosshairOnEnemyExact(target); }
                catch { exact = false; }
            }
            if (exact)
            {
                _bodyConfirmTarget = null;
                _bodyConfirmStartedAt = 0f;
                return true;
            }
            if (_bodyConfirmTarget != target)
            {
                _bodyConfirmTarget = target;
                _bodyConfirmStartedAt = Time.time;
                return false;
            }
            return Time.time - _bodyConfirmStartedAt >= fallbackSeconds;
        }

        private static bool ObserveWeaponShot(Character player)
        {
            WeaponBase weapon = player == null ? null : player.mWeapon;
            if (weapon == null)
            {
                _observedWeapon = null;
                _observedClip = -1;
                _fireRequestWeapon = null;
                _fireRequestStartedAt = 0f;
                _fireRequestFirstAt = 0f;
                return false;
            }

            int clip;
            try { clip = weapon.clip; }
            catch { return false; }
            if (_observedWeapon != weapon)
            {
                _observedWeapon = weapon;
                _observedClip = clip;
                _fireRequestWeapon = null;
                _fireRequestStartedAt = 0f;
                _fireRequestFirstAt = 0f;
                return false;
            }

            bool fired = _observedClip >= 0 && clip < _observedClip;
            if (fired)
            {
                _fireRequestWeapon = null;
                _fireRequestStartedAt = 0f;
                _fireRequestFirstAt = 0f;
                WeaponType firedType = GetWeaponType(weapon);
                if (firedType == WeaponType.kWeaponTypeRPG || firedType == WeaponType.kWeaponTypeBow)
                    _nextRoleSpecialAt = Time.time + 2.4f;
                if (_temporarilyBlockedWeapon == weapon)
                {
                    _temporarilyBlockedWeapon = null;
                    _blockedWeaponUntil = 0f;
                }
                bool ignored = Time.time < _ignoreActualShotUntil;
                FileLogger.Log("AUTO-BATTLE][FIRE", (ignored ? "late shot ignored weapon=" : "actual shot weapon=") +
                    firedType + " clip=" + _observedClip + "->" + clip);
                if (ignored) fired = false;
            }
            _observedClip = clip;
            return fired;
        }

        private static void TrackFireRequest(WeaponBase weapon)
        {
            if (weapon == null) return;
            if (_fireRequestWeapon != weapon)
            {
                _fireRequestWeapon = weapon;
                _fireRequestStartedAt = Time.time;
                _fireRequestFirstAt = Time.time;
                return;
            }

            if (IsNativeWeaponWaiting(weapon) && Time.time - _fireRequestFirstAt < 3.5f)
            {
                _fireRequestStartedAt = Time.time;
                return;
            }

            WeaponType type = GetWeaponType(weapon);
            float timeout = 0.85f;
            if (type == WeaponType.kWeaponTypeShotGun) timeout = 1.15f;
            else if (type == WeaponType.kWeaponTypeSniperGun || type == WeaponType.kWeaponTypeRPG ||
                     type == WeaponType.kWeaponTypeBow) timeout = 1.65f;
            if (Time.time - _fireRequestStartedAt < timeout) return;

            _temporarilyBlockedWeapon = weapon;
            _blockedWeaponUntil = Time.time + 0.75f;
            _fireRequestWeapon = null;
            _fireRequestStartedAt = 0f;
            _fireRequestFirstAt = 0f;
            _nextWeaponSwitchAt = 0f;
            AutoBattleInput.ClearFire();
            LastAction = "fire_timeout_switch";
            FileLogger.Log("AUTO-BATTLE][FIRE", "request timeout; rotate weapon=" + type +
                " state=" + DescribeWeaponReadiness(weapon));
        }

        private static bool IsTemporarilyBlocked(WeaponBase weapon)
        {
            if (_temporarilyBlockedWeapon == null) return false;
            if (Time.time >= _blockedWeaponUntil)
            {
                _temporarilyBlockedWeapon = null;
                _blockedWeaponUntil = 0f;
                return false;
            }
            return weapon == _temporarilyBlockedWeapon;
        }

        private static bool IsNativeWeaponWaiting(WeaponBase weapon)
        {
            try
            {
                if (weapon == null || weapon.info == null || weapon.reloading) return true;
                if ((float)weapon.change_in_time > 0f || (float)weapon.info.cooling > 0f) return true;
                if (!weapon.cool_down_ready || !weapon.info.cool_down_ready) return true;
                return !weapon.Ready();
            }
            catch { return false; }
        }

        private static bool IsOperationalGun(WeaponBase weapon)
        {
            try
            {
                if (weapon == null || weapon.info == null || weapon.reloading || weapon.clip <= 0) return false;
                if (!(weapon.info is GunInfo)) return false;
                return GetWeaponType(weapon) != WeaponType.kWeaponTypeKnife;
            }
            catch { return false; }
        }

        private static void HandleUnavailableWeapon(Character player)
        {
            if (_weaponUnavailableSince <= 0f) _weaponUnavailableSince = Time.time;
            WeaponBase weapon = player == null ? null : player.mWeapon;
            if (weapon != null && weapon.clip <= 0 && !weapon.reloading)
            {
                try { weapon.Reload(); } catch { }
            }
            if (Time.time - _weaponUnavailableSince >= 0.22f) _nextWeaponSwitchAt = 0f;
            LastAction = "weapon_unavailable_recover";
        }

        private static void EnsureEmergencyWeapon(Character player, float distance)
        {
            if (player == null || player.weaponlist == null || Time.time < _nextWeaponSwitchAt) return;

            WeaponBase sniper = FindOperationalWeapon(player, WeaponType.kWeaponTypeSniperGun);
            if (sniper != null)
            {
                if (sniper != player.mWeapon) SwitchWeapon(player, sniper, "emergency_sniper_switch");
                return;
            }

            WeaponBase best = null;
            float bestScore = float.MinValue;
            for (int i = 0; i < player.weaponlist.Count; i++)
            {
                WeaponBase weapon = player.weaponlist[i];
                if (!IsOperationalGun(weapon) || IsTemporarilyBlocked(weapon)) continue;
                WeaponType type = GetWeaponType(weapon);

                float score = Mathf.Min(30f, weapon.clip);
                if (type == WeaponType.kWeaponTypeShotGun) score += distance <= 9f ? 150f : 85f;
                else if (type == WeaponType.kWeaponTypeMachineGun || type == WeaponType.kWeaponTypeSubMachineGun ||
                         type == WeaponType.kWeaponTypeDualWeapon) score += 135f;
                else if (type == WeaponType.kWeaponTypePistol) score += 75f;
                else if (type == WeaponType.kWeaponTypeSniperGun) score += 320f;
                else if (type == WeaponType.kWeaponTypeRPG) score += CurrentRole == "重装" ? 175f : 95f;
                else if (type == WeaponType.kWeaponTypeBow) score += distance >= 10f ? 35f : -100f;

                if (CurrentRole == "重装" && type == WeaponType.kWeaponTypeMachineGun) score += 35f;
                else if (CurrentRole == "医疗/守护" &&
                         (type == WeaponType.kWeaponTypeDualWeapon || type == WeaponType.kWeaponTypePistol)) score += 25f;
                else if (CurrentRole == "突击/狙击")
                {
                    if (type == WeaponType.kWeaponTypeSniperGun) score += 80f;
                    if (type == WeaponType.kWeaponTypeShotGun || type == WeaponType.kWeaponTypeSubMachineGun) score += 25f;
                }

                if (score <= bestScore) continue;
                bestScore = score;
                best = weapon;
            }

            if (best != null && best != player.mWeapon)
            {
                SwitchWeapon(player, best, "emergency_weapon_switch");
            }
            else if (player.mWeapon != null && player.mWeapon.clip <= 0 && !player.mWeapon.reloading)
            {
                try { player.mWeapon.Reload(); } catch { }
            }
        }

        private static void EnsureRoleWeapon(Character player, float distance)
        {
            if (player == null || player.weaponlist == null) return;
            if (Time.time < _nextWeaponSwitchAt) return;

            WeaponType preferred = WeaponType.kWeaponTypeNone;
            if (CurrentRole == "重装" && distance >= 6.5f && Time.time >= _nextRoleSpecialAt)
                preferred = WeaponType.kWeaponTypeRPG;
            else if (CurrentRole == "医疗/守护" && distance >= 4f && Time.time >= _nextRoleSpecialAt)
                preferred = WeaponType.kWeaponTypeBow;
            else if (CurrentRole == "突击/狙击")
                preferred = WeaponType.kWeaponTypeSniperGun;

            if (preferred != WeaponType.kWeaponTypeNone)
            {
                WeaponBase preferredWeapon = FindOperationalWeapon(player, preferred);
                if (preferredWeapon != null)
                {
                    if (preferredWeapon != player.mWeapon) SwitchWeapon(player, preferredWeapon, "role_preferred_switch");
                    return;
                }
            }
            if (IsWeaponSuitable(player.mWeapon, WeaponType.kWeaponTypeNone, distance)) return;

            WeaponBase best = null;
            float bestScore = float.MinValue;
            for (int i = 0; i < player.weaponlist.Count; i++)
            {
                WeaponBase weapon = player.weaponlist[i];
                if (!IsOperationalGun(weapon) || IsTemporarilyBlocked(weapon)) continue;
                WeaponType type = GetWeaponType(weapon);
                float score = Mathf.Min(30f, weapon.clip);
                if (preferred != WeaponType.kWeaponTypeNone && type == preferred) score += 140f;
                if ((type == WeaponType.kWeaponTypeRPG || type == WeaponType.kWeaponTypeBow) &&
                    Time.time < _nextRoleSpecialAt) score -= 120f;
                if (type == WeaponType.kWeaponTypeSniperGun) score += distance >= 12f ? 60f : 10f;
                else if (type == WeaponType.kWeaponTypeMachineGun || type == WeaponType.kWeaponTypeSubMachineGun ||
                         type == WeaponType.kWeaponTypeDualWeapon) score += 45f;
                else if (type == WeaponType.kWeaponTypePistol) score += 30f;
                else if (type == WeaponType.kWeaponTypeShotGun) score += distance < 9f ? 40f : 5f;
                else if (type == WeaponType.kWeaponTypeRPG || type == WeaponType.kWeaponTypeBow) score -= 30f;
                if (score > bestScore)
                {
                    bestScore = score;
                    best = weapon;
                }
            }

            if (best != null && best != player.mWeapon)
            {
                SwitchWeapon(player, best, "role_weapon_switch");
            }
            else if (player.mWeapon != null && player.mWeapon.clip <= 0 && !player.mWeapon.reloading)
            {
                try { player.mWeapon.Reload(); } catch { }
            }
        }

        private static WeaponBase FindOperationalWeapon(Character player, WeaponType type)
        {
            if (player == null || player.weaponlist == null) return null;
            WeaponBase best = null;
            int bestClip = -1;
            for (int i = 0; i < player.weaponlist.Count; i++)
            {
                WeaponBase weapon = player.weaponlist[i];
                if (!IsOperationalGun(weapon) || IsTemporarilyBlocked(weapon) || GetWeaponType(weapon) != type)
                    continue;
                if (weapon.clip <= bestClip) continue;
                best = weapon;
                bestClip = weapon.clip;
            }
            return best;
        }

        private static void SwitchWeapon(Character player, WeaponBase weapon, string action)
        {
            if (player == null || weapon == null || weapon.info == null) return;
            try
            {
                player.ChangeWeapon(Convert.ToInt32(weapon.info.slot));
                _nextWeaponSwitchAt = Time.time + 0.15f;
                LastAction = action;
            }
            catch { }
        }

        private static bool EnsureSniperScope(WeaponBase weapon)
        {
            return SetSniperScope(weapon, true);
        }

        private static bool SetSniperScope(WeaponBase weapon, bool open)
        {
            SniperGunController sniper = weapon as SniperGunController;
            if (sniper == null)
            {
                if (_scopeWeapon != null) AutoBattleInput.ClearSecondFire();
                _scopeWeapon = null;
                _scopeRequestPending = false;
                return true;
            }

            try
            {
                if (_scopeWeapon != weapon)
                {
                    AutoBattleInput.ClearSecondFire();
                    _scopeWeapon = weapon;
                    _scopeRequestPending = false;
                    _nextScopeAt = 0f;
                }
                if (_scopeRequestPending && _scopeRequestedOpen != open)
                {
                    AutoBattleInput.ClearSecondFire();
                    _scopeRequestPending = false;
                    _nextScopeAt = 0f;
                }

                bool observedOpen = sniper.currentSight != 0;
                if (observedOpen == open)
                {
                    if (_scopeRequestPending) AutoBattleInput.ClearSecondFire();
                    _scopeRequestPending = false;
                    _nextScopeAt = 0f;
                    LastAction = open ? "sniper_scope_ready" : "sniper_scope_closed";
                    return true;
                }

                if (open && sniper.reloading)
                {
                    LastAction = "sniper_scope_reload_wait";
                    return false;
                }

                if (_scopeRequestPending && Time.time - _scopeRequestedAt < 0.45f)
                {
                    LastAction = open ? "sniper_scope_open_wait" : "sniper_scope_close_wait";
                    return false;
                }
                if (_scopeRequestPending)
                {
                    AutoBattleInput.ClearSecondFire();
                    _scopeRequestPending = false;
                }

                if (Time.time >= _nextScopeAt)
                {
                    AutoBattleInput.PressAction(ActionType.kActionSecondFire, 0.10f);
                    _scopeRequestPending = true;
                    _scopeRequestedOpen = open;
                    _scopeRequestedAt = Time.time;
                    _nextScopeAt = Time.time + 0.38f;
                    LastAction = open ? "sniper_scope_open_request" : "sniper_scope_close_request";
                    FileLogger.Log("AUTO-BATTLE][ROLE", "sniper scope " + (open ? "open" : "close") + " requested");
                }
                else
                {
                    LastAction = open ? "sniper_scope_open_wait" : "sniper_scope_close_wait";
                }
                return false;
            }
            catch (Exception ex)
            {
                LastAction = open ? "sniper_scope_open_error" : "sniper_scope_close_error";
                FileLogger.Log("AUTO-BATTLE][ROLE", "sniper scope state failed desired=" +
                    (open ? "open" : "closed") + " ex=" + ex.GetType().Name + ":" + ex.Message);
                return false;
            }
        }

        private static bool IsWeaponSuitable(WeaponBase weapon, WeaponType preferred, float distance)
        {
            if (!IsOperationalGun(weapon) || IsTemporarilyBlocked(weapon)) return false;
            WeaponType type = GetWeaponType(weapon);
            if (preferred != WeaponType.kWeaponTypeNone) return type == preferred;
            if (type == WeaponType.kWeaponTypeShotGun && distance > 12f) return false;
            if (type == WeaponType.kWeaponTypeSniperGun && distance < 5f) return false;
            if ((type == WeaponType.kWeaponTypeRPG || type == WeaponType.kWeaponTypeBow) &&
                Time.time < _nextRoleSpecialAt) return false;
            return true;
        }

        private static WeaponType GetWeaponType(WeaponBase weapon)
        {
            try { return weapon == null ? WeaponType.kWeaponTypeNone : weapon.GetWeaponType(); }
            catch
            {
                try { return weapon == null || weapon.info == null ? WeaponType.kWeaponTypeNone : (WeaponType)weapon.info.sub_type; }
                catch { return WeaponType.kWeaponTypeNone; }
            }
        }

        private static string DetectRole(Character player)
        {
            try
            {
                if (player != null && player.character_info != null)
                {
                    if (player.character_info.career == CareerType.kCareerGunner) return "重装";
                    if (player.character_info.career == CareerType.kCareerCommando) return "突击/狙击";
                    if (player.character_info.career == CareerType.kCareerSolider) return "医疗/守护";
                }
            }
            catch { }
            if (HasWeapon(player, WeaponType.kWeaponTypeRPG)) return "重装";
            if (HasWeapon(player, WeaponType.kWeaponTypeBow) || HasSkill(player, 0) || HasSkill(player, 9) || HasSkill(player, 14))
                return "医疗/守护";
            if (HasWeapon(player, WeaponType.kWeaponTypeSniperGun)) return "突击/狙击";
            return "通用";
        }

        private static bool HasWeapon(Character player, WeaponType type)
        {
            try
            {
                if (player == null || player.weaponlist == null) return false;
                for (int i = 0; i < player.weaponlist.Count; i++)
                    if (GetWeaponType(player.weaponlist[i]) == type) return true;
            }
            catch { }
            return false;
        }

        private static bool HasSkill(Character player, int subType)
        {
            try
            {
                if (player == null || player.character_info == null || player.character_info.slots_info == null) return false;
                ObjectBaseInfo[] slots = player.character_info.slots_info.object_info;
                if (slots == null) return false;
                for (int i = 0; i < slots.Length; i++)
                {
                    SkillInfo skill = slots[i] as SkillInfo;
                    if (skill != null && skill.sub_type == (byte)subType) return true;
                }
            }
            catch { }
            return false;
        }

        private static float HealthPercent(Character player)
        {
            try
            {
                int max = player == null ? 0 : player.max_health;
                if (player != null && player.character_info != null && player.character_info.max_health > max)
                    max = player.character_info.max_health;
                return max <= 0 ? 100f : Mathf.Clamp((float)player.hp * 100f / max, 0f, 100f);
            }
            catch { return 100f; }
        }

        private static float XzDistance(Vector3 a, Vector3 b)
        {
            float x = a.x - b.x;
            float z = a.z - b.z;
            return Mathf.Sqrt(x * x + z * z);
        }
    }
}
