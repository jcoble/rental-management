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
    string RequestIdentity,
    DateTime ConfirmedAtUtc) : IAtomicCommandData;

public enum ConfirmAccountingMappingOutcome
{
    Applied,
    ConnectionNotFound,
    InvalidTarget,
}

public sealed record ConfirmAccountingMappingResult(
    ConfirmAccountingMappingOutcome Outcome,
    int MappingId,
    int PromotedCount,
    IReadOnlyList<int> PaymentIds,
    IReadOnlyList<int> ExpenseIds) : IAtomicResultData;
