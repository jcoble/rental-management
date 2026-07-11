using RentalCommand.Core.Atomic;
using RentalCommand.Core.Enums;

namespace RentalCommand.Core.Accounting;

public sealed record ConfirmAccountingMappingCommand(
    int PortfolioId,
    int AccountingConnectionId,
    AccountingProvider Provider,
    int ConfirmedByUserId,
    string ExternalType,
    string ExternalId,
    string? ExternalDisplayName,
    string LocalEntityType,
    int? LocalEntityId,
    string? LocalEnumValue,
    string ClientOperationId,
    long ExpectedRevision,
    DateTime ConfirmedAtUtc) : IAtomicCommandData;

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
    bool HasMore) : IAtomicResultData;

public sealed record ContinueAccountingMappingPromotionCommand(
    int PortfolioId,
    int AccountingConnectionId,
    Guid ContinuationId,
    int RequestedByUserId,
    string ClientOperationId,
    DateTime AppliedAtUtc) : IAtomicCommandData;

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
    bool HasMore) : IAtomicResultData;
