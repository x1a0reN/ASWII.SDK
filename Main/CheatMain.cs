using System;
using ASWDEBUG.Cheats.SurvivalBot;
using ASWDEBUG.Logger;
using UnityEngine;

namespace ASWDEBUG.Main
{
    public sealed class CheatMain : MonoBehaviour
    {
        public static CheatMain Instance;
        public static Camera CameraMain;
        public static ChannelConnection channel_connection;

        private GUIStyle _titleStyle;
        private GUIStyle _bodyStyle;

        private void Awake()
        {
            if (Instance != null && Instance != this)
            {
                Destroy(gameObject);
                return;
            }

            Instance = this;
            DontDestroyOnLoad(gameObject);
            gameObject.hideFlags = HideFlags.HideAndDontSave;
            FileLogger.Log("BOOT", "SurvivalBot main started.");
        }

        private void Update()
        {
            if (CameraMain == null) CameraMain = Camera.main;

            GameApp app = GameApp.Instance;
            if (app != null) channel_connection = app.channel_connection;

            Level level = null;
            Character player = null;
            try
            {
                level = ASSingleton<Level>.Instance;
                if (level != null) player = level.GetPlayer();
            }
            catch { }

            try
            {
                SurvivalBotManager.Tick(level, player, CameraMain);
            }
            catch (Exception ex)
            {
                FileLogger.Log("SURVIVAL", "tick failed: " + ex);
            }
        }

        private void OnGUI()
        {
            if (_titleStyle == null)
            {
                _titleStyle = new GUIStyle(GUI.skin.label);
                _titleStyle.fontSize = 16;
                _titleStyle.fontStyle = FontStyle.Bold;
                _titleStyle.normal.textColor = Color.white;
                _bodyStyle = new GUIStyle(GUI.skin.label);
                _bodyStyle.fontSize = 13;
                _bodyStyle.normal.textColor = Color.white;
                _bodyStyle.wordWrap = true;
            }

            GUILayout.BeginArea(new Rect(12f, 12f, 390f, 150f), GUI.skin.box);
            GUILayout.Label("ASWII Survival Bot", _titleStyle);
            GUILayout.Label(SurvivalBotManager.StatusText, _bodyStyle);
            GUILayout.Label("F8: " + (SurvivalBotManager.Enabled ? "停止" : "启动") + "机器人", _bodyStyle);
            GUILayout.EndArea();
        }

        private void OnDestroy()
        {
            if (Instance == this) Instance = null;
            SurvivalBotManager.Stop("plugin_destroyed");
        }
    }
}
