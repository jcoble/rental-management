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
    private static readonly AtomicJsonResultCodec<ReconcileClaimedProviderPaymentEventResult> ReconcileCodec =
        new("reconcile-claimed-provider-payment-event-result.v1");

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
        services.AddAtomicCommandHandler<ReconcileClaimedProviderPaymentEventCommand, ReconcileClaimedProviderPaymentEventResult, ReconcileClaimedProviderPaymentEventHandler>();
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
        inbox.EventKind.Should().Be(ProviderPaymentEventKind.Succeeded);
        inbox.Amount.Should().Be(1000m);
        inbox.Currency.Should().Be("usd");
    }

    [SkippableFact]
    public async Task SimultaneousClaims_AssignEligibleRowOnce()
    {
        SkipIfNoDocker();
        await SeedInboxAsync("evt_claim_once", "pi_claim_once");
        await using var firstDb = NewContext();
        await using var secondDb = NewContext();
        var firstStore = new ProviderInboxClaimStore(firstDb);
        var secondStore = new ProviderInboxClaimStore(secondDb);

        var claims = await Task.WhenAll(
            firstStore.ClaimAsync("worker-a", _now, TimeSpan.FromMinutes(1), 1),
            secondStore.ClaimAsync("worker-b", _now, TimeSpan.FromMinutes(1), 1));

        claims.Sum(batch => batch.Count).Should().Be(1);
        claims.SelectMany(batch => batch).Select(claim => claim.Id).Should().OnlyHaveUniqueItems();
    }

    [SkippableFact]
    public async Task ActiveLease_CannotBeStolen_ButExpiredLeaseIsReclaimed()
    {
        SkipIfNoDocker();
        await SeedInboxAsync("evt_lease", "pi_lease");
        ProviderInboxClaim first;
        await using (var db = NewContext())
        {
            first = (await new ProviderInboxClaimStore(db)
                .ClaimAsync("worker-a", _now, TimeSpan.FromMinutes(1), 1)).Single();
        }

        await using (var db = NewContext())
        {
            (await new ProviderInboxClaimStore(db)
                    .ClaimAsync("worker-b", _now.AddSeconds(59), TimeSpan.FromMinutes(1), 1))
                .Should().BeEmpty();
        }

        await using (var db = NewContext())
        {
            var reclaimed = (await new ProviderInboxClaimStore(db)
                .ClaimAsync("worker-b", _now.AddMinutes(1), TimeSpan.FromMinutes(1), 1)).Single();
            reclaimed.ClaimToken.Should().NotBe(first.ClaimToken);
            reclaimed.AttemptCount.Should().Be(2);
        }
    }

    [SkippableFact]
    public async Task ExpiredClaim_RejectsStaleCompletionToken()
    {
        SkipIfNoDocker();
        await SeedInboxAsync("evt_stale", "pi_stale");
        ProviderInboxClaim stale;
        ProviderInboxClaim current;
        await using (var db = NewContext())
        {
            stale = (await new ProviderInboxClaimStore(db)
                .ClaimAsync("worker-a", _now, TimeSpan.FromMinutes(1), 1)).Single();
        }
        await using (var db = NewContext())
        {
            current = (await new ProviderInboxClaimStore(db)
                .ClaimAsync("worker-b", _now.AddMinutes(1), TimeSpan.FromMinutes(1), 1)).Single();
        }

        var act = () => Atomic.ExecuteAsync(
            ReconcileIdentity(stale),
            new ReconcileClaimedProviderPaymentEventCommand(stale.Id, stale.ClaimToken, _now.AddMinutes(1)),
            ReconcileCodec);

        await act.Should().ThrowAsync<AtomicReceiptInvariantException>();
        await using var verify = NewContext();
        (await verify.ProviderInboxEvents.SingleAsync()).ClaimToken.Should().Be(current.ClaimToken);
    }

    [SkippableFact]
    public async Task WebhookBeforeFinalize_EventuallyReconcilesFromNormalizedFacts()
    {
        SkipIfNoDocker();
        await Atomic.ExecuteAsync(
            new AtomicCommandIdentity("payments.provider-event", "stripe:evt_race"),
            Event("evt_race", "pi_race", ProviderPaymentEventKind.Succeeded),
            EventCodec);
        ProviderInboxClaim claim;
        await using (var db = NewContext())
        {
            claim = (await new ProviderInboxClaimStore(db)
                .ClaimAsync("engine", _now, TimeSpan.FromMinutes(1), 1)).Single();
        }
        await SeedProviderAttemptAsync("pi_race");

        var outcome = await Atomic.ExecuteAsync(
            ReconcileIdentity(claim),
            new ReconcileClaimedProviderPaymentEventCommand(claim.Id, claim.ClaimToken, _now.AddSeconds(1)),
            ReconcileCodec);

        outcome.Value.Outcome.Should().Be(ReconcileProviderPaymentEventOutcome.Applied);
        await using var verify = NewContext();
        (await verify.ProviderInboxEvents.SingleAsync()).ProcessedAtUtc.Should().NotBeNull();
        (await verify.PaymentTransactions.SingleAsync()).Status.Should().Be(PaymentTransactionStatus.Succeeded);
        (await verify.Payments.SingleAsync(payment => payment.Id == _paymentId)).Status.Should().Be(PaymentStatus.Paid);
    }

    [SkippableFact]
    public async Task ClaimedEvent_ReplayIsDuplicateSafe()
    {
        SkipIfNoDocker();
        await SeedProviderAttemptAsync("pi_replay");
        await SeedInboxAsync("evt_replay", "pi_replay");
        ProviderInboxClaim claim;
        await using (var db = NewContext())
        {
            claim = (await new ProviderInboxClaimStore(db)
                .ClaimAsync("engine", _now, TimeSpan.FromMinutes(1), 1)).Single();
        }
        var command = new ReconcileClaimedProviderPaymentEventCommand(claim.Id, claim.ClaimToken, _now);

        var first = await Atomic.ExecuteAsync(ReconcileIdentity(claim), command, ReconcileCodec);
        var replay = await Atomic.ExecuteAsync(ReconcileIdentity(claim), command, ReconcileCodec);

        first.Disposition.Should().Be(AtomicCommandDisposition.Executed);
        replay.Disposition.Should().Be(AtomicCommandDisposition.Replayed);
        await using var verify = NewContext();
        (await verify.ProviderInboxEvents.CountAsync()).Should().Be(1);
        (await verify.AtomicCommandReceipts.CountAsync(receipt =>
            receipt.CommandType == "payments.provider-inbox.reconcile")).Should().Be(1);
    }

    [SkippableFact]
    public async Task UnmatchedEvent_DeadLettersAtDeliberateMaximumAttempt()
    {
        SkipIfNoDocker();
        await SeedInboxAsync("evt_dead_letter", "pi_never_finalized");
        await using (var db = NewContext())
        {
            var inbox = await db.ProviderInboxEvents.SingleAsync();
            inbox.AttemptCount = 7; // The claim below increments to the deliberate maximum of eight.
            await db.SaveChangesAsync();
        }
        ProviderInboxClaim claim;
        await using (var db = NewContext())
        {
            claim = (await new ProviderInboxClaimStore(db)
                .ClaimAsync("engine", _now, TimeSpan.FromMinutes(1), 1)).Single();
        }

        var outcome = await Atomic.ExecuteAsync(
            ReconcileIdentity(claim),
            new ReconcileClaimedProviderPaymentEventCommand(claim.Id, claim.ClaimToken, _now),
            ReconcileCodec);

        outcome.Value.Outcome.Should().Be(ReconcileProviderPaymentEventOutcome.DeadLettered);
        await using var verify = NewContext();
        var deadLetter = await verify.ProviderInboxEvents.SingleAsync();
        deadLetter.DeadLetteredAtUtc.Should().Be(_now);
        deadLetter.FailureKind.Should().Be(ProviderInboxFailureKind.Unmatched);
        deadLetter.ClaimToken.Should().BeNull();
    }

    [SkippableTheory]
    [InlineData(ProviderPaymentEventKind.Pending)]
    [InlineData(ProviderPaymentEventKind.Failed)]
    [InlineData(ProviderPaymentEventKind.Canceled)]
    public async Task StaleNonSuccessEvent_DoesNotDowngradeTerminalSuccess(ProviderPaymentEventKind staleKind)
    {
        SkipIfNoDocker();
        var providerObjectId = $"pi_terminal_{staleKind}";
        await SeedProviderAttemptAsync(providerObjectId);
        await using (var db = NewContext())
        {
            var transaction = await db.PaymentTransactions.SingleAsync();
            transaction.Status = PaymentTransactionStatus.Succeeded;
            var payment = await db.Payments.SingleAsync(candidate => candidate.Id == _paymentId);
            payment.Status = PaymentStatus.Paid;
            await db.SaveChangesAsync();
        }
        await SeedInboxAsync($"evt_terminal_{staleKind}", providerObjectId, staleKind);
        ProviderInboxClaim claim;
        await using (var db = NewContext())
        {
            claim = (await new ProviderInboxClaimStore(db)
                .ClaimAsync("engine", _now, TimeSpan.FromMinutes(1), 1)).Single();
        }

        await Atomic.ExecuteAsync(
            ReconcileIdentity(claim),
            new ReconcileClaimedProviderPaymentEventCommand(claim.Id, claim.ClaimToken, _now),
            ReconcileCodec);

        await using var verify = NewContext();
        (await verify.PaymentTransactions.SingleAsync()).Status.Should().Be(PaymentTransactionStatus.Succeeded);
        (await verify.Payments.SingleAsync(candidate => candidate.Id == _paymentId)).Status.Should().Be(PaymentStatus.Paid);
    }

    private IAtomicUnitOfWork Atomic => _services!.GetRequiredService<IAtomicUnitOfWork>();

    private static AtomicCommandIdentity ReconcileIdentity(ProviderInboxClaim claim) =>
        new("payments.provider-inbox.reconcile", $"{claim.Id}:{claim.ClaimToken:N}");

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

    private async Task SeedInboxAsync(
        string providerEventId,
        string providerObjectId,
        ProviderPaymentEventKind kind = ProviderPaymentEventKind.Succeeded)
    {
        await using var db = NewContext();
        db.ProviderInboxEvents.Add(new ProviderInboxEvent
        {
            Provider = "stripe",
            ProviderEventId = providerEventId,
            EventType = $"payment_intent.{kind.ToString().ToLowerInvariant()}",
            Payload = "{}",
            ProviderObjectId = providerObjectId,
            EventKind = kind,
            Amount = 1000m,
            Currency = "usd",
            OccurredAtUtc = _now,
            ReceivedAtUtc = _now,
            NextAttemptAtUtc = _now,
            FailureKind = ProviderInboxFailureKind.Unmatched,
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
