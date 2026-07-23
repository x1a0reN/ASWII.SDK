using System;
using System.Net;
using System.Net.Sockets;
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

        public static bool Install()
        {
            if (_installed) return true;
            NetworkRouteManager.PrepareClientRole();
            HarmonyInstance harmony = HarmonyInstance.Create("aswdebug.survivalbot");
            bool networkReady = PatchNetworking(harmony);
            if (!networkReady) NetworkRouteManager.ReportHookFailure();
            if (!networkReady && NetworkRouteManager.ProxyRequired) return false;
            NetworkRouteManager.Initialize();
            PatchInput(harmony);
            PatchGameHooks(harmony);
            _installed = true;
            FileLogger.Log("PATCH", "SurvivalBot hooks installed.");
            return true;
        }

        private static bool PatchNetworking(HarmonyInstance harmony)
        {
            MethodInfo socketConnect = AccessTools.Method(typeof(Socket), "Connect", new Type[] { typeof(string), typeof(int) });
            ConstructorInfo wwwConstructor = AccessTools.Constructor(typeof(WWW), new Type[] { typeof(string) });
            MethodInfo dnsEntry = AccessTools.Method(typeof(Dns), "GetHostEntry", new Type[] { typeof(string) });
            MethodInfo dnsAddresses = AccessTools.Method(typeof(Dns), "GetHostAddresses", new Type[] { typeof(string) });
            MethodInfo webRequestString = AccessTools.Method(typeof(WebRequest), "Create", new Type[] { typeof(string) });
            MethodInfo webRequestUri = AccessTools.Method(typeof(WebRequest), "Create", new Type[] { typeof(Uri) });
            if (socketConnect == null || wwwConstructor == null || dnsEntry == null || dnsAddresses == null ||
                webRequestString == null || webRequestUri == null)
            {
                FileLogger.Log("PATCH", "one or more network targets are missing");
                return false;
            }

            try
            {
                Patch(harmony, socketConnect, "SocketConnectPrefix");
                Patch(harmony, wwwConstructor, "WwwUrlPrefix");
                Patch(harmony, dnsEntry, "DnsGetHostEntryPrefix");
                Patch(harmony, dnsAddresses, "DnsGetHostAddressesPrefix");
                Patch(harmony, webRequestString, "WebRequestStringPrefix");
                Patch(harmony, webRequestUri, "WebRequestUriPrefix");
                return true;
            }
            catch (Exception ex)
            {
                FileLogger.Log("PATCH", "network hook installation failed: " + ex.GetType().Name);
                return false;
            }
        }

        private static void PatchInput(HarmonyInstance harmony)
        {
            int installed = 0;
            installed += TryPatch(harmony, AccessTools.Method(typeof(Input), "GetAxis", new Type[] { typeof(string) }), "InputAxisPrefix", null) ? 1 : 0;
            installed += TryPatch(harmony, AccessTools.Method(typeof(Input), "GetAxisRaw", new Type[] { typeof(string) }), "InputAxisPrefix", null) ? 1 : 0;
            installed += TryPatch(harmony, AccessTools.Method(typeof(Input), "GetButton", new Type[] { typeof(string) }), "InputButtonPrefix", null) ? 1 : 0;
            installed += TryPatch(harmony, AccessTools.Method(typeof(Input), "GetButtonDown", new Type[] { typeof(string) }), "InputButtonDownPrefix", null) ? 1 : 0;
            installed += TryPatch(harmony, AccessTools.Method(typeof(Input), "GetMouseButton", new Type[] { typeof(int) }), "InputMouseButtonPrefix", null) ? 1 : 0;
            installed += TryPatch(harmony, AccessTools.Method(typeof(Input), "GetMouseButtonDown", new Type[] { typeof(int) }), "InputMouseButtonDownPrefix", null) ? 1 : 0;
            installed += TryPatch(harmony, AccessTools.Method(typeof(Input), "GetKey", new Type[] { typeof(KeyCode) }), "InputKeyPrefix", null) ? 1 : 0;
            installed += TryPatch(harmony, AccessTools.Method(typeof(Input), "GetKey", new Type[] { typeof(string) }), "InputKeyStringPrefix", null) ? 1 : 0;
            installed += TryPatch(harmony, AccessTools.Method(typeof(Input), "GetKeyDown", new Type[] { typeof(KeyCode) }), "InputKeyDownPrefix", null) ? 1 : 0;
            installed += TryPatch(harmony, AccessTools.Method(typeof(Input), "GetKeyDown", new Type[] { typeof(string) }), "InputKeyDownStringPrefix", null) ? 1 : 0;

            PropertyInfo anyKey = typeof(Input).GetProperty("anyKey", BindingFlags.Public | BindingFlags.Static);
            PropertyInfo anyKeyDown = typeof(Input).GetProperty("anyKeyDown", BindingFlags.Public | BindingFlags.Static);
            installed += TryPatch(harmony, anyKey == null ? null : anyKey.GetGetMethod(), "InputAnyKeyPrefix", null) ? 1 : 0;
            installed += TryPatch(harmony, anyKeyDown == null ? null : anyKeyDown.GetGetMethod(), "InputAnyKeyDownPrefix", null) ? 1 : 0;
            FileLogger.Log("PATCH", "managed input hooks installed=" + installed + "; Unity internal-call targets are skipped");
        }

        private static void PatchGameHooks(HarmonyInstance harmony)
        {
            PatchByName(harmony, typeof(Level), "LoadMap", 3, "LevelLoadMapPrefix", "LevelLoadMapPostfix");
            PatchByName(harmony, typeof(Level), "OnExit", 0, "LevelExitPrefix", null);
            PatchByName(harmony, typeof(ChannelConnection), "ParseCharacterInfo", 1, "CharacterInfoPrefix", null);
            PatchByName(harmony, typeof(ChannelConnection), "ParseGameEnd", 1, "GameEndPrefix", null);
#if SURVIVAL_INTERNAL_TOOLS
            PatchByName(harmony, typeof(ChannelConnection), "SyncPlayerData", 1, "PlayerSyncPrefix", null);
            PatchByName(harmony, typeof(ChannelConnection), "Shoot", 6, "LocalTestShotPrefix", null);
#endif
            PatchByName(harmony, typeof(LobbyConnection), "RequestMatching", 2, "MatchingRequestedPrefix", null);
            PatchByName(harmony, typeof(LobbyConnection), "ResponseMatching", 0, null, "MatchingResponsePostfix");
            PatchByName(harmony, typeof(LobbyConnection), "ResponseCancelMatching", 0, null, "MatchingCancelResponsePostfix");
            PatchByName(harmony, typeof(UITakeCardManager), "Refresh", 0, null, "CardRefreshPostfix");
            PatchByName(harmony, typeof(UIJiesuan), "ShowSelf", 0, null, "BalanceShownPostfix");
            PatchByName(harmony, typeof(FightState), "Update", 1, "FightStateUpdatePrefix", null);
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

            TryPatch(harmony, selected, prefix, postfix);
        }

        private static bool TryPatch(HarmonyInstance harmony, MethodBase original, string prefix, string postfix)
        {
            if (original == null)
            {
                FileLogger.Log("PATCH", "target missing for prefix=" + prefix + " postfix=" + postfix);
                return false;
            }

            try
            {
                MethodInfo method = original as MethodInfo;
                if (method != null && method.GetMethodBody() == null)
                {
                    FileLogger.Log("PATCH", "target has no managed body; skipped: " + method.DeclaringType.FullName + "." + method.Name);
                    return false;
                }

                Patch(harmony, original, prefix, postfix);
                return true;
            }
            catch (Exception ex)
            {
                FileLogger.Log("PATCH", "hook skipped for prefix=" + prefix + " postfix=" + postfix + ": " + ex.GetType().Name + ": " + ex.Message);
                return false;
            }
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

        private static bool SocketConnectPrefix(Socket __instance, string host, int port)
        {
            return NetworkRouteManager.RouteSocketConnect(__instance, host, port);
        }

        private static void WwwUrlPrefix(ref string url)
        {
            url = NetworkRouteManager.RewriteWwwUrl(url);
        }

        private static bool DnsGetHostEntryPrefix(string hostNameOrAddress, ref IPHostEntry __result)
        {
            return NetworkRouteManager.RouteDnsGetHostEntry(hostNameOrAddress, ref __result);
        }

        private static bool DnsGetHostAddressesPrefix(string hostNameOrAddress, ref IPAddress[] __result)
        {
            return NetworkRouteManager.RouteDnsGetHostAddresses(hostNameOrAddress, ref __result);
        }

        private static void WebRequestStringPrefix(string requestUriString)
        {
            NetworkRouteManager.GuardWebRequest(requestUriString);
        }

        private static void WebRequestUriPrefix(Uri requestUri)
        {
            if (requestUri != null) NetworkRouteManager.GuardWebRequest(requestUri.AbsoluteUri);
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

        private static void FightStateUpdatePrefix(object __instance)
        {
            SurvivalAntiIdle.OnFightStateUpdate(__instance);
        }

        private static void LevelLoadMapPrefix(Level __instance, string name, ref bool load_navmesh,
            out bool __state)
        {
            __state = __instance != null && __instance.state == Level.State.kPrepare &&
                !string.IsNullOrEmpty(name);
            if (__state)
                AutoBattleRoutePlanner.PrepareNavigationLoad(name, ref load_navmesh);
        }

        private static void LevelLoadMapPostfix(string name, bool __result, bool __state)
        {
            if (!__state) return;
            if (!__result)
            {
                AutoBattleRoutePlanner.DeactivateNavigation("load_map_rejected");
                return;
            }
#if SURVIVAL_INTERNAL_TOOLS
            MapBakeSceneLoader.NotifyResolvedScene(name);
#endif
        }

        private static void LevelExitPrefix()
        {
            // Release graph ownership first. Auxiliary test cleanup must never prevent the game
            // from completing its native Level.OnExit path.
            try { AutoBattleRoutePlanner.DeactivateNavigationForSceneExit("level_exit"); }
            catch (Exception ex)
            {
                FileLogger.Log("PATCH", "level_exit_nav_release_ex=" + ex.GetType().Name + ":" + ex.Message);
            }
            try { SurvivalBotManager.NotifyLevelExit(); }
            catch (Exception ex)
            {
                FileLogger.Log("PATCH", "level_exit_survival_reset_ex=" + ex.GetType().Name + ":" + ex.Message);
            }
#if SURVIVAL_INTERNAL_TOOLS
            try { LocalNavigationCombatTest.NotifyLevelExit(); }
            catch (Exception ex)
            {
                FileLogger.Log("PATCH", "level_exit_local_test_ex=" + ex.GetType().Name + ":" + ex.Message);
            }
            try { MapBakeSceneLoader.NotifyLevelExit(); }
            catch (Exception ex)
            {
                FileLogger.Log("PATCH", "level_exit_map_loader_ex=" + ex.GetType().Name + ":" + ex.Message);
            }
#endif
        }

#if SURVIVAL_INTERNAL_TOOLS
        private static bool PlayerSyncPrefix()
        {
            return !MapBakeSceneLoader.DirectSceneActive;
        }

        private static bool LocalTestShotPrefix(HitMessage __2)
        {
            if (!LocalNavigationCombatTest.InterceptShots) return true;
            try { LocalNavigationCombatTest.TryHandleLocalShot(__2); }
            catch (Exception ex)
            {
                FileLogger.Log("AUTO-BATTLE][LEVEL33-TEST", "shot_prefix_ex=" + ex.GetType().Name + ":" + ex.Message);
            }
            // The direct test is network-isolated: misses stay misses, but no shot is forwarded.
            return false;
        }
#endif

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
                if (team >= 2 && (player == null || remoteUid != player.uid))
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

        private static void MatchingRequestedPrefix(byte game_mode)
        {
            SurvivalBotManager.NotifyMatchingRequested(game_mode);
        }

        private static void MatchingResponsePostfix()
        {
            bool accepted = false;
            try
            {
                NewUIRoom room = NewUIRoom.getInstance();
                accepted = room != null && room.InMatch;
            }
            catch { }
            SurvivalBotManager.NotifyMatchingResponse(accepted);
        }

        private static void MatchingCancelResponsePostfix()
        {
            if (!SurvivalBotManager.HasPendingSurvivalMatchRequest) return;
            try
            {
                NewUIRoom room = NewUIRoom.getInstance();
                if (room == null || !room.InMatch) SurvivalBotManager.NotifyMatchingCancelled();
            }
            catch { }
        }

        private static void CardRefreshPostfix(UITakeCardManager __instance)
        {
            SurvivalBotManager.NotifyCardRefresh(__instance);
        }

        private static void BalanceShownPostfix(UIJiesuan __instance)
        {
            SurvivalBotManager.NotifyBalanceShown(__instance);
        }
    }
}
