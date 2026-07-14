using System.Text;
using System.Text.Json;
using Weitong.Ledger.Core;

namespace Weitong.Ledger.App.Services;

/// <summary>
/// 合同的克隆、JSON 序列化与差异摘要。用于管理员审批：提案携带合同快照，
/// 销售端确认后按快照落库；差异摘要供人阅读（字段 旧值→新值）。
/// </summary>
public static class ContractOps
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static Contract Clone(Contract c) => new()
    {
        Id = c.Id,
        ContractUid = c.ContractUid,
        OwnerCode = c.OwnerCode,
        Stage = c.Stage,
        ContractNo = c.ContractNo,
        SignDate = c.SignDate,
        ExpectedSignDate = c.ExpectedSignDate,
        ProjectAttribute = c.ProjectAttribute,
        ProjectName = c.ProjectName,
        PartyA = c.PartyA,
        PartyB = c.PartyB,
        SalesPersonName = c.SalesPersonName,
        MarketField = c.MarketField,
        BusinessSegment = c.BusinessSegment,
        ProductType = c.ProductType,
        Currency = c.Currency,
        ContractAmountCents = c.ContractAmountCents,
        RevenueEstCents = c.RevenueEstCents,
        CostEstCents = c.CostEstCents,
        ReceivedToDateCents = c.ReceivedToDateCents,
        ValidFrom = c.ValidFrom,
        ValidTo = c.ValidTo,
        ServiceMonthsThisYear = c.ServiceMonthsThisYear,
        CultivationSource = c.CultivationSource,
        WinProbabilityOverride = c.WinProbabilityOverride,
        CreatedAt = c.CreatedAt,
        CreatedBy = c.CreatedBy,
        UpdatedAt = c.UpdatedAt,
        UpdatedBy = c.UpdatedBy,
        RowVersion = c.RowVersion,
        IsDeleted = c.IsDeleted,
        Payments = c.Payments.Select(p => new MonthlyPayment
        {
            Kind = p.Kind, PeriodMonth = p.PeriodMonth, IsCumulative = p.IsCumulative, AmountCents = p.AmountCents,
        }).ToList(),
    };

    public static string ToJson(Contract c) => JsonSerializer.Serialize(c, JsonOpts);
    public static Contract? FromJson(string? json) =>
        string.IsNullOrEmpty(json) ? null : JsonSerializer.Deserialize<Contract>(json, JsonOpts);

    private static string Y(long cents) => Money.ToYuan(cents).ToString("N0");
    private static string? D(DateOnly? d) => d?.ToString("yyyy-MM-dd");
    private static long Month(Contract c, int m, bool cum) =>
        c.Payments.Where(p => p.Kind == PaymentKind.Forecast && p.PeriodMonth == m && p.IsCumulative == cum).Sum(p => p.AmountCents);

    /// <summary>逐字段比较 before→after，产出中文摘要；无变化返回空串。</summary>
    public static string Summarize(Contract before, Contract after)
    {
        var parts = new List<string>();
        void Cmp(string label, string? a, string? b) { if ((a ?? "") != (b ?? "")) parts.Add($"{label} {Show(a)}→{Show(b)}"); }
        void CmpMoney(string label, long a, long b) { if (a != b) parts.Add($"{label} {Y(a)}→{Y(b)} 元"); }

        Cmp("合同层级", Stages.ChineseName(before.Stage), Stages.ChineseName(after.Stage));
        Cmp("合同编号", before.ContractNo, after.ContractNo);
        Cmp("项目名称", before.ProjectName, after.ProjectName);
        Cmp("合同甲方", before.PartyA, after.PartyA);
        Cmp("销售人员", before.SalesPersonName, after.SalesPersonName);
        Cmp("市场领域", before.MarketField, after.MarketField);
        Cmp("产品类型", before.ProductType, after.ProductType);
        Cmp("项目属性", before.ProjectAttribute, after.ProjectAttribute);
        Cmp("币种", before.Currency, after.Currency);
        CmpMoney("合同金额", before.ContractAmountCents, after.ContractAmountCents);
        CmpMoney("本年收入", before.RevenueEstCents, after.RevenueEstCents);
        CmpMoney("本年成本", before.CostEstCents, after.CostEstCents);
        Cmp("签约日期", D(before.SignDate), D(after.SignDate));
        Cmp("培育来源", before.CultivationSource, after.CultivationSource);

        // 月度到款
        void CmpMonth(string label, int m, bool cum) { long a = Month(before, m, cum), b = Month(after, m, cum); if (a != b) parts.Add($"{label} {Y(a)}→{Y(b)} 元"); }
        CmpMonth("1-4月到款", 4, true);
        for (int m = 5; m <= 12; m++) CmpMonth($"{m}月到款", m, false);

        return string.Join("；", parts);
    }

    private static string Show(string? s) => string.IsNullOrWhiteSpace(s) ? "（空）" : s;

    /// <summary>合同的有序 (字段名, 值) 列表，供审批对照逐行展示。</summary>
    public static IReadOnlyList<(string Label, string Value)> Fields(Contract c)
    {
        string M(long cents) => Y(cents) + " 元";
        var list = new List<(string, string)>
        {
            ("合同层级", Stages.ChineseName(c.Stage)),
            ("合同编号", c.ContractNo ?? ""),
            ("项目名称", c.ProjectName ?? ""),
            ("合同甲方", c.PartyA ?? ""),
            ("销售人员", c.SalesPersonName ?? ""),
            ("市场领域", c.MarketField ?? ""),
            ("产品类型", c.ProductType ?? ""),
            ("项目属性", c.ProjectAttribute ?? ""),
            ("币种", c.Currency ?? ""),
            ("合同金额", M(c.ContractAmountCents)),
            ("本年收入", M(c.RevenueEstCents)),
            ("本年成本", M(c.CostEstCents)),
            ("本年利润", M(c.ProfitEstCents)),
            ("1-4月到款", M(Month(c, 4, true))),
        };
        for (int m = 5; m <= 12; m++) list.Add(($"{m}月到款", M(Month(c, m, false))));
        list.Add(("预计到款", M(c.PaymentForecastTotalCents)));
        list.Add(("签约日期", D(c.SignDate) ?? ""));
        list.Add(("培育来源", c.CultivationSource ?? ""));
        return list;
    }

    /// <summary>新增/删除提案的单行摘要。</summary>
    public static string Describe(Contract c)
    {
        var name = string.IsNullOrWhiteSpace(c.ProjectName) ? (c.PartyA ?? c.ContractNo ?? "（未命名）") : c.ProjectName;
        return $"{Stages.ChineseName(c.Stage)} · {name} · 本年收入 {Y(c.RevenueEstCents)} 元";
    }
}
