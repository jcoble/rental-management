using RentalCommand.Core.Enums;
using RentalCommand.Core.Interfaces;

namespace RentalCommand.Core.Entities;

/// <summary>
/// The legal owner of properties/income — a Person, LLC, or Trust.
/// Distinct from the legacy <see cref="Owner"/> contact record; referenced
/// from Property/Tenant/Payment/Expense in later phases.
/// </summary>
public class OwnerEntity : IAuditable, IPortfolioScoped
{
    public int Id { get; set; }
    public int PortfolioId { get; set; }
    public OwnerEntityType OwnerEntityType { get; set; } = OwnerEntityType.Person;
    public string Name { get; set; } = string.Empty;
    public string? TaxId { get; set; }

    // Structured mailing address. <see cref="Address"/> is kept as a legacy/composed
    // single-line form (set from these on write) so older read paths keep working.
    public string? AddressLine1 { get; set; }
    public string? AddressLine2 { get; set; }
    public string? City { get; set; }
    public string? State { get; set; }
    public string? PostalCode { get; set; }

    public string? Address { get; set; }
    public string? Phone { get; set; }
    public string? Email { get; set; }   // for emailed owner statements

    /// <summary>
    /// True for the owner auto-created from the landlord's own account during onboarding (the
    /// self-owner). Lets the getting-started "owner" task auto-complete without a manual "add an owner"
    /// step, while additional owners the user adds stay <c>false</c>. At most one primary owner per
    /// portfolio is expected; it is editable like any other owner (e.g. renamed to an LLC).
    /// </summary>
    public bool IsPrimary { get; set; }

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    /// <summary>Soft-delete marker; null means active.</summary>
    public DateTime? DeletedAt { get; set; }

    public Portfolio? Portfolio { get; set; }
}
