using System;
#if !DOORSTOP_BUILD
using System.Diagnostics;
#endif
using System.IO;
using System.Reflection;
#if !DOORSTOP_BUILD
using System.Threading;
#endif
using Harmony;
using UnityEngine;

namespace Doorstop
{
    /// <summary>
    /// Supports both early Doorstop loading and late Mono runtime injection.
    /// Unity objects are created only from a Unity main-thread callback.
    /// </summary>
    public static class Entrypoint
    {
#if DOORSTOP_BUILD
        public const string BuildMode = "Doorstop";
#else
        public const string BuildMode = "InjectedRuntime";
#endif
        private static readonly object PatchSync = new object();
        private static bool _patched;
        private static bool _assemblyLoadSubscribed;
        private static bool _bootstrapping;
        private static bool _bootstrapped;
#if !DOORSTOP_BUILD
        private static bool _injectedLoad;
        private static bool _injectedBootstrapScheduled;
        private static EventWaitHandle _authorizationHandoff;
#endif

        internal static bool IsInjectedLoad
        {
#if DOORSTOP_BUILD
            get { return false; }
#else
            get { return _injectedLoad; }
#endif
        }

        public static void Start()
        {
            try
            {
                LogInfo("Doorstop.Entrypoint.Start() called");

                Assembly assemblyCSharp = FindAssembly("Assembly-CSharp");
                if (assemblyCSharp != null)
                {
                    PatchGameEntry(assemblyCSharp, false);
                }
                else if (!_assemblyLoadSubscribed)
                {
                    _assemblyLoadSubscribed = true;
                    AppDomain.CurrentDomain.AssemblyLoad += OnAssemblyLoad;
                    LogInfo("Waiting for Assembly-CSharp...");
                }
            }
            catch (Exception ex)
            {
                LogInfo("Start error: " + ex);
            }
        }

#if !DOORSTOP_BUILD
        public static void StartInjected()
        {
            try
            {
                LogInfo("Doorstop.Entrypoint.StartInjected() called");
                _injectedLoad = true;
                Assembly assemblyCSharp = WaitForAssembly("Assembly-CSharp", 30000);
                if (assemblyCSharp == null)
                {
                    throw new InvalidOperationException(
                        "Assembly-CSharp was not loaded within 30 seconds.");
                }

                NeutralizeLateInjectionDetectors(assemblyCSharp);
                string eventName =
                    "Local\\x1a0reN.Launcher.VeriGate.Handoff." +
                    Process.GetCurrentProcess().Id;
                _authorizationHandoff = EventWaitHandle.OpenExisting(eventName);
                LogInfo("Authorization handoff event opened");
                ScheduleInjectedBootstrap(assemblyCSharp);
                if (!_injectedBootstrapScheduled)
                {
                    throw new InvalidOperationException(
                        "Unity main-thread bootstrap was not scheduled.");
                }
            }
            catch (Exception ex)
            {
                if (_authorizationHandoff != null)
                {
                    _authorizationHandoff.Close();
                    _authorizationHandoff = null;
                }
                LogInfo("StartInjected error: " + ex);
                throw;
            }
        }

        private static void NeutralizeLateInjectionDetectors(
            Assembly assemblyCSharp)
        {
            try
            {
                Type gameAppType = assemblyCSharp.GetType("GameApp");
                if (gameAppType == null)
                {
                    LogInfo("Late-injection detector cleanup skipped: GameApp not found");
                    return;
                }

                const BindingFlags StaticFlags =
                    BindingFlags.Public |
                    BindingFlags.NonPublic |
                    BindingFlags.Static;
                const BindingFlags InstanceFlags =
                    BindingFlags.Public |
                    BindingFlags.NonPublic |
                    BindingFlags.Instance;

                PropertyInfo instanceProperty =
                    gameAppType.GetProperty("Instance", StaticFlags);
                object gameApp = instanceProperty == null
                    ? null
                    : instanceProperty.GetValue(null, null);
                if (gameApp == null)
                {
                    LogInfo("Late-injection detector cleanup skipped: GameApp.Instance unavailable");
                    return;
                }

                FieldInfo pendingField =
                    gameAppType.GetField("pendingInjectionType", InstanceFlags);
                if (pendingField != null)
                {
                    object previous = pendingField.GetValue(gameApp);
                    pendingField.SetValue(gameApp, byte.MaxValue);
                    LogInfo(
                        "Cleared pending injection signal; previous=" +
                        (previous == null ? "<null>" : previous.ToString()));
                }

                FieldInfo managedDetectorField =
                    gameAppType.GetField("assemblyDetector", InstanceFlags);
                object managedDetector = managedDetectorField == null
                    ? null
                    : managedDetectorField.GetValue(gameApp);
                if (managedDetector != null)
                {
                    MethodInfo stopManaged = managedDetector.GetType().GetMethod(
                        "StopDetection",
                        InstanceFlags);
                    if (stopManaged != null)
                    {
                        stopManaged.Invoke(managedDetector, null);
                        LogInfo("Stopped ManagedAssemblyDetector before bootstrap");
                    }
                }

                Type injectionDetectorType = assemblyCSharp.GetType(
                    "CodeStage.AntiCheat.Detectors.InjectionDetector");
                if (injectionDetectorType != null)
                {
                    PropertyInfo detectorInstanceProperty =
                        injectionDetectorType.GetProperty(
                            "Instance",
                            StaticFlags);
                    object injectionDetector =
                        detectorInstanceProperty == null
                            ? null
                            : detectorInstanceProperty.GetValue(null, null);
                    MethodInfo stopInjection =
                        injectionDetector == null
                            ? null
                            : injectionDetectorType.GetMethod(
                                "StopDetectionInternal",
                                InstanceFlags);
                    if (stopInjection != null && injectionDetector != null)
                    {
                        stopInjection.Invoke(injectionDetector, null);
                        LogInfo("Stopped InjectionDetector before bootstrap");
                    }
                }

                if (pendingField != null)
                {
                    pendingField.SetValue(gameApp, byte.MaxValue);
                }
            }
            catch (Exception ex)
            {
                LogInfo("Late-injection detector cleanup error: " + ex);
            }
        }

        private static void ScheduleInjectedBootstrap(Assembly assemblyCSharp)
        {
            lock (PatchSync)
            {
                if (_bootstrapped)
                {
                    _injectedBootstrapScheduled = true;
                    return;
                }
                if (_injectedBootstrapScheduled)
                {
                    return;
                }

                if (!TryQueueBootstrapWithLoom(assemblyCSharp, 30000))
                {
                    throw new InvalidOperationException(
                        "The existing Loom main-thread dispatcher is unavailable.");
                }

                _injectedBootstrapScheduled = true;
                LogInfo(
                    "Queued bootstrap through the existing Loom main-thread dispatcher");
            }
        }

        private static bool TryQueueBootstrapWithLoom(
            Assembly assemblyCSharp,
            int timeoutMilliseconds)
        {
            Type loomType = assemblyCSharp.GetType("Loom");
            if (loomType == null)
            {
                return false;
            }

            FieldInfo currentField = loomType.GetField(
                "_current",
                BindingFlags.Static |
                BindingFlags.Public |
                BindingFlags.NonPublic);
            if (currentField == null)
            {
                return false;
            }

            FieldInfo actionsField = loomType.GetField(
                "_actions",
                BindingFlags.Instance |
                BindingFlags.Public |
                BindingFlags.NonPublic);
            if (actionsField == null)
            {
                return false;
            }

            DateTime deadline = DateTime.UtcNow.AddMilliseconds(
                timeoutMilliseconds);
            System.Collections.IList actions = null;
            while (DateTime.UtcNow < deadline)
            {
                object current = currentField.GetValue(null);
                if (current != null)
                {
                    actions =
                        actionsField.GetValue(current) as System.Collections.IList;
                    if (actions != null)
                    {
                        break;
                    }
                }
                Thread.Sleep(50);
            }
            if (actions == null)
            {
                return false;
            }

            lock (actions)
            {
                actions.Add(new Action(InjectedBootstrapCallback));
            }
            return true;
        }

        private static void InjectedBootstrapCallback()
        {
            try
            {
                LogInfo("Loom main-thread bootstrap callback entered");
                NeutralizeRuntimeDetectorsOnMainThread();
                Bootstrap();
            }
            catch (Exception ex)
            {
                LogInfo("Loom main-thread bootstrap error: " + ex);
            }
        }

        private static void NeutralizeRuntimeDetectorsOnMainThread()
        {
            try
            {
                Assembly assemblyCSharp = FindAssembly("Assembly-CSharp");
                if (assemblyCSharp == null)
                {
                    LogInfo("Main-thread detector cleanup skipped: Assembly-CSharp unavailable");
                    return;
                }

                Type gameAppType = assemblyCSharp.GetType("GameApp");
                object gameApp = GetStaticInstance(gameAppType);
                if (gameApp != null)
                {
                    SetPendingInjectionSignalClear(gameAppType, gameApp);
                    TryStopInstanceField(
                        gameAppType,
                        gameApp,
                        "assemblyDetector",
                        "ManagedAssemblyDetector");
                    TryStopInstanceField(
                        gameAppType,
                        gameApp,
                        "dllDetector",
                        "NativeDllDetector");
                }

                TryStopStaticDetector(
                    assemblyCSharp,
                    "CodeStage.AntiCheat.Detectors.InjectionDetector");
                TryStopStaticDetector(
                    assemblyCSharp,
                    "CodeStage.AntiCheat.Detectors.ObscuredCheatingDetector");
                TryStopStaticDetector(
                    assemblyCSharp,
                    "CodeStage.AntiCheat.Detectors.SpeedHackDetector");
                TryStopStaticDetector(
                    assemblyCSharp,
                    "CodeStage.AntiCheat.Detectors.WallHackDetector");
                TryStopStaticDetector(
                    assemblyCSharp,
                    "CodeStage.AntiCheat.Detectors.NativeDllDetector");

                if (gameApp != null)
                {
                    SetPendingInjectionSignalClear(gameAppType, gameApp);
                }
                LogInfo("Main-thread detector cleanup completed");
            }
            catch (Exception ex)
            {
                LogInfo("Main-thread detector cleanup error: " + ex);
            }
        }

        private static object GetStaticInstance(Type type)
        {
            if (type == null)
            {
                return null;
            }

            PropertyInfo property = type.GetProperty(
                "Instance",
                BindingFlags.Public |
                BindingFlags.NonPublic |
                BindingFlags.Static);
            return property == null ? null : property.GetValue(null, null);
        }

        private static void SetPendingInjectionSignalClear(
            Type gameAppType,
            object gameApp)
        {
            if (gameAppType == null || gameApp == null)
            {
                return;
            }

            FieldInfo field = gameAppType.GetField(
                "pendingInjectionType",
                BindingFlags.Public |
                BindingFlags.NonPublic |
                BindingFlags.Instance);
            if (field != null)
            {
                field.SetValue(gameApp, byte.MaxValue);
            }
        }

        private static void TryStopInstanceField(
            Type ownerType,
            object owner,
            string fieldName,
            string detectorName)
        {
            try
            {
                FieldInfo field = ownerType.GetField(
                    fieldName,
                    BindingFlags.Public |
                    BindingFlags.NonPublic |
                    BindingFlags.Instance);
                object detector = field == null ? null : field.GetValue(owner);
                if (detector == null)
                {
                    return;
                }

                MethodInfo stop = detector.GetType().GetMethod(
                    "StopDetection",
                    BindingFlags.Public |
                    BindingFlags.NonPublic |
                    BindingFlags.Instance,
                    null,
                    Type.EmptyTypes,
                    null);
                if (stop != null)
                {
                    stop.Invoke(detector, null);
                    LogInfo("Stopped " + detectorName + " on Unity main thread");
                }
            }
            catch (Exception ex)
            {
                LogInfo("Failed to stop " + detectorName + ": " + ex.Message);
            }
        }

        private static void TryStopStaticDetector(
            Assembly assemblyCSharp,
            string typeName)
        {
            try
            {
                Type detectorType = assemblyCSharp.GetType(typeName);
                object instance = GetStaticInstance(detectorType);
                if (detectorType == null || instance == null)
                {
                    return;
                }

                MethodInfo stop = detectorType.GetMethod(
                    "StopDetection",
                    BindingFlags.Public |
                    BindingFlags.NonPublic |
                    BindingFlags.Static,
                    null,
                    Type.EmptyTypes,
                    null);
                if (stop == null)
                {
                    stop = detectorType.GetMethod(
                        "StopDetection",
                        BindingFlags.Public |
                        BindingFlags.NonPublic |
                        BindingFlags.Instance,
                        null,
                        Type.EmptyTypes,
                        null);
                }
                if (stop == null)
                {
                    return;
                }

                stop.Invoke(stop.IsStatic ? null : instance, null);
                LogInfo(
                    "Stopped " + detectorType.Name +
                    " on Unity main thread");
            }
            catch (Exception ex)
            {
                LogInfo(
                    "Failed to stop " + typeName + ": " + ex.Message);
            }
        }
#endif

        internal static bool WaitForAuthorizationHandoff(int timeoutMilliseconds)
        {
#if DOORSTOP_BUILD
            return true;
#else
            if (!_injectedLoad)
            {
                return true;
            }

            EventWaitHandle handoff;
            lock (PatchSync)
            {
                handoff = _authorizationHandoff;
            }
            if (handoff == null)
            {
                return false;
            }

            bool signaled = handoff.WaitOne(timeoutMilliseconds, false);
            if (signaled)
            {
                lock (PatchSync)
                {
                    if (ReferenceEquals(_authorizationHandoff, handoff))
                    {
                        _authorizationHandoff = null;
                    }
                }
                handoff.Close();
                LogInfo("Authorization handoff received");
            }
            return signaled;
#endif
        }

        private static void OnAssemblyLoad(object sender, AssemblyLoadEventArgs args)
        {
            try
            {
                if (args.LoadedAssembly == null ||
                    args.LoadedAssembly.GetName().Name != "Assembly-CSharp")
                {
                    return;
                }

                AppDomain.CurrentDomain.AssemblyLoad -= OnAssemblyLoad;
                _assemblyLoadSubscribed = false;
                LogInfo("Assembly-CSharp loaded, patching...");
                PatchGameEntry(args.LoadedAssembly, false);
            }
            catch (Exception ex)
            {
                LogInfo("OnAssemblyLoad error: " + ex);
            }
        }

        private static void PatchGameEntry(
            Assembly assemblyCSharp,
            bool requireRecurringMethod)
        {
            lock (PatchSync)
            {
                PatchGameEntryCore(assemblyCSharp, requireRecurringMethod);
            }
        }

        private static void PatchGameEntryCore(
            Assembly assemblyCSharp,
            bool requireRecurringMethod)
        {
            if (_patched)
            {
                return;
            }

            Type gameAppType = assemblyCSharp.GetType("GameApp") ??
                assemblyCSharp.GetType("StartConfig");
            if (gameAppType == null)
            {
                throw new MissingMemberException(
                    "No suitable entry type found in Assembly-CSharp.");
            }

            MethodInfo entryMethod = FindParameterlessInstanceMethod(
                gameAppType,
                "Update");
            if (entryMethod == null && !requireRecurringMethod)
            {
                entryMethod = FindParameterlessInstanceMethod(gameAppType, "Awake");
            }
            if (entryMethod == null && !requireRecurringMethod)
            {
                entryMethod = FindParameterlessInstanceMethod(gameAppType, "Start");
            }
            if (entryMethod == null)
            {
                throw new MissingMethodException(
                    requireRecurringMethod
                        ? "No parameterless Update method found on " + gameAppType.Name
                        : "No parameterless Update/Awake/Start method found on " +
                            gameAppType.Name);
            }

            HarmonyInstance harmony = HarmonyInstance.Create("doorstop.bootstrap");
            MethodInfo postfix = typeof(Entrypoint).GetMethod(
                "GameEntryPostfix",
                BindingFlags.Static | BindingFlags.NonPublic);
            if (postfix == null)
            {
                throw new MissingMethodException("GameEntryPostfix");
            }

            harmony.Patch(entryMethod, null, new HarmonyMethod(postfix));
            _patched = true;
            LogInfo("Patched " + gameAppType.Name + "." + entryMethod.Name +
                " successfully");
        }

        private static void GameEntryPostfix()
        {
            try
            {
                Bootstrap();
            }
            catch (Exception ex)
            {
                LogInfo("GameEntryPostfix error: " + ex);
            }
        }

        private static void Bootstrap()
        {
            if (_bootstrapped || _bootstrapping)
            {
                return;
            }

            _bootstrapping = true;
            GameObject host = null;
            try
            {
                LogInfo("Bootstrap: creating ConsoleManager");
#if DOORSTOP_BUILD
                host = new GameObject("__DoorstopBoot__");
#else
                host = new GameObject("__RuntimeInjectionBoot__");
#endif
                host.hideFlags = HideFlags.HideAndDontSave;
                UnityEngine.Object.DontDestroyOnLoad(host);
                host.AddComponent<ConsoleManager>();
                _bootstrapped = true;
                LogInfo("Bootstrap: ConsoleManager created OK");
            }
            catch
            {
                if (host != null)
                {
                    UnityEngine.Object.Destroy(host);
                }
                throw;
            }
            finally
            {
                _bootstrapping = false;
            }
        }

        private static MethodInfo FindParameterlessInstanceMethod(
            Type type,
            string name)
        {
            return type.GetMethod(
                name,
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                null,
                Type.EmptyTypes,
                null);
        }

#if !DOORSTOP_BUILD
        private static Assembly WaitForAssembly(
            string name,
            int timeoutMilliseconds)
        {
            DateTime deadline = DateTime.UtcNow.AddMilliseconds(timeoutMilliseconds);
            Assembly assembly;
            while ((assembly = FindAssembly(name)) == null &&
                   DateTime.UtcNow < deadline)
            {
                Thread.Sleep(100);
            }
            return assembly;
        }
#endif

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

        private static void LogInfo(string message)
        {
            try
            {
                string logDirectory = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "ASWII");
                if (!Directory.Exists(logDirectory))
                {
                    Directory.CreateDirectory(logDirectory);
                }

                File.AppendAllText(
                    Path.Combine(logDirectory, "doorstop_boot.log"),
                    DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") +
                    " [Doorstop] " + message + Environment.NewLine);
            }
            catch
            {
            }
        }
    }
}
