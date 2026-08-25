using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using RentalCommand.Api.DTOs;
using RentalCommand.Api.Writes;
using RentalCommand.Core;
using RentalCommand.Core.Authorization;
using RentalCommand.Core.Atomic;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Interfaces;
using RentalCommand.Core.Time;
using RentalCommand.Data;
using RentalCommand.Data.Authorization;

namespace RentalCommand.Api.Services.Domain;

/// <inheritdoc cref="IOwnerEntityService"/>
public class OwnerEntityService : IOwnerEntityService
{
    private const string EntityType = "OwnerEntity";
    private readonly RentalCommandDbContext _db;
    private readonly IDataUpdateService _dataUpdate;
    private readonly IRequestWriteExecutor? _writes;
    private readonly TimeProvider _timeProvider;
    private readonly string _webBaseUrl;

    public OwnerEntityService(
        RentalCommandDbContext db,
        IDataUpdateService dataUpdate,
        TimeProvider timeProvider,
        IConfiguration? configuration = null,
        IRequestWriteExecutor? writes = null)
    {
        _db = db;
        _dataUpdate = dataUpdate;
        _timeProvider = timeProvider;
        _writes = writes;
        _webBaseUrl = configuration?["App:WebBaseUrl"] ?? "https://localhost:5667";
    }

    public async Task<OwnerEntityResponse?> CreateAsync(
        WorkspaceReadScope scope,
        CreateOwnerEntityRequest request,
        string operationKey,
        CancellationToken ct = default)
    {
        var writeRequest = CoreCrudWriteSupport.Request(scope, AtomicCoreCrudMutationDomain.OwnerEntity,
            AtomicCoreCrudMutationOperation.Create, 0, operationKey, request);
        var write = CoreCrudWriteSupport.Write(
            writeRequest, CreateOwnerAsync, AuthorizeCoreCrudReplayAsync);
        var outcome = await RequireWrites().ExecuteAsync(
            CoreCrudWriteSupport.IdempotencyKey(writeRequest), write, ct);
        return DeserializeSnapshot<OwnerEntityResponse>(outcome.Value);
    }

    public async Task<OwnerEntityResponse?> UpdateAsync(
        WorkspaceReadScope scope,
        int id,
        UpdateOwnerEntityRequest request,
        string operationKey,
        CancellationToken ct = default)
    {
        var writeRequest = CoreCrudWriteSupport.Request(scope, AtomicCoreCrudMutationDomain.OwnerEntity,
            AtomicCoreCrudMutationOperation.Update, id, operationKey, request);
        var write = CoreCrudWriteSupport.Write(
            writeRequest, UpdateOwnerAsync, AuthorizeCoreCrudReplayAsync);
        var outcome = await RequireWrites().ExecuteAsync(
            CoreCrudWriteSupport.IdempotencyKey(writeRequest), write, ct);
        return DeserializeSnapshot<OwnerEntityResponse>(outcome.Value);
    }

    public async Task<bool> DeleteAsync(
        WorkspaceReadScope scope,
        int id,
        string operationKey,
        CancellationToken ct = default)
    {
        var writeRequest = CoreCrudWriteSupport.Request(scope, AtomicCoreCrudMutationDomain.OwnerEntity,
            AtomicCoreCrudMutationOperation.Delete, id, operationKey, new { });
        var write = CoreCrudWriteSupport.Write(
            writeRequest, DeleteOwnerAsync, AuthorizeCoreCrudReplayAsync);
        var outcome = await RequireWrites().ExecuteAsync(
            CoreCrudWriteSupport.IdempotencyKey(writeRequest), write, ct);
        return outcome.Value.Found;
    }

    private async Task<AtomicCoreCrudMutationResult> CreateOwnerAsync(
        CoreCrudWriteRequest request,
        IAtomicCommandContext context,
        CancellationToken ct)
    {
        var now = await CoreCrudWriteSupport.BeginExecutionAsync(request, _db, context, ct);
        var create = CoreCrudWriteSupport.Read<CreateOwnerEntityRequest>(request);
        var owner = new OwnerEntity
        {
            PortfolioId = request.PortfolioId, OwnerEntityType = create.OwnerEntityType,
            Name = create.Name, TaxId = create.TaxId, AddressLine1 = create.AddressLine1,
            AddressLine2 = create.AddressLine2, City = create.City, State = create.State,
            PostalCode = create.PostalCode, Phone = create.Phone, Email = create.Email,
            CreatedAt = now, UpdatedAt = now,
        };
        _db.Add(owner);
        context.BindSemanticAudit(owner, TransactionalWriteDefaults.Audit(
            request, nameof(OwnerEntity), AuditLogOperation.Created,
            $"Owner {owner.Name} created", 0));
        await context.FlushBusinessAsync(ct);
        TransactionalWriteDefaults.StageDataUpdate(
            request, context, nameof(OwnerEntity), owner.Id, now, "entity");
        return new AtomicCoreCrudMutationResult(
            true, true, owner.Id, await SnapshotOwnerAsync(owner, context, ct));
    }

    private async Task<AtomicCoreCrudMutationResult> UpdateOwnerAsync(
        CoreCrudWriteRequest request,
        IAtomicCommandContext context,
        CancellationToken ct)
    {
        var now = await CoreCrudWriteSupport.BeginExecutionAsync(
            request, _db, context, ct, authorize: false);
        var owner = await _db.OwnerEntities.SingleOrDefaultAsync(entity =>
            entity.Id == request.EntityId && entity.PortfolioId == request.PortfolioId
            && entity.DeletedAt == null, ct);
        if (owner is null) return new AtomicCoreCrudMutationResult(false, false, 0);
        await CoreCrudWriteSupport.AuthorizeExecutionAsync(request, _db, now, ct);
        var update = CoreCrudWriteSupport.Read<UpdateOwnerEntityRequest>(request);
        if (update.OwnerEntityType.HasValue) owner.OwnerEntityType = update.OwnerEntityType.Value;
        if (update.Name is not null) owner.Name = update.Name;
        if (update.TaxId is not null) owner.TaxId = update.TaxId;
        if (update.AddressLine1 is not null) owner.AddressLine1 = update.AddressLine1;
        if (update.AddressLine2 is not null) owner.AddressLine2 = update.AddressLine2;
        if (update.City is not null) owner.City = update.City;
        if (update.State is not null) owner.State = update.State;
        if (update.PostalCode is not null) owner.PostalCode = update.PostalCode;
        if (update.Phone is not null) owner.Phone = update.Phone;
        if (update.Email is not null) owner.Email = update.Email;
        owner.UpdatedAt = now;
        context.BindSemanticAudit(owner, TransactionalWriteDefaults.Audit(
            request, nameof(OwnerEntity), AuditLogOperation.Updated,
            $"Owner {owner.Name} updated", owner.Id));
        await context.FlushBusinessAsync(ct);
        TransactionalWriteDefaults.StageDataUpdate(
            request, context, nameof(OwnerEntity), owner.Id, now, "entity");
        return new AtomicCoreCrudMutationResult(
            true, true, owner.Id, await SnapshotOwnerAsync(owner, context, ct));
    }

    private async Task<AtomicCoreCrudMutationResult> DeleteOwnerAsync(
        CoreCrudWriteRequest request,
        IAtomicCommandContext context,
        CancellationToken ct)
    {
        var now = await CoreCrudWriteSupport.BeginExecutionAsync(
            request, _db, context, ct, authorize: false);
        var owner = await _db.OwnerEntities.SingleOrDefaultAsync(entity =>
            entity.Id == request.EntityId && entity.PortfolioId == request.PortfolioId
            && entity.DeletedAt == null, ct);
        if (owner is null) return new AtomicCoreCrudMutationResult(false, false, 0);
        await CoreCrudWriteSupport.AuthorizeExecutionAsync(request, _db, now, ct);
        var deleteGuard = await _db.OwnerEntities
            .AsNoTracking()
            .Where(candidate =>
                candidate.PortfolioId == request.PortfolioId &&
                candidate.Id == owner.Id)
            .Select(candidate => new
            {
                PropertyCount = _db.PropertyOwnerships
                    .Where(ownership =>
                        ownership.PortfolioId == request.PortfolioId &&
                        ownership.OwnerEntityId == candidate.Id &&
                        (ownership.EffectiveToUtc == null || ownership.EffectiveToUtc > now) &&
                        ownership.Property != null &&
                        ownership.Property.DeletedAt == null)
                    .Select(ownership => ownership.PropertyId)
                    .Distinct()
                    .Count(),
                PortalAccessCount = _db.OwnerUserAccesses.Count(access =>
                    access.PortfolioId == request.PortfolioId &&
                    access.OwnerEntityId == candidate.Id &&
                    access.RevokedAtUtc == null),
            })
            .SingleAsync(ct);
        if (deleteGuard.PropertyCount > 0)
            throw Conflict($"This owner is assigned to {deleteGuard.PropertyCount} {(deleteGuard.PropertyCount == 1 ? "property" : "properties")}. Reassign or clear those properties before deleting this owner.");
        if (deleteGuard.PortalAccessCount > 0)
            throw Conflict("Revoke this owner's portal access before deleting the owner.");
        var distributions = await _db.OwnerDistributions.AsNoTracking().CountAsync(row =>
            row.PortfolioId == request.PortfolioId && row.OwnerEntityId == owner.Id, ct);
        if (distributions > 0)
            throw Conflict($"This owner has {distributions} recorded {(distributions == 1 ? "distribution" : "distributions")}. Delete or reassign them first.");
        var contributions = await _db.OwnerContributions.AsNoTracking().CountAsync(row =>
            row.PortfolioId == request.PortfolioId && row.OwnerEntityId == owner.Id, ct);
        if (contributions > 0)
            throw Conflict($"This owner has {contributions} recorded {(contributions == 1 ? "contribution" : "contributions")}. Delete or reassign them first.");
        owner.DeletedAt = now;
        owner.UpdatedAt = now;
        context.BindSemanticAudit(owner, TransactionalWriteDefaults.Audit(
            request, nameof(OwnerEntity), AuditLogOperation.Deleted,
            $"Owner {owner.Name} deleted", owner.Id));
        await context.FlushBusinessAsync(ct);
        TransactionalWriteDefaults.StageDataUpdate(
            request, context, nameof(OwnerEntity), owner.Id, now, "entity", deleted: true);
        return new AtomicCoreCrudMutationResult(true, true, owner.Id);
    }

    private Task AuthorizeCoreCrudReplayAsync(
        CoreCrudWriteRequest request,
        IAtomicCommandContext context,
        CancellationToken ct) =>
        CoreCrudWriteSupport.AuthorizeReplayAsync(request, _db, context, ct);

    public async Task<ActivateOwnerPortalAccessResponse> ActivateOwnerPortalAccessAsync(
        WorkspaceReadScope scope,
        int id,
        ActivateOwnerPortalAccessRequest request,
        string operationKey,
        CancellationToken ct = default)
    {
        var command = new ActivateOwnerPortalAccessCommand(
            scope.PortfolioId,
            id,
            _timeProvider.UtcNow(),
            request.EffectiveToUtc,
            string.IsNullOrWhiteSpace(request.Reason)
                ? "Owner portal access activated from owner management"
                : request.Reason.Trim(),
            scope.UserId,
            scope.SessionId,
            scope.AccessContextId,
            scope.AccessRevision,
            StableGuid($"{scope.PortfolioId}:owner-portal:{id}"),
            _webBaseUrl);
        var keyDigest = Digest(operationKey);
        var outcome = await RequireWrites().ExecuteExactAsync(
            $"{scope.PortfolioId}:{id}:{keyDigest}",
            OwnerRelationshipAccessWriteSupport.Write(_db, command), ct);
        return outcome.Value.Outcome switch
        {
            ActivateOwnerPortalAccessMutationOutcome.Activated => Activation(
                ActivateOwnerPortalAccessOutcome.Activated,
                outcome.Value.OwnerEntityId,
                outcome.Value.OwnerEmail,
                outcome.Value.TargetAccessContextId,
                outcome.Value.OwnerUserAccessId,
                outcome.Value.AccessRevision,
                outcome.Disposition == AtomicCommandDisposition.Replayed,
                outcome.Value.RequiresAccountActivation,
                outcome.Value.InvitationExpiresAtUtc,
                "Owner portal access activated."),
            ActivateOwnerPortalAccessMutationOutcome.InvitationPending => Activation(
                ActivateOwnerPortalAccessOutcome.InvitationPending,
                outcome.Value.OwnerEntityId,
                outcome.Value.OwnerEmail,
                outcome.Value.TargetAccessContextId,
                outcome.Value.OwnerUserAccessId,
                outcome.Value.AccessRevision,
                outcome.Disposition == AtomicCommandDisposition.Replayed,
                outcome.Value.RequiresAccountActivation,
                outcome.Value.InvitationExpiresAtUtc,
                "Owner portal invitation is pending. The owner can sign in after using the queued activation email."),
            ActivateOwnerPortalAccessMutationOutcome.AlreadyActive => Activation(
                ActivateOwnerPortalAccessOutcome.AlreadyActive,
                outcome.Value.OwnerEntityId,
                outcome.Value.OwnerEmail,
                outcome.Value.TargetAccessContextId,
                outcome.Value.OwnerUserAccessId,
                outcome.Value.AccessRevision,
                outcome.Disposition == AtomicCommandDisposition.Replayed,
                outcome.Value.RequiresAccountActivation,
                outcome.Value.InvitationExpiresAtUtc,
                "Owner portal access is already active for this owner."),
            ActivateOwnerPortalAccessMutationOutcome.MissingOwnerEmail => Activation(
                ActivateOwnerPortalAccessOutcome.MissingOwnerEmail,
                outcome.Value.OwnerEntityId,
                outcome.Value.OwnerEmail,
                outcome.Value.TargetAccessContextId,
                outcome.Value.OwnerUserAccessId,
                outcome.Value.AccessRevision,
                outcome.Disposition == AtomicCommandDisposition.Replayed,
                outcome.Value.RequiresAccountActivation,
                outcome.Value.InvitationExpiresAtUtc,
                "Add an email address to this owner before activating owner portal access."),
            ActivateOwnerPortalAccessMutationOutcome.InactiveWorkspaceAccess => Activation(
                ActivateOwnerPortalAccessOutcome.InactiveWorkspaceAccess,
                outcome.Value.OwnerEntityId,
                outcome.Value.OwnerEmail,
                outcome.Value.TargetAccessContextId,
                outcome.Value.OwnerUserAccessId,
                outcome.Value.AccessRevision,
                outcome.Disposition == AtomicCommandDisposition.Replayed,
                outcome.Value.RequiresAccountActivation,
                outcome.Value.InvitationExpiresAtUtc,
                "The matching user's workspace access is not active. Reactivate or restore that account before retrying."),
            ActivateOwnerPortalAccessMutationOutcome.PrimaryOwnerNotSupported => Activation(
                ActivateOwnerPortalAccessOutcome.PrimaryOwnerNotSupported,
                outcome.Value.OwnerEntityId,
                outcome.Value.OwnerEmail,
                outcome.Value.TargetAccessContextId,
                outcome.Value.OwnerUserAccessId,
                outcome.Value.AccessRevision,
                outcome.Disposition == AtomicCommandDisposition.Replayed,
                outcome.Value.RequiresAccountActivation,
                outcome.Value.InvitationExpiresAtUtc,
                "Primary owners already use the management account and cannot be activated as a separate owner portal identity."),
            ActivateOwnerPortalAccessMutationOutcome.Invalid => Activation(
                ActivateOwnerPortalAccessOutcome.Invalid,
                outcome.Value.OwnerEntityId,
                outcome.Value.OwnerEmail,
                outcome.Value.TargetAccessContextId,
                outcome.Value.OwnerUserAccessId,
                outcome.Value.AccessRevision,
                outcome.Disposition == AtomicCommandDisposition.Replayed,
                outcome.Value.RequiresAccountActivation,
                outcome.Value.InvitationExpiresAtUtc,
                "Owner portal access could not be activated with the requested effective period."),
            _ => Activation(
                ActivateOwnerPortalAccessOutcome.NotFound,
                outcome.Value.OwnerEntityId,
                outcome.Value.OwnerEmail,
                outcome.Value.TargetAccessContextId,
                outcome.Value.OwnerUserAccessId,
                outcome.Value.AccessRevision,
                outcome.Disposition == AtomicCommandDisposition.Replayed,
                outcome.Value.RequiresAccountActivation,
                outcome.Value.InvitationExpiresAtUtc,
                "Owner portal access could not be activated because the owner was not found."),
        };
    }

    public async Task<RevokeOwnerPortalAccessResponse> RevokeOwnerPortalAccessAsync(
        WorkspaceReadScope scope,
        int id,
        RevokeOwnerPortalAccessRequest request,
        string operationKey,
        CancellationToken ct = default)
    {
        var keyDigest = Digest(operationKey);
        var command = new RevokeOwnerPortalAccessCommand(
            scope.PortfolioId,
            id,
            string.IsNullOrWhiteSpace(request.Reason)
                ? "Owner portal access revoked from owner management"
                : request.Reason.Trim(),
            scope.UserId,
            scope.SessionId,
            scope.AccessContextId,
            scope.AccessRevision,
            _timeProvider.UtcNow(),
            $"owner-entity.portal-access.revoke:{scope.PortfolioId}:{id}:{keyDigest}");
        var outcome = await RequireWrites().ExecuteExactAsync(
            $"{scope.PortfolioId}:{id}:{keyDigest}",
            OwnerRelationshipAccessWriteSupport.Write(_db, command), ct);
        var replayed = outcome.Disposition == AtomicCommandDisposition.Replayed;
        return outcome.Value.Outcome switch
        {
            RevokeOwnerPortalAccessMutationOutcome.Revoked => new(
                RevokeOwnerPortalAccessOutcome.Revoked,
                outcome.Value.OwnerEntityId,
                outcome.Value.OwnerEmail,
                outcome.Value.RevokedRelationshipCount,
                outcome.Value.TargetAccessContextId,
                outcome.Value.OwnerUserAccessId,
                outcome.Value.AccessRevision,
                replayed,
                "Owner portal access revoked."),
            RevokeOwnerPortalAccessMutationOutcome.AlreadyRevoked => new(
                RevokeOwnerPortalAccessOutcome.AlreadyRevoked,
                outcome.Value.OwnerEntityId,
                outcome.Value.OwnerEmail,
                outcome.Value.RevokedRelationshipCount,
                outcome.Value.TargetAccessContextId,
                outcome.Value.OwnerUserAccessId,
                outcome.Value.AccessRevision,
                replayed,
                "Owner portal access is already revoked."),
            _ => new(
                RevokeOwnerPortalAccessOutcome.NotFound,
                outcome.Value.OwnerEntityId,
                outcome.Value.OwnerEmail,
                outcome.Value.RevokedRelationshipCount,
                outcome.Value.TargetAccessContextId,
                outcome.Value.OwnerUserAccessId,
                outcome.Value.AccessRevision,
                replayed,
                "Owner portal access could not be revoked because the owner was not found."),
        };
    }

    internal IQueryable<OwnerPortalActivationTarget> BuildOwnerPortalActivationTargetQuery(
        int portfolioId,
        int ownerEntityId,
        string normalizedEmail)
    {
        return from owner in _db.OwnerEntities.AsNoTracking()
               join user in _db.Users.AsNoTracking()
                   on owner.Email!.ToUpper() equals user.NormalizedEmail
               join context in _db.WorkspaceAccessContexts.AsNoTracking()
                   on new { UserId = user.Id, owner.PortfolioId }
                   equals new { context.UserId, context.PortfolioId }
               where owner.PortfolioId == portfolioId &&
                     owner.Id == ownerEntityId &&
                     owner.DeletedAt == null &&
                     !owner.IsPrimary &&
                     owner.Email != null &&
                     user.NormalizedEmail == normalizedEmail
               select new OwnerPortalActivationTarget(
                   context.Id,
                   context.UserId,
                   context.AccessRevision,
                   context.Status == WorkspaceAccessContextStatus.Active &&
                   context.SuspendedAtUtc == null &&
                   context.RevokedAtUtc == null,
                   _db.OwnerUserAccesses
                       .Where(access =>
                           access.PortfolioId == portfolioId &&
                           access.AccessContextId == context.Id &&
                           access.ApplicationUserId == context.UserId &&
                           access.OwnerEntityId == owner.Id &&
                           access.RevokedAtUtc == null)
                       .Select(access => (int?)access.Id)
                       .FirstOrDefault());
    }

    private IRequestWriteExecutor RequireWrites() =>
        _writes ?? throw new InvalidOperationException(
            "The shared request write executor is required for owner changes.");

    private async Task<string> SnapshotOwnerAsync(
        OwnerEntity owner,
        IAtomicCommandContext context,
        CancellationToken ct)
    {
        var now = await context.ReadDatabaseClockUtcAsync(ct);
        var assigned = await _db.PropertyOwnerships.AsNoTracking()
            .Where(ownership =>
                ownership.PortfolioId == owner.PortfolioId
                && ownership.OwnerEntityId == owner.Id
                && ownership.EffectiveFromUtc <= now
                && (ownership.EffectiveToUtc == null || ownership.EffectiveToUtc > now)
                && ownership.Property != null
                && ownership.Property.DeletedAt == null)
            .Select(ownership => ownership.PropertyId)
            .Distinct()
            .CountAsync(ct);
        return JsonSerializer.Serialize(OwnerEntityResponse.FromEntity(owner, assigned));
    }

    private static DomainValidationException Conflict(string message) => new(message, 409);

    private static TResponse? DeserializeSnapshot<TResponse>(AtomicCoreCrudMutationResult result)
        where TResponse : class =>
        result.Found && result.ResponseJson is not null
            ? JsonSerializer.Deserialize<TResponse>(result.ResponseJson)
            : null;

    public async Task<IReadOnlyList<OwnerEntityResponse>> ListAsync(WorkspaceReadScope scope, ListQuery query, CancellationToken ct = default)
    {
        var page = await ListPageAsync(scope, query, ct);
        return page.Items;
    }

    public async Task<OwnerEntityListResponse> ListPageAsync(WorkspaceReadScope scope, ListQuery query, CancellationToken ct = default)
    {
        var q = AuthorizedOwnersForRead(scope);

        if (query is OwnerEntityListQuery { OwnerEntityType: { } ownerEntityType })
        {
            q = q.Where(o => o.OwnerEntityType == ownerEntityType);
        }

        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var term = query.Search.Trim();
            q = q.Where(o =>
                EF.Functions.ILike(o.Name, $"%{term}%") ||
                (o.TaxId != null && EF.Functions.ILike(o.TaxId, $"%{term}%")) ||
                (o.Email != null && EF.Functions.ILike(o.Email, $"%{term}%")) ||
                (o.Phone != null && EF.Functions.ILike(o.Phone, $"%{term}%")));
        }

        q = query.SortField switch
        {
            "name" => query.SortDescending ? q.OrderByDescending(o => o.Name) : q.OrderBy(o => o.Name),
            "type" => query.SortDescending ? q.OrderByDescending(o => o.OwnerEntityType) : q.OrderBy(o => o.OwnerEntityType),
            "ownerentitytype" => query.SortDescending ? q.OrderByDescending(o => o.OwnerEntityType) : q.OrderBy(o => o.OwnerEntityType),
            "updatedat" => query.SortDescending ? q.OrderByDescending(o => o.UpdatedAt) : q.OrderBy(o => o.UpdatedAt),
            _ => query.SortDescending ? q.OrderByDescending(o => o.CreatedAt) : q.OrderBy(o => o.CreatedAt),
        };

        var totalCount = await q.CountAsync(ct);

        var items = await ProjectOwnerResponses(q, AuthorizedProperties(scope, CapabilityKeys.MoneyOwnerReportsRead))
            .Skip(query.NormalizedSkip)
            .Take(query.NormalizedTake)
            .ToListAsync(ct);

        return new OwnerEntityListResponse
        {
            Items = items,
            TotalCount = totalCount,
            Skip = query.NormalizedSkip,
            Take = query.NormalizedTake,
        };
    }

    private IQueryable<OwnerEntityResponse> ProjectOwnerResponses(
        IQueryable<OwnerEntity> query,
        IQueryable<Property> authorizedProperties)
    {
        var now = _timeProvider.UtcNow();
        return query.Select(o => new OwnerEntityResponse
        {
            Id = o.Id,
            PortfolioId = o.PortfolioId,
            OwnerEntityType = o.OwnerEntityType,
            Name = o.Name,
            TaxId = o.TaxId,
            AddressLine1 = o.AddressLine1,
            AddressLine2 = o.AddressLine2,
            City = o.City,
            State = o.State,
            PostalCode = o.PostalCode,
            Phone = o.Phone,
            Email = o.Email,
            AssignedPropertyCount = _db.PropertyOwnerships.Count(ownership =>
                ownership.PortfolioId == o.PortfolioId
                && ownership.OwnerEntityId == o.Id
                && ownership.EffectiveFromUtc <= now
                && (ownership.EffectiveToUtc == null || ownership.EffectiveToUtc > now)
                && authorizedProperties.Any(property => property.Id == ownership.PropertyId)),
            IsPrimary = o.IsPrimary,
            HasActiveOwnerPortalAccess = _db.OwnerUserAccesses.Any(access =>
                access.PortfolioId == o.PortfolioId
                && access.OwnerEntityId == o.Id
                && access.RevokedAtUtc == null
                && access.ApplicationUser!.PasswordHash != null),
            HasPendingOwnerPortalInvitation = _db.OwnerUserAccesses.Any(access =>
                access.PortfolioId == o.PortfolioId
                && access.OwnerEntityId == o.Id
                && access.RevokedAtUtc == null
                && access.ApplicationUser!.PasswordHash == null
                && _db.WorkspaceInvitations.Any(invitation =>
                    invitation.PortfolioId == o.PortfolioId
                    && invitation.InvitedUserId == access.ApplicationUserId
                    && invitation.AcceptedAtUtc == null
                    && invitation.RevokedAtUtc == null
                    && invitation.ExpiresAtUtc > now)),
            CreatedAt = o.CreatedAt,
            UpdatedAt = o.UpdatedAt,
        });
    }

    private async Task<OwnerEntityResponse?> GetProjectedAsync(
        WorkspaceReadScope scope,
        int id,
        string capabilityKey,
        CancellationToken ct = default)
    {
        return await ProjectOwnerResponses(
                capabilityKey == CapabilityKeys.MoneyOwnerReportsRead
                    ? AuthorizedOwnersForRead(scope).Where(o => o.Id == id)
                    : AuthorizedOwnersForMutation(scope, capabilityKey).Where(o => o.Id == id),
                AuthorizedProperties(scope, capabilityKey))
            .FirstOrDefaultAsync(ct);
    }

    public Task<OwnerEntityResponse?> GetAsync(WorkspaceReadScope scope, int id, CancellationToken ct = default)
    {
        return GetProjectedAsync(scope, id, CapabilityKeys.MoneyOwnerReportsRead, ct);
    }

    private IQueryable<Property> AuthorizedProperties(WorkspaceReadScope scope, string capabilityKey) =>
        _db.Properties
            .AsNoTracking()
            .WhereAuthorized(_db, scope, capabilityKey, _timeProvider.UtcNow());

    private IQueryable<OwnerEntity> AuthorizedOwnersForRead(WorkspaceReadScope scope)
    {
        var now = _timeProvider.UtcNow();
        var authorizedProperties = AuthorizedProperties(scope, CapabilityKeys.MoneyOwnerReportsRead);
        var allProperties = _db.AuthorizedWorkspaceAssignments(
            scope,
            [CapabilityKeys.MoneyOwnerReportsRead],
            CapabilityAuthorizationTargetKind.Property,
            _timeProvider.UtcNow());

        return _db.OwnerEntities
            .AsNoTracking()
            .Where(owner =>
                owner.PortfolioId == scope.PortfolioId &&
                (allProperties.Any() || _db.PropertyOwnerships.Any(ownership =>
                    ownership.PortfolioId == scope.PortfolioId
                    && ownership.OwnerEntityId == owner.Id
                    && ownership.EffectiveFromUtc <= now
                    && (ownership.EffectiveToUtc == null || ownership.EffectiveToUtc > now)
                    && authorizedProperties.Any(property => property.Id == ownership.PropertyId))));
    }

    private IQueryable<OwnerEntity> AuthorizedOwnersForMutation(WorkspaceReadScope scope, string capabilityKey)
    {
        var now = _timeProvider.UtcNow();
        var authorizedProperties = AuthorizedProperties(scope, capabilityKey);
        var allProperties = _db.AuthorizedWorkspaceAssignments(
            scope,
            [capabilityKey],
            CapabilityAuthorizationTargetKind.Property,
            _timeProvider.UtcNow());

        return _db.OwnerEntities.Where(owner =>
            owner.PortfolioId == scope.PortfolioId &&
            (allProperties.Any() ||
             (_db.PropertyOwnerships.Any(ownership =>
                  ownership.PortfolioId == scope.PortfolioId
                  && ownership.OwnerEntityId == owner.Id
                  && ownership.EffectiveFromUtc <= now
                  && (ownership.EffectiveToUtc == null || ownership.EffectiveToUtc > now)) &&
              !_db.PropertyOwnerships.Any(ownership =>
                  ownership.PortfolioId == scope.PortfolioId
                  && ownership.OwnerEntityId == owner.Id
                  && ownership.EffectiveFromUtc <= now
                  && (ownership.EffectiveToUtc == null || ownership.EffectiveToUtc > now)
                  && !authorizedProperties.Any(authorized => authorized.Id == ownership.PropertyId)))));
    }

    private static ActivateOwnerPortalAccessResponse Activation(
        ActivateOwnerPortalAccessOutcome outcome,
        int ownerEntityId,
        string? ownerEmail,
        int? targetAccessContextId,
        int? ownerUserAccessId,
        long? accessRevision,
        bool replayed,
        bool requiresAccountActivation,
        DateTime? invitationExpiresAtUtc,
        string message) => new(
        outcome,
        ownerEntityId,
        ownerEmail,
        targetAccessContextId,
        ownerUserAccessId,
        accessRevision,
        replayed,
        requiresAccountActivation,
        invitationExpiresAtUtc,
        message);

    private static string Digest(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private static Guid StableGuid(string value)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return new Guid(bytes.AsSpan(0, 16));
    }

    internal sealed record OwnerPortalActivationTarget(
        int TargetAccessContextId,
        int TargetUserId,
        long TargetAccessRevision,
        bool IsActive,
        int? ExistingOwnerUserAccessId);
}
