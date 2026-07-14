using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using RentalCommand.Core;
using RentalCommand.Core.Atomic;
using RentalCommand.Core.Authorization;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Listings;

namespace RentalCommand.Data.Listings;

public sealed class GenerateListingWorkspaceHandler
    : IAtomicCommandHandler<GenerateListingWorkspaceCommand, ListingWorkspaceMutationResult>,
      IAtomicReplayAuthorizer<GenerateListingWorkspaceCommand>
{
    public async Task<ListingWorkspaceMutationResult> HandleAsync(
        GenerateListingWorkspaceCommand command, IAtomicWriteAttempt attempt, CancellationToken ct)
    {
        var now = await ListingWorkspaceCommandSupport.AuthorizeAndLockAsync(command, attempt, ct);
        var seed = await ListingWorkspaceCommandSupport.AuthorizedUnits(command, attempt.Persistence, now)
            .Select(unit => new ListingSeed(
                unit.PortfolioId, unit.PropertyId, unit.Id, unit.Property!.Name,
                unit.Property.AddressLine1, unit.Property.AddressLine2, unit.Property.City,
                unit.Property.State, unit.Property.PostalCode, unit.UnitNumber,
                unit.Bedrooms, unit.Bathrooms, unit.SquareFeet, unit.MarketRent))
            .SingleOrDefaultAsync(ct);
        if (seed is null) return ListingWorkspaceCommandSupport.NotFound(command);

        var listing = await attempt.Persistence.Query<RentalListing>()
            .SingleOrDefaultAsync(item => item.PortfolioId == command.PortfolioId && item.UnitId == command.UnitId, ct);
        var created = listing is null;
        if (listing is null)
        {
            listing = ListingWorkspaceCommandSupport.CreateListing(seed, now);
            attempt.Persistence.Add(listing);
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

        attempt.UseDatabaseWallClockForAudit(now);
        attempt.BindSemanticAudit(listing, ListingWorkspaceCommandSupport.Audit(
            command, nameof(RentalListing), created ? AuditLogOperation.Created : AuditLogOperation.Updated,
            created
                ? "Prepared provider-neutral rental listing workspace"
                : "Synchronized non-editable unit details without replacing customized listing content"));
        await attempt.FlushBusinessAsync(ct);
        ListingWorkspaceCommandSupport.StageUpdate(attempt, command, listing.Id, now, "generate");
        return ListingWorkspaceCommandSupport.Applied(command, listing.Id);
    }

    public Task AuthorizeReplayAsync(GenerateListingWorkspaceCommand command,
        IAtomicPersistenceSession persistence, CancellationToken ct) =>
        ListingWorkspaceCommandSupport.AuthorizeReplayAsync(command, persistence, ct);
}

public sealed class SaveListingWorkspaceHandler
    : IAtomicCommandHandler<SaveListingWorkspaceCommand, ListingWorkspaceMutationResult>,
      IAtomicReplayAuthorizer<SaveListingWorkspaceCommand>
{
    public async Task<ListingWorkspaceMutationResult> HandleAsync(
        SaveListingWorkspaceCommand command, IAtomicWriteAttempt attempt, CancellationToken ct)
    {
        var now = await ListingWorkspaceCommandSupport.AuthorizeAndLockAsync(command, attempt, ct);
        var listing = await ListingWorkspaceCommandSupport.AuthorizedListings(command, attempt.Persistence, now)
            .SingleOrDefaultAsync(ct);
        if (listing is null) return ListingWorkspaceCommandSupport.NotFound(command);

        var contentChanged = ListingWorkspaceCommandSupport.Apply(command, listing);
        if (contentChanged) listing.ContentVersion++;
        listing.UpdatedAt = now;
        var guided = command.ZillowGuided is null
            ? null
            : await attempt.Persistence.Query<ListingPublication>().SingleOrDefaultAsync(publication =>
                publication.RentalListingId == listing.Id
                && publication.PortfolioId == command.PortfolioId
                && publication.ProviderKey == ListingProviderKeys.Zillow
                && publication.Mode == ListingPublicationMode.Guided, ct);
        ListingWorkspaceCommandSupport.Apply(command.ZillowGuided, listing, guided, now);

        attempt.UseDatabaseWallClockForAudit(now);
        attempt.BindSemanticAudit(listing, ListingWorkspaceCommandSupport.Audit(
            command, nameof(RentalListing), AuditLogOperation.Updated,
            "Updated rental listing and Zillow Guided workspace"));
        if (guided is not null)
        {
            attempt.BindSemanticAudit(guided, ListingWorkspaceCommandSupport.Audit(
                command, nameof(ListingPublication), AuditLogOperation.Updated,
                "Updated Zillow Guided workspace", guided.Id));
        }
        await attempt.FlushBusinessAsync(ct);
        ListingWorkspaceCommandSupport.StageUpdate(attempt, command, listing.Id, now, "save");
        return ListingWorkspaceCommandSupport.Applied(command, listing.Id);
    }

    public Task AuthorizeReplayAsync(SaveListingWorkspaceCommand command,
        IAtomicPersistenceSession persistence, CancellationToken ct) =>
        ListingWorkspaceCommandSupport.AuthorizeReplayAsync(command, persistence, ct);
}

public sealed class FinalizeListingPhotoUploadHandler
    : IAtomicCommandHandler<FinalizeListingPhotoUploadCommand, ListingWorkspaceMutationResult>,
      IAtomicReplayAuthorizer<FinalizeListingPhotoUploadCommand>
{
    public async Task<ListingWorkspaceMutationResult> HandleAsync(
        FinalizeListingPhotoUploadCommand command, IAtomicWriteAttempt attempt, CancellationToken ct)
    {
        var now = await ListingWorkspaceCommandSupport.AuthorizeAndLockAsync(command, attempt, ct);
        var photo = await (from listing in ListingWorkspaceCommandSupport.AuthorizedListings(command, attempt.Persistence, now)
                           join item in attempt.Persistence.Query<ListingPhoto>() on listing.Id equals item.RentalListingId
                           where item.Id == command.PhotoId && item.PortfolioId == command.PortfolioId
                           select item).SingleOrDefaultAsync(ct);
        if (photo is null) return ListingWorkspaceCommandSupport.NotFound(command);
        var listing = await attempt.Persistence.Query<RentalListing>()
            .SingleAsync(item => item.Id == photo.RentalListingId && item.PortfolioId == command.PortfolioId, ct);

        var pending = (await attempt.PendingFileUploads.LockPreparedSetAsync(
            command.PortfolioId,
            command.ActorUserId,
            [new AtomicPendingFileUploadExpectation(
                command.PendingUploadId, command.Purpose, command.OperationKeyHash,
                command.RequestFingerprint, command.StoragePath, command.FileName,
                command.ContentType, command.SizeBytes)],
            ct)).Single();

        if (photo.StoredFileId is int oldFileId)
        {
            var old = await attempt.Persistence.Query<StoredFile>()
                .SingleAsync(file => file.Id == oldFileId && file.PortfolioId == command.PortfolioId, ct);
            old.DeletedAt = now;
            attempt.StageOutbox(new OutboxMessage
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
        attempt.Persistence.Add(stored);
        await attempt.FlushBusinessAsync(ct);

        photo.StoredFileId = stored.Id;
        photo.FileName = command.FileName;
        photo.Sha256 = command.Sha256;
        pending.State = PendingFileUploadState.Finalized;
        pending.StoredFileId = stored.Id;
        pending.UpdatedAtUtc = now;
        listing.ContentVersion++;
        listing.UpdatedAt = now;
        attempt.UseDatabaseWallClockForAudit(now);
        attempt.BindSemanticAudit(listing, ListingWorkspaceCommandSupport.Audit(
            command, nameof(RentalListing), AuditLogOperation.Updated, "Attached listing photo"));
        await attempt.FlushBusinessAsync(ct);
        attempt.StageSemanticEvent(new AtomicSemanticAudit(
            command.PortfolioId, nameof(ListingPhoto), photo.Id, AuditLogOperation.Updated,
            command.ActorUserId,
            NewValues: JsonSerializer.Serialize(new
            {
                photo.Position, photo.Category, photo.Caption, command.FileName, command.Sha256,
            }),
            ChangeReason: "Attached listing photo"), now);
        ListingWorkspaceCommandSupport.StageUpdate(attempt, command, listing.Id, now, "photo-attach");
        return ListingWorkspaceCommandSupport.Applied(command, listing.Id);
    }

    public Task AuthorizeReplayAsync(FinalizeListingPhotoUploadCommand command,
        IAtomicPersistenceSession persistence, CancellationToken ct) =>
        ListingWorkspaceCommandSupport.AuthorizeReplayAsync(command, persistence, ct);
}

public sealed class UpdateListingPhotoHandler
    : IAtomicCommandHandler<UpdateListingPhotoCommand, ListingWorkspaceMutationResult>,
      IAtomicReplayAuthorizer<UpdateListingPhotoCommand>
{
    public async Task<ListingWorkspaceMutationResult> HandleAsync(
        UpdateListingPhotoCommand command, IAtomicWriteAttempt attempt, CancellationToken ct)
    {
        var now = await ListingWorkspaceCommandSupport.AuthorizeAndLockAsync(command, attempt, ct);
        var target = await ListingWorkspaceCommandSupport.LoadPhotoTargetAsync(command, attempt.Persistence, now, ct);
        if (target is null) return ListingWorkspaceCommandSupport.NotFound(command);
        if (target.Photo.Category == command.Category && target.Photo.Caption == command.Caption)
            return ListingWorkspaceCommandSupport.NoChange(command, target.Listing.Id);

        target.Photo.Category = command.Category;
        target.Photo.Caption = command.Caption;
        target.Listing.ContentVersion++;
        target.Listing.UpdatedAt = now;
        ListingWorkspaceCommandSupport.AuditPhotoMutation(command, attempt, target, now, "Updated listing photo details");
        await attempt.FlushBusinessAsync(ct);
        ListingWorkspaceCommandSupport.StageUpdate(attempt, command, target.Listing.Id, now, "photo-update");
        return ListingWorkspaceCommandSupport.Applied(command, target.Listing.Id);
    }

    public Task AuthorizeReplayAsync(UpdateListingPhotoCommand command,
        IAtomicPersistenceSession persistence, CancellationToken ct) =>
        ListingWorkspaceCommandSupport.AuthorizeReplayAsync(command, persistence, ct);
}

public sealed class RemoveListingPhotoHandler
    : IAtomicCommandHandler<RemoveListingPhotoCommand, ListingWorkspaceMutationResult>,
      IAtomicReplayAuthorizer<RemoveListingPhotoCommand>
{
    public async Task<ListingWorkspaceMutationResult> HandleAsync(
        RemoveListingPhotoCommand command, IAtomicWriteAttempt attempt, CancellationToken ct)
    {
        var now = await ListingWorkspaceCommandSupport.AuthorizeAndLockAsync(command, attempt, ct);
        var target = await ListingWorkspaceCommandSupport.LoadPhotoTargetAsync(command, attempt.Persistence, now, ct);
        if (target is null) return ListingWorkspaceCommandSupport.NotFound(command);
        if (target.Photo.StoredFileId is not int fileId)
            return ListingWorkspaceCommandSupport.NoChange(command, target.Listing.Id);

        var stored = await attempt.Persistence.Query<StoredFile>()
            .SingleAsync(file => file.Id == fileId && file.PortfolioId == command.PortfolioId, ct);
        stored.DeletedAt = now;
        target.Photo.StoredFileId = null;
        target.Photo.FileName = null;
        target.Photo.Sha256 = null;
        target.Listing.ContentVersion++;
        target.Listing.UpdatedAt = now;
        ListingWorkspaceCommandSupport.AuditPhotoMutation(command, attempt, target, now, "Removed listing photo attachment");
        await attempt.FlushBusinessAsync(ct);
        attempt.StageOutbox(new OutboxMessage
        {
            PortfolioId = command.PortfolioId,
            MessageType = "blob-delete",
            Payload = JsonSerializer.Serialize(new { storedFileId = stored.Id, storagePath = stored.FilePath }),
            IdempotencyKey = $"listing-photo-remove:{stored.Id}",
            CreatedAtUtc = now,
            NextAttemptAtUtc = now,
        });
        ListingWorkspaceCommandSupport.StageUpdate(attempt, command, target.Listing.Id, now, "photo-remove");
        return ListingWorkspaceCommandSupport.Applied(command, target.Listing.Id);
    }

    public Task AuthorizeReplayAsync(RemoveListingPhotoCommand command,
        IAtomicPersistenceSession persistence, CancellationToken ct) =>
        ListingWorkspaceCommandSupport.AuthorizeReplayAsync(command, persistence, ct);
}

public sealed class ReorderListingPhotosHandler
    : IAtomicCommandHandler<ReorderListingPhotosCommand, ListingWorkspaceMutationResult>,
      IAtomicReplayAuthorizer<ReorderListingPhotosCommand>
{
    public async Task<ListingWorkspaceMutationResult> HandleAsync(
        ReorderListingPhotosCommand command, IAtomicWriteAttempt attempt, CancellationToken ct)
    {
        var now = await ListingWorkspaceCommandSupport.AuthorizeAndLockAsync(command, attempt, ct);
        var listing = await ListingWorkspaceCommandSupport.AuthorizedListings(command, attempt.Persistence, now)
            .SingleOrDefaultAsync(ct);
        if (listing is null) return ListingWorkspaceCommandSupport.NotFound(command);
        var order = await attempt.Listings.ReorderPhotosAsync(
            command.PortfolioId, listing.Id, command.PhotoIds, ct);
        if (!order.IsValid)
            throw new DomainValidationException("Photo order must include every photo exactly once.");
        if (!order.HasChanges) return ListingWorkspaceCommandSupport.NoChange(command, listing.Id);

        listing.ContentVersion++;
        listing.UpdatedAt = now;
        attempt.UseDatabaseWallClockForAudit(now);
        attempt.BindSemanticAudit(listing, ListingWorkspaceCommandSupport.Audit(
            command, nameof(RentalListing), AuditLogOperation.Updated, "Reordered listing photo package"));
        await attempt.FlushBusinessAsync(ct);
        attempt.StageSemanticEvent(new AtomicSemanticAudit(
            command.PortfolioId, nameof(ListingPhoto), listing.Id, AuditLogOperation.Updated,
            command.ActorUserId,
            NewValues: JsonSerializer.Serialize(new { command.PhotoIds }),
            ChangeReason: "Reordered listing photo package"), now);
        ListingWorkspaceCommandSupport.StageUpdate(attempt, command, listing.Id, now, "photo-reorder");
        return ListingWorkspaceCommandSupport.Applied(command, listing.Id);
    }

    public Task AuthorizeReplayAsync(ReorderListingPhotosCommand command,
        IAtomicPersistenceSession persistence, CancellationToken ct) =>
        ListingWorkspaceCommandSupport.AuthorizeReplayAsync(command, persistence, ct);
}

public sealed class AdmitConnectedListingIntentHandler
    : IAtomicCommandHandler<AdmitConnectedListingIntentCommand, ConnectedListingIntentResult>,
      IAtomicReplayAuthorizer<AdmitConnectedListingIntentCommand>
{
    public async Task<ConnectedListingIntentResult> HandleAsync(
        AdmitConnectedListingIntentCommand command, IAtomicWriteAttempt attempt, CancellationToken ct)
    {
        var now = await ListingWorkspaceCommandSupport.AuthorizeAndLockAsync(command, attempt, ct);
        var target = await (from listing in ListingWorkspaceCommandSupport.AuthorizedListings(command, attempt.Persistence, now)
                            join publication in attempt.Persistence.Query<ListingPublication>()
                                on listing.Id equals publication.RentalListingId
                            where listing.Id == command.RentalListingId
                                && listing.ContentVersion == command.ExpectedContentVersion
                                && publication.Id == command.PublicationId
                                && publication.PortfolioId == command.PortfolioId
                                && publication.Mode == ListingPublicationMode.Connected
                            select new { listing.Id, listing.ContentVersion, PublicationId = publication.Id })
            .SingleOrDefaultAsync(ct);
        return target is null
            ? new ConnectedListingIntentResult(ListingWorkspaceMutationOutcome.NotFound,
                command.PortfolioId, command.UnitId, null, null, null)
            : new ConnectedListingIntentResult(ListingWorkspaceMutationOutcome.Applied,
                command.PortfolioId, command.UnitId, target.Id, target.PublicationId, target.ContentVersion);
    }

    public Task AuthorizeReplayAsync(AdmitConnectedListingIntentCommand command,
        IAtomicPersistenceSession persistence, CancellationToken ct) =>
        ListingWorkspaceCommandSupport.AuthorizeReplayAsync(command, persistence, ct);
}

public sealed class PersistConnectedListingResultHandler
    : IAtomicCommandHandler<PersistConnectedListingResultCommand, ListingWorkspaceMutationResult>,
      IAtomicReplayAuthorizer<PersistConnectedListingResultCommand>
{
    public async Task<ListingWorkspaceMutationResult> HandleAsync(
        PersistConnectedListingResultCommand command, IAtomicWriteAttempt attempt, CancellationToken ct)
    {
        var now = await ListingWorkspaceCommandSupport.AuthorizeAndLockAsync(command, attempt, ct);
        var target = await (from listing in ListingWorkspaceCommandSupport.AuthorizedListings(command, attempt.Persistence, now)
                            join publication in attempt.Persistence.Query<ListingPublication>()
                                on listing.Id equals publication.RentalListingId
                            where listing.Id == command.RentalListingId
                                && publication.Id == command.PublicationId
                                && publication.PortfolioId == command.PortfolioId
                                && publication.Mode == ListingPublicationMode.Connected
                            select new { Listing = listing, Publication = publication })
            .SingleOrDefaultAsync(ct);
        if (target is null) return ListingWorkspaceCommandSupport.NotFound(command);
        if (target.Listing.ContentVersion != command.ExpectedContentVersion)
            throw new DomainValidationException("The listing changed while the provider request was running. Prepare it again.");

        target.Publication.Status = command.Status;
        target.Publication.LastDeliveryKey = command.DeliveryKey;
        target.Publication.LastDeliveryStatus = command.DeliveryStatus;
        target.Publication.LastDeliveryError = command.DeliveryError;
        target.Publication.LastDeliveryAttemptAtUtc = now;
        target.Publication.ExternalListingId = command.ExternalListingId ?? target.Publication.ExternalListingId;
        target.Publication.ListingUrl = command.ListingUrl ?? target.Publication.ListingUrl;
        target.Publication.UpdatedAt = now;
        if (command.MarkPublishedVersion)
        {
            target.Publication.PublishedContentVersion = target.Listing.ContentVersion;
            target.Listing.Status = RentalListingStatus.Published;
        }
        else if (command.Status == ListingPublicationStatus.Removed)
        {
            var anotherPublished = await attempt.Persistence.Query<ListingPublication>().AsNoTracking().AnyAsync(item =>
                item.RentalListingId == target.Listing.Id && item.PortfolioId == command.PortfolioId
                && item.Id != target.Publication.Id && item.Status == ListingPublicationStatus.Published, ct);
            target.Listing.Status = anotherPublished
                ? RentalListingStatus.Published
                : RentalListingStatus.ReadyToPublish;
        }
        target.Listing.UpdatedAt = now;

        attempt.UseDatabaseWallClockForAudit(now);
        attempt.BindSemanticAudit(target.Publication, ListingWorkspaceCommandSupport.Audit(
            command, nameof(ListingPublication), AuditLogOperation.Updated, command.Reason, target.Publication.Id));
        attempt.BindSemanticAudit(target.Listing, ListingWorkspaceCommandSupport.Audit(
            command, nameof(RentalListing), AuditLogOperation.Updated, command.Reason, target.Listing.Id));
        await attempt.FlushBusinessAsync(ct);
        ListingWorkspaceCommandSupport.StageUpdate(attempt, command, target.Listing.Id, now, "connected-result");
        return ListingWorkspaceCommandSupport.Applied(command, target.Listing.Id);
    }

    public Task AuthorizeReplayAsync(PersistConnectedListingResultCommand command,
        IAtomicPersistenceSession persistence, CancellationToken ct) =>
        ListingWorkspaceCommandSupport.AuthorizeReplayAsync(command, persistence, ct);
}

public sealed class ConfirmExternalListingSignalHandler
    : IAtomicCommandHandler<ConfirmExternalListingSignalCommand, ListingWorkspaceMutationResult>,
      IAtomicReplayAuthorizer<ConfirmExternalListingSignalCommand>
{
    public async Task<ListingWorkspaceMutationResult> HandleAsync(
        ConfirmExternalListingSignalCommand command, IAtomicWriteAttempt attempt, CancellationToken ct)
    {
        var now = await ListingWorkspaceCommandSupport.AuthorizeAndLockAsync(command, attempt, ct);
        var target = await (from listing in ListingWorkspaceCommandSupport.AuthorizedListings(command, attempt.Persistence, now)
                            join publication in attempt.Persistence.Query<ListingPublication>()
                                on listing.Id equals publication.RentalListingId
                            join signal in attempt.Persistence.Query<ExternalListingSignal>()
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
            attempt.UseDatabaseWallClockForAudit(now);
            attempt.BindSemanticAudit(target.Publication, ListingWorkspaceCommandSupport.Audit(
                command, nameof(ListingPublication), AuditLogOperation.Updated,
                "Applied confirmed external listing hint", target.Publication.Id));
        }
        await attempt.FlushBusinessAsync(ct);
        attempt.StageSemanticEvent(new AtomicSemanticAudit(
            command.PortfolioId, nameof(ExternalListingSignal), target.Signal.Id,
            AuditLogOperation.Updated, command.ActorUserId,
            ChangeReason: command.Accept ? "Confirmed external listing hint" : "Rejected external listing hint"), now);
        ListingWorkspaceCommandSupport.StageUpdate(attempt, command, target.Listing.Id, now, "signal-confirm");
        return ListingWorkspaceCommandSupport.Applied(command, target.Listing.Id);
    }

    public Task AuthorizeReplayAsync(ConfirmExternalListingSignalCommand command,
        IAtomicPersistenceSession persistence, CancellationToken ct) =>
        ListingWorkspaceCommandSupport.AuthorizeReplayAsync(command, persistence, ct);
}

internal static class ListingWorkspaceCommandSupport
{
    internal static async Task<DateTime> AuthorizeAndLockAsync(
        IListingWorkspaceAtomicCommand command, IAtomicWriteAttempt attempt, CancellationToken ct)
    {
        Validate(command);
        await attempt.Locking.AcquireAsync(AtomicLockResource.AuthSession, command.AuthSessionId, ct);
        await attempt.Locking.AcquireAsync(AtomicLockResource.WorkspaceAccessContext, command.AccessContextId, ct);
        await attempt.Locking.AcquireAsync(AtomicLockResource.Unit, command.UnitId, ct);
        var now = await attempt.Persistence.ReadDatabaseClockUtcAsync(ct);
        if (!await AuthorizedUnits(command, attempt.Persistence, now).AnyAsync(ct))
            throw new UnauthorizedAccessException("The listing is outside the caller's current property scope.");
        return now;
    }

    internal static async Task AuthorizeReplayAsync(
        IListingWorkspaceAtomicCommand command, IAtomicPersistenceSession persistence, CancellationToken ct)
    {
        Validate(command);
        var now = await persistence.ReadDatabaseClockUtcAsync(ct);
        if (!await AuthorizedUnits(command, persistence, now).AnyAsync(ct))
            throw new UnauthorizedAccessException("The listing is outside the caller's current property scope.");
    }

    internal static IQueryable<Unit> AuthorizedUnits(
        IListingWorkspaceAtomicCommand command, IAtomicPersistenceSession persistence, DateTime now)
    {
        var assignments = persistence.Query<MembershipRoleAssignment>().Where(assignment =>
            assignment.PortfolioId == command.PortfolioId
            && assignment.Status == MembershipRoleAssignmentStatus.Active
            && assignment.SuspendedAtUtc == null && assignment.RevokedAtUtc == null
            && assignment.EffectiveFromUtc <= now
            && (assignment.EffectiveToUtc == null || assignment.EffectiveToUtc > now));

        return persistence.Query<Unit>().Where(unit =>
            unit.Id == command.UnitId && unit.PortfolioId == command.PortfolioId
            && unit.Property != null && unit.Property.PortfolioId == command.PortfolioId
            && persistence.Query<AuthSession>().Any(session =>
                session.Id == command.AuthSessionId && session.UserId == command.ActorUserId
                && session.ActiveAccessContextId == command.AccessContextId
                && session.Status == AuthSessionStatus.Active && session.RevokedAtUtc == null
                && session.ExpiresAtUtc > now)
            && persistence.Query<WorkspaceAccessContext>().Any(context =>
                context.Id == command.AccessContextId && context.UserId == command.ActorUserId
                && context.PortfolioId == command.PortfolioId
                && context.AccessRevision == command.AccessRevision
                && context.Status == WorkspaceAccessContextStatus.Active
                && context.SuspendedAtUtc == null && context.RevokedAtUtc == null)
            && persistence.Query<WorkspaceMembership>().Any(membership =>
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
        IListingWorkspaceAtomicCommand command, IAtomicPersistenceSession persistence, DateTime now) =>
        from unit in AuthorizedUnits(command, persistence, now)
        join listing in persistence.Query<RentalListing>() on unit.Id equals listing.UnitId
        where listing.PortfolioId == command.PortfolioId
        select listing;

    internal static async Task<ListingPhotoTarget?> LoadPhotoTargetAsync(
        IListingWorkspaceAtomicCommand command, IAtomicPersistenceSession persistence, DateTime now, CancellationToken ct)
    {
        var photoId = command switch
        {
            UpdateListingPhotoCommand update => update.PhotoId,
            RemoveListingPhotoCommand remove => remove.PhotoId,
            _ => throw new ArgumentOutOfRangeException(nameof(command)),
        };
        return await (from listing in AuthorizedListings(command, persistence, now)
                      join photo in persistence.Query<ListingPhoto>() on listing.Id equals photo.RentalListingId
                      where photo.Id == photoId && photo.PortfolioId == command.PortfolioId
                      select new ListingPhotoTarget(listing, photo)).SingleOrDefaultAsync(ct);
    }

    internal static void AuditPhotoMutation(
        IListingWorkspaceAtomicCommand command, IAtomicWriteAttempt attempt,
        ListingPhotoTarget target, DateTime now, string reason)
    {
        attempt.UseDatabaseWallClockForAudit(now);
        attempt.BindSemanticAudit(target.Listing, Audit(
            command, nameof(RentalListing), AuditLogOperation.Updated, reason));
        attempt.StageSemanticEvent(new AtomicSemanticAudit(
            command.PortfolioId, nameof(ListingPhoto), target.Photo.Id,
            AuditLogOperation.Updated, command.ActorUserId, ChangeReason: reason), now);
    }

    internal static AtomicSemanticAudit Audit(
        IListingWorkspaceAtomicCommand command, string entityType,
        AuditLogOperation operation, string reason, int entityId = 0) =>
        new(command.PortfolioId, entityType, entityId, operation,
            UserId: command.ActorUserId, ChangeReason: reason);

    internal static void StageUpdate(
        IAtomicWriteAttempt attempt, IListingWorkspaceAtomicCommand command,
        int listingId, DateTime now, string operation)
    {
        attempt.StageOutbox(new OutboxMessage
        {
            PortfolioId = command.PortfolioId,
            MessageType = "data-update",
            Payload = JsonSerializer.Serialize(new
            {
                entityType = nameof(RentalListing),
                entityId = listingId,
                data = new { unitId = command.UnitId, operation },
            }),
            IdempotencyKey = $"listing-workspace:{operation}:{attempt.AttemptId:N}",
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
