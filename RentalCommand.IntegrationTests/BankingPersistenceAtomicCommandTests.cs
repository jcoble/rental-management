using System.Collections.Concurrent;
using System.Data.Common;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using RentalCommand.Core.Atomic;
using RentalCommand.Core.Banking;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Interfaces;
using RentalCommand.Data;
using RentalCommand.Data.Atomic;
using RentalCommand.Data.Banking;
using Testcontainers.PostgreSql;
using Xunit;

namespace RentalCommand.IntegrationTests;

public sealed class BankingPersistenceAtomicCommandTests : IAsyncLifetime
{
    private static readonly AtomicJsonResultCodec<PreparePlaidTokenExchangeResult> ExchangePrepareCodec =
        new("banking.plaid.exchange.prepare.result.v1");
    private static readonly AtomicJsonResultCodec<AdmitPlaidTokenExchangeResult> ExchangeAdmitCodec =
        new("banking.plaid.exchange.admit.result.v1");
    private static readonly AtomicJsonResultCodec<RecordPlaidTokenExchangeReceiptResult> ExchangeReceiptCodec =
        new("banking.plaid.exchange.receipt.result.v1");
    private static readonly AtomicJsonResultCodec<ApplyPlaidConnectionResult> ConnectionCodec =
        new("banking.plaid.connection.result.v1");
    private static readonly AtomicJsonResultCodec<ImportBankTransactionsResult> ImportCodec =
        new("banking.import.result.v1");
    private static readonly AtomicJsonResultCodec<ApplyPlaidSyncResult> SyncCodec =
        new("banking.plaid.sync.result.v1");
    private static readonly AtomicJsonResultCodec<ReconcileBankTransactionResult> ReconcileCodec =
        new("banking.reconciliation.result.v1");
    private readonly DateTime _now = new(2026, 7, 11, 18, 0, 0, DateTimeKind.Utc);
    private PostgreSqlContainer? _postgres;
    private ServiceProvider? _services;
    private bool _dockerAvailable;
    private int _portfolioId;

    public async Task InitializeAsync()
    {
        try
        {
            _postgres = new PostgreSqlBuilder()
                .WithImage("postgres:16-alpine")
                .WithDatabase("rentalcommand_banking_atomic")
                .WithUsername("postgres")
                .WithPassword("postgres")
                .Build();
            await _postgres.StartAsync();
            _dockerAvailable = true;
        }
        catch
        {
            return;
        }

        var services = new ServiceCollection();
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<CommandRecorder>();
        services.AddSingleton<NotificationFailureInterceptor>();
        services.AddScoped<ICurrentActor, TestActor>();
        services.AddAtomicPersistenceKernel();
        services.AddAtomicCommandHandler<PreparePlaidTokenExchangeCommand, PreparePlaidTokenExchangeResult, PreparePlaidTokenExchangeHandler>();
        services.AddAtomicCommandHandler<AdmitPlaidTokenExchangeCommand, AdmitPlaidTokenExchangeResult, AdmitPlaidTokenExchangeHandler>();
        services.AddAtomicCommandHandler<RecordPlaidTokenExchangeReceiptCommand, RecordPlaidTokenExchangeReceiptResult, RecordPlaidTokenExchangeReceiptHandler>();
        services.AddAtomicCommandHandler<ApplyPlaidConnectionCommand, ApplyPlaidConnectionResult, ApplyPlaidConnectionHandler>();
        services.AddAtomicCommandHandler<ApplyPlaidSyncCommand, ApplyPlaidSyncResult, ApplyPlaidSyncHandler>();
        services.AddAtomicCommandHandler<ImportBankTransactionsCommand, ImportBankTransactionsResult, ImportBankTransactionsHandler>();
        services.AddAtomicCommandHandler<ReconcileBankTransactionCommand, ReconcileBankTransactionResult, ReconcileBankTransactionHandler>();
        services.AddDbContext<RentalCommandDbContext>((provider, options) =>
            options.UseNpgsql(_postgres!.GetConnectionString())
                .UseAtomicPersistenceKernel(provider)
                .AddInterceptors(
                    provider.GetRequiredService<CommandRecorder>(),
                    provider.GetRequiredService<NotificationFailureInterceptor>()));
        _services = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true,
        });

        await using var db = NewContext();
        await db.Database.MigrateAsync();
        var portfolio = new Portfolio
        {
            Name = "Banking atomic portfolio",
            ManagementCompanyName = "Banking atomic test",
            TimeZone = "UTC",
            CreatedAt = _now,
            UpdatedAt = _now,
        };
        db.Portfolios.Add(portfolio);
        await db.SaveChangesAsync();
        _portfolioId = portfolio.Id;
    }

    public async Task DisposeAsync()
    {
        if (_services is not null) await _services.DisposeAsync();
        if (_postgres is not null) await _postgres.DisposeAsync();
    }

    [SkippableFact]
    public async Task PlaidConnection_ConcurrentProviderReplay_ReturnsOneCanonicalConnection()
    {
        SkipIfNoDocker();
        var exchangeAttemptId = Guid.NewGuid();
        await using (var seed = NewContext())
        {
            seed.PlaidTokenExchangeAttempts.Add(new PlaidTokenExchangeAttempt
            {
                Id = exchangeAttemptId,
                PortfolioId = _portfolioId,
                ClientOperationId = "plaid-request-replay",
                RequestHash = "request-hash",
                PublicTokenHash = "public-token-hash",
                InstitutionName = "Replay bank",
                AccountName = "Operating checking",
                AccountMask = "4321",
                AccountType = "depository",
                AccountSubtype = "checking",
                ExternalAccountIdCipherText = "protected-account",
                ExternalAccountIdHash = "account-hash",
                Status = "RemoteReceiptRecorded",
                PreparedAtUtc = _now,
                RemoteAdmittedAtUtc = _now,
                RemoteReceiptRecordedAtUtc = _now,
                ProviderRequestIdentity = "plaid-request-replay",
                ExternalItemIdCipherText = "protected-item",
                ExternalItemIdHash = "item-hash",
                ExternalAccessTokenCipherText = "protected-access-token",
            });
            await seed.SaveChangesAsync();
        }
        var command = new ApplyPlaidConnectionCommand(_portfolioId, exchangeAttemptId, _now);
        var identity = new AtomicCommandIdentity(
            "banking.plaid.connection.apply",
            $"{_portfolioId}:plaid-request-replay");

        var outcomes = await Task.WhenAll(
            Atomic.ExecuteAsync(identity, command, ConnectionCodec),
            Atomic.ExecuteAsync(identity, command, ConnectionCodec));

        outcomes.Select(result => result.Disposition).Should()
            .BeEquivalentTo([AtomicCommandDisposition.Executed, AtomicCommandDisposition.Replayed]);
        outcomes[0].Value.Should().BeEquivalentTo(outcomes[1].Value);
        await using var db = NewContext();
        (await db.BankConnections.CountAsync(row => row.ExternalItemIdHash == "item-hash")).Should().Be(1);
        (await db.AtomicAuditLogs.CountAsync(row => row.CommandType == identity.CommandType)).Should().Be(1);
        (await db.AtomicCommandReceipts.CountAsync(row => row.CommandType == identity.CommandType)).Should().Be(1);
    }

    [SkippableFact]
    public async Task PlaidExchange_PrepareAdmitAndRemoteReceipt_AreDurableAndReplayCanonicalState()
    {
        SkipIfNoDocker();
        var prepareIdentity = new AtomicCommandIdentity(
            "banking.plaid.exchange.prepare",
            $"{_portfolioId}:durable-exchange");
        var prepare = new PreparePlaidTokenExchangeCommand(
            _portfolioId,
            "durable-exchange",
            new string('1', 64),
            new string('2', 64),
            "Durable bank",
            "Operating",
            "4321",
            "depository",
            "checking",
            "protected-account",
            new string('3', 64),
            _now);

        var prepared = await Task.WhenAll(
            Atomic.ExecuteAsync(prepareIdentity, prepare, ExchangePrepareCodec),
            Atomic.ExecuteAsync(prepareIdentity, prepare, ExchangePrepareCodec));

        prepared.Select(row => row.Disposition).Should()
            .BeEquivalentTo([AtomicCommandDisposition.Executed, AtomicCommandDisposition.Replayed]);
        prepared[0].Value.Should().BeEquivalentTo(prepared[1].Value);
        var exchangeAttemptId = prepared[0].Value.ExchangeAttemptId;
        await using (var afterPrepare = NewContext())
        {
            var attempt = await afterPrepare.PlaidTokenExchangeAttempts
                .SingleAsync(row => row.Id == exchangeAttemptId);
            attempt.Status.Should().Be("Prepared");
            attempt.RemoteAdmittedAtUtc.Should().BeNull();
            attempt.RemoteReceiptRecordedAtUtc.Should().BeNull();
        }

        var admitted = await Atomic.ExecuteAsync(
            new AtomicCommandIdentity("banking.plaid.exchange.admit", $"{_portfolioId}:{exchangeAttemptId:N}"),
            new AdmitPlaidTokenExchangeCommand(_portfolioId, exchangeAttemptId, _now.AddSeconds(1)),
            ExchangeAdmitCodec);
        admitted.Value.Outcome.Should().Be(AdmitPlaidTokenExchangeOutcome.Admitted);
        await using (var afterAdmission = NewContext())
        {
            var attempt = await afterAdmission.PlaidTokenExchangeAttempts
                .SingleAsync(row => row.Id == exchangeAttemptId);
            attempt.Status.Should().Be("RemoteAdmitted");
            attempt.RemoteAdmittedAtUtc.Should().NotBeNull();
            attempt.RemoteReceiptRecordedAtUtc.Should().BeNull();
        }

        var receiptIdentity = new AtomicCommandIdentity(
            "banking.plaid.exchange.receipt",
            $"{_portfolioId}:{exchangeAttemptId:N}");
        var receipt = new RecordPlaidTokenExchangeReceiptCommand(
            _portfolioId,
            exchangeAttemptId,
            "provider-request-exact",
            "protected-item",
            new string('4', 64),
            "protected-access",
            _now.AddSeconds(2));
        var receipts = await Task.WhenAll(
            Atomic.ExecuteAsync(receiptIdentity, receipt, ExchangeReceiptCodec),
            Atomic.ExecuteAsync(receiptIdentity, receipt, ExchangeReceiptCodec));

        receipts.Select(row => row.Disposition).Should()
            .BeEquivalentTo([AtomicCommandDisposition.Executed, AtomicCommandDisposition.Replayed]);
        await using var verify = NewContext();
        var durable = await verify.PlaidTokenExchangeAttempts.SingleAsync(row => row.Id == exchangeAttemptId);
        durable.Status.Should().Be("RemoteReceiptRecorded");
        durable.ProviderRequestIdentity.Should().Be("provider-request-exact");
        durable.ExternalItemIdCipherText.Should().Be("protected-item");
        durable.ExternalAccessTokenCipherText.Should().Be("protected-access");
        (await verify.AtomicCommandReceipts.CountAsync(row =>
            row.CommandType == prepareIdentity.CommandType || row.CommandType == receiptIdentity.CommandType))
            .Should().Be(2);
    }

    [SkippableFact]
    public async Task PlaidConnection_FinalizeFailureRollsBackConnectionAndLeavesDurableRemoteReceiptForRecovery()
    {
        SkipIfNoDocker();
        var exchangeAttemptId = Guid.NewGuid();
        await using (var seed = NewContext())
        {
            seed.PlaidTokenExchangeAttempts.Add(RemoteReceipt(exchangeAttemptId, "finalize-recovery"));
            await seed.SaveChangesAsync();
        }
        var identity = new AtomicCommandIdentity(
            "banking.plaid.connection.apply",
            $"{_portfolioId}:{exchangeAttemptId:N}");
        var command = new ApplyPlaidConnectionCommand(_portfolioId, exchangeAttemptId, _now);
        Failures.FailAtomicAudit = true;

        var failure = await FluentActions.Invoking(() => Atomic.ExecuteAsync(identity, command, ConnectionCodec))
            .Should().ThrowAsync<DbUpdateException>();
        failure.Which.InnerException.Should().BeOfType<InvalidOperationException>()
            .Which.Message.Should().Be("injected atomic audit failure");
        Failures.FailAtomicAudit = false;

        await using (var failed = NewContext())
        {
            (await failed.BankConnections.CountAsync()).Should().Be(0);
            var durable = await failed.PlaidTokenExchangeAttempts.SingleAsync(row => row.Id == exchangeAttemptId);
            durable.Status.Should().Be("RemoteReceiptRecorded");
            durable.CompletedAtUtc.Should().BeNull();
            (await failed.AtomicCommandReceipts.CountAsync(row => row.CommandType == identity.CommandType)).Should().Be(0);
        }

        var recovered = await Atomic.ExecuteAsync(identity, command, ConnectionCodec);
        recovered.Value.ConnectionId.Should().BePositive();
        await using var verify = NewContext();
        (await verify.BankConnections.CountAsync()).Should().Be(1);
        (await verify.PlaidTokenExchangeAttempts.SingleAsync(row => row.Id == exchangeAttemptId))
            .Status.Should().Be("Completed");
    }

    [SkippableFact]
    public async Task Import_ConcurrentReplay_CommitsRowsAuditsNotificationAndReceiptOnce_WithConstantReads()
    {
        SkipIfNoDocker();
        var command = Import("import-replay", 40);
        var identity = new AtomicCommandIdentity("banking.import.apply", $"{_portfolioId}:import-replay");
        Recorder.Clear();

        var outcomes = await Task.WhenAll(
            Atomic.ExecuteAsync(identity, command, ImportCodec),
            Atomic.ExecuteAsync(identity, command, ImportCodec));

        outcomes.Select(result => result.Disposition).Should()
            .BeEquivalentTo([AtomicCommandDisposition.Executed, AtomicCommandDisposition.Replayed]);
        outcomes[0].Value.Should().BeEquivalentTo(outcomes[1].Value);
        outcomes[0].Value.ImportedCount.Should().Be(40);
        await using var db = NewContext();
        (await db.BankConnections.CountAsync()).Should().Be(1);
        (await db.BankTransactions.CountAsync()).Should().Be(40);
        (await db.AtomicCommandReceipts.CountAsync(row => row.CommandType == identity.CommandType)).Should().Be(1);
        (await db.AtomicAuditLogs.CountAsync(row => row.CommandType == identity.CommandType)).Should().Be(41);
        (await db.Notifications.CountAsync(row => row.Type == "BankImportCompleted")).Should().Be(1);

        Recorder.Commands.Count(sql => sql.Contains("FROM \"BankTransactions\"", StringComparison.Ordinal))
            .Should().BeLessThanOrEqualTo(1, "import duplicate eligibility is one bounded SQL query, not one query per input row");
    }

    [SkippableFact]
    public async Task Import_DuplicateProviderIds_AreCanonicalAndDoNotCreateDuplicateRows()
    {
        SkipIfNoDocker();
        var input = Transaction("duplicate-provider-id", 10m);
        var command = new ImportBankTransactionsCommand(
            _portfolioId, "Manual", "Duplicate bank", "Checking", "1000", null, null,
            [input], 3, "duplicate-input", _now);

        var result = await Atomic.ExecuteAsync(
            new AtomicCommandIdentity("banking.import.apply", $"{_portfolioId}:duplicate-input"),
            command,
            ImportCodec);

        result.Value.ImportedCount.Should().Be(1);
        result.Value.SkippedCount.Should().Be(2);
        await using var db = NewContext();
        (await db.BankTransactions.CountAsync(row => row.ProviderTransactionId == input.ProviderTransactionId)).Should().Be(1);
    }

    [SkippableFact]
    public async Task Import_PostgreSqlTreatsCaseDistinctOpaqueProviderIdsAsDistinct()
    {
        SkipIfNoDocker();
        var command = new ImportBankTransactionsCommand(
            _portfolioId,
            "Manual",
            "Ordinal bank",
            "Checking",
            null,
            null,
            null,
            [Transaction("Opaque-AbC", 1m), Transaction("opaque-aBc", 2m)],
            2,
            "ordinal-provider-ids",
            _now);

        var result = await Atomic.ExecuteAsync(
            new AtomicCommandIdentity("banking.import.apply", $"{_portfolioId}:ordinal-provider-ids"),
            command,
            ImportCodec);

        result.Value.ImportedCount.Should().Be(2);
        await using var db = NewContext();
        (await db.BankTransactions.CountAsync(row => row.BankConnection!.InstitutionName == "Ordinal bank"))
            .Should().Be(2);
    }

    [SkippableFact]
    public async Task Import_FinalCompanionFailure_RollsBackBusinessAuditNotificationAndReceipt()
    {
        SkipIfNoDocker();
        Failures.FailNotifications = true;
        var identity = new AtomicCommandIdentity("banking.import.apply", $"{_portfolioId}:rollback");

        var act = () => Atomic.ExecuteAsync(identity, Import("rollback", 2), ImportCodec);
        var failure = await act.Should().ThrowAsync<DbUpdateException>();
        failure.Which.InnerException.Should().BeOfType<InvalidOperationException>()
            .Which.Message.Should().Be("injected notification failure");
        Failures.FailNotifications = false;

        await using var db = NewContext();
        (await db.BankConnections.CountAsync(row => row.InstitutionName == "Bank rollback")).Should().Be(0);
        (await db.BankTransactions.CountAsync(row => row.ProviderTransactionId.StartsWith("rollback-"))).Should().Be(0);
        (await db.AtomicAuditLogs.CountAsync(row => row.CommandType == identity.CommandType)).Should().Be(0);
        (await db.Notifications.CountAsync(row => row.Type == "BankImportCompleted")).Should().Be(0);
        (await db.AtomicCommandReceipts.CountAsync(row => row.CommandType == identity.CommandType)).Should().Be(0);
    }

    [SkippableFact]
    public async Task PlaidSync_ConcurrentCursorOwners_AllowOneApplyAndRejectTheStaleResult()
    {
        SkipIfNoDocker();
        int connectionId;
        await using (var db = NewContext())
        {
            var connection = new BankConnection
            {
                PortfolioId = _portfolioId,
                Provider = "Plaid",
                InstitutionName = "Cursor bank",
                AccountName = "Checking",
                Status = "Active",
                CreatedAt = _now,
                UpdatedAt = _now,
            };
            db.BankConnections.Add(connection);
            await db.SaveChangesAsync();
            connectionId = connection.Id;
        }

        var first = Sync(connectionId, "cursor-a", "sync-a");
        var second = Sync(connectionId, "cursor-b", "sync-b");
        var outcomes = await Task.WhenAll(
            Atomic.ExecuteAsync(new AtomicCommandIdentity("banking.plaid.sync.apply", $"{_portfolioId}:{connectionId}:sync-a"), first, SyncCodec),
            Atomic.ExecuteAsync(new AtomicCommandIdentity("banking.plaid.sync.apply", $"{_portfolioId}:{connectionId}:sync-b"), second, SyncCodec));

        outcomes.Count(result => result.Value.Outcome == ApplyPlaidSyncOutcome.Applied).Should().Be(1);
        outcomes.Count(result => result.Value.Outcome == ApplyPlaidSyncOutcome.StaleCursor).Should().Be(1);
        await using var verify = NewContext();
        (await verify.BankTransactions.CountAsync(row => row.BankConnectionId == connectionId)).Should().Be(1);
        (await verify.BankConnections.SingleAsync(row => row.Id == connectionId)).SyncCursorCipherText
            .Should().BeOneOf("cursor-a", "cursor-b");
    }

    [SkippableFact]
    public async Task Reconciliation_ConcurrentVersionOwners_ApplyOnceAndReplayCanonicalReceipt()
    {
        SkipIfNoDocker();
        int transactionId;
        int connectionId;
        await using (var db = NewContext())
        {
            var connection = new BankConnection
            {
                PortfolioId = _portfolioId,
                Provider = "Manual",
                InstitutionName = "Match bank",
                AccountName = "Checking",
                Status = "Active",
                CreatedAt = _now,
                UpdatedAt = _now,
            };
            db.BankConnections.Add(connection);
            await db.SaveChangesAsync();
            connectionId = connection.Id;
            var seededTransaction = new BankTransaction
            {
                PortfolioId = _portfolioId,
                BankConnectionId = connectionId,
                ProviderTransactionId = "reconcile-once",
                PostedAt = _now,
                Description = "Personal transaction",
                Amount = -5m,
                MatchStatus = "Unmatched",
                CreatedAt = _now,
                UpdatedAt = _now,
            };
            db.BankTransactions.Add(seededTransaction);
            await db.SaveChangesAsync();
            transactionId = seededTransaction.Id;
        }
        var command = new ReconcileBankTransactionCommand(
            _portfolioId, transactionId, BankReconciliationAction.Ignore, null, _now, _now.AddSeconds(1));
        var identity = new AtomicCommandIdentity("banking.transaction.reconcile", $"{_portfolioId}:{transactionId}:ignore-once");

        var outcomes = await Task.WhenAll(
            Atomic.ExecuteAsync(identity, command, ReconcileCodec),
            Atomic.ExecuteAsync(identity, command, ReconcileCodec));

        outcomes.Select(result => result.Disposition).Should()
            .BeEquivalentTo([AtomicCommandDisposition.Executed, AtomicCommandDisposition.Replayed]);
        outcomes[0].Value.Should().BeEquivalentTo(outcomes[1].Value);
        await using var verify = NewContext();
        var transaction = await verify.BankTransactions.SingleAsync(row => row.Id == transactionId);
        transaction.MatchStatus.Should().Be("Removed");
        transaction.Notes.Should().Contain("ignored");
        (await verify.AtomicAuditLogs.CountAsync(row => row.CommandType == identity.CommandType)).Should().Be(1);
        (await verify.AtomicCommandReceipts.CountAsync(row => row.CommandType == identity.CommandType)).Should().Be(1);
    }

    [SkippableFact]
    public async Task Reconciliation_CrossPortfolioTarget_IsNotVisibleOrMutated()
    {
        SkipIfNoDocker();
        int otherPortfolioId;
        int otherTransactionId;
        await using (var db = NewContext())
        {
            var otherPortfolio = new Portfolio
            {
                Name = "Other portfolio",
                ManagementCompanyName = "Other company",
                TimeZone = "UTC",
                CreatedAt = _now,
                UpdatedAt = _now,
            };
            db.Portfolios.Add(otherPortfolio);
            await db.SaveChangesAsync();
            otherPortfolioId = otherPortfolio.Id;
            var connection = new BankConnection
            {
                PortfolioId = otherPortfolioId,
                Provider = "Manual",
                InstitutionName = "Other bank",
                AccountName = "Checking",
                Status = "Active",
                CreatedAt = _now,
                UpdatedAt = _now,
            };
            db.BankConnections.Add(connection);
            await db.SaveChangesAsync();
            var transaction = new BankTransaction
            {
                PortfolioId = otherPortfolioId,
                BankConnectionId = connection.Id,
                ProviderTransactionId = "other-portfolio-line",
                PostedAt = _now,
                Description = "Must remain untouched",
                Amount = -20m,
                MatchStatus = "Unmatched",
                CreatedAt = _now,
                UpdatedAt = _now,
            };
            db.BankTransactions.Add(transaction);
            await db.SaveChangesAsync();
            otherTransactionId = transaction.Id;
        }

        var result = await Atomic.ExecuteAsync(
            new AtomicCommandIdentity("banking.transaction.reconcile", $"{_portfolioId}:{otherTransactionId}:cross-portfolio"),
            new ReconcileBankTransactionCommand(
                _portfolioId,
                otherTransactionId,
                BankReconciliationAction.Ignore,
                null,
                _now,
                _now.AddSeconds(1)),
            ReconcileCodec);

        result.Value.Outcome.Should().Be(ReconcileBankTransactionOutcome.TransactionNotFound);
        await using var verify = NewContext();
        var untouched = await verify.BankTransactions.SingleAsync(row => row.Id == otherTransactionId);
        untouched.PortfolioId.Should().Be(otherPortfolioId);
        untouched.MatchStatus.Should().Be("Unmatched");
        (await verify.AtomicAuditLogs.CountAsync(row => row.CommandIdempotencyKey.EndsWith("cross-portfolio"))).Should().Be(0);
    }

    private ImportBankTransactionsCommand Import(string identity, int count) => new(
        _portfolioId,
        "Manual",
        $"Bank {identity}",
        "Operating checking",
        "1234",
        "depository",
        "checking",
        Enumerable.Range(1, count).Select(index => Transaction($"{identity}-{index}", index)).ToArray(),
        count,
        identity,
        _now);

    private ApplyPlaidSyncCommand Sync(int connectionId, string nextCursor, string identity) => new(
        _portfolioId,
        connectionId,
        null,
        nextCursor,
        [Transaction(identity, 25m)],
        1,
        [],
        0,
        [],
        identity,
        _now.AddMinutes(1));

    private BankTransactionInput Transaction(string providerId, decimal amount) => new(
        providerId,
        _now,
        null,
        $"Transaction {providerId}",
        null,
        amount,
        "USD",
        null,
        "{}");

    private IAtomicUnitOfWork Atomic => _services!.GetRequiredService<IAtomicUnitOfWork>();
    private CommandRecorder Recorder => _services!.GetRequiredService<CommandRecorder>();
    private NotificationFailureInterceptor Failures => _services!.GetRequiredService<NotificationFailureInterceptor>();

    private RentalCommandDbContext NewContext() => new(
        new DbContextOptionsBuilder<RentalCommandDbContext>()
            .UseNpgsql(_postgres!.GetConnectionString())
            .Options);

    private void SkipIfNoDocker() =>
        Skip.IfNot(_dockerAvailable, "Docker is required for PostgreSQL banking atomic tests.");

    private PlaidTokenExchangeAttempt RemoteReceipt(Guid id, string operationId) => new()
    {
        Id = id,
        PortfolioId = _portfolioId,
        ClientOperationId = operationId,
        RequestHash = new string('1', 64),
        PublicTokenHash = new string('2', 64),
        InstitutionName = "Recovery bank",
        AccountName = "Operating",
        ExternalAccountIdCipherText = "protected-account",
        ExternalAccountIdHash = new string('3', 64),
        Status = "RemoteReceiptRecorded",
        PreparedAtUtc = _now,
        RemoteAdmittedAtUtc = _now,
        RemoteReceiptRecordedAtUtc = _now,
        ProviderRequestIdentity = operationId,
        ExternalItemIdCipherText = "protected-item",
        ExternalItemIdHash = new string('4', 64),
        ExternalAccessTokenCipherText = "protected-access",
    };

    private sealed class TestActor : ICurrentActor
    {
        public int? UserId => 801;
        public string? ActorLabel => "integration:banking-atomic";
        public string? IpAddress => "127.0.0.1";
    }

    private sealed class CommandRecorder : DbCommandInterceptor
    {
        private readonly ConcurrentQueue<string> _commands = new();
        public IReadOnlyCollection<string> Commands => _commands.ToArray();
        public void Clear()
        {
            while (_commands.TryDequeue(out _)) { }
        }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            _commands.Enqueue(command.CommandText);
            return ValueTask.FromResult(result);
        }
    }

    private sealed class NotificationFailureInterceptor : DbCommandInterceptor
    {
        public bool FailNotifications { get; set; }
        public bool FailAtomicAudit { get; set; }

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            if (FailNotifications && command.CommandText.Contains("INSERT INTO \"Notifications\"", StringComparison.Ordinal))
            {
                throw new InvalidOperationException("injected notification failure");
            }
            if (FailAtomicAudit && command.CommandText.Contains("INSERT INTO \"AtomicAuditLogs\"", StringComparison.Ordinal))
                throw new InvalidOperationException("injected atomic audit failure");
            return ValueTask.FromResult(result);
        }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            if (FailNotifications && command.CommandText.Contains("INSERT INTO \"Notifications\"", StringComparison.Ordinal))
            {
                throw new InvalidOperationException("injected notification failure");
            }
            if (FailAtomicAudit && command.CommandText.Contains("INSERT INTO \"AtomicAuditLogs\"", StringComparison.Ordinal))
                throw new InvalidOperationException("injected atomic audit failure");
            return ValueTask.FromResult(result);
        }
    }
}
