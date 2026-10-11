using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using RentalCommand.Core.Authorization;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Interfaces;
using RentalCommand.Core.Time;
using RentalCommand.Data;
using RentalCommand.Data.Authorization;

namespace RentalCommand.Api.Services.Domain;

/// <inheritdoc cref="ITenantAccountMoveOutStatementService"/>
public sealed class TenantAccountMoveOutStatementService : ITenantAccountMoveOutStatementService
{
    /// <summary>StoredFile.EntityType used for photos attached to a security-deposit account.</summary>
    internal const string DepositEntityType = nameof(RentalCommand.Core.Entities.SecurityDepositAccount);

    private readonly RentalCommandDbContext _db;
    private readonly IFileStorage _storage;
    private readonly IMoveOutStatementPdfGenerator _pdf;
    private readonly ILogger<TenantAccountMoveOutStatementService> _logger;
    private readonly TimeProvider _timeProvider;

    public TenantAccountMoveOutStatementService(
        RentalCommandDbContext db,
        IFileStorage storage,
        IMoveOutStatementPdfGenerator pdf,
        ILogger<TenantAccountMoveOutStatementService> logger,
        TimeProvider timeProvider)
    {
        _db = db;
        _storage = storage;
        _pdf = pdf;
        _logger = logger;
        _timeProvider = timeProvider;
    }

    public async Task<byte[]?> GetAsync(
        WorkspaceReadScope scope,
        int tenantAccountId,
        CancellationToken ct = default)
    {
        var authorizedProperties = BuildAuthorizedDepositProperties(scope);
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
            where account.Id == tenantAccountId
                && deposit.PortfolioId == scope.PortfolioId
                && authorizedProperties.Any(authorized => authorized.Id == management.PropertyId)
            select new
            {
                SecurityDepositAccountId = deposit.Id,
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
                // Reconstruct the deposit corpus from canonical, reversal-netted subledger
                // projections in SQL. Deductions and refunds do not reduce the legal statement's
                // original held amount, while a transfer-out must not be reported as money still
                // attributable to this relationship.
                DepositHeld = balance.HeldBalance + balance.TotalDeductions + balance.TotalRefunded < 0m
                    ? 0m
                    : balance.HeldBalance + balance.TotalDeductions + balance.TotalRefunded,
                LastRefundedAtUtc = _db.SecurityDepositEntries.AsNoTracking()
                    .Where(entry => entry.PortfolioId == deposit.PortfolioId
                        && entry.SecurityDepositAccountId == deposit.Id
                        && entry.EntryType == SecurityDepositEntryType.Refund)
                    .Max(entry => (DateTime?)entry.PostedAtUtc),
            }).SingleOrDefaultAsync(ct);

        if (header == null)
            return null;

        var securityDepositAccountId = header.SecurityDepositAccountId;

        // Reversals stay immutable. Net each original deduction against its reversal entries in
        // SQL so the legal statement never lists a deduction that has been fully undone.
        var deductions = await _db.SecurityDepositEntries
            .AsNoTracking()
            .Where(entry => entry.PortfolioId == scope.PortfolioId
                && entry.SecurityDepositAccountId == securityDepositAccountId
                && AuthorizedDepositTargets(scope).Any(
                    target => target.Id == entry.SecurityDepositAccountId)
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

        var photos = await LoadDepositPhotosAsync(scope, securityDepositAccountId, ct);

        var propertyLine =
            $"{header.PropertyName} — {header.AddressLine1}, {header.City}, {header.State} {header.PostalCode}"
                .Trim(' ', '—');
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
            DepositHeld = header.DepositHeld,
            Deductions = deductions,
            Photos = photos,
        };

        return _pdf.Generate(data);
    }

    /// <summary>
    /// Loads decoded image bytes for photos attached to this deposit account (StoredFile rows with
    /// EntityType=SecurityDepositAccount). Best-effort: a file that fails to load is skipped, never fatal.
    /// </summary>
    private async Task<IReadOnlyList<byte[]>> LoadDepositPhotosAsync(
        WorkspaceReadScope scope,
        int depositId,
        CancellationToken ct)
    {
        var files = await _db.StoredFiles
            .AsNoTracking()
            .Where(f => f.PortfolioId == scope.PortfolioId
                        && f.EntityType == DepositEntityType
                        && f.EntityId == depositId
                        && AuthorizedDepositTargets(scope).Any(target => target.Id == f.EntityId)
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

    internal IQueryable<Property> BuildAuthorizedDepositProperties(WorkspaceReadScope scope)
    {
        var utcNow = _timeProvider.GetUtcNow().UtcDateTime;
        return TenantAccountDepositAuthorization.AuthorizedProperties(_db, scope, utcNow);
    }

    private IQueryable<SecurityDepositAccount> AuthorizedDepositTargets(
        WorkspaceReadScope scope)
    {
        var authorizedProperties = BuildAuthorizedDepositProperties(scope);
        return
            from deposit in _db.SecurityDepositAccounts.AsNoTracking()
            join account in _db.TenantAccounts.AsNoTracking()
                on new { deposit.PortfolioId, Id = deposit.TenantAccountId }
                equals new { account.PortfolioId, account.Id }
            join management in _db.LeaseManagements.AsNoTracking()
                on new { account.PortfolioId, Id = account.LeaseManagementId }
                equals new { management.PortfolioId, management.Id }
            where deposit.PortfolioId == scope.PortfolioId
                && authorizedProperties.Any(property => property.Id == management.PropertyId)
            select deposit;
    }
}
