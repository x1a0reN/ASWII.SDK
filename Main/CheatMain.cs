using ASWDEBUG.Cheats.AimTrack;
using ASWDEBUG.Cheats.AutoAim;
using ASWDEBUG.Cheats.AutoBattle;
using ASWDEBUG.Cheats.AutoUse;
using ASWDEBUG.Cheats.LocalBot;
using ASWDEBUG.Cheats.ESP;
using ASWDEBUG.Cheats.Other;
using ASWDEBUG.Cheats.Player;
using ASWDEBUG.Logger;
using ASWDEBUG.UI;
using ASWDEBUG.Verify;
using Harmony;
using PDE.Animation;
using PluginTool;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Text;
using UnityEngine;

namespace ASWDEBUG.Main
{
    public class CheatMain : MonoBehaviour
    {
        private static readonly bool EnableDebugUi = true;
        public static CheatMain Instance;

        public static Camera CameraMain;
        public static ChannelConnection channel_connection;
        private bool _stylesInitialized;

        public static bool inChannel;
        public static List<CardInfo> CardData = new List<CardInfo>();

        void Awake()
        {
            if (Instance != null && Instance != this)
            {
                Destroy(this);
                return;
            }
            Instance = this;

            // 常驻
            DontDestroyOnLoad(this.gameObject);
            this.gameObject.hideFlags = HideFlags.HideAndDontSave;

            FileLogger.Log("CHEAT", "Awake");
        }

        private void Start()
        {
            FileLogger.Log("CHEAT", "Start");
            CheatUIManager.MenuVisible = EnableDebugUi;
            CheatUIManager.SpriteMenuVisible = false;
            RpcLabUI.Visible = false;
            LuaDoStringLabUI.Visible = EnableDebugUi;
            FileLogger.Log("CHEAT", EnableDebugUi ? "Audit UI enabled." : "Audit UI hidden.");
            DllUsageTelemetry.Start();
        }

        private void OnGUI()
        {
            if (!_stylesInitialized)
            {
                _stylesInitialized = true;
                UIHelper.InitializeStyles();
            }

            float margin = 10f;
            float width = Mathf.Min(900f, Mathf.Max(420f, Screen.width - margin * 2f));
            float height = Mathf.Min(820f, Mathf.Max(360f, Screen.height - margin * 2f));
            float posX = Mathf.Max(margin, Screen.width - width - margin);

            if (EnableDebugUi)
            {
                CheatUIManager.Display();
                RpcLabUI.Display(
                    posX,
                    margin,
                    width,
                    height
                );
            }
        }

        private void SlowUpdate()
        {

        }
        public static void DumpUIToolsTable()
        {
            // 1) 拿到私有静态字段 table
            Dictionary<string, string> dict =
                Traverse.Create(typeof(UITools))
                        .Field("table")
                        .GetValue<Dictionary<string, string>>();

            if (dict == null)
            {
                FileLogger.Log("UITools", "table == null");
                return;
            }

            // 2) 遍历输出
            foreach (KeyValuePair<string, string> kv in dict)
            {
                FileLogger.Log("UITools", string.Format("{0} => {1}", kv.Key, kv.Value));
            }
        }

        private void Update()
        {
            GameApp app = GameApp.Instance;
            inChannel = (app != null &&
                app.lobby_connection != null &&
                app.lobby_connection.state == LobbyConnection.State.kInChannel);
            if (!inChannel) { CardData.Clear(); }
            if (CameraMain == null) CameraMain = Camera.main ?? null;
            if (channel_connection == null && app != null) channel_connection = app.channel_connection ?? null;

            Level level = null;
            Character player = null;
            try
            {
                level = ASSingleton<Level>.Instance;
                if (level != null)
                {
                    player = level.GetPlayer();
                }
            }
            catch { }

            try
            {
                LocalBotManager.Tick(level, player);
                LocalBotPanel.TickHotkeys();
            }
            catch (Exception e)
            {
                FileLogger.Log("CHEAT", "LocalBot tick failed: " + e.Message);
            }

            if (CameraMain != null && level != null && player != null)
            {
                DllUsageTelemetry.Tick(player);

                try
                {
                    AutoUseManager.Tick(level, player);
                }
                catch (Exception e)
                {
                    FileLogger.Log("CHEAT", "AutoUse tick failed: " + e.Message);
                }

                try
                {
                    AutoAim.Enable();
                }
                catch (Exception e)
                {
                    FileLogger.Log("CHEAT", "AutoAim tick failed: " + e.Message);
                }

                try
                {
                    AimTrack.Enable();
                }
                catch (Exception e)
                {
                    FileLogger.Log("CHEAT", "AimTrack tick failed: " + e.Message);
                }

                try
                {
                    BossAutoAim.Enable();
                }
                catch (Exception e)
                {
                    FileLogger.Log("CHEAT", "BossAutoAim tick failed: " + e.Message);
                }

                try
                {
                    AutoBattleManager.Tick(level, player, CameraMain);
                }
                catch (Exception e)
                {
                    FileLogger.Log("CHEAT", "AutoBattle tick failed: " + e.Message);
                }
            }
            else
            {
                DllUsageTelemetry.Tick(null);
                AutoBattleManager.Tick(null, null, null);
                AutoAim.AimLocking = false;
                AutoAim.bestTarget = null;
                AutoAim.currentTarget = null;
                AimTrack.AimLocking = false;
                AimTrack.bestTarget = null;
                AimTrack.currentTarget = null;
                BossAutoAim.bestTarget = null;
                BossAutoAim.currentTarget = null;
            }

            if (EnableDebugUi && Input.GetKeyDown(KeyCode.Delete))
            {
                CheatUIManager.MenuVisible = !CheatUIManager.MenuVisible;
                FileLogger.Log("CHEAT", "Audit UI toggled: " + CheatUIManager.MenuVisible);
            }
        }

        void OnDestroy()
        {
            if (Instance == this) Instance = null;
            try { LocalBotManager.RemoveAll("shutdown"); } catch { }
            try
            {
                DllUsageTelemetry.Stop();
            }
            catch (Exception e)
            {
                FileLogger.Log("CHEAT", "OnDestroy telemetry stop failed: " + e.Message);
            }
            //FileLogger.Log("CHEAT", "OnDestroy");
        }
    }
}
