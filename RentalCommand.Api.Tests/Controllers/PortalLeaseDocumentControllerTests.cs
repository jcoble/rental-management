using System.Text;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Moq;
using RentalCommand.Api.Auth;
using RentalCommand.Api.Controllers;
using RentalCommand.Api.Services.Domain;
using RentalCommand.Api.Services.Payments;
using RentalCommand.Core.Authorization;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Interfaces;

namespace RentalCommand.Api.Tests.Controllers;

public sealed class PortalLeaseDocumentControllerTests
{
    private static readonly ActiveAccessContext ActiveContext = new(
        SessionId: Guid.Parse("11111111-1111-1111-1111-111111111111"),
        UserId: 17,
        AccessContextId: 23,
        PortfolioId: 31,
        AccessRevision: 41,
        LastAuthorizedExperience: WorkspaceExperience.Tenant,
        WorkspaceMembershipId: null,
        DefaultExperience: WorkspaceExperience.Tenant);

    [Fact]
    public async Task DownloadExecutedAgreement_WhenArtifactIsAvailable_ReturnsAuthoritativeFileAndPassesTenantScope()
    {
        const int leaseManagementId = 51;
        const int leaseAgreementId = 61;
        const string storageKey = "tenant/legal/executed-61.pdf";
        const string fileName = "authoritative-executed-agreement.pdf";
        const string contentType = "application/pdf";
        var bytes = Encoding.UTF8.GetBytes("signed agreement bytes");
        var service = new Mock<IPortalService>(MockBehavior.Strict);
        var files = new Mock<IFileStorage>(MockBehavior.Strict);
        PortalTenantReadScope? capturedScope = null;
        int? capturedLeaseManagementId = null;
        int? capturedLeaseAgreementId = null;

        service.Setup(candidate => candidate.GetExecutedAgreementArtifactAsync(
                It.IsAny<PortalTenantReadScope>(),
                It.IsAny<int>(),
                It.IsAny<int>(),
                It.IsAny<CancellationToken>()))
            .Callback<PortalTenantReadScope, int, int, CancellationToken>((scope, managementId, agreementId, _) =>
            {
                capturedScope = scope;
                capturedLeaseManagementId = managementId;
                capturedLeaseAgreementId = agreementId;
            })
            .ReturnsAsync(new LegalArtifactFileReference(
                FileAuthorityId: 71,
                StorageKey: storageKey,
                FileName: fileName,
                ContentType: contentType));
        files.Setup(candidate => candidate.DownloadAsync(storageKey, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MemoryStream(bytes, writable: false));
        var controller = CreateController(service.Object, files.Object);

        var result = await controller.DownloadExecutedAgreement(
            leaseManagementId,
            leaseAgreementId,
            CancellationToken.None);

        var file = result.Should().BeOfType<FileStreamResult>().Subject;
        file.ContentType.Should().Be(contentType);
        file.FileDownloadName.Should().Be(fileName);
        file.EnableRangeProcessing.Should().BeTrue();
        controller.Response.Headers["X-Content-Type-Options"].ToString().Should().Be("nosniff");
        file.FileStream.Should().BeOfType<MemoryStream>();
        ((MemoryStream)file.FileStream).ToArray().Should().Equal(bytes);
        capturedScope.Should().Be(new PortalTenantReadScope(
            ActiveContext.PortfolioId,
            ActiveContext.UserId,
            ActiveContext.AccessContextId,
            ActiveContext.AccessRevision));
        capturedLeaseManagementId.Should().Be(leaseManagementId);
        capturedLeaseAgreementId.Should().Be(leaseAgreementId);
        service.VerifyAll();
        files.VerifyAll();
    }

    [Fact]
    public async Task DownloadExecutedAgreement_WhenServiceReturnsNull_ReturnsExistenceSafeNotFoundAndDoesNotOpenStorage()
    {
        const int leaseManagementId = 52;
        const int leaseAgreementId = 62;
        var service = new Mock<IPortalService>(MockBehavior.Strict);
        var files = new Mock<IFileStorage>(MockBehavior.Strict);
        service.Setup(candidate => candidate.GetExecutedAgreementArtifactAsync(
                It.Is<PortalTenantReadScope>(scope =>
                    scope.PortfolioId == ActiveContext.PortfolioId &&
                    scope.UserId == ActiveContext.UserId &&
                    scope.AccessContextId == ActiveContext.AccessContextId &&
                    scope.AccessRevision == ActiveContext.AccessRevision),
                leaseManagementId,
                leaseAgreementId,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((LegalArtifactFileReference?)null);
        var controller = CreateController(service.Object, files.Object);

        var result = await controller.DownloadExecutedAgreement(
            leaseManagementId,
            leaseAgreementId,
            CancellationToken.None);

        result.Should().BeOfType<NotFoundObjectResult>();
        files.Verify(candidate => candidate.DownloadAsync(
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()),
            Times.Never);
        service.VerifyAll();
    }

    [Fact]
    public async Task DownloadExecutedAgreement_WhenStorageOpenFails_ReturnsNotFoundWithoutLeakingStorageKey()
    {
        const int leaseManagementId = 53;
        const int leaseAgreementId = 63;
        const string storageKey = "private/storage/key/executed-63.pdf";
        var service = new Mock<IPortalService>(MockBehavior.Strict);
        var files = new Mock<IFileStorage>(MockBehavior.Strict);
        service.Setup(candidate => candidate.GetExecutedAgreementArtifactAsync(
                It.IsAny<PortalTenantReadScope>(),
                leaseManagementId,
                leaseAgreementId,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new LegalArtifactFileReference(
                FileAuthorityId: 73,
                StorageKey: storageKey,
                FileName: "executed-63.pdf",
                ContentType: "application/pdf"));
        files.Setup(candidate => candidate.DownloadAsync(storageKey, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new FileNotFoundException("missing object", storageKey));
        var controller = CreateController(service.Object, files.Object);

        var result = await controller.DownloadExecutedAgreement(
            leaseManagementId,
            leaseAgreementId,
            CancellationToken.None);

        var notFound = result.Should().BeOfType<NotFoundObjectResult>().Subject;
        notFound.Value.Should().NotBeNull();
        notFound.Value!.ToString().Should().NotContain(storageKey);
        service.VerifyAll();
        files.VerifyAll();
    }

    private static PortalController CreateController(
        IPortalService service,
        IFileStorage files)
    {
        var httpContext = new DefaultHttpContext();
        httpContext.Items[CanonicalAccessContextHttpItem.Key] = ActiveContext;
        return new PortalController(
            service,
            Mock.Of<IConversationService>(),
            Mock.Of<IStripePaymentService>(),
            files,
            Mock.Of<IAccountingLedgerReadModelService>())
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = httpContext,
            },
        };
    }
}
