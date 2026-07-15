using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Weitong.Ledger.App.Services;
using Weitong.Ledger.Core;

namespace Weitong.Ledger.App.ViewModels;

/// <summary>「通知」页：销售看到管理员对自己名下数据的增删改标记（改动已生效，只需"知道了"）；
/// 管理员另可看到自己发出的通知是否已被对方知晓。</summary>
public sealed class PendingReviewViewModel : INotifyPropertyChanged
{
    private readonly ReviewService _review;

    public ObservableCollection<ReviewItemVm> Incoming { get; } = new();
    public ObservableCollection<ReviewItemVm> Outgoing { get; } = new();

    public bool IsAdmin => _review.IsAdmin;

    /// <summary>处理完一条（确认/驳回）后回调，供刷新红点并触发上云。</summary>
    public event Action? Changed;

    public PendingReviewViewModel(ReviewService review) => _review = review;

    public void Load() => Apply(_review.Incoming(), _review.Outgoing());

    /// <summary>用预取的列表填充（读库可放后台线程完成，这里只在 UI 线程填集合）。</summary>
    public void Apply(IReadOnlyList<ReviewItem> incoming, IReadOnlyList<ReviewItem> outgoing)
    {
        Incoming.Clear();
        foreach (var it in incoming) Incoming.Add(new ReviewItemVm(it));
        Outgoing.Clear();
        foreach (var it in outgoing) Outgoing.Add(new ReviewItemVm(it));
        Raise(nameof(IncomingCount)); Raise(nameof(HasIncoming)); Raise(nameof(NoIncoming));
        Raise(nameof(HasOutgoing)); Raise(nameof(NoOutgoing)); Raise(nameof(IncomingHeader));
    }

    public int IncomingCount => Incoming.Count;
    public bool HasIncoming => Incoming.Count > 0;
    public bool NoIncoming => Incoming.Count == 0;
    public bool HasOutgoing => Outgoing.Count > 0;
    public bool NoOutgoing => Outgoing.Count == 0;
    public string IncomingHeader => $"待我知晓（{Incoming.Count}）";

    public void Confirm(ReviewItemVm vm)
    {
        _review.Confirm(vm.Model);
        Load();
        Changed?.Invoke();
    }

    /// <summary>撤回我发起的通知/标记（对方知晓前）。返回是否成功。</summary>
    public bool Withdraw(ReviewItemVm vm)
    {
        var ok = _review.Withdraw(vm.Model);
        if (ok) { Load(); Changed?.Invoke(); }
        return ok;
    }

    // ————————————————— 批量处理（勾选后一次性确认/驳回/撤回；只 Load 一次，避免逐条重建集合） —————————————————

    /// <summary>批量知晓（全部标为"已知晓"）。返回处理条数。</summary>
    public int ConfirmMany(IReadOnlyList<ReviewItemVm> items)
    {
        if (items.Count == 0) return 0;
        foreach (var vm in items) _review.Confirm(vm.Model);
        Load(); Changed?.Invoke();
        return items.Count;
    }

    /// <summary>批量撤回（仅仍待对方知晓的可撤回，其余跳过）。返回（撤回数, 跳过数）。</summary>
    public (int withdrawn, int skipped) WithdrawMany(IReadOnlyList<ReviewItemVm> items)
    {
        var targets = items.Where(v => v.CanWithdraw).ToList();
        int ok = 0;
        foreach (var vm in targets) if (_review.Withdraw(vm.Model)) ok++;
        if (ok > 0) { Load(); Changed?.Invoke(); }
        return (ok, items.Count - ok);
    }

    /// <summary>清除「我发起的」里选中的通知（无需等对方知晓）：尚待对方知晓的仅本地隐藏（对方仍能看到并知晓），
    /// 已结的删历史。均不影响已生效改动，不会经同步复活。返回清除条数。</summary>
    public int Clear(IReadOnlyList<ReviewItemVm> items)
    {
        var targets = items.Where(v => v.CanClear).ToList();
        if (targets.Count == 0) return 0;
        var n = _review.ClearOutgoing(targets.Select(v => v.Model));
        if (n > 0) { Load(); Changed?.Invoke(); }
        return n;
    }

    /// <summary>构建内容详情，供"查看"弹窗展示。管理员的增删改已生效，这里展示该记录的当前内容 +
    /// 一句话说明管理员做了什么（改动明细见 <see cref="ReviewItem.Summary"/>：字段 旧值→新值）。</summary>
    public ReviewDetail BuildDetail(ReviewItemVm vm, bool isIncoming)
    {
        var item = vm.Model;
        // 改动已生效→本地现值即改后值；删除项本地可能已成墓碑（查不到），回退到通知携带的快照。
        var basis = _review.CurrentContract(item) ?? _review.ProposedContract(item);
        var change = string.IsNullOrWhiteSpace(item.Summary) ? "" : item.Summary!;
        var d = new ReviewDetail
        {
            Title = ReviewService.OpText(item.OpType) + " · 内容详情",
            Subtitle = basis == null ? "" :
                (string.IsNullOrWhiteSpace(basis.ProjectName) ? (basis.PartyA ?? "") : basis.ProjectName)
                + (string.IsNullOrWhiteSpace(basis.ContractNo) ? "" : $" · {basis.ContractNo}"),
            IsIncoming = isIncoming,
            IsMark = vm.IsMark,
            ConfirmText = vm.ConfirmText,
            CanReject = vm.CanReject,   // 现恒为 false：改动已生效，无需驳回
            ShowProposed = false,        // 不再做"现值 vs 提案值"对照（改动已生效，两列相同无意义）
            Banner = item.OpType switch
            {
                ReviewOpType.Add    => "管理员已新增此记录到你名下（已生效）。点『知道了』消除此通知。",
                ReviewOpType.Update => $"管理员已修改此记录（已生效）：{change}。点『知道了』消除此通知。",
                ReviewOpType.Delete => "管理员已删除此记录（软删除，可在审计追溯）。点『知道了』消除此通知。",
                ReviewOpType.Mark   => "复核提醒：" + change,
                _ => change,
            },
        };
        if (basis != null)
            foreach (var (lbl, val) in ContractOps.Fields(basis))
                d.Rows.Add(new DetailRow(lbl, val, "", false));
        return d;
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Raise([CallerMemberName] string? n = null) => PropertyChanged?.Invoke(this, new(n));
}

/// <summary>审批项的展示包装（避免转换器，直接暴露中文串）。</summary>
public sealed class ReviewItemVm : INotifyPropertyChanged
{
    public ReviewItem Model { get; }
    public ReviewItemVm(ReviewItem m) => Model = m;

    /// <summary>批量处理时是否勾选。</summary>
    private bool _selected;
    public bool IsSelected
    {
        get => _selected;
        set { if (_selected == value) return; _selected = value; PropertyChanged?.Invoke(this, new(nameof(IsSelected))); }
    }

    public string OpText => ReviewService.OpText(Model.OpType);
    public bool IsMark => Model.OpType == ReviewOpType.Mark;
    public string Summary => Model.Summary ?? "";
    public string ByName => Model.ByName;
    public string TargetOwnerName => Model.TargetOwnerName;
    public string ConfirmText => "知道了";
    /// <summary>改动已生效，不再提供驳回（恒为 false）。</summary>
    public bool CanReject => false;
    /// <summary>发起方可撤回：仍待对方知晓时（撤回会把对方那条一并移除）。</summary>
    public bool CanWithdraw => Model.Status == ReviewStatus.Pending;
    /// <summary>任何在列表里显示的通知都可清除：待对方知晓的仅本地隐藏（对方不受影响），已结的删历史。</summary>
    public bool CanClear => true;

    public string CreatedText => Model.CreatedUtc.ToLocalTime().ToString("MM-dd HH:mm");

    /// <summary>对方处理时填写的留言（目前仅驳回会填）。</summary>
    public string DecideNote => Model.DecideNote ?? "";
    /// <summary>是否为"已驳回且带留言"——发起方需在「我发起的」里看到驳回理由。</summary>
    public bool HasRejectNote => Model.Status == ReviewStatus.Rejected && !string.IsNullOrWhiteSpace(Model.DecideNote);

    public string StatusText => Model.Status switch
    {
        ReviewStatus.Pending => "待知晓",
        ReviewStatus.Confirmed => "已知晓",
        ReviewStatus.Rejected => "已驳回",
        ReviewStatus.Acknowledged => "已知晓",
        ReviewStatus.Withdrawn => "已撤回",
        _ => "",
    };

    /// <summary>发起方视角：一句话概述 "对 张三 的 修改 · 状态"。</summary>
    public string OutgoingLine => $"对「{TargetOwnerName}」的{OpText} · {StatusText}";

    public event PropertyChangedEventHandler? PropertyChanged;
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
    public string ConfirmText { get; set; } = "知道了";
    public bool CanReject { get; set; } = false;
}
