using FluentAssertions;
using Microsoft.EntityFrameworkCore;
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
}

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class RoleAuthorityPostgreSqlCollection : ICollectionFixture<MigratedPostgreSqlFixture>
{
    public const string Name = "Role authority PostgreSQL";
}
