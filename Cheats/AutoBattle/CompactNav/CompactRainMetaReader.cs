using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace ASWDEBUG.Cheats.AutoBattle.CompactNav
{
    internal static class CompactRainMetaReader
    {
        private const int SchemaVersion = 1;
        private const int PayloadVersion = 1;
        private const int MaxMetadataBytes = 4096;
        private static readonly byte[] Magic = Encoding.ASCII.GetBytes("ASWRMETA");

        internal static CompactRainMetaData Read(string path, CompactRainSourceCacheHeader navHeader,
            int[] nodeToPoly, int[] nodeToPortal, int polyCount, int portalCount)
        {
            if (string.IsNullOrEmpty(path)) throw new ArgumentNullException("path");
            if (navHeader == null) throw new ArgumentNullException("navHeader");
            if (nodeToPoly == null || nodeToPortal == null || nodeToPoly.Length != navHeader.GraphSize ||
                nodeToPortal.Length != navHeader.GraphSize)
                throw new InvalidDataException("rainmeta_node_maps");

            CompactRainSourceCacheHeader metaHeader;
            CompactRainMetaData result = new CompactRainMetaData();
            using (FileStream stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (BinaryReader reader = new BinaryReader(stream, Encoding.UTF8))
            {
                metaHeader = ReadOuterHeader(reader, path);
                ValidateIdentity(metaHeader, navHeader);
                byte[] actualPayloadHash = CompactRainNavFormat.ComputeSha256(stream,
                    metaHeader.PayloadOffset, metaHeader.PayloadLength);
                if (!CompactRainNavFormat.BytesEqual(actualPayloadHash, metaHeader.PayloadSha256))
                    throw new InvalidDataException("rainmeta_payload_hash");

                stream.Position = metaHeader.PayloadOffset;
                if (reader.ReadInt32() != PayloadVersion)
                    throw new InvalidDataException("rainmeta_payload_version");
                result.ComponentCount = ReadCount(reader, polyCount, "components");
                result.SafeSpawnCount = ReadCount(reader, polyCount, "safe_spawns");

                int linkCount = ReadCount(reader, checked(portalCount * 12), "links");
                result.Links = new CompactRainNavLinkRecord[linkCount];
                HashSet<long> linkPairs = new HashSet<long>();
                for (int i = 0; i < linkCount; i++)
                {
                    int fromNode = reader.ReadInt32();
                    int toNode = reader.ReadInt32();
                    CompactRainNavLinkRecord link = new CompactRainNavLinkRecord();
                    link.FromPortal = MapNode(nodeToPortal, fromNode, "link_from");
                    link.ToPortal = MapNode(nodeToPortal, toNode, "link_to");
                    link.StartX = reader.ReadSingle();
                    link.StartY = reader.ReadSingle();
                    link.StartZ = reader.ReadSingle();
                    link.EndX = reader.ReadSingle();
                    link.EndY = reader.ReadSingle();
                    link.EndZ = reader.ReadSingle();
                    link.RequiredJumpHeight = reader.ReadSingle();
                    link.RequiredRunSpeed = reader.ReadSingle();
                    link.Cost = reader.ReadSingle();
                    link.Kind = reader.ReadByte();
                    ValidateLink(link, i, portalCount);
                    long pair = ((long)link.FromPortal << 32) | (uint)link.ToPortal;
                    if (!linkPairs.Add(pair)) throw new InvalidDataException("rainmeta_duplicate_link=" + i);
                    result.Links[i] = link;
                }

                int boundaryCount = ReadCount(reader, portalCount, "boundaries");
                result.Boundaries = new CompactRainNavBoundaryRecord[boundaryCount];
                for (int i = 0; i < boundaryCount; i++)
                {
                    int node = reader.ReadInt32();
                    CompactRainNavBoundaryRecord boundary = new CompactRainNavBoundaryRecord();
                    boundary.PortalIndex = MapNode(nodeToPortal, node, "boundary");
                    boundary.PositionX = reader.ReadSingle();
                    boundary.PositionY = reader.ReadSingle();
                    boundary.PositionZ = reader.ReadSingle();
                    boundary.OutwardX = reader.ReadSingle();
                    boundary.OutwardY = reader.ReadSingle();
                    boundary.OutwardZ = reader.ReadSingle();
                    boundary.Component = reader.ReadInt32();
                    boundary.Width = reader.ReadSingle();
                    ValidateBoundary(boundary, i, portalCount, result.ComponentCount);
                    result.Boundaries[i] = boundary;
                }

                int surfaceCount = ReadCount(reader, polyCount, "surfaces");
                if (surfaceCount != polyCount)
                    throw new InvalidDataException("rainmeta_surface_count=" + surfaceCount + "/" + polyCount);
                result.Surfaces = new CompactRainNavSurfaceRecord[surfaceCount];
                bool[] seenPolys = new bool[polyCount];
                int actualSafeSpawns = 0;
                for (int i = 0; i < surfaceCount; i++)
                {
                    int node = reader.ReadInt32();
                    CompactRainNavSurfaceRecord surface = new CompactRainNavSurfaceRecord();
                    surface.PolyIndex = MapNode(nodeToPoly, node, "surface");
                    surface.PositionX = reader.ReadSingle();
                    surface.PositionY = reader.ReadSingle();
                    surface.PositionZ = reader.ReadSingle();
                    surface.Component = reader.ReadInt32();
                    surface.Clearance = reader.ReadSingle();
                    surface.CoverMask = reader.ReadByte();
                    surface.Flags = reader.ReadByte();
                    ValidateSurface(surface, i, polyCount, result.ComponentCount);
                    if (seenPolys[surface.PolyIndex])
                        throw new InvalidDataException("rainmeta_duplicate_surface_poly=" + surface.PolyIndex);
                    seenPolys[surface.PolyIndex] = true;
                    if ((surface.Flags & 1) != 0) actualSafeSpawns++;
                    result.Surfaces[i] = surface;
                }
                if (actualSafeSpawns != result.SafeSpawnCount)
                    throw new InvalidDataException("rainmeta_safe_spawns=" + actualSafeSpawns + "/" +
                        result.SafeSpawnCount);
                if (stream.Position != metaHeader.PayloadOffset + metaHeader.PayloadLength)
                    throw new InvalidDataException("rainmeta_payload_trailing_bytes");
            }
            metaHeader.FileSha256 = CompactRainNavFormat.ComputeSha256(path);
            result.Header = metaHeader;
            return result;
        }

        private static CompactRainSourceCacheHeader ReadOuterHeader(BinaryReader reader, string path)
        {
            Stream stream = reader.BaseStream;
            if (!CompactRainNavFormat.BytesEqual(reader.ReadBytes(Magic.Length), Magic))
                throw new InvalidDataException("rainmeta_magic");
            if (reader.ReadInt32() != SchemaVersion)
                throw new InvalidDataException("rainmeta_schema");
            CompactRainSourceCacheHeader header = new CompactRainSourceCacheHeader();
            header.FilePath = path;
            header.MapName = ReadString(reader);
            header.RainIdentity = ReadString(reader);
            header.Signature = ReadString(reader);
            header.ContentFingerprint = ReadString(reader);
            header.GraphSize = reader.ReadInt32();
            header.PayloadLength = reader.ReadInt32();
            header.PayloadSha256 = CompactRainNavFormat.ReadBytesExact(reader, 32);
            header.PayloadOffset = stream.Position;
            if (header.GraphSize <= 0 || header.PayloadLength < 16 ||
                header.PayloadLength > CompactRainNavFormat.MaxFileBytes ||
                stream.Length - stream.Position != header.PayloadLength)
                throw new InvalidDataException("rainmeta_outer_values");
            return header;
        }

        private static void ValidateIdentity(CompactRainSourceCacheHeader meta,
            CompactRainSourceCacheHeader nav)
        {
            if (!string.Equals(meta.MapName, "level33", StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(meta.MapName, nav.MapName, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(meta.RainIdentity, nav.RainIdentity, StringComparison.Ordinal) ||
                !string.Equals(meta.ContentFingerprint, nav.ContentFingerprint, StringComparison.Ordinal) ||
                meta.GraphSize != nav.GraphSize)
                throw new InvalidDataException("rainmeta_identity");
            string expectedPrefix = nav.Signature + "|base=" +
                CompactRainNavFormat.ToHex(nav.PayloadSha256) + "|";
            if (string.IsNullOrEmpty(meta.Signature) ||
                !meta.Signature.StartsWith(expectedPrefix, StringComparison.Ordinal))
                throw new InvalidDataException("rainmeta_signature");
        }

        private static int ReadCount(BinaryReader reader, int maximum, string name)
        {
            int value = reader.ReadInt32();
            if (value < 0 || value > maximum)
                throw new InvalidDataException("rainmeta_" + name + "=" + value);
            return value;
        }

        private static int MapNode(int[] mapping, int node, string name)
        {
            if (node < 0 || node >= mapping.Length || mapping[node] < 0)
                throw new InvalidDataException("rainmeta_" + name + "_node=" + node);
            return mapping[node];
        }

        private static void ValidateLink(CompactRainNavLinkRecord link, int index, int portalCount)
        {
            if (link.FromPortal < 0 || link.FromPortal >= portalCount ||
                link.ToPortal < 0 || link.ToPortal >= portalCount ||
                link.FromPortal == link.ToPortal || !FiniteVector(link.StartX, link.StartY, link.StartZ) ||
                !FiniteVector(link.EndX, link.EndY, link.EndZ) ||
                !CompactRainNavFormat.IsFinite(link.RequiredJumpHeight) || link.RequiredJumpHeight <= 0f ||
                !CompactRainNavFormat.IsFinite(link.RequiredRunSpeed) || link.RequiredRunSpeed <= 0f ||
                !CompactRainNavFormat.IsFinite(link.Cost) || link.Cost <= 0f ||
                (link.Kind != 1 && link.Kind != 2))
                throw new InvalidDataException("rainmeta_link=" + index);
        }

        private static void ValidateBoundary(CompactRainNavBoundaryRecord boundary, int index,
            int portalCount, int componentCount)
        {
            if (boundary.PortalIndex < 0 || boundary.PortalIndex >= portalCount ||
                !FiniteVector(boundary.PositionX, boundary.PositionY, boundary.PositionZ) ||
                !FiniteVector(boundary.OutwardX, boundary.OutwardY, boundary.OutwardZ) ||
                boundary.Component < 0 || boundary.Component >= componentCount ||
                !CompactRainNavFormat.IsFinite(boundary.Width) || boundary.Width <= 0f)
                throw new InvalidDataException("rainmeta_boundary=" + index);
        }

        private static void ValidateSurface(CompactRainNavSurfaceRecord surface, int index,
            int polyCount, int componentCount)
        {
            if (surface.PolyIndex < 0 || surface.PolyIndex >= polyCount ||
                !FiniteVector(surface.PositionX, surface.PositionY, surface.PositionZ) ||
                surface.Component < 0 || surface.Component >= componentCount ||
                !CompactRainNavFormat.IsFinite(surface.Clearance) || surface.Clearance < 0f ||
                (surface.Flags & ~7) != 0)
                throw new InvalidDataException("rainmeta_surface=" + index);
        }

        private static bool FiniteVector(float x, float y, float z)
        {
            return CompactRainNavFormat.IsFinite(x) && CompactRainNavFormat.IsFinite(y) &&
                CompactRainNavFormat.IsFinite(z);
        }

        private static string ReadString(BinaryReader reader)
        {
            int length = reader.ReadInt32();
            if (length < 0 || length > MaxMetadataBytes)
                throw new InvalidDataException("rainmeta_string_length=" + length);
            return Encoding.UTF8.GetString(CompactRainNavFormat.ReadBytesExact(reader, length));
        }
    }

    internal sealed class CompactRainSourceCacheHeader
    {
        public string FilePath;
        public byte[] FileSha256;
        public string MapName;
        public string RainIdentity;
        public string Signature;
        public string ContentFingerprint;
        public int GraphSize;
        public long PayloadOffset;
        public int PayloadLength;
        public byte[] PayloadSha256;
        public float BoundsCenterX;
        public float BoundsCenterY;
        public float BoundsCenterZ;
        public float BoundsSizeX;
        public float BoundsSizeY;
        public float BoundsSizeZ;
        public int ColliderCount;
    }

    internal sealed class CompactRainMetaData
    {
        public CompactRainSourceCacheHeader Header;
        public int ComponentCount;
        public int SafeSpawnCount;
        public CompactRainNavLinkRecord[] Links;
        public CompactRainNavBoundaryRecord[] Boundaries;
        public CompactRainNavSurfaceRecord[] Surfaces;
    }
}
