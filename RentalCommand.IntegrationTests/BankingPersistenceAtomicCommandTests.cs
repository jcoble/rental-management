using System.Collections.Concurrent;
using System.Data.Common;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using RentalCommand.Core.Atomic;
using RentalCommand.Core.Authorization;
using RentalCommand.Core.Banking;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Interfaces;
using RentalCommand.Api.Writes;
using RentalCommand.Data;
using RentalCommand.Data.Accounting;
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
        services.AddScoped<IRequestWriteExecutor, RequestWriteExecutor>();
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
        await db.Database.EnsureCreatedAsync();
        await db.Database.ExecuteSqlRawAsync(LeaseEffectiveClockSql.CreateEffectiveNowUtc);
        await db.Database.ExecuteSqlRawAsync(LeaseEffectiveClockSql.CreateBusinessDate);
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
        await new ChartOfAccountsSeedService(db).SeedAsync(_portfolioId);
        await db.SaveChangesAsync();
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
            ExecuteAtomicAsync(identity, command, ConnectionCodec),
            ExecuteAtomicAsync(identity, command, ConnectionCodec));

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
        var auth = await SeedAllPropertiesAuthorityAsync();
        var prepareIdentity = new AtomicCommandIdentity(
            "banking.plaid.exchange.prepare",
            $"{_portfolioId}:durable-exchange");
        var prepare = new PreparePlaidTokenExchangeCommand(
            _portfolioId,
            auth.UserId,
            auth.SessionId,
            auth.AccessContextId,
            auth.AccessRevision,
            CapabilityKeys.BankConnectionsManage,
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
            ExecuteAtomicAsync(prepareIdentity, prepare, ExchangePrepareCodec),
            ExecuteAtomicAsync(prepareIdentity, prepare, ExchangePrepareCodec));

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

        var admitted = await ExecuteAtomicAsync(
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
            ExecuteAtomicAsync(receiptIdentity, receipt, ExchangeReceiptCodec),
            ExecuteAtomicAsync(receiptIdentity, receipt, ExchangeReceiptCodec));

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
    public async Task PlaidExchange_PrepareReplayAfterSessionRevocation_IsDeniedBeforeReturningGlobalReceipt()
    {
        SkipIfNoDocker();
        var auth = await SeedAllPropertiesAuthorityAsync();
        var identity = new AtomicCommandIdentity(
            "banking.plaid.exchange.prepare",
            $"{_portfolioId}:revoked-prepare");
        var command = new PreparePlaidTokenExchangeCommand(
            _portfolioId,
            auth.UserId,
            auth.SessionId,
            auth.AccessContextId,
            auth.AccessRevision,
            CapabilityKeys.BankConnectionsManage,
            "revoked-prepare",
            new string('1', 64),
            new string('2', 64),
            "Revoked bank",
            "Operating",
            "4321",
            "depository",
            "checking",
            "protected-account",
            new string('3', 64),
            _now);
        await ExecuteAtomicAsync(identity, command, ExchangePrepareCodec);
        await using (var revoke = NewContext())
        {
            var session = await revoke.AuthSessions.SingleAsync(row => row.Id == auth.SessionId);
            session.Status = AuthSessionStatus.Revoked;
            session.RevokedAtUtc = DateTime.UtcNow;
            await revoke.SaveChangesAsync();
        }
        Recorder.Clear();

        await FluentActions.Invoking(() => ExecuteAtomicAsync(identity, command, ExchangePrepareCodec))
            .Should().ThrowAsync<UnauthorizedAccessException>();
        var authorizationSql = Recorder.Commands.Where(sql =>
            sql.Contains("MembershipRoleAssignments", StringComparison.Ordinal)).ToArray();
        authorizationSql.Should().ContainSingle("replay authorization is one EF-translated policy query");
        authorizationSql[0].Should().Contain("AuthSessions");
        authorizationSql[0].Should().Contain("WorkspaceAccessContexts");
        Recorder.ParameterValues.Should().Contain(CapabilityKeys.BankConnectionsManage);
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

        var failure = await FluentActions.Invoking(() => ExecuteAtomicAsync(identity, command, ConnectionCodec))
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

        var recovered = await ExecuteAtomicAsync(identity, command, ConnectionCodec);
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
            ExecuteAtomicAsync(identity, command, ImportCodec),
            ExecuteAtomicAsync(identity, command, ImportCodec));

        outcomes.Select(result => result.Disposition).Should()
            .BeEquivalentTo([AtomicCommandDisposition.Executed, AtomicCommandDisposition.Replayed]);
        outcomes[0].Value.Should().BeEquivalentTo(outcomes[1].Value);
        outcomes[0].Value.ImportedCount.Should().Be(40);
        await using var db = NewContext();
        (await db.BankConnections.CountAsync()).Should().Be(1);
        (await db.BankTransactions.CountAsync()).Should().Be(40);
        (await db.AtomicCommandReceipts.CountAsync(row => row.CommandType == identity.CommandType)).Should().Be(1);
        var auditCounts = await db.AtomicAuditLogs
            .Where(row => row.CommandType == identity.CommandType
                && row.CommandIdempotencyKey == identity.IdempotencyKey)
            .GroupBy(row => row.EntityType)
            .Select(group => new { EntityType = group.Key, Count = group.Count() })
            .ToDictionaryAsync(row => row.EntityType, row => row.Count);
        auditCounts.Should().BeEquivalentTo(new Dictionary<string, int>
        {
            [nameof(BankConnection)] = 1,
            [nameof(BankTransaction)] = 40,
            [nameof(Notification)] = 1,
        });
        var bankNotification = await db.Notifications.SingleAsync(row =>
            row.Type == "BankImportCompleted");
        bankNotification.NavigationDestination.Should().BeNull(
            "workspace-wide bank imports have no single access-bound recipient context");
        bankNotification.NavigationAccessContextId.Should().BeNull();

        var mergeCommands = Recorder.Commands
            .Where(sql => sql.Contains("jsonb_array_elements(@transactions::jsonb)", StringComparison.Ordinal))
            .ToArray();
        mergeCommands.Should().ContainSingle("the import is one DB-side dedupe/filter/insert/result statement");
        mergeCommands[0].Should().Contain("ON CONFLICT (\"BankConnectionId\", \"ProviderTransactionId\") DO NOTHING");
        mergeCommands[0].Should().Contain("jsonb_agg", "generated ids and semantic audit rows are shaped by PostgreSQL");
    }

    [SkippableFact]
    public async Task Import_DuplicateProviderIds_AreCanonicalAndDoNotCreateDuplicateRows()
    {
        SkipIfNoDocker();
        var input = Transaction("duplicate-provider-id", 10m);
        var command = new ImportBankTransactionsCommand(
            _portfolioId, "Manual", "Duplicate bank", "Checking", "1000", null, null,
            [input, input, input], 3, "duplicate-input", _now);

        var result = await ExecuteAtomicAsync(
            new AtomicCommandIdentity("banking.import.apply", $"{_portfolioId}:duplicate-input"),
            command,
            ImportCodec);

        result.Value.ImportedCount.Should().Be(1);
        result.Value.SkippedCount.Should().Be(2);
        await using var db = NewContext();
        (await db.BankTransactions.CountAsync(row => row.ProviderTransactionId == input.ProviderTransactionId)).Should().Be(1);
        var audit = await db.AtomicAuditLogs.SingleAsync(row =>
            row.CommandIdempotencyKey.EndsWith("duplicate-input")
            && row.EntityType == nameof(BankTransaction));
        audit.Operation.Should().Be(AuditLogOperation.Created);
        audit.OldValues.Should().BeNull();
        audit.NewValues.Should().Contain("duplicate-provider-id");
    }

    [SkippableFact]
    public async Task Import_StatementOnly_PersistsControlsWithoutInventingBankTransactions_AndReplaysExactly()
    {
        SkipIfNoDocker();
        var statement = new BankStatementInput(
            new DateOnly(2027, 1, 1),
            new DateOnly(2027, 1, 31),
            54250m,
            55925m,
            1675m,
            "USD");
        var command = new ImportBankTransactionsCommand(
            _portfolioId,
            "Manual",
            "Blue Door Synthetic Bank",
            "Security deposits",
            "1818",
            "depository",
            "checking",
            [],
            0,
            "jan-2027-security-deposit-statement",
            _now,
            statement);
        var identity = new AtomicCommandIdentity(
            "banking.import.apply",
            $"{_portfolioId}:jan-2027-security-deposit-statement");

        var first = await ExecuteAtomicAsync(identity, command, ImportCodec);
        var replay = await ExecuteAtomicAsync(identity, command, ImportCodec);

        replay.Disposition.Should().Be(AtomicCommandDisposition.Replayed);
        replay.Value.Should().BeEquivalentTo(first.Value);
        first.Value.ImportedCount.Should().Be(0);
        first.Value.ImportedTransactionIds.Should().BeEmpty();
        first.Value.StatementMovement.Should().Be(1675m);
        await using var db = NewContext();
        (await db.BankTransactions.CountAsync(row =>
            row.BankConnection!.AccountMask == "1818")).Should().Be(0);
        var persisted = await db.BankStatements.AsNoTracking()
            .Where(row => row.PortfolioId == _portfolioId
                && row.BankConnection!.AccountMask == "1818"
                && row.PeriodStart == statement.PeriodStart
                && row.PeriodEnd == statement.PeriodEnd)
            .Select(row => new
            {
                row.OpeningBalance,
                row.ClosingBalance,
                row.StatementMovement,
                row.IsoCurrencyCode,
            })
            .SingleAsync();
        persisted.OpeningBalance.Should().Be(54250m);
        persisted.ClosingBalance.Should().Be(55925m);
        persisted.StatementMovement.Should().Be(1675m);
        persisted.IsoCurrencyCode.Should().Be("USD");
        (await db.AtomicCommandReceipts.CountAsync(row =>
            row.CommandType == identity.CommandType
            && row.IdempotencyKey == identity.IdempotencyKey)).Should().Be(1);
    }

    [SkippableFact]
    public async Task Import_StatementCompanionFailure_RollsBackConnectionStatementNotificationAndReceipt()
    {
        SkipIfNoDocker();
        Failures.FailNotifications = true;
        var statement = new BankStatementInput(
            new DateOnly(2027, 1, 1),
            new DateOnly(2027, 1, 31),
            50000m,
            50000m,
            0m,
            "USD");
        var command = new ImportBankTransactionsCommand(
            _portfolioId, "Manual", "Blue Door Synthetic Bank", "Reserve", "7070", null, null,
            [], 0, "jan-2027-reserve-statement", _now, statement);
        var identity = new AtomicCommandIdentity(
            "banking.import.apply",
            $"{_portfolioId}:jan-2027-reserve-statement");

        var act = () => ExecuteAtomicAsync(identity, command, ImportCodec);
        await act.Should().ThrowAsync<DbUpdateException>();
        Failures.FailNotifications = false;

        await using var db = NewContext();
        (await db.BankConnections.CountAsync(row => row.AccountMask == "7070")).Should().Be(0);
        (await db.BankStatements.CountAsync(row => row.PeriodStart == statement.PeriodStart)).Should().Be(0);
        (await db.BankTransactions.CountAsync(row =>
            row.BankConnection!.AccountMask == "7070")).Should().Be(0);
        (await db.AtomicAuditLogs.CountAsync(row =>
            row.CommandType == identity.CommandType
            && row.CommandIdempotencyKey == identity.IdempotencyKey)).Should().Be(0);
        (await db.Notifications.CountAsync(row =>
            row.Type == "BankImportCompleted"
            && row.CreatedAt == command.ImportedAtUtc)).Should().Be(0);
        (await db.AtomicCommandReceipts.CountAsync(row =>
            row.CommandType == identity.CommandType
            && row.IdempotencyKey == identity.IdempotencyKey)).Should().Be(0);
    }

    [SkippableFact]
    public async Task Import_NewReceiptForPersistedProviderIds_ReturnsDatabaseShapedSkipsWithoutNewAuditOrNotification()
    {
        SkipIfNoDocker();
        var first = Import("persisted-duplicate", 2);
        await ExecuteAtomicAsync(
            new AtomicCommandIdentity("banking.import.apply", $"{_portfolioId}:persisted-duplicate:first"),
            first,
            ImportCodec);

        var replayUnderNewReceipt = first with { RequestIdentity = "persisted-duplicate-second" };
        var result = await ExecuteAtomicAsync(
            new AtomicCommandIdentity("banking.import.apply", $"{_portfolioId}:persisted-duplicate:second"),
            replayUnderNewReceipt,
            ImportCodec);

        result.Value.ImportedCount.Should().Be(0);
        result.Value.SkippedCount.Should().Be(2);
        result.Value.ImportedTransactionIds.Should().BeEmpty();
        await using var db = NewContext();
        (await db.BankTransactions.CountAsync(row => row.ProviderTransactionId.StartsWith("persisted-duplicate-")))
            .Should().Be(2);
        (await db.AtomicAuditLogs.CountAsync(row =>
            row.CommandIdempotencyKey.EndsWith("persisted-duplicate:second")
            && row.EntityType == nameof(BankTransaction))).Should().Be(0);
        (await db.Notifications.CountAsync(row =>
            row.Type == "BankImportCompleted"
            && row.CreatedAt == replayUnderNewReceipt.ImportedAtUtc)).Should().Be(1,
                "only the first receipt imported rows");
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

        var result = await ExecuteAtomicAsync(
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

        var act = () => ExecuteAtomicAsync(identity, Import("rollback", 2), ImportCodec);
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
            ExecuteAtomicAsync(new AtomicCommandIdentity("banking.plaid.sync.apply", $"{_portfolioId}:{connectionId}:sync-a"), first, SyncCodec),
            ExecuteAtomicAsync(new AtomicCommandIdentity("banking.plaid.sync.apply", $"{_portfolioId}:{connectionId}:sync-b"), second, SyncCodec));

        outcomes.Count(result => result.Value.Outcome == ApplyPlaidSyncOutcome.Applied).Should().Be(1);
        outcomes.Count(result => result.Value.Outcome == ApplyPlaidSyncOutcome.StaleCursor).Should().Be(1);
        await using var verify = NewContext();
        (await verify.BankTransactions.CountAsync(row => row.BankConnectionId == connectionId)).Should().Be(1);
        (await verify.BankConnections.SingleAsync(row => row.Id == connectionId)).SyncCursorCipherText
            .Should().BeOneOf("cursor-a", "cursor-b");
    }

    [SkippableFact]
    public async Task PlaidSync_SetMergeReturnsCreatedAndModifiedIds_AndAuditsFinalRemovedState()
    {
        SkipIfNoDocker();
        int connectionId;
        int existingId;
        await using (var db = NewContext())
        {
            var connection = new BankConnection
            {
                PortfolioId = _portfolioId,
                Provider = "Plaid",
                InstitutionName = "Set merge bank",
                AccountName = "Checking",
                Status = "Active",
                CreatedAt = _now,
                UpdatedAt = _now,
            };
            db.BankConnections.Add(connection);
            await db.SaveChangesAsync();
            connectionId = connection.Id;
            var existing = new BankTransaction
            {
                PortfolioId = _portfolioId,
                BankConnectionId = connectionId,
                ProviderTransactionId = "set-merge-existing",
                PostedAt = _now,
                Description = "Before merge",
                Amount = 10m,
                IsoCurrencyCode = "USD",
                MatchStatus = "Matched",
                MatchConfidence = 1m,
                CreatedAt = _now,
                UpdatedAt = _now,
            };
            db.BankTransactions.Add(existing);
            await db.SaveChangesAsync();
            existingId = existing.Id;
        }

        var created = Transaction("set-merge-created", 20m);
        var modified = Transaction("set-merge-existing", 30m) with { Description = "After merge" };
        var command = new ApplyPlaidSyncCommand(
            _portfolioId, connectionId, null, "set-merge-cursor",
            [created, created], 2,
            [modified], 1,
            ["set-merge-existing", "set-merge-existing"],
            "set-merge-request", _now.AddMinutes(2));
        Recorder.Clear();

        var result = await ExecuteAtomicAsync(
            new AtomicCommandIdentity("banking.plaid.sync.apply", $"{_portfolioId}:{connectionId}:set-merge"),
            command,
            SyncCodec);

        result.Value.ImportedCount.Should().Be(1);
        result.Value.SkippedCount.Should().Be(1);
        result.Value.AffectedTransactionIds.Should().Contain(existingId);
        result.Value.AffectedTransactionIds.Should().HaveCount(2);
        var mergeCommands = Recorder.Commands
            .Where(sql => sql.Contains("jsonb_array_elements(@added::jsonb)", StringComparison.Ordinal))
            .ToArray();
        mergeCommands.Should().ContainSingle("Plaid reconciliation is one PostgreSQL merge statement");
        mergeCommands[0].Should().Contain("existing AS MATERIALIZED");
        mergeCommands[0].Should().Contain("jsonb_agg");

        await using var verify = NewContext();
        var final = await verify.BankTransactions.SingleAsync(row => row.Id == existingId);
        final.Description.Should().Be("After merge");
        final.Amount.Should().Be(30m);
        final.MatchStatus.Should().Be("Removed");
        final.MatchedTenantLedgerEntryId.Should().BeNull();
        final.MatchConfidence.Should().BeNull();
        var audit = await verify.AtomicAuditLogs.SingleAsync(row =>
            row.CommandIdempotencyKey.EndsWith(":set-merge")
            && row.EntityType == nameof(BankTransaction)
            && row.EntityId == existingId);
        audit.Operation.Should().Be(AuditLogOperation.Updated);
        audit.OldValues.Should().Contain("Before merge");
        audit.NewValues.Should().Contain("After merge");
        audit.NewValues.Should().Contain("Removed");
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

        var result = await ExecuteAtomicAsync(
            new AtomicCommandIdentity("banking.transaction.reconcile", $"{_portfolioId}:{otherTransactionId}:cross-portfolio"),
            new ReconcileBankTransactionCommand(
                _portfolioId,
                otherTransactionId,
                BankReconciliationAction.Ignore,
                null,
                null,
                null,
                null,
                null,
                null,
                null,
                _now,
                _now.AddSeconds(1),
                1,
                Guid.NewGuid(),
                1,
                1,
                CapabilityKeys.MoneyReconciliationDestructive,
                "cross-portfolio"),
            ReconcileCodec);

        result.Value.Outcome.Should().Be(ReconcileBankTransactionOutcome.TransactionNotFound);
        await using var verify = NewContext();
        var untouched = await verify.BankTransactions.SingleAsync(row => row.Id == otherTransactionId);
        untouched.PortfolioId.Should().Be(otherPortfolioId);
        untouched.MatchStatus.Should().Be("Unmatched");
        (await verify.AtomicAuditLogs.CountAsync(row => row.CommandIdempotencyKey.EndsWith("cross-portfolio"))).Should().Be(0);
    }

    [SkippableFact]
    public async Task Reconciliation_SameLedgerTransferRecordsNoAccountingEffectReason()
    {
        SkipIfNoDocker();
        var auth = await SeedAllPropertiesAuthorityAsync();
        int sourceId;
        int destinationId;
        await using (var db = NewContext())
        {
            var sourceConnection = SeedBankConnection(db, "Same-ledger source bank");
            var destinationConnection = SeedBankConnection(db, "Same-ledger destination bank");
            var source = SeedBankTransaction(
                db, sourceConnection.Id, "same-ledger-transfer-source", -300m, null);
            var destination = SeedBankTransaction(
                db, destinationConnection.Id, "same-ledger-transfer-destination", 300m, null);
            await db.SaveChangesAsync();
            sourceId = source.Id;
            destinationId = destination.Id;
        }

        var identity = new AtomicCommandIdentity(
            "banking.transaction.reconcile", $"{_portfolioId}:{sourceId}:same-ledger-transfer");
        var command = new ReconcileBankTransactionCommand(
            _portfolioId,
            sourceId,
            BankReconciliationAction.MatchTransfer,
            null,
            null,
            null,
            null,
            null,
            destinationId,
            _now,
            _now,
            _now.AddSeconds(1),
            auth.UserId,
            auth.SessionId,
            auth.AccessContextId,
            auth.AccessRevision,
            CapabilityKeys.MoneyReconciliationOperate,
            "same-ledger-transfer");

        var committed = await ExecuteAtomicAsync(identity, command, ReconcileCodec);
        var replay = await ExecuteAtomicAsync(identity, command, ReconcileCodec);

        committed.Value.Outcome.Should().Be(ReconcileBankTransactionOutcome.Applied);
        replay.Disposition.Should().Be(AtomicCommandDisposition.Replayed);
        await using var verify = NewContext();
        var notes = await verify.BankTransactions.AsNoTracking()
            .Where(row => row.Id == sourceId || row.Id == destinationId)
            .OrderBy(row => row.Id)
            .Select(row => new { row.MatchStatus, row.MatchedBankTransactionId, row.Notes })
            .ToListAsync();
        notes.Should().HaveCount(2);
        notes.Should().OnlyContain(row =>
            row.MatchStatus == "Matched"
            && row.MatchedBankTransactionId != null
            && row.Notes == "Transfer between accounts tracked under the same ledger account; no accounting effect at current granularity.");
        (await verify.JournalEntries.CountAsync(row =>
            row.PortfolioId == _portfolioId && row.SourceType == JournalSourceType.BankTransfer)).Should().Be(0);
    }

    [SkippableFact]
    public async Task Reconciliation_ExpenseMatch_CommitsPaidLifecycleAndReplaysExactResult()
    {
        SkipIfNoDocker();
        var auth = await SeedAllPropertiesAuthorityAsync();
        int transactionId;
        int expenseId;
        await using (var db = NewContext())
        {
            var property = SeedProperty(db, "Expense lifecycle property");
            var expense = new Expense
            {
                PortfolioId = _portfolioId,
                OperationalScope = ExpenseOperationalScope.Property,
                PropertyId = property.Id,
                Category = ScheduleECategory.Repairs,
                Description = "Approved plumbing bill",
                Status = ExpenseStatus.Approved,
                Amount = 245.60m,
                IncurredAt = _now.AddDays(-2),
                CreatedAt = _now.AddDays(-2),
                UpdatedAt = _now.AddDays(-2),
            };
            var connection = SeedBankConnection(db, "Expense lifecycle bank");
            var transaction = SeedBankTransaction(
                db, connection.Id, "expense-lifecycle-paid", -expense.Amount, property.Id);
            db.Expenses.Add(expense);
            await db.SaveChangesAsync();
            transactionId = transaction.Id;
            expenseId = expense.Id;
        }
        var identity = new AtomicCommandIdentity(
            "banking.transaction.reconcile",
            $"{_portfolioId}:{transactionId}:expense-lifecycle-paid");
        var command = ReconcileExpense(transactionId, expenseId, auth, "expense-lifecycle-paid");

        var committed = await ExecuteAtomicAsync(identity, command, ReconcileCodec);
        var replay = await ExecuteAtomicAsync(identity, command, ReconcileCodec);

        committed.Disposition.Should().Be(AtomicCommandDisposition.Executed);
        replay.Disposition.Should().Be(AtomicCommandDisposition.Replayed);
        replay.Value.Should().BeEquivalentTo(committed.Value);
        await using var verify = NewContext();
        var state = await (
            from transaction in verify.BankTransactions.AsNoTracking()
            join expense in verify.Expenses.AsNoTracking()
                on transaction.MatchedExpenseId equals expense.Id
            where transaction.Id == transactionId
            select new
            {
                transaction.MatchStatus,
                transaction.MatchedExpenseId,
                ExpenseStatus = expense.Status,
                expense.PaidAt,
                ExpenseUpdatedAt = expense.UpdatedAt,
            })
            .SingleAsync();
        state.MatchStatus.Should().Be("Matched");
        state.MatchedExpenseId.Should().Be(expenseId);
        state.ExpenseStatus.Should().Be(ExpenseStatus.Paid);
        state.PaidAt.Should().Be(_now);
        state.ExpenseUpdatedAt.Should().Be(_now.AddSeconds(1));
        (await verify.AtomicCommandReceipts.CountAsync(row =>
            row.CommandType == identity.CommandType
            && row.IdempotencyKey == identity.IdempotencyKey)).Should().Be(1);
        (await verify.AtomicAuditLogs.CountAsync(row =>
            row.CommandType == identity.CommandType
            && row.CommandIdempotencyKey == identity.IdempotencyKey)).Should().Be(3);
    }

    [SkippableFact]
    public async Task Reconciliation_ExpenseMatch_FinalAuditFailureRollsBackMatchAndPaidLifecycle()
    {
        SkipIfNoDocker();
        var auth = await SeedAllPropertiesAuthorityAsync();
        int transactionId;
        int expenseId;
        await using (var db = NewContext())
        {
            var property = SeedProperty(db, "Expense rollback property");
            var expense = new Expense
            {
                PortfolioId = _portfolioId,
                OperationalScope = ExpenseOperationalScope.Property,
                PropertyId = property.Id,
                Category = ScheduleECategory.Repairs,
                Description = "Pending electrical bill",
                Status = ExpenseStatus.Pending,
                Amount = 180m,
                IncurredAt = _now.AddDays(-1),
                CreatedAt = _now.AddDays(-1),
                UpdatedAt = _now.AddDays(-1),
            };
            var connection = SeedBankConnection(db, "Expense rollback bank");
            var transaction = SeedBankTransaction(
                db, connection.Id, "expense-lifecycle-rollback", -expense.Amount, property.Id);
            db.Expenses.Add(expense);
            await db.SaveChangesAsync();
            transactionId = transaction.Id;
            expenseId = expense.Id;
        }
        var identity = new AtomicCommandIdentity(
            "banking.transaction.reconcile",
            $"{_portfolioId}:{transactionId}:expense-lifecycle-rollback");
        var command = ReconcileExpense(
            transactionId, expenseId, auth, "expense-lifecycle-rollback");
        Failures.FailAtomicAudit = true;

        var failure = await FluentActions.Invoking(
                () => ExecuteAtomicAsync(identity, command, ReconcileCodec))
            .Should().ThrowAsync<DbUpdateException>();
        failure.Which.InnerException.Should().BeOfType<InvalidOperationException>()
            .Which.Message.Should().Be("injected atomic audit failure");
        Failures.FailAtomicAudit = false;

        await using var verify = NewContext();
        var state = await (
            from transaction in verify.BankTransactions.AsNoTracking()
            from expense in verify.Expenses.AsNoTracking()
            where transaction.Id == transactionId && expense.Id == expenseId
            select new
            {
                transaction.MatchStatus,
                transaction.MatchedExpenseId,
                ExpenseStatus = expense.Status,
                expense.PaidAt,
            })
            .SingleAsync();
        state.MatchStatus.Should().Be("Unmatched");
        state.MatchedExpenseId.Should().BeNull();
        state.ExpenseStatus.Should().Be(ExpenseStatus.Pending);
        state.PaidAt.Should().BeNull();
        (await verify.AtomicCommandReceipts.CountAsync(row =>
            row.CommandType == identity.CommandType
            && row.IdempotencyKey == identity.IdempotencyKey)).Should().Be(0);
        (await verify.AtomicAuditLogs.CountAsync(row =>
            row.CommandType == identity.CommandType
            && row.CommandIdempotencyKey == identity.IdempotencyKey)).Should().Be(0);
    }

    [SkippableFact]
    public async Task Reconciliation_ExpenseMatchThenClear_RestoresExactPriorLifecycleAndReplaysClear()
    {
        SkipIfNoDocker();
        var auth = await SeedAllPropertiesAuthorityAsync();
        int transactionId;
        int expenseId;
        var originalPaidAt = _now.AddDays(-3);
        var originalUpdatedAt = _now.AddDays(-2);
        await using (var db = NewContext())
        {
            var property = SeedProperty(db, "Expense clear lifecycle property");
            var expense = new Expense
            {
                PortfolioId = _portfolioId,
                OperationalScope = ExpenseOperationalScope.Property,
                PropertyId = property.Id,
                Category = ScheduleECategory.Repairs,
                Description = "Previously paid plumbing bill",
                Status = ExpenseStatus.Paid,
                Amount = 245.60m,
                IncurredAt = _now.AddDays(-5),
                PaidAt = originalPaidAt,
                CreatedAt = _now.AddDays(-5),
                UpdatedAt = originalUpdatedAt,
            };
            var connection = SeedBankConnection(db, "Expense clear lifecycle bank");
            var transaction = SeedBankTransaction(
                db, connection.Id, "expense-lifecycle-clear", -expense.Amount, property.Id);
            db.Expenses.Add(expense);
            await db.SaveChangesAsync();
            transactionId = transaction.Id;
            expenseId = expense.Id;
        }
        var matchIdentity = new AtomicCommandIdentity(
            "banking.transaction.reconcile",
            $"{_portfolioId}:{transactionId}:expense-lifecycle-clear-match");
        var match = ReconcileExpense(
            transactionId, expenseId, auth, "expense-lifecycle-clear-match");
        var matched = await ExecuteAtomicAsync(matchIdentity, match, ReconcileCodec);
        var clearIdentity = new AtomicCommandIdentity(
            "banking.transaction.reconcile",
            $"{_portfolioId}:{transactionId}:expense-lifecycle-clear");
        var clear = ReconcileClear(
            transactionId,
            auth,
            "expense-lifecycle-clear",
            matched.Value.Transaction!.UpdatedAt,
            _now.AddSeconds(2));

        var committed = await ExecuteAtomicAsync(clearIdentity, clear, ReconcileCodec);
        var replay = await ExecuteAtomicAsync(clearIdentity, clear, ReconcileCodec);

        committed.Disposition.Should().Be(AtomicCommandDisposition.Executed);
        replay.Disposition.Should().Be(AtomicCommandDisposition.Replayed);
        replay.Value.Should().BeEquivalentTo(committed.Value);
        await using var verify = NewContext();
        var state = await (
            from transaction in verify.BankTransactions.AsNoTracking()
            from expense in verify.Expenses.AsNoTracking()
            where transaction.Id == transactionId && expense.Id == expenseId
            select new
            {
                transaction.MatchStatus,
                transaction.MatchedExpenseId,
                transaction.ExpenseMatchAppliedAt,
                transaction.ExpenseMatchPreviousStatus,
                transaction.ExpenseMatchPreviousPaidAt,
                transaction.ExpenseMatchPreviousUpdatedAt,
                ExpenseStatus = expense.Status,
                expense.PaidAt,
                ExpenseUpdatedAt = expense.UpdatedAt,
            })
            .SingleAsync();
        state.MatchStatus.Should().Be("Unmatched");
        state.MatchedExpenseId.Should().BeNull();
        state.ExpenseMatchAppliedAt.Should().BeNull();
        state.ExpenseMatchPreviousStatus.Should().BeNull();
        state.ExpenseMatchPreviousPaidAt.Should().BeNull();
        state.ExpenseMatchPreviousUpdatedAt.Should().BeNull();
        state.ExpenseStatus.Should().Be(ExpenseStatus.Paid);
        state.PaidAt.Should().Be(originalPaidAt);
        state.ExpenseUpdatedAt.Should().Be(originalUpdatedAt);
        (await verify.AtomicCommandReceipts.CountAsync(row =>
            row.CommandType == clearIdentity.CommandType
            && row.IdempotencyKey == clearIdentity.IdempotencyKey)).Should().Be(1);
    }

    [SkippableFact]
    public async Task Reconciliation_ExpenseClear_FinalAuditFailureRollsBackClearAndLifecycleRestore()
    {
        SkipIfNoDocker();
        var auth = await SeedAllPropertiesAuthorityAsync();
        int transactionId;
        int expenseId;
        await using (var db = NewContext())
        {
            var property = SeedProperty(db, "Expense clear rollback property");
            var expense = new Expense
            {
                PortfolioId = _portfolioId,
                OperationalScope = ExpenseOperationalScope.Property,
                PropertyId = property.Id,
                Category = ScheduleECategory.Repairs,
                Description = "Approved electrical bill",
                Status = ExpenseStatus.Approved,
                Amount = 180m,
                IncurredAt = _now.AddDays(-1),
                CreatedAt = _now.AddDays(-1),
                UpdatedAt = _now.AddDays(-1),
            };
            var connection = SeedBankConnection(db, "Expense clear rollback bank");
            var transaction = SeedBankTransaction(
                db, connection.Id, "expense-clear-rollback", -expense.Amount, property.Id);
            db.Expenses.Add(expense);
            await db.SaveChangesAsync();
            transactionId = transaction.Id;
            expenseId = expense.Id;
        }
        var matchIdentity = new AtomicCommandIdentity(
            "banking.transaction.reconcile",
            $"{_portfolioId}:{transactionId}:expense-clear-rollback-match");
        var matched = await ExecuteAtomicAsync(
            matchIdentity,
            ReconcileExpense(transactionId, expenseId, auth, "expense-clear-rollback-match"),
            ReconcileCodec);
        var clearIdentity = new AtomicCommandIdentity(
            "banking.transaction.reconcile",
            $"{_portfolioId}:{transactionId}:expense-clear-rollback");
        var clear = ReconcileClear(
            transactionId,
            auth,
            "expense-clear-rollback",
            matched.Value.Transaction!.UpdatedAt,
            _now.AddSeconds(2));
        Failures.FailAtomicAudit = true;

        var failure = await FluentActions.Invoking(
                () => ExecuteAtomicAsync(clearIdentity, clear, ReconcileCodec))
            .Should().ThrowAsync<DbUpdateException>();
        failure.Which.InnerException.Should().BeOfType<InvalidOperationException>()
            .Which.Message.Should().Be("injected atomic audit failure");
        Failures.FailAtomicAudit = false;

        await using var verify = NewContext();
        var state = await (
            from transaction in verify.BankTransactions.AsNoTracking()
            join expense in verify.Expenses.AsNoTracking()
                on transaction.MatchedExpenseId equals expense.Id
            where transaction.Id == transactionId
            select new
            {
                transaction.MatchStatus,
                transaction.MatchedExpenseId,
                ExpenseStatus = expense.Status,
                expense.PaidAt,
                ExpenseUpdatedAt = expense.UpdatedAt,
            })
            .SingleAsync();
        state.MatchStatus.Should().Be("Matched");
        state.MatchedExpenseId.Should().Be(expenseId);
        state.ExpenseStatus.Should().Be(ExpenseStatus.Paid);
        state.PaidAt.Should().Be(_now);
        state.ExpenseUpdatedAt.Should().Be(_now.AddSeconds(1));
        (await verify.AtomicCommandReceipts.CountAsync(row =>
            row.CommandType == clearIdentity.CommandType
            && row.IdempotencyKey == clearIdentity.IdempotencyKey)).Should().Be(0);
        (await verify.AtomicAuditLogs.CountAsync(row =>
            row.CommandType == clearIdentity.CommandType
            && row.CommandIdempotencyKey == clearIdentity.IdempotencyKey)).Should().Be(0);
    }

    [SkippableFact]
    public async Task Reconciliation_ExpenseClear_WhenLifecycleChangedAfterMatch_RefusesUnsafeRestore()
    {
        SkipIfNoDocker();
        var auth = await SeedAllPropertiesAuthorityAsync();
        int transactionId;
        int expenseId;
        await using (var db = NewContext())
        {
            var property = SeedProperty(db, "Expense unsafe clear property");
            var expense = new Expense
            {
                PortfolioId = _portfolioId,
                OperationalScope = ExpenseOperationalScope.Property,
                PropertyId = property.Id,
                Category = ScheduleECategory.Repairs,
                Description = "Approved roof bill",
                Status = ExpenseStatus.Approved,
                Amount = 420m,
                IncurredAt = _now.AddDays(-1),
                CreatedAt = _now.AddDays(-1),
                UpdatedAt = _now.AddDays(-1),
            };
            var connection = SeedBankConnection(db, "Expense unsafe clear bank");
            var transaction = SeedBankTransaction(
                db, connection.Id, "expense-unsafe-clear", -expense.Amount, property.Id);
            db.Expenses.Add(expense);
            await db.SaveChangesAsync();
            transactionId = transaction.Id;
            expenseId = expense.Id;
        }
        var matchIdentity = new AtomicCommandIdentity(
            "banking.transaction.reconcile",
            $"{_portfolioId}:{transactionId}:expense-unsafe-clear-match");
        var matched = await ExecuteAtomicAsync(
            matchIdentity,
            ReconcileExpense(transactionId, expenseId, auth, "expense-unsafe-clear-match"),
            ReconcileCodec);
        var concurrentPaidAt = _now.AddHours(3);
        var concurrentUpdatedAt = _now.AddHours(4);
        await using (var db = NewContext())
        {
            var expense = await db.Expenses.SingleAsync(row => row.Id == expenseId);
            expense.PaidAt = concurrentPaidAt;
            expense.UpdatedAt = concurrentUpdatedAt;
            await db.SaveChangesAsync();
        }

        var result = await ExecuteAtomicAsync(
            new AtomicCommandIdentity(
                "banking.transaction.reconcile",
                $"{_portfolioId}:{transactionId}:expense-unsafe-clear"),
            ReconcileClear(
                transactionId,
                auth,
                "expense-unsafe-clear",
                matched.Value.Transaction!.UpdatedAt,
                _now.AddSeconds(2)),
            ReconcileCodec);

        result.Value.Outcome.Should().Be(ReconcileBankTransactionOutcome.TargetNotFound);
        await using var verify = NewContext();
        var state = await (
            from transaction in verify.BankTransactions.AsNoTracking()
            join expense in verify.Expenses.AsNoTracking()
                on transaction.MatchedExpenseId equals expense.Id
            where transaction.Id == transactionId
            select new
            {
                transaction.MatchStatus,
                transaction.MatchedExpenseId,
                ExpenseStatus = expense.Status,
                expense.PaidAt,
                ExpenseUpdatedAt = expense.UpdatedAt,
            })
            .SingleAsync();
        state.MatchStatus.Should().Be("Matched");
        state.MatchedExpenseId.Should().Be(expenseId);
        state.ExpenseStatus.Should().Be(ExpenseStatus.Paid);
        state.PaidAt.Should().Be(concurrentPaidAt);
        state.ExpenseUpdatedAt.Should().Be(concurrentUpdatedAt);
    }

    [SkippableFact]
    public async Task Reconciliation_ConcurrentDifferentBankLines_CannotClaimSameLoanPayment()
    {
        SkipIfNoDocker();
        var auth = await SeedAllPropertiesAuthorityAsync();
        int firstTransactionId;
        int secondTransactionId;
        int loanPaymentId;
        await using (var db = NewContext())
        {
            var property = SeedProperty(db, "Loan target property");
            var loan = new Loan
            {
                PortfolioId = _portfolioId,
                PropertyId = property.Id,
                Lender = "Atomic Mortgage",
                OriginalAmount = 200000m,
                CurrentBalance = 180000m,
                AnnualInterestRatePct = 6m,
                TermMonths = 360,
                StartDate = _now.AddYears(-1),
                DayOfMonthDue = _now.Day,
                MonthlyPrincipalInterest = 1250m,
                Status = LoanStatus.Active,
                CreatedAt = _now,
                UpdatedAt = _now,
            };
            var payment = new LoanPayment
            {
                PortfolioId = _portfolioId,
                Loan = loan,
                PeriodKey = "2026-07",
                DueDate = _now,
                PaidDate = _now,
                InterestAmount = 700m,
                PrincipalAmount = 550m,
                TotalAmount = 1250m,
                BalanceAfter = 179450m,
                Status = LoanPaymentStatus.Paid,
                CreatedAt = _now,
            };
            var connection = SeedBankConnection(db, "Loan duplicate bank");
            var first = SeedBankTransaction(db, connection.Id, "loan-duplicate-first", -1250m, property.Id);
            var second = SeedBankTransaction(db, connection.Id, "loan-duplicate-second", -1250m, property.Id);
            db.LoanPayments.Add(payment);
            await db.SaveChangesAsync();
            firstTransactionId = first.Id;
            secondTransactionId = second.Id;
            loanPaymentId = payment.Id;
        }

        var outcomes = await Task.WhenAll(
            ExecuteAtomicAsync(
                new AtomicCommandIdentity("banking.transaction.reconcile", $"{_portfolioId}:{firstTransactionId}:loan-duplicate-first"),
                ReconcileLoanPayment(firstTransactionId, loanPaymentId, auth, "loan-duplicate-first"),
                ReconcileCodec),
            ExecuteAtomicAsync(
                new AtomicCommandIdentity("banking.transaction.reconcile", $"{_portfolioId}:{secondTransactionId}:loan-duplicate-second"),
                ReconcileLoanPayment(secondTransactionId, loanPaymentId, auth, "loan-duplicate-second"),
                ReconcileCodec));

        outcomes.Count(result => result.Value.Outcome == ReconcileBankTransactionOutcome.Applied).Should().Be(1);
        outcomes.Count(result => result.Value.Outcome == ReconcileBankTransactionOutcome.TargetNotFound).Should().Be(1);
        await using var verify = NewContext();
        var matchedRows = await verify.BankTransactions.AsNoTracking()
            .Where(row => row.PortfolioId == _portfolioId && row.MatchedLoanPaymentId == loanPaymentId)
            .Select(row => row.Id)
            .ToListAsync();
        matchedRows.Should().ContainSingle();
    }

    [SkippableFact]
    public async Task Reconciliation_MatchingAlreadyPostedLoanPaymentDoesNotCreateSecondJournal()
    {
        SkipIfNoDocker();
        var auth = await SeedAllPropertiesAuthorityAsync();
        int transactionId;
        int loanPaymentId;
        await using (var db = NewContext())
        {
            var property = SeedProperty(db, "Loan already-posted property");
            var loan = new Loan
            {
                PortfolioId = _portfolioId,
                PropertyId = property.Id,
                Lender = "Atomic Mortgage",
                OriginalAmount = 200000m,
                CurrentBalance = 180000m,
                AnnualInterestRatePct = 6m,
                TermMonths = 360,
                StartDate = _now.AddYears(-1),
                DayOfMonthDue = _now.Day,
                MonthlyPrincipalInterest = 1250m,
                Status = LoanStatus.Active,
                CreatedAt = _now,
                UpdatedAt = _now,
            };
            var payment = new LoanPayment
            {
                PortfolioId = _portfolioId,
                Loan = loan,
                PeriodKey = "2026-07",
                DueDate = _now,
                PaidDate = _now,
                InterestAmount = 700m,
                PrincipalAmount = 550m,
                EscrowAmount = 0m,
                TotalAmount = 1250m,
                BalanceAfter = 179450m,
                Status = LoanPaymentStatus.Paid,
                CreatedAt = _now,
            };
            var connection = SeedBankConnection(db, "Loan already-posted bank");
            var transaction = SeedBankTransaction(
                db, connection.Id, "loan-already-posted", -1250m, property.Id);
            db.LoanPayments.Add(payment);
            await db.SaveChangesAsync();

            await using var postingTransaction = await db.Database.BeginTransactionAsync();
            var auditScope = new AtomicAuditScope(TimeProvider.System);
            var commandContext = new AtomicCommandContext(db, auditScope, TimeProvider.System);
            var attemptId = Guid.NewGuid();
            commandContext.BeginAttempt(attemptId);
            commandContext.BindReceipt(Guid.NewGuid());
            using var attempt = auditScope.BeginAttempt(
                new AtomicCommandIdentity("test.loan-payment.post", $"{_portfolioId}:{payment.Id}"),
                attemptId,
                db);
            await MoneyAccountingPosting.PostLoanPaymentAsync(
                db, commandContext, payment, auth.UserId);
            await commandContext.FlushBusinessAsync();
            await postingTransaction.CommitAsync();
            commandContext.EndAttempt();

            transactionId = transaction.Id;
            loanPaymentId = payment.Id;
        }

        var result = await ExecuteAtomicAsync(
            new AtomicCommandIdentity(
                "banking.transaction.reconcile",
                $"{_portfolioId}:{transactionId}:loan-already-posted"),
            ReconcileLoanPayment(transactionId, loanPaymentId, auth, "loan-already-posted"),
            ReconcileCodec);

        result.Value.Outcome.Should().Be(ReconcileBankTransactionOutcome.Applied);
        var clear = await ExecuteAtomicAsync(
            new AtomicCommandIdentity(
                "banking.transaction.reconcile",
                $"{_portfolioId}:{transactionId}:loan-already-posted-clear"),
            ReconcileClear(
                transactionId,
                auth,
                "loan-already-posted-clear",
                result.Value.Transaction!.UpdatedAt,
                _now.AddSeconds(2)),
            ReconcileCodec);

        clear.Value.Outcome.Should().Be(ReconcileBankTransactionOutcome.Applied);
        await using var verify = NewContext();
        (await verify.JournalEntries.CountAsync(entry =>
            entry.PortfolioId == _portfolioId
            && entry.SourceType == JournalSourceType.LoanPayment)).Should().Be(1);
        (await verify.JournalEntries.CountAsync(entry =>
            entry.PortfolioId == _portfolioId
            && entry.SourceType == JournalSourceType.LoanPayment
            && entry.ReversesJournalEntryId != null)).Should().Be(0);
        (await verify.JournalLines.CountAsync(line =>
            line.JournalEntry!.PortfolioId == _portfolioId
            && line.JournalEntry.SourceType == JournalSourceType.LoanPayment
            && line.CreditAmount > 0m)).Should().Be(1);
    }

    [SkippableFact]
    public async Task Reconciliation_MatchingAlreadyPostedOwnerDistributionDoesNotReverseItOnClear()
    {
        SkipIfNoDocker();
        var auth = await SeedAllPropertiesAuthorityAsync();
        int transactionId;
        int distributionId;
        await using (var db = NewContext())
        {
            var owner = new OwnerEntity
            {
                PortfolioId = _portfolioId,
                OwnerEntityType = OwnerEntityType.LLC,
                Name = "Already-posted owner",
                CreatedAt = _now,
                UpdatedAt = _now,
            };
            var distribution = new OwnerDistribution
            {
                PortfolioId = _portfolioId,
                OwnerEntity = owner,
                Date = _now,
                Amount = 900m,
                Method = DistributionMethod.Ach,
                Status = OwnerDistributionStatus.Approved,
                ApprovedAt = _now,
                ApprovedBusinessDate = _now,
                ApprovedByUserId = auth.UserId,
                CreatedAt = _now,
                UpdatedAt = _now,
            };
            var connection = SeedBankConnection(db, "Owner already-posted bank");
            var transaction = SeedBankTransaction(
                db, connection.Id, "owner-already-posted", -900m, null);
            db.OwnerDistributions.Add(distribution);
            await db.SaveChangesAsync();

            await using var postingTransaction = await db.Database.BeginTransactionAsync();
            var auditScope = new AtomicAuditScope(TimeProvider.System);
            var commandContext = new AtomicCommandContext(db, auditScope, TimeProvider.System);
            var attemptId = Guid.NewGuid();
            commandContext.BeginAttempt(attemptId);
            commandContext.BindReceipt(Guid.NewGuid());
            using var attempt = auditScope.BeginAttempt(
                new AtomicCommandIdentity("test.owner-distribution.post", $"{_portfolioId}:{distribution.Id}"),
                attemptId,
                db);
            await MoneyAccountingPosting.PostOwnerDistributionAsync(
                db, commandContext, distribution, auth.UserId);
            await commandContext.FlushBusinessAsync();
            await postingTransaction.CommitAsync();
            commandContext.EndAttempt();

            transactionId = transaction.Id;
            distributionId = distribution.Id;
        }

        var result = await ExecuteAtomicAsync(
            new AtomicCommandIdentity(
                "banking.transaction.reconcile",
                $"{_portfolioId}:{transactionId}:owner-already-posted"),
            ReconcileOwnerDistribution(
                transactionId, distributionId, auth, "owner-already-posted"),
            ReconcileCodec);
        result.Value.Outcome.Should().Be(ReconcileBankTransactionOutcome.Applied);
        var clear = await ExecuteAtomicAsync(
            new AtomicCommandIdentity(
                "banking.transaction.reconcile",
                $"{_portfolioId}:{transactionId}:owner-already-posted-clear"),
            ReconcileClear(
                transactionId,
                auth,
                "owner-already-posted-clear",
                result.Value.Transaction!.UpdatedAt,
                _now.AddSeconds(2)),
            ReconcileCodec);
        clear.Value.Outcome.Should().Be(ReconcileBankTransactionOutcome.Applied);

        await using var verify = NewContext();
        (await verify.JournalEntries.CountAsync(entry =>
            entry.PortfolioId == _portfolioId
            && entry.SourceType == JournalSourceType.OwnerDistribution)).Should().Be(1);
        (await verify.JournalEntries.CountAsync(entry =>
            entry.PortfolioId == _portfolioId
            && entry.SourceType == JournalSourceType.OwnerDistribution
            && entry.ReversesJournalEntryId != null)).Should().Be(0);
    }

    [SkippableFact]
    public async Task Reconciliation_MatchingUnpostedOwnerDistributionReversesOnlyMatchJournalOnClear()
    {
        SkipIfNoDocker();
        var auth = await SeedAllPropertiesAuthorityAsync();
        int transactionId;
        int distributionId;
        await using (var db = NewContext())
        {
            var owner = new OwnerEntity
            {
                PortfolioId = _portfolioId,
                OwnerEntityType = OwnerEntityType.LLC,
                Name = "Match-created owner",
                CreatedAt = _now,
                UpdatedAt = _now,
            };
            var distribution = new OwnerDistribution
            {
                PortfolioId = _portfolioId,
                OwnerEntity = owner,
                Date = _now,
                Amount = 700m,
                Method = DistributionMethod.Ach,
                Status = OwnerDistributionStatus.Approved,
                ApprovedAt = _now,
                ApprovedBusinessDate = _now,
                ApprovedByUserId = auth.UserId,
                CreatedAt = _now,
                UpdatedAt = _now,
            };
            var connection = SeedBankConnection(db, "Match-created owner bank");
            var transaction = SeedBankTransaction(
                db, connection.Id, "owner-match-created", -700m, null);
            db.OwnerDistributions.Add(distribution);
            await db.SaveChangesAsync();
            transactionId = transaction.Id;
            distributionId = distribution.Id;
        }

        var result = await ExecuteAtomicAsync(
            new AtomicCommandIdentity(
                "banking.transaction.reconcile",
                $"{_portfolioId}:{transactionId}:owner-match-created"),
            ReconcileOwnerDistribution(
                transactionId, distributionId, auth, "owner-match-created"),
            ReconcileCodec);
        result.Value.Outcome.Should().Be(ReconcileBankTransactionOutcome.Applied);
        var clear = await ExecuteAtomicAsync(
            new AtomicCommandIdentity(
                "banking.transaction.reconcile",
                $"{_portfolioId}:{transactionId}:owner-match-created-clear"),
            ReconcileClear(
                transactionId,
                auth,
                "owner-match-created-clear",
                result.Value.Transaction!.UpdatedAt,
                _now.AddSeconds(2)),
            ReconcileCodec);
        clear.Value.Outcome.Should().Be(ReconcileBankTransactionOutcome.Applied);

        await using var verify = NewContext();
        (await verify.JournalEntries.CountAsync(entry =>
            entry.PortfolioId == _portfolioId
            && entry.SourceType == JournalSourceType.OwnerDistribution)).Should().Be(2);
        (await verify.JournalEntries.CountAsync(entry =>
            entry.PortfolioId == _portfolioId
            && entry.SourceType == JournalSourceType.OwnerDistribution
            && entry.ReversesJournalEntryId != null)).Should().Be(1);
        (await verify.JournalEntries.CountAsync(entry =>
            entry.PortfolioId == _portfolioId
            && entry.SourceType == JournalSourceType.OwnerDistribution
            && entry.SourceBusinessKey == $"bank-owner-distribution:{transactionId}"))
            .Should().Be(1);
    }

    [SkippableFact]
    public async Task Reconciliation_ConcurrentDifferentBankLines_CannotClaimSameOwnerDistribution()
    {
        SkipIfNoDocker();
        var auth = await SeedAllPropertiesAuthorityAsync();
        int firstTransactionId;
        int secondTransactionId;
        int distributionId;
        await using (var db = NewContext())
        {
            var owner = new OwnerEntity
            {
                PortfolioId = _portfolioId,
                OwnerEntityType = OwnerEntityType.LLC,
                Name = "Atomic Owner LLC",
                CreatedAt = _now,
                UpdatedAt = _now,
            };
            var distribution = new OwnerDistribution
            {
                PortfolioId = _portfolioId,
                OwnerEntity = owner,
                Date = _now,
                Amount = 900m,
                Method = DistributionMethod.Ach,
                Status = OwnerDistributionStatus.Approved,
                ApprovedAt = _now,
                ApprovedBusinessDate = _now,
                ApprovedByUserId = auth.UserId,
                CreatedAt = _now,
                UpdatedAt = _now,
            };
            var connection = SeedBankConnection(db, "Owner duplicate bank");
            var first = SeedBankTransaction(db, connection.Id, "owner-duplicate-first", -900m, null);
            var second = SeedBankTransaction(db, connection.Id, "owner-duplicate-second", -900m, null);
            db.OwnerDistributions.Add(distribution);
            await db.SaveChangesAsync();
            firstTransactionId = first.Id;
            secondTransactionId = second.Id;
            distributionId = distribution.Id;
        }

        var outcomes = await Task.WhenAll(
            ExecuteAtomicAsync(
                new AtomicCommandIdentity("banking.transaction.reconcile", $"{_portfolioId}:{firstTransactionId}:owner-duplicate-first"),
                ReconcileOwnerDistribution(firstTransactionId, distributionId, auth, "owner-duplicate-first"),
                ReconcileCodec),
            ExecuteAtomicAsync(
                new AtomicCommandIdentity("banking.transaction.reconcile", $"{_portfolioId}:{secondTransactionId}:owner-duplicate-second"),
                ReconcileOwnerDistribution(secondTransactionId, distributionId, auth, "owner-duplicate-second"),
                ReconcileCodec));

        outcomes.Count(result => result.Value.Outcome == ReconcileBankTransactionOutcome.Applied).Should().Be(1);
        outcomes.Count(result => result.Value.Outcome == ReconcileBankTransactionOutcome.TargetNotFound).Should().Be(1);
        await using var verify = NewContext();
        var matchedRows = await verify.BankTransactions.AsNoTracking()
            .Where(row => row.PortfolioId == _portfolioId && row.MatchedOwnerDistributionId == distributionId)
            .Select(row => row.Id)
            .ToListAsync();
        matchedRows.Should().ContainSingle();
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

    private async Task<AtomicTestAuthority> SeedAllPropertiesAuthorityAsync()
    {
        await using var db = NewContext();
        const int userId = 801;
        var sessionId = Guid.NewGuid();
        var authorizationNow = DateTime.UtcNow;
        var user = await db.Users.FindAsync(userId);
        if (user is null)
        {
            user = new ApplicationUser
            {
                Id = userId,
                UserName = "banking-atomic@example.test",
                NormalizedUserName = "BANKING-ATOMIC@EXAMPLE.TEST",
                Email = "banking-atomic@example.test",
                NormalizedEmail = "BANKING-ATOMIC@EXAMPLE.TEST",
                DisplayName = "Banking Atomic",
                CreatedAt = authorizationNow,
            };
            db.Users.Add(user);
        }
        var accessContext = new WorkspaceAccessContext
        {
            UserId = userId,
            PortfolioId = _portfolioId,
            Status = WorkspaceAccessContextStatus.Active,
            CreatedAtUtc = authorizationNow,
            UpdatedAtUtc = authorizationNow,
        };
        var membership = new WorkspaceMembership
        {
            AccessContext = accessContext,
            PortfolioId = _portfolioId,
            Status = WorkspaceMembershipStatus.Active,
            EffectiveFromUtc = authorizationNow.AddDays(-1),
            CreatedAtUtc = authorizationNow,
            UpdatedAtUtc = authorizationNow,
        };
        membership.RoleAssignments.Add(new MembershipRoleAssignment
        {
            PortfolioId = _portfolioId,
            RoleProfileId = 1,
            ScopeKind = MembershipRoleAssignmentScopeKind.AllProperties,
            Status = MembershipRoleAssignmentStatus.Active,
            EffectiveFromUtc = authorizationNow.AddDays(-1),
            CreatedAtUtc = authorizationNow,
            UpdatedAtUtc = authorizationNow,
        });
        db.WorkspaceMemberships.Add(membership);
        db.AuthSessions.Add(new AuthSession
        {
            Id = sessionId,
            UserId = userId,
            ActiveAccessContext = accessContext,
            Status = AuthSessionStatus.Active,
            CreatedAtUtc = authorizationNow,
            LastSeenAtUtc = authorizationNow,
            ExpiresAtUtc = authorizationNow.AddHours(1),
        });
        await db.SaveChangesAsync();
        return new AtomicTestAuthority(userId, sessionId, accessContext.Id, accessContext.AccessRevision);
    }

    [SkippableFact]
    public async Task Reconciliation_RevalidatesDisplayedExpenseCandidateInsideWriteTransaction()
    {
        SkipIfNoDocker();
        var auth = await SeedAllPropertiesAuthorityAsync();
        int transactionId;
        int expenseId;

        await using (var db = NewContext())
        {
            var property = SeedProperty(db, "Commit-time candidate property");
            var expense = new Expense
            {
                PortfolioId = _portfolioId,
                OperationalScope = ExpenseOperationalScope.Property,
                PropertyId = property.Id,
                Category = ScheduleECategory.Repairs,
                Description = "Displayed reconciliation candidate",
                Status = ExpenseStatus.Approved,
                Amount = 425m,
                IncurredAt = _now,
                CreatedAt = _now,
                UpdatedAt = _now,
            };
            var connection = SeedBankConnection(db, "Commit-time candidate bank");
            var transaction = SeedBankTransaction(
                db, connection.Id, "commit-time-expense-candidate", -expense.Amount, property.Id);
            db.Expenses.Add(expense);
            await db.SaveChangesAsync();
            transactionId = transaction.Id;
            expenseId = expense.Id;

            (await BankReconciliationCandidateQuery.EligibleExpenses(
                    db.BankTransactions.AsNoTracking().Where(row => row.Id == transactionId),
                    db.Expenses.AsNoTracking())
                .AnyAsync(candidate => candidate.Target.Id == expenseId))
                .Should().BeTrue("the candidate was displayed from the canonical selection query");
        }

        await using (var concurrent = NewContext())
        {
            var expense = await concurrent.Expenses.SingleAsync(row => row.Id == expenseId);
            expense.DeletedAt = _now.AddMinutes(1);
            expense.UpdatedAt = _now.AddMinutes(1);
            await concurrent.SaveChangesAsync();
        }

        Recorder.Clear();
        var result = await ExecuteAtomicAsync(
            new AtomicCommandIdentity(
                "banking.transaction.reconcile",
                $"{_portfolioId}:{transactionId}:commit-time-candidate"),
            ReconcileExpense(transactionId, expenseId, auth, "commit-time-candidate"),
            ReconcileCodec);

        result.Value.Outcome.Should().Be(ReconcileBankTransactionOutcome.TargetNotFound);
        Recorder.Commands.Should().ContainSingle(sql =>
            sql.Contains("FROM \"Expenses\"", StringComparison.OrdinalIgnoreCase)
            && sql.Contains("EXISTS", StringComparison.OrdinalIgnoreCase)
            && sql.Contains("DeletedAt", StringComparison.OrdinalIgnoreCase),
            "the locked write transaction must re-run canonical candidate eligibility in one SQL statement");

        await using var verify = NewContext();
        var bankState = await verify.BankTransactions.AsNoTracking()
            .Where(row => row.Id == transactionId)
            .Select(row => new { row.MatchStatus, row.MatchedExpenseId })
            .SingleAsync();
        bankState.MatchStatus.Should().Be("Unmatched");
        bankState.MatchedExpenseId.Should().BeNull();
    }

    private ReconcileBankTransactionCommand ReconcileLoanPayment(
        int transactionId,
        int loanPaymentId,
        AtomicTestAuthority auth,
        string operationKey) => new(
        _portfolioId,
        transactionId,
        BankReconciliationAction.MatchLoanPayment,
        null,
        null,
        null,
        loanPaymentId,
        null,
        null,
        null,
        _now,
        _now.AddSeconds(1),
        auth.UserId,
        auth.SessionId,
        auth.AccessContextId,
        auth.AccessRevision,
        CapabilityKeys.MoneyReconciliationOperate,
        operationKey);

    private ReconcileBankTransactionCommand ReconcileExpense(
        int transactionId,
        int expenseId,
        AtomicTestAuthority auth,
        string operationKey) => new(
        _portfolioId,
        transactionId,
        BankReconciliationAction.MatchExpense,
        null,
        null,
        expenseId,
        null,
        null,
        null,
        null,
        _now,
        _now.AddSeconds(1),
        auth.UserId,
        auth.SessionId,
        auth.AccessContextId,
        auth.AccessRevision,
        CapabilityKeys.MoneyReconciliationOperate,
        operationKey);

    private ReconcileBankTransactionCommand ReconcileClear(
        int transactionId,
        AtomicTestAuthority auth,
        string operationKey,
        DateTime expectedUpdatedAtUtc,
        DateTime appliedAtUtc) => new(
        _portfolioId,
        transactionId,
        BankReconciliationAction.Clear,
        null,
        null,
        null,
        null,
        null,
        null,
        null,
        expectedUpdatedAtUtc,
        appliedAtUtc,
        auth.UserId,
        auth.SessionId,
        auth.AccessContextId,
        auth.AccessRevision,
        CapabilityKeys.MoneyReconciliationDestructive,
        operationKey);

    private ReconcileBankTransactionCommand ReconcileOwnerDistribution(
        int transactionId,
        int ownerDistributionId,
        AtomicTestAuthority auth,
        string operationKey) => new(
        _portfolioId,
        transactionId,
        BankReconciliationAction.MatchOwnerDistribution,
        null,
        null,
        null,
        null,
        ownerDistributionId,
        null,
        null,
        _now,
        _now.AddSeconds(1),
        auth.UserId,
        auth.SessionId,
        auth.AccessContextId,
        auth.AccessRevision,
        CapabilityKeys.MoneyReconciliationOperate,
        operationKey);

    private Property SeedProperty(RentalCommandDbContext db, string name)
    {
        var property = new Property
        {
            PortfolioId = _portfolioId,
            Name = name,
            AddressLine1 = "1 Atomic Lane",
            City = "Columbus",
            State = "OH",
            PostalCode = "43215",
            CreatedAt = _now,
            UpdatedAt = _now,
        };
        db.Properties.Add(property);
        db.SaveChanges();
        return property;
    }

    private BankConnection SeedBankConnection(RentalCommandDbContext db, string institutionName)
    {
        var connection = new BankConnection
        {
            PortfolioId = _portfolioId,
            Provider = "Manual",
            InstitutionName = institutionName,
            AccountName = "Operating checking",
            Status = "Active",
            CreatedAt = _now,
            UpdatedAt = _now,
        };
        db.BankConnections.Add(connection);
        db.SaveChanges();
        return connection;
    }

    private BankTransaction SeedBankTransaction(
        RentalCommandDbContext db,
        int connectionId,
        string providerTransactionId,
        decimal amount,
        int? propertyId)
    {
        var transaction = new BankTransaction
        {
            PortfolioId = _portfolioId,
            BankConnectionId = connectionId,
            ProviderTransactionId = providerTransactionId,
            PostedAt = _now,
            Description = providerTransactionId,
            Amount = amount,
            IsoCurrencyCode = "USD",
            PropertyId = propertyId,
            MatchStatus = "Unmatched",
            CreatedAt = _now,
            UpdatedAt = _now,
        };
        db.BankTransactions.Add(transaction);
        db.SaveChanges();
        return transaction;
    }

    private sealed record AtomicTestAuthority(
        int UserId,
        Guid SessionId,
        int AccessContextId,
        long AccessRevision);

    private async Task<AtomicCommandOutcome<TResult>> ExecuteAtomicAsync<TCommand, TResult>(
        AtomicCommandIdentity identity,
        TCommand command,
        AtomicJsonResultCodec<TResult> codec,
        CancellationToken ct = default)
        where TCommand : notnull, IAtomicCommandData
        where TResult : notnull
    {
        await using var scope = _services!.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<RentalCommandDbContext>();
        var write = BankingWriteSupport.Write<TCommand, TResult>(db, command);
        write.OperationName.Should().Be(identity.CommandType);
        write.ResultContract.Should().Be(codec.ContractName);
        return await scope.ServiceProvider.GetRequiredService<IRequestWriteExecutor>()
            .ExecuteExactAsync(identity.IdempotencyKey, write, ct);
    }

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
        private readonly ConcurrentQueue<object?> _parameterValues = new();
        public IReadOnlyCollection<string> Commands => _commands.ToArray();
        public IReadOnlyCollection<object?> ParameterValues => _parameterValues.ToArray();
        public void Clear()
        {
            while (_commands.TryDequeue(out _)) { }
            while (_parameterValues.TryDequeue(out _)) { }
        }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            _commands.Enqueue(command.CommandText);
            foreach (DbParameter parameter in command.Parameters)
            {
                _parameterValues.Enqueue(parameter.Value);
            }
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
