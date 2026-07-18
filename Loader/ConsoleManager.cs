using System;
using System.IO;
using ASWDEBUG.Logger;
using ASWDEBUG.Main;
using ASWDEBUG.Patch;
using UnityEngine;

public sealed class SurvivalBotBootstrap : MonoBehaviour
{
    private static SurvivalBotBootstrap _instance;

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
    }

    private void Start()
    {
        string logDir = Path.Combine(Application.persistentDataPath, "Logs");
        try { Directory.CreateDirectory(logDir); } catch { }

        int pid = -1;
        try { pid = System.Diagnostics.Process.GetCurrentProcess().Id; } catch { }
        FileLogger.Init(Path.Combine(logDir, "ASW_SurvivalBot.pid" + pid + ".log"), true);
        FileLogger.Log("BOOT", "SurvivalBot bootstrap started.");

        Application.RegisterLogCallback(HandleLog);
        HarmonyLoader.Install();

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
}
