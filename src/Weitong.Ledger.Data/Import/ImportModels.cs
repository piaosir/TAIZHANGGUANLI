namespace Weitong.Ledger.Data.Import;

/// <summary>导入过程中发现的问题行（不阻断其余导入，进"待修正"区）。</summary>
public sealed record ImportAnomaly(
    string Sheet,
    int RowNumber,
    string Field,
    string RawValue,
    string Reason);

/// <summary>一次导入的结果汇总。</summary>
public sealed class ImportResult
{
    public List<Core.Contract> Contracts { get; } = new();
    public List<ImportAnomaly> Anomalies { get; } = new();
    public int RowsScanned { get; set; }
    public int RowsImported => Contracts.Count;
    public Dictionary<string, int> BySalesperson { get; } = new();
    public Dictionary<string, int> ByStage { get; } = new();

    public long TotalRevenueCents => Contracts.Sum(c => c.RevenueEstCents);
    public long TotalProfitCents => Contracts.Sum(c => c.ProfitEstCents);
}
