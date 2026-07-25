using RentalCommand.Core.Enums;
using RentalCommand.Core.Interfaces;

namespace RentalCommand.Core.Entities;

/// <summary>
/// The legal owner of properties/income — a Person, LLC, or Trust.
/// </summary>
public class OwnerEntity : IAuditable, IPortfolioScoped
{
    public int Id { get; set; }
    public int PortfolioId { get; set; }
    public OwnerEntityType OwnerEntityType { get; set; } = OwnerEntityType.Person;
    public string Name { get; set; } = string.Empty;
    public string? TaxId { get; set; }

    // Canonical structured mailing address.
    public string? AddressLine1 { get; set; }
    public string? AddressLine2 { get; set; }
    public string? City { get; set; }
    public string? State { get; set; }
    public string? PostalCode { get; set; }

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
    public ICollection<PropertyOwnership> PropertyOwnerships { get; set; } = new List<PropertyOwnership>();
    public ICollection<OwnerUserAccess> UserAccesses { get; set; } = new List<OwnerUserAccess>();
}
