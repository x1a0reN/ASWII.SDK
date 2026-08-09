using ASWDEBUG.Logger;
using Harmony;
using System;
using System.Reflection;
using UnityEngine;

namespace ASWDEBUG.Cheats.Other
{
    public static class FlightMode
    {
        public static bool Enabled;
        public static KeyCode AscendKey = KeyCode.PageUp;
        public static KeyCode DescendKey = KeyCode.PageDown;
        public static float VerticalSpeed = 8f;

        private static MoveScript _activeMotor;
        private static bool _originalUseGravity;

        public static void Toggle()
        {
            Enabled = !Enabled;
            if (!Enabled)
            {
                ReleaseActiveMotor(true);
            }

            FileLogger.Log(
                "FEATURE",
                "[FLIGHT] enabled=" + Enabled +
                " ascend=" + AscendKey +
                " descend=" + DescendKey +
                " speed=" + VerticalSpeed.ToString("F1"));
        }

        public static void SetAscendKey(KeyCode key)
        {
            AscendKey = key;
            FileLogger.Log("FEATURE", "[FLIGHT] ascendKey=" + key);
        }

        public static void SetDescendKey(KeyCode key)
        {
            DescendKey = key;
            FileLogger.Log("FEATURE", "[FLIGHT] descendKey=" + key);
        }

        public static void Apply(MoveScript motor)
        {
            if (motor == null || motor.character == null || !motor.character.IsPlayer)
            {
                return;
            }

            if (!Enabled)
            {
                if (ReferenceEquals(_activeMotor, motor))
                {
                    ReleaseActiveMotor(true);
                }
                return;
            }

            if (motor.character.IsDied)
            {
                if (ReferenceEquals(_activeMotor, motor))
                {
                    // The game's ragdoll transition owns gravity after death.
                    ReleaseActiveMotor(false);
                }
                return;
            }

            if (!ReferenceEquals(_activeMotor, motor))
            {
                ReleaseActiveMotor(true);
                _activeMotor = motor;
                _originalUseGravity = motor.useGravity;
            }

            Rigidbody body = motor.MoveRigidBody;
            if (body == null)
            {
                return;
            }

            float direction = 0f;
            if (AscendKey != KeyCode.None && Input.GetKey(AscendKey))
            {
                direction += 1f;
            }
            if (DescendKey != KeyCode.None && Input.GetKey(DescendKey))
            {
                direction -= 1f;
            }

            float speed = Mathf.Clamp(VerticalSpeed, 0.5f, 50f);
            Vector3 velocity = body.velocity;
            velocity.y = direction * speed;

            motor.useGravity = false;
            body.velocity = velocity;
            motor.vertical_speed = velocity.y;
            motor.is_check_fall_down = false;
            motor.is_fall_down = false;
            motor.start_to_fall_down = false;
            motor.fall_down_time = 0f;
            if (motor.fall)
            {
                motor.SetFall(false);
            }
        }

        public static void Shutdown()
        {
            Enabled = false;
            ReleaseActiveMotor(true);
        }

        private static void ReleaseActiveMotor(bool restoreGravity)
        {
            MoveScript motor = _activeMotor;
            _activeMotor = null;

            if (motor == null)
            {
                return;
            }

            if (restoreGravity)
            {
                motor.useGravity = _originalUseGravity;
            }

            Rigidbody body = motor.MoveRigidBody;
            if (body != null)
            {
                Vector3 velocity = body.velocity;
                velocity.y = 0f;
                body.velocity = velocity;
                motor.vertical_speed = 0f;
            }
        }
    }

    [HarmonyPatch]
    [Obfuscation(Exclude = true, ApplyToMembers = true, Feature = "-rename", StripAfterObfuscation = false)]
    public static class Patch_MoveScript_FlightMode
    {
        private static MethodBase TargetMethod()
        {
            return AccessTools.Method(typeof(MoveScript), "FixedUpdate");
        }

        private static void Prefix(MoveScript __instance)
        {
            FlightMode.Apply(__instance);
        }
    }
}
