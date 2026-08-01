using RentalCommand.Core.Atomic;

namespace RentalCommand.Core.Money;

public enum AtomicMoneyDomain
{
    Expense = 0,
    RecurringExpense = 1,
    Loan = 2,
    OwnerDistribution = 3,
    CapitalAsset = 4,
    PropertyDisposition = 5,
    OwnerContribution = 6,
}

public enum AtomicMoneyOperation
{
    Create = 0,
    Update = 1,
    Delete = 2,
    CapitalizeExpense = 3,
    PostPayment = 4,
    Approve = 5,
    Reject = 6,
}

public sealed record AtomicMoneyMutationCommand(
    int PortfolioId,
    int ActorUserId,
    [property: AtomicFingerprintIgnore]
    Guid AuthSessionId,
    [property: AtomicFingerprintIgnore]
    int AccessContextId,
    [property: AtomicFingerprintIgnore]
    long ExpectedAccessRevision,
    string RequiredCapability,
    AtomicMoneyDomain Domain,
    AtomicMoneyOperation Operation,
    int EntityId,
    string IdempotencyKey,
    string RequestJson,
    [property: AtomicFingerprintIgnore]
    DateTime BusinessNowUtc) : IAtomicCommandData;

public sealed record AtomicMoneyMutationResult(
    bool Found,
    bool Applied,
    int EntityId,
    string? ResponseJson = null);

public static class AtomicMoneyMutationContracts
{
    public const string ResultContract = "money.scoped-mutation.v2";
}
