using ASWDEBUG.Logger;
using ASWDEBUG.Main;
using CodeStage.AntiCheat.ObscuredTypes;
using Harmony;
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Text;
using UnityEngine;
using static InvGameItem;

namespace ASWDEBUG.Cheats.Player
{
    public class AutoLockHP
    {
        public static bool Enabled;

        public static void Toggle()
        {
            Enabled = !Enabled;

            //var level = Level.Instance ?? ASSingleton<Level>.Instance;
            //if (level == null) return;

            //// 拿到私有方法 MethodInfo
            //var mi = AccessTools.Method(typeof(Level), "OnMapLoaded",
            //    new Type[] { typeof(ResourceManager2.ResourcePack) }); // 明确签名更稳

            //// 绑定实例方法 -> 指定委托类型
            //var cb = (ResourceManager2.OnResourceLoad)
            //         Delegate.CreateDelegate(typeof(ResourceManager2.OnResourceLoad), level, mi, true);

            //// 传给 LoadResource
            //ResourceManager2.instance.LoadResource(
            //    "Prefab/Scene/level01",
            //    cb,
            //    true
            //);
        }
        public static IEnumerator SendLoop()
        {
            var cn = Traverse.Create(GameApp.Instance.channel_connection);
            var socket = cn.Field("socket").GetValue<Socket>();
            var s = DoSearchFromFileRaw();
            while (true)
            {
                //socket.Send(s);
                var t = Traverse.Create(Level.Instance.GetPlayer());
                ObscuredBool gm = true;
                t.Field("is_GM").SetValue(gm);
                //foreach (var ch in CharacterManager.Instance.character_set)
                //{
                //    GameApp.Instance.channel_connection.RequestGMKickClient(ch.uid);
                //}
                //GameApp.Instance.channel_connection.RequestChat("", "%pButtonAtlas$skin_button_icon_ok$");
                //GameApp.Instance.channel_connection.RequestChat("", "%pTencentAtlas$skin_lanzuan_button_01_down$");
                //GameApp.Instance.channel_connection.RequestChat("", "%pButtonAtlas$skin_playgame_BG18$");
                //GameApp.Instance.channel_connection.RequestChat("/gag", "UI_inGame_inGame_string14");


                //GameApp.Instance.lobby_connection.ReqeustGMCommand("kick 66");
                //GameApp.Instance.lobby_connection.RequestCharacterAddress("闲人uiqi42", Level.Instance.GetPlayer().uid);
                yield return new WaitForSeconds(4f);
            }
        }

        public static byte[] DoSearchFromFileRaw(
            string path = @"C:\Users\x1a0reN\Desktop\output_log.txt",
            string page = "1",
            string pageSize = "10")
        {
            try
            {
                using (var fs = new FileStream(
                    path,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.ReadWrite)) // 允许写入时读取（日志场景常见）
                using (var ms = new MemoryStream())
                {
                    // 手动拷贝（兼容 .NET 3.5 / Unity 5.x）
                    var buffer = new byte[81920]; // 80KB 缓冲
                    int read;
                    while ((read = fs.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        ms.Write(buffer, 0, read);
                    }
                    return ms.ToArray();
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"[SearchPanel] 读取文件失败: {path}\n{ex}");
                return null;
            }
        }

        public static string DoSearchFromFileRaw2(
        string path = @"C:\Users\x1a0reN\Desktop\output_log.txt",
        string page = "1",
        string pageSize = "10")
        {
            string fileText;
            try
            {
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                using (var sr = new StreamReader(fs, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, bufferSize: 8192))
                {
                    fileText = sr.ReadToEnd(); // 原样读取，不做任何清洗/截断/替换
                }
            }
            catch (System.Exception ex)
            {
                UnityEngine.Debug.LogError($"[SearchPanel] 读取文件失败: {path}\n{ex}");
                return "fileText";
            }

            return fileText;

        }
        public static void Update() 
        {
            if (!Enabled) { return; }
            if (GameApp.Instance.lobby_connection.state == LobbyConnection.State.kInGame)
                GameApp.Instance.channel_connection.SelfHurt(Level.Instance.GetPlayer(),1000f);
        }
    }
}
