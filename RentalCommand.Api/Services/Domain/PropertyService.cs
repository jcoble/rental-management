using Microsoft.EntityFrameworkCore;
using RentalCommand.Api.DTOs;
using RentalCommand.Core;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Interfaces;
using RentalCommand.Core.Time;
using RentalCommand.Data;

namespace RentalCommand.Api.Services.Domain;

/// <inheritdoc cref="IPropertyService"/>
public class PropertyService : IPropertyService
{
    private const string EntityType = "Property";
    private const string UnitEntityType = "Unit";
    private const int UnitNumberMaxLength = 50;

    private readonly RentalCommandDbContext _db;
    private readonly IDataUpdateService _dataUpdate;
    private readonly TimeProvider _timeProvider;

    public PropertyService(RentalCommandDbContext db, IDataUpdateService dataUpdate, TimeProvider timeProvider)
    {
        _db = db;
        _dataUpdate = dataUpdate;
        _timeProvider = timeProvider;
    }

    public async Task<IReadOnlyList<PropertyResponse>> ListAsync(int portfolioId, ListQuery query, CancellationToken ct = default)
    {
        var page = await ListPageAsync(portfolioId, ToPropertyListQuery(query), ct);
        return page.Items;
    }

    public async Task<PropertyListResponse> ListPageAsync(int portfolioId, PropertyListQuery query, CancellationToken ct = default)
    {
        var q = _db.Properties
            .AsNoTracking()
            .Where(p => p.PortfolioId == portfolioId);

        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var term = query.Search.Trim();
            q = q.Where(p =>
                EF.Functions.ILike(p.Name, $"%{term}%") ||
                EF.Functions.ILike(p.AddressLine1, $"%{term}%") ||
                EF.Functions.ILike(p.City, $"%{term}%") ||
                EF.Functions.ILike(p.State, $"%{term}%"));
        }

        if (query.Type.HasValue)
        {
            q = q.Where(p => p.PropertyType == query.Type.Value);
        }

        if (query.Status.HasValue)
        {
            q = q.Where(p => p.Status == query.Status.Value);
        }

        q = query.SortField switch
        {
            "name" => query.SortDescending ? q.OrderByDescending(p => p.Name) : q.OrderBy(p => p.Name),
            "city" => query.SortDescending ? q.OrderByDescending(p => p.City) : q.OrderBy(p => p.City),
            "type" => query.SortDescending ? q.OrderByDescending(p => p.PropertyType) : q.OrderBy(p => p.PropertyType),
            "status" => query.SortDescending ? q.OrderByDescending(p => p.Status) : q.OrderBy(p => p.Status),
            "unitcount" => query.SortDescending ? q.OrderByDescending(p => p.Units.Count) : q.OrderBy(p => p.Units.Count),
            "occupiedunits" => query.SortDescending ? q.OrderByDescending(p => p.Units.Count(u => u.Status == UnitStatus.Occupied)) : q.OrderBy(p => p.Units.Count(u => u.Status == UnitStatus.Occupied)),
            "updatedat" => query.SortDescending ? q.OrderByDescending(p => p.UpdatedAt) : q.OrderBy(p => p.UpdatedAt),
            _ => query.SortDescending ? q.OrderByDescending(p => p.CreatedAt) : q.OrderBy(p => p.CreatedAt),
        };

        var totalCount = await q.CountAsync(ct);

        // Unit / occupied counts are computed in SQL as correlated subqueries (p.Units.Count(...))
        // so the database does the aggregation — no Units collection is loaded into memory and
        // counted client-side, and there is no per-row follow-up query (N+1). EF translates each
        // count to a scalar subquery in the single list SELECT.
        var rows = await q
            .Select(p => new ProjectedProperty(
                p,
                p.OwnerEntity != null ? p.OwnerEntity.Name : (p.Owner != null ? p.Owner.Name : null),
                p.Units.Count,
                p.Units.Count(u => u.Status == UnitStatus.Occupied)))
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
    };

    public async Task<PropertyResponse?> GetAsync(int portfolioId, int id, CancellationToken ct = default)
    {
        var row = await _db.Properties
            .AsNoTracking()
            .Where(p => p.Id == id && p.PortfolioId == portfolioId)
            .Select(p => new ProjectedProperty(
                p,
                p.OwnerEntity != null ? p.OwnerEntity.Name : (p.Owner != null ? p.Owner.Name : null),
                p.Units.Count,
                p.Units.Count(u => u.Status == UnitStatus.Occupied)))
            .FirstOrDefaultAsync(ct);

        return row == null ? null : ToResponse(row);
    }

    /// <summary>
    /// Property plus its SQL-computed owner name and unit aggregates. Carrying the entity (rather than
    /// re-listing every scalar in the projection) keeps the read in one SELECT while letting
    /// <see cref="PropertyResponse.FromEntity"/> own the scalar mapping.
    /// </summary>
    private sealed record ProjectedProperty(Property Property, string? OwnerName, int UnitCount, int OccupiedUnits);

    private static PropertyResponse ToResponse(ProjectedProperty row)
    {
        var response = PropertyResponse.FromEntity(row.Property, row.UnitCount, row.OccupiedUnits);
        response.OwnerName = row.OwnerName;
        return response;
    }

    public async Task<PropertyResponse?> CreateAsync(int portfolioId, CreatePropertyRequest request, CancellationToken ct = default)
    {
        // Verify any supplied owner/owner-entity references belong to the caller's portfolio (no cross-tenant linking).
        if (request.OwnerId.HasValue &&
            !await _db.EnsureOwnerInPortfolioAsync(portfolioId, request.OwnerId.Value, ct))
        {
            return null;
        }

        if (request.OwnerEntityId.HasValue &&
            !await _db.EnsureOwnerEntityInPortfolioAsync(portfolioId, request.OwnerEntityId.Value, ct))
        {
            return null;
        }

        // When no owner is supplied, link the property to the portfolio's primary (self) owner so it shows up
        // in the owners report. If the portfolio has no primary owner yet, leave it null rather than failing.
        var ownerEntityId = request.ClearOwnerEntity ? null : request.OwnerEntityId;
        if (!ownerEntityId.HasValue && !request.ClearOwnerEntity)
        {
            ownerEntityId = await _db.OwnerEntities
                .Where(oe => oe.PortfolioId == portfolioId && oe.IsPrimary)
                .Select(oe => (int?)oe.Id)
                .FirstOrDefaultAsync(ct);
        }

        var now = _timeProvider.UtcNow();
        var entity = new Property
        {
            PortfolioId = portfolioId,
            OwnerId = request.OwnerId,
            OwnerEntityId = ownerEntityId,
            Name = request.Name,
            PropertyType = request.PropertyType,
            Status = request.Status,
            AddressLine1 = request.AddressLine1,
            AddressLine2 = request.AddressLine2,
            City = request.City,
            State = request.State,
            PostalCode = request.PostalCode,
            YearBuilt = request.YearBuilt,
            ManagementFeePercent = request.ManagementFeePercent,
            Notes = request.Notes,
            PurchasePrice = request.PurchasePrice,
            LandValue = request.LandValue,
            InServiceDate = request.InServiceDate.ToUtc(),
            ManualAnnualDepreciation = request.ManualAnnualDepreciation,
            CreatedAt = now,
            UpdatedAt = now,
        };

        Unit? canonicalUnit = null;
        if (IsPropertyUnitType(entity.PropertyType))
        {
            canonicalUnit = NewCanonicalUnit(entity, now);
            _db.Units.Add(canonicalUnit);
        }

        _db.Properties.Add(entity);
        await _db.SaveChangesAsync(ct);

        var response = PropertyResponse.FromEntity(entity, canonicalUnit == null ? 0 : 1, 0);
        response.OwnerName = entity.OwnerEntity?.Name ?? entity.Owner?.Name;
        if (canonicalUnit != null)
        {
            await _dataUpdate.BroadcastEntityUpdateAsync(
                portfolioId,
                UnitEntityType,
                canonicalUnit.Id,
                UnitResponse.FromEntity(canonicalUnit),
                ct);
        }
        await _dataUpdate.BroadcastEntityUpdateAsync(portfolioId, EntityType, entity.Id, response, ct);
        return response;
    }

    public async Task<PropertyResponse?> UpdateAsync(int portfolioId, int id, UpdatePropertyRequest request, CancellationToken ct = default)
    {
        var entity = await _db.Properties
            .Include(p => p.Owner)
            .Include(p => p.OwnerEntity)
            .FirstOrDefaultAsync(p => p.Id == id && p.PortfolioId == portfolioId, ct);
        if (entity == null)
        {
            return null;
        }

        // Verify any supplied owner/owner-entity references belong to the caller's portfolio (no cross-tenant linking).
        if (request.OwnerId.HasValue &&
            !await _db.EnsureOwnerInPortfolioAsync(portfolioId, request.OwnerId.Value, ct))
        {
            return null;
        }

        if (request.OwnerEntityId.HasValue &&
            !await _db.EnsureOwnerEntityInPortfolioAsync(portfolioId, request.OwnerEntityId.Value, ct))
        {
            return null;
        }

        var previousCanonicalUnitNumber = CanonicalUnitNumber(entity.Name);
        var now = _timeProvider.UtcNow();

        if (request.OwnerId.HasValue) entity.OwnerId = request.OwnerId;
        if (request.ClearOwnerEntity)
        {
            entity.OwnerEntityId = null;
        }
        else if (request.OwnerEntityId.HasValue)
        {
            entity.OwnerEntityId = request.OwnerEntityId;
        }
        if (request.Name != null) entity.Name = request.Name;
        if (request.PropertyType.HasValue) entity.PropertyType = request.PropertyType.Value;
        if (request.Status.HasValue) entity.Status = request.Status.Value;
        if (request.AddressLine1 != null) entity.AddressLine1 = request.AddressLine1;
        if (request.AddressLine2 != null) entity.AddressLine2 = request.AddressLine2;
        if (request.City != null) entity.City = request.City;
        if (request.State != null) entity.State = request.State;
        if (request.PostalCode != null) entity.PostalCode = request.PostalCode;
        if (request.YearBuilt.HasValue) entity.YearBuilt = request.YearBuilt;
        if (request.ManagementFeePercent.HasValue) entity.ManagementFeePercent = request.ManagementFeePercent;
        if (request.Notes != null) entity.Notes = request.Notes;
        if (request.PurchasePrice.HasValue) entity.PurchasePrice = request.PurchasePrice;
        if (request.LandValue.HasValue) entity.LandValue = request.LandValue;
        if (request.InServiceDate.HasValue) entity.InServiceDate = request.InServiceDate.ToUtc();
        if (request.ManualAnnualDepreciation.HasValue) entity.ManualAnnualDepreciation = request.ManualAnnualDepreciation;
        entity.UpdatedAt = now;

        var touchedCanonicalUnit = await EnsureCanonicalUnitAsync(
            entity,
            previousCanonicalUnitNumber,
            now,
            ct);
        await _db.SaveChangesAsync(ct);

        // Re-read the unit aggregates in SQL (single scalar query) so the broadcast row carries the
        // same counts the list/detail show — the edit doesn't change unit membership, but keeping the
        // shape consistent avoids the grid flashing 0s on a live update.
        var counts = await _db.Properties
            .AsNoTracking()
            .Where(p => p.Id == id && p.PortfolioId == portfolioId)
            .Select(p => new { UnitCount = p.Units.Count, Occupied = p.Units.Count(u => u.Status == UnitStatus.Occupied) })
            .FirstOrDefaultAsync(ct);

        var response = PropertyResponse.FromEntity(entity, counts?.UnitCount ?? 0, counts?.Occupied ?? 0);
        response.OwnerName = entity.OwnerEntity?.Name ?? entity.Owner?.Name;
        if (touchedCanonicalUnit != null)
        {
            await _dataUpdate.BroadcastEntityUpdateAsync(
                portfolioId,
                UnitEntityType,
                touchedCanonicalUnit.Id,
                UnitResponse.FromEntity(touchedCanonicalUnit),
                ct);
        }
        await _dataUpdate.BroadcastEntityUpdateAsync(portfolioId, EntityType, entity.Id, response, ct);
        return response;
    }

    public async Task<bool> DeleteAsync(int portfolioId, int id, CancellationToken ct = default)
    {
        var entity = await _db.Properties
            .FirstOrDefaultAsync(p => p.Id == id && p.PortfolioId == portfolioId, ct);
        if (entity == null)
        {
            return false;
        }

        // Block the soft-delete while the property still has live units or an occupying lease.
        // DeleteAsync only sets the property's own DeletedAt; a child unit keeps DeletedAt == null but
        // every unit read INNER-JOINs through the property, so it would persist live yet vanish from
        // every UI surface (and still hold its slot in the (PropertyId, UnitNumber) unique index).
        // Mirror the tenant active-lease / vendor open-work-order delete guards: require the landlord to
        // clear the children first. Both checks run SQL-side (COUNT / EXISTS); the global query filters
        // already exclude soft-deleted units and leases. Unit has no PortfolioId of its own — the parent
        // property was already confirmed in-portfolio above, so PropertyId == id is correctly scoped.
        var liveUnitCount = await _db.Units
            .CountAsync(u => u.PropertyId == id, ct);
        Unit? canonicalUnitToDelete = null;
        if (liveUnitCount > 0)
        {
            if (liveUnitCount == 1 && IsPropertyUnitType(entity.PropertyType))
            {
                var unit = await _db.Units
                    .FirstAsync(u => u.PropertyId == id, ct);
                if (string.Equals(unit.UnitNumber, CanonicalUnitNumber(entity.Name), StringComparison.Ordinal))
                {
                    await EnsureUnitHasNoHistoryAsync(portfolioId, unit.Id, ct);
                    canonicalUnitToDelete = unit;
                }
            }

            if (canonicalUnitToDelete == null)
            {
                var unitNoun = liveUnitCount == 1 ? "unit" : "units";
                throw new DomainValidationException(
                    $"This property still has {liveUnitCount} {unitNoun}. Remove the {unitNoun} before deleting this property.",
                    statusCode: 409);
            }
        }

        // Safety net for the orphan edge case: an occupying lease whose unit was already soft-deleted
        // slips past the unit check above. NoticeGiven still occupies its unit (it is treated as the
        // current lease elsewhere), so block on it too — matching the tenant delete guard.
        var hasOccupyingLease = await _db.Leases
            .AnyAsync(l => l.PropertyId == id
                && l.PortfolioId == portfolioId
                && (l.Status == LeaseStatus.Active || l.Status == LeaseStatus.NoticeGiven), ct);
        if (hasOccupyingLease)
        {
            throw new DomainValidationException(
                "This property has an active lease; end or reassign it first.",
                statusCode: 409);
        }

        await EnsurePropertyHasNoHistoryAsync(portfolioId, id, ct);

        var now = _timeProvider.UtcNow();
        if (canonicalUnitToDelete != null)
        {
            canonicalUnitToDelete.DeletedAt = now;
            canonicalUnitToDelete.UpdatedAt = now;
        }
        entity.DeletedAt = now;
        entity.UpdatedAt = now;
        await _db.SaveChangesAsync(ct);

        if (canonicalUnitToDelete != null)
        {
            await _dataUpdate.BroadcastEntityDeleteAsync(portfolioId, UnitEntityType, canonicalUnitToDelete.Id, ct);
        }
        await _dataUpdate.BroadcastEntityDeleteAsync(portfolioId, EntityType, id, ct);
        return true;
    }

    private async Task<Unit?> EnsureCanonicalUnitAsync(
        Property property,
        string previousCanonicalUnitNumber,
        DateTime now,
        CancellationToken ct)
    {
        if (!IsPropertyUnitType(property.PropertyType))
        {
            return null;
        }

        var liveUnits = await _db.Units
            .Where(u => u.PropertyId == property.Id)
            .OrderBy(u => u.Id)
            .Take(2)
            .ToListAsync(ct);

        if (liveUnits.Count == 0)
        {
            var created = NewCanonicalUnit(property, now);
            _db.Units.Add(created);
            return created;
        }

        if (liveUnits.Count != 1)
        {
            return null;
        }

        var unit = liveUnits[0];
        if (!string.Equals(unit.UnitNumber, previousCanonicalUnitNumber, StringComparison.Ordinal))
        {
            return null;
        }

        var nextUnitNumber = CanonicalUnitNumber(property.Name);
        if (string.Equals(unit.UnitNumber, nextUnitNumber, StringComparison.Ordinal))
        {
            return null;
        }

        unit.UnitNumber = nextUnitNumber;
        unit.UpdatedAt = now;
        return unit;
    }

    private static Unit NewCanonicalUnit(Property property, DateTime now) => new()
    {
        Property = property,
        PropertyId = property.Id,
        UnitNumber = CanonicalUnitNumber(property.Name),
        Status = UnitStatus.Vacant,
        CreatedAt = now,
        UpdatedAt = now,
    };

    private static bool IsPropertyUnitType(PropertyType type) =>
        type is PropertyType.SingleFamily or PropertyType.Condo or PropertyType.Townhome;

    private static string CanonicalUnitNumber(string propertyName)
    {
        var value = propertyName.Trim();
        if (value.Length == 0)
        {
            return "Property";
        }

        return value.Length <= UnitNumberMaxLength ? value : value[..UnitNumberMaxLength];
    }

    private async Task EnsurePropertyHasNoHistoryAsync(int portfolioId, int propertyId, CancellationToken ct)
    {
        if (await _db.Leases
            .IgnoreQueryFilters()
            .AsNoTracking()
            .AnyAsync(l => l.PortfolioId == portfolioId && l.PropertyId == propertyId, ct))
        {
            throw new DomainValidationException(
                "This property has lease history. Archive or end the lease history instead of deleting the property.",
                statusCode: 409);
        }

        if (await _db.WorkOrders
            .IgnoreQueryFilters()
            .AsNoTracking()
            .AnyAsync(w => w.PortfolioId == portfolioId && w.PropertyId == propertyId, ct))
        {
            throw new DomainValidationException(
                "This property has work order history. Archive the work order history instead of deleting the property.",
                statusCode: 409);
        }

        if (await _db.Appointments
            .IgnoreQueryFilters()
            .AsNoTracking()
            .AnyAsync(a => a.PortfolioId == portfolioId && a.PropertyId == propertyId, ct))
        {
            throw new DomainValidationException(
                "This property has appointment history. Archive the appointment history instead of deleting the property.",
                statusCode: 409);
        }

        if (await _db.Inspections
            .IgnoreQueryFilters()
            .AsNoTracking()
            .AnyAsync(i => i.PortfolioId == portfolioId && i.PropertyId == propertyId, ct))
        {
            throw new DomainValidationException(
                "This property has inspection history. Archive the inspection history instead of deleting the property.",
                statusCode: 409);
        }

        if (await _db.Expenses
            .IgnoreQueryFilters()
            .AsNoTracking()
            .AnyAsync(e => e.PortfolioId == portfolioId && e.PropertyId == propertyId, ct))
        {
            throw new DomainValidationException(
                "This property has expense history. Archive the expense history instead of deleting the property.",
                statusCode: 409);
        }

        if (await _db.RentalApplications
            .IgnoreQueryFilters()
            .AsNoTracking()
            .AnyAsync(a => a.PortfolioId == portfolioId && a.PropertyId == propertyId, ct))
        {
            throw new DomainValidationException(
                "This property has application history. Archive the applications instead of deleting the property.",
                statusCode: 409);
        }

        if (await _db.RecurringExpenses
            .IgnoreQueryFilters()
            .AsNoTracking()
            .AnyAsync(e => e.PortfolioId == portfolioId && e.PropertyId == propertyId, ct))
        {
            throw new DomainValidationException(
                "This property has recurring expense history. Archive the recurring expense history instead of deleting the property.",
                statusCode: 409);
        }

        if (await _db.Loans
            .IgnoreQueryFilters()
            .AsNoTracking()
            .AnyAsync(l => l.PortfolioId == portfolioId && l.PropertyId == propertyId, ct))
        {
            throw new DomainValidationException(
                "This property has loan history. Archive the loan history instead of deleting the property.",
                statusCode: 409);
        }

        if (await _db.StoredFiles
            .IgnoreQueryFilters()
            .AsNoTracking()
            .AnyAsync(f => f.PortfolioId == portfolioId
                && f.EntityType == EntityType
                && f.EntityId == propertyId, ct))
        {
            throw new DomainValidationException(
                "This property has document history. Archive the documents instead of deleting the property.",
                statusCode: 409);
        }
    }

    private async Task EnsureUnitHasNoHistoryAsync(int portfolioId, int unitId, CancellationToken ct)
    {
        if (await _db.Leases
            .IgnoreQueryFilters()
            .AsNoTracking()
            .AnyAsync(l => l.PortfolioId == portfolioId && l.UnitId == unitId, ct))
        {
            throw new DomainValidationException(
                "This unit has lease history. Archive or end the lease history instead of deleting the unit.",
                statusCode: 409);
        }

        if (await _db.WorkOrders
            .IgnoreQueryFilters()
            .AsNoTracking()
            .AnyAsync(w => w.PortfolioId == portfolioId && w.UnitId == unitId, ct))
        {
            throw new DomainValidationException(
                "This unit has work order history. Archive the work order history instead of deleting the unit.",
                statusCode: 409);
        }

        if (await _db.Appointments
            .IgnoreQueryFilters()
            .AsNoTracking()
            .AnyAsync(a => a.PortfolioId == portfolioId && a.UnitId == unitId, ct))
        {
            throw new DomainValidationException(
                "This unit has appointment history. Archive the appointment history instead of deleting the unit.",
                statusCode: 409);
        }

        if (await _db.Inspections
            .IgnoreQueryFilters()
            .AsNoTracking()
            .AnyAsync(i => i.PortfolioId == portfolioId && i.UnitId == unitId, ct))
        {
            throw new DomainValidationException(
                "This unit has inspection history. Archive the inspection history instead of deleting the unit.",
                statusCode: 409);
        }

        if (await _db.Expenses
            .IgnoreQueryFilters()
            .AsNoTracking()
            .AnyAsync(e => e.PortfolioId == portfolioId && e.UnitId == unitId, ct))
        {
            throw new DomainValidationException(
                "This unit has expense history. Archive the expense history instead of deleting the unit.",
                statusCode: 409);
        }

        if (await _db.RentalApplications
            .IgnoreQueryFilters()
            .AsNoTracking()
            .AnyAsync(a => a.PortfolioId == portfolioId && a.UnitId == unitId, ct))
        {
            throw new DomainValidationException(
                "This unit has application history. Archive the applications instead of deleting the unit.",
                statusCode: 409);
        }

        if (await _db.RecurringExpenses
            .IgnoreQueryFilters()
            .AsNoTracking()
            .AnyAsync(e => e.PortfolioId == portfolioId && e.UnitId == unitId, ct))
        {
            throw new DomainValidationException(
                "This unit has recurring expense history. Archive the recurring expense history instead of deleting the unit.",
                statusCode: 409);
        }

        if (await _db.StoredFiles
            .IgnoreQueryFilters()
            .AsNoTracking()
            .AnyAsync(f => f.PortfolioId == portfolioId
                && f.EntityType == UnitEntityType
                && f.EntityId == unitId, ct))
        {
            throw new DomainValidationException(
                "This unit has document history. Archive the documents instead of deleting the unit.",
                statusCode: 409);
        }
    }
}
