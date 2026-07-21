using System.Windows;
using System.Windows.Controls;
using Weitong.Ledger.App.ViewModels;

namespace Weitong.Ledger.App.Views;

public partial class LedgerBrowseView : UserControl
{
    private LedgerGridViewModel? _hooked;

    public LedgerBrowseView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private LedgerGridViewModel Vm => (LedgerGridViewModel)DataContext;

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_hooked != null) _hooked.ActionMessage -= OnActionMessage;
        _hooked = e.NewValue as LedgerGridViewModel;
        if (_hooked != null)
        {
            _hooked.ActionMessage += OnActionMessage;
            if (_hooked.IsAdminReview)
            {
                TitleText.Text = "台账明细 · 本团队总库（管理员可编辑）";
                HintText.Text = "本团队管理员：可直接在表格中增删改，改动立即生效。改本组他人名下的记录会同时在『通知』里告知对应销售（无需其确认）。选中行可『标记复核』提醒对方。（跨团队互不可见）";
            }
        }
    }

    // 用 SetCurrentValue 而非直接赋值，避免清除 StatusText 的 OneWay 绑定（否则计数/校验提示会僵住）
    private void OnActionMessage(string msg) => StatusText.SetCurrentValue(TextBlock.TextProperty, msg);

    // ——— 管理员编辑 ———
    private void OnAdd(object sender, RoutedEventArgs e)
    {
        var row = Vm.AddRow();
        Grid.ScrollIntoView(row);
        Grid.SelectedItem = row;
    }

    private void OnDeleteRows(object sender, RoutedEventArgs e)
    {
        var selected = SelectedRows();
        if (selected.Count == 0) { MessageBox.Show("请先选中要删除的行。", "提示", MessageBoxButton.OK, MessageBoxImage.Information); return; }
        if (MessageBox.Show($"确认对选中的 {selected.Count} 条发起删除？\n本人名下立即软删，他人名下将提交对方确认。",
                "删除", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        Vm.Delete(selected);
    }

    private void OnMark(object sender, RoutedEventArgs e)
    {
        var selected = SelectedRows();
        if (selected.Count == 0) { MessageBox.Show("请先选中要标记的行。", "提示", MessageBoxButton.OK, MessageBoxImage.Information); return; }
        var note = TextPromptDialog.Ask(Window.GetWindow(this)!, "标记复核",
            $"给选中的 {selected.Count} 条记录发送复核提醒（销售确认后消失）。备注：", "请核对这条记录。");
        if (note == null) return;
        Vm.MarkRows(selected, note);
    }

    private List<ContractRow> SelectedRows()
    {
        var rows = Grid.SelectedCells.Select(c => c.Item).OfType<ContractRow>().Distinct().ToList();
        if (Grid.SelectedItem is ContractRow single && !rows.Contains(single)) rows.Add(single);
        return rows;
    }

    // 编辑一行离开时：管理员模式下按归属决定直改或提案
    private void OnRowEdited(object sender, DataGridRowEditEndingEventArgs e)
    {
        if (!Vm.IsAdminReview || e.EditAction != DataGridEditAction.Commit) return;
        if (e.Row.Item is ContractRow row)
            Dispatcher.BeginInvoke(new Action(() => Vm.SaveRow(row)), System.Windows.Threading.DispatcherPriority.Background);
    }

    private async void OnImport(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "选择合同台账 Excel 文件",
            Filter = "Excel 台账 (*.xlsx)|*.xlsx",
            CheckFileExists = true,
        };
        if (dlg.ShowDialog() != true) return;

        string path = dlg.FileName;

        // 先列出该文件里的台账分表，让用户按团队只导本团队那部分（多分表时才弹选择框）。
        // 这一步是关键：整份多团队 Excel 若整体导入当前团队，会把别团队的数据也倒进来。
        List<string> sheets;
        try { sheets = new Weitong.Ledger.Data.Import.ExcelImporter().ListLedgerSheets(path); }
        catch (Exception ex) { MessageBox.Show("读取分表失败：\n" + ex.Message, "错误", MessageBoxButton.OK, MessageBoxImage.Error); return; }
        if (sheets.Count == 0) { MessageBox.Show("这个文件里没有找到台账分表（分表名需包含\"台账\"）。", "提示", MessageBoxButton.OK, MessageBoxImage.Information); return; }
        var chosen = SheetPickerDialog.Ask(Window.GetWindow(this), sheets);
        if (chosen == null || chosen.Count == 0) return;

        // 选完分表再问落库策略：覆盖现有（幂等更新）还是添加为新记录（一律追加不覆盖）。取消则不导入。
        var mode = ImportModeDialog.Ask(Window.GetWindow(this), path);
        if (mode == null) return;

        var vm = Vm;
        SetBusy(true, "正在导入，请稍候…");
        try
        {
            // 解析 + 落库放后台线程，避免卡住 UI 线程（导入卡死的直接原因）；
            // 拿到结果后回到 UI 线程刷新表格。
            var outcome = await Task.Run(() => vm.ImportExcelToStore(path, mode.Value, chosen));
            vm.LoadFrom(outcome.Data);
            string modeText = mode == Weitong.Ledger.Data.Import.ImportMode.AppendNew ? "添加为新记录" : "覆盖现有";
            MessageBox.Show($"导入成功（{modeText}）：{outcome.Imported} 条已写入总库。\n数据质量提示：{outcome.Anomalies} 项（见导出的详细报告）。",
                "导入完成", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            // 展开内层异常：EF 的 DbUpdateException 外层信息很笼统（"保存实体变更时出错"），
            // 真正原因（如 UNIQUE/NOT NULL 约束）在 InnerException 里，务必一并显示，便于排查。
            var sb = new System.Text.StringBuilder("导入失败：\n").Append(ex.Message);
            for (var inner = ex.InnerException; inner != null; inner = inner.InnerException)
                sb.Append("\n· ").Append(inner.Message);
            MessageBox.Show(sb.ToString(), "错误", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally { SetBusy(false); }
    }

    /// <summary>切换"正在导入"遮罩（可视化进度并挡住误操作）。</summary>
    private void SetBusy(bool on, string? text = null)
    {
        if (text != null) BusyText.Text = text;
        BusyOverlay.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnExport(object sender, RoutedEventArgs e) => LedgerGridView.ExportGrid(Vm);
}
