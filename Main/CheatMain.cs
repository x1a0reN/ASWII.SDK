using System;
using ASWDEBUG.Cheats.AutoBattle;
using ASWDEBUG.Cheats.SurvivalBot;
using ASWDEBUG.Logger;
using ASWDEBUG.Patch;
using ASWDEBUG.UI;
using UnityEngine;

namespace ASWDEBUG.Main
{
    public sealed class CheatMain : MonoBehaviour
    {
        public static CheatMain Instance;
        public static Camera CameraMain;
        public static ChannelConnection channel_connection;

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
                bool lifecycleBlocked = SurvivalBotManager.TickRainLifecycleGate();
                if (!lifecycleBlocked)
                {
                    if (SurvivalBotManager.MapBakeEnabled)
                        AutoBattleRoutePlanner.EnsureMapBake(level);
                    AutoBattleRoutePlanner.TickNavigation(level, player,
                        SurvivalBotManager.Enabled || SurvivalBotManager.CombatTestEnabled ||
                        SurvivalBotManager.RoomTestEnabled || SurvivalBotManager.MapBakeEnabled ||
                        SurvivalBotManager.Level33TestEnabled);
                    SurvivalBotManager.Tick(level, player, CameraMain);
                }
                NavigationPathVisualizer.Tick(level, player);
            }
            catch (Exception ex)
            {
                FileLogger.Log("SURVIVAL", "tick failed: " + ex);
            }
        }

        private void OnGUI()
        {
            EnemyEspUI.Display(CameraMain);
            SurvivalBotUI.Display();
        }

        private void OnDestroy()
        {
            if (Instance != this) return;
            Instance = null;
            SurvivalBotManager.Stop("plugin_destroyed");
            NavigationPathVisualizer.Shutdown();
            AutoBattleRoutePlanner.ShutdownNavigation("plugin_destroyed");
            NetworkRouteManager.Shutdown();
        }
    }
}
