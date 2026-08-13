using System;

namespace ASWDEBUG.Verify
{
    internal sealed class VeriGateException : Exception
    {
        internal VeriGateException(uint errorCode)
            : base(GetMessage(errorCode))
        {
            ErrorCode = errorCode;
        }

        internal uint ErrorCode { get; private set; }

        internal bool IsAuthenticationFailure
        {
            get { return ErrorCode == 2 || ErrorCode == 4 || ErrorCode == 5 || ErrorCode == 6; }
        }

        private static string GetMessage(uint errorCode)
        {
            switch (errorCode)
            {
                case 1:
                    return "直登卡或客户端配置格式不正确。";
                case 2:
                    return "直登卡无效、已过期，或当前会话已经失效。";
                case 3:
                    return "当前直登卡无权访问，或设备、会话数量已经达到上限。";
                case 4:
                    return "验证状态发生冲突，请重试或在后台处理旧设备。";
                case 5:
                    return "验证凭证已经过期。";
                case 6:
                    return "验证凭证被重复使用，当前会话已撤销。";
                case 7:
                    return "网络验证服务暂时不可用，请稍后重试。";
                case 8:
                    return "Windows 安全存储不可用。";
                case 9:
                    return "网络验证服务返回了无效响应。";
                case 10:
                    return "网络验证内容完整性检查失败。";
                case 11:
                    return "此操作需要额外身份验证。";
                case 12:
                    return "服务器要求的额外身份验证方式不可用。";
                default:
                    return "网络验证失败，错误代码：" + errorCode + "。";
            }
        }
    }
}
