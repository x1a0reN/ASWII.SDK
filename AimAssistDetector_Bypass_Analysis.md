# 新版自瞄检测链逆向分析与绕过思路

这版检测的关键点，不是客户端里多了几个叫 `AimAssistDetector` 的函数这么简单。

真正麻烦的地方在于：它没有把自瞄检测单独做成一个显眼的举报接口，而是把“瞄准行为特征”塞进每次开枪的命中包里，跟射击数据一起加密发到服务端。也就是说，表面上看还是正常开枪，实际上每一枪都在带一份行为遥测。

所以这次要处理的不是“有没有自瞄函数”，而是这条从采样、捕获、填报到加密打包的完整遥测链。

## 1. 新版自瞄检测链怎么工作

从程序集结构看，检测链路可以拆成四段：

```text
CameraObj.LateUpdate
  -> AimAssistDetector.UpdateSample(camera, frameTime)

Weapon Attack
  -> AimAssistDetector.MarkFireCooldown(fireTime)
     -> AimAssistDetector.CaptureShotReport(camera, fireTime)

ChannelConnection.Shoot(...)
  -> AimAssistDetector.ApplyPendingShotReport(hitMessage)
     -> AimAssistDetector.FillRequiredShotReport(...)
     -> AimAssistDetector.FillPendingShotReport(...)

ShootPayloadCrypt.BuildEncryptedPayload(hitMessage)
  -> aim_report_* 写入加密 payload
```

第一段发生在镜头更新阶段。

`CameraObj.LateUpdate` 会持续把当前镜头状态交给 `AimAssistDetector.UpdateSample`。这里采的不是单帧结果，而是一段时间内的瞄准行为：准星是否长时间贴着敌人头部、目标是否在移动、相对速度是多少、锁定持续了多久、头部精度是否异常稳定。

第二段发生在开枪前后。

武器攻击逻辑会调用 `MarkFireCooldown`，然后在开枪帧附近触发 `CaptureShotReport`。这一步的意义很明确：平时采样只是积累状态，真正要上报的是“这一枪打出去时，准星到底处在什么状态”。

第三段发生在射击 RPC 组包之前。

`ChannelConnection.Shoot` 走到命中包构造时，会调用 `ApplyPendingShotReport`。如果前面已经有 pending report，它就把那份报告写进 `HitMessage`；如果没有，也会走 `FillRequiredShotReport` 补齐协议要求的字段。

第四段是最终打包。

`ShootPayloadCrypt.BuildEncryptedPayload(hitMessage)` 会把 `HitMessage` 里的 `aim_report_*` 字段写进 32 字节加密 payload。服务端拿到的不是明文 Lua 或普通日志，而是跟命中数据绑定在一起的加密射击包。

关键字段主要是这些：

```text
aim_report_version
aim_lock_session_id
aim_lock_duration_ms
aim_lock_target_uid
aim_target_uid
aim_relative_speed_cmps
aim_head_precision_mm
```

这些字段组合起来，足够还原一次射击时的瞄准特征。

自瞄最容易暴露的点也在这里：准星持续吸附同一个目标，锁定时长稳定增长，目标相对移动时仍保持极高头部精度。正常手动瞄准会有抖动、丢失、修正和误差；自瞄的轨迹太干净，统计上很容易被抓出来。

## 2. 为什么只拦 PluginReport 不够

旧思路一般会盯 `PluginReport` 这类名字明显的上报函数。

但这版不一样。

`PluginReport` 就算被拦住，只要 `ShootPayloadCrypt.BuildEncryptedPayload` 仍然把异常的 `aim_report_*` 打进射击包，服务端照样能看到这一枪的锁头统计。

这也是这版检测比较阴的地方：它不靠单独举报包，而是把检测数据并入核心战斗协议。你只看普通上报接口，会以为已经干净了；实际上每次开枪都还在把证据送出去。

所以正确切入点不是“拦举报”，而是拆完整链路：

```text
采样源头 -> 开枪帧报告 -> HitMessage 填报 -> 加密 payload
```

只断一段不保险，必须按链路分层处理。

## 3. 这次绕过的三层保护

### 第一层：阻断采样源头

第一层处理的是检测数据的来源。

重点位置：

```text
AimAssistDetector.UpdateSample
AimAssistDetector.CaptureShotReport
AimAssistDetector.MarkFireCooldown
AimAssistDetector.SetRelativeSpeedThreshold
```

`UpdateSample` 是持续采样点，负责积累锁头状态。

`CaptureShotReport` 是开枪帧捕获点，负责把当前采样变成一次 pending shot report。

`MarkFireCooldown` 会参与射击时间窗口标记，辅助检测器确认哪一帧属于有效开火。

`SetRelativeSpeedThreshold` 则说明服务端或者配置侧可以调整相对速度阈值。这个点不能随便放行，否则检测参数可能热更新。

第一层的目标很简单：不让检测器持续形成“锁头时长”和“开枪瞬间头部精度”这类数据。

注意，这里不应该改瞄准动作本身。镜头最终怎么转、目标怎么选、吸附速度是多少，都不在这一层动。否则看起来像是绕过了检测，实际上只是把自瞄手感改烂了。

### 第二层：阻断填报

第二层处理的是检测数据写入 `HitMessage` 的过程。

重点位置：

```text
AimAssistDetector.ApplyPendingShotReport
AimAssistDetector.FillPendingShotReport
AimAssistDetector.FillRequiredShotReport
```

这几个函数的作用，是在 `ChannelConnection.Shoot` 组包时，把 pending report 或 required report 写进命中消息。

如果第一层没挡住，第二层也能挡掉大部分有效报告。

如果第一层已经挡住，第二层则负责防止协议补齐逻辑重新塞入异常值。

这里的处理原则是 no-op：让射击流程继续走，但不让检测报告污染 `HitMessage`。

也就是说，战斗包还是战斗包，命中流程还是命中流程，只是不再携带有效的自瞄行为证据。

### 第三层：打包前最终净化

第三层是兜底，也是最关键的一层。

重点位置：

```text
ShootPayloadCrypt.BuildEncryptedPayload(hitMessage)
```

最新版 IL 里可以看到，它会按新协议把 aim report 字段写进加密 payload，而且会强制处理 `aim_report_version`。

所以不能只依赖前两层。只要某个路径漏了一次，最终加密包里还是可能带出脏字段。

打包前需要把 `HitMessage` 里的 aim report 字段统一净化成“无有效检测样本”的形态：

```text
aim_report_version       = 3
aim_lock_session_id      = 0
aim_lock_duration_ms     = 0
aim_lock_target_uid      = 0
aim_target_uid           = 0
aim_relative_speed_cmps  = -1
aim_head_precision_mm    = -1
```

这不是随便填。

从 `FillRequiredShotReport` 的逻辑看，在没有同帧 pending report 的情况下，它本身也会填类似的中性值：目标 UID 为 0，锁定 session 为 0，锁定时长为 0，相对速度和头部精度为 -1。

也就是说，这组值表达的不是“老协议”或“异常协议”，而是“本次射击没有有效 aim-assist 检测样本”。

这点很重要。绕过检测最忌讳乱填字段。乱填虽然可能短时间不报错，但很容易被服务端用协议一致性抓出来。

## 4. 为什么保留 aim_report_version = 3

`aim_report_version` 不能简单清零。

新版 `ShootPayloadCrypt.BuildEncryptedPayload` 已经按 v3 结构打包，而且它自己会把版本往 v3 写。如果硬改成 0 或伪装旧版，服务端看到的就不是“没有检测样本”，而是“客户端协议字段不符合当前版本”。

这种异常比自瞄特征还显眼。

正确策略是：

```text
协议版本保持 v3
检测指标保持空样本
```

这样服务端看到的是一个结构完整、版本正确、但没有有效 aim report 证据的射击包。

简单说：

```text
不要装成旧客户端
要装成新版客户端里一次没有采到有效检测样本的普通射击
```

这才是稳定策略。

## 5. 为什么不改自瞄动作

这条链路跟瞄准动作本身是分开的。

镜头修正一般发生在类似下面这些位置：

```text
camera.finalx
camera.finaly
target bone / head point
aim speed
target selection
hotkey state
```

检测器只是观察这些行为，然后统计成报告。

如果为了绕检测去改镜头吸附、目标选择或速度曲线，那就本末倒置了。手感变了，命中表现也变了，最后只能算削弱功能，不算真正绕过。

这次的核心是只切遥测链，不碰瞄准链。

也就是：

```text
自瞄照常工作
检测器采不到有效样本
HitMessage 不携带异常统计
加密射击包保持 v3 结构但 aim report 为空
```

这个边界必须分清。

## 6. 证据链怎么确认

静态上看，证据链主要看四类点：

第一类，采样入口是否存在：

```text
CameraObj.LateUpdate -> AimAssistDetector.UpdateSample
```

第二类，开枪帧是否捕获报告：

```text
Weapon Attack -> MarkFireCooldown -> CaptureShotReport
```

第三类，射击 RPC 是否填入报告：

```text
ChannelConnection.Shoot -> ApplyPendingShotReport
```

第四类，加密打包是否读取字段：

```text
ShootPayloadCrypt.BuildEncryptedPayload -> aim_report_* -> encrypted payload
```

运行态确认则看这些点有没有命中：

```text
AimAssistDetector.UpdateSample blocked
AimAssistDetector.CaptureShotReport blocked
AimAssistDetector.ApplyPendingShotReport blocked
ShootPayloadCrypt.BuildEncryptedPayload neutralized
```

如果这些点都命中，说明从采样到加密包的链路已经被切干净。

如果仍然触发处罚，那就说明还有第二条检测链，比如：

```text
输入轨迹统计
视角同步包
命中率/爆头率行为模型
服务端侧移动目标命中模型
连发时间窗口统计
```

但至少 `aim_report_*` 这条客户端遥测链，已经不是有效证据来源。

## 7. 总结

这版检测的核心不是函数名，也不是普通插件上报，而是把自瞄行为特征混进射击加密包里。

绕过思路必须按完整链路处理：

```text
阻断采样源头
阻断 HitMessage 填报
打包前净化 aim_report_*
```

`aim_report_version` 保留 v3，是为了维持当前协议结构；其他指标置为中性值，是为了表达“本次没有有效检测样本”，而不是伪造旧协议。

这套处理的关键点只有一个：不碰瞄准动作，只断检测遥测。

只要射击包结构正常、版本正确、检测字段为空，服务端就拿不到那组最直接的锁头统计证据。
