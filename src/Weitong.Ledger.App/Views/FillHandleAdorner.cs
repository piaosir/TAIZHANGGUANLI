using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using Weitong.Ledger.App.ViewModels;

namespace Weitong.Ledger.App.Views;

/// <summary>
/// Excel 式「填充柄」：在选区右下角画一个小黑方块（小黑点），
/// · 向下/向上拖拽 → 把选区顶端那一格的值铺满拖过的行（「统一一个值」）；
/// · 双击 → 自动向下填充到相邻数据的末行。
/// 只画极小几何，其余区域对命中测试透明，不影响表格正常点击。
/// </summary>
public sealed class FillHandleAdorner : Adorner
{
    private const double HandleSize = 7.0;   // 小黑方块边长（设备无关像素）
    private const double HitPad = 4.0;       // 命中判定外扩，方便点中
    private const double HeaderH = 34.0;     // 列头高度（与 EnterpriseGrid 一致）
    private const int MaxSelForHandle = 2000;// 选区过大(如 Ctrl+A)不显示填充柄，避免每次布局重算卡顿

    private readonly DataGrid _grid;
    private ScrollViewer? _scroll;
    private bool _scrollHooked;

    private readonly Brush _fill;
    private readonly Brush _halo;
    private readonly Pen _border;
    private readonly Pen _rangePen;

    private bool _dragging;
    private int _toIndex = -1;                // 拖拽当前目标行（Items 索引）
    private double _dragX;                     // 拖拽期用于命中测试的安全 X（填充列内，避开右侧滚动条）
    private Rect _handleRect = Rect.Empty;    // 最近一次画出的小方块（adorner 坐标）
    private Rect _lastCorner = Rect.Empty;    // 供布局变化去抖

    public FillHandleAdorner(DataGrid grid) : base(grid)
    {
        _grid = grid;
        _fill = new SolidColorBrush(Color.FromRgb(0x21, 0x73, 0x46)); _fill.Freeze();   // Excel 绿
        _halo = Brushes.White;
        _border = new Pen(Brushes.White, 1.0); _border.Freeze();
        _rangePen = new Pen(new SolidColorBrush(Color.FromRgb(0x21, 0x73, 0x46)), 1.5)
        { DashStyle = new DashStyle(new double[] { 3, 2 }, 0) };
        _rangePen.Freeze();

        _grid.SelectedCellsChanged += OnSelectionChanged;
        _grid.LayoutUpdated += OnLayoutUpdated;
        _grid.Loaded += OnGridLoaded;   // 具名，便于 Detach 退订（匿名 lambda 无法退订→adorner 泄漏）
        HookScroll();
    }

    public void Detach()
    {
        _grid.SelectedCellsChanged -= OnSelectionChanged;
        _grid.LayoutUpdated -= OnLayoutUpdated;
        _grid.Loaded -= OnGridLoaded;
        if (_scroll != null) _scroll.ScrollChanged -= OnScroll;
        _scrollHooked = false;
    }

    private void OnGridLoaded(object? s, RoutedEventArgs e) => HookScroll();

    private LedgerGridViewModel? Vm => _grid.DataContext as LedgerGridViewModel;

    private void HookScroll()
    {
        if (_scrollHooked) return;
        _scroll = GridExcel.FindVisualChild<ScrollViewer>(_grid);
        if (_scroll != null) { _scroll.ScrollChanged += OnScroll; _scrollHooked = true; }
    }

    private void OnSelectionChanged(object? s, SelectedCellsChangedEventArgs e) => InvalidateVisual();
    private void OnScroll(object? s, ScrollChangedEventArgs e) => InvalidateVisual();

    // 布局变化时只在小方块位置真正变了才重画，避免 LayoutUpdated 抖动
    private void OnLayoutUpdated(object? s, EventArgs e)
    {
        if (_dragging) return;
        var r = ComputeHandleCornerRect() ?? Rect.Empty;
        if (r != _lastCorner) { _lastCorner = r; InvalidateVisual(); }
    }

    // ————————————————— 绘制 —————————————————
    protected override void OnRender(DrawingContext dc)
    {
        var rect = ComputeHandleCornerRect();
        if (rect == null) { _handleRect = Rect.Empty; return; }
        _handleRect = rect.Value;

        if (_dragging)
        {
            var range = ComputeDragRangeRect();
            if (range != null) dc.DrawRectangle(null, _rangePen, range.Value);
        }

        // 先画一层透明命中区(比可见方块大 HitPad)，使 adorner 在该范围内可命中——
        // Adorner 只在「实际绘制过几何」处才吃鼠标，不画则点不中，HitPad 容差形同虚设。
        dc.DrawRectangle(Brushes.Transparent, null, Inflate(_handleRect, HitPad));
        dc.DrawRectangle(_halo, null, Inflate(_handleRect, 1.5));
        dc.DrawRectangle(_fill, _border, _handleRect);
    }

    private static Rect Inflate(Rect r, double d) => new(r.X - d, r.Y - d, r.Width + 2 * d, r.Height + 2 * d);

    // ————————————————— 鼠标 —————————————————
    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        if (_handleRect == Rect.Empty) return;
        var p = e.GetPosition(this);
        if (!Inflate(_handleRect, HitPad).Contains(p)) return;   // 没点在小方块上
        if (_grid.IsReadOnly) return;
        e.Handled = true;

        if (e.ClickCount == 2) { AutoFillDown(); return; }

        var col = FillColumn();
        if (col == null) return;
        var (_, bottom, _) = SelectionExtent(col);
        if (bottom < 0) return;
        // 记住一个「安全 X」：填充列单元格的水平中心，位于内容区、避开右侧竖直滚动条。
        // 拖拽时命中测试一律用它做 X，只跟随鼠标 Y，杜绝光标右漂到滚动条→命中失败→误判末行。
        var br = CellRect(_grid.Items[bottom], col);
        _dragX = br != null ? br.Value.X + br.Value.Width / 2 : Math.Max(2, _handleRect.X - HandleSize);
        _dragging = true;
        _toIndex = bottom;
        CaptureMouse();
        InvalidateVisual();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (!_dragging) return;
        var p = e.GetPosition(_grid);
        AutoScroll(p);
        int idx = RowIndexAtY(p.Y);
        if (idx >= 0 && idx != _toIndex) { _toIndex = idx; InvalidateVisual(); }
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        if (!_dragging) return;
        _dragging = false;
        ReleaseMouseCapture();
        e.Handled = true;
        PerformFill(_toIndex);
        InvalidateVisual();
    }

    private void AutoScroll(Point pInGrid)
    {
        if (_scroll == null) return;
        const double edge = 22;
        if (pInGrid.Y > _grid.ActualHeight - edge) _scroll.ScrollToVerticalOffset(_scroll.VerticalOffset + 1);
        else if (pInGrid.Y < HeaderH + edge) _scroll.ScrollToVerticalOffset(_scroll.VerticalOffset - 1);
    }

    private int RowIndexAtY(double yInGrid)
    {
        double y = Math.Clamp(yInGrid, 0, Math.Max(0, _grid.ActualHeight - 1));
        if (_grid.InputHitTest(new Point(_dragX, y)) is DependencyObject d)
        {
            var row = GridExcel.FindAncestor<DataGridRow>(d);
            if (row != null)
            {
                int i = _grid.Items.IndexOf(row.Item);
                if (i >= 0) return i;
            }
        }
        // 命中失败(如拖到末行下方空白)：保持当前目标不变，绝不回退到末行——
        // 避免任何一次命中抖动就把填充范围静默扩到全表底部(评审确认的高危误改)。
        return -1;
    }

    // ————————————————— 填充执行 —————————————————
    private void AutoFillDown()
    {
        var col = FillColumn();
        if (col == null || col.IsReadOnly) return;
        var (_, bottom, _) = SelectionExtent(col);
        if (bottom < 0) return;
        int last = LastFilledIndex(bottom);
        if (last > bottom) PerformFill(last);
    }

    private int LastFilledIndex(int from)
    {
        int last = from;
        for (int i = from + 1; i < _grid.Items.Count; i++)
        {
            if (_grid.Items[i] is not ContractRow r || IsRowBlank(r)) break;
            last = i;
        }
        return last;
    }

    private static bool IsRowBlank(ContractRow r) =>
        string.IsNullOrWhiteSpace(r.ProjectName) && string.IsNullOrWhiteSpace(r.PartyA)
        && string.IsNullOrWhiteSpace(r.ContractNo) && r.ContractAmount == 0 && r.Revenue == 0;

    private void PerformFill(int targetIndex)
    {
        var vm = Vm;
        if (vm == null || _grid.IsReadOnly) return;
        var col = FillColumn();
        if (col == null || col.IsReadOnly) return;
        var (top, bottom, src) = SelectionExtent(col);
        if (src == null) return;

        int lo = Math.Min(top, targetIndex);
        int hi = Math.Max(bottom, targetIndex);
        if (lo >= top && hi <= bottom) return;    // 没有向外扩展 → 忽略

        string val = GridExcel.GetCellValue(src, col);
        _grid.CommitEdit();
        vm.BeginBatch();
        var affected = new List<ContractRow>();
        for (int i = lo; i <= hi; i++)
        {
            if (i < 0 || i >= _grid.Items.Count) continue;
            if (_grid.Items[i] is ContractRow r && GridExcel.SetCellValue(r, col, val)) affected.Add(r);
        }
        vm.EndBatch();
        _grid.Items.Refresh();
        vm.SaveRows(affected);
        vm.NotifyChanged();
    }

    // ————————————————— 选区 / 几何 —————————————————
    private DataGridColumn? FillColumn()
    {
        if (_grid.CurrentColumn != null) return _grid.CurrentColumn;
        foreach (var c in _grid.SelectedCells) return c.Column;
        return null;
    }

    /// <summary>返回填充列上选区的顶/底行索引与「源行」（顶端选中行，其值作为填充值）。单遍扫描，不排序。</summary>
    private (int top, int bottom, ContractRow? src) SelectionExtent(DataGridColumn col)
    {
        int top = int.MaxValue, bottom = -1;
        ContractRow? topRow = null;
        foreach (var c in _grid.SelectedCells)
        {
            if (c.Column != col || c.Item is not ContractRow r) continue;
            int idx = _grid.Items.IndexOf(r);
            if (idx < 0) continue;
            if (idx < top) { top = idx; topRow = r; }
            if (idx > bottom) bottom = idx;
        }
        if (bottom < 0)   // 无多选：退回当前单元格
        {
            if (_grid.CurrentCell.Item is ContractRow cur)
            {
                int idx = _grid.Items.IndexOf(cur);
                return (idx, idx, idx >= 0 ? cur : null);
            }
            return (-1, -1, null);
        }
        return (top, bottom, topRow);
    }

    private Rect? ComputeHandleCornerRect()
    {
        if (_grid.IsReadOnly) return null;                          // 只读表（非管理员浏览）不显示填充柄
        if (_grid.SelectedCells.Count > MaxSelForHandle) return null; // 超大选区(Ctrl+A)：不显示，免每次布局重算卡顿
        var col = FillColumn();
        if (col == null) return null;
        var (_, bottom, src) = SelectionExtent(col);
        if (src == null || bottom < 0 || bottom >= _grid.Items.Count) return null;
        var cell = CellRect(_grid.Items[bottom], col);
        if (cell == null) return null;   // 该格被虚拟化/滚出视口 → 不画
        var corner = cell.Value.BottomRight;
        return new Rect(corner.X - HandleSize / 2, corner.Y - HandleSize / 2, HandleSize, HandleSize);
    }

    private Rect? ComputeDragRangeRect()
    {
        var col = FillColumn();
        if (col == null) return null;
        var (top, bottom, _) = SelectionExtent(col);
        if (top < 0) return null;
        int lo = Math.Clamp(Math.Min(top, _toIndex), 0, _grid.Items.Count - 1);
        int hi = Math.Clamp(Math.Max(bottom, _toIndex), 0, _grid.Items.Count - 1);
        var a = CellRect(_grid.Items[lo], col);
        var b = CellRect(_grid.Items[hi], col);
        if (a == null && b == null) return null;
        return Rect.Union(a ?? b!.Value, b ?? a!.Value);
    }

    private Rect? CellRect(object item, DataGridColumn col)
    {
        if (_grid.ItemContainerGenerator.ContainerFromItem(item) is not DataGridRow row) return null;
        var presenter = GridExcel.FindVisualChild<DataGridCellsPresenter>(row);
        if (presenter == null) return null;
        if (presenter.ItemContainerGenerator.ContainerFromIndex(col.DisplayIndex) is not DataGridCell cell) return null;
        if (cell.ActualWidth <= 0 || cell.ActualHeight <= 0) return null;
        var tl = cell.TransformToVisual(this).Transform(new Point(0, 0));
        return new Rect(tl, new Size(cell.ActualWidth, cell.ActualHeight));
    }
}
