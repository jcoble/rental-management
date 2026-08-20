using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using RentalCommand.Api.DTOs;
using RentalCommand.Api.Services.Domain;
using RentalCommand.Api.Writes;
using RentalCommand.Core.Atomic;
using RentalCommand.Core.Authorization;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Interfaces;
using RentalCommand.Data;
using RentalCommand.Data.Atomic;
using RentalCommand.TestCommon;

namespace RentalCommand.IntegrationTests;

[Collection(RoleAuthorityPostgreSqlCollection.Name)]
public sealed class OwnerVendorCrudWritePostgreSqlTests : IAsyncLifetime
{
    private readonly MigratedPostgreSqlFixture _fixture;
    private MigratedPostgreSqlTestContext _context = null!;

    public OwnerVendorCrudWritePostgreSqlTests(MigratedPostgreSqlFixture fixture) =>
        _fixture = fixture;

    public async Task InitializeAsync() =>
        _context = await _fixture.CreateContextAsync();

    public async Task DisposeAsync() =>
        await _context.DisposeAsync();

    [Fact]
    public async Task VendorCreate_PreservesLegacyRows_ReplaysExactly_AndRejectsStaleAuthorization()
    {
        var now = DateTime.UtcNow;
        var scope = await SeedAdministratorScopeAsync(now);
        var request = new CreateVendorRequest
        {
            Name = "Phase 3 Plumbing",
            ServiceType = "Plumbing",
            Email = "phase3-plumbing@example.test",
            Phone = "555-0133",
            Website = "https://phase3-plumbing.example.test",
            TaxId = "12-3456789",
            AddressLine1 = "33 Executor Way",
            City = "Columbus",
            State = "OH",
            PostalCode = "43215",
            Is1099Eligible = true,
            W9OnFile = true,
            Preferred = true,
            Notes = "Family 2 parity canary",
        };
        const string operationKey = "phase3-vendor-create-canary";

        VendorResponse first;
        VendorResponse replay;
        await using (var services = BuildServices(now))
        await using (var serviceScope = services.CreateAsyncScope())
        {
            var sut = serviceScope.ServiceProvider.GetRequiredService<VendorService>();
            first = (await sut.CreateAsync(scope, request, operationKey))!;
            replay = (await sut.CreateAsync(scope, request, operationKey))!;
        }

        replay.Should().BeEquivalentTo(first);
        _context.Db.ChangeTracker.Clear();
        var vendor = await _context.Db.Vendors.AsNoTracking().SingleAsync(row =>
            row.Id == first.Id && row.PortfolioId == scope.PortfolioId);
        vendor.Name.Should().Be(request.Name);
        vendor.CreatedAt.Should().Be(now);
        vendor.UpdatedAt.Should().Be(now);

        var identity = new AtomicCommandIdentity(
            "rental.vendor.create",
            $"{scope.PortfolioId}:{scope.AccessContextId}:Vendor:Create:0:{operationKey}");
        var receipt = await _context.Db.AtomicCommandReceipts.AsNoTracking().SingleAsync(row =>
            row.CommandType == identity.CommandType
            && row.IdempotencyKey == identity.IdempotencyKey);
        var legacyCommand = AtomicCoreCrudMutation.Command(
            scope,
            AtomicCoreCrudMutationDomain.Vendor,
            AtomicCoreCrudMutationOperation.Create,
            0,
            operationKey,
            request,
            createdAtUtc: now);
        receipt.RequestFingerprint.Should().Be(AtomicCommandFingerprint.Create(legacyCommand));
        receipt.ResultContract.Should().Be("rental.core-crud-mutation.v1");
        JsonSerializer.Deserialize<AtomicCoreCrudMutationResult>(receipt.ResultJson!)
            .Should().Be(new AtomicCoreCrudMutationResult(
                true, true, vendor.Id, JsonSerializer.Serialize(VendorResponse.FromEntity(vendor))));

        var audit = await _context.Db.AtomicAuditLogs.AsNoTracking().SingleAsync(row =>
            row.CommandType == identity.CommandType
            && row.CommandIdempotencyKey == identity.IdempotencyKey);
        audit.Should().BeEquivalentTo(new
        {
            PortfolioId = scope.PortfolioId,
            UserId = (int?)scope.UserId,
            EntityType = nameof(Vendor),
            EntityId = vendor.Id,
            Operation = AuditLogOperation.Created,
            ChangeReason = $"Vendor {vendor.Name} created",
            Timestamp = now,
            AttemptId = receipt.AttemptId,
        }, options => options.ExcludingMissingMembers());

        var outbox = await _context.Db.OutboxMessages.AsNoTracking().SingleAsync(row =>
            row.IdempotencyKey == operationKey + ":entity");
        outbox.Should().BeEquivalentTo(new
        {
            PortfolioId = (int?)scope.PortfolioId,
            MessageType = "data-update",
            AttemptCount = 0,
            CreatedAtUtc = now,
            NextAttemptAtUtc = now,
        }, options => options.ExcludingMissingMembers());
        using (var payload = JsonDocument.Parse(outbox.Payload))
        {
            payload.RootElement.GetProperty("entityType").GetString().Should().Be(nameof(Vendor));
            payload.RootElement.GetProperty("entityId").GetInt32().Should().Be(vendor.Id);
            payload.RootElement.GetProperty("operation").GetString().Should().Be("update");
            payload.RootElement.GetProperty("data").EnumerateObject().Should().BeEmpty();
        }

        var session = await _context.Db.AuthSessions.SingleAsync(row => row.Id == scope.SessionId);
        session.Status = AuthSessionStatus.Revoked;
        session.RevokedAtUtc = now.AddMinutes(1);
        await _context.Db.SaveChangesAsync();
        _context.Db.ChangeTracker.Clear();

        await using (var services = BuildServices(now.AddMinutes(2)))
        await using (var serviceScope = services.CreateAsyncScope())
        {
            var sut = serviceScope.ServiceProvider.GetRequiredService<VendorService>();
            Func<Task> staleReplay = () => sut.CreateAsync(scope, request, operationKey);
            await staleReplay.Should().ThrowAsync<UnauthorizedAccessException>()
                .WithMessage("Workspace access changed. Refresh and try again.");
        }

        (await _context.Db.Vendors.AsNoTracking().CountAsync(row => row.Id == vendor.Id))
            .Should().Be(1);
        (await _context.Db.AtomicAuditLogs.AsNoTracking().CountAsync(row =>
            row.CommandType == identity.CommandType
            && row.CommandIdempotencyKey == identity.IdempotencyKey)).Should().Be(1);
        (await _context.Db.OutboxMessages.AsNoTracking().CountAsync(row =>
            row.IdempotencyKey == operationKey + ":entity")).Should().Be(1);
    }

    private async Task<WorkspaceReadScope> SeedAdministratorScopeAsync(DateTime now)
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
            Name = "Family 2 authorization property",
            AddressLine1 = "2 Authorization Way",
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

    private ServiceProvider BuildServices(DateTime now)
    {
        var services = new ServiceCollection();
        services.AddSingleton<TimeProvider>(new FixedTimeProvider(now));
        services.AddSingleton(Mock.Of<IDataUpdateService>());
        services.AddScoped<ICurrentActor, TestActor>();
        services.AddAtomicPersistenceKernel();
        services.AddScoped<IRequestWriteExecutor, RequestWriteExecutor>();
        services.AddScoped<VendorService>();
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

    private sealed class TestActor : ICurrentActor
    {
        public int? UserId => null;
        public string? ActorLabel => "integration:owner-vendor-crud";
        public string? IpAddress => "127.0.0.1";
    }
}
