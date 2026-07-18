# Codex 工作规则

## 目标

- 在用户授权范围内完成任务，交付可复现、可验证的结果。

## 约束

- 默认使用简体中文；用户明确要求英文时除外。代码标识符、命令、日志和报错保持原文。
- 先确认事实和当前状态；无法验证的内容明确标为推断，不把猜测写成结论。
- 只修改用户明确授权的范围，优先复用现有文件，不覆盖或整理无关改动。计划、审查和说明类任务默认不改源代码。
- 不执行删除、清空、重建目录或等价操作；需要移除内容时提供保留原件的替代方案。
- Windows 环境使用 PowerShell，并以当前会话实际可用的工具为准；手工文件修改使用 `apply_patch`。
- 沟通直接、专业、尊重。发现错误前提或明显风险时明确指出，不用角色设定、吹捧或无关吐槽稀释结论。
- 仅在关键里程碑、阻塞或风险决策点汇报进度，每次 1-2 句，不复述内部推理。

## 成功标准

- 用户要求的产物或结论已完成，且没有越权修改、破坏既有改动或隐瞒不确定性。
- 变更保持现有约定和行为一致；必要的关联文件、文档和验证一并完成。

## 验证方式

- 运行与改动直接相关的最小充分检查，优先验证真实运行路径而非只看静态代码。
- 最终报告变更范围、验证结果及未执行项的原因；不得把未运行或不完整的验证表述为通过。

# ASWDEBUG 项目状态与部署规则

最后更新：2026-07-17

## 1. 固定路径

工作区：

```text
D:\逆向\逆向-源码\创想兵团腾讯\ASWDEBUG
```

当前逆向分析用主程序集：

```text
C:\Users\x1a0reN\AppData\LocalLow\____________II\Assembly-CSharp.deobf.dll
```

游戏根目录：

```text
C:\Users\x1a0reN\AppData\Local\ASWII\Data
```

游戏托管程序集目录：

```text
C:\Users\x1a0reN\AppData\Local\ASWII\Data\ASWII_Data\Managed
```

## 2. 架构事实

`ASWII.exe` 的 PE Machine 是 `0x014C`，即 x86。

因此本项目必须部署：

```text
Doorstop\x86\winhttp.dll
Doorstop\x86\doorstop_config.ini
```

不得把 `Doorstop\x64\winhttp.dll` 部署到当前游戏目录，除非重新检查后的 `ASWII.exe` 已变成 x64。

## 3. 构建命令

使用 Visual Studio 2026 MSBuild 构建 Debug：

```powershell
& 'D:\Program Files\Visual Studio 2026\MSBuild\Current\Bin\MSBuild.exe' `
  'D:\逆向\逆向-源码\创想兵团腾讯\ASWDEBUG\ASWDEBUG.csproj' `
  /t:Build /p:Configuration=Debug /m /v:minimal
```

主输出：

```text
D:\逆向\逆向-源码\创想兵团腾讯\ASWDEBUG\bin\Debug\ASWDEBUG.dll
```

项目 `PostBuildEvent` 已配置自动部署。每次完成需要运行程序集的代码修改后，都必须执行构建并验证部署结果，不能只生成 `bin\Debug` 文件后结束。

## 4. 强制部署清单

### 4.1 项目提供的托管程序集

以下文件部署到：

```text
C:\Users\x1a0reN\AppData\Local\ASWII\Data\ASWII_Data\Managed
```

清单：

| 源文件 | 目标文件 |
|---|---|
| `bin\Debug\ASWDEBUG.dll` | `ASWDEBUG.dll` |
| `Lib\0Harmony.dll` | `0Harmony.dll` |
| `Lib\Mono.Cecil.dll` | `Mono.Cecil.dll` |
| `Lib\BouncyCastle.Crypto.dll` | `BouncyCastle.Crypto.dll` |

### 4.2 Doorstop 文件

以下文件部署到：

```text
C:\Users\x1a0reN\AppData\Local\ASWII\Data
```

清单：

| 源文件 | 目标文件 |
|---|---|
| `Doorstop\x86\winhttp.dll` | `winhttp.dll` |
| `Doorstop\x86\doorstop_config.ini` | `doorstop_config.ini` |

配置必须至少满足：

```ini
[General]
enabled=true
target_assembly=ASWII_Data\Managed\ASWDEBUG.dll

[UnityMono]
dll_search_path_override=ASWII_Data\Managed
```

### 4.3 游戏提供的依赖

`ASWDEBUG.dll` 当前引用以下由游戏安装目录提供的程序集：

```text
Assembly-CSharp.dll
RAIN.dll
System.dll
System.Core.dll
UnityEngine.dll
mscorlib.dll / Unity Mono 运行库
```

这些文件在游戏 `Managed` 目录中已经存在。默认不得用 `LocalLow` 中的反编译、检查或参考副本覆盖游戏原件。

`Assembly-CSharp.deobf.dll` 只用于编译引用和逆向分析，不部署为游戏的 `Assembly-CSharp.dll`。

## 5. 部署前备份规则

构建会执行带 `/Y` 的 PostBuild 复制，因此备份检查必须发生在构建前。

部署流程：

```text
1. 计算所有源文件和现有目标文件的 SHA-256。
2. 目标不存在时直接进入构建。
3. 目标存在且哈希相同时，不重复创建备份。
4. 目标存在且哈希不同时，先复制到带时间戳的备份目录。
5. 不删除旧目标和旧备份。
6. 执行 MSBuild，让 PostBuild 完成复制。
7. 再次比较所有源/目标文件长度和 SHA-256。
```

建议备份目录：

```text
C:\Users\x1a0reN\AppData\Local\ASWII\Data\ASWDEBUG.DeployBackups\yyyyMMdd_HHmmss
```

备份必须保留原文件名或记录原始相对路径，以便区分 `Managed` 文件和游戏根目录文件。

## 6. 部署后验证

至少验证以下六对文件：

```text
bin\Debug\ASWDEBUG.dll
  == Managed\ASWDEBUG.dll

Lib\0Harmony.dll
  == Managed\0Harmony.dll

Lib\Mono.Cecil.dll
  == Managed\Mono.Cecil.dll

Lib\BouncyCastle.Crypto.dll
  == Managed\BouncyCastle.Crypto.dll

Doorstop\x86\winhttp.dll
  == Data\winhttp.dll

Doorstop\x86\doorstop_config.ini
  == Data\doorstop_config.ini
```

比较条件：

```text
文件存在
长度一致
SHA-256 一致
```

只看到 PostBuild 的“已复制”输出不算完成，必须做源/目标哈希验收。

## 7. 2026-07-17 当前部署状态

本次重新构建已成功，PostBuild 共复制 6 个文件。

当前已验证哈希：

| 文件 | SHA-256 |
|---|---|
| `ASWDEBUG.dll` | `225DA29D3BBC4312B6055C2CE51CE87E03C5B29B0BECB01FA22A4B422F6122B2` |
| `0Harmony.dll` | `E271D22A7C32BFCA105D0E471C04EEE14EEC6142D37A30CD73CB7D32C7D1DD0F` |
| `Mono.Cecil.dll` | `F622A5B5B5ACECD40D11D2188C93AADDF2D1CFE04B8ABB897F504309EAF9610B` |
| `BouncyCastle.Crypto.dll` | `1985B85BB44BE6C6EAF35E02EF11E23A890E809B8EC2E53210A4AD5A85B26C70` |
| x86 `winhttp.dll` | `CC643B54484F694A8E0E6641CAC79D74141009AFC9E24D826F6FD7FD48FD182A` |
| `doorstop_config.ini` | `B83884899D2198177D84BBBB2DA02A2DD42F13E80849FEBE177F1D5E6A944C32` |

上述六个目标文件的长度和哈希均与源文件一致。

固定哈希仅用于记录本次状态。以后源码或依赖更新后，正确标准是“新源文件哈希与新目标文件哈希一致”，不是继续要求匹配本表旧值。
