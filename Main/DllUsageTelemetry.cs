using ASWDEBUG.Cheats.AimTrack;
using ASWDEBUG.Cheats.AutoAim;
using ASWDEBUG.Cheats.ESP;
using ASWDEBUG.Logger;
using ASWDEBUG.Verify;
using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using UnityEngine;

namespace ASWDEBUG.Main
{
    public static class DllUsageTelemetry
    {
        private const bool TelemetryEnabled = true;
        private const string ServerBase = "http://43.133.30.226/asw-usage";
        private const string ApiToken = "098ff34b471943c6891036a0c0b75c5fd3002b5949734a70b8aebb9e748df590";
        private const float HeartbeatInterval = 8f;
        private const float LookupInterval = 3f;
        private const float KnownTtl = 45f;
        private const int MaxLookupIds = 96;

        private struct UsageSeen
        {
            public float Seen;
            public string CardLabel;
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

        public static int KnownCount;
        public static string LastStatus = "idle";

        public static void Start()
        {
            if (!TelemetryEnabled)
            {
                _started = false;
                LastStatus = "disabled";
                return;
            }

            _started = true;
            _nextHeartbeat = 0f;
            _nextLookup = 0f;
        }

        public static void Stop()
        {
            if (!TelemetryEnabled)
            {
                _started = false;
                LastStatus = "disabled";
                return;
            }

            _started = false;
            if (_lastPlayerId != 0UL)
            {
                QueueHeartbeat(_lastPlayerId, _lastUid, _lastName, "offline", true);
            }
        }

        public static void Tick(Character localPlayer)
        {
            if (!TelemetryEnabled)
            {
                LastStatus = "disabled";
                return;
            }

            if (!_started) Start();

            float now = Time.realtimeSinceStartup;
            PruneKnown(now);

            ulong playerId = GetCharacterId(localPlayer);
            int uid = GetUid(localPlayer);
            string name = GetName(localPlayer);

            if (playerId != 0UL)
            {
                _lastPlayerId = playerId;
                _lastUid = uid;
                _lastName = name;

                if (now >= _nextHeartbeat && !_heartbeatInFlight)
                {
                    _nextHeartbeat = now + HeartbeatInterval;
                    QueueHeartbeat(playerId, uid, name, BuildFeatureString());
                }
            }

            if (!CanLookupDllUsers())
            {
                ClearKnown();
                return;
            }

            if (now >= _nextLookup && !_lookupInFlight)
            {
                List<ulong> ids = CollectEnemyIds(playerId);
                if (ids.Count > 0)
                {
                    _nextLookup = now + LookupInterval;
                    QueueLookup(ids, playerId, now);
                }
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
            string url = ServerBase.TrimEnd('/') + path;
            byte[] data = Encoding.UTF8.GetBytes(body ?? string.Empty);

            HttpWebRequest req = (HttpWebRequest)WebRequest.Create(url);
            req.Method = "POST";
            req.ProtocolVersion = HttpVersion.Version10;
            req.ContentType = "application/x-www-form-urlencoded";
            req.UserAgent = "ASWDEBUG/usage";
            req.Timeout = 2500;
            req.ReadWriteTimeout = 2500;
            req.KeepAlive = false;
            req.Headers["X-ASW-Token"] = ApiToken;

            using (Stream stream = req.GetRequestStream())
            {
                stream.Write(data, 0, data.Length);
            }

            using (HttpWebResponse resp = (HttpWebResponse)req.GetResponse())
            using (Stream rs = resp.GetResponseStream())
            using (StreamReader sr = new StreamReader(rs, Encoding.UTF8))
            {
                return sr.ReadToEnd();
            }
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
            return 0UL;
        }

        private static int GetUid(Character c)
        {
            try
            {
                if (c != null) return (int)c.uid;
            }
            catch { }
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

            return string.Empty;
        }

        private static string BuildFeatureString()
        {
            StringBuilder sb = new StringBuilder(64);
            AppendFeature(sb, "DLL");
            if (ESP.Enabled) AppendFeature(sb, "ESP");
            if (ESP.InfoEsp) AppendFeature(sb, "ESP_INFO");
            if (ESP.D3BoxEsp) AppendFeature(sb, "ESP_BOX");
            if (ESP.LineEsp) AppendFeature(sb, "ESP_LINE");
            if (AutoAim.Enabled) AppendFeature(sb, "AUTO_AIM");
            if (BossAutoAim.Enabled) AppendFeature(sb, "BOSS_AIM");
            if (AimTrack.Enabled) AppendFeature(sb, "AIM_TRACK");
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
