using ASWDEBUG.Logger;
using ASWDEBUG.Main;
using PluginTool;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace ASWDEBUG.Cheats.Player
{
    public class HealthBarDisplay
    {
        public static bool Enabled;

        public static void Toggle()
        {
            Enabled = !Enabled;
        }
    }
}
