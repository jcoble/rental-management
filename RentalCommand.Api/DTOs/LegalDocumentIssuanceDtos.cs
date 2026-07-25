using RentalCommand.Api.Services.Domain;

namespace RentalCommand.Api.DTOs;

public sealed class PrepareLegalDocumentIssuanceRequest
{
    public int DraftRevision { get; set; }
}

public sealed record LegalDocumentIssuancePreparationResponse(
    Guid PendingUploadId,
    int DraftRevision,
    int DocumentSourceVersionId,
    string IssuanceFingerprint,
    string StorageKey,
    string FileName,
    long FileSize,
    string ContentSha256)
{
    public static LegalDocumentIssuancePreparationResponse From(
        LegalDocumentIssuancePreparation preparation) => new(
        preparation.PendingUploadId,
        preparation.DraftRevision,
        preparation.DocumentSourceVersionId,
        preparation.IssuanceFingerprint,
        preparation.StorageKey,
        preparation.FileName,
        preparation.FileSize,
        preparation.ContentSha256);
}
