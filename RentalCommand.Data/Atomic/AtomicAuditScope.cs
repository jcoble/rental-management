using Microsoft.EntityFrameworkCore.ChangeTracking;
using RentalCommand.Core.Atomic;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;

namespace RentalCommand.Data.Atomic;

internal sealed class AtomicAuditScope : IAtomicExecutionState, IAtomicInfrastructureWriteGate
{
    private readonly AtomicPersistenceMode _mode;

    public AtomicAuditScope(AtomicPersistenceMode mode) => _mode = mode;

    private sealed class MutationSlot
    {
        public required AtomicAuditMutation Mutation { get; init; }
        public required AtomicAuditLog Row { get; init; }
    }

    private readonly object _gate = new();
    private readonly Dictionary<object, AtomicSemanticAudit> _boundSemantic =
        new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<long, MutationSlot> _mutations = [];
    private readonly List<AtomicAuditLog> _rows = [];
    private AtomicCommandIdentity? _command;
    private Guid _attemptId;
    private long _ordinal;
    private int _transactionLifecycleDepth;
    private AtomicSetBasedTarget? _activeSetBasedTarget;
    private IReadOnlyList<AtomicRawDmlPermit>? _activeRawDmlPermits;

    public Guid ScopeId { get; } = Guid.NewGuid();
    public bool IsActive => _command is not null;
    public bool AllowsUnconvertedWrites => _mode.AllowUnconvertedWrites;
    public long CurrentMutationOrdinal => _ordinal;

    public IDisposable BeginAttempt(AtomicCommandIdentity command, Guid attemptId)
    {
        ArgumentNullException.ThrowIfNull(command);
        lock (_gate)
        {
            if (_command is not null)
            {
                throw new InvalidOperationException("This atomic audit scope already owns an attempt.");
            }
            if (_activeRawDmlPermits is not null)
            {
                throw new AtomicArchitectureException(
                    "An atomic attempt cannot begin while an infrastructure raw-DML lease is active.");
            }

            _command = command;
            _attemptId = attemptId;
        }

        return new Lease(() =>
        {
            lock (_gate)
            {
                _command = null;
                _attemptId = default;
                _transactionLifecycleDepth = 0;
                _activeSetBasedTarget = null;
                _activeRawDmlPermits = null;
                _boundSemantic.Clear();
                _mutations.Clear();
                _rows.Clear();
            }
        });
    }

    public IDisposable BeginExecutorTransactionLifecycle()
    {
        RequireActive();
        lock (_gate)
        {
            _transactionLifecycleDepth++;
        }

        return new Lease(() =>
        {
            lock (_gate)
            {
                _transactionLifecycleDepth--;
            }
        });
    }

    public void GuardTransactionLifecycle(string operation)
    {
        if (IsActive && _transactionLifecycleDepth == 0)
        {
            throw new AtomicArchitectureException(
                $"Atomic command handlers cannot {operation} transactions; the atomic executor owns the transaction lifecycle.");
        }
    }

    public IDisposable BeginInternalRawDml(string tableName, AtomicRawDmlOperation operation)
        => BeginInternalRawDmlBatch(new AtomicRawDmlTarget(tableName, operation));

    public IDisposable BeginInternalRawDmlBatch(params AtomicRawDmlTarget[] targets)
    {
        RequireActive();
        if (targets.Length == 0 || targets.Any(target => string.IsNullOrWhiteSpace(target.TableName)))
        {
            throw new ArgumentException("At least one exact raw-DML target is required.", nameof(targets));
        }
        lock (_gate)
        {
            if (_activeRawDmlPermits is not null)
            {
                throw new InvalidOperationException("An internal raw DML command is already executing.");
            }

            _activeRawDmlPermits = targets
                .Distinct()
                .Select(target => new AtomicRawDmlPermit(
                    target.TableName, target.Operation, AllowWithoutAttempt: false))
                .ToArray();
        }

        return new Lease(() =>
        {
            lock (_gate)
            {
                _activeRawDmlPermits = null;
            }
        });
    }

    /// <summary>
    /// Leases one exact kernel-owned infrastructure statement that must run before an atomic
    /// business attempt can begin. This is for durable admission/claim metadata only; it cannot
    /// overlap an active command and never permits arbitrary caller-authored DML.
    /// </summary>
    private IDisposable BeginInfrastructureRawDml(string tableName, AtomicRawDmlOperation operation)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tableName);
        lock (_gate)
        {
            if (_command is not null)
            {
                throw new AtomicArchitectureException(
                    "Infrastructure raw DML cannot execute inside an atomic business attempt.");
            }
            if (_activeRawDmlPermits is not null)
            {
                throw new InvalidOperationException("An internal raw DML command is already executing.");
            }

            _activeRawDmlPermits =
            [
                new AtomicRawDmlPermit(tableName, operation, AllowWithoutAttempt: true),
            ];
        }

        return new Lease(() =>
        {
            lock (_gate)
            {
                _activeRawDmlPermits = null;
            }
        });
    }

    IDisposable IAtomicInfrastructureWriteGate.BeginPendingFileUploadAdmission() =>
        BeginInfrastructureRawDml("PendingFileUploads", AtomicRawDmlOperation.Insert);

    public void GuardRawDml(
        string tableName,
        AtomicRawDmlOperation operation)
    {
        lock (_gate)
        {
            var permit = _activeRawDmlPermits?.SingleOrDefault(candidate =>
                candidate.TableName == tableName && candidate.Operation == operation);
            if (permit is null
                || (_command is null && !permit.AllowWithoutAttempt)
                || (_command is not null && permit.AllowWithoutAttempt))
            {
                throw new AtomicArchitectureException(
                    $"Raw {operation} DML is forbidden without an exact internal atomic mutation lease.");
            }
        }
    }

    public void BindSemantic(
        object entityReference,
        EntityEntry entry,
        AtomicSemanticAudit audit)
    {
        RequireActive();
        ValidateSemanticAgainstEntry(entry, audit);
        lock (_gate)
        {
            if (!_boundSemantic.TryAdd(entityReference, audit))
            {
                throw new InvalidOperationException(
                    "Semantic audit detail is already bound to this exact tracked object.");
            }
        }
    }

    public AtomicAuditMutation StageTrackedMutation(
        object entityReference,
        string entityType,
        int entityId,
        int portfolioId,
        AuditLogOperation operation,
        string? oldValues,
        string? newValues,
        int? actorUserId,
        string? actorLabel,
        string? ipAddress,
        DateTime timestamp)
    {
        RequireActive();
        lock (_gate)
        {
            var ordinal = ++_ordinal;
            var mutation = new AtomicAuditMutation(
                _attemptId,
                ordinal,
                entityReference,
                entityType,
                entityId,
                operation);
            var row = NewRow(
                ordinal,
                portfolioId,
                entityType,
                entityId,
                operation,
                actorUserId,
                actorLabel,
                oldValues,
                newValues,
                null,
                ipAddress,
                timestamp);

            if (_boundSemantic.Remove(entityReference, out var semantic))
            {
                if (operation == AuditLogOperation.Created && semantic.EntityId == 0)
                {
                    if (entityId <= 0)
                    {
                        throw new InvalidOperationException(
                            "A created semantic audit requires a generated positive entity id after SaveChanges.");
                    }

                    semantic = semantic with { EntityId = entityId };
                }

                ValidateSemantic(mutation, semantic);
                ApplySemantic(row, semantic);
            }

            _mutations.Add(ordinal, new MutationSlot { Mutation = mutation, Row = row });
            _rows.Add(row);
            return mutation;
        }
    }

    public IReadOnlyList<AtomicAuditMutation> GetMutationsAfter(long ordinal)
    {
        lock (_gate)
        {
            return _mutations.Values
                .Where(slot => slot.Mutation.MutationOrdinal > ordinal)
                .OrderBy(slot => slot.Mutation.MutationOrdinal)
                .Select(slot => slot.Mutation)
                .ToArray();
        }
    }

    public void Enrich(AtomicAuditMutation mutation, AtomicSemanticAudit audit)
    {
        RequireActive();
        lock (_gate)
        {
            if (mutation.AttemptId != _attemptId
                || !_mutations.TryGetValue(mutation.MutationOrdinal, out var slot)
                || !ReferenceEquals(slot.Mutation.EntityReference, mutation.EntityReference)
                || slot.Mutation != mutation)
            {
                throw new ArgumentException(
                    "Mutation descriptor does not belong to this exact physical attempt and object.",
                    nameof(mutation));
            }

            ValidateSemantic(mutation, audit);
            ApplySemantic(slot.Row, audit);
        }
    }

    public void StageSemanticEvent(AtomicSemanticAudit audit, DateTime timestamp)
    {
        RequireActive();
        ValidateSemanticShape(audit);
        lock (_gate)
        {
            var ordinal = ++_ordinal;
            var row = NewRow(
                ordinal,
                audit.PortfolioId,
                audit.EntityType,
                audit.EntityId,
                audit.Operation,
                audit.UserId,
                audit.ActorLabel,
                audit.OldValues,
                audit.NewValues,
                audit.ChangeReason,
                audit.IpAddress,
                timestamp);
            _rows.Add(row);
        }
    }

    public IDisposable BeginSetBasedMutation(AtomicSetBasedTarget target, AtomicSemanticAudit audit)
    {
        RequireActive();
        ValidateSetBased(target, audit);
        lock (_gate)
        {
            if (_activeSetBasedTarget is not null)
            {
                throw new InvalidOperationException("A set-based mutation is already executing in this attempt.");
            }
            _activeSetBasedTarget = target;
        }

        return new Lease(() =>
        {
            lock (_gate)
            {
                _activeSetBasedTarget = null;
            }
        });
    }

    public void CompleteSetBasedMutation(
        AtomicSetBasedTarget target,
        AtomicSemanticAudit audit,
        DateTime timestamp)
    {
        RequireActive();
        ValidateSetBased(target, audit);
        lock (_gate)
        {
            if (_activeSetBasedTarget != target)
            {
                throw new AtomicArchitectureException(
                    "The exact set-based mutation lease is not active for this target.");
            }

            var ordinal = ++_ordinal;
            _rows.Add(NewRow(
                ordinal,
                audit.PortfolioId,
                audit.EntityType,
                audit.EntityId,
                audit.Operation,
                audit.UserId,
                audit.ActorLabel,
                audit.OldValues,
                audit.NewValues,
                audit.ChangeReason,
                audit.IpAddress,
                timestamp));
        }
    }

    public void GuardSetBasedCommand(Type entityType, AuditLogOperation operation)
    {
        RequireActive();
        lock (_gate)
        {
            if (_activeSetBasedTarget is null
                || _activeSetBasedTarget.EntityClrType != entityType
                || _activeSetBasedTarget.Operation != operation)
            {
                throw new AtomicArchitectureException(
                    $"Set-based {operation} for {entityType.Name} must use the exact atomic set-based executor target.");
            }
        }
    }

    public IReadOnlyList<AtomicAuditLog> TakeRows()
    {
        lock (_gate)
        {
            if (_boundSemantic.Count != 0)
            {
                throw new InvalidOperationException(
                    "One or more semantic audits were bound to objects that were never flushed.");
            }

            var result = _rows.ToArray();
            _rows.Clear();
            return result;
        }
    }

    private AtomicAuditLog NewRow(
        long ordinal,
        int portfolioId,
        string entityType,
        int entityId,
        AuditLogOperation operation,
        int? userId,
        string? actorLabel,
        string? oldValues,
        string? newValues,
        string? changeReason,
        string? ipAddress,
        DateTime timestamp) => new()
        {
            AttemptId = _attemptId,
            CommandType = _command!.CommandType,
            CommandIdempotencyKey = _command.IdempotencyKey,
            MutationOrdinal = ordinal,
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
            Timestamp = timestamp,
        };

    private static void ValidateSemanticAgainstEntry(EntityEntry entry, AtomicSemanticAudit audit)
    {
        ValidateSemanticShape(audit);
        var entityType = entry.Entity.GetType().Name;
        var portfolioId = Convert.ToInt32(
            entry.Property(nameof(RentalCommand.Core.Interfaces.IPortfolioScoped.PortfolioId)).CurrentValue);
        var id = entry.Metadata.FindPrimaryKey()?.Properties.Count == 1
            ? Convert.ToInt32(entry.Property(entry.Metadata.FindPrimaryKey()!.Properties[0].Name).CurrentValue)
            : 0;
        var operation = entry.State switch
        {
            Microsoft.EntityFrameworkCore.EntityState.Added => AuditLogOperation.Created,
            Microsoft.EntityFrameworkCore.EntityState.Modified => AuditLogOperation.Updated,
            Microsoft.EntityFrameworkCore.EntityState.Deleted => AuditLogOperation.Deleted,
            _ => throw new ArgumentException("The exact tracked object has no pending auditable mutation."),
        };

        var unresolvedGeneratedId = operation == AuditLogOperation.Created
            && audit.EntityId == 0
            && id <= 0;
        if (!string.Equals(entityType, audit.EntityType, StringComparison.Ordinal)
            || portfolioId != audit.PortfolioId
            || (!unresolvedGeneratedId && id != audit.EntityId)
            || operation != audit.Operation)
        {
            throw new ArgumentException("Semantic audit does not match the exact pending tracked mutation.");
        }
    }

    private static void ValidateSemantic(AtomicAuditMutation mutation, AtomicSemanticAudit audit)
    {
        ValidateSemanticShape(audit);
        if (!string.Equals(mutation.EntityType, audit.EntityType, StringComparison.Ordinal)
            || mutation.EntityId != audit.EntityId
            || mutation.Operation != audit.Operation)
        {
            throw new ArgumentException("Semantic audit does not match the exact mutation descriptor.");
        }
    }

    private static void ValidateSetBased(AtomicSetBasedTarget target, AtomicSemanticAudit audit)
    {
        ValidateSemanticShape(audit);
        if (!string.Equals(target.EntityClrType.Name, audit.EntityType, StringComparison.Ordinal)
            || target.PortfolioId != audit.PortfolioId
            || target.EntityId != audit.EntityId
            || target.Operation != audit.Operation)
        {
            throw new ArgumentException("Semantic audit does not match the exact set-based target.");
        }
    }

    private static void ValidateSemanticShape(AtomicSemanticAudit audit)
    {
        ArgumentNullException.ThrowIfNull(audit);
        ArgumentException.ThrowIfNullOrWhiteSpace(audit.EntityType);
        if (audit.PortfolioId <= 0 || audit.EntityId < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(audit), "Audit identity values are invalid.");
        }
    }

    private static void ApplySemantic(AtomicAuditLog row, AtomicSemanticAudit audit)
    {
        row.UserId = audit.UserId ?? row.UserId;
        row.ActorLabel = audit.ActorLabel ?? row.ActorLabel;
        row.OldValues = audit.OldValues ?? row.OldValues;
        row.NewValues = audit.NewValues ?? row.NewValues;
        row.ChangeReason = audit.ChangeReason ?? row.ChangeReason;
        row.IpAddress = audit.IpAddress ?? row.IpAddress;
    }

    private void RequireActive()
    {
        if (!IsActive)
        {
            throw new AtomicArchitectureException(
                "The opt-in atomic persistence scope is not active for this operation.");
        }
    }

    private sealed class Lease(Action dispose) : IDisposable
    {
        private Action? _dispose = dispose;
        public void Dispose() => Interlocked.Exchange(ref _dispose, null)?.Invoke();
    }
}

internal sealed record AtomicSetBasedTarget(
    Type EntityClrType,
    int PortfolioId,
    int EntityId,
    AuditLogOperation Operation);

internal sealed record AtomicRawDmlPermit(
    string TableName,
    AtomicRawDmlOperation Operation,
    bool AllowWithoutAttempt);

internal sealed record AtomicRawDmlTarget(
    string TableName,
    AtomicRawDmlOperation Operation);

internal enum AtomicRawDmlOperation
{
    Insert,
    Update,
    Delete,
}
