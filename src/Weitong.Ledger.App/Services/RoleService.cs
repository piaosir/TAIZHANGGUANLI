namespace Weitong.Ledger.App.Services;

/// <summary>
/// 身份角色判定（按姓名）。姓名在 <see cref="CosSettings.AdminNames"/> 名单中的使用人即为管理员。
/// 管理员可在台账明细对全组数据发起增删改查与标记；对他人名下记录的改动需对应销售确认后生效。
/// </summary>
public sealed class RoleService
{
    private readonly HashSet<string> _admins;

    public RoleService(CosSettings cos)
    {
        _admins = new HashSet<string>(
            (cos.AdminNames ?? new List<string>())
                .Where(n => !string.IsNullOrWhiteSpace(n))
                .Select(Norm),
            StringComparer.Ordinal);
    }

    /// <summary>该姓名是否为管理员。</summary>
    public bool IsAdmin(string? name) => !string.IsNullOrWhiteSpace(name) && _admins.Contains(Norm(name));

    private static string Norm(string s) => s.Trim();
}
