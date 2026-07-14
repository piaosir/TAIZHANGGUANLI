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
    private readonly string _personCode;
    private readonly int _year;

    public string Name { get; }
    public DashboardViewModel Dash { get; } = new();

    public MyAchievementViewModel(LedgerStore store, string personCode, string name, int year)
    {
        _store = store; _personCode = personCode; Name = name; _year = year;
        Load();
    }

    private decimal _rev, _profit, _cost;
    public decimal RevenueTargetWan { get => _rev; set { _rev = value; Raise(); } }
    public decimal ProfitTargetWan { get => _profit; set { _profit = value; Raise(); } }
    public decimal CostTargetWan { get => _cost; set { _cost = value; Raise(); } }

    public string Summary { get; private set; } = "";
    public string Title => $"我的达成 · {Name}";

    public void Load()
    {
        var contracts = _store.GetContractsFor(Name);
        var t = _store.GetPersonTarget(_personCode, _year);
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
        _store.SavePersonTarget(_personCode, _year,
            Money.FromWan((double)_rev), Money.FromWan((double)_profit), Money.FromWan((double)_cost), Name);
        _store.WriteAudit("Target", Name, "Target", $"设置个人目标 收入{_rev}万/利润{_profit}万/成本上限{_cost}万");
        Load();
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Raise([CallerMemberName] string? n = null) => PropertyChanged?.Invoke(this, new(n));
}
