using ASWDEBUG.Global;
using ASWDEBUG.Logger;
using AimTracker = ASWDEBUG.Cheats.AimTrack.AimTrack;
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

        public static bool SkeletonEsp = false;
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
        public static Color BoxColor = new Color(0.31f, 0.96f, 0.73f, 1f);
        public static Color BoxColorOccluded = new Color(1.00f, 0.32f, 0.43f, 1f);
        public static Color BoxColorHidden = new Color(0.65f, 0.72f, 0.80f, 1f);
        public static float BoxLineWidth = 1f;

        // 骨骼颜色
        public static Color SkeletonColorVisible = new Color(0.31f, 0.96f, 0.73f, 1f);
        public static Color SkeletonColorOccluded = new Color(1.00f, 0.32f, 0.43f, 1f);
        public static Color SkeletonColorHidden = new Color(0.65f, 0.72f, 0.80f, 1f);

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
        private static readonly int[] InfoAnchorBoneIndices = new int[] {
            0,1,2,3,5,6,10,11,13,14,18,19,20,21,22,23,24,25,26
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

            public Transform[] bones;
            public bool bonesInit;

            public Vector3[] boxCorners;

            public float uprightBottom;
            public float uprightTop;
            public float uprightHalfWidth;
            public float uprightHalfDepth;
            public bool uprightBoxValid;

            // ✅ 新增：LOS
            public OcclusionCache occlusion;
        }

        private static readonly Dictionary<int, PerCharCache> _cache = new Dictionary<int, PerCharCache>(128);
        private static readonly Dictionary<string, string> _weaponNameByKey = new Dictionary<string, string>(256);
        private static readonly Dictionary<int, string> _weaponNameById = new Dictionary<int, string>(256);
        private static readonly Dictionary<ulong, RelationCache> _relationCache = new Dictionary<ulong, RelationCache>(256);
        private static readonly List<Rect> _placedInfoCards = new List<Rect>(32);

        private enum InfoCardMode
        {
            Near,
            Mid,
            Far
        }

        private struct InfoStatusLine
        {
            public string text;
            public Color color;

            public InfoStatusLine(string text, Color color)
            {
                this.text = text;
                this.color = color;
            }
        }

        private struct ScreenTargetBounds
        {
            public float minX, maxX, minY, maxY, centerX, centerY;
        }

        private struct EspDrawStyle
        {
            public Color skeletonColor;
            public Color boxColor;
            public float skeletonOutlineWidth;
            public float skeletonInnerWidth;
            public float boxOutlineWidth;
            public float boxInnerWidth;
            public float outlineAlpha;
            public float cornerRatio;
        }

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
        private static GUIStyle _infoTitleStyle;
        private static GUIStyle _infoBodyStyle;
        private static GUIStyle _infoMicroStyle;
        private static int _infoStyleKey = -1;
        private static readonly Color InfoAccent = new Color(0.30f, 0.96f, 0.74f, 1f);
        private static readonly Color InfoAccentHidden = new Color(0.60f, 0.69f, 0.75f, 1f);
        private static readonly Color InfoPrimary = new Color(0.96f, 0.99f, 1.00f, 1f);
        private static readonly Color InfoSecondary = new Color(0.73f, 0.82f, 0.85f, 1f);
        private static readonly Color InfoMuted = new Color(0.54f, 0.65f, 0.70f, 1f);
        private static readonly Color InfoShield = new Color(0.32f, 0.72f, 1.00f, 1f);
        private static readonly Color InfoWarning = new Color(1.00f, 0.70f, 0.34f, 1f);
        private static readonly Color InfoDanger = new Color(1.00f, 0.36f, 0.45f, 1f);
        private static readonly Color InfoBarTrack = new Color(0.05f, 0.09f, 0.12f, 0.98f);
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
            // The master switch suppresses rendering without destroying the user's
            // independently configured visual layers.
            _placedInfoCards.Clear();
        }
        public static void ToggleEnabled() { Enabled = !Enabled; }
        public static void ToggleSkeletonEsp() { SkeletonEsp = !SkeletonEsp; }
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
            bool drawTrackingFov = AimTracker.Enabled && AimTracker.DrawFovCircle;
            if (!Enabled && !drawTrackingFov) return;

            if (Event.current != null && Event.current.type != EventType.Repaint) return;

            var cam = (CheatMain.CameraMain != null) ? CheatMain.CameraMain : Camera.main;
            if (cam == null) return;

            if (drawTrackingFov)
                DrawTrackingFov();
            else if (CircleEsp)
                UIHelper.DrawCircle(
                    new Vector2(Screen.width * 0.5f, Screen.height * 0.5f),
                    CircleRadius,
                    Color.white,
                    1f,
                    48);

            if (!Enabled) return;

            if (CrossEsp)
                UIHelper.DrawCrosshair(new Vector2(Screen.width * 0.5f, Screen.height * 0.5f), 10f, Color.red, 2f);

            Character player = (Level.Instance != null) ? Level.Instance.GetPlayer() : null;
            if (player == null) return;

            DrawGMTopRightOverlay(player);

            var set = CharacterManager.Instance.character_set;
            if (set == null) return;

            float now = Time.time;
            float dt = Mathf.Max(0.0001f, Time.deltaTime);
            _placedInfoCards.Clear();

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

                // 骨骼与人物方框使用独立开关，但共享一次几何缓存。
                if ((SkeletonEsp || D3BoxEsp) && dist <= MAX_SKELETON_DISTANCE)
                {
                    DrawSkeletonAndBox_Smooth(character, cam, dist, now, dt, occludedByWall, isHidden);
                }

                // 信息卡片
                if (InfoEsp && dist <= MAX_INFO_DISTANCE)
                    DrawCharacterInfoCard_Smooth(character, player, cam, dist, now);
            }
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
        // 骨骼/人物方框：世界竖直方框避免动画倾斜，骨骼只保留主关节链。
        // ===========================================================
        private static void DrawSkeletonAndBox_Smooth(
            Character c,
            Camera cam,
            float dist,
            float now,
            float dt,
            bool occluded,
            bool isHidden)
        {
            int id = c.GetInstanceID();
            PerCharCache pc;
            if (!_cache.TryGetValue(id, out pc)) pc = new PerCharCache();
            EnsureEspBones(c, ref pc);

            EspDrawStyle style = ResolveEspDrawStyle(dist, occluded, isHidden);
            if (SkeletonEsp) DrawCleanSkeleton(c, cam, pc.bones, style);
            if (D3BoxEsp) DrawUprightCornerBox(c, cam, ref pc, style, dt);
            _cache[id] = pc;
        }

        private static void EnsureEspBones(Character c, ref PerCharCache pc)
        {
            if (pc.bonesInit && pc.bones != null && pc.bones.Length == BoneNames.Length) return;
            if (pc.bones == null || pc.bones.Length != BoneNames.Length)
                pc.bones = new Transform[BoneNames.Length];
            for (int i = 0; i < BoneNames.Length; i++)
            {
                try { pc.bones[i] = c.getBone(BoneNames[i]); }
                catch { pc.bones[i] = null; }
            }
            pc.bonesInit = true;
        }

        private static EspDrawStyle ResolveEspDrawStyle(float distance, bool occluded, bool hidden)
        {
            Color skeletonBase = hidden
                ? SkeletonColorHidden
                : occluded ? SkeletonColorOccluded : SkeletonColorVisible;
            Color boxBase = hidden
                ? BoxColorHidden
                : occluded ? BoxColorOccluded : BoxColor;
            float far = Mathf.Clamp01((distance - 12f) / 78f);

            EspDrawStyle style = new EspDrawStyle();
            style.skeletonColor = EspWithAlpha(skeletonBase, Mathf.Lerp(0.96f, 0.76f, far));
            style.boxColor = EspWithAlpha(boxBase, Mathf.Lerp(0.68f, 0.42f, far));
            style.skeletonOutlineWidth = Mathf.Lerp(1.72f, 1.12f, far);
            style.skeletonInnerWidth = Mathf.Lerp(0.90f, 0.62f, far);
            style.boxOutlineWidth = Mathf.Lerp(1.62f, 1.05f, far) * Mathf.Max(0.5f, BoxLineWidth);
            style.boxInnerWidth = Mathf.Lerp(0.82f, 0.54f, far) * Mathf.Max(0.5f, BoxLineWidth);
            style.outlineAlpha = Mathf.Lerp(0.50f, 0.26f, far);
            style.cornerRatio = Mathf.Lerp(0.29f, 0.22f, far);
            return style;
        }

        private static void DrawCleanSkeleton(
            Character c,
            Camera cam,
            Transform[] bones,
            EspDrawStyle style)
        {
            Vector3 headCenter, ignoredUp;
            float headHeight, headRadius;
            if (TryGetHeadBounds(c, out headCenter, out headHeight, out headRadius, out ignoredUp))
            {
                DrawSegmentedHeadRing(cam, headCenter, headHeight, headRadius, style);
                Transform chest = EspBone(bones, 1);
                if (chest != null)
                {
                    Vector3 neck = headCenter - Vector3.up * (headHeight * 0.43f);
                    DrawEspSkeletonLine(cam, neck, chest.position, style);
                }
            }
            else
            {
                DrawEspBone(cam, bones, 0, 1, style);
            }

            DrawEspBone(cam, bones, 1, 18, style);

            DrawEspBone(cam, bones, 1, 2, style);
            DrawEspBone(cam, bones, 2, 3, style);
            DrawEspBone(cam, bones, 3, EspFirstBoneIndex(bones, 5, 4), style);
            DrawEspBone(cam, bones, EspFirstBoneIndex(bones, 5, 4), 6, style);

            DrawEspBone(cam, bones, 1, 10, style);
            DrawEspBone(cam, bones, 10, 11, style);
            DrawEspBone(cam, bones, 11, EspFirstBoneIndex(bones, 13, 12), style);
            DrawEspBone(cam, bones, EspFirstBoneIndex(bones, 13, 12), 14, style);

            DrawEspBone(cam, bones, 18, 19, style);
            DrawEspBone(cam, bones, 19, 20, style);
            DrawEspBone(cam, bones, 20, EspFirstBoneIndex(bones, 22, 21), style);
            DrawEspBone(cam, bones, 18, 23, style);
            DrawEspBone(cam, bones, 23, 24, style);
            DrawEspBone(cam, bones, 24, EspFirstBoneIndex(bones, 26, 25), style);
        }

        private static void DrawUprightCornerBox(
            Character c,
            Camera cam,
            ref PerCharCache pc,
            EspDrawStyle style,
            float dt)
        {
            Vector3 headCenter, ignoredUp;
            float headHeight, headRadius;
            bool hasHead = TryGetHeadBounds(c, out headCenter, out headHeight, out headRadius, out ignoredUp);

            Vector3 origin = c.transform.position;
            float scale = SafeEspScale(c.transform);
            float rawBottom = origin.y;
            float fallbackHeight = 1.8f * scale;
            float rawTop = hasHead ? headCenter.y + headHeight * 0.55f : rawBottom + fallbackHeight;
            float rawHeight = rawTop - rawBottom;
            if (rawHeight < fallbackHeight * 0.55f || rawHeight > fallbackHeight * 2.4f)
            {
                rawTop = rawBottom + fallbackHeight;
                rawHeight = fallbackHeight;
            }

            float shoulderWidth = EspBoneDistance(pc.bones, 2, 10);
            float rawHalfWidth = shoulderWidth > 0.01f ? shoulderWidth * 0.58f : rawHeight * 0.225f;
            rawHalfWidth = Mathf.Clamp(rawHalfWidth, rawHeight * 0.18f, rawHeight * 0.32f);
            float rawHalfDepth = Mathf.Clamp(rawHalfWidth * 0.62f, rawHeight * 0.12f, rawHeight * 0.22f);

            float blend = 1f - Mathf.Exp(-14f * Mathf.Max(0.0001f, dt));
            if (!pc.uprightBoxValid)
            {
                pc.uprightBottom = rawBottom;
                pc.uprightTop = rawTop;
                pc.uprightHalfWidth = rawHalfWidth;
                pc.uprightHalfDepth = rawHalfDepth;
                pc.uprightBoxValid = true;
            }
            else
            {
                pc.uprightBottom = Mathf.Lerp(pc.uprightBottom, rawBottom, blend);
                pc.uprightTop = Mathf.Lerp(pc.uprightTop, rawTop, blend);
                pc.uprightHalfWidth = Mathf.Lerp(pc.uprightHalfWidth, rawHalfWidth, blend);
                pc.uprightHalfDepth = Mathf.Lerp(pc.uprightHalfDepth, rawHalfDepth, blend);
            }

            Vector3 forward = c.transform.forward;
            forward.y = 0f;
            if (forward.sqrMagnitude < 0.0001f) forward = Vector3.forward;
            forward.Normalize();
            Vector3 right = Vector3.Cross(Vector3.up, forward).normalized;

            float centerY = (pc.uprightBottom + pc.uprightTop) * 0.5f;
            float halfHeight = Mathf.Max(0.1f, (pc.uprightTop - pc.uprightBottom) * 0.5f);
            Vector3 center = new Vector3(origin.x, centerY, origin.z);
            Vector3 ax = right * pc.uprightHalfWidth;
            Vector3 ay = Vector3.up * halfHeight;
            Vector3 az = forward * pc.uprightHalfDepth;

            if (pc.boxCorners == null || pc.boxCorners.Length != 8) pc.boxCorners = new Vector3[8];
            pc.boxCorners[0] = center - ax - ay - az;
            pc.boxCorners[1] = center + ax - ay - az;
            pc.boxCorners[2] = center + ax + ay - az;
            pc.boxCorners[3] = center - ax + ay - az;
            pc.boxCorners[4] = center - ax - ay + az;
            pc.boxCorners[5] = center + ax - ay + az;
            pc.boxCorners[6] = center + ax + ay + az;
            pc.boxCorners[7] = center - ax + ay + az;

            bool positiveForwardIsNear = Vector3.Dot(cam.transform.position - center, forward) >= 0f;
            int nearStart = positiveForwardIsNear ? 4 : 0;
            int farStart = positiveForwardIsNear ? 0 : 4;
            DrawEspBoxFace(cam, pc.boxCorners, farStart, EspWithAlpha(style.boxColor, 0.42f), style);
            DrawEspCornerWorldLine(cam, pc.boxCorners[0], pc.boxCorners[4], EspWithAlpha(style.boxColor, 0.66f), style);
            DrawEspCornerWorldLine(cam, pc.boxCorners[1], pc.boxCorners[5], EspWithAlpha(style.boxColor, 0.66f), style);
            DrawEspCornerWorldLine(cam, pc.boxCorners[2], pc.boxCorners[6], EspWithAlpha(style.boxColor, 0.66f), style);
            DrawEspCornerWorldLine(cam, pc.boxCorners[3], pc.boxCorners[7], EspWithAlpha(style.boxColor, 0.66f), style);
            DrawEspBoxFace(cam, pc.boxCorners, nearStart, style.boxColor, style);
        }

        private static void DrawEspBoxFace(
            Camera cam,
            Vector3[] corners,
            int start,
            Color color,
            EspDrawStyle style)
        {
            DrawEspCornerWorldLine(cam, corners[start], corners[start + 1], color, style);
            DrawEspCornerWorldLine(cam, corners[start + 1], corners[start + 2], color, style);
            DrawEspCornerWorldLine(cam, corners[start + 2], corners[start + 3], color, style);
            DrawEspCornerWorldLine(cam, corners[start + 3], corners[start], color, style);
        }

        private static void DrawEspCornerWorldLine(
            Camera cam,
            Vector3 a,
            Vector3 b,
            Color color,
            EspDrawStyle style)
        {
            Vector3 fromA = Vector3.Lerp(a, b, style.cornerRatio);
            Vector3 fromB = Vector3.Lerp(b, a, style.cornerRatio);
            DrawStyledEspWorldLine(cam, a, fromA, color,
                style.boxOutlineWidth, style.boxInnerWidth, style.outlineAlpha);
            DrawStyledEspWorldLine(cam, b, fromB, color,
                style.boxOutlineWidth, style.boxInnerWidth, style.outlineAlpha);
        }

        private static void DrawSegmentedHeadRing(
            Camera cam,
            Vector3 center,
            float height,
            float radius,
            EspDrawStyle style)
        {
            Vector2 screenCenter, screenRight, screenTop;
            if (!ProjectEspWorld(cam, center, out screenCenter) ||
                !ProjectEspWorld(cam, center + cam.transform.right * radius, out screenRight) ||
                !ProjectEspWorld(cam, center + Vector3.up * (height * 0.5f), out screenTop)) return;

            float radiusX = Mathf.Clamp(Vector2.Distance(screenCenter, screenRight), 2.5f, 72f);
            float radiusY = Mathf.Clamp(Vector2.Distance(screenCenter, screenTop), 3.2f, 88f);
            for (int arc = 0; arc < 4; arc++)
            {
                float start = (arc * 90f + 14f) * Mathf.Deg2Rad;
                float span = 62f * Mathf.Deg2Rad;
                Vector2 previous = screenCenter + new Vector2(
                    Mathf.Cos(start) * radiusX,
                    Mathf.Sin(start) * radiusY);
                for (int i = 1; i <= 4; i++)
                {
                    float angle = start + span * i / 4f;
                    Vector2 current = screenCenter + new Vector2(
                        Mathf.Cos(angle) * radiusX,
                        Mathf.Sin(angle) * radiusY);
                    DrawStyledEspScreenLine(previous, current, style.skeletonColor,
                        style.skeletonOutlineWidth, style.skeletonInnerWidth, style.outlineAlpha);
                    previous = current;
                }
            }
        }

        private static void DrawTrackingFov()
        {
            Vector2 center = new Vector2(Screen.width * 0.5f, Screen.height * 0.5f);
            float configuredRadius = AimTracker.RadiusPixels;
            if (float.IsNaN(configuredRadius) || float.IsInfinity(configuredRadius))
                configuredRadius = 188f;
            float radius = Mathf.Clamp(configuredRadius, 24f, 1200f);
            Color accent = AimTracker.currentTarget == null
                ? new Color(0.27f, 0.79f, 0.76f, 0.72f)
                : new Color(0.96f, 0.72f, 0.31f, 0.92f);

            UIHelper.DrawCircle(center, radius, new Color(0f, 0f, 0f, 0.55f), 2.2f, 20);
            UIHelper.DrawCircle(center, radius, accent, 0.85f, 20);

            const float tick = 8f;
            UIHelper.DrawLine(
                new Vector2(center.x - radius - tick, center.y),
                new Vector2(center.x - radius + 2f, center.y),
                accent,
                1.2f);
            UIHelper.DrawLine(
                new Vector2(center.x + radius - 2f, center.y),
                new Vector2(center.x + radius + tick, center.y),
                accent,
                1.2f);
            UIHelper.DrawLine(
                new Vector2(center.x, center.y - radius - tick),
                new Vector2(center.x, center.y - radius + 2f),
                accent,
                1.2f);
            UIHelper.DrawLine(
                new Vector2(center.x, center.y + radius - 2f),
                new Vector2(center.x, center.y + radius + tick),
                accent,
                1.2f);
        }

        private static void DrawEspBone(
            Camera cam,
            Transform[] bones,
            int a,
            int b,
            EspDrawStyle style)
        {
            Transform ta = EspBone(bones, a);
            Transform tb = EspBone(bones, b);
            if (ta == null || tb == null) return;
            DrawEspSkeletonLine(cam, ta.position, tb.position, style);
        }

        private static void DrawEspSkeletonLine(Camera cam, Vector3 a, Vector3 b, EspDrawStyle style)
        {
            DrawStyledEspWorldLine(cam, a, b, style.skeletonColor,
                style.skeletonOutlineWidth, style.skeletonInnerWidth, style.outlineAlpha);
        }

        private static void DrawStyledEspWorldLine(
            Camera cam,
            Vector3 a,
            Vector3 b,
            Color color,
            float outlineWidth,
            float innerWidth,
            float outlineAlpha)
        {
            Vector2 screenA, screenB;
            if (!ProjectEspWorld(cam, a, out screenA) || !ProjectEspWorld(cam, b, out screenB)) return;
            if ((screenA - screenB).sqrMagnitude > Screen.width * Screen.width * 2.5f) return;
            DrawStyledEspScreenLine(screenA, screenB, color, outlineWidth, innerWidth, outlineAlpha);
        }

        private static void DrawStyledEspScreenLine(
            Vector2 a,
            Vector2 b,
            Color color,
            float outlineWidth,
            float innerWidth,
            float outlineAlpha)
        {
            Color outline = new Color(0.01f, 0.018f, 0.024f, Mathf.Min(outlineAlpha, color.a));
            UIHelper.DrawLine(a, b, outline, outlineWidth);
            UIHelper.DrawLine(a, b, color, innerWidth);
        }

        private static bool ProjectEspWorld(Camera cam, Vector3 world, out Vector2 screen)
        {
            screen = Vector2.zero;
            Vector3 projected = cam.WorldToScreenPoint(world);
            if (projected.z <= 0.03f || float.IsNaN(projected.x) || float.IsNaN(projected.y) ||
                float.IsInfinity(projected.x) || float.IsInfinity(projected.y)) return false;
            screen = new Vector2(projected.x, Screen.height - projected.y);
            float maxX = Screen.width * 3f;
            float maxY = Screen.height * 3f;
            return screen.x > -maxX && screen.x < maxX && screen.y > -maxY && screen.y < maxY;
        }

        private static Transform EspBone(Transform[] bones, int index)
        {
            return bones != null && index >= 0 && index < bones.Length ? bones[index] : null;
        }

        private static int EspFirstBoneIndex(Transform[] bones, int preferred, int fallback)
        {
            return EspBone(bones, preferred) != null ? preferred : fallback;
        }

        private static float EspBoneDistance(Transform[] bones, int a, int b)
        {
            Transform ta = EspBone(bones, a);
            Transform tb = EspBone(bones, b);
            return ta == null || tb == null ? 0f : Vector3.Distance(ta.position, tb.position);
        }

        private static float SafeEspScale(Transform transform)
        {
            if (transform == null) return 1f;
            Vector3 scale = transform.lossyScale;
            float value = Mathf.Max(Mathf.Abs(scale.x), Mathf.Max(Mathf.Abs(scale.y), Mathf.Abs(scale.z)));
            return value < 0.01f ? 1f : value;
        }

        private static Color EspWithAlpha(Color color, float multiplier)
        {
            return new Color(color.r, color.g, color.b, color.a * multiplier);
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
            ScreenTargetBounds target;
            if (!TryGetInfoTargetBounds(c, cam, out target)) return;
            if (target.centerX < -24f || target.centerX > Screen.width + 24f ||
                target.centerY < -40f || target.centerY > Screen.height + 40f) return;

            // Never shrink below 1:1 pixels. Fractional down-scaling was the main
            // cause of the unreadable bitmap text in the live preview.
            float uiScale = Mathf.Clamp(Screen.height / 960f, 1f, 1.14f);
            EnsureInfoCardStyles(uiScale);

            InfoCardMode mode = dist > 76f
                ? InfoCardMode.Far
                : dist > 34f ? InfoCardMode.Mid : InfoCardMode.Near;
            bool hidden = false;
            try { hidden = c.GetHidden(); } catch { }
            Color accent = hidden ? InfoAccentHidden : InfoAccent;
            InfoStatusLine status = mode == InfoCardMode.Far
                ? new InfoStatusLine(string.Empty, InfoSecondary)
                : BuildInfoStatus(c, mode);

            float width = (mode == InfoCardMode.Near ? 178f : mode == InfoCardMode.Mid ? 156f : 126f) * uiScale;
            float padX = (mode == InfoCardMode.Far ? 7f : 9f) * uiScale;
            float topPad = (mode == InfoCardMode.Far ? 4f : 5f) * uiScale;
            float bottomPad = topPad;
            float titleHeight = (_infoTitleStyle.fontSize + 3f) * uiScale;
            float bodyHeight = (_infoBodyStyle.fontSize + 2f) * uiScale;
            float microHeight = (_infoMicroStyle.fontSize + 2f) * uiScale;
            float gap = 2f * uiScale;
            bool hasShield = c.max_shield > 0;
            float barsHeight = (hasShield ? 7f : 4f) * uiScale;

            float height = topPad + titleHeight + gap;
            if (mode == InfoCardMode.Near) height += microHeight + gap;
            height += barsHeight;
            if (mode != InfoCardMode.Far)
            {
                height += 3f * uiScale + bodyHeight;
                if (mode == InfoCardMode.Near) height += gap + microHeight;
                if (!string.IsNullOrEmpty(status.text)) height += gap + microHeight;
            }
            height += bottomPad;

            bool panelOnRight;
            Rect panel = PlaceInfoCard(target, width, height, 10f * uiScale, out panelOnRight);
            DrawInfoCardChrome(panel, target, panelOnRight, accent, mode, uiScale);

            float contentX = panel.x + padX + (panelOnRight ? 2f * uiScale : 0f);
            float contentRight = panel.xMax - padX - (panelOnRight ? 0f : 2f * uiScale);
            float contentWidth = Mathf.Max(28f, contentRight - contentX);
            float y = panel.y + topPad;

            string distanceText = Mathf.RoundToInt(dist) + "m";
            float distanceWidth = MeasureInfoText(distanceText, _infoMicroStyle).x + 5f * uiScale;
            string nameText = c.character_info != null && !string.IsNullOrEmpty(c.character_info.name)
                ? c.character_info.name
                : !string.IsNullOrEmpty(c.name) ? c.name : "UNKNOWN";
            nameText = FitInfoText(
                nameText,
                _infoTitleStyle,
                Mathf.Max(24f, contentWidth - distanceWidth - 5f * uiScale));
            DrawInfoText(
                new Rect(contentX, y, contentWidth - distanceWidth, titleHeight),
                nameText,
                _infoTitleStyle,
                InfoPrimary,
                TextAnchor.MiddleLeft);
            DrawInfoText(
                new Rect(contentRight - distanceWidth, y, distanceWidth, titleHeight),
                distanceText,
                _infoMicroStyle,
                InfoSecondary,
                TextAnchor.MiddleRight);
            y += titleHeight + gap;

            int maxHp = c.max_health > 0
                ? c.max_health
                : c.character_info != null ? c.character_info.max_health : 0;
            float hpPercent = maxHp > 0 ? Mathf.Clamp01((float)c.hp / maxHp) : 0f;
            float shieldPercent = hasShield ? Mathf.Clamp01((float)c.shield / c.max_shield) : 0f;
            Color hpColor = ResolveInfoHealthColor(hpPercent);

            if (mode == InfoCardMode.Near)
            {
                string hpText = maxHp > 0 ? "HP  " + c.hp + "/" + maxHp : "HP  --";
                DrawInfoText(
                    new Rect(contentX, y, contentWidth * 0.62f, microHeight),
                    hpText,
                    _infoMicroStyle,
                    InfoSecondary,
                    TextAnchor.MiddleLeft);
                if (hasShield)
                {
                    string shieldText = "SH  " + c.shield + "/" + c.max_shield;
                    DrawInfoText(
                        new Rect(contentX + contentWidth * 0.44f, y, contentWidth * 0.56f, microHeight),
                        shieldText,
                        _infoMicroStyle,
                        InfoShield,
                        TextAnchor.MiddleRight);
                }
                y += microHeight + gap;
            }

            DrawInfoVitals(contentX, y, contentWidth, hpPercent, shieldPercent, hasShield, hpColor, uiScale);
            y += barsHeight;
            if (mode == InfoCardMode.Far) return;

            byte quality;
            int plus;
            string weapon = GetWeaponNameCached(c, now, out quality, out plus);
            string weaponText = "WPN  " + weapon + (plus > 0 ? "  +" + plus : string.Empty);
            y += 3f * uiScale;
            DrawInfoText(
                new Rect(contentX, y, contentWidth, bodyHeight),
                FitInfoText(weaponText, _infoBodyStyle, contentWidth),
                _infoBodyStyle,
                ResolveInfoWeaponColor(quality),
                TextAnchor.MiddleLeft);
            y += bodyHeight;

            if (mode == InfoCardMode.Near)
            {
                y += gap;
                string meta = BuildInfoMeta(c);
                DrawInfoText(
                    new Rect(contentX, y, contentWidth, microHeight),
                    FitInfoText(meta, _infoMicroStyle, contentWidth),
                    _infoMicroStyle,
                    InfoMuted,
                    TextAnchor.MiddleLeft);
                y += microHeight;
            }

            if (!string.IsNullOrEmpty(status.text))
            {
                y += gap;
                float marker = Mathf.Max(3f, 3f * uiScale);
                UIHelper.DrawBox(
                    new Vector2(Mathf.Round(contentX), Mathf.Round(y + (microHeight - marker) * 0.5f)),
                    new Vector2(marker, marker),
                    status.color,
                    false);
                float textX = contentX + 8f * uiScale;
                DrawInfoText(
                    new Rect(textX, y, contentRight - textX, microHeight),
                    FitInfoText(status.text, _infoMicroStyle, contentRight - textX),
                    _infoMicroStyle,
                    status.color,
                    TextAnchor.MiddleLeft);
            }
        }

        private static void DrawCharacterInfoCard_Legacy(Character c, Character player, Camera cam, float dist, float now)
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

        private static InfoStatusLine BuildInfoStatus(Character c, InfoCardMode mode)
        {
            _sb.Length = 0;
            int count = 0;
            Color color = InfoSecondary;
            bool priorityColor = false;

            WeaponBase weapon = c.mWeapon as WeaponBase;
            if (weapon != null && weapon.reloading)
            {
                AppendInfoStatus("换弹", ref count);
                color = InfoWarning;
                priorityColor = true;
            }
            if (c.invincible_time > 0f)
            {
                AppendInfoStatus("无敌", ref count);
                color = InfoDanger;
                priorityColor = true;
            }

            bool hidden = false;
            try { hidden = c.GetHidden(); } catch { }
            if (hidden && count < 3)
            {
                AppendInfoStatus("隐身", ref count);
                color = InfoWarning;
                priorityColor = true;
            }
            if (weapon != null && weapon.shooting && count < 3)
            {
                AppendInfoStatus("开火", ref count);
                if (!priorityColor) color = InfoDanger;
            }

            if (count == 0 || mode == InfoCardMode.Near)
            {
                ulong pid = c.character_info != null ? c.character_info.character_id : 0UL;
                int relationMask = pid != 0UL ? GetRelationMaskCached(pid) : 0;
                if ((relationMask & 4) != 0 && count < 3)
                {
                    AppendInfoStatus("黑名单", ref count);
                    color = InfoDanger;
                    priorityColor = true;
                }
                else if ((relationMask & 1) != 0 && count < 3)
                {
                    AppendInfoStatus("好友", ref count);
                }
                else if ((relationMask & 2) != 0 && count < 3)
                {
                    AppendInfoStatus("最近玩家", ref count);
                }

                if (count < 3)
                {
                    if (LocalBotManager.Contains(c)) AppendInfoStatus("本地Bot", ref count);
                    else if (c.IsRobot) AppendInfoStatus("人机", ref count);
                }

                if (mode == InfoCardMode.Near && count < 3 && c.character_info != null &&
                    !string.IsNullOrEmpty(c.character_info.guild_name))
                {
                    AppendInfoStatus("公会 " + c.character_info.guild_name, ref count);
                }
            }

            return new InfoStatusLine(_sb.ToString(), color);
        }

        private static void AppendInfoStatus(string value, ref int count)
        {
            if (string.IsNullOrEmpty(value)) return;
            if (count > 0) _sb.Append("  /  ");
            _sb.Append(value);
            count++;
        }

        private static string BuildInfoMeta(Character c)
        {
            if (c.character_info == null) return string.Empty;
            _sb.Length = 0;
            if (c.character_info.character_level > 0)
                _sb.Append("LV ").Append(c.character_info.character_level);

            string career = InfoCareerLabel(c.character_info.career);
            if (!string.IsNullOrEmpty(career))
            {
                if (_sb.Length > 0) _sb.Append("  /  ");
                _sb.Append(career);
            }

            string rank = InfoRankLabel(c.character_info.rank_type, c.character_info.rank_level);
            if (!string.IsNullOrEmpty(rank))
            {
                if (_sb.Length > 0) _sb.Append("  /  ");
                _sb.Append(rank);
            }
            return _sb.ToString();
        }

        private static string InfoCareerLabel(CareerType career)
        {
            if (career == CareerType.kCareerSolider) return "护卫";
            if (career == CareerType.kCareerGunner) return "重装";
            if (career == CareerType.kCareerCommando) return "突击";
            return string.Empty;
        }

        private static string InfoRankLabel(int type, int level)
        {
            if (type <= 0 || level <= 0) return string.Empty;
            string tier = type == 1
                ? "铜"
                : type == 2 ? "银" : type == 3 ? "金" : type == 4 ? "钻" : "R" + type;
            return tier + " " + level.ToString("D2");
        }

        private static Color ResolveInfoHealthColor(float percent)
        {
            if (percent <= 0.25f) return InfoDanger;
            if (percent <= 0.55f) return InfoWarning;
            return InfoAccent;
        }

        private static Color ResolveInfoWeaponColor(byte quality)
        {
            if (quality == 2) return new Color(0.52f, 0.94f, 0.66f, 1f);
            if (quality == 3) return new Color(0.53f, 0.75f, 1.00f, 1f);
            if (quality == 4) return new Color(0.80f, 0.67f, 1.00f, 1f);
            if (quality >= 5) return new Color(1.00f, 0.76f, 0.42f, 1f);
            return new Color(0.80f, 0.87f, 0.89f, 1f);
        }

        private static bool TryGetInfoTargetBounds(Character c, Camera cam, out ScreenTargetBounds target)
        {
            target = new ScreenTargetBounds();
            int id = c.GetInstanceID();
            PerCharCache pc;
            if (!_cache.TryGetValue(id, out pc)) pc = new PerCharCache();
            EnsureEspBones(c, ref pc);
            _cache[id] = pc;

            bool any = false;
            float minX = float.MaxValue;
            float minY = float.MaxValue;
            float maxX = float.MinValue;
            float maxY = float.MinValue;
            for (int i = 0; i < InfoAnchorBoneIndices.Length; i++)
            {
                Transform bone = EspBone(pc.bones, InfoAnchorBoneIndices[i]);
                if (bone == null) continue;
                AddInfoProjected(cam, bone.position, ref any, ref minX, ref minY, ref maxX, ref maxY);
            }

            Vector3 headCenter, ignoredUp;
            float headHeight, headRadius;
            try
            {
                if (TryGetHeadBounds(c, out headCenter, out headHeight, out headRadius, out ignoredUp))
                {
                    AddInfoProjected(cam, headCenter + Vector3.up * (headHeight * 0.55f),
                        ref any, ref minX, ref minY, ref maxX, ref maxY);
                    AddInfoProjected(cam, headCenter + cam.transform.right * headRadius,
                        ref any, ref minX, ref minY, ref maxX, ref maxY);
                    AddInfoProjected(cam, headCenter - cam.transform.right * headRadius,
                        ref any, ref minX, ref minY, ref maxX, ref maxY);
                }
            }
            catch { }

            if (!any)
            {
                AddInfoProjected(cam, c.transform.position,
                    ref any, ref minX, ref minY, ref maxX, ref maxY);
                AddInfoProjected(cam, c.transform.position + Vector3.up * 1.8f,
                    ref any, ref minX, ref minY, ref maxX, ref maxY);
            }
            if (!any) return false;

            const float margin = 2f;
            target.minX = minX - margin;
            target.maxX = maxX + margin;
            target.minY = minY - margin;
            target.maxY = maxY + margin;
            target.centerX = (target.minX + target.maxX) * 0.5f;
            target.centerY = (target.minY + target.maxY) * 0.5f;
            return true;
        }

        private static void AddInfoProjected(
            Camera cam,
            Vector3 world,
            ref bool any,
            ref float minX,
            ref float minY,
            ref float maxX,
            ref float maxY)
        {
            Vector2 point;
            if (!ProjectEspWorld(cam, world, out point)) return;
            if (!any)
            {
                minX = maxX = point.x;
                minY = maxY = point.y;
                any = true;
                return;
            }
            if (point.x < minX) minX = point.x;
            if (point.x > maxX) maxX = point.x;
            if (point.y < minY) minY = point.y;
            if (point.y > maxY) maxY = point.y;
        }

        private static Rect PlaceInfoCard(
            ScreenTargetBounds target,
            float width,
            float height,
            float gap,
            out bool panelOnRight)
        {
            const float edge = 7f;
            width = Mathf.Round(width);
            height = Mathf.Round(height);
            bool preferRight = target.centerX >= Screen.width * 0.5f;
            float rightX = target.maxX + gap;
            float leftX = target.minX - gap - width;
            bool rightFits = rightX + width <= Screen.width - edge;
            bool leftFits = leftX >= edge;

            if (preferRight) panelOnRight = rightFits || !leftFits;
            else panelOnRight = !leftFits && rightFits;

            float x = panelOnRight ? rightX : leftX;
            x = Mathf.Round(Mathf.Clamp(x, edge, Mathf.Max(edge, Screen.width - width - edge)));
            float desiredY = Mathf.Round(Mathf.Clamp(
                target.minY,
                edge,
                Mathf.Max(edge, Screen.height - height - edge)));

            Rect best = new Rect(x, desiredY, width, height);
            if (InfoCardOverlaps(best))
            {
                const float step = 13f;
                for (int i = 1; i <= 10; i++)
                {
                    float offset = ((i + 1) / 2) * step * (i % 2 == 1 ? 1f : -1f);
                    float candidateY = Mathf.Round(Mathf.Clamp(
                        desiredY + offset,
                        edge,
                        Mathf.Max(edge, Screen.height - height - edge)));
                    Rect candidate = new Rect(x, candidateY, width, height);
                    if (!InfoCardOverlaps(candidate))
                    {
                        best = candidate;
                        break;
                    }
                }
            }
            _placedInfoCards.Add(best);
            return best;
        }

        private static bool InfoCardOverlaps(Rect candidate)
        {
            for (int i = 0; i < _placedInfoCards.Count; i++)
            {
                Rect other = _placedInfoCards[i];
                if (candidate.xMin < other.xMax + 3f && candidate.xMax + 3f > other.xMin &&
                    candidate.yMin < other.yMax + 3f && candidate.yMax + 3f > other.yMin)
                    return true;
            }
            return false;
        }

        private static void DrawInfoCardChrome(
            Rect panel,
            ScreenTargetBounds target,
            bool panelOnRight,
            Color accent,
            InfoCardMode mode,
            float scale)
        {
            float alpha = mode == InfoCardMode.Near ? 0.86f : mode == InfoCardMode.Mid ? 0.78f : 0.68f;
            Color surface = new Color(0.012f, 0.028f, 0.043f, alpha);
            if (mode != InfoCardMode.Far)
            {
                UIHelper.DrawBox(
                    new Vector2(panel.x + 1f, panel.y + 2f),
                    new Vector2(panel.width, panel.height),
                    new Color(0f, 0f, 0f, 0.36f),
                    false);
            }
            UIHelper.DrawBox(new Vector2(panel.x, panel.y), new Vector2(panel.width, panel.height), surface, false);

            float railX = panelOnRight ? panel.x : panel.xMax;
            UIHelper.DrawLine(
                new Vector2(railX, panel.y),
                new Vector2(railX, panel.yMax),
                new Color(accent.r, accent.g, accent.b, mode == InfoCardMode.Far ? 0.72f : 0.94f),
                mode == InfoCardMode.Far ? 1f : Mathf.Max(1.25f, 1.4f * scale));
            float tickLength = (mode == InfoCardMode.Far ? 14f : 22f) * scale;
            float tickEndX = panelOnRight ? railX + tickLength : railX - tickLength;
            UIHelper.DrawLine(
                new Vector2(railX, panel.y),
                new Vector2(tickEndX, panel.y),
                new Color(accent.r, accent.g, accent.b, 0.60f),
                1f);

            float leaderY = panel.y + (mode == InfoCardMode.Far ? 9f : 12f) * scale;
            float targetX = panelOnRight ? target.maxX + 1f : target.minX - 1f;
            float targetY = target.maxY - target.minY > 6f
                ? Mathf.Clamp(leaderY, target.minY + 3f, target.maxY - 3f)
                : target.centerY;
            Color leaderColor = new Color(accent.r, accent.g, accent.b, 0.48f);
            UIHelper.DrawLine(new Vector2(targetX, targetY), new Vector2(railX, leaderY), leaderColor, 1f);
            UIHelper.DrawBox(
                new Vector2(Mathf.Round(targetX - 1f), Mathf.Round(targetY - 1f)),
                new Vector2(3f, 3f),
                accent,
                false);
        }

        private static void DrawInfoVitals(
            float x,
            float y,
            float width,
            float hpPercent,
            float shieldPercent,
            bool hasShield,
            Color hpColor,
            float scale)
        {
            x = Mathf.Round(x);
            y = Mathf.Round(y);
            width = Mathf.Round(width);
            float hpHeight = Mathf.Max(4f, Mathf.Round(4f * scale));
            UIHelper.DrawBox(new Vector2(x, y), new Vector2(width, hpHeight), InfoBarTrack, false);
            float hpWidth = Mathf.Round(width * hpPercent);
            if (hpWidth > 0.5f)
                UIHelper.DrawBox(new Vector2(x, y), new Vector2(hpWidth, hpHeight), hpColor, false);

            if (!hasShield) return;
            float shieldY = Mathf.Round(y + 5f * scale);
            float shieldHeight = Mathf.Max(2f, Mathf.Round(2f * scale));
            UIHelper.DrawBox(new Vector2(x, shieldY), new Vector2(width, shieldHeight), InfoBarTrack, false);
            float shieldWidth = Mathf.Round(width * shieldPercent);
            if (shieldWidth > 0.5f)
                UIHelper.DrawBox(new Vector2(x, shieldY), new Vector2(shieldWidth, shieldHeight), InfoShield, false);
        }

        private static void EnsureInfoCardStyles(float scale)
        {
            int titleSize = Mathf.Max(15, Mathf.RoundToInt(15f * scale));
            int bodySize = Mathf.Max(13, Mathf.RoundToInt(13f * scale));
            int microSize = Mathf.Max(12, Mathf.RoundToInt(12f * scale));
            int key = titleSize * 10000 + bodySize * 100 + microSize;
            if (_infoTitleStyle != null && _infoStyleKey == key) return;

            _infoTitleStyle = CreateInfoCardStyle(titleSize, FontStyle.Bold);
            _infoBodyStyle = CreateInfoCardStyle(bodySize, FontStyle.Normal);
            _infoMicroStyle = CreateInfoCardStyle(microSize, FontStyle.Normal);
            _infoStyleKey = key;
        }

        private static GUIStyle CreateInfoCardStyle(int size, FontStyle fontStyle)
        {
            GUIStyle style = new GUIStyle(GUI.skin.label);
            style.fontSize = size;
            style.fontStyle = fontStyle;
            style.wordWrap = false;
            style.richText = false;
            style.clipping = TextClipping.Clip;
            style.padding = new RectOffset(0, 0, 0, 0);
            style.margin = new RectOffset(0, 0, 0, 0);
            style.contentOffset = Vector2.zero;
            style.normal = new GUIStyleState { background = null, textColor = Color.white };
            return style;
        }

        private static void DrawInfoText(
            Rect rect,
            string text,
            GUIStyle style,
            Color color,
            TextAnchor alignment)
        {
            if (string.IsNullOrEmpty(text)) return;
            rect = PixelAlignInfoRect(rect);
            style.alignment = alignment;
            style.normal.textColor = new Color(0f, 0f, 0f, 0.94f);
            GUI.Label(new Rect(rect.x + 1f, rect.y + 1f, rect.width, rect.height), text, style);
            style.normal.textColor = color;
            GUI.Label(rect, text, style);
        }

        private static Rect PixelAlignInfoRect(Rect rect)
        {
            return new Rect(
                Mathf.Round(rect.x),
                Mathf.Round(rect.y),
                Mathf.Round(rect.width),
                Mathf.Round(rect.height));
        }

        private static Vector2 MeasureInfoText(string text, GUIStyle style)
        {
            _gc.text = text ?? string.Empty;
            return style.CalcSize(_gc);
        }

        private static string FitInfoText(string text, GUIStyle style, float maxWidth)
        {
            if (string.IsNullOrEmpty(text)) return string.Empty;
            if (MeasureInfoText(text, style).x <= maxWidth) return text;

            const string suffix = "...";
            int low = 0;
            int high = text.Length;
            while (low < high)
            {
                int middle = (low + high + 1) / 2;
                string candidate = text.Substring(0, middle) + suffix;
                if (MeasureInfoText(candidate, style).x <= maxWidth) low = middle;
                else high = middle - 1;
            }
            return low <= 0 ? suffix : text.Substring(0, low) + suffix;
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
