using Microsoft.EntityFrameworkCore;
using RentalCommand.Core.Atomic;
using RentalCommand.Core.Entities;

namespace RentalCommand.Data.Atomic;

/// <summary>
/// Scoped metadata and audit/outbox companion for the exact transaction-owned DbContext.
/// Domain code injects RentalCommandDbContext directly for persistence.
/// </summary>
internal sealed class AtomicCommandContext : IAtomicCommandContext
{
    private readonly RentalCommandDbContext _db;
    private readonly AtomicAuditScope _auditScope;
    private readonly AtomicLockingPersistence _locking;
    private readonly TimeProvider _timeProvider;
    private readonly List<OutboxMessage> _outbox = [];
    private bool _outboxMaterialized;

    public AtomicCommandContext(
        RentalCommandDbContext db,
        AtomicAuditScope auditScope,
        TimeProvider timeProvider)
    {
        _db = db;
        _auditScope = auditScope;
        _locking = new AtomicLockingPersistence(db);
        _timeProvider = timeProvider;
    }

    public bool IsActive => _auditScope.IsActive;
    public Guid AttemptId { get; private set; }
    public Guid AtomicReceiptId { get; private set; }
    public DateTime BusinessNowUtc => _timeProvider.GetUtcNow().UtcDateTime;
    internal AtomicAuditScope AuditScope => _auditScope;
    internal bool Owns(RentalCommandDbContext db) => ReferenceEquals(_db, db);

    internal void BeginAttempt(Guid attemptId)
    {
        if (AttemptId != Guid.Empty || _outbox.Count != 0 || _outboxMaterialized)
        {
            throw new AtomicArchitectureException("The scoped atomic command context is already in use.");
        }

        AttemptId = attemptId;
        AtomicReceiptId = Guid.Empty;
    }

    internal void BindReceipt(Guid receiptId)
    {
        if (AttemptId == Guid.Empty)
        {
            throw new AtomicArchitectureException("The atomic receipt cannot be bound outside an active attempt.");
        }

        if (receiptId == Guid.Empty)
        {
            throw new ArgumentException("The atomic receipt identifier is required.", nameof(receiptId));
        }

        AtomicReceiptId = receiptId;
    }

    internal void EndAttempt()
    {
        AttemptId = Guid.Empty;
        AtomicReceiptId = Guid.Empty;
        _outbox.Clear();
        _outboxMaterialized = false;
    }

    public Task<DateTime> ReadDatabaseClockUtcAsync(CancellationToken ct = default) =>
        _db.Database
            .SqlQuery<DateTime>($"SELECT clock_timestamp() AS \"Value\"")
            .SingleAsync(ct);

    public Task AcquireLockAsync(
        string lockNamespace,
        int aggregateId,
        CancellationToken ct = default) =>
        _locking.AcquireAsync(lockNamespace, aggregateId, ct);

    public Task AcquireLockAsync(
        string lockNamespace,
        Guid aggregateId,
        CancellationToken ct = default) =>
        _locking.AcquireAsync(lockNamespace, aggregateId, ct);

    public async Task<AtomicBusinessFlush> FlushBusinessAsync(CancellationToken ct = default)
    {
        var priorOrdinal = _auditScope.CurrentMutationOrdinal;
        var rows = await _db.SaveChangesAsync(ct);
        return new AtomicBusinessFlush(rows, _auditScope.GetMutationsAfter(priorOrdinal));
    }

    public void BindSemanticAudit(object entityReference, AtomicSemanticAudit audit)
    {
        ArgumentNullException.ThrowIfNull(entityReference);
        _db.ChangeTracker.DetectChanges();
        _auditScope.BindSemantic(entityReference, _db.Entry(entityReference), audit);
    }

    public void UseDatabaseWallClockForAudit(DateTime occurredAtUtc) =>
        _auditScope.UseDatabaseWallClockForTrackedMutations(occurredAtUtc);

    public void EnrichMutation(AtomicAuditMutation mutation, AtomicSemanticAudit audit) =>
        _auditScope.Enrich(mutation, audit);

    public void StageSemanticEvent(AtomicSemanticAudit audit) =>
        _auditScope.StageSemanticEvent(audit, _timeProvider.GetUtcNow().UtcDateTime);

    public void StageSemanticEvent(AtomicSemanticAudit audit, DateTime occurredAtUtc) =>
        _auditScope.StageSemanticEvent(audit, occurredAtUtc);

    public void StageOutbox(OutboxMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (_outboxMaterialized)
        {
            throw new InvalidOperationException("The final outbox batch has already been materialized.");
        }

        if (string.IsNullOrWhiteSpace(message.IdempotencyKey))
        {
            throw new ArgumentException("Outbox idempotency key is required.", nameof(message));
        }

        var now = _timeProvider.GetUtcNow().UtcDateTime;
        if (message.CreatedAtUtc == default) message.CreatedAtUtc = now;
        if (message.NextAttemptAtUtc == default) message.NextAttemptAtUtc = now;
        _outbox.Add(message);
    }

    internal void MaterializeOutbox()
    {
        if (_outboxMaterialized)
        {
            throw new InvalidOperationException("The final outbox batch has already been materialized.");
        }

        _outboxMaterialized = true;
        if (_outbox.Count > 0)
        {
            _db.OutboxMessages.AddRange(_outbox);
        }
    }
}
