using System;
using System.IO;
using System.Reflection;
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
    /// 策略：用 Harmony patch GameApp.Awake()，在游戏自身初始化时注入。
    /// GameApp.Awake() 一定在 Unity 主线程、引擎就绪后执行。
    /// </summary>
    public static class Entrypoint
    {
        private static bool _patched;
        private static bool _bootstrapped;

        public static void Start()
        {
            try
            {
                LogInfo("Doorstop.Entrypoint.Start() called");

                // 检查 Assembly-CSharp 是否已加载
                Assembly asmAC = FindAssembly("Assembly-CSharp");
                if (asmAC != null)
                {
                    PatchGameEntry(asmAC);
                }
                else
                {
                    // 等待 Assembly-CSharp 加载后再 patch
                    AppDomain.CurrentDomain.AssemblyLoad += OnAssemblyLoad;
                    LogInfo("Waiting for Assembly-CSharp...");
                }
            }
            catch (Exception ex)
            {
                LogInfo("Start error: " + ex);
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
                    LogInfo("Assembly-CSharp loaded, patching...");
                    PatchGameEntry(args.LoadedAssembly);
                }
            }
            catch (Exception ex)
            {
                LogInfo("OnAssemblyLoad error: " + ex);
            }
        }

        /// <summary>
        /// 用 Harmony patch GameApp.Awake()，在其执行时启动我们的代码。
        /// Harmony patch 操作本身不需要 Unity 主线程，是安全的。
        /// </summary>
        private static void PatchGameEntry(Assembly asmAC)
        {
            if (_patched) return;
            _patched = true;

            try
            {
                Type gameAppType = asmAC.GetType("GameApp");
                if (gameAppType == null)
                {
                    LogInfo("GameApp type not found, trying fallback...");
                    // 兜底：尝试其他常见入口
                    gameAppType = asmAC.GetType("StartConfig");
                }

                if (gameAppType == null)
                {
                    LogInfo("No suitable entry type found in Assembly-CSharp!");
                    return;
                }

                MethodInfo awakeMethod = gameAppType.GetMethod("Awake",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

                if (awakeMethod == null)
                {
                    // 尝试 Start
                    awakeMethod = gameAppType.GetMethod("Start",
                        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                }

                if (awakeMethod == null)
                {
                    LogInfo("No Awake/Start method found on " + gameAppType.Name);
                    return;
                }

                var harmony = HarmonyInstance.Create("doorstop.bootstrap");
                var postfix = typeof(Entrypoint).GetMethod("GameEntryPostfix",
                    BindingFlags.Static | BindingFlags.NonPublic);

                harmony.Patch(awakeMethod, null, new HarmonyMethod(postfix));
                LogInfo("Patched " + gameAppType.Name + "." + awakeMethod.Name + " successfully");
            }
            catch (Exception ex)
            {
                LogInfo("PatchGameEntry failed: " + ex);
            }
        }

        /// <summary>
        /// Harmony Postfix：在 GameApp.Awake() 执行后触发。
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
            if (_bootstrapped) return;
            _bootstrapped = true;

            LogInfo("Bootstrap: creating ConsoleManager");

            // 创建 ConsoleManager（原有入口），它会负责后续所有初始化
            GameObject host = new GameObject("__DoorstopBoot__");
            host.hideFlags = HideFlags.HideAndDontSave;
            UnityEngine.Object.DontDestroyOnLoad(host);
            host.AddComponent<ConsoleManager>();

            LogInfo("Bootstrap: ConsoleManager created OK");
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
