using System.Globalization;

namespace Weitong.Ledger.Core;

/// <summary>
/// 金额工具：全系统内部一律以 <b>分（long）</b> 存储，严禁浮点，避免统计误差累积。
/// 元 = 分 / 100；万元 = 分 / 1_000_000。
/// 来源单位换算集中在这里，杜绝各处硬编码。
/// </summary>
public static class Money
{
    public const long CentsPerYuan = 100L;
    public const long CentsPerWan = 1_000_000L; // 1 万元 = 10000 元 = 1_000_000 分

    /// <summary>元（可含小数，如 132075.4717）→ 分（四舍五入到分）。</summary>
    public static long FromYuan(double yuan) =>
        (long)Math.Round(yuan * CentsPerYuan, MidpointRounding.AwayFromZero);

    /// <summary>元（decimal，精确）→ 分。</summary>
    public static long FromYuan(decimal yuan) =>
        (long)Math.Round(yuan * CentsPerYuan, MidpointRounding.AwayFromZero);

    /// <summary>万元 → 分。用于导入团队/个人指标（表1、子公司表口径为万元）。</summary>
    public static long FromWan(double wan) =>
        (long)Math.Round(wan * CentsPerWan, MidpointRounding.AwayFromZero);

    public static decimal ToYuan(long cents) => cents / 100m;
    public static decimal ToWan(long cents) => cents / 1_000_000m;

    /// <summary>加权：分 × 概率 → 分（四舍五入）。用于预测口径。</summary>
    public static long Weight(long cents, double probability) =>
        (long)Math.Round(cents * probability, MidpointRounding.AwayFromZero);

    /// <summary>格式化为"¥1,234.56 万"，聚合展示用。</summary>
    public static string FormatWan(long cents, int decimals = 2) =>
        "¥" + ToWan(cents).ToString("N" + decimals, CultureInfo.InvariantCulture) + " 万";

    /// <summary>格式化为"¥1,234,567.89"，单笔合同展示用。</summary>
    public static string FormatYuan(long cents) =>
        "¥" + ToYuan(cents).ToString("N2", CultureInfo.InvariantCulture);
}
