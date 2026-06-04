namespace RentalCommand.Core.Entities;

/// <summary>
/// A per-lease balance carried over from before the landlord migrated onto Rental Command. When the
/// books start mid-stream, a tenant may already owe (or have credit) from prior history; recording it
/// here keeps the transparent ledger honest instead of silently dropping pre-app financials.
/// <see cref="Amount"/> is signed: positive = the tenant owed this much as of <see cref="AsOfDate"/>;
/// negative = they had a credit. At most one row per lease (enforced by a unique index).
/// </summary>
public class OpeningBalance
{
    public int Id { get; set; }
    public int PortfolioId { get; set; }
    public int LeaseId { get; set; }

    /// <summary>Signed carried-over amount: positive = owed by tenant, negative = credit.</summary>
    public decimal Amount { get; set; }

    /// <summary>The "books start" date this balance was true as of.</summary>
    public DateTime AsOfDate { get; set; }

    public string? Note { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    public Lease? Lease { get; set; }
    public Portfolio? Portfolio { get; set; }
}
