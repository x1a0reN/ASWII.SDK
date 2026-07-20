using ASWDEBUG.Logger;
using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using RainNavMesh = RAIN.Navigation.NavMesh.NavMesh;

namespace ASWDEBUG.Cheats.AutoBattle
{
    internal enum RuntimeRainNavState
    {
        Idle,
        WaitingScene,
        Building,
        Ready,
        Failed
    }

    internal struct RuntimeRainNavSnapshot
    {
        public RuntimeRainNavState State;
        public string MapName;
        public string Detail;
        public string CacheSource;
        public string CacheStatus;
        public string CacheFileName;
        public float Progress01;
        public float ElapsedSeconds;
        public float TimeoutSeconds;
        public float CellSize;
        public int ColliderCount;
        public int GraphSize;
        public int WorkerCount;
        public int CacheCount;
        public int Generation;
        public long CacheBytes;
        public Vector3 BoundsSize;
    }

    internal static class RuntimeRainNavMesh
    {
        private const float SceneSettleSeconds = 0.80f;
        private const float ColliderWaitSeconds = 20f;
        private const float BuildTimeoutSeconds = 75f;
        private const float RuntimeCellSize = 0.25f;
        private const string GeneratorSignature =
            "v1|autoGrid=1|slope=50|height=1.80|radius=0.45|step=0.85|cell=0.25|vertex=0.22|segment=4";

        private static RuntimeRainNavState _state;
        private static string _mapName = string.Empty;
        private static string _detail = "idle";
        private static bool _requested;
        private static bool _registered;
        private static float _sceneReadyAt;
        private static float _waitStartedAt;
        private static float _buildStartedAt;
        private static float _readyAt;
        private static float _nextAttemptAt;
        private static float _nextLogAt;
        private static int _generation;
        private static GameObject _host;
        private static RainNavMesh _navMesh;
        private static bool _reusePending;
        private static CachedNavMeshEntry _activeCache;
        private static string _lastMapName = string.Empty;
        private static string _cacheSource = "none";
        private static string _cacheStatus = "not_checked";
        private static string _cacheFileName = "-";
        private static float _progress;
        private static float _lastBuildSeconds;
        private static int _colliderCount;
        private static int _graphSize;
        private static long _cacheBytes;
        private static Vector3 _boundsSize;
        private static readonly Dictionary<string, CachedNavMeshEntry> CachedMaps =
            new Dictionary<string, CachedNavMeshEntry>(StringComparer.OrdinalIgnoreCase);

        internal static bool Requested
        {
            get { return _requested; }
        }

        internal static bool IsReady
        {
            get { return _state == RuntimeRainNavState.Ready && _registered; }
        }

        internal static bool IsPending
        {
            get { return _requested && (_state == RuntimeRainNavState.WaitingScene || _state == RuntimeRainNavState.Building); }
        }

        internal static bool HasFailed
        {
            get { return _state == RuntimeRainNavState.Failed; }
        }

        internal static float ReadyAt
        {
            get { return _readyAt; }
        }

        internal static string Detail
        {
            get { return _detail; }
        }

        internal static RAIN.Navigation.Graph.RAINNavigationGraph OwnedGraph
        {
            get { return IsReady && _navMesh != null ? _navMesh.Graph : null; }
        }

        internal static RuntimeRainNavSnapshot GetStatusSnapshot()
        {
            float elapsed = _lastBuildSeconds;
            if (_state == RuntimeRainNavState.Building && _buildStartedAt > 0f)
                elapsed = Mathf.Max(0f, Time.realtimeSinceStartup - _buildStartedAt);
            return new RuntimeRainNavSnapshot
            {
                State = _state,
                MapName = string.IsNullOrEmpty(_mapName) ? _lastMapName : _mapName,
                Detail = _detail,
                CacheSource = _cacheSource,
                CacheStatus = _cacheStatus,
                CacheFileName = _cacheFileName,
                Progress01 = Mathf.Clamp01(_progress),
                ElapsedSeconds = elapsed,
                TimeoutSeconds = BuildTimeoutSeconds,
                CellSize = RuntimeCellSize,
                ColliderCount = _colliderCount,
                GraphSize = _graphSize,
                WorkerCount = Math.Max(1, Environment.ProcessorCount / 2),
                CacheCount = CachedMaps.Count,
                Generation = _generation,
                CacheBytes = _cacheBytes,
                BoundsSize = _boundsSize
            };
        }

        internal static void PrepareMap(string mapName, bool runtimeRequired)
        {
            Deactivate("map_change");
            _generation++;
            _mapName = (mapName ?? string.Empty).Trim().ToLowerInvariant();
            _lastMapName = _mapName;
            _requested = runtimeRequired && !string.IsNullOrEmpty(_mapName);
            _state = _requested ? RuntimeRainNavState.WaitingScene : RuntimeRainNavState.Idle;
            _detail = _requested ? "waiting_scene" : "native_or_empty";
            _sceneReadyAt = 0f;
            _waitStartedAt = Time.realtimeSinceStartup;
            _buildStartedAt = 0f;
            _readyAt = 0f;
            _nextAttemptAt = 0f;
            _nextLogAt = 0f;
            _progress = _requested ? 0f : 1f;
            _lastBuildSeconds = 0f;
            _colliderCount = 0;
            _graphSize = 0;
            _boundsSize = Vector3.zero;
            _cacheBytes = 0L;
            _cacheSource = _requested ? "none" : "native";
            _cacheStatus = _requested ? "checking" : "not_required";
            _cacheFileName = _requested
                ? System.IO.Path.GetFileName(RuntimeRainNavDiskCache.GetCachePath(_mapName))
                : "-";

            CachedNavMeshEntry cached;
            if (_requested && TryGetCachedMap(_mapName, out cached))
            {
                _activeCache = cached;
                _host = cached.Host;
                _navMesh = cached.NavMesh;
                _reusePending = true;
                ApplyCacheTelemetry(cached, "memory", "memory_hit");
                _detail = "cached_waiting_scene nodes=" + cached.GraphSize;
            }
            else if (_requested && TryLoadDiskMap(_mapName, out cached))
            {
                _activeCache = cached;
                _host = cached.Host;
                _navMesh = cached.NavMesh;
                _reusePending = true;
                ApplyCacheTelemetry(cached, "disk", "disk_hit");
                _detail = "disk_cached_waiting_scene nodes=" + cached.GraphSize;
            }
            FileLogger.Log("AUTO-BATTLE][NAVMESH", "runtime_prepare generation=" + _generation +
                " map=" + SafeMap(_mapName) + " requested=" + (_requested ? "1" : "0") +
                " cached=" + (_reusePending ? "1" : "0") + " source=" + _cacheSource +
                " cacheStatus=" + SafeOneLine(_cacheStatus, 80));
        }

        internal static void Tick(Level level, Character player, bool navigationActive)
        {
            if (!_requested || _state == RuntimeRainNavState.Ready || _state == RuntimeRainNavState.Failed) return;

            if (_state == RuntimeRainNavState.Building)
            {
                TickBuild();
                return;
            }

            if (!navigationActive || level == null || player == null || player.transform == null)
            {
                _detail = navigationActive ? "waiting_level_player" : "waiting_activation";
                return;
            }
            if (level.state != Level.State.kReady)
            {
                _detail = "waiting_level_ready state=" + level.state;
                return;
            }
            if (!string.IsNullOrEmpty(level.map_name) &&
                !string.Equals(level.map_name.Trim(), _mapName, StringComparison.OrdinalIgnoreCase))
            {
                _detail = "waiting_map current=" + SafeMap(level.map_name);
                return;
            }

            if (_reusePending)
            {
                ActivateCachedMap();
                return;
            }

            float now = Time.realtimeSinceStartup;
            if (_sceneReadyAt <= 0f)
            {
                _sceneReadyAt = now + SceneSettleSeconds;
                _waitStartedAt = now;
                _detail = "settling_scene";
                return;
            }
            if (now < _sceneReadyAt || now < _nextAttemptAt) return;

            Bounds bounds;
            int colliderCount;
            int terrainMask = TerrainMask;
            if (!TryCollectTerrainBounds(terrainMask, out bounds, out colliderCount))
            {
                _detail = "waiting_terrain_colliders";
                _nextAttemptAt = now + 0.50f;
                if (now - _waitStartedAt >= ColliderWaitSeconds)
                    Fail("terrain_colliders_timeout");
                return;
            }

            StartBuild(bounds, colliderCount, terrainMask);
        }

        internal static void Shutdown(string reason)
        {
            Deactivate("shutdown:" + reason);
            List<CachedNavMeshEntry> entries = new List<CachedNavMeshEntry>(CachedMaps.Values);
            CachedMaps.Clear();
            for (int i = 0; i < entries.Count; i++)
                ReleaseCacheEntry(entries[i], reason);
            _requested = false;
            _state = RuntimeRainNavState.Idle;
            _mapName = string.Empty;
            _detail = "shutdown:" + SafeOneLine(reason, 48);
            _sceneReadyAt = 0f;
            _waitStartedAt = 0f;
            _buildStartedAt = 0f;
            _readyAt = 0f;
        }

        internal static void Deactivate(string reason)
        {
            bool hadRuntime = _navMesh != null || _host != null;
            bool cached = _activeCache != null;
            if (_state == RuntimeRainNavState.Building)
            {
                ReleaseCurrentGraph("deactivate_build:" + reason);
            }
            else if (_registered && _navMesh != null)
            {
                try { _navMesh.UnregisterNavigationGraph(); }
                catch (Exception ex)
                {
                    FileLogger.Log("AUTO-BATTLE][NAVMESH", "runtime_unregister_ex=" + ex.GetType().Name +
                        ":" + SafeOneLine(ex.Message, 80));
                }
            }

            _registered = false;
            _requested = false;
            _state = RuntimeRainNavState.Idle;
            _detail = "deactivated:" + SafeOneLine(reason, 48);
            _mapName = string.Empty;
            _host = null;
            _navMesh = null;
            _activeCache = null;
            _reusePending = false;
            _sceneReadyAt = 0f;
            _waitStartedAt = 0f;
            _buildStartedAt = 0f;
            _readyAt = 0f;
            _nextAttemptAt = 0f;
            _nextLogAt = 0f;
            if (hadRuntime)
                FileLogger.Log("AUTO-BATTLE][NAVMESH", "runtime_deactivated reason=" + SafeOneLine(reason, 80) +
                    " cached=" + (cached ? "1" : "0") + " cacheCount=" + CachedMaps.Count);
        }

        private static void StartBuild(Bounds bounds, int colliderCount, int terrainMask)
        {
            try
            {
                string capabilityDetail;
                if (!ProbeRuntimeCapabilities(out capabilityDetail))
                {
                    Fail("runtime_api_missing:" + capabilityDetail);
                    return;
                }

                Vector3 size = bounds.size;
                size.x = Mathf.Max(8f, size.x + 8f);
                size.y = Mathf.Max(12f, size.y + 10f);
                size.z = Mathf.Max(8f, size.z + 8f);

                _host = CreateHost(bounds.center, size);
                _navMesh = CreateNavMesh(_host, terrainMask);
                _navMesh.StartCreatingContours(-1);

                _state = RuntimeRainNavState.Building;
                _buildStartedAt = Time.realtimeSinceStartup;
                _progress = 0f;
                _colliderCount = colliderCount;
                _boundsSize = size;
                _graphSize = 0;
                _cacheSource = "generated";
                _cacheStatus = "building";
                _detail = "building progress=0";
                _nextLogAt = 0f;
                FileLogger.Log("AUTO-BATTLE][NAVMESH", "runtime_build_started generation=" + _generation +
                    " map=" + SafeMap(_mapName) + " colliders=" + colliderCount +
                    " center=" + FormatVector(bounds.center) + " size=" + FormatVector(size) +
                    " cell=" + _navMesh.CellSize.ToString("0.00") + " workers=auto_half_cpu");
            }
            catch (Exception ex)
            {
                Fail("build_start_ex:" + ex.GetType().Name + ":" + SafeOneLine(ex.Message, 96));
            }
        }

        private static GameObject CreateHost(Vector3 center, Vector3 size)
        {
            GameObject host = new GameObject("ASWDEBUG_RuntimeRainNav_" + SafeMap(_mapName));
            host.hideFlags = HideFlags.HideAndDontSave;
            host.transform.position = center;
            host.transform.rotation = Quaternion.identity;
            host.transform.localScale = size;
            UnityEngine.Object.DontDestroyOnLoad(host);
            return host;
        }

        private static RainNavMesh CreateNavMesh(GameObject host, int terrainMask)
        {
            RainNavMesh navMesh = new RainNavMesh();
            navMesh.MountPoint = host.transform;
            navMesh.GraphName = "ASWDEBUG_RuntimeRainNav_" + SafeMap(_mapName) + "_" + _generation;
            navMesh.Size = 1f;
            navMesh.AutomaticGridSize = true;
            navMesh.IncludedLayers = terrainMask;
            navMesh.IgnoredTags = new List<string>();
            navMesh.UnwalkableTags = new List<string>();
            navMesh.MaxSlope = 50f;
            navMesh.WalkableHeight = 1.80f;
            navMesh.WalkableRadius = 0.45f;
            navMesh.StepHeight = 0.85f;
            navMesh.CellSize = RuntimeCellSize;
            navMesh.MaxVertexError = 0.22f;
            navMesh.MaxSegmentLength = 4f;
            return navMesh;
        }

        private static bool TryLoadDiskMap(string mapName, out CachedNavMeshEntry cached)
        {
            cached = null;
            RuntimeRainNavCacheRecord record;
            string status;
            string rainIdentity = GetRainIdentity();
            if (!RuntimeRainNavDiskCache.TryLoad(mapName, rainIdentity, GeneratorSignature, out record, out status))
            {
                _cacheStatus = status;
                FileLogger.Log("AUTO-BATTLE][NAVCACHE", "disk_miss map=" + SafeMap(mapName) +
                    " reason=" + SafeOneLine(status, 100) + " file=" + _cacheFileName);
                return false;
            }

            GameObject host = null;
            RainNavMesh navMesh = null;
            try
            {
                host = CreateHost(record.BoundsCenter, record.BoundsSize);
                navMesh = CreateNavMesh(host, TerrainMask);
                string capability;
                if (!ProbeDiskCacheCapabilities(navMesh, out capability))
                {
                    _cacheStatus = "api_missing=" + capability;
                    ReleaseUnregisteredGraph(navMesh, host, "disk_api_missing");
                    return false;
                }

                navMesh.Graph.Deserialize(record.Payload);
                int graphSize = navMesh.Graph == null ? 0 : navMesh.Graph.Size;
                if (graphSize <= 0 || graphSize != record.GraphSize)
                {
                    _cacheStatus = "graph_size_mismatch=" + graphSize + "/" + record.GraphSize;
                    ReleaseUnregisteredGraph(navMesh, host, "disk_graph_invalid");
                    return false;
                }

                cached = new CachedNavMeshEntry(mapName, host, navMesh, graphSize, _generation,
                    record.ColliderCount, record.BoundsSize, record.FileBytes);
                CachedMaps[mapName] = cached;
                FileLogger.Log("AUTO-BATTLE][NAVCACHE", "disk_hit map=" + SafeMap(mapName) +
                    " nodes=" + graphSize + " bytes=" + record.FileBytes +
                    " file=" + SafeOneLine(record.FilePath, 180));
                return true;
            }
            catch (Exception ex)
            {
                _cacheStatus = "deserialize_ex=" + ex.GetType().Name + ":" + SafeOneLine(ex.Message, 80);
                ReleaseUnregisteredGraph(navMesh, host, "disk_deserialize_failed");
                FileLogger.Log("AUTO-BATTLE][NAVCACHE", "disk_invalid map=" + SafeMap(mapName) +
                    " reason=" + SafeOneLine(_cacheStatus, 120));
                return false;
            }
        }

        private static bool ProbeDiskCacheCapabilities(RainNavMesh navMesh, out string detail)
        {
            try
            {
                if (navMesh == null || navMesh.Graph == null)
                {
                    detail = "graph=null";
                    return false;
                }
                Type graphType = navMesh.Graph.GetType();
                if (graphType.GetMethod("Serialize", BindingFlags.Instance | BindingFlags.Public, null,
                    Type.EmptyTypes, null) == null)
                {
                    detail = "method=Serialize";
                    return false;
                }
                if (graphType.GetMethod("Deserialize", BindingFlags.Instance | BindingFlags.Public, null,
                    new Type[] { typeof(byte[]) }, null) == null)
                {
                    detail = "method=Deserialize(Byte[])";
                    return false;
                }
                detail = "ok";
                return true;
            }
            catch (Exception ex)
            {
                detail = ex.GetType().Name + ":" + SafeOneLine(ex.Message, 80);
                return false;
            }
        }

        private static void ApplyCacheTelemetry(CachedNavMeshEntry cached, string source, string status)
        {
            if (cached == null) return;
            _cacheSource = source;
            _cacheStatus = status;
            _progress = 1f;
            _graphSize = cached.GraphSize;
            _colliderCount = cached.ColliderCount;
            _boundsSize = cached.BoundsSize;
            _cacheBytes = cached.CacheBytes;
        }

        private static string GetRainIdentity()
        {
            try
            {
                Assembly assembly = typeof(RainNavMesh).Assembly;
                return assembly.FullName + "|mvid=" + assembly.ManifestModule.ModuleVersionId.ToString("D");
            }
            catch (Exception ex)
            {
                return "unknown:" + ex.GetType().Name;
            }
        }

        private static bool ProbeRuntimeCapabilities(out string detail)
        {
            try
            {
                Type type = typeof(RainNavMesh);
                string[] methods =
                {
                    "CreateContours", "CancelCreatingContours", "RegisterNavigationGraph",
                    "UnregisterNavigationGraph", "ClearNavigationGraph"
                };
                for (int i = 0; i < methods.Length; i++)
                {
                    if (type.GetMethod(methods[i], BindingFlags.Instance | BindingFlags.Public, null, Type.EmptyTypes, null) == null)
                    {
                        detail = "method=" + methods[i];
                        return false;
                    }
                }
                if (type.GetMethod("StartCreatingContours", BindingFlags.Instance | BindingFlags.Public, null,
                    new Type[] { typeof(int) }, null) == null)
                {
                    detail = "method=StartCreatingContours(Int32)";
                    return false;
                }

                string[] requiredProperties =
                {
                    "MountPoint", "GraphName", "Size", "IncludedLayers", "Creating", "CreatingProgress", "Graph"
                };
                for (int i = 0; i < requiredProperties.Length; i++)
                {
                    if (type.GetProperty(requiredProperties[i], BindingFlags.Instance | BindingFlags.Public) == null)
                    {
                        detail = "property=" + requiredProperties[i];
                        return false;
                    }
                }

                Assembly assembly = type.Assembly;
                detail = "ok assembly=" + assembly.GetName().Name + " version=" + assembly.GetName().Version;
                FileLogger.Log("AUTO-BATTLE][NAVMESH", "runtime_api_probe " + detail);
                return true;
            }
            catch (Exception ex)
            {
                detail = ex.GetType().Name + ":" + SafeOneLine(ex.Message, 96);
                return false;
            }
        }

        private static void ActivateCachedMap()
        {
            try
            {
                int graphSize = _navMesh == null || _navMesh.Graph == null ? 0 : _navMesh.Graph.Size;
                if (_activeCache == null || _host == null || graphSize <= 0)
                {
                    Fail("cached_graph_invalid");
                    return;
                }

                _navMesh.RegisterNavigationGraph();
                _registered = true;
                _reusePending = false;
                _state = RuntimeRainNavState.Ready;
                _readyAt = Time.realtimeSinceStartup;
                _progress = 1f;
                _graphSize = graphSize;
                _detail = "ready cached=1 source=" + _cacheSource + " nodes=" + graphSize;
                FileLogger.Log("AUTO-BATTLE][NAVMESH", "runtime_reused generation=" + _generation +
                    " map=" + SafeMap(_mapName) + " nodes=" + graphSize +
                    " source=" + _cacheSource + " cacheCount=" + CachedMaps.Count);
            }
            catch (Exception ex)
            {
                Fail("cache_register_ex:" + ex.GetType().Name + ":" + SafeOneLine(ex.Message, 96));
            }
        }

        private static bool TryGetCachedMap(string mapName, out CachedNavMeshEntry cached)
        {
            cached = null;
            CachedNavMeshEntry candidate;
            if (!CachedMaps.TryGetValue(mapName, out candidate) || candidate == null) return false;
            if (candidate.Host != null && candidate.NavMesh != null && candidate.NavMesh.Graph != null &&
                candidate.NavMesh.Graph.Size > 0)
            {
                cached = candidate;
                return true;
            }

            CachedMaps.Remove(mapName);
            ReleaseCacheEntry(candidate, "invalid_cache");
            return false;
        }

        private static void TickBuild()
        {
            if (_navMesh == null)
            {
                Fail("builder_missing");
                return;
            }

            float now = Time.realtimeSinceStartup;
            try
            {
                _progress = Mathf.Clamp01(_navMesh.CreatingProgress);
                if (now - _buildStartedAt >= BuildTimeoutSeconds)
                {
                    Fail("build_timeout progress=" + _progress.ToString("0.000"));
                    return;
                }

                if (_navMesh.Creating) _navMesh.CreateContours();
                if (_navMesh.Creating)
                {
                    _progress = Mathf.Clamp01(_navMesh.CreatingProgress);
                    _detail = "building progress=" + _progress.ToString("0.000");
                    if (now >= _nextLogAt)
                    {
                        _nextLogAt = now + 1f;
                        FileLogger.Log("AUTO-BATTLE][NAVMESH", "runtime_building generation=" + _generation +
                            " map=" + SafeMap(_mapName) + " progress=" + _progress.ToString("0.000") +
                            " wait=" + (now - _buildStartedAt).ToString("0.0"));
                    }
                    return;
                }

                int graphSize = _navMesh.Graph == null ? 0 : _navMesh.Graph.Size;
                if (graphSize <= 0)
                {
                    Fail("empty_graph");
                    return;
                }

                _progress = 1f;
                _graphSize = graphSize;
                _lastBuildSeconds = now - _buildStartedAt;
                PersistCurrentGraph(graphSize);
                _navMesh.RegisterNavigationGraph();
                _registered = true;
                _state = RuntimeRainNavState.Ready;
                _readyAt = now;
                _detail = "ready nodes=" + graphSize;
                CachedNavMeshEntry oldCache;
                if (CachedMaps.TryGetValue(_mapName, out oldCache) && oldCache != null &&
                    oldCache.NavMesh != _navMesh)
                {
                    CachedMaps.Remove(_mapName);
                    ReleaseCacheEntry(oldCache, "cache_replace");
                }
                _activeCache = new CachedNavMeshEntry(_mapName, _host, _navMesh, graphSize, _generation,
                    _colliderCount, _boundsSize, _cacheBytes);
                CachedMaps[_mapName] = _activeCache;
                FileLogger.Log("AUTO-BATTLE][NAVMESH", "runtime_ready generation=" + _generation +
                    " map=" + SafeMap(_mapName) + " nodes=" + graphSize +
                    " build=" + _lastBuildSeconds.ToString("0.0") +
                    " cached=1 disk=" + SafeOneLine(_cacheStatus, 80) +
                    " cacheCount=" + CachedMaps.Count);
            }
            catch (Exception ex)
            {
                Fail("build_tick_ex:" + ex.GetType().Name + ":" + SafeOneLine(ex.Message, 96));
            }
        }

        private static void PersistCurrentGraph(int graphSize)
        {
            string capability;
            if (!ProbeDiskCacheCapabilities(_navMesh, out capability))
            {
                _cacheStatus = "api_missing=" + capability;
                FileLogger.Log("AUTO-BATTLE][NAVCACHE", "save_skipped map=" + SafeMap(_mapName) +
                    " reason=" + SafeOneLine(_cacheStatus, 100));
                return;
            }

            try
            {
                _cacheStatus = "saving";
                byte[] payload = _navMesh.Graph.Serialize();
                string path;
                long fileBytes;
                string status;
                bool saved = RuntimeRainNavDiskCache.TrySave(_mapName, GetRainIdentity(), GeneratorSignature,
                    _host.transform.position, _boundsSize, _colliderCount, graphSize, payload,
                    out fileBytes, out path, out status);
                _cacheStatus = status;
                _cacheFileName = System.IO.Path.GetFileName(path);
                if (saved) _cacheBytes = fileBytes;
                FileLogger.Log("AUTO-BATTLE][NAVCACHE", (saved ? "disk_saved" : "disk_save_failed") +
                    " map=" + SafeMap(_mapName) + " nodes=" + graphSize + " payload=" +
                    (payload == null ? 0 : payload.Length) + " bytes=" + fileBytes +
                    " status=" + SafeOneLine(status, 100) + " file=" + SafeOneLine(path, 180));
            }
            catch (Exception ex)
            {
                _cacheStatus = "serialize_ex=" + ex.GetType().Name + ":" + SafeOneLine(ex.Message, 80);
                FileLogger.Log("AUTO-BATTLE][NAVCACHE", "disk_save_failed map=" + SafeMap(_mapName) +
                    " reason=" + SafeOneLine(_cacheStatus, 120));
            }
        }

        private static bool TryCollectTerrainBounds(int terrainMask, out Bounds bounds, out int colliderCount)
        {
            bounds = new Bounds();
            colliderCount = 0;
            try
            {
                UnityEngine.Object[] objects = UnityEngine.Object.FindObjectsOfType(typeof(Collider));
                if (objects == null) return false;
                for (int i = 0; i < objects.Length; i++)
                {
                    Collider collider = objects[i] as Collider;
                    if (collider == null || !collider.enabled || collider.isTrigger) continue;
                    if ((terrainMask & (1 << collider.gameObject.layer)) == 0) continue;
                    Transform root = collider.transform == null ? null : collider.transform.root;
                    if (root != null && root.GetComponent<Character>() != null) continue;

                    Bounds colliderBounds = collider.bounds;
                    Vector3 size = colliderBounds.size;
                    if (float.IsNaN(size.x) || float.IsInfinity(size.x) ||
                        float.IsNaN(size.y) || float.IsInfinity(size.y) ||
                        float.IsNaN(size.z) || float.IsInfinity(size.z)) continue;
                    if (size.x < 0.01f && size.z < 0.01f) continue;
                    if (collider is WheelCollider) continue;
                    if (colliderCount == 0) bounds = colliderBounds;
                    else bounds.Encapsulate(colliderBounds);
                    colliderCount++;
                }
                return colliderCount > 0 && bounds.size.x > 1f && bounds.size.z > 1f;
            }
            catch (Exception ex)
            {
                _detail = "bounds_ex:" + ex.GetType().Name + ":" + SafeOneLine(ex.Message, 80);
                return false;
            }
        }

        private static void Fail(string reason)
        {
            string safeReason = SafeOneLine(reason, 160);
            ReleaseCurrentGraph("failed:" + safeReason);
            _state = RuntimeRainNavState.Failed;
            _detail = safeReason;
            FileLogger.Log("AUTO-BATTLE][NAVMESH", "runtime_failed generation=" + _generation +
                " map=" + SafeMap(_mapName) + " reason=" + safeReason);
        }

        private static void ReleaseCurrentGraph(string reason)
        {
            RainNavMesh owned = _navMesh;
            _navMesh = null;
            CachedNavMeshEntry activeCache = _activeCache;
            _activeCache = null;
            if (activeCache != null)
                CachedMaps.Remove(activeCache.MapName);
            if (owned != null)
            {
                try
                {
                    if (owned.Creating) owned.CancelCreatingContours();
                    else if (_registered || (owned.Graph != null && owned.Graph.Size > 0)) owned.ClearNavigationGraph();
                }
                catch (Exception ex)
                {
                    FileLogger.Log("AUTO-BATTLE][NAVMESH", "runtime_release_ex=" + ex.GetType().Name +
                        ":" + SafeOneLine(ex.Message, 80));
                }
            }
            _registered = false;
            _reusePending = false;

            GameObject ownedHost = _host;
            _host = null;
            if (ownedHost != null)
            {
                try { UnityEngine.Object.Destroy(ownedHost); }
                catch { }
            }

            if (owned != null || ownedHost != null)
                FileLogger.Log("AUTO-BATTLE][NAVMESH", "runtime_released generation=" + _generation +
                    " map=" + SafeMap(_mapName) + " reason=" + SafeOneLine(reason, 80));
        }

        private static void ReleaseCacheEntry(CachedNavMeshEntry entry, string reason)
        {
            if (entry == null) return;
            try
            {
                if (entry.NavMesh != null)
                {
                    if (entry.NavMesh.Creating) entry.NavMesh.CancelCreatingContours();
                    else if (entry.NavMesh.Graph != null && entry.NavMesh.Graph.Size > 0)
                        entry.NavMesh.ClearNavigationGraph();
                }
            }
            catch (Exception ex)
            {
                FileLogger.Log("AUTO-BATTLE][NAVMESH", "cache_release_ex=" + ex.GetType().Name +
                    ":" + SafeOneLine(ex.Message, 80));
            }
            if (entry.Host != null)
            {
                try { UnityEngine.Object.Destroy(entry.Host); }
                catch { }
            }
            FileLogger.Log("AUTO-BATTLE][NAVMESH", "cache_released map=" + SafeMap(entry.MapName) +
                " generation=" + entry.Generation + " reason=" + SafeOneLine(reason, 80));
        }

        private static void ReleaseUnregisteredGraph(RainNavMesh navMesh, GameObject host, string reason)
        {
            try
            {
                if (navMesh != null && navMesh.Graph != null && navMesh.Graph.Size > 0)
                    navMesh.ClearNavigationGraph();
            }
            catch (Exception ex)
            {
                FileLogger.Log("AUTO-BATTLE][NAVCACHE", "disk_release_ex=" + ex.GetType().Name + ":" +
                    SafeOneLine(ex.Message, 80));
            }
            if (host != null)
            {
                try { UnityEngine.Object.Destroy(host); }
                catch { }
            }
            FileLogger.Log("AUTO-BATTLE][NAVCACHE", "disk_graph_released reason=" + SafeOneLine(reason, 80));
        }

        private sealed class CachedNavMeshEntry
        {
            public readonly string MapName;
            public readonly GameObject Host;
            public readonly RainNavMesh NavMesh;
            public readonly int GraphSize;
            public readonly int Generation;
            public readonly int ColliderCount;
            public readonly Vector3 BoundsSize;
            public readonly long CacheBytes;

            public CachedNavMeshEntry(string mapName, GameObject host, RainNavMesh navMesh,
                int graphSize, int generation, int colliderCount, Vector3 boundsSize, long cacheBytes)
            {
                MapName = mapName;
                Host = host;
                NavMesh = navMesh;
                GraphSize = graphSize;
                Generation = generation;
                ColliderCount = colliderCount;
                BoundsSize = boundsSize;
                CacheBytes = cacheBytes;
            }
        }

        private static int TerrainMask
        {
            get
            {
                int mask = LayerMask.GetMask(new string[] { "Terrarin" });
                return mask == 0 ? 256 : mask;
            }
        }

        private static string SafeMap(string mapName)
        {
            return string.IsNullOrEmpty(mapName) ? "-" : SafeOneLine(mapName, 48);
        }

        private static string FormatVector(Vector3 value)
        {
            return "(" + value.x.ToString("0.0") + "," + value.y.ToString("0.0") + "," +
                value.z.ToString("0.0") + ")";
        }

        private static string SafeOneLine(string value, int maxLength)
        {
            if (string.IsNullOrEmpty(value)) return "-";
            string safe = value.Replace('\r', ' ').Replace('\n', ' ').Trim();
            return safe.Length <= maxLength ? safe : safe.Substring(0, maxLength);
        }
    }
}
