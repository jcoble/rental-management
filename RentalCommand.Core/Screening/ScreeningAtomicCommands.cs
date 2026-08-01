using RentalCommand.Core.Atomic;
using RentalCommand.Core.Enums;

namespace RentalCommand.Core.Screening;

public enum ScreeningMutationOutcome
{
    Applied,
    NotFound,
}

/// <summary>Receipt-safe projection of provider-neutral screening workflow metadata.</summary>
public sealed record ApplicantScreeningSnapshot(
    int Id,
    int ApplicationId,
    ScreeningMode Mode,
    ApplicantScreeningStatus Status,
    string ProviderDisplayName,
    string? ProviderReference,
    string? ProviderHostedUrl,
    bool ConsentConfirmed,
    DateTime? InvitedAtUtc,
    DateTime? ApplicantSubmittedAtUtc,
    DateTime? CompletedAtUtc,
    DateTime? FailedAtUtc,
    DateTime LastStatusAtUtc,
    ScreeningDecision? Decision,
    string? DecisionReason,
    bool ConsumerReportUsedForDecision,
    string? CreditReportingAgencyName,
    string? CreditReportingAgencyAddress,
    string? CreditReportingAgencyPhone,
    bool HasCompleteCreditReportingAgencyContact,
    bool CanGenerateAdverseAction,
    string StatusSummary,
    string NextAction,
    bool IsTerminal,
    bool CanOpenProvider);

public sealed record ScreeningMutationResult(
    ScreeningMutationOutcome Outcome,
    ApplicantScreeningSnapshot? Screening = null);

public sealed record TrackExternalScreeningCommand(
    int PortfolioId,
    int ApplicationId,
    int ActorUserId,
    [property: AtomicFingerprintIgnore] Guid AuthSessionId,
    [property: AtomicFingerprintIgnore] int AccessContextId,
    [property: AtomicFingerprintIgnore] long ExpectedAccessRevision,
    string OperationKey,
    string ProviderDisplayName,
    string? ProviderReference,
    string? ProviderHostedUrl,
    string? CreditReportingAgencyName,
    string? CreditReportingAgencyAddress,
    string? CreditReportingAgencyPhone,
    ApplicantScreeningStatus Status,
    [property: AtomicFingerprintIgnore] string DeliveryIdempotencyKey) : IAtomicCommandData;

public sealed record UpdateExternalScreeningCommand(
    int PortfolioId,
    int ApplicationId,
    int ScreeningId,
    int ActorUserId,
    [property: AtomicFingerprintIgnore] Guid AuthSessionId,
    [property: AtomicFingerprintIgnore] int AccessContextId,
    [property: AtomicFingerprintIgnore] long ExpectedAccessRevision,
    string OperationKey,
    ApplicantScreeningStatus? Status,
    string? ProviderReference,
    string? ProviderHostedUrl,
    string? CreditReportingAgencyName,
    string? CreditReportingAgencyAddress,
    string? CreditReportingAgencyPhone,
    DateTime? OccurredAtUtc,
    [property: AtomicFingerprintIgnore] string DeliveryIdempotencyKey) : IAtomicCommandData;

public sealed record RecordScreeningDecisionCommand(
    int PortfolioId,
    int ApplicationId,
    int ScreeningId,
    int ActorUserId,
    [property: AtomicFingerprintIgnore] Guid AuthSessionId,
    [property: AtomicFingerprintIgnore] int AccessContextId,
    [property: AtomicFingerprintIgnore] long ExpectedAccessRevision,
    string OperationKey,
    ScreeningDecision Decision,
    string? Reason,
    bool ConsumerReportUsed,
    [property: AtomicFingerprintIgnore] string DeliveryIdempotencyKey) : IAtomicCommandData;

public sealed record PrepareIntegratedScreeningCommand(
    int PortfolioId,
    int ApplicationId,
    int ActorUserId,
    [property: AtomicFingerprintIgnore] Guid AuthSessionId,
    [property: AtomicFingerprintIgnore] int AccessContextId,
    [property: AtomicFingerprintIgnore] long ExpectedAccessRevision,
    string OperationKey,
    string ProviderKey,
    string ProviderDisplayName,
    [property: AtomicFingerprintIgnore] string DeliveryIdempotencyKey) : IAtomicCommandData;

public sealed record PrepareIntegratedScreeningResult(
    ScreeningMutationOutcome Outcome,
    ApplicantScreeningSnapshot? Screening,
    int ApplicationId,
    string OperationKey,
    string? ApplicantName,
    string? ApplicantEmail,
    DateTime? ConsentAtUtc);

public sealed record FinalizeIntegratedScreeningCommand(
    int PortfolioId,
    int ApplicationId,
    int ScreeningId,
    int ActorUserId,
    [property: AtomicFingerprintIgnore] Guid AuthSessionId,
    [property: AtomicFingerprintIgnore] int AccessContextId,
    [property: AtomicFingerprintIgnore] long ExpectedAccessRevision,
    string OperationKey,
    string ProviderKey,
    bool Accepted,
    string? ProviderReference,
    string? ProviderHostedUrl,
    DateTime? InvitedAtUtc,
    string? ErrorCode,
    string? CreditReportingAgencyName,
    string? CreditReportingAgencyAddress,
    string? CreditReportingAgencyPhone,
    [property: AtomicFingerprintIgnore] string DeliveryIdempotencyKey) : IAtomicCommandData;

public sealed record ApplyScreeningProviderDeliveryCommand(
    string ProviderKey,
    string DeliveryId,
    string ProviderReference,
    string EventType,
    ApplicantScreeningStatus Status,
    DateTime OccurredAtUtc,
    string? ProviderHostedUrl,
    string? CreditReportingAgencyName,
    string? CreditReportingAgencyAddress,
    string? CreditReportingAgencyPhone,
    [property: AtomicFingerprintIgnore] string DeliveryIdempotencyKey) : IAtomicCommandData;
