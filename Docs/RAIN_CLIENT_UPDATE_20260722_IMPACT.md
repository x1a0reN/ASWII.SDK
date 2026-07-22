# 2026-07-22 客户端更新对 SurvivalBot / RAIN 的影响

日期：2026-07-22
分支：`codex/survival-ai`
基线：`085f1cf test: validate compact navigation lifecycle`

## 1. 结论

本次更新没有改变 `RAIN.dll` 的程序集身份、MVID、类型或成员签名；变化是游戏把 RAIN
纳入了新的磁盘加密/运行时解密保护，并给 1172 个方法写入保护器实现标记。对
SurvivalBot 正式 `level33` 运行路径没有代码兼容性影响，也不要求重新生成现有
`level33.aswnav`。

正式 `level33` 路径仍使用进程级不可变 `CompactRainNavDataset`，不会创建、注册、注销
或跨场景重挂 RAIN 对象。因而新 RAIN 保护不会重新引入此前的多次进出地图崩溃路径。

仍需实机验证的是游戏整体更新后的运行行为，而不是 RAIN API 兼容性：连续进出 20 次、
连续 50 局及 24/72 小时内存曲线仍不能由静态差分替代。

## 2. RAIN 前后证据

旧版分析基线：

```text
C:\Users\x1a0reN\AppData\LocalLow\____________II\AssemblyBackups\20260722_RAIN_before_update\RAIN.dll
```

新版运行时透明解密镜像：

```text
C:\Users\x1a0reN\AppData\LocalLow\____________II\Managed_Dump\BATCH_20260722_153552\
  STRUCTURED_RAIN__02fb7e0d-e39e-4135-ac79-ad06bab671f9\RAIN.runtime-read.dll
```

新版 IL 重建镜像：

```text
C:\Users\x1a0reN\AppData\LocalLow\____________II\Managed_Dump\BATCH_20260722_153552\
  STRUCTURED_RAIN__02fb7e0d-e39e-4135-ac79-ad06bab671f9\RAIN.deobf.dll
```

| 项目 | 旧版 | 新版运行时镜像 |
|---|---|---|
| Assembly | `RAIN, Version=2.0.0.0` | 相同 |
| MVID | `02fb7e0d-e39e-4135-ac79-ad06bab671f9` | 相同 |
| 类型 | 210 | 210 |
| 方法（含构造器） | 2308 | 2308 |
| 字段 | 984 | 984 |
| 属性 | 589 | 589 |
| API 签名集合 | 4091 | 4091 |
| API 新增 / 删除 | - | `0 / 0` |
| `ImplAttributes & 0x8000` | 0 | 1172 |
| 程序集引用 | 4 | 相同的 4 个 |

文件 SHA-256：

```text
旧版 RAIN.dll
16358C4D13FE367AB829E9A919FC66376DA342A7FEFE2355CFFA89FE18DAC68A

新版 RAIN.runtime-read.dll
B5A3A43934646EBD977E891B506B36C2DE85F1F82DFE20F1B676A50DDCBB886B

新版 RAIN.deobf.dll
DB2549C2C7678FDE15C0C7FA41CB33DA871D3E99F2B8787234E62066E47D3239
```

相同 MVID 与完全相同的 API 集合说明没有证据表明 RAIN 业务版本被替换；文件哈希和大小
变化来自外层加密、保护标记及重建输出格式，不能单独解释为寻路算法变化。

## 3. 对 SurvivalBot 各路径的影响

### 3.1 正式 `level33` 路径

无直接影响：

- `AutoBattleRoutePlanner.PrepareNavigationLoad` 对非 Bake 的 `level33` 强制
  `loadNavmesh=false`；
- provider 为 `aswnav_0_10`；
- `RuntimeRainNavMesh.EnableResidentRainGraph=false`；
- 进退场只维护 scene epoch、查询与轻量场景状态；
- `level33.aswnav` 的几何、Portal 和 Off-Mesh 数据不依赖运行时 RAIN 方法体。

### 3.2 MapBake、诊断与非 `level33` 回退路径

这些路径仍直接引用 `RAIN.Navigation` 类型和部分私有字段。当前签名及已检查字段均未变化，
所以编译兼容；但保护器改变了磁盘装载形态，仍应执行一次显式 MapBake/诊断 smoke test。
此测试不能与长期正式 provider 同时开启。

### 3.3 编译引用

`ASWDEBUG.csproj` 当前仍引用 LocalLow 中保存的干净 `RAIN.dll` / `RAINMetaform.dll` 分析副本。
由于 API 集合完全一致，该引用可继续用于编译。不得把游戏目录中以
`2F-39-EA-21-22-9F-3F-75` 开头的受保护磁盘文件直接作为普通 Cecil/编译引用，也不得用
分析副本覆盖游戏原件。

## 4. 客户端程序集的相关更新

`Assembly-CSharp` 更新前后保持 2271 个类型、16598 个方法和 35594 个 API 条目，新增、
删除和签名变化均为 0。规范化 IL 差分中明确的非构造器业务变化是
`AimAssistDetector.UpdateSample(CameraObj)`：新版移除了 `IsSupportedWeapon` 条件和该分支
的 `ResetDetectionState()`，采样范围扩大。这影响主分支瞄准检测处理，不影响 Compact
RAIN 数据格式或寻路算法。

## 5. 后续验收

1. 使用主分支最终安全构建正常启动游戏，确认一次性批量 dump 开关保持关闭。
2. 在 SurvivalBot 分支执行不部署构建，确认更新后的 `Assembly-CSharp.deobf.dll` 编译兼容。
3. 实机进入 `level33`，日志确认 `provider=aswnav_0_10`、`loadCount=1`、`activeQueries=0`
   能在退场后成立。
4. 完成 20 次进退场、50 局与 24/72 小时内存曲线验收。
5. 若使用 MapBake/诊断路径，单独执行一次 smoke test；正式长期运行继续保持关闭。
