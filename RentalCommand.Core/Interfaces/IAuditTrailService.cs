using RentalCommand.Core.Enums;

namespace RentalCommand.Core.Interfaces;

/// <summary>
/// Stages append-only semantic detail on the current atomic command audit. Calls outside an admitted
/// atomic command are rejected so audit can never commit separately from its business mutation.
/// </summary>
public interface IAuditTrailService
{
    /// <summary>Fail before a business write when the caller is not inside an atomic command.</summary>
    void EnsureAtomicCommand();

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
