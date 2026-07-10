using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using RentalCommand.Core.Atomic;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Interfaces;
using RentalCommand.Data;
using RentalCommand.Data.Atomic;
using RentalCommand.Data.Auditing;
using Testcontainers.PostgreSql;
using Xunit;

namespace RentalCommand.IntegrationTests;

/// <summary>
/// PostgreSQL proof for the atomic command/audit kernel. Transaction rollback, unique receipt claims,
/// generated identity values, and ExecuteUpdate command-source guards are provider behavior that a
/// SQLite-only test cannot establish.
/// </summary>
public sealed class AtomicUnitOfWorkTests : IAsyncLifetime
{
    private static readonly AtomicJsonResultCodec<ExpenseResult> ExpenseCodec =
        new("atomic-expense-result.v1");

    private PostgreSqlContainer? _pg;
    private ServiceProvider? _services;
    private AuditFlushFailureInterceptor? _auditFailure;
    private bool _dockerAvailable;
    private int _portfolioId;

    public async Task InitializeAsync()
    {
        try
        {
            _pg = new PostgreSqlBuilder()
                .WithImage("postgres:16-alpine")
                .WithDatabase("rentalcommand")
                .WithUsername("postgres")
                .WithPassword("postgres")
                .Build();
            await _pg.StartAsync();
            _dockerAvailable = true;
        }
        catch
        {
            _dockerAvailable = false;
            return;
        }

        _auditFailure = new AuditFlushFailureInterceptor();
        var services = new ServiceCollection();
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton(_auditFailure);
        services.AddScoped<ICurrentActor, TestActor>();
        services.AddScoped<IAuditScope, AuditScope>();
        services.AddScoped<IAuditTrailService, StagedAuditTrailService>();
        services.AddScoped<AuditSaveChangesInterceptor>();
        services.AddScoped<AuditableCommandGuardInterceptor>();
        services.AddDbContext<RentalCommandDbContext>((provider, options) =>
            options.UseNpgsql(
                    _pg.GetConnectionString(),
                    npgsql => npgsql.EnableRetryOnFailure(maxRetryCount: 2))
                .AddInterceptors(
                    provider.GetRequiredService<AuditSaveChangesInterceptor>(),
                    provider.GetRequiredService<AuditableCommandGuardInterceptor>(),
                    provider.GetRequiredService<AuditFlushFailureInterceptor>()));
        services.AddSingleton<IAtomicUnitOfWork, AtomicUnitOfWork>();
        _services = services.BuildServiceProvider(validateScopes: true);

        await using var scope = _services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<RentalCommandDbContext>();
        await db.Database.MigrateAsync();
        var portfolio = new Portfolio
        {
            Name = "Atomic Kernel Portfolio",
            ManagementCompanyName = "Test Co",
            TimeZone = "UTC",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        db.Portfolios.Add(portfolio);
        await db.SaveChangesAsync();
        _portfolioId = portfolio.Id;
    }

    public async Task DisposeAsync()
    {
        if (_services is not null)
        {
            await _services.DisposeAsync();
        }

        if (_pg is not null)
        {
            await _pg.DisposeAsync();
        }
    }

    [SkippableFact]
    public async Task GeneratedIdAudit_IsStagedAfterBusinessFlush_AndCommittedWithReceipt()
    {
        SkipIfNoDocker();
        var command = Command(nameof(GeneratedIdAudit_IsStagedAfterBusinessFlush_AndCommittedWithReceipt));

        var outcome = await UnitOfWork.ExecuteAsync(
            command,
            ExpenseCodec,
            async (attempt, ct) =>
            {
                var expense = NewExpense("generated-id");
                attempt.DbContext.Expenses.Add(expense);
                await attempt.FlushBusinessAsync(ct);
                expense.Id.Should().BeGreaterThan(0);
                return new ExpenseResult(expense.Id);
            });

        outcome.Disposition.Should().Be(AtomicCommandDisposition.Executed);
        await using var verify = await NewScopeAsync();
        var row = await verify.Db.AuditLogs.AsNoTracking()
            .SingleAsync(a => a.CommandType == command.CommandType
                && a.CommandIdempotencyKey == command.IdempotencyKey);
        row.EntityId.Should().Be(outcome.Value.Id);
        row.Operation.Should().Be(AuditLogOperation.Created);
        row.MutationOrdinal.Should().BeGreaterThan(0);
        row.CommandAttemptId.Should().Be(outcome.AttemptId);
        (await verify.Db.AtomicCommandReceipts.AsNoTracking()
                .CountAsync(r => r.CommandType == command.CommandType
                    && r.IdempotencyKey == command.IdempotencyKey))
            .Should().Be(1);
    }

    [SkippableFact]
    public async Task FailureAfterGeneratedIdFlush_RollsBackBusinessAuditAndReceipt()
    {
        SkipIfNoDocker();
        var command = Command(nameof(FailureAfterGeneratedIdFlush_RollsBackBusinessAuditAndReceipt));
        const string marker = "rollback-after-business";

        Func<Task> act = async () => await UnitOfWork.ExecuteAsync(
            command,
            ExpenseCodec,
            async (attempt, ct) =>
            {
                attempt.DbContext.Expenses.Add(NewExpense(marker));
                await attempt.FlushBusinessAsync(ct);
                throw new InjectedFailureException("after business flush");
            });

        await act.Should().ThrowAsync<InjectedFailureException>();
        await AssertNothingCommitted(command, marker);
    }

    [SkippableFact]
    public async Task FailureDuringFinalAuditFlush_RollsBackEarlierBusinessFlush()
    {
        SkipIfNoDocker();
        var command = Command(nameof(FailureDuringFinalAuditFlush_RollsBackEarlierBusinessFlush));
        const string marker = "rollback-audit-flush";
        _auditFailure!.Arm();

        Func<Task> act = async () => await UnitOfWork.ExecuteAsync(
            command,
            ExpenseCodec,
            async (attempt, ct) =>
            {
                var expense = NewExpense(marker);
                attempt.DbContext.Expenses.Add(expense);
                await attempt.FlushBusinessAsync(ct);
                return new ExpenseResult(expense.Id);
            });

        await act.Should().ThrowAsync<InjectedFailureException>();
        await AssertNothingCommitted(command, marker);
    }

    [SkippableFact]
    public async Task DuplicateCommand_ReplaysTypedReceipt_WithoutRunningCallbackAgain()
    {
        SkipIfNoDocker();
        var command = Command(nameof(DuplicateCommand_ReplaysTypedReceipt_WithoutRunningCallbackAgain));
        var callbackCount = 0;

        async Task<ExpenseResult> Callback(IAtomicWriteAttempt attempt, CancellationToken ct)
        {
            Interlocked.Increment(ref callbackCount);
            var expense = NewExpense("idempotent");
            attempt.DbContext.Expenses.Add(expense);
            await attempt.FlushBusinessAsync(ct);
            return new ExpenseResult(expense.Id);
        }

        var first = await UnitOfWork.ExecuteAsync(command, ExpenseCodec, Callback);
        var replay = await UnitOfWork.ExecuteAsync(command, ExpenseCodec, Callback);

        first.Disposition.Should().Be(AtomicCommandDisposition.Executed);
        replay.Disposition.Should().Be(AtomicCommandDisposition.Replayed);
        replay.Value.Should().Be(first.Value);
        replay.AttemptId.Should().Be(first.AttemptId);
        callbackCount.Should().Be(1);
    }

    [SkippableFact]
    public async Task NestedCommand_JoinsOwnerContext_AndCannotCreateItsOwnReceipt()
    {
        SkipIfNoDocker();
        var outerCommand = Command(nameof(NestedCommand_JoinsOwnerContext_AndCannotCreateItsOwnReceipt));
        var innerCommand = new AtomicCommandIdentity("test.atomic.inner", outerCommand.IdempotencyKey);
        AtomicCommandOutcome<ExpenseResult>? innerOutcome = null;

        var outer = await UnitOfWork.ExecuteAsync(
            outerCommand,
            ExpenseCodec,
            async (ownerAttempt, ct) =>
            {
                innerOutcome = await UnitOfWork.ExecuteAsync(
                    innerCommand,
                    ExpenseCodec,
                    async (joinedAttempt, joinedCt) =>
                    {
                        joinedAttempt.DbContext.Should().BeSameAs(ownerAttempt.DbContext);
                        var expense = NewExpense("nested");
                        joinedAttempt.DbContext.Expenses.Add(expense);
                        await joinedAttempt.FlushBusinessAsync(joinedCt);
                        return new ExpenseResult(expense.Id);
                    },
                    ct);
                return innerOutcome.Value;
            });

        innerOutcome.Should().NotBeNull();
        innerOutcome!.Disposition.Should().Be(AtomicCommandDisposition.Joined);
        innerOutcome.AttemptId.Should().Be(outer.AttemptId);

        await using var verify = await NewScopeAsync();
        (await verify.Db.AtomicCommandReceipts.AsNoTracking()
                .CountAsync(r => (r.CommandType == outerCommand.CommandType
                        || r.CommandType == innerCommand.CommandType)
                    && r.IdempotencyKey == outerCommand.IdempotencyKey))
            .Should().Be(1);
    }

    [SkippableFact]
    public async Task SemanticAudit_EnrichesTheExactGeneratedGenericMutation()
    {
        SkipIfNoDocker();
        var command = Command(nameof(SemanticAudit_EnrichesTheExactGeneratedGenericMutation));

        var result = await UnitOfWork.ExecuteAsync(
            command,
            ExpenseCodec,
            async (attempt, ct) =>
            {
                var expense = NewExpense("semantic-enrichment");
                attempt.DbContext.Expenses.Add(expense);
                var flush = await attempt.FlushBusinessAsync(ct);
                await attempt.StageExactAuditAsync(
                    flush.Mutations.Single(),
                    new AtomicAuditEntry(
                        _portfolioId,
                        nameof(Expense),
                        expense.Id,
                        AuditLogOperation.Created,
                        NewValues: """{"Description":"semantic-enrichment","Amount":100}""",
                        ChangeReason: "Receipt confirmed"),
                    ct);
                return new ExpenseResult(expense.Id);
            });

        await using var verify = await NewScopeAsync();
        var rows = await verify.Db.AuditLogs.AsNoTracking()
            .Where(a => a.CommandType == command.CommandType
                && a.CommandIdempotencyKey == command.IdempotencyKey
                && a.EntityId == result.Value.Id)
            .ToListAsync();
        rows.Should().ContainSingle();
        rows[0].ChangeReason.Should().Be("Receipt confirmed");
        rows[0].NewValues.Should().Contain("semantic-enrichment");
    }

    [SkippableFact]
    public async Task TwoUpdatesToSameEntity_ProduceTwoDistinctMutationOrdinals()
    {
        SkipIfNoDocker();
        var command = Command(nameof(TwoUpdatesToSameEntity_ProduceTwoDistinctMutationOrdinals));

        var result = await UnitOfWork.ExecuteAsync(
            command,
            ExpenseCodec,
            async (attempt, ct) =>
            {
                var expense = NewExpense("two-updates");
                attempt.DbContext.Expenses.Add(expense);
                await attempt.FlushBusinessAsync(ct);

                expense.Amount = 150m;
                var firstFlush = await attempt.FlushBusinessAsync(ct);
                await attempt.StageExactAuditAsync(
                    firstFlush.Mutations.Single(),
                    new AtomicAuditEntry(
                        _portfolioId,
                        nameof(Expense),
                        expense.Id,
                        AuditLogOperation.Updated,
                        ChangeReason: "First mutation"),
                    ct);

                expense.Amount = 225m;
                var secondFlush = await attempt.FlushBusinessAsync(ct);
                await attempt.StageExactAuditAsync(
                    secondFlush.Mutations.Single(),
                    new AtomicAuditEntry(
                        _portfolioId,
                        nameof(Expense),
                        expense.Id,
                        AuditLogOperation.Updated,
                        ChangeReason: "Second mutation"),
                    ct);
                return new ExpenseResult(expense.Id);
            });

        await using var verify = await NewScopeAsync();
        var updates = await verify.Db.AuditLogs.AsNoTracking()
            .Where(a => a.CommandType == command.CommandType
                && a.CommandIdempotencyKey == command.IdempotencyKey
                && a.EntityId == result.Value.Id
                && a.Operation == AuditLogOperation.Updated)
            .OrderBy(a => a.MutationOrdinal)
            .ToListAsync();
        updates.Should().HaveCount(2);
        updates.Select(a => a.MutationOrdinal).Should().OnlyHaveUniqueItems();
        updates.Select(a => a.ChangeReason).Should().Equal("First mutation", "Second mutation");
    }

    [SkippableFact]
    public async Task AuditableWriteOutsideAtomicScope_IsRejectedBeforeSql()
    {
        SkipIfNoDocker();
        await using var scope = await NewScopeAsync();
        scope.Db.Expenses.Add(NewExpense("outside-scope"));

        Func<Task> act = () => scope.Db.SaveChangesAsync();

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*IAtomicUnitOfWork*");
    }

    [SkippableFact]
    public async Task ExecuteUpdateRequiresPreStagedSemanticAudit()
    {
        SkipIfNoDocker();
        var command = Command(nameof(ExecuteUpdateRequiresPreStagedSemanticAudit));

        Func<Task> unguarded = async () => await UnitOfWork.ExecuteAsync(
            command,
            ExpenseCodec,
            async (attempt, ct) =>
            {
                var expense = NewExpense("set-based-unguarded");
                attempt.DbContext.Expenses.Add(expense);
                await attempt.FlushBusinessAsync(ct);
                await attempt.DbContext.Expenses
                    .Where(e => e.Id == expense.Id)
                    .ExecuteUpdateAsync(
                        setters => setters.SetProperty(e => e.Amount, 333m),
                        ct);
                return new ExpenseResult(expense.Id);
            });

        await unguarded.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*StageSetBasedAuditAsync*");
        await AssertNothingCommitted(command, "set-based-unguarded");

        var guardedCommand = Command("ExecuteUpdateWithSemanticAudit");
        var guarded = await UnitOfWork.ExecuteAsync(
            guardedCommand,
            ExpenseCodec,
            async (attempt, ct) =>
            {
                var expense = NewExpense("set-based-guarded");
                attempt.DbContext.Expenses.Add(expense);
                await attempt.FlushBusinessAsync(ct);
                await attempt.StageSetBasedAuditAsync(
                    nameof(Expense),
                    new AtomicAuditEntry(
                        _portfolioId,
                        nameof(Expense),
                        expense.Id,
                        AuditLogOperation.Updated,
                        NewValues: """{"Amount":444}""",
                        ChangeReason: "Set-based correction"),
                    ct);
                await attempt.DbContext.Expenses
                    .Where(e => e.Id == expense.Id)
                    .ExecuteUpdateAsync(
                        setters => setters.SetProperty(e => e.Amount, 444m),
                        ct);
                return new ExpenseResult(expense.Id);
            });

        await using var verify = await NewScopeAsync();
        (await verify.Db.Expenses.AsNoTracking()
                .Where(e => e.Id == guarded.Value.Id)
                .Select(e => e.Amount)
                .SingleAsync())
            .Should().Be(444m);
        (await verify.Db.AuditLogs.AsNoTracking().CountAsync(a =>
                a.CommandType == guardedCommand.CommandType
                && a.CommandIdempotencyKey == guardedCommand.IdempotencyKey
                && a.Operation == AuditLogOperation.Updated))
            .Should().Be(1);
    }

    [SkippableFact]
    public async Task AuditOnlyCommand_HasExplicitAtomicDurabilityAndReplayPath()
    {
        SkipIfNoDocker();
        var command = Command(nameof(AuditOnlyCommand_HasExplicitAtomicDurabilityAndReplayPath));
        var audit = new AtomicAuditEntry(
            _portfolioId,
            "PortfolioPolicy",
            _portfolioId,
            AuditLogOperation.Updated,
            ActorLabel: "integration:atomic-kernel",
            NewValues: """{"Policy":"enabled"}""",
            ChangeReason: "Policy enabled");

        var first = await UnitOfWork.ExecuteAuditOnlyAsync(command, audit);
        var replay = await UnitOfWork.ExecuteAuditOnlyAsync(command, audit);

        first.Disposition.Should().Be(AtomicCommandDisposition.Executed);
        replay.Disposition.Should().Be(AtomicCommandDisposition.Replayed);
        replay.Value.Should().Be(first.Value);
        await using var verify = await NewScopeAsync();
        (await verify.Db.AuditLogs.AsNoTracking().CountAsync(a =>
                a.CommandType == command.CommandType
                && a.CommandIdempotencyKey == command.IdempotencyKey))
            .Should().Be(1);
    }

    private IAtomicUnitOfWork UnitOfWork =>
        _services!.GetRequiredService<IAtomicUnitOfWork>();

    private AtomicCommandIdentity Command(string testName) =>
        new($"test.atomic.{testName}", Guid.NewGuid().ToString("N"));

    private Expense NewExpense(string marker) => new()
    {
        PortfolioId = _portfolioId,
        Category = ScheduleECategory.Repairs,
        Description = marker,
        Status = ExpenseStatus.Pending,
        Amount = 100m,
        IncurredAt = DateTime.UtcNow,
        CreatedAt = DateTime.UtcNow,
        UpdatedAt = DateTime.UtcNow,
    };

    private async Task AssertNothingCommitted(AtomicCommandIdentity command, string expenseMarker)
    {
        await using var verify = await NewScopeAsync();
        (await verify.Db.Expenses.AsNoTracking().CountAsync(e => e.Description == expenseMarker))
            .Should().Be(0);
        (await verify.Db.AuditLogs.AsNoTracking().CountAsync(a =>
                a.CommandType == command.CommandType
                && a.CommandIdempotencyKey == command.IdempotencyKey))
            .Should().Be(0);
        (await verify.Db.AtomicCommandReceipts.AsNoTracking().CountAsync(r =>
                r.CommandType == command.CommandType
                && r.IdempotencyKey == command.IdempotencyKey))
            .Should().Be(0);
    }

    private Task<VerificationScope> NewScopeAsync()
    {
        var scope = _services!.CreateAsyncScope();
        return Task.FromResult(new VerificationScope(
            scope,
            scope.ServiceProvider.GetRequiredService<RentalCommandDbContext>()));
    }

    private void SkipIfNoDocker() =>
        Skip.IfNot(_dockerAvailable, "Docker is not available; atomic kernel PostgreSQL verification skipped.");

    private sealed record ExpenseResult(int Id);

    private sealed class TestActor : ICurrentActor
    {
        public int? UserId => null;
        public string? ActorLabel => "integration:atomic-kernel";
        public string? IpAddress => "127.0.0.1";
    }

    private sealed class InjectedFailureException(string message) : Exception(message);

    private sealed class AuditFlushFailureInterceptor : SaveChangesInterceptor
    {
        private int _armed;

        public void Arm() => Interlocked.Exchange(ref _armed, 1);

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            if (Volatile.Read(ref _armed) == 1
                && eventData.Context?.ChangeTracker.Entries<AuditLog>()
                    .Any(entry => entry.State == EntityState.Added) == true
                && Interlocked.Exchange(ref _armed, 0) == 1)
            {
                throw new InjectedFailureException("during final audit flush");
            }

            return base.SavingChangesAsync(eventData, result, cancellationToken);
        }
    }

    private sealed class VerificationScope(AsyncServiceScope scope, RentalCommandDbContext db)
        : IAsyncDisposable
    {
        public RentalCommandDbContext Db { get; } = db;
        public ValueTask DisposeAsync() => scope.DisposeAsync();
    }
}
