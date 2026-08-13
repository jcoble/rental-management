using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using RentalCommand.Core.Atomic;
using RentalCommand.Core.Authorization;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Interfaces;
using RentalCommand.Core.Leasing;
using RentalCommand.Data;
using RentalCommand.Data.Atomic;
using RentalCommand.Data.Leasing;
using RentalCommand.TestCommon;

namespace RentalCommand.IntegrationTests;

[Collection(RoleAuthorityPostgreSqlCollection.Name)]
public sealed class PrepareMoveInPostgreSqlTests : IAsyncLifetime
{
    private static readonly AtomicJsonResultCodec<PrepareMoveInResult> Codec =
        new("lease-management.prepare-move-in.v1");

    private readonly MigratedPostgreSqlFixture _fixture;
    private MigratedPostgreSqlTestContext _context = null!;
    private ServiceProvider _services = null!;
    private IServiceScope _serviceScope = null!;

    public PrepareMoveInPostgreSqlTests(MigratedPostgreSqlFixture fixture)
    {
        _fixture = fixture;
    }

    public async Task InitializeAsync()
    {
        _context = await _fixture.CreateContextAsync();
        var services = new ServiceCollection();
        services.AddSingleton(TimeProvider.System);
        services.AddScoped<ICurrentActor, TestActor>();
        services.AddAtomicPersistenceKernel();
        services.AddAtomicCommandHandler<PrepareMoveInCommand,
            PrepareMoveInResult, PrepareMoveInHandler>();
        services.AddDbContext<RentalCommandDbContext>((provider, builder) =>
            builder.UseNpgsql(_context.ConnectionString)
                .UseAtomicPersistenceKernel(provider));
        _services = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true,
        });
        _serviceScope = _services.CreateScope();
    }

    public async Task DisposeAsync()
    {
        _serviceScope?.Dispose();
        if (_services is not null) await _services.DisposeAsync();
        if (_context is not null) await _context.DisposeAsync();
    }

    [Fact]
    public async Task ManualPrepareMoveIn_RejectsStaleUnitWithPreparedRelationship()
    {
        var scenario = await SeedScenarioAsync();
        var staleRelationship = Relationship(
            scenario.PropertyId,
            scenario.TargetUnitId,
            scenario.ActorUserId,
            "stale-unit",
            DateTime.UtcNow);
        _context.Db.LeaseManagements.Add(staleRelationship);
        await _context.Db.SaveChangesAsync();

        var command = ManualCommand(
            scenario,
            [
                new PrepareMoveInParty(
                    null,
                    new PrepareMoveInNewTenant(
                        "Fresh",
                        "Resident",
                        "fresh-unit@example.test",
                        null,
                        null),
                    LeaseManagementPartyRole.PrimaryTenant,
                    false,
                    "Manual lease creation",
                    true,
                    1,
                    true),
            ],
            "stale-unit");

        var before = await CountRelationshipsForUnitAsync(scenario.TargetUnitId);
        var result = await Atomic.ExecuteAsync(Identity(command), command, Codec);

        result.Value.Outcome.Should().Be(PrepareMoveInOutcome.UnitUnavailable);
        result.Value.LeaseManagementId.Should().Be(0);
        _context.Db.ChangeTracker.Clear();
        (await CountRelationshipsForUnitAsync(scenario.TargetUnitId)).Should().Be(before);
    }

    [Fact]
    public async Task ManualPrepareMoveIn_RejectsExistingTenantWithActiveNonGuarantorRelationship()
    {
        var scenario = await SeedScenarioAsync();
        var existingTenant = Tenant("Active", "Resident", "active-tenant@example.test", DateTime.UtcNow);
        var occupiedUnit = Unit(scenario.PropertyId, "occupied", DateTime.UtcNow);
        _context.Db.AddRange(existingTenant, occupiedUnit);
        await _context.Db.SaveChangesAsync();

        var staleRelationship = Relationship(
            scenario.PropertyId,
            occupiedUnit.Id,
            scenario.ActorUserId,
            "stale-tenant",
            DateTime.UtcNow);
        _context.Db.LeaseManagements.Add(staleRelationship);
        await _context.Db.SaveChangesAsync();
        _context.Db.LeaseManagementParties.Add(new LeaseManagementParty
        {
            PortfolioId = scenario.PortfolioId,
            LeaseManagementId = staleRelationship.Id,
            TenantId = existingTenant.Id,
            Role = LeaseManagementPartyRole.PrimaryTenant,
            EffectiveFrom = scenario.BusinessDate.AddDays(-30),
            ChangeReason = "Existing active relationship",
            CreatedAtUtc = DateTime.UtcNow,
            CreatedByUserId = scenario.ActorUserId,
        });
        await _context.Db.SaveChangesAsync();

        var command = ManualCommand(
            scenario,
            [
                new PrepareMoveInParty(
                    existingTenant.Id,
                    null,
                    LeaseManagementPartyRole.PrimaryTenant,
                    false,
                    "Manual lease creation",
                    true,
                    1,
                    true),
            ],
            "stale-tenant");

        var before = await CountRelationshipsForUnitAsync(scenario.TargetUnitId);
        var result = await Atomic.ExecuteAsync(Identity(command), command, Codec);

        result.Value.Outcome.Should().Be(PrepareMoveInOutcome.InvalidParties);
        result.Value.LeaseManagementId.Should().Be(0);
        _context.Db.ChangeTracker.Clear();
        (await CountRelationshipsForUnitAsync(scenario.TargetUnitId)).Should().Be(before);
    }

    private async Task<Scenario> SeedScenarioAsync()
    {
        var now = DateTime.UtcNow;
        var businessDate = DateOnly.FromDateTime(now);
        var actor = await _context.Db.Users.SingleAsync(user => user.Id == 1);
        var property = new Property
        {
            PortfolioId = 1,
            Name = $"Prepare Move-In PostgreSQL {Guid.NewGuid():N}",
            AddressLine1 = "754 Review Way",
            City = "Columbus",
            State = "OH",
            PostalCode = "43215",
            CreatedAt = now,
            UpdatedAt = now,
        };
        var unit = new Unit
        {
            PortfolioId = 1,
            Property = property,
            UnitNumber = $"target-{Guid.NewGuid():N}"[..12],
            CreatedAt = now,
            UpdatedAt = now,
        };
        _context.Db.AddRange(property, unit);
        await _context.Db.SaveChangesAsync();

        var context = new WorkspaceAccessContext
        {
            UserId = actor.Id,
            PortfolioId = 1,
            Status = WorkspaceAccessContextStatus.Active,
            LastAuthorizedExperience = WorkspaceExperience.Management,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
        };
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
            User = actor,
            ActiveAccessContext = context,
            Status = AuthSessionStatus.Active,
            CreatedAtUtc = now,
            LastSeenAtUtc = now,
            ExpiresAtUtc = now.AddHours(1),
        };
        _context.Db.AddRange(assignment, session);
        await _context.Db.SaveChangesAsync();

        return new Scenario(
            1,
            actor.Id,
            session.Id,
            context.Id,
            context.AccessRevision,
            businessDate,
            property.Id,
            unit.Id);
    }

    private PrepareMoveInCommand ManualCommand(
        Scenario scenario,
        IReadOnlyList<PrepareMoveInParty> parties,
        string suffix) => new(
        scenario.PortfolioId,
        null,
        scenario.TargetUnitId,
        scenario.ActorUserId,
        scenario.SessionId,
        scenario.AccessContextId,
        scenario.AccessRevision,
        DateTime.UtcNow.AddDays(15),
        scenario.BusinessDate,
        parties,
        null,
        LeaseAgreementTermType.FixedTerm,
        scenario.BusinessDate,
        scenario.BusinessDate.AddYears(1).AddDays(-1),
        1200m,
        1,
        1200m,
        50m,
        5,
        1,
        "{}",
        false,
        null,
        null,
        null,
        $"prepare-move-in:{suffix}:{Guid.NewGuid():N}");

    private async Task<int> CountRelationshipsForUnitAsync(int unitId) =>
        await _context.Db.LeaseManagements.AsNoTracking()
            .CountAsync(relationship => relationship.UnitId == unitId);

    private IAtomicUnitOfWork Atomic =>
        _serviceScope.ServiceProvider.GetRequiredService<IAtomicUnitOfWork>();

    private static AtomicCommandIdentity Identity(PrepareMoveInCommand command) => new(
        "lease-management.prepare-move-in",
        $"{command.PortfolioId}:{command.UnitId}:{command.DeliveryIdempotencyKey}");

    private static Unit Unit(int propertyId, string number, DateTime now) => new()
    {
        PortfolioId = 1,
        PropertyId = propertyId,
        UnitNumber = number,
        CreatedAt = now,
        UpdatedAt = now,
    };

    private static LeaseManagement Relationship(
        int propertyId,
        int unitId,
        int actorUserId,
        string suffix,
        DateTime now) => new()
    {
        PublicId = Guid.NewGuid(),
        PortfolioId = 1,
        PropertyId = propertyId,
        UnitId = unitId,
        RelationshipNumber = $"LM-PREPARE-{suffix}-{Guid.NewGuid():N}"[..32],
        PlannedPossessionAtUtc = now.AddDays(10),
        CreatedAtUtc = now,
        CreatedByUserId = actorUserId,
        UpdatedAtUtc = now,
        RowVersion = Guid.NewGuid(),
    };

    private static Tenant Tenant(string firstName, string lastName, string email, DateTime now) => new()
    {
        PortfolioId = 1,
        FirstName = firstName,
        LastName = lastName,
        Email = email,
        CreatedAt = now,
        UpdatedAt = now,
    };

    private sealed class TestActor : ICurrentActor
    {
        public int? UserId => 1;
        public string? ActorLabel => "integration:prepare-move-in-postgresql";
        public string? IpAddress => "127.0.0.1";
    }

    private sealed record Scenario(
        int PortfolioId,
        int ActorUserId,
        Guid SessionId,
        int AccessContextId,
        long AccessRevision,
        DateOnly BusinessDate,
        int PropertyId,
        int TargetUnitId);
}
