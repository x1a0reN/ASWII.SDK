using ASWDEBUG.Global;
using ASWDEBUG.Logger;
using ASWDEBUG.Main;
using PDE.Animation;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using UnityEngine;

namespace ASWDEBUG.Cheats.Player
{
    public class AutoFire
    {
        private const float CastRadius = 0.05f;
        private const float MaxCastDistance = 300f;
        private const float KeyDownRepeatSeconds = 0.06f;

        private static bool _wasAllowed;
        private static float _nextKeyDownAt;
        private static int _keyDownPulseFrame = -1;
        private static bool _castMaskInitialized;
        private static int _castMask;

        public static bool Enabled;
        public static bool AutoFireAllowed;

        public static bool WantsFire
        {
            get { return Enabled && AutoFireAllowed; }
        }

        public static void Enable()
        {
            Level level = null;
            Character player = null;
            try
            {
                level = ASSingleton<Level>.Instance;
                player = level != null ? level.GetPlayer() : null;
            }
            catch
            {
            }

            Tick(level, player, CheatMain.CameraMain != null
                ? CheatMain.CameraMain
                : Camera.main);
        }

        public static void Tick(Level level, Character player, Camera camera)
        {
            if (!Enabled || level == null || player == null || camera == null ||
                player.IsDied || (player.Is_Viewer && !player.Is_GP))
            {
                SetAllowed(false);
                return;
            }

            Character target;
            bool allowed = TryGetCrosshairTarget(level, player, camera, out target);
            SetAllowed(allowed);
        }

        public static void Toggle()
        {
            Enabled = !Enabled;
            if (!Enabled) SetAllowed(false);
        }

        public static void Reset()
        {
            SetAllowed(false);
        }

        public static void ToggleAutoFireAllowed()
        {
            SetAllowed(!AutoFireAllowed);
        }

        public static bool ShouldFireKeyDown()
        {
            if (!WantsFire) return false;

            int frame = Time.frameCount;
            if (_keyDownPulseFrame == frame) return true;

            float now = Time.unscaledTime;
            if (now + 0.0001f < _nextKeyDownAt) return false;

            _keyDownPulseFrame = frame;
            _nextKeyDownAt = now + KeyDownRepeatSeconds;
            return true;
        }

        public static bool IsCrosshairOnEnemyExact(Character target)
        {
            if (target == null) return false;

            Level level = null;
            Character player = null;
            Camera camera = CheatMain.CameraMain != null
                ? CheatMain.CameraMain
                : Camera.main;
            try
            {
                level = ASSingleton<Level>.Instance;
                player = level != null ? level.GetPlayer() : null;
            }
            catch
            {
            }
            if (level == null || player == null || camera == null) return false;

            Character crosshairTarget;
            return TryGetCrosshairTarget(level, player, camera, out crosshairTarget) &&
                crosshairTarget == target;
        }

        public static void Fire()
        {
            Enable();
        }

        private static bool TryGetCrosshairTarget(Level level, Character player,
            Camera camera, out Character target)
        {
            target = null;
            if (level == null || player == null || camera == null) return false;
            if (SafeGetHidden(player)) return false;

            Ray ray = camera.ScreenPointToRay(new Vector3(
                Screen.width * 0.5f,
                Screen.height * 0.5f,
                0f));

            RaycastHit[] hits;
            try
            {
                hits = Physics.SphereCastAll(ray, CastRadius, MaxCastDistance,
                    GetCastMask());
            }
            catch
            {
                return false;
            }
            if (hits == null || hits.Length == 0) return false;

            for (int n = 0; n < hits.Length; n++)
            {
                int nearestIndex = n;
                for (int i = n + 1; i < hits.Length; i++)
                {
                    if (hits[i].distance < hits[nearestIndex].distance)
                        nearestIndex = i;
                }
                if (nearestIndex != n)
                {
                    RaycastHit swap = hits[n];
                    hits[n] = hits[nearestIndex];
                    hits[nearestIndex] = swap;
                }

                Transform hitTransform = hits[n].transform;
                if (hitTransform == null) continue;
                Character hitCharacter = ResolveHitCharacter(hitTransform);
                if (hitCharacter == player) continue;

                // The first non-local collider is authoritative. Terrain, a teammate,
                // or an invalid character must block a target behind it.
                if (hitCharacter == null) return false;
                if (!IsValidEnemy(level, player, hitCharacter)) return false;
                if (!IsVisibleToPlayer(player, hitCharacter)) return false;

                target = hitCharacter;
                return true;
            }
            return false;
        }

        private static Character ResolveHitCharacter(Transform hitTransform)
        {
            CharacterManager manager = CharacterManager.Instance;
            if (hitTransform == null || manager == null ||
                manager.character_set == null)
                return null;

            Character rootCandidate = null;
            int rootMatches = 0;
            foreach (Character character in manager.character_set)
            {
                if (character == null || character.transform == null) continue;
                Transform characterTransform = character.transform;
                if (hitTransform == characterTransform ||
                    hitTransform.IsChildOf(characterTransform))
                    return character;

                if (hitTransform.root == characterTransform.root)
                {
                    rootCandidate = character;
                    rootMatches++;
                }
            }

            return rootMatches == 1 ? rootCandidate : null;
        }

        private static bool IsValidEnemy(Level level, Character player,
            Character target)
        {
            if (target == null || target == player || target.IsDied ||
                target.Is_Viewer)
                return false;

            try
            {
                if (target.invincible_time > 0.03f) return false;
                if (level.game_type == RoomInfo.GameType.kGameTypeChiji)
                    return true;
                return target.GetTeam() != player.GetTeam();
            }
            catch
            {
                return false;
            }
        }

        private static bool IsVisibleToPlayer(Character player, Character target)
        {
            try
            {
                if (!target.GetHidden()) return true;
                return player.SeeEffect(target) >= 0.999f;
            }
            catch
            {
                return false;
            }
        }

        private static bool SafeGetHidden(Character player)
        {
            try
            {
                return player != null && player.GetHidden();
            }
            catch
            {
                return false;
            }
        }

        private static int GetCastMask()
        {
            if (_castMaskInitialized) return _castMask;
            _castMaskInitialized = true;
            _castMask = LayerMask.GetMask(new[]
            {
                "kPlayer",
                "kController",
                "Terrarin",
                "Terrain"
            });
            if (_castMask == 0) _castMask = -1;
            return _castMask;
        }

        private static void SetAllowed(bool allowed)
        {
            if (allowed && !_wasAllowed)
            {
                _nextKeyDownAt = 0f;
                _keyDownPulseFrame = -1;
            }
            else if (!allowed)
            {
                _nextKeyDownAt = 0f;
                _keyDownPulseFrame = -1;
            }

            AutoFireAllowed = allowed;
            _wasAllowed = allowed;
        }
    }
}
