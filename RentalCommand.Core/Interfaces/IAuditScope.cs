using RentalCommand.Core.Enums;

namespace RentalCommand.Core.Interfaces;

/// <summary>
/// Per-request de-dupe registry that coordinates the generic audit interceptor with the explicit
/// <c>AuditTrailService.LogAsync</c> path so a single change is never recorded twice. Both writers
/// claim the same (entityType, entityId, operation) key; the first claim wins and writes the row,
/// every later claim for the same key is skipped. Scoped to the request, thread-safe.
/// </summary>
public interface IAuditScope
{
    /// <summary>
    /// Atomically claims the (<paramref name="entityType"/>, <paramref name="entityId"/>,
    /// <paramref name="operation"/>) key. Returns <c>true</c> if THIS call claimed it (the caller
    /// should write the audit row); <c>false</c> if it was already claimed (the caller should skip).
    /// </summary>
    bool Claim(string entityType, int entityId, AuditLogOperation operation);
}
