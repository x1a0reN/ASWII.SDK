using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace ASWDEBUG.Verify
{
    internal static class VeriGateCredentialStore
    {
        private const string CredentialFileName = "direct-card.dpapi";
        private const string HandoffCredentialPrefix = "direct-card-handoff-";
        private static readonly TimeSpan HandoffLifetime = TimeSpan.FromMinutes(5);
        private static readonly byte[] Entropy = Encoding.UTF8.GetBytes(
            "VeriGate.DirectCard|" +
            VeriGateOptions.TenantId + "|" +
            VeriGateOptions.ApplicationId + "|" +
            VeriGateOptions.EnvironmentId);

        internal static string StorageRoot
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

        internal static string Load()
        {
            int processId = Process.GetCurrentProcess().Id;
            string handoffCredential = LoadHandoff(processId);
            if (!string.IsNullOrEmpty(handoffCredential))
            {
                return handoffCredential;
            }

            string path = Path.Combine(StorageRoot, CredentialFileName);
            if (!File.Exists(path))
            {
                throw new InvalidOperationException(
                    "未找到 Launcher 写入的网络验证凭据，请先通过 Launcher 登录。");
            }

            byte[] encrypted = File.ReadAllBytes(path);
            byte[] plaintext = null;
            try
            {
                plaintext = ProtectedData.Unprotect(
                    encrypted,
                    Entropy,
                    DataProtectionScope.CurrentUser);
                string directCard = Encoding.UTF8.GetString(plaintext).Trim();
                if (directCard.Length != 78)
                {
                    throw new InvalidOperationException("Launcher 网络验证凭据格式无效。");
                }
                return directCard;
            }
            catch (CryptographicException)
            {
                throw new InvalidOperationException(
                    "Launcher 网络验证凭据不属于当前 Windows 用户。");
            }
            finally
            {
                Array.Clear(encrypted, 0, encrypted.Length);
                if (plaintext != null)
                {
                    Array.Clear(plaintext, 0, plaintext.Length);
                }
            }
        }

        private static string LoadHandoff(int processId)
        {
            string path = GetHandoffPath(processId);
            if (!File.Exists(path))
            {
                return null;
            }

            byte[] encrypted = null;
            byte[] plaintext = null;
            try
            {
                encrypted = File.ReadAllBytes(path);
                plaintext = ProtectedData.Unprotect(
                    encrypted,
                    GetHandoffEntropy(processId),
                    DataProtectionScope.CurrentUser);
                string payload = Encoding.UTF8.GetString(plaintext);
                int separator = payload.IndexOf('\n');
                long createdTicks;
                if (separator <= 0 ||
                    !long.TryParse(
                        payload.Substring(0, separator),
                        NumberStyles.None,
                        CultureInfo.InvariantCulture,
                        out createdTicks))
                {
                    throw new InvalidOperationException(
                        "Launcher 一次性网络验证凭据格式无效。");
                }

                DateTime createdAt = new DateTime(createdTicks, DateTimeKind.Utc);
                DateTime now = DateTime.UtcNow;
                if (createdAt > now.AddMinutes(1) ||
                    now - createdAt > HandoffLifetime)
                {
                    throw new InvalidOperationException(
                        "Launcher 一次性网络验证凭据已过期。");
                }

                string directCard = payload.Substring(separator + 1).Trim();
                if (directCard.Length != 78)
                {
                    throw new InvalidOperationException(
                        "Launcher 一次性网络验证凭据格式无效。");
                }
                return directCard;
            }
            catch (CryptographicException)
            {
                throw new InvalidOperationException(
                    "Launcher 一次性网络验证凭据不属于当前 Windows 用户。");
            }
            finally
            {
                TryDelete(path);
                if (encrypted != null)
                {
                    Array.Clear(encrypted, 0, encrypted.Length);
                }
                if (plaintext != null)
                {
                    Array.Clear(plaintext, 0, plaintext.Length);
                }
            }
        }

        private static string GetHandoffPath(int processId)
        {
            return Path.Combine(
                StorageRoot,
                HandoffCredentialPrefix +
                processId.ToString(CultureInfo.InvariantCulture) +
                ".dpapi");
        }

        private static byte[] GetHandoffEntropy(int processId)
        {
            return Encoding.UTF8.GetBytes(
                "VeriGate.DirectCard.Handoff|" +
                VeriGateOptions.TenantId + "|" +
                VeriGateOptions.ApplicationId + "|" +
                VeriGateOptions.EnvironmentId + "|" +
                processId.ToString(CultureInfo.InvariantCulture));
        }

        private static void TryDelete(string path)
        {
            try
            {
                File.Delete(path);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}
