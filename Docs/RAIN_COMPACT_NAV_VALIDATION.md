# SurvivalBot 0.10m Compact RAIN 导航验收记录

日期：2026-07-22
分支：`codex/survival-ai`
计划：`Docs/RAIN_COMPACT_NAV_IMPLEMENTATION_PLAN.md`

## 1. 当前结论

`level33` 的正式全局寻路已经切换到 `aswnav_0_10`：保留原始 RAIN `cell=0.10m` 几何、Portal 拓扑和 Off-Mesh Link，但运行期不再创建、注册、注销或跨场景重新挂载 RAIN 对象图。

进程只加载一次不可变 `CompactRainNavDataset`，每次进出地图只递增 scene epoch、取消活动查询并清除场景级状态。常驻数据仅由值类型数组、索引和字符串构成，不持有 `GameObject`、`Transform`、`Collider` 或其他 Unity 场景对象。

这解决的是原 RAIN 对象图重复物化、注销/重挂失败以及 x86 堆碎片造成的导航侧主要崩溃来源。它不能保证游戏其他模块永远不会内存不足；贴图、音频、特效、原生插件或其他泄漏仍可能耗尽 x86 地址空间。

## 2. 缓存完整性与确定性

真实缓存：

```text
C:\Users\x1a0reN\AppData\LocalLow\____________II\ASWDEBUG\NavMeshCache\level33.aswnav
```

| 项目 | 结果 |
|---|---:|
| 文件长度 | 54,491,474 bytes |
| ASWNAV SHA-256 | `99FCF27A8640E13BD3270A8D2C46ABCD3CF48F9A4136ADBF2217C1F1EF2D7E4E` |
| Payload SHA-256 | `9F27876D864C6055240B27189C9D9AE2A4CE19F190C8177D4DAFE1A83845D460` |
| 源 `.rainnav` SHA-256 | `C8960D9EFCA52A085475908A96D0F6099451ED449C9199A0F6F5FC455668F539` |
| 源 `.rainmeta` SHA-256 | `FFF05DD21E3F7685CF471EF9F1552F55D75CDE1DD85D6C2E19D841E26303CB2B` |
| 原始 RAIN graph nodes | 625,403 |
| 顶点 / Poly / Portal | 214,383 / 206,056 / 419,347 |
| Off-Mesh Link | 17,955 |
| 连通分区 / 安全点 | 1,141 / 143,655 |

同一输入的两个独立转换产物长度均为 54,491,474 bytes，SHA-256 完全相同。`--verify` 对 header、section、payload hash、索引范围和尾随数据的校验通过。

## 3. 加载、投影与路径验证

执行工具：

```powershell
& 'Tools\CompactNavConverter\bin\Release\CompactNavConverter.exe' --verify  '<level33.aswnav>'
& 'Tools\CompactNavConverter\bin\Release\CompactNavConverter.exe' --load    '<level33.aswnav>'
& 'Tools\CompactNavConverter\bin\Release\CompactNavConverter.exe' --selftest '<level33.aswnav>'
& 'Tools\CompactNavConverter\bin\Release\CompactNavConverter.exe' --pathtest '<level33.aswnav>'
& 'Tools\CompactNavConverter\bin\Release\CompactNavConverter.exe' --safetytest '<level33.aswnav>'
```

结果：

- 流式加载成功：文件 54,491,474 bytes，Dataset + 3D BVH 估算常驻 57,936,004 bytes，BVH 65,535 nodes。
- 本次独立加载耗时 1388 ms；托管内存增量 58,187,008 bytes，Private Bytes 增量 61,652,992 bytes。
- 1505 个三角形中心投影全部成功，1505 个均返回原 Poly；错误 0；地图外点被拒绝。
- 普通路径连续两次完全一致：component 0（181,117 Poly），cost 132.403，63 Portal，5 waypoint，展开 491 nodes；中心线间隔 0.10m，长段额外横向余量上限 0.18m。
- Off-Mesh 测试使用 link 0 成功：2 Portal，4 waypoint，1 个动作，展开 13 nodes。
- 安全回归的凹角、短凹角、断层、悬崖边余量和远端伪拓扑五项合成用例全部通过；70 次尝试取得 64/64 条真实可达路径，1495 个步行段、unsafe=0。
- 当前正式缓存包含 96 条不属于对应 Poly 轮廓边的 Portal 引用，涉及 96 个 Poly；搜索阶段已全部按真实几何过滤。
- 可复用 A* 工作区为 15,168,572 bytes；Dataset + 工作区合计 73,104,576 bytes（约 69.72 MiB），低于 100 MB 导航常驻预算。

## 4. 生命周期压力测试

命令：

```powershell
& 'Tools\CompactNavConverter\bin\Release\CompactNavConverter.exe' --stress '<level33.aswnav>'
```

测试模型：一次加载 Dataset 和一个可复用查询工作区；循环 1000 次模拟进场复用，每轮执行确定性完整寻路；每 50 轮启动一次只展开一个节点的查询并取消；每 100 轮强制完整 GC 后采样。

最终结果：

```text
cycles=1000
mismatches=0
cancelled=20
dataset_loads=1
singleton_reuses=1000
elapsed_ms=17161
managed_peak_delta=3576
private_peak_delta=2101248
managed_growth=-15167832
private_growth=-14307328
```

验收结论：通过。1000 次复用期间 Dataset 始终为同一实例，路径无差异，所有取消均生效；热身后的托管堆和 Private Bytes 没有持续单调增长。

## 5. 运行时接入与遥测

正式 `level33` 路径的运行时约束：

- `AutoBattleRoutePlanner` 强制 `loadNavmesh=false` 并选择 `aswnav_0_10`。
- `RuntimeRainNavMesh.EnableResidentRainGraph=false`，旧的 resident unregister/remount 分支不能被正式路径置为 active。
- `CompactRainNavRuntime.DeactivateScene` 先取消 query，再保留无 Unity 引用的 Dataset/工作区。
- 进场日志记录 `scene`、`loadCount`、`activeQueries`、Dataset/workspace、managed/private。
- 退场日志记录 scene begin/end 计数、query begin/cancel 计数，并明确 `dataset=retained unityRefs=0`。
- 日志仅在首次加载、进场和退场边界写入，不逐帧输出内存状态。
- UI 快照显示 Dataset 加载次数和活动查询数。
- 旧 RAIN 路径只保留为显式 MapBake/诊断入口，不能与正式 Compact Dataset 同时作为长期 provider。

## 6. 构建与部署验收

Visual Studio 2026 MSBuild Debug 构建成功，PostBuild 复制 6 个文件。构建前六对源/目标哈希均相同，因此没有重复创建备份。

部署后六对文件均满足“存在、长度一致、SHA-256 一致”：

| 文件 | 长度 | SHA-256 |
|---|---:|---|
| `ASWDEBUG.dll` | 486,912 | `FA0FF80D817F0991FE53B903555317C03A36FFB0A6AF9F70105B51920C2A9666` |
| `0Harmony.dll` | 113,152 | `E271D22A7C32BFCA105D0E471C04EEE14EEC6142D37A30CD73CB7D32C7D1DD0F` |
| `Mono.Cecil.dll` | 280,064 | `F622A5B5B5ACECD40D11D2188C93AADDF2D1CFE04B8ABB897F504309EAF9610B` |
| `BouncyCastle.Crypto.dll` | 2,236,416 | `1985B85BB44BE6C6EAF35E02EF11E23A890E809B8EC2E53210A4AD5A85B26C70` |
| x86 `winhttp.dll` | 22,016 | `CC643B54484F694A8E0E6641CAC79D74141009AFC9E24D826F6FD7FD48FD182A` |
| `doorstop_config.ini` | 1,406 | `B83884899D2198177D84BBBB2DA02A2DD42F13E80849FEBE177F1D5E6A944C32` |

## 7. 尚未执行的实机验收

本次命令行会话没有启动并登录游戏，因此以下项目不能标记为通过：

1. 游戏内连续进出 `level33` 至少 20 次。
2. 生存模式连续运行至少 50 局。
3. 24 小时最低门槛、72 小时目标的长期内存曲线。
4. 真实玩家/怪物/动态 Collider 条件下的全量路线物理跟随质量。

实机日志位于：

```text
C:\Users\x1a0reN\AppData\LocalLow\____________II\Logs\ASW_SurvivalBot.pid<PID>.log
```

每次进退场必须满足：`loadCount=1`；`sceneEnds` 最终追平 `sceneBegins`；退场 `activeQueries=0`；不得出现 `resident_pinned`、`resident_suspended`、`resident_resumed`、`resident_mount_detach_failed`。热身阶段后 managed/private 不得随局数持续单调增长。
