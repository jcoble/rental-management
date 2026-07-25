namespace RentalCommand.Core.Entities;

/// <summary>
/// Read-only row shape for the accounting transaction query. AccountingService builds one translated
/// UNION ALL over canonical tenant ledger, expense, bank, and application-finance sources, then applies
/// filtering, sorting, and paging before materialization.
/// </summary>
public class AccountingTransactionView
{
    /// <summary>Source discriminator such as "TenantLedger", "Expense", "Bank", or "ApplicationFee".</summary>
    public string Kind { get; set; } = string.Empty;

    /// <summary>Underlying source-row id. TenantLedgerEntries use bigint identities.</summary>
    public long Id { get; set; }

    /// <summary>Owning portfolio (app-layer scope predicate; RLS is the security boundary).</summary>
    public int PortfolioId { get; set; }

    /// <summary>Transaction date: ledger post time, expense paid/incurred time, bank posted time, or application event time.</summary>
    public DateTime Date { get; set; }

    /// <summary>When the row entered the system (source CreatedAt) — drives the default newest-entered sort.</summary>
    public DateTime CreatedAt { get; set; }

    /// <summary>When the row was last edited (source UpdatedAt; equals CreatedAt for untouched rows).</summary>
    public DateTime UpdatedAt { get; set; }

    public string Description { get; set; } = string.Empty;

    /// <summary>Typed ledger entry, expense category, application-finance type, or bank category.</summary>
    public string Category { get; set; } = string.Empty;

    /// <summary>Ledger direction, expense status, posted application state, or bank match state.</summary>
    public string Status { get; set; } = string.Empty;

    public decimal Amount { get; set; }

    /// <summary>Canonical account id for tenant-ledger rows; null for other source kinds.</summary>
    public int? TenantAccountId { get; set; }

    /// <summary>Property id via TenantAccount/LeaseManagement or the source expense/application row.</summary>
    public int? PropertyId { get; set; }

    /// <summary>Unit id via TenantAccount/LeaseManagement or the source expense/application row.
    /// Drives the unit-scoped Command Center deep link on the ledger.</summary>
    public int? UnitId { get; set; }

    public string? PropertyName { get; set; }

    /// <summary>Tenant, vendor, applicant-safe label, or bank merchant/institution.</summary>
    public string? Counterparty { get; set; }

    /// <summary>Extra searchable account, work-order, application, or bank reference.</summary>
    public string? Reference { get; set; }

    public string? Notes { get; set; }
    public bool HasReceipt { get; set; }
    public bool ReceiptIsImage { get; set; }
    public bool Reconciled { get; set; }
    public string? ClearedBankName { get; set; }
    public DateTime? ClearedAt { get; set; }
}
