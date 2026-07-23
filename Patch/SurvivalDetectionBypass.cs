using System;
using System.Reflection;
using ASWDEBUG.Cheats.SurvivalBot;
using ASWDEBUG.Logger;
using Harmony;
using UnityEngine;

namespace ASWDEBUG.Patch
{
    internal static class SurvivalDetectionBypass
    {
        private const int SuspiciousPrecisionThresholdMm = 120;
        private const int HumanizedPrecisionMinMm = 120;
        private const int HumanizedPrecisionMaxMm = 300;
        private const int SuspiciousRunMinSamples = 3;

        private static int _blockedReportCount;
        private static int _blockedDetectorCount;
        private static int _sanitizedPayloadCount;
        private static int _syntheticTargetUid;
        private static float _syntheticSessionEnd;
        private static uint _syntheticPrng = 0x6D2B79F5u;
        private static int _syntheticPrecisionMm = 190;
        private static int _syntheticVelocityMm;
        private static int _historyCacheFrame = -1;
        private static int _historyCacheTargetUid;
        private static int _historyCacheHitUid;
        private static bool[] _historyCacheMask;
        private static short[] _historyCacheValues;

        internal static void Install(HarmonyInstance harmony)
        {
            Assembly assemblyCSharp = FindAssembly("Assembly-CSharp");
            if (assemblyCSharp == null)
            {
                FileLogger.Log("PATCH", "detection bypass skipped: Assembly-CSharp missing");
                return;
            }

            PatchAll(harmony, assemblyCSharp, "ChannelConnection", "PluginReport",
                "BlockDetectionReportPrefix");
            PatchAll(harmony, assemblyCSharp, "GunBaseController", "AssitToolCheck",
                "BlockDetectionReportPrefix");
            PatchAll(harmony, assemblyCSharp, "ChannelConnection", "ParseProcessCheck",
                "BlockProcessCheckPrefix");
            PatchAll(harmony, assemblyCSharp, "ProcessCheck", "CheckByBlackJosnTable",
                "SkipProcessScanPrefix");
            PatchAll(harmony, assemblyCSharp, "ProcessCheck", "CheckByUrl",
                "SkipProcessScanPrefix");
            PatchAll(harmony, assemblyCSharp, "ChannelConnection", "ParsePositionCheck",
                "BlockPositionCheckPrefix");
            PatchAll(harmony, assemblyCSharp, "ChannelConnection", "ParseKickOutByPlugin",
                "BlockKickOutByPluginPrefix");
            PatchAll(harmony, assemblyCSharp, "ChannelConnection", "ParseNotifyKickedByGM",
                "BlockKickNotificationPrefix");
            PatchAll(harmony, assemblyCSharp, "ChannelConnection", "ParseNotifyKickedByVote",
                "BlockKickNotificationPrefix");
            PatchAll(harmony, assemblyCSharp, "LobbyConnection", "RequestReport",
                "BlockDetectionReportPrefix");
            PatchAll(harmony, assemblyCSharp, "LobbyConnection", "RequestShutdown",
                "BlockDetectionReportPrefix");
            PatchAll(harmony, assemblyCSharp, "ClientFileMd5Checker",
                "GetClientBinarySummaryMd5",
                "DisableClientMd5LogPrefix");
            PatchAll(harmony, assemblyCSharp, "ClientFileMd5Checker",
                "GetEncryptedClientBinarySummaryMd5",
                "DisableEncryptedClientMd5LogPrefix");
            PatchAll(harmony, assemblyCSharp, "GameApp", "InitializeDllDetection",
                "BlockDetectorPrefix");
            PatchAll(harmony, assemblyCSharp, "GameApp", "ExitOnCheatDetected",
                "BlockDetectorPrefix");
            PatchAll(harmony, assemblyCSharp,
                "CodeStage.AntiCheat.Detectors.ActDetectorBase",
                "OnCheatingDetected",
                "BlockDetectorPrefix");
            InstallExtendedDetectorBypass(harmony, assemblyCSharp);
            PatchPayloadBuilders(harmony, assemblyCSharp);
            FileLogger.Log("PATCH",
                "Survival detection bypass installed; native AimAssistDetector lifecycle retained");
        }

        private static void InstallExtendedDetectorBypass(
            HarmonyInstance harmony,
            Assembly assemblyCSharp)
        {
            string[] detectorTypes =
            {
                "CodeStage.AntiCheat.Detectors.InjectionDetector",
                "CodeStage.AntiCheat.Detectors.ManagedAssemblyDetector",
                "CodeStage.AntiCheat.Detectors.NativeDllDetector",
                "CodeStage.AntiCheat.Detectors.ObscuredCheatingDetector",
                "CodeStage.AntiCheat.Detectors.SpeedHackDetector",
                "CodeStage.AntiCheat.Detectors.WallHackDetector"
            };
            string[] blockedMethods =
            {
                "Start",
                "Update",
                "FixedUpdate",
                "StartDetection",
                "StartDetectionInternal",
                "StartDetectionAutomatically",
                "ResumeDetector",
                "OnNewAssemblyLoaded",
                "OnAssemblyLoaded",
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
                for (int j = 0; j < blockedMethods.Length; j++)
                {
                    PatchAll(
                        harmony,
                        assemblyCSharp,
                        detectorTypes[i],
                        blockedMethods[j],
                        "BlockDetectorPrefix",
                        false);
                }
            }

            PatchAll(harmony, assemblyCSharp,
                "CodeStage.AntiCheat.Detectors.ActDetectorBase",
                "StartDetectionAutomatically",
                "BlockDetectorPrefix",
                false);
            PatchAll(harmony, assemblyCSharp,
                "CodeStage.AntiCheat.Detectors.ActDetectorBase",
                "ResumeDetector",
                "BlockDetectorPrefix",
                false);
            PatchAll(harmony, assemblyCSharp,
                "CodeStage.AntiCheat.Detectors.InjectionDetector",
                "AssemblyAllowed",
                "ForceTruePrefix",
                false);
            PatchAll(harmony, assemblyCSharp,
                "CodeStage.AntiCheat.Detectors.ManagedAssemblyDetector",
                "GetSuspiciousCount",
                "ForceZeroPrefix",
                false);
            PatchAll(harmony, assemblyCSharp,
                "CodeStage.AntiCheat.Detectors.NativeDllDetector",
                "get_IsRunning",
                "ForceFalsePrefix",
                false);
            PatchAll(harmony, assemblyCSharp,
                "CodeStage.AntiCheat.Detectors.WallHackDetector",
                "Detect",
                "ForceFalsePrefix",
                false);
            StopRuntimeDetector(
                assemblyCSharp,
                "CodeStage.AntiCheat.Detectors.ObscuredCheatingDetector");
        }

        private static void PatchPayloadBuilders(
            HarmonyInstance harmony,
            Assembly assemblyCSharp)
        {
            Type type = assemblyCSharp.GetType("ShootPayloadCrypt");
            if (type == null)
            {
                FileLogger.Log("PATCH", "detection bypass target missing: ShootPayloadCrypt");
                return;
            }

            MethodInfo prefix = AccessTools.Method(
                typeof(SurvivalDetectionBypass),
                "SanitizeShootPayloadPrefix");
            MethodInfo[] methods = type.GetMethods(
                BindingFlags.Static |
                BindingFlags.Instance |
                BindingFlags.Public |
                BindingFlags.NonPublic |
                BindingFlags.DeclaredOnly);
            int patched = 0;
            for (int i = 0; i < methods.Length; i++)
            {
                MethodInfo method = methods[i];
                if (method.Name != "BuildEncryptedPayload")
                {
                    continue;
                }

                ParameterInfo[] parameters = method.GetParameters();
                if (parameters.Length != 2 || parameters[1].ParameterType != typeof(int))
                {
                    continue;
                }

                try
                {
                    harmony.Patch(method, new HarmonyMethod(prefix), null, null);
                    patched++;
                }
                catch (Exception ex)
                {
                    FileLogger.Log("PATCH",
                        "payload sanitizer hook failed: " + ex.GetType().Name +
                        ": " + ex.Message);
                }
            }

            FileLogger.Log("PATCH", "shoot payload sanitizer hooks=" + patched);
        }

        private static void PatchAll(
            HarmonyInstance harmony,
            Assembly assembly,
            string typeName,
            string methodName,
            string prefixName)
        {
            PatchAll(
                harmony,
                assembly,
                typeName,
                methodName,
                prefixName,
                true);
        }

        private static void PatchAll(
            HarmonyInstance harmony,
            Assembly assembly,
            string typeName,
            string methodName,
            string prefixName,
            bool logMissing)
        {
            Type type = assembly.GetType(typeName);
            MethodInfo prefix = AccessTools.Method(
                typeof(SurvivalDetectionBypass),
                prefixName);
            if (type == null || prefix == null)
            {
                if (logMissing)
                {
                    FileLogger.Log("PATCH",
                        "detection bypass target missing: " +
                        typeName + "." + methodName);
                }
                return;
            }

            MethodInfo[] methods = type.GetMethods(
                BindingFlags.Static |
                BindingFlags.Instance |
                BindingFlags.Public |
                BindingFlags.NonPublic |
                BindingFlags.DeclaredOnly);
            int patched = 0;
            for (int i = 0; i < methods.Length; i++)
            {
                MethodInfo method = methods[i];
                if (method.Name != methodName || method.ContainsGenericParameters)
                {
                    continue;
                }

                try
                {
                    harmony.Patch(method, new HarmonyMethod(prefix), null, null);
                    patched++;
                }
                catch (Exception ex)
                {
                    FileLogger.Log("PATCH",
                        "detection bypass hook failed: " + typeName + "." +
                        methodName + " " + ex.GetType().Name + ": " + ex.Message);
                }
            }

            if (patched > 0 || logMissing)
            {
                FileLogger.Log("PATCH",
                    "detection bypass hooks=" + patched + " target=" +
                    typeName + "." + methodName);
            }
        }

        private static void StopRuntimeDetector(
            Assembly assembly,
            string typeName)
        {
            try
            {
                Type type = assembly.GetType(typeName);
                MethodInfo stop = type == null
                    ? null
                    : type.GetMethod(
                        "StopDetection",
                        BindingFlags.Static |
                        BindingFlags.Public |
                        BindingFlags.NonPublic,
                        null,
                        Type.EmptyTypes,
                        null);
                if (stop != null)
                {
                    stop.Invoke(null, null);
                    FileLogger.Log("DETECTION",
                        "stopped runtime detector " + typeName);
                }
            }
            catch (Exception ex)
            {
                FileLogger.Log("DETECTION",
                    "runtime detector stop failed " + typeName + ": " +
                    ex.GetType().Name + ": " + ex.Message);
            }
        }

        private static bool BlockDetectionReportPrefix(MethodBase __originalMethod)
        {
            _blockedReportCount++;
            if (_blockedReportCount <= 8 || _blockedReportCount % 200 == 0)
            {
                FileLogger.Log("DETECTION",
                    "blocked report/check count=" + _blockedReportCount +
                    " source=" + FormatMethod(__originalMethod));
            }
            return false;
        }

        private static bool BlockDetectorPrefix(MethodBase __originalMethod)
        {
            _blockedDetectorCount++;
            if (_blockedDetectorCount <= 12 || _blockedDetectorCount % 200 == 0)
            {
                FileLogger.Log("DETECTION",
                    "blocked detector count=" + _blockedDetectorCount +
                    " source=" + FormatMethod(__originalMethod));
            }
            return false;
        }

        private static bool ForceTruePrefix(
            ref bool __result,
            MethodBase __originalMethod)
        {
            __result = true;
            TraceForcedDetectorResult(__originalMethod, "true");
            return false;
        }

        private static bool ForceFalsePrefix(
            ref bool __result,
            MethodBase __originalMethod)
        {
            __result = false;
            TraceForcedDetectorResult(__originalMethod, "false");
            return false;
        }

        private static bool ForceZeroPrefix(
            ref int __result,
            MethodBase __originalMethod)
        {
            __result = 0;
            TraceForcedDetectorResult(__originalMethod, "0");
            return false;
        }

        private static void TraceForcedDetectorResult(
            MethodBase method,
            string result)
        {
            _blockedDetectorCount++;
            if (_blockedDetectorCount <= 12 ||
                _blockedDetectorCount % 200 == 0)
            {
                FileLogger.Log("DETECTION",
                    "forced detector result=" + result +
                    " count=" + _blockedDetectorCount +
                    " source=" + FormatMethod(method));
            }
        }

        private static bool BlockProcessCheckPrefix(global::NetworkStream __0)
        {
            try
            {
                byte mode = 255;
                int payloadLength = 0;
                if (__0 != null)
                {
                    mode = __0.ReadByte();
                    string payload = __0.ReadString();
                    payloadLength = payload == null ? 0 : payload.Length;
                }
                FileLogger.Log("DETECTION",
                    "process check suppressed mode=" + mode +
                    " payloadLength=" + payloadLength);
            }
            catch (Exception ex)
            {
                FileLogger.Log("DETECTION",
                    "process check consume failed: " + ex.GetType().Name +
                    ": " + ex.Message);
            }
            return false;
        }

        private static bool SkipProcessScanPrefix(
            string __0,
            Action<global::ProcessCheckInfo> __1)
        {
            try
            {
                if (__1 != null)
                {
                    __1(null);
                }
            }
            catch (Exception ex)
            {
                FileLogger.Log("DETECTION",
                    "process scan callback failed: " + ex.GetType().Name +
                    ": " + ex.Message);
            }
            return false;
        }

        private static bool BlockPositionCheckPrefix(
            global::ChannelConnection __instance)
        {
            try
            {
                byte uid = __instance == null
                    ? (byte)0
                    : __instance.ReadByte();
                int marker = __instance == null
                    ? 0
                    : __instance.ReadInt();
                global::NetworkStream stream = __instance == null
                    ? null
                    : Traverse.Create(__instance)
                        .Field("_stream")
                        .GetValue<global::NetworkStream>();
                Vector3 position = stream == null
                    ? default(Vector3)
                    : global::ConnectionDef.ReadCharacterPosition(stream);
                byte flags = __instance == null
                    ? (byte)0
                    : __instance.ReadByte();
                FileLogger.Log("DETECTION",
                    "position check suppressed uid=" + uid +
                    " marker=" + marker +
                    " position=" + position +
                    " flags=0x" + flags.ToString("X2"));
            }
            catch (Exception ex)
            {
                FileLogger.Log("DETECTION",
                    "position check consume failed: " + ex.GetType().Name +
                    ": " + ex.Message);
            }
            return false;
        }

        private static bool BlockKickOutByPluginPrefix(
            global::ChannelConnection __instance)
        {
            byte mode = 0;
            try
            {
                if (__instance != null)
                {
                    mode = __instance.ReadByte();
                }
            }
            catch
            {
            }
            FileLogger.Log("DETECTION",
                "plugin kick notification suppressed mode=" + mode);
            return false;
        }

        private static bool BlockKickNotificationPrefix(
            MethodBase __originalMethod)
        {
            FileLogger.Log("DETECTION",
                "kick notification suppressed source=" +
                FormatMethod(__originalMethod));
            return false;
        }

        private static void DisableClientMd5LogPrefix(ref bool __0)
        {
            __0 = false;
        }

        private static void DisableEncryptedClientMd5LogPrefix(ref bool __1)
        {
            __1 = false;
        }

        private static void SanitizeShootPayloadPrefix(object __0, int __1)
        {
            try
            {
                int adjusted = NormalizeAimReportFields(__0);
                if (adjusted <= 0)
                {
                    return;
                }

                _sanitizedPayloadCount++;
                if (_sanitizedPayloadCount <= 8 ||
                    _sanitizedPayloadCount % 100 == 0)
                {
                    FileLogger.Log("DETECTION",
                        "aim history humanized payload=" +
                        _sanitizedPayloadCount + " adjusted=" + adjusted +
                        " spread=" + __1);
                }
            }
            catch (Exception ex)
            {
                FileLogger.Log("DETECTION",
                    "aim payload sanitizer failed: " + ex.GetType().Name +
                    ": " + ex.Message);
            }
        }

        private static int NormalizeAimReportFields(object hitMessage)
        {
            if (hitMessage == null ||
                !HasRuntimeField(hitMessage, "aim_report_version"))
            {
                return -1;
            }

            int version = ReadRuntimeFieldInt(
                hitMessage,
                "aim_report_version",
                8);
            if ((version & 0x80) == 0 || !SurvivalBotManager.Enabled)
            {
                return 0;
            }

            int targetUid = ReadRuntimeFieldInt(
                hitMessage,
                "aim_target_uid",
                0);
            PrepareSyntheticSession(targetUid);

            short[] samples = ReadRuntimeShortArray(
                hitMessage,
                "aim_precision_samples");
            if (samples == null || samples.Length == 0)
            {
                return 0;
            }

            int hitUid = ReadRuntimeFieldInt(hitMessage, "uid", 0);
            short[] normalizedSamples;
            int adjusted = HumanizeHistoricalPrecisionRuns(
                samples,
                targetUid,
                hitUid,
                out normalizedSamples);
            if (adjusted > 0 && normalizedSamples != null)
            {
                TrySetRuntimeField(
                    hitMessage,
                    "aim_precision_samples",
                    normalizedSamples);
            }

            _syntheticSessionEnd = Time.realtimeSinceStartup;
            return adjusted;
        }

        private static void PrepareSyntheticSession(int targetUid)
        {
            float now = Time.realtimeSinceStartup;
            bool expired = _syntheticSessionEnd <= 0f ||
                now - _syntheticSessionEnd > 1.25f;
            if (!expired && _syntheticTargetUid == targetUid)
            {
                return;
            }

            _syntheticTargetUid = targetUid;
            _syntheticSessionEnd = now;
            _syntheticPrng ^= unchecked((uint)(targetUid * 397));
            _syntheticPrng ^= unchecked((uint)Environment.TickCount);
            _syntheticPrng ^= unchecked((uint)Time.frameCount * 0x9E3779B9u);
            _syntheticPrecisionMm = 165 + (int)(NextSyntheticUInt() % 111u);
            _syntheticVelocityMm = -8 + (int)(NextSyntheticUInt() % 17u);
            _historyCacheFrame = -1;
            _historyCacheMask = null;
            _historyCacheValues = null;
        }

        private static int HumanizeHistoricalPrecisionRuns(
            short[] samples,
            int targetUid,
            int hitUid,
            out short[] normalizedSamples)
        {
            normalizedSamples = null;
            bool[] adjustmentMask = BuildPrecisionAdjustmentMask(samples);
            int adjustedCount = CountTrue(adjustmentMask);
            if (adjustedCount == 0)
            {
                return 0;
            }

            if (TryReuseHistoryCache(
                samples,
                adjustmentMask,
                targetUid,
                hitUid,
                out normalizedSamples))
            {
                return adjustedCount;
            }

            normalizedSamples = (short[])samples.Clone();
            int index = 0;
            while (index < adjustmentMask.Length)
            {
                if (!adjustmentMask[index])
                {
                    index++;
                    continue;
                }

                int runStart = index;
                while (index < adjustmentMask.Length &&
                       adjustmentMask[index])
                {
                    index++;
                }
                int runEnd = index;

                int tailMillimeters;
                if (runEnd >= samples.Length ||
                    !TryDecodePrecisionMillimeters(
                        samples[runEnd],
                        out tailMillimeters))
                {
                    tailMillimeters =
                        55 + (int)(NextSyntheticUInt() % 61u);
                }

                int observedBeforeRun;
                if (runStart > 0 &&
                    TryDecodePrecisionMillimeters(
                        samples[runStart - 1],
                        out observedBeforeRun) &&
                    observedBeforeRun >= SuspiciousPrecisionThresholdMm &&
                    observedBeforeRun <= 330)
                {
                    _syntheticPrecisionMm =
                        (_syntheticPrecisionMm * 2 + observedBeforeRun) / 3;
                }

                int startMillimeters = Mathf.Clamp(
                    _syntheticPrecisionMm +
                    (int)(NextSyntheticUInt() % 31u) - 15,
                    HumanizedPrecisionMinMm + 25,
                    HumanizedPrecisionMaxMm);
                int endMillimeters = Mathf.Clamp(
                    tailMillimeters + 30 +
                    (int)(NextSyntheticUInt() % 51u),
                    40,
                    175);
                endMillimeters = Mathf.Min(
                    endMillimeters,
                    startMillimeters - 8);
                int runLength = runEnd - runStart;
                int previousMillimeters = startMillimeters;

                for (int offset = 0; offset < runLength; offset++)
                {
                    float progress =
                        (float)(offset + 1) / (float)(runLength + 1);
                    float smoothProgress =
                        progress * progress * (3f - 2f * progress);
                    int idealMillimeters = Mathf.RoundToInt(Mathf.Lerp(
                        startMillimeters,
                        endMillimeters,
                        smoothProgress));

                    int acceleration =
                        (int)(NextSyntheticUInt() % 5u) - 2;
                    int jitter = (int)(NextSyntheticUInt() % 11u) - 5;
                    _syntheticVelocityMm = Mathf.Clamp(
                        _syntheticVelocityMm + acceleration,
                        -12,
                        12);
                    int humanizedMillimeters = Mathf.Clamp(
                        idealMillimeters + _syntheticVelocityMm + jitter,
                        30,
                        HumanizedPrecisionMaxMm);
                    humanizedMillimeters = Mathf.Min(
                        humanizedMillimeters,
                        previousMillimeters + 9);
                    previousMillimeters = humanizedMillimeters;
                    normalizedSamples[runStart + offset] =
                        EncodePrecisionMillimeters(humanizedMillimeters);
                }

                _syntheticPrecisionMm = Mathf.Clamp(
                    (previousMillimeters * 3 + tailMillimeters) / 4,
                    30,
                    HumanizedPrecisionMaxMm);
            }

            StoreHistoryCache(
                adjustmentMask,
                normalizedSamples,
                targetUid,
                hitUid);
            return adjustedCount;
        }

        private static bool[] BuildPrecisionAdjustmentMask(short[] samples)
        {
            bool[] mask = new bool[samples.Length];
            int index = 0;
            while (index < samples.Length)
            {
                int millimeters;
                if (!TryDecodePrecisionMillimeters(
                        samples[index],
                        out millimeters) ||
                    millimeters >= SuspiciousPrecisionThresholdMm)
                {
                    index++;
                    continue;
                }

                int runStart = index;
                while (index < samples.Length)
                {
                    if (!TryDecodePrecisionMillimeters(
                            samples[index],
                            out millimeters) ||
                        millimeters >= SuspiciousPrecisionThresholdMm)
                    {
                        break;
                    }
                    index++;
                }

                if (index - runStart < SuspiciousRunMinSamples)
                {
                    continue;
                }

                for (int i = runStart; i < index - 1; i++)
                {
                    mask[i] = true;
                }
            }
            return mask;
        }

        private static bool TryReuseHistoryCache(
            short[] samples,
            bool[] adjustmentMask,
            int targetUid,
            int hitUid,
            out short[] normalizedSamples)
        {
            normalizedSamples = null;
            if (_historyCacheFrame != Time.frameCount ||
                _historyCacheTargetUid != targetUid ||
                _historyCacheHitUid != hitUid ||
                _historyCacheMask == null ||
                _historyCacheValues == null ||
                _historyCacheMask.Length != adjustmentMask.Length ||
                _historyCacheValues.Length != samples.Length)
            {
                return false;
            }

            for (int i = 0; i < adjustmentMask.Length; i++)
            {
                if (_historyCacheMask[i] != adjustmentMask[i])
                {
                    return false;
                }
            }

            normalizedSamples = (short[])samples.Clone();
            for (int i = 0; i < adjustmentMask.Length; i++)
            {
                if (adjustmentMask[i])
                {
                    normalizedSamples[i] = _historyCacheValues[i];
                }
            }
            return true;
        }

        private static void StoreHistoryCache(
            bool[] adjustmentMask,
            short[] normalizedSamples,
            int targetUid,
            int hitUid)
        {
            _historyCacheFrame = Time.frameCount;
            _historyCacheTargetUid = targetUid;
            _historyCacheHitUid = hitUid;
            _historyCacheMask = (bool[])adjustmentMask.Clone();
            _historyCacheValues = (short[])normalizedSamples.Clone();
        }

        private static int CountTrue(bool[] values)
        {
            int count = 0;
            for (int i = 0; i < values.Length; i++)
            {
                if (values[i])
                {
                    count++;
                }
            }
            return count;
        }

        private static bool TryDecodePrecisionMillimeters(
            short code,
            out int millimeters)
        {
            millimeters = -1;
            if (code < 0)
            {
                return false;
            }

            millimeters = code / 10;
            if (millimeters < 0 || millimeters > 3276)
            {
                return false;
            }

            int expectedCheckDigit = Mathf.Abs(
                millimeters / 100 % 10 +
                millimeters / 10 % 10 -
                millimeters % 10) % 10;
            return code % 10 == expectedCheckDigit;
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

        private static uint NextSyntheticUInt()
        {
            uint value = _syntheticPrng;
            if (value == 0u)
            {
                value = 0x6D2B79F5u;
            }
            value ^= value << 13;
            value ^= value >> 17;
            value ^= value << 5;
            _syntheticPrng = value;
            return value;
        }

        private static bool HasRuntimeField(object instance, string fieldName)
        {
            return instance != null &&
                instance.GetType().GetField(
                    fieldName,
                    BindingFlags.Instance |
                    BindingFlags.Public |
                    BindingFlags.NonPublic) != null;
        }

        private static int ReadRuntimeFieldInt(
            object instance,
            string fieldName,
            int fallback)
        {
            try
            {
                if (instance == null)
                {
                    return fallback;
                }

                FieldInfo field = instance.GetType().GetField(
                    fieldName,
                    BindingFlags.Instance |
                    BindingFlags.Public |
                    BindingFlags.NonPublic);
                if (field == null)
                {
                    return fallback;
                }

                object value = field.GetValue(instance);
                if (value == null)
                {
                    return fallback;
                }

                try
                {
                    return Convert.ToInt32(value);
                }
                catch
                {
                }

                MethodInfo implicitMethod = field.FieldType.GetMethod(
                    "op_Implicit",
                    BindingFlags.Public | BindingFlags.Static,
                    null,
                    new Type[] { field.FieldType },
                    null);
                if (implicitMethod == null)
                {
                    return fallback;
                }

                object plainValue = implicitMethod.Invoke(
                    null,
                    new object[] { value });
                return plainValue == null
                    ? fallback
                    : Convert.ToInt32(plainValue);
            }
            catch
            {
                return fallback;
            }
        }

        private static short[] ReadRuntimeShortArray(
            object instance,
            string fieldName)
        {
            try
            {
                FieldInfo field = instance == null
                    ? null
                    : instance.GetType().GetField(
                        fieldName,
                        BindingFlags.Instance |
                        BindingFlags.Public |
                        BindingFlags.NonPublic);
                return field == null
                    ? null
                    : field.GetValue(instance) as short[];
            }
            catch
            {
                return null;
            }
        }

        private static bool TrySetRuntimeField(
            object instance,
            string fieldName,
            object plainValue)
        {
            try
            {
                FieldInfo field = instance == null
                    ? null
                    : instance.GetType().GetField(
                        fieldName,
                        BindingFlags.Instance |
                        BindingFlags.Public |
                        BindingFlags.NonPublic);
                if (field == null)
                {
                    return false;
                }

                object boxed = ConvertRuntimeFieldValue(
                    field.FieldType,
                    plainValue);
                if (boxed == null)
                {
                    return false;
                }

                field.SetValue(instance, boxed);
                return true;
            }
            catch (Exception ex)
            {
                FileLogger.Log("DETECTION",
                    "aim field update failed: " + fieldName + " " +
                    ex.GetType().Name + ": " + ex.Message);
                return false;
            }
        }

        private static object ConvertRuntimeFieldValue(
            Type fieldType,
            object plainValue)
        {
            if (fieldType == null || plainValue == null)
            {
                return null;
            }
            if (fieldType.IsAssignableFrom(plainValue.GetType()))
            {
                return plainValue;
            }

            try
            {
                MethodInfo implicitMethod = fieldType.GetMethod(
                    "op_Implicit",
                    BindingFlags.Public | BindingFlags.Static,
                    null,
                    new Type[] { plainValue.GetType() },
                    null);
                if (implicitMethod != null)
                {
                    return implicitMethod.Invoke(
                        null,
                        new object[] { plainValue });
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

        private static Assembly FindAssembly(string name)
        {
            Assembly[] assemblies = AppDomain.CurrentDomain.GetAssemblies();
            for (int i = 0; i < assemblies.Length; i++)
            {
                try
                {
                    if (assemblies[i].GetName().Name == name)
                    {
                        return assemblies[i];
                    }
                }
                catch
                {
                }
            }
            return null;
        }

        private static string FormatMethod(MethodBase method)
        {
            return method == null || method.DeclaringType == null
                ? "<unknown>"
                : method.DeclaringType.FullName + "." + method.Name;
        }
    }
}
