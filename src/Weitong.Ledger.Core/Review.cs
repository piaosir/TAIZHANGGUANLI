namespace Weitong.Ledger.Core;

/// <summary>管理员发起的操作类型。查(读)不产生审批项。</summary>
public enum ReviewOpType
{
    /// <summary>新增一条记录（增）。</summary>
    Add = 0,
    /// <summary>修改一条记录（改）。</summary>
    Update = 1,
    /// <summary>删除一条记录（删）。</summary>
    Delete = 2,
    /// <summary>标记（复核提醒，不改数据，销售确认后消失）。</summary>
    Mark = 3,
}

/// <summary>审批项状态。</summary>
public enum ReviewStatus
{
    /// <summary>待对应销售确认。</summary>
    Pending = 0,
    /// <summary>销售已确认（增删改已执行）。</summary>
    Confirmed = 1,
    /// <summary>销售已驳回（不执行）。</summary>
    Rejected = 2,
    /// <summary>标记已被销售知晓（随即消失）。</summary>
    Acknowledged = 3,
    /// <summary>发起方（管理员）在销售处理前撤回，随即消失。</summary>
    Withdrawn = 4,
}

/// <summary>
/// 审批项：管理员对<b>他人名下</b>记录发起的一次操作（增/删/改/标记）。
/// 通过云端在管理员与对应销售之间传递：管理员上传自己发起的全部审批项，
/// 销售拉取属于自己的项，逐条确认或驳回；确认后本地执行数据变更，
/// 决策再通过决策通道回流，管理员即可看到结果。以 <see cref="OpId"/> 跨机去重。
/// </summary>
public sealed class ReviewItem
{
    /// <summary>本机数据库主键。</summary>
    public long Id { get; set; }

    /// <summary>全局唯一操作号（GUID），跨机去重与决策关联的锚点。</summary>
    public string OpId { get; set; } = "";

    public ReviewOpType OpType { get; set; }
    public ReviewStatus Status { get; set; } = ReviewStatus.Pending;

    /// <summary>目标合同的全局唯一键。</summary>
    public string TargetContractUid { get; set; } = "";
    /// <summary>路由键：对应销售的姓名（= 合同 SalesPersonName / OwnerCode）。</summary>
    public string TargetOwnerName { get; set; } = "";

    /// <summary>发起管理员姓名。</summary>
    public string ByName { get; set; } = "";
    /// <summary>发起管理员机器码（仅用于云端文件命名，避免多管理员互相覆盖）。</summary>
    public string ByCode { get; set; } = "";

    public DateTime CreatedUtc { get; set; }
    public DateTime? DecidedUtc { get; set; }
    /// <summary>销售驳回/确认时可附带的说明。</summary>
    public string? DecideNote { get; set; }

    /// <summary>供人阅读的变更摘要（改：字段 旧值→新值；标记：备注原文）。</summary>
    public string? Summary { get; set; }

    /// <summary>提案的完整合同快照(JSON)，供销售端确认后落库；删除/纯标记可为空。</summary>
    public string? ContractJson { get; set; }

    /// <summary>发起方（管理员）已把本条从自己的「我发起的」列表清除：<b>仅本地视图隐藏</b>，
    /// 不影响对方的通知与知晓、不改已生效数据。云端合并（Ingest）不覆盖此标记，故对方仍能看到并知晓。</summary>
    public bool ClearedByOwner { get; set; }

    /// <summary>是否已结（非待办）。</summary>
    public bool IsClosed => Status != ReviewStatus.Pending;
}

/// <summary>
/// 销售对某审批项的决策，经决策通道回流给发起管理员。以 OpId 关联。
/// </summary>
public sealed record ReviewDecision(string OpId, ReviewStatus Status, DateTime DecidedUtc, string? Note);
