using System.Text.RegularExpressions;
using NPOI.SS.UserModel;
using NPOI.XSSF.UserModel;
using Weitong.Ledger.Core;

namespace Weitong.Ledger.Data.Import;

/// <summary>从填报表单解析出的一行团队年度指标（单位已换算为「分」）。</summary>
public sealed record TeamTargetRow(string TeamName, int Year, long RevenueCents, long ProfitCents, long CostCeilingCents);

/// <summary>
/// 从「市场策划进展表 / 填报表单」表1解析各团队的年度指标（收入 / 利润 / 成本控制，原单位万元）。
/// 关键：按<b>表头文字</b>定位列（而非固定列号），且排除「差距 / 差值 / 完成值」等派生列，
/// 因而明年表格增删列、列序变化也能正确解析；年份从表头「20xx年…指标」自动提取。
/// </summary>
public sealed class TargetFormImporter
{
    /// <summary>识别「指标表 / 策划进展表」的工作表名关键字。</summary>
    private static bool IsTargetSheet(string name) =>
        name.Contains("策划") || name.Contains("进展") || name.Contains("计划") || name.Contains("指标");

    public IReadOnlyList<TeamTargetRow> ParseFile(string path)
    {
        using var fs = File.OpenRead(path);
        return Parse(fs);
    }

    public IReadOnlyList<TeamTargetRow> Parse(Stream stream)
    {
        IWorkbook wb = new XSSFWorkbook(stream);

        // 先按表名关键字找；找不到再逐表兜底（凭表头识别）
        for (int si = 0; si < wb.NumberOfSheets; si++)
        {
            var sheet = wb.GetSheetAt(si);
            if (!IsTargetSheet(sheet.SheetName)) continue;
            var rows = ParseSheet(sheet);
            if (rows.Count > 0) return rows;
        }
        for (int si = 0; si < wb.NumberOfSheets; si++)
        {
            var rows = ParseSheet(wb.GetSheetAt(si));
            if (rows.Count > 0) return rows;
        }
        return Array.Empty<TeamTargetRow>();
    }

    private static List<TeamTargetRow> ParseSheet(ISheet sheet)
    {
        var result = new List<TeamTargetRow>();

        // 1) 定位表头行：同时含「收入指标 / 利润指标 / 成本控制指标」三种目标列（前 15 行内扫描）
        int headerRow = -1, revCol = -1, profCol = -1, costCol = -1, year = 0;
        int scan = Math.Min(sheet.LastRowNum, 15);
        for (int r = 0; r <= scan && headerRow < 0; r++)
        {
            var row = sheet.GetRow(r);
            if (row == null) continue;

            int rc = -1, pc = -1, cc = -1;
            for (int c = row.FirstCellNum; c < row.LastCellNum; c++)
            {
                var raw = CellReader.Str(row.GetCell(c));
                if (raw == null) continue;
                var t = raw.Replace("\n", "").Replace(" ", "");
                // 目标列：含「指标」，但排除「差距 / 差值 / 完成值 / 变动」等派生列
                bool isTargetCol = t.Contains("指标") && !t.Contains("差") && !t.Contains("完成") && !t.Contains("变动");
                if (!isTargetCol) continue;
                if (rc < 0 && t.Contains("收入指标")) rc = c;
                else if (pc < 0 && t.Contains("利润指标")) pc = c;
                else if (cc < 0 && t.Contains("成本控制指标")) cc = c;
            }

            if (rc >= 0 && pc >= 0 && cc >= 0)
            {
                headerRow = r; revCol = rc; profCol = pc; costCol = cc;
                year = ExtractYear(CellReader.Str(row.GetCell(rc)))
                    ?? ExtractYear(CellReader.Str(row.GetCell(pc)))
                    ?? ExtractYear(CellReader.Str(row.GetCell(cc)))
                    ?? 0;
            }
        }
        if (headerRow < 0) return result;

        // 2) 团队名列 = 表头行首个非空列（表1为「销售团队/领域」，即 A 列）
        int nameCol = sheet.GetRow(headerRow).FirstCellNum;

        // 3) 数据行：表头之后，团队名非空、且非说明/注释行
        for (int r = headerRow + 1; r <= sheet.LastRowNum; r++)
        {
            var row = sheet.GetRow(r);
            if (row == null) continue;

            var name = CellReader.Str(row.GetCell(nameCol));
            if (name == null) continue;
            // 表末注释行：「（一）合同层级…」「1、已签约…」「数据逻辑为…」等
            if (name.StartsWith("（") || name.StartsWith("(") || name.Contains("逻辑") || name.Contains("说明")
                || Regex.IsMatch(name, @"^\d+[、.]"))
                continue;

            long rev = Money.FromWan(CellReader.Yuan(row.GetCell(revCol)) ?? 0);
            long prof = Money.FromWan(CellReader.Yuan(row.GetCell(profCol)) ?? 0);
            long cost = Money.FromWan(CellReader.Yuan(row.GetCell(costCol)) ?? 0);

            // 只保留：像团队/汇总/领域 的行，或至少有一项非零指标的行（排除长段注释与空行）
            bool looksTeam = name.Contains("团队") || name.Contains("汇总") || name.Contains("领域");
            bool hasValue = rev != 0 || prof != 0 || cost != 0;
            if (!looksTeam && !hasValue) continue;

            result.Add(new TeamTargetRow(name, year == 0 ? DateTime.Now.Year : year, rev, prof, cost));
        }
        return result;
    }

    /// <summary>从「2026年收入指标」等表头提取年份。</summary>
    private static int? ExtractYear(string? header)
    {
        if (string.IsNullOrEmpty(header)) return null;
        var m = Regex.Match(header, @"(20\d{2})\s*年");
        return m.Success ? int.Parse(m.Groups[1].Value) : null;
    }
}
