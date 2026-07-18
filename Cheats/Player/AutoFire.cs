using UnityEngine;

namespace ASWDEBUG.Cheats.Player
{
    public static class AutoFire
    {
        public static bool IsCrosshairOnEnemyExact(Character target)
        {
            if (target == null || target.IsDied || target.Is_Viewer || target.GetHidden()) return false;
            Level level = ASSingleton<Level>.Instance;
            Character player = level == null ? null : level.GetPlayer();
            Camera camera = Camera.main;
            if (player == null || camera == null) return false;

            Ray ray = camera.ScreenPointToRay(new Vector3(Screen.width * 0.5f, Screen.height * 0.5f, 0f));
            RaycastHit hit;
            if (!Physics.SphereCast(ray, 0.05f, out hit, 180f,
                LayerMask.GetMask(new string[] { "kPlayer", "Terrarin" }))) return false;
            if (hit.transform == null || target.transform == null) return false;
            return hit.transform.root == target.transform.root;
        }
    }
}
