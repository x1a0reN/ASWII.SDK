using ASWDEBUG.Logger;

namespace ASWDEBUG.Cheats.AutoAim
{
    public static class MotherBossAutoClear
    {
        public static bool Enabled;

        public static bool SendingDirectMotherShot { get; private set; }

        public static void ToggleEnabled()
        {
            Enabled = !Enabled;
            SendingDirectMotherShot = false;
            ExpeditionBossLockController.OnToggle(Enabled);
            FileLogger.Log("MOTHER-LOCK", "enabled=" + Enabled + " distance=6m");
        }

        public static void Tick(Level level, Character player)
        {
            ExpeditionBossLockController.Tick(level, player);
        }

        internal static void SetDirectShotState(bool sending)
        {
            SendingDirectMotherShot = sending;
        }

        internal static bool IsManagedMotherUid(long uid)
        {
            return ExpeditionBossLockController.IsManagedBossUid(uid);
        }
    }
}
