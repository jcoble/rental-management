using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Moq;
using RentalCommand.Api.Auth;
using RentalCommand.Api.Controllers;
using RentalCommand.Api.DTOs;
using RentalCommand.Api.Services.Domain;
using RentalCommand.Api.Services.Payments;
using RentalCommand.Core.Authorization;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Interfaces;

namespace RentalCommand.Api.Tests.Controllers;

public sealed class PortalPaymentControllerTests
{
    private static readonly ActiveAccessContext ActiveContext = new(
        SessionId: Guid.Parse("22222222-2222-2222-2222-222222222222"),
        UserId: 17,
        AccessContextId: 23,
        PortfolioId: 31,
        AccessRevision: 41,
        LastAuthorizedExperience: WorkspaceExperience.Tenant,
        WorkspaceMembershipId: null,
        DefaultExperience: WorkspaceExperience.Tenant);

    [Fact]
    public async Task CreatePaymentCheckout_WhenServiceReportsSucceeded_ReturnsTypedAlreadyPaidResponse()
    {
        var portal = new Mock<IPortalService>(MockBehavior.Strict);
        portal.Setup(service => service.ResolveTenantIdAsync(
                ActiveContext.PortfolioId, ActiveContext.AccessContextId,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(99);
        var stripe = new Mock<IStripePaymentService>(MockBehavior.Strict);
        stripe.Setup(service => service.CreatePaymentCheckoutSessionAsync(
                ActiveContext.PortfolioId, 99, 7, 88, ActiveContext.UserId,
                null, null, It.IsAny<CancellationToken>(), "retry-key"))
            .ReturnsAsync(CheckoutResult.Succeeded(45, "pi_accepted"));
        var controller = CreateController(portal.Object, stripe.Object);

        var result = await controller.CreatePaymentCheckout(
            tenantAccountId: 7,
            chargeLedgerEntryId: 88,
            idempotencyKey: " retry-key ",
            request: null,
            CancellationToken.None);

        var response = result.Should().BeOfType<OkObjectResult>().Subject.Value
            .Should().BeOfType<CheckoutSessionResponse>().Subject;
        response.AlreadyPaid.Should().BeTrue();
        response.PaymentAttemptId.Should().Be(45);
        response.AttemptState.Should().Be("Succeeded");
        stripe.VerifyAll();
    }

    [Fact]
    public async Task CreatePaymentCheckout_WhenServiceReportsPending_ReturnsAttemptIdentityForReconciliation()
    {
        var portal = new Mock<IPortalService>(MockBehavior.Strict);
        portal.Setup(service => service.ResolveTenantIdAsync(
                ActiveContext.PortfolioId, ActiveContext.AccessContextId,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(99);
        var stripe = new Mock<IStripePaymentService>(MockBehavior.Strict);
        stripe.Setup(service => service.CreatePaymentCheckoutSessionAsync(
                ActiveContext.PortfolioId, 99, 7, 88, ActiveContext.UserId,
                null, null, It.IsAny<CancellationToken>(), "pending-key"))
            .ReturnsAsync(CheckoutResult.Pending(46, "Submitted", "pi_pending"));
        var controller = CreateController(portal.Object, stripe.Object);

        var result = await controller.CreatePaymentCheckout(
            7, 88, "pending-key", null, CancellationToken.None);

        var conflict = result.Should().BeOfType<ConflictObjectResult>().Subject;
        conflict.Value.Should().NotBeNull();
        conflict.Value!.ToString().Should().Contain("paymentAttemptId");
        conflict.Value.ToString().Should().Contain("Submitted");
        stripe.VerifyAll();
    }

    private static PortalController CreateController(
        IPortalService portal, IStripePaymentService stripe)
    {
        var httpContext = new DefaultHttpContext();
        httpContext.Items[CanonicalAccessContextHttpItem.Key] = ActiveContext;
        return new PortalController(
            portal,
            Mock.Of<IConversationService>(),
            stripe,
            Mock.Of<IFileStorage>())
        {
            ControllerContext = new ControllerContext { HttpContext = httpContext },
        };
    }
}
