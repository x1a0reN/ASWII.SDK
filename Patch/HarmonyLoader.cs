// ASWDEBUG/HarmonyLoader.cs
using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using Harmony;
using ASWDEBUG.Logger;
using ASWDEBUG.UI;
using ASWDEBUG.Verify;
using UnityEngine;

namespace ASWDEBUG.Patch
{
    public static class HarmonyLoader
    {
        private static bool _installed;
        private static bool _patched;
        private static bool _telemetryOnly;
        private static int _positionCheckLogCount;
        private const int LongPayloadChars = 2048;
        private const int PreviewChars = 160;
        private const string RedirectHost = "127.0.0.1";
        private const int RedirectPort = 3100;
        private const string LocalAccountFileName = "local_account_id.txt";

        // 在你能保证会被调用的地方调用它（例如 ASWDEBUG 入口、你自己的初始化点）
        public static void Install()
        {
            Install(false);
        }

        public static void Install(bool telemetryOnly)
        {
            if (_installed) return;
            _installed = true;
            _telemetryOnly = telemetryOnly;

            try
            {

                // 先看 Assembly-CSharp 是否已在域里
                Assembly asmAC = AppDomain.CurrentDomain
                    .GetAssemblies()
                    .FirstOrDefault(a => a.GetName().Name == "Assembly-CSharp");

                if (asmAC != null)
                {
                    ApplyPatches();
                }
                else
                {
                    // 兜底：等它加载出来再打补丁
                    AppDomain.CurrentDomain.AssemblyLoad += OnAssemblyLoad;
                }
            }
            catch (Exception e)
            {
                FileLogger.Log("ASWDEBUG", "[HarmonyLoader] Install failed: " + e);
            }
        }

        private static void OnAssemblyLoad(object sender, AssemblyLoadEventArgs e)
        {
            try
            {
                if (e.LoadedAssembly != null &&
                    e.LoadedAssembly.GetName().Name == "Assembly-CSharp")
                {
                    AppDomain.CurrentDomain.AssemblyLoad -= OnAssemblyLoad;
                    ApplyPatches();
                }
            }
            catch (Exception ex)
            {
                FileLogger.Log("ASWDEBUG", "[HarmonyLoader] OnAssemblyLoad error: " + ex);
            }
        }

        private static void ApplyPatches()
        {
            if (_patched) return;
            _patched = true;

            try
            {
                var harmony = HarmonyInstance.Create("aswdebug.hooks");
                if (_telemetryOnly)
                {
                    ApplyTelemetryPatches(harmony);
                    FileLogger.Log("ASWDEBUG", "[HarmonyLoader] Telemetry-only patches applied.");
                }
                else
                {
                    // PatchAll 扫描“当前程序集”（也就是你的 ASWDEBUG.dll）里带 [HarmonyPatch] 的类
                    // harmony.PatchAll(Assembly.GetExecutingAssembly());
                    FileLogger.Log("ASWDEBUG", "[HarmonyLoader] PatchAll applied (all TargetMethods return null for bisect).");
                }
            }
            catch (Exception e)
            {
                FileLogger.Log("ASWDEBUG", "[HarmonyLoader] ApplyPatches failed: " + e);
            }
        }

        /// <summary>
        /// 二分法排查：手动逐批 patch GamePatches 里的类。
        /// 每次测试时注释/取消注释不同的行来定位问题补丁。
        /// </summary>
        private static void BisectPatchGameClasses(HarmonyInstance harmony)
        {
            try
            {
                var asm = Assembly.GetExecutingAssembly();
                // === 第一批（基础/UI 相关） ===
                PatchType(harmony, asm, "Patch_MessageBox_show_Prefix");
                PatchType(harmony, asm, "Patch_Character_UpdateBloodBar_Prefix");
                PatchType(harmony, asm, "FightState_Update_Patch");
                PatchType(harmony, asm, "Patch_LobbyConnection_AddTextRpc");
                PatchType(harmony, asm, "Patch_LobbyConnection_rpcCallBack");

                // === 第二批（战斗/射击相关） ===
                PatchType(harmony, asm, "Patch_Character_UpdateSyncData");
                PatchType(harmony, asm, "Patch_Character_Shoot_Prefix");
                PatchType(harmony, asm, "Patch_ChannelConnection_Shoot_Prefix");
                PatchType(harmony, asm, "Patch_ChannelConnection_GrenadeHurt_Prefix");
                PatchType(harmony, asm, "Patch_GunBaseController_FireCheck_Transpiler");
                PatchType(harmony, asm, "Patch_SniperGunController_FireCheck_Transpiler");
                PatchType(harmony, asm, "Patch_WeaponBase_Ready");

                // === 第三批（其他） ===
                PatchType(harmony, asm, "Patch_Character_SetLookDir_Prefix");
                PatchType(harmony, asm, "Patch_UITakeCardManager_ref_Prefix");
                PatchType(harmony, asm, "Patch_Input_GetKey_String_Prefix");
                PatchType(harmony, asm, "Patch_distance_Prefix");
                PatchType(harmony, asm, "Patch_BaseController_InputUpdate_Transpile");
                PatchType(harmony, asm, "Patch_LoginState_Login_LocalRedirect");

                // === 第四批（之前遗漏的） ===
                PatchType(harmony, asm, "Patch_KnifeBaseController_Ready");
                PatchType(harmony, asm, "BNR_IL2");
                PatchType(harmony, asm, "BNR_IL");
                PatchType(harmony, asm, "Patch_Input_GetKey_KeyCode_Prefix");
                PatchType(harmony, asm, "Patch_ChannelConnection_SyncPlayerData_SpinLocal");
                PatchType(harmony, asm, "Patch_Boss1_Prefix");
                PatchType(harmony, asm, "Patch_Boss2_Prefix");
                PatchType(harmony, asm, "Patch_Boss3_Prefix");
                PatchType(harmony, asm, "Patch_Boss4_Prefix");
                PatchType(harmony, asm, "Patch_Boss5_Prefix");
                PatchType(harmony, asm, "Patch_ParseSyncCharacterData_Prefix");
                PatchType(harmony, asm, "Patch_UICreateCharacter_DealRoleList_Prefix");
                PatchType(harmony, asm, "Patch_LuaGod_DoString_FilterInfiniteWhile");
                PatchType(harmony, asm, "Patch_LuaState_L_DoString_FilterInfiniteWhile");
                PatchType(harmony, asm, "Patch_ParseChannelInfo_AlwaysBypass");
                PatchType(harmony, asm, "Patch_UICreateCharacter_FinishBtn_Prefix");
                PatchType(harmony, asm, "Patch_AvatarLuaParse_SkinInit_Prefix");
                PatchType(harmony, asm, "Patch_UIRankingListItem_ParseLuaString_Prefix");

                FileLogger.Log("ASWDEBUG", "[BisectPatch] Batch 1 applied.");
            }
            catch (Exception e)
            {
                FileLogger.Log("ASWDEBUG", "[BisectPatch] Error: " + e);
            }
        }

        private static void PatchType(HarmonyInstance harmony, Assembly asm, string typeName)
        {
            try
            {
                Type t = asm.GetTypes().FirstOrDefault(x => x.Name == typeName);
                if (t == null)
                {
                    FileLogger.Log("ASWDEBUG", "[BisectPatch] Type not found: " + typeName);
                    return;
                }
                // Harmony 1.x: 手动获取 TargetMethod + Prefix/Postfix/Transpiler 并 patch
                var targetMethodInfo = AccessTools.Method(t, "TargetMethod");
                if (targetMethodInfo == null)
                {
                    FileLogger.Log("ASWDEBUG", "[BisectPatch] No TargetMethod in: " + typeName);
                    return;
                }
                var original = targetMethodInfo.Invoke(null, null) as MethodBase;
                if (original == null)
                {
                    FileLogger.Log("ASWDEBUG", "[BisectPatch] TargetMethod returned null: " + typeName);
                    return;
                }

                var prefix = AccessTools.Method(t, "Prefix");
                var postfix = AccessTools.Method(t, "Postfix");
                var transpiler = AccessTools.Method(t, "Transpiler");

                harmony.Patch(original,
                    prefix != null ? new HarmonyMethod(prefix) : null,
                    postfix != null ? new HarmonyMethod(postfix) : null,
                    transpiler != null ? new HarmonyMethod(transpiler) : null);

                FileLogger.Log("ASWDEBUG", "[BisectPatch] OK: " + typeName + " -> " + original.DeclaringType.Name + "." + original.Name);
            }
            catch (Exception e)
            {
                FileLogger.Log("ASWDEBUG", "[BisectPatch] FAIL " + typeName + ": " + e.Message);
            }
        }

        private static void ApplyTelemetryPatches(HarmonyInstance harmony)
        {
            Assembly asm = AppDomain.CurrentDomain.GetAssemblies()
                .FirstOrDefault(a => a.GetName().Name == "Assembly-CSharp");
            if (asm == null)
            {
                FileLogger.Log("ASWDEBUG", "[HarmonyLoader] Assembly-CSharp not found for telemetry patches.");
                return;
            }

            TryPatch(harmony, asm, "LobbyConnection", "AddTextRpc",
                new Type[] { typeof(string), typeof(global::LobbyConnection.RpcCallback), typeof(Dictionary<string, string>) },
                "Telemetry_AddTextRpcPrefix");

            TryPatch(harmony, asm, "LobbyConnection", "rpcCallBack",
                new Type[] { typeof(string) },
                "Telemetry_RpcCallBackPrefix");

            TryPatch(harmony, asm, "LobbyConnection", "ReqeustGMCommand",
                new Type[] { typeof(string) },
                "Telemetry_RequestGmPrefix");

            TryPatch(harmony, asm, "LobbyConnection", "BeginTextRpc",
                new Type[] { typeof(string), typeof(global::LobbyConnection.RpcCallback) },
                "Telemetry_BeginTextRpcPrefix");

            TryPatch(harmony, asm, "LobbyConnection", "runRpcRequest",
                Type.EmptyTypes,
                "Telemetry_RunRpcRequestPrefix");

            TryPatch(harmony, asm, "LobbyConnection", "RequestReport",
                new Type[] { typeof(byte), typeof(string), typeof(string), typeof(int), typeof(byte[]) },
                "Telemetry_RequestReportPrefix");

            TryPatch(harmony, asm, "LobbyConnection", "RequestShutdown",
                new Type[] { typeof(string) },
                "Telemetry_RequestShutdownPrefix");

            TryPatch(harmony, asm, "LobbyConnection", "RequestTengxunZhuanFa",
                new Type[] { typeof(global::LobbyConnection.TengxunApi) },
                "Telemetry_RequestTengxunPrefix");

            TryPatch(harmony, asm, "ChannelConnection", "RequestRoomEnter",
                new Type[] { typeof(int), typeof(string), typeof(uint) },
                "Telemetry_RequestRoomEnterPrefix");

            TryPatch(harmony, asm, "ChannelConnection", "RequestGameStart",
                Type.EmptyTypes,
                "Telemetry_RequestGameStartPrefix");

            TryPatch(harmony, asm, "ChannelConnection", "RequestGameEnter",
                Type.EmptyTypes,
                "Telemetry_RequestGameEnterPrefix");

            TryPatch(harmony, asm, "ChannelConnection", "RequestVoteBegin",
                new Type[] { typeof(byte), typeof(string) },
                "Telemetry_RequestVoteBeginPrefix");

            TryPatch(harmony, asm, "ChannelConnection", "RequestVote",
                new Type[] { typeof(bool) },
                "Telemetry_RequestVotePrefix");

            TryPatch(harmony, asm, "ChannelConnection", "RequestGMKickClient",
                new Type[] { typeof(byte) },
                "Telemetry_RequestGMKickPrefix");

            TryPatch(harmony, asm, "ChannelConnection", "RequestRevive",
                Type.EmptyTypes,
                "Telemetry_RequestRevivePrefix");

            TryPatch(harmony, asm, "ChannelConnection", "Use",
                new Type[] { typeof(byte), typeof(int), typeof(byte) },
                "Telemetry_UsePrefix");

            TryPatch(harmony, asm, "ChannelConnection", "Shoot",
                new Type[] { typeof(Vector3), typeof(Vector3), typeof(global::HitMessage), typeof(byte), typeof(bool), typeof(Vector3) },
                "Telemetry_ShootPrefix");

            TryPatch(harmony, asm, "ChannelConnection", "PluginReport",
                new Type[] { typeof(byte), typeof(global::AssitToolType), typeof(bool) },
                "Telemetry_PluginReportPrefix");

            TryPatch(harmony, asm, "ChannelConnection", "ParsePositionCheck",
                Type.EmptyTypes,
                "Telemetry_ParsePositionCheckPrefix");

            TryPatch(harmony, asm, "NetworkStream", "WriteString",
                new Type[] { typeof(string) },
                "Telemetry_WriteStringPrefix");

            TryPatch(harmony, asm, "NetworkStream", "WriteCompressString",
                new Type[] { typeof(string) },
                "Telemetry_WriteCompressStringPrefix");

            TryPatch(harmony, asm, "LoginState", "Login",
                new Type[] { typeof(string), typeof(string) },
                "Telemetry_LoginRedirectPrefix");

            TryPatch(harmony, asm, "GlobalStatic", "CheckEquipment",
                Type.EmptyTypes,
                "Telemetry_CheckEquipmentPrefix");

            TryPatch(harmony, asm, "UIEventWin", "SortLuaByFilter",
                new Type[] { typeof(UniLua.LuaTable), typeof(string) },
                "Telemetry_SortLuaByFilterPrefix");

            TryPatch(harmony, asm, "UIPlayerWin", "InitBagPage",
                Type.EmptyTypes,
                "Telemetry_InitBagPagePrefix");
        }

        private static void TryPatch(HarmonyInstance harmony, Assembly asm, string typeName, string methodName, Type[] argTypes, string prefixMethodName)
        {
            try
            {
                Type t = asm.GetType(typeName);
                if (t == null)
                {
                    FileLogger.Log("ASWDEBUG", "[HarmonyLoader] type not found: " + typeName);
                    return;
                }

                MethodInfo original = AccessTools.Method(t, methodName, argTypes);
                if (original == null)
                {
                    FileLogger.Log("ASWDEBUG", "[HarmonyLoader] method not found: " + typeName + "." + methodName);
                    return;
                }

                MethodInfo prefix = AccessTools.Method(typeof(HarmonyLoader), prefixMethodName);
                if (prefix == null)
                {
                    FileLogger.Log("ASWDEBUG", "[HarmonyLoader] prefix not found: " + prefixMethodName);
                    return;
                }

                harmony.Patch(original, new HarmonyMethod(prefix), null, null);
                FileLogger.Log("ASWDEBUG", "[HarmonyLoader] patched: " + typeName + "." + methodName);
            }
            catch (Exception ex)
            {
                FileLogger.Log("ASWDEBUG", "[HarmonyLoader] patch failed: " + typeName + "." + methodName + " => " + ex);
            }
        }

        private static void Telemetry_AddTextRpcPrefix(ref string func, ref global::LobbyConnection.RpcCallback callback, ref Dictionary<string, string> argument)
        {
            try
            {
                RpcLabUI.OnBeforeAddTextRpc(null, ref func, ref callback, ref argument);

                string caller = CaptureCaller();
                int funcLen = func == null ? 0 : func.Length;
                int argCount;
                int keyChars;
                int valChars;
                CollectDictStats(argument, out argCount, out keyChars, out valChars);

                FileLogger.Log("NET-AUDIT",
                    "[RPC-REQ] func=" + (func ?? "<null>") +
                    " funcLen=" + funcLen +
                    " argCount=" + argCount +
                    " keyChars=" + keyChars +
                    " valChars=" + valChars +
                    " caller=" + caller);

                if (funcLen >= LongPayloadChars || valChars >= LongPayloadChars)
                {
                    FileLogger.Log("NET-AUDIT", "[RPC-REQ-LONG] func=" + TrimLog(func, PreviewChars) + " args=" + DictToLog(argument));
                }
            }
            catch (Exception ex)
            {
                FileLogger.Log("NET-AUDIT", "[RPC-REQ] log error: " + ex.Message);
            }
        }

        private static void Telemetry_RpcCallBackPrefix(string data)
        {
            try
            {
                FileLogger.Log("NET-AUDIT", "[RPC-RSP] len=" + (data == null ? 0 : data.Length) + " data=" + TrimLog(data, 400));
            }
            catch (Exception ex)
            {
                FileLogger.Log("NET-AUDIT", "[RPC-RSP] log error: " + ex.Message);
            }
        }

        private static void Telemetry_RequestGmPrefix(string command)
        {
            try
            {
                int len = command == null ? 0 : command.Length;
                FileLogger.Log("NET-AUDIT", "[GM-REQ] len=" + len + " command=" + TrimLog(command, PreviewChars));
            }
            catch (Exception ex)
            {
                FileLogger.Log("NET-AUDIT", "[GM-REQ] log error: " + ex.Message);
            }
        }

        private static void Telemetry_BeginTextRpcPrefix(string func, global::LobbyConnection.RpcCallback callback)
        {
            try
            {
                RpcLabUI.OnBeginTextRpc(func, callback);
                int len = func == null ? 0 : func.Length;
                FileLogger.Log("NET-AUDIT", "[RPC-BEGIN] funcLen=" + len + " func=" + TrimLog(func, PreviewChars));
            }
            catch (Exception ex)
            {
                FileLogger.Log("NET-AUDIT", "[RPC-BEGIN] log error: " + ex.Message);
            }
        }

        private static void Telemetry_RunRpcRequestPrefix(object __instance)
        {
            try
            {
                if (__instance == null) return;

                object req = Traverse.Create(__instance).Field("rpcRequest").GetValue();
                if (req == null)
                {
                    FileLogger.Log("NET-AUDIT", "[RPC-RUN] rpcRequest=<null>");
                    return;
                }

                string func = Traverse.Create(req).Field("func").GetValue<string>();
                object argObj = Traverse.Create(req).Field("argument").GetValue();
                IDictionary dict = argObj as IDictionary;

                int argCount = 0;
                int rawChars = 0;
                if (dict != null)
                {
                    argCount = dict.Count;
                    foreach (DictionaryEntry entry in dict)
                    {
                        string k = entry.Key == null ? string.Empty : entry.Key.ToString();
                        string v = entry.Value == null ? string.Empty : entry.Value.ToString();
                        rawChars += k.Length + 1 + v.Length + 1; // key\nvalue\n
                    }
                }

                FileLogger.Log("NET-AUDIT",
                    "[RPC-RUN] func=" + (func ?? "<null>") +
                    " funcLen=" + (func == null ? 0 : func.Length) +
                    " argCount=" + argCount +
                    " rawChars=" + rawChars);
            }
            catch (Exception ex)
            {
                FileLogger.Log("NET-AUDIT", "[RPC-RUN] log error: " + ex.Message);
            }
        }

        private static void Telemetry_RequestReportPrefix(byte report_type, string target, string message, int size, byte[] data)
        {
            try
            {
                int targetLen = target == null ? 0 : target.Length;
                int messageLen = message == null ? 0 : message.Length;
                int dataLen = data == null ? 0 : data.Length;
                FileLogger.Log("NET-AUDIT",
                    "[REPORT] type=" + report_type +
                    " targetLen=" + targetLen +
                    " messageLen=" + messageLen +
                    " size=" + size +
                    " dataLen=" + dataLen);
            }
            catch (Exception ex)
            {
                FileLogger.Log("NET-AUDIT", "[REPORT] log error: " + ex.Message);
            }
        }

        private static void Telemetry_RequestShutdownPrefix(string password)
        {
            try
            {
                FileLogger.Log("NET-AUDIT", "[SHUTDOWN-REQ] passwordLen=" + SafeLen(password) + " preview=" + TrimLog(password, PreviewChars));
            }
            catch (Exception ex)
            {
                FileLogger.Log("NET-AUDIT", "[SHUTDOWN-REQ] log error: " + ex.Message);
            }
        }

        private static void Telemetry_RequestTengxunPrefix(global::LobbyConnection.TengxunApi api)
        {
            try
            {
                FileLogger.Log("NET-AUDIT",
                    "[TX-REQ] api=" + api +
                    " appid=" + TrimLog(global::GlobalStatic.appid, 48) +
                    " openidLen=" + SafeLen(global::GlobalStatic.openid) +
                    " openkeyLen=" + SafeLen(global::GlobalStatic.openkey));
            }
            catch (Exception ex)
            {
                FileLogger.Log("NET-AUDIT", "[TX-REQ] log error: " + ex.Message);
            }
        }

        private static void Telemetry_RequestRoomEnterPrefix(int room_id, string password, uint token)
        {
            try
            {
                FileLogger.Log("NET-AUDIT", "[ROOM-ENTER] roomId=" + room_id + " token=" + token + " passwordLen=" + SafeLen(password));
            }
            catch (Exception ex)
            {
                FileLogger.Log("NET-AUDIT", "[ROOM-ENTER] log error: " + ex.Message);
            }
        }

        private static void Telemetry_RequestGameStartPrefix()
        {
            FileLogger.Log("NET-AUDIT", "[ROOM-GAMESTART] request");
        }

        private static void Telemetry_RequestGameEnterPrefix()
        {
            FileLogger.Log("NET-AUDIT", "[ROOM-GAMEENTER] request");
        }

        private static void Telemetry_RequestVoteBeginPrefix(byte target_uid, string reason)
        {
            try
            {
                FileLogger.Log("NET-AUDIT", "[VOTE-BEGIN] uid=" + target_uid + " reasonLen=" + SafeLen(reason) + " reason=" + TrimLog(reason, PreviewChars));
            }
            catch (Exception ex)
            {
                FileLogger.Log("NET-AUDIT", "[VOTE-BEGIN] log error: " + ex.Message);
            }
        }

        private static void Telemetry_RequestVotePrefix(bool agree)
        {
            FileLogger.Log("NET-AUDIT", "[VOTE] agree=" + agree);
        }

        private static void Telemetry_RequestGMKickPrefix(byte target_uid)
        {
            FileLogger.Log("NET-AUDIT", "[GM-KICK] uid=" + target_uid);
        }

        private static void Telemetry_RequestRevivePrefix()
        {
            FileLogger.Log("NET-AUDIT", "[REVIVE] request");
        }

        private static void Telemetry_UsePrefix(byte is_real_man, int robot_uid, byte slot)
        {
            try
            {
                FileLogger.Log("NET-AUDIT", "[USE] is_real_man=" + is_real_man + " robot_uid=" + robot_uid + " slot=" + slot);
            }
            catch (Exception ex)
            {
                FileLogger.Log("NET-AUDIT", "[USE] log error: " + ex.Message);
            }
        }

        private static void Telemetry_ShootPrefix(Vector3 position, Vector3 direction, global::HitMessage hit_message, byte slot, bool do_effect, Vector3 velocity)
        {
            try
            {
                int uid = (hit_message != null) ? hit_message.uid : 0;
                int part = (hit_message != null) ? hit_message.part : 0;
                int enc = (hit_message != null) ? hit_message.enc : 0;
                int sight = (hit_message != null) ? hit_message.current_sight : 0;
                int robotUid = (hit_message != null) ? hit_message.robot_uid : 0;
                int isRealMan = (hit_message != null) ? hit_message.is_real_man : 0;
                int distance = (hit_message != null) ? hit_message.distance : 0;
                float spread = (hit_message != null) ? hit_message.spread : 0f;
                FileLogger.Log("NET-AUDIT",
                    "[SHOOT] slot=" + slot +
                    " target=" + uid +
                    " part=" + part +
                    " dist=" + distance +
                    " enc=" + enc +
                    " sight=" + sight +
                    " is_real_man=" + isRealMan +
                    " robot_uid=" + robotUid +
                    " spread=" + spread +
                    " doEffect=" + do_effect);
            }
            catch (Exception ex)
            {
                FileLogger.Log("NET-AUDIT", "[SHOOT] log error: " + ex.Message);
            }
        }

        private static void Telemetry_PluginReportPrefix(byte uid, global::AssitToolType type, bool check_ok)
        {
            try
            {
                FileLogger.Log("NET-AUDIT", "[PLUGIN-REPORT] uid=" + uid + " type=" + type + " check_ok=" + check_ok);
            }
            catch (Exception ex)
            {
                FileLogger.Log("NET-AUDIT", "[PLUGIN-REPORT] log error: " + ex.Message);
            }
        }

        private static void Telemetry_WriteStringPrefix(string str)
        {
            try
            {
                int len = str == null ? 0 : str.Length;
                if (len < LongPayloadChars) return;
                FileLogger.Log("NET-AUDIT", "[NET-WRITE-STR] len=" + len + " preview=" + TrimLog(str, PreviewChars));
            }
            catch (Exception ex)
            {
                FileLogger.Log("NET-AUDIT", "[NET-WRITE-STR] log error: " + ex.Message);
            }
        }

        private static void Telemetry_WriteCompressStringPrefix(ref string str)
        {
            try
            {
                RpcLabUI.OnBeforeWriteCompressString(ref str);
                int len = str == null ? 0 : str.Length;
                if (len < LongPayloadChars) return;
                FileLogger.Log("NET-AUDIT", "[NET-WRITE-COMP] len=" + len + " preview=" + TrimLog(str, PreviewChars));
            }
            catch (Exception ex)
            {
                FileLogger.Log("NET-AUDIT", "[NET-WRITE-COMP] log error: " + ex.Message);
            }
        }

        private static bool Telemetry_LoginRedirectPrefix(string name, string password)
        {
            // [已注释] 连接服务器重定向补丁 - 不再劫持登录到本地服务器
            return true; // 放行原方法，走正常登录流程
            /*
            try
            {
                if (global::GameApp.Instance == null)
                {
                    FileLogger.Log("NET-AUDIT", "[LOGIN-REDIRECT] GameApp.Instance == null");
                    return true;
                }

                try
                {
                    if (global::GameApp.Instance.lobby_connection != null)
                    {
                        global::GameApp.Instance.lobby_connection.Disconnect();
                    }
                }
                catch (Exception disconnectEx)
                {
                    FileLogger.Log("NET-AUDIT", "[LOGIN-REDIRECT] disconnect skipped: " + disconnectEx.Message);
                }

                string localAccountKey = GetOrCreateLocalAccountKey();

                global::StartConfig.platform = 0;
                global::GameApp.Instance.lobby_connection = new global::LobbyConnection();
                global::GameApp.Instance.lobby_connection.login_name = localAccountKey;
                global::GameApp.Instance.lobby_connection.login_pass = string.Empty;
                global::GameApp.Instance.lobby_connection.real_login_ip = RedirectHost;

                FileLogger.Log("NET-AUDIT",
                    "[LOGIN-REDIRECT] force connect " + RedirectHost + ":" + RedirectPort +
                    " user=" + localAccountKey +
                    " input=" + (name ?? string.Empty));

                global::GameApp.Instance.lobby_connection.Connect(RedirectHost, RedirectPort);
                global::GameApp.Instance.error_message = "connect_failed";
                return false;
            }
            catch (Exception ex)
            {
                FileLogger.Log("NET-AUDIT", "[LOGIN-REDIRECT] prefix error: " + ex);
                return true;
            }
            */
        }

        private static string GetOrCreateLocalAccountKey()
        {
            try
            {
                string root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ASWII");
                if (!Directory.Exists(root))
                {
                    Directory.CreateDirectory(root);
                }

                string path = Path.Combine(root, LocalAccountFileName);
                string value = string.Empty;
                if (File.Exists(path))
                {
                    value = (File.ReadAllText(path) ?? string.Empty).Trim();
                }

                if (string.IsNullOrEmpty(value))
                {
                    value = "local_" + Guid.NewGuid().ToString("N");
                    File.WriteAllText(path, value);
                }

                return value;
            }
            catch (Exception ex)
            {
                FileLogger.Log("NET-AUDIT", "[LOGIN-REDIRECT] local account fallback: " + ex.Message);
                return "local_fallback";
            }
        }

        private static bool Telemetry_CheckEquipmentPrefix(ref bool __result)
        {
            // [已禁用] 不再 bypass 装备校验，放行原方法避免服务器检测
            return true;
            /*
            try
            {
                __result = true;
                FileLogger.Log("NET-AUDIT", "[CHECK-EQUIPMENT] bypass=true");
                return false;
            }
            catch (Exception ex)
            {
                FileLogger.Log("NET-AUDIT", "[CHECK-EQUIPMENT] prefix error: " + ex.Message);
                return true;
            }
            */
        }

        private static bool Telemetry_SortLuaByFilterPrefix(ref List<UniLua.LuaTable> __result, UniLua.LuaTable tables, string filter)
        {
            try
            {
                if (tables == null)
                {
                    __result = new List<UniLua.LuaTable>();
                    FileLogger.Log("NET-AUDIT", "[EVENT-FILTER] null table -> empty list");
                    return false;
                }
            }
            catch (Exception ex)
            {
                FileLogger.Log("NET-AUDIT", "[EVENT-FILTER] prefix error: " + ex.Message);
            }
            return true;
        }

        private static void Telemetry_InitBagPagePrefix(global::UIPlayerWin __instance)
        {
            try
            {
                if (__instance == null)
                {
                    return;
                }

                Traverse traverse = Traverse.Create(__instance);
                traverse.Field("storageShowPage").SetValue(global::UIPlayerWin.PlayerEquipShowEnum.equip);
                traverse.Field("storageItemPage").SetValue(1);
                traverse.Field("needreRreshpages").SetValue(true);
                traverse.Field("useFilter").SetValue(false);
            }
            catch (Exception ex)
            {
                FileLogger.Log("NET-AUDIT", "[INIT-BAG-PAGE] prefix error: " + ex.Message);
            }
        }

        private static void Telemetry_ParsePositionCheckPrefix()
        {
            if (_positionCheckLogCount >= 20) return;
            _positionCheckLogCount++;
            FileLogger.Log("NET-AUDIT", "[CH-ANTICHEAT] ParsePositionCheck triggered #" + _positionCheckLogCount);
        }

        private static string DictToLog(Dictionary<string, string> dict)
        {
            if (dict == null || dict.Count == 0) return "{}";
            return string.Join(", ", dict.Select(kv => SafeKey(kv.Key) + "=" + TrimLog(kv.Value, PreviewChars)).ToArray());
        }

        private static void CollectDictStats(Dictionary<string, string> dict, out int count, out int keyChars, out int valueChars)
        {
            count = 0;
            keyChars = 0;
            valueChars = 0;
            if (dict == null || dict.Count == 0) return;

            count = dict.Count;
            foreach (KeyValuePair<string, string> kv in dict)
            {
                keyChars += kv.Key == null ? 0 : kv.Key.Length;
                valueChars += kv.Value == null ? 0 : kv.Value.Length;
            }
        }

        private static string SafeKey(string key)
        {
            return key ?? "<null>";
        }

        private static int SafeLen(string text)
        {
            return text == null ? 0 : text.Length;
        }

        private static string TrimLog(string text, int maxLen)
        {
            if (string.IsNullOrEmpty(text)) return "<empty>";
            if (text.Length <= maxLen) return text.Replace('\n', ' ').Replace('\r', ' ');
            return text.Substring(0, maxLen).Replace('\n', ' ').Replace('\r', ' ') + "...";
        }

        private static string CaptureCaller()
        {
            try
            {
                StackTrace st = new StackTrace(2, false);
                for (int i = 0; i < st.FrameCount; i++)
                {
                    MethodBase m = st.GetFrame(i).GetMethod();
                    if (m == null) continue;
                    Type dt = m.DeclaringType;
                    string tn = dt != null ? dt.FullName : string.Empty;
                    if (string.IsNullOrEmpty(tn)) continue;
                    if (tn.StartsWith("System.", StringComparison.Ordinal)) continue;
                    if (tn.StartsWith("UnityEngine.", StringComparison.Ordinal)) continue;
                    if (tn.StartsWith("Harmony", StringComparison.Ordinal)) continue;
                    if (tn.StartsWith("ASWDEBUG.", StringComparison.Ordinal)) continue;
                    if (m.Name.IndexOf("_Patch", StringComparison.Ordinal) >= 0) continue;
                    return tn + "." + m.Name;
                }
            }
            catch
            {
            }
            return "(unknown)";
        }
    }
}
