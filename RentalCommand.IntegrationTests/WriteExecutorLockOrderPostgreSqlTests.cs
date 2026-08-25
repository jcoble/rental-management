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

[Collection(RoleAuthorityPostgreSqlCollection3.Name)]
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

    /// <summary>
    /// Pins every protocol's derived namespace order. Call sites now pass only ids, so this table
    /// is the sole guard against a mapping drifting in <see cref="WriteLockPlan"/>'s protocol
    /// table: a changed or added protocol must be re-pinned here deliberately.
    /// </summary>
    private static readonly IReadOnlyDictionary<WriteLockProtocol, string[]> PinnedProtocolNamespaces =
        new Dictionary<WriteLockProtocol, string[]>
        {
            [WriteLockProtocol.AuthorizationScope] =
                ["AuthSession", "WorkspaceAccessContext", "Portfolio"],
            [WriteLockProtocol.AuthorizationScopeOwnerEntity] =
                ["AuthSession", "WorkspaceAccessContext", "Portfolio", "OwnerEntity"],
            [WriteLockProtocol.AuthorizationScopeVendor] =
                ["AuthSession", "WorkspaceAccessContext", "Portfolio", "Vendor"],
            [WriteLockProtocol.AuthorizationScopeProperty] =
                ["AuthSession", "WorkspaceAccessContext", "Portfolio", "Property"],
            [WriteLockProtocol.AuthorizationScopeUnit] =
                ["AuthSession", "WorkspaceAccessContext", "Portfolio", "Unit"],
            [WriteLockProtocol.AuthorizationScopeApplication] =
                ["AuthSession", "WorkspaceAccessContext", "Portfolio", "RentalApplication"],
            [WriteLockProtocol.AuthorizationScopeTenant] =
                ["AuthSession", "WorkspaceAccessContext", "Portfolio", "Tenant"],
            [WriteLockProtocol.AuthorizationScopeScanDraft] =
                ["AuthSession", "WorkspaceAccessContext", "Portfolio", "ScanDraft"],
            [WriteLockProtocol.AuthorizationScopeRentalApplication] =
                ["AuthSession", "WorkspaceAccessContext", "Portfolio", "RentalApplication"],
            [WriteLockProtocol.Portfolio] = ["Portfolio"],
            [WriteLockProtocol.StoredFile] = ["StoredFile"],
            [WriteLockProtocol.WorkOrder] = ["WorkOrder"],
            [WriteLockProtocol.VendorDispatchInbound] = ["VendorDispatch"],
            [WriteLockProtocol.WorkOrderResponsibility] = [],
            [WriteLockProtocol.WorkspaceAccessContextWorkOrder] =
                ["WorkspaceAccessContext", "WorkOrder"],
            [WriteLockProtocol.WorkOrderVendor] = ["WorkOrder", "Vendor"],
            [WriteLockProtocol.Vendor] = ["Vendor"],
            [WriteLockProtocol.AppointmentWorkOrder] = ["Appointment"],
            [WriteLockProtocol.WorkOrderAppointmentProgression] = [],
            [WriteLockProtocol.Possession] = ["Unit", "LeaseManagement"],
            [WriteLockProtocol.ConfirmMoveIn] = ["Unit", "LeaseManagement", "TenantAccount"],
            [WriteLockProtocol.LeaseParty] = ["LeaseManagement"],
            [WriteLockProtocol.LeasePartyAccessGrant] = ["LeaseManagement"],
            [WriteLockProtocol.LeasePartyAccessRevoke] = ["LeaseManagement"],
            [WriteLockProtocol.TenantAccount] = ["TenantAccount"],
            [WriteLockProtocol.RentalApplication] = ["RentalApplication"],
            [WriteLockProtocol.TenantAccountRecurringCharge] =
                ["TenantAccount", "RecurringTenantCharge"],
            [WriteLockProtocol.AuthorizationScopeLedgerAccount] =
                ["AuthSession", "WorkspaceAccessContext", "Portfolio", "LedgerAccount"],
            [WriteLockProtocol.AuthorizationScopeTenantAccount] =
                ["AuthSession", "WorkspaceAccessContext", "TenantAccount"],
            [WriteLockProtocol.AuthorizationScopeAccountingConnection] =
                ["AuthSession", "WorkspaceAccessContext", "AccountingConnection"],
            [WriteLockProtocol.AccountingConnection] = ["AccountingConnection"],
            [WriteLockProtocol.NativeEsignLeaseManagement] = ["LeaseManagement"],
            [WriteLockProtocol.NativeEsignRequest] = ["SignatureRequest"],
            [WriteLockProtocol.PrepareMoveIn] = ["Unit"],
            [WriteLockProtocol.LeaseManagement] = ["LeaseManagement"],
            [WriteLockProtocol.LeaseAgreementDraft] =
                ["AuthSession", "WorkspaceAccessContext", "LeaseManagement"],
            [WriteLockProtocol.LeaseTransfer] = ["Unit", "Unit", "LeaseManagement"],
            [WriteLockProtocol.PropertyDisposition] = ["Property"],
            [WriteLockProtocol.BankingPrepareExchange] =
                ["AuthSession", "WorkspaceAccessContext", "BankConnection"],
            [WriteLockProtocol.BankingConnection] = ["BankConnection"],
            [WriteLockProtocol.BankingApplyConnection] = ["BankConnection"],
            [WriteLockProtocol.BankingReconciliation] =
                ["AuthSession", "WorkspaceAccessContext"],
            [WriteLockProtocol.BankingRoute] =
                ["AuthSession", "WorkspaceAccessContext", "BankTransaction"],
        };

    [Fact]
    public void EveryProtocol_DerivesItsPinnedNamespaceOrder()
    {
        var protocols = Enum.GetValues<WriteLockProtocol>();
        PinnedProtocolNamespaces.Keys.Should().BeEquivalentTo(
            protocols, "every protocol must be pinned here when it is added");

        foreach (var protocol in protocols)
        {
            var expected = PinnedProtocolNamespaces[protocol];
            var ids = Enumerable.Range(101, expected.Length).Cast<object>().ToArray();

            var plan = new WriteLockPlan(protocol, ids);

            plan.Locks.Select(item => item.LockNamespace).Should()
                .Equal(expected, "protocol '{0}' namespace order is pinned", protocol);
            plan.Locks.Select(item => (object)item.IntegerId!.Value).Should()
                .Equal(ids, "ids must bind positionally for protocol '{0}'", protocol);
        }
    }

    [Fact]
    public void Protocol_RejectsWrongLockIdCount()
    {
        var incomplete = () => new WriteLockPlan(WriteLockProtocol.ConfirmMoveIn, 31, 32);
        var excess = () => new WriteLockPlan(WriteLockProtocol.Possession, 21, 22, 23);

        incomplete.Should().Throw<ArgumentException>();
        excess.Should().Throw<ArgumentException>();
    }

    [Fact]
    public async Task PossessionExecutorRules_OnSameTargets_CompleteWithoutDeadlock()
    {
        await using var database = await fixture.CreateContextAsync();
        var coordinator = new LockCanaryCoordinator();
        var newLockProbe = new NewExecutorLockProbe(coordinator);
        await using var oldServices = BuildServices(database.ConnectionString, coordinator);
        await using var newServices = BuildServices(
            database.ConnectionString, coordinator, newLockProbe);
        await using var oldScope = oldServices.CreateAsyncScope();
        await using var newScope = newServices.CreateAsyncScope();
        var command = new LockCanaryCommand(UnitId: 41, LeaseManagementId: 42);

        var firstRule = new PossessionLockRule(coordinator);
        var firstWrite = new TransactionalWrite<LockCanaryCommand, LockCanaryResult>(
            "canary.possession.first-executor",

            command,
            LockCanaryResult.Codec.ContractName,
            WriteLockPlan.None,
            firstRule.ExecuteAsync,
            firstRule.AuthorizeReplayAsync);
        var oldTask = oldScope.ServiceProvider.GetRequiredService<IWriteExecutor>()
            .ExecuteAsync("first-executor", firstWrite);
        await coordinator.OldUnitHeld.Task.WaitAsync(TimeSpan.FromSeconds(10));

        var write = new TransactionalWrite<LockCanaryCommand, LockCanaryResult>(
            "canary.possession.new-executor",

            command,
            LockCanaryResult.Codec.ContractName,
            new WriteLockPlan(
                WriteLockProtocol.Possession,
                command.UnitId,
                command.LeaseManagementId),
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
    public async Task NoticeDeliveryExecutorRules_OnSameTargets_CompleteWithoutDeadlock()
    {
        await using var database = await fixture.CreateContextAsync();
        var coordinator = new LockCanaryCoordinator();
        var newLockProbe = new NewExecutorLockProbe(coordinator);
        await using var oldServices = BuildServices(database.ConnectionString, coordinator);
        await using var newServices = BuildServices(
            database.ConnectionString, coordinator, newLockProbe);
        await using var oldScope = oldServices.CreateAsyncScope();
        await using var newScope = newServices.CreateAsyncScope();
        var command = new NoticeDeliveryLockCanaryCommand(
            PortfolioId: 51,
            AuthSessionId: Guid.Parse("7cb9184f-1b50-47bc-bde6-3336152f5645"),
            AccessContextId: 52,
            NoticeDraftId: 53);

        var firstRule = new NoticeDeliveryLockRule(coordinator);
        var firstWrite = new TransactionalWrite<NoticeDeliveryLockCanaryCommand, LockCanaryResult>(
            "canary.notice-delivery.first-executor",

            command,
            LockCanaryResult.Codec.ContractName,
            WriteLockPlan.None,
            firstRule.ExecuteAsync,
            firstRule.AuthorizeReplayAsync);
        var oldTask = oldScope.ServiceProvider.GetRequiredService<IWriteExecutor>()
            .ExecuteAsync("first-executor", firstWrite);
        await coordinator.OldAuthSessionHeld.Task.WaitAsync(TimeSpan.FromSeconds(10));

        var write = new TransactionalWrite<NoticeDeliveryLockCanaryCommand, LockCanaryResult>(
            "canary.notice-delivery.new-executor",

            command,
            LockCanaryResult.Codec.ContractName,
            new WriteLockPlan(
                WriteLockProtocol.AuthorizationScope,
                command.AuthSessionId,
                command.AccessContextId,
                command.PortfolioId),
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
        NewExecutorLockProbe? newLockProbe = null)
    {
        var services = new ServiceCollection();
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton(coordinator);
        services.AddScoped<ICurrentActor, TestActor>();
        services.AddAtomicPersistenceKernel();
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

    private sealed class PossessionLockRule(LockCanaryCoordinator coordinator)
    {
        public async Task<LockCanaryResult> ExecuteAsync(
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

    private sealed class NoticeDeliveryLockRule(LockCanaryCoordinator coordinator)
    {
        public async Task<LockCanaryResult> ExecuteAsync(
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
