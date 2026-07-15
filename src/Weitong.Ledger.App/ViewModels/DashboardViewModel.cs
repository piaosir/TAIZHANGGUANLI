using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media;
using LiveChartsCore;
using LiveChartsCore.Measure;
using LiveChartsCore.SkiaSharpView;
using LiveChartsCore.SkiaSharpView.Painting;
using SkiaSharp;
using Weitong.Ledger.Core;
using Weitong.Ledger.Core.Stats;
using Weitong.Ledger.Data.Export;
using Weitong.Ledger.Data.Import;

namespace Weitong.Ledger.App.ViewModels;

public sealed record KpiTile(string Title, string RateText, string ValueText, string GapText,
    string StatusText, Brush StatusBrush, Brush StatusSoftBrush);

public sealed class AchievementRow
{
    public string Name { get; init; } = "";
    public int Count { get; init; }
    public decimal SignedRevenue { get; init; }
    public decimal SignedProfit { get; init; }
    public decimal Weighted { get; init; }
    public decimal Payment { get; init; }
    public decimal SharePct { get; init; }
    public bool IsTotal { get; init; }
    public FontWeight RowWeight => IsTotal ? FontWeights.SemiBold : FontWeights.Normal;
}

public sealed record AnomalyRow(string Sheet, int Row, string Field, string Value, string Reason);

/// <summary>
/// 达成总览视图模型。数据来源为合同集合(来自加密库或一次导入)，
/// 统计全部走 Core.StatsEngine。支持严/预测口径切换(仅重算 KPI 与排行)。
/// </summary>
public sealed class DashboardViewModel : INotifyPropertyChanged
{
    private static readonly SKColor Accent = new(0x1B, 0x5F, 0xA8);
    private static readonly SKColor ColSoft = new(0xA9, 0xC7, 0xE6);
    private static readonly SKColor AxisText = new(0x8A, 0x90, 0x99);
    private static readonly SKColor Separator = new(0xED, 0xEE, 0xF1);
    private static readonly SKColor[] StageColors =
    {
        new(0x16, 0x34, 0x5A), new(0x1B, 0x5F, 0xA8), new(0x4E, 0x86, 0xC6),
        new(0x8F, 0xB4, 0xDC), new(0xC3, 0xD6, 0xEC),
    };

    private static SolidColorBrush Frozen(byte r, byte g, byte b)
    { var br = new SolidColorBrush(Color.FromRgb(r, g, b)); br.Freeze(); return br; }
    private static readonly Brush GoodB = Frozen(0x1E, 0x8E, 0x3E), GoodS = Frozen(0xE6, 0xF4, 0xEA);
    private static readonly Brush WarnB = Frozen(0xB2, 0x6A, 0x00), WarnS = Frozen(0xFB, 0xF0, 0xDD);
    private static readonly Brush BadB = Frozen(0xC5, 0x22, 0x1F), BadS = Frozen(0xFB, 0xE9, 0xE8);
    private static readonly Brush MutedB = Frozen(0x8A, 0x90, 0x99), MutedS = Frozen(0xF0, 0xF1, 0xF3);

    // 目标默认「未设」(0)；由外壳按 团队×当前年 从加密库读入（见 MainWindow.ApplyTeamTarget）。
    // 不再写死年度数字，随年份逐年生效，避免多年后显示过期指标。
    private DashboardExporter.TeamTarget _target = new(0, 0, 0);

    private List<Contract> _contracts = new();
    private List<string> _people = new();
    private List<AnomalyRow> _anoms = new();
    private CompletionBasis _basis = CompletionBasis.Strict;

    public string MetaText { get; private set; } = "尚无数据";

    /// <summary>团队名（导出详细报告标题用）。由外壳按当前身份设置。</summary>
    public string TeamName { get; set; } = "行业市场组";

    public ObservableCollection<KpiTile> Kpis { get; } = new();
    public ObservableCollection<AchievementRow> Achievements { get; } = new();

    public ISeries[] RankSeries { get; private set; } = Array.Empty<ISeries>();
    public Axis[] RankX { get; private set; } = Array.Empty<Axis>();
    public Axis[] RankY { get; private set; } = Array.Empty<Axis>();
    public ISeries[] FunnelSeries { get; private set; } = Array.Empty<ISeries>();
    public Axis[] FunnelX { get; private set; } = Array.Empty<Axis>();
    public Axis[] FunnelY { get; private set; } = Array.Empty<Axis>();
    public ISeries[] MonthlySeries { get; private set; } = Array.Empty<ISeries>();
    public Axis[] MonthlyX { get; private set; } = Array.Empty<Axis>();
    public Axis[] MonthlyY { get; private set; } = Array.Empty<Axis>();
    public ISeries[] StackSeries { get; private set; } = Array.Empty<ISeries>();
    public Axis[] StackX { get; private set; } = Array.Empty<Axis>();
    public Axis[] StackY { get; private set; } = Array.Empty<Axis>();

    public bool IsStrict
    {
        get => _basis == CompletionBasis.Strict;
        set { if (value) { _basis = CompletionBasis.Strict; RecomputeBasisDependent(); } }
    }
    public bool IsWeighted
    {
        get => _basis == CompletionBasis.Weighted;
        set { if (value) { _basis = CompletionBasis.Weighted; RecomputeBasisDependent(); } }
    }

    /// <summary>从合同集合加载（DB 或导入均可）。</summary>
    public void Load(IEnumerable<Contract> contracts, string sourceNote, IEnumerable<AnomalyRow>? anomalies = null)
    {
        _contracts = contracts.Where(c => !c.IsDeleted).ToList();
        _people = _contracts.Select(c => c.SalesPersonName).Distinct().ToList();
        _anoms = (anomalies ?? Enumerable.Empty<AnomalyRow>()).ToList();
        MetaText = $"{sourceNote} · 共 {_contracts.Count} 条 · {_people.Count} 名销售" +
                   (_anoms.Count > 0 ? $" · 数据质量提示 {_anoms.Count} 项" : "");

        BuildAchievements();
        BuildFunnel();
        BuildMonthly();
        BuildStack();
        RecomputeBasisDependent();

        Raise(nameof(MetaText));
        Raise(nameof(FunnelSeries)); Raise(nameof(FunnelX)); Raise(nameof(FunnelY));
        Raise(nameof(MonthlySeries)); Raise(nameof(MonthlyX)); Raise(nameof(MonthlyY));
        Raise(nameof(StackSeries)); Raise(nameof(StackX)); Raise(nameof(StackY));
    }

    /// <summary>设置对标目标（个人页用不同目标）。调用后需再 Load 以重算。</summary>
    public void SetTarget(long revenueCents, long profitCents, long costCeilingCents)
        => _target = new DashboardExporter.TeamTarget(revenueCents, profitCents, costCeilingCents);

    /// <summary>更新对标目标并<b>即时重算 KPI</b>（编辑/同步团队目标后调用，无需重载合同）。</summary>
    public void ApplyTarget(long revenueCents, long profitCents, long costCeilingCents)
    {
        _target = new DashboardExporter.TeamTarget(revenueCents, profitCents, costCeilingCents);
        BuildKpis();   // 内部 Kpis.Clear() 后重填，UI 自动刷新
    }

    public void Load(ImportResult import, string sourceName)
        => Load(import.Contracts,
                $"源：{sourceName}",
                import.Anomalies.Take(200).Select(a => new AnomalyRow(
                    System.Text.RegularExpressions.Regex.Replace(a.Sheet, @"^\d+\.", ""),
                    a.RowNumber, a.Field, a.RawValue, a.Reason)));

    /// <summary>导出「达成总览 · 详细报告」多 sheet Excel（内容与本页完全一致）。</summary>
    public void ExportDetailReport(string path) =>
        DetailReportExporter.Export(
            _contracts,
            _target,
            TeamName,
            MetaText,
            _basis,
            _anoms.Select(a => new DetailReportExporter.Anomaly(a.Sheet, a.Row, a.Field, a.Value, a.Reason)),
            DateTime.Now,
            path);

    private IEnumerable<Contract> Of(string name) => _contracts.Where(c => c.SalesPersonName == name);

    private void RecomputeBasisDependent()
    {
        BuildKpis();
        BuildRanking();
        Raise(nameof(RankSeries)); Raise(nameof(RankX)); Raise(nameof(RankY));
        Raise(nameof(IsStrict)); Raise(nameof(IsWeighted));
    }

    private void BuildKpis()
    {
        var comp = StatsEngine.BuildCompletions(_contracts, new Target
        {
            RevenueTargetCents = _target.RevenueCents,
            ProfitTargetCents = _target.ProfitCents,
            CostCeilingCents = _target.CostCeilingCents,
        }, _basis);

        Kpis.Clear();
        Kpis.Add(TileFor("收入达成", comp[0]));
        Kpis.Add(TileFor("利润达成", comp[1]));
        Kpis.Add(TileFor("成本控制", comp[2]));

        long rec = _contracts.Sum(x => x.ReceivedToDateCents);
        bool noRevTarget = _target.RevenueCents == 0;
        double? rrate = noRevTarget ? null : (double)rec / _target.RevenueCents;
        Brush rb, rs; string rt;
        if (noRevTarget) (rb, rs, rt) = (MutedB, MutedS, "未设指标");
        else if (rrate >= 1) (rb, rs, rt) = (GoodB, GoodS, "达标");
        else if (rrate >= 0.6) (rb, rs, rt) = (WarnB, WarnS, "进行中");
        else (rb, rs, rt) = (BadB, BadS, "偏慢");
        Kpis.Add(new KpiTile("累计到款进度",
            rrate.HasValue ? (rrate.Value * 100).ToString("F1") + "%" : "—",
            noRevTarget ? $"已到款 {Money.FormatWan(rec)} / 指标 尚未设置"
                        : $"已到款 {Money.FormatWan(rec)} / 指标 {Money.FormatWan(_target.RevenueCents)}",
            "现金回收进度", rt, rb, rs));
    }

    private KpiTile TileFor(string title, CompletionMetric m)
    {
        Brush b, s; string st;
        bool noTarget = m.TargetCents == 0;
        if (m.Direction == MetricDirection.LowerIsBetter)
        {
            if (noTarget) (b, s, st) = (MutedB, MutedS, "未设目标");
            else (b, s, st) = m.OnTrack ? (GoodB, GoodS, "预算内") : (BadB, BadS, "超支");
        }
        else if (noTarget || m.Rate is null) { (b, s, st) = (MutedB, MutedS, "未设目标"); }
        else if (m.Rate >= 1) { (b, s, st) = (GoodB, GoodS, "达标"); }
        else if (m.Rate >= 0.8) { (b, s, st) = (WarnB, WarnS, "接近"); }
        else { (b, s, st) = (BadB, BadS, "欠佳"); }

        string gap = noTarget ? "尚未设置目标"
            : m.Direction == MetricDirection.LowerIsBetter
                ? (m.OnTrack ? $"距上限 {Money.FormatWan(m.TargetCents - m.AchievedCents)}" : $"超支 {Money.FormatWan(m.ShortfallCents)}")
                : (m.Rate >= 1 ? $"超目标 {Money.FormatWan(m.AchievedCents - m.TargetCents)}" : $"距目标还差 {Money.FormatWan(m.ShortfallCents)}");

        return new KpiTile(title,
            noTarget || m.Rate is null ? "—" : (m.Rate.Value * 100).ToString("F1") + "%",
            $"达成 {Money.FormatWan(m.AchievedCents)} / 目标 {Money.FormatWan(m.TargetCents)}",
            gap, st, b, s);
    }

    private void BuildAchievements()
    {
        Achievements.Clear();
        if (_contracts.Count == 0) return;
        long teamSigned = StatsEngine.AchievedRevenue(_contracts, CompletionBasis.Strict);
        foreach (var name in _people.OrderByDescending(n => StatsEngine.AchievedRevenue(Of(n), CompletionBasis.Strict)))
        {
            var mine = Of(name).ToList();
            long sr = StatsEngine.AchievedRevenue(mine, CompletionBasis.Strict);
            Achievements.Add(new AchievementRow
            {
                Name = name,
                Count = mine.Count,
                SignedRevenue = Money.ToWan(sr),
                SignedProfit = Money.ToWan(StatsEngine.AchievedProfit(mine, CompletionBasis.Strict)),
                Weighted = Money.ToWan(StatsEngine.AchievedRevenue(mine, CompletionBasis.Weighted)),
                Payment = Money.ToWan(mine.Sum(x => x.PaymentForecastTotalCents)),
                SharePct = teamSigned == 0 ? 0 : Math.Round((decimal)sr / teamSigned * 100, 1),
            });
        }
        Achievements.Add(new AchievementRow
        {
            Name = "合计",
            Count = _contracts.Count,
            SignedRevenue = Money.ToWan(teamSigned),
            SignedProfit = Money.ToWan(StatsEngine.AchievedProfit(_contracts, CompletionBasis.Strict)),
            Weighted = Money.ToWan(StatsEngine.AchievedRevenue(_contracts, CompletionBasis.Weighted)),
            Payment = Money.ToWan(_contracts.Sum(x => x.PaymentForecastTotalCents)),
            SharePct = 100,
            IsTotal = true,
        });
    }

    private void BuildRanking()
    {
        string label = _basis == CompletionBasis.Strict ? "已签约收入" : "加权收入";
        var people = _people
            .Select(n => (n, val: (double)Money.ToWan(StatsEngine.AchievedRevenue(Of(n), _basis))))
            .OrderBy(p => p.val).ToList();

        RankSeries = new ISeries[]
        {
            new RowSeries<double>
            {
                Name = label,
                Values = people.Select(p => p.val).ToArray(),
                Fill = new SolidColorPaint(Accent),
                MaxBarWidth = 22, Padding = 6,
                DataLabelsPaint = new SolidColorPaint(new SKColor(0x5C, 0x63, 0x6E)),
                DataLabelsSize = 11, DataLabelsPosition = DataLabelsPosition.End,
                DataLabelsFormatter = p => p.Coordinate.PrimaryValue.ToString("N0"),
            }
        };
        RankY = new[] { CategoryAxis(people.Select(p => p.n).ToArray()) };
        RankX = new[] { ValueAxis() };
    }

    private void BuildFunnel()
    {
        var funnel = StatsEngine.BuildFunnel(_contracts);
        var ordered = funnel.Buckets.Reverse().ToList();
        FunnelSeries = new ISeries[]
        {
            new RowSeries<double>
            {
                Name = "预计收入",
                Values = ordered.Select(b => (double)Money.ToWan(b.RevenueCents)).ToArray(),
                Fill = new SolidColorPaint(new SKColor(0x2C, 0x6F, 0xB5)),
                MaxBarWidth = 26, Padding = 6,
                DataLabelsPaint = new SolidColorPaint(new SKColor(0x5C, 0x63, 0x6E)),
                DataLabelsSize = 11, DataLabelsPosition = DataLabelsPosition.End,
                DataLabelsFormatter = p => p.Coordinate.PrimaryValue.ToString("N0"),
            }
        };
        FunnelY = new[] { CategoryAxis(ordered.Select(b => $"{b.StageName}({b.Count})").ToArray()) };
        FunnelX = new[] { ValueAxis() };
    }

    private void BuildMonthly()
    {
        var pts = StatsEngine.BuildMonthly(_contracts);
        MonthlySeries = new ISeries[]
        {
            new ColumnSeries<double>
            {
                Name = "本期到款",
                Values = pts.Select(p => (double)Money.ToWan(p.BucketCents)).ToArray(),
                Fill = new SolidColorPaint(ColSoft), MaxBarWidth = 30,
            },
            new LineSeries<double>
            {
                Name = "累计到款",
                Values = pts.Select(p => (double)Money.ToWan(p.CumulativeCents)).ToArray(),
                Fill = null, Stroke = new SolidColorPaint(Accent) { StrokeThickness = 2 },
                GeometrySize = 7, GeometryFill = new SolidColorPaint(SKColors.White),
                GeometryStroke = new SolidColorPaint(Accent) { StrokeThickness = 2 },
            },
        };
        MonthlyX = new[] { CategoryAxis(pts.Select(p => p.Label).ToArray()) };
        MonthlyY = new[] { ValueAxis() };
    }

    private void BuildStack()
    {
        var stages = Stages.All.ToList();
        StackSeries = stages.Select((meta, i) => (ISeries)new StackedColumnSeries<double>
        {
            Name = meta.ChineseName,
            Values = _people.Select(pn => (double)Money.ToWan(
                Of(pn).Where(c => c.Stage == meta.Stage).Sum(c => c.RevenueEstCents))).ToArray(),
            Fill = new SolidColorPaint(StageColors[i]), MaxBarWidth = 34,
        }).ToArray();
        StackX = new[] { CategoryAxis(_people.ToArray()) };
        StackY = new[] { ValueAxis() };
    }

    private static Axis CategoryAxis(string[] labels) => new()
    {
        Labels = labels,
        LabelsPaint = new SolidColorPaint(AxisText),
        TextSize = 12, SeparatorsPaint = null, TicksPaint = null,
    };

    private static Axis ValueAxis() => new()
    {
        MinLimit = 0, Labeler = v => v.ToString("N0"),
        LabelsPaint = new SolidColorPaint(AxisText),
        TextSize = 11, SeparatorsPaint = new SolidColorPaint(Separator) { StrokeThickness = 1 }, TicksPaint = null,
    };

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Raise([CallerMemberName] string? n = null) => PropertyChanged?.Invoke(this, new(n));
}
