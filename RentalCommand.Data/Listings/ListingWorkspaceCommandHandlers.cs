using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using RentalCommand.Core;
using RentalCommand.Core.Atomic;
using RentalCommand.Core.Authorization;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Listings;
using RentalCommand.Core.Documents;
using RentalCommand.Data.Documents;

namespace RentalCommand.Data.Listings;

public sealed class GenerateListingWorkspaceHandler
    : IAtomicCommandHandler<GenerateListingWorkspaceCommand, ListingWorkspaceMutationResult>
{
    private readonly RentalCommandDbContext _db;

    public GenerateListingWorkspaceHandler(RentalCommandDbContext db) => _db = db;

    public async Task<ListingWorkspaceMutationResult> HandleAsync(
        GenerateListingWorkspaceCommand command, IAtomicCommandContext context, CancellationToken ct)
    {
        var now = await ListingWorkspaceCommandSupport.AuthorizeAndLockAsync(command, _db, context, ct);
        var seed = await ListingWorkspaceCommandSupport.AuthorizedUnits(command, _db, now)
            .Select(unit => new ListingSeed(
                unit.PortfolioId, unit.PropertyId, unit.Id, unit.Property!.Name,
                unit.Property.AddressLine1, unit.Property.AddressLine2, unit.Property.City,
                unit.Property.State, unit.Property.PostalCode, unit.UnitNumber,
                unit.Bedrooms, unit.Bathrooms, unit.SquareFeet, unit.MarketRent))
            .SingleOrDefaultAsync(ct);
        if (seed is null) return ListingWorkspaceCommandSupport.NotFound(command);

        var listing = await _db.Set<RentalListing>()
            .SingleOrDefaultAsync(item => item.PortfolioId == command.PortfolioId && item.UnitId == command.UnitId, ct);
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
            command, nameof(RentalListing), created ? AuditLogOperation.Created : AuditLogOperation.Updated,
            created
                ? "Prepared provider-neutral rental listing workspace"
                : "Synchronized non-editable unit details without replacing customized listing content"));
        await context.FlushBusinessAsync(ct);
        ListingWorkspaceCommandSupport.StageUpdate(context, command, listing.Id, now, "generate");
        return ListingWorkspaceCommandSupport.Applied(command, listing.Id);
    }

    public Task AuthorizeReplayAsync(GenerateListingWorkspaceCommand command, IAtomicCommandContext context, CancellationToken ct) =>
        ListingWorkspaceCommandSupport.AuthorizeReplayAsync(command, _db, ct);
}

public sealed class SaveListingWorkspaceHandler
    : IAtomicCommandHandler<SaveListingWorkspaceCommand, ListingWorkspaceMutationResult>
{
    private readonly RentalCommandDbContext _db;

    public SaveListingWorkspaceHandler(RentalCommandDbContext db) => _db = db;

    public async Task<ListingWorkspaceMutationResult> HandleAsync(
        SaveListingWorkspaceCommand command, IAtomicCommandContext context, CancellationToken ct)
    {
        var now = await ListingWorkspaceCommandSupport.AuthorizeAndLockAsync(command, _db, context, ct);
        var listing = await ListingWorkspaceCommandSupport.AuthorizedListings(command, _db, now)
            .SingleOrDefaultAsync(ct);
        if (listing is null) return ListingWorkspaceCommandSupport.NotFound(command);

        var contentChanged = ListingWorkspaceCommandSupport.Apply(command, listing);
        if (contentChanged) listing.ContentVersion++;
        listing.UpdatedAt = now;
        var guided = command.ZillowGuided is null
            ? null
            : await _db.Set<ListingPublication>().SingleOrDefaultAsync(publication =>
                publication.RentalListingId == listing.Id
                && publication.PortfolioId == command.PortfolioId
                && publication.ProviderKey == ListingProviderKeys.Zillow
                && publication.Mode == ListingPublicationMode.Guided, ct);
        ListingWorkspaceCommandSupport.Apply(command.ZillowGuided, listing, guided, now);

        context.UseDatabaseWallClockForAudit(now);
        context.BindSemanticAudit(listing, ListingWorkspaceCommandSupport.Audit(
            command, nameof(RentalListing), AuditLogOperation.Updated,
            "Updated rental listing and Zillow Guided workspace"));
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

    public Task AuthorizeReplayAsync(SaveListingWorkspaceCommand command, IAtomicCommandContext context, CancellationToken ct) =>
        ListingWorkspaceCommandSupport.AuthorizeReplayAsync(command, _db, ct);
}

public sealed class FinalizeListingPhotoUploadHandler
    : IAtomicCommandHandler<FinalizeListingPhotoUploadCommand, ListingWorkspaceMutationResult>
{
    private readonly RentalCommandDbContext _db;

    public FinalizeListingPhotoUploadHandler(RentalCommandDbContext db) => _db = db;

    public async Task<ListingWorkspaceMutationResult> HandleAsync(
        FinalizeListingPhotoUploadCommand command, IAtomicCommandContext context, CancellationToken ct)
    {
        var now = await ListingWorkspaceCommandSupport.AuthorizeAndLockAsync(command, _db, context, ct);
        var photo = await (from authorizedListing in ListingWorkspaceCommandSupport.AuthorizedListings(command, _db, now)
                           join item in _db.Set<ListingPhoto>() on authorizedListing.Id equals item.RentalListingId
                           where item.Id == command.PhotoId && item.PortfolioId == command.PortfolioId
                           select item).SingleOrDefaultAsync(ct);
        if (photo is null) return ListingWorkspaceCommandSupport.NotFound(command);
        var listing = await _db.Set<RentalListing>()
            .SingleAsync(item => item.Id == photo.RentalListingId && item.PortfolioId == command.PortfolioId, ct);

        var pending = (await AtomicPendingFileUploadPersistence.LockPreparedSetAsync(_db,
            context, command.PortfolioId,
            command.ActorUserId,
            [new AtomicPendingFileUploadExpectation(
                command.PendingUploadId, command.Purpose, command.OperationKeyHash,
                command.RequestFingerprint, command.StoragePath, command.FileName,
                command.ContentType, command.SizeBytes)],
            ct)).Single();

        if (photo.StoredFileId is int oldFileId)
        {
            var old = await _db.Set<StoredFile>()
                .SingleAsync(file => file.Id == oldFileId && file.PortfolioId == command.PortfolioId, ct);
            old.DeletedAt = now;
            context.StageOutbox(new OutboxMessage
            {
                PortfolioId = command.PortfolioId,
                MessageType = "blob-delete",
                Payload = JsonSerializer.Serialize(new { storedFileId = old.Id, storagePath = old.FilePath }),
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
            command, nameof(RentalListing), AuditLogOperation.Updated, "Attached listing photo"));
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

    public Task AuthorizeReplayAsync(FinalizeListingPhotoUploadCommand command, IAtomicCommandContext context, CancellationToken ct) =>
        ListingWorkspaceCommandSupport.AuthorizeReplayAsync(command, _db, ct);
}

public sealed class UpdateListingPhotoHandler
    : IAtomicCommandHandler<UpdateListingPhotoCommand, ListingWorkspaceMutationResult>
{
    private readonly RentalCommandDbContext _db;

    public UpdateListingPhotoHandler(RentalCommandDbContext db) => _db = db;

    public async Task<ListingWorkspaceMutationResult> HandleAsync(
        UpdateListingPhotoCommand command, IAtomicCommandContext context, CancellationToken ct)
    {
        var now = await ListingWorkspaceCommandSupport.AuthorizeAndLockAsync(command, _db, context, ct);
        var target = await ListingWorkspaceCommandSupport.LoadPhotoTargetAsync(command, _db, now, ct);
        if (target is null) return ListingWorkspaceCommandSupport.NotFound(command);
        if (target.Photo.Category == command.Category && target.Photo.Caption == command.Caption)
            return ListingWorkspaceCommandSupport.NoChange(command, target.Listing.Id);

        target.Photo.Category = command.Category;
        target.Photo.Caption = command.Caption;
        target.Listing.ContentVersion++;
        target.Listing.UpdatedAt = now;
        ListingWorkspaceCommandSupport.AuditPhotoMutation(command, context, target, now, "Updated listing photo details");
        await context.FlushBusinessAsync(ct);
        ListingWorkspaceCommandSupport.StageUpdate(context, command, target.Listing.Id, now, "photo-update");
        return ListingWorkspaceCommandSupport.Applied(command, target.Listing.Id);
    }

    public Task AuthorizeReplayAsync(UpdateListingPhotoCommand command, IAtomicCommandContext context, CancellationToken ct) =>
        ListingWorkspaceCommandSupport.AuthorizeReplayAsync(command, _db, ct);
}

public sealed class RemoveListingPhotoHandler
    : IAtomicCommandHandler<RemoveListingPhotoCommand, ListingWorkspaceMutationResult>
{
    private readonly RentalCommandDbContext _db;

    public RemoveListingPhotoHandler(RentalCommandDbContext db) => _db = db;

    public async Task<ListingWorkspaceMutationResult> HandleAsync(
        RemoveListingPhotoCommand command, IAtomicCommandContext context, CancellationToken ct)
    {
        var now = await ListingWorkspaceCommandSupport.AuthorizeAndLockAsync(command, _db, context, ct);
        var target = await ListingWorkspaceCommandSupport.LoadPhotoTargetAsync(command, _db, now, ct);
        if (target is null) return ListingWorkspaceCommandSupport.NotFound(command);
        if (target.Photo.StoredFileId is not int fileId)
            return ListingWorkspaceCommandSupport.NoChange(command, target.Listing.Id);

        var stored = await _db.Set<StoredFile>()
            .SingleAsync(file => file.Id == fileId && file.PortfolioId == command.PortfolioId, ct);
        stored.DeletedAt = now;
        target.Photo.StoredFileId = null;
        target.Photo.FileName = null;
        target.Photo.Sha256 = null;
        target.Listing.ContentVersion++;
        target.Listing.UpdatedAt = now;
        ListingWorkspaceCommandSupport.AuditPhotoMutation(command, context, target, now, "Removed listing photo attachment");
        await context.FlushBusinessAsync(ct);
        context.StageOutbox(new OutboxMessage
        {
            PortfolioId = command.PortfolioId,
            MessageType = "blob-delete",
            Payload = JsonSerializer.Serialize(new { storedFileId = stored.Id, storagePath = stored.FilePath }),
            IdempotencyKey = $"listing-photo-remove:{stored.Id}",
            CreatedAtUtc = now,
            NextAttemptAtUtc = now,
        });
        ListingWorkspaceCommandSupport.StageUpdate(context, command, target.Listing.Id, now, "photo-remove");
        return ListingWorkspaceCommandSupport.Applied(command, target.Listing.Id);
    }

    public Task AuthorizeReplayAsync(RemoveListingPhotoCommand command, IAtomicCommandContext context, CancellationToken ct) =>
        ListingWorkspaceCommandSupport.AuthorizeReplayAsync(command, _db, ct);
}

public sealed class ReorderListingPhotosHandler
    : IAtomicCommandHandler<ReorderListingPhotosCommand, ListingWorkspaceMutationResult>
{
    private readonly RentalCommandDbContext _db;

    public ReorderListingPhotosHandler(RentalCommandDbContext db) => _db = db;

    public async Task<ListingWorkspaceMutationResult> HandleAsync(
        ReorderListingPhotosCommand command, IAtomicCommandContext context, CancellationToken ct)
    {
        var now = await ListingWorkspaceCommandSupport.AuthorizeAndLockAsync(command, _db, context, ct);
        var listing = await ListingWorkspaceCommandSupport.AuthorizedListings(command, _db, now)
            .SingleOrDefaultAsync(ct);
        if (listing is null) return ListingWorkspaceCommandSupport.NotFound(command);
        var order = await AtomicListingPersistence.ReorderPhotosAsync(_db,
            context, command.PortfolioId, listing.Id, command.PhotoIds, ct);
        if (!order.IsValid)
            throw new DomainValidationException("Photo order must include every photo exactly once.");
        if (!order.HasChanges) return ListingWorkspaceCommandSupport.NoChange(command, listing.Id);

        listing.ContentVersion++;
        listing.UpdatedAt = now;
        context.UseDatabaseWallClockForAudit(now);
        context.BindSemanticAudit(listing, ListingWorkspaceCommandSupport.Audit(
            command, nameof(RentalListing), AuditLogOperation.Updated, "Reordered listing photo package"));
        await context.FlushBusinessAsync(ct);
        context.StageSemanticEvent(new AtomicSemanticAudit(
            command.PortfolioId, nameof(ListingPhoto), listing.Id, AuditLogOperation.Updated,
            command.ActorUserId,
            NewValues: JsonSerializer.Serialize(new { command.PhotoIds }),
            ChangeReason: "Reordered listing photo package"), now);
        ListingWorkspaceCommandSupport.StageUpdate(context, command, listing.Id, now, "photo-reorder");
        return ListingWorkspaceCommandSupport.Applied(command, listing.Id);
    }

    public Task AuthorizeReplayAsync(ReorderListingPhotosCommand command, IAtomicCommandContext context, CancellationToken ct) =>
        ListingWorkspaceCommandSupport.AuthorizeReplayAsync(command, _db, ct);
}

public sealed class AdmitConnectedListingIntentHandler
    : IAtomicCommandHandler<AdmitConnectedListingIntentCommand, ConnectedListingIntentResult>
{
    private readonly RentalCommandDbContext _db;

    public AdmitConnectedListingIntentHandler(RentalCommandDbContext db) => _db = db;

    public async Task<ConnectedListingIntentResult> HandleAsync(
        AdmitConnectedListingIntentCommand command, IAtomicCommandContext context, CancellationToken ct)
    {
        var now = await ListingWorkspaceCommandSupport.AuthorizeAndLockAsync(command, _db, context, ct);
        var target = await (from listing in ListingWorkspaceCommandSupport.AuthorizedListings(command, _db, now)
                            join publication in _db.Set<ListingPublication>()
                                on listing.Id equals publication.RentalListingId
                            where listing.Id == command.RentalListingId
                                && listing.ContentVersion == command.ExpectedContentVersion
                                && publication.Id == command.PublicationId
                                && publication.PortfolioId == command.PortfolioId
                                && publication.Mode == ListingPublicationMode.Connected
                            select new { listing.Id, listing.PropertyId, listing.ContentVersion, PublicationId = publication.Id })
            .SingleOrDefaultAsync(ct);
        return target is null
            ? new ConnectedListingIntentResult(ListingWorkspaceMutationOutcome.NotFound,
                command.PortfolioId, null, command.UnitId, null, null, null)
            : new ConnectedListingIntentResult(ListingWorkspaceMutationOutcome.Applied,
                command.PortfolioId, target.PropertyId, command.UnitId,
                target.Id, target.PublicationId, target.ContentVersion);
    }

    public Task AuthorizeReplayAsync(AdmitConnectedListingIntentCommand command, IAtomicCommandContext context, CancellationToken ct) =>
        ListingWorkspaceCommandSupport.AuthorizeReplayAsync(command, _db, ct);
}

public sealed class PersistConnectedListingResultHandler
    : IAtomicCommandHandler<PersistConnectedListingResultCommand, ConnectedListingPersistenceResult>
{
    private readonly RentalCommandDbContext _db;

    public PersistConnectedListingResultHandler(RentalCommandDbContext db) => _db = db;

    public async Task<ConnectedListingPersistenceResult> HandleAsync(
        PersistConnectedListingResultCommand command, IAtomicCommandContext context, CancellationToken ct)
    {
        await ValidateAdmissionAsync(command, _db, ct);
        return new ConnectedListingPersistenceResult(
            ListingWorkspaceMutationOutcome.Applied,
            command.PortfolioId, command.PropertyId, command.UnitId, command.RentalListingId,
            command.PublicationId, command.AdmissionAttemptId,
            AppliedToCurrentPublication: false, ReconciliationRequired: true,
            command.Status, command.DeliveryKey,
            command.DeliveryStatus, command.DeliveryError, command.ExternalListingId, command.ListingUrl);
    }

    public Task AuthorizeReplayAsync(PersistConnectedListingResultCommand command, IAtomicCommandContext context, CancellationToken ct) =>
        ValidateAdmissionAsync(command, _db, ct);

    internal static async Task ValidateAdmissionAsync(
        PersistConnectedListingResultCommand command,
        RentalCommandDbContext db,
        CancellationToken ct)
    {
        var expectedResult = JsonSerializer.Serialize(new ConnectedListingIntentResult(
            ListingWorkspaceMutationOutcome.Applied, command.PortfolioId, command.PropertyId,
            command.UnitId, command.RentalListingId, command.PublicationId,
            command.ExpectedContentVersion));
        var admitted = await db.Set<AtomicCommandReceipt>().AsNoTracking().AnyAsync(receipt =>
            receipt.AttemptId == command.AdmissionAttemptId
            && receipt.CommandType == command.AdmissionCommandType
            && receipt.IdempotencyKey == command.AdmissionIdempotencyKey
            && receipt.Status == AtomicCommandReceiptStatus.Completed
            && receipt.ResultContract == command.AdmissionResultContract
            && receipt.ResultJson == expectedResult, ct);
        if (!admitted)
            throw new AtomicReceiptInvariantException(
                $"Connected listing result has no matching admitted intent {command.AdmissionAttemptId}.");
    }
}

public sealed class ApplyConnectedListingResultHandler
    : IAtomicCommandHandler<ApplyConnectedListingResultCommand, ConnectedListingPersistenceResult>
{
    private readonly RentalCommandDbContext _db;

    public ApplyConnectedListingResultHandler(RentalCommandDbContext db) => _db = db;

    public async Task<ConnectedListingPersistenceResult> HandleAsync(
        ApplyConnectedListingResultCommand command, IAtomicCommandContext context, CancellationToken ct)
    {
        await ValidateProviderResultAsync(command, _db, ct);
        await context.AcquireLockAsync("Unit", command.UnitId, ct);
        var now = await context.ReadDatabaseClockUtcAsync(ct);
        var provider = command.ProviderResult;
        var target = await (from listing in _db.Set<RentalListing>()
                            join publication in _db.Set<ListingPublication>()
                                on listing.Id equals publication.RentalListingId
                            where listing.Id == provider.RentalListingId
                                && listing.PortfolioId == command.PortfolioId
                                && listing.PropertyId == command.PropertyId
                                && listing.UnitId == command.UnitId
                                && publication.Id == provider.PublicationId
                                && publication.PortfolioId == command.PortfolioId
                                && publication.Mode == ListingPublicationMode.Connected
                            select new { Listing = listing, Publication = publication })
            .SingleOrDefaultAsync(ct);

        var compatible = target?.Listing.ContentVersion == command.ExpectedContentVersion;
        if (compatible)
        {
            target!.Publication.Status = provider.ProviderStatus;
            target.Publication.LastDeliveryKey = provider.DeliveryKey;
            target.Publication.LastDeliveryStatus = provider.DeliveryStatus;
            target.Publication.LastDeliveryError = provider.DeliveryError;
            target.Publication.LastDeliveryAttemptAtUtc = now;
            target.Publication.ExternalListingId = provider.ExternalListingId ?? target.Publication.ExternalListingId;
            target.Publication.ListingUrl = provider.ListingUrl ?? target.Publication.ListingUrl;
            target.Publication.UpdatedAt = now;
            if (command.MarkPublishedVersion)
            {
                target.Publication.PublishedContentVersion = target.Listing.ContentVersion;
                target.Listing.Status = RentalListingStatus.Published;
            }
            else if (provider.ProviderStatus == ListingPublicationStatus.Removed)
            {
                var anotherPublished = await _db.Set<ListingPublication>().AsNoTracking().AnyAsync(item =>
                    item.RentalListingId == target.Listing.Id && item.PortfolioId == command.PortfolioId
                    && item.Id != target.Publication.Id && item.Status == ListingPublicationStatus.Published, ct);
                target.Listing.Status = anotherPublished
                    ? RentalListingStatus.Published
                    : RentalListingStatus.ReadyToPublish;
            }
            target.Listing.UpdatedAt = now;
            BindAudit(context, command, target.Publication, target.Listing, now, command.Reason);
            await context.FlushBusinessAsync(ct);
            StageUpdate(context, command, target.Listing.Id, now, "connected-result");
        }
        else if (target is not null)
        {
            target.Publication.Status = ListingPublicationStatus.ReconciliationRequired;
            target.Publication.UpdatedAt = now;
            context.UseDatabaseWallClockForAudit(now);
            context.BindSemanticAudit(target.Publication, new AtomicSemanticAudit(
                command.PortfolioId, nameof(ListingPublication), target.Publication.Id,
                AuditLogOperation.Updated, command.AdmittedActorUserId,
                ActorLabel: "system:listing-provider-reconciler",
                ChangeReason: "Provider outcome requires reconciliation with newer listing content"));
            await context.FlushBusinessAsync(ct);
            StageUpdate(context, command, target.Listing.Id, now, "connected-reconciliation-required");
        }

        return provider with
        {
            Outcome = compatible ? ListingWorkspaceMutationOutcome.Applied : ListingWorkspaceMutationOutcome.NoChange,
            AppliedToCurrentPublication = compatible,
            ReconciliationRequired = !compatible,
        };
    }

    public Task AuthorizeReplayAsync(ApplyConnectedListingResultCommand command, IAtomicCommandContext context, CancellationToken ct) =>
        ValidateProviderResultAsync(command, _db, ct);

    private static async Task ValidateProviderResultAsync(
        ApplyConnectedListingResultCommand command,
        RentalCommandDbContext db,
        CancellationToken ct)
    {
        var expectedResult = JsonSerializer.Serialize(command.ProviderResult);
        var recorded = await db.Set<AtomicCommandReceipt>().AsNoTracking().AnyAsync(receipt =>
            receipt.AttemptId == command.ProviderResultAttemptId
            && receipt.CommandType == command.ProviderResultCommandType
            && receipt.IdempotencyKey == command.ProviderResultIdempotencyKey
            && receipt.Status == AtomicCommandReceiptStatus.Completed
            && receipt.ResultContract == command.ProviderResultContract
            && receipt.ResultJson == expectedResult, ct);
        if (!recorded)
            throw new AtomicReceiptInvariantException(
                $"Connected listing apply has no durable provider result {command.ProviderResultAttemptId}.");
    }

    private static void BindAudit(
        IAtomicCommandContext context,
        ApplyConnectedListingResultCommand command,
        ListingPublication publication,
        RentalListing listing,
        DateTime now,
        string reason)
    {
        context.UseDatabaseWallClockForAudit(now);
        context.BindSemanticAudit(publication, new AtomicSemanticAudit(
            command.PortfolioId, nameof(ListingPublication), publication.Id,
            AuditLogOperation.Updated, command.AdmittedActorUserId,
            ActorLabel: "system:listing-provider-reconciler", ChangeReason: reason));
        context.BindSemanticAudit(listing, new AtomicSemanticAudit(
            command.PortfolioId, nameof(RentalListing), listing.Id,
            AuditLogOperation.Updated, command.AdmittedActorUserId,
            ActorLabel: "system:listing-provider-reconciler", ChangeReason: reason));
    }

    private static void StageUpdate(
        IAtomicCommandContext context,
        ApplyConnectedListingResultCommand command,
        int listingId,
        DateTime now,
        string operation)
    {
        context.StageOutbox(new OutboxMessage
        {
            PortfolioId = command.PortfolioId,
            MessageType = "data-update",
            Payload = JsonSerializer.Serialize(new
            {
                entityType = nameof(RentalListing),
                entityId = listingId,
                data = new { unitId = command.UnitId, operation },
            }),
            IdempotencyKey = $"listing-workspace:{operation}:{context.AttemptId:N}",
            CreatedAtUtc = now,
            NextAttemptAtUtc = now,
        });
    }
}

public sealed class ConfirmExternalListingSignalHandler
    : IAtomicCommandHandler<ConfirmExternalListingSignalCommand, ListingWorkspaceMutationResult>
{
    private readonly RentalCommandDbContext _db;

    public ConfirmExternalListingSignalHandler(RentalCommandDbContext db) => _db = db;

    public async Task<ListingWorkspaceMutationResult> HandleAsync(
        ConfirmExternalListingSignalCommand command, IAtomicCommandContext context, CancellationToken ct)
    {
        var now = await ListingWorkspaceCommandSupport.AuthorizeAndLockAsync(command, _db, context, ct);
        var target = await (from listing in ListingWorkspaceCommandSupport.AuthorizedListings(command, _db, now)
                            join publication in _db.Set<ListingPublication>()
                                on listing.Id equals publication.RentalListingId
                            join signal in _db.Set<ExternalListingSignal>()
                                on publication.Id equals signal.ListingPublicationId
                            where signal.Id == command.SignalId && signal.PortfolioId == command.PortfolioId
                            select new { Listing = listing, Publication = publication, Signal = signal })
            .SingleOrDefaultAsync(ct);
        if (target is null) return ListingWorkspaceCommandSupport.NotFound(command);
        if (target.Signal.Disposition != ExternalListingSignalDisposition.Unconfirmed)
            throw new DomainValidationException("This external listing signal was already reviewed.");

        target.Signal.Disposition = command.Accept
            ? ExternalListingSignalDisposition.Confirmed
            : ExternalListingSignalDisposition.Rejected;
        target.Signal.ConfirmedByUserId = command.ActorUserId;
        target.Signal.ConfirmedAtUtc = now;
        if (command.Accept)
        {
            target.Publication.ExternalListingId = target.Signal.SuggestedExternalListingId
                ?? target.Publication.ExternalListingId;
            target.Publication.ListingUrl = target.Signal.SuggestedListingUrl ?? target.Publication.ListingUrl;
            target.Publication.LastConfirmedExternalStatus = target.Signal.SuggestedExternalStatus
                ?? target.Publication.LastConfirmedExternalStatus;
            target.Publication.LastConfirmedAtUtc = now;
            target.Publication.UpdatedAt = now;
            context.UseDatabaseWallClockForAudit(now);
            context.BindSemanticAudit(target.Publication, ListingWorkspaceCommandSupport.Audit(
                command, nameof(ListingPublication), AuditLogOperation.Updated,
                "Applied confirmed external listing hint", target.Publication.Id));
        }
        await context.FlushBusinessAsync(ct);
        context.StageSemanticEvent(new AtomicSemanticAudit(
            command.PortfolioId, nameof(ExternalListingSignal), target.Signal.Id,
            AuditLogOperation.Updated, command.ActorUserId,
            ChangeReason: command.Accept ? "Confirmed external listing hint" : "Rejected external listing hint"), now);
        ListingWorkspaceCommandSupport.StageUpdate(context, command, target.Listing.Id, now, "signal-confirm");
        return ListingWorkspaceCommandSupport.Applied(command, target.Listing.Id);
    }

    public Task AuthorizeReplayAsync(ConfirmExternalListingSignalCommand command, IAtomicCommandContext context, CancellationToken ct) =>
        ListingWorkspaceCommandSupport.AuthorizeReplayAsync(command, _db, ct);
}

public sealed class IngestExternalListingSignalHandler
    : IAtomicCommandHandler<IngestExternalListingSignalCommand, IngestExternalListingSignalResult>
{
    private readonly RentalCommandDbContext _db;

    public IngestExternalListingSignalHandler(RentalCommandDbContext db) => _db = db;

    public async Task<IngestExternalListingSignalResult> HandleAsync(
        IngestExternalListingSignalCommand command, IAtomicCommandContext context, CancellationToken ct)
    {
        Validate(command);
        await context.AcquireLockAsync(
            "ExternalListingSignal",
            StableProviderMessageKey(command.PortfolioId, command.ProviderMessageKey),
            ct);
        var now = await ListingWorkspaceCommandSupport.AuthorizeAndLockAsync(command, _db, context, ct);
        var publicationExists = await (from listing in ListingWorkspaceCommandSupport.AuthorizedListings(
                                           command, _db, now)
                                       join publication in _db.Set<ListingPublication>()
                                           on listing.Id equals publication.RentalListingId
                                       where publication.Id == command.PublicationId
                                           && publication.PortfolioId == command.PortfolioId
                                       select publication.Id)
            .AnyAsync(ct);
        if (!publicationExists) return Missing(command);

        var signal = await _db.Set<ExternalListingSignal>()
            .SingleOrDefaultAsync(item =>
                item.PortfolioId == command.PortfolioId
                && item.ProviderMessageKey == command.ProviderMessageKey, ct);
        if (signal is not null)
        {
            if (signal.ListingPublicationId != command.PublicationId)
            {
                throw new DomainValidationException(
                    "Provider message key is already assigned to another listing publication.");
            }

            return Result(signal);
        }

        signal = new ExternalListingSignal
        {
            PortfolioId = command.PortfolioId,
            ListingPublicationId = command.PublicationId,
            ProviderMessageKey = command.ProviderMessageKey,
            SignalType = command.SignalType,
            SuggestedExternalListingId = command.SuggestedExternalListingId,
            SuggestedListingUrl = command.SuggestedListingUrl,
            SuggestedExternalStatus = command.SuggestedExternalStatus,
            Disposition = ExternalListingSignalDisposition.Unconfirmed,
            ReceivedAtUtc = now,
        };
        _db.Add(signal);
        await context.FlushBusinessAsync(ct);
        context.StageSemanticEvent(new AtomicSemanticAudit(
            command.PortfolioId,
            nameof(ExternalListingSignal),
            signal.Id,
            AuditLogOperation.Created,
            command.ActorUserId,
            NewValues: JsonSerializer.Serialize(new
            {
                command.PublicationId,
                command.ProviderMessageKey,
                command.SignalType,
                command.SuggestedExternalListingId,
                command.SuggestedListingUrl,
                command.SuggestedExternalStatus,
            }),
            ChangeReason: "Ingested external listing signal"), now);
        return Result(signal);
    }

    public Task AuthorizeReplayAsync(
        IngestExternalListingSignalCommand command, IAtomicCommandContext context, CancellationToken ct) =>
        ListingWorkspaceCommandSupport.AuthorizeReplayAsync(command, _db, ct);

    private static void Validate(IngestExternalListingSignalCommand command)
    {
        if (command.PublicationId <= 0)
            throw new DomainValidationException("Listing publication is required.");
        if (string.IsNullOrWhiteSpace(command.ProviderMessageKey))
            throw new DomainValidationException("Provider message key is required.");
        if (string.IsNullOrWhiteSpace(command.SignalType))
            throw new DomainValidationException("Signal type is required.");
    }

    private static IngestExternalListingSignalResult Missing(
        IngestExternalListingSignalCommand command) =>
        new(false, 0, command.SignalType, command.SuggestedExternalListingId,
            command.SuggestedListingUrl, command.SuggestedExternalStatus,
            ExternalListingSignalDisposition.Unconfirmed, default);

    private static IngestExternalListingSignalResult Result(ExternalListingSignal signal) =>
        new(true, signal.Id, signal.SignalType, signal.SuggestedExternalListingId,
            signal.SuggestedListingUrl, signal.SuggestedExternalStatus,
            signal.Disposition, signal.ReceivedAtUtc);

    private static Guid StableProviderMessageKey(int portfolioId, string providerMessageKey)
    {
        var bytes = SHA256.HashData(
            Encoding.UTF8.GetBytes($"{portfolioId}:{providerMessageKey}"));
        return new Guid(bytes.AsSpan(0, 16));
    }
}

internal static class ListingWorkspaceCommandSupport
{
    internal static async Task<DateTime> AuthorizeAndLockAsync(
        IListingWorkspaceAtomicCommand command, RentalCommandDbContext db,
        IAtomicCommandContext context, CancellationToken ct)
    {
        Validate(command);
        await context.AcquireLockAsync("AuthSession", command.AuthSessionId, ct);
        await context.AcquireLockAsync("WorkspaceAccessContext", command.AccessContextId, ct);
        await context.AcquireLockAsync("Unit", command.UnitId, ct);
        var now = await context.ReadDatabaseClockUtcAsync(ct);
        if (!await AuthorizedUnits(command, db, now).AnyAsync(ct))
            throw new UnauthorizedAccessException("The listing is outside the caller's current property scope.");
        return now;
    }

    internal static async Task AuthorizeReplayAsync(
        IListingWorkspaceAtomicCommand command, RentalCommandDbContext db, CancellationToken ct)
    {
        Validate(command);
        var now = await db.Database.SqlQuery<DateTime>($"SELECT clock_timestamp() AS \"Value\"").SingleAsync(ct);
        if (!await AuthorizedUnits(command, db, now).AnyAsync(ct))
            throw new UnauthorizedAccessException("The listing is outside the caller's current property scope.");
    }

    internal static IQueryable<Unit> AuthorizedUnits(
        IListingWorkspaceAtomicCommand command, RentalCommandDbContext db, DateTime now)
    {
        var assignments = db.Set<MembershipRoleAssignment>().Where(assignment =>
            assignment.PortfolioId == command.PortfolioId
            && assignment.Status == MembershipRoleAssignmentStatus.Active
            && assignment.SuspendedAtUtc == null && assignment.RevokedAtUtc == null
            && assignment.EffectiveFromUtc <= now
            && (assignment.EffectiveToUtc == null || assignment.EffectiveToUtc > now));

        return db.Set<Unit>().Where(unit =>
            unit.Id == command.UnitId && unit.PortfolioId == command.PortfolioId
            && unit.Property != null && unit.Property.PortfolioId == command.PortfolioId
            && db.Set<AuthSession>().Any(session =>
                session.Id == command.AuthSessionId && session.UserId == command.ActorUserId
                && session.ActiveAccessContextId == command.AccessContextId
                && session.Status == AuthSessionStatus.Active && session.RevokedAtUtc == null
                && session.ExpiresAtUtc > now)
            && db.Set<WorkspaceAccessContext>().Any(context =>
                context.Id == command.AccessContextId && context.UserId == command.ActorUserId
                && context.PortfolioId == command.PortfolioId
                && context.AccessRevision == command.AccessRevision
                && context.Status == WorkspaceAccessContextStatus.Active
                && context.SuspendedAtUtc == null && context.RevokedAtUtc == null)
            && db.Set<WorkspaceMembership>().Any(membership =>
                membership.AccessContextId == command.AccessContextId
                && membership.PortfolioId == command.PortfolioId
                && membership.Status == WorkspaceMembershipStatus.Active
                && membership.SuspendedAtUtc == null && membership.RevokedAtUtc == null
                && membership.EffectiveFromUtc <= now
                && (membership.EffectiveToUtc == null || membership.EffectiveToUtc > now)
                && assignments.Any(assignment =>
                    assignment.WorkspaceMembershipId == membership.Id
                    && (assignment.ScopeKind == MembershipRoleAssignmentScopeKind.AllProperties
                        || (assignment.ScopeKind == MembershipRoleAssignmentScopeKind.SelectedProperties
                            && assignment.SelectedProperties.Any(scope =>
                                scope.PortfolioId == command.PortfolioId
                                && scope.PropertyId == unit.PropertyId)))
                    && assignment.RoleProfile!.Capabilities.Any(capability =>
                        capability.CapabilityDefinition!.Key == CapabilityKeys.LeasingListingsManage
                        && capability.CapabilityDefinition.AuthorizationTargetKind ==
                            CapabilityAuthorizationTargetKind.Property))));
    }

    internal static IQueryable<RentalListing> AuthorizedListings(
        IListingWorkspaceAtomicCommand command, RentalCommandDbContext db, DateTime now) =>
        from unit in AuthorizedUnits(command, db, now)
        join listing in db.Set<RentalListing>() on unit.Id equals listing.UnitId
        where listing.PortfolioId == command.PortfolioId
        select listing;

    internal static async Task<ListingPhotoTarget?> LoadPhotoTargetAsync(
        IListingWorkspaceAtomicCommand command, RentalCommandDbContext db, DateTime now, CancellationToken ct)
    {
        var photoId = command switch
        {
            UpdateListingPhotoCommand update => update.PhotoId,
            RemoveListingPhotoCommand remove => remove.PhotoId,
            _ => throw new ArgumentOutOfRangeException(nameof(command)),
        };
        return await (from listing in AuthorizedListings(command, db, now)
                      join photo in db.Set<ListingPhoto>() on listing.Id equals photo.RentalListingId
                      where photo.Id == photoId && photo.PortfolioId == command.PortfolioId
                      select new ListingPhotoTarget(listing, photo)).SingleOrDefaultAsync(ct);
    }

    internal static void AuditPhotoMutation(
        IListingWorkspaceAtomicCommand command, IAtomicCommandContext context,
        ListingPhotoTarget target, DateTime now, string reason)
    {
        context.UseDatabaseWallClockForAudit(now);
        context.BindSemanticAudit(target.Listing, Audit(
            command, nameof(RentalListing), AuditLogOperation.Updated, reason));
        context.StageSemanticEvent(new AtomicSemanticAudit(
            command.PortfolioId, nameof(ListingPhoto), target.Photo.Id,
            AuditLogOperation.Updated, command.ActorUserId, ChangeReason: reason), now);
    }

    internal static AtomicSemanticAudit Audit(
        IListingWorkspaceAtomicCommand command, string entityType,
        AuditLogOperation operation, string reason, int entityId = 0) =>
        new(command.PortfolioId, entityType, entityId, operation,
            UserId: command.ActorUserId, ChangeReason: reason);

    internal static void StageUpdate(
        IAtomicCommandContext context, IListingWorkspaceAtomicCommand command,
        int listingId, DateTime now, string operation)
    {
        context.StageOutbox(new OutboxMessage
        {
            PortfolioId = command.PortfolioId,
            MessageType = "data-update",
            Payload = JsonSerializer.Serialize(new
            {
                entityType = nameof(RentalListing),
                entityId = listingId,
                data = new { unitId = command.UnitId, operation },
            }),
            IdempotencyKey = $"listing-workspace:{operation}:{context.AttemptId:N}",
            CreatedAtUtc = now,
            NextAttemptAtUtc = now,
        });
    }

    internal static ListingWorkspaceMutationResult Applied(IListingWorkspaceAtomicCommand command, int listingId) =>
        new(ListingWorkspaceMutationOutcome.Applied, command.PortfolioId, command.UnitId, listingId);
    internal static ListingWorkspaceMutationResult NoChange(IListingWorkspaceAtomicCommand command, int listingId) =>
        new(ListingWorkspaceMutationOutcome.NoChange, command.PortfolioId, command.UnitId, listingId);
    internal static ListingWorkspaceMutationResult NotFound(IListingWorkspaceAtomicCommand command) =>
        new(ListingWorkspaceMutationOutcome.NotFound, command.PortfolioId, command.UnitId, null);

    internal static RentalListing CreateListing(ListingSeed seed, DateTime now)
    {
        var listing = new RentalListing
        {
            PortfolioId = seed.PortfolioId,
            PropertyId = seed.PropertyId,
            UnitId = seed.UnitId,
            Rent = seed.MarketRent,
            SecurityDeposit = seed.MarketRent > 0 ? seed.MarketRent : null,
            Bedrooms = seed.Bedrooms,
            Bathrooms = seed.Bathrooms,
            SquareFeet = seed.SquareFeet,
            Headline = $"{Rooms(seed.Bedrooms, "bed")} / {Rooms(seed.Bathrooms, "bath")} at {seed.PropertyName} - Unit {seed.UnitNumber}",
            Description = BuildDescription(seed),
            LeaseTerms = "12-month lease",
            CreatedAt = now,
            UpdatedAt = now,
            Photos = DefaultPhotos(seed.PortfolioId, now),
            Publications =
            [
                new ListingPublication
                {
                    PortfolioId = seed.PortfolioId,
                    ProviderKey = ListingProviderKeys.Zillow,
                    Mode = ListingPublicationMode.Guided,
                    Status = ListingPublicationStatus.Draft,
                    ManagementUrl = "https://www.zillow.com/rental-manager/properties",
                    CreatedAt = now,
                    UpdatedAt = now,
                },
                new ListingPublication
                {
                    PortfolioId = seed.PortfolioId,
                    ProviderKey = ListingProviderKeys.Zillow,
                    Mode = ListingPublicationMode.Connected,
                    Status = ListingPublicationStatus.Draft,
                    CreatedAt = now,
                    UpdatedAt = now,
                },
            ],
        };
        return listing;
    }

    internal static bool Apply(SaveListingWorkspaceCommand command, RentalListing listing)
    {
        var changed = false;
        if (!string.IsNullOrWhiteSpace(command.Status))
            listing.Status = Parse<RentalListingStatus>(command.Status, "Listing status");
        changed |= SetRequired(command.Headline, listing.Headline, value => listing.Headline = value, "Listing headline");
        changed |= SetRequired(command.Description, listing.Description, value => listing.Description = value, "Listing description");
        changed |= Set(command.Rent, listing.Rent, value => listing.Rent = value);
        changed |= Set(command.SecurityDeposit, listing.SecurityDeposit, value => listing.SecurityDeposit = value);
        if (command.AvailableOn.HasValue)
            changed |= Set(command.AvailableOn.Value.ToUniversalTime(), listing.AvailableOn, value => listing.AvailableOn = value);
        changed |= SetOptional(command.LeaseTerms, listing.LeaseTerms, value => listing.LeaseTerms = value);
        changed |= SetOptional(command.PetPolicy, listing.PetPolicy, value => listing.PetPolicy = value);
        changed |= SetOptional(command.Utilities, listing.Utilities, value => listing.Utilities = value);
        changed |= SetOptional(command.Parking, listing.Parking, value => listing.Parking = value);
        changed |= SetOptional(command.Amenities, listing.Amenities, value => listing.Amenities = value);
        return changed;
    }

    internal static void Apply(
        GuidedListingValues? values, RentalListing listing, ListingPublication? publication, DateTime now)
    {
        if (values is null) return;
        if (publication is null) throw new DomainValidationException("Zillow Guided publication is missing.");
        if (!string.IsNullOrWhiteSpace(values.Status))
            publication.Status = Parse<ListingPublicationStatus>(values.Status, "Publication status");
        if (values.ExternalListingId is not null) publication.ExternalListingId = Clean(values.ExternalListingId);
        if (values.ListingUrl is not null) publication.ListingUrl = Clean(values.ListingUrl);
        if (values.ApplicationUrl is not null) publication.ApplicationUrl = Clean(values.ApplicationUrl);
        if (values.ManagementUrl is not null) publication.ManagementUrl = Clean(values.ManagementUrl);
        if (values.LastConfirmedExternalStatus is not null)
            publication.LastConfirmedExternalStatus = Clean(values.LastConfirmedExternalStatus);
        if (values.LastConfirmedAtUtc.HasValue)
            publication.LastConfirmedAtUtc = values.LastConfirmedAtUtc.Value.ToUniversalTime();
        if (values.CopyConfirmed.HasValue) publication.CopyConfirmed = values.CopyConfirmed.Value;
        if (values.TermsConfirmed.HasValue) publication.TermsConfirmed = values.TermsConfirmed.Value;
        if (values.PhotosConfirmed.HasValue) publication.PhotosConfirmed = values.PhotosConfirmed.Value;
        if (values.ProviderWorkspaceOpened.HasValue) publication.ProviderWorkspaceOpened = values.ProviderWorkspaceOpened.Value;
        if (values.MarkCurrentVersionPublished == true)
        {
            publication.PublishedContentVersion = listing.ContentVersion;
            publication.Status = ListingPublicationStatus.Published;
            listing.Status = RentalListingStatus.Published;
        }
        publication.UpdatedAt = now;
    }

    private static void Validate(IListingWorkspaceAtomicCommand command)
    {
        if (command.PortfolioId <= 0 || command.UnitId <= 0 || command.ActorUserId <= 0
            || command.AuthSessionId == Guid.Empty || command.AccessContextId <= 0
            || command.AccessRevision <= 0)
            throw new UnauthorizedAccessException();
    }

    private static List<ListingPhoto> DefaultPhotos(int portfolioId, DateTime now) =>
        new[] { "Exterior", "Living area", "Kitchen", "Primary bedroom", "Bathroom", "Utility and storage" }
            .Select((category, index) => new ListingPhoto
            {
                PortfolioId = portfolioId,
                Position = index + 1,
                Category = category,
                Caption = $"Add the best {category.ToLowerInvariant()} photo",
                CreatedAt = now,
            }).ToList();

    private static string BuildDescription(ListingSeed seed)
    {
        var size = seed.SquareFeet is > 0 ? $" Approximate size: {seed.SquareFeet.Value:N0} sq ft." : string.Empty;
        return $"Now available at {seed.PropertyName}, Unit {seed.UnitNumber}. This rental offers "
            + $"{Rooms(seed.Bedrooms, "bedroom")}, {Rooms(seed.Bathrooms, "bathroom")}, and listed rent of "
            + $"{seed.MarketRent.ToString("C0", CultureInfo.GetCultureInfo("en-US"))} per month.{size} "
            + "Review all details before publishing.";
    }

    private static string Rooms(decimal value, string name) =>
        $"{value.ToString(value % 1m == 0 ? "0" : "0.##", CultureInfo.InvariantCulture)} {name}";
    private static T Parse<T>(string value, string label) where T : struct, Enum =>
        Enum.TryParse<T>(value, true, out var result)
            ? result
            : throw new DomainValidationException($"{label} is invalid.");
    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    private static bool SetRequired(string? value, string current, Action<string> setter, string label)
    {
        if (value is null) return false;
        var clean = string.IsNullOrWhiteSpace(value)
            ? throw new DomainValidationException($"{label} is required.")
            : value.Trim();
        if (clean == current) return false;
        setter(clean);
        return true;
    }
    private static bool SetOptional(string? value, string? current, Action<string?> setter)
    {
        if (value is null) return false;
        var clean = Clean(value);
        if (clean == current) return false;
        setter(clean);
        return true;
    }
    private static bool Set<T>(T? value, T? current, Action<T> setter) where T : struct
    {
        if (!value.HasValue || (current.HasValue && EqualityComparer<T>.Default.Equals(value.Value, current.Value)))
            return false;
        setter(value.Value);
        return true;
    }
}

internal sealed record ListingSeed(
    int PortfolioId, int PropertyId, int UnitId, string PropertyName,
    string AddressLine1, string? AddressLine2, string City, string State,
    string PostalCode, string UnitNumber, decimal Bedrooms, decimal Bathrooms,
    int? SquareFeet, decimal MarketRent);

internal sealed record ListingPhotoTarget(RentalListing Listing, ListingPhoto Photo);
