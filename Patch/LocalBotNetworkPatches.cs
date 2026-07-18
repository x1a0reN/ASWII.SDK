using ASWDEBUG.Cheats.LocalBot;
using Harmony;
using System;
using System.Reflection;
using UnityEngine;

namespace ASWDEBUG.Patch
{
    internal static class Patch_LocalBot_SyncPlayerData
    {
        private static MethodBase TargetMethod()
        {
            return AccessTools.Method(typeof(ChannelConnection), "SyncPlayerData", new Type[] { typeof(Character) });
        }

        private static bool Prefix(Character __0)
        {
            return !LocalBotManager.Contains(__0);
        }
    }

    internal static class Patch_LocalBot_SelfHurt
    {
        private static MethodBase TargetMethod()
        {
            return AccessTools.Method(typeof(ChannelConnection), "SelfHurt", new Type[] { typeof(Character), typeof(float) });
        }

        private static bool Prefix(Character __0)
        {
            return !LocalBotManager.Contains(__0);
        }
    }

    internal static class Patch_LocalBot_Reload
    {
        private static MethodBase TargetMethod()
        {
            return AccessTools.Method(typeof(ChannelConnection), "Reload", new Type[] { typeof(Character) });
        }

        private static bool Prefix(Character __0)
        {
            return !LocalBotManager.Contains(__0);
        }
    }

    internal static class Patch_LocalBot_SelectObjectBase
    {
        private static MethodBase TargetMethod()
        {
            return AccessTools.Method(typeof(ChannelConnection), "SelectObjectBase", new Type[] { typeof(Character), typeof(byte) });
        }

        private static bool Prefix(Character __0)
        {
            return !LocalBotManager.Contains(__0);
        }
    }

    internal static class Patch_LocalBot_Use
    {
        private static MethodBase TargetMethod()
        {
            return AccessTools.Method(typeof(ChannelConnection), "Use", new Type[] { typeof(byte), typeof(int), typeof(byte) });
        }

        private static bool Prefix(int __1)
        {
            return !LocalBotManager.IsLocalRobotUid(__1);
        }
    }

    internal static class Patch_LocalBot_Shoot
    {
        private static MethodBase TargetMethod()
        {
            return AccessTools.Method(
                typeof(ChannelConnection),
                "Shoot",
                new Type[] { typeof(Vector3), typeof(Vector3), typeof(HitMessage), typeof(byte), typeof(bool), typeof(Vector3) });
        }

        private static bool Prefix(HitMessage __2)
        {
            if (!LocalBotManager.ShouldSuppressShot(__2)) return true;
            LocalBotManager.TryApplyShot(__2);
            return false;
        }
    }

    internal static class Patch_LocalBot_ArrowHit
    {
        private static MethodBase TargetMethod()
        {
            return AccessTools.Method(
                typeof(ChannelConnection),
                "ArrowHit",
                new Type[] { typeof(byte), typeof(byte), typeof(byte), typeof(Quaternion), typeof(Vector3) });
        }

        private static bool Prefix(byte __1, byte __2, Vector3 __4)
        {
            if (!LocalBotManager.IsLocalCharacterUid(__1)) return true;
            LocalBotManager.TryApplyArrowHit(__1, __2, __4);
            return false;
        }
    }
}
