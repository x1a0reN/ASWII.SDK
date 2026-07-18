using ASWDEBUG.Global;
using ASWDEBUG.Main;
using ASWDEBUG.UI;
using System;
using System.Collections.Generic;
using UnityEngine;

namespace ASWDEBUG.Cheats.AutoAim
{
    public static class BossAutoAim
    {
        public static bool Enabled;
        public static BaseBoss bestTarget;
        public static BaseBoss currentTarget;
        public static float closestDistance = float.MaxValue;

        private static readonly List<BaseBoss> BossCache = new List<BaseBoss>(16);

        public static void ToggleEnabled()
        {
            Enabled = !Enabled;
        }

        public static void Enable()
        {
            if (!Enabled || CheatMain.CameraMain == null)
            {
                currentTarget = null;
                bestTarget = null;
                closestDistance = float.MaxValue;
                return;
            }

            Aim();
        }

        private static void Aim()
        {
            if (!Input.GetKey(GlobalHotkeys.PlayerKey))
            {
                currentTarget = null;
                bestTarget = null;
                closestDistance = float.MaxValue;
                return;
            }

            Camera cam = CheatMain.CameraMain != null ? CheatMain.CameraMain : Camera.main;
            if (cam == null) return;

            Character player = null;
            try
            {
                player = ASSingleton<Level>.Instance.GetPlayer();
            }
            catch
            {
                player = null;
            }

            if (player == null || player.camera == null) return;

            Vector3 aimPoint;
            BossColliderData hitData;
            bestTarget = SelectBestBossTarget(cam, ESP.ESP.CircleRadius, out aimPoint, out hitData, out closestDistance);
            if (bestTarget == null) return;

            currentTarget = bestTarget;

            CameraObj cameraObj = player.camera;
            Vector3 eulerAngles = Quaternion.LookRotation((aimPoint - cam.transform.position).normalized).eulerAngles;
            Vector3 currentAngles = cameraObj.transform.eulerAngles;
            float deltaYaw = Mathf.DeltaAngle(currentAngles.y, eulerAngles.y);
            float deltaPitch = Mathf.DeltaAngle(currentAngles.x, eulerAngles.x);

            cameraObj.finalx += deltaYaw * Time.deltaTime * Settings._aimspeed;
            cameraObj.finaly -= deltaPitch * Time.deltaTime * Settings._aimspeed;
        }

        private static BaseBoss SelectBestBossTarget(
            Camera cam,
            float radius,
            out Vector3 aimPoint,
            out BossColliderData hitData,
            out float closestDistance)
        {
            aimPoint = Vector3.zero;
            hitData = null;
            closestDistance = float.MaxValue;

            CollectBosses(BossCache);
            if (BossCache.Count == 0) return null;

            float cx = Screen.width * 0.5f;
            float cy = Screen.height * 0.5f;
            float radiusSqr = radius > 0f ? radius * radius : float.PositiveInfinity;
            float bestDistSqr = float.MaxValue;
            int bestDamageLevel = int.MinValue;
            BaseBoss bestBoss = null;

            for (int i = 0; i < BossCache.Count; i++)
            {
                BaseBoss boss = BossCache[i];
                if (!IsBossUsable(boss)) continue;

                Vector3 candidatePoint;
                BossColliderData candidateData;
                if (!TryGetBossDamagePoint(boss, cam, out candidatePoint, out candidateData))
                    continue;

                Vector3 screen = cam.WorldToScreenPoint(candidatePoint);
                if (screen.z <= 0f) continue;

                float dx = screen.x - cx;
                float dy = screen.y - cy;
                float distSqr = dx * dx + dy * dy;
                if (distSqr > radiusSqr) continue;

                int damageLevel = candidateData != null ? candidateData.damage_level : 0;
                bool betterDamage = damageLevel > bestDamageLevel;
                bool sameDamageCloser = damageLevel == bestDamageLevel && distSqr < bestDistSqr;
                if (!betterDamage && !sameDamageCloser) continue;

                bestBoss = boss;
                aimPoint = candidatePoint;
                hitData = candidateData;
                bestDistSqr = distSqr;
                bestDamageLevel = damageLevel;
            }

            if (bestBoss != null)
                closestDistance = Mathf.Sqrt(bestDistSqr);

            return bestBoss;
        }

        private static void CollectBosses(List<BaseBoss> output)
        {
            output.Clear();

            Level level = null;
            try
            {
                level = ASSingleton<Level>.Instance;
            }
            catch
            {
                level = null;
            }

            if (level == null) return;

            AddBosses(output, level.boss_manager);
            AddBosses(output, level.freedom_boss_manager);

            try
            {
                if (level.bossStorageList != null)
                {
                    for (int i = 0; i < level.bossStorageList.Count; i++)
                        AddBoss(output, level.bossStorageList[i]);
                }
            }
            catch
            {
            }
        }

        private static void AddBosses(List<BaseBoss> output, BossManager manager)
        {
            if (manager == null) return;

            try
            {
                List<BaseBoss> bosses = manager.GetBosses();
                if (bosses == null) return;

                for (int i = 0; i < bosses.Count; i++)
                    AddBoss(output, bosses[i]);
            }
            catch
            {
            }
        }

        private static void AddBoss(List<BaseBoss> output, BaseBoss boss)
        {
            if (boss == null || output.Contains(boss)) return;
            output.Add(boss);
        }

        private static bool IsBossUsable(BaseBoss boss)
        {
            try
            {
                if (boss == null || !boss.GetActive()) return false;
                if (boss.hp <= 0 || boss.max_hp <= 0f) return false;
                return true;
            }
            catch
            {
                return boss != null;
            }
        }

        public static bool TryResolveBossTrackShot(
            int decodedBossUid,
            Vector3 shotOrigin,
            Vector3 shotDirection,
            out int bossUid,
            out Vector3 point,
            out BossColliderData hitData,
            out short distance,
            out int damageLevel)
        {
            bossUid = 0;
            point = Vector3.zero;
            hitData = null;
            distance = 0;
            damageLevel = 0;

            if (shotDirection.sqrMagnitude <= 0.0001f)
            {
                Camera cam = CheatMain.CameraMain != null ? CheatMain.CameraMain : Camera.main;
                if (cam != null) shotDirection = cam.transform.forward;
            }

            if (shotDirection.sqrMagnitude <= 0.0001f) return false;
            shotDirection.Normalize();

            CollectBosses(BossCache);
            if (BossCache.Count == 0) return false;

            BaseBoss preferredBoss = null;
            if (decodedBossUid != 0)
            {
                for (int i = 0; i < BossCache.Count; i++)
                {
                    BaseBoss boss = BossCache[i];
                    if (!IsBossUsable(boss)) continue;

                    int uid = GetBossUid(boss, null);
                    if (uid == decodedBossUid)
                    {
                        preferredBoss = boss;
                        break;
                    }
                }
            }

            if (preferredBoss != null)
            {
                float dot;
                float worldDistance;
                if (TryGetBestDamagePointForShot(preferredBoss, shotOrigin, shotDirection, false,
                    out point, out hitData, out damageLevel, out dot, out worldDistance))
                {
                    bossUid = GetBossUid(preferredBoss, hitData);
                    distance = ClampDistance(worldDistance);
                    return bossUid != 0;
                }
            }

            BaseBoss bestBoss = null;
            BossColliderData bestData = null;
            Vector3 bestPoint = Vector3.zero;
            int bestDamage = int.MinValue;
            float bestDot = -2f;
            float bestDistance = float.MaxValue;

            for (int i = 0; i < BossCache.Count; i++)
            {
                BaseBoss boss = BossCache[i];
                if (!IsBossUsable(boss)) continue;

                Vector3 candidatePoint;
                BossColliderData candidateData;
                int candidateDamage;
                float candidateDot;
                float candidateDistance;
                if (!TryGetBestDamagePointForShot(boss, shotOrigin, shotDirection, true,
                    out candidatePoint, out candidateData, out candidateDamage, out candidateDot, out candidateDistance))
                    continue;

                bool betterDamage = candidateDamage > bestDamage;
                bool sameDamageBetterAngle = candidateDamage == bestDamage && candidateDot > bestDot + 0.0001f;
                bool sameDamageAngleCloser = candidateDamage == bestDamage && Mathf.Abs(candidateDot - bestDot) <= 0.0001f && candidateDistance < bestDistance;
                if (!betterDamage && !sameDamageBetterAngle && !sameDamageAngleCloser) continue;

                bestBoss = boss;
                bestData = candidateData;
                bestPoint = candidatePoint;
                bestDamage = candidateDamage;
                bestDot = candidateDot;
                bestDistance = candidateDistance;
            }

            if (bestBoss == null || bestData == null) return false;

            bossUid = GetBossUid(bestBoss, bestData);
            point = bestPoint;
            hitData = bestData;
            damageLevel = bestDamage;
            distance = ClampDistance(bestDistance);
            return bossUid != 0;
        }

        private static bool TryGetBestDamagePointForShot(
            BaseBoss boss,
            Vector3 shotOrigin,
            Vector3 shotDirection,
            bool requireForward,
            out Vector3 point,
            out BossColliderData hitData,
            out int damageLevel,
            out float directionDot,
            out float worldDistance)
        {
            point = Vector3.zero;
            hitData = null;
            damageLevel = 0;
            directionDot = -2f;
            worldDistance = float.MaxValue;

            if (boss == null || shotDirection.sqrMagnitude <= 0.0001f) return false;

            BossColliderData bestData = null;
            Vector3 bestPoint = Vector3.zero;
            int bestDamageLevel = int.MinValue;
            float bestDot = -2f;
            float bestDistance = float.MaxValue;

            try
            {
                List<BossColliderData> colliders = boss.colliderList;
                if (colliders == null) return false;

                for (int i = 0; i < colliders.Count; i++)
                {
                    BossColliderData data = colliders[i];
                    if (data == null || data.collider == null || !data.collider.enabled)
                        continue;

                    Vector3 candidate = data.collider.bounds.center;
                    Vector3 toCandidate = candidate - shotOrigin;
                    float candidateDistance = toCandidate.magnitude;
                    if (candidateDistance <= 0.001f) continue;

                    float dot = Vector3.Dot(shotDirection, toCandidate / candidateDistance);
                    if (requireForward && dot <= 0f) continue;

                    int candidateDamage = data.damage_level;
                    bool betterDamage = candidateDamage > bestDamageLevel;
                    bool sameDamageBetterAngle = candidateDamage == bestDamageLevel && dot > bestDot + 0.0001f;
                    bool sameDamageAngleCloser = candidateDamage == bestDamageLevel && Mathf.Abs(dot - bestDot) <= 0.0001f && candidateDistance < bestDistance;
                    if (!betterDamage && !sameDamageBetterAngle && !sameDamageAngleCloser) continue;

                    bestData = data;
                    bestPoint = candidate;
                    bestDamageLevel = candidateDamage;
                    bestDot = dot;
                    bestDistance = candidateDistance;
                }
            }
            catch
            {
                return false;
            }

            if (bestData == null) return false;

            point = bestPoint;
            hitData = bestData;
            damageLevel = bestDamageLevel;
            directionDot = bestDot;
            worldDistance = bestDistance;
            return true;
        }

        private static int GetBossUid(BaseBoss boss, BossColliderData hitData)
        {
            try
            {
                if (hitData != null && hitData.owner != null)
                    return unchecked((int)hitData.owner.GetUid());
            }
            catch
            {
            }

            try
            {
                if (boss != null)
                    return unchecked((int)boss.GetUid());
            }
            catch
            {
            }

            return 0;
        }

        private static short ClampDistance(float value)
        {
            if (float.IsNaN(value) || float.IsInfinity(value) || value < 0f) return 0;
            if (value > short.MaxValue) return short.MaxValue;
            return (short)value;
        }

        private static bool TryGetBossDamagePoint(
            BaseBoss boss,
            Camera cam,
            out Vector3 point,
            out BossColliderData hitData)
        {
            point = Vector3.zero;
            hitData = null;

            if (boss == null) return false;

            BossColliderData bestData = null;
            Vector3 bestPoint = Vector3.zero;
            int bestDamageLevel = int.MinValue;
            float bestScreenDistSqr = float.MaxValue;
            float cx = Screen.width * 0.5f;
            float cy = Screen.height * 0.5f;

            try
            {
                List<BossColliderData> colliders = boss.colliderList;
                if (colliders != null)
                {
                    for (int i = 0; i < colliders.Count; i++)
                    {
                        BossColliderData data = colliders[i];
                        if (data == null || data.collider == null || !data.collider.enabled)
                            continue;

                        Vector3 candidate = data.collider.bounds.center;
                        Vector3 screen = cam.WorldToScreenPoint(candidate);
                        if (screen.z <= 0f) continue;

                        float dx = screen.x - cx;
                        float dy = screen.y - cy;
                        float distSqr = dx * dx + dy * dy;
                        int damageLevel = data.damage_level;

                        bool betterDamage = damageLevel > bestDamageLevel;
                        bool sameDamageCloser = damageLevel == bestDamageLevel && distSqr < bestScreenDistSqr;
                        if (!betterDamage && !sameDamageCloser) continue;

                        bestData = data;
                        bestPoint = candidate;
                        bestDamageLevel = damageLevel;
                        bestScreenDistSqr = distSqr;
                    }
                }
            }
            catch
            {
            }

            if (bestData != null)
            {
                point = bestPoint;
                hitData = bestData;
                return true;
            }

            try
            {
                point = boss.GetPosition();
                Transform transform = boss.getTransfrom();
                if (transform != null)
                {
                    Renderer[] renderers = transform.GetComponentsInChildren<Renderer>(true);
                    if (renderers != null && renderers.Length > 0)
                    {
                        Bounds bounds = renderers[0].bounds;
                        for (int i = 1; i < renderers.Length; i++)
                            bounds.Encapsulate(renderers[i].bounds);
                        point = bounds.center;
                    }
                    else
                    {
                        point += Vector3.up * 1.2f;
                    }
                }
                else
                {
                    point += Vector3.up * 1.2f;
                }
                return true;
            }
            catch
            {
                return false;
            }
        }
    }
}
