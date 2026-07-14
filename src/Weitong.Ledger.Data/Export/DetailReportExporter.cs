using NPOI.SS.UserModel;
using NPOI.SS.Util;
using NPOI.XSSF.UserModel;
using Weitong.Ledger.Core;
using Weitong.Ledger.Core.Stats;

namespace Weitong.Ledger.Data.Export;

/// <summary>
/// 「达成总览 · 详细报告」导出器。把总览页的全部内容 + 市场策划进展表结构，
/// 从活台账数据自动生成，分 sheet 详细展示。所有统计口径复用 <see cref="StatsEngine"/>，
/// 与界面数字完全一致。金额单位统一为万元（与原市场策划进展表口径一致）。
/// </summary>
public static class DetailReportExporter
{
    /// <summary>一条数据质量提示（与界面异常列表同构，解耦 App 层类型）。</summary>
    public sealed record Anomaly(string Sheet, int Row, string Field, string Value, string Reason);

    private const ContractStage Signed = ContractStage.Signed;
    private const ContractStage Pending = ContractStage.PendingSign;
    private const ContractStage Negotiate = ContractStage.Negotiating;
    private const ContractStage Lead = ContractStage.Lead;
    private const ContractStage Cultivate = ContractStage.Cultivating;

    private static double Wan(long cents) => (double)Money.ToWan(cents);

    public static void Export(
        IEnumerable<Contract> contracts,
        DashboardExporter.TeamTarget target,
        string teamName,
        string sourceNote,
        CompletionBasis currentBasis,
        IEnumerable<Anomaly> anomalies,
        DateTime generatedAt,
        string path)
    {
        var list = contracts.Where(c => !c.IsDeleted).ToList();
        var anoms = anomalies?.ToList() ?? new List<Anomaly>();

        var wb = new XSSFWorkbook();
        var sty = new Styles(wb);

        SheetProgress(wb, sty, list, target, teamName, sourceNote, generatedAt);
        SheetKpi(wb, sty, list, target, currentBasis, generatedAt);
        SheetBySales(wb, sty, list);
        SheetFunnel(wb, sty, list);
        SheetMonthly(wb, sty, list, target);
        SheetSalesByStage(wb, sty, list);
        SheetAnomalies(wb, sty, anoms);

        using var fs = File.Create(path);
        wb.Write(fs);
    }

    // ————————————————————————————————————————————————————————————————
    // Sheet 1 市场策划进展（分领域 + 本组合计，镜像原表列结构）
    // ————————————————————————————————————————————————————————————————
    private static void SheetProgress(
        IWorkbook wb, Styles sty, List<Contract> list,
        DashboardExporter.TeamTarget target, string teamName, string sourceNote, DateTime at)
    {
        var sh = wb.CreateSheet("市场策划进展");
        int width = 22; // 1(领域) + 8(收入) + 8(利润) + 5(成本)

        Title(sh, sty, 0, width, $"{teamName} · 市场策划进展（自动生成）");
        Meta(sh, sty, 1, width, $"单位：万元 · 合计=已签约+待签约+洽谈中 · 差距=合计−指标 · {sourceNote} · 生成于 {at:yyyy-MM-dd HH:mm}");

        // 分组超级表头（行 3）
        int hr0 = 3, hr1 = 4;
        var g0 = sh.CreateRow(hr0);
        SetMerged(sh, g0, sty.GroupHead, hr0, hr1, 0, 0, "市场领域");
        SetMerged(sh, g0, sty.GroupRev, hr0, hr0, 1, 8, "收入（万元）");
        SetMerged(sh, g0, sty.GroupProfit, hr0, hr0, 9, 16, "利润（万元）");
        SetMerged(sh, g0, sty.GroupCost, hr0, hr0, 17, 21, "成本（万元）");

        // 明细列头（行 4）
        var g1 = sh.CreateRow(hr1);
        string[] revCols = { "收入指标", "已签约", "待签约", "洽谈中", "合计", "差距", "项目线索", "培育中" };
        string[] profitCols = { "利润指标", "已签约", "待签约", "洽谈中", "合计", "差距", "项目线索", "培育中" };
        string[] costCols = { "成本指标", "已签约", "待签约", "洽谈中", "合计" };
        int c = 1;
        foreach (var t in revCols) Cell(g1, c++, t, sty.ColHead);
        foreach (var t in profitCols) Cell(g1, c++, t, sty.ColHead);
        foreach (var t in costCols) Cell(g1, c++, t, sty.ColHead);

        int r = hr1 + 1;
        // 各市场领域一行（无分领域指标，故指标/差距留空）
        var groups = list
            .GroupBy(x => string.IsNullOrWhiteSpace(x.MarketField) ? "未分类" : x.MarketField!.Trim())
            .OrderByDescending(gp => gp.Where(x => x.Stage == Signed).Sum(x => x.RevenueEstCents))
            .ToList();
        foreach (var gp in groups)
            WriteProgressRow(sh, sty, r++, gp.Key, gp.ToList(), null, bold: false);

        // 本组合计（带指标 + 差距）
        WriteProgressRow(sh, sty, r++, $"合计（{teamName}）", list, target, bold: true);

        // 列宽
        sh.SetColumnWidth(0, 20 * 256);
        for (int i = 1; i < width; i++) sh.SetColumnWidth(i, 12 * 256);
        sh.CreateFreezePane(1, hr1 + 1);
    }

    private static void WriteProgressRow(
        ISheet sh, Styles sty, int r, string label, List<Contract> cs,
        DashboardExporter.TeamTarget? target, bool bold)
    {
        var f = StatsEngine.BuildFunnel(cs);
        StageBucket B(ContractStage s) => f.Buckets.First(b => b.Stage == s);
        var row = sh.CreateRow(r);

        Cell(row, 0, label, bold ? sty.LabelBold : sty.Label);

        var mMoney = bold ? sty.MoneyBold : sty.Money;
        var mGap = bold ? sty.GapBold : sty.Gap;

        // —— 收入 ——
        long revSigned = B(Signed).RevenueCents, revPend = B(Pending).RevenueCents, revNeg = B(Negotiate).RevenueCents;
        long revSum = revSigned + revPend + revNeg;
        long revLead = B(Lead).RevenueCents, revCult = B(Cultivate).RevenueCents;
        if (target != null) Num(row, 1, Wan(target.RevenueCents), mMoney); else row.CreateCell(1).CellStyle = mMoney;
        Num(row, 2, Wan(revSigned), mMoney);
        Num(row, 3, Wan(revPend), mMoney);
        Num(row, 4, Wan(revNeg), mMoney);
        Num(row, 5, Wan(revSum), bold ? sty.MoneyBold : sty.MoneyEmph);
        if (target != null) Num(row, 6, Wan(revSum - target.RevenueCents), mGap); else row.CreateCell(6).CellStyle = mGap;
        Num(row, 7, Wan(revLead), mMoney);
        Num(row, 8, Wan(revCult), mMoney);

        // —— 利润 ——
        long pfSigned = B(Signed).ProfitCents, pfPend = B(Pending).ProfitCents, pfNeg = B(Negotiate).ProfitCents;
        long pfSum = pfSigned + pfPend + pfNeg;
        long pfLead = B(Lead).ProfitCents, pfCult = B(Cultivate).ProfitCents;
        if (target != null) Num(row, 9, Wan(target.ProfitCents), mMoney); else row.CreateCell(9).CellStyle = mMoney;
        Num(row, 10, Wan(pfSigned), mMoney);
        Num(row, 11, Wan(pfPend), mMoney);
        Num(row, 12, Wan(pfNeg), mMoney);
        Num(row, 13, Wan(pfSum), bold ? sty.MoneyBold : sty.MoneyEmph);
        if (target != null) Num(row, 14, Wan(pfSum - target.ProfitCents), mGap); else row.CreateCell(14).CellStyle = mGap;
        Num(row, 15, Wan(pfLead), mMoney);
        Num(row, 16, Wan(pfCult), mMoney);

        // —— 成本 ——
        long ctSigned = B(Signed).CostCents, ctPend = B(Pending).CostCents, ctNeg = B(Negotiate).CostCents;
        long ctSum = ctSigned + ctPend + ctNeg;
        if (target != null) Num(row, 17, Wan(target.CostCeilingCents), mMoney); else row.CreateCell(17).CellStyle = mMoney;
        Num(row, 18, Wan(ctSigned), mMoney);
        Num(row, 19, Wan(ctPend), mMoney);
        Num(row, 20, Wan(ctNeg), mMoney);
        Num(row, 21, Wan(ctSum), bold ? sty.MoneyBold : sty.MoneyEmph);
    }

    // ————————————————————————————————————————————————————————————————
    // Sheet 2 达成总览 KPI（两口径并列）
    // ————————————————————————————————————————————————————————————————
    private static void SheetKpi(
        IWorkbook wb, Styles sty, List<Contract> list,
        DashboardExporter.TeamTarget target, CompletionBasis current, DateTime at)
    {
        var sh = wb.CreateSheet("达成总览KPI");
        int width = 6;
        Title(sh, sty, 0, width, "目标达成总览（KPI）");
        Meta(sh, sty, 1, width, $"单位：万元 · 严口径=仅已签约 · 预测口径=各层级按成交概率加权 · 界面当前口径：{(current == CompletionBasis.Strict ? "只算已签约" : "含在谈预测")} · 生成于 {at:yyyy-MM-dd HH:mm}");

        var hr = sh.CreateRow(3);
        string[] cols = { "指标", "口径", "达成", "目标 / 上限", "完成率", "差距说明" };
        for (int i = 0; i < cols.Length; i++) Cell(hr, i, cols[i], sty.ColHead);

        int r = 4;
        foreach (var basis in new[] { CompletionBasis.Strict, CompletionBasis.Weighted })
        {
            string bName = basis == CompletionBasis.Strict ? "严口径" : "预测口径";
            var comp = StatsEngine.BuildCompletions(list, new Target
            {
                RevenueTargetCents = target.RevenueCents,
                ProfitTargetCents = target.ProfitCents,
                CostCeilingCents = target.CostCeilingCents,
            }, basis);
            KpiRow(sh, sty, r++, "收入达成", bName, comp[0]);
            KpiRow(sh, sty, r++, "利润达成", bName, comp[1]);
            KpiRow(sh, sty, r++, "成本控制", bName, comp[2]);
        }

        // 累计到款进度（口径无关）
        long rec = list.Sum(x => x.ReceivedToDateCents);
        double? recRate = target.RevenueCents != 0 ? (double)rec / target.RevenueCents : null;
        var row = sh.CreateRow(r++);
        Cell(row, 0, "累计到款进度", sty.Label);
        Cell(row, 1, "实收", sty.Label);
        Num(row, 2, Wan(rec), sty.Money);
        Num(row, 3, Wan(target.RevenueCents), sty.Money);
        if (recRate.HasValue) Num(row, 4, recRate.Value, sty.Pct); else Cell(row, 4, "—", sty.Note);
        Cell(row, 5, $"距收入指标还需回款 {Money.ToWan(Math.Max(0, target.RevenueCents - rec)):N2} 万", sty.Note);

        for (int i = 0; i < width; i++) sh.SetColumnWidth(i, (i == 5 ? 34 : i <= 1 ? 12 : 14) * 256);
        sh.CreateFreezePane(0, 4);
    }

    private static void KpiRow(ISheet sh, Styles sty, int r, string title, string basis, CompletionMetric m)
    {
        var row = sh.CreateRow(r);
        Cell(row, 0, title, sty.Label);
        Cell(row, 1, basis, sty.Label);
        Num(row, 2, Wan(m.AchievedCents), sty.Money);
        Num(row, 3, Wan(m.TargetCents), sty.Money);
        if (m.TargetCents != 0 && m.Rate.HasValue) Num(row, 4, m.Rate.Value, sty.Pct); else Cell(row, 4, "—", sty.Note);

        string note;
        if (m.TargetCents == 0) note = "未设目标";
        else if (m.Direction == MetricDirection.LowerIsBetter)
            note = m.OnTrack
                ? $"预算内结余 {Money.ToWan(m.TargetCents - m.AchievedCents):N2} 万"
                : $"超支 {Money.ToWan(m.ShortfallCents):N2} 万";
        else
            note = m.AchievedCents >= m.TargetCents
                ? $"超目标 {Money.ToWan(m.AchievedCents - m.TargetCents):N2} 万"
                : $"距目标还差 {Money.ToWan(m.ShortfallCents):N2} 万";
        Cell(row, 5, note, sty.Note);
    }

    // ————————————————————————————————————————————————————————————————
    // Sheet 3 目标达成明细（按销售）
    // ————————————————————————————————————————————————————————————————
    private static void SheetBySales(IWorkbook wb, Styles sty, List<Contract> list)
    {
        var sh = wb.CreateSheet("按销售明细");
        int width = 7;
        Title(sh, sty, 0, width, "目标达成明细（按销售）");
        Meta(sh, sty, 1, width, "单位：万元 · 收入/利润为已签约口径 · 加权收入为预测口径 · 占比=已签约收入占全组");

        var hr = sh.CreateRow(3);
        string[] cols = { "销售人员", "合同数", "已签约收入", "已签约利润", "加权收入(预测)", "本年预计到款", "占全组收入" };
        for (int i = 0; i < cols.Length; i++) Cell(hr, i, cols[i], sty.ColHead);

        long teamSigned = StatsEngine.AchievedRevenue(list, CompletionBasis.Strict);
        var people = list.Select(x => x.SalesPersonName).Distinct()
            .OrderByDescending(n => StatsEngine.AchievedRevenue(list.Where(x => x.SalesPersonName == n), CompletionBasis.Strict))
            .ToList();

        int r = 4;
        foreach (var name in people)
        {
            var mine = list.Where(x => x.SalesPersonName == name).ToList();
            long sr = StatsEngine.AchievedRevenue(mine, CompletionBasis.Strict);
            var row = sh.CreateRow(r++);
            Cell(row, 0, name, sty.Label);
            Num(row, 1, mine.Count, sty.Int);
            Num(row, 2, Wan(sr), sty.Money);
            Num(row, 3, Wan(StatsEngine.AchievedProfit(mine, CompletionBasis.Strict)), sty.Money);
            Num(row, 4, Wan(StatsEngine.AchievedRevenue(mine, CompletionBasis.Weighted)), sty.Money);
            Num(row, 5, Wan(mine.Sum(x => x.PaymentForecastTotalCents)), sty.Money);
            Num(row, 6, teamSigned == 0 ? 0 : (double)sr / teamSigned, sty.Pct);
        }

        var tr = sh.CreateRow(r);
        Cell(tr, 0, "合计", sty.LabelBold);
        Num(tr, 1, list.Count, sty.IntBold);
        Num(tr, 2, Wan(teamSigned), sty.MoneyBold);
        Num(tr, 3, Wan(StatsEngine.AchievedProfit(list, CompletionBasis.Strict)), sty.MoneyBold);
        Num(tr, 4, Wan(StatsEngine.AchievedRevenue(list, CompletionBasis.Weighted)), sty.MoneyBold);
        Num(tr, 5, Wan(list.Sum(x => x.PaymentForecastTotalCents)), sty.MoneyBold);
        Num(tr, 6, 1.0, sty.PctBold);

        sh.SetColumnWidth(0, 16 * 256);
        for (int i = 1; i < width; i++) sh.SetColumnWidth(i, 15 * 256);
        sh.CreateFreezePane(1, 4);
    }

    // ————————————————————————————————————————————————————————————————
    // Sheet 4 合同层级分布（漏斗）
    // ————————————————————————————————————————————————————————————————
    private static void SheetFunnel(IWorkbook wb, Styles sty, List<Contract> list)
    {
        var sh = wb.CreateSheet("合同层级分布");
        int width = 8;
        Title(sh, sty, 0, width, "合同层级分布（漏斗）");
        Meta(sh, sty, 1, width, "单位：万元 · 加权=金额×成交概率（已签约100% 待签约80% 洽谈中50% 项目线索20% 培育中5%）");

        var hr = sh.CreateRow(3);
        string[] cols = { "合同层级", "笔数", "合同金额", "预计收入", "预计成本", "预计利润", "加权收入", "加权利润" };
        for (int i = 0; i < cols.Length; i++) Cell(hr, i, cols[i], sty.ColHead);

        var f = StatsEngine.BuildFunnel(list);
        int r = 4;
        foreach (var b in f.Buckets)
        {
            var row = sh.CreateRow(r++);
            Cell(row, 0, b.StageName, sty.Label);
            Num(row, 1, b.Count, sty.Int);
            Num(row, 2, Wan(b.ContractAmountCents), sty.Money);
            Num(row, 3, Wan(b.RevenueCents), sty.Money);
            Num(row, 4, Wan(b.CostCents), sty.Money);
            Num(row, 5, Wan(b.ProfitCents), sty.Money);
            Num(row, 6, Wan(b.WeightedRevenueCents), sty.Money);
            Num(row, 7, Wan(b.WeightedProfitCents), sty.Money);
        }

        var tr = sh.CreateRow(r);
        Cell(tr, 0, "合计", sty.LabelBold);
        Num(tr, 1, f.Buckets.Sum(b => b.Count), sty.IntBold);
        Num(tr, 2, Wan(f.Buckets.Sum(b => b.ContractAmountCents)), sty.MoneyBold);
        Num(tr, 3, Wan(f.Buckets.Sum(b => b.RevenueCents)), sty.MoneyBold);
        Num(tr, 4, Wan(f.Buckets.Sum(b => b.CostCents)), sty.MoneyBold);
        Num(tr, 5, Wan(f.Buckets.Sum(b => b.ProfitCents)), sty.MoneyBold);
        Num(tr, 6, Wan(f.Buckets.Sum(b => b.WeightedRevenueCents)), sty.MoneyBold);
        Num(tr, 7, Wan(f.Buckets.Sum(b => b.WeightedProfitCents)), sty.MoneyBold);

        sh.SetColumnWidth(0, 14 * 256);
        for (int i = 1; i < width; i++) sh.SetColumnWidth(i, 14 * 256);
        sh.CreateFreezePane(1, 4);
    }

    // ————————————————————————————————————————————————————————————————
    // Sheet 5 月度到款（1–12 月）
    // ————————————————————————————————————————————————————————————————
    private static void SheetMonthly(IWorkbook wb, Styles sty, List<Contract> list, DashboardExporter.TeamTarget target)
    {
        var sh = wb.CreateSheet("月度到款");
        int width = 5;
        Title(sh, sty, 0, width, "1–12 月预计到款");
        Meta(sh, sty, 1, width, "单位：万元 · 匀速应达=收入指标×月份/12 · 差额=累计−匀速应达");

        var hr = sh.CreateRow(3);
        string[] cols = { "月份", "本期到款", "累计到款", "匀速应达", "与匀速差额" };
        for (int i = 0; i < cols.Length; i++) Cell(hr, i, cols[i], sty.ColHead);

        var pts = StatsEngine.BuildMonthly(list, PaymentKind.Forecast, target.RevenueCents);
        int r = 4;
        foreach (var p in pts)
        {
            var row = sh.CreateRow(r++);
            Cell(row, 0, p.Label, sty.Label);
            Num(row, 1, Wan(p.BucketCents), sty.Money);
            Num(row, 2, Wan(p.CumulativeCents), sty.Money);
            Num(row, 3, Wan(p.PacingTargetCents), sty.Money);
            Num(row, 4, Wan(p.CumulativeCents - p.PacingTargetCents), sty.Gap);
        }

        sh.SetColumnWidth(0, 12 * 256);
        for (int i = 1; i < width; i++) sh.SetColumnWidth(i, 15 * 256);
        sh.CreateFreezePane(1, 4);
    }

    // ————————————————————————————————————————————————————————————————
    // Sheet 6 销售 × 层级（预计收入矩阵）
    // ————————————————————————————————————————————————————————————————
    private static void SheetSalesByStage(IWorkbook wb, Styles sty, List<Contract> list)
    {
        var sh = wb.CreateSheet("销售×层级");
        var stages = Stages.All.ToList();
        int width = 1 + stages.Count + 1; // 销售 + 各层级 + 行合计
        Title(sh, sty, 0, width, "销售 × 合同层级（预计收入）");
        Meta(sh, sty, 1, width, "单位：万元 · 值=各销售在该层级的本年预计属期收入之和");

        var hr = sh.CreateRow(3);
        Cell(hr, 0, "销售人员", sty.ColHead);
        for (int i = 0; i < stages.Count; i++) Cell(hr, i + 1, stages[i].ChineseName, sty.ColHead);
        Cell(hr, width - 1, "行合计", sty.ColHead);

        var people = list.Select(x => x.SalesPersonName).Distinct()
            .OrderByDescending(n => list.Where(x => x.SalesPersonName == n).Sum(x => x.RevenueEstCents))
            .ToList();

        var colTotals = new long[stages.Count];
        int r = 4;
        foreach (var name in people)
        {
            var mine = list.Where(x => x.SalesPersonName == name).ToList();
            var row = sh.CreateRow(r++);
            Cell(row, 0, name, sty.Label);
            long rowTotal = 0;
            for (int i = 0; i < stages.Count; i++)
            {
                long v = mine.Where(x => x.Stage == stages[i].Stage).Sum(x => x.RevenueEstCents);
                colTotals[i] += v; rowTotal += v;
                Num(row, i + 1, Wan(v), sty.Money);
            }
            Num(row, width - 1, Wan(rowTotal), sty.MoneyEmph);
        }

        var tr = sh.CreateRow(r);
        Cell(tr, 0, "列合计", sty.LabelBold);
        long grand = 0;
        for (int i = 0; i < stages.Count; i++) { Num(tr, i + 1, Wan(colTotals[i]), sty.MoneyBold); grand += colTotals[i]; }
        Num(tr, width - 1, Wan(grand), sty.MoneyBold);

        sh.SetColumnWidth(0, 16 * 256);
        for (int i = 1; i < width; i++) sh.SetColumnWidth(i, 13 * 256);
        sh.CreateFreezePane(1, 4);
    }

    // ————————————————————————————————————————————————————————————————
    // Sheet 7 数据质量提示
    // ————————————————————————————————————————————————————————————————
    private static void SheetAnomalies(IWorkbook wb, Styles sty, List<Anomaly> anoms)
    {
        var sh = wb.CreateSheet("数据质量提示");
        int width = 5;
        Title(sh, sty, 0, width, "数据质量提示");
        Meta(sh, sty, 1, width, "软件自动捕获的需人工核对项（如 预计到款≠月度之和、金额异常），Excel 中不易发现。");

        var hr = sh.CreateRow(3);
        string[] cols = { "工作表", "行", "字段", "值", "提示" };
        for (int i = 0; i < cols.Length; i++) Cell(hr, i, cols[i], sty.ColHead);

        int r = 4;
        if (anoms.Count == 0)
        {
            var row = sh.CreateRow(r);
            SetMerged(sh, row, sty.Note, r, r, 0, width - 1, "未发现数据质量问题。");
        }
        else
        {
            foreach (var a in anoms)
            {
                var row = sh.CreateRow(r++);
                Cell(row, 0, a.Sheet, sty.Label);
                Num(row, 1, a.Row, sty.Int);
                Cell(row, 2, a.Field, sty.Label);
                Cell(row, 3, a.Value, sty.Note);
                Cell(row, 4, a.Reason, sty.Note);
            }
        }

        sh.SetColumnWidth(0, 20 * 256);
        sh.SetColumnWidth(1, 6 * 256);
        sh.SetColumnWidth(2, 16 * 256);
        sh.SetColumnWidth(3, 18 * 256);
        sh.SetColumnWidth(4, 46 * 256);
        sh.CreateFreezePane(0, 4);
    }

    // ————————————————————————————————————————————————————————————————
    // 低层写入 & 样式
    // ————————————————————————————————————————————————————————————————
    private static void Cell(IRow row, int col, string? text, ICellStyle style)
    {
        var cell = row.CreateCell(col);
        cell.SetCellValue(text ?? "");
        cell.CellStyle = style;
    }

    private static void Num(IRow row, int col, double value, ICellStyle style)
    {
        var cell = row.CreateCell(col);
        cell.SetCellValue(value);
        cell.CellStyle = style;
    }

    private static void Title(ISheet sh, Styles sty, int r, int width, string text)
    {
        var row = sh.CreateRow(r);
        row.HeightInPoints = 22;
        SetMerged(sh, row, sty.TitleStyle, r, r, 0, width - 1, text);
    }

    private static void Meta(ISheet sh, Styles sty, int r, int width, string text)
    {
        var row = sh.CreateRow(r);
        SetMerged(sh, row, sty.MetaStyle, r, r, 0, width - 1, text);
    }

    /// <summary>合并区域并给左上角单元格赋值/样式，其余单元格也套样式以显边框。</summary>
    private static void SetMerged(ISheet sh, IRow firstRow, ICellStyle style,
        int r0, int r1, int c0, int c1, string text)
    {
        for (int r = r0; r <= r1; r++)
        {
            var row = r == firstRow.RowNum ? firstRow : (sh.GetRow(r) ?? sh.CreateRow(r));
            for (int cc = c0; cc <= c1; cc++)
            {
                var cell = row.GetCell(cc) ?? row.CreateCell(cc);
                cell.CellStyle = style;
                if (r == r0 && cc == c0) cell.SetCellValue(text);
            }
        }
        if (r0 != r1 || c0 != c1)
            sh.AddMergedRegion(new CellRangeAddress(r0, r1, c0, c1));
    }

    /// <summary>集中管理所有单元格样式，一次创建、多处复用。</summary>
    private sealed class Styles
    {
        public readonly ICellStyle TitleStyle, MetaStyle;
        public readonly ICellStyle GroupHead, GroupRev, GroupProfit, GroupCost, ColHead;
        public readonly ICellStyle Label, LabelBold, Note;
        public readonly ICellStyle Money, MoneyBold, MoneyEmph, Gap, GapBold;
        public readonly ICellStyle Int, IntBold, Pct, PctBold;

        public Styles(IWorkbook wb)
        {
            var fmt = wb.CreateDataFormat();
            short moneyFmt = fmt.GetFormat("#,##0.00");
            short gapFmt = fmt.GetFormat("#,##0.00;[Red]\\-#,##0.00");
            short intFmt = fmt.GetFormat("#,##0");
            short pctFmt = fmt.GetFormat("0.0%");

            static XSSFColor XColor(byte[] rgb) => new(rgb, null);

            IFont Font(double size, bool bold, byte[]? rgb = null)
            {
                var f = wb.CreateFont();
                f.FontHeightInPoints = size; f.IsBold = bold; f.FontName = "微软雅黑";
                if (rgb != null && f is XSSFFont xf) xf.SetColor(XColor(rgb));
                return f;
            }

            var accent = new byte[] { 0x1B, 0x5F, 0xA8 };
            var revFill = new byte[] { 0x21, 0x6B, 0xB5 };
            var profitFill = new byte[] { 0x1E, 0x7A, 0x46 };
            var costFill = new byte[] { 0x9A, 0x5B, 0x1E };
            var soft = new byte[] { 0xEE, 0xF3, 0xF9 };
            var white = new byte[] { 0xFF, 0xFF, 0xFF };

            ICellStyle Base(bool border)
            {
                var s = wb.CreateCellStyle();
                s.VerticalAlignment = VerticalAlignment.Center;
                if (border)
                {
                    s.BorderBottom = s.BorderTop = s.BorderLeft = s.BorderRight = BorderStyle.Thin;
                    var line = IndexedColors.Grey25Percent.Index;
                    s.BottomBorderColor = s.TopBorderColor = s.LeftBorderColor = s.RightBorderColor = line;
                }
                return s;
            }

            void Fill(ICellStyle s, byte[] rgb)
            {
                if (s is XSSFCellStyle xs) xs.SetFillForegroundColor(XColor(rgb));
                s.FillPattern = FillPattern.SolidForeground;
            }

            TitleStyle = Base(false);
            TitleStyle.SetFont(Font(14, true, accent));
            TitleStyle.Alignment = HorizontalAlignment.Left;

            MetaStyle = Base(false);
            MetaStyle.SetFont(Font(9, false, new byte[] { 0x80, 0x86, 0x90 }));
            MetaStyle.Alignment = HorizontalAlignment.Left;
            MetaStyle.WrapText = true;

            ICellStyle Head(byte[] fill)
            {
                var s = Base(true);
                s.SetFont(Font(10.5, true, white));
                s.Alignment = HorizontalAlignment.Center;
                s.WrapText = true;
                Fill(s, fill);
                return s;
            }
            GroupHead = Head(accent);
            GroupRev = Head(revFill);
            GroupProfit = Head(profitFill);
            GroupCost = Head(costFill);
            ColHead = Head(accent);

            Label = Base(true);
            Label.SetFont(Font(10, false));
            Label.Alignment = HorizontalAlignment.Left;

            LabelBold = Base(true);
            LabelBold.SetFont(Font(10, true));
            LabelBold.Alignment = HorizontalAlignment.Left;
            Fill(LabelBold, soft);

            Note = Base(true);
            Note.SetFont(Font(9.5, false, new byte[] { 0x55, 0x5B, 0x66 }));
            Note.Alignment = HorizontalAlignment.Left;
            Note.WrapText = true;

            ICellStyle NumStyle(short f, bool bold, bool emph = false)
            {
                var s = Base(true);
                s.SetFont(Font(10, bold));
                s.Alignment = HorizontalAlignment.Right;
                s.DataFormat = f;
                if (bold) Fill(s, soft);
                else if (emph) Fill(s, new byte[] { 0xF4, 0xF7, 0xFB });
                return s;
            }
            Money = NumStyle(moneyFmt, false);
            MoneyEmph = NumStyle(moneyFmt, false, emph: true);
            MoneyBold = NumStyle(moneyFmt, true);
            Gap = NumStyle(gapFmt, false);
            GapBold = NumStyle(gapFmt, true);
            Int = NumStyle(intFmt, false);
            IntBold = NumStyle(intFmt, true);
            Pct = NumStyle(pctFmt, false);
            PctBold = NumStyle(pctFmt, true);
        }
    }
}
