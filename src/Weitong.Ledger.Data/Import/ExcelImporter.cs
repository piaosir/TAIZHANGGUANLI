using NPOI.SS.UserModel;
using NPOI.XSSF.UserModel;
using Weitong.Ledger.Core;

namespace Weitong.Ledger.Data.Import;

/// <summary>
/// 把现有的合同台账 xlsx 导入为领域对象。
/// 按固定列序解析（表2/表3 表头一致）；脏数据交给 CellReader 兜底，无法解析的记 Anomaly。
/// </summary>
public sealed class ExcelImporter
{
    // 0 基列索引（与真实台账表头对应）
    private const int C_Serial = 0, C_Stage = 1, C_ContractNo = 2, C_SignDate = 3, C_ExpSignDate = 4,
        C_ProjAttr = 5, C_ProjName = 6, C_PartyA = 7, C_PartyB = 8, C_Sales = 9, C_MarketField = 10,
        C_Segment = 11, C_Product = 12, C_Currency = 13, C_Amount = 14, C_ValidFrom = 15, C_ValidTo = 16,
        C_ServiceMonths = 17, C_Revenue = 18, C_Cost = 19, C_Profit = 20, C_PaymentTotal = 21,
        C_Pay1to4 = 22, C_Pay5 = 23, C_Pay6 = 24, C_Pay7 = 25, C_Pay8 = 26, C_Pay9 = 27,
        C_Pay10 = 28, C_Pay11 = 29, C_Pay12 = 30, C_ReceivedToDate = 31, C_CultSource = 32;

    /// <summary>识别为"合同台账"明细的工作表名关键字。</summary>
    private static bool IsLedgerSheet(string name) =>
        name.Contains("合同台账") || name.Contains("台账");

    public ImportResult ImportFile(string path, DateTime importTimeUtc, string importedBy, IReadOnlyCollection<string>? sheets = null)
    {
        using var fs = File.OpenRead(path);
        return Import(fs, importTimeUtc, importedBy, sheets);
    }

    /// <param name="sheets">null=导入全部台账分表；否则只导入名字在此集合里的分表（按团队只导本团队那部分）。</param>
    public ImportResult Import(Stream stream, DateTime importTimeUtc, string importedBy, IReadOnlyCollection<string>? sheets = null)
    {
        var result = new ImportResult();
        IWorkbook wb = new XSSFWorkbook(stream);

        for (int si = 0; si < wb.NumberOfSheets; si++)
        {
            var sheet = wb.GetSheetAt(si);
            if (!IsLedgerSheet(sheet.SheetName)) continue;
            if (sheets != null && !sheets.Contains(sheet.SheetName)) continue;   // 只导入选中的分表
            ImportSheet(sheet, result, importTimeUtc, importedBy);
        }
        return result;
    }

    /// <summary>列出文件里可导入的台账分表名（供导入时按团队选择只导本团队那部分）。</summary>
    public List<string> ListLedgerSheets(string path)
    {
        using var fs = File.OpenRead(path);
        IWorkbook wb = new XSSFWorkbook(fs);
        var names = new List<string>();
        for (int i = 0; i < wb.NumberOfSheets; i++)
        {
            var n = wb.GetSheetAt(i).SheetName;
            if (IsLedgerSheet(n)) names.Add(n);
        }
        return names;
    }

    private void ImportSheet(ISheet sheet, ImportResult result, DateTime now, string by)
    {
        string sheetName = sheet.SheetName;
        int lastRow = sheet.LastRowNum;

        for (int r = 1; r <= lastRow; r++) // 第 0 行是表头
        {
            var row = sheet.GetRow(r);
            if (row == null) continue;

            // 序号为空 且 甲方/项目名都空 → 视为空行/说明行，跳过
            var serial = CellReader.Str(row.GetCell(C_Serial));
            var partyA = CellReader.Str(row.GetCell(C_PartyA));
            var projName = CellReader.Str(row.GetCell(C_ProjName));
            var salesName = CellReader.Str(row.GetCell(C_Sales));
            if (serial == null && partyA == null && projName == null && salesName == null) continue;
            result.RowsScanned++;

            int excelRow = r + 1; // 1 基，便于用户在 Excel 里定位

            // 层级
            var stageRaw = CellReader.Str(row.GetCell(C_Stage));
            var stage = Stages.Parse(stageRaw);
            if (stage == null)
            {
                result.Anomalies.Add(new ImportAnomaly(sheetName, excelRow, "合同层级", stageRaw ?? "", "无法识别的合同层级"));
                continue; // 层级是核心维度，缺失则该行不入库
            }

            var c = new Core.Contract
            {
                Stage = stage.Value,
                ContractNo = CellReader.Str(row.GetCell(C_ContractNo)),
                ProjectAttribute = NormalizeAttr(CellReader.Str(row.GetCell(C_ProjAttr))),
                ProjectName = projName,
                PartyA = partyA,
                PartyB = CellReader.Str(row.GetCell(C_PartyB)) ?? "中国卫通集团股份有限公司",
                SalesPersonName = salesName ?? "(未填)",
                OwnerCode = salesName ?? "(未填)",
                MarketField = CellReader.Str(row.GetCell(C_MarketField)),
                BusinessSegment = CellReader.Str(row.GetCell(C_Segment)),
                ProductType = CellReader.Str(row.GetCell(C_Product)),
                Currency = CellReader.Str(row.GetCell(C_Currency)) ?? "人民币",
                CultivationSource = CellReader.Str(row.GetCell(C_CultSource)),
                ServiceMonthsThisYear = CellReader.Int(row.GetCell(C_ServiceMonths)),
                CreatedAt = now, CreatedBy = by, UpdatedAt = now, UpdatedBy = by, RowVersion = 1,
            };

            // 金额（元 → 分）
            c.ContractAmountCents = ToCents(row, C_Amount, sheetName, excelRow, "销售合同金额", result);
            c.RevenueEstCents = ToCents(row, C_Revenue, sheetName, excelRow, "本年预计收入", result);
            c.CostEstCents = ToCents(row, C_Cost, sheetName, excelRow, "本年预计成本", result);
            c.ReceivedToDateCents = ToCents(row, C_ReceivedToDate, sheetName, excelRow, "截止累计到款", result);

            // 日期
            c.SignDate = ReadDate(row, C_SignDate, sheetName, excelRow, "签约日期", result);
            c.ExpectedSignDate = ReadDate(row, C_ExpSignDate, sheetName, excelRow, "预计签约日期", result);
            c.ValidFrom = ReadDate(row, C_ValidFrom, sheetName, excelRow, "有效期起", result);
            c.ValidTo = ReadDate(row, C_ValidTo, sheetName, excelRow, "有效期止", result);

            // 月度到款（预计口径）
            AddPayment(c, C_Pay1to4, 4, true, row);
            AddPayment(c, C_Pay5, 5, false, row);
            AddPayment(c, C_Pay6, 6, false, row);
            AddPayment(c, C_Pay7, 7, false, row);
            AddPayment(c, C_Pay8, 8, false, row);
            AddPayment(c, C_Pay9, 9, false, row);
            AddPayment(c, C_Pay10, 10, false, row);
            AddPayment(c, C_Pay11, 11, false, row);
            AddPayment(c, C_Pay12, 12, false, row);

            // 勾稽校验：V(预计到款) 应 = 月度之和（记异常但不阻断）
            var declaredV = CellReader.Yuan(row.GetCell(C_PaymentTotal));
            if (declaredV.HasValue)
            {
                long declaredCents = Money.FromYuan(declaredV.Value);
                long sumCents = c.PaymentForecastTotalCents;
                if (Math.Abs(declaredCents - sumCents) > Money.CentsPerYuan) // 容差 1 元
                    result.Anomalies.Add(new ImportAnomaly(sheetName, excelRow, "本年预计到款",
                        Money.FormatYuan(declaredCents),
                        $"与月度之和({Money.FormatYuan(sumCents)})不符，差{Money.FormatYuan(declaredCents - sumCents)}"));
            }

            // 用"工作表#行号"作稳定唯一键（行号在表内唯一；序号在物联网表会重复，不能用）
            c.ContractUid = $"{sheetName}#{excelRow}";

            result.Contracts.Add(c);
            Bump(result.BySalesperson, c.SalesPersonName);
            Bump(result.ByStage, Stages.ChineseName(c.Stage));
        }
    }

    private static long ToCents(IRow row, int col, string sheet, int excelRow, string field, ImportResult result)
    {
        var y = CellReader.Yuan(row.GetCell(col));
        if (y == null)
        {
            result.Anomalies.Add(new ImportAnomaly(sheet, excelRow, field,
                CellReader.Str(row.GetCell(col)) ?? "", "金额无法解析，按 0 处理"));
            return 0;
        }
        if (y < 0)
            result.Anomalies.Add(new ImportAnomaly(sheet, excelRow, field, y.Value.ToString("F2"), "金额为负，请核对"));
        return Money.FromYuan(y.Value);
    }

    private static DateOnly? ReadDate(IRow row, int col, string sheet, int excelRow, string field, ImportResult result)
    {
        var (date, ok, raw) = CellReader.Date(row.GetCell(col));
        if (!ok)
            result.Anomalies.Add(new ImportAnomaly(sheet, excelRow, field, raw, "日期无法解析"));
        return date;
    }

    private static void AddPayment(Core.Contract c, int col, int month, bool cumulative, IRow row)
    {
        var y = CellReader.Yuan(row.GetCell(col));
        long cents = y.HasValue ? Money.FromYuan(y.Value) : 0;
        if (cents == 0) return; // 不存 0 桶，省空间
        c.Payments.Add(new MonthlyPayment
        {
            Kind = PaymentKind.Forecast,
            PeriodMonth = month,
            IsCumulative = cumulative,
            AmountCents = cents,
        });
    }

    private static string? NormalizeAttr(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return raw;
        var t = raw.Trim();
        // 归一化简写："新签"→"新用户纯新签"不强行改，仅保留原值，避免误判；此处只去空白
        return t;
    }

    private static void Bump(Dictionary<string, int> d, string key) =>
        d[key] = d.TryGetValue(key, out var v) ? v + 1 : 1;
}
