using Weitong.Ledger.Core;

namespace Weitong.Ledger.Data.Db;

/// <summary>本地登录账户（离线鉴权）。与 SalesPerson 以 SalesPersonCode 关联。</summary>
public sealed class UserAccount
{
    public long Id { get; set; }
    public string UserName { get; set; } = "";      // 登录名（唯一）
    public string DisplayName { get; set; } = "";    // 显示名，如 朴东旭
    /// <summary>关联的销售业务码；经理/管理员可为空。数据分区核心。</summary>
    public string? SalesPersonCode { get; set; }
    public UserRole Role { get; set; } = UserRole.Sales;
    public string PasswordHash { get; set; } = "";   // BCrypt 复合串
    public bool MustChangePassword { get; set; } = true;
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; }
    public DateTime? LastLoginAt { get; set; }
    public int FailedAttempts { get; set; }
    public DateTime? LockedUntil { get; set; }
}

/// <summary>审计日志（append-only）：登录、切换、导出、改数据全留痕，符合国企内控。</summary>
public sealed class AuditLog
{
    public long Id { get; set; }
    public string EntityName { get; set; } = "";
    public string? EntityId { get; set; }
    public string Action { get; set; } = "";         // Login / Import / Edit / Export / Merge …
    public string? ChangedBy { get; set; }
    public DateTime ChangedAtUtc { get; set; }
    public string? OldJson { get; set; }
    public string? NewJson { get; set; }
    public string? Note { get; set; }
}
