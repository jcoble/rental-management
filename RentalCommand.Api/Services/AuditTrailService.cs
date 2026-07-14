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
    private readonly IAtomicAuditEventSink _sink;

    public AuditTrailService(IAtomicAuditEventSink sink) => _sink = sink;

    public void EnsureAtomicCommand() => _sink.EnsureActive();

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
        _sink.Stage(new AtomicSemanticAudit(
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
