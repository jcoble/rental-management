using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using RentalCommand.Api.DTOs;
using RentalCommand.Api.Services.Domain;
using RentalCommand.Core.Atomic;
using RentalCommand.Core.Authorization;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Interfaces;
using RentalCommand.Data;
using RentalCommand.Data.Atomic;
using RentalCommand.TestCommon;

namespace RentalCommand.IntegrationTests;

[Collection(RoleAuthorityPostgreSqlCollection2.Name)]
public sealed class PropertyUnitTenantCrudWritePostgreSqlTests : IAsyncLifetime
{
    private readonly MigratedPostgreSqlFixture _fixture;
    private MigratedPostgreSqlTestContext _context = null!;

    public PropertyUnitTenantCrudWritePostgreSqlTests(MigratedPostgreSqlFixture fixture) =>
        _fixture = fixture;

    public async Task InitializeAsync() =>
        _context = await _fixture.CreateContextAsync();

    public async Task DisposeAsync() =>
        await _context.DisposeAsync();

    [Fact]
    public async Task PropertySetup_PreservesFingerprint_ReplaysExactly_UsesSaveClock_AndRejectsStaleAuthorization()
    {
        var now = DateTime.UtcNow;
        var auditNow = now.AddHours(1);
        var scope = await SeedScopeAsync(now);
        var request = new SetupPropertyRequest
        {
            Property = new CreatePropertyRequest
            {
                Name = "Family 3 House",
                PropertyType = PropertyType.SingleFamily,
                RentalStructure = RentalStructure.SingleRental,
                AddressLine1 = "3 Executor Way",
                City = "Columbus",
                State = "OH",
                PostalCode = "43215",
            },
            Units =
            [
                new SetupUnitRequest
                {
                    UnitNumber = "Home",
                    Bedrooms = 3,
                    Bathrooms = 2,
                    MarketRent = 1800m,
                },
            ],
        };
        const string operationKey = "family3-property-setup-canary";

        PropertySetupResponse first;
        PropertySetupResponse replay;
        await using (var services = BuildServices(new FirstThenFixedTimeProvider(now, auditNow)))
        await using (var serviceScope = services.CreateAsyncScope())
        {
            var sut = serviceScope.ServiceProvider.GetRequiredService<PropertyService>();
            first = (await sut.SetupAsync(scope, request, operationKey))!;
            replay = (await sut.SetupAsync(scope, request, operationKey))!;
        }

        replay.Should().BeEquivalentTo(first);
        first.Units.Should().ContainSingle();
        _context.Db.ChangeTracker.Clear();
        var property = await _context.Db.Properties.AsNoTracking()
            .SingleAsync(row => row.Id == first.Property.Id);
        property.CreatedAt.Should().Be(now);
        property.UpdatedAt.Should().Be(now);

        var identity = new AtomicCommandIdentity(
            "rental.property.setup",
            $"{scope.PortfolioId}:{scope.AccessContextId}:Property:Setup:0:{operationKey}");
        var receipt = await _context.Db.AtomicCommandReceipts.AsNoTracking().SingleAsync(row =>
            row.CommandType == identity.CommandType && row.IdempotencyKey == identity.IdempotencyKey);
        var frozenLegacyShape = CoreCrudWriteSupport.Request(
            scope, AtomicCoreCrudMutationDomain.Property, AtomicCoreCrudMutationOperation.Setup,
            0, operationKey, request, createdAtUtc: now, changedAtUtc: now);
        receipt.RequestFingerprint.Should().Be(AtomicCommandFingerprint.Create(frozenLegacyShape));
        receipt.ResultContract.Should().Be(CoreCrudWriteSupport.ResultContract);

        var audit = await _context.Db.AtomicAuditLogs.AsNoTracking().SingleAsync(row =>
            row.CommandType == identity.CommandType
            && row.CommandIdempotencyKey == identity.IdempotencyKey
            && row.EntityType == nameof(Property));
        audit.Timestamp.Should().Be(auditNow);
        audit.Timestamp.Should().NotBe(property.CreatedAt);

        var session = await _context.Db.AuthSessions.SingleAsync(row => row.Id == scope.SessionId);
        session.Status = AuthSessionStatus.Revoked;
        session.RevokedAtUtc = now.AddMinutes(1);
        await _context.Db.SaveChangesAsync();
        _context.Db.ChangeTracker.Clear();

        await using var staleServices = BuildServices(new FixedTimeProvider(now.AddMinutes(2)));
        await using var staleScope = staleServices.CreateAsyncScope();
        Func<Task> staleReplay = () => staleScope.ServiceProvider
            .GetRequiredService<PropertyService>()
            .SetupAsync(scope, request, operationKey);
        await staleReplay.Should().ThrowAsync<UnauthorizedAccessException>()
            .WithMessage("Workspace access changed. Refresh and try again.");
        (await _context.Db.Properties.AsNoTracking().CountAsync(row => row.Id == property.Id))
            .Should().Be(1);
    }

    [Fact]
    public async Task UnitAndTenantCreates_UseExecutorContracts()
    {
        var now = DateTime.UtcNow;
        var scope = await SeedScopeAsync(now);
        var propertyId = await _context.Db.Properties.AsNoTracking()
            .Where(row => row.Name == "Family 3 authorization property")
            .Select(row => row.Id)
            .SingleAsync();

        await using var services = BuildServices(new FixedTimeProvider(now));
        await using var serviceScope = services.CreateAsyncScope();
        var unit = (await serviceScope.ServiceProvider.GetRequiredService<UnitService>().CreateAsync(
            scope,
            new CreateUnitRequest
            {
                PropertyId = propertyId,
                UnitNumber = "3A",
                Bedrooms = 2,
                Bathrooms = 1,
                MarketRent = 1400m,
            },
            "family3-unit-create"))!;
        var tenant = (await serviceScope.ServiceProvider.GetRequiredService<TenantService>()
            .CreateAuthorizedAsync(scope, new CreateTenantRequest
            {
                FirstName = "Terry",
                LastName = "Executor",
                Email = "terry-executor@example.test",
            }, "family3-tenant-create"))!;

        var unitReceipt = await _context.Db.AtomicCommandReceipts.AsNoTracking().SingleAsync(row =>
            row.CommandType == "rental.unit.create"
            && row.IdempotencyKey.EndsWith(":family3-unit-create"));
        unitReceipt.ResultContract.Should().Be(RentalCrudWriteSupport.ResultContract);
        JsonSerializer.Deserialize<AtomicRentalMutationResult>(unitReceipt.ResultJson!)!
            .EntityId.Should().Be(unit.Id);
        var tenantReceipt = await _context.Db.AtomicCommandReceipts.AsNoTracking().SingleAsync(row =>
            row.CommandType == "rental.tenant.create"
            && row.IdempotencyKey.EndsWith(":family3-tenant-create"));
        tenantReceipt.ResultContract.Should().Be(CoreCrudWriteSupport.ResultContract);
        JsonSerializer.Deserialize<AtomicCoreCrudMutationResult>(tenantReceipt.ResultJson!)!
            .EntityId.Should().Be(tenant.Id);

    }

    private async Task<WorkspaceReadScope> SeedScopeAsync(DateTime now)
    {
        var user = await _context.Db.Users.SingleAsync(row => row.Id == 1);
        var accessContext = new WorkspaceAccessContext
        {
            UserId = user.Id,
            PortfolioId = 1,
            Status = WorkspaceAccessContextStatus.Active,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
        };
        var membership = new WorkspaceMembership
        {
            AccessContext = accessContext,
            PortfolioId = 1,
            Status = WorkspaceMembershipStatus.Active,
            DefaultExperience = WorkspaceExperience.Management,
            EffectiveFromUtc = now.AddMinutes(-5),
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
            EffectiveFromUtc = now.AddMinutes(-5),
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
        };
        var session = new AuthSession
        {
            Id = Guid.NewGuid(),
            UserId = user.Id,
            ActiveAccessContext = accessContext,
            Status = AuthSessionStatus.Active,
            CreatedAtUtc = now,
            LastSeenAtUtc = now,
            ExpiresAtUtc = now.AddDays(30),
        };
        var property = new Property
        {
            PortfolioId = 1,
            Name = "Family 3 authorization property",
            AddressLine1 = "3 Authorization Way",
            City = "Columbus",
            State = "OH",
            PostalCode = "43215",
            CreatedAt = now,
            UpdatedAt = now,
        };
        _context.Db.AddRange(accessContext, membership, assignment, session, property);
        await _context.Db.SaveChangesAsync();
        _context.Db.ChangeTracker.Clear();
        return new WorkspaceReadScope(
            1, user.Id, session.Id, accessContext.Id, accessContext.AccessRevision);
    }

    private ServiceProvider BuildServices(TimeProvider timeProvider)
    {
        var services = new ServiceCollection();
        services.AddSingleton(timeProvider);
        services.AddSingleton(Mock.Of<IDataUpdateService>());
        services.AddSingleton(Mock.Of<IAuditTrailService>());
        services.AddScoped<ICurrentActor, TestActor>();
        services.AddAtomicPersistenceKernel();
        services.AddScoped<PropertyService>();
        services.AddScoped<UnitService>();
        services.AddScoped<TenantService>();
        services.AddDbContext<RentalCommandDbContext>((provider, options) =>
            options.UseNpgsql(_context.ConnectionString)
                .UseAtomicPersistenceKernel(provider));
        return services.BuildServiceProvider(
            new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
    }

    private sealed class FixedTimeProvider(DateTime now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(now);
    }

    private sealed class FirstThenFixedTimeProvider(DateTime first, DateTime subsequent)
        : TimeProvider
    {
        private int _calls;
        public override DateTimeOffset GetUtcNow() =>
            new(Interlocked.Increment(ref _calls) == 1 ? first : subsequent);
    }

    private sealed class TestActor : ICurrentActor
    {
        public int? UserId => null;
        public string? ActorLabel => "integration:property-unit-tenant-crud";
        public string? IpAddress => "127.0.0.1";
    }
}
