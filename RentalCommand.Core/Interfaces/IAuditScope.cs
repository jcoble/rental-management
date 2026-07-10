using RentalCommand.Core.Atomic;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;

namespace RentalCommand.Core.Interfaces;

/// <summary>
/// Attempt-scoped audit staging area. Mutation identity is the physical command attempt plus the
/// tracked entity reference plus a monotonic mutation ordinal; it is deliberately not just
/// (entity type, id, operation), because the same command may update one entity more than once.
/// </summary>
public interface IAuditScope
{
    bool IsActive { get; }
    AtomicCommandIdentity Command { get; }
    Guid AttemptId { get; }
    long CurrentMutationOrdinal { get; }

    /// <summary>Activates a fresh audit scope for one physical execution-strategy attempt.</summary>
    IDisposable BeginAttempt(AtomicCommandIdentity command, Guid attemptId);

    /// <summary>
    /// Reserves the exact mutation captured by SavingChanges. A later SavedChanges call stages the
    /// row under this ordinal after a database-generated key is available.
    /// </summary>
    long ReserveMutation(
        object entityReference,
        string entityType,
        int entityId,
        AuditLogOperation operation);

    /// <summary>Stages the generic row materialized after the business flush; never flushes it.</summary>
    void StageGeneric(long mutationOrdinal, AuditLog row);

    /// <summary>
    /// Resolves a semantic audit against the newest unenriched generic mutation with the same
    /// semantic signature. When there is no generic mutation, a new semantic-only ordinal is
    /// reserved. The returned ordinal makes the enrichment target explicit.
    /// </summary>
    AuditResolution ResolveExplicit(string entityType, int entityId, AuditLogOperation operation);

    /// <summary>Resolves semantic data against one exact mutation handle.</summary>
    AuditResolution ResolveExplicit(long mutationOrdinal);

    /// <summary>Returns generic mutations staged after the supplied ordinal.</summary>
    IReadOnlyList<AuditMutationDescriptor> GetMutationsAfter(long mutationOrdinal);

    /// <summary>Stages a semantic-only row for a previously reserved ordinal.</summary>
    void StageExplicit(long mutationOrdinal, AuditLog row);

    /// <summary>Returns all staged rows exactly once for the owner's final companion flush.</summary>
    IReadOnlyList<AuditLog> TakeStagedRows();

    /// <summary>
    /// Grants one set-based ExecuteUpdate/ExecuteDelete command against the named auditable entity.
    /// The semantic audit must be staged before granting the permit.
    /// </summary>
    void AuthorizeSetBasedMutation(string entityType);

    /// <summary>Consumes one matching set-based mutation permit.</summary>
    bool TryConsumeSetBasedMutation(string entityType);
}

public enum AuditWrite
{
    Insert,
    Enrich,
}

public readonly record struct AuditResolution(
    AuditWrite Decision,
    AuditLog? ExistingRow,
    long MutationOrdinal);

public sealed record AuditMutationDescriptor(
    long MutationOrdinal,
    object EntityReference,
    string EntityType,
    int EntityId,
    AuditLogOperation Operation);
