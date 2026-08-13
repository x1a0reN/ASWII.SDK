using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace ASWDEBUG.Verify
{
    // 读取登录器在注入时通过 DPAPI 写入的一次性 handoff（卡密 + 版本 + 受保护角色名单）。
    // handoff 文件：%LocalAppData%\x1a0reN.Launcher\VeriGate\direct-card-handoff-<pid>.dpapi
    // 载荷格式（v2）：
    //   sv2
    //   <timestamp_ticks>
    //   <direct_card>
    //   <edition>                          # normal | releasea | core
    //   <comma_separated_character_ids>    # 普通版为定制版 character_id 名单，其余为空
    internal static class SurvivalHandoff
    {
        private const string HandoffCredentialPrefix = "direct-card-handoff-";
        private const string TenantId = "019f8e44-942f-73e2-9ad6-f4bcb615b07f";
        private const string ApplicationId = "019f8e5f-805c-7211-9d07-b09d5f109780";
        private const string EnvironmentId = "019f8e60-2796-78d8-95fe-d40bc7f3637f";

        internal sealed class Result
        {
            internal string DirectCard = string.Empty;
            internal string Edition = string.Empty;
            internal readonly List<ulong> ProtectedCharacterIds = new List<ulong>();
        }

        private static string StorageRoot
        {
            get
            {
                return Path.Combine(
                    Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                        "x1a0reN.Launcher"),
                    "VeriGate");
            }
        }

        internal static Result TryLoad()
        {
            try
            {
                int processId = Process.GetCurrentProcess().Id;
                string path = Path.Combine(
                    StorageRoot,
                    HandoffCredentialPrefix +
                    processId.ToString(CultureInfo.InvariantCulture) +
                    ".dpapi");
                if (!File.Exists(path)) return null;

                byte[] encrypted = null;
                byte[] plaintext = null;
                try
                {
                    encrypted = File.ReadAllBytes(path);
                    plaintext = ProtectedData.Unprotect(
                        encrypted,
                        GetHandoffEntropy(processId),
                        DataProtectionScope.CurrentUser);
                    return Parse(Encoding.UTF8.GetString(plaintext));
                }
                finally
                {
                    if (encrypted != null) Array.Clear(encrypted, 0, encrypted.Length);
                    if (plaintext != null) Array.Clear(plaintext, 0, plaintext.Length);
                    TryDelete(path);
                }
            }
            catch
            {
                return null;
            }
        }

        private static Result Parse(string payload)
        {
            string[] lines = (payload ?? string.Empty).Replace("\r\n", "\n").Split('\n');
            Result result = new Result();
            if (lines.Length >= 2 && lines[0] == "sv2")
            {
                result.DirectCard = lines.Length > 2 ? lines[2].Trim() : string.Empty;
                result.Edition = lines.Length > 3 ? lines[3].Trim() : string.Empty;
                if (lines.Length > 4)
                {
                    string ids = lines[4].Trim();
                    if (!string.IsNullOrEmpty(ids))
                    {
                        foreach (string part in ids.Split(','))
                        {
                            ulong id;
                            if (ulong.TryParse(part.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out id) && id != 0UL)
                            {
                                result.ProtectedCharacterIds.Add(id);
                            }
                        }
                    }
                }
            }
            else
            {
                // v1 兼容：<timestamp_ticks>\n<card>
                result.DirectCard = lines.Length > 1 ? lines[1].Trim() : string.Empty;
            }
            return result;
        }

        private static byte[] GetHandoffEntropy(int processId)
        {
            return Encoding.UTF8.GetBytes(
                "VeriGate.DirectCard.Handoff|" +
                TenantId + "|" +
                ApplicationId + "|" +
                EnvironmentId + "|" +
                processId.ToString(CultureInfo.InvariantCulture));
        }

        private static void TryDelete(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { }
        }
    }
}
