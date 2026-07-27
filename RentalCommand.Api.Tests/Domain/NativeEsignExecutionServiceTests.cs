using FluentAssertions;
using RentalCommand.Api.Services.Esign;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;

namespace RentalCommand.Api.Tests.Domain;

public sealed class NativeEsignExecutionServiceTests
{
    [Fact]
    public void ExecutedUploadFingerprint_IsStableAcrossRetryState()
    {
        var publicId = Guid.Parse("d27710cd-c3af-4f6b-9b9f-1c0595fe2ab8");
        var firstAttempt = new SignatureRequest
        {
            PublicId = publicId,
            PortfolioId = 2,
            LeaseAgreementId = 80,
            Status = SignatureRequestStatus.ExecutionPending,
            ExecutionAttemptCount = 1,
            LastError = "first failure",
        };
        var retry = new SignatureRequest
        {
            PublicId = publicId,
            PortfolioId = 2,
            LeaseAgreementId = 80,
            Status = SignatureRequestStatus.ExecutionPending,
            ExecutionAttemptCount = 4,
            LastError = "different failure",
            ExecutionClaimToken = Guid.NewGuid(),
        };

        var firstFingerprint = NativeEsignExecutionService.BuildExecutedUploadRequestFingerprint(
            firstAttempt,
            "lease-agreement-80-executed.pdf");
        var retryFingerprint = NativeEsignExecutionService.BuildExecutedUploadRequestFingerprint(
            retry,
            "lease-agreement-80-executed.pdf");

        retryFingerprint.Should().Be(firstFingerprint);
    }

    [Fact]
    public void ExecutedUploadFingerprint_DistinguishesCanonicalTarget()
    {
        var publicId = Guid.Parse("d27710cd-c3af-4f6b-9b9f-1c0595fe2ab8");
        var agreement = new SignatureRequest
        {
            PublicId = publicId,
            PortfolioId = 2,
            LeaseAgreementId = 80,
        };
        var addendum = new SignatureRequest
        {
            PublicId = publicId,
            PortfolioId = 2,
            LeaseAddendumId = 80,
        };

        var agreementFingerprint = NativeEsignExecutionService.BuildExecutedUploadRequestFingerprint(
            agreement,
            "lease-agreement-80-executed.pdf");
        var addendumFingerprint = NativeEsignExecutionService.BuildExecutedUploadRequestFingerprint(
            addendum,
            "lease-addendum-80-executed.pdf");

        addendumFingerprint.Should().NotBe(agreementFingerprint);
    }
}
