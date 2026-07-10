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
using Testcontainers.PostgreSql;
using Xunit;

namespace RentalCommand.IntegrationTests;

/// <summary>PostgreSQL proof for the explicitly opt-in atomic persistence kernel.</summary>
public sealed class AtomicUnitOfWorkTests : IAsyncLifetime
{
    private static readonly AtomicJsonResultCodec<ExpenseResult> ExpenseCodec =
        new("atomic-expense-result.v2");
    private static readonly AtomicJsonResultCodec<EventResult> EventCodec =
        new("atomic-event-result.v1");

    private PostgreSqlContainer? _postgres;
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
        _postgres = new PostgreSqlBuilder()
            .WithImage("postgres:16-alpine")
            .WithDatabase("rentalcommand")
            .WithUsername("postgres")
            .WithPassword("postgres")
            .Build();
        await _postgres.StartAsync();

        var services = new ServiceCollection();
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<HandlerProbe>();
        services.AddSingleton<NestedProbe>();
        services.AddSingleton<AtomicFlushFailureInterceptor>();
        services.AddSingleton<TransactionEvidenceInterceptor>();
        services.AddScoped<ICurrentActor, TestActor>();
        services.AddAtomicPersistenceKernel();
        services.AddScoped<IAtomicCommandHandler<CreateExpenseCommand, ExpenseResult>, CreateExpenseHandler>();
        services.AddScoped<IAtomicCommandHandler<RetryExpenseCommand, ExpenseResult>, RetryExpenseHandler>();
        services.AddScoped<IAtomicCommandHandler<ExactAuditCommand, ExpenseResult>, ExactAuditHandler>();
        services.AddScoped<IAtomicCommandHandler<SemanticEventCommand, EventResult>, SemanticEventHandler>();
        services.AddScoped<IAtomicCommandHandler<SetBasedCommand, ExpenseResult>, SetBasedHandler>();
        services.AddScoped<IAtomicCommandHandler<RawSetBasedCommand, ExpenseResult>, RawSetBasedHandler>();
        services.AddScoped<IAtomicCommandHandler<TransactionCommand, ExpenseResult>, TransactionHandler>();
        services.AddScoped<IAtomicCommandHandler<NestedOuterCommand, ExpenseResult>, NestedOuterHandler>();
        services.AddScoped<IAtomicCommandHandler<NestedInnerCommand, ExpenseResult>, NestedInnerHandler>();
        services.AddScoped<FakeRemoteDependency>();
        services.AddScoped<IAtomicCommandHandler<RemoteCommand, ExpenseResult>, RemoteHandler>();
        services.AddDbContext<RentalCommandDbContext>((provider, options) =>
            options.UseNpgsql(
                    _postgres.GetConnectionString(),
                    npgsql => npgsql.EnableRetryOnFailure(maxRetryCount: 2))
                .UseAtomicPersistenceKernel(provider)
                .AddInterceptors(
                    provider.GetRequiredService<AtomicFlushFailureInterceptor>(),
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
    public async Task Command_CommitsBusinessAuditReceipt_AndTransactionEvidence()
    {
        SkipIfDockerUnavailable();
        var evidence = Services.GetRequiredService<TransactionEvidenceInterceptor>();
        evidence.Reset();
        var identity = Identity(nameof(Command_CommitsBusinessAuditReceipt_AndTransactionEvidence));

        var outcome = await UnitOfWork.ExecuteAsync(
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

        Func<Task> act = async () => await UnitOfWork.ExecuteAsync(
            identity,
            new CreateExpenseCommand(_portfolioId, "atomic-final-failure"),
            ExpenseCodec);

        await act.Should().ThrowAsync<InjectedFailureException>();
        await AssertNothingCommitted(identity, "atomic-final-failure");
        evidence.Starts.Should().Be(1);
        evidence.Commits.Should().Be(0);
        evidence.Rollbacks.Should().Be(1);
    }

    [SkippableFact]
    public async Task ConcurrentSameKey_ExecutesOneProducer_AndReplaysTheOther()
    {
        SkipIfDockerUnavailable();
        var identity = Identity(nameof(ConcurrentSameKey_ExecutesOneProducer_AndReplaysTheOther));
        var command = new CreateExpenseCommand(_portfolioId, "atomic-concurrent", DelayMilliseconds: 250);

        var outcomes = await Task.WhenAll(
            UnitOfWork.ExecuteAsync(identity, command, ExpenseCodec),
            UnitOfWork.ExecuteAsync(identity, command, ExpenseCodec));

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
    public async Task TransientFirstAttempt_UsesFreshHandlerDbContextAndAuditScope()
    {
        SkipIfDockerUnavailable();
        var identity = Identity(nameof(TransientFirstAttempt_UsesFreshHandlerDbContextAndAuditScope));
        var command = new RetryExpenseCommand(_portfolioId, "atomic-retry");

        var outcome = await UnitOfWork.ExecuteAsync(identity, command, ExpenseCodec);

        outcome.Disposition.Should().Be(AtomicCommandDisposition.Executed);
        var attempts = Probe.Entries.Where(entry => entry.Marker == command.Marker).ToArray();
        attempts.Should().HaveCount(2);
        attempts.Select(entry => entry.HandlerId).Should().OnlyHaveUniqueItems();
        attempts.Select(entry => entry.DbContextId).Should().OnlyHaveUniqueItems();
        attempts.Select(entry => entry.AuditScopeId).Should().OnlyHaveUniqueItems();
        attempts.Select(entry => entry.AttemptId).Should().OnlyHaveUniqueItems();
        await using var verify = await VerificationScope.CreateAsync(Services);
        (await verify.Db.Expenses.AsNoTracking().CountAsync(row => row.Description == command.Marker)).Should().Be(1);
        (await verify.Db.AtomicAuditLogs.AsNoTracking().CountAsync(row =>
            row.CommandType == identity.CommandType && row.CommandIdempotencyKey == identity.IdempotencyKey))
            .Should().Be(1);
    }

    [SkippableFact]
    public async Task SemanticAudit_BindsBeforeFlush_AndEnrichesExactDescriptorAfterFlush()
    {
        SkipIfDockerUnavailable();
        var identity = Identity(nameof(SemanticAudit_BindsBeforeFlush_AndEnrichesExactDescriptorAfterFlush));

        var outcome = await UnitOfWork.ExecuteAsync(
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

        var first = await UnitOfWork.ExecuteAsync(identity, command, EventCodec);
        var replay = await UnitOfWork.ExecuteAsync(identity, command, EventCodec);

        first.Disposition.Should().Be(AtomicCommandDisposition.Executed);
        replay.Disposition.Should().Be(AtomicCommandDisposition.Replayed);
        await using var verify = await VerificationScope.CreateAsync(Services);
        (await verify.Db.AtomicAuditLogs.AsNoTracking().CountAsync(row =>
            row.CommandType == identity.CommandType && row.CommandIdempotencyKey == identity.IdempotencyKey))
            .Should().Be(1);
    }

    [SkippableFact]
    public async Task SetBasedAuditMismatch_IsRejectedAndRollsBack()
    {
        SkipIfDockerUnavailable();
        var identity = Identity(nameof(SetBasedAuditMismatch_IsRejectedAndRollsBack));

        Func<Task> act = async () => await UnitOfWork.ExecuteAsync(
            identity,
            new SetBasedCommand(_portfolioId, "atomic-set-mismatch", MismatchAudit: true),
            ExpenseCodec);

        await act.Should().ThrowAsync<ArgumentException>().WithMessage("*exact set-based target*");
        await AssertNothingCommitted(identity, "atomic-set-mismatch-1");
    }

    [SkippableFact]
    public async Task SetBasedExecutor_ExecutesTwoDistinctExactMutations()
    {
        SkipIfDockerUnavailable();
        var identity = Identity(nameof(SetBasedExecutor_ExecutesTwoDistinctExactMutations));

        await UnitOfWork.ExecuteAsync(
            identity,
            new SetBasedCommand(_portfolioId, "atomic-set-two", MismatchAudit: false),
            ExpenseCodec);

        await using var verify = await VerificationScope.CreateAsync(Services);
        var rows = await verify.Db.Expenses.AsNoTracking()
            .Where(row => row.Description.StartsWith("atomic-set-two"))
            .OrderBy(row => row.Description)
            .Select(row => row.Amount)
            .ToListAsync();
        rows.Should().Equal(301m, 302m);
        var audits = await verify.Db.AtomicAuditLogs.AsNoTracking()
            .Where(row => row.CommandType == identity.CommandType
                && row.CommandIdempotencyKey == identity.IdempotencyKey
                && row.Operation == AuditLogOperation.Updated)
            .OrderBy(row => row.MutationOrdinal)
            .ToListAsync();
        audits.Should().HaveCount(2);
        audits.Select(row => row.EntityId).Should().OnlyHaveUniqueItems();
        audits.Select(row => row.ChangeReason).Should().Equal("exact-set-1", "exact-set-2");
    }

    [SkippableFact]
    public async Task RawSetBasedWrite_IsRejectedWithoutExactExecutorLease()
    {
        SkipIfDockerUnavailable();
        var identity = Identity(nameof(RawSetBasedWrite_IsRejectedWithoutExactExecutorLease));

        Func<Task> act = async () => await UnitOfWork.ExecuteAsync(
            identity,
            new RawSetBasedCommand(_portfolioId, "atomic-raw-set"),
            ExpenseCodec);

        await act.Should().ThrowAsync<AtomicArchitectureException>().WithMessage("*exact atomic set-based executor*");
        await AssertNothingCommitted(identity, "atomic-raw-set");
    }

    [SkippableTheory]
    [InlineData(TransactionAction.Begin)]
    [InlineData(TransactionAction.Commit)]
    [InlineData(TransactionAction.Rollback)]
    public async Task HandlerCannotOwnTransactionLifecycle(TransactionAction action)
    {
        SkipIfDockerUnavailable();
        var marker = $"atomic-transaction-{action}";
        var identity = Identity($"{nameof(HandlerCannotOwnTransactionLifecycle)}-{action}");

        Func<Task> act = async () => await UnitOfWork.ExecuteAsync(
            identity,
            new TransactionCommand(_portfolioId, marker, action),
            ExpenseCodec);

        await act.Should().ThrowAsync<AtomicArchitectureException>().WithMessage("*executor owns the transaction lifecycle*");
        await AssertNothingCommitted(identity, marker);
    }

    [SkippableFact]
    public async Task NestedCommand_JoinsOwnerScope_WithoutSecondReceipt()
    {
        SkipIfDockerUnavailable();
        var outerIdentity = Identity(nameof(NestedCommand_JoinsOwnerScope_WithoutSecondReceipt));
        var innerIdentity = new AtomicCommandIdentity("test.atomic.inner", outerIdentity.IdempotencyKey);

        var outer = await UnitOfWork.ExecuteAsync(
            outerIdentity,
            new NestedOuterCommand(_portfolioId, "atomic-nested", innerIdentity),
            ExpenseCodec);

        Nested.InnerOutcome.Should().NotBeNull();
        Nested.InnerOutcome!.Disposition.Should().Be(AtomicCommandDisposition.Joined);
        Nested.InnerOutcome.AttemptId.Should().Be(outer.AttemptId);
        await using var verify = await VerificationScope.CreateAsync(Services);
        (await verify.Db.AtomicCommandReceipts.AsNoTracking().CountAsync(row =>
            (row.CommandType == outerIdentity.CommandType || row.CommandType == innerIdentity.CommandType)
            && row.IdempotencyKey == outerIdentity.IdempotencyKey)).Should().Be(1);
    }

    [SkippableFact]
    public async Task DirectRemoteDependency_IsRejectedBeforeHandlerRuns()
    {
        SkipIfDockerUnavailable();
        var identity = Identity(nameof(DirectRemoteDependency_IsRejectedBeforeHandlerRuns));

        Func<Task> act = async () => await UnitOfWork.ExecuteAsync(
            identity,
            new RemoteCommand(_portfolioId),
            ExpenseCodec);

        await act.Should().ThrowAsync<AtomicArchitectureException>().WithMessage("*remote dependency*");
        await using var verify = await VerificationScope.CreateAsync(Services);
        (await verify.Db.AtomicCommandReceipts.AsNoTracking().CountAsync(row =>
            row.CommandType == identity.CommandType && row.IdempotencyKey == identity.IdempotencyKey)).Should().Be(0);
    }

    private IServiceProvider Services => _services!;
    private IAtomicUnitOfWork UnitOfWork => Services.GetRequiredService<IAtomicUnitOfWork>();
    private HandlerProbe Probe => Services.GetRequiredService<HandlerProbe>();
    private NestedProbe Nested => Services.GetRequiredService<NestedProbe>();

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
    private sealed record CreateExpenseCommand(int PortfolioId, string Marker, int DelayMilliseconds = 0);
    private sealed record RetryExpenseCommand(int PortfolioId, string Marker);
    private sealed record ExactAuditCommand(int PortfolioId, string Marker);
    private sealed record SemanticEventCommand(int PortfolioId, string Name);
    private sealed record SetBasedCommand(int PortfolioId, string Marker, bool MismatchAudit);
    private sealed record RawSetBasedCommand(int PortfolioId, string Marker);
    private sealed record TransactionCommand(int PortfolioId, string Marker, TransactionAction Action);
    private sealed record NestedOuterCommand(
        int PortfolioId,
        string Marker,
        AtomicCommandIdentity InnerIdentity);
    private sealed record NestedInnerCommand(int PortfolioId, string Marker);
    private sealed record RemoteCommand(int PortfolioId);

    public enum TransactionAction { Begin, Commit, Rollback }

    private sealed class CreateExpenseHandler : IAtomicCommandHandler<CreateExpenseCommand, ExpenseResult>
    {
        private readonly RentalCommandDbContext _db;
        private readonly HandlerProbe _probe;
        private readonly Guid _handlerId = Guid.NewGuid();

        public CreateExpenseHandler(RentalCommandDbContext db, HandlerProbe probe)
        {
            _db = db;
            _probe = probe;
        }

        public async Task<ExpenseResult> HandleAsync(
            CreateExpenseCommand command,
            IAtomicWriteAttempt attempt,
            CancellationToken ct)
        {
            _probe.Record(command.Marker, _handlerId, _db, attempt);
            if (command.DelayMilliseconds > 0)
            {
                await Task.Delay(command.DelayMilliseconds, ct);
            }

            var expense = NewExpense(command.PortfolioId, command.Marker);
            _db.Expenses.Add(expense);
            await attempt.FlushBusinessAsync(ct);
            return new ExpenseResult(expense.Id);
        }
    }

    private sealed class RetryExpenseHandler : IAtomicCommandHandler<RetryExpenseCommand, ExpenseResult>
    {
        private readonly RentalCommandDbContext _db;
        private readonly HandlerProbe _probe;
        private readonly Guid _handlerId = Guid.NewGuid();

        public RetryExpenseHandler(RentalCommandDbContext db, HandlerProbe probe)
        {
            _db = db;
            _probe = probe;
        }

        public async Task<ExpenseResult> HandleAsync(
            RetryExpenseCommand command,
            IAtomicWriteAttempt attempt,
            CancellationToken ct)
        {
            _probe.Record(command.Marker, _handlerId, _db, attempt);
            var expense = NewExpense(command.PortfolioId, command.Marker);
            _db.Expenses.Add(expense);
            await attempt.FlushBusinessAsync(ct);
            if (_probe.Entries.Count(entry => entry.Marker == command.Marker) == 1)
            {
                throw new NpgsqlException("injected transient attempt", new TimeoutException());
            }

            return new ExpenseResult(expense.Id);
        }
    }

    private sealed class ExactAuditHandler : IAtomicCommandHandler<ExactAuditCommand, ExpenseResult>
    {
        private readonly RentalCommandDbContext _db;
        public ExactAuditHandler(RentalCommandDbContext db) => _db = db;

        public async Task<ExpenseResult> HandleAsync(
            ExactAuditCommand command,
            IAtomicWriteAttempt attempt,
            CancellationToken ct)
        {
            var expense = NewExpense(command.PortfolioId, command.Marker);
            _db.Expenses.Add(expense);
            await attempt.FlushBusinessAsync(ct);

            expense.Amount = 150m;
            attempt.BindSemanticAudit(
                expense,
                Audit(command.PortfolioId, expense.Id, "bound-before-flush", 150m));
            await attempt.FlushBusinessAsync(ct);

            expense.Amount = 225m;
            var second = await attempt.FlushBusinessAsync(ct);
            attempt.EnrichMutation(
                second.Mutations.Single(),
                Audit(command.PortfolioId, expense.Id, "enriched-after-flush", 225m));
            return new ExpenseResult(expense.Id);
        }

        private static AtomicSemanticAudit Audit(int portfolioId, int id, string reason, decimal amount) =>
            new(
                portfolioId,
                nameof(Expense),
                id,
                AuditLogOperation.Updated,
                NewValues: $$"""{"Amount":{{amount}}}""",
                ChangeReason: reason);
    }

    private sealed class SemanticEventHandler : IAtomicCommandHandler<SemanticEventCommand, EventResult>
    {
        public Task<EventResult> HandleAsync(
            SemanticEventCommand command,
            IAtomicWriteAttempt attempt,
            CancellationToken ct)
        {
            attempt.StageSemanticEvent(new AtomicSemanticAudit(
                command.PortfolioId,
                "PortfolioPolicy",
                command.PortfolioId,
                AuditLogOperation.Updated,
                NewValues: $$"""{"Name":"{{command.Name}}"}""",
                ChangeReason: command.Name));
            return Task.FromResult(new EventResult(command.Name));
        }
    }

    private sealed class SetBasedHandler : IAtomicCommandHandler<SetBasedCommand, ExpenseResult>
    {
        private readonly RentalCommandDbContext _db;
        private readonly IAtomicSetBasedMutationExecutor _setBased;

        public SetBasedHandler(RentalCommandDbContext db, IAtomicSetBasedMutationExecutor setBased)
        {
            _db = db;
            _setBased = setBased;
        }

        public async Task<ExpenseResult> HandleAsync(
            SetBasedCommand command,
            IAtomicWriteAttempt attempt,
            CancellationToken ct)
        {
            var first = NewExpense(command.PortfolioId, $"{command.Marker}-1");
            var second = NewExpense(command.PortfolioId, $"{command.Marker}-2");
            _db.Expenses.AddRange(first, second);
            await attempt.FlushBusinessAsync(ct);

            var firstAuditId = command.MismatchAudit ? second.Id : first.Id;
            await _setBased.UpdateAsync<Expense>(
                AtomicSetBasedTarget.For<Expense>(
                    command.PortfolioId,
                    first.Id,
                    AuditLogOperation.Updated),
                new AtomicSemanticAudit(
                    command.PortfolioId,
                    nameof(Expense),
                    firstAuditId,
                    AuditLogOperation.Updated,
                    NewValues: """{"Amount":301}""",
                    ChangeReason: "exact-set-1"),
                setters => setters.SetProperty(row => row.Amount, 301m),
                ct);
            if (!command.MismatchAudit)
            {
                await _setBased.UpdateAsync<Expense>(
                    AtomicSetBasedTarget.For<Expense>(
                        command.PortfolioId,
                        second.Id,
                        AuditLogOperation.Updated),
                    new AtomicSemanticAudit(
                        command.PortfolioId,
                        nameof(Expense),
                        second.Id,
                        AuditLogOperation.Updated,
                        NewValues: """{"Amount":302}""",
                        ChangeReason: "exact-set-2"),
                    setters => setters.SetProperty(row => row.Amount, 302m),
                    ct);
            }

            return new ExpenseResult(first.Id);
        }
    }

    private sealed class RawSetBasedHandler : IAtomicCommandHandler<RawSetBasedCommand, ExpenseResult>
    {
        private readonly RentalCommandDbContext _db;
        public RawSetBasedHandler(RentalCommandDbContext db) => _db = db;

        public async Task<ExpenseResult> HandleAsync(
            RawSetBasedCommand command,
            IAtomicWriteAttempt attempt,
            CancellationToken ct)
        {
            var expense = NewExpense(command.PortfolioId, command.Marker);
            _db.Expenses.Add(expense);
            await attempt.FlushBusinessAsync(ct);
            await _db.Expenses.Where(row => row.Id == expense.Id)
                .ExecuteUpdateAsync(setters => setters.SetProperty(row => row.Amount, 999m), ct);
            return new ExpenseResult(expense.Id);
        }
    }

    private sealed class TransactionHandler : IAtomicCommandHandler<TransactionCommand, ExpenseResult>
    {
        private readonly RentalCommandDbContext _db;
        private readonly DbContextOptions<RentalCommandDbContext> _options;

        public TransactionHandler(
            RentalCommandDbContext db,
            DbContextOptions<RentalCommandDbContext> options)
        {
            _db = db;
            _options = options;
        }

        public async Task<ExpenseResult> HandleAsync(
            TransactionCommand command,
            IAtomicWriteAttempt attempt,
            CancellationToken ct)
        {
            var expense = NewExpense(command.PortfolioId, command.Marker);
            _db.Expenses.Add(expense);
            await attempt.FlushBusinessAsync(ct);
            switch (command.Action)
            {
                case TransactionAction.Begin:
                    await using (var secondContext = new RentalCommandDbContext(_options))
                    {
                        await secondContext.Database.BeginTransactionAsync(ct);
                    }
                    break;
                case TransactionAction.Commit:
                    await _db.Database.CurrentTransaction!.CommitAsync(ct);
                    break;
                case TransactionAction.Rollback:
                    await _db.Database.CurrentTransaction!.RollbackAsync(ct);
                    break;
            }

            return new ExpenseResult(expense.Id);
        }
    }

    private sealed class NestedOuterHandler : IAtomicCommandHandler<NestedOuterCommand, ExpenseResult>
    {
        private readonly IAtomicUnitOfWork _unitOfWork;
        private readonly NestedProbe _probe;

        public NestedOuterHandler(IAtomicUnitOfWork unitOfWork, NestedProbe probe)
        {
            _unitOfWork = unitOfWork;
            _probe = probe;
        }

        public async Task<ExpenseResult> HandleAsync(
            NestedOuterCommand command,
            IAtomicWriteAttempt attempt,
            CancellationToken ct)
        {
            _probe.InnerOutcome = await _unitOfWork.ExecuteAsync(
                command.InnerIdentity,
                new NestedInnerCommand(command.PortfolioId, command.Marker),
                ExpenseCodec,
                ct);
            return _probe.InnerOutcome.Value;
        }
    }

    private sealed class NestedInnerHandler : IAtomicCommandHandler<NestedInnerCommand, ExpenseResult>
    {
        private readonly RentalCommandDbContext _db;
        public NestedInnerHandler(RentalCommandDbContext db) => _db = db;

        public async Task<ExpenseResult> HandleAsync(
            NestedInnerCommand command,
            IAtomicWriteAttempt attempt,
            CancellationToken ct)
        {
            var expense = NewExpense(command.PortfolioId, command.Marker);
            _db.Expenses.Add(expense);
            await attempt.FlushBusinessAsync(ct);
            return new ExpenseResult(expense.Id);
        }
    }

    private sealed class FakeRemoteDependency : IAtomicRemoteDependency;

    private sealed class RemoteHandler : IAtomicCommandHandler<RemoteCommand, ExpenseResult>
    {
        public RemoteHandler(FakeRemoteDependency remote) { }

        public Task<ExpenseResult> HandleAsync(
            RemoteCommand command,
            IAtomicWriteAttempt attempt,
            CancellationToken ct) => throw new InvalidOperationException("Remote handler must never run.");
    }

    private sealed class HandlerProbe
    {
        public ConcurrentBag<AttemptEvidence> Entries { get; } = [];

        public void Record(
            string marker,
            Guid handlerId,
            RentalCommandDbContext db,
            IAtomicWriteAttempt attempt) =>
            Entries.Add(new AttemptEvidence(
                marker,
                handlerId,
                db.ContextId.InstanceId,
                attempt.AuditScopeId,
                attempt.AttemptId));
    }

    private sealed record AttemptEvidence(
        string Marker,
        Guid HandlerId,
        Guid DbContextId,
        Guid AuditScopeId,
        Guid AttemptId);

    private sealed class NestedProbe
    {
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
