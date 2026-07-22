# SurvivalBot Compact RAIN 寻路精度加固

日期：2026-07-22
分支：`codex/survival-ai`
数据源：`level33.aswnav`（RAIN `cell=0.10m`）

## 1. 结论

本轮不恢复 RAIN 运行时对象，也不改变已经验证稳定的单例 Dataset/scene epoch 架构。
正式寻路仍只使用 `.aswnav`，但把“拓扑可达”提升为“拓扑与连续几何同时可达”：

- 搜索只接受 Portal 顶点确实构成 Poly 轮廓边的邻接。
- 每一条步行线都以 0.10m 间隔检查中心线；长步行段再检查左右安全余量。
- 路径优先经过 Portal 中心；不再把 Portal 端点/悬崖尖角直接当作步行拐点。
- 优化、拐角外扩、Follower 跳点和卡墙恢复都必须再次通过同一 `.aswnav` 走廊检查。
- 无合法 Off-Mesh Link 时宁可拒绝、重算或绕路，不把断层自动降级成普通步行或隐式跳跃。

这保留了 0.10m RAIN 数据精度，同时避免重新引入跨场景 RAIN 对象生命周期和内存问题。

## 2. 已确认根因

### 2.1 数据中存在几何不成立的拓扑引用

当前正式 `level33.aswnav` 的审计结果：

```text
invalid_poly_portal_refs=96
affected_polys=96
```

这些引用在索引上是双向的，但 Portal 的两个顶点并不是对应 Poly 的任一轮廓边。
例如 Poly 140047 位于约 `(-121.9, -69.0, 180.0)`，却引用中心位于约
`(-155.9, -240.7, -754.6)` 的 Portal 6524，两者相距约 935m。
旧搜索仅验证索引互相引用，因此可能把它当成合法邻接，直接形成“玩家与敌人隔空相连”的路径。

修复后，`CompactRainNavDataset.IsPortalOnPolyBoundary` 会核对 Portal 顶点对是否与
Poly 的连续轮廓顶点匹配。起点扩展、Portal→Poly、Poly→Portal、目标命中和走廊修复
都使用这一几何条件；错误引用保留在原始缓存中用于可追溯性，但运行时永远不会展开。

### 2.2 旧 Funnel 与后处理会靠近悬崖尖角

旧 Funnel 直接使用 Portal 端点作为拐点，后续物理层捷径、拐角外扩和 Follower 跳点
又分别进行离散判断。端点恰好是两个边界的交点时，路径可能沿悬崖尖角切线通过；
较稀疏的 Physics ground sample 也可能漏掉三角形断层。

本轮改为保守的 Portal 中心走廊：

1. 起点、每个 Portal 中点、终点构成原始中心线。
2. 相邻点不可安全直达时，依次尝试 Poly surface sample、Poly center、三角形重心。
3. 只有整条候选线通过连续检查时才允许贪心缩短。
4. 每个步行段最终还会由 RoutePlanner 和 Follower 再检查一次。

## 3. 连续走廊约束

当前正式参数从 `.aswnav` header 派生：

| 参数 | 当前值 | 作用 |
|---|---:|---|
| CellSize | 0.10m | 原始 RAIN 几何精度 |
| AgentRadius | 0.45m | 烘焙时角色半径 |
| 连续采样间隔 | 0.10m | 不跨过一个 RAIN cell |
| 额外左右余量上限 | 0.18m | 防止长捷径贴近悬崖尖角 |
| 双侧检查最短段 | 1.30m | 更短的 Portal 过渡依靠精确中心线与已烘焙半径 |
| 路径端点投影上限 | 0.55m | 避免 1.25m 投影跨过窄断层 |

左右余量在起终点附近渐进收放，并按 Poly 的实测边界 clearance 下调，避免合法窄通道和
Portal 边界产生假阴性。长度不超过 1.30m 的过渡仍执行全部 0.10m 中心/高度检查，
但不额外扩张双侧样本。该余量是对已按 0.45m AgentRadius 生成的可行走面的附加保护，
而不是替代 AgentRadius。

每个样本必须同时满足：

- 中心点精确位于三角形 XZ 投影内；
- 左右偏移点也精确位于同一连通分区；
- 相邻中心高度差不超过 StepHeight 容差；
- 左右样本与中心高度一致；
- 全程不依赖“投影到附近边缘”来填补空洞。

## 4. 接入位置

- `CompactRainCorridorValidator.cs`：0.10m 连续中心/双侧验证，可复用 64-int BVH 栈。
- `CompactRainPathfinder.cs`：起点、邻接和终点的几何拓扑过滤；同 Poly 直达也须验证。
- `CompactRainFunnel.cs`：Portal 中心走廊、内部修复点和安全捷径。
- `CompactRainNavDataset.cs`：Portal 是否属于 Poly 真实轮廓边的判定。
- `CompactRainQuery.cs` / `CompactRainNavRuntime.cs`：统一暴露步行段安全查询。
- `AutoBattleRoutePlanner.cs`：优化、角点外扩、路径终验、Follower 跳点及卡墙恢复统一加闸。
- `CompactNavConverter --safetytest`：合成悬崖/断层/错误拓扑与真实路径语料回归。

文件格式、Section 布局和正式缓存均未改变；现有 `.aswnav` SHA-256 仍为：

```text
99FCF27A8640E13BD3270A8D2C46ABCD3CF48F9A4136ADBF2217C1F1EF2D7E4E
```

## 5. 自动验证结果

### 5.1 合成回归

```text
synthetic_corner:
  对角切过凹角被拒绝，修复为 4 waypoint 安全路线

synthetic_short_corner:
  小于 1.30m 的凹角捷径仍被中心线检查拒绝，修复为 4 waypoint 安全路线

synthetic_gap:
  同一 Poly 内两个断开的三角形岛不可直达，完整路径被拒绝

synthetic_cliff_margin:
  仍在面内但距边缘过近的直线被拒绝，修复为经过内部的 3 waypoint 路线

synthetic_topology:
  远端 Portal 伪邻接被识别为 invalid_boundary，搜索返回 start_poly_has_no_portals
```

五项结果均为 `True`。

### 5.2 真实数据路径语料

```text
safety_topology invalid_poly_portal_refs=96 affected_polys=96
safety_corpus paths=64/64 attempts=70 unreachable=6 failed=0 segments=1495 unsafe=0 component=0
```

其中 6 组端点沿用旧 component 标记但在移除伪邻接后实际不可达，因此不作为可达路径样本；
测试继续抽样直到取得 64 条真实可达路径。所有已输出步行段均通过独立复验。

普通确定性路径：

```text
portals=63
waypoints=5
repairs=0
shortcuts=60
checks=68
spacing=0.10
clearanceMax=0.18
sideMinLength=1.30
deterministic=True
```

Off-Mesh 路径保留 1 个显式动作，4 waypoint，验证通过。

### 5.3 生命周期/内存

1000 次单例 Dataset 复用及路径查询：

```text
mismatches=0
cancelled=20
dataset_loads=1
singleton_reuses=1000
managed_peak_delta=3576
private_peak_delta=2101248
```

新增固定工作区仅为 256 bytes；完整 A* 工作区由 15,168,316 增至
15,168,572 bytes。测试过程中没有持续单调内存增长。

## 6. 实机验收重点

部署后应重点覆盖：

1. 敌人在对岸、上下层或断桥另一端时，不出现普通步行直线。
2. 连续经过悬崖凸角、凹角和窄桥，角色走 Portal/Poly 内部而不是贴尖角。
3. 卡墙恢复触发时，日志中的 `rain_clearance` 候选不得离开 `.aswnav` 走廊。
4. Off-Mesh Link 的跳跃/下落仍可执行，普通断层不能被推断为跳跃。
5. 多次进出 `level33` 继续保持 `loadCount=1`、退场 `activeQueries=0`。

自动测试能证明输出线段不离开数据层安全走廊；真实 Collider、角色控制器和网络状态仍需
通过上述实机路线验证，不能把未运行的游戏内测试标记为已通过。

## 7. 部署状态

游戏进程退出后已执行 Debug/PostBuild 部署。旧 `ASWDEBUG.dll` 保存在：

```text
C:\Users\x1a0reN\AppData\Local\ASWII\Data\ASWDEBUG.DeployBackups\20260722_193922340\Managed\ASWDEBUG.dll
```

新 `ASWDEBUG.dll` 长度为 494,592 bytes，SHA-256：

```text
4CB87AF41B02782D52755AB67624C56102FA81F7A886BAE7ABB8CBD582B4A570
```

`ASWDEBUG.dll`、`0Harmony.dll`、`Mono.Cecil.dll`、`BouncyCastle.Crypto.dll`、
x86 `winhttp.dll` 和 `doorstop_config.ini` 六对源/目标文件均已验证存在、长度一致、
SHA-256 一致。
