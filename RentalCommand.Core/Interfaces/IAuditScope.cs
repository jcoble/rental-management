using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;

namespace RentalCommand.Core.Interfaces;

/// <summary>
/// Per-request registry that coordinates the generic audit interceptor with the explicit
/// <c>AuditTrailService.LogAsync</c> path so a single change yields exactly one <see cref="AuditLog"/>
/// row — and the rich explicit row always wins, <b>regardless of whether it runs before or after the
/// entity's <c>SaveChanges</c></b>. Both writers reconcile on the same (entityType, entityId,
/// operation) key. Scoped to the request, thread-safe.
/// </summary>
public interface IAuditScope
{
    /// <summary>
    /// Called by the generic interceptor for a row it is about to write. Returns <c>true</c> if the
    /// generic row should be written — in which case the scope keeps a reference to <paramref name="row"/>
    /// so a later explicit log can enrich it in place. Returns <c>false</c> when an explicit rich log
    /// already owns this key (it ran before the entity's save), so the generic twin is suppressed.
    /// </summary>
    bool TryAddGenericRow(string entityType, int entityId, AuditLogOperation operation, AuditLog row);

    /// <summary>
    /// Called by the explicit <c>AuditTrailService.LogAsync</c>. Takes ownership of the key and reports
    /// how to record the rich row: <see cref="AuditWrite.Insert"/> a fresh row, <see cref="AuditWrite.Enrich"/>
    /// the generic twin already written this request (returned in <see cref="AuditResolution.ExistingRow"/>),
    /// or <see cref="AuditWrite.Skip"/> when a prior explicit log already covered this key.
    /// </summary>
    AuditResolution ResolveExplicit(string entityType, int entityId, AuditLogOperation operation);
}

/// <summary>How an explicit audit log should be recorded for a key (see <see cref="IAuditScope.ResolveExplicit"/>).</summary>
public enum AuditWrite
{
    /// <summary>No row exists yet for this key — insert a fresh rich row.</summary>
    Insert,

    /// <summary>A generic twin was already written this request — enrich it in place instead of inserting.</summary>
    Enrich,

    /// <summary>A prior explicit log already owns this key — skip to avoid a duplicate.</summary>
    Skip,
}

/// <summary>
/// Result of <see cref="IAuditScope.ResolveExplicit"/>: the decision plus the row to enrich when
/// <see cref="Decision"/> is <see cref="AuditWrite.Enrich"/> (otherwise <c>null</c>).
/// </summary>
public readonly record struct AuditResolution(AuditWrite Decision, AuditLog? ExistingRow);
