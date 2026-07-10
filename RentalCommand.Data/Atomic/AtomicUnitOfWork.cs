using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using RentalCommand.Core.Atomic;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Interfaces;

namespace RentalCommand.Data.Atomic;

/// <summary>
/// Retry-safe operation kernel. The EF execution strategy owns the complete explicit transaction;
/// every strategy attempt creates a fresh DI scope, DbContext, audit scope, and change tracker.
/// </summary>
public sealed class AtomicUnitOfWork : IAtomicUnitOfWork
{
    private static readonly AsyncLocal<AtomicWriteAttempt?> AmbientAttempt = new();
    private static readonly AtomicJsonResultCodec<AtomicAuditCommandResult> AuditOnlyCodec =
        new("atomic-audit-command-result.v1");

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly TimeProvider _timeProvider;

    public AtomicUnitOfWork(IServiceScopeFactory scopeFactory, TimeProvider timeProvider)
    {
        _scopeFactory = scopeFactory;
        _timeProvider = timeProvider;
    }

    public async Task<AtomicCommandOutcome<TResult>> ExecuteAsync<TResult>(
        AtomicCommandIdentity command,
        IAtomicResultCodec<TResult> resultCodec,
        Func<IAtomicWriteAttempt, CancellationToken, Task<TResult>> dbOnlyCallback,
        CancellationToken ct = default)
        where TResult : notnull
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(resultCodec);
        ArgumentNullException.ThrowIfNull(dbOnlyCallback);
        ArgumentException.ThrowIfNullOrWhiteSpace(resultCodec.ContractName);
        if (resultCodec.ContractName.Length > 200)
        {
            throw new ArgumentOutOfRangeException(
                nameof(resultCodec),
                "Result contract cannot exceed 200 characters.");
        }

        var ownerAttempt = AmbientAttempt.Value;
        if (ownerAttempt is not null)
        {
            // Nested code has no receipt/transaction/retry ownership. The outer receipt makes the
            // entire joined callback idempotent and the outer transaction decides commit/rollback.
            var joinedValue = await dbOnlyCallback(ownerAttempt, ct);
            return new AtomicCommandOutcome<TResult>(
                joinedValue,
                AtomicCommandDisposition.Joined,
                ownerAttempt.AttemptId);
        }

        // This context is only the execution-strategy coordinator. Physical mutation attempts below
        // each resolve a different scope and context, so a rolled-back tracker is never retried.
        await using var coordinatorScope = _scopeFactory.CreateAsyncScope();
        var coordinatorDb = coordinatorScope.ServiceProvider.GetRequiredService<RentalCommandDbContext>();
        var strategy = coordinatorDb.Database.CreateExecutionStrategy();

        return await strategy.ExecuteAsync(
            () => ExecuteAttemptAsync(command, resultCodec, dbOnlyCallback, ct));
    }

    public Task<AtomicCommandOutcome<AtomicAuditCommandResult>> ExecuteAuditOnlyAsync(
        AtomicCommandIdentity command,
        AtomicAuditEntry audit,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(audit);
        return ExecuteAsync(
            command,
            AuditOnlyCodec,
            async (attempt, token) =>
            {
                await StageAuditAsync(attempt.AuditTrail, audit, token);
                return new AtomicAuditCommandResult(command.CommandType, command.IdempotencyKey);
            },
            ct);
    }

    private async Task<AtomicCommandOutcome<TResult>> ExecuteAttemptAsync<TResult>(
        AtomicCommandIdentity command,
        IAtomicResultCodec<TResult> resultCodec,
        Func<IAtomicWriteAttempt, CancellationToken, Task<TResult>> dbOnlyCallback,
        CancellationToken ct)
        where TResult : notnull
    {
        await using var attemptScope = _scopeFactory.CreateAsyncScope();
        var services = attemptScope.ServiceProvider;
        var db = services.GetRequiredService<RentalCommandDbContext>();
        var auditScope = services.GetRequiredService<IAuditScope>();
        var auditTrail = services.GetRequiredService<IAuditTrailService>();
        var attemptId = Guid.NewGuid();
        var startedAt = _timeProvider.GetUtcNow().UtcDateTime;

        using var auditLease = auditScope.BeginAttempt(command, attemptId);
        var attempt = new AtomicWriteAttempt(attemptId, db, auditTrail, auditScope, _timeProvider);
        await using var transaction = await db.Database.BeginTransactionAsync(ct);

        // ON CONFLICT handles concurrent duplicate callers without using an exception as control flow.
        // The loser waits for the owner transaction and then reads the completed receipt DB-side.
        var claimed = await db.Database.ExecuteSqlInterpolatedAsync($$"""
            INSERT INTO "AtomicCommandReceipts"
                ("Id", "AttemptId", "CommandType", "IdempotencyKey", "Status", "ResultContract", "StartedAt")
            VALUES
                ({{Guid.NewGuid()}}, {{attemptId}}, {{command.CommandType}}, {{command.IdempotencyKey}},
                 {{(int)AtomicCommandReceiptStatus.Pending}}, {{resultCodec.ContractName}}, {{startedAt}})
            ON CONFLICT ("CommandType", "IdempotencyKey") DO NOTHING
            """, ct);

        var receipt = await db.AtomicCommandReceipts.SingleAsync(
            row => row.CommandType == command.CommandType
                && row.IdempotencyKey == command.IdempotencyKey,
            ct);

        if (claimed == 0)
        {
            var replay = DeserializeReplay(receipt, resultCodec);
            await transaction.CommitAsync(ct);
            return new AtomicCommandOutcome<TResult>(
                replay,
                AtomicCommandDisposition.Replayed,
                receipt.AttemptId);
        }

        var priorAmbient = AmbientAttempt.Value;
        AmbientAttempt.Value = attempt;
        TResult value;
        try
        {
            value = await dbOnlyCallback(attempt, ct);
        }
        finally
        {
            AmbientAttempt.Value = priorAmbient;
        }

        // Flush all remaining business mutations first. The audit interceptor only stages rows in
        // AuditScope, so no audit is made durable by this generated-id flush.
        await attempt.FlushBusinessAsync(ct);

        var resultJson = resultCodec.Serialize(value);
        using (JsonDocument.Parse(resultJson))
        {
            // Validate that custom codecs really persisted JSON before assigning the jsonb column.
        }

        receipt.ResultJson = resultJson;
        receipt.Status = AtomicCommandReceiptStatus.Completed;
        receipt.CompletedAt = _timeProvider.GetUtcNow().UtcDateTime;

        var auditRows = auditScope.TakeStagedRows();
        if (auditRows.Count > 0)
        {
            db.AuditLogs.AddRange(auditRows);
        }

        attempt.MaterializeOutbox();

        // One final flush writes audit/outbox/receipt companions. Any failure rolls the preceding
        // generated-id business flushes back with this explicit transaction.
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);

        return new AtomicCommandOutcome<TResult>(
            value,
            AtomicCommandDisposition.Executed,
            attemptId);
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

    private static Task StageAuditAsync(
        IAuditTrailService auditTrail,
        AtomicAuditEntry audit,
        CancellationToken ct) =>
        auditTrail.LogAsync(
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
            ct);

    private sealed class AtomicWriteAttempt : IAtomicWriteAttempt
    {
        private readonly IAuditScope _auditScope;
        private readonly TimeProvider _timeProvider;
        private readonly List<OutboxMessage> _outbox = [];
        private bool _outboxMaterialized;

        public AtomicWriteAttempt(
            Guid attemptId,
            RentalCommandDbContext dbContext,
            IAuditTrailService auditTrail,
            IAuditScope auditScope,
            TimeProvider timeProvider)
        {
            AttemptId = attemptId;
            DbContext = dbContext;
            AuditTrail = auditTrail;
            _auditScope = auditScope;
            _timeProvider = timeProvider;
        }

        public Guid AttemptId { get; }
        public RentalCommandDbContext DbContext { get; }
        public IAuditTrailService AuditTrail { get; }

        public async Task<AtomicBusinessFlush> FlushBusinessAsync(CancellationToken ct = default)
        {
            var priorOrdinal = _auditScope.CurrentMutationOrdinal;
            var rows = await DbContext.SaveChangesAsync(ct);
            return new AtomicBusinessFlush(rows, _auditScope.GetMutationsAfter(priorOrdinal));
        }

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

        public async Task StageSetBasedAuditAsync(
            string entityType,
            AtomicAuditEntry audit,
            CancellationToken ct = default)
        {
            if (!string.Equals(entityType, audit.EntityType, StringComparison.Ordinal))
            {
                throw new ArgumentException(
                    "Set-based permit entity type must match the semantic audit entity type.",
                    nameof(entityType));
            }

            await StageAuditAsync(AuditTrail, audit, ct);
            _auditScope.AuthorizeSetBasedMutation(entityType);
        }

        public Task StageExactAuditAsync(
            AuditMutationDescriptor mutation,
            AtomicAuditEntry audit,
            CancellationToken ct = default)
        {
            ArgumentNullException.ThrowIfNull(mutation);
            ArgumentNullException.ThrowIfNull(audit);
            ct.ThrowIfCancellationRequested();
            if (!ReferenceEquals(mutation.EntityReference, FindTrackedEntity(mutation.EntityReference))
                || mutation.EntityType != audit.EntityType
                || mutation.EntityId != audit.EntityId
                || mutation.Operation != audit.Operation)
            {
                throw new ArgumentException(
                    "Semantic audit does not match the exact tracked mutation handle.",
                    nameof(audit));
            }

            var resolution = _auditScope.ResolveExplicit(mutation.MutationOrdinal);
            var existing = resolution.ExistingRow
                ?? throw new InvalidOperationException(
                    $"Audit mutation {mutation.MutationOrdinal} has no generic row to enrich.");
            if (audit.OldValues is not null) existing.OldValues = audit.OldValues;
            if (audit.NewValues is not null) existing.NewValues = audit.NewValues;
            if (audit.ChangeReason is not null) existing.ChangeReason = audit.ChangeReason;
            if (audit.UserId.HasValue) existing.UserId = audit.UserId;
            if (audit.ActorLabel is not null) existing.ActorLabel = audit.ActorLabel;
            if (audit.IpAddress is not null) existing.IpAddress = audit.IpAddress;
            return Task.CompletedTask;
        }

        private object? FindTrackedEntity(object entityReference) =>
            DbContext.ChangeTracker.Entries()
                .Select(entry => entry.Entity)
                .FirstOrDefault(entity => ReferenceEquals(entity, entityReference));

        public void MaterializeOutbox()
        {
            if (_outboxMaterialized)
            {
                throw new InvalidOperationException("The final outbox batch has already been materialized.");
            }

            _outboxMaterialized = true;
            if (_outbox.Count > 0)
            {
                DbContext.OutboxMessages.AddRange(_outbox);
            }
        }
    }
}
