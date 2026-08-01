using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using RentalCommand.Core.Atomic;

namespace RentalCommand.Data.Atomic;

/// <summary>
/// Thin transaction boundary over the current scoped DbContext. Every handler, SaveChanges call,
/// audit row, receipt, and outbox row uses this exact context and explicit transaction.
/// </summary>
internal sealed class AtomicTransactionRunner
{
    private readonly RentalCommandDbContext _db;
    private readonly AtomicAuditScope _auditScope;
    private readonly AtomicCommandContext _context;
    private readonly TimeProvider _timeProvider;

    public AtomicTransactionRunner(
        RentalCommandDbContext db,
        AtomicAuditScope auditScope,
        AtomicCommandContext context,
        TimeProvider timeProvider)
    {
        _db = db;
        _auditScope = auditScope;
        _context = context;
        _timeProvider = timeProvider;
    }

    public async Task<AtomicCommandOutcome<TResult>> ExecuteAsync<TCommand, TResult>(
        AtomicCommandIdentity identity,
        TCommand command,
        AtomicJsonResultCodec<TResult> resultCodec,
        IAtomicCommandHandler<TCommand, TResult> handler,
        CancellationToken ct = default)
        where TCommand : notnull, IAtomicCommandData
        where TResult : notnull
    {
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(handler);
        ValidateCodec(resultCodec);
        identity = identity.BindRequest(command);

        if (_auditScope.IsActive)
        {
            throw new AtomicArchitectureException(
                "Nested atomic commands are not supported. Compose the workflow inside its owning handler.");
        }

        return await ExecuteAttemptAsync(identity, command, resultCodec, handler, ct);
    }

    private async Task<AtomicCommandOutcome<TResult>> ExecuteAttemptAsync<TCommand, TResult>(
        AtomicCommandIdentity identity,
        TCommand command,
        AtomicJsonResultCodec<TResult> resultCodec,
        IAtomicCommandHandler<TCommand, TResult> handler,
        CancellationToken ct)
        where TCommand : notnull, IAtomicCommandData
        where TResult : notnull
    {
        if (_db.Database.CurrentTransaction is not null)
        {
            throw new AtomicArchitectureException(
                "The scoped DbContext already has a transaction. The atomic boundary must own it.");
        }

        if (_db.ChangeTracker.HasChanges())
        {
            throw new AtomicArchitectureException(
                "The scoped DbContext has pending changes before the atomic workflow begins.");
        }

        var attemptId = Guid.NewGuid();
        IDisposable? auditLease = null;
        IDbContextTransaction? transaction = null;
        var committed = false;
        var contextBound = false;
        var originalAutoSavepointsEnabled = _db.Database.AutoSavepointsEnabled;

        try
        {
            _context.BeginAttempt(attemptId);
            contextBound = true;
            auditLease = _auditScope.BeginAttempt(identity, attemptId, _db);

            // This executor owns the complete transaction and always rolls it back on failure.
            // EF savepoints would split one atomic attempt across PostgreSQL subtransaction IDs,
            // preventing RLS from proving that staged business rows and their audit receipt were
            // written by this exact outer transaction.
            _db.Database.AutoSavepointsEnabled = false;
            using (_auditScope.BeginExecutorTransactionLifecycle())
            {
                transaction = await _db.Database.BeginTransactionAsync(ct);
            }
            _auditScope.BindExecutorTransaction(transaction);

            var startedAt = _timeProvider.GetUtcNow().UtcDateTime;
            int claimed;
            using (_auditScope.BeginInternalRawDml(
                "AtomicCommandReceipts",
                AtomicRawDmlOperation.Insert))
            {
                claimed = await _db.Database.ExecuteSqlInterpolatedAsync($$"""
                    INSERT INTO "AtomicCommandReceipts"
                        ("Id", "AttemptId", "CommandType", "IdempotencyKey", "RequestFingerprint",
                         "Status", "ResultContract", "StartedAt")
                    VALUES
                        ({{Guid.NewGuid()}}, {{attemptId}}, {{identity.CommandType}}, {{identity.IdempotencyKey}},
                         {{identity.RequestFingerprint!}},
                         {{(int)AtomicCommandReceiptStatus.Pending}}, {{resultCodec.ContractName}}, {{startedAt}})
                    ON CONFLICT ("CommandType", "IdempotencyKey") DO NOTHING
                    """, ct);
            }

            var receipt = await _db.AtomicCommandReceipts.SingleAsync(
                row => row.CommandType == identity.CommandType
                    && row.IdempotencyKey == identity.IdempotencyKey,
                ct);
            _context.BindReceipt(receipt.Id);
            if (claimed == 0)
            {
                await handler.AuthorizeReplayAsync(command, _context, ct);

                if (!string.Equals(
                        receipt.RequestFingerprint,
                        identity.RequestFingerprint,
                        StringComparison.Ordinal))
                {
                    throw new AtomicIdempotencyConflictException();
                }

                var replay = DeserializeReplay(
                    receipt.CommandType,
                    receipt.IdempotencyKey,
                    receipt.Status,
                    receipt.CompletedAt,
                    receipt.ResultContract,
                    receipt.ResultJson,
                    resultCodec);
                using (_auditScope.BeginExecutorTransactionLifecycle())
                {
                    await transaction.CommitAsync(ct);
                }
                committed = true;

                return new AtomicCommandOutcome<TResult>(
                    replay,
                    AtomicCommandDisposition.Replayed,
                    receipt.AttemptId);
            }

            var value = await handler.HandleAsync(command, _context, ct);

            ValidateResultValue(value);
            await _context.FlushBusinessAsync(ct);
            var resultJson = resultCodec.Serialize(value);
            using (JsonDocument.Parse(resultJson))
            {
                // A receipt codec must persist valid JSON for the PostgreSQL jsonb column.
            }

            receipt.ResultJson = resultJson;
            receipt.Status = AtomicCommandReceiptStatus.Completed;
            receipt.CompletedAt = _timeProvider.GetUtcNow().UtcDateTime;
            var auditRows = _auditScope.TakeRows();
            if (auditRows.Count > 0)
            {
                _db.AtomicAuditLogs.AddRange(auditRows);
            }

            _context.MaterializeOutbox();
            await _db.SaveChangesAsync(ct);
            using (_auditScope.BeginExecutorTransactionLifecycle())
            {
                await transaction.CommitAsync(ct);
            }
            committed = true;

            return new AtomicCommandOutcome<TResult>(
                value,
                AtomicCommandDisposition.Executed,
                attemptId);
        }
        catch
        {
            if (transaction is not null && _db.Database.CurrentTransaction is not null)
            {
                try
                {
                    using (_auditScope.BeginExecutorTransactionLifecycle())
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
            try
            {
                _db.Database.AutoSavepointsEnabled = originalAutoSavepointsEnabled;
                if (transaction is not null)
                {
                    await transaction.DisposeAsync();
                }
                if (!committed)
                {
                    _db.ChangeTracker.Clear();
                }
            }
            finally
            {
                try
                {
                    auditLease?.Dispose();
                }
                finally
                {
                    if (contextBound)
                    {
                        _context.EndAttempt();
                    }
                }
            }
        }
    }

    private static void ValidateCodec<TResult>(AtomicJsonResultCodec<TResult> codec)
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

    private static void ValidateResultValue<TResult>(TResult result)
        where TResult : notnull
    {
        if (result is null)
        {
            throw new AtomicArchitectureException("Atomic handlers cannot return a null result.");
        }

    }

    private static TResult DeserializeReplay<TResult>(
        string commandType,
        string idempotencyKey,
        AtomicCommandReceiptStatus status,
        DateTime? completedAt,
        string resultContract,
        string? resultJson,
        AtomicJsonResultCodec<TResult> resultCodec)
        where TResult : notnull
    {
        if (status != AtomicCommandReceiptStatus.Completed || completedAt is null)
        {
            throw new AtomicReceiptInvariantException(
                $"Visible receipt {commandType}/{idempotencyKey} is not completed.");
        }

        if (!string.Equals(resultContract, resultCodec.ContractName, StringComparison.Ordinal))
        {
            throw new AtomicReceiptInvariantException(
                $"Receipt {commandType}/{idempotencyKey} uses result contract " +
                $"'{resultContract}', not '{resultCodec.ContractName}'.");
        }

        if (string.IsNullOrWhiteSpace(resultJson))
        {
            throw new AtomicReceiptInvariantException(
                $"Completed receipt {commandType}/{idempotencyKey} has no result JSON.");
        }

        var result = resultCodec.Deserialize(resultJson);
        ValidateResultValue(result);
        return result;
    }

}
