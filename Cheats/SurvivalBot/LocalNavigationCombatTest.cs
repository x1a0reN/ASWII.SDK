using ASWDEBUG.Cheats.AutoBattle;
using ASWDEBUG.Logger;
using RAIN.Navigation.Graph;
using RAIN.Navigation.NavMesh;
using RAIN.Navigation.Pathfinding;
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using UnityEngine;

namespace ASWDEBUG.Cheats.SurvivalBot
{
    internal enum LocalNavigationTestState
    {
        Idle,
        Loading,
        WaitingCache,
        PreparingActors,
        WaitingNavigation,
        Running,
        Returning,
        Failed
    }

    internal static class LocalNavigationCombatTest
    {
        private const string PhysicalMapName = "level33";
        private const int DesiredBotCount = 4;
        private const int ActorHealth = 500;
        private const int LocalShotDamage = 650;
        private const float LoadTimeoutSeconds = 55f;
        private const float RespawnDelaySeconds = 1.5f;
        private static readonly MethodInfo MemberwiseCloneMethod = typeof(object).GetMethod(
            "MemberwiseClone", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly List<LocalBotEntry> Bots = new List<LocalBotEntry>(DesiredBotCount);
        private static CharacterInfoData _capturedProfile;
        private static bool _enabled;
        private static bool _ownsDirectScene;
        private static bool _actorsPrepared;
        private static float _startedAt;
        private static float _nextStatusAt;
        private static int _spawnSequence;

        private sealed class LocalBotEntry
        {
            internal Character Character;
            internal float RespawnAt;
        }

        internal static bool Enabled
        {
            get { return _enabled; }
        }

        internal static bool Running
        {
            get { return _enabled && State == LocalNavigationTestState.Running; }
        }

        internal static LocalNavigationTestState State { get; private set; }
        internal static string StatusText { get; private set; }

        internal static int AliveBotCount
        {
            get
            {
                int alive = 0;
                for (int i = 0; i < Bots.Count; i++)
                {
                    Character bot = Bots[i] == null ? null : Bots[i].Character;
                    if (bot != null && !bot.IsDied) alive++;
                }
                return alive;
            }
        }

        internal static int BotCount
        {
            get { return Bots.Count; }
        }

        internal static bool InterceptShots
        {
            get { return _ownsDirectScene && MapBakeSceneLoader.DirectSceneActive; }
        }

        static LocalNavigationCombatTest()
        {
            State = LocalNavigationTestState.Idle;
            StatusText = "level33 本地测试未启动";
        }

        internal static bool RequestStart(out string detail)
        {
            detail = string.Empty;
            if (_enabled)
            {
                detail = "level33 本地测试已在运行";
                return true;
            }

            string cachePath = RuntimeRainNavDiskCache.GetCachePath(PhysicalMapName);
            if (!File.Exists(cachePath))
            {
                detail = "未找到 level33.rainnav，请先完成该地图建图";
                StatusText = detail;
                State = LocalNavigationTestState.Failed;
                return false;
            }

            CaptureCurrentProfile();
            if (!MapBakeSceneLoader.RequestPhysicalScene(
                PhysicalMapName, RoomInfo.GameType.kGameTypeChiji, out detail))
            {
                StatusText = detail;
                State = LocalNavigationTestState.Failed;
                return false;
            }

            _enabled = true;
            _ownsDirectScene = true;
            _actorsPrepared = false;
            _startedAt = Time.realtimeSinceStartup;
            _nextStatusAt = 0f;
            State = LocalNavigationTestState.Loading;
            StatusText = "正在加载生存模式 level33";
            SurvivalBotSettings.SetEnemyEspEnabled(true);
            AutoBattleManager.SetEnabled(false, "level33_test_loading");
            FileLogger.Log("AUTO-BATTLE][LEVEL33-TEST", "requested cache=" + cachePath);
            return true;
        }

        internal static void Tick(Level level, Character player, Camera camera)
        {
            if (!_enabled) return;
            MapBakeSceneLoader.Tick();

            float now = Time.realtimeSinceStartup;
            if (now - _startedAt > LoadTimeoutSeconds && State != LocalNavigationTestState.Running)
            {
                FailAndReturn("level33 测试加载超时");
                return;
            }

            if (!MapBakeSceneLoader.DirectSceneActive)
            {
                if (!MapBakeSceneLoader.IsTransitioning && now - _startedAt > 4f)
                    FailAndReturn("level33 直接场景未能启动");
                return;
            }

            if (level == null || level.state != Level.State.kReady || player == null || player.transform == null)
            {
                State = LocalNavigationTestState.Loading;
                StatusText = "level33 场景加载中";
                return;
            }
            if (!string.Equals(level.map_name, PhysicalMapName, StringComparison.OrdinalIgnoreCase) ||
                !MapBakeSceneLoader.IsExpectedDirectScene(PhysicalMapName))
            {
                FailAndReturn("地图解析不一致，预期 level33，实际 " + (level.map_name ?? "-"));
                return;
            }

            RuntimeRainNavSnapshot snapshot = RuntimeRainNavMesh.GetStatusSnapshot();
            State = LocalNavigationTestState.WaitingCache;
            if (!string.Equals(snapshot.MapName, PhysicalMapName, StringComparison.OrdinalIgnoreCase))
            {
                FailAndReturn("导航钩子未准备 level33 缓存，实际 " + (snapshot.MapName ?? "-"));
                return;
            }
            if (snapshot.State == RuntimeRainNavState.Building)
            {
                AutoBattleRoutePlanner.DeactivateNavigation("level33_test_cache_miss");
                FailAndReturn("level33.rainnav 未命中，已禁止现场重新建图");
                return;
            }
            if (snapshot.State == RuntimeRainNavState.Failed)
            {
                FailAndReturn("level33.rainnav 加载失败: " + snapshot.Detail);
                return;
            }
            if (!string.Equals(snapshot.CacheSource, "disk", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(snapshot.CacheSource, "memory", StringComparison.OrdinalIgnoreCase))
            {
                if (now - _startedAt > 3f)
                {
                    AutoBattleRoutePlanner.DeactivateNavigation("level33_test_cache_not_reused");
                    FailAndReturn("level33.rainnav 未通过缓存校验: " + snapshot.CacheStatus);
                }
                else
                {
                    StatusText = "正在校验 level33.rainnav";
                }
                return;
            }
            if (!RuntimeRainNavMesh.IsReady)
            {
                StatusText = "正在从" + CacheSourceName(snapshot.CacheSource) + "挂载 level33.rainnav";
                return;
            }

            if (!_actorsPrepared)
            {
                State = LocalNavigationTestState.PreparingActors;
                string actorError;
                if (!PrepareActors(level, player, out actorError))
                {
                    FailAndReturn("本地角色初始化失败: " + actorError);
                    return;
                }
                _actorsPrepared = true;
                AutoBattleManager.SetEnabled(true, "level33_test_ready");
                State = LocalNavigationTestState.WaitingNavigation;
                StatusText = "Bot 已生成，等待玩家位置接入 RAIN 导航";
                return;
            }

            if (!AutoBattleRoutePlanner.IsGameNavigationReady)
            {
                FreezeAllBots();
                State = LocalNavigationTestState.WaitingNavigation;
                StatusText = "等待 RAIN 路径查询就绪 | Bot " + AliveBotCount + "/" + Bots.Count;
                return;
            }

            State = LocalNavigationTestState.Running;
            TickBotRespawns(level, player);
            AutoBattleManager.Tick(level, player, camera);
            if (now >= _nextStatusAt)
            {
                _nextStatusAt = now + 0.25f;
                StatusText = "level33 实战测试 | Bot " + AliveBotCount + "/" + Bots.Count +
                    " | " + CacheSourceName(snapshot.CacheSource) + " " + snapshot.GraphSize + " 节点" +
                    " | 路径 " + AutoBattleManager.LastPathProvider;
            }
        }

        internal static void Stop(string reason, bool returnToLobby)
        {
            bool wasEnabled = _enabled;
            _enabled = false;
            _actorsPrepared = false;
            AutoBattleManager.SetEnabled(false, "level33_test_stop:" + reason);
            AutoBattleInput.ClearAll();
            DestroyBots();
            if (returnToLobby && MapBakeSceneLoader.DirectSceneActive)
            {
                string detail;
                MapBakeSceneLoader.TryReturnToLobby(out detail);
                State = LocalNavigationTestState.Returning;
                StatusText = string.IsNullOrEmpty(detail) ? "正在返回大厅" : detail;
            }
            else
            {
                _ownsDirectScene = false;
                MapBakeSceneLoader.CancelPending("level33_test_stop:" + reason);
                State = LocalNavigationTestState.Idle;
                StatusText = wasEnabled ? "level33 本地测试已关闭" : StatusText;
            }
            FileLogger.Log("AUTO-BATTLE][LEVEL33-TEST", "stopped reason=" + reason +
                " return=" + (returnToLobby ? "1" : "0"));
        }

        internal static void NotifyLevelExit()
        {
            if (!_enabled && !_ownsDirectScene && Bots.Count == 0) return;
            _enabled = false;
            _ownsDirectScene = false;
            _actorsPrepared = false;
            Bots.Clear();
            AutoBattleManager.SetEnabled(false, "level33_test_level_exit");
            AutoBattleInput.ClearAll();
            State = LocalNavigationTestState.Idle;
            StatusText = "level33 本地场景已退出";
        }

        internal static bool TryHandleLocalShot(HitMessage message)
        {
            if (!Running || message == null || message.part > 18) return false;
            Character target = FindBotByUid(message.uid);
            if (target == null || target.IsDied) return false;

            try
            {
                Character player = CurrentPlayer();
                int oldHp = Mathf.Max(0, target.hp);
                int oldShield = Mathf.Max(0, (int)target.shield);
                int shieldDamage = Mathf.Min(oldShield, LocalShotDamage);
                int hpDamage = Mathf.Min(oldHp, Mathf.Max(0, LocalShotDamage - shieldDamage));
                int newShield = oldShield - shieldDamage;
                int newHp = oldHp - hpDamage;
                HitInfo hit = new HitInfo();
                hit.hp = newHp;
                hit.shield = newShield;
                hit.type = 2;
                hit.shot_state = 0;
                hit.time = 1f;
                hit.from_uid = player == null ? (byte)0 : player.uid;
                hit.to_uid = target.uid;
                byte hitPart = (byte)Mathf.Clamp((int)message.part, 0, 18);
                hit.part = hitPart;
                hit.pos = player == null ? Vector3.zero : player.transform.position;
                Vector3 direction = player == null
                    ? Vector3.forward
                    : target.transform.position - player.transform.position;
                hit.hitDir = direction.sqrMagnitude > 0.0001f ? direction.normalized : Vector3.forward;

                target.HealthChange(hit);
                if (newHp <= 0 && !target.IsDied)
                {
                    target.Die(hit);
                    try { if (player != null) player.num_killed++; } catch { }
                }
                FileLogger.Log("AUTO-BATTLE][LEVEL33-TEST", "local_hit target=" + target.uid +
                    " hp=" + newHp + " shield=" + newShield + " part=" + hitPart +
                    " killed=" + (target.IsDied ? "1" : "0"));
                return true;
            }
            catch (Exception ex)
            {
                FileLogger.Log("AUTO-BATTLE][LEVEL33-TEST", "local_hit_failed target=" + target.uid +
                    " ex=" + ex.GetType().Name + ":" + Safe(ex.Message));
                return false;
            }
        }

        private static bool PrepareActors(Level level, Character player, out string error)
        {
            error = string.Empty;
            RAINNavigationGraph graph = RuntimeRainNavMesh.OwnedGraph;
            if (graph == null || graph.Size <= 0)
            {
                error = "RAIN 图为空";
                return false;
            }

            Vector3 playerSpawn;
            if (!TrySampleGraphPoint(graph, Vector3.zero, 0f, float.MaxValue, null, out playerSpawn))
            {
                error = "无法抽取玩家导航出生点";
                return false;
            }
            if (!InitializePlayer(player, playerSpawn, out error)) return false;

            DestroyBots();
            for (int i = 0; i < DesiredBotCount; i++)
            {
                Vector3 botSpawn;
                if (!TrySampleGraphPoint(graph, playerSpawn, 16f, 95f, Bots, out botSpawn))
                {
                    error = "只能生成 " + Bots.Count + "/" + DesiredBotCount + " 个导航 Bot";
                    return false;
                }
                LocalBotEntry entry;
                if (!TryCreateBot(level, player, botSpawn, out entry, out error)) return false;
                Bots.Add(entry);
            }

            FileLogger.Log("AUTO-BATTLE][LEVEL33-TEST", "actors_ready player=" + FormatVector(playerSpawn) +
                " bots=" + Bots.Count + " graph=" + graph.Size);
            return true;
        }

        private static bool InitializePlayer(Character player, Vector3 spawn, out string error)
        {
            error = string.Empty;
            try
            {
                if (player.uid == 0) player.uid = 1;
                player.baseName = "BaseBody" + player.uid;
                player.hpName = "BaseBodyHP" + player.uid;
                player.hpBGName = "BaseBodyHPBG" + player.uid;
                player.hpLabelName = "BaseBodyHPLabel" + player.uid;
                player.gameObject.name = player.baseName;
                player.SetTeam(0);
                CharacterInfoData info = BuildProfile(_capturedProfile, player.uid, 0, "RAIN 测试员");
                player.SetCharacterInfo(info);
                player.InitBuff();
                player.ready = true;
                player.connected = true;
                player.playing = true;
                player.can_select = true;
                player.max_health = ActorHealth;
                player.primary_max_health = ActorHealth;
                player.InitializeObjectBase();
                player.Rebirth(ActorHealth, 0, spawn, Quaternion.identity);
                player.transform.position = spawn;
                player.invincible_time = 0f;
                player.ActivateObjects();
                if (player.weaponlist != null && player.weaponlist.Count > 0)
                    player.ChangeWeapon(player.weaponlist[0]);
                return true;
            }
            catch (Exception ex)
            {
                error = ex.GetType().Name + ":" + Safe(ex.Message);
                return false;
            }
        }

        private static bool TryCreateBot(Level level, Character player, Vector3 spawn,
            out LocalBotEntry entry, out string error)
        {
            entry = null;
            error = string.Empty;
            GameObject gameObject = null;
            Character bot = null;
            try
            {
                byte uid;
                if (!TryAllocateBotUid(level, player, out uid))
                {
                    error = "没有可用的本地 Bot UID";
                    return false;
                }
                gameObject = GameApp.Instance.getBaseMode(false, CharacterControlType.Robot);
                if (gameObject == null)
                {
                    error = "Robot Prefab 创建失败";
                    return false;
                }
                gameObject.SetActive(false);
                bot = gameObject.GetComponent<Character>();
                if (bot == null)
                {
                    UnityEngine.Object.Destroy(gameObject);
                    error = "Robot Prefab 缺少 Character";
                    return false;
                }

                int sequence = ++_spawnSequence;
                bot.uid = uid;
                bot.robot_uid = 910000 + sequence;
                bot.baseName = "BaseBody" + uid;
                bot.hpName = "BaseBodyHP" + uid;
                bot.hpBGName = "BaseBodyHPBG" + uid;
                bot.hpLabelName = "BaseBodyHPLabel" + uid;
                gameObject.name = bot.baseName;
                bot.SetTeam(1);
                bot.SetCharacterInfo(BuildProfile(_capturedProfile, uid, 1, "RAIN Bot-" + sequence.ToString("00")));
                bot.InitBuff();
                bot.SetPhysxControl(false);
                bot.transform.position = new Vector3(-10000f, -10000f, -10000f);
                CharacterManager.Instance.AddCharacter(bot);
                try { RobotManager.Instance.RemoveRobot(bot); } catch { }
                bot.ready = true;
                bot.connected = true;
                bot.playing = true;
                bot.can_select = true;
                bot.max_health = ActorHealth;
                bot.primary_max_health = ActorHealth;
                bot.InitializeObjectBase();
                Quaternion rotation = FaceTowards(spawn, player.transform.position);
                bot.Rebirth(ActorHealth, 0, spawn, rotation);
                bot.transform.rotation = rotation;
                bot.invincible_time = 0f;
                bot.can_select = true;
                bot.ActivateObjects();
                FreezeRobot(bot as RobotControl);
                entry = new LocalBotEntry { Character = bot, RespawnAt = 0f };
                return true;
            }
            catch (Exception ex)
            {
                error = ex.GetType().Name + ":" + Safe(ex.Message);
                try
                {
                    if (bot != null)
                    {
                        RobotManager.Instance.RemoveRobot(bot);
                        if (CharacterManager.Instance.character_set.Contains(bot))
                            CharacterManager.Instance.RemoveCharacter(bot.uid);
                        else if (bot.gameObject != null)
                            UnityEngine.Object.Destroy(bot.gameObject);
                    }
                    else if (gameObject != null)
                    {
                        UnityEngine.Object.Destroy(gameObject);
                    }
                }
                catch { }
                return false;
            }
        }

        private static void TickBotRespawns(Level level, Character player)
        {
            RAINNavigationGraph graph = RuntimeRainNavMesh.OwnedGraph;
            if (graph == null) return;
            float now = Time.realtimeSinceStartup;
            for (int i = 0; i < Bots.Count; i++)
            {
                LocalBotEntry entry = Bots[i];
                Character bot = entry == null ? null : entry.Character;
                if (bot == null) continue;
                if (!bot.IsDied)
                {
                    entry.RespawnAt = 0f;
                    FreezeRobot(bot as RobotControl);
                    continue;
                }
                if (entry.RespawnAt <= 0f)
                {
                    entry.RespawnAt = now + RespawnDelaySeconds;
                    continue;
                }
                if (now < entry.RespawnAt) continue;

                Vector3 spawn;
                if (!TrySampleGraphPoint(graph, player.transform.position, 14f, 75f, Bots, out spawn))
                    spawn = bot.transform.position - Vector3.up;
                Quaternion rotation = FaceTowards(spawn, player.transform.position);
                bot.Rebirth(ActorHealth, 0, spawn, rotation);
                bot.transform.rotation = rotation;
                bot.invincible_time = 0f;
                bot.can_select = true;
                bot.ActivateObjects();
                FreezeRobot(bot as RobotControl);
                entry.RespawnAt = 0f;
                FileLogger.Log("AUTO-BATTLE][LEVEL33-TEST", "bot_respawn uid=" + bot.uid +
                    " pos=" + FormatVector(spawn));
            }
        }

        private static bool TrySampleGraphPoint(RAINNavigationGraph graph, Vector3 origin,
            float minDistance, float maxDistance, List<LocalBotEntry> existing, out Vector3 point)
        {
            point = Vector3.zero;
            if (graph == null || graph.Size <= 0) return false;
            int pathChecks = 0;
            for (int attempt = 0; attempt < 2500; attempt++)
            {
                NavigationGraphNode node;
                try { node = graph.GetNode(UnityEngine.Random.Range(0, graph.Size)); }
                catch { continue; }
                NavMeshPoly poly = node as NavMeshPoly;
                if (poly == null || poly.Unwalkable || poly.TriangleCount <= 0 || !IsWellConnected(poly)) continue;
                Vector3 candidate = poly.Position;
                if (!IsFinite(candidate)) continue;
                float distance = minDistance <= 0f ? 0f : XzDistance(origin, candidate);
                if (distance < minDistance || distance > maxDistance) continue;
                if (!IsSeparated(candidate, existing, 8f)) continue;
                if (minDistance > 0f)
                {
                    if (pathChecks++ >= 48) return false;
                    if (!HasCompleteGraphPath(graph, origin, candidate)) continue;
                }
                Vector3 grounded;
                if (!TryValidateSpawnPoint(candidate, out grounded)) continue;
                point = grounded;
                return true;
            }
            return false;
        }

        private static bool IsWellConnected(NavMeshPoly poly)
        {
            int sharedEdges = 0;
            try
            {
                for (int i = 0; i < poly.EdgeCount; i++)
                {
                    NavMeshEdge edge = poly.GetEdgeNode(i);
                    if (edge != null && edge.PolyCount > 1) sharedEdges++;
                }
            }
            catch { return false; }
            return sharedEdges >= 2;
        }

        private static bool TryValidateSpawnPoint(Vector3 candidate, out Vector3 grounded)
        {
            grounded = candidate;
            try
            {
                RaycastHit[] hits = Physics.RaycastAll(candidate + Vector3.up * 1.25f, Vector3.down, 2.5f);
                bool found = false;
                float bestDelta = float.MaxValue;
                for (int i = 0; i < hits.Length; i++)
                {
                    Collider collider = hits[i].collider;
                    if (collider == null || collider.isTrigger || hits[i].normal.y < 0.55f) continue;
                    float delta = Mathf.Abs(hits[i].point.y - candidate.y);
                    if (delta > 0.9f || delta >= bestDelta) continue;
                    bestDelta = delta;
                    grounded = hits[i].point;
                    found = true;
                }
                if (!found) return false;
                return !Physics.CheckCapsule(grounded + Vector3.up * 0.35f,
                    grounded + Vector3.up * 1.55f, 0.28f);
            }
            catch { return false; }
        }

        private static bool HasCompleteGraphPath(RAINNavigationGraph graph, Vector3 from, Vector3 to)
        {
            try
            {
                RAINPathFinder finder = graph.CreatePathFinder();
                if (finder == null) return false;
                finder.MaxYOffset = 4f;
                finder.MaxPathfindingSteps = 2048;
                finder.MaxPathLength = 600f;
                finder.StartPath(graph, from, to);
                RAINPath path = null;
                for (int slice = 0; slice < 24; slice++)
                {
                    bool complete = finder.ComputePath(out path);
                    if (complete || !finder.InProgress) break;
                }
                return path != null && path.IsValid && !path.IsPartial && path.WaypointCount > 0;
            }
            catch { return false; }
        }

        private static bool IsSeparated(Vector3 candidate, List<LocalBotEntry> existing, float minimum)
        {
            if (existing == null) return true;
            for (int i = 0; i < existing.Count; i++)
            {
                Character character = existing[i] == null ? null : existing[i].Character;
                if (character != null && XzDistance(candidate, character.transform.position) < minimum) return false;
            }
            return true;
        }

        private static void FreezeRobot(RobotControl robot)
        {
            if (robot == null) return;
            try { RobotManager.Instance.RemoveRobot(robot); } catch { }
            try { robot.SetAttackCheckDistance(-1f); } catch { }
            try { robot.direction = Vector2.zero; } catch { }
            try
            {
                if (robot.aiRig != null && robot.aiRig.AI != null)
                {
                    robot.aiRig.AI.IsActive = false;
                    if (robot.aiRig.AI.Motor != null) robot.aiRig.AI.Motor.Stop();
                }
            }
            catch { }
        }

        private static void FreezeAllBots()
        {
            for (int i = 0; i < Bots.Count; i++)
            {
                Character bot = Bots[i] == null ? null : Bots[i].Character;
                if (bot != null && !bot.IsDied) FreezeRobot(bot as RobotControl);
            }
        }

        private static CharacterInfoData BuildProfile(CharacterInfoData source, byte uid, int team, string displayName)
        {
            CharacterInfoData profile = new CharacterInfoData();
            profile.is_primary_card = source != null && source.is_primary_card;
            profile.run_speed = 8f;
            profile.primary_run_speed = 8f;
            profile.run_acceleration = 12f;
            profile.primary_run_acceleration = 12f;
            profile.roll_speed = source != null && source.roll_speed > 0f ? source.roll_speed : 4f;
            profile.primary_roll_speed = profile.roll_speed;
            profile.roll_acceleration = source == null ? 8f : source.roll_acceleration;
            profile.roll_start_frozen_time = source == null ? 0.1f : source.roll_start_frozen_time;
            profile.jump_height = 1.5f;
            profile.primary_jump_height = 1.5f;
            profile.jump_velocity = Mathf.Sqrt(profile.jump_height * 39.2f);
            profile.throw_velocity = source == null ? 15f : source.throw_velocity;
            profile.shot_height = source == null ? 1f : source.shot_height;
            profile.gender = source == null ? (byte)0 : source.gender;
            profile.character_id = 0x7FFF33000000UL + uid;
            profile.character_level = source == null ? 1u : source.character_level;
            profile.rank_type = source == null ? 0 : source.rank_type;
            profile.rank_level = source == null ? (byte)0 : source.rank_level;
            profile.ladderLevel = source == null ? (byte)0 : source.ladderLevel;
            profile.server_name = "LOCAL";
            profile.vip_level = source == null ? (byte)0 : source.vip_level;
            profile.name = displayName;
            profile.team = (byte)team;
            profile.is_haspet = false;
            profile.eyes_distance = 120f;
            profile.attack_spread = 0f;
            profile.follow_distance = 3f;
            profile.max_weapon_use_time = 30f;
            profile.career = source == null ? (byte)0 : source.career;
            profile.max_health = ActorHealth;
            profile.avatarId = source == null || source.avatarId == null ? string.Empty : source.avatarId;
            profile.cool_down_addition = source == null ? 0f : source.cool_down_addition;
            profile.shoot_spread_addition = 0f;
            profile.shoot_speed_addition = source == null ? 0f : source.shoot_speed_addition;
            profile.avatar_part = source == null ? CopyGlobalAvatar() : CopyStringArray(source.avatar_part, 18);
            profile.temp_avatar = CopyStringArray(profile.avatar_part, 18);
            profile.gesture = source == null ? new string[6] : CopyStringArray(source.gesture, 6);
            profile.independ_info = source == null ? new string[5] : CopyStringArray(source.independ_info, 5);
            profile.primary_independ_info = source == null ? new string[5] :
                CopyStringArray(source.primary_independ_info, 5);
            profile.wing_params = source == null || source.wing_params == null
                ? new float[0]
                : (float[])source.wing_params.Clone();
            profile.slots_info = CreateEmptySlots();
            profile.primary_slots_info = CreateEmptySlots();
            ObjectBaseInfo sniper = CreateSniperInfo();
            profile.slots_info.object_info[0] = sniper;
            profile.primary_slots_info.object_info[0] = CloneWeaponInfo(sniper) ?? sniper;
            profile.ParsePartInfo();
            return profile;
        }

        private static ObjectBaseInfo CreateSniperInfo()
        {
            SniperGunInfo info = new SniperGunInfo();
            info.name = "sniperrifle_01";
            info.LoadLua("sniperrifle_01");
            info.name = "sniperrifle_01";
            info.display_name = "sniperrifle_01";
            info.id = 0;
            info.slot = 1;
            info.type = 2;
            info.sub_type = 2;
            info.cool_down_origial = info.cool_down;
            info.range_origial = info.range;
            info.cooling = -1f;
            info.stop_cooling = false;
            info.incool = false;
            info.cool_down_ready = true;
            return info;
        }

        private static ObjectBaseInfo CloneWeaponInfo(ObjectBaseInfo source)
        {
            if (source == null || MemberwiseCloneMethod == null) return null;
            ObjectBaseInfo clone = MemberwiseCloneMethod.Invoke(source, null) as ObjectBaseInfo;
            if (clone == null) return null;
            clone.owner = null;
            clone.owner_boss = null;
            clone.cast_effectData = source.cast_effectData == null
                ? new List<string>()
                : new List<string>(source.cast_effectData);
            clone.cooling = -1f;
            clone.stop_cooling = false;
            clone.incool = false;
            clone.cool_down_ready = true;
            return clone;
        }

        private static SlotInfo CreateEmptySlots()
        {
            SlotInfo slots = new SlotInfo();
            slots.object_info = new ObjectBaseInfo[36];
            return slots;
        }

        private static void CaptureCurrentProfile()
        {
            _capturedProfile = null;
            try
            {
                Level level = ASSingleton<Level>.Instance;
                Character player = level == null ? null : level.GetPlayer();
                _capturedProfile = player == null ? null : player.character_info;
            }
            catch { }
        }

        private static string[] CopyGlobalAvatar()
        {
            string[] result = new string[18];
            string[] keys =
            {
                "skin", "eye", "mouth", "nose", "ear", "beard", "hair", "helmet", "underwear",
                "outerwear", "trousers", "glove", "shoes", "decal", "movable", "immobile",
                "immobileUp", "immobileDown"
            };
            try
            {
                if (GlobalStatic.avatarTable == null) return result;
                for (int i = 0; i < result.Length; i++)
                {
                    object value = GlobalStatic.avatarTable[keys[i]];
                    result[i] = value == null ? string.Empty : value.ToString();
                }
            }
            catch { }
            return result;
        }

        private static string[] CopyStringArray(string[] source, int length)
        {
            string[] copy = new string[length];
            for (int i = 0; i < copy.Length; i++)
                copy[i] = source != null && i < source.Length && source[i] != null ? source[i] : string.Empty;
            return copy;
        }

        private static bool TryAllocateBotUid(Level level, Character player, out byte uid)
        {
            for (int candidate = 239; candidate >= 220; candidate--)
            {
                if (player != null && player.uid == candidate) continue;
                Character existing = null;
                try { existing = level.GetCharacter(candidate); } catch { }
                if (existing == null)
                {
                    uid = (byte)candidate;
                    return true;
                }
            }
            uid = 0;
            return false;
        }

        private static Character FindBotByUid(int uid)
        {
            for (int i = 0; i < Bots.Count; i++)
            {
                Character bot = Bots[i] == null ? null : Bots[i].Character;
                if (bot != null && bot.uid == uid) return bot;
            }
            return null;
        }

        private static Character CurrentPlayer()
        {
            try
            {
                Level level = ASSingleton<Level>.Instance;
                return level == null ? null : level.GetPlayer();
            }
            catch { return null; }
        }

        private static void DestroyBots()
        {
            for (int i = Bots.Count - 1; i >= 0; i--)
            {
                Character bot = Bots[i] == null ? null : Bots[i].Character;
                if (bot == null) continue;
                try { RobotManager.Instance.RemoveRobot(bot); } catch { }
                try
                {
                    CharacterManager manager = CharacterManager.Instance;
                    if (manager != null && manager.character_set != null && manager.character_set.Contains(bot))
                        manager.RemoveCharacter(bot.uid);
                    else if (bot.gameObject != null)
                        UnityEngine.Object.Destroy(bot.gameObject);
                }
                catch
                {
                    try { if (bot.gameObject != null) UnityEngine.Object.Destroy(bot.gameObject); } catch { }
                }
            }
            Bots.Clear();
        }

        private static void FailAndReturn(string reason)
        {
            _enabled = false;
            _actorsPrepared = false;
            AutoBattleManager.SetEnabled(false, "level33_test_failed");
            AutoBattleInput.ClearAll();
            DestroyBots();
            AutoBattleRoutePlanner.DeactivateNavigation("level33_test_failed");
            State = LocalNavigationTestState.Failed;
            StatusText = reason;
            if (MapBakeSceneLoader.DirectSceneActive)
            {
                string ignored;
                MapBakeSceneLoader.TryReturnToLobby(out ignored);
            }
            else
            {
                _ownsDirectScene = false;
                MapBakeSceneLoader.CancelPending("level33_test_failed");
            }
            FileLogger.Log("AUTO-BATTLE][LEVEL33-TEST", "failed reason=" + Safe(reason));
        }

        private static Quaternion FaceTowards(Vector3 from, Vector3 to)
        {
            Vector3 direction = to - from;
            direction.y = 0f;
            return direction.sqrMagnitude < 0.0001f
                ? Quaternion.identity
                : Quaternion.LookRotation(direction.normalized, Vector3.up);
        }

        private static float XzDistance(Vector3 a, Vector3 b)
        {
            float x = a.x - b.x;
            float z = a.z - b.z;
            return Mathf.Sqrt(x * x + z * z);
        }

        private static bool IsFinite(Vector3 value)
        {
            return !float.IsNaN(value.x) && !float.IsInfinity(value.x) &&
                   !float.IsNaN(value.y) && !float.IsInfinity(value.y) &&
                   !float.IsNaN(value.z) && !float.IsInfinity(value.z);
        }

        private static string CacheSourceName(string source)
        {
            return string.Equals(source, "memory", StringComparison.OrdinalIgnoreCase) ? "内存缓存" : "磁盘缓存";
        }

        private static string FormatVector(Vector3 value)
        {
            return string.Format("({0:0.00},{1:0.00},{2:0.00})", value.x, value.y, value.z);
        }

        private static string Safe(string value)
        {
            if (string.IsNullOrEmpty(value)) return "-";
            string safe = value.Replace('\r', ' ').Replace('\n', ' ').Trim();
            return safe.Length <= 120 ? safe : safe.Substring(0, 120);
        }
    }
}
