# 2026-07-22 受保护托管程序集扫描与 Dump 记录

## 1. 结果

游戏目录中共有 22 个 DLL 不是普通 `MZ` 文件，而是统一以以下 8 字节开头：

```text
2F-39-EA-21-22-9F-3F-75
```

本次运行时批量任务先保存当前 AppDomain 已加载的 20 个透明解密 `MZ` 镜像；
`Mono.Posix` 和 `System.Security` 在本次会话未加载。随后通过 20/20 已知明密文交叉验证
恢复外层 `XOR4096 + 40-byte trailer` 格式，并在游戏进程外成功恢复全部 22 个程序集的
PE/CLR 映像。进一步分析确认这只是第一层：带 `MethodDef.ImplFlags 0x8000` 的方法代码区
仍被统一方法密钥流加密。

批量目录：

```text
C:\Users\x1a0reN\AppData\LocalLow\____________II\Managed_Dump\BATCH_20260722_153552
```

完成标志与清单：

```text
__complete.txt
batch_manifest.txt
```

第一层离线目录（只解除程序集外层）：

```text
C:\Users\x1a0reN\AppData\LocalLow\____________II\Managed_Dump\OFFLINE_20260722_161436
```

完整双层离线目录：

```text
C:\Users\x1a0reN\AppData\LocalLow\____________II\Managed_Dump\OFFLINE_FULL_20260722_172449\FULL_MANAGED
```

## 2. 磁盘受保护程序集

```text
Assembly-CSharp.dll
Assembly-CSharp-firstpass.dll
Assembly-UnityScript-firstpass.dll
Boo.Lang.dll
LitJson.dll
LZ4.dll
Mono.Posix.dll
Mono.Security.dll
mscorlib.dll
Pathfinding.ClipperLib.dll
Pathfinding.Ionic.Zip.Reduced.dll
Pathfinding.JsonFx.dll
Pathfinding.Poly2Tri.dll
RAIN.dll
RAINMetaform.dll
System.Configuration.dll
System.Core.dll
System.dll
System.Security.dll
System.Xml.dll
UnityEngine.dll
UnityEngine.UI.dll
```

## 3. 本次实际提取

成功透明解密并保存 `*.runtime-read.dll`：

```text
RAIN
RAINMetaform
Assembly-CSharp
Assembly-CSharp-firstpass
Assembly-UnityScript-firstpass
Pathfinding.ClipperLib
Pathfinding.Ionic.Zip.Reduced
Pathfinding.JsonFx
Pathfinding.Poly2Tri
LitJson
LZ4
Boo.Lang
Mono.Security
System.Configuration
System.Core
System
System.Xml
UnityEngine
UnityEngine.UI
mscorlib
```

运行时未加载：

```text
Mono.Posix
System.Security
```

这两个文件现已离线还原：

| Assembly | MVID | Length | SHA-256 |
|---|---|---:|---|
| `Mono.Posix.runtime-read.dll` | `554385c3-6f7e-4da5-8582-ed825de1b5f5` | 310272 | `71906D91C42390CD273269CB9F985F26BBF76BB70DDC868F84FAACA0F7EE7219` |
| `System.Security.runtime-read.dll` | `294234e1-9c70-4892-bf87-44eece2d3c58` | 135168 | `F3B08F66A14E2D868CBE304A6DFF7B8453B9764EFBFF479C4637017B624D46FB` |

旧版独立工具对 22 个受保护文件的外层解密结果为：

```text
Protected=22
Success=22
Failure=0
```

其中 20 个已加载程序集与运行时透明映像逐文件 SHA-256 完全一致（20/20），另外两个
通过 `MZ`、Cecil Assembly Identity、MVID、类型和方法表解析验证。这里的“透明映像”仍
保留方法级密文，不能作为最终可反编译结果。

所有已保存的 20 个运行时镜像都通过 `MZ=true` 与 SHA-256 记录。`System.dll` 的 Cecil
重打包因 comparer 异常失败，但其完整 `System.runtime-read.dll` 已成功保存，所以透明解密
Dump 本身不受影响。

关键镜像：

| Assembly | Length | SHA-256 |
|---|---:|---|
| `RAIN.runtime-read.dll` | 530432 | `B5A3A43934646EBD977E891B506B36C2DE85F1F82DFE20F1B676A50DDCBB886B` |
| `RAINMetaform.runtime-read.dll` | 7680 | `97FC79C49D9581FBBBC3118E2FA2DF6349C747881320635FBE501275619E3F88` |
| `Assembly-CSharp.runtime-read.dll` | 5242368 | `438B5252F51A3449E3BB098AF91AA48EE4FEE7FE4177AF2E7CE8BBC5A8BB5072` |
| `RAIN.deobf.dll` | 347648 | `DB2549C2C7678FDE15C0C7FA41CB33DA871D3E99F2B8787234E62066E47D3239` |

其余文件的长度、MVID、SHA-256 与状态以 `batch_manifest.txt` 为准。

## 4. x86 内存结论与最终策略

第一次实现对 20 个已加载程序集全部执行：

```text
透明解密镜像保存 -> 全方法反射 IL 遍历 -> Cecil 全量重打包
```

虽然任务完成，但游戏是 x86 进程，Private Bytes 峰值约 3.5 GB，已经逼近地址空间极限。
这套流程不能保留为每次启动自动执行的正常路径。

最终实现采用：

```text
所有目标：只保存运行时透明解密 MZ 镜像
关键游戏程序集：额外做结构化 IL 与 Cecil 重建
普通 framework 程序集：MZ 镜像有效时跳过反射遍历和重打包
每个关键目标结束：释放局部引用并执行一次受控 GC
正常游戏启动：AutoDumpProtectedAssemblies=false
```

此外新增并完成独立工具：

```text
Tools\ManagedAssemblyDecryptor
```

它现已实现完整双层流程：

```text
运行时 BATCH 校准 -> 外层解密 -> MethodDef 定位 -> 方法代码区解密
-> 清除 0x8000 -> 全方法 IL/Token/分支/EH 校验 -> FULL_MANAGED
```

当前格式的 4096 字节外层密钥 SHA-256 为：

```text
086955961CA017E7E01D20E92C0F53D9873E921F40696FE1E6FF478EA92893D6
```

方法保护使用所有程序集共享、每个方法从偏移 0 开始的统一 XOR 密钥流。当前最长受保护
方法为 `System.dll` 的 `0x06002A87 EvalByteCode`，代码长度 15914 字节；校准所得方法流：

```text
Length=15914
SHA256=B313CB2731511FFC9236657E4976CBB9D7D45A2A5C3F1C05FEB04081F6963208
```

正式工具对当前客户端的结果：

```text
ProtectedAssemblies=22
CopiedManagedAssemblies=4
ProtectedMethods=37338
ValidatedMethodBodies=65462
ImplFlag8000Remaining=0
```

`Assembly-CSharp.dll` 共解密 11199 个方法，完整输出 SHA-256：

```text
77AD962F5D2453641B7BA1D7D1A0E6EF84BE7F4DFA496F208E4A7BE03C5B821C
```

该文件的 15609 个方法体全部通过 Cecil 解析；ILSpy 9 全程序集反编译结果中
`DecompilerException=0`、`Unknown result type=0`。工具每次创建新的时间戳目录，不修改
游戏原件。

默认额外重建名单：

```text
RAIN
RAINMetaform
Assembly-CSharp
Assembly-CSharp-firstpass
Assembly-UnityScript-firstpass
```

结构化 IL 与重打包现已同时枚举普通方法、实例构造器和静态构造器，避免旧实现只覆盖
`MethodInfo` 而遗漏 `.ctor` / `.cctor`。

## 5. 使用规则

1. 批量 Dump 只作为一次性维护动作；正常游戏必须保持自动开关关闭。
2. 不用 LocalLow 中的分析副本覆盖游戏 `Managed` 原件。
3. `*.runtime-read.dll` 只代表外层已解除，不能直接视为方法体已经解密。
4. 优先使用 `Tools\ManagedAssemblyDecryptor calibrate-and-decrypt` 生成 `FULL_MANAGED`，
   不为扩大覆盖率强制加载 framework 程序集，也不在 x86 游戏进程内做全量 Cecil 重建。
5. 同一保护版本后续使用生成的 `protection_profile.txt` 执行 `decrypt`，无需再次启动游戏。
6. 客户端保护格式或密钥变化时重新执行运行时 BATCH 和校准；工具会在写盘前验证完整
   外层明密文对、方法流一致性、覆盖长度和解密后 IL，失败时不生成部分输出。
