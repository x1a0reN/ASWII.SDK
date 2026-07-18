using System;
using System.Reflection;
using ASWDEBUG.Cheats.AutoBattle;
using ASWDEBUG.Cheats.SurvivalBot;
using ASWDEBUG.Logger;
using Harmony;
using UnityEngine;

namespace ASWDEBUG.Patch
{
    public static class HarmonyLoader
    {
        private static bool _installed;

        public static void Install()
        {
            if (_installed) return;
            _installed = true;

            HarmonyInstance harmony = HarmonyInstance.Create("aswdebug.survivalbot");
            PatchInput(harmony);
            PatchGameHooks(harmony);
            FileLogger.Log("PATCH", "SurvivalBot hooks installed.");
        }

        private static void PatchInput(HarmonyInstance harmony)
        {
            Patch(harmony, AccessTools.Method(typeof(Input), "GetAxis", new Type[] { typeof(string) }), "InputAxisPrefix");
            Patch(harmony, AccessTools.Method(typeof(Input), "GetAxisRaw", new Type[] { typeof(string) }), "InputAxisPrefix");
            Patch(harmony, AccessTools.Method(typeof(Input), "GetButton", new Type[] { typeof(string) }), "InputButtonPrefix");
            Patch(harmony, AccessTools.Method(typeof(Input), "GetButtonDown", new Type[] { typeof(string) }), "InputButtonDownPrefix");
            Patch(harmony, AccessTools.Method(typeof(Input), "GetMouseButton", new Type[] { typeof(int) }), "InputMouseButtonPrefix");
            Patch(harmony, AccessTools.Method(typeof(Input), "GetMouseButtonDown", new Type[] { typeof(int) }), "InputMouseButtonDownPrefix");
            Patch(harmony, AccessTools.Method(typeof(Input), "GetKey", new Type[] { typeof(KeyCode) }), "InputKeyPrefix");
            Patch(harmony, AccessTools.Method(typeof(Input), "GetKey", new Type[] { typeof(string) }), "InputKeyStringPrefix");
            Patch(harmony, AccessTools.Method(typeof(Input), "GetKeyDown", new Type[] { typeof(KeyCode) }), "InputKeyDownPrefix");
            Patch(harmony, AccessTools.Method(typeof(Input), "GetKeyDown", new Type[] { typeof(string) }), "InputKeyDownStringPrefix");

            PropertyInfo anyKey = typeof(Input).GetProperty("anyKey", BindingFlags.Public | BindingFlags.Static);
            PropertyInfo anyKeyDown = typeof(Input).GetProperty("anyKeyDown", BindingFlags.Public | BindingFlags.Static);
            Patch(harmony, anyKey == null ? null : anyKey.GetGetMethod(), "InputAnyKeyPrefix");
            Patch(harmony, anyKeyDown == null ? null : anyKeyDown.GetGetMethod(), "InputAnyKeyDownPrefix");
        }

        private static void PatchGameHooks(HarmonyInstance harmony)
        {
            PatchByName(harmony, typeof(Level), "LoadMap", 3, "LevelLoadMapPrefix", null);
            PatchByName(harmony, typeof(ChannelConnection), "ParseCharacterInfo", 1, "CharacterInfoPrefix", null);
            PatchByName(harmony, typeof(ChannelConnection), "ParseGameEnd", 1, "GameEndPrefix", null);
            PatchByName(harmony, typeof(LobbyConnection), "ResponseMatching", 0, null, "MatchingAcceptedPostfix");
            PatchByName(harmony, typeof(LobbyConnection), "ResponseCancelMatching", 0, null, "MatchingCancelledPostfix");
            PatchByName(harmony, typeof(UITakeCardManager), "Refresh", 0, null, "CardRefreshPostfix");
        }

        private static void PatchByName(HarmonyInstance harmony, Type type, string name, int parameterCount, string prefix, string postfix)
        {
            MethodInfo selected = null;
            MethodInfo[] methods = type.GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            for (int i = 0; i < methods.Length; i++)
            {
                if (methods[i].Name == name && methods[i].GetParameters().Length == parameterCount)
                {
                    selected = methods[i];
                    break;
                }
            }

            Patch(harmony, selected, prefix, postfix);
        }

        private static void Patch(HarmonyInstance harmony, MethodBase original, string prefix)
        {
            Patch(harmony, original, prefix, null);
        }

        private static void Patch(HarmonyInstance harmony, MethodBase original, string prefix, string postfix)
        {
            if (original == null)
            {
                FileLogger.Log("PATCH", "target missing for prefix=" + prefix + " postfix=" + postfix);
                return;
            }

            MethodInfo prefixMethod = string.IsNullOrEmpty(prefix) ? null : AccessTools.Method(typeof(HarmonyLoader), prefix);
            MethodInfo postfixMethod = string.IsNullOrEmpty(postfix) ? null : AccessTools.Method(typeof(HarmonyLoader), postfix);
            harmony.Patch(original,
                prefixMethod == null ? null : new HarmonyMethod(prefixMethod),
                postfixMethod == null ? null : new HarmonyMethod(postfixMethod),
                null);
        }

        private static bool InputAxisPrefix(string axisName, ref float __result)
        {
            return !AutoBattleInput.TryGetAxis(axisName, ref __result);
        }

        private static bool InputButtonPrefix(string buttonName, ref bool __result)
        {
            return !AutoBattleInput.TryGetButton(buttonName, ref __result);
        }

        private static bool InputButtonDownPrefix(string buttonName, ref bool __result)
        {
            return !AutoBattleInput.TryGetButtonDown(buttonName, ref __result);
        }

        private static bool InputMouseButtonPrefix(int button, ref bool __result)
        {
            return !AutoBattleInput.TryGetMouseButton(button, ref __result);
        }

        private static bool InputMouseButtonDownPrefix(int button, ref bool __result)
        {
            return !AutoBattleInput.TryGetMouseButtonDown(button, ref __result);
        }

        private static bool InputKeyPrefix(KeyCode key, ref bool __result)
        {
            return !AutoBattleInput.TryGetKey(key, ref __result);
        }

        private static bool InputKeyStringPrefix(string name, ref bool __result)
        {
            KeyCode key;
            return !AutoBattleInput.TryParseKeyCode(name, out key) || !AutoBattleInput.TryGetKey(key, ref __result);
        }

        private static bool InputKeyDownPrefix(KeyCode key, ref bool __result)
        {
            return !AutoBattleInput.TryGetKeyDown(key, ref __result);
        }

        private static bool InputKeyDownStringPrefix(string name, ref bool __result)
        {
            KeyCode key;
            return !AutoBattleInput.TryParseKeyCode(name, out key) || !AutoBattleInput.TryGetKeyDown(key, ref __result);
        }

        private static bool InputAnyKeyPrefix(ref bool __result)
        {
            return !AutoBattleInput.TryAnyKey(ref __result);
        }

        private static bool InputAnyKeyDownPrefix(ref bool __result)
        {
            return !AutoBattleInput.TryAnyKeyDown(ref __result);
        }

        private static void LevelLoadMapPrefix(string name, ref bool load_navmesh)
        {
            AutoBattleRoutePlanner.PrepareNavigationLoad(name, ref load_navmesh);
        }

        private static void CharacterInfoPrefix(NetworkStream reader)
        {
            try
            {
                if (reader == null || reader.recv_buffer == null) return;
                int p = reader.read_position;
                if (p < 0 || p + 6 >= reader.read_end) return;

                byte remoteUid = reader.recv_buffer[p + 5];
                byte team = reader.recv_buffer[p + 6];
                Level level = ASSingleton<Level>.Instance;
                Character player = level == null ? null : level.GetPlayer();
                if (player != null && remoteUid != player.uid && team >= 2)
                    SurvivalBotManager.NotifyRemoteGmCandidate(remoteUid, team);
            }
            catch (Exception ex)
            {
                FileLogger.Log("GM", "character-info probe failed: " + ex.Message);
            }
        }

        private static void GameEndPrefix(NetworkStream reader)
        {
            try
            {
                if (reader != null && reader.recv_buffer != null && reader.read_position < reader.read_end)
                    SurvivalBotManager.NotifyFinalRank(reader.recv_buffer[reader.read_position]);
            }
            catch { }
        }

        private static void MatchingAcceptedPostfix()
        {
            SurvivalBotManager.NotifyMatchingAccepted();
        }

        private static void MatchingCancelledPostfix()
        {
            SurvivalBotManager.NotifyMatchingCancelled();
        }

        private static void CardRefreshPostfix(UITakeCardManager __instance)
        {
            SurvivalBotManager.NotifyCardRefresh(__instance);
        }
    }
}
