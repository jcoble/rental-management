using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using RentalCommand.Core.Atomic;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Interfaces;
using RentalCommand.Core.Payments;
using RentalCommand.Data;
using RentalCommand.Data.Atomic;
using RentalCommand.Data.Payments;
using Testcontainers.PostgreSql;
using Xunit;

namespace RentalCommand.IntegrationTests;

public sealed class ProviderPaymentAtomicCommandTests : IAsyncLifetime
{
    private static readonly AtomicJsonResultCodec<PrepareProviderPaymentCreateResult> PrepareCodec =
        new("prepare-provider-payment-create-result.v1");
    private static readonly AtomicJsonResultCodec<FinalizeProviderPaymentCreateResult> FinalizeCodec =
        new("finalize-provider-payment-create-result.v1");
    private static readonly AtomicJsonResultCodec<RecordVerifiedProviderPaymentEventResult> EventCodec =
        new("record-verified-provider-payment-event-result.v1");

    private PostgreSqlContainer? _postgres;
    private ServiceProvider? _services;
    private bool _dockerAvailable;
    private int _portfolioId;
    private int _paymentId;
    private readonly DateTime _now = new(2026, 7, 11, 6, 0, 0, DateTimeKind.Utc);

    public async Task InitializeAsync()
    {
        try
        {
            _postgres = new PostgreSqlBuilder()
                .WithImage("postgres:16-alpine")
                .WithDatabase("rentalcommand_provider_payments")
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

        await using (var db = NewContext())
        {
            await db.Database.EnsureCreatedAsync();
            await SeedAsync(db);
        }

        var services = new ServiceCollection();
        services.AddSingleton(TimeProvider.System);
        services.AddScoped<ICurrentActor, TestActor>();
        services.AddAtomicPersistenceKernel();
        services.AddAtomicCommandHandler<PrepareProviderPaymentCreateCommand, PrepareProviderPaymentCreateResult, PrepareProviderPaymentCreateHandler>();
        services.AddAtomicCommandHandler<FinalizeProviderPaymentCreateCommand, FinalizeProviderPaymentCreateResult, FinalizeProviderPaymentCreateHandler>();
        services.AddAtomicCommandHandler<RecordVerifiedProviderPaymentEventCommand, RecordVerifiedProviderPaymentEventResult, RecordVerifiedProviderPaymentEventHandler>();
        services.AddDbContext<RentalCommandDbContext>((provider, options) =>
            options.UseNpgsql(_postgres!.GetConnectionString()).UseAtomicPersistenceKernel(provider));
        _services = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true,
        });
    }

    public async Task DisposeAsync()
    {
        if (_services is not null) await _services.DisposeAsync();
        if (_postgres is not null) await _postgres.DisposeAsync();
    }

    [SkippableFact]
    public async Task PrepareAndFinalize_CreateOneDurableProviderAttempt()
    {
        SkipIfNoDocker();
        var key = $"stripe:checkout:{_paymentId}:2026-07";

        var prepared = await Atomic.ExecuteAsync(
            new AtomicCommandIdentity("payments.provider-create.prepare", key),
            new PrepareProviderPaymentCreateCommand(_portfolioId, _paymentId, null, "stripe", key, "usd", _now),
            PrepareCodec);
        var finalized = await Atomic.ExecuteAsync(
            new AtomicCommandIdentity("payments.provider-create.finalize", key),
            new FinalizeProviderPaymentCreateCommand(
                _portfolioId, _paymentId, prepared.Value.PaymentTransactionId, "stripe", key,
                "cs_test_123", PaymentTransactionStatus.Pending, null, _now),
            FinalizeCodec);

        finalized.Value.Outcome.Should().Be(FinalizeProviderPaymentCreateOutcome.Applied);
        await using var db = NewContext();
        var transaction = await db.PaymentTransactions.SingleAsync();
        transaction.IdempotencyKey.Should().Be(key);
        transaction.ProviderPaymentIntentId.Should().Be("cs_test_123");
        (await db.AtomicCommandReceipts.CountAsync()).Should().Be(2);
        (await db.AtomicAuditLogs.CountAsync()).Should().BeGreaterThanOrEqualTo(2);
    }

    [SkippableFact]
    public async Task VerifiedSuccess_IsAtomicAndDuplicateDeliveryReplaysReceipt()
    {
        SkipIfNoDocker();
        await SeedProviderAttemptAsync("pi_test_success");
        var identity = new AtomicCommandIdentity("payments.provider-event", "stripe:evt_success");
        var command = Event("evt_success", "pi_test_success", ProviderPaymentEventKind.Succeeded);

        var first = await Atomic.ExecuteAsync(identity, command, EventCodec);
        var replay = await Atomic.ExecuteAsync(identity, command, EventCodec);

        first.Disposition.Should().Be(AtomicCommandDisposition.Executed);
        replay.Disposition.Should().Be(AtomicCommandDisposition.Replayed);
        await using var db = NewContext();
        (await db.ProviderInboxEvents.CountAsync()).Should().Be(1);
        (await db.Payments.SingleAsync(p => p.Id == _paymentId)).Status.Should().Be(PaymentStatus.Paid);
        (await db.PaymentTransactions.SingleAsync()).Status.Should().Be(PaymentTransactionStatus.Succeeded);
    }

    [SkippableFact]
    public async Task VerifiedEventWithoutLocalAttempt_RemainsDurableForReconciliation()
    {
        SkipIfNoDocker();
        var result = await Atomic.ExecuteAsync(
            new AtomicCommandIdentity("payments.provider-event", "stripe:evt_unmatched"),
            Event("evt_unmatched", "pi_missing", ProviderPaymentEventKind.Succeeded),
            EventCodec);

        result.Value.Outcome.Should().Be(RecordProviderPaymentEventOutcome.Unmatched);
        await using var db = NewContext();
        var inbox = await db.ProviderInboxEvents.SingleAsync();
        inbox.ProcessedAtUtc.Should().BeNull();
        inbox.FailureKind.Should().Be(ProviderInboxFailureKind.Unmatched);
        inbox.ProviderObjectId.Should().Be("pi_missing");
    }

    private IAtomicUnitOfWork Atomic => _services!.GetRequiredService<IAtomicUnitOfWork>();

    private RecordVerifiedProviderPaymentEventCommand Event(
        string eventId,
        string providerObjectId,
        ProviderPaymentEventKind kind) =>
        new("stripe", eventId, "payment_intent.succeeded", "{}", providerObjectId, kind,
            1000m, "usd", null, _now, _now);

    private async Task SeedProviderAttemptAsync(string providerObjectId)
    {
        await using var db = NewContext();
        db.PaymentTransactions.Add(new PaymentTransaction
        {
            PortfolioId = _portfolioId,
            PaymentId = _paymentId,
            Amount = 1000m,
            Currency = "usd",
            Provider = "stripe",
            ProviderPaymentIntentId = providerObjectId,
            IdempotencyKey = $"seed:{providerObjectId}",
            Status = PaymentTransactionStatus.Pending,
            CreatedAt = _now,
            UpdatedAt = _now,
        });
        await db.SaveChangesAsync();
    }

    private async Task SeedAsync(RentalCommandDbContext db)
    {
        var portfolio = new Portfolio { Name = "Provider test", CreatedAt = _now, UpdatedAt = _now };
        db.Portfolios.Add(portfolio);
        await db.SaveChangesAsync();
        _portfolioId = portfolio.Id;

        var property = new Property
        {
            PortfolioId = _portfolioId, Name = "Test", AddressLine1 = "1 Test St",
            City = "Test", State = "OH", PostalCode = "00000", CreatedAt = _now, UpdatedAt = _now,
        };
        var tenant = new Tenant
        {
            PortfolioId = _portfolioId, FirstName = "Paying", LastName = "Tenant",
            CreatedAt = _now, UpdatedAt = _now,
        };
        db.AddRange(property, tenant);
        await db.SaveChangesAsync();
        var unit = new Unit { PropertyId = property.Id, UnitNumber = "1", CreatedAt = _now, UpdatedAt = _now };
        db.Units.Add(unit);
        await db.SaveChangesAsync();
        var lease = new Lease
        {
            PortfolioId = _portfolioId, PropertyId = property.Id, UnitId = unit.Id, TenantId = tenant.Id,
            LeaseNumber = "L-1", Status = LeaseStatus.Active, StartDate = _now.AddMonths(-1),
            EndDate = _now.AddYears(1), MonthlyRent = 1000m, CreatedAt = _now, UpdatedAt = _now,
        };
        db.Leases.Add(lease);
        await db.SaveChangesAsync();
        var payment = new Payment
        {
            PortfolioId = _portfolioId, LeaseId = lease.Id, PaymentType = PaymentType.Rent,
            Status = PaymentStatus.Scheduled, Amount = 1000m, DueDate = _now,
            PeriodKey = "2026-07", CreatedAt = _now, UpdatedAt = _now,
        };
        db.Payments.Add(payment);
        await db.SaveChangesAsync();
        _paymentId = payment.Id;
    }

    private RentalCommandDbContext NewContext()
    {
        var options = new DbContextOptionsBuilder<RentalCommandDbContext>()
            .UseNpgsql(_postgres!.GetConnectionString())
            .Options;
        return new RentalCommandDbContext(options);
    }

    private void SkipIfNoDocker() => Skip.IfNot(_dockerAvailable, "Docker is required for PostgreSQL atomic tests.");

    private sealed class TestActor : ICurrentActor
    {
        public int? UserId => null;
        public string? ActorLabel => "integration:provider-payments";
        public string? IpAddress => "127.0.0.1";
    }
}
