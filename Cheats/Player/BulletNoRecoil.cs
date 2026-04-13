using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace ASWDEBUG.Cheats.Player
{
    public class BulletNoRecoil
    {
        public static bool Enabled;
        public static float _spread = 0f;
        public static float Sniper_spread = 0f;

        public static void Toggle()
        {
            Enabled = !Enabled;

            if (Enabled)
            {
                _spread = -1.3f;
                Sniper_spread = -3f;
            }
            else
            {
                _spread = 0f;
                Sniper_spread = 0f;
            }
        }

    }
}
