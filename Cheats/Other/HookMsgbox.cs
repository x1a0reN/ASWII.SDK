using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace ASWDEBUG.Cheats.Other
{
    public class HookMsgbox
    {
        public static bool Enabled = false;

        public static void Toggle()
        {
            Enabled = !Enabled;
        }
    }
}
