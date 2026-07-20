using ASWDEBUG.Logger;
using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using UnityEngine;

namespace ASWDEBUG.Cheats.AutoBattle
{
    internal sealed class RuntimeRainDerivedCacheRecord
    {
        public readonly List<RuntimeRainOffMeshLink> Links = new List<RuntimeRainOffMeshLink>();
        public readonly List<RuntimeRainBoundarySample> Boundaries = new List<RuntimeRainBoundarySample>();
        public readonly List<RuntimeRainSurfaceSample> Surfaces = new List<RuntimeRainSurfaceSample>();
        public int ComponentCount;
        public int SafeSpawnCount;
        public long FileBytes;
        public string FilePath;
    }

    internal static class RuntimeRainNavDerivedDiskCache
    {
        internal const int SchemaVersion = 1;
        internal const int PayloadVersion = 1;
        private const int MaxCacheBytes = 512 * 1024 * 1024;
        private const int MaxMetadataBytes = 4096;
        private static readonly byte[] Magic = Encoding.ASCII.GetBytes("ASWRMETA");

        internal static string GetCachePath(string mapName)
        {
            string basePath = RuntimeRainNavDiskCache.GetCachePath(mapName);
            return Path.ChangeExtension(basePath, ".rainmeta");
        }

        internal static bool TryLoad(string mapName, string rainIdentity, string graphSignature,
            int graphSize, out RuntimeRainDerivedCacheRecord record, out string status)
        {
            record = null;
            string path = GetCachePath(mapName);
            if (!File.Exists(path))
            {
                status = "miss";
                return false;
            }

            try
            {
                FileInfo info = new FileInfo(path);
                if (info.Length <= Magic.Length + 64 || info.Length > MaxCacheBytes)
                {
                    status = "invalid_file_size=" + info.Length;
                    return false;
                }

                using (FileStream stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                using (BinaryReader reader = new BinaryReader(stream, Encoding.UTF8))
                {
                    if (!BytesEqual(reader.ReadBytes(Magic.Length), Magic))
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
                    string cachedGraph = ReadString(reader);
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
                    if (!string.Equals(cachedGraph, graphSignature, StringComparison.Ordinal))
                    {
                        status = "graph_settings_changed";
                        return false;
                    }
                    if (!string.Equals(cachedContent, RuntimeRainNavDiskCache.GetContentFingerprint(),
                        StringComparison.Ordinal))
                    {
                        status = "content_changed";
                        return false;
                    }
                    if (reader.ReadInt32() != graphSize)
                    {
                        status = "graph_size_changed";
                        return false;
                    }

                    int payloadLength = reader.ReadInt32();
                    byte[] expectedHash = reader.ReadBytes(32);
                    long remaining = stream.Length - stream.Position;
                    if (payloadLength < 16 || payloadLength > MaxCacheBytes ||
                        payloadLength != remaining || expectedHash.Length != 32)
                    {
                        status = "invalid_metadata";
                        return false;
                    }

                    byte[] payload = reader.ReadBytes(payloadLength);
                    byte[] actualHash;
                    using (SHA256 sha = SHA256.Create()) actualHash = sha.ComputeHash(payload);
                    if (!BytesEqual(expectedHash, actualHash))
                    {
                        status = "payload_hash_mismatch";
                        return false;
                    }

                    RuntimeRainDerivedCacheRecord loaded = Deserialize(payload, graphSize);
                    loaded.FileBytes = info.Length;
                    loaded.FilePath = path;
                    record = loaded;
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

        internal static bool TrySave(string mapName, string rainIdentity, string graphSignature,
            int graphSize, IList<RuntimeRainOffMeshLink> links,
            IList<RuntimeRainBoundarySample> boundaries, IList<RuntimeRainSurfaceSample> surfaces,
            int componentCount, int safeSpawnCount, out long fileBytes, out string path, out string status)
        {
            path = GetCachePath(mapName);
            fileBytes = 0L;
            string tempPath = path + ".tmp." + System.Diagnostics.Process.GetCurrentProcess().Id;
            try
            {
                byte[] payload = Serialize(links, boundaries, surfaces, componentCount, safeSpawnCount);
                if (payload.Length > MaxCacheBytes) throw new InvalidDataException("payload_too_large=" + payload.Length);
                byte[] payloadHash;
                using (SHA256 sha = SHA256.Create()) payloadHash = sha.ComputeHash(payload);

                string directory = Path.GetDirectoryName(path);
                if (!Directory.Exists(directory)) Directory.CreateDirectory(directory);
                using (FileStream stream = File.Open(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
                using (BinaryWriter writer = new BinaryWriter(stream, Encoding.UTF8))
                {
                    writer.Write(Magic);
                    writer.Write(SchemaVersion);
                    WriteString(writer, mapName);
                    WriteString(writer, rainIdentity);
                    WriteString(writer, graphSignature);
                    WriteString(writer, RuntimeRainNavDiskCache.GetContentFingerprint());
                    writer.Write(graphSize);
                    writer.Write(payload.Length);
                    writer.Write(payloadHash);
                    writer.Write(payload);
                    writer.Flush();
                    stream.Flush();
                }

                if (File.Exists(path))
                {
                    string backup = path + ".previous." + DateTime.Now.ToString("yyyyMMdd_HHmmssfff");
                    try { File.Replace(tempPath, path, backup, true); }
                    catch
                    {
                        File.Copy(path, backup, false);
                        File.Copy(tempPath, path, true);
                        File.Delete(tempPath);
                    }
                }
                else File.Move(tempPath, path);

                fileBytes = new FileInfo(path).Length;
                status = "saved";
                return true;
            }
            catch (Exception ex)
            {
                status = "save_ex=" + ex.GetType().Name + ":" + SafeOneLine(ex.Message, 80);
                FileLogger.Log("AUTO-BATTLE][NAVMETA", "save_failed map=" + mapName + " " + status);
                return false;
            }
        }

        private static byte[] Serialize(IList<RuntimeRainOffMeshLink> links,
            IList<RuntimeRainBoundarySample> boundaries, IList<RuntimeRainSurfaceSample> surfaces,
            int componentCount, int safeSpawnCount)
        {
            using (MemoryStream stream = new MemoryStream())
            using (BinaryWriter writer = new BinaryWriter(stream, Encoding.UTF8))
            {
                writer.Write(PayloadVersion);
                writer.Write(componentCount);
                writer.Write(safeSpawnCount);
                writer.Write(links == null ? 0 : links.Count);
                for (int i = 0; links != null && i < links.Count; i++)
                {
                    RuntimeRainOffMeshLink link = links[i];
                    writer.Write(link.FromNodeIndex);
                    writer.Write(link.ToNodeIndex);
                    WriteVector(writer, link.Start);
                    WriteVector(writer, link.End);
                    writer.Write(link.RequiredJumpHeight);
                    writer.Write(link.RequiredRunSpeed);
                    writer.Write(link.Cost);
                    writer.Write(link.Kind);
                }
                writer.Write(boundaries == null ? 0 : boundaries.Count);
                for (int i = 0; boundaries != null && i < boundaries.Count; i++)
                {
                    RuntimeRainBoundarySample boundary = boundaries[i];
                    writer.Write(boundary.NodeIndex);
                    WriteVector(writer, boundary.Position);
                    WriteVector(writer, boundary.Outward);
                    writer.Write(boundary.Component);
                    writer.Write(boundary.Width);
                }
                writer.Write(surfaces == null ? 0 : surfaces.Count);
                for (int i = 0; surfaces != null && i < surfaces.Count; i++)
                {
                    RuntimeRainSurfaceSample surface = surfaces[i];
                    writer.Write(surface.NodeIndex);
                    WriteVector(writer, surface.Position);
                    writer.Write(surface.Component);
                    writer.Write(surface.Clearance);
                    writer.Write(surface.CoverMask);
                    writer.Write(surface.Flags);
                }
                writer.Flush();
                return stream.ToArray();
            }
        }

        private static RuntimeRainDerivedCacheRecord Deserialize(byte[] payload, int graphSize)
        {
            RuntimeRainDerivedCacheRecord record = new RuntimeRainDerivedCacheRecord();
            HashSet<long> linkPairs = new HashSet<long>();
            using (MemoryStream stream = new MemoryStream(payload, false))
            using (BinaryReader reader = new BinaryReader(stream, Encoding.UTF8))
            {
                if (reader.ReadInt32() != PayloadVersion) throw new InvalidDataException("payload_version");
                record.ComponentCount = ReadCount(reader, graphSize, "components");
                record.SafeSpawnCount = ReadCount(reader, graphSize, "safe_spawns");
                int linkCount = ReadCount(reader, graphSize * 12, "links");
                for (int i = 0; i < linkCount; i++)
                {
                    RuntimeRainOffMeshLink link = new RuntimeRainOffMeshLink();
                    link.FromNodeIndex = reader.ReadInt32();
                    link.ToNodeIndex = reader.ReadInt32();
                    link.Start = ReadVector(reader);
                    link.End = ReadVector(reader);
                    link.RequiredJumpHeight = reader.ReadSingle();
                    link.RequiredRunSpeed = reader.ReadSingle();
                    link.Cost = reader.ReadSingle();
                    link.Kind = reader.ReadByte();
                    ValidateNodeIndex(link.FromNodeIndex, graphSize);
                    ValidateNodeIndex(link.ToNodeIndex, graphSize);
                    if (link.FromNodeIndex == link.ToNodeIndex || !IsFinite(link.Start) ||
                        !IsFinite(link.End) || !IsFinite(link.RequiredJumpHeight) ||
                        !IsFinite(link.RequiredRunSpeed) || !IsFinite(link.Cost) ||
                        link.RequiredJumpHeight <= 0f || link.RequiredRunSpeed <= 0f || link.Cost <= 0f ||
                        (link.Kind != RuntimeRainOffMeshLink.Jump && link.Kind != RuntimeRainOffMeshLink.Drop))
                        throw new InvalidDataException("invalid_link=" + i);
                    long pair = ((long)link.FromNodeIndex << 32) | (uint)link.ToNodeIndex;
                    if (!linkPairs.Add(pair)) throw new InvalidDataException("duplicate_link=" + i);
                    record.Links.Add(link);
                }
                int boundaryCount = ReadCount(reader, graphSize, "boundaries");
                for (int i = 0; i < boundaryCount; i++)
                {
                    RuntimeRainBoundarySample boundary = new RuntimeRainBoundarySample();
                    boundary.NodeIndex = reader.ReadInt32();
                    boundary.Position = ReadVector(reader);
                    boundary.Outward = ReadVector(reader);
                    boundary.Component = reader.ReadInt32();
                    boundary.Width = reader.ReadSingle();
                    ValidateNodeIndex(boundary.NodeIndex, graphSize);
                    if (!IsFinite(boundary.Position) || !IsFinite(boundary.Outward) ||
                        !IsFinite(boundary.Width) || boundary.Width <= 0f ||
                        boundary.Component < 0 || boundary.Component >= record.ComponentCount)
                        throw new InvalidDataException("invalid_boundary=" + i);
                    record.Boundaries.Add(boundary);
                }
                int surfaceCount = ReadCount(reader, graphSize, "surfaces");
                int safeSpawnCount = 0;
                for (int i = 0; i < surfaceCount; i++)
                {
                    RuntimeRainSurfaceSample surface = new RuntimeRainSurfaceSample();
                    surface.NodeIndex = reader.ReadInt32();
                    surface.Position = ReadVector(reader);
                    surface.Component = reader.ReadInt32();
                    surface.Clearance = reader.ReadSingle();
                    surface.CoverMask = reader.ReadByte();
                    surface.Flags = reader.ReadByte();
                    ValidateNodeIndex(surface.NodeIndex, graphSize);
                    if (!IsFinite(surface.Position) || !IsFinite(surface.Clearance) ||
                        surface.Clearance < 0f || surface.Component < 0 ||
                        surface.Component >= record.ComponentCount || (surface.Flags & ~7) != 0)
                        throw new InvalidDataException("invalid_surface=" + i);
                    if ((surface.Flags & RuntimeRainSurfaceSample.SafeSpawn) != 0) safeSpawnCount++;
                    record.Surfaces.Add(surface);
                }
                if (record.ComponentCount <= 0 || record.Surfaces.Count <= 0 ||
                    record.SafeSpawnCount <= 0 || safeSpawnCount != record.SafeSpawnCount)
                    throw new InvalidDataException("invalid_derived_counts");
                if (stream.Position != stream.Length) throw new InvalidDataException("payload_trailing_bytes");
            }
            return record;
        }

        private static int ReadCount(BinaryReader reader, int maximum, string name)
        {
            int value = reader.ReadInt32();
            if (value < 0 || value > maximum) throw new InvalidDataException("invalid_" + name + "=" + value);
            return value;
        }

        private static void ValidateNodeIndex(int index, int graphSize)
        {
            if (index < 0 || index >= graphSize) throw new InvalidDataException("invalid_node=" + index);
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
            return IsFinite(value.x) && IsFinite(value.y) && IsFinite(value.z);
        }

        private static bool IsFinite(float value)
        {
            return !float.IsNaN(value) && !float.IsInfinity(value);
        }

        private static bool BytesEqual(byte[] left, byte[] right)
        {
            if (left == null || right == null || left.Length != right.Length) return false;
            int difference = 0;
            for (int i = 0; i < left.Length; i++) difference |= left[i] ^ right[i];
            return difference == 0;
        }

        private static string SafeOneLine(string value, int maxLength)
        {
            if (string.IsNullOrEmpty(value)) return "-";
            string safe = value.Replace('\r', ' ').Replace('\n', ' ').Trim();
            return safe.Length <= maxLength ? safe : safe.Substring(0, maxLength);
        }
    }
}
