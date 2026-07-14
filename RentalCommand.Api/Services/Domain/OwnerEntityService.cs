using System.Text.Json;
using Microsoft.EntityFrameworkCore;
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

    private readonly RentalCommandDbContext _db;
    private readonly IDataUpdateService _dataUpdate;
    private readonly IAtomicUnitOfWork? _atomic;
    private readonly TimeProvider _timeProvider;

    public OwnerEntityService(
        RentalCommandDbContext db,
        IDataUpdateService dataUpdate,
        TimeProvider timeProvider,
        IAtomicUnitOfWork? atomic = null)
    {
        _db = db;
        _dataUpdate = dataUpdate;
        _timeProvider = timeProvider;
        _atomic = atomic;
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
            Address = o.Address,
            Phone = o.Phone,
            Email = o.Email,
            AssignedPropertyCount = authorizedProperties.Count(p => p.OwnerEntityId == o.Id),
            IsPrimary = o.IsPrimary,
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
                (allProperties.Any() || authorizedProperties.Any(property => property.OwnerEntityId == owner.Id)));
    }

    private IQueryable<OwnerEntity> AuthorizedOwnersForMutation(WorkspaceReadScope scope, string capabilityKey)
    {
        var authorizedProperties = AuthorizedProperties(scope, capabilityKey);
        var allProperties = _db.AuthorizedWorkspaceAssignments(
            scope,
            [capabilityKey],
            CapabilityAuthorizationTargetKind.Property,
            _timeProvider.UtcNow());

        return _db.OwnerEntities.Where(owner =>
            owner.PortfolioId == scope.PortfolioId &&
            (allProperties.Any() ||
             (_db.Properties.Any(property =>
                  property.PortfolioId == scope.PortfolioId && property.OwnerEntityId == owner.Id) &&
              !_db.Properties.Any(property =>
                  property.PortfolioId == scope.PortfolioId &&
                  property.OwnerEntityId == owner.Id &&
                  !authorizedProperties.Any(authorized => authorized.Id == property.Id)))));
    }
}
