using System;

namespace ASWDEBUG.Cheats.AutoBattle.CompactNav
{
    internal sealed class CompactRainCorridorValidator
    {
        private const int ProjectionStackSize = 64;
        private const int MaximumSamples = 4096;

        private readonly CompactRainNavDataset _dataset;
        private readonly int[] _projectionStack = new int[ProjectionStackSize];
        private readonly float _sampleSpacing;
        private readonly float _sideClearance;
        private readonly float _endpointTaperDistance;
        private readonly float _endpointHorizontalTolerance;
        private readonly float _endpointVerticalTolerance;
        private readonly float _sampleHorizontalTolerance;
        private readonly float _sampleVerticalTolerance;
        private readonly float _sideVerticalTolerance;

        internal CompactRainCorridorValidator(CompactRainNavDataset dataset)
        {
            if (dataset == null) throw new ArgumentNullException("dataset");
            _dataset = dataset;
            CompactRainNavHeader header = dataset.Header;
            _sampleSpacing = Clamp(header.CellSize, 0.08f, 0.12f);
            _sideClearance = Clamp(header.AgentRadius * 0.40f, 0.16f, 0.22f);
            _endpointTaperDistance = Math.Max(0.65f, header.AgentRadius + _sideClearance);
            _endpointHorizontalTolerance = Clamp(header.AgentRadius, 0.30f, 0.55f);
            _endpointVerticalTolerance = Math.Max(2.25f, header.WalkableHeight + 0.35f);
            _sampleHorizontalTolerance = Clamp(header.CellSize * 0.65f, 0.04f, 0.08f);
            _sampleVerticalTolerance = Math.Max(1.0f, header.StepHeight + 0.25f);
            _sideVerticalTolerance = Math.Max(0.55f, header.StepHeight * 0.80f);
        }

        internal long WorkspaceBytes
        {
            get { return (long)_projectionStack.Length * sizeof(int); }
        }

        internal float SideClearance
        {
            get { return _sideClearance; }
        }

        internal float SampleSpacing
        {
            get { return _sampleSpacing; }
        }

        internal float MinimumSideClearanceLength
        {
            get { return _endpointTaperDistance * 2f; }
        }

        internal bool TryProjectEndpoint(CompactRainPoint point, out CompactRainProjection projection)
        {
            return _dataset.SpatialIndex.TryProject(point, _endpointHorizontalTolerance,
                _endpointVerticalTolerance, _projectionStack, out projection);
        }

        internal bool TryValidateWalkSegment(CompactRainPoint from, CompactRainPoint to,
            out string detail)
        {
            CompactRainProjection startProjection;
            if (!TryProjectEndpoint(from, out startProjection))
                return Fail("start_projection", out detail);
            CompactRainProjection endProjection;
            if (!TryProjectEndpoint(to, out endProjection))
                return Fail("end_projection", out detail);

            int component = _dataset.GetPoly(startProjection.PolyIndex).Component;
            if (_dataset.GetPoly(endProjection.PolyIndex).Component != component)
                return Fail("component_mismatch", out detail);

            CompactRainPoint start = startProjection.Point;
            CompactRainPoint end = endProjection.Point;
            float distance = CompactRainPoint.DistanceXZ(start, end);
            if (distance <= 0.02f)
            {
                if (Math.Abs(start.Y - end.Y) > _sampleVerticalTolerance)
                    return Fail("vertical_endpoint", out detail);
                detail = "safe samples=1 clearanceMax=" + _sideClearance.ToString("0.00");
                return true;
            }

            int sampleCount = (int)Math.Ceiling(distance / _sampleSpacing);
            if (sampleCount < 1) sampleCount = 1;
            if (sampleCount > MaximumSamples)
                return Fail("sample_limit=" + sampleCount, out detail);

            float directionX = (end.X - start.X) / distance;
            float directionZ = (end.Z - start.Z) / distance;
            float sideX = -directionZ;
            float sideZ = directionX;
            bool validateSideClearance = distance > _endpointTaperDistance * 2f;
            float previousHeight = start.Y;
            for (int i = 0; i <= sampleCount; i++)
            {
                float t = (float)i / sampleCount;
                CompactRainPoint expected = Lerp(start, end, t);
                CompactRainProjection center;
                if (!_dataset.SpatialIndex.TryProject(expected, _sampleHorizontalTolerance,
                    _sampleVerticalTolerance, _projectionStack, out center) || !center.ExactXZ)
                    return Fail("center=" + i + "/" + sampleCount, out detail);
                if (_dataset.GetPoly(center.PolyIndex).Component != component)
                    return Fail("center_component=" + i + "/" + sampleCount, out detail);
                if (i > 0 && Math.Abs(center.Point.Y - previousHeight) >
                    _dataset.Header.StepHeight + 0.25f)
                    return Fail("height_step=" + i + "/" + sampleCount, out detail);
                previousHeight = center.Point.Y;
                if (!validateSideClearance) continue;

                float travelled = distance * t;
                float remaining = distance - travelled;
                float taper = Math.Min(1f, Math.Min(travelled, remaining) /
                    _endpointTaperDistance);
                float localClearance = Math.Min(_sideClearance,
                    _dataset.GetSurface(center.PolyIndex).Clearance * 0.50f);
                float clearance = localClearance * taper;
                if (clearance < 0.04f) continue;

                CompactRainPoint left = new CompactRainPoint(
                    expected.X + sideX * clearance, center.Point.Y,
                    expected.Z + sideZ * clearance);
                CompactRainPoint right = new CompactRainPoint(
                    expected.X - sideX * clearance, center.Point.Y,
                    expected.Z - sideZ * clearance);
                bool leftSafe = TryValidateSide(left, center.Point.Y, component);
                bool rightSafe = TryValidateSide(right, center.Point.Y, component);
                if (leftSafe && rightSafe) continue;
                float leftClearance = leftSafe ? clearance : MeasureSideClearance(expected,
                    center.Point.Y, sideX, sideZ, clearance, component);
                float rightClearance = rightSafe ? clearance : MeasureSideClearance(expected,
                    center.Point.Y, -sideX, -sideZ, clearance, component);
                float maximumClearance = Math.Max(leftClearance, rightClearance);
                float minimumClearance = Math.Min(leftClearance, rightClearance);
                if (minimumClearance < 0.04f || maximumClearance <= 0f ||
                    minimumClearance / maximumClearance < 0.70f)
                    return Fail("side_balance=" + i + "/" + sampleCount +
                        " left=" + leftClearance.ToString("0.000") +
                        " right=" + rightClearance.ToString("0.000") +
                        " requested=" + clearance.ToString("0.000"), out detail);
            }

            detail = "safe samples=" + (sampleCount + 1) + " clearanceMax=" +
                _sideClearance.ToString("0.00") + " sideMinLength=" +
                MinimumSideClearanceLength.ToString("0.00");
            return true;
        }

        private bool TryValidateSide(CompactRainPoint point, float centerHeight, int component)
        {
            CompactRainProjection projection;
            if (!_dataset.SpatialIndex.TryProject(point, _sampleHorizontalTolerance,
                _sampleVerticalTolerance, _projectionStack, out projection) || !projection.ExactXZ)
                return false;
            return _dataset.GetPoly(projection.PolyIndex).Component == component &&
                Math.Abs(projection.Point.Y - centerHeight) <= _sideVerticalTolerance;
        }

        private float MeasureSideClearance(CompactRainPoint center, float centerHeight,
            float sideX, float sideZ, float maximum, int component)
        {
            float minimum = 0f;
            float maximumCandidate = maximum;
            for (int i = 0; i < 7; i++)
            {
                float candidate = (minimum + maximumCandidate) * 0.5f;
                CompactRainPoint point = new CompactRainPoint(center.X + sideX * candidate,
                    centerHeight, center.Z + sideZ * candidate);
                if (TryValidateSide(point, centerHeight, component)) minimum = candidate;
                else maximumCandidate = candidate;
            }
            return minimum;
        }

        private static CompactRainPoint Lerp(CompactRainPoint from, CompactRainPoint to, float t)
        {
            return new CompactRainPoint(from.X + (to.X - from.X) * t,
                from.Y + (to.Y - from.Y) * t,
                from.Z + (to.Z - from.Z) * t);
        }

        private static float Clamp(float value, float minimum, float maximum)
        {
            if (value < minimum) return minimum;
            return value > maximum ? maximum : value;
        }

        private static bool Fail(string reason, out string detail)
        {
            detail = "unsafe " + reason;
            return false;
        }
    }
}
