using System.Collections.Concurrent;
using System.Data.Common;
using System.Reflection;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using RentalCommand.Api.Writes;
using RentalCommand.Core.Atomic;
using RentalCommand.Core.Automation;
using RentalCommand.Core.Authorization;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Interfaces;
using RentalCommand.Core.Navigation;
using RentalCommand.Core.Outbox;
using RentalCommand.Core.Payments;
using RentalCommand.Core.Time;
using RentalCommand.Data;
using RentalCommand.Data.Accounting;
using RentalCommand.Data.Atomic;
using RentalCommand.Data.Authorization;
using RentalCommand.Data.Payments;
using RentalCommand.Engine.Services;
using RentalCommand.Engine.Writes;
using Testcontainers.PostgreSql;
using Xunit;

namespace RentalCommand.IntegrationTests;

/// <summary>
/// PostgreSQL proof for the destructive scheduled-rent and late-fee cutover. Candidate generation,
/// proration, duplicate suppression, ordering, paging, and posting all execute in the set-based SQL
/// owned by the tenant-money persistence writer.
/// </summary>
public sealed class ScheduledTenantChargeAtomicCommandTests : IAsyncLifetime
{
    private static readonly AtomicJsonResultCodec<ApplyScheduledRentChargeBatchResult> Codec =
        new("scheduled-tenant-charges.rent.apply.v1");
    private static readonly AtomicJsonResultCodec<ApplyScheduledLateFeeChargeBatchResult> LateFeeCodec =
        new("scheduled-tenant-charges.late-fee.apply.v1");
    private static readonly AtomicJsonResultCodec<TenantChargeMutationResult> ChargeCodec =
        new("tenant-account.charge.mutation.v1");
    private static readonly DateTime FrozenNow =
        new(2026, 7, 15, 12, 0, 0, DateTimeKind.Utc);

    private PostgreSqlContainer? _postgres;
    private ServiceProvider? _services;
    private bool _dockerAvailable;

    public async Task InitializeAsync()
    {
        try
        {
            _postgres = new PostgreSqlBuilder()
                .WithImage("postgres:16-alpine")
                .WithDatabase("rentalcommand_scheduled_tenant_charges")
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
        services.AddSingleton<CompanionFailureInterceptor>();
        services.AddScoped<ICurrentActor, TestActor>();
        services.AddAtomicPersistenceKernel();
        services.AddScoped<IRequestWriteExecutor, RequestWriteExecutor>();
        services.AddScoped<IJobStepWriteExecutor, JobStepWriteExecutor>();
        services.AddDbContext<RentalCommandDbContext>((provider, options) =>
            options.UseNpgsql(_postgres!.GetConnectionString())
                .UseAtomicPersistenceKernel(provider)
                .AddInterceptors(
                    provider.GetRequiredService<CommandRecorder>(),
                    provider.GetRequiredService<CompanionFailureInterceptor>()));
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
        // canonical SQL objects used by rent and late-fee candidate generation.
        await db.Database.EnsureCreatedAsync();
        await db.Database.ExecuteSqlRawAsync(LeaseEffectiveClockSql.CreateEffectiveNowUtc);
        await db.Database.ExecuteSqlRawAsync(LeaseEffectiveClockSql.CreateBusinessDate);
        await db.Database.ExecuteSqlRawAsync(RelationshipAccessProjectionSql.Create);
        await db.Database.ExecuteSqlRawAsync(TenantChargeBalanceViewSql.Create);
        await db.Database.ExecuteSqlRawAsync(TenantAccountBalanceViewSql.Create);
    }

    public async Task DisposeAsync()
    {
        if (_services is not null) await _services.DisposeAsync();
        if (_postgres is not null) await _postgres.DisposeAsync();
    }

    [SkippableFact]
    public async Task RentBatch_ReplayAndConcurrentSweep_PostOneProratedEntryPerBusinessKey()
    {
        SkipIfNoDocker();
        var first = await SeedScenarioAsync("rent-replay", FrozenNow);
        await AddTenantAccessAsync(first, "rent-replay-cotenant", LeaseManagementPartyRole.CoTenant, FrozenNow);
        await AddTenantAccessAsync(
            first,
            "rent-replay-disabled",
            LeaseManagementPartyRole.Occupant,
            FrozenNow,
            enableInApp: false);
        await AddRevokedTenantAccessAsync(first, "rent-replay-revoked", FrozenNow);
        Recorder.Clear();
        var command = RentCommand("rent-replay");
        var identity = Identity("scheduled-tenant-charges.rent.apply", "rent-replay");

        var executed = await ExecuteAtomicAsync(identity, command, Codec);
        var replayed = await ExecuteAtomicAsync(identity, command, Codec);
        var laterSweep = await ExecuteAtomicAsync(
            Identity("scheduled-tenant-charges.rent.apply", "rent-replay-later"),
            RentCommand("rent-replay-later"),
            Codec);

        executed.Disposition.Should().Be(AtomicCommandDisposition.Executed);
        executed.Value.RentChargeCount.Should().Be(1);
        replayed.Disposition.Should().Be(AtomicCommandDisposition.Replayed);
        replayed.Value.Should().Be(executed.Value);
        laterSweep.Value.RentChargeCount.Should().Be(0);

        await using (var verify = NewContext())
        {
            var entry = await verify.TenantLedgerEntries.SingleAsync(row =>
                row.TenantAccountId == first.AccountId
                && row.EntryType == TenantLedgerEntryType.RentCharge);
            entry.Amount.Should().Be(2200m, "July 10 through July 31 is 22/31 of $3,100");
            entry.DueOn.Should().Be(new DateOnly(2026, 7, 10));
            entry.BusinessKey.Should().Be($"rent:{first.AgreementPublicId}:2026-07");
            (await verify.AtomicCommandReceipts.CountAsync(row =>
                row.CommandType == identity.CommandType
                && row.IdempotencyKey == identity.IdempotencyKey)).Should().Be(1);
            (await verify.AtomicAuditLogs.CountAsync(row =>
                row.CommandType == identity.CommandType
                && row.CommandIdempotencyKey == identity.IdempotencyKey
                && row.EntityType == nameof(TenantAccount)
                && row.EntityId == first.AccountId)).Should().Be(1);
            (await verify.OutboxMessages.CountAsync(row =>
                row.IdempotencyKey == OutboxIdempotency.Create(
                    "scheduled-tenant-charge",
                    $"{first.AccountId}:{entry.BusinessKey}"))).Should().Be(1);
            var notifications = await verify.Notifications
                .Where(row =>
                    row.PortfolioId == first.PortfolioId
                    && row.Type == "ScheduledRentCharge"
                    && row.RelatedEntityType == nameof(TenantLedgerEntry)
                    && row.RelatedEntityId == entry.Id)
                .OrderBy(row => row.UserId)
                .ToListAsync();
            notifications.Select(row => row.UserId).Should().Equal(
                first.UserId,
                first.CoTenantUserId!.Value,
                first.DisabledInAppUserId!.Value);
            var notificationIds = notifications.Select(row => row.Id).ToArray();
            var notificationAuditCounts = await verify.AtomicAuditLogs
                .Where(row =>
                    row.CommandType == identity.CommandType
                    && row.CommandIdempotencyKey == identity.IdempotencyKey
                    && row.EntityType == nameof(Notification)
                    && row.Operation == AuditLogOperation.Created
                    && notificationIds.Contains(row.EntityId))
                .GroupBy(row => row.EntityId)
                .Select(group => new { NotificationId = group.Key, Count = group.Count() })
                .ToListAsync();
            notificationAuditCounts.Should().HaveCount(notificationIds.Length);
            notificationAuditCounts.Should().OnlyContain(row => row.Count == 1,
                "each scheduled rent notification is a tracked auditable create and must not also stage a duplicate semantic create");
            (await verify.AtomicAuditLogs.CountAsync(row =>
                row.CommandType == identity.CommandType
                && row.CommandIdempotencyKey == identity.IdempotencyKey
                && row.EntityType == nameof(Notification)
                && row.Operation == AuditLogOperation.Created
                && row.ChangeReason == "Posted scheduled rent charge tenant notification.")).Should()
                .Be(notificationIds.Length);
            var notification = notifications.Single(row => row.UserId == first.UserId);
            var coTenantNotification = notifications.Single(row => row.UserId == first.CoTenantUserId.Value);
            coTenantNotification.NavigationAccessContextId.Should().Be(first.CoTenantAccessContextId!.Value);
            coTenantNotification.NavigationAccessRevision.Should().Be(first.CoTenantAccessRevision!.Value);
            notification.Title.Should().Be("Rent charge posted");
            notification.Message.Should().Contain("A rent charge");
            notification.NavigationExperience.Should().Be(NavigationExperience.Tenant);
            notification.NavigationDestination.Should().Be(NavigationDestination.TenantLedgerEntry);
            notification.NavigationAccessContextId.Should().Be(first.AccessContextId);
            notification.NavigationAccessRevision.Should().Be(first.AccessRevision);
            notification.NavigationResourceKind.Should().Be(nameof(TenantLedgerEntry));
            notification.NavigationResourceId.Should().Be((int)entry.Id);
            notification.NavigationParentResourceKind.Should().Be(nameof(TenantAccount));
            notification.NavigationParentResourceId.Should().Be(first.AccountId);
            (await verify.Notifications.CountAsync(row =>
                row.PortfolioId == first.PortfolioId && row.UserId == first.RevokedUserId)).Should().Be(0,
                "revoked tenant access must not receive charge notifications");
            var outboxTypes = await verify.OutboxMessages
                .Where(row => row.PortfolioId == first.PortfolioId
                    && row.IdempotencyKey.Contains("scheduled-rent-charge-notification"))
                .OrderBy(row => row.MessageType)
                .Select(row => row.MessageType)
                .ToListAsync();
            outboxTypes.Should().Equal(
                "data-update",
                "data-update",
                "data-update",
                "email",
                "email",
                "email",
                "push",
                "push",
                "push",
                "sms",
                "sms",
                "sms");
            (await verify.OutboxMessages.CountAsync(row =>
                row.PortfolioId == first.PortfolioId
                && row.MessageType == "email"
                && row.IdempotencyKey == OutboxIdempotency.Create(
                    "scheduled-rent-charge-notification",
                    $"{first.AccountId}:{entry.BusinessKey}:{first.DisabledInAppUserId!.Value}:email:{DestinationHash("tenant-access-rent-replay-disabled@example.test")}"))).Should().Be(1,
                "email delivery remains independently enabled when in-app is disabled");
            var pushPayloads = await verify.OutboxMessages
                .Where(row => row.PortfolioId == first.PortfolioId && row.MessageType == "push")
                .Select(row => row.Payload)
                .ToListAsync();
            pushPayloads.Should().OnlyContain(payload => !payload.Contains("2200", StringComparison.Ordinal));
            var smsPayloads = await verify.OutboxMessages
                .Where(row => row.PortfolioId == first.PortfolioId && row.MessageType == "sms")
                .Select(row => row.Payload)
                .ToListAsync();
            smsPayloads.Should().OnlyContain(payload => !payload.Contains("2200", StringComparison.Ordinal));
        }

        var raced = await SeedScenarioAsync("rent-race", FrozenNow);
        var raceCommandA = RentCommand("rent-race-a");
        var raceCommandB = RentCommand("rent-race-b");
        var outcomes = await Task.WhenAll(
            ExecuteAtomicAsync(
                Identity("scheduled-tenant-charges.rent.apply", "rent-race-a"),
                raceCommandA,
                Codec),
            ExecuteAtomicAsync(
                Identity("scheduled-tenant-charges.rent.apply", "rent-race-b"),
                raceCommandB,
                Codec));

        outcomes.Sum(outcome => outcome.Value.RentChargeCount).Should().Be(1);
        await using var raceVerify = NewContext();
        (await raceVerify.TenantLedgerEntries.CountAsync(row =>
            row.TenantAccountId == raced.AccountId
            && row.EntryType == TenantLedgerEntryType.RentCharge)).Should().Be(1);

        Recorder.Commands.Should().Contain(sql =>
            sql.Contains("generate_series", StringComparison.Ordinal)
            && sql.Contains("ON CONFLICT (\"TenantAccountId\", \"BusinessKey\") DO NOTHING", StringComparison.Ordinal)
            && sql.Contains("INSERT INTO \"TenantLedgerEntries\"", StringComparison.Ordinal));
    }

    [SkippableFact]
    public async Task RentBatch_TrackingStartModesUseLeaseStartForwardOnlyAndCustomCutoff()
    {
        SkipIfNoDocker();
        var leaseStart = new DateOnly(2026, 5, 10);
        var noCutoff = await SeedScenarioAsync("rent-no-cutoff", FrozenNow, leaseStart);
        var current = await SeedScenarioAsync(
            "rent-current",
            FrozenNow,
            leaseStart,
            new DateOnly(2026, 7, 15));
        var custom = await SeedScenarioAsync(
            "rent-custom",
            FrozenNow,
            leaseStart,
            new DateOnly(2026, 6, 20));

        var result = await ExecuteAtomicAsync(
            Identity("scheduled-tenant-charges.rent.apply", "rent-cutoffs"),
            RentCommand("rent-cutoffs"),
            Codec);

        result.Value.RentChargeCount.Should().Be(6);
        await using var verify = NewContext();
        var rows = await verify.TenantLedgerEntries
            .Where(row =>
                row.TenantAccountId == noCutoff.AccountId
                || row.TenantAccountId == current.AccountId
                || row.TenantAccountId == custom.AccountId)
            .OrderBy(row => row.TenantAccountId)
            .ThenBy(row => row.EffectiveOn)
            .ToListAsync();

        rows.Where(row => row.TenantAccountId == noCutoff.AccountId)
            .Select(row => (row.EffectiveOn, row.Amount))
            .Should().Equal([
                (new DateOnly(2026, 5, 10), 2200m),
                (new DateOnly(2026, 6, 1), 3100m),
                (new DateOnly(2026, 7, 1), 3100m)],
                "a null tracking start selects BackfillFromLeaseStart");
        rows.Where(row => row.TenantAccountId == current.AccountId)
            .Select(row => (row.EffectiveOn, row.Amount))
            .Should().Equal((new DateOnly(2026, 7, 15), 1700m));
        rows.Where(row => row.TenantAccountId == custom.AccountId)
            .Select(row => (row.EffectiveOn, row.Amount))
            .Should().Equal([
                (new DateOnly(2026, 6, 20), 1136.67m),
                (new DateOnly(2026, 7, 1), 3100m)],
                "a stored custom cutoff starts billing at that cutoff");
    }

    [SkippableFact]
    public async Task RentBatch_UsesFrozenBusinessClockForLedgerAuditNotificationAndOutboxTimestamps()
    {
        SkipIfNoDocker();
        var businessNow = new DateTime(2027, 1, 22, 12, 0, 0, DateTimeKind.Utc);
        var scenario = await SeedScenarioAsync(
            "rent-business-clock",
            businessNow,
            new DateOnly(2027, 1, 1),
            termEndOn: new DateOnly(2027, 12, 31));
        var identity = Identity("scheduled-tenant-charges.rent.apply", "rent-business-clock");
        var command = RentCommand("rent-business-clock", businessNow);
        Recorder.Clear();

        var result = await ExecuteAtomicAsync(identity, command, Codec);

        result.Value.RentChargeCount.Should().Be(1);
        await using var verify = NewContext();
        var entry = await verify.TenantLedgerEntries.SingleAsync(row =>
            row.TenantAccountId == scenario.AccountId
            && row.EntryType == TenantLedgerEntryType.RentCharge);
        entry.EffectiveOn.Should().Be(new DateOnly(2027, 1, 1));
        entry.PostedAtUtc.Should().Be(businessNow);

        var auditTimes = await verify.AtomicAuditLogs
            .Where(row => row.CommandType == identity.CommandType
                && row.CommandIdempotencyKey == identity.IdempotencyKey)
            .Select(row => row.Timestamp)
            .ToListAsync();
        auditTimes.Should().NotBeEmpty();
        auditTimes.Should().OnlyContain(timestamp => timestamp == businessNow);

        var notifications = await verify.Notifications
            .Where(row => row.PortfolioId == scenario.PortfolioId
                && row.RelatedEntityType == nameof(TenantLedgerEntry)
                && row.RelatedEntityId == entry.Id)
            .ToListAsync();
        notifications.Should().ContainSingle();
        notifications.Should().OnlyContain(notification =>
            notification.CreatedAt == businessNow
            && notification.NavigationExpiresAtUtc == businessNow.AddDays(30));

        var outboxRows = await verify.OutboxMessages
            .Where(row => row.PortfolioId == scenario.PortfolioId
                && row.CreatedAtUtc == businessNow)
            .ToListAsync();
        outboxRows.Should().NotBeEmpty();
        outboxRows.Should().OnlyContain(row =>
            row.CreatedAtUtc == businessNow
            && row.NextAttemptAtUtc == businessNow);

        Recorder.Commands.Should().Contain(sql =>
            sql.Contains("INSERT INTO \"TenantLedgerEntries\"", StringComparison.Ordinal)
            && !sql.Contains("clock_timestamp()", StringComparison.Ordinal));
    }

    [SkippableFact]
    public async Task RentBatch_LeavesUnappliedReceiptUnallocatedWhenPostingNewCharge()
    {
        SkipIfNoDocker();
        var receiptNow = new DateTime(2027, 1, 4, 15, 0, 0, DateTimeKind.Utc);
        var chargeNow = new DateTime(2027, 1, 31, 13, 0, 0, DateTimeKind.Utc);
        var scenario = await SeedScenarioAsync(
            "advance-credit",
            receiptNow,
            new DateOnly(2027, 1, 1),
            termEndOn: new DateOnly(2027, 12, 31),
            rentDueDay: 31,
            baseRentAmount: 1225m);
        var receiptId = await SeedAdvanceReceiptAsync(
            scenario,
            1225m,
            new DateOnly(2027, 1, 4),
            "advance-credit:ash-duplex-a:2027-01",
            receiptNow,
            withProviderPaymentAttempt: true);
        await FreezeAtAsync(chargeNow);
        var identity = Identity("scheduled-tenant-charges.rent.apply", "advance-credit");
        var command = RentCommand("advance-credit", chargeNow);
        Recorder.Clear();

        var executed = await ExecuteAtomicAsync(identity, command, Codec);
        var replayed = await ExecuteAtomicAsync(identity, command, Codec);
        var laterSweep = await ExecuteAtomicAsync(
            Identity("scheduled-tenant-charges.rent.apply", "advance-credit-later"),
            RentCommand("advance-credit-later", chargeNow),
            Codec);

        executed.Disposition.Should().Be(AtomicCommandDisposition.Executed);
        executed.Value.RentChargeCount.Should().Be(1);
        replayed.Disposition.Should().Be(AtomicCommandDisposition.Replayed);
        replayed.Value.Should().Be(executed.Value);
        laterSweep.Value.RentChargeCount.Should().Be(0);
        await using var verify = NewContext();
        var charge = await verify.TenantLedgerEntries.SingleAsync(row =>
            row.TenantAccountId == scenario.AccountId
            && row.EntryType == TenantLedgerEntryType.RentCharge);
        charge.Amount.Should().Be(1225m);
        charge.EffectiveOn.Should().Be(new DateOnly(2027, 1, 31));
        charge.DueOn.Should().Be(new DateOnly(2027, 1, 31));

        (await verify.TenantLedgerAllocations.CountAsync(row =>
            row.TenantAccountId == scenario.AccountId
            && row.DebitEntryId == charge.Id
            && row.CreditEntryId == receiptId)).Should().Be(0,
                "scheduled rent posting must not allocate an unapplied receipt");
        var balance = await verify.TenantChargeBalanceProjections.SingleAsync(row =>
            row.TenantLedgerEntryId == charge.Id);
        balance.OpenAmount.Should().Be(1225m);
        var receipt = await verify.TenantLedgerEntries.SingleAsync(row => row.Id == receiptId);
        (await verify.TenantPaymentAttempts.SingleAsync(row =>
            row.Id == receipt.ProviderPaymentAttemptId)).AttemptType
            .Should().Be(TenantPaymentAttemptType.UnappliedReceipt);
        var auditPayloads = await verify.AtomicAuditLogs
            .Where(row =>
            row.CommandType == identity.CommandType
            && row.CommandIdempotencyKey == identity.IdempotencyKey
            && row.EntityType == nameof(TenantAccount)
            && row.EntityId == scenario.AccountId)
            .Select(row => row.NewValues)
            .ToListAsync();
        auditPayloads.Should().ContainSingle();
        (await verify.OutboxMessages.CountAsync(row =>
            row.IdempotencyKey == OutboxIdempotency.Create(
                "scheduled-tenant-charge",
                $"{scenario.AccountId}:{charge.BusinessKey}"))).Should().Be(1);

        Recorder.Commands.Should().NotContain(sql =>
            sql.Contains("INSERT INTO \"TenantLedgerAllocations\"", StringComparison.Ordinal));
    }

    [SkippableFact]
    public async Task RentBatch_UnappliedReceiptRemainsUnallocatedAcrossRollbackAndRecovery()
    {
        SkipIfNoDocker();
        var receiptNow = new DateTime(2027, 1, 4, 15, 0, 0, DateTimeKind.Utc);
        var chargeNow = new DateTime(2027, 1, 31, 13, 0, 0, DateTimeKind.Utc);
        var scenario = await SeedScenarioAsync(
            "advance-credit-rollback",
            receiptNow,
            new DateOnly(2027, 1, 1),
            termEndOn: new DateOnly(2027, 12, 31),
            rentDueDay: 31,
            baseRentAmount: 1225m);
        var receiptId = await SeedAdvanceReceiptAsync(
            scenario,
            1225m,
            new DateOnly(2027, 1, 4),
            "advance-credit-rollback:ash-duplex-a:2027-01",
            receiptNow);
        await FreezeAtAsync(chargeNow);
        var identity = Identity("scheduled-tenant-charges.rent.apply", "advance-credit-rollback");
        var command = RentCommand("advance-credit-rollback", chargeNow);
        Failures.FailAtomicAudit = true;

        var failure = await FluentActions
            .Invoking(() => ExecuteAtomicAsync(identity, command, Codec))
            .Should().ThrowAsync<DbUpdateException>();
        failure.WithInnerException<InvalidOperationException>()
            .WithMessage("injected scheduled tenant-charge audit failure");

        await using (var failed = NewContext())
        {
            (await failed.TenantLedgerEntries.CountAsync(row =>
                row.TenantAccountId == scenario.AccountId
                && row.EntryType == TenantLedgerEntryType.RentCharge)).Should().Be(0);
            (await failed.TenantLedgerEntries.CountAsync(row => row.Id == receiptId)).Should().Be(1,
                "the pre-existing advance receipt is outside the failed scheduled-charge transaction");
            (await failed.TenantLedgerAllocations.CountAsync(row =>
                row.TenantAccountId == scenario.AccountId)).Should().Be(0);
            (await failed.AtomicCommandReceipts.CountAsync(row =>
                row.CommandType == identity.CommandType
                && row.IdempotencyKey == identity.IdempotencyKey)).Should().Be(0);
            (await failed.OutboxMessages.CountAsync(row =>
                row.PortfolioId == scenario.PortfolioId)).Should().Be(0);
        }

        Failures.FailAtomicAudit = false;
        var recovered = await ExecuteAtomicAsync(identity, command, Codec);
        recovered.Disposition.Should().Be(AtomicCommandDisposition.Executed);
        recovered.Value.RentChargeCount.Should().Be(1);

        await using var verify = NewContext();
        var charge = await verify.TenantLedgerEntries.SingleAsync(row =>
            row.TenantAccountId == scenario.AccountId
            && row.EntryType == TenantLedgerEntryType.RentCharge);
        (await verify.TenantLedgerAllocations.CountAsync(row =>
            row.TenantAccountId == scenario.AccountId
            && row.DebitEntryId == charge.Id
            && row.CreditEntryId == receiptId
            && row.Amount == 1225m)).Should().Be(0);
    }

    [SkippableFact]
    public async Task RentBatch_DoesNotBackfillExistingChargeFromImportedReceipt()
    {
        SkipIfNoDocker();
        var receiptNow = new DateTime(2027, 1, 4, 15, 0, 0, DateTimeKind.Utc);
        var chargeNow = new DateTime(2027, 1, 31, 13, 0, 0, DateTimeKind.Utc);
        var scenario = await SeedScenarioAsync(
            "advance-credit-existing",
            receiptNow,
            new DateOnly(2027, 1, 1),
            termEndOn: new DateOnly(2027, 12, 31),
            rentDueDay: 31,
            baseRentAmount: 1225m);
        var receiptId = await SeedAdvanceReceiptAsync(
            scenario,
            1225m,
            new DateOnly(2027, 1, 4),
            "advance-credit-existing:ash-duplex-a:2027-01",
            receiptNow,
            withProviderPaymentAttempt: true,
            attemptType: TenantPaymentAttemptType.ImportedReceipt);
        var chargeId = await SeedExistingRentChargeAsync(
            scenario,
            1225m,
            new DateOnly(2027, 1, 31),
            $"rent:{scenario.AgreementPublicId}:2027-01",
            chargeNow);
        await FreezeAtAsync(chargeNow);
        var identity = Identity("scheduled-tenant-charges.rent.apply", "advance-credit-existing");
        var command = RentCommand("advance-credit-existing", chargeNow);

        var executed = await ExecuteAtomicAsync(identity, command, Codec);
        var replayed = await ExecuteAtomicAsync(identity, command, Codec);
        var laterSweep = await ExecuteAtomicAsync(
            Identity("scheduled-tenant-charges.rent.apply", "advance-credit-existing-later"),
            RentCommand("advance-credit-existing-later", chargeNow),
            Codec);

        executed.Value.RentChargeCount.Should().Be(0);
        replayed.Disposition.Should().Be(AtomicCommandDisposition.Replayed);
        laterSweep.Value.RentChargeCount.Should().Be(0);
        await using var verify = NewContext();
        (await verify.TenantLedgerAllocations.CountAsync(row =>
            row.TenantAccountId == scenario.AccountId
            && row.DebitEntryId == chargeId
            && row.CreditEntryId == receiptId)).Should().Be(0,
                "scheduled billing must not backfill an existing charge from an imported receipt");
        (await verify.TenantLedgerEntries.CountAsync(row =>
            row.TenantAccountId == scenario.AccountId
            && row.EntryType == TenantLedgerEntryType.RentCharge)).Should().Be(1);
        (await verify.Notifications.CountAsync(row =>
            row.PortfolioId == scenario.PortfolioId
            && row.Type == "ScheduledRentCharge")).Should().Be(0,
            "an existing charge must not produce a second charge-posted notification");
        var receipt = await verify.TenantLedgerEntries.SingleAsync(row => row.Id == receiptId);
        (await verify.TenantPaymentAttempts.SingleAsync(row =>
            row.Id == receipt.ProviderPaymentAttemptId)).AttemptType
            .Should().Be(TenantPaymentAttemptType.ImportedReceipt);
        (await verify.AtomicAuditLogs.CountAsync(row =>
            row.CommandType == identity.CommandType
            && row.CommandIdempotencyKey == identity.IdempotencyKey)).Should().Be(0);
    }

    [SkippableFact]
    public async Task RentBatch_DoesNotAllocateFullyRefundedAdvanceReceiptToExistingDueCharge()
    {
        SkipIfNoDocker();
        var receiptNow = new DateTime(2027, 1, 5, 15, 0, 0, DateTimeKind.Utc);
        var chargeNow = new DateTime(2027, 2, 1, 5, 0, 0, DateTimeKind.Utc);
        var scenario = await SeedScenarioAsync(
            "refunded-advance-credit",
            receiptNow,
            new DateOnly(2027, 1, 1),
            rentTrackingStartOn: new DateOnly(2027, 2, 1),
            termEndOn: new DateOnly(2027, 12, 31),
            rentDueDay: 1,
            baseRentAmount: 1200m);
        var receiptId = await SeedAdvanceReceiptAsync(
            scenario,
            1200m,
            new DateOnly(2027, 1, 5),
            "refunded-advance-credit:receipt",
            receiptNow,
            withProviderPaymentAttempt: true);
        await SeedFullRefundAsync(
            scenario,
            receiptId,
            1200m,
            new DateOnly(2027, 1, 6),
            receiptNow.AddDays(1));
        var chargeId = await SeedExistingRentChargeAsync(
            scenario,
            1200m,
            new DateOnly(2027, 2, 1),
            $"rent:{scenario.AgreementPublicId}:2027-02",
            chargeNow);
        await FreezeAtAsync(chargeNow);
        var identity = Identity(
            "scheduled-tenant-charges.rent.apply",
            "refunded-advance-credit");
        Recorder.Clear();

        var result = await ExecuteAtomicAsync(
            identity,
            RentCommand("refunded-advance-credit", chargeNow),
            Codec);

        result.Disposition.Should().Be(AtomicCommandDisposition.Executed);
        result.Value.RentChargeCount.Should().Be(0);
        await using var verify = NewContext();
        (await verify.TenantLedgerAllocations.CountAsync(row =>
            row.TenantAccountId == scenario.AccountId
            && row.DebitEntryId == chargeId
            && row.CreditEntryId == receiptId)).Should().Be(0);
        (await verify.AtomicAuditLogs.CountAsync(row =>
            row.CommandType == identity.CommandType
            && row.CommandIdempotencyKey == identity.IdempotencyKey)).Should().Be(0);
        Recorder.Commands.Should().NotContain(sql =>
            sql.Contains("TenantPaymentAttempts", StringComparison.Ordinal)
            || sql.Contains("TenantLedgerAllocations", StringComparison.Ordinal));
    }

    [SkippableFact]
    public async Task RentBatch_RunOnceDoesNotBackfillExistingChargeWhenCandidateAmountDiffers()
    {
        SkipIfNoDocker();
        var receiptNow = new DateTime(2027, 1, 4, 15, 0, 0, DateTimeKind.Utc);
        var chargeNow = new DateTime(2027, 1, 31, 5, 0, 0, DateTimeKind.Utc);
        var scenario = await SeedScenarioAsync(
            "advance-credit-existing-amount-diff",
            receiptNow,
            new DateOnly(2026, 2, 1),
            new DateOnly(2027, 1, 31),
            termEndOn: new DateOnly(2027, 12, 31),
            rentDueDay: 31,
            baseRentAmount: 1225m);
        var receiptId = await SeedAdvanceReceiptAsync(
            scenario,
            1225m,
            new DateOnly(2027, 1, 4),
            "manual-receipt:2a5-integration",
            receiptNow,
            withProviderPaymentAttempt: true,
            attemptType: TenantPaymentAttemptType.ImportedReceipt);
        var chargeId = await SeedExistingRentChargeAsync(
            scenario,
            1225m,
            new DateOnly(2027, 1, 31),
            $"rent:{scenario.AgreementPublicId}:2027-01",
            chargeNow);
        await FreezeAtAsync(chargeNow, "America/New_York");
        await using var atomicScope = _services!.CreateAsyncScope();
        var service = new RentChargeService(
            atomicScope.ServiceProvider.GetRequiredService<IJobStepWriteExecutor>(),
            atomicScope.ServiceProvider.GetRequiredService<RentalCommandDbContext>(),
            new FixedTimeProvider(chargeNow),
            NullLogger<RentChargeService>.Instance);

        await using (var before = NewContext())
        {
            var receipt = await before.TenantLedgerEntries.SingleAsync(row => row.Id == receiptId);
            receipt.EntryType.Should().Be(TenantLedgerEntryType.PaymentReceipt);
            receipt.Direction.Should().Be(TenantLedgerDirection.Credit);
            receipt.ProviderPaymentAttemptId.Should().NotBeNull();
            receipt.EffectiveOn.Should().Be(new DateOnly(2027, 1, 4));
            var account = await before.TenantAccounts.SingleAsync(row => row.Id == scenario.AccountId);
            account.RentTrackingStartOn.Should().Be(new DateOnly(2027, 1, 31),
                "an existing committed full-period charge remains the recoverable debit even if a later cutoff would now recompute a prorated candidate");
            var openCharge = await before.TenantChargeBalanceProjections.SingleAsync(row =>
                row.TenantLedgerEntryId == chargeId);
            openCharge.OpenAmount.Should().Be(1225m);
            (await before.TenantLedgerAllocations.CountAsync(row =>
                row.TenantAccountId == scenario.AccountId)).Should().Be(0);
        }

        Recorder.Clear();
        var created = await service.GenerateAsync();
        var replayCreated = await service.GenerateAsync();

        created.Should().Be(0);
        replayCreated.Should().Be(0);
        await using var verify = NewContext();
        (await verify.TenantLedgerAllocations.CountAsync(row =>
            row.TenantAccountId == scenario.AccountId
            && row.DebitEntryId == chargeId
            && row.CreditEntryId == receiptId)).Should().Be(0);
        var balance = await verify.TenantChargeBalanceProjections.SingleAsync(row =>
            row.TenantLedgerEntryId == chargeId);
        balance.OpenAmount.Should().Be(1225m);
        (await verify.TenantLedgerEntries.CountAsync(row =>
            row.TenantAccountId == scenario.AccountId
            && row.EntryType == TenantLedgerEntryType.RentCharge)).Should().Be(1);
        (await verify.Notifications.CountAsync(row =>
            row.PortfolioId == scenario.PortfolioId
            && row.Type == "ScheduledRentCharge")).Should().Be(0,
            "an existing charge must not produce a second charge-posted notification");
        (await verify.AtomicAuditLogs.CountAsync(row =>
            row.CommandType == "scheduled-tenant-charges.rent.apply")).Should().Be(0);
    }

    [SkippableFact]
    public async Task RentBatch_RunOnceLeavesDepositReceiptUnallocatedAndExistingChargeOpen()
    {
        SkipIfNoDocker();
        var receiptNow = new DateTime(2027, 1, 4, 15, 0, 0, DateTimeKind.Utc);
        var chargeNow = new DateTime(2027, 1, 31, 5, 0, 0, DateTimeKind.Utc);
        var scenario = await SeedScenarioAsync(
            "advance-credit-existing-exact-row",
            receiptNow,
            new DateOnly(2026, 2, 1),
            new DateOnly(2027, 1, 1),
            termEndOn: new DateOnly(2027, 12, 31),
            rentDueDay: 31,
            baseRentAmount: 1225m);
        var receiptId = await SeedAdvanceReceiptAsync(
            scenario,
            1225m,
            new DateOnly(2027, 1, 4),
            "manual-receipt:2a5-exact",
            receiptNow,
            withProviderPaymentAttempt: true,
            attemptType: TenantPaymentAttemptType.DepositReceipt);
        var chargeId = await SeedExistingRentChargeAsync(
            scenario,
            1225m,
            new DateOnly(2027, 1, 31),
            $"rent:{scenario.AgreementPublicId}:2027-01",
            chargeNow);
        await FreezeAtAsync(chargeNow, "America/New_York");
        await using var atomicScope = _services!.CreateAsyncScope();
        var service = new RentChargeService(
            atomicScope.ServiceProvider.GetRequiredService<IJobStepWriteExecutor>(),
            atomicScope.ServiceProvider.GetRequiredService<RentalCommandDbContext>(),
            new FixedTimeProvider(chargeNow),
            NullLogger<RentChargeService>.Instance);

        await using (var before = NewContext())
        {
            var receipt = await before.TenantLedgerEntries.SingleAsync(row => row.Id == receiptId);
            receipt.EntryType.Should().Be(TenantLedgerEntryType.PaymentReceipt);
            receipt.Direction.Should().Be(TenantLedgerDirection.Credit);
            receipt.Amount.Should().Be(1225m);
            receipt.EffectiveOn.Should().Be(new DateOnly(2027, 1, 4));
            receipt.DueOn.Should().BeNull();
            receipt.BusinessKey.Should().Be("manual-receipt:2a5-exact");
            receipt.ProviderPaymentAttemptId.Should().NotBeNull();
            var charge = await before.TenantLedgerEntries.SingleAsync(row => row.Id == chargeId);
            charge.EntryType.Should().Be(TenantLedgerEntryType.RentCharge);
            charge.Direction.Should().Be(TenantLedgerDirection.Debit);
            charge.Amount.Should().Be(1225m);
            charge.EffectiveOn.Should().Be(new DateOnly(2027, 1, 31));
            charge.DueOn.Should().Be(new DateOnly(2027, 1, 31));
            charge.BusinessKey.Should().Be($"rent:{scenario.AgreementPublicId}:2027-01");
            var account = await before.TenantAccounts.SingleAsync(row => row.Id == scenario.AccountId);
            account.RentTrackingStartOn.Should().Be(new DateOnly(2027, 1, 1));
            var openCharge = await before.TenantChargeBalanceProjections.SingleAsync(row =>
                row.TenantLedgerEntryId == chargeId);
            openCharge.OpenAmount.Should().Be(1225m);
            openCharge.BusinessDate.Should().Be(new DateOnly(2027, 1, 31));
            (await before.TenantLedgerAllocations.CountAsync(row =>
                row.TenantAccountId == scenario.AccountId)).Should().Be(0);
            (await before.SecurityDepositEntries.CountAsync(row =>
                row.PortfolioId == scenario.PortfolioId
                && row.TenantLedgerEntryId == receiptId
                && row.EntryType == SecurityDepositEntryType.Receipt)).Should().Be(1);
        }

        Recorder.Clear();
        var created = await service.GenerateAsync();
        var replayCreated = await service.GenerateAsync();

        created.Should().Be(0);
        replayCreated.Should().Be(0);
        await using var verify = NewContext();
        (await verify.TenantLedgerAllocations.CountAsync(row =>
            row.TenantAccountId == scenario.AccountId
            && row.DebitEntryId == chargeId
            && row.CreditEntryId == receiptId)).Should().Be(0);
        var balance = await verify.TenantChargeBalanceProjections.SingleAsync(row =>
            row.TenantLedgerEntryId == chargeId);
        balance.OpenAmount.Should().Be(1225m);
        (await verify.TenantLedgerEntries.CountAsync(row =>
            row.TenantAccountId == scenario.AccountId
            && row.EntryType == TenantLedgerEntryType.RentCharge)).Should().Be(1);
        (await verify.Notifications.CountAsync(row =>
            row.PortfolioId == scenario.PortfolioId
            && row.Type == "ScheduledRentCharge")).Should().Be(0,
            "an existing charge must not produce a second charge-posted notification");
        (await verify.AtomicAuditLogs.CountAsync(row =>
            row.CommandType == "scheduled-tenant-charges.rent.apply")).Should().Be(0);
        Recorder.Commands.Should().NotContain(sql =>
            sql.Contains("TenantLedgerAllocations", StringComparison.Ordinal)
            || sql.Contains("PaymentReceipt", StringComparison.Ordinal));
    }

    [SkippableFact]
    public async Task RentBatch_DoesNotPostNextMonthChargeBeforeFrozenBusinessDueDate()
    {
        SkipIfNoDocker();
        var january29 = new DateTime(2027, 1, 29, 14, 0, 0, DateTimeKind.Utc);
        var february1 = new DateTime(2027, 2, 1, 14, 0, 0, DateTimeKind.Utc);
        var scenario = await SeedScenarioAsync(
            "rent-month-boundary",
            january29,
            new DateOnly(2027, 1, 1),
            new DateOnly(2027, 2, 1),
            new DateOnly(2027, 12, 31));
        await SetRentChargeLeadDaysAsync(scenario.PortfolioId, 5, january29);
        var identity = Identity("scheduled-tenant-charges.rent.apply", "rent-month-boundary-jan29");
        var command = RentCommand("rent-month-boundary-jan29", january29);
        Recorder.Clear();

        var early = await ExecuteAtomicAsync(identity, command, Codec);
        var earlyReplay = await ExecuteAtomicAsync(identity, command, Codec);
        var earlyLaterSweep = await ExecuteAtomicAsync(
            Identity("scheduled-tenant-charges.rent.apply", "rent-month-boundary-jan29-later"),
            RentCommand("rent-month-boundary-jan29-later", january29),
            Codec);

        early.Value.RentChargeCount.Should().Be(0,
            "tenant-notice candidates may drive reminders, but ledger charges must not post before the due date");
        earlyReplay.Disposition.Should().Be(AtomicCommandDisposition.Replayed);
        earlyLaterSweep.Value.RentChargeCount.Should().Be(0);
        await using (var earlyVerify = NewContext())
        {
            (await earlyVerify.TenantLedgerEntries.CountAsync(row =>
                row.TenantAccountId == scenario.AccountId
                && row.EntryType == TenantLedgerEntryType.RentCharge)).Should().Be(0);
            (await earlyVerify.Notifications.CountAsync(row =>
                row.PortfolioId == scenario.PortfolioId
                && row.UserId == scenario.UserId
                && row.Type == "ScheduledRentDueReminder:2027-02")).Should().Be(0);
            (await earlyVerify.OutboxMessages.CountAsync(row =>
                row.PortfolioId == scenario.PortfolioId
                && row.IdempotencyKey.Contains("scheduled-rent-due-reminder"))).Should().Be(0);
        }

        await FreezeAtAsync(february1);
        var dueIdentity = Identity("scheduled-tenant-charges.rent.apply", "rent-month-boundary-feb1");
        var dueCommand = RentCommand("rent-month-boundary-feb1", february1);
        var due = await ExecuteAtomicAsync(dueIdentity, dueCommand, Codec);
        var replay = await ExecuteAtomicAsync(dueIdentity, dueCommand, Codec);
        var laterSweep = await ExecuteAtomicAsync(
            Identity("scheduled-tenant-charges.rent.apply", "rent-month-boundary-feb1-later"),
            RentCommand("rent-month-boundary-feb1-later", february1),
            Codec);

        due.Disposition.Should().Be(AtomicCommandDisposition.Executed);
        due.Value.RentChargeCount.Should().Be(1);
        replay.Disposition.Should().Be(AtomicCommandDisposition.Replayed);
        replay.Value.Should().Be(due.Value);
        laterSweep.Value.RentChargeCount.Should().Be(0);

        await using var verify = NewContext();
        var entry = await verify.TenantLedgerEntries.SingleAsync(row =>
            row.TenantAccountId == scenario.AccountId
            && row.EntryType == TenantLedgerEntryType.RentCharge);
        entry.EffectiveOn.Should().Be(new DateOnly(2027, 2, 1));
        entry.DueOn.Should().Be(new DateOnly(2027, 2, 1));
        entry.PostedAtUtc.Should().Be(february1);
        entry.BusinessKey.Should().Be($"rent:{scenario.AgreementPublicId}:2027-02");

        Recorder.Commands.Should().Contain(sql =>
            sql.Contains("generate_series", StringComparison.Ordinal)
            && sql.Contains("INSERT INTO \"TenantLedgerEntries\"", StringComparison.Ordinal)
            && !sql.Contains("\"RentChargeLeadDays\"", StringComparison.Ordinal));
    }

    [SkippableFact]
    public async Task RentReminder_DirectScheduledChargeNotificationPathIsRetired()
    {
        SkipIfNoDocker();
        var january29 = new DateTime(2027, 1, 29, 14, 0, 0, DateTimeKind.Utc);
        var scenario = await SeedScenarioAsync(
            "rent-reminder-rollback",
            january29,
            new DateOnly(2027, 1, 1),
            new DateOnly(2027, 2, 1),
            new DateOnly(2027, 12, 31));
        await SetRentChargeLeadDaysAsync(scenario.PortfolioId, 5, january29);
        var identity = Identity(
            "scheduled-tenant-charges.rent.apply",
            "rent-reminder-retired");
        var command = RentCommand("rent-reminder-retired", january29);
        Failures.FailNotifications = true;
        AtomicCommandOutcome<ApplyScheduledRentChargeBatchResult> result;
        try
        {
            result = await ExecuteAtomicAsync(identity, command, Codec);
        }
        finally
        {
            Failures.FailNotifications = false;
        }

        result.Disposition.Should().Be(AtomicCommandDisposition.Executed);
        result.Value.RentChargeCount.Should().Be(0);

        await using var verify = NewContext();
        (await verify.TenantLedgerEntries.CountAsync(row =>
            row.TenantAccountId == scenario.AccountId)).Should().Be(0);
        (await verify.Notifications.CountAsync(row =>
            row.PortfolioId == scenario.PortfolioId
            && row.Type == "ScheduledRentDueReminder:2027-02")).Should().Be(0);
        (await verify.OutboxMessages.CountAsync(row =>
            row.PortfolioId == scenario.PortfolioId
            && row.IdempotencyKey.Contains("scheduled-rent-due-reminder"))).Should().Be(0);
    }

    [SkippableFact]
    public async Task RentReminder_DirectScheduledChargePathDoesNotProjectAnyPortfolio()
    {
        SkipIfNoDocker();
        var january29 = new DateTime(2027, 1, 29, 14, 0, 0, DateTimeKind.Utc);
        var first = await SeedScenarioAsync(
            "rent-reminder-portfolio-a",
            january29,
            new DateOnly(2027, 1, 1),
            new DateOnly(2027, 2, 1),
            new DateOnly(2027, 12, 31));
        var second = await SeedScenarioAsync(
            "rent-reminder-portfolio-b",
            january29,
            new DateOnly(2027, 1, 1),
            new DateOnly(2027, 2, 1),
            new DateOnly(2027, 12, 31),
            forceNewPortfolio: true);
        await SetRentChargeLeadDaysAsync(first.PortfolioId, 5, january29);
        await SetRentChargeLeadDaysAsync(second.PortfolioId, 5, january29);

        var result = await ExecuteAtomicAsync(
            Identity("scheduled-tenant-charges.rent.apply", "rent-reminder-two-portfolios"),
            RentCommand("rent-reminder-two-portfolios", january29),
            Codec);

        result.Value.RentChargeCount.Should().Be(0);
        await using var verify = NewContext();
        (await verify.Notifications.CountAsync(row =>
            row.Type == "ScheduledRentDueReminder:2027-02"
            && (row.PortfolioId == first.PortfolioId
                || row.PortfolioId == second.PortfolioId))).Should().Be(0);
        (await verify.OutboxMessages.CountAsync(row =>
            row.IdempotencyKey.Contains("scheduled-rent-due-reminder")
            && (row.PortfolioId == first.PortfolioId
                || row.PortfolioId == second.PortfolioId))).Should().Be(0);
    }

    [SkippableFact]
    public async Task BalanceViews_HideLegacyFutureEffectiveRentUntilItsBusinessDate()
    {
        SkipIfNoDocker();
        var january29 = new DateTime(2027, 1, 29, 14, 0, 0, DateTimeKind.Utc);
        var scenario = await SeedScenarioAsync(
            "legacy-future-rent-as-of",
            january29,
            new DateOnly(2027, 1, 1),
            new DateOnly(2027, 2, 1),
            new DateOnly(2027, 12, 31));
        await using (var seed = NewContext())
        {
            seed.TenantLedgerEntries.Add(new TenantLedgerEntry
            {
                PortfolioId = scenario.PortfolioId,
                TenantAccountId = scenario.AccountId,
                LeaseAgreementId = scenario.AgreementId,
                EntryType = TenantLedgerEntryType.RentCharge,
                Direction = TenantLedgerDirection.Debit,
                Amount = 3100m,
                Currency = "USD",
                EffectiveOn = new DateOnly(2027, 2, 1),
                DueOn = new DateOnly(2027, 2, 1),
                PostedAtUtc = january29,
                Description = "Legacy future-effective rent",
                BusinessKey = $"rent:{scenario.AgreementPublicId}:2027-02",
                CreatedByUserId = scenario.UserId,
            });
            await seed.SaveChangesAsync();
        }

        await using (var january = NewContext())
        {
            (await january.TenantChargeBalanceProjections.CountAsync(row =>
                row.PortfolioId == scenario.PortfolioId
                && row.TenantAccountId == scenario.AccountId)).Should().Be(0);
            var balance = await january.TenantAccountBalanceProjections.SingleAsync(row =>
                row.PortfolioId == scenario.PortfolioId
                && row.TenantAccountId == scenario.AccountId);
            balance.ReceivableBalance.Should().Be(0m);
            balance.TotalDebits.Should().Be(0m);
        }

        await FreezeAtAsync(new DateTime(2027, 2, 1, 14, 0, 0, DateTimeKind.Utc));
        await using var february = NewContext();
        var charge = await february.TenantChargeBalanceProjections.SingleAsync(row =>
            row.PortfolioId == scenario.PortfolioId
            && row.TenantAccountId == scenario.AccountId);
        charge.OpenAmount.Should().Be(3100m);
        var februaryBalance = await february.TenantAccountBalanceProjections.SingleAsync(row =>
            row.PortfolioId == scenario.PortfolioId
            && row.TenantAccountId == scenario.AccountId);
        februaryBalance.ReceivableBalance.Should().Be(3100m);
    }

    [SkippableFact]
    public async Task LateFeeBatch_UsesOpenChargeBusinessDateGraceAndStateCap_AndDoesNotMutateRent()
    {
        SkipIfNoDocker();
        var scenario = await SeedScenarioAsync("late-fee", FrozenNow);
        await ExecuteAtomicAsync(
            Identity("scheduled-tenant-charges.rent.apply", "late-fee-rent"),
            RentCommand("late-fee-rent"),
            Codec);
        await FreezeAtAsync(new DateTime(2026, 7, 20, 12, 0, 0, DateTimeKind.Utc));

        var caps = "[{\"State\":\"OH\",\"MaxFlat\":500,\"MaxPercentOfRent\":5}]";
        var command = new ApplyScheduledLateFeeChargeBatchCommand(
            Guid.NewGuid(),
            new DateTime(2026, 7, 20, 12, 0, 0, DateTimeKind.Utc),
            200,
            caps);
        var first = await ExecuteAtomicAsync(
            Identity("scheduled-tenant-charges.late-fee.apply", "late-fee-first"),
            command,
            LateFeeCodec);
        var duplicate = await ExecuteAtomicAsync(
            Identity("scheduled-tenant-charges.late-fee.apply", "late-fee-second"),
            command with { RunToken = Guid.NewGuid() },
            LateFeeCodec);

        first.Value.LateFeeChargeCount.Should().Be(1);
        duplicate.Value.LateFeeChargeCount.Should().Be(0);
        await using var verify = NewContext();
        var rows = await verify.TenantLedgerEntries
            .Where(row => row.TenantAccountId == scenario.AccountId)
            .OrderBy(row => row.Id)
            .ToListAsync();
        rows.Should().HaveCount(2);
        rows[0].EntryType.Should().Be(TenantLedgerEntryType.RentCharge);
        rows[0].Amount.Should().Be(2200m);
        rows[1].EntryType.Should().Be(TenantLedgerEntryType.LateFeeCharge);
        rows[1].Amount.Should().Be(110m, "the percent cap applies to the prorated rent charge");
        rows[1].EffectiveOn.Should().Be(new DateOnly(2026, 7, 20));
        rows[1].BusinessKey.Should().Be($"late-fee:{rows[0].BusinessKey}");
    }

    [SkippableFact]
    public async Task FutureDatedReceiptAllocation_IsIgnoredUntilReceiptDate_AndLateFeeStillSeesCharge()
    {
        SkipIfNoDocker();
        var todayUtc = new DateTime(2027, 1, 20, 12, 0, 0, DateTimeKind.Utc);
        var receiptDate = new DateOnly(2027, 1, 25);
        var scenario = await SeedScenarioAsync(
            "h6-future-receipt",
            todayUtc,
            new DateOnly(2027, 1, 1),
            new DateOnly(2027, 1, 1),
            new DateOnly(2027, 12, 31),
            rentDueDay: 1,
            baseRentAmount: 100m);
        var chargeId = await SeedExistingRentChargeAsync(
            scenario,
            100m,
            new DateOnly(2027, 1, 1),
            "rent:h6-future-receipt:2027-01",
            new DateTime(2027, 1, 1, 12, 0, 0, DateTimeKind.Utc));
        var receiptId = await SeedAdvanceReceiptAsync(
            scenario,
            100m,
            receiptDate,
            "receipt:h6-future-receipt",
            todayUtc);
        await SeedLedgerAllocationAsync(scenario, chargeId, receiptId, 100m, todayUtc);
        await FreezeAtAsync(todayUtc);

        await using (var today = NewContext())
        {
            var charge = await today.TenantChargeBalanceProjections.SingleAsync(row =>
                row.TenantLedgerEntryId == chargeId);
            charge.OpenAmount.Should().Be(100m);
            charge.IsPastDue.Should().BeTrue();

            var account = await today.TenantAccountBalanceProjections.SingleAsync(row =>
                row.TenantAccountId == scenario.AccountId);
            account.TotalCredits.Should().Be(0m);
            account.ReceivableBalance.Should().Be(100m);
            account.PastDueAmount.Should().Be(100m);
            account.UnappliedCredit.Should().Be(0m);

            (await today.Database.SqlQuery<long>($"""
                SELECT count(*)::bigint AS "Value"
                FROM "vw_tenant_charge_balances" AS balance
                WHERE balance."PortfolioId" = {scenario.PortfolioId}
                  AND balance."TenantAccountId" = {scenario.AccountId}
                  AND balance."IsPastDue"
                  AND balance."OpenAmount" > 0
                """).SingleAsync()).Should().Be(1,
                "the delinquency source must retain the open charge before the receipt's effective date");
            (await QueryAsOfAgedReceivableTotalAsync(
                today,
                scenario.PortfolioId,
                new DateOnly(2027, 1, 20))).Should().Be(account.PastDueAmount);
        }

        await FreezeAtAsync(new DateTime(2027, 1, 25, 12, 0, 0, DateTimeKind.Utc));
        await using (var receiptDateContext = NewContext())
        {
            var charge = await receiptDateContext.TenantChargeBalanceProjections.SingleAsync(row =>
                row.TenantLedgerEntryId == chargeId);
            charge.OpenAmount.Should().Be(0m);

            var account = await receiptDateContext.TenantAccountBalanceProjections.SingleAsync(row =>
                row.TenantAccountId == scenario.AccountId);
            account.TotalCredits.Should().Be(100m);
            account.ReceivableBalance.Should().Be(0m);
            account.PastDueAmount.Should().Be(0m);
            (await QueryAsOfAgedReceivableTotalAsync(
                receiptDateContext,
                scenario.PortfolioId,
                receiptDate)).Should().Be(0m);
        }

        await FreezeAtAsync(todayUtc);
        var lateFee = await ExecuteAtomicAsync(
            Identity("scheduled-tenant-charges.late-fee.apply", "h6-future-receipt-late-fee"),
            new ApplyScheduledLateFeeChargeBatchCommand(
                Guid.NewGuid(), todayUtc, 200, "[{\"State\":\"OH\",\"MaxFlat\":500}]"),
            LateFeeCodec);
        lateFee.Value.LateFeeChargeCount.Should().Be(1,
            "late-fee generation must use the same as-of open amount as delinquency");
    }

    [SkippableFact]
    public async Task FutureDatedReversal_IsIgnoredUntilReversalDate()
    {
        SkipIfNoDocker();
        var todayUtc = new DateTime(2027, 2, 10, 12, 0, 0, DateTimeKind.Utc);
        var reversalDate = new DateOnly(2027, 2, 15);
        var scenario = await SeedScenarioAsync(
            "h6-future-reversal",
            todayUtc,
            new DateOnly(2027, 2, 1),
            new DateOnly(2027, 2, 1),
            new DateOnly(2027, 12, 31),
            rentDueDay: 1,
            baseRentAmount: 100m);
        var chargeId = await SeedExistingRentChargeAsync(
            scenario,
            100m,
            new DateOnly(2027, 2, 1),
            "rent:h6-future-reversal:2027-02",
            new DateTime(2027, 2, 1, 12, 0, 0, DateTimeKind.Utc));
        await SeedFutureReversalAsync(
            scenario,
            chargeId,
            100m,
            reversalDate,
            new DateTime(2027, 2, 10, 12, 0, 0, DateTimeKind.Utc));
        await FreezeAtAsync(todayUtc);

        await using (var today = NewContext())
        {
            var charge = await today.TenantChargeBalanceProjections.SingleAsync(row =>
                row.TenantLedgerEntryId == chargeId);
            charge.OpenAmount.Should().Be(100m);
            var account = await today.TenantAccountBalanceProjections.SingleAsync(row =>
                row.TenantAccountId == scenario.AccountId);
            account.TotalDebits.Should().Be(100m);
            account.TotalCredits.Should().Be(0m);
            account.ReceivableBalance.Should().Be(100m);
        }

        await FreezeAtAsync(new DateTime(2027, 2, 15, 12, 0, 0, DateTimeKind.Utc));
        await using var effective = NewContext();
        var effectiveCharge = await effective.TenantChargeBalanceProjections.SingleAsync(row =>
            row.TenantLedgerEntryId == chargeId);
        effectiveCharge.OpenAmount.Should().Be(0m);
        var effectiveAccount = await effective.TenantAccountBalanceProjections.SingleAsync(row =>
            row.TenantAccountId == scenario.AccountId);
        effectiveAccount.TotalDebits.Should().Be(0m);
        effectiveAccount.TotalCredits.Should().Be(0m);
        effectiveAccount.ReceivableBalance.Should().Be(0m);
    }

    [SkippableFact]
    public async Task AllocatedFutureDatedReversal_UsesReversalEffectiveBoundaryAcrossLedgerAndLateFees()
    {
        SkipIfNoDocker();
        var todayUtc = new DateTime(2027, 4, 10, 12, 0, 0, DateTimeKind.Utc);
        var today = DateOnly.FromDateTime(todayUtc);
        var reversalDate = new DateOnly(2027, 4, 15);
        var scenario = await SeedScenarioAsync(
            "h6-future-allocated-reversal",
            todayUtc,
            new DateOnly(2027, 4, 1),
            new DateOnly(2027, 4, 1),
            new DateOnly(2027, 12, 31),
            rentDueDay: 1,
            baseRentAmount: 100m);
        var sourceStoredFileId = await GetIssuedStoredFileIdAsync(
            scenario, "h6-future-allocated-reversal");
        var authSessionId = await SeedChargeAuthorizationAsync(scenario);
        var postedCharge = await ExecuteAtomicAsync(
            Identity("tenant-account.charge.post", "h6-future-allocated-reversal-charge"),
            new PostTenantChargeCommand(
                scenario.PortfolioId,
                scenario.AccountId,
                100m,
                new DateOnly(2027, 4, 1),
                new DateOnly(2027, 4, 1),
                "April rent charge",
                sourceStoredFileId,
                scenario.UserId,
                authSessionId,
                scenario.AccessContextId,
                scenario.AccessRevision,
                CapabilityKeys.MoneyChargesManage,
                "h6-future-allocated-reversal-charge",
                "tenant-charge:h6-future-allocated-reversal-charge"),
            ChargeCodec);
        postedCharge.Value.Applied.Should().BeTrue();
        var chargeId = postedCharge.Value.LedgerEntryId;
        var receiptId = await SeedAdvanceReceiptAsync(
            scenario,
            100m,
            today,
            "receipt:h6-future-allocated-reversal",
            todayUtc);
        await SeedLedgerAllocationAsync(scenario, chargeId, receiptId, 100m, todayUtc);

        var reversalCommand = new ReverseTenantChargeCommand(
            scenario.PortfolioId,
            scenario.AccountId,
            chargeId,
            reversalDate,
            "Future-dated correction of the allocated charge",
            sourceStoredFileId,
            scenario.UserId,
            authSessionId,
            scenario.AccessContextId,
            scenario.AccessRevision,
            CapabilityKeys.MoneyChargesManage,
            "h6-future-allocated-reversal",
            "tenant-charge-reversal:h6-future-allocated-reversal");
        var reversal = await ExecuteAtomicAsync(
            Identity("tenant-account.charge.reverse", "h6-future-allocated-reversal"),
            reversalCommand,
            ChargeCodec);
        reversal.Value.Applied.Should().BeTrue();

        await using (var persisted = NewContext())
        {
            var allocations = await persisted.TenantLedgerAllocations
                .Where(row => row.DebitEntryId == chargeId)
                .OrderBy(row => row.Id)
                .ToListAsync();
            allocations.Should().HaveCount(2);
            allocations[0].ReversesAllocationId.Should().BeNull();
            allocations[0].EffectiveOn.Should().BeNull();
            allocations[1].ReversesAllocationId.Should().Be(allocations[0].Id);
            allocations[1].EffectiveOn.Should().Be(reversalDate);
        }

        await FreezeAtAsync(todayUtc);
        await using (var beforeReversal = NewContext())
        {
            var charge = await beforeReversal.TenantChargeBalanceProjections.SingleAsync(row =>
                row.TenantLedgerEntryId == chargeId);
            charge.OpenAmount.Should().Be(0m);
            charge.IsPastDue.Should().BeFalse();

            var account = await beforeReversal.TenantAccountBalanceProjections.SingleAsync(row =>
                row.TenantAccountId == scenario.AccountId);
            account.TotalCredits.Should().Be(100m);
            account.UnappliedCredit.Should().Be(0m);
            account.PastDueAmount.Should().Be(0m);
            account.ReceivableBalance.Should().Be(0m);
            (await beforeReversal.Database.SqlQuery<long>($"""
                SELECT count(*)::bigint AS "Value"
                FROM "vw_tenant_charge_balances" AS balance
                WHERE balance."PortfolioId" = {scenario.PortfolioId}
                  AND balance."TenantAccountId" = {scenario.AccountId}
                  AND balance."IsPastDue"
                  AND balance."OpenAmount" > 0
                """).SingleAsync()).Should().Be(0,
                "a paid charge must not be delinquent before the future reversal boundary");
            (await QueryAsOfAgedReceivableTotalAsync(
                beforeReversal,
                scenario.PortfolioId,
                today)).Should().Be(0m);
        }

        var lateFeeBeforeReversal = await ExecuteAtomicAsync(
            Identity("scheduled-tenant-charges.late-fee.apply", "h6-future-allocated-reversal-before"),
            new ApplyScheduledLateFeeChargeBatchCommand(
                Guid.NewGuid(), todayUtc, 200, "[{\"State\":\"OH\",\"MaxFlat\":500}]"),
            LateFeeCodec);
        lateFeeBeforeReversal.Value.LateFeeChargeCount.Should().Be(0,
            "late-fee eligibility must use the paid pre-boundary charge state");

        await FreezeAtAsync(new DateTime(2027, 4, 15, 12, 0, 0, DateTimeKind.Utc));
        await using (var onReversalDate = NewContext())
        {
            var charge = await onReversalDate.TenantChargeBalanceProjections.SingleAsync(row =>
                row.TenantLedgerEntryId == chargeId);
            charge.OpenAmount.Should().Be(0m);
            charge.IsPastDue.Should().BeFalse();

            var account = await onReversalDate.TenantAccountBalanceProjections.SingleAsync(row =>
                row.TenantAccountId == scenario.AccountId);
            account.TotalCredits.Should().Be(100m);
            account.UnappliedCredit.Should().Be(100m);
            account.PastDueAmount.Should().Be(0m);
            account.ReceivableBalance.Should().Be(-100m);
            (await QueryAsOfAgedReceivableTotalAsync(
                onReversalDate,
                scenario.PortfolioId,
                reversalDate)).Should().Be(0m);
        }
    }

    [SkippableFact]
    public async Task TenantAccountBalance_MatchesAsOfAgedReceivablesAndMovesCreditsWithAllocations()
    {
        SkipIfNoDocker();
        var todayUtc = new DateTime(2027, 3, 20, 12, 0, 0, DateTimeKind.Utc);
        var receiptDate = new DateOnly(2027, 3, 25);
        var scenario = await SeedScenarioAsync(
            "h6-account-consistency",
            todayUtc,
            new DateOnly(2027, 3, 1),
            new DateOnly(2027, 3, 1),
            new DateOnly(2027, 12, 31),
            rentDueDay: 1,
            baseRentAmount: 250m);
        var chargeId = await SeedExistingRentChargeAsync(
            scenario,
            250m,
            new DateOnly(2027, 3, 1),
            "rent:h6-account-consistency:2027-03",
            new DateTime(2027, 3, 1, 12, 0, 0, DateTimeKind.Utc));
        var receiptId = await SeedAdvanceReceiptAsync(
            scenario,
            250m,
            receiptDate,
            "receipt:h6-account-consistency",
            todayUtc);
        await SeedLedgerAllocationAsync(scenario, chargeId, receiptId, 250m, todayUtc);

        await FreezeAtAsync(todayUtc);
        await AssertAccountMatchesAsOfAgedReceivablesAsync(
            scenario,
            new DateOnly(2027, 3, 20),
            expectedCredits: 0m,
            expectedEffectiveAllocations: 0m,
            expectedReceivable: 250m);

        await FreezeAtAsync(new DateTime(2027, 3, 25, 12, 0, 0, DateTimeKind.Utc));
        await AssertAccountMatchesAsOfAgedReceivablesAsync(
            scenario,
            receiptDate,
            expectedCredits: 250m,
            expectedEffectiveAllocations: 250m,
            expectedReceivable: 0m);
    }

    [SkippableFact]
    public async Task TenantLedgerViewsMigration_UpDownUpIsReversibleOnPostgreSql()
    {
        SkipIfNoDocker();
        await using var db = NewContext();

        await ExecuteTenantLedgerViewsMigrationAsync(db, "Up");
        var appliedChargeView = await ReadTenantLedgerViewDefinitionAsync(
            db, "vw_tenant_charge_balances");
        var appliedAccountView = await ReadTenantLedgerViewDefinitionAsync(
            db, "vw_tenant_account_balances");
        appliedChargeView.Should().Contain(
            "reversal.\"EffectiveOn\" <= ");
        appliedChargeView.Should().Contain(
            "credit.\"EffectiveOn\" <= ");
        appliedAccountView.Should().Contain(
            "reversal.\"EffectiveOn\" <= ");
        appliedAccountView.Should().Contain(
            "credit.\"EffectiveOn\" <= ");

        await ExecuteTenantLedgerViewsMigrationAsync(db, "Down");
        var revertedChargeView = await ReadTenantLedgerViewDefinitionAsync(
            db, "vw_tenant_charge_balances");
        var revertedAccountView = await ReadTenantLedgerViewDefinitionAsync(
            db, "vw_tenant_account_balances");
        revertedChargeView.Should().NotContain(
            "reversal.\"EffectiveOn\" <= ");
        revertedChargeView.Should().NotContain(
            "credit.\"EffectiveOn\" <= ");
        revertedAccountView.Should().NotContain(
            "reversal.\"EffectiveOn\" <= ");
        revertedAccountView.Should().NotContain(
            "credit.\"EffectiveOn\" <= ");

        await ExecuteTenantLedgerViewsMigrationAsync(db, "Up");
        var reappliedChargeView = await ReadTenantLedgerViewDefinitionAsync(
            db, "vw_tenant_charge_balances");
        var reappliedAccountView = await ReadTenantLedgerViewDefinitionAsync(
            db, "vw_tenant_account_balances");
        reappliedChargeView.Should().Contain(
            "reversal.\"EffectiveOn\" <= ");
        reappliedChargeView.Should().Contain(
            "credit.\"EffectiveOn\" <= ");
        reappliedAccountView.Should().Contain(
            "reversal.\"EffectiveOn\" <= ");
        reappliedAccountView.Should().Contain(
            "credit.\"EffectiveOn\" <= ");
    }

    [SkippableFact]
    public async Task TenantLedgerAllocationEffectiveOnMigration_UpDownUpBackfillsAndRestoresViews()
    {
        SkipIfNoDocker();
        var todayUtc = new DateTime(2027, 5, 10, 12, 0, 0, DateTimeKind.Utc);
        var receiptDate = new DateOnly(2027, 5, 10);
        var scenario = await SeedScenarioAsync(
            "h6-effective-on-migration",
            todayUtc,
            new DateOnly(2027, 5, 1),
            new DateOnly(2027, 5, 1),
            new DateOnly(2027, 12, 31),
            rentDueDay: 1,
            baseRentAmount: 100m);
        var chargeId = await SeedExistingRentChargeAsync(
            scenario,
            100m,
            new DateOnly(2027, 5, 1),
            "rent:h6-effective-on-migration:2027-05",
            new DateTime(2027, 5, 1, 12, 0, 0, DateTimeKind.Utc));
        var receiptId = await SeedAdvanceReceiptAsync(
            scenario,
            100m,
            receiptDate,
            "receipt:h6-effective-on-migration",
            todayUtc);
        await SeedLedgerAllocationAsync(scenario, chargeId, receiptId, 100m, todayUtc);

        await using var db = NewContext();
        await db.Database.ExecuteSqlRawAsync(TenantChargeBalanceViewSql.Drop);
        await db.Database.ExecuteSqlRawAsync(TenantAccountBalanceViewSql.Drop);
        await db.Database.ExecuteSqlRawAsync(
            "ALTER TABLE \"TenantLedgerAllocations\" DROP COLUMN \"EffectiveOn\";");

        await ExecuteTenantLedgerAllocationEffectiveOnMigrationAsync(db, "Up");
        (await HasTenantLedgerAllocationEffectiveOnColumnAsync(db)).Should().BeTrue();
        (await db.Database.SqlQuery<DateOnly>($"""
            SELECT allocation."EffectiveOn" AS "Value"
            FROM "TenantLedgerAllocations" AS allocation
            WHERE allocation."PortfolioId" = {scenario.PortfolioId}
              AND allocation."DebitEntryId" = {chargeId}
            """).SingleAsync()).Should().Be(receiptDate);
        (await ReadTenantLedgerViewDefinitionAsync(db, "vw_tenant_charge_balances"))
            .Should().Contain("COALESCE(allocation.\"EffectiveOn\", credit.\"EffectiveOn\") <= ");
        (await ReadTenantLedgerViewDefinitionAsync(db, "vw_tenant_account_balances"))
            .Should().Contain("COALESCE(allocation.\"EffectiveOn\", credit.\"EffectiveOn\") <= ");

        await ExecuteTenantLedgerAllocationEffectiveOnMigrationAsync(db, "Down");
        (await HasTenantLedgerAllocationEffectiveOnColumnAsync(db)).Should().BeFalse();
        (await ReadTenantLedgerViewDefinitionAsync(db, "vw_tenant_charge_balances"))
            .Should().NotContain("COALESCE(allocation.\"EffectiveOn\", credit.\"EffectiveOn\") <= ");
        (await ReadTenantLedgerViewDefinitionAsync(db, "vw_tenant_account_balances"))
            .Should().NotContain("COALESCE(allocation.\"EffectiveOn\", credit.\"EffectiveOn\") <= ");

        await ExecuteTenantLedgerAllocationEffectiveOnMigrationAsync(db, "Up");
        (await HasTenantLedgerAllocationEffectiveOnColumnAsync(db)).Should().BeTrue();
        (await db.Database.SqlQuery<DateOnly>($"""
            SELECT allocation."EffectiveOn" AS "Value"
            FROM "TenantLedgerAllocations" AS allocation
            WHERE allocation."PortfolioId" = {scenario.PortfolioId}
              AND allocation."DebitEntryId" = {chargeId}
            """).SingleAsync()).Should().Be(receiptDate);
    }

    [SkippableFact]
    public async Task RentBatch_CompanionFailureRollsBackLedgerAuditOutboxAndReceipt_ThenRecovers()
    {
        SkipIfNoDocker();
        var scenario = await SeedScenarioAsync("rollback", FrozenNow);
        var identity = Identity("scheduled-tenant-charges.rent.apply", "rollback");
        var command = RentCommand("rollback");
        Failures.FailAtomicAudit = true;

        var failure = await FluentActions
            .Invoking(() => ExecuteAtomicAsync(identity, command, Codec))
            .Should().ThrowAsync<DbUpdateException>();
        failure.WithInnerException<InvalidOperationException>()
            .WithMessage("injected scheduled tenant-charge audit failure");

        await using (var failed = NewContext())
        {
            (await failed.TenantLedgerEntries.CountAsync(row =>
                row.TenantAccountId == scenario.AccountId)).Should().Be(0);
            (await failed.AtomicAuditLogs.CountAsync(row =>
                row.CommandType == identity.CommandType
                && row.CommandIdempotencyKey == identity.IdempotencyKey)).Should().Be(0);
            (await failed.AtomicCommandReceipts.CountAsync(row =>
                row.CommandType == identity.CommandType
                && row.IdempotencyKey == identity.IdempotencyKey)).Should().Be(0);
            (await failed.OutboxMessages.CountAsync(row =>
                row.PortfolioId == scenario.PortfolioId)).Should().Be(0);
        }

        Failures.FailAtomicAudit = false;
        var recovered = await ExecuteAtomicAsync(identity, command, Codec);
        recovered.Disposition.Should().Be(AtomicCommandDisposition.Executed);
        recovered.Value.RentChargeCount.Should().Be(1);
    }

    [SkippableFact]
    public async Task RentBatch_NotificationFailureRollsBackLedgerAuditOutboxNotificationAndReceipt()
    {
        SkipIfNoDocker();
        var scenario = await SeedScenarioAsync("notification-rollback", FrozenNow);
        var identity = Identity("scheduled-tenant-charges.rent.apply", "notification-rollback");
        var command = RentCommand("notification-rollback");
        Failures.FailNotifications = true;

        var failure = await FluentActions
            .Invoking(() => ExecuteAtomicAsync(identity, command, Codec))
            .Should().ThrowAsync<DbUpdateException>();
        failure.WithInnerException<InvalidOperationException>()
            .WithMessage("injected scheduled tenant-charge notification failure");

        await using (var failed = NewContext())
        {
            (await failed.TenantLedgerEntries.CountAsync(row =>
                row.TenantAccountId == scenario.AccountId)).Should().Be(0);
            (await failed.Notifications.CountAsync(row =>
                row.PortfolioId == scenario.PortfolioId)).Should().Be(0);
            (await failed.AtomicAuditLogs.CountAsync(row =>
                row.CommandType == identity.CommandType
                && row.CommandIdempotencyKey == identity.IdempotencyKey)).Should().Be(0);
            (await failed.AtomicCommandReceipts.CountAsync(row =>
                row.CommandType == identity.CommandType
                && row.IdempotencyKey == identity.IdempotencyKey)).Should().Be(0);
            (await failed.OutboxMessages.CountAsync(row =>
                row.PortfolioId == scenario.PortfolioId)).Should().Be(0);
        }

        Failures.FailNotifications = false;
        var recovered = await ExecuteAtomicAsync(identity, command, Codec);
        recovered.Disposition.Should().Be(AtomicCommandDisposition.Executed);
        recovered.Value.RentChargeCount.Should().Be(1);
        await using var verify = NewContext();
        (await verify.Notifications.CountAsync(row =>
            row.PortfolioId == scenario.PortfolioId
            && row.Type == "ScheduledRentCharge")).Should().Be(1);
    }

    private async Task<Scenario> SeedScenarioAsync(
        string suffix,
        DateTime frozenAtUtc,
        DateOnly? termStartOn = null,
        DateOnly? rentTrackingStartOn = null,
        DateOnly? termEndOn = null,
        bool forceNewPortfolio = false,
        short rentDueDay = 1,
        decimal baseRentAmount = 3100m)
    {
        await using var db = NewContext();
        var portfolio = forceNewPortfolio
            ? null
            : await db.Portfolios.FirstOrDefaultAsync();
        if (portfolio is null)
        {
            portfolio = new Portfolio
            {
                Name = "Scheduled tenant charges",
                ManagementCompanyName = "Scheduled tenant charges",
                TimeZone = "UTC",
                Currency = "USD",
                Settings = "{\"prorationConvention\":\"ActualDays\"}",
                CreatedAt = frozenAtUtc,
                UpdatedAt = frozenAtUtc,
            };
            db.Portfolios.Add(portfolio);
            await db.SaveChangesAsync();
            db.AutomationSettings.Add(new AutomationSettings
            {
                PortfolioId = portfolio.Id,
                EnableRentCharges = true,
                EnableLateFees = true,
                RentChargeLeadDays = 0,
                CreatedAtUtc = frozenAtUtc,
                UpdatedAtUtc = frozenAtUtc,
            });
            if (!await db.SimulationClocks.AnyAsync(row => row.Id == 1))
            {
                db.SimulationClocks.Add(new SimulationClock
                {
                    Id = 1,
                    Mode = ClockMode.Frozen,
                    SimAnchorUtc = frozenAtUtc,
                    RealAnchorUtc = frozenAtUtc,
                    TimeZoneId = "UTC",
                    UpdatedAtRealUtc = frozenAtUtc,
                });
            }
            await db.SaveChangesAsync();
        }
        else
        {
            await FreezeAtAsync(frozenAtUtc);
        }

        await new ChartOfAccountsSeedService(db).SeedAsync(portfolio.Id);
        await db.SaveChangesAsync();

        var user = new ApplicationUser
        {
            UserName = $"billing-{suffix}@example.test",
            NormalizedUserName = $"BILLING-{suffix.ToUpperInvariant()}@EXAMPLE.TEST",
            Email = $"billing-{suffix}@example.test",
            NormalizedEmail = $"BILLING-{suffix.ToUpperInvariant()}@EXAMPLE.TEST",
            PhoneNumber = "+15555550100",
            DisplayName = $"Billing {suffix}",
            SecurityStamp = Guid.NewGuid().ToString("N"),
            ConcurrencyStamp = Guid.NewGuid().ToString("N"),
            CreatedAt = frozenAtUtc,
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        var accessContext = new WorkspaceAccessContext
        {
            UserId = user.Id,
            PortfolioId = portfolio.Id,
            Status = WorkspaceAccessContextStatus.Active,
            LastAuthorizedExperience = WorkspaceExperience.Tenant,
            CreatedAtUtc = frozenAtUtc,
            UpdatedAtUtc = frozenAtUtc,
        };
        db.WorkspaceAccessContexts.Add(accessContext);
        await db.SaveChangesAsync();
        db.UserAlertPreferences.Add(new UserAlertPreference
        {
            PortfolioId = portfolio.Id,
            UserId = user.Id,
            EnableInApp = true,
            EnableMobilePush = true,
            EnableEmail = true,
            EnableSms = true,
            CreatedAtUtc = frozenAtUtc,
            UpdatedAtUtc = frozenAtUtc,
        });
        db.DeviceTokens.Add(new DeviceToken
        {
            PortfolioId = portfolio.Id,
            UserId = user.Id,
            Token = $"push-token-{suffix}",
            Platform = "android",
            CreatedAt = frozenAtUtc,
            LastSeenAt = frozenAtUtc,
        });
        await db.SaveChangesAsync();

        var property = new Property
        {
            PortfolioId = portfolio.Id,
            Name = $"Property {suffix}",
            AddressLine1 = "1 Ledger Way",
            City = "Columbus",
            State = "OH",
            PostalCode = "43215",
            CreatedAt = frozenAtUtc,
            UpdatedAt = frozenAtUtc,
        };
        db.Properties.Add(property);
        await db.SaveChangesAsync();
        var unit = new Unit
        {
            PortfolioId = portfolio.Id,
            PropertyId = property.Id,
            UnitNumber = suffix,
            CreatedAt = frozenAtUtc,
            UpdatedAt = frozenAtUtc,
        };
        var template = new DocumentTemplate
        {
            PortfolioId = portfolio.Id,
            Name = $"Template {suffix}",
            Version = 1,
            CreatedAtUtc = frozenAtUtc,
            UpdatedAtUtc = frozenAtUtc,
        };
        var issuedFile = StoredFile(portfolio.Id, $"issued-{suffix}.pdf", frozenAtUtc);
        var executedFile = StoredFile(portfolio.Id, $"executed-{suffix}.pdf", frozenAtUtc);
        db.AddRange(unit, template, issuedFile, executedFile);
        await db.SaveChangesAsync();
        var documentSourceVersion = new LegalDocumentSourceVersion
        {
            PublicId = Guid.NewGuid(), PortfolioId = portfolio.Id,
            SourceKind = LegalDocumentSourceKind.AuthoredTemplateSnapshot,
            BusinessKey = $"template:{template.Id}:v{template.Version}",
            DocumentTemplateId = template.Id, DocumentTemplateVersion = template.Version,
            RendererKey = "lease-agreement-overlay", RendererVersion = 1,
            SnapshotPayload = "{}", CreatedAtUtc = frozenAtUtc, CreatedByUserId = user.Id,
        };
        db.Add(documentSourceVersion);
        await db.SaveChangesAsync();

        var issuedArtifact = Artifact(
            portfolio.Id, user.Id, issuedFile.Id, LegalDocumentArtifactKind.IssuedAgreement,
            $"issued-{suffix}", frozenAtUtc);
        var executedArtifact = Artifact(
            portfolio.Id, user.Id, executedFile.Id, LegalDocumentArtifactKind.ExecutedAgreement,
            $"executed-{suffix}", frozenAtUtc);
        db.AddRange(issuedArtifact, executedArtifact);
        await db.SaveChangesAsync();

        var management = new LeaseManagement
        {
            PortfolioId = portfolio.Id,
            PropertyId = property.Id,
            UnitId = unit.Id,
            RelationshipNumber = $"LM-{suffix}",
            PossessionGivenAtUtc = frozenAtUtc.AddDays(-5),
            CreatedAtUtc = frozenAtUtc,
            UpdatedAtUtc = frozenAtUtc,
            CreatedByUserId = user.Id,
            RowVersion = Guid.NewGuid(),
        };
        db.LeaseManagements.Add(management);
        await db.SaveChangesAsync();
        var tenant = new Tenant
        {
            PortfolioId = portfolio.Id,
            FirstName = "Tenant",
            LastName = suffix,
            Email = $"tenant-{suffix}@example.test",
            Phone = "+15555550200",
            CreatedAt = frozenAtUtc,
            UpdatedAt = frozenAtUtc,
        };
        db.Tenants.Add(tenant);
        await db.SaveChangesAsync();
        var account = new TenantAccount
        {
            PortfolioId = portfolio.Id,
            LeaseManagementId = management.Id,
            AccountNumber = $"TA-{suffix}",
            Currency = "USD",
            RentTrackingStartOn = rentTrackingStartOn,
            OpenedAtUtc = frozenAtUtc,
            CreatedAtUtc = frozenAtUtc,
            CreatedByUserId = user.Id,
        };
        var party = new LeaseManagementParty
        {
            PortfolioId = portfolio.Id,
            LeaseManagementId = management.Id,
            TenantId = tenant.Id,
            Role = LeaseManagementPartyRole.PrimaryTenant,
            EffectiveFrom = termStartOn ?? new DateOnly(2026, 7, 10),
            ChangeReason = "integration tenant access",
            CreatedAtUtc = frozenAtUtc,
            CreatedByUserId = user.Id,
        };
        var agreement = new LeaseAgreement
        {
            PublicId = Guid.NewGuid(),
            PortfolioId = portfolio.Id,
            LeaseManagementId = management.Id,
            VersionNumber = 1,
            AgreementNumber = $"AGR-{suffix}",
            ChangeType = LeaseAgreementChangeType.Initial,
            TermType = LeaseAgreementTermType.FixedTerm,
            TermStartOn = termStartOn ?? new DateOnly(2026, 7, 10),
            TermEndOn = termEndOn ?? new DateOnly(2026, 8, 31),
            GoverningFromOn = termStartOn ?? new DateOnly(2026, 7, 10),
            BaseRentAmount = baseRentAmount,
            RentDueDay = rentDueDay,
            SecurityDepositObligation = 0m,
            LateFeeAmount = 500m,
            GracePeriodDays = 5,
            Currency = "USD",
            TermsSchemaVersion = 1,
            TermsPayload = "{}",
            DocumentSourceVersionId = documentSourceVersion.Id,
            IssuedArtifactId = issuedArtifact.Id,
            IssuedAtUtc = frozenAtUtc.AddDays(-10),
            ExecutedArtifactId = executedArtifact.Id,
            FullyExecutedAtUtc = frozenAtUtc.AddDays(-9),
            CreatedAtUtc = frozenAtUtc,
            UpdatedAtUtc = frozenAtUtc,
            CreatedByUserId = user.Id,
        };
        db.AddRange(account, party, agreement);
        await db.SaveChangesAsync();
        db.TenantUserAccesses.Add(new TenantUserAccess
        {
            PublicId = Guid.NewGuid(),
            PortfolioId = portfolio.Id,
            AccessContextId = accessContext.Id,
            ApplicationUserId = user.Id,
            LeaseManagementPartyId = party.Id,
            GrantedAtUtc = frozenAtUtc,
            GrantedByUserId = user.Id,
            Reason = "integration tenant portal access",
        });
        await db.SaveChangesAsync();
        return new Scenario(
            portfolio.Id,
            account.Id,
            agreement.PublicId,
            agreement.Id,
            management.Id,
            party.Id,
            user.Id,
            accessContext.Id,
            accessContext.AccessRevision,
            null);
    }

    private async Task<int> GetIssuedStoredFileIdAsync(Scenario scenario, string suffix)
    {
        await using var db = NewContext();
        return await db.StoredFiles
            .Where(row => row.PortfolioId == scenario.PortfolioId
                && row.FileName == $"issued-{suffix}.pdf")
            .Select(row => row.Id)
            .SingleAsync();
    }

    private async Task<Guid> SeedChargeAuthorizationAsync(Scenario scenario)
    {
        await using var db = NewContext();
        var now = DateTime.UtcNow;
        var propertyId = await db.LeaseManagements
            .Where(row => row.Id == scenario.LeaseManagementId
                && row.PortfolioId == scenario.PortfolioId)
            .Select(row => row.PropertyId)
            .SingleAsync();
        var membership = new WorkspaceMembership
        {
            AccessContextId = scenario.AccessContextId,
            PortfolioId = scenario.PortfolioId,
            Status = WorkspaceMembershipStatus.Active,
            DefaultExperience = WorkspaceExperience.Management,
            EffectiveFromUtc = now.AddMinutes(-5),
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
        };
        db.WorkspaceMemberships.Add(membership);
        await db.SaveChangesAsync();

        var assignment = new MembershipRoleAssignment
        {
            WorkspaceMembershipId = membership.Id,
            PortfolioId = scenario.PortfolioId,
            RoleProfileId = 2,
            Status = MembershipRoleAssignmentStatus.Active,
            ScopeKind = MembershipRoleAssignmentScopeKind.SelectedProperties,
            EffectiveFromUtc = now.AddMinutes(-5),
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
        };
        db.MembershipRoleAssignments.Add(assignment);
        await db.SaveChangesAsync();
        db.MembershipRoleAssignmentProperties.Add(new MembershipRoleAssignmentProperty
        {
            MembershipRoleAssignmentId = assignment.Id,
            PropertyId = propertyId,
            PortfolioId = scenario.PortfolioId,
        });
        var session = new AuthSession
        {
            Id = Guid.NewGuid(),
            UserId = scenario.UserId,
            ActiveAccessContextId = scenario.AccessContextId,
            Status = AuthSessionStatus.Active,
            CreatedAtUtc = now,
            LastSeenAtUtc = now,
            ExpiresAtUtc = now.AddHours(8),
        };
        db.AuthSessions.Add(session);
        await db.SaveChangesAsync();
        return session.Id;
    }

    private async Task AddTenantAccessAsync(
        Scenario scenario,
        string suffix,
        LeaseManagementPartyRole role,
        DateTime now,
        bool enableInApp = true)
    {
        await using var db = NewContext();
        var user = new ApplicationUser
        {
            UserName = $"tenant-access-{suffix}@example.test",
            NormalizedUserName = $"TENANT-ACCESS-{suffix.ToUpperInvariant()}@EXAMPLE.TEST",
            Email = $"tenant-access-{suffix}@example.test",
            NormalizedEmail = $"TENANT-ACCESS-{suffix.ToUpperInvariant()}@EXAMPLE.TEST",
            PhoneNumber = "+15555550101",
            DisplayName = $"Tenant access {suffix}",
            SecurityStamp = Guid.NewGuid().ToString("N"),
            ConcurrencyStamp = Guid.NewGuid().ToString("N"),
            CreatedAt = now,
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        var accessContext = new WorkspaceAccessContext
        {
            UserId = user.Id,
            PortfolioId = scenario.PortfolioId,
            Status = WorkspaceAccessContextStatus.Active,
            LastAuthorizedExperience = WorkspaceExperience.Tenant,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
        };
        db.WorkspaceAccessContexts.Add(accessContext);
        await db.SaveChangesAsync();
        db.UserAlertPreferences.Add(new UserAlertPreference
        {
            PortfolioId = scenario.PortfolioId,
            UserId = user.Id,
            EnableInApp = enableInApp,
            EnableMobilePush = true,
            EnableEmail = true,
            EnableSms = true,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
        });
        db.DeviceTokens.Add(new DeviceToken
        {
            PortfolioId = scenario.PortfolioId,
            UserId = user.Id,
            Token = $"push-token-{suffix}",
            Platform = "android",
            CreatedAt = now,
            LastSeenAt = now,
        });
        var tenant = new Tenant
        {
            PortfolioId = scenario.PortfolioId,
            FirstName = "Tenant",
            LastName = suffix,
            Email = $"tenant-{suffix}@example.test",
            Phone = "+15555550201",
            CreatedAt = now,
            UpdatedAt = now,
        };
        db.Tenants.Add(tenant);
        await db.SaveChangesAsync();
        var party = new LeaseManagementParty
        {
            PortfolioId = scenario.PortfolioId,
            LeaseManagementId = scenario.LeaseManagementId,
            TenantId = tenant.Id,
            Role = role,
            EffectiveFrom = new DateOnly(2026, 7, 10),
            ChangeReason = "integration tenant access",
            CreatedAtUtc = now,
            CreatedByUserId = scenario.UserId,
        };
        db.LeaseManagementParties.Add(party);
        await db.SaveChangesAsync();
        db.TenantUserAccesses.Add(new TenantUserAccess
        {
            PublicId = Guid.NewGuid(),
            PortfolioId = scenario.PortfolioId,
            AccessContextId = accessContext.Id,
            ApplicationUserId = user.Id,
            LeaseManagementPartyId = party.Id,
            GrantedAtUtc = now,
            GrantedByUserId = scenario.UserId,
            Reason = "integration tenant portal access",
        });
        await db.SaveChangesAsync();

        if (role == LeaseManagementPartyRole.CoTenant)
        {
            scenario.CoTenantUserId = user.Id;
            scenario.CoTenantAccessContextId = accessContext.Id;
            scenario.CoTenantAccessRevision = accessContext.AccessRevision;
        }
        if (!enableInApp)
        {
            scenario.DisabledInAppUserId = user.Id;
        }
    }

    private async Task AddRevokedTenantAccessAsync(Scenario scenario, string suffix, DateTime now)
    {
        await using var db = NewContext();
        var revoked = new ApplicationUser
        {
            UserName = $"revoked-{suffix}@example.test",
            NormalizedUserName = $"REVOKED-{suffix.ToUpperInvariant()}@EXAMPLE.TEST",
            Email = $"revoked-{suffix}@example.test",
            NormalizedEmail = $"REVOKED-{suffix.ToUpperInvariant()}@EXAMPLE.TEST",
            DisplayName = $"Revoked {suffix}",
            SecurityStamp = Guid.NewGuid().ToString("N"),
            ConcurrencyStamp = Guid.NewGuid().ToString("N"),
            CreatedAt = now,
        };
        db.Users.Add(revoked);
        await db.SaveChangesAsync();
        var accessContext = new WorkspaceAccessContext
        {
            UserId = revoked.Id,
            PortfolioId = scenario.PortfolioId,
            Status = WorkspaceAccessContextStatus.Active,
            LastAuthorizedExperience = WorkspaceExperience.Tenant,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
        };
        db.WorkspaceAccessContexts.Add(accessContext);
        await db.SaveChangesAsync();
        db.TenantUserAccesses.Add(new TenantUserAccess
        {
            PublicId = Guid.NewGuid(),
            PortfolioId = scenario.PortfolioId,
            AccessContextId = accessContext.Id,
            ApplicationUserId = revoked.Id,
            LeaseManagementPartyId = scenario.LeaseManagementPartyId,
            GrantedAtUtc = now,
            RevokedAtUtc = now.AddMinutes(1),
            GrantedByUserId = scenario.UserId,
            RevokedByUserId = scenario.UserId,
            Reason = "integration revoked tenant portal access",
        });
        await db.SaveChangesAsync();
        scenario.RevokedUserId = revoked.Id;
    }

    private async Task<long> SeedAdvanceReceiptAsync(
        Scenario scenario,
        decimal amount,
        DateOnly effectiveOn,
        string businessKey,
        DateTime postedAtUtc,
        bool withProviderPaymentAttempt = false,
        TenantPaymentAttemptType attemptType = TenantPaymentAttemptType.UnappliedReceipt)
    {
        await using var db = NewContext();
        TenantPaymentAttempt? attempt = null;
        if (withProviderPaymentAttempt)
        {
            attempt = new TenantPaymentAttempt
            {
                PortfolioId = scenario.PortfolioId,
                TenantAccountId = scenario.AccountId,
                Provider = "manual",
                ProviderObjectId = $"manual-payment-{businessKey}",
                IdempotencyKey = $"manual-payment-{businessKey}",
                AttemptType = attemptType,
                State = TenantPaymentAttemptState.Succeeded,
                Amount = amount,
                Currency = "USD",
                PaymentMethodSummary = "Portal card",
                PreparedAtUtc = postedAtUtc,
                SubmittedAtUtc = postedAtUtc,
                SettledAtUtc = postedAtUtc,
                UpdatedAtUtc = postedAtUtc,
                CreatedByUserId = scenario.UserId,
            };
            db.TenantPaymentAttempts.Add(attempt);
            await db.SaveChangesAsync();
        }
        var receipt = new TenantLedgerEntry
        {
            PortfolioId = scenario.PortfolioId,
            TenantAccountId = scenario.AccountId,
            EntryType = TenantLedgerEntryType.PaymentReceipt,
            Direction = TenantLedgerDirection.Credit,
            Amount = amount,
            Currency = "USD",
            EffectiveOn = effectiveOn,
            PostedAtUtc = postedAtUtc,
            Description = "Advance rent receipt",
            BusinessKey = businessKey,
            ProviderPaymentAttemptId = attempt?.Id,
            CreatedByUserId = scenario.UserId,
        };
        db.TenantLedgerEntries.Add(receipt);
        await db.SaveChangesAsync();
        if (attemptType == TenantPaymentAttemptType.DepositReceipt)
        {
            var depositAccount = new SecurityDepositAccount
            {
                PortfolioId = scenario.PortfolioId,
                TenantAccountId = scenario.AccountId,
                OriginatingAgreementId = scenario.AgreementId,
                Currency = "USD",
                CreatedAtUtc = postedAtUtc,
                CreatedByUserId = scenario.UserId,
            };
            db.SecurityDepositAccounts.Add(depositAccount);
            await db.SaveChangesAsync();
            db.SecurityDepositEntries.Add(new SecurityDepositEntry
            {
                PublicId = Guid.NewGuid(),
                PortfolioId = scenario.PortfolioId,
                SecurityDepositAccountId = depositAccount.Id,
                EntryType = SecurityDepositEntryType.Receipt,
                Direction = SecurityDepositDirection.Increase,
                Amount = amount,
                Currency = "USD",
                EffectiveOn = effectiveOn,
                PostedAtUtc = postedAtUtc,
                BusinessKey = $"{businessKey}:deposit",
                Description = "Security deposit receipt",
                LeaseAgreementId = scenario.AgreementId,
                TenantLedgerEntryId = receipt.Id,
                CreatedByUserId = scenario.UserId,
            });
            await db.SaveChangesAsync();
        }
        return receipt.Id;
    }

    private async Task<long> SeedExistingRentChargeAsync(
        Scenario scenario,
        decimal amount,
        DateOnly dueOn,
        string businessKey,
        DateTime postedAtUtc)
    {
        await using var db = NewContext();
        var charge = new TenantLedgerEntry
        {
            PortfolioId = scenario.PortfolioId,
            TenantAccountId = scenario.AccountId,
            LeaseAgreementId = scenario.AgreementId,
            EntryType = TenantLedgerEntryType.RentCharge,
            Direction = TenantLedgerDirection.Debit,
            Amount = amount,
            Currency = "USD",
            EffectiveOn = dueOn,
            DueOn = dueOn,
            PostedAtUtc = postedAtUtc,
            Description = $"Rent due {dueOn:MMM d, yyyy}",
            BusinessKey = businessKey,
            CreatedByUserId = scenario.UserId,
        };
        db.TenantLedgerEntries.Add(charge);
        await db.SaveChangesAsync();
        return charge.Id;
    }

    private async Task SeedLedgerAllocationAsync(
        Scenario scenario,
        long debitEntryId,
        long creditEntryId,
        decimal amount,
        DateTime allocatedAtUtc)
    {
        await using var db = NewContext();
        db.TenantLedgerAllocations.Add(new TenantLedgerAllocation
        {
            PortfolioId = scenario.PortfolioId,
            TenantAccountId = scenario.AccountId,
            DebitEntryId = debitEntryId,
            CreditEntryId = creditEntryId,
            Amount = amount,
            AllocatedAtUtc = allocatedAtUtc,
            BusinessKey = $"allocation:h6:{debitEntryId}:{creditEntryId}",
            CreatedByUserId = scenario.UserId,
        });
        await db.SaveChangesAsync();
    }

    private async Task SeedFutureReversalAsync(
        Scenario scenario,
        long chargeEntryId,
        decimal amount,
        DateOnly effectiveOn,
        DateTime postedAtUtc)
    {
        await using var db = NewContext();
        db.TenantLedgerEntries.Add(new TenantLedgerEntry
        {
            PublicId = Guid.NewGuid(),
            PortfolioId = scenario.PortfolioId,
            TenantAccountId = scenario.AccountId,
            EntryType = TenantLedgerEntryType.Reversal,
            Direction = TenantLedgerDirection.Credit,
            Amount = amount,
            Currency = "USD",
            EffectiveOn = effectiveOn,
            PostedAtUtc = postedAtUtc,
            Description = "Future-dated charge reversal",
            BusinessKey = $"reversal:h6:{chargeEntryId}",
            ReversesEntryId = chargeEntryId,
            CreatedByUserId = scenario.UserId,
        });
        await db.SaveChangesAsync();
    }

    private async Task AssertAccountMatchesAsOfAgedReceivablesAsync(
        Scenario scenario,
        DateOnly asOfDate,
        decimal expectedCredits,
        decimal expectedEffectiveAllocations,
        decimal expectedReceivable)
    {
        await using var db = NewContext();
        var account = await db.TenantAccountBalanceProjections.SingleAsync(row =>
            row.TenantAccountId == scenario.AccountId);
        var agedReceivable = await QueryAsOfAgedReceivableTotalAsync(
            db,
            scenario.PortfolioId,
            asOfDate);
        var effectiveAllocations = await db.Database.SqlQuery<decimal>($"""
            SELECT COALESCE(sum(allocation."Amount"), 0::numeric) AS "Value"
            FROM "TenantLedgerAllocations" AS allocation
            JOIN "TenantLedgerEntries" AS credit
              ON credit."PortfolioId" = allocation."PortfolioId"
             AND credit."TenantAccountId" = allocation."TenantAccountId"
             AND credit."Id" = allocation."CreditEntryId"
            WHERE allocation."PortfolioId" = {scenario.PortfolioId}
              AND allocation."TenantAccountId" = {scenario.AccountId}
              AND credit."Direction" = 'Credit'
              AND COALESCE(allocation."EffectiveOn", credit."EffectiveOn") <= {asOfDate}
            """).SingleAsync();

        account.TotalCredits.Should().Be(expectedCredits);
        effectiveAllocations.Should().Be(expectedEffectiveAllocations);
        (account.TotalCredits - effectiveAllocations).Should().Be(account.UnappliedCredit);
        account.ReceivableBalance.Should().Be(expectedReceivable);
        agedReceivable.Should().Be(account.ReceivableBalance);
        account.PastDueAmount.Should().Be(agedReceivable);
    }

    private static Task<decimal> QueryAsOfAgedReceivableTotalAsync(
        RentalCommandDbContext db,
        int portfolioId,
        DateOnly asOfDate) => db.Database.SqlQuery<decimal>($"""
        WITH as_of_reversals AS MATERIALIZED (
            SELECT reversal."PortfolioId",
                   reversal."TenantAccountId",
                   reversal."ReversesEntryId" AS "TenantLedgerEntryId",
                   sum(reversal."Amount") AS "ReversedAmount"
            FROM "TenantLedgerEntries" AS reversal
            WHERE reversal."PortfolioId" = {portfolioId}
              AND reversal."EntryType" = 'Reversal'
              AND reversal."Direction" = 'Credit'
              AND reversal."EffectiveOn" <= {asOfDate}
            GROUP BY reversal."PortfolioId", reversal."TenantAccountId", reversal."ReversesEntryId"
        ),
        as_of_allocations AS MATERIALIZED (
            SELECT allocation."PortfolioId",
                   allocation."TenantAccountId",
                   allocation."DebitEntryId" AS "TenantLedgerEntryId",
                   sum(allocation."Amount") AS "NetAllocations"
            FROM "TenantLedgerAllocations" AS allocation
            JOIN "TenantLedgerEntries" AS credit
              ON credit."PortfolioId" = allocation."PortfolioId"
             AND credit."TenantAccountId" = allocation."TenantAccountId"
             AND credit."Id" = allocation."CreditEntryId"
            WHERE allocation."PortfolioId" = {portfolioId}
              AND credit."Direction" = 'Credit'
              AND COALESCE(allocation."EffectiveOn", credit."EffectiveOn") <= {asOfDate}
            GROUP BY allocation."PortfolioId", allocation."TenantAccountId", allocation."DebitEntryId"
        ),
        as_of_charge_balances AS MATERIALIZED (
            SELECT entry."Id" AS "TenantLedgerEntryId",
                   GREATEST(
                       0::numeric,
                       entry."Amount"
                         - COALESCE(reversal."ReversedAmount", 0::numeric)
                         - COALESCE(allocation."NetAllocations", 0::numeric)
                   ) AS "OpenAmount"
            FROM "TenantLedgerEntries" AS entry
            LEFT JOIN as_of_reversals AS reversal
              ON reversal."PortfolioId" = entry."PortfolioId"
             AND reversal."TenantAccountId" = entry."TenantAccountId"
             AND reversal."TenantLedgerEntryId" = entry."Id"
            LEFT JOIN as_of_allocations AS allocation
              ON allocation."PortfolioId" = entry."PortfolioId"
             AND allocation."TenantAccountId" = entry."TenantAccountId"
             AND allocation."TenantLedgerEntryId" = entry."Id"
            WHERE entry."PortfolioId" = {portfolioId}
              AND entry."Direction" = 'Debit'
              AND entry."EffectiveOn" <= {asOfDate}
              AND entry."EntryType" NOT IN ('Refund', 'Reversal', 'TransferOut')
        )
        SELECT COALESCE(sum("OpenAmount"), 0::numeric) AS "Value"
        FROM as_of_charge_balances
        WHERE "OpenAmount" > 0::numeric
        """).SingleAsync();

    private static async Task ExecuteTenantLedgerViewsMigrationAsync(
        RentalCommandDbContext db,
        string methodName)
    {
        var migration = new RentalCommand.Data.Migrations.FixTenantLedgerViewsAsOfReversalsAndAllocations();
        var builder = new MigrationBuilder("Npgsql.EntityFrameworkCore.PostgreSQL");
        typeof(RentalCommand.Data.Migrations.FixTenantLedgerViewsAsOfReversalsAndAllocations)
            .GetMethod(methodName, BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(migration, [builder]);

        foreach (var operation in builder.Operations)
        {
            operation.Should().BeOfType<SqlOperation>();
            await db.Database.ExecuteSqlRawAsync(((SqlOperation)operation).Sql);
        }
    }

    private static async Task ExecuteTenantLedgerAllocationEffectiveOnMigrationAsync(
        RentalCommandDbContext db,
        string methodName)
    {
        var migration = new RentalCommand.Data.Migrations.AddTenantLedgerAllocationEffectiveOn();
        var builder = new MigrationBuilder("Npgsql.EntityFrameworkCore.PostgreSQL");
        typeof(RentalCommand.Data.Migrations.AddTenantLedgerAllocationEffectiveOn)
            .GetMethod(methodName, BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(migration, [builder]);

        foreach (var operation in builder.Operations)
        {
            switch (operation)
            {
                case SqlOperation sql:
                    await db.Database.ExecuteSqlRawAsync(sql.Sql);
                    break;
                case AddColumnOperation addColumn:
                    await db.Database.ExecuteSqlRawAsync($"""
                        ALTER TABLE "{addColumn.Table}"
                        ADD COLUMN "{addColumn.Name}" {addColumn.ColumnType}
                        {(addColumn.IsNullable ? "NULL" : "NOT NULL")};
                        """);
                    break;
                case DropColumnOperation dropColumn:
                    await db.Database.ExecuteSqlRawAsync($"""
                        ALTER TABLE "{dropColumn.Table}"
                        DROP COLUMN "{dropColumn.Name}";
                        """);
                    break;
                default:
                    throw new InvalidOperationException(
                        $"Unexpected operation {operation.GetType().Name} in effective-on migration.");
            }
        }
    }

    private static async Task<bool> HasTenantLedgerAllocationEffectiveOnColumnAsync(
        RentalCommandDbContext db) => await db.Database.SqlQuery<int>($"""
        SELECT count(*)::integer AS "Value"
        FROM information_schema.columns
        WHERE table_schema = 'public'
          AND table_name = 'TenantLedgerAllocations'
          AND column_name = 'EffectiveOn'
        """).SingleAsync() == 1;

    private static Task<string> ReadTenantLedgerViewDefinitionAsync(
        RentalCommandDbContext db,
        string viewName) => db.Database.SqlQuery<string>($"""
        SELECT pg_get_viewdef({viewName}::regclass, true) AS "Value"
        """).SingleAsync();

    private async Task SeedFullRefundAsync(
        Scenario scenario,
        long receiptId,
        decimal amount,
        DateOnly effectiveOn,
        DateTime settledAtUtc)
    {
        await using var db = NewContext();
        var sourceAttemptId = await db.TenantLedgerEntries
            .Where(row =>
                row.PortfolioId == scenario.PortfolioId
                && row.TenantAccountId == scenario.AccountId
                && row.Id == receiptId)
            .Select(row => row.ProviderPaymentAttemptId)
            .SingleAsync();
        sourceAttemptId.Should().NotBeNull();

        await using var transaction = await db.Database.BeginTransactionAsync();
        var refundAttempt = new TenantPaymentAttempt
        {
            PortfolioId = scenario.PortfolioId,
            TenantAccountId = scenario.AccountId,
            Provider = "manual",
            ProviderObjectId = $"manual-refund-{receiptId}",
            RefundsPaymentAttemptId = sourceAttemptId,
            IdempotencyKey = $"manual-refund-{receiptId}",
            AttemptType = TenantPaymentAttemptType.Refund,
            State = TenantPaymentAttemptState.Succeeded,
            Amount = amount,
            Currency = "USD",
            PaymentMethodSummary = "Manual refund",
            PreparedAtUtc = settledAtUtc,
            SubmittedAtUtc = settledAtUtc,
            SettledAtUtc = settledAtUtc,
            UpdatedAtUtc = settledAtUtc,
            AttemptCount = 1,
            CreatedByUserId = scenario.UserId,
        };
        db.TenantPaymentAttempts.Add(refundAttempt);
        await db.SaveChangesAsync();
        db.TenantLedgerEntries.Add(new TenantLedgerEntry
        {
            PortfolioId = scenario.PortfolioId,
            TenantAccountId = scenario.AccountId,
            EntryType = TenantLedgerEntryType.Refund,
            Direction = TenantLedgerDirection.Debit,
            Amount = amount,
            Currency = "USD",
            EffectiveOn = effectiveOn,
            PostedAtUtc = settledAtUtc,
            Description = "Refunded advance receipt",
            BusinessKey = $"refund:{receiptId}",
            ProviderPaymentAttemptId = refundAttempt.Id,
            CreatedByUserId = scenario.UserId,
        });
        await db.SaveChangesAsync();
        await transaction.CommitAsync();
    }

    private async Task FreezeAtAsync(DateTime instant, string timeZoneId = "UTC")
    {
        await using var db = NewContext();
        var clock = await db.SimulationClocks.SingleAsync(row => row.Id == 1);
        clock.Mode = ClockMode.Frozen;
        clock.SimAnchorUtc = instant;
        clock.RealAnchorUtc = instant;
        clock.TimeZoneId = timeZoneId;
        clock.UpdatedAtRealUtc = instant;
        await db.SaveChangesAsync();
    }

    private async Task SetRentChargeLeadDaysAsync(int portfolioId, int leadDays, DateTime now)
    {
        await using var db = NewContext();
        await db.AutomationSettings
            .Where(row => row.PortfolioId == portfolioId)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(row => row.RentChargeLeadDays, leadDays)
                .SetProperty(row => row.UpdatedAtUtc, now));
    }

    private static StoredFile StoredFile(int portfolioId, string name, DateTime now) => new()
    {
        PortfolioId = portfolioId,
        FileName = name,
        FilePath = $"tests/{name}",
        ContentType = "application/pdf",
        FileSize = 100,
        UploadedAt = now,
    };

    private static LegalDocumentArtifact Artifact(
        int portfolioId,
        int userId,
        int storedFileId,
        LegalDocumentArtifactKind kind,
        string key,
        DateTime now) => new()
    {
        PublicId = Guid.NewGuid(),
        PortfolioId = portfolioId,
        StoredFileId = storedFileId,
        ArtifactKind = kind,
        StorageKey = $"tests/{key}",
        FileName = $"{key}.pdf",
        ContentType = "application/pdf",
        ByteLength = 100,
        ContentSha256 = new string(kind == LegalDocumentArtifactKind.IssuedAgreement ? 'a' : 'b', 64),
        LegalIssuanceFingerprint = kind == LegalDocumentArtifactKind.IssuedAgreement
            ? new string('c', 64)
            : null,
        CreatedAtUtc = now,
        CreatedByUserId = userId,
    };

    private static ApplyScheduledRentChargeBatchCommand RentCommand(
        string _,
        DateTime? businessNowUtc = null) => new(
        Guid.NewGuid(), businessNowUtc ?? FrozenNow, 200);

    private static AtomicCommandIdentity Identity(string type, string suffix) => new(type, suffix);

    private static string DestinationHash(string destination) =>
        Convert.ToHexString(
                System.Security.Cryptography.SHA256.HashData(
                    System.Text.Encoding.UTF8.GetBytes(destination.Trim().ToLowerInvariant())))
            .ToLowerInvariant();

    private async Task<AtomicCommandOutcome<TResult>> ExecuteAtomicAsync<TCommand, TResult>(
        AtomicCommandIdentity identity,
        TCommand command,
        AtomicJsonResultCodec<TResult> codec)
        where TCommand : notnull, IAtomicCommandData
        where TResult : notnull
    {
        await using var scope = _services!.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<RentalCommandDbContext>();
        var writes = scope.ServiceProvider.GetRequiredService<IRequestWriteExecutor>();
        if (command is PostTenantChargeCommand post)
        {
            var handler = new PostTenantChargeHandler(db);
            var outcome = await writes.ExecuteAsync(identity.IdempotencyKey,
                TenantMoneyWriteSupport.Write(post, handler.ExecuteAsync, handler.AuthorizeAsync));
            return (AtomicCommandOutcome<TResult>)(object)outcome;
        }
        if (command is ReverseTenantChargeCommand reverse)
        {
            var handler = new ReverseTenantChargeHandler(db);
            var outcome = await writes.ExecuteAsync(identity.IdempotencyKey,
                TenantMoneyWriteSupport.Write(reverse, handler.ExecuteAsync, handler.AuthorizeAsync));
            return (AtomicCommandOutcome<TResult>)(object)outcome;
        }
        if (command is ApplyScheduledRentChargeBatchCommand rent)
        {
            var handler = new ApplyScheduledRentChargeBatchHandler(db);
            var outcome = await writes.ExecuteAsync(identity.IdempotencyKey,
                TenantMoneyWriteSupport.Write(rent, handler.ExecuteAsync, handler.AuthorizeAsync));
            return (AtomicCommandOutcome<TResult>)(object)outcome;
        }
        if (command is ApplyScheduledLateFeeChargeBatchCommand lateFee)
        {
            var handler = new ApplyScheduledLateFeeChargeBatchHandler(db);
            var outcome = await writes.ExecuteAsync(identity.IdempotencyKey,
                TenantMoneyWriteSupport.Write(lateFee, handler.ExecuteAsync, handler.AuthorizeAsync));
            return (AtomicCommandOutcome<TResult>)(object)outcome;
        }

        var atomic = scope.ServiceProvider.GetRequiredService<IAtomicUnitOfWork>();
        return await atomic.ExecuteAsync(identity, command, codec);
    }

    private CommandRecorder Recorder => _services!.GetRequiredService<CommandRecorder>();
    private CompanionFailureInterceptor Failures =>
        _services!.GetRequiredService<CompanionFailureInterceptor>();

    private RentalCommandDbContext NewContext() => new(
        new DbContextOptionsBuilder<RentalCommandDbContext>()
            .UseNpgsql(_postgres!.GetConnectionString())
            .Options);

    private void SkipIfNoDocker() =>
        Skip.IfNot(_dockerAvailable, "Docker is required for scheduled tenant-charge PostgreSQL tests.");

    private sealed class TestActor : ICurrentActor
    {
        public int? UserId => null;
        public string? ActorLabel => "integration:scheduled-tenant-charges";
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

    private sealed class CompanionFailureInterceptor : DbCommandInterceptor
    {
        public bool FailAtomicAudit { get; set; }
        public bool FailNotifications { get; set; }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            if (FailAtomicAudit
                && command.CommandText.Contains("INSERT INTO \"AtomicAuditLogs\"", StringComparison.Ordinal))
            {
                throw new InvalidOperationException("injected scheduled tenant-charge audit failure");
            }
            if (FailNotifications
                && command.CommandText.Contains("INSERT INTO \"Notifications\"", StringComparison.Ordinal))
            {
                throw new InvalidOperationException("injected scheduled tenant-charge notification failure");
            }

            return ValueTask.FromResult(result);
        }

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            if (FailAtomicAudit
                && command.CommandText.Contains("INSERT INTO \"AtomicAuditLogs\"", StringComparison.Ordinal))
            {
                throw new InvalidOperationException("injected scheduled tenant-charge audit failure");
            }
            if (FailNotifications
                && command.CommandText.Contains("INSERT INTO \"Notifications\"", StringComparison.Ordinal))
            {
                throw new InvalidOperationException("injected scheduled tenant-charge notification failure");
            }

            return ValueTask.FromResult(result);
        }
    }

    private sealed class FixedTimeProvider(DateTime utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(utcNow);
    }

    private sealed record Scenario(
        int PortfolioId,
        int AccountId,
        Guid AgreementPublicId,
        int AgreementId,
        int LeaseManagementId,
        int LeaseManagementPartyId,
        int UserId,
        int AccessContextId,
        long AccessRevision,
        int? InitialRevokedUserId)
    {
        public int? RevokedUserId { get; set; } = InitialRevokedUserId;
        public int? CoTenantUserId { get; set; }
        public int? CoTenantAccessContextId { get; set; }
        public long? CoTenantAccessRevision { get; set; }
        public int? DisabledInAppUserId { get; set; }
    }
}
