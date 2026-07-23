using System;
using System.IO;
using System.Reflection;
using System.Threading;
using Harmony;
using UnityEngine;

namespace Doorstop
{
    /// <summary>
    /// Supports both early Doorstop loading and late Mono runtime injection.
    /// Unity objects are created only from a GameApp main-thread callback.
    /// </summary>
    public static class Entrypoint
    {
        private static readonly object PatchSync = new object();
        private static bool _patched;
        private static bool _assemblyLoadSubscribed;
        private static bool _bootstrapping;
        private static bool _bootstrapped;

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

        public static void StartInjected()
        {
            try
            {
                LogInfo("Doorstop.Entrypoint.StartInjected() called");
                Assembly assemblyCSharp = WaitForAssembly("Assembly-CSharp", 30000);
                if (assemblyCSharp == null)
                {
                    throw new InvalidOperationException(
                        "Assembly-CSharp was not loaded within 30 seconds.");
                }

                PatchGameEntry(assemblyCSharp, true);
                if (!_patched)
                {
                    throw new InvalidOperationException(
                        "Unity main-thread bootstrap patch was not installed.");
                }
            }
            catch (Exception ex)
            {
                LogInfo("StartInjected error: " + ex);
                throw;
            }
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
                host = new GameObject("__RuntimeInjectionBoot__");
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
