using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using RentalCommand.Api.DTOs;
using RentalCommand.Api.Writes;
using RentalCommand.Core;
using RentalCommand.Core.Atomic;
using RentalCommand.Core.Authorization;
using RentalCommand.Core.Documents;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Interfaces;
using RentalCommand.Core.Listings;
using RentalCommand.Core.Time;
using RentalCommand.Data;
using RentalCommand.Data.Documents;
using RentalCommand.Data.Listings;

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
    private static readonly AtomicJsonResultCodec<IngestExternalListingSignalResult> SignalIngestCodec =
        new("listing-workspace.signal-ingest.result.v1");
    private readonly RentalCommandDbContext _db;
    private readonly IFileStorage _files;
    private readonly IPendingFileUploadStore _pendingUploads;
    private readonly IListingChannelAdapterResolver _listingChannels;
    private readonly ILogger<ListingWorkspaceService> _logger;
    private readonly TimeProvider _time;
    private readonly IRequestWriteExecutor? _writes;

    public ListingWorkspaceService(RentalCommandDbContext db,
        IFileStorage files,
        IPendingFileUploadStore pendingUploads, IListingChannelAdapterResolver listingChannels,
        ILogger<ListingWorkspaceService> logger, TimeProvider time,
        IRequestWriteExecutor? writes = null)
        => (_db, _files, _pendingUploads, _listingChannels, _logger, _time, _writes) =
            (db, files, pendingUploads, listingChannels, logger, time, writes);

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
        var command = new GenerateListingWorkspaceCommand(
            scope.PortfolioId, unitId, scope.UserId, scope.SessionId,
            scope.AccessContextId, scope.AccessRevision);
        var outcome = await ExecuteLocalAsync(
            "listing-workspace.generate", clientOperationId, command, GenerateListingAsync, ct);
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
        var command = new SaveListingWorkspaceCommand(
            scope.PortfolioId, unitId, scope.UserId, scope.SessionId,
            scope.AccessContextId, scope.AccessRevision, request.Status, request.Headline,
            request.Description, request.Rent, request.SecurityDeposit, request.AvailableOn,
            request.LeaseTerms, request.PetPolicy, request.Utilities, request.Parking,
            request.Amenities, guided);
        var outcome = await ExecuteLocalAsync(
            "listing-workspace.save", clientOperationId, command, SaveListingAsync, ct);
        return outcome.Value.Outcome == ListingWorkspaceMutationOutcome.NotFound
            ? null
            : await GetAsync(scope.PortfolioId, unitId, ct);
    }

    public async Task<ListingWorkspaceResponse?> AttachPhotoAsync(
        WorkspaceReadScope scope, int unitId, int photoId, string clientOperationId,
        string fileName, string contentType, byte[] bytes, CancellationToken ct = default)
    {
        var portfolioId = scope.PortfolioId;
        var operationId = CleanRequired(clientOperationId, "Request key");
        if (operationId.Length > 160) throw new DomainValidationException("A request key cannot exceed 160 characters.");
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

        var command = new FinalizeListingPhotoUploadCommand(
            portfolioId, unitId, scope.UserId, scope.SessionId, scope.AccessContextId,
            scope.AccessRevision, photoId, admission.Id, "listing-photo",
            OperationHash(operationId), fingerprint, admission.StoragePath, fileName,
            contentType, bytes.LongLength, sha256);
        var outcome = await ExecuteLocalAsync(
            "listing-workspace.photo.finalize", operationId, command, FinalizeListingPhotoAsync, ct);
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
        var command = new UpdateListingPhotoCommand(
            scope.PortfolioId, unitId, scope.UserId, scope.SessionId,
            scope.AccessContextId, scope.AccessRevision, photoId, category, caption);
        var outcome = await ExecuteLocalAsync(
            "listing-workspace.photo.update", clientOperationId, command, UpdateListingPhotoAsync, ct);
        return outcome.Value.Outcome == ListingWorkspaceMutationOutcome.NotFound
            ? null
            : await GetAsync(scope.PortfolioId, unitId, ct);
    }

    public async Task<ListingWorkspaceResponse?> RemovePhotoAsync(
        WorkspaceReadScope scope, int unitId, int photoId, string clientOperationId,
        CancellationToken ct = default)
    {
        var command = new RemoveListingPhotoCommand(
            scope.PortfolioId, unitId, scope.UserId, scope.SessionId,
            scope.AccessContextId, scope.AccessRevision, photoId);
        var outcome = await ExecuteLocalAsync(
            "listing-workspace.photo.remove", clientOperationId, command, RemoveListingPhotoAsync, ct);
        return outcome.Value.Outcome == ListingWorkspaceMutationOutcome.NotFound
            ? null
            : await GetAsync(scope.PortfolioId, unitId, ct);
    }

    public async Task<ListingWorkspaceResponse?> ReorderPhotosAsync(
        WorkspaceReadScope scope, int unitId, ReorderListingPhotosRequest request,
        string clientOperationId, CancellationToken ct = default)
    {
        var command = new ReorderListingPhotosCommand(
            scope.PortfolioId, unitId, scope.UserId, scope.SessionId,
            scope.AccessContextId, scope.AccessRevision, request.PhotoIds.ToArray());
        var outcome = await ExecuteLocalAsync(
            "listing-workspace.photo.reorder", clientOperationId, command, ReorderListingPhotosAsync, ct);
        return outcome.Value.Outcome == ListingWorkspaceMutationOutcome.NotFound
            ? null
            : await GetAsync(scope.PortfolioId, unitId, ct);
    }

    private Task<AtomicCommandOutcome<ListingWorkspaceMutationResult>> ExecuteLocalAsync<TCommand>(
        string operationName,
        string clientOperationId,
        TCommand command,
        Func<TCommand, IAtomicCommandContext, CancellationToken,
            Task<ListingWorkspaceMutationResult>> executeAsync,
        CancellationToken ct)
        where TCommand : notnull, IListingWorkspaceAtomicCommand
    {
        var identity = Identity(
            operationName, command.PortfolioId, command.UnitId, clientOperationId);
        var write = new TransactionalWrite<TCommand, ListingWorkspaceMutationResult>(
            operationName,
            WriteIdempotencyPolicy.Required,
            command,
            MutationCodec.ContractName,
            new WriteLockPlan(
                WriteLockProtocol.AuthorizationScopeUnit,
                WriteLock.For("AuthSession", command.AuthSessionId),
                WriteLock.For("WorkspaceAccessContext", command.AccessContextId),
                WriteLock.For("Portfolio", command.PortfolioId),
                WriteLock.For("Unit", command.UnitId)),
            executeAsync,
            AuthorizeLocalReplayAsync);
        return RequireWrites().ExecuteAsync(identity.IdempotencyKey, write, ct);
    }

    private async Task<ListingWorkspaceMutationResult> GenerateListingAsync(
        GenerateListingWorkspaceCommand command,
        IAtomicCommandContext context,
        CancellationToken ct)
    {
        var now = await ListingWorkspaceCommandSupport.BeginExecutionAsync(command, _db, context, ct);
        var seed = await ListingWorkspaceCommandSupport.AuthorizedUnits(command, _db, now)
            .Select(unit => new ListingSeed(
                unit.PortfolioId, unit.PropertyId, unit.Id, unit.Property!.Name,
                unit.Property.AddressLine1, unit.Property.AddressLine2, unit.Property.City,
                unit.Property.State, unit.Property.PostalCode, unit.UnitNumber,
                unit.Bedrooms, unit.Bathrooms, unit.SquareFeet, unit.MarketRent))
            .SingleOrDefaultAsync(ct);
        if (seed is null) return ListingWorkspaceCommandSupport.NotFound(command);

        var listing = await _db.RentalListings.SingleOrDefaultAsync(item =>
            item.PortfolioId == command.PortfolioId && item.UnitId == command.UnitId, ct);
        var created = listing is null;
        if (listing is null)
        {
            listing = ListingWorkspaceCommandSupport.CreateListing(seed, now);
            _db.Add(listing);
        }
        else
        {
            var changed = listing.PropertyId != seed.PropertyId
                || listing.Bedrooms != seed.Bedrooms
                || listing.Bathrooms != seed.Bathrooms
                || listing.SquareFeet != seed.SquareFeet;
            listing.PropertyId = seed.PropertyId;
            listing.Bedrooms = seed.Bedrooms;
            listing.Bathrooms = seed.Bathrooms;
            listing.SquareFeet = seed.SquareFeet;
            if (changed) listing.ContentVersion++;
            listing.UpdatedAt = now;
        }

        context.UseDatabaseWallClockForAudit(now);
        context.BindSemanticAudit(listing, ListingWorkspaceCommandSupport.Audit(
            command, nameof(RentalListing),
            created ? AuditLogOperation.Created : AuditLogOperation.Updated,
            created
                ? "Prepared provider-neutral rental listing workspace"
                : "Synchronized non-editable unit details without replacing customized listing content",
            created ? 0 : listing.Id));
        await context.FlushBusinessAsync(ct);
        ListingWorkspaceCommandSupport.StageUpdate(context, command, listing.Id, now, "generate");
        return ListingWorkspaceCommandSupport.Applied(command, listing.Id);
    }

    private async Task<ListingWorkspaceMutationResult> SaveListingAsync(
        SaveListingWorkspaceCommand command,
        IAtomicCommandContext context,
        CancellationToken ct)
    {
        var now = await ListingWorkspaceCommandSupport.BeginExecutionAsync(command, _db, context, ct);
        var listing = await ListingWorkspaceCommandSupport.AuthorizedListings(command, _db, now)
            .SingleOrDefaultAsync(ct);
        if (listing is null) return ListingWorkspaceCommandSupport.NotFound(command);

        var contentChanged = ListingWorkspaceCommandSupport.Apply(command, listing);
        if (contentChanged) listing.ContentVersion++;
        listing.UpdatedAt = now;
        var guided = command.ZillowGuided is null
            ? null
            : await _db.ListingPublications.SingleOrDefaultAsync(publication =>
                publication.RentalListingId == listing.Id
                && publication.PortfolioId == command.PortfolioId
                && publication.ProviderKey == ListingProviderKeys.Zillow
                && publication.Mode == ListingPublicationMode.Guided, ct);
        ListingWorkspaceCommandSupport.Apply(command.ZillowGuided, listing, guided, now);

        context.UseDatabaseWallClockForAudit(now);
        context.BindSemanticAudit(listing, ListingWorkspaceCommandSupport.Audit(
            command, nameof(RentalListing), AuditLogOperation.Updated,
            "Updated rental listing and Zillow Guided workspace", listing.Id));
        if (guided is not null)
        {
            context.BindSemanticAudit(guided, ListingWorkspaceCommandSupport.Audit(
                command, nameof(ListingPublication), AuditLogOperation.Updated,
                "Updated Zillow Guided workspace", guided.Id));
        }
        await context.FlushBusinessAsync(ct);
        ListingWorkspaceCommandSupport.StageUpdate(context, command, listing.Id, now, "save");
        return ListingWorkspaceCommandSupport.Applied(command, listing.Id);
    }

    private async Task<ListingWorkspaceMutationResult> FinalizeListingPhotoAsync(
        FinalizeListingPhotoUploadCommand command,
        IAtomicCommandContext context,
        CancellationToken ct)
    {
        var now = await ListingWorkspaceCommandSupport.BeginExecutionAsync(command, _db, context, ct);
        var photo = await (from authorizedListing in
                               ListingWorkspaceCommandSupport.AuthorizedListings(command, _db, now)
                           join item in _db.ListingPhotos on authorizedListing.Id equals item.RentalListingId
                           where item.Id == command.PhotoId && item.PortfolioId == command.PortfolioId
                           select item).SingleOrDefaultAsync(ct);
        if (photo is null) return ListingWorkspaceCommandSupport.NotFound(command);
        var listing = await _db.RentalListings.SingleAsync(item =>
            item.Id == photo.RentalListingId && item.PortfolioId == command.PortfolioId, ct);

        var pending = (await AtomicPendingFileUploadPersistence.LockPreparedSetAsync(
            _db, context, command.PortfolioId, command.ActorUserId,
            [new AtomicPendingFileUploadExpectation(
                command.PendingUploadId, command.Purpose, command.OperationKeyHash,
                command.RequestFingerprint, command.StoragePath, command.FileName,
                command.ContentType, command.SizeBytes)], ct)).Single();

        if (photo.StoredFileId is int oldFileId)
        {
            var old = await _db.StoredFiles.SingleAsync(file =>
                file.Id == oldFileId && file.PortfolioId == command.PortfolioId, ct);
            old.DeletedAt = now;
            context.StageOutbox(new OutboxMessage
            {
                PortfolioId = command.PortfolioId,
                MessageType = "blob-delete",
                Payload = JsonSerializer.Serialize(new
                    { storedFileId = old.Id, storagePath = old.FilePath }),
                IdempotencyKey = $"listing-photo-replace:{old.Id}",
                CreatedAtUtc = now,
                NextAttemptAtUtc = now,
            });
        }

        var stored = new StoredFile
        {
            PortfolioId = command.PortfolioId,
            FileName = command.FileName,
            FilePath = command.StoragePath,
            ContentType = command.ContentType,
            FileSize = command.SizeBytes,
            EntityType = nameof(ListingPhoto),
            EntityId = photo.Id,
            UploadedAt = now,
        };
        _db.Add(stored);
        await context.FlushBusinessAsync(ct);

        photo.StoredFileId = stored.Id;
        photo.FileName = command.FileName;
        photo.Sha256 = command.Sha256;
        pending.State = PendingFileUploadState.Finalized;
        pending.StoredFileId = stored.Id;
        pending.UpdatedAtUtc = now;
        listing.ContentVersion++;
        listing.UpdatedAt = now;
        context.UseDatabaseWallClockForAudit(now);
        context.BindSemanticAudit(listing, ListingWorkspaceCommandSupport.Audit(
            command, nameof(RentalListing), AuditLogOperation.Updated,
            "Attached listing photo", listing.Id));
        await context.FlushBusinessAsync(ct);
        context.StageSemanticEvent(new AtomicSemanticAudit(
            command.PortfolioId, nameof(ListingPhoto), photo.Id, AuditLogOperation.Updated,
            command.ActorUserId,
            NewValues: JsonSerializer.Serialize(new
            {
                photo.Position, photo.Category, photo.Caption, command.FileName, command.Sha256,
            }),
            ChangeReason: "Attached listing photo"), now);
        ListingWorkspaceCommandSupport.StageUpdate(context, command, listing.Id, now, "photo-attach");
        return ListingWorkspaceCommandSupport.Applied(command, listing.Id);
    }

    private async Task<ListingWorkspaceMutationResult> UpdateListingPhotoAsync(
        UpdateListingPhotoCommand command,
        IAtomicCommandContext context,
        CancellationToken ct)
    {
        var now = await ListingWorkspaceCommandSupport.BeginExecutionAsync(command, _db, context, ct);
        var target = await ListingWorkspaceCommandSupport.LoadPhotoTargetAsync(command, _db, now, ct);
        if (target is null) return ListingWorkspaceCommandSupport.NotFound(command);
        if (target.Photo.Category == command.Category && target.Photo.Caption == command.Caption)
            return ListingWorkspaceCommandSupport.NoChange(command, target.Listing.Id);

        target.Photo.Category = command.Category;
        target.Photo.Caption = command.Caption;
        target.Listing.ContentVersion++;
        target.Listing.UpdatedAt = now;
        ListingWorkspaceCommandSupport.AuditPhotoMutation(
            command, context, target, now, "Updated listing photo details");
        await context.FlushBusinessAsync(ct);
        ListingWorkspaceCommandSupport.StageUpdate(
            context, command, target.Listing.Id, now, "photo-update");
        return ListingWorkspaceCommandSupport.Applied(command, target.Listing.Id);
    }

    private async Task<ListingWorkspaceMutationResult> RemoveListingPhotoAsync(
        RemoveListingPhotoCommand command,
        IAtomicCommandContext context,
        CancellationToken ct)
    {
        var now = await ListingWorkspaceCommandSupport.BeginExecutionAsync(command, _db, context, ct);
        var target = await ListingWorkspaceCommandSupport.LoadPhotoTargetAsync(command, _db, now, ct);
        if (target is null) return ListingWorkspaceCommandSupport.NotFound(command);
        if (target.Photo.StoredFileId is not int fileId)
            return ListingWorkspaceCommandSupport.NoChange(command, target.Listing.Id);

        var stored = await _db.StoredFiles.SingleAsync(file =>
            file.Id == fileId && file.PortfolioId == command.PortfolioId, ct);
        stored.DeletedAt = now;
        target.Photo.StoredFileId = null;
        target.Photo.FileName = null;
        target.Photo.Sha256 = null;
        target.Listing.ContentVersion++;
        target.Listing.UpdatedAt = now;
        ListingWorkspaceCommandSupport.AuditPhotoMutation(
            command, context, target, now, "Removed listing photo attachment");
        await context.FlushBusinessAsync(ct);
        context.StageOutbox(new OutboxMessage
        {
            PortfolioId = command.PortfolioId,
            MessageType = "blob-delete",
            Payload = JsonSerializer.Serialize(new
                { storedFileId = stored.Id, storagePath = stored.FilePath }),
            IdempotencyKey = $"listing-photo-remove:{stored.Id}",
            CreatedAtUtc = now,
            NextAttemptAtUtc = now,
        });
        ListingWorkspaceCommandSupport.StageUpdate(
            context, command, target.Listing.Id, now, "photo-remove");
        return ListingWorkspaceCommandSupport.Applied(command, target.Listing.Id);
    }

    private async Task<ListingWorkspaceMutationResult> ReorderListingPhotosAsync(
        ReorderListingPhotosCommand command,
        IAtomicCommandContext context,
        CancellationToken ct)
    {
        var now = await ListingWorkspaceCommandSupport.BeginExecutionAsync(command, _db, context, ct);
        var listing = await ListingWorkspaceCommandSupport.AuthorizedListings(command, _db, now)
            .SingleOrDefaultAsync(ct);
        if (listing is null) return ListingWorkspaceCommandSupport.NotFound(command);
        var order = await AtomicListingPersistence.ReorderPhotosAsync(
            _db, context, command.PortfolioId, listing.Id, command.PhotoIds, ct);
        if (!order.IsValid)
            throw new DomainValidationException("Photo order must include every photo exactly once.");
        if (!order.HasChanges)
            return ListingWorkspaceCommandSupport.NoChange(command, listing.Id);

        listing.ContentVersion++;
        listing.UpdatedAt = now;
        context.UseDatabaseWallClockForAudit(now);
        context.BindSemanticAudit(listing, ListingWorkspaceCommandSupport.Audit(
            command, nameof(RentalListing), AuditLogOperation.Updated,
            "Reordered listing photo package", listing.Id));
        await context.FlushBusinessAsync(ct);
        context.StageSemanticEvent(new AtomicSemanticAudit(
            command.PortfolioId, nameof(ListingPhoto), listing.Id, AuditLogOperation.Updated,
            command.ActorUserId,
            NewValues: JsonSerializer.Serialize(new { command.PhotoIds }),
            ChangeReason: "Reordered listing photo package"), now);
        ListingWorkspaceCommandSupport.StageUpdate(
            context, command, listing.Id, now, "photo-reorder");
        return ListingWorkspaceCommandSupport.Applied(command, listing.Id);
    }

    private Task AuthorizeLocalReplayAsync<TCommand>(
        TCommand command, IAtomicCommandContext context, CancellationToken ct)
        where TCommand : IListingWorkspaceAtomicCommand =>
        ListingWorkspaceCommandSupport.AuthorizeReplayAsync(command, _db, ct);

    private IRequestWriteExecutor RequireWrites() => _writes ?? throw new InvalidOperationException(
        "The shared request write executor is required for local listing changes.");

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
        var operationId = CleanRequiredMax(clientOperationId, "Request key", 160);
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
        var operationId = CleanRequiredMax(clientOperationId, "Request key", 160);

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
        var command = new AdmitConnectedListingIntentCommand(
            scope.PortfolioId, snapshot.Package.UnitId, scope.UserId, scope.SessionId,
            scope.AccessContextId, scope.AccessRevision, snapshot.Package.PublicationId,
            snapshot.Package.RentalListingId, snapshot.Package.ContentVersion, operation);
        var outcome = await RequireWrites().ExecuteAsync(
            identity.IdempotencyKey, ConnectedListingWriteSupport.Write(command, _db), ct);
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
        var persistenceCommand = new PersistConnectedListingResultCommand(
            snapshot.Package.PortfolioId, snapshot.Package.PropertyId, snapshot.Package.UnitId,
            admission.ActorUserId, snapshot.Package.PublicationId,
            snapshot.Package.RentalListingId, snapshot.Package.ContentVersion,
            admission.AttemptId, admission.Identity.CommandType, admission.Identity.IdempotencyKey,
            ConnectedIntentCodec.ContractName,
            status, deliveryKey, deliveryStatus, deliveryError, externalListingId, listingUrl,
            markPublishedVersion, reason);
        var persisted = await RequireWrites().ExecuteAsync(
            persistenceIdentity.IdempotencyKey,
            ConnectedListingWriteSupport.Write(persistenceCommand, _db), ct);
        if (persisted.Value.AdmissionAttemptId != admission.AttemptId)
            throw new AtomicReceiptInvariantException("Connected listing finalizer receipt belongs to another admission.");

        var applicationIdentity = InternalIdentity(
            "listing-workspace.connected.apply-result", $"{persisted.AttemptId:N}");
        var applicationCommand = new ApplyConnectedListingResultCommand(
            snapshot.Package.PortfolioId, snapshot.Package.PropertyId, snapshot.Package.UnitId,
            admission.ActorUserId, snapshot.Package.ContentVersion,
            persisted.AttemptId, persistenceIdentity.CommandType, persistenceIdentity.IdempotencyKey,
            ConnectedPersistenceCodec.ContractName, persisted.Value, markPublishedVersion, reason);
        var applied = await RequireWrites().ExecuteAsync(
            applicationIdentity.IdempotencyKey,
            ConnectedListingWriteSupport.Write(applicationCommand, _db), ct);
        if (applied.Value.AdmissionAttemptId != admission.AttemptId)
            throw new AtomicReceiptInvariantException("Connected listing application receipt belongs to another admission.");
        return await GetAsync(snapshot.Package.PortfolioId, snapshot.Package.UnitId, ct);
    }

    public async Task<ExternalListingSignalResponse?> IngestSignalAsync(
        WorkspaceReadScope scope, int unitId, int publicationId,
        string providerMessageKey, IngestExternalListingSignalRequest request, CancellationToken ct = default)
    {
        providerMessageKey = CleanRequiredMax(providerMessageKey, "Provider message key", 160);
        var signalType = CleanRequired(request.SignalType, "Signal type");
        var suggestedExternalListingId = CleanOptional(request.SuggestedExternalListingId);
        var suggestedListingUrl = CleanOptional(request.SuggestedListingUrl);
        var suggestedExternalStatus = CleanOptional(request.SuggestedExternalStatus);
        var identity = Identity(
            "listing-workspace.signal.ingest", scope.PortfolioId, unitId, providerMessageKey);
        var command = new IngestExternalListingSignalCommand(
            scope.PortfolioId, unitId, scope.UserId, scope.SessionId,
            scope.AccessContextId, scope.AccessRevision, publicationId, providerMessageKey,
            signalType, suggestedExternalListingId, suggestedListingUrl,
            suggestedExternalStatus);
        var outcome = await RequireWrites().ExecuteAsync(
            identity.IdempotencyKey, ConnectedListingWriteSupport.Write(command, _db), ct);
        var admitted = outcome.Value;
        return !admitted.Found
            ? null
            : new ExternalListingSignalResponse(
                admitted.SignalId, admitted.SignalType, admitted.SuggestedExternalListingId,
                admitted.SuggestedListingUrl, admitted.SuggestedExternalStatus,
                admitted.Disposition.ToString(), admitted.ReceivedAtUtc);
    }

    public async Task<ListingWorkspaceResponse?> ConfirmSignalAsync(
        WorkspaceReadScope scope, int unitId, int signalId, bool accept,
        string clientOperationId, CancellationToken ct = default)
    {
        var identity = Identity(
            "listing-workspace.signal.confirm", scope.PortfolioId, unitId, clientOperationId);
        var command = new ConfirmExternalListingSignalCommand(
            scope.PortfolioId, unitId, scope.UserId, scope.SessionId,
            scope.AccessContextId, scope.AccessRevision, signalId, accept);
        var outcome = await RequireWrites().ExecuteAsync(
            identity.IdempotencyKey, ConnectedListingWriteSupport.Write(command, _db), ct);
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
        var operationId = CleanRequiredMax(clientOperationId, "Request key", 160);
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
