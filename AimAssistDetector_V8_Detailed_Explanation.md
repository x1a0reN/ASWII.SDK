# AimAssistDetector v8 原理详解：从相机采样到加密射击包

## 1. 这篇文档解决什么问题

这篇文档专门解释下面几个容易混在一起的问题：

1. 检测器到底观察什么，是鼠标、准星、射线还是命中结果？
2. `aim_report_version` 为什么有时是 `8`，有时是 `136`？
3. `aim_target_uid` 非零是否等于锁头？
4. `aim_shot_precision_code` 为什么不能直接当毫米读？
5. `aim_precision_samples` 为什么不能直接清空？
6. 为什么补丁要放在 payload 加密前，而不是更早或更晚？
7. 子弹直线与 aim report 为什么必须使用同一条相机射线？
8. 当前处理具体改了什么，又刻意没有改什么？

本文会引用目标程序集的反编译代码。为了可读性，代码做了三类整理：

- 去掉与主题无关的 UI、动画和异常处理；
- 把反编译器生成的无意义局部变量名改成语义名；
- 把部分泛型单例表达式恢复为更容易理解的写法。

字段、常量、分支条件和调用顺序保持与目标程序集一致。文中如果使用“等价代码”或“简化代码”，就表示它不是逐字符原始源码，而是保持行为一致的整理版本。

## 2. 先建立一张完整地图

一次普通射击会跨过四个阶段：

```text
阶段 A：相机更新
CameraObj.LateUpdate
  -> 计算最终 transform
  -> 写 shootPos / shootForward
  -> AimAssistDetector.UpdateSample

阶段 B：武器开火
GunBaseController.Attack 等
  -> AimAssistDetector.MarkFireCooldown(fireTime)
  -> CaptureShotReport
  -> 冻结开火瞬间目标和精度

阶段 C：射击消息填充
ChannelConnection.Shoot
  -> AimAssistDetector.ApplyPendingShotReport(hitMessage)
  -> FillRequiredShotReport

阶段 D：网络写包
ChannelConnection.Shoot
  -> 先写外层 sample count
  -> ShootPayloadCrypt.BuildEncryptedPayload
  -> 明文序列化
  -> MAC
  -> 加密
  -> 发送
```

把它想成一张快递单：

```text
周期样本 = 运输过程中的轨迹记录
开火样本 = 封箱时的最终照片
HitMessage = 待寄出的货物清单
sample count = 外包装标注的件数
payload = 箱子内部实际物品
MAC = 防拆封签名
加密 = 不透明外包装
```

如果外包装写着 8 件，箱子里却只有 0 件，即使每件物品本身看起来合理，协议也已经不一致。

## 3. `HitMessage`：一枪的数据容器

下面是目标程序集反编译得到的完整字段结构：

```csharp
public class HitMessage
{
    public ObscuredInt part;
    public int uid;
    public ObscuredShort distance;
    public ObscuredVector3 position;
    public ObscuredByte is_real_man;
    public ObscuredInt robot_uid;
    public ObscuredInt enc;
    public ObscuredFloat spread;
    public ObscuredInt current_sight;

    public ObscuredByte aim_report_version;
    public ObscuredByte aim_target_uid;
    public ObscuredShort aim_shot_precision_code;
    public short[] aim_precision_samples;

    public HitMessage()
    {
        uid = 0;
        distance = 0;
        position = Vector3.zero;
        part = 255;
        robot_uid = 0;
        is_real_man = 1;
        enc = 0;
        spread = 0f;
        current_sight = 0;

        aim_report_version = 0;
        aim_target_uid = 0;
        aim_shot_precision_code = -1;
        aim_precision_samples = new short[0];
    }
}
```

前半部分描述命中，后半部分描述瞄准过程。

需要注意的是：

```text
hit uid
```

和：

```text
aim target uid
```

不是同一个概念。

- `uid` 表示这发子弹最后报告命中了谁；
- `aim_target_uid` 表示检测射线认为准星附近最接近的是谁；
- 两者可以相同，也可以不同；
- 服务端可以把这种一致性作为一个统计维度。

### 3.1 为什么这些字段前面有 `Obscured`

`ObscuredByte`、`ObscuredShort`、`ObscuredInt` 等包装类型通常不直接以普通数值形式存储内容。代码中可以依靠隐式运算符把它们当普通数值使用，但反射读写时会遇到一个问题：

```csharp
object boxed = field.GetValue(instance);
Convert.ToInt32(boxed);
```

不一定能成功，因为 `boxed` 的运行类型仍然是包装结构体。

可靠读取方式是先尝试普通转换，失败后查找包装类型自己的：

```csharp
public static implicit operator byte(ObscuredByte value)
public static implicit operator short(ObscuredShort value)
```

当前处理使用的等价逻辑是：

```csharp
object value = field.GetValue(instance);

try
{
    return Convert.ToInt32(value);
}
catch
{
}

MethodInfo implicitMethod = field.FieldType.GetMethod(
    "op_Implicit",
    BindingFlags.Public | BindingFlags.Static,
    null,
    new Type[] { field.FieldType },
    null);

object plainValue = implicitMethod.Invoke(null, new object[] { value });
return Convert.ToInt32(plainValue);
```

如果忽略这一步，日志里的：

```text
version=0
target=0
shotCode=0
```

可能只是读取失败后的 fallback，不代表包里真的全是零。

## 4. 检测器到底看哪条射线

目标程序集的相机更新逻辑很长，下面保留与射击方向有关的原始代码段：

```csharp
private void LateUpdate()
{
    if (GameStateManager.Instance.CurStateType != GameStateType.Fight || lock_camera)
        return;

    float deltaTime = Time.deltaTime;
    mouseOffset.x = Input.GetAxisRaw("Mouse X");
    mouseOffset.y = Input.GetAxisRaw("Mouse Y");

    // 省略观察模式与 UI 逻辑

    if (controlmode == ControlMode.kCharacterControl)
    {
        if (character == null || character.mWeapon == null)
            return;

        bool sighting = /* 当前是否开镜 */;

        if (!GameSet.getInstance().enable && !character.IsDied)
        {
            float sensitivity = sighting
                ? GameSet.getInstance().glassesSensitive * gunInfo.sight_info[0].mouse_sensitivity
                : GameSet.getInstance().mouseSensitive * 5f;

            finalx += mouseOffsetX * sensitivity;
            finaly += mouseOffsetY * sensitivity;
            finalx %= 360f;
            finaly %= 360f;
        }

        finaly = Mathf.Clamp(finaly, -75f, 48f);
        updateMove(deltaTime, sighting);

        shootPos = transform.position;
        shootForward = transform.forward;
        shootUp = transform.up;
        shootRight = transform.right;

        if (aimAssistDetector != null)
        {
            aimAssistDetector.UpdateSample(this);
        }

        // 之后才继续处理视觉抖动等效果
    }
}
```

关键顺序是：

```text
鼠标输入
 -> finalx/finaly
 -> updateMove
 -> transform
 -> shootForward
 -> UpdateSample
```

所以检测器记录的是相机最终射击向量。

举个例子：

```text
玩家屏幕正前方：世界方向 (0, 0, 1)
辅助功能把实际子弹改向右侧敌人：(0.2, 0, 0.98)
CameraObj.shootForward 仍然是：(0, 0, 1)
```

此时：

- 命中包可能报告右侧敌人；
- 检测器仍然按正前方射线计算 `aim_target_uid` 和精度；
- 两套数据可能长期不一致。

这就是为什么“子弹直线”不能只考虑本地 Physics.Raycast 是否命中，还要考虑检测器看到的相机射线。

## 5. 周期采样如何工作

下面是 `UpdateSample` 的大段反编译代码，仅规范化了变量名：

```csharp
public void UpdateSample(CameraObj cameraObj)
{
    if (cameraObj == null ||
        cameraObj.character == null ||
        !IsSupportedWeapon(cameraObj.character))
    {
        ResetDetectionState();
        return;
    }

    float now = Time.time;

    if (nextPrecisionSampleTime <= 0f)
    {
        nextPrecisionSampleTime = now;
    }

    if (now < nextPrecisionSampleTime)
    {
        return;
    }

    if (now - nextPrecisionSampleTime > 0.5f)
    {
        precisionSamples.Clear();
        nextPrecisionSampleTime = now;
    }

    PrecisionSampleData sample = CapturePeriodicPrecisionSample(cameraObj, now);

    int catchUpCount = 1 + Mathf.FloorToInt(
        (now - nextPrecisionSampleTime) / (float)HeadTraceInterval);

    catchUpCount = Mathf.Clamp(catchUpCount, 1, 100);

    for (int i = 0; i < catchUpCount; i++)
    {
        AddPrecisionSample(sample);
    }

    nextPrecisionSampleTime +=
        (float)catchUpCount * (float)HeadTraceInterval;
}
```

这段代码解决了两个问题。

### 5.1 正常采样

时间到达 `nextPrecisionSampleTime` 后，采一条：

```text
targetUid + precisionCode
```

然后推进下一次采样时间。

### 5.2 卡顿补样

如果某一帧晚到了多个采样周期，会计算 `catchUpCount`，把同一个当前样本补入多次，最多 100 次。

如果落后超过 0.5 秒，则认为旧时间轴已经失真，先清空样本再从当前时间开始。

这里有一个静态分析限制：当前快照中 `HeadTraceInterval` 的具体运行赋值不能可靠恢复。我们可以证明它被用于采样时钟和目标缓存，但不能只凭这份静态代码声称它一定是多少毫秒。准确值应在运行时读取或从时间序列日志反推。

### 5.3 样本队列上限

原代码：

```csharp
private void AddPrecisionSample(PrecisionSampleData sample)
{
    if (precisionSamples.Count >= 100)
    {
        precisionSamples.RemoveAt(0);
    }

    precisionSamples.Add(sample);
}
```

这是一个最多 100 项的滑动窗口。第 101 个样本进来时，会移除最早的一项。

## 6. 检测器怎样找“最近的头”

很多人会把这个检测想成：

```text
Physics.Raycast 是否打中了头部 Collider
```

实际不是。它计算的是每个头部中心到射线的垂直距离。

下面是目标搜索的主要反编译代码：

```csharp
private bool TryFindNearestHeadByRay(
    Vector3 origin,
    Vector3 forward,
    out HeadTraceResult result)
{
    result = default(HeadTraceResult);

    Character player = Level.Instance == null
        ? null
        : Level.Instance.GetPlayer();

    if (player == null || forward.sqrMagnitude <= 0.0001f)
        return false;

    List<Character> characters = Level.Instance.GetCharacters();
    if (characters == null || characters.Count == 0)
        return false;

    Vector3 direction = forward.normalized;
    float maxDistance = Mathf.Max(0f, rayDistance);
    if (maxDistance <= 0f)
        return false;

    float bestCenterDistance = float.MaxValue;

    for (int i = 0; i < characters.Count; i++)
    {
        Character candidate = characters[i];

        if (candidate == null ||
            candidate == player ||
            candidate.IsDied ||
            candidate.GetTeam() == player.GetTeam() ||
            !TryGetHeadColliderCenter(candidate, out Vector3 headCenter))
        {
            continue;
        }

        float projection = Vector3.Dot(headCenter - origin, direction);

        if (projection < 0f || projection > maxDistance)
            continue;

        Vector3 nearestPoint = origin + direction * projection;
        float centerDistance = Vector3.Distance(headCenter, nearestPoint);

        if (centerDistance < bestCenterDistance)
        {
            bestCenterDistance = centerDistance;
            result.target = candidate;
            result.centerDistance = centerDistance;
        }
    }

    return result.target != null;
}
```

头部中心来自：

```csharp
HitCollider hit = target.getHitCollider("web__head");
SphereCollider sphere = hit.self.GetComponent<SphereCollider>();
center = sphere.transform.position
       + sphere.transform.rotation * sphere.center;
```

### 6.1 用二维例子理解投影

假设相机在：

```text
origin = (0, 0)
forward = (1, 0)
```

敌人 A 的头在：

```text
(10, 0.03)
```

敌人 B 的头在：

```text
(8, 0.20)
```

两者都在射线前方。到射线的垂直距离分别是：

```text
A = 0.03 m = 30 mm
B = 0.20 m = 200 mm
```

检测器会选择 A，因为 A 的头部中心更靠近射线。

这时：

```text
aim_target_uid = A.uid
precision = 30 mm 的编码
```

即使射线并没有真正撞到 A 的头部 Collider，也可能得到这个结果。

所以：

```text
targetUid 非零 != 命中头部
precision 越低 = 射线越接近头部中心
```

## 7. 开火时怎样冻结报告

普通枪开火代码中，检测调用位于真正 `FireCheck` 之前：

```csharp
public override void Attack()
{
    if (Ready() && Input.GetKey(fireKey))
    {
        if (Time.time >= next_fire_time)
        {
            float fireTime = info.fire_time;

            if (owner.buff_state[11].enable && info.sub_type == 3)
            {
                fireTime *= 1f - owner.buff_state[11].user_data.float_data[1];
            }

            AimAssistDetector.MarkFireCooldown(fireTime);

            base.Attack();
            FireCheck();

            // 后续是动画、后坐力、弹匣和冷却
        }
    }
}
```

霰弹枪也先捕获一次，然后发出多条 pellet 射线：

```csharp
public override void Attack()
{
    if (Input.GetKey(fireKey) && Time.time >= next_fire_time)
    {
        AimAssistDetector.MarkFireCooldown(info.fire_time);

        for (int i = 0; i < 7; i++)
        {
            FireCheck(do_effect: false);
        }

        FireCheck();
        // 动画、弹药和冷却
    }
}
```

这说明一轮霰弹发射对应一次 aim report 捕获，而不是每个 pellet 都生成一份独立报告。

`MarkFireCooldown` 很简单：

```csharp
public static void MarkFireCooldown(float fireTime)
{
    CameraObj camera = CameraObj.Instance;

    if (camera != null && camera.aimAssistDetector != null)
    {
        camera.aimAssistDetector.CaptureShotReport(camera, fireTime);
    }
}
```

真正冻结状态的是：

```csharp
private void CaptureShotReport(CameraObj cameraObj, float fireTime)
{
    pendingShotReportFrame = Time.frameCount;
    ResetPendingShotReport();

    if (cameraObj == null ||
        cameraObj.character == null ||
        !IsSupportedWeapon(cameraObj.character))
    {
        ResetDetectionState();
        return;
    }

    ResetHeadTraceCache();

    float targetDistance;
    Character target = GetNearestEnemyByRay(
        cameraObj.shootPos,
        cameraObj.shootForward,
        out targetDistance);

    PrecisionSampleData shotSample =
        CreatePrecisionSample(target, targetDistance);

    pendingShotTargetUid = shotSample.targetUid;
    pendingShotPrecisionCode = shotSample.precisionCode;

    pendingShotSamples.AddRange(precisionSamples);
    precisionSamples.Clear();

    fireCooldownSample = shotSample;
    fireCooldownActive = fireTime > 0f;
    fireCooldownEndTime = Mathf.Max(
        fireCooldownEndTime,
        Time.time + Mathf.Max(0f, fireTime));
}
```

逐句解释：

1. `pendingShotReportFrame = Time.frameCount`：记住是哪一帧开火。
2. `ResetPendingShotReport`：清除上一枪尚未使用的目标和样本。
3. 重新计算最近头部，不直接复用旧缓存。
4. 保存开火瞬间的 `targetUid` 和 `precisionCode`。
5. 把开火前的周期样本复制到 pending 列表。
6. 清空周期列表，让下一枪重新积累。
7. 在武器冷却窗口内复用开火样本。

### 7.1 为什么冷却窗口会重复样本

周期采样函数有这个分支：

```csharp
private PrecisionSampleData CapturePeriodicPrecisionSample(CameraObj camera, float now)
{
    if (fireCooldownActive && now < fireCooldownEndTime)
    {
        return fireCooldownSample;
    }

    if (fireCooldownActive)
    {
        fireCooldownActive = false;
        ResetHeadTraceCache();
    }

    return CaptureCurrentPrecision(camera);
}
```

所以如果开火瞬间精度非常低，冷却窗口内的周期采样可能重复同一个低值。服务端即使不看单枪，也可以对连续样本做分布统计。

## 8. `8` 与 `136` 到底是什么

填充报告的关键代码如下：

```csharp
private void FillPendingShotReport(HitMessage hitMessage)
{
    if (hitMessage != null &&
        pendingShotReportFrame == Time.frameCount)
    {
        hitMessage.aim_report_version = 136;
        hitMessage.aim_target_uid = pendingShotTargetUid;
        hitMessage.aim_shot_precision_code = pendingShotPrecisionCode;

        byte rawHitTargetUid = GetRawHitTargetUid(hitMessage);
        byte lockTargetUid = rawHitTargetUid == 0
            ? pendingShotTargetUid
            : rawHitTargetUid;

        hitMessage.aim_precision_samples =
            BuildSanitizedPrecisionSamples(lockTargetUid);
    }
}

private void FillRequiredShotReport(HitMessage hitMessage)
{
    if (pendingShotReportFrame == Time.frameCount)
    {
        FillPendingShotReport(hitMessage);
        return;
    }

    hitMessage.aim_report_version = 8;
    hitMessage.aim_target_uid = 0;
    hitMessage.aim_shot_precision_code = -1;
    hitMessage.aim_precision_samples = new short[0];
}
```

十六进制看起来更直观：

```text
136 decimal = 0x88 = 1000 1000 binary
  8 decimal = 0x08 = 0000 1000 binary
```

拆开：

```text
0x80 = captured flag
0x08 = protocol version
```

所以：

```text
0x88：协议版本为 8，而且本枪在同一帧拿到了 pending report
0x08：协议版本为 8，但本枪没有匹配到同帧 pending report
```

### 8.1 为什么不能把所有枪都改成 `8`

因为这相当于稳定告诉接收方：

```text
我每一枪都没拿到同帧采样
```

偶发 timing miss 是原生状态，持续 100% miss 就可能成为另一个异常分布。

### 8.2 为什么也不能把所有枪都改成 `0x88`

因为有些调用确实可能跨帧或没有 pending report。把真实 miss 强制改为 captured，需要同时伪造目标、开火精度和样本数组，反而扩大修改面。

最小处理原则是：

```text
原生是 captured -> 保持 captured
原生是 missing -> 保持 missing
```

## 9. 历史样本为什么会被“消毒”

原始代码：

```csharp
private short[] BuildSanitizedPrecisionSamples(byte lockTargetUid)
{
    int count = Mathf.Min(pendingShotSamples.Count, 100);
    short[] output = new short[count];

    for (int i = 0; i < count; i++)
    {
        PrecisionSampleData sample = pendingShotSamples[i];

        output[i] =
            (lockTargetUid == 0 || sample.targetUid != lockTargetUid)
            ? CreateRandomInvalidPrecision()
            : sample.precisionCode;
    }

    return output;
}
```

意思是：

- 如果周期样本属于本枪锁定目标，就保留真实精度；
- 如果周期样本属于另一个目标，就替换为无效精度；
- 如果本枪没有锁定目标，也把历史样本都标成无效。

无效精度由：

```csharp
private static short CreateRandomInvalidPrecision()
{
    int millimeters = Random.Range(331, 3277);
    return EncodePrecisionMillimeters(millimeters);
}
```

产生。

这能证明：

```text
客户端内部把 331..3276 mm 用作不属于当前目标的无效区间
```

但不能推出：

```text
服务端一定以 331 mm 为处罚阈值
```

两者不是同一个命题。

## 10. 精度编码为什么有校验位

原始编码代码：

```csharp
private static short EncodePrecision(float distanceMeters)
{
    int millimeters = Mathf.Clamp(
        Mathf.FloorToInt(Mathf.Max(0f, distanceMeters) * 1000f),
        0,
        3276);

    return EncodePrecisionMillimeters(millimeters);
}

private static short EncodePrecisionMillimeters(int millimeters)
{
    millimeters = Mathf.Clamp(millimeters, 0, 3276);

    int hundreds = millimeters / 100 % 10;
    int tens = millimeters / 10 % 10;
    int ones = millimeters % 10;
    int check = Mathf.Abs(hundreds + tens - ones) % 10;

    return (short)(millimeters * 10 + check);
}
```

编码格式可以写成：

```text
code = mm * 10 + checkDigit
```

最后一位不是精度的一部分，而是一个简单校验位。

### 10.1 例子一：42 mm

```text
hundreds = 0
tens = 4
ones = 2
check = abs(0 + 4 - 2) % 10 = 2
code = 42 * 10 + 2 = 422
```

解码毫米值时：

```text
422 / 10 = 42 mm
```

### 10.2 例子二：150 mm

```text
hundreds = 1
tens = 5
ones = 0
check = abs(1 + 5 - 0) % 10 = 6
code = 1506
```

### 10.3 例子三：237 mm

```text
hundreds = 2
tens = 3
ones = 7
check = abs(2 + 3 - 7) % 10 = 2
code = 2372
```

### 10.4 错误修改示例

原值：

```text
42 mm -> 422
```

如果想改成 150 mm，却只把前面数字改掉，生成：

```text
1502
```

校验：

```text
abs(1 + 5 - 0) % 10 = 6
```

正确值应该是 `1506`，所以 `1502` 是原生编码器不会产生的值。

## 11. 网络层为什么是最关键的边界

下面是 `ChannelConnection.Shoot` 的完整反编译主体：

```csharp
public void Shoot(
    Vector3 position,
    Vector3 direction,
    HitMessage hitMessage,
    byte slot,
    bool doEffect,
    Vector3 velocity)
{
    if (state == State.kInGame &&
        game_state != GameState.kGameLeaving)
    {
        AimAssistDetector.ApplyPendingShotReport(hitMessage);

        BeginWrite();
        WriteByte(106);
        WriteByte(hitMessage.is_real_man);
        WriteInt(hitMessage.robot_uid);
        WriteFloat(Time.time - game_server_sync_local_time + game_server_time);
        WriteByte(Convert.ToByte(doEffect));
        ConnectionDef.WriteCharacterPosition(_stream, position);
        ConnectionDef.WriteCharacterEulerAngles(_stream, direction.normalized);
        WriteByte(slot);

        WriteByte(
            ShootPayloadCrypt.GetAimPrecisionSampleCount(hitMessage));

        byte[] payload =
            ShootPayloadCrypt.BuildEncryptedPayload(hitMessage);

        Write(payload, payload.Length);
        EndWrite();
    }
}
```

注意严格顺序：

```text
1. 填充 aim report
2. 写外层固定字段
3. 写外层 sample count
4. 构造内部 payload
5. 写 payload
```

### 11.1 为什么最终 Hook 不能改变数组长度

假设原报告有 5 个样本：

```text
aim_precision_samples.Length = 5
```

网络层先执行：

```text
outer sample count = 5
```

然后进入 payload builder。

如果补丁在这里把数组清空：

```text
inner sample count = 0
```

接收方看到：

```text
外层说 5 个
内部说 0 个
payload 长度也只够 0 个
```

这不是“隐藏了样本”，而是制造了协议自相矛盾。

安全的修改只能是：

```csharp
short[] clone = (short[])samples.Clone();
clone[i] = newValue;
hitMessage.aim_precision_samples = clone;
```

其中 `clone.Length` 必须等于原数组长度。

## 12. 内部 payload 是怎样构造的

下面是原始构造器的主体：

```csharp
public static byte[] BuildEncryptedPayload(
    HitMessage hitMessage,
    int currentSpreadIndex)
{
    if (hitMessage == null)
    {
        hitMessage = new HitMessage();
    }

    hitMessage.aim_report_version =
        (byte)((hitMessage.aim_report_version & 0x80) | 8);

    short[] samples =
        hitMessage.aim_precision_samples ?? new short[0];

    int sampleCount = Mathf.Min(samples.Length, 100);
    int plainLength = 18 + sampleCount * 2;
    byte[] data = new byte[plainLength + 4];
    int offset = 0;

    PutByte(data, ref offset, (byte)hitMessage.uid);
    PutInt16(data, ref offset,
        hitMessage.uid != 0 ? hitMessage.distance : (short)0);
    PutByte(data, ref offset,
        hitMessage.uid != 0 ? (byte)hitMessage.part : (byte)0);
    PutInt32(data, ref offset, hitMessage.enc);
    PutFloat(data, ref offset, hitMessage.spread);
    PutByte(data, ref offset, (byte)hitMessage.current_sight);
    PutByte(data, ref offset, hitMessage.aim_report_version);
    PutByte(data, ref offset, (byte)sampleCount);
    PutByte(data, ref offset, hitMessage.aim_target_uid);
    PutInt16(data, ref offset, hitMessage.aim_shot_precision_code);

    for (int i = 0; i < sampleCount; i++)
    {
        PutInt16(data, ref offset, samples[i]);
    }

    uint seed = MakeShootSeed(
        NormalizeSpreadIndex(currentSpreadIndex));

    PutUInt32(data, plainLength,
        ComputeMac(data, plainLength, seed));

    XorPayload(data, seed);
    return data;
}
```

固定明文布局：

```text
offset 0   byte   hit uid
offset 1   short  distance
offset 3   byte   part
offset 4   int    enc
offset 8   float  spread
offset 12  byte   current sight
offset 13  byte   aim report version
offset 14  byte   sample count
offset 15  byte   aim target uid
offset 16  short  shot precision code
offset 18  short[sampleCount] precision samples
末尾        uint   MAC
```

总长度：

```text
fixed plain 18
+ samples 2 * N
+ MAC 4
= 22 + 2 * N
```

例子：

```text
N=0 -> 22 bytes
N=5 -> 32 bytes
N=100 -> 222 bytes
```

### 12.1 为什么保留 `0x80`

构造器执行：

```csharp
version = (version & 0x80) | 8;
```

如果输入是：

```text
0x88
```

则：

```text
(0x88 & 0x80) | 0x08
= 0x80 | 0x08
= 0x88
```

如果输入是：

```text
0x08
```

则仍是 `0x08`。

构造器明确把最高位当作状态位，把低位当作版本位。

## 13. MAC 和加密为什么不能绕开原生构造器

MAC 的等价代码：

```csharp
private static uint ComputeMac(byte[] data, int length, uint seed)
{
    uint hash = 0x811C9DC5u ^ seed ^ 0x5A19C0DEu;

    for (int i = 0; i < length; i++)
    {
        hash = (hash ^ data[i]) * 16777619u;
    }

    return Hash32(hash);
}
```

种子来自规范化后的 spread index：

```csharp
private static int NormalizeSpreadIndex(int spreadIndex)
{
    spreadIndex %= 31;
    if (spreadIndex < 0)
        spreadIndex += 31;
    return spreadIndex;
}

private static uint MakeShootSeed(int spreadIndex)
{
    uint value = 2709149090u;
    value ^= (uint)(spreadIndex * -1640531527);
    return Hash32(value);
}
```

最后，对包含 MAC 的整个数组执行流式异或。

这意味着：

```text
修改一个 precision sample
 -> 明文变化
 -> MAC 必须变化
 -> 加密后的所有相关字节也变化
```

如果在加密完成后直接改密文，接收方解密后计算的 MAC 不会匹配。

最简单、最可靠的处理边界是：

```text
在 BuildEncryptedPayload 进入时改 HitMessage
然后 return true，让原生方法继续
```

原生代码自然会使用修改后的明文重新计算 MAC 和密文。

## 14. 当前补丁到底 Hook 了什么

当前安装逻辑的关键部分是：

```csharp
// 保留检测器原生 lifecycle。

TryPatch(
    harmony,
    assembly,
    "ShootPayloadCrypt",
    "BuildEncryptedPayload",
    new Type[] { typeof(HitMessage) },
    "Protection_ShootPayloadBuildPrefix");

TryPatch(
    harmony,
    assembly,
    "ShootPayloadCrypt",
    "BuildEncryptedPayload",
    new Type[] { typeof(HitMessage), typeof(int) },
    "Protection_ShootPayloadBuildWithSpreadPrefix");
```

安装器没有给这些原生生命周期函数注册 Prefix：

```text
UpdateSample
MarkFireCooldown
CaptureShotReport
ApplyPendingShotReport
FillRequiredShotReport
```

源文件中即使还存在同名的辅助 Prefix 方法，只要安装器没有把它们传给 Harmony，它们就不会执行。判断“当前运行补丁做了什么”必须看安装表，不能只看某个方法是否存在。

### 14.1 为什么单参数 overload 不处理

原始单参数方法：

```csharp
public static byte[] BuildEncryptedPayload(HitMessage hitMessage)
{
    Character player = Level.Instance?.GetPlayer();
    int spreadIndex = player ? player.currentSpreadIndex : 0;
    return BuildEncryptedPayload(hitMessage, spreadIndex);
}
```

它自己不序列化，只是取得 spread index 后转调双参数方法。

如果两个 Prefix 都执行归一化，就会：

```text
单参数 Prefix 处理一次
 -> 转调
双参数 Prefix 再处理一次
```

因此当前单参数 Prefix 是空的：

```csharp
private static void Protection_ShootPayloadBuildPrefix(object hitMessage)
{
    // 不处理，等待实际构造 payload 的双参数 overload。
}
```

真正处理发生在：

```csharp
private static void Protection_ShootPayloadBuildWithSpreadPrefix(
    object hitMessage,
    int currentSpreadIndex)
{
    Protection_SanitizeShootPayload(
        hitMessage,
        currentSpreadIndex,
        "BuildEncryptedPayload(hit,spread)");
}
```

## 15. 2026-07-22 当前归一化逻辑

当前实现以“手动射击零修改、命中语义零修改”为边界：

```csharp
if (!captured)
    return 0;

if (!IsAimManipulationActive())
    return 0;

// version、target 和开火瞬间精度全部保留。
short[] samples = ReadShortArray(hitMessage, "aim_precision_samples");
short[] clone = (short[])samples.Clone();

for (int i = 0; i < clone.Length; i++)
    clone[i] = HumanizeHistoricalPrecisionCode(clone[i]);

// 只有内容确实变化时才写回；Length 永远不变。
SetField(hitMessage, "aim_precision_samples", clone);
```

处理条件与保持项如下：

| 项目 | 当前行为 |
|---|---|
| genuine missing (`captured=false`) | 完全原样放行 |
| 未实际启用 `AutoAim`/`AimTrack` | 完全原样放行 |
| `aim_report_version` | 保留原生值 |
| `aim_target_uid` | 保留原生值 |
| `aim_shot_precision_code` | 保留原生值，不参与归一化 |
| 历史负数 sentinel | 保留 |
| 历史 `>=120 mm` 样本 | 保留 |
| 历史 `<120 mm` 样本 | 映射到有时间连续性的 `120..300 mm` 区间 |
| `aim_precision_samples.Length` | 严格不变 |

历史低值不再按每枪独立哈希到均匀分布，而是使用按目标和时间间隔重置的随机游走：

```text
session state = precision + velocity + persistent PRNG
next = current + velocity + small acceleration + jitter
boundary = reflect into 120..300 mm
```

这样做不能证明“完全不可检测”；它只是避免固定常数、独立均匀分布和全局清零等更明显的
客户端人工指纹。开火瞬间精度处于命中信息的同一加密 payload 中，旧版对该字段的改写与
暴击伤害下降同时出现，因此当前实现不再触碰它。

## 15A. 旧版归一化逻辑（已停用，仅供差分）

> 以下代码描述 2026-07-22 修复前的实现，不代表当前运行路径。

核心代码整理如下：

```csharp
private static int NormalizeAimReportFields(object hitMessage)
{
    if (hitMessage == null)
        return -1;

    if (!HasField(hitMessage, "aim_report_version"))
        return -1;

    int version = ReadInt(hitMessage, "aim_report_version", 8);
    bool captured = (version & 0x80) != 0;

    if (!captured)
    {
        // 真实 timing miss 原样保留。
        return 0;
    }

    SetField(hitMessage, "aim_report_version", (byte)0x88);

    int seed = ReadInt(hitMessage, "enc", 0);
    seed ^= ReadInt(hitMessage, "uid", 0) * 397;
    seed ^= Time.frameCount;

    int adjusted = 0;

    short shotCode =
        (short)ReadInt(hitMessage, "aim_shot_precision_code", -1);

    short normalizedShot =
        NormalizePrecisionCode(shotCode, seed, -1);

    if (normalizedShot != shotCode)
    {
        SetField(hitMessage,
            "aim_shot_precision_code",
            normalizedShot);
        adjusted++;
    }

    short[] samples =
        ReadShortArray(hitMessage, "aim_precision_samples");

    if (samples != null && samples.Length > 0)
    {
        short[] clone = (short[])samples.Clone();
        bool changed = false;

        for (int i = 0; i < clone.Length; i++)
        {
            short normalized =
                NormalizePrecisionCode(clone[i], seed, i);

            if (normalized == clone[i])
                continue;

            clone[i] = normalized;
            changed = true;
            adjusted++;
        }

        if (changed)
        {
            // clone.Length 与原数组完全相同。
            SetField(hitMessage,
                "aim_precision_samples",
                clone);
        }
    }

    return adjusted;
}
```

### 15A.1 `hitMessage == null` 或字段不存在

返回 `-1`，表示当前对象不是可处理的报告。调用者不会继续记录本次归一化。

### 15A.2 `captured == false`

直接返回 `0`。不创建目标、不创建样本、不修改 version。

这是“保留原生 missing 状态”的关键。

### 15A.3 version 写回 `0x88`

只在原生 captured 已经存在时执行。它不是把 missing 伪造成 captured，而是把低位规范为 8，并明确保留最高位。

### 15A.4 seed 的作用

当前归一化不是把所有值改成一个常数，而是使用：

```text
enc
hit uid
frame count
sample index
```

混合出不同结果。

这不是密码学随机数，只是为了避免所有低精度样本集中到同一个数字。

### 15A.5 clone 数组

使用 clone 的目的有两个：

1. 不在遍历期间原地破坏原数组；
2. 明确保证新数组长度与原数组一致。

## 16. 旧版低精度映射（已停用）

当前算法：

```csharp
private static short NormalizePrecisionCode(
    short code,
    int seed,
    int sampleIndex)
{
    if (code < 0)
        return code;

    int millimeters = code / 10;

    if (millimeters >= 120)
        return code;

    uint mixed = unchecked((uint)seed);
    mixed ^= unchecked((uint)(sampleIndex + 2) * 0x9E3779B9u);
    mixed ^= mixed >> 16;
    mixed *= 0x7FEB352Du;
    mixed ^= mixed >> 15;
    mixed *= 0x846CA68Bu;
    mixed ^= mixed >> 16;

    int normalizedMillimeters =
        120 + (int)(mixed % 141u);

    return EncodePrecisionMillimeters(
        normalizedMillimeters);
}
```

行为可以总结为：

```text
code < 0                 -> 保留 sentinel
decoded mm >= 120        -> 原样保留
0 <= decoded mm < 120    -> 映射到 120..260 mm
```

### 16.1 为什么保留负数

`-1` 表示没有有效目标或没有精度。它不是“特别精准”，不能进入毫米映射。

### 16.2 为什么不修改大于等于 120 mm 的值

目标是缩小修改面。原本已经不在研究低值区间的样本没有必要改。

### 16.3 为什么目标区间不是单值

如果所有低值都变成：

```text
150 mm
```

统计图上会出现一个人为尖峰。

映射到：

```text
120..260 mm
```

至少会保留一定变化。

必须强调：

```text
120 和 260 是当前研究补丁参数
不是从客户端证明出来的服务端处罚线
```

## 17. 当前实现的一枪数值例子

假设开火前有 4 个周期样本，解码后的毫米值为：

```text
[42, 75, 180, 510]
```

开火瞬间精度为：

```text
28 mm
```

原生报告可能是：

```text
version = 0x88
target = 17
shotCode = Encode(28)
samples = [Encode(42), Encode(75), Encode(180), Encode(510)]
```

进入最终构造器前：

```text
28  -> 保留（开火瞬间精度不再改写）
42  -> 由连续状态映射到 120..300
75  -> 由同一连续状态继续映射
180 -> 保留
510 -> 保留
```

例如本次随机游走结果为：

```text
shot = 28 mm
samples = [167, 176, 180, 510] mm
```

编码后可能变成：

```text
28 mm  -> 0286
167 mm -> 1670
176 mm -> 1762
180 mm -> 1809
510 mm -> 5106
```

需要再次强调：具体历史映射值由会话状态决定，上面数字只是帮助理解的例子。

不变的内容：

```text
version 仍是 0x88
target 仍是 17
samples.Length 仍是 4
outer count 仍是 4
hit uid 不变
spread index 不变
```

随后原生 builder：

```text
按 4 个样本构造明文
重新计算 MAC
重新加密
```

## 18. 子弹直线为什么与检测有关

检测器用：

```text
CameraObj.shootPos
CameraObj.shootForward
```

计算目标和精度。

如果直线弹道功能也使用同一组值：

```csharp
Vector3 origin = cameraObj.shootPos;
Vector3 direction = cameraObj.shootForward.normalized;
Ray finalRay = new Ray(origin, direction);
```

那么：

```text
本地射线方向
检测器射线方向
射击包方向
```

至少在几何语义上是一致的。

如果直线弹道功能直接构造：

```csharp
direction = (targetHead - origin).normalized;
```

但不更新 `CameraObj.shootForward`，则检测器仍然按屏幕准星方向采样。实际子弹方向和 aim report 会分离。

当前普通枪与狙击枪的处理都优先使用 `CameraObj.shootForward`，其目的不是让检测器失效，而是避免两条射线描述不同的世界状态。

## 19. 五个常见误解

### 19.1 “target 非零就会被判定”

不准确。目标搜索本来就会在射线前方选择最近的敌人。非零目标只是上下文，精度和时间分布更重要。

### 19.2 “把 detector Update 禁掉最干净”

禁掉后可能让每枪都变成 missing 或空样本，形成稳定缺失模式。

### 19.3 “只要把数组清空就没数据了”

外层长度已经写出。清空会导致外层计数、内部计数和 payload 长度不一致。

### 19.4 “改密文最后几个字节就行”

密文中包含加密后的 MAC。修改密文会导致解密后的 MAC 校验失败。

### 19.5 “120 mm 就是服务端阈值”

不是。它是当前归一化实现选择的研究参数。客户端代码只能证明编码方法和无效样本区间，不能证明服务端模型。

## 20. 怎样判断当前处理真的在正确位置

静态上检查：

```text
安装器只对 payload builder 注册当前处理
原生 UpdateSample 仍会运行
原生 MarkFireCooldown 仍会运行
原生 ApplyPendingShotReport 仍会运行
双参数 builder Prefix 处理一次
Prefix 返回后原生 builder 继续运行
```

运行时检查：

```text
captured 报告仍有 0x88
真实 missing 报告仍可能有 0x08
sample count 不是每枪固定 0
样本数有自然变化
数组长度修改前后相同
没有 Obscured 字段转换错误
没有 payload sanitize error
没有 MAC 或长度导致的立即断开
```

## 21. 如何做有意义的对照测试

不要把多个功能一起开，然后根据一次断开判断原因。至少分四组：

```text
A：无辅助功能
B：仅瞄准辅助
C：仅直线弹道
D：瞄准辅助 + 直线弹道
```

每组记录：

```text
武器类型
开火次数
version/captured
target uid
shot precision
sample count
样本分布
从首枪到断开的时间
明确的断开入口与 mode
```

一次完整对局没有断开，只能说明：

```text
这次运行没有触发可观察的处罚
```

不能证明：

```text
服务端没有长期统计
所有武器都安全
所有地图和延迟条件都安全
未来不会改变模型
```

## 22. 最后用一句话概括

这套检测不是在包里放一个“是否自瞄”的布尔值，而是把最终相机射线在一段时间内相对敌人头部中心的偏差，绑定到具体开火帧，再与命中信息、样本数量、spread index、MAC 和加密 payload 一起发送。

因此，可靠的研究处理不是“把检测器删掉”，而是理解并保留它的状态机和协议形状，只在原生加密前对少量异常集中的有效精度值做长度不变、编码合法、结果有变化的归一化，然后交还给原生构造器完成 MAC 与加密。
