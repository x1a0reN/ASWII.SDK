using ASWDEBUG.Logger;
using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using UnityEngine;

namespace ASWDEBUG.Cheats.AutoBattle
{
    internal sealed class RuntimeRainNavCacheRecord
    {
        public Vector3 BoundsCenter;
        public Vector3 BoundsSize;
        public int ColliderCount;
        public int GraphSize;
        public byte[] Payload;
        public long FileBytes;
        public string FilePath;
        public string PayloadSha256;
    }

    internal static class RuntimeRainNavDiskCache
    {
        internal const int SchemaVersion = 1;
        internal const int RainGraphVersion = 4;
        // Maximum-detail maps can legitimately serialize well beyond the runtime profile size.
        private const int MaxCacheBytes = 512 * 1024 * 1024;
        private const int MaxMetadataBytes = 4096;
        private static readonly byte[] Magic = Encoding.ASCII.GetBytes("ASWRNAV1");
        private static string _contentFingerprint;

        internal static string GetCachePath(string mapName)
        {
            string directory = Path.Combine(Path.Combine(Application.persistentDataPath, "ASWDEBUG"), "NavMeshCache");
            return Path.Combine(directory, SafeFileName(mapName) + ".rainnav");
        }

        internal static string GetCachePath(string mapName, bool highDetail)
        {
            string directory = Path.Combine(Path.Combine(Application.persistentDataPath, "ASWDEBUG"), "NavMeshCache");
            return Path.Combine(directory, SafeFileName(mapName) +
                (highDetail ? ".max.rainnav" : ".runtime.rainnav"));
        }

        internal static string GetContentFingerprint()
        {
            if (!string.IsNullOrEmpty(_contentFingerprint)) return _contentFingerprint;
            try
            {
                string path = Path.Combine(Application.dataPath, "FileInfo.xml");
                if (!File.Exists(path)) return "missing";
                FileInfo info = new FileInfo(path);
                using (FileStream stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                using (SHA256 sha = SHA256.Create())
                {
                    _contentFingerprint = info.Length + ":" + ToHex(sha.ComputeHash(stream));
                }
            }
            catch (Exception ex)
            {
                FileLogger.Log("AUTO-BATTLE][NAVCACHE", "fingerprint_ex=" + ex.GetType().Name + ":" +
                    SafeOneLine(ex.Message, 96));
                _contentFingerprint = "missing";
            }
            return _contentFingerprint;
        }

        internal static bool TryLoad(string mapName, string rainIdentity, string generatorSignature,
            out RuntimeRainNavCacheRecord record, out string status)
        {
            record = null;
            bool highDetail = IsHighDetailSignature(generatorSignature);
            string path = GetCachePath(mapName, highDetail);
            // Existing caches predate profile-specific file names and are maximum-detail.
            string legacyPath = GetCachePath(mapName);
            if (!File.Exists(path) && highDetail && File.Exists(legacyPath)) path = legacyPath;
            if (!File.Exists(path))
            {
                status = "miss";
                return false;
            }

            string contentFingerprint = GetContentFingerprint();
            if (contentFingerprint == "missing")
            {
                status = "content_fingerprint_missing";
                return false;
            }

            try
            {
                FileInfo info = new FileInfo(path);
                if (info.Length <= Magic.Length + 32 || info.Length > MaxCacheBytes)
                {
                    status = "invalid_file_size=" + info.Length;
                    return false;
                }

                using (FileStream stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                using (BinaryReader reader = new BinaryReader(stream, Encoding.UTF8))
                {
                    byte[] magic = reader.ReadBytes(Magic.Length);
                    if (!BytesEqual(magic, Magic))
                    {
                        status = "invalid_magic";
                        return false;
                    }
                    if (reader.ReadInt32() != SchemaVersion)
                    {
                        status = "invalid_schema";
                        return false;
                    }

                    string cachedMap = ReadString(reader);
                    string cachedRain = ReadString(reader);
                    string cachedGenerator = ReadString(reader);
                    string cachedContent = ReadString(reader);
                    if (!string.Equals(cachedMap, mapName, StringComparison.OrdinalIgnoreCase))
                    {
                        status = "map_changed";
                        return false;
                    }
                    if (!string.Equals(cachedRain, rainIdentity, StringComparison.Ordinal))
                    {
                        status = "rain_changed";
                        return false;
                    }
                    if (!string.Equals(cachedGenerator, generatorSignature, StringComparison.Ordinal))
                    {
                        status = "settings_changed";
                        return false;
                    }
                    if (!string.Equals(cachedContent, contentFingerprint, StringComparison.Ordinal))
                    {
                        status = "content_changed";
                        return false;
                    }

                    Vector3 center = ReadVector(reader);
                    Vector3 size = ReadVector(reader);
                    int colliderCount = reader.ReadInt32();
                    int graphSize = reader.ReadInt32();
                    int payloadLength = reader.ReadInt32();
                    byte[] expectedHash = reader.ReadBytes(32);
                    long remaining = stream.Length - stream.Position;
                    if (!IsFinite(center) || !IsFinite(size) || size.x <= 0f || size.y <= 0f || size.z <= 0f ||
                        colliderCount < 0 || graphSize <= 0 || payloadLength < 4 ||
                        payloadLength > MaxCacheBytes || payloadLength != remaining || expectedHash.Length != 32)
                    {
                        status = "invalid_metadata";
                        return false;
                    }

                    byte[] payload = reader.ReadBytes(payloadLength);
                    if (payload.Length != payloadLength || BitConverter.ToInt32(payload, 0) != RainGraphVersion)
                    {
                        status = "invalid_payload_version";
                        return false;
                    }
                    byte[] actualHash;
                    using (SHA256 sha = SHA256.Create()) actualHash = sha.ComputeHash(payload);
                    if (!BytesEqual(actualHash, expectedHash))
                    {
                        status = "payload_hash_mismatch";
                        return false;
                    }

                    record = new RuntimeRainNavCacheRecord
                    {
                        BoundsCenter = center,
                        BoundsSize = size,
                        ColliderCount = colliderCount,
                        GraphSize = graphSize,
                        Payload = payload,
                        FileBytes = info.Length,
                        FilePath = path,
                        PayloadSha256 = ToHex(actualHash)
                    };
                    status = "hit";
                    return true;
                }
            }
            catch (Exception ex)
            {
                status = "load_ex=" + ex.GetType().Name + ":" + SafeOneLine(ex.Message, 80);
                return false;
            }
        }

        internal static bool TrySave(string mapName, string rainIdentity, string generatorSignature,
            Vector3 boundsCenter, Vector3 boundsSize, int colliderCount, int graphSize, byte[] payload,
            out long fileBytes, out string path, out string status)
        {
            fileBytes = 0L;
            path = GetCachePath(mapName, IsHighDetailSignature(generatorSignature));
            string contentFingerprint = GetContentFingerprint();
            if (contentFingerprint == "missing")
            {
                status = "content_fingerprint_missing";
                return false;
            }
            if (payload == null || payload.Length < 4 || payload.Length > MaxCacheBytes ||
                BitConverter.ToInt32(payload, 0) != RainGraphVersion || graphSize <= 0)
            {
                status = "invalid_payload";
                return false;
            }

            string tempPath = path + ".tmp." + System.Diagnostics.Process.GetCurrentProcess().Id;
            try
            {
                string directory = Path.GetDirectoryName(path);
                if (!Directory.Exists(directory)) Directory.CreateDirectory(directory);
                byte[] payloadHash;
                using (SHA256 sha = SHA256.Create()) payloadHash = sha.ComputeHash(payload);

                using (FileStream stream = File.Open(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
                using (BinaryWriter writer = new BinaryWriter(stream, Encoding.UTF8))
                {
                    writer.Write(Magic);
                    writer.Write(SchemaVersion);
                    WriteString(writer, mapName);
                    WriteString(writer, rainIdentity);
                    WriteString(writer, generatorSignature);
                    WriteString(writer, contentFingerprint);
                    WriteVector(writer, boundsCenter);
                    WriteVector(writer, boundsSize);
                    writer.Write(colliderCount);
                    writer.Write(graphSize);
                    writer.Write(payload.Length);
                    writer.Write(payloadHash);
                    writer.Write(payload);
                    writer.Flush();
                    stream.Flush();
                }

                if (File.Exists(path))
                {
                    string backupPath = path + ".previous." + DateTime.Now.ToString("yyyyMMdd_HHmmssfff");
                    try
                    {
                        File.Replace(tempPath, path, backupPath, true);
                    }
                    catch
                    {
                        File.Copy(path, backupPath, false);
                        File.Copy(tempPath, path, true);
                        File.Delete(tempPath);
                    }
                }
                else
                {
                    File.Move(tempPath, path);
                }

                fileBytes = new FileInfo(path).Length;
                status = "saved";
                return true;
            }
            catch (Exception ex)
            {
                status = "save_ex=" + ex.GetType().Name + ":" + SafeOneLine(ex.Message, 80);
                return false;
            }
        }

        internal static string ComputePayloadSha256(byte[] payload)
        {
            if (payload == null) return string.Empty;
            using (SHA256 sha = SHA256.Create()) return ToHex(sha.ComputeHash(payload));
        }

        private static string SafeFileName(string mapName)
        {
            string value = string.IsNullOrEmpty(mapName) ? "unknown" : mapName.Trim().ToLowerInvariant();
            char[] invalid = Path.GetInvalidFileNameChars();
            StringBuilder result = new StringBuilder(value.Length);
            for (int i = 0; i < value.Length; i++)
            {
                char c = value[i];
                bool valid = char.IsLetterOrDigit(c) || c == '-' || c == '_';
                if (valid)
                {
                    for (int j = 0; j < invalid.Length; j++)
                    {
                        if (c != invalid[j]) continue;
                        valid = false;
                        break;
                    }
                }
                result.Append(valid ? c : '_');
            }
            return result.Length == 0 ? "unknown" : result.ToString();
        }

        private static bool IsHighDetailSignature(string generatorSignature)
        {
            return !string.IsNullOrEmpty(generatorSignature) &&
                   generatorSignature.IndexOf("maxDetail=1", StringComparison.Ordinal) >= 0;
        }

        private static void WriteString(BinaryWriter writer, string value)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(value ?? string.Empty);
            if (bytes.Length > MaxMetadataBytes) throw new InvalidDataException("metadata_too_long");
            writer.Write(bytes.Length);
            writer.Write(bytes);
        }

        private static string ReadString(BinaryReader reader)
        {
            int length = reader.ReadInt32();
            if (length < 0 || length > MaxMetadataBytes) throw new InvalidDataException("invalid_string_length=" + length);
            byte[] bytes = reader.ReadBytes(length);
            if (bytes.Length != length) throw new EndOfStreamException();
            return Encoding.UTF8.GetString(bytes);
        }

        private static void WriteVector(BinaryWriter writer, Vector3 value)
        {
            writer.Write(value.x);
            writer.Write(value.y);
            writer.Write(value.z);
        }

        private static Vector3 ReadVector(BinaryReader reader)
        {
            return new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
        }

        private static bool IsFinite(Vector3 value)
        {
            return !float.IsNaN(value.x) && !float.IsInfinity(value.x) &&
                   !float.IsNaN(value.y) && !float.IsInfinity(value.y) &&
                   !float.IsNaN(value.z) && !float.IsInfinity(value.z);
        }

        private static bool BytesEqual(byte[] left, byte[] right)
        {
            if (left == null || right == null || left.Length != right.Length) return false;
            int difference = 0;
            for (int i = 0; i < left.Length; i++) difference |= left[i] ^ right[i];
            return difference == 0;
        }

        private static string ToHex(byte[] value)
        {
            if (value == null) return string.Empty;
            StringBuilder builder = new StringBuilder(value.Length * 2);
            for (int i = 0; i < value.Length; i++) builder.Append(value[i].ToString("X2"));
            return builder.ToString();
        }

        private static string SafeOneLine(string value, int maxLength)
        {
            if (string.IsNullOrEmpty(value)) return "-";
            string safe = value.Replace('\r', ' ').Replace('\n', ' ').Trim();
            return safe.Length <= maxLength ? safe : safe.Substring(0, maxLength);
        }
    }
}
