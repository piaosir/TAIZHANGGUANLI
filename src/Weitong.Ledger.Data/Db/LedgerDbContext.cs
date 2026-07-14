using Microsoft.EntityFrameworkCore;
using Weitong.Ledger.Core;

namespace Weitong.Ledger.Data.Db;

/// <summary>
/// 加密本地库上下文。复用 Core 领域模型作实体，配置软删除过滤器与枚举字符串化。
/// 统计（双口径/漏斗/月度）仍由 Core.StatsEngine 在内存完成，本库只负责加密持久化与变更承载。
/// </summary>
public sealed class LedgerDbContext : DbContext
{
    public DbSet<Contract> Contracts => Set<Contract>();
    public DbSet<MonthlyPayment> MonthlyPayments => Set<MonthlyPayment>();
    public DbSet<SalesPerson> SalesPersons => Set<SalesPerson>();
    public DbSet<Target> Targets => Set<Target>();
    public DbSet<UserAccount> Users => Set<UserAccount>();
    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();
    public DbSet<ReviewItem> ReviewItems => Set<ReviewItem>();

    public LedgerDbContext(DbContextOptions<LedgerDbContext> options) : base(options) { }

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<Contract>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.ContractUid).IsUnique();
            e.HasIndex(x => x.OwnerCode);
            e.HasIndex(x => x.SalesPersonName);
            e.Property(x => x.Stage).HasConversion<string>().HasMaxLength(20);
            e.Ignore(x => x.ProfitEstCents);
            e.Ignore(x => x.PaymentForecastTotalCents);
            e.Ignore(x => x.EffectiveWinProbability);
            e.HasMany(x => x.Payments).WithOne().HasForeignKey(p => p.ContractId).OnDelete(DeleteBehavior.Cascade);
            e.HasQueryFilter(x => !x.IsDeleted);
        });

        b.Entity<MonthlyPayment>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Kind).HasConversion<string>().HasMaxLength(12);
            e.HasIndex(x => x.ContractId);
        });

        b.Entity<SalesPerson>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.Code).IsUnique();
            e.Property(x => x.Role).HasConversion<string>().HasMaxLength(12);
            e.HasQueryFilter(x => !x.IsDeleted);
        });

        b.Entity<Target>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.OwnerType, x.OwnerKey, x.Year }).IsUnique();
            e.HasQueryFilter(x => !x.IsDeleted);
        });

        b.Entity<UserAccount>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.UserName).IsUnique();
            e.Property(x => x.Role).HasConversion<string>().HasMaxLength(12);
        });

        b.Entity<AuditLog>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.ChangedAtUtc);
        });

        b.Entity<ReviewItem>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.OpId).IsUnique();
            e.HasIndex(x => x.TargetOwnerName);
            e.HasIndex(x => x.ByName);
            e.Property(x => x.OpType).HasConversion<string>().HasMaxLength(12);
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(14);
            e.Ignore(x => x.IsClosed);
        });

        base.OnModelCreating(b);
    }
}
