using RentalCommand.Core.Interfaces;

namespace RentalCommand.Core.Entities;

/// <summary>
/// A confirm-driven mapping between an external accounting entity and a Rental
/// Command entity (port of EdiPlatform's <c>ErpCustomerMapping</c>, broadened to
/// every mappable type): accounting Customer → Tenant/Lease, Vendor → Vendor,
/// Account → Schedule-E category, Class → Property.
///
/// <para>
/// A row may exist as a <em>suggestion</em> (a name/amount match the suggester
/// surfaced, with its <see cref="Confidence"/>) before the landlord confirms it.
/// <see cref="ConfirmedAt"/>/<see cref="ConfirmedByUserId"/> are null until the
/// landlord confirms; only confirmed mappings drive idempotent create/link (AC-6).
/// </para>
///
/// <para>Portfolio-scoped (RLS) and audited — confirming a mapping is a meaningful,
/// low-volume decision worth the trail; the high-volume per-transaction ledger
/// (<see cref="AccountingSyncMap"/>) is deliberately NOT audited.</para>
/// </summary>
public class AccountingEntityMapping : IPortfolioScoped, IAuditable
{
    public int Id { get; set; }

    public int PortfolioId { get; set; }

    public int AccountingConnectionId { get; set; }

    /// <summary>"Tenant" | "Lease" | "Vendor" | "Property" | "ScheduleECategory".</summary>
    public string LocalEntityType { get; set; } = string.Empty;

    /// <summary>The local row id, or null when the target is an enum value (see <see cref="LocalEnumValue"/>).</summary>
    public int? LocalEntityId { get; set; }

    /// <summary>Set instead of <see cref="LocalEntityId"/> when the local target is an enum (e.g. a Schedule-E category).</summary>
    public string? LocalEnumValue { get; set; }

    /// <summary>"Customer" | "Vendor" | "Account" | "Class".</summary>
    public string ExternalType { get; set; } = string.Empty;

    public string ExternalId { get; set; } = string.Empty;

    public string? ExternalDisplayName { get; set; }

    /// <summary>Null while the row is only a suggestion; set when the landlord confirms.</summary>
    public DateTime? ConfirmedAt { get; set; }

    /// <summary>The user who confirmed the mapping; null while only suggested.</summary>
    public int? ConfirmedByUserId { get; set; }

    /// <summary>The suggester's score (0–1) when the mapping was surfaced for confirmation.</summary>
    public decimal? Confidence { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; }

    public AccountingConnection? AccountingConnection { get; set; }
    public Portfolio? Portfolio { get; set; }
}
