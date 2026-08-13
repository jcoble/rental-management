using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using RentalCommand.Api.Services.Domain;
using RentalCommand.Core.AiIntegrations;
using RentalCommand.Core.Authorization;
using RentalCommand.Core.Conversations;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Leasing;
using RentalCommand.Core.Listings;
using RentalCommand.Core.Notifications;
using RentalCommand.Core.Operations;
using RentalCommand.Core.Scanning;
using RentalCommand.Data.AiIntegrations;
using RentalCommand.Data.Authorization;
using RentalCommand.Data.Conversations;
using RentalCommand.Data.Leasing;
using RentalCommand.Data.Listings;
using RentalCommand.Data.Operations;
using RentalCommand.Data.Scanning;

namespace RentalCommand.Data.Tests;

public sealed class WorkspaceAuthorizationConsumerSqlTranslationTests
{
    private static readonly Guid SessionId = Guid.Parse("77777777-7777-7777-7777-777777777777");
    private static readonly DateTime SecurityNowUtc =
        new(2026, 8, 13, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Leasing_and_listing_authorization_consumers_translate_as_single_statements()
    {
        using var db = Context();

        var agreementSql = LeaseAgreementDraftCommandSupport.AuthorizedRelationships(
                new ReplaceIssuedAgreementWithDraftCommand(
                    17, 42, 73, null, "Correction", 5, SessionId, 12, 3, "agreement-replay"),
                db,
                SecurityNowUtc)
            .ToQueryString();
        var partySql = LeasePartyAccessCommandSupport.AuthorizedRelationships(
                new GrantTenantUserAccessCommand(
                    17, 42, 9, "Resident access", "https://example.test", 5,
                    SessionId, 12, 3, "party-replay"),
                db,
                SecurityNowUtc)
            .ToQueryString();
        var moveInSql = PrepareMoveInHandler.AuthorizedUnits(
                new PrepareMoveInCommand(
                    17, null, 42, 5, SessionId, 12, 3, null,
                    new DateOnly(2026, 9, 1), [], null, default,
                    new DateOnly(2026, 9, 1), null, 1500m, 1, 1500m, 0m, 0,
                    1, "{}", false, null, null, null, "move-in-replay"),
                db,
                SecurityNowUtc)
            .ToQueryString();
        var dispositionSql = CreatePropertyDispositionHandler.AuthorizedProperties(
                new CreatePropertyDispositionCommand(
                    17, 9, SecurityNowUtc, 250000m, 15000m, null, null, 5,
                    SessionId, 12, 3, "disposition-replay"),
                db,
                SecurityNowUtc)
            .ToQueryString();
        var listingSql = ListingWorkspaceCommandSupport.AuthorizedListings(
                new GenerateListingWorkspaceCommand(17, 42, 5, SessionId, 12, 3),
                db,
                SecurityNowUtc)
            .ToQueryString();

        foreach (var sql in new[] { agreementSql, partySql, moveInSql, dispositionSql, listingSql })
        {
            sql.Should().Contain("MembershipRoleAssignments");
            sql.Should().Contain("WorkspaceMemberships");
            sql.Should().Contain("WorkspaceAccessContexts");
            sql.Should().Contain("AuthSessions");
            sql.Should().Contain("RoleProfileCapabilities");
            sql.Should().Contain("MembershipRoleAssignmentProperties");
            sql.Should().Contain("AllProperties");
            sql.Should().Contain("SelectedProperties");
            sql.Should().NotContain("ClientEvaluation");
            sql.TrimEnd().Should().NotEndWith(";",
                "the complete authorization consumer must render as one SQL statement");
        }

        agreementSql.Should().Contain("LeaseManagements");
        agreementSql.Should().Contain("rentals.manage");
        agreementSql.Should().Contain("leasing.agreements.prepare");
        partySql.Should().Contain("LeaseManagements");
        partySql.Should().Contain("rentals.manage");
        partySql.Should().Contain("leasing.onboarding.manage");
        moveInSql.Should().Contain("Units");
        moveInSql.Should().Contain("rentals.manage");
        moveInSql.Should().Contain("leasing.agreements.prepare");
        dispositionSql.Should().Contain("Properties");
        dispositionSql.Should().Contain("rentals.manage");
        listingSql.Should().Contain("Units");
        listingSql.Should().Contain("RentalListings");
        listingSql.Should().Contain("leasing.listings.manage");
    }

    [Fact]
    public void Remaining_authorization_consumers_translate_as_single_statements()
    {
        using var db = Context();

        var ownerSql = QueueOwnerStatementEmailHandler.AuthorizedOwners(
                new QueueOwnerStatementEmailCommand(
                    17, 5, SessionId, 12, 3, 9, 2026, "owner@example.test", "Statement", "Body", "owner-replay"),
                db,
                SecurityNowUtc)
            .ToQueryString();
        var scanSql = ProductionScanConfirmationTargetWriter.AuthorizedProperties(
                new ConfirmScanDraftCommand(
                    17, 44, 5, SecurityNowUtc, "fingerprint", null!,
                    AuthSessionId: SessionId,
                    AccessContextId: 12,
                    ExpectedAccessRevision: 3),
                [CapabilityKeys.WorkManage, CapabilityKeys.RentalsManage],
                db,
                SecurityNowUtc)
            .ToQueryString();
        var conversationCommand = new SendConversationMessageCommand(
            17, 88, 9, "Subject", "Body", ConversationSenderRole.Landlord, [], SecurityNowUtc,
            42, new ConversationManagementAccess(SessionId, 5, 12, 3));
        var conversationSql = SendConversationMessageHandler.WhereManagementAuthorized(
                db.Set<Conversation>().Where(conversation =>
                    conversation.Id == 88 && conversation.PortfolioId == 17),
                db,
                conversationCommand,
                SecurityNowUtc)
            .ToQueryString();
        var tenantSql = SendConversationMessageHandler.WhereManagementAuthorizedForStart(
                db.Set<Tenant>().Where(tenant => tenant.Id == 9 && tenant.PortfolioId == 17),
                db,
                conversationCommand,
                SecurityNowUtc,
                new DateOnly(2026, 8, 13))
            .ToQueryString();
        var dispatchSql = DispatchWorkOrderToVendorHandler.WhereManagementAuthorized(
                db.Set<WorkOrder>().Where(workOrder => workOrder.Id == 71 && workOrder.PortfolioId == 17),
                db,
                17,
                new DispatchManagementAccess(SessionId, 5, 12, 3),
                SecurityNowUtc)
            .ToQueryString();
        var aiSql = AiIntegrationCommandSupport.PropertyCapabilityAssignments(
                17,
                new PortfolioQaDeliveryCommand(
                    17, 5, SessionId, 12, 3, "Question", "Answer", null, null, "email-key", "sms-key",
                    SecurityNowUtc),
                CapabilityKeys.MoneyOwnerReportsRead,
                db,
                SecurityNowUtc)
            .ToQueryString();

        foreach (var sql in new[] { ownerSql, scanSql, conversationSql, tenantSql, dispatchSql, aiSql })
        {
            sql.Should().Contain("MembershipRoleAssignments");
            sql.Should().Contain("WorkspaceMemberships");
            sql.Should().Contain("WorkspaceAccessContexts");
            sql.Should().Contain("AuthSessions");
            sql.Should().Contain("RoleProfileCapabilities");
            sql.Should().Contain("MembershipRoleAssignmentProperties");
            sql.Should().Contain("AllProperties");
            sql.Should().Contain("SelectedProperties");
            sql.Should().NotContain("ClientEvaluation");
            sql.TrimEnd().Should().NotEndWith(";",
                "the complete authorization consumer must render as one SQL statement");
        }

        ownerSql.Should().Contain("OwnerEntities");
        ownerSql.Should().Contain("PropertyOwnerships");
        ownerSql.Should().Contain("money.owner-reports.read");
        scanSql.Should().Contain("Properties");
        scanSql.Should().Contain("work.manage");
        scanSql.Should().Contain("rentals.manage");
        conversationSql.Should().Contain("Conversations");
        conversationSql.Should().Contain("rentals.manage");
        conversationSql.Should().Contain("leasing.onboarding.manage");
        tenantSql.Should().Contain("Tenants");
        tenantSql.Should().Contain("LeaseManagementParties");
        dispatchSql.Should().Contain("WorkOrders");
        dispatchSql.Should().Contain("work.manage");
        aiSql.Should().Contain("money.owner-reports.read");
    }

    [Fact]
    public void Notification_authorization_consumers_translate_as_single_statements()
    {
        using var db = Context();

        var conversationSql = AtomicNotificationMutationHandler.LandlordConversationQuery(
                new AtomicNotificationMutationCommand(
                    17, 5, SessionId, 12, 3,
                    AtomicNotificationMutationDomain.LandlordConversationRead,
                    88, "conversation:88", "{}", "conversation-read-replay"),
                db,
                SecurityNowUtc)
            .ToQueryString();
        var noticeCommand = new AtomicNoticeDeliveryCommand(
            17, 5, SessionId, 12, 3, 91,
            [NoticeDeliveryChannel.Email], null, null, "notice-delivery-replay");
        var authorizedProperties = AtomicNoticeDeliveryHandler.AuthorizedProperties(
            noticeCommand, db, SecurityNowUtc);
        var noticeSql = db.Set<NoticeDraft>().Where(draft =>
                draft.Id == noticeCommand.NoticeDraftId
                && draft.PortfolioId == noticeCommand.PortfolioId
                && draft.PropertyId != null
                && authorizedProperties.Any(property =>
                    property.Id == draft.PropertyId.Value
                    && property.PortfolioId == draft.PortfolioId))
            .ToQueryString();

        foreach (var sql in new[] { conversationSql, noticeSql })
        {
            sql.Should().Contain("MembershipRoleAssignments");
            sql.Should().Contain("WorkspaceMemberships");
            sql.Should().Contain("WorkspaceAccessContexts");
            sql.Should().Contain("AuthSessions");
            sql.Should().Contain("RoleProfileCapabilities");
            sql.Should().Contain("MembershipRoleAssignmentProperties");
            sql.Should().Contain("AllProperties");
            sql.Should().Contain("SelectedProperties");
            sql.Should().NotContain("ClientEvaluation");
            sql.TrimEnd().Should().NotEndWith(";",
                "the complete authorization consumer must render as one SQL statement");
        }

        conversationSql.Should().Contain("Conversations");
        conversationSql.Should().Contain("rentals.read");
        noticeSql.Should().Contain("NoticeDrafts");
        noticeSql.Should().Contain("Properties");
        noticeSql.Should().Contain("notifications.tenant-notices.manage");
    }

    [Fact]
    public void Scan_upload_and_analytics_authorization_use_complete_canonical_sql_shapes()
    {
        using var db = Context();

        var captureContext = new ScanCaptureContextData(
            WorkspaceExperience.Maintenance,
            12,
            3,
            41,
            42,
            43,
            44,
            45,
            46,
            47,
            48,
            49,
            "translation-shape");
        var scanSql = FinalizeScanUploadHandler.CaptureContextAuthorizationQuery(
                new FinalizeScanUploadCommand(
                    17,
                    5,
                    SessionId,
                    12,
                    3,
                    "scan-upload-translation",
                    "fingerprint",
                    "WorkOrder",
                    false,
                    null,
                    SecurityNowUtc,
                    [],
                    captureContext),
                captureContext,
                db,
                SecurityNowUtc)
            .ToQueryString();

        foreach (var relation in new[]
                 {
                     "Portfolios", "Properties", "Units", "LeaseManagements", "LeaseAgreements",
                     "TenantAccounts", "TenantLedgerEntries", "WorkOrders", "WorkOrderResponsibilities",
                     "RentalApplications", "RentalListings", "MembershipRoleAssignments",
                     "WorkspaceMemberships", "WorkspaceAccessContexts", "AuthSessions",
                     "RoleProfileCapabilities", "MembershipRoleAssignmentProperties",
                 })
        {
            scanSql.Should().Contain(relation);
        }

        scanSql.Should().Contain(CapabilityKeys.WorkManage);
        scanSql.Should().Contain(CapabilityKeys.AssignedWorkUpdate);
        scanSql.Should().Contain(nameof(CapabilityAuthorizationTargetKind.Property));
        scanSql.Should().Contain(nameof(CapabilityAuthorizationTargetKind.WorkOrder));
        scanSql.Should().Contain(nameof(MembershipRoleAssignmentScopeKind.AllProperties));
        scanSql.Should().Contain(nameof(MembershipRoleAssignmentScopeKind.SelectedProperties));
        scanSql.Should().Contain(nameof(MembershipRoleAssignmentScopeKind.AssignedWorkOrders));
        scanSql.Should().NotContain("ClientEvaluation");
        scanSql.TrimEnd().Should().NotEndWith(";",
            "the complete scan-upload authorization consumer must render as one SQL statement");

        var analyticsAuthorizationSql = db.AuthorizedPropertyIds(
                new WorkspaceReadScope(17, 5, SessionId, 12, 3),
                [CapabilityKeys.ReportsRead],
                SecurityNowUtc)
            .ToQueryString();

        AnalyticsService.AuthorizationSource.Should().Be(
            WorkspaceAuthorizationQuery.SecurityTimeAuthorizedPropertyIdsFunctionName);
        AnalyticsService.UseCanonicalAuthorizationSource(
                $"SELECT * FROM __canonical_authorized_property_ids__()")
            .Format.Should().Contain(AnalyticsService.AuthorizationSource);
        analyticsAuthorizationSql.Should().Contain(AnalyticsService.AuthorizationSource);
        analyticsAuthorizationSql.Should().Contain(CapabilityKeys.ReportsRead);
        analyticsAuthorizationSql.Should().NotContain("AuthSessions");
        analyticsAuthorizationSql.Should().NotContain("WorkspaceAccessContexts");
        analyticsAuthorizationSql.Should().NotContain("WorkspaceMemberships");
        analyticsAuthorizationSql.Should().NotContain("MembershipRoleAssignments");
        analyticsAuthorizationSql.TrimEnd().Should().NotEndWith(";",
            "the canonical Analytics authorization source must stay composable in one statement");
    }

    private static RentalCommandDbContext Context() => new(
        new DbContextOptionsBuilder<RentalCommandDbContext>()
            .UseNpgsql("Host=localhost;Database=translation_only;Username=none;Password=none")
            .Options);
}
