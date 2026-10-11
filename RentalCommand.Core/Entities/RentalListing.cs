using RentalCommand.Core.Enums;
using RentalCommand.Core.Interfaces;

namespace RentalCommand.Core.Entities;

/// <summary>The provider-neutral, user-owned listing copy for one rentable unit.</summary>
public sealed class RentalListing : IAuditable, IPortfolioScoped
{
    public int Id { get; set; }
    public int PortfolioId { get; set; }
    public int PropertyId { get; set; }
    public int UnitId { get; set; }
    public RentalListingStatus Status { get; set; } = RentalListingStatus.Draft;
    public int ContentVersion { get; set; } = 1;
    public string Headline { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public decimal Rent { get; set; }
    public decimal? SecurityDeposit { get; set; }
    public decimal Bedrooms { get; set; }
    public decimal Bathrooms { get; set; }
    public int? SquareFeet { get; set; }
    public DateTime? AvailableOn { get; set; }
    public string? LeaseTerms { get; set; }
    public string? PetPolicy { get; set; }
    public string? Utilities { get; set; }
    public string? Parking { get; set; }
    public string? Amenities { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public DateTime? DeletedAt { get; set; }

    public Portfolio? Portfolio { get; set; }
    public Unit? Unit { get; set; }
    public List<ListingPhoto> Photos { get; set; } = [];
    public List<ListingPublication> Publications { get; set; } = [];
}

/// <summary>Ordered, reusable package manifest. A row may be a required placeholder before a file is attached.</summary>
public sealed class ListingPhoto : IPortfolioScoped
{
    public int Id { get; set; }
    public int PortfolioId { get; set; }
    public int RentalListingId { get; set; }
    public int Position { get; set; }
    public string Category { get; set; } = string.Empty;
    public string? Caption { get; set; }
    public int? StoredFileId { get; set; }
    public string? FileName { get; set; }
    public string? Sha256 { get; set; }
    public DateTime CreatedAt { get; set; }

    public RentalListing? RentalListing { get; set; }
    public StoredFile? StoredFile { get; set; }
}

/// <summary>One channel attempt. Guided and Connected are modes on this same publication record.</summary>
public sealed class ListingPublication : IAuditable, IPortfolioScoped
{
    public int Id { get; set; }
    public int PortfolioId { get; set; }
    public int RentalListingId { get; set; }
    public string ProviderKey { get; set; } = string.Empty;
    public ListingPublicationMode Mode { get; set; }
    public ListingPublicationStatus Status { get; set; } = ListingPublicationStatus.Draft;
    public int? PublishedContentVersion { get; set; }
    public string? ExternalListingId { get; set; }
    public string? ListingUrl { get; set; }
    public string? ApplicationUrl { get; set; }
    public string? ManagementUrl { get; set; }
    public string? LastConfirmedExternalStatus { get; set; }
    public DateTime? LastConfirmedAtUtc { get; set; }
    public bool CopyConfirmed { get; set; }
    public bool TermsConfirmed { get; set; }
    public bool PhotosConfirmed { get; set; }
    public bool ProviderWorkspaceOpened { get; set; }
    public string? LastDeliveryKey { get; set; }
    public string? LastDeliveryStatus { get; set; }
    public string? LastDeliveryError { get; set; }
    public DateTime? LastDeliveryAttemptAtUtc { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    public RentalListing? RentalListing { get; set; }
    public List<ExternalListingSignal> ExternalSignals { get; set; } = [];
}

/// <summary>
/// Deduplicated, untrusted metadata hint received from an external notification channel. It never stores
/// email bodies, credentials, screening reports, or applicant data and cannot change publication state
/// until a user confirms it.
/// </summary>
public sealed class ExternalListingSignal : IPortfolioScoped
{
    public int Id { get; set; }
    public int PortfolioId { get; set; }
    public int ListingPublicationId { get; set; }
    public string ProviderMessageKey { get; set; } = string.Empty;
    public string SignalType { get; set; } = string.Empty;
    public string? SuggestedExternalListingId { get; set; }
    public string? SuggestedListingUrl { get; set; }
    public string? SuggestedExternalStatus { get; set; }
    public ExternalListingSignalDisposition Disposition { get; set; }
    public DateTime ReceivedAtUtc { get; set; }
    public int? ConfirmedByUserId { get; set; }
    public DateTime? ConfirmedAtUtc { get; set; }

    public ListingPublication? ListingPublication { get; set; }
}
