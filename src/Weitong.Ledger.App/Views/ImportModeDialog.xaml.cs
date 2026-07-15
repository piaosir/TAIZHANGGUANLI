using System.IO;
using System.Windows;
using Weitong.Ledger.Data.Import;

namespace Weitong.Ledger.App.Views;

/// <summary>导入前让用户选择落库策略：覆盖现有 / 添加为新记录。</summary>
public partial class ImportModeDialog : Window
{
    public ImportMode Mode { get; private set; }

    public ImportModeDialog(string filePath)
    {
        InitializeComponent();
        FileText.Text = $"文件：{Path.GetFileName(filePath)}";
    }

    /// <summary>弹出并返回所选模式；取消返回 null。</summary>
    public static ImportMode? Ask(Window? owner, string filePath)
    {
        var dlg = new ImportModeDialog(filePath) { Owner = owner };
        return dlg.ShowDialog() == true ? dlg.Mode : null;
    }

    private void OnOverwrite(object sender, RoutedEventArgs e) { Mode = ImportMode.Overwrite; DialogResult = true; }
    private void OnAppend(object sender, RoutedEventArgs e) { Mode = ImportMode.AppendNew; DialogResult = true; }
    private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;
}
