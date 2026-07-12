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

        q = query.SortField switch
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
                _db.UnitOccupancyProjections.Count(occupancy =>
                    occupancy.PortfolioId == portfolioId && occupancy.PropertyId == p.Id && occupancy.IsOccupied)))
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

    public async Task<PropertyResponse?> GetAsync(int portfolioId, int id, CancellationToken ct = default)
    {
        var row = await _db.Properties
            .AsNoTracking()
            .Where(p => p.Id == id && p.PortfolioId == portfolioId)
            .Select(p => new ProjectedProperty(
                p,
                p.OwnerEntity != null ? p.OwnerEntity.Name : (p.Owner != null ? p.Owner.Name : null),
                p.Units.Count,
                _db.UnitOccupancyProjections.Count(occupancy =>
                    occupancy.PortfolioId == portfolioId && occupancy.PropertyId == p.Id && occupancy.IsOccupied)))
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
            var canonicalUnitResponse = await BuildCanonicalUnitResponseQuery(portfolioId, canonicalUnit.Id)
                .SingleAsync(ct);
            await _dataUpdate.BroadcastEntityUpdateAsync(
                portfolioId,
                UnitEntityType,
                canonicalUnit.Id,
                canonicalUnitResponse,
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

        if (request.Status == PropertyStatus.Inactive && entity.Status != PropertyStatus.Inactive)
        {
            var guard = await BuildPropertyDeletionGuardQuery(portfolioId, id).SingleAsync(ct);
            if (guard.HasOccupiedUnit)
            {
                throw new DomainValidationException(
                    "This property has an occupied unit. Return possession before marking the property inactive.",
                    statusCode: 409);
            }

            if (guard.HasPlannedOrCurrentRelationship)
            {
                throw new DomainValidationException(
                    "This property has a planned or current rental relationship. Cancel or complete it before marking the property inactive.",
                    statusCode: 409);
            }
        }

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
            .Select(p => new
            {
                UnitCount = p.Units.Count,
                Occupied = _db.UnitOccupancyProjections.Count(occupancy =>
                    occupancy.PortfolioId == portfolioId && occupancy.PropertyId == p.Id && occupancy.IsOccupied),
            })
            .FirstOrDefaultAsync(ct);

        var response = PropertyResponse.FromEntity(entity, counts?.UnitCount ?? 0, counts?.Occupied ?? 0);
        response.OwnerName = entity.OwnerEntity?.Name ?? entity.Owner?.Name;
        if (touchedCanonicalUnit != null)
        {
            var canonicalUnitResponse = await BuildCanonicalUnitResponseQuery(portfolioId, touchedCanonicalUnit.Id)
                .SingleAsync(ct);
            await _dataUpdate.BroadcastEntityUpdateAsync(
                portfolioId,
                UnitEntityType,
                touchedCanonicalUnit.Id,
                canonicalUnitResponse,
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

        // Block the soft-delete while the property still has live units or canonical rental history.
        // DeleteAsync only sets the property's own DeletedAt; a child unit keeps DeletedAt == null but
        // every unit read INNER-JOINs through the property, so it would persist live yet vanish from
        // every UI surface (and still hold its slot in the (PropertyId, UnitNumber) unique index).
        // Mirror the tenant relationship / vendor open-work-order delete guards: require the landlord to
        // clear the children first. The live-unit count and the consolidated history guard run SQL-side.
        // Unit has no independently trusted portfolio scope — the parent
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
        PortfolioId = property.PortfolioId,
        PropertyId = property.Id,
        UnitNumber = CanonicalUnitNumber(property.Name),
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
        var guard = await BuildPropertyDeletionGuardQuery(portfolioId, propertyId).SingleAsync(ct);

        if (guard.HasOccupiedUnit)
        {
            throw new DomainValidationException(
                "This property has an occupied unit. Return possession before deleting the property.",
                statusCode: 409);
        }

        if (guard.HasPlannedOrCurrentRelationship)
        {
            throw new DomainValidationException(
                "This property has a planned or current rental relationship. Cancel or complete it before deleting the property.",
                statusCode: 409);
        }

        if (guard.HasRentalRelationshipHistory)
        {
            throw new DomainValidationException(
                "This property has rental relationship, legal, or financial history and cannot be deleted.",
                statusCode: 409);
        }

        if (guard.HasWorkOrderHistory)
        {
            throw new DomainValidationException(
                "This property has work order history. Archive the work order history instead of deleting the property.",
                statusCode: 409);
        }

        if (guard.HasAppointmentHistory)
        {
            throw new DomainValidationException(
                "This property has appointment history. Archive the appointment history instead of deleting the property.",
                statusCode: 409);
        }

        if (guard.HasInspectionHistory)
        {
            throw new DomainValidationException(
                "This property has inspection history. Archive the inspection history instead of deleting the property.",
                statusCode: 409);
        }

        if (guard.HasExpenseHistory)
        {
            throw new DomainValidationException(
                "This property has expense history. Archive the expense history instead of deleting the property.",
                statusCode: 409);
        }

        if (guard.HasApplicationHistory)
        {
            throw new DomainValidationException(
                "This property has application history. Archive the applications instead of deleting the property.",
                statusCode: 409);
        }

        if (guard.HasRecurringExpenseHistory)
        {
            throw new DomainValidationException(
                "This property has recurring expense history. Archive the recurring expense history instead of deleting the property.",
                statusCode: 409);
        }

        if (guard.HasLoanHistory)
        {
            throw new DomainValidationException(
                "This property has loan history. Archive the loan history instead of deleting the property.",
                statusCode: 409);
        }

        if (guard.HasDocumentHistory)
        {
            throw new DomainValidationException(
                "This property has document history. Archive the documents instead of deleting the property.",
                statusCode: 409);
        }
    }

    private async Task EnsureUnitHasNoHistoryAsync(int portfolioId, int unitId, CancellationToken ct)
    {
        var guard = await BuildUnitDeletionGuardQuery(portfolioId, unitId).SingleAsync(ct);

        if (guard.IsOccupied)
        {
            throw new DomainValidationException(
                "This unit is occupied. Return possession before deleting the unit.",
                statusCode: 409);
        }

        if (guard.HasPlannedOrCurrentRelationship)
        {
            throw new DomainValidationException(
                "This unit has a planned or current rental relationship. Cancel or complete it before deleting the unit.",
                statusCode: 409);
        }

        if (guard.HasRentalRelationshipHistory)
        {
            throw new DomainValidationException(
                "This unit has rental relationship, legal, or financial history and cannot be deleted.",
                statusCode: 409);
        }

        if (guard.HasWorkOrderHistory)
        {
            throw new DomainValidationException(
                "This unit has work order history. Archive the work order history instead of deleting the unit.",
                statusCode: 409);
        }

        if (guard.HasAppointmentHistory)
        {
            throw new DomainValidationException(
                "This unit has appointment history. Archive the appointment history instead of deleting the unit.",
                statusCode: 409);
        }

        if (guard.HasInspectionHistory)
        {
            throw new DomainValidationException(
                "This unit has inspection history. Archive the inspection history instead of deleting the unit.",
                statusCode: 409);
        }

        if (guard.HasExpenseHistory)
        {
            throw new DomainValidationException(
                "This unit has expense history. Archive the expense history instead of deleting the unit.",
                statusCode: 409);
        }

        if (guard.HasApplicationHistory)
        {
            throw new DomainValidationException(
                "This unit has application history. Archive the applications instead of deleting the unit.",
                statusCode: 409);
        }

        if (guard.HasRecurringExpenseHistory)
        {
            throw new DomainValidationException(
                "This unit has recurring expense history. Archive the recurring expense history instead of deleting the unit.",
                statusCode: 409);
        }

        if (guard.HasDocumentHistory)
        {
            throw new DomainValidationException(
                "This unit has document history. Archive the documents instead of deleting the unit.",
                statusCode: 409);
        }
    }

    /// <summary>One translated SQL statement containing every Property delete decision.</summary>
    internal IQueryable<PropertyDeletionGuard> BuildPropertyDeletionGuardQuery(int portfolioId, int propertyId) =>
        _db.Properties
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(property => property.PortfolioId == portfolioId && property.Id == propertyId)
            .Select(property => new PropertyDeletionGuard
            {
                HasOccupiedUnit = _db.UnitOccupancyProjections.Any(occupancy =>
                    occupancy.PortfolioId == portfolioId
                    && occupancy.PropertyId == property.Id
                    && occupancy.IsOccupied),
                HasPlannedOrCurrentRelationship = _db.LeaseManagementLifecycleProjections.Any(lifecycle =>
                    lifecycle.PortfolioId == portfolioId
                    && lifecycle.PropertyId == property.Id
                    && lifecycle.Lifecycle != "Canceled"
                    && lifecycle.Lifecycle != "Closed"
                    && lifecycle.Lifecycle != "AccountingCloseout"),
                HasRentalRelationshipHistory = _db.LeaseManagements.Any(management =>
                    management.PortfolioId == portfolioId && management.PropertyId == property.Id),
                HasWorkOrderHistory = _db.WorkOrders.IgnoreQueryFilters().Any(workOrder =>
                    workOrder.PortfolioId == portfolioId && workOrder.PropertyId == property.Id),
                HasAppointmentHistory = _db.Appointments.IgnoreQueryFilters().Any(appointment =>
                    appointment.PortfolioId == portfolioId && appointment.PropertyId == property.Id),
                HasInspectionHistory = _db.Inspections.IgnoreQueryFilters().Any(inspection =>
                    inspection.PortfolioId == portfolioId && inspection.PropertyId == property.Id),
                HasExpenseHistory = _db.Expenses.IgnoreQueryFilters().Any(expense =>
                    expense.PortfolioId == portfolioId && expense.PropertyId == property.Id),
                HasApplicationHistory = _db.RentalApplications.IgnoreQueryFilters().Any(application =>
                    application.PortfolioId == portfolioId && application.PropertyId == property.Id),
                HasRecurringExpenseHistory = _db.RecurringExpenses.IgnoreQueryFilters().Any(expense =>
                    expense.PortfolioId == portfolioId && expense.PropertyId == property.Id),
                HasLoanHistory = _db.Loans.IgnoreQueryFilters().Any(loan =>
                    loan.PortfolioId == portfolioId && loan.PropertyId == property.Id),
                HasDocumentHistory = _db.StoredFiles.IgnoreQueryFilters().Any(file =>
                    file.PortfolioId == portfolioId
                    && file.EntityType == EntityType
                    && file.EntityId == property.Id),
            });

    /// <summary>One translated SQL statement containing every implicit canonical-Unit delete decision.</summary>
    internal IQueryable<UnitDeletionGuard> BuildUnitDeletionGuardQuery(int portfolioId, int unitId) =>
        _db.Units
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(unit => unit.PortfolioId == portfolioId && unit.Id == unitId)
            .Select(unit => new UnitDeletionGuard
            {
                IsOccupied = _db.UnitOccupancyProjections.Any(occupancy =>
                    occupancy.PortfolioId == portfolioId
                    && occupancy.UnitId == unit.Id
                    && occupancy.IsOccupied),
                HasPlannedOrCurrentRelationship = _db.LeaseManagementLifecycleProjections.Any(lifecycle =>
                    lifecycle.PortfolioId == portfolioId
                    && lifecycle.UnitId == unit.Id
                    && lifecycle.Lifecycle != "Canceled"
                    && lifecycle.Lifecycle != "Closed"
                    && lifecycle.Lifecycle != "AccountingCloseout"),
                HasRentalRelationshipHistory = _db.LeaseManagements.Any(management =>
                    management.PortfolioId == portfolioId && management.UnitId == unit.Id),
                HasWorkOrderHistory = _db.WorkOrders.IgnoreQueryFilters().Any(workOrder =>
                    workOrder.PortfolioId == portfolioId && workOrder.UnitId == unit.Id),
                HasAppointmentHistory = _db.Appointments.IgnoreQueryFilters().Any(appointment =>
                    appointment.PortfolioId == portfolioId && appointment.UnitId == unit.Id),
                HasInspectionHistory = _db.Inspections.IgnoreQueryFilters().Any(inspection =>
                    inspection.PortfolioId == portfolioId && inspection.UnitId == unit.Id),
                HasExpenseHistory = _db.Expenses.IgnoreQueryFilters().Any(expense =>
                    expense.PortfolioId == portfolioId && expense.UnitId == unit.Id),
                HasApplicationHistory = _db.RentalApplications.IgnoreQueryFilters().Any(application =>
                    application.PortfolioId == portfolioId && application.UnitId == unit.Id),
                HasRecurringExpenseHistory = _db.RecurringExpenses.IgnoreQueryFilters().Any(expense =>
                    expense.PortfolioId == portfolioId && expense.UnitId == unit.Id),
                HasDocumentHistory = _db.StoredFiles.IgnoreQueryFilters().Any(file =>
                    file.PortfolioId == portfolioId
                    && file.EntityType == UnitEntityType
                    && file.EntityId == unit.Id),
            });

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
            Bedrooms = unit.Bedrooms,
            Bathrooms = unit.Bathrooms,
            SquareFeet = unit.SquareFeet,
            MarketRent = unit.MarketRent,
            Status = occupancy.IsInTurnover || occupancy.IsOutOfService || occupancy.IsOnManagementHold
                ? UnitStatus.Offline
                : occupancy.IsOccupied
                    ? UnitStatus.Occupied
                    : occupancy.HasScheduledMoveIn
                        ? UnitStatus.Reserved
                        : UnitStatus.Vacant,
            Notes = unit.Notes,
            CreatedAt = unit.CreatedAt,
            UpdatedAt = unit.UpdatedAt,
        };

    internal sealed class PropertyDeletionGuard
    {
        public bool HasOccupiedUnit { get; init; }
        public bool HasPlannedOrCurrentRelationship { get; init; }
        public bool HasRentalRelationshipHistory { get; init; }
        public bool HasWorkOrderHistory { get; init; }
        public bool HasAppointmentHistory { get; init; }
        public bool HasInspectionHistory { get; init; }
        public bool HasExpenseHistory { get; init; }
        public bool HasApplicationHistory { get; init; }
        public bool HasRecurringExpenseHistory { get; init; }
        public bool HasLoanHistory { get; init; }
        public bool HasDocumentHistory { get; init; }
    }

    internal sealed class UnitDeletionGuard
    {
        public bool IsOccupied { get; init; }
        public bool HasPlannedOrCurrentRelationship { get; init; }
        public bool HasRentalRelationshipHistory { get; init; }
        public bool HasWorkOrderHistory { get; init; }
        public bool HasAppointmentHistory { get; init; }
        public bool HasInspectionHistory { get; init; }
        public bool HasExpenseHistory { get; init; }
        public bool HasApplicationHistory { get; init; }
        public bool HasRecurringExpenseHistory { get; init; }
        public bool HasDocumentHistory { get; init; }
    }
}
