using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using RentalCommand.Api.DTOs;
using RentalCommand.Core;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Interfaces;
using RentalCommand.Core.Time;
using RentalCommand.Data;

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

    public async Task<IReadOnlyList<UnitResponse>> ListAsync(int portfolioId, int? propertyId, ListQuery query, CancellationToken ct = default)
    {
        // Scope through the owning Property's portfolio; Unit has no PortfolioId of its own.
        var q = _db.Units
            .AsNoTracking()
            .Where(u => u.Property != null && u.Property.PortfolioId == portfolioId);

        if (propertyId.HasValue)
        {
            q = q.Where(u => u.PropertyId == propertyId.Value);
        }

        if (query is UnitListQuery { AvailableForLease: true })
        {
            q = q.Where(u => _db.UnitOccupancyProjections.Any(occupancy =>
                occupancy.PortfolioId == portfolioId
                && occupancy.UnitId == u.Id
                && !occupancy.IsOccupied
                && !occupancy.HasScheduledMoveIn
                && !occupancy.IsInTurnover
                && !occupancy.IsOutOfService
                && !occupancy.IsOnManagementHold));
        }

        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var term = query.Search.Trim();
            q = q.Where(u =>
                EF.Functions.ILike(u.UnitNumber, $"%{term}%") ||
                (u.FloorPlan != null && EF.Functions.ILike(u.FloorPlan, $"%{term}%")));
        }

        q = query.SortField switch
        {
            "unitnumber" => query.SortDescending ? q.OrderByDescending(u => u.UnitNumber) : q.OrderBy(u => u.UnitNumber),
            "marketrent" => query.SortDescending ? q.OrderByDescending(u => u.MarketRent) : q.OrderBy(u => u.MarketRent),
            "status" => query.SortDescending ? q.OrderByDescending(u => u.Status) : q.OrderBy(u => u.Status),
            "updatedat" => query.SortDescending ? q.OrderByDescending(u => u.UpdatedAt) : q.OrderBy(u => u.UpdatedAt),
            _ => query.SortDescending ? q.OrderByDescending(u => u.CreatedAt) : q.OrderBy(u => u.CreatedAt),
        };

        var items = await q
            .Skip(query.NormalizedSkip)
            .Take(query.NormalizedTake)
            .ToListAsync(ct);

        return items.Select(UnitResponse.FromEntity).ToList();
    }

    public async Task<IReadOnlyList<UnitHealthResponse>> ListWithHealthAsync(
        int portfolioId, int? propertyId, ListQuery query, CancellationToken ct = default)
    {
        var page = await ListWithHealthPageAsync(portfolioId, ToUnitHealthListQuery(query, propertyId), ct);
        return page.Items;
    }

    public async Task<UnitHealthListResponse> ListWithHealthPageAsync(
        int portfolioId, UnitHealthListQuery query, CancellationToken ct = default)
    {
        var q = BuildHealthQuery(portfolioId);

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
            Enum.TryParse<UnitStatus>(query.Status, ignoreCase: true, out var status))
        {
            q = status switch
            {
                UnitStatus.Occupied => q.Where(row => row.IsOccupied
                    && !row.IsInTurnover
                    && !row.IsOutOfService
                    && !row.IsOnManagementHold),
                UnitStatus.Reserved => q.Where(row => !row.IsOccupied
                    && row.HasScheduledMoveIn
                    && !row.IsInTurnover
                    && !row.IsOutOfService
                    && !row.IsOnManagementHold),
                UnitStatus.Offline => q.Where(row => row.IsInTurnover || row.IsOutOfService || row.IsOnManagementHold),
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
                    && file.EntityId != null
                    && (
                        (file.EntityType == "Unit" && file.EntityId == unit.Id)
                        || _db.LegalDocumentArtifacts.Any(artifact =>
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
                            && inspection.Id == file.EntityId.Value))))),
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
            "moveout" => q.Where(row => row.Lifecycle == "Ending"
                && !row.IsInTurnover && !row.IsOutOfService && !row.IsOnManagementHold),
            "renewal" => q.Where(row => row.Lifecycle == "Occupied"
                && !row.IsInTurnover && !row.IsOutOfService && !row.IsOnManagementHold
                && row.BusinessDate != null
                && row.CurrentAgreementEndOn != null
                && row.CurrentAgreementEndOn >= row.BusinessDate
                && row.CurrentAgreementEndOn <= row.BusinessDate.Value.AddDays(90)),
            "active" => q.Where(row => row.Lifecycle == "Occupied"
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

        if (row.Lifecycle == "Ending")
        {
            return "Move-Out";
        }

        if (row.Lifecycle == "Occupied")
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

    private static UnitStatus ResolveUnitStatus(UnitHealthReadRow row) =>
        row.IsInTurnover || row.IsOutOfService || row.IsOnManagementHold
            ? UnitStatus.Offline
            : row.IsOccupied
                ? UnitStatus.Occupied
                : row.HasScheduledMoveIn
                    ? UnitStatus.Reserved
                    : UnitStatus.Vacant;

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
        var entity = await _db.Units
            .AsNoTracking()
            .FirstOrDefaultAsync(u => u.Id == id && u.Property != null && u.Property.PortfolioId == portfolioId, ct);

        return entity == null ? null : UnitResponse.FromEntity(entity);
    }

    public async Task<UnitResponse?> CreateAsync(int portfolioId, CreateUnitRequest request, CancellationToken ct = default)
    {
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
            PropertyId = request.PropertyId,
            UnitNumber = request.UnitNumber,
            FloorPlan = request.FloorPlan,
            Bedrooms = request.Bedrooms,
            Bathrooms = request.Bathrooms,
            SquareFeet = request.SquareFeet,
            MarketRent = request.MarketRent,
            Status = request.Status,
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

        var response = UnitResponse.FromEntity(entity);
        await _dataUpdate.BroadcastEntityUpdateAsync(portfolioId, EntityType, entity.Id, response, ct);
        return response;
    }

    public async Task<UnitResponse?> UpdateAsync(int portfolioId, int id, UpdateUnitRequest request, CancellationToken ct = default)
    {
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

        if (request.Status.HasValue
            && request.Status.Value != entity.Status
            && request.Status.Value != UnitStatus.Occupied
            && await _db.Leases.AnyAsync(l =>
                l.PortfolioId == portfolioId
                && l.UnitId == entity.Id
                && (l.Status == LeaseStatus.Active || l.Status == LeaseStatus.NoticeGiven), ct))
        {
            throw new DomainValidationException(
                "This unit has a current lease. End, move out, or cancel notice on the lease before changing the unit status.",
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
        ApplyValueIfChanged(request.Status, entity.Status, "Status", v => entity.Status = v);
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

        var response = UnitResponse.FromEntity(entity);
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
                && f.EntityType == EntityType
                && f.EntityId == unitId, ct))
        {
            throw new DomainValidationException(
                "This unit has document history. Archive the documents instead of deleting the unit.",
                statusCode: 409);
        }
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
        ["Status"] = entity.Status,
        ["Notes"] = entity.Notes,
    });

    private static string Serialize(Dictionary<string, object?> values) => JsonSerializer.Serialize(values);
}
