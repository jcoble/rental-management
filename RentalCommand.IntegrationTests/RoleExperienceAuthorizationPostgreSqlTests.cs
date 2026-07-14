using System.Data.Common;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using RentalCommand.Api.DTOs;
using RentalCommand.Api.Services.Domain;
using RentalCommand.Core.Authorization;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Data;
using RentalCommand.TestCommon;
using Xunit;

namespace RentalCommand.IntegrationTests;

[Collection(RoleAuthorityPostgreSqlCollection.Name)]
public sealed class RoleExperienceAuthorizationPostgreSqlTests : IAsyncLifetime
{
    private readonly MigratedPostgreSqlFixture _fixture;
    private readonly List<string> _commands = [];
    private MigratedPostgreSqlTestContext _context = null!;

    public RoleExperienceAuthorizationPostgreSqlTests(MigratedPostgreSqlFixture fixture) =>
        _fixture = fixture;

    public async Task InitializeAsync() =>
        _context = await _fixture.CreateContextAsync([new QueryRecorder(_commands)]);

    public async Task DisposeAsync()
    {
        if (_context is not null) await _context.DisposeAsync();
    }

    [Fact]
    public async Task LeasingRentals_StayInsideSelectedProperty_AndFailClosedForStaleOrRevokedAuthority()
    {
        var scenario = await SeedLeasingScenarioAsync();
        var sut = new LeasingWorkspaceService(_context.Db, TimeProvider.System);
        var query = new ListQuery { Search = "unit", Sort = "name", Take = 20 };
        _commands.Clear();

        var allowed = await sut.ListRentalsAsync(scenario.Scope, query);

        allowed.TotalCount.Should().Be(1);
        allowed.Items.Should().ContainSingle(item => item.UnitId == scenario.AllowedUnitId);
        allowed.Items.Should().NotContain(item => item.UnitId == scenario.OtherPropertyUnitId);
        allowed.Items.Should().NotContain(item => item.UnitId == scenario.OtherPortfolioUnitId);
        _commands.Should().HaveCount(2, "count and bounded page data must be the only database round trips");
        _commands.Should().ContainSingle(sql =>
            sql.Contains("SELECT count(*)::int", StringComparison.OrdinalIgnoreCase) &&
            sql.Contains("ILIKE", StringComparison.OrdinalIgnoreCase));
        _commands.Should().ContainSingle(sql =>
            sql.Contains("ILIKE", StringComparison.OrdinalIgnoreCase) &&
            sql.Contains("ORDER BY", StringComparison.OrdinalIgnoreCase) &&
            sql.Contains("LIMIT", StringComparison.OrdinalIgnoreCase));

        var stale = await sut.ListRentalsAsync(
            scenario.Scope with { AccessRevision = scenario.Scope.AccessRevision + 1 }, query);
        stale.Items.Should().BeEmpty();
        stale.TotalCount.Should().Be(0);

        scenario.Session.Status = AuthSessionStatus.Revoked;
        scenario.Session.RevokedAtUtc = DateTime.UtcNow;
        await _context.Db.SaveChangesAsync();
        _context.Db.ChangeTracker.Clear();
        (await sut.ListRentalsAsync(scenario.Scope, query)).Items.Should().BeEmpty();

        var session = await _context.Db.AuthSessions.SingleAsync(row => row.Id == scenario.Scope.SessionId);
        session.Status = AuthSessionStatus.Active;
        session.RevokedAtUtc = null;
        var membership = await _context.Db.WorkspaceMemberships.SingleAsync(row => row.Id == scenario.MembershipId);
        membership.Status = WorkspaceMembershipStatus.Revoked;
        membership.RevokedAtUtc = DateTime.UtcNow;
        await _context.Db.SaveChangesAsync();
        _context.Db.ChangeTracker.Clear();
        (await sut.ListRentalsAsync(scenario.Scope, query)).Items.Should().BeEmpty();
    }

    [Fact]
    public async Task TechnicianReads_ReturnOnlyAssignedWorkAndItsEntriesAndMessages_ThenFailClosed()
    {
        var scenario = await SeedTechnicianScenarioAsync();
        var sut = new TechnicianExperienceService(_context.Db, TimeProvider.System);
        var query = new TechnicianAssignmentQuery
        {
            Search = "repair",
            Sort = "title",
            Take = 20,
        };
        _commands.Clear();

        var page = await sut.ListAssignmentsAsync(scenario.Scope, query, conversationsOnly: false, default);

        page.TotalCount.Should().Be(1);
        page.Items.Should().ContainSingle(item => item.Id == scenario.AssignedWorkOrderId);
        page.Items.Should().NotContain(item => item.Id == scenario.UnassignedWorkOrderId);
        page.Items.Should().NotContain(item => item.Id == scenario.OtherPortfolioWorkOrderId);
        _commands.Should().HaveCount(2, "authorization, search, sort, and paging remain in count/page SQL");
        _commands.Should().ContainSingle(sql =>
            sql.Contains("count", StringComparison.OrdinalIgnoreCase) &&
            sql.Contains("ILIKE", StringComparison.OrdinalIgnoreCase));
        _commands.Should().ContainSingle(sql =>
            sql.Contains("ILIKE", StringComparison.OrdinalIgnoreCase) &&
            sql.Contains("ORDER BY", StringComparison.OrdinalIgnoreCase) &&
            sql.Contains("LIMIT", StringComparison.OrdinalIgnoreCase));

        var detail = await sut.GetAssignmentAsync(scenario.Scope, scenario.AssignedWorkOrderId, default);
        detail.Should().NotBeNull();
        detail!.Entries.Should().ContainSingle(entry => entry.Note == "Assigned entry");
        detail.Messages.Should().ContainSingle(message => message.Body == "Assigned message");
        (await sut.GetAssignmentAsync(scenario.Scope, scenario.UnassignedWorkOrderId, default)).Should().BeNull();
        (await sut.GetAssignmentAsync(scenario.Scope, scenario.OtherPortfolioWorkOrderId, default)).Should().BeNull();

        var staleScope = scenario.Scope with { AccessRevision = scenario.Scope.AccessRevision + 1 };
        (await sut.ListAssignmentsAsync(staleScope, query, false, default)).Items.Should().BeEmpty();
        (await sut.GetAssignmentAsync(staleScope, scenario.AssignedWorkOrderId, default)).Should().BeNull();

        scenario.Session.Status = AuthSessionStatus.Revoked;
        scenario.Session.RevokedAtUtc = DateTime.UtcNow;
        await _context.Db.SaveChangesAsync();
        _context.Db.ChangeTracker.Clear();
        (await sut.ListAssignmentsAsync(scenario.Scope, query, false, default)).Items.Should().BeEmpty();

        var session = await _context.Db.AuthSessions.SingleAsync(row => row.Id == scenario.Scope.SessionId);
        session.Status = AuthSessionStatus.Active;
        session.RevokedAtUtc = null;
        var membership = await _context.Db.WorkspaceMemberships.SingleAsync(row => row.Id == scenario.MembershipId);
        membership.Status = WorkspaceMembershipStatus.Revoked;
        membership.RevokedAtUtc = DateTime.UtcNow;
        await _context.Db.SaveChangesAsync();
        _context.Db.ChangeTracker.Clear();
        (await sut.GetAssignmentAsync(scenario.Scope, scenario.AssignedWorkOrderId, default)).Should().BeNull();
    }

    [Fact]
    public async Task NotificationFoundationReads_DoNotMixPersonalTeamOrTenantPolicyRowsAcrossPortfolios()
    {
        var scenario = await SeedNotificationScenarioAsync();
        var sut = new NotificationFoundationService(_context.Db, TimeProvider.System, null!);
        _commands.Clear();

        var alerts = await sut.GetMyAlertsAsync(1, scenario.UserId, default);
        var routes = await sut.ListTeamRoutingRulesAsync(1, default);
        var policies = await sut.ListTenantNoticePoliciesAsync(1, default);

        alerts.EnableEmail.Should().BeFalse();
        alerts.EnableSms.Should().BeFalse("the other portfolio preference must never bleed into this workspace");
        routes.Should().ContainSingle(row => row.Id == scenario.PortfolioOneRuleId);
        routes.Should().NotContain(row => row.Id == scenario.PortfolioTwoRuleId);
        policies.Should().ContainSingle(row => row.Id == scenario.PortfolioOnePolicyId);
        policies.Should().NotContain(row => row.Id == scenario.PortfolioTwoPolicyId);
        _commands.Should().HaveCount(3, "each notification area must stay one server-side query without per-row lookups");
        _commands.Should().OnlyContain(sql =>
            sql.Contains("SELECT", StringComparison.OrdinalIgnoreCase) &&
            sql.Contains("WHERE", StringComparison.OrdinalIgnoreCase));
    }

    private async Task<LeasingScenario> SeedLeasingScenarioAsync()
    {
        var now = DateTime.UtcNow;
        var db = _context.Db;
        var user = User("leasing-boundary@example.test", "Leasing Boundary");
        var otherPortfolio = Portfolio("Other Leasing Portfolio", now);
        var allowedProperty = Property(1, "Allowed Unit Property", now);
        var otherProperty = Property(1, "Other Unit Property", now);
        db.AddRange(user, otherPortfolio, allowedProperty, otherProperty);
        await db.SaveChangesAsync();
        var crossProperty = Property(otherPortfolio.Id, "Cross Portfolio Unit Property", now);
        db.Properties.Add(crossProperty);
        await db.SaveChangesAsync();

        var allowedUnit = Unit(1, allowedProperty.Id, "Unit A", now);
        var otherUnit = Unit(1, otherProperty.Id, "Unit B", now);
        var crossUnit = Unit(otherPortfolio.Id, crossProperty.Id, "Unit C", now);
        db.Units.AddRange(allowedUnit, otherUnit, crossUnit);
        var authority = Authority(user, 1, WorkspaceExperience.Leasing,
            AccessCatalog.Roles.Single(role => role.Key == RoleProfileKeys.LeasingAgent).Id,
            MembershipRoleAssignmentScopeKind.SelectedProperties, now);
        authority.Assignment.SelectedProperties.Add(new MembershipRoleAssignmentProperty
        {
            MembershipRoleAssignment = authority.Assignment,
            PortfolioId = 1,
            Property = allowedProperty,
        });
        db.AddRange(authority.Assignment, authority.Session);
        await db.SaveChangesAsync();
        return new LeasingScenario(
            new WorkspaceReadScope(1, user.Id, authority.Session.Id, authority.Context.Id,
                authority.Context.AccessRevision),
            authority.Membership.Id, authority.Session, allowedUnit.Id, otherUnit.Id, crossUnit.Id);
    }

    private async Task<TechnicianScenario> SeedTechnicianScenarioAsync()
    {
        var now = DateTime.UtcNow;
        var db = _context.Db;
        var user = User("technician-boundary@example.test", "Technician Boundary");
        var tenant = new Tenant
        {
            PortfolioId = 1,
            FirstName = "Assigned",
            LastName = "Tenant",
            Email = "assigned-tenant@example.test",
            CreatedAt = now,
            UpdatedAt = now,
        };
        var otherPortfolio = Portfolio("Other Technician Portfolio", now);
        var property = Property(1, "Technician Property", now);
        db.AddRange(user, tenant, otherPortfolio, property);
        await db.SaveChangesAsync();
        var crossProperty = Property(otherPortfolio.Id, "Cross Technician Property", now);
        db.Properties.Add(crossProperty);
        await db.SaveChangesAsync();

        var assigned = WorkOrder(1, property.Id, tenant.Id, "Assigned repair", now);
        var unassigned = WorkOrder(1, property.Id, tenant.Id, "Unassigned repair", now);
        var cross = WorkOrder(otherPortfolio.Id, crossProperty.Id, null, "Cross portfolio repair", now);
        db.WorkOrders.AddRange(assigned, unassigned, cross);
        var authority = Authority(user, 1, WorkspaceExperience.Maintenance,
            AccessCatalog.Roles.Single(role => role.Key == RoleProfileKeys.MaintenanceTechnician).Id,
            MembershipRoleAssignmentScopeKind.AssignedWorkOrders, now);
        db.AddRange(authority.Assignment, authority.Session);
        await db.SaveChangesAsync();

        var assignedAt = now.AddMinutes(-1);
        var responsibility = new WorkOrderResponsibility
        {
            Id = Guid.NewGuid(),
            PortfolioId = 1,
            PropertyId = property.Id,
            WorkOrderId = assigned.Id,
            WorkspaceMembershipId = authority.Membership.Id,
            MembershipRoleAssignmentId = authority.Assignment.Id,
            Kind = WorkOrderResponsibilityKind.Primary,
            EffectiveFromUtc = assignedAt,
            AssignedByUserId = user.Id,
            AssignedByAccessContextId = authority.Context.Id,
            AssignedReason = "Adversarial role-boundary proof",
            AssignedAtUtc = assignedAt,
        };
        db.WorkOrderResponsibilities.Add(responsibility);
        await db.SaveChangesAsync();

        var conversation = new Conversation
        {
            PortfolioId = 1,
            TenantId = tenant.Id,
            PropertyId = property.Id,
            WorkOrderId = assigned.Id,
            Subject = "Assigned repair",
            CreatedAt = now,
            LastMessageAt = now,
            LastMessagePreview = "Assigned message",
            TechnicianUnreadCount = 1,
        };
        db.AddRange(
            new TechnicianWorkEntry
            {
                PortfolioId = 1,
                WorkOrderId = assigned.Id,
                WorkOrderResponsibilityId = responsibility.Id,
                WorkspaceMembershipId = authority.Membership.Id,
                MembershipRoleAssignmentId = authority.Assignment.Id,
                CreatedByUserId = user.Id,
                Kind = TechnicianWorkEntryKind.Note,
                Note = "Assigned entry",
                OccurredAtUtc = now,
                CreatedAtUtc = now,
            },
            conversation);
        conversation.Messages.Add(new ConversationMessage
        {
            SenderRole = ConversationSenderRole.Landlord,
            Body = "Assigned message",
            CreatedAt = now,
        });
        await db.SaveChangesAsync();
        return new TechnicianScenario(
            new WorkspaceReadScope(1, user.Id, authority.Session.Id, authority.Context.Id,
                authority.Context.AccessRevision),
            authority.Membership.Id, authority.Session, assigned.Id, unassigned.Id, cross.Id);
    }

    private async Task<NotificationScenario> SeedNotificationScenarioAsync()
    {
        var now = DateTime.UtcNow;
        var db = _context.Db;
        var user = User("notification-boundary@example.test", "Notification Boundary");
        var otherPortfolio = Portfolio("Other Notification Portfolio", now);
        db.AddRange(user, otherPortfolio);
        await db.SaveChangesAsync();
        db.WorkspaceAccessContexts.AddRange(
            new WorkspaceAccessContext
            {
                UserId = user.Id,
                PortfolioId = 1,
                Status = WorkspaceAccessContextStatus.Active,
                CreatedAtUtc = now,
                UpdatedAtUtc = now,
            },
            new WorkspaceAccessContext
            {
                UserId = user.Id,
                PortfolioId = otherPortfolio.Id,
                Status = WorkspaceAccessContextStatus.Active,
                CreatedAtUtc = now,
                UpdatedAtUtc = now,
            });
        db.UserAlertPreferences.AddRange(
            new UserAlertPreference
            {
                PortfolioId = 1,
                UserId = user.Id,
                EnableEmail = false,
                EnableSms = false,
                CreatedAtUtc = now,
                UpdatedAtUtc = now,
            },
            new UserAlertPreference
            {
                PortfolioId = otherPortfolio.Id,
                UserId = user.Id,
                EnableEmail = true,
                EnableSms = true,
                CreatedAtUtc = now,
                UpdatedAtUtc = now,
            });
        var firstRule = new TeamRoutingRule
        {
            PortfolioId = 1,
            Topic = TeamRoutingTopic.WorkOrders,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
        };
        var secondRule = new TeamRoutingRule
        {
            PortfolioId = otherPortfolio.Id,
            Topic = TeamRoutingTopic.WorkOrders,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
        };
        db.TeamRoutingRules.AddRange(firstRule, secondRule);
        await db.SaveChangesAsync();
        var systemTemplateId = await db.SystemNoticeTemplateVersions
            .Where(template => template.SystemKey == "rent-reminder" && template.Version == 1)
            .Select(template => template.Id)
            .SingleAsync();
        var firstTemplate = WorkspaceTemplate(1, user.Id, systemTemplateId, now);
        var secondTemplate = WorkspaceTemplate(otherPortfolio.Id, user.Id, systemTemplateId, now);
        db.WorkspaceNoticeTemplateVersions.AddRange(firstTemplate, secondTemplate);
        await db.SaveChangesAsync();
        var firstPolicy = TenantPolicy(1, firstTemplate.Id, now);
        var secondPolicy = TenantPolicy(otherPortfolio.Id, secondTemplate.Id, now);
        db.TenantNoticePolicies.AddRange(firstPolicy, secondPolicy);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        return new NotificationScenario(user.Id, firstRule.Id, secondRule.Id, firstPolicy.Id, secondPolicy.Id);
    }

    private static AuthorityRows Authority(ApplicationUser user, int portfolioId,
        WorkspaceExperience experience, int roleProfileId,
        MembershipRoleAssignmentScopeKind scopeKind, DateTime now)
    {
        var context = new WorkspaceAccessContext
        {
            User = user,
            PortfolioId = portfolioId,
            Status = WorkspaceAccessContextStatus.Active,
            LastAuthorizedExperience = experience,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
        };
        var membership = new WorkspaceMembership
        {
            AccessContext = context,
            PortfolioId = portfolioId,
            Status = WorkspaceMembershipStatus.Active,
            DefaultExperience = experience,
            EffectiveFromUtc = now.AddMinutes(-5),
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
        };
        var assignment = new MembershipRoleAssignment
        {
            WorkspaceMembership = membership,
            PortfolioId = portfolioId,
            RoleProfileId = roleProfileId,
            Status = MembershipRoleAssignmentStatus.Active,
            ScopeKind = scopeKind,
            EffectiveFromUtc = now.AddMinutes(-5),
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
        };
        var session = new AuthSession
        {
            Id = Guid.NewGuid(),
            User = user,
            ActiveAccessContext = context,
            Status = AuthSessionStatus.Active,
            CreatedAtUtc = now,
            LastSeenAtUtc = now,
            ExpiresAtUtc = now.AddHours(1),
        };
        return new AuthorityRows(context, membership, assignment, session);
    }

    private static ApplicationUser User(string email, string displayName) => new()
    {
        UserName = email,
        NormalizedUserName = email.ToUpperInvariant(),
        Email = email,
        NormalizedEmail = email.ToUpperInvariant(),
        DisplayName = displayName,
        SecurityStamp = Guid.NewGuid().ToString("N"),
        ConcurrencyStamp = Guid.NewGuid().ToString("N"),
        CreatedAt = DateTime.UtcNow,
    };

    private static Portfolio Portfolio(string name, DateTime now) => new()
    {
        Name = name,
        ManagementCompanyName = name,
        TimeZone = "UTC",
        CreatedAt = now,
        UpdatedAt = now,
    };

    private static Property Property(int portfolioId, string name, DateTime now) => new()
    {
        PortfolioId = portfolioId,
        Name = name,
        AddressLine1 = $"1 {name} Way",
        City = "Columbus",
        State = "OH",
        PostalCode = "43215",
        CreatedAt = now,
        UpdatedAt = now,
    };

    private static Unit Unit(int portfolioId, int propertyId, string number, DateTime now) => new()
    {
        PortfolioId = portfolioId,
        PropertyId = propertyId,
        UnitNumber = number,
        CreatedAt = now,
        UpdatedAt = now,
    };

    private static WorkOrder WorkOrder(int portfolioId, int propertyId, int? tenantId,
        string title, DateTime now) => new()
    {
        PortfolioId = portfolioId,
        PropertyId = propertyId,
        TenantId = tenantId,
        Title = title,
        Description = title,
        Category = "General",
        Status = WorkOrderStatus.New,
        RequestedAt = now,
        UpdatedAt = now,
    };

    private static WorkspaceNoticeTemplateVersion WorkspaceTemplate(
        int portfolioId, int userId, int systemTemplateId, DateTime now) => new()
    {
        PortfolioId = portfolioId,
        SystemKey = "rent-reminder",
        Version = 1,
        BasedOnSystemTemplateVersionId = systemTemplateId,
        Subject = "Rent reminder",
        Body = "Rent reminder body",
        CreatedByUserId = userId,
        CreatedAtUtc = now,
    };

    private static TenantNoticePolicy TenantPolicy(int portfolioId, int templateId, DateTime now) => new()
    {
        PortfolioId = portfolioId,
        AutomationKey = "rent-reminder",
        Mode = TenantNoticeMode.Draft,
        Classification = NoticeClassification.Courtesy,
        LeadDays = 3,
        WorkspaceNoticeTemplateVersionId = templateId,
        CreatedAtUtc = now,
        UpdatedAtUtc = now,
    };

    private sealed record AuthorityRows(
        WorkspaceAccessContext Context,
        WorkspaceMembership Membership,
        MembershipRoleAssignment Assignment,
        AuthSession Session);

    private sealed record LeasingScenario(
        WorkspaceReadScope Scope,
        int MembershipId,
        AuthSession Session,
        int AllowedUnitId,
        int OtherPropertyUnitId,
        int OtherPortfolioUnitId);

    private sealed record TechnicianScenario(
        WorkspaceReadScope Scope,
        int MembershipId,
        AuthSession Session,
        int AssignedWorkOrderId,
        int UnassignedWorkOrderId,
        int OtherPortfolioWorkOrderId);

    private sealed record NotificationScenario(
        int UserId,
        int PortfolioOneRuleId,
        int PortfolioTwoRuleId,
        int PortfolioOnePolicyId,
        int PortfolioTwoPolicyId);

    private sealed class QueryRecorder(List<string> commands) : DbCommandInterceptor
    {
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            commands.Add(command.CommandText);
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }
    }
}
