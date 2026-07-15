using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;

namespace Weitong.Ledger.App.Views;

/// <summary>
/// 列头筛选下拉的内容控件（Excel 式）：升/降序、搜索、全选、值复选清单、确定/取消/清除。
/// 纯即用即弃的小弹窗——用轻量 code-behind 承载交互，通过回调把结果交给 <see cref="ColumnFilter"/>。
/// </summary>
public partial class ColumnFilterPopup : UserControl
{
    /// <summary>确定：传回允许显示的值集合；null 表示「不筛选（全部显示）」。</summary>
    public Action<HashSet<string>?>? Applied;
    /// <summary>请求按本列排序。</summary>
    public Action<ListSortDirection>? SortRequested;
    /// <summary>取消：不改动。</summary>
    public Action? Cancelled;

    private List<FilterItem> _all = new();
    private ICollectionView? _view;
    private string _search = "";
    private bool _suppress;   // 批量改勾选时抑制「全选」态重算递归

    public ColumnFilterPopup() => InitializeComponent();

    public void Init(string header, IReadOnlyList<string> values, HashSet<string>? currentAllowed)
    {
        TitleText.Text = header;
        _search = "";
        SearchBox.Text = "";
        foreach (var it in _all) it.CheckedChanged -= OnItemChecked;   // 解除旧订阅
        _all = values.Select(v =>
        {
            var it = new FilterItem
            {
                Value = v,
                Display = v.Length == 0 ? "（空白）" : v,
                IsChecked = currentAllowed == null || currentAllowed.Contains(v),
            };
            it.CheckedChanged += OnItemChecked;
            return it;
        }).ToList();
        _view = CollectionViewSource.GetDefaultView(_all);
        _view.Filter = o => Visible((FilterItem)o);
        ValueList.ItemsSource = _view;
        UpdateSelectAll();
        Dispatcher.BeginInvoke(new Action(() => SearchBox.Focus()));
    }

    private bool Visible(FilterItem i) =>
        _search.Length == 0 || i.Display.Contains(_search, StringComparison.OrdinalIgnoreCase);

    private void OnSearch(object sender, TextChangedEventArgs e)
    {
        _search = SearchBox.Text?.Trim() ?? "";
        _view?.Refresh();
        UpdateSelectAll();
    }

    private void OnItemChecked() { if (!_suppress) UpdateSelectAll(); }

    /// <summary>「全选」三态基于当前可见（搜索过滤后）的项。</summary>
    private void UpdateSelectAll()
    {
        var visible = _all.Where(Visible).ToList();
        _suppress = true;
        SelectAll.IsChecked = visible.Count == 0 ? false
            : visible.All(i => i.IsChecked) ? true
            : visible.All(i => !i.IsChecked) ? false
            : (bool?)null;
        _suppress = false;
    }

    private void OnSelectAll(object sender, RoutedEventArgs e)
    {
        var visible = _all.Where(Visible).ToList();
        bool target = !(visible.Count > 0 && visible.All(i => i.IsChecked));  // 已全选→取消，否则→全选
        _suppress = true;
        foreach (var i in visible) i.IsChecked = target;
        _suppress = false;
        UpdateSelectAll();   // 覆盖三态点击循环设的值，保证显示正确
    }

    private void OnOk(object sender, RoutedEventArgs e)
    {
        var picked = _all.Where(i => i.IsChecked).Select(i => i.Value).ToHashSet();
        Applied?.Invoke(picked.Count == _all.Count ? null : picked);   // 全勾选 = 不筛选
    }

    private void OnClear(object sender, RoutedEventArgs e) => Applied?.Invoke(null);
    private void OnCancel(object sender, RoutedEventArgs e) => Cancelled?.Invoke();
    private void OnSortAsc(object sender, RoutedEventArgs e) => SortRequested?.Invoke(ListSortDirection.Ascending);
    private void OnSortDesc(object sender, RoutedEventArgs e) => SortRequested?.Invoke(ListSortDirection.Descending);

    /// <summary>下拉里的一行：一个值 + 是否勾选。</summary>
    public sealed class FilterItem : INotifyPropertyChanged
    {
        public string Value { get; init; } = "";
        public string Display { get; init; } = "";
        private bool _checked;
        public bool IsChecked
        {
            get => _checked;
            set { if (_checked == value) return; _checked = value; PropertyChanged?.Invoke(this, new(nameof(IsChecked))); CheckedChanged?.Invoke(); }
        }
        public event Action? CheckedChanged;
        public event PropertyChangedEventHandler? PropertyChanged;
    }
}
