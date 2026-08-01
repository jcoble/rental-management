using RentalCommand.Core.Atomic;

namespace RentalCommand.Core.Operations;

public sealed record CreateVendorRatingCommand(
    int PortfolioId,
    StaffOperationActor Actor,
    int VendorId,
    int? WorkOrderId,
    int Stars,
    string? Comment,
    [property: AtomicFingerprintIgnore] DateTime BusinessNowUtc,
    [property: AtomicFingerprintIgnore] string DeliveryIdempotencyKey) : IAtomicCommandData;

public sealed record VendorRatingMutationResult(
    OperationMutationOutcome Outcome,
    int RatingId,
    int VendorId,
    string? ResponseJson = null);
