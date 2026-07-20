using System;
using System.Collections.Generic;
using System.Reflection;
using ASWDEBUG.Cheats.AutoBattle;
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
        CombatTest,
        RoomTest,
        MapBake,
        Stopped
    }

    public static class SurvivalBotManager
    {
        private static readonly List<Character> Enemies = new List<Character>(16);
        private static readonly HashSet<int> ParticipantIds = new HashSet<int>();
        private static readonly HashSet<int> ConfirmedDeadParticipantIds = new HashSet<int>();
        private static readonly Dictionary<int, EnemyTrack> EnemyTracks = new Dictionary<int, EnemyTrack>(16);
        private static readonly float[] SafeRadii = { 5f, 9f, 13f };

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
        private static float _attackTargetVisibleAt;
        private static float _attackTargetLockedAt;
        private static float _attackEngagementStartedAt;
        private static float _attackLastDamageAt;
        private static int _attackTargetLastVital;
        private static float _opportunityCooldownUntil;
        private static float _emergencyTargetVisibleAt;
        private static float _emergencyTargetLockedAt;
        private static float _nextEnemyTrackAt;
        private static float _recentDamageAt;
        private static int _lastPlayerHp;
        private static int _lastPlayerShield;
        private static float _safePointLeaseUntil;
        private static int _lastExposureCount;
        private static float _suicideStartedAt;
        private static float _nextCliffScanAt;
        private static float _nextGmLeaveAt;
        private static float _lastCliffProgressAt;
        private static float _lastCliffDistance;
        private static float _nextCliffTraceAt;
        private static float _nextSuicideRequestAt;
        private static Vector3 _safePoint;
        private static Vector3 _attackPoint;
        private static Vector3 _attackPointTargetPosition;
        private static Vector3 _attackSearchLookDirection;
        private static Vector3 _cliffEdge;
        private static Vector3 _cliffOutward;
        private static readonly Vector3[] FailedCandidates = new Vector3[5];
        private static readonly float[] FailedCandidateUntil = new float[5];
        private static int _failedCandidateCursor;
        private static int _combatStrafeSign = 1;
        private static bool _hasSafePoint;
        private static bool _hasAttackPoint;
        private static bool _hasCliff;
        private static bool _cliffJumpLogged;
        private static bool _serverSuicideRequested;
        private static bool _combatStrafeActive;
        private static Character _attackTarget;
        private static Character _searchTarget;
        private static float _searchTargetLockedAt;
        private static Character _emergencyTarget;
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
        public static bool CombatTestEnabled { get; private set; }
        public static bool RoomTestEnabled { get; private set; }
        public static bool MapBakeEnabled { get; private set; }
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
            CombatTestEnabled = false;
            RoomTestEnabled = false;
            MapBakeEnabled = false;
            Phase = SurvivalBotPhase.Stopped;
            StatusText = "等待手动启动";
        }

        public static void Tick(Level level, Character player, Camera camera)
        {
            AutoBattleInput.BeginFrame();

            // Map baking is a local, read-only scene operation and must not depend on the game proxy.
            if (MapBakeEnabled)
            {
                TickMapBake(level, player);
                return;
            }

            if (NetworkRouteManager.ProxyRequired && NetworkRouteManager.HasError)
            {
                if (Enabled) Stop("network_proxy_failed");
                if (CombatTestEnabled) SetCombatTestEnabled(false, "network_proxy_failed");
                if (RoomTestEnabled) SetRoomTestEnabled(false, "network_proxy_failed");
                return;
            }

            if (Input.GetKeyDown(KeyCode.F8))
                SetEnabled(!Enabled, "hotkey");

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

        public static void SetEnabled(bool enabled, string reason)
        {
            if (!enabled)
            {
                Stop(reason);
                return;
            }

            if (Enabled) return;
            if (CombatTestEnabled) DisableCombatTest("survival_loop_enabled");
            if (RoomTestEnabled) DisableRoomTest("survival_loop_enabled");
            if (MapBakeEnabled) DisableMapBake("survival_loop_enabled");
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
            DisableSurvivalLoopForCombatTest();
            MapBakeEnabled = true;
            AutoBattleInput.ClearAll();
            AutoBattleManager.SetEnabled(false, "map_bake_start");
            SurvivalCombatAdapter.ResetSurvivalRuntime("map_bake_start");
            Phase = SurvivalBotPhase.MapBake;
            StatusText = "地图建图已开启，等待进入地图";
            FileLogger.Log("AUTO-BATTLE][NAVMESH", "map bake enabled reason=" + reason);
        }

        public static void Stop(string reason)
        {
            if (!Enabled && !CombatTestEnabled && !RoomTestEnabled && !MapBakeEnabled &&
                Phase == SurvivalBotPhase.Stopped) return;
            Enabled = false;
            CombatTestEnabled = false;
            RoomTestEnabled = false;
            MapBakeEnabled = false;
            Phase = SurvivalBotPhase.Stopped;
            StatusText = "已停止: " + reason;
            AutoBattleInput.ClearAll();
            AutoBattleManager.SetEnabled(false, reason);
            SurvivalCombatAdapter.ResetSurvivalRuntime(reason);
            CancelActiveSession();
            _roundActive = false;
            _controlStarted = false;
            _awaitingReward = false;
            _cardManager = null;
            _emergencyTarget = null;
            FileLogger.Log("SURVIVAL", StatusText);
        }

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
            _nextEnemyTrackAt = 0f;
            _recentDamageAt = 0f;
            _lastPlayerHp = player == null ? 0 : player.hp;
            try { _lastPlayerShield = player == null ? 0 : player.shield; }
            catch { _lastPlayerShield = 0; }
            _safePointLeaseUntil = 0f;
            _lastExposureCount = 0;
            ClearFailedCandidates();
            _nextSafePointAt = 0f;
            ResetAttackSearchRuntime();
            _nextCliffTraceAt = 0f;
            _cliffJumpLogged = false;
            _nextSuicideRequestAt = 0f;
            _serverSuicideRequested = false;
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
            _pendingGmUid = 0;
            _pendingGmTeam = 0;
            _pendingGmGeneration = 0;
            _emergencyTarget = null;
            AutoBattleInput.ClearAll();
            SurvivalCombatAdapter.ResetSurvivalRuntime("round_finish");
            Phase = SurvivalBotPhase.Balance;
            StatusText = "等待结算/返回大厅";
            _awaitingReward = !_roundEndedByGm;
            _rewardWaitStartedAt = Time.time;
            _nextMatchAt = Time.time + (_awaitingReward ? 12f : 3f);
        }

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
            if (level == null || level.state != Level.State.kReady)
            {
                StatusText = "地图建图 | 等待地图加载";
                return;
            }

            RuntimeRainNavSnapshot snapshot = RuntimeRainNavMesh.GetStatusSnapshot();
            if (snapshot.State == RuntimeRainNavState.Ready)
            {
                StatusText = "地图建图 | 已完成并可复用 | " + snapshot.MapName +
                    " | 节点 " + snapshot.GraphSize + " | 缓存 " + snapshot.CacheStatus;
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
            StatusText = "地图建图 | 准备 " + snapshot.MapName + " | " + snapshot.Detail;
        }

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
                (player.num_killed > _baselineKills || player.holding_attack_count > _baselineAssists))
            {
                _taskCompleted = true;
                SurvivalCombatAdapter.CancelSurvivalAttack();
                SetAttackTarget(null, "objective_complete");
                _searchTarget = null;
                FileLogger.Log("SURVIVAL", "kill/assist objective complete kills=" + player.num_killed +
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

            if (!_participantLocked && Time.time - _controlStartedAt >= SurvivalBotSettings.ParticipantCaptureSeconds)
            {
                _participantLocked = true;
                InitialPlayers = Math.Max(InitialPlayers, ParticipantIds.Count);
                FileLogger.Log("SURVIVAL", "participants locked initial=" + InitialPlayers);
            }

            int initial = Math.Max(InitialPlayers, ParticipantIds.Count);
            int threshold = Math.Max(1, initial / 2);
            bool rankSecured = _participantLocked && RemainingPlayers <= threshold;

            if (_taskCompleted && rankSecured)
            {
                ClearEmergencyTarget("objective_complete");
                TickSuicide(app, player, camera);
                return;
            }

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
                _emergencyTarget = contender;
                if (_emergencyTarget == null) return false;
                _emergencyTargetLockedAt = Time.time;
                EnemyTrack acquiredTrack = GetEnemyTrack(_emergencyTarget);
                _emergencyTargetVisibleAt = acquiredTrack != null && acquiredTrack.Visible ? Time.time : 0f;
                FileLogger.Log("SURVIVAL", "emergency counterattack start uid=" + _emergencyTarget.uid +
                    " dist=" + XzDistance(player.transform.position, _emergencyTarget.transform.position).ToString("0.0") +
                    " threat=" + contenderScore.ToString("0") + " hidden=" + IsTargetHidden(_emergencyTarget));
            }

            float distance = XzDistance(player.transform.position, _emergencyTarget.transform.position);
            bool hidden = IsTargetHidden(_emergencyTarget);
            bool strictLine = SurvivalCombatAdapter.SurvivalHasEmergencyFireLine(player, _emergencyTarget, camera);
            EnemyTrack track = GetEnemyTrack(_emergencyTarget);
            if (strictLine) _emergencyTargetVisibleAt = Time.time;
            bool closing = track != null && track.ClosingSpeed >= 0.8f;
            bool recentlyDamaged = Time.time - _recentDamageAt <= 1.2f;
            float releaseDistance = hidden ? 6.8f : triggerDistance + 4f;
            bool holdThreat = distance <= 4.5f || (hidden && distance <= 6f) ||
                (distance <= releaseDistance &&
                 (strictLine || Time.time - _emergencyTargetVisibleAt <= 0.8f || closing || recentlyDamaged));
            if (!holdThreat)
            {
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
                SurvivalCombatAdapter.TryUseSurvivalDefense(player, SurvivalBotSettings.DefenseMode);
                MoveEmergency(player, _emergencyTarget, distance);
                StatusText = "近敌反击 | 目标短暂遮挡 | 距离 " + distance.ToString("0.0");
                return true;
            }

            bool invincible = track != null && track.Invincible;
            if (invincible || (objectiveComplete && distance > 6f))
            {
                SurvivalCombatAdapter.CancelSurvivalAttack();
                SurvivalCombatAdapter.CloseSurvivalScope(player);
                SurvivalCombatAdapter.TryUseSurvivalDefense(player, SurvivalBotSettings.DefenseMode);
                MoveEmergency(player, _emergencyTarget, distance);
                StatusText = invincible
                    ? "近敌威胁 | 目标无敌，优先脱离"
                    : "任务已完成 | 优先脱离追兵 | 距离 " + distance.ToString("0.0");
                return true;
            }

            bool fired = SurvivalCombatAdapter.AttackEmergency(player, _emergencyTarget, camera,
                out strictLine, out distance);
            MoveEmergency(player, _emergencyTarget, distance);
            SurvivalCombatAdapter.LogCombatState(player, _emergencyTarget, strictLine, distance, fired);
            StatusText = "近敌反击 | 目标 " + SafeName(_emergencyTarget) + " | 距离 " +
                distance.ToString("0.0") + " / " + triggerDistance.ToString("0.0") + " | 隐身 " +
                IsTargetHidden(_emergencyTarget) + " | 开火 " + fired;
            return true;
        }

        private static void ClearEmergencyTarget(string reason)
        {
            if (_emergencyTarget != null)
                FileLogger.Log("SURVIVAL", "emergency counterattack stop uid=" + _emergencyTarget.uid +
                    " reason=" + reason);
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

            if (exposure > 0)
            {
                SurvivalCombatAdapter.TryUseSurvivalDefense(player, SurvivalBotSettings.DefenseMode);
            }

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

            if (opportunityOnly) MoveCombatStrafe(player, _attackTarget);
            else MoveAttackPursuit(player, _attackTarget, camera, false);
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
            if (Phase != SurvivalBotPhase.Suicide)
            {
                Phase = SurvivalBotPhase.Suicide;
                _suicideStartedAt = Time.time;
                _nextCliffScanAt = 0f;
                _hasCliff = false;
                _lastCliffDistance = float.MaxValue;
                _lastCliffProgressAt = Time.time;
                _nextCliffTraceAt = 0f;
                _cliffJumpLogged = false;
                _nextSuicideRequestAt = 0f;
                _serverSuicideRequested = false;
                AutoBattleInput.ClearAll();
                FileLogger.Log("SURVIVAL", "suicide phase started; cliff preferred fallback=" +
                    SurvivalBotSettings.SuicideFallbackSeconds.ToString("0") + "s");
            }

            if (_serverSuicideRequested)
            {
                AutoBattleInput.ClearAll();
                StatusText = "已请求服务器自杀，等待结算";
                return;
            }

            if (!_hasCliff && Time.time >= _nextCliffScanAt)
            {
                _nextCliffScanAt = Time.time + 2f;
                _hasCliff = TryFindCliff(player, out _cliffEdge, out _cliffOutward);
                if (_hasCliff)
                {
                    _lastCliffDistance = XzDistance(player.transform.position, _cliffEdge);
                    _lastCliffProgressAt = Time.time;
                    _cliffJumpLogged = false;
                    FileLogger.Log("SURVIVAL", "reachable cliff candidate edge=" + FormatVec(_cliffEdge) +
                        " outward=" + FormatVec(_cliffOutward) + " dist=" + _lastCliffDistance.ToString("0.0"));
                }
                else if (Time.time >= _nextCliffTraceAt)
                {
                    _nextCliffTraceAt = Time.time + 4f;
                    FileLogger.Log("SURVIVAL", "no directly reachable cliff; waiting for server-suicide fallback");
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
                AutoBattleInput.SetMoveWorld(player, _cliffOutward, false);
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
                    MarkCandidateFailed(_cliffEdge);
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
                        MarkCandidateFailed(_cliffEdge);
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

                AutoBattleInput.SetMoveWorld(player, _cliffOutward, false);
                AutoBattleInput.PressAction(ActionType.kActionJump, 0.12f);
                AutoBattleInput.HoldAction(ActionType.kActionJump, 0.38f);
                if (!_cliffJumpLogged)
                {
                    _cliffJumpLogged = true;
                    FileLogger.Log("SURVIVAL", "cliff jump issued edge=" + FormatVec(_cliffEdge) +
                        " outward=" + FormatVec(_cliffOutward));
                }
                StatusText = "任务完成，跳崖结束对局";
                return;
            }

            StatusText = "任务完成，搜索悬崖";
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
            HashSet<int> counted = new HashSet<int>();
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
                int exposure = CountExposure(point, player);
                penalty += exposure * 220f;
            }
            return penalty;
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
            if ((_lastPlayerHp > 0 && hp < _lastPlayerHp) || shield < _lastPlayerShield)
                _recentDamageAt = Time.time;
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
                if (score < 95f || score <= bestScore) continue;
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
                _combatStrafeActive = false;
                SurvivalCombatAdapter.SuspendSurvivalNavigation("combat");
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
                if (AutoBattleRoutePlanner.HasForwardBlock(player.transform.position, candidate, player.transform.root))
                    continue;
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

            AutoBattleInput.ClearMovement();
            if (AutoBattleRoutePlanner.ShouldJumpForwardObstacle(player.transform.position, away, player.transform.root))
            {
                AutoBattleInput.PressAction(ActionType.kActionJump, 0.10f);
                AutoBattleInput.HoldAction(ActionType.kActionJump, 0.22f);
            }
        }

        private static void MoveCombatStrafe(Character player, Character target)
        {
            if (player == null || target == null) return;
            Vector3 toTarget = target.transform.position - player.transform.position;
            toTarget.y = 0f;
            if (toTarget.sqrMagnitude < 0.01f) return;
            float distance = toTarget.magnitude;
            if (_combatStrafeActive)
            {
                if (distance >= 15f) _combatStrafeActive = false;
            }
            else if (distance <= 13f) _combatStrafeActive = true;
            if (!_combatStrafeActive)
            {
                AutoBattleInput.ClearMovement();
                return;
            }
            toTarget.Normalize();
            Vector3 side = Vector3.Cross(Vector3.up, toTarget) * _combatStrafeSign;
            if (AutoBattleRoutePlanner.HasForwardBlock(player.transform.position, side, player.transform.root))
            {
                Vector3 opposite = -side;
                if (AutoBattleRoutePlanner.HasForwardBlock(player.transform.position, opposite, player.transform.root))
                {
                    AutoBattleInput.ClearMovement();
                    return;
                }
                side = opposite;
                _combatStrafeSign = -_combatStrafeSign;
            }
            AutoBattleInput.SetMoveWorld(player, side, false);
        }

        private static Vector3 MoveAttackPursuit(Character player, Character target, Camera camera, bool lookAlongRoute)
        {
            if (player == null || player.transform == null || target == null || target.transform == null)
                return Vector3.zero;

            Vector3 targetPosition = target.transform.position;
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
                AutoBattleInput.ClearMovement();
                return Vector3.zero;
            }

            AutoBattleInput.SetMoveWorld(player, move, false);
            if (lookAlongRoute && camera != null)
            {
                Vector3 desiredLook = move.normalized;
                if (_attackSearchLookDirection.sqrMagnitude < 0.01f) _attackSearchLookDirection = desiredLook;
                else
                {
                    _attackSearchLookDirection = Vector3.Slerp(_attackSearchLookDirection, desiredLook,
                        Mathf.Clamp01(Time.deltaTime * 8f));
                    _attackSearchLookDirection.y = 0f;
                    if (_attackSearchLookDirection.sqrMagnitude > 0.01f) _attackSearchLookDirection.Normalize();
                }
                SurvivalCombatAdapter.LookSurvival(player, camera,
                    player.transform.position + _attackSearchLookDirection * 8f + Vector3.up);
            }
            return move;
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

        private static bool TryFindCliff(Character player, out Vector3 edge, out Vector3 outward)
        {
            edge = Vector3.zero;
            outward = Vector3.zero;
            Vector3 origin = player.transform.position;
            float bestDistance = float.MaxValue;
            for (int i = 0; i < 24; i++)
            {
                Vector3 dir = Quaternion.AngleAxis(i * 15f, Vector3.up) * Vector3.forward;
                Vector3 lastGround = Vector3.zero;
                float lastSafeDistance = 0f;
                for (float distance = 2f; distance <= 18f; distance += 1f)
                {
                    Vector3 ground;
                    if (TryProjectGround(origin + dir * distance, origin.y, 3f, out ground))
                    {
                        lastGround = ground;
                        lastSafeDistance = distance;
                        continue;
                    }

                    if (lastSafeDistance < 4f || !IsFatalDrop(lastGround, dir)) break;
                    Vector3 candidate = lastGround - dir * 1.1f;
                    if (IsFailedCandidate(candidate)) break;
                    if (!HasCliffExitClearance(candidate, dir)) break;
                    float routePenalty = AutoBattleRoutePlanner.CandidatePenalty(origin, candidate, player.transform.root);
                    bool directlyReachable = routePenalty <= 0.01f &&
                        AutoBattleRoutePlanner.CanFollowSegment(origin, candidate, player.transform.root);
                    if (directlyReachable && lastSafeDistance < bestDistance)
                    {
                        bestDistance = lastSafeDistance;
                        edge = candidate;
                        outward = dir;
                    }
                    break;
                }
            }
            return outward.sqrMagnitude > 0.01f;
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

        private static bool IsFatalDrop(Vector3 edgeGround, Vector3 outward)
        {
            try
            {
                Vector3 probe = edgeGround + outward.normalized * 1.8f + Vector3.up * 0.8f;
                int mask = LayerMask.GetMask(new string[] { "Terrarin" });
                RaycastHit hit;
                bool found = mask != 0
                    ? Physics.Raycast(probe, Vector3.down, out hit, 12f, mask)
                    : Physics.Raycast(probe, Vector3.down, out hit, 12f);
                return !found || edgeGround.y - hit.point.y >= 5f;
            }
            catch { return false; }
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

        private static void ResetAttackSearchRuntime()
        {
            _hasAttackPoint = false;
            _attackPoint = Vector3.zero;
            _attackPointTargetPosition = Vector3.zero;
            _attackSearchLookDirection = Vector3.zero;
            _attackPointSetAt = 0f;
            _attackPointLastProgressAt = 0f;
            _nextAttackSearchTraceAt = 0f;
            _combatStrafeSign = 1;
            _combatStrafeActive = false;
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

        private sealed class EnemyTrack
        {
            public int Uid;
            public Character Target;
            public Vector3 Position;
            public float SampleAt;
            public float Distance;
            public float ClosingSpeed;
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
