using System.Buffers.Binary;
using System.Data.Common;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using RentalCommand.Api.Auth;
using RentalCommand.Api.Controllers;
using RentalCommand.Api.DTOs;
using RentalCommand.Api.Writes;
using RentalCommand.Core;
using RentalCommand.Core.Atomic;
using RentalCommand.Core.Authorization;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Interfaces;
using RentalCommand.Core.Operations;
using RentalCommand.Data;
using RentalCommand.Data.Atomic;
using RentalCommand.Data.Authorization;
using RentalCommand.Data.Operations;
using RentalCommand.TestCommon;
using Xunit;

namespace RentalCommand.IntegrationTests;

[Collection(RoleAuthorityPostgreSqlCollection.Name)]
public sealed class AssignedWorkOrderPostgreSqlTests : IAsyncLifetime
{
    private static readonly DateTime BusinessNowUtc = new(2027, 1, 25, 5, 0, 0, DateTimeKind.Utc);
    private readonly MigratedPostgreSqlFixture _fixture;
    private MigratedPostgreSqlTestContext _context = null!;
    private ServiceProvider _services = null!;
    private IServiceScope _serviceScope = null!;

    public AssignedWorkOrderPostgreSqlTests(MigratedPostgreSqlFixture fixture) => _fixture = fixture;

    public async Task InitializeAsync()
    {
        _context = await _fixture.CreateContextAsync();
        var services = new ServiceCollection();
        services.AddSingleton(TimeProvider.System);
        services.AddScoped<ICurrentActor, TestActor>();
        services.AddAtomicPersistenceKernel();
        services.AddScoped<IRequestWriteExecutor, RequestWriteExecutor>();
        services.AddDbContext<RentalCommandDbContext>((provider, builder) =>
            builder.UseNpgsql(_context.ConnectionString).UseAtomicPersistenceKernel(provider));
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
            BusinessNowUtc,
            "assigned-update-replay");
        var identity = new AtomicCommandIdentity(
            "assigned-work-order.update",
            $"{scenario.PortfolioId}:{scenario.AccessContextId}:{scenario.AssignedWorkOrderId}:assigned-update-replay");

        using var firstScope = _services.CreateScope();
        using var secondScope = _services.CreateScope();
        var outcomes = await Task.WhenAll(
            ExecuteAsync(firstScope.ServiceProvider, identity, command),
            ExecuteAsync(secondScope.ServiceProvider, identity, command));

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
            BusinessNowUtc,
            "unassigned-update-denied");
        var denied = async () => await ExecuteAsync(_serviceScope.ServiceProvider,
            new AtomicCommandIdentity(
                "assigned-work-order.update",
                $"{scenario.PortfolioId}:{scenario.AccessContextId}:{scenario.UnassignedWorkOrderId}:unassigned-update-denied"),
            unassigned);

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

        await ExecuteAsync(_serviceScope.ServiceProvider, new AtomicCommandIdentity(
            "technician-work-entry.record", $"{scenario.PortfolioId}:{scenario.AssignedWorkOrderId}:assigned-entry"),
            entry);
        await ExecuteAsync(_serviceScope.ServiceProvider, new AtomicCommandIdentity(
            "technician-assignment-message.send", $"{scenario.PortfolioId}:{scenario.AssignedWorkOrderId}:assigned-message"),
            message);
        await ExecuteAsync(_serviceScope.ServiceProvider, new AtomicCommandIdentity(
            "technician-assignment-conversation.read", $"{scenario.PortfolioId}:{scenario.AssignedWorkOrderId}:assigned-read"),
            read);

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

        await AssertDeniedAsync(() => ExecuteAsync(_serviceScope.ServiceProvider, new AtomicCommandIdentity(
                "technician-work-entry.record", $"{scenario.PortfolioId}:{scenario.UnassignedWorkOrderId}:unassigned-entry"),
            entry with { WorkOrderId = scenario.UnassignedWorkOrderId, DeliveryIdempotencyKey = "unassigned-entry" }));
        await AssertDeniedAsync(() => ExecuteAsync(_serviceScope.ServiceProvider, new AtomicCommandIdentity(
                "technician-assignment-message.send", $"{scenario.PortfolioId}:{scenario.UnassignedWorkOrderId}:unassigned-message"),
            message with { WorkOrderId = scenario.UnassignedWorkOrderId, DeliveryIdempotencyKey = "unassigned-message" }));
        await AssertDeniedAsync(() => ExecuteAsync(_serviceScope.ServiceProvider, new AtomicCommandIdentity(
                "technician-assignment-conversation.read", $"{scenario.PortfolioId}:{scenario.UnassignedWorkOrderId}:unassigned-read"),
            read with { WorkOrderId = scenario.UnassignedWorkOrderId, DeliveryIdempotencyKey = "unassigned-read" }));
        await AssertDeniedAsync(() => ExecuteAsync(_serviceScope.ServiceProvider, new AtomicCommandIdentity(
                "technician-work-entry.record", $"{scenario.PortfolioId}:{scenario.AssignedWorkOrderId}:stale-entry"),
            entry with
            {
                ActorAccessRevision = scenario.AccessRevision + 1,
                DeliveryIdempotencyKey = "stale-entry",
            }));

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
    public async Task SixResponsibilityAndTechnicianContracts_ReplayFrozenLegacyReceipts()
    {
        const string updateHeader = "frozen-update";
        const string entryHeader = "frozen-entry";
        const string messageHeader = "frozen-message";
        const string readHeader = "frozen-read";
        const string assignHeader = "frozen-assign";
        const string closeHeader = "frozen-close";
        // Frozen base-caller digests calculated once from the normalized headers; never regenerate.
        const string updateDigest = "91b9e3a544e0882574b44c966da585955eb8afe265a6a3833ec4cfd263cdce7d";
        const string entryDigest = "fce3b6dccd983e26059659a59e133988295737574eeb743ccd79829161dc8576";
        const string messageDigest = "c938057c287ea26a3ee6f3e8a7c91b03e471f6c8f8723a6019bfb8c35436e18e";
        const string readDigest = "122e87537461871c2ab5446425d52ef067f668e600d2c43e107fba0efe9e1515";
        const string assignDigest = "b92e85222f2ab7f39d4701165318dd360ee930f58e8d19d51594d430eab882d5";
        const string closeDigest = "26d1c15ff85e945d8ac80eb1ce354be594d6170894b68332048df826f0c034ff";
        var technician = await SeedScenarioAsync(
            sessionId: Guid.Parse("55555555-5555-5555-5555-555555555555"));
        var management = await SeedResponsibilityAssignmentScenarioAsync("frozen-replay");
        var updateKey = $"{technician.PortfolioId}:{technician.AssignedWorkOrderId}:{updateDigest}";
        var entryKey = $"{technician.PortfolioId}:{technician.AssignedWorkOrderId}:{entryDigest}";
        var messageKey = $"{technician.PortfolioId}:{technician.AssignedWorkOrderId}:{messageDigest}";
        var readKey = $"{technician.PortfolioId}:{technician.AssignedWorkOrderId}:{readDigest}";
        var assignKey = $"{management.PortfolioId}:{management.WorkOrderId}:{assignDigest}";
        var closeKey = $"{management.PortfolioId}:{management.WorkOrderId}:{closeDigest}";
        var update = new UpdateAssignedWorkOrderCommand(
            technician.PortfolioId, technician.UserId, technician.SessionId,
            technician.AccessContextId, technician.AccessRevision, technician.AssignedWorkOrderId,
            BusinessNowUtc, WorkOrderStatus.InProgress, "Frozen replay", null, null,
            null, BusinessNowUtc, "frozen-update");
        var entry = new RecordTechnicianWorkEntryCommand(
            technician.PortfolioId, technician.UserId, technician.SessionId,
            technician.AccessContextId, technician.AccessRevision, technician.AssignedWorkOrderId,
            TechnicianWorkEntryKind.Note, "Frozen replay", null, null, null, BusinessNowUtc,
            "frozen-entry");
        var message = new SendTechnicianAssignmentMessageCommand(
            technician.PortfolioId, technician.UserId, technician.SessionId,
            technician.AccessContextId, technician.AccessRevision, technician.AssignedWorkOrderId,
            "Frozen replay", "frozen-message");
        var read = new MarkTechnicianAssignmentConversationReadCommand(
            technician.PortfolioId, technician.UserId, technician.SessionId,
            technician.AccessContextId, technician.AccessRevision, technician.AssignedWorkOrderId,
            "frozen-read");
        var assign = AssignCommand(management, "frozen-assign");
        var responsibilityId = Guid.Parse("33333333-3333-3333-3333-333333333333");
        var close = new CloseWorkOrderResponsibilityCommand(
            management.PortfolioId, management.ManagerUserId, management.ManagerSessionId,
            management.ManagerAccessContextId, management.ManagerAccessRevision,
            management.WorkOrderId, responsibilityId,
            [new WorkspaceAccessRevisionExpectation(
                management.TechnicianAccessContextId, management.TechnicianAccessRevision)],
            "Frozen close", BusinessNowUtc, "frozen-close");

        new IAtomicCommandData[] { update, entry, message, read, assign, close }
            .Select(AtomicCommandFingerprint.Create).Should().Equal(
                "6e9afb75e2d8559ff80824780f2347ad8c8eb06215783049341dabd6109093d0",
                "767b8f05917dee7e29811b5eefadadd80659649e482021a8d5eefd182ad3e626",
                "939245342ed9615b99d5d7e73f90a691e5ff4e2c2e4e5f456d66d59d252e6c6d",
                "b90a96514b3ceb79a090d195124e61602b65e373b89051503ee440a7f1f49bf2",
                "c4989830c246677e3a6ca51198f42d43ee70a5c438b6278d74ec9953ec3b77ee",
                "5ba51b11d61a784d98f2d6cc32629638d477a36db1ae9503c0c8c32d4a508b10");

        await SeedFrozenReceiptAsync(
            updateKey,
            "assigned-work-order.update", "6e9afb75e2d8559ff80824780f2347ad8c8eb06215783049341dabd6109093d0", "assigned-work-order.update.v1",
            $$"""{"Outcome":0,"WorkOrderId":{{technician.AssignedWorkOrderId}},"Status":1,"ScheduledForUtc":null,"ScheduledWindowEndUtc":null,"CompletedAtUtc":null,"UpdatedAtUtc":"2099-08-21T12:34:56Z"}""");
        await SeedFrozenReceiptAsync(
            entryKey,
            "technician-work-entry.record", "767b8f05917dee7e29811b5eefadadd80659649e482021a8d5eefd182ad3e626", "technician-work-entry.v1",
            $$"""{"EntryId":42,"WorkOrderId":{{technician.AssignedWorkOrderId}},"Kind":0,"CreatedAtUtc":"2099-08-21T12:34:56Z"}""");
        await SeedFrozenReceiptAsync(
            messageKey,
            "technician-assignment-message.send", "939245342ed9615b99d5d7e73f90a691e5ff4e2c2e4e5f456d66d59d252e6c6d", "technician-assignment-message.v1",
            """{"ConversationId":43,"MessageId":44,"CreatedAtUtc":"2099-08-21T12:34:56Z"}""");
        await SeedFrozenReceiptAsync(
            readKey,
            "technician-assignment-conversation.read", "b90a96514b3ceb79a090d195124e61602b65e373b89051503ee440a7f1f49bf2", "technician-assignment-conversation-read.v1",
            """{"ConversationId":43,"Found":true}""");

        await using var managementServices = BuildResponsibilityServices(new FixedTimeProvider(BusinessNowUtc));
        using var managementScope = managementServices.CreateScope();
        await SeedFrozenReceiptAsync(
            assignKey,
            "work-order-responsibility.assign", "c4989830c246677e3a6ca51198f42d43ee70a5c438b6278d74ec9953ec3b77ee", "work-order-responsibility.assign.v1",
            $$"""{"ResponsibilityId":"33333333-3333-3333-3333-333333333333","WorkOrderId":{{management.WorkOrderId}},"WorkspaceMembershipId":{{management.TechnicianMembershipId}},"MembershipRoleAssignmentId":{{management.TechnicianRoleAssignmentId}},"Kind":0,"EffectiveFromUtc":"2099-08-21T12:34:56Z","AccessRevisions":[{"AccessContextId":{{management.TechnicianAccessContextId}},"ExpectedRevision":{{management.TechnicianAccessRevision + 1}}}]}""");
        await SeedFrozenReceiptAsync(
            closeKey,
            "work-order-responsibility.close", "5ba51b11d61a784d98f2d6cc32629638d477a36db1ae9503c0c8c32d4a508b10", "work-order-responsibility.close.v1",
            $$"""{"ResponsibilityId":"33333333-3333-3333-3333-333333333333","WorkOrderId":{{management.WorkOrderId}},"EffectiveToUtc":"2099-08-21T12:34:56Z","AccessRevisions":[{"AccessContextId":{{management.TechnicianAccessContextId}},"ExpectedRevision":{{management.TechnicianAccessRevision + 1}}}]}""");

        var technicianController = TechnicianControllerFor(
            _serviceScope.ServiceProvider, technician, new FixedTimeProvider(BusinessNowUtc));
        var updated = OkPayload(await technicianController.UpdateAssignment(
            technician.AssignedWorkOrderId, $" {updateHeader} ", new UpdateAssignedWorkOrderRequest
            {
                ExpectedUpdatedAtUtc = BusinessNowUtc,
                Status = WorkOrderStatus.InProgress,
                TechnicianNote = "Frozen replay",
            }, default));
        var recorded = OkPayload(await technicianController.RecordEntry(
            technician.AssignedWorkOrderId, entryHeader, new RecordTechnicianWorkEntryRequest
            {
                Kind = TechnicianWorkEntryKind.Note,
                Note = "Frozen replay",
                OccurredAt = BusinessNowUtc,
            }, default));
        var sent = OkPayload(await technicianController.SendMessage(
            technician.AssignedWorkOrderId, messageHeader,
            new TechnicianConversationMessageRequest { Body = "Frozen replay" }, default));
        var marked = OkPayload(await technicianController.MarkConversationRead(
            technician.AssignedWorkOrderId, readHeader, default));

        var managementController = ResponsibilityControllerFor(managementScope.ServiceProvider, management);
        var assigned = OkPayload(await managementController.Assign(
            management.WorkOrderId, assignHeader, new AssignWorkOrderResponsibilityRequest
            {
                WorkspaceMembershipId = management.TechnicianMembershipId,
                MembershipRoleAssignmentId = management.TechnicianRoleAssignmentId,
                Kind = WorkOrderResponsibilityKind.Primary,
                AccessRevisionExpectations =
                [new(management.TechnicianAccessContextId, management.TechnicianAccessRevision)],
                Reason = "Assign primary technician.",
            }, default));
        var closed = OkPayload(await managementController.Close(
            management.WorkOrderId, responsibilityId, closeHeader,
            new CloseWorkOrderResponsibilityRequest
            {
                AccessRevisionExpectations =
                [new(management.TechnicianAccessContextId, management.TechnicianAccessRevision)],
                Reason = "Frozen close",
            }, default));

        updated.GetProperty("replayed").GetBoolean().Should().BeTrue();
        updated.GetProperty("Value").GetProperty("UpdatedAtUtc").GetDateTime()
            .Should().Be(new DateTime(2099, 8, 21, 12, 34, 56, DateTimeKind.Utc));
        recorded.GetProperty("Value").GetProperty("EntryId").GetInt32().Should().Be(42);
        sent.GetProperty("Value").GetProperty("MessageId").GetInt32().Should().Be(44);
        marked.GetProperty("Value").GetProperty("ConversationId").GetInt32().Should().Be(43);
        marked.GetProperty("Value").GetProperty("Found").GetBoolean().Should().BeTrue();
        assigned.GetProperty("Value").GetProperty("ResponsibilityId").GetGuid().Should().Be(responsibilityId);
        closed.GetProperty("Value").GetProperty("ResponsibilityId").GetGuid().Should().Be(responsibilityId);
        new[] { updated, recorded, sent, marked, assigned, closed }
            .Should().OnlyContain(payload => payload.GetProperty("replayed").GetBoolean());

        _context.Db.ChangeTracker.Clear();
        (await _context.Db.WorkOrderStatusEvents.AsNoTracking().CountAsync(row =>
            row.WorkOrderId == technician.AssignedWorkOrderId)).Should().Be(0);
        (await _context.Db.TechnicianWorkEntries.AsNoTracking().CountAsync(row =>
            row.WorkOrderId == technician.AssignedWorkOrderId)).Should().Be(0);
        (await _context.Db.ConversationMessages.AsNoTracking().CountAsync(row =>
            row.ConversationId == technician.ConversationId)).Should().Be(1);
        (await _context.Db.Conversations.AsNoTracking().CountAsync(row =>
            row.Id == technician.ConversationId && row.TechnicianUnreadCount == 0)).Should().Be(0);
        (await _context.Db.WorkOrderResponsibilities.AsNoTracking().CountAsync(row =>
            row.WorkOrderId == management.WorkOrderId)).Should().Be(0);
        (await _context.Db.AtomicAuditLogs.AsNoTracking().CountAsync()).Should().Be(0);
        (await _context.Db.OutboxMessages.AsNoTracking().CountAsync()).Should().Be(0);
        var frozenKeys = new[] { updateKey, entryKey, messageKey, readKey, assignKey, closeKey };
        (await _context.Db.AtomicCommandReceipts.AsNoTracking().CountAsync(row =>
            frozenKeys.Contains(row.IdempotencyKey))).Should().Be(6);
    }

    [Fact]
    public async Task AssignResponsibility_UsesBusinessClockForLifecycleAuditAndOutbox_AndReplaysOriginalResult()
    {
        var scenario = await SeedResponsibilityAssignmentScenarioAsync("business-clock");
        await using var services = BuildResponsibilityServices(new FixedTimeProvider(BusinessNowUtc));
        using var serviceScope = services.CreateScope();
        var command = AssignCommand(scenario, "business-clock");
        var identity = AssignIdentity(scenario, command);

        var executed = await ExecuteAsync(serviceScope.ServiceProvider, identity, command);

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
        using var replayScope = replayServices.CreateScope();
        var replayed = await ExecuteAsync(replayScope.ServiceProvider, identity, command);

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
        using var serviceScope = services.CreateScope();
        var command = AssignCommand(scenario, "rollback");
        var identity = AssignIdentity(scenario, command);

        var act = async () => await ExecuteAsync(serviceScope.ServiceProvider, identity, command);

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
        using var assignScope = assignServices.CreateScope();
        var assignCommand = AssignCommand(scenario, "close-clock-assign");
        var assigned = await ExecuteAsync(
            assignScope.ServiceProvider, AssignIdentity(scenario, assignCommand), assignCommand);
        var closeNow = BusinessNowUtc.AddHours(4);
        await using var closeServices = BuildResponsibilityServices(new FixedTimeProvider(closeNow));
        using var closeScope = closeServices.CreateScope();
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
            closeNow,
            "close-clock-close");
        var closeIdentity = new AtomicCommandIdentity(
            "work-order-responsibility.close",
            $"{scenario.PortfolioId}:{scenario.WorkOrderId}:{closeCommand.DeliveryIdempotencyKey}");

        var closed = await ExecuteAsync(closeScope.ServiceProvider, closeIdentity, closeCommand);

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

    [Fact]
    public async Task ResponsibilityMutations_ReversedActorAndAffectedContextsCompleteWithoutDeadlock()
    {
        var scenario = await SeedReversedResponsibilityScenarioAsync();
        var firstProbe = new ResponsibilityLockProbe(
            scenario.WorkOrderId,
            scenario.FirstActorAccessContextId,
            pauseAfterWorkOrder: true);
        var secondProbe = new ResponsibilityLockProbe(
            scenario.WorkOrderId,
            scenario.SecondActorAccessContextId,
            pauseAfterWorkOrder: false);
        await using var firstServices = BuildResponsibilityServices(
            new FixedTimeProvider(BusinessNowUtc), firstProbe);
        await using var secondServices = BuildResponsibilityServices(
            new FixedTimeProvider(BusinessNowUtc), secondProbe);
        using var firstScope = firstServices.CreateScope();
        using var secondScope = secondServices.CreateScope();

        var firstCommand = new AssignWorkOrderResponsibilityCommand(
            scenario.PortfolioId,
            scenario.FirstActorUserId,
            scenario.FirstActorSessionId,
            scenario.FirstActorAccessContextId,
            scenario.FirstActorAccessRevision,
            scenario.WorkOrderId,
            scenario.SecondTargetMembershipId,
            scenario.SecondTargetRoleAssignmentId,
            WorkOrderResponsibilityKind.Supporting,
            null,
            [new WorkspaceAccessRevisionExpectation(
                scenario.SecondActorAccessContextId,
                scenario.SecondActorAccessRevision)],
            "First reversed-context assignment.",
            BusinessNowUtc,
            "reversed-context-first");
        var secondCommand = new AssignWorkOrderResponsibilityCommand(
            scenario.PortfolioId,
            scenario.SecondActorUserId,
            scenario.SecondActorSessionId,
            scenario.SecondActorAccessContextId,
            scenario.SecondActorAccessRevision + 1,
            scenario.WorkOrderId,
            scenario.FirstTargetMembershipId,
            scenario.FirstTargetRoleAssignmentId,
            WorkOrderResponsibilityKind.Supporting,
            null,
            [new WorkspaceAccessRevisionExpectation(
                scenario.FirstActorAccessContextId,
                scenario.FirstActorAccessRevision)],
            "Second reversed-context assignment.",
            BusinessNowUtc,
            "reversed-context-second");

        var firstTask = Task.Run(() => ExecuteAsync(firstScope.ServiceProvider,
            new AtomicCommandIdentity(
                "work-order-responsibility.assign",
                "reversed-context-first"),
            firstCommand));
        try
        {
            await firstProbe.WorkOrderLockReached.WaitAsync(TimeSpan.FromSeconds(10));
        }
        catch (TimeoutException)
        {
            throw new InvalidOperationException("First responsibility work-order lock was not reached.");
        }

        var secondTask = Task.Run(() => ExecuteAsync(secondScope.ServiceProvider,
            new AtomicCommandIdentity(
                "work-order-responsibility.assign",
                "reversed-context-second"),
            secondCommand));
        // With the unfixed order, the second command owns the reversed actor context while it waits
        // on the work order. The fixed order makes it wait for the first command's lowest context.
        _ = await Task.WhenAny(secondProbe.ActorContextLockReached, Task.Delay(TimeSpan.FromSeconds(2)));

        firstProbe.ReleaseWorkOrder();
        await Task.WhenAll(firstTask, secondTask).WaitAsync(TimeSpan.FromSeconds(15));

        firstTask.Result.Disposition.Should().Be(AtomicCommandDisposition.Executed);
        secondTask.Result.Disposition.Should().Be(AtomicCommandDisposition.Executed);
        (await _context.Db.WorkOrderResponsibilities.AsNoTracking()
            .CountAsync(row => row.WorkOrderId == scenario.WorkOrderId &&
                row.EffectiveToUtc == null)).Should().Be(2);
    }

    [Fact]
    public async Task AssignResponsibilityChangedSetRejectsWith409_AndRollsBackAllState()
    {
        var scenario = await SeedResponsibilityAssignmentScenarioAsync("changed-set-409");
        var setupNow = DateTime.UtcNow;
        var thirdUser = User("responsibility-third-changed-set");
        _context.Db.Add(thirdUser);
        await _context.Db.SaveChangesAsync();
        var thirdContext = AccessContext(thirdUser.Id, setupNow);
        _context.Db.Add(thirdContext);
        await _context.Db.SaveChangesAsync();
        var technicianMembershipId = scenario.TechnicianMembershipId;

        var probe = new ResponsibilityChangedSetProbe();
        await using var services = BuildResponsibilityServices(
            new FixedTimeProvider(BusinessNowUtc), probe);
        using var scope = services.CreateScope();
        var command = new AssignWorkOrderResponsibilityCommand(
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
                thirdContext.Id,
                thirdContext.AccessRevision)],
            "Assign primary technician.",
            BusinessNowUtc,
            "changed-set-409");
        var identity = AssignIdentity(scenario, command);

        var task = Task.Run(() => ExecuteAsync(scope.ServiceProvider, identity, command));
        try
        {
            await probe.FirstContextLockReached.WaitAsync(TimeSpan.FromSeconds(10));

            await _context.Db.WorkspaceMemberships
                .Where(item => item.Id == technicianMembershipId)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(item => item.AccessContextId, thirdContext.Id));

            probe.Release();
            var conflict = await Assert.ThrowsAsync<DomainValidationException>(async () => await task);
            conflict.StatusCode.Should().Be(409);
            conflict.Message.Should().Be("Affected responsibilities changed; refresh before retrying.");
        }
        finally
        {
            probe.Release();
            await _context.Db.WorkspaceMemberships
                .Where(item => item.Id == technicianMembershipId)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(item => item.AccessContextId, scenario.TechnicianAccessContextId));
            _context.Db.ChangeTracker.Clear();
        }

        (await _context.Db.WorkOrderResponsibilities.AsNoTracking()
            .CountAsync(item => item.WorkOrderId == scenario.WorkOrderId)).Should().Be(0);
        (await _context.Db.WorkspaceAccessContexts.AsNoTracking()
            .Where(item => item.Id == scenario.ManagerAccessContextId ||
                item.Id == scenario.TechnicianAccessContextId)
            .Select(item => new { item.Id, item.AccessRevision })
            .ToListAsync())
            .Should().BeEquivalentTo([
                new { Id = scenario.ManagerAccessContextId, AccessRevision = scenario.ManagerAccessRevision },
                new { Id = scenario.TechnicianAccessContextId, AccessRevision = scenario.TechnicianAccessRevision },
            ]);
        (await _context.Db.AtomicAuditLogs.AsNoTracking()
            .CountAsync(item => item.CommandIdempotencyKey == identity.IdempotencyKey)).Should().Be(0);
        (await _context.Db.OutboxMessages.AsNoTracking()
            .CountAsync(item => item.IdempotencyKey.Contains(identity.IdempotencyKey))).Should().Be(0);
        (await _context.Db.AtomicCommandReceipts.AsNoTracking()
            .CountAsync(item => item.CommandType == identity.CommandType &&
                item.IdempotencyKey == identity.IdempotencyKey)).Should().Be(0);
    }

    [Fact]
    public async Task CloseResponsibilityContextChangeRejectsWith409_AndRollsBackAllState()
    {
        var scenario = await SeedResponsibilityAssignmentScenarioAsync("context-changed-409");
        await using var assignServices = BuildResponsibilityServices(new FixedTimeProvider(BusinessNowUtc));
        var assignCommand = AssignCommand(scenario, "context-changed-409-assign");
        AssignWorkOrderResponsibilityResult assigned;
        using (var assignScope = assignServices.CreateScope())
        {
            assigned = (await ExecuteAsync(
                assignScope.ServiceProvider, AssignIdentity(scenario, assignCommand), assignCommand)).Value;
        }
        var responsibilityId = assigned.ResponsibilityId;
        var expectedTechnicianRevision = assigned.AccessRevisions.Single().ExpectedRevision;
        var setupNow = DateTime.UtcNow;
        var thirdUser = User("responsibility-third-context-change");
        _context.Db.Add(thirdUser);
        await _context.Db.SaveChangesAsync();
        var thirdContext = AccessContext(thirdUser.Id, setupNow);
        _context.Db.Add(thirdContext);
        await _context.Db.SaveChangesAsync();

        var probe = new ResponsibilityLockProbe(
            scenario.WorkOrderId,
            scenario.ManagerAccessContextId,
            pauseAfterWorkOrder: true);
        await using var services = BuildResponsibilityServices(
            new FixedTimeProvider(BusinessNowUtc), probe);
        using var scope = services.CreateScope();
        var identity = new AtomicCommandIdentity(
            "work-order-responsibility.close",
            "context-changed-409-close");
        var command = new CloseWorkOrderResponsibilityCommand(
            scenario.PortfolioId,
            scenario.ManagerUserId,
            scenario.ManagerSessionId,
            scenario.ManagerAccessContextId,
            scenario.ManagerAccessRevision,
            scenario.WorkOrderId,
            responsibilityId,
            [new WorkspaceAccessRevisionExpectation(
                scenario.TechnicianAccessContextId,
                expectedTechnicianRevision)],
            "Close changed responsibility context.",
            BusinessNowUtc,
            identity.IdempotencyKey);

        var task = Task.Run(() => ExecuteAsync(scope.ServiceProvider, identity, command));
        try
        {
            await probe.WorkOrderLockReached.WaitAsync(TimeSpan.FromSeconds(10));
            _context.Db.ChangeTracker.Clear();
            await _context.Db.WorkspaceMemberships
                .Where(item => item.Id == scenario.TechnicianMembershipId)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(item => item.AccessContextId, thirdContext.Id));

            probe.ReleaseWorkOrder();
            var conflict = await Assert.ThrowsAsync<DomainValidationException>(async () => await task);
            conflict.StatusCode.Should().Be(409);
            conflict.Message.Should().Be("The responsibility context changed; refresh before retrying.");
        }
        finally
        {
            probe.ReleaseWorkOrder();
            await _context.Db.WorkspaceMemberships
                .Where(item => item.Id == scenario.TechnicianMembershipId)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(item => item.AccessContextId, scenario.TechnicianAccessContextId));
            _context.Db.ChangeTracker.Clear();
        }

        var responsibility = await _context.Db.WorkOrderResponsibilities.AsNoTracking()
            .SingleAsync(item => item.Id == responsibilityId);
        responsibility.WorkspaceMembershipId.Should().Be(scenario.TechnicianMembershipId);
        responsibility.EffectiveToUtc.Should().BeNull();
        responsibility.EndedAtUtc.Should().BeNull();
        var closeRevisions = await _context.Db.WorkspaceAccessContexts.AsNoTracking()
            .Where(item => item.Id == scenario.TechnicianAccessContextId ||
                item.Id == scenario.ManagerAccessContextId)
            .Select(item => new { item.Id, item.AccessRevision })
            .ToDictionaryAsync(item => item.Id, item => item.AccessRevision);
        closeRevisions[scenario.TechnicianAccessContextId].Should().Be(expectedTechnicianRevision);
        closeRevisions[scenario.ManagerAccessContextId].Should().Be(scenario.ManagerAccessRevision);
        (await _context.Db.AtomicAuditLogs.AsNoTracking()
            .CountAsync(item => item.CommandIdempotencyKey == identity.IdempotencyKey)).Should().Be(0);
        (await _context.Db.OutboxMessages.AsNoTracking()
            .CountAsync(item => item.IdempotencyKey.Contains(identity.IdempotencyKey))).Should().Be(0);
        (await _context.Db.AtomicCommandReceipts.AsNoTracking()
            .CountAsync(item => item.CommandType == identity.CommandType &&
                item.IdempotencyKey == identity.IdempotencyKey)).Should().Be(0);
    }

    private static async Task AssertDeniedAsync(Func<Task> execute)
    {
        var act = async () => await execute();
        await act.Should().ThrowAsync<UnauthorizedAccessException>();
    }

    private async Task SeedFrozenReceiptAsync(
        string key,
        string operation,
        string frozenFingerprint,
        string resultContract,
        string literalResultJson)
    {
        _context.Db.AtomicCommandReceipts.Add(new AtomicCommandReceipt
        {
            Id = Guid.NewGuid(),
            AttemptId = Guid.NewGuid(),
            CommandType = operation,
            IdempotencyKey = key,
            RequestFingerprint = frozenFingerprint,
            Status = AtomicCommandReceiptStatus.Completed,
            ResultContract = resultContract,
            ResultJson = literalResultJson,
            StartedAt = BusinessNowUtc,
            CompletedAt = BusinessNowUtc,
        });
        await _context.Db.SaveChangesAsync();
        _context.Db.ChangeTracker.Clear();
    }

    private static TechnicianController TechnicianControllerFor(
        IServiceProvider services,
        Scenario scenario,
        TimeProvider timeProvider)
    {
        var controller = new TechnicianController(
            null!,
            services.GetRequiredService<RentalCommandDbContext>(),
            services.GetRequiredService<IRequestWriteExecutor>(),
            timeProvider);
        controller.ControllerContext = ControllerContextFor(new ActiveAccessContext(
            scenario.SessionId, scenario.UserId, scenario.AccessContextId, scenario.PortfolioId,
            scenario.AccessRevision, WorkspaceExperience.Maintenance, null, WorkspaceExperience.Maintenance));
        return controller;
    }

    private static WorkOrderResponsibilityController ResponsibilityControllerFor(
        IServiceProvider services,
        ResponsibilityAssignmentScenario scenario)
    {
        var controller = new WorkOrderResponsibilityController(
            services.GetRequiredService<RentalCommandDbContext>(),
            services.GetRequiredService<IRequestWriteExecutor>(),
            services.GetRequiredService<WorkOrderResponsibilityAccessRevisionGuard>(),
            services.GetRequiredService<TimeProvider>());
        controller.ControllerContext = ControllerContextFor(new ActiveAccessContext(
            scenario.ManagerSessionId, scenario.ManagerUserId, scenario.ManagerAccessContextId,
            scenario.PortfolioId, scenario.ManagerAccessRevision, WorkspaceExperience.Management,
            null, WorkspaceExperience.Management));
        return controller;
    }

    private static ControllerContext ControllerContextFor(ActiveAccessContext active)
    {
        var http = new DefaultHttpContext();
        http.Items[CanonicalAccessContextHttpItem.Key] = active;
        return new ControllerContext { HttpContext = http };
    }

    private static JsonElement OkPayload(IActionResult result)
    {
        var value = result.Should().BeOfType<OkObjectResult>().Which.Value;
        return JsonSerializer.SerializeToElement(value);
    }

    private static Task<AtomicCommandOutcome<AssignWorkOrderResponsibilityResult>> ExecuteAsync(
        IServiceProvider services,
        AtomicCommandIdentity identity,
        AssignWorkOrderResponsibilityCommand command)
    {
        var db = services.GetRequiredService<RentalCommandDbContext>();
        var guard = services.GetRequiredService<WorkOrderResponsibilityAccessRevisionGuard>();
        return services.GetRequiredService<IWriteExecutor>()
            .ExecuteAsync(identity.IdempotencyKey, AssignWorkOrderResponsibilityRule.Write(command, db, guard));
    }

    private static Task<AtomicCommandOutcome<CloseWorkOrderResponsibilityResult>> ExecuteAsync(
        IServiceProvider services,
        AtomicCommandIdentity identity,
        CloseWorkOrderResponsibilityCommand command)
    {
        var db = services.GetRequiredService<RentalCommandDbContext>();
        var guard = services.GetRequiredService<WorkOrderResponsibilityAccessRevisionGuard>();
        return services.GetRequiredService<IWriteExecutor>()
            .ExecuteAsync(identity.IdempotencyKey, CloseWorkOrderResponsibilityRule.Write(command, db, guard));
    }

    private static Task<AtomicCommandOutcome<UpdateAssignedWorkOrderResult>> ExecuteAsync(
        IServiceProvider services,
        AtomicCommandIdentity identity,
        UpdateAssignedWorkOrderCommand command)
    {
        var db = services.GetRequiredService<RentalCommandDbContext>();
        return services.GetRequiredService<IWriteExecutor>()
            .ExecuteAsync(identity.IdempotencyKey, UpdateAssignedWorkOrderRule.Write(command, db));
    }

    private static Task<AtomicCommandOutcome<RecordTechnicianWorkEntryResult>> ExecuteAsync(
        IServiceProvider services,
        AtomicCommandIdentity identity,
        RecordTechnicianWorkEntryCommand command)
    {
        var db = services.GetRequiredService<RentalCommandDbContext>();
        return services.GetRequiredService<IWriteExecutor>()
            .ExecuteAsync(identity.IdempotencyKey, RecordTechnicianWorkEntryRule.Write(command, db));
    }

    private static Task<AtomicCommandOutcome<SendTechnicianAssignmentMessageResult>> ExecuteAsync(
        IServiceProvider services,
        AtomicCommandIdentity identity,
        SendTechnicianAssignmentMessageCommand command)
    {
        var db = services.GetRequiredService<RentalCommandDbContext>();
        return services.GetRequiredService<IWriteExecutor>()
            .ExecuteAsync(identity.IdempotencyKey, SendTechnicianAssignmentMessageRule.Write(command, db));
    }

    private static Task<AtomicCommandOutcome<MarkTechnicianAssignmentConversationReadResult>> ExecuteAsync(
        IServiceProvider services,
        AtomicCommandIdentity identity,
        MarkTechnicianAssignmentConversationReadCommand command)
    {
        var db = services.GetRequiredService<RentalCommandDbContext>();
        return services.GetRequiredService<IWriteExecutor>()
            .ExecuteAsync(identity.IdempotencyKey, MarkTechnicianAssignmentConversationReadRule.Write(command, db));
    }

    private ServiceProvider BuildResponsibilityServices(
        TimeProvider timeProvider,
        IInterceptor? interceptor = null)
    {
        var services = new ServiceCollection();
        services.AddSingleton(timeProvider);
        services.AddScoped<ICurrentActor, TestActor>();
        services.AddAtomicPersistenceKernel();
        services.AddScoped<IRequestWriteExecutor, RequestWriteExecutor>();
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
            BusinessNowUtc,
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

    private async Task<ReversedResponsibilityScenario> SeedReversedResponsibilityScenarioAsync()
    {
        var now = DateTime.UtcNow;
        var property = new Property
        {
            PortfolioId = 1,
            Name = $"Reversed responsibility property {Guid.NewGuid():N}",
            AddressLine1 = "20 Responsibility Way",
            City = "Columbus",
            State = "OH",
            PostalCode = "43215",
            CreatedAt = now,
            UpdatedAt = now,
        };
        var tenant = new Tenant
        {
            PortfolioId = 1,
            FirstName = "Reversed",
            LastName = "Context Tenant",
            Email = $"reversed-context-{Guid.NewGuid():N}@example.test",
            CreatedAt = now,
            UpdatedAt = now,
        };
        var firstUser = User("reversed-first");
        var secondUser = User("reversed-second");
        _context.Db.AddRange(property, tenant, firstUser, secondUser);
        await _context.Db.SaveChangesAsync();

        var firstContext = AccessContext(firstUser.Id, now);
        var firstMembership = Membership(firstContext, now, WorkspaceExperience.Management);
        var firstAdminAssignment = Assignment(
            firstMembership,
            RoleProfileKeys.WorkspaceAdministrator,
            MembershipRoleAssignmentScopeKind.AllProperties,
            now);
        var firstTargetAssignment = Assignment(
            firstMembership,
            RoleProfileKeys.MaintenanceTechnician,
            MembershipRoleAssignmentScopeKind.AssignedWorkOrders,
            now);
        var firstSession = Session(firstUser.Id, firstContext, now);

        var secondContext = AccessContext(secondUser.Id, now);
        var secondMembership = Membership(secondContext, now, WorkspaceExperience.Management);
        var secondAdminAssignment = Assignment(
            secondMembership,
            RoleProfileKeys.WorkspaceAdministrator,
            MembershipRoleAssignmentScopeKind.AllProperties,
            now);
        var secondTargetAssignment = Assignment(
            secondMembership,
            RoleProfileKeys.MaintenanceTechnician,
            MembershipRoleAssignmentScopeKind.AssignedWorkOrders,
            now);
        var secondSession = Session(secondUser.Id, secondContext, now);
        _context.Db.AddRange(
            firstAdminAssignment,
            firstTargetAssignment,
            firstSession,
            secondAdminAssignment,
            secondTargetAssignment,
            secondSession);
        await _context.Db.SaveChangesAsync();

        var workOrder = WorkOrder(property.Id, tenant.Id, "Reversed responsibility repair", now);
        _context.Db.WorkOrders.Add(workOrder);
        await _context.Db.SaveChangesAsync();
        _context.Db.ChangeTracker.Clear();
        return new(
            1,
            firstUser.Id,
            firstSession.Id,
            firstContext.Id,
            firstContext.AccessRevision,
            firstMembership.Id,
            firstTargetAssignment.Id,
            secondUser.Id,
            secondSession.Id,
            secondContext.Id,
            secondContext.AccessRevision,
            secondMembership.Id,
            secondTargetAssignment.Id,
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

    private async Task<Scenario> SeedScenarioAsync(
        DateTime? fixtureNowUtc = null,
        Guid? sessionId = null)
    {
        var now = fixtureNowUtc ?? DateTime.UtcNow;
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
            Id = sessionId ?? Guid.NewGuid(),
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

    private sealed record ReversedResponsibilityScenario(
        int PortfolioId,
        int FirstActorUserId,
        Guid FirstActorSessionId,
        int FirstActorAccessContextId,
        long FirstActorAccessRevision,
        int FirstTargetMembershipId,
        int FirstTargetRoleAssignmentId,
        int SecondActorUserId,
        Guid SecondActorSessionId,
        int SecondActorAccessContextId,
        long SecondActorAccessRevision,
        int SecondTargetMembershipId,
        int SecondTargetRoleAssignmentId,
        int WorkOrderId);

    private sealed class FixedTimeProvider(DateTime utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(utcNow);
    }

    private sealed class ResponsibilityLockProbe(
        int workOrderId,
        int actorAccessContextId,
        bool pauseAfterWorkOrder) : DbCommandInterceptor
    {
        private const int NamespaceKey = 0x52434D44;
        private readonly TaskCompletionSource _workOrderLockReached =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _actorContextLockReached =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _releaseWorkOrder =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task WorkOrderLockReached => _workOrderLockReached.Task;
        public Task ActorContextLockReached => _actorContextLockReached.Task;
        public void ReleaseWorkOrder() => _releaseWorkOrder.TrySetResult();

        public override async ValueTask<int> NonQueryExecutedAsync(
            DbCommand command,
            CommandExecutedEventData eventData,
            int result,
            CancellationToken cancellationToken = default)
        {
            await ObserveAsync(command, cancellationToken);

            return await base.NonQueryExecutedAsync(command, eventData, result, cancellationToken);
        }

        public override async ValueTask<DbDataReader> ReaderExecutedAsync(
            DbCommand command,
            CommandExecutedEventData eventData,
            DbDataReader result,
            CancellationToken cancellationToken = default)
        {
            await ObserveAsync(command, cancellationToken);
            return await base.ReaderExecutedAsync(command, eventData, result, cancellationToken);
        }

        private async Task ObserveAsync(DbCommand command, CancellationToken cancellationToken)
        {
            if (Matches(command, "WorkspaceAccessContext", actorAccessContextId))
                _actorContextLockReached.TrySetResult();

            if (Matches(command, "WorkOrder", workOrderId))
            {
                _workOrderLockReached.TrySetResult();
                if (pauseAfterWorkOrder)
                    await _releaseWorkOrder.Task.WaitAsync(cancellationToken);
            }
        }

        private static bool Matches(DbCommand command, string lockNamespace, int aggregateId)
        {
            if (!command.CommandText.Contains("pg_advisory_xact_lock", StringComparison.Ordinal))
                return false;

            var values = command.Parameters.Cast<DbParameter>()
                .Select(parameter => Convert.ToInt64(parameter.Value))
                .ToArray();
            return values.Length == 2 &&
                values[0] == StableNamespaceKey(lockNamespace) &&
                values[1] == aggregateId;
        }

        private static int StableNamespaceKey(string lockNamespace)
        {
            var digest = SHA256.HashData(Encoding.UTF8.GetBytes(lockNamespace));
            return NamespaceKey ^ BinaryPrimitives.ReadInt32BigEndian(digest);
        }
    }

    private sealed class ResponsibilityChangedSetProbe : DbCommandInterceptor
    {
        private const int NamespaceKey = 0x52434D44;
        private readonly TaskCompletionSource _firstContextLockReached =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _contextLockCount;

        public Task FirstContextLockReached => _firstContextLockReached.Task;

        public void Release() => _release.TrySetResult();

        public override async ValueTask<int> NonQueryExecutedAsync(
            DbCommand command,
            CommandExecutedEventData eventData,
            int result,
            CancellationToken cancellationToken = default)
        {
            await ObserveAsync(command, cancellationToken);
            return await base.NonQueryExecutedAsync(command, eventData, result, cancellationToken);
        }

        private async Task ObserveAsync(DbCommand command, CancellationToken cancellationToken)
        {
            if (!command.CommandText.Contains("pg_advisory_xact_lock", StringComparison.Ordinal) ||
                !command.CommandText.Contains("@p", StringComparison.Ordinal))
                return;

            var values = command.Parameters.Cast<DbParameter>()
                .Select(parameter => Convert.ToInt64(parameter.Value))
                .ToArray();
            if (values.Length == 2 && values[0] == StableNamespaceKey("WorkspaceAccessContext") &&
                Interlocked.Increment(ref _contextLockCount) == 1)
            {
                _firstContextLockReached.TrySetResult();
                await _release.Task.WaitAsync(cancellationToken);
            }
        }

        private static int StableNamespaceKey(string lockNamespace)
        {
            var digest = SHA256.HashData(Encoding.UTF8.GetBytes(lockNamespace));
            return NamespaceKey ^ BinaryPrimitives.ReadInt32BigEndian(digest);
        }
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
