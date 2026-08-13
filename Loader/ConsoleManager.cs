using System;
using System.IO;
using ASWDEBUG.Build;
using ASWDEBUG.Logger;
using ASWDEBUG.Main;
using ASWDEBUG.Patch;
using ASWDEBUG.Verify;
using UnityEngine;

public sealed class SurvivalBotBootstrap : MonoBehaviour
{
    private static SurvivalBotBootstrap _instance;
    private bool _hooksReady;

    private void Awake()
    {
        if (_instance != null && _instance != this)
        {
            Destroy(gameObject);
            return;
        }

        _instance = this;
        DontDestroyOnLoad(gameObject);
        gameObject.hideFlags = HideFlags.HideAndDontSave;
        string logDir = Path.Combine(Application.persistentDataPath, "Logs");
        try { Directory.CreateDirectory(logDir); } catch { }

        int pid = -1;
        try { pid = System.Diagnostics.Process.GetCurrentProcess().Id; } catch { }
        FileLogger.Init(Path.Combine(logDir, "ASW_SurvivalBot.pid" + pid + ".log"), true);
        FileLogger.Log("BOOT", "SurvivalBot bootstrap started. edition=" + SurvivalBuildProfile.Edition);

        LoadHandoff();

        Application.RegisterLogCallback(HandleLog);
        try { _hooksReady = HarmonyLoader.Install(); }
        catch (Exception ex)
        {
            FileLogger.Log("BOOT", "hook install failed: " + ex.GetType().Name + ": " + ex.Message);
            try { NetworkRouteManager.PrepareClientRole(); } catch { }
            NetworkRouteManager.ReportHookFailure();
            _hooksReady = false;
        }
    }

    private void Start()
    {
        if (!_hooksReady) return;
        GameObject host = new GameObject("SurvivalBotMain");
        host.hideFlags = HideFlags.HideAndDontSave;
        DontDestroyOnLoad(host);
        host.AddComponent<CheatMain>();
    }

    private static void HandleLog(string condition, string stacktrace, LogType type)
    {
        if (type == LogType.Error || type == LogType.Exception)
            FileLogger.LogException(condition, stacktrace);
    }

    private static void LoadHandoff()
    {
        try
        {
            SurvivalHandoff.Result handoff = SurvivalHandoff.TryLoad();
            if (handoff == null) return;
            FileLogger.Log("BOOT", "handoff loaded edition=" + handoff.Edition +
                " card_present=" + !string.IsNullOrEmpty(handoff.DirectCard) +
                " protected_ids=" + handoff.ProtectedCharacterIds.Count);
#if SURVIVAL_NORMAL
            // 普通版：把登录器下发的定制版 character_id 名单载入，选敌时避让。
            ASWDEBUG.Cheats.SurvivalBot.SurvivalBotManager.SetProtectedCharacterIds(handoff.ProtectedCharacterIds);
#endif
            // 定制版(ReleaseA)：待 M2 服务器接口就绪后，在游戏内角色就绪时上报本地 character_id。
        }
        catch (Exception ex)
        {
            FileLogger.Log("BOOT", "handoff load failed: " + ex.GetType().Name + ": " + ex.Message);
        }
    }
}
