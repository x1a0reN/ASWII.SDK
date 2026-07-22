using System;

namespace ASWDEBUG.Cheats.AutoBattle.CompactNav
{
    internal sealed class CompactRainNavDataset
    {
        private readonly CompactRainNavHeader _header;
        private readonly float[] _vertices;
        private readonly CompactRainNavPolyRecord[] _polys;
        private readonly CompactRainNavPortalRecord[] _portals;
        private readonly int[] _contourIndices;
        private readonly int[] _triangleIndices;
        private readonly int[] _polyPortalIndices;
        private readonly int[] _portalPolyIndices;
        private readonly CompactRainNavLinkRecord[] _links;
        private readonly CompactRainNavBoundaryRecord[] _boundaries;
        private readonly CompactRainNavSurfaceRecord[] _surfaces;
        private readonly CompactRainSpatialIndex _spatialIndex;
        private readonly long _residentBytes;

        internal CompactRainNavDataset(CompactRainNavHeader header, float[] vertices,
            CompactRainNavPolyRecord[] polys, CompactRainNavPortalRecord[] portals,
            int[] contourIndices, int[] triangleIndices, int[] polyPortalIndices,
            int[] portalPolyIndices, CompactRainNavLinkRecord[] links,
            CompactRainNavBoundaryRecord[] boundaries, CompactRainNavSurfaceRecord[] surfaces)
        {
            if (header == null || vertices == null || polys == null || portals == null ||
                contourIndices == null || triangleIndices == null || polyPortalIndices == null ||
                portalPolyIndices == null || links == null || boundaries == null || surfaces == null)
                throw new ArgumentNullException("compact navigation data");
            _header = header;
            _vertices = vertices;
            _polys = polys;
            _portals = portals;
            _contourIndices = contourIndices;
            _triangleIndices = triangleIndices;
            _polyPortalIndices = polyPortalIndices;
            _portalPolyIndices = portalPolyIndices;
            _links = links;
            _boundaries = boundaries;
            _surfaces = surfaces;
            _spatialIndex = new CompactRainSpatialIndex(this);
            _residentBytes = EstimateResidentBytes() + _spatialIndex.EstimatedBytes;
        }

        internal CompactRainNavHeader Header { get { return _header; } }
        internal int VertexCount { get { return _vertices.Length / 3; } }
        internal int PolyCount { get { return _polys.Length; } }
        internal int PortalCount { get { return _portals.Length; } }
        internal int LinkCount { get { return _links.Length; } }
        internal int BoundaryCount { get { return _boundaries.Length; } }
        internal int SurfaceCount { get { return _surfaces.Length; } }
        internal int ComponentCount { get { return _header.ComponentCount; } }
        internal int SafeSpawnCount { get { return _header.SafeSpawnCount; } }
        internal long ResidentBytes { get { return _residentBytes; } }
        internal CompactRainSpatialIndex SpatialIndex { get { return _spatialIndex; } }

        internal CompactRainNavPolyRecord GetPoly(int index)
        {
            return _polys[index];
        }

        internal CompactRainNavPortalRecord GetPortal(int index)
        {
            return _portals[index];
        }

        internal CompactRainNavLinkRecord GetLink(int index)
        {
            return _links[index];
        }

        internal CompactRainNavBoundaryRecord GetBoundary(int index)
        {
            return _boundaries[index];
        }

        internal CompactRainNavSurfaceRecord GetSurface(int index)
        {
            return _surfaces[index];
        }

        internal int GetContourIndex(int index)
        {
            return _contourIndices[index];
        }

        internal int GetTriangleIndex(int index)
        {
            return _triangleIndices[index];
        }

        internal int GetPolyPortalIndex(int index)
        {
            return _polyPortalIndices[index];
        }

        internal int GetPortalPolyIndex(int index)
        {
            return _portalPolyIndices[index];
        }

        internal CompactRainPoint GetVertex(int index)
        {
            int offset = checked(index * 3);
            return new CompactRainPoint(_vertices[offset], _vertices[offset + 1], _vertices[offset + 2]);
        }

        internal CompactRainPoint GetPortalCenter(int index)
        {
            CompactRainNavPortalRecord portal = _portals[index];
            return new CompactRainPoint(portal.CenterX, portal.CenterY, portal.CenterZ);
        }

        internal CompactRainPoint GetPolyCenter(int index)
        {
            CompactRainNavPolyRecord poly = _polys[index];
            return new CompactRainPoint(poly.CenterX, poly.CenterY, poly.CenterZ);
        }

        internal bool IsPortalOnPolyBoundary(int portalIndex, int polyIndex)
        {
            CompactRainNavPortalRecord portal = _portals[portalIndex];
            CompactRainNavPolyRecord poly = _polys[polyIndex];
            if (poly.ContourCount < 2) return false;
            for (int i = 0; i < poly.ContourCount; i++)
            {
                int first = _contourIndices[poly.ContourStart + i];
                int second = _contourIndices[poly.ContourStart +
                    ((i + 1) % poly.ContourCount)];
                if ((portal.VertexOne == first && portal.VertexTwo == second) ||
                    (portal.VertexOne == second && portal.VertexTwo == first))
                    return true;
            }
            return false;
        }

        private long EstimateResidentBytes()
        {
            long result = 0L;
            result += (long)_vertices.Length * sizeof(float);
            result += (long)_polys.Length * CompactRainNavFormat.PolyBytes;
            result += (long)_portals.Length * CompactRainNavFormat.PortalBytes;
            result += (long)_contourIndices.Length * sizeof(int);
            result += (long)_triangleIndices.Length * sizeof(int);
            result += (long)_polyPortalIndices.Length * sizeof(int);
            result += (long)_portalPolyIndices.Length * sizeof(int);
            result += (long)_links.Length * CompactRainNavFormat.LinkBytes;
            result += (long)_boundaries.Length * CompactRainNavFormat.BoundaryBytes;
            result += (long)_surfaces.Length * CompactRainNavFormat.SurfaceBytes;
            return result;
        }
    }

    internal struct CompactRainPoint
    {
        public float X;
        public float Y;
        public float Z;

        public CompactRainPoint(float x, float y, float z)
        {
            X = x;
            Y = y;
            Z = z;
        }

        public static float Distance(CompactRainPoint left, CompactRainPoint right)
        {
            float x = left.X - right.X;
            float y = left.Y - right.Y;
            float z = left.Z - right.Z;
            return (float)Math.Sqrt(x * x + y * y + z * z);
        }

        public static float DistanceXZ(CompactRainPoint left, CompactRainPoint right)
        {
            float x = left.X - right.X;
            float z = left.Z - right.Z;
            return (float)Math.Sqrt(x * x + z * z);
        }
    }

    internal struct CompactRainProjection
    {
        public int PolyIndex;
        public CompactRainPoint Point;
        public float HorizontalError;
        public float VerticalError;
        public bool ExactXZ;
    }
}
