using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Weitong.Ledger.App.Services;
using Weitong.Ledger.App.ViewModels;
using Weitong.Ledger.App.Views;
using Weitong.Ledger.Core;
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
        SeedTeamTargetIfNeeded();        // 老库升级：补一次 2026 历史团队指标，保证看板数字不变且可编辑

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
        _overview.EditTargetRequested += OnEditTeamTarget;
        // 云端已配置时仅管理员可改（改动会同步全组）；未配置云同步时允许本机编辑（仅本机生效）
        _overview.EnableTargetEditing(_review.IsAdmin || !_cos.IsConfigured);
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
                var teamTargets = client.DownloadTeamTargets();   // 全组统一的团队目标（可能为 null）
                Dispatcher.Invoke(() =>
                {
                    _review.Ingest(reviews, decisions);      // 写库在 UI 线程
                    if (teamTargets != null) ApplyDownloadedTeamTargets(teamTargets); // 落本地库（按时间裁决）
                    MaybeReuploadTeamTargets(teamTargets);   // 管理员：本地更新则补传，自愈失败的上传
                    _dashVm.Load(merged, $"全组 · {payloads.Count} 名成员");
                    ApplyTeamTarget();                       // 套用（云端有则用云端，否则用本地/种子）
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
        ApplyTeamTarget();               // 按 团队×当前年 读库套用目标（否则显示"未设"）
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

    // ————————— 团队目标：本地读写 + 云端统一同步（按修改时间「谁新用谁」裁决） —————————

    /// <summary>迁移种子的基准时间：足够旧，保证任何真实编辑（本机或云端）都能盖过它。</summary>
    private static readonly DateTime MigrationBaselineUtc = new(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>团队目标的归属键（团队名，空则默认）。</summary>
    private string TeamKey => string.IsNullOrWhiteSpace(_config.TeamName) ? "行业市场组" : _config.TeamName!.Trim();

    private static DateTime AsUtc(DateTime dt) => DateTime.SpecifyKind(dt, DateTimeKind.Utc);

    /// <summary>老库升级迁移：若「行业」本团队尚无 2026 团队指标，补一条与原写死值一致的历史指标。
    /// 用很旧的基准时间落库，确保升级后看板数字不变，但任何真实编辑都能覆盖它。</summary>
    private void SeedTeamTargetIfNeeded()
    {
        if (!TeamKey.Contains("行业")) return;                    // 只给本部行业团队补 2026 历史值
        if (_store.GetTeamTarget(TeamKey, 2026) != null) return;
        _store.SaveTeamTarget(TeamKey, 2026, Money.FromWan(50131), Money.FromWan(42449), Money.FromWan(7682),
            "系统迁移", MigrationBaselineUtc);
    }

    /// <summary>按 团队×当前年 从库读入目标并即时刷新看板 KPI；无则显示「未设」。</summary>
    private void ApplyTeamTarget()
    {
        var t = _store.GetTeamTarget(TeamKey, DateTime.Now.Year);
        _dashVm.ApplyTarget(t?.RevenueTargetCents ?? 0, t?.ProfitTargetCents ?? 0, t?.CostCeilingCents ?? 0);
    }

    /// <summary>总览「目标设置」：打开编辑对话框 → 落库 → 刷新看板 →（云端已配置则）后台同步全组。</summary>
    private void OnEditTeamTarget(object? sender, EventArgs e)
    {
        int year = DateTime.Now.Year;
        var cur = _store.GetTeamTarget(TeamKey, year);
        bool canUpload = _cos.IsConfigured;
        var dlg = new TeamTargetDialog(TeamKey, year,
            cur?.RevenueTargetCents ?? 0, cur?.ProfitTargetCents ?? 0, cur?.CostCeilingCents ?? 0, canUpload) { Owner = this };
        if (dlg.ShowDialog() != true) return;

        _store.SaveTeamTarget(TeamKey, year, dlg.RevenueCents, dlg.ProfitCents, dlg.CostCeilingCents, _person);
        _store.WriteAudit("Target", _person, "TeamTarget",
            $"设定团队目标 {TeamKey} {year}：收入{Money.FormatWan(dlg.RevenueCents)}/利润{Money.FormatWan(dlg.ProfitCents)}/成本上限{Money.FormatWan(dlg.CostCeilingCents)}");
        ApplyTeamTarget();                 // 立即刷新看板
        UploadTeamTargetsInBackground();   // 同步全组（仅云端已配置时；失败会在后续同步自动补传）

        MessageBox.Show(canUpload
            ? "团队目标已保存。联网同步后会自动推送给全组（各端刷新即生效）；若本次网络不通，下次同步会自动补传。"
            : "团队目标已保存（本机未配置云同步，仅本机看板生效）。",
            "已保存", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    /// <summary>把本团队的全部年度目标打包上传。云端为单一权威对象，直接覆盖；每年度带最后修改时间供裁决。</summary>
    private void UploadTeamTargetsInBackground()
    {
        if (!_cos.IsConfigured) return;
        var team = TeamKey;
        var rows = _store.GetTeamTargets(team);
        if (rows.Count == 0) return;
        var bundle = new TeamTargetBundle
        {
            TeamName = team,
            ByName = _person,
            ByCode = _config.PersonCode ?? MachineId.Short(),
            UpdatedUtc = AsUtc(rows.Max(t => t.UpdatedAt)),      // 全包最新编辑时间
            Entries = rows.Select(t => new TeamTargetEntry
            {
                Year = t.Year,
                RevenueTargetCents = t.RevenueTargetCents,
                ProfitTargetCents = t.ProfitTargetCents,
                CostCeilingCents = t.CostCeilingCents,
                UpdatedUtc = AsUtc(t.UpdatedAt),
            }).ToList(),
        };
        var cos = _cos;
        _ = Task.Run(() => { try { new CloudSync(cos).UploadTeamTargets(bundle); } catch { /* 失败：下次同步 MaybeReuploadTeamTargets 会补传 */ } });
    }

    /// <summary>把云端下载的团队目标落本地库：仅当云端某年度<b>更新</b>（或本地尚无）才覆盖，
    /// 本地更新的编辑保留不动，避免旧云值回滚管理员刚改、尚未上传成功的新值。</summary>
    private void ApplyDownloadedTeamTargets(TeamTargetBundle b)
    {
        foreach (var en in b.Entries)
        {
            var local = _store.GetTeamTarget(TeamKey, en.Year);
            if (local != null && AsUtc(local.UpdatedAt) >= en.UpdatedUtc) continue;  // 本地不更旧 → 保留本地
            _store.SaveTeamTarget(TeamKey, en.Year, en.RevenueTargetCents, en.ProfitTargetCents, en.CostCeilingCents,
                "云端同步:" + b.ByName, en.UpdatedUtc == default ? DateTime.UtcNow : en.UpdatedUtc);  // 保留云端时间，裁决可比较
        }
    }

    /// <summary>管理员：本地团队目标比云端新（或云端尚无）时补传，自愈上次失败/未传的编辑。</summary>
    private void MaybeReuploadTeamTargets(TeamTargetBundle? cloud)
    {
        if (!_cos.IsConfigured || !_review.IsAdmin) return;
        var rows = _store.GetTeamTargets(TeamKey);
        if (rows.Count == 0) return;
        var localMax = rows.Max(t => AsUtc(t.UpdatedAt));
        var cloudMax = cloud?.UpdatedUtc ?? DateTime.MinValue;
        if (localMax > cloudMax) UploadTeamTargetsInBackground();
    }

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
