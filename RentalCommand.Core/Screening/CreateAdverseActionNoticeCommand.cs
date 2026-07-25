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
    int ActorUserId,
    Guid AuthSessionId,
    int AccessContextId,
    long ExpectedAccessRevision,
    string Reason,
    string CreditReportingAgency,
    string FileName,
    string StorageKey,
    long FileSize,
    bool SendToApplicant,
    string DeliveryIdempotencyKey,
    DateTime GeneratedAtUtc) : IAtomicCommandData;

/// <summary>
/// Receipt-backed preflight that freezes the authorized legal/PDF inputs and deterministic storage
/// key before PDF generation or blob storage crosses an external boundary.
/// </summary>
public sealed record PrepareAdverseActionNoticeCommand(
    int PortfolioId,
    int ApplicationId,
    int ActorUserId,
    Guid AuthSessionId,
    int AccessContextId,
    long ExpectedAccessRevision,
    string OperationKey,
    string? Reason,
    bool SendToApplicant) : IAtomicCommandData;

public sealed record PrepareAdverseActionNoticeResult(
    ScreeningMutationOutcome Outcome,
    int ApplicationId,
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
    string? StorageKey,
    bool SendToApplicant,
    DateTime GeneratedAtUtc) : IAtomicResultData;

/// <summary>Receipt-safe result for a generated adverse-action package.</summary>
public sealed record CreateAdverseActionNoticeResult(
    int NoticeId,
    int ApplicationId,
    string Reason,
    string CreditReportingAgency,
    DateTime GeneratedAtUtc,
    int StoredFileId,
    DateTime? SentAtUtc) : IAtomicResultData;
