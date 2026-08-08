using System.Text;
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

/// <inheritdoc cref="ITenantService"/>
public class TenantService : ITenantService
{
    private const string EntityType = "Tenant";
    private const int MaxSearchTokens = 8;
    private const string ActiveLeaseDeleteBlockedReason =
        "This tenant is a current resident in an occupied rental; return possession or change the household first.";
    private const string LeaseHistoryDeleteBlockedReason =
        "This tenant has rental relationship history; keep the tenant record to preserve agreements and account history.";

    private readonly RentalCommandDbContext _db;
    private readonly IDataUpdateService _dataUpdate;
    private readonly IAtomicUnitOfWork? _atomic;
    private readonly TimeProvider _timeProvider;

    public TenantService(
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
    public async Task<TenantResponse?> CreateAuthorizedAsync(
        WorkspaceReadScope scope,
        CreateTenantRequest request,
        string operationKey,
        CancellationToken ct = default)
    {
        var command = AtomicCoreCrudMutation.Command(scope, AtomicCoreCrudMutationDomain.Tenant,
            AtomicCoreCrudMutationOperation.Create, 0, operationKey, request,
            createdAtUtc: _timeProvider.UtcNow());
        var outcome = await Atomic.ExecuteAsync(
            AtomicCoreCrudMutation.Identity(command), command, AtomicCoreCrudMutation.Codec, ct);
        return DeserializeSnapshot<TenantResponse>(outcome.Value);
    }

    public async Task<IReadOnlyList<TenantResponse>> CreateGuidedSetupBatchAsync(
        WorkspaceReadScope scope,
        GuidedTenantSetupRequest request,
        string operationKey,
        CancellationToken ct = default)
    {
        var command = AtomicGuidedTenantSetup.Command(scope, request, operationKey);
        var outcome = await Atomic.ExecuteAsync(
            AtomicGuidedTenantSetup.Identity(command), command, AtomicGuidedTenantSetup.Codec, ct);
        return JsonSerializer.Deserialize<List<TenantResponse>>(outcome.Value.TenantsJson) ?? [];
    }

    public async Task<TenantResponse?> UpdateAuthorizedAsync(
        WorkspaceReadScope scope,
        int id,
        UpdateTenantRequest request,
        string operationKey,
        CancellationToken ct = default)
    {
        var command = AtomicCoreCrudMutation.Command(scope, AtomicCoreCrudMutationDomain.Tenant,
            AtomicCoreCrudMutationOperation.Update, id, operationKey, request,
            changedAtUtc: _timeProvider.UtcNow());
        var outcome = await Atomic.ExecuteAsync(
            AtomicCoreCrudMutation.Identity(command), command, AtomicCoreCrudMutation.Codec, ct);
        return DeserializeSnapshot<TenantResponse>(outcome.Value);
    }

    public async Task<bool> DeleteAuthorizedAsync(
        WorkspaceReadScope scope,
        int id,
        string operationKey,
        CancellationToken ct = default)
    {
        var command = AtomicCoreCrudMutation.Command(scope, AtomicCoreCrudMutationDomain.Tenant,
            AtomicCoreCrudMutationOperation.Delete, id, operationKey, new object(),
            changedAtUtc: _timeProvider.UtcNow());
        var outcome = await Atomic.ExecuteAsync(
            AtomicCoreCrudMutation.Identity(command), command, AtomicCoreCrudMutation.Codec, ct);
        return outcome.Value.Found;
    }

    private IAtomicUnitOfWork Atomic => _atomic ?? throw new InvalidOperationException(
        "Scoped tenant mutations require the atomic persistence kernel.");

    private static TResponse? DeserializeSnapshot<TResponse>(AtomicCoreCrudMutationResult result)
        where TResponse : class =>
        result.Found && result.ResponseJson is not null
            ? JsonSerializer.Deserialize<TResponse>(result.ResponseJson)
            : null;

    public async Task<IReadOnlyList<TenantResponse>> ListAsync(int portfolioId, ListQuery query, CancellationToken ct = default)
    {
        var page = await ListPageAsync(portfolioId, ToTenantListQuery(query), ct);
        return page.Items;
    }

    public async Task<IReadOnlyList<TenantResponse>> ListAuthorizedAsync(
        WorkspaceReadScope scope,
        TenantListQuery query,
        CancellationToken ct = default)
    {
        var page = await ListPageAuthorizedAsync(scope, query, ct);
        return page.Items;
    }

    public Task<TenantListResponse> ListPageAuthorizedAsync(
        WorkspaceReadScope scope,
        TenantListQuery query,
        CancellationToken ct = default)
    {
        var tenants = _db.Tenants
            .AsNoTracking()
            .WhereAuthorized(
                _db,
                scope,
                [CapabilityKeys.RentalsRead, CapabilityKeys.LeasingOnboardingManage],
                _timeProvider.UtcNow());
        return ListPageFromQueryAsync(tenants, scope.PortfolioId, query, ct);
    }

    public Task<TenantListResponse> ListPageAsync(int portfolioId, TenantListQuery query, CancellationToken ct = default)
    {
        var q = _db.Tenants
            .AsNoTracking()
            .Where(t => t.PortfolioId == portfolioId);

        return ListPageFromQueryAsync(q, portfolioId, query, ct);
    }

    private async Task<TenantListResponse> ListPageFromQueryAsync(
        IQueryable<Tenant> q,
        int portfolioId,
        TenantListQuery query,
        CancellationToken ct)
    {
        // Keep one grouped relation for all current-resident filters, active-count sorting, and
        // page projection. It remains an IQueryable, so grouping and relationship work stay in SQL.
        var currentRelationships = BuildGroupedCurrentRelationshipRelation(
            portfolioId,
            query.UnitId,
            query.PropertyId);

        if (query.UnitId.HasValue)
        {
            var workOrderTenantIds = BuildWorkOrderTenantIdsQuery(
                portfolioId,
                query.UnitId,
                query.PropertyId);
            q = q.Where(tenant =>
                currentRelationships
                    .Where(row => row.HasCurrentUnitMatch)
                    .Select(row => row.TenantId)
                    .Contains(tenant.Id)
                || workOrderTenantIds.Contains(tenant.Id));
        }
        else if (query.PropertyId.HasValue)
        {
            var workOrderTenantIds = BuildWorkOrderTenantIdsQuery(
                portfolioId,
                unitId: null,
                query.PropertyId);
            q = q.Where(tenant =>
                currentRelationships
                    .Where(row => row.HasCurrentPropertyMatch)
                    .Select(row => row.TenantId)
                    .Contains(tenant.Id)
                || workOrderTenantIds.Contains(tenant.Id));
        }

        if (query.AvailableForLease == true)
        {
            if (query.IncludeLeaseManagementId is { } includeLeaseManagementId)
            {
                var includedTenantIds = _db.LeaseManagementParties
                    .AsNoTracking()
                    .Where(party => party.PortfolioId == portfolioId
                        && party.LeaseManagementId == includeLeaseManagementId)
                    .Select(party => party.TenantId)
                    .Distinct();
                q = q.Where(tenant => includedTenantIds.Contains(tenant.Id)
                    || !currentRelationships.Any(row => row.TenantId == tenant.Id));
            }
            else
            {
                q = q.Where(tenant => !currentRelationships.Any(row => row.TenantId == tenant.Id));
            }
        }

        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var tokens = SearchTokens(query.Search);
            if (tokens.Count == 0)
            {
                q = q.Where(_ => false);
            }
            else
            {
                foreach (var token in tokens)
                {
                    var pattern = $"%{token}%";
                    q = q.Where(tenant =>
                        EF.Functions.Like(tenant.FirstName.ToLower(), pattern) ||
                        EF.Functions.Like(tenant.LastName.ToLower(), pattern) ||
                        EF.Functions.Like((tenant.FirstName + " " + tenant.LastName).ToLower(), pattern) ||
                        (tenant.Email != null && EF.Functions.Like(tenant.Email.ToLower(), pattern)) ||
                        (tenant.Phone != null && EF.Functions.Like(tenant.Phone.ToLower(), pattern)));
                }
            }
        }

        var totalCount = await q.CountAsync(ct);

        var rows = BuildTenantRelationshipReadQuery(
            q,
            BuildRelationshipHistoryRelation(portfolioId),
            currentRelationships);

        rows = query.SortField switch
        {
            "name" => query.SortDescending ? rows.OrderByDescending(row => row.Entity.LastName).ThenByDescending(row => row.Entity.FirstName) : rows.OrderBy(row => row.Entity.LastName).ThenBy(row => row.Entity.FirstName),
            "firstname" => query.SortDescending ? rows.OrderByDescending(row => row.Entity.FirstName) : rows.OrderBy(row => row.Entity.FirstName),
            "lastname" => query.SortDescending ? rows.OrderByDescending(row => row.Entity.LastName) : rows.OrderBy(row => row.Entity.LastName),
            "email" => query.SortDescending ? rows.OrderByDescending(row => row.Entity.Email) : rows.OrderBy(row => row.Entity.Email),
            "phone" => query.SortDescending ? rows.OrderByDescending(row => row.Entity.Phone) : rows.OrderBy(row => row.Entity.Phone),
            "activeleasecount" => query.SortDescending
                ? rows.OrderByDescending(row => row.ActiveLeaseCount).ThenBy(row => row.Entity.LastName).ThenBy(row => row.Entity.FirstName)
                : rows.OrderBy(row => row.ActiveLeaseCount).ThenBy(row => row.Entity.LastName).ThenBy(row => row.Entity.FirstName),
            "createdat" => query.SortDescending ? rows.OrderByDescending(row => row.Entity.CreatedAt) : rows.OrderBy(row => row.Entity.CreatedAt),
            "updatedat" => query.SortDescending ? rows.OrderByDescending(row => row.Entity.UpdatedAt) : rows.OrderBy(row => row.Entity.UpdatedAt),
            _ => query.SortDescending ? rows.OrderByDescending(row => row.Entity.CreatedAt) : rows.OrderBy(row => row.Entity.CreatedAt),
        };

        var pageRows = await rows
            .Skip(query.NormalizedSkip)
            .Take(query.NormalizedTake)
            .ToListAsync(ct);

        return new TenantListResponse
        {
            Items = pageRows.Select(r =>
            {
                var response = TenantResponse.FromEntity(r.Entity);
                ApplyDeleteState(
                    response,
                    r.ActiveLeaseCount,
                    r.LeaseHistoryCount);
                ApplyCurrentResidentContext(response, r);
                return response;
            }).ToList(),
            TotalCount = totalCount,
            Skip = query.NormalizedSkip,
            Take = query.NormalizedTake,
        };
    }

    /// <summary>
    /// Adds one grouped, set-based relationship relation to a tenant query. Agreement versions do
    /// not inflate history; an active count requires current effective resident membership in a
    /// LeaseManagement that the occupancy projection identifies as possessing its Unit.
    /// </summary>
    internal IQueryable<TenantRelationshipReadRow> BuildRelationshipCountQuery(
        IQueryable<Tenant> tenants,
        int portfolioId)
    {
        var currentRelationships = BuildGroupedCurrentRelationshipRelation(portfolioId, null, null);
        return BuildTenantRelationshipReadQuery(
            tenants,
            BuildRelationshipHistoryRelation(portfolioId),
            currentRelationships);
    }

    private IQueryable<TenantRelationshipReadRow> BuildTenantRelationshipReadQuery(
        IQueryable<Tenant> tenants,
        IQueryable<TenantRelationshipHistoryAggregateRow> historyCounts,
        IQueryable<TenantCurrentRelationshipAggregateRow> currentRelationships)
    {
        return
            from tenant in tenants
            join history in historyCounts
                on new { tenant.PortfolioId, TenantId = tenant.Id }
                equals new { history.PortfolioId, history.TenantId }
                into historyGroup
            from history in historyGroup.DefaultIfEmpty()
            join current in currentRelationships
                on new { tenant.PortfolioId, TenantId = tenant.Id }
                equals new { current.PortfolioId, current.TenantId }
                into currentGroup
            from current in currentGroup.DefaultIfEmpty()
            select new TenantRelationshipReadRow
            {
                Entity = tenant,
                ActiveLeaseCount = (int?)current.ActiveLeaseCount ?? 0,
                LeaseHistoryCount = (int?)history.LeaseHistoryCount ?? 0,
                CurrentPropertyId = current.CurrentPropertyId,
                CurrentPropertyName = current.CurrentPropertyName,
                CurrentUnitId = current.CurrentUnitId,
                CurrentUnitNumber = current.CurrentUnitNumber,
            };
    }

    private IQueryable<TenantCurrentRelationshipAggregateRow> BuildGroupedCurrentRelationshipRelation(
        int portfolioId,
        int? unitId,
        int? propertyId)
    {
        var targetUnitId = unitId ?? -1;
        var targetPropertyId = propertyId ?? -1;

        var currentRelationshipRows = (
            from party in _db.LeaseManagementParties.AsNoTracking()
            join management in _db.LeaseManagements.AsNoTracking()
                on new { party.PortfolioId, Id = party.LeaseManagementId }
                equals new { management.PortfolioId, management.Id }
            join property in _db.Properties.AsNoTracking()
                on new { management.PortfolioId, Id = management.PropertyId }
                equals new { property.PortfolioId, property.Id }
            join unit in _db.Units.AsNoTracking()
                on new { management.PortfolioId, Id = management.UnitId }
                equals new { unit.PortfolioId, unit.Id }
            join occupancy in _db.UnitOccupancyProjections.AsNoTracking()
                on new { party.PortfolioId, LeaseManagementId = (int?)party.LeaseManagementId }
                equals new { occupancy.PortfolioId, LeaseManagementId = occupancy.CurrentLeaseManagementId }
            join lifecycle in _db.LeaseManagementLifecycleProjections.AsNoTracking()
                on new { party.PortfolioId, LeaseManagementId = party.LeaseManagementId }
                equals new { lifecycle.PortfolioId, lifecycle.LeaseManagementId }
            where party.PortfolioId == portfolioId
                && party.Role != LeaseManagementPartyRole.Guarantor
                && party.EffectiveFrom <= lifecycle.BusinessDate
            where party.EffectiveThrough == null
                || party.EffectiveThrough >= lifecycle.BusinessDate
            select new TenantCurrentRelationshipRow
            {
                PortfolioId = party.PortfolioId,
                TenantId = party.TenantId,
                LeaseManagementId = party.LeaseManagementId,
                PropertyId = management.PropertyId,
                PropertyName = property.Name,
                UnitId = management.UnitId,
                UnitNumber = unit.UnitNumber,
            }).Distinct();

        var currentCountGroups =
            from row in currentRelationshipRows
            group row by new { row.PortfolioId, row.TenantId } into current
            select new
            {
                PortfolioId = current.Key.PortfolioId,
                TenantId = current.Key.TenantId,
                ActiveLeaseCount = current
                    .Select(row => row.LeaseManagementId)
                    .Distinct()
                    .Count(),
                HasCurrentRelationship = current.Any(),
                HasCurrentUnitMatch = unitId.HasValue
                    && current.Any(row => row.UnitId == targetUnitId),
                HasCurrentPropertyMatch = propertyId.HasValue
                    && current.Any(row => row.PropertyId == targetPropertyId),
            };

        // Select the complete first relationship row once per tenant. Keeping the identity fields
        // together prevents EF from emitting one ordered relationship subquery per response field.
        var minimumPropertyNames =
            from row in currentRelationshipRows
            group row by new { row.PortfolioId, row.TenantId } into current
            select new
            {
                current.Key.PortfolioId,
                current.Key.TenantId,
                PropertyName = current.Min(row => row.PropertyName),
            };

        var minimumPropertyRows =
            from row in currentRelationshipRows
            join minimum in minimumPropertyNames
                on new { row.PortfolioId, row.TenantId, row.PropertyName }
                equals new { minimum.PortfolioId, minimum.TenantId, minimum.PropertyName }
            select row;

        var minimumUnitNumbers =
            from row in minimumPropertyRows
            group row by new { row.PortfolioId, row.TenantId, row.PropertyName } into current
            select new
            {
                current.Key.PortfolioId,
                current.Key.TenantId,
                current.Key.PropertyName,
                UnitNumber = current.Min(row => row.UnitNumber),
            };

        var minimumUnitRows =
            from row in minimumPropertyRows
            join minimum in minimumUnitNumbers
                on new { row.PortfolioId, row.TenantId, row.PropertyName, row.UnitNumber }
                equals new { minimum.PortfolioId, minimum.TenantId, minimum.PropertyName, minimum.UnitNumber }
            select row;

        var minimumLeaseManagementIds =
            from row in minimumUnitRows
            group row by new { row.PortfolioId, row.TenantId, row.PropertyName, row.UnitNumber } into current
            select new
            {
                current.Key.PortfolioId,
                current.Key.TenantId,
                current.Key.PropertyName,
                current.Key.UnitNumber,
                LeaseManagementId = current.Min(row => row.LeaseManagementId),
            };

        var currentIdentityRows =
            from row in minimumUnitRows
            join minimum in minimumLeaseManagementIds
                on new { row.PortfolioId, row.TenantId, row.PropertyName, row.UnitNumber, row.LeaseManagementId }
                equals new { minimum.PortfolioId, minimum.TenantId, minimum.PropertyName, minimum.UnitNumber, minimum.LeaseManagementId }
            select row;

        var currentGroups =
            from counts in currentCountGroups
            join identity in currentIdentityRows
                on new { counts.PortfolioId, counts.TenantId }
                equals new { identity.PortfolioId, identity.TenantId }
            select new TenantCurrentRelationshipAggregateRow
            {
                PortfolioId = counts.PortfolioId,
                TenantId = counts.TenantId,
                ActiveLeaseCount = counts.ActiveLeaseCount,
                HasCurrentRelationship = counts.HasCurrentRelationship,
                HasCurrentUnitMatch = counts.HasCurrentUnitMatch,
                HasCurrentPropertyMatch = counts.HasCurrentPropertyMatch,
                CurrentPropertyId = identity.PropertyId,
                CurrentPropertyName = identity.PropertyName,
                CurrentUnitId = identity.UnitId,
                CurrentUnitNumber = identity.UnitNumber,
            };

        return currentGroups;
    }

    private IQueryable<TenantRelationshipHistoryAggregateRow> BuildRelationshipHistoryRelation(int portfolioId) =>
        from party in _db.LeaseManagementParties.AsNoTracking()
        where party.PortfolioId == portfolioId
        group party by new { party.PortfolioId, party.TenantId } into parties
        select new TenantRelationshipHistoryAggregateRow
        {
            PortfolioId = parties.Key.PortfolioId,
            TenantId = parties.Key.TenantId,
            LeaseHistoryCount = parties
                .Select(party => party.LeaseManagementId)
                .Distinct()
                .Count(),
        };

    private IQueryable<int> BuildWorkOrderTenantIdsQuery(
        int portfolioId,
        int? unitId,
        int? propertyId)
    {
        var workOrderMatches =
            from workOrder in _db.WorkOrders.AsNoTracking()
            join party in _db.LeaseManagementParties.AsNoTracking()
                on new
                {
                    workOrder.PortfolioId,
                    TenantId = workOrder.TenantId,
                }
                equals new
                {
                    party.PortfolioId,
                    TenantId = (int?)party.TenantId,
                }
            join management in _db.LeaseManagements.AsNoTracking()
                on new { party.PortfolioId, Id = party.LeaseManagementId }
                equals new { management.PortfolioId, management.Id }
            where workOrder.PortfolioId == portfolioId
                && workOrder.TenantId != null
                && party.Role != LeaseManagementPartyRole.Guarantor
                && management.CanceledAtUtc == null
                && management.PossessionReturnedAtUtc == null
            select new TenantWorkOrderMatchRow
            {
                TenantId = workOrder.TenantId!.Value,
                WorkOrderUnitId = workOrder.UnitId,
                WorkOrderPropertyId = workOrder.PropertyId,
                ManagementUnitId = management.UnitId,
                ManagementPropertyId = management.PropertyId,
            };

        if (unitId is { } targetUnitId)
        {
            workOrderMatches = workOrderMatches.Where(row =>
                row.WorkOrderUnitId == targetUnitId
                && row.ManagementUnitId == targetUnitId);
        }

        if (propertyId is { } targetPropertyId)
        {
            workOrderMatches = workOrderMatches.Where(row =>
                row.WorkOrderPropertyId == targetPropertyId
                && row.ManagementPropertyId == targetPropertyId);
        }

        return workOrderMatches
            .Select(row => row.TenantId)
            .Distinct();
    }

    internal sealed class TenantRelationshipReadRow
    {
        public Tenant Entity { get; init; } = null!;
        public int ActiveLeaseCount { get; init; }
        public int LeaseHistoryCount { get; init; }
        public int? CurrentPropertyId { get; init; }
        public string? CurrentPropertyName { get; init; }
        public int? CurrentUnitId { get; init; }
        public string? CurrentUnitNumber { get; init; }
    }

    private sealed class TenantCurrentRelationshipRow
    {
        public int PortfolioId { get; init; }
        public int TenantId { get; init; }
        public int LeaseManagementId { get; init; }
        public int PropertyId { get; init; }
        public string PropertyName { get; init; } = string.Empty;
        public int UnitId { get; init; }
        public string UnitNumber { get; init; } = string.Empty;
    }

    private sealed class TenantRelationshipHistoryAggregateRow
    {
        public int PortfolioId { get; init; }
        public int TenantId { get; init; }
        public int LeaseHistoryCount { get; init; }
    }

    private sealed class TenantCurrentRelationshipAggregateRow
    {
        public int PortfolioId { get; init; }
        public int TenantId { get; init; }
        public int ActiveLeaseCount { get; init; }
        public int? CurrentPropertyId { get; init; }
        public string? CurrentPropertyName { get; init; }
        public int? CurrentUnitId { get; init; }
        public string? CurrentUnitNumber { get; init; }
        public bool HasCurrentRelationship { get; init; }
        public bool HasCurrentUnitMatch { get; init; }
        public bool HasCurrentPropertyMatch { get; init; }
    }

    private sealed class TenantWorkOrderMatchRow
    {
        public int TenantId { get; init; }
        public int? WorkOrderUnitId { get; init; }
        public int? WorkOrderPropertyId { get; init; }
        public int ManagementUnitId { get; init; }
        public int ManagementPropertyId { get; init; }
    }

    private static TenantListQuery ToTenantListQuery(ListQuery query) => new()
    {
        Skip = query.Skip,
        Take = query.Take,
        Search = query.Search,
        Sort = query.Sort,
        AvailableForLease = (query as TenantListQuery)?.AvailableForLease,
        PropertyId = (query as TenantListQuery)?.PropertyId,
        UnitId = (query as TenantListQuery)?.UnitId,
        IncludeLeaseManagementId = (query as TenantListQuery)?.IncludeLeaseManagementId,
    };

    private static void ApplyDeleteState(TenantResponse response, int activeLeaseCount, int leaseHistoryCount)
    {
        response.ActiveLeaseCount = activeLeaseCount;
        response.LeaseHistoryCount = leaseHistoryCount;
        response.CanDelete = activeLeaseCount == 0 && leaseHistoryCount == 0;
        response.DeleteBlockedReason = activeLeaseCount > 0
            ? ActiveLeaseDeleteBlockedReason
            : leaseHistoryCount > 0
                ? LeaseHistoryDeleteBlockedReason
                : null;
    }

    private static void ApplyCurrentResidentContext(TenantResponse response, TenantRelationshipReadRow row)
    {
        response.CurrentPropertyId = row.CurrentPropertyId;
        response.CurrentPropertyName = row.CurrentPropertyName;
        response.CurrentUnitId = row.CurrentUnitId;
        response.CurrentUnitNumber = row.CurrentUnitNumber;
    }

    private static IReadOnlyList<string> SearchTokens(string search)
    {
        var tokens = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var token = new StringBuilder();

        void Flush()
        {
            if (token.Length == 0) return;
            var value = token.ToString().ToLowerInvariant();
            token.Clear();

            if (seen.Add(value))
            {
                tokens.Add(value);
            }
        }

        foreach (var c in search.Trim())
        {
            if (char.IsLetterOrDigit(c))
            {
                if (tokens.Count >= MaxSearchTokens && token.Length == 0)
                {
                    break;
                }

                token.Append(c);
            }
            else
            {
                Flush();
                if (tokens.Count >= MaxSearchTokens)
                {
                    break;
                }
            }
        }

        if (tokens.Count < MaxSearchTokens)
        {
            Flush();
        }

        return tokens;
    }

    public async Task<TenantResponse?> GetAsync(int portfolioId, int id, CancellationToken ct = default)
    {
        var tenants = _db.Tenants
            .AsNoTracking()
            .Where(t => t.Id == id && t.PortfolioId == portfolioId);
        return await GetFromQueryAsync(tenants, portfolioId, ct);
    }

    public Task<TenantResponse?> GetAuthorizedAsync(
        WorkspaceReadScope scope,
        int id,
        CancellationToken ct = default)
    {
        var tenants = _db.Tenants
            .AsNoTracking()
            .Where(t => t.Id == id)
            .WhereAuthorized(
                _db,
                scope,
                [CapabilityKeys.RentalsRead, CapabilityKeys.LeasingOnboardingManage],
                _timeProvider.UtcNow());
        return GetFromQueryAsync(tenants, scope.PortfolioId, ct);
    }

    private async Task<TenantResponse?> GetFromQueryAsync(
        IQueryable<Tenant> tenants,
        int portfolioId,
        CancellationToken ct)
    {
        var row = await tenants
            .Select(t => new
            {
                Entity = t,
                ActiveLeaseCount = _db.LeaseManagementParties
                    .Where(party => party.PortfolioId == portfolioId
                        && party.TenantId == t.Id
                        && party.Role != LeaseManagementPartyRole.Guarantor
                        && _db.UnitOccupancyProjections.Any(occupancy =>
                            occupancy.PortfolioId == portfolioId
                            && occupancy.CurrentLeaseManagementId == party.LeaseManagementId)
                        && _db.LeaseManagementLifecycleProjections.Any(lifecycle =>
                            lifecycle.PortfolioId == portfolioId
                            && lifecycle.LeaseManagementId == party.LeaseManagementId
                            && party.EffectiveFrom <= lifecycle.BusinessDate
                            && (party.EffectiveThrough == null || party.EffectiveThrough >= lifecycle.BusinessDate)))
                    .Select(party => party.LeaseManagementId)
                    .Distinct()
                    .Count(),
                LeaseHistoryCount = _db.LeaseManagementParties
                    .Where(party => party.PortfolioId == portfolioId && party.TenantId == t.Id)
                    .Select(party => party.LeaseManagementId)
                    .Distinct()
                    .Count(),
            })
            .FirstOrDefaultAsync(ct);

        if (row == null)
        {
            return null;
        }

        var response = TenantResponse.FromEntity(row.Entity);
        ApplyDeleteState(response, row.ActiveLeaseCount, row.LeaseHistoryCount);
        return response;
    }

}
