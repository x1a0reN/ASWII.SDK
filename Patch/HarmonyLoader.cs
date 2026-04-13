// ASWDEBUG/HarmonyLoader.cs
using System;
using System.Linq;
using System.Reflection;
using Harmony;
using ASWDEBUG.Logger;
using ASWDEBUG.Verify;
using UnityEngine;

namespace ASWDEBUG.Patch
{
    public static class HarmonyLoader
    {
        private static bool _installed;
        private static bool _patched;

        // 在你能保证会被调用的地方调用它（例如 ASWDEBUG 入口、你自己的初始化点）
        public static void Install()
        {
            if (_installed) return;
            _installed = true;

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
                // PatchAll 扫描“当前程序集”（也就是你的 ASWDEBUG.dll）里带 [HarmonyPatch] 的类
                harmony.PatchAll(Assembly.GetExecutingAssembly());

                FileLogger.Log("ASWDEBUG", "[HarmonyLoader] Harmony patches applied.");
            }
            catch (Exception e)
            {
                FileLogger.Log("ASWDEBUG", "[HarmonyLoader] ApplyPatches failed: " + e);
            }
        }
    }
}
