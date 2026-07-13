using Microsoft.EntityFrameworkCore;
using RentalCommand.Api.DTOs;
using RentalCommand.Core;
using RentalCommand.Core.Authorization;
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
    private readonly TimeProvider _timeProvider;

    public OwnerEntityService(RentalCommandDbContext db, IDataUpdateService dataUpdate, TimeProvider timeProvider)
    {
        _db = db;
        _dataUpdate = dataUpdate;
        _timeProvider = timeProvider;
    }

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

    public Task<OwnerEntityResponse?> CreateAsync(
        WorkspaceReadScope scope,
        CreateOwnerEntityRequest request,
        CancellationToken ct = default) =>
        _db.ExecuteAuthorizedMutationAsync(async token =>
    {
        var portfolioId = scope.PortfolioId;
        if (!await _db.AuthorizedWorkspaceAssignments(
                scope,
                [CapabilityKeys.RentalsManage],
                CapabilityAuthorizationTargetKind.Property,
                _timeProvider.UtcNow()).AnyAsync(token))
        {
            return null;
        }

        var now = _timeProvider.UtcNow();
        var entity = new OwnerEntity
        {
            PortfolioId = portfolioId,
            OwnerEntityType = request.OwnerEntityType,
            Name = request.Name,
            TaxId = request.TaxId,
            AddressLine1 = request.AddressLine1,
            AddressLine2 = request.AddressLine2,
            City = request.City,
            State = request.State,
            PostalCode = request.PostalCode,
            // Keep the legacy single-line Address in sync (composed from the structured fields,
            // falling back to any single-line Address the caller still sends).
            Address = AddressComposer.Compose(request.AddressLine1, request.AddressLine2, request.City, request.State, request.PostalCode)
                      ?? request.Address,
            Phone = request.Phone,
            Email = request.Email,
            CreatedAt = now,
            UpdatedAt = now,
        };

        _db.OwnerEntities.Add(entity);
        await _db.SaveChangesAsync(token);

        var response = await GetProjectedAsync(scope, entity.Id, CapabilityKeys.RentalsManage, token)
            ?? OwnerEntityResponse.FromEntity(entity);
        await _dataUpdate.BroadcastEntityUpdateAsync(portfolioId, EntityType, entity.Id, response, token);
        return response;
    }, ct);

    public Task<OwnerEntityResponse?> UpdateAsync(
        WorkspaceReadScope scope,
        int id,
        UpdateOwnerEntityRequest request,
        CancellationToken ct = default) =>
        _db.ExecuteAuthorizedMutationAsync(async token =>
    {
        var portfolioId = scope.PortfolioId;
        var entity = await AuthorizedOwnersForMutation(scope, CapabilityKeys.RentalsManage)
            .FirstOrDefaultAsync(o => o.Id == id, token);
        if (entity == null)
        {
            return null;
        }

        if (request.OwnerEntityType.HasValue) entity.OwnerEntityType = request.OwnerEntityType.Value;
        if (request.Name != null) entity.Name = request.Name;
        if (request.TaxId != null) entity.TaxId = request.TaxId;
        if (request.AddressLine1 != null) entity.AddressLine1 = request.AddressLine1;
        if (request.AddressLine2 != null) entity.AddressLine2 = request.AddressLine2;
        if (request.City != null) entity.City = request.City;
        if (request.State != null) entity.State = request.State;
        if (request.PostalCode != null) entity.PostalCode = request.PostalCode;
        // Re-compose the legacy single-line Address from the (possibly updated) structured fields;
        // fall back to an explicitly-sent Address only when no structured parts exist.
        entity.Address = AddressComposer.Compose(entity.AddressLine1, entity.AddressLine2, entity.City, entity.State, entity.PostalCode)
                         ?? (request.Address ?? entity.Address);
        if (request.Phone != null) entity.Phone = request.Phone;
        if (request.Email != null) entity.Email = request.Email;
        entity.UpdatedAt = _timeProvider.UtcNow();

        await _db.SaveChangesAsync(token);

        var response = await GetProjectedAsync(scope, entity.Id, CapabilityKeys.RentalsManage, token)
            ?? OwnerEntityResponse.FromEntity(entity);
        await _dataUpdate.BroadcastEntityUpdateAsync(portfolioId, EntityType, entity.Id, response, token);
        return response;
    }, ct);

    public Task<bool> DeleteAsync(
        WorkspaceReadScope scope,
        int id,
        DeleteOwnerEntityOptions? options = null,
        CancellationToken ct = default) =>
        _db.ExecuteAuthorizedMutationAsync(async token =>
    {
        var portfolioId = scope.PortfolioId;
        var entity = await AuthorizedOwnersForMutation(scope, CapabilityKeys.RentalsManage)
            .FirstOrDefaultAsync(o => o.Id == id, token);
        if (entity == null)
        {
            return false;
        }

        var propertyCount = await _db.Properties
            .AsNoTracking()
            .CountAsync(p => p.PortfolioId == portfolioId && p.OwnerEntityId == id, token);
        if (propertyCount > 0)
        {
            if (options?.ClearPropertyAssignments != true)
            {
                var propertyNoun = propertyCount == 1 ? "property" : "properties";
                var targetNoun = propertyCount == 1 ? "that property" : "those properties";
                throw new DomainValidationException(
                    $"This owner is assigned to {propertyCount} {propertyNoun}. Please reassign {targetNoun} or clear the owner before deleting this owner.",
                    statusCode: 409);
            }

            var now = _timeProvider.UtcNow();
            await _db.Properties
                .Where(p => p.PortfolioId == portfolioId && p.OwnerEntityId == id)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(p => p.OwnerEntityId, (int?)null)
                    .SetProperty(p => p.UpdatedAt, now), token);
        }

        var distributionCount = await _db.OwnerDistributions
            .AsNoTracking()
            .CountAsync(d => d.PortfolioId == portfolioId && d.OwnerEntityId == id, token);
        if (distributionCount > 0)
        {
            var distributionNoun = distributionCount == 1 ? "distribution" : "distributions";
            throw new DomainValidationException(
                $"This owner has {distributionCount} recorded {distributionNoun}. Delete or reassign those owner distributions before deleting this owner.",
                statusCode: 409);
        }

        entity.DeletedAt = _timeProvider.UtcNow();
        entity.UpdatedAt = entity.DeletedAt.Value;
        await _db.SaveChangesAsync(token);

        await _dataUpdate.BroadcastEntityDeleteAsync(portfolioId, EntityType, id, token);
        return true;
    }, ct);

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
