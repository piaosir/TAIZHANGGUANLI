namespace Weitong.Ledger.Core;

/// <summary>统一审计字段。所有业务实体继承。软删除，不物理删。</summary>
public abstract class AuditableEntity
{
    public long Id { get; set; }
    public DateTime CreatedAt { get; set; }
    public string? CreatedBy { get; set; }
    public DateTime UpdatedAt { get; set; }
    public string? UpdatedBy { get; set; }
    /// <summary>乐观并发 + 合并冲突裁决用。</summary>
    public long RowVersion { get; set; }
    public bool IsDeleted { get; set; }
}

/// <summary>销售人员 / 用户。纯离线本地账户。</summary>
public sealed class SalesPerson : AuditableEntity
{
    /// <summary>稳定短码（英文/拼音），构成 ContractUid 前缀，跨库合并锚点。</summary>
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public UserRole Role { get; set; } = UserRole.Sales;
    public string? TeamName { get; set; }
    public bool IsActive { get; set; } = true;
}

/// <summary>目标指标：按 销售×年 或 团队×年 存三条线。</summary>
public sealed class Target : AuditableEntity
{
    /// <summary>"sales" 或 "team"。</summary>
    public string OwnerType { get; set; } = "sales";
    /// <summary>销售 Code 或团队名。</summary>
    public string OwnerKey { get; set; } = "";
    public int Year { get; set; }
    public long RevenueTargetCents { get; set; } // 收入指标
    public long ProfitTargetCents { get; set; }  // 利润指标
    public long CostCeilingCents { get; set; }   // 成本控制指标
}

/// <summary>月度到款。1-4月合并为一个累计桶（PeriodMonth=4, IsCumulative=true）。</summary>
public sealed class MonthlyPayment
{
    public long Id { get; set; }
    public long ContractId { get; set; }
    public PaymentKind Kind { get; set; } = PaymentKind.Forecast;
    /// <summary>桶键：4 表示"1-4月累计"，5..12 表示单月。</summary>
    public int PeriodMonth { get; set; }
    public bool IsCumulative { get; set; }
    public long AmountCents { get; set; }
}

/// <summary>
/// 合同 / 商机记录。台账每一行。所有金额为分（long）。
/// </summary>
public sealed class Contract : AuditableEntity
{
    /// <summary>全局唯一稳定主键（跨库合并去重）。形如 "park-000123"。</summary>
    public string ContractUid { get; set; } = "";
    /// <summary>数据归属人（Code）。合并权限与分区依据。</summary>
    public string OwnerCode { get; set; } = "";

    // —— 分类维度 ——
    public ContractStage Stage { get; set; }
    public string? ContractNo { get; set; }        // 合同编号
    public DateOnly? SignDate { get; set; }         // 签约日期
    public DateOnly? ExpectedSignDate { get; set; } // 预计签约日期（月粒度存月初）
    public string? ProjectAttribute { get; set; }   // 项目属性（新签/续签/在手…）
    public string? ProjectName { get; set; }        // 项目名称
    public string? PartyA { get; set; }             // 合同甲方
    public string PartyB { get; set; } = "中国卫通集团股份有限公司"; // 合同乙方
    public string SalesPersonName { get; set; } = "";  // 销售人员（显示名）
    public string? MarketField { get; set; }        // 市场领域（字典）
    public string? BusinessSegment { get; set; }    // 本部业务板块（字典）
    public string? ProductType { get; set; }        // 产品类型（字典）
    public string Currency { get; set; } = "人民币"; // 币种

    // —— 金额（分）——
    public long ContractAmountCents { get; set; }   // 销售合同金额
    public long RevenueEstCents { get; set; }       // S 本年预计属期收入
    public long CostEstCents { get; set; }          // T 本年预计直接成本
    /// <summary>U = S − T（计算列，落库时同步）。</summary>
    public long ProfitEstCents => RevenueEstCents - CostEstCents;
    public long ReceivedToDateCents { get; set; }   // 截止目前累计到款

    // —— 日期/期数 ——
    public DateOnly? ValidFrom { get; set; }        // 合同有效期起
    public DateOnly? ValidTo { get; set; }          // 合同有效期止
    public int? ServiceMonthsThisYear { get; set; } // 本年服务期数

    public string? CultivationSource { get; set; }  // 培育来源

    /// <summary>覆盖该层级默认成交概率（预测口径用）；null 则取层级默认。</summary>
    public double? WinProbabilityOverride { get; set; }

    public List<MonthlyPayment> Payments { get; set; } = new();

    /// <summary>本年预计到款 V = 月度（预计）之和。</summary>
    public long PaymentForecastTotalCents =>
        Payments.Where(p => p.Kind == PaymentKind.Forecast).Sum(p => p.AmountCents);

    /// <summary>该合同的有效成交概率。</summary>
    public double EffectiveWinProbability =>
        WinProbabilityOverride ?? Stages.DefaultWinProbability(Stage);
}

/// <summary>
/// 跨设备合并的冲突裁决。全链统一使用「最后修改时间（<see cref="AuditableEntity.UpdatedAt"/>）
/// 谁新谁赢」，同一时刻取 <see cref="AuditableEntity.RowVersion"/> 大者。删除以「墓碑」参与合并
/// （<see cref="AuditableEntity.IsDeleted"/>=true 且带删除时刻的 UpdatedAt），因此删除能盖过更早的
/// 存活副本、且被删后不会再被其它设备的旧副本复活。
/// </summary>
public static class MergeArbiter
{
    /// <summary><paramref name="a"/> 是否比 <paramref name="b"/>「更新」（按 UpdatedAt，同刻取 RowVersion 大者）。</summary>
    public static bool IsNewer(Contract a, Contract b)
    {
        int t = a.UpdatedAt.CompareTo(b.UpdatedAt);
        return t > 0 || (t == 0 && a.RowVersion > b.RowVersion);
    }

    /// <summary>
    /// 按 <see cref="Contract.ContractUid"/> 合并多份设备快照，逐 UID 取「最新」版本（含墓碑）。
    /// 返回的集合可能包含 IsDeleted=true 的墓碑——调用方展示时应过滤，落库时应保留（好让删除继续传播、并收敛）。
    /// </summary>
    public static List<Contract> MergeByUid(IEnumerable<Contract> all)
    {
        var byUid = new Dictionary<string, Contract>(StringComparer.Ordinal);
        foreach (var c in all)
        {
            if (string.IsNullOrEmpty(c.ContractUid)) continue;
            if (!byUid.TryGetValue(c.ContractUid, out var cur) || IsNewer(c, cur))
                byUid[c.ContractUid] = c;
        }
        return byUid.Values.ToList();
    }
}
