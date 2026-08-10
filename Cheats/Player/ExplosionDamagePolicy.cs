using System;
using System.Security.Cryptography;

namespace ASWDEBUG.Cheats.Player
{
    public static class ExplosionDamagePolicy
    {
        private static readonly object RandomLock = new object();
        private static readonly RandomNumberGenerator Random =
            RandomNumberGenerator.Create();
        private static readonly byte[] RandomBytes = new byte[8];

        public static float LastNoDamageRoll = -1f;
        public static float LastHalfDamageRoll = -1f;
        public static string LastDecision = "IDLE";

        public static void Resolve(
            bool eligible,
            bool nativeHalfDamage,
            out bool suppressDamage,
            out bool resolvedHalfDamage)
        {
            double noDamageSample;
            double halfDamageSample;
            if (!TryNextSamples(out noDamageSample, out halfDamageSample))
            {
                LastNoDamageRoll = -1f;
                LastHalfDamageRoll = -1f;
                LastDecision = "RNG FALLBACK";
                suppressDamage = false;
                resolvedHalfDamage = nativeHalfDamage;
                return;
            }

            ResolveWithSamples(
                eligible,
                nativeHalfDamage,
                noDamageSample,
                halfDamageSample,
                out suppressDamage,
                out resolvedHalfDamage);
        }

        public static void ResolveWithSamples(
            bool eligible,
            bool nativeHalfDamage,
            double noDamageSample,
            double halfDamageSample,
            out bool suppressDamage,
            out bool resolvedHalfDamage)
        {
            suppressDamage = false;
            resolvedHalfDamage = nativeHalfDamage;
            LastNoDamageRoll = -1f;
            LastHalfDamageRoll = -1f;

            if (!eligible)
            {
                LastDecision = nativeHalfDamage ? "SELF / NATIVE HALF" : "SELF / NATIVE";
                return;
            }

            if (GrenadeNotHurt.Enabled)
            {
                LastNoDamageRoll = ValidSample(noDamageSample)
                    ? (float)noDamageSample
                    : -1f;
                if (GrenadeNotHurt.ShouldApply(noDamageSample))
                {
                    suppressDamage = true;
                    resolvedHalfDamage = false;
                    LastDecision = "NO DAMAGE";
                    return;
                }
            }

            if (GrenadeHalfHurt.Enabled)
            {
                LastHalfDamageRoll = ValidSample(halfDamageSample)
                    ? (float)halfDamageSample
                    : -1f;
                if (GrenadeHalfHurt.ShouldApply(halfDamageSample))
                {
                    resolvedHalfDamage = true;
                    LastDecision = "HALF DAMAGE";
                    return;
                }
            }

            LastDecision = nativeHalfDamage ? "NATIVE HALF" : "FULL DAMAGE";
        }

        private static bool TryNextSamples(
            out double noDamageSample,
            out double halfDamageSample)
        {
            noDamageSample = 0d;
            halfDamageSample = 0d;
            try
            {
                lock (RandomLock)
                {
                    Random.GetBytes(RandomBytes);
                    noDamageSample = BitConverter.ToUInt32(RandomBytes, 0) /
                        4294967296d;
                    halfDamageSample = BitConverter.ToUInt32(RandomBytes, 4) /
                        4294967296d;
                }
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static bool ValidSample(double sample)
        {
            return !double.IsNaN(sample) &&
                !double.IsInfinity(sample) &&
                sample >= 0d &&
                sample < 1d;
        }
    }
}
