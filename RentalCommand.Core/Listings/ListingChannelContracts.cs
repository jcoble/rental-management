namespace RentalCommand.Core.Listings;

public static class ListingProviderKeys
{
    public const string Zillow = "Zillow";

    /// <summary>Reserved for a later adapter; no ShowMojo integration is implemented in this slice.</summary>
    public const string ShowMojo = "ShowMojo";
}

/// <summary>
/// Future Connected-mode boundary. Implementations are provider adapters; domain and UI never branch on
/// Zillow-specific payloads. No adapter is registered until provider approval and credentials exist.
/// </summary>
public interface IListingChannelAdapter
{
    string ProviderKey { get; }
    Task<ListingPreparedPackage> PrepareAsync(PrepareListingPublicationCommand command, CancellationToken ct);
    Task<ListingPublicationDelivery> PublishAsync(PublishListingCommand command, CancellationToken ct);
    Task<ListingReconciliationResult> ReconcileAsync(ReconcileListingCommand command, CancellationToken ct);
    Task<ListingLeadReceipt> IngestLeadAsync(IngestListingLeadCommand command, CancellationToken ct);
}

public sealed record PrepareListingPublicationCommand(int RentalListingId, int PublicationId, int ContentVersion);
public sealed record PublishListingCommand(int PublicationId, string IdempotencyKey);
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
