using System.Text.Json.Serialization;
using RentalCommand.Core.Atomic;
using RentalCommand.Core.Enums;

namespace RentalCommand.Core.Accounting;

/// <summary>Atomic create contract for one user-managed chart-of-accounts category.</summary>
public sealed record CreateLedgerAccountCommand(
    int PortfolioId,
    int ActorUserId,
    [property: AtomicFingerprintIgnore] Guid ActorAuthSessionId,
    [property: AtomicFingerprintIgnore] int ActorAccessContextId,
    [property: AtomicFingerprintIgnore] long ActorAccessRevision,
    string Code,
    string Name,
    [property: JsonConverter(typeof(JsonStringEnumConverter))] AccountType AccountType,
    int? ParentAccountId,
    string? SystemKey,
    [property: JsonConverter(typeof(JsonStringEnumConverter))] ScheduleECategory? ScheduleECategory,
    bool IsActive,
    [property: AtomicFingerprintIgnore] string DeliveryIdempotencyKey) : IAtomicCommandData;

/// <summary>Atomic update/deactivation/deletion contract for one chart-of-accounts account.</summary>
public sealed record UpdateLedgerAccountCommand(
    int PortfolioId,
    int ActorUserId,
    [property: AtomicFingerprintIgnore] Guid ActorAuthSessionId,
    [property: AtomicFingerprintIgnore] int ActorAccessContextId,
    [property: AtomicFingerprintIgnore] long ActorAccessRevision,
    int AccountId,
    string? Name,
    [property: JsonConverter(typeof(JsonStringEnumConverter))] AccountType? AccountType,
    int? ParentAccountId,
    bool ParentAccountIdSpecified,
    [property: JsonConverter(typeof(JsonStringEnumConverter))] ScheduleECategory? ScheduleECategory,
    bool? IsActive,
    bool Delete,
    [property: AtomicFingerprintIgnore] string DeliveryIdempotencyKey) : IAtomicCommandData
{
    /// <summary>
    /// Compatibility overload matching the existing PATCH payload, where a non-null parent means
    /// that the parent field was supplied and deletion is not requested.
    /// </summary>
    public UpdateLedgerAccountCommand(
        int portfolioId,
        int actorUserId,
        Guid actorAuthSessionId,
        int actorAccessContextId,
        long actorAccessRevision,
        int accountId,
        string? name,
        AccountType? accountType,
        int? parentAccountId,
        ScheduleECategory? scheduleECategory,
        bool? isActive,
        string deliveryIdempotencyKey)
        : this(
            portfolioId,
            actorUserId,
            actorAuthSessionId,
            actorAccessContextId,
            actorAccessRevision,
            accountId,
            name,
            accountType,
            parentAccountId,
            parentAccountId.HasValue,
            scheduleECategory,
            isActive,
            false,
            deliveryIdempotencyKey)
    {
    }
}

public enum LedgerAccountMutationOutcome
{
    Applied,
    NotFound,
    Deleted,
}

/// <summary>Receipt-safe chart-of-accounts projection returned by Atomic mutations.</summary>
public sealed record LedgerAccountSnapshot(
    int Id,
    Guid PublicId,
    int PortfolioId,
    string Code,
    string Name,
    [property: JsonConverter(typeof(JsonStringEnumConverter))] AccountType AccountType,
    [property: JsonConverter(typeof(JsonStringEnumConverter))] NormalBalance NormalBalance,
    int? ParentAccountId,
    string? SystemKey,
    [property: JsonConverter(typeof(JsonStringEnumConverter))] ScheduleECategory? ScheduleECategory,
    bool IsSystem,
    bool IsActive,
    bool HasPostedLines);

public sealed record LedgerAccountMutationResult(
    [property: JsonConverter(typeof(JsonStringEnumConverter))] LedgerAccountMutationOutcome Outcome,
    LedgerAccountSnapshot? Account);
