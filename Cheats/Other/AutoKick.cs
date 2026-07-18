using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace ASWDEBUG.Cheats.Other
{
    public class AutoKick
    {
        public static bool Enabled;
        private static readonly Dictionary<byte, long> LastVoteTicks = new Dictionary<byte, long>();
        private const long SweepCooldownTicks = TimeSpan.TicksPerSecond;
        private const long TargetCooldownTicks = TimeSpan.TicksPerSecond * 8L;
        private static long _lastSweepTicks;

        public static void Toggle()
        {
            Enabled = !Enabled;
        }

        public static void Update()
        {
            if (!Enabled)
            {
                return;
            }

            long nowTicks = DateTime.UtcNow.Ticks;
            if (nowTicks - _lastSweepTicks < SweepCooldownTicks)
            {
                return;
            }
            _lastSweepTicks = nowTicks;

            try
            {
                if (GameApp.Instance == null || GameApp.Instance.channel_connection == null)
                {
                    return;
                }

                Level level = ASSingleton<Level>.Instance;
                if (level == null)
                {
                    return;
                }

                Character player = level.GetPlayer();
                if (player == null || CharacterManager.Instance == null || CharacterManager.Instance.character_set == null)
                {
                    return;
                }

                int playerTeam = player.GetTeam();
                foreach (Character character in CharacterManager.Instance.character_set)
                {
                    if (character == null || character == player)
                    {
                        continue;
                    }

                    if (character.GetTeam() != playerTeam)
                    {
                        continue;
                    }

                    long lastTicks;
                    if (LastVoteTicks.TryGetValue(character.uid, out lastTicks) &&
                        nowTicks - lastTicks < TargetCooldownTicks)
                    {
                        continue;
                    }

                    LastVoteTicks[character.uid] = nowTicks;
                    GameApp.Instance.channel_connection.RequestVoteBegin(character.uid, "开挂");
                }
            }
            catch
            {
                if (LastVoteTicks.Count > 64)
                {
                    LastVoteTicks.Clear();
                }
            }
        }
    }
}
