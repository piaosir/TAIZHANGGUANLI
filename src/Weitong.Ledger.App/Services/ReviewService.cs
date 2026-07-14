using Weitong.Ledger.Core;
using Weitong.Ledger.Data.Db;

namespace Weitong.Ledger.App.Services;

/// <summary>
/// 审批业务：管理员对全组数据发起增删改标记；对<b>他人名下</b>的改动生成提案，
/// 路由到对应销售确认后才落库；对<b>本人名下</b>的改动直接生效。
/// 销售端确认/驳回，决策回流给发起管理员。所有传递经 <see cref="CloudSync"/> 云通道。
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
    /// 管理员修改：仅当改动前后都在<b>本人名下</b>才直接生效；否则生成"改"提案交对应销售确认。
    /// 把本人名下记录转交他人 → 发给<b>接收人</b>确认；改他人名下记录 → 发给<b>当前归属人</b>确认。
    /// 返回 (是否已直接生效, 给用户看的说明)。说明为空表示无实际变化。
    /// </summary>
    public (bool Applied, string Message) SubmitUpdate(Contract before, Contract after)
    {
        var summary = ContractOps.Summarize(before, after);
        if (string.IsNullOrEmpty(summary)) return (false, "");   // 无实际变化
        var beforeOwner = before.SalesPersonName;
        var afterOwner = after.SalesPersonName;

        if (IsMine(beforeOwner) && IsMine(afterOwner))
        {
            _store.UpsertContractByUid(ContractOps.Clone(after), MyName);
            _store.WriteAudit("AdminEdit", MyName, "Contract", $"本人名下直接修改：{summary}");
            return (true, $"已直接生效（本人名下）：{summary}");
        }

        // 需对应销售确认：转交他人→发接收人；改他人名下→发当前归属人
        var target = IsMine(beforeOwner) ? afterOwner : beforeOwner;
        var it = NewItem(ReviewOpType.Update, target, before.ContractUid);
        it.Summary = summary;
        it.ContractJson = ContractOps.ToJson(after);
        _store.SaveReviewItem(it);
        _store.WriteAudit("ProposeUpdate", MyName, "Contract", $"提交 {target} 确认：{summary}");
        var verb = IsMine(beforeOwner) ? $"转交并修改，已提交「{target}」确认" : $"修改已提交「{target}」确认";
        return (false, $"{verb}：{summary}");
    }

    /// <summary>管理员新增：本人名下直接入库，否则生成"增"提案。</summary>
    public string SubmitAdd(Contract c)
    {
        var owner = c.SalesPersonName;
        if (string.IsNullOrWhiteSpace(owner)) return "请先填写「销售人员」，以确定这条记录归属谁。";
        if (IsMine(owner))
        {
            _store.UpsertContractByUid(ContractOps.Clone(c), MyName);
            _store.WriteAudit("AdminAdd", MyName, "Contract", $"本人名下直接新增：{ContractOps.Describe(c)}");
            return "已直接新增（本人名下）。";
        }
        var it = NewItem(ReviewOpType.Add, owner, c.ContractUid);
        it.Summary = ContractOps.Describe(c);
        it.ContractJson = ContractOps.ToJson(c);
        _store.SaveReviewItem(it);
        _store.WriteAudit("ProposeAdd", MyName, "Contract", $"提交 {owner} 确认新增：{it.Summary}");
        return $"新增已提交「{owner}」确认。";
    }

    /// <summary>管理员删除：本人名下直接软删，否则生成"删"提案。</summary>
    public string SubmitDelete(Contract c)
    {
        var owner = c.SalesPersonName;
        if (IsMine(owner))
        {
            _store.SoftDeleteByUid(c.ContractUid, MyName);
            _store.WriteAudit("AdminDelete", MyName, "Contract", $"本人名下直接删除：{ContractOps.Describe(c)}");
            return "已直接删除（本人名下）。";
        }
        var it = NewItem(ReviewOpType.Delete, owner, c.ContractUid);
        it.Summary = "删除：" + ContractOps.Describe(c);
        it.ContractJson = ContractOps.ToJson(c);
        _store.SaveReviewItem(it);
        _store.WriteAudit("ProposeDelete", MyName, "Contract", $"提交 {owner} 确认删除：{it.Summary}");
        return $"删除已提交「{owner}」确认。";
    }

    /// <summary>管理员标记（复核提醒）：本人名下无需标记，否则生成"标记"提案，销售确认后消失。</summary>
    public string SubmitMark(Contract c, string note)
    {
        var owner = c.SalesPersonName;
        if (IsMine(owner)) return "这条是你本人名下的记录，无需标记提醒。";
        var it = NewItem(ReviewOpType.Mark, owner, c.ContractUid);
        it.Summary = string.IsNullOrWhiteSpace(note) ? "请复核这条记录。" : note.Trim();
        it.ContractJson = ContractOps.ToJson(c);   // 便于销售端定位/展示
        _store.SaveReviewItem(it);
        _store.WriteAudit("ProposeMark", MyName, "Contract", $"标记提醒 {owner}：{it.Summary}");
        return $"标记已发送「{owner}」，待其确认后消失。";
    }

    // ————————— 销售端决策 —————————

    /// <summary>确认：增/改落库、删软删、标记则知晓。</summary>
    public void Confirm(ReviewItem item)
    {
        switch (item.OpType)
        {
            case ReviewOpType.Add:
            case ReviewOpType.Update:
                var c = ContractOps.FromJson(item.ContractJson);
                if (c != null) _store.UpsertContractByUid(c, MyName);
                _store.SetReviewDecision(item.OpId, ReviewStatus.Confirmed, null);
                _store.WriteAudit("ConfirmChange", MyName, "Contract", $"确认 {item.ByName} 的{OpText(item.OpType)}：{item.Summary}");
                break;
            case ReviewOpType.Delete:
                _store.SoftDeleteByUid(item.TargetContractUid, MyName);
                _store.SetReviewDecision(item.OpId, ReviewStatus.Confirmed, null);
                _store.WriteAudit("ConfirmDelete", MyName, "Contract", $"确认 {item.ByName} 的删除：{item.Summary}");
                break;
            case ReviewOpType.Mark:
                _store.SetReviewDecision(item.OpId, ReviewStatus.Acknowledged, null);
                _store.WriteAudit("AckMark", MyName, "Contract", $"已知晓 {item.ByName} 的标记：{item.Summary}");
                break;
        }
    }

    /// <summary>驳回（增删改用）。标记不涉及驳回，一律"知道了"。</summary>
    public void Reject(ReviewItem item, string? note)
    {
        _store.SetReviewDecision(item.OpId, ReviewStatus.Rejected, note);
        _store.WriteAudit("RejectChange", MyName, "Contract", $"驳回 {item.ByName} 的{OpText(item.OpType)}：{note}");
    }

    /// <summary>发起方撤回自己尚未被处理的提案/标记（对方待办随之消失）。</summary>
    public bool Withdraw(ReviewItem item)
    {
        var ok = _store.WithdrawReviewItem(item.OpId, MyName);
        if (ok) _store.WriteAudit("WithdrawReview", MyName, "Contract", $"撤回{OpText(item.OpType)}：{item.Summary}");
        return ok;
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
