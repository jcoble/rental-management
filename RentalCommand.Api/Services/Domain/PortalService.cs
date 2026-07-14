using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using RentalCommand.Api.DTOs;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Atomic;
using RentalCommand.Core.Authorization;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Time;
using RentalCommand.Core.Operations;
using RentalCommand.Data;

namespace RentalCommand.Api.Services.Domain;

/// <inheritdoc cref="IPortalService"/>
public class PortalService : IPortalService
{
    private readonly RentalCommandDbContext _db;
    private readonly ILeaseQaService _leaseQa;
    private readonly TimeProvider _timeProvider;
    private readonly IAtomicUnitOfWork? _atomic;
    private static readonly AtomicJsonResultCodec<OperationMutationResult> WorkOrderMutationCodec =
        new("portal.work-order.mutation.v1");

    public PortalService(RentalCommandDbContext db, ILeaseQaService leaseQa, TimeProvider timeProvider,
        IAtomicUnitOfWork? atomic = null)
    {
        _db = db;
        _leaseQa = leaseQa;
        _timeProvider = timeProvider;
        _atomic = atomic;
    }

    public Task<int?> ResolveTenantIdAsync(
        int portfolioId,
        int accessContextId,
        CancellationToken ct = default) =>
        _db.EffectiveTenantAccess
            .AsNoTracking()
            .Where(access => access.PortfolioId == portfolioId &&
                access.AccessContextId == accessContextId)
            .OrderBy(access => access.LeaseManagementPartyId)
            .Select(access => (int?)access.TenantId)
            .FirstOrDefaultAsync(ct);

    public async Task<IReadOnlyList<PortalLeaseRelationshipResponse>> GetLeasesAsync(
        int portfolioId,
        int accessContextId,
        int tenantId,
        CancellationToken ct = default) =>
        await BuildLeaseRelationshipQuery(portfolioId, accessContextId, tenantId)
            .ToListAsync(ct);

    internal IQueryable<PortalLeaseRelationshipResponse> BuildLeaseRelationshipQuery(
        int portfolioId,
        int accessContextId,
        int tenantId) =>
        from access in EffectiveTenantRelationshipQuery(portfolioId, accessContextId, tenantId)
        join party in _db.LeaseManagementParties.AsNoTracking()
            on new { access.PortfolioId, Id = access.LeaseManagementPartyId }
            equals new { party.PortfolioId, party.Id }
        join management in _db.LeaseManagements.AsNoTracking()
            on new { access.PortfolioId, Id = access.LeaseManagementId }
            equals new { management.PortfolioId, management.Id }
        join lifecycle in _db.LeaseManagementLifecycleProjections.AsNoTracking()
            on new { access.PortfolioId, access.LeaseManagementId }
            equals new { lifecycle.PortfolioId, lifecycle.LeaseManagementId }
        let selectedAgreementId = lifecycle.CurrentAgreementId ?? lifecycle.UpcomingAgreementId
        from agreement in _db.LeaseAgreements.AsNoTracking()
            .Where(item => item.PortfolioId == access.PortfolioId
                && item.Id == selectedAgreementId)
            .DefaultIfEmpty()
        from agreementStatus in _db.LeaseAgreementStatusProjections.AsNoTracking()
            .Where(status => status.PortfolioId == access.PortfolioId
                && status.AgreementId == selectedAgreementId)
            .Select(status => new
            {
                AgreementStatus = (string?)status.AgreementStatus,
                IsGoverning = (bool?)status.IsGoverning,
            })
            .DefaultIfEmpty()
        orderby (DateOnly?)agreement!.TermStartOn descending,
            management.Id descending
        select new PortalLeaseRelationshipResponse
        {
            LeaseManagementId = management.Id,
            LeaseManagementPublicId = management.PublicId,
            PortfolioId = management.PortfolioId,
            PropertyId = management.PropertyId,
            UnitId = management.UnitId,
            TenantId = party.TenantId,
            TenantAccountId = lifecycle.TenantAccountId,
            RelationshipNumber = management.RelationshipNumber,
            Lifecycle = lifecycle.Lifecycle,
            PropertyName = management.Property!.Name,
            UnitNumber = management.Unit!.UnitNumber,
            TenantName = (party.Tenant!.FirstName + " " + party.Tenant.LastName).Trim(),
            Agreement = agreement == null
                ? null
                : new PortalLeaseAgreementResponse
                {
                    LeaseAgreementId = agreement.Id,
                    VersionNumber = agreement.VersionNumber,
                    AgreementNumber = agreement.AgreementNumber,
                    AgreementStatus = agreementStatus.AgreementStatus ?? string.Empty,
                    IsGoverning = agreementStatus.IsGoverning ?? false,
                    ChangeType = agreement.ChangeType,
                    TermType = agreement.TermType,
                    TermStartOn = agreement.TermStartOn,
                    TermEndOn = agreement.TermEndOn,
                    BaseRentAmount = agreement.BaseRentAmount,
                    SecurityDepositObligation = agreement.SecurityDepositObligation,
                    LateFeeAmount = agreement.LateFeeAmount,
                    RentDueDay = agreement.RentDueDay,
                    Currency = agreement.Currency,
                    FullyExecutedAtUtc = agreement.FullyExecutedAtUtc,
                    ExecutedStoredFileId = agreement.ExecutedArtifact == null
                        || agreement.ExecutedArtifact.ArtifactKind != LegalDocumentArtifactKind.ExecutedAgreement
                        || agreement.ExecutedArtifact.StoredFile == null
                        || agreement.ExecutedArtifact.StoredFile.DeletedAt != null
                            ? null
                            : agreement.ExecutedArtifact.StoredFileId,
                },
        };

    public async Task<PortalTenantAccountPageResponse> ListTenantAccountsPageAsync(
        PortalTenantReadScope scope,
        PortalTenantAccountListQuery query,
        CancellationToken ct = default)
    {
        var rows = BuildTenantAccountQuery(scope, query);
        var totalCount = await rows.CountAsync(ct);
        var items = await ApplyTenantAccountSort(rows, query)
            .Skip(query.NormalizedSkip)
            .Take(query.NormalizedTake)
            .ToListAsync(ct);

        return new PortalTenantAccountPageResponse
        {
            Items = items,
            TotalCount = totalCount,
            Skip = query.NormalizedSkip,
            Take = query.NormalizedTake,
        };
    }

    public Task<PortalTenantAccountResponse?> GetTenantAccountAsync(
        PortalTenantReadScope scope,
        int tenantAccountId,
        CancellationToken ct = default) =>
        BuildTenantAccountQuery(scope, new PortalTenantAccountListQuery())
            .SingleOrDefaultAsync(account => account.TenantAccountId == tenantAccountId, ct);

    public async Task<PortalTenantLedgerEntryPageResponse?> ListTenantAccountEntriesPageAsync(
        PortalTenantReadScope scope,
        int tenantAccountId,
        PortalTenantLedgerEntryListQuery query,
        CancellationToken ct = default)
    {
        var identity = await BuildTenantAccountIdentityQuery(scope, tenantAccountId)
            .SingleOrDefaultAsync(ct);
        if (identity is null)
        {
            return null;
        }

        var rows = BuildTenantLedgerEntryQuery(scope, tenantAccountId, query);
        var totalCount = await rows.CountAsync(ct);
        var items = await ApplyTenantEntrySort(rows, query)
            .Skip(query.NormalizedSkip)
            .Take(query.NormalizedTake)
            .ToListAsync(ct);

        return new PortalTenantLedgerEntryPageResponse
        {
            TenantAccountId = identity.TenantAccountId,
            LeaseManagementId = identity.LeaseManagementId,
            Items = items,
            TotalCount = totalCount,
            Skip = query.NormalizedSkip,
            Take = query.NormalizedTake,
        };
    }

    public async Task<PortalTenantChargePageResponse?> ListTenantAccountChargesPageAsync(
        PortalTenantReadScope scope,
        int tenantAccountId,
        PortalTenantChargeListQuery query,
        CancellationToken ct = default)
    {
        var identity = await BuildTenantAccountIdentityQuery(scope, tenantAccountId)
            .SingleOrDefaultAsync(ct);
        if (identity is null)
        {
            return null;
        }

        var rows = BuildTenantChargeQuery(scope, tenantAccountId, query);
        var totalCount = await rows.CountAsync(ct);
        var items = await ApplyTenantChargeSort(rows, query)
            .Skip(query.NormalizedSkip)
            .Take(query.NormalizedTake)
            .ToListAsync(ct);

        return new PortalTenantChargePageResponse
        {
            TenantAccountId = identity.TenantAccountId,
            LeaseManagementId = identity.LeaseManagementId,
            Items = items,
            TotalCount = totalCount,
            Skip = query.NormalizedSkip,
            Take = query.NormalizedTake,
        };
    }

    public Task<PortalTenantAccountDepositResponse?> GetTenantAccountDepositAsync(
        PortalTenantReadScope scope,
        int tenantAccountId,
        CancellationToken ct = default) =>
        BuildTenantAccountDepositQuery(scope, tenantAccountId).SingleOrDefaultAsync(ct);

    internal IQueryable<PortalTenantAccountResponse> BuildTenantAccountQuery(
        PortalTenantReadScope scope,
        PortalTenantAccountListQuery query)
    {
        var rows =
            from account in BuildAuthorizedTenantAccountQuery(scope)
            join management in _db.LeaseManagements.AsNoTracking()
                on new { account.PortfolioId, Id = account.LeaseManagementId }
                equals new { management.PortfolioId, management.Id }
            join lifecycle in _db.LeaseManagementLifecycleProjections.AsNoTracking()
                on new { management.PortfolioId, LeaseManagementId = management.Id }
                equals new { lifecycle.PortfolioId, lifecycle.LeaseManagementId }
            join balance in _db.TenantAccountBalanceProjections.AsNoTracking()
                on new { account.PortfolioId, TenantAccountId = account.Id }
                equals new { balance.PortfolioId, balance.TenantAccountId }
            from agreement in _db.LeaseAgreements.AsNoTracking()
                .Where(item => item.PortfolioId == management.PortfolioId
                    && item.Id == lifecycle.CurrentAgreementId)
                .DefaultIfEmpty()
            from agreementStatus in _db.LeaseAgreementStatusProjections.AsNoTracking()
                .Where(status => status.PortfolioId == management.PortfolioId
                    && status.AgreementId == lifecycle.CurrentAgreementId)
                .Select(status => new
                {
                    AgreementStatus = (string?)status.AgreementStatus,
                    IsGoverning = (bool?)status.IsGoverning,
                })
                .DefaultIfEmpty()
            from depositAccount in _db.SecurityDepositAccounts.AsNoTracking()
                .Where(deposit => deposit.PortfolioId == account.PortfolioId
                    && deposit.TenantAccountId == account.Id)
                .DefaultIfEmpty()
            from depositBalance in _db.SecurityDepositBalanceProjections.AsNoTracking()
                .Where(deposit => deposit.PortfolioId == account.PortfolioId
                    && deposit.SecurityDepositAccountId == depositAccount.Id)
                .Select(deposit => new
                {
                    IsPresent = (bool?)true,
                    deposit.Currency,
                    EffectiveNowUtc = (DateTime?)deposit.EffectiveNowUtc,
                    BusinessDate = (DateOnly?)deposit.BusinessDate,
                    TotalReceived = (decimal?)deposit.TotalReceived,
                    TotalDeductions = (decimal?)deposit.TotalDeductions,
                    TotalRefunded = (decimal?)deposit.TotalRefunded,
                    TotalTransferredIn = (decimal?)deposit.TotalTransferredIn,
                    TotalTransferredOut = (decimal?)deposit.TotalTransferredOut,
                    NetAdjustments = (decimal?)deposit.NetAdjustments,
                    HeldBalance = (decimal?)deposit.HeldBalance,
                    deposit.DepositStatus,
                })
                .DefaultIfEmpty()
            select new PortalTenantAccountResponse
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
                Lifecycle = lifecycle.Lifecycle,
                Currency = balance.Currency,
                OpenedAtUtc = account.OpenedAtUtc,
                ClosedAtUtc = account.ClosedAtUtc,
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
                CurrentAgreement = agreement == null
                    ? null
                    : new PortalLeaseAgreementResponse
                    {
                        LeaseAgreementId = agreement.Id,
                        VersionNumber = agreement.VersionNumber,
                        AgreementNumber = agreement.AgreementNumber,
                        AgreementStatus = agreementStatus.AgreementStatus ?? string.Empty,
                        IsGoverning = agreementStatus.IsGoverning ?? false,
                        ChangeType = agreement.ChangeType,
                        TermType = agreement.TermType,
                        TermStartOn = agreement.TermStartOn,
                        TermEndOn = agreement.TermEndOn,
                        BaseRentAmount = agreement.BaseRentAmount,
                        SecurityDepositObligation = agreement.SecurityDepositObligation,
                        LateFeeAmount = agreement.LateFeeAmount,
                        RentDueDay = agreement.RentDueDay,
                        Currency = agreement.Currency,
                        FullyExecutedAtUtc = agreement.FullyExecutedAtUtc,
                        ExecutedStoredFileId = agreement.ExecutedArtifact == null
                            || agreement.ExecutedArtifact.ArtifactKind
                                != LegalDocumentArtifactKind.ExecutedAgreement
                            || agreement.ExecutedArtifact.StoredFile == null
                            || agreement.ExecutedArtifact.StoredFile.DeletedAt != null
                                ? null
                                : agreement.ExecutedArtifact.StoredFileId,
                    },
                Deposit = depositAccount == null || depositBalance.IsPresent != true
                    ? null
                    : new PortalTenantAccountDepositResponse
                    {
                        TenantAccountId = account.Id,
                        LeaseManagementId = management.Id,
                        SecurityDepositAccountId = depositAccount.Id,
                        OriginatingAgreementId = depositAccount.OriginatingAgreementId,
                        Currency = depositBalance.Currency!,
                        CreatedAtUtc = depositAccount.CreatedAtUtc,
                        EffectiveNowUtc = depositBalance.EffectiveNowUtc!.Value,
                        BusinessDate = depositBalance.BusinessDate!.Value,
                        TotalReceived = depositBalance.TotalReceived!.Value,
                        TotalDeductions = depositBalance.TotalDeductions!.Value,
                        TotalRefunded = depositBalance.TotalRefunded!.Value,
                        TotalTransferredIn = depositBalance.TotalTransferredIn!.Value,
                        TotalTransferredOut = depositBalance.TotalTransferredOut!.Value,
                        NetAdjustments = depositBalance.NetAdjustments!.Value,
                        HeldBalance = depositBalance.HeldBalance!.Value,
                        Status = depositBalance.DepositStatus,
                    },
            };

        if (!string.IsNullOrWhiteSpace(query.Lifecycle))
        {
            var lifecycle = query.Lifecycle.Trim();
            rows = rows.Where(row => row.Lifecycle == lifecycle);
        }
        if (query.Closed.HasValue)
        {
            rows = query.Closed.Value
                ? rows.Where(row => row.ClosedAtUtc != null)
                : rows.Where(row => row.ClosedAtUtc == null);
        }
        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var like = $"%{query.Search.Trim()}%";
            rows = rows.Where(row =>
                EF.Functions.ILike(row.AccountNumber, like)
                || EF.Functions.ILike(row.RelationshipNumber, like)
                || EF.Functions.ILike(row.PropertyName, like)
                || EF.Functions.ILike(row.UnitNumber, like));
        }
        return rows;
    }

    internal IQueryable<PortalTenantAccountResponse> BuildTenantAccountPageQuery(
        PortalTenantReadScope scope,
        PortalTenantAccountListQuery query) =>
        ApplyTenantAccountSort(BuildTenantAccountQuery(scope, query), query)
            .Skip(query.NormalizedSkip)
            .Take(query.NormalizedTake);

    internal IQueryable<PortalTenantLedgerEntryResponse> BuildTenantLedgerEntryQuery(
        PortalTenantReadScope scope,
        int tenantAccountId,
        PortalTenantLedgerEntryListQuery query)
    {
        var rows =
            from account in BuildAuthorizedTenantAccountQuery(scope)
            join entry in _db.TenantLedgerEntries.AsNoTracking()
                on new { account.PortfolioId, TenantAccountId = account.Id }
                equals new { entry.PortfolioId, entry.TenantAccountId }
            where account.Id == tenantAccountId
            select new PortalTenantLedgerEntryResponse
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
            };

        if (query.EntryType.HasValue)
        {
            rows = rows.Where(row => row.EntryType == query.EntryType.Value);
        }
        if (query.Direction.HasValue)
        {
            rows = rows.Where(row => row.Direction == query.Direction.Value);
        }
        return ApplyTenantEntryFilters(rows, query.Search, query.From, query.To);
    }

    internal IQueryable<PortalTenantLedgerEntryResponse> BuildTenantLedgerEntryPageQuery(
        PortalTenantReadScope scope,
        int tenantAccountId,
        PortalTenantLedgerEntryListQuery query) =>
        ApplyTenantEntrySort(BuildTenantLedgerEntryQuery(scope, tenantAccountId, query), query)
            .Skip(query.NormalizedSkip)
            .Take(query.NormalizedTake);

    internal IQueryable<PortalTenantChargeResponse> BuildTenantChargeQuery(
        PortalTenantReadScope scope,
        int tenantAccountId,
        PortalTenantChargeListQuery query)
    {
        var rows =
            from account in BuildAuthorizedTenantAccountQuery(scope)
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
            select new PortalTenantChargeResponse
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
        return ApplyTenantChargeFilters(rows, query.Search, query.From, query.To);
    }

    internal IQueryable<PortalTenantChargeResponse> BuildTenantChargePageQuery(
        PortalTenantReadScope scope,
        int tenantAccountId,
        PortalTenantChargeListQuery query) =>
        ApplyTenantChargeSort(BuildTenantChargeQuery(scope, tenantAccountId, query), query)
            .Skip(query.NormalizedSkip)
            .Take(query.NormalizedTake);

    internal IQueryable<PortalTenantAccountDepositResponse> BuildTenantAccountDepositQuery(
        PortalTenantReadScope scope,
        int tenantAccountId) =>
        from account in BuildAuthorizedTenantAccountQuery(scope)
        join deposit in _db.SecurityDepositAccounts.AsNoTracking()
            on new { account.PortfolioId, TenantAccountId = account.Id }
            equals new { deposit.PortfolioId, deposit.TenantAccountId }
        join balance in _db.SecurityDepositBalanceProjections.AsNoTracking()
            on new { deposit.PortfolioId, SecurityDepositAccountId = deposit.Id }
            equals new { balance.PortfolioId, balance.SecurityDepositAccountId }
        where account.Id == tenantAccountId
        select new PortalTenantAccountDepositResponse
        {
            TenantAccountId = account.Id,
            LeaseManagementId = account.LeaseManagementId,
            SecurityDepositAccountId = deposit.Id,
            OriginatingAgreementId = deposit.OriginatingAgreementId,
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

    internal IQueryable<TenantAccount> BuildAuthorizedTenantAccountQuery(PortalTenantReadScope scope) =>
        _db.TenantAccounts.AsNoTracking().Where(account =>
            account.PortfolioId == scope.PortfolioId
            && _db.EffectiveTenantAccess.AsNoTracking().Any(access =>
                access.PortfolioId == scope.PortfolioId
                && access.UserId == scope.UserId
                && access.AccessContextId == scope.AccessContextId
                && access.AccessRevision == scope.AccessRevision
                && access.TenantAccountId == account.Id
                && access.LeaseManagementId == account.LeaseManagementId));

    private IQueryable<PortalTenantAccountIdentity> BuildTenantAccountIdentityQuery(
        PortalTenantReadScope scope,
        int tenantAccountId) =>
        BuildAuthorizedTenantAccountQuery(scope)
            .Where(account => account.Id == tenantAccountId)
            .Select(account => new PortalTenantAccountIdentity
            {
                TenantAccountId = account.Id,
                LeaseManagementId = account.LeaseManagementId,
            });

    private static IQueryable<PortalTenantLedgerEntryResponse> ApplyTenantEntryFilters(
        IQueryable<PortalTenantLedgerEntryResponse> rows,
        string? search,
        DateTime? from,
        DateTime? to)
    {
        if (!string.IsNullOrWhiteSpace(search))
        {
            var like = $"%{search.Trim()}%";
            rows = rows.Where(row => EF.Functions.ILike(row.Description, like)
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

    private static IQueryable<PortalTenantChargeResponse> ApplyTenantChargeFilters(
        IQueryable<PortalTenantChargeResponse> rows,
        string? search,
        DateTime? from,
        DateTime? to)
    {
        if (!string.IsNullOrWhiteSpace(search))
        {
            var like = $"%{search.Trim()}%";
            rows = rows.Where(row => EF.Functions.ILike(row.Description, like)
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

    private static IQueryable<PortalTenantAccountResponse> ApplyTenantAccountSort(
        IQueryable<PortalTenantAccountResponse> rows,
        PortalTenantAccountListQuery query) => query.SortField switch
        {
            "accountnumber" => query.SortDescending
                ? rows.OrderByDescending(row => row.AccountNumber).ThenByDescending(row => row.TenantAccountId)
                : rows.OrderBy(row => row.AccountNumber).ThenBy(row => row.TenantAccountId),
            "propertyname" => query.SortDescending
                ? rows.OrderByDescending(row => row.PropertyName)
                    .ThenByDescending(row => row.UnitNumber)
                    .ThenByDescending(row => row.TenantAccountId)
                : rows.OrderBy(row => row.PropertyName)
                    .ThenBy(row => row.UnitNumber)
                    .ThenBy(row => row.TenantAccountId),
            "balance" => query.SortDescending
                ? rows.OrderByDescending(row => row.ReceivableBalance).ThenByDescending(row => row.TenantAccountId)
                : rows.OrderBy(row => row.ReceivableBalance).ThenBy(row => row.TenantAccountId),
            _ => rows.OrderByDescending(row => row.OpenedAtUtc)
                .ThenByDescending(row => row.TenantAccountId),
        };

    private static IQueryable<PortalTenantLedgerEntryResponse> ApplyTenantEntrySort(
        IQueryable<PortalTenantLedgerEntryResponse> rows,
        PortalTenantLedgerEntryListQuery query) => query.SortField switch
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
            _ => rows.OrderByDescending(row => row.EffectiveOn)
                .ThenByDescending(row => row.TenantLedgerEntryId),
        };

    private static IQueryable<PortalTenantChargeResponse> ApplyTenantChargeSort(
        IQueryable<PortalTenantChargeResponse> rows,
        PortalTenantChargeListQuery query) => query.SortField switch
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
            _ => rows.OrderBy(row => row.DueOn)
                .ThenBy(row => row.TenantLedgerEntryId),
        };

    private sealed class PortalTenantAccountIdentity
    {
        public int TenantAccountId { get; init; }
        public int LeaseManagementId { get; init; }
    }

    public async Task<IReadOnlyList<AppointmentResponse>> GetAppointmentsAsync(
        int portfolioId,
        int accessContextId,
        int tenantId,
        CancellationToken ct = default)
    {
        var now = _timeProvider.UtcNow();

        return await BuildAppointmentsQuery(portfolioId, accessContextId, tenantId, now)
            .ToListAsync(ct);
    }

    internal IQueryable<AppointmentResponse> BuildAppointmentsQuery(
        int portfolioId,
        int accessContextId,
        int tenantId,
        DateTime now)
    {
        var effectiveRelationships = EffectiveTenantRelationshipQuery(
            portfolioId, accessContextId, tenantId);

        return _db.Appointments
            .AsNoTracking()
            .Where(appointment => appointment.PortfolioId == portfolioId
                && appointment.TenantId == tenantId
                && appointment.ScheduledStart >= now
                && (appointment.Status == AppointmentStatus.Scheduled
                    || appointment.Status == AppointmentStatus.Confirmed)
                && effectiveRelationships.Any(access =>
                    appointment.LeaseManagementId == null
                    || access.LeaseManagementId == appointment.LeaseManagementId))
            .OrderBy(appointment => appointment.ScheduledStart)
            .ThenBy(appointment => appointment.Id)
            .Take(50)
            .Select(appointment => new AppointmentResponse
            {
                Id = appointment.Id,
                PortfolioId = appointment.PortfolioId,
                PropertyId = appointment.PropertyId,
                UnitId = appointment.UnitId,
                LeaseManagementId = appointment.LeaseManagementId,
                TenantId = appointment.TenantId,
                Title = appointment.Title,
                ProspectName = appointment.ProspectName,
                ProspectEmail = appointment.ProspectEmail,
                Type = appointment.Type,
                Status = appointment.Status,
                ScheduledStart = appointment.ScheduledStart,
                ScheduledEnd = appointment.ScheduledEnd,
                AssignedTo = appointment.AssignedTo,
                Notes = appointment.Notes,
                CreatedAt = appointment.CreatedAt,
                UpdatedAt = appointment.UpdatedAt,
                PropertyName = appointment.Property == null
                    ? (from access in effectiveRelationships
                       join management in _db.LeaseManagements.AsNoTracking()
                           on new { access.PortfolioId, Id = access.LeaseManagementId }
                           equals new { management.PortfolioId, management.Id }
                       join lifecycle in _db.LeaseManagementLifecycleProjections.AsNoTracking()
                           on new { access.PortfolioId, access.LeaseManagementId }
                           equals new { lifecycle.PortfolioId, lifecycle.LeaseManagementId }
                       where appointment.LeaseManagementId == null
                           || appointment.LeaseManagementId == management.Id
                       orderby lifecycle.CurrentAgreementId != null descending,
                           management.PossessionGivenAtUtc descending,
                           management.Id descending
                       select management.Property!.Name)
                        .FirstOrDefault()
                    : appointment.Property.Name,
                UnitNumber = appointment.Unit == null
                    ? (from access in effectiveRelationships
                       join management in _db.LeaseManagements.AsNoTracking()
                           on new { access.PortfolioId, Id = access.LeaseManagementId }
                           equals new { management.PortfolioId, management.Id }
                       join lifecycle in _db.LeaseManagementLifecycleProjections.AsNoTracking()
                           on new { access.PortfolioId, access.LeaseManagementId }
                           equals new { lifecycle.PortfolioId, lifecycle.LeaseManagementId }
                       where appointment.LeaseManagementId == null
                           || appointment.LeaseManagementId == management.Id
                       orderby lifecycle.CurrentAgreementId != null descending,
                           management.PossessionGivenAtUtc descending,
                           management.Id descending
                       select management.Unit!.UnitNumber)
                        .FirstOrDefault()
                    : appointment.Unit.UnitNumber,
                TenantName = appointment.Tenant == null
                    ? null
                    : (appointment.Tenant.FirstName + " " + appointment.Tenant.LastName).Trim(),
            });
    }

    public async Task<IReadOnlyList<WorkOrderResponse>> GetWorkOrdersAsync(int portfolioId, int tenantId, CancellationToken ct = default)
    {
        return await _db.WorkOrders
            .AsNoTracking()
            .Where(w => w.PortfolioId == portfolioId && w.TenantId == tenantId)
            .OrderByDescending(w => w.RequestedAt)
            .Select(w => new WorkOrderResponse
            {
                Id = w.Id,
                PortfolioId = w.PortfolioId,
                PropertyId = w.PropertyId,
                UnitId = w.UnitId,
                TenantId = w.TenantId,
                LeaseManagementId = w.LeaseManagementId,
                VendorId = w.VendorId,
                RecurringMaintenanceTaskId = w.RecurringMaintenanceTaskId,
                Title = w.Title,
                Description = w.Description,
                Category = w.Category,
                Priority = w.Priority,
                Status = w.Status,
                RequestedAt = w.RequestedAt,
                ScheduledFor = w.ScheduledFor,
                ScheduledWindowEnd = w.ScheduledWindowEnd,
                CompletedAt = w.CompletedAt,
                EstimatedCost = w.EstimatedCost,
                ActualCost = w.ActualCost,
                CreatedBy = w.CreatedBy,
                UpdatedAt = w.UpdatedAt,
                PropertyName = w.Property == null ? null : w.Property.Name,
                UnitNumber = w.Unit == null ? null : w.Unit.UnitNumber,
                VendorName = w.Vendor == null ? null : w.Vendor.Name,
                TenantName = w.Tenant == null
                    ? null
                    : (w.Tenant.FirstName + " " + w.Tenant.LastName).Trim(),
            })
            .ToListAsync(ct);
    }

    public async Task<WorkOrderDetailResponse?> GetWorkOrderDetailAsync(
        int portfolioId, int tenantId, int workOrderId, CancellationToken ct = default)
    {
        // Ownership is part of the lookup: a work order on another tenant's lease/unit simply isn't
        // found, so we never leak its existence or its timeline. Mirrors the lease-ledger restriction.
        var workOrder = await _db.WorkOrders
            .AsNoTracking()
            .FirstOrDefaultAsync(
                w => w.Id == workOrderId && w.PortfolioId == portfolioId && w.TenantId == tenantId, ct);
        if (workOrder is null)
        {
            return null;
        }

        var events = await _db.WorkOrderStatusEvents
            .AsNoTracking()
            .Where(e => e.WorkOrderId == workOrderId && e.PortfolioId == portfolioId)
            .OrderBy(e => e.CreatedAtUtc)
            .ThenBy(e => e.Id)
            .ToListAsync(ct);

        return WorkOrderDetailResponse.FromEntity(workOrder, events);
    }

    public async Task<WorkOrderResponse?> CreateTenantWorkOrderAsync(
        ActiveAccessContext access,
        CreateTenantWorkOrderRequest request,
        string idempotencyKey,
        CancellationToken ct = default)
    {
        var command = new CreateTenantWorkOrderCommand(
            access.PortfolioId, access.UserId, access.SessionId, access.AccessContextId,
            access.AccessRevision, request.Title, request.Description, request.Category,
            request.Priority, idempotencyKey);
        var digest = Convert.ToHexString(SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(idempotencyKey)));
        var outcome = await (_atomic ?? throw new InvalidOperationException(
                "Atomic tenant work-order mutations are not configured."))
            .ExecuteAsync(new AtomicCommandIdentity("portal.work-order.create", digest),
                command, WorkOrderMutationCodec, ct);
        return outcome.Value.Outcome == OperationMutationOutcome.NotFound || outcome.Value.ResponseJson is null
            ? null
            : JsonSerializer.Deserialize<WorkOrderResponse>(outcome.Value.ResponseJson);
    }

    public async Task<LeaseQuestionResponse?> AskLeaseAsync(
        int portfolioId,
        int accessContextId,
        int tenantId,
        int? leaseManagementId,
        string question,
        CancellationToken ct = default)
    {
        var resolvedLeaseManagementId = await BuildOwnedLeaseManagementQuery(
                portfolioId, accessContextId, tenantId, leaseManagementId)
            .FirstOrDefaultAsync(ct);
        if (resolvedLeaseManagementId == null)
        {
            return null;
        }

        return await _leaseQa.AskAsync(
            portfolioId, resolvedLeaseManagementId.Value, question, ct);
    }

    public async Task<AutopayStatusResponse?> GetAutopayStatusAsync(
        int portfolioId, int tenantId, int tenantAccountId, CancellationToken ct = default)
    {
        var businessDate = DateOnly.FromDateTime(_timeProvider.UtcNow());
        return await OwnedAutopayQuery(portfolioId, tenantId, tenantAccountId, businessDate)
            .AsNoTracking()
            .Select(row => new AutopayStatusResponse
            {
                TenantAccountId = row.Account.Id,
                Active = row.Enrollment != null,
                EnrolledAt = row.Enrollment == null ? null : row.Enrollment.EnrolledAtUtc,
                OnlinePaymentsAvailable = true,
            })
            .FirstOrDefaultAsync(ct);
    }

    public async Task<AutopayStatusResponse?> CancelAutopayAsync(
        ActiveAccessContext access,
        int tenantId,
        int tenantAccountId,
        string operationKey,
        CancellationToken ct = default)
    {
        var atomic = _atomic
            ?? throw new InvalidOperationException("Atomic tenant autopay cancellation is not configured.");
        var command = AtomicTenantAutopayCancellation.Command(
            access, tenantId, tenantAccountId, operationKey);
        var outcome = await atomic.ExecuteAsync(
            AtomicTenantAutopayCancellation.Identity(command),
            command,
            AtomicTenantAutopayCancellation.Codec,
            ct);
        if (!outcome.Value.Found) return null;

        return new AutopayStatusResponse
        {
            TenantAccountId = tenantAccountId,
            Active = false,
            OnlinePaymentsAvailable = true,
        };
    }

    private IQueryable<OwnedAutopayRow> OwnedAutopayQuery(
        int portfolioId, int tenantId, int tenantAccountId, DateOnly businessDate) =>
        from account in _db.TenantAccounts
        join party in _db.LeaseManagementParties
            on new { account.LeaseManagementId, account.PortfolioId }
            equals new { party.LeaseManagementId, party.PortfolioId }
        join access in _db.TenantUserAccesses
            on new { LeaseManagementPartyId = party.Id, party.PortfolioId }
            equals new { access.LeaseManagementPartyId, access.PortfolioId }
        from enrollment in _db.TenantAutopayEnrollments
            .Where(item => item.TenantAccountId == account.Id
                && item.PortfolioId == account.PortfolioId
                && item.CanceledAtUtc == null)
            .DefaultIfEmpty()
        where account.Id == tenantAccountId
            && account.PortfolioId == portfolioId
            && account.ClosedAtUtc == null
            && party.TenantId == tenantId
            && party.EffectiveFrom <= businessDate
            && (party.EffectiveThrough == null || party.EffectiveThrough >= businessDate)
            && party.Role != LeaseManagementPartyRole.Occupant
            && access.RevokedAtUtc == null
        orderby party.Id
        select new OwnedAutopayRow(account, enrollment);

    private sealed record OwnedAutopayRow(TenantAccount Account, TenantAutopayEnrollment? Enrollment);

    internal IQueryable<int?> BuildOwnedLeaseManagementQuery(
        int portfolioId,
        int accessContextId,
        int tenantId,
        int? leaseManagementId) =>
        from access in EffectiveTenantRelationshipQuery(portfolioId, accessContextId, tenantId)
        join party in _db.LeaseManagementParties.AsNoTracking()
            on new { access.PortfolioId, Id = access.LeaseManagementPartyId }
            equals new { party.PortfolioId, party.Id }
        join management in _db.LeaseManagements.AsNoTracking()
            on new { access.PortfolioId, Id = access.LeaseManagementId }
            equals new { management.PortfolioId, management.Id }
        join lifecycle in _db.LeaseManagementLifecycleProjections.AsNoTracking()
            on new { access.PortfolioId, access.LeaseManagementId }
            equals new { lifecycle.PortfolioId, lifecycle.LeaseManagementId }
        where (leaseManagementId == null || management.Id == leaseManagementId)
            && lifecycle.CurrentAgreementId != null
            && party.TenantId == tenantId
        orderby (lifecycle.Lifecycle == "Occupied" || lifecycle.Lifecycle == "Ending") descending,
            management.PossessionGivenAtUtc descending,
            management.Id descending
        select (int?)management.Id;

    private IQueryable<RentalCommand.Data.Authorization.EffectiveTenantAccessProjection> EffectiveTenantRelationshipQuery(
        int portfolioId,
        int accessContextId,
        int tenantId) =>
        _db.EffectiveTenantAccess
            .AsNoTracking()
            .Where(access => access.PortfolioId == portfolioId
                && access.AccessContextId == accessContextId
                && access.TenantId == tenantId);
}
