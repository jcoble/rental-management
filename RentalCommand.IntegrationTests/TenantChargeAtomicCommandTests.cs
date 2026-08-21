using System.Collections.Concurrent;
using System.Data.Common;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using RentalCommand.Api.Writes;
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
using RentalCommand.Data.Authorization;
using RentalCommand.Data.Payments;
using Testcontainers.PostgreSql;
using Xunit;

namespace RentalCommand.IntegrationTests;

/// <summary>
/// Real-PostgreSQL proof for canonical manual tenant charges and typed charge reversals. The
/// foundation is still forward-only, so this suite uses EnsureCreated and installs the same
/// effective-clock function and charge-balance view that the destructive baseline will own.
/// </summary>
public sealed class TenantChargeAtomicCommandTests : IAsyncLifetime
{
    private static readonly DateTime InterceptorNow =
        new(2099, 8, 20, 12, 0, 0, DateTimeKind.Utc);
    private static readonly AtomicJsonResultCodec<TenantChargeMutationResult> ChargeCodec =
        new("tenant-account.charge.mutation.v1");
    private static readonly AtomicJsonResultCodec<RecordTenantReceiptResult> ReceiptCodec =
        new("tenant-account.receipt.record.v1");
    private static readonly AtomicJsonResultCodec<TenantLedgerMutationResult> LedgerCodec =
        new("tenant-account.ledger.mutation.v1");
    private static readonly AtomicJsonResultCodec<TenantPaymentRefundResult> RefundCodec =
        new("tenant-account.payment.refund.v1");
    private static readonly AtomicJsonResultCodec<SecurityDepositMutationResult> DepositCodec =
        new("tenant-account.deposit.mutation.v1");

    private PostgreSqlContainer? _postgres;
    private ServiceProvider? _services;
    private bool _dockerAvailable;

    public async Task InitializeAsync()
    {
        try
        {
            _postgres = new PostgreSqlBuilder()
                .WithImage("postgres:16-alpine")
                .WithDatabase("rentalcommand_tenant_charge_atomic")
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
        services.AddSingleton<TimeProvider>(new FixedTimeProvider(InterceptorNow));
        services.AddSingleton<CompanionFailureInterceptor>();
        services.AddScoped<ICurrentActor, TestActor>();
        services.AddAtomicPersistenceKernel();
        services.AddScoped<IRequestWriteExecutor, RequestWriteExecutor>();
        services.AddDbContext<RentalCommandDbContext>((provider, options) =>
            options.UseNpgsql(_postgres!.GetConnectionString())
                .UseAtomicPersistenceKernel(provider)
                .AddInterceptors(provider.GetRequiredService<CompanionFailureInterceptor>()));
        _services = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true,
        });

        await using var db = NewContext();
        await CreatePhysicalTestSchemaAsync(db);
    }

    [Fact]
    public async Task StartupChartSweep_SeedsPreexistingPortfolioAndReceiptPosts()
    {
        Skip.IfNot(_dockerAvailable, "Docker is required for the PostgreSQL accounting contract.");
        var scenario = await SeedScenarioAsync("preexisting-chart", seedChart: false);

        await using (var db = NewContext())
        {
            (await db.LedgerAccounts.CountAsync(account => account.PortfolioId == scenario.PortfolioId))
                .Should().Be(0);
            var seed = new ChartOfAccountsSeedService(db);
            (await seed.SeedAllWithLockAsync()).Should().BeGreaterThan(0);
            (await seed.SeedAllWithLockAsync()).Should().Be(0);
            (await db.LedgerAccounts.CountAsync(account => account.PortfolioId == scenario.PortfolioId))
                .Should().Be(ChartOfAccountsSeedService.DefaultChart.Count);
        }

        await using var scope = _services!.CreateAsyncScope();
        var command = Receipt(scenario, "preexisting-chart", 125m);
        var outcome = await ExecuteAtomicAsync(
            new AtomicCommandIdentity("tenant-account.receipt.record", command.DeliveryIdempotencyKey),
            command,
            ReceiptCodec);

        await using var verify = NewContext();
        outcome.Value.LedgerEntryId.Should().BeGreaterThan(0);
        var lines = await (
            from entry in verify.JournalEntries.AsNoTracking()
            from line in entry.Lines
            join account in verify.LedgerAccounts.AsNoTracking()
                on line.LedgerAccountId equals account.Id
            where entry.PortfolioId == scenario.PortfolioId
                && entry.SourceType == JournalSourceType.TenantReceipt
                && entry.SourceId == outcome.Value.LedgerEntryId
            select new { account.SystemKey, line.DebitAmount, line.CreditAmount })
            .ToListAsync();
        lines.Should().ContainSingle(line =>
            line.SystemKey == "operating-cash" && line.DebitAmount == 125m && line.CreditAmount == 0m);
        lines.Should().ContainSingle(line =>
            line.SystemKey == "tenant-accounts-receivable" && line.DebitAmount == 0m && line.CreditAmount == 125m);
    }

    private static async Task CreatePhysicalTestSchemaAsync(RentalCommandDbContext db)
    {
        // The foundation migration chain is intentionally temporary and will disappear at the
        // final baseline squash. Build the EF-owned tables directly, then install only the
        // canonical SQL objects used by charge/receipt allocation and reversal proofs.
        await db.Database.EnsureCreatedAsync();
        await db.Database.ExecuteSqlRawAsync(LeaseEffectiveClockSql.CreateEffectiveNowUtc);
        await db.Database.ExecuteSqlRawAsync(LeaseEffectiveClockSql.CreateBusinessDate);
        await db.Database.ExecuteSqlRawAsync(RelationshipAccessProjectionSql.Create);
        await db.Database.ExecuteSqlRawAsync(TenantChargeBalanceViewSql.Create);
        await db.Database.ExecuteSqlRawAsync(TenantAccountBalanceViewSql.Create);
        await db.Database.ExecuteSqlRawAsync(SecurityDepositBalanceViewSql.Create);
        foreach (var statement in TenantAccountPostgreSqlContract.CreateStatements)
            await db.Database.ExecuteSqlRawAsync(statement);
    }

    public async Task DisposeAsync()
    {
        if (_services is not null) await _services.DisposeAsync();
        if (_postgres is not null) await _postgres.DisposeAsync();
    }

    [SkippableFact]
    public async Task ManualCharge_PostsOneDebitWithAuditOutboxAndReceipt_AndReplaysOnce()
    {
        SkipIfNoDocker();
        var scenario = await SeedScenarioAsync("post-replay");
        var command = PostCharge(scenario, "post-replay", 425.50m);
        var identity = new AtomicCommandIdentity(
            "tenant-account.charge.post", command.DeliveryIdempotencyKey);

        var first = await ExecuteAtomicAsync(identity, command, ChargeCodec);
        var replay = await ExecuteAtomicAsync(identity, command, ChargeCodec);

        first.Disposition.Should().Be(AtomicCommandDisposition.Executed);
        replay.Disposition.Should().Be(AtomicCommandDisposition.Replayed);
        replay.Value.Should().Be(first.Value);

        await using var db = NewContext();
        var charge = await db.TenantLedgerEntries.SingleAsync(row =>
            row.TenantAccountId == scenario.AccountId
            && row.BusinessKey == command.BusinessKey);
        charge.EntryType.Should().Be(TenantLedgerEntryType.ManualCharge);
        charge.Direction.Should().Be(TenantLedgerDirection.Debit);
        charge.Amount.Should().Be(425.50m);
        charge.Currency.Should().Be("USD");
        (await db.AtomicAuditLogs.CountAsync(row =>
            row.CommandType == identity.CommandType
            && row.CommandIdempotencyKey == identity.IdempotencyKey)).Should().Be(2,
            "the business mutation and its journal entry are audited together");
        var trackedAuditTimestamps = await db.AtomicAuditLogs
            .Where(row => row.CommandType == identity.CommandType
                && row.CommandIdempotencyKey == identity.IdempotencyKey
                && row.EntityType == nameof(JournalEntry))
            .Select(row => row.Timestamp)
            .Distinct()
            .ToListAsync();
        trackedAuditTimestamps.Should().NotBeEmpty();
        trackedAuditTimestamps.Should().OnlyContain(timestamp => timestamp == InterceptorNow);
        (await db.AtomicAuditLogs.SingleAsync(row =>
            row.CommandType == identity.CommandType
            && row.CommandIdempotencyKey == identity.IdempotencyKey
            && row.EntityType == nameof(TenantAccount))).Timestamp.Should().NotBe(InterceptorNow,
            "the unchanged tenant-money semantic event retains the command wall clock");
        var outboxKey = OutboxIdempotency.Create("tenant-money", command.DeliveryIdempotencyKey);
        (await db.OutboxMessages.CountAsync(row =>
            row.PortfolioId == scenario.PortfolioId
            && row.IdempotencyKey == outboxKey)).Should().Be(1);
        (await db.AtomicCommandReceipts.CountAsync(row =>
            row.CommandType == identity.CommandType
            && row.IdempotencyKey == identity.IdempotencyKey)).Should().Be(1);
    }

    [SkippableTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Replay_ReauthorizesAndDeniesRevokedSessionOrStaleAccessRevision(bool staleRevision)
    {
        SkipIfNoDocker();
        var scenario = await SeedScenarioAsync(staleRevision ? "stale-replay" : "revoked-replay");
        var command = PostCharge(scenario, "replay-auth", 75m);
        var identity = new AtomicCommandIdentity(
            "tenant-account.charge.post", command.DeliveryIdempotencyKey);
        await ExecuteAtomicAsync(identity, command, ChargeCodec);

        await using (var mutate = NewContext())
        {
            if (staleRevision)
            {
                var context = await mutate.WorkspaceAccessContexts.SingleAsync(row =>
                    row.Id == scenario.AccessContextId);
                context.AdvanceRevision(scenario.AccessRevision);
            }
            else
            {
                var session = await mutate.AuthSessions.SingleAsync(row => row.Id == scenario.SessionId);
                session.Status = AuthSessionStatus.Revoked;
                session.RevokedAtUtc = DateTime.UtcNow;
                session.RevocationReason = "integration replay proof";
            }
            await mutate.SaveChangesAsync();
        }

        await FluentActions.Invoking(() => ExecuteAtomicAsync(identity, command, ChargeCodec))
            .Should().ThrowAsync<UnauthorizedAccessException>();

        await using var verify = NewContext();
        (await verify.TenantLedgerEntries.CountAsync(row =>
            row.TenantAccountId == scenario.AccountId
            && row.BusinessKey == command.BusinessKey)).Should().Be(1);
    }

    [SkippableFact]
    public async Task PaymentReceiptAndRefund_RejectForgedNonPaymentCapabilityBeforeAnyWrite()
    {
        SkipIfNoDocker();
        var scenario = await SeedScenarioAsync("payment-capability");
        var receipt = Receipt(scenario, "wrong-capability", 25m) with
        {
            RequiredCapability = CapabilityKeys.MoneyChargesManage,
        };
        var refund = RefundPayment(scenario, long.MaxValue, "wrong-capability") with
        {
            RequiredCapability = CapabilityKeys.MoneyChargesManage,
        };

        await FluentActions.Invoking(() => ExecuteAtomicAsync(
                new AtomicCommandIdentity(
                    "tenant-account.receipt.record", receipt.DeliveryIdempotencyKey),
                receipt, ReceiptCodec))
            .Should().ThrowAsync<ArgumentException>()
            .WithMessage("*payment-management capability*");
        await FluentActions.Invoking(() => ExecuteAtomicAsync(
                new AtomicCommandIdentity(
                    "tenant-account.payment.refund", refund.DeliveryIdempotencyKey),
                refund, RefundCodec))
            .Should().ThrowAsync<ArgumentException>()
            .WithMessage("*payment-management capability*");

        await using var verify = NewContext();
        (await verify.TenantPaymentAttempts.CountAsync(row =>
            row.TenantAccountId == scenario.AccountId)).Should().Be(0);
        (await verify.TenantLedgerEntries.CountAsync(row =>
            row.TenantAccountId == scenario.AccountId)).Should().Be(0);
        (await verify.AtomicCommandReceipts.CountAsync(row =>
            row.IdempotencyKey == receipt.DeliveryIdempotencyKey
            || row.IdempotencyKey == refund.DeliveryIdempotencyKey)).Should().Be(0);
    }

    [SkippableFact]
    public async Task Receipt_RejectsInvalidTargetBeforeAttemptReceiptAllocationOrAtomicReceipt()
    {
        SkipIfNoDocker();
        var scenario = await SeedScenarioAsync("receipt-invalid-target");
        var crossAccountCharge = await SeedLedgerEntryAsync(
            scenario, scenario.OtherAccountId, TenantLedgerEntryType.ManualCharge,
            TenantLedgerDirection.Debit, "receipt-cross-account-target", 40m,
            DateOnly.FromDateTime(DateTime.UtcNow));
        var command = Receipt(scenario, "invalid-target", 25m, crossAccountCharge);
        var identity = new AtomicCommandIdentity(
            "tenant-account.receipt.record", command.DeliveryIdempotencyKey);

        await FluentActions.Invoking(() => ExecuteAtomicAsync(identity, command, ReceiptCodec))
            .Should().ThrowAsync<ArgumentException>()
            .WithMessage("*open debit*");

        await using var verify = NewContext();
        (await verify.TenantPaymentAttempts.CountAsync(row =>
            row.TenantAccountId == scenario.AccountId)).Should().Be(0);
        (await verify.TenantLedgerEntries.CountAsync(row =>
            row.TenantAccountId == scenario.AccountId
            && row.EntryType == TenantLedgerEntryType.PaymentReceipt)).Should().Be(0);
        (await verify.TenantLedgerAllocations.CountAsync(row =>
            row.TenantAccountId == scenario.AccountId)).Should().Be(0);
        (await verify.AtomicCommandReceipts.CountAsync(row =>
            row.CommandType == identity.CommandType
            && row.IdempotencyKey == identity.IdempotencyKey)).Should().Be(0);
    }

    [SkippableFact]
    public async Task Receipt_TargetsSelectedChargeFirstThenSpillsRemainingToOldestOpenCharges_AndReplaysUnchanged()
    {
        SkipIfNoDocker();
        var scenario = await SeedScenarioAsync("receipt-exact-target");
        var olderChargeCommand = PostCharge(scenario, "older-open-charge", 50m);
        var olderCharge = await ExecuteAtomicAsync(
            new AtomicCommandIdentity(
                "tenant-account.charge.post", olderChargeCommand.DeliveryIdempotencyKey),
            olderChargeCommand,
            ChargeCodec);
        var remainingOlderChargeCommand = PostCharge(scenario, "remaining-older-open-charge", 25m);
        var remainingOlderCharge = await ExecuteAtomicAsync(
            new AtomicCommandIdentity(
                "tenant-account.charge.post", remainingOlderChargeCommand.DeliveryIdempotencyKey),
            remainingOlderChargeCommand,
            ChargeCodec);
        var targetChargeCommand = PostCharge(scenario, "selected-open-charge", 550m);
        var targetCharge = await ExecuteAtomicAsync(
            new AtomicCommandIdentity(
                "tenant-account.charge.post", targetChargeCommand.DeliveryIdempotencyKey),
            targetChargeCommand,
            ChargeCodec);
        var receiptCommand = Receipt(
            scenario, "exact-target-surplus", 600m, targetCharge.Value.LedgerEntryId);
        var identity = new AtomicCommandIdentity(
            "tenant-account.receipt.record", receiptCommand.DeliveryIdempotencyKey);

        var first = await ExecuteAtomicAsync(identity, receiptCommand, ReceiptCodec);
        var replay = await ExecuteAtomicAsync(identity, receiptCommand, ReceiptCodec);

        first.Disposition.Should().Be(AtomicCommandDisposition.Executed);
        replay.Disposition.Should().Be(AtomicCommandDisposition.Replayed);
        replay.Value.Should().Be(first.Value);
        first.Value.AllocatedAmount.Should().Be(600m);
        first.Value.AllocationCount.Should().Be(2);

        await using var verify = NewContext();
        var paymentAttempt = await verify.TenantPaymentAttempts.SingleAsync(row =>
            row.Id == first.Value.PaymentAttemptId);
        paymentAttempt.AttemptType.Should().Be(TenantPaymentAttemptType.Charge);
        paymentAttempt.ChargeLedgerEntryId.Should().Be(targetCharge.Value.LedgerEntryId);
        var allocations = await verify.TenantLedgerAllocations
            .Where(row => row.CreditEntryId == first.Value.LedgerEntryId)
            .OrderBy(row => row.DebitEntryId == targetCharge.Value.LedgerEntryId ? 0 : 1)
            .ToListAsync();
        allocations.Should().HaveCount(2);
        allocations[0].DebitEntryId.Should().Be(targetCharge.Value.LedgerEntryId);
        allocations[0].Amount.Should().Be(550m);
        allocations[1].DebitEntryId.Should().Be(olderCharge.Value.LedgerEntryId);
        allocations[1].Amount.Should().Be(50m);
        allocations.Should().NotContain(row => row.DebitEntryId == remainingOlderCharge.Value.LedgerEntryId);
        (await verify.TenantChargeBalanceProjections.SingleAsync(row =>
            row.TenantLedgerEntryId == olderCharge.Value.LedgerEntryId)).OpenAmount.Should().Be(0m);
        (await verify.TenantChargeBalanceProjections.SingleAsync(row =>
            row.TenantLedgerEntryId == remainingOlderCharge.Value.LedgerEntryId)).OpenAmount.Should().Be(25m);
        (await verify.TenantChargeBalanceProjections.SingleAsync(row =>
            row.TenantLedgerEntryId == targetCharge.Value.LedgerEntryId)).OpenAmount.Should().Be(0m);
    }

    [SkippableFact]
    public async Task Receipt_WithNoSelectedCharge_PersistsDeliberateUnappliedIntent()
    {
        SkipIfNoDocker();
        var scenario = await SeedScenarioAsync("receipt-deliberately-unapplied");
        var command = Receipt(scenario, "deliberately-unapplied", 45m);
        var identity = new AtomicCommandIdentity(
            "tenant-account.receipt.record", command.DeliveryIdempotencyKey);

        var first = await ExecuteAtomicAsync(identity, command, ReceiptCodec);
        var replay = await ExecuteAtomicAsync(identity, command, ReceiptCodec);

        first.Value.AllocatedAmount.Should().Be(0m);
        first.Value.AllocationCount.Should().Be(0);
        replay.Disposition.Should().Be(AtomicCommandDisposition.Replayed);
        replay.Value.Should().Be(first.Value);

        await using var verify = NewContext();
        var paymentAttempt = await verify.TenantPaymentAttempts.SingleAsync(row =>
            row.Id == first.Value.PaymentAttemptId);
        paymentAttempt.AttemptType.Should().Be(TenantPaymentAttemptType.UnappliedReceipt);
        paymentAttempt.ChargeLedgerEntryId.Should().BeNull();
        (await verify.TenantLedgerAllocations.AnyAsync(row =>
            row.CreditEntryId == first.Value.LedgerEntryId)).Should().BeFalse();
        (await verify.AtomicCommandReceipts.CountAsync(row =>
            row.CommandType == identity.CommandType
            && row.IdempotencyKey == identity.IdempotencyKey)).Should().Be(1);
    }

    [SkippableFact]
    public async Task Receipt_PartiallyAllocatesOnlyTheTargetCharge()
    {
        SkipIfNoDocker();
        var scenario = await SeedScenarioAsync("receipt-partial-target");
        var olderChargeCommand = PostCharge(scenario, "partial-older-open-charge", 60m);
        var olderCharge = await ExecuteAtomicAsync(
            new AtomicCommandIdentity(
                "tenant-account.charge.post", olderChargeCommand.DeliveryIdempotencyKey),
            olderChargeCommand,
            ChargeCodec);
        var targetChargeCommand = PostCharge(scenario, "partial-selected-open-charge", 150m);
        var targetCharge = await ExecuteAtomicAsync(
            new AtomicCommandIdentity(
                "tenant-account.charge.post", targetChargeCommand.DeliveryIdempotencyKey),
            targetChargeCommand,
            ChargeCodec);
        var receiptCommand = Receipt(
            scenario, "partial-target", 75m, targetCharge.Value.LedgerEntryId);

        var receipt = await ExecuteAtomicAsync(
            new AtomicCommandIdentity(
                "tenant-account.receipt.record", receiptCommand.DeliveryIdempotencyKey),
            receiptCommand,
            ReceiptCodec);

        receipt.Value.AllocatedAmount.Should().Be(75m);
        receipt.Value.AllocationCount.Should().Be(1);
        await using var verify = NewContext();
        var allocations = await verify.TenantLedgerAllocations
            .Where(row => row.CreditEntryId == receipt.Value.LedgerEntryId)
            .ToListAsync();
        allocations.Should().ContainSingle();
        allocations.Single().DebitEntryId.Should().Be(targetCharge.Value.LedgerEntryId);
        allocations.Single().Amount.Should().Be(75m);
        (await verify.TenantChargeBalanceProjections.SingleAsync(row =>
            row.TenantLedgerEntryId == olderCharge.Value.LedgerEntryId)).OpenAmount.Should().Be(60m);
        (await verify.TenantChargeBalanceProjections.SingleAsync(row =>
            row.TenantLedgerEntryId == targetCharge.Value.LedgerEntryId)).OpenAmount.Should().Be(75m);
    }

    [SkippableFact]
    public async Task Reversal_RejectsMissingCrossAccountAndNonChargeTargets()
    {
        SkipIfNoDocker();
        var scenario = await SeedScenarioAsync("invalid-reversals");
        var nonCharge = await SeedLedgerEntryAsync(
            scenario, scenario.AccountId, TenantLedgerEntryType.PaymentReceipt,
            TenantLedgerDirection.Credit, "non-charge-target", 40m);
        var crossAccount = await SeedLedgerEntryAsync(
            scenario, scenario.OtherAccountId, TenantLedgerEntryType.ManualCharge,
            TenantLedgerDirection.Debit, "cross-account-target", 40m, DateOnly.FromDateTime(DateTime.UtcNow));

        foreach (var (entryId, suffix) in new[]
        {
            (long.MaxValue, "missing"),
            (crossAccount, "cross-account"),
            (nonCharge, "non-charge"),
        })
        {
            var command = ReverseCharge(scenario, entryId, suffix);
            var identity = new AtomicCommandIdentity(
                "tenant-account.charge.reverse", command.DeliveryIdempotencyKey);
            await FluentActions.Invoking(() => ExecuteAtomicAsync(identity, command, ChargeCodec))
                .Should().ThrowAsync<UnauthorizedAccessException>();
        }

        await using var verify = NewContext();
        (await verify.TenantLedgerEntries.CountAsync(row =>
            row.PortfolioId == scenario.PortfolioId
            && row.EntryType == TenantLedgerEntryType.Reversal)).Should().Be(0);
    }

    [SkippableFact]
    public async Task AllocatedChargeReversal_CopiesTermsAndAppendsExactNegativeAllocationsAtomically()
    {
        SkipIfNoDocker();
        var scenario = await SeedScenarioAsync("allocated-reversal");
        var chargeCommand = PostCharge(scenario, "allocated-charge", 300m);
        var charge = await ExecuteAtomicAsync(
            new AtomicCommandIdentity("tenant-account.charge.post", chargeCommand.DeliveryIdempotencyKey),
            chargeCommand,
            ChargeCodec);
        var receiptCommand = Receipt(scenario, "allocated-receipt", 125m, charge.Value.LedgerEntryId);
        await ExecuteAtomicAsync(
            new AtomicCommandIdentity("tenant-account.receipt.record", receiptCommand.DeliveryIdempotencyKey),
            receiptCommand,
            ReceiptCodec);

        var reversalCommand = ReverseCharge(scenario, charge.Value.LedgerEntryId, "allocated");
        var reversalIdentity = new AtomicCommandIdentity(
            "tenant-account.charge.reverse", reversalCommand.DeliveryIdempotencyKey);
        Failures.Clear();
        var reversal = await ExecuteAtomicAsync(reversalIdentity, reversalCommand, ChargeCodec);

        reversal.Disposition.Should().Be(AtomicCommandDisposition.Executed);
        await using var db = NewContext();
        var entries = await db.TenantLedgerEntries
            .Where(row => row.Id == charge.Value.LedgerEntryId
                || row.Id == reversal.Value.LedgerEntryId)
            .OrderBy(row => row.Id)
            .ToListAsync();
        var original = entries.Single(row => row.Id == charge.Value.LedgerEntryId);
        var reversingEntry = entries.Single(row => row.Id == reversal.Value.LedgerEntryId);
        reversingEntry.EntryType.Should().Be(TenantLedgerEntryType.Reversal);
        reversingEntry.Direction.Should().Be(TenantLedgerDirection.Credit);
        reversingEntry.Amount.Should().Be(original.Amount);
        reversingEntry.Currency.Should().Be(original.Currency);
        reversingEntry.ReversesEntryId.Should().Be(original.Id);

        var allocations = await db.TenantLedgerAllocations
            .Where(row => row.TenantAccountId == scenario.AccountId
                && row.DebitEntryId == original.Id)
            .OrderBy(row => row.Id)
            .ToListAsync();
        allocations.Should().HaveCount(2);
        var applied = allocations.Single(row => row.ReversesAllocationId == null);
        var reversed = allocations.Single(row => row.ReversesAllocationId != null);
        reversed.ReversesAllocationId.Should().Be(applied.Id);
        reversed.Amount.Should().Be(-applied.Amount);
        Failures.Commands.Should().ContainSingle(sql =>
            sql.Contains("INSERT INTO \"TenantLedgerAllocations\"", StringComparison.Ordinal)
            && sql.Contains("-source.\"Amount\"", StringComparison.Ordinal)
            && sql.Contains("NOT EXISTS", StringComparison.Ordinal));
        reversal.Value.Amount.Should().Be(original.Amount);
        (await db.AtomicCommandReceipts.CountAsync(row =>
            row.CommandType == reversalIdentity.CommandType
            && row.IdempotencyKey == reversalIdentity.IdempotencyKey)).Should().Be(1);
    }

    [SkippableFact]
    public async Task AllocatedChargeReversal_FinalCompanionFailureRollsBackEveryWrite()
    {
        SkipIfNoDocker();
        var scenario = await SeedScenarioAsync("reversal-rollback");
        var chargeCommand = PostCharge(scenario, "rollback-charge", 220m);
        var charge = await ExecuteAtomicAsync(
            new AtomicCommandIdentity("tenant-account.charge.post", chargeCommand.DeliveryIdempotencyKey),
            chargeCommand,
            ChargeCodec);
        var receiptCommand = Receipt(scenario, "rollback-receipt", 80m, charge.Value.LedgerEntryId);
        await ExecuteAtomicAsync(
            new AtomicCommandIdentity("tenant-account.receipt.record", receiptCommand.DeliveryIdempotencyKey),
            receiptCommand,
            ReceiptCodec);
        var reversalCommand = ReverseCharge(scenario, charge.Value.LedgerEntryId, "rollback");
        var identity = new AtomicCommandIdentity(
            "tenant-account.charge.reverse", reversalCommand.DeliveryIdempotencyKey);

        Failures.FailAtomicAudit = true;
        try
        {
            var failure = await FluentActions
                .Invoking(() => ExecuteAtomicAsync(identity, reversalCommand, ChargeCodec))
                .Should().ThrowAsync<DbUpdateException>();
            failure.WithInnerException<InvalidOperationException>()
                .WithMessage("injected tenant-charge audit failure");
        }
        finally
        {
            Failures.FailAtomicAudit = false;
        }

        await using var db = NewContext();
        (await db.TenantLedgerEntries.CountAsync(row =>
            row.TenantAccountId == scenario.AccountId
            && row.ReversesEntryId == charge.Value.LedgerEntryId)).Should().Be(0);
        var allocations = await db.TenantLedgerAllocations
            .Where(row => row.TenantAccountId == scenario.AccountId
                && row.DebitEntryId == charge.Value.LedgerEntryId)
            .ToListAsync();
        allocations.Should().ContainSingle(row => row.ReversesAllocationId == null && row.Amount == 80m);
        (await db.AtomicCommandReceipts.CountAsync(row =>
            row.CommandType == identity.CommandType
            && row.IdempotencyKey == identity.IdempotencyKey)).Should().Be(0);
    }

    [SkippableFact]
    public async Task Credit_ConcurrentDuplicateReplaysOnce_AllocatesOldest_AndChangedPayloadConflicts()
    {
        SkipIfNoDocker();
        var scenario = await SeedScenarioAsync("credit-concurrent");
        var chargeCommand = PostCharge(scenario, "credit-open-charge", 100m);
        await ExecuteAtomicAsync(
            new AtomicCommandIdentity("tenant-account.charge.post", chargeCommand.DeliveryIdempotencyKey),
            chargeCommand,
            ChargeCodec);
        var command = Credit(scenario, "concurrent", 60m);
        var identity = new AtomicCommandIdentity(
            "tenant-account.credit.post", command.DeliveryIdempotencyKey);

        var outcomes = await Task.WhenAll(
            ExecuteAtomicAsync(identity, command, LedgerCodec),
            ExecuteAtomicAsync(identity, command, LedgerCodec));

        outcomes.Select(result => result.Disposition).Should()
            .BeEquivalentTo([AtomicCommandDisposition.Executed, AtomicCommandDisposition.Replayed]);
        outcomes[0].Value.Should().Be(outcomes[1].Value);
        await FluentActions.Invoking(() => ExecuteAtomicAsync(
                identity,
                command with { Amount = 61m },
                LedgerCodec))
            .Should().ThrowAsync<AtomicIdempotencyConflictException>();

        await using (var revoke = NewContext())
        {
            var session = await revoke.AuthSessions.SingleAsync(row => row.Id == scenario.SessionId);
            session.Status = AuthSessionStatus.Revoked;
            session.RevokedAtUtc = DateTime.UtcNow;
            session.RevocationReason = "canonical ledger replay proof";
            await revoke.SaveChangesAsync();
        }
        await FluentActions.Invoking(() => ExecuteAtomicAsync(identity, command, LedgerCodec))
            .Should().ThrowAsync<UnauthorizedAccessException>();

        await using var db = NewContext();
        var credit = await db.TenantLedgerEntries.SingleAsync(row =>
            row.TenantAccountId == scenario.AccountId
            && row.BusinessKey == command.BusinessKey);
        credit.EntryType.Should().Be(TenantLedgerEntryType.Credit);
        credit.Direction.Should().Be(TenantLedgerDirection.Credit);
        credit.Amount.Should().Be(60m);
        var allocation = await db.TenantLedgerAllocations.SingleAsync(row =>
            row.TenantAccountId == scenario.AccountId
            && row.CreditEntryId == credit.Id);
        allocation.Amount.Should().Be(60m);
        outcomes[0].Value.AllocatedAmount.Should().Be(60m);
        outcomes[0].Value.AllocationCount.Should().Be(1);
        (await db.AtomicCommandReceipts.CountAsync(row =>
            row.CommandType == identity.CommandType
            && row.IdempotencyKey == identity.IdempotencyKey)).Should().Be(1);
    }

    [SkippableFact]
    public async Task Adjustment_RequiresItsExplicitDirection_AndFinalCompanionFailureRollsBackEveryWrite()
    {
        SkipIfNoDocker();
        var scenario = await SeedScenarioAsync("adjustment-rollback");
        var debit = Adjustment(scenario, "debit", TenantLedgerDirection.Debit, 35m);
        var debitIdentity = new AtomicCommandIdentity(
            "tenant-account.adjustment.post", debit.DeliveryIdempotencyKey);
        var posted = await ExecuteAtomicAsync(debitIdentity, debit, LedgerCodec);

        posted.Value.EntryType.Should().Be(TenantLedgerEntryType.Adjustment);
        posted.Value.Direction.Should().Be(TenantLedgerDirection.Debit);
        await using (var verifyDebit = NewContext())
        {
            var entry = await verifyDebit.TenantLedgerEntries.SingleAsync(row =>
                row.Id == posted.Value.LedgerEntryId);
            entry.EntryType.Should().Be(TenantLedgerEntryType.Adjustment);
            entry.Direction.Should().Be(TenantLedgerDirection.Debit);
            entry.DueOn.Should().BeNull();
            entry.ProviderPaymentAttemptId.Should().BeNull();
            (await verifyDebit.TenantLedgerAllocations.CountAsync(row =>
                row.DebitEntryId == entry.Id || row.CreditEntryId == entry.Id)).Should().Be(0);
        }

        var credit = Adjustment(scenario, "credit-rollback", TenantLedgerDirection.Credit, 17m);
        var creditIdentity = new AtomicCommandIdentity(
            "tenant-account.adjustment.post", credit.DeliveryIdempotencyKey);
        Failures.FailAtomicAudit = true;
        try
        {
            var failure = await FluentActions
                .Invoking(() => ExecuteAtomicAsync(creditIdentity, credit, LedgerCodec))
                .Should().ThrowAsync<DbUpdateException>();
            failure.WithInnerException<InvalidOperationException>()
                .WithMessage("injected tenant-charge audit failure");
        }
        finally
        {
            Failures.FailAtomicAudit = false;
        }

        await using var verify = NewContext();
        (await verify.TenantLedgerEntries.CountAsync(row =>
            row.TenantAccountId == scenario.AccountId
            && row.BusinessKey == credit.BusinessKey)).Should().Be(0);
        (await verify.AtomicCommandReceipts.CountAsync(row =>
            row.CommandType == creditIdentity.CommandType
            && row.IdempotencyKey == creditIdentity.IdempotencyKey)).Should().Be(0);
        (await verify.OutboxMessages.CountAsync(row =>
            row.IdempotencyKey == OutboxIdempotency.Create(
                "tenant-money", credit.DeliveryIdempotencyKey))).Should().Be(0);
    }

    [SkippableFact]
    public async Task GeneralReversal_RejectsCrossAccountTarget()
    {
        SkipIfNoDocker();
        var scenario = await SeedScenarioAsync("general-cross-account");
        var crossAccount = await SeedLedgerEntryAsync(
            scenario,
            scenario.OtherAccountId,
            TenantLedgerEntryType.Adjustment,
            TenantLedgerDirection.Debit,
            "cross-account-adjustment",
            45m);
        var command = ReverseLedger(scenario, crossAccount, "cross-account");
        var identity = new AtomicCommandIdentity(
            "tenant-account.ledger.reverse", command.DeliveryIdempotencyKey);

        await FluentActions.Invoking(() => ExecuteAtomicAsync(identity, command, LedgerCodec))
            .Should().ThrowAsync<UnauthorizedAccessException>();

        await using var verify = NewContext();
        (await verify.TenantLedgerEntries.CountAsync(row =>
            row.TenantAccountId == scenario.AccountId
            && row.EntryType == TenantLedgerEntryType.Reversal)).Should().Be(0);
        (await verify.AtomicCommandReceipts.CountAsync(row =>
            row.CommandType == identity.CommandType
            && row.IdempotencyKey == identity.IdempotencyKey)).Should().Be(0);
    }

    [SkippableFact]
    public async Task GeneralReversal_RejectsSecurityDepositLinkedCreditAndReplaysTheRejection()
    {
        SkipIfNoDocker();
        var scenario = await SeedScenarioAsync("deposit-linked-credit-reversal");
        var deduction = await CreateDepositDeductionAsync(
            scenario, "deposit-linked-credit-reversal");

        await AssertDepositGenericReversalRejectedAsync(
            scenario,
            deduction.Deduction.TenantLedgerEntryId!.Value,
            "deposit-linked-credit");
    }

    [SkippableFact]
    public async Task GeneralReversal_RejectsSecurityDepositPairedManualChargeAndReplaysTheRejection()
    {
        SkipIfNoDocker();
        var scenario = await SeedScenarioAsync("deposit-paired-charge-reversal");
        var deduction = await CreateDepositDeductionAsync(
            scenario, "deposit-paired-charge-reversal");

        await AssertDepositGenericReversalRejectedAsync(
            scenario,
            deduction.ChargeEntryId,
            "deposit-paired-charge");
    }

    [SkippableFact]
    public async Task GeneralReversal_StillAllowsOrdinaryAdjustment()
    {
        SkipIfNoDocker();
        var scenario = await SeedScenarioAsync("ordinary-adjustment-reversal");
        var adjustmentCommand = Adjustment(
            scenario, "ordinary-adjustment-reversal", TenantLedgerDirection.Debit, 45m);
        var adjustment = await ExecuteAtomicAsync(
            new AtomicCommandIdentity(
                "tenant-account.adjustment.post", adjustmentCommand.DeliveryIdempotencyKey),
            adjustmentCommand,
            LedgerCodec);
        var reversalCommand = ReverseLedger(
            scenario, adjustment.Value.LedgerEntryId, "ordinary-adjustment");

        var reversal = await ExecuteAtomicAsync(
            new AtomicCommandIdentity(
                "tenant-account.ledger.reverse", reversalCommand.DeliveryIdempotencyKey),
            reversalCommand,
            LedgerCodec);

        reversal.Value.Applied.Should().BeTrue();
        reversal.Value.ReversesEntryId.Should().Be(adjustment.Value.LedgerEntryId);
        await using var verify = NewContext();
        var persisted = await verify.TenantLedgerEntries.SingleAsync(row =>
            row.Id == reversal.Value.LedgerEntryId);
        AssertExactReversal(
            await verify.TenantLedgerEntries.SingleAsync(row =>
                row.Id == adjustment.Value.LedgerEntryId),
            persisted,
            TenantLedgerDirection.Credit);
    }

    [SkippableFact]
    public async Task GeneralReversal_IsExact_AndCompensatesAllocationsForDebitAndCreditTargets()
    {
        SkipIfNoDocker();
        var scenario = await SeedScenarioAsync("general-exact");

        var firstChargeCommand = PostCharge(scenario, "general-debit", 100m);
        var firstCharge = await ExecuteAtomicAsync(
            new AtomicCommandIdentity(
                "tenant-account.charge.post", firstChargeCommand.DeliveryIdempotencyKey),
            firstChargeCommand,
            ChargeCodec);
        var firstCreditCommand = Credit(scenario, "general-debit-allocation", 40m);
        await ExecuteAtomicAsync(
            new AtomicCommandIdentity(
                "tenant-account.credit.post", firstCreditCommand.DeliveryIdempotencyKey),
            firstCreditCommand,
            LedgerCodec);
        var reverseDebitCommand = ReverseLedger(
            scenario, firstCharge.Value.LedgerEntryId, "general-debit");
        var reverseDebit = await ExecuteAtomicAsync(
            new AtomicCommandIdentity(
                "tenant-account.ledger.reverse", reverseDebitCommand.DeliveryIdempotencyKey),
            reverseDebitCommand,
            LedgerCodec);

        var secondChargeCommand = PostCharge(scenario, "general-credit", 90m);
        await ExecuteAtomicAsync(
            new AtomicCommandIdentity(
                "tenant-account.charge.post", secondChargeCommand.DeliveryIdempotencyKey),
            secondChargeCommand,
            ChargeCodec);
        var secondCreditCommand = Credit(scenario, "general-credit-allocation", 30m);
        var secondCredit = await ExecuteAtomicAsync(
            new AtomicCommandIdentity(
                "tenant-account.credit.post", secondCreditCommand.DeliveryIdempotencyKey),
            secondCreditCommand,
            LedgerCodec);
        var reverseCreditCommand = ReverseLedger(
            scenario, secondCredit.Value.LedgerEntryId, "general-credit");
        var reverseCredit = await ExecuteAtomicAsync(
            new AtomicCommandIdentity(
                "tenant-account.ledger.reverse", reverseCreditCommand.DeliveryIdempotencyKey),
            reverseCreditCommand,
            LedgerCodec);

        await using var db = NewContext();
        var entries = await db.TenantLedgerEntries
            .Where(row => row.Id == firstCharge.Value.LedgerEntryId
                || row.Id == reverseDebit.Value.LedgerEntryId
                || row.Id == secondCredit.Value.LedgerEntryId
                || row.Id == reverseCredit.Value.LedgerEntryId)
            .ToListAsync();
        var debitTarget = entries.Single(row => row.Id == firstCharge.Value.LedgerEntryId);
        var debitReversal = entries.Single(row => row.Id == reverseDebit.Value.LedgerEntryId);
        var creditTarget = entries.Single(row => row.Id == secondCredit.Value.LedgerEntryId);
        var creditReversal = entries.Single(row => row.Id == reverseCredit.Value.LedgerEntryId);
        AssertExactReversal(debitTarget, debitReversal, TenantLedgerDirection.Credit);
        AssertExactReversal(creditTarget, creditReversal, TenantLedgerDirection.Debit);

        var compensatedAllocations = await db.TenantLedgerAllocations
            .Where(row => row.TenantAccountId == scenario.AccountId
                && (row.DebitEntryId == debitTarget.Id
                    || row.CreditEntryId == creditTarget.Id))
            .OrderBy(row => row.Id)
            .ToListAsync();
        AssertExactAllocationCompensation(
            compensatedAllocations.Where(row => row.DebitEntryId == debitTarget.Id).ToList());
        AssertExactAllocationCompensation(
            compensatedAllocations.Where(row => row.CreditEntryId == creditTarget.Id).ToList());
        Failures.Commands.Should().Contain(sql =>
            sql.Contains("source.\"DebitEntryId\"", StringComparison.Ordinal)
            && sql.Contains("source.\"CreditEntryId\"", StringComparison.Ordinal)
            && sql.Contains("-source.\"Amount\"", StringComparison.Ordinal)
            && sql.Contains("NOT EXISTS", StringComparison.Ordinal));
    }

    [SkippableFact]
    public async Task ChargeCapabilityCannotReverseReceipt_ButPaymentRefundReturnsItExactly()
    {
        SkipIfNoDocker();
        var scenario = await SeedScenarioAsync("payment-correction-boundary");
        var chargeCommand = PostCharge(scenario, "payment-correction-charge", 100m);
        var charge = await ExecuteAtomicAsync(
            new AtomicCommandIdentity(
                "tenant-account.charge.post", chargeCommand.DeliveryIdempotencyKey),
            chargeCommand,
            ChargeCodec);
        var receiptCommand = Receipt(
            scenario, "payment-correction-receipt", 100m, charge.Value.LedgerEntryId);
        var receipt = await ExecuteAtomicAsync(
            new AtomicCommandIdentity(
                "tenant-account.receipt.record", receiptCommand.DeliveryIdempotencyKey),
            receiptCommand,
            ReceiptCodec);

        var chargeCapabilityReversal = ReverseLedger(
            scenario, receipt.Value.LedgerEntryId, "forbidden-payment-reversal");
        await FluentActions.Invoking(() => ExecuteAtomicAsync(
                new AtomicCommandIdentity(
                    "tenant-account.ledger.reverse",
                    chargeCapabilityReversal.DeliveryIdempotencyKey),
                chargeCapabilityReversal,
                LedgerCodec))
            .Should().ThrowAsync<UnauthorizedAccessException>();

        var correction = RefundPayment(
            scenario, receipt.Value.LedgerEntryId, "manual-receipt-correction");
        var correctionIdentity = new AtomicCommandIdentity(
            "tenant-account.payment.refund", correction.DeliveryIdempotencyKey);
        Failures.FailAtomicAudit = true;
        try
        {
            await FluentActions.Invoking(() =>
                    ExecuteAtomicAsync(correctionIdentity, correction, RefundCodec))
                .Should().ThrowAsync<DbUpdateException>();
        }
        finally
        {
            Failures.FailAtomicAudit = false;
        }
        await using (var rolledBack = NewContext())
        {
            (await rolledBack.TenantPaymentAttempts.CountAsync(row =>
                row.AttemptType == TenantPaymentAttemptType.Refund)).Should().Be(0);
            (await rolledBack.TenantLedgerEntries.CountAsync(row =>
                row.EntryType == TenantLedgerEntryType.Refund)).Should().Be(0);
            (await rolledBack.TenantLedgerAllocations.CountAsync(row =>
                row.CreditEntryId == receipt.Value.LedgerEntryId)).Should().Be(1);
            (await rolledBack.AtomicCommandReceipts.CountAsync(row =>
                row.CommandType == correctionIdentity.CommandType
                && row.IdempotencyKey == correctionIdentity.IdempotencyKey)).Should().Be(0);
        }
        var corrected = await ExecuteAtomicAsync(
            correctionIdentity,
            correction,
            RefundCodec);
        var correctionReplay = await ExecuteAtomicAsync(
            correctionIdentity,
            correction,
            RefundCodec);

        corrected.Value.Outcome.Should().Be(TenantPaymentRefundOutcome.Refunded);
        correctionReplay.Disposition.Should().Be(AtomicCommandDisposition.Replayed);
        correctionReplay.Value.Should().Be(corrected.Value);
        corrected.Value.CompensatedAllocationAmount.Should().Be(100m);
        var refundCapabilityReversal = ReverseLedger(
            scenario, corrected.Value.RefundEntryId!.Value, "forbidden-refund-reversal");
        await FluentActions.Invoking(() => ExecuteAtomicAsync(
                new AtomicCommandIdentity(
                    "tenant-account.ledger.reverse",
                    refundCapabilityReversal.DeliveryIdempotencyKey),
                refundCapabilityReversal,
                LedgerCodec))
            .Should().ThrowAsync<UnauthorizedAccessException>();

        await using var db = NewContext();
        var refund = await db.TenantLedgerEntries.SingleAsync(row =>
            row.Id == corrected.Value.RefundEntryId);
        refund.EntryType.Should().Be(TenantLedgerEntryType.Refund);
        refund.Direction.Should().Be(TenantLedgerDirection.Debit);
        refund.Amount.Should().Be(100m);
        refund.ReversesEntryId.Should().BeNull();
        var refundAttempt = await db.TenantPaymentAttempts.SingleAsync(row =>
            row.Id == corrected.Value.ProviderPaymentAttemptId);
        refundAttempt.AttemptType.Should().Be(TenantPaymentAttemptType.Refund);
        refundAttempt.State.Should().Be(TenantPaymentAttemptState.Succeeded);
        refundAttempt.RefundsPaymentAttemptId.Should().Be(receipt.Value.PaymentAttemptId);
        var allocations = await db.TenantLedgerAllocations
            .Where(row => row.CreditEntryId == receipt.Value.LedgerEntryId)
            .OrderBy(row => row.Id)
            .ToListAsync();
        AssertExactAllocationCompensation(allocations);
        var accountBalance = await db.Set<TenantAccountBalanceProjection>()
            .SingleAsync(row => row.TenantAccountId == scenario.AccountId);
        accountBalance.ReceivableBalance.Should().Be(100m);
        accountBalance.UnappliedCredit.Should().Be(0m);
        var chargeBalance = await db.Set<TenantChargeBalanceProjection>()
            .SingleAsync(row => row.TenantAccountId == scenario.AccountId);
        chargeBalance.OpenAmount.Should().Be(100m);
    }

    [SkippableFact]
    public async Task UnappliedManualReceipt_CanBeRefundedWithoutInventingAllocationContext()
    {
        SkipIfNoDocker();
        var scenario = await SeedScenarioAsync("unapplied-receipt-correction");
        var receiptCommand = Receipt(
            scenario, "unapplied-receipt-correction", 45m);
        var receipt = await ExecuteAtomicAsync(
            new AtomicCommandIdentity(
                "tenant-account.receipt.record", receiptCommand.DeliveryIdempotencyKey),
            receiptCommand,
            ReceiptCodec);
        var correction = RefundPayment(
            scenario, receipt.Value.LedgerEntryId, "unapplied-receipt-correction");
        var identity = new AtomicCommandIdentity(
            "tenant-account.payment.refund", correction.DeliveryIdempotencyKey);

        var corrected = await ExecuteAtomicAsync(identity, correction, RefundCodec);
        var replay = await ExecuteAtomicAsync(identity, correction, RefundCodec);

        corrected.Value.Outcome.Should().Be(TenantPaymentRefundOutcome.Refunded);
        corrected.Value.CompensatedAllocationAmount.Should().Be(0m);
        corrected.Value.CompensatedAllocationCount.Should().Be(0);
        replay.Disposition.Should().Be(AtomicCommandDisposition.Replayed);
        replay.Value.Should().Be(corrected.Value);
        await using var verify = NewContext();
        var originalAttempt = await verify.TenantPaymentAttempts.SingleAsync(row =>
            row.Id == receipt.Value.PaymentAttemptId);
        originalAttempt.AttemptType.Should().Be(TenantPaymentAttemptType.UnappliedReceipt);
        var refundAttempt = await verify.TenantPaymentAttempts.SingleAsync(row =>
            row.Id == corrected.Value.ProviderPaymentAttemptId);
        refundAttempt.AttemptType.Should().Be(TenantPaymentAttemptType.Refund);
        refundAttempt.RefundsPaymentAttemptId.Should().Be(receipt.Value.PaymentAttemptId);
        (await verify.TenantLedgerAllocations.AnyAsync(row =>
            row.CreditEntryId == receipt.Value.LedgerEntryId)).Should().BeFalse();
        var balance = await verify.TenantAccountBalanceProjections.SingleAsync(row =>
            row.TenantAccountId == scenario.AccountId);
        balance.UnappliedCredit.Should().Be(0m);
    }

    [SkippableFact]
    public async Task ProviderRefund_ReturnsUnavailableWithoutBusinessWrites()
    {
        SkipIfNoDocker();
        var scenario = await SeedScenarioAsync("provider-refund-boundary");
        var chargeCommand = PostCharge(scenario, "provider-refund-charge", 82m);
        var charge = await ExecuteAtomicAsync(
            new AtomicCommandIdentity(
                "tenant-account.charge.post", chargeCommand.DeliveryIdempotencyKey),
            chargeCommand,
            ChargeCodec);
        var providerReceiptId = await SeedProviderReceiptAsync(
            scenario, "provider-refund", 82m, charge.Value.LedgerEntryId);
        var command = RefundPayment(scenario, providerReceiptId, "provider-refund");
        var identity = new AtomicCommandIdentity(
            "tenant-account.payment.refund", command.DeliveryIdempotencyKey);

        int attemptCount;
        int ledgerCount;
        int allocationCount;
        int auditCount;
        int outboxCount;
        await using (var baseline = NewContext())
        {
            attemptCount = await baseline.TenantPaymentAttempts.CountAsync();
            ledgerCount = await baseline.TenantLedgerEntries.CountAsync();
            allocationCount = await baseline.TenantLedgerAllocations.CountAsync();
            auditCount = await baseline.AtomicAuditLogs.CountAsync();
            outboxCount = await baseline.OutboxMessages.CountAsync();
        }
        var unavailable = await ExecuteAtomicAsync(identity, command, RefundCodec);
        var replay = await ExecuteAtomicAsync(identity, command, RefundCodec);
        unavailable.Value.Outcome.Should().Be(
            TenantPaymentRefundOutcome.ExternalCorrectionUnavailable);
        unavailable.Value.Applied.Should().BeFalse();
        unavailable.Value.RefundEntryId.Should().BeNull();
        unavailable.Value.ProviderPaymentAttemptId.Should().BeNull();
        unavailable.Value.Error.Should().Contain("Provider-backed payment refunds are unavailable");
        replay.Disposition.Should().Be(AtomicCommandDisposition.Replayed);
        replay.Value.Should().Be(unavailable.Value);

        await using var verify = NewContext();
        (await verify.TenantPaymentAttempts.CountAsync()).Should().Be(attemptCount);
        (await verify.TenantLedgerEntries.CountAsync()).Should().Be(ledgerCount);
        (await verify.TenantLedgerAllocations.CountAsync()).Should().Be(allocationCount);
        (await verify.AtomicAuditLogs.CountAsync()).Should().Be(auditCount);
        (await verify.OutboxMessages.CountAsync()).Should().Be(outboxCount);
        (await verify.AtomicCommandReceipts.CountAsync(row =>
            row.CommandType == identity.CommandType
            && row.IdempotencyKey == identity.IdempotencyKey)).Should().Be(1);
    }

    [SkippableFact]
    public async Task DepositDeduction_ReturnsItsExactLinkedCreditProvenance()
    {
        SkipIfNoDocker();
        var scenario = await SeedScenarioAsync("deposit-deduction-result");
        await SeedLedgerEntryAsync(scenario, scenario.AccountId,
            TenantLedgerEntryType.DepositCharge, TenantLedgerDirection.Debit,
            "deposit-deduction-charge", 100m, DateOnly.FromDateTime(DateTime.UtcNow));
        var fund = FundDeposit(scenario, "deposit-deduction-result", 100m);
        await ExecuteAtomicAsync(
            new AtomicCommandIdentity("tenant-account.deposit.fund", fund.DeliveryIdempotencyKey),
            fund,
            DepositCodec);
        var deduction = DeductDeposit(scenario, "deposit-deduction-result", 40m);
        var result = await ExecuteAtomicAsync(
            new AtomicCommandIdentity(
                "tenant-account.deposit.deduct", deduction.DeliveryIdempotencyKey),
            deduction,
            DepositCodec);

        await using var db = NewContext();
        var depositEntry = await db.SecurityDepositEntries.SingleAsync(row =>
            row.Id == result.Value.SecurityDepositEntryId);
        depositEntry.EntryType.Should().Be(SecurityDepositEntryType.Deduction);
        depositEntry.TenantLedgerEntryId.Should().Be(result.Value.TenantLedgerEntryId);
        var linkedCredit = await db.TenantLedgerEntries.SingleAsync(row =>
            row.Id == result.Value.TenantLedgerEntryId);
        linkedCredit.EntryType.Should().Be(TenantLedgerEntryType.Credit);
        linkedCredit.Direction.Should().Be(TenantLedgerDirection.Credit);
        linkedCredit.Amount.Should().Be(40m);
        var deductionCharge = await db.TenantLedgerEntries.SingleAsync(row =>
            row.BusinessKey == $"{deduction.BusinessKey}:charge");
        result.Value.TenantLedgerEntryId.Should().NotBe(deductionCharge.Id);
    }

    [SkippableFact]
    public async Task DepositFunding_AllocatesOpenDepositChargeWithExactReplayAndRollback()
    {
        SkipIfNoDocker();
        var scenario = await SeedScenarioAsync("deposit-fund-unapplied");
        var depositChargeId = await SeedLedgerEntryAsync(
            scenario,
            scenario.AccountId,
            TenantLedgerEntryType.DepositCharge,
            TenantLedgerDirection.Debit,
            "deposit-fund-unapplied:charge",
            100m,
            DateOnly.FromDateTime(DateTime.UtcNow));
        var command = FundDeposit(scenario, "deposit-fund-unapplied", 100m);
        var identity = new AtomicCommandIdentity(
            "tenant-account.deposit.fund", command.DeliveryIdempotencyKey);

        Failures.FailAtomicAudit = true;
        try
        {
            await FluentActions.Invoking(() =>
                    ExecuteAtomicAsync(identity, command, DepositCodec))
                .Should().ThrowAsync<DbUpdateException>();
        }
        finally
        {
            Failures.FailAtomicAudit = false;
        }
        await using (var rolledBack = NewContext())
        {
            (await rolledBack.TenantPaymentAttempts.CountAsync(row =>
                row.IdempotencyKey == command.DeliveryIdempotencyKey)).Should().Be(0);
            (await rolledBack.TenantLedgerEntries.CountAsync(row =>
                row.BusinessKey == $"{command.BusinessKey}:tenant-receipt")).Should().Be(0);
            (await rolledBack.SecurityDepositEntries.CountAsync(row =>
                row.BusinessKey == command.BusinessKey)).Should().Be(0);
            (await rolledBack.TenantLedgerAllocations.CountAsync(row =>
                row.BusinessKey.StartsWith($"{command.BusinessKey}:allocation"))).Should().Be(0);
            (await rolledBack.AtomicCommandReceipts.CountAsync(row =>
                row.CommandType == identity.CommandType
                && row.IdempotencyKey == identity.IdempotencyKey)).Should().Be(0);
        }

        var funded = await ExecuteAtomicAsync(identity, command, DepositCodec);
        var replay = await ExecuteAtomicAsync(identity, command, DepositCodec);

        funded.Disposition.Should().Be(AtomicCommandDisposition.Executed);
        replay.Disposition.Should().Be(AtomicCommandDisposition.Replayed);
        replay.Value.Should().Be(funded.Value);
        await using var db = NewContext();
        var receipt = await db.TenantLedgerEntries.SingleAsync(row =>
            row.Id == funded.Value.TenantLedgerEntryId);
        var paymentAttempt = await db.TenantPaymentAttempts.SingleAsync(row =>
            row.Id == receipt.ProviderPaymentAttemptId);
        paymentAttempt.AttemptType.Should().Be(TenantPaymentAttemptType.DepositReceipt);
        paymentAttempt.ChargeLedgerEntryId.Should().BeNull();
        var allocation = await db.TenantLedgerAllocations.SingleAsync(row =>
            row.CreditEntryId == funded.Value.TenantLedgerEntryId);
        allocation.DebitEntryId.Should().Be(depositChargeId);
        allocation.Amount.Should().Be(100m);
        (await db.TenantChargeBalanceProjections.SingleAsync(row =>
            row.TenantLedgerEntryId == depositChargeId)).OpenAmount.Should().Be(0m);
        (await db.SecurityDepositEntries.CountAsync(row =>
            row.Id == funded.Value.SecurityDepositEntryId
            && row.TenantLedgerEntryId == funded.Value.TenantLedgerEntryId)).Should().Be(1);
        (await db.AtomicCommandReceipts.CountAsync(row =>
            row.CommandType == identity.CommandType
            && row.IdempotencyKey == identity.IdempotencyKey)).Should().Be(1);
    }

    [SkippableFact]
    public async Task DepositCycle_FundDeductAndRefund_PreservesExactHeldBalanceParity()
    {
        SkipIfNoDocker();
        var scenario = await SeedScenarioAsync("deposit-cycle");
        await SeedLedgerEntryAsync(
            scenario, scenario.AccountId, TenantLedgerEntryType.DepositCharge,
            TenantLedgerDirection.Debit, "deposit-cycle:charge", 100m,
            DateOnly.FromDateTime(DateTime.UtcNow));

        var fund = FundDeposit(scenario, "deposit-cycle", 100m);
        var deduct = DeductDeposit(scenario, "deposit-cycle", 40m);
        var refund = RefundDeposit(scenario, "deposit-cycle", 60m);
        var funded = await ExecuteAtomicAsync(
            new("tenant-account.deposit.fund", fund.DeliveryIdempotencyKey), fund, DepositCodec);
        var deducted = await ExecuteAtomicAsync(
            new("tenant-account.deposit.deduct", deduct.DeliveryIdempotencyKey), deduct, DepositCodec);
        var refunded = await ExecuteAtomicAsync(
            new("tenant-account.deposit.refund", refund.DeliveryIdempotencyKey), refund, DepositCodec);

        funded.Value.Amount.Should().Be(100m);
        deducted.Value.Amount.Should().Be(40m);
        refunded.Value.Amount.Should().Be(60m);
        await using var db = NewContext();
        (await db.SecurityDepositBalanceProjections.SingleAsync(row =>
            row.SecurityDepositAccountId == scenario.DepositAccountId)).HeldBalance.Should().Be(0m);
        (await db.SecurityDepositEntries.CountAsync(row =>
            row.SecurityDepositAccountId == scenario.DepositAccountId
            && (row.BusinessKey == fund.BusinessKey
                || row.BusinessKey == deduct.BusinessKey
                || row.BusinessKey == refund.BusinessKey))).Should().Be(3);
    }

    [SkippableFact]
    public async Task DepositReceiptReversal_AppendsExactSubledgerAndLedgerCompensationAtomically()
    {
        SkipIfNoDocker();
        var scenario = await SeedScenarioAsync("deposit-reversal");
        var depositChargeId = await SeedLedgerEntryAsync(scenario, scenario.AccountId,
            TenantLedgerEntryType.DepositCharge, TenantLedgerDirection.Debit,
            "deposit-reversal-charge", 150m, DateOnly.FromDateTime(DateTime.UtcNow));
        var fund = FundDeposit(scenario, "deposit-reversal", 100m);
        var funded = await ExecuteAtomicAsync(
            new AtomicCommandIdentity("tenant-account.deposit.fund", fund.DeliveryIdempotencyKey),
            fund,
            DepositCodec);
        var reverse = ReverseDeposit(scenario, funded.Value.SecurityDepositEntryId, "receipt");
        var identity = new AtomicCommandIdentity(
            "tenant-account.deposit.reverse", reverse.DeliveryIdempotencyKey);

        Failures.FailAtomicAudit = true;
        try
        {
            await FluentActions.Invoking(() =>
                    ExecuteAtomicAsync(identity, reverse, DepositCodec))
                .Should().ThrowAsync<DbUpdateException>();
        }
        finally
        {
            Failures.FailAtomicAudit = false;
        }
        await using (var rolledBack = NewContext())
        {
            (await rolledBack.SecurityDepositEntries.CountAsync(row =>
                row.ReversesEntryId == funded.Value.SecurityDepositEntryId)).Should().Be(0);
            (await rolledBack.TenantLedgerEntries.CountAsync(row =>
                row.ReversesEntryId == funded.Value.TenantLedgerEntryId)).Should().Be(0);
            (await rolledBack.TenantLedgerAllocations.CountAsync(row =>
                row.CreditEntryId == funded.Value.TenantLedgerEntryId
                && row.ReversesAllocationId != null)).Should().Be(0);
            (await rolledBack.AtomicAuditLogs.CountAsync(row =>
                row.CommandType == identity.CommandType
                && row.CommandIdempotencyKey == identity.IdempotencyKey)).Should().Be(0);
            (await rolledBack.AtomicCommandReceipts.CountAsync(row =>
                row.CommandType == identity.CommandType
                && row.IdempotencyKey == identity.IdempotencyKey)).Should().Be(0);
        }

        var reversed = await ExecuteAtomicAsync(identity, reverse, DepositCodec);
        await using var db = NewContext();
        var depositReversal = await db.SecurityDepositEntries.SingleAsync(row =>
            row.Id == reversed.Value.SecurityDepositEntryId);
        depositReversal.EntryType.Should().Be(SecurityDepositEntryType.Reversal);
        depositReversal.Direction.Should().Be(SecurityDepositDirection.Decrease);
        depositReversal.Amount.Should().Be(100m);
        depositReversal.ReversesEntryId.Should().Be(funded.Value.SecurityDepositEntryId);
        var ledgerReversal = await db.TenantLedgerEntries.SingleAsync(row =>
            row.Id == reversed.Value.TenantLedgerEntryId);
        depositReversal.TenantLedgerEntryId.Should().Be(ledgerReversal.Id);
        ledgerReversal.EntryType.Should().Be(TenantLedgerEntryType.Reversal);
        ledgerReversal.Direction.Should().Be(TenantLedgerDirection.Debit);
        ledgerReversal.Amount.Should().Be(100m);
        ledgerReversal.ReversesEntryId.Should().Be(funded.Value.TenantLedgerEntryId);
        var allocationQuery = db.TenantLedgerAllocations
            .Where(row => row.CreditEntryId == funded.Value.TenantLedgerEntryId);
        (await allocationQuery.CountAsync()).Should().Be(2);
        var originalAllocation = await allocationQuery.SingleAsync(row =>
            row.ReversesAllocationId == null);
        var compensatingAllocation = await allocationQuery.SingleAsync(row =>
            row.ReversesAllocationId != null);
        originalAllocation.DebitEntryId.Should().Be(depositChargeId);
        originalAllocation.Amount.Should().Be(100m);
        compensatingAllocation.ReversesAllocationId.Should().Be(originalAllocation.Id);
        compensatingAllocation.DebitEntryId.Should().Be(originalAllocation.DebitEntryId);
        compensatingAllocation.CreditEntryId.Should().Be(originalAllocation.CreditEntryId);
        compensatingAllocation.Amount.Should().Be(-originalAllocation.Amount);
        (await db.TenantChargeBalanceProjections.SingleAsync(row =>
            row.TenantLedgerEntryId == depositChargeId)).OpenAmount.Should().Be(150m);
        var tenantBalance = await db.TenantAccountBalanceProjections.SingleAsync(row =>
            row.TenantAccountId == scenario.AccountId);
        tenantBalance.ReceivableBalance.Should().Be(150m);
        tenantBalance.UnappliedCredit.Should().Be(0m);
        (await db.SecurityDepositBalanceProjections.SingleAsync(row =>
            row.SecurityDepositAccountId == scenario.DepositAccountId))
            .HeldBalance.Should().Be(0m);
        (await db.AtomicAuditLogs.CountAsync(row =>
            row.CommandType == identity.CommandType
            && row.CommandIdempotencyKey == identity.IdempotencyKey)).Should().Be(2,
            "the business mutation and its journal entry are audited together");
        (await db.AtomicCommandReceipts.CountAsync(row =>
            row.CommandType == identity.CommandType
            && row.IdempotencyKey == identity.IdempotencyKey)).Should().Be(1);
    }

    private async Task<Scenario> SeedScenarioAsync(string suffix, bool seedChart = true)
    {
        var now = DateTime.UtcNow;
        await using var db = NewContext();
        var user = new ApplicationUser
        {
            UserName = $"tenant-money-{suffix}@example.test",
            NormalizedUserName = $"TENANT-MONEY-{suffix.ToUpperInvariant()}@EXAMPLE.TEST",
            Email = $"tenant-money-{suffix}@example.test",
            NormalizedEmail = $"TENANT-MONEY-{suffix.ToUpperInvariant()}@EXAMPLE.TEST",
            DisplayName = $"Tenant money {suffix}",
            SecurityStamp = Guid.NewGuid().ToString("N"),
            ConcurrencyStamp = Guid.NewGuid().ToString("N"),
            CreatedAt = now,
        };
        var portfolio = new Portfolio
        {
            Name = $"Tenant money {suffix}",
            ManagementCompanyName = $"Tenant money {suffix}",
            TimeZone = "UTC",
            Currency = "USD",
            CreatedAt = now,
            UpdatedAt = now,
        };
        db.AddRange(user, portfolio);
        await db.SaveChangesAsync();
        if (seedChart)
        {
            await new ChartOfAccountsSeedService(db).SeedAsync(portfolio.Id);
            await db.SaveChangesAsync();
        }

        var property = new Property
        {
            PortfolioId = portfolio.Id,
            Name = $"Charge property {suffix}",
            AddressLine1 = "1 Ledger Way",
            City = "Columbus",
            State = "OH",
            PostalCode = "43215",
            CreatedAt = now,
            UpdatedAt = now,
        };
        var firstUnit = Unit(portfolio.Id, property, "1", now);
        var secondUnit = Unit(portfolio.Id, property, "2", now);
        db.AddRange(property, firstUnit, secondUnit);
        await db.SaveChangesAsync();

        var accessContext = new WorkspaceAccessContext
        {
            UserId = user.Id,
            PortfolioId = portfolio.Id,
            Status = WorkspaceAccessContextStatus.Active,
            LastAuthorizedExperience = WorkspaceExperience.Management,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
        };
        var membership = new WorkspaceMembership
        {
            AccessContext = accessContext,
            PortfolioId = portfolio.Id,
            Status = WorkspaceMembershipStatus.Active,
            DefaultExperience = WorkspaceExperience.Management,
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
        var session = new AuthSession
        {
            Id = Guid.NewGuid(),
            UserId = user.Id,
            ActiveAccessContextId = accessContext.Id,
            Status = AuthSessionStatus.Active,
            CreatedAtUtc = now,
            LastSeenAtUtc = now,
            ExpiresAtUtc = now.AddDays(1),
        };
        var firstRelationship = Relationship(portfolio.Id, property.Id, firstUnit.Id, user.Id, $"{suffix}-1", now);
        var secondRelationship = Relationship(portfolio.Id, property.Id, secondUnit.Id, user.Id, $"{suffix}-2", now);
        db.AddRange(assignment, session, firstRelationship, secondRelationship);
        await db.SaveChangesAsync();

        var firstAccount = Account(portfolio.Id, firstRelationship.Id, user.Id, $"{suffix}-1", now);
        var secondAccount = Account(portfolio.Id, secondRelationship.Id, user.Id, $"{suffix}-2", now);
        db.AddRange(firstAccount, secondAccount);
        await db.SaveChangesAsync();

        var template = new DocumentTemplate
        {
            PortfolioId = portfolio.Id,
            Name = $"Deposit source {suffix}",
            Version = 1,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
        };
        db.DocumentTemplates.Add(template);
        await db.SaveChangesAsync();
        var source = new LegalDocumentSourceVersion
        {
            PublicId = Guid.NewGuid(),
            PortfolioId = portfolio.Id,
            SourceKind = LegalDocumentSourceKind.AuthoredTemplateSnapshot,
            BusinessKey = $"deposit-source:{suffix}",
            DocumentTemplateId = template.Id,
            DocumentTemplateVersion = template.Version,
            RendererKey = "integration-deposit-source",
            RendererVersion = 1,
            SnapshotPayload = "{}",
            CreatedAtUtc = now,
            CreatedByUserId = user.Id,
        };
        db.LegalDocumentSourceVersions.Add(source);
        await db.SaveChangesAsync();
        var agreement = new LeaseAgreement
        {
            PublicId = Guid.NewGuid(),
            PortfolioId = portfolio.Id,
            LeaseManagementId = firstRelationship.Id,
            VersionNumber = 1,
            AgreementNumber = $"AGR-{suffix}",
            ChangeType = LeaseAgreementChangeType.Initial,
            TermType = LeaseAgreementTermType.FixedTerm,
            TermStartOn = DateOnly.FromDateTime(now),
            TermEndOn = DateOnly.FromDateTime(now.AddYears(1)),
            GoverningFromOn = DateOnly.FromDateTime(now),
            BaseRentAmount = 1_000m,
            RentDueDay = 1,
            SecurityDepositObligation = 100m,
            LateFeeAmount = 25m,
            GracePeriodDays = 5,
            Currency = "USD",
            TermsSchemaVersion = 1,
            TermsPayload = "{}",
            DocumentSourceVersionId = source.Id,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
            CreatedByUserId = user.Id,
        };
        db.LeaseAgreements.Add(agreement);
        await db.SaveChangesAsync();
        var depositAccount = new SecurityDepositAccount
        {
            PortfolioId = portfolio.Id,
            TenantAccountId = firstAccount.Id,
            OriginatingAgreementId = agreement.Id,
            Currency = "USD",
            CreatedAtUtc = now,
            CreatedByUserId = user.Id,
        };
        db.SecurityDepositAccounts.Add(depositAccount);
        await db.SaveChangesAsync();
        return new Scenario(portfolio.Id, property.Id, firstAccount.Id, secondAccount.Id,
            depositAccount.Id, user.Id, session.Id, accessContext.Id,
            accessContext.AccessRevision);
    }

    private static Unit Unit(int portfolioId, Property property, string number, DateTime now) => new()
    {
        PortfolioId = portfolioId,
        Property = property,
        UnitNumber = number,
        CreatedAt = now,
        UpdatedAt = now,
    };

    private static LeaseManagement Relationship(
        int portfolioId, int propertyId, int unitId, int userId, string suffix, DateTime now) => new()
    {
        PortfolioId = portfolioId,
        PropertyId = propertyId,
        UnitId = unitId,
        RelationshipNumber = $"LM-{suffix}",
        CreatedAtUtc = now,
        UpdatedAtUtc = now,
        CreatedByUserId = userId,
    };

    private static TenantAccount Account(
        int portfolioId, int relationshipId, int userId, string suffix, DateTime now) => new()
    {
        PortfolioId = portfolioId,
        LeaseManagementId = relationshipId,
        AccountNumber = $"TA-{suffix}",
        Currency = "USD",
        OpenedAtUtc = now,
        CreatedAtUtc = now,
        CreatedByUserId = userId,
    };

    private static PostTenantChargeCommand PostCharge(Scenario scenario, string suffix, decimal amount) =>
        new(scenario.PortfolioId, scenario.AccountId, amount,
            DateOnly.FromDateTime(DateTime.UtcNow), DateOnly.FromDateTime(DateTime.UtcNow.AddDays(10)),
            $"Manual charge {suffix}", null, scenario.UserId, scenario.SessionId,
            scenario.AccessContextId, scenario.AccessRevision, CapabilityKeys.MoneyChargesManage,
            $"manual-charge:{suffix}", $"tenant-charge:{scenario.PortfolioId}:{scenario.AccountId}:{suffix}");

    private static RecordTenantReceiptCommand Receipt(
        Scenario scenario, string suffix, decimal amount, long? targetChargeEntryId = null) =>
        new(scenario.PortfolioId, scenario.AccountId, amount,
            DateOnly.FromDateTime(DateTime.UtcNow), $"Receipt {suffix}", "Check", null, null,
            null, null, null, targetChargeEntryId, scenario.UserId, scenario.SessionId, scenario.AccessContextId,
            scenario.AccessRevision, CapabilityKeys.MoneyPaymentsManage, $"receipt:{suffix}",
            $"tenant-receipt:{scenario.PortfolioId}:{scenario.AccountId}:{suffix}");

    private static ReverseTenantChargeCommand ReverseCharge(Scenario scenario, long entryId, string suffix) =>
        new(scenario.PortfolioId, scenario.AccountId, entryId, DateOnly.FromDateTime(DateTime.UtcNow),
            $"Correction {suffix}", null, scenario.UserId, scenario.SessionId,
            scenario.AccessContextId, scenario.AccessRevision, CapabilityKeys.MoneyChargesManage,
            $"charge-reversal:{suffix}",
            $"tenant-charge-reversal:{scenario.PortfolioId}:{scenario.AccountId}:{entryId}:{suffix}");

    private static PostTenantCreditCommand Credit(
        Scenario scenario, string suffix, decimal amount, bool allocateOldestCharges = true) =>
        new(scenario.PortfolioId, scenario.AccountId, amount,
            DateOnly.FromDateTime(DateTime.UtcNow), $"Credit {suffix}", null,
            allocateOldestCharges, scenario.UserId, scenario.SessionId,
            scenario.AccessContextId, scenario.AccessRevision, CapabilityKeys.MoneyChargesManage,
            $"tenant-credit:{suffix}",
            $"tenant-credit:{scenario.PortfolioId}:{scenario.AccountId}:{suffix}");

    private static PostTenantAdjustmentCommand Adjustment(
        Scenario scenario, string suffix, TenantLedgerDirection direction, decimal amount) =>
        new(scenario.PortfolioId, scenario.AccountId, direction, amount,
            DateOnly.FromDateTime(DateTime.UtcNow), $"Adjustment {suffix}", null,
            scenario.UserId, scenario.SessionId, scenario.AccessContextId,
            scenario.AccessRevision, CapabilityKeys.MoneyChargesManage,
            $"tenant-adjustment:{suffix}",
            $"tenant-adjustment:{scenario.PortfolioId}:{scenario.AccountId}:{suffix}");

    private static ReverseTenantLedgerEntryCommand ReverseLedger(
        Scenario scenario, long entryId, string suffix) =>
        new(scenario.PortfolioId, scenario.AccountId, entryId,
            DateOnly.FromDateTime(DateTime.UtcNow), $"Ledger correction {suffix}", null,
            scenario.UserId, scenario.SessionId, scenario.AccessContextId,
            scenario.AccessRevision, CapabilityKeys.MoneyChargesManage,
            $"tenant-ledger-reversal:{suffix}",
            $"tenant-ledger-reversal:{scenario.PortfolioId}:{scenario.AccountId}:{entryId}:{suffix}");

    private static RefundTenantPaymentCommand RefundPayment(
        Scenario scenario, long paymentEntryId, string suffix) =>
        new(scenario.PortfolioId, scenario.AccountId, paymentEntryId,
            DateOnly.FromDateTime(DateTime.UtcNow), $"Payment refund {suffix}",
            "Check", $"refund-{suffix}", null,
            scenario.UserId, scenario.SessionId, scenario.AccessContextId,
            scenario.AccessRevision, CapabilityKeys.MoneyPaymentsManage,
            $"tenant-payment-refund:{suffix}",
            $"tenant-payment-refund:{scenario.PortfolioId}:{scenario.AccountId}:{paymentEntryId}:{suffix}");

    private static FundSecurityDepositCommand FundDeposit(
        Scenario scenario, string suffix, decimal amount) =>
        new(scenario.PortfolioId, scenario.AccountId, scenario.DepositAccountId, amount,
            DateOnly.FromDateTime(DateTime.UtcNow), $"Deposit funding {suffix}",
            "Check", $"check-{suffix}", null, scenario.UserId, scenario.SessionId,
            scenario.AccessContextId, scenario.AccessRevision,
            CapabilityKeys.MoneyDepositsManage, $"deposit-fund:{suffix}",
            $"deposit-fund:{scenario.PortfolioId}:{scenario.AccountId}:{suffix}");

    private static DeductSecurityDepositCommand DeductDeposit(
        Scenario scenario, string suffix, decimal amount) =>
        new(scenario.PortfolioId, scenario.AccountId, scenario.DepositAccountId, amount,
            DateOnly.FromDateTime(DateTime.UtcNow), $"Deposit deduction {suffix}", null, null,
            scenario.UserId, scenario.SessionId, scenario.AccessContextId,
            scenario.AccessRevision, CapabilityKeys.MoneyDepositsManage,
            $"deposit-deduction:{suffix}",
            $"deposit-deduction:{scenario.PortfolioId}:{scenario.AccountId}:{suffix}");

    private static RefundSecurityDepositCommand RefundDeposit(
        Scenario scenario, string suffix, decimal amount) =>
        new(scenario.PortfolioId, scenario.AccountId, scenario.DepositAccountId, amount,
            DateOnly.FromDateTime(DateTime.UtcNow), $"Deposit refund {suffix}", $"refund-{suffix}",
            scenario.UserId, scenario.SessionId, scenario.AccessContextId,
            scenario.AccessRevision, CapabilityKeys.MoneyDepositsManage,
            $"deposit-refund:{suffix}",
            $"deposit-refund:{scenario.PortfolioId}:{scenario.AccountId}:{suffix}");

    private static ReverseSecurityDepositEntryCommand ReverseDeposit(
        Scenario scenario, long entryId, string suffix) =>
        new(scenario.PortfolioId, scenario.AccountId, scenario.DepositAccountId, entryId,
            DateOnly.FromDateTime(DateTime.UtcNow), $"Deposit correction {suffix}", null,
            scenario.UserId, scenario.SessionId, scenario.AccessContextId,
            scenario.AccessRevision, CapabilityKeys.MoneyDepositsManage,
            $"deposit-reversal:{suffix}",
            $"deposit-reversal:{scenario.PortfolioId}:{scenario.AccountId}:{entryId}:{suffix}");

    private static void AssertExactReversal(
        TenantLedgerEntry target,
        TenantLedgerEntry reversal,
        TenantLedgerDirection expectedDirection)
    {
        reversal.EntryType.Should().Be(TenantLedgerEntryType.Reversal);
        reversal.Direction.Should().Be(expectedDirection);
        reversal.Amount.Should().Be(target.Amount);
        reversal.Currency.Should().Be(target.Currency);
        reversal.TenantAccountId.Should().Be(target.TenantAccountId);
        reversal.ReversesEntryId.Should().Be(target.Id);
    }

    private static void AssertExactAllocationCompensation(
        IReadOnlyCollection<TenantLedgerAllocation> allocations)
    {
        allocations.Should().HaveCount(2);
        var original = allocations.Single(row => row.ReversesAllocationId == null);
        var compensation = allocations.Single(row => row.ReversesAllocationId != null);
        compensation.ReversesAllocationId.Should().Be(original.Id);
        compensation.DebitEntryId.Should().Be(original.DebitEntryId);
        compensation.CreditEntryId.Should().Be(original.CreditEntryId);
        compensation.Amount.Should().Be(-original.Amount);
    }

    private async Task<(SecurityDepositMutationResult Deduction, long ChargeEntryId)>
        CreateDepositDeductionAsync(Scenario scenario, string suffix)
    {
        await SeedLedgerEntryAsync(
            scenario,
            scenario.AccountId,
            TenantLedgerEntryType.DepositCharge,
            TenantLedgerDirection.Debit,
            $"{suffix}:deposit-charge",
            100m,
            DateOnly.FromDateTime(DateTime.UtcNow));
        var fund = FundDeposit(scenario, suffix, 100m);
        await ExecuteAtomicAsync(
            new AtomicCommandIdentity(
                "tenant-account.deposit.fund", fund.DeliveryIdempotencyKey),
            fund,
            DepositCodec);
        var deduct = DeductDeposit(scenario, suffix, 40m);
        var deduction = await ExecuteAtomicAsync(
            new AtomicCommandIdentity(
                "tenant-account.deposit.deduct", deduct.DeliveryIdempotencyKey),
            deduct,
            DepositCodec);
        await using var db = NewContext();
        var chargeEntryId = await db.TenantLedgerEntries
            .Where(row => row.PortfolioId == scenario.PortfolioId
                && row.TenantAccountId == scenario.AccountId
                && row.BusinessKey == $"{deduct.BusinessKey}:charge")
            .Select(row => row.Id)
            .SingleAsync();
        return (deduction.Value, chargeEntryId);
    }

    private async Task AssertDepositGenericReversalRejectedAsync(
        Scenario scenario,
        long targetEntryId,
        string suffix)
    {
        var command = ReverseLedger(scenario, targetEntryId, suffix);
        var identity = new AtomicCommandIdentity(
            "tenant-account.ledger.reverse", command.DeliveryIdempotencyKey);

        var rejected = await ExecuteAtomicAsync(identity, command, LedgerCodec);
        var replay = await ExecuteAtomicAsync(identity, command, LedgerCodec);

        rejected.Disposition.Should().Be(AtomicCommandDisposition.Executed);
        rejected.Value.Applied.Should().BeFalse();
        rejected.Value.ReversesEntryId.Should().Be(targetEntryId);
        rejected.Value.Error.Should().Contain("dedicated security-deposit workflow");
        replay.Disposition.Should().Be(AtomicCommandDisposition.Replayed);
        replay.Value.Should().Be(rejected.Value);

        await using var verify = NewContext();
        (await verify.TenantLedgerEntries.CountAsync(row =>
            row.PortfolioId == scenario.PortfolioId
            && row.TenantAccountId == scenario.AccountId
            && row.EntryType == TenantLedgerEntryType.Reversal
            && row.ReversesEntryId == targetEntryId)).Should().Be(0);
        (await verify.AtomicCommandReceipts.CountAsync(row =>
            row.CommandType == identity.CommandType
            && row.IdempotencyKey == identity.IdempotencyKey)).Should().Be(1);
    }

    private async Task<long> SeedLedgerEntryAsync(
        Scenario scenario,
        int accountId,
        TenantLedgerEntryType entryType,
        TenantLedgerDirection direction,
        string businessKey,
        decimal amount,
        DateOnly? dueOn = null)
    {
        await using var db = NewContext();
        var entry = new TenantLedgerEntry
        {
            PortfolioId = scenario.PortfolioId,
            TenantAccountId = accountId,
            EntryType = entryType,
            Direction = direction,
            Amount = amount,
            Currency = "USD",
            EffectiveOn = DateOnly.FromDateTime(DateTime.UtcNow),
            DueOn = dueOn,
            PostedAtUtc = DateTime.UtcNow,
            Description = businessKey,
            BusinessKey = businessKey,
            CreatedByUserId = scenario.UserId,
        };
        db.TenantLedgerEntries.Add(entry);
        await db.SaveChangesAsync();
        return entry.Id;
    }

    private async Task<long> SeedProviderReceiptAsync(
        Scenario scenario, string suffix, decimal amount, long debitEntryId)
    {
        var now = DateTime.UtcNow;
        await using var db = NewContext();
        var attempt = new TenantPaymentAttempt
        {
            PortfolioId = scenario.PortfolioId,
            TenantAccountId = scenario.AccountId,
            Provider = "stripe",
            ProviderObjectId = $"pi_{suffix}",
            IdempotencyKey = $"provider-receipt:{suffix}",
            AttemptType = TenantPaymentAttemptType.Charge,
            ChargeLedgerEntryId = debitEntryId,
            State = TenantPaymentAttemptState.Succeeded,
            Amount = amount,
            Currency = "USD",
            PreparedAtUtc = now.AddMinutes(-2),
            SubmittedAtUtc = now.AddMinutes(-1),
            SettledAtUtc = now,
            UpdatedAtUtc = now,
            AttemptCount = 1,
            CreatedByUserId = scenario.UserId,
        };
        var receipt = new TenantLedgerEntry
        {
            PortfolioId = scenario.PortfolioId,
            TenantAccountId = scenario.AccountId,
            EntryType = TenantLedgerEntryType.PaymentReceipt,
            Direction = TenantLedgerDirection.Credit,
            Amount = amount,
            Currency = "USD",
            EffectiveOn = DateOnly.FromDateTime(now),
            PostedAtUtc = now,
            Description = $"Provider receipt {suffix}",
            BusinessKey = $"provider-receipt:{suffix}",
            ProviderPaymentAttempt = attempt,
            CreatedByUserId = scenario.UserId,
        };
        db.AddRange(attempt, receipt);
        await db.SaveChangesAsync();
        db.TenantLedgerAllocations.Add(new TenantLedgerAllocation
        {
            PortfolioId = scenario.PortfolioId,
            TenantAccountId = scenario.AccountId,
            DebitEntryId = debitEntryId,
            CreditEntryId = receipt.Id,
            Amount = amount,
            AllocatedAtUtc = now,
            BusinessKey = $"provider-receipt:{suffix}:allocation",
            CreatedByUserId = scenario.UserId,
        });
        await db.SaveChangesAsync();
        return receipt.Id;
    }

    private async Task<AtomicCommandOutcome<TResult>> ExecuteAtomicAsync<TCommand, TResult>(
        AtomicCommandIdentity identity,
        TCommand command,
        AtomicJsonResultCodec<TResult> codec)
        where TCommand : notnull, ITenantMoneyCommand
        where TResult : notnull
    {
        await using var scope = _services!.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<RentalCommandDbContext>();
        var writes = scope.ServiceProvider.GetRequiredService<IRequestWriteExecutor>();
        object outcome = command switch
        {
            PostTenantChargeCommand value => await RunPostCharge(value, new PostTenantChargeHandler(db)),
            ReverseTenantChargeCommand value => await RunReverseCharge(value, new ReverseTenantChargeHandler(db)),
            RecordTenantReceiptCommand value => await RunReceipt(value, new RecordTenantReceiptHandler(db)),
            PostTenantCreditCommand value => await RunCredit(value, new PostTenantCreditHandler(db)),
            PostTenantAdjustmentCommand value => await RunAdjustment(value, new PostTenantAdjustmentHandler(db)),
            ReverseTenantLedgerEntryCommand value => await RunReverseLedger(value, new ReverseTenantLedgerEntryHandler(db)),
            RefundTenantPaymentCommand value => await RunRefundPayment(value, new RefundTenantPaymentHandler(db)),
            FundSecurityDepositCommand value => await RunFundDeposit(value, new FundSecurityDepositHandler(db)),
            DeductSecurityDepositCommand value => await RunDeductDeposit(value, new DeductSecurityDepositHandler(db)),
            RefundSecurityDepositCommand value => await RunRefundDeposit(value, new RefundSecurityDepositHandler(db)),
            ReverseSecurityDepositEntryCommand value => await RunReverseDeposit(value, new ReverseSecurityDepositEntryHandler(db)),
            _ => throw new ArgumentOutOfRangeException(nameof(command)),
        };
        return (AtomicCommandOutcome<TResult>)outcome;

        async Task<AtomicCommandOutcome<TSpecificResult>> Execute<TSpecificCommand, TSpecificResult>(
            TSpecificCommand request,
            Func<TSpecificCommand, IAtomicCommandContext, CancellationToken, Task<TSpecificResult>> executeAsync,
            Func<TSpecificCommand, IAtomicCommandContext, CancellationToken, Task> authorizeAsync)
            where TSpecificCommand : notnull, ITenantMoneyCommand
            where TSpecificResult : notnull => await writes.ExecuteAsync(
                identity.IdempotencyKey,
                TenantMoneyWriteSupport.Write(request, executeAsync, authorizeAsync));

        Task<AtomicCommandOutcome<TenantChargeMutationResult>> RunPostCharge(
            PostTenantChargeCommand request, PostTenantChargeHandler handler) =>
            Execute(request, handler.ExecuteAsync, handler.AuthorizeAsync);
        Task<AtomicCommandOutcome<TenantChargeMutationResult>> RunReverseCharge(
            ReverseTenantChargeCommand request, ReverseTenantChargeHandler handler) =>
            Execute(request, handler.ExecuteAsync, handler.AuthorizeAsync);
        Task<AtomicCommandOutcome<RecordTenantReceiptResult>> RunReceipt(
            RecordTenantReceiptCommand request, RecordTenantReceiptHandler handler) =>
            Execute(request, handler.ExecuteAsync, handler.AuthorizeAsync);
        Task<AtomicCommandOutcome<TenantLedgerMutationResult>> RunCredit(
            PostTenantCreditCommand request, PostTenantCreditHandler handler) =>
            Execute(request, handler.ExecuteAsync, handler.AuthorizeAsync);
        Task<AtomicCommandOutcome<TenantLedgerMutationResult>> RunAdjustment(
            PostTenantAdjustmentCommand request, PostTenantAdjustmentHandler handler) =>
            Execute(request, handler.ExecuteAsync, handler.AuthorizeAsync);
        Task<AtomicCommandOutcome<TenantLedgerMutationResult>> RunReverseLedger(
            ReverseTenantLedgerEntryCommand request, ReverseTenantLedgerEntryHandler handler) =>
            Execute(request, handler.ExecuteAsync, handler.AuthorizeAsync);
        Task<AtomicCommandOutcome<TenantPaymentRefundResult>> RunRefundPayment(
            RefundTenantPaymentCommand request, RefundTenantPaymentHandler handler) =>
            Execute(request, handler.ExecuteAsync, handler.AuthorizeAsync);
        Task<AtomicCommandOutcome<SecurityDepositMutationResult>> RunFundDeposit(
            FundSecurityDepositCommand request, FundSecurityDepositHandler handler) =>
            Execute(request, handler.ExecuteAsync, handler.AuthorizeAsync);
        Task<AtomicCommandOutcome<SecurityDepositMutationResult>> RunDeductDeposit(
            DeductSecurityDepositCommand request, DeductSecurityDepositHandler handler) =>
            Execute(request, handler.ExecuteAsync, handler.AuthorizeAsync);
        Task<AtomicCommandOutcome<SecurityDepositMutationResult>> RunRefundDeposit(
            RefundSecurityDepositCommand request, RefundSecurityDepositHandler handler) =>
            Execute(request, handler.ExecuteAsync, handler.AuthorizeAsync);
        Task<AtomicCommandOutcome<SecurityDepositMutationResult>> RunReverseDeposit(
            ReverseSecurityDepositEntryCommand request, ReverseSecurityDepositEntryHandler handler) =>
            Execute(request, handler.ExecuteAsync, handler.AuthorizeAsync);
    }

    private CompanionFailureInterceptor Failures =>
        _services!.GetRequiredService<CompanionFailureInterceptor>();

    private sealed class FixedTimeProvider(DateTime utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(utcNow);
    }

    private RentalCommandDbContext NewContext() => new(
        new DbContextOptionsBuilder<RentalCommandDbContext>()
            .UseNpgsql(_postgres!.GetConnectionString())
            .Options);

    private void SkipIfNoDocker() =>
        Skip.IfNot(_dockerAvailable, "Docker is required for tenant charge PostgreSQL tests.");

    private sealed class TestActor : ICurrentActor
    {
        public int? UserId => null;
        public string? ActorLabel => "integration:tenant-charge";
        public string? IpAddress => "127.0.0.1";
    }

    private sealed class CompanionFailureInterceptor : DbCommandInterceptor
    {
        private readonly ConcurrentQueue<string> _commands = new();

        public bool FailAtomicAudit { get; set; }
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
            MaybeFail(command.CommandText);
            return ValueTask.FromResult(result);
        }

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            MaybeFail(command.CommandText);
            return ValueTask.FromResult(result);
        }

        private void MaybeFail(string sql)
        {
            _commands.Enqueue(sql);
            if (FailAtomicAudit
                && sql.Contains("INSERT INTO \"AtomicAuditLogs\"", StringComparison.Ordinal))
            {
                throw new InvalidOperationException("injected tenant-charge audit failure");
            }
        }
    }

    private sealed record Scenario(
        int PortfolioId,
        int PropertyId,
        int AccountId,
        int OtherAccountId,
        int DepositAccountId,
        int UserId,
        Guid SessionId,
        int AccessContextId,
        long AccessRevision);
}
