using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using RentalCommand.Core;
using RentalCommand.Core.Authorization;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Data;
using RentalCommand.Data.Authorization;
using Testcontainers.PostgreSql;
using Xunit;

namespace RentalCommand.IntegrationTests;

/// <summary>
/// Real-PostgreSQL proof for the non-shipping workspace authorization kernel. The tests use
/// EnsureCreated because this forward-only slice deliberately adds no compatibility migration; the
/// final destructive baseline will recreate the schema.
/// </summary>
public sealed class WorkspaceAuthorizationKernelTests : IAsyncLifetime
{
    private PostgreSqlContainer? _postgres;
    private bool _dockerAvailable;
    private string _connectionString = string.Empty;
    private readonly DateTime _now = new(2026, 7, 10, 16, 0, 0, DateTimeKind.Utc);

    private int _userId;
    private int _accessContextId;
    private int _portfolioId;
    private int _membershipId;
    private Guid _sessionId;
    private int _leasingPropertyId;
    private int _managerPropertyId;
    private int _otherWorkspacePropertyId;

    public async Task InitializeAsync()
    {
        try
        {
            _postgres = new PostgreSqlBuilder()
                .WithImage("postgres:16-alpine")
                .WithDatabase("rentalcommand_access")
                .WithUsername("postgres")
                .WithPassword("postgres")
                .Build();
            await _postgres.StartAsync();
            _dockerAvailable = true;
        }
        catch
        {
            _dockerAvailable = false;
            return;
        }

        _connectionString = _postgres.GetConnectionString();
        await using var db = NewContext();
        await db.Database.EnsureCreatedAsync();
        await SeedKernelAsync(db);
    }

    public async Task DisposeAsync()
    {
        if (_postgres is not null)
        {
            await _postgres.DisposeAsync();
        }
    }

    [SkippableFact]
    public async Task SameAssignmentMustSupplyCapabilityAndScope_DecoyDoesNotLeak()
    {
        SkipIfNoDocker();
        await using var db = NewContext();
        var active = await ResolveAsync(db, presentedRevision: 7);

        var visible = await db.Properties
            .AsNoTracking()
            .WhereAuthorized(db, active, CapabilityKeys.LeasingListingsManage, _now)
            .OrderBy(property => property.Id)
            .Select(property => property.Id)
            .ToListAsync();

        visible.Should().Equal(_leasingPropertyId);
        visible.Should().NotContain(_managerPropertyId,
            "the Property Manager assignment has scope there but not the requested leasing capability");
    }

    [SkippableFact]
    public async Task MultipleAssignmentsRemainIndependentlyScoped()
    {
        SkipIfNoDocker();
        await using var db = NewContext();
        var active = await ResolveAsync(db, presentedRevision: 7);

        var leasing = await db.Properties
            .WhereAuthorized(db, active, CapabilityKeys.LeasingApplicationsManage, _now)
            .Select(property => property.Id)
            .ToListAsync();
        var money = await db.Properties
            .WhereAuthorized(db, active, CapabilityKeys.MoneyPaymentsManage, _now)
            .Select(property => property.Id)
            .ToListAsync();

        leasing.Should().Equal(_leasingPropertyId);
        money.Should().Equal(_managerPropertyId);
    }

    [SkippableFact]
    public async Task PropertyManagerHasOperationalMoneyButNotAdministrativeAuthority()
    {
        SkipIfNoDocker();
        await using var db = NewContext();
        var active = await ResolveAsync(db, presentedRevision: 7);
        var evaluator = new WorkspaceAuthorizationEvaluator(db);

        (await evaluator.HasCapabilityAsync(
            active, CapabilityKeys.MoneyPaymentsManage, _managerPropertyId, _now)).Should().BeTrue();
        (await evaluator.HasCapabilityAsync(
            active, CapabilityKeys.MoneyReconciliationOperate, _managerPropertyId, _now)).Should().BeTrue();
        (await evaluator.HasCapabilityAsync(
            active, CapabilityKeys.ResponsibilityAssignExistingMember, _managerPropertyId, _now)).Should().BeTrue();

        (await evaluator.HasCapabilityAsync(
            active, CapabilityKeys.BankConnectionsManage, _managerPropertyId, _now)).Should().BeFalse();
        (await evaluator.HasCapabilityAsync(
            active, CapabilityKeys.PayoutsManage, _managerPropertyId, _now)).Should().BeFalse();
        (await evaluator.HasCapabilityAsync(
            active, CapabilityKeys.IntegrationsManage, _managerPropertyId, _now)).Should().BeFalse();
        (await evaluator.HasCapabilityAsync(
            active, CapabilityKeys.TeamManage, _managerPropertyId, _now)).Should().BeFalse();
    }

    [SkippableFact]
    public async Task StaleAccessRevisionIsRejectedWithCurrentRevision()
    {
        SkipIfNoDocker();
        await using var db = NewContext();
        var resolver = new ActiveAccessContextResolver(db);

        var act = async () => await resolver.ResolveAsync(
            _sessionId, _userId, _accessContextId, presentedAccessRevision: 6, _now);

        var exception = await act.Should().ThrowAsync<StaleAccessRevisionException>();
        exception.Which.PresentedRevision.Should().Be(6);
        exception.Which.CurrentRevision.Should().Be(7);
    }

    [SkippableFact]
    public void AuthorizationExistsPrecedesOrderingAndPagingInGeneratedSql()
    {
        SkipIfNoDocker();
        using var db = NewContext();
        var active = ActiveContext();

        var sql = db.Properties
            .AsNoTracking()
            .WhereAuthorized(db, active, CapabilityKeys.RentalsRead, _now)
            .OrderBy(property => property.Name)
            .ThenBy(property => property.Id)
            .Skip(20)
            .Take(20)
            .Select(property => new { property.Id, property.Name })
            .ToQueryString();

        var existsAt = sql.IndexOf("EXISTS", StringComparison.OrdinalIgnoreCase);
        var orderAt = sql.IndexOf("ORDER BY", StringComparison.OrdinalIgnoreCase);
        var limitAt = sql.IndexOf("LIMIT", StringComparison.OrdinalIgnoreCase);

        existsAt.Should().BeGreaterThanOrEqualTo(0);
        sql.Should().Contain("RoleProfileCapabilities");
        sql.Should().Contain("MembershipRoleAssignmentProperties");
        existsAt.Should().BeLessThan(orderAt,
            "authorization must be in the SQL WHERE clause before sort");
        orderAt.Should().BeLessThan(limitAt,
            "paging must apply only after the authorization predicate and stable ordering");
    }

    [SkippableFact]
    public async Task CompositeScopeForeignKeysRejectPropertyFromAnotherWorkspace()
    {
        SkipIfNoDocker();
        await using var db = NewContext();
        var assignmentId = await db.MembershipRoleAssignments
            .Where(assignment => assignment.WorkspaceMembershipId == _membershipId &&
                                 assignment.RoleProfileId == 3)
            .Select(assignment => assignment.Id)
            .SingleAsync();

        db.MembershipRoleAssignmentProperties.Add(new MembershipRoleAssignmentProperty
        {
            MembershipRoleAssignmentId = assignmentId,
            PropertyId = _otherWorkspacePropertyId,
            PortfolioId = await db.WorkspaceMemberships
                .Where(membership => membership.Id == _membershipId)
                .Select(membership => membership.PortfolioId)
                .SingleAsync(),
        });

        var act = async () => await db.SaveChangesAsync();
        await act.Should().ThrowAsync<DbUpdateException>();
    }

    [SkippableFact]
    public async Task InvalidEffectivePeriodIsRejectedByDatabaseConstraint()
    {
        SkipIfNoDocker();
        await using var db = NewContext();
        var portfolioId = await db.WorkspaceMemberships
            .Where(membership => membership.Id == _membershipId)
            .Select(membership => membership.PortfolioId)
            .SingleAsync();

        db.MembershipRoleAssignments.Add(new MembershipRoleAssignment
        {
            WorkspaceMembershipId = _membershipId,
            PortfolioId = portfolioId,
            RoleProfileId = 2,
            Status = MembershipRoleAssignmentStatus.Active,
            ScopeKind = MembershipRoleAssignmentScopeKind.SelectedProperties,
            EffectiveFromUtc = _now,
            EffectiveToUtc = _now.AddMinutes(-1),
            CreatedAtUtc = _now,
            UpdatedAtUtc = _now,
        });

        var act = async () => await db.SaveChangesAsync();
        await act.Should().ThrowAsync<DbUpdateException>();
    }

    [SkippableFact]
    public async Task SelectedPropertyCardinalityValidatorRejectsMismatchedScopeKinds()
    {
        SkipIfNoDocker();
        await using var db = NewContext();
        var leasingAssignmentId = await db.MembershipRoleAssignments
            .Where(assignment => assignment.WorkspaceMembershipId == _membershipId &&
                                 assignment.RoleProfileId == 3)
            .Select(assignment => assignment.Id)
            .SingleAsync();
        var validator = new MembershipAssignmentScopeValidator(db);

        await validator.ValidateAsync(
            leasingAssignmentId, MembershipRoleAssignmentScopeKind.SelectedProperties);

        var act = async () => await validator.ValidateAsync(
            leasingAssignmentId, MembershipRoleAssignmentScopeKind.AllProperties);
        await act.Should().ThrowAsync<DomainValidationException>();
    }

    private async Task SeedKernelAsync(RentalCommandDbContext db)
    {
        var user = new ApplicationUser
        {
            UserName = "multi@example.test",
            NormalizedUserName = "MULTI@EXAMPLE.TEST",
            Email = "multi@example.test",
            NormalizedEmail = "MULTI@EXAMPLE.TEST",
            DisplayName = "Multi Assignment",
            SecurityStamp = Guid.NewGuid().ToString("N"),
            ConcurrencyStamp = Guid.NewGuid().ToString("N"),
            CreatedAt = _now,
        };
        var workspace = Portfolio("Workspace A");
        var otherWorkspace = Portfolio("Workspace B");
        db.AddRange(user, workspace, otherWorkspace);
        await db.SaveChangesAsync();

        var leasingProperty = Property(workspace.Id, "Leasing Property");
        var managerProperty = Property(workspace.Id, "Manager Property");
        var outsideProperty = Property(otherWorkspace.Id, "Outside Property");
        db.AddRange(leasingProperty, managerProperty, outsideProperty);

        var accessContext = new WorkspaceAccessContext
        {
            UserId = user.Id,
            PortfolioId = workspace.Id,
            Status = WorkspaceAccessContextStatus.Active,
            AccessRevision = 7,
            LastAuthorizedExperience = WorkspaceExperience.Leasing,
            CreatedAtUtc = _now,
            UpdatedAtUtc = _now,
        };
        var membership = new WorkspaceMembership
        {
            AccessContext = accessContext,
            PortfolioId = workspace.Id,
            Status = WorkspaceMembershipStatus.Active,
            DefaultExperience = WorkspaceExperience.Management,
            EffectiveFromUtc = _now.AddDays(-1),
            CreatedAtUtc = _now,
            UpdatedAtUtc = _now,
        };
        db.Add(membership);
        await db.SaveChangesAsync();

        var leasingAssignment = Assignment(
            membership.Id, workspace.Id, roleProfileId: 3,
            MembershipRoleAssignmentScopeKind.SelectedProperties);
        leasingAssignment.SelectedProperties.Add(new MembershipRoleAssignmentProperty
        {
            Property = leasingProperty,
            PortfolioId = workspace.Id,
        });

        var managerAssignment = Assignment(
            membership.Id, workspace.Id, roleProfileId: 2,
            MembershipRoleAssignmentScopeKind.SelectedProperties);
        managerAssignment.SelectedProperties.Add(new MembershipRoleAssignmentProperty
        {
            Property = managerProperty,
            PortfolioId = workspace.Id,
        });

        var session = new AuthSession
        {
            Id = Guid.NewGuid(),
            UserId = user.Id,
            ActiveAccessContextId = accessContext.Id,
            Status = AuthSessionStatus.Active,
            CreatedAtUtc = _now.AddHours(-1),
            LastSeenAtUtc = _now,
            ExpiresAtUtc = _now.AddDays(1),
        };
        db.AddRange(leasingAssignment, managerAssignment, session);
        await db.SaveChangesAsync();

        _userId = user.Id;
        _accessContextId = accessContext.Id;
        _portfolioId = workspace.Id;
        _membershipId = membership.Id;
        _sessionId = session.Id;
        _leasingPropertyId = leasingProperty.Id;
        _managerPropertyId = managerProperty.Id;
        _otherWorkspacePropertyId = outsideProperty.Id;
    }

    private MembershipRoleAssignment Assignment(
        int membershipId,
        int portfolioId,
        int roleProfileId,
        MembershipRoleAssignmentScopeKind scopeKind) => new()
    {
        WorkspaceMembershipId = membershipId,
        PortfolioId = portfolioId,
        RoleProfileId = roleProfileId,
        Status = MembershipRoleAssignmentStatus.Active,
        ScopeKind = scopeKind,
        EffectiveFromUtc = _now.AddHours(-1),
        CreatedAtUtc = _now,
        UpdatedAtUtc = _now,
    };

    private Portfolio Portfolio(string name) => new()
    {
        Name = name,
        ManagementCompanyName = name,
        TimeZone = "UTC",
        CreatedAt = _now,
        UpdatedAt = _now,
    };

    private Property Property(int portfolioId, string name) => new()
    {
        PortfolioId = portfolioId,
        Name = name,
        AddressLine1 = "1 Main St",
        City = "Columbus",
        State = "OH",
        PostalCode = "43215",
        CreatedAt = _now,
        UpdatedAt = _now,
    };

    private ActiveAccessContext ActiveContext() => new(
        _sessionId,
        _userId,
        _accessContextId,
        PortfolioId: _portfolioId,
        AccessRevision: 7,
        LastAuthorizedExperience: WorkspaceExperience.Leasing,
        WorkspaceMembershipId: _membershipId,
        DefaultExperience: WorkspaceExperience.Management);

    private async Task<ActiveAccessContext> ResolveAsync(
        RentalCommandDbContext db,
        long presentedRevision) =>
        await new ActiveAccessContextResolver(db).ResolveAsync(
            _sessionId, _userId, _accessContextId, presentedRevision, _now);

    private RentalCommandDbContext NewContext() => new(
        new DbContextOptionsBuilder<RentalCommandDbContext>()
            .UseNpgsql(_connectionString)
            .Options);

    private void SkipIfNoDocker() =>
        Skip.IfNot(_dockerAvailable, "Docker is unavailable; workspace authorization kernel test skipped.");
}
