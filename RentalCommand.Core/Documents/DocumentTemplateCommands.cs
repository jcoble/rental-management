using RentalCommand.Core.Atomic;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Operations;

namespace RentalCommand.Core.Documents;

public sealed record CreateDocumentTemplateCommand(
    int PortfolioId,
    StaffOperationActor Actor,
    DocumentTemplateKind Kind,
    DocumentTemplateRenderMode RenderMode,
    string Name,
    string? Description,
    int? OriginalStoredFileId,
    int? CompiledStoredFileId,
    int? PropertyId,
    bool DefaultForPortfolio,
    string? DraftHtml,
    string DeliveryIdempotencyKey) : IAtomicCommandData;

public sealed record UpdateDocumentTemplateCommand(
    int PortfolioId,
    StaffOperationActor Actor,
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
    string DeliveryIdempotencyKey) : IAtomicCommandData;

public sealed record AddDocumentTemplateFieldCommand(
    int PortfolioId,
    StaffOperationActor Actor,
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
    string DeliveryIdempotencyKey) : IAtomicCommandData;

public sealed record UpdateDocumentTemplateFieldCommand(
    int PortfolioId,
    StaffOperationActor Actor,
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
    string DeliveryIdempotencyKey) : IAtomicCommandData;

public sealed record DeleteDocumentTemplateFieldCommand(
    int PortfolioId,
    StaffOperationActor Actor,
    int DocumentTemplateId,
    int FieldId,
    string DeliveryIdempotencyKey) : IAtomicCommandData;

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
    string? DefaultText) : IAtomicResultData;

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
    IReadOnlyList<DocumentTemplateFieldSnapshot> Fields) : IAtomicResultData;

public sealed record DocumentTemplateMutationResult(
    DocumentTemplateMutationOutcome Outcome,
    int DocumentTemplateId,
    int? FieldId = null,
    DocumentTemplateSnapshot? Template = null,
    DocumentTemplateFieldSnapshot? Field = null,
    string? Error = null) : IAtomicResultData;
