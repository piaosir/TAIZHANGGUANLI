using System.ComponentModel;
using System.Runtime.CompilerServices;
using Weitong.Ledger.Core;
using Weitong.Ledger.Data.Db;

namespace Weitong.Ledger.App.ViewModels;

/// <summary>
/// 「我的达成」个人页视图模型：只看当前使用人自己的合同，
/// 对标个人目标，复用 DashboardViewModel 的 KPI/漏斗/月度。
/// </summary>
public sealed class MyAchievementViewModel : INotifyPropertyChanged
{
    private readonly LedgerStore _store;
    private readonly int _year;

    public string Name { get; }
    public DashboardViewModel Dash { get; } = new();

    // 个人目标按「姓名」存取（随人走、可跨设备云同步）。此前按「机器码」存 → 换台电脑目标就变、且从不上云，已修。
    public MyAchievementViewModel(LedgerStore store, string name, int year)
    {
        _store = store; Name = name; _year = year;
        Load();
    }

    private decimal _rev, _profit, _cost;
    public decimal RevenueTargetWan { get => _rev; set { _rev = value; Raise(); } }
    public decimal ProfitTargetWan { get => _profit; set { _profit = value; Raise(); } }
    public decimal CostTargetWan { get => _cost; set { _cost = value; Raise(); } }

    public string Summary { get; private set; } = "";
    public string Title => $"我的达成 · {Name}";

    /// <summary>个人目标保存后触发（外壳据此即时把本人目标随包上云，跨设备"随人走"）。</summary>
    public event Action? TargetSaved;

    public void Load()
    {
        var contracts = _store.GetContractsFor(Name);
        var t = _store.GetPersonTarget(Name, _year);
        _rev = t != null ? Money.ToWan(t.RevenueTargetCents) : 0;
        _profit = t != null ? Money.ToWan(t.ProfitTargetCents) : 0;
        _cost = t != null ? Money.ToWan(t.CostCeilingCents) : 0;

        Dash.SetTarget(Money.FromWan((double)_rev), Money.FromWan((double)_profit), Money.FromWan((double)_cost));
        Dash.Load(contracts, Title);

        Summary = t == null
            ? $"我的合同 {contracts.Count} 条 · {_year} 年 · 尚未设置个人目标，填写下方目标即可看到完成率与差距"
            : $"我的合同 {contracts.Count} 条 · {_year} 年";
        Raise(nameof(RevenueTargetWan)); Raise(nameof(ProfitTargetWan)); Raise(nameof(CostTargetWan));
        Raise(nameof(Summary)); Raise(nameof(Title));
    }

    public void SaveTarget()
    {
        _store.SavePersonTarget(Name, _year,
            Money.FromWan((double)_rev), Money.FromWan((double)_profit), Money.FromWan((double)_cost), Name);
        _store.WriteAudit("Target", Name, "Target", $"设置个人目标 收入{_rev}万/利润{_profit}万/成本上限{_cost}万");
        Load();
        TargetSaved?.Invoke();
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Raise([CallerMemberName] string? n = null) => PropertyChanged?.Invoke(this, new(n));
}
