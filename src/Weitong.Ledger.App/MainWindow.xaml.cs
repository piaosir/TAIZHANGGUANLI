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

    // 右上角时钟 / 数据新鲜度 / 时钟偏差提醒
    private System.Windows.Threading.DispatcherTimer? _clockTimer;
    private DateTime? _lastSyncAt;                 // 上次成功拉取全组汇总的时刻
    private double? _clockSkewSeconds;             // 本机时钟相对网络的偏差（本机快为正）；null=未测

    // 普通编辑即时上云的防抖器：停手片刻后合并成一次上传，避免每改一行发一次 COS 请求
    private System.Windows.Threading.DispatcherTimer? _pushDebounce;

    // 刷新/同步防重入：同步进行中时狂点刷新不再并发下载，最多在结束后补跑一次
    private bool _syncing;
    private bool _syncQueued;

    // 当前已打开团队库的令牌。后台同步回写前用它比对：同步期间若用户切了团队，作废旧团队那次结果，避免写错库。
    private string _currentTeamToken = "";

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

        _cos = CosSettings.Load();       // 后台 COS 配置（cos.json，销售无感知）
        _roles = new RoleService(_cos);  // 人员名册：姓名→团队→角色（判管理员 + 团队分权）

        // 显示的团队以名册为准（管理员统一维护）；名册没有该人则回退到本机自填
        NavTeam.Text = MyTeam ?? "行业市场组";
        IdentityText.Text = $"当前使用人：{_person} · {MyTeam ?? _config.TeamName}";

        // 一团队一库：把库开在「当前团队身份」对应的物理库上（不同团队互不打开对方的库）。
        // 不再从内置 xlsx 自动灌库——那份 Excel 是多团队混合的，自动灌会把别团队数据倒进当前团队；
        // 数据一律靠「当前团队身份」手动导入/录入进入本团队库（用户模型：以什么团队身份填就进哪个库）。
        OpenTeamStore();

        _dashVm = new DashboardViewModel();
        BuildViews();
        DataContext = _dashVm;          // Shell 的口径切换绑定到总览 VM
        ReloadAll();
        ContentHost.Content = _overview;
        UpdateBadge();

        SyncInBackground();              // 打开即后台自动同步（含审批通道）

        StartClock();                    // 右上角实时时钟 + 上次同步时间
        EnsureTimeSyncInBackground();    // 触发系统对时并检测本机时钟偏差
    }

    private void BuildViews()
    {
        _person = _config.PersonName ?? "未命名";
        var personCode = _config.PersonCode ?? MachineId.Short();
        _review = new ReviewService(_store, _person, personCode, _roles);

        _entryVm = new LedgerGridViewModel(_store, _person, personalOnly: true);
        _entryVm.NeedsSync += OnBrowseNeedsSync;   // 个人录入删除/结构变更后即时上云（墓碑随之广播）
        _browseVm = _roles.IsAdmin(_person)
            ? new LedgerGridViewModel(_store, _person, personalOnly: false, _review)   // 管理员：可编辑 + 提案
            : new LedgerGridViewModel(_store, _person, personalOnly: false);            // 销售：只读浏览
        _browseVm.NeedsSync += OnBrowseNeedsSync;
        _browseVm.ActionMessage += m => StatusBar.Text = m;

        _pendingVm = new PendingReviewViewModel(_review);
        _pendingVm.Changed += OnReviewDecided;

        _dashVm.TeamName = MyTeam ?? "行业市场组";
        _overview = new OverviewView { DataContext = _dashVm };
        _overview.EditTargetRequested += OnEditTeamTarget;
        // 云端已配置时仅管理员可改（改动会同步全组）；未配置云同步时允许本机编辑（仅本机生效）
        _overview.EnableTargetEditing(_review.IsAdmin || !_cos.IsConfigured);
        _entry = new LedgerGridView { DataContext = _entryVm };
        _browse = new LedgerBrowseView { DataContext = _browseVm };
        _pending = new PendingReviewView { DataContext = _pendingVm };
        _settings = new SettingsView(_config, OnIdentityChanged);
        var myVm = new MyAchievementViewModel(_store, _person, DateTime.Now.Year);
        myVm.TargetSaved += SchedulePush;   // 个人目标改完即时随人上云（防抖合并）
        _myView = new MyAchievementView { DataContext = myVm };
    }

    private SyncPayload BuildPayload()
    {
        // 一团队一库：本地库本就是本团队的数据，整库上传即可（含墓碑：删除靠墓碑传播到本团队其它设备）
        var contracts = _store.GetAllContractsForSync();
        var pt = _store.GetPersonTarget(_person, DateTime.Now.Year);   // 本人个人目标随包上云（随人走）
        return new SyncPayload
        {
            PersonCode = _config.PersonCode ?? MachineId.Short(),
            PersonName = _person,
            TeamName = MyTeam ?? _config.TeamName,          // 携带名册认定的团队，供各端按团队分权
            MachineCode = MachineId.Get(),
            ExportedUtc = DateTime.UtcNow,
            Count = contracts.Count(c => !c.IsDeleted),     // 展示用条数只算存活记录
            Contracts = contracts,
            PersonalTarget = pt == null ? null : new PersonTargetDto
            {
                Year = pt.Year,
                RevenueTargetCents = pt.RevenueTargetCents,
                ProfitTargetCents = pt.ProfitTargetCents,
                CostCeilingCents = pt.CostCeilingCents,
                UpdatedUtc = AsUtc(pt.UpdatedAt),
            },
        };
    }

    /// <summary>后台静默：上传本人数据 + 审批通道，拉取全组汇总 + 收发审批项/决策。</summary>
    private void SyncInBackground()
    {
        if (!_cos.IsConfigured) { UpdateBadge(); return; }
        if (_syncing) { _syncQueued = true; return; }       // 已在同步：狂点合并为一次待跑，不并发多次下载
        _syncing = true;
        SetRefreshBusy(true);
        var payload = BuildPayload();                       // UI 线程
        var reviewBundle = _review.BuildOutgoingBundle();   // UI 线程（读库）
        var decisionBundle = _review.BuildDecisionBundle(); // UI 线程（读库）
        bool isAdmin = _review.IsAdmin;
        var teamKey = TeamKey;                              // 本团队键（后台线程用，先在 UI 线程取好）
        var syncTeamToken = TeamPartition.Token(teamKey);   // 本次同步归属的团队；回写前比对，防切团队后写错库
        var cos = _cos;
        _ = Task.Run(() =>
        {
            try
            {
                var client = new CloudSync(cos, teamKey);   // 本团队分区的客户端：只读写本团队前缀
                try   // 上传失败（限流/瞬时错误）不应阻断下面的拉取
                {
                    client.UploadMine(payload);
                    if (isAdmin) client.UploadReview(reviewBundle);                   // 管理员上传发起的提案
                    if (decisionBundle.Decisions.Count > 0) client.UploadDecisions(decisionBundle);
                }
                catch { /* 下次刷新会重试上传 */ }
                var (merged, payloads) = client.DownloadAll();   // 只会拉到本团队分区里的成员（物理隔离）
                var reviews = client.DownloadReviews();
                var decisions = client.DownloadDecisions();
                var teamTargets = client.DownloadTeamTargets();   // 本团队的团队目标（可能为 null）
                Dispatcher.Invoke(() =>
                {
                    if (syncTeamToken != _currentTeamToken) return;   // 同步期间用户切了团队：本次(旧团队)结果作废，绝不写进新团队库
                    _review.Ingest(reviews, decisions);      // 写库在 UI 线程
                    _store.ApplyMergedFromCloud(merged, "云端合并"); // 本团队库回写(含墓碑)：删除生效并收敛，下次不再广播旧值
                    // 个人目标随人走：每人回填自己的（管理员回填本团队全员，便于将来按人汇总），LWW 裁决
                    foreach (var pl in payloads)
                        if (pl.PersonalTarget is { } ptt && !string.IsNullOrWhiteSpace(pl.PersonName)
                            && (isAdmin || string.Equals(pl.PersonName.Trim(), _person.Trim(), StringComparison.Ordinal)))
                            _store.ApplyDownloadedPersonTarget(pl.PersonName.Trim(), ptt.Year,
                                ptt.RevenueTargetCents, ptt.ProfitTargetCents, ptt.CostCeilingCents, AsUtc(ptt.UpdatedUtc), "云端同步");
                    var live = merged.Where(c => !c.IsDeleted).ToList(); // 展示只取存活记录，墓碑不显示
                    if (teamTargets != null) ApplyDownloadedTeamTargets(teamTargets); // 落本地库（按时间裁决）
                    MaybeReuploadTeamTargets(teamTargets);   // 管理员：本地更新则补传，自愈失败的上传
                    _dashVm.Load(live, TeamLabel(live.Count));
                    ApplyTeamTarget();                       // 套用（云端有则用云端，否则用本地/种子）
                    _browseVm.LoadFrom(live);                // 台账明细 = 本团队总库
                    StatusBar.Text = $"使用人 {_person} · {MyTeam ?? "本组"} {live.Count} 条";
                    UpdateBadge();
                    _pendingVm.Load();
                    _lastSyncAt = DateTime.Now;   // 数据新鲜度：右上角显示「同步于 HH:mm」
                    UpdateClockText();
                });
            }
            catch
            {
                Dispatcher.Invoke(() => { StatusBar.Text = $"使用人 {_person} · 本地 {payload.Count} 条"; UpdateBadge(); });
            }
            finally
            {
                Dispatcher.Invoke(() =>
                {
                    _syncing = false;
                    SetRefreshBusy(false);
                    if (_syncQueued) { _syncQueued = false; SyncInBackground(); }   // 同步期间又点了刷新：补跑一次，保证覆盖到最新
                });
            }
        });
    }

    /// <summary>仅上传本人数据 + 发起的提案（管理员编辑后用，不重载表格以免打断操作）。</summary>
    private void PushMineInBackground()
    {
        if (!_cos.IsConfigured) return;
        var payload = BuildPayload();
        var reviewBundle = _review.BuildOutgoingBundle();
        bool isAdmin = _review.IsAdmin;
        var team = TeamKey;
        var cos = _cos;
        _ = Task.Run(() =>
        {
            try
            {
                var client = new CloudSync(cos, team);
                client.UploadMine(payload);
                if (isAdmin) client.UploadReview(reviewBundle);   // 仅管理员上传提案文件（销售端上传空提案无意义）
            }
            catch { /* 静默：下次刷新会重试 */ }
        });
    }

    private void OnBrowseNeedsSync() { UpdateBadge(); SchedulePush(); }

    /// <summary>编辑/删除后防抖上云：停手 2.5 秒再上传，把连续多行改动合并成一次 COS 请求。</summary>
    private void SchedulePush()
    {
        _pushDebounce ??= MakeDebounce(TimeSpan.FromSeconds(2.5), () => { _pushDebounce!.Stop(); PushMineInBackground(); });
        _pushDebounce.Stop();
        _pushDebounce.Start();
    }

    private static System.Windows.Threading.DispatcherTimer MakeDebounce(TimeSpan delay, Action onTick)
    {
        var t = new System.Windows.Threading.DispatcherTimer { Interval = delay };
        t.Tick += (_, _) => onTick();
        return t;
    }

    /// <summary>销售在通知页点"知道了"后：上传回执 + 数据、重拉汇总、刷新红点。</summary>
    private void OnReviewDecided() { UpdateBadge(); SyncInBackground(); }

    private void UpdateBadge()
    {
        int n = 0;
        try { n = _review?.PendingCount() ?? 0; } catch { n = 0; }
        SetBadge(n);
    }

    /// <summary>只更新红点 UI（计数已在后台取好），供 ReloadAll 复用，避免在 UI 线程再查库。</summary>
    private void SetBadge(int n)
    {
        PendingBadgeText.Text = n > 99 ? "99+" : n.ToString();
        PendingBadge.Visibility = n > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>同步进行中禁用刷新按钮并提示，防止狂点并发下载。</summary>
    private void SetRefreshBusy(bool busy)
    {
        if (RefreshBtn == null) return;
        RefreshBtn.IsEnabled = !busy;
        RefreshBtn.Content = busy ? "同步中…" : "刷新";
    }

    private void StartClock()
    {
        _clockTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _clockTimer.Tick += (_, _) => UpdateClockText();
        _clockTimer.Start();
        UpdateClockText();
    }

    /// <summary>刷新右上角时间信息：当前时间 · 上次同步；本机时钟偏差过大时标黄并给出修复指引。</summary>
    private void UpdateClockText()
    {
        if (ClockText == null) return;
        var now = DateTime.Now;
        string sync = _lastSyncAt is { } t ? $"同步于 {t:HH:mm}" : "未同步";
        bool skewed = _clockSkewSeconds is { } s && Math.Abs(s) >= 30;
        ClockText.Text = (skewed ? "⚠ " : "🕒 ") + $"{now:HH:mm:ss} · {sync}";
        ClockText.Foreground = new System.Windows.Media.SolidColorBrush(
            skewed ? System.Windows.Media.Color.FromRgb(0xFF, 0xC2, 0x4B)
                   : System.Windows.Media.Color.FromRgb(0x9D, 0xB4, 0xD6));
        ClockText.ToolTip = skewed
            ? $"本机时间与网络相差约 {Math.Abs(_clockSkewSeconds!.Value):F0} 秒，可能导致多端同步时新旧判断出错。\n" +
              "请在 Windows「设置 → 时间和语言 → 日期和时间」开启「自动设置时间」。"
            : _clockSkewSeconds is null ? "正在校时…" : "本机时间与网络基本一致。";
    }

    /// <summary>后台触发系统立即对时，并用网络时间检测本机时钟偏差（不阻塞 UI，失败静默）。</summary>
    private void EnsureTimeSyncInBackground()
    {
        _ = Task.Run(() =>
        {
            try   // 触发 Windows 立即对时（无权限 / 服务停用则忽略）
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("w32tm", "/resync /nowait")
                { CreateNoWindow = true, UseShellExecute = false, WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden });
            }
            catch { }

            var net = NetworkTime.TryGetUtc();
            if (net is { } utc)
            {
                double skew = (DateTime.UtcNow - utc).TotalSeconds;   // 本机快为正
                Dispatcher.Invoke(() => { _clockSkewSeconds = skew; UpdateClockText(); });
            }
        });
    }

    private void OnIdentityChanged(string name, string team)
    {
        // SettingsView 已把 _config.TeamName 更新为新团队，故 MyTeam 反映的是切换后的团队
        _person = name;
        NavTeam.Text = MyTeam ?? "行业市场组";
        IdentityText.Text = $"当前使用人：{name} · {MyTeam ?? team}";
        OpenTeamStore();       // 团队身份可能变了 → 切换到该团队的物理库（一团队一库，这正是"切团队没换库"的修复）
        BuildViews();          // 各 VM 引用新 _store，并按新姓名重判管理员
        ReloadAll();
        UpdateBadge();
        SyncInBackground();    // 拉本团队云端最新
        NavOverview.IsChecked = true;
    }

    /// <summary>一次重载所需的全部本地库数据。在后台线程读好后，回 UI 线程只做集合填充。</summary>
    private sealed record ReloadSnapshot(
        List<Contract> Contracts, List<Contract> Entry,
        List<ReviewItem> Incoming, List<ReviewItem> Outgoing,
        int Badge, Target? TeamTarget);

    /// <summary>重载所有页。加密库读取（SQLCipher 逐页解密是刷新卡顿的主因）全部放后台线程，
    /// 仅在最后回到 UI 线程填充各集合，避免刷新时界面冻结。</summary>
    private async void ReloadAll()
    {
        var person = _person;
        var teamKey = TeamKey;
        var year = DateTime.Now.Year;

        ReloadSnapshot snap;
        try
        {
            snap = await Task.Run(() => new ReloadSnapshot(
                _store.GetAllContracts(),          // 全组总库（最重）
                _store.GetContractsFor(person),    // 个人录入=我的
                _review.Incoming(),                // 通知·待我知晓
                _review.Outgoing(),                // 通知·我发起的
                _review.PendingCount(),            // 红点计数
                _store.GetTeamTarget(teamKey, year)));
        }
        catch (Exception ex)
        {
            StatusBar.Text = $"读取本地数据失败：{ex.Message}";
            return;
        }

        // 以下只操作内存集合 / 绑定属性，均在 UI 线程执行（不再碰库）
        // 本地库即本团队总库（物理隔离），直接展示，无需再按团队过滤
        _dashVm.Load(snap.Contracts, TeamLabel(snap.Contracts.Count));
        _dashVm.ApplyTarget(snap.TeamTarget?.RevenueTargetCents ?? 0,   // 按 团队×当前年 套用目标（否则显示"未设"）
                            snap.TeamTarget?.ProfitTargetCents ?? 0,
                            snap.TeamTarget?.CostCeilingCents ?? 0);
        _browseVm.LoadFrom(snap.Contracts);   // 台账明细 = 本团队总库
        _entryVm.LoadFrom(snap.Entry);        // 个人录入 = 我的
        _pendingVm.Apply(snap.Incoming, snap.Outgoing);
        SetBadge(snap.Badge);
        StatusBar.Text = $"使用人 {_person} · {MyTeam ?? "本组"} {snap.Contracts.Count} 条";
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

    /// <summary>刷新：配了云同步就直接下载最新（内部会重载表格）；否则本地重载。
    /// 同步进行中的重复点击由 <see cref="SyncInBackground"/> 去重（狂点只下一次）。</summary>
    private void OnRefresh(object sender, RoutedEventArgs e)
    {
        if (_cos.IsConfigured) SyncInBackground();   // 立即下载云端最新
        else ReloadAll();                            // 未配置云同步：仅本地重载
    }

    // ————————— 团队目标：本地读写 + 云端统一同步（按修改时间「谁新用谁」裁决） —————————

    /// <summary>迁移种子的基准时间：足够旧，保证任何真实编辑（本机或云端）都能盖过它。</summary>
    private static readonly DateTime MigrationBaselineUtc = new(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>
    /// 当前使用人「当前填报身份」的团队（自填、可在设置里切换）。决定用哪一个物理库 / 哪一个 COS 前缀。
    /// 用户模型：以什么团队身份填，数据就进那个团队的库；同一个人可切换团队、可跨多个团队。null=未填。
    /// </summary>
    private string? MyTeam => string.IsNullOrWhiteSpace(_config.TeamName) ? null : _config.TeamName!.Trim();

    /// <summary>团队目标 / 分权用的团队键（有效团队，空则默认行业市场组）。</summary>
    private string TeamKey => MyTeam ?? "行业市场组";

    /// <summary>达成总览副标题：本团队名 + 条数（一团队一库，不存在跨团队全貌）。</summary>
    private string TeamLabel(int shownCount) => $"{MyTeam ?? "本组"} · {shownCount} 条";

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
        _ = Task.Run(() => { try { new CloudSync(cos, team).UploadTeamTargets(bundle); } catch { /* 失败：下次同步 MaybeReuploadTeamTargets 会补传 */ } });
    }

    /// <summary>把云端下载的团队目标落本地库：仅当云端某年度<b>更新</b>（或本地尚无）才覆盖，
    /// 本地更新的编辑保留不动，避免旧云值回滚管理员刚改、尚未上传成功的新值。</summary>
    private void ApplyDownloadedTeamTargets(TeamTargetBundle b)
    {
        // 只接受本团队的目标包（多团队时防止别组目标被套到本组）
        if (!string.IsNullOrWhiteSpace(b.TeamName) && !string.Equals(b.TeamName.Trim(), TeamKey, StringComparison.Ordinal)) return;
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

    /// <summary>打开「当前团队身份」对应的物理库（切换团队时先释放旧库再开新库），并补团队目标种子 + 迁移个人目标键。
    /// 不做合同灌库——数据靠手动导入/录入按团队进入。</summary>
    private void OpenTeamStore()
    {
        _store?.Dispose();
        _store = new LedgerStore(ResolveTeamDataDir());
        _currentTeamToken = TeamPartition.Token(TeamKey);   // 记录当前团队库，供后台同步回写时比对
        _store.WriteAudit("Open", _person, note: $"团队 {MyTeam} · 机器码 {MachineId.Short()}");
        SeedTeamTargetIfNeeded();        // 仅行业团队补 2026 历史团队指标（看板数字不变、可编辑）
        _store.MigratePersonTargetKey(_config.PersonCode ?? MachineId.Short(), _person, DateTime.Now.Year);
    }

    /// <summary>
    /// 一团队一库：返回本团队的本地库目录（data/&lt;团队令牌&gt;/）。首次运行把老的单一库
    /// (data/ledger.db + db.key) <b>复制</b>进<b>行业团队</b>目录（老库数据本属行业市场组），保证升级不丢历史数据
    /// （老库原地保留作备份，不删）。别的团队不迁移、保持空。迁移异常则回退老目录并记 startup-error.log。
    /// </summary>
    private string ResolveTeamDataDir()
    {
        var legacyDir = Path.Combine(_dataDir, "data");
        // 用 TeamKey（与云端前缀、团队目标同一口径）算令牌，保证本地库目录与 COS 前缀始终对齐
        var teamDir = Path.Combine(legacyDir, TeamPartition.Token(TeamKey));
        try
        {
            Directory.CreateDirectory(teamDir);
            var teamDb = Path.Combine(teamDir, "ledger.db");
            var legacyDb = Path.Combine(legacyDir, "ledger.db");
            // 仅「行业」团队继承老单一库(其数据本属行业市场组)：团队库尚不存在、老单一库在 → 复制迁移
            // （先拷密钥与可能的 WAL 边车，主库最后拷，保证 teamDb 存在=迁移完成）。别的团队保持空。
            if (TeamKey.Contains("行业") && !File.Exists(teamDb) && File.Exists(legacyDb) && File.Exists(Path.Combine(legacyDir, "db.key")))
            {
                File.Copy(Path.Combine(legacyDir, "db.key"), Path.Combine(teamDir, "db.key"), overwrite: true);
                foreach (var suffix in new[] { "-wal", "-shm" })
                    if (File.Exists(legacyDb + suffix)) File.Copy(legacyDb + suffix, teamDb + suffix, overwrite: true);
                File.Copy(legacyDb, teamDb, overwrite: true);
            }
            return teamDir;
        }
        catch (Exception ex)
        {
            try { File.AppendAllText(Path.Combine(_dataDir, "startup-error.log"), $"{DateTime.Now:s} 团队库迁移失败，回退单库：{ex}\n"); } catch { }
            return legacyDir;   // 迁移异常：回退老目录，保证数据可用（降级为单库）
        }
    }

}
