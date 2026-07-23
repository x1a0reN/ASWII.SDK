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
        private readonly float _minimumBoundaryClearance;
        private readonly float _preferredBoundaryClearance;
        private readonly float _recoveryDistance;
        private readonly float _endpointHorizontalTolerance;
        private readonly float _endpointVerticalTolerance;
        private readonly float _sampleHorizontalTolerance;
        private readonly float _sampleVerticalTolerance;
        private readonly float _boundaryVerticalTolerance;

        internal CompactRainCorridorValidator(CompactRainNavDataset dataset)
        {
            if (dataset == null) throw new ArgumentNullException("dataset");
            _dataset = dataset;
            CompactRainNavHeader header = dataset.Header;
            _sampleSpacing = Clamp(header.CellSize, 0.08f, 0.12f);
            _minimumBoundaryClearance = Clamp(header.AgentRadius + 0.35f, 0.80f, 1.0f);
            _preferredBoundaryClearance = _minimumBoundaryClearance + 0.55f;
            _recoveryDistance = Math.Max(1.20f, _minimumBoundaryClearance * 1.5f);
            _endpointHorizontalTolerance = Clamp(header.AgentRadius, 0.30f, 0.55f);
            _endpointVerticalTolerance = Math.Max(2.25f, header.WalkableHeight + 0.35f);
            _sampleHorizontalTolerance = Clamp(header.CellSize * 0.65f, 0.04f, 0.08f);
            _sampleVerticalTolerance = Math.Max(1.0f, header.StepHeight + 0.25f);
            _boundaryVerticalTolerance = Math.Max(0.90f, header.StepHeight + 0.15f);
        }

        internal long WorkspaceBytes
        {
            get { return (long)_projectionStack.Length * sizeof(int); }
        }

        internal float SideClearance
        {
            get { return _minimumBoundaryClearance; }
        }

        internal float MinimumBoundaryClearance
        {
            get { return _minimumBoundaryClearance; }
        }

        internal float PreferredBoundaryClearance
        {
            get { return _preferredBoundaryClearance; }
        }

        internal float SampleSpacing
        {
            get { return _sampleSpacing; }
        }

        internal float MinimumSideClearanceLength
        {
            get { return _recoveryDistance; }
        }

        internal bool TryProjectEndpoint(CompactRainPoint point, out CompactRainProjection projection)
        {
            return _dataset.SpatialIndex.TryProject(point, _endpointHorizontalTolerance,
                _endpointVerticalTolerance, _projectionStack, out projection);
        }

        internal bool TryValidateWalkSegment(CompactRainPoint from, CompactRainPoint to,
            out string detail)
        {
            return TryValidateWalkSegment(from, to, false, out detail);
        }

        internal bool TryValidateWalkSegment(CompactRainPoint from, CompactRainPoint to,
            bool allowUnsafeStart, out string detail)
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
            float startClearance = MeasureProjectionClearance(startProjection);
            if (distance <= 0.02f)
            {
                if (Math.Abs(start.Y - end.Y) > _sampleVerticalTolerance)
                    return Fail("vertical_endpoint", out detail);
                if (!allowUnsafeStart &&
                    (startClearance + 0.015f < _minimumBoundaryClearance ||
                    !IsProjectionSafe(startProjection, _minimumBoundaryClearance)))
                    return Fail("point_clearance=" + startClearance.ToString("0.000") +
                        "/" + _minimumBoundaryClearance.ToString("0.000"), out detail);
                detail = "safe";
                return true;
            }

            int sampleCount = (int)Math.Ceiling(distance / _sampleSpacing);
            if (sampleCount < 1) sampleCount = 1;
            if (sampleCount > MaximumSamples)
                return Fail("sample_limit=" + sampleCount, out detail);

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
                float travelled = distance * t;
                float requiredClearance = _minimumBoundaryClearance;
                if (allowUnsafeStart && i < sampleCount && travelled < _recoveryDistance)
                    requiredClearance *= travelled / _recoveryDistance;
                float observedClearance = MeasureProjectionClearance(center);
                if (requiredClearance <= 0.04f) continue;
                if (observedClearance + 0.015f < requiredClearance ||
                    !IsProjectionSafe(center, requiredClearance))
                    return Fail("boundary_clearance=" + i + "/" + sampleCount +
                        " observed=" + observedClearance.ToString("0.000") +
                        " required=" + requiredClearance.ToString("0.000") +
                        " recovery=" + (allowUnsafeStart ? "1" : "0"), out detail);
            }

            detail = "safe";
            return true;
        }

        internal bool TryMeasurePointClearance(CompactRainPoint point,
            out CompactRainProjection projection, out float clearance)
        {
            clearance = 0f;
            if (!TryProjectEndpoint(point, out projection) || !projection.ExactXZ)
                return false;
            clearance = MeasureProjectionClearance(projection);
            return true;
        }

        internal bool IsPointSafe(CompactRainPoint point, out float clearance,
            out string detail)
        {
            CompactRainProjection projection;
            if (!TryMeasurePointClearance(point, out projection, out clearance))
                return Fail("point_projection", out detail);
            if (clearance + 0.015f < _minimumBoundaryClearance ||
                !IsProjectionSafe(projection, _minimumBoundaryClearance))
            {
                return Fail("point_clearance=" + clearance.ToString("0.000") +
                    "/" + _minimumBoundaryClearance.ToString("0.000"), out detail);
            }
            detail = "safe point_clearance=" + clearance.ToString("0.000") +
                " required=" + _minimumBoundaryClearance.ToString("0.000");
            return true;
        }

        internal float MeasureBoundaryClearance(CompactRainPoint point, int component)
        {
            return _dataset.BoundaryIndex.MeasureClearance(point, component,
                _preferredBoundaryClearance, _boundaryVerticalTolerance);
        }

        private float MeasureProjectionClearance(CompactRainProjection projection)
        {
            int component = _dataset.GetPoly(projection.PolyIndex).Component;
            if (_dataset.BoundaryIndex.HasBoundaries)
                return MeasureBoundaryClearance(projection.Point, component);
            return Math.Min(_preferredBoundaryClearance,
                _dataset.GetSurface(projection.PolyIndex).Clearance);
        }

        private bool IsProjectionSafe(CompactRainProjection projection, float clearance)
        {
            if (_dataset.BoundaryIndex.HasBoundaries) return true;
            if (clearance <= 0.04f) return true;
            int component = _dataset.GetPoly(projection.PolyIndex).Component;
            for (int i = 0; i < 8; i++)
            {
                double angle = i * Math.PI * 0.25;
                CompactRainPoint sample = new CompactRainPoint(
                    projection.Point.X + (float)Math.Cos(angle) * clearance,
                    projection.Point.Y,
                    projection.Point.Z + (float)Math.Sin(angle) * clearance);
                CompactRainProjection radial;
                if (!_dataset.SpatialIndex.TryProject(sample, _sampleHorizontalTolerance,
                    _sampleVerticalTolerance, _projectionStack, out radial) ||
                    !radial.ExactXZ ||
                    _dataset.GetPoly(radial.PolyIndex).Component != component ||
                    Math.Abs(radial.Point.Y - projection.Point.Y) >
                    _boundaryVerticalTolerance)
                    return false;
            }
            return true;
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
