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
using RentalCommand.Data;
using RentalCommand.Data.Authorization;

namespace RentalCommand.Api.Services.Domain;

/// <inheritdoc cref="IUnitService"/>
public class UnitService : IUnitService
{
    private const string EntityType = "Unit";

    private readonly RentalCommandDbContext _db;
    private readonly TimeProvider _timeProvider;
    private readonly IAtomicUnitOfWork _atomic;

    public UnitService(
        RentalCommandDbContext db,
        IDataUpdateService dataUpdate,
        IAuditTrailService audit,
        TimeProvider timeProvider,
        IAtomicUnitOfWork atomic)
    {
        _db = db;
        _timeProvider = timeProvider;
        _atomic = atomic;
    }

    public async Task<IReadOnlyList<UnitResponse>> ListAsync(
        WorkspaceReadScope scope, int? propertyId, ListQuery query, CancellationToken ct = default)
    {
        var q = BuildListQuery(scope, propertyId, query);

        return await q
            .Skip(query.NormalizedSkip)
            .Take(query.NormalizedTake)
            .ToListAsync(ct);
    }

    public async Task<UnitListResponse> ListPageAsync(
        WorkspaceReadScope scope, int? propertyId, UnitListQuery query, CancellationToken ct = default)
    {
        var q = BuildListQuery(scope, propertyId, query);
        var totalCount = await q.CountAsync(ct);
        var items = await q
            .Skip(query.NormalizedSkip)
            .Take(query.NormalizedTake)
            .ToListAsync(ct);

        return new UnitListResponse
        {
            Items = items,
            TotalCount = totalCount,
            Skip = query.NormalizedSkip,
            Take = query.NormalizedTake,
        };
    }

    private IQueryable<UnitResponse> BuildListQuery(
        WorkspaceReadScope scope, int? propertyId, ListQuery query)
    {
        var q = BuildCanonicalResponseQuery(scope);

        if (propertyId.HasValue)
        {
            q = q.Where(unit => unit.PropertyId == propertyId.Value);
        }

        if (query is UnitListQuery { ExcludeUnitId: > 0 } unitQuery)
        {
            q = q.Where(unit => unit.Id != unitQuery.ExcludeUnitId.Value);
        }

        if (query is UnitListQuery { AvailableForLease: true })
        {
            q = q.Where(unit => unit.Status == DerivedUnitStatus.Vacant);
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

        return q;
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

        // Documents are aggregated once for the returned page. Keeping this out of the main
        // health projection avoids executing a deeply correlated StoredFiles subquery once per
        // Unit while still doing all association resolution and counting in PostgreSQL.
        var pageUnitIds = rows.Select(row => row.Id).ToArray();
        var documentCounts = pageUnitIds.Length == 0
            ? new Dictionary<int, int>()
            : await BuildUnitDocumentCountsQuery(portfolioId, pageUnitIds)
                .ToDictionaryAsync(row => row.UnitId, row => row.Count, ct);

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
                DocsNeedingReviewCount = documentCounts.GetValueOrDefault(r.Id),
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
            };
    }

    /// <summary>
    /// One page-scoped, set-based PostgreSQL aggregate for Unit document badges. UNION removes a
    /// file that reaches the same Unit through more than one relationship before GROUP BY counts it.
    /// </summary>
    internal IQueryable<UnitAggregateCountRow> BuildUnitDocumentCountsQuery(
        int portfolioId,
        IReadOnlyCollection<int> unitIds)
    {
        var directUnitFiles =
            from file in _db.StoredFiles.AsNoTracking()
            join unit in _db.Units.AsNoTracking()
                on file.EntityId equals (long?)unit.Id
            where file.PortfolioId == portfolioId
                && unit.PortfolioId == portfolioId
                && file.EntityType == EntityType
                && unitIds.Contains(unit.Id)
            select new UnitDocumentAssociationRow { UnitId = unit.Id, FileId = file.Id };

        var directExpenseFiles =
            from file in _db.StoredFiles.AsNoTracking()
            join expense in _db.Expenses.AsNoTracking()
                on file.EntityId equals (long?)expense.Id
            where file.PortfolioId == portfolioId
                && expense.PortfolioId == portfolioId
                && file.EntityType == nameof(Expense)
                && expense.UnitId != null
                && unitIds.Contains(expense.UnitId.Value)
            select new UnitDocumentAssociationRow { UnitId = expense.UnitId!.Value, FileId = file.Id };

        var workOrderExpenseFiles =
            from file in _db.StoredFiles.AsNoTracking()
            join expense in _db.Expenses.AsNoTracking()
                on file.EntityId equals (long?)expense.Id
            join workOrder in _db.WorkOrders.AsNoTracking()
                on expense.WorkOrderId equals (int?)workOrder.Id
            where file.PortfolioId == portfolioId
                && expense.PortfolioId == portfolioId
                && workOrder.PortfolioId == portfolioId
                && file.EntityType == nameof(Expense)
                && workOrder.UnitId != null
                && unitIds.Contains(workOrder.UnitId.Value)
            select new UnitDocumentAssociationRow { UnitId = workOrder.UnitId!.Value, FileId = file.Id };

        var workOrderFiles =
            from file in _db.StoredFiles.AsNoTracking()
            join workOrder in _db.WorkOrders.AsNoTracking()
                on file.EntityId equals (long?)workOrder.Id
            where file.PortfolioId == portfolioId
                && workOrder.PortfolioId == portfolioId
                && file.EntityType == nameof(WorkOrder)
                && workOrder.UnitId != null
                && unitIds.Contains(workOrder.UnitId.Value)
            select new UnitDocumentAssociationRow { UnitId = workOrder.UnitId!.Value, FileId = file.Id };

        var inspectionFiles =
            from file in _db.StoredFiles.AsNoTracking()
            join inspection in _db.Inspections.AsNoTracking()
                on file.EntityId equals (long?)inspection.Id
            where file.PortfolioId == portfolioId
                && inspection.PortfolioId == portfolioId
                && file.EntityType == nameof(Inspection)
                && inspection.UnitId != null
                && unitIds.Contains(inspection.UnitId.Value)
            select new UnitDocumentAssociationRow { UnitId = inspection.UnitId!.Value, FileId = file.Id };

        var issuedAgreementFiles =
            from agreement in _db.LeaseAgreements.AsNoTracking()
            join management in _db.LeaseManagements.AsNoTracking()
                on new { agreement.PortfolioId, Id = agreement.LeaseManagementId }
                equals new { management.PortfolioId, management.Id }
            join artifact in _db.LegalDocumentArtifacts.AsNoTracking()
                on agreement.IssuedArtifactId equals (int?)artifact.Id
            join file in _db.StoredFiles.AsNoTracking()
                on new { artifact.PortfolioId, Id = artifact.StoredFileId }
                equals new { file.PortfolioId, file.Id }
            where agreement.PortfolioId == portfolioId
                && unitIds.Contains(management.UnitId)
            select new UnitDocumentAssociationRow { UnitId = management.UnitId, FileId = file.Id };

        var executedAgreementFiles =
            from agreement in _db.LeaseAgreements.AsNoTracking()
            join management in _db.LeaseManagements.AsNoTracking()
                on new { agreement.PortfolioId, Id = agreement.LeaseManagementId }
                equals new { management.PortfolioId, management.Id }
            join artifact in _db.LegalDocumentArtifacts.AsNoTracking()
                on agreement.ExecutedArtifactId equals (int?)artifact.Id
            join file in _db.StoredFiles.AsNoTracking()
                on new { artifact.PortfolioId, Id = artifact.StoredFileId }
                equals new { file.PortfolioId, file.Id }
            where agreement.PortfolioId == portfolioId
                && unitIds.Contains(management.UnitId)
            select new UnitDocumentAssociationRow { UnitId = management.UnitId, FileId = file.Id };

        return directUnitFiles
            .Union(directExpenseFiles)
            .Union(workOrderExpenseFiles)
            .Union(workOrderFiles)
            .Union(inspectionFiles)
            .Union(issuedAgreementFiles)
            .Union(executedAgreementFiles)
            .GroupBy(row => row.UnitId)
            .Select(group => new UnitAggregateCountRow
            {
                UnitId = group.Key,
                Count = group.Count(),
            });
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
    }

    internal sealed class UnitDocumentAssociationRow
    {
        public int UnitId { get; init; }
        public int FileId { get; init; }
    }

    internal sealed class UnitAggregateCountRow
    {
        public int UnitId { get; init; }
        public int Count { get; init; }
    }

    public async Task<UnitResponse?> GetAsync(int portfolioId, int id, CancellationToken ct = default)
    {
        return await BuildCanonicalResponseQuery(portfolioId)
            .FirstOrDefaultAsync(unit => unit.Id == id, ct);
    }

    public async Task<UnitResponse?> CreateAsync(
        WorkspaceReadScope scope,
        CreateUnitRequest request,
        string operationKey,
        CancellationToken ct = default)
    {
        var command = AtomicRentalMutation.Command(scope, AtomicRentalMutationDomain.Unit,
            AtomicRentalMutationOperation.Create, 0, operationKey, request);
        var outcome = await Atomic.ExecuteAsync(
            AtomicRentalMutation.Identity(command), command, AtomicRentalMutation.Codec, ct);
        return outcome.Value.Found && outcome.Value.ResponseJson is { Length: > 0 } json
            ? JsonSerializer.Deserialize<UnitResponse>(json)
              ?? throw new InvalidOperationException("The Unit receipt snapshot is invalid.")
            : null;
    }

    public async Task<UnitResponse?> UpdateAsync(
        WorkspaceReadScope scope,
        int id,
        UpdateUnitRequest request,
        string operationKey,
        CancellationToken ct = default)
    {
        var command = AtomicRentalMutation.UnitUpdateCommand(scope, id, operationKey, request);
        var outcome = await Atomic.ExecuteAsync(
            AtomicRentalMutation.Identity(command), command, AtomicRentalMutation.Codec, ct);
        return outcome.Value.Found ? await GetAsync(scope.PortfolioId, id, ct) : null;
    }

    public async Task<bool> DeleteAsync(
        WorkspaceReadScope scope,
        int id,
        string operationKey,
        CancellationToken ct = default)
    {
        var command = AtomicRentalMutation.Command(scope, AtomicRentalMutationDomain.Unit,
            AtomicRentalMutationOperation.Delete, id, operationKey, new object());
        var outcome = await Atomic.ExecuteAsync(
            AtomicRentalMutation.Identity(command), command, AtomicRentalMutation.Codec, ct);
        return outcome.Value.Found;
    }

    private IAtomicUnitOfWork Atomic => _atomic;

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

}
