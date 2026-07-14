using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using RentalCommand.Api.DTOs;
using RentalCommand.Core;
using RentalCommand.Core.Atomic;
using RentalCommand.Core.Authorization;
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
    private static readonly AtomicJsonResultCodec<ListingWorkspaceMutationResult> MutationCodec =
        new("listing-workspace.mutation.result.v1");
    private static readonly AtomicJsonResultCodec<ConnectedListingIntentResult> ConnectedIntentCodec =
        new("listing-workspace.connected-intent.result.v1");
    private static readonly AtomicJsonResultCodec<ConnectedListingPersistenceResult> ConnectedPersistenceCodec =
        new("listing-workspace.connected-persistence.result.v1");
    private static readonly AtomicJsonResultCodec<ConnectedListingPersistenceResult> ConnectedApplicationCodec =
        new("listing-workspace.connected-application.result.v1");
    private readonly RentalCommandDbContext _db;
    private readonly IAtomicInfrastructureWriteGate _infrastructureWrites;
    private readonly IFileStorage _files;
    private readonly IPendingFileUploadStore _pendingUploads;
    private readonly IListingChannelAdapterResolver _listingChannels;
    private readonly ILogger<ListingWorkspaceService> _logger;
    private readonly TimeProvider _time;
    private readonly IAtomicUnitOfWork _atomic;

    public ListingWorkspaceService(RentalCommandDbContext db,
        IAtomicInfrastructureWriteGate infrastructureWrites, IFileStorage files,
        IPendingFileUploadStore pendingUploads, IListingChannelAdapterResolver listingChannels,
        ILogger<ListingWorkspaceService> logger, TimeProvider time, IAtomicUnitOfWork atomic)
        => (_db, _infrastructureWrites, _files, _pendingUploads, _listingChannels, _logger, _time, _atomic) =
            (db, infrastructureWrites, files, pendingUploads, listingChannels, logger, time, atomic);

    public Task<bool> UnitExistsInPortfolioAsync(int portfolioId, int unitId, CancellationToken ct = default)
        => _db.Units.AsNoTracking().AnyAsync(unit => unit.Id == unitId && unit.PortfolioId == portfolioId, ct);

    public async Task<ListingWorkspaceResponse?> GetAsync(int portfolioId, int unitId, CancellationToken ct = default)
    {
        var listing = await WorkspaceQuery(portfolioId, unitId).FirstOrDefaultAsync(ct);
        return listing is null ? null : ToResponse(listing);
    }

    public async Task<ListingWorkspaceResponse?> GenerateAsync(
        WorkspaceReadScope scope, int unitId, string clientOperationId, CancellationToken ct = default)
    {
        var outcome = await _atomic.ExecuteAsync(
            Identity("listing-workspace.generate", scope.PortfolioId, unitId, clientOperationId),
            new GenerateListingWorkspaceCommand(
                scope.PortfolioId, unitId, scope.UserId, scope.SessionId,
                scope.AccessContextId, scope.AccessRevision),
            MutationCodec,
            ct);
        return outcome.Value.Outcome == ListingWorkspaceMutationOutcome.NotFound
            ? null
            : await GetAsync(scope.PortfolioId, unitId, ct);
    }

    public async Task<ListingWorkspaceResponse?> SaveAsync(
        WorkspaceReadScope scope, int unitId, SaveListingWorkspaceRequest request,
        string clientOperationId, CancellationToken ct = default)
    {
        var guided = request.ZillowGuided is null ? null : new GuidedListingValues(
            request.ZillowGuided.Status, request.ZillowGuided.ExternalListingId,
            request.ZillowGuided.ListingUrl, request.ZillowGuided.ApplicationUrl,
            request.ZillowGuided.ManagementUrl, request.ZillowGuided.LastConfirmedExternalStatus,
            request.ZillowGuided.LastConfirmedAtUtc, request.ZillowGuided.CopyConfirmed,
            request.ZillowGuided.TermsConfirmed, request.ZillowGuided.PhotosConfirmed,
            request.ZillowGuided.ProviderWorkspaceOpened,
            request.ZillowGuided.MarkCurrentVersionPublished);
        var outcome = await _atomic.ExecuteAsync(
            Identity("listing-workspace.save", scope.PortfolioId, unitId, clientOperationId),
            new SaveListingWorkspaceCommand(
                scope.PortfolioId, unitId, scope.UserId, scope.SessionId,
                scope.AccessContextId, scope.AccessRevision, request.Status, request.Headline,
                request.Description, request.Rent, request.SecurityDeposit, request.AvailableOn,
                request.LeaseTerms, request.PetPolicy, request.Utilities, request.Parking,
                request.Amenities, guided),
            MutationCodec,
            ct);
        return outcome.Value.Outcome == ListingWorkspaceMutationOutcome.NotFound
            ? null
            : await GetAsync(scope.PortfolioId, unitId, ct);
    }

    public async Task<ListingWorkspaceResponse?> AttachPhotoAsync(
        WorkspaceReadScope scope, int unitId, int photoId, string clientOperationId,
        string fileName, string contentType, byte[] bytes, CancellationToken ct = default)
    {
        var portfolioId = scope.PortfolioId;
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
        var admission = await _pendingUploads.PrepareAsync(portfolioId, scope.UserId, "listing-photo",
            operationId, fingerprint, fileName, contentType, bytes.LongLength, now, ct);

        if (admission.State == PendingFileUploadState.Finalized)
            return await GetAsync(portfolioId, unitId, ct);

        await using (var content = new MemoryStream(bytes, writable: false))
            await _files.UploadAtAsync(content, admission.StoragePath, fileName, contentType, ct);

        var outcome = await _atomic.ExecuteAsync(
            Identity("listing-workspace.photo.finalize", portfolioId, unitId, operationId),
            new FinalizeListingPhotoUploadCommand(
                portfolioId, unitId, scope.UserId, scope.SessionId, scope.AccessContextId,
                scope.AccessRevision, photoId, admission.Id, "listing-photo",
                OperationHash(operationId), fingerprint, admission.StoragePath, fileName,
                contentType, bytes.LongLength, sha256),
            MutationCodec,
            ct);
        return outcome.Value.Outcome == ListingWorkspaceMutationOutcome.NotFound
            ? null
            : await GetAsync(portfolioId, unitId, ct);
    }

    public async Task<ListingWorkspaceResponse?> UpdatePhotoAsync(
        WorkspaceReadScope scope, int unitId, int photoId,
        UpdateListingPhotoRequest request, string clientOperationId, CancellationToken ct = default)
    {
        var category = CleanRequired(request.Category, "Photo category");
        var caption = CleanOptional(request.Caption);
        var outcome = await _atomic.ExecuteAsync(
            Identity("listing-workspace.photo.update", scope.PortfolioId, unitId, clientOperationId),
            new UpdateListingPhotoCommand(
                scope.PortfolioId, unitId, scope.UserId, scope.SessionId,
                scope.AccessContextId, scope.AccessRevision, photoId, category, caption),
            MutationCodec,
            ct);
        return outcome.Value.Outcome == ListingWorkspaceMutationOutcome.NotFound
            ? null
            : await GetAsync(scope.PortfolioId, unitId, ct);
    }

    public async Task<ListingWorkspaceResponse?> RemovePhotoAsync(
        WorkspaceReadScope scope, int unitId, int photoId, string clientOperationId,
        CancellationToken ct = default)
    {
        var outcome = await _atomic.ExecuteAsync(
            Identity("listing-workspace.photo.remove", scope.PortfolioId, unitId, clientOperationId),
            new RemoveListingPhotoCommand(
                scope.PortfolioId, unitId, scope.UserId, scope.SessionId,
                scope.AccessContextId, scope.AccessRevision, photoId),
            MutationCodec,
            ct);
        return outcome.Value.Outcome == ListingWorkspaceMutationOutcome.NotFound
            ? null
            : await GetAsync(scope.PortfolioId, unitId, ct);
    }

    public async Task<ListingWorkspaceResponse?> ReorderPhotosAsync(
        WorkspaceReadScope scope, int unitId, ReorderListingPhotosRequest request,
        string clientOperationId, CancellationToken ct = default)
    {
        var outcome = await _atomic.ExecuteAsync(
            Identity("listing-workspace.photo.reorder", scope.PortfolioId, unitId, clientOperationId),
            new ReorderListingPhotosCommand(
                scope.PortfolioId, unitId, scope.UserId, scope.SessionId,
                scope.AccessContextId, scope.AccessRevision, request.PhotoIds.ToArray()),
            MutationCodec,
            ct);
        return outcome.Value.Outcome == ListingWorkspaceMutationOutcome.NotFound
            ? null
            : await GetAsync(scope.PortfolioId, unitId, ct);
    }

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

    public async Task<ListingWorkspaceResponse?> PrepareConnectedAsync(
        WorkspaceReadScope scope, int unitId, int publicationId, string clientOperationId,
        CancellationToken ct = default)
    {
        var operationId = CleanRequiredMax(clientOperationId, "Client operation ID", 160);
        var snapshot = await LoadConnectedSnapshotAsync(scope.PortfolioId, unitId, publicationId, ct);
        if (snapshot is null) return null;
        var admitted = await AdmitConnectedIntentAsync(
            scope, snapshot, operationId, ConnectedListingIntentOperation.Prepare, ct);
        if (admitted is null) return null;
        var adapter = RequireAvailableAdapter(snapshot.ProviderKey);
        ListingPreparedPackage prepared;
        try
        {
            prepared = await adapter.PrepareAsync(
                new PrepareListingPublicationCommand(snapshot.Package, operationId), ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await PersistConnectedResultAsync(snapshot, admitted, ListingPublicationStatus.Failed,
                operationId, "Failed", ex.Message, null, null, false,
                "Connected listing preparation failed", CancellationToken.None);
            throw;
        }
        if (prepared.ContentVersion != snapshot.Package.ContentVersion)
        {
            await PersistConnectedResultAsync(snapshot, admitted, ListingPublicationStatus.Failed,
                operationId, "Failed", "Provider prepared a different listing content version.",
                null, null, false, "Connected listing preparation returned incompatible content",
                CancellationToken.None);
            throw new DomainValidationException("The provider prepared a different listing content version.");
        }

        return await PersistConnectedResultAsync(snapshot, admitted, ListingPublicationStatus.Ready,
            EncodePreparedPackageKey(prepared), "Prepared", null, null, null, false,
            "Prepared Connected listing package", CancellationToken.None);
    }

    public Task<ListingWorkspaceResponse?> PublishConnectedAsync(
        WorkspaceReadScope scope, int unitId, int publicationId, string clientOperationId,
        CancellationToken ct = default)
        => DeliverConnectedAsync(scope, unitId, publicationId, clientOperationId,
            ConnectedListingOperation.Publish, ct);

    public Task<ListingWorkspaceResponse?> UpdateConnectedAsync(
        WorkspaceReadScope scope, int unitId, int publicationId, string clientOperationId,
        CancellationToken ct = default)
        => DeliverConnectedAsync(scope, unitId, publicationId, clientOperationId,
            ConnectedListingOperation.Update, ct);

    public Task<ListingWorkspaceResponse?> UnpublishConnectedAsync(
        WorkspaceReadScope scope, int unitId, int publicationId, string clientOperationId,
        CancellationToken ct = default)
        => DeliverConnectedAsync(scope, unitId, publicationId, clientOperationId,
            ConnectedListingOperation.Unpublish, ct);

    private async Task<ListingWorkspaceResponse?> DeliverConnectedAsync(
        WorkspaceReadScope scope, int unitId, int publicationId, string clientOperationId,
        ConnectedListingOperation operation, CancellationToken ct)
    {
        var operationId = CleanRequiredMax(clientOperationId, "Client operation ID", 160);

        var snapshot = await LoadConnectedSnapshotAsync(scope.PortfolioId, unitId, publicationId, ct);
        if (snapshot is null) return null;
        var intentOperation = operation switch
        {
            ConnectedListingOperation.Publish => ConnectedListingIntentOperation.Publish,
            ConnectedListingOperation.Update => ConnectedListingIntentOperation.Update,
            ConnectedListingOperation.Unpublish => ConnectedListingIntentOperation.Unpublish,
            _ => throw new ArgumentOutOfRangeException(nameof(operation)),
        };
        var admitted = await AdmitConnectedIntentAsync(scope, snapshot, operationId, intentOperation, ct);
        if (admitted is null)
            return null;
        var adapter = RequireAvailableAdapter(snapshot.ProviderKey);
        var preparedPackageKey = operation == ConnectedListingOperation.Publish
            ? DecodePreparedPackageKey(snapshot.PreparedPackageKey, snapshot.Package.ContentVersion)
            : null;
        ListingPublicationDelivery delivery;
        try
        {
            delivery = operation switch
            {
                ConnectedListingOperation.Publish => await adapter.PublishAsync(
                    new PublishListingCommand(snapshot.Package, preparedPackageKey!, operationId), ct),
                ConnectedListingOperation.Update => await adapter.UpdateAsync(
                    new UpdateListingCommand(snapshot.Package, snapshot.ExternalListingId, operationId), ct),
                ConnectedListingOperation.Unpublish => await adapter.UnpublishAsync(
                    new UnpublishListingCommand(publicationId, snapshot.ExternalListingId, operationId), ct),
                _ => throw new ArgumentOutOfRangeException(nameof(operation)),
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await PersistConnectedResultAsync(snapshot, admitted, ListingPublicationStatus.Failed,
                operationId, "Failed", ex.Message, null, null, false,
                $"Connected listing {operation.ToString().ToLowerInvariant()} failed", CancellationToken.None);
            throw;
        }

        var succeeded = string.IsNullOrWhiteSpace(delivery.Error);
        var status = !succeeded
            ? ListingPublicationStatus.Failed
            : operation == ConnectedListingOperation.Unpublish
                ? ListingPublicationStatus.Removed
                : ListingPublicationStatus.Published;
        return await PersistConnectedResultAsync(snapshot, admitted, status,
            delivery.DeliveryKey, delivery.Status, delivery.Error, delivery.ExternalListingId,
            delivery.ListingUrl, succeeded && operation != ConnectedListingOperation.Unpublish,
            $"Connected listing {operation.ToString().ToLowerInvariant()} completed", CancellationToken.None);
    }

    private async Task<ConnectedIntentAdmission?> AdmitConnectedIntentAsync(
        WorkspaceReadScope scope, ConnectedListingSnapshot snapshot, string clientOperationId,
        ConnectedListingIntentOperation operation, CancellationToken ct)
    {
        var identity = ClientIdentity(
            $"listing-workspace.connected.{operation.ToString().ToLowerInvariant()}.intent",
            scope.PortfolioId, snapshot.Package.UnitId, clientOperationId);
        var outcome = await _atomic.ExecuteAsync(
            identity,
            new AdmitConnectedListingIntentCommand(
                scope.PortfolioId, snapshot.Package.UnitId, scope.UserId, scope.SessionId,
                scope.AccessContextId, scope.AccessRevision, snapshot.Package.PublicationId,
                snapshot.Package.RentalListingId, snapshot.Package.ContentVersion, operation),
            ConnectedIntentCodec,
            ct);
        if (outcome.Value.Outcome == ListingWorkspaceMutationOutcome.NotFound) return null;
        if (outcome.Value.PropertyId != snapshot.Package.PropertyId
            || outcome.Value.RentalListingId != snapshot.Package.RentalListingId
            || outcome.Value.PublicationId != snapshot.Package.PublicationId
            || outcome.Value.ContentVersion != snapshot.Package.ContentVersion)
            throw new AtomicReceiptInvariantException("Connected listing admission receipt does not match the provider snapshot.");
        return new ConnectedIntentAdmission(outcome.AttemptId, identity, scope.UserId);
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

    private async Task<ListingWorkspaceResponse?> PersistConnectedResultAsync(
        ConnectedListingSnapshot snapshot, ConnectedIntentAdmission admission, ListingPublicationStatus status,
        string deliveryKey, string deliveryStatus, string? deliveryError, string? externalListingId,
        string? listingUrl, bool markPublishedVersion, string reason,
        CancellationToken ct)
    {
        deliveryKey = CleanRequiredMax(deliveryKey, "Provider delivery key", 200);
        deliveryStatus = CleanRequiredMax(deliveryStatus, "Provider delivery status", 80);
        deliveryError = CleanOptionalMax(deliveryError, "Provider delivery error", 2000);
        externalListingId = CleanOptionalMax(externalListingId, "External listing ID", 200);
        listingUrl = CleanOptionalMax(listingUrl, "External listing URL", 1000);
        var providerOutcomeFingerprint = OperationHash(JsonSerializer.Serialize(new
        {
            admission.AttemptId,
            status,
            deliveryKey,
            deliveryStatus,
            deliveryError,
            externalListingId,
            listingUrl,
            markPublishedVersion,
        }));
        var persistenceIdentity = InternalIdentity("listing-workspace.connected.persist-result",
            $"{admission.AttemptId:N}:{providerOutcomeFingerprint}");
        var persisted = await _atomic.ExecuteAsync(
            persistenceIdentity,
            new PersistConnectedListingResultCommand(
                snapshot.Package.PortfolioId, snapshot.Package.PropertyId, snapshot.Package.UnitId,
                admission.ActorUserId, snapshot.Package.PublicationId,
                snapshot.Package.RentalListingId, snapshot.Package.ContentVersion,
                admission.AttemptId, admission.Identity.CommandType, admission.Identity.IdempotencyKey,
                ConnectedIntentCodec.ContractName,
                status, deliveryKey, deliveryStatus, deliveryError, externalListingId, listingUrl,
                markPublishedVersion, reason),
            ConnectedPersistenceCodec,
            ct);
        if (persisted.Value.AdmissionAttemptId != admission.AttemptId)
            throw new AtomicReceiptInvariantException("Connected listing finalizer receipt belongs to another admission.");

        var applied = await _atomic.ExecuteAsync(
            InternalIdentity("listing-workspace.connected.apply-result",
                $"{persisted.AttemptId:N}"),
            new ApplyConnectedListingResultCommand(
                snapshot.Package.PortfolioId, snapshot.Package.PropertyId, snapshot.Package.UnitId,
                admission.ActorUserId, snapshot.Package.ContentVersion,
                persisted.AttemptId, persistenceIdentity.CommandType, persistenceIdentity.IdempotencyKey,
                ConnectedPersistenceCodec.ContractName, persisted.Value, markPublishedVersion, reason),
            ConnectedApplicationCodec,
            ct);
        if (applied.Value.AdmissionAttemptId != admission.AttemptId)
            throw new AtomicReceiptInvariantException("Connected listing application receipt belongs to another admission.");
        return await GetAsync(snapshot.Package.PortfolioId, snapshot.Package.UnitId, ct);
    }

    public async Task<ExternalListingSignalResponse?> IngestSignalAsync(int portfolioId, int unitId, int publicationId,
        string providerMessageKey, IngestExternalListingSignalRequest request, CancellationToken ct = default)
    {
        providerMessageKey = CleanRequiredMax(providerMessageKey, "Provider message key", 160);
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

    public async Task<ListingWorkspaceResponse?> ConfirmSignalAsync(
        WorkspaceReadScope scope, int unitId, int signalId, bool accept,
        string clientOperationId, CancellationToken ct = default)
    {
        var outcome = await _atomic.ExecuteAsync(
            Identity("listing-workspace.signal.confirm", scope.PortfolioId, unitId, clientOperationId),
            new ConfirmExternalListingSignalCommand(
                scope.PortfolioId, unitId, scope.UserId, scope.SessionId,
                scope.AccessContextId, scope.AccessRevision, signalId, accept),
            MutationCodec,
            ct);
        return outcome.Value.Outcome == ListingWorkspaceMutationOutcome.NotFound
            ? null
            : await GetAsync(scope.PortfolioId, unitId, ct);
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
    private static AtomicCommandIdentity Identity(
        string commandType, int portfolioId, int unitId, string clientOperationId)
        => ClientIdentity(commandType, portfolioId, unitId, clientOperationId);

    private static AtomicCommandIdentity ClientIdentity(
        string commandType, int portfolioId, int unitId, string clientOperationId)
    {
        var operationId = CleanRequiredMax(clientOperationId, "Client operation ID", 160);
        return new AtomicCommandIdentity(commandType,
            $"{portfolioId}:{unitId}:{OperationHash(operationId)}");
    }

    private static AtomicCommandIdentity InternalIdentity(string commandType, string stableKey) =>
        new(commandType, stableKey);

    private static string OperationHash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private ListingWorkspaceResponse ToResponse(RentalListing listing)
        => ListingWorkspaceResponse.FromEntity(listing, _listingChannels.GetAvailability);

    private sealed record ConnectedListingSnapshot(
        ListingChannelPackage Package,
        string ProviderKey,
        string? PreparedPackageKey,
        string? ExternalListingId);
    private sealed record ConnectedIntentAdmission(
        Guid AttemptId,
        AtomicCommandIdentity Identity,
        int ActorUserId);
    private enum ConnectedListingOperation { Publish, Update, Unpublish }
}
