using System.Globalization;
using Microsoft.EntityFrameworkCore;
using RentalCommand.Api.DTOs;
using RentalCommand.Core;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Interfaces;
using RentalCommand.Core.Listings;
using RentalCommand.Core.Time;
using RentalCommand.Data;

namespace RentalCommand.Api.Services.Domain;

public sealed class ListingWorkspaceService : IListingWorkspaceService
{
    private const string EntityType = nameof(RentalListing);
    private const string Zillow = ListingProviderKeys.Zillow;
    private const string ZillowManagerUrl = "https://www.zillow.com/rental-manager/properties";
    private readonly RentalCommandDbContext _db;
    private readonly IDataUpdateService _updates;
    private readonly IAuditTrailService _audit;
    private readonly TimeProvider _time;

    public ListingWorkspaceService(RentalCommandDbContext db, IDataUpdateService updates, IAuditTrailService audit, TimeProvider time)
        => (_db, _updates, _audit, _time) = (db, updates, audit, time);

    public Task<bool> UnitExistsInPortfolioAsync(int portfolioId, int unitId, CancellationToken ct = default)
        => _db.Units.AsNoTracking().AnyAsync(unit => unit.Id == unitId && unit.PortfolioId == portfolioId, ct);

    public async Task<ListingWorkspaceResponse?> GetAsync(int portfolioId, int unitId, CancellationToken ct = default)
    {
        var listing = await WorkspaceQuery(portfolioId, unitId).FirstOrDefaultAsync(ct);
        return listing is null ? null : ListingWorkspaceResponse.FromEntity(listing);
    }

    public async Task<ListingWorkspaceResponse?> GenerateAsync(int portfolioId, int unitId, int userId, CancellationToken ct = default)
    {
        var seed = await SeedQuery(portfolioId, unitId).FirstOrDefaultAsync(ct);
        if (seed is null) return null;

        var strategy = _db.Database.CreateExecutionStrategy();
        ListingWorkspaceResponse? response = null;
        await strategy.ExecuteAsync(async () =>
        {
            await using var transaction = await _db.Database.BeginTransactionAsync(ct);
            var listing = await LoadTrackedAsync(portfolioId, unitId, ct);
            var now = _time.UtcNow();
            var created = listing is null;
            if (created)
            {
                listing = CreateListing(seed, now);
                _db.RentalListings.Add(listing);
            }
            else
            {
                var previousFacts = CaptureGeneratedFacts(listing!);
                ApplyUnitFacts(listing!, seed);
                if (previousFacts != CaptureGeneratedFacts(listing!))
                    listing!.ContentVersion++;
                listing.UpdatedAt = now;
            }

            await _db.SaveChangesAsync(ct);
            await _audit.LogAsync(portfolioId, EntityType, listing!.Id,
                created ? AuditLogOperation.Created : AuditLogOperation.Updated, userId: userId,
                changeReason: "Prepared provider-neutral rental listing workspace", ct: ct);
            await transaction.CommitAsync(ct);
            response = ListingWorkspaceResponse.FromEntity(listing);
        });

        await _updates.BroadcastEntityUpdateAsync(portfolioId, EntityType, response!.Id, response, ct);
        return response;
    }

    public async Task<ListingWorkspaceResponse?> SaveAsync(int portfolioId, int unitId, SaveListingWorkspaceRequest request,
        int userId, CancellationToken ct = default)
    {
        ListingWorkspaceResponse? response = null;
        var strategy = _db.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            await using var transaction = await _db.Database.BeginTransactionAsync(ct);
            var listing = await LoadTrackedAsync(portfolioId, unitId, ct);
            if (listing is null) return;

            var now = _time.UtcNow();
            var contentChanged = ApplyListingRequest(listing, request);
            if (contentChanged) listing.ContentVersion++;
            listing.UpdatedAt = now;
            var guidedPublication = request.ZillowGuided is null
                ? null
                : await _db.ListingPublications.FirstAsync(publication =>
                    publication.RentalListingId == listing.Id && publication.ProviderKey == Zillow
                    && publication.Mode == ListingPublicationMode.Guided, ct);
            ApplyGuidedRequest(listing, guidedPublication, request.ZillowGuided, now);

            await _db.SaveChangesAsync(ct);
            await _audit.LogAsync(portfolioId, EntityType, listing.Id, AuditLogOperation.Updated,
                userId: userId, changeReason: "Updated rental listing and Zillow Guided workspace", ct: ct);
            await transaction.CommitAsync(ct);
            response = ListingWorkspaceResponse.FromEntity(listing);
        });

        if (response is not null)
            await _updates.BroadcastEntityUpdateAsync(portfolioId, EntityType, response.Id, response, ct);
        return response;
    }

    public async Task<ExternalListingSignalResponse?> IngestSignalAsync(int portfolioId, int unitId, int publicationId,
        IngestExternalListingSignalRequest request, CancellationToken ct = default)
    {
        await using var transaction = await _db.Database.BeginTransactionAsync(ct);
        var existing = await _db.ExternalListingSignals.AsNoTracking()
            .Where(signal => signal.PortfolioId == portfolioId && signal.ProviderMessageKey == request.ProviderMessageKey)
            .Select(signal => new ExternalListingSignalResponse(signal.Id, signal.SignalType,
                signal.SuggestedExternalListingId, signal.SuggestedListingUrl, signal.SuggestedExternalStatus,
                signal.Disposition.ToString(), signal.ReceivedAtUtc))
            .FirstOrDefaultAsync(ct);
        if (existing is not null)
        {
            await transaction.CommitAsync(ct);
            return existing;
        }

        var publicationExists = await _db.ListingPublications.AsNoTracking().AnyAsync(publication =>
            publication.Id == publicationId && publication.PortfolioId == portfolioId
            && publication.RentalListing != null && publication.RentalListing.UnitId == unitId, ct);
        if (!publicationExists) return null;

        var signal = new ExternalListingSignal
        {
            PortfolioId = portfolioId,
            ListingPublicationId = publicationId,
            ProviderMessageKey = CleanRequired(request.ProviderMessageKey, "Provider message key"),
            SignalType = CleanRequired(request.SignalType, "Signal type"),
            SuggestedExternalListingId = CleanOptional(request.SuggestedExternalListingId),
            SuggestedListingUrl = CleanOptional(request.SuggestedListingUrl),
            SuggestedExternalStatus = CleanOptional(request.SuggestedExternalStatus),
            Disposition = ExternalListingSignalDisposition.Unconfirmed,
            ReceivedAtUtc = _time.UtcNow(),
        };
        _db.ExternalListingSignals.Add(signal);
        await _db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return new ExternalListingSignalResponse(signal.Id, signal.SignalType, signal.SuggestedExternalListingId,
            signal.SuggestedListingUrl, signal.SuggestedExternalStatus, signal.Disposition.ToString(), signal.ReceivedAtUtc);
    }

    public async Task<ListingWorkspaceResponse?> ConfirmSignalAsync(int portfolioId, int unitId, int signalId, bool accept,
        int userId, CancellationToken ct = default)
    {
        ListingWorkspaceResponse? response = null;
        await using var transaction = await _db.Database.BeginTransactionAsync(ct);
        var signal = await _db.ExternalListingSignals
            .Include(item => item.ListingPublication)!.ThenInclude(publication => publication!.RentalListing)
            .FirstOrDefaultAsync(item => item.Id == signalId && item.PortfolioId == portfolioId
                && item.ListingPublication!.RentalListing!.UnitId == unitId, ct);
        if (signal is null) return null;
        if (signal.Disposition != ExternalListingSignalDisposition.Unconfirmed)
            throw new DomainValidationException("This external listing signal was already reviewed.");

        var now = _time.UtcNow();
        signal.Disposition = accept ? ExternalListingSignalDisposition.Confirmed : ExternalListingSignalDisposition.Rejected;
        signal.ConfirmedByUserId = userId;
        signal.ConfirmedAtUtc = now;
        if (accept)
        {
            var publication = signal.ListingPublication!;
            publication.ExternalListingId = signal.SuggestedExternalListingId ?? publication.ExternalListingId;
            publication.ListingUrl = signal.SuggestedListingUrl ?? publication.ListingUrl;
            publication.LastConfirmedExternalStatus = signal.SuggestedExternalStatus ?? publication.LastConfirmedExternalStatus;
            publication.LastConfirmedAtUtc = now;
            publication.UpdatedAt = now;
        }
        await _db.SaveChangesAsync(ct);
        await _audit.LogAsync(portfolioId, nameof(ExternalListingSignal), signal.Id, AuditLogOperation.Updated,
            userId: userId, changeReason: accept ? "Confirmed external listing hint" : "Rejected external listing hint", ct: ct);
        await transaction.CommitAsync(ct);
        response = await GetAsync(portfolioId, unitId, ct);
        return response;
    }

    private IQueryable<RentalListing> WorkspaceQuery(int portfolioId, int unitId)
        => _db.RentalListings.AsNoTracking()
            .Where(listing => listing.PortfolioId == portfolioId && listing.UnitId == unitId)
            .Include(listing => listing.Photos.OrderBy(photo => photo.Position))
            .Include(listing => listing.Publications.OrderBy(publication => publication.Mode))
                .ThenInclude(publication => publication.ExternalSignals
                    .Where(signal => signal.Disposition == ExternalListingSignalDisposition.Unconfirmed)
                    .OrderByDescending(signal => signal.ReceivedAtUtc))
            .AsSingleQuery();

    private Task<RentalListing?> LoadTrackedAsync(int portfolioId, int unitId, CancellationToken ct)
        => _db.RentalListings
            .Include(listing => listing.Photos.OrderBy(photo => photo.Position))
            .Include(listing => listing.Publications.OrderBy(publication => publication.Mode))
                .ThenInclude(publication => publication.ExternalSignals
                    .Where(signal => signal.Disposition == ExternalListingSignalDisposition.Unconfirmed)
                    .OrderByDescending(signal => signal.ReceivedAtUtc))
            .AsSingleQuery()
            .FirstOrDefaultAsync(listing => listing.PortfolioId == portfolioId && listing.UnitId == unitId, ct);

    private IQueryable<ListingSeed> SeedQuery(int portfolioId, int unitId)
        => _db.Units.AsNoTracking()
            .Where(unit => unit.Id == unitId && unit.PortfolioId == portfolioId)
            .Select(unit => new ListingSeed(unit.PortfolioId, unit.PropertyId, unit.Id, unit.Property!.Name, unit.Property.AddressLine1,
                unit.Property.AddressLine2, unit.Property.City, unit.Property.State, unit.Property.PostalCode,
                unit.UnitNumber, unit.Bedrooms, unit.Bathrooms, unit.SquareFeet, unit.MarketRent));

    private static RentalListing CreateListing(ListingSeed seed, DateTime now)
    {
        var listing = new RentalListing
        {
            PortfolioId = seed.PortfolioId,
            PropertyId = seed.PropertyId,
            UnitId = seed.UnitId,
            CreatedAt = now,
            UpdatedAt = now,
        };
        ApplyUnitFacts(listing, seed);
        listing.Photos = DefaultPhotoManifest(seed.PortfolioId, now);
        listing.Publications =
        [
            new ListingPublication
            {
                PortfolioId = seed.PortfolioId, ProviderKey = Zillow, Mode = ListingPublicationMode.Guided,
                Status = ListingPublicationStatus.Draft, ManagementUrl = ZillowManagerUrl, CreatedAt = now, UpdatedAt = now,
            },
            new ListingPublication
            {
                PortfolioId = seed.PortfolioId, ProviderKey = Zillow, Mode = ListingPublicationMode.Connected,
                Status = ListingPublicationStatus.Draft, CreatedAt = now, UpdatedAt = now,
            },
        ];
        return listing;
    }

    private static void ApplyUnitFacts(RentalListing listing, ListingSeed seed)
    {
        listing.PropertyId = seed.PropertyId;
        listing.Rent = seed.MarketRent;
        listing.SecurityDeposit ??= seed.MarketRent > 0 ? seed.MarketRent : null;
        listing.Bedrooms = seed.Bedrooms;
        listing.Bathrooms = seed.Bathrooms;
        listing.SquareFeet = seed.SquareFeet;
        listing.Headline = $"{Rooms(seed.Bedrooms, "bed")} / {Rooms(seed.Bathrooms, "bath")} at {seed.PropertyName} - Unit {seed.UnitNumber}";
        listing.Description = BuildDescription(seed);
        listing.LeaseTerms ??= "12-month lease";
    }

    private static bool ApplyListingRequest(RentalListing listing, SaveListingWorkspaceRequest request)
    {
        var changed = false;
        if (!string.IsNullOrWhiteSpace(request.Status))
            listing.Status = Parse<RentalListingStatus>(request.Status, "Listing status");
        changed |= SetRequired(request.Headline, listing.Headline, value => listing.Headline = value, "Listing headline");
        changed |= SetRequired(request.Description, listing.Description, value => listing.Description = value, "Listing description");
        changed |= Set(request.Rent, listing.Rent, value => listing.Rent = value);
        changed |= Set(request.SecurityDeposit, listing.SecurityDeposit, value => listing.SecurityDeposit = value);
        changed |= Set(request.AvailableOn, listing.AvailableOn, value => listing.AvailableOn = value.ToUniversalTime());
        changed |= SetOptional(request.LeaseTerms, listing.LeaseTerms, value => listing.LeaseTerms = value);
        changed |= SetOptional(request.PetPolicy, listing.PetPolicy, value => listing.PetPolicy = value);
        changed |= SetOptional(request.Utilities, listing.Utilities, value => listing.Utilities = value);
        changed |= SetOptional(request.Parking, listing.Parking, value => listing.Parking = value);
        changed |= SetOptional(request.Amenities, listing.Amenities, value => listing.Amenities = value);
        return changed;
    }

    private static void ApplyGuidedRequest(RentalListing listing, ListingPublication? publication,
        SaveGuidedPublicationRequest? request, DateTime now)
    {
        if (request is null) return;
        if (publication is null) throw new DomainValidationException("Zillow Guided publication is missing.");
        if (!string.IsNullOrWhiteSpace(request.Status))
            publication.Status = Parse<ListingPublicationStatus>(request.Status, "Publication status");
        if (request.ExternalListingId is not null) publication.ExternalListingId = CleanOptional(request.ExternalListingId);
        if (request.ListingUrl is not null) publication.ListingUrl = CleanOptional(request.ListingUrl);
        if (request.ApplicationUrl is not null) publication.ApplicationUrl = CleanOptional(request.ApplicationUrl);
        if (request.ManagementUrl is not null) publication.ManagementUrl = CleanOptional(request.ManagementUrl);
        if (request.LastConfirmedExternalStatus is not null)
            publication.LastConfirmedExternalStatus = CleanOptional(request.LastConfirmedExternalStatus);
        if (request.LastConfirmedAtUtc is not null) publication.LastConfirmedAtUtc = request.LastConfirmedAtUtc.Value.ToUniversalTime();
        if (request.CopyConfirmed.HasValue) publication.CopyConfirmed = request.CopyConfirmed.Value;
        if (request.TermsConfirmed.HasValue) publication.TermsConfirmed = request.TermsConfirmed.Value;
        if (request.PhotosConfirmed.HasValue) publication.PhotosConfirmed = request.PhotosConfirmed.Value;
        if (request.ProviderWorkspaceOpened.HasValue) publication.ProviderWorkspaceOpened = request.ProviderWorkspaceOpened.Value;
        if (request.MarkCurrentVersionPublished == true)
        {
            publication.PublishedContentVersion = listing.ContentVersion;
            publication.Status = ListingPublicationStatus.Published;
            listing.Status = RentalListingStatus.Published;
        }
        publication.UpdatedAt = now;
    }

    private static List<ListingPhoto> DefaultPhotoManifest(int portfolioId, DateTime now)
        => new[] { "Exterior", "Living area", "Kitchen", "Primary bedroom", "Bathroom", "Utility and storage" }
            .Select((category, index) => new ListingPhoto
            {
                PortfolioId = portfolioId, Position = index + 1, Category = category,
                Caption = $"Add the best {category.ToLowerInvariant()} photo", CreatedAt = now,
            }).ToList();

    private static string BuildDescription(ListingSeed seed)
    {
        var size = seed.SquareFeet is > 0 ? $" Approximate size: {seed.SquareFeet.Value:N0} sq ft." : string.Empty;
        return $"Now available at {seed.PropertyName}, Unit {seed.UnitNumber}. This rental offers {Rooms(seed.Bedrooms, "bedroom")}, {Rooms(seed.Bathrooms, "bathroom")}, and listed rent of {seed.MarketRent.ToString("C0", CultureInfo.GetCultureInfo("en-US"))} per month.{size} Review all details before publishing.";
    }

    private static string Rooms(decimal value, string name)
        => $"{value.ToString(value % 1m == 0 ? "0" : "0.##", CultureInfo.InvariantCulture)} {name}";
    private static T Parse<T>(string value, string label) where T : struct, Enum
        => Enum.TryParse<T>(value, true, out var result) ? result : throw new DomainValidationException($"{label} is invalid.");
    private static string CleanRequired(string value, string label)
        => string.IsNullOrWhiteSpace(value) ? throw new DomainValidationException($"{label} is required.") : value.Trim();
    private static string? CleanOptional(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    private static bool SetRequired(string? value, string current, Action<string> setter, string label)
    {
        if (value is null) return false;
        var cleaned = CleanRequired(value, label);
        if (cleaned == current) return false;
        setter(cleaned);
        return true;
    }
    private static bool SetOptional(string? value, string? current, Action<string?> setter)
    {
        if (value is null) return false;
        var cleaned = CleanOptional(value);
        if (cleaned == current) return false;
        setter(cleaned);
        return true;
    }
    private static bool Set<T>(T? value, T? current, Action<T> setter) where T : struct
    {
        if (!value.HasValue || (current.HasValue && EqualityComparer<T>.Default.Equals(value.Value, current.Value))) return false;
        setter(value.Value);
        return true;
    }

    private static GeneratedFacts CaptureGeneratedFacts(RentalListing listing)
        => new(listing.PropertyId, listing.Headline, listing.Description, listing.Rent, listing.SecurityDeposit,
            listing.Bedrooms, listing.Bathrooms, listing.SquareFeet, listing.LeaseTerms);

    private sealed record ListingSeed(int PortfolioId, int PropertyId, int UnitId, string PropertyName, string AddressLine1,
        string? AddressLine2, string City, string State, string PostalCode, string UnitNumber,
        decimal Bedrooms, decimal Bathrooms, int? SquareFeet, decimal MarketRent);
    private sealed record GeneratedFacts(int PropertyId, string Headline, string Description, decimal Rent,
        decimal? SecurityDeposit, decimal Bedrooms, decimal Bathrooms, int? SquareFeet, string? LeaseTerms);
}
