using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
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
public sealed class RecurringMaintenanceCrudWritePostgreSqlTests : IAsyncLifetime
{
    private readonly MigratedPostgreSqlFixture _fixture;
    private MigratedPostgreSqlTestContext _context = null!;

    public RecurringMaintenanceCrudWritePostgreSqlTests(MigratedPostgreSqlFixture fixture) =>
        _fixture = fixture;

    public async Task InitializeAsync() =>
        _context = await _fixture.CreateContextAsync();

    public async Task DisposeAsync() =>
        await _context.DisposeAsync();

    [Fact]
    public void CreateRequest_PreservesFrozenLegacyFingerprint()
    {
        var command = RecurringMaintenanceCrudWriteSupport.Request(
            new WorkspaceReadScope(1, 7, Guid.Parse("11111111-1111-1111-1111-111111111111"), 9, 3),
            RecurringMaintenanceWriteOperation.Create,
            0,
            new CreateRecurringMaintenanceTaskRequest
            {
                PropertyId = 42,
                UnitId = 43,
                VendorId = 44,
                Title = "Frozen HVAC",
                Description = "Legacy fingerprint canary",
                Category = "HVAC",
                RecurrenceInterval = RecurrenceInterval.Quarterly,
                NextDueDate = new DateTime(2027, 1, 15, 0, 0, 0, DateTimeKind.Utc),
                ScheduledTime = new TimeOnly(8, 30),
                EstimatedCost = 120.5m,
                IsActive = true,
                Priority = WorkOrderPriority.High,
            },
            "frozen-recurring-create");

        AtomicCommandFingerprint.Create(command).Should().Be(
            "f8eebd38e4ad2dda025cbe7578fabed976f7ad949340ae8a1e8ef5693287e812");
    }

    [Fact]
    public async Task Create_PreservesLegacyRows_ReplaysExactly_UsesDatabaseAuditClock_AndRejectsStaleAuthorization()
    {
        var seededAt = DateTime.UtcNow.AddMinutes(-5);
        var scope = await SeedScopeAsync(seededAt);
        var property = await _context.Db.Properties.AsNoTracking().SingleAsync(row =>
            row.PortfolioId == scope.PortfolioId && row.Name == "Family 5a property");
        var fakeClock = new DateTime(2099, 8, 20, 12, 0, 0, DateTimeKind.Utc);
        var request = CreateRequest(property.Id, "Executor HVAC");
        const string operationKey = "phase3-recurring-create-canary";

        RecurringMaintenanceTaskResponse first;
        RecurringMaintenanceTaskResponse replay;
        await using (var services = BuildServices(new FixedTimeProvider(fakeClock)))
        await using (var serviceScope = services.CreateAsyncScope())
        {
            var sut = serviceScope.ServiceProvider.GetRequiredService<RecurringMaintenanceTaskService>();
            first = (await sut.CreateAuthorizedAsync(scope, request, operationKey))!;
            replay = (await sut.CreateAuthorizedAsync(scope, request, operationKey))!;
        }

        replay.Should().BeEquivalentTo(first);
        _context.Db.ChangeTracker.Clear();
        var entity = await _context.Db.RecurringMaintenanceTasks.AsNoTracking().SingleAsync(row =>
            row.Id == first.Id && row.PortfolioId == scope.PortfolioId);
        entity.CreatedAt.Should().Be(entity.UpdatedAt);
        entity.CreatedAt.Should().NotBe(fakeClock);

        var identityKey = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(operationKey)));
        var receipt = await _context.Db.AtomicCommandReceipts.AsNoTracking().SingleAsync(row =>
            row.CommandType == "recurring-maintenance.create" && row.IdempotencyKey == identityKey);
        var fingerprintRequest = RecurringMaintenanceCrudWriteSupport.Request(
            scope, RecurringMaintenanceWriteOperation.Create, 0, request, operationKey);
        receipt.RequestFingerprint.Should().Be(AtomicCommandFingerprint.Create(fingerprintRequest));
        receipt.ResultContract.Should().Be(RecurringMaintenanceCrudWriteSupport.ResultContract);
        JsonSerializer.Deserialize<RecurringMaintenanceWriteResult>(receipt.ResultJson!)!.EntityId
            .Should().Be(entity.Id);

        var audit = await _context.Db.AtomicAuditLogs.AsNoTracking().SingleAsync(row =>
            row.CommandType == receipt.CommandType && row.CommandIdempotencyKey == receipt.IdempotencyKey);
        audit.Timestamp.Should().Be(entity.CreatedAt);
        audit.Timestamp.Should().NotBe(fakeClock);
        var outboxKey = $"recurring-maintenance:{scope.PortfolioId}:{scope.AccessContextId}:" +
            $"Create:{entity.Id}:{operationKey}:data-update";
        (await _context.Db.OutboxMessages.AsNoTracking().CountAsync(row =>
            row.IdempotencyKey == outboxKey)).Should().Be(1);

        var session = await _context.Db.AuthSessions.SingleAsync(row => row.Id == scope.SessionId);
        session.Status = AuthSessionStatus.Revoked;
        session.RevokedAtUtc = DateTime.UtcNow;
        await _context.Db.SaveChangesAsync();
        _context.Db.ChangeTracker.Clear();

        await using (var services = BuildServices(new FixedTimeProvider(fakeClock.AddDays(1))))
        await using (var serviceScope = services.CreateAsyncScope())
        {
            var sut = serviceScope.ServiceProvider.GetRequiredService<RecurringMaintenanceTaskService>();
            Func<Task> staleReplay = () => sut.CreateAuthorizedAsync(scope, request, operationKey);
            await staleReplay.Should().ThrowAsync<UnauthorizedAccessException>()
                .WithMessage("Workspace access changed. Refresh and try again.");
        }

        (await _context.Db.RecurringMaintenanceTasks.AsNoTracking().CountAsync(row =>
            row.Id == entity.Id)).Should().Be(1);
    }

    [Fact]
    public async Task UpdateSetActiveDelete_EachMutatesOnce_AndReplaysExactResult()
    {
        var now = DateTime.UtcNow.AddMinutes(-5);
        var scope = await SeedScopeAsync(now);
        var propertyId = await _context.Db.Properties.AsNoTracking()
            .Where(row => row.PortfolioId == scope.PortfolioId && row.Name == "Family 5a property")
            .Select(row => row.Id)
            .SingleAsync();

        await using var services = BuildServices(new FixedTimeProvider(now.AddYears(20)));
        await using var serviceScope = services.CreateAsyncScope();
        var sut = serviceScope.ServiceProvider.GetRequiredService<RecurringMaintenanceTaskService>();
        var created = (await sut.CreateAuthorizedAsync(
            scope, CreateRequest(propertyId, "Lifecycle schedule"), "recurring-lifecycle-create"))!;

        var update = new UpdateRecurringMaintenanceTaskRequest
        {
            Title = "Updated lifecycle schedule",
            Category = "Exterior",
            IsActive = true,
        };
        var updated = await sut.UpdateAuthorizedAsync(
            scope, created.Id, update, "recurring-lifecycle-update");
        var updateReplay = await sut.UpdateAuthorizedAsync(
            scope, created.Id, update, "recurring-lifecycle-update");
        updateReplay.Should().BeEquivalentTo(updated);

        var inactive = await sut.SetActiveAuthorizedAsync(
            scope, created.Id, false, "recurring-lifecycle-active");
        var inactiveReplay = await sut.SetActiveAuthorizedAsync(
            scope, created.Id, false, "recurring-lifecycle-active");
        inactiveReplay.Should().BeEquivalentTo(inactive);
        inactive!.IsActive.Should().BeFalse();

        (await sut.DeleteAuthorizedAsync(scope, created.Id, "recurring-lifecycle-delete"))
            .Should().BeTrue();
        (await sut.DeleteAuthorizedAsync(scope, created.Id, "recurring-lifecycle-delete"))
            .Should().BeTrue();

        _context.Db.ChangeTracker.Clear();
        var deleted = await _context.Db.RecurringMaintenanceTasks.IgnoreQueryFilters()
            .AsNoTracking().SingleAsync(row => row.Id == created.Id);
        deleted.Title.Should().Be(update.Title);
        deleted.IsActive.Should().BeFalse();
        deleted.DeletedAt.Should().NotBeNull();
        (await _context.Db.AtomicCommandReceipts.AsNoTracking().CountAsync(row =>
            row.CommandType.StartsWith("recurring-maintenance."))).Should().Be(4);
        (await _context.Db.AtomicAuditLogs.AsNoTracking().CountAsync(row =>
            row.EntityType == nameof(RecurringMaintenanceTask) && row.EntityId == created.Id))
            .Should().Be(4);
    }

    [Fact]
    public async Task MutationWithoutSharedExecutor_ThrowsRetiredPathMessage()
    {
        var sut = new RecurringMaintenanceTaskService(_context.Db, TimeProvider.System);
        var scope = new WorkspaceReadScope(1, 1, Guid.NewGuid(), 1, 1);

        Func<Task> act = () => sut.CreateAuthorizedAsync(
            scope, CreateRequest(1, "Retired path"), "retired-recurring-path");

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("The shared request write executor is required for recurring maintenance changes.");
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
            Name = "Family 5a property",
            AddressLine1 = "5 Executor Way",
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

    private static CreateRecurringMaintenanceTaskRequest CreateRequest(int propertyId, string title) =>
        new()
        {
            PropertyId = propertyId,
            Title = title,
            Description = "Executor parity canary",
            Category = "HVAC",
            RecurrenceInterval = RecurrenceInterval.Monthly,
            NextDueDate = new DateTime(2027, 9, 1, 0, 0, 0, DateTimeKind.Utc),
            ScheduledTime = new TimeOnly(9, 15),
            EstimatedCost = 85m,
            IsActive = true,
            Priority = WorkOrderPriority.Normal,
        };

    private ServiceProvider BuildServices(TimeProvider timeProvider)
    {
        var services = new ServiceCollection();
        services.AddSingleton(timeProvider);
        services.AddScoped<ICurrentActor, TestActor>();
        services.AddAtomicPersistenceKernel();
        services.AddScoped<IRequestWriteExecutor, RequestWriteExecutor>();
        services.AddScoped<RecurringMaintenanceTaskService>();
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
        public string? ActorLabel => "integration:recurring-maintenance-crud";
        public string? IpAddress => "127.0.0.1";
    }
}
