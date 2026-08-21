using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using RentalCommand.Api.Services.Domain;
using RentalCommand.Api.Writes;
using RentalCommand.Core.Accounting;
using RentalCommand.Core.Atomic;
using RentalCommand.Core.Authorization;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Interfaces;
using RentalCommand.Core.Models.Accounting;
using RentalCommand.Data;
using RentalCommand.Data.Accounting;
using RentalCommand.Data.Atomic;
using RentalCommand.TestCommon;

namespace RentalCommand.IntegrationTests;

[Collection(RoleAuthorityPostgreSqlCollection.Name)]
public sealed class AccountingWriteExecutorTests(MigratedPostgreSqlFixture fixture)
{
    private static readonly Guid SessionId =
        Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid ClaimId =
        Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid ContinuationId =
        Guid.Parse("33333333-3333-3333-3333-333333333333");
    private static readonly DateTime BusinessNow =
        new(2099, 8, 21, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void LedgerLifecycleAndMapping_PreserveFrozenFingerprints()
    {
        AtomicCommandFingerprint.Create(CreateLedger()).Should().Be(
            "48ed7e9b6cdc8b374b48507346caed81e4f855459438c3ae5c0f5a7aaa183ad8");
        AtomicCommandFingerprint.Create(PrepareDisconnect()).Should().Be(
            "f2aaf57d508701af847c8e3f0c3405be644ce9dac760444ef811492f4ab1cc92");
        AtomicCommandFingerprint.Create(ConfirmMapping()).Should().Be(
            "5204c8def42f660f622163e6c7613031fddde928dbfd0a56211b421ef7c2fd0b");
    }

    [Fact]
    public void ElevenWrites_PreserveIdentitiesResultContractsAndPublishedLockPlans()
    {
        var expected = new[]
        {
            ("accounting.ledger-account.create", "accounting.ledger-account.mutation.v1",
                new[] { "AuthSession", "WorkspaceAccessContext", "Portfolio" }),
            ("accounting.ledger-account.update", "accounting.ledger-account.mutation.v1",
                new[] { "AuthSession", "WorkspaceAccessContext", "Portfolio", "LedgerAccount" }),
            ("accounting.ledger-account.update", "accounting.ledger-account.mutation.v1",
                new[] { "AuthSession", "WorkspaceAccessContext", "Portfolio", "LedgerAccount" }),
            ("accounting.oauth-state.prepare", "rental.accounting-connect.prepare.v1",
                new[] { "AuthSession", "WorkspaceAccessContext", "Portfolio" }),
            ("tenant-autopay.cancel", "rental.tenant-autopay.cancel.v1",
                new[] { "AuthSession", "WorkspaceAccessContext", "TenantAccount" }),
            ("accounting.connection.disconnect.prepare", "rental.accounting-disconnect.prepare.v1",
                new[] { "AuthSession", "WorkspaceAccessContext", "Portfolio" }),
            ("accounting.connection.disconnect.finalize", "rental.accounting-disconnect.finalize.v1",
                new[] { "AuthSession", "WorkspaceAccessContext", "AccountingConnection" }),
            ("accounting.connection.direction.set", "rental.accounting-direction.set.v1",
                new[] { "AuthSession", "WorkspaceAccessContext", "Portfolio" }),
            ("accounting.pull.apply", "accounting.pull.apply.v1",
                new[] { "AccountingConnection" }),
            ("accounting.mapping.confirm", "accounting.mapping.confirm.result.v2",
                new[] { "AuthSession", "WorkspaceAccessContext", "AccountingConnection" }),
            ("accounting.mapping.promote.continue", "accounting.mapping.promote.continue.result.v1",
                new[] { "AuthSession", "WorkspaceAccessContext", "AccountingConnection" }),
        };

        Commands().Zip(expected).Should().AllSatisfy(pair =>
        {
            var write = AccountingWriteSupport.Write(
                pair.First,
                (_, _, _) => Task.FromResult(true),
                (_, _, _) => Task.CompletedTask);
            write.OperationName.Should().Be(pair.Second.Item1);
            write.ResultContract.Should().Be(pair.Second.Item2);
            write.LockPlan.Locks.Select(item => item.LockNamespace)
                .Should().Equal(pair.Second.Item3);
        });
    }

    [Fact]
    public async Task LedgerCreateRecorder_AcquiresConditionalParentBeforeCodeLock()
    {
        await using var database = await fixture.CreateContextAsync();
        var scope = await SeedAuthorityAsync(database.Db, "create-locks");
        var parent = await AddLedgerAsync(database.Db, scope.PortfolioId, "6100", "Parent");
        database.Db.ChangeTracker.Clear();

        foreach (var parentId in new int?[] { parent.Id, null })
        {
            var command = new CreateLedgerAccountCommand(
                scope.PortfolioId, scope.UserId, scope.SessionId, scope.AccessContextId,
                scope.AccessRevision, parentId is null ? "6200" : "6201", "Recorder child",
                AccountType.Expense, parentId, null, ScheduleECategory.Other, true,
                $"create-lock-{parentId?.ToString() ?? "none"}");
            var (context, acquired) = Recorder(database.Db);
            var handler = new CreateLedgerAccountHandler(database.Db);

            await ExecuteRecordedAsync(command, handler.ExecuteAsync, handler.AuthorizeAsync, context);

            acquired.Should().Equal(Prefix(scope)
                .Concat(parentId is null ? [] : new[] { $"LedgerAccount:{parent.Id}" })
                .Append($"LedgerAccountCode:{scope.PortfolioId}"));
        }
    }

    [Fact]
    public async Task LedgerUpdateRecorder_AcquiresDistinctTargetAndEffectiveParentOnce()
    {
        await using var database = await fixture.CreateContextAsync();
        var scope = await SeedAuthorityAsync(database.Db, "update-locks");
        var target = await AddLedgerAsync(database.Db, scope.PortfolioId, "6300", "Target");
        var parent = await AddLedgerAsync(database.Db, scope.PortfolioId, "6301", "Effective parent");
        database.Db.ChangeTracker.Clear();
        var command = new UpdateLedgerAccountCommand(
            scope.PortfolioId, scope.UserId, scope.SessionId, scope.AccessContextId,
            scope.AccessRevision, target.Id, "Updated target", null, parent.Id, true,
            ScheduleECategory.Other, true, false, "update-lock");
        var (context, acquired) = Recorder(database.Db);
        var handler = new UpdateLedgerAccountHandler(database.Db);

        await ExecuteRecordedAsync(command, handler.ExecuteAsync, handler.AuthorizeAsync, context);

        acquired.Should().Equal(Prefix(scope)
            .Append($"LedgerAccount:{target.Id}")
            .Append($"LedgerAccount:{parent.Id}"));
        acquired.Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public async Task DirectionRecorder_AcquiresAuthorizationPrefixThenResolvedConnection()
    {
        await using var database = await fixture.CreateContextAsync();
        var scope = await SeedAuthorityAsync(database.Db, "direction-locks");
        var connection = await AddConnectionAsync(database.Db, scope.PortfolioId);
        database.Db.ChangeTracker.Clear();
        var command = AtomicAccountingLifecycle.DirectionCommand(
            scope, AccountingProvider.QuickBooks, false, true, "direction-lock");
        var (context, acquired) = Recorder(database.Db);
        var handler = new SetAccountingDirectionHandler(database.Db);

        await ExecuteRecordedAsync(command, handler.ExecuteAsync, handler.AuthorizeAsync, context);

        acquired.Should().Equal(Prefix(scope).Append($"AccountingConnection:{connection.Id}"));
    }

    [Fact]
    public async Task LifecycleWrites_ExecuteThenReplayWithoutRepeatingMutation()
    {
        await using var database = await fixture.CreateContextAsync();
        var scope = await SeedAuthorityAsync(database.Db, "lifecycle-replay");
        var connection = await AddConnectionAsync(database.Db, scope.PortfolioId);
        await using var services = BuildServices(database.ConnectionString);
        await using var serviceScope = services.CreateAsyncScope();
        var db = serviceScope.ServiceProvider.GetRequiredService<RentalCommandDbContext>();
        var writes = serviceScope.ServiceProvider.GetRequiredService<IRequestWriteExecutor>();

        var connect = AtomicAccountingConnect.Command(
            scope, AccountingProvider.QuickBooks, "https://example.test/callback", "connect-replay");
        var connectHandler = new PrepareAccountingConnectHandler(db);
        var connectWrite = AccountingWriteSupport.Write(
            connect, connectHandler.ExecuteAsync, connectHandler.AuthorizeAsync);
        var firstConnect = await writes.ExecuteAsync(
            AtomicAccountingConnect.Identity(connect).IdempotencyKey, connectWrite);
        var replayConnect = await writes.ExecuteAsync(
            AtomicAccountingConnect.Identity(connect).IdempotencyKey, connectWrite);
        replayConnect.Disposition.Should().Be(AtomicCommandDisposition.Replayed);
        replayConnect.Value.Should().BeEquivalentTo(firstConnect.Value);
        (await db.OAuthStates.CountAsync()).Should().Be(1);

        var prepare = AtomicAccountingLifecycle.PrepareDisconnectCommand(
            scope, AccountingProvider.QuickBooks, "disconnect-replay");
        var prepareHandler = new PrepareAccountingDisconnectHandler(db);
        var prepareWrite = AccountingWriteSupport.Write(
            prepare, prepareHandler.ExecuteAsync, prepareHandler.AuthorizeAsync);
        var firstPrepare = await writes.ExecuteAsync(
            AtomicAccountingLifecycle.PrepareDisconnectIdentity(prepare).IdempotencyKey, prepareWrite);
        db.ChangeTracker.Clear();
        var preparedState = await db.AccountingConnections.AsNoTracking()
            .SingleAsync(row => row.Id == connection.Id);
        var replayPrepare = await writes.ExecuteAsync(
            AtomicAccountingLifecycle.PrepareDisconnectIdentity(prepare).IdempotencyKey, prepareWrite);
        replayPrepare.Disposition.Should().Be(AtomicCommandDisposition.Replayed);
        replayPrepare.Value.Should().BeEquivalentTo(firstPrepare.Value);
        db.ChangeTracker.Clear();
        (await db.AccountingConnections.AsNoTracking().SingleAsync(row => row.Id == connection.Id))
            .Should().BeEquivalentTo(preparedState);

        var finalize = AtomicAccountingLifecycle.FinalizeDisconnectCommand(prepare, firstPrepare.Value);
        var finalizeHandler = new FinalizeAccountingDisconnectHandler(db);
        var finalizeWrite = AccountingWriteSupport.Write(
            finalize, finalizeHandler.ExecuteAsync, finalizeHandler.AuthorizeAsync);
        var firstFinalize = await writes.ExecuteAsync(
            AtomicAccountingLifecycle.FinalizeDisconnectIdentity(finalize).IdempotencyKey, finalizeWrite);
        db.ChangeTracker.Clear();
        var finalizedState = await db.AccountingConnections.AsNoTracking()
            .SingleAsync(row => row.Id == connection.Id);
        var replayFinalize = await writes.ExecuteAsync(
            AtomicAccountingLifecycle.FinalizeDisconnectIdentity(finalize).IdempotencyKey, finalizeWrite);
        replayFinalize.Disposition.Should().Be(AtomicCommandDisposition.Replayed);
        replayFinalize.Value.Should().BeEquivalentTo(firstFinalize.Value);
        db.ChangeTracker.Clear();
        (await db.AccountingConnections.AsNoTracking().SingleAsync(row => row.Id == connection.Id))
            .Should().BeEquivalentTo(finalizedState);

        var direction = AtomicAccountingLifecycle.DirectionCommand(
            scope, AccountingProvider.QuickBooks, false, true, "direction-replay");
        var directionHandler = new SetAccountingDirectionHandler(db);
        var directionWrite = AccountingWriteSupport.Write(
            direction, directionHandler.ExecuteAsync, directionHandler.AuthorizeAsync);
        var firstDirection = await writes.ExecuteAsync(
            AtomicAccountingLifecycle.DirectionIdentity(direction).IdempotencyKey, directionWrite);
        db.ChangeTracker.Clear();
        var directedState = await db.AccountingConnections.AsNoTracking()
            .SingleAsync(row => row.Id == connection.Id);
        var replayDirection = await writes.ExecuteAsync(
            AtomicAccountingLifecycle.DirectionIdentity(direction).IdempotencyKey, directionWrite);
        replayDirection.Disposition.Should().Be(AtomicCommandDisposition.Replayed);
        replayDirection.Value.Should().BeEquivalentTo(firstDirection.Value);
        db.ChangeTracker.Clear();
        (await db.AccountingConnections.AsNoTracking().SingleAsync(row => row.Id == connection.Id))
            .Should().BeEquivalentTo(directedState);
        (await db.AccountingConnections.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task AllElevenLegacyHandlerArmsThrow()
    {
        var create = CreateLedger();
        var update = UpdateLedger(delete: false);
        var delete = UpdateLedger(delete: true);
        var connect = PrepareConnect();
        var autopay = CancelAutopay();
        var prepare = PrepareDisconnect();
        var finalize = FinalizeDisconnect();
        var direction = SetDirection();
        var pull = ApplyPull();
        var mapping = ConfirmMapping();
        var continuation = ContinueMapping();
        var calls = new Func<Task>[]
        {
            () => new CreateLedgerAccountHandler(null!).HandleAsync(create, null!, default),
            () => new CreateLedgerAccountHandler(null!).AuthorizeReplayAsync(create, null!, default),
            () => new UpdateLedgerAccountHandler(null!).HandleAsync(update, null!, default),
            () => new UpdateLedgerAccountHandler(null!).AuthorizeReplayAsync(update, null!, default),
            () => new UpdateLedgerAccountHandler(null!).HandleAsync(delete, null!, default),
            () => new UpdateLedgerAccountHandler(null!).AuthorizeReplayAsync(delete, null!, default),
            () => new PrepareAccountingConnectHandler(null!).HandleAsync(connect, null!, default),
            () => new PrepareAccountingConnectHandler(null!).AuthorizeReplayAsync(connect, null!, default),
            () => new CancelTenantAutopayHandler(null!).HandleAsync(autopay, null!, default),
            () => new CancelTenantAutopayHandler(null!).AuthorizeReplayAsync(autopay, null!, default),
            () => new PrepareAccountingDisconnectHandler(null!).HandleAsync(prepare, null!, default),
            () => new PrepareAccountingDisconnectHandler(null!).AuthorizeReplayAsync(prepare, null!, default),
            () => new FinalizeAccountingDisconnectHandler(null!).HandleAsync(finalize, null!, default),
            () => new FinalizeAccountingDisconnectHandler(null!).AuthorizeReplayAsync(finalize, null!, default),
            () => new SetAccountingDirectionHandler(null!).HandleAsync(direction, null!, default),
            () => new SetAccountingDirectionHandler(null!).AuthorizeReplayAsync(direction, null!, default),
            () => new ApplyAccountingPullResultHandler(null!).HandleAsync(pull, null!, default),
            () => new ApplyAccountingPullResultHandler(null!).AuthorizeReplayAsync(pull, null!, default),
            () => new ConfirmAccountingMappingHandler(null!).HandleAsync(mapping, null!, default),
            () => new ConfirmAccountingMappingHandler(null!).AuthorizeReplayAsync(mapping, null!, default),
            () => new ContinueAccountingMappingPromotionHandler(null!).HandleAsync(continuation, null!, default),
            () => new ContinueAccountingMappingPromotionHandler(null!).AuthorizeReplayAsync(continuation, null!, default),
        };

        foreach (var call in calls)
        {
            await call.Should().ThrowAsync<InvalidOperationException>()
                .WithMessage("*shared write executor*");
        }
    }

    private static IAtomicCommandData[] Commands() =>
    [
        CreateLedger(), UpdateLedger(false), UpdateLedger(true), PrepareConnect(), CancelAutopay(),
        PrepareDisconnect(), FinalizeDisconnect(), SetDirection(), ApplyPull(), ConfirmMapping(),
        ContinueMapping(),
    ];

    private static CreateLedgerAccountCommand CreateLedger() => new(
        1, 7, SessionId, 8, 9, "", "Frozen income", AccountType.Income,
        null, 41, null, ScheduleECategory.Other, true, "ledger-create");

    private static UpdateLedgerAccountCommand UpdateLedger(bool delete) => new(
        1, 7, SessionId, 8, 9, 42, delete ? null : "Frozen update", null,
        41, true, ScheduleECategory.Other, null, delete, delete ? "ledger-delete" : "ledger-update");

    private static PrepareAccountingConnectCommand PrepareConnect() => new(
        1, 7, SessionId, 8, 9, AccountingProvider.QuickBooks,
        "https://example.test/callback", "connect");

    private static CancelTenantAutopayCommand CancelAutopay() => new(
        1, 7, SessionId, 8, 9, 10, 11, "autopay-cancel");

    private static PrepareAccountingDisconnectCommand PrepareDisconnect() => new(
        1, 7, SessionId, 8, 9, AccountingProvider.QuickBooks, "disconnect-prepare");

    private static FinalizeAccountingDisconnectCommand FinalizeDisconnect() => new(
        1, 7, SessionId, 8, 9, AccountingProvider.QuickBooks, 12, 3,
        BusinessNow, "disconnect-finalize");

    private static SetAccountingDirectionCommand SetDirection() => new(
        1, 7, SessionId, 8, 9, AccountingProvider.QuickBooks, true, false, "direction");

    private static ApplyAccountingPullResultCommand ApplyPull() => new(
        1, 12, ClaimId, "batch", Array.Empty<ExtCustomerDto>(), Array.Empty<ExtVendorDto>(),
        Array.Empty<ExtAccountDto>(), Array.Empty<ExtPaymentDto>(), Array.Empty<ExtExpenseDto>(),
        "{}", BusinessNow);

    private static ConfirmAccountingMappingCommand ConfirmMapping() => new(
        1, 12, AccountingProvider.QuickBooks, 7, SessionId, 8, 9,
        CapabilityKeys.IntegrationsManage, "Customer", "external-1", "Frozen tenant",
        "Tenant", 10, null, "mapping-confirm", 0, BusinessNow);

    private static ContinueAccountingMappingPromotionCommand ContinueMapping() => new(
        1, 12, ContinuationId, 7, SessionId, 8, 9, CapabilityKeys.IntegrationsManage,
        "mapping-continue", BusinessNow);

    private static async Task ExecuteRecordedAsync<TCommand, TResult>(
        TCommand command,
        Func<TCommand, IAtomicCommandContext, CancellationToken, Task<TResult>> execute,
        Func<TCommand, IAtomicCommandContext, CancellationToken, Task> authorize,
        IAtomicCommandContext context)
        where TCommand : notnull, IAtomicCommandData
        where TResult : notnull
    {
        var write = AccountingWriteSupport.Write(command, execute, authorize);
        foreach (var writeLock in write.LockPlan.Locks)
            await writeLock.AcquireAsync(context);
        await write.ExecuteAsync(command, context, CancellationToken.None);
    }

    private static (IAtomicCommandContext Context, List<string> Acquired) Recorder(
        RentalCommandDbContext db)
    {
        var acquired = new List<string>();
        var recorder = new Mock<IAtomicCommandContext>();
        recorder.Setup(item => item.ReadDatabaseClockUtcAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(DateTime.UtcNow);
        recorder.Setup(item => item.AcquireLockAsync(
                It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .Callback<string, int, CancellationToken>((name, id, _) => acquired.Add($"{name}:{id}"))
            .Returns(Task.CompletedTask);
        recorder.Setup(item => item.AcquireLockAsync(
                It.IsAny<string>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .Callback<string, Guid, CancellationToken>((name, id, _) => acquired.Add($"{name}:{id}"))
            .Returns(Task.CompletedTask);
        recorder.Setup(item => item.FlushBusinessAsync(It.IsAny<CancellationToken>()))
            .Returns(async (CancellationToken ct) =>
                new AtomicBusinessFlush(await db.SaveChangesAsync(ct), []));
        return (recorder.Object, acquired);
    }

    private static string[] Prefix(WorkspaceReadScope scope) =>
        [$"AuthSession:{scope.SessionId}", $"WorkspaceAccessContext:{scope.AccessContextId}",
            $"Portfolio:{scope.PortfolioId}"];

    private static async Task<WorkspaceReadScope> SeedAuthorityAsync(
        RentalCommandDbContext db, string suffix)
    {
        var now = DateTime.UtcNow;
        var portfolio = new Portfolio
        {
            Name = $"Accounting {suffix}", ManagementCompanyName = "Recorder", TimeZone = "UTC",
            CreatedAt = now, UpdatedAt = now,
        };
        var user = new ApplicationUser
        {
            UserName = $"{suffix}@example.test", NormalizedUserName = $"{suffix}@example.test".ToUpperInvariant(),
            Email = $"{suffix}@example.test", NormalizedEmail = $"{suffix}@example.test".ToUpperInvariant(),
            DisplayName = suffix, CreatedAt = now,
        };
        db.AddRange(portfolio, user);
        await db.SaveChangesAsync();
        var access = new WorkspaceAccessContext
        {
            UserId = user.Id, PortfolioId = portfolio.Id, Status = WorkspaceAccessContextStatus.Active,
            CreatedAtUtc = now, UpdatedAtUtc = now,
        };
        var membership = new WorkspaceMembership
        {
            AccessContext = access, PortfolioId = portfolio.Id, Status = WorkspaceMembershipStatus.Active,
            EffectiveFromUtc = now.AddMinutes(-1), CreatedAtUtc = now, UpdatedAtUtc = now,
        };
        var session = new AuthSession
        {
            Id = Guid.NewGuid(), UserId = user.Id, ActiveAccessContext = access,
            Status = AuthSessionStatus.Active, CreatedAtUtc = now, LastSeenAtUtc = now,
            ExpiresAtUtc = now.AddHours(1),
        };
        db.AddRange(new MembershipRoleAssignment
        {
            WorkspaceMembership = membership, PortfolioId = portfolio.Id,
            RoleProfileId = AccessCatalog.Roles.Single(role => role.Key == RoleProfileKeys.WorkspaceAdministrator).Id,
            Status = MembershipRoleAssignmentStatus.Active,
            ScopeKind = MembershipRoleAssignmentScopeKind.AllProperties,
            EffectiveFromUtc = now.AddMinutes(-1), CreatedAtUtc = now, UpdatedAtUtc = now,
        }, session);
        await db.SaveChangesAsync();
        return new WorkspaceReadScope(
            portfolio.Id, user.Id, session.Id, access.Id, access.AccessRevision);
    }

    private static async Task<LedgerAccount> AddLedgerAsync(
        RentalCommandDbContext db, int portfolioId, string code, string name)
    {
        var now = DateTime.UtcNow;
        var account = new LedgerAccount
        {
            PublicId = Guid.NewGuid(), PortfolioId = portfolioId, Code = code, Name = name,
            AccountType = AccountType.Expense, NormalBalance = NormalBalance.Debit,
            ScheduleECategory = ScheduleECategory.Other, CreatedAtUtc = now, UpdatedAtUtc = now,
        };
        db.Add(account);
        await db.SaveChangesAsync();
        return account;
    }

    private static async Task<AccountingConnection> AddConnectionAsync(
        RentalCommandDbContext db, int portfolioId)
    {
        var now = DateTime.UtcNow;
        var connection = new AccountingConnection
        {
            PortfolioId = portfolioId, Provider = AccountingProvider.QuickBooks,
            Status = AccountingConnectionStatus.Connected, AccessTokenCipherText = "access",
            RefreshTokenCipherText = "refresh", TokenExpiresAt = now.AddHours(1),
            PullEnabled = true, PushEnabled = false, CreatedAt = now, UpdatedAt = now,
        };
        db.Add(connection);
        await db.SaveChangesAsync();
        return connection;
    }

    private static ServiceProvider BuildServices(string connectionString)
    {
        var services = new ServiceCollection();
        services.AddSingleton(TimeProvider.System);
        services.AddScoped<ICurrentActor, TestActor>();
        services.AddAtomicPersistenceKernel();
        services.AddScoped<IRequestWriteExecutor, RequestWriteExecutor>();
        services.AddDbContext<RentalCommandDbContext>((provider, options) =>
            options.UseNpgsql(connectionString).UseAtomicPersistenceKernel(provider));
        return services.BuildServiceProvider(
            new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
    }

    private sealed class TestActor : ICurrentActor
    {
        public int? UserId => null;
        public string? ActorLabel => "integration:accounting-write-executor";
        public string? IpAddress => "127.0.0.1";
    }
}
