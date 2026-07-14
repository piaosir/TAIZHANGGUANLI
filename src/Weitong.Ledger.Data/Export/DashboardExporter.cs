using System.Text.Json;
using Weitong.Ledger.Core;
using Weitong.Ledger.Core.Stats;
using Weitong.Ledger.Data.Import;

namespace Weitong.Ledger.Data.Export;

/// <summary>
/// 把导入结果 + 统计口径导出成看板用的 JSON（window.LEDGER = {...}）。
/// 金额统一以"万元"数字输出，前端直接展示。此模块产出的数据结构
/// 将来在 WebView2 里由 C# 宿主原样注入，前端 dashboard.html 复用。
/// </summary>
public static class DashboardExporter
{
    private static double Wan(long cents) => Math.Round((double)cents / Money.CentsPerWan, 2);

    public sealed record TeamTarget(long RevenueCents, long ProfitCents, long CostCeilingCents);

    public static string BuildJson(ImportResult import, TeamTarget target, string sourceName)
    {
        var contracts = import.Contracts;
        var funnel = StatsEngine.BuildFunnel(contracts);

        var strictRev = StatsEngine.AchievedRevenue(contracts, CompletionBasis.Strict);
        var weightRev = StatsEngine.AchievedRevenue(contracts, CompletionBasis.Weighted);
        var strictProfit = StatsEngine.AchievedProfit(contracts, CompletionBasis.Strict);
        var weightProfit = StatsEngine.AchievedProfit(contracts, CompletionBasis.Weighted);
        var strictCost = StatsEngine.AchievedCost(contracts, CompletionBasis.Strict);
        var weightCost = StatsEngine.AchievedCost(contracts, CompletionBasis.Weighted);

        object Metric(long achieved, long tgt, bool higherBetter) => new
        {
            achieved = Wan(achieved),
            target = Wan(tgt),
            rate = tgt != 0 ? Math.Round((double)achieved / tgt * 100, 1) : (double?)null,
            gap = Wan(achieved - tgt),
            shortfall = higherBetter ? Wan(Math.Max(0, tgt - achieved)) : Wan(Math.Max(0, achieved - tgt)),
            onTrack = higherBetter ? achieved >= tgt : (tgt == 0 || achieved <= tgt),
        };

        // 各销售排名（按已签约收入）
        var people = import.BySalesperson.Keys
            .Select(name =>
            {
                var mine = contracts.Where(c => c.SalesPersonName == name).ToList();
                return new
                {
                    name,
                    count = mine.Count,
                    signedRevenue = Wan(StatsEngine.AchievedRevenue(mine, CompletionBasis.Strict)),
                    signedProfit = Wan(StatsEngine.AchievedProfit(mine, CompletionBasis.Strict)),
                    weightedRevenue = Wan(StatsEngine.AchievedRevenue(mine, CompletionBasis.Weighted)),
                    totalRevenue = Wan(mine.Sum(c => c.RevenueEstCents)),
                };
            })
            .OrderByDescending(p => p.signedRevenue)
            .ToList();

        // 销售 × 层级 矩阵（堆叠柱，值=预计收入 万元）
        var personNames = people.Select(p => p.name).ToList();
        var stageSeries = Stages.All.Select(meta => new
        {
            stage = meta.ChineseName,
            data = personNames.Select(pn =>
                Wan(contracts.Where(c => c.SalesPersonName == pn && c.Stage == meta.Stage)
                             .Sum(c => c.RevenueEstCents))).ToList()
        }).ToList();

        var monthly = StatsEngine.BuildMonthly(contracts, PaymentKind.Forecast, target.RevenueCents);

        var model = new
        {
            meta = new
            {
                source = sourceName,
                rowsImported = import.RowsImported,
                anomalies = import.Anomalies.Count,
                scopeNote = "口径参照 表1「行业团队」指标；台账含少量物联网/其他，故略高于该团队口径。",
            },
            totals = new
            {
                revenue = Wan(import.TotalRevenueCents),
                profit = Wan(import.TotalProfitCents),
                receivedToDate = Wan(contracts.Sum(c => c.ReceivedToDateCents)),
                contractAmount = Wan(contracts.Sum(c => c.ContractAmountCents)),
            },
            funnel = funnel.Buckets.Select(b => new
            {
                stage = b.StageName,
                count = b.Count,
                revenue = Wan(b.RevenueCents),
                profit = Wan(b.ProfitCents),
                weightedRevenue = Wan(b.WeightedRevenueCents),
            }),
            completion = new
            {
                strict = new
                {
                    revenue = Metric(strictRev, target.RevenueCents, true),
                    profit = Metric(strictProfit, target.ProfitCents, true),
                    cost = Metric(strictCost, target.CostCeilingCents, false),
                },
                weighted = new
                {
                    revenue = Metric(weightRev, target.RevenueCents, true),
                    profit = Metric(weightProfit, target.ProfitCents, true),
                    cost = Metric(weightCost, target.CostCeilingCents, false),
                },
            },
            salespeople = people,
            stagePerson = new { persons = personNames, series = stageSeries },
            monthly = monthly.Select(m => new
            {
                label = m.Label,
                bucket = Wan(m.BucketCents),
                cumulative = Wan(m.CumulativeCents),
                pacing = Wan(m.PacingTargetCents),
            }),
            anomalies = import.Anomalies.Take(50).Select(a => new
            {
                sheet = a.Sheet, row = a.RowNumber, field = a.Field, value = a.RawValue, reason = a.Reason,
            }),
        };

        var opts = new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        };
        return JsonSerializer.Serialize(model, opts);
    }

    public static void WriteDataFile(ImportResult import, TeamTarget target, string sourceName, string outJsPath)
    {
        var json = BuildJson(import, target, sourceName);
        File.WriteAllText(outJsPath, "window.LEDGER = " + json + ";\n");
    }
}
