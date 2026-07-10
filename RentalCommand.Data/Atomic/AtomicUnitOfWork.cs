using System.Reflection;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using RentalCommand.Core.Atomic;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;

namespace RentalCommand.Data.Atomic;

/// <summary>
/// Retry-safe operation kernel. The execution strategy owns the complete explicit transaction and
/// every physical attempt gets a fresh DI scope, handler, DbContext, audit scope, and tracker.
/// </summary>
public sealed class AtomicUnitOfWork : IAtomicUnitOfWork
{
    private static readonly AsyncLocal<AmbientAttempt?> Ambient = new();
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly TimeProvider _timeProvider;

    public AtomicUnitOfWork(IServiceScopeFactory scopeFactory, TimeProvider timeProvider)
    {
        _scopeFactory = scopeFactory;
        _timeProvider = timeProvider;
    }

    public async Task<AtomicCommandOutcome<TResult>> ExecuteAsync<TCommand, TResult>(
        AtomicCommandIdentity identity,
        TCommand command,
        IAtomicResultCodec<TResult> resultCodec,
        CancellationToken ct = default)
        where TCommand : notnull
        where TResult : notnull
    {
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(command);
        ValidateCodec(resultCodec);

        if (Ambient.Value is { } owner)
        {
            var joinedHandler = owner.Services.GetRequiredService<IAtomicCommandHandler<TCommand, TResult>>();
            ValidateHandler(joinedHandler);
            var joinedValue = await joinedHandler.HandleAsync(command, owner.Attempt, ct);
            return new AtomicCommandOutcome<TResult>(
                joinedValue,
                AtomicCommandDisposition.Joined,
                owner.Attempt.AttemptId);
        }

        await using var coordinatorScope = _scopeFactory.CreateAsyncScope();
        var coordinator = coordinatorScope.ServiceProvider.GetRequiredService<RentalCommandDbContext>();
        var strategy = coordinator.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(
            () => ExecuteAttemptAsync(identity, command, resultCodec, ct));
    }

    private async Task<AtomicCommandOutcome<TResult>> ExecuteAttemptAsync<TCommand, TResult>(
        AtomicCommandIdentity identity,
        TCommand command,
        IAtomicResultCodec<TResult> resultCodec,
        CancellationToken ct)
        where TCommand : notnull
        where TResult : notnull
    {
        await using var attemptScope = _scopeFactory.CreateAsyncScope();
        var services = attemptScope.ServiceProvider;
        var db = services.GetRequiredService<RentalCommandDbContext>();
        var auditScope = services.GetRequiredService<AtomicAuditScope>();
        var attemptId = Guid.NewGuid();
        var attempt = new AtomicWriteAttempt(attemptId, db, auditScope, _timeProvider);
        using var auditLease = auditScope.BeginAttempt(identity, attemptId);
        IDbContextTransaction? transaction = null;

        try
        {
            using (auditScope.BeginExecutorTransactionLifecycle())
            {
                transaction = await db.Database.BeginTransactionAsync(ct);
            }

            var startedAt = _timeProvider.GetUtcNow().UtcDateTime;
            var claimed = await db.Database.ExecuteSqlInterpolatedAsync($$"""
                INSERT INTO "AtomicCommandReceipts"
                    ("Id", "AttemptId", "CommandType", "IdempotencyKey", "Status", "ResultContract", "StartedAt")
                VALUES
                    ({{Guid.NewGuid()}}, {{attemptId}}, {{identity.CommandType}}, {{identity.IdempotencyKey}},
                     {{(int)AtomicCommandReceiptStatus.Pending}}, {{resultCodec.ContractName}}, {{startedAt}})
                ON CONFLICT ("CommandType", "IdempotencyKey") DO NOTHING
                """, ct);

            var receipt = await db.AtomicCommandReceipts.SingleAsync(
                row => row.CommandType == identity.CommandType
                    && row.IdempotencyKey == identity.IdempotencyKey,
                ct);
            if (claimed == 0)
            {
                var replay = DeserializeReplay(receipt, resultCodec);
                using (auditScope.BeginExecutorTransactionLifecycle())
                {
                    await transaction.CommitAsync(ct);
                }

                return new AtomicCommandOutcome<TResult>(
                    replay,
                    AtomicCommandDisposition.Replayed,
                    receipt.AttemptId);
            }

            var handler = services.GetRequiredService<IAtomicCommandHandler<TCommand, TResult>>();
            ValidateHandler(handler);
            var priorAmbient = Ambient.Value;
            Ambient.Value = new AmbientAttempt(services, attempt);
            TResult value;
            try
            {
                value = await handler.HandleAsync(command, attempt, ct);
            }
            finally
            {
                Ambient.Value = priorAmbient;
            }

            await attempt.FlushBusinessAsync(ct);
            var resultJson = resultCodec.Serialize(value);
            using (JsonDocument.Parse(resultJson))
            {
                // A receipt codec must persist valid JSON for the PostgreSQL jsonb column.
            }

            receipt.ResultJson = resultJson;
            receipt.Status = AtomicCommandReceiptStatus.Completed;
            receipt.CompletedAt = _timeProvider.GetUtcNow().UtcDateTime;
            var auditRows = auditScope.TakeRows();
            if (auditRows.Count > 0)
            {
                db.AtomicAuditLogs.AddRange(auditRows);
            }

            attempt.MaterializeOutbox();
            await db.SaveChangesAsync(ct);
            using (auditScope.BeginExecutorTransactionLifecycle())
            {
                await transaction.CommitAsync(ct);
            }

            return new AtomicCommandOutcome<TResult>(
                value,
                AtomicCommandDisposition.Executed,
                attemptId);
        }
        catch
        {
            if (transaction is not null && db.Database.CurrentTransaction is not null)
            {
                try
                {
                    using (auditScope.BeginExecutorTransactionLifecycle())
                    {
                        await transaction.RollbackAsync(CancellationToken.None);
                    }
                }
                catch
                {
                    // Preserve the command failure. Transaction disposal still releases resources.
                }
            }

            throw;
        }
        finally
        {
            if (transaction is not null)
            {
                await transaction.DisposeAsync();
            }
        }
    }

    private static void ValidateCodec<TResult>(IAtomicResultCodec<TResult> codec)
        where TResult : notnull
    {
        ArgumentNullException.ThrowIfNull(codec);
        ArgumentException.ThrowIfNullOrWhiteSpace(codec.ContractName);
        if (codec.ContractName.Length > 200)
        {
            throw new ArgumentOutOfRangeException(
                nameof(codec),
                "Result contract cannot exceed 200 characters.");
        }
    }

    private static void ValidateHandler<TCommand, TResult>(
        IAtomicCommandHandler<TCommand, TResult> handler)
        where TCommand : notnull
        where TResult : notnull
    {
        var forbidden = handler.GetType()
            .GetConstructors(BindingFlags.Public | BindingFlags.Instance)
            .SelectMany(constructor => constructor.GetParameters())
            .FirstOrDefault(parameter => typeof(IAtomicRemoteDependency).IsAssignableFrom(parameter.ParameterType));
        if (forbidden is not null)
        {
            throw new AtomicArchitectureException(
                $"Atomic handler {handler.GetType().Name} directly depends on remote dependency " +
                $"{forbidden.ParameterType.Name}. Stage durable outbox intent instead.");
        }
    }

    private static TResult DeserializeReplay<TResult>(
        AtomicCommandReceipt receipt,
        IAtomicResultCodec<TResult> resultCodec)
        where TResult : notnull
    {
        if (receipt.Status != AtomicCommandReceiptStatus.Completed || receipt.CompletedAt is null)
        {
            throw new AtomicReceiptInvariantException(
                $"Visible receipt {receipt.CommandType}/{receipt.IdempotencyKey} is not completed.");
        }

        if (!string.Equals(receipt.ResultContract, resultCodec.ContractName, StringComparison.Ordinal))
        {
            throw new AtomicReceiptInvariantException(
                $"Receipt {receipt.CommandType}/{receipt.IdempotencyKey} uses result contract " +
                $"'{receipt.ResultContract}', not '{resultCodec.ContractName}'.");
        }

        if (string.IsNullOrWhiteSpace(receipt.ResultJson))
        {
            throw new AtomicReceiptInvariantException(
                $"Completed receipt {receipt.CommandType}/{receipt.IdempotencyKey} has no result JSON.");
        }

        return resultCodec.Deserialize(receipt.ResultJson);
    }

    private sealed record AmbientAttempt(
        IServiceProvider Services,
        AtomicWriteAttempt Attempt);

    private sealed class AtomicWriteAttempt : IAtomicWriteAttempt
    {
        private readonly RentalCommandDbContext _db;
        private readonly AtomicAuditScope _auditScope;
        private readonly TimeProvider _timeProvider;
        private readonly List<OutboxMessage> _outbox = [];
        private bool _outboxMaterialized;

        public AtomicWriteAttempt(
            Guid attemptId,
            RentalCommandDbContext db,
            AtomicAuditScope auditScope,
            TimeProvider timeProvider)
        {
            AttemptId = attemptId;
            _db = db;
            _auditScope = auditScope;
            _timeProvider = timeProvider;
        }

        public Guid AttemptId { get; }
        public Guid AuditScopeId => _auditScope.ScopeId;

        public async Task<AtomicBusinessFlush> FlushBusinessAsync(CancellationToken ct = default)
        {
            var priorOrdinal = _auditScope.CurrentMutationOrdinal;
            var rows = await _db.SaveChangesAsync(ct);
            return new AtomicBusinessFlush(rows, _auditScope.GetMutationsAfter(priorOrdinal));
        }

        public void BindSemanticAudit(object entityReference, AtomicSemanticAudit audit)
        {
            ArgumentNullException.ThrowIfNull(entityReference);
            _auditScope.BindSemantic(entityReference, _db.Entry(entityReference), audit);
        }

        public void EnrichMutation(AtomicAuditMutation mutation, AtomicSemanticAudit audit) =>
            _auditScope.Enrich(mutation, audit);

        public void StageSemanticEvent(AtomicSemanticAudit audit) =>
            _auditScope.StageSemanticEvent(audit, _timeProvider.GetUtcNow().UtcDateTime);

        public void StageOutbox(OutboxMessage message)
        {
            ArgumentNullException.ThrowIfNull(message);
            if (_outboxMaterialized)
            {
                throw new InvalidOperationException("The final outbox batch has already been materialized.");
            }

            if (message.CreatedAt == default)
            {
                message.CreatedAt = _timeProvider.GetUtcNow().UtcDateTime;
            }

            _outbox.Add(message);
        }

        public void MaterializeOutbox()
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
}
