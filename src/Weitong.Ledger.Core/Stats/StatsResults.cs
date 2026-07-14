namespace Weitong.Ledger.Core.Stats;

/// <summary>漏斗中某一层级的汇总。</summary>
public sealed record StageBucket(
    ContractStage Stage,
    string StageName,
    int Count,
    long ContractAmountCents,
    long RevenueCents,
    long CostCents,
    long ProfitCents,
    long WeightedRevenueCents,   // Σ 收入 × 成交概率
    long WeightedProfitCents);   // Σ 利润 × 成交概率

/// <summary>整条漏斗（含各层级 + 合计）。</summary>
public sealed record FunnelResult(
    IReadOnlyList<StageBucket> Buckets,
    int TotalCount,
    long TotalRevenueCents,
    long TotalProfitCents)
{
    public StageBucket? Signed => Buckets.FirstOrDefault(b => b.Stage == ContractStage.Signed);
}

public enum MetricDirection { HigherIsBetter, LowerIsBetter }

/// <summary>单个指标的达成情况。回答"距目标还差多少 / 完成率多少"。</summary>
public sealed record CompletionMetric(
    string Name,                // 收入 / 利润 / 成本控制
    MetricDirection Direction,
    CompletionBasis Basis,
    long TargetCents,
    long AchievedCents,
    long GapCents,              // Achieved − Target（HigherIsBetter 下负数=还差；LowerIsBetter 下正数=超支）
    double? Rate)              // 完成率；无目标时 null
{
    /// <summary>是否达标。</summary>
    public bool OnTrack => Direction == MetricDirection.HigherIsBetter
        ? AchievedCents >= TargetCents
        : (TargetCents == 0 || AchievedCents <= TargetCents);

    /// <summary>"还差"金额（正数）。HigherIsBetter 时 = 目标−达成且>0；否则 0。</summary>
    public long ShortfallCents => Direction == MetricDirection.HigherIsBetter
        ? Math.Max(0, TargetCents - AchievedCents)
        : Math.Max(0, AchievedCents - TargetCents);
}

/// <summary>1-12 月到款时间轴上的一个点。</summary>
public sealed record MonthlyPoint(
    string Label,               // "1-4月" / "5月" …
    int PeriodMonth,
    long BucketCents,           // 本桶金额
    long CumulativeCents,       // 截至本桶累计
    long PacingTargetCents);    // 该时点应达累计（匀速基准）

/// <summary>某销售/团队的一页完整统计。</summary>
public sealed record EntityStats(
    string OwnerKey,
    string DisplayName,
    FunnelResult Funnel,
    IReadOnlyList<CompletionMetric> Completions, // 当前口径下三条线
    IReadOnlyList<MonthlyPoint> Monthly,
    long ReceivedToDateCents);
