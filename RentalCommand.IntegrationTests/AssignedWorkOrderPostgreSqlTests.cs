using System.Data.Common;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
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
using Xunit;

namespace RentalCommand.IntegrationTests;

[Collection(RoleAuthorityPostgreSqlCollection.Name)]
public sealed class AssignedWorkOrderPostgreSqlTests : IAsyncLifetime
{
    private static readonly DateTime BusinessNowUtc = new(2027, 1, 25, 5, 0, 0, DateTimeKind.Utc);
    private static readonly AtomicJsonResultCodec<AssignWorkOrderResponsibilityResult> AssignCodec =
        new("work-order-responsibility.assign.v1");
    private static readonly AtomicJsonResultCodec<CloseWorkOrderResponsibilityResult> CloseCodec =
        new("work-order-responsibility.close.v1");
    private static readonly AtomicJsonResultCodec<UpdateAssignedWorkOrderResult> UpdateCodec =
        new("assigned-work-order.update.v1");
    private static readonly AtomicJsonResultCodec<RecordTechnicianWorkEntryResult> EntryCodec =
        new("technician-work-entry.v1");
    private static readonly AtomicJsonResultCodec<SendTechnicianAssignmentMessageResult> MessageCodec =
        new("technician-assignment-message.v1");
    private static readonly AtomicJsonResultCodec<MarkTechnicianAssignmentConversationReadResult> ReadCodec =
        new("technician-assignment-conversation-read.v1");
    private readonly MigratedPostgreSqlFixture _fixture;
    private MigratedPostgreSqlTestContext _context = null!;
    private ServiceProvider _services = null!;

    public AssignedWorkOrderPostgreSqlTests(MigratedPostgreSqlFixture fixture) => _fixture = fixture;

    public async Task InitializeAsync()
    {
        _context = await _fixture.CreateContextAsync();
        var services = new ServiceCollection();
        services.AddSingleton(TimeProvider.System);
        services.AddScoped<ICurrentActor, TestActor>();
        services.AddAtomicPersistenceKernel(allowUnconvertedWrites: true);
        services.AddAtomicCommandHandler<UpdateAssignedWorkOrderCommand, UpdateAssignedWorkOrderResult,
            UpdateAssignedWorkOrderHandler>();
        services.AddAtomicCommandHandler<AssignWorkOrderResponsibilityCommand, AssignWorkOrderResponsibilityResult,
            AssignWorkOrderResponsibilityHandler>();
        services.AddAtomicCommandHandler<RecordTechnicianWorkEntryCommand, RecordTechnicianWorkEntryResult,
            RecordTechnicianWorkEntryHandler>();
        services.AddAtomicCommandHandler<SendTechnicianAssignmentMessageCommand,
            SendTechnicianAssignmentMessageResult, SendTechnicianAssignmentMessageHandler>();
        services.AddAtomicCommandHandler<MarkTechnicianAssignmentConversationReadCommand,
            MarkTechnicianAssignmentConversationReadResult, MarkTechnicianAssignmentConversationReadHandler>();
        services.AddDbContext<RentalCommandDbContext>((provider, builder) =>
            builder.UseNpgsql(_context.ConnectionString).UseAtomicPersistenceKernel(provider));
        _services = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true,
        });
    }

    public async Task DisposeAsync()
    {
        if (_services is not null) await _services.DisposeAsync();
        if (_context is not null) await _context.DisposeAsync();
    }

    [Fact]
    public async Task AssignedTechnician_UpdateReplaysOnce_AndUnassignedWorkIsRejected()
    {
        var scenario = await SeedScenarioAsync();
        var command = new UpdateAssignedWorkOrderCommand(
            scenario.PortfolioId,
            scenario.UserId,
            scenario.SessionId,
            scenario.AccessContextId,
            scenario.AccessRevision,
            scenario.AssignedWorkOrderId,
            scenario.AssignedUpdatedAt,
            WorkOrderStatus.InProgress,
            "Parts confirmed on site.",
            null,
            null,
            null,
            "assigned-update-replay");
        var identity = new AtomicCommandIdentity(
            "assigned-work-order.update",
            $"{scenario.PortfolioId}:{scenario.AccessContextId}:{scenario.AssignedWorkOrderId}:assigned-update-replay");

        var outcomes = await Task.WhenAll(
            Atomic.ExecuteAsync(identity, command, UpdateCodec),
            Atomic.ExecuteAsync(identity, command, UpdateCodec));

        outcomes.Select(outcome => outcome.Disposition).Should()
            .BeEquivalentTo([AtomicCommandDisposition.Executed, AtomicCommandDisposition.Replayed]);
        outcomes.Select(outcome => outcome.Value.Outcome)
            .Should().OnlyContain(outcome => outcome == UpdateAssignedWorkOrderOutcome.Applied);
        _context.Db.ChangeTracker.Clear();
        var assigned = await _context.Db.WorkOrders.AsNoTracking()
            .SingleAsync(workOrder => workOrder.Id == scenario.AssignedWorkOrderId);
        assigned.Status.Should().Be(WorkOrderStatus.InProgress);
        (await _context.Db.WorkOrderStatusEvents.CountAsync(statusEvent =>
            statusEvent.WorkOrderId == scenario.AssignedWorkOrderId)).Should().Be(1);
        (await _context.Db.AtomicCommandReceipts.CountAsync(receipt =>
            receipt.CommandType == identity.CommandType && receipt.IdempotencyKey == identity.IdempotencyKey))
            .Should().Be(1);

        var unassigned = new UpdateAssignedWorkOrderCommand(
            scenario.PortfolioId,
            scenario.UserId,
            scenario.SessionId,
            scenario.AccessContextId,
            scenario.AccessRevision,
            scenario.UnassignedWorkOrderId,
            scenario.UnassignedUpdatedAt,
            WorkOrderStatus.InProgress,
            "Must not be accepted.",
            null,
            null,
            null,
            "unassigned-update-denied");
        var denied = async () => await Atomic.ExecuteAsync(
            new AtomicCommandIdentity(
                "assigned-work-order.update",
                $"{scenario.PortfolioId}:{scenario.AccessContextId}:{scenario.UnassignedWorkOrderId}:unassigned-update-denied"),
            unassigned,
            UpdateCodec);

        await denied.Should().ThrowAsync<UnauthorizedAccessException>();
        _context.Db.ChangeTracker.Clear();
        (await _context.Db.WorkOrders.AsNoTracking()
            .Where(workOrder => workOrder.Id == scenario.UnassignedWorkOrderId)
            .Select(workOrder => workOrder.Status)
            .SingleAsync()).Should().Be(WorkOrderStatus.New);
        (await _context.Db.AtomicCommandReceipts.CountAsync(receipt =>
            receipt.IdempotencyKey.EndsWith(":unassigned-update-denied"))).Should().Be(0);
    }

    [Fact]
    public async Task AssignedTechnician_EntryMessageAndReadMutations_RejectUnassignedAndStaleAuthority()
    {
        var scenario = await SeedScenarioAsync();
        var entry = new RecordTechnicianWorkEntryCommand(
            scenario.PortfolioId, scenario.UserId, scenario.SessionId, scenario.AccessContextId,
            scenario.AccessRevision, scenario.AssignedWorkOrderId, TechnicianWorkEntryKind.Note,
            "Verified assigned entry", null, null, null, DateTime.UtcNow, "assigned-entry");
        var message = new SendTechnicianAssignmentMessageCommand(
            scenario.PortfolioId, scenario.UserId, scenario.SessionId, scenario.AccessContextId,
            scenario.AccessRevision, scenario.AssignedWorkOrderId, "Assigned technician update.",
            "assigned-message");
        var read = new MarkTechnicianAssignmentConversationReadCommand(
            scenario.PortfolioId, scenario.UserId, scenario.SessionId, scenario.AccessContextId,
            scenario.AccessRevision, scenario.AssignedWorkOrderId, "assigned-read");

        await Atomic.ExecuteAsync(new AtomicCommandIdentity(
            "technician-work-entry.record", $"{scenario.PortfolioId}:{scenario.AssignedWorkOrderId}:assigned-entry"),
            entry, EntryCodec);
        await Atomic.ExecuteAsync(new AtomicCommandIdentity(
            "technician-assignment-message.send", $"{scenario.PortfolioId}:{scenario.AssignedWorkOrderId}:assigned-message"),
            message, MessageCodec);
        await Atomic.ExecuteAsync(new AtomicCommandIdentity(
            "technician-assignment-conversation.read", $"{scenario.PortfolioId}:{scenario.AssignedWorkOrderId}:assigned-read"),
            read, ReadCodec);

        _context.Db.ChangeTracker.Clear();
        (await _context.Db.TechnicianWorkEntries.AsNoTracking().CountAsync(item =>
            item.WorkOrderId == scenario.AssignedWorkOrderId && item.Note == "Verified assigned entry"))
            .Should().Be(1);
        (await _context.Db.ConversationMessages.AsNoTracking().CountAsync(item =>
            item.ConversationId == scenario.ConversationId &&
            item.SenderRole == ConversationSenderRole.Technician &&
            item.Body == "Assigned technician update."))
            .Should().Be(1);
        (await _context.Db.Conversations.AsNoTracking()
            .Where(item => item.Id == scenario.ConversationId)
            .Select(item => item.TechnicianUnreadCount)
            .SingleAsync()).Should().Be(0);

        await AssertDeniedAsync(new AtomicCommandIdentity(
                "technician-work-entry.record", $"{scenario.PortfolioId}:{scenario.UnassignedWorkOrderId}:unassigned-entry"),
            entry with { WorkOrderId = scenario.UnassignedWorkOrderId, DeliveryIdempotencyKey = "unassigned-entry" },
            EntryCodec);
        await AssertDeniedAsync(new AtomicCommandIdentity(
                "technician-assignment-message.send", $"{scenario.PortfolioId}:{scenario.UnassignedWorkOrderId}:unassigned-message"),
            message with { WorkOrderId = scenario.UnassignedWorkOrderId, DeliveryIdempotencyKey = "unassigned-message" },
            MessageCodec);
        await AssertDeniedAsync(new AtomicCommandIdentity(
                "technician-assignment-conversation.read", $"{scenario.PortfolioId}:{scenario.UnassignedWorkOrderId}:unassigned-read"),
            read with { WorkOrderId = scenario.UnassignedWorkOrderId, DeliveryIdempotencyKey = "unassigned-read" },
            ReadCodec);
        await AssertDeniedAsync(new AtomicCommandIdentity(
                "technician-work-entry.record", $"{scenario.PortfolioId}:{scenario.AssignedWorkOrderId}:stale-entry"),
            entry with
            {
                ActorAccessRevision = scenario.AccessRevision + 1,
                DeliveryIdempotencyKey = "stale-entry",
            },
            EntryCodec);

        _context.Db.ChangeTracker.Clear();
        (await _context.Db.TechnicianWorkEntries.AsNoTracking().AnyAsync(item =>
            item.WorkOrderId == scenario.UnassignedWorkOrderId)).Should().BeFalse();
        (await _context.Db.Conversations.AsNoTracking().AnyAsync(item =>
            item.WorkOrderId == scenario.UnassignedWorkOrderId)).Should().BeFalse();
        (await _context.Db.AtomicCommandReceipts.AsNoTracking().CountAsync(receipt =>
            receipt.IdempotencyKey.Contains("unassigned-") || receipt.IdempotencyKey.Contains("stale-entry")))
            .Should().Be(0);
    }

    [Fact]
    public async Task AssignResponsibility_UsesBusinessClockForLifecycleAuditAndOutbox_AndReplaysOriginalResult()
    {
        var scenario = await SeedResponsibilityAssignmentScenarioAsync("business-clock");
        await using var services = BuildResponsibilityServices(new FixedTimeProvider(BusinessNowUtc));
        var command = AssignCommand(scenario, "business-clock");
        var identity = AssignIdentity(scenario, command);

        var executed = await services.GetRequiredService<IAtomicUnitOfWork>()
            .ExecuteAsync(identity, command, AssignCodec);

        executed.Disposition.Should().Be(AtomicCommandDisposition.Executed);
        executed.Value.EffectiveFromUtc.Should().Be(BusinessNowUtc);
        executed.Value.AccessRevisions.Should().ContainSingle()
            .Which.Should().Be(new WorkspaceAccessRevisionExpectation(
                scenario.TechnicianAccessContextId,
                scenario.TechnicianAccessRevision + 1));

        _context.Db.ChangeTracker.Clear();
        var row = await (
            from responsibility in _context.Db.WorkOrderResponsibilities.AsNoTracking()
            join membership in _context.Db.WorkspaceMemberships.AsNoTracking()
                on responsibility.WorkspaceMembershipId equals membership.Id
            join context in _context.Db.WorkspaceAccessContexts.AsNoTracking()
                on membership.AccessContextId equals context.Id
            join audit in _context.Db.AtomicAuditLogs.AsNoTracking()
                on responsibility.WorkOrderId equals audit.EntityId
            join outbox in _context.Db.OutboxMessages.AsNoTracking()
                on responsibility.PortfolioId equals outbox.PortfolioId
            where responsibility.Id == executed.Value.ResponsibilityId
                && audit.EntityType == nameof(WorkOrderResponsibility)
                && audit.CommandType == identity.CommandType
                && audit.CommandIdempotencyKey == identity.IdempotencyKey
                && outbox.IdempotencyKey == $"work-order-responsibility:{command.DeliveryIdempotencyKey}"
            select new
            {
                responsibility.EffectiveFromUtc,
                responsibility.AssignedAtUtc,
                ContextUpdatedAtUtc = context.UpdatedAtUtc,
                context.AccessRevision,
                AuditTimestamp = audit.Timestamp,
                outbox.CreatedAtUtc,
                outbox.NextAttemptAtUtc,
            }).SingleAsync();

        row.EffectiveFromUtc.Should().Be(BusinessNowUtc);
        row.AssignedAtUtc.Should().Be(BusinessNowUtc);
        row.AuditTimestamp.Should().Be(BusinessNowUtc);
        row.CreatedAtUtc.Should().Be(BusinessNowUtc);
        row.NextAttemptAtUtc.Should().Be(BusinessNowUtc);
        row.AccessRevision.Should().Be(scenario.TechnicianAccessRevision + 1);
        row.ContextUpdatedAtUtc.Should().NotBe(BusinessNowUtc,
            "access-context revision metadata remains on the database/security clock");

        await using var replayServices = BuildResponsibilityServices(new FixedTimeProvider(BusinessNowUtc.AddDays(1)));
        var replayed = await replayServices.GetRequiredService<IAtomicUnitOfWork>()
            .ExecuteAsync(identity, command, AssignCodec);

        replayed.Disposition.Should().Be(AtomicCommandDisposition.Replayed);
        replayed.Value.Should().BeEquivalentTo(executed.Value);
        _context.Db.ChangeTracker.Clear();
        (await _context.Db.WorkOrderResponsibilities.AsNoTracking()
            .CountAsync(item => item.WorkOrderId == scenario.WorkOrderId))
            .Should().Be(1);
        (await _context.Db.AtomicAuditLogs.AsNoTracking()
            .CountAsync(item => item.CommandType == identity.CommandType &&
                item.CommandIdempotencyKey == identity.IdempotencyKey))
            .Should().Be(1);
        (await _context.Db.OutboxMessages.AsNoTracking()
            .CountAsync(item => item.IdempotencyKey == $"work-order-responsibility:{command.DeliveryIdempotencyKey}"))
            .Should().Be(1);
    }

    [Fact]
    public async Task AssignResponsibility_RollsBackLifecycleRevisionAuditReceiptAndOutbox_WhenOutboxInsertFails()
    {
        var scenario = await SeedResponsibilityAssignmentScenarioAsync("rollback");
        var failure = new ThrowOnOutboxInsertInterceptor();
        await using var services = BuildResponsibilityServices(new FixedTimeProvider(BusinessNowUtc), failure);
        var command = AssignCommand(scenario, "rollback");
        var identity = AssignIdentity(scenario, command);

        var act = async () => await services.GetRequiredService<IAtomicUnitOfWork>()
            .ExecuteAsync(identity, command, AssignCodec);

        await act.Should().ThrowAsync<DbUpdateException>()
            .Where(exception => exception.InnerException is InjectedOutboxFailure);

        _context.Db.ChangeTracker.Clear();
        (await _context.Db.WorkOrderResponsibilities.AsNoTracking()
            .CountAsync(item => item.WorkOrderId == scenario.WorkOrderId))
            .Should().Be(0);
        (await _context.Db.WorkspaceAccessContexts.AsNoTracking()
            .Where(item => item.Id == scenario.TechnicianAccessContextId)
            .Select(item => item.AccessRevision)
            .SingleAsync())
            .Should().Be(scenario.TechnicianAccessRevision);
        (await _context.Db.AtomicAuditLogs.AsNoTracking()
            .CountAsync(item => item.CommandType == identity.CommandType &&
                item.CommandIdempotencyKey == identity.IdempotencyKey))
            .Should().Be(0);
        (await _context.Db.AtomicCommandReceipts.AsNoTracking()
            .CountAsync(item => item.CommandType == identity.CommandType &&
                item.IdempotencyKey == identity.IdempotencyKey))
            .Should().Be(0);
        (await _context.Db.OutboxMessages.AsNoTracking()
            .CountAsync(item => item.IdempotencyKey == $"work-order-responsibility:{command.DeliveryIdempotencyKey}"))
            .Should().Be(0);
    }

    [Fact]
    public async Task CloseResponsibility_UsesBusinessClockForLifecycleAuditAndOutbox()
    {
        var scenario = await SeedResponsibilityAssignmentScenarioAsync("close-clock");
        await using var assignServices = BuildResponsibilityServices(new FixedTimeProvider(BusinessNowUtc));
        var assignCommand = AssignCommand(scenario, "close-clock-assign");
        var assigned = await assignServices.GetRequiredService<IAtomicUnitOfWork>()
            .ExecuteAsync(AssignIdentity(scenario, assignCommand), assignCommand, AssignCodec);
        var closeNow = BusinessNowUtc.AddHours(4);
        await using var closeServices = BuildResponsibilityServices(new FixedTimeProvider(closeNow));
        var closeCommand = new CloseWorkOrderResponsibilityCommand(
            scenario.PortfolioId,
            scenario.ManagerUserId,
            scenario.ManagerSessionId,
            scenario.ManagerAccessContextId,
            scenario.ManagerAccessRevision,
            scenario.WorkOrderId,
            assigned.Value.ResponsibilityId,
            [new WorkspaceAccessRevisionExpectation(
                scenario.TechnicianAccessContextId,
                scenario.TechnicianAccessRevision + 1)],
            "Unassign technician.",
            "close-clock-close");
        var closeIdentity = new AtomicCommandIdentity(
            "work-order-responsibility.close",
            $"{scenario.PortfolioId}:{scenario.WorkOrderId}:{closeCommand.DeliveryIdempotencyKey}");

        var closed = await closeServices.GetRequiredService<IAtomicUnitOfWork>()
            .ExecuteAsync(closeIdentity, closeCommand, CloseCodec);

        closed.Value.EffectiveToUtc.Should().Be(closeNow);
        _context.Db.ChangeTracker.Clear();
        var row = await (
            from responsibility in _context.Db.WorkOrderResponsibilities.AsNoTracking()
            join membership in _context.Db.WorkspaceMemberships.AsNoTracking()
                on responsibility.WorkspaceMembershipId equals membership.Id
            join context in _context.Db.WorkspaceAccessContexts.AsNoTracking()
                on membership.AccessContextId equals context.Id
            join audit in _context.Db.AtomicAuditLogs.AsNoTracking()
                on responsibility.WorkOrderId equals audit.EntityId
            join outbox in _context.Db.OutboxMessages.AsNoTracking()
                on responsibility.PortfolioId equals outbox.PortfolioId
            where responsibility.Id == assigned.Value.ResponsibilityId
                && audit.EntityType == nameof(WorkOrderResponsibility)
                && audit.CommandType == closeIdentity.CommandType
                && audit.CommandIdempotencyKey == closeIdentity.IdempotencyKey
                && outbox.IdempotencyKey == $"work-order-responsibility-close:{closeCommand.DeliveryIdempotencyKey}"
            select new
            {
                responsibility.EffectiveToUtc,
                responsibility.EndedAtUtc,
                ContextUpdatedAtUtc = context.UpdatedAtUtc,
                context.AccessRevision,
                AuditTimestamp = audit.Timestamp,
                outbox.CreatedAtUtc,
                outbox.NextAttemptAtUtc,
            }).SingleAsync();

        row.EffectiveToUtc.Should().Be(closeNow);
        row.EndedAtUtc.Should().Be(closeNow);
        row.AuditTimestamp.Should().Be(closeNow);
        row.CreatedAtUtc.Should().Be(closeNow);
        row.NextAttemptAtUtc.Should().Be(closeNow);
        row.AccessRevision.Should().Be(scenario.TechnicianAccessRevision + 2);
        row.ContextUpdatedAtUtc.Should().NotBe(closeNow,
            "access-context revision metadata remains on the database/security clock");
    }

    private async Task AssertDeniedAsync<TCommand, TResult>(
        AtomicCommandIdentity identity,
        TCommand command,
        IAtomicResultCodec<TResult> codec)
        where TCommand : notnull, IAtomicCommandData
        where TResult : notnull, IAtomicResultData
    {
        var act = async () => await Atomic.ExecuteAsync(identity, command, codec);
        await act.Should().ThrowAsync<UnauthorizedAccessException>();
    }

    private IAtomicUnitOfWork Atomic => _services.GetRequiredService<IAtomicUnitOfWork>();

    private ServiceProvider BuildResponsibilityServices(
        TimeProvider timeProvider,
        IInterceptor? interceptor = null)
    {
        var services = new ServiceCollection();
        services.AddSingleton(timeProvider);
        services.AddScoped<ICurrentActor, TestActor>();
        services.AddAtomicPersistenceKernel(allowUnconvertedWrites: true);
        services.AddAtomicCommandHandler<AssignWorkOrderResponsibilityCommand, AssignWorkOrderResponsibilityResult,
            AssignWorkOrderResponsibilityHandler>();
        services.AddAtomicCommandHandler<CloseWorkOrderResponsibilityCommand, CloseWorkOrderResponsibilityResult,
            CloseWorkOrderResponsibilityHandler>();
        services.AddDbContext<RentalCommandDbContext>((provider, builder) =>
        {
            builder.UseNpgsql(_context.ConnectionString)
                .UseAtomicPersistenceKernel(provider);
            if (interceptor is not null) builder.AddInterceptors(interceptor);
        });
        return services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true,
        });
    }

    private static AssignWorkOrderResponsibilityCommand AssignCommand(
        ResponsibilityAssignmentScenario scenario,
        string idempotencyKey) =>
        new(
            scenario.PortfolioId,
            scenario.ManagerUserId,
            scenario.ManagerSessionId,
            scenario.ManagerAccessContextId,
            scenario.ManagerAccessRevision,
            scenario.WorkOrderId,
            scenario.TechnicianMembershipId,
            scenario.TechnicianRoleAssignmentId,
            WorkOrderResponsibilityKind.Primary,
            null,
            [new WorkspaceAccessRevisionExpectation(
                scenario.TechnicianAccessContextId,
                scenario.TechnicianAccessRevision)],
            "Assign primary technician.",
            idempotencyKey);

    private static AtomicCommandIdentity AssignIdentity(
        ResponsibilityAssignmentScenario scenario,
        AssignWorkOrderResponsibilityCommand command) =>
        new(
            "work-order-responsibility.assign",
            $"{scenario.PortfolioId}:{scenario.WorkOrderId}:{command.DeliveryIdempotencyKey}");

    private async Task<ResponsibilityAssignmentScenario> SeedResponsibilityAssignmentScenarioAsync(string suffix)
    {
        var now = DateTime.UtcNow;
        var db = _context.Db;
        var property = new Property
        {
            PortfolioId = 1,
            Name = $"Responsibility Property {suffix}",
            AddressLine1 = "10 Responsibility Way",
            City = "Columbus",
            State = "OH",
            PostalCode = "43215",
            CreatedAt = now,
            UpdatedAt = now,
        };
        var tenant = new Tenant
        {
            PortfolioId = 1,
            FirstName = "Responsibility",
            LastName = $"Tenant {suffix}",
            Email = $"responsibility-{suffix}-{Guid.NewGuid():N}@example.test",
            CreatedAt = now,
            UpdatedAt = now,
        };
        var manager = User($"responsibility-manager-{suffix}");
        var technician = User($"responsibility-technician-{suffix}");
        db.AddRange(property, tenant, manager, technician);
        await db.SaveChangesAsync();

        var managerContext = AccessContext(manager.Id, now);
        var managerMembership = Membership(managerContext, now, WorkspaceExperience.Management);
        var managerAssignment = Assignment(
            managerMembership,
            RoleProfileKeys.WorkspaceAdministrator,
            MembershipRoleAssignmentScopeKind.AllProperties,
            now);
        var managerSession = Session(manager.Id, managerContext, now);

        var technicianContext = AccessContext(technician.Id, now);
        var technicianMembership = Membership(technicianContext, now, WorkspaceExperience.Maintenance);
        var technicianAssignment = Assignment(
            technicianMembership,
            RoleProfileKeys.MaintenanceTechnician,
            MembershipRoleAssignmentScopeKind.AssignedWorkOrders,
            now);
        db.AddRange(managerAssignment, managerSession, technicianAssignment);
        await db.SaveChangesAsync();

        var workOrder = WorkOrder(property.Id, tenant.Id, $"Responsibility repair {suffix}", now);
        db.WorkOrders.Add(workOrder);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        return new ResponsibilityAssignmentScenario(
            1,
            manager.Id,
            managerSession.Id,
            managerContext.Id,
            managerContext.AccessRevision,
            technicianContext.Id,
            technicianContext.AccessRevision,
            technicianMembership.Id,
            technicianAssignment.Id,
            workOrder.Id);
    }

    private static ApplicationUser User(string prefix)
    {
        var email = $"{prefix}-{Guid.NewGuid():N}@example.test";
        return new ApplicationUser
        {
            UserName = email,
            NormalizedUserName = email.ToUpperInvariant(),
            Email = email,
            NormalizedEmail = email.ToUpperInvariant(),
            DisplayName = prefix,
            SecurityStamp = Guid.NewGuid().ToString("N"),
            ConcurrencyStamp = Guid.NewGuid().ToString("N"),
            CreatedAt = DateTime.UtcNow,
        };
    }

    private static WorkspaceAccessContext AccessContext(int userId, DateTime now) => new()
    {
        UserId = userId,
        PortfolioId = 1,
        Status = WorkspaceAccessContextStatus.Active,
        CreatedAtUtc = now,
        UpdatedAtUtc = now,
    };

    private static WorkspaceMembership Membership(
        WorkspaceAccessContext context,
        DateTime now,
        WorkspaceExperience experience) => new()
    {
        AccessContext = context,
        PortfolioId = 1,
        Status = WorkspaceMembershipStatus.Active,
        DefaultExperience = experience,
        EffectiveFromUtc = now.AddHours(-1),
        CreatedAtUtc = now,
        UpdatedAtUtc = now,
    };

    private static MembershipRoleAssignment Assignment(
        WorkspaceMembership membership,
        string roleKey,
        MembershipRoleAssignmentScopeKind scopeKind,
        DateTime now) => new()
    {
        WorkspaceMembership = membership,
        PortfolioId = 1,
        RoleProfileId = AccessCatalog.Roles.Single(role => role.Key == roleKey).Id,
        Status = MembershipRoleAssignmentStatus.Active,
        ScopeKind = scopeKind,
        EffectiveFromUtc = now.AddHours(-1),
        CreatedAtUtc = now,
        UpdatedAtUtc = now,
    };

    private static AuthSession Session(int userId, WorkspaceAccessContext context, DateTime now) => new()
    {
        Id = Guid.NewGuid(),
        UserId = userId,
        ActiveAccessContext = context,
        Status = AuthSessionStatus.Active,
        CreatedAtUtc = now,
        LastSeenAtUtc = now,
        ExpiresAtUtc = now.AddDays(30),
    };

    private async Task<Scenario> SeedScenarioAsync()
    {
        var now = DateTime.UtcNow;
        var db = _context.Db;
        var user = new ApplicationUser
        {
            UserName = "assigned-technician@example.test",
            NormalizedUserName = "ASSIGNED-TECHNICIAN@EXAMPLE.TEST",
            Email = "assigned-technician@example.test",
            NormalizedEmail = "ASSIGNED-TECHNICIAN@EXAMPLE.TEST",
            DisplayName = "Assigned Technician",
            SecurityStamp = Guid.NewGuid().ToString("N"),
            ConcurrencyStamp = Guid.NewGuid().ToString("N"),
            CreatedAt = now,
        };
        var property = new Property
        {
            PortfolioId = 1,
            Name = "Technician Property",
            AddressLine1 = "1 Repair Way",
            City = "Columbus",
            State = "OH",
            PostalCode = "43215",
            CreatedAt = now,
            UpdatedAt = now,
        };
        var tenant = new Tenant
        {
            PortfolioId = 1,
            FirstName = "Assigned",
            LastName = "Tenant",
            Email = "assigned-technician-tenant@example.test",
            CreatedAt = now,
            UpdatedAt = now,
        };
        db.AddRange(user, property, tenant);
        await db.SaveChangesAsync();

        var context = new WorkspaceAccessContext
        {
            UserId = user.Id,
            PortfolioId = 1,
            Status = WorkspaceAccessContextStatus.Active,
            LastAuthorizedExperience = WorkspaceExperience.Maintenance,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
        };
        var membership = new WorkspaceMembership
        {
            AccessContext = context,
            PortfolioId = 1,
            Status = WorkspaceMembershipStatus.Active,
            DefaultExperience = WorkspaceExperience.Maintenance,
            EffectiveFromUtc = now.AddMinutes(-1),
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
        };
        var assignment = new MembershipRoleAssignment
        {
            WorkspaceMembership = membership,
            PortfolioId = 1,
            RoleProfileId = AccessCatalog.Roles.Single(role =>
                role.Key == RoleProfileKeys.MaintenanceTechnician).Id,
            Status = MembershipRoleAssignmentStatus.Active,
            ScopeKind = MembershipRoleAssignmentScopeKind.AssignedWorkOrders,
            EffectiveFromUtc = now.AddMinutes(-1),
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
        db.AddRange(assignment, session);
        await db.SaveChangesAsync();

        var assigned = WorkOrder(property.Id, tenant.Id, "Assigned repair", now);
        var unassigned = WorkOrder(property.Id, tenant.Id, "Unassigned repair", now);
        db.AddRange(assigned, unassigned);
        await db.SaveChangesAsync();
        db.WorkOrderResponsibilities.Add(new WorkOrderResponsibility
        {
            Id = Guid.NewGuid(),
            PortfolioId = 1,
            PropertyId = property.Id,
            WorkOrderId = assigned.Id,
            WorkspaceMembershipId = membership.Id,
            MembershipRoleAssignmentId = assignment.Id,
            Kind = WorkOrderResponsibilityKind.Primary,
            EffectiveFromUtc = now.AddSeconds(-1),
            AssignedByUserId = user.Id,
            AssignedByAccessContextId = context.Id,
            AssignedReason = "Integration proof assignment",
            AssignedAtUtc = now.AddSeconds(-1),
        });
        var conversation = new Conversation
        {
            PortfolioId = 1,
            TenantId = tenant.Id,
            PropertyId = property.Id,
            WorkOrderId = assigned.Id,
            Subject = "Assigned repair",
            CreatedAt = now,
            LastMessageAt = now,
            LastMessagePreview = "Office update",
            TechnicianUnreadCount = 2,
        };
        conversation.Messages.Add(new ConversationMessage
        {
            SenderRole = ConversationSenderRole.Landlord,
            Body = "Office update",
            CreatedAt = now,
        });
        db.Conversations.Add(conversation);
        await db.SaveChangesAsync();
        var persistedVersions = await db.WorkOrders.AsNoTracking()
            .Where(workOrder => workOrder.Id == assigned.Id || workOrder.Id == unassigned.Id)
            .Select(workOrder => new { workOrder.Id, workOrder.UpdatedAt })
            .ToDictionaryAsync(workOrder => workOrder.Id, workOrder => workOrder.UpdatedAt);
        return new Scenario(1, user.Id, session.Id, context.Id, context.AccessRevision,
            assigned.Id, persistedVersions[assigned.Id],
            unassigned.Id, persistedVersions[unassigned.Id], conversation.Id);
    }

    private static WorkOrder WorkOrder(int propertyId, int tenantId, string title, DateTime now) => new()
    {
        PortfolioId = 1,
        PropertyId = propertyId,
        TenantId = tenantId,
        Title = title,
        Description = title,
        Category = "General",
        Status = WorkOrderStatus.New,
        RequestedAt = now,
        UpdatedAt = now,
    };

    private sealed class TestActor : ICurrentActor
    {
        public int? UserId => 1;
        public string? ActorLabel => "integration:assigned-work-order";
        public string? IpAddress => "127.0.0.1";
    }

    private sealed record Scenario(
        int PortfolioId,
        int UserId,
        Guid SessionId,
        int AccessContextId,
        long AccessRevision,
        int AssignedWorkOrderId,
        DateTime AssignedUpdatedAt,
        int UnassignedWorkOrderId,
        DateTime UnassignedUpdatedAt,
        int ConversationId);

    private sealed record ResponsibilityAssignmentScenario(
        int PortfolioId,
        int ManagerUserId,
        Guid ManagerSessionId,
        int ManagerAccessContextId,
        long ManagerAccessRevision,
        int TechnicianAccessContextId,
        long TechnicianAccessRevision,
        int TechnicianMembershipId,
        int TechnicianRoleAssignmentId,
        int WorkOrderId);

    private sealed class FixedTimeProvider(DateTime utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(utcNow);
    }

    private sealed class ThrowOnOutboxInsertInterceptor : DbCommandInterceptor
    {
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("INSERT INTO \"OutboxMessages\"", StringComparison.OrdinalIgnoreCase))
            {
                throw new InjectedOutboxFailure();
            }

            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }
    }

    private sealed class InjectedOutboxFailure : Exception;
}

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class RoleAuthorityPostgreSqlCollection : ICollectionFixture<MigratedPostgreSqlFixture>
{
    public const string Name = "Role authority PostgreSQL";
}
