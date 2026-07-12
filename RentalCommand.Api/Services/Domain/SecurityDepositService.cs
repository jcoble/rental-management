using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using RentalCommand.Api.DTOs;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Interfaces;
using RentalCommand.Core.Time;
using RentalCommand.Data;

namespace RentalCommand.Api.Services.Domain;

/// <inheritdoc cref="ISecurityDepositService"/>
public class SecurityDepositService : ISecurityDepositService
{
    /// <summary>StoredFile.EntityType used for photos attached to a security-deposit account.</summary>
    internal const string DepositEntityType = "SecurityDeposit";

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

        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var term = query.Search.Trim();
            var like = $"%{term}%";
            q = q.Where(row =>
                EF.Functions.ILike(row.AccountNumber, like) ||
                EF.Functions.ILike(row.RelationshipNumber, like) ||
                (row.TenantName != null && EF.Functions.ILike(row.TenantName, like)) ||
                (row.PropertyName != null && EF.Functions.ILike(row.PropertyName, like)) ||
                (row.UnitNumber != null && EF.Functions.ILike(row.UnitNumber, like)));
        }

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
        // The statement header, relationship, legal agreement, party, property, and derived
        // deposit totals are deliberately projected by one translated SQL statement. Do not
        // replace this with navigation loading or per-row lookups.
        var header = await (
            from deposit in _db.SecurityDepositAccounts.AsNoTracking()
            join balance in _db.SecurityDepositBalanceProjections.AsNoTracking()
                on new { deposit.PortfolioId, SecurityDepositAccountId = deposit.Id }
                equals new { balance.PortfolioId, balance.SecurityDepositAccountId }
            join account in _db.TenantAccounts.AsNoTracking()
                on new { deposit.PortfolioId, Id = deposit.TenantAccountId }
                equals new { account.PortfolioId, account.Id }
            join management in _db.LeaseManagements.AsNoTracking()
                on new { account.PortfolioId, Id = account.LeaseManagementId }
                equals new { management.PortfolioId, management.Id }
            join agreement in _db.LeaseAgreements.AsNoTracking()
                on new { deposit.PortfolioId, Id = deposit.OriginatingAgreementId }
                equals new { agreement.PortfolioId, agreement.Id }
            join property in _db.Properties.IgnoreQueryFilters().AsNoTracking()
                on new { management.PortfolioId, Id = management.PropertyId }
                equals new { property.PortfolioId, property.Id }
            join unit in _db.Units.IgnoreQueryFilters().AsNoTracking()
                on new { management.PortfolioId, Id = management.UnitId }
                equals new { unit.PortfolioId, unit.Id }
            join portfolio in _db.Portfolios.AsNoTracking()
                on deposit.PortfolioId equals portfolio.Id
            where deposit.Id == id && deposit.PortfolioId == portfolioId
            select new
            {
                portfolio.Name,
                portfolio.ManagementCompanyName,
                TenantName = _db.Tenants.IgnoreQueryFilters().AsNoTracking()
                    .Where(tenant => tenant.PortfolioId == deposit.PortfolioId
                        && tenant.Id == _db.LeaseManagementParties.AsNoTracking()
                            .Where(party => party.PortfolioId == deposit.PortfolioId
                                && party.LeaseManagementId == management.Id
                                && party.Role == LeaseManagementPartyRole.PrimaryTenant)
                            .OrderByDescending(party => party.EffectiveFrom)
                            .ThenByDescending(party => party.Id)
                            .Select(party => party.TenantId)
                            .FirstOrDefault())
                    .Select(tenant => tenant.FirstName + " " + tenant.LastName)
                    .FirstOrDefault(),
                PropertyName = property.Name,
                property.AddressLine1,
                property.City,
                property.State,
                property.PostalCode,
                unit.UnitNumber,
                agreement.AgreementNumber,
                management.PossessionReturnedAtUtc,
                management.PlannedMoveOutAtUtc,
                balance.TotalDeductions,
                balance.TotalRefunded,
                balance.HeldBalance,
                LastRefundedAtUtc = _db.SecurityDepositEntries.AsNoTracking()
                    .Where(entry => entry.PortfolioId == deposit.PortfolioId
                        && entry.SecurityDepositAccountId == deposit.Id
                        && entry.EntryType == SecurityDepositEntryType.Refund)
                    .Max(entry => (DateTime?)entry.PostedAtUtc),
            }).SingleOrDefaultAsync(ct);

        if (header == null)
            return null;

        // Reversals stay immutable. Net each original deduction against its reversal entries in
        // SQL so the legal statement never lists a deduction that has been fully undone.
        var deductions = await _db.SecurityDepositEntries
            .AsNoTracking()
            .Where(entry => entry.PortfolioId == portfolioId
                && entry.SecurityDepositAccountId == id
                && entry.EntryType == SecurityDepositEntryType.Deduction)
            .Select(entry => new
            {
                entry.Description,
                entry.EffectiveOn,
                entry.Id,
                NetAmount = entry.Amount - (_db.SecurityDepositEntries
                    .Where(reversal => reversal.PortfolioId == entry.PortfolioId
                        && reversal.SecurityDepositAccountId == entry.SecurityDepositAccountId
                        && reversal.EntryType == SecurityDepositEntryType.Reversal
                        && reversal.ReversesEntryId == entry.Id)
                    .Sum(reversal => (decimal?)reversal.Amount) ?? 0m),
            })
            .Where(entry => entry.NetAmount > 0m)
            .OrderBy(entry => entry.EffectiveOn)
            .ThenBy(entry => entry.Id)
            .Select(entry => new DepositDeduction(entry.Description, entry.NetAmount, null))
            .ToListAsync(ct);

        var photos = await LoadDepositPhotosAsync(portfolioId, id, ct);

        var propertyLine =
            $"{header.PropertyName} — {header.AddressLine1}, {header.City}, {header.State} {header.PostalCode}"
                .Trim(' ', '—');
        // Reconstruct the corpus relevant to this relationship. This remains the original held
        // amount after a tenant refund, while a transfer-out is not misreported as money still
        // refundable from the source relationship.
        var depositHeld = Math.Max(
            header.HeldBalance + header.TotalDeductions + header.TotalRefunded,
            0m);

        var data = new MoveOutStatementData
        {
            ManagementCompanyName = header.ManagementCompanyName,
            PortfolioName = header.Name,
            TenantName = header.TenantName?.Trim() ?? string.Empty,
            PropertyLine = propertyLine,
            UnitLine = $"Unit {header.UnitNumber}",
            LeaseNumber = header.AgreementNumber,
            StatementDate = header.LastRefundedAtUtc ?? _timeProvider.UtcNow(),
            MoveOutDate = header.PossessionReturnedAtUtc ?? header.PlannedMoveOutAtUtc,
            DepositHeld = depositHeld,
            Deductions = deductions,
            Photos = photos,
        };

        return _pdf.Generate(data);
    }

    /// <summary>
    /// Loads decoded image bytes for photos attached to this deposit account (StoredFile rows with
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
                    "Could not load deposit photo {FileId} for account {DepositId}; omitting from statement.",
                    file.Id, depositId);
            }
        }

        return photos;
    }
}
