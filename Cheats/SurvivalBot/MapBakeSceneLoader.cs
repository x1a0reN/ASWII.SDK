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
        private static readonly Regex ScenePattern = new Regex(
            "FilePath=\"Prefab/Scene/([^\"]+)\\.scene\"", RegexOptions.IgnoreCase);

        private static string[] _availableMaps;
        private static string[] _displayNames;
        private static string _selectedMap;
        private static string _pendingMap = string.Empty;
        private static string _activeMap = string.Empty;
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
                return _availableMaps;
            }
        }

        internal static string[] AvailableMapDisplayNames
        {
            get
            {
                EnsureMapsLoaded();
                RefreshLocalizedNames();
                return _displayNames;
            }
        }

        internal static string SelectedMap
        {
            get
            {
                EnsureMapsLoaded();
                return _selectedMap;
            }
        }

        internal static string SelectedMapDisplayName
        {
            get { return DisplayNameFor(SelectedMap); }
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

        internal static void SelectMap(int index)
        {
            EnsureMapsLoaded();
            if (_availableMaps.Length == 0) return;
            index = Mathf.Clamp(index, 0, _availableMaps.Length - 1);
            _selectedMap = _availableMaps[index];
            PlayerPrefs.SetString(SelectedMapKey, _selectedMap);
            PlayerPrefs.Save();
        }

        internal static int SelectedMapIndex()
        {
            EnsureMapsLoaded();
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
            if (_availableMaps.Length == 0 || string.IsNullOrEmpty(_selectedMap))
            {
                detail = "没有找到可加载的地图资源";
                return false;
            }
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

            _pendingMap = _selectedMap;
            _transitioning = true;
            _returnRequested = false;
            StatusText = "准备直接加载 " + SelectedMapDisplayName;
            detail = StatusText;
            FileLogger.Log("AUTO-BATTLE][MAP-BAKE", "direct_load_requested map=" + _selectedMap);
            return true;
        }

        internal static void Tick()
        {
            if (!string.IsNullOrEmpty(_pendingMap))
            {
                string requested = _pendingMap;
                _pendingMap = string.Empty;
                Launch(requested);
            }

            if (!_transitioning) return;
            Level level = ASSingleton<Level>.Instance;
            if (level == null || level.state != Level.State.kReady ||
                !string.Equals(level.map_name, _activeMap, StringComparison.OrdinalIgnoreCase)) return;

            _transitioning = false;
            StatusText = "地图已加载，等待极限建图启动";
            FileLogger.Log("AUTO-BATTLE][MAP-BAKE", "direct_load_ready map=" + _activeMap);
        }

        internal static void CancelPending(string reason)
        {
            _pendingMap = string.Empty;
            _transitioning = false;
            StatusText = "直接加载已取消";
            if (!_directSceneActive) RestoreAutoLeave();
            FileLogger.Log("AUTO-BATTLE][MAP-BAKE", "direct_load_cancelled reason=" + Safe(reason));
        }

        internal static void NotifyLevelExit()
        {
            if (!_directSceneActive) return;
            FileLogger.Log("AUTO-BATTLE][MAP-BAKE", "direct_scene_exit map=" + Safe(_activeMap));
            _directSceneActive = false;
            _transitioning = false;
            _activeMap = string.Empty;
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
                detail = "建图缓存已保存，正在销毁场景并返回大厅";
                StatusText = detail;
                FileLogger.Log("AUTO-BATTLE][MAP-BAKE", "auto_return_requested map=" + Safe(_activeMap));
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

        internal static void OverrideDirectSceneName(ref string mapName)
        {
            if (!_directSceneActive || string.IsNullOrEmpty(_activeMap) ||
                string.Equals(mapName, _activeMap, StringComparison.OrdinalIgnoreCase)) return;

            string configuredMap = mapName;
            mapName = _activeMap;
            FileLogger.Log("AUTO-BATTLE][MAP-BAKE", "direct_scene_override configured=" +
                Safe(configuredMap) + " selected=" + _activeMap);
        }

        internal static bool IsExpectedDirectScene(string mapName)
        {
            return _directSceneActive && !string.IsNullOrEmpty(_activeMap) &&
                string.Equals(mapName, _activeMap, StringComparison.OrdinalIgnoreCase);
        }

        internal static string DisplayNameFor(string mapName)
        {
            EnsureMapsLoaded();
            RefreshLocalizedNames();
            if (string.IsNullOrEmpty(mapName)) return "-";
            for (int i = 0; i < _availableMaps.Length; i++)
            {
                if (string.Equals(_availableMaps[i], mapName, StringComparison.OrdinalIgnoreCase))
                    return _displayNames[i];
            }
            return "地图资源（编号 " + ParseMapId(mapName ?? string.Empty) + "）";
        }

        private static void Launch(string mapName)
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

                CaptureAndDisableAutoLeave();
                _activeMap = mapName;
                _directSceneActive = true;
                level.game_type = RoomInfo.GameType.kGameTypeChiji;
                level.match_type = 0;

                loading.map_name = mapName;
                loading.mesh_name = mapName;
                loading.map_id = (ObscuredULong)(ulong)ParseMapId(mapName);
                loading.load_navmesh = false;
                loading.gameMode = (byte)RoomInfo.GameType.kGameTypeChiji;
                Character player = level.GetPlayer();
                loading.player_id = player == null ? 0u : player.uid;

                manager.ChangeState(GameStateType.GameLoading);
                StatusText = "正在直接加载 " + DisplayNameFor(mapName);
                FileLogger.Log("AUTO-BATTLE][MAP-BAKE", "direct_load_started map=" + mapName +
                    " mapId=" + ParseMapId(mapName) + " player=" + loading.player_id +
                    " autoLeave=0 nativeNav=0");
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
            List<string> maps = new List<string>();
            try
            {
                string path = Path.Combine(Application.dataPath, "FileInfo.xml");
                string xml = File.Exists(path) ? File.ReadAllText(path) : string.Empty;
                MatchCollection matches = ScenePattern.Matches(xml);
                for (int i = 0; i < matches.Count; i++)
                {
                    string name = matches[i].Groups[1].Value.Trim().ToLowerInvariant();
                    if (!name.StartsWith("level", StringComparison.OrdinalIgnoreCase) || maps.Contains(name))
                        continue;
                    maps.Add(name);
                }
            }
            catch (Exception ex)
            {
                FileLogger.Log("AUTO-BATTLE][MAP-BAKE", "map_list_failed=" + ex.GetType().Name +
                    ":" + Safe(ex.Message));
            }

            if (maps.Count == 0) maps.Add("level33");
            maps.Sort(StringComparer.OrdinalIgnoreCase);
            _availableMaps = maps.ToArray();
            _displayNames = new string[_availableMaps.Length];
            for (int i = 0; i < _availableMaps.Length; i++)
                _displayNames[i] = "地图资源（编号 " + ParseMapId(_availableMaps[i]) + "）";
            string saved = PlayerPrefs.GetString(SelectedMapKey, "level33").Trim().ToLowerInvariant();
            _selectedMap = maps.Contains(saved) ? saved : (maps.Contains("level33") ? "level33" : maps[0]);
            RefreshLocalizedNames(true);
            StatusText = "目标地图 " + DisplayNameFor(_selectedMap);
            FileLogger.Log("AUTO-BATTLE][MAP-BAKE", "map_list_ready count=" + maps.Count +
                " selected=" + _selectedMap);
        }

        private static void RefreshLocalizedNames()
        {
            RefreshLocalizedNames(false);
        }

        private static void RefreshLocalizedNames(bool force)
        {
            float now = Time.realtimeSinceStartup;
            if (!force && now < _nextLocalizationRefreshAt) return;
            _nextLocalizationRefreshAt = now + 1f;

            LobbyConnection lobby = GameApp.Instance == null ? null : GameApp.Instance.lobby_connection;
            if (lobby == null || lobby.level_list == null || lobby.level_list.Count == 0) return;
            for (int i = 0; i < lobby.level_list.Count; i++)
            {
                LevelInfo info = lobby.level_list[i];
                if (info == null || string.IsNullOrEmpty(info.name) || string.IsNullOrEmpty(info.show_name)) continue;
                string key = info.name.Trim().ToLowerInvariant();
                string localized = info.show_name.valueByThisKey();
                if (string.IsNullOrEmpty(localized) || !ContainsChinese(localized)) continue;
                for (int j = 0; j < _availableMaps.Length; j++)
                {
                    if (!string.Equals(_availableMaps[j], key, StringComparison.OrdinalIgnoreCase)) continue;
                    _displayNames[j] = localized;
                    break;
                }
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
