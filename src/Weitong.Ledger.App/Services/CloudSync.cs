using System.Text.Json;
using COSXML;
using COSXML.Auth;
using COSXML.Model.Bucket;
using COSXML.Model.Object;
using Weitong.Ledger.Core;

namespace Weitong.Ledger.App.Services;

/// <summary>
/// 腾讯云 COS 同步：把本人数据上传到 {prefix}{机器码}/latest.json，
/// 拉取全员文件合并成汇总。上云前用团队口令 AES-256-GCM 加密(若已设)。
/// 连接参数来自后台 cos.json（普通销售无感知）。
/// </summary>
public sealed class CloudSync
{
    private readonly CosSettings _s;
    public CloudSync(CosSettings s) => _s = s;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    private CosXml Build()
    {
        var config = new CosXmlConfig.Builder().SetRegion(_s.Region).IsHttps(true).Build();
        var cred = new DefaultQCloudCredentialProvider(_s.SecretId, _s.SecretKey, 600);
        return new CosXmlServer(config, cred);
    }

    private string KeyFor(string personCode) => _s.Prefix + personCode + "/latest.json";
    private string ReviewPrefix => _s.Prefix + "_review/";
    private string ReviewKey(string byCode) => ReviewPrefix + "by-" + byCode + ".json";
    private string DecisionKey(string byCode) => ReviewPrefix + "dec-" + byCode + ".json";

    private byte[] Protect(byte[] plain) =>
        _s.HasTeamKey ? TeamCrypto.Encrypt(plain, _s.TeamKey) : plain;
    private byte[] Unprotect(byte[] blob) =>
        TeamCrypto.IsEncrypted(blob)
            ? TeamCrypto.Decrypt(blob, _s.HasTeamKey ? _s.TeamKey
                : throw new InvalidOperationException("云上数据已加密，但本机未配置团队口令，无法解密。"))
            : blob;

    public void UploadMine(SyncPayload payload)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(payload, JsonOpts);
        var body = Protect(json);
        var cos = Build();
        var put = new PutObjectRequest(_s.Bucket, KeyFor(payload.PersonCode), body);
        cos.PutObject(put);
    }

    public List<PeerInfo> ListPeers()
    {
        var cos = Build();
        var req = new GetBucketRequest(_s.Bucket);
        req.SetPrefix(_s.Prefix);
        var res = cos.GetBucket(req);
        var peers = new List<PeerInfo>();
        foreach (var c in res.listBucket.contentsList)
        {
            if (!c.key.EndsWith("/latest.json")) continue;
            var personCode = c.key.Substring(_s.Prefix.Length).Split('/')[0];
            DateTime? lm = DateTime.TryParse(c.lastModified, out var d) ? d.ToUniversalTime() : null;
            peers.Add(new PeerInfo(personCode, lm, c.size));
        }
        return peers;
    }

    public SyncPayload? DownloadOne(string personCode)
    {
        var cos = Build();
        var get = new GetObjectBytesRequest(_s.Bucket, KeyFor(personCode));
        var res = cos.GetObject(get);
        return JsonSerializer.Deserialize<SyncPayload>(Unprotect(res.content), JsonOpts);
    }

    /// <summary>拉取全员并合并去重(按 ContractUid)。</summary>
    public (List<Contract> merged, List<SyncPayload> payloads) DownloadAll(Action<string>? log = null)
    {
        var payloads = new List<SyncPayload>();
        foreach (var p in ListPeers())
        {
            try
            {
                var payload = DownloadOne(p.PersonCode);
                if (payload != null) { payloads.Add(payload); log?.Invoke($"已拉取 {payload.PersonName} · {payload.Count} 条"); }
            }
            catch (Exception ex) { log?.Invoke($"拉取 {p.PersonCode} 失败：{ex.Message}"); }
        }
        var byUid = new Dictionary<string, Contract>();
        foreach (var pl in payloads)
            foreach (var c in pl.Contracts)
                byUid[c.ContractUid] = c;
        return (byUid.Values.ToList(), payloads);
    }

    // ————————— 审批通道（管理员提案 / 销售决策） —————————

    /// <summary>上传我发起的全部审批项（管理员）。空则删除云上旧文件也可，这里直接覆盖。</summary>
    public void UploadReview(ReviewBundle bundle)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(bundle, JsonOpts);
        var cos = Build();
        cos.PutObject(new PutObjectRequest(_s.Bucket, ReviewKey(bundle.ByCode), Protect(json)));
    }

    /// <summary>上传我作出的全部决策（销售）。</summary>
    public void UploadDecisions(DecisionBundle bundle)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(bundle, JsonOpts);
        var cos = Build();
        cos.PutObject(new PutObjectRequest(_s.Bucket, DecisionKey(bundle.ByCode), Protect(json)));
    }

    private List<string> ListReviewKeys(string filePrefix)
    {
        var cos = Build();
        var req = new GetBucketRequest(_s.Bucket);
        req.SetPrefix(ReviewPrefix + filePrefix);
        var res = cos.GetBucket(req);
        return res.listBucket.contentsList.Select(c => c.key).Where(k => k.EndsWith(".json")).ToList();
    }

    private byte[] GetBytes(string key)
    {
        var cos = Build();
        return cos.GetObject(new GetObjectBytesRequest(_s.Bucket, key)).content;
    }

    /// <summary>拉取全部管理员的审批项，展平去重（按 OpId，后到覆盖）。</summary>
    public List<ReviewItem> DownloadReviews(Action<string>? log = null)
    {
        var byOp = new Dictionary<string, ReviewItem>();
        foreach (var key in ListReviewKeys("by-"))
        {
            try
            {
                var bundle = JsonSerializer.Deserialize<ReviewBundle>(Unprotect(GetBytes(key)), JsonOpts);
                if (bundle == null) continue;
                foreach (var it in bundle.Items) if (!string.IsNullOrEmpty(it.OpId)) byOp[it.OpId] = it;
            }
            catch (Exception ex) { log?.Invoke($"拉取审批项 {key} 失败：{ex.Message}"); }
        }
        return byOp.Values.ToList();
    }

    /// <summary>拉取全部销售的决策，展平去重（按 OpId，取较晚决策）。</summary>
    public List<ReviewDecision> DownloadDecisions(Action<string>? log = null)
    {
        var byOp = new Dictionary<string, ReviewDecision>();
        foreach (var key in ListReviewKeys("dec-"))
        {
            try
            {
                var bundle = JsonSerializer.Deserialize<DecisionBundle>(Unprotect(GetBytes(key)), JsonOpts);
                if (bundle == null) continue;
                foreach (var d in bundle.Decisions)
                    if (!byOp.TryGetValue(d.OpId, out var cur) || d.DecidedUtc > cur.DecidedUtc) byOp[d.OpId] = d;
            }
            catch (Exception ex) { log?.Invoke($"拉取决策 {key} 失败：{ex.Message}"); }
        }
        return byOp.Values.ToList();
    }
}
