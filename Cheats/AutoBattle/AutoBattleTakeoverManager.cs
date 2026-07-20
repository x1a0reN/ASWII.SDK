using ASWDEBUG.Cheats.Player;
using ASWDEBUG.Cheats.SurvivalBot;
using ASWDEBUG.Logger;
using RAIN.Navigation;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace ASWDEBUG.Cheats.AutoBattle
{
    public enum AutoBattleState
    {
        Idle,
        Acquire,
        Seek,
        RouteToEngage,
        Engage,
        Peek,
        Evade,
        FlankShield,
        Kite,
        Recover,
        StuckRecovery
    }

    public static class AutoBattleManager
    {
        private enum LookIntentKind
        {
            Engage,
            Seek,
            Route,
            Glance,
            Roam,
            RocketJump
        }

        public static readonly string[] StrategyNames = { "稳健", "激进", "风筝" };
        public static readonly string[] AccuracyNames = { "轻微误差", "中等误差", "明显拟真" };

        public static bool Enabled { get; private set; }
        public static int StrategyMode { get; private set; }
        public static int AccuracyMode { get; private set; }
        public static bool DebugLog { get; private set; } = true;
        public static AutoBattleState State = AutoBattleState.Idle;
        public static string LastStatus = "未启动";
        public static string LastTarget = "-";
        public static string LastPath = "-";
        public static string LastPathProvider = "-";
        public static string LastPathDetail = "-";
        public static string LastAction = "-";
        public static string CurrentRole = "通用";

        internal static bool CopyActiveRoute(List<Vector3> output)
        {
            if (output == null) return false;
            output.Clear();
            int start = Mathf.Clamp(_pathIndex, 0, Path.Count);
            for (int i = start; i < Path.Count; i++) output.Add(Path[i]);
            if (_hasDestination &&
                (output.Count == 0 || XZDistanceSq(output[output.Count - 1], _destination) > 0.04f))
                output.Add(_destination);
            return output.Count > 0;
        }

        private const float TargetRefreshInterval = 0.14f;
        private const float RepathInterval = 0.32f;
        private const float CornerReachDistance = 0.55f;
        private const float ManualOverrideSeconds = 0.30f;
        private const float LogInterval = 0.55f;
        private const float TargetLostHoldSeconds = 1.65f;
        private const float TargetMinimumLockSeconds = 2.60f;
        private const float TargetSwitchScoreAdvantage = 15.0f;
        private const int MaxDetailedTargetCandidates = 6;
        private const float AimDiagInterval = 0.35f;
        private const float CloseEngageDistance = 8.0f;
        private const float ForceCloseEngageDistance = 7.5f;
        private const float CloseStealthVisibleDistance = 5.0f;
        private const float CloseTurnOnlyDistance = 11.0f;
        private const float SeekLookBlendSpeed = 10.5f;
        private const float WeaponDecisionInterval = 0.34f;
        private const float SafeReloadDistance = 13.0f;
        private const float RocketPokeInterval = 4.8f;
        private const float BowPokeInterval = 5.2f;
        private const float RoleSkillInterval = 0.55f;
        private const float SeekRouteMaxDeflection = 30.0f;
        private const float PathLookAheadBlendDistance = 1.5f;
        private const float SearchPointMinimumHold = 0.55f;
        // CameraObj.updateMove applies a fixed -atan(0.2) pitch after finaly.
        private const float CameraBasePitchOffset = -11.309932f;
        private const float CameraMinActualPitch = -59.309932f;
        private const float CameraMaxActualPitch = 63.690068f;
        private const float HighGroundMinHeight = 1.65f;
        private const float HighGroundMaxDistance = 16.0f;
        private const float HighGroundDetectDelay = 0.12f;
        private const float HighGroundClearHold = 0.32f;
        private const float HighGroundLowerProbeMinAngle = 22.0f;
        private const int HighGroundLowerProbeLimit = 3;

        private static readonly List<Character> Characters = new List<Character>(32);
        private static readonly List<Vector3> Path = new List<Vector3>(48);
        private static readonly List<bool> PathJumpFlags = new List<bool>(48);
        private static readonly List<Vector3> CandidatePoints = new List<Vector3>(10);
        private static readonly Dictionary<Character, float> TemporarilySkippedTargets = new Dictionary<Character, float>();
        private static readonly float[] RoamProbeDistances = { 1.4f, 3.0f, 5.2f };
        private static readonly float[] RoamDirectionOffsets = { 0f, 34f, -34f, 68f, -68f, 105f, -105f, 142f, -142f, 180f };
        private static readonly float[] RoamBoundaryProbeHeights = { 0.85f, 1.35f };
        private static readonly Character[] TargetScanCandidates = new Character[MaxDetailedTargetCandidates];
        private static readonly float[] TargetScanScores = new float[MaxDetailedTargetCandidates];

        private static Character _target;
        private static Character _localPatrolTarget;
        private static float _localPatrolReachedAt;
        private static int _localPatrolIndex = -1;

        internal static Character CurrentTarget
        {
            get { return _target; }
        }

        private static float _nextTargetRefresh;
        private static float _nextRepath;
        private static float _nextFireTime;
        private static float _nextRollTime;
        private static float _manualUntil;
        private static float _nextLogTime;
        private static Vector3 _destination;
        private static int _pathIndex;
        private static Vector3 _lastPlayerPos;
        private static Vector3 _aimJitterOffset;
        private static Vector3 _aimJitterTargetOffset;
        private static float _stuckTime;
        private static int _stuckCount;
        private static bool _lastEnabled;
        private static Character _aimJitterTarget;
        private static float _aimJitterLower;
        private static float _aimJitterTargetLower;
        private static float _nextAimJitterRefresh;
        private static float _targetAcquiredAt;
        private static float _targetLastVisibleAt;
        private static float _lastTargetSwitchAt;
        private static string _lastTargetSwitchReason = "-";
        private static float _nextAimDiagLogTime;
        private static float _lastLookStepYaw;
        private static float _lastLookStepPitch;
        private static string _lastLookMode;
        private static Character _smoothedAimTarget;
        private static Vector3 _smoothedAimPoint;
        private static bool _hasSmoothedAimPoint;
        private static float _lookYawVelocity;
        private static float _lookPitchVelocity;
        private static float _lastLookControlYaw;
        private static float _lastLookControlPitch;
        private static float _lastLookDesiredYaw;
        private static float _lastLookDesiredPitch;
        private static float _lastTargetRouteDelta = -1f;
        private static float _lastLookIntentDelta = -1f;
        private static int _lastLookIntentFrame = -1;
        private static string _lastLookIntent = "idle";
        private static float _lookSettlingSince;
        private static float _lastAimSettleMs = -1f;
        private static bool _lastAimReady;
        private static int _lastExposureCount;
        private static int _lastAimingThreatCount;
        private static float _nextExposureRefresh;
        private static float _nextJukeTime;
        private static float _jukeUntil;
        private static Vector3 _jukeDirection;
        private static float _nextPathDiagLogTime;
        private static int _pathBuildSeq;
        private static bool _pathSearchPending;
        private static int _pendingFollowFrames;
        private static int _pendingHoldFrames;
        private static float _nextPendingPathLogTime;
        private static float _nextStuckRecoveryTime;
        private static float _nextWallRecoveryTime;
        private static Vector3 _smoothedSeekLookPoint;
        private static Vector3 _smoothedSeekLookDir;
        private static float _smoothedSeekLookDistance;
        private static bool _hasSmoothedSeekLookPoint;
        private static float _nextWeaponDecisionTime;
        private static float _nextSniperScopeTime;
        private static bool _hasDestination;
        private static int _wallAheadCount;
        private static float _nextRocketPokeTime;
        private static float _nextBowPokeTime;
        private static float _nextRoleSkillTime;
        private static string _lastFireBlock = "-";
        private static bool _lastStrictFireLine;
        private static float _nextNoTargetMoveChangeTime;
        private static Vector3 _noTargetMoveDir;
        private static float _nextNoTargetJumpTime;
        private static float _nextNoTargetSlideTime;
        private static float _nextRocketJumpTime;
        private static float _rocketJumpActiveUntil;
        private static Vector3 _rocketJumpDir;
        private static float _nextCombatMoveChangeTime;
        private static Vector3 _combatMoveDir;
        private static float _nextCombatJumpTime;
        private static Character _highGroundRepositionTarget;
        private static Vector3 _highGroundRepositionPoint;
        private static float _highGroundBlockedSince;
        private static float _highGroundClearSince;
        private static float _nextHighGroundPointRefresh;
        private static bool _highGroundRepositionActive;
        private static float _highGroundSearchStartedAt;
        private static float _highGroundPointSelectedAt;
        private static float _highGroundLastProgressAt;
        private static Vector3 _highGroundLastProgressPosition;
        private static int _highGroundCandidateCursor;
        private static int _highGroundSelectedSector = -1;
        private static int _highGroundFailedSectorMask;
        private static int _highGroundFailureCount;
        private static Vector3 _highGroundTrackedTargetPosition;
        private static float _nextHighGroundGlanceTime;
        private static float _highGroundGlanceUntil;
        private static float _highGroundGlanceSign;
        private static float _nextPathJumpTime;
        private static Character _occludedSeekTarget;
        private static float _occludedSeekYawOffset;
        private static float _nextOccludedSeekOffsetRefresh;
        private static Vector3 _stableRouteLookDir;
        private static Vector3 _pendingRouteLookDir;
        private static float _pendingRouteLookSince;
        private static bool _hasStableRouteLookDir;
        private static Character _stableSearchTarget;
        private static Vector3 _stableSearchPoint;
        private static float _stableSearchPointScore;
        private static float _stableSearchPointHoldUntil;
        private static bool _hasStableSearchPoint;
        private static readonly List<Vector3> FailedSearchPoints = new List<Vector3>(12);
        private static Character _failedSearchTarget;
        private static Vector3 _failedSearchTargetPosition;
        private static bool _currentPathPartial;
        private static float _currentPathResidual;

        public static void ToggleEnabled()
        {
            SetEnabled(!Enabled, "toggle");
        }

        public static void ToggleAutoUseLink()
        {
            FileLogger.Log("AUTO-BATTLE", "AutoUse link is unavailable in survival fork");
        }

        public static void ToggleDebugLog()
        {
            DebugLog = !DebugLog;
        }

        public static void SetStrategy(int index)
        {
            StrategyMode = Mathf.Clamp(index, 0, StrategyNames.Length - 1);
        }

        public static void SetAccuracy(int index)
        {
            AccuracyMode = Mathf.Clamp(index, 0, AccuracyNames.Length - 1);
        }

        public static void SetEnabled(bool enabled, string reason)
        {
            if (Enabled == enabled) return;
            Enabled = enabled;
            FileLogger.Log("AUTO-BATTLE", "takeover enabled=" + enabled + " reason=" + reason);
            ResetRuntime(enabled ? "已启动" : "已关闭");
            if (!enabled) _lastEnabled = false;
        }

        public static void Tick(Level level, Character player, Camera cam)
        {
            AutoBattleInput.BeginFrame();

            if (!Enabled)
            {
                if (_lastEnabled) ResetRuntime("已关闭");
                _lastEnabled = false;
                return;
            }
            _lastEnabled = true;

            if (level == null || player == null || cam == null)
            {
                ResetRuntime("等待进入对局");
                return;
            }

            MarkPlayerActivity(player);
            CurrentRole = DetectCurrentRole(player);

            if (player.IsDied)
            {
                State = AutoBattleState.Recover;
                LastStatus = "玩家死亡，等待自动使用规则处理";
                AutoBattleInput.ClearMovement();
                EnsureAutoUseLink();
                LogMaybe(player, null, "dead");
                return;
            }

            EnsureAutoUseLink();

            if (AutoBattleInput.IsManualControlActive())
            {
                _manualUntil = Time.time + ManualOverrideSeconds;
                AutoBattleInput.ClearMovement();
                LastAction = "roam";
                LastStatus = "no target roam";
                State = AutoBattleState.Idle;
                LastStatus = "检测到手动输入，AI 暂停接管";
                LastAction = "手动暂停";
                LogMaybe(player, null, "manual_override");
                return;
            }

            if (Time.time < _manualUntil)
            {
                AutoBattleInput.ClearAll();
                return;
            }

            if (LocalNavigationCombatTest.Running)
            {
                RunLocalNavigationPatrol(player, cam);
                return;
            }

            if (Time.time >= _nextTargetRefresh || !IsUsableTarget(player, _target))
            {
                _nextTargetRefresh = Time.time + TargetRefreshInterval;
                RefreshTarget(level, player, cam);
            }

            if (_target == null)
            {
                TryCloseSniperScope(player, "no_target");
                if (RunNoTargetRoam(player, cam))
                {
                    LogMaybe(player, null, LastAction);
                    return;
                }
                State = AutoBattleState.Acquire;
                LastTarget = "-";
                LastPath = "roam";
                LastPathProvider = "roam";
                LastAction = "roam";
                LastAction = "扫描";
                LastStatus = "没有可追踪目标";
                AutoBattleInput.ClearMovement();
                LastAction = "roam";
                LastStatus = "no target roam";
                LogMaybe(player, null, "no_target");
                return;
            }

            TargetSense sense = BuildSense(player, _target, cam);
            _lastStrictFireLine = sense.StrictFireLineOfSight;
            LastTarget = SafeTargetName(_target);
            if (sense.Visible) _targetLastVisibleAt = Time.time;
            bool targetInvincible = sense.Invincible;
            if (targetInvincible) _lastFireBlock = "invincible";
            ManageWeapon(player, sense);
            bool assaultSniper = IsAssaultSniperRole(player);
            if (assaultSniper && !IsConfirmedSniperAttack(sense))
                TryCloseSniperScope(player, "seek");

            if (ShouldRunHighGroundReposition(player, _target, sense))
            {
                RunHighGroundReposition(player, _target, cam, sense);
                LogMaybe(player, sense, LastAction);
                return;
            }

            if (!sense.Visible)
            {
                if (!sense.VisibleByGame)
                {
                    State = AutoBattleState.Acquire;
                    LastAction = "等待";
                    LastStatus = "目标隐身或不可被本机看见，放弃追击";
                    AutoBattleInput.ClearAll();
                    _target = null;
                    LogMaybe(player, sense, "hidden_skip");
                    return;
                }

                State = AutoBattleState.Seek;
                if (sense.StrictFireLineOfSight && sense.LineOfSight && sense.Distance <= CloseTurnOnlyDistance)
                {
                    AutoBattleInput.ClearMovement();
                    LookAtPoint(player, cam, sense.AimPoint, LookIntentKind.Engage);
                    LastAction = "转向接战";
                    LastStatus = "近距离转向目标 | 距离 " + sense.Distance.ToString("0.0");
                    LogMaybe(player, sense, "close_turn");
                    return;
                }

                if (sense.StrictFireLineOfSight && sense.LineOfSight && !sense.OnScreen)
                {
                    AutoBattleInput.ClearMovement();
                    Vector3 bodyPoint = _target.transform.position + Vector3.up * 0.9f;
                    LookAtPoint(player, cam, bodyPoint, LookIntentKind.Engage);
                    LastAction = "转向搜敌";
                    LastStatus = "目标在视野外，转向搜索 | 距离 " + sense.Distance.ToString("0.0");
                    LogMaybe(player, sense, "turn_seek");
                    return;
                }

                Vector3 searchPoint = IsAssaultSniperRole(player) ? SelectSniperSearchPoint(player, _target, sense) : SelectSearchPoint(player, _target, sense);
                Vector3 seekDir = UpdateNavigation(player, searchPoint, sense, false);
                AutoBattleInput.SetMoveWorld(player, seekDir, false);
                Vector3 seekLookPoint = SelectSeekLookPoint(player, _target, searchPoint, seekDir, sense);
                LookAtPoint(player, cam, seekLookPoint, LookIntentKind.Seek);
                LastAction = "搜敌";
                LastStatus = "搜敌接近 | 目标 " + LastTarget +
                             " | 距离 " + sense.Distance.ToString("0.0") +
                             " | 路径 " + LastPath;
                LogMaybe(player, sense, "seek");
                return;
            }

            bool shieldFront = IsShieldFront(player, _target);
            if (shieldFront && TrySwitchOffShieldTarget(level, player, cam))
            {
                LastAction = "switch_off_shield";
                LogMaybe(player, sense, "switch_off_shield");
                return;
            }

            bool sniperMeleeEmergency = assaultSniper && sense.Distance <= 3.6f;
            bool shouldKite = ShouldKite(player, sense);
            if (assaultSniper && !sniperMeleeEmergency && sense.Distance < 13.0f)
                shouldKite = true;
            if (Time.time >= _nextExposureRefresh)
            {
                _nextExposureRefresh = Time.time + (Characters.Count >= 12 ? 0.28f : 0.16f);
                _lastExposureCount = CountExposureAtPoint(player.transform.position, player, _target, false);
                _lastAimingThreatCount = CountExposureAtPoint(player.transform.position, player, _target, true);
            }
            int exposure = _lastExposureCount;
            int aimingThreats = _lastAimingThreatCount;
            bool shouldPeekOrEvade = ShouldTacticalAvoid(player, sense, exposure, aimingThreats);
            bool closeHardEngage = !shieldFront && sense.FireLineOfSight && sense.Distance <= ForceCloseEngageDistance;
            if (closeHardEngage)
            {
                shouldKite = false;
                shouldPeekOrEvade = false;
                _jukeUntil = 0f;
            }
            bool focusThreat = HealthPercent(player) <= 35f && sense.Distance > 10f && IsEnemyFacingPoint(_target, player.transform.position, 0.74f) && sense.Distance < 18f;
            if (!closeHardEngage && !shouldPeekOrEvade && focusThreat && Time.time >= _nextJukeTime)
            {
                _jukeDirection = SelectJukeDirection(player, _target);
                _jukeUntil = Time.time + UnityEngine.Random.Range(0.18f, 0.30f);
                _nextJukeTime = Time.time + UnityEngine.Random.Range(3.8f, 5.6f);
            }
            bool jukeActive = !shouldPeekOrEvade && Time.time < _jukeUntil && _jukeDirection.sqrMagnitude > 0.01f;
            Vector3 desiredPoint = closeHardEngage
                ? player.transform.position
                : (assaultSniper && !sniperMeleeEmergency
                ? SelectSniperCombatPoint(player, _target, sense)
                : (shouldPeekOrEvade
                    ? SelectSaferCombatPoint(player, _target, sense)
                    : (jukeActive ? player.transform.position + _jukeDirection * 2.4f : SelectEngagePoint(player, _target, sense, shieldFront, shouldKite))));
            bool needsMove = !closeHardEngage && (desiredPoint - player.transform.position).sqrMagnitude > 1.8f * 1.8f;
            bool roll = false;

            if (shouldPeekOrEvade)
            {
                State = aimingThreats > 0 ? AutoBattleState.Evade : AutoBattleState.Peek;
                LastAction = aimingThreats > 0 ? "躲弹" : "peek换位";
                roll = HealthPercent(player) <= 28f && aimingThreats >= 2 && Time.time >= _nextRollTime;
                needsMove = true;
            }
            else if (jukeActive)
            {
                State = AutoBattleState.Peek;
                LastAction = "侧身规避";
                needsMove = true;
            }
            else if (shieldFront)
            {
                State = AutoBattleState.FlankShield;
                LastAction = "绕盾";
                roll = sense.Distance < 6.0f && Time.time >= _nextRollTime;
            }
            else if (shouldKite)
            {
                State = AutoBattleState.Kite;
                LastAction = "风筝";
                roll = sense.Distance < 5.0f && Time.time >= _nextRollTime;
            }
            else if (needsMove)
            {
                State = AutoBattleState.RouteToEngage;
                LastAction = "寻路";
            }
            else
            {
                State = AutoBattleState.Engage;
                LastAction = "接战";
            }

            Vector3 moveDir = Vector3.zero;
            if (needsMove || shouldPeekOrEvade || jukeActive || shieldFront || shouldKite)
            {
                moveDir = UpdateNavigation(player, desiredPoint, sense, shouldPeekOrEvade || jukeActive || shieldFront || shouldKite);
                if (roll && moveDir.sqrMagnitude > 0.01f)
                {
                    _nextRollTime = Time.time + 2.35f + UnityEngine.Random.Range(0f, 0.35f);
                }
                else
                {
                    roll = false;
                }
                AutoBattleInput.SetMoveWorld(player, moveDir, roll);
            }
            else
            {
                bool combatJump;
                if (TryGetCombatMove(player, _target, sense, out moveDir, out combatJump))
                {
                    AutoBattleInput.SetMoveWorld(player, moveDir, false);
                    if (combatJump)
                    {
                        AutoBattleInput.PressAction(ActionType.kActionJump, 0.10f);
                        AutoBattleInput.HoldAction(ActionType.kActionJump, 0.18f);
                    }
                    LastAction += "+combat_move";
                }
                else
                {
                    AutoBattleInput.ClearMovement();
                }
            }

            if (TryRunRoleSkillTactics(player, _target, cam, sense, exposure, aimingThreats))
            {
                _lastFireBlock = "role_skill";
                LastStatus = BuildStatus(sense, shieldFront, shouldKite, shouldPeekOrEvade || jukeActive, true, false, exposure, aimingThreats);
                LogMaybe(player, sense, LastAction);
                return;
            }

            if (!targetInvincible && TryRunRoleWeaponTactics(player, _target, cam, sense, assaultSniper, sniperMeleeEmergency))
            {
                _lastFireBlock = "role_weapon";
                LastStatus = BuildStatus(sense, shieldFront, shouldKite, shouldPeekOrEvade || jukeActive, true, true, exposure, aimingThreats);
                LogMaybe(player, sense, LastAction);
                return;
            }

            bool aimReady = AimAt(player, _target, cam, sense);
            bool canFire = ShouldFire(player, _target, sense, aimReady);
            if (canFire)
            {
                AutoBattleInput.RequestFire(UnityEngine.Random.Range(0.055f, 0.11f));
                _nextFireTime = Time.time + NextFireDelayForWeapon(CurrentWeaponType(player));
                LastAction += "+开火";
            }

            LastStatus = BuildStatus(sense, shieldFront, shouldKite, shouldPeekOrEvade || jukeActive, aimReady, canFire, exposure, aimingThreats);
            if (targetInvincible)
                LastStatus = "跟踪无敌目标 | 距离 " + sense.Distance.ToString("0.0") + " | 路径 " + LastPath;
            LogMaybe(player, sense, LastAction);
        }

        private static void RunLocalNavigationPatrol(Character player, Camera cam)
        {
            AutoBattleInput.ClearAll();
            _target = null;
            CurrentRole = "纯寻路";
            _lastFireBlock = "navigation_only";

            Character patrolTarget;
            int patrolIndex;
            if (!LocalNavigationCombatTest.TryGetPatrolTarget(out patrolTarget, out patrolIndex) ||
                patrolTarget == null || patrolTarget.transform == null)
            {
                _localPatrolTarget = null;
                _localPatrolIndex = -1;
                _localPatrolReachedAt = 0f;
                State = AutoBattleState.Idle;
                LastTarget = "-";
                LastAction = "等待 Bot";
                LastStatus = "纯寻路巡回等待可用 Bot";
                AutoBattleInput.ClearMovement();
                return;
            }

            if (_localPatrolTarget != patrolTarget || _localPatrolIndex != patrolIndex)
            {
                _localPatrolTarget = patrolTarget;
                _localPatrolIndex = patrolIndex;
                _localPatrolReachedAt = 0f;
                ClearCurrentPath();
                _pathIndex = 0;
                _hasDestination = false;
                _nextRepath = 0f;
                FileLogger.Log("AUTO-BATTLE][LEVEL33-TEST", "patrol_target index=" + (patrolIndex + 1) +
                    " target=" + SafeTargetName(patrolTarget) +
                    " pos=" + FormatVec(patrolTarget.transform.position));
            }

            LastTarget = SafeTargetName(patrolTarget);
            Vector3 destination = patrolTarget.transform.position;
            float horizontal = Mathf.Sqrt(XZDistanceSq(player.transform.position, destination));
            float vertical = Mathf.Abs(player.transform.position.y - destination.y);
            if (horizontal <= 1.85f && vertical <= 2.4f)
            {
                AutoBattleInput.ClearMovement();
                State = AutoBattleState.Idle;
                LastPath = "patrol_arrived";
                LastAction = "已到达 Bot " + (patrolIndex + 1);
                LastStatus = "纯寻路巡回 | 已到达 " + (patrolIndex + 1) +
                             "/" + LocalNavigationCombatTest.BotCount;
                if (_localPatrolReachedAt <= 0f) _localPatrolReachedAt = Time.time;
                if (Time.time - _localPatrolReachedAt >= 0.65f)
                {
                    LocalNavigationCombatTest.AdvancePatrolTarget(patrolTarget);
                    _localPatrolTarget = null;
                    _localPatrolIndex = -1;
                    _localPatrolReachedAt = 0f;
                    ClearCurrentPath();
                    _hasDestination = false;
                    _nextRepath = 0f;
                }
                LogMaybe(player, null, "navigation_patrol_arrived");
                return;
            }

            _localPatrolReachedAt = 0f;
            State = AutoBattleState.RouteToEngage;
            Vector3 moveDir = UpdateNavigation(player, destination, null, false, true, false);
            AutoBattleInput.SetMoveWorld(player, moveDir, false);
            Vector3 routeLook = GetPathLookDirection(player, moveDir);
            if (routeLook.sqrMagnitude > 0.01f)
            {
                Vector3 lookPoint = player.transform.position + Vector3.up * 0.9f + routeLook * 10f;
                LookAtPoint(player, cam, lookPoint, LookIntentKind.Route);
            }
            LastAction = "寻路巡回";
            LastStatus = "纯寻路巡回 | 目标 " + (patrolIndex + 1) +
                         "/" + LocalNavigationCombatTest.BotCount +
                         " | 距离 " + horizontal.ToString("0.0") +
                         " | 路径 " + LastPath;
            LogMaybe(player, null, "navigation_patrol");
        }

        private static void ResetRuntime(string reason)
        {
            AutoBattleInput.ClearAll();
            RestoreAutoUseIfNeeded();
            _target = null;
            _localPatrolTarget = null;
            _localPatrolReachedAt = 0f;
            _localPatrolIndex = -1;
            ClearCurrentPath();
            TemporarilySkippedTargets.Clear();
            _pathIndex = 0;
            _destination = Vector3.zero;
            _lastPlayerPos = Vector3.zero;
            _stuckTime = 0f;
            _stuckCount = 0;
            _nextTargetRefresh = 0f;
            _nextRepath = 0f;
            _nextFireTime = 0f;
            _nextRollTime = 0f;
            _manualUntil = 0f;
            _aimJitterTarget = null;
            _aimJitterOffset = Vector3.zero;
            _aimJitterTargetOffset = Vector3.zero;
            _aimJitterLower = 0f;
            _aimJitterTargetLower = 0f;
            _nextAimJitterRefresh = 0f;
            _targetAcquiredAt = 0f;
            _targetLastVisibleAt = 0f;
            _lastTargetSwitchAt = 0f;
            _lastTargetSwitchReason = "-";
            _nextAimDiagLogTime = 0f;
            _pathSearchPending = false;
            _pendingFollowFrames = 0;
            _pendingHoldFrames = 0;
            _nextPendingPathLogTime = 0f;
            _nextStuckRecoveryTime = 0f;
            _nextWallRecoveryTime = 0f;
            _lastLookStepYaw = 0f;
            _lastLookStepPitch = 0f;
            _lastLookMode = null;
            _smoothedAimTarget = null;
            _smoothedAimPoint = Vector3.zero;
            _hasSmoothedAimPoint = false;
            _lookYawVelocity = 0f;
            _lookPitchVelocity = 0f;
            _lastLookControlYaw = 0f;
            _lastLookControlPitch = 0f;
            _lastLookDesiredYaw = 0f;
            _lastLookDesiredPitch = 0f;
            _lastTargetRouteDelta = -1f;
            _lastLookIntentDelta = -1f;
            _lastLookIntentFrame = -1;
            _lastLookIntent = "idle";
            _lookSettlingSince = 0f;
            _lastAimSettleMs = -1f;
            _lastAimReady = false;
            _lastExposureCount = 0;
            _lastAimingThreatCount = 0;
            _nextExposureRefresh = 0f;
            _nextJukeTime = 0f;
            _jukeUntil = 0f;
            _jukeDirection = Vector3.zero;
            _hasSmoothedSeekLookPoint = false;
            _smoothedSeekLookPoint = Vector3.zero;
            _smoothedSeekLookDir = Vector3.zero;
            _smoothedSeekLookDistance = 0f;
            _nextWeaponDecisionTime = 0f;
            _nextSniperScopeTime = 0f;
            _hasDestination = false;
            _wallAheadCount = 0;
            _nextRocketPokeTime = 0f;
            _nextBowPokeTime = 0f;
            _nextRoleSkillTime = 0f;
            _lastFireBlock = "-";
            _lastStrictFireLine = false;
            _nextNoTargetMoveChangeTime = 0f;
            _noTargetMoveDir = Vector3.zero;
            _nextNoTargetJumpTime = 0f;
            _nextNoTargetSlideTime = 0f;
            _nextRocketJumpTime = 0f;
            _rocketJumpActiveUntil = 0f;
            _rocketJumpDir = Vector3.zero;
            _nextCombatMoveChangeTime = 0f;
            _combatMoveDir = Vector3.zero;
            _nextCombatJumpTime = 0f;
            _nextPathJumpTime = 0f;
            _stableRouteLookDir = Vector3.zero;
            _pendingRouteLookDir = Vector3.zero;
            _pendingRouteLookSince = 0f;
            _hasStableRouteLookDir = false;
            _stableSearchTarget = null;
            _stableSearchPoint = Vector3.zero;
            _stableSearchPointScore = 0f;
            _stableSearchPointHoldUntil = 0f;
            _hasStableSearchPoint = false;
            ResetSearchPointFailures();
            ResetHighGroundReposition();
            State = AutoBattleState.Idle;
            LastTarget = "-";
            LastPath = "-";
            LastPathProvider = "-";
            LastAction = reason;
            LastStatus = reason;
        }

        private static void MarkPlayerActivity(Character player)
        {
            AutoBattleInput.MarkActivity(0.35f);
            try
            {
                if (player != null) player.ResetIdleMenu();
            }
            catch
            {
            }
        }

        private static void EnsureAutoUseLink()
        {
            // The survival fork intentionally has no separate AutoUse rule engine.
        }

        private static void RestoreAutoUseIfNeeded()
        {
            // No linked AutoUse state to restore in this fork.
        }

        private static bool RunNoTargetRoam(Character player, Camera cam)
        {
            if (player == null || player.transform == null || cam == null) return false;

            State = AutoBattleState.Acquire;
            LastTarget = "-";
            LastPath = "roam";
            LastPathProvider = "roam";

            Vector3 dir = GetNoTargetRoamDir(player);
            LastPath = dir.sqrMagnitude > 0.01f ? "roam_safe" : "roam_edge_hold";
            if (TryNoTargetRocketJump(player, cam, dir))
                return true;

            ManageWeapon(player, null);
            if (dir.sqrMagnitude > 0.01f)
            {
                if (_nextNoTargetJumpTime <= 0f)
                    _nextNoTargetJumpTime = Time.time + UnityEngine.Random.Range(4.8f, 7.2f);
                if (_nextNoTargetSlideTime <= 0f)
                    _nextNoTargetSlideTime = Time.time + UnityEngine.Random.Range(2.8f, 4.8f);

                bool jump = Time.time >= _nextNoTargetJumpTime && SafeIsOnGround(player);
                bool slide = !jump && Time.time >= _nextNoTargetSlideTime;
                AutoBattleInput.SetMoveWorld(player, dir, slide);
                if (jump)
                {
                    AutoBattleInput.PressAction(ActionType.kActionJump, 0.10f);
                    AutoBattleInput.HoldAction(ActionType.kActionJump, 0.18f);
                    _nextNoTargetJumpTime = Time.time + UnityEngine.Random.Range(5.5f, 8.8f);
                    _nextNoTargetSlideTime = Mathf.Max(_nextNoTargetSlideTime, Time.time + 1.1f);
                    FileLogger.Log("AUTO-BATTLE][ROAM", "action=jump next=" + (_nextNoTargetJumpTime - Time.time).ToString("0.0"));
                }
                else if (slide)
                {
                    _nextNoTargetSlideTime = Time.time + UnityEngine.Random.Range(3.4f, 6.2f);
                    FileLogger.Log("AUTO-BATTLE][ROAM", "action=slide next=" + (_nextNoTargetSlideTime - Time.time).ToString("0.0"));
                }

                Vector3 lookPoint = player.transform.position + dir.normalized * 8.0f + Vector3.up * 1.05f;
                LookAtPoint(player, cam, lookPoint, LookIntentKind.Roam);
                if (slide) LastPath += " slide";
                else if (jump) LastPath += " jump";
            }
            else
            {
                AutoBattleInput.ClearMovement();
            }

            LastAction = dir.sqrMagnitude > 0.01f ? "巡游" : "边缘避让";
            LastStatus = dir.sqrMagnitude > 0.01f ? "无目标巡游" : "前方无安全路线，避开地图边缘";
            return true;
        }

        private static Vector3 GetNoTargetRoamDir(Character player)
        {
            try
            {
                if (player == null || player.transform == null) return Vector3.zero;

                bool needNew = Time.time >= _nextNoTargetMoveChangeTime || _noTargetMoveDir.sqrMagnitude < 0.01f;
                if (!needNew && !IsRoamDirectionSafe(player, _noTargetMoveDir))
                    needNew = true;

                if (needNew)
                {
                    Vector3 selected;
                    if (TrySelectSafeRoamDirection(player, out selected))
                    {
                        _noTargetMoveDir = selected;
                        _nextNoTargetMoveChangeTime = Time.time + UnityEngine.Random.Range(1.7f, 3.2f);
                    }
                    else
                    {
                        _noTargetMoveDir = Vector3.zero;
                        _nextNoTargetMoveChangeTime = Time.time + 0.28f;
                    }
                }

                return _noTargetMoveDir.sqrMagnitude > 0.01f ? _noTargetMoveDir.normalized : Vector3.zero;
            }
            catch
            {
                return Vector3.zero;
            }
        }

        private static bool TrySelectSafeRoamDirection(Character player, out Vector3 selected)
        {
            selected = Vector3.zero;
            if (player == null || player.transform == null) return false;

            Vector3 forward = player.transform.forward;
            forward.y = 0f;
            if (forward.sqrMagnitude < 0.01f) forward = Vector3.forward;
            forward.Normalize();

            Vector3 previous = _noTargetMoveDir;
            previous.y = 0f;
            if (previous.sqrMagnitude > 0.01f) previous.Normalize();

            float randomBias = UnityEngine.Random.Range(-22f, 22f);
            float bestScore = float.MaxValue;
            for (int i = 0; i < RoamDirectionOffsets.Length; i++)
            {
                float angle = randomBias + RoamDirectionOffsets[i];
                Vector3 candidate = Quaternion.AngleAxis(angle, Vector3.up) * forward;
                candidate.y = 0f;
                if (candidate.sqrMagnitude < 0.01f) continue;
                candidate.Normalize();
                if (!IsRoamDirectionSafe(player, candidate)) continue;

                float score = Mathf.Abs(RoamDirectionOffsets[i]) * 0.025f + UnityEngine.Random.Range(0f, 0.45f);
                score -= Mathf.Max(0f, Vector3.Dot(forward, candidate)) * 0.30f;
                if (previous.sqrMagnitude > 0.01f)
                    score -= Mathf.Max(0f, Vector3.Dot(previous, candidate)) * 0.70f;
                if (score < bestScore)
                {
                    bestScore = score;
                    selected = candidate;
                }
            }

            return selected.sqrMagnitude > 0.01f;
        }

        private static bool IsRoamDirectionSafe(Character player, Vector3 direction)
        {
            if (player == null || player.transform == null) return false;
            direction.y = 0f;
            if (direction.sqrMagnitude < 0.01f) return false;
            direction.Normalize();

            Vector3 playerPos = player.transform.position;
            Transform ignoreRoot = SafeRoot(player);
            if (HasRoamBoundaryBlock(playerPos, direction, RoamProbeDistances[RoamProbeDistances.Length - 1], ignoreRoot))
                return false;

            Vector3 previousGround;
            if (!TryProjectNavigationGround(playerPos, playerPos, out previousGround)) previousGround = playerPos;
            for (int i = 0; i < RoamProbeDistances.Length; i++)
            {
                Vector3 raw = playerPos + direction * RoamProbeDistances[i];
                Vector3 grounded;
                if (!TryProjectNavigationGround(raw, playerPos, out grounded)) return false;
                if (Mathf.Abs(grounded.y - previousGround.y) > 1.25f) return false;
                if (!AutoBattleRoutePlanner.HasWalkSegment(previousGround, grounded, ignoreRoot)) return false;
                previousGround = grounded;
            }

            float penalty = AutoBattleRoutePlanner.CandidatePenalty(playerPos, previousGround, ignoreRoot);
            return penalty < 120f;
        }

        private static bool HasRoamBoundaryBlock(Vector3 playerPos, Vector3 direction, float distance, Transform ignoreRoot)
        {
            for (int i = 0; i < RoamBoundaryProbeHeights.Length; i++)
            {
                RaycastHit[] hits = Physics.RaycastAll(playerPos + Vector3.up * RoamBoundaryProbeHeights[i], direction, distance);
                if (hits == null) continue;
                for (int j = 0; j < hits.Length; j++)
                {
                    Collider collider = hits[j].collider;
                    if (collider == null || collider.isTrigger) continue;
                    if (ShouldIgnoreHit(collider.transform, ignoreRoot)) continue;
                    return true;
                }
            }
            return false;
        }

        private static bool TryNoTargetRocketJump(Character player, Camera cam, Vector3 roamDir)
        {
            if (player == null || player.transform == null || cam == null) return false;
            if (!IsHeavyRole(player)) return false;
            if (roamDir.sqrMagnitude < 0.01f) return false;
            if (Time.time < _nextRocketJumpTime && Time.time > _rocketJumpActiveUntil) return false;

            WeaponBase rpg = FindWeaponByType(player, WeaponType.kWeaponTypeRPG, true);
            if (rpg == null)
            {
                _rocketJumpActiveUntil = 0f;
                _nextRocketJumpTime = Time.time + 1.6f;
                if (player.mWeapon != null && GetWeaponTypeSafe(player.mWeapon) == WeaponType.kWeaponTypeRPG)
                    TrySwitchBestNonSpecialOrFallbackWeapon(player, null, "roam_rpg_not_ready");
                return false;
            }

            if (Time.time > _rocketJumpActiveUntil)
            {
                Vector3 dir = roamDir;
                dir.y = 0f;
                _rocketJumpDir = dir.normalized;
                _rocketJumpActiveUntil = Time.time + 0.75f;
            }

            if (!IsRoamDirectionSafe(player, _rocketJumpDir))
            {
                _rocketJumpActiveUntil = 0f;
                _nextRocketJumpTime = Time.time + 2.5f;
                return false;
            }

            AutoBattleInput.SetMoveWorld(player, _rocketJumpDir, false);
            AutoBattleInput.PressAction(ActionType.kActionJump, 0.12f);
            AutoBattleInput.HoldAction(ActionType.kActionJump, 0.28f);

            if (player.mWeapon != rpg)
            {
                SwitchWeapon(player, rpg, "roam_rpg_jump_switch");
                LastAction = "火箭跳切枪";
                LastStatus = "火箭跳准备";
                return true;
            }

            Vector3 footPoint = player.transform.position + _rocketJumpDir * 0.45f - Vector3.up * 1.2f;
            bool aimReady = LookAtPoint(player, cam, footPoint, LookIntentKind.RocketJump);
            if (aimReady || Time.time > _rocketJumpActiveUntil - 0.20f)
            {
                AutoBattleInput.RequestFire(UnityEngine.Random.Range(0.08f, 0.13f));
                _nextFireTime = Time.time + NextFireDelayForWeapon(WeaponType.kWeaponTypeRPG);
                _nextRocketPokeTime = Time.time + 2.4f;
                _nextRocketJumpTime = Time.time + UnityEngine.Random.Range(8.0f, 13.0f);
                _rocketJumpActiveUntil = 0f;
                _nextWeaponDecisionTime = 0f;
                LastAction = "火箭跳开火";
                LastStatus = "火箭跳巡游";
                FileLogger.Log("AUTO-BATTLE][ROLE", "roam_rpg_jump fired");
                return true;
            }

            LastAction = "火箭跳瞄地";
            LastStatus = "火箭跳校准";
            return true;
        }

        private static void RefreshTarget(Level level, Character player, Camera cam)
        {
            TargetPick best = SelectTargetPick(level, player, cam);
            if (_target == null || !IsUsableTarget(player, _target))
            {
                SwitchTarget(best == null ? null : best.Target, best == null ? null : best.Sense, "acquire");
                return;
            }
            if (IsTargetTemporarilySkipped(_target))
            {
                SwitchTarget(best == null ? null : best.Target, best == null ? null : best.Sense, "route_skip");
                return;
            }

            TargetSense currentSense = best != null && best.Target == _target
                ? best.Sense
                : BuildSense(player, _target, cam);
            if (!currentSense.VisibleByGame)
            {
                SwitchTarget(best == null ? null : best.Target, best == null ? null : best.Sense, "current_hidden");
                return;
            }

            if (currentSense.Visible) _targetLastVisibleAt = Time.time;
            if (best == null || best.Target == _target) return;

            TargetPick current = new TargetPick();
            current.Target = _target;
            current.Sense = currentSense;
            current.Score = ScoreTarget(player, _target, currentSense);

            if (ShouldSwitchTarget(current, best))
                SwitchTarget(best.Target, best.Sense, "score_advantage");
        }

        private static TargetPick SelectTargetPick(Level level, Character player, Camera cam)
        {
            State = AutoBattleState.Acquire;
            CollectCharacters(level);
            int candidateCount = CollectDetailedTargetCandidates(player, cam);
            TargetPick best = null;
            float bestScore = float.MaxValue;
            for (int i = 0; i < candidateCount; i++)
            {
                Character ch = TargetScanCandidates[i];
                if (ch == null || !IsUsableTarget(player, ch)) continue;
                TargetSense sense = BuildSense(player, ch, cam);
                if (!sense.VisibleByGame) continue;
                float score = ScoreTarget(player, ch, sense);
                if (score < bestScore)
                {
                    bestScore = score;
                    best = new TargetPick();
                    best.Target = ch;
                    best.Sense = sense;
                    best.Score = score;
                }
            }
            return best;
        }

        private static int CollectDetailedTargetCandidates(Character player, Camera cam)
        {
            int limit = MaxDetailedTargetCandidates;
            for (int i = 0; i < MaxDetailedTargetCandidates; i++)
            {
                TargetScanCandidates[i] = null;
                TargetScanScores[i] = float.MaxValue;
            }

            int count = 0;
            for (int i = 0; i < Characters.Count; i++)
            {
                Character ch = Characters[i];
                if (!IsUsableTarget(player, ch)) continue;
                if (IsTargetTemporarilySkipped(ch)) continue;
                if (!IsVisibleByGame(player, ch)) continue;
                float score = CheapTargetScore(player, ch, cam);
                int insert = -1;
                for (int j = 0; j < limit; j++)
                {
                    if (score < TargetScanScores[j])
                    {
                        insert = j;
                        break;
                    }
                }
                if (insert < 0) continue;

                for (int j = limit - 1; j > insert; j--)
                {
                    TargetScanCandidates[j] = TargetScanCandidates[j - 1];
                    TargetScanScores[j] = TargetScanScores[j - 1];
                }
                TargetScanCandidates[insert] = ch;
                TargetScanScores[insert] = score;
                if (count < limit) count++;
            }

            if (_target != null && IsUsableTarget(player, _target))
            {
                bool found = false;
                for (int i = 0; i < count; i++)
                {
                    if (TargetScanCandidates[i] == _target)
                    {
                        found = true;
                        break;
                    }
                }
                if (!found)
                {
                    int index = count < limit ? count++ : limit - 1;
                    TargetScanCandidates[index] = _target;
                    TargetScanScores[index] = CheapTargetScore(player, _target, cam);
                }
            }

            return count;
        }

        private static bool IsTargetTemporarilySkipped(Character target)
        {
            if (target == null) return false;
            float until;
            if (!TemporarilySkippedTargets.TryGetValue(target, out until)) return false;
            if (Time.time < until) return true;
            TemporarilySkippedTargets.Remove(target);
            return false;
        }

        private static bool HasAlternativeUsableTarget(Character player, Character current)
        {
            for (int i = 0; i < Characters.Count; i++)
            {
                Character other = Characters[i];
                if (other == null || other == current) continue;
                if (!IsUsableTarget(player, other) || !IsVisibleByGame(player, other)) continue;
                if (!IsTargetTemporarilySkipped(other)) return true;
            }
            return false;
        }

        private static float CheapTargetScore(Character player, Character ch, Camera cam)
        {
            float score = SafeDistance(player, ch) * 0.72f + HealthPercent(ch) * 0.10f;
            if (ch == _target) score -= 12.0f;
            else if (IsInvincibleTarget(ch)) score += 2.0f;

            if (cam != null && ch != null && ch.transform != null)
            {
                try
                {
                    Vector3 screen = cam.WorldToScreenPoint(ch.transform.position + Vector3.up * 1.0f);
                    if (screen.z > 0f)
                    {
                        float dx = screen.x - Screen.width * 0.5f;
                        float dy = screen.y - Screen.height * 0.5f;
                        float centerDistance = Mathf.Sqrt(dx * dx + dy * dy);
                        bool onScreen = screen.x >= -100f && screen.x <= Screen.width + 100f &&
                                        screen.y >= -100f && screen.y <= Screen.height + 100f;
                        score += onScreen ? Mathf.Min(5f, centerDistance * 0.006f) - 4.0f : 2.0f;
                    }
                    else
                    {
                        score += 3.0f;
                    }
                }
                catch
                {
                }
            }
            return score;
        }

        private static float ScoreTarget(Character player, Character ch, TargetSense sense)
        {
            float score = sense.Distance * 0.65f + HealthPercent(ch) * 0.18f;
            if (sense.Visible) score -= 8.0f;
            else if (sense.LineOfSight) score -= 2.0f;
            else score += 3.0f;
            if (sense.OnScreen) score += sense.ScreenDistance * 0.012f;
            if (IsShieldFront(player, ch)) score += 4.0f;
            if (StrategyMode == 1) score -= Mathf.Min(5f, sense.Distance * 0.2f);
            if (StrategyMode == 2 && sense.Distance < 6f) score += 3f;
            return score;
        }

        private static bool ShouldSwitchTarget(TargetPick current, TargetPick best)
        {
            if (best == null || best.Target == null) return false;
            if (current == null || current.Target == null) return true;
            if (!current.Sense.VisibleByGame) return true;

            float lockedFor = Time.time - _targetAcquiredAt;
            float invisibleFor = current.Sense.Visible ? 0f : Time.time - _targetLastVisibleAt;
            float advantage = current.Score - best.Score;

            if (lockedFor < TargetMinimumLockSeconds && current.Sense.Visible) return false;
            if (best.Sense.Visible && !current.Sense.Visible && invisibleFor >= 0.85f && advantage >= 5.0f) return true;
            if (!current.Sense.Visible && invisibleFor >= TargetLostHoldSeconds && advantage >= 3.0f) return true;
            if (best.Sense.Visible && advantage >= TargetSwitchScoreAdvantage) return true;
            if (!current.Sense.Visible && advantage >= TargetSwitchScoreAdvantage + 4.0f) return true;
            return false;
        }

        private static void SwitchTarget(Character next, TargetSense sense, string reason)
        {
            if (next == _target)
            {
                if (sense != null && sense.Visible) _targetLastVisibleAt = Time.time;
                return;
            }

            Character old = _target;
            _target = next;
            ClearCurrentPath();
            _pathIndex = 0;
            _hasDestination = false;
            _wallAheadCount = 0;
            _targetAcquiredAt = next == null ? 0f : Time.time;
            _targetLastVisibleAt = next == null ? 0f : Time.time;
            _lastTargetSwitchAt = Time.time;
            _lastTargetSwitchReason = reason;
            _combatMoveDir = Vector3.zero;
            _nextCombatMoveChangeTime = 0f;
            _nextExposureRefresh = 0f;
            _stableSearchTarget = null;
            _stableSearchPoint = Vector3.zero;
            _stableSearchPointScore = 0f;
            _stableSearchPointHoldUntil = 0f;
            _hasStableSearchPoint = false;
            ResetSearchPointFailures();
            ResetHighGroundReposition();
            ResetAimRuntime();

            if (DebugLog)
            {
                FileLogger.Log("AUTO-BATTLE][AIM", "targetSwitch=1 old=" + SafeTargetName(old) + " new=" + SafeTargetName(next) + " reason=" + reason + " visible=" + (sense != null && sense.Visible ? "1" : "0"));
            }
        }

        private static void ResetAimRuntime()
        {
            _aimJitterTarget = null;
            _aimJitterOffset = Vector3.zero;
            _aimJitterTargetOffset = Vector3.zero;
            _aimJitterLower = 0f;
            _aimJitterTargetLower = 0f;
            _nextAimJitterRefresh = 0f;
            _smoothedAimTarget = null;
            _smoothedAimPoint = Vector3.zero;
            _hasSmoothedAimPoint = false;
            _lookSettlingSince = Time.time;
            _lastAimSettleMs = -1f;
            _lastAimReady = false;
            _lastTargetRouteDelta = -1f;
            _lastLookIntentDelta = -1f;
            _lastLookIntentFrame = -1;
            _occludedSeekTarget = null;
            _occludedSeekYawOffset = 0f;
            _nextOccludedSeekOffsetRefresh = 0f;
        }

        private static Character SelectTarget(Level level, Character player, Camera cam)
        {
            TargetPick pick = SelectTargetPick(level, player, cam);
            return pick == null ? null : pick.Target;
        }

        private static void CollectCharacters(Level level)
        {
            Characters.Clear();
            try
            {
                List<Character> list = level.GetCharacters();
                if (list != null)
                {
                    for (int i = 0; i < list.Count; i++)
                    {
                        if (list[i] != null && !Characters.Contains(list[i])) Characters.Add(list[i]);
                    }
                }
            }
            catch
            {
            }

            try
            {
                if (CharacterManager.Instance != null && CharacterManager.Instance.character_set != null)
                {
                    foreach (Character ch in CharacterManager.Instance.character_set)
                    {
                        if (ch != null && !Characters.Contains(ch)) Characters.Add(ch);
                    }
                }
            }
            catch
            {
            }
        }

        private static bool IsUsableTarget(Character player, Character ch)
        {
            try
            {
                if (player == null || ch == null || ch == player) return false;
                if (ch.IsDied) return false;
                if (ch.GetTeam() == player.GetTeam()) return false;
                if (ch.GetHidden()) return false;
                if (ch.transform == null) return false;
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static bool IsInvincibleTarget(Character ch)
        {
            try
            {
                return ch != null && ch.invincible_time > 0.03f;
            }
            catch
            {
                return false;
            }
        }

        private static TargetSense BuildSense(Character player, Character target, Camera cam)
        {
            TargetSense sense = new TargetSense();
            sense.Target = target;
            sense.Invincible = IsInvincibleTarget(target);
            sense.Distance = SafeDistance(player, target);
            sense.AimPoint = GetStableAimPoint(target);
            sense.FirePoint = sense.AimPoint;
            sense.ScreenDistance = 99999f;
            sense.OnScreen = false;
            sense.VisibleByGame = IsVisibleByGame(player, target);
            sense.LineOfSight = HasLineOfSight(cam, player, target, sense.AimPoint);
            Vector3 firePoint;
            sense.StrictFireLineOfSight = HasFireLineOfSight(cam, player, target, sense.AimPoint, out firePoint);
            sense.FireLineOfSight = sense.StrictFireLineOfSight;
            if (sense.FireLineOfSight) sense.FirePoint = firePoint;

            if (cam != null)
            {
                UpdateScreenSense(cam, sense, sense.AimPoint);
                UpdateScreenSense(cam, sense, GetTargetBodyPoint(target, 0.95f));
                UpdateScreenSense(cam, sense, GetTargetBodyPoint(target, 0.45f));
            }

            try
            {
                sense.HeightDelta = target.transform.position.y - player.transform.position.y;
            }
            catch
            {
                sense.HeightDelta = 0f;
            }
            sense.HighGroundBlocked = sense.VisibleByGame &&
                                      sense.HeightDelta >= HighGroundMinHeight &&
                                      sense.Distance <= HighGroundMaxDistance &&
                                      !sense.StrictFireLineOfSight &&
                                      (sense.LineOfSight || sense.OnScreen);

            float closeScreenLimit = Mathf.Min(Screen.width, Screen.height) * 0.46f;
            bool closeScreenVisible = sense.Distance <= CloseEngageDistance && sense.OnScreen && sense.ScreenDistance <= closeScreenLimit;
            bool closeProximityVisible = sense.Distance <= ForceCloseEngageDistance && sense.VisibleByGame && sense.FireLineOfSight;
            sense.CloseVisible = closeScreenVisible;
            sense.Visible = sense.VisibleByGame && (((sense.LineOfSight || sense.FireLineOfSight) && sense.OnScreen) ||
                            (closeScreenVisible && sense.FireLineOfSight) ||
                            closeProximityVisible);
            return sense;
        }

        private static void UpdateScreenSense(Camera cam, TargetSense sense, Vector3 point)
        {
            try
            {
                Vector3 sp = cam.WorldToScreenPoint(point);
                if (sp.z <= 0f) return;
                float margin = 140f;
                bool onScreen = sp.x >= -margin && sp.x <= Screen.width + margin &&
                                sp.y >= -margin && sp.y <= Screen.height + margin;
                float dx = sp.x - Screen.width * 0.5f;
                float dy = sp.y - Screen.height * 0.5f;
                float screenDistance = Mathf.Sqrt(dx * dx + dy * dy);
                if (onScreen) sense.OnScreen = true;
                if (screenDistance < sense.ScreenDistance) sense.ScreenDistance = screenDistance;
            }
            catch
            {
            }
        }

        private static bool IsVisibleByGame(Character player, Character target)
        {
            try
            {
                if (!target.GetHidden()) return true;
                if (SafeDistance(player, target) <= CloseStealthVisibleDistance) return true;
                return player != null && player.SeeEffect(target) >= 0.99f;
            }
            catch
            {
                return false;
            }
        }

        private static bool HasLineOfSight(Camera cam, Character player, Character target, Vector3 targetPoint)
        {
            Vector3 origin = Vector3.zero;
            if (cam != null)
            {
                origin = cam.transform.position;
            }
            else if (player != null && player.transform != null)
            {
                origin = player.transform.position + Vector3.up * 1.2f;
            }
            else
            {
                return false;
            }

            Transform ignoreRoot = null;
            try
            {
                ignoreRoot = player != null && player.transform != null ? player.transform.root : null;
            }
            catch
            {
                ignoreRoot = null;
            }

            if (HasClearSegment(origin, targetPoint, target, ignoreRoot)) return true;
            if (HasClearSegment(origin, GetTargetBodyPoint(target, 0.95f), target, ignoreRoot)) return true;
            if (HasClearSegment(origin, GetTargetBodyPoint(target, 0.45f), target, ignoreRoot)) return true;
            return false;
        }

        private static bool HasFireLineOfSight(Camera cam, Character player, Character target, Vector3 targetPoint)
        {
            Vector3 firePoint;
            return HasFireLineOfSight(cam, player, target, targetPoint, out firePoint);
        }

        private static bool HasFireLineOfSight(Camera cam, Character player, Character target, Vector3 targetPoint, out Vector3 firePoint)
        {
            firePoint = targetPoint;
            Vector3 origin = Vector3.zero;
            if (cam != null)
            {
                origin = cam.transform.position;
            }
            else if (player != null && player.transform != null)
            {
                origin = player.transform.position + Vector3.up * 1.2f;
            }
            else
            {
                return false;
            }

            Transform ignoreRoot = SafeRoot(player);
            Vector3 upper = GetTargetBodyPoint(target, 1.18f);
            Vector3 chest = GetTargetBodyPoint(target, 0.95f);
            Vector3 mid = GetTargetBodyPoint(target, 0.72f);
            if (!TryFindClearFirePoint(origin, target, ignoreRoot, out firePoint, targetPoint, upper, chest, mid))
                return false;

            if (player != null && player.transform != null)
            {
                Vector3 eye = player.transform.position + Vector3.up * 1.15f;
                if ((eye - origin).sqrMagnitude > 0.25f &&
                    !HasClearSegment(eye, firePoint, target, ignoreRoot) &&
                    !TryFindClearFirePoint(eye, target, ignoreRoot, out firePoint, upper, chest, mid))
                    return false;
            }

            return true;
        }

        private static bool TryFindClearFirePoint(Vector3 origin, Character target, Transform ignoreRoot, out Vector3 firePoint, params Vector3[] points)
        {
            firePoint = Vector3.zero;
            if (points == null) return false;
            for (int i = 0; i < points.Length; i++)
            {
                Vector3 point = points[i];
                if (HasClearSegment(origin, point, target, ignoreRoot))
                {
                    firePoint = point;
                    return true;
                }
            }
            return false;
        }

        private static bool HasClearSegment(Vector3 origin, Vector3 targetPoint, Character expectedTarget)
        {
            return HasClearSegment(origin, targetPoint, expectedTarget, null);
        }

        private static bool HasClearSegment(Vector3 origin, Vector3 targetPoint, Character expectedTarget, Transform ignoreRoot)
        {
            Vector3 dir = targetPoint - origin;
            float dist = dir.magnitude;
            if (dist <= 0.05f) return true;
            dir /= dist;

            int mask = LayerMask.GetMask(new string[] { "kPlayer", "Terrarin", "kController", "Weapon" });
            RaycastHit[] hits = mask != 0
                ? Physics.RaycastAll(origin, dir, dist + 0.15f, mask)
                : Physics.RaycastAll(origin, dir, dist + 0.15f);
            if (hits == null || hits.Length == 0) return true;

            Array.Sort(hits, CompareRaycastHitDistance);
            for (int i = 0; i < hits.Length; i++)
            {
                Transform hitTransform = hits[i].transform;
                if (ShouldIgnoreHit(hitTransform, ignoreRoot)) continue;
                if (expectedTarget != null && IsHitExpectedTarget(hitTransform, expectedTarget)) return true;
                return false;
            }

            return true;
        }

        private static int CompareRaycastHitDistance(RaycastHit a, RaycastHit b)
        {
            return a.distance.CompareTo(b.distance);
        }

        private static bool ShouldIgnoreHit(Transform hitTransform, Transform ignoreRoot)
        {
            if (hitTransform == null || ignoreRoot == null) return false;
            try
            {
                Transform root = hitTransform.root;
                if (root != null && root == ignoreRoot) return true;
                Transform t = hitTransform;
                while (t != null)
                {
                    if (t == ignoreRoot) return true;
                    t = t.parent;
                }
            }
            catch
            {
            }
            return false;
        }

        private static bool IsHitExpectedTarget(Transform hitTransform, Character expectedTarget)
        {
            try
            {
                if (hitTransform == null || expectedTarget == null || expectedTarget.transform == null) return false;
                Transform targetRoot = expectedTarget.transform.root;
                Transform root = hitTransform.root;
                if (root != null && targetRoot != null && root == targetRoot) return true;

                string expectedName = expectedTarget.baseName;
                if (!string.IsNullOrEmpty(expectedName))
                {
                    if (root != null && root.name == expectedName) return true;
                    Transform t = hitTransform;
                    while (t != null)
                    {
                        if (t.name == expectedName) return true;
                        t = t.parent;
                    }
                }
            }
            catch
            {
            }

            return false;
        }

        private static Vector3 GetStableAimPoint(Character target)
        {
            Vector3 basePoint = target != null && target.transform != null ? target.transform.position : Vector3.zero;
            try
            {
                Transform head = target.getBone("web__head").transform;
                if (head != null) basePoint = head.position;
            }
            catch
            {
                basePoint += Vector3.up * 1.2f;
            }

            return basePoint;
        }

        private static Vector3 GetTargetBodyPoint(Character target, float height)
        {
            try
            {
                if (target == null || target.transform == null) return Vector3.zero;
                return target.transform.position + Vector3.up * height;
            }
            catch
            {
                return Vector3.zero;
            }
        }

        private static Vector3 GetAimPoint(Character target, Camera cam)
        {
            return GetAimPoint(target, cam, GetStableAimPoint(target), true);
        }

        private static Vector3 GetAimPoint(Character target, Camera cam, Vector3 basePoint, bool allowLower)
        {
            if (_aimJitterTarget != target || Time.time >= _nextAimJitterRefresh)
            {
                int accuracy = Mathf.Clamp(AccuracyMode, 0, AccuracyNames.Length - 1);
                if (_aimJitterTarget != target)
                {
                    _aimJitterTarget = target;
                    _aimJitterOffset = Vector3.zero;
                    _aimJitterTargetOffset = Vector3.zero;
                    _aimJitterLower = 0f;
                    _aimJitterTargetLower = 0f;
                }

                float jitter = accuracy == 0 ? 0.030f : (accuracy == 1 ? 0.055f : 0.085f);
                float lowerChance = accuracy == 0 ? 0.10f : (accuracy == 1 ? 0.22f : 0.34f);
                Vector3 right = cam != null ? cam.transform.right : Vector3.right;
                _aimJitterTargetLower = allowLower && UnityEngine.Random.value < lowerChance ? UnityEngine.Random.Range(0.06f, 0.22f) : 0f;
                _aimJitterTargetOffset = right * UnityEngine.Random.Range(-jitter, jitter);
                _aimJitterTargetOffset += Vector3.up * UnityEngine.Random.Range(-jitter * 0.65f, jitter * 0.65f);
                _nextAimJitterRefresh = Time.time + UnityEngine.Random.Range(1.45f, 2.85f);
            }

            float blend = Mathf.Clamp01(Time.deltaTime * 1.65f);
            _aimJitterOffset = Vector3.Lerp(_aimJitterOffset, _aimJitterTargetOffset, blend);
            _aimJitterLower = Mathf.Lerp(_aimJitterLower, _aimJitterTargetLower, blend);
            if (allowLower) basePoint.y -= _aimJitterLower;
            return basePoint + _aimJitterOffset;
        }

        private static bool IsShieldFront(Character player, Character target)
        {
            try
            {
                if (player == null || target == null || target.mWeapon == null || target.mWeapon.name == null) return false;
                if (target.mWeapon.name.IndexOf("shield", StringComparison.OrdinalIgnoreCase) < 0) return false;
                return target.CalculateHitDirection(player.transform.position) == Character.DIRECTION.kFront;
            }
            catch
            {
                return false;
            }
        }

        private static bool ShouldKite(Character player, TargetSense sense)
        {
            if (sense == null || player == null) return false;
            if (sense.FireLineOfSight && sense.Distance <= 10.5f) return false;
            if (StrategyMode == 2 && sense.Distance < 8.0f) return true;
            if (StrategyMode == 0 && sense.Distance < 3.8f) return true;
            try
            {
                int maxHp = player.character_info != null ? player.character_info.max_health : 0;
                if (maxHp > 0 && player.hp * 100 / maxHp <= 35 && sense.Distance < 9f) return true;
            }
            catch
            {
            }
            return false;
        }

        private static bool ShouldTacticalAvoid(Character player, TargetSense sense, int exposure, int aimingThreats)
        {
            if (player == null || sense == null || !sense.Visible) return false;
            if (sense.FireLineOfSight && sense.Distance <= 10.5f) return false;

            float hpPct = HealthPercent(player);
            if (hpPct > 45f) return false;
            if (aimingThreats >= 2 && sense.Distance > 9.5f) return true;
            if (hpPct <= 28f && aimingThreats >= 1 && sense.Distance > 8.0f) return true;
            if (hpPct <= 35f && exposure >= 4 && sense.Distance > 11.0f) return true;
            return false;
        }

        private static bool ShouldRunHighGroundReposition(Character player, Character target, TargetSense sense)
        {
            if (player == null || target == null || sense == null)
            {
                ResetHighGroundReposition();
                return false;
            }

            if (_highGroundRepositionTarget != target)
            {
                ResetHighGroundReposition();
                _highGroundRepositionTarget = target;
            }

            if (sense.HighGroundBlocked)
            {
                if (_highGroundBlockedSince <= 0f) _highGroundBlockedSince = Time.time;
                _highGroundClearSince = 0f;
                if (!_highGroundRepositionActive && Time.time - _highGroundBlockedSince >= HighGroundDetectDelay)
                {
                    _highGroundRepositionActive = true;
                    _highGroundRepositionPoint = Vector3.zero;
                    _nextHighGroundPointRefresh = 0f;
                    _highGroundSearchStartedAt = Time.time;
                    _highGroundLastProgressAt = Time.time;
                    _highGroundLastProgressPosition = player.transform.position;
                    _highGroundTrackedTargetPosition = target.transform.position;
                    _nextHighGroundGlanceTime = Time.time + UnityEngine.Random.Range(2.2f, 3.4f);
                    ClearCurrentPath();
                    _pathIndex = 0;
                    _hasDestination = false;
                    _nextRepath = 0f;
                    FileLogger.Log("AUTO-BATTLE][HIGHGROUND",
                        "enter target=" + SafeTargetName(target) +
                        " height=" + sense.HeightDelta.ToString("0.0") +
                        " dist=" + sense.Distance.ToString("0.0"));
                }
                return _highGroundRepositionActive;
            }

            _highGroundBlockedSince = 0f;
            if (!_highGroundRepositionActive) return false;

            bool stillRelevant = sense.VisibleByGame && sense.Distance <= HighGroundMaxDistance + 4.0f;
            if (!stillRelevant)
            {
                ResetHighGroundReposition();
                return false;
            }

            if (!sense.StrictFireLineOfSight)
            {
                _highGroundClearSince = 0f;
                return true;
            }

            if (_highGroundClearSince <= 0f) _highGroundClearSince = Time.time;
            if (Time.time - _highGroundClearSince < HighGroundClearHold) return true;

            FileLogger.Log("AUTO-BATTLE][HIGHGROUND",
                "clear target=" + SafeTargetName(target) +
                " height=" + sense.HeightDelta.ToString("0.0") +
                " dist=" + sense.Distance.ToString("0.0"));
            ResetHighGroundReposition();
            ClearCurrentPath();
            _pathIndex = 0;
            _hasDestination = false;
            _nextRepath = 0f;
            return false;
        }

        private static void RunHighGroundReposition(Character player, Character target, Camera cam, TargetSense sense)
        {
            State = AutoBattleState.RouteToEngage;
            LastAction = "高台换位";

            Vector3 playerPos = player.transform.position;
            if (XZDistanceSq(playerPos, _highGroundLastProgressPosition) >= 0.10f)
            {
                _highGroundLastProgressPosition = playerPos;
                _highGroundLastProgressAt = Time.time;
            }

            bool targetMoved = Time.time - _highGroundPointSelectedAt > 2.5f &&
                               (XZDistanceSq(_highGroundTrackedTargetPosition, target.transform.position) > 64.0f ||
                                Mathf.Abs(_highGroundTrackedTargetPosition.y - target.transform.position.y) > 2.5f);
            bool reachedBlockedPoint = _highGroundRepositionPoint != Vector3.zero &&
                                       XZDistanceSq(playerPos, _highGroundRepositionPoint) < 1.55f &&
                                       !sense.StrictFireLineOfSight;
            bool pathFailed = _highGroundRepositionPoint != Vector3.zero &&
                              Time.time - _highGroundPointSelectedAt > 0.30f &&
                              Path.Count == 0 && !_pathSearchPending && LastPath.StartsWith("no_path");
            bool noProgress = _highGroundRepositionPoint != Vector3.zero &&
                              !_pathSearchPending &&
                              XZDistanceSq(playerPos, _highGroundRepositionPoint) > 2.25f &&
                              Time.time - _highGroundLastProgressAt > 2.20f;

            if (targetMoved || reachedBlockedPoint || pathFailed || noProgress)
            {
                MarkHighGroundCandidateFailed(targetMoved ? "target_moved" : reachedBlockedPoint ? "arrived_blocked" : pathFailed ? "path_failed" : "no_progress");
                _highGroundTrackedTargetPosition = target.transform.position;
            }

            bool hasActiveRoute = Path.Count > 0 && _pathIndex >= 0 && _pathIndex < Path.Count;
            if (!_pathSearchPending && !hasActiveRoute && Time.time - _highGroundLastProgressAt > 2.20f &&
                Time.time - _highGroundSearchStartedAt >= 6.0f &&
                (_highGroundFailureCount >= 2 || LastPath.StartsWith("no_path")))
            {
                if (HasAlternativeUsableTarget(player, target))
                {
                    TemporarilySkippedTargets[target] = Time.time + 3.0f;
                    FileLogger.Log("AUTO-BATTLE][HIGHGROUND", "unreachable=skip target=" + SafeTargetName(target) + " seconds=3 failedSectors=0x" + _highGroundFailedSectorMask.ToString("X"));
                    SwitchTarget(null, null, "highground_unreachable");
                    _nextTargetRefresh = 0f;
                    return;
                }

                FileLogger.Log("AUTO-BATTLE][HIGHGROUND", "unreachable=single_explore target=" + SafeTargetName(target) + " failedSectors=0x" + _highGroundFailedSectorMask.ToString("X"));
                _highGroundSearchStartedAt = Time.time;
                _highGroundFailedSectorMask = 0;
                _highGroundFailureCount = 0;
                _highGroundCandidateCursor += 2;
                _highGroundSelectedSector = -1;
                MarkHighGroundCandidateFailed("single_explore_rotate");
            }

            bool refreshPoint = _highGroundRepositionPoint == Vector3.zero && Time.time >= _nextHighGroundPointRefresh;
            if (refreshPoint)
            {
                _highGroundRepositionPoint = SelectHighGroundRepositionPoint(player, target, sense);
                _highGroundPointSelectedAt = Time.time;
                _highGroundLastProgressAt = Time.time;
                _highGroundLastProgressPosition = playerPos;
                _nextHighGroundPointRefresh = Time.time + 0.18f;
                ClearCurrentPath();
                _pathIndex = 0;
                _hasDestination = false;
                _nextRepath = 0f;
            }

            Vector3 moveDir = _highGroundRepositionPoint == Vector3.zero
                ? Vector3.zero
                : UpdateNavigation(player, _highGroundRepositionPoint, sense, true, true);
            // Do not walk straight into cover while the incremental 2.5D search is still running.
            // Moving here also changes the search origin and can restart the job every frame.
            if (moveDir.sqrMagnitude < 0.01f && !_pathSearchPending)
            {
                moveDir = _highGroundRepositionPoint - player.transform.position;
                moveDir.y = 0f;
                if (moveDir.sqrMagnitude > 0.01f)
                {
                    moveDir.Normalize();
                    if (AutoBattleRoutePlanner.HasForwardBlock(player.transform.position, moveDir, SafeRoot(player)))
                    {
                        Vector3 left = Quaternion.AngleAxis(58f, Vector3.up) * moveDir;
                        Vector3 right = Quaternion.AngleAxis(-58f, Vector3.up) * moveDir;
                        bool leftBlocked = AutoBattleRoutePlanner.HasForwardBlock(player.transform.position, left, SafeRoot(player));
                        bool rightBlocked = AutoBattleRoutePlanner.HasForwardBlock(player.transform.position, right, SafeRoot(player));
                        if (!leftBlocked && !rightBlocked) moveDir = (_wallAheadCount++ % 2 == 0) ? left : right;
                        else if (!leftBlocked) moveDir = left;
                        else if (!rightBlocked) moveDir = right;
                        else moveDir = Vector3.zero;
                    }
                }
            }

            if (moveDir.sqrMagnitude > 0.01f) AutoBattleInput.SetMoveWorld(player, moveDir, false);
            else AutoBattleInput.ClearMovement();

            bool glance;
            Vector3 lookPoint = SelectHighGroundRouteLookPoint(player, target, moveDir, sense.StrictFireLineOfSight, out glance);
            LookAtPoint(player, cam, lookPoint, glance ? LookIntentKind.Glance : LookIntentKind.Route);
            _lastFireBlock = sense.StrictFireLineOfSight ? "high_ground_los_stabilizing" : "high_ground_cover";

            LastStatus = "高台目标换位 | 高差 " + sense.HeightDelta.ToString("0.0") +
                         " | 距离 " + sense.Distance.ToString("0.0") +
                         " | 弹道 " + (sense.StrictFireLineOfSight ? "通" : "阻") +
                         " | 路径 " + LastPath;
        }

        private static Vector3 SelectHighGroundRepositionPoint(Character player, Character target, TargetSense sense)
        {
            Vector3 playerPos = player.transform.position;
            Vector3 targetGround = NormalizeNavigationDestination(player, target.transform.position, true);
            Vector3 away = playerPos - targetGround;
            away.y = 0f;
            if (away.sqrMagnitude < 0.16f) away = -player.transform.forward;
            away.y = 0f;
            if (away.sqrMagnitude < 0.01f) away = Vector3.back;
            away.Normalize();

            Vector3 side = Vector3.Cross(Vector3.up, away);
            if (side.sqrMagnitude < 0.01f) side = player.transform.right;
            side.y = 0f;
            if (side.sqrMagnitude < 0.01f) side = Vector3.right;
            side.Normalize();

            float desiredRange = Mathf.Clamp(8.5f + Mathf.Max(0f, sense.HeightDelta) * 1.25f, 9.0f, 13.5f);
            CandidatePoints.Clear();
            float[] upperAngles = { 0f, 45f, -45f, 90f, -90f, 135f, -135f, 180f };
            for (int i = 0; i < upperAngles.Length; i++)
            {
                float radius = i < 4 ? 2.4f : 4.2f;
                Vector3 dir = Quaternion.AngleAxis(upperAngles[i], Vector3.up) * away;
                CandidatePoints.Add(targetGround + dir * radius);
            }

            float[] angles = { 0f, 28f, -28f, 52f, -52f, 78f, -78f };
            for (int i = 0; i < angles.Length; i++)
            {
                Vector3 dir = Quaternion.AngleAxis(angles[i], Vector3.up) * away;
                CandidatePoints.Add(targetGround + dir * desiredRange);
            }
            CandidatePoints.Add(playerPos + away * 4.8f);
            CandidatePoints.Add(playerPos + away * 3.6f + side * 2.8f);
            CandidatePoints.Add(playerPos + away * 3.6f - side * 2.8f);

            Vector3 bestUpper = Vector3.zero;
            Vector3 bestLowerLanePoint = Vector3.zero;
            Vector3 bestLowerProbe = Vector3.zero;
            Vector3 bestLowerFallback = Vector3.zero;
            float bestUpperScore = float.MaxValue;
            float bestLowerLaneScore = float.MaxValue;
            float bestLowerProbeScore = float.MaxValue;
            float bestLowerFallbackScore = float.MaxValue;
            bool bestUpperLane = false;
            int bestUpperSector = -1;
            int bestLowerLaneSector = -1;
            int bestLowerProbeSector = -1;
            int bestLowerFallbackSector = -1;
            float bestLowerLaneGain = 0f;
            float bestLowerProbeGain = 0f;
            float bestLowerFallbackGain = 0f;
            int lowerFailedCount = 0;
            for (int i = upperAngles.Length; i < CandidatePoints.Count && i < 31; i++)
            {
                if ((_highGroundFailedSectorMask & (1 << i)) != 0) lowerFailedCount++;
            }

            for (int i = 0; i < CandidatePoints.Count; i++)
            {
                int sector = i;
                if (sector < 31 && (_highGroundFailedSectorMask & (1 << sector)) != 0) continue;
                bool upperCandidate = i < upperAngles.Length;
                Vector3 p = NormalizeNavigationDestination(player, CandidatePoints[i], upperCandidate);
                if (upperCandidate && Mathf.Abs(p.y - targetGround.y) > 1.15f) continue;
                float routePenalty = AutoBattleRoutePlanner.CandidatePenalty(playerPos, p, SafeRoot(player));
                if (routePenalty >= 200f) continue;
                if (upperCandidate) routePenalty = Mathf.Min(routePenalty, 28.0f);

                float targetRange = Mathf.Sqrt(XZDistanceSq(p, targetGround));
                float playerDistance = Mathf.Sqrt(XZDistanceSq(playerPos, p));
                float candidateRange = upperCandidate ? (i < 4 ? 2.4f : 4.2f) : desiredRange;
                float angleGain = 0f;
                if (!upperCandidate)
                {
                    Vector3 radial = p - targetGround;
                    radial.y = 0f;
                    if (radial.sqrMagnitude > 0.01f) angleGain = Vector3.Angle(away, radial.normalized);
                }
                float score = XZDistanceSq(playerPos, p) * 0.38f;
                score += Mathf.Abs(targetRange - candidateRange) * 4.0f;
                score += routePenalty * 2.2f;
                if (upperCandidate)
                {
                    score -= 55.0f;
                    score += Mathf.Abs(p.y - targetGround.y) * 18.0f;
                }
                else if (targetRange < 7.0f)
                {
                    score += (7.0f - targetRange) * 30.0f;
                }

                Vector3 candidateFirePoint;
                bool clearLane = HasCandidateFireLane(p, player, target, sense.AimPoint, out candidateFirePoint);
                if (playerDistance < 2.0f && !sense.StrictFireLineOfSight) continue;
                score += clearLane ? -120f : 45f;
                if (!upperCandidate)
                    score -= Mathf.Min(angleGain, 90.0f) * (clearLane ? 0.20f : 0.70f);
                score += CountExposureAtPoint(p, player, target, false) * 8.0f;
                score += ((i - _highGroundCandidateCursor + CandidatePoints.Count) % CandidatePoints.Count) * 0.18f;

                if (upperCandidate && score < bestUpperScore)
                {
                    bestUpperScore = score;
                    bestUpper = p;
                    bestUpperLane = clearLane;
                    bestUpperSector = sector;
                }
                else if (!upperCandidate)
                {
                    if (score < bestLowerFallbackScore)
                    {
                        bestLowerFallbackScore = score;
                        bestLowerFallback = p;
                        bestLowerFallbackSector = sector;
                        bestLowerFallbackGain = angleGain;
                    }
                    if (clearLane && score < bestLowerLaneScore)
                    {
                        bestLowerLaneScore = score;
                        bestLowerLanePoint = p;
                        bestLowerLaneSector = sector;
                        bestLowerLaneGain = angleGain;
                    }
                    else if (!clearLane && angleGain >= HighGroundLowerProbeMinAngle && score < bestLowerProbeScore)
                    {
                        bestLowerProbeScore = score;
                        bestLowerProbe = p;
                        bestLowerProbeSector = sector;
                        bestLowerProbeGain = angleGain;
                    }
                }
            }

            bool useLowerLane = bestLowerLanePoint != Vector3.zero;
            bool useLowerProbe = !useLowerLane && bestLowerProbe != Vector3.zero &&
                                 lowerFailedCount < HighGroundLowerProbeLimit;
            bool usingUpper = !useLowerLane && !useLowerProbe && bestUpper != Vector3.zero;
            Vector3 best;
            float bestScore;
            bool bestLane;
            float selectedAngleGain;
            string tier;
            if (useLowerLane)
            {
                best = bestLowerLanePoint;
                bestScore = bestLowerLaneScore;
                bestLane = true;
                selectedAngleGain = bestLowerLaneGain;
                _highGroundSelectedSector = bestLowerLaneSector;
                tier = "lower_lane";
            }
            else if (useLowerProbe)
            {
                best = bestLowerProbe;
                bestScore = bestLowerProbeScore;
                bestLane = false;
                selectedAngleGain = bestLowerProbeGain;
                _highGroundSelectedSector = bestLowerProbeSector;
                tier = "lower_probe";
            }
            else if (usingUpper)
            {
                best = bestUpper;
                bestScore = bestUpperScore;
                bestLane = bestUpperLane;
                selectedAngleGain = 0f;
                _highGroundSelectedSector = bestUpperSector;
                tier = "upper";
            }
            else
            {
                best = bestLowerFallback;
                bestScore = bestLowerFallbackScore;
                bestLane = false;
                selectedAngleGain = bestLowerFallbackGain;
                _highGroundSelectedSector = bestLowerFallbackSector;
                tier = "lower_fallback";
            }

            if (best == Vector3.zero)
            {
                _highGroundFailedSectorMask = 0;
                _highGroundSelectedSector = -1;
                float sign = (_highGroundCandidateCursor & 1) == 0 ? 1f : -1f;
                best = NormalizeNavigationDestination(player, playerPos + away * 4.5f + side * sign * 4.0f, false);
                tier = "explore";
                selectedAngleGain = 0f;
            }

            FileLogger.Log("AUTO-BATTLE][HIGHGROUND",
                "point target=" + SafeTargetName(target) +
                " height=" + sense.HeightDelta.ToString("0.0") +
                " desiredRange=" + desiredRange.ToString("0.0") +
                " tier=" + tier +
                " lane=" + (bestLane ? "1" : "0") +
                " angleGain=" + selectedAngleGain.ToString("0.0") +
                " lowerFails=" + lowerFailedCount +
                " candidateY=" + best.y.ToString("0.0") +
                " destDist=" + Mathf.Sqrt(XZDistanceSq(playerPos, best)).ToString("0.0") +
                " sector=" + _highGroundSelectedSector +
                " failed=0x" + _highGroundFailedSectorMask.ToString("X") +
                " score=" + (bestScore == float.MaxValue ? "fallback" : bestScore.ToString("0.0")) +
                " dest=" + FormatVec(best));
            return best;
        }

        private static void MarkHighGroundCandidateFailed(string reason)
        {
            if (_highGroundSelectedSector >= 0 && _highGroundSelectedSector < 31)
                _highGroundFailedSectorMask |= 1 << _highGroundSelectedSector;
            _highGroundFailureCount++;
            _highGroundCandidateCursor++;
            FileLogger.Log("AUTO-BATTLE][HIGHGROUND", "candidate_failed reason=" + reason + " sector=" + _highGroundSelectedSector + " failed=0x" + _highGroundFailedSectorMask.ToString("X") + " path=" + LastPath);
            _highGroundSelectedSector = -1;
            _highGroundRepositionPoint = Vector3.zero;
            _nextHighGroundPointRefresh = Time.time + 0.12f;
            ClearCurrentPath();
            _pathIndex = 0;
            _hasDestination = false;
            _nextRepath = 0f;
        }

        private static Vector3 SelectHighGroundRouteLookPoint(Character player, Character target, Vector3 moveDir, bool strictFireLine, out bool glance)
        {
            glance = false;
            Vector3 origin = player.transform.position;
            Vector3 routeDir = GetPathLookDirection(player, moveDir);
            routeDir.y = 0f;
            if (routeDir.sqrMagnitude < 0.01f) routeDir = player.transform.forward;
            routeDir.y = 0f;
            if (routeDir.sqrMagnitude < 0.01f) routeDir = Vector3.forward;
            routeDir.Normalize();

            if (!strictFireLine && Time.time >= _nextHighGroundGlanceTime && Time.time >= _highGroundGlanceUntil)
            {
                _highGroundGlanceUntil = Time.time + UnityEngine.Random.Range(0.35f, 0.55f);
                _nextHighGroundGlanceTime = _highGroundGlanceUntil + UnityEngine.Random.Range(2.2f, 3.4f);
                _highGroundGlanceSign = UnityEngine.Random.value < 0.5f ? -1f : 1f;
            }

            if (!strictFireLine && Time.time < _highGroundGlanceUntil)
            {
                Vector3 targetDir = target.transform.position - origin;
                targetDir.y = 0f;
                if (targetDir.sqrMagnitude > 0.01f)
                {
                    targetDir.Normalize();
                    float targetDelta = SignedXZAngle(routeDir, targetDir);
                    float turn = Mathf.Clamp(targetDelta, -25f, 25f);
                    float remaining = targetDelta - turn;
                    if (Mathf.Abs(remaining) < 8f)
                    {
                        float sign = Mathf.Abs(targetDelta) > 0.5f ? Mathf.Sign(targetDelta) : _highGroundGlanceSign;
                        turn -= sign * (8f - Mathf.Abs(remaining));
                        turn = Mathf.Clamp(turn, -25f, 25f);
                    }
                    routeDir = Quaternion.AngleAxis(turn, Vector3.up) * routeDir;
                    glance = true;
                }
            }

            return origin + routeDir.normalized * 7.0f + Vector3.up * 1.05f;
        }

        private static bool HasCandidateFireLane(Vector3 playerPosition, Character player, Character target, Vector3 aimPoint, out Vector3 firePoint)
        {
            Vector3 origin = playerPosition + Vector3.up * 1.15f;
            Transform ignoreRoot = SafeRoot(player);

            Vector3 upper = GetTargetBodyPoint(target, 1.18f);
            Vector3 chest = GetTargetBodyPoint(target, 0.95f);
            Vector3 mid = GetTargetBodyPoint(target, 0.72f);
            return TryFindClearFirePoint(origin, target, ignoreRoot, out firePoint, aimPoint, upper, chest, mid);
        }

        private static void ResetHighGroundReposition()
        {
            _highGroundRepositionTarget = null;
            _highGroundRepositionPoint = Vector3.zero;
            _highGroundBlockedSince = 0f;
            _highGroundClearSince = 0f;
            _nextHighGroundPointRefresh = 0f;
            _highGroundRepositionActive = false;
            _highGroundSearchStartedAt = 0f;
            _highGroundPointSelectedAt = 0f;
            _highGroundLastProgressAt = 0f;
            _highGroundLastProgressPosition = Vector3.zero;
            _highGroundCandidateCursor = 0;
            _highGroundSelectedSector = -1;
            _highGroundFailedSectorMask = 0;
            _highGroundFailureCount = 0;
            _highGroundTrackedTargetPosition = Vector3.zero;
            _nextHighGroundGlanceTime = 0f;
            _highGroundGlanceUntil = 0f;
            _highGroundGlanceSign = 0f;
        }

        private static bool TrySwitchOffShieldTarget(Level level, Character player, Camera cam)
        {
            if (level == null || player == null || cam == null) return false;
            CollectCharacters(level);

            TargetPick best = null;
            float bestScore = float.MaxValue;
            for (int i = 0; i < Characters.Count; i++)
            {
                Character ch = Characters[i];
                if (!IsUsableTarget(player, ch) || ch == _target) continue;
                TargetSense s = BuildSense(player, ch, cam);
                if (!s.VisibleByGame) continue;
                if (IsShieldFront(player, ch)) continue;
                float score = ScoreTarget(player, ch, s);
                if (s.Visible && s.FireLineOfSight) score -= 14f;
                if (score < bestScore)
                {
                    bestScore = score;
                    best = new TargetPick();
                    best.Target = ch;
                    best.Sense = s;
                    best.Score = score;
                }
            }

            if (best == null) return false;
            SwitchTarget(best.Target, best.Sense, "shield_skip");
            return true;
        }

        private static Vector3 SelectSeekLookPoint(Character player, Character target, Vector3 searchPoint, Vector3 moveDir, TargetSense sense)
        {
            try
            {
                if (player == null || player.transform == null || target == null || target.transform == null)
                    return searchPoint + Vector3.up * 1.0f;

                Vector3 origin = player.transform.position;
                Vector3 toTarget = target.transform.position - origin;
                toTarget.y = 0f;
                if (toTarget.sqrMagnitude < 0.01f)
                    return searchPoint + Vector3.up * 1.0f;
                toTarget.Normalize();

                if (sense != null && sense.StrictFireLineOfSight)
                {
                    _lastTargetRouteDelta = 0f;
                    _lastLookIntentDelta = 0f;
                    _lastLookIntentFrame = Time.frameCount;
                    return origin + toTarget * 7.0f + Vector3.up * 1.05f;
                }

                Vector3 routeDir = GetPathLookDirection(player, moveDir);
                if (routeDir.sqrMagnitude < 0.01f) routeDir = toTarget;
                routeDir.y = 0f;
                routeDir.Normalize();

                float routeDelta = SignedXZAngle(toTarget, routeDir);
                float maxDeflection = sense != null && sense.Distance <= 12.0f ? 18.0f : SeekRouteMaxDeflection;
                float clampedDelta = Mathf.Clamp(routeDelta, -maxDeflection, maxDeflection);
                if (sense == null || !sense.StrictFireLineOfSight)
                {
                    const float minimumOccludedOffset = 7.5f;
                    if (Mathf.Abs(clampedDelta) < minimumOccludedOffset)
                    {
                        float offset = GetOccludedSeekYawOffset(target);
                        if (Mathf.Abs(routeDelta) >= 1.0f)
                            offset = Mathf.Sign(routeDelta) * Mathf.Abs(offset);
                        clampedDelta = Mathf.Clamp(offset, -maxDeflection, maxDeflection);
                    }
                }
                Vector3 lookDir = Quaternion.AngleAxis(clampedDelta, Vector3.up) * toTarget;
                lookDir.y = 0f;
                if (lookDir.sqrMagnitude < 0.01f) lookDir = toTarget;
                lookDir.Normalize();

                _lastTargetRouteDelta = Mathf.Abs(routeDelta);
                _lastLookIntentDelta = Mathf.Abs(clampedDelta);
                _lastLookIntentFrame = Time.frameCount;
                return origin + lookDir * 7.0f + Vector3.up * 1.05f;
            }
            catch
            {
                return searchPoint + Vector3.up * 1.0f;
            }
        }

        private static float GetOccludedSeekYawOffset(Character target)
        {
            if (_occludedSeekTarget != target || Time.time >= _nextOccludedSeekOffsetRefresh || Mathf.Abs(_occludedSeekYawOffset) < 1f)
            {
                _occludedSeekTarget = target;
                float sign = UnityEngine.Random.value < 0.5f ? -1f : 1f;
                _occludedSeekYawOffset = sign * UnityEngine.Random.Range(7.5f, 11.5f);
                _nextOccludedSeekOffsetRefresh = Time.time + UnityEngine.Random.Range(1.8f, 3.0f);
            }
            return _occludedSeekYawOffset;
        }

        private static Vector3 GetPathLookDirection(Character player, Vector3 fallbackMoveDir)
        {
            Vector3 fallback = fallbackMoveDir;
            fallback.y = 0f;

            try
            {
                if (player == null || player.transform == null || Path.Count == 0 || _pathIndex < 0 || _pathIndex >= Path.Count)
                    return StabilizeRouteLookDirection(fallback);

                Vector3 playerPos = player.transform.position;
                Vector3 corner = Path[_pathIndex];
                Vector3 currentDir = corner - playerPos;
                currentDir.y = 0f;
                float cornerDistance = currentDir.magnitude;
                if (cornerDistance <= 0.01f) return fallback;
                currentDir /= cornerDistance;

                if (_pathIndex + 1 < Path.Count)
                {
                    Vector3 nextDir = Path[_pathIndex + 1] - corner;
                    nextDir.y = 0f;
                    if (nextDir.sqrMagnitude > 0.01f)
                    {
                        nextDir.Normalize();
                        float blend = Mathf.Clamp01((PathLookAheadBlendDistance - cornerDistance) / PathLookAheadBlendDistance);
                        blend = Mathf.SmoothStep(0f, 1f, blend);
                        currentDir = Vector3.Slerp(currentDir, nextDir, blend);
                    }
                }

                currentDir.y = 0f;
                return StabilizeRouteLookDirection(currentDir.sqrMagnitude > 0.01f ? currentDir.normalized : fallback);
            }
            catch
            {
                return StabilizeRouteLookDirection(fallback);
            }
        }

        private static Vector3 StabilizeRouteLookDirection(Vector3 candidate)
        {
            candidate.y = 0f;
            if (candidate.sqrMagnitude < 0.01f)
                return _hasStableRouteLookDir ? _stableRouteLookDir : Vector3.zero;
            candidate.Normalize();

            if (!_hasStableRouteLookDir || _stableRouteLookDir.sqrMagnitude < 0.01f)
            {
                _stableRouteLookDir = candidate;
                _pendingRouteLookDir = Vector3.zero;
                _pendingRouteLookSince = 0f;
                _hasStableRouteLookDir = true;
                return candidate;
            }

            float angle = Vector3.Angle(_stableRouteLookDir, candidate);
            if (angle > 55f)
            {
                if (_pendingRouteLookDir.sqrMagnitude < 0.01f || Vector3.Angle(_pendingRouteLookDir, candidate) > 18f)
                {
                    _pendingRouteLookDir = candidate;
                    _pendingRouteLookSince = Time.time;
                    return _stableRouteLookDir;
                }
                if (Time.time - _pendingRouteLookSince < 0.20f)
                    return _stableRouteLookDir;
            }
            else
            {
                _pendingRouteLookDir = Vector3.zero;
                _pendingRouteLookSince = 0f;
            }

            float dt = Mathf.Clamp(Time.deltaTime, 0.001f, 0.050f);
            float turnSpeed = angle > 90f ? 280f : 230f;
            _stableRouteLookDir = Vector3.RotateTowards(_stableRouteLookDir, candidate, turnSpeed * dt * Mathf.Deg2Rad, 0f);
            _stableRouteLookDir.y = 0f;
            if (_stableRouteLookDir.sqrMagnitude < 0.01f) _stableRouteLookDir = candidate;
            _stableRouteLookDir.Normalize();
            return _stableRouteLookDir;
        }

        private static float SignedXZAngle(Vector3 from, Vector3 to)
        {
            from.y = 0f;
            to.y = 0f;
            if (from.sqrMagnitude < 0.0001f || to.sqrMagnitude < 0.0001f) return 0f;
            from.Normalize();
            to.Normalize();
            float crossY = Vector3.Cross(from, to).y;
            float dot = Mathf.Clamp(Vector3.Dot(from, to), -1f, 1f);
            return Mathf.Atan2(crossY, dot) * Mathf.Rad2Deg;
        }

        private static Vector3 SelectSearchPoint(Character player, Character target, TargetSense sense)
        {
            Vector3 playerPos = player.transform.position;
            Vector3 targetPos = NormalizeNavigationDestination(player, target.transform.position);
            PrepareSearchPointFailureContext(target, targetPos);
            CandidatePoints.Clear();

            Vector3 fromTarget = playerPos - targetPos;
            fromTarget.y = 0f;
            if (fromTarget.sqrMagnitude < 0.01f) fromTarget = -target.transform.forward;
            fromTarget.Normalize();

            Vector3 side = Vector3.Cross(Vector3.up, fromTarget);
            if (side.sqrMagnitude < 0.01f) side = player.transform.right;
            side.y = 0f;
            side.Normalize();

            float seekDistance = StrategyMode == 1 ? 4.0f : 5.8f;
            CandidatePoints.Add(targetPos + fromTarget * seekDistance);
            CandidatePoints.Add(targetPos + side * seekDistance);
            CandidatePoints.Add(targetPos - side * seekDistance);
            CandidatePoints.Add(targetPos + fromTarget * (seekDistance + 2.0f) + side * 2.0f);
            CandidatePoints.Add(targetPos + fromTarget * (seekDistance + 2.0f) - side * 2.0f);
            if (FailedSearchPoints.Count >= CandidatePoints.Count)
            {
                FileLogger.Log("AUTO-BATTLE][ROUTE", "provider=seek result=retry_cycle failedPoints=" + FailedSearchPoints.Count +
                    " target=" + SafeTargetName(target));
                FailedSearchPoints.Clear();
            }

            Vector3 best = CandidatePoints[0];
            float bestScore = float.MaxValue;
            for (int i = 0; i < CandidatePoints.Count; i++)
            {
                Vector3 p = NormalizeNavigationDestination(player, CandidatePoints[i]);
                float score = ScoreSearchPoint(player, target, sense, p);
                if (score < bestScore)
                {
                    bestScore = score;
                    best = p;
                }
            }

            if (_hasStableSearchPoint && _stableSearchTarget == target)
            {
                Vector3 stable = NormalizeNavigationDestination(player, _stableSearchPoint);
                float maxTargetDrift = seekDistance + 5.0f;
                bool stillRelevant = XZDistanceSq(stable, targetPos) <= maxTargetDrift * maxTargetDrift;
                float routePenalty = stillRelevant
                    ? AutoBattleRoutePlanner.CandidatePenalty(playerPos, stable, SafeRoot(player))
                    : 220f;
                bool stableFailed = IsFailedSearchPoint(target, stable);
                if (!stableFailed && routePenalty < 200f)
                {
                    float stableScore = ScoreSearchPoint(player, target, sense, stable);
                    bool activeRoute = Path.Count > 0 && _pathIndex >= 0 && _pathIndex < Path.Count;
                    bool meaningfulImprovement = bestScore + 32f < stableScore;
                    if (Time.time < _stableSearchPointHoldUntil || (activeRoute && !meaningfulImprovement))
                    {
                        _stableSearchPoint = stable;
                        _stableSearchPointScore = stableScore;
                        return stable;
                    }
                }
            }

            _stableSearchTarget = target;
            _stableSearchPoint = best;
            _stableSearchPointScore = bestScore;
            _stableSearchPointHoldUntil = Time.time + SearchPointMinimumHold;
            _hasStableSearchPoint = true;
            return best;
        }

        private static float ScoreSearchPoint(Character player, Character target, TargetSense sense, Vector3 point)
        {
            Vector3 playerPos = player.transform.position;
            float score = (point - playerPos).sqrMagnitude * 0.72f;
            float routePenalty = AutoBattleRoutePlanner.CandidatePenalty(playerPos, point, SafeRoot(player));
            score += routePenalty * 2.8f;
            if (IsFailedSearchPoint(target, point)) score += 260f;
            if (HasClearSegment(point + Vector3.up * 1.2f, sense.AimPoint, target)) score -= 40f;
            else score += 18f;
            score += CountExposureAtPoint(point, player, target, false) * 14f;

            Vector3 candidateDir = point - playerPos;
            candidateDir.y = 0f;
            Vector3 preferredDir = player.transform.forward;
            if (Path.Count > 0 && _pathIndex >= 0 && _pathIndex < Path.Count)
                preferredDir = Path[_pathIndex] - playerPos;
            preferredDir.y = 0f;
            if (candidateDir.sqrMagnitude > 0.01f && preferredDir.sqrMagnitude > 0.01f)
                score += Vector3.Angle(preferredDir, candidateDir) * 0.48f;

            if (_hasDestination)
            {
                float destinationDrift = Mathf.Sqrt(XZDistanceSq(point, _destination));
                if (destinationDrift <= 2.4f) score -= 38f;
                else score += Mathf.Min(70f, destinationDrift * 2.2f);
            }
            return score;
        }

        private static void PrepareSearchPointFailureContext(Character target, Vector3 targetPos)
        {
            if (_failedSearchTarget == target && XZDistanceSq(_failedSearchTargetPosition, targetPos) <= 9.0f) return;
            FailedSearchPoints.Clear();
            _failedSearchTarget = target;
            _failedSearchTargetPosition = targetPos;
        }

        private static bool IsFailedSearchPoint(Character target, Vector3 point)
        {
            if (_failedSearchTarget != target) return false;
            for (int i = 0; i < FailedSearchPoints.Count; i++)
            {
                if (XZDistanceSq(FailedSearchPoints[i], point) <= 4.0f) return true;
            }
            return false;
        }

        private static void MarkCurrentSearchPointFailed(Character player, TargetSense sense, string reason)
        {
            Character target = _target;
            Vector3 point = _hasStableSearchPoint ? _stableSearchPoint : _destination;
            Vector3 targetPos = target == null || target.transform == null
                ? point
                : NormalizeNavigationDestination(player, target.transform.position);
            PrepareSearchPointFailureContext(target, targetPos);

            bool duplicate = false;
            for (int i = 0; i < FailedSearchPoints.Count; i++)
            {
                if (XZDistanceSq(FailedSearchPoints[i], point) <= 2.25f)
                {
                    duplicate = true;
                    break;
                }
            }
            if (!duplicate) FailedSearchPoints.Add(point);

            FileLogger.Log("AUTO-BATTLE][ROUTE", "provider=seek result=point_failed reason=" + reason +
                " target=" + SafeTargetName(target) +
                " targetDist=" + (sense == null ? "-" : sense.Distance.ToString("0.0")) +
                " point=" + FormatVec(point) +
                " residual=" + _currentPathResidual.ToString("0.0") +
                " failedPoints=" + FailedSearchPoints.Count);
            _stableSearchTarget = null;
            _stableSearchPoint = Vector3.zero;
            _stableSearchPointScore = 0f;
            _stableSearchPointHoldUntil = 0f;
            _hasStableSearchPoint = false;
            _hasDestination = false;
        }

        private static void ResetSearchPointFailures()
        {
            FailedSearchPoints.Clear();
            _failedSearchTarget = null;
            _failedSearchTargetPosition = Vector3.zero;
        }

        private static Vector3 SelectSniperSearchPoint(Character player, Character target, TargetSense sense)
        {
            Vector3 playerPos = player.transform.position;
            Vector3 targetPos = NormalizeNavigationDestination(player, target.transform.position);
            PrepareSearchPointFailureContext(target, targetPos);
            CandidatePoints.Clear();

            Vector3 fromTarget = playerPos - targetPos;
            fromTarget.y = 0f;
            if (fromTarget.sqrMagnitude < 0.01f) fromTarget = -target.transform.forward;
            fromTarget.Normalize();

            Vector3 side = Vector3.Cross(Vector3.up, fromTarget);
            if (side.sqrMagnitude < 0.01f) side = player.transform.right;
            side.y = 0f;
            side.Normalize();

            const float range = 17.5f;
            CandidatePoints.Add(targetPos + fromTarget * range);
            CandidatePoints.Add(targetPos + fromTarget * (range + 4.0f));
            CandidatePoints.Add(targetPos + fromTarget * range + side * 5.0f);
            CandidatePoints.Add(targetPos + fromTarget * range - side * 5.0f);
            CandidatePoints.Add(playerPos + fromTarget * 4.0f);
            CandidatePoints.Add(targetPos + fromTarget * range + side * 10.0f);
            CandidatePoints.Add(targetPos + fromTarget * range - side * 10.0f);
            CandidatePoints.Add(targetPos + side * range);
            CandidatePoints.Add(targetPos - side * range);
            if (FailedSearchPoints.Count >= CandidatePoints.Count)
            {
                FileLogger.Log("AUTO-BATTLE][ROUTE", "provider=sniper_seek result=retry_cycle failedPoints=" + FailedSearchPoints.Count +
                    " target=" + SafeTargetName(target));
                FailedSearchPoints.Clear();
            }

            Vector3 best = CandidatePoints[0];
            float bestScore = float.MaxValue;
            for (int i = 0; i < CandidatePoints.Count; i++)
            {
                Vector3 p = NormalizeNavigationDestination(player, CandidatePoints[i]);
                float score = ScoreSniperSearchPoint(player, target, sense, p, targetPos, range);
                if (score < bestScore)
                {
                    bestScore = score;
                    best = p;
                }
            }

            if (_hasStableSearchPoint && _stableSearchTarget == target)
            {
                Vector3 stable = NormalizeNavigationDestination(player, _stableSearchPoint);
                float stableTargetDistance = Mathf.Sqrt(XZDistanceSq(stable, targetPos));
                bool stillRelevant = stableTargetDistance >= 4.0f && stableTargetDistance <= range + 14.0f;
                float routePenalty = stillRelevant
                    ? AutoBattleRoutePlanner.CandidatePenalty(playerPos, stable, SafeRoot(player))
                    : 220f;
                bool stableFailed = IsFailedSearchPoint(target, stable);
                if (!stableFailed && routePenalty < 200f)
                {
                    float stableScore = ScoreSniperSearchPoint(player, target, sense, stable, targetPos, range);
                    bool activeRoute = Path.Count > 0 && _pathIndex >= 0 && _pathIndex < Path.Count;
                    bool meaningfulImprovement = bestScore + 18f < stableScore;
                    if (Time.time < _stableSearchPointHoldUntil || (activeRoute && !meaningfulImprovement))
                    {
                        _stableSearchPoint = stable;
                        _stableSearchPointScore = stableScore;
                        return stable;
                    }
                }
            }

            _stableSearchTarget = target;
            _stableSearchPoint = best;
            _stableSearchPointScore = bestScore;
            _stableSearchPointHoldUntil = Time.time + SearchPointMinimumHold;
            _hasStableSearchPoint = true;
            return best;
        }

        private static float ScoreSniperSearchPoint(Character player, Character target, TargetSense sense,
            Vector3 point, Vector3 targetPosition, float range)
        {
            Vector3 playerPosition = player.transform.position;
            float distanceToTarget = Mathf.Sqrt(XZDistanceSq(point, targetPosition));
            float score = (point - playerPosition).sqrMagnitude * 0.35f;
            score += Mathf.Abs(distanceToTarget - range) * 9.0f;
            score += AutoBattleRoutePlanner.CandidatePenalty(playerPosition, point, SafeRoot(player));
            if (IsFailedSearchPoint(target, point)) score += 500f;
            if (HasClearSegment(point + Vector3.up * 1.25f, sense.AimPoint, target)) score -= 35f;
            score += CountExposureAtPoint(point, player, target, true) * 18f;
            return score;
        }

        private static Vector3 SelectSniperCombatPoint(Character player, Character target, TargetSense sense)
        {
            Vector3 playerPos = player.transform.position;
            Vector3 targetPos = NormalizeNavigationDestination(player, target.transform.position);
            if (sense != null && sense.FireLineOfSight && sense.Distance >= 14.0f && sense.Distance <= 28.0f)
                return playerPos;

            Vector3 away = playerPos - targetPos;
            away.y = 0f;
            if (away.sqrMagnitude < 0.01f) away = -target.transform.forward;
            away.Normalize();

            if (sense != null && sense.Distance < 14.0f)
                return NormalizeNavigationDestination(player, playerPos + away * Mathf.Clamp(15.0f - sense.Distance, 3.0f, 7.0f));

            return SelectSniperSearchPoint(player, target, sense);
        }

        private static Vector3 SelectEngagePoint(Character player, Character target, TargetSense sense, bool shieldFront, bool kite)
        {
            Vector3 playerPos = player.transform.position;
            Vector3 targetPos = NormalizeNavigationDestination(player, target.transform.position);
            CandidatePoints.Clear();

            if (kite)
            {
                Vector3 away = playerPos - targetPos;
                away.y = 0f;
                if (away.sqrMagnitude < 0.01f) away = -player.transform.forward;
                away.Normalize();
                CandidatePoints.Add(playerPos + away * 5.0f);
                CandidatePoints.Add(targetPos + away * 9.0f);
            }
            else if (shieldFront)
            {
                Vector3 right = target.transform.right;
                right.y = 0f;
                if (right.sqrMagnitude < 0.01f) right = Vector3.right;
                right.Normalize();
                Vector3 front = target.transform.rotation * new Vector3(0f, 0f, -1f);
                front.y = 0f;
                if (front.sqrMagnitude < 0.01f) front = target.transform.forward;
                front.Normalize();
                CandidatePoints.Add(targetPos + right * 3.5f);
                CandidatePoints.Add(targetPos - right * 3.5f);
                CandidatePoints.Add(targetPos - front * 3.8f);
                CandidatePoints.Add(targetPos + right * 2.5f - front * 2.5f);
                CandidatePoints.Add(targetPos - right * 2.5f - front * 2.5f);
            }
            else
            {
                Vector3 fromTarget = playerPos - targetPos;
                fromTarget.y = 0f;
                if (fromTarget.sqrMagnitude < 0.01f) fromTarget = -player.transform.forward;
                fromTarget.Normalize();
                float engageDistance = StrategyMode == 1 ? 3.1f : 4.8f;
                Vector3 side = Vector3.Cross(Vector3.up, fromTarget);
                if (side.sqrMagnitude < 0.01f) side = player.transform.right;
                side.y = 0f;
                side.Normalize();
                CandidatePoints.Add(targetPos + fromTarget * engageDistance);
                CandidatePoints.Add(targetPos + side * engageDistance);
                CandidatePoints.Add(targetPos - side * engageDistance);
            }

            Vector3 best = CandidatePoints.Count > 0 ? CandidatePoints[0] : playerPos;
            float bestScore = float.MaxValue;
            for (int i = 0; i < CandidatePoints.Count; i++)
            {
                Vector3 p = NormalizeNavigationDestination(player, CandidatePoints[i]);
                float score = (p - playerPos).sqrMagnitude;
                score += AutoBattleRoutePlanner.CandidatePenalty(playerPos, p, SafeRoot(player));
                if (!HasClearSegment(p + Vector3.up * 1.2f, sense.AimPoint, target)) score += 80f;
                if (score < bestScore)
                {
                    bestScore = score;
                    best = p;
                }
            }

            return best;
        }

        private static Vector3 SelectSaferCombatPoint(Character player, Character target, TargetSense sense)
        {
            Vector3 playerPos = player.transform.position;
            Vector3 targetPos = NormalizeNavigationDestination(player, target.transform.position);
            CandidatePoints.Clear();

            Vector3 toTarget = targetPos - playerPos;
            toTarget.y = 0f;
            if (toTarget.sqrMagnitude < 0.01f) toTarget = player.transform.forward;
            toTarget.Normalize();

            Vector3 side = Vector3.Cross(Vector3.up, toTarget);
            if (side.sqrMagnitude < 0.01f) side = player.transform.right;
            side.y = 0f;
            side.Normalize();

            CandidatePoints.Add(playerPos + side * 3.0f);
            CandidatePoints.Add(playerPos - side * 3.0f);
            CandidatePoints.Add(playerPos - toTarget * 3.5f);
            CandidatePoints.Add(playerPos - toTarget * 2.4f + side * 2.2f);
            CandidatePoints.Add(playerPos - toTarget * 2.4f - side * 2.2f);

            Vector3 best = playerPos;
            float bestScore = float.MaxValue;
            for (int i = 0; i < CandidatePoints.Count; i++)
            {
                Vector3 p = NormalizeNavigationDestination(player, CandidatePoints[i]);
                int exposure = CountExposureAtPoint(p, player, target, false);
                int aimThreat = CountExposureAtPoint(p, player, target, true);
                float score = (p - playerPos).sqrMagnitude + exposure * 35f + aimThreat * 45f;
                score += AutoBattleRoutePlanner.CandidatePenalty(playerPos, p, SafeRoot(player));
                if (HasClearSegment(p + Vector3.up * 1.2f, sense.AimPoint, target)) score -= 6f;
                else score += 12f;

                if (score < bestScore)
                {
                    bestScore = score;
                    best = p;
                }
            }

            return best;
        }

        private static Vector3 SelectJukeDirection(Character player, Character target)
        {
            Vector3 playerPos = player.transform.position;
            Vector3 toTarget = target.transform.position - playerPos;
            toTarget.y = 0f;
            if (toTarget.sqrMagnitude < 0.01f) toTarget = player.transform.forward;
            toTarget.Normalize();

            Vector3 side = Vector3.Cross(Vector3.up, toTarget);
            if (side.sqrMagnitude < 0.01f) side = player.transform.right;
            side.y = 0f;
            side.Normalize();

            Vector3 leftPoint = playerPos + side * 2.4f;
            Vector3 rightPoint = playerPos - side * 2.4f;
            float leftScore = CountExposureAtPoint(leftPoint, player, target, true) * 20f + AutoBattleRoutePlanner.CandidatePenalty(playerPos, leftPoint, SafeRoot(player));
            float rightScore = CountExposureAtPoint(rightPoint, player, target, true) * 20f + AutoBattleRoutePlanner.CandidatePenalty(playerPos, rightPoint, SafeRoot(player));
            if (Mathf.Abs(leftScore - rightScore) < 0.01f)
            {
                return UnityEngine.Random.value < 0.5f ? side : -side;
            }
            return leftScore < rightScore ? side : -side;
        }

        private static bool TryGetCombatMove(Character player, Character target, TargetSense sense, out Vector3 moveDir, out bool jump)
        {
            moveDir = Vector3.zero;
            jump = false;
            try
            {
                if (player == null || target == null || sense == null) return false;
                if (!sense.Visible || !sense.FireLineOfSight) return false;

                bool needNew = Time.time >= _nextCombatMoveChangeTime ||
                               _combatMoveDir.sqrMagnitude < 0.01f ||
                               AutoBattleRoutePlanner.HasForwardBlock(player.transform.position, _combatMoveDir, SafeRoot(player));
                if (needNew)
                {
                    Vector3 toTarget = target.transform.position - player.transform.position;
                    toTarget.y = 0f;
                    if (toTarget.sqrMagnitude < 0.01f) toTarget = player.transform.forward;
                    if (toTarget.sqrMagnitude < 0.01f) return false;
                    toTarget.Normalize();

                    Vector3 side = Vector3.Cross(Vector3.up, toTarget);
                    if (side.sqrMagnitude < 0.01f) side = player.transform.right;
                    side.y = 0f;
                    if (side.sqrMagnitude < 0.01f) return false;
                    side.Normalize();

                    float sign = UnityEngine.Random.value < 0.5f ? -1f : 1f;
                    CandidatePoints.Clear();
                    if (sense.Distance <= 4.5f)
                    {
                        CandidatePoints.Add(side * sign * 0.95f - toTarget * 0.35f);
                        CandidatePoints.Add(side * -sign * 0.95f - toTarget * 0.25f);
                        CandidatePoints.Add(-toTarget * 0.65f);
                    }
                    else if (sense.Distance <= 15f)
                    {
                        CandidatePoints.Add(side * sign * 0.90f + toTarget * 0.18f);
                        CandidatePoints.Add(side * -sign * 0.75f + toTarget * 0.12f);
                        CandidatePoints.Add(side * sign * 0.75f - toTarget * 0.18f);
                    }
                    else
                    {
                        CandidatePoints.Add(side * sign * 0.55f + toTarget * 0.55f);
                        CandidatePoints.Add(side * -sign * 0.45f + toTarget * 0.60f);
                        CandidatePoints.Add(toTarget * 0.75f);
                    }

                    Vector3 selected = Vector3.zero;
                    for (int i = 0; i < CandidatePoints.Count; i++)
                    {
                        Vector3 candidate = CandidatePoints[i];
                        candidate.y = 0f;
                        if (candidate.sqrMagnitude < 0.01f) continue;
                        candidate.Normalize();
                        if (AutoBattleRoutePlanner.HasForwardBlock(player.transform.position, candidate, SafeRoot(player))) continue;
                        selected = candidate;
                        break;
                    }

                    if (selected.sqrMagnitude < 0.01f) return false;
                    _combatMoveDir = selected;
                    _nextCombatMoveChangeTime = Time.time + UnityEngine.Random.Range(0.38f, 0.82f);
                }

                moveDir = _combatMoveDir;
                if (Time.time >= _nextCombatJumpTime &&
                    sense.Distance <= 18f &&
                    UnityEngine.Random.value < (sense.Distance <= 6f ? 0.34f : 0.18f))
                {
                    jump = true;
                    _nextCombatJumpTime = Time.time + UnityEngine.Random.Range(1.05f, 2.35f);
                }
                return moveDir.sqrMagnitude > 0.01f;
            }
            catch
            {
                moveDir = Vector3.zero;
                jump = false;
                return false;
            }
        }

        private static bool IsEnemyFacingPoint(Character enemy, Vector3 point, float threshold)
        {
            try
            {
                if (enemy == null || enemy.transform == null) return false;
                Vector3 toPoint = point - enemy.transform.position;
                toPoint.y = 0f;
                if (toPoint.sqrMagnitude < 0.01f) return false;
                toPoint.Normalize();
                Vector3 forward = enemy.transform.forward;
                forward.y = 0f;
                if (forward.sqrMagnitude < 0.01f) return false;
                forward.Normalize();
                return Vector3.Dot(forward, toPoint) >= threshold;
            }
            catch
            {
                return false;
            }
        }

        private static int CountExposureAtPoint(Vector3 point, Character player, Character focusTarget, bool requireAiming)
        {
            int count = 0;
            try
            {
                bool currentPlayerPoint = player != null && player.transform != null &&
                                          (point - player.transform.position).sqrMagnitude < 1.2f * 1.2f;
                for (int i = 0; i < Characters.Count; i++)
                {
                    Character enemy = Characters[i];
                    if (!IsUsableTarget(player, enemy)) continue;
                    if (enemy == focusTarget) continue;
                    if (!IsVisibleByGame(player, enemy)) continue;

                    Vector3 enemyPos = enemy.transform.position;
                    Vector3 toPoint = point - enemyPos;
                    toPoint.y = 0f;
                    if (toPoint.sqrMagnitude < 0.01f) continue;
                    toPoint.Normalize();

                    Vector3 enemyForward = enemy.transform.forward;
                    enemyForward.y = 0f;
                    if (enemyForward.sqrMagnitude < 0.01f) enemyForward = Vector3.forward;
                    enemyForward.Normalize();

                    float dot = Vector3.Dot(enemyForward, toPoint);
                    float threshold = requireAiming ? 0.72f : 0.18f;
                    if (dot < threshold) continue;

                    Transform ignoreRoot = null;
                    try
                    {
                        ignoreRoot = enemy.transform.root;
                    }
                    catch
                    {
                        ignoreRoot = null;
                    }

                    Character expectedHit = currentPlayerPoint ? player : null;
                    bool clear = HasClearSegment(enemyPos + Vector3.up * 1.25f, point + Vector3.up * 1.1f, expectedHit, ignoreRoot);
                    if (!clear) continue;
                    count++;
                }
            }
            catch
            {
            }
            return count;
        }

        private static Vector3 NormalizeNavigationDestination(Character player, Vector3 dest)
        {
            return NormalizeNavigationDestination(player, dest, false);
        }

        private static Vector3 NormalizeNavigationDestination(Character player, Vector3 dest, bool preserveHeight)
        {
            if (player == null || player.transform == null) return dest;

            Vector3 playerPos = player.transform.position;
            Vector3 normalized = dest;
            if (!preserveHeight && Mathf.Abs(normalized.y - playerPos.y) > 2.2f)
                normalized.y = playerPos.y;

            Vector3 grounded;
            float referenceY = preserveHeight ? normalized.y : playerPos.y;
            float maxDelta = preserveHeight ? 4.5f : 2.4f;
            if (TryProjectNavigationGround(normalized, referenceY, maxDelta, out grounded))
                return grounded;

            if (!preserveHeight) normalized.y = playerPos.y;
            return normalized;
        }

        private static bool TryProjectNavigationGround(Vector3 point, Vector3 playerPos, out Vector3 grounded)
        {
            return TryProjectNavigationGround(point, playerPos.y, 2.4f, out grounded);
        }

        private static bool TryProjectNavigationGround(Vector3 point, float referenceY, float maxDelta, out Vector3 grounded)
        {
            grounded = point;
            try
            {
                int mask = LayerMask.GetMask(new string[] { "Terrarin" });
                Vector3 origin = point + Vector3.up * 3.5f;
                RaycastHit hit;
                bool ok = mask != 0
                    ? Physics.Raycast(origin, Vector3.down, out hit, 8.0f, mask)
                    : Physics.Raycast(origin, Vector3.down, out hit, 8.0f);
                if (!ok) return false;
                if (Mathf.Abs(hit.point.y - referenceY) > maxDelta) return false;
                grounded = hit.point;
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static Vector3 UpdateNavigation(Character player, Vector3 dest, TargetSense sense, bool tacticalMove)
        {
            return UpdateNavigation(player, dest, sense, tacticalMove, false, false);
        }

        private static Vector3 UpdateNavigation(Character player, Vector3 dest, TargetSense sense, bool tacticalMove, bool preserveHeight)
        {
            return UpdateNavigation(player, dest, sense, tacticalMove, preserveHeight, false);
        }

        private static Vector3 UpdateNavigation(Character player, Vector3 dest, TargetSense sense, bool tacticalMove,
            bool preserveHeight, bool requireRainPath)
        {
            bool seekNavigation = State == AutoBattleState.Seek && !tacticalMove;
            dest = NormalizeNavigationDestination(player, dest, preserveHeight);
            if (_pathSearchPending && _hasDestination)
            {
                float pendingDeltaSq = XZDistanceSq(_destination, dest);
                float pendingYDelta = Mathf.Abs(_destination.y - dest.y);
                if (pendingDeltaSq <= 16.0f && pendingYDelta <= 2.0f)
                    dest = _destination;
            }
            bool firstDestination = !_hasDestination;
            float destDeltaSq = firstDestination ? 999999f : XZDistanceSq(_destination, dest);
            float destYDelta = firstDestination ? 999f : Mathf.Abs(_destination.y - dest.y);
            bool softDestinationChanged = !firstDestination &&
                                          (destDeltaSq > (tacticalMove ? 4.0f : 6.25f) ||
                                           destYDelta > 1.25f);
            bool hardDestinationChanged = firstDestination ||
                                          destDeltaSq > 36.0f ||
                                          destYDelta > 2.5f;
            _destination = dest;
            _hasDestination = true;
            if (hardDestinationChanged && (Path.Count == 0 || Time.time >= _nextRepath))
            {
                ClearCurrentPath();
                _pathIndex = 0;
            }

            bool needRepath = Path.Count == 0 || _pathIndex >= Path.Count;
            if (!needRepath && softDestinationChanged && Time.time >= _nextRepath)
                needRepath = true;
            if ((_pathSearchPending || needRepath) && Time.time >= _nextRepath)
            {
                _nextRepath = _pathSearchPending
                    ? Time.time
                    : Time.time + (tacticalMove ? 0.24f : RepathInterval);
                BuildPath(player, player.transform.position, dest, null, SafeRoot(player), tacticalMove, requireRainPath);
            }

            bool followingPendingPath = false;
            if (_pathSearchPending)
            {
                followingPendingPath = Path.Count > 0 && _pathIndex >= 0 && _pathIndex < Path.Count;
                if (followingPendingPath)
                {
                    _pendingFollowFrames++;
                    LastPath = "path_pending_follow " + (_pathIndex + 1) + "/" + Path.Count;
                }
                else
                {
                    _pendingHoldFrames++;
                    _stuckTime = 0f;
                    LastPath = "path_pending_hold";
                }
                if (Time.time >= _nextPendingPathLogTime)
                {
                    _nextPendingPathLogTime = Time.time + 0.85f;
                    FileLogger.Log("AUTO-BATTLE][ROUTE", "provider=pending mode=" +
                        (followingPendingPath ? "follow_old" : "hold_no_old") +
                        " corner=" + (_pathIndex + 1) + "/" + Path.Count +
                        " followFrames=" + _pendingFollowFrames +
                        " holdFrames=" + _pendingHoldFrames +
                        " detail=" + LastPathDetail);
                }
                if (!followingPendingPath) return Vector3.zero;
            }

            UpdateStuck(player, sense);
            if (_stuckTime > 0.62f && Time.time >= _nextStuckRecoveryTime)
            {
                State = AutoBattleState.StuckRecovery;
                _stuckCount++;
                _stuckTime = 0f;
                _nextStuckRecoveryTime = Time.time + 0.45f;
                bool rainRecovery = !string.IsNullOrEmpty(LastPathProvider) &&
                                    LastPathProvider.StartsWith("rain_navmesh", StringComparison.Ordinal);
                Vector3 routeForward = player.transform.forward;
                if (Path.Count > 0 && _pathIndex >= 0 && _pathIndex < Path.Count)
                    routeForward = Path[_pathIndex] - player.transform.position;
                routeForward.y = 0f;
                if (seekNavigation && sense != null && !sense.StrictFireLineOfSight)
                    MarkCurrentSearchPointFailed(player, sense, "no_progress");
                ClearCurrentPath();
                _nextRepath = 0f;
                LastPath = "stuck_repath#" + _stuckCount;
                Vector3 side = player.transform.right * ((_stuckCount % 2 == 0) ? 1f : -1f);
                Vector3 forward = player.transform.forward;
                forward.y = 0f;
                side.y = 0f;
                if (forward.sqrMagnitude < 0.01f) forward = Vector3.forward;
                if (side.sqrMagnitude < 0.01f) side = Vector3.right;
                forward.Normalize();
                side.Normalize();
                if (!rainRecovery && SafeIsOnGround(player) &&
                    AutoBattleRoutePlanner.ShouldJumpForwardObstacle(player.transform.position, forward, SafeRoot(player)))
                {
                    AutoBattleInput.PressAction(ActionType.kActionJump, 0.12f);
                    AutoBattleInput.HoldAction(ActionType.kActionJump, 0.22f);
                    LastPath += " jump_obstacle";
                }
                Vector3 rainClearanceDirection;
                string rainClearanceDetail;
                if (rainRecovery && AutoBattleRoutePlanner.TryFindRainClearanceDirection(
                    player.transform.position,
                    routeForward.sqrMagnitude > 0.01f ? routeForward : forward,
                    SafeRoot(player), out rainClearanceDirection, out rainClearanceDetail))
                {
                    LastPath += " rain_clearance";
                    LastPathDetail = rainClearanceDetail + " dest=" + FormatVec(dest);
                    FileLogger.Log("AUTO-BATTLE][ROUTE", "provider=rain_navmesh recovery=corner_clearance " +
                        rainClearanceDetail + " pos=" + FormatVec(player.transform.position));
                    return rainClearanceDirection;
                }
                Vector3 escape = _stuckCount % 3 == 0
                    ? -forward
                    : side - forward * 0.45f;
                return escape.sqrMagnitude < 0.01f ? -forward : escape.normalized;
            }

            if (Path.Count == 0)
            {
                string reason = LastPath;
                LastPath = tacticalMove ? "no_path_hold" : "no_path";
                if (!string.IsNullOrEmpty(reason) && reason != "-" && !reason.StartsWith("no_path"))
                    LastPath += ":" + reason;
                return Vector3.zero;
            }

            if (_pathIndex >= Path.Count) _pathIndex = Path.Count - 1;
            Vector3 next = Path[_pathIndex];
            Vector3 flatToNext = next - player.transform.position;
            flatToNext.y = 0f;
            float d = flatToNext.magnitude;
            while (d < CornerReachDistance && _pathIndex < Path.Count - 1)
            {
                _pathIndex++;
                next = Path[_pathIndex];
                flatToNext = next - player.transform.position;
                flatToNext.y = 0f;
                d = flatToNext.magnitude;
            }

            bool jumpEdge = _pathIndex >= 0 && _pathIndex < PathJumpFlags.Count && PathJumpFlags[_pathIndex];
            bool unresolvedSeek = seekNavigation && sense != null && !sense.StrictFireLineOfSight;
            if (_currentPathPartial && unresolvedSeek && _pathIndex == Path.Count - 1 && d <= 1.15f)
            {
                MarkCurrentSearchPointFailed(player, sense, "partial_frontier");
                ClearCurrentPath();
                _nextRepath = 0f;
                LastPath = "seek_reselect:partial_frontier";
                return Vector3.zero;
            }
            if (!jumpEdge && d < CornerReachDistance && _pathIndex == Path.Count - 1)
            {
                if (unresolvedSeek)
                {
                    MarkCurrentSearchPointFailed(player, sense, "arrived_no_los");
                    ClearCurrentPath();
                    _nextRepath = 0f;
                    LastPath = "seek_reselect:arrived_no_los";
                    return Vector3.zero;
                }
                float remaining = Mathf.Sqrt(XZDistanceSq(player.transform.position, _destination));
                float remainingY = Mathf.Abs(player.transform.position.y - _destination.y);
                if (remaining > 1.35f || remainingY > 1.25f)
                {
                    ClearCurrentPath();
                    _nextRepath = 0f;
                    LastPath = "path_continue";
                }
                return Vector3.zero;
            }

            Vector3 dir = next - player.transform.position;
            dir.y = 0f;
            if (dir.sqrMagnitude < 0.01f) return Vector3.zero;
            dir.Normalize();
            if (jumpEdge && d <= 1.60f && Time.time >= _nextPathJumpTime && SafeIsOnGround(player))
            {
                AutoBattleInput.PressAction(ActionType.kActionJump, 0.11f);
                AutoBattleInput.HoldAction(ActionType.kActionJump, 0.24f);
                _nextPathJumpTime = Time.time + 0.45f;
                FileLogger.Log("AUTO-BATTLE][ROUTE", "provider=follow jump=1 corner=" + (_pathIndex + 1) + "/" + Path.Count + " dist=" + d.ToString("0.0") + " targetY=" + next.y.ToString("0.0"));
            }
            if (!jumpEdge && AutoBattleRoutePlanner.HasForwardBlock(
                player.transform.position, dir, SafeRoot(player)))
            {
                if (Time.time < _nextWallRecoveryTime) return Vector3.zero;
                _nextWallRecoveryTime = Time.time + 0.18f;
                ClearCurrentPath();
                _nextRepath = 0f;
                LastPath = "wall_repath";
                LastPathDetail = "wallAhead=1 dest=" + FormatVec(dest);
                FileLogger.Log("AUTO-BATTLE][ROUTE", "provider=follow result=partial nodes=0 corners=0 rejectGround=0 rejectBlock=1 frontier=1 reason=wall_ahead dest=" + FormatVec(dest));
                _wallAheadCount++;
                if (SafeIsOnGround(player) && AutoBattleRoutePlanner.ShouldJumpForwardObstacle(player.transform.position, dir, SafeRoot(player)))
                {
                    AutoBattleInput.PressAction(ActionType.kActionJump, 0.10f);
                    AutoBattleInput.HoldAction(ActionType.kActionJump, 0.18f);
                    LastPath = "wall_repath jump_obstacle";
                }
                Vector3 side = Vector3.Cross(Vector3.up, dir).normalized *
                               ((_wallAheadCount % 2 == 0) ? 1f : -1f);
                Vector3 escape = side - dir * 0.55f;
                if (AutoBattleRoutePlanner.HasForwardBlock(player.transform.position, escape.normalized, SafeRoot(player)))
                    escape = -side - dir * 0.55f;
                if (AutoBattleRoutePlanner.HasForwardBlock(player.transform.position, escape.normalized, SafeRoot(player)))
                    escape = -dir;
                return escape.sqrMagnitude < 0.01f ? Vector3.zero : escape.normalized;
            }
            _wallAheadCount = 0;
            LastPath = (followingPendingPath ? "path_pending_follow " : "path ") +
                       (_pathIndex + 1) + "/" + Path.Count + (jumpEdge ? " jump" : string.Empty);
            return dir;
        }

        private static void BuildPath(Character player, Vector3 from, Vector3 to, Character expectedTarget,
            Transform ignoreRoot, bool tacticalMove, bool requireRainPath)
        {
            bool resumedPendingSearch = _pathSearchPending;
            bool hadUsablePath = Path.Count > 0 && _pathIndex >= 0 && _pathIndex < Path.Count;

            int seq = ++_pathBuildSeq;
            AutoBattleRouteCapabilities capabilities = GetRouteCapabilities(player);
            capabilities.RequireRainPath = requireRainPath;
            AutoBattleRouteResult route = AutoBattleRoutePlanner.BuildRoute(from, to, ignoreRoot, capabilities);
            if (route != null && route.Provider != null && route.Provider.EndsWith("_pending", StringComparison.Ordinal))
            {
                LastPathProvider = route.Provider;
                _pathSearchPending = true;
                _nextRepath = Time.time;
                SetPathResult(hadUsablePath ? "path_pending_follow" : "path_pending_hold",
                    "seq=" + seq + " oldPath=" + (hadUsablePath ? "keep" : "none") +
                    " oldCorner=" + (_pathIndex + 1) + "/" + Path.Count +
                    " from=" + FormatVec(from) + " to=" + FormatVec(to) + " " + route.Detail, false);
                return;
            }
            if (route != null && route.Success)
            {
                LastPathProvider = route.Provider;
                ClearCurrentPath();
                _currentPathPartial = route.Partial;
                _currentPathResidual = route.Corners.Count == 0
                    ? 0f
                    : Mathf.Sqrt(XZDistanceSq(route.Corners[route.Corners.Count - 1], to));
                _pendingFollowFrames = 0;
                _pendingHoldFrames = 0;
                _nextPendingPathLogTime = 0f;
                _pathIndex = 0;
                int trimmed = AddPathPoints(route.Corners, route.JumpFlags, from);
                if (Path.Count > 0)
                {
                    if (resumedPendingSearch)
                    {
                        _stuckTime = 0f;
                        _lastPlayerPos = player == null || player.transform == null ? Vector3.zero : player.transform.position;
                        if (_highGroundRepositionActive) _highGroundLastProgressAt = Time.time;
                        _nextRepath = Time.time + (tacticalMove ? 0.22f : 0.32f);
                    }
                    string label = route.Provider + (route.Partial ? " partial " : " ") + Path.Count + " pts";
                    SetPathResult(label, "seq=" + seq + " resumed=" + (resumedPendingSearch ? "1" : "0") +
                        " trimmed=" + trimmed + " from=" + FormatVec(from) + " to=" + FormatVec(to) + " " + route.Detail,
                        route.Partial || route.Provider.StartsWith("phys_grid"));
                    return;
                }
                if (route.Corners != null && route.Corners.Count > 0)
                {
                    _nextRepath = Time.time + 0.25f;
                    SetPathResult("path_complete", "seq=" + seq + " resumed=" + (resumedPendingSearch ? "1" : "0") +
                        " trimmed=" + trimmed + " from=" + FormatVec(from) + " to=" + FormatVec(to) + " " + route.Detail,
                        route.Provider.StartsWith("phys_grid"));
                    return;
                }
            }

            _pathSearchPending = false;
            _pendingFollowFrames = 0;
            _pendingHoldFrames = 0;
            _nextPendingPathLogTime = 0f;
            string routeProvider = route == null ? "none" : route.Provider;
            LastPathProvider = routeProvider;
            string routeDetail = route == null ? "route=null" : route.Detail;
            if (hadUsablePath && Path.Count > 0 && _pathIndex >= 0 && _pathIndex < Path.Count)
            {
                SetPathResult("path_keep_after_fail", "seq=" + seq + " oldCorner=" + (_pathIndex + 1) + "/" + Path.Count +
                    " from=" + FormatVec(from) + " to=" + FormatVec(to) + " " + routeDetail, true);
                return;
            }
            SetPathResult("no_path:" + routeProvider, "seq=" + seq + " from=" + FormatVec(from) + " to=" + FormatVec(to) + " " + routeDetail, true);
        }

        private static int AddPathPoints(List<Vector3> points, List<bool> jumpFlags, Vector3 from)
        {
            if (points == null || points.Count == 0) return 0;
            int startIndex = 0;
            float bestDistance = float.MaxValue;
            for (int i = 0; i < points.Count; i++)
            {
                float distance = XZDistanceSq(points[i], from);
                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    startIndex = i;
                }
            }
            if (bestDistance > 16.0f) startIndex = 0;
            if (startIndex + 1 < points.Count)
            {
                Vector3 segment = points[startIndex + 1] - points[startIndex];
                segment.y = 0f;
                Vector3 progressed = from - points[startIndex];
                progressed.y = 0f;
                if (segment.sqrMagnitude > 0.01f && Vector3.Dot(progressed, segment) / segment.sqrMagnitude > 0.35f)
                    startIndex++;
            }

            for (int i = startIndex; i < points.Count && Path.Count < 48; i++)
            {
                Vector3 p = points[i];
                Vector3 flat = p - from;
                flat.y = 0f;
                if (flat.sqrMagnitude < 0.25f && Path.Count == 0) continue;
                Path.Add(p);
                PathJumpFlags.Add(jumpFlags != null && i < jumpFlags.Count && jumpFlags[i]);
            }
            return startIndex;
        }

        private static AutoBattleRouteCapabilities GetRouteCapabilities(Character player)
        {
            AutoBattleRouteCapabilities capabilities = new AutoBattleRouteCapabilities();
            try
            {
                if (player != null && player.character_info != null)
                {
                    if (player.character_info.jump_height > 0.1f) capabilities.JumpHeight = player.character_info.jump_height;
                    if (player.character_info.jump_velocity > 0.1f) capabilities.JumpVelocity = player.character_info.jump_velocity;
                    if (player.character_info.run_speed > 0.1f) capabilities.RunSpeed = player.character_info.run_speed;
                }
            }
            catch
            {
            }
            return capabilities;
        }

        private static bool SafeIsOnGround(Character player)
        {
            try
            {
                return player != null && player.IsOnGround();
            }
            catch
            {
                return false;
            }
        }

        private static void ClearCurrentPath()
        {
            Path.Clear();
            PathJumpFlags.Clear();
            _pathSearchPending = false;
            _currentPathPartial = false;
            _currentPathResidual = 0f;
        }

        private static bool TryBuildAstarPath(Vector3 from, Vector3 to, out List<Vector3> result, out string detail)
        {
            result = null;
            detail = "astar=not_tried";
            try
            {
                if (AstarPath.active == null)
                {
                    detail = "astar=no_active";
                    return false;
                }

                Pathfinding.NavGraph[] graphs = AstarPath.active.graphs;
                if (graphs == null || graphs.Length == 0)
                {
                    detail = "astar=no_graphs";
                    return false;
                }

                Pathfinding.ABPath path = Pathfinding.ABPath.Construct(from, to, null);
                if (path == null)
                {
                    detail = "astar=construct_null";
                    return false;
                }

                AstarPath.StartPath(path);
                AstarPath.WaitForPath(path);
                if (path.error)
                {
                    detail = "astar=error:" + SafeOneLine(path.errorLog, 120);
                    return false;
                }

                if (path.vectorPath == null || path.vectorPath.Count == 0)
                {
                    detail = "astar=empty";
                    return false;
                }

                result = new List<Vector3>(path.vectorPath.Count);
                for (int i = 0; i < path.vectorPath.Count; i++) result.Add(path.vectorPath[i]);
                detail = "astar=ok pts=" + result.Count + " len=" + path.GetTotalLength().ToString("0.0");
                return result.Count > 0;
            }
            catch (Exception ex)
            {
                detail = "astar=ex:" + ex.GetType().Name + ":" + SafeOneLine(ex.Message, 96);
                return false;
            }
        }

        private static bool TryBuildLocalDetourPath(Vector3 from, Vector3 to, Character expectedTarget, Transform ignoreRoot, out List<Vector3> result, out string detail)
        {
            result = null;
            detail = "detour=not_tried";

            Vector3 forward = to - from;
            forward.y = 0f;
            if (forward.sqrMagnitude < 0.16f)
            {
                detail = "detour=too_close";
                return false;
            }
            forward.Normalize();

            Vector3 side = Vector3.Cross(Vector3.up, forward);
            if (side.sqrMagnitude < 0.01f) side = Vector3.right;
            side.y = 0f;
            side.Normalize();

            float[] forwardSteps = { 2.2f, 3.8f, 5.5f, 7.0f };
            float[] sideSteps = { 0f, 2.2f, -2.2f, 4.2f, -4.2f, 6.5f, -6.5f };
            Vector3 best = Vector3.zero;
            float bestScore = float.MaxValue;
            int candidates = 0;
            int navReject = 0;
            int segmentReject = 0;

            for (int i = 0; i < forwardSteps.Length; i++)
            {
                for (int j = 0; j < sideSteps.Length; j++)
                {
                    candidates++;
                    Vector3 candidate = from + forward * forwardSteps[i] + side * sideSteps[j];
                    Vector3 clamped;
                    if (TryClampToAstar(candidate, out clamped)) candidate = clamped;
                    else candidate.y = from.y;

                    if (!IsPointOnNavmesh(candidate))
                    {
                        navReject++;
                        continue;
                    }

                    if (!HasClearSegment(from + Vector3.up * 0.75f, candidate + Vector3.up * 0.75f, null, ignoreRoot))
                    {
                        segmentReject++;
                        continue;
                    }

                    float score = XZDistanceSq(candidate, to);
                    score += Mathf.Abs(sideSteps[j]) * 0.35f;
                    score -= forwardSteps[i] * 0.45f;
                    if (HasClearSegment(candidate + Vector3.up * 0.75f, to + Vector3.up * 0.75f, expectedTarget, ignoreRoot)) score -= 20f;
                    if (HasClearSegment(candidate + Vector3.up * 1.25f, to + Vector3.up * 1.25f, expectedTarget, ignoreRoot)) score -= 5f;

                    if (score < bestScore)
                    {
                        bestScore = score;
                        best = candidate;
                    }
                }
            }

            if (bestScore == float.MaxValue)
            {
                detail = "detour=none cands=" + candidates + " navReject=" + navReject + " segReject=" + segmentReject;
                return false;
            }

            result = new List<Vector3>(2);
            result.Add(best);
            if (HasClearSegment(best + Vector3.up * 0.75f, to + Vector3.up * 0.75f, expectedTarget, ignoreRoot)) result.Add(to);
            detail = "detour=ok cands=" + candidates + " navReject=" + navReject + " segReject=" + segmentReject + " best=" + FormatVec(best) + " score=" + bestScore.ToString("0.0");
            return true;
        }

        private static bool TryClampToAstar(Vector3 point, out Vector3 clamped)
        {
            clamped = point;
            try
            {
                if (AstarPath.active == null) return false;
                Pathfinding.NavGraph[] graphs = AstarPath.active.graphs;
                if (graphs == null || graphs.Length == 0) return false;
                Pathfinding.NNInfo info = AstarPath.active.GetNearest(point);
                if (info.node == null) return false;
                if ((info.clampedPosition - point).sqrMagnitude > 9f) return false;
                clamped = info.clampedPosition;
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static float XZDistanceSq(Vector3 a, Vector3 b)
        {
            float dx = a.x - b.x;
            float dz = a.z - b.z;
            return dx * dx + dz * dz;
        }

        private static bool TryBuildRainPath(Vector3 from, Vector3 to, out List<Vector3> result, out string detail)
        {
            result = null;
            detail = "rain=not_tried";
            try
            {
                if (NavigationManager.Instance == null)
                {
                    detail = "rain=no_instance";
                    return false;
                }
                IList<string> tags = null;
                IList graphs = NavigationManager.Instance.GraphsForPoints(from, to, 2f, NavigationManager.GraphType.Navmesh, tags);
                if (graphs == null || graphs.Count == 0)
                {
                    detail = "rain=no_graph_for_points";
                    return false;
                }
                object graph = graphs[0];
                string invokeDetail;
                object pathObj = InvokePathBuilder(graph, from, to, out invokeDetail);
                IList<Vector3> points = ExtractPathPoints(pathObj);
                if (points == null || points.Count == 0)
                {
                    detail = "rain=no_points graph=" + graph.GetType().Name + " " + invokeDetail;
                    return false;
                }
                result = new List<Vector3>(points.Count);
                for (int i = 0; i < points.Count; i++) result.Add(points[i]);
                detail = "rain=ok graph=" + graph.GetType().Name + " pts=" + result.Count;
                return result.Count > 0;
            }
            catch (Exception ex)
            {
                detail = "rain=ex:" + ex.GetType().Name + ":" + SafeOneLine(ex.Message, 96);
                return false;
            }
        }

        private static object InvokePathBuilder(object graph, Vector3 from, Vector3 to, out string detail)
        {
            detail = "invoke=none";
            if (graph == null) return null;
            Type t = graph.GetType();
            string[] names = { "GetPath", "GetPathTo", "CalculatePath", "BuildPath" };
            for (int i = 0; i < names.Length; i++)
            {
                MethodInfo mi = t.GetMethod(names[i], BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (mi == null) continue;
                ParameterInfo[] ps = mi.GetParameters();
                if (ps.Length < 2 || ps[0].ParameterType != typeof(Vector3) || ps[1].ParameterType != typeof(Vector3)) continue;
                object[] args = new object[ps.Length];
                args[0] = from;
                args[1] = to;
                for (int k = 2; k < ps.Length; k++)
                {
                    args[k] = ps[k].ParameterType.IsValueType ? Activator.CreateInstance(ps[k].ParameterType) : null;
                }
                try
                {
                    object value = mi.Invoke(graph, args);
                    if (value != null)
                    {
                        detail = "invoke=" + names[i];
                        return value;
                    }
                }
                catch (Exception ex)
                {
                    detail = "invoke_ex=" + names[i] + ":" + ex.GetType().Name;
                }
            }
            if (detail == "invoke=none") detail = "invoke=no_matching_method";
            return null;
        }

        private static IList<Vector3> ExtractPathPoints(object pathObj)
        {
            if (pathObj == null) return null;
            IList<Vector3> direct = pathObj as IList<Vector3>;
            if (direct != null) return direct;

            string[] props = { "WaypointList", "Waypoints", "Corners", "Points" };
            for (int i = 0; i < props.Length; i++)
            {
                PropertyInfo pi = pathObj.GetType().GetProperty(props[i], BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (pi == null) continue;
                try
                {
                    IList<Vector3> points = pi.GetValue(pathObj, null) as IList<Vector3>;
                    if (points != null) return points;
                }
                catch
                {
                }
            }
            return null;
        }

        private static bool IsPointOnNavmesh(Vector3 point)
        {
            try
            {
                Vector3 clamped;
                if (TryClampToAstar(point, out clamped)) return true;
                if (NavigationManager.Instance == null) return true;
                IList<string> tags = null;
                IList graphs = NavigationManager.Instance.GraphForPoint(point, 2f, NavigationManager.GraphType.Navmesh, tags);
                if (graphs == null || graphs.Count == 0) return true;
                return true;
            }
            catch
            {
                return true;
            }
        }

        private static void UpdateStuck(Character player, TargetSense sense)
        {
            Vector3 pos = player.transform.position;
            if (_lastPlayerPos == Vector3.zero)
            {
                _lastPlayerPos = pos;
                return;
            }

            float moved = Mathf.Sqrt(XZDistanceSq(pos, _lastPlayerPos));
            float destDist = Mathf.Sqrt(XZDistanceSq(pos, _destination));
            float cornerDist = 0f;
            if (Path.Count > 0 && _pathIndex >= 0 && _pathIndex < Path.Count)
                cornerDist = Mathf.Sqrt(XZDistanceSq(pos, Path[_pathIndex]));
            bool unresolvedSeek = State == AutoBattleState.Seek && sense != null &&
                                  !sense.StrictFireLineOfSight && sense.Distance > 3.0f;
            bool routeStillNeedsMovement = cornerDist > CornerReachDistance;
            if ((destDist > 2f || routeStillNeedsMovement || unresolvedSeek) && moved < 0.03f)
                _stuckTime += Time.deltaTime;
            else
                _stuckTime = Mathf.Max(0f, _stuckTime - Time.deltaTime * 0.5f);
            _lastPlayerPos = pos;
        }

        private static bool AimAt(Character player, Character target, Camera cam, TargetSense sense)
        {
            try
            {
                if (player == null || player.camera == null || cam == null || target == null) return false;
                if (sense == null || !sense.Visible)
                {
                    LogAim("engage", target, 0f, 0f, 0f, 0f, true);
                    return false;
                }
                Vector3 aimBase = sense.FireLineOfSight ? sense.FirePoint : sense.AimPoint;
                bool allowLower = !sense.FireLineOfSight || (aimBase - sense.AimPoint).sqrMagnitude < 0.16f;
                Vector3 aimPoint = SmoothAimPoint(target, GetAimPoint(target, cam, aimBase, allowLower));
                float tolerance = AccuracyMode == 0 ? 4.2f : (AccuracyMode == 1 ? 6.2f : 8.4f);
                return ApplySmoothLook(player, cam, aimPoint, LookIntentKind.Engage, tolerance, tolerance, false);
            }
            catch (Exception ex)
            {
                FileLogger.Log("AUTO-BATTLE", "aim failed: " + ex.Message);
                return false;
            }
        }

        private static Vector3 SmoothAimPoint(Character target, Vector3 rawPoint)
        {
            if (!_hasSmoothedAimPoint || _smoothedAimTarget != target)
            {
                _smoothedAimTarget = target;
                _smoothedAimPoint = rawPoint;
                _hasSmoothedAimPoint = true;
                return rawPoint;
            }

            float dt = Mathf.Clamp(Time.deltaTime, 0.001f, 0.050f);
            int accuracy = Mathf.Clamp(AccuracyMode, 0, AccuracyNames.Length - 1);
            float follow = accuracy == 0 ? 18.0f : (accuracy == 1 ? 15.0f : 12.0f);
            float blend = 1f - Mathf.Exp(-follow * dt);
            Vector3 lerped = Vector3.Lerp(_smoothedAimPoint, rawPoint, Mathf.Clamp01(blend));
            float maxStep = (accuracy == 0 ? 9.5f : (accuracy == 1 ? 7.0f : 5.0f)) * dt;
            _smoothedAimPoint = Vector3.MoveTowards(_smoothedAimPoint, lerped, Mathf.Max(0.004f, maxStep));
            return _smoothedAimPoint;
        }

        private static Vector3 SmoothSeekLookPoint(Camera cam, Vector3 point, float maxTurnSpeed)
        {
            if (cam == null || cam.transform == null) return point;
            Vector3 rawDir = point - cam.transform.position;
            float rawDist = rawDir.magnitude;
            if (rawDist < 0.05f) return point;
            rawDir /= rawDist;

            if (!_hasSmoothedSeekLookPoint || _smoothedSeekLookDir.sqrMagnitude < 0.01f)
            {
                _smoothedSeekLookDir = cam.transform.forward;
                if (_smoothedSeekLookDir.sqrMagnitude < 0.01f) _smoothedSeekLookDir = rawDir;
                _smoothedSeekLookDir.Normalize();
                _smoothedSeekLookDistance = Mathf.Clamp(rawDist, 5.5f, 16.0f);
                _hasSmoothedSeekLookPoint = true;
            }

            float dt = Mathf.Clamp(Time.deltaTime, 0.001f, 0.050f);
            float maxTurnDeg = Mathf.Max(30f, maxTurnSpeed) * dt;
            _smoothedSeekLookDir = Vector3.RotateTowards(_smoothedSeekLookDir, rawDir, maxTurnDeg * Mathf.Deg2Rad, 0f);
            if (_smoothedSeekLookDir.sqrMagnitude < 0.01f) _smoothedSeekLookDir = rawDir;
            _smoothedSeekLookDir.Normalize();

            float targetDist = Mathf.Clamp(rawDist, 5.5f, 16.0f);
            float distBlend = 1f - Mathf.Exp(-6.0f * dt);
            _smoothedSeekLookDistance = Mathf.Lerp(_smoothedSeekLookDistance <= 0f ? targetDist : _smoothedSeekLookDistance, targetDist, distBlend);
            _smoothedSeekLookPoint = cam.transform.position + _smoothedSeekLookDir * _smoothedSeekLookDistance;
            return _smoothedSeekLookPoint;
        }

        private static bool LookAtPoint(Character player, Camera cam, Vector3 point, LookIntentKind intent)
        {
            try
            {
                if (player == null || player.camera == null || cam == null) return false;
                float tolerance = 8.0f;
                Vector3 finalPoint = point;
                if (intent == LookIntentKind.Seek || intent == LookIntentKind.Route || intent == LookIntentKind.Glance)
                {
                    bool previousNavigationMode = _lastLookMode == "seek" || _lastLookMode == "route" || _lastLookMode == "glance";
                    if (!previousNavigationMode) _hasSmoothedSeekLookPoint = false;
                    finalPoint = SmoothSeekLookPoint(cam, point, intent == LookIntentKind.Glance ? 260f : 300f);
                }
                else if (intent == LookIntentKind.Roam)
                {
                    if (_lastLookMode != "roam") _hasSmoothedSeekLookPoint = false;
                    finalPoint = SmoothSeekLookPoint(cam, point, 190f);
                }

                if ((intent != LookIntentKind.Seek && intent != LookIntentKind.Route && intent != LookIntentKind.Glance) || _lastLookIntentFrame != Time.frameCount)
                {
                    _lastTargetRouteDelta = -1f;
                    _lastLookIntentDelta = -1f;
                }

                return ApplySmoothLook(player, cam, finalPoint, intent, tolerance, tolerance,
                    intent == LookIntentKind.Seek || intent == LookIntentKind.Route || intent == LookIntentKind.Glance);
            }
            catch
            {
                return false;
            }
        }

        private static bool ApplySmoothLook(Character player, Camera cam, Vector3 point, LookIntentKind intent, float yawTolerance, float pitchTolerance, bool aimBlockedByVisibility)
        {
            Vector3 dir = point - cam.transform.position;
            if (dir.sqrMagnitude < 0.01f) return false;
            if ((intent != LookIntentKind.Seek && intent != LookIntentKind.Route && intent != LookIntentKind.Glance) || _lastLookIntentFrame != Time.frameCount)
            {
                _lastTargetRouteDelta = -1f;
                _lastLookIntentDelta = -1f;
            }

            Vector3 desired = Quaternion.LookRotation(dir.normalized).eulerAngles;
            float currentYaw = player.camera.finalx;
            float currentPitch = Mathf.Clamp(-player.camera.finaly + CameraBasePitchOffset, CameraMinActualPitch, CameraMaxActualPitch);
            float desiredYaw = desired.y;
            float desiredPitch = Mathf.Clamp(Mathf.DeltaAngle(0f, desired.x), CameraMinActualPitch, CameraMaxActualPitch);
            float yaw = Mathf.DeltaAngle(currentYaw, desiredYaw);
            float pitch = desiredPitch - currentPitch;

            float yawSmoothTime;
            float pitchSmoothTime;
            float maxYaw;
            float maxPitch;
            float deadYaw;
            float deadPitch;
            switch (intent)
            {
                case LookIntentKind.Engage:
                    yawSmoothTime = 0.060f;
                    pitchSmoothTime = 0.072f;
                    maxYaw = 370f;
                    maxPitch = 260f;
                    deadYaw = 0.12f;
                    deadPitch = 0.10f;
                    break;
                case LookIntentKind.Roam:
                    yawSmoothTime = 0.160f;
                    pitchSmoothTime = 0.180f;
                    maxYaw = 190f;
                    maxPitch = 120f;
                    deadYaw = 0.45f;
                    deadPitch = 0.38f;
                    break;
                case LookIntentKind.RocketJump:
                    yawSmoothTime = 0.070f;
                    pitchSmoothTime = 0.080f;
                    maxYaw = 340f;
                    maxPitch = 240f;
                    deadYaw = 0.10f;
                    deadPitch = 0.08f;
                    break;
                default:
                    yawSmoothTime = 0.120f;
                    pitchSmoothTime = 0.150f;
                    maxYaw = 240f;
                    maxPitch = 150f;
                    deadYaw = 0.35f;
                    deadPitch = 0.30f;
                    break;
            }

            float dt = Mathf.Clamp(Time.deltaTime, 0.001f, 0.050f);
            float yawTarget = Mathf.Abs(yaw) <= deadYaw ? currentYaw : desiredYaw;
            float pitchTarget = Mathf.Abs(pitch) <= deadPitch ? currentPitch : desiredPitch;
            float nextYaw = Mathf.SmoothDampAngle(currentYaw, yawTarget, ref _lookYawVelocity, yawSmoothTime, maxYaw, dt);
            float nextPitch = Mathf.SmoothDamp(currentPitch, pitchTarget, ref _lookPitchVelocity, pitchSmoothTime, maxPitch, dt);

            float stepYaw = Mathf.Clamp(Mathf.DeltaAngle(currentYaw, nextYaw), -maxYaw * dt, maxYaw * dt);
            float stepPitch = Mathf.Clamp(nextPitch - currentPitch, -maxPitch * dt, maxPitch * dt);
            nextYaw = currentYaw + stepYaw;
            nextPitch = Mathf.Clamp(currentPitch + stepPitch, CameraMinActualPitch, CameraMaxActualPitch);
            _lastLookStepYaw = stepYaw;
            _lastLookStepPitch = stepPitch;
            _lastLookControlYaw = nextYaw;
            _lastLookControlPitch = nextPitch;
            _lastLookDesiredYaw = desiredYaw;
            _lastLookDesiredPitch = desiredPitch;
            _lastLookIntent = intent.ToString().ToLowerInvariant();
            _lastLookMode = _lastLookIntent;

            player.camera.finalx = nextYaw;
            player.camera.finaly = Mathf.Clamp(CameraBasePitchOffset - nextPitch, -75f, 48f);

            float remainingYaw = Mathf.DeltaAngle(nextYaw, desiredYaw);
            float remainingPitch = desiredPitch - nextPitch;
            bool ready = Mathf.Abs(remainingYaw) <= yawTolerance && Mathf.Abs(remainingPitch) <= pitchTolerance;
            if (!ready)
            {
                if (_lookSettlingSince <= 0f) _lookSettlingSince = Time.time;
            }
            else if (!_lastAimReady && _lookSettlingSince > 0f)
            {
                _lastAimSettleMs = Mathf.Max(0f, (Time.time - _lookSettlingSince) * 1000f);
                _lookSettlingSince = 0f;
            }
            _lastAimReady = ready;

            LogAim(_lastLookIntent, _target, remainingYaw, remainingPitch, stepYaw, stepPitch, aimBlockedByVisibility);
            return ready;
        }

        private static void LogAim(string mode, Character target, float yaw, float pitch, float stepYaw, float stepPitch, bool aimBlockedByVisibility)
        {
            if (!DebugLog) return;
            if (Time.time < _nextAimDiagLogTime) return;
            _nextAimDiagLogTime = Time.time + AimDiagInterval;
            string switched = Time.time - _lastTargetSwitchAt < 0.60f ? "1" : "0";
            string locked = target != null && target == _target ? "1" : "0";
            FileLogger.Log("AUTO-BATTLE][AIM",
                "mode=" + mode +
                " target=" + SafeTargetName(target) +
                " locked=" + locked +
                " switched=" + switched +
                 " switchReason=" + _lastTargetSwitchReason +
                 " strictFireLos=" + (_lastStrictFireLine ? "1" : "0") +
                 " yaw=" + yaw.ToString("0.0") +
                 " pitch=" + pitch.ToString("0.0") +
                 " controlYaw=" + _lastLookControlYaw.ToString("0.0") +
                 " controlPitch=" + _lastLookControlPitch.ToString("0.0") +
                 " desiredYaw=" + _lastLookDesiredYaw.ToString("0.0") +
                 " desiredPitch=" + _lastLookDesiredPitch.ToString("0.0") +
                 " yawVel=" + _lookYawVelocity.ToString("0.0") +
                 " pitchVel=" + _lookPitchVelocity.ToString("0.0") +
                 " stepYaw=" + stepYaw.ToString("0.00") +
                 " stepPitch=" + stepPitch.ToString("0.00") +
                 " targetRouteDelta=" + _lastTargetRouteDelta.ToString("0.0") +
                 " intentDelta=" + _lastLookIntentDelta.ToString("0.0") +
                 " settleMs=" + _lastAimSettleMs.ToString("0") +
                 " aimBlockedByVisibility=" + (aimBlockedByVisibility ? "1" : "0"));
        }

        private static bool TryRunRoleSkillTactics(Character player, Character target, Camera cam, TargetSense sense, int exposure, int aimingThreats)
        {
            if (Time.time < _nextRoleSkillTime) return false;
            if (player == null || target == null || sense == null) return false;
            bool targetInvincible = sense.Invincible;

            if (IsHeavyRole(player) && sense.VisibleByGame)
            {
                float hpPct = HealthPercent(player);
                if ((sense.Visible || sense.Distance <= 28.0f || hpPct <= 85f) &&
                    TryUseReadySkillSubtype(player, 1, "heavy_shield_contact"))
                    return false;

                if ((sense.Visible || sense.Distance <= 35.0f) &&
                    TryUseReadySkillSubtype(player, 4, "heavy_gallop_contact"))
                    return false;

                if ((hpPct > 0f && hpPct <= 70f) &&
                    TryUseReadySkillSubtype(player, 7, "heavy_tenacity_lowhp"))
                    return false;
            }

            if (IsMedicGuardRole(player))
            {
                float hpPct = HealthPercent(player);
                if (hpPct > 0f && hpPct <= 58f && TryUseReadySkillSubtype(player, 0, "medic_heal_self"))
                    return true;

                if ((hpPct > 0f && hpPct <= 72f) || exposure >= 3 || aimingThreats >= 2)
                {
                    if (TryUseReadySkillSubtype(player, 14, "medic_capsule_self_or_crowd"))
                        return true;
                }

                SkillInfo arrowRain = FindReadySkill(player, 9);
                if (!targetInvincible && arrowRain != null && sense.Visible && sense.FireLineOfSight)
                {
                    AutoBattleInput.ClearMovement();
                    bool aimReady = AimAt(player, target, cam, sense);
                    LastAction = "medic_arrow_rain_aim";
                    if (aimReady)
                    {
                        TryUseSkill(arrowRain, "medic_arrow_rain");
                        return true;
                    }
                    return true;
                }
            }

            if (!targetInvincible && IsAssaultSniperRole(player) && sense.Distance <= 4.2f)
            {
                if (TryUseReadySkillSubtype(player, 11, "assault_spurt_melee"))
                    return true;
            }

            return false;
        }

        private static bool IsConfirmedSniperAttack(TargetSense sense)
        {
            return sense != null && sense.Visible && sense.VisibleByGame && sense.OnScreen &&
                   sense.StrictFireLineOfSight && !sense.Invincible;
        }

        private static bool TryCloseSniperScope(Character player, string reason)
        {
            SniperGunController sniper = player == null ? null : player.mWeapon as SniperGunController;
            if (sniper == null) return true;
            try
            {
                if (sniper.currentSight == 0)
                {
                    _nextSniperScopeTime = 0f;
                    return true;
                }

                if (Time.time >= _nextSniperScopeTime)
                {
                    AutoBattleInput.PressAction(ActionType.kActionSecondFire, 0.10f);
                    _nextSniperScopeTime = Time.time + 0.40f;
                    FileLogger.Log("AUTO-BATTLE][ROLE", "sniper scope close requested reason=" + reason);
                }
                return false;
            }
            catch (Exception ex)
            {
                FileLogger.Log("AUTO-BATTLE][ROLE", "sniper scope close failed ex=" + ex.GetType().Name + ":" + ex.Message);
                return false;
            }
        }

        private static bool TryRunRoleWeaponTactics(Character player, Character target, Camera cam, TargetSense sense, bool assaultSniper, bool sniperMeleeEmergency)
        {
            if (player == null || target == null || sense == null) return false;

            if (IsHeavyRole(player) && TryFireTimedSpecialWeapon(player, target, cam, sense, WeaponType.kWeaponTypeRPG, ref _nextRocketPokeTime, RocketPokeInterval, "heavy_rpg"))
                return true;

            if (IsMedicGuardRole(player) && TryFireTimedSpecialWeapon(player, target, cam, sense, WeaponType.kWeaponTypeBow, ref _nextBowPokeTime, BowPokeInterval, "medic_bow"))
                return true;

            if (assaultSniper)
            {
                if (sniperMeleeEmergency)
                {
                    WeaponBase knife = FindWeaponByType(player, WeaponType.kWeaponTypeKnife, false);
                    if (knife != null && sense.Distance <= 2.8f)
                    {
                        if (player.mWeapon != knife)
                        {
                            SwitchWeapon(player, knife, "assault_knife_melee");
                            LastAction = "assault_knife_switch";
                            return true;
                        }
                        bool knifeAim = AimAt(player, target, cam, sense);
                        if (knifeAim && ShouldFire(player, target, sense, true))
                        {
                            AutoBattleInput.RequestFire(UnityEngine.Random.Range(0.08f, 0.14f));
                            _nextFireTime = Time.time + UnityEngine.Random.Range(0.18f, 0.28f);
                            LastAction = "assault_knife_fire";
                            return true;
                        }
                    }

                    // No knife or not close enough: keep normal gunfire while movement code creates distance.
                    return false;
                }

                if (!IsConfirmedSniperAttack(sense))
                {
                    TryCloseSniperScope(player, "attack_not_confirmed");
                    LastAction = "assault_sniper_search_unscoped";
                    return true;
                }

                WeaponBase sniper = FindWeaponByType(player, WeaponType.kWeaponTypeSniperGun, true);
                if (sniper == null) return false;
                if (player.mWeapon != sniper)
                {
                    SwitchWeapon(player, sniper, "assault_sniper_prefer");
                    LastAction = "assault_sniper_switch";
                    return true;
                }

                SniperGunController sniperController = sniper as SniperGunController;
                if (sniperController != null && sniperController.currentSight == 0)
                {
                    if (Time.time >= _nextSniperScopeTime)
                    {
                        AutoBattleInput.PressAction(ActionType.kActionSecondFire, 0.10f);
                        _nextSniperScopeTime = Time.time + 0.40f;
                        FileLogger.Log("AUTO-BATTLE][ROLE", "sniper scope requested");
                    }
                    LastAction = "assault_sniper_scope_wait";
                    return true;
                }
                _nextSniperScopeTime = 0f;
                bool aimReady = AimAt(player, target, cam, sense);
                LastAction = "assault_sniper_scope";
                if (aimReady && ShouldFire(player, target, sense, true))
                {
                    AutoBattleInput.RequestFire(UnityEngine.Random.Range(0.06f, 0.10f));
                    _nextFireTime = Time.time + NextFireDelayForWeapon(CurrentWeaponType(player));
                    LastAction = "assault_sniper_fire";
                }
                return true;
            }

            return false;
        }

        private static bool TryFireTimedSpecialWeapon(Character player, Character target, Camera cam, TargetSense sense, WeaponType type, ref float nextTime, float interval, string label)
        {
            if (Time.time < nextTime)
            {
                if (player != null && player.mWeapon != null && GetWeaponTypeSafe(player.mWeapon) == type)
                    TrySwitchBestNonSpecialOrFallbackWeapon(player, sense, label + "_cooling_yield");
                return false;
            }
            if (sense == null || !sense.Visible || !sense.FireLineOfSight) return false;
            if (type == WeaponType.kWeaponTypeRPG && sense.Distance < 6.5f) return false;
            if (type == WeaponType.kWeaponTypeBow && sense.Distance < 4.0f) return false;

            WeaponBase weapon = FindWeaponByType(player, type, true);
            if (weapon == null)
            {
                if (player != null && player.mWeapon != null && GetWeaponTypeSafe(player.mWeapon) == type)
                    TrySwitchBestNonSpecialOrFallbackWeapon(player, sense, label + "_not_ready_yield");
                nextTime = Time.time + 0.75f;
                return false;
            }

            if (player.mWeapon != weapon)
            {
                SwitchWeapon(player, weapon, label + "_switch");
                LastAction = label + "_switch";
                return true;
            }

            bool aimReady = AimAt(player, target, cam, sense);
            LastAction = label + "_aim";
            if (aimReady && ShouldFire(player, target, sense, true))
            {
                AutoBattleInput.RequestFire(UnityEngine.Random.Range(0.065f, 0.11f));
                _nextFireTime = Time.time + NextFireDelayForWeapon(type);
                nextTime = Time.time + interval + UnityEngine.Random.Range(0f, 0.65f);
                LastAction = label + "_fire";
                FileLogger.Log("AUTO-BATTLE][ROLE", label + " fired clip=" + GetWeaponClip(weapon));
                _nextWeaponDecisionTime = 0f;
                return true;
            }

            if (!aimReady || sense.Distance <= CloseEngageDistance + 2.0f || UnityEngine.Random.value < 0.35f)
            {
                TrySwitchBestNonSpecialOrFallbackWeapon(player, sense, label + "_fallback");
                _nextWeaponDecisionTime = Time.time + 0.08f;
                nextTime = Time.time + 0.75f;
                LastAction = label + "_fallback";
                return false;
            }

            return true;
        }

        private static bool TryUseReadySkillSubtype(Character player, int subType, string reason)
        {
            SkillInfo skill = FindReadySkill(player, subType);
            if (skill == null) return false;
            return TryUseSkill(skill, reason);
        }

        private static bool TryUseSkill(SkillInfo skill, string reason)
        {
            try
            {
                if (skill == null || !skill.cool_down_ready) return false;
                if (!skill.CanAction()) return false;
                bool ok = skill.Action();
                if (ok)
                {
                    skill.cool_down_ready = false;
                    _nextRoleSkillTime = Time.time + RoleSkillInterval;
                    LastAction = reason;
                    FileLogger.Log("AUTO-BATTLE][ROLE", "skill reason=" + reason + " slot=" + skill.slot + " subtype=" + skill.sub_type);
                    return true;
                }
            }
            catch (Exception ex)
            {
                FileLogger.Log("AUTO-BATTLE][ROLE", "skill failed reason=" + reason + " ex=" + ex.GetType().Name + ":" + ex.Message);
            }
            return false;
        }

        private static SkillInfo FindReadySkill(Character player, int subType)
        {
            ObjectBaseInfo[] slots = GetPlayerSlots(player);
            if (slots == null) return null;
            for (int i = 0; i < slots.Length; i++)
            {
                SkillInfo skill = slots[i] as SkillInfo;
                if (skill == null) continue;
                if (skill.sub_type != (byte)subType) continue;
                if (!skill.cool_down_ready) continue;
                return skill;
            }
            return null;
        }

        private static ObjectBaseInfo[] GetPlayerSlots(Character player)
        {
            try
            {
                if (player == null || player.character_info == null || player.character_info.slots_info == null)
                    return null;
                return player.character_info.slots_info.object_info;
            }
            catch
            {
                return null;
            }
        }

        private static string DetectCurrentRole(Character player)
        {
            if (IsHeavyRole(player)) return "重装";
            if (IsMedicGuardRole(player)) return "医疗/守护";
            if (IsAssaultSniperRole(player)) return "突击/狙击";
            return "通用";
        }

        private static bool IsHeavyRole(Character player)
        {
            return FindWeaponByType(player, WeaponType.kWeaponTypeRPG, false) != null;
        }

        private static bool IsMedicGuardRole(Character player)
        {
            if (FindWeaponByType(player, WeaponType.kWeaponTypeBow, false) != null) return true;
            if (FindReadyOrEquippedSkill(player, 0) != null) return true;
            if (FindReadyOrEquippedSkill(player, 9) != null) return true;
            if (FindReadyOrEquippedSkill(player, 14) != null) return true;
            return false;
        }

        private static bool IsAssaultSniperRole(Character player)
        {
            if (IsHeavyRole(player) || IsMedicGuardRole(player)) return false;
            return FindWeaponByType(player, WeaponType.kWeaponTypeSniperGun, false) != null;
        }

        private static SkillInfo FindReadyOrEquippedSkill(Character player, int subType)
        {
            ObjectBaseInfo[] slots = GetPlayerSlots(player);
            if (slots == null) return null;
            for (int i = 0; i < slots.Length; i++)
            {
                SkillInfo skill = slots[i] as SkillInfo;
                if (skill != null && skill.sub_type == (byte)subType) return skill;
            }
            return null;
        }

        private static WeaponBase FindWeaponByType(Character player, WeaponType type, bool requireAmmo)
        {
            try
            {
                if (player == null || player.weaponlist == null) return null;
                for (int i = 0; i < player.weaponlist.Count; i++)
                {
                    WeaponBase weapon = player.weaponlist[i];
                    if (weapon == null || weapon.info == null) continue;
                    if (GetWeaponTypeSafe(weapon) != type) continue;
                    if (requireAmmo && !IsWeaponReadyForShot(weapon)) continue;
                    return weapon;
                }
            }
            catch
            {
            }
            return null;
        }

        private static bool IsWeaponReadyForShot(WeaponBase weapon)
        {
            try
            {
                if (weapon == null || weapon.info == null) return false;
                if (weapon.reloading) return false;
                if (!IsWeaponCooldownReady(weapon)) return false;
                if (IsFallbackMeleeOrShieldWeapon(weapon)) return true;
                return weapon.clip > 0;
            }
            catch
            {
                return false;
            }
        }

        private static bool IsWeaponCooldownReady(WeaponBase weapon)
        {
            try
            {
                if (weapon == null || weapon.info == null) return false;
                if (weapon.reloading) return false;
                if (!weapon.cool_down_ready || !weapon.info.cool_down_ready) return false;
                if ((float)weapon.info.cooling > 0f) return false;
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static bool IsFallbackMeleeOrShieldReady(WeaponBase weapon)
        {
            return IsFallbackMeleeOrShieldWeapon(weapon) && IsWeaponCooldownReady(weapon);
        }

        private static bool IsFallbackMeleeOrShieldWeapon(WeaponBase weapon)
        {
            if (weapon == null) return false;
            WeaponType type = GetWeaponTypeSafe(weapon);
            if (type == WeaponType.kWeaponTypeKnife || type == WeaponType.kWeaponTypeDualWeapon) return true;
            if (weapon is KnifeBaseController) return true;
            return IsShieldFallbackWeapon(weapon);
        }

        private static bool IsShieldFallbackWeapon(WeaponBase weapon)
        {
            try
            {
                if (weapon == null) return false;
                if (GetWeaponTypeSafe(weapon) == WeaponType.kWeaponTypeDualWeapon) return true;
                string label = WeaponLabel(weapon);
                return !string.IsNullOrEmpty(label) && label.IndexOf("shield", StringComparison.OrdinalIgnoreCase) >= 0;
            }
            catch
            {
                return false;
            }
        }

        private static WeaponType GetWeaponTypeSafe(WeaponBase weapon)
        {
            try
            {
                if (weapon == null || weapon.info == null) return WeaponType.kWeaponTypeNone;
                return weapon.GetWeaponType();
            }
            catch
            {
                return WeaponType.kWeaponTypeNone;
            }
        }

        private static WeaponType CurrentWeaponType(Character player)
        {
            try
            {
                return player != null && player.mWeapon != null ? GetWeaponTypeSafe(player.mWeapon) : WeaponType.kWeaponTypeNone;
            }
            catch
            {
                return WeaponType.kWeaponTypeNone;
            }
        }

        private static bool SwitchWeapon(Character player, WeaponBase weapon, string reason)
        {
            try
            {
                if (player == null || weapon == null || weapon.info == null) return false;
                if (player.mWeapon == weapon) return true;
                player.ChangeWeapon(Convert.ToInt32(weapon.info.slot));
                _nextWeaponDecisionTime = Time.time + 0.55f;
                FileLogger.Log("AUTO-BATTLE][ROLE", "switch reason=" + reason + " to=" + WeaponLabel(weapon));
                return true;
            }
            catch (Exception ex)
            {
                FileLogger.Log("AUTO-BATTLE][ROLE", "switch failed reason=" + reason + " ex=" + ex.GetType().Name + ":" + ex.Message);
                return false;
            }
        }

        private static void ManageWeapon(Character player, TargetSense sense)
        {
            if (Time.time < _nextWeaponDecisionTime) return;
            _nextWeaponDecisionTime = Time.time + WeaponDecisionInterval;
            if (player == null || player.mWeapon == null) return;

            try
            {
                WeaponBase current = player.mWeapon;
                WeaponType currentType = GetWeaponTypeSafe(current);
                if (currentType == WeaponType.kWeaponTypeRPG &&
                    (!IsWeaponReadyForShot(current) || Time.time < _nextRocketPokeTime - 0.05f))
                {
                    TrySwitchBestNonSpecialOrFallbackWeapon(player, sense, "rpg_cooling_or_waiting");
                    return;
                }
                if (currentType == WeaponType.kWeaponTypeBow &&
                    (!IsWeaponReadyForShot(current) || Time.time < _nextBowPokeTime - 0.05f))
                {
                    TrySwitchBestNonSpecialOrFallbackWeapon(player, sense, "bow_cooling_or_waiting");
                    return;
                }

                GunInfo currentGun = current.info as GunInfo;
                if (currentGun == null)
                {
                    TrySwitchBestWeapon(player, sense, "non_gun");
                    return;
                }
                if (current.reloading)
                {
                    TrySwitchReadyPriorityWeaponWhileReloading(player, sense, "reload_interrupt");
                    return;
                }

                int clip = current.clip;
                int clipMax = Mathf.Max(0, (int)currentGun.ammo_one_clip);
                int clipPct = PercentInt(clip, clipMax);
                bool inDanger = sense != null && sense.Visible && sense.Distance <= SafeReloadDistance;

                if (clipMax > 0 && clip <= 0)
                {
                    if (!TrySwitchBestWeapon(player, sense, "empty") &&
                        !TrySwitchFallbackMeleeOrShieldWeapon(player, sense, "empty_fallback"))
                        TryReloadCurrentWeapon(player, "empty");
                    return;
                }

                if (sense != null && sense.Visible)
                {
                    TrySwitchBestWeapon(player, sense, "better_fit");
                }

                if (clipMax > 0 && clipPct <= (inDanger ? 18 : 45))
                {
                    if (inDanger)
                    {
                        TrySwitchBestWeapon(player, sense, "low_clip_danger");
                    }
                    else
                    {
                        TryReloadCurrentWeapon(player, "safe_low_clip");
                    }
                }
            }
            catch (Exception ex)
            {
                FileLogger.Log("AUTO-BATTLE][WEAPON", "manage failed: " + ex.GetType().Name + ":" + ex.Message);
            }
        }

        private static bool TrySwitchReadyPriorityWeaponWhileReloading(Character player, TargetSense sense, string reason)
        {
            if (player == null || player.mWeapon == null) return false;

            bool combatReady = sense != null && sense.Visible && sense.FireLineOfSight && !IsInvincibleTarget(sense.Target);
            if (combatReady)
            {
                WeaponBase rpg = FindWeaponByType(player, WeaponType.kWeaponTypeRPG, true);
                if (rpg != null && rpg != player.mWeapon && sense.Distance >= 6.5f)
                {
                    _nextRocketPokeTime = 0f;
                    return SwitchWeapon(player, rpg, reason + "_rpg");
                }

                WeaponBase bow = FindWeaponByType(player, WeaponType.kWeaponTypeBow, true);
                if (bow != null && bow != player.mWeapon && sense.Distance >= 4.0f)
                {
                    _nextBowPokeTime = 0f;
                    return SwitchWeapon(player, bow, reason + "_bow");
                }
            }

            if (TrySwitchBestNonSpecialWeapon(player, sense, reason + "_primary"))
                return true;

            return TrySwitchFallbackMeleeOrShieldWeapon(player, sense, reason + "_fallback");
        }

        private static bool TrySwitchBestNonSpecialWeapon(Character player, TargetSense sense, string reason)
        {
            if (player == null || player.weaponlist == null || player.mWeapon == null) return false;
            WeaponBase current = player.mWeapon;
            WeaponBase best = null;
            float bestScore = -9999f;

            for (int i = 0; i < player.weaponlist.Count; i++)
            {
                WeaponBase weapon = player.weaponlist[i];
                if (weapon == null || weapon == current) continue;
                if (!IsUsableAutoBattleWeapon(weapon)) continue;
                WeaponType type = GetWeaponTypeSafe(weapon);
                if (type == WeaponType.kWeaponTypeRPG || type == WeaponType.kWeaponTypeBow || type == WeaponType.kWeaponTypeKnife || type == WeaponType.kWeaponTypeDualWeapon)
                    continue;
                float score = ScoreWeaponForSituation(player, weapon, sense);
                if (score > bestScore)
                {
                    bestScore = score;
                    best = weapon;
                }
            }

            if (best == null) return false;
            return SwitchWeapon(player, best, reason);
        }

        private static bool TrySwitchBestNonSpecialOrFallbackWeapon(Character player, TargetSense sense, string reason)
        {
            if (TrySwitchBestNonSpecialWeapon(player, sense, reason))
                return true;
            return TrySwitchFallbackMeleeOrShieldWeapon(player, sense, reason + "_melee_or_shield");
        }

        private static bool TrySwitchFallbackMeleeOrShieldWeapon(Character player, TargetSense sense, string reason)
        {
            if (player == null || player.weaponlist == null || player.mWeapon == null) return false;

            WeaponBase current = player.mWeapon;
            if (IsFallbackMeleeOrShieldWeapon(current) && IsFallbackMeleeOrShieldReady(current))
                return true;

            WeaponBase best = null;
            float bestScore = -9999f;
            float dist = sense == null ? 99f : sense.Distance;
            bool danger = sense != null && sense.Visible && dist <= SafeReloadDistance;

            for (int i = 0; i < player.weaponlist.Count; i++)
            {
                WeaponBase weapon = player.weaponlist[i];
                if (weapon == null || weapon == current) continue;
                if (!IsFallbackMeleeOrShieldWeapon(weapon)) continue;
                if (!IsFallbackMeleeOrShieldReady(weapon)) continue;

                bool shield = IsShieldFallbackWeapon(weapon);
                float score = shield ? 24f : 10f;
                if (shield && danger) score += 10f;
                if (!shield && dist <= 3.0f) score += 16f;
                if (!shield && dist > 5.0f) score -= 8f;
                if (score > bestScore)
                {
                    bestScore = score;
                    best = weapon;
                }
            }

            if (best == null) return false;
            return SwitchWeapon(player, best, reason);
        }

        private static bool TrySwitchBestWeapon(Character player, TargetSense sense, string reason)
        {
            if (player == null || player.weaponlist == null || player.mWeapon == null) return false;
            WeaponBase current = player.mWeapon;
            float currentScore = ScoreWeaponForSituation(player, current, sense);
            WeaponBase best = null;
            float bestScore = currentScore;

            for (int i = 0; i < player.weaponlist.Count; i++)
            {
                WeaponBase weapon = player.weaponlist[i];
                if (weapon == null || weapon == current) continue;
                if (!IsUsableAutoBattleWeapon(weapon)) continue;
                float score = ScoreWeaponForSituation(player, weapon, sense);
                if (score > bestScore)
                {
                    bestScore = score;
                    best = weapon;
                }
            }

            int currentClip = GetWeaponClip(current);
            bool force = reason == "empty" || reason == "low_clip_danger";
            if (best == null) return false;
            if (!force && bestScore < currentScore + 8.0f) return false;

            try
            {
                int slot = Convert.ToInt32(best.info.slot);
                if (slot <= 0) return false;
                player.ChangeWeapon(slot);
                _nextWeaponDecisionTime = Time.time + 0.85f;
                FileLogger.Log("AUTO-BATTLE][WEAPON",
                    "switch reason=" + reason +
                    " from=" + WeaponLabel(current) +
                    " to=" + WeaponLabel(best) +
                    " curClip=" + currentClip +
                    " score=" + bestScore.ToString("0.0"));
                return true;
            }
            catch (Exception ex)
            {
                FileLogger.Log("AUTO-BATTLE][WEAPON", "switch failed reason=" + reason + " ex=" + ex.GetType().Name + ":" + ex.Message);
                return false;
            }
        }

        private static bool TryReloadCurrentWeapon(Character player, string reason)
        {
            try
            {
                if (player == null || player.mWeapon == null) return false;
                WeaponBase weapon = player.mWeapon;
                GunInfo gun = weapon.info as GunInfo;
                if (gun == null || weapon.reloading) return false;
                if (weapon.clip >= (int)gun.ammo_one_clip) return false;
                weapon.Reload();
                _nextWeaponDecisionTime = Time.time + 1.10f;
                FileLogger.Log("AUTO-BATTLE][WEAPON", "reload reason=" + reason + " weapon=" + WeaponLabel(weapon) + " clip=" + weapon.clip + "/" + ((int)gun.ammo_one_clip));
                return true;
            }
            catch (Exception ex)
            {
                FileLogger.Log("AUTO-BATTLE][WEAPON", "reload failed reason=" + reason + " ex=" + ex.GetType().Name + ":" + ex.Message);
                return false;
            }
        }

        private static bool CanCurrentWeaponFire(Character player, TargetSense sense)
        {
            try
            {
                if (player == null || player.mWeapon == null) return false;
                WeaponBase weapon = player.mWeapon;
                if (weapon.reloading) return false;
                if (!IsWeaponCooldownReady(weapon)) return false;
                if (weapon is KnifeBaseController) return sense != null && sense.Distance <= 2.2f;
                GunInfo gun = weapon.info as GunInfo;
                if (gun == null) return true;
                if (weapon.clip <= 0) return false;
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static bool IsUsableAutoBattleWeapon(WeaponBase weapon)
        {
            try
            {
                if (weapon == null || weapon.info == null) return false;
                if (weapon.reloading) return false;
                if (!IsWeaponCooldownReady(weapon)) return false;
                if (IsFallbackMeleeOrShieldWeapon(weapon)) return false;
                GunInfo gun = weapon.info as GunInfo;
                if (gun == null) return false;
                if ((int)gun.ammo_one_clip > 0 && weapon.clip <= 0) return false;
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static float ScoreWeaponForSituation(Character player, WeaponBase weapon, TargetSense sense)
        {
            if (!IsUsableAutoBattleWeapon(weapon)) return -9999f;
            GunInfo gun = weapon.info as GunInfo;
            float dist = sense == null ? 12f : sense.Distance;
            int clipMax = gun == null ? 0 : Mathf.Max(0, (int)gun.ammo_one_clip);
            int clipPct = PercentInt(weapon.clip, clipMax);
            WeaponType weaponType = GetWeaponTypeSafe(weapon);
            float roleBias = MedicGuardWeaponBias(player, weapon, weaponType);
            if (weaponType == WeaponType.kWeaponTypeRPG || weaponType == WeaponType.kWeaponTypeBow)
                return -70f + clipPct * 0.02f;
            if (weaponType == WeaponType.kWeaponTypeSniperGun)
                return 8f + clipPct * 0.10f + (dist >= 13f ? 22f : -8f) + roleBias;
            if (weaponType == WeaponType.kWeaponTypeShotGun)
                return 8f + clipPct * 0.10f + (dist <= 7f ? 22f : -14f) + roleBias;
            if (weaponType == WeaponType.kWeaponTypeMachineGun || weaponType == WeaponType.kWeaponTypeSubMachineGun || weaponType == WeaponType.kWeaponTypeDualWeapon)
                return 8f + clipPct * 0.10f + (dist <= 18f ? 12f : 7f) + roleBias;
            if (weaponType == WeaponType.kWeaponTypePistol)
                return 8f + clipPct * 0.10f + (dist <= 12f ? 7f : 1f) + roleBias;
            string label = WeaponLabel(weapon).ToLowerInvariant();

            float score = 8f + clipPct * 0.10f;
            if (label.IndexOf("sniper") >= 0 || label.IndexOf("狙") >= 0)
                score += dist >= 13f ? 22f : -8f;
            else if (label.IndexOf("shot") >= 0 || label.IndexOf("散") >= 0)
                score += dist <= 7f ? 22f : -14f;
            else if (label.IndexOf("machine") >= 0 || label.IndexOf("rifle") >= 0 || label.IndexOf("sub") >= 0)
                score += dist <= 18f ? 12f : 7f;
            else if (label.IndexOf("rpg") >= 0 || label.IndexOf("rocket") >= 0)
                score += dist >= 8f && dist <= 23f ? 8f : -18f;
            else if (label.IndexOf("pistol") >= 0 || label.IndexOf("手枪") >= 0)
                score += dist <= 12f ? 7f : 1f;

            return score + roleBias;
        }

        private static float MedicGuardWeaponBias(Character player, WeaponBase weapon, WeaponType type)
        {
            if (!IsMedicGuardRole(player)) return 0f;
            if (type == WeaponType.kWeaponTypeMachineGun || type == WeaponType.kWeaponTypeSubMachineGun)
                return 20f;
            if (type == WeaponType.kWeaponTypeShotGun)
                return -6f;

            string label = WeaponLabel(weapon).ToLowerInvariant();
            if (label.IndexOf("rifle") >= 0 || label.IndexOf("machinegun") >= 0 || label.IndexOf("machine_gun") >= 0)
                return 20f;
            if (label.IndexOf("shotgun") >= 0 || label.IndexOf("shot_gun") >= 0)
                return -6f;
            return 0f;
        }

        private static int GetWeaponClip(WeaponBase weapon)
        {
            try
            {
                return weapon == null ? 0 : weapon.clip;
            }
            catch
            {
                return 0;
            }
        }

        private static int PercentInt(int value, int max)
        {
            if (max <= 0) return 0;
            return Mathf.Clamp(value * 100 / max, 0, 100);
        }

        private static string WeaponLabel(WeaponBase weapon)
        {
            try
            {
                if (weapon == null) return "-";
                string s = weapon.GetType().Name;
                if (!string.IsNullOrEmpty(weapon.name)) s += "/" + weapon.name;
                if (weapon.info != null)
                {
                    s += "/slot" + weapon.info.slot;
                    if (!string.IsNullOrEmpty(weapon.info.name)) s += "/" + weapon.info.name;
                }
                return s;
            }
            catch
            {
                return "-";
            }
        }

        private static bool ShouldFire(Character player, Character target, TargetSense sense, bool aimReady)
        {
            if (sense == null) return BlockFire("sense_null");
            if (IsInvincibleTarget(target)) return BlockFire("invincible");
            if (!sense.Visible) return BlockFire("not_visible");
            if (!sense.FireLineOfSight) return BlockFire("fire_los");
            if (!CanCurrentWeaponFire(player, sense)) return BlockFire("weapon_not_ready");
            if (Time.time < _nextFireTime) return BlockFire("fire_cooldown");
            WeaponType currentType = player != null && player.mWeapon != null ? GetWeaponTypeSafe(player.mWeapon) : WeaponType.kWeaponTypeNone;
            float fireRange = 120f;
            if (currentType == WeaponType.kWeaponTypeSniperGun) fireRange = 180f;
            else if (currentType == WeaponType.kWeaponTypeRPG) fireRange = 85f;
            else if (currentType == WeaponType.kWeaponTypeBow) fireRange = 85f;
            else if (currentType == WeaponType.kWeaponTypeKnife) fireRange = 3.2f;
            if (sense.Distance > fireRange) return BlockFire("range");

            bool exactCrosshair = false;
            try
            {
                exactCrosshair = AutoFire.IsCrosshairOnEnemyExact(target);
            }
            catch
            {
            }

            if (exactCrosshair) return AllowFire();

            float fallbackPixels = AccuracyMode == 0 ? 96f :
                (AccuracyMode == 1 ? 122f : 150f);
            if (sense.Distance <= CloseEngageDistance) fallbackPixels += 22f;
            if (sense.Distance >= 18f) fallbackPixels += 30f;
            if (currentType == WeaponType.kWeaponTypeShotGun) fallbackPixels += 18f;
            bool primaryGun = IsPrimaryGunType(currentType);
            bool directionalFireOk = primaryGun && sense.FireLineOfSight && sense.Distance > CloseEngageDistance;
            bool closeAimOk = sense.Distance <= ForceCloseEngageDistance && sense.ScreenDistance <= fallbackPixels + 34f;
            if (!aimReady && !closeAimOk && !directionalFireOk) return BlockFire("aim_not_ready");
            if (!directionalFireOk && !closeAimOk && sense.ScreenDistance > fallbackPixels) return BlockFire("screen_offset");

            float skipChance = AccuracyMode == 0 ? 0.030f : (AccuracyMode == 1 ? 0.065f : 0.115f);
            if (primaryGun) skipChance *= 0.55f;
            if (sense.Distance <= CloseEngageDistance) skipChance *= 0.65f;
            if (UnityEngine.Random.value < skipChance) return BlockFire("human_skip");
            return AllowFire();
        }

        private static bool BlockFire(string reason)
        {
            _lastFireBlock = reason;
            return false;
        }

        private static bool AllowFire()
        {
            _lastFireBlock = "ok";
            return true;
        }

        private static float NextFireDelay()
        {
            if (StrategyMode == 1) return UnityEngine.Random.Range(0.08f, 0.18f);
            if (StrategyMode == 2) return UnityEngine.Random.Range(0.18f, 0.34f);
            return UnityEngine.Random.Range(0.12f, 0.26f);
        }

        private static float NextFireDelayForWeapon(WeaponType type)
        {
            if (type == WeaponType.kWeaponTypeMachineGun ||
                type == WeaponType.kWeaponTypeSubMachineGun ||
                type == WeaponType.kWeaponTypeDualWeapon)
            {
                if (StrategyMode == 1) return UnityEngine.Random.Range(0.045f, 0.090f);
                if (StrategyMode == 2) return UnityEngine.Random.Range(0.080f, 0.155f);
                return UnityEngine.Random.Range(0.055f, 0.120f);
            }

            if (type == WeaponType.kWeaponTypePistol)
                return UnityEngine.Random.Range(0.095f, 0.170f);

            if (type == WeaponType.kWeaponTypeShotGun)
                return UnityEngine.Random.Range(0.160f, 0.280f);

            return NextFireDelay();
        }

        private static bool IsPrimaryGunType(WeaponType type)
        {
            return type == WeaponType.kWeaponTypeMachineGun ||
                   type == WeaponType.kWeaponTypeSubMachineGun ||
                   type == WeaponType.kWeaponTypeDualWeapon ||
                   type == WeaponType.kWeaponTypePistol ||
                   type == WeaponType.kWeaponTypeShotGun ||
                   type == WeaponType.kWeaponTypeSniperGun;
        }

        private static float HealthPercent(Character target)
        {
            try
            {
                int maxHp = 0;
                if (target.character_info != null && target.character_info.max_health > maxHp)
                    maxHp = target.character_info.max_health;
                if (target.max_health > maxHp)
                    maxHp = target.max_health;
                if (maxHp <= 0) return 100f;
                return Mathf.Clamp((float)target.hp * 100f / (float)maxHp, 0f, 100f);
            }
            catch
            {
                return 100f;
            }
        }

        private static float SafeDistance(Character player, Character target)
        {
            try
            {
                return Vector3.Distance(player.transform.position, target.transform.position);
            }
            catch
            {
                return 99999f;
            }
        }

        private static string SafeTargetName(Character target)
        {
            if (target == null) return "-";
            try
            {
                if (!string.IsNullOrEmpty(target.baseName)) return target.baseName;
                return target.name ?? "-";
            }
            catch
            {
                return "-";
            }
        }

        private static Transform SafeRoot(Character character)
        {
            try
            {
                if (character == null || character.transform == null) return null;
                return character.transform.root;
            }
            catch
            {
                return null;
            }
        }

        private static string BuildStatus(TargetSense sense, bool shieldFront, bool kite, bool tacticalAvoid, bool aimReady, bool fire, int exposure, int aimingThreats)
        {
            string action = fire ? "开火" :
                (tacticalAvoid ? (aimingThreats > 0 ? "躲弹换位" : "peek换位") :
                (!aimReady ? "修正准星" :
                (shieldFront ? "绕盾侧击" :
                (kite ? "拉开距离" : "接战"))));
            return action +
                   " | 距离 " + sense.Distance.ToString("0.0") +
                   " | 可见 " + (sense.Visible ? "是" : "否") +
                   " | 暴露 " + exposure +
                   " | 弹线 " + aimingThreats +
                   " | 路径 " + LastPath;
        }

        private static void LogMaybe(Character player, TargetSense sense, string action)
        {
            float interval = DebugLog ? LogInterval : 2.5f;
            if (Time.time < _nextLogTime) return;
            _nextLogTime = Time.time + interval;
            try
            {
                string dist = sense == null ? "-" : sense.Distance.ToString("0.0");
                string vis = sense == null ? "-" : (sense.Visible ? "1" : "0");
                string line =
                    "state=" + State +
                    " target=" + LastTarget +
                    " dist=" + dist +
                    " visible=" + vis +
                    (sense == null ? string.Empty : " visibleByGame=" + (sense.VisibleByGame ? "1" : "0") + " invincible=" + (sense.Invincible ? "1" : "0") + " los=" + (sense.LineOfSight ? "1" : "0") + " fireLos=" + (sense.FireLineOfSight ? "1" : "0") + " strictFireLos=" + (sense.StrictFireLineOfSight ? "1" : "0") + " heightDelta=" + sense.HeightDelta.ToString("0.0") + " highBlocked=" + (sense.HighGroundBlocked ? "1" : "0") + " onScreen=" + (sense.OnScreen ? "1" : "0") + " closeVisible=" + (sense.CloseVisible ? "1" : "0")) +
                   " exposure=" + _lastExposureCount +
                   " aimThreat=" + _lastAimingThreatCount +
                   " fireBlock=" + _lastFireBlock +
                   " path=" + LastPath +
                   " action=" + action;
                if (DebugLog) line += " pathDetail=" + LastPathDetail;
                FileLogger.Log("AUTO-BATTLE", line);
            }
            catch
            {
            }
        }

        private static void SetPathResult(string path, string detail, bool important)
        {
            LastPath = path;
            LastPathDetail = detail;
            if (!important && !DebugLog) return;
            if (Time.time < _nextPathDiagLogTime) return;
            _nextPathDiagLogTime = Time.time + (DebugLog ? 0.35f : 1.25f);
            FileLogger.Log("AUTO-BATTLE", "path-build result=" + path + " detail=" + detail);
        }

        private static string FormatVec(Vector3 v)
        {
            return v.x.ToString("0.0") + "," + v.y.ToString("0.0") + "," + v.z.ToString("0.0");
        }

        private static string SafeOneLine(string s, int max)
        {
            if (string.IsNullOrEmpty(s)) return "-";
            s = s.Replace('\r', ' ').Replace('\n', ' ');
            if (s.Length > max) s = s.Substring(0, max);
            return s;
        }

        private sealed class TargetPick
        {
            public Character Target;
            public TargetSense Sense;
            public float Score;
        }

        private sealed class TargetSense
        {
            public Character Target;
            public Vector3 AimPoint;
            public Vector3 FirePoint;
            public float Distance;
            public float ScreenDistance;
            public bool Invincible;
            public bool VisibleByGame;
            public bool LineOfSight;
            public bool FireLineOfSight;
            public bool StrictFireLineOfSight;
            public bool HighGroundBlocked;
            public float HeightDelta;
            public bool OnScreen;
            public bool CloseVisible;
            public bool Visible;
        }
    }
}
