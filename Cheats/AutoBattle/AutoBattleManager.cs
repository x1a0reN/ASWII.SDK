using System;
using System.Collections.Generic;
using ASWDEBUG.Cheats.Player;
using ASWDEBUG.Logger;
using UnityEngine;

namespace ASWDEBUG.Cheats.AutoBattle
{
    public static class AutoBattleManager
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

        public static string LastPath = "-";
        public static string LastPathProvider = "-";
        public static string LastAction = "-";

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
            LastPath = reason;
            LastPathProvider = "-";
            LastAction = reason;
        }

        public static void MarkSurvivalActivity(Character player)
        {
            AutoBattleInput.MarkActivity(0.35f);
            try { if (player != null) player.ResetIdleMenu(); } catch { }
        }

        public static Vector3 NavigateSurvival(Character player, Vector3 destination, bool tacticalMove)
        {
            if (player == null || player.transform == null) return Vector3.zero;
            Vector3 playerPosition = player.transform.position;
            bool destinationChanged = !_hasDestination || XzDistance(_destination, destination) > (tacticalMove ? 2.5f : 4f);
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
            if (jump && distance <= 1.7f && Time.time >= _nextJumpAt)
            {
                AutoBattleInput.PressAction(ActionType.kActionJump, 0.11f);
                AutoBattleInput.HoldAction(ActionType.kActionJump, 0.26f);
                _nextJumpAt = Time.time + 0.5f;
            }

            if (AutoBattleRoutePlanner.HasForwardBlock(playerPosition, direction, player.transform.root))
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

        public static bool TryUseSurvivalDefense(Character player)
        {
            if (player == null || Time.time < _nextSkillAt) return false;
            try
            {
                if (!player.GetHidden() && TryUseSkill(player, 2, "hidden")) return true;
            }
            catch { }
            if (TryUseSkill(player, 1, "shield")) return true;
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
            Vector3 aimPoint;
            strictLine = TryGetStrictAimPoint(player, target, camera, out aimPoint);
            if (!strictLine)
            {
                LastAction = "strict_los_blocked";
                return false;
            }

            EnsureRangedWeapon(player, distance);
            bool aimReady = AimAt(player, camera, aimPoint);
            bool exact = false;
            try { exact = AutoFire.IsCrosshairOnEnemyExact(target); } catch { }
            if ((!aimReady && !exact) || !CanFire(player, distance))
            {
                LastAction = "aim_or_weapon_not_ready";
                return false;
            }

            if (Time.time < _nextFireAt) return false;
            AutoBattleInput.RequestFire(0.10f);
            _nextFireAt = Time.time + FireInterval(player.mWeapon);
            LastAction = "fire";
            return true;
        }

        public static void LookSurvival(Character player, Camera camera, Vector3 point)
        {
            if (player == null || camera == null || player.camera == null) return;
            ApplyLook(player, camera, point, 240f, 3.5f);
        }

        private static void BuildPath(Character player, Vector3 from, Vector3 to)
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
            RaycastHit[] hits = Physics.RaycastAll(origin, direction, distance + 0.2f);
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
                return false;
            }
            return false;
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

        private static void EnsureRangedWeapon(Character player, float distance)
        {
            if (player == null || player.weaponlist == null) return;
            if (IsReadyGun(player.mWeapon)) return;

            WeaponBase best = null;
            float bestScore = float.MinValue;
            for (int i = 0; i < player.weaponlist.Count; i++)
            {
                WeaponBase weapon = player.weaponlist[i];
                if (!IsReadyGun(weapon)) continue;
                WeaponType type = (WeaponType)weapon.info.sub_type;
                float score = weapon.clip;
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
                try { player.ChangeWeapon(Convert.ToInt32(best.info.slot)); } catch { }
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
                if (!weapon.cool_down_ready || !weapon.info.cool_down_ready || (float)weapon.info.cooling > 0f) return false;
                WeaponType type = (WeaponType)weapon.info.sub_type;
                return type != WeaponType.kWeaponTypeKnife;
            }
            catch { return false; }
        }

        private static bool CanFire(Character player, float distance)
        {
            if (!IsReadyGun(player == null ? null : player.mWeapon)) return false;
            WeaponType type = (WeaponType)player.mWeapon.info.sub_type;
            if (type == WeaponType.kWeaponTypeShotGun && distance > 28f) return false;
            if ((type == WeaponType.kWeaponTypeRPG || type == WeaponType.kWeaponTypeBow) && distance > 85f) return false;
            return distance <= (type == WeaponType.kWeaponTypeSniperGun ? 180f : 120f);
        }

        private static float FireInterval(WeaponBase weapon)
        {
            if (weapon == null || weapon.info == null) return 0.12f;
            WeaponType type = (WeaponType)weapon.info.sub_type;
            if (type == WeaponType.kWeaponTypeMachineGun || type == WeaponType.kWeaponTypeSubMachineGun ||
                type == WeaponType.kWeaponTypeDualWeapon) return UnityEngine.Random.Range(0.045f, 0.085f);
            if (type == WeaponType.kWeaponTypePistol) return UnityEngine.Random.Range(0.09f, 0.15f);
            if (type == WeaponType.kWeaponTypeShotGun) return UnityEngine.Random.Range(0.16f, 0.26f);
            return UnityEngine.Random.Range(0.10f, 0.18f);
        }

        private static float XzDistance(Vector3 a, Vector3 b)
        {
            float x = a.x - b.x;
            float z = a.z - b.z;
            return Mathf.Sqrt(x * x + z * z);
        }
    }
}
