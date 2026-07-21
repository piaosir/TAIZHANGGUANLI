namespace Weitong.Ledger.App.Services;

/// <summary>
/// 身份角色 / 所属团队判定。数据源 = cos.json 的人员名册 <see cref="CosSettings.Roster"/>（姓名→团队→角色）
/// 叠加兼容旧的 <see cref="CosSettings.AdminNames"/>。
/// <para>
/// 团队分权采用<b>物理隔离</b>（一个团队一个独立数据库 + 独立 COS 前缀，见 <see cref="TeamPartition"/>），
/// 因此本服务只负责回答「某人属于哪个团队、是不是本团队管理员」，不再做跨团队的可见性过滤——
/// 别的团队的数据根本不在本机库里，连管理员也看不到别团队。<b>管理员是团队内的管理员</b>（本团队内可编辑+通知），
/// 没有能看全部团队的超级管理员。
/// </para>
/// <para>名册未配置（空）时：按 AdminNames 判管理员、团队回退到本机自填名——保持旧的单库行为，升级不锁死。</para>
/// </summary>
public sealed class RoleService
{
    private readonly Dictionary<string, RosterMember> _byName;  // 归一化姓名 → 名册项
    private readonly HashSet<string> _adminNames;               // 兼容旧 AdminNames

    public RoleService(CosSettings cos)
    {
        _byName = new Dictionary<string, RosterMember>(StringComparer.Ordinal);
        foreach (var m in cos.Roster ?? new List<RosterMember>())
        {
            var n = Norm(m.Name);
            if (n.Length == 0) continue;
            _byName[n] = m;   // 同名后者覆盖前者
        }
        _adminNames = new HashSet<string>(
            (cos.AdminNames ?? new List<string>())
                .Where(s => !string.IsNullOrWhiteSpace(s)).Select(Norm),
            StringComparer.Ordinal);
    }

    /// <summary>名册是否已配置（有任一有效成员）。未配置时团队回退本机自填、管理员按 AdminNames 判定。</summary>
    public bool RosterConfigured => _byName.Count > 0;

    /// <summary>该姓名是否本团队管理员（团队内可编辑台账、发通知、设团队目标）。名册角色 admin/领导，或在旧 AdminNames 名单中。</summary>
    public bool IsAdmin(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return false;
        var n = Norm(name);
        if (_adminNames.Contains(n)) return true;
        return _byName.TryGetValue(n, out var m) && IsAdminRole(m.Role);
    }

    /// <summary>该姓名在名册中的所属团队；不在名册返回 null（调用方用本机自填团队兜底）。
    /// 这个团队决定此人用哪一个物理库 / 哪一个 COS 前缀。</summary>
    public string? TeamOf(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        return _byName.TryGetValue(Norm(name), out var m) && !string.IsNullOrWhiteSpace(m.Team)
            ? m.Team.Trim() : null;
    }

    private static bool IsAdminRole(string? role)
    {
        var r = (role ?? "").Trim();
        return r.Equals("admin", StringComparison.OrdinalIgnoreCase)
            || r.Equals("leader", StringComparison.OrdinalIgnoreCase)
            || r == "管理员" || r == "领导";
    }

    private static string Norm(string s) => s.Trim();
}
