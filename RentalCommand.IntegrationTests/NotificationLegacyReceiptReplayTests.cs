using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using RentalCommand.Api.Services.Domain;
using RentalCommand.Api.Writes;
using RentalCommand.Core.Atomic;
using RentalCommand.Core.Automation;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Interfaces;
using RentalCommand.Data;
using RentalCommand.Data.Atomic;
using RentalCommand.Data.Auditing;
using RentalCommand.Data.Notifications;
using RentalCommand.Engine.Services;
using RentalCommand.Engine.Writes;
using RentalCommand.TestCommon;

namespace RentalCommand.IntegrationTests;

[Collection(RoleAuthorityPostgreSqlCollection.Name)]
public sealed class NotificationLegacyReceiptReplayTests : IAsyncLifetime
{
    private readonly MigratedPostgreSqlFixture _fixture;
    private MigratedPostgreSqlTestContext _context = null!;
    private ServiceProvider _services = null!;

    public NotificationLegacyReceiptReplayTests(MigratedPostgreSqlFixture fixture) => _fixture = fixture;

    public async Task InitializeAsync()
    {
        _context = await _fixture.CreateContextAsync();
        var services = new ServiceCollection();
        services.AddScoped<ICurrentActor, SystemCurrentActor>();
        services.AddAtomicPersistenceKernel();
        services.AddScoped<IRequestWriteExecutor, RequestWriteExecutor>();
        services.AddScoped<IJobStepWriteExecutor, JobStepWriteExecutor>();
        services.AddDbContext<RentalCommandDbContext>((provider, options) =>
            options.UseNpgsql(_context.ConnectionString).UseAtomicPersistenceKernel(provider));
        _services = services.BuildServiceProvider();
    }

    public async Task DisposeAsync()
    {
        await _services.DisposeAsync();
        await _context.DisposeAsync();
    }

    [Fact]
    public async Task SixLegacyReceiptShapesReplayThroughTheirNewExecutorsWithoutNewRows()
    {
        await using var scope = _services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<RentalCommandDbContext>();
        var requests = scope.ServiceProvider.GetRequiredService<IRequestWriteExecutor>();
        var jobs = scope.ServiceProvider.GetRequiredService<IJobStepWriteExecutor>();
        var now = new DateTime(2026, 8, 21, 12, 0, 0, DateTimeKind.Utc);
        var notificationCount = await _context.Db.Notifications.CountAsync();
        var draftCount = await _context.Db.NoticeDrafts.CountAsync();
        var outboxCount = await _context.Db.OutboxMessages.CountAsync();

        var notification = new AtomicNotificationMutationCommand(
            1, 1, Guid.NewGuid(), 11, 7,
            AtomicNotificationMutationDomain.Broadcast, 0, string.Empty,
            "{\"title\":\"Legacy\"}", "legacy-notification-delivery");
        await AssertRequestReplayAsync(
            requests,
            AtomicNotificationMutation.Identity(notification).IdempotencyKey,
            AtomicNotificationMutation.Write(db, notification),
            new AtomicNotificationMutationResult(true, true, 71, 1, "{\"id\":71}"),
            now);

        var draft = new AtomicNoticeDraftMutationCommand(
            1, 1, Guid.NewGuid(), 12, 8,
            AtomicNoticeDraftOperation.Update, 72,
            "{\"subject\":\"Legacy draft\"}", "legacy-draft-delivery");
        await AssertRequestReplayAsync(
            requests,
            AtomicNoticeDraftMutation.Identity(draft).IdempotencyKey,
            AtomicNoticeDraftMutation.Write(db, draft),
            new AtomicNoticeDraftMutationResult(true, true, 72, 0, "{\"id\":72}"),
            now);

        var delivery = new AtomicNoticeDeliveryCommand(
            1, null, null, null, null, 73,
            [NoticeDeliveryChannel.Email], null, null, "legacy-notice-delivery");
        await AssertRequestReplayAsync(
            requests,
            AtomicNoticeDelivery.Identity(delivery).IdempotencyKey,
            AtomicNoticeDelivery.Write(db, delivery),
            new AtomicNoticeDeliveryResult(74, 73, 1),
            now);

        var ownerStatement = new QueueOwnerStatementEmailCommand(
            1, 1, Guid.NewGuid(), 13, 9, 75, 2026,
            "owner@example.test", "Legacy statement", "Legacy body", "legacy-owner-statement");
        await AssertRequestReplayAsync(
            requests,
            QueueOwnerStatementEmail.Identity(ownerStatement).IdempotencyKey,
            QueueOwnerStatementEmail.Write(db, ownerStatement),
            new QueueOwnerStatementEmailResult(true),
            now);

        var claim = new ApplyClaimedTenantNoticeDraftBatchCommand(Guid.NewGuid());
        var claimHandler = new ApplyClaimedTenantNoticeDraftBatchHandler(db);
        await AssertJobReplayAsync(
            jobs,
            TenantNoticeDraftAutomation.Identity(claim).IdempotencyKey,
            TenantNoticeDraftAutomation.Write(
                claim, claimHandler.ExecuteAsync, claimHandler.AuthorizeAsync),
            new ApplyClaimedTenantNoticeDraftBatchResult(0, []),
            now);

        var briefing = new EnqueueMorningBriefingsCommand(now);
        await AssertJobReplayAsync(
            jobs,
            DailyBriefingDeliveryService.Identity(briefing).IdempotencyKey,
            DailyBriefingDeliveryService.Write(db, briefing),
            new EnqueueMorningBriefingsResult(6),
            now);

        _context.Db.ChangeTracker.Clear();
        (await _context.Db.Notifications.CountAsync()).Should().Be(notificationCount);
        (await _context.Db.NoticeDrafts.CountAsync()).Should().Be(draftCount);
        (await _context.Db.OutboxMessages.CountAsync()).Should().Be(outboxCount);
    }

    private async Task AssertRequestReplayAsync<TCommand, TResult>(
        IRequestWriteExecutor executor,
        string key,
        TransactionalWrite<TCommand, TResult> productionWrite,
        TResult stored,
        DateTime now)
        where TCommand : notnull, IAtomicCommandData
        where TResult : notnull
    {
        await SeedLegacyReceiptAsync(key, productionWrite, stored, now);
        var replay = await executor.ExecuteAsync(key, ReplayOnly(productionWrite));
        replay.Disposition.Should().Be(AtomicCommandDisposition.Replayed);
        replay.Value.Should().BeEquivalentTo(stored);
    }

    private async Task AssertJobReplayAsync<TCommand, TResult>(
        IJobStepWriteExecutor executor,
        string key,
        TransactionalWrite<TCommand, TResult> productionWrite,
        TResult stored,
        DateTime now)
        where TCommand : notnull, IAtomicCommandData
        where TResult : notnull
    {
        await SeedLegacyReceiptAsync(key, productionWrite, stored, now);
        var replay = await executor.ExecuteAsync(key, ReplayOnly(productionWrite));
        replay.Disposition.Should().Be(AtomicCommandDisposition.Replayed);
        replay.Value.Should().BeEquivalentTo(stored);
    }

    private async Task SeedLegacyReceiptAsync<TCommand, TResult>(
        string key,
        TransactionalWrite<TCommand, TResult> write,
        TResult stored,
        DateTime now)
        where TCommand : notnull, IAtomicCommandData
        where TResult : notnull
    {
        var codec = new AtomicJsonResultCodec<TResult>(write.ResultContract);
        _context.Db.AtomicCommandReceipts.Add(new AtomicCommandReceipt
        {
            Id = Guid.NewGuid(),
            AttemptId = Guid.NewGuid(),
            CommandType = write.OperationName,
            IdempotencyKey = key,
            RequestFingerprint = AtomicCommandFingerprint.Create(write.Request),
            Status = AtomicCommandReceiptStatus.Completed,
            ResultContract = write.ResultContract,
            ResultJson = codec.Serialize(stored),
            StartedAt = now,
            CompletedAt = now,
        });
        await _context.Db.SaveChangesAsync();
        _context.Db.ChangeTracker.Clear();
    }

    private static TransactionalWrite<TCommand, TResult> ReplayOnly<TCommand, TResult>(
        TransactionalWrite<TCommand, TResult> productionWrite)
        where TCommand : notnull, IAtomicCommandData
        where TResult : notnull => new(
            productionWrite.OperationName,
            productionWrite.IdempotencyPolicy,
            productionWrite.Request,
            productionWrite.ResultContract,
            productionWrite.LockPlan,
            (_, _, _) => throw new InvalidOperationException("A seeded receipt must not execute the write body."),
            (_, _, _) => Task.CompletedTask);
}
