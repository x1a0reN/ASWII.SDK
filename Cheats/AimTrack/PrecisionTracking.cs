using ASWDEBUG.Main;
using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using UnityEngine;

namespace ASWDEBUG.Cheats.AimTrack
{
    internal static class PrecisionTracking
    {
        internal static readonly bool EnabledPipeline = true;

        private struct TargetCandidate
        {
            internal Character Target;
            internal float ScreenDistanceSqr;
            internal float WorldDistanceSqr;
        }

        private static readonly int[] PartPriority = new int[]
        {
            4, 3, 0, 5, 9, 6, 10, 7, 11, 8, 12,
            13, 16, 14, 17, 15, 18, 1, 2
        };

        private static readonly List<TargetCandidate> TargetCandidates =
            new List<TargetCandidate>(32);
        private static readonly RNGCryptoServiceProvider ProbabilityRng =
            new RNGCryptoServiceProvider();
        private static readonly byte[] ProbabilityBytes = new byte[4];
        private static readonly object ProbabilitySync = new object();

        internal static Character SelectBestTarget(
            float radius,
            bool requireLineOfSight,
            bool checkShield,
            bool checkHidden,
            out float selectedDistance)
        {
            selectedDistance = float.MaxValue;
            Character player = GetPlayer();
            Camera camera = GetCamera();
            if (player == null || camera == null) return null;

            Vector3 shotOrigin = GetShotOrigin(player);
            Vector3 hitPoint;
            byte hitPart;
            Character selected = SelectBestTargetFromOrigin(
                player,
                camera,
                shotOrigin,
                radius,
                requireLineOfSight,
                checkShield,
                checkHidden,
                out hitPoint,
                out hitPart,
                out selectedDistance);
            if (selected != null)
            {
                AimTrack.CurrentHitPoint = hitPoint;
                AimTrack.CurrentHitPart = hitPart;
            }
            return selected;
        }

        private static Character SelectBestTargetFromOrigin(
            Character player,
            Camera camera,
            Vector3 shotOrigin,
            float radius,
            bool requireLineOfSight,
            bool checkShield,
            bool checkHidden,
            out Vector3 selectedPoint,
            out byte selectedPart,
            out float selectedDistance)
        {
            selectedPoint = Vector3.zero;
            selectedPart = 4;
            selectedDistance = float.MaxValue;
            if (float.IsNaN(radius) || float.IsInfinity(radius))
                radius = 188f;
            float radiusSqr = radius <= 0f ? float.PositiveInfinity : radius * radius;
            float centerX = Screen.width * 0.5f;
            float centerY = Screen.height * 0.5f;
            int playerTeam = player.GetTeam();

            TargetCandidates.Clear();
            try { ASAvatar.UpdateAllHitCollider(); } catch { }

            IEnumerable<Character> characters = CharacterManager.Instance == null
                ? null
                : CharacterManager.Instance.character_set;
            if (characters == null) return null;

            foreach (Character target in characters)
            {
                if (!IsEligibleTarget(target, playerTeam, checkHidden)) continue;

                Vector3 anchor = GetTargetAnchor(target);
                Vector3 screen = camera.WorldToScreenPoint(anchor);
                if (screen.z <= 0f) continue;

                float dx = screen.x - centerX;
                float dy = screen.y - centerY;
                float screenDistanceSqr = dx * dx + dy * dy;

                TargetCandidates.Add(new TargetCandidate
                {
                    Target = target,
                    ScreenDistanceSqr = screenDistanceSqr,
                    WorldDistanceSqr = (anchor - shotOrigin).sqrMagnitude
                });
            }

            TargetCandidates.Sort(CompareScreenDistance);
            Character selected = null;
            float bestPointDistanceSqr = float.MaxValue;
            for (int i = 0; i < TargetCandidates.Count; i++)
            {
                TargetCandidate candidate = TargetCandidates[i];
                Vector3 hitPoint;
                byte hitPart;
                float visibleScreenDistanceSqr;
                if (!TryResolveTargetPoint(
                    candidate.Target,
                    shotOrigin,
                    camera,
                    radiusSqr,
                    requireLineOfSight,
                    checkShield,
                    out hitPoint,
                    out hitPart,
                    out visibleScreenDistanceSqr))
                {
                    continue;
                }

                if (visibleScreenDistanceSqr >= bestPointDistanceSqr) continue;
                selected = candidate.Target;
                selectedPoint = hitPoint;
                selectedPart = hitPart;
                bestPointDistanceSqr = visibleScreenDistanceSqr;
            }

            if (selected != null)
                selectedDistance = Mathf.Sqrt(bestPointDistanceSqr);
            return selected;
        }

        internal static Character SelectBestTargetByWorldDistance(
            float radius,
            bool requireLineOfSight,
            bool checkShield,
            bool checkHidden,
            out float selectedDistance)
        {
            selectedDistance = float.MaxValue;
            Character player = GetPlayer();
            Camera camera = GetCamera();
            if (player == null || camera == null) return null;

            float maxDistanceSqr = radius <= 0f
                ? float.PositiveInfinity
                : radius * radius;
            float bestDistanceSqr = float.MaxValue;
            Character selected = null;
            Vector3 shotOrigin = GetShotOrigin(player);
            int playerTeam = player.GetTeam();

            IEnumerable<Character> characters = CharacterManager.Instance == null
                ? null
                : CharacterManager.Instance.character_set;
            if (characters == null) return null;

            try { ASAvatar.UpdateAllHitCollider(); } catch { }
            foreach (Character target in characters)
            {
                if (!IsEligibleTarget(target, playerTeam, checkHidden)) continue;
                float distanceSqr = (GetTargetAnchor(target) - shotOrigin).sqrMagnitude;
                if (distanceSqr > maxDistanceSqr || distanceSqr >= bestDistanceSqr) continue;

                Vector3 hitPoint;
                byte hitPart;
                float ignored;
                if (!TryResolveTargetPoint(
                    target,
                    shotOrigin,
                    camera,
                    float.PositiveInfinity,
                    requireLineOfSight,
                    checkShield,
                    out hitPoint,
                    out hitPart,
                    out ignored))
                {
                    continue;
                }

                selected = target;
                bestDistanceSqr = distanceSqr;
                AimTrack.CurrentHitPoint = hitPoint;
                AimTrack.CurrentHitPart = hitPart;
            }

            if (selected != null) selectedDistance = Mathf.Sqrt(bestDistanceSqr);
            return selected;
        }

        internal static bool TryResolveTrackedMiss(
            Vector3 shotOrigin,
            out Character target,
            out Vector3 hitPoint,
            out byte hitPart,
            out float probabilityRoll)
        {
            target = null;
            hitPoint = Vector3.zero;
            hitPart = 4;
            probabilityRoll = -1f;

            Character player = GetPlayer();
            Camera camera = GetCamera();
            if (!AimTrack.Enabled || player == null || camera == null ||
                AimTrack.IsExcludedWeapon(player.mWeapon))
            {
                AimTrack.LastProbabilityRoll = -1f;
                AimTrack.LastProbabilityAccepted = false;
                AimTrack.LastDecision = "INACTIVE";
                return false;
            }

            float probability = AimTrack.TrackingProbability;
            if (float.IsNaN(probability) || float.IsInfinity(probability))
                probability = 0f;
            probability = Mathf.Clamp01(probability);
            if (probability <= 0f)
            {
                AimTrack.LastProbabilityRoll = -1f;
                AimTrack.LastProbabilityAccepted = false;
                AimTrack.LastDecision = "PROBABILITY_DISABLED";
                return false;
            }

            probabilityRoll = NextProbabilityRoll();
            AimTrack.LastProbabilityRoll = probabilityRoll;
            AimTrack.LastProbabilityAccepted = probability >= 1f ||
                                               probabilityRoll < probability;
            if (!AimTrack.LastProbabilityAccepted)
            {
                AimTrack.LastDecision = "ROLL_REJECTED";
                return false;
            }

            float selectedDistance;
            Character candidate = SelectBestTargetFromOrigin(
                player,
                camera,
                shotOrigin,
                AimTrack.RadiusPixels,
                AimTrack.Wall,
                AimTrack.Shield,
                AimTrack.Hidden,
                out hitPoint,
                out hitPart,
                out selectedDistance);
            if (candidate == null)
            {
                AimTrack.LastDecision = "NO_SHOOTABLE_POINT";
                return false;
            }

            target = candidate;
            AimTrack.currentTarget = candidate;
            AimTrack.bestTarget = candidate;
            AimTrack.CurrentHitPoint = hitPoint;
            AimTrack.CurrentHitPart = hitPart;
            AimTrack.closestDistance = selectedDistance;
            AimTrack.AimLocking = true;
            AimTrack.LastDecision = "TRACKED_MISS";
            return true;
        }

        private static Character GetPlayer()
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

        private static Camera GetCamera()
        {
            return CheatMain.CameraMain != null ? CheatMain.CameraMain : Camera.main;
        }

        private static Vector3 GetShotOrigin(Character player)
        {
            try
            {
                if (player != null && player.transform != null)
                    return player.transform.position + Vector3.up;
            }
            catch { }
            return player == null ? Vector3.zero : player.GetCameraPosition();
        }

        private static bool IsEligibleTarget(
            Character target,
            int playerTeam,
            bool checkHidden)
        {
            if (target == null) return false;
            try
            {
                if (target.IsDied || target.GetTeam() == playerTeam || target.IsAsTarget())
                    return false;
                if (checkHidden && target.GetHidden()) return false;
            }
            catch
            {
                // A partially destroyed character must not abort selection for all targets.
                return false;
            }
            return true;
        }

        private static int CompareScreenDistance(TargetCandidate left, TargetCandidate right)
        {
            int screen = left.ScreenDistanceSqr.CompareTo(right.ScreenDistanceSqr);
            return screen != 0
                ? screen
                : left.WorldDistanceSqr.CompareTo(right.WorldDistanceSqr);
        }

        private static Vector3 GetTargetAnchor(Character target)
        {
            if (target == null) return Vector3.zero;
            try
            {
                HitCollider chest = target.getHitCollider(3);
                if (chest != null && chest.self != null) return chest.self.position;
            }
            catch { }
            try
            {
                Transform chestBone = target.getBone("web__chest");
                if (chestBone != null) return chestBone.position;
            }
            catch { }
            return target.transform == null
                ? Vector3.zero
                : target.transform.position + Vector3.up * 0.85f;
        }

        private static bool TryResolveTargetPoint(
            Character target,
            Vector3 origin,
            Camera camera,
            float radiusSqr,
            bool requireLineOfSight,
            bool checkShield,
            out Vector3 hitPoint,
            out byte hitPart,
            out float bestScreenDistanceSqr)
        {
            hitPoint = Vector3.zero;
            hitPart = 4;
            bestScreenDistanceSqr = float.MaxValue;
            if (target == null || camera == null) return false;

            if (!requireLineOfSight && checkShield && IsShieldFacingShooter(target, origin))
                return false;

            bool found = false;
            for (int i = 0; i < PartPriority.Length; i++)
            {
                int part = PartPriority[i];
                HitCollider hitCollider = null;
                try { hitCollider = target.getHitCollider(part); } catch { }
                if (hitCollider == null || hitCollider.self == null) continue;

                Collider collider = hitCollider.self.GetComponent<Collider>();
                if (collider == null)
                {
                    EvaluatePoint(
                        target, origin, camera, radiusSqr, requireLineOfSight,
                        checkShield, hitCollider.self.position, part,
                        ref found, ref hitPoint, ref hitPart,
                        ref bestScreenDistanceSqr);
                    continue;
                }

                Bounds bounds = collider.bounds;
                Vector3 center = bounds.center;
                Vector3 closest = Vector3.Lerp(ClosestPoint(bounds, origin), center, 0.06f);
                EvaluatePoint(
                    target, origin, camera, radiusSqr, requireLineOfSight,
                    checkShield, closest, part,
                    ref found, ref hitPoint, ref hitPart,
                    ref bestScreenDistanceSqr);
                EvaluatePoint(
                    target, origin, camera, radiusSqr, requireLineOfSight,
                    checkShield, center, part,
                    ref found, ref hitPoint, ref hitPart,
                    ref bestScreenDistanceSqr);

                Vector3 extents = bounds.extents;
                float horizontal = Mathf.Max(0.02f, Mathf.Min(extents.x, extents.z));
                Vector3 cameraRight = camera.transform == null
                    ? Vector3.right
                    : camera.transform.right;
                EvaluatePoint(
                    target, origin, camera, radiusSqr, requireLineOfSight,
                    checkShield, center + cameraRight * horizontal * 0.82f, part,
                    ref found, ref hitPoint, ref hitPart,
                    ref bestScreenDistanceSqr);
                EvaluatePoint(
                    target, origin, camera, radiusSqr, requireLineOfSight,
                    checkShield, center - cameraRight * horizontal * 0.82f, part,
                    ref found, ref hitPoint, ref hitPart,
                    ref bestScreenDistanceSqr);

                float vertical = Mathf.Max(0.02f, extents.y);
                EvaluatePoint(
                    target, origin, camera, radiusSqr, requireLineOfSight,
                    checkShield, center + Vector3.up * vertical * 0.82f, part,
                    ref found, ref hitPoint, ref hitPart,
                    ref bestScreenDistanceSqr);
                EvaluatePoint(
                    target, origin, camera, radiusSqr, requireLineOfSight,
                    checkShield, center - Vector3.up * vertical * 0.82f, part,
                    ref found, ref hitPoint, ref hitPart,
                    ref bestScreenDistanceSqr);

                if (part == 4 || part == 3 || part == 0)
                {
                    EvaluatePoint(
                        target, origin, camera, radiusSqr, requireLineOfSight,
                        checkShield,
                        center + cameraRight * horizontal * 0.68f +
                        Vector3.up * vertical * 0.68f,
                        part,
                        ref found, ref hitPoint, ref hitPart,
                        ref bestScreenDistanceSqr);
                    EvaluatePoint(
                        target, origin, camera, radiusSqr, requireLineOfSight,
                        checkShield,
                        center - cameraRight * horizontal * 0.68f +
                        Vector3.up * vertical * 0.68f,
                        part,
                        ref found, ref hitPoint, ref hitPart,
                        ref bestScreenDistanceSqr);
                    EvaluatePoint(
                        target, origin, camera, radiusSqr, requireLineOfSight,
                        checkShield,
                        center + cameraRight * horizontal * 0.68f -
                        Vector3.up * vertical * 0.68f,
                        part,
                        ref found, ref hitPoint, ref hitPart,
                        ref bestScreenDistanceSqr);
                    EvaluatePoint(
                        target, origin, camera, radiusSqr, requireLineOfSight,
                        checkShield,
                        center - cameraRight * horizontal * 0.68f -
                        Vector3.up * vertical * 0.68f,
                        part,
                        ref found, ref hitPoint, ref hitPart,
                        ref bestScreenDistanceSqr);
                }
            }

            return found;
        }

        private static void EvaluatePoint(
            Character target,
            Vector3 origin,
            Camera camera,
            float radiusSqr,
            bool requireLineOfSight,
            bool checkShield,
            Vector3 candidatePoint,
            int fallbackPart,
            ref bool found,
            ref Vector3 bestPoint,
            ref byte bestPart,
            ref float bestScreenDistanceSqr)
        {
            Vector3 screen = camera.WorldToScreenPoint(candidatePoint);
            if (screen.z <= 0f) return;
            float dx = screen.x - Screen.width * 0.5f;
            float dy = screen.y - Screen.height * 0.5f;
            float screenDistanceSqr = dx * dx + dy * dy;
            if (screenDistanceSqr > radiusSqr || screenDistanceSqr >= bestScreenDistanceSqr)
                return;

            Vector3 resolvedPoint = candidatePoint;
            int resolvedPart = fallbackPart;
            if (requireLineOfSight)
            {
                RaycastHit hit;
                if (!TryRayToTarget(target, origin, candidatePoint, checkShield, out hit))
                    return;
                resolvedPoint = hit.point;
                try
                {
                    int actorPart = target.GetActorId(hit.collider.transform.name);
                    if (actorPart >= 0 && actorPart < 254) resolvedPart = actorPart;
                }
                catch { }
            }

            found = true;
            bestPoint = resolvedPoint;
            bestPart = (byte)Mathf.Clamp(resolvedPart, 0, 253);
            bestScreenDistanceSqr = screenDistanceSqr;
        }

        private static bool TryRayToTarget(
            Character target,
            Vector3 origin,
            Vector3 targetPoint,
            bool checkShield,
            out RaycastHit hit)
        {
            hit = new RaycastHit();
            Vector3 delta = targetPoint - origin;
            float distance = delta.magnitude;
            if (distance <= 0.001f) return false;

            int mask = LayerMask.GetMask("Terrarin") |
                       LayerMask.GetMask("kController") |
                       LayerMask.GetMask("Weapon") |
                       (1 << 11) |
                       (1 << 15);
            if (!Physics.Raycast(origin, delta / distance, out hit, distance + 0.35f, mask))
                return false;
            if (!BelongsToTarget(target, hit.transform)) return false;
            if (checkShield && hit.collider != null && hit.collider.gameObject.layer == 15)
                return false;
            return true;
        }

        private static bool BelongsToTarget(Character target, Transform hitTransform)
        {
            if (target == null || hitTransform == null) return false;
            try
            {
                Level level = ASSingleton<Level>.Instance;
                if (level != null && level.GetCharacter(hitTransform) == target) return true;
            }
            catch { }
            try { if (target.checkHitTransformIsSelf(hitTransform)) return true; } catch { }

            Transform cursor = hitTransform;
            for (int i = 0; i < 10 && cursor != null; i++)
            {
                if (cursor == target.transform) return true;
                cursor = cursor.parent;
            }
            return false;
        }

        private static bool IsShieldFacingShooter(Character target, Vector3 shooterPosition)
        {
            try
            {
                if (target == null || target.mWeapon == null ||
                    string.IsNullOrEmpty(target.mWeapon.name) ||
                    target.mWeapon.name.IndexOf(
                        "shield",
                        StringComparison.OrdinalIgnoreCase) < 0)
                {
                    return false;
                }
                return target.CalculateHitDirection(shooterPosition) ==
                       Character.DIRECTION.kFront;
            }
            catch
            {
                return false;
            }
        }

        private static Vector3 ClosestPoint(Bounds bounds, Vector3 point)
        {
            Vector3 min = bounds.min;
            Vector3 max = bounds.max;
            return new Vector3(
                Mathf.Clamp(point.x, min.x, max.x),
                Mathf.Clamp(point.y, min.y, max.y),
                Mathf.Clamp(point.z, min.z, max.z));
        }

        private static float NextProbabilityRoll()
        {
            lock (ProbabilitySync)
            {
                ProbabilityRng.GetBytes(ProbabilityBytes);
                uint value = BitConverter.ToUInt32(ProbabilityBytes, 0) & 0x00FFFFFFu;
                return value / 16777216f;
            }
        }
    }
}
