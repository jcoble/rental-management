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
    int UserId,
    string Reason,
    string CreditReportingAgency,
    string FileName,
    string StorageKey,
    long FileSize,
    bool SendToApplicant,
    string DeliveryIdempotencyKey,
    DateTime GeneratedAtUtc) : IAtomicCommandData;

/// <summary>Receipt-safe result for a generated adverse-action package.</summary>
public sealed record CreateAdverseActionNoticeResult(
    int NoticeId,
    int ApplicationId,
    string Reason,
    string CreditReportingAgency,
    DateTime GeneratedAtUtc,
    int StoredFileId,
    DateTime? SentAtUtc) : IAtomicResultData;
