using RentalCommand.Core.Atomic;
using RentalCommand.Core.Enums;

namespace RentalCommand.Core.Scanning;

public enum ScanDraftMutationOutcome
{
    Applied,
    NotFound,
    InvalidStatus,
    Stale,
}

public sealed record RetryScanDraftCommand(
    int PortfolioId,
    int DraftId,
    int UserId,
    Guid AuthSessionId,
    int AccessContextId,
    long ExpectedAccessRevision,
    string DeliveryIdempotencyKey) : IAtomicCommandData;

public sealed record CreateVoiceScanDraftCommand(
    int PortfolioId,
    int UserId,
    Guid AuthSessionId,
    int AccessContextId,
    long ExpectedAccessRevision,
    string TargetEntityType,
    string ExtractedFieldsJson,
    string ModelId,
    int? TokensUsed,
    int? CapturePropertyId,
    string FilePath,
    string? SourceFileName,
    string? SourceContentType,
    long SourceFileSize,
    string? SourceContentSha256,
    string DeliveryIdempotencyKey) : IAtomicCommandData;

public sealed record AnswerVoiceScanDraftCommand(
    int PortfolioId,
    int DraftId,
    int UserId,
    Guid AuthSessionId,
    int AccessContextId,
    long ExpectedAccessRevision,
    string ExpectedExtractedFieldsJson,
    string TargetEntityType,
    string MergedExtractedFieldsJson,
    string ModelId,
    int? TokensUsed,
    int? CapturePropertyId,
    string DeliveryIdempotencyKey) : IAtomicCommandData;

public sealed record SetScanDraftPaymentAccountCommand(
    int PortfolioId,
    int DraftId,
    int UserId,
    Guid AuthSessionId,
    int AccessContextId,
    long ExpectedAccessRevision,
    int TenantAccountId,
    string DeliveryIdempotencyKey) : IAtomicCommandData;

public sealed record ScanDraftReceiptSnapshot(
    int Id,
    int PortfolioId,
    string FilePath,
    int? SourceStoredFileId,
    string? SourceContentSha256,
    string? SourceLabel,
    WorkspaceExperience? CaptureExperience,
    int? CaptureAccessContextId,
    long? CaptureAccessRevision,
    int? CapturePropertyId,
    int? CaptureUnitId,
    int? CaptureLeaseManagementId,
    int? CaptureLeaseAgreementId,
    int? CaptureTenantAccountId,
    long? CaptureTenantLedgerEntryId,
    int? CaptureWorkOrderId,
    int? CaptureApplicationId,
    int? CaptureRentalListingId,
    string TargetEntityType,
    string Status,
    string? ExtractedFields,
    string? ModelId,
    int? TokensUsed,
    decimal? CostUsd,
    string? FailureReason,
    DateTime CreatedAt,
    DateTime? ReviewedAt,
    string? ReviewedBy,
    DateTime? ConfirmedAt,
    int? ConfirmedEntityId) : IAtomicResultData;

public sealed record ScanDraftMutationResult(
    ScanDraftMutationOutcome Outcome,
    int DraftId,
    ScanDraftReceiptSnapshot? Snapshot = null) : IAtomicResultData;
