using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using ASWDEBUG.Cheats.AutoBattle;
using ASWDEBUG.Cheats.AutoBattle.CompactNav;
using ASWDEBUG.Logger;
using ASWDEBUG.Patch;
using UnityEngine;

namespace ASWDEBUG.Cheats.SurvivalBot
{
    public enum SurvivalBotPhase
    {
        Lobby,
        Matching,
        CaptureParticipants,
        Hide,
        Emergency,
        Attack,
        Suicide,
        Balance,
        GmExit,
#if SURVIVAL_INTERNAL_TOOLS
        CombatTest,
        RoomTest,
        Level33Test,
        MapBake,
#endif
        Stopped
    }

    public static class SurvivalBotManager
    {
        private enum AssaultDirectorState
        {
            Idle,
            PreStealth,
            HiddenWatch,
            RevealPursuit,
            HardHunt
        }

        private enum CliffSearchStatus
        {
            Pending,
            Found,
            Exhausted
        }

        private const float GuardShockWaveDistance = 5f;
        private const float GuardArrowRainMinDistance = 6.7f;
        private const float GuardArrowRainMaxDistance = 8.8f;
        private const float AssaultHiddenHuntDistance = 6f;
        private const float AssaultVisibleHuntDistance = 9f;
        private const float AssaultVisionPredictionSeconds = 0.75f;
        private const float AssaultVisionPredictionDistance = 40f;
        private const float AssaultStationaryConfirmSeconds = 0.55f;
        private const float AssaultOpeningStealthWindowSeconds = 5f;
        private const float AssaultOpeningStealthRetrySeconds = 0.06f;
        private const float AssaultStealthActivationGraceSeconds = 0.35f;
        private const float AssaultImmediateFireDistance = 7f;
        private const float AssaultStealthRetreatSeparation = 20f;
        private const float AssaultStealthRetreatPreferredSeparation = 23f;
        private const float AssaultStealthRetreatPlanningDistance = 28f;
        private const float EmergencyVisibleRetaliationDistance = 22f;
        private const float EmergencyVisibleRetentionSeconds = 0.65f;
        private const float ParticipantCaptureSeconds = 5f;
        private const float CliffFatalDrop = 12f;
        private const float CliffProbeDepth = 32f;
        private const double CliffSearchFrameBudgetMilliseconds = 2.5;
        private const int CliffSearchCandidatesPerFrame = 3;
        private static readonly List<Character> Enemies = new List<Character>(16);
        private static readonly HashSet<int> ParticipantIds = new HashSet<int>();
        private static readonly HashSet<int> ConfirmedDeadParticipantIds = new HashSet<int>();
        private static readonly HashSet<int> CountedParticipantIds = new HashSet<int>();
        private static readonly Dictionary<int, EnemyTrack> EnemyTracks = new Dictionary<int, EnemyTrack>(16);
        private static readonly List<Vector3> RouteExposurePoints = new List<Vector3>(48);
        private static readonly float[] SafeRadii = { 5f, 9f, 13f };
        private static readonly float[] StealthRetreatRadii = { 10f, 17f, 24f };
        private static readonly float[] CombatDirectionOffsets =
            { 0f, 18f, -18f, 36f, -36f, 58f, -58f, 82f, -82f, 112f, -112f, 145f, -145f, 180f };

        private static bool _roundActive;
        private static bool _controlStarted;
        private static bool _participantLocked;
        private static bool _taskCompleted;
        private static bool _matching;
        private static bool _pendingSurvivalMatchRequest;
        private static bool _cancelPending;
        private static bool _gmHandledThisRound;
        private static bool _roundEndedByGm;
        private static bool _awaitingReward;
        private static int _consecutiveGmRounds;
        private static int _roundGeneration;
        private static int _pendingGmGeneration;
        private static int _baselineKills;
        private static int _baselineAssists;
        private static float _roundStartedAt;
        private static float _controlStartedAt;
        private static float _rewardWaitStartedAt;
        private static float _matchStartedAt;
        private static float _nextMatchAt;
        private static float _nextRoomLoadAt;
        private static float _cancelRequestedAt;
        private static float _nextPunishRefreshAt;
        private static float _nextLobbyTraceAt;
        private static float _nextSafePointAt;
        private static float _attackPointSetAt;
        private static float _attackPointLastProgressAt;
        private static float _nextAttackSearchTraceAt;
        private static float _nextPursuitAimTraceAt;
        private static float _attackTargetVisibleAt;
        private static float _attackTargetLockedAt;
        private static float _attackEngagementStartedAt;
        private static float _attackLastDamageAt;
        private static int _attackTargetLastVital;
        private static float _opportunityCooldownUntil;
        private static float _emergencyTargetVisibleAt;
        private static float _emergencyTargetLockedAt;
        private static float _emergencyReleasedUntil;
        private static float _nextEnemyTrackAt;
        private static float _recentDamageAt;
        private static int _healthDamageSequence;
        private static int _handledHealthDamageSequence;
        private static int _incomingDamageSequence;
        private static int _avoidanceHandledDamageSequence;
        private static int _lastPlayerHp;
        private static int _lastPlayerShield;
        private static SurvivalRoleKind _currentRole;
        private static float _nextRoleDetectAt;
        private static float _roleDamageResponseUntil;
        private static float _nextRoleSkillAttemptAt;
        private static bool _heavyShieldPending;
        private static bool _heavyGallopPending;
        private static bool _guardHealPending;
        private static bool _assaultHiddenPending;
        private static Character _guardArrowTarget;
        private static float _guardArrowTargetLostAt;
        private static Character _assaultDirectorTarget;
        private static AssaultDirectorState _assaultDirectorState;
        private static float _assaultDirectorStateStartedAt;
        private static float _assaultThreatLostAt;
        private static float _assaultStationarySince;
        private static bool _assaultStationaryConfirmed;
        private static bool _assaultWasHidden;
        private static bool _avoidanceWasHidden;
        private static bool _avoidanceAttackCommitted;
        private static bool _stealthRetreatActive;
        private static bool _stealthRetreatDestinationSafe;
        private static bool _assaultOpeningStealthResolved;
        private static float _assaultOpeningStealthDeadlineAt;
        private static float _nextAssaultOpeningStealthAttemptAt;
        private static float _nextDirectorTraceAt;
        private static float _nextStealthRetreatTraceAt;
        private static float _safePointLeaseUntil;
        private static int _lastExposureCount;
        private static float _nextHideRouteAuditAt;
        private static float _suicideStartedAt;
        private static float _nextCliffScanAt;
        private static float _nextGmLeaveAt;
        private static float _lastCliffProgressAt;
        private static float _lastCliffDistance;
        private static float _nextCliffTraceAt;
        private static float _nextSuicideRequestAt;
        private static float _cliffJumpStartedAt;
        private static float _nextCliffJumpTraceAt;
        private static Vector3 _safePoint;
        private static Vector3 _attackPoint;
        private static Vector3 _attackPointTargetPosition;
        private static Vector3 _cliffEdge;
        private static Vector3 _cliffOutward;
        private static Vector3 _cliffJumpStart;
        private static readonly Vector3[] FailedCandidates = new Vector3[5];
        private static readonly float[] FailedCandidateUntil = new float[5];
        private static readonly Vector3[] FailedCliffCandidates = new Vector3[12];
        private static readonly float[] FailedCliffCandidateUntil = new float[12];
        private static readonly float[] CliffProbeDistances = { 0.9f, 1.6f, 2.4f, 3.3f, 4.2f };
        private static readonly float[] CliffProbeSideOffsets = { -0.45f, 0f, 0.45f };
        private const float CliffApproachStandOff = 1.15f;
        private static readonly List<RuntimeRainBoundarySample> CliffBoundaryCandidates =
            new List<RuntimeRainBoundarySample>(96);
        private static int _failedCandidateCursor;
        private static int _failedCliffCandidateCursor;
        private static int _combatStrafeSign = 1;
        private static float _combatStrafeSwitchAt;
        private static float _combatMoveProgressAt;
        private static float _nextCombatMoveTraceAt;
        private static Vector3 _combatMoveLastPosition;
        private static Vector3 _combatMoveDirection;
        private static int _combatMoveTargetUid;
        private static bool _hasSafePoint;
        private static bool _hasAttackPoint;
        private static bool _hasCliff;
        private static bool _cliffJumpLogged;
        private static bool _serverSuicideRequested;
        private static CliffSearchJob _cliffSearchJob;
        private static Character _attackTarget;
        private static Character _searchTarget;
        private static float _searchTargetLockedAt;
        private static Character _emergencyTarget;
        private static Character _emergencyReleasedTarget;
        private static UITakeCardManager _cardManager;
        private static UIJiesuan _balanceView;
        private static int _cardCount;
        private static float _nextCardActionAt;
        private static float _cardDetectedAt;
        private static float _nextCardWaitLogAt;
        private static float _nextBalanceConfirmAt;
        private static bool _cardCloseScheduled;
        private static byte _pendingGmUid;
        private static byte _pendingGmTeam;
        private static string _lastLobbyTrace = string.Empty;

        public static bool Enabled { get; private set; }
#if SURVIVAL_INTERNAL_TOOLS
        public static bool CombatTestEnabled { get; private set; }
        public static bool RoomTestEnabled { get; private set; }
        public static bool MapBakeEnabled { get; private set; }
        public static bool Level33TestEnabled
        {
            get { return LocalNavigationCombatTest.Enabled; }
        }
#endif
        public static SurvivalBotPhase Phase = SurvivalBotPhase.Lobby;
        public static string StatusText = "等待初始化";
        public static int InitialPlayers { get; private set; }
        public static int RemainingPlayers { get; private set; }
        public static int LastFinalRank { get; private set; }
        public static bool HasPendingSurvivalMatchRequest
        {
            get { return _pendingSurvivalMatchRequest; }
        }

        static SurvivalBotManager()
        {
            SurvivalBotSettings.EnsureLoaded();
            Enabled = false;
#if SURVIVAL_INTERNAL_TOOLS
            CombatTestEnabled = false;
            RoomTestEnabled = false;
            MapBakeEnabled = false;
#endif
            Phase = SurvivalBotPhase.Stopped;
            StatusText = "等待手动启动";
        }

        public static void Tick(Level level, Character player, Camera camera)
        {
            AutoBattleInput.BeginFrame();

            if (TickRainLifecycleGate()) return;

#if SURVIVAL_INTERNAL_TOOLS
            // The direct level33 test is fully local and intentionally bypasses proxy/channel state checks.
            if (Level33TestEnabled)
            {
                LocalNavigationCombatTest.Tick(level, player, camera);
                Phase = LocalNavigationCombatTest.Enabled
                    ? SurvivalBotPhase.Level33Test
                    : SurvivalBotPhase.Stopped;
                StatusText = LocalNavigationCombatTest.StatusText;
                return;
            }

            // Map baking is a local, read-only scene operation and must not depend on the game proxy.
            if (MapBakeEnabled)
            {
                MapBakeSceneLoader.Tick();
                TickMapBake(level, player);
                return;
            }
#endif

            if (NetworkRouteManager.ProxyRequired && NetworkRouteManager.HasError)
            {
                if (Enabled) Stop("network_proxy_failed");
#if SURVIVAL_INTERNAL_TOOLS
                if (CombatTestEnabled) SetCombatTestEnabled(false, "network_proxy_failed");
                if (RoomTestEnabled) SetRoomTestEnabled(false, "network_proxy_failed");
#endif
                return;
            }

            if (Input.GetKeyDown(KeyCode.F8))
                SetEnabled(!Enabled, "hotkey");

#if SURVIVAL_INTERNAL_TOOLS
            if (CombatTestEnabled)
            {
                TickCombatTest(GameApp.Instance, level, player, camera);
                return;
            }
            if (RoomTestEnabled)
            {
                TickRoomTest(GameApp.Instance, level, player, camera);
                return;
            }
#endif
            if (!Enabled)
            {
                AutoBattleInput.ClearAll();
                Phase = SurvivalBotPhase.Stopped;
                return;
            }

            TickCards();
            TickBalanceConfirmation();

            GameApp app = GameApp.Instance;
            bool inSurvival = IsInSurvivalGame(app);
            if (inSurvival)
            {
                _matching = false;
                _pendingSurvivalMatchRequest = false;
                _cancelPending = false;
                if (!_roundActive) StartRound(level, player);
                TickRound(app, level, player, camera);
                return;
            }

            if (_roundActive) FinishRound();
            TickLobby(app);
        }

        internal static bool TickRainLifecycleGate()
        {
            if (CompactRainNavRuntime.Requested) return false;
            if (!RuntimeRainNavMesh.HasDeferredSceneCleanup) return false;
            GameStateManager manager = ASSingleton<GameStateManager>.Instance;
            RuntimeRainNavMesh.TickDeferredSceneCleanup(IsSafeRainCollectionPoint(manager));
            AutoBattleInput.ClearAll();
            StatusText = "RAIN 内存门禁 | " + RuntimeRainNavMesh.Detail;
            return true;
        }

        internal static void NotifyLevelExit()
        {
            AutoBattleInput.ClearAll();
            SurvivalCombatAdapter.ResetSurvivalRuntime("level_exit");
#if SURVIVAL_INTERNAL_TOOLS
            AutoBattleManager.NotifyLevelExit();
#endif
            Enemies.Clear();
            EnemyTracks.Clear();
            RouteExposurePoints.Clear();
            CliffBoundaryCandidates.Clear();
            _attackTarget = null;
            _searchTarget = null;
            _emergencyTarget = null;
            _emergencyReleasedTarget = null;
            _cardManager = null;
            _balanceView = null;
            _hasSafePoint = false;
            _hasAttackPoint = false;
            _hasCliff = false;
            _cliffSearchJob = null;
            _suicideStartedAt = 0f;
            ResetRoleDirector("level_exit");
            ResetAttackSearchRuntime();
            ClearFailedCandidates();
            ClearFailedCliffCandidates();
            FileLogger.Log("SURVIVAL", "scene references cleared; round state preserved");
        }

        private static bool IsSafeRainCollectionPoint(GameStateManager manager)
        {
            if (!RuntimeRainNavMesh.IsRetiredGraphQuiescent) return false;
            try
            {
                if (ResourceManager2.instance == null || !ResourceManager2.instance.ClearFinsh)
                    return false;
                if (manager == null || manager.CurState == null || !manager.CurState.IsLoaded())
                    return false;

                if (manager.CurStateType == GameStateType.Lobby)
                {
                    bool stableLobby = UILobby.instance != null;
#if SURVIVAL_INTERNAL_TOOLS
                    stableLobby = stableLobby && !MapBakeSceneLoader.DirectSceneActive;
#endif
                    if (!stableLobby) return false;
                    Level level = ASSingleton<Level>.Instance;
                    return level == null || level.state == Level.State.kNone;
                }

                // A failed or superseded graph may be collected inside the current scene once
                // navigation is detached, the state is loaded and the level is fully ready.
                Level activeLevel = ASSingleton<Level>.Instance;
#if SURVIVAL_INTERNAL_TOOLS
                return !MapBakeSceneLoader.IsTransitioning && activeLevel != null &&
                    activeLevel.state == Level.State.kReady;
#else
                return activeLevel != null && activeLevel.state == Level.State.kReady;
#endif
            }
            catch
            {
                return false;
            }
        }

        public static void SetEnabled(bool enabled, string reason)
        {
            if (!enabled)
            {
                Stop(reason);
                return;
            }

            if (Enabled) return;
#if SURVIVAL_INTERNAL_TOOLS
            if (CombatTestEnabled) DisableCombatTest("survival_loop_enabled");
            if (RoomTestEnabled) DisableRoomTest("survival_loop_enabled");
            if (MapBakeEnabled) DisableMapBake("survival_loop_enabled");
            if (Level33TestEnabled) LocalNavigationCombatTest.Stop("survival_loop_enabled", true);
#endif
            Enabled = true;
            _consecutiveGmRounds = 0;
            _pendingGmUid = 0;
            _pendingGmTeam = 0;
            _pendingGmGeneration = 0;
            _gmHandledThisRound = false;
            _roundEndedByGm = false;
            _pendingSurvivalMatchRequest = false;
            _nextMatchAt = Time.time + 1f;
            _nextRoomLoadAt = 0f;
            _nextLobbyTraceAt = 0f;
            _lastLobbyTrace = string.Empty;
            Phase = SurvivalBotPhase.Lobby;
            StatusText = "机器人已启动";
            FileLogger.Log("SURVIVAL", "enabled reason=" + reason);
        }

#if SURVIVAL_INTERNAL_TOOLS
        public static void SetCombatTestEnabled(bool enabled, string reason)
        {
            if (!enabled)
            {
                DisableCombatTest(reason);
                return;
            }

            if (CombatTestEnabled) return;
            if (RoomTestEnabled) DisableRoomTest("combat_test_enabled");
            if (MapBakeEnabled) DisableMapBake("combat_test_enabled");
            if (Level33TestEnabled) LocalNavigationCombatTest.Stop("combat_test_enabled", true);
            DisableSurvivalLoopForCombatTest();
            CombatTestEnabled = true;
            _attackTarget = null;
            _searchTarget = null;
            _searchTargetLockedAt = 0f;
            _attackTargetVisibleAt = 0f;
            _emergencyTarget = null;
            _hasAttackPoint = false;
            ResetAttackSearchRuntime();
            AutoBattleManager.SetEnabled(true, "combat_test_start");
            Phase = SurvivalBotPhase.CombatTest;
            StatusText = "战斗测试已开启，等待进入对局";
            FileLogger.Log("AUTO-BATTLE", "combat test enabled reason=" + reason);
        }

        public static void SetRoomTestEnabled(bool enabled, string reason)
        {
            if (!enabled)
            {
                DisableRoomTest(reason);
                return;
            }

            if (RoomTestEnabled) return;
            if (CombatTestEnabled) DisableCombatTest("room_test_enabled");
            if (MapBakeEnabled) DisableMapBake("room_test_enabled");
            if (Level33TestEnabled) LocalNavigationCombatTest.Stop("room_test_enabled", true);
            DisableSurvivalLoopForCombatTest();
            RoomTestEnabled = true;
            _attackTarget = null;
            _searchTarget = null;
            _searchTargetLockedAt = 0f;
            _attackTargetVisibleAt = 0f;
            _emergencyTarget = null;
            _hasAttackPoint = false;
            ResetAttackSearchRuntime();
            AutoBattleManager.SetEnabled(true, "room_test_start");
            Phase = SurvivalBotPhase.RoomTest;
            StatusText = "开房测试已开启，等待进入对局";
            FileLogger.Log("AUTO-BATTLE", "room test enabled reason=" + reason);
        }

        public static void SetMapBakeEnabled(bool enabled, string reason)
        {
            if (!enabled)
            {
                DisableMapBake(reason);
                return;
            }

            if (MapBakeEnabled) return;
            if (CombatTestEnabled) DisableCombatTest("map_bake_enabled");
            if (RoomTestEnabled) DisableRoomTest("map_bake_enabled");
            if (Level33TestEnabled) LocalNavigationCombatTest.Stop("map_bake_enabled", true);
            DisableSurvivalLoopForCombatTest();
            MapBakeEnabled = true;
            AutoBattleInput.ClearAll();
            AutoBattleManager.SetEnabled(false, "map_bake_start");
            SurvivalCombatAdapter.ResetSurvivalRuntime("map_bake_start");
            Phase = SurvivalBotPhase.MapBake;
            StatusText = "地图建图已开启，等待进入地图";
            FileLogger.Log("AUTO-BATTLE][NAVMESH", "map bake enabled reason=" + reason);
        }

        public static void RequestDirectMapBake(string reason)
        {
            if (!MapBakeEnabled) SetMapBakeEnabled(true, "direct_map_load");
            string detail;
            if (!MapBakeSceneLoader.RequestSelectedMap(out detail))
            {
                StatusText = "地图建图 | " + detail;
                FileLogger.Log("AUTO-BATTLE][MAP-BAKE", "direct_load_rejected reason=" + detail);
                return;
            }
            StatusText = "地图建图 | " + detail;
            FileLogger.Log("AUTO-BATTLE][MAP-BAKE", "direct_load_accepted source=" + reason);
        }

        public static void SetLevel33TestEnabled(bool enabled, string reason)
        {
            if (!enabled)
            {
                LocalNavigationCombatTest.Stop(reason, true);
                Phase = SurvivalBotPhase.Stopped;
                StatusText = LocalNavigationCombatTest.StatusText;
                return;
            }

            if (Level33TestEnabled) return;
            if (CombatTestEnabled) DisableCombatTest("level33_test_enabled");
            if (RoomTestEnabled) DisableRoomTest("level33_test_enabled");
            if (MapBakeEnabled) DisableMapBake("level33_test_enabled");
            DisableSurvivalLoopForCombatTest();
            AutoBattleInput.ClearAll();
            AutoBattleManager.SetEnabled(false, "level33_test_request");
            SurvivalCombatAdapter.ResetSurvivalRuntime("level33_test_request");

            string detail;
            if (!LocalNavigationCombatTest.RequestStart(out detail))
            {
                Phase = SurvivalBotPhase.Stopped;
                StatusText = "level33 测试 | " + detail;
                FileLogger.Log("AUTO-BATTLE][LEVEL33-TEST", "request_rejected reason=" + detail);
                return;
            }
            Phase = SurvivalBotPhase.Level33Test;
            StatusText = detail;
            FileLogger.Log("AUTO-BATTLE][LEVEL33-TEST", "enabled reason=" + reason);
        }
#endif

        public static void Stop(string reason)
        {
#if SURVIVAL_INTERNAL_TOOLS
            if (!Enabled && !CombatTestEnabled && !RoomTestEnabled && !MapBakeEnabled && !Level33TestEnabled &&
                Phase == SurvivalBotPhase.Stopped) return;
#else
            if (!Enabled && Phase == SurvivalBotPhase.Stopped) return;
#endif
            Enabled = false;
#if SURVIVAL_INTERNAL_TOOLS
            CombatTestEnabled = false;
            RoomTestEnabled = false;
            MapBakeEnabled = false;
            if (Level33TestEnabled) LocalNavigationCombatTest.Stop("manager_stop:" + reason, true);
            MapBakeSceneLoader.CancelPending("stop:" + reason);
#endif
            Phase = SurvivalBotPhase.Stopped;
            StatusText = "已停止: " + reason;
            AutoBattleInput.ClearAll();
#if SURVIVAL_INTERNAL_TOOLS
            AutoBattleManager.SetEnabled(false, reason);
#endif
            SurvivalCombatAdapter.ResetSurvivalRuntime(reason);
            CancelActiveSession();
            _roundActive = false;
            _controlStarted = false;
            _cliffSearchJob = null;
            _suicideStartedAt = 0f;
            _awaitingReward = false;
            _cardManager = null;
            _emergencyTarget = null;
            ResetRoleDirector("stop");
            FileLogger.Log("SURVIVAL", StatusText);
        }

#if SURVIVAL_INTERNAL_TOOLS
        private static void DisableCombatTest(string reason)
        {
            if (!CombatTestEnabled) return;
            CombatTestEnabled = false;
            AutoBattleInput.ClearAll();
            AutoBattleManager.SetEnabled(false, "combat_test_stop");
            _attackTarget = null;
            _emergencyTarget = null;
            _hasAttackPoint = false;
            ResetAttackSearchRuntime();
            Phase = SurvivalBotPhase.Stopped;
            StatusText = "战斗测试已关闭";
            FileLogger.Log("AUTO-BATTLE", "combat test disabled reason=" + reason);
        }

        private static void DisableRoomTest(string reason)
        {
            if (!RoomTestEnabled) return;
            RoomTestEnabled = false;
            AutoBattleInput.ClearAll();
            AutoBattleManager.SetEnabled(false, "room_test_stop");
            _attackTarget = null;
            _emergencyTarget = null;
            _hasAttackPoint = false;
            ResetAttackSearchRuntime();
            Phase = SurvivalBotPhase.Stopped;
            StatusText = "开房测试已关闭";
            FileLogger.Log("AUTO-BATTLE", "room test disabled reason=" + reason);
        }

        private static void DisableMapBake(string reason)
        {
            if (!MapBakeEnabled) return;
            MapBakeEnabled = false;
            AutoBattleInput.ClearAll();
            MapBakeSceneLoader.CancelPending(reason);
            RuntimeRainNavSnapshot snapshot = RuntimeRainNavMesh.GetStatusSnapshot();
            if (snapshot.State == RuntimeRainNavState.WaitingScene ||
                snapshot.State == RuntimeRainNavState.Building)
                AutoBattleRoutePlanner.DeactivateNavigation("map_bake_disabled:" + reason);
            Phase = SurvivalBotPhase.Stopped;
            StatusText = "地图建图已关闭";
            FileLogger.Log("AUTO-BATTLE][NAVMESH", "map bake disabled reason=" + reason);
        }

        private static void DisableSurvivalLoopForCombatTest()
        {
            bool cancelMatching = _matching;
            Enabled = false;
            AutoBattleInput.ClearAll();
            SurvivalCombatAdapter.ResetSurvivalRuntime("combat_test_takeover");
            _roundActive = false;
            _controlStarted = false;
            _awaitingReward = false;
            _cardManager = null;
            _emergencyTarget = null;
            _matching = false;
            _pendingSurvivalMatchRequest = false;
            _cancelPending = false;
            _matchStartedAt = 0f;

            if (!cancelMatching) return;
            try
            {
                GameApp app = GameApp.Instance;
                if (app != null && app.lobby_connection != null)
                    app.lobby_connection.RequestCancelMatching();
            }
            catch (Exception ex)
            {
                FileLogger.Log("MATCH", "combat test matching cancel failed: " + ex.Message);
            }
        }
#endif

        public static void NotifyRemoteGmCandidate(byte uid, byte team)
        {
            GameApp app = GameApp.Instance;
            if (!Enabled || !IsInSurvivalGame(app)) return;
            _pendingGmUid = uid;
            _pendingGmTeam = team;
            _pendingGmGeneration = _roundActive ? _roundGeneration : _roundGeneration + 1;
            FileLogger.Log("GM", "remote GM/viewer candidate uid=" + uid + " team=" + team);
        }

        public static void NotifyFinalRank(byte rank)
        {
            LastFinalRank = rank;
            AutoBattleRoutePlanner.CancelPendingRoute("final_rank");
            FileLogger.Log("SURVIVAL", "final rank=" + rank + " initial=" + InitialPlayers +
                " topHalf=" + (InitialPlayers > 0 && rank <= InitialPlayers / 2));
        }

        public static void NotifyMatchingRequested(byte gameMode)
        {
            if (!Enabled || gameMode != (byte)RoomInfo.GameType.kGameTypeChiji) return;
            _pendingSurvivalMatchRequest = true;
            _matching = true;
            _cancelPending = false;
            _matchStartedAt = Time.time;
            Phase = SurvivalBotPhase.Matching;
            FileLogger.Log("MATCH", "survival matching request sent");
        }

        public static void NotifyMatchingResponse(bool accepted)
        {
            if (!_pendingSurvivalMatchRequest) return;
            if (accepted)
            {
                _matching = true;
                _cancelPending = false;
                if (_matchStartedAt <= 0f) _matchStartedAt = Time.time;
                Phase = SurvivalBotPhase.Matching;
                return;
            }

            NotifyMatchingCancelled(true);
        }

        public static void NotifyMatchingCancelled()
        {
            NotifyMatchingCancelled(false);
        }

        private static void NotifyMatchingCancelled(bool retryAllowed)
        {
            bool automaticCancel = _cancelPending;
            bool stopForManualCancel = Enabled && !retryAllowed && !automaticCancel;
            _matching = false;
            _pendingSurvivalMatchRequest = false;
            _cancelPending = false;
            _matchStartedAt = 0f;

            if (stopForManualCancel)
            {
                Enabled = false;
                Phase = SurvivalBotPhase.Stopped;
                StatusText = "已停止: 用户取消匹配";
                AutoBattleInput.ClearAll();
                SurvivalCombatAdapter.ResetSurvivalRuntime("manual_match_cancel");
                FileLogger.Log("MATCH", "manual cancellation stopped survival loop");
                return;
            }

            _nextMatchAt = Time.time + 1.5f;
            FileLogger.Log("MATCH", "matching cancelled; retry armed");
        }

        public static void NotifyCardRefresh(UITakeCardManager manager)
        {
            if (!Enabled || manager == null) return;
            _cardManager = manager;
            _cardCount = ReadPrivateInt(manager, "cardCount");
            _cardDetectedAt = Time.time;
            _nextCardWaitLogAt = 0f;
            _nextCardActionAt = Time.time + 0.15f;
            _cardCloseScheduled = false;
            _awaitingReward = true;
            Phase = SurvivalBotPhase.Balance;
            StatusText = _cardCount <= 0 ? "结算无可翻牌奖励" : "结算翻牌 0/" + _cardCount;
            bool active = manager.window != null && manager.window.gameObject != null &&
                manager.window.gameObject.activeInHierarchy;
            FileLogger.Log("CARD", "refresh cardCount=" + _cardCount + " active=" + active);
        }

        public static void NotifyBalanceShown(UIJiesuan view)
        {
            if (!Enabled || view == null) return;
            _balanceView = view;
            _nextBalanceConfirmAt = Time.time + 0.8f;
            _awaitingReward = true;
            Phase = SurvivalBotPhase.Balance;
            StatusText = "结算完成，准备确认";
            FileLogger.Log("CARD", "balance summary shown; confirm scheduled");
        }

        private static void StartRound(Level level, Character player)
        {
            _roundGeneration++;
            _roundActive = true;
            _controlStarted = false;
            _participantLocked = false;
            _taskCompleted = false;
            _gmHandledThisRound = false;
            _roundEndedByGm = false;
            _awaitingReward = false;
            _cardManager = null;
            _balanceView = null;
            _roundStartedAt = Time.time;
            _baselineKills = 0;
            _baselineAssists = 0;
            InitialPlayers = 0;
            RemainingPlayers = 0;
            LastFinalRank = 0;
            ParticipantIds.Clear();
            ConfirmedDeadParticipantIds.Clear();
            EnemyTracks.Clear();
            _hasSafePoint = false;
            _hasAttackPoint = false;
            _hasCliff = false;
            _cliffSearchJob = null;
            _suicideStartedAt = 0f;
            _attackTarget = null;
            _searchTarget = null;
            _searchTargetLockedAt = 0f;
            _attackTargetVisibleAt = 0f;
            _attackTargetLockedAt = 0f;
            _attackEngagementStartedAt = 0f;
            _attackLastDamageAt = 0f;
            _attackTargetLastVital = 0;
            _opportunityCooldownUntil = 0f;
            _emergencyTarget = null;
            _emergencyTargetVisibleAt = 0f;
            _emergencyTargetLockedAt = 0f;
            _emergencyReleasedTarget = null;
            _emergencyReleasedUntil = 0f;
            _nextEnemyTrackAt = 0f;
            _recentDamageAt = 0f;
            _incomingDamageSequence = 0;
            _avoidanceHandledDamageSequence = 0;
            _avoidanceWasHidden = false;
            _avoidanceAttackCommitted = false;
            _stealthRetreatActive = false;
            _stealthRetreatDestinationSafe = false;
            _nextStealthRetreatTraceAt = 0f;
            _lastPlayerHp = player == null ? 0 : player.hp;
            try { _lastPlayerShield = player == null ? 0 : player.shield; }
            catch { _lastPlayerShield = 0; }
            ResetRoleDirector("round_start");
            _safePointLeaseUntil = 0f;
            _lastExposureCount = 0;
            _nextHideRouteAuditAt = 0f;
            ClearFailedCandidates();
            _nextSafePointAt = 0f;
            ResetAttackSearchRuntime();
            _nextCliffTraceAt = 0f;
            _cliffJumpLogged = false;
            _cliffJumpStartedAt = 0f;
            _nextCliffJumpTraceAt = 0f;
            _cliffJumpStart = Vector3.zero;
            _nextSuicideRequestAt = 0f;
            _serverSuicideRequested = false;
            ClearFailedCliffCandidates();
            SurvivalCombatAdapter.ResetSurvivalRuntime("round_start");
            CaptureParticipants(GameApp.Instance, level, player);
            Phase = SurvivalBotPhase.CaptureParticipants;
            StatusText = "进入生存对局，等待角色就绪";
            FileLogger.Log("SURVIVAL", "round connection entered generation=" + _roundGeneration);
        }

        private static void FinishRound()
        {
            if (!_roundEndedByGm) _consecutiveGmRounds = 0;
            FileLogger.Log("SURVIVAL", "round finish task=" + _taskCompleted + " gm=" + _roundEndedByGm +
                " rank=" + LastFinalRank);
            _roundActive = false;
            _controlStarted = false;
            _cliffSearchJob = null;
            _suicideStartedAt = 0f;
            _pendingGmUid = 0;
            _pendingGmTeam = 0;
            _pendingGmGeneration = 0;
            _emergencyTarget = null;
            ResetRoleDirector("round_finish");
            AutoBattleInput.ClearAll();
            SurvivalCombatAdapter.ResetSurvivalRuntime("round_finish");
            Phase = SurvivalBotPhase.Balance;
            StatusText = "等待结算/返回大厅";
            _awaitingReward = !_roundEndedByGm;
            _rewardWaitStartedAt = Time.time;
            _nextMatchAt = Time.time + (_awaitingReward ? 12f : 3f);
        }

#if SURVIVAL_INTERNAL_TOOLS
        private static void TickCombatTest(GameApp app, Level level, Character player, Camera camera)
        {
            Phase = SurvivalBotPhase.CombatTest;
            if (app == null || app.channel_connection == null ||
                app.channel_connection.state != ChannelConnection.State.kInGame)
            {
                AutoBattleInput.ClearAll();
                _attackTarget = null;
                StatusText = "战斗测试 | 等待进入对局";
                return;
            }

            AutoBattleManager.Tick(level, player, camera);
            if (player != null)
            {
                RefreshEnemies(level, player);
                RemainingPlayers = CountRemaining(player);
            }
            StatusText = "战斗测试 | " + AutoBattleManager.LastStatus;
        }

        private static void TickRoomTest(GameApp app, Level level, Character player, Camera camera)
        {
            Phase = SurvivalBotPhase.RoomTest;
            if (!IsRoomTestRuntimeReady(level, player, camera))
            {
                AutoBattleInput.ClearAll();
                AutoBattleManager.Tick(null, null, null);
                _attackTarget = null;
                StatusText = "开房测试 | 等待本地角色进入可操作对局";
                return;
            }

            AutoBattleManager.Tick(level, player, camera);
            RefreshEnemies(level, player);
            RemainingPlayers = CountRemaining(player);
            string connection = "local-ready";
            try
            {
                if (app != null && app.channel_connection != null)
                    connection = app.channel_connection.state.ToString() + "/" +
                        app.channel_connection.game_state.ToString();
            }
            catch { }
            StatusText = "开房测试 | " + AutoBattleManager.LastStatus + " | " + connection;
        }

        private static bool IsRoomTestRuntimeReady(Level level, Character player, Camera camera)
        {
            if (level == null || player == null || player.transform == null || camera == null) return false;
            if (level.state != Level.State.kReady || player.Is_Viewer) return false;
            try
            {
                GameApp app = GameApp.Instance;
                if (app != null && app.channel_connection != null &&
                    app.channel_connection.state == ChannelConnection.State.kInGame)
                    return true;
            }
            catch { }
            if (player.IsDied) return false;
            RefreshEnemies(level, player);
            return Enemies.Count > 0;
        }

        private static void TickMapBake(Level level, Character player)
        {
            Phase = SurvivalBotPhase.MapBake;
            AutoBattleInput.ClearAll();
            RuntimeRainNavSnapshot snapshot = RuntimeRainNavMesh.GetStatusSnapshot();
            if (snapshot.State == RuntimeRainNavState.Ready)
            {
                if (MapBakeSceneLoader.DirectSceneActive &&
                    (level == null || !MapBakeSceneLoader.IsExpectedDirectScene(level.map_name) ||
                     !MapBakeSceneLoader.IsExpectedDirectScene(snapshot.MapName)))
                {
                    StatusText = "地图建图 | 正在校验目标场景，未接受其他场景的缓存";
                    return;
                }

                RuntimeRainDerivedSnapshot derived = snapshot.Derived;
                if (derived.Stage == RuntimeRainDerivedStage.Failed)
                {
                    StatusText = "地图建图 | 派生数据生成失败 | " + derived.Detail;
                    return;
                }
                if (derived.Stage != RuntimeRainDerivedStage.Ready)
                {
                    StatusText = "地图建图 | 派生 " + DerivedStageName(derived.Stage) + " " +
                        (derived.Progress01 * 100f).ToString("0.0") + "% | " +
                        derived.Processed + "/" + derived.Total + " | Jump " +
                        derived.JumpLinkCount + " Drop " + derived.DropLinkCount;
                    return;
                }

                CompactRainAutoConversionSnapshot compact = snapshot.Compact;
                bool compactRequired = string.Equals(snapshot.MapName, "level33",
                    StringComparison.OrdinalIgnoreCase);
                if (compactRequired && !compact.Ready)
                {
                    StatusText = "\u5730\u56fe\u5efa\u56fe | ASWNAV " +
                        (compact.State == CompactRainAutoConversionState.Failed
                            ? "\u8f6c\u6362\u5931\u8d25"
                            : "\u751f\u6210\u4e2d") + " | " + compact.Detail;
                    return;
                }

                string displayName = MapBakeSceneLoader.DisplayNameForRuntimeMap(snapshot.MapName);
                StatusText = "地图建图 | 已完成并可复用 | " + displayName +
                    " | 节点 " + snapshot.GraphSize + " | OffMesh " +
                    (derived.JumpLinkCount + derived.DropLinkCount) + " | 缓存 " + derived.CacheStatus;
                if (MapBakeSceneLoader.DirectSceneActive)
                {
                    if (!snapshot.BakeArtifactReady)
                    {
                        StatusText = "地图建图 | 基础图或派生缓存未完整保存，不自动退出 | base=" +
                            snapshot.CacheStatus + " meta=" + derived.CacheStatus;
                        return;
                    }

                    string returnDetail;
                    if (MapBakeSceneLoader.TryReturnToLobby(out returnDetail))
                    {
                        MapBakeEnabled = false;
                        Phase = SurvivalBotPhase.Stopped;
                        StatusText = "地图建图 | " + returnDetail;
                    }
                    else if (!string.IsNullOrEmpty(returnDetail))
                    {
                        StatusText = "地图建图 | " + returnDetail;
                    }
                }
                return;
            }
            if (snapshot.State == RuntimeRainNavState.Failed)
            {
                StatusText = "地图建图 | 生成失败 | " + snapshot.Detail;
                return;
            }
            if (snapshot.State == RuntimeRainNavState.Building)
            {
                StatusText = "地图建图 | 极限精度生成中 " +
                    (snapshot.Progress01 * 100f).ToString("0.0") + "% | 已用 " +
                    snapshot.ElapsedSeconds.ToString("0") + " 秒 | 不限时";
                return;
            }
            if (MapBakeSceneLoader.IsTransitioning)
            {
                StatusText = "地图建图 | " + MapBakeSceneLoader.StatusText;
                return;
            }
            if (level == null || level.state != Level.State.kReady)
            {
                StatusText = "地图建图 | 等待地图加载";
                return;
            }
            StatusText = "地图建图 | 准备 " + MapBakeSceneLoader.DisplayNameForRuntimeMap(snapshot.MapName) +
                " | " + snapshot.Detail;
        }

        private static string DerivedStageName(RuntimeRainDerivedStage stage)
        {
            if (stage == RuntimeRainDerivedStage.ScanGraph) return "扫描图";
            if (stage == RuntimeRainDerivedStage.Components) return "连通分区";
            if (stage == RuntimeRainDerivedStage.Surfaces) return "净空/掩体/出生点";
            if (stage == RuntimeRainDerivedStage.OffMeshLinks) return "Jump/Drop Link";
            if (stage == RuntimeRainDerivedStage.Saving) return "写入缓存";
            if (stage == RuntimeRainDerivedStage.Loading) return "加载缓存";
            return stage.ToString();
        }
#endif

        private static void TickRound(GameApp app, Level level, Character player, Camera camera)
        {
            if (_pendingGmTeam >= 2 && _pendingGmGeneration == _roundGeneration && !_gmHandledThisRound)
            {
                HandleGmExit(app);
                return;
            }

            if (_gmHandledThisRound)
            {
                MaintainGmExit(app);
                return;
            }

            CaptureParticipants(app, level, player);
            if (!_taskCompleted && _controlStarted && player != null &&
                player.num_killed > _baselineKills)
            {
                _taskCompleted = true;
                SurvivalCombatAdapter.CancelSurvivalAttack();
                SetAttackTarget(null, "objective_complete");
                _searchTarget = null;
                ResetOffensiveDirector("objective_complete");
                FileLogger.Log("SURVIVAL", "kill objective complete kills=" + player.num_killed +
                    " assists=" + player.holding_attack_count);
            }
            if (player != null && player.IsDied)
            {
                _emergencyTarget = null;
                AutoBattleInput.ClearAll();
                Phase = SurvivalBotPhase.Balance;
                StatusText = "角色已死亡，等待结算";
                return;
            }
            if (!IsCharacterControlReady(app, player))
            {
                _emergencyTarget = null;
                AutoBattleInput.ClearAll();
                Phase = SurvivalBotPhase.CaptureParticipants;
                StatusText = "等待角色和倒计时就绪 | 初始 " + InitialPlayers;
                return;
            }

            if (!_controlStarted)
            {
                _controlStarted = true;
                _controlStartedAt = Time.time;
                _baselineKills = player.num_killed;
                _baselineAssists = player.holding_attack_count;
                ParticipantIds.Remove(0);
                CaptureParticipants(app, level, player);
                FileLogger.Log("SURVIVAL", "control start kills=" + _baselineKills + " assists=" + _baselineAssists +
                    " roster=" + InitialPlayers);
            }

            SurvivalCombatAdapter.MarkSurvivalActivity(player);
            RefreshEnemies(level, player);
            UpdateEnemyTracks(player, camera);
            RemainingPlayers = CountRemaining(player);

            if (!_participantLocked && Time.time - _controlStartedAt >= ParticipantCaptureSeconds)
            {
                _participantLocked = true;
                InitialPlayers = Math.Max(InitialPlayers, ParticipantIds.Count);
                FileLogger.Log("SURVIVAL", "participants locked initial=" + InitialPlayers);
            }

            int initial = Math.Max(InitialPlayers, ParticipantIds.Count);
            int threshold = Math.Max(1, initial / 2);
            bool rankSecured = _participantLocked && RemainingPlayers <= threshold;
            bool avoidancePhase = !_taskCompleted &&
                (!_participantLocked || RemainingPlayers > threshold + 1);

            if (_taskCompleted && rankSecured)
            {
                TickRoleDirector(player, camera, true, false);
                TickSuicide(app, player, camera);
                return;
            }

            if (TickRoleDirector(player, camera, _taskCompleted, avoidancePhase)) return;

            if (TickEmergencyCounterattack(player, camera, _taskCompleted)) return;

            if (_taskCompleted)
            {
                TickHide(player, camera);
                StatusText = "任务已完成，等待排名进入前半 | 存活 " + RemainingPlayers +
                    " / 前半线 " + threshold + " | 路径 " + SurvivalCombatAdapter.LastPath;
                return;
            }

            if (!_participantLocked || RemainingPlayers > threshold + 1)
            {
                TickHide(player, camera);
            }
            else if (RemainingPlayers == threshold + 1)
            {
                ClearEmergencyTarget("opportunity_phase");
                TickAttack(player, camera, true);
            }
            else
            {
                ClearEmergencyTarget("attack_phase");
                TickAttack(player, camera, false);
            }
        }

        private static bool TickRoleDirector(Character player, Camera camera, bool objectiveComplete,
            bool avoidancePhase)
        {
            UpdateCurrentRole(player);
            bool assaultAvoidanceDamage = avoidancePhase &&
                _currentRole == SurvivalRoleKind.Assault &&
                _incomingDamageSequence != _avoidanceHandledDamageSequence;
            if (assaultAvoidanceDamage)
            {
                _handledHealthDamageSequence = _healthDamageSequence;
                _assaultHiddenPending = false;
            }
            else
            {
                ArmRoleDamageResponse(player);
                TickRoleDamageResponse(player);
            }

            if (_currentRole == SurvivalRoleKind.Guard)
                return TickGuardDirector(player, camera, objectiveComplete);
            if (_currentRole == SurvivalRoleKind.Assault)
                return TickAssaultDirector(player, camera, objectiveComplete, avoidancePhase);
            return false;
        }

        private static void UpdateCurrentRole(Character player)
        {
            if (Time.time < _nextRoleDetectAt && _currentRole != SurvivalRoleKind.Unknown) return;
            _nextRoleDetectAt = Time.time + 0.75f;
            SurvivalRoleKind detected = SurvivalCombatAdapter.DetectSurvivalRole(player);
            if (detected == _currentRole) return;
            SurvivalRoleKind previous = _currentRole;
            _currentRole = detected;
            _handledHealthDamageSequence = 0;
            _heavyShieldPending = false;
            _heavyGallopPending = false;
            _guardHealPending = false;
            _assaultHiddenPending = false;
            ResetOffensiveDirector("role_changed");
            if (detected == SurvivalRoleKind.Assault &&
                !_assaultOpeningStealthResolved &&
                _assaultOpeningStealthDeadlineAt <= 0f)
            {
                _assaultOpeningStealthDeadlineAt =
                    Time.time + AssaultOpeningStealthWindowSeconds;
            }
            FileLogger.Log("SURVIVAL][ROLE", "detected=" + detected + " previous=" + previous);
        }

        private static void ArmRoleDamageResponse(Character player)
        {
            if (_healthDamageSequence == _handledHealthDamageSequence ||
                _currentRole == SurvivalRoleKind.Unknown)
                return;
            _handledHealthDamageSequence = _healthDamageSequence;
            _roleDamageResponseUntil = Time.time + 1.25f;
            _nextRoleSkillAttemptAt = 0f;
            _heavyShieldPending = _currentRole == SurvivalRoleKind.Heavy &&
                SurvivalCombatAdapter.HasSurvivalSkill(player, SkillType.kSkillShield);
            _heavyGallopPending = _currentRole == SurvivalRoleKind.Heavy &&
                SurvivalCombatAdapter.HasSurvivalSkill(player, SkillType.kSkillGallop);
            _guardHealPending = _currentRole == SurvivalRoleKind.Guard &&
                SurvivalCombatAdapter.HasSurvivalSkill(player, SkillType.kSkillHeal);
            _assaultHiddenPending = _currentRole == SurvivalRoleKind.Assault &&
                SurvivalCombatAdapter.HasSurvivalSkill(player, SkillType.kSkillHidden);
            FileLogger.Log("SURVIVAL][ROLE", "damage response sequence=" + _healthDamageSequence +
                " role=" + _currentRole + " shield=" + _heavyShieldPending +
                " speed=" + _heavyGallopPending + " heal=" + _guardHealPending +
                " hidden=" + _assaultHiddenPending);
        }

        private static void TickRoleDamageResponse(Character player)
        {
            if (!_heavyShieldPending && !_heavyGallopPending && !_guardHealPending &&
                !_assaultHiddenPending)
                return;
            if (Time.time > _roleDamageResponseUntil)
            {
                _heavyShieldPending = false;
                _heavyGallopPending = false;
                _guardHealPending = false;
                _assaultHiddenPending = false;
                return;
            }
            if (Time.time < _nextRoleSkillAttemptAt) return;
            _nextRoleSkillAttemptAt = Time.time + 0.06f;

            if (_assaultHiddenPending &&
                SurvivalCombatAdapter.IsSurvivalSkillReady(player, SkillType.kSkillHidden) &&
                SurvivalCombatAdapter.TryUseSurvivalSkill(player, SkillType.kSkillHidden,
                    "assault_damage_fallback_stealth"))
            {
                _assaultHiddenPending = false;
                _nextRoleSkillAttemptAt = Time.time + 0.08f;
                return;
            }
            if (_heavyShieldPending &&
                SurvivalCombatAdapter.IsSurvivalSkillReady(player, SkillType.kSkillShield) &&
                SurvivalCombatAdapter.TryUseSurvivalSkill(player, SkillType.kSkillShield,
                    "heavy_hp_drop_shield"))
            {
                _heavyShieldPending = false;
                _nextRoleSkillAttemptAt = Time.time + 0.08f;
                return;
            }
            if (_heavyGallopPending &&
                SurvivalCombatAdapter.IsSurvivalSkillReady(player, SkillType.kSkillGallop) &&
                SurvivalCombatAdapter.TryUseSurvivalSkill(player, SkillType.kSkillGallop,
                    "heavy_hp_drop_gallop"))
            {
                _heavyGallopPending = false;
                _nextRoleSkillAttemptAt = Time.time + 0.08f;
                return;
            }
            if (_guardHealPending &&
                SurvivalCombatAdapter.IsSurvivalSkillReady(player, SkillType.kSkillHeal) &&
                SurvivalCombatAdapter.TryUseSurvivalSkill(player, SkillType.kSkillHeal,
                    "guard_hp_drop_heal"))
            {
                _guardHealPending = false;
                _nextRoleSkillAttemptAt = Time.time + 0.08f;
            }
        }

        private static bool TickGuardDirector(Character player, Camera camera, bool objectiveComplete)
        {
            Character closeTarget = SelectNearestDirectorTarget(player, GuardShockWaveDistance);
            if (closeTarget != null &&
                SurvivalCombatAdapter.IsSurvivalSkillReady(player, SkillType.kSkillShockWave))
            {
                SurvivalCombatAdapter.TryUseSurvivalSkill(player, SkillType.kSkillShockWave,
                    "guard_enemy_inside_5m_shockwave");
            }

            if (objectiveComplete)
            {
                _guardArrowTarget = null;
                _guardArrowTargetLostAt = 0f;
                return false;
            }
            if (!SurvivalCombatAdapter.IsSurvivalSkillReady(player, SkillType.kSkillArrowRain))
            {
                _guardArrowTarget = null;
                _guardArrowTargetLostAt = 0f;
                return false;
            }

            if (!IsGuardArrowTargetUsable(player, _guardArrowTarget, camera, 5.8f, 10f))
            {
                if (_guardArrowTarget != null)
                {
                    if (_guardArrowTargetLostAt <= 0f) _guardArrowTargetLostAt = Time.time;
                    if (Time.time - _guardArrowTargetLostAt < 0.45f) return HoldGuardArrowAim(player, camera);
                }
                _guardArrowTarget = SelectGuardArrowTarget(player, camera);
                _guardArrowTargetLostAt = 0f;
                if (_guardArrowTarget == null) return false;
                SetAttackTarget(_guardArrowTarget, "guard_arrow_rain_lock");
                SurvivalCombatAdapter.CancelSurvivalAttack();
                FileLogger.Log("SURVIVAL][ROLE", "guard arrow-rain lock uid=" +
                    _guardArrowTarget.uid + " dist=" +
                    XzDistance(player.transform.position, _guardArrowTarget.transform.position).ToString("0.0"));
            }
            else
            {
                _guardArrowTargetLostAt = 0f;
            }
            return HoldGuardArrowAim(player, camera);
        }

        private static bool HoldGuardArrowAim(Character player, Camera camera)
        {
            if (_guardArrowTarget == null) return false;
            Phase = SurvivalBotPhase.Emergency;
            ClearEmergencyTarget("guard_arrow_rain");
            SurvivalCombatAdapter.SuspendSurvivalNavigation("guard_arrow_rain");
            AutoBattleInput.ClearFire();
            SurvivalCombatAdapter.CloseSurvivalScope(player);
            float distance = XzDistance(player.transform.position, _guardArrowTarget.transform.position);
            bool aimReady = SurvivalCombatAdapter.PrepareSurvivalTargetSkill(player,
                _guardArrowTarget, camera);
            MoveCombatStrafe(player, _guardArrowTarget);
            if (aimReady && SurvivalCombatAdapter.TryUseSurvivalSkill(player,
                SkillType.kSkillArrowRain, "guard_arrow_rain_7m_lock"))
            {
                FileLogger.Log("SURVIVAL][ROLE", "guard arrow-rain fired uid=" +
                    _guardArrowTarget.uid + " dist=" + distance.ToString("0.0"));
                _guardArrowTarget = null;
                _guardArrowTargetLostAt = 0f;
            }
            StatusText = "Guard director | arrow-rain lock | distance " + distance.ToString("0.0");
            return true;
        }

        private static Character SelectGuardArrowTarget(Character player, Camera camera)
        {
            Character best = null;
            float bestScore = float.MaxValue;
            for (int i = 0; i < Enemies.Count; i++)
            {
                Character target = Enemies[i];
                if (!IsGuardArrowTargetUsable(player, target, camera,
                    GuardArrowRainMinDistance, GuardArrowRainMaxDistance))
                    continue;
                EnemyTrack track = GetEnemyTrack(target);
                float distance = track == null
                    ? XzDistance(player.transform.position, target.transform.position)
                    : track.Distance;
                float score = Mathf.Abs(distance - 7.6f) * 10f +
                    (track == null ? 0f : track.HealthPercent * 0.05f);
                if (score >= bestScore) continue;
                bestScore = score;
                best = target;
            }
            return best;
        }

        private static bool IsGuardArrowTargetUsable(Character player, Character target,
            Camera camera, float minDistance, float maxDistance)
        {
            if (player == null || camera == null || !IsEmergencyTargetUsable(target)) return false;
            EnemyTrack track = GetEnemyTrack(target);
            float distance = track == null
                ? XzDistance(player.transform.position, target.transform.position)
                : track.Distance;
            bool hidden = track == null ? IsTargetHidden(target) : track.Hidden;
            bool invincible = track != null && track.Invincible;
            return !hidden && !invincible && distance >= minDistance && distance <= maxDistance &&
                SurvivalCombatAdapter.SurvivalHasEmergencyFireLine(player, target, camera);
        }

        private static bool TickAssaultDirector(Character player, Camera camera, bool objectiveComplete,
            bool avoidancePhase)
        {
            bool playerHidden = IsTargetHidden(player);
            if (playerHidden)
            {
                _assaultWasHidden = true;
                if (_assaultDirectorState == AssaultDirectorState.PreStealth)
                    SetAssaultDirectorState(AssaultDirectorState.HiddenWatch, "stealth_confirmed");
            }

            if (avoidancePhase)
                return TickAssaultAvoidanceDirector(player, camera, playerHidden);
            TickAssaultOpeningStealth(player, playerHidden);

            if (!IsEmergencyTargetUsable(_assaultDirectorTarget))
                ResetAssaultDirector("target_invalid");

            float visionScore;
            Character visionThreat = SelectAssaultVisionThreat(player, out visionScore);
            if (!playerHidden && visionThreat != null &&
                SurvivalCombatAdapter.IsSurvivalSkillReady(player, SkillType.kSkillHidden))
            {
                if (_assaultDirectorTarget == null)
                    SetAssaultDirectorTarget(visionThreat, "predicted_vision");
                if (_assaultDirectorTarget == visionThreat &&
                    SurvivalCombatAdapter.TryUseSurvivalSkill(player, SkillType.kSkillHidden,
                        "assault_preemptive_stealth"))
                {
                    _assaultWasHidden = false;
                    SetAssaultDirectorState(AssaultDirectorState.PreStealth,
                        "predicted_vision_" + visionScore.ToString("0"));
                }
            }

            if (objectiveComplete)
            {
                ResetAssaultDirector("objective_complete_wait");
                return false;
            }

            if (_assaultDirectorTarget == null)
            {
                Character huntThreat = SelectAssaultHuntThreat(player);
                if (huntThreat == null) return false;
                SetAssaultDirectorTarget(huntThreat, "hunt_threshold");
            }

            EnemyTrack track = GetEnemyTrack(_assaultDirectorTarget);
            if (track == null) return false;
            float distance = track.Distance;
            bool hidden = track.Hidden;

            if (hidden && distance <= AssaultHiddenHuntDistance)
            {
                SetAssaultDirectorState(AssaultDirectorState.HardHunt, "hidden_inside_6m");
                return TickAssaultHardHunt(player, camera, track);
            }
            if (!hidden && distance <= AssaultVisibleHuntDistance && track.ClosingSpeed >= 0.30f)
            {
                SetAssaultDirectorState(AssaultDirectorState.HardHunt, "visible_approach_threshold");
                return TickAssaultHardHunt(player, camera, track);
            }
            if (_assaultDirectorState == AssaultDirectorState.HardHunt)
            {
                if (hidden && distance > AssaultHiddenHuntDistance)
                    SetAssaultDirectorState(AssaultDirectorState.HiddenWatch, "target_cloaked_outside_6m");
                else
                    return TickAssaultHardHunt(player, camera, track);
            }

            if (hidden && (_assaultDirectorState == AssaultDirectorState.PreStealth ||
                _assaultDirectorState == AssaultDirectorState.HiddenWatch ||
                _assaultDirectorState == AssaultDirectorState.RevealPursuit))
            {
                UpdateAssaultStationaryState(track);
                if (!playerHidden && _assaultWasHidden && _assaultStationaryConfirmed)
                    SetAssaultDirectorState(AssaultDirectorState.RevealPursuit,
                        "our_stealth_ended_enemy_waiting");
                if (_assaultDirectorState == AssaultDirectorState.RevealPursuit)
                    return TickAssaultHardHunt(player, camera, track);
                if (playerHidden && (track.ClosingSpeed >= 0.15f || _assaultStationaryConfirmed))
                    return HoldAssaultHiddenWatch(player, track);
            }

            bool stillThreat = IsAboutToGainVision(player, _assaultDirectorTarget, track,
                out visionScore) || track.ClosingSpeed >= 0.15f;
            if (stillThreat)
            {
                _assaultThreatLostAt = 0f;
            }
            else
            {
                if (_assaultThreatLostAt <= 0f) _assaultThreatLostAt = Time.time;
                if (Time.time - _assaultThreatLostAt >= 1.25f)
                    ResetAssaultDirector("threat_departed");
            }
            return false;
        }

        private static bool TickAssaultAvoidanceDirector(Character player, Camera camera,
            bool playerHidden)
        {
            if (_incomingDamageSequence != _avoidanceHandledDamageSequence)
            {
                _avoidanceHandledDamageSequence = _incomingDamageSequence;
                CommitAvoidanceAttack("incoming_damage");
            }

            bool stealthEnded = _avoidanceWasHidden && !playerHidden;
            _avoidanceWasHidden = playerHidden;
            if (stealthEnded &&
                !SurvivalCombatAdapter.IsSurvivalSkillReady(player, SkillType.kSkillHidden))
                CommitAvoidanceAttack("stealth_ended_cooldown");

            if (_avoidanceAttackCommitted)
            {
                TickAttack(player, camera, false);
                return true;
            }

            TickAssaultOpeningStealth(player, playerHidden);
            if (!playerHidden)
            {
                _stealthRetreatActive = false;
                _stealthRetreatDestinationSafe = false;
                if (_assaultDirectorState == AssaultDirectorState.PreStealth &&
                    Time.time - _assaultDirectorStateStartedAt <=
                    AssaultStealthActivationGraceSeconds)
                {
                    Phase = SurvivalBotPhase.Hide;
                    SurvivalCombatAdapter.CancelSurvivalAttack();
                    SurvivalCombatAdapter.CloseSurvivalScope(player);
                    AutoBattleInput.ClearMovement();
                    StatusText = "Stealth avoidance | waiting for stealth confirmation";
                    return true;
                }
                float visionScore;
                Character visionThreat = SelectAssaultVisionThreat(player, out visionScore);
                if (visionThreat != null &&
                    SurvivalCombatAdapter.IsSurvivalSkillReady(player, SkillType.kSkillHidden) &&
                    SurvivalCombatAdapter.TryUseSurvivalSkill(player, SkillType.kSkillHidden,
                        "assault_avoidance_preemptive_stealth"))
                {
                    _assaultWasHidden = false;
                    SetAssaultDirectorTarget(visionThreat, "avoidance_predicted_vision");
                    SetAssaultDirectorState(AssaultDirectorState.PreStealth,
                        "avoidance_predicted_vision_" + visionScore.ToString("0"));
                    Phase = SurvivalBotPhase.Hide;
                    SurvivalCombatAdapter.CancelSurvivalAttack();
                    SurvivalCombatAdapter.CloseSurvivalScope(player);
                    AutoBattleInput.ClearMovement();
                    StatusText = "Stealth avoidance | activating before enemy vision";
                    return true;
                }
                return false;
            }

            Character approachThreat = SelectAssaultStealthRetreatThreat(player);
            if (approachThreat != null) _stealthRetreatActive = true;
            else if (_stealthRetreatActive)
                approachThreat = SelectNearestLivingOpponent(player);
            if (approachThreat == null)
            {
                _stealthRetreatActive = false;
                return false;
            }
            SetAssaultDirectorTarget(approachThreat, "stealth_retreat");
            SetAssaultDirectorState(AssaultDirectorState.HiddenWatch, "stealth_retreat");
            TickAssaultStealthRetreat(player, camera, approachThreat);
            return true;
        }

        private static void CommitAvoidanceAttack(string reason)
        {
            if (_avoidanceAttackCommitted) return;
            _avoidanceAttackCommitted = true;
            _assaultHiddenPending = false;
            _stealthRetreatActive = false;
            _hasSafePoint = false;
            _stealthRetreatDestinationSafe = false;
            ClearEmergencyTarget("avoidance_attack_commit");
            SurvivalCombatAdapter.SuspendSurvivalNavigation("avoidance_attack_commit");
            FileLogger.Log("SURVIVAL][ROLE", "assault avoidance attack committed reason=" + reason +
                " damageSequence=" + _incomingDamageSequence);
        }

        private static void TickAssaultOpeningStealth(Character player, bool playerHidden)
        {
            if (_assaultOpeningStealthResolved || player == null) return;
            if (_assaultOpeningStealthDeadlineAt <= 0f)
                _assaultOpeningStealthDeadlineAt =
                    Time.time + AssaultOpeningStealthWindowSeconds;

            if (playerHidden)
            {
                _assaultOpeningStealthResolved = true;
                FileLogger.Log("SURVIVAL][ROLE", "assault opening stealth resolved=already_hidden");
                return;
            }

            if (Time.time > _assaultOpeningStealthDeadlineAt)
            {
                _assaultOpeningStealthResolved = true;
                FileLogger.Log("SURVIVAL][ROLE", "assault opening stealth resolved=timeout skill=" +
                    SurvivalCombatAdapter.HasSurvivalSkill(player, SkillType.kSkillHidden));
                return;
            }

            if (Time.time < _nextAssaultOpeningStealthAttemptAt) return;
            _nextAssaultOpeningStealthAttemptAt =
                Time.time + AssaultOpeningStealthRetrySeconds;
            if (!SurvivalCombatAdapter.IsSurvivalSkillReady(player, SkillType.kSkillHidden))
                return;
            if (!SurvivalCombatAdapter.TryUseSurvivalSkill(player, SkillType.kSkillHidden,
                "assault_round_opening_stealth"))
                return;

            _assaultOpeningStealthResolved = true;
            _assaultWasHidden = false;
            SetAssaultDirectorState(AssaultDirectorState.PreStealth,
                "round_opening_stealth");
            FileLogger.Log("SURVIVAL][ROLE", "assault opening stealth resolved=used");
        }

        private static Character SelectAssaultVisionThreat(Character player, out float bestScore)
        {
            Character best = null;
            bestScore = 0f;
            for (int i = 0; i < Enemies.Count; i++)
            {
                Character target = Enemies[i];
                if (!IsEmergencyTargetUsable(target)) continue;
                EnemyTrack track = GetEnemyTrack(target);
                float score;
                if (!IsAboutToGainVision(player, target, track, out score) || score <= bestScore) continue;
                best = target;
                bestScore = score;
            }
            return best;
        }

        private static Character SelectAssaultHuntThreat(Character player)
        {
            Character best = null;
            float bestScore = float.MinValue;
            for (int i = 0; i < Enemies.Count; i++)
            {
                Character target = Enemies[i];
                if (!IsEmergencyTargetUsable(target)) continue;
                EnemyTrack track = GetEnemyTrack(target);
                if (track == null || track.Invincible) continue;
                bool trigger = track.Hidden
                    ? track.Distance <= AssaultHiddenHuntDistance
                    : track.Distance <= AssaultVisibleHuntDistance && track.ClosingSpeed >= 0.30f;
                if (!trigger) continue;
                float score = (track.Hidden ? 200f : 100f) - track.Distance * 5f +
                    Mathf.Max(0f, track.ClosingSpeed) * 8f;
                if (score <= bestScore) continue;
                bestScore = score;
                best = target;
            }
            return best;
        }

        private static Character SelectAssaultStealthRetreatThreat(Character player)
        {
            Character best = null;
            float bestDistance = float.MaxValue;
            for (int i = 0; i < Enemies.Count; i++)
            {
                Character target = Enemies[i];
                if (!IsLivingOpponent(target)) continue;
                EnemyTrack track = GetEnemyTrack(target);
                float distance = track == null
                    ? XzDistance(player.transform.position, target.transform.position)
                    : track.Distance;
                float closingSpeed = track == null ? 0f : Mathf.Max(0f, track.ClosingSpeed);
                float predictedDistance = distance - closingSpeed * 2f;
                bool approaching = distance <= AssaultStealthRetreatPlanningDistance &&
                    (distance < AssaultStealthRetreatPreferredSeparation ||
                     closingSpeed >= 0.15f ||
                     predictedDistance < AssaultStealthRetreatPreferredSeparation);
                if (!approaching || distance >= bestDistance) continue;
                bestDistance = distance;
                best = target;
            }
            return best;
        }

        private static Character SelectNearestLivingOpponent(Character player)
        {
            if (player == null) return null;
            Character best = null;
            float bestDistance = float.MaxValue;
            for (int i = 0; i < Enemies.Count; i++)
            {
                Character target = Enemies[i];
                if (!IsLivingOpponent(target)) continue;
                float distance = XzDistance(player.transform.position, target.transform.position);
                if (distance >= bestDistance) continue;
                bestDistance = distance;
                best = target;
            }
            return best;
        }

        private static void TickAssaultStealthRetreat(Character player, Camera camera,
            Character approachThreat)
        {
            bool enteringRetreat = Phase != SurvivalBotPhase.Hide;
            Phase = SurvivalBotPhase.Hide;
            ClearEmergencyTarget("assault_stealth_retreat");
            SurvivalCombatAdapter.CancelSurvivalAttack();
            SurvivalCombatAdapter.CloseSurvivalScope(player);
            if (enteringRetreat)
            {
                _hasSafePoint = false;
                _stealthRetreatDestinationSafe = false;
                _nextSafePointAt = 0f;
            }

            Vector3 origin = player.transform.position;
            float currentSeparation = MinimumLivingEnemyDistance(origin);
            float destinationSeparation = _hasSafePoint
                ? MinimumLivingEnemyDistance(_safePoint)
                : 0f;
            bool arrived = _hasSafePoint && XzDistance(origin, _safePoint) < 1.1f;
            bool destinationInvalid = _hasSafePoint &&
                (_stealthRetreatDestinationSafe &&
                 destinationSeparation < AssaultStealthRetreatSeparation);
            bool routeFailed = IsRouteFailure("stealth_retreat");
            bool needSafePoint = !_hasSafePoint || routeFailed ||
                (!_stealthRetreatDestinationSafe && Time.time >= _nextSafePointAt) ||
                (destinationInvalid && Time.time >= _nextSafePointAt) ||
                (arrived && currentSeparation < AssaultStealthRetreatPreferredSeparation &&
                 Time.time >= _nextSafePointAt);
            if (needSafePoint)
            {
                _safePoint = SelectStealthRetreatPoint(player,
                    out _stealthRetreatDestinationSafe, out destinationSeparation);
                _hasSafePoint = true;
                _nextSafePointAt = Time.time +
                    (_stealthRetreatDestinationSafe ? 0.45f : 0.65f);
                _safePointLeaseUntil = Time.time + 1.2f;
                arrived = XzDistance(origin, _safePoint) < 1.1f;
            }

            bool separationSecured = currentSeparation >=
                AssaultStealthRetreatPreferredSeparation;
            Vector3 move = arrived && separationSecured
                ? Vector3.zero
                : SurvivalCombatAdapter.NavigateSurvival(player, _safePoint, true,
                    "stealth_retreat");
            if (move.sqrMagnitude > 0.01f && ShouldRejectStealthRetreatRoute(player))
            {
                MarkCandidateFailed(_safePoint);
                _hasSafePoint = false;
                _stealthRetreatDestinationSafe = false;
                _nextSafePointAt = 0f;
                SurvivalCombatAdapter.SuspendSurvivalNavigation(
                    "stealth_retreat_separation_reject");
                AutoBattleInput.ClearMovement();
                StatusText = "Stealth retreat | route rejected | separation floor 20.0m";
                return;
            }
            if (move.sqrMagnitude <= 0.01f && IsRouteFailure("stealth_retreat"))
            {
                MarkCandidateFailed(_safePoint);
                _hasSafePoint = false;
                _stealthRetreatDestinationSafe = false;
                _nextSafePointAt = 0f;
            }

            if (move.sqrMagnitude > 0.01f) AutoBattleInput.SetMoveWorld(player, move, false);
            else AutoBattleInput.ClearMovement();
            if (camera != null && move.sqrMagnitude > 0.01f)
                SurvivalCombatAdapter.LookSurvival(player, camera,
                    player.transform.position + move * 8f + Vector3.up);

            if (Time.time >= _nextStealthRetreatTraceAt)
            {
                _nextStealthRetreatTraceAt = Time.time + 0.8f;
                FileLogger.Log("SURVIVAL][ROLE", "stealth retreat threat=" +
                    (approachThreat == null ? 0 : approachThreat.uid) +
                    " currentMin=" + currentSeparation.ToString("0.0") +
                    " destinationMin=" + destinationSeparation.ToString("0.0") +
                    " hardSafe=" + _stealthRetreatDestinationSafe +
                    " arrived=" + arrived + " path=" + SurvivalCombatAdapter.LastPath);
            }
            StatusText = "Stealth retreat | all-enemy minimum " +
                currentSeparation.ToString("0.0") + "m / 20.0m | destination " +
                destinationSeparation.ToString("0.0") + "m | path " +
                SurvivalCombatAdapter.LastPath;
        }

        private static bool IsAboutToGainVision(Character player, Character target,
            EnemyTrack track, out float score)
        {
            score = 0f;
            if (player == null || target == null || track == null ||
                track.Distance > AssaultVisionPredictionDistance)
                return false;

            Vector3 predicted = track.Position + track.Velocity * AssaultVisionPredictionSeconds;
            bool clearNow = !HasBodyCoverFrom(target, track.Position, player.transform.position);
            bool clearFuture = !HasBodyCoverFrom(target, predicted, player.transform.position);
            bool facingNow = IsEnemyFacingPointFrom(target, track.Position,
                player.transform.position, 0.08f);
            bool facingFuture = IsEnemyFacingPointFrom(target, predicted,
                player.transform.position, 0.02f);
            Vector3 toPlayer = player.transform.position - track.Position;
            toPlayer.y = 0f;
            Vector3 velocity = track.Velocity;
            velocity.y = 0f;
            bool movingToward = velocity.sqrMagnitude > 0.04f && toPlayer.sqrMagnitude > 0.04f &&
                Vector3.Dot(velocity.normalized, toPlayer.normalized) >= 0.55f;
            bool soon = (clearNow && facingNow &&
                         track.Distance <= AssaultVisionPredictionDistance) ||
                (clearFuture && (facingFuture || movingToward) && track.ClosingSpeed >= 0.15f) ||
                (clearNow && track.Distance <= 10f && track.ClosingSpeed >= 0.35f);
            if (!soon) return false;
            score = (AssaultVisionPredictionDistance - track.Distance) * 4f +
                Mathf.Max(0f, track.ClosingSpeed) * 18f +
                (facingNow ? 45f : 0f) + (clearFuture ? 20f : 0f) +
                (track.Hidden ? 12f : 0f);
            return true;
        }

        private static void UpdateAssaultStationaryState(EnemyTrack track)
        {
            bool stationary = track != null && track.HorizontalSpeed <= 0.35f &&
                Mathf.Abs(track.ClosingSpeed) <= 0.30f;
            if (!stationary)
            {
                _assaultStationarySince = 0f;
                _assaultStationaryConfirmed = false;
                return;
            }
            if (_assaultStationarySince <= 0f) _assaultStationarySince = Time.time;
            _assaultStationaryConfirmed = Time.time - _assaultStationarySince >=
                AssaultStationaryConfirmSeconds;
        }

        private static bool HoldAssaultHiddenWatch(Character player, EnemyTrack track)
        {
            Phase = SurvivalBotPhase.Emergency;
            ClearEmergencyTarget("assault_hidden_watch");
            SurvivalCombatAdapter.SuspendSurvivalNavigation("assault_hidden_watch");
            SurvivalCombatAdapter.CancelSurvivalAttack();
            SurvivalCombatAdapter.CloseSurvivalScope(player);
            AutoBattleInput.ClearMovement();
            StatusText = "Assault director | hidden watch | distance " +
                track.Distance.ToString("0.0") + " | stationary " + _assaultStationaryConfirmed;
            TraceDirector("hidden_watch", track);
            return true;
        }

        private static bool TickAssaultHardHunt(Character player, Camera camera, EnemyTrack track)
        {
            if (player == null || camera == null || track == null ||
                !IsEmergencyTargetUsable(track.Target))
            {
                ResetAssaultDirector("hard_hunt_invalid");
                return false;
            }

            Character target = track.Target;
            bool hidden = track.Hidden;
            float distance = track.Distance;
            if (hidden && distance > AssaultHiddenHuntDistance &&
                _assaultDirectorState != AssaultDirectorState.RevealPursuit)
                return HoldAssaultHiddenWatch(player, track);

            ClearEmergencyTarget("assault_hard_hunt");
            Phase = SurvivalBotPhase.Emergency;
            SetAttackTarget(target, "assault_director_hard_lock");
            if (hidden && distance > AssaultHiddenHuntDistance)
            {
                SurvivalCombatAdapter.CancelSurvivalAttack();
                SurvivalCombatAdapter.CloseSurvivalScope(player);
                MoveAttackPursuit(player, target, camera, true);
                StatusText = "Assault director | reveal pursuit | distance " + distance.ToString("0.0");
                TraceDirector("reveal_pursuit", track);
                return true;
            }

            bool immediateFire = distance <= AssaultImmediateFireDistance;
            bool preAimReady = immediateFire || PreAimPursuitTarget(player, target, camera);
            if (!preAimReady)
            {
                // Keep the prepared aim and current scope stable while the camera converges.
                AutoBattleInput.ClearFire();
                if (hidden || !track.FireLine)
                    MoveAttackPursuit(player, target, camera, false);
                else
                    MoveCombatStrafe(player, target);
                SurvivalCombatAdapter.LogCombatState(player, target, track.FireLine,
                    distance, false);
                StatusText = "Assault director | pre-aim | distance " +
                    distance.ToString("0.0") + " | hidden " + hidden;
                TraceDirector("pre_aim", track);
                return true;
            }

            bool strictLine = false;
            bool fired = false;
            if (!track.Invincible)
                fired = SurvivalCombatAdapter.AttackEmergency(player, target, camera,
                    out strictLine, out distance);
            else
                SurvivalCombatAdapter.CancelSurvivalAttack();
            if (hidden || !strictLine)
                MoveAttackPursuit(player, target, camera, false);
            else MoveCombatStrafe(player, target);
            SurvivalCombatAdapter.LogCombatState(player, target, strictLine, distance, fired);
            StatusText = "Assault director | hard hunt | distance " + distance.ToString("0.0") +
                " | hidden " + hidden + " | immediate " + immediateFire + " | fired " + fired;
            TraceDirector("hard_hunt", track);
            return true;
        }

        private static void SetAssaultDirectorTarget(Character target, string reason)
        {
            if (_assaultDirectorTarget == target) return;
            int oldUid = _assaultDirectorTarget == null ? 0 : _assaultDirectorTarget.uid;
            int newUid = target == null ? 0 : target.uid;
            _assaultDirectorTarget = target;
            _assaultStationarySince = 0f;
            _assaultStationaryConfirmed = false;
            _assaultThreatLostAt = 0f;
            FileLogger.Log("SURVIVAL][ROLE", "assault target " + oldUid + "->" + newUid +
                " reason=" + reason);
        }

        private static void SetAssaultDirectorState(AssaultDirectorState state, string reason)
        {
            if (_assaultDirectorState == state) return;
            AssaultDirectorState previous = _assaultDirectorState;
            _assaultDirectorState = state;
            _assaultDirectorStateStartedAt = Time.time;
            FileLogger.Log("SURVIVAL][ROLE", "assault state " + previous + "->" + state +
                " reason=" + reason + " uid=" +
                (_assaultDirectorTarget == null ? 0 : _assaultDirectorTarget.uid));
        }

        private static void ResetAssaultDirector(string reason)
        {
            if (_assaultDirectorTarget != null || _assaultDirectorState != AssaultDirectorState.Idle)
                FileLogger.Log("SURVIVAL][ROLE", "assault reset reason=" + reason + " uid=" +
                    (_assaultDirectorTarget == null ? 0 : _assaultDirectorTarget.uid));
            _assaultDirectorTarget = null;
            _assaultDirectorState = AssaultDirectorState.Idle;
            _assaultDirectorStateStartedAt = 0f;
            _assaultThreatLostAt = 0f;
            _assaultStationarySince = 0f;
            _assaultStationaryConfirmed = false;
            _assaultWasHidden = false;
        }

        private static void TraceDirector(string mode, EnemyTrack track)
        {
            if (Time.time < _nextDirectorTraceAt || track == null) return;
            _nextDirectorTraceAt = Time.time + 0.8f;
            FileLogger.Log("SURVIVAL][ROLE", "director=" + mode + " role=" + _currentRole +
                " state=" + _assaultDirectorState + " uid=" + track.Uid +
                " dist=" + track.Distance.ToString("0.0") +
                " closing=" + track.ClosingSpeed.ToString("0.0") +
                " speed=" + track.HorizontalSpeed.ToString("0.0") +
                " hidden=" + track.Hidden + " stationary=" + _assaultStationaryConfirmed);
        }

        private static Character SelectNearestDirectorTarget(Character player, float maxDistance)
        {
            Character best = null;
            float bestDistance = maxDistance + 0.01f;
            for (int i = 0; i < Enemies.Count; i++)
            {
                Character target = Enemies[i];
                if (!IsEmergencyTargetUsable(target)) continue;
                EnemyTrack track = GetEnemyTrack(target);
                float distance = track == null
                    ? XzDistance(player.transform.position, target.transform.position)
                    : track.Distance;
                if (distance >= bestDistance) continue;
                bestDistance = distance;
                best = target;
            }
            return best;
        }

        private static bool HasBodyCoverFrom(Character observer, Vector3 observerPosition,
            Vector3 point)
        {
            if (observer == null) return true;
            Vector3 from = observerPosition + Vector3.up * 1.25f;
            int blocked = 0;
            if (HasMapBlock(from, point + Vector3.up * 0.55f)) blocked++;
            if (HasMapBlock(from, point + Vector3.up * 1.05f)) blocked++;
            if (HasMapBlock(from, point + Vector3.up * 1.45f)) blocked++;
            return blocked >= 2;
        }

        private static bool IsEnemyFacingPointFrom(Character enemy, Vector3 enemyPosition,
            Vector3 point, float threshold)
        {
            try
            {
                Vector3 toPoint = point + Vector3.up - (enemyPosition + Vector3.up * 1.2f);
                if (toPoint.sqrMagnitude < 0.01f) return true;
                toPoint.Normalize();
                Vector3 forward = Quaternion.Euler(enemy.lookdirection) * Vector3.forward;
                if (forward.sqrMagnitude < 0.01f) forward = enemy.transform.forward;
                forward.Normalize();
                return Vector3.Dot(forward, toPoint) >= threshold;
            }
            catch { return false; }
        }

        private static void ResetOffensiveDirector(string reason)
        {
            _guardArrowTarget = null;
            _guardArrowTargetLostAt = 0f;
            ResetAssaultDirector(reason);
        }

        private static void ResetRoleDirector(string reason)
        {
            _currentRole = SurvivalRoleKind.Unknown;
            _nextRoleDetectAt = 0f;
            _healthDamageSequence = 0;
            _handledHealthDamageSequence = 0;
            _incomingDamageSequence = 0;
            _avoidanceHandledDamageSequence = 0;
            _roleDamageResponseUntil = 0f;
            _nextRoleSkillAttemptAt = 0f;
            _heavyShieldPending = false;
            _heavyGallopPending = false;
            _guardHealPending = false;
            _assaultHiddenPending = false;
            _nextDirectorTraceAt = 0f;
            _assaultOpeningStealthResolved = false;
            _assaultOpeningStealthDeadlineAt = 0f;
            _nextAssaultOpeningStealthAttemptAt = 0f;
            _avoidanceWasHidden = false;
            _avoidanceAttackCommitted = false;
            _stealthRetreatActive = false;
            _stealthRetreatDestinationSafe = false;
            _nextStealthRetreatTraceAt = 0f;
            ResetOffensiveDirector(reason);
        }

        private static bool TickEmergencyCounterattack(Character player, Camera camera, bool objectiveComplete)
        {
            if (player == null || camera == null) return false;

            float triggerDistance = SurvivalBotSettings.EmergencyDistance;
            if (!IsEmergencyTargetUsable(_emergencyTarget)) ClearEmergencyTarget("target_invalid");
            float contenderScore;
            Character contender = SelectEmergencyThreat(player, triggerDistance, out contenderScore);
            if (_emergencyTarget != null && contender != null && contender != _emergencyTarget &&
                Time.time - _emergencyTargetLockedAt >= 0.25f)
            {
                float currentScore = ScoreEmergencyThreat(player, _emergencyTarget, triggerDistance);
                float currentDistance = XzDistance(player.transform.position, _emergencyTarget.transform.position);
                float contenderDistance = XzDistance(player.transform.position, contender.transform.position);
                if (contenderScore >= currentScore + 30f || contenderDistance + 2f < currentDistance)
                {
                    ClearEmergencyTarget("higher_threat");
                }
            }
            if (_emergencyTarget == null)
            {
                if (contender != null && contender == _emergencyReleasedTarget &&
                    Time.time < _emergencyReleasedUntil)
                    return false;
                _emergencyTarget = contender;
                if (_emergencyTarget == null) return false;
                _emergencyTargetLockedAt = Time.time;
                EnemyTrack acquiredTrack = GetEnemyTrack(_emergencyTarget);
                _emergencyTargetVisibleAt = acquiredTrack != null && acquiredTrack.Visible ? Time.time : 0f;
                FileLogger.Log("SURVIVAL", "emergency counterattack start uid=" + _emergencyTarget.uid +
                    " dist=" + XzDistance(player.transform.position, _emergencyTarget.transform.position).ToString("0.0") +
                    " threat=" + contenderScore.ToString("0") + " hidden=" + IsTargetHidden(_emergencyTarget) +
                    " line=" + (acquiredTrack != null && acquiredTrack.FireLine) +
                    " facing=" + (acquiredTrack != null && acquiredTrack.FacingPlayer) +
                    " closing=" + (acquiredTrack == null ? "-" : acquiredTrack.ClosingSpeed.ToString("0.0")) +
                    " recentDamage=" + (Time.time - _recentDamageAt <= 1.2f));
            }

            float distance = XzDistance(player.transform.position, _emergencyTarget.transform.position);
            bool hidden = IsTargetHidden(_emergencyTarget);
            bool strictLine = SurvivalCombatAdapter.SurvivalHasEmergencyFireLine(player, _emergencyTarget, camera);
            EnemyTrack track = GetEnemyTrack(_emergencyTarget);
            if (strictLine) _emergencyTargetVisibleAt = Time.time;
            bool closing = track != null && track.ClosingSpeed >= 0.8f;
            bool recentlyDamaged = Time.time - _recentDamageAt <= 1.2f;
            float visibleThreatLimit = GetEmergencyVisibleThreatLimit(triggerDistance);
            bool activeVisibleThreat = IsActiveVisibleEmergencyThreat(track, strictLine,
                recentlyDamaged, distance, visibleThreatLimit);
            bool retainedVisibleThreat = !hidden && distance <= visibleThreatLimit &&
                Time.time - _emergencyTargetVisibleAt <= EmergencyVisibleRetentionSeconds &&
                track != null && (track.FacingPlayer || closing || recentlyDamaged);
            float releaseDistance = hidden ? 6.8f :
                (activeVisibleThreat || retainedVisibleThreat ? visibleThreatLimit : triggerDistance + 4f);
            bool holdThreat = distance <= 4.5f || (hidden && distance <= 6f) ||
                (distance <= releaseDistance &&
                 (strictLine || Time.time - _emergencyTargetVisibleAt <= 0.8f || closing || recentlyDamaged));
            if (!holdThreat)
            {
                FileLogger.Log("SURVIVAL", "emergency threat release uid=" + _emergencyTarget.uid +
                    " dist=" + distance.ToString("0.0") + " limit=" + releaseDistance.ToString("0.0") +
                    " line=" + strictLine + " facing=" + (track != null && track.FacingPlayer) +
                    " closing=" + (track == null ? "-" : track.ClosingSpeed.ToString("0.0")) +
                    " recentDamage=" + recentlyDamaged);
                ClearEmergencyTarget("threat_released");
                return false;
            }

            if (Phase != SurvivalBotPhase.Emergency)
                SurvivalCombatAdapter.SuspendSurvivalNavigation("emergency");
            Phase = SurvivalBotPhase.Emergency;
            if (!strictLine)
            {
                SurvivalCombatAdapter.CancelSurvivalAttack();
                if (distance > 6f) SurvivalCombatAdapter.CloseSurvivalScope(player);
                MoveEmergency(player, _emergencyTarget, distance);
                StatusText = "近敌反击 | 目标短暂遮挡 | 距离 " + distance.ToString("0.0");
                return true;
            }

            bool invincible = track != null && track.Invincible;
            if (invincible || (objectiveComplete && distance > 6f))
            {
                SurvivalCombatAdapter.CancelSurvivalAttack();
                SurvivalCombatAdapter.CloseSurvivalScope(player);
                MoveEmergency(player, _emergencyTarget, distance);
                StatusText = invincible
                    ? "近敌威胁 | 目标无敌，优先脱离"
                    : "任务已完成 | 优先脱离追兵 | 距离 " + distance.ToString("0.0");
                return true;
            }

            bool fired = SurvivalCombatAdapter.AttackEmergency(player, _emergencyTarget, camera,
                out strictLine, out distance);
            MoveCombatStrafe(player, _emergencyTarget);
            SurvivalCombatAdapter.LogCombatState(player, _emergencyTarget, strictLine, distance, fired);
            StatusText = "近敌反击 | 目标 " + SafeName(_emergencyTarget) + " | 距离 " +
                distance.ToString("0.0") + " / " + triggerDistance.ToString("0.0") + " | 隐身 " +
                IsTargetHidden(_emergencyTarget) + " | 开火 " + fired;
            return true;
        }

        private static void ClearEmergencyTarget(string reason)
        {
            if (_emergencyTarget != null)
            {
                FileLogger.Log("SURVIVAL", "emergency counterattack stop uid=" + _emergencyTarget.uid +
                    " reason=" + reason);
                if (string.Equals(reason, "threat_released", StringComparison.Ordinal))
                {
                    _emergencyReleasedTarget = _emergencyTarget;
                    _emergencyReleasedUntil = Time.time + 0.75f;
                }
            }
            _emergencyTarget = null;
            _emergencyTargetVisibleAt = 0f;
            _emergencyTargetLockedAt = 0f;
        }

        private static void TickHide(Character player, Camera camera)
        {
            bool enteringHide = Phase != SurvivalBotPhase.Hide;
            Phase = SurvivalBotPhase.Hide;
            if (enteringHide)
            {
                _hasSafePoint = false;
                _nextSafePointAt = 0f;
            }
            SurvivalCombatAdapter.CancelSurvivalAttack();
            SurvivalCombatAdapter.CloseSurvivalScope(player);
            int exposure = CountExposure(player.transform.position, player);
            bool exposureStarted = exposure > 0 && _lastExposureCount == 0;
            _lastExposureCount = exposure;
            bool arrived = _hasSafePoint && XzDistance(player.transform.position, _safePoint) < 1.1f;
            bool routeFailed = IsRouteFailure("hide");
            bool needSafePoint = !_hasSafePoint || routeFailed ||
                exposureStarted ||
                (arrived && exposure > 0) ||
                (exposure > 0 && Time.time >= _nextSafePointAt) ||
                (arrived && Time.time >= _safePointLeaseUntil);
            if (needSafePoint)
            {
                _safePoint = SelectSafetyPoint(player);
                _hasSafePoint = true;
                _nextSafePointAt = Time.time + SurvivalBotSettings.SafePointRefreshSeconds;
                _safePointLeaseUntil = Time.time + UnityEngine.Random.Range(2.5f, 4.5f);
                arrived = XzDistance(player.transform.position, _safePoint) < 1.1f;
            }

            Vector3 move = arrived && exposure == 0
                ? Vector3.zero
                : SurvivalCombatAdapter.NavigateSurvival(player, _safePoint, true, "hide");
            if (move.sqrMagnitude > 0.01f && ShouldRejectExposedHideRoute(player))
            {
                MarkCandidateFailed(_safePoint);
                _hasSafePoint = false;
                _nextSafePointAt = 0f;
                SurvivalCombatAdapter.SuspendSurvivalNavigation("hide_visibility_reject");
                AutoBattleInput.ClearMovement();
                StatusText = "Hide route rejected | enemy sight lane ahead";
                return;
            }
            if (move.sqrMagnitude <= 0.01f && IsRouteFailure("hide"))
            {
                MarkCandidateFailed(_safePoint);
                _hasSafePoint = false;
                _nextSafePointAt = 0f;
            }
            if (move.sqrMagnitude > 0.01f) AutoBattleInput.SetMoveWorld(player, move, false);
            else AutoBattleInput.ClearMovement();

            if (camera != null && move.sqrMagnitude > 0.01f)
                SurvivalCombatAdapter.LookSurvival(player, camera, player.transform.position + move * 8f + Vector3.up);

            StatusText = "躲避模式 | 初始 " + Math.Max(InitialPlayers, ParticipantIds.Count) +
                " | 存活 " + RemainingPlayers + " | 暴露 " + exposure +
                " | 路径 " + SurvivalCombatAdapter.LastPath;
        }

        private static void TickAttack(Character player, Camera camera, bool opportunityOnly)
        {
            bool enteringAttack = Phase != SurvivalBotPhase.Attack;
            Phase = SurvivalBotPhase.Attack;
            if (enteringAttack) _hasSafePoint = false;
            string modeName = opportunityOnly ? "机会攻击" : "强制猎杀";
            if (opportunityOnly && Time.time < _opportunityCooldownUntil)
            {
                TickHide(player, camera);
                StatusText = modeName + " | 等待安全机会";
                return;
            }

            Character visibleTarget = SelectBestVisibleTarget(player, camera, opportunityOnly);
            bool currentVisible = IsAttackTargetUsable(_attackTarget) &&
                SurvivalCombatAdapter.SurvivalHasStrictFireLine(player, _attackTarget, camera);
            if (opportunityOnly && _attackTarget != null && !IsSafeOpportunityTarget(player, _attackTarget))
            {
                SetAttackTarget(null, "opportunity_risk");
                currentVisible = false;
            }
            if (currentVisible) _attackTargetVisibleAt = Time.time;
            if (!IsAttackTargetUsable(_attackTarget))
            {
                SetAttackTarget(visibleTarget, "acquire");
            }
            else if (visibleTarget != null && visibleTarget != _attackTarget &&
                ShouldSwitchAttackTarget(player, _attackTarget, visibleTarget, currentVisible))
            {
                SetAttackTarget(visibleTarget, "score_advantage");
                currentVisible = true;
            }
            else if (!currentVisible && Time.time - _attackTargetVisibleAt > 0.75f)
            {
                SetAttackTarget(null, "line_lost");
            }

            if (_attackTarget == null)
            {
                SurvivalCombatAdapter.CancelSurvivalAttack();
                SurvivalCombatAdapter.CloseSurvivalScope(player);
                if (opportunityOnly)
                {
                    TickHide(player, camera);
                    StatusText = modeName + " | 无安全直线目标";
                    return;
                }

                Character searchTarget = SelectSearchTarget(player);
                if (searchTarget == null)
                {
                    AutoBattleInput.ClearMovement();
                    StatusText = modeName + " | 搜敌中 | 无可用目标 | 已关镜";
                    return;
                }

                TickAttackSearch(player, camera, searchTarget);
                StatusText = modeName + " | 搜敌中 | 最近候选 " + SafeName(searchTarget) +
                    " | 路径 " + SurvivalCombatAdapter.LastPath + " | 已关镜";
                return;
            }

            int targetVital = CharacterVital(_attackTarget);
            if (_attackTargetLastVital > 0 && targetVital < _attackTargetLastVital)
                _attackLastDamageAt = Time.time;
            _attackTargetLastVital = targetVital;
            float engagementElapsed = Time.time - _attackEngagementStartedAt;
            if (opportunityOnly && engagementElapsed >= 2.5f)
            {
                SurvivalCombatAdapter.CancelSurvivalAttack();
                SetAttackTarget(null, "opportunity_hard_budget");
                _opportunityCooldownUntil = Time.time + 1.5f;
                TickHide(player, camera);
                StatusText = modeName + " | 机会窗口结束，返回掩体";
                return;
            }

            bool strictLine;
            float distance;
            bool fired = SurvivalCombatAdapter.AttackSurvival(player, _attackTarget, camera, out strictLine, out distance);
            if (strictLine && opportunityOnly) SurvivalCombatAdapter.SuspendSurvivalNavigation("combat");
            SurvivalCombatAdapter.LogCombatState(player, _attackTarget, strictLine, distance, fired);
            if (!opportunityOnly && engagementElapsed > 8f && Time.time - _attackLastDamageAt > 2.5f)
            {
                SurvivalCombatAdapter.CancelSurvivalAttack();
                SetAttackTarget(null, "engagement_budget");
                _opportunityCooldownUntil = Time.time + 0.25f;
                return;
            }
            if (!strictLine)
            {
                if (opportunityOnly)
                {
                    SurvivalCombatAdapter.CancelSurvivalAttack();
                    SetAttackTarget(null, "opportunity_line_lost");
                    TickHide(player, camera);
                    StatusText = modeName + " | 目标离开直线，返回掩体";
                    return;
                }
                if (Time.time - _attackTargetVisibleAt <= 0.75f)
                {
                    TickAttackSearch(player, camera, _attackTarget);
                    StatusText = modeName + " | 目标短暂遮挡，保持锁定";
                    return;
                }
                SurvivalCombatAdapter.CloseSurvivalScope(player);
                SetAttackTarget(null, "strict_line_lost");
                AutoBattleInput.ClearMovement();
                StatusText = modeName + " | 目标刚失去视线，重新搜敌 | 已关镜";
                return;
            }

            MoveCombatStrafe(player, _attackTarget);
            StatusText = modeName + " | 存活 " + RemainingPlayers + " | 目标 " + SafeName(_attackTarget) +
                " | 距离 " + distance.ToString("0.0") + " | 直线 " + strictLine + " | 开火 " + fired;
        }

        private static void TickAttackSearch(Character player, Camera camera, Character searchTarget)
        {
            Vector3 targetPosition = searchTarget.transform.position;
            float targetMoved = _hasAttackPoint ? XzDistance(_attackPointTargetPosition, targetPosition) : float.MaxValue;
            Vector3 move = MoveAttackPursuit(player, searchTarget, camera, true);
            if (SurvivalCombatAdapter.LastActualPathProgressAt > _attackPointLastProgressAt)
                _attackPointLastProgressAt = SurvivalCombatAdapter.LastActualPathProgressAt;
            if (move.sqrMagnitude <= 0.01f && IsRouteFailure("attack_chase") &&
                Time.time - _attackPointLastProgressAt >= 1.5f)
            {
                FileLogger.Log("SURVIVAL", "hunt chase retry uid=" + searchTarget.uid +
                    " goal=" + FormatVec(_attackPoint) + " path=" + SurvivalCombatAdapter.LastPath);
                SurvivalCombatAdapter.SuspendSurvivalNavigation("attack_chase_recover");
                _attackPointLastProgressAt = Time.time;
            }
            TraceAttackSearch(player, searchTarget, move, targetMoved, null);
        }

        private static void TickSuicide(GameApp app, Character player, Camera camera)
        {
            if (_suicideStartedAt <= 0f)
            {
                _suicideStartedAt = Time.time;
                _nextCliffScanAt = 0f;
                _hasCliff = false;
                _cliffSearchJob = null;
                _lastCliffDistance = float.MaxValue;
                _lastCliffProgressAt = Time.time;
                _nextCliffTraceAt = 0f;
                _cliffJumpLogged = false;
                _cliffJumpStartedAt = 0f;
                _nextCliffJumpTraceAt = 0f;
                _cliffJumpStart = Vector3.zero;
                _nextSuicideRequestAt = 0f;
                _serverSuicideRequested = false;
                ClearFailedCliffCandidates();
                AutoBattleInput.ClearAll();
                FileLogger.Log("SURVIVAL", "suicide phase started; cliff preferred fallback=" +
                    SurvivalBotSettings.SuicideFallbackSeconds.ToString("0") + "s");
            }
            Phase = SurvivalBotPhase.Suicide;

            if (_serverSuicideRequested)
            {
                AutoBattleInput.ClearAll();
                StatusText = "已请求服务器自杀，等待结算";
                return;
            }

            if (!_hasCliff && (_cliffSearchJob != null || Time.time >= _nextCliffScanAt))
            {
                string cliffDetail;
                CliffSearchStatus searchStatus = TickFindCliff(player, out _cliffEdge,
                    out _cliffOutward, out cliffDetail);
                if (searchStatus == CliffSearchStatus.Found)
                {
                    _hasCliff = true;
                    _nextCliffScanAt = Time.time + 2f;
                    _lastCliffDistance = XzDistance(player.transform.position, _cliffEdge);
                    _lastCliffProgressAt = Time.time;
                    _cliffJumpLogged = false;
                    FileLogger.Log("SURVIVAL", "reachable cliff candidate edge=" + FormatVec(_cliffEdge) +
                        " outward=" + FormatVec(_cliffOutward) + " dist=" + _lastCliffDistance.ToString("0.0") +
                        " " + cliffDetail);
                }
                else if (searchStatus == CliffSearchStatus.Exhausted)
                {
                    _nextCliffScanAt = Time.time + 2f;
                    if (Time.time >= _nextCliffTraceAt)
                    {
                        _nextCliffTraceAt = Time.time + 4f;
                        FileLogger.Log("SURVIVAL", "no verified cliff; waiting for server-suicide fallback " +
                            cliffDetail);
                    }
                }
            }

            if (Time.time - _suicideStartedAt >= SurvivalBotSettings.SuicideFallbackSeconds &&
                Time.time >= _nextSuicideRequestAt &&
                app != null && app.channel_connection != null)
            {
                _nextSuicideRequestAt = Time.time + 2f;
                AutoBattleInput.ClearAll();
                try
                {
                    app.channel_connection.Suicide(player.uid);
                    _serverSuicideRequested = true;
                    StatusText = "未找到可用悬崖，已请求服务器自杀";
                    FileLogger.Log("SURVIVAL", "death request=server_suicide_sent uid=" + player.uid +
                        " reason=cliff_timeout position=" + FormatVec(player.transform.position));
                }
                catch (Exception ex)
                {
                    StatusText = "服务器自杀请求失败，准备重试";
                    FileLogger.Log("SURVIVAL", "server suicide request failed: " + ex.Message);
                }
                return;
            }

            if (_cliffJumpLogged)
            {
                Vector3 position = player.transform.position;
                float elapsed = Mathf.Max(0f, Time.time - _cliffJumpStartedAt);
                float verticalDrop = _cliffJumpStart.y - position.y;
                Vector3 displacement = position - _cliffJumpStart;
                displacement.y = 0f;
                float forwardProgress = Vector3.Dot(displacement, _cliffOutward);
                bool grounded = SafeIsOnGround(player);
                if (Time.time >= _nextCliffJumpTraceAt)
                {
                    _nextCliffJumpTraceAt = Time.time + 0.5f;
                    FileLogger.Log("SURVIVAL", "cliff jump trace elapsed=" + elapsed.ToString("0.0") +
                        " pos=" + FormatVec(position) + " drop=" + verticalDrop.ToString("0.0") +
                        " forward=" + forwardProgress.ToString("0.0") +
                        " grounded=" + grounded);
                }

                bool blocked = elapsed >= 0.9f && grounded && forwardProgress < 0.60f;
                bool landedWithoutDrop = elapsed >= 1.8f && grounded && verticalDrop < 3.0f;
                bool noFall = elapsed >= 2.2f && verticalDrop < 1.5f;
                if (blocked || landedWithoutDrop || noFall)
                {
                    AbandonCliffCandidate(blocked ? "jump_blocked" :
                        (landedWithoutDrop ? "landed_without_fatal_drop" : "jump_no_fall"),
                        position, verticalDrop, forwardProgress);
                    return;
                }

                AutoBattleInput.SetMoveWorld(player, _cliffOutward, false);
                if (elapsed <= 0.85f)
                    AutoBattleInput.HoldAction(ActionType.kActionJump, 0.18f);
                StatusText = "任务完成，保持向外坠落";
                return;
            }

            if (_hasCliff)
            {
                float edgeDistance = XzDistance(player.transform.position, _cliffEdge);
                if (edgeDistance + 0.35f < _lastCliffDistance)
                {
                    _lastCliffDistance = edgeDistance;
                    _lastCliffProgressAt = Time.time;
                }
                else if (Time.time - _lastCliffProgressAt > 5f)
                {
                    MarkCliffCandidateFailed(_cliffEdge);
                    _hasCliff = false;
                    _nextCliffScanAt = 0f;
                    AutoBattleInput.ClearMovement();
                    StatusText = "悬崖路径无进展，重新搜索";
                    FileLogger.Log("SURVIVAL", "cliff candidate abandoned reason=no_progress edge=" +
                        FormatVec(_cliffEdge) + " dist=" + edgeDistance.ToString("0.0"));
                    return;
                }

                if (edgeDistance > 1.1f)
                {
                    Vector3 move = SurvivalCombatAdapter.NavigateSurvival(player, _cliffEdge, false, "suicide");
                    if (move.sqrMagnitude <= 0.01f && IsRouteFailure("suicide"))
                    {
                        FileLogger.Log("SURVIVAL", "cliff candidate abandoned reason=" +
                            SurvivalCombatAdapter.LastPath + " edge=" + FormatVec(_cliffEdge));
                        MarkCliffCandidateFailed(_cliffEdge);
                        _hasCliff = false;
                        _nextCliffScanAt = 0f;
                        AutoBattleInput.ClearMovement();
                        return;
                    }
                    if (move.sqrMagnitude > 0.01f) AutoBattleInput.SetMoveWorld(player, move, false);
                    if (camera != null)
                        SurvivalCombatAdapter.LookSurvival(player, camera, player.transform.position + _cliffOutward * 8f);
                    StatusText = "任务完成，前往悬崖 | 距离 " + edgeDistance.ToString("0.0");
                    return;
                }

                string launchDetail;
                float verifiedDrop;
                if (!TryValidateCliffApproach(_cliffEdge, _cliffOutward,
                    player.transform.root, out verifiedDrop, out launchDetail))
                {
                    AbandonCliffCandidate("launch_recheck:" + launchDetail,
                        player.transform.position, 0f, 0f);
                    return;
                }

                AutoBattleInput.SetMoveWorld(player, _cliffOutward, false);
                AutoBattleInput.PressAction(ActionType.kActionJump, 0.12f);
                AutoBattleInput.HoldAction(ActionType.kActionJump, 0.38f);
                if (!_cliffJumpLogged)
                {
                    _cliffJumpLogged = true;
                    _cliffJumpStartedAt = Time.time;
                    _nextCliffJumpTraceAt = 0f;
                    _cliffJumpStart = player.transform.position;
                    FileLogger.Log("SURVIVAL", "cliff jump issued edge=" + FormatVec(_cliffEdge) +
                        " outward=" + FormatVec(_cliffOutward) + " drop=" +
                        verifiedDrop.ToString("0.0") + " " + launchDetail);
                }
                StatusText = "任务完成，跳崖结束对局";
                return;
            }

            if (TickEmergencyCounterattack(player, camera, true)) return;
            MoveWhileSearchingCliff(player, camera);
        }

        private static void MoveWhileSearchingCliff(Character player, Camera camera)
        {
            Character threat = SelectNearestDirectorTarget(player, 26f);
            int checkedCount = _cliffSearchJob == null ? 0 : _cliffSearchJob.Cursor;
            int totalCount = _cliffSearchJob == null ? 0 : _cliffSearchJob.CandidateCount;
            if (threat == null)
            {
                AutoBattleInput.ClearMovement();
                StatusText = "任务完成，搜索悬崖 | " + checkedCount + "/" + totalCount;
                return;
            }

            SurvivalCombatAdapter.CancelSurvivalAttack();
            SurvivalCombatAdapter.CloseSurvivalScope(player);
            float distance = XzDistance(player.transform.position, threat.transform.position);
            MoveEmergency(player, threat, distance);
            if (camera != null)
                SurvivalCombatAdapter.LookSurvival(player, camera,
                    threat.transform.position + Vector3.up * 0.82f);
            StatusText = "任务完成，搜索悬崖并规避敌人 | " + checkedCount + "/" +
                totalCount + " | 威胁 " + distance.ToString("0.0") + "m";
        }

        private static void HandleGmExit(GameApp app)
        {
            _gmHandledThisRound = true;
            _roundEndedByGm = true;
            _consecutiveGmRounds++;
            Phase = SurvivalBotPhase.GmExit;
            AutoBattleInput.ClearAll();
            _nextGmLeaveAt = 0f;
            StatusText = "检测到 GM/观战候选，正在退出 | 连续 " + _consecutiveGmRounds + "/" +
                SurvivalBotSettings.GmStopRounds;
            FileLogger.Log("GM", StatusText + " uid=" + _pendingGmUid + " team=" + _pendingGmTeam);
            MaintainGmExit(app);

            if (_consecutiveGmRounds >= SurvivalBotSettings.GmStopRounds)
                Stop("three_consecutive_gm_rounds");
        }

        private static void MaintainGmExit(GameApp app)
        {
            Phase = SurvivalBotPhase.GmExit;
            AutoBattleInput.ClearAll();
            if (Time.time < _nextGmLeaveAt || app == null || app.channel_connection == null) return;
            _nextGmLeaveAt = Time.time + 1.5f;
            try { app.channel_connection.LeaveGame(); }
            catch (Exception ex) { FileLogger.Log("GM", "LeaveGame failed: " + ex.Message); }
        }

        private static void TickLobby(GameApp app)
        {
            TraceLobbyGate(app);

            if (_cardManager != null || _awaitingReward)
            {
                Phase = SurvivalBotPhase.Balance;
                if (_cardManager == null && Time.time - _rewardWaitStartedAt >= 12f && !IsBalanceState(app))
                {
                    _awaitingReward = false;
                    _nextMatchAt = Time.time + 1f;
                    StatusText = "未出现翻牌界面，继续匹配";
                    FileLogger.Log("CARD", "reward gate released after timeout");
                }
                return;
            }

            if (app == null || app.lobby_connection == null)
            {
                Phase = SurvivalBotPhase.Lobby;
                StatusText = "等待大厅连接";
                return;
            }

            if (GlobalStatic.halfQuitStateTime > 0)
            {
                Phase = SurvivalBotPhase.Lobby;
                StatusText = "逃跑者禁赛剩余 " + Mathf.CeilToInt(GlobalStatic.halfQuitStateTime) + " 秒";
                if (Time.time >= _nextPunishRefreshAt)
                {
                    _nextPunishRefreshAt = Time.time + 15f;
                    try { app.lobby_connection.RestoreToStateLobbyTop(false); } catch { }
                }
                return;
            }

            if (_matching)
            {
                Phase = SurvivalBotPhase.Matching;
                float elapsed = Time.time - _matchStartedAt;
                StatusText = "匹配生存模式 " + elapsed.ToString("0") + "/" +
                    SurvivalBotSettings.MatchTimeoutSeconds.ToString("0") + " 秒";
                if (elapsed >= SurvivalBotSettings.MatchTimeoutSeconds && !_cancelPending)
                {
                    _cancelPending = true;
                    _cancelRequestedAt = Time.time;
                    try { app.lobby_connection.RequestCancelMatching(); } catch { }
                    FileLogger.Log("MATCH", "matching timeout; cancel requested seconds=" +
                        SurvivalBotSettings.MatchTimeoutSeconds.ToString("0"));
                }
                else if (_cancelPending && Time.time - _cancelRequestedAt > 6f)
                {
                    _cancelRequestedAt = Time.time;
                    try { app.lobby_connection.RequestCancelMatching(); } catch { }
                    FileLogger.Log("MATCH", "cancel response timeout; cancel requested again");
                }
                return;
            }

            if (Time.time < _nextMatchAt)
            {
                Phase = SurvivalBotPhase.Lobby;
                StatusText = "等待重新匹配";
                return;
            }

            if (app.lobby_connection.state != LobbyConnection.State.kInLobby)
            {
                Phase = SurvivalBotPhase.Lobby;
                StatusText = "等待返回频道大厅";
                return;
            }

            string rainLoadGate;
            if (!RuntimeRainNavMesh.CanStartHighDetailSceneLoad(out rainLoadGate))
            {
                Phase = SurvivalBotPhase.Lobby;
                StatusText = "RAIN 加载门禁 | " + rainLoadGate;
                return;
            }

            try
            {
                NewUIRoom roomUi = NewUIRoom.getInstance();
                if (roomUi == null)
                {
                    if (Time.time >= _nextRoomLoadAt)
                    {
                        _nextRoomLoadAt = Time.time + 3f;
                        UILobby lobby = UILobby.instance;
                        if (lobby == null)
                        {
                            lobby = AssetPrefabManager.GetInstance().LoadSingletonGameObject<UILobby>(
                                prefabNameEnum.Lobby.ToString());
                        }

                        if (lobby != null)
                        {
                            FileLogger.Log("MATCH", "room UI missing; invoking native StartGameBtn page=" +
                                lobby.LobbyButtonPage);
                            lobby.StartGameBtn(null);
                        }
                        else
                        {
                            FileLogger.Log("MATCH", "room UI missing; lobby view is not ready");
                        }
                    }
                    _nextMatchAt = Time.time + 1f;
                    StatusText = "正在进入开始游戏界面";
                    return;
                }

                if (roomUi.InMatch)
                {
                    _pendingSurvivalMatchRequest = true;
                    _matching = true;
                    _cancelPending = false;
                    _matchStartedAt = Time.time;
                    Phase = SurvivalBotPhase.Matching;
                    StatusText = "已接管当前匹配";
                    FileLogger.Log("MATCH", "existing room matching state adopted");
                    return;
                }

                string hookNum = GlobalStatic.hookNum;
                try
                {
                    // Automated matching must not stall on the UI verification branch.
                    GlobalStatic.hookNum = "0";
                    FileLogger.Log("MATCH", "auto match attempt path=room-ui");
                    roomUi.TeamMatchOnClick(RoomInfo.GameType.kGameTypeChiji);
                }
                catch (Exception ex)
                {
                    FileLogger.Log("MATCH", "native room UI attempt failed: " + ex.Message);
                }
                finally
                {
                    GlobalStatic.hookNum = hookNum;
                }

                if (!_matching)
                {
                    _nextMatchAt = Time.time + 3f;
                    StatusText = "大厅尚未允许匹配，准备重试";
                    FileLogger.Log("MATCH", "native room UI did not send request; retry armed");
                }
            }
            catch (Exception ex)
            {
                _nextMatchAt = Time.time + 5f;
                StatusText = "匹配请求失败: " + ex.Message;
                FileLogger.Log("MATCH", StatusText);
            }
        }

        private static void TraceLobbyGate(GameApp app)
        {
            if (Time.time < _nextLobbyTraceAt) return;
            _nextLobbyTraceAt = Time.time + 3f;

            LobbyConnection connection = app == null ? null : app.lobby_connection;
            NewUIRoom roomUi = null;
            try { roomUi = NewUIRoom.getInstance(); } catch { }

            string trace = "state=" + (connection == null ? "null" : connection.state.ToString()) +
                " roomUi=" + (roomUi == null ? "null" : "ready") +
                " roomMatching=" + (roomUi != null && roomUi.InMatch) +
                " lobbyIsNew=" + LobbyState.isNew +
                " hookZero=" + (GlobalStatic.hookNum == "0") +
                " halfQuit=" + Mathf.CeilToInt(GlobalStatic.halfQuitStateTime) +
                " matching=" + _matching +
                " reward=" + (_cardManager != null || _awaitingReward);
            if (trace == _lastLobbyTrace) return;

            _lastLobbyTrace = trace;
            FileLogger.Log("MATCH", "lobby gate " + trace);
        }

        private static void TickCards()
        {
            if (_cardManager == null || Time.time < _nextCardActionAt) return;
            try
            {
                UIJieSuanTakeCard window = _cardManager.window;
                if (_cardCount <= 0)
                {
                    StatusText = "无可翻牌奖励，等待结算确认";
                    _nextCardActionAt = Time.time + 0.5f;
                    return;
                }

                if (window == null || window.gameObject == null || !window.gameObject.activeInHierarchy)
                {
                    if (Time.time >= _nextCardWaitLogAt)
                    {
                        _nextCardWaitLogAt = Time.time + 1f;
                        FileLogger.Log("CARD", "waiting for active card window elapsed=" +
                            (Time.time - _cardDetectedAt).ToString("0.0"));
                    }
                    _nextCardActionAt = Time.time + 0.15f;
                    return;
                }

                if (window.cards == null || window.cards.Count == 0)
                {
                    _nextCardActionAt = Time.time + 0.2f;
                    return;
                }

                int chosen = ReadPrivateInt(_cardManager, "chooseCardCount");
                if (chosen < _cardCount)
                {
                    for (int i = 0; i < window.cards.Count; i++)
                    {
                        CardBehaviour card = window.cards[i];
                        if (card == null || card.IsTrun) continue;
                        _cardManager.CardsRefresh(card.gameObject);
                        chosen = ReadPrivateInt(_cardManager, "chooseCardCount");
                        StatusText = "结算翻牌 " + chosen + "/" + _cardCount;
                        FileLogger.Log("CARD", "flipped chosen=" + chosen + "/" + _cardCount +
                            " cardIndex=" + i);
                        _nextCardActionAt = Time.time + 0.45f;
                        return;
                    }
                }

                if (!_cardCloseScheduled)
                {
                    _cardCloseScheduled = true;
                    window.StopCountdown();
                    window.FinishHideView();
                    StatusText = "翻牌完成，返回大厅";
                    FileLogger.Log("CARD", "flip complete; close scheduled");
                }
                CompleteCardPhase(5f);
            }
            catch (Exception ex)
            {
                FileLogger.Log("CARD", "auto flip failed: " + ex.Message);
                CompleteCardPhase(5f);
            }
        }

        private static void CompleteCardPhase(float nextMatchDelay)
        {
            _cardManager = null;
            _awaitingReward = false;
            _nextMatchAt = Time.time + nextMatchDelay;
        }

        private static void TickBalanceConfirmation()
        {
            if (_balanceView == null || Time.time < _nextBalanceConfirmAt) return;
            try
            {
                if (_balanceView.gameObject == null || !_balanceView.gameObject.activeInHierarchy)
                {
                    _nextBalanceConfirmAt = Time.time + 0.2f;
                    return;
                }

                GameObject button = _balanceView.confirmBtn == null
                    ? _balanceView.gameObject
                    : _balanceView.confirmBtn.gameObject;
                _balanceView.ConfirmJiesuanBtn(button);
                FileLogger.Log("CARD", "balance summary confirmed");
                _balanceView = null;
                CompleteCardPhase(3f);
                StatusText = "结算已确认，等待返回大厅";
            }
            catch (Exception ex)
            {
                _nextBalanceConfirmAt = Time.time + 1f;
                FileLogger.Log("CARD", "balance confirm failed: " + ex.Message);
            }
        }

        private static void CaptureParticipants(GameApp app, Level level, Character player)
        {
            if (_participantLocked) return;
            if (player != null && player.uid != 0) ParticipantIds.Add(player.uid);
            RefreshEnemies(level, player);
            for (int i = 0; i < Enemies.Count; i++)
                if (Enemies[i].uid != 0) ParticipantIds.Add(Enemies[i].uid);
            int rosterCount = CountRoomParticipants(app);
            InitialPlayers = Math.Max(InitialPlayers, Math.Max(ParticipantIds.Count, rosterCount));
        }

        private static void RefreshEnemies(Level level, Character player)
        {
            Enemies.Clear();
            if (level == null) return;
            try
            {
                List<Character> list = level.GetCharacters();
                if (list == null) return;
                for (int i = 0; i < list.Count; i++)
                {
                    Character ch = list[i];
                    if (ch == null || ch == player || ch.Is_Viewer) continue;
                    if (!IsOpponentForCurrentMode(level, player, ch)) continue;
                    if (!Enemies.Contains(ch)) Enemies.Add(ch);
                }
            }
            catch { }
        }

        private static bool IsOpponentForCurrentMode(Level level, Character player, Character target)
        {
            if (level == null || player == null || target == null) return false;
            try
            {
                if (level.game_type == RoomInfo.GameType.kGameTypeChiji) return true;
                int playerTeam = player.GetTeam();
                int targetTeam = target.GetTeam();
                return playerTeam < 0 || targetTeam < 0 || playerTeam != targetTeam;
            }
            catch
            {
                return true;
            }
        }

        private static int CountRemaining(Character player)
        {
            HashSet<int> counted = CountedParticipantIds;
            counted.Clear();
            int count = 0;
            if (player != null && !player.IsDied && counted.Add(player.uid))
            {
                count++;
            }
            for (int i = 0; i < Enemies.Count; i++)
            {
                Character ch = Enemies[i];
                if (ch == null) continue;
                if (ch.IsDied)
                {
                    if (ch.uid != 0) ConfirmedDeadParticipantIds.Add(ch.uid);
                    continue;
                }
                if (_participantLocked && !ParticipantIds.Contains(ch.uid)) continue;
                if (counted.Add(ch.uid))
                {
                    count++;
                }
            }
            if (_participantLocked)
            {
                foreach (int uid in ParticipantIds)
                {
                    if (uid == 0 || counted.Contains(uid) || ConfirmedDeadParticipantIds.Contains(uid)) continue;
                    count++;
                }
                count += Math.Max(0, InitialPlayers - ParticipantIds.Count);
            }
            return count;
        }

        private static Vector3 SelectStealthRetreatPoint(Character player,
            out bool meetsMinimumSeparation, out float selectedSeparation)
        {
            Vector3 origin = player.transform.position;
            float originSeparation = MinimumLivingEnemyDistance(origin);
            Vector3 bestPreferred = origin;
            float bestPreferredScore = float.MaxValue;
            bool hasPreferred = false;
            Vector3 bestHard = origin;
            float bestHardScore = float.MaxValue;
            bool hasHard = false;
            Vector3 bestFallback = origin;
            float bestFallbackScore = -originSeparation * 500f;
            selectedSeparation = originSeparation;

            if (originSeparation >= AssaultStealthRetreatPreferredSeparation)
            {
                hasPreferred = true;
                bestPreferredScore = ScoreStealthRetreatPoint(origin, player, origin,
                    0f, originSeparation);
            }
            else if (originSeparation >= AssaultStealthRetreatSeparation)
            {
                hasHard = true;
                bestHardScore = ScoreStealthRetreatPoint(origin, player, origin,
                    0f, originSeparation);
            }

            int index = 0;
            for (int r = 0; r < StealthRetreatRadii.Length; r++)
            {
                for (int i = 0; i < 24; i++)
                {
                    float angle = (360f / 24f) * i + r * 7.5f;
                    Vector3 direction = Quaternion.AngleAxis(angle, Vector3.up) *
                        Vector3.forward;
                    Vector3 ground;
                    if (!TryProjectGround(origin + direction * StealthRetreatRadii[r],
                        origin.y, 3f, out ground))
                        continue;
                    if (IsFailedCandidate(ground)) continue;
                    float routePenalty = AutoBattleRoutePlanner.CandidatePenalty(origin,
                        ground, player.transform.root);
                    if (routePenalty >= 120f) continue;

                    float separation = MinimumLivingEnemyDistance(ground);
                    float routeExposure = routePenalty <= 0.1f
                        ? ScoreRouteExposure(origin, ground, player)
                        : 420f;
                    float score = ScoreStealthRetreatPoint(ground, player, origin,
                        routePenalty + routeExposure, separation) + index * 0.01f;
                    index++;
                    if (separation >= AssaultStealthRetreatPreferredSeparation)
                    {
                        if (!hasPreferred || score < bestPreferredScore)
                        {
                            hasPreferred = true;
                            bestPreferredScore = score;
                            bestPreferred = ground;
                            selectedSeparation = separation;
                        }
                        continue;
                    }
                    if (separation >= AssaultStealthRetreatSeparation)
                    {
                        if (!hasHard || score < bestHardScore)
                        {
                            hasHard = true;
                            bestHardScore = score;
                            bestHard = ground;
                            if (!hasPreferred) selectedSeparation = separation;
                        }
                        continue;
                    }

                    float fallbackScore = -separation * 500f + routeExposure +
                        routePenalty * 5f + XzDistance(origin, ground);
                    if (fallbackScore >= bestFallbackScore) continue;
                    bestFallbackScore = fallbackScore;
                    bestFallback = ground;
                    if (!hasPreferred && !hasHard) selectedSeparation = separation;
                }
            }

            meetsMinimumSeparation = hasPreferred || hasHard;
            return hasPreferred ? bestPreferred : (hasHard ? bestHard : bestFallback);
        }

        private static float ScoreStealthRetreatPoint(Vector3 point, Character player,
            Vector3 origin, float routePenalty, float separation)
        {
            float preferredPenalty = Mathf.Max(0f,
                AssaultStealthRetreatPreferredSeparation - separation) * 160f;
            return ScoreSafetyPoint(point, player, origin, routePenalty) +
                preferredPenalty - Mathf.Min(separation, 40f) * 35f;
        }

        private static float MinimumLivingEnemyDistance(Vector3 point)
        {
            float minimum = 999f;
            for (int i = 0; i < Enemies.Count; i++)
            {
                Character enemy = Enemies[i];
                if (!IsLivingOpponent(enemy)) continue;
                float distance = XzDistance(point, enemy.transform.position);
                if (distance < minimum) minimum = distance;
            }
            return minimum;
        }

        private static Vector3 SelectSafetyPoint(Character player)
        {
            Vector3 origin = player.transform.position;
            Vector3 best = origin;
            float bestScore = ScoreSafetyPoint(origin, player, origin, 0f);
            Vector3 bestEscape = origin;
            float bestEscapeScore = float.MaxValue;
            int index = 0;
            for (int r = 0; r < SafeRadii.Length; r++)
            {
                for (int i = 0; i < 24; i++)
                {
                    float angle = (360f / 24f) * i + r * 7.5f;
                    Vector3 dir = Quaternion.AngleAxis(angle, Vector3.up) * Vector3.forward;
                    Vector3 ground;
                    if (!TryProjectGround(origin + dir * SafeRadii[r], origin.y, 3f, out ground)) continue;
                    if (IsFailedCandidate(ground)) continue;
                    float routePenalty = AutoBattleRoutePlanner.CandidatePenalty(origin, ground, player.transform.root);
                    if (routePenalty >= 120f) continue;
                    float score = ScoreSafetyPoint(ground, player, origin, routePenalty) + index * 0.01f;
                    index++;
                    score += routePenalty <= 0.1f
                        ? ScoreRouteExposure(origin, ground, player)
                        : 420f;
                    if (score < bestEscapeScore)
                    {
                        bestEscapeScore = score;
                        bestEscape = ground;
                    }
                    if (score < bestScore)
                    {
                        bestScore = score;
                        best = ground;
                    }
                }
            }
            if (_hasSafePoint && !IsFailedCandidate(_safePoint) && XzDistance(origin, _safePoint) >= 1.1f)
            {
                float committedPenalty = AutoBattleRoutePlanner.CandidatePenalty(origin, _safePoint, player.transform.root);
                if (committedPenalty < 120f)
                {
                    float committedScore = ScoreSafetyPoint(_safePoint, player, origin, committedPenalty) +
                        (committedPenalty <= 0.1f ? ScoreRouteExposure(origin, _safePoint, player) : 420f);
                    if (committedScore <= bestScore + 180f) return _safePoint;
                }
            }
            if (CountExposure(origin, player) > 0 && XzDistance(best, origin) < 0.5f &&
                bestEscapeScore < float.MaxValue)
                return bestEscape;
            return best;
        }

        private static float ScoreRouteExposure(Vector3 from, Vector3 to, Character player)
        {
            float distance = XzDistance(from, to);
            if (distance < 1.5f) return 0f;
            int samples = Mathf.Clamp(Mathf.CeilToInt(distance / 2f), 2, 7);
            float penalty = 0f;
            for (int i = 1; i < samples; i++)
            {
                Vector3 point = Vector3.Lerp(from, to, (float)i / samples);
                penalty += CountPotentialSightLines(point, player) * 260f;
                penalty += CountExposure(point, player) * 180f;
            }
            return penalty;
        }

        private static bool ShouldRejectExposedHideRoute(Character player)
        {
            if (player == null || Time.time < _nextHideRouteAuditAt) return false;
            _nextHideRouteAuditAt = Time.time + 0.18f;
            if (CountPotentialSightLines(player.transform.position, player) > 0) return false;
            RouteExposurePoints.Clear();
            if (!SurvivalCombatAdapter.CopyActiveRoute(RouteExposurePoints) ||
                RouteExposurePoints.Count == 0)
                return false;

            Vector3 previous = player.transform.position;
            int exposedSamples = 0;
            int sampleBudget = 14;
            for (int i = 0; i < RouteExposurePoints.Count && sampleBudget > 0; i++)
            {
                Vector3 next = RouteExposurePoints[i];
                float distance = XzDistance(previous, next);
                int samples = Mathf.Clamp(Mathf.CeilToInt(distance / 1.8f), 1, sampleBudget);
                for (int s = 1; s <= samples && sampleBudget > 0; s++, sampleBudget--)
                {
                    Vector3 point = Vector3.Lerp(previous, next, (float)s / samples);
                    exposedSamples += CountPotentialSightLines(point, player);
                }
                previous = next;
            }
            if (exposedSamples <= 0) return false;
            FileLogger.Log("SURVIVAL", "hide route rejected sightSamples=" + exposedSamples +
                " destination=" + FormatVec(_safePoint) + " routePoints=" +
                RouteExposurePoints.Count);
            return true;
        }

        private static bool ShouldRejectStealthRetreatRoute(Character player)
        {
            if (player == null || Time.time < _nextHideRouteAuditAt) return false;
            _nextHideRouteAuditAt = Time.time + 0.12f;
            RouteExposurePoints.Clear();
            if (!SurvivalCombatAdapter.CopyActiveRoute(RouteExposurePoints) ||
                RouteExposurePoints.Count == 0)
                return false;

            float currentSeparation = MinimumLivingEnemyDistance(player.transform.position);
            float routeFloor = currentSeparation >= AssaultStealthRetreatSeparation
                ? AssaultStealthRetreatSeparation
                : Mathf.Max(0f, currentSeparation - 0.35f);
            bool protectCurrentCover =
                CountPotentialSightLines(player.transform.position, player) == 0;
            int exposedSamples = 0;
            Vector3 previous = player.transform.position;
            int sampleBudget = 20;
            for (int i = 0; i < RouteExposurePoints.Count && sampleBudget > 0; i++)
            {
                Vector3 next = RouteExposurePoints[i];
                float distance = XzDistance(previous, next);
                int samples = Mathf.Clamp(Mathf.CeilToInt(distance / 1.5f), 1,
                    sampleBudget);
                for (int s = 1; s <= samples && sampleBudget > 0; s++, sampleBudget--)
                {
                    Vector3 point = Vector3.Lerp(previous, next, (float)s / samples);
                    float separation = MinimumLivingEnemyDistance(point);
                    if (separation + 0.05f < routeFloor)
                    {
                        FileLogger.Log("SURVIVAL][ROLE", "stealth retreat route rejected sampleMin=" +
                            separation.ToString("0.0") + " floor=" + routeFloor.ToString("0.0") +
                            " currentMin=" + currentSeparation.ToString("0.0") +
                            " destination=" + FormatVec(_safePoint));
                        return true;
                    }
                    if (protectCurrentCover)
                        exposedSamples += CountPotentialSightLines(point, player);
                }
                previous = next;
            }
            if (exposedSamples > 0)
            {
                FileLogger.Log("SURVIVAL][ROLE", "stealth retreat route rejected sightSamples=" +
                    exposedSamples + " destination=" + FormatVec(_safePoint));
                return true;
            }
            return false;
        }

        private static int CountPotentialSightLines(Vector3 point, Character player)
        {
            int count = 0;
            for (int i = 0; i < Enemies.Count; i++)
            {
                Character enemy = Enemies[i];
                if (!IsLivingOpponent(enemy)) continue;
                EnemyTrack track = GetEnemyTrack(enemy);
                bool hidden = track == null ? IsTargetHidden(enemy) : track.Hidden;
                if (hidden && (track == null || track.LastVisibleAt <= 0f ||
                    Time.time - track.LastVisibleAt > 1.25f) &&
                    XzDistance(point, enemy.transform.position) > AssaultHiddenHuntDistance)
                    continue;
                if (XzDistance(point, enemy.transform.position) > 42f) continue;
                if (HasBodyCover(enemy, point)) continue;
                count += IsEnemyFacingPoint(enemy, point, 0.12f) ? 2 : 1;
            }
            return count;
        }

        private static float ScoreSafetyPoint(Vector3 point, Character player, Vector3 origin, float routePenalty)
        {
            int exposed = 0;
            int covered = 0;
            float minDistance = 999f;
            for (int i = 0; i < Enemies.Count; i++)
            {
                Character enemy = Enemies[i];
                if (!IsLivingOpponent(enemy)) continue;
                float distance = XzDistance(point, enemy.transform.position);
                if (distance < minDistance) minDistance = distance;
                bool blocked = HasBodyCover(enemy, point);
                if (blocked) covered++;
                else
                {
                    exposed++;
                    if (IsEnemyFacingPoint(enemy, point, 0.22f)) exposed++;
                }
            }

            float separationPenalty = Mathf.Max(0f, SurvivalBotSettings.DesiredSeparation - minDistance) * 55f;
            return exposed * 1200f + separationPenalty + routePenalty + XzDistance(point, origin) * 0.7f - covered * 20f;
        }

        private static int CountExposure(Vector3 point, Character player)
        {
            int count = 0;
            for (int i = 0; i < Enemies.Count; i++)
            {
                Character enemy = Enemies[i];
                if (!IsLivingOpponent(enemy)) continue;
                if (!IsEnemyFacingPoint(enemy, point, 0.22f)) continue;
                if (!HasBodyCover(enemy, point)) count++;
            }
            return count;
        }

        private static bool IsEnemyFacingPoint(Character enemy, Vector3 point, float threshold)
        {
            try
            {
                Vector3 toPoint = point + Vector3.up - (enemy.transform.position + Vector3.up * 1.2f);
                if (toPoint.sqrMagnitude < 0.01f) return true;
                toPoint.Normalize();
                Vector3 forward = Quaternion.Euler(enemy.lookdirection) * Vector3.forward;
                if (forward.sqrMagnitude < 0.01f) forward = enemy.transform.forward;
                forward.Normalize();
                return Vector3.Dot(forward, toPoint) >= threshold;
            }
            catch { return false; }
        }

        private static void TraceAttackSearch(Character player, Character target, Vector3 move,
            float targetMoved, string note)
        {
            if (Time.time < _nextAttackSearchTraceAt || player == null || target == null) return;
            _nextAttackSearchTraceAt = Time.time + 0.9f;
            FileLogger.Log("SURVIVAL", "hunt trace uid=" + target.uid +
                " player=" + FormatVec(player.transform.position) +
                " target=" + FormatVec(target.transform.position) +
                " goal=" + (_hasAttackPoint ? FormatVec(_attackPoint) : "none") +
                " goalAge=" + (_hasAttackPoint ? (Time.time - _attackPointSetAt).ToString("0.0") : "-") +
                " targetMoved=" + (_hasAttackPoint ? targetMoved.ToString("0.0") : "-") +
                " move=" + FormatVec(move) + " path=" + SurvivalCombatAdapter.LastPath +
                (string.IsNullOrEmpty(note) ? string.Empty : " note=" + note));
        }

        private static void UpdateEnemyTracks(Character player, Camera camera)
        {
            if (player == null) return;
            int hp = player.hp;
            int shield = 0;
            try { shield = player.shield; } catch { }
            bool hpDropped = _lastPlayerHp > 0 && hp < _lastPlayerHp;
            bool shieldDropped = _lastPlayerShield > 0 && shield < _lastPlayerShield;
            if (hpDropped || shieldDropped)
            {
                _recentDamageAt = Time.time;
                _incomingDamageSequence++;
            }
            if (hpDropped) _healthDamageSequence++;
            _lastPlayerHp = hp;
            _lastPlayerShield = shield;
            if (Time.time < _nextEnemyTrackAt) return;
            _nextEnemyTrackAt = Time.time + 0.12f;

            for (int i = 0; i < Enemies.Count; i++)
            {
                Character target = Enemies[i];
                if (target == null || target.transform == null) continue;
                if (target.IsDied)
                {
                    if (target.uid != 0) ConfirmedDeadParticipantIds.Add(target.uid);
                    continue;
                }

                EnemyTrack track;
                if (!EnemyTracks.TryGetValue(target.uid, out track))
                {
                    track = new EnemyTrack();
                    track.Uid = target.uid;
                    track.Target = target;
                    track.Position = target.transform.position;
                    track.Distance = XzDistance(player.transform.position, track.Position);
                    track.LastHp = target.hp;
                    EnemyTracks[target.uid] = track;
                }

                float now = Time.time;
                float distance = XzDistance(player.transform.position, target.transform.position);
                float dt = track.SampleAt <= 0f ? 0f : now - track.SampleAt;
                if (dt > 0.02f)
                {
                    float instantaneous = (track.Distance - distance) / dt;
                    track.ClosingSpeed = Mathf.Lerp(track.ClosingSpeed, instantaneous, 0.45f);
                    Vector3 instantaneousVelocity = (target.transform.position - track.Position) / dt;
                    instantaneousVelocity.y = 0f;
                    track.Velocity = Vector3.Lerp(track.Velocity, instantaneousVelocity, 0.45f);
                    track.HorizontalSpeed = track.Velocity.magnitude;
                }
                if (track.LastHp > 0 && target.hp < track.LastHp) track.LastDamagedAt = now;
                track.Target = target;
                track.Position = target.transform.position;
                track.Distance = distance;
                track.Hidden = IsTargetHidden(target);
                track.Invincible = target.invincible_time > 0.03f;
                track.FacingPlayer = IsEnemyFacingPoint(target, player.transform.position, 0.45f);
                track.FireLine = SurvivalCombatAdapter.SurvivalHasEmergencyFireLine(player, target, camera);
                track.Visible = (!track.Hidden || distance <= 6f) && track.FireLine;
                if (track.Visible) track.LastVisibleAt = now;
                track.HealthPercent = CharacterHealthPercent(target);
                track.LastHp = target.hp;
                track.SampleAt = now;
            }
        }

        private static EnemyTrack GetEnemyTrack(Character target)
        {
            if (target == null) return null;
            EnemyTrack track;
            return EnemyTracks.TryGetValue(target.uid, out track) ? track : null;
        }

        private static Character SelectEmergencyThreat(Character player, float triggerDistance, out float bestScore)
        {
            Character best = null;
            bestScore = 0f;
            for (int i = 0; i < Enemies.Count; i++)
            {
                Character candidate = Enemies[i];
                if (!IsEmergencyTargetUsable(candidate)) continue;
                float score = ScoreEmergencyThreat(player, candidate, triggerDistance);
                EnemyTrack track = GetEnemyTrack(candidate);
                float distance = track == null
                    ? XzDistance(player.transform.position, candidate.transform.position)
                    : track.Distance;
                bool activeVisibleThreat = track != null &&
                    IsActiveVisibleEmergencyThreat(track, track.FireLine,
                        Time.time - _recentDamageAt <= 1.2f, distance,
                        GetEmergencyVisibleThreatLimit(triggerDistance));
                if (activeVisibleThreat) score = Mathf.Max(score, 100f);
                if ((!activeVisibleThreat && score < 95f) || score <= bestScore) continue;
                bestScore = score;
                best = candidate;
            }
            return best;
        }

        private static float ScoreEmergencyThreat(Character player, Character candidate, float triggerDistance)
        {
            if (player == null || !IsEmergencyTargetUsable(candidate)) return 0f;
            EnemyTrack track = GetEnemyTrack(candidate);
            float distance = track == null
                ? XzDistance(player.transform.position, candidate.transform.position)
                : track.Distance;
            bool hidden = track == null ? IsTargetHidden(candidate) : track.Hidden;
            if (hidden && distance > 6f) return 0f;
            if (!hidden && distance > GetEmergencyVisibleThreatLimit(triggerDistance)) return 0f;

            float score = 0f;
            if (distance <= 3f) score += 210f;
            else if (distance <= 6f) score += 145f;
            else if (distance <= triggerDistance) score += 55f;
            else if (distance <= triggerDistance + 6f) score += 18f;
            if (track != null)
            {
                if (track.FireLine) score += 30f;
                if (track.FacingPlayer) score += 35f;
                if (track.ClosingSpeed > 0.8f) score += Mathf.Min(45f, track.ClosingSpeed * 10f);
                if (track.Invincible) score += 8f;
            }
            if (Time.time - _recentDamageAt <= 1.2f && (track == null || track.FireLine)) score += 20f;
            if (hidden) score += 60f;
            return score;
        }

        private static float GetEmergencyVisibleThreatLimit(float triggerDistance)
        {
            return Mathf.Max(EmergencyVisibleRetaliationDistance, triggerDistance + 6f);
        }

        private static bool IsActiveVisibleEmergencyThreat(EnemyTrack track, bool strictLine,
            bool recentlyDamaged, float distance, float visibleThreatLimit)
        {
            if (track == null || track.Hidden || !strictLine || distance > visibleThreatLimit) return false;
            bool closing = track.ClosingSpeed >= 0.8f;
            return track.FacingPlayer || closing || recentlyDamaged;
        }

        private static Character SelectBestTarget(Character player)
        {
            Character best = null;
            float bestScore = float.MaxValue;
            for (int i = 0; i < Enemies.Count; i++)
            {
                Character target = Enemies[i];
                if (!IsAttackTargetUsable(target)) continue;
                float score = ScoreAttackTarget(player, target, false);
                if (score >= bestScore) continue;
                bestScore = score;
                best = target;
            }
            return best;
        }

        private static Character SelectSearchTarget(Character player)
        {
            Character best = SelectBestTarget(player);
            if (!IsAttackTargetUsable(_searchTarget))
            {
                SetSearchTarget(best, "acquire");
                return _searchTarget;
            }
            if (best == null || best == _searchTarget || Time.time - _searchTargetLockedAt < 1.5f)
                return _searchTarget;
            float currentScore = ScoreAttackTarget(player, _searchTarget, false);
            float bestScore = ScoreAttackTarget(player, best, false);
            if (bestScore + 18f < currentScore)
            {
                SetSearchTarget(best, "score_advantage");
            }
            return _searchTarget;
        }

        private static void SetSearchTarget(Character target, string reason)
        {
            if (_searchTarget == target) return;
            int oldUid = _searchTarget == null ? 0 : _searchTarget.uid;
            int newUid = target == null ? 0 : target.uid;
            _searchTarget = target;
            _searchTargetLockedAt = Time.time;
            _hasAttackPoint = false;
            _attackPointLastProgressAt = 0f;
            SurvivalCombatAdapter.SuspendSurvivalNavigation("search_target_change");
            FileLogger.Log("SURVIVAL", "hunt target " + oldUid + "->" + newUid + " reason=" + reason);
        }

        private static Character SelectBestVisibleTarget(Character player, Camera camera, bool opportunityOnly)
        {
            Character best = null;
            float bestScore = float.MaxValue;
            for (int i = 0; i < Enemies.Count; i++)
            {
                Character target = Enemies[i];
                if (!IsAttackTargetUsable(target)) continue;
                if (!SurvivalCombatAdapter.SurvivalHasStrictFireLine(player, target, camera)) continue;
                if (opportunityOnly && !IsSafeOpportunityTarget(player, target)) continue;
                float score = ScoreAttackTarget(player, target, true);
                if (score >= bestScore) continue;
                bestScore = score;
                best = target;
            }
            return best;
        }

        private static float ScoreAttackTarget(Character player, Character target, bool visible)
        {
            EnemyTrack track = GetEnemyTrack(target);
            float distance = track == null
                ? XzDistance(player.transform.position, target.transform.position)
                : track.Distance;
            float health = track == null ? CharacterHealthPercent(target) : track.HealthPercent;
            float score = distance * 2.5f + health * 0.12f;
            if (visible) score -= 75f;
            if (track != null)
            {
                if (Time.time - track.LastDamagedAt <= 2.2f) score -= 28f;
                if (track.FacingPlayer) score += 12f;
                if (track.Invincible) score += 1000f;
            }
            if (target == _attackTarget) score -= 18f;
            return score;
        }

        private static bool ShouldSwitchAttackTarget(Character player, Character current, Character candidate,
            bool currentVisible)
        {
            if (candidate == null || candidate == current) return false;
            if (Time.time - _attackTargetLockedAt < 1f && currentVisible) return false;
            float currentScore = ScoreAttackTarget(player, current, currentVisible);
            float candidateScore = ScoreAttackTarget(player, candidate, true);
            return candidateScore + (currentVisible ? 20f : 8f) < currentScore;
        }

        private static bool IsSafeOpportunityTarget(Character player, Character target)
        {
            if (player == null || target == null) return false;
            EnemyTrack track = GetEnemyTrack(target);
            if (track == null || track.Invincible || track.Hidden || track.FacingPlayer) return false;
            float distance = track == null
                ? XzDistance(player.transform.position, target.transform.position)
                : track.Distance;
            bool weakened = track.HealthPercent <= 72f || Time.time - track.LastDamagedAt <= 2.5f;
            if (!weakened || distance > 28f) return false;
            return CountExposure(player.transform.position, player) == 0 &&
                CountOpenThirdPartyThreats(player.transform.position, target) == 0;
        }

        private static int CountOpenThirdPartyThreats(Vector3 point, Character ignoredTarget)
        {
            int count = 0;
            for (int i = 0; i < Enemies.Count; i++)
            {
                Character enemy = Enemies[i];
                if (enemy == ignoredTarget || !IsLivingOpponent(enemy) || IsTargetHidden(enemy)) continue;
                if (XzDistance(point, enemy.transform.position) > 35f) continue;
                if (!HasBodyCover(enemy, point)) count++;
            }
            return count;
        }

        private static void SetAttackTarget(Character target, string reason)
        {
            if (_attackTarget == target) return;
            int oldUid = _attackTarget == null ? 0 : _attackTarget.uid;
            int newUid = target == null ? 0 : target.uid;
            _attackTarget = target;
            if (target != null)
            {
                _searchTarget = null;
                _combatStrafeSign = (target.uid & 1) == 0 ? 1 : -1;
                _combatStrafeSwitchAt = 0f;
                _combatMoveProgressAt = 0f;
                _combatMoveLastPosition = Vector3.zero;
                _combatMoveDirection = Vector3.zero;
                _combatMoveTargetUid = target.uid;
                SurvivalCombatAdapter.SuspendSurvivalNavigation("combat");
            }
            else
            {
                _combatStrafeSwitchAt = 0f;
                _combatMoveProgressAt = 0f;
                _combatMoveLastPosition = Vector3.zero;
                _combatMoveDirection = Vector3.zero;
                _combatMoveTargetUid = 0;
            }
            _hasAttackPoint = false;
            _attackTargetLockedAt = Time.time;
            _attackEngagementStartedAt = Time.time;
            _attackLastDamageAt = Time.time;
            _attackTargetLastVital = CharacterVital(target);
            _attackTargetVisibleAt = target == null ? 0f : Time.time;
            FileLogger.Log("SURVIVAL", "attack target " + oldUid + "->" + newUid + " reason=" + reason);
        }

        private static void MoveEmergency(Character player, Character target, float distance)
        {
            if (player == null || target == null) return;
            Vector3 away = player.transform.position - target.transform.position;
            away.y = 0f;
            if (away.sqrMagnitude < 0.01f) away = -player.transform.forward;
            away.Normalize();
            Vector3 side = Vector3.Cross(Vector3.up, away);
            if (((target.uid + Mathf.FloorToInt(Time.time * 0.7f)) & 1) != 0) side = -side;
            float retreatWeight = distance < 8f ? 0.9f : 0.45f;
            Vector3[] candidates =
            {
                (away * retreatWeight + side).normalized,
                (away * retreatWeight - side).normalized,
                away,
                side,
                -side
            };
            Vector3 move = Vector3.zero;
            float bestScore = float.MinValue;
            for (int i = 0; i < candidates.Length; i++)
            {
                Vector3 candidate = candidates[i];
                if (!IsSafeCombatDirection(player, candidate)) continue;
                float score = Vector3.Dot(candidate, away) * 3f - i * 0.04f;
                if (score <= bestScore) continue;
                bestScore = score;
                move = candidate;
            }
            if (move.sqrMagnitude > 0.01f)
            {
                AutoBattleInput.SetMoveWorld(player, move, false);
                return;
            }

            Vector3 fallback = MoveCombatStrafe(player, target);
            if (fallback.sqrMagnitude > 0.01f) return;
            if (AutoBattleRoutePlanner.ShouldJumpForwardObstacle(player.transform.position, away, player.transform.root))
            {
                AutoBattleInput.PressAction(ActionType.kActionJump, 0.10f);
                AutoBattleInput.HoldAction(ActionType.kActionJump, 0.22f);
            }
        }

        private static Vector3 MoveCombatStrafe(Character player, Character target)
        {
            if (player == null || player.transform == null || target == null || target.transform == null)
                return Vector3.zero;
            Vector3 position = player.transform.position;
            if (_combatMoveTargetUid != target.uid)
            {
                _combatMoveTargetUid = target.uid;
                _combatStrafeSign = (target.uid & 1) == 0 ? 1 : -1;
                _combatStrafeSwitchAt = 0f;
                _combatMoveProgressAt = 0f;
                _combatMoveLastPosition = position;
                _combatMoveDirection = Vector3.zero;
            }
            Vector3 toTarget = target.transform.position - player.transform.position;
            toTarget.y = 0f;
            if (toTarget.sqrMagnitude < 0.01f) toTarget = player.transform.forward;
            toTarget.y = 0f;
            if (toTarget.sqrMagnitude < 0.01f) toTarget = Vector3.forward;
            float distance = toTarget.magnitude;
            toTarget.Normalize();

            bool stalled = false;
            if (_combatMoveProgressAt <= 0f)
            {
                _combatMoveLastPosition = position;
                _combatMoveProgressAt = Time.time;
            }
            else if (XzDistance(position, _combatMoveLastPosition) >= 0.18f)
            {
                _combatMoveLastPosition = position;
                _combatMoveProgressAt = Time.time;
            }
            else if (Time.time - _combatMoveProgressAt >= 0.70f)
            {
                _combatStrafeSign = -_combatStrafeSign;
                _combatStrafeSwitchAt = 0f;
                _combatMoveDirection = Vector3.zero;
                _combatMoveProgressAt = Time.time;
                _combatMoveLastPosition = position;
                stalled = true;
            }

            if (_combatStrafeSwitchAt <= 0f)
            {
                _combatStrafeSwitchAt = Time.time + 1.45f +
                    ((target.uid + Mathf.FloorToInt(Time.time * 10f)) & 3) * 0.18f;
            }
            else if (Time.time >= _combatStrafeSwitchAt)
            {
                _combatStrafeSign = -_combatStrafeSign;
                _combatStrafeSwitchAt = Time.time + 1.45f +
                    ((target.uid + Mathf.FloorToInt(Time.time * 10f)) & 3) * 0.18f;
                _combatMoveDirection = Vector3.zero;
            }

            Vector3 side = Vector3.Cross(Vector3.up, toTarget) * _combatStrafeSign;
            float rangeCorrection = Mathf.Clamp((distance - 9f) / 5f, -0.95f, 0.95f);
            Vector3 preferred = side + toTarget * (rangeCorrection * 0.85f);
            preferred.y = 0f;
            if (preferred.sqrMagnitude < 0.01f) preferred = side;
            preferred.Normalize();

            if (!stalled && _combatMoveDirection.sqrMagnitude > 0.01f &&
                Vector3.Dot(_combatMoveDirection, preferred) >= 0.15f &&
                IsSafeCombatDirection(player, _combatMoveDirection))
            {
                AutoBattleInput.SetMoveWorld(player, _combatMoveDirection, false);
                return _combatMoveDirection;
            }

            Vector3 selected = Vector3.zero;
            for (int i = 0; i < CombatDirectionOffsets.Length; i++)
            {
                Vector3 candidate = Quaternion.AngleAxis(CombatDirectionOffsets[i], Vector3.up) * preferred;
                candidate.y = 0f;
                if (candidate.sqrMagnitude < 0.01f) continue;
                candidate.Normalize();
                if (!IsSafeCombatDirection(player, candidate)) continue;
                selected = candidate;
                break;
            }

            string clearanceDetail = string.Empty;
            if (selected.sqrMagnitude <= 0.01f)
            {
                AutoBattleRoutePlanner.TryFindRainClearanceDirection(position, preferred,
                    player.transform.root, out selected, out clearanceDetail);
                if (selected.sqrMagnitude > 0.01f && !IsSafeCombatDirection(player, selected))
                {
                    selected = Vector3.zero;
                    clearanceDetail += " combat_validation=reject";
                }
            }
            if (selected.sqrMagnitude > 0.01f)
            {
                selected.y = 0f;
                selected.Normalize();
                _combatMoveDirection = selected;
                AutoBattleInput.SetMoveWorld(player, selected, false);
                TraceCombatMovement(target, distance, stalled ? "stall_recovered" : "moving",
                    selected, clearanceDetail);
                return selected;
            }

            if (_combatMoveDirection.sqrMagnitude > 0.01f &&
                IsSafeCombatDirection(player, _combatMoveDirection))
            {
                AutoBattleInput.SetMoveWorld(player, _combatMoveDirection, false);
                TraceCombatMovement(target, distance, "last_lane_fallback",
                    _combatMoveDirection, clearanceDetail);
                return _combatMoveDirection;
            }
            if (AutoBattleRoutePlanner.ShouldJumpForwardObstacle(position, preferred,
                player.transform.root))
            {
                AutoBattleInput.SetMoveWorld(player, preferred, false);
                AutoBattleInput.PressAction(ActionType.kActionJump, 0.10f);
                AutoBattleInput.HoldAction(ActionType.kActionJump, 0.22f);
                TraceCombatMovement(target, distance, "jump_clearance", preferred,
                    clearanceDetail);
                return preferred;
            }

            TraceCombatMovement(target, distance, "no_safe_lane", Vector3.zero,
                clearanceDetail);
            AutoBattleInput.ClearMovement();
            return Vector3.zero;
        }

        private static bool IsSafeCombatDirection(Character player, Vector3 direction)
        {
            if (player == null || player.transform == null) return false;
            direction.y = 0f;
            if (direction.sqrMagnitude < 0.01f) return false;
            direction.Normalize();
            Vector3 position = player.transform.position;
            if (AutoBattleRoutePlanner.HasForwardBlock(position, direction, player.transform.root))
                return false;
            Vector3 probe;
            if (!TryProjectGround(position + direction * 1.05f, position.y, 1.35f, out probe))
                return false;
            if (AutoBattleRoutePlanner.IsGameNavigationReady &&
                !AutoBattleRoutePlanner.IsPointOnOwnedRainGraph(probe, 0.95f))
                return false;
            return AutoBattleRoutePlanner.HasSupportedStandingPoint(probe, player.transform.root) &&
                AutoBattleRoutePlanner.CanFollowRouteSegment(position, probe, player.transform.root);
        }

        private static void TraceCombatMovement(Character target, float distance, string mode,
            Vector3 direction, string detail)
        {
            if (Time.time < _nextCombatMoveTraceAt) return;
            _nextCombatMoveTraceAt = Time.time + 0.9f;
            FileLogger.Log("SURVIVAL][MOVE", "combat=" + mode + " uid=" +
                (target == null ? 0 : target.uid) + " dist=" + distance.ToString("0.0") +
                " direction=" + FormatVec(direction) +
                (string.IsNullOrEmpty(detail) ? string.Empty : " " + detail));
        }

        private static Vector3 MoveAttackPursuit(Character player, Character target, Camera camera,
            bool preAimTarget)
        {
            if (player == null || player.transform == null || target == null || target.transform == null)
                return Vector3.zero;

            Vector3 targetPosition = target.transform.position;
            if (preAimTarget && camera != null)
                PreAimPursuitTarget(player, target, camera);
            Vector3 pursuitPoint;
            if (!TryProjectGround(targetPosition, targetPosition.y, 3f, out pursuitPoint))
                pursuitPoint = targetPosition;

            if (!_hasAttackPoint || XzDistance(_attackPoint, pursuitPoint) >= 0.75f)
            {
                if (!_hasAttackPoint) _attackPointSetAt = Time.time;
                _attackPoint = pursuitPoint;
                _attackPointTargetPosition = targetPosition;
                _hasAttackPoint = true;
            }
            else
            {
                _attackPoint = pursuitPoint;
                _attackPointTargetPosition = targetPosition;
            }
            if (_attackPointLastProgressAt <= 0f) _attackPointLastProgressAt = Time.time;

            Vector3 move = SurvivalCombatAdapter.NavigatePursuit(player, pursuitPoint);
            if (move.sqrMagnitude <= 0.01f)
            {
                return MoveCombatStrafe(player, target);
            }

            AutoBattleInput.SetMoveWorld(player, move, false);
            return move;
        }

        private static bool PreAimPursuitTarget(Character player, Character target, Camera camera)
        {
            if (player == null || player.transform == null || target == null ||
                target.transform == null || camera == null) return false;

            Vector3 lead = Vector3.zero;
            EnemyTrack track = GetEnemyTrack(target);
            if (track != null && track.SampleAt > 0f && Time.time - track.SampleAt <= 0.35f)
            {
                lead = track.Velocity * 0.10f;
                lead.y = 0f;
                if (lead.magnitude > 0.55f) lead = lead.normalized * 0.55f;
            }
            Vector3 aimPoint = target.transform.position + lead + Vector3.up * 0.82f;
            bool ready = SurvivalCombatAdapter.PreAimSurvivalTarget(player, camera, aimPoint);
            if (Time.time >= _nextPursuitAimTraceAt)
            {
                _nextPursuitAimTraceAt = Time.time + 0.9f;
                FileLogger.Log("SURVIVAL][AIM", "prelock uid=" + target.uid +
                    " ready=" + ready + " lead=" + lead.magnitude.ToString("0.00") +
                    " point=" + FormatVec(aimPoint));
            }
            return ready;
        }

        private static float CharacterHealthPercent(Character target)
        {
            try
            {
                int max = target == null ? 0 : target.max_health;
                if (target != null && target.character_info != null && target.character_info.max_health > max)
                    max = target.character_info.max_health;
                return max <= 0 ? 100f : Mathf.Clamp((float)target.hp * 100f / max, 0f, 100f);
            }
            catch { return 100f; }
        }

        private static int CharacterVital(Character target)
        {
            if (target == null) return 0;
            int shield = 0;
            try { shield = target.shield; } catch { }
            try { return Math.Max(0, target.hp) + Math.Max(0, shield); }
            catch { return Math.Max(0, shield); }
        }

        private static Character SelectNearestTarget(Character player)
        {
            Character best = null;
            float bestDistance = float.MaxValue;
            for (int i = 0; i < Enemies.Count; i++)
            {
                Character target = Enemies[i];
                if (!IsAttackTargetUsable(target)) continue;
                float distance = XzDistance(player.transform.position, target.transform.position);
                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    best = target;
                }
            }
            return best;
        }

        private static Character SelectNearestVisibleTarget(Character player, Camera camera)
        {
            if (player == null || camera == null) return null;
            Character best = null;
            float bestDistance = float.MaxValue;
            for (int i = 0; i < Enemies.Count; i++)
            {
                Character target = Enemies[i];
                if (!IsAttackTargetUsable(target)) continue;
                float distance = XzDistance(player.transform.position, target.transform.position);
                if (distance >= bestDistance) continue;
                if (!SurvivalCombatAdapter.SurvivalHasStrictFireLine(player, target, camera)) continue;
                bestDistance = distance;
                best = target;
            }
            return best;
        }

        private static bool IsAttackTargetUsable(Character target)
        {
            if (!IsLivingOpponent(target) || !Enemies.Contains(target)) return false;
            try { return !target.GetHidden() && target.invincible_time <= 0.03f; }
            catch { return false; }
        }

        private static bool IsEmergencyTargetUsable(Character target)
        {
            return IsLivingOpponent(target) && Enemies.Contains(target);
        }

        private static bool IsTargetHidden(Character target)
        {
            try { return target != null && target.GetHidden(); }
            catch { return false; }
        }

        private static bool IsLivingOpponent(Character target)
        {
            return target != null && target.transform != null && !target.IsDied && !target.Is_Viewer;
        }

        private static CliffSearchStatus TickFindCliff(Character player, out Vector3 edge,
            out Vector3 outward, out string detail)
        {
            edge = Vector3.zero;
            outward = Vector3.zero;
            detail = "source=boundary unavailable";
            if (player == null || player.transform == null)
                return CliffSearchStatus.Exhausted;

            if (_cliffSearchJob == null)
            {
                Vector3 origin = player.transform.position;
                string source = "aswnav_boundary";
                int candidateCount = CompactRainNavRuntime.CollectNearbyBoundaries(origin, 60f,
                    96, CliffBoundaryCandidates);
                if (candidateCount <= 0)
                {
                    source = "runtime_rain_boundary";
                    candidateCount = RuntimeRainNavDerivedData.CollectNearbyBoundaries(origin,
                        60f, 96, CliffBoundaryCandidates);
                }
                _cliffSearchJob = new CliffSearchJob(origin, source, candidateCount);
                if (candidateCount <= 0)
                {
                    detail = "source=" + source + " candidates=0";
                    _cliffSearchJob = null;
                    return CliffSearchStatus.Exhausted;
                }
            }

            CliffSearchJob job = _cliffSearchJob;
            Stopwatch frameTimer = Stopwatch.StartNew();
            int processedThisFrame = 0;
            bool completed = false;
            while (job.Cursor < job.CandidateCount &&
                processedThisFrame < CliffSearchCandidatesPerFrame)
            {
                int index = job.Cursor++;
                processedThisFrame++;
                RuntimeRainBoundarySample sample = CliffBoundaryCandidates[index];
                Vector3 direction = sample.Outward;
                direction.y = 0f;
                if (direction.sqrMagnitude < 0.01f)
                {
                    if (frameTimer.Elapsed.TotalMilliseconds >=
                        CliffSearchFrameBudgetMilliseconds) break;
                    continue;
                }
                direction.Normalize();

                Vector3 approach;
                if (!TryProjectStaticGround(sample.Position - direction * CliffApproachStandOff,
                    sample.Position.y, 1.4f, player.transform.root, out approach) ||
                    IsFailedCliffCandidate(approach))
                {
                    job.Failed++;
                }
                else if (!AutoBattleRoutePlanner.IsPointOnOwnedRainGraph(approach, 1.0f) ||
                    !AutoBattleRoutePlanner.HasSupportedStandingPoint(approach,
                        player.transform.root) ||
                    !AutoBattleRoutePlanner.CanFollowRouteSegment(approach,
                        sample.Position - direction * 0.08f, player.transform.root))
                {
                    job.ApproachRejected++;
                }
                else
                {
                    float drop;
                    string validation;
                    if (!TryValidateCliffApproach(approach, direction, player.transform.root,
                        out drop, out validation))
                    {
                        job.DropRejected++;
                        job.LastReject = validation;
                    }
                    else
                    {
                        float travelDistance = XzDistance(job.Origin, approach);
                        float score = drop * 20f + Mathf.Min(6f, sample.Width) * 2f -
                            travelDistance * 0.12f;
                        if (score > job.BestScore)
                        {
                            job.BestScore = score;
                            job.BestDrop = drop;
                            job.BestWidth = sample.Width;
                            job.BestChecked = index + 1;
                            job.BestEdge = approach;
                            job.BestOutward = direction;
                        }

                        // Preserve the original certainty-first early exits exactly.
                        if (drop >= CliffProbeDepth - 0.1f ||
                            (index >= 47 && job.BestScore > float.MinValue))
                            completed = true;
                    }
                }

                if (completed || frameTimer.Elapsed.TotalMilliseconds >=
                    CliffSearchFrameBudgetMilliseconds) break;
            }
            frameTimer.Stop();
            job.Frames++;
            job.CpuMilliseconds += frameTimer.Elapsed.TotalMilliseconds;
            if (job.Cursor >= job.CandidateCount) completed = true;

            if (!completed)
            {
                detail = "source=" + job.Source + " candidates=" + job.CandidateCount +
                    " progress=" + job.Cursor + "/" + job.CandidateCount +
                    " frames=" + job.Frames + " cpuMs=" +
                    job.CpuMilliseconds.ToString("0.0");
                return CliffSearchStatus.Pending;
            }

            _cliffSearchJob = null;
            if (job.BestScore > float.MinValue)
            {
                edge = job.BestEdge;
                outward = job.BestOutward;
                detail = "source=" + job.Source + " candidates=" + job.CandidateCount +
                    " checked=" + job.BestChecked + " scanned=" + job.Cursor +
                    " failed=" + job.Failed + " approachReject=" + job.ApproachRejected +
                    " dropReject=" + job.DropRejected + " width=" +
                    job.BestWidth.ToString("0.0") + " verifiedMinDrop=" +
                    job.BestDrop.ToString("0.0") + " lethal=" +
                    (job.BestDrop >= CliffFatalDrop) + " frames=" + job.Frames +
                    " cpuMs=" + job.CpuMilliseconds.ToString("0.0");
                return CliffSearchStatus.Found;
            }

            detail = "source=" + job.Source + " candidates=" + job.CandidateCount +
                " scanned=" + job.Cursor + " failed=" + job.Failed +
                " approachReject=" + job.ApproachRejected + " dropReject=" +
                job.DropRejected + " last=" + job.LastReject + " frames=" +
                job.Frames + " cpuMs=" + job.CpuMilliseconds.ToString("0.0");
            return CliffSearchStatus.Exhausted;
        }

        private static bool TryValidateCliffApproach(Vector3 approach, Vector3 outward,
            Transform ignoreRoot, out float minimumDrop, out string detail)
        {
            minimumDrop = float.MaxValue;
            detail = "invalid_direction";
            outward.y = 0f;
            if (outward.sqrMagnitude < 0.01f) return false;
            outward.Normalize();

            Vector3 lip = approach + outward * CliffApproachStandOff;
            if (!AutoBattleRoutePlanner.CanFollowRouteSegment(approach,
                lip - outward * 0.08f, ignoreRoot))
            {
                detail = "approach_blocked";
                return false;
            }
            if (!HasCliffExitClearance(lip, outward))
            {
                detail = "exit_lane_blocked";
                return false;
            }

            Vector3 side = Vector3.Cross(Vector3.up, outward).normalized;
            int fatal = 0;
            int supported = 0;
            int centerSupported = 0;
            int probeCount = CliffProbeDistances.Length * CliffProbeSideOffsets.Length;
            for (int d = 0; d < CliffProbeDistances.Length; d++)
            {
                for (int s = 0; s < CliffProbeSideOffsets.Length; s++)
                {
                    Vector3 probe = lip + outward * CliffProbeDistances[d] +
                        side * CliffProbeSideOffsets[s];
                    Vector3 ground;
                    if (!TryFindStaticGroundBelow(probe + Vector3.up * 1.25f, CliffProbeDepth,
                        ignoreRoot, out ground))
                    {
                        fatal++;
                        minimumDrop = Mathf.Min(minimumDrop, CliffProbeDepth);
                        continue;
                    }

                    float drop = lip.y - ground.y;
                    if (drop >= CliffFatalDrop)
                    {
                        fatal++;
                        minimumDrop = Mathf.Min(minimumDrop, drop);
                    }
                    else
                    {
                        supported++;
                        if (s == 1) centerSupported++;
                    }
                }
            }

            if (minimumDrop == float.MaxValue) minimumDrop = 0f;
            if (centerSupported > 0 || supported > 0 || fatal != probeCount)
            {
                detail = "drop_probe_rejected fatal=" + fatal + "/" + probeCount +
                    " supported=" + supported +
                    " centerSupported=" + centerSupported + " minDrop=" +
                    minimumDrop.ToString("0.0");
                return false;
            }

            detail = "drop_probe_ok fatal=" + fatal + "/" + probeCount +
                " supported=" + supported + " minDrop=" + minimumDrop.ToString("0.0");
            return true;
        }

        private static bool TryProjectStaticGround(Vector3 point, float referenceY, float maxDelta,
            Transform ignoreRoot, out Vector3 ground)
        {
            Vector3 origin = point + Vector3.up * (maxDelta + 0.8f);
            if (!TryFindStaticGroundBelow(origin, maxDelta * 2f + 1.6f, ignoreRoot, out ground))
                return false;
            return Mathf.Abs(ground.y - referenceY) <= maxDelta;
        }

        private static bool TryFindStaticGroundBelow(Vector3 origin, float distance,
            Transform ignoreRoot, out Vector3 ground)
        {
            ground = origin;
            try
            {
                RaycastHit[] hits = Physics.RaycastAll(origin, Vector3.down, distance);
                Array.Sort(hits, CompareHitDistance);
                for (int i = 0; i < hits.Length; i++)
                {
                    Collider collider = hits[i].collider;
                    if (collider == null || collider.isTrigger || hits[i].normal.y < 0.38f) continue;
                    Transform root = collider.transform == null ? null : collider.transform.root;
                    if (root != null && (root == ignoreRoot || root.GetComponent<Character>() != null))
                        continue;
                    ground = hits[i].point;
                    return true;
                }
            }
            catch { }
            return false;
        }

        private static bool TryProjectGround(Vector3 point, float referenceY, float maxDelta, out Vector3 ground)
        {
            ground = point;
            try
            {
                int mask = LayerMask.GetMask(new string[] { "Terrarin" });
                RaycastHit hit;
                bool found = mask != 0
                    ? Physics.Raycast(point + Vector3.up * 3.5f, Vector3.down, out hit, 9f, mask)
                    : Physics.Raycast(point + Vector3.up * 3.5f, Vector3.down, out hit, 9f);
                if (!found || Mathf.Abs(hit.point.y - referenceY) > maxDelta) return false;
                ground = hit.point;
                return true;
            }
            catch { return false; }
        }

        private static bool HasMapBlock(Vector3 from, Vector3 to)
        {
            Vector3 direction = to - from;
            float distance = direction.magnitude;
            if (distance < 0.05f) return false;
            direction /= distance;
            try
            {
                RaycastHit[] hits = Physics.RaycastAll(from, direction, distance - 0.15f);
                Array.Sort(hits, CompareHitDistance);
                for (int i = 0; i < hits.Length; i++)
                {
                    Collider collider = hits[i].collider;
                    if (collider == null || collider.isTrigger) continue;
                    Transform root = collider.transform == null ? null : collider.transform.root;
                    if (root != null && root.GetComponent<Character>() != null) continue;
                    return true;
                }
            }
            catch { }
            return false;
        }

        private static bool HasBodyCover(Character enemy, Vector3 point)
        {
            if (enemy == null || enemy.transform == null) return false;
            Vector3 from = enemy.transform.position + Vector3.up * 1.25f;
            int blocked = 0;
            if (HasMapBlock(from, point + Vector3.up * 0.55f)) blocked++;
            if (HasMapBlock(from, point + Vector3.up * 1.05f)) blocked++;
            if (HasMapBlock(from, point + Vector3.up * 1.45f)) blocked++;
            return blocked >= 2;
        }

        private static bool HasCliffExitClearance(Vector3 edgeGround, Vector3 outward)
        {
            outward.y = 0f;
            if (outward.sqrMagnitude < 0.01f) return false;
            outward.Normalize();
            Vector3 from = edgeGround + outward * 0.15f;
            Vector3 to = edgeGround + outward * 2.6f;
            return !HasMapBlock(from + Vector3.up * 0.65f, to + Vector3.up * 0.65f) &&
                   !HasMapBlock(from + Vector3.up * 1.25f, to + Vector3.up * 1.25f);
        }

        private static void AbandonCliffCandidate(string reason, Vector3 position,
            float verticalDrop, float forwardProgress)
        {
            MarkCliffCandidateFailed(_cliffEdge);
            _hasCliff = false;
            _cliffJumpLogged = false;
            _cliffJumpStartedAt = 0f;
            _nextCliffJumpTraceAt = 0f;
            _cliffJumpStart = Vector3.zero;
            _nextCliffScanAt = 0f;
            AutoBattleInput.ClearMovement();
            SurvivalCombatAdapter.SuspendSurvivalNavigation("suicide_cliff_failed");
            FileLogger.Log("SURVIVAL", "cliff candidate abandoned reason=" + reason +
                " edge=" + FormatVec(_cliffEdge) + " pos=" + FormatVec(position) +
                " drop=" + verticalDrop.ToString("0.0") +
                " forward=" + forwardProgress.ToString("0.0"));
        }

        private static bool SafeIsOnGround(Character player)
        {
            try { return player != null && player.IsOnGround(); }
            catch { return false; }
        }

        private static bool IsRouteFailure(string intent)
        {
            if (!string.Equals(SurvivalCombatAdapter.LastPathIntent, intent, StringComparison.Ordinal)) return false;
            return string.Equals(SurvivalCombatAdapter.LastPath, "no_path", StringComparison.Ordinal) ||
                   string.Equals(SurvivalCombatAdapter.LastPath, "route_null", StringComparison.Ordinal) ||
                   string.Equals(SurvivalCombatAdapter.LastPath, "empty_route_repath", StringComparison.Ordinal) ||
                   string.Equals(SurvivalCombatAdapter.LastPath, "stuck_repath", StringComparison.Ordinal) ||
                   string.Equals(SurvivalCombatAdapter.LastPath, "jump_lane_blocked", StringComparison.Ordinal) ||
                   string.Equals(SurvivalCombatAdapter.LastPath, "wall_repath", StringComparison.Ordinal) ||
                   string.Equals(SurvivalCombatAdapter.LastPath, "path_pending_timeout", StringComparison.Ordinal) ||
                   string.Equals(SurvivalCombatAdapter.LastPath, "path_candidate_rejected", StringComparison.Ordinal);
        }

        private static void MarkCandidateFailed(Vector3 point)
        {
            FailedCandidates[_failedCandidateCursor] = point;
            FailedCandidateUntil[_failedCandidateCursor] = Time.time + 8f;
            _failedCandidateCursor = (_failedCandidateCursor + 1) % FailedCandidates.Length;
        }

        private static void MarkCliffCandidateFailed(Vector3 point)
        {
            FailedCliffCandidates[_failedCliffCandidateCursor] = point;
            FailedCliffCandidateUntil[_failedCliffCandidateCursor] = Time.time + 45f;
            _failedCliffCandidateCursor = (_failedCliffCandidateCursor + 1) %
                FailedCliffCandidates.Length;
        }

        private static bool IsFailedCliffCandidate(Vector3 point)
        {
            for (int i = 0; i < FailedCliffCandidates.Length; i++)
            {
                if (Time.time < FailedCliffCandidateUntil[i] &&
                    XzDistance(point, FailedCliffCandidates[i]) < 3f)
                    return true;
            }
            return false;
        }

        private static bool IsFailedCandidate(Vector3 point)
        {
            for (int i = 0; i < FailedCandidates.Length; i++)
            {
                if (Time.time < FailedCandidateUntil[i] && XzDistance(point, FailedCandidates[i]) < 3f)
                    return true;
            }
            return false;
        }

        private static void ClearFailedCandidates()
        {
            for (int i = 0; i < FailedCandidateUntil.Length; i++)
            {
                FailedCandidates[i] = Vector3.zero;
                FailedCandidateUntil[i] = 0f;
            }
            _failedCandidateCursor = 0;
        }

        private static void ClearFailedCliffCandidates()
        {
            for (int i = 0; i < FailedCliffCandidateUntil.Length; i++)
            {
                FailedCliffCandidates[i] = Vector3.zero;
                FailedCliffCandidateUntil[i] = 0f;
            }
            _failedCliffCandidateCursor = 0;
            CliffBoundaryCandidates.Clear();
        }

        private static void ResetAttackSearchRuntime()
        {
            _hasAttackPoint = false;
            _attackPoint = Vector3.zero;
            _attackPointTargetPosition = Vector3.zero;
            _attackPointSetAt = 0f;
            _attackPointLastProgressAt = 0f;
            _nextAttackSearchTraceAt = 0f;
            _nextPursuitAimTraceAt = 0f;
            _combatStrafeSign = 1;
            _combatStrafeSwitchAt = 0f;
            _combatMoveProgressAt = 0f;
            _nextCombatMoveTraceAt = 0f;
            _combatMoveLastPosition = Vector3.zero;
            _combatMoveDirection = Vector3.zero;
            _combatMoveTargetUid = 0;
        }

        private static string FormatVec(Vector3 value)
        {
            return "(" + value.x.ToString("0.0") + "," + value.y.ToString("0.0") + "," +
                value.z.ToString("0.0") + ")";
        }

        private static int CompareHitDistance(RaycastHit a, RaycastHit b)
        {
            return a.distance.CompareTo(b.distance);
        }

        private static bool IsInSurvivalGame(GameApp app)
        {
            try
            {
                if (app == null || app.channel_connection == null || app.channel_connection.room == null) return false;
                if (app.channel_connection.state != ChannelConnection.State.kInGame) return false;
                return (RoomInfo.GameType)(byte)app.channel_connection.room.room_info.game_type == RoomInfo.GameType.kGameTypeChiji;
            }
            catch { return false; }
        }

        private static bool IsCharacterControlReady(GameApp app, Character player)
        {
            try
            {
                if (app == null || app.channel_connection == null || player == null) return false;
                return string.Equals(app.channel_connection.game_state.ToString(), "kAlive", StringComparison.Ordinal);
            }
            catch { return false; }
        }

        private static bool IsBalanceState(GameApp app)
        {
            try
            {
                if (app == null || app.channel_connection == null) return false;
                string connectionState = app.channel_connection.state.ToString();
                string gameState = app.channel_connection.game_state.ToString();
                return string.Equals(connectionState, "kInBalance", StringComparison.Ordinal) ||
                       string.Equals(gameState, "kBalance", StringComparison.Ordinal) ||
                       string.Equals(gameState, "kInBalance", StringComparison.Ordinal);
            }
            catch { return false; }
        }

        private static int CountRoomParticipants(GameApp app)
        {
            try
            {
                object room = app == null || app.channel_connection == null ? null : app.channel_connection.room;
                if (room == null) return 0;

                int count = 0;
                Array slots = ReadMember(room, "room_slot") as Array;
                if (slots != null)
                {
                    for (int i = 0; i < slots.Length; i++)
                    {
                        object slot = slots.GetValue(i);
                        if (slot == null || ReadMember(slot, "client") == null) continue;
                        object status = ReadMember(slot, "status");
                        if (status != null && Convert.ToInt32(status) == 2) continue;
                        count++;
                    }
                }

                if (count == 0)
                {
                    object roomInfo = ReadMember(room, "room_info");
                    object currentClients = ReadMember(roomInfo, "current_client_num");
                    if (currentClients != null) count = Convert.ToInt32(currentClients);
                }
                return count;
            }
            catch { return 0; }
        }

        private static object ReadMember(object instance, string name)
        {
            if (instance == null) return null;
            Type type = instance.GetType();
            FieldInfo field = type.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (field != null) return field.GetValue(instance);
            PropertyInfo property = type.GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            return property == null ? null : property.GetValue(instance, null);
        }

        private static void CancelActiveSession()
        {
            try
            {
                GameApp app = GameApp.Instance;
                if (app == null) return;
                NewUIRoom roomUi = NewUIRoom.getInstance();
                if (app.lobby_connection != null && (_matching || (roomUi != null && roomUi.InMatch)))
                    app.lobby_connection.RequestCancelMatching();
                if (_roundActive && app.channel_connection != null &&
                    app.channel_connection.state == ChannelConnection.State.kInGame)
                    app.channel_connection.LeaveGame();
            }
            catch (Exception ex)
            {
                FileLogger.Log("SURVIVAL", "stop cleanup failed: " + ex.Message);
            }
            finally
            {
                _matching = false;
                _pendingSurvivalMatchRequest = false;
                _cancelPending = false;
                _matchStartedAt = 0f;
            }
        }

        private static int ReadPrivateInt(object instance, string fieldName)
        {
            try
            {
                FieldInfo field = instance.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
                return field == null ? 0 : (int)field.GetValue(instance);
            }
            catch { return 0; }
        }

        private sealed class CliffSearchJob
        {
            public readonly Vector3 Origin;
            public readonly string Source;
            public readonly int CandidateCount;
            public int Cursor;
            public int Failed;
            public int ApproachRejected;
            public int DropRejected;
            public string LastReject = "none";
            public float BestScore = float.MinValue;
            public float BestDrop;
            public float BestWidth;
            public int BestChecked;
            public Vector3 BestEdge;
            public Vector3 BestOutward;
            public int Frames;
            public double CpuMilliseconds;

            public CliffSearchJob(Vector3 origin, string source, int candidateCount)
            {
                Origin = origin;
                Source = source;
                CandidateCount = candidateCount;
            }
        }

        private sealed class EnemyTrack
        {
            public int Uid;
            public Character Target;
            public Vector3 Position;
            public Vector3 Velocity;
            public float SampleAt;
            public float Distance;
            public float ClosingSpeed;
            public float HorizontalSpeed;
            public float LastVisibleAt;
            public float LastDamagedAt;
            public float HealthPercent;
            public int LastHp;
            public bool Hidden;
            public bool Invincible;
            public bool FacingPlayer;
            public bool FireLine;
            public bool Visible;
        }

        private static float XzDistance(Vector3 a, Vector3 b)
        {
            float x = a.x - b.x;
            float z = a.z - b.z;
            return Mathf.Sqrt(x * x + z * z);
        }

        private static string SafeName(Character target)
        {
            try
            {
                if (target == null) return "-";
                if (!string.IsNullOrEmpty(target.baseName)) return target.baseName;
                return target.name ?? "-";
            }
            catch { return "-"; }
        }
    }
}
