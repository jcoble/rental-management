using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using RentalCommand.Api.Services.Auditing;
using RentalCommand.Api.Services.Domain;
using RentalCommand.Core.Authorization;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Data;
using RentalCommand.TestCommon;

namespace RentalCommand.Api.Tests.Domain;

/// <summary>
/// Pins the enriched dashboard "Recent Activity" feed: every row must carry the touched entity's
/// <c>EntityId</c> (for deep-linking) and a human <c>Label</c> naming the specific record, and the
/// labels, authorization, ordering, and the row limit must be resolved by ONE translated audit
/// projection — never a materialize/ID-set/follow-up lookup (the hard data-access rule).
/// </summary>
public class DashboardRecentActivityTests : IDisposable
{
    private const int PortfolioId = 1;

    private readonly SqliteConnection _conn;
    private readonly List<string> _executedSql = [];
    private readonly RentalCommandDbContext _db;
    private readonly DashboardService _sut;
    private readonly WorkspaceReadScope _scope;

    public DashboardRecentActivityTests()
    {
        _conn = new SqliteConnection("DataSource=:memory:");
        _conn.Open();

        var options = new DbContextOptionsBuilder<RentalCommandDbContext>()
            .UseSqlite(_conn)
            .AddInterceptors(new RecordingCommandInterceptor(_executedSql))
            .Options;

        _db = new ReportsServiceTestDbContext(options);
        _db.Database.EnsureCreated();
        _db.Database.InstallCanonicalLeaseProjectionViewsForSqlite();

        _db.Portfolios.Add(new Portfolio
        {
            Id = PortfolioId,
            Name = "Test Portfolio",
            ManagementCompanyName = "Test Co",
            TimeZone = "UTC",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        _db.SaveChanges();

        _scope = SeedAdministratorScope();

        _sut = new DashboardService(_db, new AuditDescriber(), TimeProvider.System);
    }

    private WorkspaceReadScope SeedAdministratorScope()
    {
        var now = DateTime.UtcNow;
        var user = new ApplicationUser
        {
            UserName = "dashboard@example.test",
            NormalizedUserName = "DASHBOARD@EXAMPLE.TEST",
            Email = "dashboard@example.test",
            NormalizedEmail = "DASHBOARD@EXAMPLE.TEST",
            DisplayName = "Dashboard Test Administrator",
            SecurityStamp = Guid.NewGuid().ToString("N"),
            ConcurrencyStamp = Guid.NewGuid().ToString("N"),
            CreatedAt = now,
        };
        var accessContext = new WorkspaceAccessContext
        {
            User = user,
            PortfolioId = PortfolioId,
            Status = WorkspaceAccessContextStatus.Active,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
        };
        var membership = new WorkspaceMembership
        {
            AccessContext = accessContext,
            PortfolioId = PortfolioId,
            Status = WorkspaceMembershipStatus.Active,
            DefaultExperience = WorkspaceExperience.Management,
            EffectiveFromUtc = now.AddMinutes(-1),
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
        };
        var assignment = new MembershipRoleAssignment
        {
            WorkspaceMembership = membership,
            PortfolioId = PortfolioId,
            RoleProfileId = AccessCatalog.Roles.Single(role =>
                role.Key == RoleProfileKeys.WorkspaceAdministrator).Id,
            Status = MembershipRoleAssignmentStatus.Active,
            ScopeKind = MembershipRoleAssignmentScopeKind.AllProperties,
            EffectiveFromUtc = now.AddMinutes(-1),
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
        };
        var session = new AuthSession
        {
            Id = Guid.NewGuid(),
            User = user,
            ActiveAccessContext = accessContext,
            Status = AuthSessionStatus.Active,
            CreatedAtUtc = now,
            LastSeenAtUtc = now,
            ExpiresAtUtc = now.AddHours(1),
        };

        _db.AddRange(assignment, session);
        _db.SaveChanges();

        return new WorkspaceReadScope(
            PortfolioId, user.Id, session.Id, accessContext.Id, accessContext.AccessRevision);
    }

    public void Dispose()
    {
        _db.Dispose();
        _conn.Dispose();
    }

    [Fact]
    public async Task RecentActivity_NamesEachEntityAndCarriesEntityId()
    {
        var seeded = SeedActivityGraph();

        var dashboard = await _sut.GetDashboardAsync(_scope);

        dashboard.Should().NotBeNull();
        var byKey = dashboard!.RecentActivity.ToDictionary(r => (r.Type, r.EntityId));

        byKey[("Tenant", seeded.Tenant1.Id)].Label.Should().Be("Maria Tenant");
        byKey[("Tenant", seeded.Tenant2.Id)].Label.Should().Be("Liam Renter");
        byKey[("Tenant", seeded.Tenant3.Id)].Label.Should().Be("Noah Lessee");
        byKey[("Unit", seeded.Unit.Id)].Label.Should().Be("Maple · Unit 1A");
        byKey[("LeaseManagement", seeded.Relationship.Id)].Label.Should().Be("REL-1A");
        byKey[("LeaseManagement", seeded.Relationship.Id)].UnitId.Should().Be(seeded.Unit.Id);
        byKey[("WorkOrder", seeded.WorkOrder.Id)].Label.Should().Be("Fix sink");
        byKey[("WorkOrder", seeded.WorkOrder.Id)].UnitId.Should().Be(seeded.Unit.Id);
        byKey[("Property", seeded.Property.Id)].Label.Should().Be("Maple");
        byKey[("TenantAccount", seeded.Account.Id)].Label.Should().Be("TA-1A");
        byKey[("TenantAccount", seeded.Account.Id)].UnitId.Should().Be(seeded.Unit.Id);
        byKey[("Expense", seeded.Expense.Id)].Label.Should().Be("Plumbing parts");
        byKey[("Expense", seeded.Expense.Id)].UnitId.Should().Be(seeded.Unit.Id);

        // Every row still carries the touched entity's id so the web can deep-link to it.
        dashboard.RecentActivity.Should().OnlyContain(r => r.EntityId > 0);
    }

    [Fact]
    public async Task RecentActivity_ExcludesRowsWithoutAnAuthorizedPropertyPath()
    {
        SeedActivityGraph();

        var dashboard = await _sut.GetDashboardAsync(_scope);

        dashboard.Should().NotBeNull();
        dashboard!.RecentActivity.Should().NotContain(r => r.Type == "Conversation");
    }

    [Fact]
    public async Task RecentActivity_Uses_One_Authorized_Projection_With_No_Label_Followups()
    {
        SeedActivityGraph();

        _executedSql.Clear();
        await _sut.GetDashboardAsync(_scope);

        var auditQueries = _executedSql
            .Where(command => command.Contains("FROM \"AuditLogs\"", StringComparison.OrdinalIgnoreCase))
            .ToList();

        auditQueries.Should().ContainSingle(
            "recent activity plus every correlated label/Unit lookup must execute as one reader command");
        auditQueries[0].Should().Contain("AuthSessions");
        auditQueries[0].Should().Contain("CapabilityDefinitions");
        auditQueries[0].Should().Contain("ORDER BY");
        auditQueries[0].Should().Contain("LIMIT");
        auditQueries[0].Should().Contain("Tenants");
        auditQueries[0].Should().Contain("TenantAccounts");
    }

    [Fact]
    public async Task RecentActivity_Applies_SelectedProperty_Scope_Before_Take()
    {
        var authorized = SeedActivityGraph();
        var now = DateTime.UtcNow;
        var decoyProperty = Property("Out-of-scope decoy", now);
        var decoyUnit = new Unit
        {
            PortfolioId = PortfolioId,
            Property = decoyProperty,
            UnitNumber = "D1",
            MarketRent = 900m,
            CreatedAt = now,
            UpdatedAt = now,
        };
        var decoyWorkOrder = new WorkOrder
        {
            PortfolioId = PortfolioId,
            Property = decoyProperty,
            Unit = decoyUnit,
            Title = "Unauthorized newest activity",
            Description = "Must not appear",
            RequestedAt = now,
            UpdatedAt = now,
        };
        _db.AddRange(decoyProperty, decoyUnit, decoyWorkOrder);
        _db.SaveChanges();
        _db.AuditLogs.Add(Audit(
            nameof(WorkOrder), decoyWorkOrder.Id, AuditLogOperation.Created, now.AddHours(1), 0));

        var assignment = _db.MembershipRoleAssignments
            .Include(item => item.SelectedProperties)
            .Single(item => item.WorkspaceMembership!.AccessContextId == _scope.AccessContextId);
        assignment.ScopeKind = MembershipRoleAssignmentScopeKind.SelectedProperties;
        assignment.SelectedProperties.Add(new MembershipRoleAssignmentProperty
        {
            PortfolioId = PortfolioId,
            PropertyId = authorized.Property.Id,
        });
        _db.SaveChanges();

        var dashboard = await _sut.GetDashboardAsync(_scope);

        dashboard!.RecentActivity.Should().NotContain(row => row.EntityId == decoyWorkOrder.Id &&
            row.Type == nameof(WorkOrder));
        dashboard.RecentActivity.Should().Contain(row => row.EntityId == authorized.WorkOrder.Id &&
            row.Type == nameof(WorkOrder));
    }

    [Fact]
    public async Task RecentActivity_FailsClosed_For_Stale_Revision()
    {
        SeedActivityGraph();

        var dashboard = await _sut.GetDashboardAsync(
            _scope with { AccessRevision = _scope.AccessRevision + 1 });

        dashboard!.RecentActivity.Should().BeEmpty();
    }

    [Fact]
    public async Task RecentActivity_FailsClosed_For_Revoked_Session()
    {
        SeedActivityGraph();
        var session = _db.AuthSessions.Single(item => item.Id == _scope.SessionId);
        session.Status = AuthSessionStatus.Revoked;
        session.RevokedAtUtc = DateTime.UtcNow;
        _db.SaveChanges();

        var dashboard = await _sut.GetDashboardAsync(_scope);

        dashboard!.RecentActivity.Should().BeEmpty();
    }

    private SeededActivityGraph SeedActivityGraph()
    {
        var baseTime = new DateTime(2026, 1, 10, 12, 0, 0, DateTimeKind.Utc);

        var property = Property("Maple", baseTime);
        var unit = new Unit
        {
            PortfolioId = PortfolioId,
            Property = property,
            UnitNumber = "1A",
            MarketRent = 1200m,
            CreatedAt = baseTime,
            UpdatedAt = baseTime,
        };
        var tenant1 = Tenant("Maria", "Tenant", baseTime);
        var tenant2 = Tenant("Liam", "Renter", baseTime);
        var tenant3 = Tenant("Noah", "Lessee", baseTime);
        var actor = new ApplicationUser
        {
            UserName = "activity@example.test",
            NormalizedUserName = "ACTIVITY@EXAMPLE.TEST",
            Email = "activity@example.test",
            NormalizedEmail = "ACTIVITY@EXAMPLE.TEST",
            DisplayName = "Activity Actor",
        };
        var relationship = new LeaseManagement
        {
            PortfolioId = PortfolioId,
            Property = property,
            Unit = unit,
            RelationshipNumber = "REL-1A",
            EndingDisposition = LeaseManagementEndingDisposition.Undecided,
            CreatedAtUtc = baseTime,
            UpdatedAtUtc = baseTime,
            RowVersion = Guid.NewGuid(),
            CreatedByUser = actor,
        };
        var account = new TenantAccount
        {
            PortfolioId = PortfolioId,
            LeaseManagement = relationship,
            AccountNumber = "TA-1A",
            Currency = "USD",
            OpenedAtUtc = baseTime,
            CreatedAtUtc = baseTime,
            CreatedByUser = actor,
        };
        var workOrder = new WorkOrder
        {
            PortfolioId = PortfolioId,
            Property = property,
            Unit = unit,
            Title = "Fix sink",
            Description = "Leak under the kitchen sink",
            RequestedAt = baseTime,
            UpdatedAt = baseTime,
        };
        var expense = new Expense
        {
            PortfolioId = PortfolioId,
            Property = property,
            Unit = unit,
            Description = "Plumbing parts",
            Amount = 40m,
            IncurredAt = baseTime,
            CreatedAt = baseTime,
            UpdatedAt = baseTime,
        };

        relationship.Parties.AddRange(
        [
            Party(tenant1, LeaseManagementPartyRole.PrimaryTenant, relationship, actor, baseTime),
            Party(tenant2, LeaseManagementPartyRole.CoTenant, relationship, actor, baseTime),
            Party(tenant3, LeaseManagementPartyRole.Occupant, relationship, actor, baseTime),
        ]);

        _db.AddRange(property, unit, tenant1, tenant2, tenant3, actor, relationship, account, workOrder, expense);
        _db.SaveChanges();

        // Nine property-backed audit rows (<= the Take(10) cap), plus one workspace-global
        // Conversation row. The latter must fail closed because it has no authorized property path.
        _db.AuditLogs.AddRange(
            Audit("Tenant", tenant1.Id, AuditLogOperation.Created, baseTime, 1),
            Audit("Tenant", tenant2.Id, AuditLogOperation.Updated, baseTime, 2),
            Audit("Tenant", tenant3.Id, AuditLogOperation.Created, baseTime, 3),
            Audit("Unit", unit.Id, AuditLogOperation.Updated, baseTime, 4),
            Audit(nameof(LeaseManagement), relationship.Id, AuditLogOperation.Created, baseTime, 5),
            Audit("WorkOrder", workOrder.Id, AuditLogOperation.Created, baseTime, 6),
            Audit("Property", property.Id, AuditLogOperation.Updated, baseTime, 7),
            Audit(nameof(TenantAccount), account.Id, AuditLogOperation.Updated, baseTime, 8),
            Audit("Expense", expense.Id, AuditLogOperation.Updated, baseTime, 9),
            Audit("Conversation", 999, AuditLogOperation.Created, baseTime, 10));
        _db.SaveChanges();

        return new SeededActivityGraph(property, unit, tenant1, tenant2, tenant3, relationship, account, workOrder, expense);
    }

    private static LeaseManagementParty Party(
        Tenant tenant,
        LeaseManagementPartyRole role,
        LeaseManagement relationship,
        ApplicationUser actor,
        DateTime now) => new()
    {
        PortfolioId = PortfolioId,
        Tenant = tenant,
        LeaseManagement = relationship,
        Role = role,
        EffectiveFrom = DateOnly.FromDateTime(now.AddDays(-1)),
        ChangeReason = "Dashboard activity test",
        CreatedAtUtc = now,
        CreatedByUser = actor,
    };

    private Tenant Tenant(string first, string last, DateTime now) => new()
    {
        PortfolioId = PortfolioId,
        FirstName = first,
        LastName = last,
        CreatedAt = now,
        UpdatedAt = now,
    };

    private static Property Property(string name, DateTime now) => new()
    {
        PortfolioId = PortfolioId,
        Name = name,
        AddressLine1 = "1 Main",
        City = "Columbus",
        State = "OH",
        PostalCode = "43219",
        CreatedAt = now,
        UpdatedAt = now,
    };

    private static AuditLog Audit(string entityType, int entityId, AuditLogOperation op, DateTime baseTime, int minutesAgo)
        => new()
        {
            PortfolioId = PortfolioId,
            EntityType = entityType,
            EntityId = entityId,
            Operation = op,
            ActorLabel = "test",
            Timestamp = baseTime.AddMinutes(-minutesAgo),
        };

    private sealed record SeededActivityGraph(
        Property Property,
        Unit Unit,
        Tenant Tenant1,
        Tenant Tenant2,
        Tenant Tenant3,
        LeaseManagement Relationship,
        TenantAccount Account,
        WorkOrder WorkOrder,
        Expense Expense);
}
