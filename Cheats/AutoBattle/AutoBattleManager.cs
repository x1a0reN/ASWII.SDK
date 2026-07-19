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

        public static string LastPath = "-";
        public static string LastPathProvider = "-";
        public static string LastAction = "-";
        public static string CurrentRole = "通用";

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
            LastPath = reason;
            LastPathProvider = "-";
            LastAction = reason;
        }

        public static void MarkSurvivalActivity(Character player)
        {
            AutoBattleInput.MarkActivity(0.35f);
            CurrentRole = SurvivalBotSettings.RoleStrategyEnabled ? DetectRole(player) : "通用";
            TryUseRoleMaintenance(player);
            try { if (player != null) player.ResetIdleMenu(); } catch { }
        }

        public static Vector3 NavigateSurvival(Character player, Vector3 destination, bool tacticalMove)
        {
            if (player == null || player.transform == null) return Vector3.zero;
            Vector3 playerPosition = player.transform.position;
            bool destinationChanged = !_hasDestination || XzDistance(_destination, destination) > (tacticalMove ? 2.5f : 4f);
            if (destinationChanged)
            {
                // Never continue walking an old route while the 2.5D planner evaluates a new target.
                Path.Clear();
                JumpFlags.Clear();
                _pathIndex = 0;
                _nextRepath = 0f;
            }
            _destination = destination;
            _hasDestination = true;

            if ((Path.Count == 0 || _pathIndex >= Path.Count || destinationChanged) && Time.time >= _nextRepath)
            {
                _nextRepath = Time.time + (tacticalMove ? 0.45f : 0.75f);
                BuildPath(player, playerPosition, destination);
            }

            if (Path.Count == 0 || _pathIndex >= Path.Count) return Vector3.zero;
            Vector3 next = Path[_pathIndex];
            while (_pathIndex < Path.Count - 1 && XzDistance(playerPosition, next) <= CornerReachDistance)
            {
                _pathIndex++;
                next = Path[_pathIndex];
            }

            float distance = XzDistance(playerPosition, next);
            if (_pathIndex == Path.Count - 1 && distance <= CornerReachDistance)
            {
                LastPath = "arrived";
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
                    Path.Clear();
                    JumpFlags.Clear();
                    _pathIndex = 0;
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
                Path.Clear();
                JumpFlags.Clear();
                _pathIndex = 0;
                _nextRepath = 0f;
                LastPath = "wall_repath";
                return Vector3.zero;
            }

            LastPath = "path " + (_pathIndex + 1) + "/" + Path.Count + (jump ? " jump" : string.Empty);
            return direction;
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

        public static bool AttackSurvival(Character player, Character target, Camera camera, out bool strictLine, out float distance)
        {
            strictLine = false;
            distance = 99999f;
            if (player == null || target == null || camera == null || target.IsDied || target.Is_Viewer) return false;
            try { if (target.GetHidden()) return false; } catch { return false; }

            distance = Vector3.Distance(player.transform.position, target.transform.position);
            CurrentRole = SurvivalBotSettings.RoleStrategyEnabled ? DetectRole(player) : "通用";
            EnsureRoleWeapon(player, distance);
            if (Time.time < _nextWeaponSwitchAt)
            {
                LastAction = "role_weapon_switch_wait";
                return false;
            }
            if (!EnsureSniperScope(player.mWeapon)) return false;

            Vector3 aimPoint;
            strictLine = TryGetStrictAimPoint(player, target, camera, out aimPoint);
            if (!strictLine)
            {
                LastAction = "strict_los_blocked";
                return false;
            }

            if (CurrentRole == "突击/狙击" && distance <= 2.8f &&
                TryAssaultMelee(player, target, camera, aimPoint)) return true;
            if (TryUseRoleAttackSkill(player, target, camera, aimPoint, strictLine, distance)) return true;

            bool aimReady = AimAt(player, camera, aimPoint);
            bool exact = false;
            try { exact = AutoFire.IsCrosshairOnEnemyExact(target); } catch { }
            if (!aimReady || !CanFire(player, distance))
            {
                LastAction = "aim_or_weapon_not_ready";
                return false;
            }

            if (Time.time < _nextFireAt) return false;
            AutoBattleInput.RequestFire(0.10f);
            _nextFireAt = Time.time + FireInterval(player.mWeapon);
            if (GetWeaponType(player.mWeapon) == WeaponType.kWeaponTypeRPG ||
                GetWeaponType(player.mWeapon) == WeaponType.kWeaponTypeBow)
                _nextRoleSpecialAt = Time.time + 2.4f;
            LastAction = exact ? "fire_exact" : "fire_strict_line";
            return true;
        }

        public static bool AttackEmergency(Character player, Character target, Camera camera, out bool strictLine,
            out float distance)
        {
            strictLine = false;
            distance = 99999f;
            if (player == null || target == null || camera == null || target.IsDied || target.Is_Viewer) return false;
            try { if (target.GetHidden()) return false; } catch { return false; }

            distance = Vector3.Distance(player.transform.position, target.transform.position);
            CurrentRole = SurvivalBotSettings.RoleStrategyEnabled ? DetectRole(player) : "通用";

            Vector3 aimPoint;
            strictLine = TryGetStrictAimPoint(player, target, camera, out aimPoint);
            if (!strictLine)
            {
                LastAction = "emergency_strict_los_blocked";
                return false;
            }

            EnsureEmergencyWeapon(player, distance);
            if (Time.time < _nextWeaponSwitchAt)
            {
                LastAction = "emergency_weapon_switch_wait";
                return false;
            }
            if (!EnsureSniperScope(player.mWeapon)) return false;

            WeaponType activeType = GetWeaponType(player.mWeapon);
            if (activeType == WeaponType.kWeaponTypeRPG && distance <= 12f)
            {
                LastAction = "emergency_rpg_too_close";
                return false;
            }

            bool aimReady = ApplyLook(player, camera, aimPoint, 1440f, 1.2f);
            bool exact = false;
            try { exact = AutoFire.IsCrosshairOnEnemyExact(target); } catch { }
            if (!aimReady || !CanFire(player, distance))
            {
                LastAction = aimReady ? "emergency_weapon_not_ready" : "emergency_strong_lock";
                return false;
            }

            if (Time.time < _nextFireAt) return false;
            AutoBattleInput.RequestFire(0.14f);
            _nextFireAt = Time.time + Mathf.Min(0.08f, FireInterval(player.mWeapon));
            LastAction = exact ? "emergency_fire_exact" : "emergency_fire_strict_line";
            return true;
        }

        public static void LogCombatState(Character player, Character target, bool strictLine, float distance, bool fired)
        {
            if (Time.time < _nextCombatTraceAt) return;
            _nextCombatTraceAt = Time.time + 1f;

            string weapon = "-";
            string scope = "-";
            try
            {
                weapon = GetWeaponType(player == null ? null : player.mWeapon).ToString();
                SniperGunController sniper = player == null ? null : player.mWeapon as SniperGunController;
                if (sniper != null) scope = sniper.currentSight.ToString();
            }
            catch { }

            string targetId = target == null ? "-" : target.uid.ToString();
            FileLogger.Log("AUTO-BATTLE][COMBAT", "role=" + CurrentRole + " target=" + targetId +
                " dist=" + distance.ToString("0.0") + " los=" + strictLine + " fired=" + fired +
                " weapon=" + weapon + " scope=" + scope + " action=" + LastAction + " path=" + LastPath);
        }

        public static void LookSurvival(Character player, Camera camera, Vector3 point)
        {
            if (player == null || camera == null || player.camera == null) return;
            ApplyLook(player, camera, point, 240f, 3.5f);
        }

        private static void BuildPath(Character player, Vector3 from, Vector3 to)
        {
            AutoBattleRouteCapabilities capabilities = CreateCapabilities(player);
            AutoBattleRouteResult route = AutoBattleRoutePlanner.BuildRoute(from, to, player.transform.root, capabilities);
            if (route == null)
            {
                LastPath = "route_null";
                return;
            }

            LastPathProvider = route.Provider ?? "-";
            if (!route.Success)
            {
                LastPath = route.Provider == "phys_grid_2_5d_pending" ? "path_pending" : "no_path";
                _nextRepath = route.Provider == "phys_grid_2_5d_pending" ? Time.time : Time.time + 0.6f;
                return;
            }

            Path.Clear();
            JumpFlags.Clear();
            _pathIndex = 0;
            for (int i = 0; i < route.Corners.Count; i++)
            {
                if (XzDistance(from, route.Corners[i]) < 0.35f) continue;
                Path.Add(route.Corners[i]);
                JumpFlags.Add(i < route.JumpFlags.Count && route.JumpFlags[i]);
            }
            LastPath = Path.Count == 0 ? "path_complete" : route.Provider + " " + Path.Count + " pts";
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

        private static bool TryUseRoleAttackSkill(Character player, Character target, Camera camera, Vector3 aimPoint,
            bool strictLine, float distance)
        {
            if (player == null || target == null) return false;
            if (CurrentRole == "医疗/守护")
            {
                float hp = HealthPercent(player);
                if (hp <= 58f && TryUseSkill(player, 0, "medic_heal_self")) return true;
                if (hp <= 72f && TryUseSkill(player, 14, "medic_capsule_self")) return true;
                if (strictLine)
                {
                    if (!AimAt(player, camera, aimPoint))
                    {
                        LastAction = "medic_arrow_rain_aim";
                        return true;
                    }
                    if (TryUseSkill(player, 9, "medic_arrow_rain")) return true;
                }
            }
            else if (CurrentRole == "重装")
            {
                if (distance <= 28f && TryUseSkill(player, 1, "heavy_shield_contact")) return false;
                if (distance <= 35f && AimAt(player, camera, aimPoint) &&
                    TryUseSkill(player, 4, "heavy_gallop_contact")) return false;
                if (HealthPercent(player) <= 70f && TryUseSkill(player, 7, "heavy_tenacity_lowhp")) return false;
            }
            else if (CurrentRole == "突击/狙击" && distance <= 4.2f)
            {
                if (TryUseSkill(player, 11, "assault_spurt_melee")) return true;
            }
            return false;
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

        private static bool TryAssaultMelee(Character player, Character target, Camera camera, Vector3 aimPoint)
        {
            WeaponBase knife = FindWeapon(player, WeaponType.kWeaponTypeKnife);
            if (knife == null) return false;
            if (player.mWeapon != knife)
            {
                if (Time.time < _nextWeaponSwitchAt) return true;
                try
                {
                    player.ChangeWeapon(Convert.ToInt32(knife.info.slot));
                    _nextWeaponSwitchAt = Time.time + 0.55f;
                    LastAction = "assault_knife_switch";
                }
                catch { }
                return true;
            }

            bool aimReady = AimAt(player, camera, aimPoint);
            bool exact = false;
            try { exact = AutoFire.IsCrosshairOnEnemyExact(target); } catch { }
            if (!aimReady || !exact || Time.time < _nextFireAt) return true;
            try
            {
                if (!knife.cool_down_ready || !knife.info.cool_down_ready || (float)knife.info.cooling > 0f) return true;
            }
            catch { return true; }
            AutoBattleInput.RequestFire(0.11f);
            _nextFireAt = Time.time + 0.22f;
            LastAction = "assault_knife_fire";
            return true;
        }

        private static bool TryGetStrictAimPoint(Character player, Character target, Camera camera, out Vector3 aimPoint)
        {
            aimPoint = Vector3.zero;
            if (player == null || target == null || target.transform == null || camera == null) return false;
            try { if (target.GetHidden()) return false; } catch { return false; }

            Vector3 origin = camera.transform.position;
            Vector3[] points =
            {
                target.transform.position + Vector3.up * 1.45f,
                target.transform.position + Vector3.up * 1.05f,
                target.transform.position + Vector3.up * 0.65f
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

        private static bool AimAt(Character player, Camera camera, Vector3 point)
        {
            return ApplyLook(player, camera, point, 520f, 1.7f);
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

        private static void EnsureEmergencyWeapon(Character player, float distance)
        {
            if (player == null || player.weaponlist == null || Time.time < _nextWeaponSwitchAt) return;
            if (IsEmergencyWeaponSuitable(player.mWeapon, distance)) return;

            WeaponBase best = null;
            float bestScore = float.MinValue;
            for (int i = 0; i < player.weaponlist.Count; i++)
            {
                WeaponBase weapon = player.weaponlist[i];
                if (!IsReadyGun(weapon)) continue;
                WeaponType type = GetWeaponType(weapon);
                if (type == WeaponType.kWeaponTypeRPG) continue;

                float score = weapon.clip;
                if (type == WeaponType.kWeaponTypeShotGun) score += distance <= 9f ? 150f : 85f;
                else if (type == WeaponType.kWeaponTypeMachineGun || type == WeaponType.kWeaponTypeSubMachineGun ||
                         type == WeaponType.kWeaponTypeDualWeapon) score += 135f;
                else if (type == WeaponType.kWeaponTypePistol) score += 75f;
                else if (type == WeaponType.kWeaponTypeSniperGun) score += distance >= 8f ? 80f : -80f;
                else if (type == WeaponType.kWeaponTypeBow) score += distance >= 10f ? 35f : -100f;

                if (CurrentRole == "重装" && type == WeaponType.kWeaponTypeMachineGun) score += 35f;
                else if (CurrentRole == "医疗/守护" &&
                         (type == WeaponType.kWeaponTypeDualWeapon || type == WeaponType.kWeaponTypePistol)) score += 25f;
                else if (CurrentRole == "突击/狙击")
                {
                    if (type == WeaponType.kWeaponTypeSniperGun && distance >= 8f) score += 30f;
                    if (type == WeaponType.kWeaponTypeShotGun || type == WeaponType.kWeaponTypeSubMachineGun) score += 25f;
                }

                if (score <= bestScore) continue;
                bestScore = score;
                best = weapon;
            }

            if (best != null && best != player.mWeapon)
            {
                try
                {
                    player.ChangeWeapon(Convert.ToInt32(best.info.slot));
                    _nextWeaponSwitchAt = Time.time + 0.32f;
                    LastAction = "emergency_weapon_switch";
                }
                catch { }
            }
            else if (player.mWeapon != null && player.mWeapon.clip <= 0 && !player.mWeapon.reloading)
            {
                try { player.mWeapon.Reload(); } catch { }
            }
        }

        private static bool IsEmergencyWeaponSuitable(WeaponBase weapon, float distance)
        {
            if (!IsReadyGun(weapon)) return false;
            WeaponType type = GetWeaponType(weapon);
            if (type == WeaponType.kWeaponTypeRPG || type == WeaponType.kWeaponTypeBow) return false;
            if (type == WeaponType.kWeaponTypeSniperGun && distance < 8f) return false;
            return true;
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
            else if (CurrentRole == "突击/狙击" && distance > 4.2f)
                preferred = WeaponType.kWeaponTypeSniperGun;

            if (IsWeaponSuitable(player.mWeapon, preferred, distance)) return;

            WeaponBase best = null;
            float bestScore = float.MinValue;
            for (int i = 0; i < player.weaponlist.Count; i++)
            {
                WeaponBase weapon = player.weaponlist[i];
                if (!IsReadyGun(weapon)) continue;
                WeaponType type = GetWeaponType(weapon);
                float score = weapon.clip;
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
                try
                {
                    player.ChangeWeapon(Convert.ToInt32(best.info.slot));
                    _nextWeaponSwitchAt = Time.time + 0.55f;
                    LastAction = "role_weapon_switch";
                }
                catch { }
            }
            else if (player.mWeapon != null && player.mWeapon.clip <= 0 && !player.mWeapon.reloading)
            {
                try { player.mWeapon.Reload(); } catch { }
            }
        }

        private static bool IsReadyGun(WeaponBase weapon)
        {
            try
            {
                if (weapon == null || weapon.info == null || weapon.reloading || weapon.clip <= 0) return false;
                if (!(weapon.info is GunInfo)) return false;
                if (!weapon.Ready()) return false;
                if (!weapon.cool_down_ready || !weapon.info.cool_down_ready || (float)weapon.info.cooling > 0f) return false;
                WeaponType type = GetWeaponType(weapon);
                return type != WeaponType.kWeaponTypeKnife;
            }
            catch { return false; }
        }

        private static bool CanFire(Character player, float distance)
        {
            if (!IsReadyGun(player == null ? null : player.mWeapon)) return false;
            WeaponType type = GetWeaponType(player.mWeapon);
            if (type == WeaponType.kWeaponTypeShotGun && distance > 28f) return false;
            if ((type == WeaponType.kWeaponTypeRPG || type == WeaponType.kWeaponTypeBow) && distance > 85f) return false;
            return distance <= (type == WeaponType.kWeaponTypeSniperGun ? 180f : 120f);
        }

        private static float FireInterval(WeaponBase weapon)
        {
            if (weapon == null || weapon.info == null) return 0.12f;
            WeaponType type = GetWeaponType(weapon);
            if (type == WeaponType.kWeaponTypeMachineGun || type == WeaponType.kWeaponTypeSubMachineGun ||
                type == WeaponType.kWeaponTypeDualWeapon) return UnityEngine.Random.Range(0.045f, 0.085f);
            if (type == WeaponType.kWeaponTypePistol) return UnityEngine.Random.Range(0.09f, 0.15f);
            if (type == WeaponType.kWeaponTypeShotGun) return UnityEngine.Random.Range(0.16f, 0.26f);
            return UnityEngine.Random.Range(0.10f, 0.18f);
        }

        private static bool EnsureSniperScope(WeaponBase weapon)
        {
            SniperGunController sniper = weapon as SniperGunController;
            if (sniper == null) return true;

            try
            {
                if (sniper.currentSight != 0)
                {
                    _nextScopeAt = 0f;
                    LastAction = "sniper_scope_ready";
                    return true;
                }

                if (sniper.reloading)
                {
                    LastAction = "sniper_scope_reload_wait";
                    return false;
                }

                if (Time.time >= _nextScopeAt)
                {
                    // SniperGunController toggles its sight on GetKeyDown, not while the key is held.
                    AutoBattleInput.PressAction(ActionType.kActionSecondFire, 0.10f);
                    _nextScopeAt = Time.time + 0.40f;
                    LastAction = "sniper_scope_request";
                    FileLogger.Log("AUTO-BATTLE][ROLE", "sniper scope requested");
                }
                else
                {
                    LastAction = "sniper_scope_wait";
                }
                return false;
            }
            catch (Exception ex)
            {
                LastAction = "sniper_scope_error";
                FileLogger.Log("AUTO-BATTLE][ROLE", "sniper scope failed ex=" + ex.GetType().Name + ":" + ex.Message);
                return false;
            }
        }

        private static bool IsWeaponSuitable(WeaponBase weapon, WeaponType preferred, float distance)
        {
            if (weapon != null && preferred == WeaponType.kWeaponTypeSniperGun &&
                GetWeaponType(weapon) == WeaponType.kWeaponTypeSniperGun && !weapon.reloading && weapon.clip > 0)
                return true;
            if (!IsReadyGun(weapon)) return false;
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

        private static WeaponBase FindWeapon(Character player, WeaponType type)
        {
            try
            {
                if (player == null || player.weaponlist == null) return null;
                for (int i = 0; i < player.weaponlist.Count; i++)
                    if (GetWeaponType(player.weaponlist[i]) == type) return player.weaponlist[i];
            }
            catch { }
            return null;
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
