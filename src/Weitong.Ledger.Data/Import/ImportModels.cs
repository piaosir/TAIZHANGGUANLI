namespace Weitong.Ledger.Data.Import;

/// <summary>导入落库策略：命中已有记录时是覆盖，还是一律作为新记录追加。</summary>
public enum ImportMode
{
    /// <summary>按「工作表#行号」匹配：命中则覆盖更新、未命中则新增（幂等重导入，同一份台账反复导入不产生重复，
    /// 但会覆盖软件里对这些行做过的改动，也会因源表行位置变动而误覆盖）。</summary>
    Overwrite,

    /// <summary>全部作为新记录追加：每行赋予全新唯一键，绝不覆盖库中任何已有数据（适合导入另一批新合同）。</summary>
    AppendNew,
}

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
