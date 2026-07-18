// ASWDEBUG/HarmonyLoader.cs
using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Harmony;
using ASWDEBUG.Global;
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
        private static int _processCheckSuppressCount;
        private static int _pluginReportSuppressCount;
        private static int _requestReportSuppressCount;
        private static int _forceDisconnectSuppressCount;
        private static int _actkSuppressCount;
        private static string _multiOpenIdentityHash;
        private static string _multiOpenAswcPath;
        private static string _multiOpenAswcPathHash;
        private static int _multiOpenUcLogCount;
        private static int _multiOpenLauncherBlockCount;
        private static int _multiOpenRoomKickCount;
        private static int _multiOpenLastErrorCode;
        private static int _multiOpenLauncherMessageLogCount;
        private static int _multiOpenLauncherUpdateLogCount;
        private static int _multiOpenLauncherCloseLogCount;
        private static int _multiOpenApplicationQuitLogCount;
        private static int _multiOpenExitOnCheatLogCount;
        private static readonly object MultiOpenProxySync = new object();
        private static bool _multiOpenProxyInitialized;
        private static bool _multiOpenProxyDirect = true;
        private static string _multiOpenProxyHost = string.Empty;
        private static int _multiOpenProxyPort;
        private static string _multiOpenProxyUser = string.Empty;
        private static string _multiOpenProxyPassword = string.Empty;
        private static int _multiOpenProxySlot = -1;
        private static FileStream _multiOpenProxySlotLock;
        private static int _aimAssistReportBypassCount;
        private static int _aimAssistSampleBypassCount;
        private static int _aimAssistPayloadSanitizeCount;
        private static int _aimAssistConfigBypassCount;
        private static int _aimSyntheticSessionId;
        private static int _aimSyntheticTargetUid;
        private static float _aimSyntheticSessionStart;
        private static float _aimSyntheticSessionEnd;
        private static uint _aimSyntheticPrng = 0x6D2B79F5u;
        private static int _assistToolCheckSuppressCount;
        private static int _extendedDetectorSuppressCount;
        private static int _clientFileMd5LogSuppressCount;
        private const int ExitStackPreviewChars = 900;
        private static readonly HashSet<string> ExcludedFeaturePatchTypeNames = new HashSet<string>(StringComparer.Ordinal)
        {
            "Patch_LobbyConnection_AddTextRpc",
            "Patch_LobbyConnection_rpcCallBack",
            "Patch_WeaponBase_Ready",
            "Patch_KnifeBaseController_Ready"
        };
        private const int LongPayloadChars = 2048;
        private const int PreviewChars = 160;
        private const string RedirectHost = "127.0.0.1";
        private const int RedirectPort = 3100;
        private const string LocalAccountFileName = "local_account_id.txt";
        private const string MultiOpenProxyConfigFileName = "ASWDEBUG.MultiOpen.proxies.ini";

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
                ApplyCoreProtectionPatches(harmony);
                if (_telemetryOnly)
                {
                    ApplyTelemetryPatches(harmony);
                    FileLogger.Log("ASWDEBUG", "[HarmonyLoader] Core protection + telemetry patches applied.");
                }
                else
                {
                    BisectPatchGameClasses(harmony);
                    FileLogger.Log("ASWDEBUG", "[HarmonyLoader] Core protection + feature patches applied.");
                }
            }
            catch (Exception e)
            {
                FileLogger.Log("ASWDEBUG", "[HarmonyLoader] ApplyPatches failed: " + e);
            }
        }

        /// <summary>
        /// 自动发现并启用全部有效的功能 patch。
        /// 只接受真正实现了 TargetMethod + Prefix/Postfix/Transpiler 的补丁类型，
        /// 避免手抄白名单漏掉功能或把辅助类也算进去。
        /// </summary>
        private static void BisectPatchGameClasses(HarmonyInstance harmony)
        {
            try
            {
                var asm = Assembly.GetExecutingAssembly();
                Type[] patchTypes = DiscoverFeaturePatchTypes(asm);
                FileLogger.Log("ASWDEBUG", "[BisectPatch] discovered feature patch types: " + patchTypes.Length);

                foreach (Type patchType in patchTypes)
                {
                    PatchType(harmony, patchType);
                }

                FileLogger.Log("ASWDEBUG", "[BisectPatch] Feature patch sweep applied. count=" + patchTypes.Length);
            }
            catch (Exception e)
            {
                FileLogger.Log("ASWDEBUG", "[BisectPatch] Error: " + e);
            }
        }

        private static Type[] DiscoverFeaturePatchTypes(Assembly asm)
        {
            return asm.GetTypes()
                .Where(IsFeaturePatchType)
                .OrderBy(t => t.MetadataToken)
                .ToArray();
        }

        private static bool IsFeaturePatchType(Type t)
        {
            if (t == null || !t.IsClass)
            {
                return false;
            }

            if (!(t.Name.StartsWith("Patch_", StringComparison.Ordinal) ||
                  t.Name.StartsWith("BNR_", StringComparison.Ordinal) ||
                  t.Name.StartsWith("FightState_", StringComparison.Ordinal)))
            {
                return false;
            }

            if (ExcludedFeaturePatchTypeNames.Contains(t.Name))
            {
                return false;
            }

            MethodInfo targetMethodInfo = AccessTools.Method(t, "TargetMethod");
            if (targetMethodInfo == null || !targetMethodInfo.IsStatic)
            {
                return false;
            }

            return AccessTools.Method(t, "Prefix") != null ||
                   AccessTools.Method(t, "Postfix") != null ||
                   AccessTools.Method(t, "Transpiler") != null;
        }

        private static void PatchType(HarmonyInstance harmony, Type t)
        {
            try
            {
                // Harmony 1.x: 手动获取 TargetMethod + Prefix/Postfix/Transpiler 并 patch
                var targetMethodInfo = AccessTools.Method(t, "TargetMethod");
                if (targetMethodInfo == null)
                {
                    FileLogger.Log("ASWDEBUG", "[BisectPatch] No TargetMethod in: " + t.Name);
                    return;
                }
                var original = targetMethodInfo.Invoke(null, null) as MethodBase;
                if (original == null)
                {
                    FileLogger.Log("ASWDEBUG", "[BisectPatch] TargetMethod returned null: " + t.Name);
                    return;
                }

                var prefix = AccessTools.Method(t, "Prefix");
                var postfix = AccessTools.Method(t, "Postfix");
                var transpiler = AccessTools.Method(t, "Transpiler");

                harmony.Patch(original,
                    prefix != null ? new HarmonyMethod(prefix) : null,
                    postfix != null ? new HarmonyMethod(postfix) : null,
                    transpiler != null ? new HarmonyMethod(transpiler) : null);

                FileLogger.Log("ASWDEBUG", "[BisectPatch] OK: " + t.Name + " -> " + original.DeclaringType.Name + "." + original.Name);
            }
            catch (Exception e)
            {
                FileLogger.Log("ASWDEBUG", "[BisectPatch] FAIL " + (t == null ? "<null>" : t.Name) + ": " + e.Message);
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

        private static void ApplyCoreProtectionPatches(HarmonyInstance harmony)
        {
            Assembly asm = AppDomain.CurrentDomain.GetAssemblies()
                .FirstOrDefault(a => a.GetName().Name == "Assembly-CSharp");
            if (asm == null)
            {
                FileLogger.Log("ASWDEBUG", "[HarmonyLoader] Assembly-CSharp not found for core protection patches.");
                return;
            }

            TryPatch(harmony, asm, "ChannelConnection", "ParseProcessCheck",
                new Type[] { typeof(global::NetworkStream) },
                "Protection_ParseProcessCheckPrefix");

            TryPatch(harmony, asm, "ProcessCheck", "CheckByBlackJosnTable",
                new Type[] { typeof(string), typeof(Action<global::ProcessCheckInfo>) },
                "Protection_SkipProcessCheckPrefix");

            TryPatch(harmony, asm, "ProcessCheck", "CheckByUrl",
                new Type[] { typeof(string), typeof(Action<global::ProcessCheckInfo>) },
                "Protection_SkipProcessCheckPrefix");

            TryPatch(harmony, asm, "ChannelConnection", "PluginReport",
                new Type[] { typeof(byte), typeof(global::AssitToolType), typeof(bool) },
                "Protection_BlockPluginReportPrefix");

            // The current assembly uses v8 reports. Bit 0x80 marks a same-frame capture, while
            // plain version 8 means the sample was missing. Keep the native lifecycle so this
            // distinction and the variable sample count reach the packet builder intact.
            FileLogger.Log("ASWDEBUG",
                "[HarmonyLoader] AimAssistDetector v8 lifecycle left native; captured payload normalization enabled.");

            TryPatchAllOverloads(harmony, asm, "GunBaseController", "AssitToolCheck",
                "Protection_BlockAssistToolCheckPrefix");

            TryPatch(harmony, asm, "ShootPayloadCrypt", "BuildEncryptedPayload",
                new Type[] { typeof(global::HitMessage) },
                "Protection_ShootPayloadBuildPrefix");

            TryPatch(harmony, asm, "ShootPayloadCrypt", "BuildEncryptedPayload",
                new Type[] { typeof(global::HitMessage), typeof(int) },
                "Protection_ShootPayloadBuildWithSpreadPrefix");

            TryPatch(harmony, asm, "ClientFileMd5Checker", "GetClientBinarySummaryMd5",
                new Type[] { typeof(bool) },
                "Protection_ClientFileMd5Prefix");

            TryPatch(harmony, asm, "ClientFileMd5Checker", "GetEncryptedClientBinarySummaryMd5",
                new Type[] { typeof(ulong), typeof(bool) },
                "Protection_EncryptedClientFileMd5Prefix");

            TryPatch(harmony, asm, "ChannelConnection", "ParsePositionCheck",
                Type.EmptyTypes,
                "Protection_ParsePositionCheckPrefix");

            TryPatch(harmony, asm, "ChannelConnection", "ParseKickOutByPlugin",
                new Type[] { typeof(global::NetworkStream) },
                "Protection_ParseKickOutByPluginPrefix");

            TryPatch(harmony, asm, "ChannelConnection", "ParseNotifyKickedByGM",
                new Type[] { typeof(global::NetworkStream) },
                "Protection_ParseNotifyKickedByGMPrefix");

            TryPatch(harmony, asm, "ChannelConnection", "ParseNotifyKickedByVote",
                new Type[] { typeof(global::NetworkStream) },
                "Protection_ParseNotifyKickedByVotePrefix");

            TryPatch(harmony, asm, "LobbyConnection", "RequestReport",
                new Type[] { typeof(byte), typeof(string), typeof(string), typeof(int), typeof(byte[]) },
                "Protection_BlockRequestReportPrefix");

            TryPatch(harmony, asm, "LobbyConnection", "RequestShutdown",
                new Type[] { typeof(string) },
                "Protection_BlockRequestShutdownPrefix");

            TryPatch(harmony, asm, "LobbyConnection", "ForceDisconnect",
                Type.EmptyTypes,
                "Protection_BlockLobbyForceDisconnectPrefix");

            TryPatch(harmony, asm, "LobbyConnection", "OnDisconnected",
                Type.EmptyTypes,
                "Protection_LogLobbyOnDisconnectedPrefix");

            TryPatch(harmony, asm, "TcpConnection", "OnError",
                new Type[] { typeof(global::TcpConnection.ErrorType), typeof(string) },
                "Protection_LogTcpErrorPrefix");

            TryPatch(harmony, asm, "TcpConnection", "Disconnect",
                Type.EmptyTypes,
                "Protection_LogTcpDisconnectPrefix");

            TryPatch(harmony, asm, "GameApp", "set_ErrorMessage",
                new Type[] { typeof(int) },
                "Protection_FilterErrorMessagePrefix");

            TryPatch(harmony, asm, "GameApp", "InitializeDllDetection",
                Type.EmptyTypes,
                "Protection_SkipInitializeDllDetectionPrefix");

            TryPatch(harmony, asm, "GameApp", "ExitOnCheatDetected",
                Type.EmptyTypes,
                "Protection_BlockGameAppExitOnCheatDetectedPrefix");

            TryPatch(harmony, asm, "CodeStage.AntiCheat.Detectors.ActDetectorBase", "OnCheatingDetected",
                Type.EmptyTypes,
                "Protection_BlockActkOnCheatingDetectedPrefix");

            TryPatch(harmony, asm, "CodeStage.AntiCheat.Detectors.InjectionDetector", "OnNewAssemblyLoaded",
                new Type[] { typeof(object), typeof(AssemblyLoadEventArgs) },
                "Protection_BlockActkAssemblyLoadedPrefix");

            TryPatch(harmony, asm, "CodeStage.AntiCheat.Detectors.ManagedAssemblyDetector", "OnAssemblyLoaded",
                new Type[] { typeof(object), typeof(AssemblyLoadEventArgs) },
                "Protection_BlockActkAssemblyLoadedPrefix");

            TryPatch(harmony, asm, "CodeStage.AntiCheat.Detectors.NativeDllDetector", "CheckDlls",
                Type.EmptyTypes,
                "Protection_BlockNativeDllCheckPrefix");

            ApplyExtendedAntiDetectionPatches(harmony, asm);

            TryPatch(harmony, asm, "ChannelConnection", "Disconnect",
                Type.EmptyTypes,
                "Protection_LogChannelDisconnectPrefix");

            TryPatch(harmony, asm, "ChannelConnection", "OnDisconnected",
                Type.EmptyTypes,
                "Protection_LogChannelOnDisconnectedPrefix");

            if (Settings.MultiOpenPatchHooksEnabled)
            {
                TryPatch(harmony, asm, "LobbyConnection", "RequestEnterLobby",
                    new Type[] { typeof(ulong) },
                    "MultiOpen_RequestEnterLobbyPrefix");

                TryPatch(harmony, asm, "LobbyConnection", "getUC",
                    Type.EmptyTypes,
                    "MultiOpen_GetUcPrefix");

                TryPatch(harmony, asm, "LobbyConnection", "setUC",
                    new Type[] { typeof(string) },
                    "MultiOpen_SetUcPrefix");

                TryPatchPostfix(harmony, asm, "LobbyConnection", "ResponseEnterLobby",
                    Type.EmptyTypes,
                    "MultiOpen_ResponseEnterLobbyPostfix");

                TryPatchPostfix(harmony, asm, "ChannelConnection", "ResponseGameEnter",
                    Type.EmptyTypes,
                    "MultiOpen_ResponseGameEnterPostfix");

                TryPatch(harmony, asm, "ChannelConnection", "NotifyRoomKickClient",
                    Type.EmptyTypes,
                    "MultiOpen_NotifyRoomKickClientPrefix");

                TryPatch(harmony, asm, "LaucherConnection", "OnClientMessage",
                    Type.EmptyTypes,
                    "MultiOpen_LauncherOnClientMessagePrefix");

                TryPatch(harmony, asm, "LaucherConnection", "Close",
                    Type.EmptyTypes,
                    "MultiOpen_LauncherClosePrefix");

                TryPatch(harmony, asm, "LaucherConnection", "Update",
                    Type.EmptyTypes,
                    "MultiOpen_LauncherUpdatePrefix");

                TryPatch(harmony, asm, "TcpConnection", "CheckConnection",
                    Type.EmptyTypes,
                    "MultiOpen_TcpCheckConnectionPrefix");

                TryPatch(harmony, asm, "GameApp", "ExitApp",
                    Type.EmptyTypes,
                    "MultiOpen_GameAppExitAppPrefix");

                TryPatch(harmony, asm, "GameApp", "ExitOnCheatDetected",
                    Type.EmptyTypes,
                    "MultiOpen_GameAppExitOnCheatDetectedPrefix");

                TryPatch(harmony, asm, "GameApp", "OnApplicationQuit",
                    Type.EmptyTypes,
                    "MultiOpen_GameAppOnApplicationQuitPrefix");

                TryPatchKnownType(harmony, typeof(Application), "Quit",
                    Type.EmptyTypes,
                    "MultiOpen_ApplicationQuitPrefix");

                TryPatchKnownType(harmony, typeof(Application), "Quit",
                    new Type[] { typeof(int) },
                    "MultiOpen_ApplicationQuitWithCodePrefix");
            }
            else
            {
                FileLogger.Log("ASWDEBUG", "[HarmonyLoader] Multi-open hooks disabled by Settings.MultiOpenPatchHooksEnabled=false.");
            }
        }

        private static void ApplyExtendedAntiDetectionPatches(HarmonyInstance harmony, Assembly asm)
        {
            if (!Settings.ExtendedAntiDetectionBypassEnabled)
            {
                FileLogger.Log("ASWDEBUG", "[HarmonyLoader] extended anti-detection patches disabled.");
                return;
            }

            string[] detectorTypes =
            {
                "CodeStage.AntiCheat.Detectors.InjectionDetector",
                "CodeStage.AntiCheat.Detectors.ManagedAssemblyDetector",
                "CodeStage.AntiCheat.Detectors.NativeDllDetector",
                "CodeStage.AntiCheat.Detectors.ObscuredCheatingDetector",
                "CodeStage.AntiCheat.Detectors.SpeedHackDetector",
                "CodeStage.AntiCheat.Detectors.WallHackDetector"
            };

            string[] voidBlockMethods =
            {
                "Start",
                "Update",
                "FixedUpdate",
                "StartDetection",
                "StartDetectionInternal",
                "StartDetectionAutomatically",
                "ResumeDetector",
                "OnNewAssemblyLoaded",
                "ScanInitialAssemblies",
                "AddSuspicious",
                "CheckDlls",
                "AnalyzeDll",
                "StartRigidModule",
                "StartControllerModule",
                "StartWireframeModule",
                "ShootWireframeModule",
                "StartRaycastModule",
                "ShootRaycastModule",
                "UpdateServiceContainer",
                "ResetStartTicks"
            };

            for (int i = 0; i < detectorTypes.Length; i++)
            {
                for (int j = 0; j < voidBlockMethods.Length; j++)
                {
                    TryPatchAllOverloads(harmony, asm, detectorTypes[i], voidBlockMethods[j], "Protection_BlockExtendedDetectorPrefix");
                }
            }

            TryPatchAllOverloads(harmony, asm, "CodeStage.AntiCheat.Detectors.ActDetectorBase",
                "StartDetectionAutomatically", "Protection_BlockExtendedDetectorPrefix");
            TryPatchAllOverloads(harmony, asm, "CodeStage.AntiCheat.Detectors.ActDetectorBase",
                "ResumeDetector", "Protection_BlockExtendedDetectorPrefix");

            TryPatchAllOverloads(harmony, asm, "CodeStage.AntiCheat.Detectors.InjectionDetector",
                "AssemblyAllowed", "Protection_AllowAssemblyPrefix");
            TryPatchAllOverloads(harmony, asm, "CodeStage.AntiCheat.Detectors.ManagedAssemblyDetector",
                "GetSuspiciousCount", "Protection_IntZeroPrefix");
            TryPatchAllOverloads(harmony, asm, "CodeStage.AntiCheat.Detectors.NativeDllDetector",
                "get_IsRunning", "Protection_BoolFalsePrefix");
            TryPatchAllOverloads(harmony, asm, "CodeStage.AntiCheat.Detectors.WallHackDetector",
                "Detect", "Protection_BoolFalsePrefix");

            // Obscured value access calls get_IsRunning on every read/write. Patching that hot
            // getter produced millions of Harmony calls and multi-megabyte logs. Stop an instance
            // that may already have started before our loader, while StartDetection remains blocked.
            StopRuntimeDetector(asm, "CodeStage.AntiCheat.Detectors.ObscuredCheatingDetector");
        }

        private static void StopRuntimeDetector(Assembly asm, string typeName)
        {
            try
            {
                Type detectorType = asm == null ? null : asm.GetType(typeName);
                MethodInfo stop = detectorType == null
                    ? null
                    : detectorType.GetMethod("StopDetection",
                        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static,
                        null, Type.EmptyTypes, null);
                if (stop == null)
                {
                    FileLogger.Log("ASWDEBUG", "[HarmonyLoader] detector stop method not found: " + typeName);
                    return;
                }

                stop.Invoke(null, null);
                FileLogger.Log("NET-AUDIT", "[EXT-DETECT] stopped runtime detector " + typeName);
            }
            catch (Exception ex)
            {
                FileLogger.Log("NET-AUDIT", "[EXT-DETECT] runtime stop failed " + typeName + ": " + ex.Message);
            }
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

        private static void TryPatchAllOverloads(HarmonyInstance harmony, Assembly asm, string typeName, string methodName, string prefixMethodName)
        {
            try
            {
                Type t = asm.GetType(typeName);
                if (t == null)
                {
                    FileLogger.Log("ASWDEBUG", "[HarmonyLoader] type not found: " + typeName);
                    return;
                }

                MethodInfo prefix = AccessTools.Method(typeof(HarmonyLoader), prefixMethodName);
                if (prefix == null)
                {
                    FileLogger.Log("ASWDEBUG", "[HarmonyLoader] prefix not found: " + prefixMethodName);
                    return;
                }

                MethodInfo[] methods = t.GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
                int patched = 0;
                for (int i = 0; i < methods.Length; i++)
                {
                    MethodInfo original = methods[i];
                    if (original == null || original.Name != methodName || original.ContainsGenericParameters)
                    {
                        continue;
                    }

                    harmony.Patch(original, new HarmonyMethod(prefix), null, null);
                    patched++;
                }

                if (patched > 0)
                {
                    FileLogger.Log("ASWDEBUG",
                        "[HarmonyLoader] patched overloads: " + typeName + "." + methodName +
                        " count=" + patched);
                }
            }
            catch (Exception ex)
            {
                FileLogger.Log("ASWDEBUG", "[HarmonyLoader] overload patch failed: " + typeName + "." + methodName + " => " + ex);
            }
        }

        private static void TryPatchKnownType(HarmonyInstance harmony, Type type, string methodName, Type[] argTypes, string prefixMethodName)
        {
            string typeName = type == null ? "<null>" : type.FullName;
            try
            {
                if (type == null)
                {
                    FileLogger.Log("ASWDEBUG", "[HarmonyLoader] type not found: " + typeName);
                    return;
                }

                MethodInfo original = AccessTools.Method(type, methodName, argTypes);
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

        private static void TryPatchPostfix(HarmonyInstance harmony, Assembly asm, string typeName, string methodName, Type[] argTypes, string postfixMethodName)
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

                MethodInfo postfix = AccessTools.Method(typeof(HarmonyLoader), postfixMethodName);
                if (postfix == null)
                {
                    FileLogger.Log("ASWDEBUG", "[HarmonyLoader] postfix not found: " + postfixMethodName);
                    return;
                }

                harmony.Patch(original, null, new HarmonyMethod(postfix), null);
                FileLogger.Log("ASWDEBUG", "[HarmonyLoader] patched postfix: " + typeName + "." + methodName);
            }
            catch (Exception ex)
            {
                FileLogger.Log("ASWDEBUG", "[HarmonyLoader] postfix patch failed: " + typeName + "." + methodName + " => " + ex);
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

        private static void Telemetry_ShootPrefix(Vector3 position, Vector3 direction, object hit_message, byte slot, bool do_effect, Vector3 velocity)
        {
            try
            {
                int uid = ReadHitInt(hit_message, "uid");
                int part = ReadHitInt(hit_message, "part");
                int enc = ReadHitInt(hit_message, "enc");
                int sight = ReadHitInt(hit_message, "current_sight");
                int robotUid = ReadHitInt(hit_message, "robot_uid");
                int isRealMan = ReadHitInt(hit_message, "is_real_man");
                int distance = ReadHitInt(hit_message, "distance");
                float spread = ReadHitFloat(hit_message, "spread");
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

        private static int ReadHitInt(object hitMessage, string fieldName)
        {
            try
            {
                if (hitMessage == null) return 0;
                FieldInfo field = hitMessage.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (field == null) return 0;
                object value = field.GetValue(hitMessage);
                return value == null ? 0 : Convert.ToInt32(value);
            }
            catch
            {
                return 0;
            }
        }

        private static float ReadHitFloat(object hitMessage, string fieldName)
        {
            try
            {
                if (hitMessage == null) return 0f;
                FieldInfo field = hitMessage.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (field == null) return 0f;
                object value = field.GetValue(hitMessage);
                return value == null ? 0f : Convert.ToSingle(value);
            }
            catch
            {
                return 0f;
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

        private static bool Protection_ParseProcessCheckPrefix(global::NetworkStream reader)
        {
            try
            {
                byte mode = 255;
                int payloadLen = 0;

                if (reader != null)
                {
                    mode = reader.ReadByte();
                    string payload = reader.ReadString();
                    payloadLen = payload == null ? 0 : payload.Length;
                }

                _processCheckSuppressCount++;
                FileLogger.Log("NET-AUDIT",
                    "[PROC-CHECK] suppressed #" + _processCheckSuppressCount +
                    " mode=" + mode +
                    " payloadLen=" + payloadLen);
            }
            catch (Exception ex)
            {
                FileLogger.Log("NET-AUDIT", "[PROC-CHECK] suppress read error: " + ex.Message);
            }

            return false;
        }

        private static bool Protection_SkipProcessCheckPrefix(string __0, Action<global::ProcessCheckInfo> __1)
        {
            try
            {
                _processCheckSuppressCount++;
                FileLogger.Log("NET-AUDIT",
                    "[PROC-CHECK] bypass #" + _processCheckSuppressCount +
                    " inputLen=" + SafeLen(__0));

                if (__1 != null)
                {
                    __1(null);
                }
            }
            catch (Exception ex)
            {
                FileLogger.Log("NET-AUDIT", "[PROC-CHECK] bypass error: " + ex.Message);
            }

            return false;
        }

        private static bool Protection_BlockPluginReportPrefix(byte uid, global::AssitToolType type, bool check_ok)
        {
            try
            {
                _pluginReportSuppressCount++;
                FileLogger.Log("NET-AUDIT",
                    "[PLUGIN-REPORT] blocked #" + _pluginReportSuppressCount +
                    " uid=" + uid +
                    " type=" + type +
                    " check_ok=" + check_ok +
                    " caller=" + CaptureCaller());
            }
            catch (Exception ex)
            {
                FileLogger.Log("NET-AUDIT", "[PLUGIN-REPORT] block error: " + ex.Message);
            }

            return false;
        }

        private static bool Protection_AimAssistReportPrefix(object hitMessage)
        {
            if (!Settings.AimAssistDetectorBypassEnabled) return true;

            try
            {
                NormalizeAimReportFields(hitMessage);
                if (global::ASWDEBUG.ShotDiagnostics.HighFrequencyLoggingEnabled &&
                    ShouldLog(ref _aimAssistReportBypassCount, 12, 200))
                {
                    FileLogger.Log("NET-AUDIT",
                        "[AIM-BYPASS] report-fill blocked #" + _aimAssistReportBypassCount +
                        " caller=" + CaptureCaller());
                }
            }
            catch (Exception ex)
            {
                FileLogger.Log("NET-AUDIT", "[AIM-BYPASS] report-fill block error: " + ex.Message);
            }

            return false;
        }

        private static bool Protection_AimAssistFireCooldownPrefix(float fireTime)
        {
            if (!Settings.AimAssistDetectorBypassEnabled) return true;

            try
            {
                if (global::ASWDEBUG.ShotDiagnostics.HighFrequencyLoggingEnabled &&
                    ShouldLog(ref _aimAssistSampleBypassCount, 12, 300))
                {
                    FileLogger.Log("NET-AUDIT",
                        "[AIM-BYPASS] fire-cooldown sample blocked #" + _aimAssistSampleBypassCount +
                        " fireTime=" + fireTime);
                }
            }
            catch (Exception ex)
            {
                FileLogger.Log("NET-AUDIT", "[AIM-BYPASS] fire-cooldown block error: " + ex.Message);
            }

            return false;
        }

        private static bool Protection_AimAssistSamplePrefix(object __instance)
        {
            if (!Settings.AimAssistDetectorBypassEnabled) return true;

            try
            {
                if (global::ASWDEBUG.ShotDiagnostics.HighFrequencyLoggingEnabled &&
                    ShouldLog(ref _aimAssistSampleBypassCount, 12, 300))
                {
                    FileLogger.Log("NET-AUDIT",
                        "[AIM-BYPASS] runtime sample blocked #" + _aimAssistSampleBypassCount +
                        " detector=" + (__instance == null ? "<null>" : __instance.GetType().FullName));
                }
            }
            catch (Exception ex)
            {
                FileLogger.Log("NET-AUDIT", "[AIM-BYPASS] runtime sample block error: " + ex.Message);
            }

            return false;
        }

        private static bool Protection_AimAssistConfigPrefix(float value)
        {
            if (!Settings.AimAssistDetectorBypassEnabled) return true;

            try
            {
                if (ShouldLog(ref _aimAssistConfigBypassCount, 12, 100))
                {
                    FileLogger.Log("NET-AUDIT",
                        "[AIM-BYPASS] remote aim-assist config ignored #" + _aimAssistConfigBypassCount +
                        " threshold=" + value);
                }
            }
            catch (Exception ex)
            {
                FileLogger.Log("NET-AUDIT", "[AIM-BYPASS] config block error: " + ex.Message);
            }

            return false;
        }

        private static bool Protection_AimAssistBlockPrefix(MethodBase __originalMethod)
        {
            if (!Settings.AimAssistDetectorBypassEnabled) return true;

            try
            {
                if (global::ASWDEBUG.ShotDiagnostics.HighFrequencyLoggingEnabled &&
                    ShouldLog(ref _aimAssistSampleBypassCount, 18, 300))
                {
                    FileLogger.Log("NET-AUDIT",
                        "[AIM-BYPASS] blocked " + FormatMethod(__originalMethod) +
                        " count=" + _aimAssistSampleBypassCount);
                }
            }
            catch (Exception ex)
            {
                FileLogger.Log("NET-AUDIT", "[AIM-BYPASS] generic block error: " + ex.Message);
            }

            return false;
        }

        private static bool Protection_BlockAssistToolCheckPrefix(MethodBase __originalMethod)
        {
            if (!Settings.ExtendedAntiDetectionBypassEnabled) return true;

            try
            {
                if (global::ASWDEBUG.ShotDiagnostics.HighFrequencyLoggingEnabled &&
                    ShouldLog(ref _assistToolCheckSuppressCount, 24, 300))
                {
                    FileLogger.Log("NET-AUDIT",
                        "[ASSIST-CHECK] blocked " + FormatMethod(__originalMethod) +
                        " count=" + _assistToolCheckSuppressCount +
                        " caller=" + CaptureCaller());
                }
            }
            catch (Exception ex)
            {
                FileLogger.Log("NET-AUDIT", "[ASSIST-CHECK] block error: " + ex.Message);
            }

            return false;
        }

        private static void Protection_ShootPayloadBuildPrefix(object hitMessage)
        {
            // This overload writes no payload itself; it delegates to BuildEncryptedPayload(hit, spread).
            // Normalizing here would make the delegated hook observe already-modified diagnostics.
        }

        private static void Protection_ShootPayloadBuildWithSpreadPrefix(object hitMessage, int currentSpreadIndex)
        {
            Protection_SanitizeShootPayload(hitMessage, currentSpreadIndex, "BuildEncryptedPayload(hit,spread)");
        }

        private static void Protection_SanitizeShootPayload(object hitMessage, int currentSpreadIndex, string source)
        {
            if (!Settings.AimAssistDetectorBypassEnabled) return;

            try
            {
                int adjustedPrecisionCount = NormalizeAimReportFields(hitMessage);
                if (adjustedPrecisionCount < 0) return;

                global::ASWDEBUG.ShotDiagnostics.LogAimPayload(
                    hitMessage,
                    currentSpreadIndex,
                    adjustedPrecisionCount,
                    source);

                if (global::ASWDEBUG.ShotDiagnostics.HighFrequencyLoggingEnabled &&
                    ShouldLog(ref _aimAssistPayloadSanitizeCount, 12, 200))
                {
                    int version = ReadRuntimeFieldInt(hitMessage, "aim_report_version", 0);
                    short[] samples = ReadRuntimeShortArray(hitMessage, "aim_precision_samples");
                    FileLogger.Log("NET-AUDIT",
                        "[AIM-BYPASS] payload aim-report normalized #" + _aimAssistPayloadSanitizeCount +
                        " source=" + source +
                        " version=" + version +
                        " captured=" + ((version & 0x80) != 0) +
                        " target=" + ReadRuntimeFieldInt(hitMessage, "aim_target_uid", 0) +
                        " shotCode=" + ReadRuntimeFieldInt(hitMessage, "aim_shot_precision_code", -1) +
                        " samples=" + (samples == null ? 0 : samples.Length) +
                        " adjusted=" + adjustedPrecisionCount);
                }
            }
            catch (Exception ex)
            {
                FileLogger.Log("NET-AUDIT", "[AIM-BYPASS] payload sanitize error: " + ex.Message);
            }
        }

        private static int NormalizeAimReportFields(object hitMessage)
        {
            if (hitMessage == null) return -1;
            if (!HasRuntimeField(hitMessage, "aim_report_version")) return -1;

            if (HasRuntimeField(hitMessage, "aim_shot_precision_code") ||
                HasRuntimeField(hitMessage, "aim_precision_samples"))
            {
                int version = ReadRuntimeFieldInt(hitMessage, "aim_report_version", 8);
                bool captured = (version & 0x80) != 0;
                if (!captured)
                {
                    // Preserve genuine timing misses instead of manufacturing a different shape.
                    return 0;
                }

                TrySetRuntimeField(hitMessage, "aim_report_version", (byte)0x88);

                int seed = ReadRuntimeFieldInt(hitMessage, "enc", 0);
                seed ^= ReadRuntimeFieldInt(hitMessage, "uid", 0) * 397;
                seed ^= Time.frameCount;

                int adjusted = 0;
                short shotCode = (short)ReadRuntimeFieldInt(hitMessage, "aim_shot_precision_code", -1);
                short normalizedShotCode = NormalizePrecisionCode(shotCode, seed, -1);
                if (normalizedShotCode != shotCode)
                {
                    TrySetRuntimeField(hitMessage, "aim_shot_precision_code", normalizedShotCode);
                    adjusted++;
                }

                short[] samples = ReadRuntimeShortArray(hitMessage, "aim_precision_samples");
                if (samples != null && samples.Length > 0)
                {
                    short[] normalizedSamples = (short[])samples.Clone();
                    bool samplesChanged = false;
                    for (int i = 0; i < normalizedSamples.Length; i++)
                    {
                        short normalized = NormalizePrecisionCode(normalizedSamples[i], seed, i);
                        if (normalized == normalizedSamples[i]) continue;

                        normalizedSamples[i] = normalized;
                        samplesChanged = true;
                        adjusted++;
                    }

                    // ChannelConnection writes this array's length before entering the payload
                    // builder. Never change the length here or the outer frame becomes inconsistent.
                    if (samplesChanged)
                    {
                        TrySetRuntimeField(hitMessage, "aim_precision_samples", normalizedSamples);
                    }
                }

                return adjusted;
            }

            // Legacy v3 reports use relative_speed == -1 as the missing-sample sentinel.
            // Normalize only real samples and retain genuine missing reports unchanged.
            int relativeSpeed = ReadRuntimeFieldInt(hitMessage, "aim_relative_speed_cmps", -1);
            if (relativeSpeed >= 0)
            {
                TrySetRuntimeField(hitMessage, "aim_report_version", (byte)3);
                TrySetRuntimeField(hitMessage, "aim_lock_session_id", 0);
                TrySetRuntimeField(hitMessage, "aim_lock_duration_ms", 0);
                TrySetRuntimeField(hitMessage, "aim_lock_target_uid", (byte)0);
                TrySetRuntimeField(hitMessage, "aim_target_uid", (byte)0);
                TrySetRuntimeField(hitMessage, "aim_relative_speed_cmps", (short)0);
                TrySetRuntimeField(hitMessage, "aim_head_precision_mm", (short)-1);
                return 1;
            }

            return 0;
        }

        private static short NormalizePrecisionCode(short code, int seed, int sampleIndex)
        {
            if (code < 0) return code;

            int millimeters = code / 10;
            if (millimeters >= 120) return code;

            uint mixed = unchecked((uint)seed);
            mixed ^= unchecked((uint)(sampleIndex + 2) * 0x9E3779B9u);
            mixed ^= mixed >> 16;
            mixed *= 0x7FEB352Du;
            mixed ^= mixed >> 15;
            mixed *= 0x846CA68Bu;
            mixed ^= mixed >> 16;

            int normalizedMillimeters = 120 + (int)(mixed % 141u);
            return EncodePrecisionMillimeters(normalizedMillimeters);
        }

        private static short EncodePrecisionMillimeters(int millimeters)
        {
            millimeters = Mathf.Clamp(millimeters, 0, 3276);
            int hundreds = millimeters / 100 % 10;
            int tens = millimeters / 10 % 10;
            int ones = millimeters % 10;
            int checkDigit = Mathf.Abs(hundreds + tens - ones) % 10;
            return (short)(millimeters * 10 + checkDigit);
        }

        private static bool HasRuntimeField(object instance, string fieldName)
        {
            return instance != null &&
                   instance.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic) != null;
        }

        private static int ReadRuntimeFieldInt(object instance, string fieldName, int fallback)
        {
            try
            {
                if (instance == null) return fallback;
                FieldInfo field = instance.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (field == null) return fallback;

                object value = field.GetValue(instance);
                if (value == null) return fallback;

                try
                {
                    return Convert.ToInt32(value);
                }
                catch
                {
                }

                MethodInfo implicitMethod = field.FieldType.GetMethod("op_Implicit",
                    BindingFlags.Public | BindingFlags.Static,
                    null,
                    new Type[] { field.FieldType },
                    null);
                if (implicitMethod == null) return fallback;

                object plainValue = implicitMethod.Invoke(null, new object[] { value });
                return plainValue == null ? fallback : Convert.ToInt32(plainValue);
            }
            catch
            {
                return fallback;
            }
        }

        private static short[] ReadRuntimeShortArray(object instance, string fieldName)
        {
            try
            {
                if (instance == null) return null;
                FieldInfo field = instance.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                return field == null ? null : field.GetValue(instance) as short[];
            }
            catch
            {
                return null;
            }
        }

        private static bool TrySetRuntimeField(object instance, string fieldName, object plainValue)
        {
            try
            {
                if (instance == null) return false;
                FieldInfo field = instance.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (field == null) return false;

                object boxed = ConvertRuntimeFieldValue(field.FieldType, plainValue);
                if (boxed == null) return false;

                field.SetValue(instance, boxed);
                return true;
            }
            catch (Exception ex)
            {
                FileLogger.Log("NET-AUDIT", "[AIM-BYPASS] set field " + fieldName + " error: " + ex.Message);
                return false;
            }
        }

        private static object ConvertRuntimeFieldValue(Type fieldType, object plainValue)
        {
            if (fieldType == null || plainValue == null) return null;
            if (fieldType.IsAssignableFrom(plainValue.GetType())) return plainValue;

            try
            {
                MethodInfo implicitMethod = fieldType.GetMethod("op_Implicit",
                    BindingFlags.Public | BindingFlags.Static,
                    null,
                    new Type[] { plainValue.GetType() },
                    null);
                if (implicitMethod != null)
                {
                    return implicitMethod.Invoke(null, new object[] { plainValue });
                }
            }
            catch
            {
            }

            try
            {
                return Convert.ChangeType(plainValue, fieldType);
            }
            catch
            {
                return null;
            }
        }

        private static bool Protection_ParsePositionCheckPrefix(global::ChannelConnection __instance)
        {
            try
            {
                byte uid = __instance != null ? __instance.ReadByte() : (byte)0;
                int marker = __instance != null ? __instance.ReadInt() : 0;
                global::NetworkStream stream = null;
                byte flags = 0;
                Vector3 pos = default(Vector3);

                if (__instance != null)
                {
                    stream = Traverse.Create(__instance).Field("_stream").GetValue<global::NetworkStream>();
                    if (stream != null)
                    {
                        pos = global::ConnectionDef.ReadCharacterPosition(stream);
                    }
                    flags = __instance.ReadByte();
                }

                _positionCheckLogCount++;
                FileLogger.Log("NET-AUDIT",
                    "[POS-CHECK] suppressed #" + _positionCheckLogCount +
                    " uid=" + uid +
                    " marker=" + marker +
                    " pos=" + pos +
                    " flags=0x" + flags.ToString("X2"));
            }
            catch (Exception ex)
            {
                FileLogger.Log("NET-AUDIT", "[POS-CHECK] suppress error: " + ex.Message);
            }

            return false;
        }

        private static bool Protection_BlockRequestReportPrefix(byte __0, string __1, string __2, int __3, byte[] __4)
        {
            try
            {
                _requestReportSuppressCount++;
                FileLogger.Log("NET-AUDIT",
                    "[REQ-REPORT] blocked #" + _requestReportSuppressCount +
                    " type=" + __0 +
                    " target=" + (__1 ?? "<null>") +
                    " size=" + __3 +
                    " message=" + TrimLog(__2, PreviewChars));
            }
            catch (Exception ex)
            {
                FileLogger.Log("NET-AUDIT", "[REQ-REPORT] block error: " + ex.Message);
            }

            return false;
        }

        private static bool Protection_BlockRequestShutdownPrefix(string __0)
        {
            try
            {
                FileLogger.Log("NET-AUDIT",
                    "[REQ-SHUTDOWN] blocked passwordLen=" + SafeLen(__0) +
                    " caller=" + CaptureCaller());
            }
            catch (Exception ex)
            {
                FileLogger.Log("NET-AUDIT", "[REQ-SHUTDOWN] block error: " + ex.Message);
            }

            return false;
        }

        private static bool Protection_BlockLobbyForceDisconnectPrefix(global::LobbyConnection __instance)
        {
            bool block = false;
            try
            {
                _forceDisconnectSuppressCount++;
                int msgType = 0;
                string state = "<null>";

                if (__instance != null)
                {
                    state = __instance.state.ToString();
                    try
                    {
                        msgType = Traverse.Create(__instance).Field("msgType").GetValue<int>();
                    }
                    catch
                    {
                    }
                }

                block = Settings.MultiOpenEnabled &&
                        Settings.MultiOpenBlockLobbyForceDisconnect &&
                        IsProtectedMultiOpenBattle(__instance);

                FileLogger.Log("NET-AUDIT",
                    "[FORCE-DISCONNECT] " + (block ? "blocked" : "observed") +
                    " #" + _forceDisconnectSuppressCount +
                    " state=" + state +
                    " msgType=" + msgType +
                    " multiOpen=" + Settings.MultiOpenEnabled +
                    " normalBattle=" + IsNormalBattleMode() +
                    " caller=" + CaptureCaller());
            }
            catch (Exception ex)
            {
                FileLogger.Log("NET-AUDIT", "[FORCE-DISCONNECT] log error: " + ex.Message);
            }

            return !block;
        }

        private static bool Protection_ParseKickOutByPluginPrefix(global::ChannelConnection __instance)
        {
            try
            {
                byte mode = __instance != null ? __instance.ReadByte() : (byte)0;
                global::ASWDEBUG.ShotDiagnostics.LogKick("PLUGIN", mode);
                FileLogger.Log("NET-AUDIT",
                    "[KICKOUT-BY-PLUGIN] suppressed mode=" + mode +
                    " caller=" + CaptureCaller());
            }
            catch (Exception ex)
            {
                FileLogger.Log("NET-AUDIT", "[KICKOUT-BY-PLUGIN] suppress error: " + ex.Message);
            }

            return false;
        }

        private static bool Protection_ParseNotifyKickedByGMPrefix()
        {
            try
            {
                global::ASWDEBUG.ShotDiagnostics.LogKick("GM", -1);
                FileLogger.Log("NET-AUDIT", "[KICKED-BY-GM] suppressed");
            }
            catch (Exception ex)
            {
                FileLogger.Log("NET-AUDIT", "[KICKED-BY-GM] suppress error: " + ex.Message);
            }

            return false;
        }

        private static bool Protection_ParseNotifyKickedByVotePrefix()
        {
            try
            {
                global::ASWDEBUG.ShotDiagnostics.LogKick("VOTE", -1);
                FileLogger.Log("NET-AUDIT", "[KICKED-BY-VOTE] suppressed");
            }
            catch (Exception ex)
            {
                FileLogger.Log("NET-AUDIT", "[KICKED-BY-VOTE] suppress error: " + ex.Message);
            }

            return false;
        }

        private static void Protection_LogTcpErrorPrefix(global::TcpConnection __instance, global::TcpConnection.ErrorType __0, string __1)
        {
            try
            {
                string connType = __instance == null ? "<null>" : __instance.GetType().FullName;
                FileLogger.Log("NET-AUDIT",
                    "[TCP-ERROR] type=" + __0 +
                    " conn=" + connType +
                    " msg=" + TrimLog(__1, 400));
            }
            catch (Exception ex)
            {
                FileLogger.Log("NET-AUDIT", "[TCP-ERROR] log error: " + ex.Message);
            }
        }

        private static void Protection_LogTcpDisconnectPrefix(global::TcpConnection __instance)
        {
            try
            {
                string connType = __instance == null ? "<null>" : __instance.GetType().FullName;
                int msgType = 0;
                try
                {
                    if (__instance != null)
                    {
                        msgType = Traverse.Create(__instance).Field("msgType").GetValue<int>();
                    }
                }
                catch
                {
                }

                FileLogger.Log("NET-AUDIT",
                    "[TCP-DISCONNECT] conn=" + connType +
                    " msgType=" + msgType +
                    " caller=" + CaptureCaller());
            }
            catch (Exception ex)
            {
                FileLogger.Log("NET-AUDIT", "[TCP-DISCONNECT] log error: " + ex.Message);
            }
        }

        private static bool MultiOpen_TcpCheckConnectionPrefix(global::TcpConnection __instance)
        {
            if (!Settings.MultiOpenEnabled || !Settings.MultiOpenPerProcessProxyEnabled || __instance == null)
            {
                return true;
            }

            EnsureMultiOpenProxySelection();
            if (_multiOpenProxyDirect)
            {
                return true;
            }

            Socket proxySocket = null;
            try
            {
                Traverse instance = Traverse.Create(__instance);
                string targetHost = instance.Field("host").GetValue<string>();
                int targetPort = instance.Field("port").GetValue<int>();
                Socket previousSocket = instance.Field("socket").GetValue<Socket>();

                if (string.IsNullOrEmpty(targetHost) || targetPort <= 0 || targetPort > 65535)
                {
                    FileLogger.Log("NET-AUDIT",
                        "[MULTI-OPEN] [PROXY-CONNECT-SKIP] invalid target=" +
                        (targetHost ?? "<null>") + ":" + targetPort);
                    return true;
                }

                if (previousSocket != null)
                {
                    try { previousSocket.Close(); } catch { }
                    instance.Field("socket").SetValue(null);
                }

                proxySocket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                proxySocket.SendTimeout = 8000;
                proxySocket.ReceiveTimeout = 8000;
                proxySocket.Connect(_multiOpenProxyHost, _multiOpenProxyPort);
                Socks5Connect(proxySocket, targetHost, targetPort, _multiOpenProxyUser, _multiOpenProxyPassword);
                proxySocket.SendTimeout = 0;
                proxySocket.ReceiveTimeout = 0;
                proxySocket.Blocking = false;
                instance.Field("socket").SetValue(proxySocket);

                MethodInfo onConnected = __instance.GetType().GetMethod(
                    "OnConnected",
                    BindingFlags.Instance | BindingFlags.NonPublic);
                if (onConnected == null)
                {
                    throw new MissingMethodException(__instance.GetType().FullName, "OnConnected");
                }

                onConnected.Invoke(__instance, null);
                FileLogger.Log("NET-AUDIT",
                    "[MULTI-OPEN] [PROXY-CONNECT-OK] slot=" + _multiOpenProxySlot +
                    " conn=" + __instance.GetType().Name +
                    " proxy=" + _multiOpenProxyHost + ":" + _multiOpenProxyPort +
                    " target=" + targetHost + ":" + targetPort);
                return false;
            }
            catch (Exception ex)
            {
                if (proxySocket != null)
                {
                    try { proxySocket.Close(); } catch { }
                }

                try
                {
                    Traverse.Create(__instance).Field("socket").SetValue(null);
                }
                catch
                {
                }

                FileLogger.Log("NET-AUDIT",
                    "[MULTI-OPEN] [PROXY-CONNECT-FAILED] slot=" + _multiOpenProxySlot +
                    " conn=" + __instance.GetType().Name +
                    " proxy=" + _multiOpenProxyHost + ":" + _multiOpenProxyPort +
                    " error=" + UnwrapExceptionMessage(ex));
                return false;
            }
        }

        private static void EnsureMultiOpenProxySelection()
        {
            if (_multiOpenProxyInitialized) return;

            lock (MultiOpenProxySync)
            {
                if (_multiOpenProxyInitialized) return;
                _multiOpenProxyInitialized = true;

                try
                {
                    string assemblyPath = typeof(HarmonyLoader).Assembly.Location;
                    string baseDirectory = Path.GetDirectoryName(assemblyPath) ?? string.Empty;
                    string configPath = Path.Combine(baseDirectory, MultiOpenProxyConfigFileName);
                    if (!File.Exists(configPath))
                    {
                        FileLogger.Log("NET-AUDIT",
                            "[MULTI-OPEN] [PROXY-DIRECT] config missing: " + configPath +
                            "; server-side same-egress restriction remains active.");
                        return;
                    }

                    string[] lines = File.ReadAllLines(configPath);
                    string lockPrefix = "ASWDEBUG.MultiOpen.ProxySlot." + HashShort(configPath) + ".";
                    for (int i = 0; i < lines.Length; i++)
                    {
                        string line = (lines[i] ?? string.Empty).Trim();
                        if (line.Length == 0 || line.StartsWith("#") || line.StartsWith(";"))
                        {
                            continue;
                        }

                        FileStream slotLock = TryAcquireMultiOpenProxySlot(lockPrefix, i);
                        if (slotLock == null)
                        {
                            continue;
                        }

                        bool direct;
                        string host;
                        int port;
                        string user;
                        string password;
                        if (!TryParseMultiOpenProxy(line, out direct, out host, out port, out user, out password))
                        {
                            slotLock.Close();
                            FileLogger.Log("NET-AUDIT",
                                "[MULTI-OPEN] [PROXY-CONFIG-INVALID] line=" + (i + 1));
                            continue;
                        }

                        _multiOpenProxySlotLock = slotLock;
                        _multiOpenProxySlot = i;
                        _multiOpenProxyDirect = direct;
                        _multiOpenProxyHost = host;
                        _multiOpenProxyPort = port;
                        _multiOpenProxyUser = user;
                        _multiOpenProxyPassword = password;

                        FileLogger.Log("NET-AUDIT",
                            direct
                                ? "[MULTI-OPEN] [PROXY-SLOT] slot=" + i + " route=direct"
                                : "[MULTI-OPEN] [PROXY-SLOT] slot=" + i +
                                  " route=socks5 proxy=" + host + ":" + port +
                                  " auth=" + (!string.IsNullOrEmpty(user)));
                        return;
                    }

                    FileLogger.Log("NET-AUDIT",
                        "[MULTI-OPEN] [PROXY-DIRECT] no free valid route slot; " +
                        "server-side same-egress restriction remains active.");
                }
                catch (Exception ex)
                {
                    FileLogger.Log("NET-AUDIT",
                        "[MULTI-OPEN] [PROXY-DIRECT] initialization failed: " + ex.Message);
                }
            }
        }

        private static FileStream TryAcquireMultiOpenProxySlot(string lockPrefix, int slot)
        {
            try
            {
                string lockPath = Path.Combine(Path.GetTempPath(), lockPrefix + slot + ".lock");
                return new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException)
            {
                return null;
            }
            catch (UnauthorizedAccessException)
            {
                return null;
            }
        }

        private static bool TryParseMultiOpenProxy(
            string line,
            out bool direct,
            out string host,
            out int port,
            out string user,
            out string password)
        {
            direct = false;
            host = string.Empty;
            port = 0;
            user = string.Empty;
            password = string.Empty;

            if (string.Equals(line, "direct", StringComparison.OrdinalIgnoreCase))
            {
                direct = true;
                return true;
            }

            string[] parts = line.Split(new char[] { '|' });
            if (parts.Length < 3 || !string.Equals(parts[0].Trim(), "socks5", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            host = parts[1].Trim();
            if (string.IsNullOrEmpty(host) || !int.TryParse(parts[2].Trim(), out port) || port <= 0 || port > 65535)
            {
                return false;
            }

            if (parts.Length > 3) user = parts[3];
            if (parts.Length > 4) password = parts[4];
            return string.IsNullOrEmpty(user) == string.IsNullOrEmpty(password);
        }

        private static void Socks5Connect(Socket socket, string targetHost, int targetPort, string user, string password)
        {
            bool useAuth = !string.IsNullOrEmpty(user);
            SendSocketBytes(socket, new byte[] { 5, 1, useAuth ? (byte)2 : (byte)0 });
            byte[] greeting = ReceiveSocketBytes(socket, 2);
            if (greeting[0] != 5 || greeting[1] == 255)
            {
                throw new InvalidOperationException("SOCKS5 method negotiation rejected");
            }

            if (useAuth)
            {
                if (greeting[1] != 2)
                {
                    throw new InvalidOperationException("SOCKS5 username/password authentication unavailable");
                }

                byte[] userBytes = Encoding.UTF8.GetBytes(user);
                byte[] passwordBytes = Encoding.UTF8.GetBytes(password);
                if (userBytes.Length == 0 || userBytes.Length > 255 || passwordBytes.Length == 0 || passwordBytes.Length > 255)
                {
                    throw new InvalidOperationException("SOCKS5 credentials must be 1-255 UTF-8 bytes");
                }

                byte[] authRequest = new byte[3 + userBytes.Length + passwordBytes.Length];
                int authOffset = 0;
                authRequest[authOffset++] = 1;
                authRequest[authOffset++] = (byte)userBytes.Length;
                Array.Copy(userBytes, 0, authRequest, authOffset, userBytes.Length);
                authOffset += userBytes.Length;
                authRequest[authOffset++] = (byte)passwordBytes.Length;
                Array.Copy(passwordBytes, 0, authRequest, authOffset, passwordBytes.Length);
                SendSocketBytes(socket, authRequest);

                byte[] authReply = ReceiveSocketBytes(socket, 2);
                if (authReply[1] != 0)
                {
                    throw new InvalidOperationException("SOCKS5 authentication rejected");
                }
            }
            else if (greeting[1] != 0)
            {
                throw new InvalidOperationException("SOCKS5 no-authentication method unavailable");
            }

            byte[] hostBytes = Encoding.ASCII.GetBytes(targetHost);
            if (hostBytes.Length == 0 || hostBytes.Length > 255)
            {
                throw new InvalidOperationException("SOCKS5 target host length is invalid");
            }

            byte[] request = new byte[7 + hostBytes.Length];
            request[0] = 5;
            request[1] = 1;
            request[2] = 0;
            request[3] = 3;
            request[4] = (byte)hostBytes.Length;
            Array.Copy(hostBytes, 0, request, 5, hostBytes.Length);
            request[request.Length - 2] = (byte)((targetPort >> 8) & 255);
            request[request.Length - 1] = (byte)(targetPort & 255);
            SendSocketBytes(socket, request);

            byte[] reply = ReceiveSocketBytes(socket, 4);
            if (reply[0] != 5 || reply[1] != 0)
            {
                throw new InvalidOperationException("SOCKS5 connect rejected, code=" + reply[1]);
            }

            int addressLength;
            if (reply[3] == 1)
            {
                addressLength = 4;
            }
            else if (reply[3] == 4)
            {
                addressLength = 16;
            }
            else if (reply[3] == 3)
            {
                addressLength = ReceiveSocketBytes(socket, 1)[0];
            }
            else
            {
                throw new InvalidOperationException("SOCKS5 returned an invalid address type");
            }

            ReceiveSocketBytes(socket, addressLength + 2);
        }

        private static void SendSocketBytes(Socket socket, byte[] data)
        {
            int sent = 0;
            while (sent < data.Length)
            {
                int count = socket.Send(data, sent, data.Length - sent, SocketFlags.None);
                if (count <= 0) throw new SocketException();
                sent += count;
            }
        }

        private static byte[] ReceiveSocketBytes(Socket socket, int count)
        {
            byte[] data = new byte[count];
            int received = 0;
            while (received < count)
            {
                int read = socket.Receive(data, received, count - received, SocketFlags.None);
                if (read <= 0) throw new SocketException();
                received += read;
            }
            return data;
        }

        private static string UnwrapExceptionMessage(Exception ex)
        {
            while (ex is TargetInvocationException && ex.InnerException != null)
            {
                ex = ex.InnerException;
            }
            return ex.Message;
        }

        private static void MultiOpen_RequestEnterLobbyPrefix(global::LobbyConnection __instance, ulong __0)
        {
            if (!Settings.MultiOpenEnabled) return;

            try
            {
                string uc = Settings.MultiOpenAswcIsolationEnabled ? ReadIsolatedUc() : ReadOriginalUc();
                string openid = SafeGlobalString(global::GlobalStatic.openid);
                if (string.IsNullOrEmpty(openid))
                {
                    openid = GetWebApiInfo("openid");
                }
                string procpara = GetWebApiInfo("procpara");
                string identityHash = GetMultiOpenIdentityHash();

                Settings.MultiOpenLastOriginalUcHash = HashShort(ReadOriginalUc());
                Settings.MultiOpenLastIsolatedUcHash = HashShort(uc);
                Settings.MultiOpenLastOpenIdHash = HashShort(openid);
                Settings.MultiOpenLastProcParaHash = HashShort(procpara);
                Settings.MultiOpenLastIdentityHash = identityHash;
                Settings.MultiOpenLastAswcPathHash = _multiOpenAswcPathHash ?? string.Empty;

                FileLogger.Log("NET-AUDIT",
                    "[MULTI-OPEN] [REQUEST-ENTER-LOBBY] pid=" + CurrentPid() +
                    " characterId=" + __0 +
                    " state=" + (__instance == null ? "<null>" : __instance.state.ToString()) +
                    " openidHash=" + Settings.MultiOpenLastOpenIdHash +
                    " procparaHash=" + Settings.MultiOpenLastProcParaHash +
                    " identityHash=" + identityHash +
                    " ucHash=" + Settings.MultiOpenLastIsolatedUcHash +
                    " aswcPathHash=" + Settings.MultiOpenLastAswcPathHash +
                    " isolated=" + Settings.MultiOpenAswcIsolationEnabled);
            }
            catch (Exception ex)
            {
                FileLogger.Log("NET-AUDIT", "[MULTI-OPEN] [REQUEST-ENTER-LOBBY] log error: " + ex.Message);
            }
        }

        private static bool MultiOpen_GetUcPrefix(ref string __result)
        {
            if (!Settings.MultiOpenEnabled || !Settings.MultiOpenAswcIsolationEnabled)
            {
                return true;
            }

            try
            {
                string isolatedUc = ReadIsolatedUc();
                // First use is initialized from a valid shared ASWC value. Runtime logs proved
                // that sending an empty UC can enter the lobby briefly but is then rejected by
                // the server with a force-disconnect, without receiving a replacement UC.
                __result = isolatedUc;

                Settings.MultiOpenLastOriginalUcHash = HashShort(ReadOriginalUc());
                Settings.MultiOpenLastIsolatedUcHash = HashShort(isolatedUc);
                Settings.MultiOpenLastIdentityHash = GetMultiOpenIdentityHash();
                Settings.MultiOpenLastAswcPathHash = _multiOpenAswcPathHash ?? string.Empty;

                if (_multiOpenUcLogCount < 20)
                {
                    _multiOpenUcLogCount++;
                    FileLogger.Log("NET-AUDIT",
                        "[MULTI-OPEN] [GET-UC-ISOLATED] pid=" + CurrentPid() +
                        " isolatedLen=" + SafeLen(isolatedUc) +
                        " isolatedUcHash=" + Settings.MultiOpenLastIsolatedUcHash +
                        " originalUcHash=" + Settings.MultiOpenLastOriginalUcHash +
                        " identityHash=" + Settings.MultiOpenLastIdentityHash +
                        " aswcPathHash=" + Settings.MultiOpenLastAswcPathHash);
                }

                return false;
            }
            catch (Exception ex)
            {
                FileLogger.Log("NET-AUDIT", "[MULTI-OPEN] [GET-UC] error: " + ex.Message);
                return true;
            }
        }

        private static bool MultiOpen_SetUcPrefix(string __0, ref string __result)
        {
            if (!Settings.MultiOpenEnabled || !Settings.MultiOpenAswcIsolationEnabled)
            {
                return true;
            }

            try
            {
                Settings.MultiOpenLastServerUcHash = HashShort(__0);
                Settings.MultiOpenLastIdentityHash = GetMultiOpenIdentityHash();
                Settings.MultiOpenLastAswcPathHash = _multiOpenAswcPathHash ?? string.Empty;
                __result = __0;

                if (string.IsNullOrEmpty(__0))
                {
                    string existingUc = ReadIsolatedUc();
                    Settings.MultiOpenLastIsolatedUcHash = HashShort(existingUc);
                    if (!string.IsNullOrEmpty(existingUc))
                    {
                        FileLogger.Log("NET-AUDIT",
                            "[MULTI-OPEN] [SET-UC-EMPTY-PRESERVED] pid=" + CurrentPid() +
                            " existingLen=" + SafeLen(existingUc) +
                            " existingUcHash=" + Settings.MultiOpenLastIsolatedUcHash +
                            " identityHash=" + Settings.MultiOpenLastIdentityHash +
                            " aswcPathHash=" + Settings.MultiOpenLastAswcPathHash);
                        return false;
                    }
                }

                Settings.MultiOpenLastIsolatedUcHash = Settings.MultiOpenLastServerUcHash;
                WriteIsolatedUc(__0);

                FileLogger.Log("NET-AUDIT",
                    "[MULTI-OPEN] [SET-UC-ISOLATED] pid=" + CurrentPid() +
                    " serverUcLen=" + SafeLen(__0) +
                    " serverUcHash=" + Settings.MultiOpenLastServerUcHash +
                    " identityHash=" + Settings.MultiOpenLastIdentityHash +
                    " aswcPathHash=" + Settings.MultiOpenLastAswcPathHash);
                return false;
            }
            catch (Exception ex)
            {
                FileLogger.Log("NET-AUDIT", "[MULTI-OPEN] [SET-UC] error: " + ex.Message);
                return true;
            }
        }

        private static void MultiOpen_ResponseEnterLobbyPostfix(global::LobbyConnection __instance)
        {
            if (!Settings.MultiOpenEnabled) return;

            try
            {
                string serverUc = string.Empty;
                ulong characterId = 0UL;
                string characterName = string.Empty;

                if (__instance != null)
                {
                    serverUc = Traverse.Create(__instance).Field("uc").GetValue<string>();
                    characterId = __instance.character_id;
                    characterName = __instance.character_name;
                }

                Settings.MultiOpenLastServerUcHash = HashShort(serverUc);
                FileLogger.Log("NET-AUDIT",
                    "[MULTI-OPEN] [RESPONSE-ENTER-LOBBY] pid=" + CurrentPid() +
                    " state=" + (__instance == null ? "<null>" : __instance.state.ToString()) +
                    " characterId=" + characterId +
                    " name=" + TrimLog(characterName, 48) +
                    " serverUcHash=" + Settings.MultiOpenLastServerUcHash +
                    " identityHash=" + GetMultiOpenIdentityHash() +
                    " isolatedUcHash=" + Settings.MultiOpenLastIsolatedUcHash +
                    " aswcPathHash=" + (_multiOpenAswcPathHash ?? string.Empty));
            }
            catch (Exception ex)
            {
                FileLogger.Log("NET-AUDIT", "[MULTI-OPEN] [RESPONSE-ENTER-LOBBY] log error: " + ex.Message);
            }
        }

        private static void MultiOpen_ResponseGameEnterPostfix(global::ChannelConnection __instance)
        {
            if (!Settings.MultiOpenEnabled) return;

            try
            {
                int code = _multiOpenLastErrorCode;
                string tag = code == 0 ? "[GAME-ENTER-OK]" : "[GAME-ENTER-DENY]";
                FileLogger.Log("NET-AUDIT",
                    "[MULTI-OPEN] " + tag +
                    " pid=" + CurrentPid() +
                    " code=" + code +
                    " state=" + (__instance == null ? "<null>" : __instance.state.ToString()) +
                    " gameState=" + (__instance == null ? "<null>" : __instance.game_state.ToString()) +
                    " beyondTime=" + (global::GameApp.Instance == null ? (byte)0 : global::GameApp.Instance.beyondTime));
            }
            catch (Exception ex)
            {
                FileLogger.Log("NET-AUDIT", "[MULTI-OPEN] [GAME-ENTER] log error: " + ex.Message);
            }
        }

        private static bool MultiOpen_NotifyRoomKickClientPrefix(global::ChannelConnection __instance)
        {
            if (!Settings.MultiOpenEnabled)
            {
                return true;
            }

            global::NetworkStream stream = null;
            int savedPosition = -1;
            ulong targetId = 0UL;
            uint reason = 0U;
            bool isSelf = false;

            try
            {
                stream = GetNetworkStream(__instance);
                if (stream != null)
                {
                    savedPosition = stream.read_position;
                }

                if (__instance != null)
                {
                    targetId = __instance.ReadUInt64();
                    reason = __instance.ReadUInt();
                    isSelf = targetId == __instance.character_id;
                }

                _multiOpenRoomKickCount++;
                FileLogger.Log("NET-AUDIT",
                    "[MULTI-OPEN] [ROOM-KICK] #" + _multiOpenRoomKickCount +
                    " pid=" + CurrentPid() +
                    " targetId=" + targetId +
                    " selfId=" + (__instance == null ? 0UL : __instance.character_id) +
                    " reason=" + reason +
                    " isSelf=" + isSelf +
                    " block=" + Settings.MultiOpenBlockRoomKickClient);

                if (Settings.MultiOpenBlockRoomKickClient && isSelf)
                {
                    FileLogger.Log("NET-AUDIT", "[MULTI-OPEN] [ROOM-KICK-BLOCKED] consumed local kick handler.");
                    return false;
                }
            }
            catch (Exception ex)
            {
                FileLogger.Log("NET-AUDIT", "[MULTI-OPEN] [ROOM-KICK] inspect error: " + ex.Message);
            }

            try
            {
                if (stream != null && savedPosition >= 0)
                {
                    stream.read_position = savedPosition;
                }
            }
            catch
            {
            }

            return true;
        }

        private static bool MultiOpen_LauncherOnClientMessagePrefix(global::LaucherConnection __instance)
        {
            if (!Settings.MultiOpenEnabled || !Settings.MultiOpenBlockLauncherProcessExit)
            {
                return true;
            }

            global::BuffReader reader = null;
            int savedPosition = -1;
            byte msg = 0;

            try
            {
                reader = Traverse.Create(__instance).Field("reader").GetValue<global::BuffReader>();
                if (reader == null)
                {
                    return true;
                }

                savedPosition = reader.start_index;
                msg = reader.ReadByte();
                string msgName = SafeServerMessageName(msg);
                if (msg != (byte)global::ServerMessage.SM_PROCESS_DATA)
                {
                    reader.start_index = savedPosition;
                    if (ShouldLog(ref _multiOpenLauncherMessageLogCount, 80, 200))
                    {
                        FileLogger.Log("NET-AUDIT",
                            "[MULTI-OPEN] [LAUNCHER-MSG-PASS] #" + _multiOpenLauncherMessageLogCount +
                            " pid=" + CurrentPid() +
                            " msg=" + msg +
                            " msgName=" + msgName +
                            " stack=" + TrimLog(CaptureStack(10), ExitStackPreviewChars));
                    }
                    return true;
                }

                string processData = string.Empty;
                string processName = string.Empty;
                try
                {
                    processData = reader.ReadString();
                    if (processData != null)
                    {
                        processData = processData.Replace(" ", string.Empty);
                    }
                    processName = reader.ReadString();
                }
                catch (Exception readEx)
                {
                    processData = "<read-error:" + readEx.Message + ">";
                }

                _multiOpenLauncherBlockCount++;
                FileLogger.Log("NET-AUDIT",
                    "[MULTI-OPEN] [LAUNCHER-PROCESS-DATA-BLOCKED] #" + _multiOpenLauncherBlockCount +
                    " pid=" + CurrentPid() +
                    " msg=" + msg +
                    " msgName=" + msgName +
                    " processName=" + TrimLog(processName, 80) +
                    " processDataHash=" + HashShort(processData) +
                    " processDataLen=" + SafeLen(processData) +
                    " stack=" + TrimLog(CaptureStack(10), ExitStackPreviewChars));
                return false;
            }
            catch (Exception ex)
            {
                try
                {
                    if (reader != null && savedPosition >= 0)
                    {
                        reader.start_index = savedPosition;
                    }
                }
                catch
                {
                }

                FileLogger.Log("NET-AUDIT",
                    "[MULTI-OPEN] [LAUNCHER-MSG] inspect error msg=" + msg +
                    " error=" + ex.Message +
                    " stack=" + TrimLog(CaptureStack(10), ExitStackPreviewChars));
                return true;
            }
        }

        private static bool MultiOpen_LauncherClosePrefix(global::LaucherConnection __instance)
        {
            if (!Settings.MultiOpenEnabled)
            {
                return true;
            }

            if (ShouldLog(ref _multiOpenLauncherCloseLogCount, 80, 200))
            {
                FileLogger.Log("NET-AUDIT",
                    "[MULTI-OPEN] [LAUNCHER-CLOSE] #" + _multiOpenLauncherCloseLogCount +
                    " pid=" + CurrentPid() +
                    " isConnected=" + SafeLauncherConnected(__instance) +
                    " stack=" + TrimLog(CaptureStack(10), ExitStackPreviewChars));
            }

            return true;
        }

        private static bool MultiOpen_LauncherUpdatePrefix(global::LaucherConnection __instance)
        {
            if (!Settings.MultiOpenEnabled)
            {
                return true;
            }

            if (ShouldLog(ref _multiOpenLauncherUpdateLogCount, 3, 60000))
            {
                FileLogger.Log("NET-AUDIT",
                    "[MULTI-OPEN] [LAUNCHER-UPDATE] #" + _multiOpenLauncherUpdateLogCount +
                    " pid=" + CurrentPid() +
                    " isConnected=" + SafeLauncherConnected(__instance) +
                    " stack=" + TrimLog(CaptureStack(10), ExitStackPreviewChars));
            }

            return true;
        }

        private static bool MultiOpen_GameAppExitAppPrefix()
        {
            if (!Settings.MultiOpenEnabled || !Settings.MultiOpenBlockLauncherProcessExit)
            {
                return true;
            }

            try
            {
                string caller = CaptureCaller();
                string stack = CaptureStack(12);
                bool block = ShouldBlockLocalExit(caller, stack);
                string tag = block ? "[EXITAPP-BLOCKED]" : "[EXITAPP-PASS]";

                FileLogger.Log("NET-AUDIT",
                    "[MULTI-OPEN] " + tag +
                    " pid=" + CurrentPid() +
                    " caller=" + caller +
                    " stack=" + TrimLog(stack, ExitStackPreviewChars));

                if (block)
                {
                    return false;
                }
            }
            catch (Exception ex)
            {
                FileLogger.Log("NET-AUDIT", "[MULTI-OPEN] [EXITAPP] log error: " + ex.Message);
            }

            return true;
        }

        private static bool MultiOpen_GameAppExitOnCheatDetectedPrefix()
        {
            if (!Settings.MultiOpenEnabled)
            {
                return true;
            }

            _multiOpenExitOnCheatLogCount++;
            bool block = Settings.MultiOpenBlockLauncherProcessExit;
            FileLogger.Log("NET-AUDIT",
                "[MULTI-OPEN] " + (block ? "[EXIT-ON-CHEAT-DETECTED-BLOCKED]" : "[EXIT-ON-CHEAT-DETECTED-PASS]") +
                " #" + _multiOpenExitOnCheatLogCount +
                " pid=" + CurrentPid() +
                " stack=" + TrimLog(CaptureStack(12), ExitStackPreviewChars));

            return !block;
        }

        private static void MultiOpen_GameAppOnApplicationQuitPrefix()
        {
            if (!Settings.MultiOpenEnabled)
            {
                return;
            }

            FileLogger.Log("NET-AUDIT",
                "[MULTI-OPEN] [GAMEAPP-ON-APPLICATION-QUIT] pid=" + CurrentPid() +
                " stack=" + TrimLog(CaptureStack(12), ExitStackPreviewChars));
        }

        private static bool MultiOpen_ApplicationQuitPrefix()
        {
            return MultiOpen_LogApplicationQuit("noExitCode");
        }

        private static bool MultiOpen_ApplicationQuitWithCodePrefix(int __0)
        {
            return MultiOpen_LogApplicationQuit("exitCode=" + __0);
        }

        private static bool MultiOpen_LogApplicationQuit(string detail)
        {
            if (!Settings.MultiOpenEnabled)
            {
                return true;
            }

            try
            {
                _multiOpenApplicationQuitLogCount++;
                string caller = CaptureCaller();
                string stack = CaptureStack(12);
                bool block = ShouldBlockLocalExit(caller, stack);

                FileLogger.Log("NET-AUDIT",
                    "[MULTI-OPEN] " + (block ? "[APPLICATION-QUIT-BLOCKED]" : "[APPLICATION-QUIT-PASS]") +
                    " #" + _multiOpenApplicationQuitLogCount +
                    " pid=" + CurrentPid() +
                    " " + detail +
                    " caller=" + caller +
                    " stack=" + TrimLog(stack, ExitStackPreviewChars));

                return !block;
            }
            catch (Exception ex)
            {
                FileLogger.Log("NET-AUDIT", "[MULTI-OPEN] [APPLICATION-QUIT] log error: " + ex.Message);
                return true;
            }
        }

        private static bool Protection_FilterErrorMessagePrefix(global::GameApp __instance, int __0)
        {
            try
            {
                string caller = CaptureCaller();
                _multiOpenLastErrorCode = __0;
                FileLogger.Log("NET-AUDIT",
                    "[ERROR-MSG] code=" + __0 +
                    " caller=" + caller);

                if (Settings.MultiOpenEnabled && __0 != 0)
                {
                    if (string.Equals(caller, "ChannelConnection.ResponseGameEnter", StringComparison.Ordinal))
                    {
                        FileLogger.Log("NET-AUDIT",
                            "[MULTI-OPEN] [GAME-ENTER-DENY] code=" + __0 +
                            " suppress=" + Settings.MultiOpenSuppressGameEnterError);
                        if (Settings.MultiOpenSuppressGameEnterError)
                        {
                            return false;
                        }
                    }
                    else if (string.Equals(caller, "LobbyConnection.ResponseChannelConnect", StringComparison.Ordinal))
                    {
                        FileLogger.Log("NET-AUDIT", "[MULTI-OPEN] [CHANNEL-DENY] code=" + __0);
                    }
                }

                if (__0 == -9998 && string.Equals(caller, "LobbyConnection.ForceDisconnect", StringComparison.Ordinal))
                {
                    FileLogger.Log("NET-AUDIT", "[ERROR-MSG] force-disconnect code=-9998 observed");
                }
            }
            catch (Exception ex)
            {
                FileLogger.Log("NET-AUDIT", "[ERROR-MSG] log error: " + ex.Message);
            }

            return true;
        }

        private static bool Protection_SkipInitializeDllDetectionPrefix()
        {
            try
            {
                _actkSuppressCount++;
                FileLogger.Log("NET-AUDIT",
                    "[ACTK] skipped GameApp.InitializeDllDetection #" + _actkSuppressCount);
            }
            catch (Exception ex)
            {
                FileLogger.Log("NET-AUDIT", "[ACTK] skip init error: " + ex.Message);
            }

            return false;
        }

        private static bool Protection_BlockActkOnCheatingDetectedPrefix()
        {
            try
            {
                _actkSuppressCount++;
                FileLogger.Log("NET-AUDIT",
                    "[ACTK] blocked OnCheatingDetected #" + _actkSuppressCount +
                    " caller=" + CaptureCaller());
            }
            catch (Exception ex)
            {
                FileLogger.Log("NET-AUDIT", "[ACTK] block OnCheatingDetected error: " + ex.Message);
            }

            return false;
        }

        private static bool Protection_BlockGameAppExitOnCheatDetectedPrefix()
        {
            if (!Settings.ExtendedAntiDetectionBypassEnabled) return true;

            try
            {
                _actkSuppressCount++;
                FileLogger.Log("NET-AUDIT",
                    "[ACTK] blocked GameApp.ExitOnCheatDetected #" + _actkSuppressCount +
                    " caller=" + CaptureCaller());
            }
            catch (Exception ex)
            {
                FileLogger.Log("NET-AUDIT", "[ACTK] block ExitOnCheatDetected error: " + ex.Message);
            }

            return false;
        }

        private static bool Protection_BlockActkAssemblyLoadedPrefix()
        {
            try
            {
                _actkSuppressCount++;
                FileLogger.Log("NET-AUDIT",
                    "[ACTK] blocked AssemblyLoaded hook #" + _actkSuppressCount +
                    " caller=" + CaptureCaller());
            }
            catch (Exception ex)
            {
                FileLogger.Log("NET-AUDIT", "[ACTK] block AssemblyLoaded error: " + ex.Message);
            }

            return false;
        }

        private static bool Protection_BlockNativeDllCheckPrefix()
        {
            try
            {
                _actkSuppressCount++;
                FileLogger.Log("NET-AUDIT",
                    "[ACTK] blocked NativeDllDetector.CheckDlls #" + _actkSuppressCount);
            }
            catch (Exception ex)
            {
                FileLogger.Log("NET-AUDIT", "[ACTK] block NativeDllDetector error: " + ex.Message);
            }

            return false;
        }

        private static void Protection_ClientFileMd5Prefix(ref bool __0)
        {
            if (!Settings.ExtendedAntiDetectionBypassEnabled) return;

            try
            {
                if (__0)
                {
                    __0 = false;
                    _clientFileMd5LogSuppressCount++;
                    FileLogger.Log("NET-AUDIT",
                        "[CLIENT-MD5] disabled local md5.log write #" + _clientFileMd5LogSuppressCount +
                        " source=GetClientBinarySummaryMd5");
                }
            }
            catch (Exception ex)
            {
                FileLogger.Log("NET-AUDIT", "[CLIENT-MD5] prefix error: " + ex.Message);
            }
        }

        private static void Protection_EncryptedClientFileMd5Prefix(ulong __0, ref bool __1)
        {
            if (!Settings.ExtendedAntiDetectionBypassEnabled) return;

            try
            {
                if (__1)
                {
                    __1 = false;
                    _clientFileMd5LogSuppressCount++;
                    FileLogger.Log("NET-AUDIT",
                        "[CLIENT-MD5] disabled local md5.log write #" + _clientFileMd5LogSuppressCount +
                        " source=GetEncryptedClientBinarySummaryMd5 characterIdHash=" + HashShort(__0.ToString()));
                }
            }
            catch (Exception ex)
            {
                FileLogger.Log("NET-AUDIT", "[CLIENT-MD5] encrypted prefix error: " + ex.Message);
            }
        }

        private static bool Protection_BlockExtendedDetectorPrefix(MethodBase __originalMethod)
        {
            if (!Settings.ExtendedAntiDetectionBypassEnabled) return true;

            try
            {
                if (ShouldLog(ref _extendedDetectorSuppressCount, 60, 500))
                {
                    FileLogger.Log("NET-AUDIT",
                        "[EXT-DETECT] blocked " + FormatMethod(__originalMethod) +
                        " count=" + _extendedDetectorSuppressCount +
                        " caller=" + CaptureCaller());
                }
            }
            catch (Exception ex)
            {
                FileLogger.Log("NET-AUDIT", "[EXT-DETECT] block error: " + ex.Message);
            }

            return false;
        }

        private static bool Protection_BoolFalsePrefix(ref bool __result, MethodBase __originalMethod)
        {
            if (!Settings.ExtendedAntiDetectionBypassEnabled) return true;

            __result = false;
            try
            {
                if (ShouldLog(ref _extendedDetectorSuppressCount, 60, 500))
                {
                    FileLogger.Log("NET-AUDIT",
                        "[EXT-DETECT] forced false " + FormatMethod(__originalMethod) +
                        " count=" + _extendedDetectorSuppressCount);
                }
            }
            catch (Exception ex)
            {
                FileLogger.Log("NET-AUDIT", "[EXT-DETECT] bool false error: " + ex.Message);
            }

            return false;
        }

        private static bool Protection_AllowAssemblyPrefix(ref bool __result, MethodBase __originalMethod)
        {
            if (!Settings.ExtendedAntiDetectionBypassEnabled) return true;

            __result = true;
            try
            {
                if (ShouldLog(ref _extendedDetectorSuppressCount, 60, 500))
                {
                    FileLogger.Log("NET-AUDIT",
                        "[EXT-DETECT] forced allow " + FormatMethod(__originalMethod) +
                        " count=" + _extendedDetectorSuppressCount);
                }
            }
            catch (Exception ex)
            {
                FileLogger.Log("NET-AUDIT", "[EXT-DETECT] allow assembly error: " + ex.Message);
            }

            return false;
        }

        private static bool Protection_IntZeroPrefix(ref int __result, MethodBase __originalMethod)
        {
            if (!Settings.ExtendedAntiDetectionBypassEnabled) return true;

            __result = 0;
            try
            {
                if (ShouldLog(ref _extendedDetectorSuppressCount, 60, 500))
                {
                    FileLogger.Log("NET-AUDIT",
                        "[EXT-DETECT] forced zero " + FormatMethod(__originalMethod) +
                        " count=" + _extendedDetectorSuppressCount);
                }
            }
            catch (Exception ex)
            {
                FileLogger.Log("NET-AUDIT", "[EXT-DETECT] int zero error: " + ex.Message);
            }

            return false;
        }

        private static string FormatMethod(MethodBase method)
        {
            if (method == null) return "<unknown>";
            Type type = method.DeclaringType;
            string typeName = type == null ? "<unknown>" : type.FullName;
            return typeName + "." + method.Name;
        }

        private static void Protection_LogLobbyOnDisconnectedPrefix(global::LobbyConnection __instance)
        {
            try
            {
                string state = __instance == null ? "<null>" : __instance.state.ToString();
                int msgType = 0;
                try
                {
                    if (__instance != null)
                    {
                        msgType = Traverse.Create(__instance).Field("msgType").GetValue<int>();
                    }
                }
                catch
                {
                }

                FileLogger.Log("NET-AUDIT",
                    "[LB-ONDISCONNECTED] state=" + state +
                    " msgType=" + msgType +
                    " caller=" + CaptureCaller());
            }
            catch (Exception ex)
            {
                FileLogger.Log("NET-AUDIT", "[LB-ONDISCONNECTED] log error: " + ex.Message);
            }
        }

        private static void Protection_LogChannelDisconnectPrefix(global::ChannelConnection __instance)
        {
            try
            {
                string state = __instance == null ? "<null>" : __instance.state.ToString();
                string gameState = __instance == null ? "<null>" : __instance.game_state.ToString();
                FileLogger.Log("NET-AUDIT",
                    "[CH-DISCONNECT] state=" + state +
                    " gameState=" + gameState +
                    " caller=" + CaptureCaller());
            }
            catch (Exception ex)
            {
                FileLogger.Log("NET-AUDIT", "[CH-DISCONNECT] log error: " + ex.Message);
            }
        }

        private static void Protection_LogChannelOnDisconnectedPrefix(global::ChannelConnection __instance)
        {
            try
            {
                string state = __instance == null ? "<null>" : __instance.state.ToString();
                string gameState = __instance == null ? "<null>" : __instance.game_state.ToString();
                FileLogger.Log("NET-AUDIT",
                    "[CH-ONDISCONNECTED] state=" + state +
                    " gameState=" + gameState +
                    " caller=" + CaptureCaller());
            }
            catch (Exception ex)
            {
                FileLogger.Log("NET-AUDIT", "[CH-ONDISCONNECTED] log error: " + ex.Message);
            }
        }

        private static global::NetworkStream GetNetworkStream(global::TcpConnection connection)
        {
            try
            {
                if (connection == null)
                {
                    return null;
                }

                return Traverse.Create(connection).Field("_stream").GetValue<global::NetworkStream>();
            }
            catch
            {
                return null;
            }
        }

        private static string GetMultiOpenIdentityHash()
        {
            if (!string.IsNullOrEmpty(_multiOpenIdentityHash))
            {
                return _multiOpenIdentityHash;
            }

            string openid = SafeGlobalString(global::GlobalStatic.openid);
            if (string.IsNullOrEmpty(openid))
            {
                openid = GetWebApiInfo("openid");
            }
            if (string.IsNullOrEmpty(openid))
            {
                openid = GetWebApiInfo("id");
            }

            string procpara = GetWebApiInfo("procpara");
            string account = GetWebApiInfo("account");
            string session = GetWebApiInfo("session");
            string userName = GetWebApiInfo("username");
            ulong platformUserId = 0UL;
            ulong loginUid = 0UL;
            try
            {
                if (global::LoginInfo.Instance != null)
                {
                    platformUserId = global::LoginInfo.Instance.user_id;
                    loginUid = global::LoginInfo.Instance.uid;
                }
            }
            catch
            {
            }

            // Prefer account-stable values. Launcher ports (lp/nlp), procpara and session are
            // deliberately excluded whenever a real account identifier is available because
            // they may change between launches and would create a fresh ASWC path every time.
            string seed;
            if (!string.IsNullOrEmpty(openid))
            {
                seed = "openid=" + openid;
            }
            else if (!string.IsNullOrEmpty(account))
            {
                seed = "account=" + account;
            }
            else if (platformUserId != 0UL)
            {
                seed = "userId=" + platformUserId;
            }
            else if (loginUid != 0UL)
            {
                seed = "loginUid=" + loginUid;
            }
            else if (!string.IsNullOrEmpty(userName))
            {
                seed = "username=" + userName;
            }
            else if (!string.IsNullOrEmpty(procpara))
            {
                seed = "procpara=" + procpara;
            }
            else if (!string.IsNullOrEmpty(session))
            {
                seed = "session=" + session;
            }
            else
            {
                // This should only occur before authentication. Keep processes separated rather
                // than returning to the shared ASWC path; the log makes the unstable fallback clear.
                seed = "pid-fallback=" + CurrentPid();
                FileLogger.Log("NET-AUDIT",
                    "[MULTI-OPEN] [IDENTITY-PID-FALLBACK] pid=" + CurrentPid());
            }

            _multiOpenIdentityHash = HashShort(seed);
            Settings.MultiOpenLastIdentityHash = _multiOpenIdentityHash;
            return _multiOpenIdentityHash;
        }

        private static string EnsureIsolatedAswcPath()
        {
            if (!string.IsNullOrEmpty(_multiOpenAswcPath))
            {
                return _multiOpenAswcPath;
            }

            string originalPath = global::GameApp.GetExeDirPath("aswc");
            string dir = Path.GetDirectoryName(originalPath);
            if (string.IsNullOrEmpty(dir))
            {
                dir = global::UnityEngine.Application.dataPath;
            }

            string identityHash = GetMultiOpenIdentityHash();
            // The path is stable for the account across process restarts. Older PID-suffixed
            // isolation files are intentionally left untouched and are no longer selected.
            _multiOpenAswcPath = Path.Combine(dir, "aswc.mo.account." + SafeFilePart(identityHash));
            _multiOpenAswcPathHash = HashShort(_multiOpenAswcPath);
            Settings.MultiOpenLastAswcPathHash = _multiOpenAswcPathHash;

            FileLogger.Log("NET-AUDIT",
                "[MULTI-OPEN] [ASWC-ISOLATION] pid=" + CurrentPid() +
                " identityHash=" + identityHash +
                " sharedPathHash=" + HashShort(originalPath) +
                " isolatedPathHash=" + _multiOpenAswcPathHash);

            return _multiOpenAswcPath;
        }

        private static string ReadIsolatedUc()
        {
            string path = EnsureIsolatedAswcPath();
            if (string.IsNullOrEmpty(path))
            {
                return string.Empty;
            }

            bool isolatedFileExists = File.Exists(path);
            if (isolatedFileExists)
            {
                string value = File.ReadAllText(path);
                value = string.IsNullOrEmpty(value) ? string.Empty : value.Trim();
                if (!string.IsNullOrEmpty(value))
                {
                    return value;
                }
            }

            string sharedUc = ReadOriginalUc();
            if (!string.IsNullOrEmpty(sharedUc))
            {
                File.WriteAllText(path, sharedUc);
                FileLogger.Log("NET-AUDIT",
                    "[MULTI-OPEN] [ASWC-ISOLATION-SEED] pid=" + CurrentPid() +
                    " identityHash=" + GetMultiOpenIdentityHash() +
                    " isolatedWasEmpty=" + isolatedFileExists +
                    " sharedUcLen=" + SafeLen(sharedUc) +
                    " sharedUcHash=" + HashShort(sharedUc) +
                    " isolatedPathHash=" + (_multiOpenAswcPathHash ?? string.Empty));
                return sharedUc;
            }

            FileLogger.Log("NET-AUDIT",
                "[MULTI-OPEN] [ASWC-ISOLATION-FIRST-USE] pid=" + CurrentPid() +
                " identityHash=" + GetMultiOpenIdentityHash() +
                " isolatedWasEmpty=" + isolatedFileExists +
                " sharedUcPresent=False" +
                " isolatedPathHash=" + (_multiOpenAswcPathHash ?? string.Empty));
            return string.Empty;
        }

        private static void WriteIsolatedUc(string uc)
        {
            string path = EnsureIsolatedAswcPath();
            if (string.IsNullOrEmpty(path))
            {
                throw new InvalidOperationException("isolated aswc path is empty");
            }

            File.WriteAllText(path, uc ?? string.Empty);
        }

        private static string SafeFilePart(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return "empty";
            }

            char[] chars = value.ToCharArray();
            char[] invalid = Path.GetInvalidFileNameChars();
            for (int i = 0; i < chars.Length; i++)
            {
                for (int j = 0; j < invalid.Length; j++)
                {
                    if (chars[i] == invalid[j])
                    {
                        chars[i] = '_';
                        break;
                    }
                }
            }

            return new string(chars);
        }

        private static string GetOrCreateVirtualUc(string originalUc)
        {
            return originalUc ?? string.Empty;
        }

        private static string ReadOriginalUc()
        {
            try
            {
                string path = global::GameApp.GetExeDirPath("aswc");
                string value = global::GameApp.ReadText(path);
                return string.IsNullOrEmpty(value) ? string.Empty : value.Trim();
            }
            catch (Exception ex)
            {
                FileLogger.Log("NET-AUDIT", "[MULTI-OPEN] [READ-ASWC] error: " + ex.Message);
                return string.Empty;
            }
        }

        private static string GetWebApiInfo(string key)
        {
            try
            {
                string value;
                if (global::GameApp.Instance != null &&
                    global::GameApp.Instance.Web_API_InfoList != null &&
                    global::GameApp.Instance.Web_API_InfoList.TryGetValue(key, out value))
                {
                    return value ?? string.Empty;
                }
            }
            catch
            {
            }

            string commandLineValue = GetCommandLineValue(key);
            if (!string.IsNullOrEmpty(commandLineValue))
            {
                return commandLineValue;
            }

            if (string.Equals(key, "openid", StringComparison.OrdinalIgnoreCase))
            {
                return GetCommandLineValue("id");
            }
            if (string.Equals(key, "openkey", StringComparison.OrdinalIgnoreCase))
            {
                return GetCommandLineValue("key");
            }

            return string.Empty;
        }

        private static string GetCommandLineValue(string key)
        {
            try
            {
                string[] args = Environment.GetCommandLineArgs();
                string prefix = key + "=";
                for (int i = 0; i < args.Length; i++)
                {
                    string arg = args[i] ?? string.Empty;
                    if (arg.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    {
                        return arg.Substring(prefix.Length);
                    }

                    string[] parts = arg.Split(',');
                    for (int j = 0; j < parts.Length; j++)
                    {
                        string part = parts[j] ?? string.Empty;
                        if (part.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                        {
                            return part.Substring(prefix.Length);
                        }
                    }
                }
            }
            catch
            {
            }

            return string.Empty;
        }

        private static string SafeGlobalString(string value)
        {
            return value ?? string.Empty;
        }

        private static int CurrentPid()
        {
            try
            {
                return Process.GetCurrentProcess().Id;
            }
            catch
            {
                return -1;
            }
        }

        private static bool ShouldLog(ref int counter, int firstCount, int everyCount)
        {
            counter++;
            if (counter <= firstCount)
            {
                return true;
            }

            return everyCount > 0 && (counter % everyCount) == 0;
        }

        private static bool ShouldBlockLocalExit(string caller, string stack)
        {
            if (!Settings.MultiOpenEnabled || !Settings.MultiOpenBlockLauncherProcessExit)
            {
                return false;
            }

            string value = (caller ?? string.Empty) + " " + (stack ?? string.Empty);
            return value.IndexOf("LaucherConnection", StringComparison.Ordinal) >= 0 ||
                   value.IndexOf("ExitOnCheatDetected", StringComparison.Ordinal) >= 0;
        }

        private static bool IsProtectedMultiOpenBattle(global::LobbyConnection lobby)
        {
            if (lobby == null || lobby.state != global::LobbyConnection.State.kInGame)
            {
                return false;
            }

            return IsNormalBattleMode();
        }

        private static bool IsNormalBattleMode()
        {
            try
            {
                global::Level level = global::ASSingleton<global::Level>.Instance;
                return level == null || level.game_type != global::RoomInfo.GameType.kGameTypeBoss;
            }
            catch
            {
                // During the loading transition the lobby state is already kInGame,
                // while Level may not be fully initialized yet. That path is normal battle.
                return true;
            }
        }

        private static string SafeServerMessageName(byte msg)
        {
            try
            {
                return ((global::ServerMessage)msg).ToString();
            }
            catch
            {
                return "<unknown>";
            }
        }

        private static bool SafeLauncherConnected(global::LaucherConnection instance)
        {
            try
            {
                if (instance == null)
                {
                    return false;
                }

                MethodInfo getter = instance.GetType().GetMethod("get_IsConnected",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static);
                if (getter == null)
                {
                    return false;
                }

                object target = getter.IsStatic ? null : instance;
                object value = getter.Invoke(target, null);
                return value is bool && (bool)value;
            }
            catch
            {
                return false;
            }
        }

        private static string HashShort(string text)
        {
            string full = HashHex(text);
            return full.Length <= 16 ? full : full.Substring(0, 16);
        }

        private static string HashHex(string text)
        {
            try
            {
                byte[] data = Encoding.UTF8.GetBytes(text ?? string.Empty);
                using (SHA256Managed sha = new SHA256Managed())
                {
                    byte[] hash = sha.ComputeHash(data);
                    StringBuilder sb = new StringBuilder(hash.Length * 2);
                    for (int i = 0; i < hash.Length; i++)
                    {
                        sb.Append(hash[i].ToString("x2"));
                    }
                    return sb.ToString();
                }
            }
            catch
            {
                return "hash-error";
            }
        }

        private static string HashBase64Url(string text)
        {
            try
            {
                byte[] data = Encoding.UTF8.GetBytes(text ?? string.Empty);
                using (SHA256Managed sha = new SHA256Managed())
                {
                    string value = Convert.ToBase64String(sha.ComputeHash(data));
                    return value.Replace('+', '-').Replace('/', '_').Replace("=", string.Empty);
                }
            }
            catch
            {
                return "hash-error";
            }
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

        private static string CaptureStack(int maxFrames)
        {
            try
            {
                StackTrace st = new StackTrace(2, false);
                List<string> frames = new List<string>();
                for (int i = 0; i < st.FrameCount; i++)
                {
                    MethodBase m = st.GetFrame(i).GetMethod();
                    if (m == null) continue;

                    Type dt = m.DeclaringType;
                    string tn = dt != null ? dt.FullName : string.Empty;
                    if (string.IsNullOrEmpty(tn)) continue;
                    if (tn.StartsWith("System.", StringComparison.Ordinal)) continue;
                    if (tn.StartsWith("UnityEngine.Debug", StringComparison.Ordinal)) continue;
                    if (tn.StartsWith("Harmony", StringComparison.Ordinal)) continue;
                    if (tn == typeof(HarmonyLoader).FullName) continue;

                    string frame = tn + "." + m.Name;
                    if (frames.Count == 0 || frames[frames.Count - 1] != frame)
                    {
                        frames.Add(frame);
                    }

                    if (frames.Count >= maxFrames)
                    {
                        break;
                    }
                }

                if (frames.Count > 0)
                {
                    return string.Join(" <= ", frames.ToArray());
                }
            }
            catch
            {
            }

            return "(unknown)";
        }
    }
}
