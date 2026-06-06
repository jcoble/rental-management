using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Interfaces;
using RentalCommand.Data;

namespace RentalCommand.Api.Services;

/// <summary>
/// Persists append-only <see cref="AuditLog"/> rows. Each call writes one row and
/// immediately flushes via <c>SaveChangesAsync</c> so audit entries are durable even if
/// the enclosing unit-of-work is later rolled back.
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
        // Coordinate with the generic audit interceptor: the first writer to claim this
        // (entityType, entityId, operation) for the request wins. If the interceptor already
        // recorded it, skip the duplicate; otherwise this rich explicit row is authoritative.
        if (!_scope.Claim(entityType, entityId, operation))
        {
            return;
        }

        var entry = new AuditLog
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
        };

        _db.AuditLogs.Add(entry);
        await _db.SaveChangesAsync(ct);
    }
}
