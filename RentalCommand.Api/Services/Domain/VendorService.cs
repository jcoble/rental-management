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
using RentalCommand.Core.Time;
using RentalCommand.Core.Vendors;
using RentalCommand.Data;
using RentalCommand.Data.Authorization;

namespace RentalCommand.Api.Services.Domain;

/// <inheritdoc cref="IVendorService"/>
public class VendorService : IVendorService
{
    private const string EntityType = "Vendor";
    private static readonly string[] ReadCapabilities =
        [CapabilityKeys.WorkRead, CapabilityKeys.WorkManage];
    private static readonly AtomicJsonResultCodec<RequestVendorW9Result> RequestW9Codec =
        new("vendor-w9.request.result.v1");

    private readonly RentalCommandDbContext _db;
    private readonly IDataUpdateService _dataUpdate;
    private readonly IAtomicUnitOfWork _atomic;
    private readonly TimeProvider _timeProvider;

    public VendorService(
        RentalCommandDbContext db,
        IDataUpdateService dataUpdate,
        IAtomicUnitOfWork atomic,
        TimeProvider timeProvider)
    {
        _db = db;
        _dataUpdate = dataUpdate;
        _atomic = atomic;
        _timeProvider = timeProvider;
    }

    public async Task<IReadOnlyList<VendorResponse>> ListAsync(WorkspaceReadScope scope, ListQuery query, CancellationToken ct = default)
    {
        var page = await ListPageAsync(scope, query, ct);
        return page.Items;
    }

    public async Task<VendorListResponse> ListPageAsync(WorkspaceReadScope scope, ListQuery query, CancellationToken ct = default)
    {
        var q = AuthorizedVendors(scope, ReadCapabilities).AsNoTracking();

        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var term = query.Search.Trim();
            q = q.Where(v =>
                EF.Functions.ILike(v.Name, $"%{term}%") ||
                EF.Functions.ILike(v.ServiceType, $"%{term}%") ||
                (v.Email != null && EF.Functions.ILike(v.Email, $"%{term}%")));
        }

        q = query.SortField switch
        {
            "name" => query.SortDescending ? q.OrderByDescending(v => v.Name) : q.OrderBy(v => v.Name),
            "servicetype" => query.SortDescending ? q.OrderByDescending(v => v.ServiceType) : q.OrderBy(v => v.ServiceType),
            "updatedat" => query.SortDescending ? q.OrderByDescending(v => v.UpdatedAt) : q.OrderBy(v => v.UpdatedAt),
            _ => query.SortDescending ? q.OrderByDescending(v => v.CreatedAt) : q.OrderBy(v => v.CreatedAt),
        };

        var totalCount = await q.CountAsync(ct);

        var items = await ProjectResponses(q)
            .Skip(query.NormalizedSkip)
            .Take(query.NormalizedTake)
            .ToListAsync(ct);

        return new VendorListResponse
        {
            Items = items,
            TotalCount = totalCount,
            Skip = query.NormalizedSkip,
            Take = query.NormalizedTake,
        };
    }

    public async Task<VendorResponse?> GetAsync(WorkspaceReadScope scope, int id, CancellationToken ct = default)
    {
        return await ProjectResponses(AuthorizedVendors(scope, ReadCapabilities).AsNoTracking())
            .FirstOrDefaultAsync(v => v.Id == id, ct);
    }

    public async Task<VendorResponse?> CreateAsync(
        WorkspaceReadScope scope,
        CreateVendorRequest request,
        string operationKey,
        CancellationToken ct = default)
    {
        var command = AtomicCoreCrudMutation.Command(scope, AtomicCoreCrudMutationDomain.Vendor,
            AtomicCoreCrudMutationOperation.Create, 0, operationKey, request,
            createdAtUtc: _timeProvider.UtcNow());
        var outcome = await _atomic.ExecuteAsync(
            AtomicCoreCrudMutation.Identity(command), command, AtomicCoreCrudMutation.Codec, ct);
        return DeserializeSnapshot<VendorResponse>(outcome.Value);
    }

    public async Task<VendorResponse?> UpdateAsync(
        WorkspaceReadScope scope,
        int id,
        UpdateVendorRequest request,
        string operationKey,
        CancellationToken ct = default)
    {
        var command = AtomicCoreCrudMutation.Command(scope, AtomicCoreCrudMutationDomain.Vendor,
            AtomicCoreCrudMutationOperation.Update, id, operationKey, request,
            changedAtUtc: _timeProvider.UtcNow());
        var outcome = await _atomic.ExecuteAsync(
            AtomicCoreCrudMutation.Identity(command), command, AtomicCoreCrudMutation.Codec, ct);
        return DeserializeSnapshot<VendorResponse>(outcome.Value);
    }

    public async Task<bool> DeleteAsync(
        WorkspaceReadScope scope,
        int id,
        string operationKey,
        CancellationToken ct = default)
    {
        var command = AtomicCoreCrudMutation.Command(scope, AtomicCoreCrudMutationDomain.Vendor,
            AtomicCoreCrudMutationOperation.Delete, id, operationKey, new object(),
            changedAtUtc: _timeProvider.UtcNow());
        var outcome = await _atomic.ExecuteAsync(
            AtomicCoreCrudMutation.Identity(command), command, AtomicCoreCrudMutation.Codec, ct);
        return outcome.Value.Found;
    }

    private static TResponse? DeserializeSnapshot<TResponse>(AtomicCoreCrudMutationResult result)
        where TResponse : class =>
        result.Found && result.ResponseJson is not null
            ? JsonSerializer.Deserialize<TResponse>(result.ResponseJson)
            : null;

    public async Task<RequestW9Result> RequestW9Async(
        WorkspaceReadScope scope,
        int id,
        string clientOperationId,
        int? changedByUserId,
        CancellationToken ct = default)
    {
        var portfolioId = scope.PortfolioId;
        ArgumentException.ThrowIfNullOrWhiteSpace(clientOperationId);
        var normalizedOperationId = clientOperationId.Trim();
        if (normalizedOperationId.Length > 160)
        {
            throw new ArgumentOutOfRangeException(
                nameof(clientOperationId),
                "A request key cannot exceed 160 characters.");
        }

        var operationDigest = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(normalizedOperationId)))
            .ToLowerInvariant();
        var outcome = await _atomic.ExecuteAsync(
            new AtomicCommandIdentity(
                "vendor-w9.request",
                $"{portfolioId}:{id}:{operationDigest}"),
            new RequestVendorW9Command(
                portfolioId,
                id,
                normalizedOperationId,
                changedByUserId,
                scope.SessionId,
                scope.UserId,
                scope.AccessContextId,
                scope.AccessRevision,
                _timeProvider.UtcNow()),
            RequestW9Codec,
            ct);

        return outcome.Value.Outcome switch
        {
            RequestVendorW9Outcome.Queued => RequestW9Result.Queued(outcome.Value.Phone!),
            RequestVendorW9Outcome.VendorHasNoPhone => RequestW9Result.NoPhone(),
            _ => RequestW9Result.NotFound(),
        };
    }

    private static IQueryable<VendorResponse> ProjectResponses(IQueryable<Vendor> vendors) =>
        vendors.Select(entity => new VendorResponse
        {
            Id = entity.Id,
            PortfolioId = entity.PortfolioId,
            Name = entity.Name,
            ServiceType = entity.ServiceType,
            Email = entity.Email,
            Phone = entity.Phone,
            Website = entity.Website,
            TaxId = entity.TaxId,
            AddressLine1 = entity.AddressLine1,
            City = entity.City,
            State = entity.State,
            PostalCode = entity.PostalCode,
            Is1099Eligible = entity.Is1099Eligible,
            W9OnFile = entity.W9OnFile,
            Preferred = entity.Preferred,
            Notes = entity.Notes,
            AverageRating = entity.AverageRating,
            RatingCount = entity.RatingCount,
            JobsCompleted = entity.JobsCompleted,
            CreatedAt = entity.CreatedAt,
            UpdatedAt = entity.UpdatedAt,
        });

    private IQueryable<Vendor> AuthorizedVendors(WorkspaceReadScope scope, IReadOnlyCollection<string> capabilityKeys)
    {
        var now = TimeProvider.System.GetUtcNow().UtcDateTime;
        var assignments = _db.MembershipRoleAssignments.AsNoTracking().Where(assignment =>
            assignment.PortfolioId == scope.PortfolioId
            && assignment.Status == MembershipRoleAssignmentStatus.Active
            && assignment.SuspendedAtUtc == null && assignment.RevokedAtUtc == null
            && assignment.EffectiveFromUtc <= now
            && (assignment.EffectiveToUtc == null || assignment.EffectiveToUtc > now)
            && assignment.WorkspaceMembership!.AccessContextId == scope.AccessContextId
            && assignment.WorkspaceMembership.PortfolioId == scope.PortfolioId
            && assignment.WorkspaceMembership.Status == WorkspaceMembershipStatus.Active
            && assignment.WorkspaceMembership.SuspendedAtUtc == null
            && assignment.WorkspaceMembership.RevokedAtUtc == null
            && assignment.WorkspaceMembership.EffectiveFromUtc <= now
            && (assignment.WorkspaceMembership.EffectiveToUtc == null ||
                assignment.WorkspaceMembership.EffectiveToUtc > now)
            && _db.WorkspaceAccessContexts.Any(context =>
                context.Id == scope.AccessContextId && context.UserId == scope.UserId
                && context.PortfolioId == scope.PortfolioId
                && context.AccessRevision == scope.AccessRevision
                && context.Status == WorkspaceAccessContextStatus.Active
                && context.SuspendedAtUtc == null && context.RevokedAtUtc == null)
            && _db.AuthSessions.Any(session =>
                session.Id == scope.SessionId && session.UserId == scope.UserId
                && session.ActiveAccessContextId == scope.AccessContextId
                && session.Status == AuthSessionStatus.Active && session.RevokedAtUtc == null
                && session.ExpiresAtUtc > now)
            && assignment.RoleProfile!.Capabilities.Any(grant =>
                capabilityKeys.Contains(grant.CapabilityDefinition!.Key)
                && grant.CapabilityDefinition.AuthorizationTargetKind ==
                    CapabilityAuthorizationTargetKind.Property));
        var authorizedProperties = _db.Properties.AsNoTracking().Where(property =>
            property.PortfolioId == scope.PortfolioId && property.DeletedAt == null
            && assignments.Any(assignment =>
                assignment.ScopeKind == MembershipRoleAssignmentScopeKind.SelectedProperties
                && assignment.SelectedProperties.Any(selected =>
                    selected.PortfolioId == scope.PortfolioId
                    && selected.PropertyId == property.Id)));
        return _db.Vendors.Where(vendor =>
            vendor.PortfolioId == scope.PortfolioId &&
            (assignments.Any(assignment => assignment.ScopeKind == MembershipRoleAssignmentScopeKind.AllProperties) ||
             authorizedProperties.Any()));
    }

    private Task<bool> HasAllPropertiesAsync(
        WorkspaceReadScope scope,
        string capabilityKey,
        CancellationToken ct) =>
        _db.AuthorizedWorkspaceAssignments(
                scope,
                [capabilityKey],
                CapabilityAuthorizationTargetKind.Property,
                _timeProvider.UtcNow())
            .AnyAsync(ct);
}
