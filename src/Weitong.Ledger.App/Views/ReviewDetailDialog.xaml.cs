using System.Windows;
using Weitong.Ledger.App.ViewModels;

namespace Weitong.Ledger.App.Views;

public partial class ReviewDetailDialog : Window
{
    /// <summary>用户在弹窗内的选择："confirm"（知道了）/ null（仅查看）。</summary>
    public string? Action { get; private set; }

    public ReviewDetailDialog(ReviewDetail detail)
    {
        InitializeComponent();
        DataContext = detail;
        if (!detail.ShowProposed) ProposedCol.Visibility = Visibility.Collapsed;
    }

    private void OnConfirm(object sender, RoutedEventArgs e) { Action = "confirm"; DialogResult = true; }
    private void OnClose(object sender, RoutedEventArgs e) { DialogResult = false; }
}
