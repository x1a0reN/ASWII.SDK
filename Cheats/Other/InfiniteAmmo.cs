using System;
using System.Reflection;
using ASWDEBUG.Logger;
using Harmony;
using UnityEngine;

namespace ASWDEBUG.Cheats.Other
{
    [Obfuscation(Exclude = true, ApplyToMembers = true, Feature = "-rename", StripAfterObfuscation = false)]
    internal static class InfiniteAmmo
    {
        private const float KeepAliveIntervalSeconds = 0.20f;
        private const float PreShotFreshnessSeconds = 0.08f;

        internal static bool Enabled;

        private static float _lastReloadRequestRealtime = -1000f;
        private static Character _lastPlayer;
        private static WeaponBase _lastWeapon;
        private static ChannelConnection _lastConnection;
        private static int _requestLogCount;
        private static int _errorLogCount;

        internal static void Toggle()
        {
            Enabled = !Enabled;
            ResetSession();
            FileLogger.Log(
                "INFINITE-AMMO",
                "Infinite ammo " + (Enabled ? "enabled" : "disabled"));
        }

        internal static void Tick(Character player)
        {
            if (!Enabled)
            {
                ResetSession();
                return;
            }

            ChannelConnection connection;
            WeaponBase weapon;
            GunInfo gun;
            if (!TryGetContext(player, null, out connection, out weapon, out gun))
            {
                ResetContextOnly();
                return;
            }

            TrackContext(player, weapon, connection);
            PrepareLocalWeapon(weapon, gun);
            SendReloadIfStale(
                connection,
                player,
                weapon,
                Time.realtimeSinceStartup,
                KeepAliveIntervalSeconds);
        }

        internal static void BeforeShoot(ChannelConnection connection)
        {
            if (!Enabled)
            {
                return;
            }

            try
            {
                Character player = GetCurrentPlayer();
                WeaponBase weapon;
                GunInfo gun;
                ChannelConnection resolvedConnection;
                if (!TryGetContext(
                    player,
                    connection,
                    out resolvedConnection,
                    out weapon,
                    out gun))
                {
                    return;
                }

                TrackContext(player, weapon, resolvedConnection);
                PrepareLocalWeapon(weapon, gun);
                SendReloadIfStale(
                    resolvedConnection,
                    player,
                    weapon,
                    Time.realtimeSinceStartup,
                    PreShotFreshnessSeconds);
            }
            catch (Exception exception)
            {
                LogError("before-shoot", exception);
            }
        }

        internal static bool TryInterceptLocalReload(WeaponBase weapon)
        {
            if (!Enabled || weapon == null)
            {
                return false;
            }

            try
            {
                Character player = GetCurrentPlayer();
                if (player == null || player.mWeapon != weapon)
                {
                    return false;
                }

                ChannelConnection connection;
                WeaponBase currentWeapon;
                GunInfo gun;
                if (!TryGetContext(
                    player,
                    null,
                    out connection,
                    out currentWeapon,
                    out gun) ||
                    currentWeapon != weapon)
                {
                    return false;
                }

                TrackContext(player, weapon, connection);
                PrepareLocalWeapon(weapon, gun);
                SendReloadIfStale(
                    connection,
                    player,
                    weapon,
                    Time.realtimeSinceStartup,
                    0f);

                // The server receives the reload request, while the local weapon never
                // enters the animation/cooldown state that would suppress firing.
                return true;
            }
            catch (Exception exception)
            {
                LogError("local-reload", exception);
                return false;
            }
        }

        private static bool TryGetContext(
            Character player,
            ChannelConnection preferredConnection,
            out ChannelConnection connection,
            out WeaponBase weapon,
            out GunInfo gun)
        {
            connection = preferredConnection;
            weapon = null;
            gun = null;

            try
            {
                if (player == null || player.mWeapon == null)
                {
                    return false;
                }

                weapon = player.mWeapon;
                gun = weapon.info as GunInfo;
                if (gun == null || (int)gun.ammo_one_clip <= 0)
                {
                    return false;
                }

                if (connection == null)
                {
                    GameApp app = GameApp.Instance;
                    connection = app == null ? null : app.channel_connection;
                }

                return connection != null;
            }
            catch (Exception exception)
            {
                LogError("context", exception);
                connection = null;
                weapon = null;
                gun = null;
                return false;
            }
        }

        private static Character GetCurrentPlayer()
        {
            try
            {
                Level level = ASSingleton<Level>.Instance;
                return level == null ? null : level.GetPlayer();
            }
            catch (Exception exception)
            {
                LogError("player", exception);
                return null;
            }
        }

        private static void PrepareLocalWeapon(WeaponBase weapon, GunInfo gun)
        {
            if (weapon == null || gun == null)
            {
                return;
            }

            int clipCapacity = (int)gun.ammo_one_clip;
            if (clipCapacity > 0 && weapon.clip < clipCapacity)
            {
                weapon.clip = clipCapacity;
            }

            if (weapon.reloading)
            {
                weapon.reloading = false;
                gun.cooling = 0f;
                gun.cool_down_ready = true;
            }
        }

        private static bool SendReloadIfStale(
            ChannelConnection connection,
            Character player,
            WeaponBase weapon,
            float now,
            float freshnessSeconds)
        {
            if (connection == null || player == null || weapon == null)
            {
                return false;
            }

            float elapsed = now - _lastReloadRequestRealtime;
            if (elapsed >= 0f && elapsed < freshnessSeconds)
            {
                return false;
            }

            try
            {
                // ChannelConnection.Reload writes and completes its own packet, so
                // this call finishes before the following Shoot packet is emitted.
                connection.Reload(player);
                _lastReloadRequestRealtime = now;

                if (_requestLogCount < 8)
                {
                    _requestLogCount++;
                    FileLogger.Log(
                        "INFINITE-AMMO",
                        "Reload request sent before fire. clip=" + weapon.clip +
                        " interval=" + freshnessSeconds.ToString("0.00"));
                }

                return true;
            }
            catch (Exception exception)
            {
                LogError("reload", exception);
                return false;
            }
        }

        private static void TrackContext(
            Character player,
            WeaponBase weapon,
            ChannelConnection connection)
        {
            if (_lastPlayer == player &&
                _lastWeapon == weapon &&
                _lastConnection == connection)
            {
                return;
            }

            _lastPlayer = player;
            _lastWeapon = weapon;
            _lastConnection = connection;
            _lastReloadRequestRealtime = -1000f;
        }

        private static void ResetContextOnly()
        {
            _lastPlayer = null;
            _lastWeapon = null;
            _lastConnection = null;
            _lastReloadRequestRealtime = -1000f;
        }

        private static void ResetSession()
        {
            ResetContextOnly();
            _requestLogCount = 0;
            _errorLogCount = 0;
        }

        private static void LogError(string stage, Exception exception)
        {
            if (_errorLogCount >= 8)
            {
                return;
            }

            _errorLogCount++;
            FileLogger.Log(
                "INFINITE-AMMO",
                stage + " failed: " + exception.GetType().Name + ":" + exception.Message);
        }
    }
}

namespace ASWDEBUG.Patch
{
    [HarmonyPatch]
    [Obfuscation(Exclude = true, ApplyToMembers = true, Feature = "-rename", StripAfterObfuscation = false)]
    public static class Patch_WeaponBase_Reload_InfiniteAmmo
    {
        [Obfuscation(Exclude = true, Feature = "-rename")]
        static MethodBase TargetMethod()
        {
            return AccessTools.Method(typeof(WeaponBase), "Reload", Type.EmptyTypes, null);
        }

        [Obfuscation(Exclude = true, Feature = "-rename")]
        static bool Prefix(WeaponBase __instance)
        {
            return !ASWDEBUG.Cheats.Other.InfiniteAmmo.TryInterceptLocalReload(__instance);
        }
    }
}
