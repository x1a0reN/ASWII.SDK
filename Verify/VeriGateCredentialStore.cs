using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace ASWDEBUG.Verify
{
    internal static class VeriGateCredentialStore
    {
        private const string CredentialFileName = "direct-card.dpapi";
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
    }
}
