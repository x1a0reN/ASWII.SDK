# RAIN 安全走廊策略

最后更新：2026-07-23

## 目标

普通移动只选择远离 `.aswnav` 外轮廓的路径。路径安全优先于距离和可达率：

- 不沿悬崖边、道路边或 NavMesh 外轮廓行走。
- 宁可绕远、等待重新规划或判定目标不可达，也不放行低净空捷径。
- 角色已经偏到边缘时，只允许朝净空持续增大的内侧方向恢复。

## 事故证据

`rain_follow_failure_pid37968_20260723_202734475.txt` 显示角色当前位置
`(-6.50,-0.69,5.31)` 已比剩余路径点低 `1.77m`，说明失败日志记录时角色已经离开
原导航层。旧实现只记录墙体净空 `clearanceTo=1.35`，没有测量目标和整段路径到
NavMesh 外轮廓的距离，因此无法在失足前阻止贴边路线或局部避让偏移。

`ASW_SurvivalBot.pid388.log` 只有启动和网络行，没有包含本局路径或失足现场信息。

## 固定安全参数

以当前 `level33.aswnav` 的 `AgentRadius=0.45m` 为基础：

| 参数 | 数值 | 作用 |
|---|---:|---|
| 硬外轮廓净空 | `0.80m` | 任一点低于此值即不可用于普通路径 |
| 优选外轮廓净空 | `1.35m` | A* 在可行路径中继续强烈偏好更居中的路线 |
| 走廊采样间距 | `0.10m` | 对平滑后每一段进行连续采样 |
| 边缘恢复距离 | `1.20m` | 仅允许从不安全起点向安全内侧渐进恢复 |
| 跟随前视距离 | `0.20m` 至 `1.15m` | 每帧在实际输出移动方向前复核 |

这些参数从 `.aswnav` 现有数据运行时计算，不需要重新生成地图文件。

## 四层防线

1. **A* 拓扑层**
   - 读取 `.aswnav` 的真实外轮廓边并建立常驻空间索引。
   - 过滤外轮廓净空不足的 Portal 和中间 Poly。
   - 对 `0.80m` 到 `1.35m` 的可行区域施加强二次代价，使较长但更居中的路径优先。
   - SurvivalBot 普通移动禁用边界 OffMesh Jump/Drop。

2. **走廊和平滑层**
   - 每 `0.10m` 校验路径点仍在同一导航连通分量。
   - 每个采样点到外轮廓必须至少 `0.80m`，不再在短路径和端点处把净空缩到零。
   - 快捷平滑和拐角修复只能保留通过完整安全走廊校验的线段。

3. **跟随器层**
   - 局部角色避让后的最终方向必须再次通过安全走廊校验。
   - 当前方向不安全时，只尝试有限角度的安全替代方向。
   - 所有候选都不安全时立即停止、清空当前路径并重新规划，不继续试探边缘。

4. **导航层错位防线**
   - 起点和投影导航层相差超过 `1.10m` 时拒绝寻路，避免角色已经跌落后继续追随上层路径。
   - 失败日志同时输出墙体净空和外轮廓净空，便于区分撞墙与失足。

## 主动跳崖的处理

排名达标后的主动结束对局不是普通寻路。寻路目标从原来的边缘内侧 `0.72m`
后移到 `1.15m`，保证到达等待点之前仍受普通安全走廊约束；只有最终经过致死落差
复核后，控制器才从安全等待点执行一次明确的向外跳跃。

## 验证

```powershell
& 'D:\Program Files\Visual Studio 2026\MSBuild\Current\Bin\MSBuild.exe' `
  '.\Tools\CompactNavConverter\CompactNavConverter.csproj' `
  /t:Rebuild /p:Configuration=Release /m /v:minimal

& '.\Tools\CompactNavConverter\bin\Release\CompactNavConverter.exe' `
  --pathtest `
  'C:\Users\x1a0reN\AppData\LocalLow\____________II\ASWDEBUG\NavMeshCache\level33.aswnav'

& '.\Tools\CompactNavConverter\bin\Release\CompactNavConverter.exe' `
  --safetytest `
  'C:\Users\x1a0reN\AppData\LocalLow\____________II\ASWDEBUG\NavMeshCache\level33.aswnav'
```

通过标准：

- `normal_path ok=True/True deterministic=True`
- `boundary_links_disabled=True`
- `safety_synthetic ... =True`
- `safety_corpus paths=64/64 ... failed=0 ... unsafe=0`
