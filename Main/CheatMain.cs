using ASWDEBUG.Cheats.AimTrack;
using ASWDEBUG.Cheats.AutoAim;
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
            RpcLabUI.Visible = EnableDebugUi;
            LuaDoStringLabUI.Visible = EnableDebugUi;
            FileLogger.Log("CHEAT", EnableDebugUi ? "Audit UI enabled." : "Audit UI hidden.");
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
            inChannel = (GameApp.Instance != null &&
                GameApp.Instance.lobby_connection != null &&
                GameApp.Instance.lobby_connection.state == LobbyConnection.State.kInChannel);
            if (!inChannel) { CardData.Clear(); }
            if (CameraMain == null) CameraMain = Camera.main ?? null;
            if (channel_connection == null) channel_connection = GameApp.Instance.channel_connection ?? null;

            if (EnableDebugUi && Input.GetKeyDown(KeyCode.Delete))
            {
                CheatUIManager.MenuVisible = !CheatUIManager.MenuVisible;
                FileLogger.Log("CHEAT", "Audit UI toggled: " + CheatUIManager.MenuVisible);
            }
        }

        void OnDestroy()
        {
            if (Instance == this) Instance = null;
            try
            {
                EyAuthManager.Instance.TryLogoutIfNeeded(EyAuthManager.Instance.Token, EyAuthManager.Instance.SingleCode);
            }
            catch (Exception e)
            {
                FileLogger.Log("CHEAT", "OnDestroy logout skipped: " + e.Message);
            }
            //FileLogger.Log("CHEAT", "OnDestroy");
        }
    }
}
