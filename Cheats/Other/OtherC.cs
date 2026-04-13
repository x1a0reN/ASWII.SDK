using ASWDEBUG.Logger;
using Harmony;
using Org.BouncyCastle.Asn1.X509;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using UnityEngine;

namespace ASWDEBUG.Cheats.Other
{
    public class OtherC
    {
        public static bool Enabled;
        public static bool EnabledVeryify;
        public static bool BossEnabled;
        public static bool KnifeEnabled;

        public static void Toggle()
        {
            Enabled = !Enabled;
            //DumpSpriteNames("ButtonAtlas");
            //DumpSpriteNames("Item Atlas");
            //DumpSpriteNames("CommonBgAtlas");
            //DumpSpriteNames("MapPreviewAtlas");
            //DumpSpriteNames("PictureWordAtlas");
            //DumpSpriteNames("PictureWordAtlas2");
            //DumpSpriteNames("TencentAtlas");
            //DumpSpriteNames("HDIconAtlas");
            //DumpSpriteNames("AvatarPartAtlas");
            //Level.Instance.treasure.SetPosition(Level.Instance.GetPlayer().transform.position + new Vector3(0, 0.5f, 0));
            //GameApp.Instance.channel_connection.PickUpDropItem(Level.Instance.treasure.GetID());
        }
        //public static void Boom() {
        //    SearchPanel.DoSearchFromFileRaw();
        //}
        public static void ToggleEnabledVeryify()
        {
            EnabledVeryify = !EnabledVeryify;
        }
        public static void ToggleBossEnabled()
        {
            BossEnabled = !BossEnabled;
        }
        public static void ToggleKnifeEnabled()
        {
            KnifeEnabled = !KnifeEnabled;
        }
        public static void DumpSpriteNames(string atlasName)
        {
            var atlas = AtlasManager.Instance.GetAtlas(atlasName);
            if (atlas == null)
            {
                FileLogger.Log("", $"[AtlasDebug] Atlas not found: {atlasName}");
                return;
            }

            // 若存在替代（replacement），一路解引用到真实图集
            while (atlas.replacement != null) atlas = atlas.replacement;

            // 通常 NGUI 的 UIAtlas 暴露 spriteList（List<UISpriteData>）
            var list = atlas.spriteList;
            if (list == null || list.Count == 0)
            {
                FileLogger.Log("",$"[AtlasDebug] No sprites in atlas: {atlasName}");
                return;
            }

            FileLogger.Log("", $"[AtlasDebug] Atlas: {atlasName}, sprites = {list.Count}");
            foreach (global::UISpriteData sd in list)
            {
                FileLogger.Log($"{atlasName}", sd.name);   // 这就是可用于占位符的 <精灵名>
            }
        }
        public static void RunStormVulnerable()
        {
            // 构造“合法占位符风暴”：%p{atlas}${sprite}$
            string payload = BuildEmojiStorm("ButtonAtlas", "skin_button_icon_ok", 4, 0);
            FileLogger.Log("",payload);
            GameApp.Instance.channel_connection.RequestChat("", payload);
        }
        public static string BuildEmojiStorm(string atlas, string sprite, int count, int between)
        {
            // 典型占位符：%pemoji$smile$
            string token = $"%p{atlas}${sprite}$";
            var sb = new StringBuilder(count * (token.Length + Mathf.Max(between, 0)));
            for (int i = 0; i < count; i++)
            {
                sb.Append(token);
                if (between > 0) sb.Append('x', between);
            }
            return sb.ToString();
        }
        public static void Update()
        {
            if (EnabledVeryify)
            {
                GlobalStatic.hookNum = "0";
            }
            //if (!Enabled) { return; }
            //if (GameApp.Instance.channel_connection.state != global::ChannelConnection.State.kInGame) { return; }
            //30 85 C0 0F 84 8C 00 00 00 C6 86 D4 01 00 00 01 8D 85 20 F8 FF FF 8B 08 89 8D 9C FD FF FF 8B 48 04 89 8D A0 FD FF FF 8B 40 08 89 85 A4 FD FF FF 8D 86 D8 01 00 00 8B 8D 9C FD FF FF 89 08 8B 8D A0 FD FF FF 89 48 04 8B 8D A4 FD FF FF 89 48 08 8D 85 20 F8 FF FF
        }
    }
}
