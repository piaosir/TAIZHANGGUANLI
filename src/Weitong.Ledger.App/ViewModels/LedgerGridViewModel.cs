using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Data;
using Weitong.Ledger.App.Services;
using Weitong.Ledger.Core;
using Weitong.Ledger.Data.Db;
using Weitong.Ledger.Data.Export;

namespace Weitong.Ledger.App.ViewModels;

/// <summary>
/// 台账网格视图模型。三种用法：
/// · 个人录入(personalOnly=true)：只我的记录，可编辑，改完自动保存；
/// · 台账明细·浏览(personalOnly=false, review=null)：全组总库，只读；
/// · 台账明细·管理(personalOnly=false, review!=null 且为管理员)：全组总库可编辑，
///   改本人名下直接生效，改他人名下生成提案交对应销售确认。
/// </summary>
public sealed class LedgerGridViewModel : INotifyPropertyChanged
{
    private readonly LedgerStore _store;
    private readonly string _currentPerson;
    private readonly bool _personalOnly;
    private readonly ReviewService? _review;

    public ObservableCollection<ContractRow> Rows { get; } = new();
    public ICollectionView View { get; }
    public bool PersonalOnly => _personalOnly;

    /// <summary>台账明细·管理模式：管理员在全组总库上可编辑并发起提案。</summary>
    public bool IsAdminReview => _review is { IsAdmin: true } && !_personalOnly;
    /// <summary>网格是否只读（非管理员浏览时只读）。</summary>
    public bool IsReadOnlyGrid => !IsAdminReview;

    /// <summary>管理员操作后给用户看的提示（如"已提交 张三 确认"）。</summary>
    public event Action<string>? ActionMessage;
    /// <summary>本机数据或提案发生变化，需上云（管理员直改/提案后触发）。</summary>
    public event Action? NeedsSync;

    /// <summary>他人名下原始快照（管理员改动 → 生成提案后回滚展示用）。按 ContractUid。</summary>
    private readonly Dictionary<string, Contract> _snapshot = new();

    private string _filter = "";
    public string Filter { get => _filter; set { _filter = value; Raise(); View.Refresh(); Raise(nameof(StatusText)); } }

    public LedgerGridViewModel(LedgerStore store, string currentPerson, bool personalOnly)
        : this(store, currentPerson, personalOnly, null) { }

    public LedgerGridViewModel(LedgerStore store, string currentPerson, bool personalOnly, ReviewService? review)
    {
        _store = store;
        _currentPerson = currentPerson;
        _personalOnly = personalOnly;
        _review = review;
        View = CollectionViewSource.GetDefaultView(Rows);
        View.Filter = o => Match((ContractRow)o);
    }

    private bool Match(ContractRow r)
    {
        if (_personalOnly && r.SalesPersonName != _currentPerson) return false;
        if (string.IsNullOrWhiteSpace(_filter)) return true;
        return r.SearchText.Contains(_filter.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    private ContractRow Wrap(ContractRow r) { r.CellChanged += OnCell; return r; }

    /// <summary>从本地库加载（个人=我的，否则=全部）。</summary>
    public void Reload()
    {
        var list = _personalOnly ? _store.GetContractsFor(_currentPerson) : _store.GetAllContracts();
        LoadFrom(list);
    }

    /// <summary>用给定合同集合填充（台账明细可传入云端合并后的总库）。</summary>
    public void LoadFrom(IEnumerable<Contract> contracts)
    {
        foreach (var r in Rows) r.CellChanged -= OnCell;
        Rows.Clear();
        _undo.Clear(); _redo.Clear();
        _snapshot.Clear();
        foreach (var c in contracts.OrderBy(c => c.SalesPersonName).ThenByDescending(c => c.RevenueEstCents))
        {
            Rows.Add(Wrap(new ContractRow(c)));
            if (IsAdminReview) _snapshot[c.ContractUid] = ContractOps.Clone(c);
        }
        RaiseUndo();
        Raise(nameof(StatusText));
    }

    public ContractRow AddRow()
    {
        var r = Wrap(ContractRow.NewBlank(_currentPerson));
        Rows.Insert(0, r);
        Raise(nameof(StatusText));
        return r;
    }

    public ContractRow AppendBlank()
    {
        var r = Wrap(ContractRow.NewBlank(_currentPerson));
        Rows.Add(r);
        return r;
    }

    /// <summary>在 anchor 行上/下方插入一张空白行（右键「插入行」用）。</summary>
    public ContractRow InsertRelative(ContractRow anchor, bool below)
    {
        int idx = Rows.IndexOf(anchor);
        int at = idx < 0 ? (below ? Rows.Count : 0) : (below ? idx + 1 : idx);
        var r = Wrap(ContractRow.NewBlank(_currentPerson));
        Rows.Insert(Math.Clamp(at, 0, Rows.Count), r);
        Raise(nameof(StatusText));
        return r;
    }

    /// <summary>批量保存受影响的行（粘贴/填充/清除/公式后调用，修正程序化改动不落库的问题）。</summary>
    public void SaveRows(IEnumerable<ContractRow> rows)
    {
        foreach (var r in rows.Distinct()) SaveRow(r);
    }

    public void Delete(IEnumerable<ContractRow> rows)
    {
        if (IsAdminReview) { DeleteAdmin(rows); return; }
        var list = rows.ToList();
        var ids = list.Where(r => !r.IsNew && r.Model.Id > 0).Select(r => r.Model.Id).ToList();
        if (ids.Count > 0) _store.SoftDeleteContracts(ids, _currentPerson);
        foreach (var r in list) { r.CellChanged -= OnCell; Rows.Remove(r); }
        Raise(nameof(StatusText));
    }

    /// <summary>自动保存单行。个人录入=直接入库；台账明细·管理=按归属决定直改或提案。</summary>
    public void SaveRow(ContractRow r)
    {
        if (IsAdminReview) { SaveRowAdmin(r); return; }
        if (!r.IsDirty && !r.IsNew) return;
        _store.UpsertContracts(new[] { r.Model }, _currentPerson);
        Raise(nameof(StatusText));
    }

    // ————————— 台账明细·管理（管理员）—————————

    private void SaveRowAdmin(ContractRow r)
    {
        if (_review == null) return;
        if (r.IsNew)
        {
            if (!r.IsDirty) return;               // 空白新行未填，忽略
            var owner = (r.Model.SalesPersonName ?? "").Trim();
            if (owner.Length == 0) { Notify("请先填写「销售人员」，以确定这条记录归属谁。"); return; }
            var mine = _review.IsMine(owner);
            var msg = _review.SubmitAdd(r.Model);
            if (mine) { r.MarkSaved(); _snapshot[r.Model.ContractUid] = ContractOps.Clone(r.Model); } // 本人名下已入库→登记快照，供后续再编辑
            else { r.CellChanged -= OnCell; Rows.Remove(r); }   // 提案：确认前不进总库
            Notify(msg);
            NeedsSync?.Invoke();
            Raise(nameof(StatusText));
            return;
        }

        if (!r.IsDirty) return;
        var uid = r.Model.ContractUid;
        if (!_snapshot.TryGetValue(uid, out var before)) { before = ContractOps.Clone(r.Model); _snapshot[uid] = before; }

        var (applied, msg2) = _review.SubmitUpdate(before, r.Model);
        if (applied)
        {
            _snapshot[uid] = ContractOps.Clone(r.Model);   // 直接生效→新的权威值
            r.MarkSaved();
            Notify(msg2); NeedsSync?.Invoke();
        }
        else if (msg2.Length > 0)
        {
            // 生成提案（含把本人名下转交他人）：回滚为权威值，总库始终显示已确认数据
            ReplaceRow(r, ContractOps.Clone(before));
            Notify(msg2); NeedsSync?.Invoke();
        }
        // else：无实际变化，不处理
    }

    /// <summary>路由基准：优先用权威快照（避免"改了归属人还没和解"时按已改的活值误判）。</summary>
    private Contract Basis(ContractRow r) =>
        _snapshot.TryGetValue(r.Model.ContractUid, out var s) ? s : r.Model;

    private void DeleteAdmin(IEnumerable<ContractRow> rows)
    {
        if (_review == null) return;
        int proposed = 0, direct = 0;
        foreach (var r in rows.ToList())
        {
            if (r.IsNew) { r.CellChanged -= OnCell; Rows.Remove(r); continue; }
            var basis = Basis(r);
            _review.SubmitDelete(basis);
            if (_review.IsMine(basis.SalesPersonName)) { r.CellChanged -= OnCell; Rows.Remove(r); _snapshot.Remove(r.Model.ContractUid); direct++; }
            else proposed++;   // 提案：保留展示，待确认后同步消失
        }
        if (proposed > 0 || direct > 0) NeedsSync?.Invoke();
        Notify($"删除：直接生效 {direct} 条，提交确认 {proposed} 条。");
        Raise(nameof(StatusText));
    }

    /// <summary>管理员对选中行发起标记（复核提醒）。</summary>
    public void MarkRows(IEnumerable<ContractRow> rows, string note)
    {
        if (_review == null) return;
        int n = 0; string? last = null;
        foreach (var r in rows.ToList())
        {
            if (r.IsNew) continue;
            var basis = Basis(r);
            last = _review.SubmitMark(basis, note);
            if (!_review.IsMine(basis.SalesPersonName)) n++;
        }
        if (n > 0) { NeedsSync?.Invoke(); Notify($"已发送 {n} 条标记提醒，待对方确认后消失。"); }
        else if (last != null) Notify(last);
    }

    private void ReplaceRow(ContractRow oldRow, Contract restored)
    {
        int idx = Rows.IndexOf(oldRow);
        if (idx < 0) return;
        oldRow.CellChanged -= OnCell;
        var fresh = Wrap(new ContractRow(restored));
        Rows[idx] = fresh;
        _undo.Clear(); _redo.Clear(); RaiseUndo();
    }

    private void Notify(string msg) { if (!string.IsNullOrEmpty(msg)) ActionMessage?.Invoke(msg); }

    public (int imported, int anomalies) ImportExcel(string path)
    {
        var res = new Weitong.Ledger.Data.Import.ExcelImporter().ImportFile(path, DateTime.UtcNow, _currentPerson);
        int n = _store.UpsertContracts(res.Contracts, _currentPerson);
        _store.WriteAudit("Import", _currentPerson, "Contract", $"导入 {n} 条 · 数据质量提示 {res.Anomalies.Count}");
        Reload();
        return (n, res.Anomalies.Count);
    }

    /// <summary>导出当前视图(含筛选/搜索结果)为标准 Excel。</summary>
    public int ExportExcel(string path)
    {
        var contracts = View.Cast<ContractRow>().Select(r => r.Model).ToList();
        LedgerExcelExporter.Export(contracts, path);
        _store.WriteAudit("Export", _currentPerson, "Contract", $"导出 {contracts.Count} 条");
        return contracts.Count;
    }

    public void NotifyChanged() => Raise(nameof(StatusText));

    // ————————— 撤销 / 重做（录入用）—————————
    private sealed record CellEdit(ContractRow Row, string Prop, object? Old, object? New);
    private readonly Stack<List<CellEdit>> _undo = new();
    private readonly Stack<List<CellEdit>> _redo = new();
    private List<CellEdit>? _batch;
    private bool _applying;
    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;

    private void OnCell(ContractRow row, string prop, object? oldV, object? newV)
    {
        if (_applying) return;
        var edit = new CellEdit(row, prop, oldV, newV);
        if (_batch != null) _batch.Add(edit);
        else { _undo.Push(new List<CellEdit> { edit }); _redo.Clear(); RaiseUndo(); }
        Raise(nameof(StatusText));
    }

    public void BeginBatch() => _batch = new List<CellEdit>();
    public void EndBatch()
    {
        if (_batch is { Count: > 0 }) { _undo.Push(_batch); _redo.Clear(); RaiseUndo(); }
        _batch = null;
    }

    public void Undo() { if (!CanUndo) return; var b = _undo.Pop(); Apply(b, true); _redo.Push(b); RaiseUndo(); Raise(nameof(StatusText)); }
    public void Redo() { if (!CanRedo) return; var b = _redo.Pop(); Apply(b, false); _undo.Push(b); RaiseUndo(); Raise(nameof(StatusText)); }

    private void Apply(List<CellEdit> batch, bool undo)
    {
        _applying = true;
        foreach (var e in undo ? Enumerable.Reverse(batch) : batch)
            typeof(ContractRow).GetProperty(e.Prop)?.SetValue(e.Row, undo ? e.Old : e.New);
        _applying = false;
    }
    private void RaiseUndo() { Raise(nameof(CanUndo)); Raise(nameof(CanRedo)); }

    public int DirtyCount => Rows.Count(r => r.IsDirty || r.IsNew);
    public int ErrorCount => Rows.Count(r => r.HasErrors);
    public string StatusText =>
        $"共 {View.Cast<object>().Count()} 条" +
        (ErrorCount > 0 ? $" · {ErrorCount} 条有校验错误(红框)" : "") +
        (_personalOnly ? " · 改完自动保存" : IsAdminReview ? " · 管理员：改本人名下直接生效，改他人名下需其确认" : "");

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Raise([CallerMemberName] string? n = null) => PropertyChanged?.Invoke(this, new(n));
}
