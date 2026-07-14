using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using RentalCommand.Core.Atomic;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Authorization;
using RentalCommand.Core.Banking;
using RentalCommand.Core.Accounting;
using RentalCommand.Core.Leasing;
using RentalCommand.Data.Accounting;
using RentalCommand.Data.Authorization;
using RentalCommand.Data.Banking;
using RentalCommand.Data.Leasing;

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
        where TCommand : notnull, IAtomicCommandData
        where TResult : notnull
    {
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(command);
        ValidateCodec(resultCodec);
        AtomicCommandAdmission.ValidateCommand(command);
        AtomicCommandAdmission.ValidateResultType(typeof(TResult));
        identity = identity.BindRequest(command);

        if (Ambient.Value is { } owner)
        {
            var joinedHandler = ResolveHandler<TCommand, TResult>(owner.Services);
            var joinedValue = await joinedHandler.HandleAsync(command, owner.Attempt, ct);
            ValidateResultValue(joinedValue);
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
        where TCommand : notnull, IAtomicCommandData
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
            int claimed;
            using (auditScope.BeginInternalRawDml(
                "AtomicCommandReceipts",
                AtomicRawDmlOperation.Insert))
            {
                claimed = await db.Database.ExecuteSqlInterpolatedAsync($$"""
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

            var receipt = await db.AtomicCommandReceipts.SingleAsync(
                row => row.CommandType == identity.CommandType
                    && row.IdempotencyKey == identity.IdempotencyKey,
                ct);
            if (claimed == 0)
            {
                var replayAuthorizer = ResolveReplayAuthorizer<TCommand, TResult>(services);
                if (replayAuthorizer is not null)
                {
                    await replayAuthorizer.AuthorizeReplayAsync(command, attempt.Persistence, ct);
                }

                if (!string.Equals(
                        receipt.RequestFingerprint,
                        identity.RequestFingerprint,
                        StringComparison.Ordinal))
                {
                    throw new AtomicIdempotencyConflictException();
                }

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

            var handler = ResolveHandler<TCommand, TResult>(services);
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

            ValidateResultValue(value);
            WorkspaceAccessGuardResult? accessGuardResult = null;
            if (command is IWorkspaceAccessMutationCommand accessMutation)
            {
                accessGuardResult = await services
                    .GetRequiredService<WorkspaceAccessRevisionGuard>()
                    .ValidatePendingMutationAsync(
                        db,
                        accessMutation.AccessContextId,
                        accessMutation.ExpectedRevision,
                        ct);
            }
            if (command is RentalCommand.Core.Operations.IWorkspaceAccessRevisionSetMutationCommand revisionSetMutation)
            {
                await services.GetRequiredService<WorkOrderResponsibilityAccessRevisionGuard>()
                    .ValidatePendingMutationAsync(
                        db,
                        revisionSetMutation.AccessRevisionExpectations,
                        ct);
            }

            await attempt.FlushBusinessAsync(ct);
            if (accessGuardResult is not null)
            {
                var assignmentIds = accessGuardResult.ExistingAssignmentIdsToValidate
                    .Concat(accessGuardResult.AssignmentEntitiesToValidate.Select(assignment => assignment.Id))
                    .Where(id => id > 0)
                    .Distinct()
                    .ToArray();
                await services
                    .GetRequiredService<MembershipAssignmentScopeValidator>()
                    .ValidateAsync(assignmentIds, ct);
            }
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
        if (codec.GetType() != typeof(AtomicJsonResultCodec<TResult>))
        {
            throw new AtomicArchitectureException(
                $"Atomic result codec {codec.GetType().FullName} is forbidden. Use the sealed " +
                $"{typeof(AtomicJsonResultCodec<TResult>).FullName} codec.");
        }

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

        AtomicCommandAdmission.ValidateResult(result);
    }

    private static IAtomicCommandHandler<TCommand, TResult> ResolveHandler<TCommand, TResult>(
        IServiceProvider services)
        where TCommand : notnull, IAtomicCommandData
        where TResult : notnull
    {
        var registration = services.GetService<AtomicHandlerRegistration<TCommand, TResult>>()
            ?? throw new AtomicArchitectureException(
                $"Atomic handler {typeof(TCommand).Name}/{typeof(TResult).Name} must be registered " +
                $"through AddAtomicCommandHandler.");
        AtomicCommandAdmission.ValidateHandler(registration.HandlerType);
        var handler = services.GetRequiredService<IAtomicCommandHandler<TCommand, TResult>>();
        if (handler.GetType() != registration.HandlerType)
        {
            throw new AtomicArchitectureException(
                $"Resolved atomic handler {handler.GetType().FullName} does not match the admitted " +
                $"type {registration.HandlerType.FullName}.");
        }

        return handler;
    }

    private static IAtomicReplayAuthorizer<TCommand>? ResolveReplayAuthorizer<TCommand, TResult>(
        IServiceProvider services)
        where TCommand : notnull, IAtomicCommandData
        where TResult : notnull
    {
        var registration = services.GetService<AtomicHandlerRegistration<TCommand, TResult>>()
            ?? throw new AtomicArchitectureException(
                $"Atomic handler {typeof(TCommand).Name}/{typeof(TResult).Name} must be registered " +
                "through AddAtomicCommandHandler.");
        if (!typeof(IAtomicReplayAuthorizer<TCommand>).IsAssignableFrom(registration.HandlerType))
        {
            return null;
        }

        var handler = ResolveHandler<TCommand, TResult>(services);
        return handler as IAtomicReplayAuthorizer<TCommand>
            ?? throw new AtomicArchitectureException(
                $"Atomic handler {registration.HandlerType.FullName} declares replay authorization " +
                $"but does not implement {typeof(IAtomicReplayAuthorizer<TCommand>).FullName}.");
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

        var result = resultCodec.Deserialize(receipt.ResultJson);
        ValidateResultValue(result);
        return result;
    }

    private sealed record AmbientAttempt(
        IServiceProvider Services,
        AtomicWriteAttempt Attempt);

    private sealed class AtomicWriteAttempt : IAtomicWriteAttempt, IAtomicPersistenceSession
    {
        private readonly RentalCommandDbContext _db;
        private readonly AtomicAuditScope _auditScope;
        private readonly IAtomicSetBasedPersistence _setBased;
        private readonly IAtomicBankingPersistence _banking;
        private readonly IAtomicAccountingPersistence _accounting;
        private readonly IAtomicLockingPersistence _locking;
        private readonly IAtomicScanConfirmationPersistence _scanConfirmation;
        private readonly IAtomicScheduledFinancePersistence _scheduledFinance;
        private readonly IAtomicProviderInboxPersistence _providerInbox;
        private readonly IAtomicProviderPaymentPersistence _providerPayments;
        private readonly IAtomicTenantMoneyPersistence _tenantMoney;
        private readonly IAtomicPendingFileUploadPersistence _pendingFileUploads;
        private readonly IAtomicLeaseMutationPersistence _leasing;
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
            _setBased = new AtomicSetBasedMutationExecutor(db, auditScope, timeProvider);
            _banking = new AtomicBankingPersistence(db, auditScope);
            _accounting = new AtomicAccountingPullPersistence(db, auditScope);
            _locking = new AtomicLockingPersistence(db);
            _scanConfirmation = new AtomicScanConfirmationPersistence(db, auditScope, _locking);
            _scheduledFinance = new AtomicScheduledFinancePersistence(db, auditScope);
            _providerInbox = new AtomicProviderInboxPersistence(db, auditScope);
            _providerPayments = new AtomicProviderPaymentPersistence(db, auditScope);
            _tenantMoney = new AtomicTenantMoneyPersistence(db, auditScope);
            _pendingFileUploads = new AtomicPendingFileUploadPersistence(db, auditScope);
            _leasing = new AtomicLeaseMutationPersistence(db, auditScope);
            _timeProvider = timeProvider;
        }

        public Guid AttemptId { get; }
        public Guid AuditScopeId => _auditScope.ScopeId;
        public IAtomicPersistenceSession Persistence => this;
        public IAtomicSetBasedPersistence SetBased => _setBased;
        public IAtomicBankingPersistence Banking => _banking;
        public IAtomicAccountingPersistence Accounting => _accounting;
        public IAtomicLockingPersistence Locking => _locking;
        public IAtomicScanConfirmationPersistence ScanConfirmation => _scanConfirmation;
        public IAtomicScheduledFinancePersistence ScheduledFinance => _scheduledFinance;
        public IAtomicProviderInboxPersistence ProviderInbox => _providerInbox;
        public IAtomicProviderPaymentPersistence ProviderPayments => _providerPayments;
        public IAtomicTenantMoneyPersistence TenantMoney => _tenantMoney;
        public IAtomicPendingFileUploadPersistence PendingFileUploads => _pendingFileUploads;
        public IAtomicLeaseMutationPersistence Leasing => _leasing;
        public Guid SessionId => _db.ContextId.InstanceId;

        public Task<DateTime> ReadDatabaseClockUtcAsync(CancellationToken ct = default) =>
            _db.Database
                .SqlQuery<DateTime>($"SELECT clock_timestamp() AS \"Value\"")
                .SingleAsync(ct);

        public Task<DateOnly> ReadBusinessDateAsync(int portfolioId, CancellationToken ct = default)
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(portfolioId);
            return _db.Database
                .SqlQuery<DateOnly>($"SELECT rc_business_date({portfolioId}) AS \"Value\"")
                .SingleAsync(ct);
        }

        public async Task<AtomicCommandTimes> ReadCommandTimesAsync(
            int portfolioId,
            CancellationToken ct = default)
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(portfolioId);
            var row = await _db.Database.SqlQuery<AtomicCommandTimesRow>($$"""
                    SELECT clock_timestamp() AS "WallClockUtc",
                           rc_business_date({{portfolioId}}) AS "BusinessDate"
                    """)
                .SingleAsync(ct);
            return new AtomicCommandTimes(row.WallClockUtc, row.BusinessDate);
        }

        public IQueryable<TEntity> Query<TEntity>() where TEntity : class => _db.Set<TEntity>();

        public void Add<TEntity>(TEntity entity) where TEntity : class
        {
            ArgumentNullException.ThrowIfNull(entity);
            _db.Set<TEntity>().Add(entity);
        }

        public void AddRange<TEntity>(IEnumerable<TEntity> entities) where TEntity : class
        {
            ArgumentNullException.ThrowIfNull(entities);
            _db.Set<TEntity>().AddRange(entities);
        }

        public void Remove<TEntity>(TEntity entity) where TEntity : class
        {
            ArgumentNullException.ThrowIfNull(entity);
            _db.Set<TEntity>().Remove(entity);
        }

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

        private sealed class AtomicCommandTimesRow
        {
            public DateTime WallClockUtc { get; set; }
            public DateOnly BusinessDate { get; set; }
        }
    }
}
