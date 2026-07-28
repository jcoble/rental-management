using System.Diagnostics;
using FluentAssertions;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;
using RentalCommand.Api.Data;
using RentalCommand.Api.DTOs;
using RentalCommand.Api.Hubs;
using RentalCommand.Api.Services;
using RentalCommand.Core.Authorization;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Navigation;
using RentalCommand.Core.Interfaces;
using RentalCommand.TestCommon;

namespace RentalCommand.Api.Tests.Auth;

public sealed class DataUpdateServiceAuthorizationTests : IDisposable
{
    private readonly List<string> _commands = [];
    private readonly SqliteTestContext _context;
    private readonly Mock<IClientProxy> _client = new();
    private readonly Mock<IHubClients> _clients = new();
    private readonly Mock<IHubContext<DataUpdateHub>> _hub = new();
    private readonly DataUpdateService _service;
    private IReadOnlyList<string> _deliveredGroups = [];

    public DataUpdateServiceAuthorizationTests()
    {
        _context = new SqliteTestContext(
            [new Domain.RecordingCommandInterceptor(_commands)]);
        EnsureRelationshipProjectionView();
        _hub.SetupGet(value => value.Clients).Returns(_clients.Object);
        _clients.Setup(value => value.Groups(It.IsAny<IReadOnlyList<string>>()))
            .Callback<IReadOnlyList<string>>(groups => _deliveredGroups = groups)
            .Returns(_client.Object);
        _client.Setup(value => value.SendCoreAsync(
                It.IsAny<string>(), It.IsAny<object?[]>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        _service = new DataUpdateService(
            _context.Db,
            _hub.Object,
            TimeProvider.System,
            Mock.Of<ILogger<DataUpdateService>>());
    }

    [Fact]
    public async Task PropertyInvalidation_TargetsOnlyCurrentAuthorizedSessionRevisions()
    {
        var now = DateTime.UtcNow;
        var targetProperty = AddProperty("Target");
        var decoyProperty = AddProperty("Decoy");
        _context.Db.SaveChanges();

        var target = AddTeamSession("target", targetProperty, now);
        var decoy = AddTeamSession("decoy", decoyProperty, now);
        var stale = AddTeamSession("stale", targetProperty, now, currentRevision: 2);
        var revoked = AddTeamSession("revoked", targetProperty, now, revoked: true);
        var relationshipOnly = AddEffectiveTenantSession("tenant", now);
        _context.Db.SaveChanges();
        _commands.Clear();

        await _service.BroadcastEntityUpdateAsync(
            1,
            "Property",
            targetProperty.Id,
            new { targetProperty.Id, Secret = "must-not-reach-signalr" });

        _deliveredGroups.Should().Contain(DataUpdateHub.SessionRevisionGroup(target.SessionId, 1));
        _deliveredGroups.Should().Contain(DataUpdateHub.SessionRevisionGroup(stale.SessionId, 2));
        _deliveredGroups.Should().NotContain(DataUpdateHub.SessionRevisionGroup(stale.SessionId, 1),
            "a connection admitted under an older access revision must stop receiving immediately");
        _deliveredGroups.Should().NotContain(DataUpdateHub.SessionRevisionGroup(decoy.SessionId, 1),
            "selected-property scope must be applied before fanout");
        _deliveredGroups.Should().NotContain(DataUpdateHub.SessionRevisionGroup(revoked.SessionId, 1));
        _deliveredGroups.Should().NotContain(DataUpdateHub.SessionRevisionGroup(relationshipOnly.SessionId, 1));

        _commands.Should().HaveCount(1,
            "resource mapping, capability scope, and current-session selection must be one SQL query");
        _client.Verify(value => value.SendCoreAsync(
            "EntityUpdated",
            It.Is<object?[]>(arguments =>
                arguments.Length == 1
                && arguments[0] != null
                && arguments[0]!.GetType() == typeof(EntityUpdatePayload)
                && ((EntityUpdatePayload)arguments[0]!).EntityType == "Property"
                && ((EntityUpdatePayload)arguments[0]!).EntityId == targetProperty.Id),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task PortfolioNotificationInvalidation_TargetsTeamAndEffectiveTenantSessionsInOneQuery()
    {
        var now = DateTime.UtcNow;
        var teamProperty = AddProperty("Notification Team");
        _context.Db.SaveChanges();

        var team = AddTeamSession("notification-team", teamProperty, now);
        var tenant = AddEffectiveTenantSession("notification-tenant", now);
        var mutedTenant = AddEffectiveTenantSession("notification-muted-tenant", now);
        var unbacked = AddBareContextSession("notification-unbacked", now);
        var notification = new Notification
        {
            PortfolioId = 1,
            Type = "System",
            Title = "Water interruption",
            Message = "Water will be off from noon until two.",
            CreatedAt = now,
        };
        _context.Db.Notifications.Add(notification);
        _context.Db.SaveChanges();
        _context.Db.UserAlertPreferences.Add(new UserAlertPreference
        {
            PortfolioId = 1,
            UserId = mutedTenant.User.Id,
            EnableInApp = false,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
        });
        _context.Db.SaveChanges();
        _commands.Clear();

        await _service.BroadcastEntityUpdateAsync(
            1,
            "Notification",
            notification.Id,
            NotificationResponse.FromEntity(notification));

        _deliveredGroups.Should().Contain(DataUpdateHub.SessionRevisionGroup(team.SessionId, 1));
        _deliveredGroups.Should().Contain(
            DataUpdateHub.SessionRevisionGroup(tenant.SessionId, 1),
            "portfolio announcements readable by an effective tenant must update the tenant UI live");
        _deliveredGroups.Should().NotContain(
            DataUpdateHub.SessionRevisionGroup(unbacked.SessionId, 1),
            "an active context label without current workspace or relationship authority is not readable");
        _deliveredGroups.Should().NotContain(
            DataUpdateHub.SessionRevisionGroup(mutedTenant.SessionId, 1),
            "realtime fanout must honor the same in-app preference as the REST notification read");
        _commands.Should().HaveCount(1,
            "notification authorization, effective access, and session selection must be one SQL query");
        _commands.Single().Should().Contain(
            "Realtime notification recipients: current REST-readable notification audience");
        _commands.Single().Should().NotContain(
            "ScopedNotificationRecipients: active workspace membership",
            "the realtime notification fanout must not nest the reusable staff-recipient query");
    }

    [Fact]
    public async Task TenantMessageNotificationInvalidation_UsesSavedAccessContextInOneQuery()
    {
        var now = DateTime.UtcNow;
        var targetProperty = AddProperty("Tenant Message Target");
        var decoyProperty = AddProperty("Tenant Message Decoy");
        _context.Db.SaveChanges();
        var target = AddTeamSession("tenant-message-target", targetProperty, now);
        var decoy = AddTeamSession("tenant-message-decoy", decoyProperty, now);
        _context.Db.SaveChanges();
        var notification = new Notification
        {
            PortfolioId = 1,
            UserId = target.User.Id,
            Type = "TenantMessage",
            Title = "New tenant message",
            Message = "The tenant replied.",
            CreatedAt = now,
            NavigationExperience = NavigationExperience.Management,
            NavigationDestination = NavigationDestination.Message,
            NavigationAccessContextId = target.Context.Id,
            NavigationAccessRevision = target.Context.AccessRevision,
            NavigationAction = NavigationAction.Open,
            NavigationExpiresAtUtc = now.AddDays(1),
            NavigationFallbackDestination = NavigationDestination.Notifications,
        };
        _context.Db.Notifications.Add(notification);
        _context.Db.SaveChanges();
        var response = NotificationResponse.FromEntity(notification);
        response.NavigationIntent.Should().NotBeNull();
        _commands.Clear();

        await _service.BroadcastEntityUpdateAsync(
            1,
            "Notification",
            notification.Id,
            response);

        _deliveredGroups.Should().Contain(DataUpdateHub.SessionRevisionGroup(target.SessionId, 1));
        _deliveredGroups.Should().NotContain(DataUpdateHub.SessionRevisionGroup(decoy.SessionId, 1),
            "a personally addressed tenant-message notification must fan out only through its saved access context");
        _commands.Should().HaveCount(1,
            "saved-context notification authorization and session selection must stay one SQL query");
        _commands.Single().Should().Contain(
            "Realtime notification recipients: saved navigation access context");
        _commands.Single().Should().NotContain(
            "ScopedNotificationRecipients: active workspace membership",
            "the tenant-message realtime query must not invoke the broad reusable staff-recipient helper");
        _commands.Single().Should().NotContain(
            "UNION",
            "a saved-context notification must not plan the portfolio broadcast branch under API-role RLS");
    }

    [Fact]
    public async Task BatchTenantMessageNotificationInvalidation_ResolvesSavedAccessContextsInOneQuery()
    {
        var now = DateTime.UtcNow;
        var targetProperty = AddProperty("Tenant Message Batch Target");
        var decoyProperty = AddProperty("Tenant Message Batch Decoy");
        _context.Db.SaveChanges();
        var targets = new[]
        {
            AddTeamSession("tenant-message-batch-target-1", targetProperty, now),
            AddTeamSession("tenant-message-batch-target-2", targetProperty, now),
            AddTeamSession("tenant-message-batch-target-3", targetProperty, now),
            AddTeamSession("tenant-message-batch-target-4", targetProperty, now),
        };
        var decoy = AddTeamSession("tenant-message-batch-decoy", decoyProperty, now);
        _context.Db.SaveChanges();
        var notifications = targets.Select((target, index) => new Notification
        {
            PortfolioId = 1,
            UserId = target.User.Id,
            Type = "TenantMessage",
            Title = "New tenant message",
            Message = $"The tenant replied {index}.",
            CreatedAt = now,
            NavigationExperience = NavigationExperience.Management,
            NavigationDestination = NavigationDestination.Message,
            NavigationAccessContextId = target.Context.Id,
            NavigationAccessRevision = target.Context.AccessRevision,
            NavigationAction = NavigationAction.Open,
            NavigationExpiresAtUtc = now.AddDays(1),
            NavigationFallbackDestination = NavigationDestination.Notifications,
        }).ToArray();
        _context.Db.Notifications.AddRange(notifications);
        _context.Db.SaveChanges();
        var sentPayloadIds = new List<int>();
        _client.Setup(value => value.SendCoreAsync(
                "EntityUpdated",
                It.IsAny<object?[]>(),
                It.IsAny<CancellationToken>()))
            .Callback<string, object?[], CancellationToken>((_, arguments, _) =>
            {
                if (arguments.SingleOrDefault() is EntityUpdatePayload payload)
                {
                    sentPayloadIds.Add(payload.EntityId);
                }
            })
            .Returns(Task.CompletedTask);
        _commands.Clear();

        await _service.BroadcastEntityUpdatesAsync(notifications
            .Select(notification => new EntityUpdateBroadcast(
                1,
                "Notification",
                notification.Id,
                NotificationResponse.FromEntity(notification)))
            .ToArray());

        sentPayloadIds.Should().BeEquivalentTo(notifications.Select(notification => notification.Id));
        _deliveredGroups.Should().NotContain(DataUpdateHub.SessionRevisionGroup(decoy.SessionId, 1),
            "a batch of personally addressed tenant-message notifications must not fan out to unrelated contexts");
        _commands.Should().HaveCount(1,
            "saved-context notification batches must not repeat the RLS recipient query once per notification");
        _commands.Single().Should().Contain(
            "Realtime notification recipients: saved navigation access contexts batch");
        _commands.Single().Should().NotContain(
            "UNION",
            "a saved-context notification batch must not plan the portfolio broadcast branch under API-role RLS");
    }

    [Fact]
    public async Task PropertyInvalidation_ReturnsPromptlyWhenHubSendNeverCompletes()
    {
        var now = DateTime.UtcNow;
        var targetProperty = AddProperty("Bounded fanout");
        _context.Db.SaveChanges();
        var target = AddTeamSession("bounded-fanout", targetProperty, now);
        _context.Db.SaveChanges();
        _commands.Clear();
        var blockedSend = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _client.Setup(value => value.SendCoreAsync(
                It.IsAny<string>(), It.IsAny<object?[]>(), It.IsAny<CancellationToken>()))
            .Returns(blockedSend.Task);
        var service = new DataUpdateService(
            _context.Db,
            _hub.Object,
            TimeProvider.System,
            Mock.Of<ILogger<DataUpdateService>>(),
            TimeSpan.FromMilliseconds(25));

        var elapsed = Stopwatch.StartNew();
        await service.BroadcastEntityUpdateAsync(
            1,
            "Property",
            targetProperty.Id,
            new { targetProperty.Id });
        elapsed.Stop();

        elapsed.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(1),
            "a stuck post-commit SignalR delivery must not hold the durable mutation response");
        blockedSend.Task.IsCompleted.Should().BeFalse();
        _deliveredGroups.Should().Contain(DataUpdateHub.SessionRevisionGroup(target.SessionId, 1));
        _commands.Should().HaveCount(1,
            "recipient authorization and session selection still run as one translated SQL query");
        _client.Verify(value => value.SendCoreAsync(
            "EntityUpdated",
            It.IsAny<object?[]>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task BatchPropertyInvalidation_SendsResolvedHubHintsConcurrently()
    {
        var now = DateTime.UtcNow;
        var targetProperty = AddProperty("Concurrent batch fanout");
        _context.Db.SaveChanges();
        AddTeamSession("batch-fanout", targetProperty, now);
        _context.Db.SaveChanges();
        _commands.Clear();
        var gate = new object();
        var inFlight = 0;
        var maxInFlight = 0;
        var calls = 0;
        _client.Setup(value => value.SendCoreAsync(
                It.IsAny<string>(), It.IsAny<object?[]>(), It.IsAny<CancellationToken>()))
            .Returns<string, object?[], CancellationToken>(async (_, _, _) =>
            {
                lock (gate)
                {
                    calls++;
                    inFlight++;
                    maxInFlight = Math.Max(maxInFlight, inFlight);
                }

                try
                {
                    await Task.Delay(300);
                }
                finally
                {
                    lock (gate)
                    {
                        inFlight--;
                    }
                }
            });
        var service = new DataUpdateService(
            _context.Db,
            _hub.Object,
            TimeProvider.System,
            Mock.Of<ILogger<DataUpdateService>>(),
            TimeSpan.FromSeconds(5));

        var elapsed = Stopwatch.StartNew();
        await service.BroadcastEntityUpdatesAsync(
        [
            new EntityUpdateBroadcast(1, "Property", targetProperty.Id, new { targetProperty.Id }),
            new EntityUpdateBroadcast(1, "Property", targetProperty.Id, new { targetProperty.Id }),
            new EntityUpdateBroadcast(1, "Property", targetProperty.Id, new { targetProperty.Id }),
        ]);
        elapsed.Stop();

        calls.Should().Be(3);
        maxInFlight.Should().BeGreaterThan(1,
            "post-commit realtime batches should not serialize one hub wait per invalidation");
        elapsed.Elapsed.Should().BeLessThan(TimeSpan.FromMilliseconds(800),
            "three 300 ms hub sends should overlap after the DB-side recipient queries resolve");
        _commands.Should().HaveCount(3,
            "each update still resolves its authorized audience with one translated SQL query");
    }

    private Property AddProperty(string name)
    {
        var now = DateTime.UtcNow;
        var property = new Property
        {
            PortfolioId = 1,
            Name = name,
            AddressLine1 = $"1 {name} Street",
            City = "Test",
            State = "OH",
            PostalCode = "44000",
            CreatedAt = now,
            UpdatedAt = now,
        };
        _context.Db.Properties.Add(property);
        return property;
    }

    private SessionCoordinates AddTeamSession(
        string key,
        Property property,
        DateTime now,
        long currentRevision = 1,
        bool revoked = false)
    {
        var user = AddUser(key, now);
        var context = new WorkspaceAccessContext
        {
            User = user,
            PortfolioId = 1,
            Status = WorkspaceAccessContextStatus.Active,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
        };
        if (currentRevision == 2)
        {
            context.AdvanceRevision(1);
        }

        var membership = new WorkspaceMembership
        {
            AccessContext = context,
            PortfolioId = 1,
            Status = WorkspaceMembershipStatus.Active,
            DefaultExperience = WorkspaceExperience.Management,
            EffectiveFromUtc = now.AddMinutes(-1),
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
        };
        var assignment = new MembershipRoleAssignment
        {
            WorkspaceMembership = membership,
            PortfolioId = 1,
            RoleProfileId = AccessCatalog.Roles.Single(role => role.Key == RoleProfileKeys.PropertyManager).Id,
            Status = MembershipRoleAssignmentStatus.Active,
            ScopeKind = MembershipRoleAssignmentScopeKind.SelectedProperties,
            EffectiveFromUtc = now.AddMinutes(-1),
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
        };
        assignment.SelectedProperties.Add(new MembershipRoleAssignmentProperty
        {
            MembershipRoleAssignment = assignment,
            Property = property,
            PortfolioId = 1,
        });
        var session = new AuthSession
        {
            Id = Guid.NewGuid(),
            User = user,
            ActiveAccessContext = context,
            Status = AuthSessionStatus.Active,
            CreatedAtUtc = now,
            LastSeenAtUtc = now,
            ExpiresAtUtc = now.AddHours(1),
            RevokedAtUtc = revoked ? now : null,
        };
        _context.Db.AddRange(assignment, session);
        return new SessionCoordinates(session.Id, user, context);
    }

    private SessionCoordinates AddEffectiveTenantSession(string key, DateTime now)
    {
        var user = AddUser(key, now);
        var context = new WorkspaceAccessContext
        {
            User = user,
            PortfolioId = 1,
            Status = WorkspaceAccessContextStatus.Active,
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
        var property = AddProperty($"{key} relationship");
        var unit = new Unit
        {
            PortfolioId = 1,
            Property = property,
            UnitNumber = "1",
            CreatedAt = now,
            UpdatedAt = now,
        };
        var tenant = new Tenant
        {
            PortfolioId = 1,
            FirstName = key,
            LastName = "Tenant",
            CreatedAt = now,
            UpdatedAt = now,
        };
        var relationship = new LeaseManagement
        {
            PublicId = Guid.NewGuid(),
            PortfolioId = 1,
            Property = property,
            Unit = unit,
            RelationshipNumber = $"LM-{key}",
            CreatedAtUtc = now,
            CreatedByUser = user,
            UpdatedAtUtc = now,
            RowVersion = Guid.NewGuid(),
        };
        var party = new LeaseManagementParty
        {
            PortfolioId = 1,
            LeaseManagement = relationship,
            Tenant = tenant,
            Role = LeaseManagementPartyRole.PrimaryTenant,
            EffectiveFrom = DateOnly.FromDateTime(now.AddDays(-1)),
            ChangeReason = "Realtime authorization test",
            CreatedAtUtc = now,
            CreatedByUser = user,
        };
        var access = new TenantUserAccess
        {
            PublicId = Guid.NewGuid(),
            PortfolioId = 1,
            AccessContext = context,
            ApplicationUser = user,
            LeaseManagementParty = party,
            GrantedAtUtc = now,
            GrantedByUser = user,
            Reason = "Realtime authorization test",
        };
        _context.Db.AddRange(session, access);
        return new SessionCoordinates(session.Id, user, context);
    }

    private SessionCoordinates AddBareContextSession(string key, DateTime now)
    {
        var user = AddUser(key, now);
        var context = new WorkspaceAccessContext
        {
            User = user,
            PortfolioId = 1,
            Status = WorkspaceAccessContextStatus.Active,
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
        _context.Db.Add(session);
        return new SessionCoordinates(session.Id, user, context);
    }

    private void EnsureRelationshipProjectionView()
    {
        _context.Db.Database.ExecuteSqlRaw("""
            CREATE VIEW "vw_effective_tenant_access" AS
            SELECT context."Id" AS "AccessContextId", context."UserId", context."PortfolioId",
                   context."AccessRevision", access."Id" AS "TenantUserAccessId",
                   party."Id" AS "LeaseManagementPartyId", party."TenantId",
                   party."LeaseManagementId", NULL AS "TenantAccountId",
                   relationship."PropertyId", relationship."UnitId"
            FROM "WorkspaceAccessContexts" context
            JOIN "TenantUserAccesses" access
              ON access."AccessContextId" = context."Id"
             AND access."ApplicationUserId" = context."UserId"
             AND access."PortfolioId" = context."PortfolioId"
            JOIN "LeaseManagementParties" party
              ON party."Id" = access."LeaseManagementPartyId"
             AND party."PortfolioId" = access."PortfolioId"
            JOIN "LeaseManagements" relationship
              ON relationship."Id" = party."LeaseManagementId"
             AND relationship."PortfolioId" = party."PortfolioId"
            WHERE context."Status" = 'Active'
              AND context."SuspendedAtUtc" IS NULL
              AND context."RevokedAtUtc" IS NULL
              AND access."RevokedAtUtc" IS NULL
              AND party."EffectiveFrom" <= date('now')
              AND (party."EffectiveThrough" IS NULL OR party."EffectiveThrough" >= date('now'));
            """);
    }

    private ApplicationUser AddUser(string key, DateTime now)
    {
        var email = $"realtime-{key}@example.test";
        return new ApplicationUser
        {
            UserName = email,
            NormalizedUserName = email.ToUpperInvariant(),
            Email = email,
            NormalizedEmail = email.ToUpperInvariant(),
            DisplayName = key,
            SecurityStamp = Guid.NewGuid().ToString("N"),
            ConcurrencyStamp = Guid.NewGuid().ToString("N"),
            CreatedAt = now,
        };
    }

    public void Dispose() => _context.Dispose();

    private sealed record SessionCoordinates(Guid SessionId, ApplicationUser User, WorkspaceAccessContext Context);
}
