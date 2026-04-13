using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace ASWDEBUG.Cheats.Player
{
    public class NotKick
    {
        public static bool Enabled;

        public static void Toggle()
        {
            Enabled = !Enabled;
        }
    }
}
