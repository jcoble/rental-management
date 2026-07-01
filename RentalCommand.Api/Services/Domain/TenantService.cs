using System.Text;
using Microsoft.EntityFrameworkCore;
using RentalCommand.Api.DTOs;
using RentalCommand.Api.Services.Auth;
using RentalCommand.Core;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Interfaces;
using RentalCommand.Core.Time;
using RentalCommand.Data;

namespace RentalCommand.Api.Services.Domain;

/// <inheritdoc cref="ITenantService"/>
public class TenantService : ITenantService
{
    private const string EntityType = "Tenant";
    private const int MaxSearchTokens = 8;

    private readonly RentalCommandDbContext _db;
    private readonly IDataUpdateService _dataUpdate;
    private readonly ITenantPortalProvisioningService _portalProvisioning;
    private readonly ILogger<TenantService> _logger;
    private readonly TimeProvider _timeProvider;

    public TenantService(
        RentalCommandDbContext db,
        IDataUpdateService dataUpdate,
        ITenantPortalProvisioningService portalProvisioning,
        ILogger<TenantService> logger,
        TimeProvider timeProvider)
    {
        _db = db;
        _dataUpdate = dataUpdate;
        _portalProvisioning = portalProvisioning;
        _logger = logger;
        _timeProvider = timeProvider;
    }

    public async Task<IReadOnlyList<TenantResponse>> ListAsync(int portfolioId, ListQuery query, CancellationToken ct = default)
    {
        var page = await ListPageAsync(portfolioId, ToTenantListQuery(query), ct);
        return page.Items;
    }

    public async Task<TenantListResponse> ListPageAsync(int portfolioId, TenantListQuery query, CancellationToken ct = default)
    {
        var q = _db.Tenants
            .AsNoTracking()
            .Where(t => t.PortfolioId == portfolioId);

        if (query.AvailableForLease == true)
        {
            q = q.Where(t =>
                !t.Leases.Any(l => l.PortfolioId == portfolioId
                    && (l.Status == LeaseStatus.Active || l.Status == LeaseStatus.NoticeGiven)) &&
                !t.LeaseTenants.Any(lt => lt.PortfolioId == portfolioId
                    && lt.Lease != null
                    && (lt.Lease.Status == LeaseStatus.Active || lt.Lease.Status == LeaseStatus.NoticeGiven)));
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
            "activeleasecount" => query.SortDescending ? q.OrderByDescending(t => t.Leases.Count(l => l.Status == LeaseStatus.Active || l.Status == LeaseStatus.NoticeGiven)).ThenBy(t => t.LastName).ThenBy(t => t.FirstName) : q.OrderBy(t => t.Leases.Count(l => l.Status == LeaseStatus.Active || l.Status == LeaseStatus.NoticeGiven)).ThenBy(t => t.LastName).ThenBy(t => t.FirstName),
            "createdat" => query.SortDescending ? q.OrderByDescending(t => t.CreatedAt) : q.OrderBy(t => t.CreatedAt),
            "updatedat" => query.SortDescending ? q.OrderByDescending(t => t.UpdatedAt) : q.OrderBy(t => t.UpdatedAt),
            _ => query.SortDescending ? q.OrderByDescending(t => t.CreatedAt) : q.OrderBy(t => t.CreatedAt),
        };

        var totalCount = await q.CountAsync(ct);

        // Project the occupying-lease count (Active + NoticeGiven — a lease in notice is still in force
        // and occupies its unit, treated as the current lease elsewhere) as a correlated subquery in the
        // SAME page query (EF-translated), so the count comes back per-row from Postgres — never
        // load-then-count in C#. This is the same occupancy signal the delete guard uses, so the UI can
        // disable delete for a tenant who still occupies a unit (including a notice-given-only tenant).
        var rows = await q
            .Skip(query.NormalizedSkip)
            .Take(query.NormalizedTake)
            .Select(t => new
            {
                Entity = t,
                ActiveLeaseCount = t.Leases.Count(l => l.Status == LeaseStatus.Active || l.Status == LeaseStatus.NoticeGiven),
            })
            .ToListAsync(ct);

        return new TenantListResponse
        {
            Items = rows.Select(r =>
            {
                var response = TenantResponse.FromEntity(r.Entity);
                response.ActiveLeaseCount = r.ActiveLeaseCount;
                return response;
            }).ToList(),
            TotalCount = totalCount,
            Skip = query.NormalizedSkip,
            Take = query.NormalizedTake,
        };
    }

    private static TenantListQuery ToTenantListQuery(ListQuery query) => new()
    {
        Skip = query.Skip,
        Take = query.Take,
        Search = query.Search,
        Sort = query.Sort,
    };

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
        var row = await _db.Tenants
            .AsNoTracking()
            .Where(t => t.Id == id && t.PortfolioId == portfolioId)
            .Select(t => new
            {
                Entity = t,
                ActiveLeaseCount = t.Leases.Count(l => l.Status == LeaseStatus.Active || l.Status == LeaseStatus.NoticeGiven),
                // Portal-login state: existence + lockout of the Identity user linked to this tenant,
                // fetched DB-side as correlated subqueries in the SAME query (no follow-up round trip).
                HasPortalUser = _db.Users.Any(u => u.TenantId == t.Id && u.PortfolioId == portfolioId),
                PortalLockoutEnd = _db.Users
                    .Where(u => u.TenantId == t.Id && u.PortfolioId == portfolioId)
                    .Select(u => u.LockoutEnd)
                    .FirstOrDefault(),
            })
            .FirstOrDefaultAsync(ct);

        if (row == null)
        {
            return null;
        }

        var response = TenantResponse.FromEntity(row.Entity);
        response.ActiveLeaseCount = row.ActiveLeaseCount;
        // Classify the single fetched row (no cross-row work): no user → none; locked-off → disabled.
        response.PortalAccess = !row.HasPortalUser
            ? "none"
            : TenantPortalProvisioningService.IsPortalDisabled(row.PortalLockoutEnd) ? "disabled" : "active";
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

        // A tenant is a first-class portal user: provision their Identity login the moment they're
        // created. Silent — no email goes out here (staff send the invite on demand). Best-effort: a
        // tenant with no email yet is a normal no-op (NoEmail), and a provisioning hiccup must never
        // fail tenant creation.
        await TryProvisionPortalAccessAsync(entity.Id, portfolioId, ct);

        var response = TenantResponse.FromEntity(entity);
        await _dataUpdate.BroadcastEntityUpdateAsync(portfolioId, EntityType, entity.Id, response, ct);
        return response;
    }

    /// <summary>
    /// Best-effort: ensure a freshly created tenant has a portal login. A failure is logged and
    /// swallowed so it can never fail the tenant creation that already succeeded.
    /// </summary>
    private async Task TryProvisionPortalAccessAsync(int tenantId, int portfolioId, CancellationToken ct)
    {
        try
        {
            await _portalProvisioning.EnsurePortalAccountForTenantAsync(tenantId, portfolioId, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Failed to provision portal access for new tenant {TenantId} in portfolio {PortfolioId}; tenant creation still succeeds.",
                tenantId, portfolioId);
        }
    }

    public async Task<TenantResponse?> UpdateAsync(int portfolioId, int id, UpdateTenantRequest request, CancellationToken ct = default)
    {
        var entity = await _db.Tenants
            .FirstOrDefaultAsync(t => t.Id == id && t.PortfolioId == portfolioId, ct);
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

        var response = TenantResponse.FromEntity(entity);
        await _dataUpdate.BroadcastEntityUpdateAsync(portfolioId, EntityType, entity.Id, response, ct);
        return response;
    }

    public async Task<bool> DeleteAsync(int portfolioId, int id, CancellationToken ct = default)
    {
        var entity = await _db.Tenants
            .FirstOrDefaultAsync(t => t.Id == id && t.PortfolioId == portfolioId, ct);
        if (entity == null)
        {
            return false;
        }

        // Block the soft-delete while the tenant still occupies a unit. A lease in Active OR NoticeGiven
        // is still in force and occupying — a notice-given lease is treated as the current lease
        // everywhere else (unit health badge, "Move-Out" stage). Otherwise the lease keeps occupying its
        // unit but 404s in the UI — Include(Tenant) inner-joins through the tenant's soft-delete query
        // filter, so the orphaned lease becomes invisible yet stays in force. Same occupancy predicate
        // the read model's ActiveLeaseCount uses; evaluated SQL-side as an EXISTS (the global query
        // filter already excludes soft-deleted leases).
        var hasOccupyingLease = await _db.Leases
            .AnyAsync(l => l.TenantId == id
                && l.PortfolioId == portfolioId
                && (l.Status == LeaseStatus.Active || l.Status == LeaseStatus.NoticeGiven), ct);
        if (hasOccupyingLease)
        {
            throw new DomainValidationException(
                "This tenant has an active lease; end or reassign it first.");
        }

        entity.DeletedAt = _timeProvider.UtcNow();
        await _db.SaveChangesAsync(ct);

        await _dataUpdate.BroadcastEntityDeleteAsync(portfolioId, EntityType, id, ct);
        return true;
    }
}
