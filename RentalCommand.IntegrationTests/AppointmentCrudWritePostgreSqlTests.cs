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

[Collection(RoleAuthorityPostgreSqlCollection4.Name)]
public sealed class AppointmentCrudWritePostgreSqlTests : IAsyncLifetime
{
    private static readonly DateTime BusinessNow =
        new(2027, 3, 4, 15, 0, 0, DateTimeKind.Utc);
    private readonly MigratedPostgreSqlFixture _fixture;
    private MigratedPostgreSqlTestContext _context = null!;

    public AppointmentCrudWritePostgreSqlTests(MigratedPostgreSqlFixture fixture) =>
        _fixture = fixture;

    public async Task InitializeAsync() =>
        _context = await _fixture.CreateContextAsync();

    public async Task DisposeAsync() =>
        await _context.DisposeAsync();

    [Fact]
    public async Task CreateAndUpdate_PreserveFingerprintReplayAuditClockAuthorizationAndWorkOrderLink()
    {
        var scope = await SeedScopeAsync(BusinessNow.AddDays(-1));
        var (property, firstWorkOrder, secondWorkOrder) = await SeedWorkOrdersAsync(BusinessNow);
        var request = CreateRequest(property.Id, firstWorkOrder.Id);
        const string createKey = "family4-appointment-create";

        AppointmentResponse created;
        await using (var services = BuildServices(new FixedTimeProvider(BusinessNow)))
        await using (var serviceScope = services.CreateAsyncScope())
        {
            var service = serviceScope.ServiceProvider.GetRequiredService<AppointmentService>();
            created = (await service.CreateAuthorizedAsync(scope, request, createKey))!;
            var replay = await service.CreateAuthorizedAsync(scope, request, createKey);
            replay.Should().BeEquivalentTo(created);

            var updated = await service.UpdateAuthorizedAsync(scope, created.Id,
                new UpdateAppointmentRequest
                {
                    WorkOrderId = secondWorkOrder.Id,
                    Title = "Executor appointment relinked",
                }, "family4-appointment-update");
            updated.Should().NotBeNull();
            updated!.WorkOrderId.Should().Be(secondWorkOrder.Id);
        }

        _context.Db.ChangeTracker.Clear();
        (await _context.Db.Appointments.AsNoTracking()
            .CountAsync(row => row.Id == created.Id)).Should().Be(1);
        var linkedAppointment = await _context.Db.Appointments.AsNoTracking()
            .SingleAsync(row => row.Id == created.Id);
        linkedAppointment.WorkOrderId.Should().Be(secondWorkOrder.Id);
        (await _context.Db.WorkOrders.AsNoTracking()
            .CountAsync(row => row.Id == firstWorkOrder.Id || row.Id == secondWorkOrder.Id))
            .Should().Be(2);

        var identity = new AtomicCommandIdentity(
            "appointment.create",
            AppointmentCrudWriteSupport.IdempotencyKey(createKey));
        var receipt = await _context.Db.AtomicCommandReceipts.AsNoTracking().SingleAsync(row =>
            row.CommandType == identity.CommandType && row.IdempotencyKey == identity.IdempotencyKey);
        var frozenLegacyShape = new CreateAppointmentCommand(
            scope.PortfolioId,
            Actor(scope),
            request.PropertyId,
            request.UnitId,
            request.LeaseManagementId,
            request.RentalApplicationId,
            request.TenantId,
            request.WorkOrderId,
            request.Title,
            request.ProspectName,
            request.ProspectEmail,
            request.Type,
            request.Status,
            request.ScheduledStart,
            request.ScheduledEnd,
            request.AssignedTo,
            request.Notes,
            BusinessNow,
            createKey);
        receipt.RequestFingerprint.Should().Be(AtomicCommandFingerprint.Create(frozenLegacyShape));
        receipt.ResultContract.Should().Be(AppointmentCrudWriteSupport.ResultContract);
        JsonSerializer.Deserialize<OperationMutationResult>(receipt.ResultJson!)!
            .EntityId.Should().Be(created.Id);

        var audit = await _context.Db.AtomicAuditLogs.AsNoTracking().SingleAsync(row =>
            row.CommandType == identity.CommandType
            && row.CommandIdempotencyKey == identity.IdempotencyKey
            && row.EntityType == nameof(Appointment));
        audit.Timestamp.Should().Be(BusinessNow);
        audit.Timestamp.Should().NotBeCloseTo(DateTime.UtcNow, TimeSpan.FromHours(1));
        (await _context.Db.OutboxMessages.AsNoTracking().CountAsync(row =>
            row.IdempotencyKey == $"appointment-create:{createKey}"))
            .Should().Be(1);

        var session = await _context.Db.AuthSessions.SingleAsync(row => row.Id == scope.SessionId);
        session.Status = AuthSessionStatus.Revoked;
        session.RevokedAtUtc = BusinessNow.AddMinutes(1);
        await _context.Db.SaveChangesAsync();
        _context.Db.ChangeTracker.Clear();

        await using var staleServices = BuildServices(new FixedTimeProvider(BusinessNow.AddMinutes(2)));
        await using var staleScope = staleServices.CreateAsyncScope();
        Func<Task> staleReplay = () => staleScope.ServiceProvider
            .GetRequiredService<AppointmentService>()
            .CreateAuthorizedAsync(scope, request, createKey);
        await staleReplay.Should().ThrowAsync<UnauthorizedAccessException>()
            .WithMessage("The active assignment cannot manage this appointment.");
    }

    [Fact]
    public async Task Delete_PreservesLinkedWorkOrderAndStagesLegacyDeleteOutbox()
    {
        var scope = await SeedScopeAsync(BusinessNow.AddDays(-1));
        var (property, workOrder, _) = await SeedWorkOrdersAsync(BusinessNow);
        await using var services = BuildServices(new FixedTimeProvider(BusinessNow));
        await using var serviceScope = services.CreateAsyncScope();
        var service = serviceScope.ServiceProvider.GetRequiredService<AppointmentService>();
        var created = (await service.CreateAuthorizedAsync(
            scope, CreateRequest(property.Id, workOrder.Id), "family4-delete-seed"))!;

        (await service.DeleteAuthorizedAsync(
            scope, created.Id, property.Id, "family4-appointment-delete")).Should().BeTrue();

        _context.Db.ChangeTracker.Clear();
        (await _context.Db.Appointments.AsNoTracking().AnyAsync(row => row.Id == created.Id))
            .Should().BeFalse();
        (await _context.Db.WorkOrders.AsNoTracking().AnyAsync(row => row.Id == workOrder.Id))
            .Should().BeTrue();
        var outbox = await _context.Db.OutboxMessages.AsNoTracking().SingleAsync(row =>
            row.IdempotencyKey == "appointment-delete:family4-appointment-delete");
        using var payload = JsonDocument.Parse(outbox.Payload);
        payload.RootElement.GetProperty("operation").GetString().Should().Be("delete");
    }

    [Fact]
    public void LockPlansDeclareLegacyOrder()
    {
        var actor = new StaffOperationActor(1, Guid.NewGuid(), 2, 3);
        var create = new CreateAppointmentCommand(
            1, actor, 4, null, null, null, null, 10, "Lock plan", null, null,
            AppointmentType.MaintenanceVisit, AppointmentStatus.Scheduled,
            BusinessNow, null, null, null, BusinessNow, "create-lock-plan");
        var update = new UpdateAppointmentCommand(
            1, actor, 11, null, null, null, null, null, 12, "Update", null, null,
            null, null, null, null, null, null, BusinessNow, "update-lock-plan");
        var delete = new DeleteAppointmentCommand(
            1, actor, 11, 4, BusinessNow, "delete-lock-plan");

        var createWrite = AppointmentCrudWriteSupport.Write(
            create, static (_, _, _) => Task.FromResult(new OperationMutationResult(OperationMutationOutcome.Applied, 1)),
            static (_, _, _) => Task.CompletedTask);
        var updateWrite = AppointmentCrudWriteSupport.Write(
            update, static (_, _, _) => Task.FromResult(new OperationMutationResult(OperationMutationOutcome.Applied, 1)),
            static (_, _, _) => Task.CompletedTask);
        var deleteWrite = AppointmentCrudWriteSupport.Write(
            delete, static (_, _, _) => Task.FromResult(new OperationMutationResult(OperationMutationOutcome.Applied, 1)),
            static (_, _, _) => Task.CompletedTask);

        createWrite.LockPlan.Protocol.Should().Be(WriteLockProtocol.WorkOrder);
        createWrite.LockPlan.Locks.Select(row => row.LockNamespace).Should().Equal("WorkOrder");
        updateWrite.LockPlan.Protocol.Should().Be(WriteLockProtocol.AppointmentWorkOrder);
        updateWrite.LockPlan.Locks.Select(row => row.LockNamespace).Should().Equal("Appointment");
        updateWrite.LockPlan.DeferredLockNamespaces.Should().Equal("WorkOrder");
        deleteWrite.LockPlan.Protocol.Should().Be(WriteLockProtocol.AppointmentWorkOrder);

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

    private async Task<(Property Property, WorkOrder First, WorkOrder Second)> SeedWorkOrdersAsync(DateTime now)
    {
        var property = new Property
        {
            PortfolioId = 1, Name = $"Family 4 property {Guid.NewGuid():N}",
            AddressLine1 = "4 Executor Way", City = "Columbus", State = "OH",
            PostalCode = "43215", CreatedAt = now, UpdatedAt = now,
        };
        var first = WorkOrder("First linked work order", property, now);
        var second = WorkOrder("Second linked work order", property, now);
        _context.Db.AddRange(property, first, second);
        await _context.Db.SaveChangesAsync();
        _context.Db.ChangeTracker.Clear();
        return (property, first, second);
    }

    private static WorkOrder WorkOrder(string title, Property property, DateTime now) => new()
    {
        PortfolioId = 1, Property = property, Title = title, Description = title,
        Status = WorkOrderStatus.New, RequestedAt = now, UpdatedAt = now,
    };

    private static CreateAppointmentRequest CreateRequest(int propertyId, int workOrderId) => new()
    {
        PropertyId = propertyId,
        WorkOrderId = workOrderId,
        Title = "Executor linked appointment",
        Type = AppointmentType.MaintenanceVisit,
        Status = AppointmentStatus.Scheduled,
        ScheduledStart = BusinessNow.AddDays(1),
        ScheduledEnd = BusinessNow.AddDays(1).AddHours(1),
    };

    private ServiceProvider BuildServices(TimeProvider timeProvider)
    {
        var services = new ServiceCollection();
        services.AddSingleton(timeProvider);
        services.AddSingleton(Mock.Of<IDataUpdateService>());
        services.AddScoped<ICurrentActor, TestActor>();
        services.AddAtomicPersistenceKernel();
        services.AddScoped<IRequestWriteExecutor, RequestWriteExecutor>();
        services.AddScoped<AppointmentService>();
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
        public string? ActorLabel => "integration:appointment-crud";
        public string? IpAddress => "127.0.0.1";
    }
}
