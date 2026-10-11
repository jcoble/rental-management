using RentalCommand.Core.Atomic;
using RentalCommand.Core.Enums;

namespace RentalCommand.Core.Accounting;

public sealed record ConfirmAccountingMappingCommand(
    int PortfolioId,
    int AccountingConnectionId,
    AccountingProvider Provider,
    int ConfirmedByUserId,
    [property: AtomicFingerprintIgnore] Guid AuthSessionId,
    [property: AtomicFingerprintIgnore] int AccessContextId,
    [property: AtomicFingerprintIgnore] long ExpectedAccessRevision,
    string RequiredCapability,
    string ExternalType,
    string ExternalId,
    string? ExternalDisplayName,
    string LocalEntityType,
    int? LocalEntityId,
    string? LocalEnumValue,
    string ClientOperationId,
    long ExpectedRevision,
    [property: AtomicFingerprintIgnore] DateTime ConfirmedAtUtc) : IAtomicCommandData;

public enum ConfirmAccountingMappingOutcome
{
    Applied,
    ConnectionNotFound,
    InvalidTarget,
    StaleRevision,
}

public sealed record ConfirmAccountingMappingResult(
    ConfirmAccountingMappingOutcome Outcome,
    int MappingId,
    long MappingRevision,
    int PromotedCount,
    Guid? ContinuationId,
    bool HasMore);

public sealed record ContinueAccountingMappingPromotionCommand(
    int PortfolioId,
    int AccountingConnectionId,
    Guid ContinuationId,
    int RequestedByUserId,
    [property: AtomicFingerprintIgnore] Guid AuthSessionId,
    [property: AtomicFingerprintIgnore] int AccessContextId,
    [property: AtomicFingerprintIgnore] long ExpectedAccessRevision,
    string RequiredCapability,
    string ClientOperationId,
    [property: AtomicFingerprintIgnore] DateTime AppliedAtUtc) : IAtomicCommandData;

public enum ContinueAccountingMappingPromotionOutcome
{
    Applied,
    Completed,
    NotFound,
    Superseded,
}

public sealed record ContinueAccountingMappingPromotionResult(
    ContinueAccountingMappingPromotionOutcome Outcome,
    Guid ContinuationId,
    int PromotedCount,
    int TotalPromotedCount,
    bool HasMore);
