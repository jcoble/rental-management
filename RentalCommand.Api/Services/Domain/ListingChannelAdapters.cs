using RentalCommand.Core.Listings;

namespace RentalCommand.Api.Services.Domain;

public sealed class ListingChannelAdapterResolver(IEnumerable<IListingChannelAdapter> adapters)
    : IListingChannelAdapterResolver
{
    private readonly IReadOnlyDictionary<string, IListingChannelAdapter> _adapters = adapters
        .ToDictionary(adapter => adapter.ProviderKey, StringComparer.OrdinalIgnoreCase);

    public IListingChannelAdapter Resolve(string providerKey)
        => _adapters.TryGetValue(providerKey, out var adapter)
            ? adapter
            : throw new ListingChannelUnavailableException(providerKey, $"No Connected adapter is registered for {providerKey}.");

    public ListingChannelAvailability GetAvailability(string providerKey)
        => _adapters.TryGetValue(providerKey, out var adapter)
            ? adapter.Availability
            : new ListingChannelAvailability(false, "Unavailable", $"No Connected adapter is registered for {providerKey}.");
}

/// <summary>
/// Safe production default. It exposes the complete Connected workflow without making a provider call
/// or requiring credentials. A provider adapter replaces this registration after approval.
/// </summary>
public sealed class DisabledZillowListingChannelAdapter : IListingChannelAdapter
{
    public string ProviderKey => ListingProviderKeys.Zillow;
    public ListingChannelAvailability Availability => new(false, "AwaitingProviderApproval",
        "Connected Zillow publishing is awaiting provider approval. Guided publishing remains available.");

    public Task<ListingPreparedPackage> PrepareAsync(PrepareListingPublicationCommand command, CancellationToken ct)
        => Unavailable<ListingPreparedPackage>();
    public Task<ListingPublicationDelivery> PublishAsync(PublishListingCommand command, CancellationToken ct)
        => Unavailable<ListingPublicationDelivery>();
    public Task<ListingPublicationDelivery> UpdateAsync(UpdateListingCommand command, CancellationToken ct)
        => Unavailable<ListingPublicationDelivery>();
    public Task<ListingPublicationDelivery> UnpublishAsync(UnpublishListingCommand command, CancellationToken ct)
        => Unavailable<ListingPublicationDelivery>();
    public Task<ListingReconciliationResult> ReconcileAsync(ReconcileListingCommand command, CancellationToken ct)
        => Unavailable<ListingReconciliationResult>();
    public Task<ListingLeadReceipt> IngestLeadAsync(IngestListingLeadCommand command, CancellationToken ct)
        => Unavailable<ListingLeadReceipt>();

    private Task<T> Unavailable<T>()
        => Task.FromException<T>(new ListingChannelUnavailableException(ProviderKey, Availability.Reason));
}
