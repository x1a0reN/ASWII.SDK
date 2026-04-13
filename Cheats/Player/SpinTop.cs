using ASWDEBUG.Cheats.AimTrack;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace ASWDEBUG.Cheats.Player
{
    public class SpinTop
    {
        public static bool Enabled;
        public static bool setLookEnabled;
        public static float currentSpeed = 0f;
        public static void Toggle()
        {
            Enabled = !Enabled;
            AimTrack.AimTrack.ToggleEnabled();
            if (Enabled)
            {
                var player = Level.Instance.GetPlayer();
                if (currentSpeed == 0f)
                {
                    currentSpeed = player.motor1.move_info.run_speed;
                }
                player.SetSpeed(24f);
            }
            else
            {
                var player = Level.Instance.GetPlayer();
                player.SetSpeed(currentSpeed);
            }
        }
    }
}
