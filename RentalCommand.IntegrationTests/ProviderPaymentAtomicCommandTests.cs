using System.Data.Common;
using System.Collections.Concurrent;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using RentalCommand.Api.Services.Domain;
using RentalCommand.Api.Services.Payments;
using RentalCommand.Core;
using RentalCommand.Core.Atomic;
using RentalCommand.Core.Authorization;
using RentalCommand.Core.Configuration;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Interfaces;
using RentalCommand.Core.Outbox;
using RentalCommand.Core.Payments;
using RentalCommand.Data;
using RentalCommand.Data.Accounting;
using RentalCommand.Data.Atomic;
using RentalCommand.Data.Authorization;
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
    private static readonly AtomicJsonResultCodec<SubmitProviderPaymentCreateResult> SubmitCodec =
        new("submit-provider-payment-create-result.v1");
    private static readonly AtomicJsonResultCodec<RecordTenantReceiptResult> ReceiptCodec =
        new("tenant-account.receipt.record.v1");
    private static readonly AtomicJsonResultCodec<FailProviderPaymentCreateResult> FailCodec =
        new("fail-provider-payment-create-result.v1");
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
            SubmitProviderPaymentCreateCommand,
            SubmitProviderPaymentCreateResult,
            SubmitProviderPaymentCreateHandler>();
        services.AddAtomicCommandHandler<
            RecordTenantReceiptCommand,
            RecordTenantReceiptResult,
            RecordTenantReceiptHandler>();
        services.AddAtomicCommandHandler<
            FinalizeProviderPaymentCreateCommand,
            FinalizeProviderPaymentCreateResult,
            FinalizeProviderPaymentCreateHandler>();
        services.AddAtomicCommandHandler<
            FailProviderPaymentCreateCommand,
            FailProviderPaymentCreateResult,
            FailProviderPaymentCreateHandler>();
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
        await db.Database.ExecuteSqlRawAsync(RelationshipAccessProjectionSql.Create);
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
    public async Task ProviderReservation_ChequeCommittedBetweenPrepareAndSubmit_CancelsWithoutProviderCreate()
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
        var receipt = new RecordTenantReceiptCommand(
            scenario.PortfolioId, scenario.AccountId, 20m,
            DateOnly.FromDateTime(DateTime.UtcNow), "Concurrent exact-target receipt", "Check",
            null, null, null, null, null, targetChargeId, scenario.UserId, scenario.AuthSessionId,
            scenario.AccessContextId, scenario.AccessRevision, CapabilityKeys.MoneyPaymentsManage,
            "receipt:exact-target:advance",
            $"tenant-receipt:{scenario.PortfolioId}:{scenario.AccountId}:exact-target-advance");
        var receiptOutcome = await ExecuteAtomicAsync(
            new AtomicCommandIdentity("tenant-account.receipt.record", receipt.DeliveryIdempotencyKey),
            receipt, ReceiptCodec);
        receiptOutcome.Value.AllocatedAmount.Should().Be(20m);
        receiptOutcome.Value.AllocationCount.Should().Be(1);

        var submitIdentity = new AtomicCommandIdentity("payments.provider-create.submit", key);
        var submitted = await ExecuteAtomicAsync(
            submitIdentity,
            new SubmitProviderPaymentCreateCommand(
                scenario.PortfolioId, scenario.AccountId, prepared.Value.PaymentAttemptId,
                "stripe", key, DateTime.UtcNow),
            SubmitCodec);
        submitted.Value.Outcome.Should().Be(SubmitProviderPaymentCreateOutcome.Canceled);
        submitted.Value.State.Should().Be(TenantPaymentAttemptState.Canceled);

        await using var verify = NewContext();
        (await verify.TenantPaymentAttempts.SingleAsync(row =>
            row.Id == prepared.Value.PaymentAttemptId)).State.Should().Be(TenantPaymentAttemptState.Canceled);
        (await verify.TenantLedgerEntries.AnyAsync(row =>
            row.ProviderPaymentAttemptId == prepared.Value.PaymentAttemptId)).Should().BeFalse();
        (await verify.TenantLedgerAllocations.AnyAsync(row =>
            row.DebitEntryId == targetChargeId
            && row.CreditEntryId == receiptOutcome.Value.LedgerEntryId
            && row.Amount == 20m)).Should().BeTrue();
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
    public async Task ProviderReceipt_MissingDurableTarget_IsDeadLetteredBeforeAnyReceiptMutation()
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
        var outcome = await ExecuteAtomicAsync(identity, command, EventCodec);
        outcome.Value.Outcome.Should().Be(RecordProviderPaymentEventOutcome.Unmatched);

        await using var verify = NewContext();
        var unchangedAttempt = await verify.TenantPaymentAttempts.SingleAsync(row => row.Id == attemptId);
        unchangedAttempt.AttemptType.Should().Be(TenantPaymentAttemptType.LegacyTargetlessCharge,
            "ambiguous historical data must not be relabeled with invented exact intent");
        unchangedAttempt.ChargeLedgerEntryId.Should().BeNull();
        unchangedAttempt.State.Should().Be(TenantPaymentAttemptState.Submitted);
        unchangedAttempt.ClaimOwner.Should().BeNull("target validation precedes the fenced claim write");
        unchangedAttempt.ClaimToken.Should().BeNull();
        var inbox = await verify.ProviderInboxEvents.SingleAsync(row =>
            row.ProviderEventId == command.ProviderEventId);
        inbox.FailureKind.Should().Be(ProviderInboxFailureKind.Permanent);
        inbox.DeadLetteredAtUtc.Should().NotBeNull();
        inbox.LastError.Should().Contain("receipt finalization");
        (await verify.TenantLedgerEntries.AnyAsync(row =>
            row.ProviderPaymentAttemptId == attemptId)).Should().BeFalse();
        (await verify.OutboxMessages.AnyAsync(row =>
            row.IdempotencyKey == OutboxIdempotency.Create(
                "provider-receipt", attemptId.ToString()))).Should().BeFalse();
        (await verify.AtomicCommandReceipts.CountAsync(row =>
            row.CommandType == identity.CommandType
            && row.IdempotencyKey == identity.IdempotencyKey)).Should().Be(1);
    }

    [SkippableFact]
    public async Task ProviderReceipt_ReversedTarget_IsDeadLetteredAndWebhookWorkCommits()
    {
        SkipIfNoDocker();
        var scenario = await SeedScenarioAsync("reversed-target");
        const string providerObjectId = "pi_reversed_target";
        long attemptId;
        await using (var db = NewContext())
        {
            db.TenantLedgerEntries.Add(new TenantLedgerEntry
            {
                PortfolioId = scenario.PortfolioId,
                TenantAccountId = scenario.AccountId,
                EntryType = TenantLedgerEntryType.Reversal,
                Direction = TenantLedgerDirection.Credit,
                Amount = 100m,
                Currency = "USD",
                EffectiveOn = DateOnly.FromDateTime(DateTime.UtcNow),
                PostedAtUtc = DateTime.UtcNow,
                Description = "Reversal for provider target",
                BusinessKey = "reversal:provider-target",
                ReversesEntryId = scenario.ChargeId,
                CreatedByUserId = scenario.UserId,
            });
            var attempt = Attempt(scenario, "provider-reversed-target", providerObjectId,
                TenantPaymentAttemptState.Submitted);
            db.TenantPaymentAttempts.Add(attempt);
            await db.SaveChangesAsync();
            attemptId = attempt.Id;
        }

        var identity = new AtomicCommandIdentity(
            "payments.provider-event.record", "stripe:evt_reversed_target");
        var command = new RecordVerifiedProviderPaymentEventCommand(
            "stripe", "evt_reversed_target", "payment_intent.succeeded", "{}",
            providerObjectId, ProviderPaymentEventKind.Succeeded, 100m, "USD", null,
            DateTime.UtcNow, DateTime.UtcNow);
        var outcome = await ExecuteAtomicAsync(identity, command, EventCodec);

        outcome.Value.Outcome.Should().Be(RecordProviderPaymentEventOutcome.Unmatched);
        await using var verify = NewContext();
        var inbox = await verify.ProviderInboxEvents.SingleAsync(row =>
            row.ProviderEventId == command.ProviderEventId);
        inbox.FailureKind.Should().Be(ProviderInboxFailureKind.Permanent);
        inbox.DeadLetteredAtUtc.Should().NotBeNull();
        inbox.LastError.Should().Contain("valid target charge");
        (await verify.AtomicAuditLogs.SingleAsync(row =>
            row.CommandType == identity.CommandType
            && row.CommandIdempotencyKey == identity.IdempotencyKey))
            .ChangeReason.Should().Contain("dead-lettered");
        (await verify.TenantPaymentAttempts.SingleAsync(row => row.Id == attemptId))
            .State.Should().Be(TenantPaymentAttemptState.Submitted);
        (await verify.TenantLedgerEntries.AnyAsync(row =>
            row.ProviderPaymentAttemptId == attemptId)).Should().BeFalse();
        (await verify.TenantLedgerAllocations.AnyAsync(row =>
            row.BusinessKey.StartsWith($"provider-receipt:{attemptId}:allocation")))
            .Should().BeFalse();
        (await verify.AtomicCommandReceipts.CountAsync(row =>
            row.CommandType == identity.CommandType
            && row.IdempotencyKey == identity.IdempotencyKey)).Should().Be(1);
    }

    [SkippableFact]
    public async Task ProviderReceipt_PreexistingAllocationReservation_DeadLettersBeforeAnyMoneyWrite()
    {
        SkipIfNoDocker();
        var scenario = await SeedScenarioAsync("post-write-allocation-mismatch");
        const string providerObjectId = "pi_post_write_allocation_mismatch";
        long attemptId;
        long priorCreditId;

        await using (var db = NewContext())
        {
            var priorCredit = new TenantLedgerEntry
            {
                PortfolioId = scenario.PortfolioId,
                TenantAccountId = scenario.AccountId,
                EntryType = TenantLedgerEntryType.PaymentReceipt,
                Direction = TenantLedgerDirection.Credit,
                Amount = 1m,
                Currency = "USD",
                EffectiveOn = DateOnly.FromDateTime(DateTime.UtcNow),
                PostedAtUtc = DateTime.UtcNow,
                Description = "Historical allocation reservation fixture",
                BusinessKey = "receipt:post-write-allocation-fixture",
                CreatedByUserId = scenario.UserId,
            };
            db.TenantLedgerEntries.Add(priorCredit);
            await db.SaveChangesAsync();
            priorCreditId = priorCredit.Id;

            var attempt = Attempt(scenario, "provider-post-write-allocation-mismatch",
                providerObjectId, TenantPaymentAttemptState.Submitted);
            db.TenantPaymentAttempts.Add(attempt);
            await db.SaveChangesAsync();
            attemptId = attempt.Id;

            // This row is deliberately a historical partial-write fixture. Bypass the live
            // reservation trigger only while seeding it; production inserts must honor the fence.
            await db.Database.ExecuteSqlRawAsync(
                "ALTER TABLE \"TenantLedgerAllocations\" DISABLE TRIGGER trg_tenant_ledger_allocation_open_account;");
            try
            {
                db.TenantLedgerAllocations.Add(new TenantLedgerAllocation
                {
                    PortfolioId = scenario.PortfolioId,
                    TenantAccountId = scenario.AccountId,
                    DebitEntryId = scenario.ChargeId,
                    CreditEntryId = priorCreditId,
                    Amount = 1m,
                    AllocatedAtUtc = DateTime.UtcNow,
                    BusinessKey = $"provider-receipt:{attemptId}:allocation:{scenario.ChargeId}",
                    CreatedByUserId = scenario.UserId,
                });
                await db.SaveChangesAsync();
            }
            finally
            {
                await db.Database.ExecuteSqlRawAsync(
                    "ALTER TABLE \"TenantLedgerAllocations\" ENABLE TRIGGER trg_tenant_ledger_allocation_open_account;");
            }
        }

        await using (var verifyAttempt = NewContext())
        {
            var storedAttempt = await verifyAttempt.TenantPaymentAttempts.SingleAsync(row => row.Id == attemptId);
            storedAttempt.Provider.Should().Be("stripe");
            storedAttempt.ProviderObjectId.Should().Be(providerObjectId);
        }

        var identity = new AtomicCommandIdentity(
            "payments.provider-event.record", "stripe:evt_post_write_allocation_mismatch");
        var command = new RecordVerifiedProviderPaymentEventCommand(
            "stripe", "evt_post_write_allocation_mismatch", "payment_intent.succeeded", "{}",
            providerObjectId, ProviderPaymentEventKind.Succeeded, 100m, "USD", null,
            DateTime.UtcNow, DateTime.UtcNow);
        var recorded = await ExecuteAtomicAsync(identity, command, EventCodec);
        recorded.Value.Outcome.Should().Be(RecordProviderPaymentEventOutcome.Unmatched);

        await using var verify = NewContext();
        (await verify.TenantPaymentAttempts.SingleAsync(row => row.Id == attemptId))
            .State.Should().Be(TenantPaymentAttemptState.Submitted);
        (await verify.TenantLedgerEntries.CountAsync(row =>
            row.ProviderPaymentAttemptId == attemptId)).Should().Be(0);
        (await verify.JournalEntries.CountAsync(row =>
            row.SourceBusinessKey == $"provider-receipt:{attemptId}")).Should().Be(0);
        (await verify.JournalLines.CountAsync(row =>
            row.JournalEntry!.SourceBusinessKey == $"provider-receipt:{attemptId}")).Should().Be(0);
        (await verify.TenantLedgerAllocations.CountAsync(row =>
            row.BusinessKey.StartsWith($"provider-receipt:{attemptId}:allocation")))
            .Should().Be(1, "only the pre-existing fixture allocation may remain");
        var inbox = await verify.ProviderInboxEvents.SingleAsync(row =>
            row.ProviderEventId == command.ProviderEventId);
        inbox.FailureKind.Should().Be(ProviderInboxFailureKind.Permanent);
        inbox.DeadLetteredAtUtc.Should().NotBeNull();
        (await verify.AtomicAuditLogs.CountAsync(row =>
            row.CommandType == identity.CommandType
            && row.CommandIdempotencyKey == identity.IdempotencyKey
            && row.ChangeReason!.Contains("dead-lettered"))).Should().Be(1);
    }

    [SkippableTheory]
    [InlineData(TenantLedgerEntryType.PaymentReceipt, TenantLedgerDirection.Credit, "non-charge")]
    [InlineData(TenantLedgerEntryType.Refund, TenantLedgerDirection.Debit, "refund")]
    [InlineData(TenantLedgerEntryType.TransferOut, TenantLedgerDirection.Debit, "transfer")]
    public async Task ProviderReceipt_InvalidPermanentTargetBranches_DeadLetterBeforeAnyMoneyWrite(
        TenantLedgerEntryType invalidType, TenantLedgerDirection direction, string suffix)
    {
        SkipIfNoDocker();
        var scenario = await SeedScenarioAsync($"invalid-target-{suffix}");
        const decimal amount = 100m;
        const string provider = "stripe";
        var providerObjectId = $"pi_invalid_target_{suffix}";
        long targetEntryId;
        long attemptId;
        await using (var db = NewContext())
        {
            var invalidTarget = new TenantLedgerEntry
            {
                PortfolioId = scenario.PortfolioId,
                TenantAccountId = scenario.AccountId,
                EntryType = invalidType,
                Direction = direction,
                Amount = amount,
                Currency = "USD",
                EffectiveOn = DateOnly.FromDateTime(DateTime.UtcNow),
                PostedAtUtc = DateTime.UtcNow,
                Description = $"Invalid provider target {suffix}",
                BusinessKey = $"invalid-provider-target:{suffix}",
                TransferPublicId = invalidType == TenantLedgerEntryType.TransferOut
                    ? Guid.NewGuid() : null,
                CreatedByUserId = scenario.UserId,
            };
            db.TenantLedgerEntries.Add(invalidTarget);
            await db.SaveChangesAsync();
            targetEntryId = invalidTarget.Id;

            var attempt = Attempt(scenario, $"provider-invalid-target-{suffix}",
                providerObjectId, TenantPaymentAttemptState.Submitted, targetEntryId);
            db.TenantPaymentAttempts.Add(attempt);
            await db.SaveChangesAsync();
            attemptId = attempt.Id;
        }

        var identity = new AtomicCommandIdentity(
            "payments.provider-event.record", $"stripe:evt_invalid_target_{suffix}");
        var command = new RecordVerifiedProviderPaymentEventCommand(
            provider, $"evt_invalid_target_{suffix}", "payment_intent.succeeded", "{}",
            providerObjectId, ProviderPaymentEventKind.Succeeded, amount, "USD", null,
            DateTime.UtcNow, DateTime.UtcNow);
        var outcome = await ExecuteAtomicAsync(identity, command, EventCodec);
        outcome.Value.Outcome.Should().Be(RecordProviderPaymentEventOutcome.Unmatched);

        await using var verify = NewContext();
        (await verify.TenantPaymentAttempts.SingleAsync(row => row.Id == attemptId))
            .State.Should().Be(TenantPaymentAttemptState.Submitted);
        (await verify.TenantLedgerEntries.AnyAsync(row =>
            row.ProviderPaymentAttemptId == attemptId)).Should().BeFalse();
        (await verify.JournalEntries.AnyAsync(row =>
            row.SourceBusinessKey == $"provider-receipt:{attemptId}")).Should().BeFalse();
        (await verify.JournalLines.AnyAsync(row =>
            row.JournalEntry!.SourceBusinessKey == $"provider-receipt:{attemptId}")).Should().BeFalse();
        (await verify.TenantLedgerAllocations.AnyAsync(row =>
            row.BusinessKey.StartsWith($"provider-receipt:{attemptId}:allocation")))
            .Should().BeFalse();
        (await verify.OutboxMessages.AnyAsync(row =>
            row.IdempotencyKey == OutboxIdempotency.Create("provider-receipt", attemptId.ToString())))
            .Should().BeFalse();
        var inbox = await verify.ProviderInboxEvents.SingleAsync(row =>
            row.ProviderEventId == command.ProviderEventId);
        inbox.FailureKind.Should().Be(ProviderInboxFailureKind.Permanent);
        inbox.DeadLetteredAtUtc.Should().NotBeNull();
        (await verify.AtomicAuditLogs.CountAsync(row =>
            row.CommandType == identity.CommandType
            && row.CommandIdempotencyKey == identity.IdempotencyKey
            && row.ChangeReason!.Contains("dead-lettered"))).Should().Be(1);
    }

    [SkippableFact]
    public async Task ProviderReceipt_AmountOrCurrencyMismatch_DeadLettersWithSearchableAudit()
    {
        SkipIfNoDocker();
        var scenario = await SeedScenarioAsync("amount-currency-mismatch");
        const string providerObjectId = "pi_amount_currency_mismatch";
        long attemptId;
        await using (var db = NewContext())
        {
            var attempt = Attempt(scenario, "provider-amount-currency-mismatch",
                providerObjectId, TenantPaymentAttemptState.Submitted);
            db.TenantPaymentAttempts.Add(attempt);
            await db.SaveChangesAsync();
            attemptId = attempt.Id;
        }

        var identity = new AtomicCommandIdentity(
            "payments.provider-event.record", "stripe:evt_amount_currency_mismatch");
        var command = new RecordVerifiedProviderPaymentEventCommand(
            "stripe", "evt_amount_currency_mismatch", "payment_intent.succeeded", "{}",
            providerObjectId, ProviderPaymentEventKind.Succeeded, 99m, "EUR", null,
            DateTime.UtcNow, DateTime.UtcNow);
        var outcome = await ExecuteAtomicAsync(identity, command, EventCodec);
        outcome.Value.Outcome.Should().Be(RecordProviderPaymentEventOutcome.Unmatched);

        await using var verify = NewContext();
        (await verify.TenantPaymentAttempts.SingleAsync(row => row.Id == attemptId))
            .State.Should().Be(TenantPaymentAttemptState.Submitted);
        var inbox = await verify.ProviderInboxEvents.SingleAsync(row =>
            row.ProviderEventId == command.ProviderEventId);
        inbox.FailureKind.Should().Be(ProviderInboxFailureKind.Permanent);
        inbox.DeadLetteredAtUtc.Should().NotBeNull();
        (await verify.TenantLedgerEntries.AnyAsync(row =>
            row.ProviderPaymentAttemptId == attemptId)).Should().BeFalse();
        var audit = await verify.AtomicAuditLogs.SingleAsync(row =>
            row.CommandType == identity.CommandType
            && row.CommandIdempotencyKey == identity.IdempotencyKey);
        audit.ChangeReason.Should().Contain(command.ProviderEventId);
        audit.NewValues.Should().Contain(providerObjectId);
    }

    [SkippableFact]
    public async Task PrepareReplay_RevalidatesOpenAmount_AndFreshAttemptUsesReducedAmount()
    {
        SkipIfNoDocker();
        var scenario = await SeedScenarioAsync("prepare-replay-open-amount");
        var key = StripePaymentService.BuildPaymentIdempotencyKey(
            "checkout", scenario.ChargeId, scenario.UserId, "first-attempt");
        var command = new PrepareProviderPaymentCreateCommand(
            scenario.PortfolioId, scenario.AccountId, scenario.ChargeId, scenario.UserId,
            scenario.TenantId, null, "stripe", key, "USD", DateTime.UtcNow);
        var identity = new AtomicCommandIdentity("payments.provider-create.prepare", key);
        var first = await ExecuteAtomicAsync(identity, command, PrepareCodec);
        first.Value.Amount.Should().Be(100m);

        await using (var db = NewContext())
        {
            var cheque = new TenantLedgerEntry
            {
                PortfolioId = scenario.PortfolioId,
                TenantAccountId = scenario.AccountId,
                EntryType = TenantLedgerEntryType.PaymentReceipt,
                Direction = TenantLedgerDirection.Credit,
                Amount = 40m,
                Currency = "USD",
                EffectiveOn = DateOnly.FromDateTime(DateTime.UtcNow),
                PostedAtUtc = DateTime.UtcNow,
                Description = "Cheque partial payment",
                BusinessKey = "receipt:prepare-replay-cheque",
                CreatedByUserId = scenario.UserId,
            };
            db.TenantLedgerEntries.Add(cheque);
            await db.SaveChangesAsync();
            db.TenantLedgerAllocations.Add(new TenantLedgerAllocation
            {
                PortfolioId = scenario.PortfolioId,
                TenantAccountId = scenario.AccountId,
                DebitEntryId = scenario.ChargeId,
                CreditEntryId = cheque.Id,
                Amount = 40m,
                AllocatedAtUtc = DateTime.UtcNow,
                BusinessKey = "allocation:prepare-replay-cheque",
                CreatedByUserId = scenario.UserId,
            });
            await db.SaveChangesAsync();
        }

        await FluentActions.Awaiting(() => ExecuteAtomicAsync(identity, command, PrepareCodec))
            .Should().ThrowAsync<DomainValidationException>();

        var freshKey = StripePaymentService.BuildPaymentIdempotencyKey(
            "checkout", scenario.ChargeId, scenario.UserId, "fresh-attempt");
        var freshCommand = command with { IdempotencyKey = freshKey };
        var fresh = await ExecuteAtomicAsync(
            new AtomicCommandIdentity("payments.provider-create.prepare", freshKey),
            freshCommand, PrepareCodec);
        fresh.Value.Amount.Should().Be(60m);
    }

    [SkippableFact]
    public async Task Prepare_RecoversOldPreparedSubmittedAndSucceededKeys()
    {
        SkipIfNoDocker();
        var scenario = await SeedScenarioAsync("old-provider-keys");
        long preparedChargeId;
        long submittedChargeId;
        long succeededChargeId;
        await using (var db = NewContext())
        {
            var preparedCharge = Charge(scenario, "old-prepared-charge", 100m);
            var submittedCharge = Charge(scenario, "old-submitted-charge", 100m);
            var succeededCharge = Charge(scenario, "old-succeeded-charge", 100m);
            db.TenantLedgerEntries.AddRange(preparedCharge, submittedCharge, succeededCharge);
            await db.SaveChangesAsync();
            preparedChargeId = preparedCharge.Id;
            submittedChargeId = submittedCharge.Id;
            succeededChargeId = succeededCharge.Id;

            var succeededAttempt = Attempt(scenario, "checkout:tenant-charge:" + succeededChargeId,
                "pi_old_succeeded", TenantPaymentAttemptState.Submitted, succeededChargeId);
            db.TenantPaymentAttempts.Add(succeededAttempt);
            await db.SaveChangesAsync();
        }

        var record = await ExecuteAtomicAsync(
            new AtomicCommandIdentity("payments.provider-event.record", "stripe:evt_old_succeeded"),
            new RecordVerifiedProviderPaymentEventCommand(
                "stripe", "evt_old_succeeded", "payment_intent.succeeded", "{}",
                "pi_old_succeeded", ProviderPaymentEventKind.Succeeded, 100m, "USD", null,
                DateTime.UtcNow, DateTime.UtcNow), EventCodec);
        record.Value.Outcome.Should().Be(RecordProviderPaymentEventOutcome.Applied);
        await using (var db = NewContext())
        {
            (await db.TenantPaymentAttempts.SingleAsync(row => row.ProviderObjectId == "pi_old_succeeded"))
                .State.Should().Be(TenantPaymentAttemptState.Succeeded);
            (await db.ProviderInboxEvents.SingleAsync(row => row.ProviderEventId == "evt_old_succeeded"))
                .ProcessedAtUtc.Should().NotBeNull();
        }

        await using (var db = NewContext())
        {
            db.TenantPaymentAttempts.Add(
                Attempt(scenario, "checkout:tenant-charge:" + preparedChargeId,
                    null, TenantPaymentAttemptState.Prepared, preparedChargeId));
            db.TenantPaymentAttempts.Add(
                Attempt(scenario, "checkout:tenant-charge:" + submittedChargeId,
                    "pi_old_submitted", TenantPaymentAttemptState.Submitted, submittedChargeId));
            await db.SaveChangesAsync();
        }

        foreach (var (chargeId, expectedState, expectedProviderId) in new[]
        {
            (preparedChargeId, TenantPaymentAttemptState.Prepared, (string?)null),
            (submittedChargeId, TenantPaymentAttemptState.Submitted, "pi_old_submitted"),
            (succeededChargeId, TenantPaymentAttemptState.Succeeded, "pi_old_succeeded"),
        })
        {
            var key = $"checkout:tenant-charge:{chargeId}:actor:{scenario.UserId}:attempt:new-key";
            var prepared = await ExecuteAtomicAsync(
                new AtomicCommandIdentity("payments.provider-create.prepare", key),
                new PrepareProviderPaymentCreateCommand(
                    scenario.PortfolioId, scenario.AccountId, chargeId, scenario.UserId,
                    null, null, "stripe", key, "USD", DateTime.UtcNow), PrepareCodec);
            prepared.Value.PaymentAttemptId.Should().BeGreaterThan(0);
            prepared.Value.IdempotencyKey.Should().Be($"checkout:tenant-charge:{chargeId}");
            prepared.Value.State.Should().Be(expectedState);
            prepared.Value.ProviderPaymentId.Should().Be(expectedProviderId);
        }
    }

    [SkippableFact]
    public async Task Prepare_AllowsIndependentSecondActorAttempt()
    {
        SkipIfNoDocker();
        var scenario = await SeedScenarioAsync("prepare-second-actor");
        int secondActorId;
        await using (var db = NewContext())
        {
            var secondUser = new ApplicationUser
            {
                UserName = "provider-second-actor@example.test",
                NormalizedUserName = "PROVIDER-SECOND-ACTOR@EXAMPLE.TEST",
                Email = "provider-second-actor@example.test",
                NormalizedEmail = "PROVIDER-SECOND-ACTOR@EXAMPLE.TEST",
                DisplayName = "Second provider actor",
                SecurityStamp = Guid.NewGuid().ToString("N"),
                ConcurrencyStamp = Guid.NewGuid().ToString("N"),
                CreatedAt = DateTime.UtcNow,
            };
            db.Set<ApplicationUser>().Add(secondUser);
            await db.SaveChangesAsync();
            var secondAccessContext = new WorkspaceAccessContext
            {
                UserId = secondUser.Id,
                PortfolioId = scenario.PortfolioId,
                Status = WorkspaceAccessContextStatus.Active,
                LastAuthorizedExperience = WorkspaceExperience.Tenant,
                CreatedAtUtc = DateTime.UtcNow,
                UpdatedAtUtc = DateTime.UtcNow,
            };
            db.Set<WorkspaceAccessContext>().Add(secondAccessContext);
            await db.SaveChangesAsync();
            db.TenantUserAccesses.Add(new TenantUserAccess
            {
                PublicId = Guid.NewGuid(),
                PortfolioId = scenario.PortfolioId,
                AccessContextId = secondAccessContext.Id,
                ApplicationUserId = secondUser.Id,
                LeaseManagementPartyId = scenario.PartyId,
                GrantedAtUtc = DateTime.UtcNow,
                GrantedByUserId = scenario.UserId,
                Reason = "integration second authorized payer",
            });
            await db.SaveChangesAsync();
            secondActorId = secondUser.Id;
        }

        var key = StripePaymentService.BuildPaymentIdempotencyKey(
            "checkout", scenario.ChargeId, scenario.UserId, "same-attempt");
        var command = new PrepareProviderPaymentCreateCommand(
            scenario.PortfolioId, scenario.AccountId, scenario.ChargeId, scenario.UserId,
            scenario.TenantId, null, "stripe", key, "USD", DateTime.UtcNow);
        var first = await ExecuteAtomicAsync(
            new AtomicCommandIdentity("payments.provider-create.prepare", key), command, PrepareCodec);
        first.Value.Outcome.Should().Be(PrepareProviderPaymentCreateOutcome.Prepared);

        var secondKey = StripePaymentService.BuildPaymentIdempotencyKey(
            "checkout", scenario.ChargeId, secondActorId, "same-attempt");
        var second = command with { ActorUserId = secondActorId, IdempotencyKey = secondKey };
        var secondOutcome = await ExecuteAtomicAsync(
            new AtomicCommandIdentity("payments.provider-create.prepare", secondKey), second, PrepareCodec);
        secondOutcome.Value.Outcome.Should().Be(PrepareProviderPaymentCreateOutcome.Prepared);

        var submits = await Task.WhenAll(
            ExecuteAtomicAsync(
                new AtomicCommandIdentity("payments.provider-create.submit", key),
                new SubmitProviderPaymentCreateCommand(
                    scenario.PortfolioId, scenario.AccountId, first.Value.PaymentAttemptId,
                    "stripe", key, DateTime.UtcNow), SubmitCodec),
            ExecuteAtomicAsync(
                new AtomicCommandIdentity("payments.provider-create.submit", secondKey),
                new SubmitProviderPaymentCreateCommand(
                    scenario.PortfolioId, scenario.AccountId, secondOutcome.Value.PaymentAttemptId,
                    "stripe", secondKey, DateTime.UtcNow), SubmitCodec));

        submits.Select(result => result.Value.Outcome)
            .Should().Contain(SubmitProviderPaymentCreateOutcome.Submitted)
            .And.Contain(SubmitProviderPaymentCreateOutcome.Canceled);
        await using var verify = NewContext();
        (await verify.TenantPaymentAttempts.CountAsync(row =>
            row.ChargeLedgerEntryId == scenario.ChargeId
            && row.State == TenantPaymentAttemptState.Submitted)).Should().Be(1,
                "one charge may have only one active provider fence");
    }

    [SkippableFact]
    public async Task Prepare_ConcurrentDuplicateTapCreatesExactlyOneAttempt()
    {
        SkipIfNoDocker();
        var scenario = await SeedScenarioAsync("prepare-concurrent-duplicate");
        var key = StripePaymentService.BuildPaymentIdempotencyKey(
            "checkout", scenario.ChargeId, scenario.UserId, "duplicate-tap");
        var command = new PrepareProviderPaymentCreateCommand(
            scenario.PortfolioId, scenario.AccountId, scenario.ChargeId, scenario.UserId,
            scenario.TenantId, null, "stripe", key, "USD", DateTime.UtcNow);
        var identity = new AtomicCommandIdentity("payments.provider-create.prepare", key);

        var outcomes = await Task.WhenAll(
            ExecuteAtomicAsync(identity, command, PrepareCodec),
            ExecuteAtomicAsync(identity, command, PrepareCodec));

        outcomes.Select(result => result.Disposition)
            .Should().BeEquivalentTo(
                new[] { AtomicCommandDisposition.Executed, AtomicCommandDisposition.Replayed });
        await using var verify = NewContext();
        (await verify.TenantPaymentAttempts.CountAsync(row =>
            row.ChargeLedgerEntryId == scenario.ChargeId
            && row.Provider == "stripe")).Should().Be(1);
    }

    [SkippableFact]
    public async Task Checkout_ConcurrentDuplicateSubmitUsesOneDeterministicProviderCreate()
    {
        SkipIfNoDocker();
        var scenario = await SeedScenarioAsync("checkout-provider-seam");
        var key = StripePaymentService.BuildPaymentIdempotencyKey(
            "checkout", scenario.ChargeId, scenario.UserId, "provider-seam");
        var command = new PrepareProviderPaymentCreateCommand(
            scenario.PortfolioId, scenario.AccountId, scenario.ChargeId, scenario.UserId,
            scenario.TenantId, null, "stripe", key, "USD", DateTime.UtcNow);
        var identity = new AtomicCommandIdentity("payments.provider-create.prepare", key);

        // These are the two concurrent Checkout requests: the atomic prepare/submit boundary
        // returns the same durable attempt, after which each request reaches the provider seam.
        var prepared = await Task.WhenAll(
            ExecuteAtomicAsync(identity, command, PrepareCodec),
            ExecuteAtomicAsync(identity, command, PrepareCodec));
        prepared.Select(result => result.Value.PaymentAttemptId).Distinct().Should().ContainSingle();
        var attemptId = prepared[0].Value.PaymentAttemptId;
        var submits = await Task.WhenAll(
            ExecuteAtomicAsync(
                new AtomicCommandIdentity("payments.provider-create.submit", key),
                new SubmitProviderPaymentCreateCommand(
                    scenario.PortfolioId, scenario.AccountId, attemptId,
                    "stripe", key, DateTime.UtcNow), SubmitCodec),
            ExecuteAtomicAsync(
                new AtomicCommandIdentity("payments.provider-create.submit", key),
                new SubmitProviderPaymentCreateCommand(
                    scenario.PortfolioId, scenario.AccountId, attemptId,
                    "stripe", key, DateTime.UtcNow), SubmitCodec));
        submits.Select(result => result.Value.PaymentAttemptId).Distinct().Should().ContainSingle();
        submits.Select(result => result.Value.Outcome)
            .All(outcome => outcome == SubmitProviderPaymentCreateOutcome.Submitted
                || outcome == SubmitProviderPaymentCreateOutcome.AlreadySubmitted)
            .Should().BeTrue();

        await using var db = NewContext();
        var attempt = await db.TenantPaymentAttempts.SingleAsync(row => row.Id == attemptId);
        var provider = new FakeAutopayProviderClient();
        await Task.WhenAll(
            provider.CreateAsync(attempt, "cus_checkout_seam", "pm_checkout_seam", CancellationToken.None),
            provider.CreateAsync(attempt, "cus_checkout_seam", "pm_checkout_seam", CancellationToken.None));
        provider.CreateCount.Should().Be(1,
            "the deterministic provider seam must deduplicate concurrent creates by the durable key");
    }

    [SkippableFact]
    public async Task FinalizeSubmittedAttempt_BindsProviderObjectWithExactFence()
    {
        SkipIfNoDocker();
        var scenario = await SeedScenarioAsync("finalize-provider-binding");
        var key = StripePaymentService.BuildPaymentIdempotencyKey(
            "checkout", scenario.ChargeId, scenario.UserId, "provider-binding");
        var prepared = await ExecuteAtomicAsync(
            new AtomicCommandIdentity("payments.provider-create.prepare", key),
            new PrepareProviderPaymentCreateCommand(
                scenario.PortfolioId, scenario.AccountId, scenario.ChargeId, scenario.UserId,
                scenario.TenantId, null, "stripe", key, "USD", DateTime.UtcNow), PrepareCodec);
        var submitted = await ExecuteAtomicAsync(
            new AtomicCommandIdentity("payments.provider-create.submit", key),
            new SubmitProviderPaymentCreateCommand(
                scenario.PortfolioId, scenario.AccountId, prepared.Value.PaymentAttemptId,
                "stripe", key, DateTime.UtcNow), SubmitCodec);
        submitted.Value.ProviderFenceToken.Should().NotBeNull();

        var finalized = await ExecuteAtomicAsync(
            new AtomicCommandIdentity("payments.provider-create.finalize", key),
            new FinalizeProviderPaymentCreateCommand(
                scenario.PortfolioId, scenario.AccountId, prepared.Value.PaymentAttemptId,
                "stripe", key, "pi_finalize_binding", TenantPaymentAttemptState.Submitted,
                null, DateTime.UtcNow, submitted.Value.ProviderFenceToken), FinalizeCodec);
        finalized.Value.Outcome.Should().Be(FinalizeProviderPaymentCreateOutcome.Applied);
        await using var verify = NewContext();
        var attempt = await verify.TenantPaymentAttempts.SingleAsync(row =>
            row.Id == prepared.Value.PaymentAttemptId);
        attempt.State.Should().Be(TenantPaymentAttemptState.Submitted);
        attempt.ProviderObjectId.Should().Be("pi_finalize_binding");
        attempt.ProviderFenceToken.Should().Be(submitted.Value.ProviderFenceToken);
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
            candidate.AttemptNonce.Should().Be(0);
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
    public async Task AutopayCrashAfterProviderAccept_ReconcilesExactKeyWithoutSecondCreate()
    {
        SkipIfNoDocker();
        var scenario = await SeedScenarioAsync("autopay-crash-boundary");
        var provider = new FakeAutopayProviderClient { ThrowAfterAccept = true };
        await using var scope = _services!.CreateAsyncScope();
        var service = new AutopayChargeService(
            scope.ServiceProvider.GetRequiredService<RentalCommandDbContext>(),
            Options.Create(new StripeConfig { SecretKey = "sk_test_deterministic" }),
            TimeProvider.System,
            scope.ServiceProvider.GetRequiredService<IAtomicUnitOfWork>(),
            NullLogger<AutopayChargeService>.Instance,
            provider);

        (await service.ChargeDueAsync()).Should().Be(0);
        provider.CreateCount.Should().Be(1);
        long attemptId;
        await using (var db = NewContext())
        {
            var attempt = await db.TenantPaymentAttempts.SingleAsync(row =>
                row.ChargeLedgerEntryId == scenario.ChargeId
                && row.AttemptType == TenantPaymentAttemptType.Charge);
            attempt.State.Should().Be(TenantPaymentAttemptState.Submitted);
            attempt.IdempotencyKey.Should().Be(
                AutopayChargeService.BuildIdempotencyKey(scenario.ChargeId, 0));
            attemptId = attempt.Id;
        }

        provider.ThrowAfterAccept = false;
        (await service.ChargeDueAsync()).Should().Be(0);
        provider.CreateCount.Should().Be(1, "the accepted provider call must be reconciled, never recreated");
        provider.ReconcileCount.Should().BeGreaterThanOrEqualTo(1);
        await using var verify = NewContext();
        var settled = await verify.TenantPaymentAttempts.SingleAsync(row => row.Id == attemptId);
        settled.State.Should().Be(TenantPaymentAttemptState.Succeeded);
        settled.ProviderObjectId.Should().Be("pi_autopay_crash_boundary");
    }

    [SkippableFact]
    public async Task AutopayAmbiguousAttemptOlderThan24HoursFailsAfterReconcileWithoutCreate()
    {
        SkipIfNoDocker();
        var scenario = await SeedScenarioAsync("autopay-expired-reconcile");
        var attemptKey = AutopayChargeService.BuildIdempotencyKey(scenario.ChargeId, 7);
        long attemptId;
        await using (var db = NewContext())
        {
            var attempt = Attempt(scenario, attemptKey, null, TenantPaymentAttemptState.Submitted);
            attempt.PreparedAtUtc = DateTime.UtcNow.AddHours(-25);
            attempt.SubmittedAtUtc = attempt.PreparedAtUtc;
            attempt.NextAttemptAtUtc = null;
            db.TenantPaymentAttempts.Add(attempt);
            await db.SaveChangesAsync();
            attemptId = attempt.Id;
        }

        var provider = new FakeAutopayProviderClient();
        await using var scope = _services!.CreateAsyncScope();
        var service = new AutopayChargeService(
            scope.ServiceProvider.GetRequiredService<RentalCommandDbContext>(),
            Options.Create(new StripeConfig { SecretKey = "sk_test_deterministic" }),
            TimeProvider.System,
            scope.ServiceProvider.GetRequiredService<IAtomicUnitOfWork>(),
            NullLogger<AutopayChargeService>.Instance,
            provider);

        (await service.ChargeDueAsync()).Should().Be(0);
        provider.CreateCount.Should().Be(0);
        provider.ReconcileCount.Should().Be(1);
        await using var verify = NewContext();
        (await verify.TenantPaymentAttempts.SingleAsync(row => row.Id == attemptId))
            .State.Should().Be(TenantPaymentAttemptState.Failed);
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
        long secondChargeId;

        await using (var db = NewContext())
        {
            var secondCharge = Charge(scenario, "provider-inbox-second-charge", 100m);
            db.TenantLedgerEntries.Add(secondCharge);
            await db.SaveChangesAsync();
            secondChargeId = secondCharge.Id;

            var succeededAttempt = Attempt(scenario, "provider-inbox-success", succeededObjectId,
                TenantPaymentAttemptState.Submitted);
            var failedAttempt = Attempt(scenario, "provider-inbox-failure", failedObjectId,
                TenantPaymentAttemptState.Submitted);
            failedAttempt.ChargeLedgerEntryId = secondChargeId;
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
        var membership = new WorkspaceMembership
        {
            AccessContextId = accessContext.Id,
            PortfolioId = portfolio.Id,
            Status = WorkspaceMembershipStatus.Active,
            DefaultExperience = WorkspaceExperience.Tenant,
            EffectiveFromUtc = now.AddDays(-1),
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
        };
        db.Add(membership);
        await db.SaveChangesAsync();
        var assignment = new MembershipRoleAssignment
        {
            WorkspaceMembershipId = membership.Id,
            PortfolioId = portfolio.Id,
            RoleProfileId = 2,
            Status = MembershipRoleAssignmentStatus.Active,
            ScopeKind = MembershipRoleAssignmentScopeKind.SelectedProperties,
            EffectiveFromUtc = now.AddDays(-1),
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
        };
        assignment.SelectedProperties.Add(new MembershipRoleAssignmentProperty
        {
            PropertyId = property.Id,
            PortfolioId = portfolio.Id,
        });
        db.Add(assignment);
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
        Scenario scenario, string key, string? providerObjectId, TenantPaymentAttemptState state,
        long? chargeLedgerEntryId = null)
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
            ChargeLedgerEntryId = chargeLedgerEntryId ?? scenario.ChargeId,
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

    private sealed class FakeAutopayProviderClient : IAutopayProviderClient
    {
        private readonly ConcurrentDictionary<long, AutopayProviderPayment> _accepted = [];
        private readonly ConcurrentDictionary<string, AutopayProviderPayment> _acceptedByKey = [];
        private int _createCount;
        private int _reconcileCount;

        public int CreateCount => Volatile.Read(ref _createCount);
        public int ReconcileCount => Volatile.Read(ref _reconcileCount);
        public bool ThrowAfterAccept { get; set; }

        public Task<AutopayProviderPayment> CreateAsync(
            TenantPaymentAttempt attempt, string customerId, string paymentMethodId,
            CancellationToken ct)
        {
            var payment = new AutopayProviderPayment(
                "pi_autopay_crash_boundary", "succeeded", attempt.IdempotencyKey);
            var created = _acceptedByKey.TryAdd(attempt.IdempotencyKey, payment);
            if (created)
            {
                Interlocked.Increment(ref _createCount);
                _accepted[attempt.Id] = payment;
            }
            else
            {
                payment = _acceptedByKey[attempt.IdempotencyKey];
            }

            if (created && ThrowAfterAccept)
                throw new InvalidOperationException("deterministic crash after provider accept");
            return Task.FromResult(payment);
        }

        public Task<AutopayProviderPayment?> ReconcileAsync(
            TenantPaymentAttempt attempt, CancellationToken ct)
        {
            Interlocked.Increment(ref _reconcileCount);
            _accepted.TryGetValue(attempt.Id, out var payment);
            return Task.FromResult(payment);
        }
    }

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
