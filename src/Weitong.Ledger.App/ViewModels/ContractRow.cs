using System.Collections;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using Weitong.Ledger.Core;

namespace Weitong.Ledger.App.ViewModels;

/// <summary>
/// 可编辑台账行（类Excel录入的一行）。包裹 Core.Contract，金额以"元"编辑、内部转分；
/// U=S−T、预计到款=月度之和 实时计算；字段级校验(INotifyDataErrorInfo → 自动红标)；
/// 每次单元格改动抛 CellChanged(旧值/新值) 供撤销重做记录。
/// </summary>
public sealed class ContractRow : INotifyPropertyChanged, INotifyDataErrorInfo
{
    private readonly Contract _c;
    public Contract Model => _c;
    public bool IsDirty { get; private set; }
    public bool IsNew { get; private set; }

    /// <summary>标记该行已落库：清脏、转为普通行（管理员本人名下直改后用）。</summary>
    public void MarkSaved() { IsDirty = false; IsNew = false; }

    /// <summary>(row, 属性名, 旧值, 新值)。撤销管理器订阅。</summary>
    public event Action<ContractRow, string, object?, object?>? CellChanged;

    public ContractRow(Contract c, bool isNew = false) { _c = c; IsNew = isNew; Validate(); }

    public static ContractRow NewBlank(string ownerName)
    {
        var c = new Contract
        {
            Stage = ContractStage.Negotiating,
            SalesPersonName = ownerName,
            OwnerCode = ownerName,
            PartyB = "中国卫通集团股份有限公司",
            Currency = "人民币",
            ContractUid = "new-" + Guid.NewGuid().ToString("N")[..12],
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow, RowVersion = 1,
        };
        return new ContractRow(c, isNew: true);
    }

    public static IReadOnlyList<string> StageOptions { get; } = Stages.All.Select(m => m.ChineseName).ToList();
    public static IReadOnlyList<string> ProductOptions { get; } =
        new[] { "带宽", "专线", "移动数据", "集成", "硬件", "软件", "其余产品" };
    public static IReadOnlyList<string> AttributeOptions { get; } =
        new[] { "新用户纯新签", "老用户纯新签", "老用户续签", "在手合同" };

    public string StageName
    {
        get => Stages.ChineseName(_c.Stage);
        set
        {
            var s = Stages.Parse(value);
            if (s.HasValue && s.Value != _c.Stage)
            {
                var old = Stages.ChineseName(_c.Stage);
                _c.Stage = s.Value; Touch(); Validate();
                CellChanged?.Invoke(this, nameof(StageName), old, value);
                Raise();
            }
        }
    }
    public string? ContractNo { get => _c.ContractNo; set => Set(v => _c.ContractNo = v, _c.ContractNo, value, validate: true); }
    public string? ProjectName { get => _c.ProjectName; set => Set(v => _c.ProjectName = v, _c.ProjectName, value, validate: true); }
    public string? PartyA { get => _c.PartyA; set => Set(v => _c.PartyA = v, _c.PartyA, value, validate: true); }
    public string SalesPersonName { get => _c.SalesPersonName; set => Set(v => { _c.SalesPersonName = v ?? ""; _c.OwnerCode = v ?? ""; }, _c.SalesPersonName, value ?? ""); }
    public string? ProductType { get => _c.ProductType; set => Set(v => _c.ProductType = v, _c.ProductType, value); }
    public string? ProjectAttribute { get => _c.ProjectAttribute; set => Set(v => _c.ProjectAttribute = v, _c.ProjectAttribute, value); }
    public string? MarketField { get => _c.MarketField; set => Set(v => _c.MarketField = v, _c.MarketField, value); }
    public string Currency { get => _c.Currency; set => Set(v => _c.Currency = v ?? "人民币", _c.Currency, value ?? "人民币"); }

    public decimal ContractAmount { get => Money.ToYuan(_c.ContractAmountCents); set => SetMoney(v => _c.ContractAmountCents = v, _c.ContractAmountCents, value, nameof(ContractAmount)); }
    public decimal Revenue { get => Money.ToYuan(_c.RevenueEstCents); set { SetMoney(v => _c.RevenueEstCents = v, _c.RevenueEstCents, value, nameof(Revenue)); Raise(nameof(Profit)); } }
    public decimal Cost { get => Money.ToYuan(_c.CostEstCents); set { SetMoney(v => _c.CostEstCents = v, _c.CostEstCents, value, nameof(Cost)); Raise(nameof(Profit)); } }
    /// <summary>U = S − T，实时计算只读。</summary>
    public decimal Profit => Money.ToYuan(_c.ProfitEstCents);

    // —— 月度到款（元），1-4月为累计桶 ——
    public decimal Pay1to4 { get => GetMonth(4, true); set => SetMonth(4, true, value, nameof(Pay1to4)); }
    public decimal Pay5 { get => GetMonth(5, false); set => SetMonth(5, false, value, nameof(Pay5)); }
    public decimal Pay6 { get => GetMonth(6, false); set => SetMonth(6, false, value, nameof(Pay6)); }
    public decimal Pay7 { get => GetMonth(7, false); set => SetMonth(7, false, value, nameof(Pay7)); }
    public decimal Pay8 { get => GetMonth(8, false); set => SetMonth(8, false, value, nameof(Pay8)); }
    public decimal Pay9 { get => GetMonth(9, false); set => SetMonth(9, false, value, nameof(Pay9)); }
    public decimal Pay10 { get => GetMonth(10, false); set => SetMonth(10, false, value, nameof(Pay10)); }
    public decimal Pay11 { get => GetMonth(11, false); set => SetMonth(11, false, value, nameof(Pay11)); }
    public decimal Pay12 { get => GetMonth(12, false); set => SetMonth(12, false, value, nameof(Pay12)); }
    /// <summary>本年预计到款 V = 月度之和，实时计算只读。</summary>
    public decimal PaymentTotal => Money.ToYuan(_c.PaymentForecastTotalCents);

    public string? SignDateText
    {
        get => _c.SignDate?.ToString("yyyy-MM-dd");
        set
        {
            var old = _c.SignDate?.ToString("yyyy-MM-dd");
            if (string.IsNullOrWhiteSpace(value)) { if (_c.SignDate != null) { _c.SignDate = null; Touch(); CellChanged?.Invoke(this, nameof(SignDateText), old, null); } SetError(nameof(SignDateText), null); Validate(); Raise(); return; }
            if (DateOnly.TryParse(value, CultureInfo.InvariantCulture, out var d))
            { if (_c.SignDate != d) { _c.SignDate = d; Touch(); CellChanged?.Invoke(this, nameof(SignDateText), old, value); } SetError(nameof(SignDateText), null); }
            else SetError(nameof(SignDateText), "日期格式应为 2026-01-31");
            Validate(); Raise();
        }
    }

    public string? CultivationSource { get => _c.CultivationSource; set => Set(v => _c.CultivationSource = v, _c.CultivationSource, value); }

    /// <summary>全字段搜索用（拼接所有可读文本 + 金额）。</summary>
    public string SearchText => string.Join(' ', new[]
    {
        StageName, ContractNo, ProjectName, PartyA, PartyB, SalesPersonName, MarketField,
        ProjectAttribute, ProductType, Currency, CultivationSource, SignDateText,
        _c.ContractAmountCents == 0 ? null : Money.ToYuan(_c.ContractAmountCents).ToString("0"),
        _c.RevenueEstCents == 0 ? null : Money.ToYuan(_c.RevenueEstCents).ToString("0"),
    }.Where(s => !string.IsNullOrEmpty(s)));

    public string PartyB => _c.PartyB;

    // —— 内部帮助 ——
    private decimal GetMonth(int month, bool cumulative)
    {
        var p = _c.Payments.FirstOrDefault(x => x.Kind == PaymentKind.Forecast && x.PeriodMonth == month && x.IsCumulative == cumulative);
        return p == null ? 0 : Money.ToYuan(p.AmountCents);
    }
    private void SetMonth(int month, bool cumulative, decimal yuan, string prop)
    {
        long cents = Money.FromYuan(yuan);
        var p = _c.Payments.FirstOrDefault(x => x.Kind == PaymentKind.Forecast && x.PeriodMonth == month && x.IsCumulative == cumulative);
        long old = p?.AmountCents ?? 0;
        if (old == cents) return;
        SetError(prop, yuan < 0 ? "金额不能为负" : null);
        if (p == null) _c.Payments.Add(new MonthlyPayment { Kind = PaymentKind.Forecast, PeriodMonth = month, IsCumulative = cumulative, AmountCents = cents });
        else p.AmountCents = cents;
        Touch();
        CellChanged?.Invoke(this, prop, Money.ToYuan(old), yuan);
        Raise(prop); Raise(nameof(PaymentTotal));
    }

    private void Set(Action<string?> apply, string? cur, string? next, bool validate = false, [CallerMemberName] string? prop = null)
    {
        if (cur == next) return;
        apply(next); Touch(); if (validate) Validate();
        CellChanged?.Invoke(this, prop!, cur, next);
        Raise(prop);
    }
    private void SetMoney(Action<long> apply, long curCents, decimal nextYuan, string prop)
    {
        long next = Money.FromYuan(nextYuan);
        if (curCents == next) return;
        SetError(prop, nextYuan < 0 ? "金额不能为负" : null);
        apply(next); Touch();
        CellChanged?.Invoke(this, prop, Money.ToYuan(curCents), nextYuan);
        Raise(prop);
    }
    private void Touch() { IsDirty = true; _c.UpdatedAt = DateTime.UtcNow; }

    private void Validate()
    {
        SetError(nameof(ProjectName),
            string.IsNullOrWhiteSpace(_c.ProjectName) && string.IsNullOrWhiteSpace(_c.PartyA) ? "项目名称与甲方不能都为空" : null);
        SetError(nameof(ContractNo),
            _c.Stage == ContractStage.Signed && string.IsNullOrWhiteSpace(_c.ContractNo) ? "已签约必须填合同编号" : null);
    }

    private readonly Dictionary<string, string> _errors = new();
    public bool HasErrors => _errors.Count > 0;
    public IEnumerable GetErrors(string? propertyName) =>
        propertyName != null && _errors.TryGetValue(propertyName, out var e) ? new[] { e } : Array.Empty<string>();
    public event EventHandler<DataErrorsChangedEventArgs>? ErrorsChanged;
    private void SetError(string prop, string? err)
    {
        bool had = _errors.ContainsKey(prop);
        if (err == null) { if (had) { _errors.Remove(prop); ErrorsChanged?.Invoke(this, new(prop)); } }
        else if (!had || _errors[prop] != err) { _errors[prop] = err; ErrorsChanged?.Invoke(this, new(prop)); }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Raise([CallerMemberName] string? n = null) => PropertyChanged?.Invoke(this, new(n));
}
