using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Weitong.Ledger.App.Services;

/// <summary>
/// 本机应用配置：身份(机器码↔人员) + 腾讯云 COS 设置。
/// 存 %LocalAppData%\WeitongLedger\config.json；COS 密钥用 DPAPI(当前用户)加密后存，
/// 明文密钥绝不落盘、绝不外传。
/// </summary>
public sealed class AppConfig
{
    // —— 身份 ——
    public string? PersonName { get; set; }      // 显示名，如 朴东旭
    public string? TeamName { get; set; }        // 团队，如 行业市场组
    public string? PersonCode { get; set; }      // 稳定分区码（默认=机器短码）
    public string? MachineCode { get; set; }     // 绑定的机器码
    public DateTime? BoundAtUtc { get; set; }

    // —— 腾讯云 COS ——
    public bool CosEnabled { get; set; }
    public string? CosRegion { get; set; }       // 如 ap-guangzhou
    public string? CosBucket { get; set; }       // 如 taizhang-1250000000
    public string CosPrefix { get; set; } = "taizhang/";
    public string? CosSecretIdProtected { get; set; }   // DPAPI base64
    public string? CosSecretKeyProtected { get; set; }  // DPAPI base64
    public string? CosTeamKeyProtected { get; set; }    // 团队同步密钥(上云加密) DPAPI base64

    [JsonIgnore] public bool IsIdentitySet => !string.IsNullOrWhiteSpace(PersonName);
    [JsonIgnore] public bool IsCosConfigured =>
        CosEnabled && !string.IsNullOrWhiteSpace(CosRegion) && !string.IsNullOrWhiteSpace(CosBucket)
        && CosSecretIdProtected != null && CosSecretKeyProtected != null;

    // —— 密钥读写（DPAPI）——
    public void SetCosSecrets(string secretId, string secretKey)
    {
        CosSecretIdProtected = Protect(secretId);
        CosSecretKeyProtected = Protect(secretKey);
    }
    public string? GetCosSecretId() => Unprotect(CosSecretIdProtected);
    public string? GetCosSecretKey() => Unprotect(CosSecretKeyProtected);
    public void SetTeamKey(string? key) => CosTeamKeyProtected = string.IsNullOrEmpty(key) ? null : Protect(key);
    public string? GetTeamKey() => Unprotect(CosTeamKeyProtected);
    public bool HasTeamKey => CosTeamKeyProtected != null;

    private static readonly byte[] Entropy = "WeitongLedger.cos"u8.ToArray();
    private static string Protect(string s) =>
        Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(s), Entropy, DataProtectionScope.CurrentUser));
    private static string? Unprotect(string? b64)
    {
        if (string.IsNullOrEmpty(b64)) return null;
        try { return Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(b64), Entropy, DataProtectionScope.CurrentUser)); }
        catch { return null; }
    }

    // —— 持久化 ——
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static string DefaultDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WeitongLedger");

    private static string PathFor(string dir) => Path.Combine(dir, "config.json");

    public static AppConfig Load(string dir)
    {
        var path = PathFor(dir);
        if (File.Exists(path))
        {
            try { return JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(path), JsonOpts) ?? new AppConfig(); }
            catch { return new AppConfig(); }
        }
        return new AppConfig();
    }

    public void Save(string dir)
    {
        Directory.CreateDirectory(dir);
        File.WriteAllText(PathFor(dir), JsonSerializer.Serialize(this, JsonOpts));
    }
}
