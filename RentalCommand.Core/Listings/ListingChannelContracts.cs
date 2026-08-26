namespace RentalCommand.Core.Listings;

public static class ListingProviderKeys
{
    public const string Zillow = "Zillow";
}

/// <summary>
/// Provider-neutral Connected-mode boundary. A real adapter is registered only after provider approval;
/// Rental Command continues to own the canonical listing and passes an immutable package to the adapter.
/// </summary>
public interface IListingChannelAdapter
{
    string ProviderKey { get; }
    ListingChannelAvailability Availability { get; }
    Task<ListingPreparedPackage> PrepareAsync(PrepareListingPublicationCommand command, CancellationToken ct);
    Task<ListingPublicationDelivery> PublishAsync(PublishListingCommand command, CancellationToken ct);
    Task<ListingPublicationDelivery> UpdateAsync(UpdateListingCommand command, CancellationToken ct);
    Task<ListingPublicationDelivery> UnpublishAsync(UnpublishListingCommand command, CancellationToken ct);
    Task<ListingReconciliationResult> ReconcileAsync(ReconcileListingCommand command, CancellationToken ct);
    Task<ListingLeadReceipt> IngestLeadAsync(IngestListingLeadCommand command, CancellationToken ct);
}

public sealed record ListingChannelAvailability(bool Available, string State, string? Reason);

public sealed record ListingChannelPackage(
    int RentalListingId,
    int PublicationId,
    int ContentVersion,
    int PortfolioId,
    int PropertyId,
    int UnitId,
    string PropertyName,
    string AddressLine1,
    string? AddressLine2,
    string City,
    string State,
    string PostalCode,
    string UnitNumber,
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
    IReadOnlyList<ListingChannelPhoto> Photos);

public sealed record ListingChannelPhoto(
    int Position, string Category, string? Caption, int? StoredFileId, string? FileName, string? Sha256);
public sealed record PrepareListingPublicationCommand(
    ListingChannelPackage Package,
    string IdempotencyKey);
public sealed record PublishListingCommand(ListingChannelPackage Package, string PreparedPackageKey, string IdempotencyKey);
public sealed record UpdateListingCommand(ListingChannelPackage Package, string? ExternalListingId, string IdempotencyKey);
public sealed record UnpublishListingCommand(int PublicationId, string? ExternalListingId, string IdempotencyKey);
public sealed record ReconcileListingCommand(int PublicationId, string? ExternalListingId);
public sealed record IngestListingLeadCommand(int PublicationId, ListingLeadEnvelope Lead);
public sealed record ListingLeadEnvelope(
    string ProviderLeadKey,
    string? GivenName,
    string? FamilyName,
    string? Email,
    string? Phone,
    DateTime SubmittedAtUtc);
public sealed record ListingPreparedPackage(string PackageKey, int ContentVersion);
public sealed record ListingPublicationDelivery(string DeliveryKey, string Status, string? ExternalListingId, string? ListingUrl, string? Error);
public sealed record ListingReconciliationResult(string ExternalStatus, string? ListingUrl, DateTime ConfirmedAtUtc);
public sealed record ListingLeadReceipt(string ProviderLeadKey, bool Accepted);

public sealed class ListingChannelUnavailableException : InvalidOperationException
{
    public ListingChannelUnavailableException(string providerKey, string? reason)
        : base(reason ?? $"Connected publishing for {providerKey} is not configured.") { }
}
