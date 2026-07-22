using ASWDEBUG.Cheats.AutoBattle;
using ASWDEBUG.Cheats.SurvivalBot;
using ASWDEBUG.Logger;
using System;
using System.Collections.Generic;
using UnityEngine;

namespace ASWDEBUG.UI
{
    internal static class NavigationPathVisualizer
    {
        private const float RefreshInterval = 0.06f;
        private const float FloorOffset = 0.055f;
        private const float MaxFloorSnapDelta = 1.10f;
        private const int MaxRoutePoints = 64;
        private static readonly Color RouteStartColor = new Color(0.95f, 0.08f, 0.02f, 0.86f);
        private static readonly Color RouteEndColor = new Color(1f, 0.72f, 0.08f, 1f);
        private static readonly Color WaypointColor = new Color(1f, 0.28f, 0.04f, 0.92f);
        private static readonly Color NextPointColor = new Color(1f, 0.82f, 0.12f, 1f);
        private static readonly Color DestinationColor = new Color(0.30f, 1f, 0.32f, 1f);
        private static readonly List<Vector3> SourceRoute = new List<Vector3>(MaxRoutePoints);
        private static readonly List<Vector3> GroundRoute = new List<Vector3>(MaxRoutePoints + 1);

        private static GameObject _lineHost;
        private static LineRenderer _line;
        private static GameObject _markerHost;
        private static MeshRenderer _markerRenderer;
        private static Mesh _markerMesh;
        private static Material _material;
        private static Texture2D _texture;
        private static Vector3[] _markerVertices;
        private static Color[] _markerColors;
        private static int[] _markerTriangles;
        private static float _nextRefreshAt;
        private static float _nextErrorLogAt;
        private static float _nextResourceAttemptAt;
        private static bool _shaderFailureLogged;
        private static bool _shaderReadyLogged;

        internal static int VisiblePointCount { get; private set; }

        internal static void Tick(Level level, Character player)
        {
            bool active = SurvivalBotManager.Enabled;
#if SURVIVAL_INTERNAL_TOOLS
            active = active || SurvivalBotManager.CombatTestEnabled ||
                SurvivalBotManager.RoomTestEnabled || SurvivalBotManager.Level33TestEnabled;
#endif
            if (!active)
            {
                Hide();
                return;
            }
            if (level == null || player == null || player.transform == null || level.state != Level.State.kReady)
            {
                Hide();
                return;
            }

            float now = Time.realtimeSinceStartup;
            if (now < _nextRefreshAt) return;
            _nextRefreshAt = now + RefreshInterval;

            try
            {
                bool hasRoute = SurvivalCombatAdapter.CopyActiveRoute(SourceRoute);
#if SURVIVAL_INTERNAL_TOOLS
                if (SurvivalBotManager.CombatTestEnabled || SurvivalBotManager.RoomTestEnabled ||
                    SurvivalBotManager.Level33TestEnabled)
                    hasRoute = AutoBattleManager.CopyActiveRoute(SourceRoute);
#endif
                if (!hasRoute || SourceRoute.Count == 0 || !EnsureResources())
                {
                    Hide();
                    return;
                }

                BuildGroundRoute(player.transform.position);
                if (GroundRoute.Count < 2)
                {
                    Hide();
                    return;
                }
                UpdateLine();
                UpdateMarkers();
                VisiblePointCount = GroundRoute.Count - 1;
            }
            catch (Exception ex)
            {
                Hide();
                if (now < _nextErrorLogAt) return;
                _nextErrorLogAt = now + 3f;
                FileLogger.Log("AUTO-BATTLE][PATH-VIS", "render_ex=" + ex.GetType().Name + ":" +
                    SafeOneLine(ex.Message, 100));
            }
        }

        internal static void Shutdown()
        {
            Hide();
            if (_lineHost != null) UnityEngine.Object.Destroy(_lineHost);
            if (_markerHost != null) UnityEngine.Object.Destroy(_markerHost);
            if (_markerMesh != null) UnityEngine.Object.Destroy(_markerMesh);
            if (_material != null) UnityEngine.Object.Destroy(_material);
            if (_texture != null) UnityEngine.Object.Destroy(_texture);
            _lineHost = null;
            _line = null;
            _markerHost = null;
            _markerRenderer = null;
            _markerMesh = null;
            _material = null;
            _texture = null;
            _markerVertices = null;
            _markerColors = null;
            _markerTriangles = null;
            _nextResourceAttemptAt = 0f;
            _shaderFailureLogged = false;
            _shaderReadyLogged = false;
            SourceRoute.Clear();
            GroundRoute.Clear();
        }

        private static bool EnsureResources()
        {
            if (_line != null && _markerRenderer != null && _markerMesh != null && _material != null) return true;

            float now = Time.realtimeSinceStartup;
            if (now < _nextResourceAttemptAt) return false;
            _nextResourceAttemptAt = now + 2f;

            string shaderSource;
            Shader shader = FindGuideShader(out shaderSource);
            if (shader == null)
            {
                if (!_shaderFailureLogged)
                {
                    _shaderFailureLogged = true;
                    FileLogger.Log("AUTO-BATTLE][PATH-VIS",
                        "waiting reason=shader_missing builtins_and_scene_materials");
                }
                return false;
            }

            _shaderFailureLogged = false;
            if (!_shaderReadyLogged)
            {
                _shaderReadyLogged = true;
                FileLogger.Log("AUTO-BATTLE][PATH-VIS", "ready shader=" + shader.name + " source=" + shaderSource);
            }

            _material = new Material(shader);
            _material.name = "ASWDEBUG_NavigationGuide_Material";
            _material.hideFlags = HideFlags.HideAndDontSave;
            _texture = new Texture2D(1, 1, TextureFormat.ARGB32, false);
            _texture.name = "ASWDEBUG_NavigationGuide_Texture";
            _texture.hideFlags = HideFlags.HideAndDontSave;
            _texture.SetPixel(0, 0, Color.white);
            _texture.Apply();
            if (_material.HasProperty("_MainTex")) _material.SetTexture("_MainTex", _texture);
            if (_material.HasProperty("_TintColor")) _material.SetColor("_TintColor", Color.white);
            if (_material.HasProperty("_Color")) _material.SetColor("_Color", Color.white);

            _lineHost = new GameObject("ASWDEBUG_NavigationGuide_Line");
            _lineHost.hideFlags = HideFlags.HideAndDontSave;
            UnityEngine.Object.DontDestroyOnLoad(_lineHost);
            _line = _lineHost.AddComponent<LineRenderer>();
            _line.useWorldSpace = true;
            _line.sharedMaterial = _material;
            _line.SetWidth(0.075f, 0.12f);
            _line.SetColors(RouteStartColor, RouteEndColor);

            _markerHost = new GameObject("ASWDEBUG_NavigationGuide_Points");
            _markerHost.hideFlags = HideFlags.HideAndDontSave;
            UnityEngine.Object.DontDestroyOnLoad(_markerHost);
            MeshFilter filter = _markerHost.AddComponent<MeshFilter>();
            _markerRenderer = _markerHost.AddComponent<MeshRenderer>();
            _markerMesh = new Mesh();
            _markerMesh.name = "ASWDEBUG_NavigationGuide_PointMesh";
            _markerMesh.hideFlags = HideFlags.HideAndDontSave;
            filter.sharedMesh = _markerMesh;
            _markerRenderer.sharedMaterial = _material;
            return true;
        }

        private static Shader FindGuideShader(out string source)
        {
            string[] preferredNames =
            {
                "Legacy Shaders/Particles/Additive",
                "Mobile/Particles/Additive",
                "Particles/Additive",
                "Particles/Alpha Blended",
                "Sprites/Default",
                "Unlit/Color",
                "Unlit/Transparent",
                "UI/Default"
            };
            for (int i = 0; i < preferredNames.Length; i++)
            {
                Shader shader = Shader.Find(preferredNames[i]);
                if (shader == null) continue;
                source = "builtin:" + preferredNames[i];
                return shader;
            }

            Shader fallback = null;
            UnityEngine.Object[] renderers = UnityEngine.Object.FindObjectsOfType(typeof(Renderer));
            for (int i = 0; i < renderers.Length; i++)
            {
                Renderer renderer = renderers[i] as Renderer;
                if (renderer == null) continue;
                Material[] materials = renderer.sharedMaterials;
                if (materials == null) continue;
                for (int j = 0; j < materials.Length; j++)
                {
                    Material material = materials[j];
                    if (material == null || material.shader == null) continue;
                    Shader candidate = material.shader;
                    if (fallback == null && material.HasProperty("_Color")) fallback = candidate;

                    string name = candidate.name ?? string.Empty;
                    if (name.IndexOf("Particle", StringComparison.OrdinalIgnoreCase) < 0 &&
                        name.IndexOf("Additive", StringComparison.OrdinalIgnoreCase) < 0 &&
                        name.IndexOf("Unlit", StringComparison.OrdinalIgnoreCase) < 0 &&
                        name.IndexOf("Transparent", StringComparison.OrdinalIgnoreCase) < 0 &&
                        name.IndexOf("Sprite", StringComparison.OrdinalIgnoreCase) < 0) continue;
                    source = "scene_material:" + SafeOneLine(material.name, 60);
                    return candidate;
                }
            }

            if (fallback != null)
            {
                source = "scene_color_fallback";
                return fallback;
            }
            source = "none";
            return null;
        }

        private static void BuildGroundRoute(Vector3 playerPosition)
        {
            GroundRoute.Clear();
            AddGroundPoint(playerPosition);
            int count = Mathf.Min(SourceRoute.Count, MaxRoutePoints);
            for (int i = 0; i < count; i++) AddGroundPoint(SourceRoute[i]);
        }

        private static void AddGroundPoint(Vector3 point)
        {
            if (!IsFinite(point)) return;
            Vector3 grounded = SnapToFloor(point);
            if (GroundRoute.Count > 0)
            {
                Vector3 previous = GroundRoute[GroundRoute.Count - 1];
                Vector3 delta = grounded - previous;
                if (delta.x * delta.x + delta.z * delta.z < 0.01f && Mathf.Abs(delta.y) < 0.15f) return;
            }
            GroundRoute.Add(grounded);
        }

        private static Vector3 SnapToFloor(Vector3 point)
        {
            Vector3 origin = point + Vector3.up * 1.25f;
            RaycastHit[] hits = Physics.RaycastAll(origin, Vector3.down, 3.25f, TerrainMask);
            float bestDelta = MaxFloorSnapDelta;
            Vector3 best = point;
            bool found = false;
            for (int i = 0; hits != null && i < hits.Length; i++)
            {
                RaycastHit hit = hits[i];
                if (hit.collider == null || hit.collider.isTrigger || hit.normal.y < 0.35f) continue;
                float delta = Mathf.Abs(hit.point.y - point.y);
                if (delta > bestDelta) continue;
                bestDelta = delta;
                best = hit.point;
                found = true;
            }
            if (found) return best + Vector3.up * FloorOffset;
            point.y += FloorOffset;
            return point;
        }

        private static void UpdateLine()
        {
            _line.enabled = true;
            _line.SetVertexCount(GroundRoute.Count);
            for (int i = 0; i < GroundRoute.Count; i++) _line.SetPosition(i, GroundRoute[i]);
        }

        private static void UpdateMarkers()
        {
            int count = GroundRoute.Count;
            int vertexCount = count * 4;
            int triangleCount = count * 6;
            if (_markerVertices == null || _markerVertices.Length != vertexCount)
            {
                _markerVertices = new Vector3[vertexCount];
                _markerColors = new Color[vertexCount];
                _markerTriangles = new int[triangleCount];
            }

            for (int i = 0; i < count; i++)
            {
                float radius = i == 0 ? 0.10f : i == 1 ? 0.19f : i == count - 1 ? 0.23f : 0.13f;
                Color color = i == 0 ? RouteStartColor : i == 1 ? NextPointColor :
                    i == count - 1 ? DestinationColor : WaypointColor;
                Vector3 center = GroundRoute[i] + Vector3.up * 0.008f;
                int vertex = i * 4;
                _markerVertices[vertex] = center + new Vector3(-radius, 0f, -radius);
                _markerVertices[vertex + 1] = center + new Vector3(-radius, 0f, radius);
                _markerVertices[vertex + 2] = center + new Vector3(radius, 0f, radius);
                _markerVertices[vertex + 3] = center + new Vector3(radius, 0f, -radius);
                _markerColors[vertex] = color;
                _markerColors[vertex + 1] = color;
                _markerColors[vertex + 2] = color;
                _markerColors[vertex + 3] = color;

                int triangle = i * 6;
                _markerTriangles[triangle] = vertex;
                _markerTriangles[triangle + 1] = vertex + 1;
                _markerTriangles[triangle + 2] = vertex + 2;
                _markerTriangles[triangle + 3] = vertex;
                _markerTriangles[triangle + 4] = vertex + 2;
                _markerTriangles[triangle + 5] = vertex + 3;
            }

            _markerMesh.Clear();
            _markerMesh.vertices = _markerVertices;
            _markerMesh.colors = _markerColors;
            _markerMesh.triangles = _markerTriangles;
            _markerMesh.RecalculateBounds();
            _markerRenderer.enabled = true;
        }

        private static void Hide()
        {
            VisiblePointCount = 0;
            if (_line != null) _line.enabled = false;
            if (_markerRenderer != null) _markerRenderer.enabled = false;
        }

        private static int TerrainMask
        {
            get
            {
                int mask = LayerMask.GetMask(new string[] { "Terrarin" });
                return mask == 0 ? 256 : mask;
            }
        }

        private static bool IsFinite(Vector3 value)
        {
            return !float.IsNaN(value.x) && !float.IsInfinity(value.x) &&
                   !float.IsNaN(value.y) && !float.IsInfinity(value.y) &&
                   !float.IsNaN(value.z) && !float.IsInfinity(value.z);
        }

        private static string SafeOneLine(string value, int maxLength)
        {
            if (string.IsNullOrEmpty(value)) return "-";
            string safe = value.Replace('\r', ' ').Replace('\n', ' ').Trim();
            return safe.Length <= maxLength ? safe : safe.Substring(0, maxLength);
        }
    }
}
