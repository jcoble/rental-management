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

/// <inheritdoc cref="IPropertyService"/>
public class PropertyService : IPropertyService
{
    private const string EntityType = "Property";
    private const string UnitEntityType = "Unit";

    private readonly RentalCommandDbContext _db;
    private readonly IDataUpdateService _dataUpdate;
    private readonly IWriteExecutor _writes;
    private readonly PropertyTenantCrudRule _crudRules;
    private readonly TimeProvider _timeProvider;

    public PropertyService(
        RentalCommandDbContext db,
        IDataUpdateService dataUpdate,
        TimeProvider timeProvider,
        IWriteExecutor writes)
    {
        _db = db;
        _dataUpdate = dataUpdate;
        _timeProvider = timeProvider;
        _writes = writes;
        _crudRules = new PropertyTenantCrudRule(db);
    }

    public async Task<PropertyResponse?> UpdateAsync(
        WorkspaceReadScope scope,
        int id,
        UpdatePropertyRequest request,
        string operationKey,
        CancellationToken ct = default)
    {
        var changedAtUtc = _timeProvider.UtcNow();
        var command = CoreCrudWriteSupport.Request(scope, AtomicCoreCrudMutationDomain.Property,
            AtomicCoreCrudMutationOperation.Update, id, operationKey, request,
            changedAtUtc: changedAtUtc);
        var write = CoreCrudWriteSupport.Write(
            command, _crudRules.UpdatePropertyAsync, _crudRules.AuthorizeReplayAsync);
        var outcome = await _writes.ExecuteAsync(
            CoreCrudWriteSupport.IdempotencyKey(command), write, ct);
        return DeserializeSnapshot<PropertyResponse>(outcome.Value);
    }

    public async Task<PropertySetupResponse?> SetupAsync(
        WorkspaceReadScope scope,
        SetupPropertyRequest request,
        string operationKey,
        CancellationToken ct = default)
    {
        var setupAtUtc = _timeProvider.UtcNow();
        var command = CoreCrudWriteSupport.Request(scope, AtomicCoreCrudMutationDomain.Property,
            AtomicCoreCrudMutationOperation.Setup, request.PropertyId.GetValueOrDefault(), operationKey, request,
            createdAtUtc: setupAtUtc,
            changedAtUtc: setupAtUtc);
        var write = CoreCrudWriteSupport.Write(
            command, _crudRules.SetupPropertyAsync, _crudRules.AuthorizeReplayAsync);
        var outcome = await _writes.ExecuteAsync(
            CoreCrudWriteSupport.IdempotencyKey(command), write, ct);
        return DeserializeSnapshot<PropertySetupResponse>(outcome.Value);
    }

    public async Task<bool> DeleteAsync(
        WorkspaceReadScope scope,
        int id,
        string operationKey,
        CancellationToken ct = default)
    {
        var command = CoreCrudWriteSupport.Request(scope, AtomicCoreCrudMutationDomain.Property,
            AtomicCoreCrudMutationOperation.Delete, id, operationKey, new object());
        var write = CoreCrudWriteSupport.Write(
            command, _crudRules.DeletePropertyAsync, _crudRules.AuthorizeReplayAsync);
        var outcome = await _writes.ExecuteAsync(
            CoreCrudWriteSupport.IdempotencyKey(command), write, ct);
        return outcome.Value.Found;
    }

    private static TResponse? DeserializeSnapshot<TResponse>(AtomicCoreCrudMutationResult result)
        where TResponse : class =>
        result.Found && result.ResponseJson is not null
            ? JsonSerializer.Deserialize<TResponse>(result.ResponseJson)
            : null;

    public async Task<IReadOnlyList<PropertyResponse>> ListAsync(
        WorkspaceReadScope scope, ListQuery query, CancellationToken ct = default)
    {
        var page = await ListPageAsync(scope, ToPropertyListQuery(query), ct);
        return page.Items;
    }

    public async Task<PropertyListResponse> ListPageAsync(
        WorkspaceReadScope scope, PropertyListQuery query, CancellationToken ct = default)
    {
        var portfolioId = scope.PortfolioId;
        var now = _timeProvider.GetUtcNow().UtcDateTime;
        var q = _db.Properties
            .AsNoTracking()
            .WhereAuthorized(
                _db,
                scope,
                CapabilityKeys.RentalsRead,
                now);

        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var term = query.Search.Trim();
            q = q.Where(p =>
                EF.Functions.ILike(p.Name, $"%{term}%") ||
                EF.Functions.ILike(p.AddressLine1, $"%{term}%") ||
                EF.Functions.ILike(p.City, $"%{term}%") ||
                EF.Functions.ILike(p.State, $"%{term}%") ||
                EF.Functions.ILike(p.PostalCode, $"%{term}%") ||
                p.Ownerships.Any(ownership =>
                    ownership.EffectiveFromUtc <= now
                    && (ownership.EffectiveToUtc == null || ownership.EffectiveToUtc > now)
                    && ownership.OwnerEntity != null
                    && EF.Functions.ILike(ownership.OwnerEntity.Name, $"%{term}%")));
        }

        if (query.Type.HasValue)
        {
            q = q.Where(p => p.PropertyType == query.Type.Value);
        }

        if (query.Status.HasValue)
        {
            q = q.Where(p => p.Status == query.Status.Value);
        }

        if (query.AvailableForLease == true)
        {
            q = q.Where(property => _db.UnitOccupancyProjections.Any(occupancy =>
                occupancy.PortfolioId == portfolioId
                && occupancy.PropertyId == property.Id
                && !occupancy.IsOccupied
                && !occupancy.HasScheduledMoveIn
                && !occupancy.IsInTurnover
                && !occupancy.IsOutOfService
                && !occupancy.IsOnManagementHold));
        }

        var totalCount = await q.CountAsync(ct);

        IOrderedQueryable<Property> ordered = query.SortField switch
        {
            "name" => query.SortDescending ? q.OrderByDescending(p => p.Name) : q.OrderBy(p => p.Name),
            "city" => query.SortDescending ? q.OrderByDescending(p => p.City) : q.OrderBy(p => p.City),
            "type" => query.SortDescending ? q.OrderByDescending(p => p.PropertyType) : q.OrderBy(p => p.PropertyType),
            "status" => query.SortDescending ? q.OrderByDescending(p => p.Status) : q.OrderBy(p => p.Status),
            "unitcount" => query.SortDescending ? q.OrderByDescending(p => p.Units.Count) : q.OrderBy(p => p.Units.Count),
            "occupiedunits" => query.SortDescending
                ? q.OrderByDescending(p => _db.UnitOccupancyProjections.Count(occupancy =>
                    occupancy.PortfolioId == portfolioId && occupancy.PropertyId == p.Id && occupancy.IsOccupied))
                : q.OrderBy(p => _db.UnitOccupancyProjections.Count(occupancy =>
                    occupancy.PortfolioId == portfolioId && occupancy.PropertyId == p.Id && occupancy.IsOccupied)),
            "updatedat" => query.SortDescending ? q.OrderByDescending(p => p.UpdatedAt) : q.OrderBy(p => p.UpdatedAt),
            _ => query.SortDescending ? q.OrderByDescending(p => p.CreatedAt) : q.OrderBy(p => p.CreatedAt),
        };

        // Unit / occupied counts are computed in SQL as correlated subqueries (p.Units.Count(...))
        // so the database does the aggregation — no Units collection is loaded into memory and
        // counted client-side, and there is no per-row follow-up query (N+1). EF translates each
        // count to a scalar subquery in the single list SELECT.
        var rows = await ordered
            .ThenBy(p => p.Id)
            .Select(p => new ProjectedProperty(
                p,
                p.Ownerships
                    .Where(ownership => ownership.EffectiveFromUtc <= now
                        && (ownership.EffectiveToUtc == null || ownership.EffectiveToUtc > now))
                    .OrderBy(ownership => ownership.OwnerEntityId)
                    .Select(ownership => new PropertyOwnershipResponse
                    {
                        Id = ownership.Id,
                        OwnerEntityId = ownership.OwnerEntityId,
                        OwnerName = ownership.OwnerEntity!.Name,
                        OwnershipSharePercent = ownership.OwnershipSharePercent,
                        EffectiveFromUtc = ownership.EffectiveFromUtc,
                        EffectiveToUtc = ownership.EffectiveToUtc,
                        StatementRecipientName = ownership.StatementRecipientName,
                        StatementRecipientEmail = ownership.StatementRecipientEmail,
                        PayeeName = ownership.PayeeName,
                    }).ToList(),
                p.Units.Count,
                _db.UnitOccupancyProjections.Count(occupancy =>
                    occupancy.PortfolioId == portfolioId && occupancy.PropertyId == p.Id && occupancy.IsOccupied),
                p.RentalStructure == RentalStructure.SingleRental
                    ? p.Units.OrderBy(unit => unit.Id).Select(unit => (int?)unit.Id).FirstOrDefault()
                    : null))
            .Skip(query.NormalizedSkip)
            .Take(query.NormalizedTake)
            .ToListAsync(ct);

        return new PropertyListResponse
        {
            Items = rows.Select(r => ToResponse(r)).ToList(),
            TotalCount = totalCount,
            Skip = query.NormalizedSkip,
            Take = query.NormalizedTake,
        };
    }

    private static PropertyListQuery ToPropertyListQuery(ListQuery query) => new()
    {
        Skip = query.Skip,
        Take = query.Take,
        Search = query.Search,
        Sort = query.Sort,
        Type = (query as PropertyListQuery)?.Type,
        Status = (query as PropertyListQuery)?.Status,
        AvailableForLease = (query as PropertyListQuery)?.AvailableForLease,
    };

    public async Task<PropertyResponse?> GetAsync(WorkspaceReadScope scope, int id, CancellationToken ct = default)
    {
        var portfolioId = scope.PortfolioId;
        var now = _timeProvider.UtcNow();
        var row = await _db.Properties
            .AsNoTracking()
            .WhereAuthorized(_db, scope, CapabilityKeys.RentalsRead, now)
            .Where(p => p.Id == id)
            .Select(p => new ProjectedProperty(
                p,
                p.Ownerships
                    .Where(ownership => ownership.EffectiveFromUtc <= now
                        && (ownership.EffectiveToUtc == null || ownership.EffectiveToUtc > now))
                    .OrderBy(ownership => ownership.OwnerEntityId)
                    .Select(ownership => new PropertyOwnershipResponse
                    {
                        Id = ownership.Id,
                        OwnerEntityId = ownership.OwnerEntityId,
                        OwnerName = ownership.OwnerEntity!.Name,
                        OwnershipSharePercent = ownership.OwnershipSharePercent,
                        EffectiveFromUtc = ownership.EffectiveFromUtc,
                        EffectiveToUtc = ownership.EffectiveToUtc,
                        StatementRecipientName = ownership.StatementRecipientName,
                        StatementRecipientEmail = ownership.StatementRecipientEmail,
                        PayeeName = ownership.PayeeName,
                    }).ToList(),
                p.Units.Count,
                _db.UnitOccupancyProjections.Count(occupancy =>
                    occupancy.PortfolioId == portfolioId && occupancy.PropertyId == p.Id && occupancy.IsOccupied),
                p.RentalStructure == RentalStructure.SingleRental
                    ? p.Units.OrderBy(unit => unit.Id).Select(unit => (int?)unit.Id).FirstOrDefault()
                    : null))
            .FirstOrDefaultAsync(ct);

        return row == null ? null : ToResponse(row);
    }

    /// <summary>
    /// Property plus its SQL-computed owner name and unit aggregates. Carrying the entity (rather than
    /// re-listing every scalar in the projection) keeps the read in one SELECT while letting
    /// <see cref="PropertyResponse.FromEntity"/> own the scalar mapping.
    /// </summary>
    private sealed record ProjectedProperty(
        Property Property,
        IReadOnlyList<PropertyOwnershipResponse> Ownerships,
        int UnitCount,
        int OccupiedUnits,
        int? SingleRentalUnitId);

    private static PropertyResponse ToResponse(ProjectedProperty row)
    {
        var response = PropertyResponse.FromEntity(row.Property, row.UnitCount, row.OccupiedUnits);
        response.Ownerships = row.Ownerships;
        response.WorkspaceEntry = PropertyWorkspaceEntryResponse.FromPersistedStructure(
            row.Property.RentalStructure,
            row.Property.Id,
            row.SingleRentalUnitId);
        return response;
    }

    internal IQueryable<UnitResponse> BuildCanonicalUnitResponseQuery(int portfolioId, int unitId) =>
        from unit in _db.Units.AsNoTracking()
        where unit.PortfolioId == portfolioId && unit.Id == unitId
        join occupancy in _db.UnitOccupancyProjections.AsNoTracking()
            on new { unit.PortfolioId, UnitId = unit.Id }
            equals new { occupancy.PortfolioId, occupancy.UnitId }
        select new UnitResponse
        {
            Id = unit.Id,
            PropertyId = unit.PropertyId,
            UnitNumber = unit.UnitNumber,
            FloorPlan = unit.FloorPlan,
            PropertyType = _db.Properties
                .Where(property => property.Id == unit.PropertyId)
                .Select(property => property.PropertyType)
                .FirstOrDefault(),
            Bedrooms = unit.Bedrooms,
            Bathrooms = unit.Bathrooms,
            SquareFeet = unit.SquareFeet,
            MarketRent = unit.MarketRent,
            Status = occupancy.IsInTurnover || occupancy.IsOutOfService || occupancy.IsOnManagementHold
                ? DerivedUnitStatus.Offline
                : occupancy.IsOccupied
                    ? DerivedUnitStatus.Occupied
                    : occupancy.HasScheduledMoveIn
                        ? DerivedUnitStatus.Reserved
                        : DerivedUnitStatus.Vacant,
            Notes = unit.Notes,
            CreatedAt = unit.CreatedAt,
            UpdatedAt = unit.UpdatedAt,
        };

}
