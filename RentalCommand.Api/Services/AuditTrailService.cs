using RentalCommand.Core.Atomic;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Interfaces;

namespace RentalCommand.Api.Services;

/// <summary>
/// Stages semantic detail on the sole atomic audit path. The atomic kernel materializes the row with
/// the command receipt in the same transaction; this service deliberately performs no database save.
/// Unconverted callers and their required follow-up are inventoried in
/// <c>Docs/Reviews/2026-07-14-tsk-672-canonical-audit-cutover.md</c>.
/// </summary>
public sealed class AuditTrailService : IAuditTrailService
{
    private readonly IAtomicCommandContext _atomicContext;

    public AuditTrailService(IAtomicCommandContext atomicContext) => _atomicContext = atomicContext;

    public void EnsureAtomicCommand()
    {
        if (!_atomicContext.IsActive)
        {
            throw new AtomicArchitectureException("Audit events must be staged inside an active atomic command.");
        }
    }

    /// <inheritdoc />
    public Task LogAsync(
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
        ct.ThrowIfCancellationRequested();
        _atomicContext.StageSemanticEvent(new AtomicSemanticAudit(
            portfolioId,
            entityType,
            entityId,
            operation,
            userId,
            actorLabel,
            oldValues,
            newValues,
            changeReason,
            ipAddress));
        return Task.CompletedTask;
    }
}
