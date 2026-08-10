using ASWDEBUG.Cheats.AimTrack;
using ASWDEBUG.Cheats.AutoAim;
using ASWDEBUG.Cheats.AutoUse;
using ASWDEBUG.Cheats.ESP;
using ASWDEBUG.Cheats.Other;
using ASWDEBUG.Cheats.Player;
using ASWDEBUG.Logger;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;

namespace ASWDEBUG.Global
{
    internal static class FeatureConfigStore
    {
        private const string FileName = "ASW_PrecisionProfile.ini";
        private static bool _loaded;
        private static bool _dirty;
        private static float _saveAt;

        internal static string ConfigPath
        {
            get { return Path.Combine(Application.persistentDataPath, FileName); }
        }

        internal static void LoadOnce()
        {
            if (_loaded) return;
            _loaded = true;
            AutoUseManager.EnsureLoaded();

            string path = ConfigPath;
            if (!File.Exists(path))
            {
                _dirty = true;
                _saveAt = Time.realtimeSinceStartup + 0.5f;
                return;
            }

            try
            {
                Dictionary<string, string> values = new Dictionary<string, string>(
                    StringComparer.OrdinalIgnoreCase);
                string[] lines = File.ReadAllLines(path, Encoding.UTF8);
                for (int i = 0; i < lines.Length; i++)
                {
                    string line = lines[i].Trim();
                    if (line.Length == 0 || line.StartsWith("#", StringComparison.Ordinal))
                        continue;
                    int separator = line.IndexOf('=');
                    if (separator <= 0) continue;
                    values[line.Substring(0, separator).Trim()] =
                        line.Substring(separator + 1).Trim();
                }

                ESP.Enabled = ReadBool(values, "esp.enabled", ESP.Enabled);
                ESP.SkeletonEsp = ReadBool(values, "esp.skeleton", ESP.SkeletonEsp);
                ESP.D3BoxEsp = ReadBool(values, "esp.box", ESP.D3BoxEsp);
                ESP.InfoEsp = ReadBool(values, "esp.info", ESP.InfoEsp);
                ESP.LineEsp = ReadBool(values, "esp.line", ESP.LineEsp);
                ESP.CrossEsp = ReadBool(values, "esp.crosshair", ESP.CrossEsp);

                BulletNoRecoil.Enabled = ReadBool(
                    values,
                    "ballistics.spread_control",
                    BulletNoRecoil.Enabled);
                BulletNoRecoil.SpreadScale = ReadFloat(
                    values,
                    "ballistics.spread_scale",
                    BulletNoRecoil.SpreadScale,
                    0f,
                    3f);

                AimTrack.Enabled = ReadBool(values, "tracking.enabled", AimTrack.Enabled);
                AimTrack.Wall = ReadBool(values, "tracking.wall", AimTrack.Wall);
                AimTrack.Hidden = ReadBool(values, "tracking.hidden", AimTrack.Hidden);
                AimTrack.Shield = ReadBool(values, "tracking.shield", AimTrack.Shield);
                AimTrack.DrawFovCircle = ReadBool(
                    values,
                    "tracking.circle",
                    AimTrack.DrawFovCircle);
                AimTrack.RadiusPixels = ReadFloat(
                    values,
                    "tracking.radius",
                    AimTrack.RadiusPixels,
                    24f,
                    1200f);
                AimTrack.TrackingProbability = ReadFloat(
                    values,
                    "tracking.probability",
                    AimTrack.TrackingProbability,
                    0f,
                    1f);
                ESP.CircleRadius = AimTrack.RadiusPixels;

                AutoAim.Enabled = ReadBool(values, "aim.enabled", AutoAim.Enabled);
                AutoAim.Wall = ReadBool(values, "aim.wall", AutoAim.Wall);
                AutoAim.Hidden = ReadBool(values, "aim.hidden", AutoAim.Hidden);
                AutoAim.Shield = ReadBool(values, "aim.shield", AutoAim.Shield);
                AutoUseManager.Enabled = ReadBool(
                    values,
                    "automation.auto_use",
                    AutoUseManager.Enabled);
                AutoFire.SetTriggerEnabled(ReadBool(
                    values,
                    "automation.auto_trigger",
                    AutoFire.Enabled));
                AutoFire.SetAutoAttackEnabled(ReadBool(
                    values,
                    "automation.auto_attack",
                    AutoFire.AutoFireAllowed));
                GrenadeNotHurt.Enabled = ReadBool(
                    values,
                    "protection.explosion_no_damage",
                    GrenadeNotHurt.Enabled);
                GrenadeNotHurt.SetProbability(ReadFloat(
                    values,
                    "protection.explosion_no_damage_probability",
                    GrenadeNotHurt.Probability,
                    0f,
                    1f));
                GrenadeHalfHurt.Enabled = ReadBool(
                    values,
                    "protection.explosion_half_damage",
                    GrenadeHalfHurt.Enabled);
                GrenadeHalfHurt.SetProbability(ReadFloat(
                    values,
                    "protection.explosion_half_damage_probability",
                    GrenadeHalfHurt.Probability,
                    0f,
                    1f));
                OtherC.Enabled = ReadBool(
                    values,
                    "utility.card_reveal",
                    OtherC.Enabled);
                AutoKick.Enabled = ReadBool(
                    values,
                    "utility.auto_anti_kick",
                    AutoKick.Enabled);
                OtherC.EnabledVeryify = ReadBool(
                    values,
                    "utility.ignore_match_validation",
                    OtherC.EnabledVeryify);
            }
            catch (Exception error)
            {
                FileLogger.Log("CONFIG", "Precision profile load failed: " + error.Message);
            }
        }

        internal static void MarkDirty()
        {
            _dirty = true;
            _saveAt = Time.realtimeSinceStartup + 0.65f;
        }

        internal static void Tick()
        {
            if (_dirty && Time.realtimeSinceStartup >= _saveAt) SaveNow();
        }

        internal static void SaveNow()
        {
            if (!_loaded) return;
            try
            {
                string path = ConfigPath;
                string directory = Path.GetDirectoryName(path);
                if (!Directory.Exists(directory)) Directory.CreateDirectory(directory);

                string[] lines = new string[]
                {
                    "# ASW precision profile v3",
                    "esp.enabled=" + Bool(ESP.Enabled),
                    "esp.skeleton=" + Bool(ESP.SkeletonEsp),
                    "esp.box=" + Bool(ESP.D3BoxEsp),
                    "esp.info=" + Bool(ESP.InfoEsp),
                    "esp.line=" + Bool(ESP.LineEsp),
                    "esp.crosshair=" + Bool(ESP.CrossEsp),
                    "ballistics.spread_control=" + Bool(BulletNoRecoil.Enabled),
                    "ballistics.spread_scale=" + Number(BulletNoRecoil.SpreadScale),
                    "tracking.enabled=" + Bool(AimTrack.Enabled),
                    "tracking.wall=" + Bool(AimTrack.Wall),
                    "tracking.hidden=" + Bool(AimTrack.Hidden),
                    "tracking.shield=" + Bool(AimTrack.Shield),
                    "tracking.circle=" + Bool(AimTrack.DrawFovCircle),
                    "tracking.radius=" + Number(AimTrack.RadiusPixels),
                    "tracking.probability=" + Number(AimTrack.TrackingProbability),
                    "aim.enabled=" + Bool(AutoAim.Enabled),
                    "aim.wall=" + Bool(AutoAim.Wall),
                    "aim.hidden=" + Bool(AutoAim.Hidden),
                    "aim.shield=" + Bool(AutoAim.Shield),
                    "automation.auto_use=" + Bool(AutoUseManager.Enabled),
                    "automation.auto_trigger=" + Bool(AutoFire.Enabled),
                    "automation.auto_attack=" + Bool(AutoFire.AutoFireAllowed),
                    "protection.explosion_no_damage=" + Bool(GrenadeNotHurt.Enabled),
                    "protection.explosion_no_damage_probability=" +
                        Number(GrenadeNotHurt.Probability),
                    "protection.explosion_half_damage=" + Bool(GrenadeHalfHurt.Enabled),
                    "protection.explosion_half_damage_probability=" +
                        Number(GrenadeHalfHurt.Probability),
                    "utility.card_reveal=" + Bool(OtherC.Enabled),
                    "utility.auto_anti_kick=" + Bool(AutoKick.Enabled),
                    "utility.ignore_match_validation=" + Bool(OtherC.EnabledVeryify)
                };
                File.WriteAllLines(path, lines, Encoding.UTF8);
                _dirty = false;
            }
            catch (Exception error)
            {
                _dirty = true;
                _saveAt = Time.realtimeSinceStartup + 5f;
                FileLogger.Log("CONFIG", "Precision profile save failed: " + error.Message);
            }
        }

        private static bool ReadBool(
            IDictionary<string, string> values,
            string key,
            bool fallback)
        {
            string value;
            if (!values.TryGetValue(key, out value)) return fallback;
            if (string.Equals(value, "1", StringComparison.Ordinal) ||
                string.Equals(value, "true", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(value, "on", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(value, "yes", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
            if (string.Equals(value, "0", StringComparison.Ordinal) ||
                string.Equals(value, "false", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(value, "off", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(value, "no", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
            return fallback;
        }

        private static float ReadFloat(
            IDictionary<string, string> values,
            string key,
            float fallback,
            float minimum,
            float maximum)
        {
            string value;
            float parsed;
            if (!values.TryGetValue(key, out value) ||
                !float.TryParse(
                    value,
                    NumberStyles.Float,
                    CultureInfo.InvariantCulture,
                    out parsed) ||
                float.IsNaN(parsed) ||
                float.IsInfinity(parsed))
            {
                return fallback;
            }
            return Mathf.Clamp(parsed, minimum, maximum);
        }

        private static string Bool(bool value)
        {
            return value ? "true" : "false";
        }

        private static string Number(float value)
        {
            return value.ToString("0.###", CultureInfo.InvariantCulture);
        }
    }
}
