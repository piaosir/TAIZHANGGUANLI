using System.Globalization;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using Weitong.Ledger.App.ViewModels;
using Weitong.Ledger.Core;

namespace Weitong.Ledger.App.Views;

/// <summary>
/// 给台账 DataGrid 附加「类 Excel」交互的统一行为，供录入表与浏览表共用，避免各写一套。
/// 能力：右键菜单、键盘（Ctrl+C/X/V、Ctrl+D、Delete 清除、Ctrl+Z/Y）、整块粘贴、
/// 单元格简单公式（=+-*/，仅数值列）、以及右下角「填充柄」小黑点（<see cref="FillHandleAdorner"/>）。
/// 只读表(<c>IsReadOnly=true</c>)自动只保留「复制」。
/// 用法：在 XAML 里给 DataGrid 加 <c>views:GridExcel.EnableExcel="True"</c>。
/// </summary>
public static class GridExcel
{
    // ————————————————— 附加属性：开关 —————————————————
    public static readonly DependencyProperty EnableExcelProperty =
        DependencyProperty.RegisterAttached("EnableExcel", typeof(bool), typeof(GridExcel),
            new PropertyMetadata(false, OnEnableChanged));

    public static void SetEnableExcel(DependencyObject o, bool v) => o.SetValue(EnableExcelProperty, v);
    public static bool GetEnableExcel(DependencyObject o) => (bool)o.GetValue(EnableExcelProperty);

    // 存放填充柄 adorner 引用，便于去重与清理
    private static readonly DependencyProperty AdornerProperty =
        DependencyProperty.RegisterAttached("Adorner", typeof(FillHandleAdorner), typeof(GridExcel),
            new PropertyMetadata(null));

    private static void OnEnableChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not DataGrid grid) return;
        if (e.NewValue is true)
        {
            grid.ClipboardCopyMode = DataGridClipboardCopyMode.ExcludeHeader;   // 复制成 Excel 可识别的纯 TSV
            grid.PreviewKeyDown += OnPreviewKeyDown;
            grid.PreviewMouseRightButtonDown += OnRightButtonDown;
            grid.CellEditEnding += OnCellEditEnding;
            grid.Loaded += OnLoaded;
            grid.Unloaded += OnUnloaded;
        }
        else
        {
            grid.PreviewKeyDown -= OnPreviewKeyDown;
            grid.PreviewMouseRightButtonDown -= OnRightButtonDown;
            grid.CellEditEnding -= OnCellEditEnding;
            grid.Loaded -= OnLoaded;
            grid.Unloaded -= OnUnloaded;
        }
    }

    private static LedgerGridViewModel? Vm(DataGrid g) => g.DataContext as LedgerGridViewModel;

    // ————————————————— 加载：挂右键菜单 + 填充柄 —————————————————
    private static void OnLoaded(object sender, RoutedEventArgs e)
    {
        var grid = (DataGrid)sender;
        if (grid.ContextMenu == null)
        {
            grid.ContextMenu = BuildMenu(grid);
            grid.ContextMenuOpening += OnContextMenuOpening;
        }
        if (grid.GetValue(AdornerProperty) is not FillHandleAdorner)
        {
            var layer = AdornerLayer.GetAdornerLayer(grid);
            if (layer != null)
            {
                var ad = new FillHandleAdorner(grid);
                layer.Add(ad);
                grid.SetValue(AdornerProperty, ad);
            }
        }
    }

    private static void OnUnloaded(object sender, RoutedEventArgs e)
    {
        var grid = (DataGrid)sender;
        if (grid.GetValue(AdornerProperty) is FillHandleAdorner ad)
        {
            ad.Detach();
            AdornerLayer.GetAdornerLayer(grid)?.Remove(ad);
            grid.ClearValue(AdornerProperty);
        }
    }

    // ————————————————— 键盘 —————————————————
    private static void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        var grid = (DataGrid)sender;
        bool editingText = Keyboard.FocusedElement is TextBox;   // 单元格内正在打字
        bool ctrl = (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control;

        // Delete：非编辑态清除所选单元格内容
        if (!editingText && !ctrl && e.Key == Key.Delete)
        {
            if (!grid.IsReadOnly) { ClearCells(grid); e.Handled = true; }
            return;
        }

        if (!ctrl || editingText) return;   // 编辑打字时 Ctrl+C/V/Z 交给文本框原生
        switch (e.Key)
        {
            case Key.X: if (!grid.IsReadOnly) { Cut(grid); e.Handled = true; } break;
            case Key.V: if (!grid.IsReadOnly) { PasteBlock(grid); e.Handled = true; } break;
            case Key.D: if (!grid.IsReadOnly) { FillDown(grid); e.Handled = true; } break;
            case Key.Z: if (!grid.IsReadOnly) { grid.CommitEdit(); Vm(grid)?.Undo(); grid.Items.Refresh(); e.Handled = true; } break;
            case Key.Y: if (!grid.IsReadOnly) { grid.CommitEdit(); Vm(grid)?.Redo(); grid.Items.Refresh(); e.Handled = true; } break;
            // Ctrl+C 走 DataGrid 内建复制，不拦截
        }
    }

    // 右键先把光标单元格选中（Excel 习惯：右击哪格就对哪格操作），已在多选内则保留多选
    private static void OnRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        var grid = (DataGrid)sender;
        var cell = FindAncestor<DataGridCell>(e.OriginalSource as DependencyObject);
        if (cell == null || cell.IsSelected) return;
        try
        {
            grid.CurrentCell = new DataGridCellInfo(cell.DataContext, cell.Column);
            if (grid.SelectionUnit != DataGridSelectionUnit.FullRow)
            {
                grid.SelectedCells.Clear();
                grid.SelectedCells.Add(new DataGridCellInfo(cell.DataContext, cell.Column));
            }
        }
        catch { /* 某些状态下 SelectedCells 不可改，忽略 */ }
    }

    // ————————————————— 右键菜单 —————————————————
    private static ContextMenu BuildMenu(DataGrid grid)
    {
        var m = new ContextMenu();
        m.Items.Add(MakeItem("复制", "Ctrl+C", () => ApplicationCommands.Copy.Execute(null, grid), tag: null));
        m.Items.Add(MakeItem("剪切", "Ctrl+X", () => Cut(grid), tag: "edit"));
        m.Items.Add(MakeItem("粘贴", "Ctrl+V", () => PasteBlock(grid), tag: "edit"));
        m.Items.Add(MakeItem("清除内容", "Delete", () => ClearCells(grid), tag: "edit"));
        m.Items.Add(new Separator());
        m.Items.Add(MakeItem("向下填充", "Ctrl+D", () => FillDown(grid), tag: "edit"));
        m.Items.Add(new Separator());
        m.Items.Add(MakeItem("在上方插入行", null, () => InsertRow(grid, below: false), tag: "edit"));
        m.Items.Add(MakeItem("在下方插入行", null, () => InsertRow(grid, below: true), tag: "edit"));
        m.Items.Add(MakeItem("删除整行", null, () => DeleteRows(grid), tag: "edit"));
        m.Items.Add(new Separator());
        m.Items.Add(MakeItem("撤销", "Ctrl+Z", () => { grid.CommitEdit(); Vm(grid)?.Undo(); grid.Items.Refresh(); }, tag: "undo"));
        m.Items.Add(MakeItem("重做", "Ctrl+Y", () => { grid.CommitEdit(); Vm(grid)?.Redo(); grid.Items.Refresh(); }, tag: "redo"));
        return m;
    }

    private static MenuItem MakeItem(string header, string? gesture, Action act, string? tag)
    {
        var mi = new MenuItem { Header = header, InputGestureText = gesture ?? "", Tag = tag };
        mi.Click += (_, __) => act();
        return mi;
    }

    private static void OnContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        var grid = (DataGrid)sender;
        var vm = Vm(grid);
        bool editable = !grid.IsReadOnly;
        foreach (var it in grid.ContextMenu!.Items)
        {
            if (it is not MenuItem mi) continue;
            mi.IsEnabled = mi.Tag switch
            {
                "edit" => editable,
                "undo" => editable && vm is { CanUndo: true },
                "redo" => editable && vm is { CanRedo: true },
                _ => true,   // 复制始终可用
            };
        }
    }

    // ————————————————— 剪贴板 / 填充 / 清除 —————————————————
    private static void Cut(DataGrid grid)
    {
        if (grid.IsReadOnly) return;
        ApplicationCommands.Copy.Execute(null, grid);
        ClearCells(grid);
    }

    internal static void PasteBlock(DataGrid grid)
    {
        var vm = Vm(grid);
        if (vm == null || grid.IsReadOnly || !Clipboard.ContainsText()) return;
        var text = Clipboard.GetText().Replace("\r\n", "\n").Replace('\r', '\n').TrimEnd('\n');
        if (text.Length == 0) return;
        var lines = text.Split('\n');

        // 目标列按「显示顺序」铺开（用户可能拖动过列顺序）
        var cols = grid.Columns.OrderBy(c => c.DisplayIndex).ToList();
        // ⚠️ 结构性追加/定位一律基于「已物化的当前视图快照」，绝不用会随筛选/排序实时变化的
        //    grid.Items.Count/索引驱动 while 追加——否则筛选下死循环、排序下写错行(评审确认的高危 bug)。
        var snapshot = grid.Items.OfType<ContractRow>().ToList();
        int startRow = grid.CurrentCell.Item is ContractRow cur ? snapshot.IndexOf(cur) : 0;
        int startCol = grid.CurrentColumn != null ? cols.IndexOf(grid.CurrentColumn) : 0;
        if (startRow < 0) startRow = 0;
        if (startCol < 0) startCol = 0;

        grid.CommitEdit();
        vm.BeginBatch();
        var affected = new List<ContractRow>();
        for (int i = 0; i < lines.Length; i++)
        {
            int rowIdx = startRow + i;
            // 超出现有行 → 追加空行并「直接用返回的行对象」，不再按视图索引回查
            ContractRow row = rowIdx < snapshot.Count ? snapshot[rowIdx] : vm.AppendBlank();
            var vals = lines[i].Split('\t');
            // 位置对齐：源值 j 逐一落到目标列(显示序)。只读计算列(利润/预计到款)由 SetCellValue 直接丢弃该位值——
            // 这样「同表复制→粘贴」与「整行台账格式(含利润列)粘贴」都对齐；不改复制侧，避免复制被误剔空。
            for (int j = 0; j < vals.Length; j++)
            {
                int colIdx = startCol + j;
                if (colIdx >= cols.Count) break;
                if (SetCellValue(row, cols[colIdx], vals[j].Trim())) affected.Add(row);
            }
        }
        vm.EndBatch();
        grid.Items.Refresh();
        vm.SaveRows(affected);
        vm.NotifyChanged();
    }

    private static void FillDown(DataGrid grid)
    {
        var vm = Vm(grid);
        if (vm == null || grid.IsReadOnly) return;
        var col = grid.CurrentColumn;
        if (col == null || col.IsReadOnly) return;

        var rows = grid.SelectedCells.Where(c => c.Column == col)
                       .Select(c => c.Item).OfType<ContractRow>().Distinct()
                       .OrderBy(r => grid.Items.IndexOf(r)).ToList();

        grid.CommitEdit();
        vm.BeginBatch();
        var affected = new List<ContractRow>();
        if (rows.Count < 2)
        {
            // 未多选：用上一行同列的值填当前行
            int idx = grid.Items.IndexOf(grid.CurrentCell.Item);
            if (idx > 0 && grid.Items[idx - 1] is ContractRow src && grid.Items[idx] is ContractRow cur)
                if (SetCellValue(cur, col, GetCellValue(src, col))) affected.Add(cur);
        }
        else
        {
            var val = GetCellValue(rows[0], col);
            foreach (var r in rows.Skip(1)) if (SetCellValue(r, col, val)) affected.Add(r);
        }
        vm.EndBatch();
        grid.Items.Refresh();
        vm.SaveRows(affected);
        vm.NotifyChanged();
    }

    private static void ClearCells(DataGrid grid)
    {
        var vm = Vm(grid);
        if (vm == null || grid.IsReadOnly) return;
        var cells = grid.SelectedCells.ToList();
        if (cells.Count == 0) return;

        grid.CommitEdit();
        vm.BeginBatch();
        var affected = new List<ContractRow>();
        foreach (var c in cells)
            if (c.Item is ContractRow row && !c.Column.IsReadOnly)
                if (SetCellValue(row, c.Column, "")) affected.Add(row);
        vm.EndBatch();
        grid.Items.Refresh();
        vm.SaveRows(affected.Distinct());
        vm.NotifyChanged();
    }

    private static void InsertRow(DataGrid grid, bool below)
    {
        var vm = Vm(grid);
        if (vm == null || grid.IsReadOnly) return;
        var anchor = grid.CurrentCell.Item as ContractRow ?? grid.SelectedItem as ContractRow;
        var r = anchor != null ? vm.InsertRelative(anchor, below) : vm.AddRow();
        grid.ScrollIntoView(r);
        if (grid.Columns.Count > 0) grid.CurrentCell = new DataGridCellInfo(r, grid.Columns[0]);
    }

    private static void DeleteRows(DataGrid grid)
    {
        var vm = Vm(grid);
        if (vm == null || grid.IsReadOnly) return;
        var rows = grid.SelectedCells.Select(c => c.Item).OfType<ContractRow>().Distinct().ToList();
        if (grid.SelectedItem is ContractRow s && !rows.Contains(s)) rows.Add(s);
        if (rows.Count == 0) return;
        if (MessageBox.Show($"确认删除选中的 {rows.Count} 行？（软删除，可在审计中追溯）",
                "删除确认", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        vm.Delete(rows);
        vm.NotifyChanged();
    }

    // ————————————————— 单元格内简单公式（=+-*/，仅数值列）—————————————————
    private static void OnCellEditEnding(object? sender, DataGridCellEditEndingEventArgs e)
    {
        if (e.EditAction != DataGridEditAction.Commit) return;
        if (e.EditingElement is not TextBox tb) return;
        var text = tb.Text?.Trim();
        if (!FormulaEval.IsFormula(text) || !IsNumericColumn(e.Column)) return;

        // 算得合法且非负 → 写回数值让绑定提交；否则(语法错/负数)还原原值+提示音，
        // 不把注定被字段校验拒绝的值塞进绑定(否则单元格会卡在编辑态无法离开)。
        if (FormulaEval.TryEvaluate(text, out var result) && result >= 0)
        {
            tb.Text = result.ToString(CultureInfo.InvariantCulture);
        }
        else
        {
            if (e.Row.Item is ContractRow row) tb.Text = GetCellValue(row, e.Column);
            System.Media.SystemSounds.Exclamation.Play();   // 公式无法识别/结果非法：提示未被采纳
        }
    }

    // ————————————————— 列 ↔ 属性 反射读写（含公式）—————————————————
    private static string? PathOf(DataGridColumn col) => col switch
    {
        DataGridBoundColumn b when b.Binding is Binding bind => bind.Path.Path,
        DataGridComboBoxColumn cb when cb.SelectedItemBinding is Binding bind => bind.Path.Path,
        _ => null,
    };

    private static bool IsNumericColumn(DataGridColumn col)
    {
        var path = PathOf(col);
        if (path == null) return false;
        var pi = typeof(ContractRow).GetProperty(path);
        if (pi == null) return false;
        var t = Nullable.GetUnderlyingType(pi.PropertyType) ?? pi.PropertyType;
        return t == typeof(decimal) || t == typeof(int) || t == typeof(long) || t == typeof(double);
    }

    internal static string GetCellValue(ContractRow row, DataGridColumn col)
    {
        var path = PathOf(col);
        if (path == null) return "";
        return typeof(ContractRow).GetProperty(path)?.GetValue(row)?.ToString() ?? "";
    }

    /// <summary>把字符串写入 row 的对应属性；数值列识别 = 公式。返回是否真的发生了改变（供只保存脏行）。</summary>
    internal static bool SetCellValue(ContractRow row, DataGridColumn col, string value)
    {
        if (col.IsReadOnly) return false;
        var path = PathOf(col);
        if (path == null) return false;
        var pi = typeof(ContractRow).GetProperty(path);
        if (pi == null || !pi.CanWrite) return false;

        var t = Nullable.GetUnderlyingType(pi.PropertyType) ?? pi.PropertyType;
        object? oldVal = pi.GetValue(row);
        try
        {
            object? conv;
            if (t == typeof(string))
                conv = value;
            else if (string.IsNullOrWhiteSpace(value))
                conv = t.IsValueType ? Activator.CreateInstance(t) : null;   // 清空 → 0 / null
            else if (t == typeof(decimal))
            {
                if (!TryNumber(value, out var d)) return false;
                conv = d;
            }
            else if (t == typeof(int))
            {
                if (!TryNumber(value, out var d)) return false;
                conv = (int)Math.Round(d, MidpointRounding.AwayFromZero);
            }
            else if (t == typeof(long))
            {
                if (!TryNumber(value, out var d)) return false;
                conv = (long)Math.Round(d, MidpointRounding.AwayFromZero);
            }
            else
                conv = Convert.ChangeType(value, t, CultureInfo.InvariantCulture);

            pi.SetValue(row, conv);
            return !Equals(oldVal, pi.GetValue(row));
        }
        catch { return false; }   // 无法转换：保留原值
    }

    /// <summary>解析数值：先看是否 = 公式，否则按普通数字（去千分位/货币符）。</summary>
    private static bool TryNumber(string value, out decimal result)
    {
        var s = value.Trim();
        if (FormulaEval.IsFormula(s)) return FormulaEval.TryEvaluate(s, out result);
        s = s.Replace(",", "").Replace("，", "").Replace("¥", "").Replace("￥", "").Trim();
        return decimal.TryParse(s, NumberStyles.Number, CultureInfo.InvariantCulture, out result);
    }

    // ————————————————— 视觉树小工具（adorner 共用）—————————————————
    internal static T? FindAncestor<T>(DependencyObject? d) where T : DependencyObject
    {
        while (d != null && d is not T)
            d = d is Visual ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);
        return d as T;
    }

    internal static T? FindVisualChild<T>(DependencyObject? parent) where T : DependencyObject
    {
        if (parent == null) return null;
        int n = VisualTreeHelper.GetChildrenCount(parent);
        for (int i = 0; i < n; i++)
        {
            var c = VisualTreeHelper.GetChild(parent, i);
            if (c is T hit) return hit;
            var deep = FindVisualChild<T>(c);
            if (deep != null) return deep;
        }
        return null;
    }
}
