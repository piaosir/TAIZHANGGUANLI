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

    // 单条"知道了"：改动已生效，只消除通知，无需二次确认
    private void OnConfirm(object sender, RoutedEventArgs e)
    {
        if (ItemOf(sender) is not { } vm) return;
        Vm.Confirm(vm);
    }

    // 查看详情（待我知晓）：弹窗展示该记录当前内容 + 管理员做了什么，看后可直接"知道了"
    private void OnView(object sender, RoutedEventArgs e)
    {
        if (ItemOf(sender) is not { } vm) return;
        var detail = Vm.BuildDetail(vm, isIncoming: true);
        var dlg = new ReviewDetailDialog(detail) { Owner = Window.GetWindow(this) };
        dlg.ShowDialog();
        if (dlg.Action == "confirm") Vm.Confirm(vm);
    }

    // 查看（我发起的）：只读详情
    private void OnViewOutgoing(object sender, RoutedEventArgs e)
    {
        if (ItemOf(sender) is not { } vm) return;
        var detail = Vm.BuildDetail(vm, isIncoming: false);
        new ReviewDetailDialog(detail) { Owner = Window.GetWindow(this) }.ShowDialog();
    }

    private void OnWithdraw(object sender, RoutedEventArgs e)
    {
        if (ItemOf(sender) is not { } vm) return;
        if (MessageBox.Show($"撤回你给「{vm.TargetOwnerName}」的这条{vm.OpText}通知？\n改动已经生效、不受影响；仅对方尚未看到的这条通知会消失。\n\n{vm.Summary}",
                "撤回通知", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
        if (!Vm.Withdraw(vm))
            MessageBox.Show("撤回失败：对方可能已经知晓这条通知。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    // ————————————————— 批量处理 —————————————————
    private void OnIncomingSelectAll(object sender, RoutedEventArgs e)
    {
        bool target = Vm.Incoming.Any(v => !v.IsSelected);   // 有未选 → 全选；否则 → 全不选
        foreach (var v in Vm.Incoming) v.IsSelected = target;
        IncomingAll.IsChecked = target;
    }

    private void OnOutgoingSelectAll(object sender, RoutedEventArgs e)
    {
        bool target = Vm.Outgoing.Any(v => !v.IsSelected);
        foreach (var v in Vm.Outgoing) v.IsSelected = target;
        OutgoingAll.IsChecked = target;
    }

    private void OnBatchConfirm(object sender, RoutedEventArgs e)
    {
        var sel = Vm.Incoming.Where(v => v.IsSelected).ToList();
        if (sel.Count == 0) { Info("请先勾选要标为已知晓的通知。"); return; }
        int n = Vm.ConfirmMany(sel);
        IncomingAll.IsChecked = false;
        Info($"已知晓 {n} 条。");
    }

    private void OnBatchWithdraw(object sender, RoutedEventArgs e)
    {
        var sel = Vm.Outgoing.Where(v => v.IsSelected).ToList();
        if (sel.Count == 0) { Info("请先勾选要撤回的通知。"); return; }
        int withdrawable = sel.Count(v => v.CanWithdraw);
        if (withdrawable == 0) { Info("选中项对方均已知晓，无法撤回。"); return; }
        if (MessageBox.Show($"撤回选中的 {withdrawable} 条通知？\n改动已生效、不受影响；仅对方尚未看到的通知会消失。", "批量撤回通知",
                MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
        var (withdrawn, skipped) = Vm.WithdrawMany(sel);
        OutgoingAll.IsChecked = false;
        Info($"已撤回 {withdrawn} 条。" + (skipped > 0 ? $"\n{skipped} 条无法撤回（对方可能已知晓），已跳过。" : ""));
    }

    private static void Info(string msg) => MessageBox.Show(msg, "提示", MessageBoxButton.OK, MessageBoxImage.Information);
}
