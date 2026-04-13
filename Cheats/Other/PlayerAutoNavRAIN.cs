// PlayerAutoNavRAIN.cs
// 依赖：UnityEngine.dll, Assembly-CSharp.dll（含 Character/Level 等）
//      RAIN.Core.dll, RAIN.Navigation.dll
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Linq;
using UnityEngine;
using RAIN.Core;
using RAIN.Navigation;

[DisallowMultipleComponent]
public class PlayerAutoNavRAIN : MonoBehaviour
{
    [Header("开关")]
    public bool autoNavEnabled = true;
    public bool takeOverMovement = true;
    public KeyCode toggleKey = KeyCode.F8;

    [Header("寻路/移动")]
    public float repathInterval = 0.5f;     // 定期重算
    public float stopDistance = 2.6f;     // 外圈停
    public float cornerReachDist = 0.30f;    // 认为到达拐点的阈值
    public float moveSpeedOverride = 8.5f;     // >0 用此速度（m/s）
    public float speedMul = 1.25f;    // 覆盖失败时，用原始速度*倍数
    public float maxHeightOffset = 2.0f;     // 点在网格上的容差

    [Tooltip("RAIN 图的Tag，留空=全图；若你知道具体Tag，请填上避免跨图")]
    public List<string> rainGraphTags = new List<string>();

    [Header("视线/攻击")]
    public float attackDistance = 6f;
    public float losYOffsetSelf = 0.5f;
    public float losConeCos = -1.0f;    // -1=不限制；0.5≈60°
    public LayerMask losBlocker = 256;
    public float fireCooldown = 0.10f;

    [Header("攻击接口优先顺序")]
    public bool preferCharacterAttack = true;
    public string[] characterAttackMethodNames = { "AttackEnemy", "AttackTarget", "Attack", "FireAt", "ShootAt" };

    [Header("调试日志")]
    public bool verboseLog = true;

    private Character _ch;
    private CharacterController _cc;
    private AIRig _rig;

    private Character _target;
    private IList<Vector3> _corners; // 当前路径
    private int _cornerIdx;
    private float _nextRepath;
    private float _fireCD;

    // 卡点检测
    private float _lastDistToNext = float.MaxValue;
    private float _noProgressTime = 0f;
    public float stuckTimeout = 0.7f;   // 超过这段时间没靠近拐点=卡住，触发自救
    public float relaunchDelay = 0.25f;  // 自救后下一次尝试的延迟

    // 保存被禁用的原移动脚本
    private Behaviour _moveScript;
    private bool _moveScriptPrevEnabled;

    void Awake()
    {
        _ch = GetComponent<Character>();
        _cc = GetComponent<CharacterController>();
        _rig = GetComponent<AIRig>();

        _moveScript = (Behaviour)GetComponent("MoveScript");
        if (verboseLog) Log("AutoNav", $"Awake go='{gameObject.name}' ch='{(_ch ? _ch.name : "null")}' cc={(_cc ? "Y" : "N")} rig={(_rig ? "Y" : "N")}");
    }

    void OnEnable()
    {
        if (takeOverMovement && _moveScript != null)
        {
            _moveScriptPrevEnabled = _moveScript.enabled;
            _moveScript.enabled = !autoNavEnabled;
            if (verboseLog) Log("AutoNav", $"Disabled move script on '{gameObject.name}': {_moveScript.GetType().Name}");
        }
    }

    void OnDisable()
    {
        if (_moveScript != null)
        {
            _moveScript.enabled = _moveScriptPrevEnabled;
            if (verboseLog) Log("AutoNav", "OnDisable - restore move script");
        }
    }

    void Update()
    {
        if (Input.GetKeyDown(toggleKey))
        {
            autoNavEnabled = !autoNavEnabled;
            if (takeOverMovement && _moveScript != null)
                _moveScript.enabled = !autoNavEnabled;
            if (!autoNavEnabled)
            {
                SetMoveDir(Vector3.zero);
                if (verboseLog) Log("AutoNav", "toggled OFF");
            }
        }

        if (!autoNavEnabled || _ch == null || _ch.IsDied)
        {
            if (verboseLog && Time.frameCount % 30 == 0)
                Log("AutoNav", $"Tick: disabled={(autoNavEnabled ? 0 : 1)} chNull={(_ch ? 0 : 1)} died={(_ch != null && _ch.IsDied ? 1 : 0)}");
            return;
        }

        // 1) 选最近敌人
        if (_target == null || !IsValidEnemy(_target))
        {
            _target = FindNearestEnemy();
            if (verboseLog && _target != null)
                Log("AutoNav", $"Target -> {_target.name}");
        }
        if (_target == null) { StopMove(); return; }

        var selfPos = transform.position;
        var tgtPos = _target.transform.position;
        float dist = Vector3.Distance(selfPos, tgtPos);

        // 2) 攻击（先算 LOS）
        bool canShoot = dist <= attackDistance && HasLOS(tgtPos);
        TryAttack(canShoot, tgtPos);

        // 3) 移动：外圈停
        if (dist > stopDistance)
        {
            // 3.1 重新规划
            if (Time.time >= _nextRepath || _corners == null || _corners.Count == 0)
                RepathToOuterRing(tgtPos);

            // 3.2 沿 RAIN 路走
            MoveAlongPath();

            // 3.3 旋转策略：移动时朝“下一拐点”
            FaceMoveDir();
        }
        else
        {
            StopMove();

            // 只在停住且有 LOS 时面向敌人，避免对着墙角锁朝向
            if (canShoot) FacePointFlat(tgtPos);
        }

        if (verboseLog && Time.frameCount % 30 == 0)
        {
            bool inRange = dist <= attackDistance;
            Log("AutoNav", $"Tick: tgt={_target.name} dist={dist:F1} inRange={(inRange ? 1 : 0)} LOS={(canShoot ? 1 : 0)} cIdx={_cornerIdx}/{(_corners != null ? _corners.Count : 0)}");
        }
    }

    // -------- 目标选择 ----------
    private Character FindNearestEnemy()
    {
        Character best = null; float bestD2 = float.PositiveInfinity;

        var level = ASSingleton<Level>.Instance;
        if (level == null) return null;

        var list = level.GetCharacters();
        if (list != null)
        {
            for (int i = 0; i < list.Count; i++)
            {
                var c = list[i];
                if (!IsValidEnemy(c)) continue;
                float d2 = (c.transform.position - transform.position).sqrMagnitude;
                if (d2 < bestD2) { bestD2 = d2; best = c; }
            }
        }
        // 万一列表拿不到，遍历 CharacterManager 兜底
        if (best == null && CharacterManager.Instance != null)
        {
            foreach (var c in CharacterManager.Instance.character_set)
            {
                if (!IsValidEnemy(c)) continue;
                float d2 = (c.transform.position - transform.position).sqrMagnitude;
                if (d2 < bestD2) { bestD2 = d2; best = c; }
            }
        }
        return best;
    }

    private bool IsValidEnemy(Character c)
    {
        if (!c) return false;
        if (c == _ch) return false;
        if (c.GetTeam() == _ch.GetTeam()) return false;
        if (c.IsDied) return false;
        if (c.GetHidden()) return false;
        return true;
    }

    // -------- 寻路 ----------
    private void RepathToOuterRing(Vector3 targetPos)
    {
        _nextRepath = Time.time + repathInterval;

        // 目的地 = 敌人外圈 stopDistance
        Vector3 dir = targetPos - transform.position; dir.y = 0f;
        float d = dir.magnitude;
        Vector3 dest = (d > stopDistance) ? (targetPos - dir.normalized * stopDistance) : transform.position;

        // RAIN 图 tag（优先取 Navigator.GraphTags；否则用 public 字段）
        IList<string> tags = GetTagsFromRig(_rig) ?? (rainGraphTags.Count > 0 ? (IList<string>)rainGraphTags : null);

        // 检查终点是否在 NavMesh 上
        var onGraphs = NavigationManager.Instance.GraphForPoint(dest, maxHeightOffset, NavigationManager.GraphType.Navmesh, tags);
        if (onGraphs.Count == 0)
        {
            if (verboseLog) Log("AutoNav", "dest NOT on navmesh, fallback straight.");
            _corners = new List<Vector3> { dest }; // 退化直线（无避障）
            _cornerIdx = 0;
            _lastDistToNext = float.MaxValue;
            _noProgressTime = 0f;
            return;
        }

        // 走 RAIN 的路径
        if (!TryBuildPathViaRAIN(transform.position, dest, tags, out var pts))
        {
            if (verboseLog) Log("AutoNav", "TryBuildPathViaRAIN FAILED, fallback straight.");
            _corners = new List<Vector3> { dest };
        }
        else
        {
            if (verboseLog) Log("AutoNav", $"RAIN path ok, corners={pts.Count}");
            _corners = pts;
        }

        _cornerIdx = 0;
        _lastDistToNext = float.MaxValue;
        _noProgressTime = 0f;
    }
    private void MoveAlongPath()
    {
        if (_corners == null || _corners.Count == 0)
        {
            SetMoveDir(Vector3.zero);
            return;
        }

        _cornerIdx = Mathf.Clamp(_cornerIdx, 0, _corners.Count - 1);

        var pos = transform.position;
        var next = _corners[_cornerIdx];
        next.y = pos.y;

        // 够近就推进到下一个 corner
        if (Vector3.Distance(pos, next) < cornerReachDist)
        {
            _cornerIdx++;
            if (_cornerIdx >= _corners.Count)
            {
                SetMoveDir(Vector3.zero);
                return;
            }
            next = _corners[_cornerIdx];
            next.y = pos.y;
        }

        // 指向下一个 corner 的水平方向
        Vector3 toNext = next - pos;
        toNext.y = 0f;
        if (toNext.sqrMagnitude < 1e-6f)
        {
            SetMoveDir(Vector3.zero);
            return;
        }

        // —— 侧移微调（避免贴墙/卡角）——
        Vector3 right = new Vector3(toNext.z, 0f, -toNext.x).normalized;
        float bump = 0.5f;

        // 不能在同一个 || 表达式里用两个 out 变量，否则可能出现“未赋值”的分支
        Vector3 alt1, alt2;
        bool ok1 = TryOffsetOnNavmesh(pos, right * bump, out alt1);
        bool ok2 = false;
        if (!ok1)
            ok2 = TryOffsetOnNavmesh(pos, -right * bump, out alt2);
        else
            alt2 = Vector3.zero; // 保证已赋值（虽然不会被用到）

        if (ok1 || ok2)
        {
            Vector3 alt = ok1 ? alt1 : alt2;
            Vector3 stepDir = alt - pos;
            stepDir.y = 0f;

            if (stepDir.sqrMagnitude > 1e-6f)
            {
                // 先做一个小侧移，拉开与墙/角的贴靠
                MoveStep(stepDir.normalized);
                return;
            }
        }

        // 常规沿路径前进
        MoveStep(toNext.normalized);
    }


    private bool TryOffsetOnNavmesh(Vector3 src, Vector3 offset, out Vector3 result)
    {
        result = Vector3.zero;
        Vector3 test = src + offset;
        var graphs = NavigationManager.Instance.GraphForPoint(test, maxHeightOffset, NavigationManager.GraphType.Navmesh,
                         GetTagsFromRig(_rig) ?? (rainGraphTags.Count > 0 ? (IList<string>)rainGraphTags : null));
        if (graphs.Count > 0) { result = test; return true; }
        return false;
    }

    private void MoveStep(Vector3 dir)
    {
        if (dir.sqrMagnitude < 1e-6f) { SetMoveDir(Vector3.zero); return; }

        float speed = moveSpeedOverride > 0f ? moveSpeedOverride : 3.5f;

        try
        {
            if (_ch != null && _ch.motor1 != null)
                speed = (moveSpeedOverride > 0f) ? moveSpeedOverride : Mathf.Max(0.1f, _ch.motor1.move_speed * speedMul);
        }
        catch { }

        if (_cc != null) _cc.Move(dir * speed * Time.deltaTime);
        else transform.position += dir * speed * Time.deltaTime;

        SetMoveDir(dir);
    }

    private void StopMove() => SetMoveDir(Vector3.zero);

    private void SetMoveDir(Vector3 worldDir)
    {
        _ch.direction = (worldDir.sqrMagnitude > 0.0001f) ? Vector2.up : Vector2.zero;
    }

    private void FaceMoveDir()
    {
        if (_corners == null || _cornerIdx >= _corners.Count) return;
        var next = _corners[_cornerIdx];
        next.y = transform.position.y;
        var dir = next - transform.position;
        dir.y = 0f;
        if (dir.sqrMagnitude < 1e-6f) return;

        transform.rotation = Quaternion.LookRotation(dir);
        _ch.SetLookDir(transform.rotation.eulerAngles);
    }

    private void FacePointFlat(Vector3 point)
    {
        var look = point; look.y = transform.position.y;
        var dir = look - transform.position;
        dir.y = 0f;
        if (dir.sqrMagnitude < 1e-6f) return;

        transform.rotation = Quaternion.LookRotation(dir);
        _ch.SetLookDir(transform.rotation.eulerAngles);
    }

    // -------- 视线 / 攻击 ----------
    private bool HasLOS(Vector3 tpos)
    {
        Vector3 self = transform.position; self.y = Mathf.Max(1f, self.y) + losYOffsetSelf;
        Vector3 tgt = tpos; tgt.y = Mathf.Max(1f, tgt.y);

        var toT = (tgt - self);
        var dist = toT.magnitude;
        if (dist <= 0.01f) return true;

        var dir = toT / dist;
        if (Vector3.Dot(transform.forward, dir) < losConeCos) return false;
        return !Physics.Raycast(self, dir, dist, losBlocker);
    }

    private void TryAttack(bool canShoot, Vector3 tpos)
    {
        _fireCD -= Time.deltaTime;
        if (!canShoot || _fireCD > 0f) return;

        bool did = false;

        if (preferCharacterAttack)
        {
            var t = _ch.GetType();
            foreach (var name in characterAttackMethodNames)
            {
                var m = t.GetMethod(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (m != null)
                {
                    var ps = m.GetParameters();
                    if (ps.Length == 1 && ps[0].ParameterType == typeof(Vector3))
                    {
                        m.Invoke(_ch, new object[] { tpos });
                        did = true;
                        break;
                    }
                }
            }
        }

        if (!did && _ch.mWeapon != null)
        {
            _ch.mWeapon.RobotAttack(tpos);
            did = true;
        }

        if (did) _fireCD = fireCooldown;
    }

    // -------- RAIN 相关 ----------
    private static IList<string> GetTagsFromRig(AIRig rig)
    {
        if (rig == null || rig.AI == null || rig.AI.Navigator == null) return null;
        object nav = rig.AI.Navigator;
        try
        {
            var prop = nav.GetType().GetProperty("GraphTags", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (prop != null)
            {
                var val = prop.GetValue(nav, null) as IList<string>;
                return val;
            }
        }
        catch { }
        return null;
    }

    private static bool TryBuildPathViaRAIN(Vector3 from, Vector3 to, IList<string> tags, out IList<Vector3> corners)
    {
        corners = null;

        // 要求同时覆盖起点+终点的图
        var graphs = NavigationManager.Instance.GraphsForPoints(
            from, to, 2f, NavigationManager.GraphType.Navmesh, tags
        );
        if (graphs == null || graphs.Count == 0)
            return false;

        var graph = graphs[0];
        var gtype = graph.GetType();

        object pathObj = null;
        var tryNames = new[] { "GetPath", "GetPathTo", "CalculatePath", "BuildPath" };
        foreach (var name in tryNames)
        {
            var mi = gtype.GetMethod(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (mi == null) continue;

            var ps = mi.GetParameters();
            if (ps.Length >= 2 && ps[0].ParameterType == typeof(Vector3) && ps[1].ParameterType == typeof(Vector3))
            {
                var args = new object[ps.Length];
                args[0] = from;
                args[1] = to;
                // 可选参数兜底
                for (int i = 2; i < ps.Length; i++)
                    args[i] = ps[i].ParameterType.IsValueType ? Activator.CreateInstance(ps[i].ParameterType) : null;

                try
                {
                    pathObj = mi.Invoke(graph, args);
                    if (pathObj != null) break;
                }
                catch { }
            }
        }

        if (pathObj == null) return false;

        if (pathObj is IList<Vector3> vlist)
        {
            corners = vlist;
            return corners.Count > 0;
        }

        // 尝试从路径对象中取折线
        var props = new[] { "WaypointList", "Waypoints", "Corners", "Points" };
        foreach (var pn in props)
        {
            var p = pathObj.GetType().GetProperty(pn, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (p == null) continue;
            try
            {
                var val = p.GetValue(pathObj, null);
                if (val is IList<Vector3> pts)
                {
                    corners = pts;
                    return corners.Count > 0;
                }
            }
            catch { }
        }
        return false;
    }

    // ---------- 日志：改为你要求的格式 ----------
    public static void Log(string tag, string msg)
    {
        WriteLine("[" + DateTime.Now.ToString("HH:mm:ss.fff") + "][" + tag + "] " + msg);
    }

    // 若你已有 FileLogger.WriteLine(string) 或 FileLogger.Log(tag,msg)，这里会自动转发；否则回退 Unity Debug.Log
    private static void WriteLine(string line)
    {
        // 1) FileLogger.WriteLine(string)
        try
        {
            var t = Type.GetType("FileLogger") ?? AppDomain.CurrentDomain.GetAssemblies().SelectMany(a => a.GetTypes()).FirstOrDefault(x => x.Name == "FileLogger");
            if (t != null)
            {
                var mWrite = t.GetMethod("WriteLine", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic, null, new[] { typeof(string) }, null);
                if (mWrite != null) { mWrite.Invoke(null, new object[] { line }); return; }

                var mLog = t.GetMethod("Log", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic, null, new[] { typeof(string), typeof(string) }, null);
                if (mLog != null) { mLog.Invoke(null, new object[] { "AutoNav", line }); return; }
            }
        }
        catch { }

        // 2) Unity 回退
        try { UnityEngine.Debug.Log(line); } catch { }
    }
}
