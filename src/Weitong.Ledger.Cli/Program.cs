using System.Text;
using Weitong.Ledger.Core;
using Weitong.Ledger.Core.Stats;
using Weitong.Ledger.Data.Import;

Console.OutputEncoding = Encoding.UTF8;

// 定位现有台账 xlsx
string root = FindProjectRoot();
string? xlsx = Directory.EnumerateFiles(root, "*.xlsx", SearchOption.TopDirectoryOnly)
    .FirstOrDefault(f => !Path.GetFileName(f).StartsWith("~$"));
if (xlsx == null) { Console.WriteLine("未找到 xlsx 台账文件于: " + root); return; }

Console.WriteLine("════════════════════════════════════════════════════════════");
Console.WriteLine(" 合同台账 · 导入与口径验证 (M0)");
Console.WriteLine("════════════════════════════════════════════════════════════");
Console.WriteLine($"源文件: {Path.GetFileName(xlsx)}\n");

var importer = new ExcelImporter();
var result = importer.ImportFile(xlsx, DateTime.UtcNow, "verify-cli");

// —— 导入概况 ——
Console.WriteLine($"扫描数据行: {result.RowsScanned}    成功入库: {result.RowsImported}    异常标记: {result.Anomalies.Count}");
Console.WriteLine($"合计预计收入: {Money.FormatWan(result.TotalRevenueCents)}    合计预计利润: {Money.FormatWan(result.TotalProfitCents)}\n");

Console.WriteLine("按销售人员:");
foreach (var (name, n) in result.BySalesperson.OrderByDescending(kv => kv.Value))
    Console.WriteLine($"  {name,-10} {n,4} 条");

Console.WriteLine("\n按合同层级(漏斗):");
foreach (var (stage, n) in result.ByStage.OrderByDescending(kv => kv.Value))
    Console.WriteLine($"  {stage,-8} {n,4} 条");

// —— 全量漏斗 ——
var funnel = StatsEngine.BuildFunnel(result.Contracts);
Console.WriteLine("\n────────── 全量销售漏斗（金额=预计收入 / 利润）──────────");
Console.WriteLine($"{"层级",-8}{"数量",6}{"预计收入",18}{"预计利润",18}{"加权收入(预测口径)",22}");
foreach (var b in funnel.Buckets)
    Console.WriteLine($"{b.StageName,-8}{b.Count,6}{Money.FormatWan(b.RevenueCents),18}{Money.FormatWan(b.ProfitCents),18}{Money.FormatWan(b.WeightedRevenueCents),22}");
Console.WriteLine($"{"合计",-8}{funnel.TotalCount,6}{Money.FormatWan(funnel.TotalRevenueCents),18}{Money.FormatWan(funnel.TotalProfitCents),18}");

// —— 口径对比：严 vs 预测 ——
Console.WriteLine("\n────────── 完成率口径对比（无目标时仅示达成额）──────────");
long strictRev = StatsEngine.AchievedRevenue(result.Contracts, CompletionBasis.Strict);
long weightRev = StatsEngine.AchievedRevenue(result.Contracts, CompletionBasis.Weighted);
long strictProfit = StatsEngine.AchievedProfit(result.Contracts, CompletionBasis.Strict);
long weightProfit = StatsEngine.AchievedProfit(result.Contracts, CompletionBasis.Weighted);
Console.WriteLine($"收入  严口径(仅已签约): {Money.FormatWan(strictRev),16}   预测口径(加权): {Money.FormatWan(weightRev),16}");
Console.WriteLine($"利润  严口径(仅已签约): {Money.FormatWan(strictProfit),16}   预测口径(加权): {Money.FormatWan(weightProfit),16}");

// —— 每个销售的个人页数据（示例：给假想目标看完成率/差距）——
Console.WriteLine("\n────────── 各销售达成（示例目标=其已签约收入的1.2倍，验证完成率/差距计算）──────────");
Console.WriteLine($"{"销售",-10}{"已签约收入",16}{"示例收入指标",16}{"完成率",10}{"距目标还差",16}");
foreach (var name in result.BySalesperson.Keys.OrderBy(x => x))
{
    var mine = result.Contracts.Where(c => c.SalesPersonName == name).ToList();
    long signedRev = StatsEngine.AchievedRevenue(mine, CompletionBasis.Strict);
    long demoTarget = (long)(signedRev * 1.2); // 仅演示
    var target = new Target { RevenueTargetCents = demoTarget, ProfitTargetCents = 0, CostCeilingCents = 0 };
    var m = StatsEngine.BuildCompletions(mine, target, CompletionBasis.Strict)[0];
    string rate = m.Rate.HasValue ? (m.Rate.Value * 100).ToString("F1") + "%" : "—";
    Console.WriteLine($"{name,-10}{Money.FormatWan(signedRev),16}{Money.FormatWan(demoTarget),16}{rate,10}{Money.FormatWan(m.ShortfallCents),16}");
}

// —— 月度到款曲线（全量，预计口径）——
Console.WriteLine("\n────────── 全量 1-12月 预计到款（累计曲线）──────────");
var monthly = StatsEngine.BuildMonthly(result.Contracts);
Console.WriteLine($"{"时点",-8}{"本期到款",16}{"累计到款",16}");
foreach (var p in monthly)
    Console.WriteLine($"{p.Label,-8}{Money.FormatWan(p.BucketCents),16}{Money.FormatWan(p.CumulativeCents),16}");

// —— 异常样例 ——
if (result.Anomalies.Count > 0)
{
    Console.WriteLine($"\n────────── 异常行样例（共 {result.Anomalies.Count} 条，显示前 15）──────────");
    foreach (var a in result.Anomalies.Take(15))
        Console.WriteLine($"  [{a.Sheet} 第{a.RowNumber}行] {a.Field} = \"{a.RawValue}\" → {a.Reason}");
}

// —— M0 地基：加密本地库 落库/回读/加密性 验证 ——
Console.WriteLine("\n────────── M0 地基：加密 SQLite 持久化验证 ──────────");
string dbDir = Path.Combine(Path.GetTempPath(), "weitong-ledger-clitest");
if (Directory.Exists(dbDir)) Directory.Delete(dbDir, true);
var store = new Weitong.Ledger.Data.Db.LedgerStore(dbDir);
Console.WriteLine($"加密库已创建: {store.DbPath}");

var seed = store.SeedFromImport(result, "verify-cli");
Console.WriteLine($"灌库: 新增 {seed.Added} 条, 跳过 {seed.Skipped} 条, 库内合计 {seed.TotalInDb} 条");

var seed2 = store.SeedFromImport(result, "verify-cli"); // 幂等性
Console.WriteLine($"再次灌库(幂等): 新增 {seed2.Added} 条 (应为0), 跳过 {seed2.Skipped} 条");

var back = store.GetAllContracts();
long backRev = back.Sum(c => c.RevenueEstCents);
Console.WriteLine($"回读: {back.Count} 条, 合计预计收入 {Money.FormatWan(backRev)} (应与导入一致 {Money.FormatWan(result.TotalRevenueCents)})");
var parkContracts = store.GetContractsFor("朴东旭");
Console.WriteLine($"按销售过滤(朴东旭): {parkContracts.Count} 条 — 个人页/权限分区就绪");

// 加密性：明文 SQLite 头部是 "SQLite format 3"，加密库则不是
var header = new byte[16];
using (var fs = File.OpenRead(store.DbPath)) { _ = fs.Read(header, 0, 16); }
string headerText = System.Text.Encoding.ASCII.GetString(header).Replace("\0", "");
bool encrypted = !headerText.StartsWith("SQLite format");
Console.WriteLine($"库文件头: \"{headerText}\" → {(encrypted ? "✔ 已加密(非明文SQLite)" : "✗ 未加密!")}");

// —— 导出标准 Excel 测试 ——
string expPath = Path.Combine(Path.GetTempPath(), "台账导出测试.xlsx");
Weitong.Ledger.Data.Export.LedgerExcelExporter.Export(result.Contracts, expPath);
Console.WriteLine($"\n导出测试: {result.Contracts.Count} 条 → {expPath}");

Console.WriteLine("\n✔ M0 验证完成（导入口径 + 加密持久化）。");

static string FindProjectRoot()
{
    var dir = new DirectoryInfo(AppContext.BaseDirectory);
    while (dir != null)
    {
        if (dir.EnumerateFiles("*.xlsx").Any(f => !f.Name.StartsWith("~$"))) return dir.FullName;
        if (Directory.Exists(Path.Combine(dir.FullName, "src"))) return dir.FullName;
        dir = dir.Parent;
    }
    return Directory.GetCurrentDirectory();
}
