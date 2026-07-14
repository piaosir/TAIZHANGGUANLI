using System.Windows;
using System.Windows.Controls;
using Weitong.Ledger.App.ViewModels;

namespace Weitong.Ledger.App.Views;

public partial class LedgerBrowseView : UserControl
{
    private LedgerGridViewModel? _hooked;

    public LedgerBrowseView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private LedgerGridViewModel Vm => (LedgerGridViewModel)DataContext;

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_hooked != null) _hooked.ActionMessage -= OnActionMessage;
        _hooked = e.NewValue as LedgerGridViewModel;
        if (_hooked != null)
        {
            _hooked.ActionMessage += OnActionMessage;
            if (_hooked.IsAdminReview)
            {
                TitleText.Text = "台账明细 · 全组总库（管理员可编辑）";
                HintText.Text = "管理员：可直接在表格中增删改。改你本人名下的记录立即生效；改他人名下的记录会生成提案，交对应销售在『待确认』确认后才执行。选中行可『标记复核』提醒对方。";
            }
        }
    }

    // 用 SetCurrentValue 而非直接赋值，避免清除 StatusText 的 OneWay 绑定（否则计数/校验提示会僵住）
    private void OnActionMessage(string msg) => StatusText.SetCurrentValue(TextBlock.TextProperty, msg);

    // ——— 管理员编辑 ———
    private void OnAdd(object sender, RoutedEventArgs e)
    {
        var row = Vm.AddRow();
        Grid.ScrollIntoView(row);
        Grid.SelectedItem = row;
    }

    private void OnDeleteRows(object sender, RoutedEventArgs e)
    {
        var selected = SelectedRows();
        if (selected.Count == 0) { MessageBox.Show("请先选中要删除的行。", "提示", MessageBoxButton.OK, MessageBoxImage.Information); return; }
        if (MessageBox.Show($"确认对选中的 {selected.Count} 条发起删除？\n本人名下立即软删，他人名下将提交对方确认。",
                "删除", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        Vm.Delete(selected);
    }

    private void OnMark(object sender, RoutedEventArgs e)
    {
        var selected = SelectedRows();
        if (selected.Count == 0) { MessageBox.Show("请先选中要标记的行。", "提示", MessageBoxButton.OK, MessageBoxImage.Information); return; }
        var note = TextPromptDialog.Ask(Window.GetWindow(this)!, "标记复核",
            $"给选中的 {selected.Count} 条记录发送复核提醒（销售确认后消失）。备注：", "请核对这条记录。");
        if (note == null) return;
        Vm.MarkRows(selected, note);
    }

    private List<ContractRow> SelectedRows()
    {
        var rows = Grid.SelectedCells.Select(c => c.Item).OfType<ContractRow>().Distinct().ToList();
        if (Grid.SelectedItem is ContractRow single && !rows.Contains(single)) rows.Add(single);
        return rows;
    }

    // 编辑一行离开时：管理员模式下按归属决定直改或提案
    private void OnRowEdited(object sender, DataGridRowEditEndingEventArgs e)
    {
        if (!Vm.IsAdminReview || e.EditAction != DataGridEditAction.Commit) return;
        if (e.Row.Item is ContractRow row)
            Dispatcher.BeginInvoke(new Action(() => Vm.SaveRow(row)), System.Windows.Threading.DispatcherPriority.Background);
    }

    private void OnImport(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "选择合同台账 Excel 文件",
            Filter = "Excel 台账 (*.xlsx)|*.xlsx",
            CheckFileExists = true,
        };
        if (dlg.ShowDialog() != true) return;
        try
        {
            var (imported, anomalies) = Vm.ImportExcel(dlg.FileName);
            MessageBox.Show($"导入成功：{imported} 条已写入总库。\n数据质量提示：{anomalies} 项（见达成总览底部）。",
                "导入完成", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex) { MessageBox.Show("导入失败：\n" + ex.Message, "错误", MessageBoxButton.OK, MessageBoxImage.Error); }
    }

    private void OnExport(object sender, RoutedEventArgs e) => LedgerGridView.ExportGrid(Vm);
}
