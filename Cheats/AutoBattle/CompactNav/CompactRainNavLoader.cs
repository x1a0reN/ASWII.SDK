using System;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace ASWDEBUG.Cheats.AutoBattle.CompactNav
{
    internal static class CompactRainNavLoader
    {
        private static readonly object Sync = new object();
        private static CompactRainNavDataset _processDataset;
        private static CompactRainNavLoadResult _processResult;
        private static string _processPath = string.Empty;
        private static bool _processLoadAttempted;

        internal static bool TryLoadProcessSingleton(string path, out CompactRainNavDataset dataset,
            out CompactRainNavLoadResult result)
        {
            lock (Sync)
            {
                string fullPath = string.IsNullOrEmpty(path) ? string.Empty : Path.GetFullPath(path);
                if (_processDataset != null)
                {
                    dataset = _processDataset;
                    result = _processResult;
                    return string.Equals(fullPath, _processPath, StringComparison.OrdinalIgnoreCase);
                }
                if (_processLoadAttempted)
                {
                    dataset = null;
                    result = _processResult;
                    return false;
                }
                _processLoadAttempted = true;
                _processPath = fullPath;
                try
                {
                    _processDataset = Load(fullPath, out _processResult);
                }
                catch (Exception ex)
                {
                    _processResult = new CompactRainNavLoadResult();
                    _processResult.FilePath = fullPath;
                    _processResult.Status = "load_ex=" + ex.GetType().Name + ":" + SafeOneLine(ex.Message, 180);
                }
                dataset = _processDataset;
                result = _processResult;
                return dataset != null;
            }
        }

        internal static CompactRainNavDataset Load(string path, out CompactRainNavLoadResult result)
        {
            if (string.IsNullOrEmpty(path)) throw new ArgumentNullException("path");
            FileInfo info = new FileInfo(path);
            if (!info.Exists || info.Length <= 0L || info.Length > CompactRainNavFormat.MaxFileBytes)
                throw new InvalidDataException("aswnav_file_size=" + (info.Exists ? info.Length : -1L));
            Stopwatch stopwatch = Stopwatch.StartNew();
            long managedBefore = GC.GetTotalMemory(false);
            long privateBefore = GetPrivateBytes();
            CompactRainNavDataset dataset;
            string fileHash;
            using (FileStream stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (BinaryReader reader = new BinaryReader(stream, Encoding.UTF8))
            {
                CompactRainNavHeader header = CompactRainNavFormat.ReadHeader(reader);
                byte[] payloadHash = CompactRainNavFormat.ComputeSha256(stream,
                    header.HeaderLength, header.PayloadLength);
                if (!CompactRainNavFormat.BytesEqual(payloadHash, header.PayloadSha256))
                    throw new InvalidDataException("aswnav_payload_hash");
                fileHash = CompactRainNavFormat.ToHex(CompactRainNavFormat.ComputeSha256(path));

                float[] vertices = ReadVertices(reader, stream, header);
                CompactRainNavPolyRecord[] polys = ReadPolys(reader, stream, header);
                CompactRainNavPortalRecord[] portals = ReadPortals(reader, stream, header);
                int[] contours = ReadIndices(reader, stream, header,
                    CompactRainNavFormat.ContoursSection, vertices.Length / 3, "contour");
                int[] triangles = ReadIndices(reader, stream, header,
                    CompactRainNavFormat.TrianglesSection, vertices.Length / 3, "triangle");
                int[] polyPortals = ReadIndices(reader, stream, header,
                    CompactRainNavFormat.PolyPortalsSection, portals.Length, "poly_portal");
                int[] portalPolys = ReadIndices(reader, stream, header,
                    CompactRainNavFormat.PortalPolysSection, polys.Length, "portal_poly");
                CompactRainNavLinkRecord[] links = ReadLinks(reader, stream, header, portals.Length);
                CompactRainNavBoundaryRecord[] boundaries = ReadBoundaries(reader, stream, header,
                    portals, header.ComponentCount);
                CompactRainNavSurfaceRecord[] surfaces = ReadSurfaces(reader, stream, header,
                    polys, header.ComponentCount, header.SafeSpawnCount);
                SkipReservedSpatialSections(stream, header);
                ValidateRanges(polys, portals, contours, triangles, polyPortals, portalPolys);
                dataset = new CompactRainNavDataset(header, vertices, polys, portals, contours,
                    triangles, polyPortals, portalPolys, links, boundaries, surfaces);
            }
            stopwatch.Stop();
            result = new CompactRainNavLoadResult();
            result.FilePath = Path.GetFullPath(path);
            result.FileBytes = info.Length;
            result.FileSha256 = fileHash;
            result.ElapsedMilliseconds = stopwatch.ElapsedMilliseconds;
            result.ManagedBytesBefore = managedBefore;
            result.ManagedBytesAfter = GC.GetTotalMemory(false);
            result.PrivateBytesBefore = privateBefore;
            result.PrivateBytesAfter = GetPrivateBytes();
            result.ResidentDatasetBytes = dataset.ResidentBytes;
            result.BvhNodeCount = dataset.SpatialIndex.NodeCount;
            result.Status = "loaded";
            return dataset;
        }

        private static float[] ReadVertices(BinaryReader reader, Stream stream,
            CompactRainNavHeader header)
        {
            CompactRainNavSection section = PositionAt(stream, header, CompactRainNavFormat.VerticesSection);
            float[] values = new float[checked(section.Count * 3)];
            for (int i = 0; i < values.Length; i++)
            {
                values[i] = reader.ReadSingle();
                if (!CompactRainNavFormat.IsFinite(values[i]))
                    throw new InvalidDataException("aswnav_vertex=" + i);
            }
            AssertSectionEnd(stream, header, section);
            return values;
        }

        private static CompactRainNavPolyRecord[] ReadPolys(BinaryReader reader, Stream stream,
            CompactRainNavHeader header)
        {
            CompactRainNavSection section = PositionAt(stream, header, CompactRainNavFormat.PolysSection);
            CompactRainNavPolyRecord[] values = new CompactRainNavPolyRecord[section.Count];
            for (int i = 0; i < values.Length; i++)
            {
                CompactRainNavPolyRecord value = new CompactRainNavPolyRecord();
                value.ContourStart = reader.ReadInt32(); value.ContourCount = reader.ReadInt32();
                value.TriangleStart = reader.ReadInt32(); value.TriangleCount = reader.ReadInt32();
                value.PortalStart = reader.ReadInt32(); value.PortalCount = reader.ReadInt32();
                value.Component = reader.ReadInt32(); value.Flags = reader.ReadInt32();
                value.CenterX = reader.ReadSingle(); value.CenterY = reader.ReadSingle(); value.CenterZ = reader.ReadSingle();
                value.BoundsCenterX = reader.ReadSingle(); value.BoundsCenterY = reader.ReadSingle();
                value.BoundsCenterZ = reader.ReadSingle(); value.BoundsSizeX = reader.ReadSingle();
                value.BoundsSizeY = reader.ReadSingle(); value.BoundsSizeZ = reader.ReadSingle();
                if (value.ContourStart < 0 || value.ContourCount < 3 || value.TriangleStart < 0 ||
                    value.TriangleCount < 3 || (value.TriangleCount % 3) != 0 || value.PortalStart < 0 ||
                    value.PortalCount != value.ContourCount || value.Component < 0 ||
                    value.Component >= header.ComponentCount ||
                    (value.Flags & ~CompactRainNavFormat.PolyUnwalkable) != 0 ||
                    !FiniteVector(value.CenterX, value.CenterY, value.CenterZ) ||
                    !FiniteVector(value.BoundsCenterX, value.BoundsCenterY, value.BoundsCenterZ) ||
                    !FiniteVector(value.BoundsSizeX, value.BoundsSizeY, value.BoundsSizeZ) ||
                    value.BoundsSizeX < 0f || value.BoundsSizeY < 0f || value.BoundsSizeZ < 0f)
                    throw new InvalidDataException("aswnav_poly=" + i);
                values[i] = value;
            }
            AssertSectionEnd(stream, header, section);
            return values;
        }

        private static CompactRainNavPortalRecord[] ReadPortals(BinaryReader reader, Stream stream,
            CompactRainNavHeader header)
        {
            CompactRainNavSection section = PositionAt(stream, header, CompactRainNavFormat.PortalsSection);
            int vertexCount = CompactRainNavFormat.FindSection(header, CompactRainNavFormat.VerticesSection).Count;
            CompactRainNavPortalRecord[] values = new CompactRainNavPortalRecord[section.Count];
            for (int i = 0; i < values.Length; i++)
            {
                CompactRainNavPortalRecord value = new CompactRainNavPortalRecord();
                value.VertexOne = reader.ReadInt32(); value.VertexTwo = reader.ReadInt32();
                value.PolyStart = reader.ReadInt32(); value.PolyCount = reader.ReadInt32();
                value.Pairing = reader.ReadInt32(); value.Flags = reader.ReadInt32();
                value.CenterX = reader.ReadSingle(); value.CenterY = reader.ReadSingle(); value.CenterZ = reader.ReadSingle();
                int expectedFlags = value.PolyCount == 1 ? CompactRainNavFormat.PortalBoundary : 0;
                if (value.PolyCount > 2) expectedFlags |= CompactRainNavFormat.PortalMultiPoly;
                if (value.VertexOne < 0 || value.VertexOne >= vertexCount || value.VertexTwo < 0 ||
                    value.VertexTwo >= vertexCount || value.VertexOne == value.VertexTwo || value.PolyStart < 0 ||
                    value.PolyCount <= 0 || value.Flags != expectedFlags ||
                    !FiniteVector(value.CenterX, value.CenterY, value.CenterZ))
                    throw new InvalidDataException("aswnav_portal=" + i);
                values[i] = value;
            }
            AssertSectionEnd(stream, header, section);
            return values;
        }

        private static int[] ReadIndices(BinaryReader reader, Stream stream, CompactRainNavHeader header,
            int sectionId, int maximumExclusive, string name)
        {
            CompactRainNavSection section = PositionAt(stream, header, sectionId);
            int[] values = new int[section.Count];
            for (int i = 0; i < values.Length; i++)
            {
                values[i] = reader.ReadInt32();
                if (values[i] < 0 || values[i] >= maximumExclusive)
                    throw new InvalidDataException("aswnav_" + name + "=" + values[i]);
            }
            AssertSectionEnd(stream, header, section);
            return values;
        }

        private static CompactRainNavLinkRecord[] ReadLinks(BinaryReader reader, Stream stream,
            CompactRainNavHeader header, int portalCount)
        {
            CompactRainNavSection section = PositionAt(stream, header, CompactRainNavFormat.LinksSection);
            CompactRainNavLinkRecord[] values = new CompactRainNavLinkRecord[section.Count];
            for (int i = 0; i < values.Length; i++)
            {
                CompactRainNavLinkRecord value = new CompactRainNavLinkRecord();
                value.FromPortal = reader.ReadInt32(); value.ToPortal = reader.ReadInt32();
                value.StartX = reader.ReadSingle(); value.StartY = reader.ReadSingle(); value.StartZ = reader.ReadSingle();
                value.EndX = reader.ReadSingle(); value.EndY = reader.ReadSingle(); value.EndZ = reader.ReadSingle();
                value.RequiredJumpHeight = reader.ReadSingle(); value.RequiredRunSpeed = reader.ReadSingle();
                value.Cost = reader.ReadSingle(); value.Kind = reader.ReadByte();
                byte padOne = reader.ReadByte(); byte padTwo = reader.ReadByte(); byte padThree = reader.ReadByte();
                if (value.FromPortal < 0 || value.FromPortal >= portalCount || value.ToPortal < 0 ||
                    value.ToPortal >= portalCount || value.FromPortal == value.ToPortal ||
                    !FiniteVector(value.StartX, value.StartY, value.StartZ) ||
                    !FiniteVector(value.EndX, value.EndY, value.EndZ) ||
                    !CompactRainNavFormat.IsFinite(value.RequiredJumpHeight) || value.RequiredJumpHeight <= 0f ||
                    !CompactRainNavFormat.IsFinite(value.RequiredRunSpeed) || value.RequiredRunSpeed <= 0f ||
                    !CompactRainNavFormat.IsFinite(value.Cost) || value.Cost <= 0f ||
                    (value.Kind != 1 && value.Kind != 2) || (padOne | padTwo | padThree) != 0)
                    throw new InvalidDataException("aswnav_link=" + i);
                values[i] = value;
            }
            AssertSectionEnd(stream, header, section);
            return values;
        }

        private static CompactRainNavBoundaryRecord[] ReadBoundaries(BinaryReader reader, Stream stream,
            CompactRainNavHeader header, CompactRainNavPortalRecord[] portals, int componentCount)
        {
            CompactRainNavSection section = PositionAt(stream, header, CompactRainNavFormat.BoundariesSection);
            CompactRainNavBoundaryRecord[] values = new CompactRainNavBoundaryRecord[section.Count];
            for (int i = 0; i < values.Length; i++)
            {
                CompactRainNavBoundaryRecord value = new CompactRainNavBoundaryRecord();
                value.PortalIndex = reader.ReadInt32();
                value.PositionX = reader.ReadSingle(); value.PositionY = reader.ReadSingle(); value.PositionZ = reader.ReadSingle();
                value.OutwardX = reader.ReadSingle(); value.OutwardY = reader.ReadSingle(); value.OutwardZ = reader.ReadSingle();
                value.Component = reader.ReadInt32(); value.Width = reader.ReadSingle();
                if (value.PortalIndex < 0 || value.PortalIndex >= portals.Length ||
                    (portals[value.PortalIndex].Flags & CompactRainNavFormat.PortalBoundary) == 0 ||
                    !FiniteVector(value.PositionX, value.PositionY, value.PositionZ) ||
                    !FiniteVector(value.OutwardX, value.OutwardY, value.OutwardZ) ||
                    value.Component < 0 || value.Component >= componentCount ||
                    !CompactRainNavFormat.IsFinite(value.Width) || value.Width <= 0f)
                    throw new InvalidDataException("aswnav_boundary=" + i);
                values[i] = value;
            }
            AssertSectionEnd(stream, header, section);
            return values;
        }

        private static CompactRainNavSurfaceRecord[] ReadSurfaces(BinaryReader reader, Stream stream,
            CompactRainNavHeader header, CompactRainNavPolyRecord[] polys, int componentCount,
            int expectedSafeSpawns)
        {
            CompactRainNavSection section = PositionAt(stream, header, CompactRainNavFormat.SurfacesSection);
            if (section.Count != polys.Length) throw new InvalidDataException("aswnav_surfaces=" + section.Count);
            CompactRainNavSurfaceRecord[] values = new CompactRainNavSurfaceRecord[section.Count];
            int safeSpawns = 0;
            for (int i = 0; i < values.Length; i++)
            {
                CompactRainNavSurfaceRecord value = new CompactRainNavSurfaceRecord();
                value.PolyIndex = reader.ReadInt32();
                value.PositionX = reader.ReadSingle(); value.PositionY = reader.ReadSingle(); value.PositionZ = reader.ReadSingle();
                value.Component = reader.ReadInt32(); value.Clearance = reader.ReadSingle();
                value.CoverMask = reader.ReadByte(); value.Flags = reader.ReadByte();
                byte padOne = reader.ReadByte(); byte padTwo = reader.ReadByte();
                if (value.PolyIndex != i || value.Component != polys[i].Component ||
                    value.Component < 0 || value.Component >= componentCount ||
                    !FiniteVector(value.PositionX, value.PositionY, value.PositionZ) ||
                    !CompactRainNavFormat.IsFinite(value.Clearance) || value.Clearance < 0f ||
                    (value.Flags & ~7) != 0 || (padOne | padTwo) != 0)
                    throw new InvalidDataException("aswnav_surface=" + i);
                if ((value.Flags & 1) != 0) safeSpawns++;
                values[i] = value;
            }
            if (safeSpawns != expectedSafeSpawns)
                throw new InvalidDataException("aswnav_safe_spawns=" + safeSpawns + "/" + expectedSafeSpawns);
            AssertSectionEnd(stream, header, section);
            return values;
        }

        private static void SkipReservedSpatialSections(Stream stream, CompactRainNavHeader header)
        {
            CompactRainNavSection cells = PositionAt(stream, header, CompactRainNavFormat.SpatialCellsSection);
            stream.Position += cells.Length;
            AssertSectionEnd(stream, header, cells);
            CompactRainNavSection polys = PositionAt(stream, header, CompactRainNavFormat.SpatialPolysSection);
            stream.Position += polys.Length;
            AssertSectionEnd(stream, header, polys);
            if (stream.Position != header.HeaderLength + header.PayloadLength)
                throw new InvalidDataException("aswnav_payload_trailing_bytes");
        }

        private static void ValidateRanges(CompactRainNavPolyRecord[] polys,
            CompactRainNavPortalRecord[] portals, int[] contours, int[] triangles,
            int[] polyPortals, int[] portalPolys)
        {
            for (int i = 0; i < polys.Length; i++)
            {
                CompactRainNavPolyRecord poly = polys[i];
                if (!RangeValid(poly.ContourStart, poly.ContourCount, contours.Length) ||
                    !RangeValid(poly.TriangleStart, poly.TriangleCount, triangles.Length) ||
                    !RangeValid(poly.PortalStart, poly.PortalCount, polyPortals.Length))
                    throw new InvalidDataException("aswnav_poly_range=" + i);
                for (int p = 0; p < poly.PortalCount; p++)
                {
                    int portalIndex = polyPortals[poly.PortalStart + p];
                    CompactRainNavPortalRecord portal = portals[portalIndex];
                    bool reverseFound = false;
                    for (int j = 0; j < portal.PolyCount; j++)
                        if (portalPolys[portal.PolyStart + j] == i) { reverseFound = true; break; }
                    if (!reverseFound) throw new InvalidDataException("aswnav_poly_topology=" + i);
                }
            }
            for (int i = 0; i < portals.Length; i++)
                if (!RangeValid(portals[i].PolyStart, portals[i].PolyCount, portalPolys.Length))
                    throw new InvalidDataException("aswnav_portal_range=" + i);
        }

        private static CompactRainNavSection PositionAt(Stream stream, CompactRainNavHeader header, int id)
        {
            CompactRainNavSection section = CompactRainNavFormat.FindSection(header, id);
            long position = checked(header.HeaderLength + section.Offset);
            if (position < header.HeaderLength || section.Length > stream.Length - position)
                throw new InvalidDataException("aswnav_section_position=" + id);
            stream.Position = position;
            return section;
        }

        private static void AssertSectionEnd(Stream stream, CompactRainNavHeader header,
            CompactRainNavSection section)
        {
            long expected = checked(header.HeaderLength + section.Offset + section.Length);
            if (stream.Position != expected)
                throw new InvalidDataException("aswnav_section_end=" + section.Id);
        }

        private static bool RangeValid(int start, int count, int length)
        {
            return start >= 0 && count >= 0 && start <= length && count <= length - start;
        }

        private static bool FiniteVector(float x, float y, float z)
        {
            return CompactRainNavFormat.IsFinite(x) && CompactRainNavFormat.IsFinite(y) &&
                CompactRainNavFormat.IsFinite(z);
        }

        private static long GetPrivateBytes()
        {
            try { return Process.GetCurrentProcess().PrivateMemorySize64; }
            catch { return 0L; }
        }

        private static string SafeOneLine(string value, int maximum)
        {
            if (string.IsNullOrEmpty(value)) return "-";
            string safe = value.Replace('\r', ' ').Replace('\n', ' ').Trim();
            return safe.Length <= maximum ? safe : safe.Substring(0, maximum);
        }
    }

    internal sealed class CompactRainNavLoadResult
    {
        public string FilePath;
        public string FileSha256;
        public string Status;
        public long FileBytes;
        public long ElapsedMilliseconds;
        public long ManagedBytesBefore;
        public long ManagedBytesAfter;
        public long PrivateBytesBefore;
        public long PrivateBytesAfter;
        public long ResidentDatasetBytes;
        public int BvhNodeCount;
    }
}
