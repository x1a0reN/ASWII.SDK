using UnityEngine;

namespace ASWDEBUG.Cheats.SurvivalBot
{
    public static class SurvivalBotSettings
    {
        private const string KeyPrefix = "ASWDEBUG.SurvivalBot.";

        private static readonly float[] MatchTimeoutValues = { 300f, 600f, 900f };
        private static readonly float[] ParticipantCaptureValues = { 3f, 5f, 8f };
        private static readonly float[] SeparationValues = { 9f, 11f, 13f, 15f, 18f };
        private static readonly float[] EmergencyDistanceValues = { 6f, 8f, 10f, 12f };
        private static readonly float[] SafePointRefreshValues = { 0.8f, 1f, 1.35f, 2f };
        private static readonly float[] SuicideFallbackValues = { 15f, 25f, 40f };

        private static bool _loaded;
        private static int _enemyEspEnabled;
        private static int _ignoreIdleKickEnabled;
        private static int _matchTimeoutIndex;
        private static int _participantCaptureIndex;
        private static int _separationIndex;
        private static int _emergencyDistanceIndex;
        private static int _safePointRefreshIndex;
        private static int _suicideFallbackIndex;
        private static int _gmStopRounds;

        public static bool EnemyEspEnabled
        {
            get { EnsureLoaded(); return _enemyEspEnabled != 0; }
        }

        public static bool IgnoreIdleKickEnabled
        {
            get { EnsureLoaded(); return _ignoreIdleKickEnabled != 0; }
        }

        public static float MatchTimeoutSeconds
        {
            get { EnsureLoaded(); return MatchTimeoutValues[_matchTimeoutIndex]; }
        }

        public static float ParticipantCaptureSeconds
        {
            get { EnsureLoaded(); return ParticipantCaptureValues[_participantCaptureIndex]; }
        }

        public static float DesiredSeparation
        {
            get { EnsureLoaded(); return SeparationValues[_separationIndex]; }
        }

        public static float EmergencyDistance
        {
            get { EnsureLoaded(); return EmergencyDistanceValues[_emergencyDistanceIndex]; }
        }

        public static float SafePointRefreshSeconds
        {
            get { EnsureLoaded(); return SafePointRefreshValues[_safePointRefreshIndex]; }
        }

        public static float SuicideFallbackSeconds
        {
            get { EnsureLoaded(); return SuicideFallbackValues[_suicideFallbackIndex]; }
        }

        public static int GmStopRounds
        {
            get { EnsureLoaded(); return _gmStopRounds; }
        }

        public static void EnsureLoaded()
        {
            if (_loaded) return;

            _enemyEspEnabled = PlayerPrefs.GetInt(KeyPrefix + "EnemyEspEnabled", 1) == 0 ? 0 : 1;
            _ignoreIdleKickEnabled = PlayerPrefs.GetInt(KeyPrefix + "IgnoreIdleKickEnabled", 1) == 0 ? 0 : 1;
            _matchTimeoutIndex = Clamp(PlayerPrefs.GetInt(KeyPrefix + "MatchTimeoutIndex", 1), 0, MatchTimeoutValues.Length - 1);
            _participantCaptureIndex = Clamp(PlayerPrefs.GetInt(KeyPrefix + "ParticipantCaptureIndex", 1), 0, ParticipantCaptureValues.Length - 1);
            _separationIndex = Clamp(PlayerPrefs.GetInt(KeyPrefix + "SeparationIndex", 2), 0, SeparationValues.Length - 1);
            _emergencyDistanceIndex = Clamp(PlayerPrefs.GetInt(KeyPrefix + "EmergencyDistanceIndex", 1), 0, EmergencyDistanceValues.Length - 1);
            _safePointRefreshIndex = Clamp(PlayerPrefs.GetInt(KeyPrefix + "SafePointRefreshIndex", 2), 0, SafePointRefreshValues.Length - 1);
            _suicideFallbackIndex = Clamp(PlayerPrefs.GetInt(KeyPrefix + "SuicideFallbackIndex", 1), 0, SuicideFallbackValues.Length - 1);
            _gmStopRounds = Clamp(PlayerPrefs.GetInt(KeyPrefix + "GmStopRounds", 3), 1, 3);
            _loaded = true;
        }

        public static void SetEnemyEspEnabled(bool value)
        {
            EnsureLoaded();
            SaveIfChanged(ref _enemyEspEnabled, KeyPrefix + "EnemyEspEnabled", value ? 1 : 0);
        }

        public static void SetIgnoreIdleKickEnabled(bool value)
        {
            EnsureLoaded();
            SaveIfChanged(ref _ignoreIdleKickEnabled, KeyPrefix + "IgnoreIdleKickEnabled", value ? 1 : 0);
        }

        public static void SetMatchTimeoutSeconds(float value)
        {
            EnsureLoaded();
            SaveIfChanged(ref _matchTimeoutIndex, KeyPrefix + "MatchTimeoutIndex", FindNearest(MatchTimeoutValues, value));
        }

        public static void SetParticipantCaptureSeconds(float value)
        {
            EnsureLoaded();
            SaveIfChanged(ref _participantCaptureIndex, KeyPrefix + "ParticipantCaptureIndex", FindNearest(ParticipantCaptureValues, value));
        }

        public static void SetDesiredSeparation(float value)
        {
            EnsureLoaded();
            SaveIfChanged(ref _separationIndex, KeyPrefix + "SeparationIndex", FindNearest(SeparationValues, value));
        }

        public static void SetEmergencyDistance(float value)
        {
            EnsureLoaded();
            SaveIfChanged(ref _emergencyDistanceIndex, KeyPrefix + "EmergencyDistanceIndex", FindNearest(EmergencyDistanceValues, value));
        }

        public static void SetSafePointRefreshSeconds(float value)
        {
            EnsureLoaded();
            SaveIfChanged(ref _safePointRefreshIndex, KeyPrefix + "SafePointRefreshIndex", FindNearest(SafePointRefreshValues, value));
        }

        public static void SetSuicideFallbackSeconds(float value)
        {
            EnsureLoaded();
            SaveIfChanged(ref _suicideFallbackIndex, KeyPrefix + "SuicideFallbackIndex", FindNearest(SuicideFallbackValues, value));
        }

        public static void SetGmStopRounds(int value)
        {
            EnsureLoaded();
            SaveIfChanged(ref _gmStopRounds, KeyPrefix + "GmStopRounds", Clamp(value, 1, 3));
        }

        private static void SaveIfChanged(ref int field, string key, int value)
        {
            if (field == value) return;
            field = value;
            PlayerPrefs.SetInt(key, value);
            PlayerPrefs.Save();
        }

        private static int FindNearest(float[] values, float value)
        {
            int best = 0;
            float bestDistance = Mathf.Abs(values[0] - value);
            for (int i = 1; i < values.Length; i++)
            {
                float distance = Mathf.Abs(values[i] - value);
                if (distance >= bestDistance) continue;
                best = i;
                bestDistance = distance;
            }
            return best;
        }

        private static int Clamp(int value, int min, int max)
        {
            if (value < min) return min;
            if (value > max) return max;
            return value;
        }
    }
}
