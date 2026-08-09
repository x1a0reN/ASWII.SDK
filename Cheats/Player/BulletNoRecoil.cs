using System;
using UnityEngine;

namespace ASWDEBUG.Cheats.Player
{
    public class BulletNoRecoil
    {
        public static bool Enabled;
        private static float _spreadScale;

        // Kept for older patch references; new code scales the native spread input.
        public static float _spread = 0f;
        public static float Sniper_spread = 0f;

        public static float SpreadScale
        {
            get { return _spreadScale; }
            set
            {
                if (float.IsNaN(value) || float.IsInfinity(value)) value = 1f;
                _spreadScale = Mathf.Clamp(value, 0f, 3f);
                _spread = -1.3f * (1f - _spreadScale);
                Sniper_spread = -3f * (1f - _spreadScale);
            }
        }

        public static bool RequiresStraightRayFallback
        {
            get { return Enabled && _spreadScale <= 0.0001f; }
        }

        public static void Toggle()
        {
            Enabled = !Enabled;
            SpreadScale = _spreadScale;
        }

        public static float ScaleNativeSpread(float nativeSpread)
        {
            if (!Enabled) return nativeSpread;
            if (float.IsNaN(nativeSpread) || float.IsInfinity(nativeSpread)) return 0f;
            return Mathf.Max(0f, nativeSpread) * _spreadScale;
        }

    }
}
