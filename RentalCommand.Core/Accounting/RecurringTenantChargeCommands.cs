using System.Text.Json.Serialization;
using RentalCommand.Core.Atomic;
using RentalCommand.Core.Operations;

namespace RentalCommand.Core.Accounting;

public sealed record CreateRecurringTenantChargeCommand(
    int PortfolioId,
    StaffOperationActor Actor,
    int TenantAccountId,
    int LeaseAgreementId,
    string DisplayName,
    decimal Amount,
    int LedgerAccountId,
    DateOnly EffectiveStartOn,
    DateOnly? EffectiveEndOn,
    int MonthlyDueDay,
    DateOnly? NextRunDate,
    [property: AtomicFingerprintIgnore] DateTime BusinessNowUtc,
    [property: AtomicFingerprintIgnore] string DeliveryIdempotencyKey) : IAtomicCommandData;

public sealed record UpdateRecurringTenantChargeCommand(
    int PortfolioId,
    StaffOperationActor Actor,
    int TenantAccountId,
    int RecurringTenantChargeId,
    string? DisplayName,
    decimal? Amount,
    int? LedgerAccountId,
    DateOnly? EffectiveStartOn,
    DateOnly? EffectiveEndOn,
    int? MonthlyDueDay,
    DateOnly? NextRunDate,
    [property: AtomicFingerprintIgnore] DateTime BusinessNowUtc,
    [property: AtomicFingerprintIgnore] string DeliveryIdempotencyKey) : IAtomicCommandData;

public sealed record DeactivateRecurringTenantChargeCommand(
    int PortfolioId,
    StaffOperationActor Actor,
    int TenantAccountId,
    int RecurringTenantChargeId,
    [property: AtomicFingerprintIgnore] DateTime BusinessNowUtc,
    [property: AtomicFingerprintIgnore] string DeliveryIdempotencyKey) : IAtomicCommandData;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum RecurringTenantChargeMutationOutcome
{
    Created,
    Updated,
    Deactivated,
    NotFound,
}

public sealed record RecurringTenantChargeMutationResult(
    RecurringTenantChargeMutationOutcome Outcome,
    int EntityId,
    RecurringTenantChargeMutationSnapshot? Snapshot = null);

public sealed record RecurringTenantChargeMutationSnapshot(
    int Id,
    Guid PublicId,
    int TenantAccountId,
    int LeaseAgreementId,
    string DisplayName,
    decimal Amount,
    string Currency,
    int LedgerAccountId,
    DateOnly EffectiveStartOn,
    DateOnly? EffectiveEndOn,
    short MonthlyDueDay,
    DateOnly NextRunDate,
    bool IsActive,
    int PropertyId,
    int UnitId);
