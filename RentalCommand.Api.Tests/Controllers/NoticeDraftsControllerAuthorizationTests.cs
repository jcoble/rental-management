using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Moq;
using RentalCommand.Api.Auth;
using RentalCommand.Api.Controllers;
using RentalCommand.Api.DTOs;
using RentalCommand.Api.Services.Domain;
using RentalCommand.Core.Authorization;
using RentalCommand.Core.Enums;

namespace RentalCommand.Api.Tests.Controllers;

public sealed class NoticeDraftsControllerAuthorizationTests
{
    [Fact]
    public void AtomicNoticeDelivery_UsesSecurityClockForAuthorizationPredicates()
    {
        var source = File.ReadAllText(Path.Combine(
            RepositoryRoot(),
            "RentalCommand.Api",
            "Services",
            "Domain",
            "AtomicNoticeDeliveryRule.cs"));

        source.Should().Contain(
            "await AuthorizeCallerAsync(command, _db, securityNow, requireActiveClaim: true, ct)");
        source.Should().Contain(
            "var authorizedProperties = AuthorizedProperties(command, _db, securityNow);");
        source.Should().Contain(
            "&& AuthorizedProperties(command, db, now).Any(property =>");
        source.Should().NotContain(
            "var authorizedProperties = AuthorizedProperties(command, _db, now);");
    }

    [Fact]
    public async Task Approve_MapsOnlyNoticeApprovalAuthorizationFailuresToForbidden()
    {
        var intended = new NoticeApprovalAuthorizationException(
            "The current Team role cannot manage this tenant notice.");
        var controller = ControllerThrowing(intended);

        var result = await controller.Approve(
            214,
            new ApproveNoticeDraftRequest { Channels = ["Portal"] },
            "notice-approval-auth-denied",
            CancellationToken.None);

        var forbidden = result.Result.Should().BeOfType<ObjectResult>().Subject;
        forbidden.StatusCode.Should().Be(StatusCodes.Status403Forbidden);
    }

    [Fact]
    public async Task Approve_DoesNotMapGenericUnauthorizedInfrastructureFailures()
    {
        var controller = ControllerThrowing(new UnauthorizedAccessException("RLS write denied."));

        var act = () => controller.Approve(
            214,
            new ApproveNoticeDraftRequest { Channels = ["Portal"] },
            "notice-approval-infra-denied",
            CancellationToken.None);

        await act.Should().ThrowAsync<UnauthorizedAccessException>()
            .WithMessage("RLS write denied.");
    }

    private static NoticeDraftsController ControllerThrowing(Exception exception)
    {
        var drafts = new Mock<INoticeDraftService>(MockBehavior.Strict);
        var foundation = new Mock<INotificationFoundationService>(MockBehavior.Strict);
        foundation
            .Setup(service => service.ApproveAndQueueAsync(
                It.IsAny<NoticeApprovalExecutionContext>(),
                It.IsAny<int>(),
                It.IsAny<ApproveAndQueueNoticeRequest>(),
                It.IsAny<TenantNoticeWorkFence?>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(exception);

        var controller = new NoticeDraftsController(drafts.Object, foundation.Object)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext(),
            },
        };
        controller.HttpContext.Items[CanonicalAccessContextHttpItem.Key] = new ActiveAccessContext(
            Guid.NewGuid(),
            UserId: 10,
            AccessContextId: 20,
            PortfolioId: 30,
            AccessRevision: 40,
            LastAuthorizedExperience: WorkspaceExperience.Management,
            WorkspaceMembershipId: 50,
            DefaultExperience: WorkspaceExperience.Management);
        return controller;
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null &&
               !File.Exists(Path.Combine(directory.FullName, "RentalCommand.sln")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName
            ?? throw new DirectoryNotFoundException("Could not locate repository root.");
    }
}
