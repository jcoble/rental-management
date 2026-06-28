using System.Text;
using Microsoft.EntityFrameworkCore;
using RentalCommand.Api.DTOs;
using RentalCommand.Core;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Interfaces;
using RentalCommand.Data;

namespace RentalCommand.Api.Services.Domain;

/// <inheritdoc cref="ITenantService"/>
public class TenantService : ITenantService
{
    private const string EntityType = "Tenant";
    private const int MaxSearchTokens = 8;

    private readonly RentalCommandDbContext _db;
    private readonly IDataUpdateService _dataUpdate;

    public TenantService(RentalCommandDbContext db, IDataUpdateService dataUpdate)
    {
        _db = db;
        _dataUpdate = dataUpdate;
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
            "activeleasecount" => query.SortDescending ? q.OrderByDescending(t => t.Leases.Count(l => l.Status == LeaseStatus.Active)).ThenBy(t => t.LastName).ThenBy(t => t.FirstName) : q.OrderBy(t => t.Leases.Count(l => l.Status == LeaseStatus.Active)).ThenBy(t => t.LastName).ThenBy(t => t.FirstName),
            "createdat" => query.SortDescending ? q.OrderByDescending(t => t.CreatedAt) : q.OrderBy(t => t.CreatedAt),
            "updatedat" => query.SortDescending ? q.OrderByDescending(t => t.UpdatedAt) : q.OrderBy(t => t.UpdatedAt),
            _ => query.SortDescending ? q.OrderByDescending(t => t.CreatedAt) : q.OrderBy(t => t.CreatedAt),
        };

        var totalCount = await q.CountAsync(ct);

        // Project the active-lease count as a correlated subquery in the SAME page query (EF-translated),
        // so the count comes back per-row from Postgres — never load-then-count in C#.
        var rows = await q
            .Skip(query.NormalizedSkip)
            .Take(query.NormalizedTake)
            .Select(t => new
            {
                Entity = t,
                ActiveLeaseCount = t.Leases.Count(l => l.Status == LeaseStatus.Active),
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
                ActiveLeaseCount = t.Leases.Count(l => l.Status == LeaseStatus.Active),
            })
            .FirstOrDefaultAsync(ct);

        if (row == null)
        {
            return null;
        }

        var response = TenantResponse.FromEntity(row.Entity);
        response.ActiveLeaseCount = row.ActiveLeaseCount;
        return response;
    }

    public async Task<TenantResponse> CreateAsync(int portfolioId, CreateTenantRequest request, CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
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

        var response = TenantResponse.FromEntity(entity);
        await _dataUpdate.BroadcastEntityUpdateAsync(portfolioId, EntityType, entity.Id, response, ct);
        return response;
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
        entity.UpdatedAt = DateTime.UtcNow;

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

        // Block the soft-delete while the tenant still holds an Active lease. Otherwise the lease keeps
        // occupying its unit but 404s in the UI — Include(Tenant) inner-joins through the tenant's
        // soft-delete query filter, so the orphaned lease becomes invisible yet stays Active. Same
        // ActiveLeaseCount predicate the read model uses; evaluated SQL-side as an EXISTS (the global
        // query filter already excludes soft-deleted leases).
        var hasActiveLease = await _db.Leases
            .AnyAsync(l => l.TenantId == id
                && l.PortfolioId == portfolioId
                && l.Status == LeaseStatus.Active, ct);
        if (hasActiveLease)
        {
            throw new DomainValidationException(
                "This tenant has an active lease; end or reassign it first.");
        }

        entity.DeletedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);

        await _dataUpdate.BroadcastEntityDeleteAsync(portfolioId, EntityType, id, ct);
        return true;
    }
}
