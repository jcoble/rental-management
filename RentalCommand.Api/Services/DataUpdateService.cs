using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using RentalCommand.Api.DTOs;
using RentalCommand.Api.Hubs;
using RentalCommand.Core.Authorization;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Interfaces;
using RentalCommand.Data;
using RentalCommand.Data.Authorization;

namespace RentalCommand.Api.Services;

/// <summary>
/// Emits cache invalidations only to currently authorized session/revision groups. The recipient
/// query joins the entity's Property, the effective Team assignment, capability, selected-property
/// scope, active access context, and active session in one translated SQL statement. Full entity
/// DTOs are never put on the SignalR wire; clients refetch through their authorized REST query.
/// </summary>
public sealed class DataUpdateService : IDataUpdateService
{
    private static readonly TimeSpan DefaultHubSendTimeout = TimeSpan.FromSeconds(2);
    private static readonly string[] RentalReadCapabilities =
        [CapabilityKeys.RentalsRead, CapabilityKeys.LeasingTermsRead, CapabilityKeys.LeasingOnboardingManage];
    private static readonly string[] WorkReadCapabilities = [CapabilityKeys.WorkRead];
    private static readonly string[] MoneyReadCapabilities = [CapabilityKeys.MoneyBalancesRead];
    private static readonly string[] ApplicationReadCapabilities = [CapabilityKeys.LeasingApplicationsManage];
    private static readonly string[] ListingReadCapabilities = [CapabilityKeys.LeasingListingsManage];
    private static readonly string[] ShowingReadCapabilities = [CapabilityKeys.LeasingShowingsManage];

    private readonly RentalCommandDbContext _db;
    private readonly IHubContext<DataUpdateHub> _hubContext;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<DataUpdateService> _logger;
    private readonly TimeSpan _hubSendTimeout;

    public DataUpdateService(
        RentalCommandDbContext db,
        IHubContext<DataUpdateHub> hubContext,
        TimeProvider timeProvider,
        ILogger<DataUpdateService> logger)
        : this(db, hubContext, timeProvider, logger, DefaultHubSendTimeout)
    {
    }

    internal DataUpdateService(
        RentalCommandDbContext db,
        IHubContext<DataUpdateHub> hubContext,
        TimeProvider timeProvider,
        ILogger<DataUpdateService> logger,
        TimeSpan hubSendTimeout)
    {
        if (hubSendTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(hubSendTimeout), "Realtime send timeout must be positive.");
        }

        _db = db;
        _hubContext = hubContext;
        _timeProvider = timeProvider;
        _logger = logger;
        _hubSendTimeout = hubSendTimeout;
    }

    public Task BroadcastEntityUpdateAsync(
        int portfolioId, string entityType, int entityId, object data, CancellationToken ct = default) =>
        BroadcastAsync(portfolioId, entityType, entityId, data, "EntityUpdated", ct);

    public Task BroadcastEntityDeleteAsync(
        int portfolioId, string entityType, int entityId, CancellationToken ct = default) =>
        BroadcastAsync(portfolioId, entityType, entityId, null, "EntityDeleted", ct);

    private async Task BroadcastAsync(
        int portfolioId,
        string entityType,
        int entityId,
        object? data,
        string eventName,
        CancellationToken ct)
    {
        try
        {
            var recipients = entityType switch
            {
                "Notification" => BuildNotificationRecipientQuery(portfolioId, entityId, data),
                "Conversation" => BuildConversationRecipientQuery(portfolioId, entityId),
                _ => BuildPropertyRecipientQuery(portfolioId, entityType, entityId),
            };

            if (recipients is null)
            {
                _logger.LogWarning(
                    "Suppressed unscoped realtime invalidation {EntityType} {EntityId} for portfolio {PortfolioId}",
                    entityType,
                    entityId,
                    portfolioId);
                return;
            }

            // Authorization, resource mapping, capability matching, session validity, and revision
            // selection all execute in this one SQL statement. Formatting already-authorized group
            // coordinates after materialization does not perform an authorization filter.
            var recipientRows = await recipients
                .Distinct()
                .ToListAsync(ct);
            if (recipientRows.Count == 0)
            {
                return;
            }

            var groupNames = recipientRows
                .Select(row => DataUpdateHub.SessionRevisionGroup(row.SessionId, row.AccessRevision))
                .ToArray();
            object payload = eventName == "EntityDeleted"
                ? new EntityDeletePayload
                {
                    EntityType = entityType,
                    EntityId = entityId,
                    Timestamp = _timeProvider.GetUtcNow().UtcDateTime,
                }
                : new EntityUpdatePayload
                {
                    EntityType = entityType,
                    EntityId = entityId,
                    Timestamp = _timeProvider.GetUtcNow().UtcDateTime,
                };

            if (!await TrySendHubAsync(groupNames, eventName, payload, ct))
            {
                return;
            }

            _logger.LogDebug(
                "Sent scoped {EventName} invalidation for {EntityType} {EntityId} to {RecipientCount} current session revisions",
                eventName,
                entityType,
                entityId,
                groupNames.Length);
        }
        catch (Exception ex)
        {
            // Realtime is a post-commit hint. A delivery failure must never fail the durable write.
            _logger.LogError(
                ex,
                "Failed scoped realtime invalidation {EventName} {EntityType} {EntityId} for portfolio {PortfolioId}",
                eventName,
                entityType,
                entityId,
                portfolioId);
        }
    }

    private async Task<bool> TrySendHubAsync(
        IReadOnlyList<string> groupNames,
        string eventName,
        object payload,
        CancellationToken ct)
    {
        try
        {
            await _hubContext.Clients.Groups(groupNames)
                .SendAsync(eventName, payload, ct)
                .WaitAsync(_hubSendTimeout, ct);
            return true;
        }
        catch (TimeoutException ex)
        {
            _logger.LogWarning(
                ex,
                "Timed out after {TimeoutMilliseconds} ms sending scoped {EventName} invalidation to {RecipientCount} current session revisions",
                _hubSendTimeout.TotalMilliseconds,
                eventName,
                groupNames.Count);
            return false;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            _logger.LogDebug(
                "Cancelled scoped {EventName} invalidation to {RecipientCount} current session revisions",
                eventName,
                groupNames.Count);
            return false;
        }
    }

    private IQueryable<RealtimeSessionRecipient>? BuildPropertyRecipientQuery(
        int portfolioId,
        string entityType,
        int entityId)
    {
        var audience = BuildPropertyAudience(portfolioId, entityType, entityId);
        if (audience is null)
        {
            return null;
        }

        var utcNow = _timeProvider.GetUtcNow().UtcDateTime;
        var propertyIds = audience.PropertyIds;
        var capabilityKeys = audience.CapabilityKeys;
        var effectiveContexts = _db.WorkspaceAccessContexts.AsNoTracking().WhereEffective();
        var effectiveMemberships = _db.WorkspaceMemberships.AsNoTracking().WhereEffective(utcNow);
        var effectiveAssignments = _db.MembershipRoleAssignments.AsNoTracking().WhereEffective(utcNow);

        return (
            from session in _db.AuthSessions.AsNoTracking()
            join context in effectiveContexts
                on new { Id = session.ActiveAccessContextId, session.UserId }
                equals new { context.Id, context.UserId }
            join membership in effectiveMemberships
                on new { AccessContextId = context.Id, context.PortfolioId }
                equals new { membership.AccessContextId, membership.PortfolioId }
            join assignment in effectiveAssignments
                on new { WorkspaceMembershipId = membership.Id, membership.PortfolioId }
                equals new { assignment.WorkspaceMembershipId, assignment.PortfolioId }
            where context.PortfolioId == portfolioId
                && session.Status == AuthSessionStatus.Active
                && session.RevokedAtUtc == null
                && session.ExpiresAtUtc > utcNow
                && assignment.RoleProfile!.Capabilities.Any(profileCapability =>
                    capabilityKeys.Contains(profileCapability.CapabilityDefinition!.Key)
                    && profileCapability.CapabilityDefinition.AuthorizationTargetKind ==
                        CapabilityAuthorizationTargetKind.Property)
                && ((assignment.ScopeKind == MembershipRoleAssignmentScopeKind.AllProperties
                        && propertyIds.Any())
                    || (assignment.ScopeKind == MembershipRoleAssignmentScopeKind.SelectedProperties
                        && assignment.SelectedProperties.Any(selected =>
                            selected.PortfolioId == portfolioId
                            && propertyIds.Contains(selected.PropertyId))))
            select new RealtimeSessionRecipient
            {
                SessionId = session.Id,
                AccessRevision = context.AccessRevision,
            })
            .TagWith("Realtime recipients: current session, capability, and entity property scope");
    }

    private IQueryable<RealtimeSessionRecipient> BuildNotificationRecipientQuery(
        int portfolioId,
        int notificationId,
        object? data)
    {
        var utcNow = _timeProvider.GetUtcNow().UtcDateTime;
        var effectiveContexts = _db.WorkspaceAccessContexts.AsNoTracking().WhereEffective();
        var effectiveMemberships = _db.WorkspaceMemberships.AsNoTracking().WhereEffective(utcNow);
        var effectiveAssignments = _db.MembershipRoleAssignments.AsNoTracking().WhereEffective(utcNow);
        var effectiveOwnerAccess = _db.OwnerUserAccesses.AsNoTracking().Where(access =>
            access.RevokedAtUtc == null
            && access.EffectiveFromUtc <= utcNow
            && (access.EffectiveToUtc == null || access.EffectiveToUtc > utcNow));
        var effectiveTenantAccess = _db.EffectiveTenantAccess.AsNoTracking();

        // Mirror NotificationService.AuthorizedNotifications: a portfolio-wide notification is
        // readable in every currently-authorized Team/owner/tenant context, while a user-addressed
        // row reaches only that user. Personal in-app preferences and the staff-only TenantMessage
        // exception stay in the same translated recipient query. The wire still carries only an
        // invalidation hint; each client refetches through the authorized REST read.
        var navigationHint = (data as NotificationResponse)?.NavigationIntent;
        var contextTargeted =
            from notification in _db.Notifications.AsNoTracking()
            join context in effectiveContexts
                on new
                {
                    AccessContextId = notification.NavigationAccessContextId,
                    UserId = notification.UserId,
                    AccessRevision = notification.NavigationAccessRevision,
                }
                equals new
                {
                    AccessContextId = (int?)context.Id,
                    UserId = (int?)context.UserId,
                    AccessRevision = (long?)context.AccessRevision,
                }
            join session in _db.AuthSessions.AsNoTracking()
                on new { ActiveAccessContextId = context.Id, context.UserId }
                equals new { session.ActiveAccessContextId, session.UserId }
            where notification.Id == notificationId
                && notification.PortfolioId == portfolioId
                && notification.NavigationAccessContextId != null
                && notification.NavigationAccessRevision != null
                && (navigationHint == null
                    || (notification.NavigationAccessContextId == navigationHint.AccessContextId
                        && notification.NavigationAccessRevision == navigationHint.AccessRevision))
                && context.PortfolioId == portfolioId
                && session.Status == AuthSessionStatus.Active
                && session.RevokedAtUtc == null
                && session.ExpiresAtUtc > utcNow
                && (effectiveMemberships.Any(membership =>
                        membership.AccessContextId == context.Id
                        && membership.PortfolioId == context.PortfolioId
                        && effectiveAssignments.Any(assignment =>
                            assignment.WorkspaceMembershipId == membership.Id
                            && assignment.PortfolioId == membership.PortfolioId))
                    || effectiveOwnerAccess.Any(access =>
                        access.AccessContextId == context.Id
                        && access.ApplicationUserId == context.UserId
                        && access.PortfolioId == context.PortfolioId)
                    || effectiveTenantAccess.Any(access =>
                        access.AccessContextId == context.Id
                        && access.UserId == context.UserId
                        && access.PortfolioId == context.PortfolioId))
                && !_db.UserAlertPreferences.AsNoTracking().Any(preference =>
                    preference.PortfolioId == portfolioId
                    && preference.UserId == context.UserId
                    && !preference.EnableInApp)
                && (notification.Type != "TenantMessage"
                    || effectiveMemberships.Any(membership =>
                        membership.AccessContextId == context.Id
                        && membership.PortfolioId == context.PortfolioId))
            select new RealtimeSessionRecipient
            {
                SessionId = session.Id,
                AccessRevision = context.AccessRevision,
            };

        if (navigationHint is not null)
        {
            return contextTargeted
                .TagWith("Realtime notification recipients: saved navigation access context");
        }

        var portfolioScoped =
            from session in _db.AuthSessions.AsNoTracking()
            join context in effectiveContexts
                on new { Id = session.ActiveAccessContextId, session.UserId }
                equals new { context.Id, context.UserId }
            where context.PortfolioId == portfolioId
                && session.Status == AuthSessionStatus.Active
                && session.RevokedAtUtc == null
                && session.ExpiresAtUtc > utcNow
                && (effectiveMemberships.Any(membership =>
                        membership.AccessContextId == context.Id
                        && membership.PortfolioId == context.PortfolioId
                        && effectiveAssignments.Any(assignment =>
                            assignment.WorkspaceMembershipId == membership.Id
                            && assignment.PortfolioId == membership.PortfolioId))
                    || effectiveOwnerAccess.Any(access =>
                        access.AccessContextId == context.Id
                        && access.ApplicationUserId == context.UserId
                        && access.PortfolioId == context.PortfolioId)
                    || effectiveTenantAccess.Any(access =>
                        access.AccessContextId == context.Id
                        && access.UserId == context.UserId
                        && access.PortfolioId == context.PortfolioId))
                && _db.Notifications.AsNoTracking().Any(notification =>
                    notification.Id == notificationId
                    && notification.PortfolioId == portfolioId
                    && notification.NavigationAccessContextId == null
                    && notification.NavigationAccessRevision == null
                    && (notification.UserId == null || notification.UserId == context.UserId)
                    && !_db.UserAlertPreferences.AsNoTracking().Any(preference =>
                        preference.PortfolioId == portfolioId
                        && preference.UserId == context.UserId
                        && !preference.EnableInApp)
                    && (notification.Type != "TenantMessage"
                        || effectiveMemberships.Any(membership =>
                            membership.AccessContextId == context.Id
                            && membership.PortfolioId == context.PortfolioId)))
            select new RealtimeSessionRecipient
            {
                SessionId = session.Id,
                AccessRevision = context.AccessRevision,
            };

        return contextTargeted
            .Union(portfolioScoped)
            .TagWith("Realtime notification recipients: current REST-readable notification audience");
    }

    private IQueryable<RealtimeSessionRecipient> BuildConversationRecipientQuery(
        int portfolioId,
        int conversationId)
    {
        var teamRecipients = BuildPropertyRecipientQuery(portfolioId, "Conversation", conversationId)
            ?? throw new InvalidOperationException("Conversation property audience is not configured.");
        var utcNow = _timeProvider.GetUtcNow().UtcDateTime;
        var tenantRecipients =
            from session in _db.AuthSessions.AsNoTracking()
            join access in _db.EffectiveTenantAccess.AsNoTracking()
                on new
                {
                    AccessContextId = session.ActiveAccessContextId,
                    session.UserId,
                }
                equals new { access.AccessContextId, access.UserId }
            where access.PortfolioId == portfolioId
                && session.Status == AuthSessionStatus.Active
                && session.RevokedAtUtc == null
                && session.ExpiresAtUtc > utcNow
                && _db.Conversations.IgnoreQueryFilters().AsNoTracking().Any(conversation =>
                    conversation.Id == conversationId
                    && conversation.PortfolioId == portfolioId
                    && conversation.TenantId == access.TenantId)
            select new RealtimeSessionRecipient
            {
                SessionId = session.Id,
                AccessRevision = access.AccessRevision,
            };

        return teamRecipients.Union(tenantRecipients)
            .TagWith("Realtime conversation recipients: authorized Team scope plus exact tenant relationship");
    }

    private PropertyAudience? BuildPropertyAudience(int portfolioId, string entityType, int entityId)
    {
        IQueryable<int>? propertyIds = entityType switch
        {
            "Property" => _db.Properties.IgnoreQueryFilters().AsNoTracking()
                .Where(row => row.PortfolioId == portfolioId && row.Id == entityId)
                .Select(row => row.Id),
            "Unit" => _db.Units.IgnoreQueryFilters().AsNoTracking()
                .Where(row => row.PortfolioId == portfolioId && row.Id == entityId)
                .Select(row => row.PropertyId),
            "WorkOrder" => _db.WorkOrders.IgnoreQueryFilters().AsNoTracking()
                .Where(row => row.PortfolioId == portfolioId && row.Id == entityId)
                .Select(row => row.PropertyId),
            "RecurringMaintenanceTask" => _db.RecurringMaintenanceTasks.IgnoreQueryFilters().AsNoTracking()
                .Where(row => row.PortfolioId == portfolioId && row.Id == entityId)
                .Select(row => row.PropertyId),
            "VendorDispatch" =>
                from dispatch in _db.VendorDispatches.IgnoreQueryFilters().AsNoTracking()
                join workOrder in _db.WorkOrders.IgnoreQueryFilters().AsNoTracking()
                    on new { dispatch.WorkOrderId, dispatch.PortfolioId }
                    equals new { WorkOrderId = workOrder.Id, workOrder.PortfolioId }
                where dispatch.PortfolioId == portfolioId && dispatch.Id == entityId
                select workOrder.PropertyId,
            "Inspection" => _db.Inspections.IgnoreQueryFilters().AsNoTracking()
                .Where(row => row.PortfolioId == portfolioId && row.Id == entityId)
                .Select(row => row.PropertyId),
            "Expense" =>
                from expense in _db.Expenses.IgnoreQueryFilters().AsNoTracking()
                from property in _db.Properties.IgnoreQueryFilters().AsNoTracking()
                where expense.PortfolioId == portfolioId && expense.Id == entityId
                    && property.PortfolioId == portfolioId
                    && (property.Id == expense.PropertyId
                        || expense.PropertyId == null && expense.UnitId != null
                            && property.Units.Any(unit => unit.Id == expense.UnitId)
                        || expense.PropertyId == null && expense.WorkOrderId != null
                            && property.WorkOrders.Any(workOrder => workOrder.Id == expense.WorkOrderId))
                select property.Id,
            "CapitalAsset" => _db.CapitalAssets.IgnoreQueryFilters().AsNoTracking()
                .Where(row => row.PortfolioId == portfolioId && row.Id == entityId)
                .Select(row => row.PropertyId),
            "Loan" => _db.Loans.IgnoreQueryFilters().AsNoTracking()
                .Where(row => row.PortfolioId == portfolioId && row.Id == entityId)
                .Select(row => row.PropertyId),
            "RecurringExpense" =>
                from expense in _db.RecurringExpenses.IgnoreQueryFilters().AsNoTracking()
                from property in _db.Properties.IgnoreQueryFilters().AsNoTracking()
                where expense.PortfolioId == portfolioId && expense.Id == entityId
                    && property.PortfolioId == portfolioId
                    && (property.Id == expense.PropertyId
                        || expense.PropertyId == null && expense.UnitId != null
                            && property.Units.Any(unit => unit.Id == expense.UnitId))
                select property.Id,
            "OwnerDistribution" => _db.OwnerDistributions.IgnoreQueryFilters().AsNoTracking()
                .Where(row => row.PortfolioId == portfolioId && row.Id == entityId && row.PropertyId != null)
                .Select(row => row.PropertyId!.Value),
            "PropertyDisposition" => _db.PropertyDispositions.IgnoreQueryFilters().AsNoTracking()
                .Where(row => row.PortfolioId == portfolioId && row.Id == entityId)
                .Select(row => row.PropertyId),
            "RentalApplication" or "Application" =>
                from application in _db.RentalApplications.IgnoreQueryFilters().AsNoTracking()
                from property in _db.Properties.IgnoreQueryFilters().AsNoTracking()
                where application.PortfolioId == portfolioId && application.Id == entityId
                    && property.PortfolioId == portfolioId
                    && (property.Id == application.PropertyId
                        || application.PropertyId == null && application.UnitId != null
                            && property.Units.Any(unit => unit.Id == application.UnitId))
                select property.Id,
            "RentalListing" => _db.RentalListings.IgnoreQueryFilters().AsNoTracking()
                .Where(row => row.PortfolioId == portfolioId && row.Id == entityId)
                .Select(row => row.PropertyId),
            "Appointment" =>
                from appointment in _db.Appointments.IgnoreQueryFilters().AsNoTracking()
                from property in _db.Properties.IgnoreQueryFilters().AsNoTracking()
                where appointment.PortfolioId == portfolioId && appointment.Id == entityId
                    && property.PortfolioId == portfolioId
                    && (property.Id == appointment.PropertyId
                        || appointment.PropertyId == null && appointment.UnitId != null
                            && property.Units.Any(unit => unit.Id == appointment.UnitId)
                        || appointment.PropertyId == null && appointment.LeaseManagementId != null
                            && property.LeaseManagements.Any(relationship => relationship.Id == appointment.LeaseManagementId)
                        || appointment.PropertyId == null && appointment.RentalApplicationId != null
                            && property.Id == appointment.RentalApplication!.PropertyId)
                select property.Id,
            "EvictionCase" => _db.EvictionCases.IgnoreQueryFilters().AsNoTracking()
                .Where(row => row.PortfolioId == portfolioId && row.Id == entityId)
                .Select(row => row.PropertyId),
            "EvictionCaseEvent" =>
                from caseEvent in _db.EvictionCaseEvents.IgnoreQueryFilters().AsNoTracking()
                join evictionCase in _db.EvictionCases.IgnoreQueryFilters().AsNoTracking()
                    on new { caseEvent.EvictionCaseId, caseEvent.PortfolioId }
                    equals new { EvictionCaseId = evictionCase.Id, evictionCase.PortfolioId }
                where caseEvent.PortfolioId == portfolioId && caseEvent.Id == entityId
                select evictionCase.PropertyId,
            "Conversation" => _db.Conversations.IgnoreQueryFilters().AsNoTracking()
                .Where(row => row.PortfolioId == portfolioId && row.Id == entityId && row.PropertyId != null)
                .Select(row => row.PropertyId!.Value),
            "Tenant" =>
                from party in _db.LeaseManagementParties.IgnoreQueryFilters().AsNoTracking()
                join relationship in _db.LeaseManagements.IgnoreQueryFilters().AsNoTracking()
                    on new { party.LeaseManagementId, party.PortfolioId }
                    equals new { LeaseManagementId = relationship.Id, relationship.PortfolioId }
                where party.PortfolioId == portfolioId && party.TenantId == entityId
                select relationship.PropertyId,
            "LeaseManagement" => _db.LeaseManagements.IgnoreQueryFilters().AsNoTracking()
                .Where(row => row.PortfolioId == portfolioId && row.Id == entityId)
                .Select(row => row.PropertyId),
            "LeaseAgreement" =>
                from agreement in _db.LeaseAgreements.IgnoreQueryFilters().AsNoTracking()
                join relationship in _db.LeaseManagements.IgnoreQueryFilters().AsNoTracking()
                    on new { agreement.LeaseManagementId, agreement.PortfolioId }
                    equals new { LeaseManagementId = relationship.Id, relationship.PortfolioId }
                where agreement.PortfolioId == portfolioId && agreement.Id == entityId
                select relationship.PropertyId,
            "TenantAccount" =>
                from account in _db.TenantAccounts.IgnoreQueryFilters().AsNoTracking()
                join relationship in _db.LeaseManagements.IgnoreQueryFilters().AsNoTracking()
                    on new { account.LeaseManagementId, account.PortfolioId }
                    equals new { LeaseManagementId = relationship.Id, relationship.PortfolioId }
                where account.PortfolioId == portfolioId && account.Id == entityId
                select relationship.PropertyId,
            "TenantLedgerEntry" =>
                from entry in _db.TenantLedgerEntries.IgnoreQueryFilters().AsNoTracking()
                join account in _db.TenantAccounts.IgnoreQueryFilters().AsNoTracking()
                    on new { entry.TenantAccountId, entry.PortfolioId }
                    equals new { TenantAccountId = account.Id, account.PortfolioId }
                join relationship in _db.LeaseManagements.IgnoreQueryFilters().AsNoTracking()
                    on new { account.LeaseManagementId, account.PortfolioId }
                    equals new { LeaseManagementId = relationship.Id, relationship.PortfolioId }
                where entry.PortfolioId == portfolioId && entry.Id == entityId
                select relationship.PropertyId,
            "SecurityDepositAccount" =>
                from deposit in _db.SecurityDepositAccounts.IgnoreQueryFilters().AsNoTracking()
                join account in _db.TenantAccounts.IgnoreQueryFilters().AsNoTracking()
                    on new { TenantAccountId = deposit.TenantAccountId, deposit.PortfolioId }
                    equals new { TenantAccountId = account.Id, account.PortfolioId }
                join relationship in _db.LeaseManagements.IgnoreQueryFilters().AsNoTracking()
                    on new { account.LeaseManagementId, account.PortfolioId }
                    equals new { LeaseManagementId = relationship.Id, relationship.PortfolioId }
                where deposit.PortfolioId == portfolioId && deposit.Id == entityId
                select relationship.PropertyId,
            "ScanDraft" =>
                from draft in _db.ScanDrafts.IgnoreQueryFilters().AsNoTracking()
                from property in _db.Properties.IgnoreQueryFilters().AsNoTracking()
                where draft.PortfolioId == portfolioId && draft.Id == entityId
                    && property.PortfolioId == portfolioId
                    && (property.Id == draft.CapturePropertyId
                        || draft.CapturePropertyId == null && draft.CaptureUnitId != null
                            && property.Units.Any(unit => unit.Id == draft.CaptureUnitId))
                select property.Id,
            _ => null,
        };

        if (propertyIds is null)
        {
            return null;
        }

        var capabilityKeys = entityType switch
        {
            "WorkOrder" or "RecurringMaintenanceTask" or "VendorDispatch" or "Inspection" => WorkReadCapabilities,
            "Expense" or "CapitalAsset" or "Loan" or "RecurringExpense" or "OwnerDistribution"
                or "PropertyDisposition" or "TenantAccount" or "TenantLedgerEntry"
                or "SecurityDepositAccount" => MoneyReadCapabilities,
            "RentalApplication" or "Application" => ApplicationReadCapabilities,
            "RentalListing" => ListingReadCapabilities,
            "Appointment" => ShowingReadCapabilities,
            _ => RentalReadCapabilities,
        };

        return new PropertyAudience(propertyIds.Distinct(), capabilityKeys);
    }

    private sealed record PropertyAudience(IQueryable<int> PropertyIds, string[] CapabilityKeys);

    private sealed class RealtimeSessionRecipient
    {
        public Guid SessionId { get; init; }
        public long AccessRevision { get; init; }
    }
}
