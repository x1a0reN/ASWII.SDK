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
        Stopped
    }

    public static class SurvivalBotManager
    {
        private static readonly List<Character> Enemies = new List<Character>(16);
        private static readonly HashSet<int> ParticipantIds = new HashSet<int>();
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
        private static float _nextAttackPointAt;
        private static float _suicideStartedAt;
        private static float _nextCliffScanAt;
        private static float _nextGmLeaveAt;
        private static float _lastCliffProgressAt;
        private static float _lastCliffDistance;
        private static Vector3 _safePoint;
        private static Vector3 _attackPoint;
        private static Vector3 _cliffEdge;
        private static Vector3 _cliffOutward;
        private static Vector3 _failedCandidate;
        private static float _failedCandidateUntil;
        private static bool _hasSafePoint;
        private static bool _hasAttackPoint;
        private static bool _hasCliff;
        private static Character _attackTarget;
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
            Phase = SurvivalBotPhase.Stopped;
            StatusText = "等待手动启动";
        }

        public static void Tick(Level level, Character player, Camera camera)
        {
            AutoBattleInput.BeginFrame();

            if (NetworkRouteManager.ProxyRequired && NetworkRouteManager.HasError)
            {
                if (Enabled) Stop("network_proxy_failed");
                if (CombatTestEnabled) SetCombatTestEnabled(false, "network_proxy_failed");
                return;
            }

            if (Input.GetKeyDown(KeyCode.F8))
                SetEnabled(!Enabled, "hotkey");

            if (CombatTestEnabled)
            {
                TickCombatTest(GameApp.Instance, level, player, camera);
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
            DisableSurvivalLoopForCombatTest();
            CombatTestEnabled = true;
            _attackTarget = null;
            _emergencyTarget = null;
            _hasAttackPoint = false;
            _nextAttackPointAt = 0f;
            AutoBattleManager.SetEnabled(true, "combat_test_start");
            Phase = SurvivalBotPhase.CombatTest;
            StatusText = "战斗测试已开启，等待进入对局";
            FileLogger.Log("AUTO-BATTLE", "combat test enabled reason=" + reason);
        }

        public static void Stop(string reason)
        {
            if (!Enabled && !CombatTestEnabled && Phase == SurvivalBotPhase.Stopped) return;
            Enabled = false;
            CombatTestEnabled = false;
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
            _nextAttackPointAt = 0f;
            Phase = SurvivalBotPhase.Stopped;
            StatusText = "战斗测试已关闭";
            FileLogger.Log("AUTO-BATTLE", "combat test disabled reason=" + reason);
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
            _hasSafePoint = false;
            _hasAttackPoint = false;
            _hasCliff = false;
            _attackTarget = null;
            _emergencyTarget = null;
            _failedCandidateUntil = 0f;
            _nextSafePointAt = 0f;
            _nextAttackPointAt = 0f;
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

            if (_taskCompleted)
            {
                ClearEmergencyTarget("objective_complete");
                if (rankSecured)
                {
                    TickSuicide(app, player, camera);
                }
                else
                {
                    TickHide(player, camera);
                    StatusText = "任务已完成，等待排名进入前半 | 存活 " + RemainingPlayers +
                        " / 前半线 " + threshold + " | 路径 " + SurvivalCombatAdapter.LastPath;
                }
                return;
            }

            if (TickEmergencyCounterattack(player, camera)) return;

            if (!_participantLocked || RemainingPlayers > threshold)
            {
                TickHide(player, camera);
            }
            else
            {
                ClearEmergencyTarget("attack_phase");
                TickAttack(player, camera);
            }
        }

        private static bool TickEmergencyCounterattack(Character player, Camera camera)
        {
            if (player == null || camera == null) return false;

            float triggerDistance = SurvivalBotSettings.EmergencyDistance;
            float releaseDistance = triggerDistance + 2f;
            bool strictLine = false;
            float distance = float.MaxValue;

            if (IsEmergencyTargetUsable(_emergencyTarget))
            {
                distance = XzDistance(player.transform.position, _emergencyTarget.transform.position);
                float currentLimit = IsTargetHidden(_emergencyTarget) ? 6f : releaseDistance;
                strictLine = distance <= currentLimit &&
                    SurvivalCombatAdapter.SurvivalHasEmergencyFireLine(player, _emergencyTarget, camera);
            }

            if (!strictLine)
            {
                ClearEmergencyTarget("threat_lost");
                float bestDistance = triggerDistance;
                for (int i = 0; i < Enemies.Count; i++)
                {
                    Character candidate = Enemies[i];
                    if (!IsEmergencyTargetUsable(candidate)) continue;
                    float candidateDistance = XzDistance(player.transform.position, candidate.transform.position);
                    float candidateLimit = IsTargetHidden(candidate) ? 6f : triggerDistance;
                    if (candidateDistance > candidateLimit || candidateDistance > bestDistance) continue;
                    if (!SurvivalCombatAdapter.SurvivalHasEmergencyFireLine(player, candidate, camera)) continue;
                    bestDistance = candidateDistance;
                    _emergencyTarget = candidate;
                }

                if (_emergencyTarget == null) return false;
                distance = bestDistance;
                FileLogger.Log("SURVIVAL", "emergency counterattack start uid=" + _emergencyTarget.uid +
                    " dist=" + distance.ToString("0.0") + " trigger=" + triggerDistance.ToString("0.0") +
                    " hidden=" + IsTargetHidden(_emergencyTarget));
            }

            bool fired = SurvivalCombatAdapter.AttackEmergency(player, _emergencyTarget, camera,
                out strictLine, out distance);
            if (!strictLine)
            {
                ClearEmergencyTarget("strict_line_lost");
                return false;
            }

            Phase = SurvivalBotPhase.Emergency;
            AutoBattleInput.ClearMovement();
            SurvivalCombatAdapter.LogCombatState(player, _emergencyTarget, true, distance, fired);
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
        }

        private static void TickHide(Character player, Camera camera)
        {
            Phase = SurvivalBotPhase.Hide;
            SurvivalCombatAdapter.CloseSurvivalScope(player);
            int exposure = CountExposure(player.transform.position, player);
            if (!_hasSafePoint || Time.time >= _nextSafePointAt ||
                XzDistance(player.transform.position, _safePoint) < 1.1f)
            {
                _safePoint = SelectSafetyPoint(player);
                _hasSafePoint = true;
                _nextSafePointAt = Time.time + SurvivalBotSettings.SafePointRefreshSeconds;
            }

            Vector3 move = SurvivalCombatAdapter.NavigateSurvival(player, _safePoint, true);
            if (move.sqrMagnitude <= 0.01f && IsRouteFailure())
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
                _nextSafePointAt = 0f;
            }

            StatusText = "躲避模式 | 初始 " + Math.Max(InitialPlayers, ParticipantIds.Count) +
                " | 存活 " + RemainingPlayers + " | 暴露 " + exposure +
                " | 路径 " + SurvivalCombatAdapter.LastPath;
        }

        private static void TickAttack(Character player, Camera camera)
        {
            Phase = SurvivalBotPhase.Attack;
            const string modeName = "攻击模式";
            _attackTarget = SelectNearestVisibleTarget(player, camera);
            if (_attackTarget == null)
            {
                SurvivalCombatAdapter.CloseSurvivalScope(player);
                Character searchTarget = SelectNearestTarget(player);
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

            _hasAttackPoint = false;
            bool strictLine;
            float distance;
            bool fired = SurvivalCombatAdapter.AttackSurvival(player, _attackTarget, camera, out strictLine, out distance);
            SurvivalCombatAdapter.LogCombatState(player, _attackTarget, strictLine, distance, fired);
            if (!strictLine)
            {
                SurvivalCombatAdapter.CloseSurvivalScope(player);
                _attackTarget = null;
                AutoBattleInput.ClearMovement();
                StatusText = modeName + " | 目标刚失去视线，重新搜敌 | 已关镜";
                return;
            }

            AutoBattleInput.ClearMovement();
            StatusText = modeName + " | 存活 " + RemainingPlayers + " | 目标 " + SafeName(_attackTarget) +
                " | 距离 " + distance.ToString("0.0") + " | 直线 " + strictLine + " | 开火 " + fired;
        }

        private static void TickAttackSearch(Character player, Camera camera, Character searchTarget)
        {
            if (!_hasAttackPoint || Time.time >= _nextAttackPointAt ||
                XzDistance(player.transform.position, _attackPoint) < 1.2f)
            {
                _attackPoint = SelectAttackPoint(player, searchTarget);
                _hasAttackPoint = true;
                _nextAttackPointAt = Time.time + 1.2f;
            }

            Vector3 move = SurvivalCombatAdapter.NavigateSurvival(player, _attackPoint, false);
            if (move.sqrMagnitude <= 0.01f && IsRouteFailure())
            {
                MarkCandidateFailed(_attackPoint);
                _hasAttackPoint = false;
                _nextAttackPointAt = 0f;
            }
            if (move.sqrMagnitude > 0.01f)
            {
                AutoBattleInput.SetMoveWorld(player, move, false);
                if (camera != null)
                    SurvivalCombatAdapter.LookSurvival(player, camera, player.transform.position + move * 8f + Vector3.up);
            }
            else
            {
                AutoBattleInput.ClearMovement();
            }
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
                AutoBattleInput.ClearAll();
                FileLogger.Log("SURVIVAL", "kill/assist complete; cliff suicide started");
            }

            if (!_hasCliff && Time.time >= _nextCliffScanAt)
            {
                _nextCliffScanAt = Time.time + 2f;
                _hasCliff = TryFindCliff(player, out _cliffEdge, out _cliffOutward);
            }

            if (Time.time - _suicideStartedAt >= SurvivalBotSettings.SuicideFallbackSeconds &&
                app != null && app.channel_connection != null)
            {
                app.channel_connection.Suicide(player.uid);
                _suicideStartedAt = Time.time + 3600f;
                AutoBattleInput.ClearAll();
                StatusText = "跳崖超时，已请求自杀兜底";
                FileLogger.Log("SURVIVAL", "cliff timeout; Suicide(uid) fallback sent");
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
                    _hasCliff = false;
                    _nextCliffScanAt = 0f;
                    AutoBattleInput.ClearMovement();
                    StatusText = "悬崖路径无进展，重新搜索";
                    return;
                }

                if (edgeDistance > 1.1f)
                {
                    Vector3 move = SurvivalCombatAdapter.NavigateSurvival(player, _cliffEdge, false);
                    if (move.sqrMagnitude <= 0.01f && IsRouteFailure())
                    {
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
            int count = player != null && !player.IsDied ? 1 : 0;
            for (int i = 0; i < Enemies.Count; i++)
            {
                Character ch = Enemies[i];
                if (ch == null || ch.IsDied) continue;
                if (_participantLocked && !ParticipantIds.Contains(ch.uid)) continue;
                count++;
            }
            return count;
        }

        private static Vector3 SelectSafetyPoint(Character player)
        {
            Vector3 origin = player.transform.position;
            Vector3 best = origin;
            float bestScore = ScoreSafetyPoint(origin, player, origin, 0f);
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
                    if (score < bestScore)
                    {
                        bestScore = score;
                        best = ground;
                    }
                }
            }
            return best;
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

        private static Vector3 SelectAttackPoint(Character player, Character target)
        {
            Vector3 playerPos = player.transform.position;
            Vector3 targetPos = target.transform.position;
            float currentDistance = XzDistance(playerPos, targetPos);
            if (currentDistance < 9f)
            {
                Vector3 away = playerPos - targetPos;
                away.y = 0f;
                if (away.sqrMagnitude < 0.01f) away = -target.transform.forward;
                away.Normalize();
                Vector3 retreat;
                if (TryProjectGround(playerPos + away * 7f, playerPos.y, 3f, out retreat) &&
                    !IsFailedCandidate(retreat) &&
                    AutoBattleRoutePlanner.CandidatePenalty(playerPos, retreat, player.transform.root) < 120f)
                    return retreat;
            }

            Vector3 best = playerPos;
            float bestScore = float.MaxValue;
            for (int i = 0; i < 24; i++)
            {
                float angle = i * 15f;
                Vector3 dir = Quaternion.AngleAxis(angle, Vector3.up) * Vector3.forward;
                Vector3 point;
                float attackRadius = SurvivalBotSettings.AttackStandOffDistance;
                if (!TryProjectGround(targetPos + dir * attackRadius, playerPos.y, 4f, out point)) continue;
                if (IsFailedCandidate(point)) continue;
                float routePenalty = AutoBattleRoutePlanner.CandidatePenalty(playerPos, point, player.transform.root);
                if (routePenalty >= 120f) continue;
                bool clearLane = !HasMapBlock(point + Vector3.up * 1.2f, targetPos + Vector3.up * 1.1f);
                float score = routePenalty + XzDistance(playerPos, point) + (clearLane ? -80f : 120f);
                if (score < bestScore)
                {
                    bestScore = score;
                    best = point;
                }
            }
            return best;
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
            try { return !target.GetHidden(); }
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

                    if (lastSafeDistance < 3f || !IsFatalDrop(lastGround, dir)) break;
                    Vector3 candidate = lastGround - dir * 0.8f;
                    if (IsFailedCandidate(candidate)) break;
                    float routePenalty = AutoBattleRoutePlanner.CandidatePenalty(origin, candidate, player.transform.root);
                    if (routePenalty < 120f && lastSafeDistance + routePenalty * 0.03f < bestDistance)
                    {
                        bestDistance = lastSafeDistance + routePenalty * 0.03f;
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

        private static bool IsRouteFailure()
        {
            return string.Equals(SurvivalCombatAdapter.LastPath, "no_path", StringComparison.Ordinal) ||
                   string.Equals(SurvivalCombatAdapter.LastPath, "route_null", StringComparison.Ordinal) ||
                   string.Equals(SurvivalCombatAdapter.LastPath, "jump_lane_blocked", StringComparison.Ordinal) ||
                   string.Equals(SurvivalCombatAdapter.LastPath, "wall_repath", StringComparison.Ordinal);
        }

        private static void MarkCandidateFailed(Vector3 point)
        {
            _failedCandidate = point;
            _failedCandidateUntil = Time.time + 8f;
        }

        private static bool IsFailedCandidate(Vector3 point)
        {
            return Time.time < _failedCandidateUntil && XzDistance(point, _failedCandidate) < 3f;
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
