using System;
using System.Collections.Generic;
using System.Reflection;
using ASWDEBUG.Cheats.AutoBattle;
using ASWDEBUG.Logger;
using UnityEngine;

namespace ASWDEBUG.Cheats.SurvivalBot
{
    public enum SurvivalBotPhase
    {
        Lobby,
        Matching,
        CaptureParticipants,
        Hide,
        Attack,
        Suicide,
        Balance,
        GmExit,
        Stopped
    }

    public static class SurvivalBotManager
    {
        private const float MatchTimeoutSeconds = 600f;
        private const float ParticipantCaptureSeconds = 5f;
        private const float SafePointRefreshSeconds = 1.35f;
        private const float DesiredSeparation = 13f;
        private const float SuicideFallbackSeconds = 25f;

        private static readonly List<Character> Enemies = new List<Character>(16);
        private static readonly HashSet<int> ParticipantIds = new HashSet<int>();
        private static readonly float[] SafeRadii = { 5f, 9f, 13f };

        private static bool _roundActive;
        private static bool _participantLocked;
        private static bool _taskCompleted;
        private static bool _previousRoundSlept;
        private static bool _matching;
        private static bool _cancelPending;
        private static bool _gmHandledThisRound;
        private static bool _roundEndedByGm;
        private static int _consecutiveGmRounds;
        private static int _baselineKills;
        private static int _baselineAssists;
        private static float _roundStartedAt;
        private static float _matchStartedAt;
        private static float _nextMatchAt;
        private static float _cancelRequestedAt;
        private static float _nextPunishRefreshAt;
        private static float _nextSafePointAt;
        private static float _nextAttackPointAt;
        private static float _suicideStartedAt;
        private static float _nextCliffScanAt;
        private static Vector3 _safePoint;
        private static Vector3 _attackPoint;
        private static Vector3 _cliffEdge;
        private static Vector3 _cliffOutward;
        private static bool _hasSafePoint;
        private static bool _hasAttackPoint;
        private static bool _hasCliff;
        private static Character _attackTarget;
        private static UITakeCardManager _cardManager;
        private static int _cardCount;
        private static float _nextCardActionAt;
        private static bool _cardCloseScheduled;
        private static byte _pendingGmUid;
        private static byte _pendingGmTeam;

        public static bool Enabled = true;
        public static SurvivalBotPhase Phase = SurvivalBotPhase.Lobby;
        public static string StatusText = "等待初始化";
        public static int InitialPlayers { get; private set; }
        public static int RemainingPlayers { get; private set; }
        public static int LastFinalRank { get; private set; }

        public static void Tick(Level level, Character player, Camera camera)
        {
            AutoBattleInput.BeginFrame();

            if (Input.GetKeyDown(KeyCode.F8))
            {
                Enabled = !Enabled;
                if (!Enabled) Stop("manual_stop");
                else
                {
                    Phase = SurvivalBotPhase.Lobby;
                    StatusText = "机器人已启动";
                    _nextMatchAt = Time.time + 1f;
                }
            }

            if (!Enabled)
            {
                AutoBattleInput.ClearAll();
                Phase = SurvivalBotPhase.Stopped;
                return;
            }

            TickCards();

            GameApp app = GameApp.Instance;
            bool inSurvival = IsInSurvivalGame(app) && player != null;
            if (inSurvival)
            {
                _matching = false;
                _cancelPending = false;
                if (!_roundActive) StartRound(level, player);
                TickRound(app, level, player, camera);
                return;
            }

            if (_roundActive) FinishRound();
            TickLobby(app);
        }

        public static void Stop(string reason)
        {
            Enabled = false;
            Phase = SurvivalBotPhase.Stopped;
            StatusText = "已停止: " + reason;
            AutoBattleInput.ClearAll();
            AutoBattleManager.ResetSurvivalRuntime(reason);
            FileLogger.Log("SURVIVAL", StatusText);
        }

        public static void NotifyRemoteGmCandidate(byte uid, byte team)
        {
            if (!Enabled) return;
            _pendingGmUid = uid;
            _pendingGmTeam = team;
            FileLogger.Log("GM", "remote GM/viewer candidate uid=" + uid + " team=" + team);
        }

        public static void NotifyFinalRank(byte rank)
        {
            LastFinalRank = rank;
            FileLogger.Log("SURVIVAL", "final rank=" + rank + " initial=" + InitialPlayers +
                " topHalf=" + (InitialPlayers > 0 && rank <= InitialPlayers / 2));
        }

        public static void NotifyMatchingAccepted()
        {
            _matching = true;
            _cancelPending = false;
            if (_matchStartedAt <= 0f) _matchStartedAt = Time.time;
            Phase = SurvivalBotPhase.Matching;
        }

        public static void NotifyMatchingCancelled()
        {
            _matching = false;
            _cancelPending = false;
            _matchStartedAt = 0f;
            _nextMatchAt = Time.time + 1.5f;
            FileLogger.Log("MATCH", "matching cancelled; retry armed");
        }

        public static void NotifyCardRefresh(UITakeCardManager manager)
        {
            if (!Enabled || manager == null) return;
            _cardManager = manager;
            _cardCount = ReadPrivateInt(manager, "cardCount");
            _nextCardActionAt = Time.time + 0.8f;
            _cardCloseScheduled = false;
            Phase = SurvivalBotPhase.Balance;
            StatusText = _cardCount <= 0 ? "结算无可翻牌奖励" : "结算翻牌 0/" + _cardCount;
            FileLogger.Log("CARD", "refresh cardCount=" + _cardCount);
        }

        private static void StartRound(Level level, Character player)
        {
            _roundActive = true;
            _participantLocked = false;
            _taskCompleted = false;
            _gmHandledThisRound = false;
            _roundEndedByGm = false;
            _roundStartedAt = Time.time;
            _baselineKills = player.num_killed;
            _baselineAssists = player.holding_attack_count;
            InitialPlayers = 0;
            RemainingPlayers = 0;
            ParticipantIds.Clear();
            _hasSafePoint = false;
            _hasAttackPoint = false;
            _hasCliff = false;
            _attackTarget = null;
            _nextSafePointAt = 0f;
            _nextAttackPointAt = 0f;
            AutoBattleManager.ResetSurvivalRuntime("round_start");
            CaptureParticipants(level, player);
            Phase = SurvivalBotPhase.CaptureParticipants;
            StatusText = "进入生存对局，采集初始人数";
            FileLogger.Log("SURVIVAL", "round start kills=" + _baselineKills + " assists=" + _baselineAssists);
        }

        private static void FinishRound()
        {
            if (!_roundEndedByGm) _consecutiveGmRounds = 0;
            _previousRoundSlept = !_taskCompleted && !_roundEndedByGm;
            FileLogger.Log("SURVIVAL", "round finish task=" + _taskCompleted + " gm=" + _roundEndedByGm +
                " slept=" + _previousRoundSlept + " rank=" + LastFinalRank);
            _roundActive = false;
            _pendingGmUid = 0;
            _pendingGmTeam = 0;
            AutoBattleInput.ClearAll();
            AutoBattleManager.ResetSurvivalRuntime("round_finish");
            Phase = SurvivalBotPhase.Balance;
            StatusText = "等待结算/返回大厅";
            _nextMatchAt = Time.time + 5f;
        }

        private static void TickRound(GameApp app, Level level, Character player, Camera camera)
        {
            AutoBattleManager.MarkSurvivalActivity(player);

            if (_pendingGmTeam >= 2 && !_gmHandledThisRound)
            {
                HandleGmExit(app);
                return;
            }

            CaptureParticipants(level, player);
            RefreshEnemies(level, player);
            RemainingPlayers = CountRemaining(player);

            if (!_participantLocked && Time.time - _roundStartedAt >= ParticipantCaptureSeconds)
            {
                _participantLocked = true;
                InitialPlayers = Math.Max(InitialPlayers, ParticipantIds.Count);
                FileLogger.Log("SURVIVAL", "participants locked initial=" + InitialPlayers);
            }

            if (player.num_killed > _baselineKills || player.holding_attack_count > _baselineAssists)
                _taskCompleted = true;

            if (player.IsDied)
            {
                AutoBattleInput.ClearAll();
                Phase = SurvivalBotPhase.Balance;
                StatusText = "角色已死亡，等待结算";
                return;
            }

            if (_taskCompleted)
            {
                TickSuicide(app, player, camera);
                return;
            }

            int initial = Math.Max(InitialPlayers, ParticipantIds.Count);
            int threshold = Math.Max(1, initial / 2);
            if (!_participantLocked || RemainingPlayers > threshold)
                TickHide(player, camera);
            else
                TickAttack(player, camera);
        }

        private static void TickHide(Character player, Camera camera)
        {
            Phase = SurvivalBotPhase.Hide;
            int exposure = CountExposure(player.transform.position, player);
            if (exposure > 0)
            {
                AutoBattleManager.TryUseSurvivalDefense(player);
                _nextSafePointAt = 0f;
            }

            if (!_hasSafePoint || Time.time >= _nextSafePointAt ||
                XzDistance(player.transform.position, _safePoint) < 1.1f)
            {
                _safePoint = SelectSafetyPoint(player);
                _hasSafePoint = true;
                _nextSafePointAt = Time.time + SafePointRefreshSeconds;
            }

            Vector3 move = AutoBattleManager.NavigateSurvival(player, _safePoint, true);
            if (move.sqrMagnitude > 0.01f) AutoBattleInput.SetMoveWorld(player, move, false);
            else AutoBattleInput.ClearMovement();

            if (camera != null && move.sqrMagnitude > 0.01f)
                AutoBattleManager.LookSurvival(player, camera, player.transform.position + move * 8f + Vector3.up);

            StatusText = "躲避模式 | 初始 " + Math.Max(InitialPlayers, ParticipantIds.Count) +
                " | 存活 " + RemainingPlayers + " | 暴露 " + exposure +
                " | 路径 " + AutoBattleManager.LastPath;
        }

        private static void TickAttack(Character player, Camera camera)
        {
            Phase = SurvivalBotPhase.Attack;
            if (!IsAttackTargetUsable(_attackTarget)) _attackTarget = SelectNearestTarget(player);
            if (_attackTarget == null)
            {
                AutoBattleInput.ClearMovement();
                StatusText = "攻击模式 | 等待可见且未隐身目标";
                return;
            }

            bool strictLine;
            float distance;
            bool fired = AutoBattleManager.AttackSurvival(player, _attackTarget, camera, out strictLine, out distance);
            if (strictLine)
            {
                AutoBattleInput.ClearMovement();
            }
            else
            {
                if (!_hasAttackPoint || Time.time >= _nextAttackPointAt ||
                    XzDistance(player.transform.position, _attackPoint) < 1.2f)
                {
                    _attackPoint = SelectAttackPoint(player, _attackTarget);
                    _hasAttackPoint = true;
                    _nextAttackPointAt = Time.time + 1.2f;
                }

                Vector3 move = AutoBattleManager.NavigateSurvival(player, _attackPoint, false);
                if (move.sqrMagnitude > 0.01f) AutoBattleInput.SetMoveWorld(player, move, false);
                else AutoBattleInput.ClearMovement();
                if (camera != null)
                    AutoBattleManager.LookSurvival(player, camera, _attackTarget.transform.position + Vector3.up);
            }

            StatusText = "攻击模式 | 存活 " + RemainingPlayers + " | 目标 " + SafeName(_attackTarget) +
                " | 距离 " + distance.ToString("0.0") + " | 直线 " + strictLine + " | 开火 " + fired;
        }

        private static void TickSuicide(GameApp app, Character player, Camera camera)
        {
            if (Phase != SurvivalBotPhase.Suicide)
            {
                Phase = SurvivalBotPhase.Suicide;
                _suicideStartedAt = Time.time;
                _nextCliffScanAt = 0f;
                _hasCliff = false;
                AutoBattleInput.ClearAll();
                FileLogger.Log("SURVIVAL", "kill/assist complete; cliff suicide started");
            }

            if (!_hasCliff && Time.time >= _nextCliffScanAt)
            {
                _nextCliffScanAt = Time.time + 2f;
                _hasCliff = TryFindCliff(player, out _cliffEdge, out _cliffOutward);
            }

            if (_hasCliff)
            {
                float edgeDistance = XzDistance(player.transform.position, _cliffEdge);
                if (edgeDistance > 1.1f)
                {
                    Vector3 move = AutoBattleManager.NavigateSurvival(player, _cliffEdge, false);
                    if (move.sqrMagnitude > 0.01f) AutoBattleInput.SetMoveWorld(player, move, false);
                    if (camera != null)
                        AutoBattleManager.LookSurvival(player, camera, player.transform.position + _cliffOutward * 8f);
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
            if (Time.time - _suicideStartedAt >= SuicideFallbackSeconds && app != null && app.channel_connection != null)
            {
                app.channel_connection.Suicide(player.uid);
                _suicideStartedAt = Time.time + 3600f;
                StatusText = "地图无可达悬崖，已请求自杀兜底";
                FileLogger.Log("SURVIVAL", "cliff timeout; Suicide(uid) fallback sent");
            }
        }

        private static void HandleGmExit(GameApp app)
        {
            _gmHandledThisRound = true;
            _roundEndedByGm = true;
            _consecutiveGmRounds++;
            Phase = SurvivalBotPhase.GmExit;
            AutoBattleInput.ClearAll();
            StatusText = "检测到 GM/观战候选，正在退出 | 连续 " + _consecutiveGmRounds + "/3";
            FileLogger.Log("GM", StatusText + " uid=" + _pendingGmUid + " team=" + _pendingGmTeam);

            if (app != null && app.channel_connection != null)
            {
                try { app.channel_connection.LeaveGame(); }
                catch (Exception ex) { FileLogger.Log("GM", "LeaveGame failed: " + ex.Message); }
            }

            if (_consecutiveGmRounds >= 3)
                Stop("three_consecutive_gm_rounds");
        }

        private static void TickLobby(GameApp app)
        {
            if (_cardManager != null)
            {
                Phase = SurvivalBotPhase.Balance;
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
                StatusText = "匹配生存模式 " + elapsed.ToString("0") + "/600 秒";
                if (elapsed >= MatchTimeoutSeconds && !_cancelPending)
                {
                    _cancelPending = true;
                    _cancelRequestedAt = Time.time;
                    try { app.lobby_connection.RequestCancelMatching(); } catch { }
                    FileLogger.Log("MATCH", "600 second timeout; cancel requested");
                }
                else if (_cancelPending && Time.time - _cancelRequestedAt > 6f)
                {
                    NotifyMatchingCancelled();
                }
                return;
            }

            if (Time.time < _nextMatchAt)
            {
                Phase = SurvivalBotPhase.Lobby;
                StatusText = "等待重新匹配";
                return;
            }

            if (app.lobby_connection.state != LobbyConnection.State.kInChannel)
            {
                Phase = SurvivalBotPhase.Lobby;
                StatusText = "等待返回频道大厅";
                return;
            }

            try
            {
                app.lobby_connection.RequestMatching((byte)RoomInfo.GameType.kGameTypeChiji, 0);
                _matching = true;
                _matchStartedAt = Time.time;
                Phase = SurvivalBotPhase.Matching;
                StatusText = _previousRoundSlept ? "睡眠局后跳过本地验证码，匹配生存" : "开始匹配生存模式";
                FileLogger.Log("MATCH", StatusText + " gameType=" + (byte)RoomInfo.GameType.kGameTypeChiji);
            }
            catch (Exception ex)
            {
                _nextMatchAt = Time.time + 5f;
                StatusText = "匹配请求失败: " + ex.Message;
            }
        }

        private static void TickCards()
        {
            if (_cardManager == null || Time.time < _nextCardActionAt) return;
            try
            {
                if (_cardCount <= 0 || _cardManager.window == null || _cardManager.window.cards == null)
                {
                    _cardManager = null;
                    _nextMatchAt = Time.time + 3f;
                    return;
                }

                int chosen = ReadPrivateInt(_cardManager, "chooseCardCount");
                if (chosen < _cardCount)
                {
                    for (int i = 0; i < _cardManager.window.cards.Count; i++)
                    {
                        CardBehaviour card = _cardManager.window.cards[i];
                        if (card == null || card.IsTrun) continue;
                        _cardManager.CardsRefresh(card.gameObject);
                        chosen++;
                        StatusText = "结算翻牌 " + chosen + "/" + _cardCount;
                        _nextCardActionAt = Time.time + 0.45f;
                        return;
                    }
                }

                if (!_cardCloseScheduled)
                {
                    _cardCloseScheduled = true;
                    _cardManager.window.StopCountdown();
                    _cardManager.window.FinishHideView();
                    StatusText = "翻牌完成，返回大厅";
                    _nextMatchAt = Time.time + 5f;
                }
                _cardManager = null;
            }
            catch (Exception ex)
            {
                FileLogger.Log("CARD", "auto flip failed: " + ex.Message);
                _cardManager = null;
                _nextMatchAt = Time.time + 5f;
            }
        }

        private static void CaptureParticipants(Level level, Character player)
        {
            if (_participantLocked) return;
            if (player != null) ParticipantIds.Add(player.uid);
            RefreshEnemies(level, player);
            for (int i = 0; i < Enemies.Count; i++) ParticipantIds.Add(Enemies[i].uid);
            InitialPlayers = Math.Max(InitialPlayers, ParticipantIds.Count);
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
                    if (!Enemies.Contains(ch)) Enemies.Add(ch);
                }
            }
            catch { }
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
                    float routePenalty = AutoBattleRoutePlanner.CandidatePenalty(origin, ground, player.transform.root);
                    if (routePenalty >= 500f) continue;
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
                if (!IsAttackTargetUsable(enemy)) continue;
                float distance = XzDistance(point, enemy.transform.position);
                if (distance < minDistance) minDistance = distance;
                bool blocked = HasMapBlock(enemy.transform.position + Vector3.up * 1.25f, point + Vector3.up * 1.05f);
                if (blocked) covered++;
                else if (IsEnemyFacingPoint(enemy, point, 0.22f)) exposed++;
            }

            float separationPenalty = Mathf.Max(0f, DesiredSeparation - minDistance) * 55f;
            return exposed * 1200f + separationPenalty + routePenalty + XzDistance(point, origin) * 0.7f - covered * 12f;
        }

        private static int CountExposure(Vector3 point, Character player)
        {
            int count = 0;
            for (int i = 0; i < Enemies.Count; i++)
            {
                Character enemy = Enemies[i];
                if (!IsAttackTargetUsable(enemy)) continue;
                if (!IsEnemyFacingPoint(enemy, point, 0.22f)) continue;
                if (!HasMapBlock(enemy.transform.position + Vector3.up * 1.25f, point + Vector3.up * 1.05f)) count++;
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
                if (TryProjectGround(playerPos + away * 7f, playerPos.y, 3f, out retreat)) return retreat;
            }

            Vector3 best = playerPos;
            float bestScore = float.MaxValue;
            for (int i = 0; i < 24; i++)
            {
                float angle = i * 15f;
                Vector3 dir = Quaternion.AngleAxis(angle, Vector3.up) * Vector3.forward;
                Vector3 point;
                if (!TryProjectGround(targetPos + dir * 16f, playerPos.y, 4f, out point)) continue;
                float routePenalty = AutoBattleRoutePlanner.CandidatePenalty(playerPos, point, player.transform.root);
                if (routePenalty >= 500f) continue;
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

        private static bool IsAttackTargetUsable(Character target)
        {
            if (target == null || target.transform == null || target.IsDied || target.Is_Viewer) return false;
            try { return !target.GetHidden(); }
            catch { return false; }
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

                    if (lastSafeDistance < 3f) break;
                    Vector3 candidate = lastGround - dir * 0.8f;
                    float routePenalty = AutoBattleRoutePlanner.CandidatePenalty(origin, candidate, player.transform.root);
                    if (routePenalty < 500f && lastSafeDistance + routePenalty * 0.03f < bestDistance)
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
