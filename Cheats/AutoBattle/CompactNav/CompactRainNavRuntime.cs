using ASWDEBUG.Logger;
using ASWDEBUG.Cheats.AutoBattle;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using UnityEngine;

namespace ASWDEBUG.Cheats.AutoBattle.CompactNav
{
    internal static class CompactRainNavRuntime
    {
        private const string SupportedMap = "level33";
        private const int ExpansionsPerSlice = 4096;
        private const double MillisecondsPerSlice = 8.0;
        private const float BoundaryBucketSize = 4.5f;

        private static CompactRainNavDataset _dataset;
        private static CompactRainNavLoadResult _loadResult;
        private static CompactRainQuery _query;
        private static CompactRouteJob _job;
        private static string _mapName = string.Empty;
        private static string _detail = "idle";
        private static bool _requested;
        private static bool _failed;
        private static int _sceneEpoch;
        private static int _sceneBeginCount;
        private static int _sceneEndCount;
        private static int _queryBeginCount;
        private static int _queryCancelCount;
        private static long _lastManagedBytes;
        private static long _lastPrivateBytes;
        private static Dictionary<long, List<int>> _boundaryBuckets;
        private static int[] _boundarySelectionIndices = new int[0];
        private static float[] _boundarySelectionScores = new float[0];
        private static readonly List<RuntimeRainBoundarySample> BoundarySamplePool =
            new List<RuntimeRainBoundarySample>(96);

        internal static bool Requested { get { return _requested; } }
        internal static bool IsReady { get { return _requested && !_failed && _dataset != null && _query != null; } }
        internal static bool HasFailed { get { return _requested && _failed; } }
        internal static bool IsPending { get { return _requested && !_failed && (_dataset == null || _query == null); } }
        internal static string Detail { get { return _detail; } }
        internal static string CurrentMapName { get { return _mapName; } }

        internal static bool PrepareMap(string mapName)
        {
            string normalized = (mapName ?? string.Empty).Trim().ToLowerInvariant();
            if (!string.Equals(normalized, SupportedMap, StringComparison.OrdinalIgnoreCase))
            {
                DeactivateScene("map_changed:" + normalized);
                return false;
            }
            CancelJob();
            _sceneEpoch++;
            if (_sceneEpoch == int.MaxValue) _sceneEpoch = 1;
            _sceneBeginCount++;
            _mapName = normalized;
            _requested = true;
            _failed = false;
            if (_dataset != null && _query != null)
            {
                EnsureBoundaryIndex();
                _detail = "ready source=process_resident scene=" + _sceneEpoch;
                FileLogger.Log("AUTO-BATTLE][ASWNAV", "scene_begin map=" + normalized +
                    " scene=" + _sceneEpoch + " source=process_resident resident=" + _dataset.ResidentBytes +
                    " workspace=" + _query.WorkspaceBytes + " loadCount=" +
                    CompactRainNavLoader.ProcessLoadCount + " activeQueries=0 sceneBegins=" +
                    _sceneBeginCount + MemoryTelemetry());
                return true;
            }

            string path = GetCachePath();
            if (!File.Exists(path))
            {
                _failed = true;
                _detail = "aswnav_missing path=" + path + " run=Tools/CompactNavConverter";
                FileLogger.Log("AUTO-BATTLE][ASWNAV", "load_failed map=" + normalized + " " + _detail);
                return false;
            }
            CompactRainNavDataset loaded;
            CompactRainNavLoadResult result;
            if (!CompactRainNavLoader.TryLoadProcessSingleton(path, out loaded, out result) || loaded == null)
            {
                _loadResult = result;
                _failed = true;
                _detail = result == null ? "aswnav_load_failed" : result.Status;
                FileLogger.Log("AUTO-BATTLE][ASWNAV", "load_failed map=" + normalized +
                    " detail=" + Safe(_detail));
                return false;
            }
            _dataset = loaded;
            _loadResult = result;
            _query = new CompactRainQuery(_dataset);
            EnsureBoundaryIndex();
            _detail = "ready source=disk sha=" + result.FileSha256 + " loadMs=" +
                result.ElapsedMilliseconds + " resident=" + _dataset.ResidentBytes +
                " workspace=" + _query.WorkspaceBytes;
            FileLogger.Log("AUTO-BATTLE][ASWNAV", "loaded map=" + normalized +
                " file=" + result.FilePath + " sha=" + result.FileSha256 +
                " fileBytes=" + result.FileBytes + " resident=" + result.ResidentDatasetBytes +
                " workspace=" + _query.WorkspaceBytes + " bvh=" + result.BvhNodeCount +
                " loadMs=" + result.ElapsedMilliseconds + " managedDelta=" +
                (result.ManagedBytesAfter - result.ManagedBytesBefore) + " privateDelta=" +
                (result.PrivateBytesAfter - result.PrivateBytesBefore) + " loadCount=" +
                CompactRainNavLoader.ProcessLoadCount + " activeQueries=0 sceneBegins=" +
                _sceneBeginCount + MemoryTelemetry());
            return true;
        }

        internal static void DeactivateScene(string reason)
        {
            bool wasRequested = _requested;
            CancelJob();
            _requested = false;
            _failed = false;
            _detail = "scene_inactive reason=" + Safe(reason);
            if (wasRequested)
            {
                _sceneEndCount++;
                FileLogger.Log("AUTO-BATTLE][ASWNAV", "scene_end map=" + Safe(_mapName) +
                    " scene=" + _sceneEpoch + " dataset=retained unityRefs=0 loadCount=" +
                    CompactRainNavLoader.ProcessLoadCount + " activeQueries=0 sceneBegins=" +
                    _sceneBeginCount + " sceneEnds=" + _sceneEndCount + " queryBegins=" +
                    _queryBeginCount + " queryCancels=" + _queryCancelCount +
                    " reason=" + Safe(reason) + MemoryTelemetry());
            }
        }

        internal static void Shutdown(string reason)
        {
            DeactivateScene(reason);
            // Dataset and workspace intentionally remain process-resident; they contain no Unity objects.
        }

        internal static bool IsPointOnGraph(Vector3 point, float tolerance)
        {
            if (!IsReady) return false;
            CompactRainProjection projection;
            return _dataset.SpatialIndex.TryProject(ToPoint(point), Math.Max(0f, tolerance),
                Math.Max(1.65f, tolerance), out projection);
        }

        internal static bool IsSafeWalkSegment(Vector3 from, Vector3 to, out string detail)
        {
            detail = "aswnav=not_ready";
            if (!IsReady || _query == null) return false;
            return _query.TryValidateWalkSegment(ToPoint(from), ToPoint(to), out detail);
        }

        internal static int PolyCount
        {
            get { return _dataset == null ? 0 : _dataset.PolyCount; }
        }

        internal static bool TryProjectInfo(Vector3 point, float horizontalTolerance,
            float verticalTolerance, out Vector3 projected, out int polyIndex, out int component)
        {
            projected = point;
            polyIndex = -1;
            component = -1;
            if (!IsReady) return false;
            CompactRainProjection projection;
            if (!_dataset.SpatialIndex.TryProject(ToPoint(point), horizontalTolerance,
                verticalTolerance, out projection)) return false;
            projected = ToVector(projection.Point);
            polyIndex = projection.PolyIndex;
            component = _dataset.GetPoly(polyIndex).Component;
            return true;
        }

        internal static bool TryGetPolySample(int polyIndex, out Vector3 point,
            out int component, out int sharedPortalCount, out bool safeSpawn)
        {
            point = Vector3.zero;
            component = -1;
            sharedPortalCount = 0;
            safeSpawn = false;
            if (!IsReady || polyIndex < 0 || polyIndex >= _dataset.PolyCount) return false;
            CompactRainNavPolyRecord poly = _dataset.GetPoly(polyIndex);
            if ((poly.Flags & CompactRainNavFormat.PolyUnwalkable) != 0) return false;
            CompactRainNavSurfaceRecord surface = _dataset.GetSurface(polyIndex);
            point = new Vector3(surface.PositionX, surface.PositionY, surface.PositionZ);
            component = poly.Component;
            safeSpawn = (surface.Flags & 1) != 0;
            for (int i = 0; i < poly.PortalCount; i++)
            {
                int portalIndex = _dataset.GetPolyPortalIndex(poly.PortalStart + i);
                if (_dataset.GetPortal(portalIndex).PolyCount > 1) sharedPortalCount++;
            }
            return true;
        }

        internal static int CollectNearbyBoundaries(Vector3 from, float maxDistance, int maxCount,
            List<RuntimeRainBoundarySample> output)
        {
            if (output == null) return 0;
            output.Clear();
            if (!IsReady || _dataset == null || maxDistance <= 0f || maxCount <= 0)
                return 0;
            EnsureBoundaryIndex();
            if (_boundaryBuckets == null || _boundaryBuckets.Count == 0) return 0;

            EnsureBoundarySelectionCapacity(maxCount);
            int selectedCount = 0;
            int bx = Mathf.FloorToInt(from.x / BoundaryBucketSize);
            int bz = Mathf.FloorToInt(from.z / BoundaryBucketSize);
            int radius = Mathf.Clamp(Mathf.CeilToInt(maxDistance / BoundaryBucketSize), 1, 18);
            float maximumDistanceSquared = maxDistance * maxDistance;
            for (int dz = -radius; dz <= radius; dz++)
            {
                for (int dx = -radius; dx <= radius; dx++)
                {
                    List<int> candidates;
                    if (!_boundaryBuckets.TryGetValue(BoundarySpatialKey(bx + dx, bz + dz),
                        out candidates)) continue;
                    for (int i = 0; i < candidates.Count; i++)
                    {
                        int boundaryIndex = candidates[i];
                        CompactRainNavBoundaryRecord boundary = _dataset.GetBoundary(boundaryIndex);
                        if (boundary.Width < 0.35f) continue;
                        float offsetX = from.x - boundary.PositionX;
                        float offsetZ = from.z - boundary.PositionZ;
                        float horizontalSquared = offsetX * offsetX + offsetZ * offsetZ;
                        if (horizontalSquared > maximumDistanceSquared) continue;
                        float score = Mathf.Sqrt(horizontalSquared) +
                            Mathf.Abs(boundary.PositionY - from.y) * 1.8f;
                        int insertAt = FindBoundaryInsertIndex(score, selectedCount);
                        if (insertAt >= maxCount) continue;
                        int shiftEnd = Mathf.Min(selectedCount, maxCount - 1);
                        for (int shift = shiftEnd; shift > insertAt; shift--)
                        {
                            _boundarySelectionIndices[shift] = _boundarySelectionIndices[shift - 1];
                            _boundarySelectionScores[shift] = _boundarySelectionScores[shift - 1];
                        }
                        _boundarySelectionIndices[insertAt] = boundaryIndex;
                        _boundarySelectionScores[insertAt] = score;
                        if (selectedCount < maxCount) selectedCount++;
                    }
                }
            }

            while (BoundarySamplePool.Count < selectedCount)
                BoundarySamplePool.Add(new RuntimeRainBoundarySample());
            for (int i = 0; i < selectedCount; i++)
            {
                CompactRainNavBoundaryRecord boundary =
                    _dataset.GetBoundary(_boundarySelectionIndices[i]);
                RuntimeRainBoundarySample sample = BoundarySamplePool[i];
                sample.NodeIndex = boundary.PortalIndex;
                sample.Position = new Vector3(boundary.PositionX, boundary.PositionY,
                    boundary.PositionZ);
                sample.Outward = new Vector3(boundary.OutwardX, boundary.OutwardY,
                    boundary.OutwardZ);
                sample.Component = boundary.Component;
                sample.Width = boundary.Width;
                sample.PolyIndex = -1;
                output.Add(sample);
            }
            return output.Count;
        }

        internal static bool TryBuildPath(Vector3 from, Vector3 to,
            AutoBattleRouteCapabilities capabilities, out List<Vector3> result,
            out List<bool> jumpFlags, out bool pending, out bool offMesh, out string detail)
        {
            result = null;
            jumpFlags = null;
            pending = false;
            offMesh = false;
            detail = "aswnav=not_ready";
            if (!IsReady)
            {
                detail = "aswnav=" + (_failed ? "failed " : "pending ") + _detail;
                pending = !_failed;
                return false;
            }
            if (capabilities == null) capabilities = new AutoBattleRouteCapabilities();
            CompactRouteJob job = _job;
            if (job == null || !job.Matches(from, to, capabilities, _sceneEpoch))
            {
                CancelJob();
                CompactRainPathCapabilities compactCapabilities = new CompactRainPathCapabilities(
                    capabilities.AllowJump, capabilities.JumpHeight, capabilities.JumpVelocity,
                    capabilities.RunSpeed, 8f);
                int queryEpoch = _query.Begin(ToPoint(from), ToPoint(to), compactCapabilities,
                    CompactRainQuery.DefaultMaximumHorizontalProjection,
                    CompactRainQuery.DefaultMaximumVerticalProjection);
                job = new CompactRouteJob(_sceneEpoch, queryEpoch, from, to, capabilities);
                _job = job;
                _queryBeginCount++;
            }

            Stopwatch slice = Stopwatch.StartNew();
            CompactRainSearchStatus status = _query.Status;
            if (status == CompactRainSearchStatus.Pending)
                status = _query.Tick(job.QueryEpoch, ExpansionsPerSlice, MillisecondsPerSlice);
            slice.Stop();
            job.Slices++;
            job.CpuMilliseconds += slice.ElapsedMilliseconds;
            if (status == CompactRainSearchStatus.Pending)
            {
                if (job.Slices >= 180 || job.CpuMilliseconds >= 3000L)
                {
                    detail = "aswnav=timeout slices=" + job.Slices + " ms=" + job.CpuMilliseconds +
                        " " + _query.Detail;
                    CancelJob();
                    return false;
                }
                pending = true;
                detail = "aswnav=pending slices=" + job.Slices + " sliceMs=" +
                    slice.ElapsedMilliseconds + " ms=" + job.CpuMilliseconds + " " + _query.Detail;
                return false;
            }
            if (status != CompactRainSearchStatus.Complete || _query.Result == null)
            {
                detail = "aswnav=no_path slices=" + job.Slices + " ms=" + job.CpuMilliseconds +
                    " " + _query.Detail;
                CancelJob();
                return false;
            }

            CompactRainPathResult path = _query.Result;
            result = new List<Vector3>(path.Waypoints.Length + 1);
            jumpFlags = new List<bool>(path.Waypoints.Length + 1);
            Vector3 projectedStart = ToVector(path.StartProjection.Point);
            if (DistanceXZ(from, projectedStart) > 0.10f || Math.Abs(from.y - projectedStart.y) > 0.18f)
            {
                result.Add(from);
                jumpFlags.Add(false);
            }
            for (int i = 0; i < path.Waypoints.Length; i++)
            {
                result.Add(ToVector(path.Waypoints[i]));
                jumpFlags.Add(path.Actions[i] != CompactRainQuery.WalkAction);
            }
            offMesh = path.ActionCount > 0;
            detail = "aswnav=ok provider=aswnav_0_10 pts=" + result.Count +
                " portals=" + path.PortalPath.Length + " offmesh=" + (offMesh ? "1" : "0") +
                " expanded=" + path.ExpandedNodes + " slices=" + job.Slices +
                " ms=" + job.CpuMilliseconds + " queryStartDrift=" +
                DistanceXZ(job.From, from).ToString("0.00") + " startErr=" +
                path.StartProjection.HorizontalError.ToString("0.00") + "/" +
                path.StartProjection.VerticalError.ToString("0.00") + " goalErr=" +
                path.GoalProjection.HorizontalError.ToString("0.00") + "/" +
                path.GoalProjection.VerticalError.ToString("0.00");
            _job = null;
            return result.Count > 0;
        }

        internal static bool CancelPendingPath()
        {
            bool hadPendingPath = _job != null;
            CancelJob();
            return hadPendingPath;
        }

        internal static CompactRainRuntimeSnapshot GetSnapshot()
        {
            CompactRainRuntimeSnapshot snapshot = new CompactRainRuntimeSnapshot();
            snapshot.Requested = _requested;
            snapshot.Ready = IsReady;
            snapshot.Failed = HasFailed;
            snapshot.MapName = _mapName;
            snapshot.Detail = _detail;
            snapshot.SceneEpoch = _sceneEpoch;
            snapshot.SceneBeginCount = _sceneBeginCount;
            snapshot.SceneEndCount = _sceneEndCount;
            snapshot.DatasetLoadCount = CompactRainNavLoader.ProcessLoadCount;
            snapshot.ActiveQueryCount = _job == null ? 0 : 1;
            snapshot.QueryBeginCount = _queryBeginCount;
            snapshot.QueryCancelCount = _queryCancelCount;
            snapshot.ManagedBytes = _lastManagedBytes;
            snapshot.PrivateBytes = _lastPrivateBytes;
            if (_dataset != null)
            {
                snapshot.VertexCount = _dataset.VertexCount;
                snapshot.PolyCount = _dataset.PolyCount;
                snapshot.PortalCount = _dataset.PortalCount;
                snapshot.LinkCount = _dataset.LinkCount;
                snapshot.BoundaryCount = _dataset.BoundaryCount;
                snapshot.SurfaceCount = _dataset.SurfaceCount;
                snapshot.ComponentCount = _dataset.ComponentCount;
                snapshot.SafeSpawnCount = _dataset.SafeSpawnCount;
                snapshot.ResidentBytes = _dataset.ResidentBytes;
            }
            if (_query != null) snapshot.WorkspaceBytes = _query.WorkspaceBytes;
            if (_loadResult != null)
            {
                snapshot.FilePath = _loadResult.FilePath;
                snapshot.FileSha256 = _loadResult.FileSha256;
                snapshot.FileBytes = _loadResult.FileBytes;
                snapshot.LoadMilliseconds = _loadResult.ElapsedMilliseconds;
            }
            return snapshot;
        }

        internal static string GetCachePath()
        {
            string directory = Path.Combine(Path.Combine(Application.persistentDataPath,
                "ASWDEBUG"), "NavMeshCache");
            return Path.Combine(directory, "level33.aswnav");
        }

        private static void EnsureBoundaryIndex()
        {
            if (_dataset == null || _boundaryBuckets != null) return;
            Stopwatch timer = Stopwatch.StartNew();
            Dictionary<long, List<int>> buckets = new Dictionary<long, List<int>>();
            for (int i = 0; i < _dataset.BoundaryCount; i++)
            {
                CompactRainNavBoundaryRecord boundary = _dataset.GetBoundary(i);
                int x = Mathf.FloorToInt(boundary.PositionX / BoundaryBucketSize);
                int z = Mathf.FloorToInt(boundary.PositionZ / BoundaryBucketSize);
                long key = BoundarySpatialKey(x, z);
                List<int> indices;
                if (!buckets.TryGetValue(key, out indices))
                {
                    indices = new List<int>(8);
                    buckets.Add(key, indices);
                }
                indices.Add(i);
            }
            _boundaryBuckets = buckets;
            timer.Stop();
            FileLogger.Log("AUTO-BATTLE][ASWNAV", "boundary_index_ready boundaries=" +
                _dataset.BoundaryCount + " buckets=" + buckets.Count + " ms=" +
                timer.ElapsedMilliseconds);
        }

        private static void EnsureBoundarySelectionCapacity(int count)
        {
            if (_boundarySelectionIndices.Length >= count) return;
            int capacity = Math.Max(96, count);
            _boundarySelectionIndices = new int[capacity];
            _boundarySelectionScores = new float[capacity];
        }

        private static int FindBoundaryInsertIndex(float score, int count)
        {
            int low = 0;
            int high = count;
            while (low < high)
            {
                int middle = low + ((high - low) >> 1);
                if (_boundarySelectionScores[middle] <= score) low = middle + 1;
                else high = middle;
            }
            return low;
        }

        private static long BoundarySpatialKey(int x, int z)
        {
            return ((long)x << 32) ^ (uint)z;
        }

        private static void CancelJob()
        {
            if (_job != null && _query != null)
            {
                _query.Cancel(_job.QueryEpoch);
                _queryCancelCount++;
            }
            _job = null;
        }

        private static string MemoryTelemetry()
        {
            _lastManagedBytes = GC.GetTotalMemory(false);
            _lastPrivateBytes = GetPrivateBytes();
            return " managed=" + _lastManagedBytes + " private=" + _lastPrivateBytes;
        }

        private static long GetPrivateBytes()
        {
            Process process = null;
            try
            {
                process = Process.GetCurrentProcess();
                return process.PrivateMemorySize64;
            }
            catch { return 0L; }
            finally { if (process != null) process.Dispose(); }
        }

        private static CompactRainPoint ToPoint(Vector3 value)
        {
            return new CompactRainPoint(value.x, value.y, value.z);
        }

        private static Vector3 ToVector(CompactRainPoint value)
        {
            return new Vector3(value.X, value.Y, value.Z);
        }

        private static float DistanceXZ(Vector3 left, Vector3 right)
        {
            float x = left.x - right.x;
            float z = left.z - right.z;
            return (float)Math.Sqrt(x * x + z * z);
        }

        private static string Safe(string value)
        {
            if (string.IsNullOrEmpty(value)) return "-";
            string safe = value.Replace('\r', ' ').Replace('\n', ' ').Trim();
            return safe.Length <= 160 ? safe : safe.Substring(0, 160);
        }

        private sealed class CompactRouteJob
        {
            public readonly int SceneEpoch;
            public readonly int QueryEpoch;
            public readonly Vector3 From;
            public readonly Vector3 To;
            public readonly bool AllowJump;
            public readonly float JumpHeight;
            public readonly float JumpVelocity;
            public readonly float RunSpeed;
            public int Slices;
            public long CpuMilliseconds;

            public CompactRouteJob(int sceneEpoch, int queryEpoch, Vector3 from, Vector3 to,
                AutoBattleRouteCapabilities capabilities)
            {
                SceneEpoch = sceneEpoch;
                QueryEpoch = queryEpoch;
                From = from;
                To = to;
                AllowJump = capabilities.AllowJump;
                JumpHeight = capabilities.JumpHeight;
                JumpVelocity = capabilities.JumpVelocity;
                RunSpeed = capabilities.RunSpeed;
            }

            public bool Matches(Vector3 from, Vector3 to, AutoBattleRouteCapabilities capabilities,
                int sceneEpoch)
            {
                return SceneEpoch == sceneEpoch && AllowJump == capabilities.AllowJump &&
                    Math.Abs(JumpHeight - capabilities.JumpHeight) <= 0.01f &&
                    Math.Abs(JumpVelocity - capabilities.JumpVelocity) <= 0.01f &&
                    Math.Abs(RunSpeed - capabilities.RunSpeed) <= 0.01f &&
                    DistanceXZ(From, from) <= 3.25f && Math.Abs(From.y - from.y) <= 1.50f &&
                    DistanceXZ(To, to) <= 0.65f && Math.Abs(To.y - to.y) <= 0.75f;
            }
        }
    }

    internal sealed class CompactRainRuntimeSnapshot
    {
        public bool Requested;
        public bool Ready;
        public bool Failed;
        public string MapName;
        public string Detail;
        public string FilePath;
        public string FileSha256;
        public long FileBytes;
        public long ResidentBytes;
        public long WorkspaceBytes;
        public long LoadMilliseconds;
        public long ManagedBytes;
        public long PrivateBytes;
        public int SceneEpoch;
        public int SceneBeginCount;
        public int SceneEndCount;
        public int DatasetLoadCount;
        public int ActiveQueryCount;
        public int QueryBeginCount;
        public int QueryCancelCount;
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
