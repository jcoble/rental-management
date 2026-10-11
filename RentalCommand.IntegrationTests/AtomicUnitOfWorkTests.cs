using System.Collections.Concurrent;
using System.Data.Common;
using System.Net.Sockets;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using RentalCommand.Core.Atomic;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Interfaces;
using RentalCommand.Data;
using RentalCommand.Data.Atomic;
using RentalCommand.TestCommon;
using Xunit;

namespace RentalCommand.IntegrationTests;

/// <summary>PostgreSQL proof for the explicitly opt-in atomic persistence kernel.</summary>
public sealed class AtomicUnitOfWorkTests : IAsyncLifetime
{
    private static readonly AtomicJsonResultCodec<ExpenseResult> ExpenseCodec =
        new("atomic-expense-result.v2");
    private static readonly AtomicJsonResultCodec<EventResult> EventCodec =
        new("atomic-event-result.v1");
    private SharedPostgreSqlDatabase? _postgres;
    private ServiceProvider? _services;
    private bool _dockerAvailable;
    private int _portfolioId;

    public async Task InitializeAsync()
    {
        _dockerAvailable = await DockerSocketPreflightAsync();
        if (!_dockerAvailable)
        {
            return;
        }

        // Once Docker passes the explicit preflight, startup, migration, DI, and application errors
        // are deliberately not caught: they fail the test class instead of becoming false skips.
        _postgres = new SharedPostgreSqlDatabase(SharedPostgreSqlSchema.Migrated);
        await _postgres.StartAsync();

        var services = new ServiceCollection();
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<HandlerProbe>();
        services.AddSingleton<NestedProbe>();
        services.AddSingleton<AtomicFlushFailureInterceptor>();
        services.AddSingleton<SaveBoundaryFailureInterceptor>();
        services.AddSingleton<TransactionEvidenceInterceptor>();
        services.AddScoped<ICurrentActor, TestActor>();
        services.AddAtomicPersistenceKernel();
        services.AddScoped<ITestWriteRule<CreateExpenseCommand, ExpenseResult>, CreateExpenseHandler>();
        services.AddScoped<ITestWriteRule<RetryExpenseCommand, ExpenseResult>, RetryExpenseHandler>();
        services.AddScoped<ITestWriteRule<CreatedSemanticCommand, ExpenseResult>, CreatedSemanticHandler>();
        services.AddScoped<ITestWriteRule<ExactAuditCommand, ExpenseResult>, ExactAuditHandler>();
        services.AddScoped<ITestWriteRule<SemanticEventCommand, EventResult>, SemanticEventHandler>();
        services.AddScoped<ITestWriteRule<AtomicityCommand, ExpenseResult>, AtomicityHandler>();
        services.AddScoped<ITestWriteRule<NestedOuterCommand, ExpenseResult>, NestedOuterHandler>();
        services.AddScoped<ITestWriteRule<NestedInnerCommand, ExpenseResult>, NestedInnerHandler>();
        services.AddDbContext<RentalCommandDbContext>((provider, options) =>
            options.UseNpgsql(_postgres.GetConnectionString())
                .UseAtomicPersistenceKernel(provider)
                .AddInterceptors(
                    provider.GetRequiredService<AtomicFlushFailureInterceptor>(),
                    provider.GetRequiredService<SaveBoundaryFailureInterceptor>(),
                    provider.GetRequiredService<TransactionEvidenceInterceptor>()));
        _services = services.BuildServiceProvider(
            new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });

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

        if (_postgres is not null)
        {
            await _postgres.DisposeAsync();
        }
    }

    [SkippableFact]
    public async Task OrdinarySaveChanges_UsesTheScopedContextWithoutAnAtomicTransaction()
    {
        SkipIfDockerUnavailable();
        var evidence = Services.GetRequiredService<TransactionEvidenceInterceptor>();
        evidence.Reset();
        await using var scope = Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<RentalCommandDbContext>();
        var expense = NewExpense(_portfolioId, "ordinary-one-off-save");
        db.Expenses.Add(expense);

        await db.SaveChangesAsync();

        expense.Id.Should().BePositive();
        db.Database.CurrentTransaction.Should().BeNull();
        evidence.Starts.Should().Be(0);
        evidence.Commits.Should().Be(0);
        evidence.Rollbacks.Should().Be(0);
    }

    [SkippableFact]
    public async Task ActiveAtomicScope_RejectsADifferentDbContext()
    {
        SkipIfDockerUnavailable();
        await using var ownerScope = Services.CreateAsyncScope();
        await using var otherScope = Services.CreateAsyncScope();
        var ownerDb = ownerScope.ServiceProvider.GetRequiredService<RentalCommandDbContext>();
        var otherDb = otherScope.ServiceProvider.GetRequiredService<RentalCommandDbContext>();
        var audit = ownerScope.ServiceProvider.GetRequiredService<AtomicAuditScope>();
        using var attempt = audit.BeginAttempt(
            Identity(nameof(ActiveAtomicScope_RejectsADifferentDbContext)),
            Guid.NewGuid(),
            ownerDb);

        Action act = () => audit.GuardSaveChanges(otherDb);

        act.Should().Throw<AtomicArchitectureException>()
            .WithMessage("*different DbContext instance*");
    }

    [SkippableFact]
    public async Task ActiveAtomicScope_RejectsADifferentTransactionOnTheSameContext()
    {
        SkipIfDockerUnavailable();
        await using var scope = Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<RentalCommandDbContext>();
        var audit = scope.ServiceProvider.GetRequiredService<AtomicAuditScope>();
        using var attempt = audit.BeginAttempt(
            Identity(nameof(ActiveAtomicScope_RejectsADifferentTransactionOnTheSameContext)),
            Guid.NewGuid(),
            db);

        Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction owner;
        using (audit.BeginExecutorTransactionLifecycle())
        {
            owner = await db.Database.BeginTransactionAsync();
        }
        audit.BindExecutorTransaction(owner);
        audit.GuardSaveChanges(db);
        await owner.DisposeAsync();

        var connection = db.Database.GetDbConnection();
        if (connection.State != System.Data.ConnectionState.Open)
        {
            await connection.OpenAsync();
        }
        await using var foreign = await connection.BeginTransactionAsync();
        await db.Database.UseTransactionAsync(foreign);

        Action act = () => audit.GuardSaveChanges(db);

        act.Should().Throw<AtomicArchitectureException>()
            .WithMessage("*exact executor-owned transaction*");
        await db.Database.UseTransactionAsync(null);
        await foreign.RollbackAsync();
    }

    [SkippableFact]
    public async Task Command_CommitsBusinessAuditReceipt_AndTransactionEvidence()
    {
        SkipIfDockerUnavailable();
        var evidence = Services.GetRequiredService<TransactionEvidenceInterceptor>();
        evidence.Reset();
        var identity = Identity(nameof(Command_CommitsBusinessAuditReceipt_AndTransactionEvidence));

        var outcome = await ExecuteAsync(
            identity,
            new CreateExpenseCommand(_portfolioId, "atomic-success"),
            ExpenseCodec);

        outcome.Disposition.Should().Be(AtomicCommandDisposition.Executed);
        await using var verify = await VerificationScope.CreateAsync(Services);
        (await verify.Db.Expenses.AsNoTracking().CountAsync(row => row.Id == outcome.Value.Id)).Should().Be(1);
        (await verify.Db.AtomicAuditLogs.AsNoTracking().CountAsync(row =>
            row.CommandType == identity.CommandType
            && row.CommandIdempotencyKey == identity.IdempotencyKey
            && row.EntityId == outcome.Value.Id)).Should().Be(1);
        (await verify.Db.AtomicCommandReceipts.AsNoTracking().CountAsync(row =>
            row.CommandType == identity.CommandType
            && row.IdempotencyKey == identity.IdempotencyKey)).Should().Be(1);
        evidence.Starts.Should().Be(1);
        evidence.Commits.Should().Be(1);
        evidence.Rollbacks.Should().Be(0);
    }

    [SkippableFact]
    public async Task FailureDuringFinalAuditFlush_RollsBackBusinessAuditReceipt()
    {
        SkipIfDockerUnavailable();
        var failure = Services.GetRequiredService<AtomicFlushFailureInterceptor>();
        var evidence = Services.GetRequiredService<TransactionEvidenceInterceptor>();
        evidence.Reset();
        failure.Arm();
        var identity = Identity(nameof(FailureDuringFinalAuditFlush_RollsBackBusinessAuditReceipt));

        Func<Task> act = async () => await ExecuteAsync(
            identity,
            new CreateExpenseCommand(_portfolioId, "atomic-final-failure"),
            ExpenseCodec);

        await act.Should().ThrowAsync<InjectedFailureException>();
        await AssertNothingCommitted(identity, "atomic-final-failure");
        evidence.Starts.Should().Be(1);
        evidence.Commits.Should().Be(0);
        evidence.Rollbacks.Should().Be(1);
    }

    [SkippableTheory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task FailureAtEachSaveBoundary_RollsBackBusinessReceiptAuditAndOutbox(int saveBoundary)
    {
        SkipIfDockerUnavailable();
        var failure = Services.GetRequiredService<SaveBoundaryFailureInterceptor>();
        var evidence = Services.GetRequiredService<TransactionEvidenceInterceptor>();
        evidence.Reset();
        failure.Arm(saveBoundary);
        var marker = $"atomic-boundary-{saveBoundary}-{Guid.NewGuid():N}";
        var identity = Identity($"{nameof(FailureAtEachSaveBoundary_RollsBackBusinessReceiptAuditAndOutbox)}-{saveBoundary}");

        Func<Task> act = async () => await ExecuteAsync(
            identity,
            new AtomicityCommand(_portfolioId, marker),
            ExpenseCodec);

        await act.Should().ThrowAsync<InjectedFailureException>();
        await AssertNothingCommitted(identity, marker);
        evidence.Starts.Should().Be(1);
        evidence.Commits.Should().Be(0);
        evidence.Rollbacks.Should().Be(1);
    }

    [SkippableFact]
    public async Task Command_CommitsBusinessReceiptAuditAndOutboxTogether()
    {
        SkipIfDockerUnavailable();
        var evidence = Services.GetRequiredService<TransactionEvidenceInterceptor>();
        evidence.Reset();
        var marker = $"atomic-four-part-{Guid.NewGuid():N}";
        var identity = Identity(nameof(Command_CommitsBusinessReceiptAuditAndOutboxTogether));

        var outcome = await ExecuteAsync(
            identity,
            new AtomicityCommand(_portfolioId, marker),
            ExpenseCodec);

        await using var verify = await VerificationScope.CreateAsync(Services);
        (await verify.Db.Expenses.AsNoTracking().CountAsync(row => row.Id == outcome.Value.Id)).Should().Be(1);
        (await verify.Db.AtomicCommandReceipts.AsNoTracking().CountAsync(row =>
            row.CommandType == identity.CommandType && row.IdempotencyKey == identity.IdempotencyKey)).Should().Be(1);
        (await verify.Db.AtomicAuditLogs.AsNoTracking().CountAsync(row =>
            row.CommandType == identity.CommandType && row.CommandIdempotencyKey == identity.IdempotencyKey))
            .Should().Be(1);
        (await verify.Db.OutboxMessages.AsNoTracking().CountAsync(row => row.IdempotencyKey == marker)).Should().Be(1);
        evidence.Starts.Should().Be(1);
        evidence.Commits.Should().Be(1);
        evidence.Rollbacks.Should().Be(0);
    }

    [SkippableFact]
    public async Task ConcurrentSameKey_ExecutesOneProducer_AndReplaysTheOther()
    {
        SkipIfDockerUnavailable();
        var identity = Identity(nameof(ConcurrentSameKey_ExecutesOneProducer_AndReplaysTheOther));
        var command = new CreateExpenseCommand(_portfolioId, "atomic-concurrent", DelayMilliseconds: 250);

        var outcomes = await Task.WhenAll(
            ExecuteAsync(identity, command, ExpenseCodec),
            ExecuteAsync(identity, command, ExpenseCodec));

        outcomes.Select(result => result.Disposition).Should()
            .BeEquivalentTo([AtomicCommandDisposition.Executed, AtomicCommandDisposition.Replayed]);
        outcomes.Select(result => result.Value).Should().OnlyContain(value => value == outcomes[0].Value);
        Probe.Entries.Count(entry => entry.Marker == command.Marker).Should().Be(1);
        await using var verify = await VerificationScope.CreateAsync(Services);
        (await verify.Db.Expenses.AsNoTracking().CountAsync(row => row.Description == command.Marker)).Should().Be(1);
        (await verify.Db.AtomicCommandReceipts.AsNoTracking().CountAsync(row =>
            row.CommandType == identity.CommandType && row.IdempotencyKey == identity.IdempotencyKey)).Should().Be(1);
    }

    [SkippableFact]
    public async Task TransientFailure_IsNotRetried_AndRollsBackTheAttempt()
    {
        SkipIfDockerUnavailable();
        var identity = Identity(nameof(TransientFailure_IsNotRetried_AndRollsBackTheAttempt));
        var command = new RetryExpenseCommand(_portfolioId, "atomic-retry");

        Func<Task> act = async () => await ExecuteAsync(identity, command, ExpenseCodec);
        await act.Should().ThrowAsync<NpgsqlException>();
        var attempts = Probe.Entries.Where(entry => entry.Marker == command.Marker).ToArray();
        attempts.Should().ContainSingle();
        await AssertNothingCommitted(identity, command.Marker);
    }

    [SkippableFact]
    public async Task CreatedSemanticAudit_BindsUnresolvedId_AndStampsGeneratedId()
    {
        SkipIfDockerUnavailable();
        var identity = Identity(nameof(CreatedSemanticAudit_BindsUnresolvedId_AndStampsGeneratedId));

        var outcome = await ExecuteAsync(
            identity,
            new CreatedSemanticCommand(_portfolioId, "atomic-created-semantic"),
            ExpenseCodec);

        outcome.Value.Id.Should().BePositive();
        await using var verify = await VerificationScope.CreateAsync(Services);
        var row = await verify.Db.AtomicAuditLogs.AsNoTracking().SingleAsync(audit =>
            audit.CommandType == identity.CommandType
            && audit.CommandIdempotencyKey == identity.IdempotencyKey);
        row.EntityId.Should().Be(outcome.Value.Id);
        row.Operation.Should().Be(AuditLogOperation.Created);
        row.ChangeReason.Should().Be("rich-created-audit");
        row.NewValues.Should().Contain("atomic-created-semantic");
    }

    [SkippableFact]
    public async Task SemanticAudit_BindsBeforeFlush_AndEnrichesExactDescriptorAfterFlush()
    {
        SkipIfDockerUnavailable();
        var identity = Identity(nameof(SemanticAudit_BindsBeforeFlush_AndEnrichesExactDescriptorAfterFlush));

        var outcome = await ExecuteAsync(
            identity,
            new ExactAuditCommand(_portfolioId, "atomic-exact-audit"),
            ExpenseCodec);

        await using var verify = await VerificationScope.CreateAsync(Services);
        var updates = await verify.Db.AtomicAuditLogs.AsNoTracking()
            .Where(row => row.CommandType == identity.CommandType
                && row.CommandIdempotencyKey == identity.IdempotencyKey
                && row.EntityId == outcome.Value.Id
                && row.Operation == AuditLogOperation.Updated)
            .OrderBy(row => row.MutationOrdinal)
            .ToListAsync();
        updates.Should().HaveCount(2);
        updates.Select(row => row.MutationOrdinal).Should().OnlyHaveUniqueItems();
        updates.Select(row => row.ChangeReason).Should().Equal("bound-before-flush", "enriched-after-flush");
    }

    [SkippableFact]
    public async Task SemanticOnlyEvent_UsesSeparateExplicitPath_AndReplays()
    {
        SkipIfDockerUnavailable();
        var identity = Identity(nameof(SemanticOnlyEvent_UsesSeparateExplicitPath_AndReplays));
        var command = new SemanticEventCommand(_portfolioId, "policy-enabled");

        var first = await ExecuteAsync(identity, command, EventCodec);
        var replay = await ExecuteAsync(identity, command, EventCodec);

        first.Disposition.Should().Be(AtomicCommandDisposition.Executed);
        replay.Disposition.Should().Be(AtomicCommandDisposition.Replayed);
        await using var verify = await VerificationScope.CreateAsync(Services);
        (await verify.Db.AtomicAuditLogs.AsNoTracking().CountAsync(row =>
            row.CommandType == identity.CommandType && row.CommandIdempotencyKey == identity.IdempotencyKey))
            .Should().Be(1);
    }

    [SkippableFact]
    public async Task RawExecuteSqlDml_IsRejectedBeforeDatabaseMutation()
    {
        SkipIfDockerUnavailable();
        var identity = Identity(nameof(RawExecuteSqlDml_IsRejectedBeforeDatabaseMutation));
        var created = await ExecuteAsync(
            identity,
            new CreateExpenseCommand(_portfolioId, "atomic-raw-sql"),
            ExpenseCodec);
        await using var rawScope = await VerificationScope.CreateAsync(Services);

        Func<Task> act = () => rawScope.Db.Database.ExecuteSqlRawAsync(
            """UPDATE "Expenses" SET "Amount" = 999 WHERE "Id" = {0}""",
            created.Value.Id);

        await act.Should().ThrowAsync<AtomicArchitectureException>()
            .WithMessage("*Raw Update DML is forbidden*");
        Func<Task> interpolatedAct = () => rawScope.Db.Database.ExecuteSqlInterpolatedAsync(
            $"""UPDATE "Expenses" SET "Amount" = 998 WHERE "Id" = {created.Value.Id}""");
        await interpolatedAct.Should().ThrowAsync<AtomicArchitectureException>()
            .WithMessage("*Raw Update DML is forbidden*");
        await using var verify = await VerificationScope.CreateAsync(Services);
        (await verify.Db.Expenses.AsNoTracking()
            .Where(expense => expense.Id == created.Value.Id)
            .Select(expense => expense.Amount)
            .SingleAsync()).Should().Be(100m);
    }

    [SkippableFact]
    public async Task SetBasedDmlWithoutPermit_IsRejectedBeforeDatabaseMutation()
    {
        SkipIfDockerUnavailable();
        var identity = Identity(nameof(SetBasedDmlWithoutPermit_IsRejectedBeforeDatabaseMutation));
        var created = await ExecuteAsync(
            identity,
            new CreateExpenseCommand(_portfolioId, "atomic-set-based"),
            ExpenseCodec);
        await using var mutationScope = await VerificationScope.CreateAsync(Services);

        Func<Task> act = () => mutationScope.Db.Expenses
            .Where(expense => expense.Id == created.Value.Id)
            .ExecuteUpdateAsync(setters => setters.SetProperty(expense => expense.Amount, 999m));

        await act.Should().ThrowAsync<AtomicArchitectureException>()
            .WithMessage("The opt-in atomic persistence scope is not active for this operation.");
        await using var verify = await VerificationScope.CreateAsync(Services);
        (await verify.Db.Expenses.AsNoTracking()
            .Where(expense => expense.Id == created.Value.Id)
            .Select(expense => expense.Amount)
            .SingleAsync()).Should().Be(100m);
    }

    [SkippableFact]
    public async Task NestedCommand_IsRejectedInsteadOfCreatingASecondPath()
    {
        SkipIfDockerUnavailable();
        var outerIdentity = Identity(nameof(NestedCommand_IsRejectedInsteadOfCreatingASecondPath));
        var innerIdentity = new AtomicCommandIdentity("test.atomic.inner", outerIdentity.IdempotencyKey);

        Func<Task> act = async () => await ExecuteAsync(
            outerIdentity,
            new NestedOuterCommand(_portfolioId, "atomic-nested", innerIdentity),
            ExpenseCodec);

        await act.Should().ThrowAsync<AtomicArchitectureException>()
            .WithMessage("*Nested atomic commands are not supported*");
        await using var verify = await VerificationScope.CreateAsync(Services);
        (await verify.Db.AtomicCommandReceipts.AsNoTracking().CountAsync(row =>
            (row.CommandType == outerIdentity.CommandType || row.CommandType == innerIdentity.CommandType)
            && row.IdempotencyKey == outerIdentity.IdempotencyKey)).Should().Be(0);
        (await verify.Db.Expenses.AsNoTracking()
            .CountAsync(row => row.Description == "atomic-nested")).Should().Be(0);
    }

    [SkippableTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DirtyOrTransactionOwnedContext_IsRejectedBeforeAtomicWork(bool beginTransaction)
    {
        SkipIfDockerUnavailable();
        await using var scope = Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<RentalCommandDbContext>();
        await using var transaction = beginTransaction
            ? await db.Database.BeginTransactionAsync()
            : null;
        if (!beginTransaction)
        {
            db.Expenses.Add(NewExpense(_portfolioId, "preexisting-dirty-row"));
        }
        var marker = beginTransaction ? "preexisting-transaction" : "preexisting-dirty";
        var identity = Identity($"{nameof(DirtyOrTransactionOwnedContext_IsRejectedBeforeAtomicWork)}-{beginTransaction}");

        var command = new CreateExpenseCommand(_portfolioId, marker);
        var rule = scope.ServiceProvider
            .GetRequiredService<ITestWriteRule<CreateExpenseCommand, ExpenseResult>>();
        Func<Task> act = async () => await scope.ServiceProvider
            .GetRequiredService<IWriteExecutor>()
            .ExecuteAsync(identity.IdempotencyKey, Write(identity, command, ExpenseCodec, rule));

        await act.Should().ThrowAsync<AtomicArchitectureException>()
            .WithMessage(beginTransaction
                ? "*already has a transaction*"
                : "*pending changes before the atomic workflow begins*");
        await using var verify = await VerificationScope.CreateAsync(Services);
        (await verify.Db.Expenses.AsNoTracking().CountAsync(row => row.Description == marker)).Should().Be(0);
        (await verify.Db.AtomicCommandReceipts.AsNoTracking().CountAsync(row =>
            row.CommandType == identity.CommandType && row.IdempotencyKey == identity.IdempotencyKey)).Should().Be(0);
    }

    [SkippableTheory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ReplayWithMissingOrMismatchedStoredResult_FailsClosed(bool removeResult)
    {
        SkipIfDockerUnavailable();
        var marker = $"atomic-corrupt-result-{removeResult}-{Guid.NewGuid():N}";
        var identity = Identity($"{nameof(ReplayWithMissingOrMismatchedStoredResult_FailsClosed)}-{removeResult}");
        var command = new CreateExpenseCommand(_portfolioId, marker);
        await ExecuteAsync(identity, command, ExpenseCodec);
        await using (var corrupt = await VerificationScope.CreateAsync(Services))
        {
            var receipt = await corrupt.Db.AtomicCommandReceipts.SingleAsync(row =>
                row.CommandType == identity.CommandType && row.IdempotencyKey == identity.IdempotencyKey);
            if (removeResult)
            {
                receipt.ResultJson = null;
            }
            else
            {
                receipt.ResultContract = "different-result-contract.v1";
            }
            await corrupt.Db.SaveChangesAsync();
        }

        Func<Task> act = async () => await ExecuteAsync(identity, command, ExpenseCodec);

        await act.Should().ThrowAsync<AtomicReceiptInvariantException>();
        Probe.Entries.Count(entry => entry.Marker == marker).Should().Be(1,
            "a corrupt stored result must fail closed instead of executing the handler again");
    }

    [SkippableFact]
    public void ReceiptCodec_IsSealedInsteadOfAcceptingCallerImplementations()
    {
        typeof(AtomicJsonResultCodec<ExpenseResult>).IsSealed.Should().BeTrue();
    }

    private IServiceProvider Services => _services!;
    private HandlerProbe Probe => Services.GetRequiredService<HandlerProbe>();
    private NestedProbe Nested => Services.GetRequiredService<NestedProbe>();

    private async Task<AtomicCommandOutcome<TResult>> ExecuteAsync<TCommand, TResult>(
        AtomicCommandIdentity identity,
        TCommand command,
        AtomicJsonResultCodec<TResult> codec)
        where TCommand : notnull, IAtomicCommandData
        where TResult : notnull
    {
        await using var scope = Services.CreateAsyncScope();
        var rule = scope.ServiceProvider.GetRequiredService<ITestWriteRule<TCommand, TResult>>();
        return await scope.ServiceProvider
            .GetRequiredService<IWriteExecutor>()
            .ExecuteAsync(identity.IdempotencyKey, Write(identity, command, codec, rule));
    }

    private static TransactionalWrite<TCommand, TResult> Write<TCommand, TResult>(
        AtomicCommandIdentity identity,
        TCommand command,
        AtomicJsonResultCodec<TResult> codec,
        ITestWriteRule<TCommand, TResult> rule)
        where TCommand : notnull, IAtomicCommandData
        where TResult : notnull =>
        new(
            identity.CommandType,

            command,
            codec.ContractName,
            WriteLockPlan.None,
            rule.ExecuteAsync,
            rule.AuthorizeReplayAsync);

    private AtomicCommandIdentity Identity(string testName) =>
        new($"test.atomic.{testName}", Guid.NewGuid().ToString("N"));

    private async Task AssertNothingCommitted(AtomicCommandIdentity identity, string marker)
    {
        await using var verify = await VerificationScope.CreateAsync(Services);
        (await verify.Db.Expenses.AsNoTracking().CountAsync(row => row.Description == marker)).Should().Be(0);
        (await verify.Db.AtomicAuditLogs.AsNoTracking().CountAsync(row =>
            row.CommandType == identity.CommandType && row.CommandIdempotencyKey == identity.IdempotencyKey))
            .Should().Be(0);
        (await verify.Db.AtomicCommandReceipts.AsNoTracking().CountAsync(row =>
            row.CommandType == identity.CommandType && row.IdempotencyKey == identity.IdempotencyKey))
            .Should().Be(0);
        (await verify.Db.OutboxMessages.AsNoTracking().CountAsync(row => row.IdempotencyKey == marker))
            .Should().Be(0);
    }

    private void SkipIfDockerUnavailable() =>
        Skip.IfNot(_dockerAvailable, "Docker socket preflight failed; PostgreSQL atomic-kernel tests skipped.");

    private static async Task<bool> DockerSocketPreflightAsync()
    {
        var dockerHost = Environment.GetEnvironmentVariable("DOCKER_HOST");
        if (Uri.TryCreate(dockerHost, UriKind.Absolute, out var hostUri)
            && hostUri.Scheme is "tcp" or "http" or "https")
        {
            try
            {
                using var tcp = new TcpClient();
                await tcp.ConnectAsync(hostUri.Host, hostUri.Port > 0 ? hostUri.Port : 2375);
                return true;
            }
            catch (SocketException)
            {
                return false;
            }
        }

        var candidates = new[]
        {
            dockerHost?.StartsWith("unix://", StringComparison.Ordinal) == true ? dockerHost[7..] : null,
            "/var/run/docker.sock",
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".docker/run/docker.sock"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".colima/default/docker.sock"),
        }.Where(path => !string.IsNullOrWhiteSpace(path)).Distinct(StringComparer.Ordinal);

        foreach (var path in candidates)
        {
            try
            {
                using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                await socket.ConnectAsync(new UnixDomainSocketEndPoint(path!));
                return true;
            }
            catch (SocketException)
            {
                // Try the next well-known Docker socket.
            }
        }

        return false;
    }

    private static Expense NewExpense(int portfolioId, string marker) => new()
    {
        PortfolioId = portfolioId,
        Category = ScheduleECategory.Repairs,
        Description = marker,
        Status = ExpenseStatus.Pending,
        Amount = 100m,
        IncurredAt = DateTime.UtcNow,
        CreatedAt = DateTime.UtcNow,
        UpdatedAt = DateTime.UtcNow,
    };

    private sealed record ExpenseResult(int Id);
    private sealed record EventResult(string Name);
    private sealed record CreateExpenseCommand(int PortfolioId, string Marker, int DelayMilliseconds = 0)
        : IAtomicCommandData;
    private sealed record RetryExpenseCommand(int PortfolioId, string Marker) : IAtomicCommandData;
    private sealed record CreatedSemanticCommand(int PortfolioId, string Marker) : IAtomicCommandData;
    private sealed record ExactAuditCommand(int PortfolioId, string Marker) : IAtomicCommandData;
    private sealed record SemanticEventCommand(int PortfolioId, string Name) : IAtomicCommandData;
    private sealed record AtomicityCommand(int PortfolioId, string Marker) : IAtomicCommandData;
    private sealed record NestedOuterCommand(
        int PortfolioId,
        string Marker,
        AtomicCommandIdentity InnerIdentity) : IAtomicCommandData;
    private sealed record NestedInnerCommand(int PortfolioId, string Marker) : IAtomicCommandData;

    private sealed class CreateExpenseHandler : ITestWriteRule<CreateExpenseCommand, ExpenseResult>
    {
        private readonly RentalCommandDbContext _db;
        private readonly HandlerProbe _probe;
        private readonly Guid _handlerId = Guid.NewGuid();

        public CreateExpenseHandler(RentalCommandDbContext db, HandlerProbe probe)
        {
            _db = db;
            _probe = probe;
        }

        public async Task<ExpenseResult> ExecuteAsync(
            CreateExpenseCommand command,
            IAtomicCommandContext context,
            CancellationToken ct)
        {
            _probe.Record(command.Marker, _handlerId, _db, context);
            if (command.DelayMilliseconds > 0)
            {
                await Task.Delay(command.DelayMilliseconds, ct);
            }

            var expense = NewExpense(command.PortfolioId, command.Marker);
            _db.Expenses.Add(expense);
            await context.FlushBusinessAsync(ct);
            return new ExpenseResult(expense.Id);
        }

        public Task AuthorizeReplayAsync(
            CreateExpenseCommand command,
            IAtomicCommandContext context,
            CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class RetryExpenseHandler : ITestWriteRule<RetryExpenseCommand, ExpenseResult>
    {
        private readonly RentalCommandDbContext _db;
        private readonly HandlerProbe _probe;
        private readonly Guid _handlerId = Guid.NewGuid();

        public RetryExpenseHandler(RentalCommandDbContext db, HandlerProbe probe)
        {
            _db = db;
            _probe = probe;
        }

        public async Task<ExpenseResult> ExecuteAsync(
            RetryExpenseCommand command,
            IAtomicCommandContext context,
            CancellationToken ct)
        {
            _probe.Record(command.Marker, _handlerId, _db, context);
            var expense = NewExpense(command.PortfolioId, command.Marker);
            _db.Expenses.Add(expense);
            await context.FlushBusinessAsync(ct);
            if (_probe.Entries.Count(entry => entry.Marker == command.Marker) == 1)
            {
                throw new NpgsqlException("injected transient attempt", new TimeoutException());
            }

            return new ExpenseResult(expense.Id);
        }

        public Task AuthorizeReplayAsync(
            RetryExpenseCommand command,
            IAtomicCommandContext context,
            CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class CreatedSemanticHandler : ITestWriteRule<CreatedSemanticCommand, ExpenseResult>
    {
        private readonly RentalCommandDbContext _db;

        public CreatedSemanticHandler(RentalCommandDbContext db) => _db = db;

        public async Task<ExpenseResult> ExecuteAsync(
            CreatedSemanticCommand command,
            IAtomicCommandContext context,
            CancellationToken ct)
        {
            var expense = NewExpense(command.PortfolioId, command.Marker);
            _db.Expenses.Add(expense);
            context.BindSemanticAudit(
                expense,
                new AtomicSemanticAudit(
                    command.PortfolioId,
                    nameof(Expense),
                    EntityId: 0,
                    Operation: AuditLogOperation.Created,
                    NewValues: $$"""{"Description":"{{command.Marker}}"}""",
                    ChangeReason: "rich-created-audit"));
            await context.FlushBusinessAsync(ct);
            return new ExpenseResult(expense.Id);
        }

        public Task AuthorizeReplayAsync(
            CreatedSemanticCommand command,
            IAtomicCommandContext context,
            CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class ExactAuditHandler : ITestWriteRule<ExactAuditCommand, ExpenseResult>
    {
        private readonly RentalCommandDbContext _db;

        public ExactAuditHandler(RentalCommandDbContext db) => _db = db;

        public async Task<ExpenseResult> ExecuteAsync(
            ExactAuditCommand command,
            IAtomicCommandContext context,
            CancellationToken ct)
        {
            var expense = NewExpense(command.PortfolioId, command.Marker);
            _db.Expenses.Add(expense);
            await context.FlushBusinessAsync(ct);

            expense.Amount = 150m;
            context.BindSemanticAudit(
                expense,
                Audit(command.PortfolioId, expense.Id, "bound-before-flush", 150m));
            await context.FlushBusinessAsync(ct);

            expense.Amount = 225m;
            var second = await context.FlushBusinessAsync(ct);
            context.EnrichMutation(
                second.Mutations.Single(),
                Audit(command.PortfolioId, expense.Id, "enriched-after-flush", 225m));
            return new ExpenseResult(expense.Id);
        }

        public Task AuthorizeReplayAsync(
            ExactAuditCommand command,
            IAtomicCommandContext context,
            CancellationToken ct) => Task.CompletedTask;

        private static AtomicSemanticAudit Audit(int portfolioId, int id, string reason, decimal amount) =>
            new(
                portfolioId,
                nameof(Expense),
                id,
                AuditLogOperation.Updated,
                NewValues: $$"""{"Amount":{{amount}}}""",
                ChangeReason: reason);
    }

    private sealed class SemanticEventHandler : ITestWriteRule<SemanticEventCommand, EventResult>
    {
        public SemanticEventHandler() { }

        public Task<EventResult> ExecuteAsync(
            SemanticEventCommand command,
            IAtomicCommandContext context,
            CancellationToken ct)
        {
            context.StageSemanticEvent(new AtomicSemanticAudit(
                command.PortfolioId,
                "PortfolioPolicy",
                command.PortfolioId,
                AuditLogOperation.Updated,
                NewValues: $$"""{"Name":"{{command.Name}}"}""",
                ChangeReason: command.Name));
            return Task.FromResult(new EventResult(command.Name));
        }

        public Task AuthorizeReplayAsync(
            SemanticEventCommand command,
            IAtomicCommandContext context,
            CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class AtomicityHandler : ITestWriteRule<AtomicityCommand, ExpenseResult>
    {
        private readonly RentalCommandDbContext _db;

        public AtomicityHandler(RentalCommandDbContext db) => _db = db;

        public async Task<ExpenseResult> ExecuteAsync(
            AtomicityCommand command,
            IAtomicCommandContext context,
            CancellationToken ct)
        {
            var expense = NewExpense(command.PortfolioId, command.Marker);
            _db.Expenses.Add(expense);
            context.StageOutbox(new OutboxMessage
            {
                PortfolioId = command.PortfolioId,
                MessageType = "atomic-kernel-canary",
                Payload = "{}",
                IdempotencyKey = command.Marker,
            });
            await context.FlushBusinessAsync(ct);
            return new ExpenseResult(expense.Id);
        }

        public Task AuthorizeReplayAsync(
            AtomicityCommand command,
            IAtomicCommandContext context,
            CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class NestedOuterHandler : ITestWriteRule<NestedOuterCommand, ExpenseResult>
    {
        private readonly IWriteExecutor _writes;
        private readonly ITestWriteRule<NestedInnerCommand, ExpenseResult> _inner;
        private readonly NestedProbe _probe;

        public NestedOuterHandler(
            IWriteExecutor writes,
            ITestWriteRule<NestedInnerCommand, ExpenseResult> inner,
            NestedProbe probe)
        {
            _writes = writes;
            _inner = inner;
            _probe = probe;
        }

        public async Task<ExpenseResult> ExecuteAsync(
            NestedOuterCommand command,
            IAtomicCommandContext context,
            CancellationToken ct)
        {
            var innerCommand = new NestedInnerCommand(command.PortfolioId, command.Marker);
            _probe.InnerOutcome = await _writes.ExecuteAsync(
                command.InnerIdentity.IdempotencyKey,
                Write(command.InnerIdentity, innerCommand, ExpenseCodec, _inner),
                ct);
            return _probe.InnerOutcome.Value;
        }

        public Task AuthorizeReplayAsync(
            NestedOuterCommand command,
            IAtomicCommandContext context,
            CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class NestedInnerHandler : ITestWriteRule<NestedInnerCommand, ExpenseResult>
    {
        private readonly RentalCommandDbContext _db;

        public NestedInnerHandler(RentalCommandDbContext db) => _db = db;

        public async Task<ExpenseResult> ExecuteAsync(
            NestedInnerCommand command,
            IAtomicCommandContext context,
            CancellationToken ct)
        {
            var expense = NewExpense(command.PortfolioId, command.Marker);
            _db.Expenses.Add(expense);
            await context.FlushBusinessAsync(ct);
            return new ExpenseResult(expense.Id);
        }

        public Task AuthorizeReplayAsync(
            NestedInnerCommand command,
            IAtomicCommandContext context,
            CancellationToken ct) => Task.CompletedTask;
    }

    private interface ITestWriteRule<in TCommand, TResult>
        where TCommand : notnull, IAtomicCommandData
        where TResult : notnull
    {
        Task<TResult> ExecuteAsync(
            TCommand command,
            IAtomicCommandContext context,
            CancellationToken ct);

        Task AuthorizeReplayAsync(
            TCommand command,
            IAtomicCommandContext context,
            CancellationToken ct);
    }

    private sealed class HandlerProbe
    {
        public HandlerProbe() { }

        public ConcurrentBag<AttemptEvidence> Entries { get; } = [];

        public void Record(
            string marker,
            Guid handlerId,
            RentalCommandDbContext db,
            IAtomicCommandContext context) =>
            Entries.Add(new AttemptEvidence(
                marker,
                handlerId,
                db.ContextId.InstanceId,
                context.AttemptId,
                context.AttemptId));
    }

    private sealed record AttemptEvidence(
        string Marker,
        Guid HandlerId,
        Guid PersistenceSessionId,
        Guid AuditScopeId,
        Guid AttemptId);

    private sealed class NestedProbe
    {
        public NestedProbe() { }

        public AtomicCommandOutcome<ExpenseResult>? InnerOutcome { get; set; }
    }

    private sealed class TestActor : ICurrentActor
    {
        public int? UserId => null;
        public string? ActorLabel => "integration:atomic-kernel";
        public string? IpAddress => "127.0.0.1";
    }

    private sealed class InjectedFailureException(string message) : Exception(message);

    private sealed class AtomicFlushFailureInterceptor : SaveChangesInterceptor
    {
        private int _armed;
        public void Arm() => Interlocked.Exchange(ref _armed, 1);

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            if (Volatile.Read(ref _armed) == 1
                && eventData.Context?.ChangeTracker.Entries<AtomicAuditLog>()
                    .Any(entry => entry.State == EntityState.Added) == true
                && Interlocked.Exchange(ref _armed, 0) == 1)
            {
                throw new InjectedFailureException("during atomic audit companion flush");
            }

            return ValueTask.FromResult(result);
        }
    }

    private sealed class SaveBoundaryFailureInterceptor : SaveChangesInterceptor
    {
        private int _armedBoundary;
        private int _saveAttempt;

        public void Arm(int saveBoundary)
        {
            Interlocked.Exchange(ref _saveAttempt, 0);
            Interlocked.Exchange(ref _armedBoundary, saveBoundary);
        }

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            var attempt = Interlocked.Increment(ref _saveAttempt);
            if (attempt == Volatile.Read(ref _armedBoundary)
                && Interlocked.Exchange(ref _armedBoundary, 0) != 0)
            {
                throw new InjectedFailureException($"at atomic save boundary {attempt}");
            }

            return ValueTask.FromResult(result);
        }
    }

    private sealed class TransactionEvidenceInterceptor : DbTransactionInterceptor
    {
        private int _starts;
        private int _commits;
        private int _rollbacks;
        public int Starts => Volatile.Read(ref _starts);
        public int Commits => Volatile.Read(ref _commits);
        public int Rollbacks => Volatile.Read(ref _rollbacks);
        public void Reset()
        {
            Interlocked.Exchange(ref _starts, 0);
            Interlocked.Exchange(ref _commits, 0);
            Interlocked.Exchange(ref _rollbacks, 0);
        }

        public override ValueTask<InterceptionResult<DbTransaction>> TransactionStartingAsync(
            DbConnection connection,
            TransactionStartingEventData eventData,
            InterceptionResult<DbTransaction> result,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _starts);
            return ValueTask.FromResult(result);
        }

        public override ValueTask<InterceptionResult> TransactionCommittingAsync(
            DbTransaction transaction,
            TransactionEventData eventData,
            InterceptionResult result,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _commits);
            return ValueTask.FromResult(result);
        }

        public override ValueTask<InterceptionResult> TransactionRollingBackAsync(
            DbTransaction transaction,
            TransactionEventData eventData,
            InterceptionResult result,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _rollbacks);
            return ValueTask.FromResult(result);
        }
    }

    private sealed class VerificationScope(AsyncServiceScope scope, RentalCommandDbContext db)
        : IAsyncDisposable
    {
        public RentalCommandDbContext Db { get; } = db;

        public static Task<VerificationScope> CreateAsync(IServiceProvider services)
        {
            var scope = services.CreateAsyncScope();
            return Task.FromResult(new VerificationScope(
                scope,
                scope.ServiceProvider.GetRequiredService<RentalCommandDbContext>()));
        }

        public ValueTask DisposeAsync() => scope.DisposeAsync();
    }
}
