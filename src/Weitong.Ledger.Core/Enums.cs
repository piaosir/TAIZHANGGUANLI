namespace Weitong.Ledger.Core;

/// <summary>
/// 合同层级（销售漏斗，5 类）。这是全系统最核心的枚举：
/// 既驱动漏斗可视化，也承载"成交概率"用于加权（预测）完成率口径。
/// </summary>
public enum ContractStage
{
    /// <summary>已签约：已在合同系统归档、成为生效合同。</summary>
    Signed = 1,
    /// <summary>待签约：方案与条款已明确，正在走合同审批流程。</summary>
    PendingSign = 2,
    /// <summary>洽谈中：正在与用户沟通方案与条款。</summary>
    Negotiating = 3,
    /// <summary>项目线索：有明确方向/项目名/甲方，初步需求沟通。</summary>
    Lead = 4,
    /// <summary>培育中：来自部委/用户规划文件的市场方向或需求。</summary>
    Cultivating = 5,
}

/// <summary>付款记录类型：预计 or 实际。同一张月度表两用。</summary>
public enum PaymentKind
{
    /// <summary>预计到款（计划）。</summary>
    Forecast = 0,
    /// <summary>实际到款（已收）。</summary>
    Actual = 1,
}

/// <summary>用户角色。纯离线场景下用于本地权限分层。</summary>
public enum UserRole
{
    /// <summary>销售：仅可见/可编辑本人名下记录。</summary>
    Sales = 0,
    /// <summary>组长/经理：可汇总全组、下发指标（唯一 hub）。</summary>
    Manager = 1,
    /// <summary>管理员：用户/字典/备份维护。</summary>
    Admin = 2,
}

/// <summary>完成率口径开关。用户已决定"两个都要，可切换"。</summary>
public enum CompletionBasis
{
    /// <summary>严口径：仅"已签约"计入。国企考核常用，数字最实。</summary>
    Strict = 0,
    /// <summary>预测口径：各层级金额 × 成交概率 加权求和。</summary>
    Weighted = 1,
}

/// <summary>
/// 合同层级的元数据：中文名、默认成交概率、漏斗排序。
/// 成交概率可被 admin 在字典里覆盖（这里给业务默认值）。
/// </summary>
public static class Stages
{
    public sealed record Meta(ContractStage Stage, string Code, string ChineseName, double DefaultWinProbability, int FunnelOrder);

    public static readonly IReadOnlyList<Meta> All = new List<Meta>
    {
        new(ContractStage.Signed,      "signed",   "已签约",   1.00, 1),
        new(ContractStage.PendingSign, "pending",  "待签约",   0.80, 2),
        new(ContractStage.Negotiating, "negotiate","洽谈中",   0.50, 3),
        new(ContractStage.Lead,        "lead",     "项目线索", 0.20, 4),
        new(ContractStage.Cultivating, "cultivate","培育中",   0.05, 5),
    };

    private static readonly Dictionary<string, ContractStage> ByChinese =
        All.ToDictionary(m => m.ChineseName, m => m.Stage);

    private static readonly Dictionary<ContractStage, Meta> ByStage =
        All.ToDictionary(m => m.Stage, m => m);

    public static Meta MetaOf(ContractStage s) => ByStage[s];
    public static string ChineseName(ContractStage s) => ByStage[s].ChineseName;
    public static double DefaultWinProbability(ContractStage s) => ByStage[s].DefaultWinProbability;

    /// <summary>把 Excel 里脏的中文层级文本解析成枚举。无法识别返回 null。</summary>
    public static ContractStage? Parse(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var t = raw.Trim();
        if (ByChinese.TryGetValue(t, out var s)) return s;
        // 容错：包含关键字即可
        foreach (var m in All)
            if (t.Contains(m.ChineseName)) return m.Stage;
        return null;
    }
}
