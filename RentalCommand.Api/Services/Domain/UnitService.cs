using System.Text.Json;
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

/// <inheritdoc cref="IUnitService"/>
public class UnitService : IUnitService
{
    private const string EntityType = "Unit";

    private readonly RentalCommandDbContext _db;
    private readonly IDataUpdateService _dataUpdate;
    private readonly IAuditTrailService _audit;
    private readonly TimeProvider _timeProvider;

    public UnitService(RentalCommandDbContext db, IDataUpdateService dataUpdate, IAuditTrailService audit, TimeProvider timeProvider)
    {
        _db = db;
        _dataUpdate = dataUpdate;
        _audit = audit;
        _timeProvider = timeProvider;
    }

    public async Task<IReadOnlyList<UnitResponse>> ListAsync(
        WorkspaceReadScope scope, int? propertyId, ListQuery query, CancellationToken ct = default)
    {
        var portfolioId = scope.PortfolioId;
        var q = BuildCanonicalResponseQuery(scope);

        if (propertyId.HasValue)
        {
            q = q.Where(unit => unit.PropertyId == propertyId.Value);
        }

        if (query is UnitListQuery { AvailableForLease: true })
        {
            q = q.Where(unit => _db.UnitOccupancyProjections.Any(occupancy =>
                occupancy.PortfolioId == portfolioId
                && occupancy.UnitId == unit.Id
                && !occupancy.IsOccupied
                && !occupancy.HasScheduledMoveIn
                && !occupancy.IsInTurnover
                && !occupancy.IsOutOfService
                && !occupancy.IsOnManagementHold));
        }

        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var term = query.Search.Trim();
            q = q.Where(unit =>
                EF.Functions.ILike(unit.UnitNumber, $"%{term}%") ||
                (unit.FloorPlan != null && EF.Functions.ILike(unit.FloorPlan, $"%{term}%")));
        }

        q = query.SortField switch
        {
            "unitnumber" => query.SortDescending ? q.OrderByDescending(unit => unit.UnitNumber) : q.OrderBy(unit => unit.UnitNumber),
            "marketrent" => query.SortDescending ? q.OrderByDescending(unit => unit.MarketRent) : q.OrderBy(unit => unit.MarketRent),
            "status" => query.SortDescending ? q.OrderByDescending(unit => unit.Status) : q.OrderBy(unit => unit.Status),
            "updatedat" => query.SortDescending ? q.OrderByDescending(unit => unit.UpdatedAt) : q.OrderBy(unit => unit.UpdatedAt),
            _ => query.SortDescending ? q.OrderByDescending(unit => unit.CreatedAt) : q.OrderBy(unit => unit.CreatedAt),
        };

        return await q
            .Skip(query.NormalizedSkip)
            .Take(query.NormalizedTake)
            .ToListAsync(ct);
    }

    /// <summary>
    /// Basic Unit wire rows with presentation status derived from the canonical occupancy projection.
    /// The mutable legacy Unit.Status column is intentionally absent from this query.
    /// </summary>
    internal IQueryable<UnitResponse> BuildCanonicalResponseQuery(int portfolioId) =>
        from unit in _db.Units.AsNoTracking()
        where unit.PortfolioId == portfolioId
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

    internal IQueryable<UnitResponse> BuildCanonicalResponseQuery(WorkspaceReadScope scope)
    {
        var portfolioId = scope.PortfolioId;
        var authorizedProperties = _db.Properties
            .AsNoTracking()
            .WhereAuthorized(
                _db,
                scope,
                CapabilityKeys.RentalsRead,
                _timeProvider.GetUtcNow().UtcDateTime);

        return from unit in _db.Units.AsNoTracking()
            where unit.PortfolioId == portfolioId
                  && authorizedProperties.Any(property =>
                      property.Id == unit.PropertyId && property.PortfolioId == unit.PortfolioId)
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
    }

    public async Task<IReadOnlyList<UnitHealthResponse>> ListWithHealthAsync(
        WorkspaceReadScope scope, int? propertyId, ListQuery query, CancellationToken ct = default)
    {
        var page = await ListWithHealthPageAsync(scope, ToUnitHealthListQuery(query, propertyId), ct);
        return page.Items;
    }

    public async Task<UnitHealthListResponse> ListWithHealthPageAsync(
        WorkspaceReadScope scope, UnitHealthListQuery query, CancellationToken ct = default)
    {
        var portfolioId = scope.PortfolioId;
        var authorizedProperties = _db.Properties
            .AsNoTracking()
            .WhereAuthorized(
                _db,
                scope,
                CapabilityKeys.RentalsRead,
                _timeProvider.GetUtcNow().UtcDateTime);
        var q = BuildHealthQuery(portfolioId)
            .Where(row => authorizedProperties.Any(property =>
                property.Id == row.PropertyId && property.PortfolioId == portfolioId));

        if (query.PropertyId.HasValue)
        {
            q = q.Where(row => row.PropertyId == query.PropertyId.Value);
        }

        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var term = query.Search.Trim();
            q = q.Where(row =>
                EF.Functions.ILike(row.UnitNumber, $"%{term}%")
                || EF.Functions.ILike(row.PropertyName, $"%{term}%"));
        }

        if (!string.IsNullOrWhiteSpace(query.Status) &&
            Enum.TryParse<DerivedUnitStatus>(query.Status, ignoreCase: true, out var status))
        {
            q = status switch
            {
                DerivedUnitStatus.Occupied => q.Where(row => row.IsOccupied
                    && !row.IsInTurnover
                    && !row.IsOutOfService
                    && !row.IsOnManagementHold),
                DerivedUnitStatus.Reserved => q.Where(row => !row.IsOccupied
                    && row.HasScheduledMoveIn
                    && !row.IsInTurnover
                    && !row.IsOutOfService
                    && !row.IsOnManagementHold),
                DerivedUnitStatus.Offline => q.Where(row => row.IsInTurnover || row.IsOutOfService || row.IsOnManagementHold),
                _ => q.Where(row => !row.IsOccupied
                    && !row.HasScheduledMoveIn
                    && !row.IsInTurnover
                    && !row.IsOutOfService
                    && !row.IsOnManagementHold),
            };
        }

        q = ApplyStageFilter(q, query.Stage);

        q = query.SortField switch
        {
            "unitnumber" => query.SortDescending ? q.OrderByDescending(row => row.UnitNumber) : q.OrderBy(row => row.UnitNumber),
            "propertyname" => query.SortDescending ? q.OrderByDescending(row => row.PropertyName) : q.OrderBy(row => row.PropertyName),
            "openworkordercount" => query.SortDescending ? q.OrderByDescending(row => row.OpenWorkOrderCount) : q.OrderBy(row => row.OpenWorkOrderCount),
            "marketrent" => query.SortDescending ? q.OrderByDescending(row => row.MarketRent) : q.OrderBy(row => row.MarketRent),
            "status" => query.SortDescending
                ? q.OrderByDescending(row => row.IsOutOfService || row.IsInTurnover || row.IsOnManagementHold ? 3 : row.IsOccupied ? 2 : row.HasScheduledMoveIn ? 1 : 0)
                : q.OrderBy(row => row.IsOutOfService || row.IsInTurnover || row.IsOnManagementHold ? 3 : row.IsOccupied ? 2 : row.HasScheduledMoveIn ? 1 : 0),
            "updatedat" => query.SortDescending ? q.OrderByDescending(row => row.UpdatedAt) : q.OrderBy(row => row.UpdatedAt),
            _ => query.SortDescending ? q.OrderByDescending(row => row.UnitNumber) : q.OrderBy(row => row.UnitNumber),
        };

        var totalCount = await q.CountAsync(ct);

        var rows = await q
            .Skip(query.NormalizedSkip)
            .Take(query.NormalizedTake)
            .ToListAsync(ct);

        return new UnitHealthListResponse
        {
            Items = rows.Select(r => new UnitHealthResponse
            {
                Id = r.Id,
                PropertyId = r.PropertyId,
                PropertyName = r.PropertyName,
                UnitNumber = r.UnitNumber,
                Status = ResolveUnitStatus(r).ToString(),
                CurrentLeaseManagementId = r.CurrentLeaseManagementId,
                CurrentAgreementId = r.CurrentAgreementId,
                TenantAccountId = r.TenantAccountId,
                MarketRent = r.MarketRent,
                OpenWorkOrderCount = r.OpenWorkOrderCount,
                LeaseEndsInDays = r.CurrentAgreementEndOn is { } end && r.BusinessDate is { } businessDate
                    ? Math.Max(0, end.DayNumber - businessDate.DayNumber)
                    : null,
                DocsNeedingReviewCount = r.DocsCount,
                SimpleStage = ComputeSimpleStage(r),
            }).ToList(),
            TotalCount = totalCount,
            Skip = query.NormalizedSkip,
            Take = query.NormalizedTake,
        };
    }

    /// <summary>
    /// Canonical, composable Unit Command Center list query. Keeping this query visible to tests lets
    /// us inspect the PostgreSQL generated SQL without executing or materializing any domain rows.
    /// </summary>
    internal IQueryable<UnitHealthReadRow> BuildHealthQuery(int portfolioId)
    {
        // Occupancy, lifecycle, and the governing agreement are database projections over the
        // canonical LeaseManagement graph. This query deliberately does not consult Unit.Status,
        // Unit.Leases, Lease.Status, or LeaseTenants: those legacy columns cannot be allowed to
        // disagree with possession and effective-dated agreement facts.
        return
            from unit in _db.Units.AsNoTracking()
            where unit.PortfolioId == portfolioId
            join property in _db.Properties.AsNoTracking()
                on new { unit.PortfolioId, Id = unit.PropertyId }
                equals new { property.PortfolioId, property.Id }
            join occupancy in _db.UnitOccupancyProjections.AsNoTracking()
                on new { unit.PortfolioId, UnitId = unit.Id }
                equals new { occupancy.PortfolioId, occupancy.UnitId }
            from lifecycle in _db.LeaseManagementLifecycleProjections.AsNoTracking()
                .Where(row => row.PortfolioId == unit.PortfolioId
                    && row.LeaseManagementId == occupancy.CurrentLeaseManagementId)
                .DefaultIfEmpty()
            from agreement in _db.LeaseAgreements.AsNoTracking()
                .Where(row => row.PortfolioId == unit.PortfolioId
                    && row.Id == lifecycle!.CurrentAgreementId)
                .DefaultIfEmpty()
            select new UnitHealthReadRow
            {
                Id = unit.Id,
                PropertyId = unit.PropertyId,
                PropertyName = property.Name,
                UnitNumber = unit.UnitNumber,
                MarketRent = unit.MarketRent,
                UpdatedAt = unit.UpdatedAt,
                IsOccupied = occupancy.IsOccupied,
                HasScheduledMoveIn = occupancy.HasScheduledMoveIn,
                IsInTurnover = occupancy.IsInTurnover,
                IsOutOfService = occupancy.IsOutOfService,
                IsOnManagementHold = occupancy.IsOnManagementHold,
                CurrentLeaseManagementId = occupancy.CurrentLeaseManagementId,
                CurrentAgreementId = lifecycle == null ? null : lifecycle.CurrentAgreementId,
                TenantAccountId = lifecycle == null ? null : lifecycle.TenantAccountId,
                Lifecycle = lifecycle == null ? null : lifecycle.Lifecycle,
                BusinessDate = lifecycle == null ? null : lifecycle.BusinessDate,
                CurrentAgreementEndOn = agreement == null ? null : agreement.TermEndOn,
                OpenWorkOrderCount = unit.WorkOrders.Count(workOrder =>
                    workOrder.Status != WorkOrderStatus.Completed
                    && workOrder.Status != WorkOrderStatus.Cancelled
                    && workOrder.Status != WorkOrderStatus.Archived),
                DocsCount = _db.StoredFiles.Count(file =>
                    file.PortfolioId == portfolioId
                    && (
                        _db.LegalDocumentArtifacts.Any(artifact =>
                            artifact.PortfolioId == portfolioId
                            && artifact.StoredFileId == file.Id
                            && _db.LeaseAgreements.Any(legalAgreement =>
                                legalAgreement.PortfolioId == portfolioId
                                && (legalAgreement.IssuedArtifactId == artifact.Id
                                    || legalAgreement.ExecutedArtifactId == artifact.Id)
                                && _db.LeaseManagements.Any(management =>
                                    management.PortfolioId == portfolioId
                                    && management.UnitId == unit.Id
                                    && management.Id == legalAgreement.LeaseManagementId)))
                        || (file.EntityId != null && (
                            (file.EntityType == "Unit" && file.EntityId == unit.Id)
                            || (file.EntityType == "Expense" && _db.Expenses.Any(expense =>
                                expense.PortfolioId == portfolioId
                                && expense.Id == file.EntityId.Value
                                && (expense.UnitId == unit.Id
                                    || (expense.WorkOrderId != null && _db.WorkOrders.Any(workOrder =>
                                        workOrder.PortfolioId == portfolioId
                                        && workOrder.UnitId == unit.Id
                                        && workOrder.Id == expense.WorkOrderId.Value)))))
                            || (file.EntityType == "WorkOrder" && _db.WorkOrders.Any(workOrder =>
                                workOrder.PortfolioId == portfolioId
                                && workOrder.UnitId == unit.Id
                                && workOrder.Id == file.EntityId.Value))
                            || (file.EntityType == "Inspection" && _db.Inspections.Any(inspection =>
                                inspection.PortfolioId == portfolioId
                                && inspection.UnitId == unit.Id
                                && inspection.Id == file.EntityId.Value)))))),
            };
    }

    private static UnitHealthListQuery ToUnitHealthListQuery(ListQuery query, int? propertyId) => new()
    {
        Skip = query.Skip,
        Take = query.Take,
        Search = query.Search,
        Sort = query.Sort,
        PropertyId = propertyId,
    };

    private static IQueryable<UnitHealthReadRow> ApplyStageFilter(IQueryable<UnitHealthReadRow> q, string? stage)
    {
        if (string.IsNullOrWhiteSpace(stage))
        {
            return q;
        }

        var normalized = stage.Replace("-", string.Empty, StringComparison.Ordinal).Trim().ToLowerInvariant();
        return normalized switch
        {
            "turnover" => q.Where(row => row.IsInTurnover || row.IsOutOfService || row.IsOnManagementHold),
            "moveout" => q.Where(row => row.IsOccupied
                && row.Lifecycle == "Ending"
                && !row.IsInTurnover && !row.IsOutOfService && !row.IsOnManagementHold),
            "renewal" => q.Where(row => row.IsOccupied
                && row.Lifecycle == "Occupied"
                && !row.IsInTurnover && !row.IsOutOfService && !row.IsOnManagementHold
                && row.BusinessDate != null
                && row.CurrentAgreementEndOn != null
                && row.CurrentAgreementEndOn >= row.BusinessDate
                && row.CurrentAgreementEndOn <= row.BusinessDate.Value.AddDays(90)),
            "active" => q.Where(row => row.IsOccupied
                && row.Lifecycle == "Occupied"
                && !row.IsInTurnover && !row.IsOutOfService && !row.IsOnManagementHold
                && (row.CurrentAgreementEndOn == null
                    || row.BusinessDate == null
                    || row.CurrentAgreementEndOn < row.BusinessDate
                    || row.CurrentAgreementEndOn > row.BusinessDate.Value.AddDays(90))),
            "lease" => q.Where(row => !row.IsOccupied
                && (row.HasScheduledMoveIn || row.Lifecycle == "Upcoming" || row.Lifecycle == "Preparing")),
            "vacant" => q.Where(row => !row.IsOccupied
                && !row.HasScheduledMoveIn
                && !row.IsInTurnover
                && !row.IsOutOfService
                && !row.IsOnManagementHold),
            _ => q,
        };
    }

    /// <summary>
    /// Simplified list badge (NOT the full 9-stage detail derivation): a cheap label from the unit's
    /// occupancy status + current-lease status, formatted from already-projected scalars (no extra query).
    /// </summary>
    private static string ComputeSimpleStage(UnitHealthReadRow row)
    {
        if (row.IsInTurnover || row.IsOutOfService || row.IsOnManagementHold)
        {
            return "Turnover";
        }

        if (row.IsOccupied && row.Lifecycle == "Ending")
        {
            return "Move-Out";
        }

        if (row.IsOccupied && row.Lifecycle == "Occupied")
        {
            return row.CurrentAgreementEndOn is { } end
                && row.BusinessDate is { } businessDate
                && end >= businessDate
                && end <= businessDate.AddDays(90)
                ? "Renewal"
                : "Active";
        }

        return row.HasScheduledMoveIn || row.Lifecycle is "Upcoming" or "Preparing"
            ? "Lease"
            : "Vacant";
    }

    private static DerivedUnitStatus ResolveUnitStatus(UnitHealthReadRow row) =>
        row.IsInTurnover || row.IsOutOfService || row.IsOnManagementHold
            ? DerivedUnitStatus.Offline
            : row.IsOccupied
                ? DerivedUnitStatus.Occupied
                : row.HasScheduledMoveIn
                    ? DerivedUnitStatus.Reserved
                    : DerivedUnitStatus.Vacant;

    internal sealed class UnitHealthReadRow
    {
        public int Id { get; init; }
        public int PropertyId { get; init; }
        public string PropertyName { get; init; } = string.Empty;
        public string UnitNumber { get; init; } = string.Empty;
        public decimal MarketRent { get; init; }
        public DateTime UpdatedAt { get; init; }
        public bool IsOccupied { get; init; }
        public bool HasScheduledMoveIn { get; init; }
        public bool IsInTurnover { get; init; }
        public bool IsOutOfService { get; init; }
        public bool IsOnManagementHold { get; init; }
        public int? CurrentLeaseManagementId { get; init; }
        public int? CurrentAgreementId { get; init; }
        public int? TenantAccountId { get; init; }
        public string? Lifecycle { get; init; }
        public DateOnly? BusinessDate { get; init; }
        public DateOnly? CurrentAgreementEndOn { get; init; }
        public int OpenWorkOrderCount { get; init; }
        public int DocsCount { get; init; }
    }

    public async Task<UnitResponse?> GetAsync(int portfolioId, int id, CancellationToken ct = default)
    {
        return await BuildCanonicalResponseQuery(portfolioId)
            .FirstOrDefaultAsync(unit => unit.Id == id, ct);
    }

    public async Task<UnitResponse?> CreateAsync(int portfolioId, CreateUnitRequest request, CancellationToken ct = default)
    {
        _audit.EnsureAtomicCommand();
        // Verify the target property exists within the caller's portfolio before attaching the unit.
        var propertyInScope = await _db.Properties
            .AnyAsync(p => p.Id == request.PropertyId && p.PortfolioId == portfolioId, ct);
        if (!propertyInScope)
        {
            return null;
        }

        // Reject a duplicate unit number up front with a clear, field-specific message instead of letting
        // it hit the (PropertyId, UnitNumber) unique index and surface as the generic "conflicts with
        // existing data" 409. Only LIVE units collide (the global query filter excludes soft-deleted
        // rows); evaluated SQL-side as an EXISTS.
        if (await _db.Units.AnyAsync(u => u.PropertyId == request.PropertyId && u.UnitNumber == request.UnitNumber, ct))
        {
            throw new DomainValidationException(
                $"Unit number \"{request.UnitNumber}\" already exists on this property.",
                StatusCodes.Status409Conflict);
        }

        var now = _timeProvider.UtcNow();
        var entity = new Unit
        {
            PortfolioId = portfolioId,
            PropertyId = request.PropertyId,
            UnitNumber = request.UnitNumber,
            FloorPlan = request.FloorPlan,
            Bedrooms = request.Bedrooms,
            Bathrooms = request.Bathrooms,
            SquareFeet = request.SquareFeet,
            MarketRent = request.MarketRent,
            Notes = request.Notes,
            CreatedAt = now,
            UpdatedAt = now,
        };

        _db.Units.Add(entity);
        await _db.SaveChangesAsync(ct);

        await _audit.LogAsync(
            portfolioId,
            EntityType,
            entity.Id,
            AuditLogOperation.Created,
            newValues: Snapshot(entity),
            changeReason: $"Unit {entity.UnitNumber} created",
            ct: ct);

        var response = await GetAsync(portfolioId, entity.Id, ct)
            ?? throw new InvalidOperationException("The newly created unit is missing from canonical occupancy.");
        await _dataUpdate.BroadcastEntityUpdateAsync(portfolioId, EntityType, entity.Id, response, ct);
        return response;
    }

    public async Task<UnitResponse?> UpdateAsync(int portfolioId, int id, UpdateUnitRequest request, CancellationToken ct = default)
    {
        _audit.EnsureAtomicCommand();
        var entity = await _db.Units
            .FirstOrDefaultAsync(u => u.Id == id && u.Property != null && u.Property.PortfolioId == portfolioId, ct);
        if (entity == null)
        {
            return null;
        }

        // Same duplicate-number guard as create, scoped to a rename: only check when the number is
        // actually changing, and exclude this unit's own row. Keeps the clear 409 message instead of the
        // opaque unique-index conflict. Only LIVE units collide (the global query filter excludes
        // soft-deleted rows).
        if (request.UnitNumber is not null
            && !string.Equals(request.UnitNumber, entity.UnitNumber, StringComparison.Ordinal)
            && await _db.Units.AnyAsync(u =>
                u.PropertyId == entity.PropertyId
                && u.UnitNumber == request.UnitNumber
                && u.Id != entity.Id, ct))
        {
            throw new DomainValidationException(
                $"Unit number \"{request.UnitNumber}\" already exists on this property.",
                StatusCodes.Status409Conflict);
        }

        var oldValues = new Dictionary<string, object?>();
        var newValues = new Dictionary<string, object?>();

        ApplyStringIfChanged(request.UnitNumber, entity.UnitNumber, "UnitNumber", v => entity.UnitNumber = v);
        ApplyStringIfChanged(request.FloorPlan, entity.FloorPlan, "FloorPlan", v => entity.FloorPlan = v);
        ApplyValueIfChanged(request.Bedrooms, entity.Bedrooms, "Bedrooms", v => entity.Bedrooms = v);
        ApplyValueIfChanged(request.Bathrooms, entity.Bathrooms, "Bathrooms", v => entity.Bathrooms = v);
        ApplyNullableValueIfChanged(request.SquareFeet, entity.SquareFeet, "SquareFeet", v => entity.SquareFeet = v);
        ApplyValueIfChanged(request.MarketRent, entity.MarketRent, "MarketRent", v => entity.MarketRent = v);
        ApplyStringIfChanged(request.Notes, entity.Notes, "Notes", v => entity.Notes = v);
        entity.UpdatedAt = _timeProvider.UtcNow();

        await _db.SaveChangesAsync(ct);

        if (oldValues.Count > 0)
        {
            await _audit.LogAsync(
                portfolioId,
                EntityType,
                entity.Id,
                AuditLogOperation.Updated,
                oldValues: Serialize(oldValues),
                newValues: Serialize(newValues),
                changeReason: $"Unit {entity.UnitNumber} updated",
                ct: ct);
        }

        var response = await GetAsync(portfolioId, entity.Id, ct)
            ?? throw new InvalidOperationException("The updated unit is missing from canonical occupancy.");
        await _dataUpdate.BroadcastEntityUpdateAsync(portfolioId, EntityType, entity.Id, response, ct);
        return response;

        void ApplyStringIfChanged(string? requested, string? current, string field, Action<string> apply)
        {
            if (requested is null || string.Equals(requested, current, StringComparison.Ordinal))
            {
                return;
            }

            oldValues[field] = current;
            newValues[field] = requested;
            apply(requested);
        }

        void ApplyValueIfChanged<T>(T? requested, T current, string field, Action<T> apply)
            where T : struct
        {
            if (!requested.HasValue || EqualityComparer<T>.Default.Equals(requested.Value, current))
            {
                return;
            }

            oldValues[field] = current;
            newValues[field] = requested.Value;
            apply(requested.Value);
        }

        void ApplyNullableValueIfChanged<T>(T? requested, T? current, string field, Action<T?> apply)
            where T : struct
        {
            if (!requested.HasValue || EqualityComparer<T?>.Default.Equals(requested, current))
            {
                return;
            }

            oldValues[field] = current;
            newValues[field] = requested.Value;
            apply(requested.Value);
        }
    }

    public async Task<bool> DeleteAsync(int portfolioId, int id, CancellationToken ct = default)
    {
        _audit.EnsureAtomicCommand();
        var entity = await _db.Units
            .FirstOrDefaultAsync(u => u.Id == id && u.Property != null && u.Property.PortfolioId == portfolioId, ct);
        if (entity == null)
        {
            return false;
        }

        await EnsureUnitHasNoHistoryAsync(portfolioId, id, ct);

        entity.DeletedAt = _timeProvider.UtcNow();
        await _db.SaveChangesAsync(ct);

        await _audit.LogAsync(
            portfolioId,
            EntityType,
            id,
            AuditLogOperation.Deleted,
            oldValues: Snapshot(entity),
            changeReason: $"Unit {entity.UnitNumber} deleted",
            ct: ct);

        await _dataUpdate.BroadcastEntityDeleteAsync(portfolioId, EntityType, id, ct);
        return true;
    }

    private async Task EnsureUnitHasNoHistoryAsync(int portfolioId, int unitId, CancellationToken ct)
    {
        var guard = await BuildDeletionGuardQuery(portfolioId, unitId).SingleAsync(ct);

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

    /// <summary>One translated SQL statement containing every Unit delete decision.</summary>
    internal IQueryable<UnitDeletionGuard> BuildDeletionGuardQuery(int portfolioId, int unitId) =>
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
                    && file.EntityType == EntityType
                    && file.EntityId == unit.Id),
            });

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

    private static string Snapshot(Unit entity) => Serialize(new Dictionary<string, object?>
    {
        ["PropertyId"] = entity.PropertyId,
        ["UnitNumber"] = entity.UnitNumber,
        ["FloorPlan"] = entity.FloorPlan,
        ["Bedrooms"] = entity.Bedrooms,
        ["Bathrooms"] = entity.Bathrooms,
        ["SquareFeet"] = entity.SquareFeet,
        ["MarketRent"] = entity.MarketRent,
        ["Notes"] = entity.Notes,
    });

    private static string Serialize(Dictionary<string, object?> values) => JsonSerializer.Serialize(values);
}
