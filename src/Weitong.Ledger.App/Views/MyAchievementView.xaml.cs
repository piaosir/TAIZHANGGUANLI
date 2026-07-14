using System.Windows;
using System.Windows.Controls;
using Weitong.Ledger.App.ViewModels;

namespace Weitong.Ledger.App.Views;

public partial class MyAchievementView : UserControl
{
    public MyAchievementView() => InitializeComponent();

    private void OnSaveTarget(object sender, RoutedEventArgs e)
    {
        var vm = (MyAchievementViewModel)DataContext;
        vm.SaveTarget();
        MessageBox.Show("个人目标已保存，完成率已更新。", "已保存", MessageBoxButton.OK, MessageBoxImage.Information);
    }
}
