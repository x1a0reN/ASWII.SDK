using System;
using System.Diagnostics;
using System.IO;

namespace ASWDEBUG.Cheats.AutoBattle.CompactNav
{
    internal enum CompactRainSearchStatus
    {
        Idle,
        Pending,
        Complete,
        Failed,
        Cancelled
    }

    internal sealed class CompactRainPathfinder
    {
        private const float CostEpsilon = 0.0001f;

        private readonly CompactRainNavDataset _dataset;
        private readonly float[] _cost;
        private readonly int[] _parent;
        private readonly int[] _parentLink;
        private readonly int[] _seenStamp;
        private readonly float[] _expandedCost;
        private readonly int[] _expandedStamp;
        private readonly int[] _heapNodes;
        private readonly int[] _heapPositions;
        private readonly int[] _linkStarts;
        private readonly int[] _linkIndices;
        private readonly sbyte[] _portalSafety;
        private readonly float[] _portalClearance;
        private readonly CompactRainCorridorValidator _corridorValidator;

        private int _searchStamp;
        private int _heapCount;
        private int _startPoly;
        private int _goalPoly;
        private int _bestGoalPortal;
        private float _bestGoalCost;
        private float _goalClearance;
        private CompactRainProjection _startProjection;
        private CompactRainProjection _goalProjection;
        private CompactRainPathCapabilities _capabilities;
        private CompactRainSearchStatus _status;
        private CompactRainPathResult _result;
        private string _detail = "idle";
        private int _expandedNodes;

        internal CompactRainPathfinder(CompactRainNavDataset dataset)
        {
            if (dataset == null) throw new ArgumentNullException("dataset");
            _dataset = dataset;
            int portalCount = dataset.PortalCount;
            _cost = new float[portalCount];
            _parent = new int[portalCount];
            _parentLink = new int[portalCount];
            _seenStamp = new int[portalCount];
            _expandedCost = new float[portalCount];
            _expandedStamp = new int[portalCount];
            _heapNodes = new int[portalCount];
            _heapPositions = new int[portalCount];
            _portalSafety = new sbyte[portalCount];
            _portalClearance = new float[portalCount];
            for (int i = 0; i < _heapPositions.Length; i++) _heapPositions[i] = -1;
            BuildLinkIndex(dataset, out _linkStarts, out _linkIndices);
            _corridorValidator = new CompactRainCorridorValidator(dataset);
        }

        internal CompactRainSearchStatus Status { get { return _status; } }
        internal CompactRainPathResult Result { get { return _result; } }
        internal string Detail { get { return _detail; } }
        internal int ExpandedNodes { get { return _expandedNodes; } }

        internal long WorkspaceBytes
        {
            get
            {
                return (long)(_cost.Length + _expandedCost.Length) * sizeof(float) +
                    (long)(_parent.Length + _parentLink.Length + _seenStamp.Length +
                    _expandedStamp.Length + _heapNodes.Length + _heapPositions.Length +
                    _linkStarts.Length + _linkIndices.Length) * sizeof(int) +
                    (long)_portalSafety.Length * sizeof(sbyte) +
                    (long)_portalClearance.Length * sizeof(float) +
                    _corridorValidator.WorkspaceBytes;
            }
        }

        internal bool TryValidateWalkSegment(CompactRainPoint from, CompactRainPoint to,
            out string detail)
        {
            return _corridorValidator.TryValidateWalkSegment(from, to, out detail);
        }

        internal bool TryValidateWalkSegment(CompactRainPoint from, CompactRainPoint to,
            bool allowUnsafeStart, out string detail)
        {
            return _corridorValidator.TryValidateWalkSegment(from, to,
                allowUnsafeStart, out detail);
        }

        internal bool TryMeasurePointClearance(CompactRainPoint point,
            out float clearance, out float required, out string detail)
        {
            if (!TryMeasurePointClearance(point, out clearance, out required))
            {
                detail = "point_projection";
                return false;
            }
            CompactRainProjection projection;
            _corridorValidator.TryProjectEndpoint(point, out projection);
            detail = "clearance=" + clearance.ToString("0.000") +
                " required=" + required.ToString("0.000") +
                " poly=" + projection.PolyIndex;
            return true;
        }

        internal bool TryMeasurePointClearance(CompactRainPoint point,
            out float clearance, out float required)
        {
            required = _corridorValidator.MinimumBoundaryClearance;
            CompactRainProjection projection;
            return _corridorValidator.TryMeasurePointClearance(point,
                out projection, out clearance);
        }

        internal CompactRainSearchStatus Begin(CompactRainPoint start, CompactRainPoint goal,
            CompactRainPathCapabilities capabilities, float maximumHorizontalProjection,
            float maximumVerticalProjection)
        {
            ResetHeap();
            _result = null;
            _detail = "projecting";
            _expandedNodes = 0;
            _bestGoalPortal = -1;
            _bestGoalCost = float.MaxValue;
            _capabilities = capabilities ?? new CompactRainPathCapabilities();
            AdvanceStamp();
            if (!_dataset.SpatialIndex.TryProject(start, maximumHorizontalProjection,
                maximumVerticalProjection, out _startProjection))
                return Fail("start_projection_failed");
            if (!_dataset.SpatialIndex.TryProject(goal, maximumHorizontalProjection,
                maximumVerticalProjection, out _goalProjection))
                return Fail("goal_projection_failed");
            if (_startProjection.VerticalError > 1.10f)
                return Fail("start_layer_mismatch=" +
                    _startProjection.VerticalError.ToString("0.00"));
            CompactRainNavPolyRecord startPoly = _dataset.GetPoly(_startProjection.PolyIndex);
            CompactRainNavPolyRecord goalPoly = _dataset.GetPoly(_goalProjection.PolyIndex);
            if ((startPoly.Flags & CompactRainNavFormat.PolyUnwalkable) != 0 ||
                (goalPoly.Flags & CompactRainNavFormat.PolyUnwalkable) != 0)
                return Fail("projected_poly_unwalkable");
            float startClearance;
            string pointDetail;
            bool startSafe = _corridorValidator.IsPointSafe(_startProjection.Point,
                out startClearance, out pointDetail);
            if (!startSafe && !_capabilities.AllowUnsafeStart)
                return Fail("unsafe_start " + pointDetail);
            if (!_corridorValidator.IsPointSafe(_goalProjection.Point,
                out _goalClearance, out pointDetail))
                return Fail("unsafe_goal " + pointDetail);
            _startPoly = _startProjection.PolyIndex;
            _goalPoly = _goalProjection.PolyIndex;
            if (_startProjection.PolyIndex == _goalPoly)
            {
                CompleteDirect();
                return _status;
            }

            for (int i = 0; i < startPoly.PortalCount; i++)
            {
                int portalIndex = _dataset.GetPolyPortalIndex(startPoly.PortalStart + i);
                if (!_dataset.IsPortalOnPolyBoundary(portalIndex,
                    _startProjection.PolyIndex)) continue;
                float portalClearance;
                if (!TryGetSafePortalClearance(portalIndex, startPoly.Component,
                    out portalClearance)) continue;
                float distance = CompactRainPoint.Distance(_startProjection.Point,
                    _dataset.GetPortalCenter(portalIndex));
                float cost = AdjustForSafety(distance, portalClearance);
                Relax(portalIndex, cost, -1, -1);
            }
            if (_heapCount <= 0) return Fail("start_poly_has_no_portals");
            _status = CompactRainSearchStatus.Pending;
            _detail = "pending";
            return _status;
        }

        internal CompactRainSearchStatus Step(int maximumExpansions, double maximumMilliseconds)
        {
            if (_status != CompactRainSearchStatus.Pending) return _status;
            if (maximumExpansions <= 0) maximumExpansions = 1;
            Stopwatch stopwatch = Stopwatch.StartNew();
            int stepExpansions = 0;
            while (_heapCount > 0 && stepExpansions < maximumExpansions)
            {
                if (maximumMilliseconds > 0.0 && stepExpansions > 0 &&
                    stopwatch.Elapsed.TotalMilliseconds >= maximumMilliseconds) break;
                int portalIndex = PopHeap();
                float portalCost = _cost[portalIndex];
                if (_expandedStamp[portalIndex] == _searchStamp &&
                    portalCost >= _expandedCost[portalIndex] - CostEpsilon) continue;
                _expandedStamp[portalIndex] = _searchStamp;
                _expandedCost[portalIndex] = portalCost;
                stepExpansions++;
                _expandedNodes++;

                if (PortalTouchesPoly(portalIndex, _goalPoly))
                {
                    float goalDistance = CompactRainPoint.Distance(
                        _dataset.GetPortalCenter(portalIndex), _goalProjection.Point);
                    float candidate = portalCost + AdjustForSafety(goalDistance,
                        Math.Min(GetCachedPortalClearance(portalIndex), _goalClearance));
                    if (candidate < _bestGoalCost)
                    {
                        _bestGoalCost = candidate;
                        _bestGoalPortal = portalIndex;
                    }
                }
                ExpandPortal(portalIndex, portalCost);
                if (_bestGoalPortal >= 0 && (_heapCount == 0 ||
                    HeapPriority(_heapNodes[0]) >= _bestGoalCost - CostEpsilon))
                {
                    CompleteFromBestGoal();
                    return _status;
                }
            }
            if (_heapCount == 0)
            {
                if (_bestGoalPortal >= 0) CompleteFromBestGoal();
                else Fail("no_route expanded=" + _expandedNodes);
            }
            else _detail = "pending expanded=" + _expandedNodes + " open=" + _heapCount;
            return _status;
        }

        internal void Cancel()
        {
            if (_status != CompactRainSearchStatus.Pending) return;
            ResetHeap();
            _status = CompactRainSearchStatus.Cancelled;
            _detail = "cancelled";
        }

        private void ExpandPortal(int portalIndex, float baseCost)
        {
            CompactRainNavPortalRecord portal = _dataset.GetPortal(portalIndex);
            CompactRainPoint center = _dataset.GetPortalCenter(portalIndex);
            for (int i = 0; i < portal.PolyCount; i++)
            {
                int polyIndex = _dataset.GetPortalPolyIndex(portal.PolyStart + i);
                if (!_dataset.IsPortalOnPolyBoundary(portalIndex, polyIndex)) continue;
                CompactRainNavPolyRecord poly = _dataset.GetPoly(polyIndex);
                if ((poly.Flags & CompactRainNavFormat.PolyUnwalkable) != 0) continue;
                float polyClearance = _dataset.GetSurface(polyIndex).Clearance;
                if (polyIndex != _startPoly && polyIndex != _goalPoly &&
                    polyClearance + 0.015f <
                    _corridorValidator.MinimumBoundaryClearance) continue;
                for (int p = 0; p < poly.PortalCount; p++)
                {
                    int neighbor = _dataset.GetPolyPortalIndex(poly.PortalStart + p);
                    if (neighbor == portalIndex) continue;
                    if (!_dataset.IsPortalOnPolyBoundary(neighbor, polyIndex)) continue;
                    float neighborClearance;
                    if (!TryGetSafePortalClearance(neighbor, poly.Component,
                        out neighborClearance)) continue;
                    float transitionClearance = Math.Min(neighborClearance,
                        polyIndex == _startPoly || polyIndex == _goalPoly
                            ? neighborClearance : polyClearance);
                    float distance = CompactRainPoint.Distance(center,
                        _dataset.GetPortalCenter(neighbor));
                    float nextCost = baseCost + AdjustForSafety(distance,
                        transitionClearance);
                    Relax(neighbor, nextCost, portalIndex, -1);
                }
            }
            int linkEnd = _linkStarts[portalIndex + 1];
            for (int i = _linkStarts[portalIndex]; i < linkEnd; i++)
            {
                int linkIndex = _linkIndices[i];
                CompactRainNavLinkRecord link = _dataset.GetLink(linkIndex);
                if (!CanUseLink(link, _capabilities) || !IsSafeLink(link)) continue;
                int component = GetPortalComponent(link.ToPortal);
                float destinationClearance;
                if (component < 0 || !TryGetSafePortalClearance(link.ToPortal,
                    component, out destinationClearance)) continue;
                Relax(link.ToPortal, baseCost + AdjustForSafety(link.Cost,
                    destinationClearance), portalIndex, linkIndex);
            }
        }

        private void Relax(int portalIndex, float newCost, int parent, int parentLink)
        {
            if (!CompactRainNavFormat.IsFinite(newCost) || newCost < 0f)
                throw new InvalidDataException("aswnav_path_cost");
            if (_seenStamp[portalIndex] == _searchStamp &&
                newCost >= _cost[portalIndex] - CostEpsilon) return;
            _seenStamp[portalIndex] = _searchStamp;
            _cost[portalIndex] = newCost;
            _parent[portalIndex] = parent;
            _parentLink[portalIndex] = parentLink;
            int heapPosition = _heapPositions[portalIndex];
            if (heapPosition >= 0) MoveHeapUp(heapPosition);
            else PushHeap(portalIndex);
        }

        private bool PortalTouchesPoly(int portalIndex, int polyIndex)
        {
            if (!_dataset.IsPortalOnPolyBoundary(portalIndex, polyIndex)) return false;
            CompactRainNavPortalRecord portal = _dataset.GetPortal(portalIndex);
            for (int i = 0; i < portal.PolyCount; i++)
                if (_dataset.GetPortalPolyIndex(portal.PolyStart + i) == polyIndex) return true;
            return false;
        }

        private void CompleteDirect()
        {
            CompactRainPoint[] points;
            byte[] actions;
            string corridorDetail;
            if (!CompactRainFunnel.BuildPath(_dataset, _corridorValidator,
                new int[0], new int[0], _startProjection.Point, _goalProjection.Point,
                _capabilities.AllowUnsafeStart, out points, out actions,
                out corridorDetail))
            {
                Fail("direct_" + corridorDetail);
                return;
            }
            _result = CreateResult(points, actions, new int[0], new int[0],
                CompactRainPoint.Distance(_startProjection.Point, _goalProjection.Point));
            _status = CompactRainSearchStatus.Complete;
            _detail = "complete direct=1 " + corridorDetail;
        }

        private void CompleteFromBestGoal()
        {
            int count = 0;
            int current = _bestGoalPortal;
            while (current >= 0)
            {
                count++;
                if (count > _dataset.PortalCount) throw new InvalidDataException("aswnav_parent_cycle");
                current = _parent[current];
            }
            int[] portals = new int[count];
            int[] links = new int[count];
            current = _bestGoalPortal;
            for (int i = count - 1; i >= 0; i--)
            {
                portals[i] = current;
                links[i] = _parentLink[current];
                current = _parent[current];
            }
            CompactRainPoint[] points;
            byte[] actions;
            string corridorDetail;
            if (!CompactRainFunnel.BuildPath(_dataset, _corridorValidator, portals, links,
                _startProjection.Point, _goalProjection.Point,
                _capabilities.AllowUnsafeStart, out points, out actions,
                out corridorDetail))
            {
                Fail(corridorDetail + " expanded=" + _expandedNodes);
                return;
            }
            _result = CreateResult(points, actions, portals, links, _bestGoalCost);
            _status = CompactRainSearchStatus.Complete;
            _detail = "complete portals=" + portals.Length + " waypoints=" + points.Length +
                " expanded=" + _expandedNodes + " " + corridorDetail;
            ResetHeap();
        }

        private CompactRainPathResult CreateResult(CompactRainPoint[] points, byte[] actions,
            int[] portals, int[] links, float cost)
        {
            CompactRainPathResult result = new CompactRainPathResult();
            result.StartProjection = _startProjection;
            result.GoalProjection = _goalProjection;
            result.Waypoints = points;
            result.Actions = actions;
            result.PortalPath = portals;
            result.IncomingLinks = links;
            result.Cost = cost;
            result.ExpandedNodes = _expandedNodes;
            return result;
        }

        private CompactRainSearchStatus Fail(string detail)
        {
            ResetHeap();
            _status = CompactRainSearchStatus.Failed;
            _detail = detail;
            return _status;
        }

        private bool TryGetSafePortalClearance(int portalIndex, int component,
            out float clearance)
        {
            if (_portalSafety[portalIndex] != 0)
            {
                clearance = _portalClearance[portalIndex];
                return _portalSafety[portalIndex] > 0;
            }
            clearance = _corridorValidator.MeasureBoundaryClearance(
                _dataset.GetPortalCenter(portalIndex), component);
            _portalClearance[portalIndex] = clearance;
            bool safe = clearance + 0.015f >=
                _corridorValidator.MinimumBoundaryClearance;
            _portalSafety[portalIndex] = (sbyte)(safe ? 1 : -1);
            return safe;
        }

        private float GetCachedPortalClearance(int portalIndex)
        {
            if (_portalSafety[portalIndex] != 0)
                return _portalClearance[portalIndex];
            int component = GetPortalComponent(portalIndex);
            float clearance;
            return component >= 0 &&
                TryGetSafePortalClearance(portalIndex, component, out clearance)
                ? clearance : 0f;
        }

        private int GetPortalComponent(int portalIndex)
        {
            CompactRainNavPortalRecord portal = _dataset.GetPortal(portalIndex);
            for (int i = 0; i < portal.PolyCount; i++)
            {
                int polyIndex = _dataset.GetPortalPolyIndex(portal.PolyStart + i);
                if (polyIndex >= 0 && polyIndex < _dataset.PolyCount)
                    return _dataset.GetPoly(polyIndex).Component;
            }
            return -1;
        }

        private bool IsSafeLink(CompactRainNavLinkRecord link)
        {
            CompactRainPoint start = new CompactRainPoint(link.StartX, link.StartY,
                link.StartZ);
            CompactRainPoint end = new CompactRainPoint(link.EndX, link.EndY, link.EndZ);
            float clearance;
            string detail;
            return _corridorValidator.IsPointSafe(start, out clearance, out detail) &&
                _corridorValidator.IsPointSafe(end, out clearance, out detail);
        }

        private float AdjustForSafety(float distance, float clearance)
        {
            float preferred = _corridorValidator.PreferredBoundaryClearance;
            float minimum = _corridorValidator.MinimumBoundaryClearance;
            if (clearance >= preferred || preferred <= minimum + 0.01f)
                return distance;
            float normalized = (preferred - Math.Max(minimum, clearance)) /
                (preferred - minimum);
            return distance * (1f + normalized * normalized * 12f);
        }

        private void PushHeap(int portalIndex)
        {
            if (_heapCount >= _heapNodes.Length) throw new InvalidDataException("aswnav_heap_capacity");
            int position = _heapCount++;
            _heapNodes[position] = portalIndex;
            _heapPositions[portalIndex] = position;
            MoveHeapUp(position);
        }

        private int PopHeap()
        {
            int result = _heapNodes[0];
            _heapPositions[result] = -1;
            _heapCount--;
            if (_heapCount > 0)
            {
                int replacement = _heapNodes[_heapCount];
                _heapNodes[0] = replacement;
                _heapPositions[replacement] = 0;
                MoveHeapDown(0);
            }
            return result;
        }

        private void MoveHeapUp(int position)
        {
            int node = _heapNodes[position];
            while (position > 0)
            {
                int parentPosition = (position - 1) / 2;
                int parentNode = _heapNodes[parentPosition];
                if (!HeapLess(node, parentNode)) break;
                _heapNodes[position] = parentNode;
                _heapPositions[parentNode] = position;
                position = parentPosition;
            }
            _heapNodes[position] = node;
            _heapPositions[node] = position;
        }

        private void MoveHeapDown(int position)
        {
            int node = _heapNodes[position];
            while (true)
            {
                int left = position * 2 + 1;
                if (left >= _heapCount) break;
                int right = left + 1;
                int best = right < _heapCount && HeapLess(_heapNodes[right], _heapNodes[left]) ? right : left;
                if (!HeapLess(_heapNodes[best], node)) break;
                int child = _heapNodes[best];
                _heapNodes[position] = child;
                _heapPositions[child] = position;
                position = best;
            }
            _heapNodes[position] = node;
            _heapPositions[node] = position;
        }

        private bool HeapLess(int left, int right)
        {
            float leftPriority = HeapPriority(left);
            float rightPriority = HeapPriority(right);
            if (leftPriority < rightPriority - CostEpsilon) return true;
            if (leftPriority > rightPriority + CostEpsilon) return false;
            if (_cost[left] < _cost[right] - CostEpsilon) return true;
            if (_cost[left] > _cost[right] + CostEpsilon) return false;
            return left < right;
        }

        private float HeapPriority(int portalIndex)
        {
            return _cost[portalIndex] + CompactRainPoint.Distance(
                _dataset.GetPortalCenter(portalIndex), _goalProjection.Point);
        }

        private void ResetHeap()
        {
            for (int i = 0; i < _heapCount; i++) _heapPositions[_heapNodes[i]] = -1;
            _heapCount = 0;
        }

        private void AdvanceStamp()
        {
            _searchStamp++;
            if (_searchStamp != int.MaxValue) return;
            Array.Clear(_seenStamp, 0, _seenStamp.Length);
            Array.Clear(_expandedStamp, 0, _expandedStamp.Length);
            _searchStamp = 1;
        }

        private static void BuildLinkIndex(CompactRainNavDataset dataset, out int[] starts,
            out int[] indices)
        {
            starts = new int[dataset.PortalCount + 1];
            indices = new int[dataset.LinkCount];
            for (int i = 0; i < dataset.LinkCount; i++)
            {
                CompactRainNavLinkRecord link = dataset.GetLink(i);
                starts[link.FromPortal + 1]++;
            }
            for (int i = 1; i < starts.Length; i++) starts[i] += starts[i - 1];
            int[] cursors = new int[starts.Length];
            Array.Copy(starts, cursors, starts.Length);
            for (int i = 0; i < dataset.LinkCount; i++)
            {
                CompactRainNavLinkRecord link = dataset.GetLink(i);
                indices[cursors[link.FromPortal]++] = i;
            }
        }

        private static bool CanUseLink(CompactRainNavLinkRecord link,
            CompactRainPathCapabilities capabilities)
        {
            if (capabilities == null || !capabilities.AllowJump) return false;
            if (capabilities.JumpHeight + 0.05f < link.RequiredJumpHeight ||
                capabilities.RunSpeed + 0.05f < link.RequiredRunSpeed) return false;
            float horizontalX = link.EndX - link.StartX;
            float horizontalZ = link.EndZ - link.StartZ;
            float horizontal = (float)Math.Sqrt(horizontalX * horizontalX + horizontalZ * horizontalZ);
            float rise = link.EndY - link.StartY;
            float jumpHeight = Math.Max(1.2f, capabilities.JumpHeight);
            float jumpVelocity = capabilities.JumpVelocity > 0.1f ? capabilities.JumpVelocity :
                (float)Math.Sqrt(jumpHeight * 39.2f);
            float runSpeed = capabilities.RunSpeed > 0.1f ? capabilities.RunSpeed : 6f;
            float maximumHorizontal = runSpeed * (2f * jumpVelocity / 19.6f) * 0.65f;
            if (maximumHorizontal < 2.2f) maximumHorizontal = 2.2f;
            else if (maximumHorizontal > 4.2f) maximumHorizontal = 4.2f;
            float maximumDrop = capabilities.MaximumDrop > 0f ? capabilities.MaximumDrop :
                Math.Max(8f, jumpHeight + 2f);
            return horizontal >= 0.35f && horizontal <= maximumHorizontal + 0.05f &&
                rise <= jumpHeight * 0.92f && rise >= -maximumDrop;
        }
    }

    internal sealed class CompactRainPathCapabilities
    {
        public bool AllowJump;
        public float JumpHeight;
        public float JumpVelocity;
        public float RunSpeed;
        public float MaximumDrop;
        public bool AllowUnsafeStart;

        public CompactRainPathCapabilities()
        {
            AllowJump = false;
            JumpHeight = 0f;
            JumpVelocity = 0f;
            RunSpeed = 0f;
            MaximumDrop = 0f;
            AllowUnsafeStart = false;
        }

        public CompactRainPathCapabilities(bool allowJump, float jumpHeight,
            float jumpVelocity, float runSpeed, float maximumDrop)
        {
            AllowJump = allowJump;
            JumpHeight = jumpHeight;
            JumpVelocity = jumpVelocity;
            RunSpeed = runSpeed;
            MaximumDrop = maximumDrop;
            AllowUnsafeStart = false;
        }
    }

    internal sealed class CompactRainPathResult
    {
        public CompactRainProjection StartProjection;
        public CompactRainProjection GoalProjection;
        public CompactRainPoint[] Waypoints;
        public byte[] Actions;
        public int[] PortalPath;
        public int[] IncomingLinks;
        public float Cost;
        public int ExpandedNodes;

        internal int ActionCount
        {
            get
            {
                int count = 0;
                for (int i = 0; Actions != null && i < Actions.Length; i++)
                    if (Actions[i] != 0) count++;
                return count;
            }
        }
    }
}
