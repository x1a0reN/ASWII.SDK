using ASWDEBUG.Cheats.AimTrack;
using ASWDEBUG.Cheats.AutoAim;
using ASWDEBUG.Cheats.AutoUse;
using ASWDEBUG.Cheats.ESP;
using ASWDEBUG.Cheats.Other;
using ASWDEBUG.Cheats.Player;
using ASWDEBUG.Global;
using ASWDEBUG.Logger;
using ASWDEBUG.Verify;
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using UniLua;
using UnityEngine;

namespace ASWDEBUG.Main
{
    public static class DllUsageTelemetry
    {
        private const bool RemoteLookupEnabled = false;
        private const float HeartbeatInterval = 8f;
        private const float LookupInterval = 3f;
        private const float KnownTtl = 45f;
        private const int MaxLookupIds = 96;
        private const float SnapshotInterval = 1f;
        private const int MaxEquipmentItems = 20;
        private const int MaxInventoryItems = 24;
        private const int MaxItemFieldBytes = 160;
        private const int MaxItemsJsonBytes = 1800;
        private const float PlayerDataRefreshInterval = 45f;
        private const float PlayerDataRetryInterval = 5f;
        private const float PlayerDataRequestTimeout = 15f;
        private const float ItemDisplayRequestInterval = 1f;

        private static readonly string[] StorageTypes =
        {
            "2", "3", "4", "5", "10"
        };

        private static readonly string[] DynamicMetadataKeys =
        {
            "channel_state", "currency_coupon", "currency_gold",
            "currency_star", "equipment", "experience", "experience_next",
            "fitness_value", "game_mode", "game_mode_text", "game_state",
            "game_state_text", "guild_name", "in_match", "inventory", "job",
            "ladder_level", "lobby_state", "map_name",
            "match_duration_seconds", "match_player_count", "match_players",
            "occupation", "player_level", "presence_state", "presence_text",
            "rank_level", "rank_type", "room_max_players",
            "room_name", "room_player_count", "team_name", "uid",
            "unity_scene", "venture_force", "vip_level"
        };

        private struct UsageSeen
        {
            public float Seen;
            public string CardLabel;
        }

        private struct ItemDisplayLookup
        {
            public string Key;
            public string ItemId;
            public string StorageType;
        }

        private static readonly object Sync = new object();
        private static readonly Dictionary<ulong, UsageSeen> ActiveDllUsers = new Dictionary<ulong, UsageSeen>(128);
        private static bool _started;
        private static bool _heartbeatInFlight;
        private static bool _lookupInFlight;
        private static float _nextHeartbeat;
        private static float _nextLookup;
        private static ulong _lastPlayerId;
        private static int _lastUid;
        private static string _lastName = string.Empty;
        private static string _clientHash;
        private static float _nextSnapshot;
        private static bool _playerInfoInFlight;
        private static bool _storageInFlight;
        private static bool _itemDisplayInFlight;
        private static float _playerInfoDeadline;
        private static float _storageDeadline;
        private static float _itemDisplayDeadline;
        private static float _nextItemDisplayLookup;
        private static float _nextPlayerInfoRefresh;
        private static float _nextStorageRefresh;
        private static int _playerInfoRequestGeneration;
        private static int _storageRequestGeneration;
        private static int _itemDisplayRequestGeneration;
        private static int _storageTypeIndex;
        private static string _activeStorageType = string.Empty;
        private static string _activeItemDisplayKey = string.Empty;
        private static string _cachedEquipmentJson = string.Empty;
        private static string _cachedInventoryJson = string.Empty;
        private static readonly List<Dictionary<string, string>>
            ProfileEquipmentItems =
                new List<Dictionary<string, string>>();
        private static readonly List<Dictionary<string, string>>
            SlotEquipmentItems =
                new List<Dictionary<string, string>>();
        private static readonly Dictionary<string, List<Dictionary<string, string>>>
            StorageItemsByType =
                new Dictionary<string, List<Dictionary<string, string>>>();
        private static readonly Queue<ItemDisplayLookup>
            PendingItemDisplayLookups = new Queue<ItemDisplayLookup>();
        private static readonly HashSet<string>
            PendingItemDisplayKeys =
                new HashSet<string>(StringComparer.Ordinal);
        private static readonly Dictionary<string, string>
            ItemDisplayNames =
                new Dictionary<string, string>(StringComparer.Ordinal);
        private static VeriGateClientSnapshot _snapshot;

        public static int KnownCount;
        public static string LastStatus = "idle";

        public static void Start()
        {
            if (_started) return;
            _started = true;
            _nextHeartbeat = 0f;
            _nextLookup = 0f;
            _nextSnapshot = 0f;
            _nextPlayerInfoRefresh = 0f;
            _nextStorageRefresh = 0f;
            _playerInfoInFlight = false;
            _storageInFlight = false;
            _itemDisplayInFlight = false;
            _playerInfoRequestGeneration = 0;
            _storageRequestGeneration = 0;
            _itemDisplayRequestGeneration = 0;
            _storageTypeIndex = 0;
            _activeStorageType = string.Empty;
            _activeItemDisplayKey = string.Empty;
            _cachedEquipmentJson = string.Empty;
            _cachedInventoryJson = string.Empty;
            ProfileEquipmentItems.Clear();
            SlotEquipmentItems.Clear();
            StorageItemsByType.Clear();
            PendingItemDisplayLookups.Clear();
            PendingItemDisplayKeys.Clear();
            ItemDisplayNames.Clear();
            var metadata = new Dictionary<string, string>();
            TrySet(metadata, "unity_version", Application.unityVersion);
            TrySet(metadata, "platform", Application.platform.ToString());
            TrySet(metadata, "device_model", SystemInfo.deviceModel);
            TrySet(metadata, "device_type", SystemInfo.deviceType.ToString());
            TrySet(metadata, "processor_type", SystemInfo.processorType);
            TrySet(metadata, "processor_count", SystemInfo.processorCount.ToString());
            TrySet(metadata, "system_memory_mb", SystemInfo.systemMemorySize.ToString());
            TrySet(metadata, "graphics_device", SystemInfo.graphicsDeviceName);
            TrySet(metadata, "graphics_vendor", SystemInfo.graphicsDeviceVendor);
            TrySet(metadata, "graphics_version", SystemInfo.graphicsDeviceVersion);
            TrySet(metadata, "graphics_memory_mb", SystemInfo.graphicsMemorySize.ToString());
            TrySet(metadata, "language", Application.systemLanguage.ToString());

            var next = new VeriGateClientSnapshot
            {
                RuntimeVersion = Environment.Version.ToString(),
                ModuleVersion = typeof(DllUsageTelemetry).Assembly.GetName().Version.ToString(),
                GameVersion = GetApplicationVersion(),
                OSVersion = SystemInfo.operatingSystem ?? string.Empty,
                MachineName = Environment.MachineName ?? string.Empty,
                SceneName = Application.loadedLevelName ?? string.Empty,
                Metadata = metadata
            };
            lock (Sync) _snapshot = next;
            LastStatus = "VeriGate runtime telemetry ready";
        }

        public static void Stop()
        {
            _started = false;
            LastStatus = "stopped";
        }

        public static void Tick(Character localPlayer)
        {
            if (!_started) Start();

            float now = Time.realtimeSinceStartup;
            if (now < _nextSnapshot) return;
            _nextSnapshot = now + SnapshotInterval;

            ulong playerId = GetCharacterId(localPlayer);
            int uid = GetUid(localPlayer);
            string name = GetName(localPlayer);
            string serverName = GetServerName(localPlayer);
            string sceneName = GetGameLocation();
            QueuePlayerDataRefresh(playerId, now);
            QueueItemDisplayLookup(now);
            Dictionary<string, string> dynamicMetadata =
                BuildDynamicMetadata(localPlayer, uid);
            _lastPlayerId = playerId;
            _lastUid = uid;
            _lastName = name;

            lock (Sync)
            {
                if (_snapshot == null) return;
                _snapshot.PlayerID = playerId == 0UL
                    ? string.Empty
                    : playerId.ToString();
                _snapshot.PlayerName = name ?? string.Empty;
                _snapshot.ServerName = serverName;
                _snapshot.SceneName = sceneName;
                IDictionary<string, string> metadata = _snapshot.Metadata;
                if (metadata != null)
                {
                    for (int i = 0; i < DynamicMetadataKeys.Length; i++)
                        metadata.Remove(DynamicMetadataKeys[i]);
                    foreach (KeyValuePair<string, string> item in dynamicMetadata)
                        TrySet(metadata, item.Key, item.Value);
                    metadata["features"] = BuildFeatureString();
                    metadata["screen"] = Screen.width + "x" + Screen.height;
                    metadata["quality_level"] = QualitySettings.GetQualityLevel().ToString();
                }
            }
            LastStatus = "VeriGate runtime telemetry updated";
            if (!RemoteLookupEnabled) ClearKnown();
        }

        internal static VeriGateClientSnapshot Capture()
        {
            lock (Sync)
            {
                if (_snapshot == null)
                {
                    return new VeriGateClientSnapshot
                    {
                        RuntimeVersion = Environment.Version.ToString(),
                        ModuleVersion =
                            typeof(DllUsageTelemetry).Assembly.GetName().Version.ToString(),
                        OSVersion = Environment.OSVersion.ToString(),
                        MachineName = Environment.MachineName,
                        Metadata = new Dictionary<string, string>()
                    };
                }
                return new VeriGateClientSnapshot
                {
                    RuntimeVersion = _snapshot.RuntimeVersion,
                    ModuleVersion = _snapshot.ModuleVersion,
                    GameVersion = _snapshot.GameVersion,
                    OSVersion = _snapshot.OSVersion,
                    MachineName = _snapshot.MachineName,
                    PlayerID = _snapshot.PlayerID,
                    PlayerName = _snapshot.PlayerName,
                    ServerName = _snapshot.ServerName,
                    SceneName = _snapshot.SceneName,
                    Metadata = _snapshot.Metadata == null
                        ? new Dictionary<string, string>()
                        : new Dictionary<string, string>(_snapshot.Metadata)
                };
            }
        }

        public static bool IsDllUser(ulong playerId)
        {
            return GetVisibleCardLabel(playerId) != null;
        }

        public static string GetVisibleCardLabel(ulong playerId)
        {
            if (playerId == 0UL) return null;

            float now = Time.realtimeSinceStartup;
            lock (Sync)
            {
                UsageSeen seen;
                if (!ActiveDllUsers.TryGetValue(playerId, out seen)) return null;
                if ((now - seen.Seen) > KnownTtl) return null;
                return string.IsNullOrEmpty(seen.CardLabel) ? null : seen.CardLabel;
            }
        }

        private static void QueueHeartbeat(ulong playerId, int uid, string name, string features)
        {
            QueueHeartbeat(playerId, uid, name, features, false);
        }

        private static void QueueHeartbeat(ulong playerId, int uid, string name, string features, bool force)
        {
            if (_heartbeatInFlight && !force) return;
            _heartbeatInFlight = true;
            string status = string.Equals(features, "offline", StringComparison.Ordinal) ? "offline" : "online";

            string body =
                "pid=" + Escape(playerId.ToString()) +
                "&uid=" + Escape(uid.ToString()) +
                "&name=" + Escape(name) +
                "&features=" + Escape(features) +
                "&client=" + Escape(GetClientHash()) +
                "&card=" + Escape(GetLocalCardCode()) +
                "&status=" + Escape(status) +
                "&version=ASWDEBUG-main";

            ThreadPool.QueueUserWorkItem(delegate
            {
                try
                {
                    PostForm("/api/heartbeat", body);
                    LastStatus = "heartbeat ok";
                }
                catch (Exception ex)
                {
                    LastStatus = "heartbeat failed";
                    FileLogger.Log("DLL-USAGE", "heartbeat failed: " + ex.Message);
                }
                finally
                {
                    _heartbeatInFlight = false;
                }
            });
        }

        private static void QueueLookup(List<ulong> ids, ulong viewerId, float stamp)
        {
            if (_lookupInFlight) return;
            _lookupInFlight = true;

            string csv = JoinIds(ids);
            string body =
                "viewer=" + Escape(viewerId.ToString()) +
                "&client=" + Escape(GetClientHash()) +
                "&ids=" + Escape(csv);

            ThreadPool.QueueUserWorkItem(delegate
            {
                try
                {
                    string resp = PostForm("/api/lookup", body);
                    ApplyLookupResponse(resp, stamp);
                    LastStatus = "lookup ok";
                }
                catch (Exception ex)
                {
                    LastStatus = "lookup failed";
                    FileLogger.Log("DLL-USAGE", "lookup failed: " + ex.Message);
                }
                finally
                {
                    _lookupInFlight = false;
                }
            });
        }

        private static void ApplyLookupResponse(string resp, float stamp)
        {
            Dictionary<ulong, UsageSeen> next = new Dictionary<ulong, UsageSeen>(128);
            if (!string.IsNullOrEmpty(resp))
            {
                string[] lines = resp.Split(new char[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                for (int i = 0; i < lines.Length; i++)
                {
                    string line = lines[i].Trim();
                    if (line.Length == 0 || string.Equals(line, "ok", StringComparison.OrdinalIgnoreCase)) continue;

                    string card = string.Empty;
                    int sep = line.IndexOf('|');
                    if (sep >= 0)
                    {
                        card = line.Substring(sep + 1).Trim();
                        line = line.Substring(0, sep).Trim();
                    }

                    ulong id;
                    if (TryParseUlong(line, out id) && id != 0UL)
                    {
                        string label = GetCardLabel(card);
                        if (!string.IsNullOrEmpty(label))
                        {
                            next[id] = new UsageSeen { Seen = stamp, CardLabel = label };
                        }
                    }
                }
            }

            lock (Sync)
            {
                ActiveDllUsers.Clear();
                foreach (KeyValuePair<ulong, UsageSeen> kv in next)
                {
                    ActiveDllUsers[kv.Key] = kv.Value;
                }
                KnownCount = ActiveDllUsers.Count;
            }
        }

        private static void ClearKnown()
        {
            lock (Sync)
            {
                if (ActiveDllUsers.Count == 0) return;
                ActiveDllUsers.Clear();
                KnownCount = 0;
            }
        }

        private static void PruneKnown(float now)
        {
            lock (Sync)
            {
                if (ActiveDllUsers.Count == 0) return;

                List<ulong> stale = null;
                foreach (KeyValuePair<ulong, UsageSeen> kv in ActiveDllUsers)
                {
                    if ((now - kv.Value.Seen) > KnownTtl)
                    {
                        if (stale == null) stale = new List<ulong>();
                        stale.Add(kv.Key);
                    }
                }

                if (stale != null)
                {
                    for (int i = 0; i < stale.Count; i++)
                    {
                        ActiveDllUsers.Remove(stale[i]);
                    }
                    KnownCount = ActiveDllUsers.Count;
                }
            }
        }

        private static List<ulong> CollectEnemyIds(ulong localPlayerId)
        {
            List<ulong> ids = new List<ulong>(32);
            try
            {
                Character player = null;
                try
                {
                    if (Level.Instance != null) player = Level.Instance.GetPlayer();
                }
                catch { }

                var mgr = CharacterManager.Instance;
                if (mgr == null || mgr.character_set == null) return ids;

                foreach (Character ch in mgr.character_set)
                {
                    if (ch == null || ch.IsDied) continue;
                    if (player != null && ch.GetTeam() == player.GetTeam()) continue;

                    ulong id = GetCharacterId(ch);
                    if (id == 0UL || id == localPlayerId) continue;
                    if (!ids.Contains(id))
                    {
                        ids.Add(id);
                        if (ids.Count >= MaxLookupIds) break;
                    }
                }
            }
            catch { }
            return ids;
        }

        private static string PostForm(string path, string body)
        {
            throw new NotSupportedException(
                "Legacy telemetry transport is disabled; VeriGate verify carries runtime data.");
        }

        private static void TrySet(
            IDictionary<string, string> metadata,
            string key,
            string value)
        {
            if (metadata == null || string.IsNullOrEmpty(key) ||
                string.IsNullOrEmpty(value))
                return;
            metadata[key] = value;
        }

        private static string GetServerName(Character localPlayer)
        {
            object characterInfo = ReadMember(localPlayer, "character_info");
            string value = ReadStringMember(
                characterInfo,
                "server_name",
                "ServerName");
            if (!string.IsNullOrEmpty(value)) return value;

            object lobby = GetLobbyConnection();
            value = ReadStringMember(lobby, "game_name", "GameName");
            if (!string.IsNullOrEmpty(value)) return value;

            try
            {
                GameApp app = GameApp.Instance;
                if (app != null && app.Web_API_InfoList != null)
                {
                    string server;
                    if (app.Web_API_InfoList.TryGetValue("servername", out server) &&
                        !string.IsNullOrEmpty(server))
                        return server;
                    if (app.Web_API_InfoList.TryGetValue("server", out server) &&
                        !string.IsNullOrEmpty(server))
                        return server;
                    if (app.Web_API_InfoList.TryGetValue("serverid", out server) &&
                        !string.IsNullOrEmpty(server))
                        return "服务器 " + server;
                }
            }
            catch { }
            return value ?? string.Empty;
        }

        private static Dictionary<string, string> BuildDynamicMetadata(
            Character localPlayer,
            int uid)
        {
            var metadata = new Dictionary<string, string>();
            object lobby = GetLobbyConnection();
            object channel = GetChannelConnection();
            object roomInfo = GetRoomInfo(channel);
            object characterInfo = ReadMember(localPlayer, "character_info");
            string lobbyState = ReadStringMember(lobby, "state");
            string channelState = ReadStringMember(channel, "state");
            string gameState = ReadStringMember(channel, "game_state");
            string gameMode = FirstNonEmpty(
                ReadStringMember(roomInfo, "game_mode_name"),
                ReadStringMember(Level.Instance, "game_type"));
            string presenceState = GetPresenceState(
                lobbyState,
                channelState,
                gameState,
                roomInfo);
            bool inMatch = IsInMatch(presenceState);

            TrySet(metadata, "uid", uid.ToString());
            TrySet(metadata, "unity_scene", Application.loadedLevelName);
            TrySet(metadata, "lobby_state", lobbyState);
            TrySet(metadata, "channel_state", channelState);
            TrySet(metadata, "game_state", gameState);
            TrySet(metadata, "game_state_text", TranslateGameState(gameState));
            TrySet(metadata, "presence_state", presenceState);
            TrySet(metadata, "presence_text", TranslatePresence(presenceState));
            TrySet(metadata, "in_match", inMatch ? "true" : "false");
            TrySet(metadata, "room_name", ReadStringMember(roomInfo, "room_name"));
            TrySet(metadata, "map_name", FirstNonEmpty(
                ReadStringMember(Level.Instance, "map_name"),
                ReadStringMember(roomInfo, "map_name")));
            TrySet(metadata, "game_mode", gameMode);
            TrySet(metadata, "game_mode_text", TranslateGameMode(gameMode));
            TrySet(metadata, "room_player_count", ReadStringMember(
                roomInfo,
                "current_client_num"));
            TrySet(metadata, "room_max_players", ReadStringMember(
                roomInfo,
                "max_client_num"));
            TrySet(metadata, "match_duration_seconds", ReadStringMember(
                channel,
                "game_time"));

            if (inMatch || presenceState == "matching" ||
                presenceState == "room" || presenceState == "loading" ||
                presenceState == "post_game" || presenceState == "replay")
            {
                int matchPlayerCount;
                string matchPlayers = BuildMatchPlayersJson(
                    localPlayer,
                    out matchPlayerCount);
                if (matchPlayerCount > 0)
                {
                    TrySet(
                        metadata,
                        "match_player_count",
                        matchPlayerCount.ToString());
                    TrySet(metadata, "match_players", matchPlayers);
                }
            }

            TrySet(metadata, "player_level", FirstNonEmpty(
                ReadStringMember(characterInfo, "character_level"),
                ReadStringMember(lobby, "character_level"),
                ReadGlobalString("level")));
            TrySet(metadata, "experience", ReadGlobalString("exp"));
            TrySet(metadata, "experience_next", ReadGlobalString("expNextLevel"));
            TrySet(metadata, "occupation", ReadGlobalString("occupation"));
            TrySet(metadata, "job", ReadGlobalString("job"));
            TrySet(metadata, "rank_type", FirstNonEmpty(
                ReadStringMember(characterInfo, "rank_type"),
                ReadStringMember(lobby, "rank_type"),
                ReadGlobalString("rankType")));
            TrySet(metadata, "rank_level", FirstNonEmpty(
                ReadStringMember(characterInfo, "rank_level"),
                ReadStringMember(lobby, "rank_level"),
                ReadGlobalString("rankLevel")));
            TrySet(metadata, "ladder_level", FirstNonEmpty(
                ReadStringMember(characterInfo, "ladderLevel"),
                ReadGlobalString("ladderLevel")));
            TrySet(metadata, "vip_level", FirstNonEmpty(
                ReadStringMember(characterInfo, "vip_level"),
                ReadStringMember(lobby, "vip_level"),
                ReadGlobalString("blueVipLevel")));
            TrySet(metadata, "guild_name", FirstNonEmpty(
                ReadStringMember(characterInfo, "guild_name"),
                ReadGlobalString("guildName")));
            TrySet(metadata, "team_name", FirstNonEmpty(
                ReadStringMember(characterInfo, "team_name"),
                ReadGlobalString("teamName")));
            TrySet(metadata, "venture_force", ReadGlobalString("ventureForce"));
            TrySet(metadata, "fitness_value", ReadGlobalString("fitnessValue"));
            TrySet(metadata, "currency_gold", ReadGlobalString("gp"));
            TrySet(metadata, "currency_coupon", ReadGlobalString("tk"));
            TrySet(metadata, "currency_star", ReadGlobalString("mb"));

            TrySet(metadata, "equipment", BuildEquipmentJson(characterInfo));
            TrySet(metadata, "inventory", BuildInventoryJson());
            return metadata;
        }

        private static void QueuePlayerDataRefresh(ulong playerId, float now)
        {
            if (playerId == 0UL) return;
            LobbyConnection lobby = GetLobbyConnection() as LobbyConnection;
            if (lobby == null) return;

            if (_playerInfoInFlight && now >= _playerInfoDeadline)
            {
                _playerInfoInFlight = false;
                _nextPlayerInfoRefresh =
                    now + PlayerDataRetryInterval;
            }
            if (_storageInFlight && now >= _storageDeadline)
            {
                _storageInFlight = false;
                _nextStorageRefresh =
                    now + PlayerDataRetryInterval;
            }

            if (!_playerInfoInFlight && now >= _nextPlayerInfoRefresh)
            {
                _playerInfoInFlight = true;
                _playerInfoDeadline = now + PlayerDataRequestTimeout;
                _nextPlayerInfoRefresh = now + PlayerDataRefreshInterval;
                int playerInfoGeneration =
                    ++_playerInfoRequestGeneration;
                try
                {
                    lobby.AddTextRpc(
                        "player_info",
                        delegate(string response)
                        {
                            OnTelemetryPlayerInfo(
                                playerInfoGeneration,
                                response);
                        },
                        null);
                }
                catch (Exception ex)
                {
                    _playerInfoInFlight = false;
                    _nextPlayerInfoRefresh =
                        now + PlayerDataRetryInterval;
                    FileLogger.Log(
                        "DLL-USAGE",
                        "player_info telemetry failed: " + ex.Message);
                }
            }

            if (_storageInFlight || now < _nextStorageRefresh) return;
            _activeStorageType = StorageTypes[_storageTypeIndex];
            string requestedStorageType = _activeStorageType;
            int storageGeneration = ++_storageRequestGeneration;
            _storageInFlight = true;
            _storageDeadline = now + PlayerDataRequestTimeout;
            try
            {
                lobby.AddTextRpc(
                    "storage_storage_list",
                    delegate(string response)
                    {
                        OnTelemetryStorage(
                            storageGeneration,
                            requestedStorageType,
                            response);
                    },
                    new Dictionary<string, string>
                    {
                        { "t", requestedStorageType },
                        { "s", "24" },
                        { "p", "1" }
                    });
            }
            catch (Exception ex)
            {
                _storageInFlight = false;
                _nextStorageRefresh = now + PlayerDataRetryInterval;
                FileLogger.Log(
                    "DLL-USAGE",
                    "storage telemetry failed: " + ex.Message);
            }
        }

        private static void OnTelemetryPlayerInfo(
            int requestGeneration,
            string data)
        {
            if (requestGeneration != _playerInfoRequestGeneration) return;
            try
            {
                LuaState lua = new LuaState();
                lua.DoString(data ?? string.Empty);
                if (lua["error"] != null)
                    throw new InvalidOperationException(
                        lua["error"].ToString());

                LuaTable player = lua.GetTable("player");
                if (player == null) player = lua.GetTable("characters");
                if (player == null)
                    throw new InvalidOperationException(
                        "player_info has no player table");

                var items = new List<Dictionary<string, string>>();
                AddItems(
                    items,
                    player["equips"],
                    MaxEquipmentItems);
                lock (Sync)
                {
                    ProfileEquipmentItems.Clear();
                    ProfileEquipmentItems.AddRange(items);
                    QueueUnresolvedItemDisplayLookups(
                        string.Empty,
                        ProfileEquipmentItems);
                    _cachedEquipmentJson =
                        BuildCombinedEquipmentJson();
                }
            }
            catch (Exception ex)
            {
                _nextPlayerInfoRefresh =
                    Time.realtimeSinceStartup + PlayerDataRetryInterval;
                FileLogger.Log(
                    "DLL-USAGE",
                    "player_info telemetry response failed: " + ex.Message);
            }
            finally
            {
                _playerInfoInFlight = false;
                QueueTelemetrySlotInfo(requestGeneration);
            }
        }

        private static void QueueTelemetrySlotInfo(int requestGeneration)
        {
            LobbyConnection lobby = GetLobbyConnection() as LobbyConnection;
            if (lobby == null) return;
            try
            {
                lobby.AddTextRpc(
                    "slot_get",
                    delegate(string response)
                    {
                        OnTelemetrySlotInfo(
                            requestGeneration,
                            response);
                    },
                    null);
            }
            catch (Exception ex)
            {
                FileLogger.Log(
                    "DLL-USAGE",
                    "slot_get telemetry failed: " + ex.Message);
            }
        }

        private static void OnTelemetrySlotInfo(
            int requestGeneration,
            string data)
        {
            if (requestGeneration != _playerInfoRequestGeneration) return;
            try
            {
                LuaState lua = new LuaState();
                lua.DoString(data ?? string.Empty);
                if (lua["error"] != null)
                    throw new InvalidOperationException(
                        lua["error"].ToString());

                var items = new List<Dictionary<string, string>>();
                AddItems(
                    items,
                    lua.GetTable("slots"),
                    MaxEquipmentItems);
                lock (Sync)
                {
                    SlotEquipmentItems.Clear();
                    SlotEquipmentItems.AddRange(items);
                    QueueUnresolvedItemDisplayLookups(
                        string.Empty,
                        SlotEquipmentItems);
                    _cachedEquipmentJson =
                        BuildCombinedEquipmentJson();
                }
            }
            catch (Exception ex)
            {
                _nextPlayerInfoRefresh =
                    Time.realtimeSinceStartup + PlayerDataRetryInterval;
                FileLogger.Log(
                    "DLL-USAGE",
                    "slot_get telemetry response failed: " + ex.Message);
            }
        }

        private static void OnTelemetryStorage(
            int requestGeneration,
            string storageType,
            string data)
        {
            if (requestGeneration != _storageRequestGeneration) return;
            try
            {
                LuaState lua = new LuaState();
                lua.DoString(data ?? string.Empty);
                if (lua["error"] != null)
                    throw new InvalidOperationException(
                        lua["error"].ToString());

                LuaTable table = lua.GetTable("items");
                var items = new List<Dictionary<string, string>>();
                AddItems(items, table, MaxInventoryItems);
                lock (Sync)
                {
                    QueueUnresolvedItemDisplayLookups(
                        storageType,
                        items);
                    StorageItemsByType[storageType] = items;
                    _cachedInventoryJson =
                        BuildCombinedInventoryJson();
                }

                _storageTypeIndex++;
                if (_storageTypeIndex >= StorageTypes.Length)
                {
                    _storageTypeIndex = 0;
                    _nextStorageRefresh =
                        Time.realtimeSinceStartup +
                        PlayerDataRefreshInterval;
                }
                else
                {
                    _nextStorageRefresh =
                        Time.realtimeSinceStartup + 1f;
                }
            }
            catch (Exception ex)
            {
                _nextStorageRefresh =
                    Time.realtimeSinceStartup + PlayerDataRetryInterval;
                FileLogger.Log(
                    "DLL-USAGE",
                    "storage telemetry response failed: " + ex.Message);
            }
            finally
            {
                _storageInFlight = false;
                _activeStorageType = string.Empty;
            }
        }

        private static void QueueUnresolvedItemDisplayLookups(
            string storageType,
            IList<Dictionary<string, string>> items)
        {
            if (string.IsNullOrEmpty(storageType) || items == null) return;
            for (int index = 0; index < items.Count; index++)
            {
                Dictionary<string, string> item = items[index];
                string itemId;
                string resolved;
                if (item == null ||
                    !item.TryGetValue("id", out itemId) ||
                    string.IsNullOrEmpty(itemId) ||
                    (item.TryGetValue("_display_resolved", out resolved) &&
                     resolved == "true"))
                    continue;

                string itemStorageType = storageType;
                if (string.IsNullOrEmpty(itemStorageType))
                    item.TryGetValue("_tip_type", out itemStorageType);
                if (string.IsNullOrEmpty(itemStorageType)) continue;

                string key = itemStorageType + "\n" + itemId;
                string displayName;
                if (ItemDisplayNames.TryGetValue(key, out displayName))
                {
                    item["name"] = displayName;
                    item["_display_resolved"] = "true";
                    continue;
                }
                if (!PendingItemDisplayKeys.Add(key)) continue;
                PendingItemDisplayLookups.Enqueue(new ItemDisplayLookup
                {
                    Key = key,
                    ItemId = itemId,
                    StorageType = itemStorageType
                });
            }
        }

        private static void QueueItemDisplayLookup(float now)
        {
            if (_itemDisplayInFlight && now >= _itemDisplayDeadline)
            {
                _itemDisplayInFlight = false;
                PendingItemDisplayKeys.Remove(_activeItemDisplayKey);
                _activeItemDisplayKey = string.Empty;
            }
            if (_itemDisplayInFlight ||
                now < _nextItemDisplayLookup ||
                PendingItemDisplayLookups.Count == 0)
                return;

            LobbyConnection lobby = GetLobbyConnection() as LobbyConnection;
            if (lobby == null) return;
            ItemDisplayLookup lookup = PendingItemDisplayLookups.Dequeue();
            int generation = ++_itemDisplayRequestGeneration;
            _itemDisplayInFlight = true;
            _itemDisplayDeadline = now + PlayerDataRequestTimeout;
            _nextItemDisplayLookup = now + ItemDisplayRequestInterval;
            _activeItemDisplayKey = lookup.Key;
            try
            {
                lobby.AddTextRpc(
                    "tip_player_item",
                    delegate(string response)
                    {
                        OnTelemetryItemDisplay(
                            generation,
                            lookup,
                            response);
                    },
                    new Dictionary<string, string>
                    {
                        { "pid", lookup.ItemId },
                        { "t", lookup.StorageType }
                    });
            }
            catch (Exception ex)
            {
                _itemDisplayInFlight = false;
                PendingItemDisplayKeys.Remove(lookup.Key);
                _activeItemDisplayKey = string.Empty;
                FileLogger.Log(
                    "DLL-USAGE",
                    "item display telemetry failed: " + ex.Message);
            }
        }

        private static void OnTelemetryItemDisplay(
            int generation,
            ItemDisplayLookup lookup,
            string data)
        {
            try
            {
                LuaState lua = new LuaState();
                lua.DoString(data ?? string.Empty);
                if (lua["error"] != null)
                    throw new InvalidOperationException(
                        lua["error"].ToString());

                string displayKey = lua["display"] == null
                    ? string.Empty
                    : lua["display"].ToString();
                string displayName = LocalizeItemKey(displayKey);
                if (string.IsNullOrEmpty(displayName))
                    throw new InvalidOperationException(
                        "tip_player_item has no localized display");

                lock (Sync)
                {
                    ItemDisplayNames[lookup.Key] = displayName;
                    List<Dictionary<string, string>> items;
                    if (StorageItemsByType.TryGetValue(
                        lookup.StorageType,
                        out items) && items != null)
                    {
                        ApplyItemDisplayName(
                            items,
                            lookup.ItemId,
                            displayName);
                        _cachedInventoryJson =
                            BuildCombinedInventoryJson();
                    }
                    bool equipmentUpdated = ApplyItemDisplayName(
                        ProfileEquipmentItems,
                        lookup.ItemId,
                        displayName);
                    equipmentUpdated = ApplyItemDisplayName(
                        SlotEquipmentItems,
                        lookup.ItemId,
                        displayName) || equipmentUpdated;
                    if (equipmentUpdated)
                        _cachedEquipmentJson =
                            BuildCombinedEquipmentJson();
                }
            }
            catch (Exception ex)
            {
                FileLogger.Log(
                    "DLL-USAGE",
                    "item display telemetry response failed: " +
                    ex.Message);
            }
            finally
            {
                PendingItemDisplayKeys.Remove(lookup.Key);
                if (generation == _itemDisplayRequestGeneration)
                {
                    _itemDisplayInFlight = false;
                    _activeItemDisplayKey = string.Empty;
                }
            }
        }

        private static bool ApplyItemDisplayName(
            IList<Dictionary<string, string>> items,
            string itemId,
            string displayName)
        {
            bool updated = false;
            for (int index = 0; items != null && index < items.Count; index++)
            {
                Dictionary<string, string> item = items[index];
                string currentItemId;
                if (item != null &&
                    item.TryGetValue("id", out currentItemId) &&
                    currentItemId == itemId)
                {
                    item["name"] = displayName;
                    item["_display_resolved"] = "true";
                    updated = true;
                }
            }
            return updated;
        }

        private static string BuildCombinedInventoryJson()
        {
            var combined = new List<Dictionary<string, string>>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            for (int typeIndex = 0;
                typeIndex < StorageTypes.Length &&
                combined.Count < MaxInventoryItems;
                typeIndex++)
            {
                List<Dictionary<string, string>> items;
                if (!StorageItemsByType.TryGetValue(
                    StorageTypes[typeIndex],
                    out items) || items == null)
                    continue;

                for (int itemIndex = 0;
                    itemIndex < items.Count &&
                    combined.Count < MaxInventoryItems;
                    itemIndex++)
                {
                    Dictionary<string, string> item = items[itemIndex];
                    string id;
                    string name;
                    item.TryGetValue("id", out id);
                    item.TryGetValue("name", out name);
                    string key = (id ?? string.Empty) + "\n" +
                        (name ?? string.Empty);
                    if (key == "\n" || !seen.Add(key)) continue;
                    combined.Add(item);
                }
            }
            return BuildItemsJson(combined, true);
        }

        private static string BuildCombinedEquipmentJson()
        {
            var combined = new List<Dictionary<string, string>>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            AddUniqueItems(
                combined,
                seen,
                SlotEquipmentItems,
                MaxEquipmentItems);
            AddUniqueItems(
                combined,
                seen,
                ProfileEquipmentItems,
                MaxEquipmentItems);
            return BuildItemsJson(combined, true);
        }

        private static void AddUniqueItems(
            List<Dictionary<string, string>> target,
            HashSet<string> seen,
            IList<Dictionary<string, string>> source,
            int maximum)
        {
            if (target == null || seen == null || source == null) return;
            for (int index = 0;
                index < source.Count && target.Count < maximum;
                index++)
            {
                Dictionary<string, string> item = source[index];
                if (item == null) continue;
                string id;
                string name;
                item.TryGetValue("id", out id);
                item.TryGetValue("name", out name);
                string key = (id ?? string.Empty) + "\n" +
                    (name ?? string.Empty);
                if (key == "\n" || !seen.Add(key)) continue;
                target.Add(item);
            }
        }

        private static string GetGameLocation()
        {
            object channel = GetChannelConnection();
            object roomInfo = GetRoomInfo(channel);
            string lobbyState = ReadStringMember(GetLobbyConnection(), "state");
            string channelState = ReadStringMember(channel, "state");
            string gameState = ReadStringMember(channel, "game_state");
            string presence = GetPresenceState(
                lobbyState,
                channelState,
                gameState,
                roomInfo);
            string presenceText = TranslatePresence(presence);
            string roomName = ReadStringMember(roomInfo, "room_name");
            string mapName = FirstNonEmpty(
                ReadStringMember(Level.Instance, "map_name"),
                ReadStringMember(roomInfo, "map_name"));
            string gameMode = TranslateGameMode(FirstNonEmpty(
                ReadStringMember(roomInfo, "game_mode_name"),
                ReadStringMember(Level.Instance, "game_type")));
            if (IsInMatch(presence) || presence == "room" ||
                presence == "matching" || presence == "loading" ||
                presence == "post_game" || presence == "replay")
            {
                string detail = FirstNonEmpty(gameMode, mapName, roomName);
                if (!string.IsNullOrEmpty(mapName) && detail != mapName)
                    detail += " · " + mapName;
                if (!string.IsNullOrEmpty(roomName) &&
                    detail != roomName && detail.IndexOf(roomName) < 0)
                    detail += " · " + roomName;
                return string.IsNullOrEmpty(detail)
                    ? presenceText
                    : presenceText + " · " + detail;
            }
            return FirstNonEmpty(presenceText, Application.loadedLevelName);
        }

        private static string GetPresenceState(
            string lobbyState,
            string channelState,
            string gameState,
            object roomInfo)
        {
            if (EqualsState(channelState, "kInReplay")) return "replay";
            if (EqualsState(channelState, "kInBalance") ||
                EqualsState(gameState, "kGameEnd") ||
                EqualsState(gameState, "kGameLeaving"))
                return "post_game";
            if (EqualsState(channelState, "kInGame") ||
                EqualsState(lobbyState, "kInGame"))
                return "in_game";
            if (EqualsState(gameState, "kLoading") ||
                EqualsState(channelState, "kInInitialized"))
                return "loading";
            if (EqualsState(channelState, "kInRoom"))
            {
                string matching = ReadStringMember(roomInfo, "is_matching");
                return IsTrue(matching) ? "matching" : "room";
            }
            if (EqualsState(channelState, "kInChannel") ||
                EqualsState(lobbyState, "kInChannel"))
                return "channel";
            if (EqualsState(lobbyState, "kInLobby")) return "lobby";
            if (EqualsState(lobbyState, "kInLogin") ||
                EqualsState(lobbyState, "kAuthentication") ||
                EqualsState(lobbyState, "kInitialized") ||
                EqualsState(lobbyState, "kConnected"))
                return "login";
            if (EqualsState(lobbyState, "kDisconnected"))
                return "disconnected";
            return "unknown";
        }

        private static bool IsInMatch(string presenceState)
        {
            return presenceState == "in_game" || presenceState == "replay";
        }

        private static bool EqualsState(string value, string expected)
        {
            return string.Equals(
                value,
                expected,
                StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsTrue(string value)
        {
            return string.Equals(value, "true", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(value, "1", StringComparison.OrdinalIgnoreCase);
        }

        private static string TranslatePresence(string value)
        {
            switch (value)
            {
                case "disconnected": return "未连接游戏";
                case "login": return "登录中";
                case "lobby": return "游戏大厅";
                case "channel": return "频道中";
                case "matching": return "匹配中";
                case "room": return "对局房间";
                case "loading": return "加载对局";
                case "in_game": return "对局中";
                case "post_game": return "结算中";
                case "replay": return "观看回放";
                default: return "状态未知";
            }
        }

        private static string TranslateGameState(string value)
        {
            switch (value)
            {
                case "kAuthentication": return "认证中";
                case "kLoading": return "加载中";
                case "kWaiting": return "等待开始";
                case "kInitialized": return "初始化完成";
                case "kAlive": return "存活";
                case "kDied": return "已阵亡";
                case "kGameEnd": return "对局结束";
                case "kGameLeaving": return "离开对局";
                default: return string.Empty;
            }
        }

        private static string TranslateGameMode(string value)
        {
            if (string.IsNullOrEmpty(value)) return string.Empty;
            if (value.IndexOf("Contention", StringComparison.OrdinalIgnoreCase) >= 0)
                return "争夺模式";
            if (value.IndexOf("Occupy", StringComparison.OrdinalIgnoreCase) >= 0)
                return "占领模式";
            if (value.IndexOf("Snatch", StringComparison.OrdinalIgnoreCase) >= 0)
                return "抢夺模式";
            if (value.IndexOf("TeamDead", StringComparison.OrdinalIgnoreCase) >= 0)
                return "团队竞技";
            if (value.IndexOf("Hero", StringComparison.OrdinalIgnoreCase) >= 0)
                return "英雄模式";
            if (value.IndexOf("Round", StringComparison.OrdinalIgnoreCase) >= 0)
                return "回合模式";
            if (value.IndexOf("Novice", StringComparison.OrdinalIgnoreCase) >= 0)
                return "新手模式";
            if (value.IndexOf("Blast", StringComparison.OrdinalIgnoreCase) >= 0)
                return "爆破模式";
            if (value.IndexOf("Boss", StringComparison.OrdinalIgnoreCase) >= 0)
                return "BOSS 模式";
            if (value.IndexOf("BiocheHunter", StringComparison.OrdinalIgnoreCase) >= 0)
                return "生化猎场";
            if (value.IndexOf("Bioche", StringComparison.OrdinalIgnoreCase) >= 0)
                return "生化模式";
            if (value.IndexOf("KillAll", StringComparison.OrdinalIgnoreCase) >= 0)
                return "歼灭模式";
            if (value.IndexOf("Werewolf", StringComparison.OrdinalIgnoreCase) >= 0)
                return "狼人模式";
            if (value.IndexOf("Chiji", StringComparison.OrdinalIgnoreCase) >= 0)
                return "吃鸡模式";
            if (value.IndexOf("Random", StringComparison.OrdinalIgnoreCase) >= 0)
                return "随机模式";
            if (value.IndexOf("None", StringComparison.OrdinalIgnoreCase) >= 0)
                return "未知模式";
            return value;
        }

        private static string BuildMatchPlayersJson(
            Character localPlayer,
            out int playerCount)
        {
            playerCount = 0;
            var players = new List<Dictionary<string, string>>();
            try
            {
                CharacterManager manager = CharacterManager.Instance;
                if (manager == null || manager.character_set == null)
                    return string.Empty;

                ulong localPlayerId = GetCharacterId(localPlayer);
                foreach (Character character in manager.character_set)
                {
                    if (character == null) continue;
                    var item = new Dictionary<string, string>();
                    ulong characterId = GetCharacterId(character);
                    item["id"] = characterId == 0UL
                        ? string.Empty
                        : characterId.ToString();
                    item["name"] = LimitUtf8(
                        GetName(character),
                        MaxItemFieldBytes);
                    item["team"] = character.GetTeam().ToString();
                    item["alive"] = character.IsDied ? "false" : "true";
                    item["bot"] = character.IsRobot ? "true" : "false";
                    item["self"] =
                        localPlayerId != 0UL && characterId == localPlayerId
                            ? "true"
                            : "false";
                    item["level"] = ReadStringMember(
                        ReadMember(character, "character_info"),
                        "character_level");
                    players.Add(item);
                    playerCount++;
                    if (players.Count >= 24) break;
                }
            }
            catch { }
            return BuildItemsJson(players, playerCount > 0);
        }

        private static object GetLobbyConnection()
        {
            try
            {
                GameApp app = GameApp.Instance;
                return app == null ? null : app.lobby_connection;
            }
            catch
            {
                return null;
            }
        }

        private static object GetChannelConnection()
        {
            try
            {
                GameApp app = GameApp.Instance;
                return app == null ? null : app.channel_connection;
            }
            catch
            {
                return null;
            }
        }

        private static object GetRoomInfo(object channel)
        {
            object room = ReadMember(channel, "room");
            return ReadMember(room, "room_info", "RoomInfo");
        }

        private static string ReadGlobalString(params string[] names)
        {
            return ReadStaticStringMember(FindLoadedType("GlobalStatic"), names);
        }

        private static string FirstNonEmpty(params string[] values)
        {
            if (values == null) return string.Empty;
            for (int i = 0; i < values.Length; i++)
            {
                if (!string.IsNullOrEmpty(values[i])) return values[i];
            }
            return string.Empty;
        }

        private static string GetApplicationVersion()
        {
            try
            {
                PropertyInfo property = typeof(Application).GetProperty(
                    "version",
                    BindingFlags.Static | BindingFlags.Public);
                object value = property == null
                    ? null
                    : property.GetValue(null, null);
                return value == null ? string.Empty : value.ToString();
            }
            catch
            {
                return string.Empty;
            }
        }

        private static string ReadStringMember(object target, params string[] names)
        {
            if (target == null || names == null) return string.Empty;
            Type type = target.GetType();
            for (int i = 0; i < names.Length; i++)
            {
                try
                {
                    PropertyInfo property = type.GetProperty(
                        names[i],
                        BindingFlags.Instance | BindingFlags.Public |
                        BindingFlags.NonPublic);
                    if (property != null)
                    {
                        object value = property.GetValue(target, null);
                        if (value != null) return value.ToString();
                    }
                    FieldInfo field = type.GetField(
                        names[i],
                        BindingFlags.Instance | BindingFlags.Public |
                        BindingFlags.NonPublic);
                    if (field != null)
                    {
                        object value = field.GetValue(target);
                        if (value != null) return value.ToString();
                    }
                }
                catch { }
            }
            return string.Empty;
        }

        private static object ReadMember(object target, params string[] names)
        {
            if (target == null || names == null) return null;
            Type type = target.GetType();
            for (int i = 0; i < names.Length; i++)
            {
                try
                {
                    PropertyInfo property = type.GetProperty(
                        names[i],
                        BindingFlags.Instance | BindingFlags.Public |
                        BindingFlags.NonPublic);
                    if (property != null) return property.GetValue(target, null);
                    FieldInfo field = type.GetField(
                        names[i],
                        BindingFlags.Instance | BindingFlags.Public |
                        BindingFlags.NonPublic);
                    if (field != null) return field.GetValue(target);
                }
                catch { }
            }
            return null;
        }

        private static string ReadStaticStringMember(
            Type type,
            params string[] names)
        {
            if (type == null || names == null) return string.Empty;
            for (int i = 0; i < names.Length; i++)
            {
                try
                {
                    PropertyInfo property = type.GetProperty(
                        names[i],
                        BindingFlags.Static | BindingFlags.Public |
                        BindingFlags.NonPublic);
                    if (property != null)
                    {
                        object value = property.GetValue(null, null);
                        if (value != null) return value.ToString();
                    }
                    FieldInfo field = type.GetField(
                        names[i],
                        BindingFlags.Static | BindingFlags.Public |
                        BindingFlags.NonPublic);
                    if (field != null)
                    {
                        object value = field.GetValue(null);
                        if (value != null) return value.ToString();
                    }
                }
                catch { }
            }
            return string.Empty;
        }

        private static object ReadStaticMember(Type type, params string[] names)
        {
            if (type == null || names == null) return null;
            for (int i = 0; i < names.Length; i++)
            {
                try
                {
                    PropertyInfo property = type.GetProperty(
                        names[i],
                        BindingFlags.Static | BindingFlags.Public |
                        BindingFlags.NonPublic);
                    if (property != null) return property.GetValue(null, null);
                    FieldInfo field = type.GetField(
                        names[i],
                        BindingFlags.Static | BindingFlags.Public |
                        BindingFlags.NonPublic);
                    if (field != null) return field.GetValue(null);
                }
                catch { }
            }
            return null;
        }

        private static Type FindLoadedType(string typeName)
        {
            if (string.IsNullOrEmpty(typeName)) return null;
            Type result = Type.GetType(typeName, false);
            if (result != null) return result;
            Assembly[] assemblies = AppDomain.CurrentDomain.GetAssemblies();
            for (int i = 0; i < assemblies.Length; i++)
            {
                try
                {
                    result = assemblies[i].GetType(typeName, false);
                    if (result != null) return result;
                }
                catch { }
            }
            return null;
        }

        private static string BuildEquipmentJson(object characterInfo)
        {
            var items = new List<Dictionary<string, string>>();
            object slots = ReadMember(characterInfo, "slots_info");
            AddItems(
                items,
                ReadMember(slots, "object_info"),
                MaxEquipmentItems);

            if (items.Count == 0)
            {
                AddItems(
                    items,
                    ReadStaticMember(
                        FindLoadedType("GlobalStatic"),
                        "avatarEquip"),
                    MaxEquipmentItems);
            }

            if (items.Count == 0)
            {
                Type globalType = FindLoadedType("GlobalStatic");
                object avatar = ReadStaticMember(globalType, "avatarEquip");
                string[] slotsNames =
                {
                    "helmet", "outerwear", "trousers", "glove", "shoes",
                    "movable", "immobile", "immobileUp", "immobileDown",
                    "skin", "hair", "eye", "mouth", "nose", "ear", "beard"
                };
                for (int i = 0;
                    avatar != null && i < slotsNames.Length &&
                    items.Count < MaxEquipmentItems;
                    i++)
                {
                    string value = ReadIndexedString(avatar, slotsNames[i]);
                    if (string.IsNullOrEmpty(value) || value == "0") continue;
                    var item = new Dictionary<string, string>();
                    item["id"] = LimitUtf8(value, MaxItemFieldBytes);
                    item["name"] = LimitUtf8(value, MaxItemFieldBytes);
                    item["slot"] = slotsNames[i];
                    item["type"] = "avatar";
                    items.Add(item);
                }
            }
            if (items.Count != 0) return BuildItemsJson(items, true);
            lock (Sync) return _cachedEquipmentJson;
        }

        private static string BuildInventoryJson()
        {
            lock (Sync)
            {
                if (!string.IsNullOrEmpty(_cachedInventoryJson))
                    return _cachedInventoryJson;
            }
            var items = new List<Dictionary<string, string>>();
            Type type = FindLoadedType("UIPlayerWin");
            object instance = ReadStaticMember(type, "instance");
            AddItems(items, ReadMember(instance, "allItemLua"), MaxInventoryItems);
            return BuildItemsJson(items, false);
        }

        private static void AddItems(
            List<Dictionary<string, string>> items,
            object source,
            int maximum)
        {
            if (items == null || source == null || maximum <= 0) return;
            LuaTable luaTable = source as LuaTable;
            if (luaTable != null)
            {
                for (int index = 1;
                    index <= luaTable.Length && items.Count < maximum;
                    index++)
                {
                    AddItem(items, luaTable[index]);
                }
                return;
            }
            IEnumerable enumerable = source as IEnumerable;
            if (enumerable == null) return;
            try
            {
                foreach (object raw in enumerable)
                {
                    if (raw == null || items.Count >= maximum) break;
                    AddItem(items, raw);
                }
            }
            catch { }
        }

        private static void AddItem(
            List<Dictionary<string, string>> items,
            object raw)
        {
            if (items == null || raw == null) return;
            var item = new Dictionary<string, string>();
            string displayName = ReadItemString(
                raw,
                "display",
                "display_name",
                "displayName");
            string resourceName = ReadItemString(
                raw,
                "resource",
                "name");
            resourceName = NormalizeItemResourceName(resourceName);
            bool displayResolved;
            item["id"] = LimitUtf8(
                ReadItemString(
                    raw,
                    "playerItemId", "itemid", "pid", "id", "sid",
                    "object_id"),
                MaxItemFieldBytes);
            item["name"] = LimitUtf8(
                ResolveItemDisplayName(
                    displayName,
                    resourceName,
                    out displayResolved),
                MaxItemFieldBytes);
            item["resource"] = LimitUtf8(
                resourceName,
                MaxItemFieldBytes);
            item["_display_resolved"] =
                displayResolved ? "true" : "false";
            item["_tip_type"] = LimitUtf8(
                ReadItemString(raw, "type"),
                MaxItemFieldBytes);
            item["count"] = LimitUtf8(
                ReadItemString(
                    raw,
                    "quantity", "count", "num", "amount"),
                MaxItemFieldBytes);
            item["slot"] = LimitUtf8(
                ReadItemString(raw, "slot", "slot_id", "index", "type"),
                MaxItemFieldBytes);
            item["type"] = LimitUtf8(
                ReadItemString(raw, "subtype", "type", "object_type"),
                MaxItemFieldBytes);
            if (!string.IsNullOrEmpty(item["id"]) ||
                !string.IsNullOrEmpty(item["name"]))
                items.Add(item);
        }

        private static string ResolveItemDisplayName(
            string displayName,
            string resourceName,
            out bool resolved)
        {
            resolved = false;
            resourceName = NormalizeItemResourceName(resourceName);
            string localized = LocalizeItemKey(displayName);
            if (!string.IsNullOrEmpty(localized))
            {
                resolved = true;
                return localized;
            }

            localized = LocalizeItemKey(resourceName);
            if (!string.IsNullOrEmpty(localized))
            {
                resolved = true;
                return localized;
            }

            string displayKey = GetKnownItemDisplayKey(resourceName);
            localized = LocalizeItemKey(displayKey);
            if (!string.IsNullOrEmpty(localized))
            {
                resolved = true;
                return localized;
            }

            localized = LocalizeItemKey("id_weapon_" + resourceName);
            if (!string.IsNullOrEmpty(localized))
            {
                resolved = true;
                return localized;
            }
            localized = LocalizeItemKey("id_datalist_" + resourceName);
            if (!string.IsNullOrEmpty(localized))
            {
                resolved = true;
                return localized;
            }
            return resourceName;
        }

        private static string LocalizeItemKey(string key)
        {
            if (string.IsNullOrEmpty(key)) return string.Empty;
            try
            {
                TableManager manager = TableManager.Instance;
                string localized = manager == null
                    ? string.Empty
                    : manager.GetLabelText(key);
                if (!string.IsNullOrEmpty(localized) &&
                    !string.Equals(
                        localized,
                        key,
                        StringComparison.OrdinalIgnoreCase))
                    return localized;
            }
            catch { }
            try
            {
                string localized = key.valueByThisKey();
                if (!string.IsNullOrEmpty(localized) &&
                    !string.Equals(
                        localized,
                        key,
                        StringComparison.OrdinalIgnoreCase))
                    return localized;
            }
            catch { }
            return string.Empty;
        }

        private static string GetKnownItemDisplayKey(string resourceName)
        {
            switch ((resourceName ?? string.Empty).ToLowerInvariant())
            {
                case "baoxiang_tong":
                    return "id_datalist_Bronze_Chest";
                case "bengdai":
                    return "id_datalist_Bandage";
                case "bow_01":
                    return "id_datalist_Simple_Compound_Bow";
                case "food_cookies":
                    return "id_datalist_Cheerie_Cookie";
                case "grenade_01":
                    return "id_datalist_STG39_Wooden_Handle_Grenade";
                case "knives_01":
                    return "id_datalist_Rusty_Knife";
                case "leechdom_cardiac":
                    return "id_datalist_Cardiac";
                case "loudspeaker":
                    return "id_datalist_Megaphone";
                case "machinegun_01":
                    return "id_datalist_DP";
                case "machinegun_51":
                    return "id_weapon_machinegun_51";
                case "pistol_01":
                    return "id_datalist_TARGET";
                case "rpg_01":
                    return "id_datalist_Recoilless_Artillery";
                case "shield_01":
                    return "id_datalist_Buckler_Bat";
                case "shotgun_01":
                    return "id_datalist_M37";
                case "smg_01":
                    return "id_datalist_AK74";
                case "smg_51":
                    return "id_weapon_smg_51";
                case "sniperrifle_01":
                    return "id_datalist_M200";
                case "sniperrifle_51":
                    return "id_weapon_sniperrifle_51";
                case "wing03":
                case "wing03_indie":
                    return "id_common_name_wing_03";
                case "wing35":
                case "wing35_indie":
                    return "id_weapon_wing35_indie";
                default:
                    return string.Empty;
            }
        }

        private static string NormalizeItemResourceName(string resourceName)
        {
            string value = resourceName ?? string.Empty;
            int firstQuote = value.IndexOf('\'');
            if (firstQuote >= 0)
            {
                int secondQuote = value.IndexOf('\'', firstQuote + 1);
                if (secondQuote > firstQuote + 1)
                    return value.Substring(
                        firstQuote + 1,
                        secondQuote - firstQuote - 1);
            }
            return value.Trim();
        }

        private static string ReadItemString(
            object target,
            params string[] names)
        {
            string value = ReadStringMember(target, names);
            if (!string.IsNullOrEmpty(value)) return value;
            for (int index = 0; names != null && index < names.Length; index++)
            {
                value = ReadIndexedString(target, names[index]);
                if (!string.IsNullOrEmpty(value)) return value;
            }
            return string.Empty;
        }

        private static string ReadIndexedString(object target, string key)
        {
            if (target == null || string.IsNullOrEmpty(key)) return string.Empty;
            try
            {
                PropertyInfo[] properties = target.GetType().GetProperties(
                    BindingFlags.Instance | BindingFlags.Public |
                    BindingFlags.NonPublic);
                for (int i = 0; i < properties.Length; i++)
                {
                    ParameterInfo[] parameters = properties[i].GetIndexParameters();
                    if (parameters.Length != 1) continue;
                    try
                    {
                        object value = properties[i].GetValue(
                            target,
                            new object[] { key });
                        if (value != null) return value.ToString();
                    }
                    catch { }
                }
            }
            catch { }
            return string.Empty;
        }

        private static string BuildItemsJson(
            List<Dictionary<string, string>> items,
            bool reported)
        {
            if (items == null || items.Count == 0)
                return reported ? "[]" : string.Empty;
            StringBuilder result = new StringBuilder(512);
            result.Append('[');
            int encodedBytes = 1;
            for (int i = 0; i < items.Count; i++)
            {
                StringBuilder encodedItem = new StringBuilder(128);
                encodedItem.Append('{');
                bool first = true;
                foreach (KeyValuePair<string, string> field in items[i])
                {
                    if (field.Key.StartsWith("_", StringComparison.Ordinal))
                        continue;
                    if (string.IsNullOrEmpty(field.Value)) continue;
                    if (!first) encodedItem.Append(',');
                    AppendJsonString(encodedItem, field.Key);
                    encodedItem.Append(':');
                    AppendJsonString(encodedItem, field.Value);
                    first = false;
                }
                encodedItem.Append('}');
                string itemText = encodedItem.ToString();
                int itemBytes = Encoding.UTF8.GetByteCount(itemText);
                int separatorBytes = result.Length > 1 ? 1 : 0;
                if (encodedBytes + separatorBytes + itemBytes + 1 >
                    MaxItemsJsonBytes)
                    break;
                if (separatorBytes != 0) result.Append(',');
                result.Append(itemText);
                encodedBytes += separatorBytes + itemBytes;
            }
            result.Append(']');
            return result.ToString();
        }

        private static string LimitUtf8(string value, int maximumBytes)
        {
            string current = value ?? string.Empty;
            if (maximumBytes <= 0 || current.Length == 0) return string.Empty;
            if (Encoding.UTF8.GetByteCount(current) <= maximumBytes)
                return current;

            int length = Math.Min(current.Length, maximumBytes);
            while (length > 0 &&
                Encoding.UTF8.GetByteCount(current.Substring(0, length)) >
                    maximumBytes)
                length--;
            if (length > 0 && char.IsHighSurrogate(current[length - 1]))
                length--;
            return current.Substring(0, Math.Max(0, length));
        }

        private static void AppendJsonString(StringBuilder output, string value)
        {
            output.Append('"');
            string current = value ?? string.Empty;
            for (int i = 0; i < current.Length; i++)
            {
                char c = current[i];
                switch (c)
                {
                    case '"': output.Append("\\\""); break;
                    case '\\': output.Append("\\\\"); break;
                    case '\b': output.Append("\\b"); break;
                    case '\f': output.Append("\\f"); break;
                    case '\n': output.Append("\\n"); break;
                    case '\r': output.Append("\\r"); break;
                    case '\t': output.Append("\\t"); break;
                    default:
                        if (c < 32)
                            output.Append("\\u" + ((int)c).ToString("x4"));
                        else
                            output.Append(c);
                        break;
                }
            }
            output.Append('"');
        }

        private static ulong GetCharacterId(Character c)
        {
            try
            {
                if (c != null && c.character_info != null)
                {
                    return c.character_info.character_id;
                }
            }
            catch { }
            ulong parsed;
            if (TryParseUlong(
                ReadStringMember(GetLobbyConnection(), "character_id"),
                out parsed))
                return parsed;
            if (TryParseUlong(ReadGlobalString("id"), out parsed)) return parsed;
            return 0UL;
        }

        private static int GetUid(Character c)
        {
            try
            {
                if (c != null) return (int)c.uid;
            }
            catch { }
            int value;
            if (int.TryParse(
                ReadStringMember(GetLobbyConnection(), "uid"),
                out value))
                return value;
            return 0;
        }

        private static string GetName(Character c)
        {
            try
            {
                if (c != null && c.character_info != null && !string.IsNullOrEmpty(c.character_info.name))
                    return c.character_info.name;
            }
            catch { }

            try
            {
                if (c != null)
                {
                    string n = c.GetName();
                    if (!string.IsNullOrEmpty(n)) return n;
                }
            }
            catch { }

            try
            {
                if (c != null && !string.IsNullOrEmpty(c.name)) return c.name;
            }
            catch { }

            return FirstNonEmpty(
                ReadStringMember(GetLobbyConnection(), "character_name"),
                ReadGlobalString("name"));
        }

        private static string BuildFeatureString()
        {
            StringBuilder sb = new StringBuilder(256);
            AppendFeature(sb, "DLL");
            if (ESP.Enabled) AppendFeature(sb, "ESP");
            if (ESP.InfoEsp) AppendFeature(sb, "ESP_INFO");
            if (ESP.D3BoxEsp) AppendFeature(sb, "ESP_BOX");
            if (ESP.CrossEsp) AppendFeature(sb, "ESP_CROSS");
            if (ESP.CircleEsp) AppendFeature(sb, "ESP_CIRCLE");
            if (ESP.LineEsp) AppendFeature(sb, "ESP_LINE");
            if (AutoAim.Enabled) AppendFeature(sb, "AUTO_AIM");
            if (BossAutoAim.Enabled) AppendFeature(sb, "BOSS_AIM");
            if (AimTrack.Enabled) AppendFeature(sb, "AIM_TRACK");
            if (AimTrack.Wall) AppendFeature(sb, "AIM_TRACK_WALL");
            if (AimTrack.Shield) AppendFeature(sb, "AIM_TRACK_SHIELD");
            if (AimTrack.Hidden) AppendFeature(sb, "AIM_TRACK_HIDDEN");
            if (WeaponNotCD.Enabled) AppendFeature(sb, "WEAPON_NO_CD");
            if (BulletNoRecoil.Enabled) AppendFeature(sb, "NO_RECOIL");
            if (HealthBarDisplay.Enabled) AppendFeature(sb, "HEALTH_BAR");
            if (AutoLockHP.Enabled) AppendFeature(sb, "AUTO_LOCK_HP");
            if (GrenadeHalfHurt.Enabled) AppendFeature(sb, "GRENADE_HALF_HURT");
            if (GrenadeNotHurt.Enabled) AppendFeature(sb, "GRENADE_NO_HURT");
            if (NotKick.Enabled) AppendFeature(sb, "ANTI_KICK");
            if (Aike.Enabled) AppendFeature(sb, "AIKE");
            if (AutoFire.Enabled) AppendFeature(sb, "AUTO_FIRE");
            if (SpinTop.Enabled) AppendFeature(sb, "SPIN_TOP");
            if (HookMsgbox.Enabled) AppendFeature(sb, "HOOK_MESSAGE");
            if (AutoKick.Enabled) AppendFeature(sb, "AUTO_KICK");
            if (OtherC.Enabled) AppendFeature(sb, "OTHER_C");
            if (OtherC.EnabledVeryify) AppendFeature(sb, "OTHER_VERIFY");
            if (OtherC.BossEnabled) AppendFeature(sb, "OTHER_BOSS");
            if (OtherC.KnifeEnabled) AppendFeature(sb, "OTHER_KNIFE");
            if (AutoInterface.Enabled) AppendFeature(sb, "AUTO_INTERFACE");
            if (AutoInterface.BlackListEnabled)
                AppendFeature(sb, "AUTO_INTERFACE_BLACKLIST");
            if (AutoUseManager.Enabled) AppendFeature(sb, "AUTO_USE");
            if (Settings.AutoBattleEnabled) AppendFeature(sb, "AUTO_BATTLE");
            return sb.ToString();
        }

        private static void AppendFeature(StringBuilder sb, string value)
        {
            if (sb.Length > 0) sb.Append(',');
            sb.Append(value);
        }

        private static string GetClientHash()
        {
            if (!string.IsNullOrEmpty(_clientHash)) return _clientHash;

            string raw = string.Empty;
            try
            {
                if (VeriGateAuthManager.Instance != null && !string.IsNullOrEmpty(VeriGateAuthManager.Instance.DeviceID))
                    raw += VeriGateAuthManager.Instance.DeviceID;
            }
            catch { }

            try
            {
                raw += "|" + (SystemInfo.deviceUniqueIdentifier ?? string.Empty);
            }
            catch { }

            if (string.IsNullOrEmpty(raw)) raw = Environment.MachineName;
            _clientHash = Sha256Hex(raw);
            return _clientHash;
        }

        private static bool CanLookupDllUsers()
        {
            return string.Equals(GetLocalCardCode(), "Z", StringComparison.OrdinalIgnoreCase);
        }

        private static string GetLocalCardCode()
        {
            try
            {
                if (VeriGateAuthManager.Instance != null && VeriGateAuthManager.Instance.LoggedIn)
                    return "D";
            }
            catch { }

            return "U";
        }

        private static string GetCardLabel(string code)
        {
            if (string.IsNullOrEmpty(code)) return null;

            string normalized = code.Trim();
            if (normalized.Length == 0) return null;

            switch (char.ToUpperInvariant(normalized[0]))
            {
                case 'D': return "天";
                case 'M': return "月";
                case 'W': return "周";
                case 'L': return "年";
                case 'O': return "永久";
                case 'Z': return "永久";
                default: return null;
            }
        }

        private static string Sha256Hex(string value)
        {
            byte[] data = Encoding.UTF8.GetBytes(value ?? string.Empty);
            using (SHA256Managed sha = new SHA256Managed())
            {
                byte[] hash = sha.ComputeHash(data);
                StringBuilder sb = new StringBuilder(hash.Length * 2);
                for (int i = 0; i < hash.Length; i++) sb.Append(hash[i].ToString("x2"));
                return sb.ToString();
            }
        }

        private static string JoinIds(List<ulong> ids)
        {
            StringBuilder sb = new StringBuilder(ids.Count * 12);
            for (int i = 0; i < ids.Count; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append(ids[i]);
            }
            return sb.ToString();
        }

        private static string Escape(string value)
        {
            return Uri.EscapeDataString(value ?? string.Empty);
        }

        private static bool TryParseUlong(string value, out ulong result)
        {
            result = 0UL;
            if (string.IsNullOrEmpty(value)) return false;
            try
            {
                result = ulong.Parse(value);
                return true;
            }
            catch
            {
                return false;
            }
        }
    }
}
