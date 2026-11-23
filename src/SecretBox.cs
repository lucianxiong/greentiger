using System;
using System.Security.Cryptography;
using System.Text;

namespace GreenTiger
{
    /// <summary>
    /// 本机密钥保管（DPAPI / DataProtectionScope.CurrentUser）。
    ///
    /// 目标：config.json 里的 API Key 不再明文落盘——文件被同步盘带走、被备份
    /// 泄露、被同机其他账户读取时拿到的都是密文；只有当前 Windows 用户在本机
    /// 才能解开。DPAPI 不可用（组策略禁用等极端情况）时 Encrypt 返回 null，
    /// 调用方回退明文存储并写日志，宁可降级也不让配置存不下来。
    /// 格式带 "dp1:" 前缀，将来换算法可平滑再升一版。
    /// </summary>
    public static class SecretBox
    {
        private const string Prefix = "dp1:";

        // 附加熵：同用户下的其他进程同样能调 DPAPI，熵挡不住真正的同用户恶意代码，
        // 但能挡住「拿到密文顺手解一下」的通用脚本。不是强边界，是廉价加固。
        private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("GreenTiger.ProviderKey.v1");

        public static bool IsEncrypted(string value)
        {
            return value != null && value.StartsWith(Prefix, StringComparison.Ordinal);
        }

        /// <summary>加密成 "dp1:Base64"。空串返回空串；失败返回 null（调用方回退明文）。</summary>
        public static string Encrypt(string plain)
        {
            if (string.IsNullOrEmpty(plain)) return "";
            try
            {
                byte[] raw = Encoding.UTF8.GetBytes(plain);
                byte[] blob = ProtectedData.Protect(raw, Entropy, DataProtectionScope.CurrentUser);
                return Prefix + Convert.ToBase64String(blob);
            }
            catch (Exception ex)
            {
                Log.Warn("DPAPI 加密失败，密钥将以明文保存在本机 config.json：" + ex.Message);
                return null;
            }
        }

        /// <summary>解密 "dp1:Base64"。非本格式原样返回（兼容历史明文值）；解密失败返回 null。</summary>
        public static string Decrypt(string value)
        {
            if (string.IsNullOrEmpty(value)) return "";
            if (!IsEncrypted(value)) return value;
            try
            {
                byte[] raw = Convert.FromBase64String(value.Substring(Prefix.Length));
                byte[] plain = ProtectedData.Unprotect(raw, Entropy, DataProtectionScope.CurrentUser);
                return Encoding.UTF8.GetString(plain);
            }
            catch
            {
                // 换了 Windows 用户 / 换机器 / 熵被改：解不开就是解不开，返回 null
                // 让调用方走「保留明文（通常为空）」的兜底，用户重新填 Key 即可。
                return null;
            }
        }
    }
}
