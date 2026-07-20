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

    internal static class RuntimeRainNavMesh
    {
        private const float SceneSettleSeconds = 0.80f;
        private const float ColliderWaitSeconds = 20f;
        private const float BuildTimeoutSeconds = 75f;

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

        internal static void PrepareMap(string mapName, bool runtimeRequired)
        {
            ReleaseOwnedGraph("map_change");
            _generation++;
            _mapName = (mapName ?? string.Empty).Trim().ToLowerInvariant();
            _requested = runtimeRequired && !string.IsNullOrEmpty(_mapName);
            _state = _requested ? RuntimeRainNavState.WaitingScene : RuntimeRainNavState.Idle;
            _detail = _requested ? "waiting_scene" : "native_or_empty";
            _sceneReadyAt = 0f;
            _waitStartedAt = Time.realtimeSinceStartup;
            _buildStartedAt = 0f;
            _readyAt = 0f;
            _nextAttemptAt = 0f;
            _nextLogAt = 0f;
            FileLogger.Log("AUTO-BATTLE][NAVMESH", "runtime_prepare generation=" + _generation +
                " map=" + SafeMap(_mapName) + " requested=" + (_requested ? "1" : "0"));
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
            ReleaseOwnedGraph(reason);
            _requested = false;
            _state = RuntimeRainNavState.Idle;
            _mapName = string.Empty;
            _detail = "shutdown:" + SafeOneLine(reason, 48);
            _sceneReadyAt = 0f;
            _waitStartedAt = 0f;
            _buildStartedAt = 0f;
            _readyAt = 0f;
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

                _host = new GameObject("ASWDEBUG_RuntimeRainNav_" + SafeMap(_mapName));
                _host.hideFlags = HideFlags.HideAndDontSave;
                _host.transform.position = bounds.center;
                _host.transform.rotation = Quaternion.identity;
                _host.transform.localScale = size;

                _navMesh = new RainNavMesh();
                _navMesh.MountPoint = _host.transform;
                _navMesh.GraphName = "ASWDEBUG_RuntimeRainNav_" + SafeMap(_mapName) + "_" + _generation;
                _navMesh.Size = 1f;
                _navMesh.AutomaticGridSize = true;
                _navMesh.IncludedLayers = terrainMask;
                _navMesh.IgnoredTags = new List<string>();
                _navMesh.UnwalkableTags = new List<string>();
                _navMesh.MaxSlope = 50f;
                _navMesh.WalkableHeight = 1.80f;
                _navMesh.WalkableRadius = 0.45f;
                _navMesh.StepHeight = 0.85f;
                _navMesh.CellSize = 0.30f;
                _navMesh.MaxVertexError = 0.28f;
                _navMesh.MaxSegmentLength = 5f;
                _navMesh.StartCreatingContours(2);

                _state = RuntimeRainNavState.Building;
                _buildStartedAt = Time.realtimeSinceStartup;
                _detail = "building progress=0";
                _nextLogAt = 0f;
                FileLogger.Log("AUTO-BATTLE][NAVMESH", "runtime_build_started generation=" + _generation +
                    " map=" + SafeMap(_mapName) + " colliders=" + colliderCount +
                    " center=" + FormatVector(bounds.center) + " size=" + FormatVector(size) +
                    " cell=" + _navMesh.CellSize.ToString("0.00"));
            }
            catch (Exception ex)
            {
                Fail("build_start_ex:" + ex.GetType().Name + ":" + SafeOneLine(ex.Message, 96));
            }
        }

        private static bool ProbeRuntimeCapabilities(out string detail)
        {
            try
            {
                Type type = typeof(RainNavMesh);
                string[] methods =
                {
                    "CreateContours", "CancelCreatingContours", "RegisterNavigationGraph", "ClearNavigationGraph"
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

        private static void TickBuild()
        {
            if (_navMesh == null)
            {
                Fail("builder_missing");
                return;
            }

            float now = Time.realtimeSinceStartup;
            if (now - _buildStartedAt >= BuildTimeoutSeconds)
            {
                Fail("build_timeout progress=" + _navMesh.CreatingProgress.ToString("0.000"));
                return;
            }

            try
            {
                if (_navMesh.Creating) _navMesh.CreateContours();
                if (_navMesh.Creating)
                {
                    _detail = "building progress=" + _navMesh.CreatingProgress.ToString("0.000");
                    if (now >= _nextLogAt)
                    {
                        _nextLogAt = now + 1f;
                        FileLogger.Log("AUTO-BATTLE][NAVMESH", "runtime_building generation=" + _generation +
                            " map=" + SafeMap(_mapName) + " progress=" + _navMesh.CreatingProgress.ToString("0.000") +
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

                _navMesh.RegisterNavigationGraph();
                _registered = true;
                _state = RuntimeRainNavState.Ready;
                _readyAt = now;
                _detail = "ready nodes=" + graphSize;
                FileLogger.Log("AUTO-BATTLE][NAVMESH", "runtime_ready generation=" + _generation +
                    " map=" + SafeMap(_mapName) + " nodes=" + graphSize +
                    " build=" + (now - _buildStartedAt).ToString("0.0"));
            }
            catch (Exception ex)
            {
                Fail("build_tick_ex:" + ex.GetType().Name + ":" + SafeOneLine(ex.Message, 96));
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
            ReleaseOwnedGraph("failed:" + safeReason);
            _state = RuntimeRainNavState.Failed;
            _detail = safeReason;
            FileLogger.Log("AUTO-BATTLE][NAVMESH", "runtime_failed generation=" + _generation +
                " map=" + SafeMap(_mapName) + " reason=" + safeReason);
        }

        private static void ReleaseOwnedGraph(string reason)
        {
            RainNavMesh owned = _navMesh;
            _navMesh = null;
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
