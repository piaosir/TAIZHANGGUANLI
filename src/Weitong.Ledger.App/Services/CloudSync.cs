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
    private readonly string _teamPrefix;   // {Prefix}{团队令牌}/ —— 一团队一分区，跨团队在云端物理隔离
    public CloudSync(CosSettings s, string team)
    {
        _s = s;
        _teamPrefix = s.Prefix + TeamPartition.Token(team) + "/";
    }

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

    // 所有对象键都在本团队前缀 _teamPrefix 之下：别的团队用别的前缀，互不可见（含管理员）。
    private string KeyFor(string personCode) => _teamPrefix + personCode + "/latest.json";
    private string ReviewPrefix => _teamPrefix + "_review/";
    private string ReviewKey(string byCode) => ReviewPrefix + "by-" + byCode + ".json";
    private string DecisionKey(string byCode) => ReviewPrefix + "dec-" + byCode + ".json";
    private string TeamTargetKey => _teamPrefix + "_team/targets.json";

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
        req.SetPrefix(_teamPrefix);        // 只列本团队分区下的成员文件
        var res = cos.GetBucket(req);
        var peers = new List<PeerInfo>();
        foreach (var c in res.listBucket.contentsList)
        {
            if (!c.key.EndsWith("/latest.json")) continue;
            var personCode = c.key.Substring(_teamPrefix.Length).Split('/')[0];
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

    /// <summary>
    /// 拉取全员并按 ContractUid 合并：逐 UID 取<b>最后修改时间最新</b>的版本（<see cref="MergeArbiter"/>），
    /// 而非"最后拉到的覆盖"。返回的 merged <b>含墓碑</b>（IsDeleted=true）——调用方展示前应过滤、落库时应保留，
    /// 这样删除才能盖过其它设备的旧存活副本、且不被复活。
    /// </summary>
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
        var merged = MergeArbiter.MergeByUid(payloads.SelectMany(pl => pl.Contracts));
        return (merged, payloads);
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

    // ————————— 团队目标（管理员统一设定，全组共享） —————————

    /// <summary>上传团队年度目标（管理员）。写在本团队前缀下，别的团队互不影响。</summary>
    public void UploadTeamTargets(TeamTargetBundle bundle)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(bundle, JsonOpts);
        var cos = Build();
        cos.PutObject(new PutObjectRequest(_s.Bucket, TeamTargetKey, Protect(json)));
    }

    /// <summary>下载本团队年度目标（本团队前缀下的单一对象）。不存在/读取失败 → null（调用方不覆盖本地）。</summary>
    public TeamTargetBundle? DownloadTeamTargets()
    {
        try { return JsonSerializer.Deserialize<TeamTargetBundle>(Unprotect(GetBytes(TeamTargetKey)), JsonOpts); }
        catch { return null; }
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
