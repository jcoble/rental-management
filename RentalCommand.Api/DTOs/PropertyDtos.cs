using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;

namespace RentalCommand.Api.DTOs;

/// <summary>
/// The canonical record opened from a Property collection row. This is selected from persisted
/// <see cref="RentalStructure"/> by the server; clients must not infer it from Unit count.
/// </summary>
public enum PropertyWorkspaceDestination
{
    Property,
    Unit,
}

/// <summary>One stable top-level area in the MultiRental Property workspace.</summary>
public enum PropertyWorkspaceArea
{
    Summary,
    Rentals,
    OwnershipManagement,
    PropertyWork,
    PropertyFinances,
    DocumentsHistory,
}

/// <summary>
/// Server-owned presentation routing for a Property. SingleRental opens its canonical Unit directly;
/// MultiRental opens the Property workspace and exposes the six approved top-level areas.
/// </summary>
public sealed class PropertyWorkspaceEntryResponse
{
    public PropertyWorkspaceDestination Destination { get; init; }
    public int PropertyId { get; init; }
    public int? UnitId { get; init; }
    public IReadOnlyList<PropertyWorkspaceArea> Areas { get; init; } = [];

    public static PropertyWorkspaceEntryResponse FromPersistedStructure(
        RentalStructure rentalStructure,
        int propertyId,
        int? singleRentalUnitId) =>
        rentalStructure == RentalStructure.SingleRental
            ? new PropertyWorkspaceEntryResponse
            {
                Destination = PropertyWorkspaceDestination.Unit,
                PropertyId = propertyId,
                UnitId = singleRentalUnitId,
            }
            : new PropertyWorkspaceEntryResponse
            {
                Destination = PropertyWorkspaceDestination.Property,
                PropertyId = propertyId,
                Areas =
                [
                    PropertyWorkspaceArea.Summary,
                    PropertyWorkspaceArea.Rentals,
                    PropertyWorkspaceArea.OwnershipManagement,
                    PropertyWorkspaceArea.PropertyWork,
                    PropertyWorkspaceArea.PropertyFinances,
                    PropertyWorkspaceArea.DocumentsHistory,
                ],
            };
}

/// <summary>Wire shape returned for a <see cref="Property"/>.</summary>
public class PropertyResponse
{
    public int Id { get; set; }
    public int PortfolioId { get; set; }
    public IReadOnlyList<PropertyOwnershipResponse> Ownerships { get; set; } = [];
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Property type. Serialized as <c>type</c> (not <c>propertyType</c>) to match the single field
    /// name the web client reads/sends everywhere (grid column, type filter, create/edit forms). The
    /// mismatch previously left the value <c>undefined</c> client-side — Type showed "-" in list/detail.
    /// </summary>
    [JsonPropertyName("type")]
    public PropertyType PropertyType { get; set; }

    /// <summary>
    /// Controls whether the UI collapses the redundant Property-to-Unit navigation layer. It does
    /// not select a different rental, lease, accounting, or maintenance model.
    /// </summary>
    public RentalStructure RentalStructure { get; set; }

    public PropertyStatus Status { get; set; }
    public string AddressLine1 { get; set; } = string.Empty;
    public string? AddressLine2 { get; set; }
    public string City { get; set; } = string.Empty;
    public string State { get; set; } = string.Empty;
    public string PostalCode { get; set; } = string.Empty;
    public int? YearBuilt { get; set; }
    public decimal? ManagementFeePercent { get; set; }
    public string? Notes { get; set; }

    /// <summary>Acquisition cost used as the depreciation basis (null when not tracking depreciation).</summary>
    public decimal? PurchasePrice { get; set; }

    /// <summary>Portion of the purchase price allocated to land (not depreciable).</summary>
    public decimal? LandValue { get; set; }

    /// <summary>Date placed in service (drives first-year mid-month depreciation proration).</summary>
    public DateTime? InServiceDate { get; set; }

    /// <summary>Manual annual depreciation override; wins over the computed figure when set.</summary>
    public decimal? ManualAnnualDepreciation { get; set; }

    /// <summary>Cumulative depreciation taken to date (caps the computed annual figure at basis).</summary>
    public decimal AccumulatedDepreciation { get; set; }

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    /// <summary>Total units on the property. Computed in SQL (correlated subquery), never loaded + counted.</summary>
    public int UnitCount { get; set; }

    /// <summary>Units currently occupied. Computed in SQL from canonical possession projection.</summary>
    public int OccupiedUnits { get; set; }

    /// <summary>
    /// Server-selected collection entry. Its destination is based only on persisted RentalStructure;
    /// the canonical SingleRental Unit id is selected in the same SQL projection as this Property.
    /// </summary>
    public PropertyWorkspaceEntryResponse WorkspaceEntry { get; set; } = new();

    /// <summary>Stable selector for frontend tests, e.g. <c>property-1</c>.</summary>
    public string TestId => $"property-{Id}";

    public static PropertyResponse FromEntity(Property e, int unitCount = 0, int occupiedUnits = 0) => new()
    {
        Id = e.Id,
        PortfolioId = e.PortfolioId,
        Name = e.Name,
        PropertyType = e.PropertyType,
        RentalStructure = e.RentalStructure,
        Status = e.Status,
        AddressLine1 = e.AddressLine1,
        AddressLine2 = e.AddressLine2,
        City = e.City,
        State = e.State,
        PostalCode = e.PostalCode,
        YearBuilt = e.YearBuilt,
        ManagementFeePercent = e.ManagementFeePercent,
        Notes = e.Notes,
        PurchasePrice = e.PurchasePrice,
        LandValue = e.LandValue,
        InServiceDate = e.InServiceDate,
        ManualAnnualDepreciation = e.ManualAnnualDepreciation,
        AccumulatedDepreciation = e.AccumulatedDepreciation,
        CreatedAt = e.CreatedAt,
        UpdatedAt = e.UpdatedAt,
        UnitCount = unitCount,
        OccupiedUnits = occupiedUnits,
    };
}

public sealed class PropertyOwnershipResponse
{
    public int Id { get; set; }
    public int OwnerEntityId { get; set; }
    public string OwnerName { get; set; } = string.Empty;
    public decimal OwnershipSharePercent { get; set; }
    public DateTime EffectiveFromUtc { get; set; }
    public DateTime? EffectiveToUtc { get; set; }
    public string StatementRecipientName { get; set; } = string.Empty;
    public string? StatementRecipientEmail { get; set; }
    public string PayeeName { get; set; } = string.Empty;
}

public sealed class PropertyOwnershipRequest
{
    [Range(1, int.MaxValue)]
    public int OwnerEntityId { get; set; }

    [Range(typeof(decimal), "0.0001", "100")]
    public decimal OwnershipSharePercent { get; set; } = 100m;

    public DateTime? EffectiveFromUtc { get; set; }
    public DateTime? EffectiveToUtc { get; set; }

    [MaxLength(200)]
    public string? StatementRecipientName { get; set; }

    [MaxLength(254)]
    [EmailAddress]
    public string? StatementRecipientEmail { get; set; }

    [MaxLength(200)]
    public string? PayeeName { get; set; }
}

public class PropertyListResponse
{
    public IReadOnlyList<PropertyResponse> Items { get; set; } = [];
    public int TotalCount { get; set; }
    public int Skip { get; set; }
    public int Take { get; set; }
}

public class PropertyListQuery : ListQuery
{
    [JsonPropertyName("type")]
    public PropertyType? Type { get; set; }
    public PropertyStatus? Status { get; set; }
    public bool? AvailableForLease { get; set; }
}

public class CreatePropertyRequest
{
    /// <summary>
    /// Optional complete canonical ownership set. When omitted, setup assigns the workspace's
    /// primary OwnerEntity as one effective 100% relationship.
    /// </summary>
    public IReadOnlyList<PropertyOwnershipRequest>? Ownerships { get; set; }

    /// <summary>
    /// Explicitly leave the property without effective ownership. Without this flag, setup falls
    /// back to the portfolio's primary OwnerEntity when no ownership rows are supplied.
    /// </summary>
    public bool ClearOwnership { get; set; }

    [Required]
    [MaxLength(200)]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("type")]
    public PropertyType PropertyType { get; set; } = PropertyType.MultiFamily;
    public RentalStructure RentalStructure { get; set; } = RentalStructure.MultiRental;
    public PropertyStatus Status { get; set; } = PropertyStatus.Active;

    [Required]
    [MaxLength(250)]
    public string AddressLine1 { get; set; } = string.Empty;

    [MaxLength(250)]
    public string? AddressLine2 { get; set; }

    [Required]
    [MaxLength(100)]
    public string City { get; set; } = string.Empty;

    [Required]
    [MaxLength(100)]
    public string State { get; set; } = string.Empty;

    [Required]
    [MaxLength(20)]
    public string PostalCode { get; set; } = string.Empty;

    [Range(1800, 2200)]
    public int? YearBuilt { get; set; }

    [Range(0, 100)]
    public decimal? ManagementFeePercent { get; set; }

    [MaxLength(2000)]
    public string? Notes { get; set; }

    // Depreciation basis (optional; see DepreciationCalculator). AccumulatedDepreciation is maintained
    // by the system, not set here.
    [Range(0, 999_999_999)]
    public decimal? PurchasePrice { get; set; }

    [Range(0, 999_999_999)]
    public decimal? LandValue { get; set; }

    public DateTime? InServiceDate { get; set; }

    [Range(0, 999_999_999)]
    public decimal? ManualAnnualDepreciation { get; set; }
}

public class UpdatePropertyRequest
{
    /// <summary>
    /// Optional replacement set for current ownership. Existing current rows are ended and these
    /// effective rows are inserted in the same atomic command.
    /// </summary>
    public IReadOnlyList<PropertyOwnershipRequest>? Ownerships { get; set; }

    /// <summary>
    /// Explicitly ends every current ownership row on update.
    /// </summary>
    public bool ClearOwnership { get; set; }

    [MaxLength(200)]
    public string? Name { get; set; }

    [JsonPropertyName("type")]
    public PropertyType? PropertyType { get; set; }
    public PropertyStatus? Status { get; set; }

    [MaxLength(250)]
    public string? AddressLine1 { get; set; }

    [MaxLength(250)]
    public string? AddressLine2 { get; set; }

    [MaxLength(100)]
    public string? City { get; set; }

    [MaxLength(100)]
    public string? State { get; set; }

    [MaxLength(20)]
    public string? PostalCode { get; set; }

    [Range(1800, 2200)]
    public int? YearBuilt { get; set; }

    [Range(0, 100)]
    public decimal? ManagementFeePercent { get; set; }

    [MaxLength(2000)]
    public string? Notes { get; set; }

    // Depreciation basis (optional). AccumulatedDepreciation is system-maintained, not set here.
    [Range(0, 999_999_999)]
    public decimal? PurchasePrice { get; set; }

    [Range(0, 999_999_999)]
    public decimal? LandValue { get; set; }

    public DateTime? InServiceDate { get; set; }

    [Range(0, 999_999_999)]
    public decimal? ManualAnnualDepreciation { get; set; }
}

/// <summary>One explicit Unit supplied with the atomic Property setup command.</summary>
public sealed class SetupUnitRequest
{
    [Required]
    [MaxLength(50)]
    public string UnitNumber { get; set; } = string.Empty;

    [MaxLength(100)]
    public string? FloorPlan { get; set; }

    [Range(0, 99)]
    public decimal Bedrooms { get; set; }

    [Range(0, 99)]
    public decimal Bathrooms { get; set; }

    [Range(0, 99999)]
    public int? SquareFeet { get; set; }

    [Range(0, 99999999)]
    public decimal MarketRent { get; set; }

    [MaxLength(2000)]
    public string? Notes { get; set; }
}

/// <summary>
/// Atomic Guided Setup payload. The nested Property shape deliberately matches the normal create
/// contract while Units omit PropertyId because the transaction owns that relationship.
/// </summary>
public sealed class SetupPropertyRequest
{
    [Range(1, int.MaxValue)]
    public int? PropertyId { get; set; }

    [Required]
    public CreatePropertyRequest Property { get; set; } = new();

    [Required]
    public List<SetupUnitRequest> Units { get; set; } = [];
}

public sealed class PropertySetupResponse
{
    public PropertyResponse Property { get; set; } = new();
    public IReadOnlyList<UnitResponse> Units { get; set; } = [];
    public bool Updated { get; set; }
}
