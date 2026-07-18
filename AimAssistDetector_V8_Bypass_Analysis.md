# AimAssistDetector v8 检测链逆向分析与规避思路（脱敏版）

## 1. 文档范围与脱敏说明

本文记录一次针对 Unity/.NET 客户端中瞄准辅助检测链的静态逆向结果，目的是解释客户端如何采集瞄准数据、如何把数据绑定到一次射击，以及研究补丁应当在哪个边界内工作。

为避免泄露样本来源，本文不包含：

- 产品名称、发行渠道和运营主体；
- 本机用户名、目录、进程号和日志路径；
- 服务地址、账号信息、授权信息和部署结构；
- 样本文件哈希、项目名称和构建产物名称。

文中的类型名和字段名只保留理解数据流所必需的部分。示例代码由反编译结果整理而来，局部变量名经过规范化，不代表原开发者使用的源码命名。

本文只讨论客户端侧可验证事实。服务端的判定模型、阈值、权重和处罚策略不可从客户端程序集直接证明，相关内容均标为推测。

## 2. 核心结论

该检测并不是简单判断“准星是否指向敌人”，而是建立了一条完整的射击遥测链：

```text
相机最终射击向量
  -> 周期采样目标与头部偏差
  -> 开火时冻结本次报告
  -> 同帧填入 HitMessage
  -> 写入外层样本数量
  -> 构造内部明文
  -> 计算 MAC
  -> 加密
  -> 随射击包发送
```

协议中最重要的状态不是单个精度值，而是以下字段之间的一致性：

```text
aim_report_version
aim_target_uid
aim_shot_precision_code
aim_precision_samples.Length
外层 sample count
内部 payload sample count
payload 长度
MAC
加密所用的 spread index
```

研究中最容易犯的错误是直接阻断采样或把报告清空。这样虽然消除了低偏差值，却会制造另一种稳定特征：每一枪都缺少同帧报告、每一枪样本数都为零，或者外层长度与加密 payload 不一致。

更稳妥的处理边界是：

```text
保留原生采样生命周期
保留真实的 captured/missing 状态
保留目标、样本数量和数组长度
只对异常集中的精度编码做有限归一化
让原生构造器重新计算 MAC 并完成加密
```

## 3. 逆向方法

分析采用以下步骤：

1. 枚举 `HitMessage` 中与 aim report 有关的字段。
2. 查找这些字段的全部写入者。
3. 从写入者回溯到采样入口和开火入口。
4. 从 `ChannelConnection.Shoot` 前向跟踪到 payload 构造与加密。
5. 对外层写包顺序和内部明文布局做字节级核对。
6. 区分客户端确定事实与服务端统计推测。
7. 在最终加密边界前验证最小修改方案。

关键调用关系如下：

```text
CameraObj.LateUpdate
  -> AimAssistDetector.UpdateSample

Gun/Bow/RPG/Shotgun.Attack
  -> AimAssistDetector.MarkFireCooldown
  -> AimAssistDetector.CaptureShotReport

ChannelConnection.Shoot
  -> AimAssistDetector.ApplyPendingShotReport
  -> ShootPayloadCrypt.GetAimPrecisionSampleCount
  -> ShootPayloadCrypt.BuildEncryptedPayload
```

## 4. 报告字段

`HitMessage` 中与该检测直接相关的字段为：

```csharp
public ObscuredByte aim_report_version;
public ObscuredByte aim_target_uid;
public ObscuredShort aim_shot_precision_code;
public short[] aim_precision_samples;
```

字段语义可由客户端写入逻辑确认：

| 字段 | 客户端可确认的语义 |
|---|---|
| `aim_report_version` | 低位保存协议版本；最高位表示本次是否完成同帧捕获 |
| `aim_target_uid` | 开火瞬间，检测射线认为最接近的敌方目标 |
| `aim_shot_precision_code` | 开火瞬间射线到目标头部中心的距离编码 |
| `aim_precision_samples` | 本次开火前积累的周期精度样本 |

`ObscuredByte` 和 `ObscuredShort` 是包装类型。反射读取时不能假定 `Convert.ToInt32` 一定有效，应调用包装类型公开的隐式转换运算符。否则日志可能把真实非零值错误显示为零。

## 5. 采样入口

相机在 `LateUpdate` 中完成鼠标输入、灵敏度、视角限制和相机变换后，才更新：

```csharp
shootPos = transform.position;
shootForward = transform.forward;
shootUp = transform.up;
shootRight = transform.right;

if (aimAssistDetector != null)
{
    aimAssistDetector.UpdateSample(this);
}
```

这说明检测器看到的是游戏最终用于射击的相机向量，而不是原始鼠标增量。任何在更早阶段伪造输入、但最终没有改变 `shootForward` 的方案，都不会改变检测器看到的方向。

反过来，如果射击功能把实际命中射线改到另一个方向，而 `shootForward` 仍指向原处，服务端可以观察到命中目标与 aim report 之间的语义脱节。

## 6. 周期采样

`UpdateSample` 的等价逻辑为：

```csharp
if (camera == null || camera.character == null || !IsSupportedWeapon(camera.character))
{
    ResetDetectionState();
    return;
}

float now = Time.time;
if (nextSampleTime <= 0f)
    nextSampleTime = now;

if (now >= nextSampleTime)
{
    if (now - nextSampleTime > 0.5f)
    {
        samples.Clear();
        nextSampleTime = now;
    }

    PrecisionSample sample = CapturePeriodicPrecisionSample(camera, now);
    int count = 1 + Floor((now - nextSampleTime) / sampleInterval);
    count = Clamp(count, 1, 100);

    for (int i = 0; i < count; i++)
        AddPrecisionSample(sample);

    nextSampleTime += count * sampleInterval;
}
```

可确认的行为包括：

- 只对指定远程武器类型采样；
- 卡顿超过 0.5 秒时清空积压样本；
- 单次更新最多补入 100 个样本；
- 队列最多保留 100 个样本；
- 开火冷却窗口内可以重复使用开火时冻结的样本。

样本间隔字段的具体运行值不能仅由该静态快照可靠确认，应通过运行时读取或时间序列日志测量。

## 7. 目标搜索几何

检测器不是依赖一次普通的头部 Collider Raycast，而是遍历敌方角色并计算头部中心到射线的垂直距离。

核心公式为：

```text
forward = normalize(shootForward)
toHead = headCenter - origin
projection = dot(toHead, forward)
nearestPoint = origin + forward * projection
centerDistance = distance(headCenter, nearestPoint)
```

候选目标必须满足：

```text
不是本地角色
未死亡
不属于同一队伍
能取得头部球形碰撞体中心
projection >= 0
projection <= rayDistance
```

随后选择 `centerDistance` 最小的目标。

因此：

```text
aim_target_uid != 0
```

只表示检测射线前方存在一个“最靠近射线”的敌人，并不等价于已经命中头部。真正描述准星贴近程度的是精度编码所代表的毫米距离。

## 8. 开火捕获与同帧标志

武器开火时调用：

```csharp
AimAssistDetector.MarkFireCooldown(fireTime);
```

该调用最终执行 `CaptureShotReport`：

```csharp
pendingShotReportFrame = Time.frameCount;
ResetPendingShotReport();

ResetHeadTraceCache();
Character target = GetNearestEnemyByRay(camera.shootPos, camera.shootForward, out distance);
PrecisionSample shot = CreatePrecisionSample(target, distance);

pendingShotTargetUid = shot.targetUid;
pendingShotPrecisionCode = shot.precisionCode;
pendingShotSamples.AddRange(periodicSamples);
periodicSamples.Clear();

fireCooldownSample = shot;
fireCooldownActive = fireTime > 0f;
fireCooldownEndTime = Max(fireCooldownEndTime, Time.time + Max(0f, fireTime));
```

网络层稍后请求填充报告时，检测器比较：

```text
pendingShotReportFrame == Time.frameCount
```

同帧成立时：

```text
aim_report_version = 0x88
aim_target_uid = captured target
aim_shot_precision_code = captured precision
aim_precision_samples = captured periodic samples
```

同帧不成立时：

```text
aim_report_version = 0x08
aim_target_uid = 0
aim_shot_precision_code = -1
aim_precision_samples = []
```

其中：

```text
0x88 = 0x80 captured flag + version 8
0x08 = version 8 without captured flag
```

`0x08` 是客户端真实存在的 timing miss 状态，不应被全部改成 captured；同样，也不应把真实 `0x88` 全部改成 `0x08`。

## 9. 精度编码

距离先从米转换为毫米并限制到 `0..3276`：

```csharp
int mm = Clamp(Floor(Max(0, distanceMeters) * 1000), 0, 3276);
```

随后添加十进制校验位：

```csharp
int hundreds = mm / 100 % 10;
int tens = mm / 10 % 10;
int ones = mm % 10;
int check = Abs(hundreds + tens - ones) % 10;
short code = (short)(mm * 10 + check);
```

例如：

```text
150 mm -> hundreds=1, tens=5, ones=0, check=6 -> code=1506
237 mm -> hundreds=2, tens=3, ones=7, check=2 -> code=2372
```

因此，修改毫米值后必须重新计算末位。只替换 `code / 10` 而保留旧校验位，会生成原生编码器无法产生的值。

客户端还会把与本次锁定目标不一致的历史样本替换为一个 `331..3276 mm` 的随机无效精度值。这个范围能证明客户端内部如何标记“不属于当前目标”的样本，但不能单独证明服务端处罚阈值。

## 10. 射击包布局

网络层写包顺序为：

```csharp
ApplyPendingShotReport(hitMessage);
WriteByte(messageId);
WriteByte(hitMessage.is_real_man);
WriteInt(hitMessage.robot_uid);
WriteFloat(serverAdjustedTime);
WriteByte(doEffect);
WritePosition(position);
WriteDirection(direction.normalized);
WriteByte(slot);
WriteByte(GetAimPrecisionSampleCount(hitMessage));

byte[] payload = BuildEncryptedPayload(hitMessage);
Write(payload, payload.Length);
```

内部明文固定部分为 18 字节：

| 偏移 | 长度 | 内容 |
|---:|---:|---|
| 0 | 1 | hit uid |
| 1 | 2 | distance |
| 3 | 1 | part |
| 4 | 4 | enc |
| 8 | 4 | spread |
| 12 | 1 | current sight |
| 13 | 1 | aim report version |
| 14 | 1 | sample count |
| 15 | 1 | aim target uid |
| 16 | 2 | shot precision code |
| 18 | `2*N` | precision samples |
| `18+2*N` | 4 | MAC |

总长度为：

```text
22 + 2 * sampleCount
```

构造器会：

1. 保留 version 的 `0x80` 标志，并把低位固定为 8；
2. 把数组长度限制到 100；
3. 序列化固定字段和样本；
4. 使用 spread index 派生种子；
5. 对明文计算 MAC；
6. 对明文和 MAC 整体执行异或流加密。

## 11. 客户端可见的检测信号

从客户端遥测可推测服务端至少能够计算以下特征，但具体使用方式未知：

- captured/missing 比例；
- 单枪精度值分布；
- 一段时间内低精度值的连续性；
- 周期样本与开火样本的相关性；
- 命中目标与 aim target 的一致性；
- 样本数量与开火节奏、帧率和采样周期的一致性；
- 外层长度、内部长度、MAC 和加密种子的协议一致性；
- 不同武器和不同 spread index 下的行为差异。

“持续贴近头部中心”比“偶尔出现低精度值”更可能成为统计信号。单个样本不能说明服务端一定会处罚，稳定分布才是更值得关注的对象。

## 12. 可行的规避边界

在授权研究环境中，一个低破坏性的规避方案应满足：

### 12.1 保留原生生命周期

不阻断：

```text
UpdateSample
MarkFireCooldown
CaptureShotReport
ApplyPendingShotReport
FillRequiredShotReport
```

这样能够保留真实同帧状态、目标选择、样本数量和开火节奏。

### 12.2 在最终加密前修改

合适的边界是：

```text
BuildEncryptedPayload(HitMessage, currentSpreadIndex)
```

此时原生报告已经填充，外层样本数也已经写出。补丁必须保持数组长度不变，只替换必要元素，然后让原生方法继续执行以重新计算 MAC 和加密数据。

### 12.3 只归一化异常集中值

一种研究方案是：

```text
负数 sentinel：保持不变
高于研究阈值的样本：保持不变
低于研究阈值的有效样本：映射到一个有变化的中间区间
```

映射后必须重新生成校验位。阈值和目标区间只是研究参数，不能描述为服务端事实。

### 12.4 保持数组长度

外层 sample count 在 payload builder 之前写出，因此只能：

```text
clone array
replace array[i]
assign array with same Length
```

不能在这个边界执行：

```text
Clear
Resize
RemoveAt
replace with empty array
```

### 12.5 保留原生 captured/missing

只有 `version & 0x80 != 0` 时才处理真实 captured 报告。对 genuine missing report 直接放行，可避免人为制造不可能的同帧状态。

### 12.6 统一实际射线与检测射线

如果实现直线弹道或命中射线修正，最终射线应以 `CameraObj.shootPos` 和 `CameraObj.shootForward` 为基准。否则实际命中方向与 aim report 描述的方向不一致。

## 13. 不可靠方案

### 13.1 完全关闭采样

风险：每枪稳定产生 missing 或空数组，形成新的固定指纹。

### 13.2 把所有字段清零

风险：目标、精度、样本和 captured 状态失去自然变化；零值本身不等于中性值。

### 13.3 最终阶段清空数组

风险：外层 sample count 已写入，内部 payload 长度与外层不一致。

### 13.4 手写完整射击包

风险：容易遗漏字段顺序、长度、MAC、加密种子和 spread index 归一化。

### 13.5 修改密文

风险：任何字节变化都会破坏 MAC，除非完整复现明文、MAC 和加密流程。

### 13.6 固定映射到单一值

风险：把低精度样本全部映射到同一个数字，会从“过度精确”变成“异常离散尖峰”。

## 14. 验证要求

静态验证至少应检查：

- Hook 位于双参数 payload builder 之前；
- 单参数 overload 不重复处理；
- captured bit 保留；
- missing report 不被强制改写；
- 数组长度保持不变；
- 校验位由原算法重新计算；
- 原生方法继续执行；
- MAC 和加密仍由原生构造器完成。

动态验证应分组记录：

```text
无辅助功能
仅瞄准辅助
仅弹道修正
两者同时开启
```

每组至少观察：

```text
version
captured flag
target uid
shot precision
sample count
sample distribution
payload length
是否出现字段转换或写入错误
连接结束原因
```

不要只根据“没有立即断开”判定成功。应确认协议形状自然、构造器完成、没有 MAC/长度异常，并进行足够长的对照测试。

## 15. 防守方改进建议

该客户端设计把较多语义决策留在本地，因此存在最终加密前篡改的窗口。防守方可考虑：

- 将关键采样或摘要移到更难被托管层 Hook 的可信边界；
- 使用会话级挑战值绑定 aim report、射击包和服务器时间；
- 对客户端完整性、方法体和 Hook 状态做多点交叉验证；
- 服务端按玩家、武器、帧率和距离建立分布模型，而不是依赖单一阈值；
- 对 captured/missing、样本数、命中目标和弹道结果做联合一致性检查；
- 避免把无效区间和编码规则全部暴露在同一托管程序集内。

## 16. 结论

该检测的关键不在某一个字段，而在“相机采样、开火冻结、同帧填报、外层长度、内部序列化、MAC 和加密”组成的完整一致性链。

研究补丁若只追求删除检测数据，通常会生成更稳定的异常。较低风险的处理方式是保留原生状态机，只在原生加密前对少量有效精度值做长度不变、校验合法、分布有变化的归一化，并让原生构造器完成剩余协议工作。

客户端逆向只能说明报告如何生成，不能证明服务端如何评分。任何“已完全规避”的结论都必须建立在长期、分组、可复现的运行验证上。
