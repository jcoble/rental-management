using Microsoft.AspNetCore.Mvc;
using RentalCommand.Core.Enums;

namespace RentalCommand.Api.DTOs;

/// <summary>A bounded, deterministic page envelope used by the accounting contract.</summary>
public sealed class AccountingPage<T>
{
    public IReadOnlyList<T> Items { get; init; } = [];
    public int TotalCount { get; init; }
    public int Skip { get; init; }
    public int Take { get; init; }
}

public sealed class ChartOfAccountsQuery : ListQuery
{
    [FromQuery(Name = "activeOnly")]
    public bool? ActiveOnly { get; set; }

    [FromQuery(Name = "accountTypes")]
    public string? AccountTypes { get; set; }
}

public sealed class GeneralLedgerQuery : ListQuery
{
    [FromQuery(Name = "accountId")]
    public int? AccountId { get; set; }

    [FromQuery(Name = "propertyId")]
    public int? PropertyId { get; set; }

    [FromQuery(Name = "unitId")]
    public int? UnitId { get; set; }

    [FromQuery(Name = "sourceType")]
    public JournalSourceType? SourceType { get; set; }

    [FromQuery(Name = "effectiveFrom")]
    public DateOnly? EffectiveFrom { get; set; }

    [FromQuery(Name = "effectiveTo")]
    public DateOnly? EffectiveTo { get; set; }
}

public sealed class TenantLedgerQuery : ListQuery
{
    [FromQuery(Name = "entryType")]
    public TenantLedgerEntryType? EntryType { get; set; }

    [FromQuery(Name = "effectiveFrom")]
    public DateOnly? EffectiveFrom { get; set; }

    [FromQuery(Name = "effectiveTo")]
    public DateOnly? EffectiveTo { get; set; }

    [FromQuery(Name = "openOnly")]
    public bool? OpenOnly { get; set; }

    [FromQuery(Name = "settledOnly")]
    public bool? SettledOnly { get; set; }
}

public sealed class StatementQuery
{
    [FromQuery(Name = "from")]
    public DateOnly? From { get; set; }

    [FromQuery(Name = "to")]
    public DateOnly? To { get; set; }

    [FromQuery(Name = "currency")]
    public string? Currency { get; set; }

    [FromQuery(Name = "propertyId")]
    public int? PropertyId { get; set; }

    [FromQuery(Name = "unitId")]
    public int? UnitId { get; set; }
}

public sealed class TenantMonthSummaryQuery
{
    [FromQuery(Name = "from")]
    public DateOnly? From { get; set; }

    [FromQuery(Name = "to")]
    public DateOnly? To { get; set; }
}

public sealed class TenantLedgerPeriodSummaryQuery
{
    [FromQuery(Name = "months")]
    public int Months { get; set; } = 12;
}

public sealed class ChartOfAccountsRow
{
    public int Id { get; init; }
    public Guid PublicId { get; init; }
    public string Code { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public AccountType AccountType { get; init; }
    public NormalBalance NormalBalance { get; init; }
    public int? ParentAccountId { get; init; }
    public string? SystemKey { get; init; }
    public ScheduleECategory? ScheduleECategory { get; init; }
    public bool IsSystem { get; init; }
    public bool IsActive { get; init; }
    public bool HasPostedLines { get; init; }
}

public sealed class CreateChartOfAccountsRequest
{
    public string Code { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public AccountType AccountType { get; init; }
    public NormalBalance NormalBalance { get; init; }
    public int? ParentAccountId { get; init; }
    public string? SystemKey { get; init; }
    public ScheduleECategory? ScheduleECategory { get; init; }
    public bool IsActive { get; init; } = true;
}

public sealed class PatchChartOfAccountsRequest
{
    public string? Name { get; init; }
    public bool? IsActive { get; init; }
    public ScheduleECategory? ScheduleECategory { get; init; }
    public int? ParentAccountId { get; init; }
}

public sealed class GeneralLedgerRow
{
    public Guid JournalEntryPublicId { get; init; }
    public int LineId { get; init; }
    public DateOnly EffectiveOn { get; init; }
    public DateTime PostedAtUtc { get; init; }
    public JournalSourceType SourceType { get; init; }
    public long SourceId { get; init; }
    public string SourceBusinessKey { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    public int AccountId { get; init; }
    public string AccountCode { get; init; } = string.Empty;
    public string AccountName { get; init; } = string.Empty;
    public decimal DebitAmount { get; init; }
    public decimal CreditAmount { get; init; }
    public string Currency { get; init; } = string.Empty;
    public int? PropertyId { get; init; }
    public int? UnitId { get; init; }
    public int? TenantAccountId { get; init; }
    public int? OwnerEntityId { get; init; }
    public decimal? RunningBalance { get; init; }
}

public sealed class AllocationRef
{
    public long TargetSourceId { get; init; }
    public Guid TargetPublicId { get; init; }
    public string TargetDescription { get; init; } = string.Empty;
    public decimal Amount { get; init; }
    public DateOnly EffectiveOn { get; init; }
}

public sealed class TenantLedgerRow
{
    public long TenantLedgerEntryId { get; init; }
    public Guid PublicId { get; init; }
    public string SourceType { get; init; } = string.Empty;
    public long SourceId { get; init; }
    public Guid? SourcePublicId { get; init; }
    public DateOnly EffectiveOn { get; init; }
    public DateTime PostedAtUtc { get; init; }
    public TenantLedgerEntryType Type { get; init; }
    public string Description { get; init; } = string.Empty;
    public decimal ChargeAmount { get; init; }
    public decimal PaymentAmount { get; init; }
    public decimal CreditAmount { get; init; }
    public decimal RunningAmountOwed { get; init; }
    public DateOnly? DueOn { get; init; }
    public decimal OpenAmount { get; init; }
    public string Status { get; init; } = string.Empty;
    public string? PaymentMethod { get; init; }
    public string? Reference { get; init; }
    public string? AccountLabel { get; init; }
    public string? RecurringScheduleContext { get; init; }
    public string? SourceDocumentContext { get; init; }
    public IReadOnlyList<AllocationRef> Allocations { get; set; } = [];
    public long? ReversesEntryId { get; init; }
    public long? ReplacedByEntryId { get; init; }
    public Guid? JournalEntryPublicId { get; init; }
    public string Currency { get; init; } = string.Empty;
}

public sealed class TenantMonthSummary
{
    public int Year { get; init; }
    public int Month { get; init; }
    public string Currency { get; init; } = string.Empty;
    public decimal OpeningBalance { get; set; }
    public decimal ChargeAmount { get; init; }
    public decimal PaymentAmount { get; init; }
    public decimal CreditAmount { get; init; }
    public decimal ClosingBalance { get; set; }
}

public sealed class TenantStatementBalances
{
    public decimal OpeningBalance { get; init; }
    public decimal ClosingBalance { get; init; }
}

public sealed class TenantLedgerPeriodSummary
{
    public int PeriodMonths { get; init; }
    public string Currency { get; init; } = string.Empty;
    public decimal ChargeAmount { get; init; }
    public decimal PaymentAmount { get; init; }
    public decimal CreditAmount { get; init; }
    public decimal EndingBalance { get; init; }
    public decimal AgingCurrent { get; init; }
    public decimal Aging1To30 { get; init; }
    public decimal Aging31To60 { get; init; }
    public decimal Aging61To90 { get; init; }
    public decimal Aging90Plus { get; init; }
}

public sealed class JournalDetail
{
    public Guid PublicId { get; init; }
    public string Description { get; init; } = string.Empty;
    public DateOnly EffectiveOn { get; init; }
    public DateTime PostedAtUtc { get; init; }
    public JournalSourceType SourceType { get; init; }
    public long SourceId { get; init; }
    public string SourceBusinessKey { get; init; } = string.Empty;
    public string? Actor { get; init; }
    public Guid AttemptId { get; init; }
    public Guid AtomicReceiptId { get; init; }
    public string IdempotencyDigest { get; init; } = string.Empty;
    public string Currency { get; init; } = string.Empty;
    public IReadOnlyList<JournalDetailLine> Lines { get; init; } = [];
    public decimal TotalDebits { get; init; }
    public decimal TotalCredits { get; init; }
    public bool IsBalanced { get; init; }
    public Guid? ReversesJournalEntryPublicId { get; init; }
    public IReadOnlyList<Guid> ReversalPublicIds { get; init; } = [];
    public string? AuditLink { get; init; }
    public IReadOnlyList<int> DocumentIds { get; init; } = [];
    public BankReconciliationEvidence? BankReconciliationEvidence { get; init; }
}

public sealed class JournalDetailLine
{
    public int Id { get; init; }
    public int AccountId { get; init; }
    public string AccountCode { get; init; } = string.Empty;
    public string AccountName { get; init; } = string.Empty;
    public decimal DebitAmount { get; init; }
    public decimal CreditAmount { get; init; }
    public string? Memo { get; init; }
    public int? PropertyId { get; init; }
    public int? UnitId { get; init; }
    public int? TenantAccountId { get; init; }
    public int? OwnerEntityId { get; init; }
}

public sealed class BankReconciliationEvidence
{
    public int? BankTransactionId { get; init; }
    public string? BankAccountLabel { get; init; }
    public DateOnly? MatchedOn { get; init; }
    public string? Status { get; init; }
}

public sealed class TrialBalanceRow
{
    public int AccountId { get; init; }
    public string AccountCode { get; init; } = string.Empty;
    public string AccountName { get; init; } = string.Empty;
    public AccountType AccountType { get; init; }
    public decimal DebitBalance { get; init; }
    public decimal CreditBalance { get; init; }
    public string Currency { get; init; } = string.Empty;
}

public sealed class TrialBalanceResponse
{
    public IReadOnlyList<TrialBalanceRow> Rows { get; init; } = [];
    public decimal TotalDebits { get; init; }
    public decimal TotalCredits { get; init; }
    public bool IsBalanced { get; init; }
}

public sealed class FinancialStatementRow
{
    public int AccountId { get; init; }
    public string AccountCode { get; init; } = string.Empty;
    public string AccountName { get; init; } = string.Empty;
    public decimal Amount { get; init; }
    public string Currency { get; init; } = string.Empty;
}

public sealed class StatementSection
{
    public string Label { get; init; } = string.Empty;
    public IReadOnlyList<FinancialStatementRow> Rows { get; init; } = [];
    public decimal Subtotal { get; init; }
}

public sealed class StatementTotals
{
    public decimal Total { get; init; }
    public decimal? NetIncome { get; init; }
    public decimal? Assets { get; init; }
    public decimal? LiabilitiesAndEquity { get; init; }
}

public sealed class FinancialStatementResponse
{
    public IReadOnlyList<StatementSection> Sections { get; init; } = [];
    public StatementTotals Totals { get; init; } = new();
}

public sealed class RecurringTenantChargeRow
{
    public int Id { get; init; }
    public Guid PublicId { get; init; }
    public int TenantAccountId { get; init; }
    public int? LeaseAgreementId { get; init; }
    public string DisplayName { get; init; } = string.Empty;
    public decimal Amount { get; init; }
    public string Currency { get; init; } = string.Empty;
    public int LedgerAccountId { get; init; }
    public DateOnly EffectiveStartOn { get; init; }
    public DateOnly? EffectiveEndOn { get; init; }
    public int MonthlyDueDay { get; init; }
    public DateOnly NextRunDate { get; init; }
    public bool IsActive { get; init; }
    public int? PropertyId { get; init; }
    public int? UnitId { get; init; }
}

public sealed class CreateRecurringTenantChargeRequest
{
    public string DisplayName { get; init; } = string.Empty;
    public decimal Amount { get; init; }
    public int LedgerAccountId { get; init; }
    public int? LeaseAgreementId { get; init; }
    public DateOnly EffectiveStartOn { get; init; }
    public DateOnly? EffectiveEndOn { get; init; }
    public int MonthlyDueDay { get; init; }
    public DateOnly? NextRunDate { get; init; }
    public int? PropertyId { get; init; }
    public int? UnitId { get; init; }
}

public sealed class PatchRecurringTenantChargeRequest
{
    public string? DisplayName { get; init; }
    public decimal? Amount { get; init; }
    public int? LedgerAccountId { get; init; }
    public DateOnly? EffectiveStartOn { get; init; }
    public DateOnly? EffectiveEndOn { get; init; }
    public int? MonthlyDueDay { get; init; }
    public DateOnly? NextRunDate { get; init; }
    public bool? IsActive { get; init; }
    public int? PropertyId { get; init; }
    public int? UnitId { get; init; }
}
