using ASWDEBUG.Global;
using ASWDEBUG.Logger;
using Harmony;
using Pathfinding.Util;
using System;
using System.Collections.Generic;
using System.Runtime.Remoting.Channels;
using System.Text;
using UnityEngine;


namespace ASWDEBUG.Cheats.Other
{
    public class AutoInterface
    {
        // ====== 开关 ======
        public static bool Enabled;
        public static bool BlackListEnabled;
        public static int channel_num;
        public static int wave_num;
        public static int wave_player_num = 1;
        public static int last_wave_num;
        public static bool is_first = true;
        public static bool is_start_inv = false;
        public static bool is_ready_del = false;

        public static List<ulong> characterIds;

        // 你的按钮调用它即可
        public static void Toggle()
        {
            Enabled = !Enabled;
            if (Enabled)
            {
                wave_num = 0;
                //ResetAll();
            }
            else
            {
                wave_num = 0;
                wave_player_num = 1;
                last_wave_num = 0;
                is_first = true;
                is_start_inv = false;

                foreach (MyChannelData myChannelData in Traverse.Create(UIWin<UISocialityManager>.Instance.window.channelManager).Field("myChannel").GetValue<List<MyChannelData>>())
                {
                    GameApp.Instance.chat_connection.DeleteMyGroup((ulong)((long)myChannelData.channelID), myChannelData.id, false);
                }
            }
        }

        public static void Update()
        {
            if (!Enabled) return;

            TryIngestPlayers();
        }

        public static void BlackListUpdate()
        {
            if (!BlackListEnabled) return;

            TryIngestPlayersFromBlackList();
        }

        // ====== 扫描 & 修剪 ======
        public static void TryIngestPlayers()
        {
            try
            {

                var mgr = CharacterManager.Instance;
                var chat = GameApp.Instance.chat_connection;
                var m_ChannelArray = Traverse.Create(chat).Field("m_ChannelArray").GetValue<List<tgChatChannel>>()[1].group_array;
                var myChannel = Traverse.Create(UIWin<UISocialityManager>.Instance.window.channelManager).Field("myChannel").GetValue<List<MyChannelData>>();

                if (mgr == null || mgr.character_set == null) return;

                // 遍历所有玩家
                foreach (var character in CharacterManager.Instance.character_set)
                {
                    if (character.GetTeam() == Level.Instance.GetPlayer().GetTeam()) { continue; }
                    //FileLogger.Log("创建频道之前", $" {is_first} {m_ChannelArray.Count} {channel_num}");
                    
                    m_ChannelArray = Traverse.Create(chat).Field("m_ChannelArray").GetValue<List<tgChatChannel>>()[1].group_array;
                    myChannel = Traverse.Create(UIWin<UISocialityManager>.Instance.window.channelManager).Field("myChannel").GetValue<List<MyChannelData>>();

                    if (m_ChannelArray.Count == 0 && is_first)
                    {
                        channel_num = 0;
                    }

                    if (m_ChannelArray.Count < 5 && channel_num < 5)
                    {
                        for (int i = 0; i < 5; i++)
                        {
                            int num = (int)UnityEngine.Random.Range(1f, 9999999f);
                            chat.SendAddMyChannal("乌鸦 Q3010025032" + num.ToString());
                            channel_num++;
                            //FileLogger.Log("创建频道", $"{m_ChannelArray.Count} {channel_num}");
                        }
                        is_first = false;
                        last_wave_num = wave_num;
                    }

                    var cid = character.character_info.character_id;

                    if (!IsInRelation(cid, 1))
                    {
                        Traverse.Create(chat)
                                .Method("SendRequestFriendMake",
                                        new Type[] { typeof(ulong), typeof(byte), typeof(byte) },
                                        new object[] { cid, (byte)0, (byte)1 })
                                .GetValue();
                        FileLogger.Log("SendRequestFriendMake", $"已成功添加{character.GetName()}");
                    }
                    //FileLogger.Log("邀请朋友之前", $" {m_ChannelArray.Count} {wave_num} {last_wave_num} {is_start_inv}");
                    if (m_ChannelArray.Count >= 5 && wave_num == last_wave_num && !is_start_inv)
                    {
                        foreach (MyChannelData myChannelData in myChannel)
                        {
                            chat.InviteFriend(2UL, myChannelData.id, cid, 1u);
                            //FileLogger.Log("邀请朋友", $"{m_ChannelArray.Count} {myChannel.Count} id:{myChannelData.name}");
                        }
                        wave_player_num++;
                    }
                }
                if (wave_player_num > 1)
                {
                    is_start_inv = true;
                }

                myChannel = Traverse.Create(UIWin<UISocialityManager>.Instance.window.channelManager).Field("myChannel").GetValue<List<MyChannelData>>();

                for (int i = 0; i < myChannel.Count; i++) {
                    //FileLogger.Log("数量", $"{myChannel[i].number} {wave_player_num} name:{myChannel[i].name}");
                    if (myChannel[i].number > 1 &&  is_start_inv && wave_num == last_wave_num)
                    {
                        //FileLogger.Log("准备删除", "");
                        is_ready_del = true;
                    }
                }
                if (Traverse.Create(chat).Field("m_ChannelArray").GetValue<List<tgChatChannel>>()[1].group_array.Count >= 5 && wave_num == last_wave_num && is_ready_del)
                {
                    foreach (MyChannelData myChannelData in myChannel)
                    {
                        GameApp.Instance.chat_connection.DeleteMyGroup((ulong)((long)myChannelData.channelID), myChannelData.id, false);
                        //FileLogger.Log("删除频道", $"{Traverse.Create(chat).Field("m_ChannelArray").GetValue<List<tgChatChannel>>()[1].group_array.Count} {Traverse.Create(UIWin<UISocialityManager>.Instance.window.channelManager).Field("myChannel").GetValue<List<MyChannelData>>().Count} {myChannelData.id}");
                    }
                    is_start_inv = false;
                    is_first = true;
                    is_ready_del = false;
                    wave_player_num = 1;
                    wave_num++;
                }
            }
            catch { }
        }

        public static void TryIngestPlayersFromBlackList()
        {
            if (!BlackListEnabled) return;

            try
            {
                var mgr = CharacterManager.Instance;
                var chat = GameApp.Instance.chat_connection;
                var m_ChannelArray = Traverse.Create(chat).Field("m_ChannelArray").GetValue<List<tgChatChannel>>()[1].group_array;
                var myChannel = Traverse.Create(UIWin<UISocialityManager>.Instance.window.channelManager).Field("myChannel").GetValue<List<MyChannelData>>();

                if (mgr == null || mgr.character_set == null) return;

                // 遍历黑名单
                foreach (var cid in characterIds)
                {
                    if (!BlackListEnabled) return;

                    //FileLogger.Log("创建频道之前", $" {is_first} {m_ChannelArray.Count} {channel_num}");

                    m_ChannelArray = Traverse.Create(chat).Field("m_ChannelArray").GetValue<List<tgChatChannel>>()[1].group_array;
                    myChannel = Traverse.Create(UIWin<UISocialityManager>.Instance.window.channelManager).Field("myChannel").GetValue<List<MyChannelData>>();

                    if (m_ChannelArray.Count == 0 && is_first)
                    {
                        channel_num = 0;
                    }

                    if (m_ChannelArray.Count < 5 && channel_num < 5)
                    {
                        for (int i = 0; i < 5; i++)
                        {
                            int num = (int)UnityEngine.Random.Range(1f, 9999999f);
                            chat.SendAddMyChannal("乌鸦 Q3010025032" + num.ToString());
                            channel_num++;
                            //FileLogger.Log("创建频道", $"{m_ChannelArray.Count} {channel_num}");
                        }
                        is_first = false;
                        last_wave_num = wave_num;
                    }


                    if (!IsInRelation(cid, 1))
                    {
                        Traverse.Create(chat)
                                .Method("SendRequestFriendMake",
                                        new Type[] { typeof(ulong), typeof(byte), typeof(byte) },
                                        new object[] { cid, (byte)0, (byte)1 })
                                .GetValue();
                    }
                    //FileLogger.Log("邀请朋友之前", $" {m_ChannelArray.Count} {wave_num} {last_wave_num} {is_start_inv}");
                    if (m_ChannelArray.Count >= 5 && wave_num == last_wave_num && !is_start_inv)
                    {
                        foreach (MyChannelData myChannelData in myChannel)
                        {
                            chat.InviteFriend(2UL, myChannelData.id, cid, 1u);
                            //FileLogger.Log("邀请朋友", $"{m_ChannelArray.Count} {myChannel.Count} id:{myChannelData.name}");
                        }
                        wave_player_num++;
                    }
                }
                if (wave_player_num > 1)
                {
                    is_start_inv = true;
                }

                myChannel = Traverse.Create(UIWin<UISocialityManager>.Instance.window.channelManager).Field("myChannel").GetValue<List<MyChannelData>>();

                for (int i = 0; i < myChannel.Count; i++)
                {
                    //FileLogger.Log("数量", $"{myChannel[i].number} {wave_player_num} name:{myChannel[i].name}");
                    if (myChannel[i].number > 1 && is_start_inv && wave_num == last_wave_num)
                    {
                        FileLogger.Log("准备删除", "");
                        is_ready_del = true;
                    }
                }

                if (Traverse.Create(chat).Field("m_ChannelArray").GetValue<List<tgChatChannel>>()[1].group_array.Count >= 5 && wave_num == last_wave_num && is_ready_del)
                {
                    foreach (MyChannelData myChannelData in myChannel)
                    {
                        GameApp.Instance.chat_connection.DeleteMyGroup((ulong)((long)myChannelData.channelID), myChannelData.id, false);
                        //FileLogger.Log("删除频道", $"{Traverse.Create(chat).Field("m_ChannelArray").GetValue<List<tgChatChannel>>()[1].group_array.Count} {Traverse.Create(UIWin<UISocialityManager>.Instance.window.channelManager).Field("myChannel").GetValue<List<MyChannelData>>().Count} {myChannelData.id}");
                    }
                    is_start_inv = false;
                    is_first = true;
                    is_ready_del = false;
                    wave_player_num = 1;
                    wave_num++;
                }
            }
            catch { }
        }

        public static void TryStopIngestPlayers() 
        {
            wave_num = 0;
            wave_player_num = 1;
            last_wave_num = 0;
            is_first = true;
            is_start_inv = false;

            foreach (MyChannelData myChannelData in Traverse.Create(UIWin<UISocialityManager>.Instance.window.channelManager).Field("myChannel").GetValue<List<MyChannelData>>())
            {
                GameApp.Instance.chat_connection.DeleteMyGroup((ulong)((long)myChannelData.channelID), myChannelData.id, false);
            }
        }

        // 关系判定（1=好友, 2=最近, 3=黑名单）
        private static bool IsInRelation(ulong targetId, int channelId)
        {
            try
            {
                var app = GameApp.Instance;
                var conn = app?.chat_connection;
                var arr = conn?.m_FriendArray;
                if (arr == null) return false;

                foreach (var grp in arr)
                {
                    if (grp == null || grp.channel == null) continue;
                    if (grp.channel.channelID != (ulong)channelId) continue;
                    var players = grp.player_array;
                    if (players == null) continue;
                    foreach (var it in players)
                    {
                        if (it != null && it.playerID == targetId) return true;
                    }
                }
            }
            catch { }
            return false;
        }
    }
}
