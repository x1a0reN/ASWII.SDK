# SurvivalBot 导航与运行热点复审（2026-07-23）

## 1. 复审证据

最后一局日志在场景退出时记录：

```text
queryBegins=153 queryCancels=78
```

并且在结算前仍有 `activeQueries=1`。同一局稳定就绪状态每秒写一条完整
`NAVMESH state=ready` 心跳。路线日志还出现过同步精确收尾耗时 `optMs=26`；此前的
局部绕障记录出现过 `detourExpanded=249`。

这些证据分别对应查询起点漂移造成的重复取消、阶段结束后待处理查询未立即释放、
稳定状态日志 I/O 过密，以及局部绕障仍可能在一次迭代中执行大量实时物理探测。

## 2. 已修复项

### 2.1 RAIN 局部绕障改为可续跑任务

常规 `.aswnav` 路线的局部网格绕障现在纳入 `RainRouteFinalizeJob`：

- 每次地面投影、图归属、站立空间、墙体净空仍逐项执行；
- 每条路径段仍按原来的 `0.9m` 密度执行实时物理校验；
- 搜索半径、`0.75m` 网格、最多 720 个扩展节点和评分公式均未改变；
- 每帧最多使用约 `5ms`，未耗尽的搜索状态留到下一帧继续；
- 收尾阶段仍再次执行完整路线验证和跳跃标注。

这不是减少采样，而是把完全相同的校验顺序拆到多帧执行。

### 2.2 降低移动中 A* 查询抖动

紧跟移动目标时，旧查询仅允许起点漂移 `0.80m`，角色沿既有安全路线移动时会频繁
取消尚未完成的查询。容差改为 XZ `3.25m`、Y `1.50m`，与现有物理搜索任务一致。

查询结果不会直接替换当前路线。候选路径仍从角色**当前实时位置**执行：

1. `.aswnav` Poly/Portal 走廊校验；
2. 实时密集物理段校验；
3. 首段可行性校验；
4. 只有全部通过才替换旧路线。

路线日志新增 `queryStartDrift`，供下一局确认复用幅度和取消率。

### 2.3 明确取消不再使用的查询

进入攻击、紧急状态、回合重置或收到最终排名时，现在只取消待处理路径任务，保留
进程级 `.aswnav` Dataset 和查询工作区，不触发图重载。这样不会在结算阶段留下
`activeQueries=1`，也不会破坏反复进出地图时的常驻复用架构。

### 2.4 移除每帧临时集合分配

存活人数统计原来每次调用都创建 `HashSet<int>`；主循环每帧调用会持续制造 GC 压力。
现在复用进程内集合并在调用前清空，统计结果不变。

### 2.5 降低稳定状态日志 I/O

`Ready` 心跳由 1 秒调整为 10 秒，`Fallback` 为 5 秒；状态发生变化时仍立即写日志，
UI 状态读取频率不变。

## 3. 保持不变的精度与安全边界

- `.aswnav` 仍使用 RAIN `0.10m` 数据；
- 转换期 Poly/Portal 轮廓重建不变；
- 运行时 `IsPortalOnPolyBoundary` 第二层防御不变；
- 悬崖候选仍要求 15 个落差探针全部达到致死落差；
- 拐角外扩、角色半径、走廊偏差、实时墙体/站立空间校验阈值均未降低；
- 查询取消和分帧任务都不卸载 Dataset，不会恢复旧的反复建图生命周期。

## 4. 验收方式

构建阶段必须同时通过 Private / ReleaseA Mono.Cecil 功能审计。正式 `.aswnav` 继续执行：

```powershell
CompactNavConverter.exe --verify <level33.aswnav>
CompactNavConverter.exe --topologyaudit <level33.aswnav>
CompactNavConverter.exe --pathtest <level33.aswnav>
CompactNavConverter.exe --safetytest <level33.aswnav>
```

下一局运行日志重点核验：`queryCancels/queryBegins` 比例下降、结算时
`activeQueries=0`、路线出现 `finalizeSlices/finalizeCpuMs`，并且无
`aswnav_unsafe`、`blocked_walk` 或跟随器安全拒绝增加。
