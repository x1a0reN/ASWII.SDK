# RAIN `.aswnav` 自动产出与拓扑规范化

日期：2026-07-22
分支：`codex/survival-ai`

## 目标

将 `level33` 的 MapBake 流程固定为：

```text
游戏内 RAIN 0.10m MapBake
        ↓
level33.max.rainnav + level33.max.rainmeta
        ↓
独立进程 CompactNavConverter.exe
        ↓
level33.aswnav
```

正常游戏运行仍只加载 `level33.aswnav`，不会恢复跨场景 RAIN 对象图。

## 自动转换时序

1. `RuntimeRainNavMesh` 完成 `level33.max.rainnav` 的原子落盘。
2. `RuntimeRainNavDerivedData` 完成 `level33.max.rainmeta` 的原子落盘并进入 `Ready`。
3. `CompactRainNavAutoConverter` 确认两个输入文件同时存在。
4. DLL 启动独立的 AnyCPU `CompactNavConverter.exe`，不在 32 位游戏进程中执行大文件转换。
5. 转换器先写临时文件，校验 payload 和源文件哈希后再原子替换 `level33.aswnav`。
6. MapBake 只有在 `.rainnav`、`.rainmeta` 和 `.aswnav` 三个产物都成功后才标记完成；直接建图模式也会等转换成功后再返回大厅。

转换器部署路径：

```text
C:\Users\x1a0reN\AppData\Local\ASWII\Data\ASWDEBUG.Tools\CompactNavConverter.exe
```

输出路径：

```text
C:\Users\x1a0reN\AppData\LocalLow\____________II\ASWDEBUG\NavMeshCache\level33.aswnav
```

`.rainnav` 和 `.rainmeta` 不会自动删除，保留用于复现、审计和重新转换。

## 拓扑规范化规则

旧 `level33.aswnav` 的实测审计结果：

```text
contour_edges=754723
portals=419347
duplicate_portal_edges=0
missing_portal_edges=96
orphan_portals=0
invalid_poly_portal_refs=96
```

这 96 条引用不是重复顶点别名。端点坐标误差审计中，`within_0.001=0`，最大端点误差约为 `1406.926m`，属于远端错误邻接。

转换器现在不再复制原始 `Poly→Portal` 和 `Portal→Poly` 数组，而是：

1. 以 Portal 的无向顶点对建立唯一边索引。
2. 逐个遍历每个 Poly 的连续轮廓边。
3. 只有端点索引严格匹配的 Portal 才写入该 Poly 的邻接表。
4. 没有合法 Portal 的轮廓边按封闭边处理，不伪造、不借用其他 Portal。
5. 由新 `Poly→Portal` 关系反向生成 `Portal→Poly`。
6. 根据规范化后的 Portal 邻接重新计算 Poly、Surface 和 Boundary 的连通分量。
7. 写文件前强制检查双向关系及几何关系；输出的 `invalid_poly_portal_refs` 必须为 `0`。

格式允许 `Poly.PortalCount <= Poly.ContourCount`。少掉的 Portal 表示不可跨越的封闭轮廓边，不降低 0.10m 顶点、轮廓和三角形精度。

运行时的 `CompactRainNavDataset.IsPortalOnPolyBoundary` 保留不变，继续在搜索和 Funnel 阶段做第二层 fail-closed 验证。

## 验证命令

不依赖游戏文件的规范化回归：

```powershell
& 'Tools\CompactNavConverter\bin\Release\CompactNavConverter.exe' --topologytest
```

旧文件结构审计：

```powershell
& 'Tools\CompactNavConverter\bin\Release\CompactNavConverter.exe' --topologyaudit '<level33.aswnav>'
```

新 MapBake 完成后必须执行：

```powershell
& 'Tools\CompactNavConverter\bin\Release\CompactNavConverter.exe' --verify '<level33.aswnav>'
& 'Tools\CompactNavConverter\bin\Release\CompactNavConverter.exe' --safetytest '<level33.aswnav>'
```

验收条件：

```text
topology_raw_invalid=96        # 若新 RAIN 序列化结果与旧版一致
topology_closed_edges=96       # 对应错误邻接被封闭
topology_output_invalid=0
safety_topology invalid_poly_portal_refs=0 affected_polys=0
```

原始错误数量可能随客户端或重新烘焙结果变化，所以强制条件只有输出错误数为 `0`；不能把 `96` 写成永久常量。

## 尚待实机完成的验收

当前 `.rainnav` 和 `.rainmeta` 已被删除，因此在下一次游戏内 MapBake 前无法生成并验证新的正式 `.aswnav`。部署后需要执行一次 `level33` 直接建图，并核对日志：

```text
[AUTO-BATTLE][NAVCACHE] disk_saved ... level33.max.rainnav
[AUTO-BATTLE][NAVMETA] disk_saved ... level33.max.rainmeta
[AUTO-BATTLE][ASWNAV] converter_started ...
[AUTO-BATTLE][ASWNAV] converter_completed ... topology_output_invalid=0
```

随后再执行 `--verify` 和 `--safetytest`。在这些步骤实际完成前，不把新正式数据文件标记为已验收。

## 本次构建与部署

Debug 构建和 PostBuild 部署已完成。旧 DLL 备份：

```text
C:\Users\x1a0reN\AppData\Local\ASWII\Data\ASWDEBUG.DeployBackups\20260722_202831594\Managed\ASWDEBUG.dll
SHA-256: 4CB87AF41B02782D52755AB67624C56102FA81F7A886BAE7ABB8CBD582B4A570
```

新产物：

```text
ASWDEBUG.dll
  length: 504832
  SHA-256: 72B687E2265417C46A3C31DE768B3024FB99753E41356EEB77F55F17C88CE594

CompactNavConverter.exe
  length: 88064
  SHA-256: F21E15F808071F2E1950B1290F6E6B2746F14828AB2A1E009518AFEC1A2D8B3D
```

项目产物与游戏目录目标文件的长度及 SHA-256 均一致；原有六项部署文件和新增转换器共七项通过验收。部署目录中的转换器执行 `--topologytest` 返回：

```text
topologytest raw_invalid=2 replaced=4 closed_edges=0 output_invalid=0 canonical=True
```
