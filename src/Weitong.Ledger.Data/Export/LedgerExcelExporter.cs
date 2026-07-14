using NPOI.SS.UserModel;
using NPOI.XSSF.UserModel;
using Weitong.Ledger.Core;

namespace Weitong.Ledger.Data.Export;

/// <summary>
/// 把台账数据导出为与原始 Excel 标准版完全一致列结构的 xlsx（内容来自数据库）。
/// </summary>
public static class LedgerExcelExporter
{
    // 与原台账表头一一对应（A..AG）
    private static readonly string[] Headers =
    {
        "序号", "合同层级", "合同编号", "签约日期", "预计签约日期", "项目属性", "项目名称",
        "合同甲方", "合同乙方", "销售人员", "市场领域", "本部业务板块", "产品类型", "币种",
        "销售合同金额", "合同有效期起", "合同有效期止", "本年服务期数", "本年预计属期收入",
        "本年预计直接成本", "本年预计业务利润", "本年预计到款", "1-4月累计到款", "5月预计到款",
        "6月预计到款", "7月预计到款", "8月预计到款", "9月预计到款", "10月预计到款",
        "11月预计到款", "12月预计到款", "截止目前合同累计到款", "培育来源",
    };

    public static void Export(IEnumerable<Contract> contracts, string path)
    {
        IWorkbook wb = new XSSFWorkbook();
        var sheet = wb.CreateSheet("本部合同台账");

        // 表头样式
        var headStyle = wb.CreateCellStyle();
        var headFont = wb.CreateFont();
        headFont.IsBold = true;
        headStyle.SetFont(headFont);
        headStyle.FillForegroundColor = IndexedColors.Grey25Percent.Index;
        headStyle.FillPattern = FillPattern.SolidForeground;
        headStyle.BorderBottom = BorderStyle.Thin;

        var header = sheet.CreateRow(0);
        for (int c = 0; c < Headers.Length; c++)
        {
            var cell = header.CreateCell(c);
            cell.SetCellValue(Headers[c]);
            cell.CellStyle = headStyle;
        }

        int r = 1;
        foreach (var x in contracts.Where(c => !c.IsDeleted))
        {
            var row = sheet.CreateRow(r);
            int i = 0;
            Num(row, i++, r);                                   // 序号
            Str(row, i++, Stages.ChineseName(x.Stage));         // 合同层级
            Str(row, i++, x.ContractNo);                        // 合同编号
            Date(row, i++, x.SignDate);                         // 签约日期
            Date(row, i++, x.ExpectedSignDate);                 // 预计签约日期
            Str(row, i++, x.ProjectAttribute);                  // 项目属性
            Str(row, i++, x.ProjectName);                       // 项目名称
            Str(row, i++, x.PartyA);                            // 甲方
            Str(row, i++, x.PartyB);                            // 乙方
            Str(row, i++, x.SalesPersonName);                   // 销售人员
            Str(row, i++, x.MarketField);                       // 市场领域
            Str(row, i++, x.BusinessSegment);                   // 业务板块
            Str(row, i++, x.ProductType);                       // 产品类型
            Str(row, i++, x.Currency);                          // 币种
            Yuan(row, i++, x.ContractAmountCents);              // 销售合同金额
            Date(row, i++, x.ValidFrom);                        // 有效期起
            Date(row, i++, x.ValidTo);                          // 有效期止
            NumN(row, i++, x.ServiceMonthsThisYear);            // 本年服务期数
            Yuan(row, i++, x.RevenueEstCents);                  // 收入 S
            Yuan(row, i++, x.CostEstCents);                     // 成本 T
            Yuan(row, i++, x.ProfitEstCents);                   // 利润 U
            Yuan(row, i++, x.PaymentForecastTotalCents);        // 预计到款 V
            Yuan(row, i++, Month(x, 4, true));                  // 1-4月累计
            Yuan(row, i++, Month(x, 5, false));
            Yuan(row, i++, Month(x, 6, false));
            Yuan(row, i++, Month(x, 7, false));
            Yuan(row, i++, Month(x, 8, false));
            Yuan(row, i++, Month(x, 9, false));
            Yuan(row, i++, Month(x, 10, false));
            Yuan(row, i++, Month(x, 11, false));
            Yuan(row, i++, Month(x, 12, false));                // 12月
            Yuan(row, i++, x.ReceivedToDateCents);              // 截止累计到款
            Str(row, i++, x.CultivationSource);                 // 培育来源
            r++;
        }

        for (int c = 0; c < Headers.Length; c++) sheet.SetColumnWidth(c, 16 * 256);

        using var fs = File.Create(path);
        wb.Write(fs);
    }

    private static long Month(Contract c, int month, bool cumulative) =>
        c.Payments.Where(p => p.Kind == PaymentKind.Forecast && p.PeriodMonth == month && p.IsCumulative == cumulative)
                  .Sum(p => p.AmountCents);

    private static void Str(IRow row, int i, string? v) { if (!string.IsNullOrEmpty(v)) row.CreateCell(i).SetCellValue(v); else row.CreateCell(i); }
    private static void Num(IRow row, int i, double v) => row.CreateCell(i).SetCellValue(v);
    private static void NumN(IRow row, int i, int? v) { if (v.HasValue) row.CreateCell(i).SetCellValue(v.Value); else row.CreateCell(i); }
    private static void Yuan(IRow row, int i, long cents) => row.CreateCell(i).SetCellValue((double)Money.ToYuan(cents));
    private static void Date(IRow row, int i, DateOnly? d)
    {
        var cell = row.CreateCell(i);
        if (d.HasValue) cell.SetCellValue(d.Value.ToString("yyyy-MM-dd"));
    }
}
