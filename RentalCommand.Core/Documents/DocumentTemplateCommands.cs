using RentalCommand.Core.Atomic;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Operations;

namespace RentalCommand.Core.Documents;

public sealed record CreateDocumentTemplateCommand(
    int PortfolioId,
    StaffOperationActor Actor,
    [property: AtomicFingerprintIgnore] DateTime BusinessNowUtc,
    DocumentTemplateKind Kind,
    DocumentTemplateRenderMode RenderMode,
    string Name,
    string? Description,
    int? OriginalStoredFileId,
    int? CompiledStoredFileId,
    int? PropertyId,
    bool DefaultForPortfolio,
    string? DraftHtml,
    [property: AtomicFingerprintIgnore] string DeliveryIdempotencyKey) : IAtomicCommandData;

public sealed record FinalizeDocumentTemplateUploadCommand(
    int PortfolioId,
    StaffOperationActor Actor,
    [property: AtomicFingerprintIgnore] DateTime BusinessNowUtc,
    Guid PendingUploadId,
    string Purpose,
    string OperationKeyHash,
    string RequestFingerprint,
    string StoragePath,
    string FileName,
    string ContentType,
    long SizeBytes,
    string Sha256,
    string Name,
    string? Description,
    bool DefaultForPortfolio,
    int? PropertyId,
    [property: AtomicFingerprintIgnore] string DeliveryIdempotencyKey) : IAtomicCommandData;

public sealed record UpdateDocumentTemplateCommand(
    int PortfolioId,
    StaffOperationActor Actor,
    [property: AtomicFingerprintIgnore] DateTime BusinessNowUtc,
    int DocumentTemplateId,
    DocumentTemplateStatus? Status,
    DocumentTemplateRenderMode? RenderMode,
    string? Name,
    string? Description,
    int? OriginalStoredFileId,
    int? CompiledStoredFileId,
    int? PropertyId,
    bool? DefaultForPortfolio,
    string? DraftHtml,
    [property: AtomicFingerprintIgnore] string DeliveryIdempotencyKey) : IAtomicCommandData;

public sealed record AddDocumentTemplateFieldCommand(
    int PortfolioId,
    StaffOperationActor Actor,
    [property: AtomicFingerprintIgnore] DateTime BusinessNowUtc,
    int DocumentTemplateId,
    string FieldKey,
    string Label,
    DocumentTemplateFieldKind Kind,
    DocumentTemplateSignerRole SignerRole,
    int PageNumber,
    double XPct,
    double YPct,
    double WidthPct,
    double HeightPct,
    bool Required,
    bool Locked,
    int SortOrder,
    string? DefaultText,
    [property: AtomicFingerprintIgnore] string DeliveryIdempotencyKey) : IAtomicCommandData;

public sealed record UpdateDocumentTemplateFieldCommand(
    int PortfolioId,
    StaffOperationActor Actor,
    [property: AtomicFingerprintIgnore] DateTime BusinessNowUtc,
    int DocumentTemplateId,
    int FieldId,
    string? FieldKey,
    string? Label,
    DocumentTemplateFieldKind? Kind,
    DocumentTemplateSignerRole? SignerRole,
    int? PageNumber,
    double? XPct,
    double? YPct,
    double? WidthPct,
    double? HeightPct,
    bool? Required,
    bool? Locked,
    int? SortOrder,
    string? DefaultText,
    [property: AtomicFingerprintIgnore] string DeliveryIdempotencyKey) : IAtomicCommandData;

public sealed record DeleteDocumentTemplateFieldCommand(
    int PortfolioId,
    StaffOperationActor Actor,
    [property: AtomicFingerprintIgnore] DateTime BusinessNowUtc,
    int DocumentTemplateId,
    int FieldId,
    [property: AtomicFingerprintIgnore] string DeliveryIdempotencyKey) : IAtomicCommandData;

public enum DocumentTemplateMutationOutcome
{
    Applied = 1,
    NotFound = 2,
    Invalid = 3,
}

public sealed record DocumentTemplateFieldSnapshot(
    int Id,
    int DocumentTemplateId,
    string FieldKey,
    string Label,
    DocumentTemplateFieldKind Kind,
    DocumentTemplateSignerRole SignerRole,
    int PageNumber,
    double XPct,
    double YPct,
    double WidthPct,
    double HeightPct,
    bool Required,
    bool Locked,
    int SortOrder,
    string? DefaultText);

public sealed record DocumentTemplateSnapshot(
    int Id,
    int PortfolioId,
    DocumentTemplateKind Kind,
    DocumentTemplateStatus Status,
    DocumentTemplateRenderMode RenderMode,
    string Name,
    string? Description,
    int? OriginalStoredFileId,
    int? CompiledStoredFileId,
    bool HasDraftHtml,
    bool DefaultForPortfolio,
    int? PropertyId,
    int Version,
    int FieldCount,
    DateTime CreatedAtUtc,
    DateTime UpdatedAtUtc,
    DateTime? ArchivedAtUtc,
    IReadOnlyList<DocumentTemplateFieldSnapshot> Fields);

public sealed record DocumentTemplateMutationResult(
    DocumentTemplateMutationOutcome Outcome,
    int DocumentTemplateId,
    int? FieldId = null,
    DocumentTemplateSnapshot? Template = null,
    DocumentTemplateFieldSnapshot? Field = null,
    string? Error = null);
