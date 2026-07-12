using Microsoft.EntityFrameworkCore;
using RentalCommand.Api.DTOs;
using RentalCommand.Core.Authorization;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Time;
using RentalCommand.Data;

namespace RentalCommand.Api.Services.Domain;

/// <inheritdoc cref="IPaymentReceiptQueryService"/>
public class PaymentReceiptQueryService : IPaymentReceiptQueryService
{
    private readonly RentalCommandDbContext _db;
    private readonly TimeProvider _timeProvider;

    public PaymentReceiptQueryService(
        RentalCommandDbContext db,
        TimeProvider timeProvider)
    {
        _db = db;
        _timeProvider = timeProvider;
    }

    public async Task<TenantAccountOptionListResponse> ListAccountOptionsAsync(
        PaymentReceiptReadContext access,
        TenantAccountOptionQuery query,
        CancellationToken ct = default)
    {
        var rows = BuildAccountOptionQuery(access, query);
        var totalCount = await rows.CountAsync(ct);
        var items = await rows
            .OrderBy(row => row.PropertyName)
            .ThenBy(row => row.UnitNumber)
            .ThenBy(row => row.PrimaryTenantName)
            .ThenBy(row => row.TenantAccountId)
            .Skip(query.NormalizedSkip)
            .Take(query.NormalizedTake)
            .ToListAsync(ct);
        return new TenantAccountOptionListResponse
        {
            Items = items,
            TotalCount = totalCount,
            Skip = query.NormalizedSkip,
            Take = query.NormalizedTake,
        };
    }

    /// <summary>
    /// Builds the complete scan/import account picker query. Authorization, property scoping, search,
    /// and projection remain in the translated SQL before count, ordering, and paging are applied.
    /// </summary>
    internal IQueryable<TenantAccountOptionResponse> BuildAccountOptionQuery(
        PaymentReceiptReadContext access,
        TenantAccountOptionQuery query)
    {
        var utcNow = _timeProvider.GetUtcNow().UtcDateTime;
        var rows =
            from account in _db.TenantAccounts.AsNoTracking()
            join management in _db.LeaseManagements.AsNoTracking()
                on new { account.PortfolioId, Id = account.LeaseManagementId }
                equals new { management.PortfolioId, management.Id }
            join lifecycle in _db.LeaseManagementLifecycleProjections.AsNoTracking()
                on new { management.PortfolioId, LeaseManagementId = management.Id }
                equals new { lifecycle.PortfolioId, lifecycle.LeaseManagementId }
            where account.PortfolioId == access.PortfolioId
                && account.ClosedAtUtc == null
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
                            profileCapability.CapabilityDefinition!.Key == CapabilityKeys.MoneyPaymentsManage
                            && profileCapability.CapabilityDefinition.AuthorizationTargetKind
                                == CapabilityAuthorizationTargetKind.Property)
                        && (assignment.ScopeKind == MembershipRoleAssignmentScopeKind.AllProperties
                            || (assignment.ScopeKind == MembershipRoleAssignmentScopeKind.SelectedProperties
                                && assignment.SelectedProperties.Any(scope =>
                                    scope.PortfolioId == access.PortfolioId
                                    && scope.PropertyId == management.PropertyId)))))
            select new TenantAccountOptionResponse
            {
                TenantAccountId = account.Id,
                LeaseManagementId = management.Id,
                PropertyId = management.PropertyId,
                UnitId = management.UnitId,
                AccountNumber = account.AccountNumber,
                RelationshipNumber = management.RelationshipNumber,
                PropertyName = management.Property!.Name,
                UnitNumber = management.Unit!.UnitNumber,
                PrimaryTenantName = lifecycle.CurrentPrimaryTenantName,
            };

        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var term = query.Search.Trim();
            rows = rows.Where(row =>
                EF.Functions.ILike(row.AccountNumber, $"%{term}%")
                || EF.Functions.ILike(row.RelationshipNumber, $"%{term}%")
                || EF.Functions.ILike(row.PropertyName, $"%{term}%")
                || EF.Functions.ILike(row.UnitNumber, $"%{term}%")
                || (row.PrimaryTenantName != null
                    && EF.Functions.ILike(row.PrimaryTenantName, $"%{term}%")));
        }

        return rows;
    }

    public async Task<IReadOnlyList<PaymentReceiptResponse>> ListAsync(
        PaymentReceiptReadContext access,
        PaymentListQuery query,
        CancellationToken ct = default)
    {
        var page = await ListPageAsync(access, query, ct);
        return page.Items;
    }

    public async Task<PaymentListResponse> ListPageAsync(
        PaymentReceiptReadContext access,
        PaymentListQuery query,
        CancellationToken ct = default)
    {
        var filtered = BuildReceiptQuery(access, query);
        var totalCount = await filtered.CountAsync(ct);

        var items = await ApplyReceiptSort(filtered, query)
            .Skip(query.NormalizedSkip)
            .Take(query.NormalizedTake)
            .ToListAsync(ct);

        return new PaymentListResponse
        {
            Items = items,
            TotalCount = totalCount,
            Skip = query.NormalizedSkip,
            Take = query.NormalizedTake,
        };
    }

    /// <summary>
    /// Builds the complete receipt read as one composable database query. Authorization is a correlated
    /// EXISTS on the current session/context/membership/assignment and the receipt's property, so an
    /// assignment cannot lend its capability to another assignment's scope and no allowed-ID collection
    /// is materialized before filtering, count, sort, paging, or projection.
    /// </summary>
    internal IQueryable<PaymentReceiptResponse> BuildReceiptQuery(
        PaymentReceiptReadContext access,
        PaymentListQuery query)
    {
        var utcNow = _timeProvider.GetUtcNow().UtcDateTime;
        var authorizedRows =
            from entry in _db.TenantLedgerEntries.AsNoTracking()
            join account in _db.TenantAccounts.AsNoTracking()
                on new { entry.PortfolioId, Id = entry.TenantAccountId }
                equals new { account.PortfolioId, account.Id }
            join management in _db.LeaseManagements.AsNoTracking()
                on new { account.PortfolioId, Id = account.LeaseManagementId }
                equals new { management.PortfolioId, management.Id }
            join lifecycle in _db.LeaseManagementLifecycleProjections.AsNoTracking()
                on new { management.PortfolioId, LeaseManagementId = management.Id }
                equals new { lifecycle.PortfolioId, lifecycle.LeaseManagementId }
            where entry.PortfolioId == access.PortfolioId
                && entry.EntryType == TenantLedgerEntryType.PaymentReceipt
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
                            profileCapability.CapabilityDefinition!.Key == CapabilityKeys.MoneyBalancesRead
                            && profileCapability.CapabilityDefinition.AuthorizationTargetKind
                                == CapabilityAuthorizationTargetKind.Property)
                        && (assignment.ScopeKind == MembershipRoleAssignmentScopeKind.AllProperties
                            || (assignment.ScopeKind == MembershipRoleAssignmentScopeKind.SelectedProperties
                                && assignment.SelectedProperties.Any(scope =>
                                    scope.PortfolioId == access.PortfolioId
                                    && scope.PropertyId == management.PropertyId)))))
            select new { entry, account, management, lifecycle };

        if (query.TenantAccountId is int tenantAccountId)
            authorizedRows = authorizedRows.Where(row => row.account.Id == tenantAccountId);
        if (query.LeaseManagementId is int leaseManagementId)
            authorizedRows = authorizedRows.Where(row => row.management.Id == leaseManagementId);

        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var term = query.Search.Trim();
            authorizedRows = authorizedRows.Where(row =>
                EF.Functions.ILike(row.entry.Description, $"%{term}%") ||
                EF.Functions.ILike(row.account.AccountNumber, $"%{term}%") ||
                EF.Functions.ILike(row.management.RelationshipNumber, $"%{term}%") ||
                (row.entry.ProviderPaymentAttempt != null
                    && EF.Functions.ILike(row.entry.ProviderPaymentAttempt.ProviderObjectId, $"%{term}%")) ||
                (row.entry.ProviderPaymentAttempt != null
                    && row.entry.ProviderPaymentAttempt.PayerName != null
                    && EF.Functions.ILike(row.entry.ProviderPaymentAttempt.PayerName, $"%{term}%")) ||
                (row.entry.ProviderPaymentAttempt != null
                    && row.entry.ProviderPaymentAttempt.CheckNumber != null
                    && EF.Functions.ILike(row.entry.ProviderPaymentAttempt.CheckNumber, $"%{term}%")));
        }

        if (query.PaidFrom.HasValue)
        {
            var paidFrom = DateOnly.FromDateTime(query.PaidFrom.Value.ToUtc());
            authorizedRows = authorizedRows.Where(row => row.entry.EffectiveOn >= paidFrom);
        }
        if (query.PaidTo.HasValue)
        {
            var paidToExclusive = DateOnly.FromDateTime(ToExclusiveUpperBound(query.PaidTo.Value));
            authorizedRows = authorizedRows.Where(row => row.entry.EffectiveOn < paidToExclusive);
        }

        return authorizedRows.Select(row => new PaymentReceiptResponse
        {
            Id = row.entry.Id,
            PublicId = row.entry.PublicId,
            PortfolioId = row.entry.PortfolioId,
            TenantAccountId = row.account.Id,
            LeaseManagementId = row.management.Id,
            PropertyId = row.management.PropertyId,
            UnitId = row.management.UnitId,
            AccountNumber = row.account.AccountNumber,
            RelationshipNumber = row.management.RelationshipNumber,
            TenantName = row.lifecycle.CurrentPrimaryTenantName,
            PropertyName = row.management.Property!.Name,
            UnitNumber = row.management.Unit!.UnitNumber,
            Amount = row.entry.Amount,
            Currency = row.entry.Currency,
            ReceivedOn = row.entry.EffectiveOn,
            PostedAtUtc = row.entry.PostedAtUtc,
            Description = row.entry.Description,
            Provider = row.entry.ProviderPaymentAttempt == null
                ? null : row.entry.ProviderPaymentAttempt.Provider,
            ProviderReference = row.entry.ProviderPaymentAttempt == null
                ? null : row.entry.ProviderPaymentAttempt.ProviderObjectId,
            ProviderState = row.entry.ProviderPaymentAttempt == null
                ? null : row.entry.ProviderPaymentAttempt.State,
            PaymentMethodSummary = row.entry.ProviderPaymentAttempt == null
                ? null : row.entry.ProviderPaymentAttempt.PaymentMethodSummary,
            PayerName = row.entry.ProviderPaymentAttempt == null
                ? null : row.entry.ProviderPaymentAttempt.PayerName,
            CheckNumber = row.entry.ProviderPaymentAttempt == null
                ? null : row.entry.ProviderPaymentAttempt.CheckNumber,
            BankName = row.entry.ProviderPaymentAttempt == null
                ? null : row.entry.ProviderPaymentAttempt.BankName,
            SourceStoredFileId = row.entry.SourceStoredFileId,
        });
    }

    private static DateTime ToExclusiveUpperBound(DateTime value)
    {
        var utc = value.ToUtc();
        return value.TimeOfDay == TimeSpan.Zero ? utc.AddDays(1) : utc;
    }

    private static IQueryable<PaymentReceiptResponse> ApplyReceiptSort(
        IQueryable<PaymentReceiptResponse> q,
        ListQuery query) =>
        query.SortField switch
        {
            "amount" => query.SortDescending ? q.OrderByDescending(row => row.Amount) : q.OrderBy(row => row.Amount),
            "receivedon" or "paiddate" => query.SortDescending ? q.OrderByDescending(row => row.ReceivedOn) : q.OrderBy(row => row.ReceivedOn),
            "tenant" => query.SortDescending ? q.OrderByDescending(row => row.TenantName) : q.OrderBy(row => row.TenantName),
            _ => query.SortDescending
                ? q.OrderByDescending(row => row.PostedAtUtc).ThenByDescending(row => row.Id)
                : q.OrderBy(row => row.PostedAtUtc).ThenBy(row => row.Id),
        };

    public Task<PaymentReceiptResponse?> GetAsync(
        PaymentReceiptReadContext access,
        long id,
        CancellationToken ct = default)
    {
        return BuildReceiptQuery(access, new PaymentListQuery())
            .SingleOrDefaultAsync(row => row.Id == id, ct);
    }

}
