using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace ASWDEBUG.Cheats.Other
{
    public class AutoKick
    {
        public static bool Enabled;

        public static void Toggle()
        {
            Enabled = !Enabled;
        }

        public static void Update()
        {
            if (Enabled)
            {
                foreach (var character in CharacterManager.Instance.character_set)
                {
                    if (ASSingleton<Level>.Instance.GetPlayer().GetTeam() == character.GetTeam())
                    {
                        GameApp.Instance.channel_connection.RequestVoteBegin(character.uid, "开挂");
                    }
                }
            }
        }
    }
}
