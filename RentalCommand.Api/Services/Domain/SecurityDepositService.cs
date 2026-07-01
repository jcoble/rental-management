using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using RentalCommand.Api.DTOs;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Interfaces;
using RentalCommand.Core.Time;
using RentalCommand.Data;

namespace RentalCommand.Api.Services.Domain;

/// <inheritdoc cref="ISecurityDepositService"/>
public class SecurityDepositService : ISecurityDepositService
{
    /// <summary>StoredFile.EntityType used for photos attached to a security-deposit holding.</summary>
    internal const string DepositEntityType = "SecurityDeposit";

    private static readonly JsonSerializerOptions _jsonOptions = new() { PropertyNameCaseInsensitive = true };

    private readonly RentalCommandDbContext _db;
    private readonly IFileStorage _storage;
    private readonly IMoveOutStatementPdfGenerator _pdf;
    private readonly IAuditTrailService _audit;
    private readonly ICurrentActor _actor;
    private readonly ILogger<SecurityDepositService> _logger;
    private readonly TimeProvider _timeProvider;

    public SecurityDepositService(
        RentalCommandDbContext db,
        IFileStorage storage,
        IMoveOutStatementPdfGenerator pdf,
        IAuditTrailService audit,
        ICurrentActor actor,
        ILogger<SecurityDepositService> logger,
        TimeProvider timeProvider)
    {
        _db = db;
        _storage = storage;
        _pdf = pdf;
        _audit = audit;
        _actor = actor;
        _logger = logger;
        _timeProvider = timeProvider;
    }

    // Deposits are NOT marked IAuditable (no generic twin), so these explicit rows are the sole audit
    // for legally-sensitive deposit lifecycle events — holding, move-out deductions, and refunds — and
    // must carry the actor/IP themselves (resolved from ICurrentActor).
    private Task LogDepositAsync(int portfolioId, int id, AuditLogOperation operation,
        string? oldValues, string? newValues, string changeReason, CancellationToken ct) =>
        _audit.LogAsync(portfolioId, DepositEntityType, id, operation,
            userId: _actor.UserId, actorLabel: _actor.ActorLabel, ipAddress: _actor.IpAddress,
            oldValues: oldValues, newValues: newValues, changeReason: changeReason, ct: ct);

    public async Task<IReadOnlyList<SecurityDepositResponse>> ListAsync(int portfolioId, int? leaseId, CancellationToken ct = default)
    {
        var page = await ListPageAsync(portfolioId, leaseId, new ListQuery(), ct);
        return page.Items;
    }

    public async Task<SecurityDepositListResponse> ListPageAsync(int portfolioId, int? leaseId, ListQuery query, CancellationToken ct = default)
    {
        var q = _db.SecurityDepositHoldings
            .AsNoTracking()
            .Include(h => h.Lease)!.ThenInclude(l => l!.Tenant)
            .Where(h => h.PortfolioId == portfolioId);

        if (leaseId.HasValue)
            q = q.Where(h => h.LeaseId == leaseId.Value);

        q = query.SortField switch
        {
            "lease" => query.SortDescending ? q.OrderByDescending(h => h.Lease!.LeaseNumber) : q.OrderBy(h => h.Lease!.LeaseNumber),
            "leasenumber" => query.SortDescending ? q.OrderByDescending(h => h.Lease!.LeaseNumber) : q.OrderBy(h => h.Lease!.LeaseNumber),
            "amount" => query.SortDescending ? q.OrderByDescending(h => h.Amount) : q.OrderBy(h => h.Amount),
            "heldat" => query.SortDescending ? q.OrderByDescending(h => h.HeldAt) : q.OrderBy(h => h.HeldAt),
            "createdat" => query.SortDescending ? q.OrderByDescending(h => h.CreatedAt) : q.OrderBy(h => h.CreatedAt),
            _ => query.SortDescending ? q.OrderBy(h => h.CreatedAt) : q.OrderByDescending(h => h.CreatedAt),
        };

        var totalCount = await q.CountAsync(ct);

        var items = await q
            .Skip(query.NormalizedSkip)
            .Take(query.NormalizedTake)
            .ToListAsync(ct);

        return new SecurityDepositListResponse
        {
            Items = items.Select(SecurityDepositResponse.FromEntity).ToList(),
            TotalCount = totalCount,
            Skip = query.NormalizedSkip,
            Take = query.NormalizedTake,
        };
    }

    public async Task<SecurityDepositResponse?> GetAsync(int portfolioId, int id, CancellationToken ct = default)
    {
        var entity = await _db.SecurityDepositHoldings
            .AsNoTracking()
            .Include(h => h.Lease)!.ThenInclude(l => l!.Tenant)
            .FirstOrDefaultAsync(h => h.Id == id && h.PortfolioId == portfolioId, ct);

        return entity == null ? null : SecurityDepositResponse.FromEntity(entity);
    }

    public async Task<SecurityDepositResponse?> CreateAsync(int portfolioId, CreateDepositRequest request, CancellationToken ct = default)
    {
        var existing = await _db.SecurityDepositHoldings
            .Include(h => h.Lease).ThenInclude(l => l!.Tenant)
            .FirstOrDefaultAsync(h => h.PortfolioId == portfolioId && h.LeaseId == request.LeaseId, ct);
        if (existing is not null)
        {
            return SecurityDepositResponse.FromEntity(existing);
        }

        // Verify the lease belongs to this portfolio. Pull the tenant nav too so the create
        // response carries the tenant name for the grid row (Lease / Tenant column).
        var lease = await _db.Leases
            .AsNoTracking()
            .Include(l => l.Tenant)
            .FirstOrDefaultAsync(l => l.Id == request.LeaseId && l.PortfolioId == portfolioId, ct);

        if (lease == null)
            return null;

        var now = _timeProvider.UtcNow();
        var entity = new SecurityDepositHolding
        {
            PortfolioId = portfolioId,
            LeaseId = request.LeaseId,
            Amount = request.Amount ?? lease.SecurityDeposit,
            Status = SecurityDepositStatus.Held,
            HeldAt = now,
            DeductionsJson = "[]",
            Notes = request.Notes,
            CreatedAt = now,
            UpdatedAt = now,
        };

        _db.SecurityDepositHoldings.Add(entity);
        await _db.SaveChangesAsync(ct);

        await LogDepositAsync(portfolioId, entity.Id, AuditLogOperation.Created,
            oldValues: null,
            newValues: JsonSerializer.Serialize(new
            {
                leaseId = entity.LeaseId,
                amount = entity.Amount,
                status = entity.Status.ToString(),
                heldAt = entity.HeldAt,
            }),
            changeReason: $"Security deposit held for lease #{entity.LeaseId} (${entity.Amount:0.##})", ct);

        // Reload with navigation for response.
        entity.Lease = lease;
        return SecurityDepositResponse.FromEntity(entity);
    }

    public async Task<SecurityDepositResponse?> AddDeductionAsync(int portfolioId, int id, AddDeductionRequest request, CancellationToken ct = default)
    {
        var entity = await _db.SecurityDepositHoldings
            .Include(h => h.Lease)!.ThenInclude(l => l!.Tenant)
            .FirstOrDefaultAsync(h => h.Id == id && h.PortfolioId == portfolioId, ct);

        if (entity == null)
            return null;

        // Reject if already returned.
        if (entity.Status is SecurityDepositStatus.Returned or SecurityDepositStatus.PartiallyReturned)
            return null;

        var deductions = string.IsNullOrWhiteSpace(entity.DeductionsJson)
            ? new List<DepositDeduction>()
            : JsonSerializer.Deserialize<List<DepositDeduction>>(entity.DeductionsJson, _jsonOptions) ?? [];

        var deductionsBefore = entity.DeductionsJson;
        var totalBefore = entity.DeductionsTotal;

        deductions.Add(new DepositDeduction(request.Reason, request.Amount, request.Notes));

        entity.DeductionsJson = JsonSerializer.Serialize(deductions);
        entity.DeductionsTotal = deductions.Sum(d => d.Amount);
        entity.UpdatedAt = _timeProvider.UtcNow();

        await _db.SaveChangesAsync(ct);

        await LogDepositAsync(portfolioId, entity.Id, AuditLogOperation.Updated,
            oldValues: JsonSerializer.Serialize(new { deductions = deductionsBefore, totalDeductions = totalBefore }),
            newValues: JsonSerializer.Serialize(new { deductions = entity.DeductionsJson, totalDeductions = entity.DeductionsTotal }),
            changeReason: $"Deposit deduction added: {request.Reason} (${request.Amount:0.##})", ct);

        return SecurityDepositResponse.FromEntity(entity);
    }

    public async Task<SecurityDepositResponse?> ReturnAsync(int portfolioId, int id, ReturnDepositRequest request, CancellationToken ct = default)
    {
        var entity = await _db.SecurityDepositHoldings
            .Include(h => h.Lease)!.ThenInclude(l => l!.Tenant)
            .FirstOrDefaultAsync(h => h.Id == id && h.PortfolioId == portfolioId, ct);

        if (entity == null)
            return null;

        // Reject if already returned.
        if (entity.Status is SecurityDepositStatus.Returned or SecurityDepositStatus.PartiallyReturned)
            return null;

        var totalDeductions = entity.DeductionsTotal;
        var net = Math.Max(0m, entity.Amount - totalDeductions);

        entity.ReturnedAmount = net;
        entity.ReturnedAt = _timeProvider.UtcNow();
        // Terminal status keys off the deductions taken and what (if anything) actually went back:
        //   no deductions                 -> the full deposit was returned      -> Returned
        //   deductions, net refund > 0    -> the landlord kept part of it       -> PartiallyReturned
        //   deductions consume it all (net == 0) -> nothing was returned        -> Withheld
        entity.Status = totalDeductions <= 0m
            ? SecurityDepositStatus.Returned
            : net > 0m
                ? SecurityDepositStatus.PartiallyReturned
                : SecurityDepositStatus.Withheld;
        if (request.Notes != null)
            entity.Notes = request.Notes;
        entity.UpdatedAt = _timeProvider.UtcNow();

        await _db.SaveChangesAsync(ct);

        await LogDepositAsync(portfolioId, entity.Id, AuditLogOperation.Updated,
            oldValues: JsonSerializer.Serialize(new { status = SecurityDepositStatus.Held.ToString(), amount = entity.Amount, returnedAmount = (decimal?)null }),
            newValues: JsonSerializer.Serialize(new
            {
                status = entity.Status.ToString(),
                amount = entity.Amount,
                returnedAmount = net,
                totalDeductions,
                deductions = entity.DeductionsJson,
            }),
            changeReason: $"Deposit returned: ${net:0.##} of ${entity.Amount:0.##} (deductions ${totalDeductions:0.##})", ct);

        return SecurityDepositResponse.FromEntity(entity);
    }

    public async Task<byte[]?> GetMoveOutStatementAsync(int portfolioId, int id, CancellationToken ct = default)
    {
        var entity = await _db.SecurityDepositHoldings
            .AsNoTracking()
            .Include(h => h.Lease)!.ThenInclude(l => l!.Tenant)
            .Include(h => h.Lease)!.ThenInclude(l => l!.Property)
            .Include(h => h.Lease)!.ThenInclude(l => l!.Unit)
            .FirstOrDefaultAsync(h => h.Id == id && h.PortfolioId == portfolioId, ct);

        if (entity == null)
            return null;

        var deductions = string.IsNullOrWhiteSpace(entity.DeductionsJson)
            ? new List<DepositDeduction>()
            : JsonSerializer.Deserialize<List<DepositDeduction>>(entity.DeductionsJson, _jsonOptions) ?? [];

        var lease = entity.Lease;
        var tenant = lease?.Tenant;
        var property = lease?.Property;
        var unit = lease?.Unit;

        var tenantName = tenant == null
            ? string.Empty
            : $"{tenant.FirstName} {tenant.LastName}".Trim();

        var propertyLine = property == null
            ? $"Property #{lease?.PropertyId}"
            : $"{property.Name} — {property.AddressLine1}, {property.City}, {property.State} {property.PostalCode}".Trim(' ', '—');

        var company = await _db.Portfolios
            .AsNoTracking()
            .Where(p => p.Id == portfolioId)
            .Select(p => new { p.Name, p.ManagementCompanyName })
            .FirstOrDefaultAsync(ct);

        var photos = await LoadDepositPhotosAsync(portfolioId, id, ct);

        var data = new MoveOutStatementData
        {
            ManagementCompanyName = company?.ManagementCompanyName,
            PortfolioName = company?.Name,
            TenantName = tenantName,
            PropertyLine = propertyLine,
            UnitLine = unit == null ? null : $"Unit {unit.UnitNumber}",
            LeaseNumber = lease?.LeaseNumber,
            StatementDate = entity.ReturnedAt ?? _timeProvider.UtcNow(),
            MoveOutDate = lease?.MoveOutDate,
            DepositHeld = entity.Amount,
            Deductions = deductions,
            Photos = photos,
            Notes = entity.Notes,
        };

        return _pdf.Generate(data);
    }

    /// <summary>
    /// Loads decoded image bytes for photos attached to this deposit holding (StoredFile rows with
    /// EntityType=SecurityDeposit). Best-effort: a file that fails to load is skipped, never fatal.
    /// </summary>
    private async Task<IReadOnlyList<byte[]>> LoadDepositPhotosAsync(int portfolioId, int depositId, CancellationToken ct)
    {
        var files = await _db.StoredFiles
            .AsNoTracking()
            .Where(f => f.PortfolioId == portfolioId
                        && f.EntityType == DepositEntityType
                        && f.EntityId == depositId
                        && f.DeletedAt == null
                        && f.ContentType.StartsWith("image/"))
            .OrderBy(f => f.UploadedAt)
            .ToListAsync(ct);

        var photos = new List<byte[]>(files.Count);
        foreach (var file in files)
        {
            try
            {
                await using var s = await _storage.DownloadAsync(file.FilePath, ct);
                using var ms = new MemoryStream();
                await s.CopyToAsync(ms, ct);
                photos.Add(ms.ToArray());
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Could not load deposit photo {FileId} for holding {DepositId}; omitting from statement.",
                    file.Id, depositId);
            }
        }

        return photos;
    }
}
