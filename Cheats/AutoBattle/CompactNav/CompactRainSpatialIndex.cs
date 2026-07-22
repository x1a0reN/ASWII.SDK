using System;
using System.IO;

namespace ASWDEBUG.Cheats.AutoBattle.CompactNav
{
    internal sealed class CompactRainSpatialIndex
    {
        private const int LeafSize = 8;
        private const int NodeBytes = 40;

        private readonly CompactRainNavDataset _dataset;
        private readonly CompactRainBvhNode[] _nodes;
        private readonly int[] _polyOrder;
        private int _nodeCursor;

        internal CompactRainSpatialIndex(CompactRainNavDataset dataset)
        {
            if (dataset == null || dataset.PolyCount <= 0) throw new ArgumentNullException("dataset");
            _dataset = dataset;
            _polyOrder = new int[dataset.PolyCount];
            for (int i = 0; i < _polyOrder.Length; i++) _polyOrder[i] = i;
            _nodes = new CompactRainBvhNode[CountNodes(dataset.PolyCount)];
            int root = BuildNode(0, _polyOrder.Length);
            if (root != 0 || _nodeCursor != _nodes.Length)
                throw new InvalidDataException("aswnav_bvh_build=" + root + "/" + _nodeCursor + "/" + _nodes.Length);
            ValidatePermutation();
        }

        internal int NodeCount { get { return _nodes.Length; } }

        internal long EstimatedBytes
        {
            get { return (long)_nodes.Length * NodeBytes + (long)_polyOrder.Length * sizeof(int); }
        }

        internal bool TryProject(CompactRainPoint input, float maximumHorizontalError,
            float maximumVerticalError, out CompactRainProjection projection)
        {
            int[] stack = new int[64];
            return TryProject(input, maximumHorizontalError, maximumVerticalError, stack,
                out projection);
        }

        internal bool TryProject(CompactRainPoint input, float maximumHorizontalError,
            float maximumVerticalError, int[] stack, out CompactRainProjection projection)
        {
            projection = new CompactRainProjection();
            projection.PolyIndex = -1;
            if (!Finite(input.X) || !Finite(input.Y) || !Finite(input.Z) ||
                maximumHorizontalError < 0f || maximumVerticalError <= 0f) return false;
            if (stack == null || stack.Length < 64)
                throw new ArgumentException("aswnav_projection_workspace");

            int stackCount = 1;
            stack[0] = 0;
            bool foundExact = false;
            float bestVertical = float.MaxValue;
            float bestScore = float.MaxValue;
            while (stackCount > 0)
            {
                CompactRainBvhNode node = _nodes[stack[--stackCount]];
                if (!Overlaps(node, input, maximumHorizontalError, maximumVerticalError)) continue;
                if (node.Count > 0)
                {
                    for (int i = 0; i < node.Count; i++)
                    {
                        int polyIndex = _polyOrder[node.Start + i];
                        CompactRainProjection candidate;
                        if (!TryProjectPoly(polyIndex, input, maximumHorizontalError,
                            maximumVerticalError, out candidate)) continue;
                        if (candidate.ExactXZ)
                        {
                            if (!foundExact || candidate.VerticalError < bestVertical)
                            {
                                projection = candidate;
                                bestVertical = candidate.VerticalError;
                                foundExact = true;
                            }
                            continue;
                        }
                        if (foundExact) continue;
                        float score = candidate.HorizontalError + candidate.VerticalError * 0.35f;
                        if (score >= bestScore) continue;
                        projection = candidate;
                        bestScore = score;
                    }
                    continue;
                }
                if (stackCount + 2 > stack.Length)
                    throw new InvalidDataException("aswnav_bvh_depth");
                stack[stackCount++] = node.Left;
                stack[stackCount++] = node.Right;
            }
            return projection.PolyIndex >= 0;
        }

        private int BuildNode(int start, int count)
        {
            int nodeIndex = _nodeCursor++;
            CompactRainBvhNode node = CalculateBounds(start, count);
            if (count <= LeafSize)
            {
                node.Start = start;
                node.Count = count;
                node.Left = -1;
                node.Right = -1;
                _nodes[nodeIndex] = node;
                return nodeIndex;
            }

            float sizeX = node.MaximumX - node.MinimumX;
            float sizeY = node.MaximumY - node.MinimumY;
            float sizeZ = node.MaximumZ - node.MinimumZ;
            int axis = sizeX >= sizeY && sizeX >= sizeZ ? 0 : (sizeY >= sizeZ ? 1 : 2);
            int middle = start + count / 2;
            SelectMedian(start, start + count - 1, middle, axis);
            node.Start = -1;
            node.Count = 0;
            node.Left = BuildNode(start, middle - start);
            node.Right = BuildNode(middle, start + count - middle);
            _nodes[nodeIndex] = node;
            return nodeIndex;
        }

        private CompactRainBvhNode CalculateBounds(int start, int count)
        {
            CompactRainNavPolyRecord first = _dataset.GetPoly(_polyOrder[start]);
            CompactRainBvhNode node = new CompactRainBvhNode();
            SetBounds(ref node, first);
            for (int i = 1; i < count; i++) Encapsulate(ref node, _dataset.GetPoly(_polyOrder[start + i]));
            return node;
        }

        private void SelectMedian(int left, int right, int target, int axis)
        {
            while (left < right)
            {
                float pivot = Center(_polyOrder[left + (right - left) / 2], axis);
                int low = left;
                int high = right;
                while (low <= high)
                {
                    while (Center(_polyOrder[low], axis) < pivot) low++;
                    while (Center(_polyOrder[high], axis) > pivot) high--;
                    if (low > high) continue;
                    int swap = _polyOrder[low];
                    _polyOrder[low] = _polyOrder[high];
                    _polyOrder[high] = swap;
                    low++;
                    high--;
                }
                if (target <= high) right = high;
                else if (target >= low) left = low;
                else return;
            }
        }

        private float Center(int polyIndex, int axis)
        {
            CompactRainNavPolyRecord poly = _dataset.GetPoly(polyIndex);
            if (axis == 0) return poly.BoundsCenterX;
            return axis == 1 ? poly.BoundsCenterY : poly.BoundsCenterZ;
        }

        private bool TryProjectPoly(int polyIndex, CompactRainPoint input,
            float maximumHorizontalError, float maximumVerticalError,
            out CompactRainProjection projection)
        {
            projection = new CompactRainProjection();
            projection.PolyIndex = -1;
            CompactRainNavPolyRecord poly = _dataset.GetPoly(polyIndex);
            if ((poly.Flags & CompactRainNavFormat.PolyUnwalkable) != 0) return false;
            bool found = false;
            bool foundExact = false;
            float bestVertical = float.MaxValue;
            float bestScore = float.MaxValue;
            CompactRainPoint bestPoint = input;
            int end = poly.TriangleStart + poly.TriangleCount;
            for (int i = poly.TriangleStart; i < end; i += 3)
            {
                CompactRainPoint a = _dataset.GetVertex(_dataset.GetTriangleIndex(i));
                CompactRainPoint b = _dataset.GetVertex(_dataset.GetTriangleIndex(i + 1));
                CompactRainPoint c = _dataset.GetVertex(_dataset.GetTriangleIndex(i + 2));
                CompactRainPoint point;
                bool exact = TryInterceptTriangleXZ(input, a, b, c, out point);
                if (!exact) point = ClosestPointOnTriangleEdgesXZ(input, a, b, c);
                float horizontal = CompactRainPoint.DistanceXZ(input, point);
                float vertical = Math.Abs(input.Y - point.Y);
                if (horizontal > maximumHorizontalError + 0.0001f ||
                    vertical > maximumVerticalError + 0.0001f) continue;
                if (exact)
                {
                    if (!foundExact || vertical < bestVertical)
                    {
                        bestPoint = point;
                        bestVertical = vertical;
                        foundExact = true;
                        found = true;
                    }
                    continue;
                }
                if (foundExact) continue;
                float score = horizontal + vertical * 0.35f;
                if (score >= bestScore) continue;
                bestScore = score;
                bestPoint = point;
                found = true;
            }
            if (!found) return false;
            projection.PolyIndex = polyIndex;
            projection.Point = bestPoint;
            projection.HorizontalError = CompactRainPoint.DistanceXZ(input, bestPoint);
            projection.VerticalError = Math.Abs(input.Y - bestPoint.Y);
            projection.ExactXZ = foundExact;
            return true;
        }

        private static bool TryInterceptTriangleXZ(CompactRainPoint point, CompactRainPoint a,
            CompactRainPoint b, CompactRainPoint c, out CompactRainPoint intercept)
        {
            intercept = point;
            float denominator = (b.Z - c.Z) * (a.X - c.X) + (c.X - b.X) * (a.Z - c.Z);
            if (Math.Abs(denominator) < 0.0000001f) return false;
            float one = ((b.Z - c.Z) * (point.X - c.X) +
                (c.X - b.X) * (point.Z - c.Z)) / denominator;
            float two = ((c.Z - a.Z) * (point.X - c.X) +
                (a.X - c.X) * (point.Z - c.Z)) / denominator;
            float three = 1f - one - two;
            const float epsilon = -0.0001f;
            if (one < epsilon || two < epsilon || three < epsilon) return false;
            intercept.Y = one * a.Y + two * b.Y + three * c.Y;
            return Finite(intercept.Y);
        }

        private static CompactRainPoint ClosestPointOnTriangleEdgesXZ(CompactRainPoint point,
            CompactRainPoint a, CompactRainPoint b, CompactRainPoint c)
        {
            CompactRainPoint ab = ClosestPointOnSegmentXZ(point, a, b);
            CompactRainPoint bc = ClosestPointOnSegmentXZ(point, b, c);
            CompactRainPoint ca = ClosestPointOnSegmentXZ(point, c, a);
            float distanceAb = DistanceSquaredXZ(point, ab);
            float distanceBc = DistanceSquaredXZ(point, bc);
            float distanceCa = DistanceSquaredXZ(point, ca);
            if (distanceAb <= distanceBc && distanceAb <= distanceCa) return ab;
            return distanceBc <= distanceCa ? bc : ca;
        }

        private static CompactRainPoint ClosestPointOnSegmentXZ(CompactRainPoint point,
            CompactRainPoint start, CompactRainPoint end)
        {
            float x = end.X - start.X;
            float z = end.Z - start.Z;
            float length = x * x + z * z;
            float t = length < 0.0000001f ? 0f :
                ((point.X - start.X) * x + (point.Z - start.Z) * z) / length;
            if (t < 0f) t = 0f;
            else if (t > 1f) t = 1f;
            return new CompactRainPoint(start.X + x * t,
                start.Y + (end.Y - start.Y) * t, start.Z + z * t);
        }

        private static bool Overlaps(CompactRainBvhNode node, CompactRainPoint point,
            float horizontal, float vertical)
        {
            return point.X >= node.MinimumX - horizontal && point.X <= node.MaximumX + horizontal &&
                point.Z >= node.MinimumZ - horizontal && point.Z <= node.MaximumZ + horizontal &&
                point.Y >= node.MinimumY - vertical && point.Y <= node.MaximumY + vertical;
        }

        private static void SetBounds(ref CompactRainBvhNode node, CompactRainNavPolyRecord poly)
        {
            node.MinimumX = poly.BoundsCenterX - poly.BoundsSizeX * 0.5f;
            node.MinimumY = poly.BoundsCenterY - poly.BoundsSizeY * 0.5f;
            node.MinimumZ = poly.BoundsCenterZ - poly.BoundsSizeZ * 0.5f;
            node.MaximumX = poly.BoundsCenterX + poly.BoundsSizeX * 0.5f;
            node.MaximumY = poly.BoundsCenterY + poly.BoundsSizeY * 0.5f;
            node.MaximumZ = poly.BoundsCenterZ + poly.BoundsSizeZ * 0.5f;
        }

        private static void Encapsulate(ref CompactRainBvhNode node, CompactRainNavPolyRecord poly)
        {
            float minX = poly.BoundsCenterX - poly.BoundsSizeX * 0.5f;
            float minY = poly.BoundsCenterY - poly.BoundsSizeY * 0.5f;
            float minZ = poly.BoundsCenterZ - poly.BoundsSizeZ * 0.5f;
            float maxX = poly.BoundsCenterX + poly.BoundsSizeX * 0.5f;
            float maxY = poly.BoundsCenterY + poly.BoundsSizeY * 0.5f;
            float maxZ = poly.BoundsCenterZ + poly.BoundsSizeZ * 0.5f;
            if (minX < node.MinimumX) node.MinimumX = minX;
            if (minY < node.MinimumY) node.MinimumY = minY;
            if (minZ < node.MinimumZ) node.MinimumZ = minZ;
            if (maxX > node.MaximumX) node.MaximumX = maxX;
            if (maxY > node.MaximumY) node.MaximumY = maxY;
            if (maxZ > node.MaximumZ) node.MaximumZ = maxZ;
        }

        private void ValidatePermutation()
        {
            bool[] seen = new bool[_polyOrder.Length];
            for (int i = 0; i < _polyOrder.Length; i++)
            {
                int value = _polyOrder[i];
                if (value < 0 || value >= seen.Length || seen[value])
                    throw new InvalidDataException("aswnav_bvh_permutation=" + value);
                seen[value] = true;
            }
        }

        private static int CountNodes(int count)
        {
            if (count <= LeafSize) return 1;
            int left = count / 2;
            return checked(1 + CountNodes(left) + CountNodes(count - left));
        }

        private static float DistanceSquaredXZ(CompactRainPoint left, CompactRainPoint right)
        {
            float x = left.X - right.X;
            float z = left.Z - right.Z;
            return x * x + z * z;
        }

        private static bool Finite(float value)
        {
            return !float.IsNaN(value) && !float.IsInfinity(value);
        }
    }

    internal struct CompactRainBvhNode
    {
        public float MinimumX;
        public float MinimumY;
        public float MinimumZ;
        public float MaximumX;
        public float MaximumY;
        public float MaximumZ;
        public int Left;
        public int Right;
        public int Start;
        public int Count;
    }
}
