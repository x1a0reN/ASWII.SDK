namespace ASWDEBUG.Cheats.Player
{
    public class GrenadeNotHurt
    {
        public static bool Enabled;
        public static float Probability = 1f;

        public static void Toggle()
        {
            Enabled = !Enabled;
        }

        public static void SetProbability(float value)
        {
            Probability = ClampProbability(value);
        }

        public static bool ShouldApply(double sample)
        {
            if (!Enabled || Probability <= 0f || sample < 0d || sample >= 1d)
                return false;
            return Probability >= 1f || sample < Probability;
        }

        private static float ClampProbability(float value)
        {
            if (float.IsNaN(value) || float.IsInfinity(value)) return 1f;
            if (value < 0f) return 0f;
            if (value > 1f) return 1f;
            return value;
        }
    }
}
