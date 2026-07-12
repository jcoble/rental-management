using System.ComponentModel.DataAnnotations;
using RentalCommand.Core.Entities;

namespace RentalCommand.Api.DTOs;

public sealed record ListingPhotoResponse(
    int Id, int Position, string Category, string? Caption, int? StoredFileId, string? FileName, string? Sha256);

public sealed record ExternalListingSignalResponse(
    int Id, string SignalType, string? SuggestedExternalListingId, string? SuggestedListingUrl,
    string? SuggestedExternalStatus, string Disposition, DateTime ReceivedAtUtc);

public sealed record ListingPublicationResponse(
    int Id,
    string ProviderKey,
    string Mode,
    string Status,
    string? ExternalListingId,
    string? ListingUrl,
    string? ApplicationUrl,
    string? ManagementUrl,
    string? LastConfirmedExternalStatus,
    DateTime? LastConfirmedAtUtc,
    bool CopyConfirmed,
    bool TermsConfirmed,
    bool PhotosConfirmed,
    bool ProviderWorkspaceOpened,
    bool NeedsRepublish,
    int? PublishedContentVersion,
    string? LastDeliveryKey,
    string? LastDeliveryStatus,
    string? LastDeliveryError,
    DateTime? LastDeliveryAttemptAtUtc,
    IReadOnlyList<ExternalListingSignalResponse> UnconfirmedSignals);

public sealed record ListingWorkspaceResponse(
    int Id,
    int PortfolioId,
    int PropertyId,
    int UnitId,
    string Status,
    int ContentVersion,
    string Headline,
    string Description,
    decimal Rent,
    decimal? SecurityDeposit,
    decimal Bedrooms,
    decimal Bathrooms,
    int? SquareFeet,
    DateTime? AvailableOn,
    string? LeaseTerms,
    string? PetPolicy,
    string? Utilities,
    string? Parking,
    string? Amenities,
    IReadOnlyList<ListingPhotoResponse> PhotoManifest,
    IReadOnlyList<ListingPublicationResponse> Publications,
    string SignedLeaseImportUrl,
    DateTime CreatedAt,
    DateTime UpdatedAt)
{
    public static ListingWorkspaceResponse FromEntity(RentalListing listing)
        => new(
            listing.Id,
            listing.PortfolioId,
            listing.PropertyId,
            listing.UnitId,
            listing.Status.ToString(),
            listing.ContentVersion,
            listing.Headline,
            listing.Description,
            listing.Rent,
            listing.SecurityDeposit,
            listing.Bedrooms,
            listing.Bathrooms,
            listing.SquareFeet,
            listing.AvailableOn,
            listing.LeaseTerms,
            listing.PetPolicy,
            listing.Utilities,
            listing.Parking,
            listing.Amenities,
            listing.Photos.Select(photo => new ListingPhotoResponse(photo.Id, photo.Position, photo.Category, photo.Caption,
                    photo.StoredFileId, photo.FileName, photo.Sha256))
                .ToArray(),
            listing.Publications.Select(publication => new ListingPublicationResponse(
                    publication.Id,
                    publication.ProviderKey,
                    publication.Mode.ToString(),
                    publication.Status.ToString(),
                    publication.ExternalListingId,
                    publication.ListingUrl,
                    publication.ApplicationUrl,
                    publication.ManagementUrl,
                    publication.LastConfirmedExternalStatus,
                    publication.LastConfirmedAtUtc,
                    publication.CopyConfirmed,
                    publication.TermsConfirmed,
                    publication.PhotosConfirmed,
                    publication.ProviderWorkspaceOpened,
                    publication.PublishedContentVersion.HasValue
                        && publication.PublishedContentVersion.Value < listing.ContentVersion,
                    publication.PublishedContentVersion,
                    publication.LastDeliveryKey,
                    publication.LastDeliveryStatus,
                    publication.LastDeliveryError,
                    publication.LastDeliveryAttemptAtUtc,
                    publication.ExternalSignals.Select(signal => new ExternalListingSignalResponse(signal.Id, signal.SignalType,
                            signal.SuggestedExternalListingId, signal.SuggestedListingUrl,
                            signal.SuggestedExternalStatus, signal.Disposition.ToString(), signal.ReceivedAtUtc))
                        .ToArray()))
                .ToArray(),
            $"/scan?type=Lease&unitId={listing.UnitId}&returnTo=/units/{listing.UnitId}?tab=lease",
            listing.CreatedAt,
            listing.UpdatedAt);
}

public sealed class SaveListingWorkspaceRequest
{
    [MaxLength(40)] public string? Status { get; set; }
    [MaxLength(200)] public string? Headline { get; set; }
    [MaxLength(4000)] public string? Description { get; set; }
    [Range(0, 99999999)] public decimal? Rent { get; set; }
    [Range(0, 99999999)] public decimal? SecurityDeposit { get; set; }
    public DateTime? AvailableOn { get; set; }
    [MaxLength(1000)] public string? LeaseTerms { get; set; }
    [MaxLength(1000)] public string? PetPolicy { get; set; }
    [MaxLength(1000)] public string? Utilities { get; set; }
    [MaxLength(1000)] public string? Parking { get; set; }
    [MaxLength(2000)] public string? Amenities { get; set; }
    public SaveGuidedPublicationRequest? ZillowGuided { get; set; }
}

public sealed class SaveGuidedPublicationRequest
{
    [MaxLength(40)] public string? Status { get; set; }
    [MaxLength(200)] public string? ExternalListingId { get; set; }
    [MaxLength(1000)] public string? ListingUrl { get; set; }
    [MaxLength(1000)] public string? ApplicationUrl { get; set; }
    [MaxLength(1000)] public string? ManagementUrl { get; set; }
    [MaxLength(120)] public string? LastConfirmedExternalStatus { get; set; }
    public DateTime? LastConfirmedAtUtc { get; set; }
    public bool? CopyConfirmed { get; set; }
    public bool? TermsConfirmed { get; set; }
    public bool? PhotosConfirmed { get; set; }
    public bool? ProviderWorkspaceOpened { get; set; }
    public bool? MarkCurrentVersionPublished { get; set; }
}

public sealed class IngestExternalListingSignalRequest
{
    [Required, MaxLength(300)] public string ProviderMessageKey { get; set; } = string.Empty;
    [Required, MaxLength(80)] public string SignalType { get; set; } = string.Empty;
    [MaxLength(200)] public string? SuggestedExternalListingId { get; set; }
    [MaxLength(1000)] public string? SuggestedListingUrl { get; set; }
    [MaxLength(120)] public string? SuggestedExternalStatus { get; set; }
}
