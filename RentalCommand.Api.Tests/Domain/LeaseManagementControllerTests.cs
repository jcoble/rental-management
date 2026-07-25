using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Moq;
using RentalCommand.Api.Auth;
using RentalCommand.Api.Controllers;
using RentalCommand.Api.DTOs;
using RentalCommand.Api.Services.Domain;
using RentalCommand.Core.Atomic;
using RentalCommand.Core.Authorization;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Leasing;

namespace RentalCommand.Api.Tests.Domain;

public sealed class LeaseManagementControllerTests
{
    private const int PortfolioId = 19;
    private const int UserId = 23;
    private const int AccessContextId = 29;
    private const long AccessRevision = 31;
    private static readonly Guid SessionId = Guid.Parse("8b47e592-15c7-44ce-a9c4-2201fda01077");

    [Fact]
    public async Task CancelPlannedRelationship_RecordsReasonWithoutOccupancy()
    {
        CancelPlannedRelationshipCommand? capturedCommand = null;
        var canceledAt = new DateTime(2026, 7, 24, 12, 0, 0, DateTimeKind.Utc);
        var atomic = new Mock<IAtomicUnitOfWork>(MockBehavior.Strict);
        atomic.Setup(service => service.ExecuteAsync<
                CancelPlannedRelationshipCommand, CancelPlannedRelationshipResult>(
                It.IsAny<AtomicCommandIdentity>(),
                It.IsAny<CancelPlannedRelationshipCommand>(),
                It.IsAny<IAtomicResultCodec<CancelPlannedRelationshipResult>>(),
                It.IsAny<CancellationToken>()))
            .Callback<AtomicCommandIdentity, CancelPlannedRelationshipCommand,
                IAtomicResultCodec<CancelPlannedRelationshipResult>, CancellationToken>(
                (_, command, _, _) => capturedCommand = command)
            .ReturnsAsync(new AtomicCommandOutcome<CancelPlannedRelationshipResult>(
                new CancelPlannedRelationshipResult(
                    CancelPlannedRelationshipOutcome.Canceled,
                    41,
                    43,
                    canceledAt,
                    canceledAt,
                    [47],
                    [53],
                    [59],
                    [61],
                    null),
                AtomicCommandDisposition.Executed,
                Guid.NewGuid()));
        var controller = CreateLeaseController(atomic.Object);

        var result = await controller.CancelPlannedRelationship(
            41,
            "cancel-operation",
            new CancelPlannedRelationshipRequest
            {
                UnitId = 43,
                CancellationReasonCode = "APPLICANT_WITHDREW",
                CancellationNote = "Applicant chose another home.",
                DraftCancellationReason = "Withdrawn before possession.",
                Accesses =
                [
                    new CancelPlannedRelationshipAccessRequest
                    {
                        TenantUserAccessId = 59,
                        Disposition = CancelPlannedAccessDisposition.RevokeNow,
                    },
                    new CancelPlannedRelationshipAccessRequest
                    {
                        TenantUserAccessId = 61,
                        Disposition = CancelPlannedAccessDisposition.Retain,
                    },
                ],
            },
            CancellationToken.None);

        var response = result.Should().BeOfType<OkObjectResult>().Which.Value.Should()
            .BeOfType<CancelPlannedRelationshipResponse>().Which;
        response.CanceledAtUtc.Should().Be(canceledAt);
        response.RevokedAccessIds.Should().Equal(59);
        response.RetainedAccessIds.Should().Equal(61);
        response.Replayed.Should().BeFalse();
        capturedCommand.Should().NotBeNull();
        capturedCommand!.PortfolioId.Should().Be(PortfolioId);
        capturedCommand.LeaseManagementId.Should().Be(41);
        capturedCommand.UnitId.Should().Be(43);
        capturedCommand.CreatedByUserId.Should().Be(UserId);
        capturedCommand.AuthSessionId.Should().Be(SessionId);
        capturedCommand.AccessContextId.Should().Be(AccessContextId);
        capturedCommand.ExpectedAccessRevision.Should().Be(AccessRevision);
        capturedCommand.CancellationReasonCode.Should().Be("APPLICANT_WITHDREW");
        capturedCommand.CancellationNote.Should().Be("Applicant chose another home.");
        capturedCommand.DraftCancellationReason.Should().Be("Withdrawn before possession.");
        capturedCommand.Accesses.Should().BeEquivalentTo(
        new[]
        {
            new CancelPlannedRelationshipAccess(59, CancelPlannedAccessDisposition.RevokeNow),
            new CancelPlannedRelationshipAccess(61, CancelPlannedAccessDisposition.Retain),
        });
        atomic.VerifyAll();
    }

    [Fact]
    public async Task TransferToUnit_IsAtomic()
    {
        AtomicCommandIdentity? capturedIdentity = null;
        TransferLeaseManagementCommand? capturedCommand = null;
        var returnedAt = new DateTime(2026, 7, 24, 13, 0, 0, DateTimeKind.Utc);
        var transferPublicId = Guid.Parse("84c89a66-9025-4fd7-aa4a-c015897aa7fd");
        var atomic = new Mock<IAtomicUnitOfWork>(MockBehavior.Strict);
        atomic.Setup(service => service.ExecuteAsync<
                TransferLeaseManagementCommand, TransferLeaseManagementResult>(
                It.IsAny<AtomicCommandIdentity>(),
                It.IsAny<TransferLeaseManagementCommand>(),
                It.IsAny<IAtomicResultCodec<TransferLeaseManagementResult>>(),
                It.IsAny<CancellationToken>()))
            .Callback<AtomicCommandIdentity, TransferLeaseManagementCommand,
                IAtomicResultCodec<TransferLeaseManagementResult>, CancellationToken>(
                (identity, command, _, _) =>
                {
                    capturedIdentity = identity;
                    capturedCommand = command;
                })
            .ReturnsAsync(new AtomicCommandOutcome<TransferLeaseManagementResult>(
                new TransferLeaseManagementResult(
                    TransferLeaseManagementOutcome.Transferred,
                    transferPublicId,
                    67,
                    71,
                    73,
                    79,
                    83,
                    89,
                    null,
                    97,
                    returnedAt,
                    null,
                    125m,
                    500m,
                    [101],
                    [103],
                    [107],
                    [109],
                    [113],
                    [127],
                    [131],
                    null),
                AtomicCommandDisposition.Replayed,
                Guid.NewGuid()));
        var controller = CreateLeaseController(atomic.Object);
        var effectiveOn = new DateOnly(2026, 8, 1);

        var result = await controller.TransferToUnit(
            67,
            "transfer-operation",
            new TransferLeaseManagementRequest
            {
                SourceUnitId = 71,
                DestinationUnitId = 79,
                EffectiveOn = effectiveOn,
                GiveDestinationPossessionNow = false,
                DestinationDocumentTemplateId = 137,
                CarryTenantBalance = true,
                CarrySecurityDeposit = true,
                TransferReason = "Household requested another Unit.",
            },
            CancellationToken.None);

        var created = result.Should().BeOfType<ObjectResult>().Which;
        created.StatusCode.Should().Be(StatusCodes.Status201Created);
        var response = created.Value.Should().BeOfType<TransferLeaseManagementResponse>().Which;
        response.DestinationLeaseManagementId.Should().Be(73);
        response.DestinationTenantAccountId.Should().Be(83);
        response.TurnoverPeriodId.Should().Be(97);
        response.Replayed.Should().BeTrue();
        capturedIdentity.Should().NotBeNull();
        capturedIdentity!.CommandType.Should().Be("lease-management.transfer-unit");
        capturedCommand.Should().NotBeNull();
        capturedCommand!.PortfolioId.Should().Be(PortfolioId);
        capturedCommand.SourceLeaseManagementId.Should().Be(67);
        capturedCommand.SourceUnitId.Should().Be(71);
        capturedCommand.DestinationUnitId.Should().Be(79);
        capturedCommand.CreatedByUserId.Should().Be(UserId);
        capturedCommand.AuthSessionId.Should().Be(SessionId);
        capturedCommand.AccessContextId.Should().Be(AccessContextId);
        capturedCommand.ExpectedAccessRevision.Should().Be(AccessRevision);
        capturedCommand.EffectiveOn.Should().Be(effectiveOn);
        capturedCommand.DestinationDocumentTemplateId.Should().Be(137);
        capturedCommand.TransferReason.Should().Be("Household requested another Unit.");
        atomic.VerifyAll();
    }

    [Fact]
    public async Task CloseAccount_BlocksUntilPossessionAndMoneyConditionsPass()
    {
        CloseTenantAccountCommand? capturedCommand = null;
        var closedAt = new DateTime(2026, 7, 24, 14, 0, 0, DateTimeKind.Utc);
        var outcomes = new Queue<AtomicCommandOutcome<CloseTenantAccountResult>>(
        [
            CloseOutcome(CloseTenantAccountOutcome.PossessionNotReturned,
                "Possession must be returned."),
            CloseOutcome(CloseTenantAccountOutcome.NonzeroReceivableBalance,
                "Receivable must be zero."),
            CloseOutcome(CloseTenantAccountOutcome.NonzeroDepositBalance,
                "Deposit must be zero."),
            CloseOutcome(CloseTenantAccountOutcome.UnfinishedWorkflow,
                "Drafts, signatures, payments, and autopay must be resolved."),
            CloseOutcome(CloseTenantAccountOutcome.Closed, null, closedAt),
        ]);
        var atomic = new Mock<IAtomicUnitOfWork>(MockBehavior.Strict);
        atomic.Setup(service => service.ExecuteAsync<
                CloseTenantAccountCommand, CloseTenantAccountResult>(
                It.IsAny<AtomicCommandIdentity>(),
                It.IsAny<CloseTenantAccountCommand>(),
                It.IsAny<IAtomicResultCodec<CloseTenantAccountResult>>(),
                It.IsAny<CancellationToken>()))
            .Callback<AtomicCommandIdentity, CloseTenantAccountCommand,
                IAtomicResultCodec<CloseTenantAccountResult>, CancellationToken>(
                (_, command, _, _) => capturedCommand = command)
            .ReturnsAsync(() => outcomes.Dequeue());
        var controller = CreateCloseController(atomic.Object);
        var request = new CloseTenantAccountRequest
        {
            TenantAccountId = 149,
            CloseReasonCode = "TRANSFERRED",
            CloseNote = "Closed after transfer.",
        };

        for (var index = 0; index < 4; index++)
        {
            var blocked = await controller.Close(
                139, $"close-blocked-{index}", request, CancellationToken.None);
            blocked.Should().BeOfType<ConflictObjectResult>();
        }

        var closed = await controller.Close(
            139, "close-success", request, CancellationToken.None);

        closed.Should().BeOfType<OkObjectResult>().Which.Value.Should().BeEquivalentTo(new
        {
            Value = new CloseTenantAccountResult(
                CloseTenantAccountOutcome.Closed, 139, 149, closedAt, null),
            replayed = false,
        });
        outcomes.Should().BeEmpty();
        capturedCommand.Should().NotBeNull();
        capturedCommand!.PortfolioId.Should().Be(PortfolioId);
        capturedCommand.LeaseManagementId.Should().Be(139);
        capturedCommand.TenantAccountId.Should().Be(149);
        capturedCommand.CloseReasonCode.Should().Be("TRANSFERRED");
        capturedCommand.CloseNote.Should().Be("Closed after transfer.");
        capturedCommand.ActorUserId.Should().Be(UserId);
        capturedCommand.AuthSessionId.Should().Be(SessionId);
        capturedCommand.AccessContextId.Should().Be(AccessContextId);
        capturedCommand.ExpectedAccessRevision.Should().Be(AccessRevision);
        atomic.Verify(service => service.ExecuteAsync<
            CloseTenantAccountCommand, CloseTenantAccountResult>(
            It.IsAny<AtomicCommandIdentity>(),
            It.IsAny<CloseTenantAccountCommand>(),
            It.IsAny<IAtomicResultCodec<CloseTenantAccountResult>>(),
            It.IsAny<CancellationToken>()), Times.Exactly(5));
    }

    private static AtomicCommandOutcome<CloseTenantAccountResult> CloseOutcome(
        CloseTenantAccountOutcome outcome,
        string? error,
        DateTime? closedAt = null) => new(
            new CloseTenantAccountResult(outcome, 139, 149, closedAt, error),
            AtomicCommandDisposition.Executed,
            Guid.NewGuid());

    private static LeaseManagementController CreateLeaseController(IAtomicUnitOfWork atomic) =>
        WithAccess(new LeaseManagementController(
            atomic,
            Mock.Of<ILeaseManagementQueryService>(),
            Mock.Of<ILeaseQaService>(),
            new ConfigurationBuilder().Build()));

    private static TenantAccountLifecycleController CreateCloseController(
        IAtomicUnitOfWork atomic) =>
        WithAccess(new TenantAccountLifecycleController(atomic));

    private static TController WithAccess<TController>(TController controller)
        where TController : ControllerBase
    {
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext(),
        };
        controller.HttpContext.Items[CanonicalAccessContextHttpItem.Key] = new ActiveAccessContext(
            SessionId,
            UserId,
            AccessContextId,
            PortfolioId,
            AccessRevision,
            WorkspaceExperience.Management,
            WorkspaceMembershipId: 37,
            DefaultExperience: WorkspaceExperience.Management);
        return controller;
    }
}
