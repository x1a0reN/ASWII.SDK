using ASWDEBUG.Cheats.Player;
using ASWDEBUG.Global;
using ASWDEBUG.Logger;
using ASWDEBUG.Main;
using ASWDEBUG.UI;
using Harmony;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using UnityEngine;

namespace ASWDEBUG.Cheats.AutoAim
{
    public class AutoAim
    {
        public static bool Enabled = true;

        public static bool Wall = true;
        public static bool Shield = true;
        public static bool Hidden = true;

        public static bool AimLocking = false;
        public static Character bestTarget = null;
        public static Character currentTarget;
        public static float closestDistance = float.MaxValue;

        private const float RecentManipulationWindowSeconds = 0.35f;
        private static float _lastManipulationRealtime = -1f;
        private static int _lastManipulationFrame = -1;
        private static int _lastManipulationTargetUid;

        // —— 一次性缓存 —— //
        static FieldInfo s_Field_Character_data;
        static bool s_Field_Character_data_Scanned;

        static Camera GetCamera()
        {
            Camera cam = (CheatMain.CameraMain != null) ? CheatMain.CameraMain : Camera.main;
            return cam;
        }

        public static void Enable()
        {
            if (!Enabled || CheatMain.CameraMain == null)
            {
                ResetLockState();
                return;
            }

            Aim();
        }

        public static void Disable()
        {
            Wall = false;
            Shield = false;
            Hidden = false;
            ResetLockState();
        }

        public static void ToggleEnabled()
        {
            Enabled = !Enabled;
            if (!Enabled) ResetLockState();
        }
        public static void ToggleWall() { Wall = !Wall; }
        public static void ToggleShield() { Shield = !Shield; }
        public static void ToggleHidden() { Hidden = !Hidden; }

        /// <summary>
        /// 从 Character 实例上读出 data 字段（世界坐标）；
        /// 若字段不存在或读取失败，回退到 ch.transform.position
        /// </summary>
        static Vector3 GetCharacterDataPosition(Character ch)
        {
            if (ch == null) return Vector3.zero;

            // 第一次调用时解析 FieldInfo（只做一次）
            if (!s_Field_Character_data_Scanned)
            {
                try
                {
                    // 私有实例字段 data（大小写按实际来；如有不同请改成正确的字段名）
                    s_Field_Character_data = typeof(Character).GetField(
                        "data",
                        BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public
                    );
                }
                catch { s_Field_Character_data = null; }
                s_Field_Character_data_Scanned = true;
            }

            if (s_Field_Character_data != null)
            {
                try
                {
                    object val = s_Field_Character_data.GetValue(ch);
                    if (val is Vector3) return (Vector3)val;
                }
                catch { /* 忽略，回退 transform */ }
            }

            // 回退：直接用 Transform 位置（没有反射成本）
            if (ch.transform != null)
                return ch.transform.position;

            return Vector3.zero;
        }

        /// <summary>
        /// 选择最佳目标（按与屏幕中心的最近距离；可选视线、盾背、隐身过滤）
        /// 返回值：最佳 Character；out closestDistance：与屏幕中心的线性距离（像素）
        /// </summary>
        public static Character SelectBestTarget(
            float radius,
            bool requireLineOfSight, // 对应你原来的 Wall（Wall=true 表示需要视线）
            bool allowShieldBack,    // Shield=false 时过滤“盾且背对”目标
            bool allowHidden,        // Hidden=false 时过滤“隐身”目标
            out float closestDistance
        )
        {
            closestDistance = float.MaxValue;

            // 相机 & 玩家
            Camera cam = GetCamera();
            if (cam == null) return null;

            Character player = ASSingleton<Level>.Instance.GetPlayer();
            if (player == null) return null;

            int playerTeam = player.GetTeam();

            // 屏幕中心与半径平方（不用 Sqrt）
            float cx = Screen.width * 0.5f;
            float cy = Screen.height * 0.5f;
            float radiusSqr = radius * radius;

            Character best = null;
            float bestDistSqr = float.MaxValue;

            // 遍历候选
            // 注意：不要在循环里做反射/字符串分配等重操作
            foreach (Character ch in CharacterManager.Instance.character_set)
            {
                if (ch == null) continue;

                // 1) 轻量过滤：同队、已死
                if (ch.GetTeam() == playerTeam || ch.IsDied) continue;

                // 2) 取“身体点”（来自 data 字段；失败时回退 transform）
                Vector3 body = GetCharacterDataPosition(ch);

                // 头部点用于射线（保持你原来的 +1.2f）
                Vector3 head = body;
                head.y += 1.2f;

                // 3) 投影到屏幕（先身体判断距离/可见，再需要时用头部做射线）
                Vector3 spBody = cam.WorldToScreenPoint(body);
                if (spBody.z <= 0f) continue; // 在相机后方

                // 与屏幕中心的平方距离（注意：用同一坐标系，无需翻转 Y）
                float dx = spBody.x - cx;
                float dy = spBody.y - cy;
                float distSqr = dx * dx + dy * dy;

                // 半径 & 最优剪枝
                if (distSqr > radiusSqr || distSqr >= bestDistSqr) continue;

                // 4) 需要视线（昂贵操作推后）
                if (requireLineOfSight)
                {
                    Vector3 spHead = cam.WorldToScreenPoint(head);
                    if (spHead.z <= 0f) continue;

                    Ray ray = cam.ScreenPointToRay(spHead);
                    RaycastHit hit;
                    if (!Physics.Raycast(ray, out hit))
                        continue;

                    // 兼容你原来的名字判定（baseName / "hit_collider"）
                    // 更稳妥的做法是直接比 transform.root 引用，但这里沿用名字逻辑
                    Transform hitRoot = (hit.transform != null) ? hit.transform.root : null;
                    string hitRootName = (hitRoot != null) ? hitRoot.name : null;

                    // 如果既不是这个角色的根名字，也不是 "hit_collider"，则视为被墙挡
                    string baseName = ch.baseName;
                    if (hitRootName != baseName && hitRootName != "hit_collider")
                        continue;
                }

                // 5) 盾 + 背对过滤（仅当不允许时）
                if (allowShieldBack)
                {
                    bool isBack = (ch.CalculateHitDirection(player.transform.position) == Character.DIRECTION.kBack);

                    // 字符串匹配放最后，减少无用开销
                    bool isShield = false;
                    if (ch.mWeapon != null && ch.mWeapon.name != null)
                    {
                        // 忽略大小写
                        isShield = (ch.mWeapon.name.IndexOf("shield", StringComparison.OrdinalIgnoreCase) >= 0);
                    }

                    if (isShield && isBack) continue;
                }

                // 6) 隐身过滤（仅当不允许时）
                if (allowHidden && ch.GetHidden()) continue;

                // —— 成为当前最佳 —— 
                best = ch;
                bestDistSqr = distSqr;
            }

            // 输出线性距离（真的需要时才 Sqrt 一次）
            if (best != null)
                closestDistance = Mathf.Sqrt(bestDistSqr);

            return best;
        }

        private static void ResetLockState()
        {
            AimLocking = false;
            bestTarget = null;
            currentTarget = null;
            closestDistance = float.MaxValue;
        }

        public static bool TryGetRecentManipulation(out int targetUid)
        {
            targetUid = 0;
            try
            {
                float now = Time.realtimeSinceStartup;
                float elapsed = now - _lastManipulationRealtime;
                int frameDelta = Time.frameCount - _lastManipulationFrame;
                if (_lastManipulationRealtime < 0f ||
                    _lastManipulationFrame < 0 ||
                    elapsed < 0f ||
                    frameDelta < 0 ||
                    elapsed > RecentManipulationWindowSeconds ||
                    _lastManipulationTargetUid <= 0)
                {
                    return false;
                }

                targetUid = _lastManipulationTargetUid;
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static void RecordManipulation(Character target)
        {
            if (target == null) return;

            _lastManipulationTargetUid = target.uid;
            _lastManipulationFrame = Time.frameCount;
            _lastManipulationRealtime = Time.realtimeSinceStartup;
        }

        private static void Aim()
        {
            if (!Enabled || !Input.GetKey(GlobalHotkeys.PlayerKey))
            {
                ResetLockState();
                return;
            }

            bestTarget = SelectBestTarget(ESP.ESP.CircleRadius, Wall, Shield, Hidden, out closestDistance);
            if (!bestTarget)
            {
                ResetLockState();
                return;
            }

            Character player = ASSingleton<Level>.Instance.GetPlayer();
            Transform head = bestTarget.getBone("web__head");
            Camera viewCamera = GetCamera();
            CameraObj camera = player != null ? player.camera : null;
            if (player == null || head == null || viewCamera == null || camera == null)
            {
                ResetLockState();
                return;
            }

            currentTarget = bestTarget;
            AimLocking = true;

            Vector3 eulerAngles = Quaternion.LookRotation((head.position - viewCamera.transform.position).normalized).eulerAngles;
            Vector3 eulerAngles2 = camera.transform.eulerAngles;
            float num = Mathf.DeltaAngle(eulerAngles2.y, eulerAngles.y);
            float num2 = Mathf.DeltaAngle(eulerAngles2.x, eulerAngles.x);
            camera.finalx += num * Time.deltaTime * Settings._aimspeed;
            camera.finaly -= num2 * Time.deltaTime * Settings._aimspeed;
            // Keep the actual camera manipulation alive across Update ordering and key release.
            RecordManipulation(currentTarget);
        }
    }
}
