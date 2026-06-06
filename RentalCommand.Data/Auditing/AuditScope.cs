using RentalCommand.Core.Enums;
using RentalCommand.Core.Interfaces;

namespace RentalCommand.Data.Auditing;

/// <summary>
/// Request-scoped, thread-safe de-dupe registry shared by the generic audit interceptor and the
/// explicit <c>AuditTrailService.LogAsync</c> path. The first writer to <see cref="Claim"/> a
/// (entityType, entityId, operation) key wins and writes the audit row; every later claim of the
/// same key is suppressed, so a single change yields exactly one row per request.
/// </summary>
public sealed class AuditScope : IAuditScope
{
    private readonly object _lock = new();
    private readonly HashSet<string> _claimed = new(StringComparer.Ordinal);

    public bool Claim(string entityType, int entityId, AuditLogOperation operation)
    {
        var key = $"{entityType}|{entityId}|{(int)operation}";
        lock (_lock)
        {
            return _claimed.Add(key);
        }
    }
}
