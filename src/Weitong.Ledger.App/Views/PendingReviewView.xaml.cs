using System.Windows;
using System.Windows.Controls;
using Weitong.Ledger.App.ViewModels;

namespace Weitong.Ledger.App.Views;

public partial class PendingReviewView : UserControl
{
    public PendingReviewView() => InitializeComponent();
    private PendingReviewViewModel Vm => (PendingReviewViewModel)DataContext;

    private static ReviewItemVm? ItemOf(object sender) =>
        (sender as FrameworkElement)?.DataContext as ReviewItemVm;

    private void OnConfirm(object sender, RoutedEventArgs e)
    {
        if (ItemOf(sender) is not { } vm) return;
        var verb = vm.IsMark ? "知晓" : "确认执行";
        if (MessageBox.Show($"{verb}「{vm.ByName}」的{vm.OpText}？\n\n{vm.Summary}",
                verb, MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
        Vm.Confirm(vm);
    }

    private void OnReject(object sender, RoutedEventArgs e)
    {
        if (ItemOf(sender) is not { } vm) return;
        DoReject(vm);
    }

    private void DoReject(ReviewItemVm vm)
    {
        var owner = Window.GetWindow(this)!;
        var note = TextPromptDialog.Ask(owner, "驳回", $"驳回「{vm.ByName}」的{vm.OpText}。可填写驳回理由（可留空）：");
        if (note == null) return;   // 取消
        Vm.Reject(vm, string.IsNullOrWhiteSpace(note) ? null : note);
    }

    // 查看对照（待我确认）：弹窗展示现值 vs 提案值，可直接确认/驳回
    private void OnView(object sender, RoutedEventArgs e)
    {
        if (ItemOf(sender) is not { } vm) return;
        var detail = Vm.BuildDetail(vm, isIncoming: true);
        var dlg = new ReviewDetailDialog(detail) { Owner = Window.GetWindow(this) };
        dlg.ShowDialog();
        if (dlg.Action == "confirm") Vm.Confirm(vm);
        else if (dlg.Action == "reject") DoReject(vm);
    }

    // 查看（我发起的）：只读对照
    private void OnViewOutgoing(object sender, RoutedEventArgs e)
    {
        if (ItemOf(sender) is not { } vm) return;
        var detail = Vm.BuildDetail(vm, isIncoming: false);
        new ReviewDetailDialog(detail) { Owner = Window.GetWindow(this) }.ShowDialog();
    }

    private void OnWithdraw(object sender, RoutedEventArgs e)
    {
        if (ItemOf(sender) is not { } vm) return;
        if (MessageBox.Show($"撤回你对「{vm.TargetOwnerName}」发起的{vm.OpText}？\n对方尚未处理的这条待办将随之消失。\n\n{vm.Summary}",
                "撤回", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
        if (!Vm.Withdraw(vm))
            MessageBox.Show("撤回失败：对方可能已处理该事项。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
    }
}
