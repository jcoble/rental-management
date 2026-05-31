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

    public AuditTrailService(RentalCommandDbContext db)
    {
        _db = db;
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
