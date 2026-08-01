using System.Data.Common;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using RentalCommand.Api.Services.Domain;
using RentalCommand.Api.Services.Payments;
using RentalCommand.Core.Atomic;
using RentalCommand.Core.Authorization;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Interfaces;
using RentalCommand.Core.Outbox;
using RentalCommand.Core.Payments;
using RentalCommand.Data;
using RentalCommand.Data.Accounting;
using RentalCommand.Data.Atomic;
using RentalCommand.Data.Payments;
using RentalCommand.Engine.Services;
using RentalCommand.TestCommon;
using Stripe.Checkout;
using Testcontainers.PostgreSql;
using Xunit;

namespace RentalCommand.IntegrationTests;

/// <summary>PostgreSQL proof for canonical autopay selection and provider-event idempotency.</summary>
public sealed class ProviderPaymentAtomicCommandTests : IAsyncLifetime
{
    private static readonly AtomicJsonResultCodec<RecordVerifiedProviderPaymentEventResult> EventCodec =
        new("record-verified-provider-payment-event-result.v1");
    private static readonly AtomicJsonResultCodec<ReconcileClaimedProviderPaymentEventResult> ReconcileCodec =
        new("reconcile-claimed-provider-payment-event-result.v1");
    private static readonly AtomicJsonResultCodec<PrepareProviderPaymentCreateResult> PrepareCodec =
        new("prepare-provider-payment-create-result.v1");
    private static readonly AtomicJsonResultCodec<FinalizeProviderPaymentCreateResult> FinalizeCodec =
        new("finalize-provider-payment-create-result.v1");

    private PostgreSqlContainer? _postgres;
    private ServiceProvider? _services;
    private bool _dockerAvailable;

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

        var services = new ServiceCollection();
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<ProviderPaymentFailureInterceptor>();
        services.AddScoped<ICurrentActor, TestActor>();
        services.AddAtomicPersistenceKernel();
        services.AddAtomicCommandHandler<
            RecordVerifiedProviderPaymentEventCommand,
            RecordVerifiedProviderPaymentEventResult,
            RecordVerifiedProviderPaymentEventHandler>();
        services.AddAtomicCommandHandler<
            ReconcileClaimedProviderPaymentEventCommand,
            ReconcileClaimedProviderPaymentEventResult,
            ReconcileClaimedProviderPaymentEventHandler>();
        services.AddAtomicCommandHandler<
            CancelTenantAutopayCommand,
            CancelTenantAutopayResult,
            CancelTenantAutopayHandler>();
        services.AddAtomicCommandHandler<
            PrepareProviderPaymentCreateCommand,
            PrepareProviderPaymentCreateResult,
            PrepareProviderPaymentCreateHandler>();
        services.AddAtomicCommandHandler<
            FinalizeProviderPaymentCreateCommand,
            FinalizeProviderPaymentCreateResult,
            FinalizeProviderPaymentCreateHandler>();
        services.AddDbContext<RentalCommandDbContext>((provider, options) =>
            options.UseNpgsql(_postgres!.GetConnectionString())
                .UseAtomicPersistenceKernel(provider)
                .AddInterceptors(provider.GetRequiredService<ProviderPaymentFailureInterceptor>()));
        _services = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true,
        });

        await using var db = NewContext();
        await CreatePhysicalTestSchemaAsync(db);
    }

    private static async Task CreatePhysicalTestSchemaAsync(RentalCommandDbContext db)
    {
        // The foundation migration chain is intentionally temporary and will disappear at the
        // final baseline squash. Build the EF-owned tables directly, then install only the
        // canonical SQL objects exercised by provider receipt allocation and autopay selection.
        await db.Database.EnsureCreatedAsync();
        await db.Database.ExecuteSqlRawAsync(LeaseEffectiveClockSql.CreateEffectiveNowUtc);
        await db.Database.ExecuteSqlRawAsync(LeaseEffectiveClockSql.CreateBusinessDate);
        await db.Database.ExecuteSqlRawAsync(TenantChargeBalanceViewSql.Create);
        foreach (var statement in TenantAccountPostgreSqlContract.CreateStatements)
            await db.Database.ExecuteSqlRawAsync(statement);
    }

    public async Task DisposeAsync()
    {
        if (_services is not null) await _services.DisposeAsync();
        if (_postgres is not null) await _postgres.DisposeAsync();
    }

    [SkippableFact]
    public async Task ProviderReceipt_PersistsImmutableIntent_SettlesOnlyExactTarget_ThenLeavesSurplus()
    {
        SkipIfNoDocker();
        var scenario = await SeedScenarioAsync("exact-target");
        long targetChargeId;
        await using (var db = NewContext())
        {
            var targetCharge = Charge(scenario, "exact-target", 60m);
            db.TenantLedgerEntries.Add(targetCharge);
            await db.SaveChangesAsync();
            targetChargeId = targetCharge.Id;
        }

        const string key = "checkout:tenant-charge:exact-target";
        var prepareIdentity = new AtomicCommandIdentity("payments.provider-create.prepare", key);
        var prepareCommand = new PrepareProviderPaymentCreateCommand(
            scenario.PortfolioId, scenario.AccountId, targetChargeId, scenario.UserId,
            scenario.TenantId, null, "stripe", key, "USD", DateTime.UtcNow);
        var prepared = await ExecuteAtomicAsync(prepareIdentity, prepareCommand, PrepareCodec);
        prepared.Value.Outcome.Should().Be(PrepareProviderPaymentCreateOutcome.Prepared);
        prepared.Value.Amount.Should().Be(60m);

        long advanceReceiptId;
        await using (var db = NewContext())
        {
            var persistedAttempt = await db.TenantPaymentAttempts.SingleAsync(
                row => row.Id == prepared.Value.PaymentAttemptId);
            persistedAttempt.ChargeLedgerEntryId.Should().Be(targetChargeId);
            persistedAttempt.ChargeLedgerEntryId = scenario.ChargeId;
            await FluentActions.Awaiting(() => db.SaveChangesAsync())
                .Should().ThrowAsync<DbUpdateException>(
                    "the selected charge is immutable after the provider intent is prepared");
        }
        await using (var db = NewContext())
        {
            var advanceReceipt = new TenantLedgerEntry
            {
                PortfolioId = scenario.PortfolioId,
                TenantAccountId = scenario.AccountId,
                EntryType = TenantLedgerEntryType.PaymentReceipt,
                Direction = TenantLedgerDirection.Credit,
                Amount = 20m,
                Currency = "USD",
                EffectiveOn = DateOnly.FromDateTime(DateTime.UtcNow),
                PostedAtUtc = DateTime.UtcNow,
                Description = "Concurrent exact-target receipt",
                BusinessKey = "receipt:exact-target:advance",
                CreatedByUserId = scenario.UserId,
            };
            db.TenantLedgerEntries.Add(advanceReceipt);
            await db.SaveChangesAsync();
            advanceReceiptId = advanceReceipt.Id;
            db.TenantLedgerAllocations.Add(new TenantLedgerAllocation
            {
                PortfolioId = scenario.PortfolioId,
                TenantAccountId = scenario.AccountId,
                DebitEntryId = targetChargeId,
                CreditEntryId = advanceReceiptId,
                Amount = 20m,
                AllocatedAtUtc = DateTime.UtcNow,
                BusinessKey = "allocation:exact-target:advance",
                CreatedByUserId = scenario.UserId,
            });
            await db.SaveChangesAsync();
        }

        var finalizeIdentity = new AtomicCommandIdentity("payments.provider-create.finalize", key);
        var finalizeCommand = new FinalizeProviderPaymentCreateCommand(
            scenario.PortfolioId, scenario.AccountId, prepared.Value.PaymentAttemptId,
            "stripe", key, "pi_exact_target", TenantPaymentAttemptState.Succeeded,
            null, DateTime.UtcNow);
        var finalized = await ExecuteAtomicAsync(finalizeIdentity, finalizeCommand, FinalizeCodec);
        var replay = await ExecuteAtomicAsync(finalizeIdentity, finalizeCommand, FinalizeCodec);

        finalized.Value.Outcome.Should().Be(FinalizeProviderPaymentCreateOutcome.Applied);
        replay.Disposition.Should().Be(AtomicCommandDisposition.Replayed);
        replay.Value.Should().Be(finalized.Value);

        await using var verify = NewContext();
        var providerReceipt = await verify.TenantLedgerEntries.SingleAsync(row =>
            row.ProviderPaymentAttemptId == prepared.Value.PaymentAttemptId);
        var providerAllocation = await verify.TenantLedgerAllocations.SingleAsync(row =>
            row.CreditEntryId == providerReceipt.Id);
        providerAllocation.DebitEntryId.Should().Be(targetChargeId);
        providerAllocation.Amount.Should().Be(40m);
        (await verify.TenantLedgerAllocations.AnyAsync(row =>
            row.CreditEntryId == providerReceipt.Id && row.DebitEntryId == scenario.ChargeId))
            .Should().BeFalse("provider receipt allocation has no oldest-charge fallback");
        (await verify.TenantLedgerAllocations
            .Where(row => row.DebitEntryId == targetChargeId)
            .SumAsync(row => row.Amount)).Should().Be(60m);
        (providerReceipt.Amount - providerAllocation.Amount).Should().Be(20m,
            "surplus remains unapplied only after the selected charge is settled");
        (await verify.AtomicCommandReceipts.CountAsync(row =>
            row.CommandType == finalizeIdentity.CommandType
            && row.IdempotencyKey == finalizeIdentity.IdempotencyKey)).Should().Be(1);
        (await verify.OutboxMessages.CountAsync(row =>
            row.IdempotencyKey == OutboxIdempotency.Create(
                "provider-receipt", prepared.Value.PaymentAttemptId.ToString()))).Should().Be(1);
    }

    [SkippableFact]
    public async Task ProviderReceipt_FinalCompanionFailure_RollsBackExactAllocationLedgerAuditOutboxAndReceipt()
    {
        SkipIfNoDocker();
        var scenario = await SeedScenarioAsync("exact-target-rollback");
        const string providerObjectId = "pi_exact_target_rollback";
        long attemptId;
        await using (var db = NewContext())
        {
            var paymentAttempt = Attempt(scenario, "provider-exact-target-rollback",
                providerObjectId, TenantPaymentAttemptState.Submitted);
            db.TenantPaymentAttempts.Add(paymentAttempt);
            await db.SaveChangesAsync();
            attemptId = paymentAttempt.Id;
        }

        var identity = new AtomicCommandIdentity(
            "payments.provider-event.record", "stripe:evt_exact_target_rollback");
        var command = new RecordVerifiedProviderPaymentEventCommand(
            "stripe", "evt_exact_target_rollback", "payment_intent.succeeded", "{}",
            providerObjectId, ProviderPaymentEventKind.Succeeded, 100m, "USD", null,
            DateTime.UtcNow, DateTime.UtcNow);
        Failure.FailAtomicAudit = true;
        var failure = await FluentActions
            .Awaiting(() => ExecuteAtomicAsync(identity, command, EventCodec))
            .Should().ThrowAsync<DbUpdateException>();
        failure.Which.InnerException.Should().BeOfType<InvalidOperationException>();
        Failure.FailAtomicAudit = false;

        await using (var rolledBack = NewContext())
        {
            (await rolledBack.TenantPaymentAttempts.SingleAsync(row => row.Id == attemptId))
                .State.Should().Be(TenantPaymentAttemptState.Submitted);
            (await rolledBack.TenantLedgerEntries.AnyAsync(row =>
                row.ProviderPaymentAttemptId == attemptId)).Should().BeFalse();
            (await rolledBack.TenantLedgerAllocations.AnyAsync(row =>
                row.BusinessKey.StartsWith($"provider-receipt:{attemptId}:allocation")))
                .Should().BeFalse();
            (await rolledBack.ProviderInboxEvents.AnyAsync(row =>
                row.ProviderEventId == command.ProviderEventId)).Should().BeFalse();
            (await rolledBack.OutboxMessages.AnyAsync(row =>
                row.IdempotencyKey == OutboxIdempotency.Create(
                    "provider-receipt", attemptId.ToString()))).Should().BeFalse();
            (await rolledBack.AtomicAuditLogs.AnyAsync(row =>
                row.CommandType == identity.CommandType
                && row.CommandIdempotencyKey == identity.IdempotencyKey)).Should().BeFalse();
            (await rolledBack.AtomicCommandReceipts.AnyAsync(row =>
                row.CommandType == identity.CommandType
                && row.IdempotencyKey == identity.IdempotencyKey)).Should().BeFalse();
        }

        var recovered = await ExecuteAtomicAsync(identity, command, EventCodec);
        recovered.Value.Outcome.Should().Be(RecordProviderPaymentEventOutcome.Applied);
        await using var verify = NewContext();
        (await verify.TenantLedgerAllocations.SingleAsync(row =>
            row.BusinessKey == $"provider-receipt:{attemptId}:allocation:{scenario.ChargeId}"))
            .DebitEntryId.Should().Be(scenario.ChargeId);
    }

    [SkippableFact]
    public async Task ProviderReceipt_MissingDurableTarget_FailsBeforeAnyMutation()
    {
        SkipIfNoDocker();
        var scenario = await SeedScenarioAsync("missing-target");
        const string providerObjectId = "pi_missing_durable_target";
        long attemptId;
        await using (var db = NewContext())
        {
            var legacyAttempt = Attempt(
                scenario, "legacy-provider-attempt", providerObjectId,
                TenantPaymentAttemptState.Submitted);
            db.TenantPaymentAttempts.Add(legacyAttempt);
            await db.SaveChangesAsync();
            attemptId = legacyAttempt.Id;
            await using var historicalFixtureTransaction = await db.Database.BeginTransactionAsync();
            try
            {
                await db.Database.ExecuteSqlRawAsync(
                    """
                    ALTER TABLE "TenantPaymentAttempts"
                      DISABLE TRIGGER trg_tenant_payment_attempt_write;
                    ALTER TABLE "TenantPaymentAttempts"
                      DISABLE TRIGGER trg_tenant_payment_attempt_charge_target_immutable;
                    UPDATE "TenantPaymentAttempts"
                       SET "AttemptType" = 'LegacyTargetlessCharge',
                           "ChargeLedgerEntryId" = NULL
                     WHERE "Id" = {0};
                    SET CONSTRAINTS ALL IMMEDIATE;
                    ALTER TABLE "TenantPaymentAttempts"
                      ENABLE TRIGGER trg_tenant_payment_attempt_charge_target_immutable;
                    ALTER TABLE "TenantPaymentAttempts"
                      ENABLE TRIGGER trg_tenant_payment_attempt_write;
                    """,
                    attemptId);
                await historicalFixtureTransaction.CommitAsync();
            }
            catch
            {
                await historicalFixtureTransaction.RollbackAsync();
                throw;
            }
        }

        var identity = new AtomicCommandIdentity(
            "payments.provider-event.record", "stripe:evt_missing_durable_target");
        var command = new RecordVerifiedProviderPaymentEventCommand(
            "stripe", "evt_missing_durable_target", "payment_intent.succeeded", "{}",
            providerObjectId, ProviderPaymentEventKind.Succeeded, 100m, "USD", null,
            DateTime.UtcNow, DateTime.UtcNow);
        await FluentActions.Awaiting(() => ExecuteAtomicAsync(identity, command, EventCodec))
            .Should().ThrowAsync<AtomicReceiptInvariantException>();

        await using var verify = NewContext();
        var unchangedAttempt = await verify.TenantPaymentAttempts.SingleAsync(row => row.Id == attemptId);
        unchangedAttempt.AttemptType.Should().Be(TenantPaymentAttemptType.LegacyTargetlessCharge,
            "ambiguous historical data must not be relabeled with invented exact intent");
        unchangedAttempt.ChargeLedgerEntryId.Should().BeNull();
        unchangedAttempt.State.Should().Be(TenantPaymentAttemptState.Submitted);
        unchangedAttempt.ClaimOwner.Should().BeNull("target validation precedes the fenced claim write");
        unchangedAttempt.ClaimToken.Should().BeNull();
        (await verify.ProviderInboxEvents.AnyAsync(row =>
            row.ProviderEventId == command.ProviderEventId)).Should().BeFalse();
        (await verify.TenantLedgerEntries.AnyAsync(row =>
            row.ProviderPaymentAttemptId == attemptId)).Should().BeFalse();
        (await verify.OutboxMessages.AnyAsync(row =>
            row.IdempotencyKey == OutboxIdempotency.Create(
                "provider-receipt", attemptId.ToString()))).Should().BeFalse();
        (await verify.AtomicAuditLogs.AnyAsync(row =>
            row.CommandType == identity.CommandType
            && row.CommandIdempotencyKey == identity.IdempotencyKey)).Should().BeFalse();
        (await verify.AtomicCommandReceipts.AnyAsync(row =>
            row.CommandType == identity.CommandType
            && row.IdempotencyKey == identity.IdempotencyKey)).Should().BeFalse();
    }

    [SkippableFact]
    public async Task Database_RejectsNewTargetlessChargeAndRetiredLegacyClassification()
    {
        SkipIfNoDocker();
        var scenario = await SeedScenarioAsync("invalid-new-intent");

        await using var db = NewContext();
        var targetlessCharge = Attempt(
            scenario, "invalid-targetless-charge", "pi_invalid_targetless",
            TenantPaymentAttemptState.Submitted);
        targetlessCharge.ChargeLedgerEntryId = null;
        db.TenantPaymentAttempts.Add(targetlessCharge);
        await FluentActions.Awaiting(() => db.SaveChangesAsync())
            .Should().ThrowAsync<DbUpdateException>();

        db.ChangeTracker.Clear();
        var retiredLegacy = Attempt(
            scenario, "invalid-new-legacy", "pi_invalid_new_legacy",
            TenantPaymentAttemptState.Submitted);
        retiredLegacy.ChargeLedgerEntryId = null;
        retiredLegacy.AttemptType = TenantPaymentAttemptType.LegacyTargetlessCharge;
        db.TenantPaymentAttempts.Add(retiredLegacy);
        await FluentActions.Awaiting(() => db.SaveChangesAsync())
            .Should().ThrowAsync<DbUpdateException>();

        db.ChangeTracker.Clear();
        (await db.TenantPaymentAttempts.AnyAsync(row =>
            row.IdempotencyKey == "invalid-targetless-charge"
            || row.IdempotencyKey == "invalid-new-legacy")).Should().BeFalse();
    }

    [SkippableFact]
    public async Task CandidateSelection_IsOneTranslatedPostgreSqlQuery_AndSuppressesSubmittedAttempt()
    {
        SkipIfNoDocker();
        var scenario = await SeedScenarioAsync("candidate");

        await using (var db = NewContext())
        {
            var query = AutopayChargeService.BuildCandidateQuery(db);
            var sql = query.ToQueryString();
            sql.Should().Contain("NOT EXISTS");
            sql.Should().Contain("ORDER BY");
            sql.Should().Contain("LIMIT");
            var candidate = await query.SingleAsync();
            candidate.TenantAccountId.Should().Be(scenario.AccountId);
            candidate.ChargeLedgerEntryId.Should().Be(scenario.ChargeId);
            candidate.EnrollmentId.Should().Be(scenario.EnrollmentId);
        }

        await using (var db = NewContext())
        {
            db.TenantPaymentAttempts.Add(Attempt(scenario, "autopay:tenant-charge:" + scenario.ChargeId,
                "pi_existing", TenantPaymentAttemptState.Submitted));
            await db.SaveChangesAsync();
        }

        await using (var db = NewContext())
        {
            (await AutopayChargeService.BuildCandidateQuery(db).CountAsync()).Should().Be(0);
        }
    }

    [SkippableFact]
    public async Task CanonicalProviderKeysAndOpenEnrollment_RejectDuplicates()
    {
        SkipIfNoDocker();
        var scenario = await SeedScenarioAsync("constraints");

        await using (var db = NewContext())
        {
            db.TenantPaymentAttempts.Add(Attempt(scenario, "provider-key", "pi_unique",
                TenantPaymentAttemptState.Submitted));
            await db.SaveChangesAsync();
        }

        await using (var db = NewContext())
        {
            db.TenantPaymentAttempts.Add(Attempt(scenario, "provider-key", "pi_other",
                TenantPaymentAttemptState.Submitted));
            await FluentActions.Invoking(() => db.SaveChangesAsync())
                .Should().ThrowAsync<DbUpdateException>();
        }

        await using (var db = NewContext())
        {
            db.TenantAutopayEnrollments.Add(new TenantAutopayEnrollment
            {
                PortfolioId = scenario.PortfolioId,
                TenantAccountId = scenario.AccountId,
                AuthorizingPartyId = scenario.PartyId,
                Provider = "stripe",
                ProviderCustomerId = "cus_duplicate",
                ProviderPaymentMethodId = "pm_duplicate",
                EnrolledAtUtc = DateTime.UtcNow,
                CreatedByUserId = scenario.UserId,
            });
            await FluentActions.Invoking(() => db.SaveChangesAsync())
                .Should().ThrowAsync<DbUpdateException>();
        }
    }

    [SkippableFact]
    public async Task PortalStatusAndCancellation_UseCanonicalAccountEnrollment()
    {
        SkipIfNoDocker();
        var scenario = await SeedScenarioAsync("portal");
        await using var db = NewContext();
        await using var atomicScope = _services!.CreateAsyncScope();
        var service = new PortalService(
            db,
            new NoopLeaseQaService(),
            TimeProvider.System,
            atomicScope.ServiceProvider.GetRequiredService<IAtomicUnitOfWork>());

        var active = await service.GetAutopayStatusAsync(
            scenario.PortfolioId, scenario.TenantId, scenario.AccountId);
        active.Should().NotBeNull();
        active!.TenantAccountId.Should().Be(scenario.AccountId);
        active.Active.Should().BeTrue();

        var canceled = await service.CancelAutopayAsync(
            new ActiveAccessContext(
                scenario.AuthSessionId,
                scenario.UserId,
                scenario.AccessContextId,
                scenario.PortfolioId,
                scenario.AccessRevision,
                WorkspaceExperience.Tenant,
                null,
                WorkspaceExperience.Tenant),
            scenario.TenantId,
            scenario.AccountId,
            "portal-autopay-cancel-test");
        canceled.Should().NotBeNull();
        canceled!.Active.Should().BeFalse();
        db.ChangeTracker.Clear();
        var enrollment = await db.TenantAutopayEnrollments.SingleAsync(row =>
            row.Id == scenario.EnrollmentId);
        enrollment.CanceledAtUtc.Should().NotBeNull();
        enrollment.CancelReason.Should().Be("Canceled by tenant");
    }

    [SkippableFact]
    public async Task CheckoutAndPaymentIntentTerminalEvents_ResolveOneAttemptAndPostOneReceipt()
    {
        SkipIfNoDocker();
        var scenario = await SeedScenarioAsync("webhook");
        const string paymentIntentId = "pi_checkout_terminal";
        long attemptId;
        await using (var db = NewContext())
        {
            var attempt = Attempt(scenario, "checkout:tenant-charge:" + scenario.ChargeId,
                paymentIntentId, TenantPaymentAttemptState.Submitted);
            attempt.NextAttemptAtUtc = DateTime.UtcNow.AddDays(1);
            db.TenantPaymentAttempts.Add(attempt);
            await db.SaveChangesAsync();
            attemptId = attempt.Id;
        }

        StripePaymentService.ResolveCheckoutPaymentObjectId(new Session
        {
            Id = "cs_terminal",
            PaymentIntentId = paymentIntentId,
        }).Should().Be(paymentIntentId);

        foreach (var (eventId, eventType) in new[]
        {
            ("evt_checkout", "checkout.session.completed"),
            ("evt_intent", "payment_intent.succeeded"),
        })
        {
            var command = new RecordVerifiedProviderPaymentEventCommand(
                "stripe", eventId, eventType, "{}", paymentIntentId,
                ProviderPaymentEventKind.Succeeded, 100m, "usd", null,
                DateTime.UtcNow, DateTime.UtcNow);
            await ExecuteAtomicAsync(
                new AtomicCommandIdentity("payments.provider-event.record", "stripe:" + eventId),
                command, EventCodec);
        }

        await using var verify = NewContext();
        (await verify.TenantPaymentAttempts.SingleAsync(row => row.Id == attemptId))
            .State.Should().Be(TenantPaymentAttemptState.Succeeded);
        (await verify.TenantLedgerEntries.CountAsync(row =>
            row.ProviderPaymentAttemptId == attemptId)).Should().Be(1);
        (await verify.ProviderInboxEvents.CountAsync(row =>
            row.ProviderObjectId == paymentIntentId)).Should().Be(2);
    }

    [SkippableFact]
    public async Task ProviderInbox_TakeoverFencesStaleCompletionAndFailure_AndRecoversOnce()
    {
        SkipIfNoDocker();
        var scenario = await SeedScenarioAsync("provider-inbox-fencing");
        const string succeededObjectId = "pi_fenced_success";
        const string failedObjectId = "pi_fenced_failure";
        long succeededAttemptId;
        long failedAttemptId;
        long succeededEventId;
        long failedEventId;

        await using (var db = NewContext())
        {
            var succeededAttempt = Attempt(scenario, "provider-inbox-success", succeededObjectId,
                TenantPaymentAttemptState.Submitted);
            var failedAttempt = Attempt(scenario, "provider-inbox-failure", failedObjectId,
                TenantPaymentAttemptState.Submitted);
            db.TenantPaymentAttempts.AddRange(succeededAttempt, failedAttempt);
            await db.SaveChangesAsync();
            succeededAttemptId = succeededAttempt.Id;
            failedAttemptId = failedAttempt.Id;

            var databaseNow = await db.Database
                .SqlQuery<DateTime>($"SELECT clock_timestamp() AS \"Value\"")
                .SingleAsync();
            var succeededEvent = ProviderEvent(
                "evt_fenced_success", succeededObjectId, ProviderPaymentEventKind.Succeeded, databaseNow);
            var failedEvent = ProviderEvent(
                "evt_fenced_failure", failedObjectId, ProviderPaymentEventKind.Failed, databaseNow);
            db.ProviderInboxEvents.AddRange(succeededEvent, failedEvent);
            await db.SaveChangesAsync();
            succeededEventId = succeededEvent.Id;
            failedEventId = failedEvent.Id;
        }

        ProviderInboxClaim[] staleClaims;
        await using (var db = NewContext())
        {
            staleClaims = (await new ProviderInboxClaimStore(db)
                    .ClaimAsync("worker-a", TimeSpan.FromMinutes(2), 10))
                .ToArray();
            staleClaims.Should().HaveCount(2);
            staleClaims.Select(claim => claim.ClaimToken).Should().OnlyHaveUniqueItems();
        }

        await using (var db = NewContext())
        {
            (await new ProviderInboxClaimStore(db)
                    .ClaimAsync("worker-b", TimeSpan.FromMinutes(2), 10))
                .Should().BeEmpty("an unexpired claim token has one owner");
            await db.Database.ExecuteSqlInterpolatedAsync($$"""
                UPDATE "ProviderInboxEvents"
                SET "ClaimExpiresAtUtc" = clock_timestamp() - interval '1 second'
                WHERE "Id" IN ({{succeededEventId}}, {{failedEventId}})
                """);
        }

        ProviderInboxClaim[] currentClaims;
        await using (var db = NewContext())
        {
            currentClaims = (await new ProviderInboxClaimStore(db)
                    .ClaimAsync("worker-b", TimeSpan.FromMinutes(2), 10))
                .ToArray();
            currentClaims.Should().HaveCount(2);
            currentClaims.Select(claim => claim.ClaimToken)
                .Should().NotIntersectWith(staleClaims.Select(claim => claim.ClaimToken));
            currentClaims.Should().OnlyContain(claim => claim.ClaimOwner == "worker-b");
        }

        foreach (var staleClaim in staleClaims)
        {
            await FluentActions.Awaiting(() => ReconcileAsync(staleClaim))
                .Should().ThrowAsync<AtomicReceiptInvariantException>(
                    "a reclaimed event cannot be completed or failed by its former token owner");
        }

        await using (var verify = NewContext())
        {
            (await verify.TenantPaymentAttempts.SingleAsync(row => row.Id == succeededAttemptId))
                .State.Should().Be(TenantPaymentAttemptState.Submitted);
            (await verify.TenantPaymentAttempts.SingleAsync(row => row.Id == failedAttemptId))
                .State.Should().Be(TenantPaymentAttemptState.Submitted);
            (await verify.TenantLedgerEntries.CountAsync(row =>
                row.ProviderPaymentAttemptId == succeededAttemptId)).Should().Be(0);
        }

        var currentSucceeded = currentClaims.Single(claim => claim.Id == succeededEventId);
        var currentFailed = currentClaims.Single(claim => claim.Id == failedEventId);
        (await ReconcileAsync(currentSucceeded)).Value.Outcome
            .Should().Be(ReconcileProviderPaymentEventOutcome.Applied);
        (await ReconcileAsync(currentFailed)).Value.Outcome
            .Should().Be(ReconcileProviderPaymentEventOutcome.Applied);

        var duplicateCommand = new RecordVerifiedProviderPaymentEventCommand(
            "stripe", "evt_fenced_success", "payment_intent.succeeded", "{}", succeededObjectId,
            ProviderPaymentEventKind.Succeeded, 100m, "USD", null, DateTime.UtcNow, DateTime.UtcNow);
        var duplicate = await ExecuteAtomicAsync(
            new AtomicCommandIdentity("payments.provider-event.record", "stripe:evt_fenced_success"),
            duplicateCommand, EventCodec);
        var replay = await ExecuteAtomicAsync(
            new AtomicCommandIdentity("payments.provider-event.record", "stripe:evt_fenced_success"),
            duplicateCommand, EventCodec);
        duplicate.Value.Outcome.Should().Be(RecordProviderPaymentEventOutcome.Duplicate);
        replay.Disposition.Should().Be(AtomicCommandDisposition.Replayed);
        replay.Value.ProviderInboxEventId.Should().Be(succeededEventId);

        await using (var verify = NewContext())
        {
            (await verify.ProviderInboxEvents.CountAsync(row =>
                row.Provider == "stripe" && row.ProviderEventId == "evt_fenced_success"))
                .Should().Be(1);
            (await verify.ProviderInboxEvents.CountAsync(row =>
                (row.Id == succeededEventId || row.Id == failedEventId)
                && row.ProcessedAtUtc != null
                && row.ClaimOwner == null
                && row.ClaimToken == null
                && row.ClaimExpiresAtUtc == null))
                .Should().Be(2, "only the replacement tokens finalize and release the durable events");
            (await verify.TenantPaymentAttempts.SingleAsync(row => row.Id == succeededAttemptId))
                .State.Should().Be(TenantPaymentAttemptState.Succeeded);
            (await verify.TenantPaymentAttempts.SingleAsync(row => row.Id == failedAttemptId))
                .State.Should().Be(TenantPaymentAttemptState.Failed);
            (await verify.TenantLedgerEntries.CountAsync(row =>
                row.ProviderPaymentAttemptId == succeededAttemptId)).Should().Be(1);
            (await verify.OutboxMessages.CountAsync(row =>
                row.IdempotencyKey == OutboxIdempotency.Create(
                    "provider-receipt", succeededAttemptId.ToString())))
                .Should().Be(1);
            var succeededAuditKey = $"{currentSucceeded.Id}:{currentSucceeded.ClaimToken:N}";
            var failedAuditKey = $"{currentFailed.Id}:{currentFailed.ClaimToken:N}";
            (await verify.AtomicAuditLogs.CountAsync(row =>
                row.CommandType == "payments.provider-inbox.reconcile"
                && (row.CommandIdempotencyKey == succeededAuditKey
                    || row.CommandIdempotencyKey == failedAuditKey)
                && row.PortfolioId == scenario.PortfolioId
                && row.EntityType == nameof(TenantAccount)
                && row.EntityId == scenario.AccountId
                && row.Operation == AuditLogOperation.Updated
                && row.ActorLabel == "provider:worker:worker-b"
                && (row.ChangeReason == "Claimed provider event evt_fenced_success reconciled"
                    || row.ChangeReason == "Claimed provider event evt_fenced_failure reconciled")))
                .Should().Be(2, "each canonical terminal attempt produces one atomic audit event");
        }
    }

    private async Task<Scenario> SeedScenarioAsync(string suffix)
    {
        var now = DateTime.UtcNow;
        await using var db = NewContext();
        var user = new ApplicationUser
        {
            UserName = $"provider-{suffix}@example.test",
            NormalizedUserName = $"PROVIDER-{suffix.ToUpperInvariant()}@EXAMPLE.TEST",
            Email = $"provider-{suffix}@example.test",
            NormalizedEmail = $"PROVIDER-{suffix.ToUpperInvariant()}@EXAMPLE.TEST",
            DisplayName = $"Provider {suffix}",
            SecurityStamp = Guid.NewGuid().ToString("N"),
            ConcurrencyStamp = Guid.NewGuid().ToString("N"),
            CreatedAt = now,
        };
        var portfolio = new Portfolio
        {
            Name = $"Provider {suffix}", ManagementCompanyName = $"Provider {suffix}",
            TimeZone = "UTC", Currency = "USD", CreatedAt = now, UpdatedAt = now,
        };
        db.AddRange(user, portfolio);
        await db.SaveChangesAsync();
        await new ChartOfAccountsSeedService(db).SeedAsync(portfolio.Id);
        await db.SaveChangesAsync();
        var accessContext = new WorkspaceAccessContext
        {
            UserId = user.Id, PortfolioId = portfolio.Id,
            Status = WorkspaceAccessContextStatus.Active,
            LastAuthorizedExperience = WorkspaceExperience.Tenant,
            CreatedAtUtc = now, UpdatedAtUtc = now,
        };
        db.Add(accessContext);
        await db.SaveChangesAsync();
        var authSession = new AuthSession
        {
            Id = Guid.NewGuid(),
            UserId = user.Id,
            ActiveAccessContextId = accessContext.Id,
            Status = AuthSessionStatus.Active,
            CreatedAtUtc = now,
            LastSeenAtUtc = now,
            ExpiresAtUtc = now.AddHours(1),
        };
        db.Add(authSession);
        await db.SaveChangesAsync();
        var property = new Property
        {
            PortfolioId = portfolio.Id, Name = $"Property {suffix}", AddressLine1 = "1 Pay Way",
            City = "Columbus", State = "OH", PostalCode = "43215", CreatedAt = now, UpdatedAt = now,
        };
        var unit = new Unit
        {
            PortfolioId = portfolio.Id, Property = property, UnitNumber = "1",
            CreatedAt = now, UpdatedAt = now,
        };
        var tenant = new Tenant
        {
            PortfolioId = portfolio.Id, FirstName = "Tenant", LastName = suffix,
            CreatedAt = now, UpdatedAt = now,
        };
        var template = new DocumentTemplate
        {
            PortfolioId = portfolio.Id, Name = $"Template {suffix}", Version = 1,
            CreatedAtUtc = now, UpdatedAtUtc = now,
        };
        db.AddRange(property, unit, tenant, template);
        await db.SaveChangesAsync();
        var documentSourceVersion = new LegalDocumentSourceVersion
        {
            PublicId = Guid.NewGuid(), PortfolioId = portfolio.Id,
            SourceKind = LegalDocumentSourceKind.AuthoredTemplateSnapshot,
            BusinessKey = $"template:{template.Id}:v{template.Version}",
            DocumentTemplateId = template.Id, DocumentTemplateVersion = template.Version,
            RendererKey = "lease-agreement-overlay", RendererVersion = 1,
            SnapshotPayload = "{}", CreatedAtUtc = now, CreatedByUserId = user.Id,
        };
        db.Add(documentSourceVersion);
        await db.SaveChangesAsync();
        var relationship = new LeaseManagement
        {
            PortfolioId = portfolio.Id, PropertyId = property.Id, UnitId = unit.Id,
            RelationshipNumber = $"LM-{suffix}", CreatedAtUtc = now, UpdatedAtUtc = now,
            CreatedByUserId = user.Id,
        };
        db.Add(relationship);
        await db.SaveChangesAsync();
        var account = new TenantAccount
        {
            PortfolioId = portfolio.Id, LeaseManagementId = relationship.Id,
            AccountNumber = $"TA-{suffix}", Currency = "USD", OpenedAtUtc = now,
            CreatedAtUtc = now, CreatedByUserId = user.Id,
        };
        var party = new LeaseManagementParty
        {
            PortfolioId = portfolio.Id, LeaseManagementId = relationship.Id, TenantId = tenant.Id,
            Role = LeaseManagementPartyRole.PrimaryTenant, EffectiveFrom = DateOnly.FromDateTime(now.AddDays(-1)),
            ChangeReason = "integration", CreatedAtUtc = now, CreatedByUserId = user.Id,
        };
        var agreement = new LeaseAgreement
        {
            PublicId = Guid.NewGuid(), PortfolioId = portfolio.Id,
            LeaseManagementId = relationship.Id, VersionNumber = 1,
            AgreementNumber = $"AGR-{suffix}", ChangeType = LeaseAgreementChangeType.Initial,
            TermType = LeaseAgreementTermType.FixedTerm,
            TermStartOn = DateOnly.FromDateTime(now.AddMonths(-1)),
            TermEndOn = DateOnly.FromDateTime(now.AddMonths(1)),
            GoverningFromOn = DateOnly.FromDateTime(now.AddMonths(-1)),
            BaseRentAmount = 100m, RentDueDay = 1, SecurityDepositObligation = 0m,
            LateFeeAmount = 0m, GracePeriodDays = 0, Currency = "USD",
            TermsSchemaVersion = 1, TermsPayload = "{}",
            DocumentSourceVersionId = documentSourceVersion.Id,
            CreatedAtUtc = now, UpdatedAtUtc = now, CreatedByUserId = user.Id,
        };
        db.AddRange(account, party, agreement);
        await db.SaveChangesAsync();
        var enrollment = new TenantAutopayEnrollment
        {
            PortfolioId = portfolio.Id, TenantAccountId = account.Id, AuthorizingPartyId = party.Id,
            Provider = "stripe", ProviderCustomerId = $"cus_{suffix}",
            ProviderPaymentMethodId = $"pm_{suffix}", EnrolledAtUtc = now, CreatedByUserId = user.Id,
        };
        var access = new TenantUserAccess
        {
            PublicId = Guid.NewGuid(), PortfolioId = portfolio.Id,
            AccessContextId = accessContext.Id,
            ApplicationUserId = user.Id, LeaseManagementPartyId = party.Id,
            GrantedAtUtc = now, GrantedByUserId = user.Id, Reason = "integration portal access",
        };
        var charge = new TenantLedgerEntry
        {
            PortfolioId = portfolio.Id, TenantAccountId = account.Id,
            EntryType = TenantLedgerEntryType.RentCharge, Direction = TenantLedgerDirection.Debit,
            Amount = 100m, Currency = "USD", EffectiveOn = DateOnly.FromDateTime(now),
            DueOn = DateOnly.FromDateTime(now), PostedAtUtc = now, Description = "Rent",
            BusinessKey = $"rent:{suffix}", LeaseAgreementId = agreement.Id,
            CreatedByUserId = user.Id,
        };
        db.AddRange(access, enrollment, charge);
        await db.SaveChangesAsync();
        return new Scenario(
            portfolio.Id, account.Id, party.Id, enrollment.Id, charge.Id, user.Id, tenant.Id,
            authSession.Id, accessContext.Id, accessContext.AccessRevision);
    }

    private static TenantPaymentAttempt Attempt(
        Scenario scenario, string key, string providerObjectId, TenantPaymentAttemptState state)
    {
        var preparedAtUtc = DateTime.UtcNow.AddMinutes(-1);
        return new()
        {
            PortfolioId = scenario.PortfolioId,
            TenantAccountId = scenario.AccountId,
            Provider = "stripe",
            ProviderObjectId = providerObjectId,
            IdempotencyKey = key,
            AttemptType = TenantPaymentAttemptType.Charge,
            State = state,
            Amount = 100m,
            Currency = "USD",
            ChargeLedgerEntryId = scenario.ChargeId,
            PreparedAtUtc = preparedAtUtc,
            SubmittedAtUtc = preparedAtUtc,
            UpdatedAtUtc = DateTime.UtcNow,
            CreatedByUserId = scenario.UserId,
        };
    }

    private static ProviderInboxEvent ProviderEvent(
        string eventId,
        string providerObjectId,
        ProviderPaymentEventKind eventKind,
        DateTime databaseNow) => new()
    {
        Provider = "stripe",
        ProviderEventId = eventId,
        EventType = eventKind == ProviderPaymentEventKind.Succeeded
            ? "payment_intent.succeeded"
            : "payment_intent.payment_failed",
        Payload = "{}",
        ProviderObjectId = providerObjectId,
        EventKind = eventKind,
        Amount = 100m,
        Currency = "USD",
        FailureReason = eventKind == ProviderPaymentEventKind.Failed ? "Provider declined payment." : null,
        OccurredAtUtc = databaseNow,
        ReceivedAtUtc = databaseNow,
        NextAttemptAtUtc = databaseNow,
    };

    private Task<AtomicCommandOutcome<ReconcileClaimedProviderPaymentEventResult>> ReconcileAsync(
        ProviderInboxClaim claim) => ExecuteAtomicAsync(
        new AtomicCommandIdentity(
            "payments.provider-inbox.reconcile", $"{claim.Id}:{claim.ClaimToken:N}"),
        new ReconcileClaimedProviderPaymentEventCommand(
            claim.Id, claim.ClaimOwner, claim.ClaimToken, DateTime.UtcNow),
        ReconcileCodec);

    private async Task<AtomicCommandOutcome<TResult>> ExecuteAtomicAsync<TCommand, TResult>(
        AtomicCommandIdentity identity,
        TCommand command,
        AtomicJsonResultCodec<TResult> codec,
        CancellationToken ct = default)
        where TCommand : notnull, IAtomicCommandData
        where TResult : notnull
    {
        await using var scope = _services!.CreateAsyncScope();
        return await scope.ServiceProvider
            .GetRequiredService<IAtomicUnitOfWork>()
            .ExecuteAsync(identity, command, codec, ct);
    }

    private ProviderPaymentFailureInterceptor Failure =>
        _services!.GetRequiredService<ProviderPaymentFailureInterceptor>();

    private RentalCommandDbContext NewContext() => new(
        new DbContextOptionsBuilder<RentalCommandDbContext>()
            .UseNpgsql(_postgres!.GetConnectionString()).Options);

    private void SkipIfNoDocker() =>
        Skip.IfNot(_dockerAvailable, "Docker is required for provider-payment PostgreSQL tests.");

    private sealed class TestActor : ICurrentActor
    {
        public int? UserId => null;
        public string? ActorLabel => "integration:provider-payment";
        public string? IpAddress => "127.0.0.1";
    }

    private static TenantLedgerEntry Charge(Scenario scenario, string suffix, decimal amount) => new()
    {
        PortfolioId = scenario.PortfolioId,
        TenantAccountId = scenario.AccountId,
        EntryType = TenantLedgerEntryType.ManualCharge,
        Direction = TenantLedgerDirection.Debit,
        Amount = amount,
        Currency = "USD",
        EffectiveOn = DateOnly.FromDateTime(DateTime.UtcNow),
        DueOn = DateOnly.FromDateTime(DateTime.UtcNow),
        PostedAtUtc = DateTime.UtcNow,
        Description = $"Charge {suffix}",
        BusinessKey = $"charge:{suffix}",
        CreatedByUserId = scenario.UserId,
    };

    private sealed class ProviderPaymentFailureInterceptor : DbCommandInterceptor
    {
        public bool FailAtomicAudit { get; set; }

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            MaybeFail(command);
            return ValueTask.FromResult(result);
        }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            MaybeFail(command);
            return ValueTask.FromResult(result);
        }

        private void MaybeFail(DbCommand command)
        {
            if (FailAtomicAudit
                && command.CommandText.Contains(
                    "INSERT INTO \"AtomicAuditLogs\"", StringComparison.Ordinal))
                throw new InvalidOperationException("injected provider-payment audit failure");
        }
    }

    private sealed record Scenario(
        int PortfolioId, int AccountId, int PartyId, int EnrollmentId, long ChargeId,
        int UserId, int TenantId, Guid AuthSessionId, int AccessContextId, long AccessRevision);
}
