# AimAssistDetector v8 检测链详细逆向与当前处理

## 0. 文档范围

本文是 [`AimAssistDetector_Bypass_Analysis.md`](AimAssistDetector_Bypass_Analysis.md) 的 v8 后续分析。

旧文档对“采样 -> 开枪捕获 -> HitMessage 填报 -> 加密射击包”的总体链路判断仍然成立，但它分析的是旧版 v3 字段。当前主程序集已经改成 v8，报告状态、字段集合、payload 长度和包级一致性约束都发生了变化，不能继续把 v3 的“空报告”方案直接套到 v8。

本文结论绑定以下程序集快照：

```text
文件：C:\Users\x1a0reN\AppData\LocalLow\____________II\Assembly-CSharp.deobf.dll
程序集名：Assembly-CSharp
架构：MSIL
SHA-256：106F9209403113C46437AB73E832573AB5DD692E832667EFC63E3711E0E485B5
分析日期：2026-07-17
```

当前项目构建产物：

```text
文件：bin\Debug\ASWDEBUG.dll
SHA-256：225DA29D3BBC4312B6055C2CE51CE87E03C5B29B0BECB01FA22A4B422F6122B2
部署目录：C:\Users\x1a0reN\AppData\Local\ASWII\Data\ASWII_Data\Managed
```

如果主程序集哈希变化，应重新反编译下列类型，不应把本文常量直接视为新版本事实：

```text
AimAssistDetector
HitMessage
ShootPayloadCrypt
ChannelConnection
CameraObj
GunBaseController
SniperGunController
```

## 1. 结论摘要

v8 与 v3 最大的区别不是字段改名，而是新增了明确的“本次是否完成同帧捕获”状态：

```text
0x88 = 0x80 captured flag + version 8
0x08 = version 8，但本次没有同帧捕获
```

因此，旧方案把所有射击统一处理成：

```text
version = 8
target = 0
shot precision = -1
samples = []
```

并不是“普通中性报告”，而是在稳定表达“每一枪都缺少同帧采样”。偶发缺失是协议允许状态，连续每枪缺失则形成了高度稳定的异常特征。

另外，v8 的 `aim_precision_samples` 是变长数组。`ChannelConnection.Shoot` 会先把数组长度写到外层射击包，再调用 `BuildEncryptedPayload` 构造内部加密数据。若在最终打包 Hook 中把数组清空，就会出现：

```text
外层 sample count = 原始 N
加密 payload sample count = 0
```

这是比单纯精度异常更直接的协议一致性风险。

当前处理不再阻断整个采样链，也不再把每枪伪造成缺失报告，而是遵守以下约束：

```text
保留原生 UpdateSample / MarkFireCooldown / CaptureShotReport
保留原生 ApplyPendingShotReport / FillRequiredShotReport
保留 v8 captured flag
保留 aim_target_uid
保留 aim_precision_samples 数组长度
只修正过度精确的精度编码
由原版 BuildEncryptedPayload 重新计算 MAC 并加密
```

## 2. 逆向过程

### 2.1 先验证分析对象

项目里的旧 POC 源码仍然包含 v3 版本的 `AimAssistDetector`，其中字段是：

```text
aim_lock_session_id
aim_lock_duration_ms
aim_lock_target_uid
aim_target_uid
aim_relative_speed_cmps
aim_head_precision_mm
```

但直接对当前 DLL 执行 ILSpy 反编译后，`HitMessage` 实际字段变成：

```csharp
public ObscuredByte aim_report_version;
public ObscuredByte aim_target_uid;
public ObscuredShort aim_shot_precision_code;
public short[] aim_precision_samples;
```

这一步说明旧 POC 只能用于理解历史演进，不能作为当前协议依据。

### 2.2 确认采样入口

当前 `CameraObj` 在更新完射击方向后调用：

```text
shootPos = transform.position
shootForward = transform.forward
shootUp = transform.up
shootRight = transform.right
AimAssistDetector.UpdateSample(this)
```

所以检测器观察的是游戏最终用于射击的 `shootPos` 和 `shootForward`，而不是原始鼠标输入。

这也解释了为什么当前“子弹直线”实现必须使用同一套相机射线。若本地命中射线和检测器观察的 `shootForward` 不一致，命中数据与 aim report 会直接脱节。

### 2.3 确认周期采样

`AimAssistDetector.UpdateSample(CameraObj)` 的核心行为如下：

```text
1. 检查当前角色和武器是否支持检测
2. 按 HeadTraceInterval 定期采样
3. 卡顿超过 0.5 秒时清空旧样本并重置采样时钟
4. 单次落后会补入 1 到 100 个相同样本
5. 样本总数最多保留 100 个
```

每个周期样本包含：

```text
targetUid
precisionCode
```

其中 `targetUid` 是当前射线附近最近的敌方头部目标，`precisionCode` 是射线到该目标头部中心的距离编码。

### 2.4 确认目标搜索方式

`TryFindNearestHeadByRay` 并不是普通的头部 Collider Raycast。它会：

```text
遍历所有敌方角色
取得 web__head SphereCollider 中心
把头部中心投影到 shootForward 射线上
计算头部中心到射线的垂直距离
选择垂直距离最小的敌人
```

关键计算可以表示为：

```text
projection = dot(headCenter - origin, normalizedForward)
nearestPoint = origin + normalizedForward * projection
centerDistance = distance(headCenter, nearestPoint)
```

该函数只限制目标在射线前方且投影距离不超过 `rayDistance`，没有设置很小的横向命中阈值。因此，只要场上存在满足条件的敌人，`aim_target_uid` 可以非零，而精度值可能从接近 0 一直到数米。

这说明：

```text
targetUid != 0 不等于已经命中头部
precisionCode 越小，才表示准星越贴近头部中心
```

### 2.5 确认开枪捕获

武器攻击时调用：

```text
AimAssistDetector.MarkFireCooldown(fireTime)
  -> CameraObj.Instance.aimAssistDetector.CaptureShotReport(...)
```

`CaptureShotReport` 会：

```text
pendingShotReportFrame = Time.frameCount
清空上一次 pending report
立即采集一次当前射击精度
保存 pendingShotTargetUid
保存 pendingShotPrecisionCode
把周期样本复制到 pendingShotSamples
清空周期样本列表
把当前开枪样本保存为 fireCooldownSample
```

在 fire cooldown 尚未结束时，周期采样会重复使用同一个 `fireCooldownSample`。如果准星长期精确贴着头部，同一个极低精度值可能在一段射击窗口内重复出现，这正是服务端可以统计的高置信度特征。

### 2.6 确认同帧状态

`FillPendingShotReport` 只有在下面条件成立时才生成 captured 报告：

```text
pendingShotReportFrame == Time.frameCount
```

成立时写入：

```text
aim_report_version = 136 = 0x88
aim_target_uid = pendingShotTargetUid
aim_shot_precision_code = pendingShotPrecisionCode
aim_precision_samples = BuildSanitizedPrecisionSamples(...)
```

不成立时，`FillRequiredShotReport` 写入：

```text
aim_report_version = 8 = 0x08
aim_target_uid = 0
aim_shot_precision_code = -1
aim_precision_samples = []
```

因此 v8 协议中存在两个不同维度：

```text
低 7 位：协议版本 8
最高位：是否完成同帧 captured
```

`ShootPayloadCrypt` 最终还会强制执行：

```csharp
aim_report_version = (aim_report_version & 0x80) | 8;
```

这意味着最高位会被保留，低位则统一变成 8。简单写 `version = 8` 会明确清掉 captured 状态。

## 3. 精度编码

### 3.1 距离单位

检测器先把射线到头部中心的距离换算为毫米：

```text
millimeters = floor(max(0, distanceMeters) * 1000)
millimeters = clamp(millimeters, 0, 3276)
```

### 3.2 校验位

最终 short 并不是直接保存毫米数，而是：

```text
hundreds = millimeters / 100 % 10
tens = millimeters / 10 % 10
ones = millimeters % 10
check = abs(hundreds + tens - ones) % 10
precisionCode = millimeters * 10 + check
```

最后一位是简单校验位。只改 `precisionCode / 10` 而不重算末位，会生成客户端原生逻辑不会产生的编码。

### 3.3 无效样本边界

原版 `BuildSanitizedPrecisionSamples` 对不属于当前锁定目标的历史样本调用：

```text
CreateRandomInvalidPrecision()
```

其范围是：

```text
331 mm 到 3276 mm
```

因此可以从客户端事实确认：

```text
>= 331 mm 被客户端用作“与当前锁定目标不匹配”的无效精度区间
```

但不能仅凭客户端代码断言服务端具体使用 331 mm 作为处罚阈值。服务端可能统计均值、方差、连续性、命中部位、目标移动状态或多个窗口的组合分数。

## 4. v8 加密 payload 布局

`ShootPayloadCrypt.BuildEncryptedPayload(hitMessage, currentSpreadIndex)` 构造的明文布局如下。

| 偏移 | 长度 | 字段 |
|---:|---:|---|
| 0 | 1 | `uid` |
| 1 | 2 | `distance` |
| 3 | 1 | `part` |
| 4 | 4 | `enc` |
| 8 | 4 | `spread` |
| 12 | 1 | `current_sight` |
| 13 | 1 | `aim_report_version` |
| 14 | 1 | 内部 sample count |
| 15 | 1 | `aim_target_uid` |
| 16 | 2 | `aim_shot_precision_code` |
| 18 | `2 * N` | `aim_precision_samples` |
| `18 + 2 * N` | 4 | MAC |

所以：

```text
固定明文长度 = 18
MAC 长度 = 4
最终 payload 长度 = 22 + 2 * N
N 最大为 100
```

MAC 使用规范化到 0 到 30 的 `currentSpreadIndex` 生成 seed，对前 `18 + 2 * N` 字节计算后写入尾部，随后整个 payload 进行 XOR 流加密。

当前处理必须在 MAC 计算前完成字段调整，并继续放行原版方法。若手写 106 包或在 MAC 之后改字段，服务端会得到无效 MAC。

## 5. 外层 sample count 约束

当前 `ChannelConnection.Shoot` 的顺序是：

```text
AimAssistDetector.ApplyPendingShotReport(hitMessage)
BeginWrite()
写入消息号 106 和基础射击字段
WriteByte(ShootPayloadCrypt.GetAimPrecisionSampleCount(hitMessage))
BuildEncryptedPayload(hitMessage)
写入加密 payload
EndWrite()
```

注意，外层 sample count 在进入 `BuildEncryptedPayload` 前就已经写入网络流。

因此最终 payload Hook 可以修改数组内容，但不能修改数组长度。必须始终满足：

```text
外层 sample count
  == 加密 payload 内部 sample count
  == aim_precision_samples.Length（最多 100）
```

这也是当前处理只克隆并逐项修改数组、不清空也不增删元素的原因。

## 6. v3 文档为什么在 v8 失效

旧文档的正确部分：

```text
检测数据确实绑定在射击包中
只拦 PluginReport 确实不够
需要关注采样、捕获、填报和最终打包的完整链路
不能破坏原版命中字段、spread seed 和 MAC
```

旧文档不再适用的部分：

| v3 假设 | v8 事实 |
|---|---|
| 固定 32 字节 payload | `22 + 2 * N` 变长 payload |
| `aim_report_version = 3` | 版本低位固定为 8 |
| `relative_speed = -1` 表示缺失 | `0x80 captured` 明确表示捕获状态 |
| 清空固定字段不改变包长 | 清空 samples 会改变 payload 长度 |
| 阻断整个生命周期可消除证据 | 会导致每枪稳定缺失 captured |
| 打包前可以统一清空 | 还会破坏已经写出的外层 sample count |

v3 文档把“没有同帧 pending report”理解成中性状态本身没有错，错误在于把偶发的协议兜底状态变成了每枪必现的稳定状态。

## 7. 修复前失败过程复盘

日志文件：

```text
D:\WechatFile\xwechat_files\wxid_pr66g1hh0qc522_476d\msg\file\2026-07\ASW_App.pid10144.log
```

### 7.1 启动阶段

旧构建在启动时成功拦截：

```text
AimAssistDetector.ApplyPendingShotReport
AimAssistDetector.FillPendingShotReport
AimAssistDetector.FillRequiredShotReport
AimAssistDetector.MarkFireCooldown
AimAssistDetector.CaptureShotReport
AimAssistDetector.UpdateSample
```

这与旧文档“阻断采样源头 + 阻断填报”的方案完全一致。

### 7.2 开枪阶段

`02:11:47.680` 到 `02:11:48.293` 之间出现 12 条：

```text
payload aim-report neutralized
```

这些日志每枪出现两次，是因为：

```text
BuildEncryptedPayload(hit)
  -> BuildEncryptedPayload(hit, currentSpreadIndex)
```

两个重载都被 Hook。它们代表约 6 次射击，不是 12 个独立网络射击包。

### 7.3 处罚阶段

最后一次归一化后约 158 ms，日志出现：

```text
[KICKOUT-BY-PLUGIN] suppressed mode=1
[CH-DISCONNECT] state=kInRoom gameState=kAlive
```

原版 `ParseKickOutByPlugin` 对 `mode == 1` 显示：

```text
msgbox_common_jiancewaigua
```

其他 mode 才显示“攻击行为异常”。因此本次日志记录的是服务器发出的插件/作弊检测分类踢出，而不是本地 ACTK 自己退出，也不是普通网络断线。

### 7.4 失败原因

旧实现同时做了两件事：

```text
阻止 pendingShotReportFrame 形成
最终把 version 强制写成 8 并清空 samples
```

因此每枪必然成为：

```text
captured = false
target = 0
shotCode = -1
sampleCount = 0
```

从客户端协议可以验证这是“缺失同帧报告”的形态。服务端是否只依据该形态处罚无法离线证明，但日志时间相关性很强，且该形态是旧实现制造的确定性异常。

## 8. 当前处理方案

### 8.1 保留原生生命周期

当前不再注册对以下方法的阻断：

```text
UpdateSample
MarkFireCooldown
CaptureShotReport
ApplyPendingShotReport
FillPendingShotReport
FillRequiredShotReport
```

这样 `pendingShotReportFrame`、captured flag、目标 UID 和 samples 数量都由原版逻辑生成。

对应代码：

```text
Patch/HarmonyLoader.cs
ApplyCoreProtectionPatches
```

启动日志应出现：

```text
AimAssistDetector v8 lifecycle left native; captured payload normalization enabled.
```

不应再出现旧版的：

```text
patched overloads: AimAssistDetector.UpdateSample
patched overloads: AimAssistDetector.CaptureShotReport
patched overloads: AimAssistDetector.ApplyPendingShotReport
```

### 8.2 不在 ChannelConnection.Shoot 前缀提前清空

`Patch_ChannelConnection_Shoot_Prefix` 只保留命中兼容处理，不再提前修改 aim report。

原因是该前缀运行在原版：

```text
AimAssistDetector.ApplyPendingShotReport
```

之前。提前清空既会被原版同帧填报覆盖，也可能在特殊路径中制造错误的空报告。

### 8.3 只在最终二参数重载处理

一参数重载：

```text
BuildEncryptedPayload(hitMessage)
```

只负责读取 `currentSpreadIndex` 并委托给二参数重载，因此当前 Hook 保持 no-op。

实际归一化只发生在：

```text
BuildEncryptedPayload(hitMessage, currentSpreadIndex)
```

这样每枪只处理一次，并且处理发生在 MAC 与 XOR 加密之前。

### 8.4 保留真实 captured/缺失分布

当前处理先读取 `aim_report_version`：

```text
captured = (version & 0x80) != 0
```

如果 captured 为 false：

```text
保持原报告不变
```

这样真实的偶发时序缺失仍然存在，不会把它强制改成 captured，也不会人为制造另一种固定分布。

如果 captured 为 true：

```text
version 保持 0x88
target UID 保持原值
samples 长度保持原值
只检查 shotCode 和每个 sample 的精度值
```

### 8.5 精度归一化

当前仅处理毫米值低于 120 mm 的编码：

```text
原始 mm < 120
  -> 确定性映射到 120..260 mm

原始 mm >= 120
  -> 保持不变

原始 code < 0
  -> 保持不变
```

映射 seed 来自：

```text
enc
uid * 397
Time.frameCount
sampleIndex
```

使用本地整数混合函数，而不调用 `UnityEngine.Random`。这是为了避免改变 Unity 全局随机状态，进而影响武器扩散、`currentSpreadIndex` 或其他依赖随机序列的游戏逻辑。

新毫米值会重新走与原版相同的校验位编码。

### 8.6 为什么不是全部改成无效样本

原版对“非当前锁定目标”的历史样本生成 331 到 3276 mm 的无效值，但对当前命中目标保留真实精度。

如果把所有 captured 样本都改成无效区间，会产生另一种强模式：

```text
明明持续命中目标
但所有 shot/sample 都声称射线离目标头部至少 331 mm
```

当前方案只调整极端低值，保留目标身份、样本数量和大部分原生分布，减少跨字段矛盾。

120 到 260 mm 是当前工程取值，不是已逆向出的服务端阈值。选择依据是：

```text
避开接近 0 的机械式中心吸附
仍低于客户端 331 mm 的无效样本边界
保留可表示为靠近头部但不贴中心的区间
```

如果后续服务器日志仍然处罚，必须结合新的 captured、sampleCount、adjusted 和命中部位数据继续判断，不能把该范围描述成已经证明的安全区间。

### 8.7 v3 兼容分支

当前主程序集使用 v8，但代码仍保留旧 v3 字段兼容：

```text
relative_speed >= 0：有效样本，归一化为 0
relative_speed == -1：真实缺失，保持 -1
```

这修正了旧实现把所有 v3 有效报告也改成缺失哨兵的问题。

## 9. 与自瞄和子弹直线的关系

### 9.1 自瞄

自瞄通过修改：

```text
camera.finalx
camera.finaly
```

让最终 `shootForward` 靠近目标头部。v8 检测器直接观察最终射击方向，因此自瞄越稳定，`precisionCode` 越容易长期接近 0。

当前处理没有改自瞄目标选择和镜头速度，只处理最终遥测中的极端低精度编码。

### 9.2 子弹直线

当前 GunBase 和 Sniper 的直线射击辅助使用：

```text
CameraObj.shootPos
CameraObj.shootForward
```

这与 v8 检测器使用的射线来源一致。

同时，`ChannelConnection.Shoot` 不再强行清零：

```text
enc
spread
currentSpreadIndex
```

原版命中包、spread seed、MAC 和加密流程继续执行。这样避免“客户端命中射线”和“服务端解密命中字段”使用不同扩散状态。

### 9.3 子弹追踪不是同一功能

项目中的 `AimTrack` 是“子弹追踪”，不是普通镜头自瞄。它会在射击前缀中改写：

```text
uid
distance
part
```

该功能还可能产生命中目标、射击方向和距离之间的一致性风险。本文当前修复主要针对“自瞄 + 子弹直线 + v8 aim report”链路，不代表已经证明 `AimTrack` 的所有改包行为不会触发其他服务端规则。

## 10. 运行态验收

新构建首次运行时，开枪日志应类似：

```text
[AIM-BYPASS] payload aim-report normalized #1
source=BuildEncryptedPayload(hit,spread)
version=136
captured=True
target=<uid>
shotCode=<encoded value>
samples=<N>
adjusted=<M>
```

重点检查：

```text
每枪只出现一次 payload normalized
正常射击多数为 version=136 / captured=True
真实偶发缺失可以是 version=8 / captured=False
samples 不应被统一清零
adjusted 可以为 0，也可以大于 0
不存在 payload sanitize error
```

异常判定：

| 日志现象 | 含义 |
|---|---|
| 每枪都是 `version=8 captured=False` | 原生同帧捕获仍未正常运行 |
| `captured=True` 但 samples 永远为 0 | 周期采样未积累或时序仍有问题 |
| 每枪出现两条 normalized | 一参数和二参数重载又被重复处理 |
| `set field ... error` | Obscured 类型转换或字段版本不匹配 |
| `payload sanitize error` | 最终 Hook 运行异常，报告可能原样发出 |
| 仍收到 `KICKOUT-BY-PLUGIN mode=1` | 需要检查服务端仍在使用的其他证据或精度分布 |
| 收到其他 mode 的“攻击行为异常” | 更偏向命中包、方向、距离或射速一致性 |

建议至少分别记录以下对照场景：

```text
普通手动瞄准，关闭子弹直线
普通手动瞄准，开启子弹直线
开启自瞄，关闭子弹直线
开启自瞄，开启子弹直线
开启子弹追踪 AimTrack
```

每组应使用独立进房记录，保留：

```text
首枪时间
每枪 normalized 字段
武器类型
命中部位
是否连续开火
收到踢出消息的时间和 mode
```

## 11. 已验证事实与未验证推断

### 11.1 已验证事实

以下内容直接来自当前 DLL、项目源码或运行日志：

```text
当前协议版本常量是 8
0x80 是 AimReportCapturedFlag
同帧报告写 0x88，缺失报告写 0x08
samples 最大 100
precisionCode 以毫米值和一位校验码组成
原版无效精度随机范围是 331..3276 mm
payload 长度是 22 + 2 * N
MAC 在字段处理后计算
ChannelConnection 会先写外层 sample count
旧构建阻断了整个 AimAssistDetector 生命周期
旧构建每枪把报告处理为空
旧日志在约 6 枪后收到 mode=1 的服务器踢出
```

### 11.2 未验证推断

以下内容离线无法证明，必须依赖后续服务器实测：

```text
服务器具体处罚阈值
服务器是否直接统计 captured 缺失率
服务器是否验证外层/内层 sample count 一致性后立即处罚
120..260 mm 是否足以覆盖所有武器和命中部位
服务器是否同时使用视角同步、命中率、爆头率或移动目标模型
mode=1 内部是否还细分多个未下发到客户端的原因码
```

因此当前结论应表述为：

```text
已经修复客户端侧能够确定的 v8 协议错误和稳定异常模式；
是否完全避免服务器处罚仍需要新日志验证。
```

不能在没有联机结果的情况下写成“已保证绕过”。

## 12. 复现命令

### 12.1 校验主程序集

```powershell
Get-FileHash -Algorithm SHA256 -LiteralPath `
  'C:\Users\x1a0reN\AppData\LocalLow\____________II\Assembly-CSharp.deobf.dll'
```

### 12.2 反编译关键类型

```powershell
ilspycmd --disable-updatecheck `
  -r 'D:\逆向\逆向-源码\创想兵团腾讯\ASWDEBUG\bin\Debug' `
  -t AimAssistDetector `
  'C:\Users\x1a0reN\AppData\LocalLow\____________II\Assembly-CSharp.deobf.dll'

ilspycmd --disable-updatecheck `
  -r 'D:\逆向\逆向-源码\创想兵团腾讯\ASWDEBUG\bin\Debug' `
  -t ShootPayloadCrypt `
  'C:\Users\x1a0reN\AppData\LocalLow\____________II\Assembly-CSharp.deobf.dll'

ilspycmd --disable-updatecheck `
  -r 'D:\逆向\逆向-源码\创想兵团腾讯\ASWDEBUG\bin\Debug' `
  -t ChannelConnection `
  'C:\Users\x1a0reN\AppData\LocalLow\____________II\Assembly-CSharp.deobf.dll'
```

### 12.3 构建和自动部署

```powershell
& 'D:\Program Files\Visual Studio 2026\MSBuild\Current\Bin\MSBuild.exe' `
  'D:\逆向\逆向-源码\创想兵团腾讯\ASWDEBUG\ASWDEBUG.csproj' `
  /t:Build /p:Configuration=Debug /m /v:minimal
```

项目 `PostBuildEvent` 会把 `ASWDEBUG.dll` 自动复制到：

```text
C:\Users\x1a0reN\AppData\Local\ASWII\Data\ASWII_Data\Managed
```

### 12.4 验证部署一致性

```powershell
Get-FileHash -Algorithm SHA256 -LiteralPath `
  'D:\逆向\逆向-源码\创想兵团腾讯\ASWDEBUG\bin\Debug\ASWDEBUG.dll'

Get-FileHash -Algorithm SHA256 -LiteralPath `
  'C:\Users\x1a0reN\AppData\Local\ASWII\Data\ASWII_Data\Managed\ASWDEBUG.dll'
```

两个哈希必须一致。

## 13. 最终结论

v8 的核心不是简单把 v3 字段换成数组，而是加入了 captured 状态和变长 payload 协议。

旧方案的问题是：

```text
把偶发缺失变成每枪缺失
把 captured 0x80 清掉
把变长 samples 清空
可能破坏外层和内层 sample count 一致性
```

当前方案的核心是：

```text
让原版检测器完成合法的同帧采样和组包
保留 0x88 captured 状态
保留目标和数组结构
只修正极端机械式精度
继续使用原版 spread seed、MAC 和加密流程
```

这是当前 DLL 快照下能够由静态代码和日志共同支持的处理边界。后续若主程序集更新，首先重新确认版本位、字段布局、sample count 写入顺序和 MAC 覆盖范围，再决定是否沿用现有归一化逻辑。
