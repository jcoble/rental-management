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
    private readonly ILogger<SecurityDepositService> _logger;
    private readonly TimeProvider _timeProvider;

    public SecurityDepositService(
        RentalCommandDbContext db,
        IFileStorage storage,
        IMoveOutStatementPdfGenerator pdf,
        ILogger<SecurityDepositService> logger,
        TimeProvider timeProvider)
    {
        _db = db;
        _storage = storage;
        _pdf = pdf;
        _logger = logger;
        _timeProvider = timeProvider;
    }

    public async Task<IReadOnlyList<SecurityDepositAccountResponse>> ListAsync(int portfolioId, int? leaseManagementId, CancellationToken ct = default)
    {
        var page = await ListPageAsync(portfolioId, leaseManagementId, new ListQuery(), ct);
        return page.Items;
    }

    public async Task<SecurityDepositListResponse> ListPageAsync(int portfolioId, int? leaseManagementId, ListQuery query, CancellationToken ct = default)
    {
        var q = BuildAccountQuery(portfolioId);

        if (leaseManagementId.HasValue)
            q = q.Where(row => row.LeaseManagementId == leaseManagementId.Value);

        q = query.SortField switch
        {
            "account" => query.SortDescending ? q.OrderByDescending(row => row.AccountNumber) : q.OrderBy(row => row.AccountNumber),
            "amount" => query.SortDescending ? q.OrderByDescending(row => row.HeldBalance) : q.OrderBy(row => row.HeldBalance),
            "createdat" => query.SortDescending ? q.OrderByDescending(row => row.CreatedAtUtc) : q.OrderBy(row => row.CreatedAtUtc),
            _ => query.SortDescending ? q.OrderBy(row => row.CreatedAtUtc) : q.OrderByDescending(row => row.CreatedAtUtc),
        };

        var totalCount = await q.CountAsync(ct);

        var items = await q
            .Skip(query.NormalizedSkip)
            .Take(query.NormalizedTake)
            .ToListAsync(ct);

        return new SecurityDepositListResponse
        {
            Items = items,
            TotalCount = totalCount,
            Skip = query.NormalizedSkip,
            Take = query.NormalizedTake,
        };
    }

    public Task<SecurityDepositAccountResponse?> GetAsync(int portfolioId, int id, CancellationToken ct = default)
    {
        return BuildAccountQuery(portfolioId).SingleOrDefaultAsync(row => row.Id == id, ct);
    }

    internal IQueryable<SecurityDepositAccountResponse> BuildAccountQuery(int portfolioId)
    {
        return
            from balance in _db.SecurityDepositBalanceProjections.AsNoTracking()
            join account in _db.TenantAccounts.AsNoTracking()
                on new { balance.PortfolioId, Id = balance.TenantAccountId }
                equals new { account.PortfolioId, account.Id }
            join deposit in _db.SecurityDepositAccounts.AsNoTracking()
                on new { balance.PortfolioId, Id = balance.SecurityDepositAccountId }
                equals new { deposit.PortfolioId, deposit.Id }
            join management in _db.LeaseManagements.AsNoTracking()
                on new { account.PortfolioId, Id = account.LeaseManagementId }
                equals new { management.PortfolioId, management.Id }
            join lifecycle in _db.LeaseManagementLifecycleProjections.AsNoTracking()
                on new { management.PortfolioId, LeaseManagementId = management.Id }
                equals new { lifecycle.PortfolioId, lifecycle.LeaseManagementId }
            where balance.PortfolioId == portfolioId
            select new SecurityDepositAccountResponse
            {
                Id = deposit.Id,
                PortfolioId = deposit.PortfolioId,
                TenantAccountId = account.Id,
                LeaseManagementId = management.Id,
                OriginatingAgreementId = deposit.OriginatingAgreementId,
                PropertyId = management.PropertyId,
                UnitId = management.UnitId,
                AccountNumber = account.AccountNumber,
                RelationshipNumber = management.RelationshipNumber,
                TenantName = lifecycle.CurrentPrimaryTenantName,
                PropertyName = management.Property!.Name,
                UnitNumber = management.Unit!.UnitNumber,
                Currency = balance.Currency,
                TotalReceived = balance.TotalReceived,
                TotalDeductions = balance.TotalDeductions,
                TotalRefunded = balance.TotalRefunded,
                HeldBalance = balance.HeldBalance,
                Status = balance.DepositStatus,
                CreatedAtUtc = deposit.CreatedAtUtc,
            };
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
