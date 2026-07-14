using System.IO;
using System.Windows;
using System.Windows.Controls;
using Weitong.Ledger.App.Services;

namespace Weitong.Ledger.App.Views;

public partial class SettingsView : UserControl
{
    private readonly AppConfig _cfg;
    private readonly Action<string, string> _onSaved;

    public SettingsView(AppConfig cfg, Action<string, string> onSaved)
    {
        InitializeComponent();
        _cfg = cfg;
        _onSaved = onSaved;
        NameBox.Text = cfg.PersonName ?? "";
        TeamBox.Text = cfg.TeamName ?? "行业市场组";

        DataInfo.Text = "· 台账数据在本机加密存储（AES-256 落地加密）\n" +
                        "· 汇总数据以国密 SM4 加密后传输与存储\n" +
                        "· 身份与配置均加密保存在本机，不外传";
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        var name = NameBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            MessageBox.Show("请填写姓名。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var team = string.IsNullOrWhiteSpace(TeamBox.Text) ? "行业市场组" : TeamBox.Text.Trim();
        _cfg.PersonName = name;
        _cfg.TeamName = team;
        _cfg.Save(AppConfig.DefaultDir);
        _onSaved(name, team);
        MessageBox.Show("已保存。", "保存完成", MessageBoxButton.OK, MessageBoxImage.Information);
    }
}
