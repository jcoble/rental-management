using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using RentalCommand.Api.DTOs;
using RentalCommand.Api.Services.Domain;
using RentalCommand.Core.Atomic;
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
        var drafts = new Mock<ITenantNoticeDraftSetStore>(MockBehavior.Strict);
        drafts.Setup(store => store.GenerateClaimedBatchAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([
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
            ]);
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
            drafts.Object,
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
        claims.Setup(store => store.CompleteAsync(work.Id, token, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        var drafts = new Mock<ITenantNoticeDraftSetStore>(MockBehavior.Strict);
        drafts.Setup(store => store.GenerateClaimedBatchAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([
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
            ]);

        var service = new NoticeDraftGenerationService(
            claims.Object,
            drafts.Object,
            Mock.Of<INotificationFoundationService>(),
            NullLogger<NoticeDraftGenerationService>.Instance,
            TimeProvider.System);

        (await service.GenerateAllAsync()).Should().Be(1);
        claims.VerifyAll();
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
        TenantLedgerEntryId = 34,
        DueAtUtc = DateTime.UtcNow.AddMinutes(-1),
        BusinessKey = "tenant-notice:7:12:23:ledger:34",
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
