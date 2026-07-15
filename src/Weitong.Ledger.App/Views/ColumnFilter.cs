using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using Weitong.Ledger.App.ViewModels;

namespace Weitong.Ledger.App.Views;

/// <summary>
/// 列头「漏斗」筛选（Excel 式）：点击列头漏斗按钮弹出该列不同值的复选清单，支持搜索、全选、升/降序。
/// 与 <see cref="LedgerGridViewModel"/> 的列筛选状态协作；靠附加属性挂到表头模板里的漏斗按钮上，无需 code-behind。
/// </summary>
public static class ColumnFilter
{
    // 贴在表头模板里的漏斗 Button 上：标记它是筛选触发器（模板一实例化就挂事件）。
    public static readonly DependencyProperty IsFilterTriggerProperty =
        DependencyProperty.RegisterAttached("IsFilterTrigger", typeof(bool), typeof(ColumnFilter),
            new PropertyMetadata(false, OnIsFilterTriggerChanged));
    public static void SetIsFilterTrigger(DependencyObject o, bool v) => o.SetValue(IsFilterTriggerProperty, v);
    public static bool GetIsFilterTrigger(DependencyObject o) => (bool)o.GetValue(IsFilterTriggerProperty);

    // 贴在漏斗 Button 上：该列是否处于筛选态（驱动按钮模板里的高亮 Trigger）。
    public static readonly DependencyProperty IsActiveProperty =
        DependencyProperty.RegisterAttached("IsActive", typeof(bool), typeof(ColumnFilter),
            new PropertyMetadata(false));
    public static void SetIsActive(DependencyObject o, bool v) => o.SetValue(IsActiveProperty, v);
    public static bool GetIsActive(DependencyObject o) => (bool)o.GetValue(IsActiveProperty);

    // 全表共用一个下拉（同一时刻只会打开一个）。
    private static Popup? _popup;
    private static ColumnFilterPopup? _content;

    private static void OnIsFilterTriggerChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not Button btn || e.NewValue is not true) return;
        btn.Click += OnFilterClick;
        btn.Loaded += OnFilterLoaded;
    }

    // 表头加载后：若该列已在筛选，恢复漏斗高亮（切页/表头重建后仍能反映筛选态）。
    private static void OnFilterLoaded(object sender, RoutedEventArgs e)
    {
        var btn = (Button)sender;
        var (grid, _, path) = Resolve(btn);
        var vm = grid?.DataContext as LedgerGridViewModel;
        SetIsActive(btn, vm != null && path != null && vm.IsColumnFiltered(path));
    }

    private static void OnFilterClick(object sender, RoutedEventArgs e)
    {
        e.Handled = true;   // 阻止冒泡到列头，避免顺带触发排序
        var btn = (Button)sender;
        var (grid, col, path) = Resolve(btn);
        if (grid == null || col == null || path == null) return;
        if (grid.DataContext is not LedgerGridViewModel vm) return;

        EnsurePopup();
        _content!.Init(col.Header?.ToString() ?? "筛选", vm.DistinctColumnValues(path), vm.CurrentColumnFilter(path));
        _content.Applied = allowed =>
        {
            vm.SetColumnFilter(path, allowed);
            SetIsActive(btn, allowed != null);
            _popup!.IsOpen = false;
        };
        _content.SortRequested = dir =>
        {
            ApplySort(grid, col, path, dir);
            _popup!.IsOpen = false;
        };
        _content.Cancelled = () => _popup!.IsOpen = false;

        _popup!.PlacementTarget = btn;
        _popup.IsOpen = true;
    }

    private static (DataGrid? grid, DataGridColumn? col, string? path) Resolve(Button btn)
    {
        var header = GridExcel.FindAncestor<DataGridColumnHeader>(btn);
        var grid = GridExcel.FindAncestor<DataGrid>(btn);
        var col = header?.Column;
        return (grid, col, col == null ? null : GridExcel.PathOf(col));
    }

    private static void ApplySort(DataGrid grid, DataGridColumn col, string path, ListSortDirection dir)
    {
        if (grid.ItemsSource is not ICollectionView view) return;
        using (view.DeferRefresh())
        {
            view.SortDescriptions.Clear();
            view.SortDescriptions.Add(new SortDescription(path, dir));
        }
        foreach (var c in grid.Columns) c.SortDirection = null;   // 同步列头排序箭头
        col.SortDirection = dir;
    }

    private static void EnsurePopup()
    {
        if (_popup != null) return;
        _content = new ColumnFilterPopup();
        _popup = new Popup
        {
            Child = _content,
            StaysOpen = false,          // 点外部自动关闭
            AllowsTransparency = true,
            PopupAnimation = PopupAnimation.Fade,
            Placement = PlacementMode.Bottom,
        };
    }
}
