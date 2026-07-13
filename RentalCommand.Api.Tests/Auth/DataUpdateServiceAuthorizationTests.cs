using FluentAssertions;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;
using Moq;
using RentalCommand.Api.Data;
using RentalCommand.Api.Hubs;
using RentalCommand.Api.Services;
using RentalCommand.Core.Authorization;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.TestCommon;

namespace RentalCommand.Api.Tests.Auth;

public sealed class DataUpdateServiceAuthorizationTests : IDisposable
{
    private readonly List<string> _commands = [];
    private readonly SqliteTestContext _context;
    private readonly Mock<IClientProxy> _client = new();
    private readonly Mock<IHubClients> _clients = new();
    private readonly DataUpdateService _service;
    private IReadOnlyList<string> _deliveredGroups = [];

    public DataUpdateServiceAuthorizationTests()
    {
        _context = new SqliteTestContext(
            [new Domain.RecordingCommandInterceptor(_commands)]);
        var hub = new Mock<IHubContext<DataUpdateHub>>();
        hub.SetupGet(value => value.Clients).Returns(_clients.Object);
        _clients.Setup(value => value.Groups(It.IsAny<IReadOnlyList<string>>()))
            .Callback<IReadOnlyList<string>>(groups => _deliveredGroups = groups)
            .Returns(_client.Object);
        _client.Setup(value => value.SendCoreAsync(
                It.IsAny<string>(), It.IsAny<object?[]>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        _service = new DataUpdateService(
            _context.Db,
            hub.Object,
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
        var relationshipOnly = AddRelationshipSession("tenant", now);
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
        return new SessionCoordinates(session.Id);
    }

    private SessionCoordinates AddRelationshipSession(string key, DateTime now)
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
        return new SessionCoordinates(session.Id);
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

    private sealed record SessionCoordinates(Guid SessionId);
}
