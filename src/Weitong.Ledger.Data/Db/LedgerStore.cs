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
public sealed class LedgerStore
{
    private readonly string _dbPath;
    private readonly string _password;

    static LedgerStore() => SQLitePCL.Batteries_V2.Init();

    public LedgerStore(string dataDir)
    {
        Directory.CreateDirectory(dataDir);
        _dbPath = Path.Combine(dataDir, "ledger.db");
        _password = DbKeyProvider.GetOrCreate(Path.Combine(dataDir, "db.key"));
        using var ctx = CreateContext();
        ctx.Database.EnsureCreated();
        EnsureAuxTables(ctx);   // 老库补建后加的表（EnsureCreated 不改既有库）
    }

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

    public LedgerDbContext CreateContext()
    {
        var cs = new SqliteConnectionStringBuilder
        {
            DataSource = _dbPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Password = _password,      // Microsoft.Data.Sqlite 在 SQLCipher 下自动执行 PRAGMA key
            Pooling = false,
        }.ToString();

        var options = new DbContextOptionsBuilder<LedgerDbContext>()
            .UseSqlite(cs)
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

    /// <summary>按销售人员过滤（个人页/权限分区用）。</summary>
    public List<Contract> GetContractsFor(string salesPersonName)
    {
        using var ctx = CreateContext();
        return ctx.Contracts.Include(c => c.Payments)
            .Where(c => c.SalesPersonName == salesPersonName)
            .AsNoTracking().ToList();
    }

    /// <summary>保存录入：按 Id/ContractUid upsert 标量字段 + 整表替换月度到款，返回写入条数。</summary>
    public int UpsertContracts(IEnumerable<Contract> rows, string by)
    {
        using var ctx = CreateContext();
        int n = 0;
        foreach (var m in rows)
        {
            Contract? e = m.Id > 0
                ? ctx.Contracts.Include(c => c.Payments).FirstOrDefault(c => c.Id == m.Id)
                : ctx.Contracts.Include(c => c.Payments).FirstOrDefault(c => c.ContractUid == m.ContractUid);

            if (e == null)
            {
                m.Id = 0;
                m.UpdatedBy = by; m.UpdatedAt = DateTime.UtcNow;
                foreach (var p in m.Payments) { p.Id = 0; p.ContractId = 0; }
                ctx.Contracts.Add(m);
            }
            else
            {
                long id = e.Id; m.Id = id;
                ctx.Entry(e).CurrentValues.SetValues(m); // 复制标量字段
                e.UpdatedBy = by; e.UpdatedAt = DateTime.UtcNow; e.RowVersion++;
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

    /// <summary>我发起的审批项（管理员查看自己提案的进度）。</summary>
    public List<ReviewItem> GetOutgoingReviews(string myName, bool includeClosed = true)
    {
        using var ctx = CreateContext();
        var q = ctx.ReviewItems.Where(x => x.ByName == myName);
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
