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

        }

        private void OnGUI()
        {
            if (!_stylesInitialized)
            {
                _stylesInitialized = true;
                UIHelper.InitializeStyles();
            }

            CheatUIManager.Display();

            //SearchPanel.Display();

            RpcLabUI.Display(
                775f,  // 右移一点
                10f,
                400f + 280f,     // 比左侧更宽
                300f + 220f + 135f // 叠加黑名单高度再加点余量
            );
            LuaDoStringLabUI.Display(10f, 300f, 750f, 800f);
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

            if (Input.GetKeyDown(KeyCode.Delete))
            {
                CheatUIManager.MenuVisible = !CheatUIManager.MenuVisible;
            }
            if (Input.GetKeyDown(KeyCode.Home))
            {
                CheatUIManager.SpriteMenuVisible = !CheatUIManager.SpriteMenuVisible;
                //DumpUIToolsTable();
            }
            if (Input.GetKeyDown(KeyCode.F1))
            {
                channel_connection.Use(0);
                channel_connection.Use(1);
                channel_connection.Use(255);
                //DumpUIToolsTable();
                //OtherC.RunStormVulnerable();
                //RpcScripts.FetchBoxPrizeDisplays("1");
                //RpcScripts.FetchBoxPrizeDisplays("2");
                //RpcScripts.FetchBoxPrizeDisplays("3");
                //RpcScripts.FetchBoxPrizeDisplays("5");
                //RpcScripts.FetchBoxPrizeDisplays("6");
                //RpcScripts.FetchBoxPrizeDisplays("7");
                //RpcScripts.FetchBoxPrizeDisplays("8");
                //RpcScripts.FetchBoxPrizeDisplays("9");
            }
            // 自瞄
            if (AutoAim.Enabled) { AutoAim.Enable(); } else { AutoAim.Disable(); }
            // 子弹追踪
            if (AimTrack.Enabled) { AimTrack.Enable(); } else { AimTrack.Disable(); }
            // 自动扳机
            if (AutoFire.Enabled) { AutoFire.Enable(); }
            // 取消验证码
            OtherC.Update();
            // 自动防踢
            AutoKick.Update();
            // 自动拉频道
            AutoInterface.Update();
            AutoInterface.BlackListUpdate();
            // 大陀螺维持速度
            if (SpinTop.Enabled && Level.Instance.GetPlayer().motor1.move_info.run_speed != 24f) { Level.Instance.GetPlayer().SetSpeed(24f); }

            // 自动锁血
            //AutoLockHP.Update();

            if (GameApp.Instance.lobby_connection != null && GameApp.Instance.lobby_connection.state == LobbyConnection.State.kInGame)
            {
                if (OtherC.KnifeEnabled)
                {
                    foreach (var boss in Level.Instance.boss_manager.GetBosses())
                    {
                        boss.getTransfrom().position = Level.Instance.GetPlayer().transform.position + new Vector3(0f, 0f, -4f);

                        var obj = Traverse.Create(boss).Field("bossGameObject").GetValue<GameObject>();
                        obj.transform.position = Level.Instance.GetPlayer().transform.position + new Vector3(0f, 0f, -4f);
                    }
                }
            }

        }

        void OnDestroy()
        {
            if (Instance == this) Instance = null;
            EyAuthManager.Instance.TryLogoutIfNeeded(EyAuthManager.Instance.Token, EyAuthManager.Instance.SingleCode);
            //FileLogger.Log("CHEAT", "OnDestroy");
        }
    }
}
