using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Xml;
using Mono.Cecil;
using Mono.Cecil.Cil;

internal static class ProtectedReleaseVerifier
{
    private static readonly string[] ForbiddenText =
    {
        "NetworkAuthEnabled",
        "Network auth disabled",
        "Auth bypassed"
    };

    private static readonly HashSet<string> UnityMessageNames =
        new HashSet<string>(StringComparer.Ordinal)
        {
            "Awake", "Start", "Update", "LateUpdate", "FixedUpdate",
            "OnGUI", "OnEnable", "OnDisable", "OnDestroy",
            "OnApplicationQuit", "OnApplicationFocus", "OnApplicationPause",
            "OnLevelWasLoaded", "OnDrawGizmos", "OnDrawGizmosSelected",
            "OnValidate", "Reset", "OnAnimatorIK", "OnAnimatorMove",
            "OnAudioFilterRead", "OnBecameInvisible", "OnBecameVisible",
            "OnCollisionEnter", "OnCollisionEnter2D", "OnCollisionExit",
            "OnCollisionExit2D", "OnCollisionStay", "OnCollisionStay2D",
            "OnControllerColliderHit", "OnJointBreak", "OnJointBreak2D",
            "OnMouseDown", "OnMouseDrag", "OnMouseEnter", "OnMouseExit",
            "OnMouseOver", "OnMouseUp", "OnMouseUpAsButton",
            "OnParticleCollision", "OnPostRender", "OnPreCull", "OnPreRender",
            "OnRenderImage", "OnRenderObject", "OnServerInitialized",
            "OnTransformChildrenChanged", "OnTransformParentChanged",
            "OnTriggerEnter", "OnTriggerEnter2D", "OnTriggerExit",
            "OnTriggerExit2D", "OnTriggerStay", "OnTriggerStay2D",
            "OnWillRenderObject"
        };

    private static readonly string[] HarmonyConventionNames =
    {
        "TargetMethod", "Prefix", "Postfix", "Transpiler"
    };

    private static int Main(string[] args)
    {
        if (args.Length != 4)
        {
            Console.Error.WriteLine(
                "Usage: ProtectedReleaseVerifier <raw> <protected> <mapping> <version>");
            return 2;
        }

        try
        {
            AssemblyDefinition raw = AssemblyDefinition.ReadAssembly(args[0]);
            AssemblyDefinition protectedAssembly = AssemblyDefinition.ReadAssembly(args[1]);

            VerifyAssembly(raw, "raw", args[3]);
            VerifyAssembly(protectedAssembly, "protected", args[3]);
            VerifyCompilerReferencesExternal(raw, "raw");
            VerifyCompilerReferencesExternal(protectedAssembly, "protected");
            VerifyLauncherNativeSdkBridge(raw, "raw");
            VerifyLauncherNativeSdkBridge(protectedAssembly, "protected");
            VerifyVeriGateEndpoints(raw, "raw");
            VerifyVeriGateEndpoints(protectedAssembly, "protected");
            VerifyHeartbeatCadence(raw, "raw");
            VerifyHeartbeatCadence(protectedAssembly, "protected");
            VerifyAuthorizationGate(raw, "raw");
            VerifyAuthorizationGate(protectedAssembly, "protected");
            VerifyAutoFireModes(raw, "raw");
            VerifyAutoFireModes(protectedAssembly, "protected");
            VerifyRemoteCSharpIsolation(raw, "raw");
            VerifyRemoteCSharpIsolation(protectedAssembly, "protected");
            VerifyCurrentAutoAim(raw, "raw", true);
            VerifyCurrentAutoAim(protectedAssembly, "protected", false);
            VerifyAimReportV9Protection(raw, "raw");
            VerifyAimReportV9Protection(protectedAssembly, "protected");
            VerifyCurrentAimTrack(raw, "raw", true);
            VerifyCurrentAimTrack(protectedAssembly, "protected", false);
            VerifyPrecisionTracker(raw, "raw", true);
            VerifyPrecisionTracker(protectedAssembly, "protected", false);
            VerifyUtilityFeatureWiring(raw, "raw", true);
            VerifyUtilityFeatureWiring(protectedAssembly, "protected", false);
            VerifyExplosionProtection(raw, "raw");
            VerifyExplosionProtection(protectedAssembly, "protected");
            VerifyCurrentDetectionProtection(raw, "raw");
            VerifyCurrentDetectionProtection(protectedAssembly, "protected");
            VerifyPendingInjectionReset(raw, "raw");
            VerifyPendingInjectionReset(protectedAssembly, "protected");
            VerifyMotherBossAutoClear(raw, "raw", true);
            VerifyMotherBossAutoClear(protectedAssembly, "protected", false);
            VerifyInfiniteItemWiring(raw);
            VerifyInfiniteItemDefaultDisabled(raw);
            VerifyInfiniteAmmoWiring(raw, "raw");
            VerifyInfiniteAmmoWiring(protectedAssembly, "protected");
            VerifyInfiniteAmmoDefaultDisabled(raw, "raw");
            VerifyInfiniteAmmoDefaultDisabled(protectedAssembly, "protected");
            VerifyFlightModeWiring(raw, "raw");
            VerifyFlightModeWiring(protectedAssembly, "protected");
            VerifyFlightModeDefaultDisabled(raw, "raw");
            VerifyFlightModeDefaultDisabled(protectedAssembly, "protected");
            VerifyQuestFarmRemoved(raw, "raw");
            VerifyQuestFarmRemoved(protectedAssembly, "protected");
            VerifyCurrentEntryPoint(protectedAssembly);
            VerifyConventionMethods(raw, protectedAssembly);
            VerifyObfuscation(args[0], args[1], args[2]);

            Console.WriteLine("Protected release IL verification passed.");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine("Protected release verification failed: " + exception.Message);
            return 1;
        }
    }

    private static void VerifyLauncherNativeSdkBridge(
        AssemblyDefinition assembly,
        string label)
    {
        MethodDefinition resolver = null;
        foreach (TypeDefinition type in AllTypes(assembly.MainModule))
        {
            foreach (MethodDefinition method in type.Methods)
            {
                if (MethodHasString(method, "current.sha256") &&
                    MethodHasString(method, "x1a0reN.Launcher") &&
                    MethodHasString(method, "verigate_sdk.dll"))
                {
                    resolver = method;
                    break;
                }
            }
            if (resolver != null)
            {
                break;
            }
        }

        Require(
            resolver != null,
            label + " core does not resolve the launcher-published native SDK.");
        Require(
            MethodCallsNamed(resolver, "System.IO.File", "ReadAllText") &&
            MethodCallsNamed(resolver, "System.IO.File", "OpenRead"),
            label + " launcher native SDK resolver no longer reads and verifies the digest marker.");

        bool hashesFile = false;
        bool loadsLibrary = false;
        foreach (MethodDefinition method in resolver.DeclaringType.Methods)
        {
            if (MethodCallsNamed(
                method,
                "System.Security.Cryptography.HashAlgorithm",
                "ComputeHash"))
            {
                hashesFile = true;
            }
            if (method.PInvokeInfo != null &&
                string.Equals(
                    method.PInvokeInfo.EntryPoint,
                    "LoadLibrary",
                    StringComparison.Ordinal))
            {
                loadsLibrary = true;
            }
        }
        Require(
            hashesFile,
            label + " launcher native SDK resolver no longer hashes the selected DLL.");
        Require(
            loadsLibrary,
            label + " launcher native SDK bridge no longer loads the verified DLL.");
    }

    private static void VerifyCompilerReferencesExternal(
        AssemblyDefinition assembly,
        string label)
    {
        foreach (Resource resource in assembly.MainModule.Resources)
        {
            Require(
                !resource.Name.StartsWith(
                    "ASWDEBUG.CompilerReferences.",
                    StringComparison.Ordinal),
                label + " core still embeds compiler reference resource: " +
                resource.Name + ".");
            Require(
                !string.Equals(
                    resource.Name,
                    "ASWDEBUG.Native.verigate_sdk.dll",
                    StringComparison.Ordinal),
                label + " still embeds verigate_sdk.dll; protected output must keep native dependencies external.");
        }
    }

    private static void VerifyAutoFireModes(
        AssemblyDefinition assembly,
        string label)
    {
        TypeDefinition autoFire = FindType(
            assembly.MainModule,
            "ASWDEBUG.Cheats.Player.AutoFire");
        TypeDefinition cheatMain = FindType(
            assembly.MainModule,
            "ASWDEBUG.Main.CheatMain");
        TypeDefinition keyCodeGetKeyPatch = FindType(
            assembly.MainModule,
            "ASWDEBUG.Patch_Input_GetKey_KeyCode_Prefix");
        TypeDefinition stringGetKeyPatch = FindType(
            assembly.MainModule,
            "ASWDEBUG.Patch_Input_GetKey_String_Prefix");
        TypeDefinition keyCodeGetKeyDownPatch = FindType(
            assembly.MainModule,
            "ASWDEBUG.Patch_Input_GetKeyDown_KeyCode_Prefix");
        TypeDefinition stringGetKeyDownPatch = FindType(
            assembly.MainModule,
            "ASWDEBUG.Patch_Input_GetKeyDown_String_Prefix");

        Require(autoFire != null, label + " AutoFire type is missing.");

        MethodDefinition tick = autoFire == null
            ? null
            : FindMethod(autoFire, "Tick");
        MethodDefinition reset = autoFire == null
            ? null
            : FindMethod(autoFire, "Reset");
        MethodDefinition wantsFire = autoFire == null
            ? null
            : FindMethod(autoFire, "get_WantsFire");
        MethodDefinition shouldFireKeyDown = autoFire == null
            ? null
            : FindMethod(autoFire, "ShouldFireKeyDown");
        MethodDefinition update = cheatMain == null
            ? null
            : FindMethod(cheatMain, "Update");
        MethodDefinition keyCodeGetKeyPrefix = keyCodeGetKeyPatch == null
            ? null
            : FindMethod(keyCodeGetKeyPatch, "Prefix");
        MethodDefinition stringGetKeyPrefix = stringGetKeyPatch == null
            ? null
            : FindMethod(stringGetKeyPatch, "Prefix");
        MethodDefinition keyCodeGetKeyDownPrefix = keyCodeGetKeyDownPatch == null
            ? null
            : FindMethod(keyCodeGetKeyDownPatch, "Prefix");
        MethodDefinition stringGetKeyDownPrefix = stringGetKeyDownPatch == null
            ? null
            : FindMethod(stringGetKeyDownPatch, "Prefix");
        FieldDefinition keyDownRepeat = autoFire == null
            ? null
            : FindField(autoFire, "KeyDownRepeatSeconds");

        Require(
            FindMethod(autoFire, "Toggle") != null,
            label + " automatic-trigger toggle is missing.");
        Require(
            FindMethod(autoFire, "Enable") != null &&
            FindMethod(autoFire, "Fire") != null &&
            FindMethod(autoFire, "IsCrosshairOnEnemyExact") != null &&
            FindMethod(autoFire, "ToggleAutoFireAllowed") != null &&
            tick != null &&
            reset != null &&
            wantsFire != null &&
            shouldFireKeyDown != null &&
            FindField(autoFire, "Enabled") != null &&
            FindField(autoFire, "AutoFireAllowed") != null,
            label + " automatic-trigger runtime contract is incomplete.");
        Require(
            update != null &&
            CallsMethod(update, tick) &&
            CallsMethod(update, reset),
            label + " CheatMain does not tick and reset AutoFire.");
        Require(
            keyCodeGetKeyPrefix != null &&
            stringGetKeyPrefix != null &&
            CallsMethod(keyCodeGetKeyPrefix, wantsFire) &&
            CallsMethod(stringGetKeyPrefix, wantsFire) &&
            !MethodReferencesField(keyCodeGetKeyPrefix, "AutoFireAllowed") &&
            !MethodReferencesField(stringGetKeyPrefix, "AutoFireAllowed"),
            label + " held-fire patches are not wired to AutoFire.WantsFire.");
        Require(
            keyCodeGetKeyDownPrefix != null &&
            stringGetKeyDownPrefix != null &&
            CallsMethod(keyCodeGetKeyDownPrefix, shouldFireKeyDown) &&
            CallsMethod(stringGetKeyDownPrefix, shouldFireKeyDown),
            label + " semi-auto fire patches are not wired to AutoFire key-down pulses.");
        Require(
            FindReachableMethodCalling(tick, "SphereCastAll", 3) != null,
            label + " AutoFire crosshair collider scan is missing.");
        Require(
            keyDownRepeat != null &&
            keyDownRepeat.HasConstant &&
            Math.Abs(Convert.ToSingle(keyDownRepeat.Constant) - 0.06f) < 0.0001f,
            label + " AutoFire key-down repeat interval is invalid.");
    }

    private static void VerifyRemoteCSharpIsolation(
        AssemblyDefinition assembly,
        string label)
    {
        Require(
            HasStringLiteral(assembly, "console_command") &&
            HasStringLiteral(assembly, "invoke_method") &&
            HasStringLiteral(assembly, "COMMAND_EXPIRED") &&
            HasStringLiteral(assembly, "UNSUPPORTED_COMMAND"),
            label + " remote command dispatcher contract is incomplete.");
    }

    private static bool HasStringLiteral(
        AssemblyDefinition assembly,
        string expected)
    {
        foreach (TypeDefinition type in AllTypes(assembly.MainModule))
        {
            foreach (MethodDefinition method in type.Methods)
            {
                if (!method.HasBody)
                {
                    continue;
                }
                foreach (Instruction instruction in method.Body.Instructions)
                {
                    string literal = instruction.Operand as string;
                    if (literal != null &&
                        literal.IndexOf(expected, StringComparison.Ordinal) >= 0)
                    {
                        return true;
                    }
                }
            }
        }
        return false;
    }

    private static void VerifyVeriGateEndpoints(
        AssemblyDefinition assembly,
        string label)
    {
        Require(
            HasStringLiteral(assembly, "https://verigate.x1a0ren.com") &&
            HasStringLiteral(assembly, "transport_fallback_origin") &&
            HasStringLiteral(assembly, "https://43.133.30.226") &&
            HasStringLiteral(assembly, "vg_sdk_last_error_diagnostic") &&
            HasStringLiteral(assembly, "route may switch to trusted IP") &&
            !HasStringLiteral(
                assembly,
                "https://verigate.43-133-30-226.nip.io"),
            label + " VeriGate primary/fallback endpoints are not synchronized with the Launcher.");
    }

    private static void VerifyHeartbeatCadence(
        AssemblyDefinition assembly,
        string label)
    {
        TypeDefinition manager = FindType(
            assembly.MainModule,
            "ASWDEBUG.Verify.VeriGateAuthManager");
        MethodDefinition loop = manager == null
            ? null
            : FindMethod(manager, "HeartbeatLoop");
        MethodDefinition implementation = manager == null
            ? null
            : FindMethodWithString(
                manager,
                "VeriGate heartbeat scheduled interval_seconds=");
        Require(
            loop != null &&
            implementation != null &&
            CountCallsByName(implementation, "get_realtimeSinceStartup") >= 2,
            label + " VeriGate heartbeat no longer enforces its realtime interval inline.");
        Require(
            FindMethod(manager, "WaitRealtime") == null,
            label + " VeriGate heartbeat restored the Unity 4.7 nested-IEnumerator bug.");
    }

    private static void VerifyPendingInjectionReset(
        AssemblyDefinition assembly,
        string label)
    {
        TypeDefinition loader = FindType(
            assembly.MainModule,
            "ASWDEBUG.Patch.HarmonyLoader");
        MethodDefinition core = loader == null
            ? null
            : FindMethod(loader, "ApplyCoreProtectionPatches");
        MethodDefinition clear = loader == null
            ? null
            : FindMethod(loader, "ClearPendingInjectionFlag");
        MethodDefinition keepAlive = loader == null
            ? null
            : FindMethod(loader, "Protection_ClearPendingInjectionPrefix");
        Require(
            core != null &&
            clear != null &&
            keepAlive != null &&
            CallsMethod(core, clear) &&
            CallsMethod(keepAlive, clear) &&
            MethodReferencesField(clear, "pendingInjectionType") &&
            MethodHasString(core, "RequestKeepAlive"),
            label + " pending injection flag is not cleared before lobby keepalive.");
    }

    private static void VerifyAimProtection(
        AssemblyDefinition assembly,
        string label)
    {
        TypeDefinition loader = FindType(
            assembly.MainModule,
            "ASWDEBUG.Patch.HarmonyLoader");
        Require(loader != null, label + " HarmonyLoader type is missing.");

        MethodDefinition core = FindMethod(
            loader,
            "ApplyCoreProtectionPatches");
        MethodDefinition aim = FindMethod(
            loader,
            "ApplyAimProtectionPatches");
        MethodDefinition payloadPrefix = FindMethod(
            loader,
            "Protection_ShootPayloadBuildPrefix");
        MethodDefinition sanitizer = FindMethod(
            loader,
            "Protection_SanitizeShootPayload");
        MethodDefinition normalizeReport = FindMethod(
            loader,
            "NormalizeAimReportFields");
        MethodDefinition normalizeGeometry = FindMethod(
            loader,
            "NormalizeAimGeometryChecksum");
        MethodDefinition legacyPredicate = FindMethod(
            loader,
            "IsLegacyAimManipulationActive");
        MethodDefinition cameraPostfix = FindMethod(
            loader,
            "Protection_AutoAimCameraPostfix");
        TypeDefinition autoAim = FindType(
            assembly.MainModule,
            "ASWDEBUG.Cheats.AutoAim.AutoAim");
        MethodDefinition cameraObserver = autoAim == null
            ? null
            : FindMethod(autoAim, "ObserveCameraAfterLateUpdate");
        MethodDefinition nativeValidator = autoAim == null
            ? null
            : FindMethod(autoAim, "ValidateNativeShotReport");
        Require(core != null, label + " core protection installer is missing.");
        Require(aim != null, label + " narrow aim protection installer is missing.");
        Require(
            CallsMethod(core, aim),
            label + " core protection no longer installs narrow aim protection.");
        Require(
            MethodHasString(aim, "PluginReport") &&
            MethodHasString(aim, "AssitToolCheck") &&
            MethodHasString(aim, "CameraObj") &&
            MethodHasString(aim, "LateUpdate") &&
            MethodHasString(aim, "BuildEncryptedPayload"),
            label + " narrow aim protection targets are incomplete.");
        Require(
            !MethodHasString(aim, "UpdateSample") &&
            !MethodHasString(aim, "MarkFireCooldown") &&
            !MethodHasString(aim, "ApplyPendingShotReport"),
            label + " native aim detector lifecycle is intercepted.");
        Require(
            cameraPostfix != null &&
            cameraObserver != null &&
            CallsMethod(cameraPostfix, cameraObserver),
            label + " AutoAim camera observation no longer runs after native LateUpdate.");
        Require(
            payloadPrefix != null &&
            sanitizer != null &&
            CallsMethod(payloadPrefix, sanitizer),
            label + " one-argument v9 payload hook no longer reaches the sanitizer.");
        Require(
            normalizeReport != null &&
            normalizeGeometry != null &&
            legacyPredicate != null &&
            CallsMethod(sanitizer, normalizeReport) &&
            CallsMethod(sanitizer, normalizeGeometry) &&
            CallsMethod(normalizeReport, legacyPredicate) &&
            CallsMethod(normalizeGeometry, legacyPredicate),
            label + " legacy aim compatibility path is incomplete.");
        Require(
            nativeValidator != null &&
            CallsMethod(sanitizer, nativeValidator),
            label + " AutoAim native shot report validation is missing.");
        Require(
            !MethodReferencesType(
                legacyPredicate,
                "ASWDEBUG.Cheats.AutoAim.AutoAim"),
            label + " AutoAim is still routed through synthetic legacy telemetry.");

        TypeDefinition shootPatch = FindType(
            assembly.MainModule,
            "ASWDEBUG.Patch_ChannelConnection_Shoot_Prefix");
        MethodDefinition shootPrefix = shootPatch == null
            ? null
            : FindMethod(shootPatch, "Prefix");
        MethodDefinition aimTrackRewrite = shootPatch == null
            ? null
            : FindMethod(shootPatch, "ApplyAimTrackShotCompat");
        MethodDefinition excludedWeapon = shootPatch == null
            ? null
            : FindMethod(shootPatch, "IsAimTrackExcludedWeapon");
        MethodDefinition synchronizeEnc = shootPatch == null
            ? null
            : FindMethod(shootPatch, "SynchronizeAimTrackEnc");
        Require(
            shootPrefix != null &&
            shootPrefix.Parameters.Count >= 3 &&
            shootPrefix.Parameters[2].ParameterType.IsByReference &&
            aimTrackRewrite != null &&
            CallsMethod(shootPrefix, aimTrackRewrite),
            label + " silent-aim packet direction is not rewritten coherently.");
        Require(
            excludedWeapon != null &&
            CallsMethod(aimTrackRewrite, excludedWeapon) &&
            MethodReferencesType(excludedWeapon, "KnifeBaseController") &&
            MethodReferencesType(excludedWeapon, "BowController") &&
            MethodReferencesType(excludedWeapon, "RPGController"),
            label + " AimTrack packet rewrite weapon exclusions are incomplete.");
        Require(
            synchronizeEnc != null &&
            CallsMethod(aimTrackRewrite, synchronizeEnc) &&
            MethodHasString(synchronizeEnc, "enc"),
            label + " AimTrack hit rewrite no longer synchronizes enc.");
    }

    private static void VerifyAutoAimV2(
        AssemblyDefinition assembly,
        string label,
        bool inspectPrivateMotion)
    {
        TypeDefinition autoAim = FindType(
            assembly.MainModule,
            "ASWDEBUG.Cheats.AutoAim.AutoAim");
        Require(autoAim != null, label + " AutoAim type is missing.");

        MethodDefinition telemetryActive = FindMethod(
            autoAim,
            "IsTelemetryActive");
        MethodDefinition cameraObserver = FindMethod(
            autoAim,
            "ObserveCameraAfterLateUpdate");
        MethodDefinition validator = FindMethod(
            autoAim,
            "ValidateNativeShotReport");
        MethodDefinition reset = FindMethod(
            autoAim,
            "ResetRuntimeState");
        MethodDefinition summary = FindMethod(
            autoAim,
            "GetDiagnosticSummary");
        Require(
            telemetryActive != null &&
            cameraObserver != null &&
            validator != null &&
            reset != null &&
            summary != null,
            label + " AutoAim V2 public runtime contract is incomplete.");
        Require(
            MethodReferencesField(cameraObserver, "shootPos") &&
            MethodReferencesField(cameraObserver, "shootForward") &&
            MethodReferencesField(cameraObserver, "finalx") == false,
            label + " AutoAim camera observer no longer reads the native shot ray.");
        Require(
            MethodReferencesField(validator, "aim_report_version") &&
            MethodReferencesField(validator, "aim_target_uid") &&
            MethodReferencesField(validator, "aim_shot_precision_code") &&
            MethodReferencesField(validator, "aim_precision_samples"),
            label + " AutoAim native report validator fields are incomplete.");
        Require(
            !MethodWritesField(validator, "HitMessage"),
            label + " AutoAim native report validator mutates HitMessage.");

        if (!inspectPrivateMotion)
        {
            return;
        }

        MethodDefinition aim = FindMethod(autoAim, "Aim");
        MethodDefinition beginTarget = FindMethod(
            autoAim,
            "BeginOrContinueTarget");
        MethodDefinition stableTarget = FindMethod(
            autoAim,
            "ResolveStableTarget");
        MethodDefinition resolveAimPoint = FindMethod(
            autoAim,
            "ResolveAimPoint");
        MethodDefinition refreshAimOffset = FindMethod(
            autoAim,
            "RefreshAimOffset");
        MethodDefinition updateState = FindMethod(
            autoAim,
            "UpdateMotionState");
        MethodDefinition applyMotion = FindMethod(
            autoAim,
            "ApplyMotionStep");
        MethodDefinition pauseAssist = FindMethod(
            autoAim,
            "ShouldPauseAssist");
        MethodDefinition headFrame = FindMethod(
            autoAim,
            "TryResolveHeadFrame");
        MethodDefinition nativeTrace = FindMethod(
            autoAim,
            "TryResolveNativeHeadTrace");
        Require(
            aim != null &&
            beginTarget != null &&
            stableTarget != null &&
            resolveAimPoint != null &&
            refreshAimOffset != null &&
            updateState != null &&
            applyMotion != null &&
            pauseAssist != null &&
            headFrame != null &&
            nativeTrace != null &&
            CallsMethod(aim, beginTarget) &&
            CallsMethod(aim, stableTarget) &&
            CallsMethod(aim, resolveAimPoint) &&
            CallsMethod(aim, updateState) &&
            CallsMethod(aim, applyMotion) &&
            CallsMethod(resolveAimPoint, headFrame) &&
            CallsMethod(resolveAimPoint, refreshAimOffset) &&
            CallsMethod(applyMotion, pauseAssist) &&
            CallsMethod(cameraObserver, nativeTrace),
            label + " AutoAim V3 humanized motion pipeline is incomplete.");
        Require(
            MethodReferencesField(applyMotion, "finalx") &&
            MethodReferencesField(applyMotion, "finaly"),
            label + " AutoAim V2 no longer drives the native camera angles.");
    }

    private static void VerifyAutoAimRollback(
        AssemblyDefinition assembly,
        string label)
    {
        TypeDefinition autoAim = FindType(
            assembly.MainModule,
            "ASWDEBUG.Cheats.AutoAim.AutoAim");
        Require(autoAim != null, label + " AutoAim type is missing.");

        MethodDefinition aim = FindMethod(autoAim, "Aim");
        MethodDefinition beginTarget = FindMethod(autoAim, "BeginOrContinueTarget");
        MethodDefinition resolveAimPoint = FindMethod(autoAim, "ResolveAimPoint");
        MethodDefinition updateState = FindMethod(autoAim, "UpdateMotionState");
        MethodDefinition applyMotion = FindMethod(autoAim, "ApplyMotionStep");
        MethodDefinition headFrame = FindMethod(autoAim, "TryResolveHeadFrame");
        MethodDefinition cameraObserver = FindMethod(
            autoAim,
            "ObserveCameraAfterLateUpdate");
        MethodDefinition nativeTrace = FindMethod(
            autoAim,
            "TryResolveNativeHeadTrace");
        MethodDefinition summary = FindMethod(autoAim, "GetDiagnosticSummary");
        Require(
            aim != null &&
            beginTarget != null &&
            resolveAimPoint != null &&
            updateState != null &&
            applyMotion != null &&
            headFrame != null &&
            cameraObserver != null &&
            nativeTrace != null &&
            CallsMethod(aim, beginTarget) &&
            CallsMethod(aim, resolveAimPoint) &&
            CallsMethod(aim, updateState) &&
            CallsMethod(aim, applyMotion) &&
            CallsMethod(resolveAimPoint, headFrame) &&
            CallsMethod(cameraObserver, nativeTrace) &&
            MethodReferencesField(applyMotion, "finalx") &&
            MethodReferencesField(applyMotion, "finaly"),
            label + " pre-humanizer AutoAim V2 pipeline is incomplete.");
        Require(
            FindMethod(autoAim, "ResolveStableTarget") == null &&
            FindMethod(autoAim, "RefreshAimOffset") == null &&
            FindMethod(autoAim, "ShouldPauseAssist") == null &&
            FindMethod(autoAim, "ReleaseInputState") == null &&
            FindMethod(autoAim, "ApplyDeadZone") == null &&
            FindMethod(autoAim, "LimitMotionStep") == null &&
            FindMethod(autoAim, "InitializeHumanizer") == null,
            label + " AutoAim still contains the 1.0.37 humanizer pipeline.");
        Require(
            summary != null &&
            !MethodHasString(summary, "jitter=") &&
            !MethodHasString(summary, "deadzone=") &&
            !MethodHasString(summary, "reaction_ms=") &&
            !MethodHasString(summary, "pause="),
            label + " AutoAim diagnostics still expose humanizer state.");
    }

    private static void VerifyAimTrackWeaponExclusions(
        AssemblyDefinition assembly,
        string label)
    {
        TypeDefinition aimTrack = FindType(
            assembly.MainModule,
            "ASWDEBUG.Cheats.AimTrack.AimTrack");
        Require(aimTrack != null, label + " AimTrack type is missing.");

        MethodDefinition track = FindMethod(aimTrack, "Track");
        MethodDefinition excluded = FindMethod(aimTrack, "IsExcludedWeapon");
        Require(
            track != null &&
            excluded != null &&
            CallsMethod(track, excluded),
            label + " AimTrack target selection does not apply weapon exclusions.");
        Require(
            MethodReferencesType(excluded, "KnifeBaseController") &&
            MethodReferencesType(excluded, "BowController") &&
            MethodReferencesType(excluded, "RPGController"),
            label + " AimTrack target selection weapon exclusions are incomplete.");
    }

    private static void VerifyDetectionProtection(
        AssemblyDefinition assembly,
        string label)
    {
        TypeDefinition loader = FindType(
            assembly.MainModule,
            "ASWDEBUG.Patch.HarmonyLoader");
        Require(loader != null, label + " HarmonyLoader type is missing.");

        MethodDefinition core = FindMethod(
            loader,
            "ApplyCoreProtectionPatches");
        MethodDefinition detection = FindMethod(
            loader,
            "ApplyDetectionProtectionPatches");
        Require(
            core != null &&
            detection != null &&
            CallsMethod(core, detection),
            label + " core protection no longer installs detection protection.");

        string[] requiredTargets =
        {
            "ParseProcessCheck",
            "CheckByBlackJosnTable",
            "CheckByUrl",
            "GetClientBinarySummaryMd5",
            "GetEncryptedClientBinarySummaryMd5",
            "OnClientMessage",
            "HttpRequest",
            "ParseKickOutByPlugin",
            "ParseNotifyKickedByGM",
            "ParseNotifyKickedByVote"
        };
        foreach (string target in requiredTargets)
        {
            Require(
                MethodHasString(detection, target),
                label + " detection protection target is missing: " + target + ".");
        }

        MethodDefinition process = FindMethod(
            loader,
            "Protection_ParseProcessCheckPrefix");
        MethodDefinition launcher = FindMethod(
            loader,
            "Protection_LauncherProcessDataPrefix");
        MethodDefinition http = FindMethod(
            loader,
            "Protection_FilterGameHttpRequestPrefix");
        MethodDefinition md5 = FindMethod(
            loader,
            "Protection_ClientFileMd5Prefix");
        MethodDefinition encryptedMd5 = FindMethod(
            loader,
            "Protection_EncryptedClientFileMd5Prefix");
        MethodDefinition kick = FindMethod(
            loader,
            "Protection_LogKickOutByPluginPrefix");
        MethodDefinition gmKick = FindMethod(
            loader,
            "Protection_LogNotifyKickedByGMPostfix");
        MethodDefinition voteKick = FindMethod(
            loader,
            "Protection_LogNotifyKickedByVotePostfix");
        Require(
            process != null &&
            launcher != null &&
            MethodHasString(launcher, "LAUNCHER-PROCESS-DATA-BLOCKED"),
            label + " framed process-check handlers are incomplete.");
        Require(
            http != null &&
            MethodHasString(http, "client_inner.log") &&
            MethodHasString(http, "waigua.log"),
            label + " game exception/report upload filter is incomplete.");
        Require(
            md5 != null &&
            md5.Parameters.Count == 1 &&
            md5.Parameters[0].ParameterType.IsByReference &&
            encryptedMd5 != null &&
            encryptedMd5.Parameters.Count == 2 &&
            encryptedMd5.Parameters[1].ParameterType.IsByReference,
            label + " native client MD5 prefixes no longer preserve the summary result.");

        TypeDefinition diagnostics = FindType(
            assembly.MainModule,
            "ASWDEBUG.ShotDiagnostics");
        MethodDefinition logKick = diagnostics == null
            ? null
            : FindMethod(diagnostics, "LogKick");
        Require(
            kick != null &&
            gmKick != null &&
            voteKick != null &&
            logKick != null &&
            CallsMethod(kick, logKick) &&
            CallsMethod(gmKick, logKick) &&
            CallsMethod(voteKick, logKick),
            label + " kick-reason diagnostics are incomplete.");
    }

    private static void VerifyInfiniteItemWiring(AssemblyDefinition assembly)
    {
        TypeDefinition type = FindType(
            assembly.MainModule,
            "ASWDEBUG.Patch.InfiniteItemUse");
        Require(type != null, "Raw InfiniteItemUse type is missing.");

        MethodDefinition tick = FindMethod(type, "Tick");
        MethodDefinition prepareSlots = FindMethod(type, "PreparePlayerSlots");
        MethodDefinition prepare = FindMethod(type, "Prepare");
        MethodDefinition sendUse = FindMethod(type, "SendUse");
        MethodDefinition toggle = FindMethod(type, "Toggle");
        FieldDefinition enabled = FindField(type, "Enabled");
        Require(
            tick != null &&
            prepareSlots != null &&
            prepare != null &&
            sendUse != null &&
            toggle != null &&
            enabled != null &&
            CallsMethod(tick, prepareSlots) &&
            CallsMethod(prepareSlots, prepare) &&
            CallsMethod(sendUse, prepare),
            "InfiniteItemUse wiring is incomplete.");
        Require(
            MethodReferencesField(prepare, "count") &&
            MethodReferencesField(prepare, "cooling") &&
            MethodReferencesField(prepare, "stop_cooling") &&
            MethodReferencesField(prepare, "cool_down_ready") &&
            CountCallsByName(sendUse, "Use") == 1,
            "InfiniteItemUse state or network use path changed.");

        TypeDefinition ui = FindType(
            assembly.MainModule,
            "ASWDEBUG.UI.CheatUIManager");
        MethodDefinition display = ui == null ? null : FindMethod(ui, "Display");
        Require(
            display != null &&
            CallsMethod(display, toggle) &&
            MethodHasString(display, "\u65e0\u9650\u836f"),
            "InfiniteItemUse UI toggle is missing.");
    }

    private static void VerifyInfiniteAmmoWiring(
        AssemblyDefinition assembly,
        string label)
    {
        TypeDefinition type = FindType(
            assembly.MainModule,
            "ASWDEBUG.Cheats.Other.InfiniteAmmo");
        Require(type != null, label + " InfiniteAmmo type is missing.");

        FieldDefinition enabled = FindField(type, "Enabled");
        FieldDefinition keepAlive = FindField(
            type,
            "KeepAliveIntervalSeconds");
        FieldDefinition preShotFreshness = FindField(
            type,
            "PreShotFreshnessSeconds");
        MethodDefinition tick = FindMethod(type, "Tick");
        MethodDefinition beforeShoot = FindMethod(type, "BeforeShoot");
        MethodDefinition interceptReload = FindMethod(
            type,
            "TryInterceptLocalReload");
        MethodDefinition prepare = FindMethod(type, "PrepareLocalWeapon");
        MethodDefinition sendReload = FindMethod(type, "SendReloadIfStale");
        MethodDefinition toggle = FindMethod(type, "Toggle");

        Require(
            enabled != null &&
            enabled.IsStatic &&
            enabled.FieldType.MetadataType == MetadataType.Boolean &&
            keepAlive != null &&
            keepAlive.HasConstant &&
            Math.Abs(Convert.ToSingle(keepAlive.Constant) - 0.20f) < 0.0001f &&
            preShotFreshness != null &&
            preShotFreshness.HasConstant &&
            Math.Abs(Convert.ToSingle(preShotFreshness.Constant) - 0.08f) < 0.0001f &&
            tick != null &&
            beforeShoot != null &&
            interceptReload != null &&
            interceptReload.ReturnType.MetadataType == MetadataType.Boolean &&
            prepare != null &&
            sendReload != null &&
            toggle != null,
            label + " InfiniteAmmo public contract or cadence changed.");

        Require(
            CallsMethod(tick, prepare) &&
            CallsMethod(tick, sendReload) &&
            CallsMethod(beforeShoot, prepare) &&
            CallsMethod(beforeShoot, sendReload) &&
            CallsMethod(interceptReload, prepare) &&
            CallsMethod(interceptReload, sendReload) &&
            CountCallsByName(sendReload, "Reload") == 1,
            label + " InfiniteAmmo reload request wiring is incomplete.");

        Require(
            CountCallsByName(prepare, "set_clip") >= 1 &&
            MethodReferencesField(prepare, "reloading") &&
            MethodReferencesField(prepare, "cooling") &&
            MethodReferencesField(prepare, "cool_down_ready"),
            label + " InfiniteAmmo local firing state is incomplete.");

        TypeDefinition main = FindType(
            assembly.MainModule,
            "ASWDEBUG.Main.CheatMain");
        MethodDefinition update = main == null ? null : FindMethod(main, "Update");
        Require(
            update != null && CallsMethod(update, tick),
            label + " CheatMain.Update no longer ticks InfiniteAmmo.");

        TypeDefinition shootPatch = FindType(
            assembly.MainModule,
            "ASWDEBUG.Patch_ChannelConnection_Shoot_Prefix");
        MethodDefinition shootPrefix = shootPatch == null
            ? null
            : FindMethod(shootPatch, "Prefix");
        Require(
            shootPrefix != null && CallsMethod(shootPrefix, beforeShoot),
            label + " Shoot prefix no longer sends pre-shot reload requests.");

        TypeDefinition reloadPatch = FindType(
            assembly.MainModule,
            "ASWDEBUG.Patch.Patch_WeaponBase_Reload_InfiniteAmmo");
        MethodDefinition reloadTarget = reloadPatch == null
            ? null
            : FindMethod(reloadPatch, "TargetMethod");
        MethodDefinition reloadPrefix = reloadPatch == null
            ? null
            : FindMethod(reloadPatch, "Prefix");
        Require(
            reloadTarget != null &&
            MethodHasString(reloadTarget, "Reload") &&
            reloadPrefix != null &&
            reloadPrefix.ReturnType.MetadataType == MetadataType.Boolean &&
            CallsMethod(reloadPrefix, interceptReload),
            label + " local reload suppression patch is incomplete.");

        TypeDefinition ui = FindType(
            assembly.MainModule,
            "ASWDEBUG.UI.CheatUIManager");
        MethodDefinition display = ui == null ? null : FindMethod(ui, "Display");
        Require(
            display != null &&
            CallsMethod(display, toggle) &&
            MethodHasString(display, "\u65e0\u9650\u5b50\u5f39"),
            label + " InfiniteAmmo UI toggle is missing from Other.");
    }

    private static void VerifyFlightModeWiring(
        AssemblyDefinition assembly,
        string label)
    {
        TypeDefinition type = FindType(
            assembly.MainModule,
            "ASWDEBUG.Cheats.Other.FlightMode");
        Require(type != null, label + " FlightMode type is missing.");

        FieldDefinition enabled = FindField(type, "Enabled");
        FieldDefinition ascendKey = FindField(type, "AscendKey");
        FieldDefinition descendKey = FindField(type, "DescendKey");
        FieldDefinition verticalSpeed = FindField(type, "VerticalSpeed");
        MethodDefinition apply = FindMethod(type, "Apply");
        MethodDefinition toggle = FindMethod(type, "Toggle");
        MethodDefinition setAscendKey = FindMethod(type, "SetAscendKey");
        MethodDefinition setDescendKey = FindMethod(type, "SetDescendKey");

        Require(
            enabled != null &&
            enabled.IsStatic &&
            enabled.FieldType.MetadataType == MetadataType.Boolean &&
            ascendKey != null &&
            descendKey != null &&
            verticalSpeed != null &&
            verticalSpeed.FieldType.MetadataType == MetadataType.Single &&
            apply != null &&
            toggle != null &&
            setAscendKey != null &&
            setDescendKey != null,
            label + " FlightMode public contract is incomplete.");

        Require(
            CountCallsByName(apply, "GetKey") >= 2 &&
            MethodReferencesField(apply, "useGravity") &&
            CountCallsByName(apply, "get_velocity") >= 1 &&
            CountCallsByName(apply, "set_velocity") >= 1 &&
            MethodReferencesField(apply, "vertical_speed") &&
            MethodReferencesField(apply, "is_check_fall_down") &&
            MethodReferencesField(apply, "is_fall_down") &&
            MethodReferencesField(apply, "start_to_fall_down") &&
            MethodReferencesField(apply, "fall_down_time"),
            label + " FlightMode gravity, velocity, or fall-state wiring changed.");

        TypeDefinition patch = FindType(
            assembly.MainModule,
            "ASWDEBUG.Cheats.Other.Patch_MoveScript_FlightMode");
        MethodDefinition target = patch == null
            ? null
            : FindMethod(patch, "TargetMethod");
        MethodDefinition prefix = patch == null
            ? null
            : FindMethod(patch, "Prefix");
        Require(
            target != null &&
            MethodHasString(target, "FixedUpdate") &&
            prefix != null &&
            CallsMethod(prefix, apply),
            label + " MoveScript.FixedUpdate flight patch is incomplete.");

        TypeDefinition ui = FindType(
            assembly.MainModule,
            "ASWDEBUG.UI.CheatUIManager");
        MethodDefinition display = ui == null ? null : FindMethod(ui, "Display");
        MethodDefinition bind = null;
        if (ui != null)
        {
            foreach (MethodDefinition method in ui.Methods)
            {
                if (CallsMethod(method, setAscendKey) &&
                    CallsMethod(method, setDescendKey))
                {
                    bind = method;
                    break;
                }
            }
        }
        Require(
            display != null &&
            CallsMethod(display, toggle) &&
            MethodHasString(display, "\u6ede\u7a7a\u98de\u884c") &&
            MethodHasString(display, "\u4e0a\u5347") &&
            MethodHasString(display, "\u4e0b\u964d") &&
            MethodHasString(display, "\u5347\u964d\u901f\u5ea6") &&
            bind != null &&
            CallsMethod(bind, setAscendKey) &&
            CallsMethod(bind, setDescendKey),
            label + " FlightMode UI or custom key binding is incomplete.");
    }

    private static void VerifyCurrentEntryPoint(AssemblyDefinition assembly)
    {
        TypeDefinition entrypoint = FindType(
            assembly.MainModule,
            "Doorstop.Entrypoint");
        Require(entrypoint != null, "Protected Doorstop.Entrypoint is missing.");

        MethodDefinition start = FindMethod(entrypoint, "Start");
        MethodDefinition patch = FindMethod(entrypoint, "PatchGameEntry");
        MethodDefinition patchCore = FindMethod(entrypoint, "PatchGameEntryCore");
        MethodDefinition postfix = FindMethod(entrypoint, "GameEntryPostfix");
        MethodDefinition bootstrap = FindMethod(entrypoint, "Bootstrap");
        Require(
            start != null &&
            start.HasBody &&
            start.IsPublic &&
            start.IsStatic &&
            patch != null &&
            patchCore != null &&
            postfix != null &&
            bootstrap != null,
            "Protected Doorstop entrypoint contract is incomplete.");
        Require(
            MethodHasString(start, "Doorstop.Entrypoint.Start() called") &&
            CallsMethod(start, patch) &&
            CallsMethod(patch, patchCore) &&
            MethodHasString(patchCore, "GameEntryPostfix") &&
            CallsMethod(postfix, bootstrap) &&
            MethodHasString(bootstrap, "__RuntimeInjectionBoot__") &&
            MethodHasString(bootstrap, "ConsoleManager created OK"),
            "Protected Doorstop bootstrap flow is incomplete.");
    }

    private static void VerifyCurrentAimTrack(
        AssemblyDefinition assembly,
        string label,
        bool inspectPrivate)
    {
        TypeDefinition aimTrack = FindType(
            assembly.MainModule,
            "ASWDEBUG.Cheats.AimTrack.AimTrack");
        Require(aimTrack != null, label + " AimTrack type is missing.");

        MethodDefinition enable = FindMethod(aimTrack, "Enable");
        MethodDefinition screenTarget = FindMethod(
            aimTrack,
            "SelectBestTarget");
        MethodDefinition worldTarget = FindMethod(
            aimTrack,
            "SelectBestTargetByM");
        Require(
            enable != null &&
            screenTarget != null &&
            worldTarget != null &&
            FindField(aimTrack, "Enabled") != null &&
            FindField(aimTrack, "currentTarget") != null,
            label + " AimTrack public runtime contract is incomplete.");

        if (!inspectPrivate)
        {
            return;
        }

        MethodDefinition track = FindMethod(aimTrack, "Track");
        MethodDefinition excluded = FindMethod(aimTrack, "IsExcludedWeapon");
        Require(
            track != null &&
            excluded != null &&
            CallsMethod(enable, track) &&
            CallsMethod(track, screenTarget) &&
            CallsMethod(track, worldTarget) &&
            CallsMethod(track, excluded) &&
            MethodReferencesType(excluded, "KnifeBaseController") &&
            MethodReferencesType(excluded, "BowController") &&
            MethodReferencesType(excluded, "RPGController"),
            label + " AimTrack target-selection pipeline is incomplete.");
    }

    private static void VerifyPrecisionTracker(
        AssemblyDefinition assembly,
        string label,
        bool inspectPrivate)
    {
        TypeDefinition aimTrack = FindType(
            assembly.MainModule,
            "ASWDEBUG.Cheats.AimTrack.AimTrack");
        TypeDefinition spread = FindType(
            assembly.MainModule,
            "ASWDEBUG.Cheats.Player.BulletNoRecoil");
        TypeDefinition spreadPatch = FindType(
            assembly.MainModule,
            "ASWDEBUG.Patch_Character_GetSpread_SpreadControl");
        TypeDefinition shootPatch = FindType(
            assembly.MainModule,
            "ASWDEBUG.Patch_ChannelConnection_Shoot_Prefix");

        MethodDefinition resolveMiss = aimTrack == null
            ? null
            : FindMethod(aimTrack, "TryResolveTrackedMiss");
        MethodDefinition scaleSpread = spread == null
            ? null
            : FindMethod(spread, "ScaleNativeSpread");
        MethodDefinition setSpread = spread == null
            ? null
            : FindMethod(spread, "set_SpreadScale");
        MethodDefinition spreadPrefix = spreadPatch == null
            ? null
            : FindMethod(spreadPatch, "Prefix");
        MethodDefinition spreadTarget = spreadPatch == null
            ? null
            : FindMethod(spreadPatch, "TargetMethod");
        MethodDefinition rewrite = shootPatch == null
            ? null
            : FindMethod(shootPatch, "ApplyAimTrackShotCompat");

        Require(
            aimTrack != null &&
            resolveMiss != null &&
            FindField(aimTrack, "RadiusPixels") != null &&
            FindField(aimTrack, "TrackingProbability") != null &&
            FindField(aimTrack, "DrawFovCircle") != null,
            label + " precision-tracking public contract is incomplete.");
        Require(
            spread != null &&
            scaleSpread != null &&
            FindMethod(spread, "get_SpreadScale") != null &&
            setSpread != null &&
            MethodCallsNamed(setSpread, "System.Single", "IsNaN") &&
            MethodCallsNamed(setSpread, "System.Single", "IsInfinity") &&
            FindMethod(spread, "get_RequiresStraightRayFallback") != null,
            label + " adjustable-spread public contract is incomplete.");
        Require(
            spreadTarget != null &&
            spreadPrefix != null &&
            MethodHasString(spreadTarget, "GetSpread") &&
            CallsMethod(spreadPrefix, scaleSpread),
            label + " local Character.GetSpread patch is incomplete.");
        Require(
            rewrite != null &&
            MethodHasString(rewrite, "uid") &&
            CallsMethod(rewrite, resolveMiss) &&
            HasEarlyReturnGuardBeforeCall(rewrite, resolveMiss, "uid") &&
            MethodHasString(rewrite, "part") &&
            MethodHasString(rewrite, "position") &&
            MethodHasString(rewrite, "distance") &&
            MethodHasString(rewrite, "aim_hit_geometry_state"),
            label + " miss-only tracking rewrite is incomplete.");

        if (!inspectPrivate)
        {
            return;
        }

        TypeDefinition pipeline = FindType(
            assembly.MainModule,
            "ASWDEBUG.Cheats.AimTrack.PrecisionTracking");
        TypeDefinition config = FindType(
            assembly.MainModule,
            "ASWDEBUG.Global.FeatureConfigStore");
        TypeDefinition cheatMain = FindType(
            assembly.MainModule,
            "ASWDEBUG.Main.CheatMain");
        MethodDefinition pipelineResolve = pipeline == null
            ? null
            : FindMethod(pipeline, "TryResolveTrackedMiss");
        MethodDefinition targetPoint = pipeline == null
            ? null
            : FindMethod(pipeline, "TryResolveTargetPoint");
        MethodDefinition selectFromOrigin = pipeline == null
            ? null
            : FindMethod(pipeline, "SelectBestTargetFromOrigin");
        MethodDefinition rayToTarget = pipeline == null
            ? null
            : FindMethod(pipeline, "TryRayToTarget");
        MethodDefinition evaluatePoint = pipeline == null
            ? null
            : FindMethod(pipeline, "EvaluatePoint");
        MethodDefinition probability = pipeline == null
            ? null
            : FindMethod(pipeline, "NextProbabilityRoll");
        MethodDefinition loadConfig = config == null
            ? null
            : FindMethod(config, "LoadOnce");
        MethodDefinition tickConfig = config == null
            ? null
            : FindMethod(config, "Tick");
        MethodDefinition saveConfig = config == null
            ? null
            : FindMethod(config, "SaveNow");
        MethodDefinition readConfigFloat = config == null
            ? null
            : FindMethod(config, "ReadFloat");
        MethodDefinition startMain = cheatMain == null
            ? null
            : FindMethod(cheatMain, "Start");
        MethodDefinition updateMain = cheatMain == null
            ? null
            : FindMethod(cheatMain, "Update");
        MethodDefinition destroyMain = cheatMain == null
            ? null
            : FindMethod(cheatMain, "OnDestroy");
        Require(
            pipelineResolve != null &&
            selectFromOrigin != null &&
            targetPoint != null &&
            evaluatePoint != null &&
            rayToTarget != null &&
            probability != null &&
            FindField(pipeline, "PartPriority") != null &&
            CallsMethod(pipelineResolve, selectFromOrigin) &&
            CallsMethod(pipelineResolve, probability) &&
            CallsMethod(selectFromOrigin, targetPoint) &&
            CallsMethod(targetPoint, evaluatePoint) &&
            CallsMethod(evaluatePoint, rayToTarget) &&
            MethodCallsNamed(rayToTarget, "UnityEngine.Physics", "Raycast") &&
            MethodCallsNamed(
                probability,
                "System.Security.Cryptography.RandomNumberGenerator",
                "GetBytes"),
            label + " multi-point LOS/probability pipeline is incomplete.");
        Require(
            loadConfig != null &&
            tickConfig != null &&
            saveConfig != null &&
            readConfigFloat != null &&
            startMain != null &&
            updateMain != null &&
            destroyMain != null &&
            CallsMethod(startMain, loadConfig) &&
            CallsMethod(updateMain, tickConfig) &&
            CallsMethod(destroyMain, saveConfig) &&
            MethodCallsNamed(readConfigFloat, "System.Single", "IsNaN") &&
            MethodCallsNamed(readConfigFloat, "System.Single", "IsInfinity") &&
            MethodHasString(saveConfig, "ballistics.spread_scale") &&
            MethodHasString(saveConfig, "tracking.probability") &&
            MethodHasString(saveConfig, "automation.auto_use"),
            label + " precision profile persistence is incomplete.");

        TypeDefinition tacticalUi = FindType(
            assembly.MainModule,
            "ASWDEBUG.UI.TacticalConsoleUI");
        MethodDefinition display = tacticalUi == null
            ? null
            : FindMethod(tacticalUi, "Display");
        Require(
            display != null &&
            HasStringLiteral(assembly, "NATIVE SPREAD PIPELINE") &&
            HasStringLiteral(assembly, "MISS-ONLY REDIRECTION") &&
            HasStringLiteral(assembly, "FAIL-CLOSED ACCESS"),
            label + " tactical precision UI is incomplete.");
    }

    private static void VerifyUtilityFeatureWiring(
        AssemblyDefinition assembly,
        string label,
        bool inspectPrivate)
    {
        TypeDefinition cheatMain = FindType(
            assembly.MainModule,
            "ASWDEBUG.Main.CheatMain");
        TypeDefinition autoKick = FindType(
            assembly.MainModule,
            "ASWDEBUG.Cheats.Other.AutoKick");
        TypeDefinition other = FindType(
            assembly.MainModule,
            "ASWDEBUG.Cheats.Other.OtherC");
        TypeDefinition matchPatch = FindType(
            assembly.MainModule,
            "ASWDEBUG.Cheats.Other.Patch_NewUIRoom_OpenMatchCheck");
        TypeDefinition cardPatch = FindType(
            assembly.MainModule,
            "ASWDEBUG.Patch_UITakeCardManager_ref_Prefix");
        TypeDefinition config = FindType(
            assembly.MainModule,
            "ASWDEBUG.Global.FeatureConfigStore");

        MethodDefinition mainUpdate = cheatMain == null
            ? null
            : FindMethod(cheatMain, "Update");
        MethodDefinition antiKickUpdate = autoKick == null
            ? null
            : FindMethod(autoKick, "Update");
        MethodDefinition utilityUpdate = other == null
            ? null
            : FindMethod(other, "Update");
        MethodDefinition matchPrefix = matchPatch == null
            ? null
            : FindMethod(matchPatch, "Prefix");
        MethodDefinition cardPostfix = cardPatch == null
            ? null
            : FindMethod(cardPatch, "Postfix");
        MethodDefinition saveConfig = config == null
            ? null
            : FindMethod(config, "SaveNow");

        Require(
            mainUpdate != null &&
            antiKickUpdate != null &&
            utilityUpdate != null &&
            FindField(autoKick, "Enabled") != null &&
            FindField(other, "Enabled") != null &&
            FindField(other, "EnabledVeryify") != null &&
            CallsMethod(mainUpdate, antiKickUpdate) &&
            CallsMethod(mainUpdate, utilityUpdate),
            label + " card-reveal/anti-kick runtime ticks are incomplete.");
        Require(
            matchPrefix != null &&
            MethodReferencesField(matchPrefix, "EnabledVeryify") &&
            MethodHasString(matchPrefix, "[MATCH-CHECK] challenge skipped"),
            label + " game match-check override patch is incomplete.");
        Require(
            cardPostfix != null &&
            MethodReferencesField(cardPostfix, "CardData") &&
            MethodReferencesField(cardPostfix, "stageQuitData"),
            label + " card reward capture patch is incomplete.");
        bool profilePersistenceValid = inspectPrivate
            ? saveConfig != null &&
              MethodHasString(saveConfig, "utility.card_reveal") &&
              MethodHasString(saveConfig, "utility.auto_anti_kick") &&
              MethodHasString(saveConfig, "utility.ignore_match_validation")
            : HasStringLiteral(assembly, "utility.card_reveal") &&
              HasStringLiteral(assembly, "utility.auto_anti_kick") &&
              HasStringLiteral(assembly, "utility.ignore_match_validation");
        Require(
            profilePersistenceValid,
            label + " utility profile persistence is incomplete.");
        Require(
            HasStringLiteral(assembly, "CARD REVEAL") &&
            HasStringLiteral(assembly, "MATCH INTELLIGENCE") &&
            HasStringLiteral(assembly, "AUTH BOUNDARY"),
            label + " utility controls are incomplete.");

        if (!inspectPrivate)
        {
            return;
        }

        TypeDefinition ui = FindType(
            assembly.MainModule,
            "ASWDEBUG.UI.CheatUIManager");
        MethodDefinition display = ui == null
            ? null
            : FindMethod(ui, "Display");
        MethodDefinition cardOverlay = ui == null
            ? null
            : FindMethod(ui, "DisplayTacticalCardOverlay");
        MethodDefinition updateWidget = ui == null
            ? null
            : FindMethod(ui, "UpdateWidgetFromInfo");
        MethodDefinition layoutWidgets = ui == null
            ? null
            : FindMethod(ui, "LayoutWidgetsInArea");
        Require(
            display != null &&
            cardOverlay != null &&
            updateWidget != null &&
            layoutWidgets != null &&
            CallsMethod(display, cardOverlay) &&
            CallsMethod(cardOverlay, updateWidget) &&
            CallsMethod(cardOverlay, layoutWidgets),
            label + " tactical card-reveal overlay is not wired to the captured rewards.");
    }

    private static void VerifyExplosionProtection(
        AssemblyDefinition assembly,
        string label)
    {
        TypeDefinition noDamage = FindType(
            assembly.MainModule,
            "ASWDEBUG.Cheats.Player.GrenadeNotHurt");
        TypeDefinition halfDamage = FindType(
            assembly.MainModule,
            "ASWDEBUG.Cheats.Player.GrenadeHalfHurt");
        TypeDefinition policy = FindType(
            assembly.MainModule,
            "ASWDEBUG.Cheats.Player.ExplosionDamagePolicy");
        TypeDefinition patch = FindType(
            assembly.MainModule,
            "ASWDEBUG.Patch_ChannelConnection_GrenadeHurt_Prefix");

        Require(
            noDamage != null &&
            halfDamage != null &&
            policy != null &&
            patch != null,
            label + " explosion protection types are incomplete.");

        MethodDefinition noDamageSet = FindMethod(noDamage, "SetProbability");
        MethodDefinition noDamageShouldApply = FindMethod(noDamage, "ShouldApply");
        MethodDefinition halfDamageSet = FindMethod(halfDamage, "SetProbability");
        MethodDefinition halfDamageShouldApply = FindMethod(halfDamage, "ShouldApply");
        MethodDefinition resolve = FindMethod(policy, "Resolve");
        MethodDefinition resolveWithSamples = FindMethod(policy, "ResolveWithSamples");
        MethodDefinition prefix = FindMethod(patch, "Prefix");

        Require(
            FindField(noDamage, "Enabled") != null &&
            FindField(noDamage, "Probability") != null &&
            noDamageSet != null &&
            noDamageShouldApply != null &&
            FindField(halfDamage, "Enabled") != null &&
            FindField(halfDamage, "Probability") != null &&
            halfDamageSet != null &&
            halfDamageShouldApply != null,
            label + " explosion probability controls are incomplete.");
        Require(
            resolve != null &&
            resolveWithSamples != null &&
            FindField(policy, "LastNoDamageRoll") != null &&
            FindField(policy, "LastHalfDamageRoll") != null &&
            FindField(policy, "LastDecision") != null &&
            CallsMethod(resolve, resolveWithSamples) &&
            CallsMethod(resolveWithSamples, noDamageShouldApply) &&
            CallsMethod(resolveWithSamples, halfDamageShouldApply),
            label + " explosion probability resolution pipeline is incomplete.");
        Require(
            prefix != null && CallsMethod(prefix, resolve),
            label + " GrenadeHurt patch does not use ExplosionDamagePolicy.");

        bool usesCSPRNG = false;
        foreach (MethodDefinition method in policy.Methods)
        {
            if (MethodCallsNamed(
                method,
                "System.Security.Cryptography.RandomNumberGenerator",
                "GetBytes"))
            {
                usesCSPRNG = true;
                break;
            }
        }
        Require(
            usesCSPRNG,
            label + " explosion probability policy does not use the CSPRNG source.");
        Require(
            HasStringLiteral(assembly, "protection.explosion_no_damage") &&
            HasStringLiteral(
                assembly,
                "protection.explosion_no_damage_probability") &&
            HasStringLiteral(assembly, "protection.explosion_half_damage") &&
            HasStringLiteral(
                assembly,
                "protection.explosion_half_damage_probability"),
            label + " explosion probability profile persistence is incomplete.");
        Require(
            HasStringLiteral(assembly, "EXPLOSION POLICY") &&
            HasStringLiteral(assembly, "OUTCOME MODEL") &&
            HasStringLiteral(assembly, "LAST RESOLUTION"),
            label + " explosion probability menu controls are incomplete.");
    }

    private static void VerifyCurrentAutoAim(
        AssemblyDefinition assembly,
        string label,
        bool inspectPrivate)
    {
        TypeDefinition autoAim = FindType(
            assembly.MainModule,
            "ASWDEBUG.Cheats.AutoAim.AutoAim");
        Require(autoAim != null, label + " AutoAim type is missing.");

        MethodDefinition enable = FindMethod(autoAim, "Enable");
        MethodDefinition select = FindMethod(autoAim, "SelectBestTarget");
        MethodDefinition toggle = FindMethod(autoAim, "ToggleEnabled");
        MethodDefinition recentManipulation = FindMethod(
            autoAim,
            "TryGetRecentManipulation");
        Require(
            enable != null &&
            select != null &&
            toggle != null &&
            recentManipulation != null &&
            FindField(autoAim, "Enabled") != null &&
            FindField(autoAim, "AimLocking") != null &&
            FindField(autoAim, "currentTarget") != null &&
            MethodReferencesField(recentManipulation, "_lastManipulationRealtime") &&
            MethodReferencesField(recentManipulation, "_lastManipulationTargetUid"),
            label + " AutoAim public runtime contract is incomplete.");

        if (!inspectPrivate)
        {
            return;
        }

        MethodDefinition aim = FindMethod(autoAim, "Aim");
        MethodDefinition reset = FindMethod(autoAim, "ResetLockState");
        MethodDefinition recordManipulation = FindMethod(
            autoAim,
            "RecordManipulation");
        Require(
            aim != null &&
            reset != null &&
            recordManipulation != null &&
            CallsMethod(enable, aim) &&
            CallsMethod(aim, select) &&
            CallsMethod(aim, reset) &&
            CallsMethod(aim, recordManipulation) &&
            MethodReferencesField(aim, "finalx") &&
            MethodReferencesField(aim, "finaly"),
            label + " AutoAim camera-control pipeline is incomplete.");
    }

    private static void VerifyAimReportV9Protection(
        AssemblyDefinition assembly,
        string label)
    {
        TypeDefinition loader = FindType(
            assembly.MainModule,
            "ASWDEBUG.Patch.HarmonyLoader");
        Require(loader != null, label + " HarmonyLoader type is missing.");

        MethodDefinition core = FindMethod(loader, "ApplyCoreProtectionPatches");
        MethodDefinition install = FindMethod(loader, "ApplyAimReportV9Patches");
        if (install == null)
        {
            install = core;
        }
        MethodDefinition payloadPrefix = FindMethod(
            loader,
            "Protection_ShootPayloadBuildPrefix");
        MethodDefinition geometryPrefix = FindMethod(
            loader,
            "Protection_AimHitGeometryPrefix");
        MethodDefinition sanitizer = FindMethod(
            loader,
            "Protection_SanitizeShootPayload");
        MethodDefinition normalize = FindMethod(loader, "NormalizeAimReportFields");
        MethodDefinition captureContext = FindMethod(
            loader,
            "CaptureAimShotContext");
        MethodDefinition consumeContext = FindMethod(
            loader,
            "TryConsumeAimShotContext");
        MethodDefinition history = FindMethod(
            loader,
            "HumanizeHistoricalPrecisionRuns");
        MethodDefinition trackHistory = FindMethod(
            loader,
            "HumanizeAimTrackPrecisionHistory");
        MethodDefinition decode = FindMethod(
            loader,
            "TryDecodePrecisionSampleMillimeters");
        MethodDefinition encode = FindMethod(
            loader,
            "EncodePrecisionSampleMillimeters");
        MethodDefinition sampleChecksum = FindMethod(
            loader,
            "ComputePrecisionSampleCheckDigitV9");
        MethodDefinition baseChecksum = FindMethod(
            loader,
            "ComputePrecisionCheckDigitV9");
        MethodDefinition normalizeShot = FindMethod(
            loader,
            "NormalizeShotPrecisionV9");
        MethodDefinition resolveShotPrecision = FindMethod(
            loader,
            "ResolveShotPrecisionMillimeters");
        MethodDefinition shotChecksum = FindMethod(
            loader,
            "ComputeShotPrecisionCheckDigitV9");
        MethodDefinition hitPointChecksum = FindMethod(
            loader,
            "ShouldApplyHitPointChecksumV9");
        MethodDefinition historyMask = FindMethod(
            loader,
            "BuildAimPrecisionAdjustmentMask");
        MethodDefinition shouldHumanizeRun = FindMethod(
            loader,
            "ShouldHumanizePrecisionRun");
        MethodDefinition markPrecisionRun = FindMethod(
            loader,
            "MarkPrecisionRun");
        MethodDefinition historyTail = FindMethod(
            loader,
            "TryGetHumanizedTailPrecisionMillimeters");
        MethodDefinition bulletNoRecoil = FindMethod(
            loader,
            "IsBulletNoRecoilActive");
        MethodDefinition bulletGeometry = FindMethod(
            loader,
            "IsBulletGeometryManipulationActive");
        TypeDefinition autoAim = FindType(
            assembly.MainModule,
            "ASWDEBUG.Cheats.AutoAim.AutoAim");
        MethodDefinition recentManipulation = autoAim == null
            ? null
            : FindMethod(autoAim, "TryGetRecentManipulation");
        TypeDefinition shootPatch = FindType(
            assembly.MainModule,
            "ASWDEBUG.Patch_ChannelConnection_Shoot_Prefix");
        MethodDefinition shootPrefix = shootPatch == null
            ? null
            : FindMethod(shootPatch, "Prefix");

        Require(
            core != null &&
            install != null &&
            (install == core || CallsMethod(core, install)) &&
            MethodHasString(install, "PluginReport") &&
            MethodHasString(install, "AssitToolCheck") &&
            MethodHasString(install, "BuildEncryptedPayload") &&
            MethodHasString(install, "EvaluateBulletTrackingHit"),
            label + " aim-report v9 hooks are incomplete.");
        Require(
            payloadPrefix != null &&
            sanitizer != null &&
            normalize != null &&
            captureContext != null &&
            consumeContext != null &&
            recentManipulation != null &&
            shootPrefix != null &&
            CallsMethod(payloadPrefix, sanitizer) &&
            CallsMethod(shootPrefix, captureContext) &&
            CallsMethod(captureContext, recentManipulation) &&
            CallsMethod(sanitizer, consumeContext) &&
            CallsMethod(sanitizer, normalize) &&
            normalize.Parameters.Count == 3 &&
            MethodHasString(normalize, "aim_report_version") &&
            MethodHasString(normalize, "aim_target_uid") &&
            MethodHasString(normalize, "aim_shot_precision_code") &&
            MethodHasString(normalize, "aim_precision_samples"),
            label + " aim-report v9 payload sanitizer is incomplete.");
        Require(
            history != null &&
            trackHistory != null &&
            normalizeShot != null &&
            resolveShotPrecision != null &&
            historyTail != null &&
            CallsMethod(normalize, history) &&
            CallsMethod(normalize, trackHistory) &&
            CallsMethod(normalize, historyTail) &&
            CallsMethod(normalize, normalizeShot) &&
            CallsMethod(normalizeShot, resolveShotPrecision) &&
            normalizeShot.Parameters.Count == 6,
            label + " aim precision history normalization is incomplete.");
        Require(
            historyMask != null &&
            shouldHumanizeRun != null &&
            markPrecisionRun != null &&
            CallsMethod(historyMask, shouldHumanizeRun) &&
            CallsMethod(historyMask, markPrecisionRun),
            label + " short tail precision runs are not normalized end-to-end.");
        Require(
            decode != null &&
            encode != null &&
            sampleChecksum != null &&
            baseChecksum != null &&
            sampleChecksum.Parameters.Count == 4 &&
            CallsMethod(decode, sampleChecksum) &&
            CallsMethod(encode, sampleChecksum) &&
            CallsMethod(sampleChecksum, baseChecksum),
            label + " first-sample precision checksum support is incomplete.");
        Require(
            shotChecksum != null &&
            hitPointChecksum != null &&
            CallsMethod(normalizeShot, shotChecksum) &&
            CallsMethod(shotChecksum, hitPointChecksum) &&
            MethodHasString(shotChecksum, "position") &&
            MethodHasString(shotChecksum, "aim_hit_geometry_state"),
            label + " shot hit-point checksum support is incomplete.");
        Require(
            geometryPrefix != null &&
            bulletNoRecoil != null &&
            bulletGeometry != null &&
            CallsMethod(normalize, bulletNoRecoil) &&
            CallsMethod(geometryPrefix, bulletGeometry) &&
            MethodHasString(geometryPrefix, "aim_hit_geometry_state"),
            label + " straight-bullet geometry protection is incomplete.");
    }

    private static void VerifyCurrentDetectionProtection(
        AssemblyDefinition assembly,
        string label)
    {
        TypeDefinition loader = FindType(
            assembly.MainModule,
            "ASWDEBUG.Patch.HarmonyLoader");
        Require(loader != null, label + " HarmonyLoader type is missing.");

        MethodDefinition core = FindMethod(loader, "ApplyCoreProtectionPatches");
        MethodDefinition detection = FindMethod(
            loader,
            "ApplyDetectionProtectionPatches");
        MethodDefinition protocol = FindMethod(
            loader,
            "ApplyProtocolMutationPatches");
        MethodDefinition extended = FindMethod(
            loader,
            "ApplyExtendedAntiDetectionPatches");
        Require(
            core != null &&
            detection != null &&
            protocol != null &&
            extended != null &&
            CallsMethod(core, detection) &&
            CallsMethod(core, extended),
            label + " current detection protection installers are incomplete.");

        string[] requiredTargets =
        {
            "ParseProcessCheck",
            "CheckByBlackJosnTable",
            "CheckByUrl",
            "GetClientBinarySummaryMd5",
            "GetEncryptedClientBinarySummaryMd5",
            "ParsePositionCheck",
            "OnClientMessage",
            "HttpRequest"
        };
        foreach (string target in requiredTargets)
        {
            Require(
                MethodHasString(detection, target),
                label + " always-on detection protection target is missing: " + target + ".");
        }

        MethodDefinition launcher = FindMethod(
            loader,
            "Protection_LauncherProcessDataPrefix");
        MethodDefinition upload = FindMethod(
            loader,
            "Protection_FilterGameHttpRequestPrefix");
        MethodDefinition uploadPredicate = FindMethod(
            loader,
            "IsGameDetectionUpload");
        MethodDefinition injectionCallback = FindMethod(
            loader,
            "Protection_BlockGameInjectionCallbackPrefix");
        MethodDefinition clearPending = FindMethod(
            loader,
            "ClearPendingInjectionFlag");
        Require(
            FindMethod(loader, "Protection_ParseProcessCheckPrefix") != null &&
            FindMethod(loader, "Protection_SkipProcessCheckPrefix") != null &&
            FindMethod(loader, "Protection_ClientFileMd5Prefix") != null &&
            FindMethod(loader, "Protection_EncryptedClientFileMd5Prefix") != null &&
            FindMethod(loader, "Protection_ParsePositionCheckPrefix") != null &&
            launcher != null &&
            MethodHasString(launcher, "LAUNCHER-PROCESS-DATA-BLOCKED") &&
            upload != null &&
            uploadPredicate != null &&
            CallsMethod(upload, uploadPredicate) &&
            MethodHasString(uploadPredicate, "filename=waigua.log") &&
            MethodHasString(uploadPredicate, "filename=client_inner.log") &&
            injectionCallback != null &&
            clearPending != null &&
            CallsMethod(injectionCallback, clearPending) &&
            MethodHasString(core, "OnDllInjectionDetected") &&
            MethodHasString(core, "OnAssemblyInjectionDetected") &&
            FindMethod(loader, "Protection_BlockExtendedDetectorPrefix") != null &&
            MethodHasString(extended, "FindInjectionInCurrentAssemblies") &&
            MethodHasString(extended, "IsFromManagedDir"),
            label + " current detection protection handlers are incomplete.");

        Require(
            !MethodHasString(protocol, "ParseProcessCheck") &&
            !MethodHasString(protocol, "ParsePositionCheck") &&
            !MethodHasString(protocol, "OnClientMessage") &&
            !MethodHasString(protocol, "HttpRequest"),
            label + " always-on detection hooks were moved back behind the protocol-mutation gate.");
    }

    private static void VerifyMotherBossAutoClear(
        AssemblyDefinition assembly,
        string label,
        bool inspectPrivate)
    {
        TypeDefinition mother = FindType(
            assembly.MainModule,
            "ASWDEBUG.Cheats.AutoAim.MotherBossAutoClear");
        Require(mother != null, label + " MotherBossAutoClear type is missing.");

        FieldDefinition enabled = FindField(mother, "Enabled");
        MethodDefinition tick = FindMethod(mother, "Tick");
        MethodDefinition toggle = FindMethod(mother, "ToggleEnabled");
        MethodDefinition getSending = FindMethod(
            mother,
            "get_SendingDirectMotherShot");
        MethodDefinition setSending = FindMethod(
            mother,
            "set_SendingDirectMotherShot");
        Require(
            enabled != null &&
            enabled.IsStatic &&
            enabled.FieldType.MetadataType == MetadataType.Boolean &&
            tick != null &&
            toggle != null &&
            getSending != null &&
            setSending != null,
            label + " mother auto-clear public contract is incomplete.");
        MethodDefinition directTick = FindReachableMethodCalling(
            tick,
            "ShootBoss",
            2);
        Require(
            directTick == null,
            label + " expedition boss lock still sends automatic ShootBoss packets.");

        TypeDefinition main = FindType(
            assembly.MainModule,
            "ASWDEBUG.Main.CheatMain");
        MethodDefinition update = main == null ? null : FindMethod(main, "Update");
        Require(
            update != null && CallsMethod(update, tick),
            label + " CheatMain.Update no longer ticks mother auto-clear.");

        TypeDefinition shootPatch = FindType(
            assembly.MainModule,
            "ASWDEBUG.Patch_ChannelConnection_ShootBoss_Prefix");
        MethodDefinition shootPrefix = shootPatch == null
            ? null
            : FindMethod(shootPatch, "Prefix");
        Require(
            shootPrefix != null && CallsMethod(shootPrefix, getSending),
            label + " ShootBoss compatibility guard no longer preserves manual boss shots.");

        if (!inspectPrivate)
        {
            return;
        }

        TypeDefinition lockController = FindType(
            assembly.MainModule,
            "ASWDEBUG.Cheats.AutoAim.ExpeditionBossLockController");
        MethodDefinition lockTick = lockController == null
            ? null
            : FindMethod(lockController, "Tick");
        MethodDefinition captureStageBosses = lockController == null
            ? null
            : FindMethod(lockController, "CaptureActiveStageBoss");
        MethodDefinition captureFreedomBosses = lockController == null
            ? null
            : FindMethod(lockController, "CaptureExistingFreedomBosses");
        MethodDefinition pin = lockController == null
            ? null
            : FindMethod(lockController, "PinAndDisarmBoss");
        MethodDefinition clearAttack = lockController == null
            ? null
            : FindMethod(lockController, "ClearAttackState");
        MethodDefinition getLockPoint = lockController == null
            ? null
            : FindMethod(lockController, "GetLockPoint");
        MethodDefinition trackRegistration = lockController == null
            ? null
            : FindMethod(lockController, "TrackFreedomBossRegistration");
        MethodDefinition trackBorn = lockController == null
            ? null
            : FindMethod(lockController, "TrackBossBorn");
        MethodDefinition applyBossState = lockController == null
            ? null
            : FindMethod(lockController, "ApplyFreedomBossState");
        MethodDefinition lockBlockAttack = lockController == null
            ? null
            : FindMethod(lockController, "ShouldBlockBossAttack");
        MethodDefinition lockIsManagedUid = lockController == null
            ? null
            : FindMethod(lockController, "IsManagedBossUid");
        MethodDefinition restoreManaged = lockController == null
            ? null
            : FindMethod(lockController, "RestoreManagedBosses");
        FieldDefinition lockDistance = lockController == null
            ? null
            : FindField(lockController, "LockDistanceFromPlayer");

        TypeDefinition bridge = FindType(
            assembly.MainModule,
            "ASWDEBUG.Cheats.AutoAim.MotherBossStageController");
        MethodDefinition trackRegistrationBridge = bridge == null
            ? null
            : FindMethod(bridge, "TrackFreedomBossRegistration");
        MethodDefinition trackBornBridge = bridge == null
            ? null
            : FindMethod(bridge, "TrackFreedomBossBorn");
        MethodDefinition applyState = bridge == null
            ? null
            : FindMethod(bridge, "ApplyFreedomBossState");
        MethodDefinition blockAttack = bridge == null
            ? null
            : FindMethod(bridge, "ShouldBlockBossAttack");
        MethodDefinition managedMotherUid = FindMethod(
            mother,
            "IsManagedMotherUid");

        TypeDefinition addFreedomPatch = FindType(
            assembly.MainModule,
            "ASWDEBUG.Cheats.AutoAim.Patch_Level_AddFreedomBoss_MotherClear");
        TypeDefinition refreshBornPatch = FindType(
            assembly.MainModule,
            "ASWDEBUG.Cheats.AutoAim.Patch_BossImpl_RefreshBornPoint_MotherClear");
        TypeDefinition firePatch = FindType(
            assembly.MainModule,
            "ASWDEBUG.Cheats.AutoAim.Patch_BossImpl_Fire_MotherClear");
        TypeDefinition shootBossPatch = FindType(
            assembly.MainModule,
            "ASWDEBUG.Cheats.AutoAim.Patch_BossImpl_Shoot_MotherLock");
        TypeDefinition throwPatch = FindType(
            assembly.MainModule,
            "ASWDEBUG.Cheats.AutoAim.Patch_BossImpl_DoThrow_MotherLock");
        TypeDefinition attackStatusPatch = FindType(
            assembly.MainModule,
            "ASWDEBUG.Cheats.AutoAim.Patch_BossImpl_SetSlotAttackStatus_MotherClear");
        TypeDefinition activationPatch = FindType(
            assembly.MainModule,
            "ASWDEBUG.Cheats.AutoAim.Patch_Level_ActiveAllFreedomBosses_MotherLock");
        TypeDefinition getBossPatch = FindType(
            assembly.MainModule,
            "ASWDEBUG.Cheats.AutoAim.Patch_Level_GetBossByUID_MotherClear");
        TypeDefinition damagePatch = FindType(
            assembly.MainModule,
            "ASWDEBUG.Cheats.AutoAim.Patch_ChannelConnection_TakeEffectFromBoss_MotherClear");
        MethodDefinition addFreedomPrefix = addFreedomPatch == null
            ? null
            : FindMethod(addFreedomPatch, "Prefix");
        MethodDefinition addFreedomPostfix = addFreedomPatch == null
            ? null
            : FindMethod(addFreedomPatch, "Postfix");
        MethodDefinition refreshBornPrefix = refreshBornPatch == null
            ? null
            : FindMethod(refreshBornPatch, "Prefix");
        MethodDefinition firePrefix = firePatch == null
            ? null
            : FindMethod(firePatch, "Prefix");
        MethodDefinition shootBossPrefix = shootBossPatch == null
            ? null
            : FindMethod(shootBossPatch, "Prefix");
        MethodDefinition throwPrefix = throwPatch == null
            ? null
            : FindMethod(throwPatch, "Prefix");
        MethodDefinition attackStatusPrefix = attackStatusPatch == null
            ? null
            : FindMethod(attackStatusPatch, "Prefix");
        MethodDefinition damagePrefix = damagePatch == null
            ? null
            : FindMethod(damagePatch, "Prefix");

        Require(
            lockController != null &&
            lockTick != null &&
            CallsMethod(tick, lockTick) &&
            captureStageBosses != null &&
            captureFreedomBosses != null &&
            pin != null &&
            clearAttack != null &&
            getLockPoint != null &&
            CallsMethod(lockTick, captureStageBosses) &&
            CallsMethod(lockTick, captureFreedomBosses) &&
            CallsMethod(lockTick, getLockPoint) &&
            CallsMethod(lockTick, pin) &&
            MethodReferencesField(captureFreedomBosses, "freedom_boss_manager") &&
            CountCallsByName(captureStageBosses, "GetActiveStageBoss") >= 1 &&
            CountCallsByName(captureStageBosses, "GetBosses") == 0 &&
            CountCallsByName(captureFreedomBosses, "GetBosses") >= 1 &&
            MethodReferencesField(captureStageBosses, "start_sync_boss_data") &&
            MethodReferencesField(pin, "start_sync_boss_data") &&
            CountCallsByName(pin, "SetUpdatePostion") >= 1 &&
            CountCallsByName(pin, "UseGravity") >= 1 &&
            CountCallsByName(pin, "SetActive") == 0 &&
            CountCallsByName(pin, "SetWeaponEnable") >= 1 &&
            CountCallsByName(pin, "SetPosition") >= 1 &&
            CountCallsByName(pin, "set_position") >= 1 &&
            CallsMethod(pin, clearAttack) &&
            CountCallsByName(clearAttack, "SetSlotAttackStatusAndPosition") >= 1 &&
            lockDistance != null &&
            lockDistance.HasConstant &&
            Math.Abs(Convert.ToSingle(lockDistance.Constant) - 6f) < 0.0001f,
            label + " active server boss capture, 6-meter pinning, or disarm logic is incomplete.");

        Require(
            trackRegistrationBridge != null &&
            trackBornBridge != null &&
            applyState != null &&
            blockAttack != null &&
            trackRegistration != null &&
            trackBorn != null &&
            applyBossState != null &&
            lockBlockAttack != null &&
            lockIsManagedUid != null &&
            restoreManaged != null &&
            CallsMethod(trackRegistrationBridge, trackRegistration) &&
            CallsMethod(trackBornBridge, trackBorn) &&
            CallsMethod(applyState, applyBossState) &&
            CallsMethod(blockAttack, lockBlockAttack) &&
            FindField(lockController, "RegisteredFreedomTemplates") == null &&
            FindMethod(lockController, "SuppressClone") == null &&
            MethodReferencesField(trackRegistration, "RegistrationAccepted") &&
            MethodReferencesField(trackBorn, "BornPoint") &&
            CountCallsByName(trackRegistration, "SetActive") == 0 &&
            CountCallsByName(trackBorn, "SetActive") == 0 &&
            addFreedomPrefix != null &&
            addFreedomPrefix.ReturnType.MetadataType == MetadataType.Void &&
            addFreedomPostfix != null &&
            refreshBornPrefix != null &&
            refreshBornPrefix.ReturnType.MetadataType == MetadataType.Void &&
            activationPatch == null &&
            getBossPatch == null &&
            attackStatusPrefix != null &&
            firePrefix != null &&
            shootBossPrefix != null &&
            throwPrefix != null &&
            damagePrefix != null &&
            CallsMethod(addFreedomPrefix, trackRegistrationBridge) &&
            CallsMethod(addFreedomPostfix, applyState) &&
            CallsMethod(refreshBornPrefix, trackBornBridge) &&
            CallsMethod(attackStatusPrefix, blockAttack) &&
            CallsMethod(firePrefix, blockAttack) &&
            CallsMethod(shootBossPrefix, blockAttack) &&
            CallsMethod(throwPrefix, blockAttack) &&
            CallsMethod(damagePrefix, blockAttack) &&
            managedMotherUid != null &&
            CallsMethod(managedMotherUid, lockIsManagedUid) &&
            CallsMethod(shootPrefix, managedMotherUid),
            label + " server instance tracking, attack blocking, or UID integrity is incomplete.");

        TypeDefinition ui = FindType(
            assembly.MainModule,
            "ASWDEBUG.UI.CheatUIManager");
        MethodDefinition display = ui == null ? null : FindMethod(ui, "Display");
        Require(
            display != null &&
            CallsMethod(display, toggle),
            label + " mother auto-clear UI wiring is incomplete.");
    }

    private static void VerifyQuestFarmRemoved(
        AssemblyDefinition assembly,
        string label)
    {
        Require(
            FindType(
                assembly.MainModule,
                "ASWDEBUG.Cheats.Quest.QuestRepeatFarmManager") == null &&
            FindType(
                assembly.MainModule,
                "ASWDEBUG.UI.QuestRepeatFarmPanel") == null &&
            !HasStringLiteral(assembly, "ASW_QuestRepeatFarmCache.txt") &&
            !HasStringLiteral(assembly, "QUEST-FARM"),
            label + " still contains Quest repeat-farm code.");
    }

    private static MethodDefinition FindReachableMethodCalling(
        MethodDefinition root,
        string expectedCall,
        int remainingDepth)
    {
        if (root == null || !root.HasBody)
        {
            return null;
        }
        if (CountCallsByName(root, expectedCall) > 0)
        {
            return root;
        }
        if (remainingDepth <= 0)
        {
            return null;
        }

        foreach (Instruction instruction in root.Body.Instructions)
        {
            MethodReference reference = instruction.Operand as MethodReference;
            if (reference == null)
            {
                continue;
            }

            MethodDefinition called = null;
            try
            {
                called = reference.Resolve();
            }
            catch
            {
                called = null;
            }
            if (called == null || called.Module != root.Module)
            {
                continue;
            }

            MethodDefinition match = FindReachableMethodCalling(
                called,
                expectedCall,
                remainingDepth - 1);
            if (match != null)
            {
                return match;
            }
        }
        return null;
    }

    private static int CountCallsByName(
        MethodDefinition method,
        string expectedName)
    {
        if (method == null || !method.HasBody)
        {
            return 0;
        }

        int count = 0;
        foreach (Instruction instruction in method.Body.Instructions)
        {
            MethodReference called = instruction.Operand as MethodReference;
            if (called != null &&
                string.Equals(called.Name, expectedName, StringComparison.Ordinal))
            {
                count++;
            }
        }
        return count;
    }

    private static bool CallHasFalseLastArgument(
        MethodDefinition method,
        string expectedName)
    {
        if (method == null || !method.HasBody)
        {
            return false;
        }

        foreach (Instruction instruction in method.Body.Instructions)
        {
            MethodReference called = instruction.Operand as MethodReference;
            if (called == null ||
                !string.Equals(called.Name, expectedName, StringComparison.Ordinal))
            {
                continue;
            }

            Instruction previous = instruction.Previous;
            if (previous != null && previous.OpCode == OpCodes.Ldc_I4_0)
            {
                return true;
            }
        }
        return false;
    }

    private static bool MethodHasOpcode(MethodDefinition method, Code code)
    {
        if (method == null || !method.HasBody)
        {
            return false;
        }
        foreach (Instruction instruction in method.Body.Instructions)
        {
            if (instruction.OpCode.Code == code)
            {
                return true;
            }
        }
        return false;
    }

    private static bool MethodReferencesType(
        MethodDefinition method,
        string expectedFullName)
    {
        if (method == null || !method.HasBody)
        {
            return false;
        }

        foreach (Instruction instruction in method.Body.Instructions)
        {
            TypeReference type = instruction.Operand as TypeReference;
            if (type != null &&
                string.Equals(
                    type.FullName,
                    expectedFullName,
                    StringComparison.Ordinal))
            {
                return true;
            }

            MethodReference called = instruction.Operand as MethodReference;
            if (called != null &&
                called.DeclaringType != null &&
                string.Equals(
                    called.DeclaringType.FullName,
                    expectedFullName,
                    StringComparison.Ordinal))
            {
                return true;
            }
        }
        return false;
    }

    private static bool MethodReferencesField(
        MethodDefinition method,
        string expectedName)
    {
        if (method == null || !method.HasBody)
        {
            return false;
        }

        foreach (Instruction instruction in method.Body.Instructions)
        {
            FieldReference field = instruction.Operand as FieldReference;
            if (field != null &&
                string.Equals(
                    field.Name,
                    expectedName,
                    StringComparison.Ordinal))
            {
                return true;
            }
        }
        return false;
    }

    private static bool MethodWritesField(
        MethodDefinition method,
        string declaringTypeName)
    {
        if (method == null || !method.HasBody)
        {
            return false;
        }

        foreach (Instruction instruction in method.Body.Instructions)
        {
            if (instruction.OpCode.Code != Code.Stfld &&
                instruction.OpCode.Code != Code.Stsfld)
            {
                continue;
            }

            FieldReference field = instruction.Operand as FieldReference;
            if (field == null || field.DeclaringType == null)
            {
                continue;
            }

            if (string.Equals(
                    field.DeclaringType.Name,
                    declaringTypeName,
                    StringComparison.Ordinal) ||
                string.Equals(
                    field.DeclaringType.FullName,
                    declaringTypeName,
                    StringComparison.Ordinal))
            {
                return true;
            }
        }
        return false;
    }

    private static bool CallsMethod(
        MethodDefinition caller,
        MethodDefinition target)
    {
        if (caller == null || target == null || !caller.HasBody)
        {
            return false;
        }

        foreach (Instruction instruction in caller.Body.Instructions)
        {
            MethodReference reference = instruction.Operand as MethodReference;
            if (reference != null &&
                string.Equals(
                    reference.FullName,
                    target.FullName,
                    StringComparison.Ordinal))
            {
                return true;
            }
        }
        return false;
    }

    private static bool HasEarlyReturnGuardBeforeCall(
        MethodDefinition caller,
        MethodDefinition target,
        string guardedField)
    {
        if (caller == null || target == null || !caller.HasBody)
        {
            return false;
        }

        Mono.Collections.Generic.Collection<Instruction> instructions =
            caller.Body.Instructions;
        int callIndex = -1;
        for (int i = 0; i < instructions.Count; i++)
        {
            MethodReference reference = instructions[i].Operand as MethodReference;
            if (reference != null && string.Equals(
                reference.FullName,
                target.FullName,
                StringComparison.Ordinal))
            {
                callIndex = i;
                break;
            }
        }
        if (callIndex < 0) return false;

        for (int literalIndex = 0; literalIndex < callIndex; literalIndex++)
        {
            string literal = instructions[literalIndex].Operand as string;
            if (!string.Equals(literal, guardedField, StringComparison.Ordinal))
                continue;

            for (int branchIndex = literalIndex + 1;
                 branchIndex < callIndex;
                 branchIndex++)
            {
                Instruction branch = instructions[branchIndex];
                if (branch.OpCode.FlowControl != FlowControl.Cond_Branch)
                    continue;

                Instruction continuation = branch.Operand as Instruction;
                int continuationIndex = continuation == null
                    ? -1
                    : instructions.IndexOf(continuation);
                if (continuationIndex <= branchIndex || continuationIndex >= callIndex)
                    continue;

                for (int guardedIndex = branchIndex + 1;
                     guardedIndex < continuationIndex;
                     guardedIndex++)
                {
                    Code code = instructions[guardedIndex].OpCode.Code;
                    if (code == Code.Ret || code == Code.Leave || code == Code.Leave_S)
                        return true;
                }
            }
        }
        return false;
    }

    private static bool MethodCallsNamed(
        MethodDefinition caller,
        string declaringType,
        string methodName)
    {
        if (caller == null || !caller.HasBody)
        {
            return false;
        }

        foreach (Instruction instruction in caller.Body.Instructions)
        {
            MethodReference reference = instruction.Operand as MethodReference;
            if (reference != null &&
                reference.DeclaringType != null &&
                string.Equals(
                    reference.DeclaringType.FullName,
                    declaringType,
                    StringComparison.Ordinal) &&
                string.Equals(
                    reference.Name,
                    methodName,
                    StringComparison.Ordinal))
            {
                return true;
            }
        }
        return false;
    }

    private static bool MethodHasString(
        MethodDefinition method,
        string expected)
    {
        if (method == null || !method.HasBody)
        {
            return false;
        }

        foreach (Instruction instruction in method.Body.Instructions)
        {
            string literal = instruction.Operand as string;
            if (literal != null &&
                literal.IndexOf(expected, StringComparison.Ordinal) >= 0)
            {
                return true;
            }
        }
        return false;
    }

    private static void VerifyAssembly(
        AssemblyDefinition assembly,
        string label,
        string expectedVersion)
    {
        Require(
            string.Equals(
                assembly.Name.Version.ToString(),
                expectedVersion,
                StringComparison.Ordinal),
            label + " assembly version is " + assembly.Name.Version +
            ", expected " + expectedVersion + ".");
        Require(
            assembly.MainModule.Runtime == TargetRuntime.Net_2_0 &&
            assembly.MainModule.RuntimeVersion.StartsWith(
                "v2.0",
                StringComparison.OrdinalIgnoreCase),
            label + " assembly no longer targets CLR 2.0: " +
            assembly.MainModule.Runtime + " / " + assembly.MainModule.RuntimeVersion + ".");
        Require(
            !HasUnsafeDebugging(assembly.CustomAttributes) &&
            !HasUnsafeDebugging(assembly.MainModule.CustomAttributes),
            label + " assembly enables JIT tracking, edit-and-continue, or " +
            "disabled optimizations through DebuggableAttribute.");

        foreach (TypeDefinition type in AllTypes(assembly.MainModule))
        {
            VerifyText(type.FullName, label + " type");
            foreach (FieldDefinition field in type.Fields)
            {
                VerifyText(field.Name, label + " field");
            }
            foreach (MethodDefinition method in type.Methods)
            {
                VerifyText(method.Name, label + " method");
                if (!method.HasBody)
                {
                    continue;
                }

                foreach (Instruction instruction in method.Body.Instructions)
                {
                    string literal = instruction.Operand as string;
                    if (literal != null)
                    {
                        VerifyText(literal, label + " string literal");
                    }
                }
            }
        }
    }

    private static void VerifyAuthorizationGate(
        AssemblyDefinition assembly,
        string label)
    {
        TypeDefinition consoleManager = FindType(assembly.MainModule, "ConsoleManager");
        Require(consoleManager != null, label + " ConsoleManager type is missing.");

        foreach (FieldDefinition field in consoleManager.Fields)
        {
            Require(
                !string.Equals(
                    field.Name,
                    "NetworkAuthEnabled",
                    StringComparison.Ordinal),
                label + " ConsoleManager still contains NetworkAuthEnabled.");
        }

        MethodDefinition start = FindMethod(consoleManager, "Start");
        Require(start != null && start.HasBody, label + " ConsoleManager.Start is missing.");
        foreach (Instruction instruction in start.Body.Instructions)
        {
            MethodReference called = instruction.Operand as MethodReference;
            if (called == null)
            {
                continue;
            }

            Require(
                !string.Equals(called.Name, "BootCheatMain", StringComparison.Ordinal) &&
                !string.Equals(called.Name, "InstallAuthorized", StringComparison.Ordinal),
                label + " ConsoleManager.Start directly calls " + called.FullName +
                "; authorization must complete through the login callback.");
        }
    }

    private static void VerifyEntryPoint(AssemblyDefinition assembly)
    {
        TypeDefinition entrypoint = FindType(assembly.MainModule, "Doorstop.Entrypoint");
        Require(entrypoint != null, "Protected Doorstop.Entrypoint is missing.");
        MethodDefinition startInjected = FindMethod(entrypoint, "StartInjected");
        Require(
            startInjected != null &&
            startInjected.HasBody &&
            startInjected.IsStatic &&
            startInjected.IsPublic,
            "Protected Doorstop.Entrypoint.StartInjected must remain public and static.");

        bool hasMonoCompatibleLiteral = false;
        foreach (Instruction instruction in startInjected.Body.Instructions)
        {
            string literal = instruction.Operand as string;
            if (string.Equals(
                literal,
                "Doorstop.Entrypoint.StartInjected() called",
                StringComparison.Ordinal))
            {
                hasMonoCompatibleLiteral = true;
                break;
            }
        }
        Require(
            hasMonoCompatibleLiteral,
            "Protected StartInjected uses a generated string decoder; " +
            "Unity 4.7 legacy Mono requires direct string literals.");

        MethodDefinition schedule = FindMethod(
            entrypoint,
            "ScheduleInjectedBootstrap");
        MethodDefinition tryLoom = FindMethod(
            entrypoint,
            "TryQueueBootstrapWithLoom");
        MethodDefinition patchGameEntry = FindMethod(
            entrypoint,
            "PatchGameEntryCore");
        MethodDefinition gameEntryPostfix = FindMethod(
            entrypoint,
            "GameEntryPostfix");
        MethodDefinition runInjected = FindMethod(
            entrypoint,
            "RunInjectedBootstrapOnMainThread");
        Require(
            schedule != null &&
            tryLoom != null &&
            patchGameEntry != null &&
            CallsMethod(schedule, tryLoom) &&
            CallsMethod(schedule, patchGameEntry),
            "Protected injected bootstrap must retain Loom preference and " +
            "the GameApp.Update fallback.");
        Require(
            gameEntryPostfix != null &&
            runInjected != null &&
            CallsMethod(gameEntryPostfix, runInjected),
            "Protected GameApp.Update fallback no longer reaches the " +
            "injected main-thread bootstrap.");
    }

    private static void VerifyInfiniteItemDefaultDisabled(
        AssemblyDefinition assembly)
    {
        TypeDefinition type = FindType(
            assembly.MainModule,
            "ASWDEBUG.Patch.InfiniteItemUse");
        Require(type != null, "Raw InfiniteItemUse type is missing.");

        FieldDefinition enabled = null;
        foreach (FieldDefinition field in type.Fields)
        {
            if (string.Equals(
                field.Name,
                "Enabled",
                StringComparison.Ordinal))
            {
                enabled = field;
                break;
            }
        }
        Require(
            enabled != null &&
            enabled.IsStatic &&
            enabled.FieldType.MetadataType == MetadataType.Boolean,
            "InfiniteItemUse.Enabled static Boolean field is missing.");

        MethodDefinition initializer = FindMethod(type, ".cctor");
        if (initializer == null || !initializer.HasBody)
        {
            return;
        }
        foreach (Instruction instruction in initializer.Body.Instructions)
        {
            FieldReference target = instruction.Operand as FieldReference;
            if (instruction.OpCode != OpCodes.Stsfld ||
                target == null ||
                !string.Equals(
                    target.FullName,
                    enabled.FullName,
                    StringComparison.Ordinal))
            {
                continue;
            }

            Instruction value = instruction.Previous;
            Require(
                value != null &&
                (value.OpCode == OpCodes.Ldc_I4_0 ||
                 (value.OpCode == OpCodes.Ldc_I4 &&
                  Convert.ToInt32(value.Operand) == 0) ||
                 (value.OpCode == OpCodes.Ldc_I4_S &&
                  Convert.ToInt32(value.Operand) == 0)),
                "InfiniteItemUse.Enabled must default to false.");
        }
    }

    private static void VerifyInfiniteAmmoDefaultDisabled(
        AssemblyDefinition assembly,
        string label)
    {
        TypeDefinition type = FindType(
            assembly.MainModule,
            "ASWDEBUG.Cheats.Other.InfiniteAmmo");
        Require(type != null, label + " InfiniteAmmo type is missing.");

        FieldDefinition enabled = FindField(type, "Enabled");
        Require(
            enabled != null &&
            enabled.IsStatic &&
            enabled.FieldType.MetadataType == MetadataType.Boolean,
            label + " InfiniteAmmo.Enabled static Boolean field is missing.");

        MethodDefinition initializer = FindMethod(type, ".cctor");
        if (initializer == null || !initializer.HasBody)
        {
            return;
        }

        foreach (Instruction instruction in initializer.Body.Instructions)
        {
            FieldReference target = instruction.Operand as FieldReference;
            if (instruction.OpCode != OpCodes.Stsfld ||
                target == null ||
                !string.Equals(
                    target.FullName,
                    enabled.FullName,
                    StringComparison.Ordinal))
            {
                continue;
            }

            Instruction value = instruction.Previous;
            Require(
                value != null &&
                (value.OpCode == OpCodes.Ldc_I4_0 ||
                 (value.OpCode == OpCodes.Ldc_I4 &&
                  Convert.ToInt32(value.Operand) == 0) ||
                 (value.OpCode == OpCodes.Ldc_I4_S &&
                  Convert.ToInt32(value.Operand) == 0)),
                label + " InfiniteAmmo.Enabled must default to false.");
        }
    }

    private static void VerifyFlightModeDefaultDisabled(
        AssemblyDefinition assembly,
        string label)
    {
        TypeDefinition type = FindType(
            assembly.MainModule,
            "ASWDEBUG.Cheats.Other.FlightMode");
        Require(type != null, label + " FlightMode type is missing.");

        FieldDefinition enabled = FindField(type, "Enabled");
        Require(
            enabled != null &&
            enabled.IsStatic &&
            enabled.FieldType.MetadataType == MetadataType.Boolean,
            label + " FlightMode.Enabled static Boolean field is missing.");

        MethodDefinition initializer = FindMethod(type, ".cctor");
        if (initializer == null || !initializer.HasBody)
        {
            return;
        }

        foreach (Instruction instruction in initializer.Body.Instructions)
        {
            FieldReference target = instruction.Operand as FieldReference;
            if (instruction.OpCode != OpCodes.Stsfld ||
                target == null ||
                !string.Equals(
                    target.FullName,
                    enabled.FullName,
                    StringComparison.Ordinal))
            {
                continue;
            }

            Instruction value = instruction.Previous;
            Require(
                value != null &&
                (value.OpCode == OpCodes.Ldc_I4_0 ||
                 (value.OpCode == OpCodes.Ldc_I4 &&
                  Convert.ToInt32(value.Operand) == 0) ||
                 (value.OpCode == OpCodes.Ldc_I4_S &&
                  Convert.ToInt32(value.Operand) == 0)),
                label + " FlightMode.Enabled must default to false.");
        }
    }

    private static void VerifyConventionMethods(
        AssemblyDefinition raw,
        AssemblyDefinition protectedAssembly)
    {
        Dictionary<string, int> rawUnity = CountUnityMessages(raw.MainModule);
        Dictionary<string, int> protectedUnity = CountUnityMessages(protectedAssembly.MainModule);
        foreach (KeyValuePair<string, int> pair in rawUnity)
        {
            int protectedCount;
            protectedUnity.TryGetValue(pair.Key, out protectedCount);
            Require(
                pair.Value == protectedCount,
                "Unity message " + pair.Key + " count changed from " + pair.Value +
                " to " + protectedCount + ".");
        }

        foreach (string name in HarmonyConventionNames)
        {
            int rawCount = CountMethods(raw.MainModule, name);
            int protectedCount = CountMethods(protectedAssembly.MainModule, name);
            Require(rawCount > 0, "Raw Harmony convention method is missing: " + name + ".");
            Require(
                rawCount == protectedCount,
                "Harmony convention method " + name + " count changed from " +
                rawCount + " to " + protectedCount + ".");
        }
    }

    private static void VerifyObfuscation(
        string rawPath,
        string protectedPath,
        string mappingPath)
    {
        Require(
            !HashesEqual(rawPath, protectedPath),
            "Protected assembly is byte-identical to the raw assembly.");

        XmlDocument mapping = new XmlDocument();
        mapping.Load(mappingPath);
        int renamed = 0;
        XmlNodeList elements = mapping.SelectNodes("//*");
        foreach (XmlNode element in elements)
        {
            if (element.Attributes == null)
            {
                continue;
            }

            string status = GetAttribute(element, "status");
            string oldName = GetAttribute(element, "oldName");
            string newName = GetAttribute(element, "newName");
            if ((!string.IsNullOrEmpty(status) &&
                 string.Equals(status, "renamed", StringComparison.OrdinalIgnoreCase)) ||
                (!string.IsNullOrEmpty(oldName) && !string.IsNullOrEmpty(newName) &&
                 !string.Equals(oldName, newName, StringComparison.Ordinal)))
            {
                renamed++;
            }
        }

        Require(
            renamed >= 10,
            "Obfuscation mapping contains only " + renamed +
            " renamed items; expected at least 10.");
    }

    private static Dictionary<string, int> CountUnityMessages(ModuleDefinition module)
    {
        Dictionary<string, TypeDefinition> byName = new Dictionary<string, TypeDefinition>(
            StringComparer.Ordinal);
        foreach (TypeDefinition type in AllTypes(module))
        {
            byName[type.FullName] = type;
        }

        Dictionary<string, int> result = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (TypeDefinition type in AllTypes(module))
        {
            if (!DerivesFromMonoBehaviour(type, byName))
            {
                continue;
            }

            foreach (MethodDefinition method in type.Methods)
            {
                if (!UnityMessageNames.Contains(method.Name))
                {
                    continue;
                }

                int count;
                result.TryGetValue(method.Name, out count);
                result[method.Name] = count + 1;
            }
        }
        return result;
    }

    private static bool DerivesFromMonoBehaviour(
        TypeDefinition type,
        IDictionary<string, TypeDefinition> byName)
    {
        HashSet<string> seen = new HashSet<string>(StringComparer.Ordinal);
        TypeReference current = type.BaseType;
        while (current != null && seen.Add(current.FullName))
        {
            if (string.Equals(
                current.FullName,
                "UnityEngine.MonoBehaviour",
                StringComparison.Ordinal))
            {
                return true;
            }

            TypeDefinition local;
            if (!byName.TryGetValue(current.FullName, out local))
            {
                break;
            }
            current = local.BaseType;
        }
        return false;
    }

    private static int CountMethods(ModuleDefinition module, string name)
    {
        int count = 0;
        foreach (TypeDefinition type in AllTypes(module))
        {
            foreach (MethodDefinition method in type.Methods)
            {
                if (string.Equals(method.Name, name, StringComparison.Ordinal))
                {
                    count++;
                }
            }
        }
        return count;
    }

    private static bool HasUnsafeDebugging(IEnumerable<CustomAttribute> attributes)
    {
        foreach (CustomAttribute attribute in attributes)
        {
            if (!string.Equals(
                attribute.AttributeType.FullName,
                "System.Diagnostics.DebuggableAttribute",
                StringComparison.Ordinal))
            {
                continue;
            }

            if (attribute.ConstructorArguments.Count == 1)
            {
                int modes = Convert.ToInt32(attribute.ConstructorArguments[0].Value);
                const int Default = 1;
                const int EnableEditAndContinue = 4;
                const int DisableOptimizations = 256;
                return (modes & (Default | EnableEditAndContinue | DisableOptimizations)) != 0;
            }
            if (attribute.ConstructorArguments.Count == 2)
            {
                bool jitTracking = Convert.ToBoolean(
                    attribute.ConstructorArguments[0].Value);
                bool disableOptimizations = Convert.ToBoolean(
                    attribute.ConstructorArguments[1].Value);
                return jitTracking || disableOptimizations;
            }

            return true;
        }
        return false;
    }

    private static string GetAttribute(XmlNode node, string name)
    {
        if (node.Attributes == null)
        {
            return null;
        }
        foreach (XmlAttribute attribute in node.Attributes)
        {
            if (string.Equals(attribute.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                return attribute.Value;
            }
        }
        return null;
    }

    private static void VerifyText(string value, string source)
    {
        if (value == null)
        {
            return;
        }
        foreach (string forbidden in ForbiddenText)
        {
            Require(
                value.IndexOf(forbidden, StringComparison.OrdinalIgnoreCase) < 0,
                source + " contains forbidden authorization bypass text: " + forbidden + ".");
        }
    }

    private static TypeDefinition FindType(ModuleDefinition module, string fullName)
    {
        foreach (TypeDefinition type in AllTypes(module))
        {
            if (string.Equals(type.FullName, fullName, StringComparison.Ordinal))
            {
                return type;
            }
        }
        return null;
    }

    private static MethodDefinition FindMethod(TypeDefinition type, string name)
    {
        foreach (MethodDefinition method in type.Methods)
        {
            if (string.Equals(method.Name, name, StringComparison.Ordinal))
            {
                return method;
            }
        }
        return null;
    }

    private static MethodDefinition FindMethodWithString(
        TypeDefinition type,
        string value)
    {
        foreach (TypeDefinition candidateType in AllTypes(type))
        {
            foreach (MethodDefinition method in candidateType.Methods)
            {
                if (MethodHasString(method, value))
                {
                    return method;
                }
            }
        }
        return null;
    }

    private static FieldDefinition FindField(TypeDefinition type, string name)
    {
        foreach (FieldDefinition field in type.Fields)
        {
            if (string.Equals(field.Name, name, StringComparison.Ordinal))
            {
                return field;
            }
        }
        return null;
    }

    private static IEnumerable<TypeDefinition> AllTypes(ModuleDefinition module)
    {
        foreach (TypeDefinition type in module.Types)
        {
            foreach (TypeDefinition nested in AllTypes(type))
            {
                yield return nested;
            }
        }
    }

    private static IEnumerable<TypeDefinition> AllTypes(TypeDefinition type)
    {
        yield return type;
        foreach (TypeDefinition nested in type.NestedTypes)
        {
            foreach (TypeDefinition descendant in AllTypes(nested))
            {
                yield return descendant;
            }
        }
    }

    private static bool HashesEqual(string leftPath, string rightPath)
    {
        using (SHA256 sha256 = SHA256.Create())
        {
            byte[] left;
            byte[] right;
            using (FileStream stream = File.OpenRead(leftPath))
            {
                left = sha256.ComputeHash(stream);
            }
            using (FileStream stream = File.OpenRead(rightPath))
            {
                right = sha256.ComputeHash(stream);
            }

            if (left.Length != right.Length)
            {
                return false;
            }
            for (int index = 0; index < left.Length; index++)
            {
                if (left[index] != right[index])
                {
                    return false;
                }
            }
            return true;
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
