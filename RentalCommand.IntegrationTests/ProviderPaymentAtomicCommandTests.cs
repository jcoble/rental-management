using System.Data.Common;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
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
        services.AddSingleton<ProviderInboxFailureInterceptor>();
        services.AddScoped<ICurrentActor, TestActor>();
        services.AddAtomicPersistenceKernel();
        services.AddAtomicCommandHandler<PrepareProviderPaymentCreateCommand, PrepareProviderPaymentCreateResult, PrepareProviderPaymentCreateHandler>();
        services.AddAtomicCommandHandler<FinalizeProviderPaymentCreateCommand, FinalizeProviderPaymentCreateResult, FinalizeProviderPaymentCreateHandler>();
        services.AddAtomicCommandHandler<RecordVerifiedProviderPaymentEventCommand, RecordVerifiedProviderPaymentEventResult, RecordVerifiedProviderPaymentEventHandler>();
        services.AddAtomicCommandHandler<ReconcileClaimedProviderPaymentEventCommand, ReconcileClaimedProviderPaymentEventResult, ReconcileClaimedProviderPaymentEventHandler>();
        services.AddDbContext<RentalCommandDbContext>((provider, options) =>
            options.UseNpgsql(_postgres!.GetConnectionString())
                .UseAtomicPersistenceKernel(provider)
                .AddInterceptors(provider.GetRequiredService<ProviderInboxFailureInterceptor>()));
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
    public async Task ProviderObjectUniqueness_IsScopedByProviderAndRejectsSameProviderDuplicate()
    {
        SkipIfNoDocker();
        await using (var db = NewContext())
        {
            db.PaymentTransactions.AddRange(
                NewProviderAttempt("stripe", "shared-object", "stripe-shared"),
                NewProviderAttempt("paypal", "shared-object", "paypal-shared"));
            await db.SaveChangesAsync();
        }

        await using (var duplicate = NewContext())
        {
            duplicate.PaymentTransactions.Add(
                NewProviderAttempt("stripe", "shared-object", "stripe-duplicate"));
            var save = () => duplicate.SaveChangesAsync();
            await save.Should().ThrowAsync<DbUpdateException>();
        }

        await using var verify = NewContext();
        (await verify.PaymentTransactions.CountAsync()).Should().Be(2);
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
    public async Task SameProviderEventWithDifferentOperationKey_ReturnsCanonicalDuplicate()
    {
        SkipIfNoDocker();
        var command = Event("evt_different_keys", "pi_not_created", ProviderPaymentEventKind.Succeeded);

        var first = await Atomic.ExecuteAsync(
            new AtomicCommandIdentity("payments.provider-event", "stripe:delivery-a"),
            command,
            EventCodec);
        var duplicate = await Atomic.ExecuteAsync(
            new AtomicCommandIdentity("payments.provider-event", "stripe:delivery-b"),
            command,
            EventCodec);

        first.Value.Outcome.Should().Be(RecordProviderPaymentEventOutcome.Unmatched);
        duplicate.Value.Outcome.Should().Be(RecordProviderPaymentEventOutcome.Duplicate);
        duplicate.Value.ProviderInboxEventId.Should().Be(first.Value.ProviderInboxEventId);
        await using var db = NewContext();
        (await db.ProviderInboxEvents.CountAsync()).Should().Be(1);
        (await db.AtomicCommandReceipts.CountAsync(receipt =>
            receipt.CommandType == "payments.provider-event")).Should().Be(2);
    }

    [SkippableFact]
    public async Task PaymentEventWithoutProviderObject_DeadLettersAsPermanentAndIsNeverClaimed()
    {
        SkipIfNoDocker();

        var outcome = await Atomic.ExecuteAsync(
            new AtomicCommandIdentity("payments.provider-event", "stripe:evt_missing_object"),
            Event("evt_missing_object", "", ProviderPaymentEventKind.Succeeded),
            EventCodec);

        outcome.Value.Outcome.Should().Be(RecordProviderPaymentEventOutcome.Unmatched);
        await using var db = NewContext();
        var inbox = await db.ProviderInboxEvents.SingleAsync();
        inbox.FailureKind.Should().Be(ProviderInboxFailureKind.Permanent);
        inbox.DeadLetteredAtUtc.Should().Be(_now);
        (await new ProviderInboxClaimStore(db)
            .ClaimAsync("engine", TimeSpan.FromMinutes(1), 1)).Should().BeEmpty();
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
            firstStore.ClaimAsync("worker-a", TimeSpan.FromMinutes(1), 1),
            secondStore.ClaimAsync("worker-b", TimeSpan.FromMinutes(1), 1));

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
                .ClaimAsync("worker-a", TimeSpan.FromMinutes(1), 1)).Single();
        }

        await using (var db = NewContext())
        {
            (await new ProviderInboxClaimStore(db)
                    .ClaimAsync("worker-b", TimeSpan.FromMinutes(1), 1))
                .Should().BeEmpty();
        }

        await ExpireClaimAsync(first.Id);
        await using (var db = NewContext())
        {
            var reclaimed = (await new ProviderInboxClaimStore(db)
                .ClaimAsync("worker-b", TimeSpan.FromMinutes(1), 1)).Single();
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
                .ClaimAsync("worker-a", TimeSpan.FromMinutes(1), 1)).Single();
        }
        await ExpireClaimAsync(stale.Id);
        await using (var db = NewContext())
        {
            current = (await new ProviderInboxClaimStore(db)
                .ClaimAsync("worker-b", TimeSpan.FromMinutes(1), 1)).Single();
        }

        var act = () => Atomic.ExecuteAsync(
            ReconcileIdentity(stale),
            ReconcileCommand(stale, _now.AddMinutes(1)),
            ReconcileCodec);

        await act.Should().ThrowAsync<AtomicReceiptInvariantException>();
        await using var verify = NewContext();
        (await verify.ProviderInboxEvents.SingleAsync()).ClaimToken.Should().Be(current.ClaimToken);
    }

    [SkippableFact]
    public async Task DatabaseExpiredLease_RejectsCompletionEvenBeforeAnotherWorkerTakesOver()
    {
        SkipIfNoDocker();
        await SeedProviderAttemptAsync("pi_expired_without_takeover");
        await SeedInboxAsync("evt_expired_without_takeover", "pi_expired_without_takeover");
        ProviderInboxClaim claim;
        await using (var db = NewContext())
        {
            claim = (await new ProviderInboxClaimStore(db)
                .ClaimAsync("engine", TimeSpan.FromMinutes(1), 1)).Single();
        }
        await ExpireClaimAsync(claim.Id);

        var act = () => Atomic.ExecuteAsync(
            ReconcileIdentity(claim),
            ReconcileCommand(claim, _now),
            ReconcileCodec);

        await act.Should().ThrowAsync<AtomicReceiptInvariantException>();
        await using var verify = NewContext();
        var inbox = await verify.ProviderInboxEvents.SingleAsync();
        inbox.ClaimToken.Should().Be(claim.ClaimToken);
        inbox.ProcessedAtUtc.Should().BeNull();
        (await verify.PaymentTransactions.SingleAsync()).Status.Should().Be(PaymentTransactionStatus.Pending);
        (await verify.AtomicCommandReceipts.CountAsync(receipt =>
            receipt.CommandType == "payments.provider-inbox.reconcile")).Should().Be(0);
    }

    [SkippableFact]
    public async Task MatchingTokenWithWrongOwner_CannotCompleteClaim()
    {
        SkipIfNoDocker();
        await SeedProviderAttemptAsync("pi_wrong_owner");
        await SeedInboxAsync("evt_wrong_owner", "pi_wrong_owner");
        ProviderInboxClaim claim;
        await using (var db = NewContext())
        {
            claim = (await new ProviderInboxClaimStore(db)
                .ClaimAsync("worker-a", TimeSpan.FromMinutes(1), 1)).Single();
        }

        var identity = new AtomicCommandIdentity(
            "payments.provider-inbox.reconcile",
            $"{claim.Id}:wrong-owner:{claim.ClaimToken:N}");
        var act = () => Atomic.ExecuteAsync(
            identity,
            new ReconcileClaimedProviderPaymentEventCommand(
                claim.Id,
                "worker-b",
                claim.ClaimToken,
                _now),
            ReconcileCodec);

        await act.Should().ThrowAsync<AtomicReceiptInvariantException>();
        await using var verify = NewContext();
        var inbox = await verify.ProviderInboxEvents.SingleAsync();
        inbox.ClaimOwner.Should().Be("worker-a");
        inbox.ClaimToken.Should().Be(claim.ClaimToken);
        inbox.ProcessedAtUtc.Should().BeNull();
        (await verify.PaymentTransactions.SingleAsync()).Status.Should().Be(PaymentTransactionStatus.Pending);
        (await verify.AtomicCommandReceipts.CountAsync(receipt =>
            receipt.CommandType == identity.CommandType &&
            receipt.IdempotencyKey == identity.IdempotencyKey)).Should().Be(0);
    }

    [SkippableFact]
    public async Task ClaimStatement_SelectsOnlyBoundedEarliestDueEvents()
    {
        SkipIfNoDocker();
        await SeedInboxAsync("evt_due_first", "pi_due_first");
        await SeedInboxAsync("evt_due_second", "pi_due_second");
        await SeedInboxAsync("evt_due_third", "pi_due_third");
        await SeedInboxAsync("evt_future", "pi_future");
        await using (var db = NewContext())
        {
            await db.Database.ExecuteSqlRawAsync("""
                UPDATE "ProviderInboxEvents"
                SET "NextAttemptAtUtc" = clock_timestamp() + CASE "ProviderEventId"
                    WHEN 'evt_due_first' THEN interval '-4 minutes'
                    WHEN 'evt_due_second' THEN interval '-3 minutes'
                    WHEN 'evt_due_third' THEN interval '-2 minutes'
                    WHEN 'evt_future' THEN interval '1 hour'
                    ELSE interval '0 seconds'
                END
                """);
        }

        IReadOnlyList<ProviderInboxClaim> claims;
        await using (var db = NewContext())
        {
            claims = await new ProviderInboxClaimStore(db)
                .ClaimAsync("engine", TimeSpan.FromMinutes(1), 2);
        }

        claims.Should().HaveCount(2);
        claims.Select(claim => claim.ClaimOwner).Should().OnlyContain(owner => owner == "engine");
        claims.Select(claim => claim.ClaimToken).Should().OnlyHaveUniqueItems();
        await using var expectedDb = NewContext();
        var expectedClaimIds = await expectedDb.ProviderInboxEvents
            .Where(row => row.ProviderEventId == "evt_due_first" || row.ProviderEventId == "evt_due_second")
            .OrderBy(row => row.NextAttemptAtUtc)
            .Select(row => row.Id)
            .ToListAsync();
        claims.Select(claim => claim.Id).Should().Equal(expectedClaimIds);
        await using var verify = NewContext();
        var claimedEventIds = await verify.ProviderInboxEvents
            .Where(row => row.ClaimToken != null)
            .OrderBy(row => row.NextAttemptAtUtc)
            .Select(row => row.ProviderEventId)
            .ToListAsync();
        claimedEventIds.Should().Equal("evt_due_first", "evt_due_second");
        (await verify.ProviderInboxEvents.SingleAsync(row => row.ProviderEventId == "evt_due_third"))
            .ClaimToken.Should().BeNull();
        (await verify.ProviderInboxEvents.SingleAsync(row => row.ProviderEventId == "evt_future"))
            .ClaimToken.Should().BeNull();
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
                .ClaimAsync("engine", TimeSpan.FromMinutes(1), 1)).Single();
        }
        await SeedProviderAttemptAsync("pi_race");

        var outcome = await Atomic.ExecuteAsync(
            ReconcileIdentity(claim),
            ReconcileCommand(claim, _now.AddSeconds(1)),
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
                .ClaimAsync("engine", TimeSpan.FromMinutes(1), 1)).Single();
        }
        var command = ReconcileCommand(claim, _now);

        var first = await Atomic.ExecuteAsync(ReconcileIdentity(claim), command, ReconcileCodec);
        var replay = await Atomic.ExecuteAsync(ReconcileIdentity(claim), command, ReconcileCodec);

        first.Disposition.Should().Be(AtomicCommandDisposition.Executed);
        replay.Disposition.Should().Be(AtomicCommandDisposition.Replayed);
        await using var verify = NewContext();
        (await verify.ProviderInboxEvents.CountAsync()).Should().Be(1);
        (await verify.AtomicCommandReceipts.CountAsync(receipt =>
            receipt.CommandType == "payments.provider-inbox.reconcile")).Should().Be(1);
    }

    [SkippableTheory]
    [InlineData(ProviderInboxFailureBoundary.Business)]
    [InlineData(ProviderInboxFailureBoundary.Audit)]
    [InlineData(ProviderInboxFailureBoundary.Receipt)]
    public async Task InjectedAtomicBoundaryFailure_RollsBackClaimDispositionAndFinancialRows_ThenRecovers(
        ProviderInboxFailureBoundary boundary)
    {
        SkipIfNoDocker();
        var providerObjectId = $"pi_rollback_{boundary}";
        await SeedProviderAttemptAsync(providerObjectId);
        await SeedInboxAsync($"evt_rollback_{boundary}", providerObjectId);
        ProviderInboxClaim claim;
        await using (var db = NewContext())
        {
            claim = (await new ProviderInboxClaimStore(db)
                .ClaimAsync("engine", TimeSpan.FromMinutes(5), 1)).Single();
        }
        var identity = ReconcileIdentity(claim);
        var command = ReconcileCommand(claim, _now);
        Failure.Arm(boundary);

        var act = () => Atomic.ExecuteAsync(identity, command, ReconcileCodec);
        var failure = await act.Should().ThrowAsync<DbUpdateException>();
        failure.Which.InnerException.Should().BeOfType<InjectedProviderInboxFailure>();

        await using (var failed = NewContext())
        {
            var inbox = await failed.ProviderInboxEvents.SingleAsync();
            inbox.ClaimToken.Should().Be(claim.ClaimToken);
            inbox.ProcessedAtUtc.Should().BeNull();
            (await failed.PaymentTransactions.SingleAsync()).Status.Should().Be(PaymentTransactionStatus.Pending);
            (await failed.Payments.SingleAsync(payment => payment.Id == _paymentId)).Status
                .Should().Be(PaymentStatus.Scheduled);
            (await failed.AtomicCommandReceipts.CountAsync(receipt =>
                receipt.CommandType == identity.CommandType &&
                receipt.IdempotencyKey == identity.IdempotencyKey)).Should().Be(0);
            (await failed.AtomicAuditLogs.CountAsync(log =>
                log.CommandType == identity.CommandType &&
                log.CommandIdempotencyKey == identity.IdempotencyKey)).Should().Be(0);
        }

        var recovered = await Atomic.ExecuteAsync(identity, command, ReconcileCodec);
        recovered.Disposition.Should().Be(AtomicCommandDisposition.Executed);
        recovered.Value.Outcome.Should().Be(ReconcileProviderPaymentEventOutcome.Applied);
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
                .ClaimAsync("engine", TimeSpan.FromMinutes(1), 1)).Single();
        }

        var outcome = await Atomic.ExecuteAsync(
            ReconcileIdentity(claim),
            ReconcileCommand(claim, _now),
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
                .ClaimAsync("engine", TimeSpan.FromMinutes(1), 1)).Single();
        }

        await Atomic.ExecuteAsync(
            ReconcileIdentity(claim),
            ReconcileCommand(claim, _now),
            ReconcileCodec);

        await using var verify = NewContext();
        (await verify.PaymentTransactions.SingleAsync()).Status.Should().Be(PaymentTransactionStatus.Succeeded);
        (await verify.Payments.SingleAsync(candidate => candidate.Id == _paymentId)).Status.Should().Be(PaymentStatus.Paid);
    }

    private IAtomicUnitOfWork Atomic => _services!.GetRequiredService<IAtomicUnitOfWork>();
    private ProviderInboxFailureInterceptor Failure =>
        _services!.GetRequiredService<ProviderInboxFailureInterceptor>();

    private static AtomicCommandIdentity ReconcileIdentity(ProviderInboxClaim claim) =>
        new("payments.provider-inbox.reconcile", $"{claim.Id}:{claim.ClaimToken:N}");

    private static ReconcileClaimedProviderPaymentEventCommand ReconcileCommand(
        ProviderInboxClaim claim,
        DateTime reconciledAtUtc) =>
        new(claim.Id, claim.ClaimOwner, claim.ClaimToken, reconciledAtUtc);

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

    private PaymentTransaction NewProviderAttempt(
        string provider,
        string providerObjectId,
        string idempotencyKey) => new()
    {
        PortfolioId = _portfolioId,
        PaymentId = _paymentId,
        Amount = 1000m,
        Currency = "usd",
        Provider = provider,
        ProviderPaymentIntentId = providerObjectId,
        IdempotencyKey = idempotencyKey,
        Status = PaymentTransactionStatus.Pending,
        CreatedAt = _now,
        UpdatedAt = _now,
    };

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

    private async Task ExpireClaimAsync(long providerInboxEventId)
    {
        await using var db = NewContext();
        await db.Database.ExecuteSqlInterpolatedAsync($$"""
            UPDATE "ProviderInboxEvents"
            SET "ClaimExpiresAtUtc" = clock_timestamp() - interval '1 second'
            WHERE "Id" = {{providerInboxEventId}}
            """);
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

    public enum ProviderInboxFailureBoundary
    {
        Business,
        Audit,
        Receipt,
    }

    private sealed class ProviderInboxFailureInterceptor : DbCommandInterceptor
    {
        private ProviderInboxFailureBoundary? _armed;

        public void Arm(ProviderInboxFailureBoundary boundary) => _armed = boundary;

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            ThrowIfArmed(command);
            return ValueTask.FromResult(result);
        }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            ThrowIfArmed(command);
            return ValueTask.FromResult(result);
        }

        private void ThrowIfArmed(DbCommand command)
        {
            var boundary = _armed;
            var matches = boundary switch
            {
                ProviderInboxFailureBoundary.Business =>
                    command.CommandText.Contains("UPDATE \"PaymentTransactions\"", StringComparison.Ordinal),
                ProviderInboxFailureBoundary.Audit =>
                    command.CommandText.Contains("INSERT INTO \"AtomicAuditLogs\"", StringComparison.Ordinal),
                ProviderInboxFailureBoundary.Receipt =>
                    command.CommandText.Contains("UPDATE \"AtomicCommandReceipts\"", StringComparison.Ordinal),
                _ => false,
            };
            if (!matches) return;

            _armed = null;
            throw new InjectedProviderInboxFailure();
        }
    }

    private sealed class InjectedProviderInboxFailure : Exception;
}
