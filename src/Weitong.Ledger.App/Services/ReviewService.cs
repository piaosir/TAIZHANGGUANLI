using Weitong.Ledger.Core;
using Weitong.Ledger.Data.Db;

namespace Weitong.Ledger.App.Services;

/// <summary>
/// 审批业务：管理员对全组数据发起增删改标记。改动<b>一律立即生效</b>（落本地库，随
/// <see cref="CloudSync"/> 云端合并按"最后修改最新者胜"广播到各端）；对<b>他人名下</b>的改动
/// 额外生成一条"通知"，路由到对应销售，销售端只需"知道了"（不再需要确认/驳回）。
/// 知晓回流给发起管理员。所有传递经 <see cref="CloudSync"/> 云通道。
/// </summary>
public sealed class ReviewService
{
    private readonly LedgerStore _store;
    private readonly RoleService _roles;

    public string MyName { get; }
    public string MyCode { get; }
    public bool IsAdmin => _roles.IsAdmin(MyName);

    public ReviewService(LedgerStore store, string myName, string myCode, RoleService roles)
    {
        _store = store; MyName = myName; MyCode = myCode; _roles = roles;
    }

    /// <summary>某归属人是否就是当前使用人（管理员改自己名下 → 直接生效）。</summary>
    public bool IsMine(string? ownerName) => string.Equals((ownerName ?? "").Trim(), MyName.Trim(), StringComparison.Ordinal);

    private ReviewItem NewItem(ReviewOpType type, string owner, string uid) => new()
    {
        OpId = Guid.NewGuid().ToString("N"),
        OpType = type,
        Status = ReviewStatus.Pending,
        TargetContractUid = uid,
        TargetOwnerName = (owner ?? "").Trim(),
        ByName = MyName,
        ByCode = MyCode,
        CreatedUtc = DateTime.UtcNow,
    };

    /// <summary>
    /// 管理员修改：改动一律立即生效（落库并经云端合并广播）。改他人名下时额外生成一条"通知"让对应销售知晓：
    /// 把本人名下记录转交他人 → 通知<b>接收人</b>；改他人名下记录 → 通知<b>当前归属人</b>。
    /// 返回 (是否已生效, 给用户看的说明)。Applied 仅在"无实际变化"时为 false，其余情形恒为 true。
    /// </summary>
    public (bool Applied, string Message) SubmitUpdate(Contract before, Contract after)
    {
        var summary = ContractOps.Summarize(before, after);
        if (string.IsNullOrEmpty(summary)) return (false, "");   // 无实际变化
        var beforeOwner = before.SalesPersonName;
        var afterOwner = after.SalesPersonName;

        // 管理员改动一律立即生效（落本地库，随云端合并广播到各端）；改他人名下时再生成"通知"让对应销售知晓。
        _store.UpsertContractByUid(ContractOps.Clone(after), MyName);

        if (IsMine(beforeOwner) && IsMine(afterOwner))
        {
            _store.WriteAudit("AdminEdit", MyName, "Contract", $"本人名下直接修改：{summary}");
            return (true, $"已直接生效（本人名下）：{summary}");
        }

        // 改他人名下 / 转交他人：改动已生效，仅通知对应销售（转交→通知接收人；改他人名下→通知当前归属人）
        var target = IsMine(beforeOwner) ? afterOwner : beforeOwner;
        var it = NewItem(ReviewOpType.Update, target, before.ContractUid);
        it.Summary = summary;
        it.ContractJson = ContractOps.ToJson(after);
        _store.SaveReviewItem(it);
        _store.WriteAudit("NotifyUpdate", MyName, "Contract", $"已修改并通知 {target}：{summary}");
        var verb = IsMine(beforeOwner) ? $"已转交并修改，已通知「{target}」知晓" : $"修改已生效，已通知「{target}」知晓";
        return (true, $"{verb}：{summary}");
    }

    /// <summary>管理员新增：立即入库；非本人名下时再通知对应销售知晓。</summary>
    public string SubmitAdd(Contract c)
    {
        var owner = c.SalesPersonName;
        if (string.IsNullOrWhiteSpace(owner)) return "请先填写「销售人员」，以确定这条记录归属谁。";
        _store.UpsertContractByUid(ContractOps.Clone(c), MyName);   // 立即入库
        if (IsMine(owner))
        {
            _store.WriteAudit("AdminAdd", MyName, "Contract", $"本人名下直接新增：{ContractOps.Describe(c)}");
            return "已直接新增（本人名下）。";
        }
        var it = NewItem(ReviewOpType.Add, owner, c.ContractUid);
        it.Summary = ContractOps.Describe(c);
        it.ContractJson = ContractOps.ToJson(c);
        _store.SaveReviewItem(it);
        _store.WriteAudit("NotifyAdd", MyName, "Contract", $"已新增并通知 {owner}：{it.Summary}");
        return $"已新增到「{owner}」名下，并通知其知晓。";
    }

    /// <summary>管理员删除：立即软删；非本人名下时再通知对应销售知晓。</summary>
    public string SubmitDelete(Contract c)
    {
        var owner = c.SalesPersonName;
        _store.SoftDeleteByUid(c.ContractUid, MyName);   // 立即软删
        if (IsMine(owner))
        {
            _store.WriteAudit("AdminDelete", MyName, "Contract", $"本人名下直接删除：{ContractOps.Describe(c)}");
            return "已直接删除（本人名下）。";
        }
        var it = NewItem(ReviewOpType.Delete, owner, c.ContractUid);
        it.Summary = "删除：" + ContractOps.Describe(c);
        it.ContractJson = ContractOps.ToJson(c);
        _store.SaveReviewItem(it);
        _store.WriteAudit("NotifyDelete", MyName, "Contract", $"已删除并通知 {owner}：{it.Summary}");
        return $"已删除「{owner}」名下记录，并通知其知晓。";
    }

    /// <summary>管理员标记（复核提醒）：本人名下无需标记，否则生成"标记"通知，销售知晓后消失。</summary>
    public string SubmitMark(Contract c, string note)
    {
        var owner = c.SalesPersonName;
        if (IsMine(owner)) return "这条是你本人名下的记录，无需标记提醒。";
        var it = NewItem(ReviewOpType.Mark, owner, c.ContractUid);
        it.Summary = string.IsNullOrWhiteSpace(note) ? "请复核这条记录。" : note.Trim();
        it.ContractJson = ContractOps.ToJson(c);   // 便于销售端定位/展示
        _store.SaveReviewItem(it);
        _store.WriteAudit("ProposeMark", MyName, "Contract", $"标记提醒 {owner}：{it.Summary}");
        return $"标记已发送「{owner}」，待其知晓后消失。";
    }

    // ————————— 销售端知晓 —————————

    /// <summary>销售端"知道了"：管理员的增删改已在其端生效并经云端合并同步到本地，这里仅记录"已知晓"
    /// ——不重复改数据，避免覆盖本人可能更晚的编辑。</summary>
    public void Confirm(ReviewItem item)
    {
        _store.SetReviewDecision(item.OpId, ReviewStatus.Acknowledged, null);
        _store.WriteAudit("Ack", MyName, "Contract", $"已知晓 {item.ByName} 的{OpText(item.OpType)}：{item.Summary}");
    }

    /// <summary>发起方撤回自己尚未被处理的提案/标记（对方待办随之消失）。</summary>
    public bool Withdraw(ReviewItem item)
    {
        var ok = _store.WithdrawReviewItem(item.OpId, MyName);
        if (ok) _store.WriteAudit("WithdrawReview", MyName, "Contract", $"撤回{OpText(item.OpType)}：{item.Summary}");
        return ok;
    }

    /// <summary>清除我发起的、<b>已结（已知晓/已驳回）</b>历史通知：仅从本地「我发起的」列表移除，
    /// 不影响已生效的改动、不动审计，且不会经同步复活（已结项不在上传包内）。待办项会被跳过。
    /// 返回实际清除条数。</summary>
    public int ClearClosedOutgoing(IEnumerable<ReviewItem> items)
    {
        var ids = items.Where(i => i.Status != ReviewStatus.Pending)
                       .Select(i => i.OpId)
                       .Where(s => !string.IsNullOrEmpty(s))
                       .Distinct()
                       .ToList();
        if (ids.Count == 0) return 0;
        var n = _store.DeleteClosedOutgoing(ids, MyName);
        if (n > 0) _store.WriteAudit("ClearReviewHistory", MyName, "Contract", $"清除已完成通知 {n} 条");
        return n;
    }

    /// <summary>审批对照：本机现有的目标合同（可能为空，如新增提案）。</summary>
    public Contract? CurrentContract(ReviewItem item) => _store.GetContractByUid(item.TargetContractUid);
    /// <summary>审批对照：提案携带的合同快照（删除/纯标记可能为空）。</summary>
    public Contract? ProposedContract(ReviewItem item) => ContractOps.FromJson(item.ContractJson);

    // ————————— 云同步桥接 —————————

    /// <summary>我发起、需上云的审批项（待办 + 已撤回，管理员上传用）。</summary>
    public ReviewBundle BuildOutgoingBundle() => new()
    {
        ByCode = MyCode, ByName = MyName, ExportedUtc = DateTime.UtcNow,
        Items = _store.GetOutgoingForUpload(MyName),
    };

    /// <summary>我作出的决策（销售上传用）。</summary>
    public DecisionBundle BuildDecisionBundle() => new()
    {
        ByCode = MyCode, ByName = MyName, ExportedUtc = DateTime.UtcNow,
        Decisions = _store.GetMyDecisions(MyName),
    };

    /// <summary>合并云端拉来的审批项与决策（只保留与我相关的：发我的 或 我发的）。</summary>
    public void Ingest(IEnumerable<ReviewItem> reviews, IEnumerable<ReviewDecision> decisions)
    {
        var mine = reviews.Where(r => IsMine(r.TargetOwnerName) || string.Equals(r.ByName?.Trim(), MyName.Trim(), StringComparison.Ordinal)).ToList();
        _store.IngestReviewItems(mine);
        _store.IngestDecisions(decisions);
    }

    public List<ReviewItem> Incoming() => _store.GetIncomingReviews(MyName, includeClosed: false);
    public List<ReviewItem> Outgoing() => _store.GetOutgoingReviews(MyName, includeClosed: true);
    public int PendingCount() => _store.CountIncomingPending(MyName);

    public static string OpText(ReviewOpType t) => t switch
    {
        ReviewOpType.Add => "新增", ReviewOpType.Update => "修改",
        ReviewOpType.Delete => "删除", ReviewOpType.Mark => "标记", _ => "操作",
    };
}
