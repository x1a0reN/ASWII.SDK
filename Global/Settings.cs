using System;
using System.Collections.Generic;
using UnityEngine;

namespace ASWDEBUG.Global 
{
    public class Settings
    {
        static Settings()
        {
            Settings.TujiNot_spread = false;
            Settings.UserCanTraceList = new List<int>
        {
            0,
            0,
            0,
            0,
            0,
            0,
            0,
            0,
            0,
            0,
            0,
            0,
            0,
            0,
            0,
            0
        };
            Settings.UserCanTraceDistanceList = new List<float>
        {
            -1f,
            -1f,
            -1f,
            -1f,
            -1f,
            -1f,
            -1f,
            -1f,
            -1f,
            -1f,
            -1f,
            -1f,
            -1f,
            -1f,
            -1f,
            -1f
        };
            Settings.UserKillCharacterDistanceList = new List<float>
        {
            -1f,
            -1f,
            -1f,
            -1f,
            -1f,
            -1f,
            -1f,
            -1f
        };
            Settings.UserKillCharacterPositionList = new List<Vector3>
        {
            new Vector3(0f, 0f, 0f),
            new Vector3(0f, 0f, 0f),
            new Vector3(0f, 0f, 0f),
            new Vector3(0f, 0f, 0f),
            new Vector3(0f, 0f, 0f),
            new Vector3(0f, 0f, 0f),
            new Vector3(0f, 0f, 0f),
            new Vector3(0f, 0f, 0f)
        };
        }

        public static bool Visible = false;

        public static Character currentTarget;

        public static float closestDistance = float.MaxValue;

        public static Character bestTarget = null;

        public static bool Aim = false;
        public static bool InfKnife = false;

        public static bool EspEnable = true;
        public static bool Esp3D = true;

        public static bool is_aim_shield = false;
        public static bool is_aim_hidden = false;

        public static List<int> UserCanTraceList;

        public static List<float> UserCanTraceDistanceList;

        public static bool AimTracking = false;
        public static bool AimLocking = false;

        public static bool JumpCheat = false;

        public static bool IgnoreGrenade = false;

        public static bool PassCheckTime = true;

        public static bool TP = false;

        public static List<float> UserKillCharacterDistanceList;

        public static List<Vector3> UserKillCharacterPositionList;

        public static bool AutoKinfeAttack = false;

        public static bool CheatEnable = true;

        public static bool half_damage = false;

        public static bool AutoFire = false;

        public static bool Edit_spread = true;


        public static float _spread = 0f;
        public static float _aimspeed = 40f;


        public static bool TujiNot_spread = false;

        public static bool TujiNot_spread2 = false;

        public static bool AimTrackAll = false;

        public static bool IshpAlpha;

        public static bool AllMapKill = false;

        public static bool ChangeWeapon = false;

        public static bool InfBullet = false;

        public static bool CenterCross = true;

        public static bool CenterCircle = true;

        public static float _radius = 300f;

        public static bool TP2 = false;

        public static bool LoopBeginVote = false;

        public static bool EnableGMMode;

        public static bool LoopInvite = false;

        public static readonly List<Transform> sharedBoneList = new List<Transform>(32);

        public static readonly List<Vector3> sharedWorldPoints = new List<Vector3>(32);

        public static readonly Vector3 halfSize = new Vector3(0.4f, 0.7f, 0.3f);

        public static float smoothVelocityX = 0f; // 横向平滑速度

        public static float smoothVelocityY = 0f; // 纵向平滑速度

        public static float smoothTime = 0.05f; // 越小越快，越大越慢，建议 0.03 ~ 0.08

        public static ulong lastFriendID; // 越小越快，越大越慢，建议 0.03 ~ 0.08

        public static List<ulong> ListFriends; // 越小越快，越大越慢，建议 0.03 ~ 0.08
    }

}
