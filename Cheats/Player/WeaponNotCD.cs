using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using UnityEngine;

namespace ASWDEBUG.Cheats.Player
{
    public class WeaponNotCD
    {
        public static bool Enabled;

        public static void Toggle()
        {
            Enabled = !Enabled;
        }
    }
}
