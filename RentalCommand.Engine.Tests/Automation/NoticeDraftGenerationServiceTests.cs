using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using RentalCommand.Api.DTOs;
using RentalCommand.Api.Services.Domain;
using RentalCommand.Core.Atomic;
using RentalCommand.Core.Automation;
using RentalCommand.Core.Enums;
using RentalCommand.Data.Notifications;
using RentalCommand.Engine.Services;

namespace RentalCommand.Engine.Tests.Automation;

public sealed class NoticeDraftGenerationServiceTests
{
    [Fact]
    public async Task AutoPolicy_UsesExactSetResultAndAtomicallyQueuesEnabledChannels()
    {
        var token = Guid.Empty;
        var work = Work(isAuto: true);
        var claims = Claims(work, claimedToken => token = claimedToken);
        var atomic = new Mock<IAtomicUnitOfWork>(MockBehavior.Strict);
        atomic.Setup(unit => unit.ExecuteAsync(
                It.IsAny<AtomicCommandIdentity>(),
                It.Is<ApplyClaimedTenantNoticeDraftBatchCommand>(command =>
                    command.ClaimToken == token && command.ClaimToken != Guid.Empty),
                TenantNoticeDraftAutomation.Codec,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => AtomicOutcome(token, [
                new AtomicGeneratedTenantNoticeDraft
                {
                    WorkItemId = work.Id,
                    DraftId = 56,
                    WasCreated = true,
                    CreatedCount = 1,
                    LeaseManagementId = work.LeaseManagementId,
                    TenantLedgerEntryId = work.TenantLedgerEntryId,
                    NoticeType = work.AutomationKey,
                    Status = "Draft",
                },
            ]));
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
            atomic.Object,
            foundation.Object,
            NullLogger<NoticeDraftGenerationService>.Instance,
            TimeProvider.System);

        (await service.GenerateAllAsync()).Should().Be(1);
        foundation.VerifyAll();
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
        var atomic = new Mock<IAtomicUnitOfWork>(MockBehavior.Strict);
        atomic.Setup(unit => unit.ExecuteAsync(
                It.IsAny<AtomicCommandIdentity>(),
                It.Is<ApplyClaimedTenantNoticeDraftBatchCommand>(command =>
                    command.ClaimToken == token && command.ClaimToken != Guid.Empty),
                TenantNoticeDraftAutomation.Codec,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => AtomicOutcome(token, [
                new AtomicGeneratedTenantNoticeDraft
                {
                    WorkItemId = work.Id,
                    DraftId = 56,
                    WasCreated = true,
                    CreatedCount = 1,
                    LeaseManagementId = work.LeaseManagementId,
                    TenantLedgerEntryId = work.TenantLedgerEntryId,
                    NoticeType = work.AutomationKey,
                    Status = "Draft",
                },
            ]));

        var service = new NoticeDraftGenerationService(
            claims.Object,
            atomic.Object,
            Mock.Of<INotificationFoundationService>(),
            NullLogger<NoticeDraftGenerationService>.Instance,
            TimeProvider.System);

        (await service.GenerateAllAsync()).Should().Be(1);
        claims.VerifyAll();
    }

    private static AtomicCommandOutcome<ApplyClaimedTenantNoticeDraftBatchResult> AtomicOutcome(
        Guid expectedToken,
        AtomicGeneratedTenantNoticeDraft[] drafts)
    {
        expectedToken.Should().NotBeEmpty();
        return new AtomicCommandOutcome<ApplyClaimedTenantNoticeDraftBatchResult>(
            new ApplyClaimedTenantNoticeDraftBatchResult(
                drafts.FirstOrDefault()?.CreatedCount ?? 0,
                drafts),
            AtomicCommandDisposition.Executed,
            Guid.NewGuid());
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
