using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
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
public sealed class WorkOrderCrudWritePostgreSqlTests : IAsyncLifetime
{
    private static readonly DateTime BusinessNow =
        new(2027, 4, 5, 16, 0, 0, DateTimeKind.Utc);
    private readonly MigratedPostgreSqlFixture _fixture;
    private MigratedPostgreSqlTestContext _context = null!;

    public WorkOrderCrudWritePostgreSqlTests(MigratedPostgreSqlFixture fixture) =>
        _fixture = fixture;

    public async Task InitializeAsync() =>
        _context = await _fixture.CreateContextAsync();

    public async Task DisposeAsync() =>
        await _context.DisposeAsync();

    [Fact]
    public async Task StaffCreate_PreservesFingerprintReplayAuditClockAuthorizationAndAppointmentSync()
    {
        var scope = await SeedScopeAsync(BusinessNow.AddDays(-1));
        var relationship = await SeedRelationshipAsync(BusinessNow.AddDays(-30));
        var request = new CreateWorkOrderRequest
        {
            PropertyId = relationship.PropertyId,
            UnitId = relationship.UnitId,
            TenantId = relationship.TenantId,
            LeaseManagementId = relationship.ManagementId,
            Title = "Executor plumbing repair",
            Description = "Repair the kitchen sink.",
            Category = "Plumbing",
            Priority = WorkOrderPriority.High,
            Status = WorkOrderStatus.Scheduled,
            RequestedAt = BusinessNow.AddMinutes(-5),
            ScheduledFor = new DateTimeOffset(BusinessNow.AddDays(1)),
            ScheduledWindowEnd = new DateTimeOffset(BusinessNow.AddDays(1).AddHours(2)),
            CreatedBy = "Staff",
        };
        const string key = "family4-work-order-create";

        WorkOrderResponse created;
        await using (var services = BuildServices())
        await using (var serviceScope = services.CreateAsyncScope())
        {
            var service = serviceScope.ServiceProvider.GetRequiredService<WorkOrderService>();
            created = (await service.CreateAuthorizedAsync(scope, request, key))!;
            var replay = await service.CreateAuthorizedAsync(scope, request, key);
            replay.Should().BeEquivalentTo(created);
        }

        _context.Db.ChangeTracker.Clear();
        (await _context.Db.WorkOrders.AsNoTracking().CountAsync(row => row.Id == created.Id))
            .Should().Be(1);
        var appointment = await _context.Db.Appointments.AsNoTracking()
            .SingleAsync(row => row.WorkOrderId == created.Id);
        appointment.Status.Should().Be(AppointmentStatus.Confirmed);
        appointment.ScheduledStart.Should().Be(request.ScheduledFor!.Value.UtcDateTime);

        var identity = new AtomicCommandIdentity(
            "work-order.create", WorkOrderCrudWriteSupport.IdempotencyKey(key));
        var receipt = await _context.Db.AtomicCommandReceipts.AsNoTracking().SingleAsync(row =>
            row.CommandType == identity.CommandType && row.IdempotencyKey == identity.IdempotencyKey);
        receipt.RequestFingerprint.Should().Be(AtomicCommandFingerprint.Create(
            CreateCommand(scope, request, key)));
        receipt.ResultContract.Should().Be(WorkOrderCrudWriteSupport.StaffResultContract);

        var audit = await _context.Db.AtomicAuditLogs.AsNoTracking().SingleAsync(row =>
            row.CommandType == identity.CommandType
            && row.CommandIdempotencyKey == identity.IdempotencyKey
            && row.EntityType == nameof(WorkOrder));
        audit.Timestamp.Should().Be(BusinessNow);
        audit.Timestamp.Should().NotBeCloseTo(DateTime.UtcNow, TimeSpan.FromHours(1));
        (await _context.Db.OutboxMessages.AsNoTracking().CountAsync(row =>
            row.IdempotencyKey == $"work-order-create:{key}")).Should().Be(1);
        (await _context.Db.OutboxMessages.AsNoTracking().CountAsync(row =>
            row.IdempotencyKey == $"appointment-work-order-sync:{key}")).Should().Be(1);

        var session = await _context.Db.AuthSessions.SingleAsync(row => row.Id == scope.SessionId);
        session.Status = AuthSessionStatus.Revoked;
        session.RevokedAtUtc = BusinessNow.AddMinutes(1);
        await _context.Db.SaveChangesAsync();
        _context.Db.ChangeTracker.Clear();

        await using var staleServices = BuildServices();
        await using var staleScope = staleServices.CreateAsyncScope();
        Func<Task> staleReplay = () => staleScope.ServiceProvider
            .GetRequiredService<WorkOrderService>()
            .CreateAuthorizedAsync(scope, request, key);
        await staleReplay.Should().ThrowAsync<UnauthorizedAccessException>()
            .WithMessage("The active assignment cannot manage this property.");
    }

    [Fact]
    public async Task LockPlansDeclareLegacyProtocols_AndAllLegacyHandlerArmsThrow()
    {
        var actor = new StaffOperationActor(1, Guid.NewGuid(), 2, 3);
        var create = CreateCommand(new WorkspaceReadScope(1, 1, actor.AuthSessionId, 2, 3),
            new CreateWorkOrderRequest
            {
                PropertyId = 4, Title = "Create", Description = "Create", Category = "General",
            }, "staff-create");
        var update = new UpdateWorkOrderCommand(
            1, actor, 10, null, false, null, false, null, false, null,
            "Update", null, null, null, null, null, null, null, null, null, null, null,
            null, null, null, null, null, null, null, null, null, null, null, null,
            BusinessNow, "staff-update");
        var delete = new DeleteWorkOrderCommand(1, actor, 10, BusinessNow, "staff-delete");
        var staffComment = new AddStaffWorkOrderCommentCommand(
            1, actor, 10, "Staff comment", false, BusinessNow, "staff-comment");
        var tenantCreate = new CreateTenantWorkOrderCommand(
            1, 1, Guid.NewGuid(), 2, 3, "Tenant create", "Tenant create", "General",
            WorkOrderPriority.Normal, BusinessNow, null, null, null, null, null, null,
            null, null, null, "tenant-create");
        var tenantComment = new AddTenantWorkOrderCommentCommand(
            1, 1, tenantCreate.TenantAuthSessionId, 2, 3, 10, "Tenant comment",
            BusinessNow, "tenant-comment");
        var tenantUpdate = new UpdateTenantWorkOrderCommand(
            PortfolioId: 1,
            TenantUserId: 1,
            TenantAuthSessionId: tenantCreate.TenantAuthSessionId,
            TenantAccessContextId: 2,
            TenantAccessRevision: 3,
            WorkOrderId: 10,
            Title: "Tenant update",
            Description: null,
            RequesterName: null,
            RequesterPhone: null,
            RequesterEmail: null,
            ResidentMustBePresent: null,
            CallBeforeEntry: null,
            CallIfNotHome: null,
            PermissionToEnter: null,
            EntryNotes: null,
            PetWarnings: null,
            AccessWarnings: null,
            BusinessNowUtc: BusinessNow,
            DeliveryIdempotencyKey: "tenant-update");
        var tenantCancel = new CancelTenantWorkOrderCommand(
            1, 1, tenantCreate.TenantAuthSessionId, 2, 3, 10, "Cancel",
            BusinessNow, "tenant-cancel");

        var noResult = static (CreateWorkOrderCommand _, IAtomicCommandContext _, CancellationToken _) =>
            Task.FromResult(new WorkOrderMutationResult(OperationMutationOutcome.Applied, 1));
        WorkOrderCrudWriteSupport.Write(create, noResult, static (_, _, _) => Task.CompletedTask)
            .LockPlan.Locks.Should().BeEmpty();
        var updatePlan = WorkOrderCrudWriteSupport.Write(update,
            static (_, _, _) => Task.FromResult(new WorkOrderMutationResult(OperationMutationOutcome.Applied, 1)),
            static (_, _, _) => Task.CompletedTask).LockPlan;
        updatePlan.Protocol.Should().Be(WriteLockProtocol.WorkOrderAppointmentProgression);
        updatePlan.DeferredLockNamespaces.Should().Equal("Appointment", "WorkOrder");
        WorkOrderCrudWriteSupport.Write(delete,
            static (_, _, _) => Task.FromResult(new WorkOrderMutationResult(OperationMutationOutcome.Applied, 1)),
            static (_, _, _) => Task.CompletedTask).LockPlan.Protocol.Should().Be(WriteLockProtocol.WorkOrder);
        WorkOrderCrudWriteSupport.Write(staffComment,
            static (_, _, _) => Task.FromResult(new WorkOrderMutationResult(OperationMutationOutcome.Applied, 1)),
            static (_, _, _) => Task.CompletedTask).LockPlan.Protocol.Should().Be(WriteLockProtocol.WorkOrder);
        WorkOrderCrudWriteSupport.Write(tenantCreate,
            static (_, _, _) => Task.FromResult(new WorkOrderMutationResult(OperationMutationOutcome.Applied, 1)),
            static (_, _, _) => Task.CompletedTask).LockPlan.Locks.Should().BeEmpty();
        WorkOrderCrudWriteSupport.Write(tenantComment,
            static (_, _, _) => Task.FromResult(new WorkOrderMutationResult(OperationMutationOutcome.Applied, 1)),
            static (_, _, _) => Task.CompletedTask).LockPlan.Protocol.Should().Be(WriteLockProtocol.WorkOrder);
        WorkOrderCrudWriteSupport.Write(tenantUpdate,
            static (_, _, _) => Task.FromResult(new WorkOrderMutationResult(OperationMutationOutcome.Applied, 1)),
            static (_, _, _) => Task.CompletedTask).LockPlan.Protocol.Should().Be(WriteLockProtocol.WorkOrder);
        WorkOrderCrudWriteSupport.Write(tenantCancel,
            static (_, _, _) => Task.FromResult(new WorkOrderMutationResult(OperationMutationOutcome.Applied, 1)),
            static (_, _, _) => Task.CompletedTask).LockPlan.Protocol
            .Should().Be(WriteLockProtocol.WorkOrderAppointmentProgression);

        await AssertRetiredAsync(new CreateWorkOrderHandler(_context.Db), create);
        await AssertRetiredAsync(new UpdateWorkOrderHandler(_context.Db), update);
        await AssertRetiredAsync(new DeleteWorkOrderHandler(_context.Db), delete);
        await AssertRetiredAsync(new AddStaffWorkOrderCommentHandler(_context.Db), staffComment);
        await AssertRetiredAsync(new CreateTenantWorkOrderHandler(_context.Db), tenantCreate);
        await AssertRetiredAsync(new AddTenantWorkOrderCommentHandler(_context.Db), tenantComment);
        await AssertRetiredAsync(new UpdateTenantWorkOrderHandler(_context.Db), tenantUpdate);
        await AssertRetiredAsync(new CancelTenantWorkOrderHandler(_context.Db), tenantCancel);
    }

    [Fact]
    public async Task ForwardAndReplayLockAcquisitionSequences_MatchLegacySingleWorkOrderLock()
    {
        var scope = await SeedScopeAsync(BusinessNow.AddDays(-1));
        var relationship = await SeedRelationshipAsync(BusinessNow.AddDays(-30));
        var partyId = await _context.Db.LeaseManagementParties
            .Where(row => row.LeaseManagementId == relationship.ManagementId)
            .Select(row => row.Id)
            .SingleAsync();
        _context.Db.Add(new TenantUserAccess
        {
            PublicId = Guid.NewGuid(), PortfolioId = scope.PortfolioId,
            AccessContextId = scope.AccessContextId, ApplicationUserId = scope.UserId,
            LeaseManagementPartyId = partyId, GrantedAtUtc = BusinessNow.AddDays(-1),
            GrantedByUserId = scope.UserId, Reason = "Work-order lock sequence proof",
        });
        var workOrder = new WorkOrder
        {
            PortfolioId = scope.PortfolioId, PropertyId = relationship.PropertyId,
            UnitId = relationship.UnitId, TenantId = relationship.TenantId,
            LeaseManagementId = relationship.ManagementId, Title = "Lock sequence",
            Description = "Freeze forward and replay acquisition order.", Category = "General",
            Priority = WorkOrderPriority.Normal, Status = WorkOrderStatus.New,
            RequestedAt = BusinessNow.AddHours(-1), UpdatedAt = BusinessNow.AddHours(-1),
            CreatedBy = "Tenant",
        };
        _context.Db.Add(workOrder);
        await _context.Db.SaveChangesAsync();
        _context.Db.ChangeTracker.Clear();

        var acquired = new List<string>();
        var context = new Mock<IAtomicCommandContext>();
        context.Setup(item => item.ReadDatabaseClockUtcAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(BusinessNow);
        context.Setup(item => item.AcquireLockAsync(
                It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .Callback<string, int, CancellationToken>((lockNamespace, id, _) =>
                acquired.Add($"{lockNamespace}:{id}"))
            .Returns(Task.CompletedTask);
        context.Setup(item => item.FlushBusinessAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AtomicBusinessFlush(0, []));

        var actor = new StaffOperationActor(
            scope.UserId, scope.SessionId, scope.AccessContextId, scope.AccessRevision);
        var staffComment = new AddStaffWorkOrderCommentCommand(
            scope.PortfolioId, actor, workOrder.Id, "Staff comment", false,
            BusinessNow, "lock-staff-comment");
        var tenantComment = new AddTenantWorkOrderCommentCommand(
            scope.PortfolioId, scope.UserId, scope.SessionId, scope.AccessContextId,
            scope.AccessRevision, workOrder.Id, "Tenant comment", BusinessNow, "lock-tenant-comment");
        var tenantUpdate = new UpdateTenantWorkOrderCommand(
            scope.PortfolioId, scope.UserId, scope.SessionId, scope.AccessContextId,
            scope.AccessRevision, workOrder.Id, "Updated lock sequence", null, null, null, null,
            null, null, null, null, null, null, null, BusinessNow, "lock-tenant-update");
        var delete = new DeleteWorkOrderCommand(
            scope.PortfolioId, actor, workOrder.Id, BusinessNow, "lock-staff-delete");

        var staffCommentRule = new AddStaffWorkOrderCommentRule(_context.Db);
        await AssertLockSequencesAsync(_context.Db, context.Object, acquired,
            WorkOrderCrudWriteSupport.Write(
                staffComment, staffCommentRule.HandleAsync, staffCommentRule.AuthorizeReplayAsync), workOrder.Id);
        var tenantCommentRule = new AddTenantWorkOrderCommentRule(_context.Db);
        await AssertLockSequencesAsync(_context.Db, context.Object, acquired,
            WorkOrderCrudWriteSupport.Write(
                tenantComment, tenantCommentRule.HandleAsync, tenantCommentRule.AuthorizeReplayAsync), workOrder.Id);
        var tenantUpdateRule = new UpdateTenantWorkOrderRule(_context.Db);
        await AssertLockSequencesAsync(_context.Db, context.Object, acquired,
            WorkOrderCrudWriteSupport.Write(
                tenantUpdate, tenantUpdateRule.HandleAsync, tenantUpdateRule.AuthorizeReplayAsync), workOrder.Id);
        var deleteRule = new DeleteWorkOrderRule(_context.Db);
        await AssertLockSequencesAsync(_context.Db, context.Object, acquired,
            WorkOrderCrudWriteSupport.Write(
                delete, deleteRule.HandleAsync, deleteRule.AuthorizeReplayAsync), workOrder.Id);
    }

    private static async Task AssertLockSequencesAsync<TCommand>(
        RentalCommandDbContext db,
        IAtomicCommandContext context,
        List<string> acquired,
        TransactionalWrite<TCommand, WorkOrderMutationResult> write,
        int workOrderId)
        where TCommand : notnull, IAtomicCommandData
    {
        acquired.Clear();
        foreach (var writeLock in write.LockPlan.Locks)
            await writeLock.AcquireAsync(context);
        await write.ExecuteAsync(write.Request, context, CancellationToken.None);
        acquired.Should().Equal($"WorkOrder:{workOrderId}");
        db.ChangeTracker.Clear();

        acquired.Clear();
        await write.AuthorizeReplayAsync(write.Request, context, CancellationToken.None);
        acquired.Should().Equal($"WorkOrder:{workOrderId}");
        db.ChangeTracker.Clear();
    }

    private static async Task AssertRetiredAsync<TCommand>(
        IAtomicCommandHandler<TCommand, WorkOrderMutationResult> handler, TCommand command)
        where TCommand : IAtomicCommandData
    {
        await handler.Invoking(item => item.HandleAsync(command, null!, CancellationToken.None))
            .Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("Work-order CRUD and comments no longer use the legacy mutation handlers.");
        await handler.Invoking(item => item.AuthorizeReplayAsync(command, null!, CancellationToken.None))
            .Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("Work-order CRUD and comments no longer use the legacy mutation handlers.");
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
            AccessContext = accessContext, PortfolioId = 1, Status = WorkspaceMembershipStatus.Active,
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

    private async Task<RelationshipIds> SeedRelationshipAsync(DateTime now)
    {
        var property = new Property
        {
            PortfolioId = 1, Name = $"Work-order property {Guid.NewGuid():N}",
            AddressLine1 = "4 Executor Way", City = "Columbus", State = "OH",
            PostalCode = "43215", CreatedAt = now, UpdatedAt = now,
        };
        var tenant = new Tenant
        {
            PortfolioId = 1, FirstName = "Morgan", LastName = "Resident",
            Email = "morgan@example.test", Phone = "5550104", CreatedAt = now, UpdatedAt = now,
        };
        _context.Db.AddRange(property, tenant);
        await _context.Db.SaveChangesAsync();
        var unit = new Unit
        {
            PortfolioId = 1, PropertyId = property.Id, UnitNumber = "4",
            CreatedAt = now, UpdatedAt = now,
        };
        _context.Db.Add(unit);
        await _context.Db.SaveChangesAsync();
        var management = new LeaseManagement
        {
            PublicId = Guid.NewGuid(), PortfolioId = 1, PropertyId = property.Id, UnitId = unit.Id,
            RelationshipNumber = $"LM-{Guid.NewGuid():N}", PossessionGivenAtUtc = now,
            CreatedAtUtc = now, UpdatedAtUtc = now, CreatedByUserId = 1, RowVersion = Guid.NewGuid(),
        };
        _context.Db.Add(management);
        await _context.Db.SaveChangesAsync();
        _context.Db.Add(new LeaseManagementParty
        {
            PortfolioId = 1, LeaseManagementId = management.Id, TenantId = tenant.Id,
            Role = LeaseManagementPartyRole.PrimaryTenant,
            EffectiveFrom = DateOnly.FromDateTime(now), ChangeReason = "Executor family test",
            CreatedAtUtc = now, CreatedByUserId = 1,
        });
        await _context.Db.SaveChangesAsync();
        _context.Db.ChangeTracker.Clear();
        return new(property.Id, unit.Id, tenant.Id, management.Id);
    }

    private ServiceProvider BuildServices()
    {
        var services = new ServiceCollection();
        services.AddSingleton<TimeProvider>(new FixedTimeProvider(BusinessNow));
        services.AddSingleton(Mock.Of<IDataUpdateService>());
        services.AddSingleton(Mock.Of<IMessagePublisher>());
        services.AddSingleton(Mock.Of<IFileStorage>());
        services.AddSingleton(Mock.Of<ILogger<WorkOrderService>>());
        services.AddScoped<ICurrentActor, TestActor>();
        services.AddAtomicPersistenceKernel();
        services.AddAtomicCommandHandler<CreateWorkOrderCommand, WorkOrderMutationResult, CreateWorkOrderHandler>();
        services.AddAtomicCommandHandler<UpdateWorkOrderCommand, WorkOrderMutationResult, UpdateWorkOrderHandler>();
        services.AddAtomicCommandHandler<DeleteWorkOrderCommand, WorkOrderMutationResult, DeleteWorkOrderHandler>();
        services.AddAtomicCommandHandler<AddStaffWorkOrderCommentCommand, WorkOrderMutationResult, AddStaffWorkOrderCommentHandler>();
        services.AddAtomicCommandHandler<CreateTenantWorkOrderCommand, WorkOrderMutationResult, CreateTenantWorkOrderHandler>();
        services.AddAtomicCommandHandler<AddTenantWorkOrderCommentCommand, WorkOrderMutationResult, AddTenantWorkOrderCommentHandler>();
        services.AddAtomicCommandHandler<UpdateTenantWorkOrderCommand, WorkOrderMutationResult, UpdateTenantWorkOrderHandler>();
        services.AddAtomicCommandHandler<CancelTenantWorkOrderCommand, WorkOrderMutationResult, CancelTenantWorkOrderHandler>();
        services.AddScoped<IRequestWriteExecutor, RequestWriteExecutor>();
        services.AddScoped<WorkOrderService>();
        services.AddDbContext<RentalCommandDbContext>((provider, options) =>
            options.UseNpgsql(_context.ConnectionString).UseAtomicPersistenceKernel(provider));
        return services.BuildServiceProvider(
            new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
    }

    private static CreateWorkOrderCommand CreateCommand(
        WorkspaceReadScope scope, CreateWorkOrderRequest request, string key) => new(
        scope.PortfolioId,
        new StaffOperationActor(scope.UserId, scope.SessionId, scope.AccessContextId, scope.AccessRevision),
        request.PropertyId, request.UnitId, request.TenantId, request.LeaseManagementId,
        request.VendorId, request.Title, request.Description, request.TechnicianAccessInstructions,
        request.SubmittedByLabel, request.RequesterName, request.RequesterPhone, request.RequesterEmail,
        request.ResidentMustBePresent, request.CallBeforeEntry, request.CallIfNotHome,
        request.PermissionToEnter, request.EntryNotes, request.PetWarnings, request.AccessWarnings,
        request.Category, request.Priority, request.Status, request.RequestedAt,
        request.ScheduledFor, request.ScheduledWindowEnd,
        request.ScheduledFor?.UtcDateTime, request.ScheduledWindowEnd?.UtcDateTime,
        request.CompletedAt, request.EstimatedCost, request.ActualCost,
        request.CreatedBy, request.ExtractedData, BusinessNow, key);

    private sealed record RelationshipIds(
        int PropertyId, int UnitId, int TenantId, int ManagementId);

    private sealed class FixedTimeProvider(DateTime now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(now);
    }

    private sealed class TestActor : ICurrentActor
    {
        public int? UserId => null;
        public string? ActorLabel => "integration:work-order-crud";
        public string? IpAddress => "127.0.0.1";
    }
}
