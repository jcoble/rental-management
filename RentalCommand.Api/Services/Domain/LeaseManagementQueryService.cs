using Microsoft.EntityFrameworkCore;
using RentalCommand.Api.DTOs;
using RentalCommand.Core.Authorization;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Data;

namespace RentalCommand.Api.Services.Domain;

public sealed class LeaseManagementQueryService : ILeaseManagementQueryService
{
    private const int DefaultLedgerPageSize = 50;
    private const int MaxLedgerPageSize = 200;
    private readonly RentalCommandDbContext _db;
    private readonly TimeProvider _timeProvider;

    public LeaseManagementQueryService(RentalCommandDbContext db, TimeProvider timeProvider)
    {
        _db = db;
        _timeProvider = timeProvider;
    }

    public async Task<LeaseManagementListResponse> ListPageAsync(
        LeaseManagementReadContext access,
        LeaseManagementListQuery query,
        CancellationToken ct = default)
    {
        var rows = BuildSummaryQuery(access, query);
        var totalCount = await rows.CountAsync(ct);
        var items = await ApplySort(rows, query)
            .Skip(query.NormalizedSkip)
            .Take(query.NormalizedTake)
            .ToListAsync(ct);
        return new LeaseManagementListResponse
        {
            Items = items,
            TotalCount = totalCount,
            Skip = query.NormalizedSkip,
            Take = query.NormalizedTake,
        };
    }

    public async Task<LeaseManagementDetailResponse?> GetAsync(
        LeaseManagementReadContext access,
        int leaseManagementId,
        CancellationToken ct = default)
    {
        var header = await BuildDetailHeaderQuery(access, leaseManagementId).SingleOrDefaultAsync(ct);
        if (header is null)
        {
            return null;
        }

        var parties = await BuildPartyQuery(access, leaseManagementId)
            .OrderBy(party => party.EffectiveThrough != null)
            .ThenBy(party => party.Role)
            .ThenBy(party => party.TenantName)
            .ThenBy(party => party.LeaseManagementPartyId)
            .ToListAsync(ct);

        return new LeaseManagementDetailResponse
        {
            Summary = header,
            EndingDisposition = header.EndingDisposition,
            NoticeGivenAtUtc = header.NoticeGivenAtUtc,
            CancellationReasonCode = header.CancellationReasonCode,
            CancellationNote = header.CancellationNote,
            Parties = parties,
        };
    }

    public async Task<LeaseLedgerResponse?> GetLedgerAsync(
        LeaseManagementReadContext access,
        int leaseManagementId,
        int skip = 0,
        int? take = null,
        CancellationToken ct = default)
    {
        skip = Math.Max(0, skip);
        var pageSize = Math.Clamp(take ?? DefaultLedgerPageSize, 1, MaxLedgerPageSize);
        var header = await BuildCanonicalLedgerHeaderQuery(access, leaseManagementId)
            .SingleOrDefaultAsync(ct);
        if (header is null)
        {
            return null;
        }

        var entries = await BuildCanonicalLedgerEntriesQuery(access.PortfolioId, header.TenantAccountId)
            .OrderByDescending(entry => entry.EffectiveOn)
            .ThenByDescending(entry => entry.Id)
            .Skip(skip)
            .Take(pageSize)
            .ToListAsync(ct);
        var opening = await BuildOpeningEntryQuery(access.PortfolioId, header.TenantAccountId)
            .FirstOrDefaultAsync(ct);

        return new LeaseLedgerResponse
        {
            LeaseManagementId = header.LeaseManagementId,
            TenantAccountId = header.TenantAccountId,
            AccountNumber = header.AccountNumber,
            TenantName = string.IsNullOrWhiteSpace(header.TenantName) ? "Tenant" : header.TenantName,
            PropertyName = header.PropertyName,
            TotalCharged = header.TotalDebits,
            TotalPaid = header.TotalCredits,
            Balance = header.ReceivableBalance,
            PastDueCount = header.PastDueCount,
            Opening = opening is null ? null : ToLedgerResponse(opening, header, "Opening"),
            Entries = entries.Select(entry => ToLedgerResponse(entry, header)).ToList(),
            TotalCount = header.TotalEntryCount,
            Skip = skip,
            Take = pageSize,
        };
    }

    internal IQueryable<LeaseManagementSummaryResponse> BuildSummaryQuery(
        LeaseManagementReadContext access,
        LeaseManagementListQuery query)
    {
        var rows = BuildSummaryBaseQuery(access, CapabilityKeys.RentalsRead);
        if (query.PropertyId is int propertyId)
            rows = rows.Where(row => row.PropertyId == propertyId);
        if (query.UnitId is int unitId)
            rows = rows.Where(row => row.UnitId == unitId);
        if (query.TenantId is int tenantId)
            rows = rows.Where(row => _db.LeaseManagementParties.Any(party =>
                party.PortfolioId == access.PortfolioId
                && party.LeaseManagementId == row.LeaseManagementId
                && party.TenantId == tenantId));
        if (!string.IsNullOrWhiteSpace(query.Lifecycle))
        {
            var lifecycle = query.Lifecycle.Trim();
            rows = rows.Where(row => row.Lifecycle == lifecycle);
        }
        if (query.HasReconciliationException is bool hasException)
            rows = rows.Where(row => row.HasReconciliationException == hasException);
        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var term = query.Search.Trim();
            rows = rows.Where(row =>
                EF.Functions.ILike(row.RelationshipNumber, $"%{term}%")
                || EF.Functions.ILike(row.PropertyName, $"%{term}%")
                || EF.Functions.ILike(row.UnitNumber, $"%{term}%")
                || (row.PrimaryTenantName != null && EF.Functions.ILike(row.PrimaryTenantName, $"%{term}%"))
                || (row.AgreementNumber != null && EF.Functions.ILike(row.AgreementNumber, $"%{term}%")));
        }
        return rows;
    }

    private IQueryable<LeaseManagementSummaryResponse> BuildSummaryBaseQuery(
        LeaseManagementReadContext access,
        string capabilityKey) =>
        from management in BuildAuthorizedManagementQuery(access, capabilityKey)
        join lifecycle in _db.LeaseManagementLifecycleProjections.AsNoTracking()
            on new { management.PortfolioId, LeaseManagementId = management.Id }
            equals new { lifecycle.PortfolioId, lifecycle.LeaseManagementId }
        join agreement in _db.LeaseAgreements.AsNoTracking()
            on new { management.PortfolioId, AgreementId = lifecycle.CurrentAgreementId }
            equals new { agreement.PortfolioId, AgreementId = (int?)agreement.Id } into agreements
        from agreement in agreements.DefaultIfEmpty()
        join agreementStatus in _db.LeaseAgreementStatusProjections.AsNoTracking()
            on new { management.PortfolioId, AgreementId = lifecycle.CurrentAgreementId }
            equals new { agreementStatus.PortfolioId, AgreementId = (int?)agreementStatus.AgreementId } into statuses
        from agreementStatus in statuses.DefaultIfEmpty()
        select new LeaseManagementSummaryResponse
        {
            LeaseManagementId = management.Id,
            LeaseManagementPublicId = management.PublicId,
            RelationshipNumber = management.RelationshipNumber,
            PropertyId = management.PropertyId,
            PropertyName = management.Property!.Name,
            UnitId = management.UnitId,
            UnitNumber = management.Unit!.UnitNumber,
            Lifecycle = lifecycle.Lifecycle,
            LeaseAgreementId = lifecycle.CurrentAgreementId,
            AgreementNumber = agreement == null ? null : agreement.AgreementNumber,
            AgreementStatus = agreementStatus == null ? null : agreementStatus.AgreementStatus,
            TermStartOn = agreement == null ? null : agreement.TermStartOn,
            TermEndOn = agreement == null ? null : agreement.TermEndOn,
            BaseRentAmount = agreement == null ? null : agreement.BaseRentAmount,
            UpcomingLeaseAgreementId = lifecycle.UpcomingAgreementId,
            TenantAccountId = lifecycle.TenantAccountId,
            PrimaryTenantId = lifecycle.CurrentPrimaryTenantId,
            PrimaryTenantName = lifecycle.CurrentPrimaryTenantName,
            CurrentPartyCount = lifecycle.CurrentPartyCount,
            CurrentResidentCount = lifecycle.CurrentResidentCount,
            CurrentFinanciallyResponsiblePartyCount = lifecycle.CurrentFinanciallyResponsiblePartyCount,
            HasReconciliationException = lifecycle.HasReconciliationException,
            PlannedPossessionAtUtc = management.PlannedPossessionAtUtc,
            PossessionGivenAtUtc = management.PossessionGivenAtUtc,
            PlannedMoveOutAtUtc = management.PlannedMoveOutAtUtc,
            PossessionReturnedAtUtc = management.PossessionReturnedAtUtc,
            AccountClosedAtUtc = management.AccountClosedAtUtc,
            CanceledAtUtc = management.CanceledAtUtc,
            EndingDisposition = management.EndingDisposition,
            NoticeGivenAtUtc = management.NoticeGivenAtUtc,
            CancellationReasonCode = management.CancellationReasonCode,
            CancellationNote = management.CancellationNote,
            UpdatedAtUtc = management.UpdatedAtUtc,
        };

    internal IQueryable<LeaseManagementSummaryResponse> BuildDetailHeaderQuery(
        LeaseManagementReadContext access,
        int leaseManagementId) =>
        BuildSummaryBaseQuery(access, CapabilityKeys.RentalsRead)
            .Where(summary => summary.LeaseManagementId == leaseManagementId);

    internal IQueryable<LeaseManagementPartyResponse> BuildPartyQuery(
        LeaseManagementReadContext access,
        int leaseManagementId) =>
        from management in BuildAuthorizedManagementQuery(access, CapabilityKeys.RentalsRead)
        join party in _db.LeaseManagementParties.AsNoTracking()
            on new { management.PortfolioId, LeaseManagementId = management.Id }
            equals new { party.PortfolioId, party.LeaseManagementId }
        where management.Id == leaseManagementId
        select new LeaseManagementPartyResponse
        {
            LeaseManagementPartyId = party.Id,
            LeaseManagementId = party.LeaseManagementId,
            TenantId = party.TenantId,
            TenantName = (party.Tenant!.FirstName + " " + party.Tenant.LastName).Trim(),
            Email = party.Tenant.Email,
            Phone = party.Tenant.Phone,
            Role = party.Role,
            EffectiveFrom = party.EffectiveFrom,
            EffectiveThrough = party.EffectiveThrough,
            GuarantorLegalNoticeEligible = party.GuarantorLegalNoticeEligible,
        };

    internal IQueryable<CanonicalLedgerHeaderReadRow> BuildCanonicalLedgerHeaderQuery(
        LeaseManagementReadContext access,
        int leaseManagementId) =>
        from management in BuildAuthorizedManagementQuery(access, CapabilityKeys.MoneyBalancesRead)
        where management.Id == leaseManagementId
        join account in _db.TenantAccounts.AsNoTracking()
            on new { management.PortfolioId, LeaseManagementId = management.Id }
            equals new { account.PortfolioId, account.LeaseManagementId }
        join lifecycle in _db.LeaseManagementLifecycleProjections.AsNoTracking()
            on new { management.PortfolioId, LeaseManagementId = management.Id }
            equals new { lifecycle.PortfolioId, lifecycle.LeaseManagementId }
        join balance in _db.TenantAccountBalanceProjections.AsNoTracking()
            on new { account.PortfolioId, TenantAccountId = account.Id }
            equals new { balance.PortfolioId, balance.TenantAccountId }
        select new CanonicalLedgerHeaderReadRow
        {
            LeaseManagementId = management.Id,
            TenantAccountId = account.Id,
            AccountNumber = account.AccountNumber,
            TenantName = lifecycle.CurrentPrimaryTenantName,
            PropertyId = management.PropertyId,
            PropertyName = management.Property!.Name,
            TotalDebits = balance.TotalDebits,
            TotalCredits = balance.TotalCredits,
            ReceivableBalance = balance.ReceivableBalance,
            PastDueCount = balance.PastDueCount,
            TotalEntryCount = _db.TenantLedgerEntries.Count(entry =>
                entry.PortfolioId == access.PortfolioId
                && entry.TenantAccountId == account.Id
                && entry.EntryType != TenantLedgerEntryType.OpeningBalance),
        };

    internal IQueryable<CanonicalLedgerEntryReadRow> BuildCanonicalLedgerEntriesQuery(
        int portfolioId,
        int tenantAccountId) =>
        _db.TenantLedgerEntries.AsNoTracking()
            .Where(entry => entry.PortfolioId == portfolioId
                && entry.TenantAccountId == tenantAccountId
                && entry.EntryType != TenantLedgerEntryType.OpeningBalance)
            .Select(ToLedgerReadRowExpression());

    private IQueryable<CanonicalLedgerEntryReadRow> BuildOpeningEntryQuery(
        int portfolioId,
        int tenantAccountId) =>
        _db.TenantLedgerEntries.AsNoTracking()
            .Where(entry => entry.PortfolioId == portfolioId
                && entry.TenantAccountId == tenantAccountId
                && entry.EntryType == TenantLedgerEntryType.OpeningBalance)
            .OrderBy(entry => entry.EffectiveOn)
            .ThenBy(entry => entry.Id)
            .Select(ToLedgerReadRowExpression());

    private static System.Linq.Expressions.Expression<Func<TenantLedgerEntry, CanonicalLedgerEntryReadRow>>
        ToLedgerReadRowExpression() => entry => new CanonicalLedgerEntryReadRow
        {
            Id = entry.Id,
            TenantAccountId = entry.TenantAccountId,
            EntryType = entry.EntryType,
            Direction = entry.Direction,
            Amount = entry.Amount,
            EffectiveOn = entry.EffectiveOn,
            DueOn = entry.DueOn,
            Description = entry.Description,
            PaymentMethodSummary = entry.ProviderPaymentAttempt == null
                ? null : entry.ProviderPaymentAttempt.PaymentMethodSummary,
            LeaseAgreementBaseRent = entry.LeaseAgreement == null
                ? null : entry.LeaseAgreement.BaseRentAmount,
        };

    internal IQueryable<LeaseManagement> BuildAuthorizedManagementQuery(
        LeaseManagementReadContext access,
        string capabilityKey)
    {
        var utcNow = _timeProvider.GetUtcNow().UtcDateTime;
        return _db.LeaseManagements.AsNoTracking().Where(management =>
            management.PortfolioId == access.PortfolioId
            && _db.AuthSessions.AsNoTracking().Any(session =>
                session.Id == access.SessionId
                && session.UserId == access.UserId
                && session.ActiveAccessContextId == access.AccessContextId
                && session.Status == AuthSessionStatus.Active
                && session.RevokedAtUtc == null
                && session.ExpiresAtUtc > utcNow
                && session.ActiveAccessContext != null
                && session.ActiveAccessContext.UserId == access.UserId
                && session.ActiveAccessContext.PortfolioId == access.PortfolioId
                && session.ActiveAccessContext.AccessRevision == access.AccessRevision
                && session.ActiveAccessContext.Status == WorkspaceAccessContextStatus.Active
                && session.ActiveAccessContext.SuspendedAtUtc == null
                && session.ActiveAccessContext.RevokedAtUtc == null
                && session.ActiveAccessContext.Membership != null
                && session.ActiveAccessContext.Membership.PortfolioId == access.PortfolioId
                && session.ActiveAccessContext.Membership.Status == WorkspaceMembershipStatus.Active
                && session.ActiveAccessContext.Membership.SuspendedAtUtc == null
                && session.ActiveAccessContext.Membership.RevokedAtUtc == null
                && session.ActiveAccessContext.Membership.EffectiveFromUtc <= utcNow
                && (session.ActiveAccessContext.Membership.EffectiveToUtc == null
                    || session.ActiveAccessContext.Membership.EffectiveToUtc > utcNow)
                && session.ActiveAccessContext.Membership.RoleAssignments.Any(assignment =>
                    assignment.PortfolioId == access.PortfolioId
                    && assignment.Status == MembershipRoleAssignmentStatus.Active
                    && assignment.SuspendedAtUtc == null
                    && assignment.RevokedAtUtc == null
                    && assignment.EffectiveFromUtc <= utcNow
                    && (assignment.EffectiveToUtc == null || assignment.EffectiveToUtc > utcNow)
                    && assignment.RoleProfile!.Capabilities.Any(profileCapability =>
                        profileCapability.CapabilityDefinition!.Key == capabilityKey
                        && profileCapability.CapabilityDefinition.AuthorizationTargetKind
                            == CapabilityAuthorizationTargetKind.Property)
                    && (assignment.ScopeKind == MembershipRoleAssignmentScopeKind.AllProperties
                        || (assignment.ScopeKind == MembershipRoleAssignmentScopeKind.SelectedProperties
                            && assignment.SelectedProperties.Any(scope =>
                                scope.PortfolioId == access.PortfolioId
                                && scope.PropertyId == management.PropertyId))))));
    }

    private static IQueryable<LeaseManagementSummaryResponse> ApplySort(
        IQueryable<LeaseManagementSummaryResponse> rows,
        LeaseManagementListQuery query) => query.SortField switch
        {
            "relationshipnumber" => query.SortDescending
                ? rows.OrderByDescending(row => row.RelationshipNumber).ThenByDescending(row => row.LeaseManagementId)
                : rows.OrderBy(row => row.RelationshipNumber).ThenBy(row => row.LeaseManagementId),
            "propertyname" => query.SortDescending
                ? rows.OrderByDescending(row => row.PropertyName).ThenByDescending(row => row.UnitNumber)
                : rows.OrderBy(row => row.PropertyName).ThenBy(row => row.UnitNumber),
            "unitnumber" => query.SortDescending
                ? rows.OrderByDescending(row => row.UnitNumber).ThenByDescending(row => row.LeaseManagementId)
                : rows.OrderBy(row => row.UnitNumber).ThenBy(row => row.LeaseManagementId),
            "tenantname" => query.SortDescending
                ? rows.OrderByDescending(row => row.PrimaryTenantName).ThenByDescending(row => row.LeaseManagementId)
                : rows.OrderBy(row => row.PrimaryTenantName).ThenBy(row => row.LeaseManagementId),
            "lifecycle" => query.SortDescending
                ? rows.OrderByDescending(row => row.Lifecycle).ThenByDescending(row => row.LeaseManagementId)
                : rows.OrderBy(row => row.Lifecycle).ThenBy(row => row.LeaseManagementId),
            "rent" => query.SortDescending
                ? rows.OrderByDescending(row => row.BaseRentAmount).ThenByDescending(row => row.LeaseManagementId)
                : rows.OrderBy(row => row.BaseRentAmount).ThenBy(row => row.LeaseManagementId),
            _ => query.SortDescending
                ? rows.OrderByDescending(row => row.UpdatedAtUtc).ThenByDescending(row => row.LeaseManagementId)
                : rows.OrderBy(row => row.UpdatedAtUtc).ThenBy(row => row.LeaseManagementId),
        };

    private static LedgerTransactionResponse ToLedgerResponse(
        CanonicalLedgerEntryReadRow entry,
        CanonicalLedgerHeaderReadRow header,
        string? type = null) => new()
    {
        Date = entry.EffectiveOn.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc),
        Type = type ?? (entry.EntryType == TenantLedgerEntryType.PaymentReceipt ? "Payment"
            : entry.Direction == TenantLedgerDirection.Debit ? "Charge" : "Credit"),
        Id = entry.Id,
        Description = entry.Description,
        Amount = entry.Direction == TenantLedgerDirection.Debit ? -entry.Amount : entry.Amount,
        PropertyId = header.PropertyId,
        PropertyName = header.PropertyName,
        Counterparty = string.IsNullOrWhiteSpace(header.TenantName) ? "Tenant" : header.TenantName,
        Category = entry.EntryType.ToString(),
        Status = "Posted",
        SourceHref = $"/tenant-accounts/{entry.TenantAccountId}/entries?entryId={entry.Id}",
        IsProrated = entry.EntryType == TenantLedgerEntryType.RentCharge
            && entry.LeaseAgreementBaseRent.HasValue
            && entry.Amount != entry.LeaseAgreementBaseRent.Value,
        Explanation = LedgerExplanation.ForTenantLedgerEntry(
            entry.EntryType, entry.Direction, entry.Amount, entry.EffectiveOn, entry.DueOn,
            entry.PaymentMethodSummary, entry.Description),
    };

    internal sealed class CanonicalLedgerHeaderReadRow
    {
        public int LeaseManagementId { get; init; }
        public int TenantAccountId { get; init; }
        public string AccountNumber { get; init; } = string.Empty;
        public string? TenantName { get; init; }
        public int PropertyId { get; init; }
        public string PropertyName { get; init; } = string.Empty;
        public decimal TotalDebits { get; init; }
        public decimal TotalCredits { get; init; }
        public decimal ReceivableBalance { get; init; }
        public int PastDueCount { get; init; }
        public int TotalEntryCount { get; init; }
    }

    internal sealed class CanonicalLedgerEntryReadRow
    {
        public long Id { get; init; }
        public int TenantAccountId { get; init; }
        public TenantLedgerEntryType EntryType { get; init; }
        public TenantLedgerDirection Direction { get; init; }
        public decimal Amount { get; init; }
        public DateOnly EffectiveOn { get; init; }
        public DateOnly? DueOn { get; init; }
        public string Description { get; init; } = string.Empty;
        public string? PaymentMethodSummary { get; init; }
        public decimal? LeaseAgreementBaseRent { get; init; }
    }
}
