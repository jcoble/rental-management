using RentalCommand.Core.Atomic;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Interfaces;

namespace RentalCommand.Data.Auditing;

/// <summary>
/// Mutable audit state owned by exactly one atomic write attempt. Rows remain here, outside the EF
/// tracker, until the owner unit of work performs its final companion flush.
/// </summary>
public sealed class AuditScope : IAuditScope
{
    private sealed class MutationSlot
    {
        public required long Ordinal { get; init; }
        public object? EntityReference { get; set; }
        public required string EntityType { get; init; }
        public required int EntityId { get; set; }
        public required AuditLogOperation Operation { get; init; }
        public bool GenericCaptured { get; set; }
        public bool SemanticApplied { get; set; }
        public AuditLog? Row { get; set; }
    }

    private readonly object _lock = new();
    private readonly List<MutationSlot> _slots = [];
    private readonly List<AuditLog> _stagedRows = [];
    private readonly Dictionary<string, int> _setBasedPermits = new(StringComparer.Ordinal);
    private AtomicCommandIdentity? _command;
    private Guid _attemptId;
    private long _nextOrdinal;
    private bool _rowsTaken;

    public bool IsActive
    {
        get
        {
            lock (_lock)
            {
                return _command is not null;
            }
        }
    }

    public AtomicCommandIdentity Command =>
        _command ?? throw new InvalidOperationException("No atomic audit attempt is active.");

    public Guid AttemptId => IsActive
        ? _attemptId
        : throw new InvalidOperationException("No atomic audit attempt is active.");

    public long CurrentMutationOrdinal
    {
        get
        {
            lock (_lock)
            {
                EnsureActive();
                return _nextOrdinal;
            }
        }
    }

    public IDisposable BeginAttempt(AtomicCommandIdentity command, Guid attemptId)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (attemptId == Guid.Empty)
        {
            throw new ArgumentException("Attempt id cannot be empty.", nameof(attemptId));
        }

        lock (_lock)
        {
            if (_command is not null)
            {
                throw new InvalidOperationException("This audit scope already owns an atomic attempt.");
            }

            _command = command;
            _attemptId = attemptId;
            _nextOrdinal = 0;
            _rowsTaken = false;
            _slots.Clear();
            _stagedRows.Clear();
            _setBasedPermits.Clear();
        }

        return new AttemptLease(this);
    }

    public long ReserveMutation(
        object entityReference,
        string entityType,
        int entityId,
        AuditLogOperation operation)
    {
        ArgumentNullException.ThrowIfNull(entityReference);
        lock (_lock)
        {
            EnsureActive();
            EnsureRowsOpen();

            // A semantic log may be staged before SaveChanges for an entity whose key already
            // exists. Bind that ordered semantic slot to this exact tracked reference.
            var preStaged = _slots.LastOrDefault(slot =>
                slot.EntityReference is null
                && slot.SemanticApplied
                && !slot.GenericCaptured
                && slot.EntityType == entityType
                && slot.EntityId == entityId
                && slot.Operation == operation);
            if (preStaged is not null)
            {
                preStaged.EntityReference = entityReference;
                return preStaged.Ordinal;
            }

            var ordinal = ++_nextOrdinal;
            _slots.Add(new MutationSlot
            {
                Ordinal = ordinal,
                EntityReference = entityReference,
                EntityType = entityType,
                EntityId = entityId,
                Operation = operation,
            });
            return ordinal;
        }
    }

    public void StageGeneric(long mutationOrdinal, AuditLog row)
    {
        ArgumentNullException.ThrowIfNull(row);
        lock (_lock)
        {
            EnsureActive();
            EnsureRowsOpen();
            var slot = FindSlot(mutationOrdinal);
            slot.GenericCaptured = true;
            slot.EntityId = row.EntityId;

            if (slot.Row is null)
            {
                Stamp(row, mutationOrdinal);
                slot.Row = row;
                _stagedRows.Add(row);
                return;
            }

            // Semantic-before-save: retain semantic values and fill only the generic fields the
            // caller did not provide. There is still exactly one row for this mutation ordinal.
            slot.Row.OldValues ??= row.OldValues;
            slot.Row.NewValues ??= row.NewValues;
            slot.Row.UserId ??= row.UserId;
            slot.Row.ActorLabel ??= row.ActorLabel;
            slot.Row.IpAddress ??= row.IpAddress;
        }
    }

    public AuditResolution ResolveExplicit(
        string entityType,
        int entityId,
        AuditLogOperation operation)
    {
        lock (_lock)
        {
            EnsureActive();
            EnsureRowsOpen();

            var genericCandidates = _slots.Where(slot =>
                slot.GenericCaptured
                && !slot.SemanticApplied
                && slot.EntityType == entityType
                && slot.EntityId == entityId
                && slot.Operation == operation)
                .ToArray();
            if (genericCandidates.Length > 1)
            {
                throw new InvalidOperationException(
                    $"More than one unenriched {entityType}/{entityId}/{operation} mutation exists. " +
                    "Use IAtomicWriteAttempt.StageExactAuditAsync with the flush mutation handle.");
            }

            if (genericCandidates.Length == 1)
            {
                var generic = genericCandidates[0];
                generic.SemanticApplied = true;
                return new AuditResolution(AuditWrite.Enrich, generic.Row, generic.Ordinal);
            }

            var ordinal = ++_nextOrdinal;
            _slots.Add(new MutationSlot
            {
                Ordinal = ordinal,
                EntityType = entityType,
                EntityId = entityId,
                Operation = operation,
                SemanticApplied = true,
            });
            return new AuditResolution(AuditWrite.Insert, null, ordinal);
        }
    }

    public AuditResolution ResolveExplicit(long mutationOrdinal)
    {
        lock (_lock)
        {
            EnsureActive();
            EnsureRowsOpen();
            var slot = FindSlot(mutationOrdinal);
            if (!slot.GenericCaptured || slot.Row is null)
            {
                throw new InvalidOperationException(
                    $"Audit mutation {mutationOrdinal} has not been materialized by a business flush.");
            }

            if (slot.SemanticApplied)
            {
                throw new InvalidOperationException(
                    $"Audit mutation {mutationOrdinal} already has semantic audit data.");
            }

            slot.SemanticApplied = true;
            return new AuditResolution(AuditWrite.Enrich, slot.Row, slot.Ordinal);
        }
    }

    public IReadOnlyList<AuditMutationDescriptor> GetMutationsAfter(long mutationOrdinal)
    {
        lock (_lock)
        {
            EnsureActive();
            return _slots
                .Where(slot => slot.Ordinal > mutationOrdinal
                    && slot.GenericCaptured
                    && slot.EntityReference is not null)
                .OrderBy(slot => slot.Ordinal)
                .Select(slot => new AuditMutationDescriptor(
                    slot.Ordinal,
                    slot.EntityReference!,
                    slot.EntityType,
                    slot.EntityId,
                    slot.Operation))
                .ToArray();
        }
    }

    public void StageExplicit(long mutationOrdinal, AuditLog row)
    {
        ArgumentNullException.ThrowIfNull(row);
        lock (_lock)
        {
            EnsureActive();
            EnsureRowsOpen();
            var slot = FindSlot(mutationOrdinal);
            if (slot.Row is not null)
            {
                throw new InvalidOperationException(
                    $"Audit mutation {mutationOrdinal} already has a staged row.");
            }

            Stamp(row, mutationOrdinal);
            slot.Row = row;
            _stagedRows.Add(row);
        }
    }

    public IReadOnlyList<AuditLog> TakeStagedRows()
    {
        lock (_lock)
        {
            EnsureActive();
            EnsureRowsOpen();
            _rowsTaken = true;
            return _stagedRows.ToArray();
        }
    }

    public void AuthorizeSetBasedMutation(string entityType)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(entityType);
        lock (_lock)
        {
            EnsureActive();
            _setBasedPermits.TryGetValue(entityType, out var count);
            _setBasedPermits[entityType] = count + 1;
        }
    }

    public bool TryConsumeSetBasedMutation(string entityType)
    {
        lock (_lock)
        {
            if (_command is null
                || !_setBasedPermits.TryGetValue(entityType, out var count)
                || count == 0)
            {
                return false;
            }

            if (count == 1)
            {
                _setBasedPermits.Remove(entityType);
            }
            else
            {
                _setBasedPermits[entityType] = count - 1;
            }

            return true;
        }
    }

    private MutationSlot FindSlot(long ordinal) =>
        _slots.SingleOrDefault(slot => slot.Ordinal == ordinal)
        ?? throw new InvalidOperationException($"Unknown audit mutation ordinal {ordinal}.");

    private void Stamp(AuditLog row, long ordinal)
    {
        row.CommandType = Command.CommandType;
        row.CommandIdempotencyKey = Command.IdempotencyKey;
        row.CommandAttemptId = _attemptId;
        row.MutationOrdinal = ordinal;
    }

    private void EnsureActive()
    {
        if (_command is null)
        {
            throw new InvalidOperationException(
                "Audit rows can only be staged inside IAtomicUnitOfWork.");
        }
    }

    private void EnsureRowsOpen()
    {
        if (_rowsTaken)
        {
            throw new InvalidOperationException("The final audit batch has already been materialized.");
        }
    }

    private void EndAttempt()
    {
        lock (_lock)
        {
            _command = null;
            _attemptId = Guid.Empty;
            _slots.Clear();
            _stagedRows.Clear();
            _setBasedPermits.Clear();
            _rowsTaken = false;
        }
    }

    private sealed class AttemptLease(AuditScope owner) : IDisposable
    {
        private AuditScope? _owner = owner;

        public void Dispose() => Interlocked.Exchange(ref _owner, null)?.EndAttempt();
    }
}
