using Microsoft.EntityFrameworkCore;
using RentalCommand.Api.DTOs;
using RentalCommand.Core.Authorization;
using RentalCommand.Core.Entities;
using RentalCommand.Data;
using RentalCommand.Data.Authorization;

namespace RentalCommand.Api.Services.Domain;

/// <summary>
/// Canonical staff reader for one continuous tenant receivable account. Authorization remains a
/// correlated database predicate in every detail, count, page, charge, and deposit statement.
/// </summary>
public sealed class TenantAccountQueryService : ITenantAccountQueryService
{
    private readonly RentalCommandDbContext _db;
    private readonly TimeProvider _timeProvider;

    public TenantAccountQueryService(RentalCommandDbContext db, TimeProvider timeProvider)
    {
        _db = db;
        _timeProvider = timeProvider;
    }

    public async Task<TenantAccountPageResponse> ListAccountsPageAsync(
        WorkspaceReadScope scope,
        TenantAccountListQuery query,
        CancellationToken ct = default)
    {
        var rows = BuildAccountListQuery(scope, query);
        var totalCount = await rows.CountAsync(ct);
        var items = await ApplyAccountSort(rows, query)
            .Skip(query.NormalizedSkip)
            .Take(query.NormalizedTake)
            .ToListAsync(ct);

        return new TenantAccountPageResponse
        {
            Items = items,
            TotalCount = totalCount,
            Skip = query.NormalizedSkip,
            Take = query.NormalizedTake,
        };
    }

    public async Task<TenantLedgerEntryGlobalPageResponse> ListEntriesPageAsync(
        WorkspaceReadScope scope,
        TenantLedgerEntryGlobalListQuery query,
        CancellationToken ct = default)
    {
        var rows = BuildGlobalEntryQuery(scope, query);
        var totalCount = await rows.CountAsync(ct);
        var items = await ApplyGlobalEntrySort(rows, query)
            .Skip(query.NormalizedSkip)
            .Take(query.NormalizedTake)
            .ToListAsync(ct);

        return new TenantLedgerEntryGlobalPageResponse
        {
            Items = items,
            TotalCount = totalCount,
            Skip = query.NormalizedSkip,
            Take = query.NormalizedTake,
        };
    }

    public async Task<TenantAccountDepositPageResponse> ListDepositsPageAsync(
        WorkspaceReadScope scope,
        TenantAccountDepositListQuery query,
        CancellationToken ct = default)
    {
        var totalCount = await BuildDepositCountQuery(scope, query).CountAsync(ct);
        var items = await BuildDepositPageQuery(scope, query).ToListAsync(ct);

        return new TenantAccountDepositPageResponse
        {
            Items = items,
            TotalCount = totalCount,
            Skip = query.NormalizedSkip,
            Take = query.NormalizedTake,
        };
    }

    public Task<TenantAccountDetailResponse?> GetAsync(
        WorkspaceReadScope scope,
        int tenantAccountId,
        CancellationToken ct = default) =>
        BuildDetailQuery(scope, tenantAccountId).SingleOrDefaultAsync(ct);

    public async Task<TenantLedgerEntryPageResponse?> ListEntriesPageAsync(
        WorkspaceReadScope scope,
        int tenantAccountId,
        TenantLedgerEntryListQuery query,
        CancellationToken ct = default)
    {
        var identity = await BuildIdentityQuery(scope, tenantAccountId).SingleOrDefaultAsync(ct);
        if (identity is null)
        {
            return null;
        }

        var rows = BuildEntryQuery(scope, tenantAccountId, query);
        var totalCount = await rows.CountAsync(ct);
        var items = await ApplyEntrySort(rows, query)
            .Skip(query.NormalizedSkip)
            .Take(query.NormalizedTake)
            .ToListAsync(ct);

        return new TenantLedgerEntryPageResponse
        {
            TenantAccountId = identity.TenantAccountId,
            LeaseManagementId = identity.LeaseManagementId,
            Items = items,
            TotalCount = totalCount,
            Skip = query.NormalizedSkip,
            Take = query.NormalizedTake,
        };
    }

    public async Task<TenantChargePageResponse?> ListChargesPageAsync(
        WorkspaceReadScope scope,
        int tenantAccountId,
        TenantChargeListQuery query,
        CancellationToken ct = default)
    {
        var identity = await BuildIdentityQuery(scope, tenantAccountId).SingleOrDefaultAsync(ct);
        if (identity is null)
        {
            return null;
        }

        var rows = BuildChargeQuery(scope, tenantAccountId, query);
        var totalCount = await rows.CountAsync(ct);
        var items = await ApplyChargeSort(rows, query)
            .Skip(query.NormalizedSkip)
            .Take(query.NormalizedTake)
            .ToListAsync(ct);

        return new TenantChargePageResponse
        {
            TenantAccountId = identity.TenantAccountId,
            LeaseManagementId = identity.LeaseManagementId,
            Items = items,
            TotalCount = totalCount,
            Skip = query.NormalizedSkip,
            Take = query.NormalizedTake,
        };
    }

    public Task<TenantAccountDepositResponse?> GetDepositAsync(
        WorkspaceReadScope scope,
        int tenantAccountId,
        CancellationToken ct = default) =>
        BuildDepositQuery(scope, tenantAccountId).SingleOrDefaultAsync(ct);

    public Task<TenantLedgerEntryDetailResponse?> GetEntryAsync(
        WorkspaceReadScope scope,
        int tenantAccountId,
        long tenantLedgerEntryId,
        CancellationToken ct = default) =>
        BuildEntryDetailQuery(scope, tenantAccountId, tenantLedgerEntryId)
            .SingleOrDefaultAsync(ct);

    internal IQueryable<TenantAccountListItemResponse> BuildAccountListQuery(
        WorkspaceReadScope scope,
        TenantAccountListQuery query)
    {
        var rows =
            from account in BuildAuthorizedAccountQuery(scope)
            join management in _db.LeaseManagements.AsNoTracking()
                on new { account.PortfolioId, Id = account.LeaseManagementId }
                equals new { management.PortfolioId, management.Id }
            join lifecycle in _db.LeaseManagementLifecycleProjections.AsNoTracking()
                on new { management.PortfolioId, LeaseManagementId = management.Id }
                equals new { lifecycle.PortfolioId, lifecycle.LeaseManagementId }
            join balance in _db.TenantAccountBalanceProjections.AsNoTracking()
                on new { account.PortfolioId, TenantAccountId = account.Id }
                equals new { balance.PortfolioId, balance.TenantAccountId }
            select new TenantAccountListItemResponse
            {
                TenantAccountId = account.Id,
                TenantAccountPublicId = account.PublicId,
                LeaseManagementId = management.Id,
                LeaseManagementPublicId = management.PublicId,
                PropertyId = management.PropertyId,
                PropertyName = management.Property!.Name,
                UnitId = management.UnitId,
                UnitNumber = management.Unit!.UnitNumber,
                AccountNumber = account.AccountNumber,
                RelationshipNumber = management.RelationshipNumber,
                PrimaryTenantName = lifecycle.CurrentPrimaryTenantName,
                Lifecycle = lifecycle.Lifecycle,
                Currency = balance.Currency,
                OpenedAtUtc = account.OpenedAtUtc,
                ClosedAtUtc = account.ClosedAtUtc,
                ReceivableBalance = balance.ReceivableBalance,
                PastDueAmount = balance.PastDueAmount,
                PastDueCount = balance.PastDueCount,
            };

        if (query.Closed.HasValue)
        {
            rows = query.Closed.Value
                ? rows.Where(row => row.ClosedAtUtc != null)
                : rows.Where(row => row.ClosedAtUtc == null);
        }
        if (!string.IsNullOrWhiteSpace(query.Lifecycle))
        {
            var lifecycle = query.Lifecycle.Trim();
            rows = rows.Where(row => row.Lifecycle == lifecycle);
        }
        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var like = $"%{query.Search.Trim()}%";
            rows = rows.Where(row =>
                EF.Functions.ILike(row.AccountNumber, like)
                || EF.Functions.ILike(row.RelationshipNumber, like)
                || EF.Functions.ILike(row.PropertyName, like)
                || EF.Functions.ILike(row.UnitNumber, like)
                || (row.PrimaryTenantName != null
                    && EF.Functions.ILike(row.PrimaryTenantName, like)));
        }
        return rows;
    }

    internal IQueryable<TenantAccountListItemResponse> BuildAccountPageQuery(
        WorkspaceReadScope scope,
        TenantAccountListQuery query) =>
        ApplyAccountSort(BuildAccountListQuery(scope, query), query)
            .Skip(query.NormalizedSkip)
            .Take(query.NormalizedTake);

    internal IQueryable<TenantLedgerEntryGlobalResponse> BuildGlobalEntryQuery(
        WorkspaceReadScope scope,
        TenantLedgerEntryGlobalListQuery query)
    {
        var rows =
            from account in BuildAuthorizedAccountQuery(scope)
            join management in _db.LeaseManagements.AsNoTracking()
                on new { account.PortfolioId, Id = account.LeaseManagementId }
                equals new { management.PortfolioId, management.Id }
            join lifecycle in _db.LeaseManagementLifecycleProjections.AsNoTracking()
                on new { management.PortfolioId, LeaseManagementId = management.Id }
                equals new { lifecycle.PortfolioId, lifecycle.LeaseManagementId }
            join entry in _db.TenantLedgerEntries.AsNoTracking()
                on new { account.PortfolioId, TenantAccountId = account.Id }
                equals new { entry.PortfolioId, entry.TenantAccountId }
            select new TenantLedgerEntryGlobalResponse
            {
                TenantAccountId = account.Id,
                LeaseManagementId = management.Id,
                PropertyId = management.PropertyId,
                PropertyName = management.Property!.Name,
                UnitId = management.UnitId,
                UnitNumber = management.Unit!.UnitNumber,
                AccountNumber = account.AccountNumber,
                RelationshipNumber = management.RelationshipNumber,
                PrimaryTenantName = lifecycle.CurrentPrimaryTenantName,
                TenantLedgerEntryId = entry.Id,
                PublicId = entry.PublicId,
                EntryType = entry.EntryType,
                Direction = entry.Direction,
                Amount = entry.Amount,
                Currency = entry.Currency,
                EffectiveOn = entry.EffectiveOn,
                DueOn = entry.DueOn,
                PostedAtUtc = entry.PostedAtUtc,
                Description = entry.Description,
                BusinessKey = entry.BusinessKey,
                TransferPublicId = entry.TransferPublicId,
                LeaseAgreementId = entry.LeaseAgreementId,
                LeaseAddendumId = entry.LeaseAddendumId,
                ReversesEntryId = entry.ReversesEntryId,
                ProviderPaymentAttemptId = entry.ProviderPaymentAttemptId,
                SourceStoredFileId = entry.SourceStoredFileId,
                CreatedByUserId = entry.CreatedByUserId,
            };

        if (query.TenantAccountId.HasValue)
        {
            rows = rows.Where(row => row.TenantAccountId == query.TenantAccountId.Value);
        }
        if (query.EntryType.HasValue)
        {
            rows = rows.Where(row => row.EntryType == query.EntryType.Value);
        }
        if (query.Direction.HasValue)
        {
            rows = rows.Where(row => row.Direction == query.Direction.Value);
        }
        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var like = $"%{query.Search.Trim()}%";
            rows = rows.Where(row =>
                EF.Functions.ILike(row.Description, like)
                || EF.Functions.ILike(row.BusinessKey, like)
                || EF.Functions.ILike(row.AccountNumber, like)
                || EF.Functions.ILike(row.RelationshipNumber, like)
                || EF.Functions.ILike(row.PropertyName, like)
                || EF.Functions.ILike(row.UnitNumber, like)
                || (row.PrimaryTenantName != null
                    && EF.Functions.ILike(row.PrimaryTenantName, like)));
        }
        if (query.From.HasValue)
        {
            var fromOn = DateOnly.FromDateTime(query.From.Value);
            rows = rows.Where(row => row.EffectiveOn >= fromOn);
        }
        if (query.To.HasValue)
        {
            var throughExclusive = DateOnly.FromDateTime(query.To.Value.Date.AddDays(1));
            rows = rows.Where(row => row.EffectiveOn < throughExclusive);
        }
        return rows;
    }

    internal IQueryable<TenantLedgerEntryGlobalResponse> BuildGlobalEntryPageQuery(
        WorkspaceReadScope scope,
        TenantLedgerEntryGlobalListQuery query) =>
        ApplyGlobalEntrySort(BuildGlobalEntryQuery(scope, query), query)
            .Skip(query.NormalizedSkip)
            .Take(query.NormalizedTake);

    internal IQueryable<TenantAccountDepositListItemResponse> BuildDepositListQuery(
        WorkspaceReadScope scope,
        TenantAccountDepositListQuery query)
    {
        var rows =
            from account in BuildAuthorizedDepositAccountQuery(scope)
            join management in _db.LeaseManagements.AsNoTracking()
                on new { account.PortfolioId, Id = account.LeaseManagementId }
                equals new { management.PortfolioId, management.Id }
            join lifecycle in _db.LeaseManagementLifecycleProjections.AsNoTracking()
                on new { management.PortfolioId, LeaseManagementId = management.Id }
                equals new { lifecycle.PortfolioId, lifecycle.LeaseManagementId }
            join deposit in _db.SecurityDepositAccounts.AsNoTracking()
                on new { account.PortfolioId, TenantAccountId = account.Id }
                equals new { deposit.PortfolioId, deposit.TenantAccountId }
            join balance in _db.SecurityDepositBalanceProjections.AsNoTracking()
                on new { deposit.PortfolioId, SecurityDepositAccountId = deposit.Id }
                equals new { balance.PortfolioId, balance.SecurityDepositAccountId }
            select new TenantAccountDepositListItemResponse
            {
                SecurityDepositAccountId = deposit.Id,
                TenantAccountId = account.Id,
                LeaseManagementId = management.Id,
                OriginatingAgreementId = deposit.OriginatingAgreementId,
                PropertyId = management.PropertyId,
                PropertyName = management.Property!.Name,
                UnitId = management.UnitId,
                UnitNumber = management.Unit!.UnitNumber,
                AccountNumber = account.AccountNumber,
                RelationshipNumber = management.RelationshipNumber,
                PrimaryTenantName = lifecycle.CurrentPrimaryTenantName,
                Currency = balance.Currency,
                CreatedAtUtc = deposit.CreatedAtUtc,
                EffectiveNowUtc = balance.EffectiveNowUtc,
                BusinessDate = balance.BusinessDate,
                TotalReceived = balance.TotalReceived,
                TotalDeductions = balance.TotalDeductions,
                TotalRefunded = balance.TotalRefunded,
                TotalTransferredIn = balance.TotalTransferredIn,
                TotalTransferredOut = balance.TotalTransferredOut,
                NetAdjustments = balance.NetAdjustments,
                HeldBalance = balance.HeldBalance,
                Status = balance.DepositStatus,
            };

        if (query.TenantAccountId.HasValue)
        {
            rows = rows.Where(row => row.TenantAccountId == query.TenantAccountId.Value);
        }
        if (query.PropertyId.HasValue)
        {
            rows = rows.Where(row => row.PropertyId == query.PropertyId.Value);
        }
        if (!string.IsNullOrWhiteSpace(query.Status))
        {
            var status = query.Status.Trim();
            rows = rows.Where(row => EF.Functions.ILike(row.Status, status));
        }
        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var like = $"%{query.Search.Trim()}%";
            rows = rows.Where(row =>
                EF.Functions.ILike(row.AccountNumber, like)
                || EF.Functions.ILike(row.RelationshipNumber, like)
                || EF.Functions.ILike(row.PropertyName, like)
                || EF.Functions.ILike(row.UnitNumber, like)
                || (row.PrimaryTenantName != null
                    && EF.Functions.ILike(row.PrimaryTenantName, like)));
        }
        return rows;
    }

    internal IQueryable<TenantAccountDepositListItemResponse> BuildDepositPageQuery(
        WorkspaceReadScope scope,
        TenantAccountDepositListQuery query)
    {
        if (query.SortField == "createdatutc"
            && string.IsNullOrWhiteSpace(query.Status)
            && string.IsNullOrWhiteSpace(query.Search))
        {
            return BuildCreatedAtDepositPageQuery(scope, query);
        }

        return ApplyDepositSort(BuildDepositListQuery(scope, query), query)
            .Skip(query.NormalizedSkip)
            .Take(query.NormalizedTake);
    }

    private IQueryable<TenantAccountDepositListItemResponse> BuildCreatedAtDepositPageQuery(
        WorkspaceReadScope scope,
        TenantAccountDepositListQuery query)
    {
        var seeds =
            from account in BuildAuthorizedDepositAccountQuery(scope)
            join management in _db.LeaseManagements.AsNoTracking()
                on new { account.PortfolioId, Id = account.LeaseManagementId }
                equals new { management.PortfolioId, management.Id }
            join deposit in _db.SecurityDepositAccounts.AsNoTracking()
                on new { account.PortfolioId, TenantAccountId = account.Id }
                equals new { deposit.PortfolioId, deposit.TenantAccountId }
            select new DepositPageSeed
            {
                PortfolioId = account.PortfolioId,
                SecurityDepositAccountId = deposit.Id,
                TenantAccountId = account.Id,
                LeaseManagementId = management.Id,
                OriginatingAgreementId = deposit.OriginatingAgreementId,
                PropertyId = management.PropertyId,
                UnitId = management.UnitId,
                AccountNumber = account.AccountNumber,
                RelationshipNumber = management.RelationshipNumber,
                CreatedAtUtc = deposit.CreatedAtUtc,
            };

        if (query.TenantAccountId.HasValue)
        {
            seeds = seeds.Where(row => row.TenantAccountId == query.TenantAccountId.Value);
        }
        if (query.PropertyId.HasValue)
        {
            seeds = seeds.Where(row => row.PropertyId == query.PropertyId.Value);
        }

        var pagedSeeds = query.SortDescending
            ? seeds.OrderByDescending(row => row.CreatedAtUtc)
                .ThenByDescending(row => row.SecurityDepositAccountId)
                .Skip(query.NormalizedSkip)
                .Take(query.NormalizedTake)
            : seeds.OrderBy(row => row.CreatedAtUtc)
                .ThenBy(row => row.SecurityDepositAccountId)
                .Skip(query.NormalizedSkip)
                .Take(query.NormalizedTake);

        var rows =
            from seed in pagedSeeds
            // Referencing the already-paged seed in each projection intentionally keeps these
            // expensive views as correlated LATERAL lookups. A normal join lets PostgreSQL expand
            // both security-invoker views across the whole portfolio before applying the keys.
            from lifecycle in _db.LeaseManagementLifecycleProjections.AsNoTracking()
                .Where(row => row.PortfolioId == seed.PortfolioId
                    && row.LeaseManagementId == seed.LeaseManagementId)
                .Select(row => new
                {
                    row.CurrentPrimaryTenantName,
                    SeedId = seed.SecurityDepositAccountId,
                })
                .Take(1)
            from balance in _db.SecurityDepositBalanceProjections.AsNoTracking()
                .Where(row => row.PortfolioId == seed.PortfolioId
                    && row.SecurityDepositAccountId == seed.SecurityDepositAccountId)
                .Select(row => new
                {
                    row.Currency,
                    row.EffectiveNowUtc,
                    row.BusinessDate,
                    row.TotalReceived,
                    row.TotalDeductions,
                    row.TotalRefunded,
                    row.TotalTransferredIn,
                    row.TotalTransferredOut,
                    row.NetAdjustments,
                    row.HeldBalance,
                    row.DepositStatus,
                    SeedId = seed.SecurityDepositAccountId,
                })
                .Take(1)
            join property in _db.Properties.AsNoTracking()
                on new { seed.PortfolioId, Id = seed.PropertyId }
                equals new { property.PortfolioId, property.Id }
            join unit in _db.Units.AsNoTracking()
                on new { seed.PortfolioId, seed.PropertyId, Id = seed.UnitId }
                equals new { unit.PortfolioId, unit.PropertyId, unit.Id }
            select new TenantAccountDepositListItemResponse
            {
                SecurityDepositAccountId = seed.SecurityDepositAccountId,
                TenantAccountId = seed.TenantAccountId,
                LeaseManagementId = seed.LeaseManagementId,
                OriginatingAgreementId = seed.OriginatingAgreementId,
                PropertyId = seed.PropertyId,
                PropertyName = property.Name,
                UnitId = seed.UnitId,
                UnitNumber = unit.UnitNumber,
                AccountNumber = seed.AccountNumber,
                RelationshipNumber = seed.RelationshipNumber,
                PrimaryTenantName = lifecycle.CurrentPrimaryTenantName,
                Currency = balance.Currency,
                CreatedAtUtc = seed.CreatedAtUtc,
                EffectiveNowUtc = balance.EffectiveNowUtc,
                BusinessDate = balance.BusinessDate,
                TotalReceived = balance.TotalReceived,
                TotalDeductions = balance.TotalDeductions,
                TotalRefunded = balance.TotalRefunded,
                TotalTransferredIn = balance.TotalTransferredIn,
                TotalTransferredOut = balance.TotalTransferredOut,
                NetAdjustments = balance.NetAdjustments,
                HeldBalance = balance.HeldBalance,
                Status = balance.DepositStatus,
            };

        return ApplyDepositSort(rows, query);
    }

    internal IQueryable<int> BuildDepositCountQuery(
        WorkspaceReadScope scope,
        TenantAccountDepositListQuery query)
    {
        // Status and free-text search depend on the balance/lifecycle projections, so keep those
        // filtered counts on the full translated query. The common unfiltered page count only needs
        // canonical deposit identity and authorization; joining both aggregate views made that
        // first-page count several seconds slower than the page itself.
        if (!string.IsNullOrWhiteSpace(query.Status) || !string.IsNullOrWhiteSpace(query.Search))
        {
            return BuildDepositListQuery(scope, query)
                .Select(row => row.SecurityDepositAccountId);
        }

        var rows =
            from account in BuildAuthorizedDepositAccountQuery(scope)
            join management in _db.LeaseManagements.AsNoTracking()
                on new { account.PortfolioId, Id = account.LeaseManagementId }
                equals new { management.PortfolioId, management.Id }
            join deposit in _db.SecurityDepositAccounts.AsNoTracking()
                on new { account.PortfolioId, TenantAccountId = account.Id }
                equals new { deposit.PortfolioId, deposit.TenantAccountId }
            select new
            {
                DepositId = deposit.Id,
                TenantAccountId = account.Id,
                management.PropertyId,
            };

        if (query.TenantAccountId.HasValue)
        {
            rows = rows.Where(row => row.TenantAccountId == query.TenantAccountId.Value);
        }
        if (query.PropertyId.HasValue)
        {
            rows = rows.Where(row => row.PropertyId == query.PropertyId.Value);
        }

        return rows.Select(row => row.DepositId);
    }

    internal IQueryable<TenantAccountDetailResponse> BuildDetailQuery(
        WorkspaceReadScope scope,
        int tenantAccountId) =>
        from account in BuildAuthorizedAccountQuery(scope)
        join management in _db.LeaseManagements.AsNoTracking()
            on new { account.PortfolioId, Id = account.LeaseManagementId }
            equals new { management.PortfolioId, management.Id }
        join balance in _db.TenantAccountBalanceProjections.AsNoTracking()
            on new { account.PortfolioId, TenantAccountId = account.Id }
            equals new { balance.PortfolioId, balance.TenantAccountId }
        where account.Id == tenantAccountId
        select new TenantAccountDetailResponse
        {
            TenantAccountId = account.Id,
            TenantAccountPublicId = account.PublicId,
            LeaseManagementId = management.Id,
            LeaseManagementPublicId = management.PublicId,
            PropertyId = management.PropertyId,
            PropertyName = management.Property!.Name,
            UnitId = management.UnitId,
            UnitNumber = management.Unit!.UnitNumber,
            AccountNumber = account.AccountNumber,
            RelationshipNumber = management.RelationshipNumber,
            Currency = balance.Currency,
            OpenedAtUtc = account.OpenedAtUtc,
            ClosedAtUtc = account.ClosedAtUtc,
            CloseReasonCode = account.CloseReasonCode,
            CloseNote = account.CloseNote,
            EffectiveNowUtc = balance.EffectiveNowUtc,
            BusinessDate = balance.BusinessDate,
            TotalDebits = balance.TotalDebits,
            TotalCredits = balance.TotalCredits,
            ReceivableBalance = balance.ReceivableBalance,
            UnappliedCredit = balance.UnappliedCredit,
            PastDueAmount = balance.PastDueAmount,
            PastDueCount = balance.PastDueCount,
            NextDueOn = balance.NextDueOn,
            NextDueAmount = balance.NextDueAmount,
            Condition = balance.Condition,
            LastReceiptOn = balance.LastReceiptOn,
            LastReceiptAmount = balance.LastReceiptAmount,
        };

    internal IQueryable<TenantLedgerEntryResponse> BuildEntryQuery(
        WorkspaceReadScope scope,
        int tenantAccountId,
        TenantLedgerEntryListQuery query)
    {
        var rows =
            from account in BuildAuthorizedAccountQuery(scope)
            join entry in _db.TenantLedgerEntries.AsNoTracking()
                on new { account.PortfolioId, TenantAccountId = account.Id }
                equals new { entry.PortfolioId, entry.TenantAccountId }
            where account.Id == tenantAccountId
            select new TenantLedgerEntryResponse
            {
                TenantAccountId = account.Id,
                LeaseManagementId = account.LeaseManagementId,
                TenantLedgerEntryId = entry.Id,
                PublicId = entry.PublicId,
                EntryType = entry.EntryType,
                Direction = entry.Direction,
                Amount = entry.Amount,
                Currency = entry.Currency,
                EffectiveOn = entry.EffectiveOn,
                DueOn = entry.DueOn,
                PostedAtUtc = entry.PostedAtUtc,
                Description = entry.Description,
                BusinessKey = entry.BusinessKey,
                TransferPublicId = entry.TransferPublicId,
                LeaseAgreementId = entry.LeaseAgreementId,
                LeaseAddendumId = entry.LeaseAddendumId,
                ReversesEntryId = entry.ReversesEntryId,
                ProviderPaymentAttemptId = entry.ProviderPaymentAttemptId,
                SourceStoredFileId = entry.SourceStoredFileId,
                CreatedByUserId = entry.CreatedByUserId,
            };

        if (query.EntryType.HasValue)
        {
            rows = rows.Where(row => row.EntryType == query.EntryType.Value);
        }
        if (query.Direction.HasValue)
        {
            rows = rows.Where(row => row.Direction == query.Direction.Value);
        }
        rows = ApplyEntryFilters(rows, query.Search, query.From, query.To);
        return rows;
    }

    internal IQueryable<TenantLedgerEntryResponse> BuildEntryPageQuery(
        WorkspaceReadScope scope,
        int tenantAccountId,
        TenantLedgerEntryListQuery query) =>
        ApplyEntrySort(BuildEntryQuery(scope, tenantAccountId, query), query)
            .Skip(query.NormalizedSkip)
            .Take(query.NormalizedTake);

    internal IQueryable<TenantLedgerEntryDetailResponse> BuildEntryDetailQuery(
        WorkspaceReadScope scope,
        int tenantAccountId,
        long tenantLedgerEntryId) =>
        from account in BuildAuthorizedAccountQuery(scope)
        join management in _db.LeaseManagements.AsNoTracking()
            on new { account.PortfolioId, Id = account.LeaseManagementId }
            equals new { management.PortfolioId, management.Id }
        join lifecycle in _db.LeaseManagementLifecycleProjections.AsNoTracking()
            on new { management.PortfolioId, LeaseManagementId = management.Id }
            equals new { lifecycle.PortfolioId, lifecycle.LeaseManagementId }
        join entry in _db.TenantLedgerEntries.AsNoTracking()
            on new { account.PortfolioId, TenantAccountId = account.Id }
            equals new { entry.PortfolioId, entry.TenantAccountId }
        from agreement in _db.LeaseAgreements.AsNoTracking()
            .Where(item => item.PortfolioId == entry.PortfolioId
                && item.LeaseManagementId == account.LeaseManagementId
                && item.Id == entry.LeaseAgreementId)
            .DefaultIfEmpty()
        from addendum in _db.LeaseAddenda.AsNoTracking()
            .Where(item => item.PortfolioId == entry.PortfolioId
                && item.LeaseManagementId == account.LeaseManagementId
                && item.Id == entry.LeaseAddendumId)
            .DefaultIfEmpty()
        from providerAttempt in _db.TenantPaymentAttempts.AsNoTracking()
            .Where(attempt => attempt.PortfolioId == entry.PortfolioId
                && attempt.TenantAccountId == account.Id
                && attempt.Id == entry.ProviderPaymentAttemptId)
            .DefaultIfEmpty()
        from sourceFile in _db.StoredFiles.AsNoTracking()
            .Where(file => file.PortfolioId == entry.PortfolioId
                && file.Id == entry.SourceStoredFileId)
            .DefaultIfEmpty()
        where account.Id == tenantAccountId
            && entry.Id == tenantLedgerEntryId
        select new TenantLedgerEntryDetailResponse
        {
            PortfolioId = account.PortfolioId,
            TenantAccountId = account.Id,
            TenantAccountPublicId = account.PublicId,
            LeaseManagementId = management.Id,
            LeaseManagementPublicId = management.PublicId,
            PropertyId = management.PropertyId,
            PropertyName = management.Property!.Name,
            UnitId = management.UnitId,
            UnitNumber = management.Unit!.UnitNumber,
            AccountNumber = account.AccountNumber,
            RelationshipNumber = management.RelationshipNumber,
            TenantName = lifecycle.CurrentPrimaryTenantName,
            TenantLedgerEntryId = entry.Id,
            PublicId = entry.PublicId,
            EntryType = entry.EntryType,
            Direction = entry.Direction,
            Amount = entry.Amount,
            Currency = entry.Currency,
            EffectiveOn = entry.EffectiveOn,
            DueOn = entry.DueOn,
            PostedAtUtc = entry.PostedAtUtc,
            Description = entry.Description,
            BusinessKey = entry.BusinessKey,
            TransferPublicId = entry.TransferPublicId,
            LeaseAgreementId = entry.LeaseAgreementId,
            LeaseAddendumId = entry.LeaseAddendumId,
            ReversesEntryId = entry.ReversesEntryId,
            ProviderPaymentAttemptId = entry.ProviderPaymentAttemptId,
            SourceStoredFileId = entry.SourceStoredFileId,
            CreatedByUserId = entry.CreatedByUserId,
            Agreement = agreement == null
                ? null
                : new TenantLedgerAgreementProvenanceResponse
                {
                    LeaseAgreementId = agreement.Id,
                    PublicId = agreement.PublicId,
                    LeaseManagementId = agreement.LeaseManagementId,
                    VersionNumber = agreement.VersionNumber,
                    AgreementNumber = agreement.AgreementNumber,
                    ChangeType = agreement.ChangeType,
                    TransferredFromAgreementId = agreement.TransferredFromAgreementId,
                    ReplacesAgreementId = agreement.ReplacesAgreementId,
                    RenewsAgreementId = agreement.RenewsAgreementId,
                    GoverningFromOn = agreement.GoverningFromOn,
                    SupersededEffectiveOn = agreement.SupersededEffectiveOn,
                    SupersededByAgreementId = agreement.SupersededByAgreementId,
                    FullyExecutedAtUtc = agreement.FullyExecutedAtUtc,
                    VoidedAtUtc = agreement.VoidedAtUtc,
                },
            Addendum = addendum == null
                ? null
                : new TenantLedgerAddendumProvenanceResponse
                {
                    LeaseAddendumId = addendum.Id,
                    PublicId = addendum.PublicId,
                    SeriesPublicId = addendum.SeriesPublicId,
                    LeaseManagementId = addendum.LeaseManagementId,
                    BaseAgreementId = addendum.BaseAgreementId,
                    VersionNumber = addendum.VersionNumber,
                    AddendumNumber = addendum.AddendumNumber,
                    Purpose = addendum.Purpose,
                    ReplacesAddendumId = addendum.ReplacesAddendumId,
                    EffectiveFromOn = addendum.EffectiveFromOn,
                    EffectiveThroughOn = addendum.EffectiveThroughOn,
                    SupersededEffectiveOn = addendum.SupersededEffectiveOn,
                    SupersededByAddendumId = addendum.SupersededByAddendumId,
                    FullyExecutedAtUtc = addendum.FullyExecutedAtUtc,
                    VoidedAtUtc = addendum.VoidedAtUtc,
                },
            ProviderAttempt = providerAttempt == null
                ? null
                : new TenantLedgerProviderAttemptResponse
                {
                    ProviderPaymentAttemptId = providerAttempt.Id,
                    PublicId = providerAttempt.PublicId,
                    Provider = providerAttempt.Provider,
                    ProviderReference = providerAttempt.ProviderObjectId,
                    AttemptType = providerAttempt.AttemptType,
                    State = providerAttempt.State,
                    Amount = providerAttempt.Amount,
                    Currency = providerAttempt.Currency,
                    PaymentMethodSummary = providerAttempt.PaymentMethodSummary,
                    PayerName = providerAttempt.PayerName,
                    CheckNumber = providerAttempt.CheckNumber,
                    BankName = providerAttempt.BankName,
                    FailureCode = providerAttempt.FailureCode,
                    FailureReason = providerAttempt.FailureReason,
                    PreparedAtUtc = providerAttempt.PreparedAtUtc,
                    SubmittedAtUtc = providerAttempt.SubmittedAtUtc,
                    SettledAtUtc = providerAttempt.SettledAtUtc,
                    UpdatedAtUtc = providerAttempt.UpdatedAtUtc,
                    AttemptCount = providerAttempt.AttemptCount,
                    NextAttemptAtUtc = providerAttempt.NextAttemptAtUtc,
                },
            SourceFile = sourceFile == null
                ? null
                : new TenantLedgerSourceFileResponse
                {
                    SourceStoredFileId = sourceFile.Id,
                    FileName = sourceFile.FileName,
                    ContentType = sourceFile.ContentType,
                    FileSize = sourceFile.FileSize,
                    EntityType = sourceFile.EntityType,
                    EntityId = sourceFile.EntityId,
                    UploadedAtUtc = sourceFile.UploadedAt,
                    DeletedAtUtc = sourceFile.DeletedAt,
                },
        };

    internal IQueryable<TenantChargeResponse> BuildChargeQuery(
        WorkspaceReadScope scope,
        int tenantAccountId,
        TenantChargeListQuery query)
    {
        var rows =
            from account in BuildAuthorizedAccountQuery(scope)
            join balance in _db.TenantChargeBalanceProjections.AsNoTracking()
                on new { account.PortfolioId, TenantAccountId = account.Id }
                equals new { balance.PortfolioId, balance.TenantAccountId }
            join entry in _db.TenantLedgerEntries.AsNoTracking()
                on new
                {
                    balance.PortfolioId,
                    balance.TenantAccountId,
                    Id = balance.TenantLedgerEntryId,
                }
                equals new { entry.PortfolioId, entry.TenantAccountId, entry.Id }
            where account.Id == tenantAccountId
            select new TenantChargeResponse
            {
                TenantAccountId = account.Id,
                LeaseManagementId = account.LeaseManagementId,
                TenantLedgerEntryId = entry.Id,
                PublicId = entry.PublicId,
                EntryType = entry.EntryType,
                Direction = entry.Direction,
                Currency = balance.Currency,
                EffectiveOn = balance.EffectiveOn,
                DueOn = balance.DueOn,
                PostedAtUtc = entry.PostedAtUtc,
                Description = entry.Description,
                OriginalAmount = balance.OriginalAmount,
                ReversedAmount = balance.ReversedAmount,
                NetAllocations = balance.NetAllocations,
                OpenAmount = balance.OpenAmount,
                IsPastDue = balance.IsPastDue,
                TransferPublicId = entry.TransferPublicId,
                LeaseAgreementId = entry.LeaseAgreementId,
                LeaseAddendumId = entry.LeaseAddendumId,
                ReversesEntryId = entry.ReversesEntryId,
                ProviderPaymentAttemptId = entry.ProviderPaymentAttemptId,
                SourceStoredFileId = entry.SourceStoredFileId,
            };

        if (query.EntryType.HasValue)
        {
            rows = rows.Where(row => row.EntryType == query.EntryType.Value);
        }
        if (query.IsPastDue.HasValue)
        {
            rows = rows.Where(row => row.IsPastDue == query.IsPastDue.Value);
        }
        rows = ApplyChargeFilters(rows, query.Search, query.From, query.To);
        return rows;
    }

    internal IQueryable<TenantChargeResponse> BuildChargePageQuery(
        WorkspaceReadScope scope,
        int tenantAccountId,
        TenantChargeListQuery query) =>
        ApplyChargeSort(BuildChargeQuery(scope, tenantAccountId, query), query)
            .Skip(query.NormalizedSkip)
            .Take(query.NormalizedTake);

    internal IQueryable<TenantAccountDepositResponse> BuildDepositQuery(
        WorkspaceReadScope scope,
        int tenantAccountId) =>
        from account in BuildAuthorizedDepositAccountQuery(scope)
        join management in _db.LeaseManagements.AsNoTracking()
            on new { account.PortfolioId, Id = account.LeaseManagementId }
            equals new { management.PortfolioId, management.Id }
        join lifecycle in _db.LeaseManagementLifecycleProjections.AsNoTracking()
            on new { management.PortfolioId, LeaseManagementId = management.Id }
            equals new { lifecycle.PortfolioId, lifecycle.LeaseManagementId }
        join deposit in _db.SecurityDepositAccounts.AsNoTracking()
            on new { account.PortfolioId, TenantAccountId = account.Id }
            equals new { deposit.PortfolioId, deposit.TenantAccountId }
        join balance in _db.SecurityDepositBalanceProjections.AsNoTracking()
            on new { deposit.PortfolioId, SecurityDepositAccountId = deposit.Id }
            equals new { balance.PortfolioId, balance.SecurityDepositAccountId }
        where account.Id == tenantAccountId
        select new TenantAccountDepositResponse
        {
            TenantAccountId = account.Id,
            LeaseManagementId = management.Id,
            SecurityDepositAccountId = deposit.Id,
            OriginatingAgreementId = deposit.OriginatingAgreementId,
            PropertyId = management.PropertyId,
            PropertyName = management.Property!.Name,
            UnitId = management.UnitId,
            UnitNumber = management.Unit!.UnitNumber,
            AccountNumber = account.AccountNumber,
            RelationshipNumber = management.RelationshipNumber,
            PrimaryTenantName = lifecycle.CurrentPrimaryTenantName,
            Currency = balance.Currency,
            CreatedAtUtc = deposit.CreatedAtUtc,
            EffectiveNowUtc = balance.EffectiveNowUtc,
            BusinessDate = balance.BusinessDate,
            TotalReceived = balance.TotalReceived,
            TotalDeductions = balance.TotalDeductions,
            TotalRefunded = balance.TotalRefunded,
            TotalTransferredIn = balance.TotalTransferredIn,
            TotalTransferredOut = balance.TotalTransferredOut,
            NetAdjustments = balance.NetAdjustments,
            HeldBalance = balance.HeldBalance,
            Status = balance.DepositStatus,
        };

    internal IQueryable<TenantAccount> BuildAuthorizedAccountQuery(WorkspaceReadScope scope)
    {
        var utcNow = _timeProvider.GetUtcNow().UtcDateTime;
        var authorizedProperties = _db.Properties.AsNoTracking().WhereAuthorized(
            _db, scope, CapabilityKeys.MoneyBalancesRead, utcNow);

        return
            from account in _db.TenantAccounts.AsNoTracking()
            join management in _db.LeaseManagements.AsNoTracking()
                on new { account.PortfolioId, Id = account.LeaseManagementId }
                equals new { management.PortfolioId, management.Id }
            where account.PortfolioId == scope.PortfolioId
                && authorizedProperties.Any(property => property.Id == management.PropertyId)
            select account;
    }

    internal IQueryable<TenantAccount> BuildAuthorizedDepositAccountQuery(
        WorkspaceReadScope scope)
    {
        var utcNow = _timeProvider.GetUtcNow().UtcDateTime;
        var authorizedProperties = TenantAccountDepositAuthorization.AuthorizedProperties(
            _db, scope, utcNow);

        return
            from account in _db.TenantAccounts.AsNoTracking()
            join management in _db.LeaseManagements.AsNoTracking()
                on new { account.PortfolioId, Id = account.LeaseManagementId }
                equals new { management.PortfolioId, management.Id }
            where account.PortfolioId == scope.PortfolioId
                && authorizedProperties.Any(property => property.Id == management.PropertyId)
            select account;
    }

    private IQueryable<TenantAccountReadIdentity> BuildIdentityQuery(
        WorkspaceReadScope scope,
        int tenantAccountId) =>
        BuildAuthorizedAccountQuery(scope)
            .Where(account => account.Id == tenantAccountId)
            .Select(account => new TenantAccountReadIdentity
            {
                TenantAccountId = account.Id,
                LeaseManagementId = account.LeaseManagementId,
            });

    private static IQueryable<TenantLedgerEntryResponse> ApplyEntryFilters(
        IQueryable<TenantLedgerEntryResponse> rows,
        string? search,
        DateTime? from,
        DateTime? to)
    {
        if (!string.IsNullOrWhiteSpace(search))
        {
            var like = $"%{search.Trim()}%";
            rows = rows.Where(row =>
                EF.Functions.ILike(row.Description, like)
                || EF.Functions.ILike(row.BusinessKey, like)
                || EF.Functions.ILike(row.Currency, like));
        }
        if (from.HasValue)
        {
            var fromOn = DateOnly.FromDateTime(from.Value);
            rows = rows.Where(row => row.EffectiveOn >= fromOn);
        }
        if (to.HasValue)
        {
            var throughExclusive = DateOnly.FromDateTime(to.Value.Date.AddDays(1));
            rows = rows.Where(row => row.EffectiveOn < throughExclusive);
        }
        return rows;
    }

    private static IQueryable<TenantChargeResponse> ApplyChargeFilters(
        IQueryable<TenantChargeResponse> rows,
        string? search,
        DateTime? from,
        DateTime? to)
    {
        if (!string.IsNullOrWhiteSpace(search))
        {
            var like = $"%{search.Trim()}%";
            rows = rows.Where(row =>
                EF.Functions.ILike(row.Description, like)
                || EF.Functions.ILike(row.Currency, like));
        }
        if (from.HasValue)
        {
            var fromOn = DateOnly.FromDateTime(from.Value);
            rows = rows.Where(row => row.EffectiveOn >= fromOn);
        }
        if (to.HasValue)
        {
            var throughExclusive = DateOnly.FromDateTime(to.Value.Date.AddDays(1));
            rows = rows.Where(row => row.EffectiveOn < throughExclusive);
        }
        return rows;
    }

    private static IQueryable<TenantLedgerEntryResponse> ApplyEntrySort(
        IQueryable<TenantLedgerEntryResponse> rows,
        TenantLedgerEntryListQuery query) => query.SortField switch
        {
            "effectiveon" => query.SortDescending
                ? rows.OrderByDescending(row => row.EffectiveOn).ThenByDescending(row => row.TenantLedgerEntryId)
                : rows.OrderBy(row => row.EffectiveOn).ThenBy(row => row.TenantLedgerEntryId),
            "amount" => query.SortDescending
                ? rows.OrderByDescending(row => row.Amount).ThenByDescending(row => row.TenantLedgerEntryId)
                : rows.OrderBy(row => row.Amount).ThenBy(row => row.TenantLedgerEntryId),
            "dueon" => query.SortDescending
                ? rows.OrderByDescending(row => row.DueOn).ThenByDescending(row => row.TenantLedgerEntryId)
                : rows.OrderBy(row => row.DueOn).ThenBy(row => row.TenantLedgerEntryId),
            "entrytype" => query.SortDescending
                ? rows.OrderByDescending(row => row.EntryType).ThenByDescending(row => row.TenantLedgerEntryId)
                : rows.OrderBy(row => row.EntryType).ThenBy(row => row.TenantLedgerEntryId),
            "postedatutc" => query.SortDescending
                ? rows.OrderByDescending(row => row.PostedAtUtc).ThenByDescending(row => row.TenantLedgerEntryId)
                : rows.OrderBy(row => row.PostedAtUtc).ThenBy(row => row.TenantLedgerEntryId),
            _ => rows.OrderByDescending(row => row.EffectiveOn)
                .ThenByDescending(row => row.TenantLedgerEntryId),
        };

    private static IQueryable<TenantAccountListItemResponse> ApplyAccountSort(
        IQueryable<TenantAccountListItemResponse> rows,
        TenantAccountListQuery query) => query.SortField switch
        {
            "propertyname" => query.SortDescending
                ? rows.OrderByDescending(row => row.PropertyName)
                    .ThenByDescending(row => row.UnitNumber)
                    .ThenByDescending(row => row.TenantAccountId)
                : rows.OrderBy(row => row.PropertyName)
                    .ThenBy(row => row.UnitNumber)
                    .ThenBy(row => row.TenantAccountId),
            "unitnumber" => query.SortDescending
                ? rows.OrderByDescending(row => row.UnitNumber).ThenByDescending(row => row.TenantAccountId)
                : rows.OrderBy(row => row.UnitNumber).ThenBy(row => row.TenantAccountId),
            "accountnumber" => query.SortDescending
                ? rows.OrderByDescending(row => row.AccountNumber).ThenByDescending(row => row.TenantAccountId)
                : rows.OrderBy(row => row.AccountNumber).ThenBy(row => row.TenantAccountId),
            "tenantname" => query.SortDescending
                ? rows.OrderByDescending(row => row.PrimaryTenantName).ThenByDescending(row => row.TenantAccountId)
                : rows.OrderBy(row => row.PrimaryTenantName).ThenBy(row => row.TenantAccountId),
            "balance" => query.SortDescending
                ? rows.OrderByDescending(row => row.ReceivableBalance).ThenByDescending(row => row.TenantAccountId)
                : rows.OrderBy(row => row.ReceivableBalance).ThenBy(row => row.TenantAccountId),
            "openedatutc" => query.SortDescending
                ? rows.OrderByDescending(row => row.OpenedAtUtc).ThenByDescending(row => row.TenantAccountId)
                : rows.OrderBy(row => row.OpenedAtUtc).ThenBy(row => row.TenantAccountId),
            _ => rows.OrderBy(row => row.PropertyName)
                .ThenBy(row => row.UnitNumber)
                .ThenBy(row => row.TenantAccountId),
        };

    private static IQueryable<TenantLedgerEntryGlobalResponse> ApplyGlobalEntrySort(
        IQueryable<TenantLedgerEntryGlobalResponse> rows,
        TenantLedgerEntryGlobalListQuery query) => query.SortField switch
        {
            "effectiveon" => query.SortDescending
                ? rows.OrderByDescending(row => row.EffectiveOn).ThenByDescending(row => row.TenantLedgerEntryId)
                : rows.OrderBy(row => row.EffectiveOn).ThenBy(row => row.TenantLedgerEntryId),
            "dueon" => query.SortDescending
                ? rows.OrderByDescending(row => row.DueOn).ThenByDescending(row => row.TenantLedgerEntryId)
                : rows.OrderBy(row => row.DueOn).ThenBy(row => row.TenantLedgerEntryId),
            "amount" => query.SortDescending
                ? rows.OrderByDescending(row => row.Amount).ThenByDescending(row => row.TenantLedgerEntryId)
                : rows.OrderBy(row => row.Amount).ThenBy(row => row.TenantLedgerEntryId),
            "entrytype" => query.SortDescending
                ? rows.OrderByDescending(row => row.EntryType).ThenByDescending(row => row.TenantLedgerEntryId)
                : rows.OrderBy(row => row.EntryType).ThenBy(row => row.TenantLedgerEntryId),
            "propertyname" => query.SortDescending
                ? rows.OrderByDescending(row => row.PropertyName).ThenByDescending(row => row.TenantLedgerEntryId)
                : rows.OrderBy(row => row.PropertyName).ThenBy(row => row.TenantLedgerEntryId),
            "tenantname" => query.SortDescending
                ? rows.OrderByDescending(row => row.PrimaryTenantName).ThenByDescending(row => row.TenantLedgerEntryId)
                : rows.OrderBy(row => row.PrimaryTenantName).ThenBy(row => row.TenantLedgerEntryId),
            _ => rows.OrderByDescending(row => row.EffectiveOn)
                .ThenByDescending(row => row.TenantLedgerEntryId),
        };

    private static IQueryable<TenantChargeResponse> ApplyChargeSort(
        IQueryable<TenantChargeResponse> rows,
        TenantChargeListQuery query) => query.SortField switch
        {
            "dueon" => query.SortDescending
                ? rows.OrderByDescending(row => row.DueOn).ThenByDescending(row => row.TenantLedgerEntryId)
                : rows.OrderBy(row => row.DueOn).ThenBy(row => row.TenantLedgerEntryId),
            "effectiveon" => query.SortDescending
                ? rows.OrderByDescending(row => row.EffectiveOn).ThenByDescending(row => row.TenantLedgerEntryId)
                : rows.OrderBy(row => row.EffectiveOn).ThenBy(row => row.TenantLedgerEntryId),
            "originalamount" => query.SortDescending
                ? rows.OrderByDescending(row => row.OriginalAmount).ThenByDescending(row => row.TenantLedgerEntryId)
                : rows.OrderBy(row => row.OriginalAmount).ThenBy(row => row.TenantLedgerEntryId),
            "openamount" => query.SortDescending
                ? rows.OrderByDescending(row => row.OpenAmount).ThenByDescending(row => row.TenantLedgerEntryId)
                : rows.OrderBy(row => row.OpenAmount).ThenBy(row => row.TenantLedgerEntryId),
            "entrytype" => query.SortDescending
                ? rows.OrderByDescending(row => row.EntryType).ThenByDescending(row => row.TenantLedgerEntryId)
                : rows.OrderBy(row => row.EntryType).ThenBy(row => row.TenantLedgerEntryId),
            _ => rows.OrderBy(row => row.DueOn)
                .ThenBy(row => row.TenantLedgerEntryId),
        };

    private static IQueryable<TenantAccountDepositListItemResponse> ApplyDepositSort(
        IQueryable<TenantAccountDepositListItemResponse> rows,
        TenantAccountDepositListQuery query) => query.SortField switch
        {
            "propertyname" => query.SortDescending
                ? rows.OrderByDescending(row => row.PropertyName)
                    .ThenByDescending(row => row.UnitNumber)
                    .ThenByDescending(row => row.SecurityDepositAccountId)
                : rows.OrderBy(row => row.PropertyName)
                    .ThenBy(row => row.UnitNumber)
                    .ThenBy(row => row.SecurityDepositAccountId),
            "unitnumber" => query.SortDescending
                ? rows.OrderByDescending(row => row.UnitNumber)
                    .ThenByDescending(row => row.SecurityDepositAccountId)
                : rows.OrderBy(row => row.UnitNumber)
                    .ThenBy(row => row.SecurityDepositAccountId),
            "accountnumber" => query.SortDescending
                ? rows.OrderByDescending(row => row.AccountNumber)
                    .ThenByDescending(row => row.SecurityDepositAccountId)
                : rows.OrderBy(row => row.AccountNumber)
                    .ThenBy(row => row.SecurityDepositAccountId),
            "tenantname" => query.SortDescending
                ? rows.OrderByDescending(row => row.PrimaryTenantName)
                    .ThenByDescending(row => row.SecurityDepositAccountId)
                : rows.OrderBy(row => row.PrimaryTenantName)
                    .ThenBy(row => row.SecurityDepositAccountId),
            "status" => query.SortDescending
                ? rows.OrderByDescending(row => row.Status)
                    .ThenByDescending(row => row.SecurityDepositAccountId)
                : rows.OrderBy(row => row.Status)
                    .ThenBy(row => row.SecurityDepositAccountId),
            "heldbalance" => query.SortDescending
                ? rows.OrderByDescending(row => row.HeldBalance)
                    .ThenByDescending(row => row.SecurityDepositAccountId)
                : rows.OrderBy(row => row.HeldBalance)
                    .ThenBy(row => row.SecurityDepositAccountId),
            "createdatutc" => query.SortDescending
                ? rows.OrderByDescending(row => row.CreatedAtUtc)
                    .ThenByDescending(row => row.SecurityDepositAccountId)
                : rows.OrderBy(row => row.CreatedAtUtc)
                    .ThenBy(row => row.SecurityDepositAccountId),
            _ => rows.OrderBy(row => row.PropertyName)
                .ThenBy(row => row.UnitNumber)
                .ThenBy(row => row.SecurityDepositAccountId),
        };

    private sealed class TenantAccountReadIdentity
    {
        public int TenantAccountId { get; init; }
        public int LeaseManagementId { get; init; }
    }

    private sealed class DepositPageSeed
    {
        public int PortfolioId { get; init; }
        public int SecurityDepositAccountId { get; init; }
        public int TenantAccountId { get; init; }
        public int LeaseManagementId { get; init; }
        public int OriginatingAgreementId { get; init; }
        public int PropertyId { get; init; }
        public int UnitId { get; init; }
        public string AccountNumber { get; init; } = string.Empty;
        public string RelationshipNumber { get; init; } = string.Empty;
        public DateTime CreatedAtUtc { get; init; }
    }
}
