using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;
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
    private static readonly AtomicJsonResultCodec<WorkOrderMutationResult> WorkOrderMutationCodec =
        new("portal.work-order.mutation.v2");
    private static readonly JsonSerializerOptions PortalHistoryJsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

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
                    ExecutedDocumentAvailable = agreement.FullyExecutedAtUtc != null
                        && agreement.VoidedAtUtc == null
                        && agreement.ExecutedArtifact != null
                        && agreement.ExecutedArtifact.ArtifactKind == LegalDocumentArtifactKind.ExecutedAgreement
                        && agreement.ExecutedArtifact.StoredFile != null
                        && agreement.ExecutedArtifact.StoredFile.DeletedAt == null,
                    ExecutedDocumentFileName = agreement.FullyExecutedAtUtc == null
                        || agreement.VoidedAtUtc != null
                        || agreement.ExecutedArtifact == null
                        || agreement.ExecutedArtifact.ArtifactKind != LegalDocumentArtifactKind.ExecutedAgreement
                        || agreement.ExecutedArtifact.StoredFile == null
                        || agreement.ExecutedArtifact.StoredFile.DeletedAt != null
                            ? null
                            : agreement.ExecutedArtifact.FileName,
                    ExecutedDocumentContentType = agreement.FullyExecutedAtUtc == null
                        || agreement.VoidedAtUtc != null
                        || agreement.ExecutedArtifact == null
                        || agreement.ExecutedArtifact.ArtifactKind != LegalDocumentArtifactKind.ExecutedAgreement
                        || agreement.ExecutedArtifact.StoredFile == null
                        || agreement.ExecutedArtifact.StoredFile.DeletedAt != null
                            ? null
                            : agreement.ExecutedArtifact.ContentType,
                },
        };

    public Task<LegalArtifactFileReference?> GetExecutedAgreementArtifactAsync(
        PortalTenantReadScope scope,
        int leaseManagementId,
        int leaseAgreementId,
        CancellationToken ct = default) =>
        BuildExecutedAgreementArtifactQuery(scope, leaseManagementId, leaseAgreementId)
            .SingleOrDefaultAsync(ct);

    internal IQueryable<LegalArtifactFileReference> BuildExecutedAgreementArtifactQuery(
        PortalTenantReadScope scope,
        int leaseManagementId,
        int leaseAgreementId) =>
            from access in _db.EffectiveTenantAccess.AsNoTracking()
            join agreement in _db.LeaseAgreements.AsNoTracking()
                on new { access.PortfolioId, access.LeaseManagementId }
                equals new { agreement.PortfolioId, agreement.LeaseManagementId }
            join artifact in _db.LegalDocumentArtifacts.AsNoTracking()
                on new { agreement.PortfolioId, ArtifactId = agreement.ExecutedArtifactId!.Value }
                equals new { artifact.PortfolioId, ArtifactId = artifact.Id }
            join storedFile in _db.StoredFiles.AsNoTracking()
                on new { artifact.PortfolioId, StoredFileId = artifact.StoredFileId }
                equals new { storedFile.PortfolioId, StoredFileId = storedFile.Id }
            where access.PortfolioId == scope.PortfolioId
                && access.UserId == scope.UserId
                && access.AccessContextId == scope.AccessContextId
                && access.AccessRevision == scope.AccessRevision
                && access.LeaseManagementId == leaseManagementId
                && agreement.Id == leaseAgreementId
                && agreement.FullyExecutedAtUtc != null
                && agreement.ExecutedArtifactId != null
                && agreement.VoidedAtUtc == null
                && artifact.ArtifactKind == LegalDocumentArtifactKind.ExecutedAgreement
                && storedFile.DeletedAt == null
            select new LegalArtifactFileReference(
                storedFile.Id,
                artifact.StorageKey,
                artifact.FileName,
                artifact.ContentType);

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

    public async Task<PortalTenantAccountHistoryResponse?> GetTenantAccountHistoryAsync(
        PortalTenantReadScope scope,
        int tenantAccountId,
        PortalTenantAccountHistoryQuery query,
        CancellationToken ct = default)
    {
        var from = query.From.HasValue ? DateOnly.FromDateTime(query.From.Value) : (DateOnly?)null;
        var to = query.To.HasValue ? DateOnly.FromDateTime(query.To.Value) : (DateOnly?)null;
        var rows = await _db.Database.SqlQueryRaw<PortalTenantAccountHistorySqlRow>(
            """
            WITH history AS (
                SELECT *
                FROM rc_portal_tenant_account_history(
                    @portfolio_id, @user_id, @access_context_id, @access_revision,
                    @tenant_account_id, @period, @from_on, @to_on, @skip, @take, @focused_entry_id)
            )
            SELECT history."TenantAccountId",
                   history."LeaseManagementId",
                   history."Currency",
                   history."BusinessDate",
                   history."Period",
                   history."PeriodFrom",
                   history."PeriodTo",
                   history."CurrentDue",
                   history."BeginningBalance",
                   history."ClosingBalance",
                   history."TotalCount",
                   COALESCE((
                       SELECT jsonb_agg(
                           item.value || jsonb_build_object(
                               'allocations', COALESCE((
                                   SELECT jsonb_agg(
                                       jsonb_build_object(
                                           'allocationId', allocation."Id",
                                           'targetSourceId', debit."Id",
                                           'targetPublicId', debit."PublicId",
                                           'targetDescription', debit."Description",
                                           'amount', allocation."Amount",
                                           'effectiveOn', debit."EffectiveOn")
                                       ORDER BY debit."EffectiveOn", debit."Id", allocation."Id")
                                   FROM "TenantLedgerAllocations" AS allocation
                                   JOIN "TenantLedgerEntries" AS debit
                                     ON debit."PortfolioId" = allocation."PortfolioId"
                                    AND debit."TenantAccountId" = allocation."TenantAccountId"
                                    AND debit."Id" = allocation."DebitEntryId"
                                   WHERE allocation."PortfolioId" = @portfolio_id
                                     AND allocation."TenantAccountId" = @tenant_account_id
                                     AND allocation."CreditEntryId" = (item.value->>'tenantLedgerEntryId')::bigint
                               ), '[]'::jsonb)
                           ) ORDER BY item.ordinal)
                       FROM jsonb_array_elements(history."ItemsJson"::jsonb)
                           WITH ORDINALITY AS item(value, ordinal)
                   ), '[]'::jsonb)::text AS "ItemsJson"
            FROM history
            """,
                new NpgsqlParameter<int>("portfolio_id", scope.PortfolioId),
                new NpgsqlParameter<int>("user_id", scope.UserId),
                new NpgsqlParameter<int>("access_context_id", scope.AccessContextId),
                new NpgsqlParameter<long>("access_revision", scope.AccessRevision),
                new NpgsqlParameter<int>("tenant_account_id", tenantAccountId),
                new NpgsqlParameter<string>("period", query.NormalizedPeriod),
                new NpgsqlParameter("from_on", NpgsqlDbType.Date)
                {
                    Value = from.HasValue ? from.Value : DBNull.Value,
                },
                new NpgsqlParameter("to_on", NpgsqlDbType.Date)
                {
                    Value = to.HasValue ? to.Value : DBNull.Value,
                },
                new NpgsqlParameter<int>("skip", query.NormalizedSkip),
                new NpgsqlParameter<int>("take", query.NormalizedTake),
                new NpgsqlParameter("focused_entry_id", NpgsqlDbType.Bigint)
                {
                    Value = query.FocusedEntryId.HasValue
                        ? query.FocusedEntryId.Value
                        : DBNull.Value,
                })
            .ToListAsync(ct);

        if (rows.Count == 0)
        {
            return null;
        }

        var summary = rows[0];
        return new PortalTenantAccountHistoryResponse
        {
            TenantAccountId = summary.TenantAccountId,
            LeaseManagementId = summary.LeaseManagementId,
            Currency = summary.Currency,
            BusinessDate = summary.BusinessDate,
            Period = summary.Period,
            PeriodFrom = summary.PeriodFrom,
            PeriodTo = summary.PeriodTo,
            CurrentDue = summary.CurrentDue,
            BeginningBalance = summary.BeginningBalance,
            ClosingBalance = summary.ClosingBalance,
            TotalCount = summary.TotalCount,
            Skip = query.NormalizedSkip,
            Take = query.NormalizedTake,
            Items = JsonSerializer.Deserialize<List<PortalTenantAccountHistoryItemResponse>>(
                summary.ItemsJson,
                PortalHistoryJsonOptions) ?? [],
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
                        ExecutedDocumentAvailable = agreement.FullyExecutedAtUtc != null
                            && agreement.VoidedAtUtc == null
                            && agreement.ExecutedArtifact != null
                            && agreement.ExecutedArtifact.ArtifactKind
                                == LegalDocumentArtifactKind.ExecutedAgreement
                            && agreement.ExecutedArtifact.StoredFile != null
                            && agreement.ExecutedArtifact.StoredFile.DeletedAt == null,
                        ExecutedDocumentFileName = agreement.FullyExecutedAtUtc == null
                            || agreement.VoidedAtUtc != null
                            || agreement.ExecutedArtifact == null
                            || agreement.ExecutedArtifact.ArtifactKind
                                != LegalDocumentArtifactKind.ExecutedAgreement
                            || agreement.ExecutedArtifact.StoredFile == null
                            || agreement.ExecutedArtifact.StoredFile.DeletedAt != null
                                ? null
                                : agreement.ExecutedArtifact.FileName,
                        ExecutedDocumentContentType = agreement.FullyExecutedAtUtc == null
                            || agreement.VoidedAtUtc != null
                            || agreement.ExecutedArtifact == null
                            || agreement.ExecutedArtifact.ArtifactKind
                                != LegalDocumentArtifactKind.ExecutedAgreement
                            || agreement.ExecutedArtifact.StoredFile == null
                            || agreement.ExecutedArtifact.StoredFile.DeletedAt != null
                                ? null
                                : agreement.ExecutedArtifact.ContentType,
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
            join balance in _db.TenantAccountBalanceProjections.AsNoTracking()
                on new { account.PortfolioId, TenantAccountId = account.Id }
                equals new { balance.PortfolioId, balance.TenantAccountId }
            from reversedEntry in _db.TenantLedgerEntries.AsNoTracking()
                .Where(original => original.PortfolioId == entry.PortfolioId
                    && original.TenantAccountId == entry.TenantAccountId
                    && original.Id == entry.ReversesEntryId)
                .DefaultIfEmpty()
            where account.Id == tenantAccountId
                && entry.EffectiveOn <= balance.BusinessDate
                && (!account.RentTrackingStartOn.HasValue
                    || entry.EffectiveOn >= account.RentTrackingStartOn.Value)
                && (!account.RentTrackingStartOn.HasValue
                    || entry.EntryType != TenantLedgerEntryType.Reversal
                    || reversedEntry == null
                    || reversedEntry.EffectiveOn >= account.RentTrackingStartOn.Value)
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
                && balance.OpenAmount > 0m
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
        from account in _db.TenantAccounts.AsNoTracking()
        join authorized in BuildAuthorizedTenantAccountSet(scope)
            on new { account.PortfolioId, TenantAccountId = account.Id, account.LeaseManagementId }
            equals new { authorized.PortfolioId, authorized.TenantAccountId, authorized.LeaseManagementId }
        select account;

    /// <summary>
    /// Materializes the tenant portal's effective account relationship once inside the SQL statement.
    /// The account page and all of its count/detail variants join this set directly, so the stacked
    /// effective-tenant-access view is not re-evaluated as a correlated EXISTS for every account row.
    /// </summary>
    private IQueryable<PortalTenantAccountAuthorizationRow> BuildAuthorizedTenantAccountSet(
        PortalTenantReadScope scope) =>
        _db.Database.SqlQuery<PortalTenantAccountAuthorizationRow>($"""
            WITH authorized_tenant_accounts AS MATERIALIZED (
                SELECT DISTINCT
                    access."PortfolioId" AS "PortfolioId",
                    access."TenantAccountId" AS "TenantAccountId",
                    access."LeaseManagementId" AS "LeaseManagementId"
                FROM "vw_effective_tenant_access" AS access
                WHERE access."PortfolioId" = {scope.PortfolioId}
                  AND access."UserId" = {scope.UserId}
                  AND access."AccessContextId" = {scope.AccessContextId}
                  AND access."AccessRevision" = {scope.AccessRevision}
                  AND access."TenantAccountId" IS NOT NULL
            )
            SELECT authorized_tenant_accounts."PortfolioId" AS "PortfolioId",
                   authorized_tenant_accounts."TenantAccountId" AS "TenantAccountId",
                   authorized_tenant_accounts."LeaseManagementId" AS "LeaseManagementId"
            FROM authorized_tenant_accounts
            """);

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

    private sealed class PortalTenantAccountAuthorizationRow
    {
        public int PortfolioId { get; set; }
        public int TenantAccountId { get; set; }
        public int LeaseManagementId { get; set; }
    }

    private sealed class PortalTenantAccountHistorySqlRow
    {
        public int TenantAccountId { get; set; }
        public int LeaseManagementId { get; set; }
        public string Currency { get; set; } = string.Empty;
        public DateOnly BusinessDate { get; set; }
        public string Period { get; set; } = string.Empty;
        public DateOnly? PeriodFrom { get; set; }
        public DateOnly PeriodTo { get; set; }
        public decimal CurrentDue { get; set; }
        public decimal BeginningBalance { get; set; }
        public decimal ClosingBalance { get; set; }
        public int TotalCount { get; set; }
        public string ItemsJson { get; set; } = "[]";
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

        var appointments = _db.Appointments
            .AsNoTracking()
            .Where(appointment => appointment.PortfolioId == portfolioId
                && appointment.TenantId == tenantId
                && appointment.ScheduledStart >= now
                && (appointment.Status == AppointmentStatus.Scheduled
                    || appointment.Status == AppointmentStatus.Confirmed)
                && effectiveRelationships.Any(access =>
                    appointment.LeaseManagementId == null
                    || access.LeaseManagementId == appointment.LeaseManagementId))
            .Select(appointment => new AppointmentResponse
            {
                Id = appointment.Id,
                PortfolioId = appointment.PortfolioId,
                PropertyId = appointment.PropertyId,
                UnitId = appointment.UnitId,
                LeaseManagementId = appointment.LeaseManagementId,
                RentalApplicationId = appointment.RentalApplicationId,
                TenantId = appointment.TenantId,
                WorkOrderId = appointment.WorkOrderId,
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

        return appointments
            .OrderBy(appointment => appointment.ScheduledStart)
            .ThenBy(appointment => appointment.Id)
            .Take(50);
    }

    public async Task<PortalTenantWorkOrderPageResponse> ListWorkOrdersPageAsync(
        PortalTenantReadScope scope,
        int tenantId,
        PortalTenantWorkOrderListQuery query,
        CancellationToken ct = default)
    {
        var rows = BuildWorkOrdersQuery(scope, tenantId, query);
        var totalCount = await rows.CountAsync(ct);
        var items = await BuildWorkOrderPageQuery(scope, tenantId, query).ToListAsync(ct);

        return new PortalTenantWorkOrderPageResponse
        {
            Items = items,
            TotalCount = totalCount,
            Skip = query.NormalizedSkip,
            Take = query.NormalizedTake,
        };
    }

    internal IQueryable<WorkOrderResponse> BuildWorkOrdersQuery(
        PortalTenantReadScope scope,
        int tenantId,
        PortalTenantWorkOrderListQuery query)
    {
        var effectiveRelationships = EffectiveTenantRelationshipQuery(scope, tenantId);
        var rows = _db.WorkOrders
            .AsNoTracking()
            .Where(w => w.PortfolioId == scope.PortfolioId
                && w.TenantId == tenantId
                && w.LeaseManagementId != null
                && effectiveRelationships.Any(access =>
                    access.LeaseManagementId == w.LeaseManagementId))
            .Select(w => new WorkOrderResponse
            {
                Id = w.Id,
                Title = w.Title,
                Description = w.Description,
                Category = w.Category,
                Priority = w.Priority,
                Status = w.Status,
                RequestedAt = w.RequestedAt,
                ScheduledFor = w.ScheduledFor,
                ScheduledWindowEnd = w.ScheduledWindowEnd,
                CompletedAt = w.CompletedAt,
                UpdatedAt = w.UpdatedAt,
                PropertyName = w.Property == null ? null : w.Property.Name,
                UnitNumber = w.Unit == null ? null : w.Unit.UnitNumber,
                TenantName = w.Tenant == null
                    ? null
                    : (w.Tenant.FirstName + " " + w.Tenant.LastName).Trim(),
                SubmittedByLabel = w.SubmittedByLabel ?? w.CreatedBy ?? "Resident",
            });

        if (query.Status is { } status)
        {
            rows = rows.Where(w => w.Status == status);
        }
        else if (query.OpenOnly == true)
        {
            rows = rows.Where(w => w.Status != WorkOrderStatus.Completed
                && w.Status != WorkOrderStatus.Cancelled
                && w.Status != WorkOrderStatus.Archived);
        }

        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var search = $"%{query.Search.Trim()}%";
            rows = rows.Where(w =>
                EF.Functions.ILike(w.Title, search)
                || EF.Functions.ILike(w.Description, search)
                || (w.PropertyName != null && EF.Functions.ILike(w.PropertyName, search))
                || (w.UnitNumber != null && EF.Functions.ILike(w.UnitNumber, search)));
        }

        var (fromUtc, toUtcExclusive) = ListDateRange.UtcDay(query.From, query.To);
        if (fromUtc is { } from)
        {
            rows = rows.Where(w => w.RequestedAt >= from);
        }
        if (toUtcExclusive is { } exclusiveEnd)
        {
            rows = rows.Where(w => w.RequestedAt < exclusiveEnd);
        }

        return rows;
    }

    internal IQueryable<WorkOrderResponse> BuildWorkOrderPageQuery(
        PortalTenantReadScope scope,
        int tenantId,
        PortalTenantWorkOrderListQuery query) =>
        ApplyWorkOrderSort(BuildWorkOrdersQuery(scope, tenantId, query), query)
            .Skip(query.NormalizedSkip)
            .Take(query.NormalizedTake);

    private static IOrderedQueryable<WorkOrderResponse> ApplyWorkOrderSort(
        IQueryable<WorkOrderResponse> rows,
        PortalTenantWorkOrderListQuery query) =>
        (query.SortField, query.SortDescending) switch
        {
            ("title", false) => rows.OrderBy(w => w.Title).ThenByDescending(w => w.Id),
            ("title", true) => rows.OrderByDescending(w => w.Title).ThenByDescending(w => w.Id),
            ("status", false) => rows.OrderBy(w => w.Status).ThenByDescending(w => w.Id),
            ("status", true) => rows.OrderByDescending(w => w.Status).ThenByDescending(w => w.Id),
            ("priority", false) => rows.OrderBy(w => w.Priority).ThenByDescending(w => w.Id),
            ("priority", true) => rows.OrderByDescending(w => w.Priority).ThenByDescending(w => w.Id),
            ("updatedat", false) => rows.OrderBy(w => w.UpdatedAt).ThenByDescending(w => w.Id),
            ("updatedat", true) => rows.OrderByDescending(w => w.UpdatedAt).ThenByDescending(w => w.Id),
            ("requestedat", false) => rows.OrderBy(w => w.RequestedAt).ThenBy(w => w.Id),
            _ => rows.OrderByDescending(w => w.RequestedAt).ThenByDescending(w => w.Id),
        };

    public async Task<WorkOrderDetailResponse?> GetWorkOrderDetailAsync(
        PortalTenantReadScope scope,
        int tenantId,
        int workOrderId,
        CancellationToken ct = default)
    {
        // Ownership is part of the lookup: a work order on another tenant's lease/unit simply isn't
        // found, so we never leak its existence or its timeline. Mirrors the lease-ledger restriction.
        var effectiveRelationships = EffectiveTenantRelationshipQuery(scope, tenantId);
        var workOrder = await _db.WorkOrders.AsNoTracking()
            .Where(w => w.Id == workOrderId
                && w.PortfolioId == scope.PortfolioId
                && w.TenantId == tenantId
                && w.LeaseManagementId != null
                && effectiveRelationships.Any(access =>
                    access.LeaseManagementId == w.LeaseManagementId))
            .Select(w => new WorkOrderDetailResponse
            {
                Id = w.Id,
                Title = w.Title,
                Description = w.Description,
                Category = w.Category,
                Priority = w.Priority,
                Status = w.Status,
                RequestedAt = w.RequestedAt,
                ScheduledFor = w.ScheduledFor,
                ScheduledWindowEnd = w.ScheduledWindowEnd,
                CompletedAt = w.CompletedAt,
                UpdatedAt = w.UpdatedAt,
                PropertyName = w.Property == null ? null : w.Property.Name,
                UnitNumber = w.Unit == null ? null : w.Unit.UnitNumber,
                TenantName = w.Tenant == null
                    ? null
                    : (w.Tenant.FirstName + " " + w.Tenant.LastName).Trim(),
                SubmittedByLabel = w.SubmittedByLabel ?? w.CreatedBy ?? "Resident",
                DetailRole = "tenant",
                RequesterPhone = w.RequesterPhone,
                RequesterEmail = w.RequesterEmail,
                ResidentMustBePresent = w.ResidentMustBePresent,
                PermissionToEnter = w.PermissionToEnter,
                EntryNotes = w.EntryNotes,
                PetWarnings = w.PetWarnings,
                AccessWarnings = w.AccessWarnings,
                Capabilities = WorkOrderDetailCapabilities.Tenant(w.Status),
            })
            .SingleOrDefaultAsync(ct);
        if (workOrder is null)
        {
            return null;
        }

        var recentActivity = _db.WorkOrderStatusEvents
            .AsNoTracking()
            .Where(e => e.WorkOrderId == workOrderId
                && e.PortfolioId == scope.PortfolioId
                && e.Visibility == "Public"
                && _db.WorkOrders.Any(candidate =>
                    candidate.Id == e.WorkOrderId
                    && candidate.PortfolioId == scope.PortfolioId
                    && candidate.TenantId == tenantId
                    && candidate.LeaseManagementId != null
                    && effectiveRelationships.Any(access =>
                        access.LeaseManagementId == candidate.LeaseManagementId)))
            .OrderByDescending(e => e.CreatedAtUtc)
            .ThenByDescending(e => e.Id)
            .Take(50);

        var activity = await recentActivity
            .OrderBy(e => e.CreatedAtUtc)
            .ThenBy(e => e.Id)
            .Select(e => new WorkOrderActivityResponse
            {
                Id = e.Id,
                Kind = e.Kind,
                FromStatus = e.FromStatus,
                ToStatus = e.ToStatus,
                Note = e.Note,
                ActorLabel = e.ChangedByLabel ?? "System",
                Visibility = e.Visibility,
                CreatedAtUtc = e.CreatedAtUtc,
            })
            .ToListAsync(ct);

        workOrder.ResidentNames = string.IsNullOrWhiteSpace(workOrder.TenantName)
            ? []
            : [workOrder.TenantName];
        workOrder.Timeline = activity.Select(item => new WorkOrderStatusEventResponse
        {
            Id = item.Id,
            Kind = item.Kind,
            Visibility = item.Visibility,
            FromStatus = item.FromStatus,
            ToStatus = item.ToStatus,
            Note = item.Note,
            ChangedByLabel = item.ActorLabel,
            CreatedAtUtc = item.CreatedAtUtc,
        }).ToList();
        workOrder.Activity = activity;
        return workOrder;
    }

    public async Task<WorkOrderMutationReceipt?> CommentTenantWorkOrderAsync(
        ActiveAccessContext access,
        int tenantId,
        int workOrderId,
        WorkOrderCommentRequest request,
        string idempotencyKey,
        CancellationToken ct = default)
    {
        var command = new AddTenantWorkOrderCommentCommand(
            access.PortfolioId, access.UserId, access.SessionId, access.AccessContextId,
            access.AccessRevision, workOrderId, request.Body, _timeProvider.UtcNow(), idempotencyKey);
        var outcome = await AtomicWorkOrderAsync(
            "portal.work-order.comment", idempotencyKey, command, ct);
        return Receipt(outcome);
    }

    public async Task<WorkOrderMutationReceipt?> UpdateTenantWorkOrderAsync(
        ActiveAccessContext access,
        int tenantId,
        int workOrderId,
        TenantWorkOrderUpdateRequest request,
        string idempotencyKey,
        CancellationToken ct = default)
    {
        var command = new UpdateTenantWorkOrderCommand(
            access.PortfolioId, access.UserId, access.SessionId, access.AccessContextId,
            access.AccessRevision, workOrderId, request.Title, request.Description,
            request.RequesterName, request.RequesterPhone, request.RequesterEmail,
            request.ResidentMustBePresent, request.CallBeforeEntry, request.CallIfNotHome,
            request.PermissionToEnter, request.EntryNotes, request.PetWarnings,
            request.AccessWarnings, _timeProvider.UtcNow(), idempotencyKey);
        var outcome = await AtomicWorkOrderAsync(
            "portal.work-order.update", idempotencyKey, command, ct);
        return Receipt(outcome);
    }

    public async Task<WorkOrderMutationReceipt?> CancelTenantWorkOrderAsync(
        ActiveAccessContext access,
        int tenantId,
        int workOrderId,
        TenantWorkOrderCancelRequest request,
        string idempotencyKey,
        CancellationToken ct = default)
    {
        var command = new CancelTenantWorkOrderCommand(
            access.PortfolioId, access.UserId, access.SessionId, access.AccessContextId,
            access.AccessRevision, workOrderId, request.Note, _timeProvider.UtcNow(), idempotencyKey);
        var outcome = await AtomicWorkOrderAsync(
            "portal.work-order.cancel", idempotencyKey, command, ct);
        return Receipt(outcome);
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
            request.Priority, _timeProvider.UtcNow(),
            request.ContactPhone, request.ContactEmail, request.ResidentMustBePresent,
            request.CallBeforeEntry, request.CallIfNotHome, request.PermissionToEnter,
            request.EntryNotes, request.PetWarnings, request.AccessWarnings,
            idempotencyKey);
        var digest = Convert.ToHexString(SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(idempotencyKey)));
        var outcome = await (_atomic ?? throw new InvalidOperationException(
                "Tenant work-order changes are not available right now."))
            .ExecuteAsync(new AtomicCommandIdentity("portal.work-order.create", digest),
                command, WorkOrderMutationCodec, ct);
        return outcome.Value.Outcome == OperationMutationOutcome.NotFound || outcome.Value.Snapshot is null
            ? null
            : ToWorkOrderResponse(outcome.Value.Snapshot);
    }

    private async Task<WorkOrderMutationResult> AtomicWorkOrderAsync<TCommand>(
        string operation,
        string idempotencyKey,
        TCommand command,
        CancellationToken ct)
        where TCommand : RentalCommand.Core.Atomic.IAtomicCommandData
    {
        var digest = Convert.ToHexString(SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(idempotencyKey)));
        var outcome = await (_atomic ?? throw new InvalidOperationException(
                "Tenant work-order changes are not available right now."))
            .ExecuteAsync(new AtomicCommandIdentity(operation, digest),
                command, WorkOrderMutationCodec, ct);
        return outcome.Value;
    }

    private static WorkOrderMutationReceipt? Receipt(WorkOrderMutationResult result) =>
        result.Outcome == OperationMutationOutcome.NotFound || result.Receipt is null
            ? null
            : ToWorkOrderReceipt(result.Receipt);

    private static WorkOrderResponse ToWorkOrderResponse(WorkOrderMutationSnapshot snapshot) => new()
    {
        Id = snapshot.Id,
        PortfolioId = snapshot.PortfolioId,
        PropertyId = snapshot.PropertyId,
        UnitId = snapshot.UnitId,
        TenantId = snapshot.TenantId,
        LeaseManagementId = snapshot.LeaseManagementId,
        VendorId = snapshot.VendorId,
        RecurringMaintenanceTaskId = snapshot.RecurringMaintenanceTaskId,
        Title = snapshot.Title,
        Description = snapshot.Description,
        TechnicianAccessInstructions = snapshot.TechnicianAccessInstructions,
        SubmittedByLabel = snapshot.SubmittedByLabel,
        RequesterName = snapshot.RequesterName,
        RequesterPhone = snapshot.RequesterPhone,
        RequesterEmail = snapshot.RequesterEmail,
        ResidentMustBePresent = snapshot.ResidentMustBePresent,
        CallBeforeEntry = snapshot.CallBeforeEntry,
        CallIfNotHome = snapshot.CallIfNotHome,
        PermissionToEnter = snapshot.PermissionToEnter,
        EntryNotes = snapshot.EntryNotes,
        PetWarnings = snapshot.PetWarnings,
        AccessWarnings = snapshot.AccessWarnings,
        Category = snapshot.Category,
        Priority = snapshot.Priority,
        Status = snapshot.Status,
        RequestedAt = snapshot.RequestedAt,
        ScheduledFor = snapshot.ScheduledFor,
        ScheduledWindowEnd = snapshot.ScheduledWindowEnd,
        CompletedAt = snapshot.CompletedAt,
        EstimatedCost = snapshot.EstimatedCost,
        ActualCost = snapshot.ActualCost,
        CreatedBy = snapshot.CreatedBy,
        UpdatedAt = snapshot.UpdatedAt,
        PropertyName = snapshot.PropertyName,
        UnitNumber = snapshot.UnitNumber,
        VendorName = snapshot.VendorName,
        TenantName = snapshot.TenantName,
    };

    private static WorkOrderMutationReceipt ToWorkOrderReceipt(
        WorkOrderMutationActivityReceipt receipt) => new()
    {
        EntityId = receipt.EntityId,
        Outcome = receipt.Outcome.ToString(),
        ActivityId = receipt.ActivityId,
        CommittedAtUtc = receipt.CommittedAtUtc,
    };

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

    private IQueryable<RentalCommand.Data.Authorization.EffectiveTenantAccessProjection> EffectiveTenantRelationshipQuery(
        PortalTenantReadScope scope,
        int tenantId) =>
        _db.EffectiveTenantAccess
            .AsNoTracking()
            .Where(access => access.PortfolioId == scope.PortfolioId
                && access.UserId == scope.UserId
                && access.AccessContextId == scope.AccessContextId
                && access.AccessRevision == scope.AccessRevision
                && access.TenantId == tenantId);
}
