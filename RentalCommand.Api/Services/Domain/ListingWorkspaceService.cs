using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;
using RentalCommand.Api.DTOs;
using RentalCommand.Core;
using RentalCommand.Core.Atomic;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Interfaces;
using RentalCommand.Core.Listings;
using RentalCommand.Core.Time;
using RentalCommand.Data;
using RentalCommand.Data.Documents;

namespace RentalCommand.Api.Services.Domain;

public sealed class ListingWorkspaceService : IListingWorkspaceService
{
    private const string EntityType = nameof(RentalListing);
    private const string Zillow = ListingProviderKeys.Zillow;
    private const string ZillowManagerUrl = "https://www.zillow.com/rental-manager/properties";
    internal const string PhotoOrderValidationSql = """
        WITH requested AS (
            SELECT item."PhotoId", item."Position"::integer
            FROM unnest(@photoIds::integer[]) WITH ORDINALITY AS item("PhotoId", "Position")
        )
        SELECT
            COUNT(*) = COUNT(DISTINCT requested."PhotoId")
            AND COUNT(*) = (
                SELECT COUNT(*)
                FROM "ListingPhotos" photo
                WHERE photo."RentalListingId" = @listingId
                  AND photo."PortfolioId" = @portfolioId)
            AND COUNT(*) = COUNT(photo."Id") AS "IsValid",
            COALESCE(BOOL_OR(photo."Position" <> requested."Position"), FALSE) AS "HasChanges"
        FROM requested
        LEFT JOIN "ListingPhotos" photo
          ON photo."Id" = requested."PhotoId"
         AND photo."RentalListingId" = @listingId
         AND photo."PortfolioId" = @portfolioId
        """;
    internal const string PhotoOrderUpdateSql = """
        UPDATE "ListingPhotos" AS photo
        SET "Position" = requested."Position"::integer
        FROM unnest(@photoIds::integer[]) WITH ORDINALITY AS requested("PhotoId", "Position")
        WHERE photo."Id" = requested."PhotoId"
          AND photo."RentalListingId" = @listingId
          AND photo."PortfolioId" = @portfolioId
        """;
    private readonly RentalCommandDbContext _db;
    private readonly IDataUpdateService _updates;
    private readonly IAuditTrailService _audit;
    private readonly IAtomicInfrastructureWriteGate _infrastructureWrites;
    private readonly IFileStorage _files;
    private readonly IPendingFileUploadStore _pendingUploads;
    private readonly IListingChannelAdapterResolver _listingChannels;
    private readonly ILogger<ListingWorkspaceService> _logger;
    private readonly TimeProvider _time;

    public ListingWorkspaceService(RentalCommandDbContext db, IDataUpdateService updates, IAuditTrailService audit,
        IAtomicInfrastructureWriteGate infrastructureWrites, IFileStorage files,
        IPendingFileUploadStore pendingUploads, IListingChannelAdapterResolver listingChannels,
        ILogger<ListingWorkspaceService> logger, TimeProvider time)
        => (_db, _updates, _audit, _infrastructureWrites, _files, _pendingUploads, _listingChannels, _logger, _time) =
            (db, updates, audit, infrastructureWrites, files, pendingUploads, listingChannels, logger, time);

    public Task<bool> UnitExistsInPortfolioAsync(int portfolioId, int unitId, CancellationToken ct = default)
        => _db.Units.AsNoTracking().AnyAsync(unit => unit.Id == unitId && unit.PortfolioId == portfolioId, ct);

    public async Task<ListingWorkspaceResponse?> GetAsync(int portfolioId, int unitId, CancellationToken ct = default)
    {
        var listing = await WorkspaceQuery(portfolioId, unitId).FirstOrDefaultAsync(ct);
        return listing is null ? null : ToResponse(listing);
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
                // A later sync is deliberately non-destructive. Headline, description, rent,
                // deposit, and lease terms are user-owned after initial preparation; only facts
                // that cannot be edited in this workspace are refreshed from the Unit.
                var previousFacts = CaptureSynchronizedUnitDetails(listing!);
                ApplySynchronizedUnitDetails(listing!, seed);
                if (previousFacts != CaptureSynchronizedUnitDetails(listing!))
                    listing!.ContentVersion++;
                listing!.UpdatedAt = now;
            }

            await _db.SaveChangesAsync(ct);
            await _audit.LogAsync(portfolioId, EntityType, listing!.Id,
                created ? AuditLogOperation.Created : AuditLogOperation.Updated, userId: userId,
                changeReason: created
                    ? "Prepared provider-neutral rental listing workspace"
                    : "Synchronized non-editable unit details without replacing customized listing content",
                ct: ct);
            await transaction.CommitAsync(ct);
            response = ToResponse(listing);
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
            response = ToResponse(listing);
        });

        if (response is not null)
            await _updates.BroadcastEntityUpdateAsync(portfolioId, EntityType, response.Id, response, ct);
        return response;
    }

    public async Task<ListingWorkspaceResponse?> AttachPhotoAsync(
        int portfolioId, int unitId, int photoId, int userId, string clientOperationId,
        string fileName, string contentType, byte[] bytes, CancellationToken ct = default)
    {
        var operationId = CleanRequired(clientOperationId, "Client operation ID");
        if (operationId.Length > 160) throw new DomainValidationException("Client operation ID cannot exceed 160 characters.");
        var photoExists = await _db.ListingPhotos.AsNoTracking().AnyAsync(photo =>
            photo.Id == photoId && photo.PortfolioId == portfolioId
            && photo.RentalListing != null && photo.RentalListing.UnitId == unitId, ct);
        if (!photoExists) return null;
        var sha256 = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        var fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            JsonSerializer.Serialize(new { portfolioId, unitId, photoId, fileName, contentType, bytes = bytes.LongLength, sha256 })))).ToLowerInvariant();
        var now = _time.UtcNow();
        var admission = await _pendingUploads.PrepareAsync(portfolioId, userId, "listing-photo",
            operationId, fingerprint, fileName, contentType, bytes.LongLength, now, ct);

        if (admission.State == PendingFileUploadState.Finalized)
            return await GetAsync(portfolioId, unitId, ct);

        await using (var content = new MemoryStream(bytes, writable: false))
            await _files.UploadAtAsync(content, admission.StoragePath, fileName, contentType, ct);

        ListingWorkspaceResponse? response = null;
        await using (var transaction = await _db.Database.BeginTransactionAsync(ct))
        {
            var photo = await _db.ListingPhotos.FirstOrDefaultAsync(item =>
                item.Id == photoId && item.PortfolioId == portfolioId
                && item.RentalListing != null && item.RentalListing.UnitId == unitId, ct);
            if (photo is null) return null;
            var listing = await _db.RentalListings.SingleAsync(item =>
                item.Id == photo.RentalListingId && item.PortfolioId == portfolioId, ct);

            var pending = await _db.PendingFileUploads.SingleOrDefaultAsync(upload =>
                upload.Id == admission.Id && upload.PortfolioId == portfolioId
                && upload.State == PendingFileUploadState.Prepared
                && upload.RequestFingerprint == fingerprint && upload.CleanupClaimToken == null, ct)
                ?? throw new DomainValidationException("The photo upload is no longer available to finalize.");

            if (photo.StoredFileId.HasValue)
            {
                var old = await _db.StoredFiles.SingleAsync(file =>
                    file.Id == photo.StoredFileId.Value && file.PortfolioId == portfolioId, ct);
                old.DeletedAt = now;
                _db.OutboxMessages.Add(new OutboxMessage
                {
                    PortfolioId = portfolioId,
                    MessageType = "blob-delete",
                    Payload = JsonSerializer.Serialize(new { storedFileId = old.Id, storagePath = old.FilePath }),
                    IdempotencyKey = $"listing-photo-replace:{old.Id}",
                    CreatedAtUtc = now,
                    NextAttemptAtUtc = now,
                });
            }

            var stored = new StoredFile
            {
                PortfolioId = portfolioId,
                FileName = fileName,
                FilePath = admission.StoragePath,
                ContentType = contentType,
                FileSize = bytes.LongLength,
                EntityType = nameof(ListingPhoto),
                EntityId = photo.Id,
                UploadedAt = now,
            };
            _db.StoredFiles.Add(stored);
            await _db.SaveChangesAsync(ct);

            photo.StoredFileId = stored.Id;
            photo.FileName = fileName;
            photo.Sha256 = sha256;
            pending.State = PendingFileUploadState.Finalized;
            pending.StoredFileId = stored.Id;
            pending.UpdatedAtUtc = now;
            listing.ContentVersion++;
            listing.UpdatedAt = now;
            await _db.SaveChangesAsync(ct);
            await _audit.LogAsync(portfolioId, nameof(ListingPhoto), photo.Id, AuditLogOperation.Updated,
                userId: userId, newValues: JsonSerializer.Serialize(new { photo.Position, photo.Category, photo.Caption, fileName, sha256 }),
                changeReason: "Attached listing photo", ct: ct);
            await transaction.CommitAsync(ct);
        }

        response = await GetAsync(portfolioId, unitId, ct);

        if (response is not null)
            await _updates.BroadcastEntityUpdateAsync(portfolioId, EntityType, response.Id, response, ct);
        return response;
    }

    public Task<ListingWorkspaceResponse?> UpdatePhotoAsync(int portfolioId, int unitId, int photoId,
        UpdateListingPhotoRequest request, int userId, CancellationToken ct = default)
    {
        var category = CleanRequired(request.Category, "Photo category");
        var caption = CleanOptional(request.Caption);
        return MutatePhotoPackageAsync(portfolioId, unitId, userId, "Updated listing photo details", async (listingId, _) =>
        {
            var photo = await _db.ListingPhotos.FirstOrDefaultAsync(item =>
                item.Id == photoId && item.PortfolioId == portfolioId && item.RentalListingId == listingId, ct);
            if (photo is null) return false;
            if (photo.Category == category && photo.Caption == caption) return false;
            photo.Category = category;
            photo.Caption = caption;
            return true;
        }, ct);
    }

    public Task<ListingWorkspaceResponse?> RemovePhotoAsync(int portfolioId, int unitId, int photoId, int userId,
        CancellationToken ct = default) =>
        MutatePhotoPackageAsync(portfolioId, unitId, userId, "Removed listing photo attachment", async (listingId, now) =>
        {
            var photo = await _db.ListingPhotos.FirstOrDefaultAsync(item =>
                item.Id == photoId && item.PortfolioId == portfolioId && item.RentalListingId == listingId, ct);
            if (photo?.StoredFileId is not int storedFileId) return false;
            var stored = await _db.StoredFiles.SingleAsync(file => file.Id == storedFileId && file.PortfolioId == portfolioId, ct);
            stored.DeletedAt = now;
            _db.OutboxMessages.Add(new OutboxMessage
            {
                PortfolioId = portfolioId, MessageType = "blob-delete",
                Payload = JsonSerializer.Serialize(new { storedFileId = stored.Id, storagePath = stored.FilePath }),
                IdempotencyKey = $"listing-photo-remove:{stored.Id}", CreatedAtUtc = now, NextAttemptAtUtc = now,
            });
            photo.StoredFileId = null;
            photo.FileName = null;
            photo.Sha256 = null;
            return true;
        }, ct);

    public Task<ListingWorkspaceResponse?> ReorderPhotosAsync(int portfolioId, int unitId,
        ReorderListingPhotosRequest request, int userId, CancellationToken ct = default) =>
        MutatePhotoPackageAsync(portfolioId, unitId, userId, "Reordered listing photo package", async (listingId, _) =>
        {
            var requested = request.PhotoIds.ToArray();
            var validation = await ValidatePhotoOrderAsync(listingId, portfolioId, requested, ct);
            if (!validation.IsValid)
                throw new DomainValidationException("Photo order must include every photo exactly once.");
            if (!validation.HasChanges) return false;

            // Avoid transient collisions with the unique (listing, position) index, then apply the
            // complete caller-supplied order in one set-based PostgreSQL UPDATE. No photo rows are
            // materialized and no per-row writes are issued.
            await _db.ListingPhotos
                .Where(photo => photo.RentalListingId == listingId && photo.PortfolioId == portfolioId)
                .ExecuteUpdateAsync(setters => setters.SetProperty(photo => photo.Position, photo => photo.Position + 1_000_000), ct);
            await ApplyPhotoOrderAsync(listingId, portfolioId, requested, ct);
            return true;
        }, ct);

    public async Task<ListingPhotoFileResult?> OpenPhotoAsync(int portfolioId, int unitId, int photoId, CancellationToken ct = default)
    {
        var file = await (from photo in _db.ListingPhotos.AsNoTracking()
                join listing in _db.RentalListings.AsNoTracking() on photo.RentalListingId equals listing.Id
                join stored in _db.StoredFiles.AsNoTracking() on photo.StoredFileId equals (int?)stored.Id
                where photo.Id == photoId && photo.PortfolioId == portfolioId && listing.UnitId == unitId
                select new { stored.FilePath, stored.ContentType, stored.FileName })
            .SingleOrDefaultAsync(ct);
        if (file is null) return null;
        try
        {
            return new ListingPhotoFileResult(await _files.DownloadAsync(file.FilePath, ct), file.ContentType, file.FileName);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Listing photo {PhotoId} blob is unavailable", photoId);
            return null;
        }
    }

    private async Task<ListingWorkspaceResponse?> MutatePhotoPackageAsync(
        int portfolioId, int unitId, int userId, string reason,
        Func<int, DateTime, Task<bool>> mutation, CancellationToken ct)
    {
        var listingFound = false;
        var strategy = _db.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            await using var transaction = await _db.Database.BeginTransactionAsync(ct);
            var listing = await _db.RentalListings
                .Where(listing => listing.PortfolioId == portfolioId && listing.UnitId == unitId)
                .SingleOrDefaultAsync(ct);
            if (listing is null) return;
            listingFound = true;
            var now = _time.UtcNow();
            if (await mutation(listing.Id, now))
            {
                listing.ContentVersion++;
                listing.UpdatedAt = now;
                await _db.SaveChangesAsync(ct);
                await _audit.LogAsync(portfolioId, EntityType, listing.Id, AuditLogOperation.Updated,
                    userId: userId, changeReason: reason, ct: ct);
            }
            await transaction.CommitAsync(ct);
        });
        if (!listingFound) return null;
        var response = await GetAsync(portfolioId, unitId, ct);
        if (response is not null)
            await _updates.BroadcastEntityUpdateAsync(portfolioId, EntityType, response.Id, response, ct);
        return response;
    }

    private async Task<PhotoOrderValidation> ValidatePhotoOrderAsync(
        int listingId, int portfolioId, int[] photoIds, CancellationToken ct)
    {
        var ids = new NpgsqlParameter("photoIds", NpgsqlDbType.Array | NpgsqlDbType.Integer) { Value = photoIds };
        return await _db.Database.SqlQueryRaw<PhotoOrderValidation>(PhotoOrderValidationSql,
                ids,
                new NpgsqlParameter("listingId", NpgsqlDbType.Integer) { Value = listingId },
                new NpgsqlParameter("portfolioId", NpgsqlDbType.Integer) { Value = portfolioId })
            .SingleAsync(ct);
    }

    private Task<int> ApplyPhotoOrderAsync(int listingId, int portfolioId, int[] photoIds, CancellationToken ct)
        => _db.Database.ExecuteSqlRawAsync(PhotoOrderUpdateSql,
            [
                new NpgsqlParameter("photoIds", NpgsqlDbType.Array | NpgsqlDbType.Integer) { Value = photoIds },
                new NpgsqlParameter("listingId", NpgsqlDbType.Integer) { Value = listingId },
                new NpgsqlParameter("portfolioId", NpgsqlDbType.Integer) { Value = portfolioId },
            ], ct);

    private sealed class PhotoOrderValidation
    {
        public bool IsValid { get; set; }
        public bool HasChanges { get; set; }
    }

    public async Task<ListingWorkspaceResponse?> PrepareConnectedAsync(
        int portfolioId, int unitId, int publicationId, int userId, CancellationToken ct = default)
    {
        var snapshot = await LoadConnectedSnapshotAsync(portfolioId, unitId, publicationId, ct);
        if (snapshot is null) return null;
        var adapter = RequireAvailableAdapter(snapshot.ProviderKey);
        var prepared = await adapter.PrepareAsync(new PrepareListingPublicationCommand(snapshot.Package), ct);
        if (prepared.ContentVersion != snapshot.Package.ContentVersion)
            throw new DomainValidationException("The provider prepared a different listing content version.");

        return await PersistConnectedResultAsync(snapshot, userId, ListingPublicationStatus.Ready,
            EncodePreparedPackageKey(prepared), "Prepared", null, null, null, false,
            "Prepared Connected listing package", ct);
    }

    public Task<ListingWorkspaceResponse?> PublishConnectedAsync(
        int portfolioId, int unitId, int publicationId, string clientOperationId, int userId,
        CancellationToken ct = default)
        => DeliverConnectedAsync(portfolioId, unitId, publicationId, clientOperationId, userId,
            ConnectedListingOperation.Publish, ct);

    public Task<ListingWorkspaceResponse?> UpdateConnectedAsync(
        int portfolioId, int unitId, int publicationId, string clientOperationId, int userId,
        CancellationToken ct = default)
        => DeliverConnectedAsync(portfolioId, unitId, publicationId, clientOperationId, userId,
            ConnectedListingOperation.Update, ct);

    public Task<ListingWorkspaceResponse?> UnpublishConnectedAsync(
        int portfolioId, int unitId, int publicationId, string clientOperationId, int userId,
        CancellationToken ct = default)
        => DeliverConnectedAsync(portfolioId, unitId, publicationId, clientOperationId, userId,
            ConnectedListingOperation.Unpublish, ct);

    private async Task<ListingWorkspaceResponse?> DeliverConnectedAsync(
        int portfolioId, int unitId, int publicationId, string clientOperationId, int userId,
        ConnectedListingOperation operation, CancellationToken ct)
    {
        var operationId = CleanRequired(clientOperationId, "Client operation ID");
        if (operationId.Length > 160)
            throw new DomainValidationException("Client operation ID cannot exceed 160 characters.");

        var snapshot = await LoadConnectedSnapshotAsync(portfolioId, unitId, publicationId, ct);
        if (snapshot is null) return null;
        var adapter = RequireAvailableAdapter(snapshot.ProviderKey);
        ListingPublicationDelivery delivery;
        try
        {
            delivery = operation switch
            {
                ConnectedListingOperation.Publish => await adapter.PublishAsync(
                    new PublishListingCommand(snapshot.Package,
                        DecodePreparedPackageKey(snapshot.PreparedPackageKey, snapshot.Package.ContentVersion),
                        operationId), ct),
                ConnectedListingOperation.Update => await adapter.UpdateAsync(
                    new UpdateListingCommand(snapshot.Package, snapshot.ExternalListingId, operationId), ct),
                ConnectedListingOperation.Unpublish => await adapter.UnpublishAsync(
                    new UnpublishListingCommand(publicationId, snapshot.ExternalListingId, operationId), ct),
                _ => throw new ArgumentOutOfRangeException(nameof(operation)),
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not DomainValidationException)
        {
            await PersistConnectedResultAsync(snapshot, userId, ListingPublicationStatus.Failed,
                operationId, "Failed", ex.Message, null, null, false,
                $"Connected listing {operation.ToString().ToLowerInvariant()} failed", ct);
            throw;
        }

        var succeeded = string.IsNullOrWhiteSpace(delivery.Error);
        var status = !succeeded
            ? ListingPublicationStatus.Failed
            : operation == ConnectedListingOperation.Unpublish
                ? ListingPublicationStatus.Removed
                : ListingPublicationStatus.Published;
        return await PersistConnectedResultAsync(snapshot, userId, status,
            delivery.DeliveryKey, delivery.Status, delivery.Error, delivery.ExternalListingId,
            delivery.ListingUrl, succeeded && operation != ConnectedListingOperation.Unpublish,
            $"Connected listing {operation.ToString().ToLowerInvariant()} completed", ct);
    }

    private IListingChannelAdapter RequireAvailableAdapter(string providerKey)
    {
        IListingChannelAdapter adapter;
        try { adapter = _listingChannels.Resolve(providerKey); }
        catch (ListingChannelUnavailableException exception)
        {
            throw new DomainValidationException(exception.Message, StatusCodes.Status409Conflict);
        }
        if (!adapter.Availability.Available)
            throw new DomainValidationException(
                adapter.Availability.Reason ?? $"Connected publishing for {providerKey} is not configured.",
                StatusCodes.Status409Conflict);
        return adapter;
    }

    private async Task<ConnectedListingSnapshot?> LoadConnectedSnapshotAsync(
        int portfolioId, int unitId, int publicationId, CancellationToken ct)
    {
        var publication = await _db.ListingPublications.AsNoTracking()
            .Where(item => item.Id == publicationId && item.PortfolioId == portfolioId
                && item.Mode == ListingPublicationMode.Connected
                && item.RentalListing != null && item.RentalListing.UnitId == unitId)
            .Include(item => item.RentalListing)!.ThenInclude(listing => listing!.Photos.OrderBy(photo => photo.Position))
            .Include(item => item.RentalListing)!.ThenInclude(listing => listing!.Unit)!.ThenInclude(unit => unit!.Property)
            .AsSingleQuery()
            .SingleOrDefaultAsync(ct);
        if (publication?.RentalListing is not { } listing) return null;
        var unit = listing.Unit ?? throw new DomainValidationException("The listing unit is missing.");
        var property = unit.Property ?? throw new DomainValidationException("The listing property is missing.");
        if (listing.Rent <= 0 || string.IsNullOrWhiteSpace(listing.Headline) || string.IsNullOrWhiteSpace(listing.Description))
            throw new DomainValidationException("Complete the listing headline, description, and rent before Connected publishing.");

        var package = new ListingChannelPackage(
            listing.Id, publication.Id, listing.ContentVersion, listing.PortfolioId, listing.PropertyId, listing.UnitId,
            property.Name, property.AddressLine1, property.AddressLine2, property.City, property.State,
            property.PostalCode, unit.UnitNumber,
            listing.Headline, listing.Description, listing.Rent, listing.SecurityDeposit, listing.Bedrooms,
            listing.Bathrooms, listing.SquareFeet, listing.AvailableOn, listing.LeaseTerms, listing.PetPolicy,
            listing.Utilities, listing.Parking, listing.Amenities,
            listing.Photos.Select(photo => new ListingChannelPhoto(
                photo.Position, photo.Category, photo.Caption, photo.StoredFileId, photo.FileName, photo.Sha256)).ToArray());
        return new ConnectedListingSnapshot(package, publication.ProviderKey, publication.LastDeliveryKey,
            publication.ExternalListingId);
    }

    private async Task<ListingWorkspaceResponse> PersistConnectedResultAsync(
        ConnectedListingSnapshot snapshot, int userId, ListingPublicationStatus status,
        string deliveryKey, string deliveryStatus, string? deliveryError, string? externalListingId,
        string? listingUrl, bool markPublishedVersion, string reason, CancellationToken ct)
    {
        deliveryKey = CleanRequiredMax(deliveryKey, "Provider delivery key", 200);
        deliveryStatus = CleanRequiredMax(deliveryStatus, "Provider delivery status", 80);
        deliveryError = CleanOptionalMax(deliveryError, "Provider delivery error", 2000);
        externalListingId = CleanOptionalMax(externalListingId, "External listing ID", 200);
        listingUrl = CleanOptionalMax(listingUrl, "External listing URL", 1000);
        ListingWorkspaceResponse? response = null;
        var strategy = _db.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            await using var transaction = await _db.Database.BeginTransactionAsync(ct);
            var publication = await _db.ListingPublications
                .Include(item => item.RentalListing)!.ThenInclude(listing => listing!.Photos.OrderBy(photo => photo.Position))
                .Include(item => item.RentalListing)!.ThenInclude(listing => listing!.Publications.OrderBy(item => item.Mode))
                    .ThenInclude(item => item.ExternalSignals
                        .Where(signal => signal.Disposition == ExternalListingSignalDisposition.Unconfirmed)
                        .OrderByDescending(signal => signal.ReceivedAtUtc))
                .AsSingleQuery()
                .SingleAsync(item => item.Id == snapshot.Package.PublicationId
                    && item.PortfolioId == snapshot.Package.PortfolioId
                    && item.Mode == ListingPublicationMode.Connected, ct);
            var listing = publication.RentalListing
                ?? throw new DomainValidationException("The canonical rental listing is missing.");
            if (listing.Id != snapshot.Package.RentalListingId || listing.UnitId != snapshot.Package.UnitId)
                throw new DomainValidationException("The Connected publication no longer belongs to this listing.");
            if (listing.ContentVersion != snapshot.Package.ContentVersion)
                throw new DomainValidationException("The listing changed while the provider request was running. Prepare it again.");

            var now = _time.UtcNow();
            publication.Status = status;
            publication.LastDeliveryKey = deliveryKey;
            publication.LastDeliveryStatus = deliveryStatus;
            publication.LastDeliveryError = CleanOptional(deliveryError);
            publication.LastDeliveryAttemptAtUtc = now;
            publication.ExternalListingId = CleanOptional(externalListingId) ?? publication.ExternalListingId;
            publication.ListingUrl = CleanOptional(listingUrl) ?? publication.ListingUrl;
            publication.UpdatedAt = now;
            if (markPublishedVersion)
            {
                publication.PublishedContentVersion = listing.ContentVersion;
                listing.Status = RentalListingStatus.Published;
            }
            else if (status == ListingPublicationStatus.Removed)
            {
                var anotherPublishedChannelExists = await _db.ListingPublications.AsNoTracking().AnyAsync(item =>
                    item.RentalListingId == listing.Id && item.PortfolioId == listing.PortfolioId
                    && item.Id != publication.Id && item.Status == ListingPublicationStatus.Published, ct);
                listing.Status = anotherPublishedChannelExists
                    ? RentalListingStatus.Published
                    : RentalListingStatus.ReadyToPublish;
            }
            listing.UpdatedAt = now;
            await _db.SaveChangesAsync(ct);
            await _audit.LogAsync(listing.PortfolioId, nameof(ListingPublication), publication.Id,
                AuditLogOperation.Updated, userId: userId, changeReason: reason, ct: ct);
            await transaction.CommitAsync(ct);
            response = ToResponse(listing);
        });

        await _updates.BroadcastEntityUpdateAsync(response!.PortfolioId, EntityType, response.Id, response, ct);
        return response;
    }

    public async Task<ExternalListingSignalResponse?> IngestSignalAsync(int portfolioId, int unitId, int publicationId,
        IngestExternalListingSignalRequest request, CancellationToken ct = default)
    {
        var providerMessageKey = CleanRequired(request.ProviderMessageKey, "Provider message key");
        var signalType = CleanRequired(request.SignalType, "Signal type");
        await using var transaction = await _db.Database.BeginTransactionAsync(ct);
        var publicationExists = await _db.ListingPublications.AsNoTracking().AnyAsync(publication =>
            publication.Id == publicationId && publication.PortfolioId == portfolioId
            && publication.RentalListing != null && publication.RentalListing.UnitId == unitId, ct);
        if (!publicationExists) return null;

        var suggestedExternalListingId = CleanOptional(request.SuggestedExternalListingId);
        var suggestedListingUrl = CleanOptional(request.SuggestedListingUrl);
        var suggestedExternalStatus = CleanOptional(request.SuggestedExternalStatus);
        var receivedAtUtc = _time.UtcNow();

        // The unique portfolio/message key is the concurrency boundary. PostgreSQL waits for
        // an in-flight conflicting insert, then this statement either owns the row or performs
        // a no-op. The following DB-side read therefore returns the same response to every
        // concurrent delivery instead of surfacing a unique-constraint error.
        using (_infrastructureWrites.BeginExternalListingSignalAdmission())
        {
            await _db.Database.ExecuteSqlInterpolatedAsync($$"""
                INSERT INTO "ExternalListingSignals"
                    ("PortfolioId", "ListingPublicationId", "ProviderMessageKey", "SignalType",
                     "SuggestedExternalListingId", "SuggestedListingUrl", "SuggestedExternalStatus",
                     "Disposition", "ReceivedAtUtc")
                VALUES
                    ({{portfolioId}}, {{publicationId}}, {{providerMessageKey}}, {{signalType}},
                     {{suggestedExternalListingId}}, {{suggestedListingUrl}}, {{suggestedExternalStatus}},
                     {{ExternalListingSignalDisposition.Unconfirmed.ToString()}}, {{receivedAtUtc}})
                ON CONFLICT ("PortfolioId", "ProviderMessageKey") DO NOTHING
                """, ct);
        }

        var admitted = await _db.ExternalListingSignals.AsNoTracking()
            .Where(signal => signal.PortfolioId == portfolioId && signal.ProviderMessageKey == providerMessageKey)
            .Select(signal => new
            {
                signal.ListingPublicationId,
                signal.Id,
                signal.SignalType,
                signal.SuggestedExternalListingId,
                signal.SuggestedListingUrl,
                signal.SuggestedExternalStatus,
                Disposition = signal.Disposition.ToString(),
                signal.ReceivedAtUtc,
            })
            .SingleAsync(ct);
        if (admitted.ListingPublicationId != publicationId)
            throw new DomainValidationException("Provider message key is already assigned to another listing publication.");

        await transaction.CommitAsync(ct);
        return new ExternalListingSignalResponse(admitted.Id, admitted.SignalType,
            admitted.SuggestedExternalListingId, admitted.SuggestedListingUrl, admitted.SuggestedExternalStatus,
            admitted.Disposition, admitted.ReceivedAtUtc);
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
        ApplySynchronizedUnitDetails(listing, seed);
        SeedCustomizableListingContent(listing, seed);
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

    private static void ApplySynchronizedUnitDetails(RentalListing listing, ListingSeed seed)
    {
        listing.PropertyId = seed.PropertyId;
        listing.Bedrooms = seed.Bedrooms;
        listing.Bathrooms = seed.Bathrooms;
        listing.SquareFeet = seed.SquareFeet;
    }

    private static void SeedCustomizableListingContent(RentalListing listing, ListingSeed seed)
    {
        listing.Rent = seed.MarketRent;
        listing.SecurityDeposit = seed.MarketRent > 0 ? seed.MarketRent : null;
        listing.Headline = $"{Rooms(seed.Bedrooms, "bed")} / {Rooms(seed.Bathrooms, "bath")} at {seed.PropertyName} - Unit {seed.UnitNumber}";
        listing.Description = BuildDescription(seed);
        listing.LeaseTerms = "12-month lease";
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
    private static string CleanRequiredMax(string value, string label, int maxLength)
    {
        var cleaned = CleanRequired(value, label);
        return cleaned.Length <= maxLength
            ? cleaned
            : throw new DomainValidationException($"{label} cannot exceed {maxLength} characters.");
    }
    private static string? CleanOptionalMax(string? value, string label, int maxLength)
    {
        var cleaned = CleanOptional(value);
        return cleaned is null || cleaned.Length <= maxLength
            ? cleaned
            : throw new DomainValidationException($"{label} cannot exceed {maxLength} characters.");
    }
    private static string EncodePreparedPackageKey(ListingPreparedPackage prepared)
        => $"{prepared.ContentVersion}:{Convert.ToBase64String(Encoding.UTF8.GetBytes(prepared.PackageKey))}";
    private static string DecodePreparedPackageKey(string? storedKey, int expectedContentVersion)
    {
        var separator = storedKey?.IndexOf(':') ?? -1;
        if (separator <= 0
            || !int.TryParse(storedKey![..separator], CultureInfo.InvariantCulture, out var preparedVersion)
            || preparedVersion != expectedContentVersion)
            throw new DomainValidationException("Prepare the current listing version before publishing.");
        try
        {
            return Encoding.UTF8.GetString(Convert.FromBase64String(storedKey[(separator + 1)..]));
        }
        catch (FormatException)
        {
            throw new DomainValidationException("Prepare the current listing version before publishing.");
        }
    }
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

    private static SynchronizedUnitDetails CaptureSynchronizedUnitDetails(RentalListing listing)
        => new(listing.PropertyId, listing.Bedrooms, listing.Bathrooms, listing.SquareFeet);

    private ListingWorkspaceResponse ToResponse(RentalListing listing)
        => ListingWorkspaceResponse.FromEntity(listing, _listingChannels.GetAvailability);

    private sealed record ListingSeed(int PortfolioId, int PropertyId, int UnitId, string PropertyName, string AddressLine1,
        string? AddressLine2, string City, string State, string PostalCode, string UnitNumber,
        decimal Bedrooms, decimal Bathrooms, int? SquareFeet, decimal MarketRent);
    private sealed record SynchronizedUnitDetails(int PropertyId, decimal Bedrooms, decimal Bathrooms, int? SquareFeet);
    private sealed record ConnectedListingSnapshot(
        ListingChannelPackage Package,
        string ProviderKey,
        string? PreparedPackageKey,
        string? ExternalListingId);
    private enum ConnectedListingOperation { Publish, Update, Unpublish }
}
