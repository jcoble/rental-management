using RentalCommand.Core.Enums;

namespace RentalCommand.Core.Interfaces;

/// <summary>
/// Writes append-only <see cref="Entities.AuditLog"/> entries. Audit entries are never
/// updated or deleted. Phase 0 defines the contract only; the implementation lands later.
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
