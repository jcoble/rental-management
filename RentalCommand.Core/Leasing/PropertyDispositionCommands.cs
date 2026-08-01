using RentalCommand.Core.Atomic;

namespace RentalCommand.Core.Leasing;

public sealed record CreatePropertyDispositionCommand(
    int PortfolioId,
    int PropertyId,
    DateTime ClosedOnDate,
    decimal SalePrice,
    decimal SellingCosts,
    string? BuyerName,
    string? Memo,
    int ActorUserId,
    [property: AtomicFingerprintIgnore] Guid AuthSessionId,
    [property: AtomicFingerprintIgnore] int AccessContextId,
    [property: AtomicFingerprintIgnore] long ExpectedAccessRevision,
    [property: AtomicFingerprintIgnore] string DeliveryIdempotencyKey) : IAtomicCommandData;

public enum CreatePropertyDispositionOutcome
{
    Created,
    PropertyNotFoundOrAlreadyDisposed,
}

public sealed record CreatePropertyDispositionResult(
    CreatePropertyDispositionOutcome Outcome,
    int? DispositionId,
    int LeaseManagementCount,
    int TenantAccountCount);
