using RentalCommand.Core.Atomic;
using RentalCommand.Core.Enums;

namespace RentalCommand.Core.Operations;

public sealed record CreateEvictionCaseCommand(
    int PortfolioId, StaffOperationActor Actor, int LeaseManagementId,
    int? LeaseAgreementId, int[] RespondentLeaseManagementPartyIds,
    EvictionCaseStatus Status, DateTime? FiledOnDateUtc, DateTime? HearingDateUtc,
    string? CourtName, string? CaseNumber, string? Notes,
    string DeliveryIdempotencyKey) : IAtomicCommandData;

public sealed record UpdateEvictionCaseCommand(
    int PortfolioId, StaffOperationActor Actor, int EvictionCaseId,
    EvictionCaseStatus? Status, DateTime? FiledOnDateUtc, DateTime? HearingDateUtc,
    DateTime? ResolvedOnDateUtc, string? CourtName, string? CaseNumber,
    string? Resolution, string? Notes, string DeliveryIdempotencyKey) : IAtomicCommandData;

public sealed record AddEvictionCaseEventCommand(
    int PortfolioId, StaffOperationActor Actor, int EvictionCaseId,
    EvictionEventType EventType, DateTime EventDateUtc, string? Notes,
    string DeliveryIdempotencyKey) : IAtomicCommandData;

public sealed record DeleteEvictionCaseCommand(
    int PortfolioId, StaffOperationActor Actor, int EvictionCaseId,
    string DeliveryIdempotencyKey) : IAtomicCommandData;
