using Microsoft.EntityFrameworkCore;
using RentalCommand.Api.DTOs;
using RentalCommand.Core.Authorization;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Time;
using RentalCommand.Data;
using RentalCommand.Data.Authorization;

namespace RentalCommand.Api.Services.Domain;

/// <inheritdoc cref="IOwnerStatementService"/>
public class OwnerStatementService : IOwnerStatementService
{
    private readonly RentalCommandDbContext _db;
    private readonly TimeProvider _timeProvider;

    public OwnerStatementService(RentalCommandDbContext db, TimeProvider timeProvider)
    {
        _db = db;
        _timeProvider = timeProvider;
    }

    /// <inheritdoc/>
    public Task<OwnerStatementReport?> GetForOwnerAsync(
        WorkspaceReadScope scope, int ownerId, int year, CancellationToken ct = default) =>
        GetForOwnerCoreAsync(
            scope.PortfolioId,
            ownerId,
            year,
            AuthorizedProperties(scope),
            ct);

    public Task<OwnerStatementReport?> GetForOwnerPortalAsync(
        OwnerPortalReadScope scope, int ownerId, int year, CancellationToken ct = default) =>
        GetForOwnerCoreAsync(
            scope.PortfolioId,
            ownerId,
            year,
            AuthorizedOwnerPortalProperties(scope),
            ct);

    private async Task<OwnerStatementReport?> GetForOwnerCoreAsync(
        int portfolioId,
        int ownerId,
        int year,
        IQueryable<Property> authorizedProperties,
        CancellationToken ct)
    {
        var (start, end) = YearRange(year);
        var propertyNetRows = OwnerPropertyNetRows(portfolioId, year, authorizedProperties);
        var roundedPropertyLines = propertyNetRows.Select(row => new OwnerStatementPropertySqlRow
        {
            OwnerId = row.OwnerId,
            PropertyId = row.PropertyId,
            PropertyName = row.PropertyName,
            RentalIncome = SqlNumericFunctions.Round(row.RentalIncome, 2),
            Expenses = SqlNumericFunctions.Round(row.Expenses, 2),
            ManagementFee = SqlNumericFunctions.Round(
                row.RentalIncome * row.ManagementFeePercent / 100m, 2),
        });
        var statement = await _db.OwnerEntities
            .AsNoTracking()
            .AsSingleQuery()
            .Where(owner =>
                owner.PortfolioId == portfolioId &&
                owner.Id == ownerId &&
                authorizedProperties.Any(property => property.OwnerEntityId == owner.Id))
            .Select(owner => new OwnerStatementSqlRow
            {
                OwnerId = owner.Id,
                OwnerName = owner.Name,
                TotalIncome = roundedPropertyLines
                    .Where(row => row.OwnerId == owner.Id)
                    .Sum(row => (decimal?)row.RentalIncome) ?? 0m,
                TotalExpenses = roundedPropertyLines
                    .Where(row => row.OwnerId == owner.Id)
                    .Sum(row => (decimal?)row.Expenses) ?? 0m,
                TotalManagementFee = roundedPropertyLines
                    .Where(row => row.OwnerId == owner.Id)
                    .Sum(row => (decimal?)row.ManagementFee) ?? 0m,
                TotalNetToOwner = roundedPropertyLines
                    .Where(row => row.OwnerId == owner.Id)
                    .Sum(row => (decimal?)(row.RentalIncome - row.Expenses - row.ManagementFee)) ?? 0m,
                TotalDistributed = _db.OwnerDistributions
                    .Where(distribution =>
                        distribution.PortfolioId == portfolioId &&
                        distribution.OwnerEntityId == owner.Id &&
                        distribution.Date >= start &&
                        distribution.Date < end &&
                        ((distribution.PropertyId != null &&
                          authorizedProperties.Any(property =>
                              property.Id == distribution.PropertyId &&
                              property.OwnerEntityId == owner.Id)) ||
                         (distribution.PropertyId == null &&
                          !_db.Properties.Any(property =>
                              property.PortfolioId == portfolioId &&
                              property.OwnerEntityId == owner.Id &&
                              !authorizedProperties.Any(authorized => authorized.Id == property.Id)))))
                    .Sum(distribution => (decimal?)distribution.Amount) ?? 0m,
                Properties = roundedPropertyLines
                    .Where(row => row.OwnerId == owner.Id)
                    .OrderBy(row => row.PropertyName)
                    .Select(row => new OwnerStatementPropertySqlRow
                    {
                        OwnerId = row.OwnerId,
                        PropertyId = row.PropertyId,
                        PropertyName = row.PropertyName,
                        RentalIncome = row.RentalIncome,
                        Expenses = row.Expenses,
                        ManagementFee = row.ManagementFee,
                    })
                    .ToList(),
            })
            .SingleOrDefaultAsync(ct);

        if (statement is null)
            return null;

        // The query above performs authorization, filtering, joins, rounding, aggregation, and ordering
        // in SQL. This mapping only shapes its already-computed display values into the public DTO.
        var lines = statement.Properties
            .Select(property => new OwnerStatementPropertyLine(
                property.PropertyId,
                property.PropertyName,
                property.RentalIncome,
                property.Expenses,
                property.ManagementFee,
                property.RentalIncome - property.Expenses - property.ManagementFee))
            .ToList();

        var totalDistributed = Math.Round(statement.TotalDistributed, 2);

        return new OwnerStatementReport
        {
            OwnerId = statement.OwnerId,
            OwnerName = statement.OwnerName,
            Year = year,
            Properties = lines,
            TotalIncome = statement.TotalIncome,
            TotalExpenses = statement.TotalExpenses,
            TotalManagementFee = statement.TotalManagementFee,
            TotalNetToOwner = statement.TotalNetToOwner,
            TotalDistributed = totalDistributed,
            Undistributed = statement.TotalNetToOwner - totalDistributed,
        };
    }

    /// <inheritdoc/>
    public Task<IReadOnlyList<OwnerStatementSummary>> ListOwnersWithNetAsync(
        WorkspaceReadScope scope, int year, CancellationToken ct = default) =>
        ListOwnersWithNetCoreAsync(
            scope.PortfolioId,
            year,
            AuthorizedProperties(scope),
            ct);

    public async Task<OwnerStatementSummaryPageResponse> ListForOwnerPortalPageAsync(
        OwnerPortalReadScope scope,
        int year,
        ListQuery query,
        CancellationToken ct = default)
    {
        var summaries = OwnerSummaries(
            scope.PortfolioId,
            year,
            AuthorizedOwnerPortalProperties(scope));
        var search = query.Search?.Trim();
        if (!string.IsNullOrWhiteSpace(search))
        {
            summaries = summaries.Where(summary =>
                EF.Functions.ILike(summary.OwnerName, $"%{search}%"));
        }

        summaries = (query.SortField, query.SortDescending) switch
        {
            ("nettoowner", false) => summaries
                .OrderBy(summary => summary.NetToOwner)
                .ThenBy(summary => summary.OwnerName)
                .ThenBy(summary => summary.OwnerId),
            ("nettoowner", true) => summaries
                .OrderByDescending(summary => summary.NetToOwner)
                .ThenBy(summary => summary.OwnerName)
                .ThenBy(summary => summary.OwnerId),
            ("name", true) => summaries
                .OrderByDescending(summary => summary.OwnerName)
                .ThenByDescending(summary => summary.OwnerId),
            _ => summaries
                .OrderBy(summary => summary.OwnerName)
                .ThenBy(summary => summary.OwnerId),
        };

        var totalCount = await summaries.CountAsync(ct);
        var items = await summaries
            .Skip(query.NormalizedSkip)
            .Take(query.NormalizedTake)
            .Select(summary => new OwnerStatementSummary(
                summary.OwnerId,
                summary.OwnerName,
                summary.NetToOwner,
                summary.TotalDistributed,
                summary.NetToOwner - summary.TotalDistributed))
            .ToListAsync(ct);

        return new OwnerStatementSummaryPageResponse
        {
            Items = items,
            TotalCount = totalCount,
            Skip = query.NormalizedSkip,
            Take = query.NormalizedTake,
        };
    }

    private async Task<IReadOnlyList<OwnerStatementSummary>> ListOwnersWithNetCoreAsync(
        int portfolioId,
        int year,
        IQueryable<Property> authorizedProperties,
        CancellationToken ct)
    {
        return await OwnerSummaries(portfolioId, year, authorizedProperties)
            .OrderBy(summary => summary.OwnerName)
            .ThenBy(summary => summary.OwnerId)
            .Select(summary => new OwnerStatementSummary(
                summary.OwnerId,
                summary.OwnerName,
                summary.NetToOwner,
                summary.TotalDistributed,
                summary.NetToOwner - summary.TotalDistributed))
            .ToListAsync(ct);
    }

    private IQueryable<OwnerStatementSummarySqlRow> OwnerSummaries(
        int portfolioId,
        int year,
        IQueryable<Property> authorizedProperties)
    {
        var propertyNetRows = OwnerPropertyNetRows(portfolioId, year, authorizedProperties);
        var (start, end) = YearRange(year);

        return propertyNetRows
            .GroupBy(property => new { property.OwnerId, property.OwnerName })
            .Select(group => new OwnerStatementSummarySqlRow
            {
                OwnerId = group.Key.OwnerId,
                OwnerName = group.Key.OwnerName,
                NetToOwner = group.Sum(property =>
                    property.RentalIncome -
                    property.Expenses -
                    (property.RentalIncome * property.ManagementFeePercent / 100m)),
                TotalDistributed = _db.OwnerDistributions
                    .Where(distribution =>
                        distribution.PortfolioId == portfolioId &&
                        distribution.OwnerEntityId == group.Key.OwnerId &&
                        distribution.Date >= start &&
                        distribution.Date < end &&
                        ((distribution.PropertyId != null &&
                          authorizedProperties.Any(property =>
                              property.Id == distribution.PropertyId &&
                              property.OwnerEntityId == group.Key.OwnerId)) ||
                         (distribution.PropertyId == null &&
                          !_db.Properties.Any(property =>
                              property.PortfolioId == portfolioId &&
                              property.OwnerEntityId == group.Key.OwnerId &&
                              !authorizedProperties.Any(authorized => authorized.Id == property.Id)))))
                    .Sum(distribution => (decimal?)distribution.Amount) ?? 0m,
            });
    }

    /// <inheritdoc/>
    public Task<decimal> GetTotalNetToOwnersAsync(
        WorkspaceReadScope scope, int year, CancellationToken ct = default) =>
        GetTotalNetToOwnersCoreAsync(
            scope.PortfolioId,
            year,
            AuthorizedProperties(scope),
            ct);

    private async Task<decimal> GetTotalNetToOwnersCoreAsync(
        int portfolioId,
        int year,
        IQueryable<Property> authorizedProperties,
        CancellationToken ct)
    {
        return await OwnerPropertyNetRows(portfolioId, year, authorizedProperties)
            .GroupBy(_ => 1)
            .Select(g => g.Sum(p =>
                p.RentalIncome -
                p.Expenses -
                (p.RentalIncome * p.ManagementFeePercent / 100m)))
            .SingleOrDefaultAsync(ct);
    }

    private IQueryable<OwnerPropertyNetRow> OwnerPropertyNetRows(
        int portfolioId,
        int year,
        IQueryable<Property> authorizedProperties)
    {
        var (startOn, endOn) = YearDateRange(year);

        return authorizedProperties
            .Where(p =>
                p.PortfolioId == portfolioId &&
                p.OwnerEntityId != null &&
                p.OwnerEntity != null)
            .Select(p => new OwnerPropertyNetRow
            {
                PropertyId = p.Id,
                PropertyName = p.Name,
                OwnerId = p.OwnerEntityId!.Value,
                OwnerName = p.OwnerEntity!.Name,
                ManagementFeePercent = p.ManagementFeePercent ?? 0m,
                RentalIncome = (
                    from allocation in _db.TenantLedgerAllocations
                    join credit in _db.TenantLedgerEntries
                        on new { allocation.PortfolioId, Id = allocation.CreditEntryId }
                        equals new { credit.PortfolioId, credit.Id }
                    join debit in _db.TenantLedgerEntries
                        on new { allocation.PortfolioId, Id = allocation.DebitEntryId }
                        equals new { debit.PortfolioId, debit.Id }
                    join account in _db.TenantAccounts
                        on new { allocation.PortfolioId, Id = allocation.TenantAccountId }
                        equals new { account.PortfolioId, account.Id }
                    join management in _db.LeaseManagements
                        on new { account.PortfolioId, Id = account.LeaseManagementId }
                        equals new { management.PortfolioId, management.Id }
                    where allocation.PortfolioId == portfolioId
                        && management.PropertyId == p.Id
                        && credit.EntryType == TenantLedgerEntryType.PaymentReceipt
                        && credit.EffectiveOn >= startOn
                        && credit.EffectiveOn < endOn
                        && debit.EntryType == TenantLedgerEntryType.RentCharge
                    select (decimal?)allocation.Amount).Sum() ?? 0m,
                Expenses = _db.Expenses
                    .Where(e =>
                        e.PortfolioId == portfolioId &&
                        e.PropertyId == p.Id &&
                        e.Status == ExpenseStatus.Paid &&
                        // Sargable half-open year range (was .Year ==).
                        (e.PaidAt ?? e.IncurredAt) >= new DateTime(year, 1, 1, 0, 0, 0, DateTimeKind.Utc) &&
                        (e.PaidAt ?? e.IncurredAt) < new DateTime(year + 1, 1, 1, 0, 0, 0, DateTimeKind.Utc))
                    .Sum(e => (decimal?)e.Amount) ?? 0m,
            });
    }

    private IQueryable<Property> AuthorizedProperties(WorkspaceReadScope scope) =>
        _db.Properties
            .AsNoTracking()
            .WhereAuthorized(
                _db,
                scope,
                CapabilityKeys.MoneyOwnerReportsRead,
                _timeProvider.UtcNow());

    private IQueryable<Property> AuthorizedOwnerPortalProperties(OwnerPortalReadScope scope)
    {
        var ownerAccess = _db.EffectiveOwnerAccess.AsNoTracking().Where(access =>
            access.AccessContextId == scope.AccessContextId &&
            access.UserId == scope.UserId &&
            access.PortfolioId == scope.PortfolioId &&
            access.AccessRevision == scope.AccessRevision);
        return _db.Properties.AsNoTracking().Where(property =>
            property.PortfolioId == scope.PortfolioId &&
            property.DeletedAt == null &&
            ownerAccess.Any(access =>
                access.PropertyId == property.Id &&
                access.OwnerEntityId == property.OwnerEntityId));
    }

    private static (DateTime Start, DateTime End) YearRange(int year)
    {
        return (
            new DateTime(year, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            new DateTime(year + 1, 1, 1, 0, 0, 0, DateTimeKind.Utc));
    }

    private static (DateOnly Start, DateOnly End) YearDateRange(int year)
    {
        return (new DateOnly(year, 1, 1), new DateOnly(year + 1, 1, 1));
    }

    private sealed class OwnerPropertyNetRow
    {
        public int PropertyId { get; set; }
        public string PropertyName { get; set; } = string.Empty;
        public int OwnerId { get; set; }
        public string OwnerName { get; set; } = string.Empty;
        public decimal ManagementFeePercent { get; set; }
        public decimal RentalIncome { get; set; }
        public decimal Expenses { get; set; }
    }

    private sealed class OwnerStatementSqlRow
    {
        public int OwnerId { get; set; }
        public string OwnerName { get; set; } = string.Empty;
        public decimal TotalIncome { get; set; }
        public decimal TotalExpenses { get; set; }
        public decimal TotalManagementFee { get; set; }
        public decimal TotalNetToOwner { get; set; }
        public decimal TotalDistributed { get; set; }
        public List<OwnerStatementPropertySqlRow> Properties { get; set; } = [];
    }

    private sealed class OwnerStatementSummarySqlRow
    {
        public int OwnerId { get; set; }
        public string OwnerName { get; set; } = string.Empty;
        public decimal NetToOwner { get; set; }
        public decimal TotalDistributed { get; set; }
    }

    private sealed class OwnerStatementPropertySqlRow
    {
        public int OwnerId { get; set; }
        public int PropertyId { get; set; }
        public string PropertyName { get; set; } = string.Empty;
        public decimal RentalIncome { get; set; }
        public decimal Expenses { get; set; }
        public decimal ManagementFee { get; set; }
    }
}
