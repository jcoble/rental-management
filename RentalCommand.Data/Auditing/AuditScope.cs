using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Interfaces;

namespace RentalCommand.Data.Auditing;

/// <summary>
/// Request-scoped, thread-safe registry shared by the generic audit interceptor and the explicit
/// <c>AuditTrailService.LogAsync</c> path. Reconciles the two so a single change yields exactly one
/// <see cref="AuditLog"/> row per request — and the rich explicit row always wins, whichever order
/// the two writers run in:
/// <list type="bullet">
/// <item>Explicit before the entity's save → the explicit row is inserted and the later generic twin
/// is suppressed.</item>
/// <item>Generic before the explicit log (the common case — services log after <c>SaveChanges</c>) →
/// the generic row is kept as a handle and the explicit log enriches it in place.</item>
/// </list>
/// </summary>
public sealed class AuditScope : IAuditScope
{
    private sealed class Slot
    {
        public bool ExplicitOwned;
        public AuditLog? GenericRow;
    }

    private readonly object _lock = new();
    private readonly Dictionary<string, Slot> _slots = new(StringComparer.Ordinal);

    private static string Key(string entityType, int entityId, AuditLogOperation operation) =>
        $"{entityType}|{entityId}|{(int)operation}";

    public bool TryAddGenericRow(string entityType, int entityId, AuditLogOperation operation, AuditLog row)
    {
        var key = Key(entityType, entityId, operation);
        lock (_lock)
        {
            // A slot already exists: either an explicit log pre-claimed this key (before the entity's
            // save), or a generic twin was already recorded this request. Either way, don't write again.
            if (_slots.ContainsKey(key))
            {
                return false;
            }

            _slots[key] = new Slot { GenericRow = row };
            return true;
        }
    }

    public AuditResolution ResolveExplicit(string entityType, int entityId, AuditLogOperation operation)
    {
        var key = Key(entityType, entityId, operation);
        lock (_lock)
        {
            if (_slots.TryGetValue(key, out var slot))
            {
                if (slot.ExplicitOwned)
                {
                    // A prior explicit log already wrote/enriched this key — skip the duplicate.
                    return new AuditResolution(AuditWrite.Skip, null);
                }

                // The generic interceptor wrote first; take ownership and enrich its row in place.
                slot.ExplicitOwned = true;
                return slot.GenericRow is not null
                    ? new AuditResolution(AuditWrite.Enrich, slot.GenericRow)
                    : new AuditResolution(AuditWrite.Insert, null);
            }

            // Nothing recorded yet — claim the key and insert a fresh rich row. A later generic twin for
            // the same key will defer (TryAddGenericRow sees the slot and returns false).
            _slots[key] = new Slot { ExplicitOwned = true };
            return new AuditResolution(AuditWrite.Insert, null);
        }
    }
}
