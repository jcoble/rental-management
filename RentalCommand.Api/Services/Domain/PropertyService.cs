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
    private readonly IAtomicUnitOfWork? _atomic;
    private readonly TimeProvider _timeProvider;

    public PropertyService(
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

    public async Task<PropertyResponse?> UpdateAsync(
        WorkspaceReadScope scope,
        int id,
        UpdatePropertyRequest request,
        string operationKey,
        CancellationToken ct = default)
    {
        var command = AtomicCoreCrudMutation.Command(scope, AtomicCoreCrudMutationDomain.Property,
            AtomicCoreCrudMutationOperation.Update, id, operationKey, request);
        var outcome = await Atomic.ExecuteAsync(
            AtomicCoreCrudMutation.Identity(command), command, AtomicCoreCrudMutation.Codec, ct);
        return DeserializeSnapshot<PropertyResponse>(outcome.Value);
    }

    public async Task<PropertySetupResponse?> SetupAsync(
        WorkspaceReadScope scope,
        SetupPropertyRequest request,
        string operationKey,
        CancellationToken ct = default)
    {
        var setupAtUtc = _timeProvider.UtcNow();
        var command = AtomicCoreCrudMutation.Command(scope, AtomicCoreCrudMutationDomain.Property,
            AtomicCoreCrudMutationOperation.Setup, request.PropertyId.GetValueOrDefault(), operationKey, request,
            createdAtUtc: setupAtUtc,
            changedAtUtc: setupAtUtc);
        var outcome = await Atomic.ExecuteAsync(
            AtomicCoreCrudMutation.Identity(command), command, AtomicCoreCrudMutation.Codec, ct);
        return DeserializeSnapshot<PropertySetupResponse>(outcome.Value);
    }

    public async Task<bool> DeleteAsync(
        WorkspaceReadScope scope,
        int id,
        string operationKey,
        CancellationToken ct = default)
    {
        var command = AtomicCoreCrudMutation.Command(scope, AtomicCoreCrudMutationDomain.Property,
            AtomicCoreCrudMutationOperation.Delete, id, operationKey, new object());
        var outcome = await Atomic.ExecuteAsync(
            AtomicCoreCrudMutation.Identity(command), command, AtomicCoreCrudMutation.Codec, ct);
        return outcome.Value.Found;
    }

    private IAtomicUnitOfWork Atomic => _atomic ?? throw new InvalidOperationException(
        "Scoped property mutations require the atomic persistence kernel.");

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
