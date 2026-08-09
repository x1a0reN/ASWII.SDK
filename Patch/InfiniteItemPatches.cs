using System;
using System.Reflection;
using Harmony;
using ASWDEBUG.Logger;
using UnityEngine;

namespace ASWDEBUG.Patch
{
    internal static class InfiniteItemUse
    {
        private const short StableUsableCount = 99;
        internal static bool Enabled;
        private static int _useLogCount;
        private static int _sweepLogCount;
        private static int _lastPreparedFrame = -1;

        internal static void Tick(Character player)
        {
            if (!Enabled || player == null)
            {
                return;
            }

            int frame = Time.frameCount;
            if (_lastPreparedFrame == frame)
            {
                return;
            }
            _lastPreparedFrame = frame;

            PreparePlayerSlots(player);
        }

        internal static void PreparePlayerSlots(Character player)
        {
            ObjectBaseInfo[] slots = GetSlots(player);
            if (slots == null)
            {
                return;
            }

            int prepared = 0;
            for (int i = 0; i < slots.Length; i++)
            {
                ItemInfo item = slots[i] as ItemInfo;
                if (item == null)
                {
                    continue;
                }

                Prepare(item);
                prepared++;
            }

            if (prepared > 0 && _sweepLogCount < 3)
            {
                _sweepLogCount++;
                FileLogger.Log("INFINITE-ITEM", "Prepared player item slots. count=" + prepared);
            }
        }

        internal static void Prepare(ItemInfo item)
        {
            if (!Enabled || item == null)
            {
                return;
            }

            if ((short)item.count < StableUsableCount)
            {
                item.count = StableUsableCount;
            }

            item.cooling = 0f;
            item.stop_cooling = false;
            item.cool_down_ready = true;
        }

        internal static bool SendUse(ItemInfo item)
        {
            if (!Enabled || item == null)
            {
                return false;
            }

            Prepare(item);
            byte slot = item.slot;

            if (GameApp.Instance == null ||
                GameApp.Instance.channel_connection == null)
            {
                return false;
            }

            GameApp.Instance.channel_connection.Use(slot);
            Prepare(item);

            if (_useLogCount < 12)
            {
                _useLogCount++;
                FileLogger.Log(
                    "INFINITE-ITEM",
                    "Sent unrestricted item use. slot=" + slot +
                    " subtype=" + item.sub_type +
                    " count=" + (short)item.count);
            }

            return true;
        }

        internal static void Toggle()
        {
            Enabled = !Enabled;
            FileLogger.Log("INFINITE-ITEM",
                "Infinite item use " + (Enabled ? "enabled" : "disabled"));
        }

        private static ObjectBaseInfo[] GetSlots(Character player)
        {
            try
            {
                if (player == null ||
                    player.character_info == null ||
                    player.character_info.slots_info == null)
                {
                    return null;
                }

                return player.character_info.slots_info.object_info;
            }
            catch
            {
                return null;
            }
        }
    }

    [HarmonyPatch]
    [Obfuscation(Exclude = true, ApplyToMembers = true, Feature = "-rename", StripAfterObfuscation = false)]
    public static class Patch_ItemInfo_Initialize_InfiniteUse
    {
        [Obfuscation(Exclude = true, Feature = "-rename")]
        static MethodBase TargetMethod()
        {
            return AccessTools.Method(typeof(ItemInfo), "Initialize", Type.EmptyTypes, null);
        }

        [Obfuscation(Exclude = true, Feature = "-rename")]
        static void Postfix(ItemInfo __instance)
        {
            InfiniteItemUse.Prepare(__instance);
        }
    }

    [HarmonyPatch]
    [Obfuscation(Exclude = true, ApplyToMembers = true, Feature = "-rename", StripAfterObfuscation = false)]
    public static class Patch_NewSkillBar_ChangeWeapon_InfiniteItemUse
    {
        [Obfuscation(Exclude = true, Feature = "-rename")]
        static MethodBase TargetMethod()
        {
            return AccessTools.Method(
                typeof(NewSkillBar),
                "changeWeapon",
                new Type[] { typeof(int), typeof(bool) },
                null);
        }

        [Obfuscation(Exclude = true, Feature = "-rename")]
        static void Prefix(NewSkillBar __instance, int index)
        {
            if (__instance == null ||
                __instance.player == null ||
                __instance.player.character_info == null ||
                __instance.player.character_info.slots_info == null ||
                __instance.player.character_info.slots_info.object_info == null)
            {
                return;
            }

            ObjectBaseInfo[] slots = __instance.player.character_info.slots_info.object_info;
            if (index < 0 || index >= slots.Length)
            {
                return;
            }

            InfiniteItemUse.Prepare(slots[index] as ItemInfo);
        }
    }

    [HarmonyPatch]
    [Obfuscation(Exclude = true, ApplyToMembers = true, Feature = "-rename", StripAfterObfuscation = false)]
    public static class Patch_ItemInfo_Action_InfiniteUse
    {
        [Obfuscation(Exclude = true, Feature = "-rename")]
        static MethodBase TargetMethod()
        {
            return AccessTools.Method(typeof(ItemInfo), "Action", Type.EmptyTypes, null);
        }

        [Obfuscation(Exclude = true, Feature = "-rename")]
        static bool Prefix(ItemInfo __instance, ref bool __result)
        {
            if (!InfiniteItemUse.Enabled)
            {
                return true;
            }

            try
            {
                __result = InfiniteItemUse.SendUse(__instance);
            }
            catch (Exception e)
            {
                __result = false;
                FileLogger.Log("INFINITE-ITEM", "ItemInfo.Action failed: " + e);
            }

            return false;
        }
    }

    [HarmonyPatch]
    [Obfuscation(Exclude = true, ApplyToMembers = true, Feature = "-rename", StripAfterObfuscation = false)]
    public static class Patch_ItemInfo_Use_InfiniteUse
    {
        [Obfuscation(Exclude = true, Feature = "-rename")]
        static MethodBase TargetMethod()
        {
            return AccessTools.Method(typeof(ItemInfo), "Use", Type.EmptyTypes, null);
        }

        [Obfuscation(Exclude = true, Feature = "-rename")]
        static void Prefix(ItemInfo __instance)
        {
            InfiniteItemUse.Prepare(__instance);
        }

        [Obfuscation(Exclude = true, Feature = "-rename")]
        static void Postfix(ItemInfo __instance)
        {
            InfiniteItemUse.Prepare(__instance);
        }
    }

    [HarmonyPatch]
    [Obfuscation(Exclude = true, ApplyToMembers = true, Feature = "-rename", StripAfterObfuscation = false)]
    public static class Patch_ItemInfo_SetUsedNum_InfiniteUse
    {
        [Obfuscation(Exclude = true, Feature = "-rename")]
        static MethodBase TargetMethod()
        {
            return AccessTools.Method(typeof(ItemInfo), "SetUsedNum", new Type[] { typeof(int) }, null);
        }

        [Obfuscation(Exclude = true, Feature = "-rename")]
        static void Prefix(ItemInfo __instance)
        {
            InfiniteItemUse.Prepare(__instance);
        }

        [Obfuscation(Exclude = true, Feature = "-rename")]
        static void Postfix(ItemInfo __instance)
        {
            InfiniteItemUse.Prepare(__instance);
        }
    }

    [HarmonyPatch]
    [Obfuscation(Exclude = true, ApplyToMembers = true, Feature = "-rename", StripAfterObfuscation = false)]
    public static class Patch_ConnectionDef_ReadItemInfo_InfiniteUse
    {
        [Obfuscation(Exclude = true, Feature = "-rename")]
        static MethodBase TargetMethod()
        {
            return AccessTools.Method(
                typeof(ConnectionDef),
                "ReadItemInfo",
                new Type[] { typeof(global::NetworkStream), typeof(ItemInfo) },
                null);
        }

        [Obfuscation(Exclude = true, Feature = "-rename")]
        static void Postfix(ItemInfo __1)
        {
            InfiniteItemUse.Prepare(__1);
        }
    }
}
