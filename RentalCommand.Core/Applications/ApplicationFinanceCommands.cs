using RentalCommand.Core.Atomic;
using RentalCommand.Core.Enums;

namespace RentalCommand.Core.Applications;

public sealed record RecordApplicationFeeCommand(
    int PortfolioId,
    int ApplicationId,
    decimal Amount,
    string Currency,
    DateOnly? EffectiveOn,
    string? Method,
    string? Provider,
    string? ProviderReference,
    ApplicationFinancialEntrySource Source,
    string? SourceReference,
    string IdempotencyKey,
    int ActorUserId,
    [property: AtomicFingerprintIgnore] Guid AuthSessionId,
    [property: AtomicFingerprintIgnore] int AccessContextId,
    [property: AtomicFingerprintIgnore] long ExpectedAccessRevision) : IAtomicCommandData;

public sealed record RefundApplicationFeeCommand(
    int PortfolioId,
    int ApplicationId,
    int CollectionEntryId,
    decimal Amount,
    DateOnly? EffectiveOn,
    string? Method,
    string? Provider,
    string? ProviderReference,
    ApplicationFinancialEntrySource Source,
    string? SourceReference,
    string Reason,
    string IdempotencyKey,
    int ActorUserId,
    [property: AtomicFingerprintIgnore] Guid AuthSessionId,
    [property: AtomicFingerprintIgnore] int AccessContextId,
    [property: AtomicFingerprintIgnore] long ExpectedAccessRevision) : IAtomicCommandData;

public enum ApplicationFinanceMutationOutcome
{
    Posted,
    ApplicationNotFound,
    CollectionNotFound,
    CurrencyMismatch,
    RefundExceedsCollectedAmount,
}

public sealed record ApplicationFinanceMutationResult(
    ApplicationFinanceMutationOutcome Outcome,
    int ApplicationId,
    int? AccountId,
    int? EntryId,
    int? RelatedEntryId,
    ApplicationFinancialEntryType? EntryType,
    ApplicationFinancialDirection? Direction,
    decimal? Amount,
    string? Currency,
    DateOnly? EffectiveOn,
    DateTime? OccurredAtUtc,
    bool AccountCreated,
    string? Error);
