using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Weitong.Ledger.Core;
using Weitong.Ledger.Data.Import;

namespace Weitong.Ledger.Data.Db;

public sealed record SeedResult(int Added, int Skipped, int TotalInDb);

/// <summary>
/// 台账加密本地库的门面：创建/打开加密库、建表、首次灌库(幂等)、查询。
/// 每个使用者一份独立加密库(物理隔离 + 天然按销售分区)。
/// </summary>
public sealed class LedgerStore : IDisposable
{
    private readonly string _dbPath;
    private readonly string _password;
    private readonly string _connString;
    // 进程级「保活连接」：整个 App 生命周期常开不关。作用是让 Microsoft.Data.Sqlite 的连接池
    // 始终保有一个已完成 SQLCipher 密钥派生(KDF)的物理连接——后续每次 CreateContext 都从池中
    // 复用它，不再重跑 PRAGMA key 的 25.6 万次 PBKDF2 迭代。这正是「启动慢 + 编辑/撤销/翻页卡」的根因。
    private readonly Microsoft.Data.Sqlite.SqliteConnection _keepAlive;

    // 进程内写库串行锁。本 App 里「后台云同步回写(经 Dispatcher 在 UI 线程)」与「手动导入/编辑(在 Task.Run 后台线程)」
    // 会并发写同一加密库；SQLite 在默认回滚日志模式下会抛 "database is locked" —— 这正是「离线点导入弹红色
    // 失败框」的真正根因（导入链路本身不联网，是并发写库撞锁）。所有会写库的方法进入前先取此锁，保证全程单写者。
    private readonly object _writeLock = new();
    private WriteScope WriteGate() => new(_writeLock);

    /// <summary>using 作用域内持有写锁；Monitor 可重入，同线程嵌套安全。</summary>
    private readonly struct WriteScope : IDisposable
    {
        private readonly object _gate;
        public WriteScope(object gate) { _gate = gate; System.Threading.Monitor.Enter(_gate); }
        public void Dispose() => System.Threading.Monitor.Exit(_gate);
    }

    static LedgerStore() => SQLitePCL.Batteries_V2.Init();

    public LedgerStore(string dataDir)
    {
        Directory.CreateDirectory(dataDir);
        _dbPath = Path.Combine(dataDir, "ledger.db");
        _password = DbKeyProvider.GetOrCreate(Path.Combine(dataDir, "db.key"));
        _connString = BuildConnectionString();
        using (var ctx = CreateContext())
        {
            ctx.Database.EnsureCreated();
            EnsureAuxTables(ctx);   // 老库补建后加的表（EnsureCreated 不改既有库）
        }
        _keepAlive = new Microsoft.Data.Sqlite.SqliteConnection(_connString);
        _keepAlive.Open();          // 常开，保证连接池不被清空、KDF 只在此刻跑一次
        // 开启 WAL：读写并发不再互相阻塞（后台同步回写 与 前台导入/编辑可并存），配合上面的写锁根治
        // "database is locked"。busy_timeout 兜底：偶发忙时短暂等待而非立刻抛错。WAL 状态写入库头，
        // 后续所有连接自动沿用；SQLCipher 兼容 WAL（-wal 文件同样加密）。
        using (var pragma = _keepAlive.CreateCommand())
        {
            pragma.CommandText = "PRAGMA journal_mode=WAL; PRAGMA busy_timeout=8000; PRAGMA synchronous=NORMAL;";
            pragma.ExecuteNonQuery();
        }
    }

    public void Dispose() => _keepAlive.Dispose();

    /// <summary>
    /// 为<b>已存在</b>的加密库补建后续版本新增的表（EnsureCreated 只在建库时生效，不会为老库加表）。
    /// 用 IF NOT EXISTS，新库无副作用。列名/类型与 EF 约定一致，确保查询兼容。
    /// </summary>
    private static void EnsureAuxTables(LedgerDbContext ctx)
    {
        ctx.Database.ExecuteSqlRaw(@"
CREATE TABLE IF NOT EXISTS ""ReviewItems"" (
    ""Id"" INTEGER NOT NULL CONSTRAINT ""PK_ReviewItems"" PRIMARY KEY AUTOINCREMENT,
    ""OpId"" TEXT NOT NULL,
    ""OpType"" TEXT NOT NULL,
    ""Status"" TEXT NOT NULL,
    ""TargetContractUid"" TEXT NOT NULL,
    ""TargetOwnerName"" TEXT NOT NULL,
    ""ByName"" TEXT NOT NULL,
    ""ByCode"" TEXT NOT NULL,
    ""CreatedUtc"" TEXT NOT NULL,
    ""DecidedUtc"" TEXT NULL,
    ""DecideNote"" TEXT NULL,
    ""Summary"" TEXT NULL,
    ""ContractJson"" TEXT NULL,
    ""ClearedByOwner"" INTEGER NOT NULL DEFAULT 0
);");
        ctx.Database.ExecuteSqlRaw(@"CREATE UNIQUE INDEX IF NOT EXISTS ""IX_ReviewItems_OpId"" ON ""ReviewItems"" (""OpId"");");
        ctx.Database.ExecuteSqlRaw(@"CREATE INDEX IF NOT EXISTS ""IX_ReviewItems_TargetOwnerName"" ON ""ReviewItems"" (""TargetOwnerName"");");
        ctx.Database.ExecuteSqlRaw(@"CREATE INDEX IF NOT EXISTS ""IX_ReviewItems_ByName"" ON ""ReviewItems"" (""ByName"");");

        // 老库补列：ReviewItems 加「发起方已清除」本地隐藏标记（v1.0.3 之后新增）。
        EnsureColumn(ctx, "ReviewItems", "ClearedByOwner", @"INTEGER NOT NULL DEFAULT 0");
    }

    /// <summary>为已存在的表幂等补列：SQLite 无 ADD COLUMN IF NOT EXISTS，先查 PRAGMA table_info 再决定是否补，
    /// 避免每次启动都触发「duplicate column」异常。</summary>
    private static void EnsureColumn(LedgerDbContext ctx, string table, string column, string decl)
    {
        var conn = ctx.Database.GetDbConnection();
        bool wasClosed = conn.State != System.Data.ConnectionState.Open;
        if (wasClosed) conn.Open();
        try
        {
            bool exists = false;
            using (var probe = conn.CreateCommand())
            {
                probe.CommandText = $@"PRAGMA table_info(""{table}"");";
                using var r = probe.ExecuteReader();
                while (r.Read())
                    if (string.Equals(r.GetString(1), column, StringComparison.OrdinalIgnoreCase)) { exists = true; break; }
            }
            if (!exists)
            {
                using var alter = conn.CreateCommand();
                alter.CommandText = $@"ALTER TABLE ""{table}"" ADD COLUMN ""{column}"" {decl};";
                alter.ExecuteNonQuery();
            }
        }
        finally { if (wasClosed) conn.Close(); }
    }

    public string DbPath => _dbPath;

    /// <summary>连接串：启用连接池(默认)，让已派生密钥的连接可复用，配合保活连接避免每次开连接重跑 KDF。</summary>
    private string BuildConnectionString() =>
        new SqliteConnectionStringBuilder
        {
            DataSource = _dbPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Password = _password,      // Microsoft.Data.Sqlite 在 SQLCipher 下自动执行 PRAGMA key
            // 不再设 Pooling=false：保持默认池化，是本次性能修复的关键
        }.ToString();

    public LedgerDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<LedgerDbContext>()
            .UseSqlite(_connString)
            .Options;
        return new LedgerDbContext(options);
    }

    /// <summary>首次把导入结果灌库；按 ContractUid 幂等，可重复调用不重复入库。</summary>
    public SeedResult SeedFromImport(ImportResult import, string by)
    {
        using var _g = WriteGate();
        using var ctx = CreateContext();
        var existing = ctx.Contracts.IgnoreQueryFilters().Select(c => c.ContractUid).ToHashSet();

        int added = 0, skipped = 0;
        foreach (var c in import.Contracts)
        {
            if (existing.Contains(c.ContractUid)) { skipped++; continue; }
            c.Id = 0;
            foreach (var p in c.Payments) p.Id = 0;
            ctx.Contracts.Add(c);
            existing.Add(c.ContractUid);
            added++;
        }
        ctx.SaveChanges();
        return new SeedResult(added, skipped, ctx.Contracts.Count());
    }

    public int ContractCount()
    {
        using var ctx = CreateContext();
        return ctx.Contracts.Count();
    }

    public List<Contract> GetAllContracts()
    {
        using var ctx = CreateContext();
        return ctx.Contracts.Include(c => c.Payments).AsNoTracking().ToList();
    }

    /// <summary>
    /// 取全部合同（<b>含已软删的墓碑</b>），供云同步广播。删除靠墓碑传播：只有把 IsDeleted=true 的行
    /// 也上云，其它设备才知道"这条已删"，否则并集会把它复活。
    /// </summary>
    public List<Contract> GetAllContractsForSync()
    {
        using var ctx = CreateContext();
        return ctx.Contracts.IgnoreQueryFilters().Include(c => c.Payments).AsNoTracking().ToList();
    }

    /// <summary>
    /// 把云端合并后的<b>权威集合（含墓碑）</b>落回本地库，让删除真正生效、并使本地库与云端收敛。
    /// 逐条按 <see cref="MergeArbiter.IsNewer"/> 裁决：仅当云端版本比本地<b>更新</b>才覆盖，
    /// 从而不会用旧云值回滚本机刚改、尚未上传成功的新编辑；云端为墓碑则本地也随之软删。
    /// 返回实际写入/更新的条数。
    /// </summary>
    public int ApplyMergedFromCloud(IEnumerable<Contract> rows, string by)
    {
        using var _g = WriteGate();
        using var ctx = CreateContext();
        // 预载全部既有行(含墓碑)到内存，避免逐条查库的 N 次往返。
        var existing = ctx.Contracts.IgnoreQueryFilters().Include(c => c.Payments)
                          .ToDictionary(c => c.ContractUid, StringComparer.Ordinal);
        int n = 0;
        foreach (var m in rows)
        {
            if (string.IsNullOrEmpty(m.ContractUid)) continue;
            if (!existing.TryGetValue(m.ContractUid, out var e))
            {
                // 本地没有：直接落库（墓碑也落，保证本机继续广播删除、并停止显示）
                m.Id = 0;
                foreach (var p in m.Payments) { p.Id = 0; p.ContractId = 0; }
                ctx.Contracts.Add(m);
                existing[m.ContractUid] = m;
                n++;
            }
            else
            {
                if (!MergeArbiter.IsNewer(m, e)) continue;   // 本地不更旧 → 保留本地（不回滚未上传的新编辑）
                long id = e.Id; m.Id = id;
                ctx.Entry(e).CurrentValues.SetValues(m);     // 复制标量（含 IsDeleted / UpdatedAt / RowVersion）
                // 月度到款整表替换（SetValues 不合并导航集合）
                ctx.MonthlyPayments.RemoveRange(e.Payments);
                foreach (var p in m.Payments)
                    ctx.MonthlyPayments.Add(new MonthlyPayment { ContractId = id, Kind = p.Kind, PeriodMonth = p.PeriodMonth, IsCumulative = p.IsCumulative, AmountCents = p.AmountCents });
                n++;
            }
        }
        ctx.SaveChanges();
        return n;
    }

    /// <summary>按销售人员过滤（个人页/权限分区用）。</summary>
    public List<Contract> GetContractsFor(string salesPersonName)
    {
        using var ctx = CreateContext();
        return ctx.Contracts.Include(c => c.Payments)
            .Where(c => c.SalesPersonName == salesPersonName)
            .AsNoTracking().ToList();
    }

    /// <summary>保存录入：按 Id/ContractUid upsert 标量字段 + 整表替换月度到款，返回写入条数。
    /// 批量（导入）时先一次性预载已存在记录，再在内存里匹配，避免逐行查库带来的 O(N) 往返与界面卡顿。</summary>
    public int UpsertContracts(IEnumerable<Contract> rows, string by)
    {
        var list = rows as IReadOnlyList<Contract> ?? rows.ToList();
        if (list.Count == 0) return 0;

        using var _g = WriteGate();
        using var ctx = CreateContext();
        var now = DateTime.UtcNow;

        // 一次性预载：按 Id、按 ContractUid 各批量查一遍（含月度到款）。后续在内存字典里匹配，
        // 取代原来的"逐行 FirstOrDefault"——那是导入卡死的主因（N 行 = N 次加密库往返 + 变更跟踪随行数膨胀）。
        var ids = list.Where(m => m.Id > 0).Select(m => m.Id).Distinct().ToList();
        var uids = list.Where(m => m.Id <= 0 && !string.IsNullOrEmpty(m.ContractUid))
                       .Select(m => m.ContractUid).Distinct().ToList();

        // 预载必须 IgnoreQueryFilters：软删的墓碑（IsDeleted=true）也要能被匹配到。否则重复 UID 会被当成
        // 新行 INSERT，撞上 ContractUid 的 UNIQUE 约束 → "导入失败/保存实体变更出错"。导入的 Excel 若含此前
        // 被删的记录，应更新并复活（SetValues 会把 IsDeleted 复位为 false）而非再插一条。
        var byId = ids.Count == 0
            ? new Dictionary<long, Contract>()
            : ctx.Contracts.IgnoreQueryFilters().Include(c => c.Payments).Where(c => ids.Contains(c.Id)).ToDictionary(c => c.Id);
        var byUid = uids.Count == 0
            ? new Dictionary<string, Contract>()
            : ctx.Contracts.IgnoreQueryFilters().Include(c => c.Payments).Where(c => uids.Contains(c.ContractUid)).ToDictionary(c => c.ContractUid);

        int n = 0;
        foreach (var m in list)
        {
            Contract? e = null;
            if (m.Id > 0) byId.TryGetValue(m.Id, out e);
            else if (!string.IsNullOrEmpty(m.ContractUid)) byUid.TryGetValue(m.ContractUid, out e);

            if (e == null)
            {
                m.Id = 0;
                m.UpdatedBy = by; m.UpdatedAt = now;
                foreach (var p in m.Payments) { p.Id = 0; p.ContractId = 0; }
                ctx.Contracts.Add(m);
                if (!string.IsNullOrEmpty(m.ContractUid)) byUid[m.ContractUid] = m; // 防同批次重复键二次插入触发唯一约束
            }
            else
            {
                long id = e.Id; m.Id = id;
                ctx.Entry(e).CurrentValues.SetValues(m); // 复制标量字段
                e.UpdatedBy = by; e.UpdatedAt = now; e.RowVersion++;
                // 月度到款整表替换（EF 的 SetValues 不合并导航集合，必须手动处理，否则月度编辑丢失）
                ctx.MonthlyPayments.RemoveRange(e.Payments);
                foreach (var p in m.Payments)
                    ctx.MonthlyPayments.Add(new MonthlyPayment { ContractId = id, Kind = p.Kind, PeriodMonth = p.PeriodMonth, IsCumulative = p.IsCumulative, AmountCents = p.AmountCents });
            }
            n++;
        }
        ctx.SaveChanges();
        return n;
    }

    /// <summary>软删除（置 IsDeleted，不物理删，符合国企审计）。</summary>
    public int SoftDeleteContracts(IEnumerable<long> ids, string by)
    {
        using var _g = WriteGate();
        using var ctx = CreateContext();
        int n = 0;
        foreach (var id in ids)
        {
            var e = ctx.Contracts.FirstOrDefault(c => c.Id == id);
            if (e != null) { e.IsDeleted = true; e.UpdatedBy = by; e.UpdatedAt = DateTime.UtcNow; n++; }
        }
        ctx.SaveChanges();
        return n;
    }

    /// <summary>读取个人目标（OwnerType=sales, OwnerKey=机器码/人员码）。</summary>
    public Target? GetPersonTarget(string ownerKey, int year)
    {
        using var ctx = CreateContext();
        return ctx.Targets.FirstOrDefault(t => t.OwnerType == "sales" && t.OwnerKey == ownerKey && t.Year == year);
    }

    public void SavePersonTarget(string ownerKey, int year, long revenueCents, long profitCents, long costCeilingCents, string by)
    {
        using var _g = WriteGate();
        using var ctx = CreateContext();
        var t = ctx.Targets.FirstOrDefault(x => x.OwnerType == "sales" && x.OwnerKey == ownerKey && x.Year == year);
        if (t == null)
        {
            t = new Target { OwnerType = "sales", OwnerKey = ownerKey, Year = year, CreatedAt = DateTime.UtcNow, CreatedBy = by, RowVersion = 1 };
            ctx.Targets.Add(t);
        }
        t.RevenueTargetCents = revenueCents;
        t.ProfitTargetCents = profitCents;
        t.CostCeilingCents = costCeilingCents;
        t.UpdatedAt = DateTime.UtcNow; t.UpdatedBy = by; t.RowVersion++;
        ctx.SaveChanges();
    }

    /// <summary>
    /// 迁移旧「按机器码存」的个人目标到「按姓名存」：当姓名键下尚无该年目标、而机器码键下有时，复制过去。
    /// 一次性、幂等（新键已存在则不动）。修复「换台电脑目标就变、且从不上云」的老 bug。返回是否发生迁移。
    /// </summary>
    public bool MigratePersonTargetKey(string oldKey, string newName, int year)
    {
        if (string.IsNullOrWhiteSpace(oldKey) || string.IsNullOrWhiteSpace(newName) || oldKey == newName) return false;
        using var _g = WriteGate();
        using var ctx = CreateContext();
        bool hasNew = ctx.Targets.Any(t => t.OwnerType == "sales" && t.OwnerKey == newName && t.Year == year);
        if (hasNew) return false;
        var old = ctx.Targets.FirstOrDefault(t => t.OwnerType == "sales" && t.OwnerKey == oldKey && t.Year == year);
        if (old == null) return false;
        ctx.Targets.Add(new Target
        {
            OwnerType = "sales", OwnerKey = newName, Year = year,
            RevenueTargetCents = old.RevenueTargetCents, ProfitTargetCents = old.ProfitTargetCents, CostCeilingCents = old.CostCeilingCents,
            CreatedAt = old.CreatedAt == default ? DateTime.UtcNow : old.CreatedAt, CreatedBy = old.CreatedBy,
            UpdatedAt = old.UpdatedAt == default ? DateTime.UtcNow : old.UpdatedAt, UpdatedBy = old.UpdatedBy, RowVersion = old.RowVersion,
        });
        ctx.SaveChanges();
        return true;
    }

    /// <summary>落库云端下发的个人目标（按姓名键，LWW：仅当云端更新、或本地尚无，才覆盖，不回滚本机更晚的编辑）。</summary>
    public void ApplyDownloadedPersonTarget(string name, int year, long revenueCents, long profitCents, long costCeilingCents, DateTime updatedUtc, string by)
    {
        if (string.IsNullOrWhiteSpace(name)) return;
        using var _g = WriteGate();
        using var ctx = CreateContext();
        var t = ctx.Targets.FirstOrDefault(x => x.OwnerType == "sales" && x.OwnerKey == name && x.Year == year);
        if (t != null && DateTime.SpecifyKind(t.UpdatedAt, DateTimeKind.Utc) >= updatedUtc) return; // 本地不更旧→保留
        if (t == null)
        {
            t = new Target { OwnerType = "sales", OwnerKey = name, Year = year, CreatedAt = DateTime.UtcNow, CreatedBy = by, RowVersion = 0 };
            ctx.Targets.Add(t);
        }
        t.RevenueTargetCents = revenueCents; t.ProfitTargetCents = profitCents; t.CostCeilingCents = costCeilingCents;
        t.UpdatedAt = updatedUtc == default ? DateTime.UtcNow : updatedUtc; t.UpdatedBy = by; t.RowVersion++;
        ctx.SaveChanges();
    }

    /// <summary>读取团队目标（OwnerType=team, OwnerKey=团队名）。null=该年尚未设置。</summary>
    public Target? GetTeamTarget(string teamKey, int year)
    {
        using var ctx = CreateContext();
        return ctx.Targets.FirstOrDefault(t => t.OwnerType == "team" && t.OwnerKey == teamKey && t.Year == year);
    }

    /// <summary>读取某团队的全部年度目标（升序），构建云端同步包用。</summary>
    public List<Target> GetTeamTargets(string teamKey)
    {
        using var ctx = CreateContext();
        return ctx.Targets.Where(t => t.OwnerType == "team" && t.OwnerKey == teamKey)
                          .OrderBy(t => t.Year).ToList();
    }

    /// <summary>写入/更新团队某年度目标（(OwnerType,OwnerKey,Year) 唯一，幂等 upsert）。
    /// <paramref name="updatedAtUtc"/> 显式指定「最后修改时间」（同步落库时用云端时间，保证冲突裁决可比较）；
    /// 传 null 表示本机此刻编辑，取 UtcNow。</summary>
    public void SaveTeamTarget(string teamKey, int year, long revenueCents, long profitCents, long costCeilingCents, string by, DateTime? updatedAtUtc = null)
    {
        using var _g = WriteGate();
        using var ctx = CreateContext();
        var t = ctx.Targets.FirstOrDefault(x => x.OwnerType == "team" && x.OwnerKey == teamKey && x.Year == year);
        if (t == null)
        {
            t = new Target { OwnerType = "team", OwnerKey = teamKey, Year = year, CreatedAt = DateTime.UtcNow, CreatedBy = by, RowVersion = 1 };
            ctx.Targets.Add(t);
        }
        t.RevenueTargetCents = revenueCents;
        t.ProfitTargetCents = profitCents;
        t.CostCeilingCents = costCeilingCents;
        t.UpdatedAt = updatedAtUtc ?? DateTime.UtcNow; t.UpdatedBy = by; t.RowVersion++;
        ctx.SaveChanges();
    }

    /// <summary>按 ContractUid 落库一条合同（忽略外来 Id，避免跨机 Id 冲突）。用于确认管理员提案。</summary>
    public void UpsertContractByUid(Contract c, string by)
    {
        using var _g = WriteGate();
        using var ctx = CreateContext();
        var e = ctx.Contracts.IgnoreQueryFilters().Include(x => x.Payments)
                   .FirstOrDefault(x => x.ContractUid == c.ContractUid);
        if (e == null)
        {
            c.Id = 0;
            foreach (var p in c.Payments) { p.Id = 0; p.ContractId = 0; }
            c.CreatedBy ??= by; c.CreatedAt = c.CreatedAt == default ? DateTime.UtcNow : c.CreatedAt;
            c.UpdatedBy = by; c.UpdatedAt = DateTime.UtcNow; c.IsDeleted = false;
            ctx.Contracts.Add(c);
        }
        else
        {
            long id = e.Id;
            c.Id = id;
            ctx.Entry(e).CurrentValues.SetValues(c);   // 仅标量
            e.IsDeleted = false;
            e.UpdatedBy = by; e.UpdatedAt = DateTime.UtcNow; e.RowVersion++;
            // 月度到款：整体替换为提案值
            ctx.MonthlyPayments.RemoveRange(e.Payments);
            foreach (var p in c.Payments)
                ctx.MonthlyPayments.Add(new MonthlyPayment { ContractId = id, Kind = p.Kind, PeriodMonth = p.PeriodMonth, IsCumulative = p.IsCumulative, AmountCents = p.AmountCents });
        }
        ctx.SaveChanges();
    }

    /// <summary>按 ContractUid 软删除。用于确认管理员的删除提案。</summary>
    public void SoftDeleteByUid(string uid, string by)
    {
        using var _g = WriteGate();
        using var ctx = CreateContext();
        var e = ctx.Contracts.FirstOrDefault(x => x.ContractUid == uid);
        if (e != null) { e.IsDeleted = true; e.UpdatedBy = by; e.UpdatedAt = DateTime.UtcNow; e.RowVersion++; ctx.SaveChanges(); }
    }

    // ————————— 审批项（管理员↔销售） —————————

    /// <summary>本机新建/更新一条审批项（管理员发起时用）。按 OpId 幂等。</summary>
    public void SaveReviewItem(ReviewItem item)
    {
        using var _g = WriteGate();
        using var ctx = CreateContext();
        var e = ctx.ReviewItems.FirstOrDefault(x => x.OpId == item.OpId);
        if (e == null) { item.Id = 0; ctx.ReviewItems.Add(item); }
        else { item.Id = e.Id; ctx.Entry(e).CurrentValues.SetValues(item); }
        ctx.SaveChanges();
    }

    /// <summary>合并云端拉来的审批项：按 OpId 去重；不把已决策项回退成待办。</summary>
    public int IngestReviewItems(IEnumerable<ReviewItem> items)
    {
        using var _g = WriteGate();
        using var ctx = CreateContext();
        var byOp = ctx.ReviewItems.ToDictionary(x => x.OpId);
        int n = 0;
        foreach (var it in items)
        {
            if (string.IsNullOrEmpty(it.OpId)) continue;
            if (byOp.TryGetValue(it.OpId, out var e))
            {
                // 已存在：更新内容，但保留本机已作出的决策状态
                e.Summary = it.Summary; e.ContractJson = it.ContractJson;
                e.TargetContractUid = it.TargetContractUid; e.TargetOwnerName = it.TargetOwnerName;
                e.OpType = it.OpType; e.ByName = it.ByName; e.ByCode = it.ByCode;
                if (e.Status == ReviewStatus.Pending && it.Status != ReviewStatus.Pending)
                { e.Status = it.Status; e.DecidedUtc = it.DecidedUtc; e.DecideNote = it.DecideNote; }
            }
            else
            {
                it.Id = 0; ctx.ReviewItems.Add(it);
                byOp[it.OpId] = it; n++;
            }
        }
        ctx.SaveChanges();
        return n;
    }

    /// <summary>合并回流的决策：更新对应审批项的状态（管理员看到结果）。返回被应用为"确认执行"的项。</summary>
    public List<ReviewItem> IngestDecisions(IEnumerable<ReviewDecision> decisions)
    {
        using var _g = WriteGate();
        using var ctx = CreateContext();
        var byOp = ctx.ReviewItems.ToDictionary(x => x.OpId);
        var newlyConfirmed = new List<ReviewItem>();
        foreach (var d in decisions)
        {
            if (!byOp.TryGetValue(d.OpId, out var e)) continue;
            if (e.Status != ReviewStatus.Pending) continue;      // 已结不重复处理
            e.Status = d.Status; e.DecidedUtc = d.DecidedUtc; e.DecideNote = d.Note;
            if (d.Status == ReviewStatus.Confirmed) newlyConfirmed.Add(Clone(e));
        }
        ctx.SaveChanges();
        return newlyConfirmed;
    }

    /// <summary>发给我、需我处理的审批项（他人发起、目标是我）。includeClosed=false 只取待办。</summary>
    public List<ReviewItem> GetIncomingReviews(string myName, bool includeClosed = false)
    {
        using var ctx = CreateContext();
        var q = ctx.ReviewItems.Where(x => x.TargetOwnerName == myName && x.ByName != myName);
        if (!includeClosed) q = q.Where(x => x.Status == ReviewStatus.Pending);
        return q.OrderByDescending(x => x.CreatedUtc).ToList();
    }

    /// <summary>我发起的审批项（管理员查看自己提案的进度）。已撤回的不在列表展示——发起方撤回后即视作删除；
    /// 但记录仍保留在库并继续经 <see cref="GetOutgoingForUpload"/> 上云，确保对方那端的待办被移除。</summary>
    public List<ReviewItem> GetOutgoingReviews(string myName, bool includeClosed = true)
    {
        using var ctx = CreateContext();
        // 已撤回、已被本人清除(隐藏)的都不在「我发起的」列表展示。
        var q = ctx.ReviewItems.Where(x => x.ByName == myName && x.Status != ReviewStatus.Withdrawn && !x.ClearedByOwner);
        if (!includeClosed) q = q.Where(x => x.Status == ReviewStatus.Pending);
        return q.OrderByDescending(x => x.CreatedUtc).ToList();
    }

    /// <summary>我发起、需上云广播的审批项：待办 + 已撤回（让对方据此移除待办）。</summary>
    public List<ReviewItem> GetOutgoingForUpload(string myName)
    {
        using var ctx = CreateContext();
        return ctx.ReviewItems
            .Where(x => x.ByName == myName && (x.Status == ReviewStatus.Pending || x.Status == ReviewStatus.Withdrawn))
            .OrderByDescending(x => x.CreatedUtc).ToList();
    }

    /// <summary>发起方撤回一条尚未处理的审批项（仅待办可撤回）。</summary>
    public bool WithdrawReviewItem(string opId, string byName)
    {
        using var _g = WriteGate();
        using var ctx = CreateContext();
        var e = ctx.ReviewItems.FirstOrDefault(x => x.OpId == opId && x.ByName == byName);
        if (e == null || e.Status != ReviewStatus.Pending) return false;
        e.Status = ReviewStatus.Withdrawn; e.DecidedUtc = DateTime.UtcNow;
        ctx.SaveChanges();
        return true;
    }

    /// <summary>
    /// 从我发起的「我发起的」列表清除通知（无需等对方知晓）。按状态分别处理，保证与云端同步一致、不会复活：
    /// <list type="bullet">
    /// <item><b>待对方知晓(Pending)</b>：置 <see cref="ReviewItem.ClearedByOwner"/> 本地隐藏——记录仍保留、仍经
    /// <see cref="GetOutgoingForUpload"/> 上云，<b>对方仍能看到并知晓</b>；此标记不参与 Ingest 合并，故不会被同步覆盖。</item>
    /// <item><b>已结(非待办)</b>：物理删除历史——已结项不在上传范围，删后不会复活。</item>
    /// </list>
    /// 审计日志(AuditLog)独立留存、不受影响。返回实际清除条数。
    /// </summary>
    public int ClearOutgoing(IReadOnlyList<string> opIds, string byName)
    {
        if (opIds.Count == 0) return 0;
        using var _g = WriteGate();
        using var ctx = CreateContext();
        var rows = ctx.ReviewItems
            .Where(x => x.ByName == byName && !x.ClearedByOwner && opIds.Contains(x.OpId))
            .ToList();
        int n = 0;
        foreach (var e in rows)
        {
            if (e.Status == ReviewStatus.Pending) e.ClearedByOwner = true; // 本地隐藏，对方不受影响
            else ctx.ReviewItems.Remove(e);                                // 已结：删历史
            n++;
        }
        if (n > 0) ctx.SaveChanges();
        return n;
    }

    /// <summary>按 ContractUid 取一条合同（审批对照用）。</summary>
    public Contract? GetContractByUid(string uid)
    {
        using var ctx = CreateContext();
        return ctx.Contracts.Include(c => c.Payments).AsNoTracking().FirstOrDefault(c => c.ContractUid == uid);
    }

    /// <summary>待我处理的条数（红点用）。</summary>
    public int CountIncomingPending(string myName)
    {
        using var ctx = CreateContext();
        return ctx.ReviewItems.Count(x => x.TargetOwnerName == myName && x.ByName != myName && x.Status == ReviewStatus.Pending);
    }

    /// <summary>我作出的决策，供上云回流。</summary>
    public List<ReviewDecision> GetMyDecisions(string myName)
    {
        using var ctx = CreateContext();
        return ctx.ReviewItems
            .Where(x => x.TargetOwnerName == myName && x.ByName != myName && x.Status != ReviewStatus.Pending && x.DecidedUtc != null)
            .Select(x => new ReviewDecision(x.OpId, x.Status, x.DecidedUtc!.Value, x.DecideNote))
            .ToList();
    }

    /// <summary>销售对某审批项作出决策（本机记录，待上云回流给管理员）。</summary>
    public void SetReviewDecision(string opId, ReviewStatus status, string? note)
    {
        using var _g = WriteGate();
        using var ctx = CreateContext();
        var e = ctx.ReviewItems.FirstOrDefault(x => x.OpId == opId);
        if (e == null) return;
        e.Status = status; e.DecidedUtc = DateTime.UtcNow; e.DecideNote = note;
        ctx.SaveChanges();
    }

    private static ReviewItem Clone(ReviewItem s) => new()
    {
        OpId = s.OpId, OpType = s.OpType, Status = s.Status,
        TargetContractUid = s.TargetContractUid, TargetOwnerName = s.TargetOwnerName,
        ByName = s.ByName, ByCode = s.ByCode, CreatedUtc = s.CreatedUtc,
        DecidedUtc = s.DecidedUtc, DecideNote = s.DecideNote,
        Summary = s.Summary, ContractJson = s.ContractJson,
        ClearedByOwner = s.ClearedByOwner,
    };

    public void WriteAudit(string action, string? by, string? entity = null, string? note = null)
    {
        using var _g = WriteGate();
        using var ctx = CreateContext();
        ctx.AuditLogs.Add(new AuditLog
        {
            Action = action, ChangedBy = by, EntityName = entity ?? "",
            ChangedAtUtc = DateTime.UtcNow, Note = note,
        });
        ctx.SaveChanges();
    }
}
