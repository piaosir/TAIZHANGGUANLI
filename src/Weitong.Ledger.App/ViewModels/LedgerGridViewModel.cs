using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Reflection;
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

    // ————————————————— 列筛选（Excel 式：每列勾选允许显示的值） —————————————————
    // key = ContractRow 属性名（列 Binding 路径）；value = 允许显示的「值字符串」集合。
    // 不在字典中的列 = 不筛选（全部显示）。空值统一表示为 ""，UI 显示「(空白)」。
    private readonly Dictionary<string, HashSet<string>> _colFilters = new();
    private static readonly Dictionary<string, PropertyInfo?> _propCache = new();

    private static PropertyInfo? Prop(string path)
    {
        if (!_propCache.TryGetValue(path, out var pi)) _propCache[path] = pi = typeof(ContractRow).GetProperty(path);
        return pi;
    }

    /// <summary>取某行某列的「值字符串」（distinct 列举与筛选匹配同源，保证一致）。</summary>
    private static string CellString(ContractRow r, string path) => Prop(path)?.GetValue(r)?.ToString() ?? "";

    /// <summary>该列当前是否处于筛选态（列头漏斗高亮用）。</summary>
    public bool IsColumnFiltered(string path) => _colFilters.ContainsKey(path);

    /// <summary>该列当前允许显示的值集合；未筛选返回 null（下拉初始化时用于回显勾选状态）。</summary>
    public HashSet<string>? CurrentColumnFilter(string path) =>
        _colFilters.TryGetValue(path, out var s) ? s : null;

    /// <summary>设置某列筛选；allowed=null → 清除该列筛选（全部显示）。</summary>
    public void SetColumnFilter(string path, HashSet<string>? allowed)
    {
        if (allowed == null) { if (!_colFilters.Remove(path)) return; }
        else _colFilters[path] = allowed;
        View.Refresh();
        Raise(nameof(StatusText));
    }

    /// <summary>清空全部列筛选。</summary>
    public void ClearColumnFilters()
    {
        if (_colFilters.Count == 0) return;
        _colFilters.Clear(); View.Refresh(); Raise(nameof(StatusText));
    }

    /// <summary>某列出现过的全部不同值（供筛选下拉列出）。基于全部行、行为稳定；
    /// 数值列按数值排序、其它按文本排序；空值排最前（UI 显示「(空白)」）。</summary>
    public IReadOnlyList<string> DistinctColumnValues(string path)
    {
        var raw = new HashSet<string>();
        foreach (var r in Rows) raw.Add(CellString(r, path));
        bool hasBlank = raw.Remove("");
        var list = raw.ToList();
        if (list.Count > 0 && list.All(s => decimal.TryParse(s, out _)))
            list.Sort((a, b) => decimal.Parse(a).CompareTo(decimal.Parse(b)));
        else
            list.Sort(StringComparer.CurrentCulture);
        if (hasBlank) list.Insert(0, "");
        return list;
    }

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
        if (!string.IsNullOrWhiteSpace(_filter) &&
            !r.SearchText.Contains(_filter.Trim(), StringComparison.OrdinalIgnoreCase)) return false;
        foreach (var kv in _colFilters)
            if (!kv.Value.Contains(CellString(r, kv.Key))) return false;
        return true;
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
        _batch?.Added.Add(r);   // 若处于批次(粘贴)中，登记新行，供撤销时移除
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
        if (ids.Count > 0) NeedsSync?.Invoke();   // 软删已入库记录 → 即时上云，让墓碑传播到其它设备
        Raise(nameof(StatusText));
    }

    /// <summary>自动保存单行。个人录入=直接入库；台账明细·管理=按归属决定直改或提案。</summary>
    public void SaveRow(ContractRow r)
    {
        if (IsAdminReview) { SaveRowAdmin(r); return; }
        if (!r.IsDirty && !r.IsNew) return;
        _store.UpsertContracts(new[] { r.Model }, _currentPerson);
        Raise(nameof(StatusText));
        NeedsSync?.Invoke();   // 普通编辑也即时上云（外壳会防抖合并，避免每行一次请求）
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
            var msg = _review.SubmitAdd(r.Model);   // 立即入库（他人名下会附带通知对方）
            r.MarkSaved();
            _snapshot[r.Model.ContractUid] = ContractOps.Clone(r.Model); // 登记权威快照，供后续再编辑
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
            _snapshot[uid] = ContractOps.Clone(r.Model);   // 已立即生效→新的权威值
            r.MarkSaved();
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
        int direct = 0, notified = 0;
        foreach (var r in rows.ToList())
        {
            if (r.IsNew) { r.CellChanged -= OnCell; Rows.Remove(r); continue; }
            var basis = Basis(r);
            _review.SubmitDelete(basis);   // 立即软删（他人名下会附带通知对方）
            r.CellChanged -= OnCell; Rows.Remove(r); _snapshot.Remove(r.Model.ContractUid);
            if (_review.IsMine(basis.SalesPersonName)) direct++; else notified++;
        }
        if (direct > 0 || notified > 0) NeedsSync?.Invoke();
        Notify($"已删除 {direct + notified} 条" + (notified > 0 ? $"（其中 {notified} 条已通知对应销售知晓）" : "") + "。");
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
        if (n > 0) { NeedsSync?.Invoke(); Notify($"已发送 {n} 条标记提醒，待对方知晓后消失。"); }
        else if (last != null) Notify(last);
    }

    private void Notify(string msg) { if (!string.IsNullOrEmpty(msg)) ActionMessage?.Invoke(msg); }

    /// <summary>导入结果 + 落库后读回的最新全量数据（供 UI 线程刷新表格用）。</summary>
    public readonly record struct ImportOutcome(int Imported, int Anomalies, List<Contract> Data);

    /// <summary>
    /// 后台可执行部分：解析 Excel + 批量落库 + 读回最新数据。<b>不触碰 UI 集合</b>，
    /// 因此可安全放到 Task.Run 里跑，避免解析/写库把 UI 线程卡住（导入卡死的直接原因）。
    /// 拿到结果后再由调用方在 UI 线程调用 <see cref="LoadFrom"/> 刷新表格。
    /// </summary>
    public ImportOutcome ImportExcelToStore(string path, Weitong.Ledger.Data.Import.ImportMode mode, IReadOnlyCollection<string>? sheets = null)
    {
        var res = new Weitong.Ledger.Data.Import.ExcelImporter().ImportFile(path, DateTime.UtcNow, _currentPerson, sheets);

        // 「添加为新记录」：给每行换一个全新唯一键，使其在 UpsertContracts 里一律走 INSERT，
        // 绝不与库中已有的「工作表#行号」撞键覆盖。「覆盖现有」则保留原键，命中即更新（幂等重导入）。
        if (mode == Weitong.Ledger.Data.Import.ImportMode.AppendNew)
            foreach (var c in res.Contracts)
                c.ContractUid = "imp-" + Guid.NewGuid().ToString("N")[..12];

        int n = _store.UpsertContracts(res.Contracts, _currentPerson);
        string modeText = mode == Weitong.Ledger.Data.Import.ImportMode.AppendNew ? "添加为新记录" : "覆盖现有";
        _store.WriteAudit("Import", _currentPerson, "Contract", $"导入 {n} 条（{modeText}）· 数据质量提示 {res.Anomalies.Count}");
        var data = _personalOnly ? _store.GetContractsFor(_currentPerson) : _store.GetAllContracts();
        return new ImportOutcome(n, res.Anomalies.Count, data);
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
    /// <summary>一步可撤销操作：一批单元格值改动 + 本步(粘贴)新建的行。</summary>
    private sealed class EditBatch
    {
        public List<CellEdit> Cells { get; } = new();
        public List<ContractRow> Added { get; } = new();
        public bool IsEmpty => Cells.Count == 0 && Added.Count == 0;
    }
    private readonly Stack<EditBatch> _undo = new();
    private readonly Stack<EditBatch> _redo = new();
    private EditBatch? _batch;
    private bool _applying;
    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;

    private void OnCell(ContractRow row, string prop, object? oldV, object? newV)
    {
        if (_applying) return;
        var edit = new CellEdit(row, prop, oldV, newV);
        if (_batch != null) _batch.Cells.Add(edit);
        else { var b = new EditBatch(); b.Cells.Add(edit); _undo.Push(b); _redo.Clear(); RaiseUndo(); }
        Raise(nameof(StatusText));
    }

    public void BeginBatch() => _batch = new EditBatch();
    public void EndBatch()
    {
        if (_batch is { IsEmpty: false }) { _undo.Push(_batch); _redo.Clear(); RaiseUndo(); }
        _batch = null;
    }

    public void Undo()
    {
        if (!CanUndo) return;
        var b = _undo.Pop();
        ApplyCells(b, undo: true);
        var touched = TouchedExisting(b);
        RemoveAdded(b.Added);       // 撤销粘贴：移除本步新增的行（个人模式并软删库）
        PersistTouched(touched);    // 把撤销后的值写回库，保持 库=界面 一致
        _redo.Push(b); RaiseUndo(); Raise(nameof(StatusText));
    }

    public void Redo()
    {
        if (!CanRedo) return;
        var b = _redo.Pop();
        ReAddRows(b.Added);         // 重做粘贴：先把曾新增的行加回
        ApplyCells(b, undo: false);
        var touched = TouchedExisting(b);
        foreach (var r in b.Added) touched.Add(r);
        PersistTouched(touched);
        _undo.Push(b); RaiseUndo(); Raise(nameof(StatusText));
    }

    private void ApplyCells(EditBatch batch, bool undo)
    {
        _applying = true;
        foreach (var e in undo ? Enumerable.Reverse(batch.Cells) : batch.Cells)
            typeof(ContractRow).GetProperty(e.Prop)?.SetValue(e.Row, undo ? e.Old : e.New);
        _applying = false;
    }

    /// <summary>本步触及的既有行（排除本步新增的行）。</summary>
    private HashSet<ContractRow> TouchedExisting(EditBatch b)
    {
        var set = new HashSet<ContractRow>();
        foreach (var e in b.Cells) if (!b.Added.Contains(e.Row)) set.Add(e.Row);
        return set;
    }

    /// <summary>撤销/重做后把值写回库。仅个人录入模式——管理员模式撤销保持纯视觉，避免 SaveRow 误生成提案。
    /// 一次性 UpsertContracts（单个 DbContext），避免逐行开加密连接跑 KDF 导致大批量撤销卡死。</summary>
    private void PersistTouched(ICollection<ContractRow> rows)
    {
        if (!_personalOnly || rows.Count == 0) return;
        // 只落真正带改动的行(IsDirty)。不看 IsNew——否则「重做」会把从未填过值的空白追加行(如 tab-only 粘贴行)也插入库，
        // 造成库比原始粘贴多出一条空合同。
        var dirty = rows.Where(r => r.IsDirty).Select(r => r.Model).ToList();
        if (dirty.Count > 0) _store.UpsertContracts(dirty, _currentPerson);
        Raise(nameof(StatusText));
    }

    /// <summary>撤销粘贴新增的行(仅个人录入)：批量软删已入库者并重置身份，使「重做」能以全新 ContractUid 干净重插。
    /// 管理员/浏览模式不在此结构性反转——其新增经审核系统路由(直改本人/生成提案+已上云)，无法在撤销里安全反转，
    /// 故保持纯视觉(仅回退单元格值)，避免"界面移除了、总库/提案还在且已广播"的错误一致性(评审确认的高危)。</summary>
    private void RemoveAdded(IList<ContractRow> added)
    {
        if (added.Count == 0 || !_personalOnly) return;
        var ids = added.Where(r => r.Model.Id > 0).Select(r => r.Model.Id).ToList();
        if (ids.Count > 0) _store.SoftDeleteContracts(ids, _currentPerson);   // 批量软删（单 DbContext）
        foreach (var r in added)
        {
            r.Model.Id = 0;
            r.Model.ContractUid = "new-" + Guid.NewGuid().ToString("N")[..12];  // 换新键，重做时纯插入
            if (Rows.Contains(r)) { r.CellChanged -= OnCell; Rows.Remove(r); }
        }
    }

    private void ReAddRows(IEnumerable<ContractRow> added)
    {
        if (!_personalOnly) return;   // 与 RemoveAdded 对称：管理员模式不做结构性增删撤销/重做
        foreach (var r in added)
            if (!Rows.Contains(r)) { r.CellChanged += OnCell; Rows.Add(r); }
    }

    private void RaiseUndo() { Raise(nameof(CanUndo)); Raise(nameof(CanRedo)); }

    public int DirtyCount => Rows.Count(r => r.IsDirty || r.IsNew);
    public int ErrorCount => Rows.Count(r => r.HasErrors);
    public string StatusText =>
        $"共 {View.Cast<object>().Count()} 条" +
        (ErrorCount > 0 ? $" · {ErrorCount} 条有校验错误(红框)" : "") +
        (_personalOnly ? " · 改完自动保存" : IsAdminReview ? " · 管理员：增删改立即生效，改他人名下会通知对方知晓" : "");

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Raise([CallerMemberName] string? n = null) => PropertyChanged?.Invoke(this, new(n));
}
