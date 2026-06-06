using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Interfaces;
using RentalCommand.Data;

namespace RentalCommand.Api.Services;

/// <summary>
/// Persists append-only <see cref="AuditLog"/> rows for the legally-rich semantic events. Each call
/// flushes via <c>SaveChangesAsync</c> so audit entries are durable even if the enclosing
/// unit-of-work is later rolled back.
///
/// <para>Coordinates with the generic <c>AuditSaveChangesInterceptor</c> through <see cref="IAuditScope"/>
/// so a change is recorded exactly once and the rich explicit row always wins — <b>regardless of call
/// order</b>. When a service logs <i>after</i> the entity's save (the common case), the interceptor has
/// already written a generic twin; this enriches that row in place with the full snapshot / change
/// reason instead of being silently suppressed. When a service logs <i>before</i> the save, it inserts
/// the rich row and the later generic twin defers.</para>
/// </summary>
public sealed class AuditTrailService : IAuditTrailService
{
    private readonly RentalCommandDbContext _db;
    private readonly IAuditScope _scope;

    public AuditTrailService(RentalCommandDbContext db, IAuditScope scope)
    {
        _db = db;
        _scope = scope;
    }

    /// <inheritdoc />
    public async Task LogAsync(
        int portfolioId,
        string entityType,
        int entityId,
        AuditLogOperation operation,
        int? userId = null,
        string? actorLabel = null,
        string? oldValues = null,
        string? newValues = null,
        string? changeReason = null,
        string? ipAddress = null,
        CancellationToken ct = default)
    {
        var resolution = _scope.ResolveExplicit(entityType, entityId, operation);

        switch (resolution.Decision)
        {
            case AuditWrite.Skip:
                // A prior explicit log already covered this key this request — avoid a duplicate.
                return;

            case AuditWrite.Enrich:
                // The generic interceptor already wrote a twin for this change earlier in the request.
                // Overwrite it in place with the rich payload. Explicit, non-null values win; anything
                // the caller leaves null keeps the generic capture (its actor/IP, and the changed-property
                // diff when only a ChangeReason is supplied).
                var existing = resolution.ExistingRow!;
                if (oldValues is not null) existing.OldValues = oldValues;
                if (newValues is not null) existing.NewValues = newValues;
                if (changeReason is not null) existing.ChangeReason = changeReason;
                if (userId.HasValue) existing.UserId = userId;
                if (actorLabel is not null) existing.ActorLabel = actorLabel;
                if (ipAddress is not null) existing.IpAddress = ipAddress;
                await _db.SaveChangesAsync(ct);
                return;

            default: // AuditWrite.Insert
                _db.AuditLogs.Add(new AuditLog
                {
                    PortfolioId = portfolioId,
                    EntityType = entityType,
                    EntityId = entityId,
                    Operation = operation,
                    UserId = userId,
                    ActorLabel = actorLabel,
                    OldValues = oldValues,
                    NewValues = newValues,
                    ChangeReason = changeReason,
                    IpAddress = ipAddress,
                    Timestamp = DateTime.UtcNow,
                });
                await _db.SaveChangesAsync(ct);
                return;
        }
    }
}
