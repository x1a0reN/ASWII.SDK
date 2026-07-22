using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace ASWDEBUG.Cheats.AutoBattle.CompactNav
{
    internal static class CompactRainNavFormat
    {
        internal const int SchemaVersion = 1;
        internal const int SectionCount = 12;
        internal const int MaxStringBytes = 8192;
        internal const long MaxFileBytes = 512L * 1024L * 1024L;

        internal const int VerticesSection = 1;
        internal const int PolysSection = 2;
        internal const int PortalsSection = 3;
        internal const int ContoursSection = 4;
        internal const int TrianglesSection = 5;
        internal const int PolyPortalsSection = 6;
        internal const int PortalPolysSection = 7;
        internal const int LinksSection = 8;
        internal const int BoundariesSection = 9;
        internal const int SurfacesSection = 10;
        internal const int SpatialCellsSection = 11;
        internal const int SpatialPolysSection = 12;

        internal const int VertexBytes = 12;
        internal const int PolyBytes = 68;
        internal const int PortalBytes = 36;
        internal const int IndexBytes = 4;
        internal const int LinkBytes = 48;
        internal const int BoundaryBytes = 36;
        internal const int SurfaceBytes = 28;
        internal const int SpatialCellBytes = 32;

        internal const byte PolyUnwalkable = 1;
        internal const byte PortalBoundary = 1;
        internal const byte PortalMultiPoly = 2;

        private static readonly byte[] Magic = Encoding.ASCII.GetBytes("ASWNAV01");

        internal static byte[] MagicBytes
        {
            get { return (byte[])Magic.Clone(); }
        }

        internal static CompactRainNavSection[] CreateSections(CompactRainNavBuildData data)
        {
            if (data == null) throw new ArgumentNullException("data");
            CompactRainNavSection[] sections = new CompactRainNavSection[SectionCount];
            long offset = 0L;
            AddSection(sections, 0, VerticesSection, data.VertexCount, VertexBytes, ref offset);
            AddSection(sections, 1, PolysSection, Length(data.Polys), PolyBytes, ref offset);
            AddSection(sections, 2, PortalsSection, Length(data.Portals), PortalBytes, ref offset);
            AddSection(sections, 3, ContoursSection, Length(data.ContourIndices), IndexBytes, ref offset);
            AddSection(sections, 4, TrianglesSection, Length(data.TriangleIndices), IndexBytes, ref offset);
            AddSection(sections, 5, PolyPortalsSection, Length(data.PolyPortalIndices), IndexBytes, ref offset);
            AddSection(sections, 6, PortalPolysSection, Length(data.PortalPolyIndices), IndexBytes, ref offset);
            AddSection(sections, 7, LinksSection, Length(data.Links), LinkBytes, ref offset);
            AddSection(sections, 8, BoundariesSection, Length(data.Boundaries), BoundaryBytes, ref offset);
            AddSection(sections, 9, SurfacesSection, Length(data.Surfaces), SurfaceBytes, ref offset);
            AddSection(sections, 10, SpatialCellsSection, Length(data.SpatialCells), SpatialCellBytes, ref offset);
            AddSection(sections, 11, SpatialPolysSection, Length(data.SpatialPolyIndices), IndexBytes, ref offset);
            return sections;
        }

        internal static long GetPayloadLength(CompactRainNavSection[] sections)
        {
            ValidateSectionTable(sections);
            CompactRainNavSection last = sections[sections.Length - 1];
            return checked(last.Offset + last.Length);
        }

        internal static long WriteHeader(BinaryWriter writer, CompactRainNavHeader header)
        {
            if (writer == null) throw new ArgumentNullException("writer");
            ValidateHeaderForWrite(header);
            Stream stream = writer.BaseStream;
            long start = stream.Position;
            writer.Write(Magic);
            writer.Write(SchemaVersion);
            long headerLengthPosition = stream.Position;
            writer.Write(0);
            writer.Write(header.PayloadLength);
            writer.Write(new byte[32]);
            writer.Write(header.SourceNavSha256);
            writer.Write(header.SourceMetaSha256);
            WriteString(writer, header.MapName);
            WriteString(writer, header.RainIdentity);
            WriteString(writer, header.GeneratorSignature);
            WriteString(writer, header.MetaSignature);
            WriteString(writer, header.ContentFingerprint);
            WriteString(writer, header.GraphName);
            writer.Write(header.CellSize);
            writer.Write(header.AgentRadius);
            writer.Write(header.StepHeight);
            writer.Write(header.WalkableHeight);
            writer.Write(header.MinEdgeCost);
            WriteVector(writer, header.BoundsCenterX, header.BoundsCenterY, header.BoundsCenterZ);
            WriteVector(writer, header.BoundsSizeX, header.BoundsSizeY, header.BoundsSizeZ);
            writer.Write(header.ColliderCount);
            writer.Write(header.RawGraphSize);
            writer.Write(header.ComponentCount);
            writer.Write(header.SafeSpawnCount);
            writer.Write(header.Sections.Length);
            for (int i = 0; i < header.Sections.Length; i++)
            {
                CompactRainNavSection section = header.Sections[i];
                writer.Write(section.Id);
                writer.Write(section.Count);
                writer.Write(section.Offset);
                writer.Write(section.Length);
            }
            writer.Flush();
            long end = stream.Position;
            int headerLength = checked((int)(end - start));
            stream.Position = headerLengthPosition;
            writer.Write(headerLength);
            writer.Flush();
            stream.Position = end;
            return start + headerLength;
        }

        internal static CompactRainNavHeader ReadHeader(BinaryReader reader)
        {
            if (reader == null) throw new ArgumentNullException("reader");
            Stream stream = reader.BaseStream;
            long start = stream.Position;
            if (!BytesEqual(reader.ReadBytes(Magic.Length), Magic))
                throw new InvalidDataException("aswnav_magic");
            if (reader.ReadInt32() != SchemaVersion)
                throw new InvalidDataException("aswnav_schema");
            int headerLength = reader.ReadInt32();
            long payloadLength = reader.ReadInt64();
            byte[] payloadHash = ReadBytesExact(reader, 32);
            byte[] navHash = ReadBytesExact(reader, 32);
            byte[] metaHash = ReadBytesExact(reader, 32);
            CompactRainNavHeader header = new CompactRainNavHeader();
            header.HeaderLength = headerLength;
            header.PayloadLength = payloadLength;
            header.PayloadSha256 = payloadHash;
            header.SourceNavSha256 = navHash;
            header.SourceMetaSha256 = metaHash;
            header.MapName = ReadString(reader);
            header.RainIdentity = ReadString(reader);
            header.GeneratorSignature = ReadString(reader);
            header.MetaSignature = ReadString(reader);
            header.ContentFingerprint = ReadString(reader);
            header.GraphName = ReadString(reader);
            header.CellSize = reader.ReadSingle();
            header.AgentRadius = reader.ReadSingle();
            header.StepHeight = reader.ReadSingle();
            header.WalkableHeight = reader.ReadSingle();
            header.MinEdgeCost = reader.ReadSingle();
            header.BoundsCenterX = reader.ReadSingle();
            header.BoundsCenterY = reader.ReadSingle();
            header.BoundsCenterZ = reader.ReadSingle();
            header.BoundsSizeX = reader.ReadSingle();
            header.BoundsSizeY = reader.ReadSingle();
            header.BoundsSizeZ = reader.ReadSingle();
            header.ColliderCount = reader.ReadInt32();
            header.RawGraphSize = reader.ReadInt32();
            header.ComponentCount = reader.ReadInt32();
            header.SafeSpawnCount = reader.ReadInt32();
            int sectionCount = reader.ReadInt32();
            if (sectionCount != SectionCount) throw new InvalidDataException("aswnav_sections=" + sectionCount);
            header.Sections = new CompactRainNavSection[sectionCount];
            for (int i = 0; i < sectionCount; i++)
            {
                CompactRainNavSection section = new CompactRainNavSection();
                section.Id = reader.ReadInt32();
                section.Count = reader.ReadInt32();
                section.Offset = reader.ReadInt64();
                section.Length = reader.ReadInt64();
                header.Sections[i] = section;
            }
            long consumed = stream.Position - start;
            if (headerLength != consumed || headerLength <= 0)
                throw new InvalidDataException("aswnav_header_length=" + headerLength + "/" + consumed);
            ValidateHeader(header, stream.Length - stream.Position);
            return header;
        }

        internal static CompactRainNavSection FindSection(CompactRainNavHeader header, int id)
        {
            if (header == null || header.Sections == null) throw new ArgumentNullException("header");
            for (int i = 0; i < header.Sections.Length; i++)
                if (header.Sections[i].Id == id) return header.Sections[i];
            throw new InvalidDataException("aswnav_missing_section=" + id);
        }

        internal static byte[] ComputeSha256(string path)
        {
            using (FileStream stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (SHA256 sha = SHA256.Create()) return sha.ComputeHash(stream);
        }

        internal static byte[] ComputeSha256(Stream stream, long offset, long length)
        {
            if (stream == null || !stream.CanSeek || offset < 0L || length < 0L ||
                offset > stream.Length || length > stream.Length - offset)
                throw new ArgumentOutOfRangeException("offset");
            long restore = stream.Position;
            byte[] buffer = new byte[1024 * 1024];
            try
            {
                stream.Position = offset;
                using (SHA256 sha = SHA256.Create())
                {
                    long remaining = length;
                    while (remaining > 0L)
                    {
                        int requested = (int)Math.Min((long)buffer.Length, remaining);
                        int read = stream.Read(buffer, 0, requested);
                        if (read <= 0) throw new EndOfStreamException();
                        sha.TransformBlock(buffer, 0, read, buffer, 0);
                        remaining -= read;
                    }
                    sha.TransformFinalBlock(buffer, 0, 0);
                    return sha.Hash;
                }
            }
            finally
            {
                stream.Position = restore;
            }
        }

        internal static bool BytesEqual(byte[] left, byte[] right)
        {
            if (left == null || right == null || left.Length != right.Length) return false;
            int difference = 0;
            for (int i = 0; i < left.Length; i++) difference |= left[i] ^ right[i];
            return difference == 0;
        }

        internal static string ToHex(byte[] value)
        {
            if (value == null) return string.Empty;
            StringBuilder result = new StringBuilder(value.Length * 2);
            for (int i = 0; i < value.Length; i++) result.Append(value[i].ToString("X2"));
            return result.ToString();
        }

        internal static void WriteString(BinaryWriter writer, string value)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(value ?? string.Empty);
            if (bytes.Length > MaxStringBytes) throw new InvalidDataException("aswnav_string_too_long");
            writer.Write(bytes.Length);
            writer.Write(bytes);
        }

        internal static string ReadString(BinaryReader reader)
        {
            int length = reader.ReadInt32();
            if (length < 0 || length > MaxStringBytes)
                throw new InvalidDataException("aswnav_string_length=" + length);
            return Encoding.UTF8.GetString(ReadBytesExact(reader, length));
        }

        internal static byte[] ReadBytesExact(BinaryReader reader, int count)
        {
            byte[] value = reader.ReadBytes(count);
            if (value.Length != count) throw new EndOfStreamException();
            return value;
        }

        internal static bool IsFinite(float value)
        {
            return !float.IsNaN(value) && !float.IsInfinity(value);
        }

        private static void ValidateHeaderForWrite(CompactRainNavHeader header)
        {
            if (header == null) throw new ArgumentNullException("header");
            if (header.SourceNavSha256 == null || header.SourceNavSha256.Length != 32 ||
                header.SourceMetaSha256 == null || header.SourceMetaSha256.Length != 32)
                throw new InvalidDataException("aswnav_source_hash");
            if (header.Sections == null || header.Sections.Length != SectionCount)
                throw new InvalidDataException("aswnav_section_table");
            ValidateSectionTable(header.Sections);
            if (header.PayloadLength != GetPayloadLength(header.Sections))
                throw new InvalidDataException("aswnav_payload_length");
        }

        private static void ValidateHeader(CompactRainNavHeader header, long remaining)
        {
            if (header.HeaderLength <= 0 || header.PayloadLength <= 0L ||
                header.PayloadLength > MaxFileBytes || remaining != header.PayloadLength)
                throw new InvalidDataException("aswnav_file_length=" + remaining + "/" + header.PayloadLength);
            if (header.PayloadSha256 == null || header.PayloadSha256.Length != 32 ||
                header.SourceNavSha256 == null || header.SourceNavSha256.Length != 32 ||
                header.SourceMetaSha256 == null || header.SourceMetaSha256.Length != 32)
                throw new InvalidDataException("aswnav_hash_length");
            if (!string.Equals(header.MapName, "level33", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("aswnav_map=" + header.MapName);
            if (!IsFinite(header.CellSize) || Math.Abs(header.CellSize - 0.10f) > 0.0001f ||
                !IsFinite(header.AgentRadius) || header.AgentRadius <= 0f ||
                !IsFinite(header.StepHeight) || header.StepHeight <= 0f ||
                !IsFinite(header.WalkableHeight) || header.WalkableHeight <= 0f ||
                !IsFinite(header.MinEdgeCost) || header.MinEdgeCost <= 0f ||
                header.RawGraphSize <= 0 || header.ComponentCount <= 0 || header.SafeSpawnCount <= 0)
                throw new InvalidDataException("aswnav_header_values");
            ValidateSectionTable(header.Sections);
            if (GetPayloadLength(header.Sections) != header.PayloadLength)
                throw new InvalidDataException("aswnav_section_length");
            ValidateSectionSize(header, VerticesSection, VertexBytes);
            ValidateSectionSize(header, PolysSection, PolyBytes);
            ValidateSectionSize(header, PortalsSection, PortalBytes);
            ValidateSectionSize(header, ContoursSection, IndexBytes);
            ValidateSectionSize(header, TrianglesSection, IndexBytes);
            ValidateSectionSize(header, PolyPortalsSection, IndexBytes);
            ValidateSectionSize(header, PortalPolysSection, IndexBytes);
            ValidateSectionSize(header, LinksSection, LinkBytes);
            ValidateSectionSize(header, BoundariesSection, BoundaryBytes);
            ValidateSectionSize(header, SurfacesSection, SurfaceBytes);
            ValidateSectionSize(header, SpatialCellsSection, SpatialCellBytes);
            ValidateSectionSize(header, SpatialPolysSection, IndexBytes);
        }

        private static void ValidateSectionTable(CompactRainNavSection[] sections)
        {
            if (sections == null || sections.Length != SectionCount)
                throw new InvalidDataException("aswnav_section_count");
            long expectedOffset = 0L;
            bool[] ids = new bool[SectionCount + 1];
            for (int i = 0; i < sections.Length; i++)
            {
                CompactRainNavSection section = sections[i];
                if (section.Id < 1 || section.Id > SectionCount || ids[section.Id] ||
                    section.Count < 0 || section.Offset != expectedOffset || section.Length < 0L)
                    throw new InvalidDataException("aswnav_section=" + i);
                ids[section.Id] = true;
                expectedOffset = checked(expectedOffset + section.Length);
            }
        }

        private static void ValidateSectionSize(CompactRainNavHeader header, int id, int itemBytes)
        {
            CompactRainNavSection section = FindSection(header, id);
            if (section.Length != checked((long)section.Count * itemBytes))
                throw new InvalidDataException("aswnav_section_size=" + id);
        }

        private static void AddSection(CompactRainNavSection[] sections, int index, int id,
            int count, int itemBytes, ref long offset)
        {
            if (count < 0) throw new InvalidDataException("aswnav_negative_count=" + id);
            long length = checked((long)count * itemBytes);
            CompactRainNavSection section = new CompactRainNavSection();
            section.Id = id;
            section.Count = count;
            section.Offset = offset;
            section.Length = length;
            sections[index] = section;
            offset = checked(offset + length);
        }

        private static int Length<T>(T[] value)
        {
            return value == null ? 0 : value.Length;
        }

        private static void WriteVector(BinaryWriter writer, float x, float y, float z)
        {
            writer.Write(x);
            writer.Write(y);
            writer.Write(z);
        }
    }

    internal sealed class CompactRainNavHeader
    {
        public int HeaderLength;
        public long PayloadLength;
        public byte[] PayloadSha256;
        public byte[] SourceNavSha256;
        public byte[] SourceMetaSha256;
        public string MapName;
        public string RainIdentity;
        public string GeneratorSignature;
        public string MetaSignature;
        public string ContentFingerprint;
        public string GraphName;
        public float CellSize;
        public float AgentRadius;
        public float StepHeight;
        public float WalkableHeight;
        public float MinEdgeCost;
        public float BoundsCenterX;
        public float BoundsCenterY;
        public float BoundsCenterZ;
        public float BoundsSizeX;
        public float BoundsSizeY;
        public float BoundsSizeZ;
        public int ColliderCount;
        public int RawGraphSize;
        public int ComponentCount;
        public int SafeSpawnCount;
        public CompactRainNavSection[] Sections;
    }

    internal struct CompactRainNavSection
    {
        public int Id;
        public int Count;
        public long Offset;
        public long Length;
    }

    internal struct CompactRainNavPolyRecord
    {
        public int ContourStart;
        public int ContourCount;
        public int TriangleStart;
        public int TriangleCount;
        public int PortalStart;
        public int PortalCount;
        public int Component;
        public int Flags;
        public float CenterX;
        public float CenterY;
        public float CenterZ;
        public float BoundsCenterX;
        public float BoundsCenterY;
        public float BoundsCenterZ;
        public float BoundsSizeX;
        public float BoundsSizeY;
        public float BoundsSizeZ;
    }

    internal struct CompactRainNavPortalRecord
    {
        public int VertexOne;
        public int VertexTwo;
        public int PolyStart;
        public int PolyCount;
        public int Pairing;
        public int Flags;
        public float CenterX;
        public float CenterY;
        public float CenterZ;
    }

    internal struct CompactRainNavLinkRecord
    {
        public int FromPortal;
        public int ToPortal;
        public float StartX;
        public float StartY;
        public float StartZ;
        public float EndX;
        public float EndY;
        public float EndZ;
        public float RequiredJumpHeight;
        public float RequiredRunSpeed;
        public float Cost;
        public byte Kind;
    }

    internal struct CompactRainNavBoundaryRecord
    {
        public int PortalIndex;
        public float PositionX;
        public float PositionY;
        public float PositionZ;
        public float OutwardX;
        public float OutwardY;
        public float OutwardZ;
        public int Component;
        public float Width;
    }

    internal struct CompactRainNavSurfaceRecord
    {
        public int PolyIndex;
        public float PositionX;
        public float PositionY;
        public float PositionZ;
        public int Component;
        public float Clearance;
        public byte CoverMask;
        public byte Flags;
    }

    internal struct CompactRainSpatialCellRecord
    {
        public int X;
        public int Y;
        public int Z;
        public int PolyStart;
        public int PolyCount;
        public float MinimumY;
        public float MaximumY;
        public int Reserved;

        public CompactRainSpatialCellRecord(int x, int y, int z, int polyStart, int polyCount,
            float minimumY, float maximumY)
        {
            X = x;
            Y = y;
            Z = z;
            PolyStart = polyStart;
            PolyCount = polyCount;
            MinimumY = minimumY;
            MaximumY = maximumY;
            Reserved = 0;
        }
    }

    internal sealed class CompactRainNavBuildData
    {
        public CompactRainNavHeader Header;
        public float[] Vertices;
        public CompactRainNavPolyRecord[] Polys;
        public CompactRainNavPortalRecord[] Portals;
        public int[] ContourIndices;
        public int[] TriangleIndices;
        public int[] PolyPortalIndices;
        public int[] PortalPolyIndices;
        public CompactRainNavLinkRecord[] Links;
        public CompactRainNavBoundaryRecord[] Boundaries;
        public CompactRainNavSurfaceRecord[] Surfaces;
        public CompactRainSpatialCellRecord[] SpatialCells;
        public int[] SpatialPolyIndices;
        public int RawInvalidTopologyReferenceCount;
        public int ReplacedTopologyReferenceCount;
        public int ClosedContourEdgeCount;
        public int CanonicalComponentCount;

        public int VertexCount
        {
            get { return Vertices == null ? 0 : Vertices.Length / 3; }
        }
    }
}
