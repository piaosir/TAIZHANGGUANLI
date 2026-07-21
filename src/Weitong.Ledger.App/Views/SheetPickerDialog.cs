using System.Windows;
using System.Windows.Controls;

namespace Weitong.Ledger.App.Views;

/// <summary>
/// 导入前选择要导入哪些台账分表（按团队只导本团队那部分）。
/// 只有一个分表时直接返回它、不弹窗；用户取消返回 null。代码构建，无独立 XAML。
/// </summary>
public static class SheetPickerDialog
{
    public static List<string>? Ask(Window? owner, IReadOnlyList<string> sheets)
    {
        if (sheets.Count <= 1) return sheets.ToList();

        var checks = sheets
            .Select(s => new CheckBox { Content = s, IsChecked = true, Margin = new Thickness(0, 3, 0, 3), FontSize = 13 })
            .ToList();

        var panel = new StackPanel { Margin = new Thickness(18) };
        panel.Children.Add(new TextBlock
        {
            Text = "这个 Excel 有多个台账分表。请只勾选要导入到【当前团队】的分表——别的团队的分表请以那个团队的身份另行导入：",
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10), FontSize = 13,
        });
        foreach (var c in checks) panel.Children.Add(c);

        var ok = new Button { Content = "导入选中分表", IsDefault = true, Padding = new Thickness(14, 5, 14, 5), Margin = new Thickness(0, 14, 8, 0), MinWidth = 96 };
        var cancel = new Button { Content = "取消", IsCancel = true, Padding = new Thickness(14, 5, 14, 5), Margin = new Thickness(0, 14, 0, 0), MinWidth = 72 };
        var btns = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        btns.Children.Add(ok);
        btns.Children.Add(cancel);
        panel.Children.Add(btns);

        var win = new Window
        {
            Title = "选择要导入的分表",
            Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto },
            SizeToContent = SizeToContent.Height,
            Width = 470,
            MaxHeight = 560,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner = owner,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false,
        };

        List<string>? result = null;
        ok.Click += (_, _) =>
        {
            result = checks.Where(c => c.IsChecked == true).Select(c => (string)c.Content).ToList();
            if (result.Count == 0) { MessageBox.Show(win, "请至少勾选一个分表。", "提示", MessageBoxButton.OK, MessageBoxImage.Information); return; }
            win.DialogResult = true;
        };
        return win.ShowDialog() == true ? result : null;
    }
}
