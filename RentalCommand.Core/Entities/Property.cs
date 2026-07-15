using RentalCommand.Core.Enums;
using RentalCommand.Core.Interfaces;

namespace RentalCommand.Core.Entities;

public class Property : IAuditable, IPortfolioScoped
{
    public int Id { get; set; }
    public int PortfolioId { get; set; }
    public int? OwnerId { get; set; }
    public int? OwnerEntityId { get; set; }
    public string Name { get; set; } = string.Empty;
    public PropertyType PropertyType { get; set; } = PropertyType.MultiFamily;
    public RentalStructure RentalStructure { get; set; } = RentalStructure.MultiRental;
    public PropertyStatus Status { get; set; } = PropertyStatus.Active;
    public string AddressLine1 { get; set; } = string.Empty;
    public string? AddressLine2 { get; set; }
    public string City { get; set; } = string.Empty;
    public string State { get; set; } = string.Empty;
    public string PostalCode { get; set; } = string.Empty;
    public int? YearBuilt { get; set; }
    public decimal? ManagementFeePercent { get; set; }
    public string? Notes { get; set; }

    // ── Depreciation basis (year-end tax picture) ────────────────────────────────────────────────
    // Straight-line residential depreciation (27.5-yr) is computed from these; see
    // DepreciationCalculator. Land is not depreciable, so the building basis is PurchasePrice − LandValue.

    /// <summary>Acquisition cost used as the depreciation basis. Null when not tracking depreciation.</summary>
    public decimal? PurchasePrice { get; set; }

    /// <summary>Portion of <see cref="PurchasePrice"/> allocated to land (NOT depreciable).</summary>
    public decimal? LandValue { get; set; }

    /// <summary>Date the property was placed in service (drives first-year mid-month proration).</summary>
    public DateTime? InServiceDate { get; set; }

    /// <summary>Manual annual depreciation override; when set it wins over the computed figure.</summary>
    public decimal? ManualAnnualDepreciation { get; set; }

    /// <summary>
    /// Cumulative depreciation taken to date. Persisted so the computed annual figure can be capped at
    /// the building basis (§6/§18) and a future sale-year §1250 recapture has the basis it needs.
    /// </summary>
    public decimal AccumulatedDepreciation { get; set; }

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    /// <summary>Soft-delete marker; null means active.</summary>
    public DateTime? DeletedAt { get; set; }

    public Portfolio? Portfolio { get; set; }
    public Owner? Owner { get; set; }
    public OwnerEntity? OwnerEntity { get; set; }
    public List<Unit> Units { get; set; } = [];
    public List<LeaseManagement> LeaseManagements { get; set; } = [];
    public List<UnitOperationalPeriod> UnitOperationalPeriods { get; set; } = [];
    public List<WorkOrder> WorkOrders { get; set; } = [];
    public List<Appointment> Appointments { get; set; } = [];
    public List<Inspection> Inspections { get; set; } = [];
    public List<Expense> Expenses { get; set; } = [];
    public List<CapitalAsset> CapitalAssets { get; set; } = [];
    public List<PropertyDisposition> Dispositions { get; set; } = [];
    public List<EvictionCase> EvictionCases { get; set; } = [];
}
