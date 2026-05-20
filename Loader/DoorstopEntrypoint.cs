using System;
using System.IO;
using System.Reflection;
using UnityEngine;

namespace Doorstop
{
    /// <summary>
    /// Unity Doorstop 入口点。
    /// Doorstop 会在 Mono 域初始化后、游戏代码执行前调用 Start()。
    /// 此时 Assembly-CSharp 可能尚未加载，需要监听 AssemblyLoad 事件。
    /// </summary>
    public static class Entrypoint
    {
        private static bool _bootstrapped;

        public static void Start()
        {
            try
            {
                // 尝试直接启动（如果 Assembly-CSharp 已经加载）
                Assembly asmAC = FindAssembly("Assembly-CSharp");
                if (asmAC != null)
                {
                    Bootstrap();
                    return;
                }

                // Assembly-CSharp 尚未加载，注册事件等待
                AppDomain.CurrentDomain.AssemblyLoad += OnAssemblyLoad;
            }
            catch (Exception ex)
            {
                File.AppendAllText(
                    Path.Combine(Application.persistentDataPath, "doorstop_error.log"),
                    DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " [Doorstop] Start error: " + ex + Environment.NewLine);
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
                    Bootstrap();
                }
            }
            catch (Exception ex)
            {
                File.AppendAllText(
                    Path.Combine(Application.persistentDataPath, "doorstop_error.log"),
                    DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " [Doorstop] OnAssemblyLoad error: " + ex + Environment.NewLine);
            }
        }

        private static void Bootstrap()
        {
            if (_bootstrapped) return;
            _bootstrapped = true;

            // 创建 ConsoleManager（原有入口），它会负责后续所有初始化
            GameObject host = new GameObject("__DoorstopBoot__");
            host.hideFlags = HideFlags.HideAndDontSave;
            UnityEngine.Object.DontDestroyOnLoad(host);
            host.AddComponent<ConsoleManager>();
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
    }
}
