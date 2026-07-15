using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using Weitong.Ledger.Core;
using Weitong.Ledger.Data.Import;

namespace Weitong.Ledger.App.Views;

/// <summary>
/// 团队年度目标编辑对话框：手动填写或从「填报表单」Excel 一键导入。
/// 结果以「分」通过 <see cref="RevenueCents"/> 等属性回传；<see cref="Year"/> 为对标年度。
/// </summary>
public partial class TeamTargetDialog : Window
{
    private readonly string _teamName;
    private IReadOnlyList<TeamTargetRow> _rows = Array.Empty<TeamTargetRow>();

    public int Year { get; }
    public long RevenueCents { get; private set; }
    public long ProfitCents { get; private set; }
    public long CostCeilingCents { get; private set; }

    public TeamTargetDialog(string teamName, int year, long revCents, long profitCents, long costCents, bool canUpload)
    {
        InitializeComponent();
        _teamName = teamName;
        Year = year;

        TitleText.Text = $"{teamName} · {year} 年度目标";
        SaveBtn.Content = canUpload ? "保存并同步" : "保存";
        RevBox.Text = Wan(revCents);
        ProfitBox.Text = Wan(profitCents);
        CostBox.Text = Wan(costCents);
        Loaded += (_, _) => RevBox.Focus();
    }

    private static string Wan(long cents) => cents == 0 ? "" : Money.ToWan(cents).ToString("0.####", CultureInfo.InvariantCulture);

    private void OnImport(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "选择「本部各领域填报表单」Excel",
            Filter = "Excel 填报表单 (*.xlsx)|*.xlsx",
        };
        if (dlg.ShowDialog() != true) return;

        IReadOnlyList<TeamTargetRow> rows;
        try { rows = new TargetFormImporter().ParseFile(dlg.FileName); }
        catch (Exception ex)
        {
            MessageBox.Show("解析失败：\n" + ex.Message, "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }
        if (rows.Count == 0)
        {
            MessageBox.Show("未在该表中找到含「收入指标 / 利润指标 / 成本控制指标」的表格。\n" +
                            "请确认选择的是含「市场策划进展表」那一页的填报表单。",
                "未识别到指标", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        _rows = rows;
        TeamCombo.ItemsSource = rows.Select(r => r.TeamName).ToList();
        TeamCombo.SelectedIndex = BestMatch(rows, _teamName);   // 触发 OnTeamChanged 填值
        TeamPanel.Visibility = Visibility.Visible;

        int formYear = rows[0].Year;
        ImportInfo.Text = $"已读取 {rows.Count} 个团队/领域（表内年度 {formYear}）。已自动选中「{rows[TeamCombo.SelectedIndex].TeamName}」，可在下拉中改选，确认后点保存。"
                        + (formYear != Year ? $"\n注意：表内年度为 {formYear}，本次将写入 {Year} 年度目标。" : "");
    }

    private void OnTeamChanged(object sender, SelectionChangedEventArgs e)
    {
        int i = TeamCombo.SelectedIndex;
        if (i < 0 || i >= _rows.Count) return;
        var row = _rows[i];
        RevBox.Text = Wan(row.RevenueCents);
        ProfitBox.Text = Wan(row.ProfitCents);
        CostBox.Text = Wan(row.CostCeilingCents);
    }

    /// <summary>预选与当前团队名最匹配的一行（共同前缀字数 + 是否含团队名首二字）。</summary>
    private static int BestMatch(IReadOnlyList<TeamTargetRow> rows, string team)
    {
        int best = 0, bestScore = -1;
        string head = team.Length >= 2 ? team.Substring(0, 2) : team;
        for (int i = 0; i < rows.Count; i++)
        {
            var n = rows[i].TeamName;
            int score = CommonPrefixLen(n, team);
            if (head.Length > 0 && n.Contains(head)) score += 2;
            if (score > bestScore) { bestScore = score; best = i; }
        }
        return best;
    }

    private static int CommonPrefixLen(string a, string b)
    {
        int n = Math.Min(a.Length, b.Length), i = 0;
        while (i < n && a[i] == b[i]) i++;
        return i;
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        if (!TryWan(RevBox.Text, out var rev) || !TryWan(ProfitBox.Text, out var prof) || !TryWan(CostBox.Text, out var cost))
        {
            MessageBox.Show("请把三项指标都填成有效数字（单位万元，可留 0）。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        RevenueCents = Money.FromWan(rev);
        ProfitCents = Money.FromWan(prof);
        CostCeilingCents = Money.FromWan(cost);
        DialogResult = true;
    }

    private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;

    /// <summary>解析万元输入：容忍千分位、全角逗号、¥、空白；空 → 0。</summary>
    private static bool TryWan(string? text, out double wan)
    {
        wan = 0;
        if (string.IsNullOrWhiteSpace(text)) return true; // 空视为 0（未设）
        var s = text.Replace(",", "").Replace("，", "").Replace("¥", "").Replace(" ", "").Trim();
        return double.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out wan);
    }
}
