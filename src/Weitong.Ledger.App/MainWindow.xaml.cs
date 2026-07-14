using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Weitong.Ledger.App.Services;
using Weitong.Ledger.App.ViewModels;
using Weitong.Ledger.App.Views;
using Weitong.Ledger.Data.Db;
using Weitong.Ledger.Data.Import;

namespace Weitong.Ledger.App;

public partial class MainWindow : Window
{
    private string _dataDir = "";
    private AppConfig _config = new();
    private CosSettings _cos = new();
    private RoleService _roles = null!;
    private ReviewService _review = null!;
    private LedgerStore _store = null!;
    private DashboardViewModel _dashVm = null!;
    private LedgerGridViewModel _entryVm = null!;
    private LedgerGridViewModel _browseVm = null!;
    private PendingReviewViewModel _pendingVm = null!;
    private OverviewView _overview = null!;
    private LedgerGridView _entry = null!;
    private LedgerBrowseView _browse = null!;
    private PendingReviewView _pending = null!;
    private SettingsView _settings = null!;
    private MyAchievementView _myView = null!;
    private string _person = "未命名";

    public MainWindow()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _dataDir = AppConfig.DefaultDir;
        _config = AppConfig.Load(_dataDir);

        // 机器码识别身份（替代登录）：首次或换机 → 弹身份确认
        var machine = MachineId.Get();
        if (!_config.IsIdentitySet || _config.MachineCode != machine)
        {
            var dlg = new IdentityDialog(_config.PersonName) { Owner = this };
            if (dlg.ShowDialog() == true)
            {
                _config.PersonName = dlg.PersonName;
                _config.TeamName = dlg.TeamName;
                _config.MachineCode = machine;
                _config.PersonCode = MachineId.Short();
                _config.BoundAtUtc = DateTime.UtcNow;
                _config.Save(_dataDir);
            }
            else if (!_config.IsIdentitySet)
            {
                _config.PersonName = "未命名";
            }
        }
        _person = _config.PersonName ?? "未命名";
        NavTeam.Text = _config.TeamName ?? "行业市场组";
        IdentityText.Text = $"当前使用人：{_person} · {_config.TeamName}";

        // 打开加密库；库为空则尝试从就近 xlsx 首次灌库
        _store = new LedgerStore(Path.Combine(_dataDir, "data"));
        if (_store.ContractCount() == 0)
        {
            var xlsx = FindLedgerFile();
            if (xlsx != null)
            {
                var res = new ExcelImporter().ImportFile(xlsx, DateTime.UtcNow, _person);
                _store.SeedFromImport(res, _person);
                _store.WriteAudit("Seed", _person, "Contract", $"首次从 {Path.GetFileName(xlsx)} 灌库");
            }
        }
        _store.WriteAudit("Open", _person, note: $"机器码 {MachineId.Short()}");

        _cos = CosSettings.Load();       // 后台 COS 配置（cos.json，销售无感知）
        _roles = new RoleService(_cos);  // 按姓名判定管理员

        _dashVm = new DashboardViewModel();
        BuildViews();
        DataContext = _dashVm;          // Shell 的口径切换绑定到总览 VM
        ReloadAll();
        ContentHost.Content = _overview;
        UpdateBadge();

        SyncInBackground();              // 打开即后台自动同步（含审批通道）
    }

    private void BuildViews()
    {
        _person = _config.PersonName ?? "未命名";
        var personCode = _config.PersonCode ?? MachineId.Short();
        _review = new ReviewService(_store, _person, personCode, _roles);

        _entryVm = new LedgerGridViewModel(_store, _person, personalOnly: true);
        _browseVm = _roles.IsAdmin(_person)
            ? new LedgerGridViewModel(_store, _person, personalOnly: false, _review)   // 管理员：可编辑 + 提案
            : new LedgerGridViewModel(_store, _person, personalOnly: false);            // 销售：只读浏览
        _browseVm.NeedsSync += OnBrowseNeedsSync;
        _browseVm.ActionMessage += m => StatusBar.Text = m;

        _pendingVm = new PendingReviewViewModel(_review);
        _pendingVm.Changed += OnReviewDecided;

        _dashVm.TeamName = string.IsNullOrWhiteSpace(_config.TeamName) ? "行业市场组" : _config.TeamName!;
        _overview = new OverviewView { DataContext = _dashVm };
        _entry = new LedgerGridView { DataContext = _entryVm };
        _browse = new LedgerBrowseView { DataContext = _browseVm };
        _pending = new PendingReviewView { DataContext = _pendingVm };
        _settings = new SettingsView(_config, OnIdentityChanged);
        _myView = new MyAchievementView { DataContext = new MyAchievementViewModel(_store, personCode, _person, DateTime.Now.Year) };
    }

    private SyncPayload BuildPayload()
    {
        var contracts = _store.GetAllContracts();
        return new SyncPayload
        {
            PersonCode = _config.PersonCode ?? MachineId.Short(),
            PersonName = _person,
            TeamName = _config.TeamName,
            MachineCode = MachineId.Get(),
            ExportedUtc = DateTime.UtcNow,
            Count = contracts.Count,
            Contracts = contracts,
        };
    }

    /// <summary>后台静默：上传本人数据 + 审批通道，拉取全组汇总 + 收发审批项/决策。</summary>
    private void SyncInBackground()
    {
        if (!_cos.IsConfigured) { UpdateBadge(); return; }
        var payload = BuildPayload();                       // UI 线程
        var reviewBundle = _review.BuildOutgoingBundle();   // UI 线程（读库）
        var decisionBundle = _review.BuildDecisionBundle(); // UI 线程（读库）
        bool isAdmin = _review.IsAdmin;
        var cos = _cos;
        _ = Task.Run(() =>
        {
            try
            {
                var client = new CloudSync(cos);
                try   // 上传失败（限流/瞬时错误）不应阻断下面的拉取
                {
                    client.UploadMine(payload);
                    if (isAdmin) client.UploadReview(reviewBundle);                   // 管理员上传发起的提案
                    if (decisionBundle.Decisions.Count > 0) client.UploadDecisions(decisionBundle);
                }
                catch { /* 下次刷新会重试上传 */ }
                var (merged, payloads) = client.DownloadAll();
                var reviews = client.DownloadReviews();
                var decisions = client.DownloadDecisions();
                Dispatcher.Invoke(() =>
                {
                    _review.Ingest(reviews, decisions);      // 写库在 UI 线程
                    _dashVm.Load(merged, $"全组 · {payloads.Count} 名成员");
                    _browseVm.LoadFrom(merged);              // 台账明细 = 云端合并的全组总库
                    StatusBar.Text = $"使用人 {_person} · 全组 {merged.Count} 条";
                    UpdateBadge();
                    _pendingVm.Load();
                });
            }
            catch
            {
                Dispatcher.Invoke(() => { StatusBar.Text = $"使用人 {_person} · 本地 {payload.Count} 条"; UpdateBadge(); });
            }
        });
    }

    /// <summary>仅上传本人数据 + 发起的提案（管理员编辑后用，不重载表格以免打断操作）。</summary>
    private void PushMineInBackground()
    {
        if (!_cos.IsConfigured) return;
        var payload = BuildPayload();
        var reviewBundle = _review.BuildOutgoingBundle();
        var cos = _cos;
        _ = Task.Run(() =>
        {
            try
            {
                var client = new CloudSync(cos);
                client.UploadMine(payload);
                client.UploadReview(reviewBundle);
            }
            catch { /* 静默：下次刷新会重试 */ }
        });
    }

    private void OnBrowseNeedsSync() { UpdateBadge(); PushMineInBackground(); }

    /// <summary>销售在待确认页作出决策后：上传决策 + 数据、重拉汇总、刷新红点。</summary>
    private void OnReviewDecided() { UpdateBadge(); SyncInBackground(); }

    private void UpdateBadge()
    {
        int n = 0;
        try { n = _review?.PendingCount() ?? 0; } catch { n = 0; }
        PendingBadgeText.Text = n > 99 ? "99+" : n.ToString();
        PendingBadge.Visibility = n > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnIdentityChanged(string name, string team)
    {
        _person = name;
        NavTeam.Text = team;
        IdentityText.Text = $"当前使用人：{name} · {team}";
        BuildViews();          // 用新姓名重建各页（含重判管理员身份）
        ReloadAll();
        UpdateBadge();
        NavOverview.IsChecked = true;
    }

    private void ReloadAll()
    {
        var contracts = _store.GetAllContracts();
        _dashVm.Load(contracts, "本机数据");
        _browseVm.LoadFrom(contracts);   // 台账明细=总库（同步后由云端合并覆盖）
        _entryVm.Reload();               // 个人录入=我的
        _pendingVm.Load();
        UpdateBadge();
        StatusBar.Text = $"使用人 {_person} · 共 {contracts.Count} 条";
    }

    private void OnNav(object sender, RoutedEventArgs e)
    {
        if (_overview == null) return; // 初始化期间 IsChecked 触发的 Checked 忽略
        if (sender is not RadioButton rb || rb.Tag is not string key) return;
        ContentHost.Content = key switch
        {
            "overview" => _overview,
            "browse" => _browse,
            "entry" => ReloadEntry(),
            "mine" => ReloadMine(),
            "pending" => ReloadPending(),
            "settings" => _settings,
            _ => Placeholder(key),
        };
    }

    private UserControl ReloadEntry() { _entryVm.Reload(); return _entry; }

    private UserControl ReloadMine()
    {
        ((MyAchievementViewModel)_myView.DataContext).Load();
        return _myView;
    }

    private UserControl ReloadPending() { _pendingVm.Load(); UpdateBadge(); return _pending; }

    private static UserControl Placeholder(string key)
    {
        return new UserControl
        {
            Content = new TextBlock
            {
                Text = "该模块开发中。",
                Margin = new Thickness(28),
                FontSize = 14,
                TextWrapping = TextWrapping.Wrap,
                Foreground = (System.Windows.Media.Brush)Application.Current.Resources["B.TextSecondary"],
            }
        };
    }

    private void OnRefresh(object sender, RoutedEventArgs e) { ReloadAll(); SyncInBackground(); }

    private static string? FindLedgerFile()
    {
        bool IsLedger(string f) => !Path.GetFileName(f).StartsWith("~$") &&
                                   Path.GetExtension(f).Equals(".xlsx", StringComparison.OrdinalIgnoreCase);
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var hit = dir.EnumerateFiles("*.xlsx").FirstOrDefault(f => IsLedger(f.FullName));
            if (hit != null) return hit.FullName;
            dir = dir.Parent;
        }
        return null;
    }
}
