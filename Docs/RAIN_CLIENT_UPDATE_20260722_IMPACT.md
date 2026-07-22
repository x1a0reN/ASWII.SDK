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

最终完整离线还原镜像（二次验收基线）：

```text
C:\Users\x1a0reN\AppData\LocalLow\____________II\Managed_Dump\
  OFFLINE_FULL_20260722_173157\FULL_MANAGED\RAIN.dll
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

最终 FULL_MANAGED RAIN.dll
5A4D071B4A30B569041C24B682F5388277FF806173212A84C54F3DCE4694308F
```

相同 MVID 与完全相同的 API 集合说明没有证据表明 RAIN 业务版本被替换；文件哈希和大小
变化来自外层加密、保护标记及重建输出格式，不能单独解释为寻路算法变化。

对最终 `FULL_MANAGED` 文件再次使用 Mono.Cecil 逐条规范化比较后，程序集版本、MVID、
210 个类型、2308 个方法、984 个字段、589 个属性及全部 4091 条签名仍与旧版完全相同，
新增 / 删除均为 `0 / 0`。两侧规范化 API 集合 SHA-256 均为：

```text
BB76BC322E1FFA5E30DC2FA1A14228A723D5BCB2388BFC9D445F9901A9F6DF0F
```

最终还原文件中的 `ImplAttributes & 0x8000` 已恢复为 0；运行时镜像中的 1172 个保护标记
属于保护器载荷，不是 RAIN API 或业务版本变化。

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

## 5. 验收状态

已完成：

1. 主分支检测链适配已构建、部署并推送，提交为 `2c4f27e`；一次性批量 dump 开关保持关闭。
2. SurvivalBot `Auction` 构建通过，确认最新 `Assembly-CSharp.deobf.dll` 编译兼容。
3. 对现有 `level33.aswnav` 执行 `--verify`、`--load`、`--selftest`、`--pathtest` 和
   `--stress`：文件 SHA-256 保持
   `99FCF27A8640E13BD3270A8D2C46ABCD3CF48F9A4136ADBF2217C1F1EF2D7E4E`，1000 次生命周期
   压测为 `mismatches=0`、`cancelled=20`、`dataset_loads=1`、`singleton_reuses=1000`，
   没有持续托管堆或 Private Bytes 增长。

仍需实机完成：

1. 进入 `level33`，日志确认 `provider=aswnav_0_10`、`loadCount=1`、`activeQueries=0`
   能在退场后成立。
2. 完成 20 次进退场、50 局与 24/72 小时内存曲线验收。
3. 若使用 MapBake/诊断路径，单独执行一次 smoke test；正式长期运行继续保持关闭。

## 6. 本次部署记录

部署时间：2026-07-22 18:36（游戏进程已退出）。

- 部署前仅 `Managed\ASWDEBUG.dll` 与当前 SurvivalBot 源构建不同；原主分支部署文件已备份到：

  ```text
  C:\Users\x1a0reN\AppData\Local\ASWII\Data\ASWDEBUG.DeployBackups\
    20260722_183609\Managed\ASWDEBUG.dll
  ```

- 最终部署的 SurvivalBot `ASWDEBUG.dll` 长度为 486912，SHA-256 为：

  ```text
  FA0FF80D817F0991FE53B903555317C03A36FFB0A6AF9F70105B51920C2A9666
  ```

- `ASWDEBUG.dll`、`0Harmony.dll`、`Mono.Cecil.dll`、`BouncyCastle.Crypto.dll`、x86
  `winhttp.dll` 与 `doorstop_config.ini` 六对源/目标文件均已验证长度和 SHA-256 相同。
- 最终 DLL 反编译确认包含 `CompactRainNavRuntime`、provider `aswnav_0_10`，并保持
  `RuntimeRainNavMesh.EnableResidentRainGraph=false`。
- 当前游戏目录中的活动 DLL 是刻意裁剪的 SurvivalBot 构建，不包含主分支 `AutoAim` UI/功能；
  主分支检测适配保留在 `main` 的 `2c4f27e`，两套部署不能在同一个 `ASWDEBUG.dll` 中同时生效。
