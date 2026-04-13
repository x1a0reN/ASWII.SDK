using ASWDEBUG.Global;
using ASWDEBUG.Logger;
using ASWDEBUG.Main;
using PDE.Animation;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using UnityEngine;

namespace ASWDEBUG.Cheats.Player
{
    public class AutoFire
    {
        public static bool Enabled;

        public static bool AutoFireAllowed;
        public static void Enable()
        {
            if (CheatMain.CameraMain != null)
            {
                Fire();
            }
        }

        public static void Toggle()
        {
            Enabled = !Enabled;
        }
        public static void ToggleAutoFireAllowed()
        {
            AutoFireAllowed = !AutoFireAllowed;
        }
        /// <summary>
        /// 完全复刻 Character.UpdateBloodBar 中“准星是否指到该敌人”的判定。
        /// </summary>
        public static bool IsCrosshairOnEnemyExact(Character target)
        {
            if (target == null) { Log("AF", "IsCrosshairOnEnemyExact: target=null"); return false; }

            var lvl = ASSingleton<Level>.Instance;
            if (lvl == null) { Log("AF", "IsCrosshairOnEnemyExact: lvl=null"); return false; }

            var player = lvl.GetPlayer();
            if (player == null) { Log("AF", "IsCrosshairOnEnemyExact: player=null"); return false; }

            // 同队/已死直接否
            if (target.GetTeam() == player.GetTeam()) { Log("AF", $"IsCrosshairOnEnemyExact: same team ({target.baseName})"); return false; }
            if (target.IsDied) { Log("AF", $"IsCrosshairOnEnemyExact: target died ({target.baseName})"); return false; }

            // 观战但非GP时，不进行准星命中判断
            if (player.Is_Viewer && !player.Is_GP)
            {
                Log("AF", "IsCrosshairOnEnemyExact: viewer && !GP => skip");
                return false;
            }

            var cam = Camera.main;
            if (cam == null) { Log("AF", "IsCrosshairOnEnemyExact: Camera.main=null"); return false; }

            // ── 和原逻辑一致的 SphereCast ──
            Ray ray = cam.ScreenPointToRay(new Vector3(Screen.width * 0.5f, Screen.height * 0.5f, 0f));
            RaycastHit hit;
            bool hitOk = Physics.SphereCast(ray, 0.05f, out hit, 100f, LayerMask.GetMask(new string[] { "kPlayer", "Terrarin" }));
            if (!hitOk)
            {
                Log("AF", "IsCrosshairOnEnemyExact: SphereCast miss");
                return false;
            }

            var root = (hit.transform != null) ? hit.transform.root : null;
            string rootName = (root != null) ? root.name : "null";
            Log("AF", $"IsCrosshairOnEnemyExact: SphereCast hit root='{rootName}' need='{target.baseName}'");

            if (root == null || root.name != target.baseName)
            {
                Log("AF", "IsCrosshairOnEnemyExact: root name not match target.baseName");
                return false;
            }

            // 可见度（与原逻辑一致）
            float hpAlpha = 0f;
            try
            {
                hpAlpha = (!target.GetHidden()) ? 1f : player.SeeEffect(target);
            }
            catch (Exception e)
            {
                Log("AF", $"IsCrosshairOnEnemyExact: SeeEffect error: {e}");
                hpAlpha = 0f;
            }

            Log("AF", $"IsCrosshairOnEnemyExact: hpAlpha={hpAlpha:0.###}, playerHidden={player.GetHidden()}");

            // hpAlpha 必须 == 1
            if (hpAlpha != 1f)
            {
                Log("AF", "IsCrosshairOnEnemyExact: hpAlpha != 1 => false");
                return false;
            }

            // 本地玩家处于隐身 => 不允许
            if (player.GetHidden())
            {
                Log("AF", "IsCrosshairOnEnemyExact: local player hidden => false");
                return false;
            }

            Log("AF", $"IsCrosshairOnEnemyExact: OK for '{target.baseName}'");
            return true;
        }

        /// <summary>
        /// 用“完全一致”的准星命中逻辑修复 Fire()；一次检测 + 匹配 baseName 的角色。
        /// </summary>
        public static void Fire()
        {
            AutoFireAllowed = false;
            if (!Enabled) { Log("AF", "Fire: not enabled"); return; }

            var lvl = ASSingleton<Level>.Instance;
            if (lvl == null) { Log("AF", "Fire: lvl=null"); return; }

            var player = lvl.GetPlayer();
            if (player == null) { Log("AF", "Fire: player=null"); return; }

            if (player.Is_Viewer && !player.Is_GP)
            {
                Log("AF", "Fire: viewer && !GP => skip");
                return;
            }

            var cam = Camera.main;
            if (cam == null) { Log("AF", "Fire: Camera.main=null"); return; }

            // ── 和原逻辑一致：中心点 SphereCast ──
            Ray ray = cam.ScreenPointToRay(new Vector3(Screen.width * 0.5f, Screen.height * 0.5f, 0f));
            RaycastHit hit;
            if (!Physics.SphereCast(ray, 0.05f, out hit, 100f, LayerMask.GetMask(new string[] { "kPlayer", "Terrarin" })))
            {
                Log("AF", "Fire: SphereCast miss");
                return;
            }

            var root = (hit.transform != null) ? hit.transform.root : null;
            string rootName = (root != null) ? root.name : "null";
            Log("AF", $"Fire: SphereCast hit root='{rootName}'");

            if (root == null)
            {
                Log("AF", "Fire: hit.root is null");
                return;
            }

            // 在集合里找匹配 baseName 的角色
            Character target = null;
            try
            {
                foreach (var ch in CharacterManager.Instance.character_set)
                {
                    if (ch == null) continue;
                    if (ch.baseName == root.name) { target = ch; break; }
                }
            }
            catch (Exception e)
            {
                Log("AF", $"Fire: iterate character_set error: {e}");
            }

            if (target == null)
            {
                Log("AF", "Fire: no Character matched by baseName");
                return;
            }

            bool ok = IsCrosshairOnEnemyExact(target);
            AutoFireAllowed = ok;
            Log("AF", $"Fire: AutoFireAllowed={AutoFireAllowed} target='{target.baseName}'");
        }


        private static void Log(string tag, string msg)
        {
            try
            {
                // 按你的要求：直接用这个函数体
                //FileLogger.Log(tag, msg);
                return;
            }
            catch
            {
                // 如果 WriteLine 不可用，兜底到 Unity 日志，避免因日志崩溃
                try { Debug.Log("[" + tag + "] " + msg); } catch { }
            }
        }
    }
}
