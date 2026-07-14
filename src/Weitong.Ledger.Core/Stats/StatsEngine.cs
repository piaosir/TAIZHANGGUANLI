namespace Weitong.Ledger.Core.Stats;

/// <summary>
/// 统计口径引擎（纯函数，无副作用，可单元测试）。
/// 全系统所有"完成率 / 差距 / 漏斗 / 月度"数字都从这里出，保证口径唯一。
/// </summary>
public static class StatsEngine
{
    /// <summary>月度桶顺序：1-4月累计，随后 5..12 各单月。</summary>
    private static readonly (int Month, bool Cumulative, string Label)[] BucketDefs =
    {
        (4, true,  "1-4月"),
        (5, false, "5月"), (6, false, "6月"), (7, false, "7月"), (8, false, "8月"),
        (9, false, "9月"), (10, false, "10月"), (11, false, "11月"), (12, false, "12月"),
    };

    // ——————————————————————————————————————————————————————————————
    // 漏斗
    // ——————————————————————————————————————————————————————————————
    public static FunnelResult BuildFunnel(IEnumerable<Contract> contracts)
    {
        var list = contracts.Where(c => !c.IsDeleted).ToList();
        var buckets = new List<StageBucket>();

        foreach (var meta in Stages.All) // 固定漏斗顺序
        {
            var inStage = list.Where(c => c.Stage == meta.Stage).ToList();
            long rev = inStage.Sum(c => c.RevenueEstCents);
            long cost = inStage.Sum(c => c.CostEstCents);
            long profit = inStage.Sum(c => c.ProfitEstCents);
            long amt = inStage.Sum(c => c.ContractAmountCents);
            long wRev = inStage.Sum(c => Money.Weight(c.RevenueEstCents, c.EffectiveWinProbability));
            long wProfit = inStage.Sum(c => Money.Weight(c.ProfitEstCents, c.EffectiveWinProbability));

            buckets.Add(new StageBucket(
                meta.Stage, meta.ChineseName, inStage.Count,
                amt, rev, cost, profit, wRev, wProfit));
        }

        return new FunnelResult(
            buckets,
            list.Count,
            list.Sum(c => c.RevenueEstCents),
            list.Sum(c => c.ProfitEstCents));
    }

    // ——————————————————————————————————————————————————————————————
    // 达成（完成率 / 差距）—— 支持严口径 & 预测口径
    // ——————————————————————————————————————————————————————————————

    /// <summary>某口径下"已达成"的收入。</summary>
    public static long AchievedRevenue(IEnumerable<Contract> contracts, CompletionBasis basis) =>
        basis == CompletionBasis.Strict
            ? contracts.Where(c => !c.IsDeleted && c.Stage == ContractStage.Signed).Sum(c => c.RevenueEstCents)
            : contracts.Where(c => !c.IsDeleted).Sum(c => Money.Weight(c.RevenueEstCents, c.EffectiveWinProbability));

    public static long AchievedProfit(IEnumerable<Contract> contracts, CompletionBasis basis) =>
        basis == CompletionBasis.Strict
            ? contracts.Where(c => !c.IsDeleted && c.Stage == ContractStage.Signed).Sum(c => c.ProfitEstCents)
            : contracts.Where(c => !c.IsDeleted).Sum(c => Money.Weight(c.ProfitEstCents, c.EffectiveWinProbability));

    /// <summary>某口径下已发生/预计的成本（成本控制指标是天花板，越低越好）。</summary>
    public static long AchievedCost(IEnumerable<Contract> contracts, CompletionBasis basis) =>
        basis == CompletionBasis.Strict
            ? contracts.Where(c => !c.IsDeleted && c.Stage == ContractStage.Signed).Sum(c => c.CostEstCents)
            : contracts.Where(c => !c.IsDeleted).Sum(c => Money.Weight(c.CostEstCents, c.EffectiveWinProbability));

    private static CompletionMetric BuildMetric(
        string name, MetricDirection dir, CompletionBasis basis, long target, long achieved)
    {
        double? rate = target != 0 ? (double)achieved / target : (achieved == 0 ? (double?)null : null);
        return new CompletionMetric(name, dir, basis, target, achieved, achieved - target, rate);
    }

    /// <summary>三条线（收入/利润/成本）在给定口径下的达成。</summary>
    public static IReadOnlyList<CompletionMetric> BuildCompletions(
        IEnumerable<Contract> contracts, Target? target, CompletionBasis basis)
    {
        var list = contracts as IList<Contract> ?? contracts.ToList();
        long tRev = target?.RevenueTargetCents ?? 0;
        long tProfit = target?.ProfitTargetCents ?? 0;
        long tCost = target?.CostCeilingCents ?? 0;

        return new List<CompletionMetric>
        {
            BuildMetric("收入",   MetricDirection.HigherIsBetter, basis, tRev,    AchievedRevenue(list, basis)),
            BuildMetric("利润",   MetricDirection.HigherIsBetter, basis, tProfit, AchievedProfit(list, basis)),
            BuildMetric("成本控制", MetricDirection.LowerIsBetter,  basis, tCost,   AchievedCost(list, basis)),
        };
    }

    // ——————————————————————————————————————————————————————————————
    // 月度到款时间轴（累计曲线 + 匀速目标基准）
    // ——————————————————————————————————————————————————————————————

    /// <summary>
    /// 汇总一组合同的月度到款（默认预计口径），产出 9 个桶的累计曲线。
    /// pacingTarget 若给定（如全年收入指标），按"覆盖月数/12"匀速给出应达累计基准。
    /// </summary>
    public static IReadOnlyList<MonthlyPoint> BuildMonthly(
        IEnumerable<Contract> contracts, PaymentKind kind = PaymentKind.Forecast, long pacingTargetCents = 0)
    {
        var list = contracts.Where(c => !c.IsDeleted).ToList();
        var points = new List<MonthlyPoint>();
        long cumulative = 0;

        foreach (var (month, cml, label) in BucketDefs)
        {
            long bucket = list.Sum(c =>
                c.Payments.Where(p => p.Kind == kind && p.PeriodMonth == month && p.IsCumulative == cml)
                          .Sum(p => p.AmountCents));
            cumulative += bucket;
            // 匀速基准：该桶覆盖到第 month 个月 → target × month/12
            long pacing = pacingTargetCents == 0 ? 0
                : (long)Math.Round(pacingTargetCents * (month / 12.0), MidpointRounding.AwayFromZero);
            points.Add(new MonthlyPoint(label, month, bucket, cumulative, pacing));
        }
        return points;
    }

    // ——————————————————————————————————————————————————————————————
    // 一页完整统计（个人页 / 团队页复用）
    // ——————————————————————————————————————————————————————————————
    public static EntityStats BuildEntityStats(
        string ownerKey, string displayName,
        IEnumerable<Contract> contracts, Target? target, CompletionBasis basis,
        PaymentKind monthlyKind = PaymentKind.Forecast)
    {
        var list = contracts.Where(c => !c.IsDeleted).ToList();
        return new EntityStats(
            ownerKey,
            displayName,
            BuildFunnel(list),
            BuildCompletions(list, target, basis),
            BuildMonthly(list, monthlyKind, target?.RevenueTargetCents ?? 0),
            list.Sum(c => c.ReceivedToDateCents));
    }
}
