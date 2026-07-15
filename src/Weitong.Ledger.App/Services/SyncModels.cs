using System.Security.Cryptography;
using Org.BouncyCastle.Crypto.Digests;
using Org.BouncyCastle.Crypto.Engines;
using Org.BouncyCastle.Crypto.Generators;
using Org.BouncyCastle.Crypto.Modes;
using Org.BouncyCastle.Crypto.Parameters;
using Weitong.Ledger.Core;

namespace Weitong.Ledger.App.Services;

/// <summary>
/// 一个人上传到云的同步包。以机器码 PersonCode 作唯一分区键（支持重名）。
/// </summary>
public sealed class SyncPayload
{
    public int SchemaVersion { get; set; } = 1;
    public string PersonCode { get; set; } = "";
    public string PersonName { get; set; } = "";
    public string? TeamName { get; set; }
    public string MachineCode { get; set; } = "";
    public DateTime ExportedUtc { get; set; }
    public int Count { get; set; }
    public List<Contract> Contracts { get; set; } = new();
}

public sealed record PeerInfo(string PersonCode, DateTime? LastModifiedUtc, long SizeBytes);

/// <summary>一名管理员上传的全部审批项（增删改标记提案）。云端按发起人机器码分文件，互不覆盖。</summary>
public sealed class ReviewBundle
{
    public int SchemaVersion { get; set; } = 1;
    public string ByCode { get; set; } = "";
    public string ByName { get; set; } = "";
    public DateTime ExportedUtc { get; set; }
    public List<ReviewItem> Items { get; set; } = new();
}

/// <summary>一名销售上传的全部决策（确认/驳回/知晓），回流给发起管理员。</summary>
public sealed class DecisionBundle
{
    public int SchemaVersion { get; set; } = 1;
    public string ByCode { get; set; } = "";
    public string ByName { get; set; } = "";
    public DateTime ExportedUtc { get; set; }
    public List<ReviewDecision> Decisions { get; set; } = new();
}

/// <summary>
/// 团队年度目标同步包。云端为<b>单一权威对象</b>（{prefix}_team/targets.json）：
/// 由管理员设定后上传、全组下载并落本地库；含多年，天然支持逐年沿用。
/// </summary>
public sealed class TeamTargetBundle
{
    public int SchemaVersion { get; set; } = 1;
    public string TeamName { get; set; } = "";
    public string ByName { get; set; } = "";
    public string ByCode { get; set; } = "";
    public DateTime UpdatedUtc { get; set; }
    public List<TeamTargetEntry> Entries { get; set; } = new();
}

/// <summary>某团队某一年度的三条线指标（金额为「分」）。</summary>
public sealed class TeamTargetEntry
{
    public int Year { get; set; }
    public long RevenueTargetCents { get; set; }
    public long ProfitTargetCents { get; set; }
    public long CostCeilingCents { get; set; }
    /// <summary>该年度指标的最后编辑时间（UTC）。用于「谁更新用谁」的冲突裁决，避免旧值回滚新值。</summary>
    public DateTime UpdatedUtc { get; set; }
}

/// <summary>
/// 上云加密：默认使用<b>国密 SM4-GCM</b>（密钥由团队口令经 <b>SM3</b> 的 PBKDF2 派生）。
/// 云上仅存密文。兼容解密早期的 AES-GCM 数据（迁移期）。
/// 格式：MAGIC(4) | salt(16) | nonce(12) | 密文+tag
///   国密：MAGIC = "SMC1"；旧版：MAGIC = "WTC1"(AES)
/// </summary>
public static class TeamCrypto
{
    private static readonly byte[] MagicSm = "SMC1"u8.ToArray();  // 国密 SM4
    private static readonly byte[] MagicAes = "WTC1"u8.ToArray(); // 旧 AES（仅解密兼容）
    private const int Iterations = 120_000;

    public static bool IsEncrypted(byte[] b) => Has(b, MagicSm) || Has(b, MagicAes);
    private static bool Has(byte[] b, byte[] m) => b.Length >= 4 && b[0] == m[0] && b[1] == m[1] && b[2] == m[2] && b[3] == m[3];

    // —— 加密：一律国密 SM4-GCM ——
    public static byte[] Encrypt(byte[] plain, string passphrase)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        var nonce = RandomNumberGenerator.GetBytes(12);
        var key = DeriveSm4Key(passphrase, salt);

        var gcm = new GcmBlockCipher(new SM4Engine());
        gcm.Init(true, new AeadParameters(new KeyParameter(key), 128, nonce));
        var enc = new byte[gcm.GetOutputSize(plain.Length)];
        int len = gcm.ProcessBytes(plain, 0, plain.Length, enc, 0);
        gcm.DoFinal(enc, len);

        var outBuf = new byte[4 + 16 + 12 + enc.Length];
        Buffer.BlockCopy(MagicSm, 0, outBuf, 0, 4);
        Buffer.BlockCopy(salt, 0, outBuf, 4, 16);
        Buffer.BlockCopy(nonce, 0, outBuf, 20, 12);
        Buffer.BlockCopy(enc, 0, outBuf, 32, enc.Length);
        return outBuf;
    }

    // —— 解密：按 MAGIC 分派（国密 / 旧AES） ——
    public static byte[] Decrypt(byte[] blob, string passphrase)
    {
        if (Has(blob, MagicSm)) return DecryptSm(blob, passphrase);
        if (Has(blob, MagicAes)) return DecryptAes(blob, passphrase);
        return blob; // 明文
    }

    private static byte[] DecryptSm(byte[] blob, string passphrase)
    {
        var salt = blob[4..20]; var nonce = blob[20..32]; var enc = blob[32..];
        var key = DeriveSm4Key(passphrase, salt);
        var gcm = new GcmBlockCipher(new SM4Engine());
        gcm.Init(false, new AeadParameters(new KeyParameter(key), 128, nonce));
        var dec = new byte[gcm.GetOutputSize(enc.Length)];
        int len = gcm.ProcessBytes(enc, 0, enc.Length, dec, 0);
        len += gcm.DoFinal(dec, len);
        return dec[..len];
    }

    private static byte[] DecryptAes(byte[] blob, string passphrase)
    {
        var salt = blob[4..20]; var nonce = blob[20..32]; var tag = blob[32..48]; var cipher = blob[48..];
        var key = Rfc2898DeriveBytes.Pbkdf2(passphrase, salt, Iterations, HashAlgorithmName.SHA256, 32);
        var plain = new byte[cipher.Length];
        using var aes = new AesGcm(key, 16);
        aes.Decrypt(nonce, cipher, tag, plain);
        return plain;
    }

    /// <summary>SM3 的 PBKDF2 派生 128 位 SM4 密钥（全链国密）。</summary>
    private static byte[] DeriveSm4Key(string passphrase, byte[] salt)
    {
        var gen = new Pkcs5S2ParametersGenerator(new SM3Digest());
        gen.Init(System.Text.Encoding.UTF8.GetBytes(passphrase), salt, Iterations);
        return ((KeyParameter)gen.GenerateDerivedMacParameters(128)).GetKey();
    }
}
