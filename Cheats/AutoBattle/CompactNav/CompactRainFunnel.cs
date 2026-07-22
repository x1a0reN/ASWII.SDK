using System;
using System.Collections.Generic;
using System.IO;

namespace ASWDEBUG.Cheats.AutoBattle.CompactNav
{
    internal static class CompactRainFunnel
    {
        private const int MaximumRepairCandidates = 128;

        internal static bool BuildPath(CompactRainNavDataset dataset,
            CompactRainCorridorValidator validator, int[] portals, int[] incomingLinks,
            CompactRainPoint start, CompactRainPoint goal,
            out CompactRainPoint[] waypoints, out byte[] actions, out string detail)
        {
            waypoints = null;
            actions = null;
            detail = "corridor=invalid";
            if (dataset == null || validator == null || portals == null || incomingLinks == null ||
                portals.Length != incomingLinks.Length)
                throw new ArgumentNullException("compact funnel input");
            List<CompactRainPoint> points = new List<CompactRainPoint>(Math.Max(4, portals.Length + 2));
            List<byte> pointActions = new List<byte>(Math.Max(4, portals.Length + 2));
            CompactRainFunnelStats stats = new CompactRainFunnelStats();
            int partitionStart = 0;
            CompactRainPoint partitionStartPoint = start;
            for (int i = 0; i < portals.Length; i++)
            {
                int linkIndex = incomingLinks[i];
                if (linkIndex < 0) continue;
                if (i <= 0) throw new InvalidDataException("aswnav_link_without_parent");
                CompactRainNavLinkRecord link = dataset.GetLink(linkIndex);
                if (link.FromPortal != portals[i - 1] || link.ToPortal != portals[i])
                    throw new InvalidDataException("aswnav_link_corridor=" + linkIndex);
                CompactRainPoint linkStart = new CompactRainPoint(link.StartX, link.StartY, link.StartZ);
                CompactRainPoint linkEnd = new CompactRainPoint(link.EndX, link.EndY, link.EndZ);
                string partitionDetail;
                if (!AppendSafePartition(dataset, validator, portals, partitionStart, i - 1,
                    partitionStartPoint, linkStart, points, pointActions, ref stats,
                    out partitionDetail))
                {
                    detail = "corridor=failed partition=" + stats.Partitions + " " + partitionDetail;
                    return false;
                }
                AppendPoint(points, pointActions, linkEnd, link.Kind);
                partitionStart = i;
                partitionStartPoint = linkEnd;
            }
            string finalDetail;
            if (!AppendSafePartition(dataset, validator, portals, partitionStart,
                portals.Length - 1, partitionStartPoint, goal, points, pointActions,
                ref stats, out finalDetail))
            {
                detail = "corridor=failed partition=" + stats.Partitions + " " + finalDetail;
                return false;
            }
            waypoints = points.ToArray();
            actions = pointActions.ToArray();
            detail = "corridor=centerline partitions=" + stats.Partitions +
                " repairs=" + stats.Repairs + " shortcuts=" + stats.Shortcuts +
                " checks=" + stats.Checks + " spacing=" +
                validator.SampleSpacing.ToString("0.00") + " clearanceMax=" +
                validator.SideClearance.ToString("0.00") + " sideMinLength=" +
                validator.MinimumSideClearanceLength.ToString("0.00");
            return true;
        }

        private static bool AppendSafePartition(CompactRainNavDataset dataset,
            CompactRainCorridorValidator validator, int[] portals,
            int startIndex, int endIndex, CompactRainPoint start, CompactRainPoint goal,
            List<CompactRainPoint> output, List<byte> actions, ref CompactRainFunnelStats stats,
            out string detail)
        {
            stats.Partitions++;
            int portalCount = endIndex < startIndex ? 0 : endIndex - startIndex + 1;
            List<int> rawNodes = new List<int>(portalCount + 2);
            List<CompactRainPoint> rawPoints = new List<CompactRainPoint>(portalCount + 2);
            rawNodes.Add(-1);
            rawPoints.Add(start);
            for (int i = 0; i < portalCount; i++)
            {
                int portalIndex = portals[startIndex + i];
                rawNodes.Add(portalIndex);
                rawPoints.Add(dataset.GetPortalCenter(portalIndex));
            }
            rawNodes.Add(-1);
            rawPoints.Add(goal);

            List<CompactRainPoint> safePoints = new List<CompactRainPoint>(rawPoints.Count + 4);
            safePoints.Add(rawPoints[0]);
            for (int i = 1; i < rawPoints.Count; i++)
            {
                CompactRainPoint previous = safePoints[safePoints.Count - 1];
                CompactRainPoint current = rawPoints[i];
                string segmentDetail;
                stats.Checks++;
                if (validator.TryValidateWalkSegment(previous, current, out segmentDetail))
                {
                    AddDistinct(safePoints, current);
                    continue;
                }

                int transitionPoly = FindTransitionPoly(dataset, validator,
                    rawNodes[i - 1], rawNodes[i], rawPoints[i - 1], current);
                List<CompactRainPoint> repairs;
                if (transitionPoly < 0 || !TryFindRepairPath(dataset, validator,
                    transitionPoly, previous, current, ref stats, out repairs))
                {
                    detail = "segment=" + (i - 1) + "->" + i + " poly=" +
                        transitionPoly + " nodes=" + rawNodes[i - 1] + "->" +
                        rawNodes[i] + " distance=" +
                        CompactRainPoint.DistanceXZ(previous, current).ToString("0.00") +
                        " rawDistance=" + CompactRainPoint.DistanceXZ(rawPoints[i - 1],
                        current).ToString("0.00") + " " + segmentDetail;
                    return false;
                }
                for (int repairIndex = 0; repairIndex < repairs.Count; repairIndex++)
                    AddDistinct(safePoints, repairs[repairIndex]);
                AddDistinct(safePoints, current);
                stats.Repairs += repairs.Count;
            }

            List<CompactRainPoint> simplified = new List<CompactRainPoint>(safePoints.Count);
            int anchor = 0;
            simplified.Add(safePoints[0]);
            while (anchor < safePoints.Count - 1)
            {
                int selected = anchor + 1;
                int furthest = Math.Min(safePoints.Count - 1, anchor + 16);
                for (int candidate = furthest; candidate > anchor + 1; candidate--)
                {
                    string shortcutDetail;
                    stats.Checks++;
                    if (!validator.TryValidateWalkSegment(safePoints[anchor],
                        safePoints[candidate], out shortcutDetail)) continue;
                    selected = candidate;
                    break;
                }
                if (selected > anchor + 1) stats.Shortcuts += selected - anchor - 1;
                simplified.Add(safePoints[selected]);
                anchor = selected;
            }

            for (int i = 0; i < simplified.Count; i++)
                AppendPoint(output, actions, simplified[i], 0);
            detail = "safe points=" + simplified.Count;
            return true;
        }

        private static int FindTransitionPoly(CompactRainNavDataset dataset,
            CompactRainCorridorValidator validator, int fromNode, int toNode,
            CompactRainPoint from, CompactRainPoint to)
        {
            CompactRainProjection fromProjection;
            CompactRainProjection toProjection;
            if (!validator.TryProjectEndpoint(from, out fromProjection) ||
                !validator.TryProjectEndpoint(to, out toProjection)) return -1;
            int component = dataset.GetPoly(fromProjection.PolyIndex).Component;
            if (dataset.GetPoly(toProjection.PolyIndex).Component != component) return -1;

            if (fromNode < 0 && toNode < 0)
                return fromProjection.PolyIndex == toProjection.PolyIndex
                    ? fromProjection.PolyIndex : -1;
            if (fromNode < 0)
                return PortalTouchesPoly(dataset, toNode, fromProjection.PolyIndex)
                    ? fromProjection.PolyIndex : -1;
            if (toNode < 0)
                return PortalTouchesPoly(dataset, fromNode, toProjection.PolyIndex)
                    ? toProjection.PolyIndex : -1;

            CompactRainNavPortalRecord left = dataset.GetPortal(fromNode);
            int best = -1;
            float bestClearance = float.MinValue;
            for (int i = 0; i < left.PolyCount; i++)
            {
                int polyIndex = dataset.GetPortalPolyIndex(left.PolyStart + i);
                CompactRainNavPolyRecord poly = dataset.GetPoly(polyIndex);
                if ((poly.Flags & CompactRainNavFormat.PolyUnwalkable) != 0 ||
                    poly.Component != component ||
                    !dataset.IsPortalOnPolyBoundary(fromNode, polyIndex) ||
                    !dataset.IsPortalOnPolyBoundary(toNode, polyIndex))
                    continue;
                float clearance = dataset.GetSurface(polyIndex).Clearance;
                if (clearance <= bestClearance) continue;
                bestClearance = clearance;
                best = polyIndex;
            }
            return best;
        }

        private static bool PortalTouchesPoly(CompactRainNavDataset dataset, int portalIndex,
            int polyIndex)
        {
            if (!dataset.IsPortalOnPolyBoundary(portalIndex, polyIndex)) return false;
            CompactRainNavPortalRecord portal = dataset.GetPortal(portalIndex);
            for (int i = 0; i < portal.PolyCount; i++)
                if (dataset.GetPortalPolyIndex(portal.PolyStart + i) == polyIndex) return true;
            return false;
        }

        private static bool TryFindRepairPath(CompactRainNavDataset dataset,
            CompactRainCorridorValidator validator, int polyIndex, CompactRainPoint from,
            CompactRainPoint to, ref CompactRainFunnelStats stats,
            out List<CompactRainPoint> repairs)
        {
            repairs = new List<CompactRainPoint>();
            List<CompactRainPoint> candidates = new List<CompactRainPoint>(32);
            candidates.Add(from);
            candidates.Add(to);
            CompactRainNavSurfaceRecord surface = dataset.GetSurface(polyIndex);
            CompactRainPoint surfacePoint = new CompactRainPoint(surface.PositionX,
                surface.PositionY, surface.PositionZ);
            AddUniqueCandidate(candidates, surfacePoint);

            CompactRainPoint center = dataset.GetPolyCenter(polyIndex);
            AddUniqueCandidate(candidates, center);

            CompactRainNavPolyRecord poly = dataset.GetPoly(polyIndex);
            int triangleEnd = poly.TriangleStart + poly.TriangleCount;
            int triangleLimit = Math.Min(poly.TriangleCount / 3,
                MaximumRepairCandidates - candidates.Count);
            int[] triangleVertices = new int[triangleLimit * 3];
            int triangleCount = 0;
            for (int i = poly.TriangleStart; i + 2 < triangleEnd &&
                triangleCount < triangleLimit; i += 3, triangleCount++)
            {
                int aIndex = dataset.GetTriangleIndex(i);
                int bIndex = dataset.GetTriangleIndex(i + 1);
                int cIndex = dataset.GetTriangleIndex(i + 2);
                triangleVertices[triangleCount * 3] = aIndex;
                triangleVertices[triangleCount * 3 + 1] = bIndex;
                triangleVertices[triangleCount * 3 + 2] = cIndex;
                CompactRainPoint a = dataset.GetVertex(aIndex);
                CompactRainPoint b = dataset.GetVertex(bIndex);
                CompactRainPoint c = dataset.GetVertex(cIndex);
                CompactRainPoint candidate = new CompactRainPoint(
                    (a.X + b.X + c.X) / 3f, (a.Y + b.Y + c.Y) / 3f,
                    (a.Z + b.Z + c.Z) / 3f);
                AddUniqueCandidate(candidates, candidate);
            }
            for (int left = 0; left < triangleCount &&
                candidates.Count < MaximumRepairCandidates; left++)
            {
                for (int right = left + 1; right < triangleCount &&
                    candidates.Count < MaximumRepairCandidates; right++)
                {
                    int sharedOne;
                    int sharedTwo;
                    if (!TryGetSharedEdge(triangleVertices, left, right,
                        out sharedOne, out sharedTwo)) continue;
                    CompactRainPoint one = dataset.GetVertex(sharedOne);
                    CompactRainPoint two = dataset.GetVertex(sharedTwo);
                    AddUniqueCandidate(candidates, new CompactRainPoint(
                        (one.X + two.X) * 0.5f, (one.Y + two.Y) * 0.5f,
                        (one.Z + two.Z) * 0.5f));
                }
            }

            int count = candidates.Count;
            float[] costs = new float[count];
            int[] parents = new int[count];
            bool[] closed = new bool[count];
            for (int i = 0; i < count; i++)
            {
                costs[i] = float.MaxValue;
                parents[i] = -1;
            }
            costs[0] = 0f;
            for (int expansion = 0; expansion < count; expansion++)
            {
                int current = -1;
                float bestScore = float.MaxValue;
                for (int i = 0; i < count; i++)
                {
                    if (closed[i] || costs[i] == float.MaxValue) continue;
                    float score = costs[i] + CompactRainPoint.DistanceXZ(candidates[i], to);
                    if (score >= bestScore) continue;
                    bestScore = score;
                    current = i;
                }
                if (current < 0) break;
                if (current == 1)
                {
                    int cursor = parents[current];
                    while (cursor > 0)
                    {
                        repairs.Insert(0, candidates[cursor]);
                        cursor = parents[cursor];
                    }
                    return cursor == 0 && repairs.Count > 0;
                }
                closed[current] = true;
                for (int next = 1; next < count; next++)
                {
                    if (next == current || closed[next]) continue;
                    string segmentDetail;
                    stats.Checks++;
                    if (!validator.TryValidateWalkSegment(candidates[current],
                        candidates[next], out segmentDetail)) continue;
                    float nextCost = costs[current] + CompactRainPoint.DistanceXZ(
                        candidates[current], candidates[next]);
                    if (nextCost >= costs[next]) continue;
                    costs[next] = nextCost;
                    parents[next] = current;
                }
            }
            return false;
        }

        private static void AddUniqueCandidate(List<CompactRainPoint> candidates,
            CompactRainPoint candidate)
        {
            if (!Finite(candidate)) return;
            for (int i = 0; i < candidates.Count; i++)
            {
                if (CompactRainPoint.DistanceXZ(candidates[i], candidate) <= 0.001f &&
                    Math.Abs(candidates[i].Y - candidate.Y) <= 0.002f) return;
            }
            candidates.Add(candidate);
        }

        private static bool TryGetSharedEdge(int[] triangleVertices, int left, int right,
            out int sharedOne, out int sharedTwo)
        {
            sharedOne = -1;
            sharedTwo = -1;
            int leftStart = left * 3;
            int rightStart = right * 3;
            for (int i = 0; i < 3; i++)
            {
                int value = triangleVertices[leftStart + i];
                bool shared = false;
                for (int j = 0; j < 3; j++)
                {
                    if (triangleVertices[rightStart + j] != value) continue;
                    shared = true;
                    break;
                }
                if (!shared) continue;
                if (sharedOne < 0) sharedOne = value;
                else if (sharedTwo < 0 && value != sharedOne) sharedTwo = value;
            }
            return sharedOne >= 0 && sharedTwo >= 0;
        }

        private static void AddDistinct(List<CompactRainPoint> points, CompactRainPoint point)
        {
            if (points.Count > 0 && CompactRainPoint.DistanceXZ(points[points.Count - 1], point) <= 0.001f &&
                Math.Abs(points[points.Count - 1].Y - point.Y) <= 0.002f) return;
            points.Add(point);
        }

        private static void AppendPoint(List<CompactRainPoint> points, List<byte> actions,
            CompactRainPoint point, byte action)
        {
            if (!Finite(point)) throw new InvalidDataException("aswnav_funnel_nonfinite");
            if (points.Count > 0 && CompactRainPoint.DistanceXZ(points[points.Count - 1], point) <= 0.001f &&
                Math.Abs(points[points.Count - 1].Y - point.Y) <= 0.002f)
            {
                if (action != 0) actions[actions.Count - 1] = action;
                return;
            }
            points.Add(point);
            actions.Add(action);
        }

        private static bool Finite(CompactRainPoint value)
        {
            return CompactRainNavFormat.IsFinite(value.X) && CompactRainNavFormat.IsFinite(value.Y) &&
                CompactRainNavFormat.IsFinite(value.Z);
        }

        private struct CompactRainFunnelStats
        {
            public int Partitions;
            public int Repairs;
            public int Shortcuts;
            public int Checks;
        }
    }
}
