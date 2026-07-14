using System.Windows;
using System.Windows.Controls;
using Weitong.Ledger.App.ViewModels;

namespace Weitong.Ledger.App.Views;

public partial class LedgerGridView : UserControl
{
    public LedgerGridView() => InitializeComponent();

    private LedgerGridViewModel Vm => (LedgerGridViewModel)DataContext;

    // 类 Excel 交互（右键菜单 / 键盘 Ctrl+C/X/V·Ctrl+D·Delete / 填充柄 / =公式）统一由
    // GridExcel 附加行为提供（见 XAML 的 views:GridExcel.EnableExcel="True"）。这里只留工具栏按钮逻辑。

    private void OnAdd(object sender, RoutedEventArgs e)
    {
        var row = Vm.AddRow();
        Grid.ScrollIntoView(row);
        Grid.SelectedItem = row;
    }

    private void OnDelete(object sender, RoutedEventArgs e)
    {
        var selected = Grid.SelectedCells.Select(c => c.Item).OfType<ContractRow>().Distinct().ToList();
        if (Grid.SelectedItem is ContractRow single && !selected.Contains(single)) selected.Add(single);
        if (selected.Count == 0) return;
        if (MessageBox.Show($"确认删除选中的 {selected.Count} 条记录？（软删除，可在审计中追溯）",
                "删除确认", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        Vm.Delete(selected);
    }

    // 改完一行离开时自动保存进总库（无需手动保存按钮）
    private void OnRowEdited(object sender, DataGridRowEditEndingEventArgs e)
    {
        if (e.EditAction != DataGridEditAction.Commit) return;
        if (e.Row.Item is ContractRow row)
            Dispatcher.BeginInvoke(new Action(() => Vm.SaveRow(row)), System.Windows.Threading.DispatcherPriority.Background);
    }

    private void OnExport(object sender, RoutedEventArgs e) => ExportGrid(Vm);

    internal static void ExportGrid(LedgerGridViewModel vm)
    {
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Title = "导出台账为 Excel",
            Filter = "Excel 台账 (*.xlsx)|*.xlsx",
            FileName = $"合同台账_{DateTime.Now:yyyyMMdd}.xlsx",
        };
        if (dlg.ShowDialog() != true) return;
        try
        {
            int n = vm.ExportExcel(dlg.FileName);
            MessageBox.Show($"已导出 {n} 条到：\n{dlg.FileName}", "导出完成", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex) { MessageBox.Show("导出失败：\n" + ex.Message, "错误", MessageBoxButton.OK, MessageBoxImage.Error); }
    }

    private void OnUndo(object sender, RoutedEventArgs e) { Grid.CommitEdit(); Vm.Undo(); Grid.Items.Refresh(); }
    private void OnRedo(object sender, RoutedEventArgs e) { Grid.CommitEdit(); Vm.Redo(); Grid.Items.Refresh(); }
}
