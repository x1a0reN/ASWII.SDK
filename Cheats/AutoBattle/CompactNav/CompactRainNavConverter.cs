using System;
using System.IO;
using System.Text;

namespace ASWDEBUG.Cheats.AutoBattle.CompactNav
{
    internal static class CompactRainNavConverter
    {
        private const int NavSchemaVersion = 1;
        private const int RainGraphVersion = 4;
        private const int MaxMetadataBytes = 4096;
        private const long PayloadHashOffset = 24L;
        private static readonly byte[] NavMagic = Encoding.ASCII.GetBytes("ASWRNAV1");

        internal static bool TryConvert(string navPath, string metaPath, string outputPath,
            out CompactRainNavConversionResult result, out string status)
        {
            result = null;
            try
            {
                result = Convert(navPath, metaPath, outputPath);
                status = "converted sha256=" + result.OutputSha256 + " bytes=" + result.OutputBytes;
                return true;
            }
            catch (Exception ex)
            {
                status = "convert_ex=" + ex.GetType().Name + ":" + SafeOneLine(ex.Message, 180);
                return false;
            }
        }

        internal static CompactRainNavConversionResult Convert(string navPath, string metaPath,
            string outputPath)
        {
            ValidatePaths(navPath, metaPath, outputPath);
            CompactRainSourceCacheHeader navHeader = ReadNavOuterHeader(navPath);
            navHeader.FileSha256 = CompactRainNavFormat.ComputeSha256(navPath);

            GraphScan scan = ScanGraph(navHeader);
            CompactRainNavBuildData data = ReadGraph(navHeader, scan);
            CompactRainMetaData meta = CompactRainMetaReader.Read(metaPath, navHeader,
                scan.NodeToPoly, scan.NodeToPortal, data.Polys.Length, data.Portals.Length);
            ApplyMetadata(data, meta);
            data.Links = meta.Links;
            data.Boundaries = meta.Boundaries;
            data.SpatialCells = new CompactRainSpatialCellRecord[0];
            data.SpatialPolyIndices = new int[0];
            data.Header = CreateHeader(navHeader, meta, data, scan);
            data.Header.Sections = CompactRainNavFormat.CreateSections(data);
            data.Header.PayloadLength = CompactRainNavFormat.GetPayloadLength(data.Header.Sections);

            string directory = Path.GetDirectoryName(Path.GetFullPath(outputPath));
            if (!Directory.Exists(directory)) Directory.CreateDirectory(directory);
            string tempPath = outputPath + ".tmp." + System.Diagnostics.Process.GetCurrentProcess().Id + "." +
                DateTime.UtcNow.Ticks;
            byte[] payloadHash = WriteFile(tempPath, data);
            VerifyFile(tempPath, navHeader.FileSha256, meta.Header.FileSha256);
            CommitFile(tempPath, outputPath);

            CompactRainNavConversionResult result = new CompactRainNavConversionResult();
            result.OutputPath = Path.GetFullPath(outputPath);
            result.OutputBytes = new FileInfo(outputPath).Length;
            result.OutputSha256 = CompactRainNavFormat.ToHex(CompactRainNavFormat.ComputeSha256(outputPath));
            result.PayloadSha256 = CompactRainNavFormat.ToHex(payloadHash);
            result.SourceNavSha256 = CompactRainNavFormat.ToHex(navHeader.FileSha256);
            result.SourceMetaSha256 = CompactRainNavFormat.ToHex(meta.Header.FileSha256);
            result.VertexCount = data.VertexCount;
            result.PolyCount = data.Polys.Length;
            result.PortalCount = data.Portals.Length;
            result.LinkCount = data.Links.Length;
            result.BoundaryCount = data.Boundaries.Length;
            result.SurfaceCount = data.Surfaces.Length;
            result.ComponentCount = meta.ComponentCount;
            result.SafeSpawnCount = meta.SafeSpawnCount;
            return result;
        }

        internal static CompactRainNavHeader VerifyFile(string path, byte[] expectedNavHash,
            byte[] expectedMetaHash)
        {
            FileInfo info = new FileInfo(path);
            if (!info.Exists || info.Length <= 0L || info.Length > CompactRainNavFormat.MaxFileBytes)
                throw new InvalidDataException("aswnav_file_size=" + (info.Exists ? info.Length : -1L));
            using (FileStream stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (BinaryReader reader = new BinaryReader(stream, Encoding.UTF8))
            {
                CompactRainNavHeader header = CompactRainNavFormat.ReadHeader(reader);
                byte[] actualHash = CompactRainNavFormat.ComputeSha256(stream,
                    header.HeaderLength, header.PayloadLength);
                if (!CompactRainNavFormat.BytesEqual(actualHash, header.PayloadSha256))
                    throw new InvalidDataException("aswnav_payload_hash");
                if (expectedNavHash != null &&
                    !CompactRainNavFormat.BytesEqual(expectedNavHash, header.SourceNavSha256))
                    throw new InvalidDataException("aswnav_source_nav_hash");
                if (expectedMetaHash != null &&
                    !CompactRainNavFormat.BytesEqual(expectedMetaHash, header.SourceMetaSha256))
                    throw new InvalidDataException("aswnav_source_meta_hash");
                ValidateCrossSectionCounts(header);
                return header;
            }
        }

        private static CompactRainSourceCacheHeader ReadNavOuterHeader(string path)
        {
            using (FileStream stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (BinaryReader reader = new BinaryReader(stream, Encoding.UTF8))
            {
                if (!CompactRainNavFormat.BytesEqual(reader.ReadBytes(NavMagic.Length), NavMagic))
                    throw new InvalidDataException("rainnav_magic");
                if (reader.ReadInt32() != NavSchemaVersion)
                    throw new InvalidDataException("rainnav_schema");
                CompactRainSourceCacheHeader header = new CompactRainSourceCacheHeader();
                header.FilePath = path;
                header.MapName = ReadOuterString(reader);
                header.RainIdentity = ReadOuterString(reader);
                header.Signature = ReadOuterString(reader);
                header.ContentFingerprint = ReadOuterString(reader);
                header.BoundsCenterX = reader.ReadSingle();
                header.BoundsCenterY = reader.ReadSingle();
                header.BoundsCenterZ = reader.ReadSingle();
                header.BoundsSizeX = reader.ReadSingle();
                header.BoundsSizeY = reader.ReadSingle();
                header.BoundsSizeZ = reader.ReadSingle();
                header.ColliderCount = reader.ReadInt32();
                header.GraphSize = reader.ReadInt32();
                header.PayloadLength = reader.ReadInt32();
                header.PayloadSha256 = CompactRainNavFormat.ReadBytesExact(reader, 32);
                header.PayloadOffset = stream.Position;
                if (!string.Equals(header.MapName, "level33", StringComparison.OrdinalIgnoreCase) ||
                    string.IsNullOrEmpty(header.RainIdentity) || string.IsNullOrEmpty(header.ContentFingerprint) ||
                    header.Signature.IndexOf("maxDetail=1", StringComparison.Ordinal) < 0 ||
                    Math.Abs(ReadSignatureFloat(header.Signature, "cell") - 0.10f) > 0.0001f ||
                    header.ColliderCount < 0 || header.GraphSize <= 0 || header.PayloadLength < 4 ||
                    header.PayloadLength > CompactRainNavFormat.MaxFileBytes ||
                    stream.Length - stream.Position != header.PayloadLength ||
                    !FiniteVector(header.BoundsCenterX, header.BoundsCenterY, header.BoundsCenterZ) ||
                    !FiniteVector(header.BoundsSizeX, header.BoundsSizeY, header.BoundsSizeZ) ||
                    header.BoundsSizeX <= 0f || header.BoundsSizeY <= 0f || header.BoundsSizeZ <= 0f)
                    throw new InvalidDataException("rainnav_outer_values");
                byte[] actualHash = CompactRainNavFormat.ComputeSha256(stream,
                    header.PayloadOffset, header.PayloadLength);
                if (!CompactRainNavFormat.BytesEqual(actualHash, header.PayloadSha256))
                    throw new InvalidDataException("rainnav_payload_hash");
                return header;
            }
        }

        private static GraphScan ScanGraph(CompactRainSourceCacheHeader navHeader)
        {
            using (FileStream stream = File.Open(navHeader.FilePath, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (BinaryReader reader = new BinaryReader(stream))
            {
                stream.Position = navHeader.PayloadOffset;
                GraphPreamble preamble = ReadGraphPreamble(reader, navHeader.GraphSize, false);
                GraphScan scan = new GraphScan();
                scan.NodeTypes = preamble.NodeTypes;
                scan.NodeToPoly = CreateFilledArray(navHeader.GraphSize, -1);
                scan.NodeToPortal = CreateFilledArray(navHeader.GraphSize, -1);
                scan.VertexCount = preamble.VertexCount;
                scan.GraphName = preamble.GraphName;
                scan.MinEdgeCost = preamble.MinEdgeCost;
                int polyCount = 0;
                int portalCount = 0;
                for (int i = 0; i < scan.NodeTypes.Length; i++)
                {
                    if (scan.NodeTypes[i] == 0) scan.NodeToPoly[i] = polyCount++;
                    else scan.NodeToPortal[i] = portalCount++;
                }
                scan.PolyCount = polyCount;
                scan.PortalCount = portalCount;
                for (int node = 0; node < scan.NodeTypes.Length; node++)
                {
                    if (scan.NodeTypes[node] == 0)
                    {
                        int contourCount = ReadArrayCount(reader, scan.VertexCount, "contour");
                        ValidateIndexArray(reader, contourCount, scan.VertexCount, "contour");
                        int triangleCount = ReadArrayCount(reader, checked(scan.VertexCount * 12), "triangles");
                        if ((triangleCount % 3) != 0) throw new InvalidDataException("rainnav_triangle_mod3");
                        ValidateIndexArray(reader, triangleCount, scan.VertexCount, "triangles");
                        int portalRefCount = ReadArrayCount(reader, scan.PortalCount, "poly_portals");
                        ValidateNodeReferences(reader, portalRefCount, scan.NodeToPortal, "poly_portals");
                        SkipBytes(reader, 37L);
                        scan.ContourIndexCount = checked(scan.ContourIndexCount + contourCount);
                        scan.TriangleIndexCount = checked(scan.TriangleIndexCount + triangleCount);
                        scan.PolyPortalIndexCount = checked(scan.PolyPortalIndexCount + portalRefCount);
                    }
                    else
                    {
                        int vertexRefCount = ReadArrayCount(reader, 2, "portal_vertices");
                        if (vertexRefCount != 2) throw new InvalidDataException("rainnav_portal_vertices=" + vertexRefCount);
                        ValidateIndexArray(reader, vertexRefCount, scan.VertexCount, "portal_vertices");
                        int polyRefCount = ReadArrayCount(reader, scan.PolyCount, "portal_polys");
                        if (polyRefCount <= 0) throw new InvalidDataException("rainnav_empty_portal");
                        ValidateNodeReferences(reader, polyRefCount, scan.NodeToPoly, "portal_polys");
                        reader.ReadInt32();
                        scan.PortalPolyIndexCount = checked(scan.PortalPolyIndexCount + polyRefCount);
                    }
                }
                if (stream.Position != navHeader.PayloadOffset + navHeader.PayloadLength)
                    throw new InvalidDataException("rainnav_payload_trailing_bytes");
                if (scan.PolyCount <= 0 || scan.PortalCount <= 0 ||
                    scan.PolyCount + scan.PortalCount != navHeader.GraphSize ||
                    scan.ContourIndexCount != scan.PolyPortalIndexCount ||
                    scan.PolyPortalIndexCount != scan.PortalPolyIndexCount)
                    throw new InvalidDataException("rainnav_graph_invariants");
                return scan;
            }
        }

        private static CompactRainNavBuildData ReadGraph(CompactRainSourceCacheHeader navHeader,
            GraphScan scan)
        {
            CompactRainNavBuildData data = new CompactRainNavBuildData();
            data.Polys = new CompactRainNavPolyRecord[scan.PolyCount];
            data.Portals = new CompactRainNavPortalRecord[scan.PortalCount];
            data.ContourIndices = new int[scan.ContourIndexCount];
            data.TriangleIndices = new int[scan.TriangleIndexCount];
            data.PolyPortalIndices = new int[scan.PolyPortalIndexCount];
            data.PortalPolyIndices = new int[scan.PortalPolyIndexCount];
            using (FileStream stream = File.Open(navHeader.FilePath, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (BinaryReader reader = new BinaryReader(stream))
            {
                stream.Position = navHeader.PayloadOffset;
                GraphPreamble preamble = ReadGraphPreamble(reader, navHeader.GraphSize, true);
                if (!NodeTypesEqual(scan.NodeTypes, preamble.NodeTypes) ||
                    preamble.VertexCount != scan.VertexCount ||
                    !string.Equals(preamble.GraphName, scan.GraphName, StringComparison.Ordinal) ||
                    preamble.MinEdgeCost != scan.MinEdgeCost)
                    throw new InvalidDataException("rainnav_preamble_changed");
                data.Vertices = preamble.Vertices;
                int contourCursor = 0;
                int triangleCursor = 0;
                int polyPortalCursor = 0;
                int portalPolyCursor = 0;
                for (int node = 0; node < scan.NodeTypes.Length; node++)
                {
                    if (scan.NodeTypes[node] == 0)
                    {
                        int polyIndex = scan.NodeToPoly[node];
                        CompactRainNavPolyRecord poly = new CompactRainNavPolyRecord();
                        poly.ContourStart = contourCursor;
                        poly.ContourCount = ReadMappedIndices(reader, data.ContourIndices,
                            ref contourCursor, scan.VertexCount, null, "contour");
                        poly.TriangleStart = triangleCursor;
                        poly.TriangleCount = ReadMappedIndices(reader, data.TriangleIndices,
                            ref triangleCursor, scan.VertexCount, null, "triangles");
                        if ((poly.TriangleCount % 3) != 0)
                            throw new InvalidDataException("rainnav_triangle_mod3");
                        poly.PortalStart = polyPortalCursor;
                        poly.PortalCount = ReadMappedIndices(reader, data.PolyPortalIndices,
                            ref polyPortalCursor, scan.NodeToPortal.Length, scan.NodeToPortal, "poly_portals");
                        if (poly.PortalCount != poly.ContourCount)
                            throw new InvalidDataException("rainnav_poly_portal_count=" + polyIndex);
                        poly.CenterX = reader.ReadSingle();
                        poly.CenterY = reader.ReadSingle();
                        poly.CenterZ = reader.ReadSingle();
                        poly.BoundsCenterX = reader.ReadSingle();
                        poly.BoundsCenterY = reader.ReadSingle();
                        poly.BoundsCenterZ = reader.ReadSingle();
                        poly.BoundsSizeX = reader.ReadSingle();
                        poly.BoundsSizeY = reader.ReadSingle();
                        poly.BoundsSizeZ = reader.ReadSingle();
                        poly.Flags = reader.ReadBoolean() ? CompactRainNavFormat.PolyUnwalkable : 0;
                        poly.Component = -1;
                        ValidatePoly(poly, polyIndex, scan.VertexCount, data);
                        data.Polys[polyIndex] = poly;
                    }
                    else
                    {
                        int portalIndex = scan.NodeToPortal[node];
                        int count = reader.ReadInt32();
                        if (count != 2) throw new InvalidDataException("rainnav_portal_vertices=" + count);
                        CompactRainNavPortalRecord portal = new CompactRainNavPortalRecord();
                        portal.VertexOne = reader.ReadInt32();
                        portal.VertexTwo = reader.ReadInt32();
                        if (portal.VertexOne < 0 || portal.VertexOne >= scan.VertexCount ||
                            portal.VertexTwo < 0 || portal.VertexTwo >= scan.VertexCount ||
                            portal.VertexOne == portal.VertexTwo)
                            throw new InvalidDataException("rainnav_portal_vertex=" + portalIndex);
                        portal.PolyStart = portalPolyCursor;
                        portal.PolyCount = ReadMappedIndices(reader, data.PortalPolyIndices,
                            ref portalPolyCursor, scan.NodeToPoly.Length, scan.NodeToPoly, "portal_polys");
                        portal.Pairing = reader.ReadInt32();
                        if (portal.PolyCount == 1) portal.Flags |= CompactRainNavFormat.PortalBoundary;
                        if (portal.PolyCount > 2) portal.Flags |= CompactRainNavFormat.PortalMultiPoly;
                        int a = portal.VertexOne * 3;
                        int b = portal.VertexTwo * 3;
                        portal.CenterX = data.Vertices[a] + (data.Vertices[b] - data.Vertices[a]) * 0.5f;
                        portal.CenterY = data.Vertices[a + 1] + (data.Vertices[b + 1] - data.Vertices[a + 1]) * 0.5f;
                        portal.CenterZ = data.Vertices[a + 2] + (data.Vertices[b + 2] - data.Vertices[a + 2]) * 0.5f;
                        data.Portals[portalIndex] = portal;
                    }
                }
                if (contourCursor != data.ContourIndices.Length || triangleCursor != data.TriangleIndices.Length ||
                    polyPortalCursor != data.PolyPortalIndices.Length || portalPolyCursor != data.PortalPolyIndices.Length ||
                    stream.Position != navHeader.PayloadOffset + navHeader.PayloadLength)
                    throw new InvalidDataException("rainnav_read_counts");
            }
            ValidateBidirectionalTopology(data);
            return data;
        }

        private static GraphPreamble ReadGraphPreamble(BinaryReader reader, int expectedGraphSize,
            bool keepVertices)
        {
            if (reader.ReadInt32() != RainGraphVersion)
                throw new InvalidDataException("rainnav_graph_version");
            int vertexCount = reader.ReadInt32();
            if (vertexCount <= 0 || vertexCount > expectedGraphSize * 4)
                throw new InvalidDataException("rainnav_vertices=" + vertexCount);
            float[] vertices = keepVertices ? new float[checked(vertexCount * 3)] : null;
            for (int i = 0; i < vertexCount * 3; i++)
            {
                float value = reader.ReadSingle();
                if (!CompactRainNavFormat.IsFinite(value)) throw new InvalidDataException("rainnav_vertex=" + i);
                if (keepVertices) vertices[i] = value;
            }
            for (int i = 0; i < 6; i++)
                if (!CompactRainNavFormat.IsFinite(reader.ReadSingle()))
                    throw new InvalidDataException("rainnav_vertex_bounds");
            string graphName = reader.ReadString();
            int tagCount = reader.ReadInt32();
            if (tagCount < 0 || tagCount > 1024) throw new InvalidDataException("rainnav_tags=" + tagCount);
            for (int i = 0; i < tagCount; i++) reader.ReadString();
            float minEdgeCost = reader.ReadSingle();
            if (!CompactRainNavFormat.IsFinite(minEdgeCost) || minEdgeCost <= 0f)
                throw new InvalidDataException("rainnav_min_edge_cost");
            int nodeCount = reader.ReadInt32();
            if (nodeCount != expectedGraphSize) throw new InvalidDataException("rainnav_nodes=" + nodeCount);
            byte[] nodeTypes = new byte[nodeCount];
            for (int i = 0; i < nodeCount; i++)
            {
                int type = reader.ReadInt32();
                if (type != 0 && type != 1) throw new InvalidDataException("rainnav_node_type=" + type);
                nodeTypes[i] = (byte)type;
            }
            GraphPreamble result = new GraphPreamble();
            result.VertexCount = vertexCount;
            result.Vertices = vertices;
            result.GraphName = graphName;
            result.MinEdgeCost = minEdgeCost;
            result.NodeTypes = nodeTypes;
            return result;
        }

        private static void ApplyMetadata(CompactRainNavBuildData data, CompactRainMetaData meta)
        {
            CompactRainNavSurfaceRecord[] ordered = new CompactRainNavSurfaceRecord[data.Polys.Length];
            for (int i = 0; i < meta.Surfaces.Length; i++)
            {
                CompactRainNavSurfaceRecord surface = meta.Surfaces[i];
                ordered[surface.PolyIndex] = surface;
                CompactRainNavPolyRecord poly = data.Polys[surface.PolyIndex];
                poly.Component = surface.Component;
                data.Polys[surface.PolyIndex] = poly;
            }
            for (int i = 0; i < data.Polys.Length; i++)
                if (data.Polys[i].Component < 0)
                    throw new InvalidDataException("rainmeta_missing_poly=" + i);
            data.Surfaces = ordered;
        }

        private static CompactRainNavHeader CreateHeader(CompactRainSourceCacheHeader nav,
            CompactRainMetaData meta, CompactRainNavBuildData data, GraphScan scan)
        {
            CompactRainNavHeader header = new CompactRainNavHeader();
            header.SourceNavSha256 = nav.FileSha256;
            header.SourceMetaSha256 = meta.Header.FileSha256;
            header.MapName = nav.MapName;
            header.RainIdentity = nav.RainIdentity;
            header.GeneratorSignature = nav.Signature;
            header.MetaSignature = meta.Header.Signature;
            header.ContentFingerprint = nav.ContentFingerprint;
            header.GraphName = scan.GraphName;
            header.CellSize = ReadSignatureFloat(nav.Signature, "cell");
            header.AgentRadius = ReadSignatureFloat(nav.Signature, "radius");
            header.StepHeight = ReadSignatureFloat(nav.Signature, "step");
            header.WalkableHeight = ReadSignatureFloat(nav.Signature, "height");
            header.MinEdgeCost = scan.MinEdgeCost;
            header.BoundsCenterX = nav.BoundsCenterX;
            header.BoundsCenterY = nav.BoundsCenterY;
            header.BoundsCenterZ = nav.BoundsCenterZ;
            header.BoundsSizeX = nav.BoundsSizeX;
            header.BoundsSizeY = nav.BoundsSizeY;
            header.BoundsSizeZ = nav.BoundsSizeZ;
            header.ColliderCount = nav.ColliderCount;
            header.RawGraphSize = nav.GraphSize;
            header.ComponentCount = meta.ComponentCount;
            header.SafeSpawnCount = meta.SafeSpawnCount;
            return header;
        }

        private static byte[] WriteFile(string path, CompactRainNavBuildData data)
        {
            using (FileStream stream = File.Open(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
            using (BinaryWriter writer = new BinaryWriter(stream, Encoding.UTF8))
            {
                long payloadStart = CompactRainNavFormat.WriteHeader(writer, data.Header);
                WritePayload(writer, data);
                writer.Flush();
                if (stream.Position - payloadStart != data.Header.PayloadLength)
                    throw new InvalidDataException("aswnav_written_payload_length");
                byte[] payloadHash = CompactRainNavFormat.ComputeSha256(stream,
                    payloadStart, data.Header.PayloadLength);
                stream.Position = PayloadHashOffset;
                writer.Write(payloadHash);
                writer.Flush();
                return payloadHash;
            }
        }

        private static void WritePayload(BinaryWriter writer, CompactRainNavBuildData data)
        {
            for (int i = 0; i < data.Vertices.Length; i++) writer.Write(data.Vertices[i]);
            for (int i = 0; i < data.Polys.Length; i++) WritePoly(writer, data.Polys[i]);
            for (int i = 0; i < data.Portals.Length; i++) WritePortal(writer, data.Portals[i]);
            WriteIndices(writer, data.ContourIndices);
            WriteIndices(writer, data.TriangleIndices);
            WriteIndices(writer, data.PolyPortalIndices);
            WriteIndices(writer, data.PortalPolyIndices);
            for (int i = 0; i < data.Links.Length; i++) WriteLink(writer, data.Links[i]);
            for (int i = 0; i < data.Boundaries.Length; i++) WriteBoundary(writer, data.Boundaries[i]);
            for (int i = 0; i < data.Surfaces.Length; i++) WriteSurface(writer, data.Surfaces[i]);
            for (int i = 0; i < data.SpatialCells.Length; i++) WriteSpatialCell(writer, data.SpatialCells[i]);
            WriteIndices(writer, data.SpatialPolyIndices);
        }

        private static void WritePoly(BinaryWriter writer, CompactRainNavPolyRecord value)
        {
            writer.Write(value.ContourStart); writer.Write(value.ContourCount);
            writer.Write(value.TriangleStart); writer.Write(value.TriangleCount);
            writer.Write(value.PortalStart); writer.Write(value.PortalCount);
            writer.Write(value.Component); writer.Write(value.Flags);
            writer.Write(value.CenterX); writer.Write(value.CenterY); writer.Write(value.CenterZ);
            writer.Write(value.BoundsCenterX); writer.Write(value.BoundsCenterY); writer.Write(value.BoundsCenterZ);
            writer.Write(value.BoundsSizeX); writer.Write(value.BoundsSizeY); writer.Write(value.BoundsSizeZ);
        }

        private static void WritePortal(BinaryWriter writer, CompactRainNavPortalRecord value)
        {
            writer.Write(value.VertexOne); writer.Write(value.VertexTwo);
            writer.Write(value.PolyStart); writer.Write(value.PolyCount);
            writer.Write(value.Pairing); writer.Write(value.Flags);
            writer.Write(value.CenterX); writer.Write(value.CenterY); writer.Write(value.CenterZ);
        }

        private static void WriteLink(BinaryWriter writer, CompactRainNavLinkRecord value)
        {
            writer.Write(value.FromPortal); writer.Write(value.ToPortal);
            writer.Write(value.StartX); writer.Write(value.StartY); writer.Write(value.StartZ);
            writer.Write(value.EndX); writer.Write(value.EndY); writer.Write(value.EndZ);
            writer.Write(value.RequiredJumpHeight); writer.Write(value.RequiredRunSpeed); writer.Write(value.Cost);
            writer.Write(value.Kind); writer.Write((byte)0); writer.Write((byte)0); writer.Write((byte)0);
        }

        private static void WriteBoundary(BinaryWriter writer, CompactRainNavBoundaryRecord value)
        {
            writer.Write(value.PortalIndex);
            writer.Write(value.PositionX); writer.Write(value.PositionY); writer.Write(value.PositionZ);
            writer.Write(value.OutwardX); writer.Write(value.OutwardY); writer.Write(value.OutwardZ);
            writer.Write(value.Component); writer.Write(value.Width);
        }

        private static void WriteSurface(BinaryWriter writer, CompactRainNavSurfaceRecord value)
        {
            writer.Write(value.PolyIndex);
            writer.Write(value.PositionX); writer.Write(value.PositionY); writer.Write(value.PositionZ);
            writer.Write(value.Component); writer.Write(value.Clearance);
            writer.Write(value.CoverMask); writer.Write(value.Flags); writer.Write((byte)0); writer.Write((byte)0);
        }

        private static void WriteSpatialCell(BinaryWriter writer, CompactRainSpatialCellRecord value)
        {
            writer.Write(value.X); writer.Write(value.Y); writer.Write(value.Z);
            writer.Write(value.PolyStart); writer.Write(value.PolyCount);
            writer.Write(value.MinimumY); writer.Write(value.MaximumY); writer.Write(value.Reserved);
        }

        private static void WriteIndices(BinaryWriter writer, int[] values)
        {
            for (int i = 0; i < values.Length; i++) writer.Write(values[i]);
        }

        private static void CommitFile(string tempPath, string outputPath)
        {
            if (!File.Exists(outputPath))
            {
                File.Move(tempPath, outputPath);
                return;
            }
            string backupPath = outputPath + ".previous." + DateTime.Now.ToString("yyyyMMdd_HHmmssfff");
            File.Replace(tempPath, outputPath, backupPath, true);
        }

        private static void ValidateCrossSectionCounts(CompactRainNavHeader header)
        {
            int polyCount = CompactRainNavFormat.FindSection(header, CompactRainNavFormat.PolysSection).Count;
            int portalCount = CompactRainNavFormat.FindSection(header, CompactRainNavFormat.PortalsSection).Count;
            int contourCount = CompactRainNavFormat.FindSection(header, CompactRainNavFormat.ContoursSection).Count;
            int polyPortalCount = CompactRainNavFormat.FindSection(header, CompactRainNavFormat.PolyPortalsSection).Count;
            int portalPolyCount = CompactRainNavFormat.FindSection(header, CompactRainNavFormat.PortalPolysSection).Count;
            int surfaceCount = CompactRainNavFormat.FindSection(header, CompactRainNavFormat.SurfacesSection).Count;
            if (polyCount <= 0 || portalCount <= 0 || surfaceCount != polyCount ||
                polyCount + portalCount != header.RawGraphSize || contourCount != polyPortalCount ||
                polyPortalCount != portalPolyCount)
                throw new InvalidDataException("aswnav_cross_section_counts");
        }

        private static void ValidateBidirectionalTopology(CompactRainNavBuildData data)
        {
            for (int polyIndex = 0; polyIndex < data.Polys.Length; polyIndex++)
            {
                CompactRainNavPolyRecord poly = data.Polys[polyIndex];
                for (int i = 0; i < poly.PortalCount; i++)
                {
                    int portalIndex = data.PolyPortalIndices[poly.PortalStart + i];
                    CompactRainNavPortalRecord portal = data.Portals[portalIndex];
                    bool found = false;
                    for (int p = 0; p < portal.PolyCount; p++)
                        if (data.PortalPolyIndices[portal.PolyStart + p] == polyIndex) { found = true; break; }
                    if (!found) throw new InvalidDataException("rainnav_topology_poly=" + polyIndex);
                }
            }
        }

        private static void ValidatePoly(CompactRainNavPolyRecord poly, int index, int vertexCount,
            CompactRainNavBuildData data)
        {
            if (poly.ContourCount < 3 || poly.TriangleCount < 3 || poly.PortalCount != poly.ContourCount ||
                !FiniteVector(poly.CenterX, poly.CenterY, poly.CenterZ) ||
                !FiniteVector(poly.BoundsCenterX, poly.BoundsCenterY, poly.BoundsCenterZ) ||
                !FiniteVector(poly.BoundsSizeX, poly.BoundsSizeY, poly.BoundsSizeZ) ||
                poly.BoundsSizeX < 0f || poly.BoundsSizeY < 0f || poly.BoundsSizeZ < 0f)
                throw new InvalidDataException("rainnav_poly=" + index);
            for (int i = 0; i < poly.ContourCount; i++)
            {
                int vertex = data.ContourIndices[poly.ContourStart + i];
                if (vertex < 0 || vertex >= vertexCount) throw new InvalidDataException("rainnav_poly_contour=" + index);
            }
        }

        private static int ReadMappedIndices(BinaryReader reader, int[] output, ref int cursor,
            int maximumInput, int[] mapping, string name)
        {
            int count = ReadArrayCount(reader, output.Length - cursor, name);
            for (int i = 0; i < count; i++)
            {
                int value = reader.ReadInt32();
                if (value < 0 || value >= maximumInput)
                    throw new InvalidDataException("rainnav_" + name + "=" + value);
                if (mapping != null)
                {
                    if (mapping[value] < 0) throw new InvalidDataException("rainnav_" + name + "_type=" + value);
                    value = mapping[value];
                }
                output[cursor++] = value;
            }
            return count;
        }

        private static int ReadArrayCount(BinaryReader reader, int maximum, string name)
        {
            int count = reader.ReadInt32();
            if (count < 0 || count > maximum)
                throw new InvalidDataException("rainnav_" + name + "_count=" + count);
            return count;
        }

        private static void ValidateIndexArray(BinaryReader reader, int count, int maximum, string name)
        {
            for (int i = 0; i < count; i++)
            {
                int value = reader.ReadInt32();
                if (value < 0 || value >= maximum)
                    throw new InvalidDataException("rainnav_" + name + "=" + value);
            }
        }

        private static void ValidateNodeReferences(BinaryReader reader, int count, int[] mapping,
            string name)
        {
            for (int i = 0; i < count; i++)
            {
                int value = reader.ReadInt32();
                if (value < 0 || value >= mapping.Length || mapping[value] < 0)
                    throw new InvalidDataException("rainnav_" + name + "=" + value);
            }
        }

        private static void SkipBytes(BinaryReader reader, long count)
        {
            Stream stream = reader.BaseStream;
            if (count < 0L || count > stream.Length - stream.Position) throw new EndOfStreamException();
            stream.Position += count;
        }

        private static bool NodeTypesEqual(byte[] left, byte[] right)
        {
            if (left == null || right == null || left.Length != right.Length) return false;
            for (int i = 0; i < left.Length; i++) if (left[i] != right[i]) return false;
            return true;
        }

        private static int[] CreateFilledArray(int length, int value)
        {
            int[] result = new int[length];
            for (int i = 0; i < result.Length; i++) result[i] = value;
            return result;
        }

        private static float ReadSignatureFloat(string signature, string name)
        {
            string prefix = name + "=";
            string[] fields = signature.Split('|');
            for (int i = 0; i < fields.Length; i++)
            {
                if (!fields[i].StartsWith(prefix, StringComparison.Ordinal)) continue;
                float value;
                if (float.TryParse(fields[i].Substring(prefix.Length),
                    System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out value) &&
                    CompactRainNavFormat.IsFinite(value) && value > 0f) return value;
            }
            throw new InvalidDataException("rainnav_signature_" + name);
        }

        private static string ReadOuterString(BinaryReader reader)
        {
            int length = reader.ReadInt32();
            if (length < 0 || length > MaxMetadataBytes)
                throw new InvalidDataException("rainnav_string_length=" + length);
            return Encoding.UTF8.GetString(CompactRainNavFormat.ReadBytesExact(reader, length));
        }

        private static bool FiniteVector(float x, float y, float z)
        {
            return CompactRainNavFormat.IsFinite(x) && CompactRainNavFormat.IsFinite(y) &&
                CompactRainNavFormat.IsFinite(z);
        }

        private static void ValidatePaths(string navPath, string metaPath, string outputPath)
        {
            if (string.IsNullOrEmpty(navPath) || !File.Exists(navPath))
                throw new FileNotFoundException("rainnav_missing", navPath);
            if (string.IsNullOrEmpty(metaPath) || !File.Exists(metaPath))
                throw new FileNotFoundException("rainmeta_missing", metaPath);
            if (string.IsNullOrEmpty(outputPath)) throw new ArgumentNullException("outputPath");
            if (string.Equals(Path.GetFullPath(navPath), Path.GetFullPath(outputPath),
                StringComparison.OrdinalIgnoreCase) ||
                string.Equals(Path.GetFullPath(metaPath), Path.GetFullPath(outputPath),
                StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("aswnav_output_overlaps_input");
        }

        private static string SafeOneLine(string value, int maximum)
        {
            if (string.IsNullOrEmpty(value)) return "-";
            string safe = value.Replace('\r', ' ').Replace('\n', ' ').Trim();
            return safe.Length <= maximum ? safe : safe.Substring(0, maximum);
        }

        private sealed class GraphPreamble
        {
            public int VertexCount;
            public float[] Vertices;
            public string GraphName;
            public float MinEdgeCost;
            public byte[] NodeTypes;
        }

        private sealed class GraphScan
        {
            public int VertexCount;
            public int PolyCount;
            public int PortalCount;
            public int ContourIndexCount;
            public int TriangleIndexCount;
            public int PolyPortalIndexCount;
            public int PortalPolyIndexCount;
            public string GraphName;
            public float MinEdgeCost;
            public byte[] NodeTypes;
            public int[] NodeToPoly;
            public int[] NodeToPortal;
        }
    }

    internal sealed class CompactRainNavConversionResult
    {
        public string OutputPath;
        public long OutputBytes;
        public string OutputSha256;
        public string PayloadSha256;
        public string SourceNavSha256;
        public string SourceMetaSha256;
        public int VertexCount;
        public int PolyCount;
        public int PortalCount;
        public int LinkCount;
        public int BoundaryCount;
        public int SurfaceCount;
        public int ComponentCount;
        public int SafeSpawnCount;
    }
}
