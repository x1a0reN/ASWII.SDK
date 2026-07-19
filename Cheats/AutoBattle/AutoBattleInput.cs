using ASWDEBUG.Logger;
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using UnityEngine;

namespace ASWDEBUG.Cheats.AutoBattle
{
    public static class AutoBattleInput
    {
        private static readonly Dictionary<KeyCode, float> HeldUntil = new Dictionary<KeyCode, float>();
        private static readonly Dictionary<KeyCode, float> DownUntil = new Dictionary<KeyCode, float>();
        private static readonly Dictionary<KeyCode, int> DownServedFrame = new Dictionary<KeyCode, int>();
        private static float ActivityUntil;
        private static readonly KeyCode[] MovementFallback =
        {
            KeyCode.W, KeyCode.S, KeyCode.A, KeyCode.D,
            KeyCode.UpArrow, KeyCode.DownArrow, KeyCode.LeftArrow, KeyCode.RightArrow
        };

        public static void BeginFrame()
        {
            PruneExpired(HeldUntil);
            PruneExpired(DownUntil, DownServedFrame);
        }

        public static void ClearAll()
        {
            HeldUntil.Clear();
            DownUntil.Clear();
            DownServedFrame.Clear();
            ActivityUntil = 0f;
        }

        public static void MarkActivity(float seconds)
        {
            ActivityUntil = Mathf.Max(ActivityUntil, Time.time + Mathf.Max(0.05f, seconds));
        }

        public static void ClearMovement()
        {
            ClearMovementState(true);
        }

        public static void ClearFire()
        {
            ClearAction(ActionType.kActionFire);
            HeldUntil.Remove(KeyCode.Mouse0);
            DownUntil.Remove(KeyCode.Mouse0);
            DownServedFrame.Remove(KeyCode.Mouse0);
        }

        public static void ClearSecondFire()
        {
            ClearAction(ActionType.kActionSecondFire);
            HeldUntil.Remove(KeyCode.Mouse1);
            DownUntil.Remove(KeyCode.Mouse1);
            DownServedFrame.Remove(KeyCode.Mouse1);
        }

        private static void ClearMovementState(bool clearJump)
        {
            ClearAction(ActionType.kActionMoveForward);
            ClearAction(ActionType.kActionMoveBackward);
            ClearAction(ActionType.kActionMoveLeft);
            ClearAction(ActionType.kActionMoveRight);
            ClearAction(ActionType.kActionCrouch);
            if (clearJump) ClearAction(ActionType.kActionJump);
            for (int i = 0; i < MovementFallback.Length; i++)
            {
                HeldUntil.Remove(MovementFallback[i]);
                DownUntil.Remove(MovementFallback[i]);
                DownServedFrame.Remove(MovementFallback[i]);
            }
        }

        public static bool TryGetKey(KeyCode key, ref bool result)
        {
            if (!IsVirtualActive(key, HeldUntil)) return false;
            result = true;
            return true;
        }

        public static bool TryGetKeyDown(KeyCode key, ref bool result)
        {
            if (!IsVirtualActive(key, DownUntil)) return false;

            int servedFrame;
            if (DownServedFrame.TryGetValue(key, out servedFrame))
            {
                if (servedFrame == Time.frameCount)
                {
                    result = true;
                    return true;
                }

                DownUntil.Remove(key);
                DownServedFrame.Remove(key);
                return false;
            }

            DownServedFrame[key] = Time.frameCount;
            result = true;
            return true;
        }

        public static bool TryGetAxis(string axisName, ref float result)
        {
            if (string.IsNullOrEmpty(axisName)) return false;

            string name = axisName.ToLowerInvariant();
            if (name.IndexOf("horizontal") >= 0)
            {
                result = AxisValue(ActionType.kActionMoveRight, ActionType.kActionMoveLeft, KeyCode.D, KeyCode.A, KeyCode.RightArrow, KeyCode.LeftArrow);
                return Mathf.Abs(result) > 0.001f;
            }

            if (name.IndexOf("vertical") >= 0)
            {
                result = AxisValue(ActionType.kActionMoveForward, ActionType.kActionMoveBackward, KeyCode.W, KeyCode.S, KeyCode.UpArrow, KeyCode.DownArrow);
                return Mathf.Abs(result) > 0.001f;
            }

            return false;
        }

        public static bool TryGetButton(string name, ref bool result)
        {
            KeyCode key;
            if (!TryResolveButtonName(name, out key)) return false;
            return TryGetKey(key, ref result);
        }

        public static bool TryGetButtonDown(string name, ref bool result)
        {
            KeyCode key;
            if (!TryResolveButtonName(name, out key)) return false;
            return TryGetKeyDown(key, ref result);
        }

        public static bool TryGetMouseButton(int button, ref bool result)
        {
            KeyCode key = MouseButtonToKey(button);
            if (key == KeyCode.None) return false;
            return TryGetKey(key, ref result);
        }

        public static bool TryGetMouseButtonDown(int button, ref bool result)
        {
            KeyCode key = MouseButtonToKey(button);
            if (key == KeyCode.None) return false;
            return TryGetKeyDown(key, ref result);
        }

        public static bool TryAnyKey(ref bool result)
        {
            if (Time.time > ActivityUntil && !HasActiveVirtual(HeldUntil)) return false;
            result = true;
            return true;
        }

        public static bool TryAnyKeyDown(ref bool result)
        {
            foreach (KeyValuePair<KeyCode, float> pair in DownUntil)
            {
                if (Time.time > pair.Value) continue;

                int servedFrame;
                if (DownServedFrame.TryGetValue(pair.Key, out servedFrame))
                {
                    if (servedFrame == Time.frameCount)
                    {
                        result = true;
                        return true;
                    }
                    continue;
                }

                DownServedFrame[pair.Key] = Time.frameCount;
                result = true;
                return true;
            }
            return false;
        }

        public static bool TryParseKeyCode(string name, out KeyCode key)
        {
            key = KeyCode.None;
            if (string.IsNullOrEmpty(name)) return false;
            try
            {
                key = (KeyCode)Enum.Parse(typeof(KeyCode), name, true);
                return true;
            }
            catch
            {
                return false;
            }
        }

        public static void HoldKey(KeyCode key, float seconds)
        {
            if (key == KeyCode.None) return;
            HeldUntil[key] = Time.time + Mathf.Max(0.03f, seconds);
        }

        public static void PressKey(KeyCode key, float seconds)
        {
            if (key == KeyCode.None) return;
            float until = Time.time + Mathf.Max(0.04f, seconds);
            HeldUntil[key] = until;
            DownUntil[key] = until;
            DownServedFrame.Remove(key);
        }

        public static void HoldAction(ActionType action, float seconds)
        {
            HoldKey(ResolveActionKey(action), seconds);
        }

        public static void PressAction(ActionType action, float seconds)
        {
            PressKey(ResolveActionKey(action), seconds);
        }

        public static void RequestFire(float seconds)
        {
            HoldKey(KeyCode.Mouse0, seconds);
            HoldAction(ActionType.kActionFire, seconds);
        }

        public static void SetMoveWorld(Character player, Vector3 worldDir, bool roll)
        {
            // Directional refresh runs after route following, so preserve a jump queued earlier this frame.
            ClearMovementState(false);
            if (player == null || worldDir.sqrMagnitude < 0.0001f) return;

            Vector3 dir = worldDir;
            dir.y = 0f;
            if (dir.sqrMagnitude < 0.0001f) return;
            dir.Normalize();

            Vector3 forward = player.transform != null ? player.transform.forward : Vector3.forward;
            Vector3 right = player.transform != null ? player.transform.right : Vector3.right;
            forward.y = 0f;
            right.y = 0f;
            if (forward.sqrMagnitude < 0.0001f) forward = Vector3.forward;
            if (right.sqrMagnitude < 0.0001f) right = Vector3.right;
            forward.Normalize();
            right.Normalize();

            float f = Vector3.Dot(dir, forward);
            float r = Vector3.Dot(dir, right);
            const float threshold = 0.22f;
            const float hold = 0.12f;

            if (f > threshold) HoldAction(ActionType.kActionMoveForward, hold);
            if (f < -threshold) HoldAction(ActionType.kActionMoveBackward, hold);
            if (r > threshold) HoldAction(ActionType.kActionMoveRight, hold);
            if (r < -threshold) HoldAction(ActionType.kActionMoveLeft, hold);

            if (roll)
            {
                PressAction(ActionType.kActionCrouch, 0.10f);
                HoldAction(ActionType.kActionCrouch, 0.16f);
                if (f > threshold) PressAction(ActionType.kActionMoveForward, 0.10f);
                if (f < -threshold) PressAction(ActionType.kActionMoveBackward, 0.10f);
                if (r > threshold) PressAction(ActionType.kActionMoveRight, 0.10f);
                if (r < -threshold) PressAction(ActionType.kActionMoveLeft, 0.10f);
            }
        }

        public static bool IsManualControlActive()
        {
            return IsPhysicalActionDown(ActionType.kActionMoveForward) ||
                   IsPhysicalActionDown(ActionType.kActionMoveBackward) ||
                   IsPhysicalActionDown(ActionType.kActionMoveLeft) ||
                   IsPhysicalActionDown(ActionType.kActionMoveRight) ||
                   IsPhysicalActionDown(ActionType.kActionJump) ||
                   IsPhysicalActionDown(ActionType.kActionCrouch) ||
                   IsPhysicalActionDown(ActionType.kActionFire) ||
                   IsPhysicalKeyDown(KeyCode.Mouse0) ||
                   IsPhysicalKeyDown(KeyCode.Mouse1);
        }

        private static bool IsPhysicalActionDown(ActionType action)
        {
            try
            {
                return IsPhysicalKeyDown(ResolveActionKey(action));
            }
            catch
            {
                return false;
            }
        }

        private static KeyCode ResolveActionKey(ActionType action)
        {
            try
            {
                GameConfig cfg = ASSingleton<GameConfig>.Instance;
                if (cfg != null && cfg.KeyDic != null && cfg.KeyDic.ContainsKey(action))
                {
                    return cfg.KeyDic[action];
                }
            }
            catch
            {
            }

            switch (action)
            {
                case ActionType.kActionMoveForward: return KeyCode.W;
                case ActionType.kActionMoveBackward: return KeyCode.S;
                case ActionType.kActionMoveLeft: return KeyCode.A;
                case ActionType.kActionMoveRight: return KeyCode.D;
                case ActionType.kActionJump: return KeyCode.Space;
                case ActionType.kActionCrouch: return KeyCode.LeftControl;
                case ActionType.kActionFire: return KeyCode.Mouse0;
                case ActionType.kActionSecondFire: return KeyCode.Mouse1;
                case ActionType.kActionReload: return KeyCode.R;
                default: return KeyCode.None;
            }
        }

        private static void ClearAction(ActionType action)
        {
            KeyCode key = ResolveActionKey(action);
            if (key == KeyCode.None) return;
            HeldUntil.Remove(key);
            DownUntil.Remove(key);
            DownServedFrame.Remove(key);
        }

        private static bool IsVirtualActive(KeyCode key, Dictionary<KeyCode, float> map)
        {
            float until;
            return key != KeyCode.None && map.TryGetValue(key, out until) && Time.time <= until;
        }

        private static bool HasActiveVirtual(Dictionary<KeyCode, float> map)
        {
            foreach (KeyValuePair<KeyCode, float> pair in map)
            {
                if (Time.time <= pair.Value) return true;
            }
            return false;
        }

        private static float AxisValue(ActionType positive, ActionType negative, KeyCode fallbackPositive, KeyCode fallbackNegative, KeyCode arrowPositive, KeyCode arrowNegative)
        {
            float v = 0f;
            KeyCode pos = ResolveActionKey(positive);
            KeyCode neg = ResolveActionKey(negative);
            if (IsVirtualActive(pos, HeldUntil) || IsVirtualActive(fallbackPositive, HeldUntil) || IsVirtualActive(arrowPositive, HeldUntil)) v += 1f;
            if (IsVirtualActive(neg, HeldUntil) || IsVirtualActive(fallbackNegative, HeldUntil) || IsVirtualActive(arrowNegative, HeldUntil)) v -= 1f;
            return Mathf.Clamp(v, -1f, 1f);
        }

        private static bool TryResolveButtonName(string name, out KeyCode key)
        {
            key = KeyCode.None;
            if (string.IsNullOrEmpty(name)) return false;
            string n = name.ToLowerInvariant();
            if (n == "fire1" || n == "fire" || n == "mouse0")
            {
                key = ResolveActionKey(ActionType.kActionFire);
                if (key == KeyCode.None) key = KeyCode.Mouse0;
                return true;
            }
            if (n == "fire2" || n == "secondfire" || n == "mouse1")
            {
                key = ResolveActionKey(ActionType.kActionSecondFire);
                if (key == KeyCode.None) key = KeyCode.Mouse1;
                return true;
            }
            if (n == "jump")
            {
                key = ResolveActionKey(ActionType.kActionJump);
                if (key == KeyCode.None) key = KeyCode.Space;
                return true;
            }
            if (n == "reload")
            {
                key = ResolveActionKey(ActionType.kActionReload);
                if (key == KeyCode.None) key = KeyCode.R;
                return true;
            }
            return TryParseKeyCode(name, out key);
        }

        private static KeyCode MouseButtonToKey(int button)
        {
            switch (button)
            {
                case 0: return KeyCode.Mouse0;
                case 1: return KeyCode.Mouse1;
                case 2: return KeyCode.Mouse2;
                default: return KeyCode.None;
            }
        }

        private static void PruneExpired(Dictionary<KeyCode, float> map)
        {
            if (map.Count == 0) return;
            List<KeyCode> dead = null;
            foreach (KeyValuePair<KeyCode, float> pair in map)
            {
                if (Time.time > pair.Value)
                {
                    if (dead == null) dead = new List<KeyCode>();
                    dead.Add(pair.Key);
                }
            }
            if (dead == null) return;
            for (int i = 0; i < dead.Count; i++) map.Remove(dead[i]);
        }

        private static void PruneExpired(Dictionary<KeyCode, float> map, Dictionary<KeyCode, int> servedFrameMap)
        {
            if (map.Count == 0)
            {
                servedFrameMap.Clear();
                return;
            }

            List<KeyCode> dead = null;
            foreach (KeyValuePair<KeyCode, float> pair in map)
            {
                if (Time.time > pair.Value)
                {
                    if (dead == null) dead = new List<KeyCode>();
                    dead.Add(pair.Key);
                }
            }

            if (dead == null) return;
            for (int i = 0; i < dead.Count; i++)
            {
                map.Remove(dead[i]);
                servedFrameMap.Remove(dead[i]);
            }
        }

        private static bool IsPhysicalKeyDown(KeyCode key)
        {
            int vk = ToVirtualKey(key);
            if (vk == 0) return false;
            try
            {
                return (GetAsyncKeyState(vk) & 0x8000) != 0;
            }
            catch (Exception ex)
            {
                FileLogger.Log("AUTO-BATTLE", "GetAsyncKeyState failed: " + ex.Message);
                return false;
            }
        }

        private static int ToVirtualKey(KeyCode key)
        {
            if (key >= KeyCode.A && key <= KeyCode.Z) return 0x41 + (int)(key - KeyCode.A);
            if (key >= KeyCode.Alpha0 && key <= KeyCode.Alpha9) return 0x30 + (int)(key - KeyCode.Alpha0);
            switch (key)
            {
                case KeyCode.Mouse0: return 0x01;
                case KeyCode.Mouse1: return 0x02;
                case KeyCode.Mouse2: return 0x04;
                case KeyCode.Space: return 0x20;
                case KeyCode.LeftControl:
                case KeyCode.RightControl: return 0x11;
                case KeyCode.LeftShift:
                case KeyCode.RightShift: return 0x10;
                case KeyCode.LeftAlt:
                case KeyCode.RightAlt: return 0x12;
                case KeyCode.UpArrow: return 0x26;
                case KeyCode.DownArrow: return 0x28;
                case KeyCode.LeftArrow: return 0x25;
                case KeyCode.RightArrow: return 0x27;
                case KeyCode.Tab: return 0x09;
                case KeyCode.Return: return 0x0D;
                case KeyCode.Escape: return 0x1B;
                default: return 0;
            }
        }

        [DllImport("user32.dll")]
        private static extern short GetAsyncKeyState(int vKey);
    }
}
