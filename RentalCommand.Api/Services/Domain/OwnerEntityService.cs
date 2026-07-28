using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using RentalCommand.Api.DTOs;
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
    private static readonly AtomicJsonResultCodec<ActivateOwnerPortalAccessMutationResult> OwnerActivationCodec =
        new("owner-portal-access.activation.v1");

    private readonly RentalCommandDbContext _db;
    private readonly IDataUpdateService _dataUpdate;
    private readonly IAtomicUnitOfWork? _atomic;
    private readonly TimeProvider _timeProvider;
    private readonly string _webBaseUrl;

    public OwnerEntityService(
        RentalCommandDbContext db,
        IDataUpdateService dataUpdate,
        TimeProvider timeProvider,
        IAtomicUnitOfWork? atomic = null,
        IConfiguration? configuration = null)
    {
        _db = db;
        _dataUpdate = dataUpdate;
        _timeProvider = timeProvider;
        _atomic = atomic;
        _webBaseUrl = configuration?["App:WebBaseUrl"] ?? "https://localhost:5667";
    }

    public async Task<OwnerEntityResponse?> CreateAsync(
        WorkspaceReadScope scope,
        CreateOwnerEntityRequest request,
        string operationKey,
        CancellationToken ct = default)
    {
        var command = AtomicCoreCrudMutation.Command(scope, AtomicCoreCrudMutationDomain.OwnerEntity,
            AtomicCoreCrudMutationOperation.Create, 0, operationKey, request);
        var outcome = await Atomic.ExecuteAsync(
            AtomicCoreCrudMutation.Identity(command), command, AtomicCoreCrudMutation.Codec, ct);
        return DeserializeSnapshot<OwnerEntityResponse>(outcome.Value);
    }

    public async Task<OwnerEntityResponse?> UpdateAsync(
        WorkspaceReadScope scope,
        int id,
        UpdateOwnerEntityRequest request,
        string operationKey,
        CancellationToken ct = default)
    {
        var command = AtomicCoreCrudMutation.Command(scope, AtomicCoreCrudMutationDomain.OwnerEntity,
            AtomicCoreCrudMutationOperation.Update, id, operationKey, request);
        var outcome = await Atomic.ExecuteAsync(
            AtomicCoreCrudMutation.Identity(command), command, AtomicCoreCrudMutation.Codec, ct);
        return DeserializeSnapshot<OwnerEntityResponse>(outcome.Value);
    }

    public async Task<bool> DeleteAsync(
        WorkspaceReadScope scope,
        int id,
        string operationKey,
        CancellationToken ct = default)
    {
        var command = AtomicCoreCrudMutation.Command(scope, AtomicCoreCrudMutationDomain.OwnerEntity,
            AtomicCoreCrudMutationOperation.Delete, id, operationKey, new { });
        var outcome = await Atomic.ExecuteAsync(
            AtomicCoreCrudMutation.Identity(command), command, AtomicCoreCrudMutation.Codec, ct);
        return outcome.Value.Found;
    }

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
        var outcome = await Atomic.ExecuteAsync(
            new AtomicCommandIdentity(
                "owner-entity.portal-access.activate",
                $"{scope.PortfolioId}:{id}:{keyDigest}"),
            command,
            OwnerActivationCodec,
            ct);
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

    private IAtomicUnitOfWork Atomic => _atomic ?? throw new InvalidOperationException(
        "Scoped owner mutations require the atomic persistence kernel.");

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
                && access.RevokedAtUtc == null),
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
