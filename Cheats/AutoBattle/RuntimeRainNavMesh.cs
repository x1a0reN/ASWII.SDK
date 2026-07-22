using ASWDEBUG.Logger;
using ASWDEBUG.Cheats.AutoBattle.CompactNav;
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.InteropServices;
using UnityEngine;
using RainNavMesh = RAIN.Navigation.NavMesh.NavMesh;
using RainContourCreator = RAIN.Navigation.NavMesh.RecastProcess.ContourCreator;

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
        public string Profile;
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
        public RuntimeRainDerivedSnapshot Derived;
        public CompactRainAutoConversionSnapshot Compact;
        public bool BakeArtifactReady;
    }

    internal static class RuntimeRainNavMesh
    {
        private const string ResidentMapName = "level33";
        private static readonly bool EnableResidentRainGraph = false;
        private const float SceneSettleSeconds = 0.80f;
        private const float ColliderWaitSeconds = 20f;
        private const float BuildTimeoutSeconds = 75f;
        private const float ResidentBuildTimeoutSeconds = 900f;
        private const float RuntimeCellSize = 0.20f;
        private const float BakeCellSize = 0.10f;
        private const int RetiredGraphStableFrames = 20;
        private const int RetiredGraphCollectionAttempts = 2;
        private const float RetiredGraphCollectionDelaySeconds = 0.75f;
        private const long HighDetailPreloadFreeAddressBytes = 1700L * 1024L * 1024L;
        private const long HighDetailPreloadLargestRegionBytes = 1024L * 1024L * 1024L;
        private const long HighDetailRuntimeFreeAddressBytes = 1400L * 1024L * 1024L;
        private const long HighDetailRuntimeLargestRegionBytes = 768L * 1024L * 1024L;
        private const long RecycledHeapPreloadFreeAddressBytes = 1200L * 1024L * 1024L;
        private const long RecycledHeapRuntimeFreeAddressBytes = 1100L * 1024L * 1024L;
        private const long RecycledHeapLargestRegionBytes = 640L * 1024L * 1024L;
        private const long ResidentSceneMinimumFreeAddressBytes = 1400L * 1024L * 1024L;
        private const long ResidentSceneMinimumLargestRegionBytes = 1280L * 1024L * 1024L;
        private const long ResidentRuntimeFreeAddressBytes = 1100L * 1024L * 1024L;
        private const long ResidentRuntimeLargestRegionBytes = 640L * 1024L * 1024L;
        private const float SceneLoadGateProbeIntervalSeconds = 0.75f;
        private const string RuntimeGeneratorSignature =
            "v2|longRun=1|autoGrid=1|slope=50|height=1.80|radius=0.45|step=0.85|cell=0.20|vertex=0.16|segment=3";
        private const string BakeGeneratorSignature =
            "v2|maxDetail=1|autoGrid=1|slope=50|height=1.80|radius=0.45|step=0.85|cell=0.10|vertex=0.10|segment=2";

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
        private static bool _diskLoadPending;
        private static bool _retiredCollectionPending;
        private static bool _retiredGcPending;
        private static bool _retiredCollectionBlocked;
        private static int _retiredStableFrames;
        private static int _retiredCollectionAttempts;
        private static int _retiredCompletionFrames;
        private static float _retiredNextActionAt;
        private static string _retiredCollectionReason = string.Empty;
        private static float _nextPreloadGateLogAt;
        private static float _nextSceneLoadGateProbeAt;
        private static bool _sceneLoadGateCached;
        private static bool _sceneLoadGateAvailable;
        private static bool _sceneLoadGateResident;
        private static long _sceneLoadGateMinimumFree;
        private static long _sceneLoadGateMinimumLargest;
        private static string _sceneLoadGateDetail = "not_checked";
        private static bool _retiredGraphCollectionVerified;
        private static bool _loadCircuitBroken;
        private static string _loadCircuitReason = string.Empty;
        private static bool _residentPinned;
        private static bool _residentSuspended;
        private static bool _residentResumePending;
        private static string _residentMapName = string.Empty;
        private static Vector3 _residentMountCenter;
        private static Vector3 _residentMountSize;
        private static int _residentMaterializationCount;
        private static int _residentRegistrationCount;
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
        private static bool _highDetail;
        private static int _workerCount;
        private static string _baseGraphIdentity = string.Empty;
        private static int _baseSaveRetryCount;
        private static float _nextBaseSaveRetryAt;
        private static SerializedNavMeshEntry _serializedCache;
        private static string _lastLoadSource = "none";
        private static readonly List<WeakReference> RetiredGraphWatches =
            new List<WeakReference>();
        private static readonly FieldInfo RainNavMeshGraphField = typeof(RainNavMesh).GetField(
            "_graph", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo RainNavMeshCreatorField = typeof(RainNavMesh).GetField(
            "_creator", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo RainNavMeshCreatingField = typeof(RainNavMesh).GetField(
            "_creatingContours", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo RainNavMeshProgressField = typeof(RainNavMesh).GetField(
            "_creatingProgress", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo NavigationManagerGraphsField =
            typeof(RAIN.Navigation.NavigationManager).GetField("_graphs",
                BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo NavigationManagerNavMeshGraphsField =
            typeof(RAIN.Navigation.NavigationManager).GetField("_navMeshGraphs",
                BindingFlags.Instance | BindingFlags.NonPublic);

        private enum GraphRegistrationState
        {
            Unknown,
            Registered,
            Unregistered
        }

        internal static bool Requested
        {
            get { return _requested; }
        }

        internal static bool IsReady
        {
            get { return !_residentSuspended && _state == RuntimeRainNavState.Ready && _registered; }
        }

        internal static bool IsPending
        {
            get { return _requested && (_state == RuntimeRainNavState.WaitingScene || _state == RuntimeRainNavState.Building); }
        }

        internal static bool IsBuilding
        {
            get { return _requested && _state == RuntimeRainNavState.Building; }
        }

        internal static bool HasDeferredSceneCleanup
        {
            get
            {
                return _retiredCollectionPending || _retiredCollectionBlocked ||
                    _retiredCompletionFrames > 0 || RetiredGraphWatches.Count > 0;
            }
        }

        internal static bool IsRetiredGraphQuiescent
        {
            get { return !_registered && _navMesh == null && _state != RuntimeRainNavState.Building; }
        }

        internal static bool SceneExitReleaseBlocked
        {
            get { return _retiredCollectionBlocked; }
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

        internal static bool IsHighDetail
        {
            get { return _highDetail; }
        }

        internal static string CurrentMapName
        {
            get { return _mapName; }
        }

        internal static RAIN.Navigation.Graph.RAINNavigationGraph OwnedGraph
        {
            get { return IsReady && _navMesh != null ? _navMesh.Graph : null; }
        }

        internal static bool HasResidentLevel33Graph
        {
            get
            {
                return _residentPinned && _navMesh != null && _navMesh.Graph != null &&
                    string.Equals(_residentMapName, ResidentMapName, StringComparison.OrdinalIgnoreCase);
            }
        }

        internal static RuntimeRainNavSnapshot GetStatusSnapshot()
        {
            float elapsed = _lastBuildSeconds;
            if (_state == RuntimeRainNavState.Building && _buildStartedAt > 0f)
                elapsed = Mathf.Max(0f, Time.realtimeSinceStartup - _buildStartedAt);
            RuntimeRainDerivedSnapshot derived = RuntimeRainNavDerivedData.GetSnapshot();
            CompactRainAutoConversionSnapshot compact = CompactRainNavAutoConverter.GetSnapshot();
            bool compactRequired = _highDetail && string.Equals(_mapName, ResidentMapName,
                StringComparison.OrdinalIgnoreCase);
            return new RuntimeRainNavSnapshot
            {
                State = _state,
                MapName = string.IsNullOrEmpty(_mapName) ? _lastMapName : _mapName,
                Detail = _detail,
                CacheSource = _cacheSource,
                CacheStatus = _cacheStatus,
                CacheFileName = _cacheFileName,
                Profile = _highDetail ? "max_detail" : "runtime",
                Progress01 = Mathf.Clamp01(_progress),
                ElapsedSeconds = elapsed,
                TimeoutSeconds = _highDetail ? 0f : CurrentBuildTimeoutSeconds,
                CellSize = _highDetail ? BakeCellSize : RuntimeCellSize,
                ColliderCount = _colliderCount,
                GraphSize = _graphSize,
                WorkerCount = _workerCount > 0 ? _workerCount : Math.Max(1, Environment.ProcessorCount / 2),
                CacheCount = _residentPinned || _activeCache != null ? 1 : 0,
                Generation = _generation,
                CacheBytes = _cacheBytes,
                BoundsSize = _boundsSize,
                Derived = derived,
                Compact = compact,
                BakeArtifactReady = _state == RuntimeRainNavState.Ready && _cacheBytes > 0L &&
                    derived.Stage == RuntimeRainDerivedStage.Ready && derived.CacheBytes > 0L &&
                    (!compactRequired || compact.Ready)
            };
        }

        internal static void PrepareMap(string mapName, bool runtimeRequired, bool highDetail)
        {
            string normalized = (mapName ?? string.Empty).Trim().ToLowerInvariant();
            if (TryResumeResidentGraph(normalized, runtimeRequired, highDetail)) return;
            if (_residentPinned)
            {
                _residentSuspended = true;
                _residentResumePending = false;
                _requested = false;
                _state = RuntimeRainNavState.Failed;
                _detail = "resident_map_locked expected=" + ResidentMapName + " actual=" + SafeMap(normalized);
                TripLoadCircuit(_detail);
                FileLogger.Log("AUTO-BATTLE][NAVMESH", "resident_map_rejected expected=" +
                    ResidentMapName + " actual=" + SafeMap(normalized));
                return;
            }

            DeactivateCore("map_change");
            _generation++;
            _mapName = normalized;
            _lastMapName = _mapName;
            _highDetail = highDetail;
            _workerCount = Math.Max(1, Environment.ProcessorCount);
            _requested = runtimeRequired && !string.IsNullOrEmpty(_mapName);
            _state = _requested && _loadCircuitBroken
                ? RuntimeRainNavState.Failed
                : (_requested ? RuntimeRainNavState.WaitingScene : RuntimeRainNavState.Idle);
            _detail = _requested && _loadCircuitBroken
                ? "load_circuit_broken restart_required " + _loadCircuitReason
                : (_requested ? "waiting_scene" : "native_or_empty");
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
            _baseGraphIdentity = string.Empty;
            _baseSaveRetryCount = 0;
            _nextBaseSaveRetryAt = 0f;
            _cacheSource = _requested ? "none" : "native";
            _cacheStatus = _requested ? "checking" : "not_required";
            _cacheFileName = _requested
                ? System.IO.Path.GetFileName(RuntimeRainNavDiskCache.GetCachePath(_mapName, _highDetail))
                : "-";

            if (_serializedCache != null &&
                !string.Equals(_serializedCache.MapName, _mapName, StringComparison.OrdinalIgnoreCase))
            {
                FileLogger.Log("AUTO-BATTLE][NAVCACHE", "serialized_payload_released previous=" +
                    SafeMap(_serializedCache.MapName) + " next=" + SafeMap(_mapName));
                _serializedCache = null;
            }

            // Non-resident maps never retain a live RAIN graph across a map change. level33 takes
            // the resident fast path above and therefore never reaches this reset after pinning.
            _activeCache = null;
            _host = null;
            _navMesh = null;
            _reusePending = false;
            _diskLoadPending = _requested && !_loadCircuitBroken;
            if (!_loadCircuitBroken)
                _detail = _requested ? "disk_waiting_scene" : "native_navigation";
            FileLogger.Log("AUTO-BATTLE][NAVMESH", "runtime_prepare generation=" + _generation +
                " map=" + SafeMap(_mapName) + " requested=" + (_requested ? "1" : "0") +
                " cached=" + (_reusePending ? "1" : "0") + " source=" + _cacheSource +
                " profile=" + (_highDetail ? "max_detail" : "runtime") +
                " cacheStatus=" + SafeOneLine(_cacheStatus, 80));
        }

        internal static void Tick(Level level, Character player, bool navigationActive)
        {
            if (HasDeferredSceneCleanup)
            {
                _detail = _retiredCollectionBlocked
                    ? "retired_graph_still_alive restart_required"
                    : "waiting_retired_graph_collection_before_load";
                return;
            }
            if (!_requested || _state == RuntimeRainNavState.Failed) return;
            if (_state == RuntimeRainNavState.Ready)
            {
                if (_cacheBytes <= 0L && string.Equals(_cacheSource, "generated", StringComparison.Ordinal))
                {
                    if (_baseSaveRetryCount > 0 && _baseSaveRetryCount < 3 &&
                        Time.realtimeSinceStartup >= _nextBaseSaveRetryAt)
                        PersistCurrentGraph(_graphSize);
                    if (_cacheBytes <= 0L) return;
                }
                RuntimeRainNavDerivedData.Prepare(_mapName, OwnedGraph, _highDetail,
                    GetRainIdentity(), ActiveGeneratorSignature);
                RuntimeRainNavDerivedData.Tick();
                if (RuntimeRainNavDerivedData.IsReady)
                    CompactRainNavAutoConverter.Tick(_mapName, _highDetail);
                return;
            }

            if (_state == RuntimeRainNavState.Building)
            {
                TickBuild();
                return;
            }

            bool playerRequired = !_highDetail;
            if (!navigationActive || level == null ||
                (playerRequired && (player == null || player.transform == null)))
            {
                _detail = navigationActive ? (playerRequired ? "waiting_level_player" : "waiting_level") :
                    "waiting_activation";
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

            float now = Time.realtimeSinceStartup;
            if (_sceneReadyAt <= 0f)
            {
                _sceneReadyAt = now + SceneSettleSeconds;
                _waitStartedAt = now;
                _detail = "settling_scene";
                return;
            }
            if (now < _sceneReadyAt || now < _nextAttemptAt) return;

            if (_residentResumePending)
            {
                CompleteResidentResume();
                return;
            }

            if (_diskLoadPending)
            {
                string memoryDetail;
                if (!HasDiskLoadHeadroom(_highDetail, out memoryDetail))
                {
                    Fail("address_space_low_before_deserialize " + memoryDetail);
                    return;
                }
                _diskLoadPending = false;

                CachedNavMeshEntry cached;
                bool cacheLoadFailed;
                if (TryLoadPreferredDiskMap(_mapName, _highDetail, out cached, out cacheLoadFailed))
                {
                    _activeCache = cached;
                    _host = cached.Host;
                    _navMesh = cached.NavMesh;
                    _reusePending = true;
                    ApplyCacheTelemetry(cached, _lastLoadSource,
                        string.Equals(_lastLoadSource, "memory_payload", StringComparison.Ordinal)
                            ? "memory_payload_hit" : "disk_hit");
                    _detail = "disk_loaded_waiting_register nodes=" + cached.GraphSize;
                    FileLogger.Log("AUTO-BATTLE][NAVMESH", "runtime_disk_loaded generation=" +
                        _generation + " map=" + SafeMap(_mapName) + " nodes=" + cached.GraphSize +
                        " source=" + _lastLoadSource + " after_scene_ready=1");
                }
                else if (cacheLoadFailed)
                {
                    Fail("cache_materialization_failed " + SafeOneLine(_cacheStatus, 120));
                    return;
                }
            }

            if (_reusePending)
            {
                ActivateCachedMap();
                return;
            }

            Bounds bounds;
            int colliderCount;
            int terrainMask = TerrainMask;
            if (!TryCollectTerrainBounds(terrainMask, out bounds, out colliderCount))
            {
                _detail = "waiting_terrain_colliders";
                _nextAttemptAt = now + 0.50f;
                if (!_highDetail && now - _waitStartedAt >= ColliderWaitSeconds)
                    Fail("terrain_colliders_timeout");
                return;
            }

            StartBuild(bounds, colliderCount, terrainMask);
        }

        internal static void Shutdown(string reason)
        {
            DeactivateCore("shutdown:" + reason);
            ReleaseMemoryCache(reason);
            _residentPinned = false;
            _residentSuspended = false;
            _residentResumePending = false;
            _residentMapName = string.Empty;
            _requested = false;
            _state = RuntimeRainNavState.Idle;
            _mapName = string.Empty;
            _detail = "shutdown:" + SafeOneLine(reason, 48);
            _sceneReadyAt = 0f;
            _waitStartedAt = 0f;
            _buildStartedAt = 0f;
            _readyAt = 0f;
        }

        internal static void ReleaseMemoryCache(string reason)
        {
            if (_residentPinned)
            {
                FileLogger.Log("AUTO-BATTLE][NAVMESH", "resident_release_ignored map=" +
                    SafeMap(_residentMapName) + " reason=" + SafeOneLine(reason, 80));
                return;
            }
            try
            {
                if (_navMesh != null || _host != null)
                {
                    ReleaseCurrentGraph("memory_cache:" + reason);
                    return;
                }

                CachedNavMeshEntry orphan = _activeCache;
                _activeCache = null;
                if (orphan != null)
                {
                    ReleaseCacheEntry(orphan, "memory_cache_orphan:" + reason);
                    FileLogger.Log("AUTO-BATTLE][NAVMESH", "memory_cache_orphan_released reason=" +
                        SafeOneLine(reason, 80));
                }
            }
            catch (Exception ex)
            {
                BlockRetiredGraphLifecycle("memory_cache_release_ex:" + ex.GetType().Name);
                FileLogger.Log("AUTO-BATTLE][NAVMESH", "memory_cache_release_ex=" +
                    ex.GetType().Name + ":" + SafeOneLine(ex.Message, 96));
            }
        }

        internal static bool TickDeferredSceneCleanup(bool safeToRelease)
        {
            if (_retiredCompletionFrames > 0)
            {
                _retiredCompletionFrames--;
                _detail = "retired_graph_collection_complete";
                return true;
            }
            if (_retiredCollectionBlocked)
            {
                _detail = "retired_graph_still_alive restart_required";
                return true;
            }
            if (!_retiredCollectionPending)
            {
                if (RetiredGraphWatches.Count <= 0) return false;
                ScheduleRetiredGraphCollection("orphaned_retired_graph");
            }
            if (!safeToRelease)
            {
                _retiredStableFrames = 0;
                _detail = "waiting_stable_lobby_before_graph_collection";
                return true;
            }

            if (_retiredStableFrames < RetiredGraphStableFrames)
            {
                _retiredStableFrames++;
                _detail = "waiting_stable_lobby frames=" + _retiredStableFrames + "/" +
                    RetiredGraphStableFrames;
                return true;
            }

            if (Time.realtimeSinceStartup < _retiredNextActionAt)
            {
                _detail = "waiting_retired_graph_collection";
                return true;
            }

            if (_retiredGcPending)
            {
                _retiredGcPending = false;
                CollectRetiredGraphMemory(RetiredGraphWatches.Count);
                return true;
            }

            int alive = CountAliveRetiredGraphs();
            if (alive == 0)
            {
                CompleteRetiredGraphCollection();
                return true;
            }
            if (_retiredCollectionAttempts >= RetiredGraphCollectionAttempts)
            {
                _retiredCollectionBlocked = true;
                _detail = "retired_graph_still_alive restart_required alive=" + alive;
                FileLogger.Log("AUTO-BATTLE][NAVMESH", "retired_graph_collection_blocked alive=" +
                    alive + " attempts=" + _retiredCollectionAttempts + " reason=" +
                    SafeOneLine(_retiredCollectionReason, 80));
                return true;
            }

            // Do not collect in the same frame that inspected WeakReference.Target; old Mono's
            // conservative stack scanner may otherwise treat that temporary as a live root.
            _retiredGcPending = true;
            _retiredNextActionAt = Time.realtimeSinceStartup + 0.25f;
            _detail = "retired_graph_collection_retry_scheduled alive=" + alive;
            return true;
        }

        private static void ScheduleRetiredGraphCollection(string reason)
        {
            _retiredCollectionPending = true;
            _retiredGraphCollectionVerified = false;
            _retiredGcPending = false;
            _retiredStableFrames = 0;
            _retiredCollectionAttempts = 0;
            _retiredNextActionAt = 0f;
            _retiredCompletionFrames = 0;
            _retiredCollectionReason = reason ?? string.Empty;
        }

        private static int CountAliveRetiredGraphs()
        {
            int alive = 0;
            for (int i = 0; i < RetiredGraphWatches.Count; i++)
            {
                WeakReference watch = RetiredGraphWatches[i];
                if (watch != null && watch.IsAlive) alive++;
            }
            return alive;
        }

        private static void CollectRetiredGraphMemory(int aliveBefore)
        {
            long managedBefore = GC.GetTotalMemory(false);
            MemorySnapshot memoryBefore = ReadMemorySnapshot();

            // The old graph was detached in the scene that owned it. This collection runs in a
            // later frame with no RAIN or Unity cleanup calls on the stack.
            GC.Collect();

            _retiredCollectionAttempts++;
            _retiredNextActionAt = Time.realtimeSinceStartup + RetiredGraphCollectionDelaySeconds;
            _detail = "collecting_retired_graph attempt=" + _retiredCollectionAttempts;
            FileLogger.Log("AUTO-BATTLE][NAVMESH", "retired_graph_gc_requested attempt=" +
                _retiredCollectionAttempts + " aliveBefore=" + aliveBefore + " managedBefore=" +
                managedBefore + " privateBefore=" + memoryBefore.PrivateBytes + " addressFreeBefore=" +
                memoryBefore.FreeAddressBytes + " reason=" + SafeOneLine(_retiredCollectionReason, 80));
        }

        private static void CompleteRetiredGraphCollection()
        {
            string reason = _retiredCollectionReason;
            int attempts = _retiredCollectionAttempts;
            RetiredGraphWatches.Clear();
            _retiredCollectionPending = false;
            _retiredCollectionBlocked = false;
            _retiredGcPending = false;
            _retiredStableFrames = 0;
            _retiredCollectionAttempts = 0;
            _retiredNextActionAt = 0f;
            _retiredCollectionReason = string.Empty;
            _retiredCompletionFrames = 2;
            _retiredGraphCollectionVerified = true;
            _detail = "retired_graph_collected";

            MemorySnapshot memory = ReadMemorySnapshot();
            FileLogger.Log("AUTO-BATTLE][NAVMESH", "retired_graph_collected attempts=" + attempts +
                " managed=" + GC.GetTotalMemory(false) + " private=" + memory.PrivateBytes +
                " addressFree=" + memory.FreeAddressBytes + " largestFree=" +
                memory.LargestFreeRegionBytes + " reason=" + SafeOneLine(reason, 80));
        }

        internal static bool CanStartHighDetailSceneLoad(out string detail)
        {
            if (_residentPinned)
            {
                if (TryGetCachedSceneLoadGate(true, ResidentSceneMinimumFreeAddressBytes,
                    ResidentSceneMinimumLargestRegionBytes, out detail))
                    return _sceneLoadGateAvailable;

                RAIN.Navigation.Graph.RAINNavigationGraph residentGraph =
                    _navMesh == null ? null : _navMesh.Graph;
                string registrationDetail;
                GraphRegistrationState registration = InspectGraphRegistration(residentGraph,
                    out registrationDetail);
                if (!HasResidentLevel33Graph || !_residentSuspended || _registered ||
                    registration != GraphRegistrationState.Unregistered || _host != null)
                {
                    string reason = "resident_scene_preflight_invalid registration=" + registration +
                        " inspection=" + registrationDetail;
                    TripLoadCircuit(reason);
                    detail = "level33 resident graph invalid; restart required | " + reason;
                    return false;
                }

                MemorySnapshot residentMemory = ReadMemorySnapshot();
                bool residentAvailable = HasAddressSpace(residentMemory,
                    ResidentSceneMinimumFreeAddressBytes, ResidentSceneMinimumLargestRegionBytes);
                detail = "level33 resident graph detached and ready; " + FormatMemorySnapshot(residentMemory,
                    ResidentSceneMinimumFreeAddressBytes, ResidentSceneMinimumLargestRegionBytes);
                CacheSceneLoadGate(true, ResidentSceneMinimumFreeAddressBytes,
                    ResidentSceneMinimumLargestRegionBytes, residentAvailable, detail);
                if (!residentAvailable && Time.realtimeSinceStartup >= _nextPreloadGateLogAt)
                {
                    _nextPreloadGateLogAt = Time.realtimeSinceStartup + 3f;
                    FileLogger.Log("AUTO-BATTLE][NAVMESH", "resident_scene_preflight_blocked " + detail);
                }
                return residentAvailable;
            }
            if (_loadCircuitBroken)
            {
                detail = "RAIN 加载熔断，请重启游戏 | " + _loadCircuitReason;
                return false;
            }
            if (HasDeferredSceneCleanup)
            {
                detail = _retiredCollectionBlocked
                    ? "上一张导航图未能回收，请重启游戏"
                    : "正在安全回收上一张导航图";
                return false;
            }

            long minimumFree = _retiredGraphCollectionVerified
                ? RecycledHeapPreloadFreeAddressBytes : HighDetailPreloadFreeAddressBytes;
            long minimumLargest = _retiredGraphCollectionVerified
                ? RecycledHeapLargestRegionBytes : HighDetailPreloadLargestRegionBytes;
            if (TryGetCachedSceneLoadGate(false, minimumFree, minimumLargest, out detail))
                return _sceneLoadGateAvailable;

            MemorySnapshot snapshot = ReadMemorySnapshot();
            bool available = HasAddressSpace(snapshot, minimumFree, minimumLargest);
            detail = FormatMemorySnapshot(snapshot, minimumFree, minimumLargest) +
                " retiredVerified=" + (_retiredGraphCollectionVerified ? "1" : "0");
            CacheSceneLoadGate(false, minimumFree, minimumLargest, available, detail);
            if (!available && Time.realtimeSinceStartup >= _nextPreloadGateLogAt)
            {
                _nextPreloadGateLogAt = Time.realtimeSinceStartup + 3f;
                FileLogger.Log("AUTO-BATTLE][NAVMESH", "scene_load_preflight_blocked " + detail);
            }
            return available;
        }

        private static bool TryGetCachedSceneLoadGate(bool resident, long minimumFree,
            long minimumLargest, out string detail)
        {
            detail = _sceneLoadGateDetail;
            return _sceneLoadGateCached && _sceneLoadGateResident == resident &&
                _sceneLoadGateMinimumFree == minimumFree &&
                _sceneLoadGateMinimumLargest == minimumLargest &&
                Time.realtimeSinceStartup < _nextSceneLoadGateProbeAt;
        }

        private static void CacheSceneLoadGate(bool resident, long minimumFree,
            long minimumLargest, bool available, string detail)
        {
            _sceneLoadGateCached = true;
            _sceneLoadGateResident = resident;
            _sceneLoadGateMinimumFree = minimumFree;
            _sceneLoadGateMinimumLargest = minimumLargest;
            _sceneLoadGateAvailable = available;
            _sceneLoadGateDetail = detail ?? string.Empty;
            _nextSceneLoadGateProbeAt = Time.realtimeSinceStartup +
                SceneLoadGateProbeIntervalSeconds;
        }

        private static void InvalidateSceneLoadGate()
        {
            _sceneLoadGateCached = false;
            _sceneLoadGateAvailable = false;
            _sceneLoadGateDetail = "not_checked";
            _nextSceneLoadGateProbeAt = 0f;
        }

        internal static void SuspendForSceneExit(string reason)
        {
            InvalidateSceneLoadGate();
            if (!_residentPinned)
            {
                DeactivateCore("scene_exit:" + reason);
                return;
            }

            RAIN.Navigation.Graph.RAINNavigationGraph graph = _navMesh == null ? null : _navMesh.Graph;
            string inspection;
            GraphRegistrationState registration = InspectGraphRegistration(graph, out inspection);
            if (graph == null || registration == GraphRegistrationState.Unknown)
            {
                _residentSuspended = true;
                _residentResumePending = false;
                _requested = false;
                _state = RuntimeRainNavState.Failed;
                TripLoadCircuit("resident_suspend_registration_" + registration.ToString().ToLowerInvariant() +
                    ":" + inspection);
                _detail = "resident_graph_invalid restart_required";
                return;
            }

            if (registration == GraphRegistrationState.Registered &&
                !TryUnregisterGraph(graph, "resident_suspend:" + reason))
            {
                _residentSuspended = true;
                _residentResumePending = false;
                _requested = false;
                _state = RuntimeRainNavState.Failed;
                TripLoadCircuit("resident_suspend_unregister_failed:" + inspection);
                _detail = "resident_unregister_failed restart_required";
                return;
            }

            try
            {
                if (_host != null)
                {
                    _residentMountCenter = _host.transform.position;
                    _residentMountSize = _host.transform.localScale;
                }
                _navMesh.MountPoint = null;
                GameObject oldHost = _host;
                _host = null;
                _activeCache = null;
                if (oldHost != null) UnityEngine.Object.Destroy(oldHost);
            }
            catch (Exception ex)
            {
                _residentSuspended = true;
                _residentResumePending = false;
                _requested = false;
                _state = RuntimeRainNavState.Failed;
                TripLoadCircuit("resident_mount_detach_failed:" + ex.GetType().Name);
                _detail = "resident_mount_detach_failed restart_required";
                return;
            }

            _registered = false;
            _residentSuspended = true;
            _residentResumePending = false;
            _requested = false;
            _reusePending = false;
            _diskLoadPending = false;
            _sceneReadyAt = 0f;
            _waitStartedAt = 0f;
            _nextAttemptAt = 0f;
            _detail = "resident_suspended map=" + SafeMap(_residentMapName) + " reason=" +
                SafeOneLine(reason, 64);
            MemorySnapshot memory = ReadMemorySnapshot();
            FileLogger.Log("AUTO-BATTLE][NAVMESH", "resident_suspended map=" +
                SafeMap(_residentMapName) + " nodes=" + _graphSize +
                " register=removed mount=destroyed reason=" +
                SafeOneLine(reason, 80) + " managed=" + GC.GetTotalMemory(false) +
                " private=" + memory.PrivateBytes + " addressFree=" + memory.FreeAddressBytes +
                " largestFree=" + memory.LargestFreeRegionBytes);
        }

        private static bool TryResumeResidentGraph(string mapName, bool runtimeRequired, bool highDetail)
        {
            if (!_residentPinned) return false;
            if (!runtimeRequired || !string.Equals(mapName, _residentMapName,
                StringComparison.OrdinalIgnoreCase)) return false;

            RAIN.Navigation.Graph.RAINNavigationGraph graph = _navMesh == null ? null : _navMesh.Graph;
            string inspection;
            GraphRegistrationState registration = InspectGraphRegistration(graph, out inspection);
            if (graph == null || registration != GraphRegistrationState.Unregistered || _registered ||
                _host != null ||
                (highDetail && !_highDetail))
            {
                _residentSuspended = true;
                _residentResumePending = false;
                _requested = false;
                _state = RuntimeRainNavState.Failed;
                TripLoadCircuit("resident_resume_invalid registration=" + registration +
                    " highDetail=" + (_highDetail ? "1" : "0") + " inspection=" + inspection);
                _detail = "resident_resume_invalid restart_required";
                return true;
            }

            _generation++;
            _mapName = _residentMapName;
            _lastMapName = _residentMapName;
            _residentSuspended = true;
            _residentResumePending = true;
            _requested = true;
            _state = RuntimeRainNavState.WaitingScene;
            _reusePending = false;
            _diskLoadPending = false;
            _sceneReadyAt = 0f;
            _waitStartedAt = Time.realtimeSinceStartup;
            _readyAt = 0f;
            _detail = "resident_waiting_scene map=" + SafeMap(_residentMapName);
            FileLogger.Log("AUTO-BATTLE][NAVMESH", "resident_resume_armed generation=" + _generation +
                " map=" + SafeMap(_residentMapName) + " nodes=" + graph.Size +
                " deserialize=0 init=0 register=pending remount=pending");
            return true;
        }

        private static void CompleteResidentResume()
        {
            InvalidateSceneLoadGate();
            RAIN.Navigation.Graph.RAINNavigationGraph graph = _navMesh == null ? null : _navMesh.Graph;
            string inspection;
            GraphRegistrationState registration = InspectGraphRegistration(graph, out inspection);
            if (graph == null || registration != GraphRegistrationState.Unregistered || _registered ||
                _host != null)
            {
                _residentResumePending = false;
                _residentSuspended = true;
                _requested = false;
                _state = RuntimeRainNavState.Failed;
                TripLoadCircuit("resident_scene_resume_registration_" + registration + ":" + inspection);
                _detail = "resident_scene_resume_invalid restart_required";
                return;
            }

            try
            {
                Vector3 mountSize = _residentMountSize;
                if (mountSize.x <= 0f || mountSize.y <= 0f || mountSize.z <= 0f)
                    mountSize = _boundsSize;
                _host = CreateHost(_residentMountCenter, mountSize);
                _navMesh.MountPoint = _host.transform;
                _navMesh.RegisterNavigationGraph();
                _residentRegistrationCount++;
                string registeredInspection;
                GraphRegistrationState registered = InspectGraphRegistration(graph,
                    out registeredInspection);
                if (registered != GraphRegistrationState.Registered)
                    throw new InvalidOperationException("resident_reregister_" + registered + ":" +
                        registeredInspection);
                _registered = true;
            }
            catch (Exception ex)
            {
                try
                {
                    string ignored;
                    if (InspectGraphRegistration(graph, out ignored) == GraphRegistrationState.Registered)
                        TryUnregisterGraph(graph, "resident_resume_rollback");
                }
                catch { }
                _registered = false;
                try { _navMesh.MountPoint = null; } catch { }
                GameObject failedHost = _host;
                _host = null;
                if (failedHost != null)
                {
                    try { UnityEngine.Object.Destroy(failedHost); } catch { }
                }
                _residentResumePending = false;
                _residentSuspended = true;
                _requested = false;
                _state = RuntimeRainNavState.Failed;
                TripLoadCircuit("resident_scene_reregister_failed:" + ex.GetType().Name + ":" +
                    SafeOneLine(ex.Message, 80));
                _detail = "resident_reregister_failed restart_required";
                return;
            }

            _residentResumePending = false;
            _residentSuspended = false;
            _state = RuntimeRainNavState.Ready;
            _readyAt = Time.realtimeSinceStartup;
            _progress = 1f;
            _detail = "resident_resumed map=" + SafeMap(_residentMapName) + " nodes=" + graph.Size;
            MemorySnapshot memory = ReadMemorySnapshot();
            FileLogger.Log("AUTO-BATTLE][NAVMESH", "resident_resumed generation=" + _generation +
                " map=" + SafeMap(_residentMapName) + " nodes=" + graph.Size +
                " deserialize=0 init=0 register=1 remount=1 registrationCount=" +
                _residentRegistrationCount + " managed=" + GC.GetTotalMemory(false) +
                " private=" + memory.PrivateBytes + " addressFree=" + memory.FreeAddressBytes +
                " largestFree=" + memory.LargestFreeRegionBytes);
        }

        private static void PinResidentGraphIfEligible(string source)
        {
            // Official level33 routing is process-resident ASWNAV data. RAIN graphs are diagnostic
            // scene resources only and must never enter the broken unregister/remount lifecycle.
            if (!EnableResidentRainGraph) return;
            if (!string.Equals(_mapName, ResidentMapName, StringComparison.OrdinalIgnoreCase) ||
                !_registered || _navMesh == null || _navMesh.Graph == null) return;

            _residentPinned = true;
            _residentSuspended = false;
            _residentResumePending = false;
            _residentMapName = ResidentMapName;
            InvalidateSceneLoadGate();
            if (_host != null)
            {
                _residentMountCenter = _host.transform.position;
                _residentMountSize = _host.transform.localScale;
            }

            long releasedPayloadBytes = 0L;
            if (_serializedCache != null && _serializedCache.Record != null &&
                _serializedCache.Record.Payload != null)
                releasedPayloadBytes = _serializedCache.Record.Payload.Length;
            _serializedCache = null;
            _activeCache = null;

            MemorySnapshot memory = ReadMemorySnapshot();
            FileLogger.Log("AUTO-BATTLE][NAVMESH", "resident_pinned map=" + ResidentMapName +
                " nodes=" + _navMesh.Graph.Size + " source=" + SafeOneLine(source, 32) +
                " payloadReleased=" + releasedPayloadBytes + " private=" + memory.PrivateBytes +
                " addressFree=" + memory.FreeAddressBytes + " largestFree=" +
                memory.LargestFreeRegionBytes + " lifetime=process");
        }

        private static bool HasDiskLoadHeadroom(bool highDetail, out string detail)
        {
            MemorySnapshot snapshot = ReadMemorySnapshot();
            bool residentRuntime = !highDetail &&
                string.Equals(_mapName, ResidentMapName, StringComparison.OrdinalIgnoreCase);
            long minimumFree = highDetail
                ? (_retiredGraphCollectionVerified ? RecycledHeapRuntimeFreeAddressBytes :
                    HighDetailRuntimeFreeAddressBytes)
                : (residentRuntime ? ResidentRuntimeFreeAddressBytes : 420L * 1024L * 1024L);
            long minimumLargest = highDetail
                ? (_retiredGraphCollectionVerified ? RecycledHeapLargestRegionBytes :
                    HighDetailRuntimeLargestRegionBytes)
                : (residentRuntime ? ResidentRuntimeLargestRegionBytes : 256L * 1024L * 1024L);
            detail = FormatMemorySnapshot(snapshot, minimumFree, minimumLargest);
            return HasAddressSpace(snapshot, minimumFree, minimumLargest);
        }

        private static bool HasAddressSpace(MemorySnapshot snapshot, long minimumFree, long minimumLargest)
        {
            if (!snapshot.AddressSpaceValid) return false;
            return snapshot.FreeAddressBytes >= minimumFree &&
                   snapshot.LargestFreeRegionBytes >= minimumLargest;
        }

        private static string FormatMemorySnapshot(MemorySnapshot snapshot, long minimumFree,
            long minimumLargest)
        {
            return "valid=" + (snapshot.AddressSpaceValid ? "1" : "0") +
                   " managed=" + GC.GetTotalMemory(false) + " private=" + snapshot.PrivateBytes +
                   " addressFree=" + snapshot.FreeAddressBytes + " largestFree=" +
                   snapshot.LargestFreeRegionBytes + " requiredFree=" + minimumFree +
                   " requiredLargest=" + minimumLargest;
        }

        private static MemorySnapshot ReadMemorySnapshot()
        {
            MemorySnapshot result = new MemorySnapshot();
            try
            {
                ProcessMemoryCountersEx counters = new ProcessMemoryCountersEx();
                counters.Size = (uint)Marshal.SizeOf(typeof(ProcessMemoryCountersEx));
                if (GetProcessMemoryInfo(GetCurrentProcess(), out counters, counters.Size))
                    result.PrivateBytes = unchecked((long)counters.PrivateUsage.ToUInt64());

                ulong cursor = 0UL;
                const ulong limit = 0xFFF00000UL;
                uint structureSize = (uint)Marshal.SizeOf(typeof(MemoryBasicInformation));
                int regionCount = 0;
                while (cursor < limit)
                {
                    MemoryBasicInformation information;
                    UIntPtr queried = VirtualQuery(new IntPtr(unchecked((int)(uint)cursor)),
                        out information, new UIntPtr(structureSize));
                    if (queried == UIntPtr.Zero) break;
                    ulong baseAddress = unchecked((uint)information.BaseAddress.ToInt32());
                    ulong regionSize = information.RegionSize.ToUInt64();
                    if (regionSize == 0UL) break;
                    regionCount++;
                    if (information.State == MemFree)
                    {
                        result.FreeAddressBytes += unchecked((long)regionSize);
                        if (regionSize > (ulong)result.LargestFreeRegionBytes)
                            result.LargestFreeRegionBytes = unchecked((long)regionSize);
                    }
                    ulong next = baseAddress + regionSize;
                    if (next <= cursor) break;
                    cursor = next;
                }
                result.AddressSpaceValid = IntPtr.Size == 4 && regionCount > 0;
            }
            catch
            {
                // Telemetry must never take down the game.
            }
            return result;
        }

        private static bool TryUnregisterGraph(RAIN.Navigation.Graph.RAINNavigationGraph graph,
            string reason)
        {
            if (graph == null) return true;
            try
            {
                RAIN.Navigation.NavigationManager.Instance.Unregister(graph);
            }
            catch (Exception ex)
            {
                FileLogger.Log("AUTO-BATTLE][NAVMESH", "runtime_unregister_ex=" +
                    ex.GetType().Name + ":" + SafeOneLine(ex.Message, 80) + " reason=" +
                    SafeOneLine(reason, 80));
            }

            string inspection;
            GraphRegistrationState registration = InspectGraphRegistration(graph, out inspection);
            bool removed = registration == GraphRegistrationState.Unregistered;
            FileLogger.Log("AUTO-BATTLE][NAVMESH", "runtime_unregister_verified removed=" +
                (removed ? "1" : "0") + " graph=" + SafeOneLine(graph.GraphName, 80) +
                " state=" + registration + " inspection=" + SafeOneLine(inspection, 96) +
                " reason=" + SafeOneLine(reason, 80));
            if (!removed)
            {
                string failure = registration == GraphRegistrationState.Unknown
                    ? "navigation_manager_inspection_failed:"
                    : "navigation_manager_still_references_graph:";
                BlockRetiredGraphLifecycle(failure + reason);
            }
            return removed;
        }

        private static GraphRegistrationState InspectGraphRegistration(
            RAIN.Navigation.Graph.RAINNavigationGraph graph, out string detail)
        {
            if (graph == null)
            {
                detail = "graph=null";
                return GraphRegistrationState.Unregistered;
            }
            try
            {
                RAIN.Navigation.NavigationManager manager = RAIN.Navigation.NavigationManager.Instance;
                if (manager == null)
                {
                    detail = "manager=null";
                    return GraphRegistrationState.Unknown;
                }
                if (NavigationManagerGraphsField == null || NavigationManagerNavMeshGraphsField == null)
                {
                    detail = "registration_fields_missing";
                    return GraphRegistrationState.Unknown;
                }

                System.Collections.IList graphs = NavigationManagerGraphsField.GetValue(manager) as
                    System.Collections.IList;
                System.Collections.IList navGraphs = NavigationManagerNavMeshGraphsField.GetValue(manager) as
                    System.Collections.IList;
                if (graphs == null || navGraphs == null)
                {
                    detail = "registration_lists_unavailable";
                    return GraphRegistrationState.Unknown;
                }

                bool inAll = graphs.Contains(graph);
                bool inNav = navGraphs.Contains(graph);
                if (inAll != inNav)
                {
                    detail = "registration_lists_disagree all=" + (inAll ? "1" : "0") +
                        " nav=" + (inNav ? "1" : "0");
                    return GraphRegistrationState.Unknown;
                }
                detail = "all=" + (inAll ? "1" : "0") + " nav=" + (inNav ? "1" : "0");
                return inAll ? GraphRegistrationState.Registered : GraphRegistrationState.Unregistered;
            }
            catch (Exception ex)
            {
                detail = ex.GetType().Name + ":" + SafeOneLine(ex.Message, 80);
                return GraphRegistrationState.Unknown;
            }
        }

        private static bool IsGraphRegistered(RAIN.Navigation.Graph.RAINNavigationGraph graph)
        {
            string detail;
            return InspectGraphRegistration(graph, out detail) == GraphRegistrationState.Registered;
        }

        private static void BlockRetiredGraphLifecycle(string reason)
        {
            _retiredCollectionPending = true;
            _retiredCollectionBlocked = true;
            _retiredGraphCollectionVerified = false;
            _retiredCollectionReason = reason ?? string.Empty;
            _detail = "retired_graph_lifecycle_blocked restart_required";
            FileLogger.Log("AUTO-BATTLE][NAVMESH", "retired_graph_lifecycle_blocked reason=" +
                SafeOneLine(reason, 120));
        }

        internal static void Deactivate(string reason)
        {
            if (_residentPinned)
            {
                SuspendForSceneExit("deactivate:" + reason);
                return;
            }
            DeactivateCore(reason);
        }

        private static void DeactivateCore(string reason)
        {
            InvalidateSceneLoadGate();
            bool hadRuntime = _navMesh != null || _host != null;
            bool cached = _activeCache != null;
            RAIN.Navigation.Graph.RAINNavigationGraph graph = _navMesh == null ? null : _navMesh.Graph;
            try
            {
                RuntimeRainNavDerivedData.Deactivate(graph);
            }
            catch (Exception ex)
            {
                BlockRetiredGraphLifecycle("derived_release_ex:" + ex.GetType().Name);
                FileLogger.Log("AUTO-BATTLE][NAVMESH", "derived_release_ex=" +
                    ex.GetType().Name + ":" + SafeOneLine(ex.Message, 96));
            }
            try
            {
                if (_navMesh != null || _host != null || _activeCache != null)
                    ReleaseCurrentGraph("deactivate:" + reason);
                else if (_registered)
                    BlockRetiredGraphLifecycle("registered_graph_owner_missing:" + reason);
            }
            catch (Exception ex)
            {
                BlockRetiredGraphLifecycle("active_graph_release_ex:" + ex.GetType().Name);
                FileLogger.Log("AUTO-BATTLE][NAVMESH", "active_graph_release_ex=" +
                    ex.GetType().Name + ":" + SafeOneLine(ex.Message, 96));
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
            _diskLoadPending = false;
            _sceneReadyAt = 0f;
            _waitStartedAt = 0f;
            _buildStartedAt = 0f;
            _readyAt = 0f;
            _nextAttemptAt = 0f;
            _nextLogAt = 0f;
            _residentPinned = false;
            _residentSuspended = false;
            _residentResumePending = false;
            _residentMapName = string.Empty;
            if (hadRuntime)
                FileLogger.Log("AUTO-BATTLE][NAVMESH", "runtime_deactivated reason=" + SafeOneLine(reason, 80) +
                    " cached=" + (cached ? "1" : "0") + " cacheCount=0");
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
                _navMesh = CreateNavMesh(_host, terrainMask, _highDetail);
                _navMesh.StartCreatingContours(_highDetail ? _workerCount : -1);

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
                    " cell=" + _navMesh.CellSize.ToString("0.00") + " workers=" + _workerCount +
                    " profile=" + (_highDetail ? "max_detail" : "runtime") +
                    " timeout=" + (_highDetail ? "unlimited" : CurrentBuildTimeoutSeconds.ToString("0")));
            }
            catch (Exception ex)
            {
                if (ex is OutOfMemoryException)
                    TripLoadCircuit("runtime_build_start_oom:" + SafeMap(_mapName));
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
            return host;
        }

        private static RainNavMesh CreateNavMesh(GameObject host, int terrainMask, bool highDetail)
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
            navMesh.CellSize = highDetail ? BakeCellSize : RuntimeCellSize;
            navMesh.MaxVertexError = highDetail ? 0.10f : 0.16f;
            navMesh.MaxSegmentLength = highDetail ? 2f : 3f;
            return navMesh;
        }

        private static bool TryLoadPreferredDiskMap(string mapName, bool requireHighDetail,
            out CachedNavMeshEntry cached, out bool loadFailed)
        {
            loadFailed = false;
            // Each profile has an isolated disk artifact. level33 long-run navigation must never
            // fall back to the legacy maximum-detail payload.
            SerializedNavMeshEntry serialized = _serializedCache;
            if (serialized != null && serialized.Record != null && serialized.Record.Payload != null &&
                string.Equals(serialized.MapName, mapName, StringComparison.OrdinalIgnoreCase) &&
                serialized.HighDetail == requireHighDetail)
            {
                bool loaded = TryLoadSerializedMap(mapName, requireHighDetail, out cached);
                loadFailed = !loaded;
                return loaded;
            }

            bool diskLoaded = TryLoadDiskMap(mapName, requireHighDetail, out cached);
            loadFailed = !diskLoaded && IsMaterializationFailure(_cacheStatus);
            return diskLoaded;
        }

        private static bool IsMaterializationFailure(string status)
        {
            if (string.IsNullOrEmpty(status)) return false;
            return status.StartsWith("deserialize_ex=", StringComparison.Ordinal) ||
                   status.StartsWith("graph_size_mismatch=", StringComparison.Ordinal) ||
                   status.StartsWith("api_missing=", StringComparison.Ordinal) ||
                   status.StartsWith("load_ex=", StringComparison.Ordinal);
        }

        private static bool TryLoadSerializedMap(string mapName, bool highDetail,
            out CachedNavMeshEntry cached)
        {
            cached = null;
            SerializedNavMeshEntry serialized = _serializedCache;
            if (serialized == null || serialized.Record == null || serialized.Record.Payload == null ||
                !string.Equals(serialized.MapName, mapName, StringComparison.OrdinalIgnoreCase) ||
                serialized.HighDetail != highDetail) return false;

            if (!TryMaterializeRecord(mapName, highDetail, serialized.Record, out cached))
            {
                // A failed in-memory payload must not remain rooted in an x86 process after the
                // partially materialized graph has been retired.
                _serializedCache = null;
                return false;
            }
            _lastLoadSource = "memory_payload";
            FileLogger.Log("AUTO-BATTLE][NAVCACHE", "memory_payload_hit map=" + SafeMap(mapName) +
                " nodes=" + cached.GraphSize + " bytes=" + serialized.Record.Payload.Length +
                " profile=" + (highDetail ? "max_detail" : "runtime"));
            return true;
        }

        private static bool TryLoadDiskMap(string mapName, bool highDetail, out CachedNavMeshEntry cached)
        {
            cached = null;
            RuntimeRainNavCacheRecord record;
            string status;
            string rainIdentity = GetRainIdentity();
            string signature = highDetail ? BakeGeneratorSignature : RuntimeGeneratorSignature;
            if (!RuntimeRainNavDiskCache.TryLoad(mapName, rainIdentity, signature, out record, out status))
            {
                _cacheStatus = status;
                if (!string.IsNullOrEmpty(status) &&
                    status.StartsWith("load_ex=OutOfMemoryException", StringComparison.Ordinal))
                    TripLoadCircuit("cache_file_read_oom:" + SafeMap(mapName));
                FileLogger.Log("AUTO-BATTLE][NAVCACHE", "disk_miss map=" + SafeMap(mapName) +
                    " reason=" + SafeOneLine(status, 100) + " file=" + _cacheFileName);
                return false;
            }

            if (!TryMaterializeRecord(mapName, highDetail, record, out cached)) return false;
            _serializedCache = new SerializedNavMeshEntry(mapName, highDetail, record);
            _lastLoadSource = "disk";
            FileLogger.Log("AUTO-BATTLE][NAVCACHE", "disk_hit map=" + SafeMap(mapName) +
                " nodes=" + cached.GraphSize + " bytes=" + record.FileBytes +
                " profile=" + (highDetail ? "max_detail" : "runtime") +
                " file=" + SafeOneLine(record.FilePath, 180));
            return true;
        }

        private static bool TryMaterializeRecord(string mapName, bool highDetail,
            RuntimeRainNavCacheRecord record, out CachedNavMeshEntry cached)
        {
            cached = null;
            GameObject host = null;
            RainNavMesh navMesh = null;
            try
            {
                bool resident = string.Equals(mapName, ResidentMapName, StringComparison.OrdinalIgnoreCase);
                if (resident && _residentMaterializationCount != 0)
                {
                    _cacheStatus = "resident_materialization_rejected count=" +
                        _residentMaterializationCount;
                    TripLoadCircuit(_cacheStatus);
                    return false;
                }
                host = CreateHost(record.BoundsCenter, record.BoundsSize);
                navMesh = CreateNavMesh(host, TerrainMask, highDetail);
                string capability;
                if (!ProbeDiskCacheCapabilities(navMesh, out capability))
                {
                    _cacheStatus = "api_missing=" + capability;
                    ReleaseUnregisteredGraph(navMesh, host, "disk_api_missing");
                    return false;
                }

                if (resident) _residentMaterializationCount++;
                navMesh.Graph.Deserialize(record.Payload);
                int graphSize = navMesh.Graph == null ? 0 : navMesh.Graph.Size;
                if (graphSize <= 0 || graphSize != record.GraphSize)
                {
                    _cacheStatus = "graph_size_mismatch=" + graphSize + "/" + record.GraphSize;
                    ReleaseUnregisteredGraph(navMesh, host, "disk_graph_invalid");
                    return false;
                }

                cached = new CachedNavMeshEntry(mapName, host, navMesh, graphSize, _generation,
                    record.ColliderCount, record.BoundsSize, record.FileBytes, highDetail,
                    record.PayloadSha256, false);
                return true;
            }
            catch (Exception ex)
            {
                if (ex is OutOfMemoryException)
                    TripLoadCircuit("cache_materialization_oom:" + SafeMap(mapName));
                _cacheStatus = "deserialize_ex=" + ex.GetType().Name + ":" + SafeOneLine(ex.Message, 80);
                ReleaseUnregisteredGraph(navMesh, host, "disk_deserialize_failed");
                FileLogger.Log("AUTO-BATTLE][NAVCACHE", "materialize_invalid map=" + SafeMap(mapName) +
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
                if (RainNavMeshGraphField == null)
                {
                    detail = "field=NavMesh._graph";
                    return false;
                }
                if (RainNavMeshCreatorField == null || RainNavMeshCreatingField == null ||
                    RainNavMeshProgressField == null)
                {
                    detail = "field=NavMesh.build_state";
                    return false;
                }
                if (NavigationManagerGraphsField == null || NavigationManagerNavMeshGraphsField == null)
                {
                    detail = "field=NavigationManager.registration_lists";
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
            _baseGraphIdentity = cached.BaseGraphIdentity;
            _highDetail = cached.HighDetail;
            _workerCount = _highDetail ? Math.Max(1, Environment.ProcessorCount) :
                Math.Max(1, Environment.ProcessorCount / 2);
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
                    "CreateContours", "RegisterNavigationGraph"
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
                if (RainNavMeshGraphField == null)
                {
                    detail = "field=NavMesh._graph";
                    return false;
                }
                if (RainNavMeshCreatorField == null || RainNavMeshCreatingField == null ||
                    RainNavMeshProgressField == null)
                {
                    detail = "field=NavMesh.build_state";
                    return false;
                }
                if (NavigationManagerGraphsField == null || NavigationManagerNavMeshGraphsField == null)
                {
                    detail = "field=NavigationManager.registration_lists";
                    return false;
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

                if (_activeCache.Initialized)
                    throw new InvalidOperationException("scene_graph_registered_more_than_once");
                bool resident = string.Equals(_mapName, ResidentMapName, StringComparison.OrdinalIgnoreCase);
                if (resident && _residentRegistrationCount != 0)
                    throw new InvalidOperationException("resident_registration_rejected count=" +
                        _residentRegistrationCount);
                string memoryDetail;
                if (!HasDiskLoadHeadroom(_highDetail, out memoryDetail))
                {
                    TripLoadCircuit("address_space_low_before_register:" + SafeMap(_mapName));
                    Fail("address_space_low_before_register " + memoryDetail);
                    return;
                }
                _navMesh.RegisterNavigationGraph();
                if (resident) _residentRegistrationCount++;
                _activeCache.Initialized = true;
                string inspection;
                GraphRegistrationState registration = InspectGraphRegistration(_navMesh.Graph, out inspection);
                if (registration != GraphRegistrationState.Registered)
                    throw new InvalidOperationException("rain_graph_registration_" + registration + ":" + inspection);
                _registered = true;
                _reusePending = false;
                _state = RuntimeRainNavState.Ready;
                _readyAt = Time.realtimeSinceStartup;
                _progress = 1f;
                _graphSize = graphSize;
                _detail = "ready cached=1 source=" + _cacheSource + " nodes=" + graphSize;
                if (_cacheBytes > 0L)
                    RuntimeRainNavDerivedData.Prepare(_mapName, _navMesh.Graph, _highDetail,
                        GetRainIdentity(), ActiveGeneratorSignature);
                PinResidentGraphIfEligible("disk_cache");
                FileLogger.Log("AUTO-BATTLE][NAVMESH", "runtime_reused generation=" + _generation +
                    " map=" + SafeMap(_mapName) + " nodes=" + graphSize +
                    " source=" + _cacheSource + " register=init_once cacheCount=1");
            }
            catch (Exception ex)
            {
                if (ex is OutOfMemoryException)
                    TripLoadCircuit("cache_registration_oom:" + SafeMap(_mapName));
                Fail("cache_register_ex:" + ex.GetType().Name + ":" + SafeOneLine(ex.Message, 96));
            }
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
                if (!_highDetail && now - _buildStartedAt >= CurrentBuildTimeoutSeconds)
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
                            " wait=" + (now - _buildStartedAt).ToString("0.0") +
                            " profile=" + (_highDetail ? "max_detail" : "runtime"));
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
                bool resident = string.Equals(_mapName, ResidentMapName, StringComparison.OrdinalIgnoreCase);
                if (resident && _residentRegistrationCount != 0)
                    throw new InvalidOperationException("resident_registration_rejected count=" +
                        _residentRegistrationCount);
                string memoryDetail;
                if (!HasDiskLoadHeadroom(_highDetail, out memoryDetail))
                {
                    TripLoadCircuit("address_space_low_before_build_register:" + SafeMap(_mapName));
                    Fail("address_space_low_before_build_register " + memoryDetail);
                    return;
                }
                _navMesh.RegisterNavigationGraph();
                if (resident) _residentRegistrationCount++;
                string inspection;
                GraphRegistrationState registration = InspectGraphRegistration(_navMesh.Graph, out inspection);
                if (registration != GraphRegistrationState.Registered)
                    throw new InvalidOperationException("rain_graph_registration_" + registration + ":" + inspection);
                _registered = true;
                _state = RuntimeRainNavState.Ready;
                _readyAt = now;
                _detail = "ready nodes=" + graphSize;
                RuntimeRainNavDerivedData.Prepare(_mapName, _navMesh.Graph, _highDetail,
                    GetRainIdentity(), ActiveGeneratorSignature);
                _activeCache = new CachedNavMeshEntry(_mapName, _host, _navMesh, graphSize, _generation,
                    _colliderCount, _boundsSize, _cacheBytes, _highDetail, _baseGraphIdentity, true);
                PinResidentGraphIfEligible("generated");
                FileLogger.Log("AUTO-BATTLE][NAVMESH", "runtime_ready generation=" + _generation +
                    " map=" + SafeMap(_mapName) + " nodes=" + graphSize +
                    " build=" + _lastBuildSeconds.ToString("0.0") +
                    " profile=" + (_highDetail ? "max_detail" : "runtime") +
                    " cached=1 disk=" + SafeOneLine(_cacheStatus, 80) +
                    " cacheCount=1");
            }
            catch (Exception ex)
            {
                if (ex is OutOfMemoryException)
                    TripLoadCircuit("runtime_build_oom:" + SafeMap(_mapName));
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
                ScheduleBaseSaveRetry();
                return;
            }

            try
            {
                _cacheStatus = "saving";
                byte[] payload = _navMesh.Graph.Serialize();
                _baseGraphIdentity = RuntimeRainNavDiskCache.ComputePayloadSha256(payload);
                string path;
                long fileBytes;
                string status;
                string signature = _highDetail ? BakeGeneratorSignature : RuntimeGeneratorSignature;
                bool saved = RuntimeRainNavDiskCache.TrySave(_mapName, GetRainIdentity(), signature,
                    _host.transform.position, _boundsSize, _colliderCount, graphSize, payload,
                    out fileBytes, out path, out status);
                _cacheStatus = status;
                _cacheFileName = System.IO.Path.GetFileName(path);
                if (saved) _cacheBytes = fileBytes;
                if (payload != null && payload.Length > 0)
                {
                    _serializedCache = new SerializedNavMeshEntry(_mapName, _highDetail,
                        new RuntimeRainNavCacheRecord
                        {
                            BoundsCenter = _host.transform.position,
                            BoundsSize = _boundsSize,
                            ColliderCount = _colliderCount,
                            GraphSize = graphSize,
                            Payload = payload,
                            FileBytes = fileBytes,
                            FilePath = path,
                            PayloadSha256 = _baseGraphIdentity
                        });
                }
                if (saved)
                {
                    _baseSaveRetryCount = 0;
                    _nextBaseSaveRetryAt = 0f;
                }
                else ScheduleBaseSaveRetry();
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
                ScheduleBaseSaveRetry();
            }
        }

        private static void ScheduleBaseSaveRetry()
        {
            _baseSaveRetryCount++;
            if (_baseSaveRetryCount > 3) _baseSaveRetryCount = 3;
            _nextBaseSaveRetryAt = Time.realtimeSinceStartup + (1 << _baseSaveRetryCount);
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
                    if (_highDetail)
                    {
                        FileLogger.Log("AUTO-BATTLE][NAVMESH", "bake_collider index=" + colliderCount +
                            " type=" + collider.GetType().Name +
                            " name=" + SafeOneLine(collider.gameObject.name, 72) +
                            " root=" + (root == null ? "-" : SafeOneLine(root.name, 72)) +
                            " layer=" + collider.gameObject.layer +
                            " center=" + FormatVector(colliderBounds.center) +
                            " size=" + FormatVector(colliderBounds.size));
                    }
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

        private static void TripLoadCircuit(string reason)
        {
            InvalidateSceneLoadGate();
            _loadCircuitBroken = true;
            _loadCircuitReason = SafeOneLine(reason, 120);
            FileLogger.Log("AUTO-BATTLE][NAVMESH", "load_circuit_broken restart_required reason=" +
                _loadCircuitReason);
        }

        private static void ReleaseCurrentGraph(string reason)
        {
            RainNavMesh owned = _navMesh;
            _navMesh = null;
            _activeCache = null;
            if (owned != null)
            {
                if (owned.Creating && !StopBuildWorkers(owned, reason))
                    BlockRetiredGraphLifecycle("build_worker_stop_failed:" + reason);
                RAIN.Navigation.Graph.RAINNavigationGraph graph = owned.Graph;
                string inspection;
                GraphRegistrationState registration = InspectGraphRegistration(graph, out inspection);
                if ((_registered || registration != GraphRegistrationState.Unregistered) &&
                    !TryUnregisterGraph(graph, reason))
                    BlockRetiredGraphLifecycle("release_current_unregister_failed:" + reason);
                RetireNavMeshGraph(owned, reason);
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
            RainNavMesh navMesh = entry.NavMesh;
            if (navMesh != null)
            {
                if (navMesh.Creating && !StopBuildWorkers(navMesh, reason))
                    BlockRetiredGraphLifecycle("cache_worker_stop_failed:" + reason);

                RAIN.Navigation.Graph.RAINNavigationGraph graph = navMesh.Graph;
                string inspection;
                GraphRegistrationState registration = InspectGraphRegistration(graph, out inspection);
                if (registration != GraphRegistrationState.Unregistered && !TryUnregisterGraph(graph, reason))
                    BlockRetiredGraphLifecycle("release_cache_unregister_failed:" + reason);
                RetireNavMeshGraph(navMesh, reason);
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
            RetireNavMeshGraph(navMesh, reason);
            if (host != null)
            {
                try { UnityEngine.Object.Destroy(host); }
                catch { }
            }
            FileLogger.Log("AUTO-BATTLE][NAVCACHE", "disk_graph_released reason=" + SafeOneLine(reason, 80));
        }

        private static bool StopBuildWorkers(RainNavMesh navMesh, string reason)
        {
            try
            {
                if (navMesh == null || !navMesh.Creating) return true;
                if (RainNavMeshCreatorField == null || RainNavMeshCreatingField == null ||
                    RainNavMeshProgressField == null)
                    throw new MissingFieldException(typeof(RainNavMesh).FullName, "build_state");

                RainContourCreator creator = RainNavMeshCreatorField.GetValue(navMesh) as RainContourCreator;
                if (creator != null) creator.CancelCreatingContours();
                RainNavMeshCreatorField.SetValue(navMesh, null);
                RainNavMeshCreatingField.SetValue(navMesh, false);
                RainNavMeshProgressField.SetValue(navMesh, 0f);
                FileLogger.Log("AUTO-BATTLE][NAVMESH", "build_workers_joined reason=" +
                    SafeOneLine(reason, 80));
                return true;
            }
            catch (Exception ex)
            {
                FileLogger.Log("AUTO-BATTLE][NAVMESH", "build_worker_stop_ex=" +
                    ex.GetType().Name + ":" + SafeOneLine(ex.Message, 96) + " reason=" +
                    SafeOneLine(reason, 80));
                return false;
            }
        }

        private static void RetireNavMeshGraph(RainNavMesh navMesh, string reason)
        {
            if (navMesh == null) return;
            RAIN.Navigation.Graph.RAINNavigationGraph graph = navMesh.Graph;
            if (graph == null) return;

            bool detached = false;
            try
            {
                if (RainNavMeshGraphField == null)
                    throw new MissingFieldException(typeof(RainNavMesh).FullName, "_graph");
                RainNavMeshGraphField.SetValue(navMesh, null);
                detached = true;
            }
            catch (Exception ex)
            {
                BlockRetiredGraphLifecycle("graph_detach_failed:" + ex.GetType().Name);
                FileLogger.Log("AUTO-BATTLE][NAVMESH", "graph_detach_failed reason=" +
                    SafeOneLine(reason, 80) + " error=" + ex.GetType().Name + ":" +
                    SafeOneLine(ex.Message, 80));
            }

            // Never keep a full RAIN graph alive across a scene boundary. The weak watch is only
            // a fail-closed proof that the engine/GC released the detached object before reload.
            RetiredGraphWatches.Add(new WeakReference(graph));
            ScheduleRetiredGraphCollection(reason);
            FileLogger.Log("AUTO-BATTLE][NAVMESH", "graph_retired_pre_exit nodes=" + graph.Size +
                " detached=" + (detached ? "1" : "0") + " reason=" + SafeOneLine(reason, 80));
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
            public readonly bool HighDetail;
            public readonly string BaseGraphIdentity;
            public bool Initialized;

            public CachedNavMeshEntry(string mapName, GameObject host, RainNavMesh navMesh,
                int graphSize, int generation, int colliderCount, Vector3 boundsSize, long cacheBytes,
                bool highDetail, string baseGraphIdentity, bool initialized)
            {
                MapName = mapName;
                Host = host;
                NavMesh = navMesh;
                GraphSize = graphSize;
                Generation = generation;
                ColliderCount = colliderCount;
                BoundsSize = boundsSize;
                CacheBytes = cacheBytes;
                HighDetail = highDetail;
                BaseGraphIdentity = baseGraphIdentity ?? string.Empty;
                Initialized = initialized;
            }
        }

        private sealed class SerializedNavMeshEntry
        {
            public readonly string MapName;
            public readonly bool HighDetail;
            public readonly RuntimeRainNavCacheRecord Record;

            public SerializedNavMeshEntry(string mapName, bool highDetail,
                RuntimeRainNavCacheRecord record)
            {
                MapName = mapName ?? string.Empty;
                HighDetail = highDetail;
                Record = record;
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct ProcessMemoryCountersEx
        {
            public uint Size;
            public uint PageFaultCount;
            public UIntPtr PeakWorkingSetSize;
            public UIntPtr WorkingSetSize;
            public UIntPtr QuotaPeakPagedPoolUsage;
            public UIntPtr QuotaPagedPoolUsage;
            public UIntPtr QuotaPeakNonPagedPoolUsage;
            public UIntPtr QuotaNonPagedPoolUsage;
            public UIntPtr PagefileUsage;
            public UIntPtr PeakPagefileUsage;
            public UIntPtr PrivateUsage;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MemoryBasicInformation
        {
            public IntPtr BaseAddress;
            public IntPtr AllocationBase;
            public uint AllocationProtect;
            public UIntPtr RegionSize;
            public uint State;
            public uint Protect;
            public uint Type;
        }

        private struct MemorySnapshot
        {
            public bool AddressSpaceValid;
            public long PrivateBytes;
            public long FreeAddressBytes;
            public long LargestFreeRegionBytes;
        }

        private const uint MemFree = 0x10000;

        [DllImport("kernel32.dll")]
        private static extern IntPtr GetCurrentProcess();

        [DllImport("psapi.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetProcessMemoryInfo(IntPtr process,
            out ProcessMemoryCountersEx counters, uint size);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern UIntPtr VirtualQuery(IntPtr address,
            out MemoryBasicInformation information, UIntPtr length);

        private static int TerrainMask
        {
            get
            {
                int mask = LayerMask.GetMask(new string[] { "Terrarin" });
                return mask == 0 ? 256 : mask;
            }
        }

        private static string ActiveGeneratorSignature
        {
            get
            {
                string signature = _highDetail ? BakeGeneratorSignature : RuntimeGeneratorSignature;
                return signature + "|base=" + (string.IsNullOrEmpty(_baseGraphIdentity) ? "unknown" : _baseGraphIdentity);
            }
        }

        private static float CurrentBuildTimeoutSeconds
        {
            get
            {
                return string.Equals(_mapName, ResidentMapName, StringComparison.OrdinalIgnoreCase)
                    ? ResidentBuildTimeoutSeconds : BuildTimeoutSeconds;
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
