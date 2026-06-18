using RentalCommand.Core.Interfaces;

namespace RentalCommand.Core.Entities;

/// <summary>
/// The idempotency + import/push ledger. One row per external transaction per
/// direction, keyed uniquely on
/// <c>(PortfolioId, AccountingConnectionId, Direction, ExternalType, ExternalId)</c>,
/// pointing at the Rental Command row it created/linked. Import SKIPs any external
/// txn already mapped <c>Imported</c>; push SKIPs any local row already mapped
/// <c>Pushed</c> — so re-import/re-push never duplicates (AC-5).
///
/// <para>
/// Generalizes EdiPlatform's stamp-external-id-on-the-row approach; RC's
/// <c>Payment</c>/<c>Expense</c> have no external-id column, so a side ledger is
/// cleaner. Portfolio-scoped (RLS). Deliberately NOT <c>IAuditable</c>: this is
/// high-volume per-transaction sync bookkeeping, not a business decision — keeping
/// it out of the audit trail prevents flooding it (the audit-marker guidance).
/// </para>
/// </summary>
public class AccountingSyncMap : IPortfolioScoped
{
    public int Id { get; set; }

    public int PortfolioId { get; set; }

    public int AccountingConnectionId { get; set; }

    /// <summary>"Import" (accounting → RC) | "Push" (RC → accounting).</summary>
    public string Direction { get; set; } = string.Empty;

    /// <summary>"Payment" | "Purchase" | "Bill" | "SalesReceipt".</summary>
    public string ExternalType { get; set; } = string.Empty;

    public string ExternalId { get; set; } = string.Empty;

    /// <summary>"Payment" | "Expense" — the kind of RC row this maps to.</summary>
    public string? LocalEntityType { get; set; }

    public int? LocalEntityId { get; set; }

    /// <summary>"Imported" | "Pushed" | "NeedsReview" | "Unmatched" | "Failed".</summary>
    public string Status { get; set; } = string.Empty;

    public int AttemptCount { get; set; }
    public string? LastError { get; set; }
    public DateTime? LastAttemptAt { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; }

    public AccountingConnection? AccountingConnection { get; set; }
    public Portfolio? Portfolio { get; set; }
}
