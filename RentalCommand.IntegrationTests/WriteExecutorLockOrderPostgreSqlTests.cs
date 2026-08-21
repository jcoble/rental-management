using System.Data.Common;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using RentalCommand.Core.Atomic;
using RentalCommand.Core.Interfaces;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Leasing;
using RentalCommand.Data;
using RentalCommand.Data.Atomic;
using RentalCommand.Data.Leasing;
using RentalCommand.TestCommon;

namespace RentalCommand.IntegrationTests;

[Collection(RoleAuthorityPostgreSqlCollection.Name)]
public sealed class WriteExecutorLockOrderPostgreSqlTests(MigratedPostgreSqlFixture fixture)
{
    [Fact]
    public async Task PrepareMoveInRule_ExecutorRecordsResolvedUnitThenConditionalApplicationLock()
    {
        var recorder = new ResolvedLockRecorder(new Dictionary<int, string>
        {
            [101] = "Unit",
            [202] = "RentalApplication",
        });
        await using var database = await fixture.CreateContextAsync();
        await using var services = BuildMigrationServices(database.ConnectionString, recorder);
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<RentalCommandDbContext>();
        var command = new PrepareMoveInCommand(
            1, 202, 101, 1, Guid.NewGuid(), 1, 1, null, new DateOnly(2026, 8, 21), [], null,
            LeaseAgreementTermType.FixedTerm, new DateOnly(2026, 9, 1), new DateOnly(2027, 8, 31),
            1000m, 1, 500m, 25m, 5, 1, "{}", false, null, null, null, "move-in-lock-proof");

        Func<Task> act = async () => await scope.ServiceProvider.GetRequiredService<IWriteExecutor>()
            .ExecuteAsync("move-in-lock-proof",
                LeasingWriteSupport.Write<PrepareMoveInCommand, PrepareMoveInResult>(db, command));

        await act.Should().ThrowAsync<Exception>();
        recorder.Sequence.Should().Equal(("Unit", 101), ("RentalApplication", 202));
    }

    [Fact]
    public async Task TransferRule_ExecutorRecordsOrderedResolvedUnitsThenSourceRelationshipLock()
    {
        var recorder = new ResolvedLockRecorder(new Dictionary<int, string>
        {
            [303] = "Unit",
            [404] = "Unit",
            [505] = "LeaseManagement",
        });
        await using var database = await fixture.CreateContextAsync();
        await using var services = BuildMigrationServices(database.ConnectionString, recorder);
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<RentalCommandDbContext>();
        var command = new TransferLeaseManagementCommand(
            1, 505, 404, 303, 1, Guid.NewGuid(), 1, 1, DateTime.UtcNow, Guid.NewGuid(),
            new DateOnly(2026, 9, 1), null, false, null, 606, true, true,
            "Household requested a transfer.", "transfer-lock-proof");

        Func<Task> act = async () => await scope.ServiceProvider.GetRequiredService<IWriteExecutor>()
            .ExecuteAsync("transfer-lock-proof",
                LeasingWriteSupport.Write<TransferLeaseManagementCommand,
                    TransferLeaseManagementResult>(db, command));

        await act.Should().ThrowAsync<Exception>();
        recorder.Sequence.Should().Equal(("Unit", 303), ("Unit", 404), ("LeaseManagement", 505));
    }

    [Fact]
    public async Task PossessionRule_ExecutorRecordsUnitThenRelationshipLock()
    {
        var recorder = new ResolvedLockRecorder(new Dictionary<int, string>
        {
            [606] = "Unit",
            [707] = "LeaseManagement",
        });
        await using var database = await fixture.CreateContextAsync();
        await using var services = BuildMigrationServices(database.ConnectionString, recorder);
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<RentalCommandDbContext>();
        var command = new GivePossessionCommand(
            1, 707, 606, 1, Guid.NewGuid(), 1, 1, DateTime.UtcNow, "possession-lock-proof");

        Func<Task> act = async () => await scope.ServiceProvider.GetRequiredService<IWriteExecutor>()
            .ExecuteAsync("possession-lock-proof",
                LeasingWriteSupport.Write<GivePossessionCommand, GivePossessionResult>(db, command));

        await act.Should().ThrowAsync<Exception>();
        recorder.Sequence.Should().Equal(("Unit", 606), ("LeaseManagement", 707));
    }

    [Fact]
    public void ExplicitProtocols_EnforceCommonPrefixAndLegacyMultiAggregateOrder()
    {
        var authSessionId = Guid.Parse("9e10d15b-f5f0-48c0-9334-3215f24d07b5");

        var commonPrefix = new WriteLockPlan(
            WriteLockProtocol.AuthorizationScope,
            WriteLock.For("AuthSession", authSessionId),
            WriteLock.For("WorkspaceAccessContext", 11),
            WriteLock.For("Portfolio", 12));
        var possession = new WriteLockPlan(
            WriteLockProtocol.Possession,
            WriteLock.For("Unit", 21),
            WriteLock.For("LeaseManagement", 22));
        var confirmMoveIn = new WriteLockPlan(
            WriteLockProtocol.ConfirmMoveIn,
            WriteLock.For("Unit", 31),
            WriteLock.For("LeaseManagement", 32),
            WriteLock.For("TenantAccount", 33));

        commonPrefix.Locks.Select(item => item.LockNamespace).Should()
            .Equal("AuthSession", "WorkspaceAccessContext", "Portfolio");
        possession.Locks.Select(item => item.LockNamespace).Should()
            .Equal("Unit", "LeaseManagement");
        confirmMoveIn.Locks.Select(item => item.LockNamespace).Should()
            .Equal("Unit", "LeaseManagement", "TenantAccount");

        Action reversedPossession = () => new WriteLockPlan(
            WriteLockProtocol.Possession,
            WriteLock.For("LeaseManagement", 22),
            WriteLock.For("Unit", 21));
        Action incompleteMoveIn = () => new WriteLockPlan(
            WriteLockProtocol.ConfirmMoveIn,
            WriteLock.For("Unit", 31),
            WriteLock.For("LeaseManagement", 32));

        reversedPossession.Should().Throw<ArgumentException>();
        incompleteMoveIn.Should().Throw<ArgumentException>();
    }

    [Fact]
    public async Task OldPossessionShellAndNewExecutor_OnSameTargets_CompleteWithoutDeadlock()
    {
        await using var database = await fixture.CreateContextAsync();
        var coordinator = new LockCanaryCoordinator();
        var newLockProbe = new NewExecutorLockProbe(coordinator);
        await using var oldServices = BuildServices(database.ConnectionString, coordinator, oldShell: true);
        await using var newServices = BuildServices(
            database.ConnectionString, coordinator, oldShell: false, newLockProbe);
        await using var oldScope = oldServices.CreateAsyncScope();
        await using var newScope = newServices.CreateAsyncScope();
        var command = new LockCanaryCommand(UnitId: 41, LeaseManagementId: 42);

        var oldTask = oldScope.ServiceProvider.GetRequiredService<IAtomicUnitOfWork>().ExecuteAsync(
            new AtomicCommandIdentity("canary.possession.old-shell", "old-shell"),
            command,
            LockCanaryResult.Codec);
        await coordinator.OldUnitHeld.Task.WaitAsync(TimeSpan.FromSeconds(10));

        var write = new TransactionalWrite<LockCanaryCommand, LockCanaryResult>(
            "canary.possession.new-executor",
            WriteIdempotencyPolicy.Required,
            command,
            LockCanaryResult.Codec.ContractName,
            new WriteLockPlan(
                WriteLockProtocol.Possession,
                WriteLock.For("Unit", command.UnitId),
                WriteLock.For("LeaseManagement", command.LeaseManagementId)),
            static (_, _, _) => Task.FromResult(new LockCanaryResult(true)),
            static (_, _, _) => Task.CompletedTask);
        var newTask = newScope.ServiceProvider.GetRequiredService<IWriteExecutor>()
            .ExecuteAsync("new-executor", write);

        await coordinator.NewAdvisoryLockAttempted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var outcomes = await Task.WhenAll(oldTask, newTask).WaitAsync(TimeSpan.FromSeconds(15));

        outcomes.Should().OnlyContain(outcome =>
            outcome.Disposition == AtomicCommandDisposition.Executed && outcome.Value.Completed);
    }

    [Fact]
    public async Task ActorDrivenNoticeDeliveryShellAndAuthorizationExecutor_OnSameTargets_CompleteWithoutDeadlock()
    {
        await using var database = await fixture.CreateContextAsync();
        var coordinator = new LockCanaryCoordinator();
        var newLockProbe = new NewExecutorLockProbe(coordinator);
        await using var oldServices = BuildServices(database.ConnectionString, coordinator, oldShell: true);
        await using var newServices = BuildServices(
            database.ConnectionString, coordinator, oldShell: false, newLockProbe);
        await using var oldScope = oldServices.CreateAsyncScope();
        await using var newScope = newServices.CreateAsyncScope();
        var command = new NoticeDeliveryLockCanaryCommand(
            PortfolioId: 51,
            AuthSessionId: Guid.Parse("7cb9184f-1b50-47bc-bde6-3336152f5645"),
            AccessContextId: 52,
            NoticeDraftId: 53);

        var oldTask = oldScope.ServiceProvider.GetRequiredService<IAtomicUnitOfWork>().ExecuteAsync(
            new AtomicCommandIdentity("canary.notice-delivery.old-shell", "old-shell"),
            command,
            LockCanaryResult.Codec);
        await coordinator.OldAuthSessionHeld.Task.WaitAsync(TimeSpan.FromSeconds(10));

        var write = new TransactionalWrite<NoticeDeliveryLockCanaryCommand, LockCanaryResult>(
            "canary.notice-delivery.new-executor",
            WriteIdempotencyPolicy.Required,
            command,
            LockCanaryResult.Codec.ContractName,
            new WriteLockPlan(
                WriteLockProtocol.AuthorizationScope,
                WriteLock.For("AuthSession", command.AuthSessionId),
                WriteLock.For("WorkspaceAccessContext", command.AccessContextId),
                WriteLock.For("Portfolio", command.PortfolioId)),
            static (_, _, _) => Task.FromResult(new LockCanaryResult(true)),
            static (_, _, _) => Task.CompletedTask);
        var newTask = newScope.ServiceProvider.GetRequiredService<IWriteExecutor>()
            .ExecuteAsync("new-executor", write);

        await coordinator.NewAdvisoryLockAttempted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var outcomes = await Task.WhenAll(oldTask, newTask).WaitAsync(TimeSpan.FromSeconds(15));

        outcomes.Should().OnlyContain(outcome =>
            outcome.Disposition == AtomicCommandDisposition.Executed && outcome.Value.Completed);
    }

    private static ServiceProvider BuildServices(
        string connectionString,
        LockCanaryCoordinator coordinator,
        bool oldShell,
        NewExecutorLockProbe? newLockProbe = null)
    {
        var services = new ServiceCollection();
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton(coordinator);
        services.AddScoped<ICurrentActor, TestActor>();
        services.AddAtomicPersistenceKernel();
        if (oldShell)
        {
            services.AddAtomicCommandHandler<LockCanaryCommand, LockCanaryResult, OldPossessionShell>();
            services.AddAtomicCommandHandler<
                NoticeDeliveryLockCanaryCommand,
                LockCanaryResult,
                ActorDrivenNoticeDeliveryShell>();
        }

        services.AddDbContext<RentalCommandDbContext>((provider, options) =>
        {
            options.UseNpgsql(connectionString)
                .UseAtomicPersistenceKernel(provider);
            if (newLockProbe is not null)
            {
                options.AddInterceptors(newLockProbe);
            }
        });
        return services.BuildServiceProvider(
            new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
    }

    private static ServiceProvider BuildMigrationServices(
        string connectionString,
        ResolvedLockRecorder recorder)
    {
        var services = new ServiceCollection();
        services.AddSingleton(TimeProvider.System);
        services.AddScoped<ICurrentActor, TestActor>();
        services.AddAtomicPersistenceKernel();
        services.AddDbContext<RentalCommandDbContext>((provider, options) =>
            options.UseNpgsql(connectionString)
                .UseAtomicPersistenceKernel(provider)
                .AddInterceptors(recorder));
        return services.BuildServiceProvider(
            new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
    }

    private sealed record LockCanaryCommand(int UnitId, int LeaseManagementId) : IAtomicCommandData;

    private sealed record NoticeDeliveryLockCanaryCommand(
        int PortfolioId,
        Guid AuthSessionId,
        int AccessContextId,
        int NoticeDraftId) : IAtomicCommandData;

    private sealed record LockCanaryResult(bool Completed)
    {
        public static AtomicJsonResultCodec<LockCanaryResult> Codec { get; } =
            new("lock-order-canary.v1");
    }

    private sealed class OldPossessionShell(LockCanaryCoordinator coordinator)
        : IAtomicCommandHandler<LockCanaryCommand, LockCanaryResult>
    {
        public async Task<LockCanaryResult> HandleAsync(
            LockCanaryCommand command,
            IAtomicCommandContext context,
            CancellationToken ct)
        {
            await context.AcquireLockAsync("Unit", command.UnitId, ct);
            coordinator.OldUnitHeld.TrySetResult();
            await coordinator.NewAdvisoryLockAttempted.Task.WaitAsync(TimeSpan.FromSeconds(10), ct);
            await context.AcquireLockAsync("LeaseManagement", command.LeaseManagementId, ct);
            return new LockCanaryResult(true);
        }

        public Task AuthorizeReplayAsync(
            LockCanaryCommand command,
            IAtomicCommandContext context,
            CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class ActorDrivenNoticeDeliveryShell(LockCanaryCoordinator coordinator)
        : IAtomicCommandHandler<NoticeDeliveryLockCanaryCommand, LockCanaryResult>
    {
        public async Task<LockCanaryResult> HandleAsync(
            NoticeDeliveryLockCanaryCommand command,
            IAtomicCommandContext context,
            CancellationToken ct)
        {
            await context.AcquireLockAsync("AuthSession", command.AuthSessionId, ct);
            coordinator.OldAuthSessionHeld.TrySetResult();
            await coordinator.NewAdvisoryLockAttempted.Task.WaitAsync(TimeSpan.FromSeconds(10), ct);
            await context.AcquireLockAsync("WorkspaceAccessContext", command.AccessContextId, ct);
            await context.AcquireLockAsync("Portfolio", command.PortfolioId, ct);
            await context.AcquireLockAsync("NoticeDraft", command.NoticeDraftId, ct);
            return new LockCanaryResult(true);
        }

        public Task AuthorizeReplayAsync(
            NoticeDeliveryLockCanaryCommand command,
            IAtomicCommandContext context,
            CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class LockCanaryCoordinator
    {
        public TaskCompletionSource OldUnitHeld { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource OldAuthSessionHeld { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource NewAdvisoryLockAttempted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class NewExecutorLockProbe(LockCanaryCoordinator coordinator) : DbCommandInterceptor
    {
        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("pg_advisory_xact_lock", StringComparison.Ordinal))
            {
                coordinator.NewAdvisoryLockAttempted.TrySetResult();
            }

            return ValueTask.FromResult(result);
        }
    }

    private sealed class ResolvedLockRecorder(IReadOnlyDictionary<int, string> namespaces)
        : DbCommandInterceptor
    {
        public List<(string Namespace, int Id)> Sequence { get; } = [];

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("pg_advisory_xact_lock", StringComparison.Ordinal)
                && command.Parameters[command.Parameters.Count - 1].Value is int id
                && namespaces.TryGetValue(id, out var lockNamespace))
            {
                Sequence.Add((lockNamespace, id));
            }

            return ValueTask.FromResult(result);
        }
    }

    private sealed class TestActor : ICurrentActor
    {
        public int? UserId => null;
        public string? ActorLabel => "integration:lock-order-canary";
        public string? IpAddress => "127.0.0.1";
    }
}
