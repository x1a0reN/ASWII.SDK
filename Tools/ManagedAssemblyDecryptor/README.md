# ManagedAssemblyDecryptor

该工具在游戏进程外完整还原当前客户端的两层托管程序集保护：

1. 去除程序集外层 `XOR4096 + trailer`，恢复可解析的 PE/CLR 映像。
2. 定位 `MethodDef.ImplFlags` 中带 `0x8000` 的方法，使用统一方法密钥流解密 IL 代码区，
   再清除保护标记。

工具不会修改或覆盖游戏文件。成功前会先在内存中完成全部程序集和方法体校验，随后才在
输出根目录新建 `OFFLINE_FULL_yyyyMMdd_HHmmss\FULL_MANAGED`。

## 构建

```powershell
& 'D:\Program Files\Visual Studio 2026\MSBuild\Current\Bin\MSBuild.exe' `
  '.\Tools\ManagedAssemblyDecryptor\ManagedAssemblyDecryptor.csproj' `
  /t:Build /p:Configuration=Release /m /v:minimal
```

输出：

```text
Tools\ManagedAssemblyDecryptor\bin\Release\ManagedAssemblyDecryptor.exe
```

## 首次使用或客户端保护更新后

使用一次真实运行时批量 Dump 校准保护参数，并立即完整解密：

```powershell
& '.\Tools\ManagedAssemblyDecryptor\bin\Release\ManagedAssemblyDecryptor.exe' `
  calibrate-and-decrypt `
  'C:\Users\x1a0reN\AppData\Local\ASWII\Data\ASWII_Data\Managed' `
  'C:\Users\x1a0reN\AppData\LocalLow\____________II\Managed_Dump\BATCH_20260722_153552' `
  'C:\Users\x1a0reN\AppData\LocalLow\____________II\Managed_Dump'
```

运行时目录必须包含匹配当前游戏版本的：

```text
*.runtime-read.dll
0x06*.bin
```

工具会从完整的密文/运行时映像对恢复外层密钥，再从所有可用 SIL2 方法对恢复最长的一致
方法密钥流。若样本长度不足以覆盖当前客户端的最长受保护方法，任务会在写盘前失败并要求
重新校准，不会生成部分解密程序集。

## 后续纯离线解密

首次成功运行会生成：

```text
OFFLINE_FULL_yyyyMMdd_HHmmss\protection_profile.txt
```

同一保护版本下，后续不需要启动游戏或重新 Dump：

```powershell
& '.\Tools\ManagedAssemblyDecryptor\bin\Release\ManagedAssemblyDecryptor.exe' `
  decrypt `
  'C:\Users\x1a0reN\AppData\Local\ASWII\Data\ASWII_Data\Managed' `
  '<上次输出目录>\protection_profile.txt' `
  'C:\Users\x1a0reN\AppData\LocalLow\____________II\Managed_Dump'
```

也可以只生成配置文件：

```powershell
& '.\Tools\ManagedAssemblyDecryptor\bin\Release\ManagedAssemblyDecryptor.exe' `
  calibrate `
  '<Managed目录>' `
  '<BATCH目录>' `
  '<输出的protection_profile.txt>'
```

## 输出

```text
OFFLINE_FULL_yyyyMMdd_HHmmss\
  __complete.txt
  protection_profile.txt
  full_managed_manifest.txt
  FULL_MANAGED\
    Assembly-CSharp.dll
    RAIN.dll
    ...
```

`FULL_MANAGED` 中：

- 受保护程序集使用原始文件名保存完整双层解密结果。
- 输入目录中原本就是普通托管 PE 的依赖会原样复制，便于 dnSpy/ILSpy 解析引用。
- 原生 PE DLL 不复制，但会记录在清单统计中。

## 强制校验

工具只在以下检查全部通过后写出结果：

- 每个校准明密文对完整符合外层循环 XOR，而不是只检查 `MZ`。
- 所有方法明密文对导出的密钥流前缀完全一致。
- SIL2 的 IL 长度、`MaxStack`、`InitLocals` 和异常处理表与运行时映像一致。
- 外层解密结果具有合法 PE、CLR Header、Metadata Root 和 `MethodDef` 表。
- 每个受保护方法的代码长度都被方法密钥流完整覆盖。
- 输出中不再存在 `MethodImplFlags 0x8000`。
- 所有带 RVA 的方法体都通过 IL opcode、操作数长度、Metadata Token、分支目标和异常区间校验。
- 配置文件中的外层密钥及方法密钥流 SHA-256 与内容一致。

客户端更新导致保护头、外层密钥或方法密钥流变化时，旧配置会被拒绝，应重新执行
`calibrate-and-decrypt`。

## 2026-07-22 当前客户端验证

```text
ProtectedAssemblies=22
ProtectedMethods=37338
ValidatedMethodBodies=65462
OuterKeySHA256=086955961CA017E7E01D20E92C0F53D9873E921F40696FE1E6FF478EA92893D6
InnerStreamLength=15914
InnerStreamSHA256=B313CB2731511FFC9236657E4976CBB9D7D45A2A5C3F1C05FEB04081F6963208
```

完整输出中的 `Assembly-CSharp.dll` SHA-256：

```text
77AD962F5D2453641B7BA1D7D1A0E6EF84BE7F4DFA496F208E4A7BE03C5B821C
```
