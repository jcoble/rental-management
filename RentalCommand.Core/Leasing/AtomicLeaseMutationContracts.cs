namespace RentalCommand.Core.Leasing;

public enum AtomicTenantAccessTransitionKind
{
    Retain,
    Revoke,
    ContinueOnReplacement,
}

public sealed record AtomicTenantAccessTransition(
    int SourcePartyId,
    int? ReplacementPartyId,
    AtomicTenantAccessTransitionKind Kind);

public sealed record AtomicTenantAccessTransitionResult(
    IReadOnlyList<int> ActiveAccessIds,
    IReadOnlyList<int> RevokedAccessIds,
    IReadOnlyList<int> CreatedAccessIds);

public sealed record AtomicReturnPossessionPartyInput(
    int PartyId,
    ReturnPartyDisposition Disposition);

public sealed record AtomicReturnPossessionAccessInput(
    int AccessId,
    ReturnAccessDisposition Disposition);

public sealed record AtomicReturnPossessionMutationResult(
    ReturnPossessionOutcome Outcome,
    int? TurnoverPeriodId,
    DateTime? PossessionReturnedAtUtc,
    IReadOnlyList<int> EndedPartyIds,
    IReadOnlyList<int> RevokedAccessIds);

/// <summary>
/// PostgreSQL-owned lease graph mutations. Input sets are validated, joined, partitioned, and
/// applied by one statement per operation; handlers never materialize a relationship graph.
/// </summary>
public interface IAtomicLeaseMutationPersistence
{
    Task<AtomicTenantAccessTransitionResult> TransitionTenantAccessAsync(
        int portfolioId,
        int leaseManagementId,
        IReadOnlyList<AtomicTenantAccessTransition> transitions,
        int actorUserId,
        DateTime changedAtUtc,
        string reason,
        CancellationToken ct = default);

    Task<AtomicReturnPossessionMutationResult> ReturnPossessionAsync(
        int portfolioId,
        int leaseManagementId,
        int unitId,
        DateOnly requiredBusinessDate,
        IReadOnlyList<AtomicReturnPossessionPartyInput> parties,
        IReadOnlyList<AtomicReturnPossessionAccessInput> accesses,
        int actorUserId,
        DateTime changedAtUtc,
        string turnoverReason,
        CancellationToken ct = default);
}
