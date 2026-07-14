using System.Windows;

namespace Weitong.Ledger.App.Views;

public partial class TextPromptDialog : Window
{
    public string Value => InputBox.Text.Trim();

    public TextPromptDialog(string title, string hint, string preset = "")
    {
        InitializeComponent();
        TitleText.Text = title;
        HintText.Text = hint;
        InputBox.Text = preset;
        Loaded += (_, _) => { InputBox.Focus(); InputBox.CaretIndex = InputBox.Text.Length; };
    }

    /// <summary>弹出并返回输入值；取消返回 null。</summary>
    public static string? Ask(Window owner, string title, string hint, string preset = "")
    {
        var dlg = new TextPromptDialog(title, hint, preset) { Owner = owner };
        return dlg.ShowDialog() == true ? dlg.Value : null;
    }

    private void OnOk(object sender, RoutedEventArgs e) => DialogResult = true;
    private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;
}
