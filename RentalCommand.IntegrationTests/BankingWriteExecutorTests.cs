using Microsoft.AspNetCore.DataProtection;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Moq;
using RentalCommand.Api.DTOs;
using RentalCommand.Api.Services.Domain;
using RentalCommand.Core.Atomic;
using RentalCommand.Core.Authorization;
using RentalCommand.Core.Banking;
using RentalCommand.Core.Configuration;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Interfaces;
using RentalCommand.Data;
using RentalCommand.Data.Atomic;
using RentalCommand.Data.Banking;
using RentalCommand.TestCommon;

namespace RentalCommand.IntegrationTests;

[Collection(RoleAuthorityPostgreSqlCollection3.Name)]
public sealed class BankingWriteExecutorTests(MigratedPostgreSqlFixture fixture)
{
    private static readonly DateTime Now = new(2026, 8, 21, 16, 0, 0, DateTimeKind.Utc);
    private static readonly Guid SessionId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid ExchangeId = Guid.Parse("22222222-2222-2222-2222-222222222222");

    // Frozen legacy fingerprints computed once from caller-reachable banking commands at c9dae23c.
    // These values must never be regenerated from the current command model or a current helper.
    private const string PrepareFingerprint = "10aaaaa4930e65eb179a8070cd0e9430ad2d1b54f3e038e0728a331f3c4a669c";
    private const string AdmitFingerprint = "fe07fac3a2c4fdb08bd17cc8f485a1d03ff6a6c8896e82fab23c3b3eb93745e4";
    private const string ReceiptFingerprint = "1f4425d6318d439343532a9eb918e22a364812c03301ee344c7247eccd1282ab";
    private const string ConnectionFingerprint = "fe07fac3a2c4fdb08bd17cc8f485a1d03ff6a6c8896e82fab23c3b3eb93745e4";
    private const string SyncFingerprint = "0934c5515c854bc26651852fea2c8a3c2bb768a48013e79665198b69667d049a";
    private const string ImportFingerprint = "0963c107033347b1ba50dbf5cc8c38f1ae7f73de9a7d709c89c1461a0aac5c52";
    private const string ReconcileFingerprint = "01fc85703ce95860dcac62b3708fb433e878029d7b0040b9df2b5674f6619886";
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
    public async Task LegacyReceiptFixtures_ReplayAllEightBankingContracts()
    {
        await using var database = await fixture.CreateContextAsync();
        await SeedReplayStateAsync(database.Db);
        var plaid = new Mock<IPlaidBankingProvider>();
        plaid.Setup(value => value.ExchangePublicTokenAsync(
                It.IsAny<PlaidRuntimeSettings>(), "public-token", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PlaidExchangeResult("access-token", "item-id", "provider-request"));
        plaid.Setup(value => value.SyncTransactionsAsync(
                It.IsAny<PlaidRuntimeSettings>(), "access-token", null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PlaidTransactionsSyncResult(
                "cursor-next",
                [new PlaidSyncedTransaction(
                    "provider-transaction", "account-id", Now, null, "Bank transaction", null,
                    -25m, "USD", null, null!)],
                [], [], "provider-request"));
        await using var services = Services(database.ConnectionString, plaid.Object);
        // Frozen caller digests calculated once from the concrete inputs below; never regenerate.
        const string prepareKey =
            "8:43630806754633734D13710D2E8E0EB5D69FBFBA66B94EB8FF337558BAB37388";
        const string syncKey =
            "8:810:2393429A406F72713F04DCEAB858AC03A88A051576680A500E91E5EB468C3F56";
        const string importKey =
            "8:A951CCB6D1493A26E6CBA360A076E3E287616CFA970D5F0C709367630A0E4A7B";
        var exchangeKey = $"8:{ExchangeId:N}";

        await SeedReceiptAsync(services, "banking.plaid.exchange.prepare", prepareKey,
            PrepareFingerprint, BankingWriteSupport.PlaidExchangePrepareResultContract,
            "{\"Outcome\":0,\"ExchangeAttemptId\":\"22222222-2222-2222-2222-222222222222\"}",
            Now);
        await SeedReceiptAsync(services, "banking.plaid.exchange.admit", exchangeKey,
            AdmitFingerprint, BankingWriteSupport.PlaidExchangeAdmitResultContract,
            "{\"Outcome\":0,\"ExchangeAttemptId\":\"22222222-2222-2222-2222-222222222222\"}",
            Now);
        await SeedReceiptAsync(services, "banking.plaid.exchange.receipt", exchangeKey,
            ReceiptFingerprint, BankingWriteSupport.PlaidExchangeReceiptResultContract,
            "{\"Outcome\":0,\"ExchangeAttemptId\":\"22222222-2222-2222-2222-222222222222\"}",
            Now);
        await SeedReceiptAsync(services, "banking.plaid.connection.apply", exchangeKey,
            ConnectionFingerprint, BankingWriteSupport.PlaidConnectionResultContract,
            "{\"ConnectionId\":810,\"Created\":true}",
            Now);
        await SeedReceiptAsync(services, "banking.plaid.sync.apply", syncKey,
            SyncFingerprint, BankingWriteSupport.PlaidSyncResultContract,
            "{\"Outcome\":0,\"ConnectionId\":810,\"ImportedCount\":1,\"SkippedCount\":0,\"AffectedTransactionIds\":[812]}",
            Now);
        await SeedReceiptAsync(services, "banking.import.apply", importKey,
            ImportFingerprint, BankingWriteSupport.ImportResultContract,
            "{\"ConnectionId\":811,\"ImportedCount\":1,\"SkippedCount\":0,\"ImportedTransactionIds\":[812],\"StatementId\":null,\"StatementPeriodStart\":null,\"StatementPeriodEnd\":null,\"StatementOpeningBalance\":null,\"StatementClosingBalance\":null,\"StatementMovement\":null,\"StatementIsoCurrencyCode\":null}",
            Now);
        await SeedReceiptAsync(services, "banking.transaction.reconcile",
            "8:9:812: reconcile exact ", ReconcileFingerprint,
            BankingWriteSupport.ReconciliationResultContract,
            "{\"Outcome\":2,\"TransactionId\":812,\"Transaction\":null}",
            Now);
        await SeedReceiptAsync(services, "banking.transaction.route", "8:9:812: route exact ",
            RouteFingerprint, BankingWriteSupport.RoutingResultContract,
            "{\"Outcome\":0,\"TransactionId\":812}",
            Now);

        await using var callerScope = services.CreateAsyncScope();
        var service = callerScope.ServiceProvider.GetRequiredService<BankingService>();
        var request = ExchangeRequest();
        (await service.ExchangePlaidPublicTokenAsync(FrozenScope(), request)).Id.Should().Be(810);

        var db = callerScope.ServiceProvider.GetRequiredService<RentalCommandDbContext>();
        await db.PlaidTokenExchangeAttempts.Where(row => row.Id == ExchangeId)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(row => row.Status, "Prepared")
                .SetProperty(row => row.RemoteAdmittedAtUtc, (DateTime?)null)
                .SetProperty(row => row.RemoteReceiptRecordedAtUtc, (DateTime?)null)
                .SetProperty(row => row.CompletedAtUtc, (DateTime?)null));
        await FluentActions.Invoking(() => service.ExchangePlaidPublicTokenAsync(FrozenScope(), request))
            .Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*already owned by an admitted request*");

        (await service.ImportAsync(8, ImportRequest())).ImportedCount.Should().Be(1);
        (await service.IgnoreTransactionAsync(FrozenScope(), 812,
            new BankTransactionMutationRequest
            {
                OperationKey = " reconcile exact ", ExpectedUpdatedAtUtc = Now,
            })).Should().BeNull();
        (await service.RouteTransactionAsync(FrozenScope(), 812,
            new RouteBankTransactionRequest
            {
                OperationKey = " route exact ", ExpectedUpdatedAtUtc = Now,
            })).Should().NotBeNull();
        (await service.SyncPlaidConnectionAsync(8, 810))!.ImportedCount.Should().Be(1);

        db.ChangeTracker.Clear();
        (await db.AtomicCommandReceipts.CountAsync(row => row.CommandType.StartsWith("banking.")))
            .Should().Be(8);
        (await db.PlaidTokenExchangeAttempts.CountAsync(row => row.Id == ExchangeId)).Should().Be(1);
        (await db.BankConnections.CountAsync(row => row.PortfolioId == 8)).Should().Be(2);
        (await db.BankTransactions.CountAsync(row => row.PortfolioId == 8)).Should().Be(1);
        (await db.BankTransactions.CountAsync(row => row.Id == 812
            && row.PropertyId == null && row.MatchStatus == "Unmatched")).Should().Be(1);

        await using var receiptDatabase = await fixture.CreateContextAsync();
        await SeedReplayStateAsync(receiptDatabase.Db);
        await receiptDatabase.Db.PlaidTokenExchangeAttempts.Where(row => row.Id == ExchangeId)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(row => row.Status, "Prepared")
                .SetProperty(row => row.RemoteAdmittedAtUtc, (DateTime?)null)
                .SetProperty(row => row.RemoteReceiptRecordedAtUtc, (DateTime?)null)
                .SetProperty(row => row.CompletedAtUtc, (DateTime?)null));
        await using var receiptServices = Services(receiptDatabase.ConnectionString, plaid.Object);
        await SeedReceiptAsync(receiptServices, "banking.plaid.exchange.prepare", prepareKey,
            PrepareFingerprint, BankingWriteSupport.PlaidExchangePrepareResultContract,
            "{\"Outcome\":0,\"ExchangeAttemptId\":\"22222222-2222-2222-2222-222222222222\"}", Now);
        await SeedReceiptAsync(receiptServices, "banking.plaid.exchange.receipt", exchangeKey,
            ReceiptFingerprint, BankingWriteSupport.PlaidExchangeReceiptResultContract,
            "{\"Outcome\":0,\"ExchangeAttemptId\":\"22222222-2222-2222-2222-222222222222\"}", Now);
        await SeedReceiptAsync(receiptServices, "banking.plaid.connection.apply", exchangeKey,
            ConnectionFingerprint, BankingWriteSupport.PlaidConnectionResultContract,
            "{\"ConnectionId\":810,\"Created\":true}", Now);
        await SeedReceiptAsync(receiptServices, "banking.plaid.sync.apply", syncKey,
            SyncFingerprint, BankingWriteSupport.PlaidSyncResultContract,
            "{\"Outcome\":0,\"ConnectionId\":810,\"ImportedCount\":1,\"SkippedCount\":0,\"AffectedTransactionIds\":[812]}", Now);
        await using var receiptScope = receiptServices.CreateAsyncScope();
        await FluentActions.Invoking(() => receiptScope.ServiceProvider.GetRequiredService<BankingService>()
                .ExchangePlaidPublicTokenAsync(FrozenScope(), request))
            .Should().ThrowAsync<UnauthorizedAccessException>()
            .WithMessage("*applied Plaid connection exchange is unavailable*");
        var receiptDb = receiptScope.ServiceProvider.GetRequiredService<RentalCommandDbContext>();
        (await receiptDb.AtomicCommandReceipts.CountAsync(row => row.CommandType.StartsWith("banking.")))
            .Should().Be(5, "admission executes once while the other four stages replay");
        (await receiptDb.BankConnections.CountAsync(row => row.PortfolioId == 8)).Should().Be(2);
        (await receiptDb.BankTransactions.CountAsync(row => row.PortfolioId == 8)).Should().Be(1);
        plaid.Verify(value => value.ExchangePublicTokenAsync(
            It.IsAny<PlaidRuntimeSettings>(), "public-token", It.IsAny<CancellationToken>()), Times.Once);
        plaid.Verify(value => value.SyncTransactionsAsync(
            It.IsAny<PlaidRuntimeSettings>(), "access-token", null, It.IsAny<CancellationToken>()), Times.Once);
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

        await SeedReceiptAsync(services, "banking.plaid.exchange.prepare",
            "8:43630806754633734D13710D2E8E0EB5D69FBFBA66B94EB8FF337558BAB37388",
            PrepareFingerprint, BankingWriteSupport.PlaidExchangePrepareResultContract,
            "{\"Outcome\":0,\"ExchangeAttemptId\":\"22222222-2222-2222-2222-222222222222\"}", Now);
        await using var scope = services.CreateAsyncScope();
        var action = () => scope.ServiceProvider.GetRequiredService<BankingService>()
            .ExchangePlaidPublicTokenAsync(FrozenScope(), ExchangeRequest());

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

    private static async Task SeedReceiptAsync(
        ServiceProvider services,
        string operation,
        string key,
        string fingerprint,
        string contract,
        string resultJson,
        DateTime completedAt)
    {
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<RentalCommandDbContext>();
        db.AtomicCommandReceipts.Add(new AtomicCommandReceipt
        {
            Id = Guid.NewGuid(), AttemptId = Guid.NewGuid(), CommandType = operation,
            IdempotencyKey = key, RequestFingerprint = fingerprint,
            Status = AtomicCommandReceiptStatus.Completed, ResultContract = contract,
            ResultJson = resultJson, StartedAt = completedAt, CompletedAt = completedAt,
        });
        await db.SaveChangesAsync();
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
                AccountName = "Checking", AccountMask = "1234", AccountType = "depository",
                AccountSubtype = "checking", Status = "Active",
                ExternalAccountIdCipherText = "cHJvdGVjdGVkOmFjY291bnQtaWQ",
                ExternalAccountIdHash = "61F852A6454401360C03BCBC609F4662CC767F009F206550448439DE3F4A7FB2",
                ExternalItemIdCipherText = "cHJvdGVjdGVkOml0ZW0taWQ",
                ExternalItemIdHash = "86B30AD6DB41093E7E36E495C42F2E7BF9CCBFEF54E7189C3A5AEB7A9CCC7E1E",
                ExternalAccessTokenCipherText = "cHJvdGVjdGVkOmFjY2Vzcy10b2tlbg",
                CreatedAt = Now, UpdatedAt = Now,
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
            RequestHash = "CD5B6A33C0C6C7407D4DE04A91005296C64B5B2AEBF8F65B91200E77E4DA799F",
            PublicTokenHash = "0A11C586276E130D0BCD50C28EA06B6AEB63F2E99D0E41C56CF4E4178AE2590A",
            InstitutionName = "Plaid bank", AccountName = "Checking",
            AccountMask = "1234", AccountType = "depository", AccountSubtype = "checking",
            ExternalAccountIdCipherText = "cHJvdGVjdGVkOmFjY291bnQtaWQ",
            ExternalAccountIdHash = "61F852A6454401360C03BCBC609F4662CC767F009F206550448439DE3F4A7FB2",
            Status = "Completed", PreparedAtUtc = Now, RemoteAdmittedAtUtc = Now,
            RemoteReceiptRecordedAtUtc = Now, CompletedAtUtc = Now,
            ProviderRequestIdentity = "provider-request",
            ExternalItemIdCipherText = "cHJvdGVjdGVkOml0ZW0taWQ",
            ExternalItemIdHash = "86B30AD6DB41093E7E36E495C42F2E7BF9CCBFEF54E7189C3A5AEB7A9CCC7E1E",
            ExternalAccessTokenCipherText = "cHJvdGVjdGVkOmFjY2Vzcy10b2tlbg",
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

    private static ServiceProvider Services(
        string connectionString,
        IPlaidBankingProvider? plaid = null)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<TimeProvider>(new FixedTimeProvider(Now));
        services.AddSingleton<IDataProtectionProvider, FixedDataProtectionProvider>();
        services.AddSingleton(plaid ?? Mock.Of<IPlaidBankingProvider>());
        services.AddSingleton(Options.Create(new PlaidOptions
        {
            Environment = "sandbox", ClientId = "client-id", Secret = "secret",
        }));
        services.AddScoped<ICurrentActor, TestActor>();
        services.AddAtomicPersistenceKernel();
        services.AddScoped<BankingService>();
        services.AddDbContext<RentalCommandDbContext>((provider, options) =>
            options.UseNpgsql(connectionString).UseAtomicPersistenceKernel(provider));
        return services.BuildServiceProvider();
    }

    private static RentalCommandDbContext EmptyContext() => new(
        new DbContextOptionsBuilder<RentalCommandDbContext>().Options);

    private static PreparePlaidTokenExchangeCommand Prepare() => new(
        8, 7, SessionId, 9, 3, CapabilityKeys.BankConnectionsManage, "client-operation",
        "CD5B6A33C0C6C7407D4DE04A91005296C64B5B2AEBF8F65B91200E77E4DA799F",
        "0A11C586276E130D0BCD50C28EA06B6AEB63F2E99D0E41C56CF4E4178AE2590A",
        "Plaid bank", "Checking", "1234", "depository", "checking",
        "cHJvdGVjdGVkOmFjY291bnQtaWQ",
        "61F852A6454401360C03BCBC609F4662CC767F009F206550448439DE3F4A7FB2", Now);
    private static AdmitPlaidTokenExchangeCommand Admit() => new(8, ExchangeId, Now);
    private static RecordPlaidTokenExchangeReceiptCommand Receipt() => new(
        8, ExchangeId, "provider-request", "cHJvdGVjdGVkOml0ZW0taWQ",
        "86B30AD6DB41093E7E36E495C42F2E7BF9CCBFEF54E7189C3A5AEB7A9CCC7E1E",
        "cHJvdGVjdGVkOmFjY2Vzcy10b2tlbg", Now);
    private static ApplyPlaidConnectionCommand Connection() => new(8, ExchangeId, Now);
    private static ApplyPlaidSyncCommand Sync() => new(
        8, 810, null, "cHJvdGVjdGVkOmN1cnNvci1uZXh0",
        [TransactionInput()], 1, [], 0, [], "provider-request", Now);
    private static ImportBankTransactionsCommand Import() => new(
        8, "Manual", "Manual bank", "Operating", "1234", "depository", "checking",
        [TransactionInput()], 1,
        "A951CCB6D1493A26E6CBA360A076E3E287616CFA970D5F0C709367630A0E4A7B", Now);
    private static ReconcileBankTransactionCommand Reconcile() => new(
        8, 812, BankReconciliationAction.Ignore, null, null, null, null, null, null, null,
        Now, Now, 7, SessionId, 9, 3, CapabilityKeys.MoneyReconciliationDestructive,
        " reconcile exact ");
    private static RouteBankTransactionCommand Route() => new(
        8, 812, null, Now, Now, 7, SessionId, 9, 3, " route exact ");
    private static BankTransactionInput TransactionInput() => new(
        "provider-transaction", Now, null, "Bank transaction", null, 25m, "USD", null, null);

    private static WorkspaceReadScope FrozenScope() => new(8, 7, SessionId, 9, 3);

    private static ExchangePlaidPublicTokenRequest ExchangeRequest() => new()
    {
        ClientOperationId = "client-operation",
        PublicToken = "public-token",
        InstitutionName = "Plaid bank",
        AccountId = "account-id",
        AccountName = "Checking",
        AccountMask = "1234",
        AccountType = "depository",
        AccountSubtype = "checking",
    };

    private static ImportBankTransactionsRequest ImportRequest() => new()
    {
        Provider = "Manual",
        InstitutionName = "Manual bank",
        AccountName = "Operating",
        AccountMask = "1234",
        AccountType = "depository",
        AccountSubtype = "checking",
        Transactions =
        [
            new ImportBankTransactionItem
            {
                ProviderTransactionId = "provider-transaction",
                PostedAt = Now,
                Description = "Bank transaction",
                Amount = 25m,
                IsoCurrencyCode = "USD",
            },
        ],
    };

    private sealed class FixedTimeProvider(DateTime now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(now);
    }

    private sealed class FixedDataProtectionProvider : IDataProtectionProvider, IDataProtector
    {
        public IDataProtector CreateProtector(string purpose) => this;
        public byte[] Protect(byte[] plaintext) => "protected:"u8.ToArray().Concat(plaintext).ToArray();
        public byte[] Unprotect(byte[] protectedData) => protectedData["protected:"u8.Length..];
    }

    private sealed class TestActor : ICurrentActor
    {
        public int? UserId => 7;
        public string? ActorLabel => "integration:banking-write-executor";
        public string? IpAddress => "127.0.0.1";
    }
}
