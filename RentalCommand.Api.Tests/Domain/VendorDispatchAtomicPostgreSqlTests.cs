using System.Data.Common;
using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using RentalCommand.Api.DTOs;
using RentalCommand.Api.Services.Domain;
using RentalCommand.Core.Atomic;
using RentalCommand.Core.Authorization;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Interfaces;
using RentalCommand.Core.Operations;
using RentalCommand.Core.Time;
using RentalCommand.TestCommon;

namespace RentalCommand.Api.Tests.Domain;

[Collection(MigratedPostgreSqlCollection.Name2)]
public sealed class VendorDispatchAtomicPostgreSqlTests : IAsyncLifetime
{
    private const int PortfolioId = 1;
    private static readonly DateTime SeededAtUtc = new(2026, 7, 28, 12, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime ContaminatedDispatchedAtUtc = new(2026, 7, 28, 16, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime CorrectDispatchedAtUtc = new(2027, 1, 24, 19, 15, 0, DateTimeKind.Utc);
    private static readonly DateTime BusinessNowUtc = new(2027, 1, 25, 5, 0, 0, DateTimeKind.Utc);

    private readonly MigratedPostgreSqlFixture _fixture;
    private MigratedPostgreSqlTestContext _context = null!;
    private ServiceProvider _services = null!;
    private WorkspaceReadScope _scope;

    public VendorDispatchAtomicPostgreSqlTests(MigratedPostgreSqlFixture fixture) =>
        _fixture = fixture;

    public async Task InitializeAsync()
    {
        _context = await _fixture.CreateContextAsync();
        _scope = _context.Db.SeedAdministratorScope(PortfolioId, nameof(VendorDispatchAtomicPostgreSqlTests));
        FreezeSimulationClock();
        await ExtendScopeThroughBusinessClockAsync();
        await _context.Db.SaveChangesAsync();
        await _context.ActivateApiScopeAsync(_scope);
        _context.Db.ChangeTracker.Clear();
        _services = AtomicDomainTestKernel.CreateForVendorDispatchPostgreSql(
            _context.ConnectionString,
            new FixedTimeProvider(new DateTimeOffset(BusinessNowUtc)),
            [new RequestGucConnectionInterceptor(_scope)]);
    }

    public async Task DisposeAsync()
    {
        await _services.DisposeAsync();
        await _context.DisposeAsync();
    }

    [Fact]
    public async Task DispatchAuthorizedAsync_AlreadyAssignedWorkOrderCreatesDispatchHistoryAuditOutboxAndReplaysExactly()
    {
        var scenario = await SeedDispatchScenarioAsync(alreadyAssignedAtCommandClock: true);
        var service = Service(_services);
        var request = Request(scenario.VendorId, "ys-264-valid-dispatch");

        var first = await service.DispatchAuthorizedAsync(_scope, scenario.WorkOrderId, request, changedByUserId: 1);
        var replay = await service.DispatchAuthorizedAsync(_scope, scenario.WorkOrderId, request, changedByUserId: 1);

        replay.Should().BeEquivalentTo(first);
        first.Outcome.Should().Be(DispatchOutcome.Dispatched);
        first.Dispatch.Should().NotBeNull();
        first.Dispatch!.Status.Should().Be(VendorDispatchStatus.Dispatched);
        first.Dispatch.WorkOrderId.Should().Be(scenario.WorkOrderId);
        first.Dispatch.VendorId.Should().Be(scenario.VendorId);

        var commandKey = CommandKey(scenario.WorkOrderId, request.IdempotencyKey);
        _context.Db.ChangeTracker.Clear();
        (await _context.Db.VendorDispatches.AsNoTracking()
                .CountAsync(row => row.WorkOrderId == scenario.WorkOrderId
                    && row.VendorId == scenario.VendorId
                    && row.Status == VendorDispatchStatus.Dispatched))
            .Should().Be(1);
        (await _context.Db.WorkOrders.AsNoTracking()
                .Where(row => row.Id == scenario.WorkOrderId)
                .Select(row => new { row.VendorId, row.UpdatedAt })
                .SingleAsync())
            .Should().BeEquivalentTo(new { VendorId = (int?)scenario.VendorId, UpdatedAt = BusinessNowUtc });

        var statusEvent = await _context.Db.WorkOrderStatusEvents.AsNoTracking()
            .SingleAsync(row => row.WorkOrderId == scenario.WorkOrderId && row.Kind == "Dispatch");
        statusEvent.FromStatus.Should().Be(WorkOrderStatus.New);
        statusEvent.ToStatus.Should().Be(WorkOrderStatus.New);
        statusEvent.CreatedAtUtc.Should().Be(BusinessNowUtc);

        var outbox = await _context.Db.OutboxMessages.AsNoTracking().SingleAsync();
        outbox.MessageType.Should().Be("sms");
        outbox.IdempotencyKey.Should().Be($"vendor-dispatch:{first.Dispatch.Id}:sms");
        using (var payload = JsonDocument.Parse(outbox.Payload))
        {
            payload.RootElement.GetProperty("to").GetString().Should().Be("+16145550199");
            payload.RootElement.GetProperty("message").GetString().Should().Contain("Reply DONE");
        }

        (await _context.Db.AtomicCommandReceipts.AsNoTracking()
                .CountAsync(row => row.CommandType == "vendor-dispatch.create"
                    && row.IdempotencyKey == commandKey
                    && row.Status == AtomicCommandReceiptStatus.Completed))
            .Should().Be(1);
        (await _context.Db.AtomicAuditLogs.AsNoTracking()
                .CountAsync(row => row.CommandType == "vendor-dispatch.create"
                    && row.CommandIdempotencyKey == commandKey
                    && row.EntityType == nameof(VendorDispatch)
                    && row.EntityId == first.Dispatch.Id))
            .Should().Be(1);
        (await _context.Db.AtomicAuditLogs.AsNoTracking()
                .CountAsync(row => row.CommandType == "vendor-dispatch.create"
                    && row.CommandIdempotencyKey == commandKey
                    && row.EntityType == nameof(WorkOrderStatusEvent)
                    && row.EntityId == statusEvent.Id))
            .Should().Be(1);
        (await _context.Db.AtomicAuditLogs.AsNoTracking()
                .CountAsync(row => row.CommandType == "vendor-dispatch.create"
                    && row.CommandIdempotencyKey == commandKey
                    && row.EntityType == nameof(WorkOrder)))
            .Should().Be(0);
    }

    [Fact]
    public async Task DispatchAuthorizedAsync_OutboxFailureRollsBackDispatchHistoryAuditAndReceipt()
    {
        var scenario = await SeedDispatchScenarioAsync(alreadyAssignedAtCommandClock: true);
        await using var failingServices = AtomicDomainTestKernel.CreateForVendorDispatchPostgreSql(
            _context.ConnectionString,
            new FixedTimeProvider(new DateTimeOffset(BusinessNowUtc)),
            [new RequestGucConnectionInterceptor(_scope), new ThrowOnOutboxInsertInterceptor()]);
        var request = Request(scenario.VendorId, "ys-264-outbox-rollback");

        var act = async () => await Service(failingServices)
            .DispatchAuthorizedAsync(_scope, scenario.WorkOrderId, request, changedByUserId: 1);

        await act.Should().ThrowAsync<DbUpdateException>()
            .Where(exception => exception.InnerException is InjectedOutboxFailure);

        var commandKey = CommandKey(scenario.WorkOrderId, request.IdempotencyKey);
        _context.Db.ChangeTracker.Clear();
        (await _context.Db.VendorDispatches.AsNoTracking()
                .CountAsync(row => row.WorkOrderId == scenario.WorkOrderId))
            .Should().Be(0);
        (await _context.Db.WorkOrderStatusEvents.AsNoTracking()
                .CountAsync(row => row.WorkOrderId == scenario.WorkOrderId && row.Kind == "Dispatch"))
            .Should().Be(0);
        (await _context.Db.OutboxMessages.AsNoTracking()
                .CountAsync(row => row.IdempotencyKey.Contains("vendor-dispatch")))
            .Should().Be(0);
        (await _context.Db.AtomicAuditLogs.AsNoTracking()
                .CountAsync(row => row.CommandIdempotencyKey == commandKey))
            .Should().Be(0);
        (await _context.Db.AtomicCommandReceipts.AsNoTracking()
                .CountAsync(row => row.CommandType == "vendor-dispatch.create"
                    && row.IdempotencyKey == commandKey))
            .Should().Be(0);
    }

    [Fact]
    public async Task CancelAuthorizedAsync_CancelsOneOpenDispatchPreservesHistoryAndAllowsRedispatch()
    {
        var scenario = await SeedDispatchScenarioAsync(alreadyAssignedAtCommandClock: true);
        var service = Service(_services);
        var firstVendor = scenario.VendorId;
        var secondVendor = await SeedSecondVendorAsync();
        var dispatch = await service.DispatchAuthorizedAsync(
            _scope,
            scenario.WorkOrderId,
            Request(firstVendor, "ys-307-dispatch"),
            changedByUserId: 1);
        dispatch.Outcome.Should().Be(DispatchOutcome.Dispatched);

        var cancelRequest = new CancelVendorDispatchRequest
        {
            IdempotencyKey = "ys-307-cancel",
            Reason = "Cancelled for reassignment.",
        };
        var cancelled = await service.CancelAuthorizedAsync(
            _scope,
            scenario.WorkOrderId,
            dispatch.Dispatch!.Id,
            cancelRequest,
            changedByUserId: 1);
        var replay = await service.CancelAuthorizedAsync(
            _scope,
            scenario.WorkOrderId,
            dispatch.Dispatch.Id,
            cancelRequest,
            changedByUserId: 1);
        var redispatched = await service.DispatchAuthorizedAsync(
            _scope,
            scenario.WorkOrderId,
            Request(secondVendor, "ys-307-redispatch"),
            changedByUserId: 1);

        cancelled.Outcome.Should().Be(CancelDispatchOutcome.Cancelled);
        replay.Outcome.Should().Be(CancelDispatchOutcome.Cancelled);
        replay.Dispatch!.Replayed.Should().BeTrue();
        redispatched.Outcome.Should().Be(DispatchOutcome.Dispatched);

        _context.Db.ChangeTracker.Clear();
        var dispatchRows = await _context.Db.VendorDispatches.AsNoTracking()
            .Where(row => row.WorkOrderId == scenario.WorkOrderId)
            .OrderBy(row => row.Id)
            .Select(row => new { row.Id, row.VendorId, row.Status })
            .ToListAsync();
        dispatchRows.Should().Equal(
            new { Id = dispatch.Dispatch.Id, VendorId = firstVendor, Status = VendorDispatchStatus.Cancelled },
            new { Id = redispatched.Dispatch!.Id, VendorId = secondVendor, Status = VendorDispatchStatus.Dispatched });
        (await _context.Db.WorkOrders.AsNoTracking()
                .Where(row => row.Id == scenario.WorkOrderId)
                .Select(row => row.VendorId)
                .SingleAsync())
            .Should().Be(secondVendor);
        (await _context.Db.WorkOrderStatusEvents.AsNoTracking()
                .Where(row => row.WorkOrderId == scenario.WorkOrderId && row.Kind == "Dispatch")
                .OrderBy(row => row.Id)
                .Select(row => new { row.Note, row.ChangedByUserId })
                .ToListAsync())
            .Should().ContainSingle(row => row.Note == "Cancelled for reassignment."
                && row.ChangedByUserId == 1);
        (await _context.Db.OutboxMessages.AsNoTracking()
                .CountAsync(row => row.IdempotencyKey.StartsWith("vendor-dispatch-cancel:")))
            .Should().Be(1);
        (await _context.Db.AtomicAuditLogs.AsNoTracking()
                .CountAsync(row => row.CommandType == "vendor-dispatch.cancel"
                    && row.EntityType == nameof(VendorDispatch)
                    && row.EntityId == dispatch.Dispatch.Id))
            .Should().Be(1);
        (await _context.Db.AtomicCommandReceipts.AsNoTracking()
                .CountAsync(row => row.CommandType == "vendor-dispatch.cancel"
                    && row.Status == AtomicCommandReceiptStatus.Completed))
            .Should().Be(1);
    }

    [Fact]
    public async Task CancelAuthorizedAsync_OutboxFailureRollsBackDispatchStatusWorkOrderTimelineAuditAndReceipt()
    {
        var scenario = await SeedDispatchScenarioAsync(alreadyAssignedAtCommandClock: true);
        var dispatch = await Service(_services).DispatchAuthorizedAsync(
            _scope,
            scenario.WorkOrderId,
            Request(scenario.VendorId, "ys-307-rollback-dispatch"),
            changedByUserId: 1);
        dispatch.Outcome.Should().Be(DispatchOutcome.Dispatched);
        await using var failingServices = AtomicDomainTestKernel.CreateForVendorDispatchPostgreSql(
            _context.ConnectionString,
            new FixedTimeProvider(new DateTimeOffset(BusinessNowUtc)),
            [new RequestGucConnectionInterceptor(_scope), new ThrowOnOutboxInsertInterceptor()]);
        var cancelRequest = new CancelVendorDispatchRequest
        {
            IdempotencyKey = "ys-307-cancel-rollback",
            Reason = "Cancelled for reassignment.",
        };

        var act = async () => await Service(failingServices).CancelAuthorizedAsync(
            _scope,
            scenario.WorkOrderId,
            dispatch.Dispatch!.Id,
            cancelRequest,
            changedByUserId: 1);

        await act.Should().ThrowAsync<DbUpdateException>()
            .Where(exception => exception.InnerException is InjectedOutboxFailure);

        var cancelCommandKey = $"{PortfolioId}:{scenario.WorkOrderId}:{dispatch.Dispatch!.Id}:{cancelRequest.IdempotencyKey}";
        _context.Db.ChangeTracker.Clear();
        (await _context.Db.VendorDispatches.AsNoTracking()
                .Where(row => row.Id == dispatch.Dispatch.Id)
                .Select(row => new { row.Status, row.VendorId, row.RespondedAtUtc })
                .SingleAsync())
            .Should().BeEquivalentTo(new
            {
                Status = VendorDispatchStatus.Dispatched,
                VendorId = scenario.VendorId,
                RespondedAtUtc = (DateTime?)null,
            });
        (await _context.Db.WorkOrders.AsNoTracking()
                .Where(row => row.Id == scenario.WorkOrderId)
                .Select(row => row.VendorId)
                .SingleAsync())
            .Should().Be(scenario.VendorId);
        (await _context.Db.WorkOrderStatusEvents.AsNoTracking()
                .CountAsync(row => row.WorkOrderId == scenario.WorkOrderId
                    && row.Kind == "Dispatch"
                    && row.Note == "Cancelled for reassignment."))
            .Should().Be(0);
        (await _context.Db.OutboxMessages.AsNoTracking()
                .CountAsync(row => row.IdempotencyKey.StartsWith("vendor-dispatch-cancel:")))
            .Should().Be(0);
        (await _context.Db.AtomicAuditLogs.AsNoTracking()
                .CountAsync(row => row.CommandType == "vendor-dispatch.cancel"
                    && row.CommandIdempotencyKey == cancelCommandKey))
            .Should().Be(0);
        (await _context.Db.AtomicCommandReceipts.AsNoTracking()
                .CountAsync(row => row.CommandType == "vendor-dispatch.cancel"
                    && row.IdempotencyKey == cancelCommandKey))
            .Should().Be(0);
    }

    [Fact]
    public async Task RecoverChronologyAuthorizedAsync_RepairsExactPendingGraphAndPreservesOriginalReceipt()
    {
        var graph = await SeedContaminatedDispatchGraphAsync("ys-273-repair");
        var originalReceipt = await OriginalReceiptSnapshotAsync(graph);
        var service = Service(_services);

        var result = await service.RecoverChronologyAuthorizedAsync(
            _scope,
            graph.WorkOrderId,
            graph.DispatchId,
            RecoveryRequest(graph),
            actorUserId: _scope.UserId,
            idempotencyKey: "ys-273-repair");

        result.Should().BeEquivalentTo(new RecoverVendorDispatchChronologyResponse
        {
            WorkOrderId = graph.WorkOrderId,
            DispatchId = graph.DispatchId,
            StatusEventId = graph.StatusEventId,
            OutboxId = graph.OutboxId,
            DispatchedAtUtc = CorrectDispatchedAtUtc,
            WorkOrderUpdatedAtRepaired = true,
            Replayed = false,
        });

        _context.Db.ChangeTracker.Clear();
        (await _context.Db.VendorDispatches.AsNoTracking()
                .Where(row => row.Id == graph.DispatchId)
                .Select(row => new
                {
                    row.WorkOrderId,
                    row.VendorId,
                    row.Status,
                    row.DispatchedAtUtc,
                    row.RespondedAtUtc,
                    row.Message,
                })
                .SingleAsync())
            .Should().BeEquivalentTo(new
            {
                graph.WorkOrderId,
                graph.VendorId,
                Status = VendorDispatchStatus.Dispatched,
                DispatchedAtUtc = CorrectDispatchedAtUtc,
                RespondedAtUtc = (DateTime?)null,
                graph.Message,
            });
        (await _context.Db.WorkOrderStatusEvents.AsNoTracking()
                .Where(row => row.Id == graph.StatusEventId)
                .Select(row => new
                {
                    row.WorkOrderId,
                    row.Kind,
                    row.Visibility,
                    row.Note,
                    row.CreatedAtUtc,
                    row.ChangedByUserId,
                })
                .SingleAsync())
            .Should().BeEquivalentTo(new
            {
                graph.WorkOrderId,
                Kind = "Dispatch",
                Visibility = "Public",
                Note = "Vendor dispatch sent by SMS.",
                CreatedAtUtc = CorrectDispatchedAtUtc,
                ChangedByUserId = (int?)1,
            });
        (await _context.Db.OutboxMessages.AsNoTracking()
                .Where(row => row.Id == graph.OutboxId)
                .Select(row => new
                {
                    row.MessageType,
                    row.IdempotencyKey,
                    row.CreatedAtUtc,
                    row.NextAttemptAtUtc,
                    row.AttemptCount,
                    row.AcceptedAtUtc,
                    row.DeliveredAtUtc,
                    row.DeadLetteredAtUtc,
                })
                .SingleAsync())
            .Should().BeEquivalentTo(new
            {
                MessageType = "sms",
                IdempotencyKey = graph.OutboxIdempotencyKey,
                CreatedAtUtc = CorrectDispatchedAtUtc,
                NextAttemptAtUtc = CorrectDispatchedAtUtc,
                AttemptCount = 0,
                AcceptedAtUtc = (DateTime?)null,
                DeliveredAtUtc = (DateTime?)null,
                DeadLetteredAtUtc = (DateTime?)null,
            });
        await AssertOriginalReceiptUnchangedAsync(graph, originalReceipt);
        await AssertExactChronologyAuditAsync(graph, expectedRecoveryAuditCount: 1);
        await AssertExactOutboxCountsAsync(graph);
    }

    [Fact]
    public async Task RecoverChronologyAuthorizedAsync_ReplayReturnsSameResultAndCreatesNoDuplicates()
    {
        var graph = await SeedContaminatedDispatchGraphAsync("ys-273-replay");
        var service = Service(_services);
        var request = RecoveryRequest(graph);

        var first = await service.RecoverChronologyAuthorizedAsync(
            _scope,
            graph.WorkOrderId,
            graph.DispatchId,
            request,
            actorUserId: _scope.UserId,
            idempotencyKey: "ys-273-replay");
        var replay = await service.RecoverChronologyAuthorizedAsync(
            _scope,
            graph.WorkOrderId,
            graph.DispatchId,
            request,
            actorUserId: _scope.UserId,
            idempotencyKey: "ys-273-replay");

        replay.Should().BeEquivalentTo(new RecoverVendorDispatchChronologyResponse
        {
            WorkOrderId = first.WorkOrderId,
            DispatchId = first.DispatchId,
            StatusEventId = first.StatusEventId,
            OutboxId = first.OutboxId,
            DispatchedAtUtc = first.DispatchedAtUtc,
            WorkOrderUpdatedAtRepaired = first.WorkOrderUpdatedAtRepaired,
            Replayed = true,
        });
        _context.Db.ChangeTracker.Clear();
        (await _context.Db.VendorDispatches.AsNoTracking()
                .CountAsync(row => row.Id == graph.DispatchId
                    && row.DispatchedAtUtc == CorrectDispatchedAtUtc))
            .Should().Be(1);
        (await _context.Db.WorkOrderStatusEvents.AsNoTracking()
                .CountAsync(row => row.Id == graph.StatusEventId
                    && row.CreatedAtUtc == CorrectDispatchedAtUtc))
            .Should().Be(1);
        await AssertExactChronologyAuditAsync(graph, expectedRecoveryAuditCount: 1);
        await AssertExactOutboxCountsAsync(graph);
        (await _context.Db.AtomicCommandReceipts.AsNoTracking()
                .CountAsync(row => row.CommandType == "vendor-dispatch.recover-chronology"))
            .Should().Be(1);
    }

    [Fact]
    public async Task RecoverChronologyAuthorizedAsync_WrongPreconditionLeavesGraphUnchanged()
    {
        var graph = await SeedContaminatedDispatchGraphAsync("ys-273-wrong-precondition");
        var before = await RecoveryMutationSnapshotAsync(graph);
        var request = RecoveryRequest(graph);
        request.ExpectedStatusEventId += 10_000;

        await FluentActions.Invoking(() => Service(_services).RecoverChronologyAuthorizedAsync(
                _scope,
                graph.WorkOrderId,
                graph.DispatchId,
                request,
                actorUserId: _scope.UserId,
                idempotencyKey: "ys-273-wrong-precondition"))
            .Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("Vendor-dispatch chronology recovery expected-state check failed; no rows were changed.");

        await AssertSnapshotUnchangedAsync(graph, before);
        (await _context.Db.AtomicCommandReceipts.AsNoTracking()
                .CountAsync(row => row.CommandType == "vendor-dispatch.recover-chronology"))
            .Should().Be(0);
    }

    [Theory]
    [InlineData("claimed")]
    [InlineData("attempted")]
    [InlineData("accepted")]
    [InlineData("delivered")]
    [InlineData("dead-lettered")]
    public async Task RecoverChronologyAuthorizedAsync_TouchedOutboxLeavesGraphUnchanged(string touchedState)
    {
        var graph = await SeedContaminatedDispatchGraphAsync($"ys-273-{touchedState}");
        await TouchOutboxAsync(graph.OutboxId, touchedState);
        var before = await RecoveryMutationSnapshotAsync(graph);

        await FluentActions.Invoking(() => Service(_services).RecoverChronologyAuthorizedAsync(
                _scope,
                graph.WorkOrderId,
                graph.DispatchId,
                RecoveryRequest(graph),
                actorUserId: _scope.UserId,
                idempotencyKey: $"ys-273-{touchedState}"))
            .Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("Vendor-dispatch chronology recovery expected-state check failed; no rows were changed.");

        await AssertSnapshotUnchangedAsync(graph, before);
        (await _context.Db.AtomicCommandReceipts.AsNoTracking()
                .CountAsync(row => row.CommandType == "vendor-dispatch.recover-chronology"))
            .Should().Be(0);
    }

    [Theory]
    [InlineData("audit")]
    [InlineData("data-update-outbox")]
    public async Task RecoverChronologyAuthorizedAsync_StatementFailureRollsBackEveryRepairedTimestamp(
        string failurePoint)
    {
        var graph = await SeedContaminatedDispatchGraphAsync($"ys-273-{failurePoint}-rollback");
        var before = await RecoveryMutationSnapshotAsync(graph);
        await using var failingServices = AtomicDomainTestKernel.CreateForVendorDispatchPostgreSql(
            _context.ConnectionString,
            new FixedTimeProvider(new DateTimeOffset(BusinessNowUtc)),
            [
                new RequestGucConnectionInterceptor(_scope),
                new ThrowOnRecoveryCommitInterceptor(failurePoint),
            ]);

        await FluentActions.Invoking(() => Service(failingServices).RecoverChronologyAuthorizedAsync(
                _scope,
                graph.WorkOrderId,
                graph.DispatchId,
                RecoveryRequest(graph),
                actorUserId: _scope.UserId,
                idempotencyKey: $"ys-273-{failurePoint}-rollback"))
            .Should().ThrowAsync<InjectedRecoveryCommitFailure>();

        await AssertSnapshotUnchangedAsync(graph, before);
        (await _context.Db.AtomicCommandReceipts.AsNoTracking()
                .CountAsync(row => row.CommandType == "vendor-dispatch.recover-chronology"))
            .Should().Be(0);
    }

    private VendorDispatchService Service(ServiceProvider services) => new(
        services.GetRequiredService<RentalCommand.Data.RentalCommandDbContext>(),
        Mock.Of<IDataUpdateService>(),
        services.GetRequiredService<RentalCommand.Core.Atomic.IWriteExecutor>(),
        Mock.Of<ILogger<VendorDispatchService>>(),
        services.GetRequiredService<TimeProvider>());

    private static DispatchWorkOrderRequest Request(int vendorId, string idempotencyKey) => new()
    {
        IdempotencyKey = idempotencyKey,
        VendorId = vendorId,
        Note = "Gate code 1234",
    };

    private async Task<ContaminatedDispatchGraph> SeedContaminatedDispatchGraphAsync(string key)
    {
        var scenario = await SeedDispatchScenarioAsync(alreadyAssignedAtCommandClock: false);
        await using var contaminatedServices = AtomicDomainTestKernel.CreateForVendorDispatchPostgreSql(
            _context.ConnectionString,
            new FixedTimeProvider(new DateTimeOffset(ContaminatedDispatchedAtUtc)),
            [new RequestGucConnectionInterceptor(_scope)]);
        var request = Request(scenario.VendorId, key);
        var dispatched = await Service(contaminatedServices)
            .DispatchAuthorizedAsync(_scope, scenario.WorkOrderId, request, changedByUserId: 1);
        dispatched.Outcome.Should().Be(DispatchOutcome.Dispatched);
        dispatched.Dispatch.Should().NotBeNull();

        var commandKey = CommandKey(scenario.WorkOrderId, request.IdempotencyKey);
        _context.Db.ChangeTracker.Clear();
        var statusEventId = await _context.Db.WorkOrderStatusEvents.AsNoTracking()
            .Where(row => row.WorkOrderId == scenario.WorkOrderId && row.Kind == "Dispatch")
            .Select(row => row.Id)
            .SingleAsync();
        var outbox = await _context.Db.OutboxMessages.AsNoTracking()
            .Where(row => row.IdempotencyKey == $"vendor-dispatch:{dispatched.Dispatch!.Id}:sms")
            .Select(row => new { row.Id, row.IdempotencyKey })
            .SingleAsync();

        return new ContaminatedDispatchGraph(
            scenario.WorkOrderId,
            scenario.VendorId,
            dispatched.Dispatch!.Id,
            dispatched.Dispatch.Message,
            statusEventId,
            outbox.Id,
            outbox.IdempotencyKey,
            commandKey);
    }

    private static RecoverVendorDispatchChronologyRequest RecoveryRequest(ContaminatedDispatchGraph graph) => new()
    {
        ExpectedContaminatedDispatchedAtUtc = ContaminatedDispatchedAtUtc,
        ExpectedStatusEventId = graph.StatusEventId,
        ExpectedOutboxId = graph.OutboxId,
        ExpectedOutboxIdempotencyKey = graph.OutboxIdempotencyKey,
        OriginalCommandIdempotencyKey = graph.OriginalCommandIdempotencyKey,
        CorrectDispatchedAtUtc = CorrectDispatchedAtUtc,
    };

    private async Task<OriginalReceiptSnapshot> OriginalReceiptSnapshotAsync(
        ContaminatedDispatchGraph graph) =>
        await _context.Db.AtomicCommandReceipts.AsNoTracking()
            .Where(row => row.CommandType == "vendor-dispatch.create"
                && row.IdempotencyKey == graph.OriginalCommandIdempotencyKey)
            .Select(row => new OriginalReceiptSnapshot(
                row.Id,
                row.AttemptId,
                row.Status,
                row.ResultContract,
                row.ResultJson))
            .SingleAsync();

    private async Task<RecoveryMutationSnapshot> RecoveryMutationSnapshotAsync(
        ContaminatedDispatchGraph graph)
    {
        _context.Db.ChangeTracker.Clear();
        var dispatch = await _context.Db.VendorDispatches.AsNoTracking()
            .Where(row => row.Id == graph.DispatchId)
            .Select(row => new
            {
                row.DispatchedAtUtc,
                row.Status,
                row.RespondedAtUtc,
            })
            .SingleAsync();
        var statusEvent = await _context.Db.WorkOrderStatusEvents.AsNoTracking()
            .Where(row => row.Id == graph.StatusEventId)
            .Select(row => new { row.CreatedAtUtc })
            .SingleAsync();
        var outbox = await _context.Db.OutboxMessages.AsNoTracking()
            .Where(row => row.Id == graph.OutboxId)
            .Select(row => new
            {
                row.CreatedAtUtc,
                row.NextAttemptAtUtc,
                row.AttemptCount,
                row.LastAttemptAtUtc,
                row.ClaimOwner,
                row.ClaimToken,
                row.ClaimExpiresAtUtc,
                row.AcceptedAtUtc,
                row.DeliveredAtUtc,
                row.DeadLetteredAtUtc,
                row.Provider,
                row.ProviderMessageId,
                row.FailureKind,
                row.LastError,
            })
            .SingleAsync();
        var workOrder = await _context.Db.WorkOrders.AsNoTracking()
            .Where(row => row.Id == graph.WorkOrderId)
            .Select(row => new { row.UpdatedAt })
            .SingleAsync();
        var audits = await _context.Db.AtomicAuditLogs.AsNoTracking()
            .Where(row => row.CommandType == "vendor-dispatch.create"
                && row.CommandIdempotencyKey == graph.OriginalCommandIdempotencyKey)
            .OrderBy(row => row.EntityType)
            .ThenBy(row => row.EntityId)
            .ThenBy(row => row.Operation)
            .Select(row => new AuditSnapshot(row.EntityType, row.EntityId, row.Operation, row.Timestamp))
            .ToListAsync();

        return new RecoveryMutationSnapshot(
            dispatch.DispatchedAtUtc,
            dispatch.Status,
            dispatch.RespondedAtUtc,
            statusEvent.CreatedAtUtc,
            outbox.CreatedAtUtc,
            outbox.NextAttemptAtUtc,
            outbox.AttemptCount,
            outbox.LastAttemptAtUtc,
            outbox.ClaimOwner,
            outbox.ClaimToken,
            outbox.ClaimExpiresAtUtc,
            outbox.AcceptedAtUtc,
            outbox.DeliveredAtUtc,
            outbox.DeadLetteredAtUtc,
            outbox.Provider,
            outbox.ProviderMessageId,
            outbox.FailureKind,
            outbox.LastError,
            workOrder.UpdatedAt,
            audits);
    }

    private async Task AssertSnapshotUnchangedAsync(
        ContaminatedDispatchGraph graph,
        RecoveryMutationSnapshot before)
    {
        var after = await RecoveryMutationSnapshotAsync(graph);
        after.Should().BeEquivalentTo(before);
    }

    private async Task AssertOriginalReceiptUnchangedAsync(
        ContaminatedDispatchGraph graph,
        OriginalReceiptSnapshot before)
    {
        var after = await OriginalReceiptSnapshotAsync(graph);
        after.Should().BeEquivalentTo(before);
    }

    private async Task AssertExactChronologyAuditAsync(
        ContaminatedDispatchGraph graph,
        int expectedRecoveryAuditCount)
    {
        _context.Db.ChangeTracker.Clear();
        (await _context.Db.AtomicAuditLogs.AsNoTracking()
                .CountAsync(row => row.CommandType == "vendor-dispatch.create"
                    && row.CommandIdempotencyKey == graph.OriginalCommandIdempotencyKey
                    && row.Timestamp == CorrectDispatchedAtUtc))
            .Should().Be(3);
        (await _context.Db.AtomicAuditLogs.AsNoTracking()
                .CountAsync(row => row.CommandType == "vendor-dispatch.create"
                    && row.CommandIdempotencyKey == graph.OriginalCommandIdempotencyKey
                    && row.EntityType == nameof(WorkOrder)
                    && row.EntityId == graph.WorkOrderId
                    && row.Operation == AuditLogOperation.Updated
                    && row.Timestamp == CorrectDispatchedAtUtc))
            .Should().Be(1);
        (await _context.Db.AtomicAuditLogs.AsNoTracking()
                .CountAsync(row => row.CommandType == "vendor-dispatch.create"
                    && row.CommandIdempotencyKey == graph.OriginalCommandIdempotencyKey
                    && row.EntityType == nameof(VendorDispatch)
                    && row.EntityId == graph.DispatchId
                    && row.Operation == AuditLogOperation.Created
                    && row.Timestamp == CorrectDispatchedAtUtc))
            .Should().Be(1);
        (await _context.Db.AtomicAuditLogs.AsNoTracking()
                .CountAsync(row => row.CommandType == "vendor-dispatch.create"
                    && row.CommandIdempotencyKey == graph.OriginalCommandIdempotencyKey
                    && row.EntityType == nameof(WorkOrderStatusEvent)
                    && row.EntityId == graph.StatusEventId
                    && row.Operation == AuditLogOperation.Created
                    && row.Timestamp == CorrectDispatchedAtUtc))
            .Should().Be(1);
        (await _context.Db.AtomicAuditLogs.AsNoTracking()
                .CountAsync(row => row.CommandType == "vendor-dispatch.create"
                    && row.CommandIdempotencyKey == graph.OriginalCommandIdempotencyKey))
            .Should().Be(3);
        (await _context.Db.AtomicAuditLogs.AsNoTracking()
                .CountAsync(row => row.CommandType == "vendor-dispatch.recover-chronology"
                    && row.EntityType == nameof(WorkOrder)
                    && row.EntityId == graph.WorkOrderId
                    && row.Timestamp == BusinessNowUtc
                    && row.ChangeReason == "Recovered exact pending vendor-dispatch business chronology."))
            .Should().Be(expectedRecoveryAuditCount);
    }

    private async Task AssertExactOutboxCountsAsync(ContaminatedDispatchGraph graph)
    {
        _context.Db.ChangeTracker.Clear();
        (await _context.Db.OutboxMessages.AsNoTracking()
                .CountAsync(row => row.IdempotencyKey == graph.OutboxIdempotencyKey
                    && row.MessageType == "sms"))
            .Should().Be(1);
        (await _context.Db.OutboxMessages.AsNoTracking()
                .CountAsync(row => row.MessageType == "data-update"
                    && row.IdempotencyKey.StartsWith("vendor-dispatch-chronology-recovery:")))
            .Should().Be(1);
    }

    private async Task TouchOutboxAsync(long outboxId, string touchedState)
    {
        var outbox = await _context.Db.OutboxMessages.SingleAsync(row => row.Id == outboxId);
        switch (touchedState)
        {
            case "claimed":
                outbox.ClaimOwner = "worker-a";
                outbox.ClaimToken = Guid.NewGuid();
                outbox.ClaimExpiresAtUtc = ContaminatedDispatchedAtUtc.AddMinutes(5);
                break;
            case "attempted":
                outbox.AttemptCount = 1;
                outbox.LastAttemptAtUtc = ContaminatedDispatchedAtUtc.AddMinutes(1);
                outbox.LastError = "temporary provider timeout";
                break;
            case "accepted":
                outbox.AcceptedAtUtc = ContaminatedDispatchedAtUtc.AddMinutes(2);
                outbox.Provider = "twilio";
                outbox.ProviderMessageId = "SM-accepted";
                break;
            case "delivered":
                outbox.AcceptedAtUtc = ContaminatedDispatchedAtUtc.AddMinutes(2);
                outbox.DeliveredAtUtc = ContaminatedDispatchedAtUtc.AddMinutes(3);
                outbox.Provider = "twilio";
                outbox.ProviderMessageId = "SM-delivered";
                break;
            case "dead-lettered":
                outbox.AttemptCount = 3;
                outbox.LastAttemptAtUtc = ContaminatedDispatchedAtUtc.AddMinutes(4);
                outbox.DeadLetteredAtUtc = ContaminatedDispatchedAtUtc.AddMinutes(5);
                outbox.FailureKind = OutboxFailureKind.Permanent;
                outbox.LastError = "provider rejected number";
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(touchedState), touchedState, null);
        }

        await _context.Db.SaveChangesAsync();
        _context.Db.ChangeTracker.Clear();
    }

    private async Task<DispatchScenario> SeedDispatchScenarioAsync(bool alreadyAssignedAtCommandClock)
    {
        var property = new Property
        {
            PortfolioId = PortfolioId,
            Name = "YS-264 Dispatch Property",
            AddressLine1 = "100 Simulation Way",
            City = "Columbus",
            State = "OH",
            PostalCode = "43215",
            CreatedAt = SeededAtUtc,
            UpdatedAt = SeededAtUtc,
        };
        var vendor = new Vendor
        {
            PortfolioId = PortfolioId,
            Name = "YS-264 Vendor",
            ServiceType = "Plumbing",
            Phone = "(614) 555-0199",
            CreatedAt = SeededAtUtc,
            UpdatedAt = SeededAtUtc,
        };
        _context.Db.Properties.Add(property);
        _context.Db.Vendors.Add(vendor);
        await _context.Db.SaveChangesAsync();

        var workOrder = new WorkOrder
        {
            PortfolioId = PortfolioId,
            PropertyId = property.Id,
            VendorId = alreadyAssignedAtCommandClock ? vendor.Id : null,
            Title = "YS-264 dispatch regression",
            Description = "Valid dispatch must not require a fake work-order update.",
            Priority = WorkOrderPriority.High,
            Status = WorkOrderStatus.New,
            RequestedAt = SeededAtUtc,
            UpdatedAt = alreadyAssignedAtCommandClock ? BusinessNowUtc : SeededAtUtc,
        };
        _context.Db.WorkOrders.Add(workOrder);
        await _context.Db.SaveChangesAsync();
        _context.Db.ChangeTracker.Clear();
        return new DispatchScenario(workOrder.Id, vendor.Id);
    }

    private async Task<int> SeedSecondVendorAsync()
    {
        var vendor = new Vendor
        {
            PortfolioId = PortfolioId,
            Name = "YS-307 Reassignment Vendor",
            ServiceType = "Locksmith",
            Phone = "(614) 555-0208",
            CreatedAt = SeededAtUtc,
            UpdatedAt = SeededAtUtc,
        };
        _context.Db.Vendors.Add(vendor);
        await _context.Db.SaveChangesAsync();
        _context.Db.ChangeTracker.Clear();
        return vendor.Id;
    }

    private async Task ExtendScopeThroughBusinessClockAsync()
    {
        var session = await _context.Db.AuthSessions.SingleAsync(row => row.Id == _scope.SessionId);
        session.CreatedAtUtc = SeededAtUtc;
        session.LastSeenAtUtc = BusinessNowUtc;
        session.ExpiresAtUtc = BusinessNowUtc.AddHours(1);

        var accessContext = await _context.Db.WorkspaceAccessContexts
            .SingleAsync(row => row.Id == _scope.AccessContextId);
        accessContext.CreatedAtUtc = SeededAtUtc;
        accessContext.UpdatedAtUtc = BusinessNowUtc;

        var membership = await _context.Db.WorkspaceMemberships
            .SingleAsync(row => row.AccessContextId == _scope.AccessContextId
                && row.PortfolioId == PortfolioId);
        membership.EffectiveFromUtc = SeededAtUtc;
        membership.EffectiveToUtc = null;
        membership.UpdatedAtUtc = BusinessNowUtc;

        var assignments = await _context.Db.MembershipRoleAssignments
            .Where(row => row.WorkspaceMembershipId == membership.Id
                && row.PortfolioId == PortfolioId)
            .ToListAsync();
        foreach (var assignment in assignments)
        {
            assignment.EffectiveFromUtc = SeededAtUtc;
            assignment.EffectiveToUtc = null;
            assignment.UpdatedAtUtc = BusinessNowUtc;
        }
    }

    private void FreezeSimulationClock()
    {
        var clock = _context.Db.SimulationClocks.SingleOrDefault(candidate => candidate.Id == 1);
        if (clock is null)
        {
            _context.Db.SimulationClocks.Add(new SimulationClock { Id = 1 });
            clock = _context.Db.SimulationClocks.Local.Single(candidate => candidate.Id == 1);
        }

        clock.Mode = ClockMode.Frozen;
        clock.SimAnchorUtc = BusinessNowUtc;
        clock.RealAnchorUtc = BusinessNowUtc;
        clock.TimeZoneId = "UTC";
        clock.UpdatedAtRealUtc = BusinessNowUtc;
    }

    private static string CommandKey(int workOrderId, string idempotencyKey) =>
        $"{PortfolioId}:{workOrderId}:{idempotencyKey}";

    private sealed record DispatchScenario(int WorkOrderId, int VendorId);
    private sealed record ContaminatedDispatchGraph(
        int WorkOrderId,
        int VendorId,
        int DispatchId,
        string? Message,
        int StatusEventId,
        long OutboxId,
        string OutboxIdempotencyKey,
        string OriginalCommandIdempotencyKey);
    private sealed record OriginalReceiptSnapshot(
        Guid Id,
        Guid AttemptId,
        AtomicCommandReceiptStatus Status,
        string ResultContract,
        string? ResultJson);
    private sealed record AuditSnapshot(
        string EntityType,
        int EntityId,
        AuditLogOperation Operation,
        DateTime Timestamp);
    private sealed record RecoveryMutationSnapshot(
        DateTime DispatchDispatchedAtUtc,
        VendorDispatchStatus DispatchStatus,
        DateTime? DispatchRespondedAtUtc,
        DateTime StatusEventCreatedAtUtc,
        DateTime OutboxCreatedAtUtc,
        DateTime OutboxNextAttemptAtUtc,
        int OutboxAttemptCount,
        DateTime? OutboxLastAttemptAtUtc,
        string? OutboxClaimOwner,
        Guid? OutboxClaimToken,
        DateTime? OutboxClaimExpiresAtUtc,
        DateTime? OutboxAcceptedAtUtc,
        DateTime? OutboxDeliveredAtUtc,
        DateTime? OutboxDeadLetteredAtUtc,
        string? OutboxProvider,
        string? OutboxProviderMessageId,
        OutboxFailureKind? OutboxFailureKind,
        string? OutboxLastError,
        DateTime WorkOrderUpdatedAt,
        IReadOnlyList<AuditSnapshot> OriginalAudits);

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }

    private sealed class ThrowOnOutboxInsertInterceptor : DbCommandInterceptor
    {
        public override InterceptionResult<int> NonQueryExecuting(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<int> result)
        {
            ThrowIfOutboxInsert(command);
            return base.NonQueryExecuting(command, eventData, result);
        }

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            ThrowIfOutboxInsert(command);
            return base.NonQueryExecutingAsync(command, eventData, result, cancellationToken);
        }

        public override InterceptionResult<DbDataReader> ReaderExecuting(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result)
        {
            ThrowIfOutboxInsert(command);
            return base.ReaderExecuting(command, eventData, result);
        }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            ThrowIfOutboxInsert(command);
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }

        private static void ThrowIfOutboxInsert(DbCommand command)
        {
            if (command.CommandText.Contains("INSERT INTO \"OutboxMessages\"", StringComparison.OrdinalIgnoreCase))
            {
                throw new InjectedOutboxFailure();
            }
        }
    }

    private sealed class InjectedOutboxFailure : Exception;

    private sealed class ThrowOnRecoveryCommitInterceptor(string failurePoint) : DbCommandInterceptor
    {
        public override InterceptionResult<int> NonQueryExecuting(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<int> result)
        {
            ThrowIfTarget(command);
            return base.NonQueryExecuting(command, eventData, result);
        }

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            ThrowIfTarget(command);
            return base.NonQueryExecutingAsync(command, eventData, result, cancellationToken);
        }

        public override InterceptionResult<DbDataReader> ReaderExecuting(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result)
        {
            ThrowIfTarget(command);
            return base.ReaderExecuting(command, eventData, result);
        }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            ThrowIfTarget(command);
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }

        private void ThrowIfTarget(DbCommand command)
        {
            if (failurePoint == "audit"
                && command.CommandText.Contains("INSERT INTO \"AtomicAuditLogs\"", StringComparison.OrdinalIgnoreCase))
            {
                throw new InjectedRecoveryCommitFailure();
            }

            if (failurePoint == "data-update-outbox"
                && command.CommandText.Contains("INSERT INTO \"OutboxMessages\"", StringComparison.OrdinalIgnoreCase))
            {
                throw new InjectedRecoveryCommitFailure();
            }
        }
    }

    private sealed class InjectedRecoveryCommitFailure : Exception;

    private sealed class RequestGucConnectionInterceptor(WorkspaceReadScope scope) : DbConnectionInterceptor
    {
        public override async Task ConnectionOpenedAsync(
            DbConnection connection,
            ConnectionEndEventData eventData,
            CancellationToken cancellationToken = default)
        {
            using var command = connection.CreateCommand();
            command.CommandText = """
                SET SESSION AUTHORIZATION rentalcommand_api;
                SELECT set_config('app.current_portfolio_id', @portfolio_id, false),
                       set_config('app.auth_session_id', @auth_session_id, false),
                       set_config('app.current_user_id', @user_id, false),
                       set_config('app.current_access_context_id', @access_context_id, false),
                       set_config('app.access_revision', @access_revision, false);
                """;
            AddParameter(command, "portfolio_id", scope.PortfolioId.ToString());
            AddParameter(command, "auth_session_id", scope.SessionId.ToString());
            AddParameter(command, "user_id", scope.UserId.ToString());
            AddParameter(command, "access_context_id", scope.AccessContextId.ToString());
            AddParameter(command, "access_revision", scope.AccessRevision.ToString());
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        private static void AddParameter(DbCommand command, string name, string value)
        {
            var parameter = command.CreateParameter();
            parameter.ParameterName = $"@{name}";
            parameter.Value = value;
            command.Parameters.Add(parameter);
        }
    }
}
