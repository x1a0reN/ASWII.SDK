using System;
using System.Collections.Generic;

namespace ASWDEBUG.Cheats.AutoBattle.CompactNav
{
    internal sealed class CompactRainBoundaryIndex
    {
        private const float BucketSize = 3.0f;

        private readonly CompactRainNavDataset _dataset;
        private readonly Dictionary<long, List<int>> _buckets =
            new Dictionary<long, List<int>>();
        private readonly int[] _selectionStamps;
        private int _selectionStamp;
        private long _storedReferences;

        internal CompactRainBoundaryIndex(CompactRainNavDataset dataset)
        {
            if (dataset == null) throw new ArgumentNullException("dataset");
            _dataset = dataset;
            _selectionStamps = new int[dataset.BoundaryCount];
            for (int i = 0; i < dataset.BoundaryCount; i++)
                AddBoundary(i, dataset.GetBoundary(i));
        }

        internal long EstimatedBytes
        {
            get
            {
                return _storedReferences * sizeof(int) +
                    (long)_selectionStamps.Length * sizeof(int) +
                    (long)_buckets.Count * 40L;
            }
        }

        internal bool HasBoundaries
        {
            get { return _dataset.BoundaryCount > 0; }
        }

        internal float MeasureClearance(CompactRainPoint point, int component,
            float maximumDistance, float maximumVerticalDistance)
        {
            if (maximumDistance <= 0f || _dataset.BoundaryCount == 0)
                return Math.Max(0f, maximumDistance);

            int minimumX = Cell(point.X - maximumDistance);
            int maximumX = Cell(point.X + maximumDistance);
            int minimumZ = Cell(point.Z - maximumDistance);
            int maximumZ = Cell(point.Z + maximumDistance);
            float best = maximumDistance;
            for (int z = minimumZ; z <= maximumZ; z++)
            {
                for (int x = minimumX; x <= maximumX; x++)
                {
                    List<int> candidates;
                    if (!_buckets.TryGetValue(Key(x, z), out candidates)) continue;
                    for (int i = 0; i < candidates.Count; i++)
                    {
                        CompactRainNavBoundaryRecord boundary =
                            _dataset.GetBoundary(candidates[i]);
                        if (component >= 0 && boundary.Component != component) continue;
                        if (Math.Abs(boundary.PositionY - point.Y) >
                            maximumVerticalDistance) continue;
                        float distance = DistanceToBoundaryXZ(point, boundary);
                        if (distance < best) best = distance;
                    }
                }
            }
            return best;
        }

        internal int CollectNearby(CompactRainPoint point, int component,
            float maximumDistance, float maximumVerticalDistance, List<int> output)
        {
            if (output == null) throw new ArgumentNullException("output");
            output.Clear();
            if (maximumDistance <= 0f || _dataset.BoundaryCount == 0) return 0;
            AdvanceSelectionStamp();
            int minimumX = Cell(point.X - maximumDistance);
            int maximumX = Cell(point.X + maximumDistance);
            int minimumZ = Cell(point.Z - maximumDistance);
            int maximumZ = Cell(point.Z + maximumDistance);
            for (int z = minimumZ; z <= maximumZ; z++)
            {
                for (int x = minimumX; x <= maximumX; x++)
                {
                    List<int> candidates;
                    if (!_buckets.TryGetValue(Key(x, z), out candidates)) continue;
                    for (int i = 0; i < candidates.Count; i++)
                    {
                        int boundaryIndex = candidates[i];
                        if (_selectionStamps[boundaryIndex] == _selectionStamp) continue;
                        _selectionStamps[boundaryIndex] = _selectionStamp;
                        CompactRainNavBoundaryRecord boundary =
                            _dataset.GetBoundary(boundaryIndex);
                        if (component >= 0 && boundary.Component != component) continue;
                        if (Math.Abs(boundary.PositionY - point.Y) >
                            maximumVerticalDistance) continue;
                        if (DistanceToBoundaryXZ(point, boundary) > maximumDistance) continue;
                        output.Add(boundaryIndex);
                    }
                }
            }
            return output.Count;
        }

        private void AddBoundary(int boundaryIndex, CompactRainNavBoundaryRecord boundary)
        {
            float sideX = boundary.OutwardZ;
            float sideZ = -boundary.OutwardX;
            float sideLength = (float)Math.Sqrt(sideX * sideX + sideZ * sideZ);
            if (sideLength <= 0.0001f) return;
            sideX /= sideLength;
            sideZ /= sideLength;
            float halfWidth = Math.Max(0.025f, boundary.Width * 0.5f);
            float firstX = boundary.PositionX - sideX * halfWidth;
            float firstZ = boundary.PositionZ - sideZ * halfWidth;
            float secondX = boundary.PositionX + sideX * halfWidth;
            float secondZ = boundary.PositionZ + sideZ * halfWidth;
            int minimumX = Cell(Math.Min(firstX, secondX));
            int maximumX = Cell(Math.Max(firstX, secondX));
            int minimumZ = Cell(Math.Min(firstZ, secondZ));
            int maximumZ = Cell(Math.Max(firstZ, secondZ));
            for (int z = minimumZ; z <= maximumZ; z++)
            {
                for (int x = minimumX; x <= maximumX; x++)
                {
                    long key = Key(x, z);
                    List<int> values;
                    if (!_buckets.TryGetValue(key, out values))
                    {
                        values = new List<int>(8);
                        _buckets.Add(key, values);
                    }
                    values.Add(boundaryIndex);
                    _storedReferences++;
                }
            }
        }

        private static float DistanceToBoundaryXZ(CompactRainPoint point,
            CompactRainNavBoundaryRecord boundary)
        {
            float sideX = boundary.OutwardZ;
            float sideZ = -boundary.OutwardX;
            float sideLength = (float)Math.Sqrt(sideX * sideX + sideZ * sideZ);
            if (sideLength <= 0.0001f)
            {
                float dx = point.X - boundary.PositionX;
                float dz = point.Z - boundary.PositionZ;
                return (float)Math.Sqrt(dx * dx + dz * dz);
            }
            sideX /= sideLength;
            sideZ /= sideLength;
            float halfWidth = Math.Max(0.025f, boundary.Width * 0.5f);
            float firstX = boundary.PositionX - sideX * halfWidth;
            float firstZ = boundary.PositionZ - sideZ * halfWidth;
            float segmentX = sideX * halfWidth * 2f;
            float segmentZ = sideZ * halfWidth * 2f;
            float segmentLengthSquared = segmentX * segmentX + segmentZ * segmentZ;
            float projection = segmentLengthSquared <= 0.000001f ? 0f :
                ((point.X - firstX) * segmentX + (point.Z - firstZ) * segmentZ) /
                segmentLengthSquared;
            projection = Math.Max(0f, Math.Min(1f, projection));
            float nearestX = firstX + segmentX * projection;
            float nearestZ = firstZ + segmentZ * projection;
            float offsetX = point.X - nearestX;
            float offsetZ = point.Z - nearestZ;
            return (float)Math.Sqrt(offsetX * offsetX + offsetZ * offsetZ);
        }

        private static int Cell(float value)
        {
            return (int)Math.Floor(value / BucketSize);
        }

        private static long Key(int x, int z)
        {
            return ((long)x << 32) ^ (uint)z;
        }

        private void AdvanceSelectionStamp()
        {
            _selectionStamp++;
            if (_selectionStamp != int.MaxValue) return;
            Array.Clear(_selectionStamps, 0, _selectionStamps.Length);
            _selectionStamp = 1;
        }
    }
}
