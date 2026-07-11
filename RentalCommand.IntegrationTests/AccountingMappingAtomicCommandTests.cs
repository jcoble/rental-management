using System.Collections.Concurrent;
using System.Data.Common;
using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using RentalCommand.Core.Accounting;
using RentalCommand.Core.Atomic;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Interfaces;
using RentalCommand.Core.Models.Accounting;
using RentalCommand.Data;
using RentalCommand.Data.Accounting;
using RentalCommand.Data.Atomic;
using Testcontainers.PostgreSql;
using Xunit;

namespace RentalCommand.IntegrationTests;

public sealed class AccountingMappingAtomicCommandTests : IAsyncLifetime
{
    private static readonly AtomicJsonResultCodec<ConfirmAccountingMappingResult> Codec =
        new("accounting.mapping.confirm.result.v2");
    private readonly DateTime _now = new(2026, 7, 11, 17, 0, 0, DateTimeKind.Utc);
    private PostgreSqlContainer? _postgres;
    private ServiceProvider? _services;
    private bool _dockerAvailable;
    private int _portfolioId;
    private int _otherPortfolioId;
    private int _connectionId;
    private int _tenantId;
    private int _secondTenantId;
    private int _otherTenantId;

    public async Task InitializeAsync()
    {
        try
        {
            _postgres = new PostgreSqlBuilder()
                .WithImage("postgres:16-alpine")
                .WithDatabase("rentalcommand_accounting_mapping")
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
        services.AddSingleton<OutboxFailureInterceptor>();
        services.AddScoped<ICurrentActor, TestActor>();
        services.AddAtomicPersistenceKernel();
        services.AddAtomicCommandHandler<
            ConfirmAccountingMappingCommand,
            ConfirmAccountingMappingResult,
            ConfirmAccountingMappingHandler>();
        services.AddAtomicCommandHandler<
            ContinueAccountingMappingPromotionCommand,
            ContinueAccountingMappingPromotionResult,
            ContinueAccountingMappingPromotionHandler>();
        services.AddDbContext<RentalCommandDbContext>((provider, options) =>
            options.UseNpgsql(_postgres!.GetConnectionString())
                .UseAtomicPersistenceKernel(provider)
                .AddInterceptors(
                    provider.GetRequiredService<CommandRecorder>(),
                    provider.GetRequiredService<OutboxFailureInterceptor>()));
        _services = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true,
        });

        await using var db = NewContext();
        await db.Database.MigrateAsync();
        await SeedAsync(db);
    }

    public async Task DisposeAsync()
    {
        if (_services is not null) await _services.DisposeAsync();
        if (_postgres is not null) await _postgres.DisposeAsync();
    }

    [SkippableFact]
    public async Task Confirm_CommitsMappingPromotionsAuditOutboxReceipt_AndConcurrentReplayIsCanonical()
    {
        SkipIfNoDocker();
        await SeedParkedPaymentAndExpenseAsync("customer-main", "payment-main", "expense-main");
        var identity = Identity("customer-main", _tenantId);
        var command = Command("customer-main", _tenantId);
        Recorder.Clear();

        var outcomes = await Task.WhenAll(
            Atomic.ExecuteAsync(identity, command, Codec),
            Atomic.ExecuteAsync(identity, command, Codec));

        outcomes.Select(outcome => outcome.Disposition).Should()
            .BeEquivalentTo([AtomicCommandDisposition.Executed, AtomicCommandDisposition.Replayed]);
        outcomes[0].Value.Should().BeEquivalentTo(outcomes[1].Value);
        outcomes[0].Value.PromotedCount.Should().Be(2);

        await using var db = NewContext();
        (await db.AccountingEntityMappings.CountAsync(mapping =>
            mapping.PortfolioId == _portfolioId
            && mapping.AccountingConnectionId == _connectionId
            && mapping.ExternalType == ExternalKind.Customer
            && mapping.ExternalId == "customer-main")).Should().Be(1);
        (await db.Payments.CountAsync(payment => payment.PortfolioId == _portfolioId)).Should().Be(1);
        (await db.Expenses.CountAsync(expense => expense.PortfolioId == _portfolioId)).Should().Be(1);
        (await db.AccountingSyncMaps.CountAsync(ledger =>
            ledger.PortfolioId == _portfolioId
            && ledger.Status == LedgerStatus.Imported)).Should().Be(2);
        (await db.AtomicCommandReceipts.CountAsync(receipt =>
            receipt.CommandType == identity.CommandType
            && receipt.IdempotencyKey == identity.IdempotencyKey)).Should().Be(1);
        (await db.AtomicAuditLogs.CountAsync(audit =>
            audit.CommandType == identity.CommandType
            && audit.CommandIdempotencyKey == identity.IdempotencyKey)).Should().BeGreaterThanOrEqualTo(5);
        (await db.Notifications.CountAsync(notification =>
            notification.PortfolioId == _portfolioId
            && notification.Type == "AccountingMappingConfirmed")).Should().Be(1);
        (await db.OutboxMessages.CountAsync(message =>
            message.PortfolioId == _portfolioId
            && message.MessageType == "push")).Should().Be(1);

        var promotionReads = Recorder.Commands
            .Where(sql => sql.Contains("AccountingSyncMaps", StringComparison.Ordinal)
                && sql.Contains("vw_accounting_parked_transactions", StringComparison.Ordinal)
                && sql.Contains("LIMIT", StringComparison.OrdinalIgnoreCase))
            .ToList();
        promotionReads.Should().HaveCount(2,
            "confirmation runs exactly one bounded payment query and one bounded expense query");
        Recorder.Commands.Count(sql => sql.TrimStart().StartsWith("SELECT", StringComparison.OrdinalIgnoreCase))
            .Should().BeLessThan(20, "promotion query count is batch-bounded, not row-count-driven");
    }

    [SkippableFact]
    public async Task FinalCompanionFailure_RollsBackMappingNotificationOutboxAuditAndReceipt()
    {
        SkipIfNoDocker();
        var identity = Identity("customer-rollback", _tenantId);
        Failure.FailOutboxInsert = true;

        var act = async () => await Atomic.ExecuteAsync(
            identity,
            Command("customer-rollback", _tenantId),
            Codec);
        var failure = await act.Should().ThrowAsync<DbUpdateException>();
        failure.Which.InnerException.Should().BeOfType<InjectedOutboxFailure>();
        Failure.FailOutboxInsert = false;

        await using var db = NewContext();
        (await db.AccountingEntityMappings.CountAsync(mapping =>
            mapping.AccountingConnectionId == _connectionId
            && mapping.ExternalId == "customer-rollback")).Should().Be(0);
        (await db.Notifications.CountAsync(notification =>
            notification.PortfolioId == _portfolioId
            && notification.Type == "AccountingMappingConfirmed")).Should().Be(0);
        (await db.OutboxMessages.CountAsync(message =>
            message.PortfolioId == _portfolioId)).Should().Be(0);
        (await db.AtomicAuditLogs.CountAsync(audit =>
            audit.CommandType == identity.CommandType
            && audit.CommandIdempotencyKey == identity.IdempotencyKey)).Should().Be(0);
        (await db.AtomicCommandReceipts.CountAsync(receipt =>
            receipt.CommandType == identity.CommandType
            && receipt.IdempotencyKey == identity.IdempotencyKey)).Should().Be(0);

        var recovered = await Atomic.ExecuteAsync(
            identity,
            Command("customer-rollback", _tenantId),
            Codec);
        recovered.Disposition.Should().Be(AtomicCommandDisposition.Executed);
        await using var recoveredDb = NewContext();
        (await recoveredDb.AccountingEntityMappings.CountAsync(mapping =>
            mapping.AccountingConnectionId == _connectionId
            && mapping.ExternalId == "customer-rollback")).Should().Be(1);
        (await recoveredDb.AtomicCommandReceipts.CountAsync(receipt =>
            receipt.CommandType == identity.CommandType
            && receipt.IdempotencyKey == identity.IdempotencyKey)).Should().Be(1);
    }

    [SkippableFact]
    public async Task CrossPortfolioTarget_IsRejectedWithoutMappingOrPromotion()
    {
        SkipIfNoDocker();
        await SeedParkedPaymentAndExpenseAsync("customer-cross", "payment-cross", "expense-cross");
        var identity = Identity("customer-cross", _otherTenantId);

        var outcome = await Atomic.ExecuteAsync(
            identity,
            Command("customer-cross", _otherTenantId),
            Codec);

        outcome.Value.Outcome.Should().Be(ConfirmAccountingMappingOutcome.InvalidTarget);
        await using var db = NewContext();
        (await db.AccountingEntityMappings.CountAsync(mapping =>
            mapping.AccountingConnectionId == _connectionId
            && mapping.ExternalId == "customer-cross")).Should().Be(0);
        (await db.Payments.CountAsync(payment => payment.PortfolioId == _portfolioId)).Should().Be(0);
        (await db.Expenses.CountAsync(expense => expense.PortfolioId == _portfolioId)).Should().Be(0);
        (await db.AccountingSyncMaps.CountAsync(ledger =>
            ledger.AccountingConnectionId == _connectionId
            && ledger.Status == LedgerStatus.Imported)).Should().Be(0);
    }

    [SkippableFact]
    public async Task CorrectedRequestIdentity_ReplaysEachRequestAndNeverDuplicatesPromotedRows()
    {
        SkipIfNoDocker();
        await SeedParkedPaymentAndExpenseAsync("customer-correction", "payment-correction", "expense-correction");
        var firstIdentity = Identity("customer-correction", _tenantId, "first");
        var secondIdentity = Identity("customer-correction", _tenantId, "corrected");

        var first = await Atomic.ExecuteAsync(
            firstIdentity,
            Command("customer-correction", _tenantId, "first"),
            Codec);
        var corrected = await Atomic.ExecuteAsync(
            secondIdentity,
            Command("customer-correction", _tenantId, "corrected", expectedRevision: 1),
            Codec);
        var replay = await Atomic.ExecuteAsync(
            secondIdentity,
            Command("customer-correction", _tenantId, "corrected", expectedRevision: 1),
            Codec);

        first.Value.PromotedCount.Should().Be(2);
        corrected.Disposition.Should().Be(AtomicCommandDisposition.Executed);
        corrected.Value.PromotedCount.Should().Be(0);
        replay.Disposition.Should().Be(AtomicCommandDisposition.Replayed);
        replay.Value.Should().BeEquivalentTo(corrected.Value);
        await using var db = NewContext();
        (await db.Payments.CountAsync(payment => payment.PortfolioId == _portfolioId)).Should().Be(1);
        (await db.Expenses.CountAsync(expense => expense.PortfolioId == _portfolioId)).Should().Be(1);
        (await db.AccountingSyncMaps.CountAsync(ledger =>
            ledger.AccountingConnectionId == _connectionId
            && ledger.Status == LedgerStatus.Imported)).Should().Be(2);
        (await db.AtomicCommandReceipts.CountAsync(receipt =>
            receipt.CommandType == firstIdentity.CommandType
            && (receipt.IdempotencyKey == firstIdentity.IdempotencyKey
                || receipt.IdempotencyKey == secondIdentity.IdempotencyKey))).Should().Be(2);
        (await db.AtomicAuditLogs.CountAsync(audit =>
            audit.CommandType == firstIdentity.CommandType
            && audit.CommandIdempotencyKey == firstIdentity.IdempotencyKey
            && audit.EntityType == nameof(AccountingEntityMapping))).Should().Be(1);
        var correctedAudit = await db.AtomicAuditLogs.SingleAsync(audit =>
            audit.CommandType == secondIdentity.CommandType
            && audit.CommandIdempotencyKey == secondIdentity.IdempotencyKey
            && audit.EntityType == nameof(AccountingEntityMapping));
        correctedAudit.NewValues.Should().NotBeNull();
        using var correctedAuditValues = JsonDocument.Parse(correctedAudit.NewValues!);
        correctedAuditValues.RootElement.GetProperty("ClientOperationId").GetString()
            .Should().Be("corrected");
        correctedAuditValues.RootElement.GetProperty("Revision").GetInt64()
            .Should().Be(2);
        var mapping = await db.AccountingEntityMappings.SingleAsync(row =>
            row.AccountingConnectionId == _connectionId
            && row.ExternalId == "customer-correction");
        mapping.ConfirmedAt.Should().Be(
            _now.AddMinutes(5).AddTicks(TimeSpan.TicksPerMicrosecond),
            "a correction at the same requested instant must still receive the next PostgreSQL-representable mutation timestamp");
        mapping.UpdatedAt.Should().Be(mapping.ConfirmedAt);
    }

    [SkippableFact]
    public async Task MoreThan256_PromotesFixedBatches_AndConcurrentContinuationReplaysWithoutDuplicates()
    {
        SkipIfNoDocker();
        await SeedParkedPaymentsAsync("customer-large", 300);
        var confirmed = await Atomic.ExecuteAsync(
            Identity("customer-large", _tenantId, "large-confirm"),
            Command("customer-large", _tenantId, "large-confirm"),
            Codec);

        confirmed.Value.PromotedCount.Should().Be(256);
        confirmed.Value.HasMore.Should().BeTrue();
        confirmed.Value.ContinuationId.Should().NotBeNull();
        var continuationId = confirmed.Value.ContinuationId!.Value;
        var command = new ContinueAccountingMappingPromotionCommand(
            _portfolioId,
            _connectionId,
            continuationId,
            701,
            "large-batch-2",
            _now.AddMinutes(6));
        var identity = new AtomicCommandIdentity(
            "accounting.mapping.promote.continue",
            $"{_portfolioId}:{_connectionId}:{continuationId:N}:large-batch-2");
        var codec = new AtomicJsonResultCodec<ContinueAccountingMappingPromotionResult>(
            "accounting.mapping.promote.continue.result.v1");
        var outcomes = await Task.WhenAll(
            Atomic.ExecuteAsync(identity, command, codec),
            Atomic.ExecuteAsync(identity, command, codec));

        outcomes.Select(row => row.Disposition).Should()
            .BeEquivalentTo([AtomicCommandDisposition.Executed, AtomicCommandDisposition.Replayed]);
        outcomes[0].Value.Should().BeEquivalentTo(outcomes[1].Value);
        outcomes[0].Value.PromotedCount.Should().Be(44);
        outcomes[0].Value.HasMore.Should().BeFalse();
        await using var db = NewContext();
        (await db.Payments.CountAsync(row => row.PortfolioId == _portfolioId)).Should().Be(300);
        (await db.AccountingSyncMaps.CountAsync(row => row.PortfolioId == _portfolioId
            && row.Status == LedgerStatus.Imported)).Should().Be(300);
        (await db.AccountingMappingPromotionJobs.SingleAsync(row => row.Id == continuationId))
            .CompletedAtUtc.Should().NotBeNull();
    }

    [SkippableFact]
    public async Task AToBToA_CorrectionsRequireSuccessiveRevisions_AndRetainActorOperationAudit()
    {
        SkipIfNoDocker();
        const string externalId = "customer-a-b-a";
        await Atomic.ExecuteAsync(
            Identity(externalId, _tenantId, "operation-a1"),
            Command(externalId, _tenantId, "operation-a1", expectedRevision: 0, confirmedByUserId: 701),
            Codec);
        await Atomic.ExecuteAsync(
            Identity(externalId, _secondTenantId, "operation-b"),
            Command(externalId, _secondTenantId, "operation-b", expectedRevision: 1, confirmedByUserId: 702),
            Codec);
        var returned = await Atomic.ExecuteAsync(
            Identity(externalId, _tenantId, "operation-a2"),
            Command(externalId, _tenantId, "operation-a2", expectedRevision: 2, confirmedByUserId: 701),
            Codec);

        returned.Value.MappingRevision.Should().Be(3);
        await using var db = NewContext();
        var mapping = await db.AccountingEntityMappings.SingleAsync(row =>
            row.AccountingConnectionId == _connectionId && row.ExternalId == externalId);
        mapping.LocalEntityId.Should().Be(_tenantId);
        mapping.Revision.Should().Be(3);
        var audits = await db.AtomicAuditLogs
            .Where(row => row.EntityType == nameof(AccountingEntityMapping) && row.EntityId == mapping.Id)
            .OrderBy(row => row.Id)
            .Select(row => new { row.UserId, row.NewValues })
            .ToListAsync();
        audits.Should().HaveCount(3);
        audits.Should().Contain(row => row.UserId == 702 && row.NewValues!.Contains("operation-b"));
        audits.Last().NewValues.Should().Contain("operation-a2");
    }

    [SkippableFact]
    public async Task ConcurrentCorrectionsFromSameRevision_AllowOneMutationAndReturnOneStaleRevision()
    {
        SkipIfNoDocker();
        const string externalId = "customer-concurrent-revision";
        await Atomic.ExecuteAsync(
            Identity(externalId, _tenantId, "seed"),
            Command(externalId, _tenantId, "seed"),
            Codec);

        var corrections = await Task.WhenAll(
            Atomic.ExecuteAsync(
                Identity(externalId, _tenantId, "correction-a"),
                Command(externalId, _tenantId, "correction-a", expectedRevision: 1, confirmedByUserId: 701),
                Codec),
            Atomic.ExecuteAsync(
                Identity(externalId, _secondTenantId, "correction-b"),
                Command(externalId, _secondTenantId, "correction-b", expectedRevision: 1, confirmedByUserId: 702),
                Codec));

        corrections.Count(row => row.Value.Outcome == ConfirmAccountingMappingOutcome.Applied).Should().Be(1);
        corrections.Count(row => row.Value.Outcome == ConfirmAccountingMappingOutcome.StaleRevision).Should().Be(1);
        corrections.Single(row => row.Value.Outcome == ConfirmAccountingMappingOutcome.StaleRevision)
            .Value.MappingRevision.Should().Be(2);
        await using var db = NewContext();
        var mapping = await db.AccountingEntityMappings.SingleAsync(row =>
            row.AccountingConnectionId == _connectionId && row.ExternalId == externalId);
        mapping.Revision.Should().Be(2);
        mapping.LocalEntityId.Should().BeOneOf(_tenantId, _secondTenantId);
        (await db.AtomicAuditLogs.CountAsync(row =>
            row.EntityType == nameof(AccountingEntityMapping) && row.EntityId == mapping.Id)).Should().Be(2);
    }

    private IAtomicUnitOfWork Atomic => _services!.GetRequiredService<IAtomicUnitOfWork>();
    private CommandRecorder Recorder => _services!.GetRequiredService<CommandRecorder>();
    private OutboxFailureInterceptor Failure => _services!.GetRequiredService<OutboxFailureInterceptor>();

    private AtomicCommandIdentity Identity(string externalId, int tenantId, string suffix = "canonical") =>
        new("accounting.mapping.confirm", $"{_portfolioId}:{_connectionId}:{externalId}:{tenantId}:{suffix}");

    private ConfirmAccountingMappingCommand Command(
        string externalId,
        int tenantId,
        string requestIdentity = "canonical",
        long expectedRevision = 0,
        int confirmedByUserId = 701) => new(
            _portfolioId,
            _connectionId,
            AccountingProvider.QuickBooks,
            confirmedByUserId,
            ExternalKind.Customer,
            externalId,
            "Mapped tenant",
            LocalEntityKind.Tenant,
            tenantId,
            null,
            requestIdentity,
            expectedRevision,
            _now.AddMinutes(5));

    private async Task SeedAsync(RentalCommandDbContext db)
    {
        var portfolio = new Portfolio
        {
            Name = "Accounting atomic",
            TimeZone = "UTC",
            CreatedAt = _now,
            UpdatedAt = _now,
        };
        var other = new Portfolio
        {
            Name = "Other portfolio",
            TimeZone = "UTC",
            CreatedAt = _now,
            UpdatedAt = _now,
        };
        db.Portfolios.AddRange(portfolio, other);
        await db.SaveChangesAsync();
        _portfolioId = portfolio.Id;
        _otherPortfolioId = other.Id;

        var property = Property(_portfolioId, "Primary property");
        var otherProperty = Property(_otherPortfolioId, "Other property");
        var tenant = Tenant(_portfolioId, "Primary");
        var secondTenant = Tenant(_portfolioId, "Second");
        var otherTenant = Tenant(_otherPortfolioId, "Other");
        var vendor = new Vendor
        {
            PortfolioId = _portfolioId,
            Name = "Mapped vendor",
            CreatedAt = _now,
            UpdatedAt = _now,
        };
        db.AddRange(property, otherProperty, tenant, secondTenant, otherTenant, vendor);
        await db.SaveChangesAsync();
        _tenantId = tenant.Id;
        _secondTenantId = secondTenant.Id;
        _otherTenantId = otherTenant.Id;

        var unit = new Unit { PropertyId = property.Id, UnitNumber = "1", CreatedAt = _now, UpdatedAt = _now };
        db.Units.Add(unit);
        await db.SaveChangesAsync();
        db.Leases.Add(new Lease
        {
            PortfolioId = _portfolioId,
            PropertyId = property.Id,
            UnitId = unit.Id,
            TenantId = tenant.Id,
            LeaseNumber = "L-ATOMIC",
            Status = LeaseStatus.Active,
            StartDate = _now.AddMonths(-1),
            EndDate = _now.AddYears(1),
            MonthlyRent = 1500m,
            RentDueDay = 1,
            CreatedAt = _now,
            UpdatedAt = _now,
        });
        var connection = new AccountingConnection
        {
            PortfolioId = _portfolioId,
            Provider = AccountingProvider.QuickBooks,
            Status = AccountingConnectionStatus.Connected,
            CreatedAt = _now,
            UpdatedAt = _now,
        };
        db.AccountingConnections.Add(connection);
        await db.SaveChangesAsync();
        _connectionId = connection.Id;
        db.AccountingEntityMappings.Add(new AccountingEntityMapping
        {
            PortfolioId = _portfolioId,
            AccountingConnectionId = _connectionId,
            ExternalType = ExternalKind.Vendor,
            ExternalId = "vendor-main",
            ExternalDisplayName = vendor.Name,
            LocalEntityType = LocalEntityKind.Vendor,
            LocalEntityId = vendor.Id,
            ConfirmedAt = _now,
            CreatedAt = _now,
            UpdatedAt = _now,
        });
        db.DeviceTokens.Add(new DeviceToken
        {
            PortfolioId = _portfolioId,
            UserId = 701,
            Token = "accounting-atomic-device",
            Platform = "android",
            CreatedAt = _now,
            LastSeenAt = _now,
        });
        await db.SaveChangesAsync();
    }

    private async Task SeedParkedPaymentAndExpenseAsync(
        string customerExternalId,
        string paymentExternalId,
        string expenseExternalId)
    {
        await using var db = NewContext();
        db.AccountingSyncMaps.AddRange(
            Parked(
                ExternalKind.Payment,
                paymentExternalId,
                JsonSerializer.Serialize(new ExtPaymentDto(
                    paymentExternalId,
                    customerExternalId,
                    1500m,
                    _now,
                    "ACH",
                    $"ref-{paymentExternalId}",
                    _now,
                    null,
                    null,
                    null))),
            Parked(
                ExternalKind.Purchase,
                expenseExternalId,
                JsonSerializer.Serialize(new ExtExpenseDto(
                    expenseExternalId,
                    "vendor-main",
                    null,
                    null,
                    125m,
                    _now,
                    $"ref-{expenseExternalId}",
                    _now,
                    ExternalKind.Purchase,
                    null))));
        await db.SaveChangesAsync();
    }

    private async Task SeedParkedPaymentsAsync(string customerExternalId, int count)
    {
        await using var db = NewContext();
        db.AccountingSyncMaps.AddRange(Enumerable.Range(1, count).Select(index =>
            Parked(
                ExternalKind.Payment,
                $"payment-large-{index:D4}",
                JsonSerializer.Serialize(new ExtPaymentDto(
                    $"payment-large-{index:D4}",
                    customerExternalId,
                    10m,
                    _now.AddSeconds(index),
                    "ACH",
                    $"large-ref-{index:D4}",
                    _now,
                    null,
                    null,
                    null)))));
        await db.SaveChangesAsync();
    }

    private AccountingSyncMap Parked(string externalType, string externalId, string payload) => new()
    {
        PortfolioId = _portfolioId,
        AccountingConnectionId = _connectionId,
        Direction = LedgerDirection.Import,
        ExternalType = externalType,
        ExternalId = externalId,
        Status = LedgerStatus.Unmatched,
        MetadataJson = payload,
        AttemptCount = 1,
        CreatedAt = _now,
        UpdatedAt = _now,
    };

    private Property Property(int portfolioId, string name) => new()
    {
        PortfolioId = portfolioId,
        Name = name,
        AddressLine1 = "1 Test St",
        City = "Columbus",
        State = "OH",
        PostalCode = "43215",
        CreatedAt = _now,
        UpdatedAt = _now,
    };

    private Tenant Tenant(int portfolioId, string firstName) => new()
    {
        PortfolioId = portfolioId,
        FirstName = firstName,
        LastName = "Tenant",
        CreatedAt = _now,
        UpdatedAt = _now,
    };

    private RentalCommandDbContext NewContext()
    {
        var options = new DbContextOptionsBuilder<RentalCommandDbContext>()
            .UseNpgsql(_postgres!.GetConnectionString())
            .Options;
        return new RentalCommandDbContext(options);
    }

    private void SkipIfNoDocker() =>
        Skip.IfNot(_dockerAvailable, "Docker is required for PostgreSQL accounting atomic tests.");

    private sealed class TestActor : ICurrentActor
    {
        public int? UserId => 701;
        public string? ActorLabel => "integration:accounting-mapping";
        public string? IpAddress => "127.0.0.1";
    }

    private sealed class CommandRecorder : DbCommandInterceptor
    {
        private readonly ConcurrentQueue<string> _commands = new();
        public IReadOnlyCollection<string> Commands => _commands.ToArray();
        public void Clear()
        {
            while (_commands.TryDequeue(out _))
            {
            }
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

    private sealed class OutboxFailureInterceptor : DbCommandInterceptor
    {
        public bool FailOutboxInsert { get; set; }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            if (FailOutboxInsert
                && command.CommandText.Contains("INSERT INTO \"OutboxMessages\"", StringComparison.Ordinal))
            {
                throw new InjectedOutboxFailure();
            }
            return ValueTask.FromResult(result);
        }
    }

    private sealed class InjectedOutboxFailure : Exception
    {
    }
}
