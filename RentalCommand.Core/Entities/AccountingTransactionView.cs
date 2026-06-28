namespace RentalCommand.Core.Entities;

/// <summary>
/// Keyless EF entity projected over the <c>vw_accounting_transactions</c> Postgres view, which
/// <c>UNION ALL</c>s Payments, Expenses and (unmatched, non-removed) BankTransactions into one
/// filterable/sortable/pageable surface for the accounting transactions grid. Filtering, sorting and
/// pagination execute in the database as one SQL statement instead of materializing all three
/// sources and merging in memory.
///
/// <para>
/// The view carries only the raw columns the grid filters / sorts / searches on. Display-only
/// enrichment (receipt thumbnails, bank-reconciliation state, the deep-link href) is still computed
/// in C# on the returned page only — see <c>AccountingService.GetTransactionsAsync</c>.
/// </para>
///
/// <para>
/// Soft-delete is enforced inside the view SQL (the base-table EF query filters don't apply to a raw
/// view): payments join their Lease with <c>DeletedAt IS NULL</c>, expenses filter their own
/// <c>DeletedAt IS NULL</c>, and bank rows require a live Portfolio — exactly mirroring the entities'
/// <c>HasQueryFilter</c> predicates. Property / Tenant / Vendor / WorkOrder names come from LEFT
/// joins that also require <c>DeletedAt IS NULL</c>, so a soft-deleted parent nulls the name rather
/// than dropping the row, matching the EF projection.
/// </para>
///
/// <para>
/// The view is created <c>WITH (security_invoker = true)</c> so the base tables' RLS
/// <c>tenant_isolation</c> policies apply to the querying <c>rentalcommand_api</c> role — portfolio
/// isolation is preserved through the view. Callers still filter <c>PortfolioId == portfolioId</c>
/// for the app-layer scope.
/// </para>
/// </summary>
public class AccountingTransactionView
{
    /// <summary>"Payment", "Expense" or "Bank" — the source-table discriminator (grid "kind" filter).</summary>
    public string Kind { get; set; } = string.Empty;

    /// <summary>Underlying row id (Payments.Id / Expenses.Id / BankTransactions.Id).</summary>
    public int Id { get; set; }

    /// <summary>Owning portfolio (app-layer scope predicate; RLS is the security boundary).</summary>
    public int PortfolioId { get; set; }

    /// <summary>Transaction date: Payment PaidDate ?? DueDate, Expense PaidAt ?? IncurredAt, Bank PostedAt.</summary>
    public DateTime Date { get; set; }

    /// <summary>When the row entered the system (source CreatedAt) — drives the default newest-entered sort.</summary>
    public DateTime CreatedAt { get; set; }

    /// <summary>When the row was last edited (source UpdatedAt; equals CreatedAt for untouched rows).</summary>
    public DateTime UpdatedAt { get; set; }

    public string Description { get; set; } = string.Empty;

    /// <summary>Category label: PaymentType / ExpenseCategory enum name, or the bank category/Deposit/Withdrawal.</summary>
    public string Category { get; set; } = string.Empty;

    /// <summary>Status label: PaymentStatus / ExpenseStatus enum name, or the bank MatchStatus.</summary>
    public string Status { get; set; } = string.Empty;

    public decimal Amount { get; set; }

    /// <summary>Property id for the row (via Lease for payments, direct for expenses; null for bank rows).</summary>
    public int? PropertyId { get; set; }

    /// <summary>Unit id for the row (via Lease for payments, direct for expenses; null for bank rows).
    /// Drives the unit-scoped Command Center deep link on the ledger.</summary>
    public int? UnitId { get; set; }

    public string? PropertyName { get; set; }

    /// <summary>Tenant name (payments), vendor name (expenses), or merchant/institution (bank).</summary>
    public string? Counterparty { get; set; }

    /// <summary>Extra searchable text: lease number/method/reference (payments), work-order title
    /// (expenses), account name/provider txn id (bank).</summary>
    public string? Reference { get; set; }

    public string? Notes { get; set; }
}
