using System.IO;
using System.Text.Json;

namespace Weitong.Ledger.App.Services;

/// <summary>
/// 后台 COS 连接配置。密钥<b>随 exe 内置</b>（cos.json 作为嵌入资源编译进程序集），
/// 分发后销售零配置、也不会在别人电脑上再生成 cos.json 文件。
/// 仍支持在 exe 旁放一份外部 cos.json 覆盖内置（换密钥无需重编译）。
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

    /// <summary>
    /// 人员名册：姓名→团队→角色。台账明细/达成总览按此「所属团队」分权：同组可见本组全部、跨组屏蔽、管理员/领导看全部。
    /// 由管理员在 cos.json 统一维护、随软件下发。<b>名册为空则不启用团队分权（全部可见），仅按 AdminNames 判管理员——向后兼容。</b>
    /// Role 取值：sales(普通成员) / manager(组长) / admin(管理员·领导，看全部)。
    /// </summary>
    public List<RosterMember> Roster { get; set; } = new();

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
    /// 读取 COS 配置，优先级：
    /// ① exe 旁的外部 cos.json（管理员放置可覆盖内置，用于换密钥而无需重编译）；
    /// ② 用户稳定目录（%LocalAppData%）的外部 cos.json（历史遗留/手动放置）；
    /// ③ <b>内置：编译进 exe 的 cos.json 嵌入资源</b>——随软件分发，销售零配置、不在他机生成文件；
    /// ④ 三者皆无（多为他人 fresh clone、构建时未放 cos.json）：写一份模板到 exe 旁并返回未配置，保持旧的开发机行为。
    /// 命中①/②/③时<b>不再写任何文件</b>——从此别人电脑上不会再出现 cos.json。
    /// </summary>
    public static CosSettings Load()
    {
        // ① exe 旁的外部 cos.json——优先（管理员可放一份覆盖内置）
        var beside = TryRead(FilePath);
        if (beside is { IsConfigured: true }) return beside;

        // ② 用户稳定目录的外部 cos.json
        var user = TryRead(UserFilePath);
        if (user is { IsConfigured: true }) return user;

        // ③ 内置嵌入资源——随 exe 分发，销售无需任何配置；命中即用，不落地任何文件
        var embedded = TryReadEmbedded();
        if (embedded is { IsConfigured: true }) return embedded;

        // ④ 全无可用配置：写模板到 exe 旁（保留已有的地域/桶名），返回未配置
        var template = beside ?? new CosSettings
        {
            SecretId = "在这里填写你的SecretId",
            SecretKey = "在这里填写你的SecretKey",
            TeamKey = "在这里填写团队同步口令（全队一致，自定义一句话）",
        };
        TryWrite(FilePath, template);
        return template;
    }

    /// <summary>读取编译进 exe 的 cos.json 嵌入资源（LogicalName=cos.json）。未内置（资源不存在）时返回 null。</summary>
    private static CosSettings? TryReadEmbedded()
    {
        try
        {
            using var stream = typeof(CosSettings).Assembly.GetManifestResourceStream("cos.json");
            if (stream is null) return null;
            using var reader = new StreamReader(stream);
            return JsonSerializer.Deserialize<CosSettings>(reader.ReadToEnd(), JsonOpts);
        }
        catch { return null; }
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

/// <summary>名册一名成员：姓名、所属团队、角色（sales/manager/admin）。</summary>
public sealed class RosterMember
{
    public string Name { get; set; } = "";
    public string Team { get; set; } = "";
    /// <summary>sales(普通成员) / manager(组长) / admin(管理员·领导，看全部)。</summary>
    public string Role { get; set; } = "sales";
}
