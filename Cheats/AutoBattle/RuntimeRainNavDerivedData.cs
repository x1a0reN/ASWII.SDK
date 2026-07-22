using ASWDEBUG.Logger;
using RAIN.Navigation.Graph;
using RAIN.Navigation.NavMesh;
using RAIN.Navigation.Pathfinding;
using System;
using System.Collections.Generic;
using UnityEngine;

namespace ASWDEBUG.Cheats.AutoBattle
{
    internal enum RuntimeRainDerivedStage
    {
        Idle,
        Loading,
        ScanGraph,
        Components,
        Surfaces,
        OffMeshLinks,
        Saving,
        Ready,
        Failed
    }

    internal sealed class RuntimeRainOffMeshLink
    {
        internal const byte Jump = 1;
        internal const byte Drop = 2;
        public int FromNodeIndex;
        public int ToNodeIndex;
        public Vector3 Start;
        public Vector3 End;
        public float RequiredJumpHeight;
        public float RequiredRunSpeed;
        public float Cost;
        public byte Kind;
    }

    internal sealed class RuntimeRainBoundarySample
    {
        public int NodeIndex;
        public Vector3 Position;
        public Vector3 Outward;
        public int Component;
        public float Width;
        internal int PolyIndex;
    }

    internal sealed class RuntimeRainSurfaceSample
    {
        internal const byte SafeSpawn = 1;
        internal const byte DeadSpace = 2;
        internal const byte TightHeadroom = 4;
        public int NodeIndex;
        public Vector3 Position;
        public int Component;
        public float Clearance;
        public byte CoverMask;
        public byte Flags;
    }

    internal struct RuntimeRainDerivedSnapshot
    {
        public RuntimeRainDerivedStage Stage;
        public string Detail;
        public string CacheStatus;
        public string CacheFileName;
        public float Progress01;
        public int Processed;
        public int Total;
        public int ComponentCount;
        public int BoundaryCount;
        public int SurfaceCount;
        public int SafeSpawnCount;
        public int JumpLinkCount;
        public int DropLinkCount;
        public long CacheBytes;
        public float ElapsedSeconds;
    }

    internal static class RuntimeRainNavDerivedData
    {
        private const string DerivationSignature =
            "v2|boundary=poly1|edgeNormal=1|component=cross-only|approach=dense|clearance=segment6|cover=8|body=0.48|head=1.55|jump=4.2|rise=2.2|drop=8|arc=12x9";
        private const float BoundaryBucketSize = 4.5f;
        private const float SurfaceBucketSize = 3.0f;
        private const float SurfaceSearchRadius = 6f;
        private const int MaxLinksPerBoundary = 2;

        private static RuntimeRainDerivedStage _stage;
        private static string _mapName = string.Empty;
        private static string _rainIdentity = string.Empty;
        private static string _graphSignature = string.Empty;
        private static string _detail = "idle";
        private static string _cacheStatus = "not_checked";
        private static string _cacheFileName = "-";
        private static RAINNavigationGraph _graph;
        private static bool _highDetail;
        private static int _cursor;
        private static int _total;
        private static int _componentCount;
        private static int _safeSpawnCount;
        private static int _jumpLinkCount;
        private static int _dropLinkCount;
        private static long _cacheBytes;
        private static float _nextLogAt;
        private static float _startedAt;
        private static float _nextSaveAttemptAt;
        private static int _saveAttempts;

        private static List<PolyWork> Polys = new List<PolyWork>();
        private static Dictionary<NavMeshPoly, int> PolyLookup = new Dictionary<NavMeshPoly, int>();
        private static List<RuntimeRainBoundarySample> Boundaries = new List<RuntimeRainBoundarySample>();
        private static List<RuntimeRainBoundarySample> LinkBoundaries = new List<RuntimeRainBoundarySample>();
        private static List<RuntimeRainSurfaceSample> Surfaces = new List<RuntimeRainSurfaceSample>();
        private static List<RuntimeRainOffMeshLink> Links = new List<RuntimeRainOffMeshLink>();
        private static Dictionary<long, List<int>> BoundaryBuckets = new Dictionary<long, List<int>>();
        private static Dictionary<long, List<int>> LinkBuckets = new Dictionary<long, List<int>>();
        private static Dictionary<long, List<int>> SurfaceBuckets = new Dictionary<long, List<int>>();
        private static HashSet<long> LinkSampleKeys = new HashSet<long>();
        private static Dictionary<long, RuntimeRainOffMeshLink> LinkLookup =
            new Dictionary<long, RuntimeRainOffMeshLink>();
        private static Dictionary<long, RuntimeRainOffMeshLink> ActiveLinkLookup =
            new Dictionary<long, RuntimeRainOffMeshLink>();
        private static Dictionary<NavigationGraphNode, int> LinkNodeLookup =
            new Dictionary<NavigationGraphNode, int>();
        private static List<InjectedLink> InjectedLinks = new List<InjectedLink>();
        private static int[] _parents = new int[0];
        private static byte[] _ranks = new byte[0];
        private static string _injectedCapabilityKey = string.Empty;

        internal static bool IsReady { get { return _stage == RuntimeRainDerivedStage.Ready; } }
        internal static bool HasFailed { get { return _stage == RuntimeRainDerivedStage.Failed; } }
        internal static bool IsPending
        {
            get
            {
                return _stage != RuntimeRainDerivedStage.Idle &&
                       _stage != RuntimeRainDerivedStage.Ready &&
                       _stage != RuntimeRainDerivedStage.Failed;
            }
        }

        internal static RuntimeRainDerivedSnapshot GetSnapshot()
        {
            int processed = Mathf.Clamp(_cursor, 0, Mathf.Max(0, _total));
            float progress = _total <= 0 ? (_stage == RuntimeRainDerivedStage.Ready ? 1f : 0f) :
                Mathf.Clamp01((float)processed / _total);
            return new RuntimeRainDerivedSnapshot
            {
                Stage = _stage,
                Detail = _detail,
                CacheStatus = _cacheStatus,
                CacheFileName = _cacheFileName,
                Progress01 = progress,
                Processed = processed,
                Total = _total,
                ComponentCount = _componentCount,
                BoundaryCount = Boundaries.Count,
                SurfaceCount = Surfaces.Count,
                SafeSpawnCount = _safeSpawnCount,
                JumpLinkCount = _jumpLinkCount,
                DropLinkCount = _dropLinkCount,
                CacheBytes = _cacheBytes,
                ElapsedSeconds = _startedAt <= 0f ? 0f :
                    Mathf.Max(0f, Time.realtimeSinceStartup - _startedAt)
            };
        }

        internal static void Prepare(string mapName, RAINNavigationGraph graph, bool highDetail,
            string rainIdentity, string graphSignature)
        {
            if (graph == null || graph.Size <= 0) return;
            if (_graph == graph && string.Equals(_mapName, mapName, StringComparison.OrdinalIgnoreCase) &&
                (_stage == RuntimeRainDerivedStage.Ready || _stage == RuntimeRainDerivedStage.Failed || IsPending)) return;

            Reset(false);
            _stage = RuntimeRainDerivedStage.Loading;
            _mapName = mapName ?? string.Empty;
            _graph = graph;
            _highDetail = highDetail;
            _rainIdentity = rainIdentity ?? string.Empty;
            _graphSignature = (graphSignature ?? string.Empty) + "|" + DerivationSignature;
            _startedAt = Time.realtimeSinceStartup;
            _cacheFileName = System.IO.Path.GetFileName(
                RuntimeRainNavDerivedDiskCache.GetCachePath(_mapName, _highDetail));
            if (string.IsNullOrEmpty(graphSignature) || graphSignature.IndexOf("|base=unknown",
                StringComparison.Ordinal) >= 0)
            {
                Fail("base_payload_identity_missing");
                return;
            }

            RuntimeRainDerivedCacheRecord record;
            string status;
            if (RuntimeRainNavDerivedDiskCache.TryLoad(_mapName, _rainIdentity, _graphSignature,
                graph.Size, out record, out status))
            {
                int graphPolyCount = CountGraphPolys(graph);
                if (record.ComponentCount <= 0 || record.Surfaces.Count <= 0 ||
                    record.Surfaces.Count != graphPolyCount || record.SafeSpawnCount <= 0)
                {
                    status = "derived_invariant_mismatch polys=" + record.Surfaces.Count + "/" +
                        graphPolyCount + " components=" + record.ComponentCount +
                        " safe=" + record.SafeSpawnCount;
                }
                else
                {
                ApplyRecord(record);
                _cacheStatus = "disk_hit";
                _cacheBytes = record.FileBytes;
                _stage = RuntimeRainDerivedStage.Ready;
                _cursor = _total = 1;
                _detail = "ready source=disk links=" + Links.Count + " surfaces=" + Surfaces.Count;
                FileLogger.Log("AUTO-BATTLE][NAVMETA", "disk_hit map=" + Safe(_mapName) +
                    " links=" + Links.Count + " boundaries=" + Boundaries.Count +
                    " surfaces=" + Surfaces.Count + " components=" + _componentCount +
                    " bytes=" + _cacheBytes);
                return;
                }
            }

            _cacheStatus = status;
            _stage = RuntimeRainDerivedStage.ScanGraph;
            _cursor = 0;
            _total = graph.Size;
            _detail = "scan_graph reason=" + status;
            FileLogger.Log("AUTO-BATTLE][NAVMETA", "build_started map=" + Safe(_mapName) +
                " graph=" + graph.Size + " reason=" + Safe(status));
        }

        internal static void Tick()
        {
            if (!IsPending || _graph == null) return;
            try
            {
            if (_stage == RuntimeRainDerivedStage.ScanGraph) TickScanGraph();
                else if (_stage == RuntimeRainDerivedStage.Components) TickComponents();
                else if (_stage == RuntimeRainDerivedStage.Surfaces) TickSurfaces();
                else if (_stage == RuntimeRainDerivedStage.OffMeshLinks) TickOffMeshLinks();
                else if (_stage == RuntimeRainDerivedStage.Saving && Time.realtimeSinceStartup >= _nextSaveAttemptAt) Save();
                LogProgress();
            }
            catch (Exception ex)
            {
                Fail("tick_ex=" + ex.GetType().Name + ":" + Safe(ex.Message));
            }
        }

        internal static void Deactivate(RAINNavigationGraph graph)
        {
            bool mismatched = _graph != null && graph != null && _graph != graph;
            // RuntimeRainNavMesh has a single active graph. Always drop every node-indexed
            // container here so a stale identity mismatch cannot keep the previous graph alive.
            Reset(false);
            ReleaseContainerCapacity();
            if (mismatched)
                FileLogger.Log("AUTO-BATTLE][NAVMETA", "deactivate_graph_identity_mismatch cleared=1");
        }

        internal static bool PrepareLinksForRoute(RAINNavigationGraph graph, AutoBattleRouteCapabilities capabilities)
        {
            if (!IsReady || graph == null || graph != _graph || capabilities == null) return false;
            string key = capabilities.AllowJump ?
                "1:" + capabilities.JumpHeight.ToString("0.00") + ":" +
                capabilities.JumpVelocity.ToString("0.00") + ":" + capabilities.RunSpeed.ToString("0.00") : "0";
            if (key == _injectedCapabilityKey) return false;
            RemoveInjectedLinks();
            _injectedCapabilityKey = key;
            if (!capabilities.AllowJump) return true;

            for (int i = 0; i < Links.Count; i++)
            {
                RuntimeRainOffMeshLink link = Links[i];
                if (!CanUseLink(link, capabilities)) continue;
                NavigationGraphNode from;
                NavigationGraphNode to;
                try
                {
                    from = graph.GetNode(link.FromNodeIndex);
                    to = graph.GetNode(link.ToNodeIndex);
                }
                catch { continue; }
                if (from == null || to == null || from.EdgeTo(to) != null) continue;
                NavigationGraphEdge edge = new NavigationGraphEdge(from, to, Mathf.Max(graph.MinEdgeCost, link.Cost));
                from.AddEdgeOut(edge);
                to.AddEdgeIn(edge);
                InjectedLinks.Add(new InjectedLink(edge, link));
                ActiveLinkLookup[PairKey(link.FromNodeIndex, link.ToNodeIndex)] = link;
            }
            FileLogger.Log("AUTO-BATTLE][NAVMETA", "links_applied count=" + InjectedLinks.Count +
                " jump=" + capabilities.JumpHeight.ToString("0.00") +
                " speed=" + capabilities.RunSpeed.ToString("0.00"));
            return true;
        }

        internal static bool TryBuildLinkedWorldPath(RAINPath path, Vector3 from, Vector3 to,
            out List<Vector3> result, out List<bool> jumpFlags)
        {
            result = new List<Vector3>();
            jumpFlags = new List<bool>();
            if (path == null || path.PathNodes == null || path.PathPoints == null ||
                path.PathNodes.Count < 2 || path.PathNodes.Count != path.PathPoints.Count ||
                ActiveLinkLookup.Count == 0) return false;
            Matrix4x4 transform = path.Graph.MountTransform;
            AddLinkedPoint(result, jumpFlags, from, false);
            bool used = false;
            for (int i = 1; i < path.PathNodes.Count; i++)
            {
                int fromNode;
                int toNode;
                RuntimeRainOffMeshLink link;
                if (LinkNodeLookup.TryGetValue(path.PathNodes[i - 1], out fromNode) &&
                    LinkNodeLookup.TryGetValue(path.PathNodes[i], out toNode) &&
                    ActiveLinkLookup.TryGetValue(PairKey(fromNode, toNode), out link))
                {
                    AddLinkedPoint(result, jumpFlags, link.Start, false);
                    AddLinkedPoint(result, jumpFlags, link.End, true);
                    used = true;
                    continue;
                }
                if (i < path.PathNodes.Count - 1)
                {
                    Vector3 world = transform.MultiplyPoint(path.PathPoints[i]);
                    if (!IsFinitePoint(world))
                    {
                        NavMeshPoly poly = path.PathNodes[i] as NavMeshPoly;
                        if (poly != null) world = poly.Position;
                    }
                    AddLinkedPoint(result, jumpFlags, world, false);
                }
            }
            AddLinkedPoint(result, jumpFlags, to, false);
            return used;
        }

        internal static bool TryFindSameLayerAnchor(RAINNavigationGraph graph, Vector3 from,
            Vector3 toward, Transform ignoreRoot, NavigationGraphNode excludedNode,
            out Vector3 anchor, out NavigationGraphNode node, out string detail)
        {
            anchor = from;
            node = null;
            detail = "metaAnchor=unavailable";
            if (!IsReady || graph == null || graph != _graph || SurfaceBuckets.Count == 0) return false;

            Vector3 forward = toward - from;
            forward.y = 0f;
            if (forward.sqrMagnitude < 0.01f) forward = Vector3.forward;
            forward.Normalize();
            int bx = Mathf.FloorToInt(from.x / SurfaceBucketSize);
            int bz = Mathf.FloorToInt(from.z / SurfaceBucketSize);
            int checkedCount = 0;
            int sameLayerCount = 0;
            int physicalCount = 0;
            float bestScore = float.MaxValue;
            RuntimeRainSurfaceSample best = null;
            NavigationGraphNode bestNode = null;
            for (int dz = -2; dz <= 2; dz++)
            {
                for (int dx = -2; dx <= 2; dx++)
                {
                    List<int> candidates;
                    if (!SurfaceBuckets.TryGetValue(SpatialKey(bx + dx, bz + dz), out candidates)) continue;
                    for (int i = 0; i < candidates.Count; i++)
                    {
                        RuntimeRainSurfaceSample surface = Surfaces[candidates[i]];
                        checkedCount++;
                        float distance = XzDistance(from, surface.Position);
                        float dyValue = Mathf.Abs(surface.Position.y - from.y);
                        if (distance < 0.35f || distance > 6.25f || dyValue > 1.65f) continue;
                        if ((surface.Flags & (RuntimeRainSurfaceSample.DeadSpace |
                            RuntimeRainSurfaceSample.TightHeadroom)) != 0 || surface.Clearance < 0.58f) continue;
                        sameLayerCount++;
                        NavigationGraphNode candidateNode;
                        try { candidateNode = graph.GetNode(surface.NodeIndex); }
                        catch { continue; }
                        if (candidateNode == null || candidateNode == excludedNode) continue;
                        if (!AutoBattleRoutePlanner.HasSupportedStandingPoint(surface.Position, ignoreRoot) ||
                            !AutoBattleRoutePlanner.CanFollowRouteSegment(from, surface.Position, ignoreRoot)) continue;
                        physicalCount++;
                        Vector3 direction = surface.Position - from;
                        direction.y = 0f;
                        direction.Normalize();
                        float awayPenalty = Mathf.Max(0f, -Vector3.Dot(direction, forward)) * 0.45f;
                        float score = distance + dyValue * 1.6f + awayPenalty -
                            Mathf.Min(1.5f, surface.Clearance) * 0.22f;
                        if (score >= bestScore) continue;
                        bestScore = score;
                        best = surface;
                        bestNode = candidateNode;
                    }
                }
            }

            if (best == null || bestNode == null)
            {
                detail = "metaAnchor=failed checked=" + checkedCount +
                    " sameLayer=" + sameLayerCount + " physical=" + physicalCount;
                return false;
            }
            anchor = best.Position;
            node = bestNode;
            detail = "metaAnchor=ok node=" + best.NodeIndex +
                " dist=" + XzDistance(from, best.Position).ToString("0.00") +
                " dy=" + Mathf.Abs(best.Position.y - from.y).ToString("0.00") +
                " clearance=" + best.Clearance.ToString("0.00") +
                " checked=" + checkedCount + " physical=" + physicalCount;
            return true;
        }

        internal static int CollectNearbyBoundaries(Vector3 from, float maxDistance, int maxCount,
            List<RuntimeRainBoundarySample> output)
        {
            if (output == null) return 0;
            output.Clear();
            if (!IsReady || BoundaryBuckets.Count == 0 || maxDistance <= 0f || maxCount <= 0)
                return 0;

            int bx = Mathf.FloorToInt(from.x / BoundaryBucketSize);
            int bz = Mathf.FloorToInt(from.z / BoundaryBucketSize);
            int radius = Mathf.Clamp(Mathf.CeilToInt(maxDistance / BoundaryBucketSize), 1, 18);
            for (int dz = -radius; dz <= radius; dz++)
            {
                for (int dx = -radius; dx <= radius; dx++)
                {
                    List<int> candidates;
                    if (!BoundaryBuckets.TryGetValue(SpatialKey(bx + dx, bz + dz), out candidates))
                        continue;
                    for (int i = 0; i < candidates.Count; i++)
                    {
                        RuntimeRainBoundarySample sample = Boundaries[candidates[i]];
                        float distance = XzDistance(from, sample.Position);
                        if (distance > maxDistance || sample.Width < 0.35f) continue;
                        float score = distance + Mathf.Abs(sample.Position.y - from.y) * 1.8f;
                        int insertAt = 0;
                        while (insertAt < output.Count &&
                               CliffBoundaryScore(from, output[insertAt]) <= score)
                            insertAt++;
                        if (insertAt >= maxCount) continue;
                        output.Insert(insertAt, sample);
                        if (output.Count > maxCount) output.RemoveAt(maxCount);
                    }
                }
            }
            return output.Count;
        }

        private static float CliffBoundaryScore(Vector3 from, RuntimeRainBoundarySample sample)
        {
            return XzDistance(from, sample.Position) +
                   Mathf.Abs(sample.Position.y - from.y) * 1.8f;
        }

        private static void AppendSmoothedWalkPartition(RAINPath sourcePath, int startIndex, int endIndex,
            Vector3 startWorld, Vector3 endWorld, List<Vector3> result, List<bool> jumpFlags)
        {
            AddLinkedPoint(result, jumpFlags, startWorld, false);
            if (sourcePath == null || sourcePath.Graph == null || startIndex < 0 ||
                endIndex < startIndex || endIndex >= sourcePath.PathNodes.Count)
            {
                AddLinkedPoint(result, jumpFlags, endWorld, false);
                return;
            }

            int count = endIndex - startIndex + 1;
            if (count < 2)
            {
                AddLinkedPoint(result, jumpFlags, endWorld, false);
                return;
            }

            List<NavigationGraphNode> nodes = new List<NavigationGraphNode>(count);
            List<Vector3> points = new List<Vector3>(count);
            for (int i = startIndex; i <= endIndex; i++)
            {
                nodes.Add(sourcePath.PathNodes[i]);
                points.Add(sourcePath.PathPoints[i]);
            }

            Matrix4x4 inverse = sourcePath.Graph.MountInverseTransform;
            points[0] = inverse.MultiplyPoint(startWorld);
            points[points.Count - 1] = inverse.MultiplyPoint(endWorld);
            try
            {
                RAIN.Navigation.Pathfinding.NavMeshPath partition =
                    new RAIN.Navigation.Pathfinding.NavMeshPath(sourcePath.Graph, nodes, points);
                if (partition.IsValid)
                {
                    for (int i = 1; i < partition.WaypointCount; i++)
                        AddLinkedPoint(result, jumpFlags, partition.GetWaypointPosition(i), false);
                }
            }
            catch
            {
                // The exact endpoints are still validated by the route planner fail-closed.
            }
            AddLinkedPoint(result, jumpFlags, endWorld, false);
        }

        private static void AddLinkedPoint(List<Vector3> points, List<bool> flags, Vector3 point, bool jump)
        {
            if (!IsFinitePoint(point)) return;
            if (points.Count > 0 && XzDistance(points[points.Count - 1], point) <= 0.08f &&
                Mathf.Abs(points[points.Count - 1].y - point.y) <= 0.10f)
            {
                if (jump) flags[flags.Count - 1] = true;
                return;
            }
            points.Add(point);
            flags.Add(jump);
        }

        private static bool IsFinitePoint(Vector3 point)
        {
            return !float.IsNaN(point.x) && !float.IsInfinity(point.x) &&
                   !float.IsNaN(point.y) && !float.IsInfinity(point.y) &&
                   !float.IsNaN(point.z) && !float.IsInfinity(point.z);
        }

        private static void TickScanGraph()
        {
            int budget = _highDetail ? 12000 : 2400;
            int end = Mathf.Min(_graph.Size, _cursor + budget);
            for (; _cursor < end; _cursor++)
            {
                NavigationGraphNode node = _graph.GetNode(_cursor);
                NavMeshPoly poly = node as NavMeshPoly;
                if (poly != null)
                {
                    int index = Polys.Count;
                    Polys.Add(new PolyWork(_cursor, poly));
                    PolyLookup[poly] = index;
                    continue;
                }

                NavMeshEdge edge = node as NavMeshEdge;
                if (edge == null || edge.PolyCount != 1) continue;
                NavMeshPoly owner = edge.GetPolyNode(0);
                int polyIndex;
                if (!PolyLookup.TryGetValue(owner, out polyIndex)) polyIndex = -1;
                Vector3 position = edge.Position;
                Vector3 tangent = edge.PointTwo - edge.PointOne;
                tangent.y = 0f;
                if (tangent.sqrMagnitude < 0.001f) continue;
                tangent.Normalize();
                Vector3 outward = Vector3.Cross(Vector3.up, tangent).normalized;
                Vector3 radial = position - owner.Position;
                radial.y = 0f;
                if (radial.sqrMagnitude < 0.001f) continue;
                if (Vector3.Dot(outward, radial) < 0f) outward = -outward;
                RuntimeRainBoundarySample sample = new RuntimeRainBoundarySample
                {
                    NodeIndex = _cursor,
                    Position = position,
                    Outward = outward,
                    Component = -1,
                    Width = Mathf.Max(0.05f, XzDistance(edge.PointOne, edge.PointTwo)),
                    PolyIndex = polyIndex
                };
                int boundaryIndex = Boundaries.Count;
                Boundaries.Add(sample);
                AddBucket(BoundaryBuckets, SpatialKey(position, BoundaryBucketSize), boundaryIndex);
                long linkSampleKey = LinkSampleKey(sample);
                if (LinkSampleKeys.Add(linkSampleKey))
                {
                    int linkIndex = LinkBoundaries.Count;
                    LinkBoundaries.Add(sample);
                    AddBucket(LinkBuckets, SpatialKey(position, BoundaryBucketSize), linkIndex);
                }
            }
            _detail = "scan nodes=" + _cursor + "/" + _total + " polys=" + Polys.Count +
                " boundaries=" + Boundaries.Count;
            if (_cursor < _graph.Size) return;

            _parents = new int[Polys.Count];
            _ranks = new byte[Polys.Count];
            for (int i = 0; i < _parents.Length; i++) _parents[i] = i;
            _stage = RuntimeRainDerivedStage.Components;
            _cursor = 0;
            _total = _graph.Size;
        }

        private static void TickComponents()
        {
            int budget = _highDetail ? 16000 : 3200;
            int end = Mathf.Min(_graph.Size, _cursor + budget);
            for (; _cursor < end; _cursor++)
            {
                NavMeshEdge edge = _graph.GetNode(_cursor) as NavMeshEdge;
                if (edge == null || edge.PolyCount < 2) continue;
                int first;
                if (!PolyLookup.TryGetValue(edge.GetPolyNode(0), out first)) continue;
                for (int p = 1; p < edge.PolyCount; p++)
                {
                    int other;
                    if (PolyLookup.TryGetValue(edge.GetPolyNode(p), out other)) Union(first, other);
                }
            }
            _detail = "components edges=" + _cursor + "/" + _total;
            if (_cursor < _graph.Size) return;

            Dictionary<int, int> components = new Dictionary<int, int>();
            for (int i = 0; i < Polys.Count; i++)
            {
                int root = Find(i);
                int component;
                if (!components.TryGetValue(root, out component))
                {
                    component = components.Count;
                    components[root] = component;
                }
                Polys[i].Component = component;
            }
            _componentCount = components.Count;
            for (int i = 0; i < Boundaries.Count; i++)
            {
                RuntimeRainBoundarySample sample = Boundaries[i];
                if (sample.PolyIndex >= 0 && sample.PolyIndex < Polys.Count)
                    sample.Component = Polys[sample.PolyIndex].Component;
                Boundaries[i] = sample;
            }
            for (int i = 0; i < LinkBoundaries.Count; i++)
            {
                RuntimeRainBoundarySample sample = LinkBoundaries[i];
                if (sample.PolyIndex >= 0 && sample.PolyIndex < Polys.Count)
                    sample.Component = Polys[sample.PolyIndex].Component;
                LinkBoundaries[i] = sample;
            }
            _stage = RuntimeRainDerivedStage.Surfaces;
            _cursor = 0;
            _total = Polys.Count;
        }

        private static void TickSurfaces()
        {
            int budget = _highDetail ? 900 : 120;
            int end = Mathf.Min(Polys.Count, _cursor + budget);
            for (; _cursor < end; _cursor++)
            {
                PolyWork poly = Polys[_cursor];
                Vector3 position = poly.Poly.Position;
                float clearance;
                byte coverMask;
                MeasureBoundaryData(position, out clearance, out coverMask);
                byte flags = 0;
                bool dead = false;
                bool tight = false;
                try { dead = DeadSpace.inDeadSpace(position + Vector3.up * 0.8f); }
                catch { }
                try
                {
                    tight = Physics.CheckCapsule(position + Vector3.up * 0.35f,
                        position + Vector3.up * 1.55f, 0.28f, TerrainMask);
                }
                catch { tight = true; }
                if (dead) flags |= RuntimeRainSurfaceSample.DeadSpace;
                if (tight) flags |= RuntimeRainSurfaceSample.TightHeadroom;
                if (!dead && !tight && clearance >= 0.65f)
                {
                    flags |= RuntimeRainSurfaceSample.SafeSpawn;
                    _safeSpawnCount++;
                }
                RuntimeRainSurfaceSample sample = new RuntimeRainSurfaceSample
                {
                    NodeIndex = poly.NodeIndex,
                    Position = position,
                    Component = poly.Component,
                    Clearance = clearance,
                    CoverMask = coverMask,
                    Flags = flags
                };
                int surfaceIndex = Surfaces.Count;
                Surfaces.Add(sample);
                AddBucket(SurfaceBuckets, SpatialKey(position, SurfaceBucketSize), surfaceIndex);
            }
            _detail = "surfaces=" + _cursor + "/" + _total + " safe=" + _safeSpawnCount;
            if (_cursor < Polys.Count) return;
            _stage = RuntimeRainDerivedStage.OffMeshLinks;
            _cursor = 0;
            _total = LinkBoundaries.Count;
        }

        private static void TickOffMeshLinks()
        {
            int budget = _highDetail ? 48 : 8;
            int end = Mathf.Min(LinkBoundaries.Count, _cursor + budget);
            AutoBattleRouteCapabilities maximum = new AutoBattleRouteCapabilities
            {
                AllowJump = true,
                JumpHeight = 2.40f,
                JumpVelocity = 8.0f,
                RunSpeed = 8.5f,
                RequireRainPath = true
            };
            for (; _cursor < end; _cursor++) BuildLinksFrom(_cursor, maximum);
            _detail = "offmesh=" + _cursor + "/" + _total + " jump=" + _jumpLinkCount +
                " drop=" + _dropLinkCount;
            if (_cursor < LinkBoundaries.Count) return;
            BuildLinkLookups();
            _stage = RuntimeRainDerivedStage.Saving;
            _cursor = 0;
            _total = 1;
        }

        private static void BuildLinksFrom(int sourceIndex, AutoBattleRouteCapabilities maximum)
        {
            RuntimeRainBoundarySample source = LinkBoundaries[sourceIndex];
            int sx = Mathf.FloorToInt(source.Position.x / BoundaryBucketSize);
            int sz = Mathf.FloorToInt(source.Position.z / BoundaryBucketSize);
            int accepted = 0;
            for (int dz = -1; dz <= 1 && accepted < MaxLinksPerBoundary; dz++)
            {
                for (int dx = -1; dx <= 1 && accepted < MaxLinksPerBoundary; dx++)
                {
                    List<int> candidates;
                    if (!LinkBuckets.TryGetValue(SpatialKey(sx + dx, sz + dz), out candidates)) continue;
                    for (int c = 0; c < candidates.Count && accepted < MaxLinksPerBoundary; c++)
                    {
                        int targetIndex = candidates[c];
                        if (targetIndex == sourceIndex) continue;
                        RuntimeRainBoundarySample target = LinkBoundaries[targetIndex];
                        if (source.Component < 0 || target.Component < 0 ||
                            source.Component == target.Component) continue;
                        Vector3 delta = target.Position - source.Position;
                        float horizontal = new Vector2(delta.x, delta.z).magnitude;
                        float rise = delta.y;
                        if (horizontal < 0.75f || horizontal > 4.2f || rise > 2.2f || rise < -8f) continue;
                        Vector3 flat = new Vector3(delta.x, 0f, delta.z) / horizontal;
                        if (Vector3.Dot(source.Outward, flat) < 0.35f ||
                            Vector3.Dot(target.Outward, -flat) < 0.15f) continue;
                        NavigationGraphNode sourceNode = _graph.GetNode(source.NodeIndex);
                        NavigationGraphNode targetNode = _graph.GetNode(target.NodeIndex);
                        if (sourceNode == null || targetNode == null || sourceNode.EdgeTo(targetNode) != null) continue;

                        Vector3 start = source.Position - source.Outward * 0.52f;
                        Vector3 finish = target.Position - target.Outward * 0.52f;
                        Vector3 sourceCenter = source.PolyIndex >= 0 && source.PolyIndex < Polys.Count
                            ? Polys[source.PolyIndex].Poly.Position : start;
                        Vector3 targetCenter = target.PolyIndex >= 0 && target.PolyIndex < Polys.Count
                            ? Polys[target.PolyIndex].Poly.Position : finish;
                        if (!AutoBattleRoutePlanner.CanFollowRouteSegment(sourceCenter, start, null) ||
                            !AutoBattleRoutePlanner.CanFollowRouteSegment(finish, targetCenter, null) ||
                            !AutoBattleRoutePlanner.HasSupportedStandingPoint(start, null) ||
                            !AutoBattleRoutePlanner.HasSupportedStandingPoint(finish, null)) continue;
                        if (!AutoBattleRoutePlanner.CanExecuteJump(start, finish, maximum, null)) continue;
                        float requiredHeight = Mathf.Max(1.2f, Mathf.Max(0f, rise) / 0.92f + 0.08f);
                        float requiredSpeed = Mathf.Clamp(horizontal /
                            ((2f * maximum.JumpVelocity / 19.6f) * 0.65f), 3.0f, 8.5f);
                        byte kind = rise < -0.85f ? RuntimeRainOffMeshLink.Drop : RuntimeRainOffMeshLink.Jump;
                        RuntimeRainOffMeshLink link = new RuntimeRainOffMeshLink
                        {
                            FromNodeIndex = source.NodeIndex,
                            ToNodeIndex = target.NodeIndex,
                            Start = start,
                            End = finish,
                            RequiredJumpHeight = requiredHeight,
                            RequiredRunSpeed = requiredSpeed,
                            Cost = horizontal + Mathf.Abs(rise) * 0.55f + (kind == RuntimeRainOffMeshLink.Drop ? 2.5f : 3.5f),
                            Kind = kind
                        };
                        long key = PairKey(link.FromNodeIndex, link.ToNodeIndex);
                        if (LinkLookup.ContainsKey(key)) continue;
                        LinkLookup[key] = link;
                        Links.Add(link);
                        if (kind == RuntimeRainOffMeshLink.Drop) _dropLinkCount++;
                        else _jumpLinkCount++;
                        accepted++;
                    }
                }
            }
        }

        private static void MeasureBoundaryData(Vector3 point, out float clearance, out byte coverMask)
        {
            clearance = SurfaceSearchRadius;
            coverMask = 0;
            int bx = Mathf.FloorToInt(point.x / BoundaryBucketSize);
            int bz = Mathf.FloorToInt(point.z / BoundaryBucketSize);
            for (int dz = -2; dz <= 2; dz++)
            {
                for (int dx = -2; dx <= 2; dx++)
                {
                    List<int> candidates;
                    if (!BoundaryBuckets.TryGetValue(SpatialKey(bx + dx, bz + dz), out candidates)) continue;
                    for (int i = 0; i < candidates.Count; i++)
                    {
                        RuntimeRainBoundarySample boundary = Boundaries[candidates[i]];
                        if (Mathf.Abs(boundary.Position.y - point.y) > 2.0f) continue;
                        Vector3 side = Vector3.Cross(Vector3.up, boundary.Outward).normalized;
                        Vector3 a = boundary.Position - side * boundary.Width * 0.5f;
                        Vector3 b = boundary.Position + side * boundary.Width * 0.5f;
                        float distance = DistancePointSegmentXZ(point, a, b);
                        if (distance < clearance) clearance = distance;
                        if (distance > 3.2f) continue;
                        Vector3 direction = boundary.Position - point;
                        float angle = Mathf.Atan2(direction.z, direction.x) * Mathf.Rad2Deg;
                        if (angle < 0f) angle += 360f;
                        int sector = Mathf.FloorToInt((angle + 22.5f) / 45f) & 7;
                        coverMask |= (byte)(1 << sector);
                    }
                }
            }
        }

        private static float DistancePointSegmentXZ(Vector3 point, Vector3 a, Vector3 b)
        {
            Vector2 p = new Vector2(point.x, point.z);
            Vector2 start = new Vector2(a.x, a.z);
            Vector2 segment = new Vector2(b.x - a.x, b.z - a.z);
            float lengthSq = segment.sqrMagnitude;
            if (lengthSq < 0.0001f) return Vector2.Distance(p, start);
            float t = Mathf.Clamp01(Vector2.Dot(p - start, segment) / lengthSq);
            return Vector2.Distance(p, start + segment * t);
        }

        private static void Save()
        {
            if (_graph == null || _graph.Size <= 0 || Polys.Count <= 0 || _componentCount <= 0 ||
                Surfaces.Count != Polys.Count || _safeSpawnCount <= 0)
            {
                Fail("derived_invariant_failed graph=" + (_graph == null ? 0 : _graph.Size) +
                    " polys=" + Polys.Count + " surfaces=" + Surfaces.Count +
                    " components=" + _componentCount + " safe=" + _safeSpawnCount);
                return;
            }
            long bytes;
            string path;
            string status;
            bool saved = RuntimeRainNavDerivedDiskCache.TrySave(_mapName, _rainIdentity, _graphSignature,
                _graph.Size, Links, Boundaries, Surfaces, _componentCount, _safeSpawnCount,
                out bytes, out path, out status);
            _cacheStatus = status;
            _cacheFileName = System.IO.Path.GetFileName(path);
            if (!saved)
            {
                _saveAttempts++;
                if (_saveAttempts < 3)
                {
                    _cacheStatus = "retry_" + _saveAttempts + ":" + status;
                    _detail = "save_retry=" + _saveAttempts + "/3 " + status;
                    _nextSaveAttemptAt = Time.realtimeSinceStartup + (1 << _saveAttempts);
                    return;
                }
                _stage = RuntimeRainDerivedStage.Ready;
                _cursor = _total = 1;
                _detail = "ready_memory_only save_failed_after_retries:" + status;
                _cacheStatus = "save_failed_after_retries:" + status;
                return;
            }
            _cacheBytes = bytes;
            _cursor = _total = 1;
            _stage = RuntimeRainDerivedStage.Ready;
            _detail = "ready source=generated links=" + Links.Count + " surfaces=" + Surfaces.Count;
            FileLogger.Log("AUTO-BATTLE][NAVMETA", "disk_saved map=" + Safe(_mapName) +
                " links=" + Links.Count + " jump=" + _jumpLinkCount + " drop=" + _dropLinkCount +
                " boundaries=" + Boundaries.Count + " surfaces=" + Surfaces.Count +
                " components=" + _componentCount + " safe=" + _safeSpawnCount + " bytes=" + bytes);
        }

        private static void ApplyRecord(RuntimeRainDerivedCacheRecord record)
        {
            Links.AddRange(record.Links);
            Boundaries.AddRange(record.Boundaries);
            Surfaces.AddRange(record.Surfaces);
            BuildBoundaryBuckets();
            BuildSurfaceBuckets();
            _componentCount = record.ComponentCount;
            _safeSpawnCount = record.SafeSpawnCount;
            for (int i = 0; i < Links.Count; i++)
            {
                if (Links[i].Kind == RuntimeRainOffMeshLink.Drop) _dropLinkCount++;
                else _jumpLinkCount++;
            }
            BuildLinkLookups();
        }

        private static void BuildLinkLookups()
        {
            LinkLookup.Clear();
            LinkNodeLookup.Clear();
            for (int i = 0; i < Links.Count; i++)
            {
                RuntimeRainOffMeshLink link = Links[i];
                LinkLookup[PairKey(link.FromNodeIndex, link.ToNodeIndex)] = link;
                try
                {
                    LinkNodeLookup[_graph.GetNode(link.FromNodeIndex)] = link.FromNodeIndex;
                    LinkNodeLookup[_graph.GetNode(link.ToNodeIndex)] = link.ToNodeIndex;
                }
                catch { }
            }
        }

        private static void BuildSurfaceBuckets()
        {
            SurfaceBuckets.Clear();
            for (int i = 0; i < Surfaces.Count; i++)
                AddBucket(SurfaceBuckets, SpatialKey(Surfaces[i].Position, SurfaceBucketSize), i);
        }

        private static void BuildBoundaryBuckets()
        {
            BoundaryBuckets.Clear();
            for (int i = 0; i < Boundaries.Count; i++)
                AddBucket(BoundaryBuckets, SpatialKey(Boundaries[i].Position, BoundaryBucketSize), i);
        }

        private static void RemoveInjectedLinks()
        {
            for (int i = 0; i < InjectedLinks.Count; i++)
            {
                InjectedLink injected = InjectedLinks[i];
                try
                {
                    injected.Edge.FromNode.RemoveEdgeOut(injected.Edge);
                    injected.Edge.ToNode.RemoveEdgeIn(injected.Edge);
                }
                catch { }
            }
            InjectedLinks.Clear();
            ActiveLinkLookup.Clear();
            _injectedCapabilityKey = string.Empty;
        }

        private static bool CanUseLink(RuntimeRainOffMeshLink link, AutoBattleRouteCapabilities capabilities)
        {
            float horizontal = XzDistance(link.Start, link.End);
            float rise = link.End.y - link.Start.y;
            float jumpHeight = Mathf.Max(1.2f, capabilities.JumpHeight);
            float jumpVelocity = capabilities.JumpVelocity > 0.1f
                ? capabilities.JumpVelocity
                : Mathf.Sqrt(jumpHeight * 39.2f);
            float runSpeed = capabilities.RunSpeed > 0.1f ? capabilities.RunSpeed : 6f;
            float maximumHorizontal = Mathf.Clamp(
                runSpeed * (2f * jumpVelocity / 19.6f) * 0.65f, 2.2f, 4.2f);
            return horizontal >= 0.35f && horizontal <= maximumHorizontal + 0.05f &&
                   rise <= jumpHeight * 0.92f && rise >= -Mathf.Max(8f, jumpHeight + 2f);
        }

        private static void Reset(bool keepGraph)
        {
            RemoveInjectedLinks();
            Polys.Clear();
            PolyLookup.Clear();
            Boundaries.Clear();
            LinkBoundaries.Clear();
            Surfaces.Clear();
            Links.Clear();
            BoundaryBuckets.Clear();
            LinkBuckets.Clear();
            SurfaceBuckets.Clear();
            LinkSampleKeys.Clear();
            LinkLookup.Clear();
            ActiveLinkLookup.Clear();
            LinkNodeLookup.Clear();
            _parents = new int[0];
            _ranks = new byte[0];
            _stage = RuntimeRainDerivedStage.Idle;
            _cursor = _total = 0;
            _componentCount = _safeSpawnCount = _jumpLinkCount = _dropLinkCount = 0;
            _cacheBytes = 0L;
            _cacheStatus = "not_checked";
            _cacheFileName = "-";
            _detail = "idle";
            _startedAt = 0f;
            _nextSaveAttemptAt = 0f;
            _saveAttempts = 0;
            if (!keepGraph)
            {
                _graph = null;
                _mapName = string.Empty;
                _rainIdentity = string.Empty;
                _graphSignature = string.Empty;
            }
        }

        private static void ReleaseContainerCapacity()
        {
            Polys = new List<PolyWork>();
            PolyLookup = new Dictionary<NavMeshPoly, int>();
            Boundaries = new List<RuntimeRainBoundarySample>();
            LinkBoundaries = new List<RuntimeRainBoundarySample>();
            Surfaces = new List<RuntimeRainSurfaceSample>();
            Links = new List<RuntimeRainOffMeshLink>();
            BoundaryBuckets = new Dictionary<long, List<int>>();
            LinkBuckets = new Dictionary<long, List<int>>();
            SurfaceBuckets = new Dictionary<long, List<int>>();
            LinkSampleKeys = new HashSet<long>();
            LinkLookup = new Dictionary<long, RuntimeRainOffMeshLink>();
            ActiveLinkLookup = new Dictionary<long, RuntimeRainOffMeshLink>();
            LinkNodeLookup = new Dictionary<NavigationGraphNode, int>();
            InjectedLinks = new List<InjectedLink>();
        }

        private static int Find(int value)
        {
            int root = value;
            while (_parents[root] != root) root = _parents[root];
            while (_parents[value] != value)
            {
                int next = _parents[value];
                _parents[value] = root;
                value = next;
            }
            return root;
        }

        private static void Union(int left, int right)
        {
            int a = Find(left);
            int b = Find(right);
            if (a == b) return;
            if (_ranks[a] < _ranks[b]) _parents[a] = b;
            else if (_ranks[a] > _ranks[b]) _parents[b] = a;
            else
            {
                _parents[b] = a;
                _ranks[a]++;
            }
        }

        private static void AddBucket(Dictionary<long, List<int>> buckets, long key, int value)
        {
            List<int> list;
            if (!buckets.TryGetValue(key, out list))
            {
                list = new List<int>();
                buckets[key] = list;
            }
            list.Add(value);
        }

        private static long SpatialKey(Vector3 point, float cell)
        {
            return SpatialKey(Mathf.FloorToInt(point.x / cell), Mathf.FloorToInt(point.z / cell));
        }

        private static long SpatialKey(int x, int z)
        {
            return ((long)x << 32) ^ (uint)z;
        }

        private static long LinkSampleKey(RuntimeRainBoundarySample sample)
        {
            int x = Mathf.FloorToInt(sample.Position.x / 0.75f) & 0xFFFF;
            int z = Mathf.FloorToInt(sample.Position.z / 0.75f) & 0xFFFF;
            int y = Mathf.FloorToInt(sample.Position.y / 0.50f) & 0xFFFF;
            float angle = Mathf.Atan2(sample.Outward.z, sample.Outward.x) * Mathf.Rad2Deg;
            if (angle < 0f) angle += 360f;
            int dir = Mathf.FloorToInt((angle + 22.5f) / 45f) & 7;
            return ((long)x << 48) | ((long)z << 32) | ((long)y << 16) | (uint)dir;
        }

        private static long PairKey(int from, int to)
        {
            return ((long)from << 32) | (uint)to;
        }

        private static float XzDistance(Vector3 a, Vector3 b)
        {
            float dx = a.x - b.x;
            float dz = a.z - b.z;
            return Mathf.Sqrt(dx * dx + dz * dz);
        }

        private static int CountGraphPolys(RAINNavigationGraph graph)
        {
            int count = 0;
            for (int i = 0; graph != null && i < graph.Size; i++)
                if (graph.GetNode(i) is NavMeshPoly) count++;
            return count;
        }

        private static void LogProgress()
        {
            float now = Time.realtimeSinceStartup;
            if (now < _nextLogAt) return;
            _nextLogAt = now + 2f;
            FileLogger.Log("AUTO-BATTLE][NAVMETA", "stage=" + _stage + " progress=" +
                (_total <= 0 ? "0" : ((float)_cursor / _total).ToString("0.000")) + " " + _detail);
        }

        private static void Fail(string detail)
        {
            _stage = RuntimeRainDerivedStage.Failed;
            _detail = detail;
            FileLogger.Log("AUTO-BATTLE][NAVMETA", "failed map=" + Safe(_mapName) + " " + Safe(detail));
        }

        private static string Safe(string value)
        {
            if (string.IsNullOrEmpty(value)) return "-";
            string safe = value.Replace('\r', ' ').Replace('\n', ' ').Trim();
            return safe.Length <= 120 ? safe : safe.Substring(0, 120);
        }

        private static int TerrainMask
        {
            get
            {
                int mask = LayerMask.GetMask(new string[] { "Terrarin" });
                return mask == 0 ? 256 : mask;
            }
        }

        private sealed class PolyWork
        {
            public readonly int NodeIndex;
            public readonly NavMeshPoly Poly;
            public int Component;

            public PolyWork(int nodeIndex, NavMeshPoly poly)
            {
                NodeIndex = nodeIndex;
                Poly = poly;
                Component = -1;
            }
        }

        private sealed class InjectedLink
        {
            public readonly NavigationGraphEdge Edge;
            public readonly RuntimeRainOffMeshLink Link;

            public InjectedLink(NavigationGraphEdge edge, RuntimeRainOffMeshLink link)
            {
                Edge = edge;
                Link = link;
            }
        }
    }
}
