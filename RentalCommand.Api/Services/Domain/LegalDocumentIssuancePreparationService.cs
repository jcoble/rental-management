using System.Security.Cryptography;
using RentalCommand.Core;
using RentalCommand.Core.Authorization;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Interfaces;
using RentalCommand.Core.Leasing;
using RentalCommand.Data.Documents;

namespace RentalCommand.Api.Services.Domain;

public sealed record LegalDocumentIssuancePreparation(
    Guid PendingUploadId,
    int DraftRevision,
    int DocumentSourceVersionId,
    string IssuanceFingerprint,
    string StorageKey,
    string FileName,
    long FileSize,
    string ContentSha256);

public interface ILegalDocumentIssuancePreparationService
{
    Task<LegalDocumentIssuancePreparation> PrepareAgreementAsync(
        WorkspaceReadScope scope,
        int leaseManagementId,
        int leaseAgreementId,
        int expectedDraftRevision,
        string operationKey,
        CancellationToken ct = default);

    Task<LegalDocumentIssuancePreparation> PrepareAddendumAsync(
        WorkspaceReadScope scope,
        int leaseManagementId,
        int leaseAddendumId,
        int expectedDraftRevision,
        string operationKey,
        CancellationToken ct = default);
}

/// <summary>
/// Owns legal PDF preparation. Clients never choose bytes, hashes, source ids, fingerprints, file
/// names, sizes, or storage keys; they receive one tuple that the later atomic issue command rechecks.
/// </summary>
public sealed class LegalDocumentIssuancePreparationService : ILegalDocumentIssuancePreparationService
{
    private readonly ILegalDocumentIssuanceDraftReader _drafts;
    private readonly ILeaseAgreementRenderer _renderer;
    private readonly ILeaseAgreementPdfGenerator _agreementPdf;
    private readonly ILeaseAddendumPdfGenerator _addendumPdf;
    private readonly IPendingFileUploadStore _pendingUploads;
    private readonly IFileStorage _storage;
    private readonly TimeProvider _timeProvider;

    public LegalDocumentIssuancePreparationService(
        ILegalDocumentIssuanceDraftReader drafts,
        ILeaseAgreementRenderer renderer,
        ILeaseAgreementPdfGenerator agreementPdf,
        ILeaseAddendumPdfGenerator addendumPdf,
        IPendingFileUploadStore pendingUploads,
        IFileStorage storage,
        TimeProvider timeProvider)
    {
        _drafts = drafts;
        _renderer = renderer;
        _agreementPdf = agreementPdf;
        _addendumPdf = addendumPdf;
        _pendingUploads = pendingUploads;
        _storage = storage;
        _timeProvider = timeProvider;
    }

    public async Task<LegalDocumentIssuancePreparation> PrepareAgreementAsync(
        WorkspaceReadScope scope,
        int leaseManagementId,
        int leaseAgreementId,
        int expectedDraftRevision,
        string operationKey,
        CancellationToken ct = default)
    {
        ValidateRequest(scope, leaseManagementId, leaseAgreementId, expectedDraftRevision, operationKey);
        var draft = await _drafts.ReadAgreementAsync(
                scope, leaseManagementId, leaseAgreementId, expectedDraftRevision,
                _timeProvider.GetUtcNow().UtcDateTime, ct)
            ?? throw new UnauthorizedAccessException(
                "Agreement issuance preparation is outside the caller's current access scope.");
        EnsureIssuable(draft, nameof(LeaseAgreement));
        var renderData = ToAgreementRenderData(draft);
        var rendered = await _renderer.RenderExactAsync(
            scope.PortfolioId,
            draft.DocumentSourceVersionId,
            renderData,
            () => _agreementPdf.Generate(renderData),
            ct);
        EnsureExactSource(draft, rendered);
        return await AdmitAsync(
            scope,
            draft,
            rendered.PdfBytes,
            $"agreement-{draft.LegalDocumentId}-r{draft.DraftRevision}.pdf",
            LegalDocumentIssuanceBinding.AgreementUploadPurpose,
            $"agreement:{draft.LegalDocumentId}:r{draft.DraftRevision}:{operationKey}",
            ct);
    }

    public async Task<LegalDocumentIssuancePreparation> PrepareAddendumAsync(
        WorkspaceReadScope scope,
        int leaseManagementId,
        int leaseAddendumId,
        int expectedDraftRevision,
        string operationKey,
        CancellationToken ct = default)
    {
        ValidateRequest(scope, leaseManagementId, leaseAddendumId, expectedDraftRevision, operationKey);
        var draft = await _drafts.ReadAddendumAsync(
                scope, leaseManagementId, leaseAddendumId, expectedDraftRevision,
                _timeProvider.GetUtcNow().UtcDateTime, ct)
            ?? throw new UnauthorizedAccessException(
                "Addendum issuance preparation is outside the caller's current access scope.");
        EnsureIssuable(draft, nameof(LeaseAddendum));
        var overlayData = ToAgreementRenderData(draft);
        var addendumData = new LeaseAddendumRenderData
        {
            AddendumNumber = draft.DocumentNumber,
            EffectiveFromOn = draft.EffectiveFromOn,
            EffectiveThroughOn = draft.EffectiveThroughOn,
            LandlordName = draft.LandlordName,
            TenantName = draft.TenantName,
            PropertyAddress = overlayData.PropertyAddress,
            TermsPayload = draft.TermsPayload,
            FinancialEffects = draft.FinancialEffects,
        };
        var rendered = await _renderer.RenderExactAsync(
            scope.PortfolioId,
            draft.DocumentSourceVersionId,
            overlayData,
            () => _addendumPdf.Generate(addendumData),
            ct);
        EnsureExactSource(draft, rendered);
        return await AdmitAsync(
            scope,
            draft,
            rendered.PdfBytes,
            $"addendum-{draft.LegalDocumentId}-r{draft.DraftRevision}.pdf",
            LegalDocumentIssuanceBinding.AddendumUploadPurpose,
            $"addendum:{draft.LegalDocumentId}:r{draft.DraftRevision}:{operationKey}",
            ct);
    }

    private async Task<LegalDocumentIssuancePreparation> AdmitAsync(
        WorkspaceReadScope scope,
        LegalDocumentIssuanceDraftSnapshot draft,
        byte[] pdfBytes,
        string fileName,
        string purpose,
        string operationKey,
        CancellationToken ct)
    {
        if (pdfBytes.Length == 0)
        {
            throw new InvalidOperationException("The legal-document renderer returned no PDF bytes.");
        }

        var contentSha256 = Convert.ToHexString(SHA256.HashData(pdfBytes)).ToLowerInvariant();
        var fingerprint = draft.LegalKind == nameof(LeaseAddendum)
            ? LegalDocumentIssuanceBinding.CreateAddendum(
                draft.PortfolioId,
                draft.LeaseManagementId,
                draft.LegalDocumentId,
                draft.DraftRevision,
                draft.DocumentSourceVersionId,
                draft.TermsSchemaVersion,
                draft.TermsPayload,
                draft.FinancialEffects,
                contentSha256,
                pdfBytes.LongLength,
                fileName)
            : LegalDocumentIssuanceBinding.Create(
                draft.LegalKind,
                draft.PortfolioId,
                draft.LeaseManagementId,
                draft.LegalDocumentId,
                draft.DraftRevision,
                draft.DocumentSourceVersionId,
                draft.TermsSchemaVersion,
                draft.TermsPayload,
                contentSha256,
                pdfBytes.LongLength,
                fileName);
        var admission = await _pendingUploads.PrepareAsync(
            scope.PortfolioId,
            scope.UserId,
            purpose,
            operationKey,
            fingerprint,
            fileName,
            "application/pdf",
            pdfBytes.LongLength,
            _timeProvider.GetUtcNow().UtcDateTime,
            ct);
        if (admission.State == PendingFileUploadState.Abandoned)
        {
            throw new DomainValidationException("The legal-document upload admission was abandoned.");
        }
        if (admission.State == PendingFileUploadState.Prepared)
        {
            await using var stream = new MemoryStream(pdfBytes, writable: false);
            await _storage.UploadAtAsync(stream, admission.StoragePath, fileName, "application/pdf", ct);
        }

        return new LegalDocumentIssuancePreparation(
            admission.Id,
            draft.DraftRevision,
            draft.DocumentSourceVersionId,
            fingerprint,
            admission.StoragePath,
            fileName,
            pdfBytes.LongLength,
            contentSha256);
    }

    private static LeaseAgreementRenderData ToAgreementRenderData(
        LegalDocumentIssuanceDraftSnapshot draft)
    {
        var locality = $"{draft.City}, {draft.State} {draft.PostalCode}".Trim();
        var address = string.Join(", ", new[] { draft.AddressLine1, draft.AddressLine2, locality }
            .Where(part => !string.IsNullOrWhiteSpace(part)));
        return new LeaseAgreementRenderData
        {
            PropertyId = draft.PropertyId,
            AgreementNumber = draft.DocumentNumber,
            TermStartOn = draft.EffectiveFromOn,
            TermEndOn = draft.EffectiveThroughOn,
            BaseRentAmount = draft.BaseRentAmount,
            SecurityDepositObligation = draft.SecurityDepositObligation,
            LateFeeAmount = draft.LateFeeAmount,
            RentDueDay = draft.RentDueDay,
            LandlordName = draft.LandlordName,
            TenantName = draft.TenantName,
            TenantEmail = draft.TenantEmail,
            PropertyName = draft.PropertyName,
            PropertyAddress = address,
            UnitNumber = draft.UnitNumber,
            State = draft.State,
            YearBuilt = draft.YearBuilt,
        };
    }

    private static void EnsureIssuable(LegalDocumentIssuanceDraftSnapshot draft, string expectedKind)
    {
        if (!string.Equals(draft.LegalKind, expectedKind, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The legal-document reader returned the wrong draft kind.");
        }
        if (!draft.MatchesExpectedDraftRevision)
        {
            throw new DomainValidationException(
                $"DraftRevision is stale; reload the {expectedKind} draft before preparing issuance.");
        }
        if (!draft.IsOpenDraft)
        {
            throw new DomainValidationException(
                $"Only an open, unissued {expectedKind} draft can be prepared for issuance.");
        }
    }

    private static void EnsureExactSource(
        LegalDocumentIssuanceDraftSnapshot draft,
        LeaseAgreementRenderResult rendered)
    {
        if (rendered.DocumentSourceVersionId != draft.DocumentSourceVersionId)
        {
            throw new InvalidOperationException(
                "The legal-document renderer returned bytes from a different immutable source version.");
        }
    }

    private static void ValidateRequest(
        WorkspaceReadScope scope,
        int leaseManagementId,
        int legalDocumentId,
        int expectedDraftRevision,
        string operationKey)
    {
        if (scope.PortfolioId <= 0 || scope.UserId <= 0 || scope.SessionId == Guid.Empty
            || scope.AccessContextId <= 0 || scope.AccessRevision <= 0
            || leaseManagementId <= 0 || legalDocumentId <= 0 || expectedDraftRevision <= 0
            || string.IsNullOrWhiteSpace(operationKey))
        {
            throw new ArgumentException("The legal-document issuance preparation request is incomplete.");
        }
    }
}
