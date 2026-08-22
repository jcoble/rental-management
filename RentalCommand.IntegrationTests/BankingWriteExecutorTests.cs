using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using RentalCommand.Api.Writes;
using RentalCommand.Core.Atomic;
using RentalCommand.Core.Authorization;
using RentalCommand.Core.Banking;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Interfaces;
using RentalCommand.Data;
using RentalCommand.Data.Atomic;
using RentalCommand.Data.Banking;
using RentalCommand.TestCommon;

namespace RentalCommand.IntegrationTests;

[Collection(RoleAuthorityPostgreSqlCollection.Name)]
public sealed class BankingWriteExecutorTests(MigratedPostgreSqlFixture fixture)
{
    private static readonly DateTime Now = new(2026, 8, 21, 16, 0, 0, DateTimeKind.Utc);
    private static readonly Guid SessionId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid ExchangeId = Guid.Parse("22222222-2222-2222-2222-222222222222");

    // Frozen legacy fingerprints computed once from the banking command DTO shapes at c9dae23c.
    // These values must never be regenerated from the current command model or a current helper.
    private const string PrepareFingerprint = "9325cd089ea0db489b615e80829b88a097241aa0d3768c00cb33ae07ba5c2306";
    private const string AdmitFingerprint = "fe07fac3a2c4fdb08bd17cc8f485a1d03ff6a6c8896e82fab23c3b3eb93745e4";
    private const string ReceiptFingerprint = "b660754976998f49e70016a85c07fb97cb6af775fb3f078e17b5bf667d8adef1";
    private const string ConnectionFingerprint = "fe07fac3a2c4fdb08bd17cc8f485a1d03ff6a6c8896e82fab23c3b3eb93745e4";
    private const string SyncFingerprint = "fe9db51249f914c1f6015367121054c9d8b84fc39fd168a18ab3227b4cd17af7";
    private const string ImportFingerprint = "483292435c527947f4e5e8be719036579e7c13a810bb5d3b614ac10e79b2e787";
    private const string ReconcileFingerprint = "143f0dfba0b6ee4bbc1432f861d67624a4994886e589428799a7f7483f1d3cbe";
    private const string RouteFingerprint = "d64db0fc96f7ee1e395bd2745652ce23857051eb718c668b0aef71bf9327a221";

    [Fact]
    public async Task EightRules_ExecuteRecorderLockPlansAndPreserveLegacyMetadata()
    {
        await using var db = EmptyContext();
        await AssertLocksAsync(BankingWriteSupport.Write<PreparePlaidTokenExchangeCommand,
                PreparePlaidTokenExchangeResult>(db, Prepare()),
            "banking.plaid.exchange.prepare", BankingWriteSupport.PlaidExchangePrepareResultContract,
            "AuthSession", "WorkspaceAccessContext", "BankConnection");
        await AssertLocksAsync(BankingWriteSupport.Write<AdmitPlaidTokenExchangeCommand,
                AdmitPlaidTokenExchangeResult>(db, Admit()),
            "banking.plaid.exchange.admit", BankingWriteSupport.PlaidExchangeAdmitResultContract,
            "BankConnection");
        await AssertLocksAsync(BankingWriteSupport.Write<RecordPlaidTokenExchangeReceiptCommand,
                RecordPlaidTokenExchangeReceiptResult>(db, Receipt()),
            "banking.plaid.exchange.receipt", BankingWriteSupport.PlaidExchangeReceiptResultContract,
            "BankConnection");
        await AssertLocksAsync(BankingWriteSupport.Write<ApplyPlaidConnectionCommand,
                ApplyPlaidConnectionResult>(db, Connection()),
            "banking.plaid.connection.apply", BankingWriteSupport.PlaidConnectionResultContract,
            "BankConnection");
        await AssertLocksAsync(BankingWriteSupport.Write<ApplyPlaidSyncCommand,
                ApplyPlaidSyncResult>(db, Sync()),
            "banking.plaid.sync.apply", BankingWriteSupport.PlaidSyncResultContract,
            "BankConnection");
        await AssertLocksAsync(BankingWriteSupport.Write<ImportBankTransactionsCommand,
                ImportBankTransactionsResult>(db, Import()),
            "banking.import.apply", BankingWriteSupport.ImportResultContract,
            "BankConnection");
        await AssertLocksAsync(BankingWriteSupport.Write<ReconcileBankTransactionCommand,
                ReconcileBankTransactionResult>(db, Reconcile()),
            "banking.transaction.reconcile", BankingWriteSupport.ReconciliationResultContract,
            "AuthSession", "WorkspaceAccessContext");
        await AssertLocksAsync(BankingWriteSupport.Write<RouteBankTransactionCommand,
                RouteBankTransactionResult>(db, Route()),
            "banking.transaction.route", BankingWriteSupport.RoutingResultContract,
            "AuthSession", "WorkspaceAccessContext", "BankTransaction");
    }

    [Fact]
    public async Task EightLegacyHandlerArms_AreRetired()
    {
        await using var db = EmptyContext();
        var context = Mock.Of<IAtomicCommandContext>();
        var actions = new Func<Task>[]
        {
            () => new PreparePlaidTokenExchangeHandler(db).HandleAsync(Prepare(), context, default),
            () => new AdmitPlaidTokenExchangeHandler(db).HandleAsync(Admit(), context, default),
            () => new RecordPlaidTokenExchangeReceiptHandler(db).HandleAsync(Receipt(), context, default),
            () => new ApplyPlaidConnectionHandler(db).HandleAsync(Connection(), context, default),
            () => new ApplyPlaidSyncHandler(db).HandleAsync(Sync(), context, default),
            () => new ImportBankTransactionsHandler(db).HandleAsync(Import(), context, default),
            () => new ReconcileBankTransactionHandler(db).HandleAsync(Reconcile(), context, default),
            () => new RouteBankTransactionHandler(db).HandleAsync(Route(), context, default),
        };

        foreach (var action in actions)
            await action.Should().ThrowAsync<InvalidOperationException>()
                .WithMessage("Legacy atomic banking writes are retired; use the shared write executor.");
    }

    [Fact]
    public async Task LegacyReceiptFixtures_ReplayAllEightBankingContracts()
    {
        await using var database = await fixture.CreateContextAsync();
        await SeedReplayStateAsync(database.Db);
        await using var services = Services(database.ConnectionString);

        await ReplayAsync(services,
            "8:43630806754633734D13710D2E8E0EB5D69FBFBA66B94EB8FF337558BAB37388",
            Prepare(), PrepareFingerprint,
            "{\"Outcome\":0,\"ExchangeAttemptId\":\"22222222-2222-2222-2222-222222222222\"}",
            new PreparePlaidTokenExchangeResult(PreparePlaidTokenExchangeOutcome.Prepared, ExchangeId));
        await ReplayAsync(services, $"8:{ExchangeId:N}", Admit(), AdmitFingerprint,
            "{\"Outcome\":0,\"ExchangeAttemptId\":\"22222222-2222-2222-2222-222222222222\"}",
            new AdmitPlaidTokenExchangeResult(AdmitPlaidTokenExchangeOutcome.Admitted, ExchangeId));
        await ReplayAsync(services, $"8:{ExchangeId:N}", Receipt(), ReceiptFingerprint,
            "{\"Outcome\":0,\"ExchangeAttemptId\":\"22222222-2222-2222-2222-222222222222\"}",
            new RecordPlaidTokenExchangeReceiptResult(
                RecordPlaidTokenExchangeReceiptOutcome.Recorded, ExchangeId));
        await ReplayAsync(services, $"8:{ExchangeId:N}", Connection(), ConnectionFingerprint,
            "{\"ConnectionId\":810,\"Created\":true}",
            new ApplyPlaidConnectionResult(810, true));
        await ReplayAsync(services,
            "8:810:2393429A406F72713F04DCEAB858AC03A88A051576680A500E91E5EB468C3F56",
            Sync(), SyncFingerprint,
            "{\"Outcome\":0,\"ConnectionId\":810,\"ImportedCount\":1,\"SkippedCount\":0,\"AffectedTransactionIds\":[812]}",
            new ApplyPlaidSyncResult(ApplyPlaidSyncOutcome.Applied, 810, 1, 0, [812]));
        await ReplayAsync(services, $"8:{new string('A', 64)}", Import(), ImportFingerprint,
            "{\"ConnectionId\":811,\"ImportedCount\":1,\"SkippedCount\":0,\"ImportedTransactionIds\":[812],\"StatementId\":null,\"StatementPeriodStart\":null,\"StatementPeriodEnd\":null,\"StatementOpeningBalance\":null,\"StatementClosingBalance\":null,\"StatementMovement\":null,\"StatementIsoCurrencyCode\":null}",
            new ImportBankTransactionsResult(811, 1, 0, [812]));
        await ReplayAsync(services, "8:9:812: reconcile exact ", Reconcile(), ReconcileFingerprint,
            "{\"Outcome\":2,\"TransactionId\":812,\"Transaction\":null}",
            new ReconcileBankTransactionResult(ReconcileBankTransactionOutcome.TransactionNotFound, 812));
        await ReplayAsync(services, "8:9:812: route exact ", Route(), RouteFingerprint,
            "{\"Outcome\":0,\"TransactionId\":812}",
            new RouteBankTransactionResult(RouteBankTransactionOutcome.Applied, 812));
    }

    [Fact]
    public async Task LegacyPrepareReplay_RefusesRevokedSession()
    {
        await using var database = await fixture.CreateContextAsync();
        await SeedReplayStateAsync(database.Db);
        await database.Db.AuthSessions.Where(row => row.Id == SessionId)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(row => row.Status, AuthSessionStatus.Revoked)
                .SetProperty(row => row.RevokedAtUtc, Now));
        await using var services = Services(database.ConnectionString);

        var action = () => ReplayAsync(services,
            "8:43630806754633734D13710D2E8E0EB5D69FBFBA66B94EB8FF337558BAB37388",
            Prepare(), PrepareFingerprint,
            "{\"Outcome\":0,\"ExchangeAttemptId\":\"22222222-2222-2222-2222-222222222222\"}",
            new PreparePlaidTokenExchangeResult(PreparePlaidTokenExchangeOutcome.Prepared, ExchangeId));

        await action.Should().ThrowAsync<UnauthorizedAccessException>();
    }

    [Fact]
    public void Fingerprints_PreserveEveryLegacyIgnoreExclusion()
    {
        AtomicCommandFingerprint.Create(Prepare()).Should().Be(PrepareFingerprint);
        AtomicCommandFingerprint.Create(Admit()).Should().Be(AdmitFingerprint);
        AtomicCommandFingerprint.Create(Receipt()).Should().Be(ReceiptFingerprint);
        AtomicCommandFingerprint.Create(Connection()).Should().Be(ConnectionFingerprint);
        AtomicCommandFingerprint.Create(Sync()).Should().Be(SyncFingerprint);
        AtomicCommandFingerprint.Create(Import()).Should().Be(ImportFingerprint);
        AtomicCommandFingerprint.Create(Reconcile()).Should().Be(ReconcileFingerprint);
        AtomicCommandFingerprint.Create(Route()).Should().Be(RouteFingerprint);

        AtomicCommandFingerprint.Create(Prepare() with
        {
            AuthSessionId = Guid.NewGuid(), AccessContextId = 99, ExpectedAccessRevision = 99,
            ExternalAccountIdCipherText = "changed", PreparedAtUtc = Now.AddDays(1),
        }).Should().Be(PrepareFingerprint);
        AtomicCommandFingerprint.Create(Admit() with { AdmittedAtUtc = Now.AddDays(1) })
            .Should().Be(AdmitFingerprint);
        AtomicCommandFingerprint.Create(Receipt() with { RecordedAtUtc = Now.AddDays(1) })
            .Should().Be(ReceiptFingerprint);
        AtomicCommandFingerprint.Create(Connection() with { AppliedAtUtc = Now.AddDays(1) })
            .Should().Be(ConnectionFingerprint);
        AtomicCommandFingerprint.Create(Sync() with { AppliedAtUtc = Now.AddDays(1) })
            .Should().Be(SyncFingerprint);
        AtomicCommandFingerprint.Create(Import() with { ImportedAtUtc = Now.AddDays(1) })
            .Should().Be(ImportFingerprint);
        AtomicCommandFingerprint.Create(Reconcile() with
        {
            AppliedAtUtc = Now.AddDays(1), AuthSessionId = Guid.NewGuid(), AccessContextId = 99,
            ExpectedAccessRevision = 99, ResolvedSuggestionTransferUpdatedAtUtc = Now.AddDays(2),
        }).Should().Be(ReconcileFingerprint);
        AtomicCommandFingerprint.Create(Route() with
        {
            AppliedAtUtc = Now.AddDays(1), AuthSessionId = Guid.NewGuid(), AccessContextId = 99,
            ExpectedAccessRevision = 99,
        }).Should().Be(RouteFingerprint);
    }

    private static async Task AssertLocksAsync<TCommand, TResult>(
        TransactionalWrite<TCommand, TResult> write,
        string operation,
        string contract,
        params string[] expectedLocks)
        where TCommand : notnull, IAtomicCommandData
        where TResult : notnull
    {
        var context = new Mock<IAtomicCommandContext>();
        var acquired = new List<string>();
        context.Setup(value => value.AcquireLockAsync(
                It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .Callback<string, int, CancellationToken>((name, _, _) => acquired.Add(name))
            .Returns(Task.CompletedTask);
        context.Setup(value => value.AcquireLockAsync(
                It.IsAny<string>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .Callback<string, Guid, CancellationToken>((name, _, _) => acquired.Add(name))
            .Returns(Task.CompletedTask);

        foreach (var writeLock in write.LockPlan.Locks)
            await writeLock.AcquireAsync(context.Object);

        write.OperationName.Should().Be(operation);
        write.ResultContract.Should().Be(contract);
        acquired.Should().Equal(expectedLocks);
    }

    private static async Task ReplayAsync<TCommand, TResult>(
        ServiceProvider services,
        string key,
        TCommand command,
        string fingerprint,
        string resultJson,
        TResult expected)
        where TCommand : notnull, IAtomicCommandData
        where TResult : notnull
    {
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<RentalCommandDbContext>();
        var write = BankingWriteSupport.Write<TCommand, TResult>(db, command);
        db.AtomicCommandReceipts.Add(new AtomicCommandReceipt
        {
            Id = Guid.NewGuid(), AttemptId = Guid.NewGuid(), CommandType = write.OperationName,
            IdempotencyKey = key, RequestFingerprint = fingerprint,
            Status = AtomicCommandReceiptStatus.Completed, ResultContract = write.ResultContract,
            ResultJson = resultJson, StartedAt = Now, CompletedAt = Now,
        });
        await db.SaveChangesAsync();

        var outcome = await scope.ServiceProvider.GetRequiredService<IRequestWriteExecutor>()
            .ExecuteExactAsync(key, write);
        outcome.Disposition.Should().Be(AtomicCommandDisposition.Replayed);
        outcome.Value.Should().BeEquivalentTo(expected);
    }

    private static async Task SeedReplayStateAsync(RentalCommandDbContext db)
    {
        db.Add(new ApplicationUser
        {
            Id = 7, UserName = "banking@example.test", NormalizedUserName = "BANKING@EXAMPLE.TEST",
            Email = "banking@example.test", NormalizedEmail = "BANKING@EXAMPLE.TEST",
            DisplayName = "Banking", SecurityStamp = "stamp", ConcurrencyStamp = "concurrency",
            CreatedAt = Now.AddDays(-2),
        });
        db.Add(new Portfolio
        {
            Id = 8, Name = "Banking", ManagementCompanyName = "Banking Company",
            CreatedAt = Now.AddDays(-2), UpdatedAt = Now.AddDays(-2),
        });
        await db.SaveChangesAsync();
        var access = new WorkspaceAccessContext
        {
            Id = 9, UserId = 7, PortfolioId = 8, Status = WorkspaceAccessContextStatus.Active,
            CreatedAtUtc = Now.AddDays(-2), UpdatedAtUtc = Now.AddDays(-2),
        };
        access.AdvanceRevision(1);
        access.AdvanceRevision(2);
        db.Add(access);
        await db.SaveChangesAsync();
        var membership = new WorkspaceMembership
        {
            AccessContextId = 9, PortfolioId = 8, Status = WorkspaceMembershipStatus.Active,
            DefaultExperience = WorkspaceExperience.Management, EffectiveFromUtc = Now.AddDays(-1),
            CreatedAtUtc = Now.AddDays(-1), UpdatedAtUtc = Now.AddDays(-1),
        };
        db.Add(membership);
        await db.SaveChangesAsync();
        db.Add(new MembershipRoleAssignment
        {
            WorkspaceMembershipId = membership.Id, PortfolioId = 8, RoleProfileId = 1,
            Status = MembershipRoleAssignmentStatus.Active,
            ScopeKind = MembershipRoleAssignmentScopeKind.AllProperties,
            EffectiveFromUtc = Now.AddDays(-1), CreatedAtUtc = Now.AddDays(-1), UpdatedAtUtc = Now.AddDays(-1),
        });
        db.Add(new AuthSession
        {
            Id = SessionId, UserId = 7, ActiveAccessContextId = 9, Status = AuthSessionStatus.Active,
            CreatedAtUtc = Now, LastSeenAtUtc = Now, ExpiresAtUtc = Now.AddDays(30),
        });
        db.AddRange(
            new BankConnection
            {
                Id = 810, PortfolioId = 8, Provider = "Plaid", InstitutionName = "Plaid bank",
                AccountName = "Checking", Status = "Active", CreatedAt = Now, UpdatedAt = Now,
            },
            new BankConnection
            {
                Id = 811, PortfolioId = 8, Provider = "Manual", InstitutionName = "Manual bank",
                AccountName = "Operating", AccountMask = "1234", Status = "Active",
                CreatedAt = Now, UpdatedAt = Now,
            });
        await db.SaveChangesAsync();
        db.Add(new PlaidTokenExchangeAttempt
        {
            Id = ExchangeId, PortfolioId = 8, ClientOperationId = "client-operation",
            RequestHash = new string('1', 64), PublicTokenHash = new string('2', 64),
            InstitutionName = "Plaid bank", AccountName = "Checking",
            ExternalAccountIdCipherText = "account-cipher", ExternalAccountIdHash = new string('3', 64),
            Status = "Completed", PreparedAtUtc = Now, RemoteAdmittedAtUtc = Now,
            RemoteReceiptRecordedAtUtc = Now, CompletedAtUtc = Now,
            ProviderRequestIdentity = "provider-request", ExternalItemIdCipherText = "item-cipher",
            ExternalItemIdHash = new string('4', 64), ExternalAccessTokenCipherText = "token-cipher",
            BankConnectionId = 810,
        });
        db.Add(new BankTransaction
        {
            Id = 812, PortfolioId = 8, BankConnectionId = 810,
            ProviderTransactionId = "provider-transaction", PostedAt = Now,
            Description = "Bank transaction", Amount = 25m, IsoCurrencyCode = "USD",
            MatchStatus = "Unmatched", CreatedAt = Now, UpdatedAt = Now,
        });
        await db.SaveChangesAsync();
    }

    private static ServiceProvider Services(string connectionString)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(TimeProvider.System);
        services.AddScoped<ICurrentActor, TestActor>();
        services.AddAtomicPersistenceKernel();
        services.AddScoped<IRequestWriteExecutor, RequestWriteExecutor>();
        services.AddDbContext<RentalCommandDbContext>((provider, options) =>
            options.UseNpgsql(connectionString).UseAtomicPersistenceKernel(provider));
        return services.BuildServiceProvider();
    }

    private static RentalCommandDbContext EmptyContext() => new(
        new DbContextOptionsBuilder<RentalCommandDbContext>().Options);

    private static PreparePlaidTokenExchangeCommand Prepare() => new(
        8, 7, SessionId, 9, 3, CapabilityKeys.BankConnectionsManage, "client-operation",
        new string('1', 64), new string('2', 64), "Plaid bank", "Checking", "1234",
        "depository", "checking", "account-cipher", new string('3', 64), Now);
    private static AdmitPlaidTokenExchangeCommand Admit() => new(8, ExchangeId, Now);
    private static RecordPlaidTokenExchangeReceiptCommand Receipt() => new(
        8, ExchangeId, "provider-request", "item-cipher", new string('4', 64), "token-cipher", Now);
    private static ApplyPlaidConnectionCommand Connection() => new(8, ExchangeId, Now);
    private static ApplyPlaidSyncCommand Sync() => new(
        8, 810, null, "cursor-next", [TransactionInput()], 1, [], 0, [], "provider-request", Now);
    private static ImportBankTransactionsCommand Import() => new(
        8, "Manual", "Manual bank", "Operating", "1234", "depository", "checking",
        [TransactionInput()], 1, new string('A', 64), Now);
    private static ReconcileBankTransactionCommand Reconcile() => new(
        8, 812, BankReconciliationAction.Ignore, null, null, null, null, null, null, null,
        Now, Now, 7, SessionId, 9, 3, CapabilityKeys.MoneyReconciliationOperate,
        " reconcile exact ");
    private static RouteBankTransactionCommand Route() => new(
        8, 812, null, Now, Now, 7, SessionId, 9, 3, " route exact ");
    private static BankTransactionInput TransactionInput() => new(
        "provider-transaction", Now, null, "Bank transaction", null, 25m, "USD", null, null);

    private sealed class TestActor : ICurrentActor
    {
        public int? UserId => 7;
        public string? ActorLabel => "integration:banking-write-executor";
        public string? IpAddress => "127.0.0.1";
    }
}
