using System.Security.Cryptography;
using System.Text;

namespace Weitong.Ledger.App.Services;

/// <summary>
/// 团队分区：把团队名映射成稳定的 ASCII 令牌，用作<b>本地库子目录名</b>与<b>COS 对象前缀段</b>，
/// 实现「一个团队一个数据库」的物理隔离——不同团队的数据落在不同的本地库文件、不同的云端前缀下，
/// 互不可见（连管理员也看不到别的团队），从根上隔离而非仅靠界面过滤。
/// <para>令牌 = 团队名(去空白) 的 SHA256 前 4 字节 hex，避免中文/特殊字符直接进文件名或对象键。</para>
/// </summary>
public static class TeamPartition
{
    public static string Token(string? team)
    {
        var norm = (team ?? "").Trim();
        if (norm.Length == 0) return "default";
        using var sha = SHA256.Create();
        var h = sha.ComputeHash(Encoding.UTF8.GetBytes(norm));
        return "t-" + Convert.ToHexString(h, 0, 4).ToLowerInvariant();
    }
}
