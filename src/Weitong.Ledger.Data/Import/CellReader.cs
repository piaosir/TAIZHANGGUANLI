using System.Globalization;
using NPOI.SS.UserModel;

namespace Weitong.Ledger.Data.Import;

/// <summary>
/// 健壮的单元格读取工具，专治真实台账里的脏数据：
/// 日期有 datetime / ' 2025-06-23' / '2026-9-25' / '2026.9' 多种写法，
/// 金额有数字 0 与字符串 '0' 混用，等等。
/// </summary>
internal static class CellReader
{
    public static string? Str(ICell? cell)
    {
        if (cell == null) return null;
        string s = cell.CellType switch
        {
            CellType.String => cell.StringCellValue,
            CellType.Numeric => DateUtil.IsCellDateFormatted(cell)
                ? cell.DateCellValue?.ToString("yyyy-MM-dd") ?? ""
                : cell.NumericCellValue.ToString(CultureInfo.InvariantCulture),
            CellType.Boolean => cell.BooleanCellValue.ToString(),
            CellType.Formula => FormulaStr(cell),
            _ => ""
        };
        s = s?.Trim() ?? "";
        return string.IsNullOrEmpty(s) ? null : s;
    }

    private static string FormulaStr(ICell cell)
    {
        try
        {
            return cell.CachedFormulaResultType switch
            {
                CellType.String => cell.StringCellValue,
                CellType.Numeric => cell.NumericCellValue.ToString(CultureInfo.InvariantCulture),
                _ => ""
            };
        }
        catch { return ""; }
    }

    /// <summary>读金额，返回"元"（double）。空 → 0。无法解析 → null（记异常）。</summary>
    public static double? Yuan(ICell? cell)
    {
        if (cell == null) return 0;
        switch (cell.CellType)
        {
            case CellType.Numeric:
                return cell.NumericCellValue;
            case CellType.Blank:
                return 0;
            case CellType.String:
                var raw = cell.StringCellValue?.Trim();
                if (string.IsNullOrEmpty(raw)) return 0;
                raw = raw.Replace(",", "").Replace("，", "").Replace("¥", "").Replace(" ", "");
                if (double.TryParse(raw, NumberStyles.Any, CultureInfo.InvariantCulture, out var v)) return v;
                return null;
            case CellType.Formula:
                try { return cell.NumericCellValue; } catch { return null; }
            default:
                return 0;
        }
    }

    public static int? Int(ICell? cell)
    {
        var y = Yuan(cell);
        if (y == null) return null;
        return (int)Math.Round(y.Value);
    }

    /// <summary>读日期，容忍多种脏格式。无值 → null。无法解析 → 抛给调用方记异常。</summary>
    public static (DateOnly? date, bool ok, string raw) Date(ICell? cell)
    {
        if (cell == null) return (null, true, "");
        if (cell.CellType == CellType.Blank) return (null, true, "");

        if (cell.CellType == CellType.Numeric)
        {
            if (DateUtil.IsCellDateFormatted(cell))
            {
                var dt = cell.DateCellValue;
                return dt.HasValue ? (DateOnly.FromDateTime(dt.Value), true, dt.Value.ToString("yyyy-MM-dd")) : (null, true, "");
            }
            // 形如 2026.9（年.月）
            double num = cell.NumericCellValue;
            var yearMonth = ParseYearDotMonth(num);
            if (yearMonth.HasValue) return (yearMonth, true, num.ToString(CultureInfo.InvariantCulture));
            // 可能是 Excel 序列号但未标记日期格式
            try
            {
                var dt2 = DateUtil.GetJavaDate(num);
                return (DateOnly.FromDateTime(dt2), true, num.ToString(CultureInfo.InvariantCulture));
            }
            catch { return (null, false, num.ToString(CultureInfo.InvariantCulture)); }
        }

        var raw = cell.CellType == CellType.String ? cell.StringCellValue?.Trim() ?? "" : Str(cell) ?? "";
        if (string.IsNullOrEmpty(raw)) return (null, true, "");

        var parsed = ParseLooseDate(raw);
        return parsed.HasValue ? (parsed, true, raw) : (null, false, raw);
    }

    /// <summary>"2026.9" → 2026-09-01；"2026.12" → 2026-12-01。</summary>
    private static DateOnly? ParseYearDotMonth(double num)
    {
        // 2026.9 表示 2026 年 9 月；2026.12 表示 12 月
        long yearPart = (long)Math.Floor(num);
        if (yearPart < 2000 || yearPart > 2100) return null;
        double frac = num - yearPart;
        int month;
        if (frac == 0) month = 1;
        else
        {
            // .9 → 9 月，.12 → 12 月（按字面小数位理解）
            string fracStr = num.ToString(CultureInfo.InvariantCulture).Split('.')[1];
            if (!int.TryParse(fracStr, out month)) return null;
        }
        if (month < 1 || month > 12) return null;
        return new DateOnly((int)yearPart, month, 1);
    }

    private static DateOnly? ParseLooseDate(string raw)
    {
        raw = raw.Trim().Replace('/', '-').Replace('.', '-').Replace('年', '-').Replace('月', '-').Replace('日', ' ').Trim();
        string[] fmts =
        {
            "yyyy-M-d", "yyyy-MM-dd", "yyyy-M", "yyyy-MM",
            "yyyy-M-d H:mm:ss", "yyyy-MM-dd HH:mm:ss"
        };
        foreach (var f in fmts)
            if (DateTime.TryParseExact(raw, f, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt))
                return DateOnly.FromDateTime(dt);
        if (DateTime.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt2))
            return DateOnly.FromDateTime(dt2);
        var parts = raw.Split('-', StringSplitOptions.RemoveEmptyEntries);
        // "yyyy-M"（年月）→ 月初
        if (parts.Length >= 2 && int.TryParse(parts[0], out var yy) && int.TryParse(parts[1], out var mm)
            && yy is >= 2000 and <= 2100 && mm is >= 1 and <= 12)
            return new DateOnly(yy, mm, 1);
        // "yyyy年" / "yyyy"（仅年，多为在手老合同）→ 年初
        if (parts.Length == 1 && int.TryParse(parts[0], out var yOnly) && yOnly is >= 2000 and <= 2100)
            return new DateOnly(yOnly, 1, 1);
        return null;
    }
}
