using System.Security.Cryptography;
using FluentAssertions;
using Moq;
using RentalCommand.Api.Services.Domain;
using RentalCommand.Core;
using RentalCommand.Core.Authorization;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Interfaces;
using RentalCommand.Core.Leasing;
using RentalCommand.Data.Documents;

namespace RentalCommand.Api.Tests.Domain;

public sealed class LegalDocumentIssuancePreparationServiceTests
{
    private static readonly WorkspaceReadScope Scope = new(3, 7, Guid.NewGuid(), 11, 4);
    private static readonly byte[] PdfBytes = "%PDF-server-owned"u8.ToArray();

    [Fact]
    public async Task Agreement_preparation_hashes_server_bytes_and_returns_the_admitted_tuple()
    {
        var draft = Draft(nameof(LeaseAgreement));
        var (service, pending, storage, renderer) = Service(draft);

        var prepared = await service.PrepareAgreementAsync(
            Scope, draft.LeaseManagementId, draft.LegalDocumentId, draft.DraftRevision, "operation-a");

        var expectedHash = Convert.ToHexString(SHA256.HashData(PdfBytes)).ToLowerInvariant();
        var expectedFingerprint = LegalDocumentIssuanceBinding.Create(
            draft.LegalKind, draft.PortfolioId, draft.LeaseManagementId, draft.LegalDocumentId,
            draft.DraftRevision, draft.DocumentSourceVersionId, draft.TermsSchemaVersion,
            draft.TermsPayload, expectedHash, PdfBytes.LongLength,
            $"agreement-{draft.LegalDocumentId}-r{draft.DraftRevision}.pdf");
        prepared.ContentSha256.Should().Be(expectedHash);
        prepared.IssuanceFingerprint.Should().Be(expectedFingerprint);
        prepared.DocumentSourceVersionId.Should().Be(draft.DocumentSourceVersionId);
        prepared.StorageKey.Should().Be("pending/legal.pdf");
        pending.Verify(store => store.PrepareAsync(
            Scope.PortfolioId, Scope.UserId, LegalDocumentIssuanceBinding.AgreementUploadPurpose,
            $"agreement:{draft.LegalDocumentId}:r{draft.DraftRevision}:operation-a",
            expectedFingerprint, prepared.FileName, "application/pdf", PdfBytes.LongLength,
            It.IsAny<DateTime>(), It.IsAny<CancellationToken>()), Times.Once);
        storage.Verify(files => files.UploadAtAsync(
            It.IsAny<Stream>(), prepared.StorageKey, prepared.FileName, "application/pdf",
            It.IsAny<CancellationToken>()), Times.Once);
        renderer.Verify(value => value.RenderExactAsync(
            Scope.PortfolioId, draft.DocumentSourceVersionId, It.IsAny<LeaseAgreementRenderData>(),
            It.IsAny<Func<byte[]>>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Addendum_preparation_uses_the_separate_admission_purpose_and_server_renderer()
    {
        var draft = Draft(nameof(LeaseAddendum));
        var (service, pending, storage, _) = Service(draft);

        var prepared = await service.PrepareAddendumAsync(
            Scope, draft.LeaseManagementId, draft.LegalDocumentId, draft.DraftRevision, "operation-b");

        prepared.FileName.Should().Be($"addendum-{draft.LegalDocumentId}-r{draft.DraftRevision}.pdf");
        pending.Verify(store => store.PrepareAsync(
            Scope.PortfolioId, Scope.UserId, LegalDocumentIssuanceBinding.AddendumUploadPurpose,
            $"addendum:{draft.LegalDocumentId}:r{draft.DraftRevision}:operation-b",
            prepared.IssuanceFingerprint, prepared.FileName, "application/pdf", PdfBytes.LongLength,
            It.IsAny<DateTime>(), It.IsAny<CancellationToken>()), Times.Once);
        storage.Verify(files => files.UploadAtAsync(
            It.IsAny<Stream>(), prepared.StorageKey, prepared.FileName, "application/pdf",
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Stale_draft_is_rejected_before_render_or_upload_admission()
    {
        var draft = Draft(nameof(LeaseAgreement)) with { MatchesExpectedDraftRevision = false };
        var (service, pending, storage, renderer) = Service(draft);

        var action = () => service.PrepareAgreementAsync(
            Scope, draft.LeaseManagementId, draft.LegalDocumentId, draft.DraftRevision + 1, "operation-c");

        await action.Should().ThrowAsync<DomainValidationException>()
            .WithMessage("*DraftRevision is stale*");
        renderer.Verify(value => value.RenderExactAsync(
            It.IsAny<int>(), It.IsAny<int>(), It.IsAny<LeaseAgreementRenderData>(),
            It.IsAny<Func<byte[]>>(), It.IsAny<CancellationToken>()), Times.Never);
        pending.VerifyNoOtherCalls();
        storage.VerifyNoOtherCalls();
    }

    private static (LegalDocumentIssuancePreparationService Service,
        Mock<IPendingFileUploadStore> Pending,
        Mock<IFileStorage> Storage,
        Mock<ILeaseAgreementRenderer> Renderer) Service(
        LegalDocumentIssuanceDraftSnapshot draft)
    {
        var drafts = new Mock<ILegalDocumentIssuanceDraftReader>(MockBehavior.Strict);
        drafts.Setup(reader => reader.ReadAgreementAsync(
                Scope, draft.LeaseManagementId, draft.LegalDocumentId, It.IsAny<int>(),
                It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(draft);
        drafts.Setup(reader => reader.ReadAddendumAsync(
                Scope, draft.LeaseManagementId, draft.LegalDocumentId, It.IsAny<int>(),
                It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(draft);
        var renderer = new Mock<ILeaseAgreementRenderer>(MockBehavior.Strict);
        renderer.Setup(value => value.RenderExactAsync(
                Scope.PortfolioId, draft.DocumentSourceVersionId, It.IsAny<LeaseAgreementRenderData>(),
                It.IsAny<Func<byte[]>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new LeaseAgreementRenderResult(PdfBytes, draft.DocumentSourceVersionId, null));
        var pending = new Mock<IPendingFileUploadStore>(MockBehavior.Strict);
        pending.Setup(store => store.PrepareAsync(
                Scope.PortfolioId, Scope.UserId, It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<string>(), "application/pdf", PdfBytes.LongLength,
                It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PendingFileUploadAdmission(
                Guid.Parse("11111111-1111-1111-1111-111111111111"),
                "pending/legal.pdf",
                PendingFileUploadState.Prepared,
                null,
                new string('a', 64)));
        var storage = new Mock<IFileStorage>(MockBehavior.Strict);
        storage.Setup(files => files.UploadAtAsync(
                It.IsAny<Stream>(), "pending/legal.pdf", It.IsAny<string>(), "application/pdf",
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        return (new LegalDocumentIssuancePreparationService(
            drafts.Object,
            renderer.Object,
            Mock.Of<ILeaseAgreementPdfGenerator>(),
            Mock.Of<ILeaseAddendumPdfGenerator>(),
            pending.Object,
            storage.Object,
            TimeProvider.System), pending, storage, renderer);
    }

    private static LegalDocumentIssuanceDraftSnapshot Draft(string kind) => new(
        kind,
        Scope.PortfolioId,
        LeaseManagementId: 17,
        LegalDocumentId: 29,
        DraftRevision: 4,
        MatchesExpectedDraftRevision: true,
        IsOpenDraft: true,
        DocumentSourceVersionId: 41,
        TermsSchemaVersion: 2,
        TermsPayload: "{\"rent\":1450}",
        PropertyId: 5,
        DocumentNumber: "LEGAL-29",
        EffectiveFromOn: new DateOnly(2026, 8, 1),
        EffectiveThroughOn: new DateOnly(2027, 7, 31),
        BaseRentAmount: 1450m,
        SecurityDepositObligation: 1450m,
        LateFeeAmount: 50m,
        RentDueDay: 1,
        LandlordName: "Example Management",
        TenantName: "Ada Tenant",
        TenantEmail: "ada@example.test",
        PropertyName: "Example House",
        AddressLine1: "1 Main Street",
        AddressLine2: null,
        City: "Columbus",
        State: "OH",
        PostalCode: "43215",
        UnitNumber: "A",
        YearBuilt: 2001);
}
