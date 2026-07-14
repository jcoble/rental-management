using System.Text;
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
    private readonly TimeProvider _timeProvider;

    public TenantService(
        RentalCommandDbContext db,
        IDataUpdateService dataUpdate,
        TimeProvider timeProvider)
    {
        _db = db;
        _dataUpdate = dataUpdate;
        _timeProvider = timeProvider;
    }

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

        if (query.UnitId.HasValue)
        {
            var unitId = query.UnitId.Value;
            q = q.Where(tenant => _db.LeaseManagementParties.Any(party =>
                party.PortfolioId == portfolioId
                && party.TenantId == tenant.Id
                && party.Role != LeaseManagementPartyRole.Guarantor
                && _db.UnitOccupancyProjections.Any(occupancy =>
                    occupancy.PortfolioId == portfolioId
                    && occupancy.UnitId == unitId
                    && occupancy.CurrentLeaseManagementId == party.LeaseManagementId)
                && _db.LeaseManagementLifecycleProjections.Any(lifecycle =>
                    lifecycle.PortfolioId == portfolioId
                    && lifecycle.LeaseManagementId == party.LeaseManagementId
                    && party.EffectiveFrom <= lifecycle.BusinessDate
                    && (party.EffectiveThrough == null || party.EffectiveThrough >= lifecycle.BusinessDate))));
        }
        else if (query.PropertyId.HasValue)
        {
            var propertyId = query.PropertyId.Value;
            q = q.Where(tenant => _db.LeaseManagementParties.Any(party =>
                party.PortfolioId == portfolioId
                && party.TenantId == tenant.Id
                && party.Role != LeaseManagementPartyRole.Guarantor
                && _db.UnitOccupancyProjections.Any(occupancy =>
                    occupancy.PortfolioId == portfolioId
                    && occupancy.PropertyId == propertyId
                    && occupancy.CurrentLeaseManagementId == party.LeaseManagementId)
                && _db.LeaseManagementLifecycleProjections.Any(lifecycle =>
                    lifecycle.PortfolioId == portfolioId
                    && lifecycle.LeaseManagementId == party.LeaseManagementId
                    && party.EffectiveFrom <= lifecycle.BusinessDate
                    && (party.EffectiveThrough == null || party.EffectiveThrough >= lifecycle.BusinessDate))));
        }

        if (query.AvailableForLease == true)
        {
            var includeLeaseManagementId = query.IncludeLeaseManagementId;
            q = q.Where(tenant =>
                (includeLeaseManagementId.HasValue && _db.LeaseManagementParties.Any(party =>
                    party.PortfolioId == portfolioId
                    && party.LeaseManagementId == includeLeaseManagementId.Value
                    && party.TenantId == tenant.Id))
                || !_db.LeaseManagementParties.Any(party =>
                    party.PortfolioId == portfolioId
                    && party.TenantId == tenant.Id
                    && party.Role != LeaseManagementPartyRole.Guarantor
                    && _db.UnitOccupancyProjections.Any(occupancy =>
                        occupancy.PortfolioId == portfolioId
                        && occupancy.CurrentLeaseManagementId == party.LeaseManagementId)
                    && _db.LeaseManagementLifecycleProjections.Any(lifecycle =>
                        lifecycle.PortfolioId == portfolioId
                        && lifecycle.LeaseManagementId == party.LeaseManagementId
                        && party.EffectiveFrom <= lifecycle.BusinessDate
                        && (party.EffectiveThrough == null || party.EffectiveThrough >= lifecycle.BusinessDate))));
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
                    q = q.Where(t =>
                        EF.Functions.Like(t.FirstName.ToLower(), pattern) ||
                        EF.Functions.Like(t.LastName.ToLower(), pattern) ||
                        EF.Functions.Like((t.FirstName + " " + t.LastName).ToLower(), pattern) ||
                        (t.Email != null && EF.Functions.Like(t.Email.ToLower(), pattern)) ||
                        (t.Phone != null && EF.Functions.Like(t.Phone.ToLower(), pattern)));
                }
            }
        }

        q = query.SortField switch
        {
            "name" => query.SortDescending ? q.OrderByDescending(t => t.LastName).ThenByDescending(t => t.FirstName) : q.OrderBy(t => t.LastName).ThenBy(t => t.FirstName),
            "firstname" => query.SortDescending ? q.OrderByDescending(t => t.FirstName) : q.OrderBy(t => t.FirstName),
            "lastname" => query.SortDescending ? q.OrderByDescending(t => t.LastName) : q.OrderBy(t => t.LastName),
            "email" => query.SortDescending ? q.OrderByDescending(t => t.Email) : q.OrderBy(t => t.Email),
            "phone" => query.SortDescending ? q.OrderByDescending(t => t.Phone) : q.OrderBy(t => t.Phone),
            "activeleasecount" => query.SortDescending
                ? q.OrderByDescending(tenant => _db.LeaseManagementParties
                    .Where(party => party.PortfolioId == portfolioId
                        && party.TenantId == tenant.Id
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
                    .Count()).ThenBy(tenant => tenant.LastName).ThenBy(tenant => tenant.FirstName)
                : q.OrderBy(tenant => _db.LeaseManagementParties
                    .Where(party => party.PortfolioId == portfolioId
                        && party.TenantId == tenant.Id
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
                    .Count()).ThenBy(tenant => tenant.LastName).ThenBy(tenant => tenant.FirstName),
            "createdat" => query.SortDescending ? q.OrderByDescending(t => t.CreatedAt) : q.OrderBy(t => t.CreatedAt),
            "updatedat" => query.SortDescending ? q.OrderByDescending(t => t.UpdatedAt) : q.OrderBy(t => t.UpdatedAt),
            _ => query.SortDescending ? q.OrderByDescending(t => t.CreatedAt) : q.OrderBy(t => t.CreatedAt),
        };

        var totalCount = await q.CountAsync(ct);

        // Both counts are correlated, DISTINCT LeaseManagement aggregates in this page statement.
        // Agreement versions never inflate relationship history and no IDs are materialized for a
        // follow-up IN query.
        var rows = await BuildRelationshipCountQuery(
                q.Skip(query.NormalizedSkip).Take(query.NormalizedTake),
                portfolioId)
            .ToListAsync(ct);

        return new TenantListResponse
        {
            Items = rows.Select(r =>
            {
                var response = TenantResponse.FromEntity(r.Entity);
                ApplyDeleteState(
                    response,
                    r.ActiveLeaseCount,
                    r.LeaseHistoryCount);
                return response;
            }).ToList(),
            TotalCount = totalCount,
            Skip = query.NormalizedSkip,
            Take = query.NormalizedTake,
        };
    }

    /// <summary>
    /// Adds canonical relationship counts as correlated PostgreSQL subqueries. Agreement versions
    /// do not inflate history; an active count requires current effective resident membership in a
    /// LeaseManagement that the occupancy projection identifies as possessing its Unit.
    /// </summary>
    internal IQueryable<TenantRelationshipReadRow> BuildRelationshipCountQuery(
        IQueryable<Tenant> tenants,
        int portfolioId) =>
        tenants.Select(t => new TenantRelationshipReadRow
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
        });

    internal sealed class TenantRelationshipReadRow
    {
        public Tenant Entity { get; init; } = null!;
        public int ActiveLeaseCount { get; init; }
        public int LeaseHistoryCount { get; init; }
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

    public async Task<TenantResponse> CreateAsync(int portfolioId, CreateTenantRequest request, CancellationToken ct = default)
    {
        var now = _timeProvider.UtcNow();
        var entity = new Tenant
        {
            PortfolioId = portfolioId,
            FirstName = request.FirstName,
            LastName = request.LastName,
            Email = request.Email,
            Phone = request.Phone,
            EmergencyContact = request.EmergencyContact,
            DateOfBirth = request.DateOfBirth.ToUtc(),
            Notes = request.Notes,
            CreatedAt = now,
            UpdatedAt = now,
        };

        _db.Tenants.Add(entity);
        await _db.SaveChangesAsync(ct);

        var response = await GetAsync(portfolioId, entity.Id, ct) ?? TenantResponse.FromEntity(entity);
        await _dataUpdate.BroadcastEntityUpdateAsync(portfolioId, EntityType, entity.Id, response, ct);
        return response;
    }

    public async Task<TenantResponse?> CreateAuthorizedAsync(
        WorkspaceReadScope scope,
        CreateTenantRequest request,
        CancellationToken ct = default)
    {
        var entity = await _db.ExecuteAuthorizedMutationAsync(async innerCt =>
        {
            var hasPortfolioWideAccess = await _db.AuthorizedAllPropertyAssignments(
                    scope,
                    [CapabilityKeys.RentalsManage, CapabilityKeys.LeasingOnboardingManage],
                    CapabilityAuthorizationTargetKind.Property,
                    _timeProvider.UtcNow())
                .AnyAsync(innerCt);
            if (!hasPortfolioWideAccess)
            {
                return null;
            }

            var now = _timeProvider.UtcNow();
            var created = new Tenant
            {
                PortfolioId = scope.PortfolioId,
                FirstName = request.FirstName,
                LastName = request.LastName,
                Email = request.Email,
                Phone = request.Phone,
                EmergencyContact = request.EmergencyContact,
                DateOfBirth = request.DateOfBirth.ToUtc(),
                Notes = request.Notes,
                CreatedAt = now,
                UpdatedAt = now,
            };
            _db.Tenants.Add(created);
            await _db.SaveChangesAsync(innerCt);
            return created;
        }, ct);

        if (entity is null)
        {
            return null;
        }

        var response = await GetAsync(scope.PortfolioId, entity.Id, ct) ?? TenantResponse.FromEntity(entity);
        await _dataUpdate.BroadcastEntityUpdateAsync(scope.PortfolioId, EntityType, entity.Id, response, ct);
        return response;
    }

    public async Task<TenantResponse?> UpdateAsync(int portfolioId, int id, UpdateTenantRequest request, CancellationToken ct = default)
    {
        var tenants = _db.Tenants.Where(t => t.Id == id && t.PortfolioId == portfolioId);
        return await UpdateFromQueryAsync(tenants, portfolioId, request, ct);
    }

    public async Task<TenantResponse?> UpdateAuthorizedAsync(
        WorkspaceReadScope scope,
        int id,
        UpdateTenantRequest request,
        CancellationToken ct = default)
    {
        var response = await _db.ExecuteAuthorizedMutationAsync(innerCt =>
        {
            var tenants = _db.Tenants
                .Where(t => t.Id == id)
                .WhereAuthorized(
                    _db,
                    scope,
                    [CapabilityKeys.RentalsManage, CapabilityKeys.LeasingOnboardingManage],
                    _timeProvider.UtcNow());
            return UpdateFromQueryAsync(tenants, scope.PortfolioId, request, innerCt, broadcast: false);
        }, ct);

        if (response is not null)
        {
            await _dataUpdate.BroadcastEntityUpdateAsync(scope.PortfolioId, EntityType, response.Id, response, ct);
        }

        return response;
    }

    private async Task<TenantResponse?> UpdateFromQueryAsync(
        IQueryable<Tenant> tenants,
        int portfolioId,
        UpdateTenantRequest request,
        CancellationToken ct,
        bool broadcast = true)
    {
        var entity = await tenants.FirstOrDefaultAsync(ct);
        if (entity == null)
        {
            return null;
        }

        if (request.FirstName != null) entity.FirstName = request.FirstName;
        if (request.LastName != null) entity.LastName = request.LastName;
        if (request.Email != null) entity.Email = request.Email;
        if (request.Phone != null) entity.Phone = request.Phone;
        if (request.EmergencyContact != null) entity.EmergencyContact = request.EmergencyContact;
        if (request.DateOfBirth.HasValue) entity.DateOfBirth = request.DateOfBirth.ToUtc();
        if (request.Notes != null) entity.Notes = request.Notes;
        entity.UpdatedAt = _timeProvider.UtcNow();

        await _db.SaveChangesAsync(ct);

        var response = await GetAsync(portfolioId, entity.Id, ct) ?? TenantResponse.FromEntity(entity);
        if (broadcast)
        {
            await _dataUpdate.BroadcastEntityUpdateAsync(portfolioId, EntityType, entity.Id, response, ct);
        }
        return response;
    }

    public async Task<bool> DeleteAsync(int portfolioId, int id, CancellationToken ct = default)
    {
        var tenants = _db.Tenants.Where(t => t.Id == id && t.PortfolioId == portfolioId);
        return await DeleteFromQueryAsync(tenants, portfolioId, id, ct);
    }

    public async Task<bool> DeleteAuthorizedAsync(
        WorkspaceReadScope scope,
        int id,
        CancellationToken ct = default)
    {
        var deleted = await _db.ExecuteAuthorizedMutationAsync(innerCt =>
        {
            var tenants = _db.Tenants
                .Where(t => t.Id == id)
                .WhereAuthorized(
                    _db,
                    scope,
                    [CapabilityKeys.RentalsManage, CapabilityKeys.LeasingOnboardingManage],
                    _timeProvider.UtcNow());
            return DeleteFromQueryAsync(tenants, scope.PortfolioId, id, innerCt, broadcast: false);
        }, ct);

        if (deleted)
        {
            await _dataUpdate.BroadcastEntityDeleteAsync(scope.PortfolioId, EntityType, id, ct);
        }

        return deleted;
    }

    private async Task<bool> DeleteFromQueryAsync(
        IQueryable<Tenant> tenants,
        int portfolioId,
        int id,
        CancellationToken ct,
        bool broadcast = true)
    {
        var entity = await tenants.FirstOrDefaultAsync(ct);
        if (entity == null)
        {
            return false;
        }

        var hasOccupyingLease = await _db.LeaseManagementParties.AnyAsync(party =>
            party.PortfolioId == portfolioId
            && party.TenantId == id
            && party.Role != LeaseManagementPartyRole.Guarantor
            && _db.UnitOccupancyProjections.Any(occupancy =>
                occupancy.PortfolioId == portfolioId
                && occupancy.CurrentLeaseManagementId == party.LeaseManagementId)
            && _db.LeaseManagementLifecycleProjections.Any(lifecycle =>
                lifecycle.PortfolioId == portfolioId
                && lifecycle.LeaseManagementId == party.LeaseManagementId
                && party.EffectiveFrom <= lifecycle.BusinessDate
                && (party.EffectiveThrough == null || party.EffectiveThrough >= lifecycle.BusinessDate)), ct);
        if (hasOccupyingLease)
        {
            throw new DomainValidationException(
                ActiveLeaseDeleteBlockedReason,
                StatusCodes.Status409Conflict);
        }

        var hasLeaseHistory = await _db.LeaseManagementParties.AnyAsync(party =>
            party.PortfolioId == portfolioId && party.TenantId == id, ct);
        if (hasLeaseHistory)
        {
            throw new DomainValidationException(
                LeaseHistoryDeleteBlockedReason,
                StatusCodes.Status409Conflict);
        }

        entity.DeletedAt = _timeProvider.UtcNow();
        await _db.SaveChangesAsync(ct);

        if (broadcast)
        {
            await _dataUpdate.BroadcastEntityDeleteAsync(portfolioId, EntityType, id, ct);
        }
        return true;
    }
}
