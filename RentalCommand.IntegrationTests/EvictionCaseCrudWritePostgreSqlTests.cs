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
using RentalCommand.Core.Operations;
using RentalCommand.Data;
using RentalCommand.Data.Atomic;
using RentalCommand.Data.Operations;
using RentalCommand.TestCommon;

namespace RentalCommand.IntegrationTests;

[Collection(RoleAuthorityPostgreSqlCollection.Name)]
public sealed class EvictionCaseCrudWritePostgreSqlTests : IAsyncLifetime
{
    private static readonly DateTime BusinessNow =
        new(2027, 4, 5, 16, 0, 0, DateTimeKind.Utc);
    private readonly MigratedPostgreSqlFixture _fixture;
    private MigratedPostgreSqlTestContext _context = null!;

    public EvictionCaseCrudWritePostgreSqlTests(MigratedPostgreSqlFixture fixture) =>
        _fixture = fixture;

    public async Task InitializeAsync() =>
        _context = await _fixture.CreateContextAsync();

    public async Task DisposeAsync() =>
        await _context.DisposeAsync();

    [Fact]
    public void CreateCommand_PreservesFrozenLegacyFingerprint()
    {
        var command = new CreateEvictionCaseCommand(
            1,
            new StaffOperationActor(
                7, Guid.Parse("11111111-1111-1111-1111-111111111111"), 9, 3),
            42, 43, [44, 45], EvictionCaseStatus.Filed,
            new DateTime(2027, 4, 1, 0, 0, 0, DateTimeKind.Utc),
            new DateTime(2027, 4, 20, 0, 0, 0, DateTimeKind.Utc),
            "Franklin County Municipal Court", "2027-CVG-42", "Frozen fingerprint",
            BusinessNow, "frozen-eviction-create");

        AtomicCommandFingerprint.Create(command).Should().Be(
            "49476f30cb801e0dba91f58e3ad55fac968780ebf1f92149a391dcd3491f8165");
    }

    [Fact]
    public async Task CreateUpdateAndEvent_PreserveReplayResultAuditOutboxAndStaleAuthorization()
    {
        var scope = await SeedScopeAsync(BusinessNow.AddDays(-1));
        var lease = await SeedLeaseAsync(BusinessNow);
        var request = new CreateEvictionCaseRequest
        {
            LeaseManagementId = lease.Management.Id,
            RespondentLeaseManagementPartyIds = [lease.Party.Id],
            Status = EvictionCaseStatus.Filed,
            FiledOnDate = BusinessNow.AddDays(-2),
            CourtName = "Franklin County Municipal Court",
            CaseNumber = "2027-CVG-100",
            Notes = "Executor eviction case",
        };
        const string createKey = "family4-eviction-create";

        EvictionCaseResponse created;
        await using (var services = BuildServices(new FixedTimeProvider(BusinessNow)))
        {
            await using (var serviceScope = services.CreateAsyncScope())
            {
                var service = serviceScope.ServiceProvider.GetRequiredService<EvictionCaseService>();
                created = (await service.CreateAuthorizedAsync(scope, request, createKey))!;
            }

            await using (var serviceScope = services.CreateAsyncScope())
            {
                var service = serviceScope.ServiceProvider.GetRequiredService<EvictionCaseService>();
                var replay = await service.CreateAuthorizedAsync(scope, request, createKey);
                replay.Should().BeEquivalentTo(created);
            }

            await using (var serviceScope = services.CreateAsyncScope())
            {
                var service = serviceScope.ServiceProvider.GetRequiredService<EvictionCaseService>();
                var updated = await service.UpdateAuthorizedAsync(scope, created.Id,
                    new UpdateEvictionCaseRequest
                    {
                        Status = EvictionCaseStatus.HearingScheduled,
                        HearingDate = BusinessNow.AddDays(14),
                        Notes = "Hearing scheduled",
                    }, "family4-eviction-update");
                updated.Should().NotBeNull();
                updated!.Status.Should().Be(EvictionCaseStatus.HearingScheduled);
            }

            await using (var serviceScope = services.CreateAsyncScope())
            {
                var service = serviceScope.ServiceProvider.GetRequiredService<EvictionCaseService>();
                var withEvent = await service.AddEventAuthorizedAsync(scope, created.Id,
                    new CreateEvictionCaseEventRequest
                    {
                        EventType = EvictionEventType.Judgment,
                        EventDate = BusinessNow.AddDays(20),
                        Notes = "Judgment entered",
                    }, "family4-eviction-event");
                withEvent.Should().NotBeNull();
                withEvent!.Status.Should().Be(EvictionCaseStatus.Judgment);
                withEvent.Events.Should().ContainSingle(row => row.EventType == EvictionEventType.Judgment);
            }
        }

        _context.Db.ChangeTracker.Clear();
        var entity = await _context.Db.EvictionCases.AsNoTracking()
            .SingleAsync(row => row.Id == created.Id);
        entity.Status.Should().Be(EvictionCaseStatus.Judgment);
        (await _context.Db.EvictionCaseEvents.AsNoTracking()
            .CountAsync(row => row.EvictionCaseId == created.Id)).Should().Be(2);

        var identity = new AtomicCommandIdentity(
            "eviction-case.create", EvictionCrudWriteSupport.IdempotencyKey(createKey));
        var receipt = await _context.Db.AtomicCommandReceipts.AsNoTracking().SingleAsync(row =>
            row.CommandType == identity.CommandType && row.IdempotencyKey == identity.IdempotencyKey);
        var frozenLegacyShape = new CreateEvictionCaseCommand(
            scope.PortfolioId, Actor(scope), request.LeaseManagementId, null,
            request.RespondentLeaseManagementPartyIds.ToArray(), request.Status,
            request.FiledOnDate, null, request.CourtName, request.CaseNumber, request.Notes,
            BusinessNow, createKey);
        receipt.RequestFingerprint.Should().Be(AtomicCommandFingerprint.Create(frozenLegacyShape));
        receipt.ResultContract.Should().Be(EvictionCrudWriteSupport.ResultContract);
        JsonSerializer.Deserialize<OperationMutationResult>(receipt.ResultJson!)!.EntityId
            .Should().Be(created.Id);

        var audit = await _context.Db.AtomicAuditLogs.AsNoTracking().SingleAsync(row =>
            row.CommandType == identity.CommandType
            && row.CommandIdempotencyKey == identity.IdempotencyKey
            && row.EntityType == nameof(EvictionCase));
        audit.Timestamp.Should().Be(BusinessNow);
        audit.Timestamp.Should().NotBeCloseTo(DateTime.UtcNow, TimeSpan.FromHours(1));
        (await _context.Db.OutboxMessages.AsNoTracking().CountAsync(row =>
            row.IdempotencyKey == $"eviction-case-create:{createKey}")).Should().Be(1);
        (await _context.Db.OutboxMessages.AsNoTracking().CountAsync(row =>
            row.IdempotencyKey.StartsWith($"eviction-event-create:{createKey}:"))).Should().Be(1);
        (await _context.Db.OutboxMessages.AsNoTracking().CountAsync(row =>
            row.IdempotencyKey == "eviction-case-update:family4-eviction-update"
            || row.IdempotencyKey == "eviction-event-create:family4-eviction-event"
            || row.IdempotencyKey == "eviction-case-event:family4-eviction-event")).Should().Be(3);

        var session = await _context.Db.AuthSessions.SingleAsync(row => row.Id == scope.SessionId);
        session.Status = AuthSessionStatus.Revoked;
        session.RevokedAtUtc = BusinessNow.AddMinutes(1);
        await _context.Db.SaveChangesAsync();
        _context.Db.ChangeTracker.Clear();

        await using var staleServices = BuildServices(new FixedTimeProvider(BusinessNow.AddMinutes(2)));
        await using var staleScope = staleServices.CreateAsyncScope();
        Func<Task> staleReplay = () => staleScope.ServiceProvider
            .GetRequiredService<EvictionCaseService>()
            .CreateAuthorizedAsync(scope, request, createKey);
        await staleReplay.Should().ThrowAsync<UnauthorizedAccessException>()
            .WithMessage("The active assignment cannot manage this eviction case.");
    }

    [Fact]
    public async Task Delete_ReplaysExactlyAndPreservesLegacySoftDeleteOutbox()
    {
        var scope = await SeedScopeAsync(BusinessNow.AddDays(-1));
        var lease = await SeedLeaseAsync(BusinessNow);
        await using var services = BuildServices(new FixedTimeProvider(BusinessNow));
        EvictionCaseResponse created;
        await using (var serviceScope = services.CreateAsyncScope())
        {
            var service = serviceScope.ServiceProvider.GetRequiredService<EvictionCaseService>();
            created = (await service.CreateAuthorizedAsync(scope, new CreateEvictionCaseRequest
            {
                LeaseManagementId = lease.Management.Id,
                RespondentLeaseManagementPartyIds = [lease.Party.Id],
                Status = EvictionCaseStatus.Draft,
            }, "family4-eviction-delete-seed"))!;
        }

        await using (var serviceScope = services.CreateAsyncScope())
        {
            var service = serviceScope.ServiceProvider.GetRequiredService<EvictionCaseService>();
            (await service.DeleteAuthorizedAsync(
                scope, created.Id, "family4-eviction-delete")).Should().BeTrue();
        }

        await using (var serviceScope = services.CreateAsyncScope())
        {
            var service = serviceScope.ServiceProvider.GetRequiredService<EvictionCaseService>();
            (await service.DeleteAuthorizedAsync(
                scope, created.Id, "family4-eviction-delete")).Should().BeTrue();
        }

        _context.Db.ChangeTracker.Clear();
        var deleted = await _context.Db.EvictionCases.IgnoreQueryFilters().AsNoTracking()
            .SingleAsync(row => row.Id == created.Id);
        deleted.DeletedAt.Should().Be(BusinessNow);
        var outbox = await _context.Db.OutboxMessages.AsNoTracking().SingleAsync(row =>
            row.IdempotencyKey == "eviction-case-delete:family4-eviction-delete");
        using var payload = JsonDocument.Parse(outbox.Payload);
        payload.RootElement.GetProperty("operation").GetString().Should().Be("delete");
    }

    [Fact]
    public void WritesUseAuthorizationScopeOnly()
    {
        var actor = new StaffOperationActor(1, Guid.NewGuid(), 2, 3);
        var create = new CreateEvictionCaseCommand(
            1, actor, 4, null, [5], EvictionCaseStatus.Draft,
            null, null, null, null, null, BusinessNow, "create-lock-plan");
        var update = new UpdateEvictionCaseCommand(
            1, actor, 6, EvictionCaseStatus.Filed, null, null, null,
            null, null, null, null, BusinessNow, "update-lock-plan");
        var addEvent = new AddEvictionCaseEventCommand(
            1, actor, 6, EvictionEventType.Note, BusinessNow, null,
            BusinessNow, "event-lock-plan");
        var delete = new DeleteEvictionCaseCommand(
            1, actor, 6, BusinessNow, "delete-lock-plan");

        AssertPlan(EvictionCrudWriteSupport.Write(create, Applied, Authorized));
        AssertPlan(EvictionCrudWriteSupport.Write(update, Applied, Authorized));
        AssertPlan(EvictionCrudWriteSupport.Write(addEvent, Applied, Authorized));
        AssertPlan(EvictionCrudWriteSupport.Write(delete, Applied, Authorized));

        static void AssertPlan<TCommand>(TransactionalWrite<TCommand, OperationMutationResult> write)
            where TCommand : notnull, IAtomicCommandData
        {
            write.LockPlan.Protocol.Should().Be(WriteLockProtocol.AuthorizationScope);
            write.LockPlan.Locks.Select(row => row.LockNamespace)
                .Should().Equal("AuthSession", "WorkspaceAccessContext", "Portfolio");
        }

        static Task<OperationMutationResult> Applied<T>(
            T _, IAtomicCommandContext __, CancellationToken ___) where T : IAtomicCommandData =>
            Task.FromResult(new OperationMutationResult(OperationMutationOutcome.Applied, 1));
        static Task Authorized<T>(
            T _, IAtomicCommandContext __, CancellationToken ___) where T : IAtomicCommandData =>
            Task.CompletedTask;
    }

    private async Task<WorkspaceReadScope> SeedScopeAsync(DateTime now)
    {
        var user = await _context.Db.Users.SingleAsync(row => row.Id == 1);
        var accessContext = new WorkspaceAccessContext
        {
            UserId = user.Id, PortfolioId = 1, Status = WorkspaceAccessContextStatus.Active,
            CreatedAtUtc = now, UpdatedAtUtc = now,
        };
        var membership = new WorkspaceMembership
        {
            AccessContext = accessContext, PortfolioId = 1,
            Status = WorkspaceMembershipStatus.Active,
            DefaultExperience = WorkspaceExperience.Management,
            EffectiveFromUtc = now.AddMinutes(-5), CreatedAtUtc = now, UpdatedAtUtc = now,
        };
        var assignment = new MembershipRoleAssignment
        {
            WorkspaceMembership = membership, PortfolioId = 1,
            RoleProfileId = AccessCatalog.Roles.Single(role =>
                role.Key == RoleProfileKeys.WorkspaceAdministrator).Id,
            Status = MembershipRoleAssignmentStatus.Active,
            ScopeKind = MembershipRoleAssignmentScopeKind.AllProperties,
            EffectiveFromUtc = now.AddMinutes(-5), CreatedAtUtc = now, UpdatedAtUtc = now,
        };
        var session = new AuthSession
        {
            Id = Guid.NewGuid(), UserId = user.Id, ActiveAccessContext = accessContext,
            Status = AuthSessionStatus.Active, CreatedAtUtc = now, LastSeenAtUtc = now,
            ExpiresAtUtc = now.AddDays(30),
        };
        _context.Db.AddRange(accessContext, membership, assignment, session);
        await _context.Db.SaveChangesAsync();
        _context.Db.ChangeTracker.Clear();
        return new(1, user.Id, session.Id, accessContext.Id, accessContext.AccessRevision);
    }

    private async Task<(LeaseManagement Management, LeaseManagementParty Party)> SeedLeaseAsync(DateTime now)
    {
        var property = new Property
        {
            PortfolioId = 1, Name = $"Family 4 eviction {Guid.NewGuid():N}",
            AddressLine1 = "4 Executor Way", City = "Columbus", State = "OH",
            PostalCode = "43215", CreatedAt = now, UpdatedAt = now,
        };
        var unit = new Unit
        {
            PortfolioId = 1, Property = property, UnitNumber = "E-4",
            Bedrooms = 2, Bathrooms = 1, MarketRent = 1200,
            CreatedAt = now, UpdatedAt = now,
        };
        var tenant = new Tenant
        {
            PortfolioId = 1, FirstName = "Evan", LastName = "Executor",
            CreatedAt = now, UpdatedAt = now,
        };
        var management = new LeaseManagement
        {
            PortfolioId = 1, Property = property, Unit = unit,
            RelationshipNumber = $"EVICT-{Guid.NewGuid():N}"[..16],
            PlannedPossessionAtUtc = now.AddMonths(-2),
            PossessionGivenAtUtc = now.AddMonths(-2),
            CreatedAtUtc = now, UpdatedAtUtc = now, CreatedByUserId = 1,
            RowVersion = Guid.NewGuid(),
        };
        var party = new LeaseManagementParty
        {
            PortfolioId = 1, LeaseManagement = management, Tenant = tenant,
            Role = LeaseManagementPartyRole.PrimaryTenant,
            EffectiveFrom = DateOnly.FromDateTime(now.AddMonths(-2)),
            ChangeReason = "Eviction executor test", CreatedAtUtc = now, CreatedByUserId = 1,
        };
        _context.Db.AddRange(property, unit, tenant, management, party);
        await _context.Db.SaveChangesAsync();
        _context.Db.ChangeTracker.Clear();
        return (management, party);
    }

    private ServiceProvider BuildServices(TimeProvider timeProvider)
    {
        var services = new ServiceCollection();
        services.AddSingleton(timeProvider);
        services.AddSingleton(Mock.Of<IDataUpdateService>());
        services.AddScoped<ICurrentActor, TestActor>();
        services.AddAtomicPersistenceKernel();
        services.AddScoped<IRequestWriteExecutor, RequestWriteExecutor>();
        services.AddScoped<EvictionCaseService>();
        services.AddDbContext<RentalCommandDbContext>((provider, options) =>
            options.UseNpgsql(_context.ConnectionString).UseAtomicPersistenceKernel(provider));
        return services.BuildServiceProvider(
            new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
    }

    private static StaffOperationActor Actor(WorkspaceReadScope scope) => new(
        scope.UserId, scope.SessionId, scope.AccessContextId, scope.AccessRevision);

    private sealed class FixedTimeProvider(DateTime now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(now);
    }

    private sealed class TestActor : ICurrentActor
    {
        public int? UserId => null;
        public string? ActorLabel => "integration:eviction-crud";
        public string? IpAddress => "127.0.0.1";
    }
}
