using System;
using System.Collections.Generic;
using System.IO;

namespace ASWDEBUG.Cheats.AutoBattle.CompactNav
{
    internal static class CompactRainFunnel
    {
        internal static void BuildPath(CompactRainNavDataset dataset, int[] portals,
            int[] incomingLinks, CompactRainPoint start, CompactRainPoint goal,
            out CompactRainPoint[] waypoints, out byte[] actions)
        {
            if (dataset == null || portals == null || incomingLinks == null ||
                portals.Length != incomingLinks.Length)
                throw new ArgumentNullException("compact funnel input");
            List<CompactRainPoint> points = new List<CompactRainPoint>(Math.Max(4, portals.Length + 2));
            List<byte> pointActions = new List<byte>(Math.Max(4, portals.Length + 2));
            if (portals.Length == 0)
            {
                AppendPoint(points, pointActions, start, 0);
                AppendPoint(points, pointActions, goal, 0);
                waypoints = points.ToArray();
                actions = pointActions.ToArray();
                return;
            }

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
                AppendSmoothPartition(dataset, portals, partitionStart, i - 1,
                    partitionStartPoint, linkStart, points, pointActions);
                AppendPoint(points, pointActions, linkEnd, link.Kind);
                partitionStart = i;
                partitionStartPoint = linkEnd;
            }
            AppendSmoothPartition(dataset, portals, partitionStart, portals.Length - 1,
                partitionStartPoint, goal, points, pointActions);
            waypoints = points.ToArray();
            actions = pointActions.ToArray();
        }

        private static void AppendSmoothPartition(CompactRainNavDataset dataset, int[] portals,
            int startIndex, int endIndex, CompactRainPoint start, CompactRainPoint goal,
            List<CompactRainPoint> output, List<byte> actions)
        {
            if (endIndex < startIndex)
            {
                AppendPoint(output, actions, start, 0);
                AppendPoint(output, actions, goal, 0);
                return;
            }
            int portalCount = endIndex - startIndex + 1;
            int[] rawNodes = new int[portalCount + 2];
            CompactRainPoint[] rawPoints = new CompactRainPoint[portalCount + 2];
            rawNodes[0] = -1;
            rawPoints[0] = start;
            for (int i = 0; i < portalCount; i++)
            {
                int portalIndex = portals[startIndex + i];
                rawNodes[i + 1] = portalIndex;
                rawPoints[i + 1] = dataset.GetPortalCenter(portalIndex);
            }
            rawNodes[rawNodes.Length - 1] = -1;
            rawPoints[rawPoints.Length - 1] = goal;

            List<int> smoothNodes = new List<int>();
            List<CompactRainPoint> smoothPoints = new List<CompactRainPoint>();
            smoothNodes.Add(-1);
            smoothPoints.Add(start);
            CompactRainPoint apex = start;
            bool reset = true;
            int rightIndex = -1;
            CompactRainPoint right = new CompactRainPoint();
            int rightNode = -1;
            int leftIndex = -1;
            CompactRainPoint left = new CompactRainPoint();
            int leftNode = -1;

            for (int i = 0; i < rawNodes.Length; i++)
            {
                int portalIndex = rawNodes[i];
                if (portalIndex >= 0)
                {
                    CompactRainNavPortalRecord portal = dataset.GetPortal(portalIndex);
                    CompactRainPoint one = dataset.GetVertex(portal.VertexOne);
                    CompactRainPoint two = dataset.GetVertex(portal.VertexTwo);
                    if (IsOnXZ(one, two, apex)) continue;
                    CompactRainPoint candidateRight;
                    CompactRainPoint candidateLeft;
                    if (IsLeftXZ(apex, one, two))
                    {
                        candidateRight = two;
                        candidateLeft = one;
                    }
                    else
                    {
                        candidateRight = one;
                        candidateLeft = two;
                    }
                    if (reset)
                    {
                        reset = false;
                        rightIndex = i;
                        right = candidateRight;
                        rightNode = portalIndex;
                        leftIndex = i;
                        left = candidateLeft;
                        leftNode = portalIndex;
                    }
                    else
                    {
                        if (IsRightXZ(apex, right, candidateRight))
                        {
                            if (IsRightOrOnXZ(apex, left, candidateRight))
                            {
                                apex = left;
                                smoothPoints.Add(left);
                                smoothNodes.Add(leftNode);
                                i = leftIndex;
                                reset = true;
                            }
                            else
                            {
                                rightIndex = i;
                                right = candidateRight;
                                rightNode = portalIndex;
                            }
                        }
                        if (IsLeftXZ(apex, left, candidateLeft))
                        {
                            if (IsLeftOrOnXZ(apex, right, candidateLeft))
                            {
                                apex = right;
                                smoothPoints.Add(right);
                                smoothNodes.Add(rightNode);
                                i = rightIndex;
                                reset = true;
                            }
                            else
                            {
                                leftIndex = i;
                                left = candidateLeft;
                                leftNode = portalIndex;
                            }
                        }
                    }
                }
                if (!reset && i == rawNodes.Length - 1)
                {
                    if (IsRightXZ(apex, left, rawPoints[i]))
                    {
                        apex = left;
                        smoothPoints.Add(left);
                        smoothNodes.Add(leftNode);
                        i = leftIndex;
                        reset = true;
                    }
                    else if (IsLeftXZ(apex, right, rawPoints[i]))
                    {
                        apex = right;
                        smoothPoints.Add(right);
                        smoothNodes.Add(rightNode);
                        i = rightIndex;
                        reset = true;
                    }
                }
            }
            smoothPoints.Add(goal);
            smoothNodes.Add(-1);

            for (int i = 1; i < rawNodes.Length && i < smoothNodes.Count; i++)
            {
                if (rawNodes[i] < 0 || smoothNodes[i] == rawNodes[i]) continue;
                CompactRainNavPortalRecord portal = dataset.GetPortal(rawNodes[i]);
                CompactRainPoint crossing = IntersectPoints2D(smoothPoints[i - 1], smoothPoints[i],
                    dataset.GetVertex(portal.VertexOne), dataset.GetVertex(portal.VertexTwo));
                smoothNodes.Insert(i, rawNodes[i]);
                smoothPoints.Insert(i, crossing);
            }
            for (int i = 0; i < smoothPoints.Count; i++) AppendPoint(output, actions, smoothPoints[i], 0);
        }

        private static CompactRainPoint IntersectPoints2D(CompactRainPoint firstStart,
            CompactRainPoint firstFinish, CompactRainPoint secondStart, CompactRainPoint secondFinish)
        {
            float denominator = (firstStart.X - firstFinish.X) * (secondStart.Z - secondFinish.Z) -
                (firstStart.Z - firstFinish.Z) * (secondStart.X - secondFinish.X);
            if (Math.Abs(denominator) < 0.000001f)
                return ClosestPointOnSegmentXZ(firstFinish, secondStart, secondFinish);
            float firstCross = firstStart.X * firstFinish.Z - firstStart.Z * firstFinish.X;
            float secondCross = secondStart.X * secondFinish.Z - secondStart.Z * secondFinish.X;
            float x = (firstCross * (secondStart.X - secondFinish.X) -
                secondCross * (firstStart.X - firstFinish.X)) / denominator;
            float z = (firstCross * (secondStart.Z - secondFinish.Z) -
                secondCross * (firstStart.Z - firstFinish.Z)) / denominator;
            return new CompactRainPoint(x, (firstStart.Y + secondFinish.Y) * 0.5f, z);
        }

        private static CompactRainPoint ClosestPointOnSegmentXZ(CompactRainPoint point,
            CompactRainPoint start, CompactRainPoint end)
        {
            float x = end.X - start.X;
            float z = end.Z - start.Z;
            float length = x * x + z * z;
            float t = length <= 0.000001f ? 0f :
                ((point.X - start.X) * x + (point.Z - start.Z) * z) / length;
            if (t < 0f) t = 0f;
            else if (t > 1f) t = 1f;
            return new CompactRainPoint(start.X + x * t,
                start.Y + (end.Y - start.Y) * t, start.Z + z * t);
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

        private static float Area2D(CompactRainPoint a, CompactRainPoint b, CompactRainPoint c)
        {
            return (b.X - a.X) * (c.Z - a.Z) - (c.X - a.X) * (b.Z - a.Z);
        }

        private static bool IsOnXZ(CompactRainPoint a, CompactRainPoint b, CompactRainPoint c)
        {
            return Area2D(a, b, c) == 0f;
        }

        private static bool IsLeftXZ(CompactRainPoint a, CompactRainPoint b, CompactRainPoint c)
        {
            return Area2D(a, b, c) > 0f;
        }

        private static bool IsLeftOrOnXZ(CompactRainPoint a, CompactRainPoint b, CompactRainPoint c)
        {
            return Area2D(a, b, c) >= 0f;
        }

        private static bool IsRightXZ(CompactRainPoint a, CompactRainPoint b, CompactRainPoint c)
        {
            return Area2D(a, b, c) < 0f;
        }

        private static bool IsRightOrOnXZ(CompactRainPoint a, CompactRainPoint b, CompactRainPoint c)
        {
            return Area2D(a, b, c) <= 0f;
        }

        private static bool Finite(CompactRainPoint value)
        {
            return CompactRainNavFormat.IsFinite(value.X) && CompactRainNavFormat.IsFinite(value.Y) &&
                CompactRainNavFormat.IsFinite(value.Z);
        }
    }
}
