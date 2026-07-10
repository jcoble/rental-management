using RentalCommand.Core.Enums;

namespace RentalCommand.Core.Interfaces;

/// <summary>
/// Stages append-only <see cref="Entities.AuditLog"/> entries in the active atomic command.
/// Implementations must never call SaveChanges or make an audit independently durable; the owner
/// transaction flushes the staged row with the business mutation and command receipt.
/// </summary>
public interface IAuditTrailService
{
    /// <summary>
    /// Record an audit event. Either <paramref name="userId"/> (a real <see cref="Entities.ApplicationUser"/>)
    /// or <paramref name="actorLabel"/> (a system/AI actor, e.g. "ai-scan") identifies the actor.
    /// </summary>
    Task LogAsync(
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
        CancellationToken ct = default);
}
