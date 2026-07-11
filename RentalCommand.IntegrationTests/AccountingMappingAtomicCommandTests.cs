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
        new("accounting.mapping.confirm.result.v1");
    private readonly DateTime _now = new(2026, 7, 11, 17, 0, 0, DateTimeKind.Utc);
    private PostgreSqlContainer? _postgres;
    private ServiceProvider? _services;
    private bool _dockerAvailable;
    private int _portfolioId;
    private int _otherPortfolioId;
    private int _connectionId;
    private int _tenantId;
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
        promotionReads.Should().HaveCountGreaterThanOrEqualTo(4,
            "each payment/expense lane uses bounded SQL batches and a bounded empty recovery probe");
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
        await act.Should().ThrowAsync<InjectedOutboxFailure>();
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
            Command("customer-correction", _tenantId, "corrected"),
            Codec);
        var replay = await Atomic.ExecuteAsync(
            secondIdentity,
            Command("customer-correction", _tenantId, "corrected"),
            Codec);

        first.Value.PromotedCount.Should().Be(2);
        corrected.Value.PromotedCount.Should().Be(0);
        replay.Disposition.Should().Be(AtomicCommandDisposition.Replayed);
        replay.Value.Should().BeEquivalentTo(corrected.Value);
        await using var db = NewContext();
        (await db.Payments.CountAsync(payment => payment.PortfolioId == _portfolioId)).Should().Be(1);
        (await db.Expenses.CountAsync(expense => expense.PortfolioId == _portfolioId)).Should().Be(1);
        (await db.AccountingSyncMaps.CountAsync(ledger =>
            ledger.AccountingConnectionId == _connectionId
            && ledger.Status == LedgerStatus.Imported)).Should().Be(2);
    }

    private IAtomicUnitOfWork Atomic => _services!.GetRequiredService<IAtomicUnitOfWork>();
    private CommandRecorder Recorder => _services!.GetRequiredService<CommandRecorder>();
    private OutboxFailureInterceptor Failure => _services!.GetRequiredService<OutboxFailureInterceptor>();

    private AtomicCommandIdentity Identity(string externalId, int tenantId, string suffix = "canonical") =>
        new("accounting.mapping.confirm", $"{_portfolioId}:{_connectionId}:{externalId}:{tenantId}:{suffix}");

    private ConfirmAccountingMappingCommand Command(
        string externalId,
        int tenantId,
        string requestIdentity = "canonical") => new(
            _portfolioId,
            _connectionId,
            AccountingProvider.QuickBooks,
            701,
            ExternalKind.Customer,
            externalId,
            "Mapped tenant",
            LocalEntityKind.Tenant,
            tenantId,
            null,
            requestIdentity,
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
        var otherTenant = Tenant(_otherPortfolioId, "Other");
        var vendor = new Vendor
        {
            PortfolioId = _portfolioId,
            Name = "Mapped vendor",
            CreatedAt = _now,
            UpdatedAt = _now,
        };
        db.AddRange(property, otherProperty, tenant, otherTenant, vendor);
        await db.SaveChangesAsync();
        _tenantId = tenant.Id;
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
