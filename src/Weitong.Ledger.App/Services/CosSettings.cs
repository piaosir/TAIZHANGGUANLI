using System.IO;
using System.Text.Json;

namespace Weitong.Ledger.App.Services;

/// <summary>
/// 后台 COS 连接配置。从应用目录下的 cos.json 读取（管理员一次性填好、随软件分发）。
/// 普通销售看不到、不用配。地域/桶名预置，密钥由管理员填一次。
/// </summary>
public sealed class CosSettings
{
    public string Region { get; set; } = "ap-guangzhou";
    public string Bucket { get; set; } = "taizhang-1385987144";
    public string Prefix { get; set; } = "taizhang/";
    public string SecretId { get; set; } = "";
    public string SecretKey { get; set; } = "";
    /// <summary>团队同步口令：上云前用它加密数据。全队一致。留空=明文上云。</summary>
    public string TeamKey { get; set; } = "";

    /// <summary>管理员名单：姓名在此列表中的使用人即为管理员（可在 cos.json 调整）。</summary>
    public List<string> AdminNames { get; set; } = new() { "丁晖", "李偲", "张磊", "朴东旭", "刘依婷" };

    private const string Placeholder = "在这里填写";

    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(SecretId) && !SecretId.Contains(Placeholder) &&
        !string.IsNullOrWhiteSpace(SecretKey) && !SecretKey.Contains(Placeholder) &&
        !string.IsNullOrWhiteSpace(Region) && !string.IsNullOrWhiteSpace(Bucket);

    [System.Text.Json.Serialization.JsonIgnore]
    public bool HasTeamKey => !string.IsNullOrWhiteSpace(TeamKey) && !TeamKey.Contains(Placeholder);

    /// <summary>exe 旁的 cos.json（随分发包走）。</summary>
    public static string FilePath => Path.Combine(AppContext.BaseDirectory, "cos.json");

    /// <summary>用户级稳定 cos.json（%LocalAppData%\WeitongLedger\cos.json）。
    /// 关键：<b>重新编译 / 清理 bin / 换构建都不会丢</b>——开发版(bin)与分发版都能从这里读到已配好的密钥，
    /// 从此不再出现「跑了没填密钥的构建 → 同步被静默跳过」。</summary>
    public static string UserFilePath => Path.Combine(AppConfig.DefaultDir, "cos.json");

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>
    /// 读取 cos.json，优先级：① exe 旁（分发包）② 用户稳定目录（%LocalAppData%）。
    /// 任一处已配置好，就采用它，并<b>同步补写到另一处</b>，使配置在"重建/清理/换构建"后依然生效。
    /// 两处都没有可用配置时，在 exe 旁写一份模板（密钥待管理员填一次）后返回未配置。
    /// </summary>
    public static CosSettings Load()
    {
        // ① exe 旁（随分发包）——优先
        var beside = TryRead(FilePath);
        if (beside is { IsConfigured: true }) { TryWrite(UserFilePath, beside); return beside; }

        // ② 用户稳定目录——bin 被清理/重建后仍能同步的关键
        var user = TryRead(UserFilePath);
        if (user is { IsConfigured: true }) { TryWrite(FilePath, user); return user; }

        // ③ 两处都未配置：写模板到 exe 旁（保留已有的地域/桶名），返回未配置
        var template = beside ?? new CosSettings
        {
            SecretId = "在这里填写你的SecretId",
            SecretKey = "在这里填写你的SecretKey",
            TeamKey = "在这里填写团队同步口令（全队一致，自定义一句话）",
        };
        TryWrite(FilePath, template);
        return template;
    }

    private static CosSettings? TryRead(string path)
    {
        try { return File.Exists(path) ? JsonSerializer.Deserialize<CosSettings>(File.ReadAllText(path), JsonOpts) : null; }
        catch { return null; }
    }

    private static void TryWrite(string path, CosSettings s)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(s, JsonOpts));
        }
        catch { /* 只读目录等失败可忽略：另一处仍可用 */ }
    }
}
