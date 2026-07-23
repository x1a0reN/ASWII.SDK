using System;
using System.Reflection;
using ASWDEBUG.Logger;

namespace ASWDEBUG.Cheats.SurvivalBot
{
    internal static class SurvivalAntiIdle
    {
        private const BindingFlags InstanceFields =
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        private static Type _cachedType;
        private static FieldInfo _staticTimeField;
        private static FieldInfo _timeoutTipField;
        private static bool _cacheReady;
        private static bool _activationLogged;
        private static bool _failureLogged;

        internal static void OnFightStateUpdate(object fightState)
        {
            if (!SurvivalBotSettings.IgnoreIdleKickEnabled || fightState == null) return;

            Type runtimeType = fightState.GetType();
            EnsureFieldCache(runtimeType);
            if (_staticTimeField == null && _timeoutTipField == null) return;

            try
            {
                if (_staticTimeField != null) _staticTimeField.SetValue(fightState, 0f);
                if (_timeoutTipField != null) _timeoutTipField.SetValue(fightState, false);

                if (_activationLogged) return;
                _activationLogged = true;
                FileLogger.Log("SURVIVAL", "anti-idle active; FightState timeout state is reset before Update");
            }
            catch (Exception ex)
            {
                if (_failureLogged) return;
                _failureLogged = true;
                FileLogger.Log("SURVIVAL", "anti-idle reset failed: " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        private static void EnsureFieldCache(Type runtimeType)
        {
            if (_cacheReady && _cachedType == runtimeType) return;

            _cachedType = runtimeType;
            _staticTimeField = runtimeType.GetField("staticTime", InstanceFields);
            _timeoutTipField = runtimeType.GetField("time_out_tip", InstanceFields);
            _cacheReady = true;

            bool validStaticTime = _staticTimeField != null && _staticTimeField.FieldType == typeof(float);
            bool validTimeoutTip = _timeoutTipField != null && _timeoutTipField.FieldType == typeof(bool);
            if (!validStaticTime) _staticTimeField = null;
            if (!validTimeoutTip) _timeoutTipField = null;

            FileLogger.Log("PATCH", "FightState anti-idle fields staticTime=" + (_staticTimeField != null) +
                " time_out_tip=" + (_timeoutTipField != null));
        }
    }
}
