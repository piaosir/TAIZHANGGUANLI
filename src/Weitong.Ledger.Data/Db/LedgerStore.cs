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
    ""ContractJson"" TEXT NULL
);");
        ctx.Database.ExecuteSqlRaw(@"CREATE UNIQUE INDEX IF NOT EXISTS ""IX_ReviewItems_OpId"" ON ""ReviewItems"" (""OpId"");");
        ctx.Database.ExecuteSqlRaw(@"CREATE INDEX IF NOT EXISTS ""IX_ReviewItems_TargetOwnerName"" ON ""ReviewItems"" (""TargetOwnerName"");");
        ctx.Database.ExecuteSqlRaw(@"CREATE INDEX IF NOT EXISTS ""IX_ReviewItems_ByName"" ON ""ReviewItems"" (""ByName"");");
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

        using var ctx = CreateContext();
        var now = DateTime.UtcNow;

        // 一次性预载：按 Id、按 ContractUid 各批量查一遍（含月度到款）。后续在内存字典里匹配，
        // 取代原来的"逐行 FirstOrDefault"——那是导入卡死的主因（N 行 = N 次加密库往返 + 变更跟踪随行数膨胀）。
        var ids = list.Where(m => m.Id > 0).Select(m => m.Id).Distinct().ToList();
        var uids = list.Where(m => m.Id <= 0 && !string.IsNullOrEmpty(m.ContractUid))
                       .Select(m => m.ContractUid).Distinct().ToList();

        var byId = ids.Count == 0
            ? new Dictionary<long, Contract>()
            : ctx.Contracts.Include(c => c.Payments).Where(c => ids.Contains(c.Id)).ToDictionary(c => c.Id);
        var byUid = uids.Count == 0
            ? new Dictionary<string, Contract>()
            : ctx.Contracts.Include(c => c.Payments).Where(c => uids.Contains(c.ContractUid)).ToDictionary(c => c.ContractUid);

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
        using var ctx = CreateContext();
        var e = ctx.Contracts.FirstOrDefault(x => x.ContractUid == uid);
        if (e != null) { e.IsDeleted = true; e.UpdatedBy = by; e.UpdatedAt = DateTime.UtcNow; e.RowVersion++; ctx.SaveChanges(); }
    }

    // ————————— 审批项（管理员↔销售） —————————

    /// <summary>本机新建/更新一条审批项（管理员发起时用）。按 OpId 幂等。</summary>
    public void SaveReviewItem(ReviewItem item)
    {
        using var ctx = CreateContext();
        var e = ctx.ReviewItems.FirstOrDefault(x => x.OpId == item.OpId);
        if (e == null) { item.Id = 0; ctx.ReviewItems.Add(item); }
        else { item.Id = e.Id; ctx.Entry(e).CurrentValues.SetValues(item); }
        ctx.SaveChanges();
    }

    /// <summary>合并云端拉来的审批项：按 OpId 去重；不把已决策项回退成待办。</summary>
    public int IngestReviewItems(IEnumerable<ReviewItem> items)
    {
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
        var q = ctx.ReviewItems.Where(x => x.ByName == myName && x.Status != ReviewStatus.Withdrawn);
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
        using var ctx = CreateContext();
        var e = ctx.ReviewItems.FirstOrDefault(x => x.OpId == opId && x.ByName == byName);
        if (e == null || e.Status != ReviewStatus.Pending) return false;
        e.Status = ReviewStatus.Withdrawn; e.DecidedUtc = DateTime.UtcNow;
        ctx.SaveChanges();
        return true;
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
    };

    public void WriteAudit(string action, string? by, string? entity = null, string? note = null)
    {
        using var ctx = CreateContext();
        ctx.AuditLogs.Add(new AuditLog
        {
            Action = action, ChangedBy = by, EntityName = entity ?? "",
            ChangedAtUtc = DateTime.UtcNow, Note = note,
        });
        ctx.SaveChanges();
    }
}
