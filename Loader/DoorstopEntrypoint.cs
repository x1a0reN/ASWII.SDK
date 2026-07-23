using System;
using System.IO;
using System.Reflection;
using System.Threading;
using Harmony;
using UnityEngine;

namespace Doorstop
{
    /// <summary>
    /// Unity Doorstop 入口点。
    /// 
    /// 关键约束：Doorstop.Start() 在 Mono ReloadAssembly 期间被调用，
    /// 此时不能创建 GameObject 或调用大部分 Unity API。
    /// 
    /// 策略：用 Harmony patch GameApp.Update()，在下一帧 Unity 主线程中注入。
    /// 这样同时兼容进程启动早期的 Doorstop 加载和游戏已经运行后的 Mono 注入。
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

                Assembly asmAC = FindAssembly("Assembly-CSharp");
                if (asmAC != null)
                {
                    PatchGameEntry(asmAC, false);
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

        /// <summary>
        /// 运行时注入入口。等待游戏程序集出现并确认主线程调度 patch 已成功安装，
        /// 让注入器只有在入口真正就绪后才报告成功。
        /// </summary>
        public static void StartInjected()
        {
            try
            {
                LogInfo("Doorstop.Entrypoint.StartInjected() called");
                Assembly asmAC = WaitForAssembly("Assembly-CSharp", 30000);
                if (asmAC == null)
                {
                    throw new InvalidOperationException(
                        "Assembly-CSharp was not loaded within 30 seconds.");
                }

                PatchGameEntry(asmAC, true);
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
                if (args.LoadedAssembly != null &&
                    args.LoadedAssembly.GetName().Name == "Assembly-CSharp")
                {
                    AppDomain.CurrentDomain.AssemblyLoad -= OnAssemblyLoad;
                    _assemblyLoadSubscribed = false;
                    LogInfo("Assembly-CSharp loaded, patching...");
                    PatchGameEntry(args.LoadedAssembly, false);
                }
            }
            catch (Exception ex)
            {
                LogInfo("OnAssemblyLoad error: " + ex);
            }
        }

        /// <summary>
        /// 用 Harmony patch GameApp.Update()，在下一帧启动我们的代码。
        /// 这里不创建 Unity 对象；对象初始化统一延迟到 Update 所在的主线程。
        /// </summary>
        private static void PatchGameEntry(
            Assembly asmAC,
            bool requireRecurringMethod)
        {
            lock (PatchSync)
            {
                PatchGameEntryCore(asmAC, requireRecurringMethod);
            }
        }

        private static void PatchGameEntryCore(
            Assembly asmAC,
            bool requireRecurringMethod)
        {
            if (_patched) return;

            Type gameAppType = asmAC.GetType("GameApp");
            if (gameAppType == null)
            {
                LogInfo("GameApp type not found, trying fallback...");
                gameAppType = asmAC.GetType("StartConfig");
            }

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

            var harmony = HarmonyInstance.Create("doorstop.bootstrap");
            var postfix = typeof(Entrypoint).GetMethod("GameEntryPostfix",
                BindingFlags.Static | BindingFlags.NonPublic);
            if (postfix == null)
            {
                throw new MissingMethodException("GameEntryPostfix");
            }

            harmony.Patch(entryMethod, null, new HarmonyMethod(postfix));
            _patched = true;
            LogInfo("Patched " + gameAppType.Name + "." + entryMethod.Name + " successfully");
        }

        /// <summary>
        /// Harmony Postfix：在 GameApp.Update()/Awake()/Start() 执行后触发。
        /// 此时在 Unity 主线程，引擎已就绪，可以安全创建 GameObject。
        /// </summary>
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
            if (_bootstrapped || _bootstrapping) return;
            _bootstrapping = true;

            GameObject host = null;
            try
            {
                LogInfo("Bootstrap: creating SurvivalBotBootstrap");

                host = new GameObject("__DoorstopBoot__");
                host.hideFlags = HideFlags.HideAndDontSave;
                UnityEngine.Object.DontDestroyOnLoad(host);
                host.AddComponent<SurvivalBotBootstrap>();
                _bootstrapped = true;

                LogInfo("Bootstrap: SurvivalBotBootstrap created OK");
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
            Assembly[] asms = AppDomain.CurrentDomain.GetAssemblies();
            for (int i = 0; i < asms.Length; i++)
            {
                try
                {
                    if (asms[i].GetName().Name == name)
                        return asms[i];
                }
                catch { }
            }
            return null;
        }

        private static void LogInfo(string msg)
        {
            try
            {
                string logDir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "ASWII");
                if (!Directory.Exists(logDir)) Directory.CreateDirectory(logDir);
                string logPath = Path.Combine(logDir, "doorstop_boot.log");
                File.AppendAllText(logPath,
                    DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " [Doorstop] " + msg + Environment.NewLine);
            }
            catch { }
        }
    }
}
