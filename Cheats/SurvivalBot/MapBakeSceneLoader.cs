using ASWDEBUG.Cheats.AutoBattle;
using ASWDEBUG.Logger;
using CodeStage.AntiCheat.ObscuredTypes;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using UnityEngine;

namespace ASWDEBUG.Cheats.SurvivalBot
{
    internal static class MapBakeSceneLoader
    {
        private const string SelectedMapKey = "ASWDEBUG.SurvivalBot.MapBakeTarget";
        private const string PreferredLevel33SurvivalMap = "level46";
        private static readonly Regex ScenePattern = new Regex(
            "FilePath=\"Prefab/Scene/([^\"]+)\\.scene\"", RegexOptions.IgnoreCase);
        private static readonly Regex SetMeshPattern = new Regex(
            @"(?:level\.)?SetMesh\s*\(\s*[""']([^""']+)[""']", RegexOptions.IgnoreCase);
        private static readonly Regex LuaBlockCommentPattern = new Regex(
            @"--\[\[.*?\]\]", RegexOptions.Singleline);
        private static readonly Regex LuaLineCommentPattern = new Regex(@"--[^\r\n]*");

        private sealed class MapOption
        {
            internal string Token;
            internal string Key;
            internal string DisplayName;
            internal ulong Id;
            internal RoomInfo.GameType GameType;
        }

        private static string[] _availableMaps;
        private static string[] _displayNames;
        private static MapOption[] _mapOptions;
        private static string _selectedMap;
        private static string _pendingMap = string.Empty;
        private static MapOption _pendingOption;
        private static string _activeMap = string.Empty;
        private static string _activeDisplayName = string.Empty;
        private static string _activeOptionToken = string.Empty;
        private static string _resolvedSceneMap = string.Empty;
        private static string _lastResolvedSceneMap = string.Empty;
        private static string _lastResolvedDisplayName = string.Empty;
        private static string _mapOptionSignature = string.Empty;
        private static bool _authoritativeOptionsReady;
        private static bool _transitioning;
        private static bool _directSceneActive;
        private static bool _autoLeaveCaptured;
        private static int _savedAutoLeaveTime;
        private static float _nextLocalizationRefreshAt;
        private static bool _returnRequested;

        internal static string[] AvailableMaps
        {
            get
            {
                EnsureMapsLoaded();
                RefreshMapOptions();
                return _availableMaps;
            }
        }

        internal static string[] AvailableMapDisplayNames
        {
            get
            {
                EnsureMapsLoaded();
                RefreshMapOptions();
                return _displayNames;
            }
        }

        internal static string SelectedMap
        {
            get
            {
                EnsureMapsLoaded();
                RefreshMapOptions();
                return _selectedMap;
            }
        }

        internal static string SelectedMapDisplayName
        {
            get
            {
                MapOption option = FindOption(SelectedMap);
                return option == null ? "-" : option.DisplayName;
            }
        }

        internal static bool IsTransitioning
        {
            get { return _transitioning || !string.IsNullOrEmpty(_pendingMap); }
        }

        internal static bool DirectSceneActive
        {
            get { return _directSceneActive; }
        }

        internal static string StatusText { get; private set; }
        internal static int MapOptionsVersion { get; private set; }

        internal static void SelectMap(int index)
        {
            EnsureMapsLoaded();
            RefreshMapOptions();
            if (_availableMaps.Length == 0) return;
            index = Mathf.Clamp(index, 0, _availableMaps.Length - 1);
            _selectedMap = _availableMaps[index];
            PlayerPrefs.SetString(SelectedMapKey, _selectedMap);
            PlayerPrefs.Save();
        }

        internal static int SelectedMapIndex()
        {
            EnsureMapsLoaded();
            RefreshMapOptions();
            for (int i = 0; i < _availableMaps.Length; i++)
            {
                if (string.Equals(_availableMaps[i], _selectedMap, StringComparison.OrdinalIgnoreCase))
                    return i;
            }
            return 0;
        }

        internal static bool RequestSelectedMap(out string detail)
        {
            EnsureMapsLoaded();
            RefreshMapOptions(true);
            if (!_authoritativeOptionsReady)
            {
                detail = "地图列表尚未同步，请稍后重试";
                return false;
            }
            if (_availableMaps.Length == 0 || string.IsNullOrEmpty(_selectedMap))
            {
                detail = "没有找到可加载的地图资源";
                return false;
            }
            MapOption option = FindOption(_selectedMap);
            if (option == null)
            {
                detail = "所选地图已失效，请重新选择";
                return false;
            }
            return QueueOption(option, out detail);
        }

        internal static bool RequestPhysicalScene(string sceneName, RoomInfo.GameType gameType, out string detail)
        {
            EnsureMapsLoaded();
            RefreshMapOptions(true);
            string normalized = (sceneName ?? string.Empty).Trim().ToLowerInvariant();
            if (!_authoritativeOptionsReady)
            {
                detail = "地图列表尚未同步，请稍后重试";
                return false;
            }
            if (string.IsNullOrEmpty(normalized))
            {
                detail = "物理地图名称为空";
                return false;
            }

            string resolution;
            MapOption option = FindOptionForPhysicalScene(normalized, gameType, out resolution);
            if (option == null)
            {
                detail = string.IsNullOrEmpty(resolution)
                    ? "没有找到加载 " + normalized + " 的" + GameTypeName(gameType) + "关卡"
                    : resolution;
                return false;
            }
            return QueueOption(option, out detail);
        }

        private static bool QueueOption(MapOption option, out string detail)
        {
            if (_directSceneActive)
            {
                detail = "当前已在直接加载的地图中，请先退出该场景";
                return false;
            }
            GameStateManager manager = ASSingleton<GameStateManager>.Instance;
            if (manager == null || manager.CurStateType != GameStateType.Lobby)
            {
                detail = "请先停留在频道大厅再直接加载地图";
                return false;
            }
            if (RuntimeRainNavMesh.IsBuilding)
            {
                detail = "已有地图正在生成，不能替换当前构建";
                return false;
            }
            if (option == null || string.IsNullOrEmpty(option.Key) || option.Id == 0UL)
            {
                detail = "地图配置无效";
                return false;
            }

            // A disk cache is reusable; a large live RAIN graph is not safe to carry through
            // Unity's unload/load cycle in this 32-bit client.
            AutoBattleRoutePlanner.ShutdownNavigation("direct_map_transition:" + option.Key);
            _pendingOption = option;
            _pendingMap = option.Token;
            _resolvedSceneMap = string.Empty;
            _transitioning = true;
            _returnRequested = false;
            StatusText = "准备直接加载 " + option.DisplayName;
            detail = StatusText;
            FileLogger.Log("AUTO-BATTLE][MAP-BAKE", "direct_load_requested token=" + option.Token +
                " logical=" + option.Key + " mapId=" + option.Id + " gameType=" + (byte)option.GameType);
            return true;
        }

        internal static void Tick()
        {
            if (!string.IsNullOrEmpty(_pendingMap))
            {
                MapOption requested = _pendingOption;
                _pendingMap = string.Empty;
                _pendingOption = null;
                Launch(requested);
            }

            if (!_transitioning) return;
            Level level = ASSingleton<Level>.Instance;
            if (level == null || level.state != Level.State.kReady ||
                string.IsNullOrEmpty(_resolvedSceneMap) ||
                !string.Equals(level.map_name, _resolvedSceneMap, StringComparison.OrdinalIgnoreCase)) return;

            _transitioning = false;
            StatusText = "地图已加载，等待极限建图启动";
            FileLogger.Log("AUTO-BATTLE][MAP-BAKE", "direct_load_ready logical=" + _activeMap +
                " scene=" + _resolvedSceneMap);
        }

        internal static void CancelPending(string reason)
        {
            _pendingMap = string.Empty;
            _pendingOption = null;
            _transitioning = false;
            StatusText = "直接加载已取消";
            if (!_directSceneActive) RestoreAutoLeave();
            FileLogger.Log("AUTO-BATTLE][MAP-BAKE", "direct_load_cancelled reason=" + Safe(reason));
        }

        internal static void NotifyLevelExit()
        {
            if (!_directSceneActive) return;
            FileLogger.Log("AUTO-BATTLE][MAP-BAKE", "direct_scene_exit logical=" + Safe(_activeMap) +
                " scene=" + Safe(_resolvedSceneMap));
            _lastResolvedSceneMap = _resolvedSceneMap;
            _lastResolvedDisplayName = _activeDisplayName;
            _directSceneActive = false;
            _transitioning = false;
            _activeMap = string.Empty;
            _activeDisplayName = string.Empty;
            _activeOptionToken = string.Empty;
            _resolvedSceneMap = string.Empty;
            _returnRequested = false;
            RestoreAutoLeave();
        }

        internal static bool TryReturnToLobby(out string detail)
        {
            detail = string.Empty;
            if (!_directSceneActive || _returnRequested) return false;
            try
            {
                GameStateManager manager = ASSingleton<GameStateManager>.Instance;
                if (manager == null)
                {
                    detail = "返回大厅失败：游戏状态不存在";
                    return false;
                }

                _returnRequested = true;
                detail = "正在销毁本地场景并返回大厅";
                StatusText = detail;
                FileLogger.Log("AUTO-BATTLE][MAP-BAKE", "auto_return_requested logical=" + Safe(_activeMap) +
                    " scene=" + Safe(_resolvedSceneMap));
                manager.ChangeState(GameStateType.Lobby);
                return true;
            }
            catch (Exception ex)
            {
                _returnRequested = false;
                detail = "返回大厅失败：" + ex.GetType().Name;
                FileLogger.Log("AUTO-BATTLE][MAP-BAKE", "auto_return_failed=" + ex.GetType().Name +
                    ":" + Safe(ex.Message));
                return false;
            }
        }

        internal static void NotifyResolvedScene(string mapName)
        {
            if (!_directSceneActive || string.IsNullOrEmpty(_activeMap) || string.IsNullOrEmpty(mapName)) return;
            string normalized = mapName.Trim().ToLowerInvariant();
            if (string.Equals(_resolvedSceneMap, normalized, StringComparison.OrdinalIgnoreCase)) return;
            _resolvedSceneMap = normalized;
            FileLogger.Log("AUTO-BATTLE][MAP-BAKE", "direct_scene_resolved logical=" + _activeMap +
                " scene=" + _resolvedSceneMap);
        }

        internal static bool IsExpectedDirectScene(string mapName)
        {
            return _directSceneActive && !string.IsNullOrEmpty(_resolvedSceneMap) &&
                string.Equals(mapName, _resolvedSceneMap, StringComparison.OrdinalIgnoreCase);
        }

        internal static string DisplayNameFor(string mapName)
        {
            EnsureMapsLoaded();
            RefreshMapOptions();
            if (string.IsNullOrEmpty(mapName)) return "-";
            MapOption option = FindOption(mapName) ?? FindOptionByKey(mapName);
            if (option != null) return option.DisplayName;
            return "地图资源（编号 " + ParseMapId(mapName ?? string.Empty) + "）";
        }

        internal static string DisplayNameForRuntimeMap(string mapName)
        {
            if (_directSceneActive &&
                (string.Equals(mapName, _resolvedSceneMap, StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(mapName, _activeMap, StringComparison.OrdinalIgnoreCase)))
                return _activeDisplayName;
            if (!string.IsNullOrEmpty(_lastResolvedDisplayName) &&
                string.Equals(mapName, _lastResolvedSceneMap, StringComparison.OrdinalIgnoreCase))
                return _lastResolvedDisplayName;
            return DisplayNameFor(mapName);
        }

        private static void Launch(MapOption option)
        {
            try
            {
                GameStateManager manager = ASSingleton<GameStateManager>.Instance;
                GameLoadingState loading = manager == null ? null : manager.getState<GameLoadingState>();
                Level level = ASSingleton<Level>.Instance;
                if (manager == null || loading == null || level == null)
                {
                    _transitioning = false;
                    StatusText = "直接加载失败：游戏状态尚未初始化";
                    FileLogger.Log("AUTO-BATTLE][MAP-BAKE", "direct_load_failed reason=state_not_initialized");
                    return;
                }
                if (option == null || string.IsNullOrEmpty(option.Key) || option.Id == 0UL)
                {
                    _transitioning = false;
                    StatusText = "直接加载失败：地图配置无效";
                    FileLogger.Log("AUTO-BATTLE][MAP-BAKE", "direct_load_failed reason=invalid_option");
                    return;
                }

                CaptureAndDisableAutoLeave();
                _activeMap = option.Key;
                _activeDisplayName = option.DisplayName;
                _activeOptionToken = option.Token;
                _resolvedSceneMap = string.Empty;
                _directSceneActive = true;
                level.game_type = option.GameType;
                level.match_type = 0;

                loading.map_name = option.Key;
                loading.mesh_name = string.Empty;
                loading.map_id = (ObscuredULong)option.Id;
                loading.load_navmesh = false;
                loading.gameMode = (byte)option.GameType;
                Character player = level.GetPlayer();
                loading.player_id = player == null || player.uid == 0 ? 1u : player.uid;

                manager.ChangeState(GameStateType.GameLoading);
                StatusText = "正在直接加载 " + option.DisplayName;
                FileLogger.Log("AUTO-BATTLE][MAP-BAKE", "direct_load_started token=" + option.Token +
                    " logical=" + option.Key + " mapId=" + option.Id + " gameType=" +
                    (byte)option.GameType + " player=" + loading.player_id +
                    " scene=pending autoLeave=0 nativeNav=0");
            }
            catch (Exception ex)
            {
                _transitioning = false;
                _directSceneActive = false;
                RestoreAutoLeave();
                StatusText = "直接加载失败：" + ex.GetType().Name;
                FileLogger.Log("AUTO-BATTLE][MAP-BAKE", "direct_load_failed reason=" + ex.GetType().Name +
                    ":" + Safe(ex.Message));
            }
        }

        private static void EnsureMapsLoaded()
        {
            if (_availableMaps != null) return;
            List<MapOption> options = new List<MapOption>();
            try
            {
                string path = Path.Combine(Application.dataPath, "FileInfo.xml");
                string xml = File.Exists(path) ? File.ReadAllText(path) : string.Empty;
                MatchCollection matches = ScenePattern.Matches(xml);
                for (int i = 0; i < matches.Count; i++)
                {
                    string name = matches[i].Groups[1].Value.Trim().ToLowerInvariant();
                    if (!name.StartsWith("level", StringComparison.OrdinalIgnoreCase) || ContainsOption(options, name))
                        continue;
                    options.Add(new MapOption
                    {
                        Token = BuildToken((ulong)ParseMapId(name), RoomInfo.GameType.kGameTypeChiji, name),
                        Key = name,
                        DisplayName = "地图资源（编号 " + ParseMapId(name) + "）",
                        Id = (ulong)ParseMapId(name),
                        GameType = RoomInfo.GameType.kGameTypeChiji
                    });
                }
            }
            catch (Exception ex)
            {
                FileLogger.Log("AUTO-BATTLE][MAP-BAKE", "map_list_failed=" + ex.GetType().Name +
                    ":" + Safe(ex.Message));
            }

            if (options.Count == 0)
            {
                options.Add(new MapOption
                {
                    Token = BuildToken(33UL, RoomInfo.GameType.kGameTypeChiji, "level33"),
                    Key = "level33",
                    DisplayName = "地图资源（编号 33）",
                    Id = 33UL,
                    GameType = RoomInfo.GameType.kGameTypeChiji
                });
            }
            options.Sort(delegate(MapOption left, MapOption right)
            {
                return StringComparer.OrdinalIgnoreCase.Compare(left.Key, right.Key);
            });
            ApplyOptions(options, "installed_scene_fallback", false);
            RefreshMapOptions(true);
            StatusText = "目标地图 " + DisplayNameFor(_selectedMap);
        }

        private static void RefreshMapOptions()
        {
            RefreshMapOptions(false);
        }

        private static void RefreshMapOptions(bool force)
        {
            float now = Time.realtimeSinceStartup;
            if (!force && now < _nextLocalizationRefreshAt) return;
            _nextLocalizationRefreshAt = now + 1f;

            LobbyConnection lobby = GameApp.Instance == null ? null : GameApp.Instance.lobby_connection;
            if (lobby == null || lobby.level_list == null || lobby.level_list.Count == 0) return;
            List<MapOption> options = new List<MapOption>();
            for (int i = 0; i < lobby.level_list.Count; i++)
            {
                LevelInfo info = lobby.level_list[i];
                if (info == null || string.IsNullOrEmpty(info.name)) continue;
                string key = info.name.Trim().ToLowerInvariant();
                if (!key.StartsWith("level", StringComparison.OrdinalIgnoreCase)) continue;
                ulong id = (ulong)info.id;
                string token = BuildToken(id, info.game_type, key);
                if (ContainsOption(options, token)) continue;
                string localized = string.IsNullOrEmpty(info.show_name)
                    ? string.Empty
                    : info.show_name.valueByThisKey();
                if (string.IsNullOrEmpty(localized) || !ContainsChinese(localized))
                    localized = "地图（编号 " + (id == 0UL ? (ulong)ParseMapId(key) : id) + "）";
                options.Add(new MapOption
                {
                    Token = token,
                    Key = key,
                    DisplayName = GameTypeName(info.game_type) + " · " + localized,
                    Id = id,
                    GameType = info.game_type
                });
            }
            if (options.Count == 0) return;
            EnsureUniqueDisplayNames(options);

            string[] signatureParts = new string[options.Count];
            for (int i = 0; i < options.Count; i++)
                signatureParts[i] = options[i].Token + ":" + options[i].DisplayName;
            string signature = string.Join("|", signatureParts);
            if (string.Equals(signature, _mapOptionSignature, StringComparison.Ordinal)) return;
            _mapOptionSignature = signature;
            ApplyOptions(options, "lobby_all_levels", true);
        }

        private static void ApplyOptions(List<MapOption> options, string source, bool authoritative)
        {
            string previous = _selectedMap;
            string saved = PlayerPrefs.GetString(SelectedMapKey, "level33").Trim().ToLowerInvariant();
            _mapOptions = options.ToArray();
            _availableMaps = new string[_mapOptions.Length];
            _displayNames = new string[_mapOptions.Length];
            for (int i = 0; i < _mapOptions.Length; i++)
            {
                _availableMaps[i] = _mapOptions[i].Token;
                _displayNames[i] = _mapOptions[i].DisplayName;
            }

            if (FindOptionIndex(previous) >= 0) _selectedMap = previous;
            else if (FindOptionIndex(saved) >= 0) _selectedMap = saved;
            else if (FindOptionByKey(previous) != null) _selectedMap = FindOptionByKey(previous).Token;
            else if (FindOptionByKey(saved) != null) _selectedMap = FindOptionByKey(saved).Token;
            else _selectedMap = _availableMaps[0];
            _authoritativeOptionsReady = authoritative;
            MapOptionsVersion++;
            FileLogger.Log("AUTO-BATTLE][MAP-BAKE", "map_list_ready source=" + source +
                " authoritative=" + (authoritative ? 1 : 0) + " count=" + _availableMaps.Length +
                " selected=" + _selectedMap);
        }

        private static MapOption FindOption(string key)
        {
            int index = FindOptionIndex(key);
            return index < 0 ? null : _mapOptions[index];
        }

        private static MapOption FindOptionByKey(string key)
        {
            if (_mapOptions == null || string.IsNullOrEmpty(key)) return null;
            for (int i = 0; i < _mapOptions.Length; i++)
            {
                if (string.Equals(_mapOptions[i].Key, key, StringComparison.OrdinalIgnoreCase))
                    return _mapOptions[i];
            }
            return null;
        }

        private static MapOption FindOptionForPhysicalScene(string sceneName, RoomInfo.GameType gameType,
            out string resolution)
        {
            resolution = string.Empty;
            if (_mapOptions == null || string.IsNullOrEmpty(sceneName)) return null;
            TableManager table = ASSingleton<TableManager>.Instance;
            if (table == null) return null;
            List<MapOption> candidates = new List<MapOption>();

            for (int i = 0; i < _mapOptions.Length; i++)
            {
                MapOption option = _mapOptions[i];
                if (option == null || option.GameType != gameType || string.IsNullOrEmpty(option.Key)) continue;
                string lua = table.LoadAssetBundle("level/" + option.Key.ToLowerInvariant());
                if (string.IsNullOrEmpty(lua)) continue;
                lua = LuaLineCommentPattern.Replace(LuaBlockCommentPattern.Replace(lua, string.Empty), string.Empty);
                MatchCollection matches = SetMeshPattern.Matches(lua);
                string onlyScene = string.Empty;
                bool ambiguousScript = false;
                for (int j = 0; j < matches.Count; j++)
                {
                    string resolved = matches[j].Groups[1].Value.Trim().ToLowerInvariant();
                    if (string.IsNullOrEmpty(onlyScene)) onlyScene = resolved;
                    else if (!string.Equals(onlyScene, resolved, StringComparison.OrdinalIgnoreCase))
                    {
                        ambiguousScript = true;
                        break;
                    }
                }
                if (!ambiguousScript && string.Equals(onlyScene, sceneName, StringComparison.OrdinalIgnoreCase))
                    candidates.Add(option);
            }
            if (candidates.Count == 0) return null;

            MapOption selected = candidates.Count == 1 ? candidates[0] : null;
            if (selected == null && string.Equals(sceneName, "level33", StringComparison.OrdinalIgnoreCase) &&
                gameType == RoomInfo.GameType.kGameTypeChiji)
            {
                for (int i = 0; i < candidates.Count; i++)
                {
                    if (string.Equals(candidates[i].Key, PreferredLevel33SurvivalMap,
                        StringComparison.OrdinalIgnoreCase))
                    {
                        selected = candidates[i];
                        break;
                    }
                }
            }

            string candidateNames = string.Empty;
            for (int i = 0; i < candidates.Count; i++)
                candidateNames += (i == 0 ? string.Empty : ",") + candidates[i].Key + "#" + candidates[i].Id;
            if (selected == null)
            {
                resolution = "物理地图映射不唯一: " + candidateNames;
                FileLogger.Log("AUTO-BATTLE][MAP-BAKE", "physical_scene_ambiguous scene=" + sceneName +
                    " gameType=" + (byte)gameType + " candidates=" + candidateNames);
                return null;
            }

            FileLogger.Log("AUTO-BATTLE][MAP-BAKE", "physical_scene_match scene=" + sceneName +
                " token=" + selected.Token + " logical=" + selected.Key + " gameType=" + (byte)gameType +
                " candidates=" + candidateNames);
            return selected;
        }

        private static int FindOptionIndex(string key)
        {
            if (_mapOptions == null || string.IsNullOrEmpty(key)) return -1;
            for (int i = 0; i < _mapOptions.Length; i++)
            {
                if (string.Equals(_mapOptions[i].Token, key, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(_mapOptions[i].Key, key, StringComparison.OrdinalIgnoreCase)) return i;
            }
            return -1;
        }

        private static bool ContainsOption(List<MapOption> options, string token)
        {
            if (string.IsNullOrEmpty(token)) return false;
            for (int i = 0; i < options.Count; i++)
            {
                if (string.Equals(options[i].Token, token, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        private static void EnsureUniqueDisplayNames(List<MapOption> options)
        {
            for (int i = 0; i < options.Count; i++)
            {
                int matches = 0;
                for (int j = 0; j < options.Count; j++)
                {
                    if (string.Equals(options[i].DisplayName, options[j].DisplayName,
                        StringComparison.OrdinalIgnoreCase)) matches++;
                }
                if (matches > 1) options[i].DisplayName += " · ID " + options[i].Id;
            }
        }

        private static string BuildToken(ulong id, RoomInfo.GameType gameType, string key)
        {
            return id + "|" + (byte)gameType + "|" + (key ?? string.Empty).Trim().ToLowerInvariant();
        }

        private static string GameTypeName(RoomInfo.GameType gameType)
        {
            switch (gameType)
            {
                case RoomInfo.GameType.kGameTypeRandom: return "随机";
                case RoomInfo.GameType.kGameTypeContention: return "占点";
                case RoomInfo.GameType.kGameTypeOccupy: return "夺旗";
                case RoomInfo.GameType.kGameTypeSnatch: return "夺宝";
                case RoomInfo.GameType.kGameTypeTeamDead: return "团战";
                case RoomInfo.GameType.kGameTypeHero: return "英雄";
                case RoomInfo.GameType.kGameTypeRound: return "回合";
                case RoomInfo.GameType.kGameTypeNovice: return "新手";
                case RoomInfo.GameType.kGameTypeBlast: return "爆破";
                case RoomInfo.GameType.kGameTypeBoss: return "BOSS";
                case RoomInfo.GameType.kGameTypeBioche: return "生化";
                case RoomInfo.GameType.kGameTypeKillAll: return "歼灭";
                case RoomInfo.GameType.kGameTypeWerewolf: return "狼人";
                case RoomInfo.GameType.kGameTypeBiocheHunter: return "救世主";
                case RoomInfo.GameType.kGameTypeChiji: return "生存";
                default: return "其他";
            }
        }

        private static bool ContainsChinese(string value)
        {
            for (int i = 0; i < value.Length; i++)
            {
                char c = value[i];
                if (c >= '\u3400' && c <= '\u9fff') return true;
            }
            return false;
        }

        private static void CaptureAndDisableAutoLeave()
        {
            if (!_autoLeaveCaptured)
            {
                _savedAutoLeaveTime = StartConfig.autoLeaveTime;
                _autoLeaveCaptured = true;
            }
            StartConfig.autoLeaveTime = 0;
        }

        private static void RestoreAutoLeave()
        {
            if (!_autoLeaveCaptured) return;
            StartConfig.autoLeaveTime = _savedAutoLeaveTime;
            _autoLeaveCaptured = false;
        }

        private static int ParseMapId(string mapName)
        {
            int value = 0;
            for (int i = 0; i < mapName.Length; i++)
            {
                char c = mapName[i];
                if (c < '0' || c > '9') continue;
                value = value * 10 + c - '0';
                if (value > 999999) break;
            }
            return value;
        }

        private static string Safe(string value)
        {
            if (string.IsNullOrEmpty(value)) return "-";
            string safe = value.Replace('\r', ' ').Replace('\n', ' ').Trim();
            return safe.Length <= 100 ? safe : safe.Substring(0, 100);
        }
    }
}
