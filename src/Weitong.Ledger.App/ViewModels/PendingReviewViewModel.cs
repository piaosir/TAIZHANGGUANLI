using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Weitong.Ledger.App.Services;
using Weitong.Ledger.Core;

namespace Weitong.Ledger.App.ViewModels;

/// <summary>「待确认」页：销售看到管理员发来的待办（增删改需确认/驳回，标记需知晓）；
/// 管理员另可看到自己发起的提案进度。</summary>
public sealed class PendingReviewViewModel : INotifyPropertyChanged
{
    private readonly ReviewService _review;

    public ObservableCollection<ReviewItemVm> Incoming { get; } = new();
    public ObservableCollection<ReviewItemVm> Outgoing { get; } = new();

    public bool IsAdmin => _review.IsAdmin;

    /// <summary>处理完一条（确认/驳回）后回调，供刷新红点并触发上云。</summary>
    public event Action? Changed;

    public PendingReviewViewModel(ReviewService review) => _review = review;

    public void Load()
    {
        Incoming.Clear();
        foreach (var it in _review.Incoming()) Incoming.Add(new ReviewItemVm(it));
        Outgoing.Clear();
        foreach (var it in _review.Outgoing()) Outgoing.Add(new ReviewItemVm(it));
        Raise(nameof(IncomingCount)); Raise(nameof(HasIncoming)); Raise(nameof(NoIncoming));
        Raise(nameof(HasOutgoing)); Raise(nameof(NoOutgoing)); Raise(nameof(IncomingHeader));
    }

    public int IncomingCount => Incoming.Count;
    public bool HasIncoming => Incoming.Count > 0;
    public bool NoIncoming => Incoming.Count == 0;
    public bool HasOutgoing => Outgoing.Count > 0;
    public bool NoOutgoing => Outgoing.Count == 0;
    public string IncomingHeader => $"待我确认（{Incoming.Count}）";

    public void Confirm(ReviewItemVm vm)
    {
        _review.Confirm(vm.Model);
        Load();
        Changed?.Invoke();
    }

    public void Reject(ReviewItemVm vm, string? note)
    {
        _review.Reject(vm.Model, note);
        Load();
        Changed?.Invoke();
    }

    /// <summary>撤回我发起的提案/标记（对方处理前）。返回是否成功。</summary>
    public bool Withdraw(ReviewItemVm vm)
    {
        var ok = _review.Withdraw(vm.Model);
        if (ok) { Load(); Changed?.Invoke(); }
        return ok;
    }

    /// <summary>构建审批对照（现值 vs 提案值），供"查看"弹窗展示。</summary>
    public ReviewDetail BuildDetail(ReviewItemVm vm, bool isIncoming)
    {
        var item = vm.Model;
        var current = _review.CurrentContract(item);
        var proposed = _review.ProposedContract(item);
        var basis = proposed ?? current;
        var d = new ReviewDetail
        {
            Title = ReviewService.OpText(item.OpType) + " · 内容对照",
            Subtitle = basis == null ? "" :
                (string.IsNullOrWhiteSpace(basis.ProjectName) ? (basis.PartyA ?? "") : basis.ProjectName)
                + (string.IsNullOrWhiteSpace(basis.ContractNo) ? "" : $" · {basis.ContractNo}"),
            IsIncoming = isIncoming,
            IsMark = vm.IsMark,
            ConfirmText = vm.ConfirmText,
            CanReject = vm.CanReject,
        };

        var curF = current != null ? ContractOps.Fields(current).ToDictionary(x => x.Label, x => x.Value) : null;
        var propF = proposed != null ? ContractOps.Fields(proposed).ToDictionary(x => x.Label, x => x.Value) : null;
        var labelSource = proposed ?? current;

        switch (item.OpType)
        {
            case ReviewOpType.Update:
            case ReviewOpType.Add:
                d.ShowProposed = true;
                if (labelSource != null)
                    foreach (var (lbl, _) in ContractOps.Fields(labelSource))
                    {
                        var cv = curF != null && curF.TryGetValue(lbl, out var a) ? a : "—";
                        var pv = propF != null && propF.TryGetValue(lbl, out var b) ? b : "—";
                        d.Rows.Add(new DetailRow(lbl, cv, pv, cv != pv));
                    }
                if (item.OpType == ReviewOpType.Add) d.Banner = "确认后将新增此记录到你名下。";
                else d.Banner = "标红为管理员改动的字段。确认后按「提案值」生效。";
                break;
            case ReviewOpType.Delete:
                d.ShowProposed = false;
                d.Banner = "确认后将删除此记录（软删除，可在审计追溯）。";
                if (basis != null) foreach (var (lbl, val) in ContractOps.Fields(basis)) d.Rows.Add(new DetailRow(lbl, val, "", false));
                break;
            case ReviewOpType.Mark:
                d.ShowProposed = false;
                d.Banner = "复核提醒：" + (item.Summary ?? "");
                if (basis != null) foreach (var (lbl, val) in ContractOps.Fields(basis)) d.Rows.Add(new DetailRow(lbl, val, "", false));
                break;
        }
        return d;
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Raise([CallerMemberName] string? n = null) => PropertyChanged?.Invoke(this, new(n));
}

/// <summary>审批项的展示包装（避免转换器，直接暴露中文串）。</summary>
public sealed class ReviewItemVm
{
    public ReviewItem Model { get; }
    public ReviewItemVm(ReviewItem m) => Model = m;

    public string OpText => ReviewService.OpText(Model.OpType);
    public bool IsMark => Model.OpType == ReviewOpType.Mark;
    public string Summary => Model.Summary ?? "";
    public string ByName => Model.ByName;
    public string TargetOwnerName => Model.TargetOwnerName;
    public string ConfirmText => IsMark ? "知道了" : "确认执行";
    public bool CanReject => !IsMark;
    /// <summary>发起方可撤回：仍待对方处理（待确认）时。</summary>
    public bool CanWithdraw => Model.Status == ReviewStatus.Pending;

    public string CreatedText => Model.CreatedUtc.ToLocalTime().ToString("MM-dd HH:mm");

    public string StatusText => Model.Status switch
    {
        ReviewStatus.Pending => "待确认",
        ReviewStatus.Confirmed => "已确认",
        ReviewStatus.Rejected => "已驳回",
        ReviewStatus.Acknowledged => "已知晓",
        ReviewStatus.Withdrawn => "已撤回",
        _ => "",
    };

    /// <summary>发起方视角：一句话概述 "对 张三 的 修改 · 状态"。</summary>
    public string OutgoingLine => $"对「{TargetOwnerName}」的{OpText} · {StatusText}";
}

/// <summary>审批对照的一行：字段 + 现值 + 提案值 + 是否变化。</summary>
public sealed record DetailRow(string Field, string Current, string Proposed, bool Changed);

/// <summary>审批对照数据（供"查看"弹窗展示现值 vs 提案值）。</summary>
public sealed class ReviewDetail
{
    public string Title { get; set; } = "";
    public string Subtitle { get; set; } = "";
    public string? Banner { get; set; }
    public bool ShowProposed { get; set; } = true;
    public List<DetailRow> Rows { get; } = new();
    public bool IsIncoming { get; set; }
    public bool IsMark { get; set; }
    public string ConfirmText { get; set; } = "确认执行";
    public bool CanReject { get; set; } = true;
}
