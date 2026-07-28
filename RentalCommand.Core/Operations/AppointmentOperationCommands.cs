using RentalCommand.Core.Atomic;
using RentalCommand.Core.Enums;

namespace RentalCommand.Core.Operations;

public sealed record CreateAppointmentCommand(
    int PortfolioId, StaffOperationActor Actor, int? PropertyId, int? UnitId,
    int? LeaseManagementId, int? RentalApplicationId, int? TenantId,
    string Title, string? ProspectName, string? ProspectEmail,
    AppointmentType Type, AppointmentStatus Status, DateTime ScheduledStartUtc,
    DateTime? ScheduledEndUtc, string? AssignedTo, string? Notes,
    DateTime BusinessNowUtc,
    string DeliveryIdempotencyKey) : IAtomicCommandData;

public sealed record UpdateAppointmentCommand(
    int PortfolioId, StaffOperationActor Actor, int AppointmentId,
    int? PropertyId, int? UnitId, int? LeaseManagementId, int? RentalApplicationId,
    int? TenantId, string? Title, string? ProspectName, string? ProspectEmail,
    AppointmentType? Type, AppointmentStatus? Status, DateTime? ScheduledStartUtc,
    DateTime? ScheduledEndUtc, string? AssignedTo, string? Notes,
    DateTime BusinessNowUtc,
    string DeliveryIdempotencyKey) : IAtomicCommandData;

public sealed record DeleteAppointmentCommand(
    int PortfolioId, StaffOperationActor Actor, int AppointmentId,
    int? ExpectedPropertyId,
    DateTime BusinessNowUtc,
    string DeliveryIdempotencyKey) : IAtomicCommandData;
