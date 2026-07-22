# SurvivalBot：0.10m RAIN 紧凑导航实施计划

更新日期：2026-07-22  
适用分支：`codex/survival-ai`  
实施基线：`bf0bd37 checkpoint: preserve current SurvivalBot RAIN state`

## 实施状态（2026-07-22）

| 阶段 | 状态 | 提交/结果 |
|---|---|---|
| 0：保存当前状态 | 已完成并推送 | `bf0bd37` |
| 1：计划与证据固化 | 已完成并推送 | `946f1ea` |
| 2：转换器与格式 | 已完成并推送 | `8ef80e0` |
| 3：数据集与空间索引 | 已完成并推送 | `4c66fe2` |
| 4：Portal A* / Funnel / Off-Mesh | 已完成并推送 | `4d50231` |
| 5：SurvivalBot 生命周期接入 | 已完成并推送 | `9bcdbe9` |
| 6：遥测与自动压力验收 | 已完成实现和离线验收，随阶段 6 提交推送 | 详见 `RAIN_COMPACT_NAV_VALIDATION.md` |

阶段 6 的“游戏内连续进出 20 次、连续 50 局、24/72 小时长期运行”属于部署后的实机运行验收，不能由离线工具替代；在取得真实运行日志前不得标记为通过。

## 1. 目标与最终决策

最终架构采用：

> RAIN 只作为高精度建图来源和过渡期比对基准；`level33` 运行时只加载本项目的不可变紧凑导航数据，由本项目执行 Portal A*、Funnel 和 Off-Mesh Link 规划。

必须同时满足：

1. 保留当前 `level33.rainnav` 的 `cell=0.10m` 几何和拓扑，不降采样。
2. 不在场景切换期间创建、注册、注销或销毁 RAIN 导航图。
3. 不让常驻导航数据持有 `GameObject`、`Transform`、Collider 或其他 Unity 原生对象。
4. 导航数据每个进程最多加载一次；每局只创建轻量 `SceneSession`。
5. `phys_grid_2_5d` 不承担全局规划，只允许做局部碰撞确认和受控脱困。
6. 在 x86 地址空间中保留足够余量，避免 RAIN 对象图的大量小对象和重复物化造成堆碎片。

## 2. 已验证事实

### 2.1 当前跨场景方案已经失败

当前代码在退出 `level33` 时调用：

```csharp
_navMesh.MountPoint = null;
```

实际 `RAIN.dll` 的 `NavMesh.MountPoint` setter 会访问传入 Transform 的 `parent`，传入 `null` 会抛出 `NullReferenceException`。

最新日志已经记录：

```text
runtime_unregister_verified removed=1
resident_mount_detach_failed:NullReferenceException
```

因此“保留同一 `NavMesh` 并跨场景重新注册”不再作为交付方向。

### 2.2 当前 level33 长期运行配置不是 0.10m

现有两个缓存：

| 文件 | 生成精度 | 节点数 | 用途 |
|---|---:|---:|---|
| `level33.rainnav` | `0.10m` | 625,403 | 最大精度缓存 |
| `level33.runtime.rainnav` | `0.20m` | 364,991 | 当前 level33 运行缓存 |

`AutoBattleRoutePlanner.PrepareNavigationLoad` 当前对 `level33` 强制选择 `0.20m` profile。

### 2.3 0.10m 缓存结构

对 `level33.rainnav` 和 `level33.rainmeta` 的流式解析结果：

| 数据 | 数量 |
|---|---:|
| 顶点 | 214,383 |
| 多边形 | 206,056 |
| RAIN Edge/Portal 节点 | 419,347 |
| 单多边形边界边 | 84,056 |
| 双多边形 Portal | 335,241 |
| 多多边形共享边 | 50 |
| Contour 索引 | 754,723 |
| Triangle 索引 | 1,027,833 |
| Off-Mesh Jump/Drop Link | 17,955 |
| 连通分区 | 1,141 |

按 `float32 + int32 + CSR` 组织的核心拓扑粗估约 `25.7 MB`。最终内存还需加入 metadata、空间索引、A* 工作区和运行遥测，以真实测量为准。

### 2.4 RAIN 实际寻路语义

RAIN 不是简单的“多边形中心 A*”：

1. `NavMeshEdge` 是主要路径节点。
2. 同一多边形内的所有 Edge 两两连接。
3. 连接代价是 Edge 中心之间的三维距离。
4. 起点和终点临时连接到所在多边形的 Edge。
5. 路径重建后由 `NavMeshPath.Smooth()` 对 Portal corridor 执行漏斗式平滑。

新规划器必须保留这个模型，不能用普通多边形中心 A* 替代，否则会改变拐角、窄通道和多楼层路径选择。

## 3. 目标架构

```text
level33.rainnav + level33.rainmeta
                 |
                 v
       deterministic converter
                 |
                 v
          level33.aswnav
                 |
                 v
  CompactRainNavDataset (process lifetime)
       |             |              |
       v             v              v
  3D projection   Portal A*      Off-Mesh links
       \             |              /
        \            v             /
         +------ Portal corridor --+
                       |
                       v
                    Funnel
                       |
                       v
             SceneSession route result
                       |
                       v
             existing local follower/physics
```

### 3.1 进程级对象

`CompactRainNavDataset`：

- 只包含值类型数组、字符串标识和哈希。
- 成功加载后保持只读。
- 只允许 `level33` 使用。
- 不引用任何场景对象。
- 不在场景退出时释放或重新加载。

### 3.2 场景级对象

`CompactRainNavSession`：

- 保存场景代次 ID。
- 保存当前路径任务、路径结果和跟随状态。
- 可以引用当前玩家和目标，但必须在离场时全部清除。
- 退出时只取消查询并清空场景引用，不操作 Dataset。

### 3.3 线程模型

第一版使用主线程分帧 A*：

- 每帧限制展开节点数和耗时预算。
- 不调用 RAIN Worker。
- 不在后台线程调用 Unity Physics。
- 不引入场景卸载与导航 Worker 的竞态。

若后续性能不足，再增加单一长期 Worker；Worker 只能访问不可变数组，并通过 scene epoch 丢弃过期结果。

## 4. `ASWNAV` 文件格式

首版格式标识：`ASWNAV01`，Schema Version `1`。

### 4.1 Header

- Magic 和 Schema Version。
- 地图名，只接受 `level33`。
- RAIN 程序集身份。
- 原始 `.rainnav` SHA-256。
- 原始 `.rainmeta` SHA-256。
- 内容指纹 `FileInfo.xml`。
- 完整生成签名，必须包含 `cell=0.10`、`radius=0.45` 等参数。
- 各 section 的数量、偏移和长度。
- 整体 payload SHA-256。

### 4.2 Geometry

- `NavVertex[]`：原始三个 `float32`，不得降采样。
- `NavPoly[]`：contour/triangle/edge CSR 范围、中心、Bounds、component、flags。
- `NavPortal[]`：两个顶点、关联多边形 CSR 范围、pairing、边界标志。
- 所有原始 RAIN node index 到紧凑 poly/portal index 的映射在转换阶段完成；运行文件不保留对象引用。

### 4.3 Metadata

- Boundary sample。
- Surface sample、clearance、cover、safe-spawn flags。
- Off-Mesh Link：起终 Portal、精确世界坐标、方向、类型、代价、跳跃能力要求。

### 4.4 Spatial index

- 使用三维分桶或 BVH，不能只按 X/Z 建索引。
- 索引只缩小候选范围；最终命中必须使用 triangle point-in-poly 和高度截取。
- 必须避免上下楼层相同 X/Z 的错误投影。

### 4.5 加载原则

- 不压缩，避免 x86 解压内存峰值。
- 使用流式读取直接填充最终数组。
- 校验失败时不部分启用。
- 加载完成后不得保留整个文件的 `byte[]` 副本。

## 5. 规划算法

### 5.1 起终点投影

1. 使用三维空间索引取得候选多边形。
2. 对候选 triangle 计算 X/Z 包含和 Y 截距。
3. 使用水平距离、垂直误差、楼层容差和可站立条件评分。
4. 返回精确表面点及 poly index。
5. 没有同层候选时 fail closed，禁止投影到上下层。

### 5.2 Portal A*

- 搜索状态为 Portal index。
- 从起点向起始多边形的所有 Portal 建临时边。
- 展开 Portal 时，遍历它关联的所有多边形，再遍历这些多边形的其他 Portal。
- 代价使用 Portal 中心三维距离，保持 RAIN 语义。
- 终点多边形的 Portal 与目标点建立临时代价。
- heuristic 使用当前位置到目标点的三维距离。
- 通过 capability 过滤 Jump/Drop Link。
- 使用预分配数组和 search stamp，禁止每次查询创建数十万个对象或 Dictionary。

### 5.3 Funnel

- 使用路径经过的 Portal 两端点构造 corridor。
- 第一版移植 RAIN `NavMeshPath.Smooth()` 的 X/Z 左右判定和交点行为。
- 起终点使用投影后的真实表面点。
- 输出世界坐标 `Vector3` 仅发生在最终结果转换阶段。
- 不额外按 10cm 量化路径点。

### 5.4 Off-Mesh Link

- Link 是不可变记录，不再动态修改全局图。
- 查询时根据职业能力筛选。
- 路径结果携带强制 Jump/Drop flag。
- 跟随器只在对应硬锚点执行跳跃或下落。

## 6. 内存预算

第一版目标，而非未经验证的结论：

| 项目 | 目标 |
|---|---:|
| 文件读取临时峰值 | `< 16 MB` |
| Geometry + topology | `< 45 MB` |
| Metadata + spatial index | `< 35 MB` |
| A* 可复用工作区 | `< 20 MB` |
| 导航常驻总量 | `< 100 MB` |
| 每次普通查询临时分配 | `< 256 KB` |

硬性规则：

- 禁止每个 poly/portal 使用 class。
- 禁止每个 poly 使用独立 `List<T>`。
- 禁止查询级 `Dictionary<node,...>`。
- 禁止同时保留 RAIN 对象图和 Compact Dataset 作为正式运行路径。
- 转换/比对模式与长期运行模式互斥。

## 7. 分阶段实施与提交边界

每个阶段必须独立构建、验证、提交并推送。

### 阶段 0：保存当前状态

状态：已完成。

提交：

```text
bf0bd37 checkpoint: preserve current SurvivalBot RAIN state
```

### 阶段 1：计划与证据固化

产物：

- 本文档。
- 明确方案 C 已失败。
- 固化 0.10m 数据统计、内存预算和验收条件。

提交建议：

```text
docs: plan compact 0.10m navigation runtime
```

### 阶段 2：确定性转换器和文件格式

新增模块建议：

```text
Cheats/AutoBattle/CompactNav/CompactRainNavFormat.cs
Cheats/AutoBattle/CompactNav/CompactRainNavConverter.cs
Cheats/AutoBattle/CompactNav/CompactRainMetaReader.cs
```

要求：

- 流式读取现有 `.rainnav` 和 `.rainmeta`。
- 验证 Magic、Schema、签名、SHA 和完整消费长度。
- 处理单侧、双侧及多侧 Portal。
- 将 RAIN node index 转换为紧凑 index。
- 原子生成 `level33.aswnav`，保留旧文件和 `.previous.*` 备份。
- 同一输入重复转换必须得到相同 SHA-256。

提交建议：

```text
feat: convert RAIN caches to compact navigation
```

### 阶段 3：不可变数据集和三维空间索引

新增模块建议：

```text
Cheats/AutoBattle/CompactNav/CompactRainNavDataset.cs
Cheats/AutoBattle/CompactNav/CompactRainNavLoader.cs
Cheats/AutoBattle/CompactNav/CompactRainSpatialIndex.cs
```

要求：

- 一次加载和完整校验。
- 对外只暴露只读查询。
- 实现同层 poly 投影、triangle Y 截距和最近点。
- 记录精确常驻字节数和加载峰值。

提交建议：

```text
feat: load immutable compact navigation data
```

### 阶段 4：Portal A*、Funnel 和 Off-Mesh Link

新增模块建议：

```text
Cheats/AutoBattle/CompactNav/CompactRainPathfinder.cs
Cheats/AutoBattle/CompactNav/CompactRainFunnel.cs
Cheats/AutoBattle/CompactNav/CompactRainQuery.cs
```

要求：

- 分帧、可取消、无查询级大对象分配。
- 保留 RAIN Portal 连接代价。
- 支持 partial/pending/complete 状态。
- 支持 Jump/Drop capability 筛选和硬锚点输出。
- 支持固定查询语料的确定性输出。

提交建议：

```text
feat: add compact portal pathfinding
```

### 阶段 5：接入 SurvivalBot 和场景生命周期

修改范围：

```text
AutoBattleRoutePlanner.cs
RuntimeRainNavMesh.cs
RuntimeRainNavDerivedData.cs
LocalNavigationCombatTest.cs
MapBakeSceneLoader.cs
SurvivalBotManager.cs
```

要求：

- `level33` 默认 provider 改为 `aswnav_0_10`。
- 运行时不再为 `level33` 创建 `RainNavMesh`、host 或注册 RAIN manager。
- 场景退出只取消查询和清理场景引用。
- 删除/禁用 resident unregister/remount 路径。
- 原 RAIN 路径仅保留在显式诊断/比对模式中。

提交建议：

```text
feat: switch level33 to compact navigation
```

### 阶段 6：遥测、比对和压力验收

要求：

- Dataset load count 必须始终为 1。
- 每局记录 scene epoch、active query count、dataset bytes。
- 内存日志限流，禁止异常状态逐帧刷日志。
- 提供 RAIN/Compact 双跑比对入口，正式长期运行时默认关闭。
- 缓存错误、内存预算失败或 transform 校验失败时禁用 Bot，不回退全局 2.5D。

提交建议：

```text
test: validate compact navigation lifecycle
```

## 8. 验证矩阵

### 8.1 文件与结构验证

- 两次转换 SHA-256 完全一致。
- 输入和输出统计一致。
- 所有 index 都在范围内。
- 所有 section 无重叠、无尾随数据。
- 625,403 个 RAIN node 全部得到明确处理。
- 50 个多多边形共享边不能被丢弃。

### 8.2 路径质量比对

从 safe surfaces 和巡逻点生成固定查询集，至少覆盖：

- 同层直线路径。
- 多个墙角。
- 楼梯和斜坡。
- 上下楼层相同 X/Z。
- 狭窄通道。
- Jump/Drop Link。
- 不可达分区。

比对项目：

- 可达/不可达一致。
- 起终楼层一致。
- Off-Mesh Link 序列一致。
- 路径长度和 corridor 合理一致。
- 所有输出点都在 0.10m 数据表达的有效表面上。

同代价的不同 corridor 可以接受，但必须通过现有物理跟随安全检查。

### 8.3 生命周期验收

- 本地巡回测试连续进出 `level33` 至少 20 次。
- 生存模式至少连续 50 局。
- Dataset ID 和 load count 不变。
- 不出现 RAIN 注册、注销、resident remount 日志。
- 不存在旧 scene epoch 的查询结果被采用。

### 8.4 长期内存验收

- 挂测 24 小时作为最低门槛，目标 72 小时。
- 记录 Private Bytes、托管堆、最大连续空闲区。
- 前几局允许缓存热身增长，稳定后不得持续单调增长。
- 导航常驻内存必须满足预算，单次查询结束后工作区回到基线。

## 9. 构建、部署和回滚

每个代码阶段：

1. 构建前按项目规则比较六对部署文件并备份不同目标。
2. 使用 Visual Studio 2026 MSBuild 构建 Debug。
3. 验证 PostBuild 部署的六对文件长度和 SHA-256 一致。
4. 执行该阶段的最小充分验证。
5. 提交并推送当前阶段。

回滚原则：

- 每阶段一个独立提交，可使用正常 `git revert` 生成反向提交。
- 保留原 `.rainnav`、`.rainmeta` 和旧 `.aswnav`。
- 不删除部署备份。
- Compact provider 未通过验收时，可由配置切回已知的 `0.20m` 诊断路径，但不能重新启用方案 C 的跨场景注册。

## 10. 完成定义

只有同时满足以下条件才算完成：

1. `level33` 正式路径完全不依赖运行时 RAIN 图注册。
2. 使用 `0.10m` 源数据且无几何降采样。
3. 转换器、加载器、投影、Portal A*、Funnel、Off-Mesh Link 均有可复现验证。
4. 完成连续进出和长期内存验收。
5. 所有阶段均已提交并推送到 `origin/codex/survival-ai`。
