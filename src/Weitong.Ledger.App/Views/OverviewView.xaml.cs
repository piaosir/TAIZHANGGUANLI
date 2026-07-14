using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Weitong.Ledger.App.ViewModels;

namespace Weitong.Ledger.App.Views;

public partial class OverviewView : UserControl
{
    public OverviewView() => InitializeComponent();

    private void OnExportReport(object sender, RoutedEventArgs e)
    {
        if (DataContext is not DashboardViewModel vm) return;

        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Title = "导出达成总览 · 详细报告",
            Filter = "Excel 详细报告 (*.xlsx)|*.xlsx",
            FileName = $"达成总览详细报告_{vm.TeamName}_{DateTime.Now:yyyyMMdd}.xlsx",
        };
        if (dlg.ShowDialog() != true) return;

        try
        {
            vm.ExportDetailReport(dlg.FileName);
            if (MessageBox.Show($"已导出详细报告到：\n{dlg.FileName}\n\n是否现在打开？", "导出完成",
                    MessageBoxButton.YesNo, MessageBoxImage.Information) == MessageBoxResult.Yes)
                Process.Start(new ProcessStartInfo(dlg.FileName) { UseShellExecute = true });
        }
        catch (IOException)
        {
            MessageBox.Show("导出失败：目标文件可能正被 Excel 打开，请先关闭后重试。", "错误",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
        catch (Exception ex)
        {
            MessageBox.Show("导出失败：\n" + ex.Message, "错误", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
}
