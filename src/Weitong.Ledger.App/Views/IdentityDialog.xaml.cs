using System.Windows;
using Weitong.Ledger.App.Services;

namespace Weitong.Ledger.App.Views;

public partial class IdentityDialog : Window
{
    public string PersonName => NameBox.Text.Trim();
    public string TeamName => string.IsNullOrWhiteSpace(TeamBox.Text) ? "行业市场组" : TeamBox.Text.Trim();

    public IdentityDialog(string? presetName = null)
    {
        InitializeComponent();
        if (!string.IsNullOrWhiteSpace(presetName)) NameBox.Text = presetName;
        MachineText.Text = "确认后自动记住你的身份，以后打开无需再选。";
        Loaded += (_, _) => NameBox.Focus();
    }

    private void OnOk(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(NameBox.Text))
        {
            MessageBox.Show("请填写姓名。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        DialogResult = true;
    }

    private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;
}
