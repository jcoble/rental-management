using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using RentalCommand.Api.DTOs;
using RentalCommand.Api.Services.Domain;
using RentalCommand.Core.Atomic;
using RentalCommand.Core.Notifications;
using RentalCommand.Core.Automation;
using RentalCommand.Core.Enums;
using RentalCommand.Data;
using RentalCommand.Data.Notifications;
using RentalCommand.Engine.Services;
using RentalCommand.Engine.Writes;

namespace RentalCommand.Engine.Tests.Automation;

public sealed class NoticeDraftGenerationServiceTests
{
    [Fact]
    public async Task AutoPolicy_UsesExactSetResultAndAtomicallyQueuesEnabledChannels()
    {
        var token = Guid.Empty;
        var work = Work(isAuto: true);
        var claims = Claims(work, claimedToken => token = claimedToken);
        await using var harness = new NoticeDraftHarness(work);
        var foundation = new Mock<INotificationFoundationService>(MockBehavior.Strict);
        foundation.Setup(service => service.ApproveAndQueueAsync(
                It.Is<NoticeApprovalExecutionContext>(context =>
                    context.PortfolioId == work.PortfolioId && context.ActorUserId == null),
                56,
                It.Is<ApproveAndQueueNoticeRequest>(request =>
                    request.Channels.SequenceEqual(new[]
                    {
                        NoticeDeliveryChannel.TenantPortal,
                        NoticeDeliveryChannel.Email,
                    })),
                It.Is<TenantNoticeWorkFence>(fence =>
                    fence.WorkItemId == work.Id && fence.ClaimToken == token),
                $"tenant-notice-work:{work.Id}:approve",
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(78);

        var service = new NoticeDraftGenerationService(
            claims.Object,
            harness.Writes,
            harness.Db,
            foundation.Object,
            NullLogger<NoticeDraftGenerationService>.Instance,
            TimeProvider.System);

        (await service.GenerateAllAsync()).Should().Be(1);
        foundation.VerifyAll();
        harness.Writes.ExecutedToken.Should().Be(token);
        claims.Verify(store => store.CompleteAsync(
            It.IsAny<long>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task DraftPolicy_CompletesExactWorkWithoutApproval()
    {
        var token = Guid.Empty;
        var work = Work(isAuto: false);
        var claims = Claims(work, claimedToken => token = claimedToken);
        claims.Setup(store => store.CompleteAsync(
                work.Id,
                It.Is<Guid>(claimedToken => claimedToken == token),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        await using var harness = new NoticeDraftHarness(work);

        var service = new NoticeDraftGenerationService(
            claims.Object,
            harness.Writes,
            harness.Db,
            Mock.Of<INotificationFoundationService>(),
            NullLogger<NoticeDraftGenerationService>.Instance,
            TimeProvider.System);

        (await service.GenerateAllAsync()).Should().Be(1);
        claims.VerifyAll();
        harness.Writes.ExecutedToken.Should().Be(token);
    }

    /// <summary>
    /// SQLite harness for the Engine orchestration. The service still creates the production command,
    /// operation, result contract, and step key; only the PostgreSQL set command is substituted.
    /// </summary>
    private sealed class NoticeDraftHarness : IAsyncDisposable
    {
        private readonly SqliteConnection _connection = new("DataSource=:memory:");

        public NoticeDraftHarness(ClaimedTenantNoticeWorkItem work)
        {
            _connection.Open();
            Db = new RentalCommandDbContext(
                new DbContextOptionsBuilder<RentalCommandDbContext>().UseSqlite(_connection).Options);
            Writes = new TestJobStepWriteExecutor(work);
        }

        public RentalCommandDbContext Db { get; }
        public TestJobStepWriteExecutor Writes { get; }

        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
            await _connection.DisposeAsync();
        }
    }

    private sealed class TestJobStepWriteExecutor(ClaimedTenantNoticeWorkItem work)
        : IJobStepWriteExecutor
    {
        public Guid ExecutedToken { get; private set; }

        public Task<AtomicCommandOutcome<TResult>> ExecuteAsync<TCommand, TResult>(
            string stepKey,
            TransactionalWrite<TCommand, TResult> write,
            CancellationToken ct = default)
            where TCommand : notnull, IAtomicCommandData
            where TResult : notnull
        {
            var command = write.Request.Should()
                .BeOfType<ApplyClaimedTenantNoticeDraftBatchCommand>().Subject;
            stepKey.Should().Be(command.ClaimToken.ToString("N"));
            write.OperationName.Should().Be("tenant-notice-draft.claimed-batch.apply");
            write.ResultContract.Should().Be("tenant-notice-draft.claimed-batch.apply.v1");
            ExecutedToken = command.ClaimToken;
            var draft = new AtomicGeneratedTenantNoticeDraft
            {
                WorkItemId = work.Id,
                DraftId = 56,
                WasCreated = true,
                CreatedCount = 1,
                LeaseManagementId = work.LeaseManagementId,
                TenantLedgerEntryId = work.TenantLedgerEntryId,
                NoticeType = work.AutomationKey,
                Status = "Draft",
            };
            var result = new ApplyClaimedTenantNoticeDraftBatchResult(1, [draft]);
            return Task.FromResult(new AtomicCommandOutcome<TResult>(
                (TResult)(object)result, AtomicCommandDisposition.Executed, Guid.NewGuid()));
        }
    }

    private static Mock<ITenantNoticeWorkClaimStore> Claims(
        ClaimedTenantNoticeWorkItem work,
        Action<Guid> captureToken)
    {
        var claims = new Mock<ITenantNoticeWorkClaimStore>(MockBehavior.Strict);
        claims.Setup(store => store.ClaimReadyAsync(
                It.IsAny<string>(),
                It.IsAny<Guid>(),
                It.IsAny<DateTime>(),
                It.IsAny<DateTime>(),
                50,
                It.IsAny<CancellationToken>()))
            .Callback<string, Guid, DateTime, DateTime, int, CancellationToken>(
                (_, token, _, _, _, _) => captureToken(token))
            .ReturnsAsync([work]);
        return claims;
    }

    private static ClaimedTenantNoticeWorkItem Work(bool isAuto) => new()
    {
        Id = 91,
        PortfolioId = 7,
        TenantNoticePolicyId = 12,
        LeaseManagementId = 23,
        RecipientLeaseManagementPartyId = 24,
        TenantLedgerEntryId = 34,
        DueAtUtc = DateTime.UtcNow.AddMinutes(-1),
        BusinessKey = "tenant-notice:7:12:23:party:24:ledger:34",
        AttemptCount = 1,
        AutomationKey = "rent-reminder",
        IsAuto = isAuto,
        FailureBehavior = nameof(NoticeFailureBehavior.RetryThenDraft),
        WorkspaceNoticeTemplateVersionId = 45,
        SendTenantPortal = true,
        SendMobilePush = false,
        SendEmail = true,
        SendSms = false,
    };
}
