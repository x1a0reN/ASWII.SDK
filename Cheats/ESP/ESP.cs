using ASWDEBUG.Global;
using ASWDEBUG.Logger;
using ASWDEBUG.Cheats.LocalBot;
using ASWDEBUG.Main;
using ASWDEBUG.UI;
using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace ASWDEBUG.Cheats.ESP
{
    public class ESP
    {
        // ============ 总开关/绘制项 ============
        public static bool Enabled = false;

        public static bool D3BoxEsp = false;
        public static bool CrossEsp;
        public static bool CircleEsp;
        public static bool LineEsp;
        public static bool InfoEsp = false;

        // ============ 可调参数（性能/表现） ============
        // —— 优先顺滑：头/盒子对近距离每帧重算；远距离才降频，并对几何做插值平滑 —— //
        private const float HEAD_CACHE_INTERVAL_NEAR = 0.000f; // 近距离：每帧
        private const float HEAD_CACHE_INTERVAL_MID = 0.0167f; // 中距离：~60Hz
        private const float HEAD_CACHE_INTERVAL_FAR = 0.033f;  // 远距离：~30Hz

        private const float BOX_UPDATE_INTERVAL_NEAR = 0.000f; // 近距离：每帧
        private const float BOX_UPDATE_INTERVAL_MID = 0.0167f; // 中距离：~60Hz
        private const float BOX_UPDATE_INTERVAL_FAR = 0.033f;  // 远距离：~30Hz

        private const float WEAPON_CACHE_INTERVAL = 0.50f;
        private const float RELATION_CACHE_TTL = 5.00f;
        private const float RAYCAST_CACHE_INTERVAL = 0.10f;
        private const float TEXT_FIT_CACHE_TTL = 0.50f;

        // 几何插值平滑（用于盒子角点/减少降频跳变）
        private const float BOX_SMOOTH_SPEED = 12f;   // 越大收敛越快（推荐 8~16）

        // 距离阈值（超过则不绘制/降级）
        private const float NEAR_DIST = 20f;
        private const float MID_DIST = 50f;

        private const float MAX_SKELETON_DISTANCE = 1000f;
        private const float MAX_INFO_DISTANCE = 1000f;
        private const float MAX_LINE_DISTANCE = 1000f;

        // 圆/准心
        public static float CircleRadius = 188f;

        // 3D 盒子参数
        public static Vector3 BoxHalfSize = new Vector3(0.4f, 0.9f, 0.3f);
        public static Color BoxColor = Color.green;
        public static Color BoxColorOccluded = Color.red; // ✅ 被墙挡时的颜色
        public static Color BoxColorHidden = Color.gray;      // 隐身
        public static float BoxLineWidth = 1f;

        // 骨骼颜色
        public static Color SkeletonColorVisible = Color.green;
        public static Color SkeletonColorOccluded = Color.red;
        public static Color SkeletonColorHidden = Color.gray; // 隐身

        // 线
        public static Color LineColor = Color.red;
        public static float LineWidth = 1f;

        // 头部估算
        private static readonly string HeadBoneName = "web__head";
        private static readonly string HeadColName = "headCollider";
        private static readonly string EarLeftName = "EarLeftBone";
        private static readonly string EarRightName = "EarRightBone";
        public static float HeadHeightByWidth = 1.30f;
        public static float HeadUiLift = 0.04f;

        // 骨骼
        private static readonly string[] BoneNames = new string[] {
            "web__head", "web__chest",
            "web__arm_l","web__elbow_l","web__forearm_l","web__wrist_l","web__hand_l","web__finger2_c_l","web__thumb_c_l","web__handweapon_l",
            "web__arm_r","web__elbow_r","web__forearm_r","web__wrist_r","web__hand_r","web__finger2_c_r","web__thumb_c_r","web__handweapon_r",
            "web__hip","web__knee_l","web__ankle_l","web__foot_l","web__toe_l","web__knee_r","web__ankle_r","web__foot_r","web__toe_r"
        };
        private static readonly int[] BoneEdges = new int[] {
            0,1,
            1,2, 2,3, 3,4, 4,5, 5,6, 6,7, 6,8, 6,9,
            1,10,10,11,11,12,12,13,13,14,14,15,14,16,14,17,
            1,18, 18,19,19,20,20,21,21,22,
            18,23,23,24,24,25,25,26
        };

        // 动态盒子调参
        public static bool UseDynamicBounds = true;
        public static bool SnapBottomToGround = true;
        public static float GroundCheckDistance = 3f;
        public static float GroundYOffset = 0.02f;
        public static float BoundPaddingXZ = 0.05f;
        public static float BoundPaddingYTop = 0.08f;
        public static float BoundPaddingYBottom = 0.04f;
        public static float HeadTopExtraPad = 0.02f;
        public static float HeadBottomExtraPad = 0.00f;

        // 没有渲染器时的兜底
        public static Vector3 FallbackHalfSize = new Vector3(0.35f, 0.9f, 0.3f);

        // ===== 信息卡片视觉参数 =====
        public static int InfoTitleFont = 14;
        public static int InfoSubFont = 12;
        public static Color InfoBgColor = new Color(0f, 0f, 0f, 0.75f);
        public static Color InfoBorder = new Color(1f, 1f, 1f, 0.18f);
        public static Color NameEnemy = new Color(1f, 0.35f, 0.35f, 1f);
        public static Color NameNeutral = Color.white;
        public static Color HpBgColor = new Color(0.15f, 0.15f, 0.15f, 0.9f);
        public static Color HpFillColor = new Color(0.2f, 0.85f, 0.2f, 1f);
        public static Color ShieldFill = new Color(1f, 0.95f, 0.2f, 1f);
        public static Color ChipBg = new Color(1f, 1f, 1f, 0.08f);
        public static Color ChipText = new Color(1f, 1f, 1f, 0.85f);
        public static Color ChipWarnBg = new Color(1f, 0.3f, 0.2f, 0.25f);
        public static Color ChipWarnText = new Color(1f, 0.5f, 0.4f, 1f);
        public static Color ChipDllBg = new Color(0.16f, 0.45f, 1f, 0.35f);
        public static Color ChipDllText = new Color(0.65f, 0.9f, 1f, 1f);

        public static float InfoMaxWidth = 220f;
        public static float BarHeight = 6f;
        public static float BarSpacing = 3f;
        public static float PanelPadding = 6f;
        public static float ChipHeight = 16f;
        public static float ChipGap = 4f;
        public static float PanelMinWidth = 120f;

        // 卡片缩放
        public static float CardWorldGapMeters = 0.12f;
        public static float RefPixelsPerMeter = 80f;
        public static float MinScale = 0.6f;
        public static float MaxScale = 2.0f;

        // “参考像素”基准
        public static int BaseTitleFont = 14;
        public static int BaseSubFont = 12;
        public static float BasePanelPadding = 6f;
        public static float BaseBarHeight = 6f;
        public static float BaseBarSpacing = 3f;
        public static float BaseChipHeight = 16f;
        public static float BaseChipGap = 4f;
        public static float BaseBorderWidth = 1f;
        public static float BaseMinWidth = 120f;
        public static float BaseMaxWidth = 220f;

        // ============ 复用缓冲 ============
        private static readonly List<Transform> _boneBuf = new List<Transform>(27);
        private static readonly List<Vector3> _pointBuf = new List<Vector3>(64);
        private static readonly Vector3[] _corners = new Vector3[8];
        private static readonly List<Chip> _chipBuf = new List<Chip>(8);
        private static readonly GUIContent _gc = new GUIContent();
        private static readonly StringBuilder _sb = new StringBuilder(128);

        // ============ 缓存结构 ============
        private struct HeadCache
        {
            public Vector3 headCenter, headUp, headTopWorld;
            public float headHeight, headRadius, lastUpdate;
            public Vector3 anchorWorld;
            public float ppm;
            public bool valid;
        }

        private struct WeaponCache
        {
            public string nameRaw;
            public byte quality;
            public int plus;
            public string keyStr;
            public int keyInt;
            public float lastUpdate;
        }

        private struct TextFitCache
        {
            public string src;
            public string fitted;
            public float innerW;
            public int fontSize;
            public float time;
        }

        private struct RaycastGroundCache
        {
            public float lastTime;
            public float minY;
            public bool has;
        }

        private struct RelationCache
        {
            public int mask; // bit0: friend, bit1: recent, bit2: blacklist
            public float time;
        }

        // ✅ 新增：视线（是否被遮挡）缓存
        private struct OcclusionCache
        {
            public float lastTime;
            public bool has;
            public bool visible;
        }

        private struct PerCharCache
        {
            public HeadCache head;
            public WeaponCache weapon;
            public TextFitCache titleFit, weaponFit;
            public RaycastGroundCache ground;
            public float lastDist;
            public bool visible;

            public Transform[] bones;
            public bool bonesInit;

            // 盒子几何缓存 + 平滑缓冲
            public Vector3[] boxCorners;          // 原始角点
            public Vector3[] boxCornersSmoothed;  // 插值后角点
            public float boxLastUpdate;
            public bool boxValid;

            public Vector3 fixedLocalCenter;   // 在 avatar.root 局部坐标系中的中心
            public Vector3 fixedLocalExtents;  // 在 avatar.root 局部坐标系中的半径
            public int fixedRootId;            // avatar.root 的 InstanceID（变更时失效）
            public bool fixedValid;            // 是否已经计算过

            // ✅ 新增：LOS
            public OcclusionCache occlusion;
        }

        private static readonly Dictionary<int, PerCharCache> _cache = new Dictionary<int, PerCharCache>(128);
        private static readonly Dictionary<string, string> _weaponNameByKey = new Dictionary<string, string>(256);
        private static readonly Dictionary<int, string> _weaponNameById = new Dictionary<int, string>(256);
        private static readonly Dictionary<ulong, RelationCache> _relationCache = new Dictionary<ulong, RelationCache>(256);

        // ===== 信息卡片内部类型 =====
        private struct Chip
        {
            public string text;
            public Color bg;
            public Color fg;
            public Chip(string t, Color b, Color f) { text = t; bg = b; fg = f; }
        }

        // ===== GUIStyle 复用 =====
        private static GUIStyle _titleStyle;
        private static GUIStyle _subStyle;
        private static int _titleFontLast = -1, _subFontLast = -1;
        private static GUIStyle TitleStyle(int size)
        {
            if (_titleStyle == null) _titleStyle = (UIHelper.StringStyle ?? new GUIStyle(GUI.skin.label));
            if (_titleFontLast != size) { _titleStyle.fontSize = size; _titleFontLast = size; }
            return _titleStyle;
        }
        private static GUIStyle SubStyle(int size)
        {
            if (_subStyle == null) _subStyle = (UIHelper.StringStyle ?? new GUIStyle(GUI.skin.label));
            if (_subFontLast != size) { _subStyle.fontSize = size; _subFontLast = size; }
            return _subStyle;
        }

        // 对外
        public static void Enable()
        {
            if (CheatMain.CameraMain != null) { Actors(); }
        }
        public static void Disable()
        {
            D3BoxEsp = CrossEsp = CircleEsp = LineEsp = InfoEsp = false;
        }
        public static void ToggleEnabled() { Enabled = !Enabled; }
        public static void ToggleD3BoxEsp() { D3BoxEsp = !D3BoxEsp; }
        public static void ToggleInfoEsp() { InfoEsp = !InfoEsp; }
        public static void ToggleCrossEsp() { CrossEsp = !CrossEsp; }
        public static void ToggleCircleEsp() { CircleEsp = !CircleEsp; }
        public static void ToggleLineEsp() { LineEsp = !LineEsp; }

        // ===========================================================
        // 主绘制（只在 Repaint 事件画，避免 IMGUI 多事件抖动）
        // ===========================================================
        private static void Actors()
        {
            if (!Enabled) return;

            if (Event.current != null && Event.current.type != EventType.Repaint) return;

            var cam = (CheatMain.CameraMain != null) ? CheatMain.CameraMain : Camera.main;
            if (cam == null) return;

            if (CrossEsp)
                UIHelper.DrawCrosshair(new Vector2(Screen.width * 0.5f, Screen.height * 0.5f), 10f, Color.red, 2f);
            if (CircleEsp)
                UIHelper.DrawCircle(new Vector2(Screen.width * 0.5f, Screen.height * 0.5f), CircleRadius, Color.white, 1f, 48);

            Character player = (Level.Instance != null) ? Level.Instance.GetPlayer() : null;
            if (player == null) return;

            DrawGMTopRightOverlay(player);

            var set = CharacterManager.Instance.character_set;
            if (set == null) return;

            float now = Time.time;
            float dt = Mathf.Max(0.0001f, Time.deltaTime);

            foreach (var character in set)
            {
                if (character == null) continue;
                if (character.IsDied) continue;
                if (character.avatar == null || character.avatar.root == null) continue;
                if (player.GetTeam() == character.GetTeam()) continue;

                Vector3 pos = character.transform.position;
                Vector3 sp = cam.WorldToScreenPoint(pos);
                if (sp.z <= 0f) continue;

                float dist = Vector3.Distance(player.transform.position, pos);

                // 1) LOS：从相机到目标是否有视线
                bool hasLOS = IsVisibleFromCamera_Cached(character, cam, now);

                // 2) 隐身：角色自身的隐身状态
                bool isHidden = false;
                try
                {
                    isHidden = character.GetHidden();
                }
                catch
                {
                    isHidden = false;
                }

                // 3) 是否被墙挡 = 没有 LOS
                bool occludedByWall = !hasLOS;

                // 连线
                if (LineEsp && dist <= MAX_LINE_DISTANCE)
                {
                    Vector3 world;
                    if (GetHeadAnchorForUI_Cached(character, cam, now, dist, out world))
                    {
                        Vector3 sps = cam.WorldToScreenPoint(world);
                        if (sps.z > 0f)
                        {
                            Vector2 headScreen = new Vector2(sps.x, Screen.height - sps.y);
                            Vector2 topCenter = new Vector2(Screen.width * 0.5f, 0f);
                            UIHelper.DrawLine(topCenter, headScreen, LineColor, LineWidth);
                        }
                    }
                }

                // 骨架 + 3D 盒
                if (D3BoxEsp && dist <= MAX_SKELETON_DISTANCE)
                {
                    // ✅ 这里把 hasLOS 传进去
                    DrawSkeletonAndBox_Smooth(character, cam, dist, now, dt, occludedByWall, isHidden);
                }

                // 信息卡片
                if (InfoEsp && dist <= MAX_INFO_DISTANCE)
                    DrawCharacterInfoCard_Smooth(character, player, cam, dist, now);
                else if (GetDllUserCardText(character) != null)
                    DrawDllUserTag(character, cam, dist, now);
            }
        }

        private static string GetDllUserCardText(Character c)
        {
            try
            {
                if (c == null || c.character_info == null) return null;

                string label = DllUsageTelemetry.GetVisibleCardLabel(c.character_info.character_id);
                return string.IsNullOrEmpty(label) ? null : "闲人" + label + "卡";
            }
            catch
            {
                return null;
            }
        }

        private static void DrawDllUserTag(Character c, Camera cam, float dist, float now)
        {
            Vector3 anchor;
            if (!GetHeadAnchorForUI_Cached(c, cam, now, dist, out anchor)) return;

            Vector3 sp = cam.WorldToScreenPoint(anchor);
            if (sp.z <= 0f) return;

            string text = GetDllUserCardText(c);
            if (string.IsNullOrEmpty(text)) return;

            const int font = 13;
            GUIStyle style = SubStyle(font);
            _gc.text = text;
            Vector2 sz = style.CalcSize(_gc);
            float w = sz.x + 12f;
            float h = sz.y + 4f;
            float x = sp.x - w * 0.5f;
            float y = Screen.height - sp.y - h - 20f;

            UIHelper.DrawBox(new Vector2(x, y), new Vector2(w, h), ChipDllBg, false);
            UIHelper.DrawString(new Vector2(x + w * 0.5f, y + h * 0.5f), text, ChipDllText, font, true);
        }

        // —— 新增：对固定盒子尺寸做合理化（避免被“扁平化”）——
        private static void EnsureFixedBoxDims(Character c, Transform root, ref Vector3 localCenter, ref Vector3 localExtents)
        {
            // 最小下限（按米）：适配你游戏的人形体量，自己可再调
            const float MIN_X = 0.25f;
            const float MIN_Y = 0.80f;
            const float MIN_Z = 0.25f;

            if (localExtents.x < MIN_X) localExtents.x = MIN_X;
            if (localExtents.z < MIN_Z) localExtents.z = MIN_Z;

            // 尝试用“头高”估计整体身高，修正 Y
            Vector3 hc, hup; float hh, hr;
            float estH = 0f;
            if (TryGetHeadBounds(c, out hc, out hh, out hr, out hup))
                estH = Mathf.Max(estH, hh * 1.8f); // 头高→身高的经验比例（可调）

            float curH = localExtents.y * 2f;
            float targetH = Mathf.Max(curH, estH, MIN_Y * 2f);
            localExtents.y = targetH * 0.5f;

            // 可选：把中心抬到半高，假定 root 在脚面附近
            if (localCenter.y < localExtents.y * 0.5f)
                localCenter.y = localExtents.y;
        }

        // ===========================================================
        // 骨架/3D盒子（骨架每帧画；盒子固定尺寸，只随位移/旋转/缩放）
        // ===========================================================
        private static void DrawSkeletonAndBox_Smooth(Character c, Camera cam, float dist, float now, float dt, bool hasLOS, bool isHidden)
        {
            var id = c.GetInstanceID();
            PerCharCache pc;
            if (!_cache.TryGetValue(id, out pc)) { pc = new PerCharCache(); }

            // ===== 颜色选择逻辑（优先级：隐身 > 墙挡 > 正常） =====
            Color boneCol;
            Color boxCol;

            if (isHidden)
            {
                boneCol = SkeletonColorHidden;   // 👻 隐身颜色
                boxCol = BoxColorHidden;
            }
            else if (hasLOS)
            {
                boneCol = SkeletonColorOccluded; // 🧱 被墙挡颜色
                boxCol = BoxColorOccluded;
            }
            else
            {
                boneCol = SkeletonColorVisible;  // ✅ 正常颜色
                boxCol = BoxColor;
            }

            // ------- 骨骼引用缓存：每帧画骨架线 -------
            if (!pc.bonesInit || pc.bones == null || pc.bones.Length != BoneNames.Length)
            {
                if (pc.bones == null) pc.bones = new Transform[BoneNames.Length];
                for (int i = 0; i < BoneNames.Length; i++)
                    pc.bones[i] = c.getBone(BoneNames[i]);
                pc.bonesInit = true;
                _cache[id] = pc;
            }

            for (int i = 0; i < BoneEdges.Length; i += 2)
            {
                int a = BoneEdges[i], b = BoneEdges[i + 1];
                Transform ta = (a >= 0 && a < pc.bones.Length) ? pc.bones[a] : null;
                Transform tb = (b >= 0 && b < pc.bones.Length) ? pc.bones[b] : null;
                if (ta != null && tb != null)
                {
                    // UIHelper 里增加 DrawBone(Transform,Transform,Color) 重载
                    UIHelper.DrawBone(ta, tb, boneCol);
                }
            }


            // ------- 固定盒子：只随 root 的位移/旋转/缩放 -------
            Transform root = (c.avatar != null && c.avatar.root != null) ? c.avatar.root.transform : c.transform;
            if (root == null) return;

            int rootId = root.GetInstanceID();
            if (!pc.fixedValid || pc.fixedRootId != rootId)
            {
                Vector3 lc, le;
                Transform usedRoot;
                if (GetStableModelBounds(c, out lc, out le, out usedRoot) && usedRoot != null)
                {
                    // 合理化，避免“扁”
                    EnsureFixedBoxDims(c, usedRoot, ref lc, ref le);

                    pc.fixedLocalCenter = lc;
                    pc.fixedLocalExtents = le;
                    pc.fixedRootId = usedRoot.GetInstanceID();
                    pc.fixedValid = true;
                }
                else
                {
                    // 兜底：由头部估计
                    Vector3 headC, headUp; float headH, headR;
                    if (TryGetHeadBounds(c, out headC, out headH, out headR, out headUp))
                    {
                        float H = Mathf.Max(0.6f, headH * 1.8f);
                        Vector3 ext = new Vector3(
                            Mathf.Max(0.30f, headR * 1.6f),
                            Mathf.Max(0.80f, H * 0.5f),
                            Mathf.Max(0.25f, headR * 1.4f));
                        Vector3 cen = new Vector3(0f, ext.y, 0f);
                        // 合理化（一般已满足）
                        EnsureFixedBoxDims(c, root, ref cen, ref ext);

                        pc.fixedLocalExtents = ext;
                        pc.fixedLocalCenter = cen;
                        pc.fixedRootId = rootId;
                        pc.fixedValid = true;
                    }
                    else
                    {
                        // 最终兜底
                        Vector3 ext = FallbackHalfSize;
                        Vector3 cen = new Vector3(0f, ext.y, 0f);
                        EnsureFixedBoxDims(c, root, ref cen, ref ext);

                        pc.fixedLocalExtents = ext;
                        pc.fixedLocalCenter = cen;
                        pc.fixedRootId = rootId;
                        pc.fixedValid = true;
                    }
                }
                _cache[id] = pc;
            }

            if (!pc.fixedValid) return;

            // 把固定盒子从 root 局部坐标变换到世界坐标
            Vector3 centerW = root.TransformPoint(pc.fixedLocalCenter);

            // 三个半轴（考虑非均匀缩放+旋转）
            Vector3 ax = root.TransformVector(new Vector3(pc.fixedLocalExtents.x, 0f, 0f));
            Vector3 ay = root.TransformVector(new Vector3(0f, pc.fixedLocalExtents.y, 0f));
            Vector3 az = root.TransformVector(new Vector3(0f, 0f, pc.fixedLocalExtents.z));

            // —— 关键兜底：若某轴被极端缩放压扁，改用 root 的方向向量 × 对应长度 —— //
            const float EPS = 1e-6f;
            if (ax.sqrMagnitude < EPS) ax = root.right.normalized * pc.fixedLocalExtents.x * Mathf.Max(0.0001f, Mathf.Abs(root.lossyScale.x));
            if (ay.sqrMagnitude < EPS) ay = root.up.normalized * pc.fixedLocalExtents.y * Mathf.Max(0.0001f, Mathf.Abs(root.lossyScale.y));
            if (az.sqrMagnitude < EPS) az = root.forward.normalized * pc.fixedLocalExtents.z * Mathf.Max(0.0001f, Mathf.Abs(root.lossyScale.z));

            if (pc.boxCorners == null || pc.boxCorners.Length != 8) pc.boxCorners = new Vector3[8];
            pc.boxCorners[0] = centerW + (-ax - ay - az);
            pc.boxCorners[1] = centerW + (ax - ay - az);
            pc.boxCorners[2] = centerW + (ax + ay - az);
            pc.boxCorners[3] = centerW + (-ax + ay - az);
            pc.boxCorners[4] = centerW + (-ax - ay + az);
            pc.boxCorners[5] = centerW + (ax - ay + az);
            pc.boxCorners[6] = centerW + (ax + ay + az);
            pc.boxCorners[7] = centerW + (-ax + ay + az);

            UIHelper.Draw3DBox(pc.boxCorners, cam, boxCol, BoxLineWidth);
        }


        // 从 avatar.root 子树里聚合“稳定的”渲染器 bounds（在 root 的局部坐标系里）
        private static bool GetStableModelBounds(Character c, out Vector3 localCenter, out Vector3 localExtents, out Transform root)
        {
            localCenter = Vector3.zero;
            localExtents = Vector3.zero;
            root = (c.avatar != null && c.avatar.root != null) ? c.avatar.root.transform : c.transform;
            if (root == null) return false;

            bool any = false;
            Vector3 min = Vector3.zero, max = Vector3.zero;

            // 1) SkinnedMeshRenderer：优先 localBounds
            var smrs = root.GetComponentsInChildren<SkinnedMeshRenderer>(true);
            for (int i = 0; i < smrs.Length; i++)
            {
                var smr = smrs[i];
                Bounds lb; bool ok = false;
                try { lb = smr.localBounds; ok = true; } catch { ok = false; lb = new Bounds(Vector3.zero, Vector3.zero); }
                if (!ok && smr.sharedMesh != null) { lb = smr.sharedMesh.bounds; ok = true; }
                if (!ok) continue;

                ExpandToRootLocalAABB(lb, smr.transform, root, ref any, ref min, ref max);
            }

            // 2) MeshRenderer + MeshFilter
            var mrs = root.GetComponentsInChildren<MeshRenderer>(true);
            for (int i = 0; i < mrs.Length; i++)
            {
                var mr = mrs[i];
                var mf = mr.GetComponent<MeshFilter>();
                if (mf == null || mf.sharedMesh == null) continue;
                Bounds lb = mf.sharedMesh.bounds;
                ExpandToRootLocalAABB(lb, mf.transform, root, ref any, ref min, ref max);
            }

            if (!any)
            {
                // 没有渲染器就失败，让上层走兜底
                return false;
            }

            localCenter = (min + max) * 0.5f;
            localExtents = (max - min) * 0.5f;
            return true;
        }

        // 把 child 的局部 bounds 8 角 -> 世界 -> root 本地，累计到 root 本地 AABB
        private static void ExpandToRootLocalAABB(Bounds lb, Transform child, Transform root,
                                                  ref bool any, ref Vector3 min, ref Vector3 max)
        {
            Vector3 c = lb.center, e = lb.extents;

            Vector3[] pts = new Vector3[8];
            pts[0] = c + new Vector3(-e.x, e.y, -e.z);
            pts[1] = c + new Vector3(e.x, e.y, -e.z);
            pts[2] = c + new Vector3(e.x, e.y, e.z);
            pts[3] = c + new Vector3(-e.x, e.y, e.z);
            pts[4] = c + new Vector3(-e.x, -e.y, -e.z);
            pts[5] = c + new Vector3(e.x, -e.y, -e.z);
            pts[6] = c + new Vector3(e.x, -e.y, e.z);
            pts[7] = c + new Vector3(-e.x, -e.y, e.z);

            for (int i = 0; i < 8; i++)
            {
                Vector3 w = child.TransformPoint(pts[i]);
                Vector3 rl = root.InverseTransformPoint(w);
                if (!any) { min = rl; max = rl; any = true; }
                else
                {
                    if (rl.x < min.x) min.x = rl.x; if (rl.x > max.x) max.x = rl.x;
                    if (rl.y < min.y) min.y = rl.y; if (rl.y > max.y) max.y = rl.y;
                    if (rl.z < min.z) min.z = rl.z; if (rl.z > max.z) max.z = rl.z;
                }
            }
        }


        // ===========================================================
        // 信息卡片（头部投影自适应频率，但每帧画 UI）
        // ===========================================================
        private static void DrawCharacterInfoCard_Smooth(Character c, Character player, Camera cam, float dist, float now)
        {
            Vector3 headTopWorld;
            if (!GetHeadTopWorld_Cached(c, now, dist, out headTopWorld)) return;

            PerCharCache pc;
            if (!_cache.TryGetValue(c.GetInstanceID(), out pc)) return;
            if (pc.head.ppm <= 0f) return;

            const float RefPPM = 100f;
            float s = pc.head.ppm / RefPPM;
            if (s < MinScale) s = MinScale;
            if (s > MaxScale) s = MaxScale;

            const int BaseTitle = 16;
            const int BaseSub = 12;
            const float BasePad = 8f;
            const float BaseBarH = 6f;
            const float BaseBarGap = 4f;
            const float BaseChipH = 18f;
            const float BaseChipGap = 6f;
            const float BaseBorder = 1.5f;

            int titleFont = Mathf.Max(10, Mathf.RoundToInt(BaseTitle * s));
            int subFont = Mathf.Max(8, Mathf.RoundToInt(BaseSub * s));
            float pad = BasePad * s;
            float barH = BaseBarH * s;
            float barGap = BaseBarGap * s;
            float chipH = BaseChipH * s;
            float chipGap = BaseChipGap * s;
            float borderW = Mathf.Max(1f, BaseBorder * s);

            _sb.Length = 0;
            string nameText = (c.character_info != null && !string.IsNullOrEmpty(c.character_info.name))
                                ? c.character_info.name : c.name;
            _sb.Append(nameText).Append("  [").Append((int)dist).Append("m]");
            string line1Raw = _sb.ToString();

            byte weaponQuality = 0; int plus = 0;
            string weaponNamePlain = GetWeaponNameCached(c, now, out weaponQuality, out plus);

            const float BaseMinW = 150f, BaseMaxW = 260f, BaseExtraW = 14f;
            float minW = BaseMinW * s, maxW = BaseMaxW * s, extraW = BaseExtraW * s;

            var tStyle = TitleStyle(titleFont);
            var sStyle = SubStyle(subFont);

            float innerW, panelW, sz1x;
            string line1Fitted = FitWithCacheTitle(c.GetInstanceID(), line1Raw, tStyle, minW, maxW, extraW, out innerW, out panelW, out sz1x, now);

            string plusStr = " [+" + plus + "]";
            _gc.text = plusStr;
            Vector2 szPlus = sStyle.CalcSize(_gc);

            float nameBudget = Mathf.Max(0f, innerW - szPlus.x);
            string weaponNameFitted = FitWithCacheWeapon(c.GetInstanceID(), weaponNamePlain, sStyle, nameBudget, now);

            _gc.text = weaponNameFitted;
            Vector2 szName = sStyle.CalcSize(_gc);
            float weaponLineW = szName.x + szPlus.x;
            float weaponLineH = Mathf.Max(szName.y, szPlus.y);

            int maxHp = (c.max_health > 0) ? c.max_health : ((c.character_info != null) ? c.character_info.max_health : 0);
            float hpPct = (maxHp > 0) ? Mathf.Clamp01((float)c.hp / (float)maxHp) : 0f;
            bool hasShieldStat = (c.max_shield > 0);
            float shPct = hasShieldStat ? Mathf.Clamp01((float)c.shield / (float)c.max_shield) : 0f;

            _chipBuf.Clear();
            WeaponBase weaponBase = c.mWeapon as WeaponBase;
            if (weaponBase != null && weaponBase.reloading) _chipBuf.Add(new Chip("换弹中", ChipWarnBg, ChipWarnText));
            if (c.invincible_time > 0f) _chipBuf.Add(new Chip("无敌", ChipBg, ChipText));
            if (weaponBase != null && weaponBase.shooting) _chipBuf.Add(new Chip("射击中", ChipBg, ChipText));
            if (c.character_info != null && !string.IsNullOrEmpty(c.character_info.guild_name))
                _chipBuf.Add(new Chip("公会:" + c.character_info.guild_name, ChipBg, ChipText));
            bool isHidden = false; try { isHidden = c.GetHidden(); } catch { }
            if (isHidden) _chipBuf.Add(new Chip("隐身", ChipBg, ChipText));
            if (LocalBotManager.Contains(c)) _chipBuf.Add(new Chip("本地测试Bot", ChipBg, ChipText));
            else if (c.IsRobot) _chipBuf.Add(new Chip("人机", ChipBg, ChipText));

            ulong pid = (c.character_info != null) ? c.character_info.character_id : 0UL;
            if (pid != 0UL)
            {
                string dllCardText = GetDllUserCardText(c);
                if (!string.IsNullOrEmpty(dllCardText)) _chipBuf.Add(new Chip(dllCardText, ChipDllBg, ChipDllText));
                int relMask = GetRelationMaskCached(pid);
                if ((relMask & 1) != 0) _chipBuf.Add(new Chip("好友", ChipBg, ChipText));
                if ((relMask & 2) != 0) _chipBuf.Add(new Chip("最近玩家", ChipBg, ChipText));
                if ((relMask & 4) != 0) _chipBuf.Add(new Chip("黑名单", ChipWarnBg, ChipWarnText));
            }

            _gc.text = line1Fitted; Vector2 sz1 = tStyle.CalcSize(_gc);
            int chipLines = 0;
            if (_chipBuf.Count > 0)
            {
                float lineWacc = 0f;
                for (int i = 0; i < _chipBuf.Count; i++)
                {
                    _gc.text = _chipBuf[i].text;
                    float cw = sStyle.CalcSize(_gc).x + 10f;
                    float needed = (lineWacc <= 0f) ? cw : (lineWacc + chipGap + cw);
                    if (needed > innerW) { chipLines++; lineWacc = cw; }
                    else { lineWacc = needed; if (chipLines == 0) chipLines = 1; }
                }
            }

            float h = pad + sz1.y + barGap + weaponLineH + pad;
            if (maxHp > 0) h += barGap + barH;
            if (hasShieldStat) h += barGap + barH;
            if (chipLines > 0) h += barGap + chipLines * chipH + (chipLines - 1) * barGap;

            Vector3 spTop = cam.WorldToScreenPoint(pc.head.headTopWorld);
            if (spTop.z <= 0f) return;
            const float PixelLiftAboveHead = 4f;
            float anchorX = spTop.x;
            float anchorY = (float)Screen.height - spTop.y - PixelLiftAboveHead;

            float x = anchorX - panelW * 0.5f;
            float y = anchorY - h;

            float tFade = Mathf.InverseLerp(30f, 60f, dist);
            float bgA = Mathf.Lerp(InfoBgColor.a, 0.25f, tFade);
            float bdA = Mathf.Lerp(InfoBorder.a, 0.30f, tFade);
            var bgCol = new Color(InfoBgColor.r, InfoBgColor.g, InfoBgColor.b, bgA);
            var bdCol = new Color(InfoBorder.r, InfoBorder.g, InfoBorder.b, bdA);

            UIHelper.DrawBox(new Vector2(x, y), new Vector2(panelW, h), bgCol, false);
            UIHelper.DrawLine(new Vector2(x, y), new Vector2(x + panelW, y), bdCol, borderW);
            UIHelper.DrawLine(new Vector2(x + panelW, y), new Vector2(x + panelW, y + h), bdCol, borderW);
            UIHelper.DrawLine(new Vector2(x + panelW, y + h), new Vector2(x, y + h), bdCol, borderW);
            UIHelper.DrawLine(new Vector2(x, y + h), new Vector2(x, y), bdCol, borderW);

            float cy = y + pad;

            bool enemy = (player != null && player.GetTeam() != c.GetTeam());
            Color nameCol = enemy ? NameEnemy : NameNeutral;
            UIHelper.DrawString(new Vector2(x + panelW * 0.5f, cy + sz1.y * 0.5f), line1Fitted, nameCol, titleFont, true);
            cy += sz1.y + barGap;

            Color nameColByQ = GetWeaponQualityColor(weaponQuality);
            Color plusCol = GetPlusLevelColor(plus);
            float leftBase = x + (panelW - weaponLineW) * 0.5f;

            UIHelper.DrawString(new Vector2(leftBase + szName.x * 0.5f, cy + weaponLineH * 0.5f),
                                weaponNameFitted, nameColByQ, subFont, true);
            UIHelper.DrawString(new Vector2(leftBase + szName.x + szPlus.x * 0.5f, cy + weaponLineH * 0.5f),
                                plusStr, plusCol, subFont, true);
            cy += weaponLineH + barGap;

            if (maxHp > 0)
            {
                DrawBar(new Rect(x + pad, cy, panelW - pad * 2f, barH), hpPct, HpBgColor, HpFillColor);
                cy += barH + barGap;
            }
            if (hasShieldStat)
            {
                DrawBar(new Rect(x + pad, cy, panelW - pad * 2f, barH), shPct, HpBgColor, ShieldFill);
                cy += barH + barGap;
            }

            if (_chipBuf.Count > 0)
            {
                float gx = x + pad, gy = cy, lineWacc = 0f;
                for (int i = 0; i < _chipBuf.Count; i++)
                {
                    _gc.text = _chipBuf[i].text;
                    float cw = sStyle.CalcSize(_gc).x + 10f;
                    float needed = (lineWacc <= 0f) ? cw : (lineWacc + chipGap + cw);
                    if (needed > (panelW - pad * 2f)) { gy += chipH + barGap; gx = x + pad; lineWacc = 0f; }

                    float w = cw;
                    UIHelper.DrawBox(new Vector2(gx, gy), new Vector2(w, chipH), _chipBuf[i].bg, false);
                    UIHelper.DrawString(new Vector2(gx + w * 0.5f, gy + chipH * 0.5f), _chipBuf[i].text, _chipBuf[i].fg, subFont, true);

                    lineWacc = (lineWacc <= 0f) ? cw : (lineWacc + chipGap + cw);
                    gx += w + chipGap;
                }
            }
        }

        // ===========================================================
        // 文本截断缓存
        // ===========================================================
        private static string FitWithCacheTitle(int id, string raw, GUIStyle style,
                                                float minW, float maxW, float extraW,
                                                out float innerW, out float panelW, out float sz1x, float now)
        {
            PerCharCache pc;
            if (!_cache.TryGetValue(id, out pc)) pc = new PerCharCache();

            TextFitCache tf = pc.titleFit;
            bool need = (tf.src != raw) || (tf.fontSize != style.fontSize) || (now - tf.time > TEXT_FIT_CACHE_TTL);

            if (need)
            {
                _gc.text = raw;
                Vector2 sz = style.CalcSize(_gc);
                sz1x = sz.x;

                float w = sz.x + extraW + PanelPadding * 2f;
                float hardMaxW = Screen.width * 0.92f;
                if (w < minW) w = minW;
                if (w > maxW) w = maxW;
                if (w > hardMaxW) w = hardMaxW;
                panelW = w;
                innerW = w - PanelPadding * 2f;

                string fitted = raw;
                if (sz.x > innerW)
                {
                    const string ell = "...";
                    _gc.text = ell;
                    float ellW = style.CalcSize(_gc).x;
                    float budget = Mathf.Max(0f, innerW - ellW);

                    int lo = 0, hi = raw.Length, ans = 0;
                    while (lo <= hi)
                    {
                        int mid = (lo + hi) >> 1;
                        _gc.text = raw.Substring(0, mid);
                        float wx = style.CalcSize(_gc).x;
                        if (wx <= budget) { ans = mid; lo = mid + 1; }
                        else hi = mid - 1;
                    }
                    fitted = (ans > 0) ? raw.Substring(0, ans) + ell : ell;
                }

                tf.src = raw; tf.fitted = fitted; tf.innerW = innerW; tf.fontSize = style.fontSize; tf.time = now;
                pc.titleFit = tf; _cache[id] = pc;
                return fitted;
            }
            else
            {
                innerW = tf.innerW;
                panelW = innerW + PanelPadding * 2f;
                sz1x = 0f;
                return tf.fitted;
            }
        }

        private static string FitWithCacheWeapon(int id, string raw, GUIStyle style, float nameBudget, float now)
        {
            PerCharCache pc;
            if (!_cache.TryGetValue(id, out pc)) pc = new PerCharCache();

            TextFitCache tf = pc.weaponFit;
            bool need = (tf.src != raw) || (tf.fontSize != style.fontSize) || !Mathf.Approximately(tf.innerW, nameBudget) || (now - tf.time > TEXT_FIT_CACHE_TTL);

            if (need)
            {
                _gc.text = raw;
                Vector2 sz = style.CalcSize(_gc);
                string fitted = raw;
                if (sz.x > nameBudget)
                {
                    const string ell = "...";
                    _gc.text = ell; float ellW = style.CalcSize(_gc).x;
                    float budget = Mathf.Max(0f, nameBudget - ellW);
                    int lo = 0, hi = raw.Length, ans = 0;
                    while (lo <= hi)
                    {
                        int mid = (lo + hi) >> 1;
                        _gc.text = raw.Substring(0, mid);
                        float wx = style.CalcSize(_gc).x;
                        if (wx <= budget) { ans = mid; lo = mid + 1; }
                        else hi = mid - 1;
                    }
                    fitted = (ans > 0) ? raw.Substring(0, ans) + ell : ell;
                }
                tf.src = raw; tf.fitted = fitted; tf.innerW = nameBudget; tf.fontSize = style.fontSize; tf.time = now;
                pc.weaponFit = tf; _cache[id] = pc;
                return fitted;
            }
            return tf.fitted;
        }

        // ===========================================================
        // 头部/锚点缓存（按距离自适应频率）
        // ===========================================================
        private static bool GetHeadTopWorld_Cached(Character c, float now, float dist, out Vector3 topWorld)
        {
            Vector3 up;
            return GetHeadTopWorld_Cached(c, now, dist, out topWorld, out up);
        }

        private static bool GetHeadTopWorld_Cached(Character c, float now, float dist, out Vector3 topWorld, out Vector3 up)
        {
            topWorld = Vector3.zero; up = Vector3.up;

            int id = c.GetInstanceID();
            PerCharCache pc;
            if (!_cache.TryGetValue(id, out pc)) pc = new PerCharCache();

            float interval =
                (dist < NEAR_DIST) ? HEAD_CACHE_INTERVAL_NEAR :
                (dist < MID_DIST) ? HEAD_CACHE_INTERVAL_MID :
                                     HEAD_CACHE_INTERVAL_FAR;

            if (!pc.head.valid || interval <= 0f || (now - pc.head.lastUpdate) > interval)
            {
                Vector3 center, hup; float hh, hr;
                if (!TryGetHeadBounds(c, out center, out hh, out hr, out hup)) return false;

                pc.head.headCenter = center;
                pc.head.headUp = hup;
                pc.head.headHeight = hh;
                pc.head.headRadius = hr;
                pc.head.headTopWorld = center + hup * (hh * 0.5f) + Vector3.up * HeadTopExtraPad;
                pc.head.anchorWorld = pc.head.headTopWorld + hup * HeadUiLift;
                pc.head.lastUpdate = now;
                pc.head.valid = true;

                var cam = (CheatMain.CameraMain != null) ? CheatMain.CameraMain : Camera.main;
                if (cam != null)
                {
                    Vector3 a = cam.WorldToScreenPoint(pc.head.headTopWorld);
                    if (a.z > 0f)
                    {
                        Vector3 b = cam.WorldToScreenPoint(pc.head.headTopWorld + cam.transform.up * 1f);
                        if (b.z > 0f) pc.head.ppm = (new Vector2(b.x - a.x, b.y - a.y)).magnitude;
                    }
                }
                _cache[id] = pc;
            }

            topWorld = pc.head.headTopWorld;
            up = pc.head.headUp;
            return true;
        }

        private static bool GetHeadAnchorForUI_Cached(Character c, Camera cam, float now, float dist, out Vector3 anchor)
        {
            anchor = Vector3.zero;
            int id = c.GetInstanceID();
            PerCharCache pc;
            if (!_cache.TryGetValue(id, out pc)) pc = new PerCharCache();

            float interval =
                (dist < NEAR_DIST) ? HEAD_CACHE_INTERVAL_NEAR :
                (dist < MID_DIST) ? HEAD_CACHE_INTERVAL_MID :
                                     HEAD_CACHE_INTERVAL_FAR;

            if (!pc.head.valid || interval <= 0f || (now - pc.head.lastUpdate) > interval)
            {
                Vector3 center, hup; float hh, hr;
                if (!TryGetHeadBounds(c, out center, out hh, out hr, out hup)) return false;

                pc.head.headCenter = center;
                pc.head.headUp = hup;
                pc.head.headHeight = hh;
                pc.head.headRadius = hr;
                pc.head.headTopWorld = center + hup * (hh * 0.5f) + Vector3.up * HeadTopExtraPad;
                pc.head.anchorWorld = pc.head.headTopWorld + hup * HeadUiLift;
                pc.head.lastUpdate = now;
                pc.head.valid = true;

                if (cam != null)
                {
                    Vector3 a = cam.WorldToScreenPoint(pc.head.headTopWorld);
                    if (a.z > 0f)
                    {
                        Vector3 b = cam.WorldToScreenPoint(pc.head.headTopWorld + cam.transform.up * 1f);
                        if (b.z > 0f) pc.head.ppm = (new Vector2(b.x - a.x, b.y - a.y)).magnitude;
                    }
                }
                _cache[id] = pc;
            }

            anchor = pc.head.anchorWorld;
            return true;
        }

        // ===========================================================
        // 视线 / 遮挡缓存（从当前相机看过去是否被墙挡）
        // ===========================================================
        private static bool IsVisibleFromCamera_Cached(Character c, Camera cam, float now)
        {
            if (c == null || cam == null) return true;

            int id = c.GetInstanceID();
            PerCharCache pc;
            if (!_cache.TryGetValue(id, out pc)) pc = new PerCharCache();

            // 10 帧一个射线（RAYCAST_CACHE_INTERVAL 已经有）
            if (pc.occlusion.has && (now - pc.occlusion.lastTime) <= RAYCAST_CACHE_INTERVAL)
                return pc.occlusion.visible;

            bool visible = true;

            try
            {
                // 优先用头部锚点
                Vector3 anchor;
                float dist = 20f;
                if (c.transform != null && cam.transform != null)
                    dist = Vector3.Distance(cam.transform.position, c.transform.position);

                if (!GetHeadAnchorForUI_Cached(c, cam, now, dist, out anchor))
                {
                    // 兜底：角色位置 + 一点高度
                    anchor = c.transform.position + Vector3.up * 1.2f;
                }

                Vector3 dir = anchor - cam.transform.position;
                float len = dir.magnitude;
                if (len <= 0.01f)
                {
                    visible = true;
                }
                else
                {
                    dir /= len;
                    Ray ray = new Ray(cam.transform.position, dir);
                    RaycastHit hit;

                    if (Physics.Raycast(ray, out hit, len + 0.5f))
                    {
                        Transform hitRoot = hit.transform != null ? hit.transform.root : null;
                        Transform charRoot = (c.avatar != null && c.avatar.root != null)
                            ? c.avatar.root.transform
                            : c.transform;

                        string hitRootName = (hitRoot != null) ? hitRoot.name : null;
                        string baseName = c.baseName;

                        // 和你 aimbot 里类似：打到自己 root 或 hit_collider 视为可见，否则被墙挡
                        if (hitRoot == charRoot ||
                            hitRootName == baseName ||
                            hitRootName == "hit_collider")
                        {
                            visible = true;
                        }
                        else
                        {
                            visible = false;
                        }
                    }
                    else
                    {
                        // 没碰到任何东西，就当可见
                        visible = true;
                    }
                }
            }
            catch
            {
                visible = true;
            }

            pc.occlusion.visible = visible;
            pc.occlusion.lastTime = now;
            pc.occlusion.has = true;
            _cache[id] = pc;

            return visible;
        }


        // ===========================================================
        // 武器名/品质/强化缓存
        // ===========================================================
        private static string GetWeaponNameCached(Character c, float now, out byte quality, out int plus)
        {
            quality = 0; plus = 0;
            int id = c.GetInstanceID();
            PerCharCache pc;
            if (!_cache.TryGetValue(id, out pc)) pc = new PerCharCache();

            string keyStr = null;
            int keyInt = 0;

            if (c.mWeapon != null && c.mWeapon.info != null)
            {
                try { keyStr = (string)c.mWeapon.info.display_name; } catch { keyStr = null; }
            }
            if (string.IsNullOrEmpty(keyStr)) keyInt = c.weapon_id;

            bool need = (pc.weapon.keyStr != keyStr) || (pc.weapon.keyInt != keyInt) || (now - pc.weapon.lastUpdate > WEAPON_CACHE_INTERVAL) || string.IsNullOrEmpty(pc.weapon.nameRaw);
            if (need)
            {
                string raw = "No Weapon";
                byte q = 0; int pl = 0;

                if (c.mWeapon != null && c.mWeapon.info != null)
                {
                    try
                    {
                        string disp = (string)c.mWeapon.info.display_name;
                        if (!string.IsNullOrEmpty(disp))
                        {
                            if (_weaponNameByKey.TryGetValue(disp, out var cached))
                                raw = cached;
                            else
                            {
                                raw = TableManager.Instance.GetLabelText(disp);
                                _weaponNameByKey[disp] = raw;
                            }
                        }
                        else raw = c.mWeapon.info.ToString();

                        q = (byte)c.mWeapon.info.quality;
                        pl = (int)c.mWeapon.info.plus_level;
                    }
                    catch { raw = "Weapon"; }
                }
                else if (keyInt != 0)
                {
                    if (_weaponNameById.TryGetValue(keyInt, out var cached))
                        raw = cached;
                    else
                    {
                        raw = "ID:" + keyInt;
                        _weaponNameById[keyInt] = raw;
                    }
                }

                pc.weapon.nameRaw = raw;
                pc.weapon.quality = q;
                pc.weapon.plus = pl;
                pc.weapon.keyStr = keyStr;
                pc.weapon.keyInt = keyInt;
                pc.weapon.lastUpdate = now;
                _cache[id] = pc;
            }

            quality = pc.weapon.quality; plus = pc.weapon.plus;
            return pc.weapon.nameRaw;
        }

        // ===========================================================
        // 关系缓存
        // ===========================================================
        private static int GetRelationMaskCached(ulong targetId)
        {
            RelationCache rc;
            if (_relationCache.TryGetValue(targetId, out rc))
            {
                if (Time.time - rc.time <= RELATION_CACHE_TTL) return rc.mask;
            }

            int mask = 0;
            try
            {
                if (IsInRelation(targetId, 1)) mask |= 1; // 好友
                if (IsInRelation(targetId, 2)) mask |= 2; // 最近
                if (IsInRelation(targetId, 3)) mask |= 4; // 黑名单
            }
            catch { }

            rc.mask = mask; rc.time = Time.time;
            _relationCache[targetId] = rc;
            return mask;
        }

        private static bool IsInRelation(ulong targetId, int channelId)
        {
            try
            {
                var app = GameApp.Instance;
                var conn = app?.chat_connection;
                var arr = conn?.m_FriendArray;
                if (arr == null) return false;

                for (int gi = 0; gi < arr.Count; gi++)
                {
                    var grp = arr[gi];
                    if (grp == null || grp.channel == null) continue;
                    if (grp.channel.channelID != (ulong)channelId) continue;
                    var players = grp.player_array;
                    if (players == null) continue;
                    for (int pi = 0; pi < players.Count; pi++)
                    {
                        var it = players[pi];
                        if (it != null && it.playerID == targetId) return true;
                    }
                }
            }
            catch { }
            return false;
        }

        // ===========================================================
        // 工具：HP 条
        // ===========================================================
        private static void DrawBar(Rect r, float pct, Color bg, Color fill)
        {
            if (pct < 0f) pct = 0f;
            if (pct > 1f) pct = 1f;

            UIHelper.DrawBox(new Vector2(r.x, r.y), new Vector2(r.width, r.height), bg, false);
            float fw = r.width * pct;
            if (fw > 0.5f) UIHelper.DrawBox(new Vector2(r.x, r.y), new Vector2(fw, r.height), fill, false);
        }

        // ===========================================================
        // GM 右上角提示
        // ===========================================================
        private static void DrawGMTopRightOverlay(Character localPlayer)
        {
            if (localPlayer == null || !localPlayer.Is_GM) return;

            const string gmText = "GM正在观看";
            int font = 18; float pad = 12f;

            GUIStyle s = UIHelper.StringStyle ?? new GUIStyle(GUI.skin.label);
            s.fontSize = font;
            _gc.text = gmText;
            Vector2 sz = s.CalcSize(_gc);

            float cx = Screen.width - pad - sz.x * 0.5f;
            float cy = pad + sz.y * 0.5f;

            UIHelper.DrawString(new Vector2(cx + 1, cy + 1), gmText, new Color(0, 0, 0, 0.6f), font, true);
            UIHelper.DrawString(new Vector2(cx, cy), gmText, new Color(1f, 0.85f, 0.2f, 1f), font, true);
        }

        // ===========================================================
        // 颜色工具
        // ===========================================================
        private static Color GetWeaponQualityColor(byte q)
        {
            switch (q)
            {
                case 1: return Color.white;
                case 2: return new Color(0.45f, 0.95f, 0.45f);
                case 3: return new Color(0.45f, 0.65f, 1.00f);
                case 4: return new Color(0.75f, 0.55f, 1.00f);
                case 5: return new Color(1.00f, 0.60f, 0.20f);
                default: return Color.white;
            }
        }
        private static Color GetPlusLevelColor(int plus)
        {
            if (plus <= 0) return Color.white;
            if (plus <= 9) return new Color(0.45f, 0.95f, 0.45f);
            if (plus <= 14) return new Color(0.45f, 0.65f, 1.00f);
            if (plus <= 19) return new Color(1.00f, 0.60f, 0.20f);
            return new Color(1.00f, 0.25f, 0.25f);
        }

        // ===========================================================
        // 头部/锚点推断
        // ===========================================================
        public static bool TryGetHeadBounds(Character c, out Vector3 center, out float height, out float radius, out Vector3 up)
        {
            center = Vector3.zero; height = 0f; radius = 0f; up = Vector3.up;
            if (c == null) return false;

            Transform hc = c.getBone(HeadColName);
            if (hc != null)
            {
                CapsuleCollider cap = hc.GetComponent<CapsuleCollider>();
                if (cap == null)
                {
                    CapsuleCollider[] caps = hc.GetComponentsInChildren<CapsuleCollider>(true);
                    if (caps != null && caps.Length > 0) cap = caps[0];
                }
                if (cap != null)
                {
                    int dir = cap.direction; // 0=X,1=Y,2=Z
                    Vector3 localAxis = (dir == 0) ? Vector3.right : ((dir == 1) ? Vector3.up : Vector3.forward);
                    up = hc.TransformDirection(localAxis).normalized;

                    Vector3 worldCenter = hc.TransformPoint(cap.center);
                    float axisScale = (dir == 0) ? Mathf.Abs(hc.lossyScale.x) : ((dir == 1) ? Mathf.Abs(hc.lossyScale.y) : Mathf.Abs(hc.lossyScale.z));
                    float rScaleA = (dir == 0) ? Mathf.Abs(hc.lossyScale.y) : Mathf.Abs(hc.lossyScale.x);
                    float rScaleB = (dir == 2) ? Mathf.Abs(hc.lossyScale.y) : Mathf.Abs(hc.lossyScale.z);
                    float rScale = Mathf.Max(rScaleA, rScaleB);

                    float worldHeight = cap.height * axisScale;
                    float worldRadius = cap.radius * rScale;

                    center = worldCenter;
                    height = Mathf.Max(worldHeight, worldRadius * 2f);
                    radius = worldRadius;
                    return true;
                }

                SphereCollider sph = hc.GetComponent<SphereCollider>();
                if (sph == null)
                {
                    SphereCollider[] sps = hc.GetComponentsInChildren<SphereCollider>(true);
                    if (sps != null && sps.Length > 0) sph = sps[0];
                }
                if (sph != null)
                {
                    Transform hb = c.getBone(HeadBoneName);
                    up = (hb != null) ? hb.transform.up.normalized : Vector3.up;

                    Vector3 worldCenter = hc.TransformPoint(sph.center);
                    float s = Mathf.Max(Mathf.Abs(hc.lossyScale.x), Mathf.Abs(hc.lossyScale.y));
                    s = Mathf.Max(s, Mathf.Abs(hc.lossyScale.z));

                    float worldRadius = sph.radius * s;
                    center = worldCenter;
                    height = worldRadius * 2f;
                    radius = worldRadius;
                    return true;
                }

                BoxCollider box = hc.GetComponent<BoxCollider>();
                if (box == null)
                {
                    BoxCollider[] bs = hc.GetComponentsInChildren<BoxCollider>(true);
                    if (bs != null && bs.Length > 0) box = bs[0];
                }
                if (box != null)
                {
                    Transform hb = c.getBone(HeadBoneName);
                    up = (hb != null) ? hb.transform.up.normalized : Vector3.up;

                    Vector3 worldCenter = hc.TransformPoint(box.center);
                    Vector3 size = new Vector3(
                        box.size.x * Mathf.Abs(hc.lossyScale.x),
                        box.size.y * Mathf.Abs(hc.lossyScale.y),
                        box.size.z * Mathf.Abs(hc.lossyScale.z));
                    float approxRadius = 0.5f * Mathf.Max(size.x, size.z);
                    center = worldCenter;
                    height = size.y;
                    radius = approxRadius;
                    return true;
                }
            }

            Transform earL = c.getBone(EarLeftName);
            Transform earR = c.getBone(EarRightName);
            Transform head = c.getBone(HeadBoneName);

            if (earL != null && earR != null)
            {
                Vector3 L = earL.position;
                Vector3 R = earR.position;
                float width = Vector3.Distance(L, R);
                float estHeight = width * HeadHeightByWidth;
                Vector3 mid = (L + R) * 0.5f;
                Vector3 upVec = (head != null) ? head.up.normalized : Vector3.up;

                center = mid + upVec * (estHeight * 0.40f);
                height = estHeight;
                radius = width * 0.5f;
                up = upVec;
                return true;
            }

            if (head != null)
            {
                up = head.up.normalized;
                center = head.position + up * 0.10f;
                height = 0.26f;
                radius = 0.10f;
                return true;
            }

            return false;
        }

        public static bool TryGetHeadTop(Character c, out Vector3 topWorld)
        {
            topWorld = Vector3.zero;
            Vector3 center, up; float h, r;
            if (!TryGetHeadBounds(c, out center, out h, out r, out up)) return false;
            topWorld = center + up * (h * 0.5f);
            return true;
        }

        public static bool GetHeadAnchorForUI(Character c, out Vector3 anchorWorld)
        {
            anchorWorld = Vector3.zero;
            Vector3 top;
            if (!TryGetHeadTop(c, out top)) return false;
            Transform head = c.getBone(HeadBoneName);
            Vector3 up = (head != null) ? head.up.normalized : Vector3.up;
            anchorWorld = top + up * HeadUiLift;
            return true;
        }
    }
}
