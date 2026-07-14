using RentalCommand.Core.Atomic;

namespace RentalCommand.Core.Operations;

public sealed record CreateVendorRatingCommand(
    int PortfolioId,
    StaffOperationActor Actor,
    int VendorId,
    int? WorkOrderId,
    int Stars,
    string? Comment,
    string DeliveryIdempotencyKey) : IAtomicCommandData;

public sealed record VendorRatingMutationResult(
    OperationMutationOutcome Outcome,
    int RatingId,
    int VendorId,
    string? ResponseJson = null) : IAtomicResultData;
