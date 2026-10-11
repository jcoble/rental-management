using RentalCommand.Core.Atomic;

namespace RentalCommand.Core.Screening;

/// <summary>
/// Persists one generated adverse-action artifact, its legal notice, semantic audit, and optional
/// applicant delivery intent as one receipt-backed database command. PDF generation and upload are
/// deliberately completed before this command begins.
/// </summary>
public sealed record CreateAdverseActionNoticeCommand(
    int PortfolioId,
    int ApplicationId,
    int ScreeningId,
    DateTime DecisionRecordedAtUtc,
    string DecisionFingerprint,
    int ActorUserId,
    [property: AtomicFingerprintIgnore] Guid AuthSessionId,
    [property: AtomicFingerprintIgnore] int AccessContextId,
    [property: AtomicFingerprintIgnore] long ExpectedAccessRevision,
    string Reason,
    string CreditReportingAgency,
    Guid PendingUploadId,
    string Purpose,
    string OperationKeyHash,
    string RequestFingerprint,
    string StoragePath,
    string FileName,
    string ContentType,
    long FileSize,
    bool SendToApplicant,
    [property: AtomicFingerprintIgnore] string DeliveryIdempotencyKey,
    [property: AtomicFingerprintIgnore] DateTime GeneratedAtUtc) : IAtomicCommandData;

/// <summary>
/// Receipt-backed preflight that freezes the authorized legal/PDF inputs and exact screening
/// decision identity before PDF generation or blob storage crosses an external boundary.
/// </summary>
public sealed record PrepareAdverseActionNoticeCommand(
    int PortfolioId,
    int ApplicationId,
    int ActorUserId,
    [property: AtomicFingerprintIgnore] Guid AuthSessionId,
    [property: AtomicFingerprintIgnore] int AccessContextId,
    [property: AtomicFingerprintIgnore] long ExpectedAccessRevision,
    string OperationKey,
    string? Reason,
    bool SendToApplicant) : IAtomicCommandData;

public sealed record PrepareAdverseActionNoticeResult(
    ScreeningMutationOutcome Outcome,
    int ApplicationId,
    int ScreeningId,
    string? ManagementCompanyName,
    string? PortfolioName,
    string? ApplicantName,
    string? PropertyName,
    string? PropertyAddressLine1,
    string? PropertyCity,
    string? PropertyState,
    string? PropertyPostalCode,
    string? Reason,
    string? CreditReportingAgencyName,
    string? CreditReportingAgencyAddress,
    string? CreditReportingAgencyPhone,
    string? CreditReportingAgencyBlock,
    string? FileName,
    DateTime? DecisionRecordedAtUtc,
    string? DecisionFingerprint,
    bool SendToApplicant,
    DateTime GeneratedAtUtc);

/// <summary>Receipt-safe result for a generated adverse-action package.</summary>
public sealed record CreateAdverseActionNoticeResult(
    int NoticeId,
    int ApplicationId,
    string Reason,
    string CreditReportingAgency,
    DateTime GeneratedAtUtc,
    int StoredFileId,
    DateTime? SentAtUtc);
