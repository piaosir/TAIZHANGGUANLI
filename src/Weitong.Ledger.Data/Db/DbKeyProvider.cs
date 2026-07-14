using System.Security.Cryptography;

namespace Weitong.Ledger.Data.Db;

/// <summary>
/// 本地加密库主密钥的生成与保管。
/// 首次运行随机生成 32 字节密钥 → 以 Windows DPAPI(当前用户) 加密后落盘；
/// 之后每次读取解密。密钥文件即便被拷走，脱离本机本用户也无法解密。
/// 用作 SQLCipher 的口令（其内部再经 KDF 派生实际加密密钥）。
/// </summary>
public static class DbKeyProvider
{
    /// <summary>返回 SQLCipher 口令（十六进制字符串）。keyFilePath 不存在则创建。</summary>
    public static string GetOrCreate(string keyFilePath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(keyFilePath)!);
        if (File.Exists(keyFilePath))
        {
            var protectedBytes = File.ReadAllBytes(keyFilePath);
            var raw = ProtectedData.Unprotect(protectedBytes, Entropy, DataProtectionScope.CurrentUser);
            return Convert.ToHexString(raw);
        }

        var key = RandomNumberGenerator.GetBytes(32);
        var protectedKey = ProtectedData.Protect(key, Entropy, DataProtectionScope.CurrentUser);
        File.WriteAllBytes(keyFilePath, protectedKey);
        return Convert.ToHexString(key);
    }

    // 附加熵，增加离线暴力破解成本
    private static readonly byte[] Entropy = "WeitongLedger.v1"u8.ToArray();
}
