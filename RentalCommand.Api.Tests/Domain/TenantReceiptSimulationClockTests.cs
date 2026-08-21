using System.Collections.Concurrent;
using System.Data.Common;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using RentalCommand.Core.Atomic;
using RentalCommand.Core.Authorization;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Interfaces;
using RentalCommand.Core.Navigation;
using RentalCommand.Core.Outbox;
using RentalCommand.Core.Payments;
using RentalCommand.Core.Scanning;
using RentalCommand.Core.Time;
using RentalCommand.Data;
using RentalCommand.Data.Atomic;
using RentalCommand.Data.Accounting;
using RentalCommand.Data.Auditing;
using RentalCommand.Data.Payments;
using RentalCommand.Data.Scanning;
using RentalCommand.TestCommon;
using RentalCommand.Api.Tests.Scanning;
using RentalCommand.Api.Writes;

namespace RentalCommand.Api.Tests.Domain;

[Collection(MigratedPostgreSqlCollection.Name)]
public sealed class TenantReceiptSimulationClockTests : IAsyncLifetime
{
    private const int PortfolioId = 1;
    private static readonly DateTime SimulatedEntryAtUtc =
        new(2027, 01, 05, 14, 30, 00, DateTimeKind.Utc);
    private static readonly AtomicJsonResultCodec<RecordTenantReceiptResult> ReceiptCodec =
        new("tenant-account.receipt.record.v1");
    private static readonly AtomicJsonResultCodec<SecurityDepositMutationResult> DepositCodec =
        new("tenant-account.security-deposit.mutation.v1");

    private readonly MigratedPostgreSqlFixture _fixture;
    private MigratedPostgreSqlTestContext _ctx = null!;
    private ServiceProvider _services = null!;
    private IAtomicUnitOfWork _atomic = null!;
    private WorkspaceReadScope _scope;
    private CommandRecorder Recorder => _services.GetRequiredService<CommandRecorder>();
    private NotificationFailureInterceptor Failures => _services.GetRequiredService<NotificationFailureInterceptor>();

    public TenantReceiptSimulationClockTests(MigratedPostgreSqlFixture fixture)
    {
        _fixture = fixture;
    }

    public async Task InitializeAsync()
    {
        _ctx = await _fixture.CreateContextAsync();
        await new ChartOfAccountsSeedService(_ctx.Db).SeedAsync(PortfolioId);
        await _ctx.Db.SaveChangesAsync();
        _scope = _ctx.Db.SeedAdministratorScope(PortfolioId, nameof(TenantReceiptSimulationClockTests));
        await FreezeSimulationClockAsync();
        _services = BuildServices(_ctx.ConnectionString, SimulatedEntryAtUtc);
        _atomic = _services.GetRequiredService<IAtomicUnitOfWork>();
    }

    public async Task DisposeAsync()
    {
        await _services.DisposeAsync();
        await _ctx.DisposeAsync();
    }

    [Fact]
    public async Task ManualReceipt_UsesCommandSimulationClockForAttemptLedgerAllocationAndOutbox()
    {
        var graph = SeedTenantAccountWithOpenCharge("manual-sim-clock", 1_200m);
        var command = ReceiptCommand(
            graph.AccountId, graph.ChargeEntryId, 1_200m, "manual-sim-clock", SimulatedEntryAtUtc);

        var outcome = await ExecuteReceiptAsync(
            new AtomicCommandIdentity("tenant-account.receipt.record", command.DeliveryIdempotencyKey),
            command,
            ReceiptCodec);

        outcome.Value.AllocatedAmount.Should().Be(1_200m);
        outcome.Value.AllocationCount.Should().Be(1);
        _ctx.Db.ChangeTracker.Clear();
        var outboxKey = OutboxIdempotency.Create("tenant-money", command.DeliveryIdempotencyKey);
        var row = await (
            from entry in _ctx.Db.TenantLedgerEntries.AsNoTracking()
            join attempt in _ctx.Db.TenantPaymentAttempts.AsNoTracking()
                on entry.ProviderPaymentAttemptId equals (long?)attempt.Id
            join allocation in _ctx.Db.TenantLedgerAllocations.AsNoTracking()
                on entry.Id equals allocation.CreditEntryId
            join outbox in _ctx.Db.OutboxMessages.AsNoTracking()
                on entry.PortfolioId equals outbox.PortfolioId
            where entry.Id == outcome.Value.LedgerEntryId
                && outbox.IdempotencyKey == outboxKey
            select new
            {
                entry.PostedAtUtc,
                attempt.PreparedAtUtc,
                attempt.SubmittedAtUtc,
                attempt.SettledAtUtc,
                attempt.UpdatedAtUtc,
                allocation.AllocatedAtUtc,
                OutboxCreatedAtUtc = outbox.CreatedAtUtc,
                OutboxNextAttemptAtUtc = outbox.NextAttemptAtUtc,
            }).SingleAsync();

        row.PostedAtUtc.Should().Be(SimulatedEntryAtUtc);
        row.PreparedAtUtc.Should().Be(SimulatedEntryAtUtc);
        row.SubmittedAtUtc.Should().Be(SimulatedEntryAtUtc);
        row.SettledAtUtc.Should().Be(SimulatedEntryAtUtc);
        row.UpdatedAtUtc.Should().Be(SimulatedEntryAtUtc);
        row.AllocatedAtUtc.Should().Be(SimulatedEntryAtUtc);
        row.OutboxCreatedAtUtc.Should().Be(SimulatedEntryAtUtc);
        row.OutboxNextAttemptAtUtc.Should().Be(SimulatedEntryAtUtc);
    }

    [Fact]
    public async Task ManualReceipt_StagesTenantRentReceivedNotificationOutboxAndReplayDedupe()
    {
        var graph = SeedTenantAccountWithOpenCharge("receipt-notification", 1_200m);
        var primary = SeedTenantPortalAccess(
            graph,
            "receipt-notification-primary",
            graph.PrimaryPartyId,
            enableInApp: true,
            enableEmail: true,
            enableSms: true,
            enablePush: true);
        var coTenant = SeedTenantPortalAccess(
            graph,
            "receipt-notification-email-only",
            null,
            LeaseManagementPartyRole.CoTenant,
            enableInApp: false,
            enableEmail: true,
            enableSms: false,
            enablePush: false);
        var revoked = SeedTenantPortalAccess(
            graph,
            "receipt-notification-revoked",
            null,
            LeaseManagementPartyRole.Occupant,
            enableInApp: true,
            enableEmail: true,
            enableSms: true,
            enablePush: true,
            revokedAtUtc: SimulatedEntryAtUtc.AddMinutes(-1));
        var command = ReceiptCommand(
            graph.AccountId, graph.ChargeEntryId, 1_200m, "receipt-notification", SimulatedEntryAtUtc);
        var identity = new AtomicCommandIdentity(
            "tenant-account.receipt.record", command.DeliveryIdempotencyKey);
        Recorder.Clear();

        var outcome = await ExecuteReceiptAsync(identity, command, ReceiptCodec);
        var replay = await ExecuteReceiptAsync(identity, command, ReceiptCodec);

        outcome.Disposition.Should().Be(AtomicCommandDisposition.Executed);
        replay.Disposition.Should().Be(AtomicCommandDisposition.Replayed);
        replay.Value.Should().Be(outcome.Value);
        _ctx.Db.ChangeTracker.Clear();
        var notification = await _ctx.Db.Notifications.AsNoTracking()
            .SingleAsync(row =>
                row.PortfolioId == PortfolioId
                && row.UserId == primary.UserId
                && row.Type == "TenantRentReceived"
                && row.RelatedEntityType == nameof(TenantLedgerEntry)
                && row.RelatedEntityId == outcome.Value.LedgerEntryId);
        notification.Title.Should().Be("Rent received");
        notification.Message.Should().NotContain("1200");
        notification.NavigationExperience.Should().Be(NavigationExperience.Tenant);
        notification.NavigationDestination.Should().Be(NavigationDestination.TenantLedgerEntry);
        notification.NavigationAccessContextId.Should().Be(primary.AccessContextId);
        notification.NavigationAccessRevision.Should().Be(primary.AccessRevision);
        notification.NavigationResourceKind.Should().Be(nameof(TenantLedgerEntry));
        notification.NavigationResourceId.Should().Be((int)outcome.Value.LedgerEntryId);
        notification.NavigationParentResourceKind.Should().Be(nameof(TenantAccount));
        notification.NavigationParentResourceId.Should().Be(graph.AccountId);
        notification.NavigationFallbackDestination.Should().Be(NavigationDestination.Home);

        (await _ctx.Db.NotificationReadStates.AsNoTracking()
            .CountAsync(row =>
                row.PortfolioId == PortfolioId
                && row.NotificationId == notification.Id
                && row.UserId == primary.UserId)).Should().Be(0);
        (await _ctx.Db.Notifications.AsNoTracking()
            .CountAsync(row =>
                row.PortfolioId == PortfolioId
                && row.Type == "TenantRentReceived"
                && row.RelatedEntityId == outcome.Value.LedgerEntryId)).Should().Be(1,
                "the co-tenant disabled in-app alerts and the revoked tenant has no effective access");
        (await _ctx.Db.Notifications.AsNoTracking()
            .CountAsync(row => row.PortfolioId == PortfolioId && row.UserId == coTenant.UserId)).Should().Be(0);
        (await _ctx.Db.Notifications.AsNoTracking()
            .CountAsync(row => row.PortfolioId == PortfolioId && row.UserId == revoked.UserId)).Should().Be(0);

        var outbox = await _ctx.Db.OutboxMessages.AsNoTracking()
            .Where(row =>
                row.PortfolioId == PortfolioId
                && row.IdempotencyKey.Contains("tenant-rent-received-notification"))
            .OrderBy(row => row.MessageType)
            .Select(row => new { row.MessageType, row.Payload, row.IdempotencyKey })
            .ToListAsync();
        outbox.Select(row => row.MessageType).Should().Equal(
            "data-update",
            "email",
            "email",
            "push",
            "sms");
        outbox.Should().OnlyContain(row => !row.Payload.Contains("1200", StringComparison.Ordinal));
        outbox.Count(row => row.Payload.Contains(primary.Email, StringComparison.Ordinal)).Should().Be(1);
        outbox.Count(row => row.Payload.Contains(coTenant.Email, StringComparison.Ordinal)).Should().Be(1);
        outbox.Should().NotContain(row => row.Payload.Contains(revoked.Email, StringComparison.Ordinal));
        outbox.Should().ContainSingle(row => row.Payload.Contains(primary.PushToken!, StringComparison.Ordinal));
        outbox.Should().ContainSingle(row => row.Payload.Contains(primary.PhoneNumber!, StringComparison.Ordinal));

        (await _ctx.Db.AtomicAuditLogs.AsNoTracking()
            .CountAsync(row =>
                row.CommandType == identity.CommandType
                && row.CommandIdempotencyKey == identity.IdempotencyKey
                && row.EntityType == nameof(Notification)
                && row.EntityId == notification.Id
                && row.Operation == AuditLogOperation.Created
                && row.ChangeReason == "Posted tenant rent-received notification.")).Should().Be(1);
        (await _ctx.Db.AtomicCommandReceipts.AsNoTracking()
            .CountAsync(row =>
                row.CommandType == identity.CommandType
                && row.IdempotencyKey == identity.IdempotencyKey)).Should().Be(1);
        Recorder.Commands.Count(sql =>
            sql.Contains("YS-304 tenant receipt notification recipients", StringComparison.Ordinal))
            .Should().Be(1, "recipient filtering, joining, ordering, and preference eligibility run in one tagged SQL query");
    }

    [Fact]
    public async Task ManualReceipt_UsesBusinessDateForTenantNotificationClockAndWallClockForOutboxAudit()
    {
        var businessNowUtc = new DateTime(2027, 02, 20, 17, 00, 00, DateTimeKind.Utc);
        var wallRecordedAtUtc = new DateTime(2026, 07, 30, 16, 00, 00, DateTimeKind.Utc);
        var receiptEffectiveOn = new DateOnly(2027, 02, 12);
        var expectedNotificationCreatedAtUtc = new DateTime(2027, 02, 20, 12, 00, 00, DateTimeKind.Utc);
        var expectedNotificationExpiresAtUtc = new DateTime(2027, 03, 22, 23, 59, 59, DateTimeKind.Utc);
        await FreezeSimulationClockAtAsync(businessNowUtc);
        var graph = SeedTenantAccountWithOpenCharge("receipt-business-clock", 500m);
        var primary = SeedTenantPortalAccess(
            graph,
            "receipt-business-clock-primary",
            graph.PrimaryPartyId,
            enableInApp: true,
            enableEmail: true,
            enableSms: true,
            enablePush: true);
        var command = ReceiptCommand(
                graph.AccountId,
                graph.ChargeEntryId,
                500m,
                "receipt-business-clock",
                wallRecordedAtUtc)
            with { EffectiveOn = receiptEffectiveOn };
        var identity = new AtomicCommandIdentity(
            "tenant-account.receipt.record", command.DeliveryIdempotencyKey);

        var outcome = await ExecuteReceiptAsync(identity, command, ReceiptCodec);

        outcome.Disposition.Should().Be(AtomicCommandDisposition.Executed);
        _ctx.Db.ChangeTracker.Clear();
        var notification = await _ctx.Db.Notifications.AsNoTracking()
            .SingleAsync(row =>
                row.PortfolioId == PortfolioId
                && row.UserId == primary.UserId
                && row.Type == "TenantRentReceived"
                && row.RelatedEntityType == nameof(TenantLedgerEntry)
                && row.RelatedEntityId == outcome.Value.LedgerEntryId);
        notification.CreatedAt.Should().Be(expectedNotificationCreatedAtUtc);
        notification.NavigationExpiresAtUtc.Should().Be(expectedNotificationExpiresAtUtc);
        notification.NavigationExpiresAtUtc.Should().BeAfter(businessNowUtc);

        var metadataTimes = await (
            from audit in _ctx.Db.AtomicAuditLogs.AsNoTracking()
            join outbox in _ctx.Db.OutboxMessages.AsNoTracking()
                on audit.PortfolioId equals outbox.PortfolioId
            where audit.CommandType == identity.CommandType
                && audit.CommandIdempotencyKey == identity.IdempotencyKey
                && audit.EntityType == nameof(Notification)
                && audit.EntityId == notification.Id
                && audit.Operation == AuditLogOperation.Created
                && outbox.IdempotencyKey.Contains("tenant-rent-received-notification")
            select new
            {
                AuditTimestamp = audit.Timestamp,
                outbox.CreatedAtUtc,
                outbox.NextAttemptAtUtc,
                outbox.MessageType,
                outbox.Payload,
            })
            .OrderBy(row => row.MessageType)
            .ToListAsync();
        metadataTimes.Should().NotBeEmpty();
        metadataTimes.Should().OnlyContain(row => row.AuditTimestamp == wallRecordedAtUtc);
        metadataTimes.Should().OnlyContain(row => row.CreatedAtUtc == wallRecordedAtUtc);
        metadataTimes.Should().OnlyContain(row => row.NextAttemptAtUtc == wallRecordedAtUtc);
        metadataTimes.Should().ContainSingle(row =>
            row.MessageType == "push"
            && row.Payload.Contains("2027-03-22T23:59:59Z", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ManualReceipt_NotificationFailureRollsBackAttemptLedgerAuditOutboxAndReceipt()
    {
        var graph = SeedTenantAccountWithOpenCharge("receipt-notification-rollback", 1_200m);
        SeedTenantPortalAccess(
            graph,
            "receipt-notification-rollback-primary",
            graph.PrimaryPartyId,
            enableInApp: true,
            enableEmail: true,
            enableSms: true,
            enablePush: true);
        var command = ReceiptCommand(
            graph.AccountId, graph.ChargeEntryId, 1_200m, "receipt-notification-rollback", SimulatedEntryAtUtc);
        var identity = new AtomicCommandIdentity(
            "tenant-account.receipt.record", command.DeliveryIdempotencyKey);
        Failures.FailNotifications = true;

        try
        {
            await FluentActions.Invoking(() => ExecuteReceiptAsync(identity, command, ReceiptCodec))
                .Should().ThrowAsync<InvalidOperationException>()
                .WithMessage("injected tenant receipt notification failure");
        }
        finally
        {
            Failures.FailNotifications = false;
        }

        _ctx.Db.ChangeTracker.Clear();
        (await _ctx.Db.TenantPaymentAttempts.AsNoTracking()
            .CountAsync(attempt => attempt.IdempotencyKey == command.DeliveryIdempotencyKey)).Should().Be(0);
        (await _ctx.Db.TenantLedgerEntries.AsNoTracking()
            .CountAsync(entry => entry.BusinessKey == command.BusinessKey)).Should().Be(0);
        (await _ctx.Db.Notifications.AsNoTracking()
            .CountAsync(row => row.PortfolioId == PortfolioId && row.Type == "TenantRentReceived")).Should().Be(0);
        (await _ctx.Db.AtomicAuditLogs.AsNoTracking()
            .CountAsync(row =>
                row.CommandType == identity.CommandType
                && row.CommandIdempotencyKey == identity.IdempotencyKey)).Should().Be(0);
        (await _ctx.Db.AtomicCommandReceipts.AsNoTracking()
            .CountAsync(row =>
                row.CommandType == identity.CommandType
                && row.IdempotencyKey == identity.IdempotencyKey)).Should().Be(0);
        (await _ctx.Db.OutboxMessages.AsNoTracking()
            .CountAsync(row =>
                row.PortfolioId == PortfolioId
                && row.IdempotencyKey.Contains("tenant-rent-received-notification"))).Should().Be(0);
    }

    [Fact]
    public async Task ScanPaymentConfirmation_CarriesConfirmedSimulationClockIntoReceiptMutation()
    {
        var graph = SeedTenantAccountWithOpenCharge("scan-sim-clock", 1_875m);
        var draft = SeedPaymentScanDraft(graph.AccountId, graph.ChargeEntryId);
        var command = ScanCommand(draft, graph.AccountId, 1_875m, SimulatedEntryAtUtc);

        var outcome = await ExecuteScanAsync(
            ScanConfirmationCommandIdentity.Create(PortfolioId, draft.Id, "scan-sim-clock"),
            command);

        outcome.Value.Outcome.Should().Be(ConfirmScanDraftOutcome.Confirmed);
        var ledgerEntryId = outcome.Value.LedgerEntryId!.Value;
        ledgerEntryId.Should().BePositive();
        _ctx.Db.ChangeTracker.Clear();
        var row = await (
            from entry in _ctx.Db.TenantLedgerEntries.AsNoTracking()
            join attempt in _ctx.Db.TenantPaymentAttempts.AsNoTracking()
                on entry.ProviderPaymentAttemptId equals (long?)attempt.Id
            join allocation in _ctx.Db.TenantLedgerAllocations.AsNoTracking()
                on entry.Id equals allocation.CreditEntryId
            where entry.Id == ledgerEntryId
            select new
            {
                entry.PostedAtUtc,
                attempt.PreparedAtUtc,
                attempt.SettledAtUtc,
                allocation.AllocatedAtUtc,
            }).SingleAsync();

        row.PostedAtUtc.Should().Be(SimulatedEntryAtUtc);
        row.PreparedAtUtc.Should().Be(SimulatedEntryAtUtc);
        row.SettledAtUtc.Should().Be(SimulatedEntryAtUtc);
        row.AllocatedAtUtc.Should().Be(SimulatedEntryAtUtc);
    }

    [Fact]
    public async Task ScanPaymentConfirmation_AllowsSameManualCheckNumberOnDifferentTenantAccounts()
    {
        const string repeatedCheckNumber = "000424242";
        var existingGraph = SeedTenantAccountWithOpenCharge("same-check-existing", 1_675m);
        var scanGraph = SeedTenantAccountWithOpenCharge("same-check-scan", 1_350m);
        var existingCommand = ReceiptCommand(
            existingGraph.AccountId,
            existingGraph.ChargeEntryId,
            1_675m,
            "same-check-existing",
            SimulatedEntryAtUtc.AddMinutes(-5),
            repeatedCheckNumber,
            repeatedCheckNumber);

        await ExecuteReceiptAsync(
            new AtomicCommandIdentity("tenant-account.receipt.record", existingCommand.DeliveryIdempotencyKey),
            existingCommand,
            ReceiptCodec);
        var draft = SeedPaymentScanDraft(scanGraph.AccountId, scanGraph.ChargeEntryId);
        var scanCommand = ScanCommand(
            draft,
            scanGraph.AccountId,
            1_350m,
            SimulatedEntryAtUtc,
            repeatedCheckNumber);

        var outcome = await ExecuteScanAsync(
            ScanConfirmationCommandIdentity.Create(PortfolioId, draft.Id, "scan-same-check"),
            scanCommand);

        outcome.Value.Outcome.Should().Be(ConfirmScanDraftOutcome.Confirmed);
        _ctx.Db.ChangeTracker.Clear();
        var attempts = await _ctx.Db.TenantPaymentAttempts.AsNoTracking()
            .Where(attempt => attempt.Provider == "manual"
                && attempt.ProviderObjectId == repeatedCheckNumber)
            .Select(attempt => new
            {
                attempt.TenantAccountId,
                attempt.Amount,
                attempt.CheckNumber,
            })
            .OrderBy(attempt => attempt.TenantAccountId)
            .ToListAsync();

        attempts.Should().HaveCount(2);
        attempts.Select(attempt => attempt.TenantAccountId)
            .Should().BeEquivalentTo([existingGraph.AccountId, scanGraph.AccountId]);
        attempts.Should().OnlyContain(attempt => attempt.CheckNumber == repeatedCheckNumber);
    }

    [Fact]
    public async Task ReceiptMutation_RollsBackPaymentAttemptWhenLaterLedgerInsertFails()
    {
        var graph = SeedTenantAccountWithOpenCharge("receipt-rollback", 1_200m);
        var duplicateBusinessKey = "receipt-rollback-duplicate";
        _ctx.Db.TenantLedgerEntries.Add(new TenantLedgerEntry
        {
            PortfolioId = PortfolioId,
            TenantAccountId = graph.AccountId,
            EntryType = TenantLedgerEntryType.PaymentReceipt,
            Direction = TenantLedgerDirection.Credit,
            Amount = 1m,
            Currency = "USD",
            EffectiveOn = new DateOnly(2027, 01, 05),
            PostedAtUtc = SimulatedEntryAtUtc.AddMinutes(-5),
            Description = "Conflicting receipt",
            BusinessKey = duplicateBusinessKey,
            CreatedByUserId = _scope.UserId,
        });
        await _ctx.Db.SaveChangesAsync();
        _ctx.Db.ChangeTracker.Clear();
        var command = ReceiptCommand(
                graph.AccountId, graph.ChargeEntryId, 1_200m, "receipt-rollback", SimulatedEntryAtUtc)
            with { BusinessKey = duplicateBusinessKey };

        Func<Task> act = async () => await ExecuteReceiptAsync(
            new AtomicCommandIdentity("tenant-account.receipt.record", command.DeliveryIdempotencyKey),
            command,
            ReceiptCodec);

        await act.Should().ThrowAsync<DbUpdateException>();
        _ctx.Db.ChangeTracker.Clear();
        (await _ctx.Db.TenantPaymentAttempts.AsNoTracking()
            .CountAsync(attempt => attempt.IdempotencyKey == command.DeliveryIdempotencyKey))
            .Should().Be(0);
        (await _ctx.Db.AtomicCommandReceipts.AsNoTracking()
            .CountAsync(receipt => receipt.CommandType == "tenant-account.receipt.record"
                && receipt.IdempotencyKey == command.DeliveryIdempotencyKey))
            .Should().Be(0);
    }

    [Fact]
    public async Task DepositFunding_UsesSimulationClockForBusinessRowsButWallClockForManualAttempt()
    {
        var graph = SeedTenantAccountWithOpenDepositCharge("deposit-sim-clock", 1_675m);
        var command = FundDepositCommand(graph, 1_675m, "deposit-sim-clock");

        var beforeWallClock = DateTime.UtcNow.AddSeconds(-5);
        var outcome = await ExecuteFundAsync(
            new AtomicCommandIdentity("tenant-account.deposit.fund", command.DeliveryIdempotencyKey),
            command,
            DepositCodec);
        var afterWallClock = DateTime.UtcNow.AddSeconds(5);

        outcome.Value.Applied.Should().BeTrue();
        _ctx.Db.ChangeTracker.Clear();
        var outboxKey = OutboxIdempotency.Create("tenant-money", command.DeliveryIdempotencyKey);
        var row = await (
            from deposit in _ctx.Db.SecurityDepositEntries.AsNoTracking()
            join receipt in _ctx.Db.TenantLedgerEntries.AsNoTracking()
                on deposit.TenantLedgerEntryId equals receipt.Id
            join attempt in _ctx.Db.TenantPaymentAttempts.AsNoTracking()
                on receipt.ProviderPaymentAttemptId equals (long?)attempt.Id
            join allocation in _ctx.Db.TenantLedgerAllocations.AsNoTracking()
                on receipt.Id equals allocation.CreditEntryId
            join audit in _ctx.Db.AtomicAuditLogs.AsNoTracking()
                on receipt.TenantAccountId equals audit.EntityId
            join outbox in _ctx.Db.OutboxMessages.AsNoTracking()
                on receipt.PortfolioId equals outbox.PortfolioId
            where deposit.Id == outcome.Value.SecurityDepositEntryId
                && audit.CommandType == "tenant-account.deposit.fund"
                && audit.EntityType == nameof(TenantAccount)
                && outbox.IdempotencyKey == outboxKey
            select new
            {
                ReceiptPostedAtUtc = receipt.PostedAtUtc,
                AllocationAllocatedAtUtc = allocation.AllocatedAtUtc,
                DepositPostedAtUtc = deposit.PostedAtUtc,
                AuditTimestamp = audit.Timestamp,
                OutboxCreatedAtUtc = outbox.CreatedAtUtc,
                OutboxNextAttemptAtUtc = outbox.NextAttemptAtUtc,
                attempt.PreparedAtUtc,
                attempt.SubmittedAtUtc,
                attempt.SettledAtUtc,
                attempt.UpdatedAtUtc,
            }).SingleAsync();

        row.ReceiptPostedAtUtc.Should().Be(SimulatedEntryAtUtc);
        row.AllocationAllocatedAtUtc.Should().Be(SimulatedEntryAtUtc);
        row.DepositPostedAtUtc.Should().Be(SimulatedEntryAtUtc);
        row.AuditTimestamp.Should().Be(SimulatedEntryAtUtc);
        row.OutboxCreatedAtUtc.Should().Be(SimulatedEntryAtUtc);
        row.OutboxNextAttemptAtUtc.Should().Be(SimulatedEntryAtUtc);
        row.PreparedAtUtc.Should().BeOnOrAfter(beforeWallClock);
        row.PreparedAtUtc.Should().BeOnOrBefore(afterWallClock);
        row.SubmittedAtUtc.Should().Be(row.PreparedAtUtc);
        row.SettledAtUtc.Should().Be(row.PreparedAtUtc);
        row.UpdatedAtUtc.Should().Be(row.PreparedAtUtc);

        var depositJournals = await _ctx.Db.JournalEntries.AsNoTracking()
            .Include(entry => entry.Lines)
            .ThenInclude(line => line.LedgerAccount)
            .Where(entry => entry.PortfolioId == PortfolioId
                && entry.SourceType == JournalSourceType.TenantReceipt
                && entry.SourceId == outcome.Value.TenantLedgerEntryId)
            .ToListAsync();
        depositJournals.Should().ContainSingle();
        depositJournals[0].Lines.Should().HaveCount(2);
        depositJournals[0].Lines.Should().ContainSingle(line =>
            line.DebitAmount == 1_675m
            && line.LedgerAccount!.SystemKey == "security-deposit-trust-cash");
        depositJournals[0].Lines.Should().ContainSingle(line =>
            line.CreditAmount == 1_675m
            && line.LedgerAccount!.SystemKey == "tenant-accounts-receivable");
        depositJournals[0].Lines.Should().NotContain(line =>
            line.LedgerAccount!.AccountType == AccountType.Income);
    }

    [Fact]
    public async Task TargetedDepositReceipt_RejectsAmountAboveTheDepositOpenAmount()
    {
        var graph = SeedTenantAccountWithOpenDepositCharge("targeted-deposit-overage", 500m);
        _ctx.Db.TenantLedgerEntries.Add(new TenantLedgerEntry
        {
            PortfolioId = PortfolioId,
            TenantAccountId = graph.AccountId,
            EntryType = TenantLedgerEntryType.RentCharge,
            Direction = TenantLedgerDirection.Debit,
            Amount = 700m,
            Currency = "USD",
            EffectiveOn = new DateOnly(2027, 01, 06),
            DueOn = new DateOnly(2027, 01, 06),
            PostedAtUtc = SimulatedEntryAtUtc,
            Description = "Rent charge that must not become deposit trust cash",
            BusinessKey = "rent-targeted-deposit-overage",
            LeaseAgreementId = graph.LeaseAgreementId,
            CreatedByUserId = _scope.UserId,
        });
        await _ctx.Db.SaveChangesAsync();
        _ctx.Db.ChangeTracker.Clear();

        var command = ReceiptCommand(
                graph.AccountId,
                graph.DepositChargeEntryId,
                1_200m,
                "targeted-deposit-overage",
                SimulatedEntryAtUtc)
            with { AllocateOldestCharges = true };

        Func<Task> act = () => ExecuteReceiptAsync(
            new AtomicCommandIdentity("tenant-account.receipt.record", command.DeliveryIdempotencyKey),
            command,
            ReceiptCodec);

        await act.Should().ThrowAsync<ArgumentException>()
            .WithMessage("*deposit target open amount*");

        _ctx.Db.ChangeTracker.Clear();
        (await _ctx.Db.TenantLedgerEntries.AsNoTracking()
            .CountAsync(entry => entry.BusinessKey == command.BusinessKey)).Should().Be(0);
        (await _ctx.Db.JournalEntries.AsNoTracking()
            .CountAsync(entry => entry.SourceBusinessKey == command.BusinessKey)).Should().Be(0);
    }

    private static ServiceProvider BuildServices(string connectionString, DateTime utcNow)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<TimeProvider>(new FixedTimeProvider(utcNow));
        services.AddSingleton<CommandRecorder>();
        services.AddSingleton<NotificationFailureInterceptor>();
        services.AddScoped<ICurrentActor, SystemCurrentActor>();
        services.AddAtomicPersistenceKernel();
        services.AddScoped<IRequestWriteExecutor, RequestWriteExecutor>();
        services.AddScoped<IScanConfirmationTargetWriter, ProductionScanConfirmationTargetWriter>();
        services.AddAtomicCommandHandler<
            ConfirmScanDraftCommand,
            ConfirmScanDraftResult,
            ConfirmScanDraftHandler>();
        services.AddDbContext<RentalCommandDbContext>((provider, builder) =>
            builder.UseNpgsql(connectionString)
                .UseAtomicPersistenceKernel(provider)
                .AddInterceptors(
                    provider.GetRequiredService<CommandRecorder>(),
                    provider.GetRequiredService<NotificationFailureInterceptor>()));
        return services.BuildServiceProvider();
    }

    private async Task<AtomicCommandOutcome<ConfirmScanDraftResult>> ExecuteScanAsync(
        AtomicCommandIdentity identity,
        ConfirmScanDraftCommand command)
    {
        await using var scope = _services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<RentalCommandDbContext>();
        var writer = scope.ServiceProvider.GetRequiredService<IScanConfirmationTargetWriter>();
        return await scope.ServiceProvider.GetRequiredService<IRequestWriteExecutor>()
            .ExecuteAsync(
                identity.IdempotencyKey,
                ScanDraftWriteSupport.Write(
                    identity.CommandType, command, ScanDraftWriteSupport.ConfirmResultContract,
                    (request, context, token) => ConfirmScanDraftHandler.ExecuteAsync(
                        db, writer, request, context, token),
                    (request, context, token) => ConfirmScanDraftHandler.AuthorizeAsync(
                        writer, request, context, token)));
    }

    private async Task<AtomicCommandOutcome<RecordTenantReceiptResult>> ExecuteReceiptAsync(
        AtomicCommandIdentity identity,
        RecordTenantReceiptCommand command,
        AtomicJsonResultCodec<RecordTenantReceiptResult> codec)
    {
        await using var scope = _services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<RentalCommandDbContext>();
        var handler = new RecordTenantReceiptHandler(db);
        return await scope.ServiceProvider.GetRequiredService<IRequestWriteExecutor>()
            .ExecuteAsync(identity.IdempotencyKey, TenantMoneyWriteSupport.Write(
                command, handler.ExecuteAsync, handler.AuthorizeAsync));
    }

    private async Task<AtomicCommandOutcome<SecurityDepositMutationResult>> ExecuteFundAsync(
        AtomicCommandIdentity identity,
        FundSecurityDepositCommand command,
        AtomicJsonResultCodec<SecurityDepositMutationResult> codec)
    {
        await using var scope = _services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<RentalCommandDbContext>();
        var handler = new FundSecurityDepositHandler(db);
        return await scope.ServiceProvider.GetRequiredService<IRequestWriteExecutor>()
            .ExecuteAsync(identity.IdempotencyKey, TenantMoneyWriteSupport.Write(
                command, handler.ExecuteAsync, handler.AuthorizeAsync));
    }

    private async Task FreezeSimulationClockAsync()
    {
        await FreezeSimulationClockAtAsync(SimulatedEntryAtUtc);
    }

    private async Task FreezeSimulationClockAtAsync(DateTime simulatedUtc)
    {
        var clock = await _ctx.Db.SimulationClocks.SingleOrDefaultAsync(clock => clock.Id == 1);
        if (clock is null)
        {
            _ctx.Db.SimulationClocks.Add(new SimulationClock
            {
                Id = 1,
                Mode = ClockMode.Frozen,
                SimAnchorUtc = simulatedUtc,
                RealAnchorUtc = DateTime.UtcNow,
                TimeZoneId = "America/New_York",
            });
        }
        else
        {
            clock.Mode = ClockMode.Frozen;
            clock.SimAnchorUtc = simulatedUtc;
            clock.RealAnchorUtc = DateTime.UtcNow;
            clock.TimeZoneId = "America/New_York";
        }

        await _ctx.Db.SaveChangesAsync();
        _ctx.Db.ChangeTracker.Clear();
    }

    private TenantAccountGraph SeedTenantAccountWithOpenCharge(string suffix, decimal amount)
    {
        var seededAt = new DateTime(2026, 12, 15, 12, 0, 0, DateTimeKind.Utc);
        var property = new Property
        {
            PortfolioId = PortfolioId,
            Name = $"Receipt Test Property {suffix}",
            AddressLine1 = "100 Receipt Street",
            City = "Columbus",
            State = "OH",
            PostalCode = "43215",
            CreatedAt = seededAt,
            UpdatedAt = seededAt,
        };
        var unit = new Unit
        {
            PortfolioId = PortfolioId,
            Property = property,
            UnitNumber = suffix,
            MarketRent = amount,
            CreatedAt = seededAt,
            UpdatedAt = seededAt,
        };
        var tenant = new Tenant
        {
            PortfolioId = PortfolioId,
            FirstName = "Receipt",
            LastName = suffix,
            CreatedAt = seededAt,
            UpdatedAt = seededAt,
        };
        var relationship = new LeaseManagement
        {
            PortfolioId = PortfolioId,
            Property = property,
            Unit = unit,
            RelationshipNumber = $"LM-{suffix}",
            PossessionGivenAtUtc = seededAt,
            CreatedAtUtc = seededAt,
            UpdatedAtUtc = seededAt,
            CreatedByUserId = _scope.UserId,
            RowVersion = Guid.NewGuid(),
        };
        var primaryParty = new LeaseManagementParty
        {
            PortfolioId = PortfolioId,
            Tenant = tenant,
            Role = LeaseManagementPartyRole.PrimaryTenant,
            EffectiveFrom = new DateOnly(2027, 01, 01),
            ChangeReason = "Test setup",
            CreatedAtUtc = seededAt,
            CreatedByUserId = _scope.UserId,
        };
        relationship.Parties.Add(primaryParty);
        var account = new TenantAccount
        {
            PortfolioId = PortfolioId,
            LeaseManagement = relationship,
            AccountNumber = $"TA-{suffix}",
            Currency = "USD",
            OpenedAtUtc = seededAt,
            CreatedAtUtc = seededAt,
            CreatedByUserId = _scope.UserId,
        };
        var charge = new TenantLedgerEntry
        {
            PortfolioId = PortfolioId,
            TenantAccount = account,
            EntryType = TenantLedgerEntryType.ManualCharge,
            Direction = TenantLedgerDirection.Debit,
            Amount = amount,
            Currency = "USD",
            EffectiveOn = new DateOnly(2027, 01, 01),
            DueOn = new DateOnly(2027, 01, 01),
            PostedAtUtc = seededAt,
            Description = "January 2027 test charge",
            BusinessKey = $"charge-{suffix}",
            CreatedByUserId = _scope.UserId,
        };
        _ctx.Db.AddRange(property, unit, tenant, relationship, account, charge);
        _ctx.Db.SaveChanges();
        _ctx.Db.ChangeTracker.Clear();
        return new TenantAccountGraph(account.Id, charge.Id, relationship.Id, primaryParty.Id);
    }

    private TenantPortalAccessGraph SeedTenantPortalAccess(
        TenantAccountGraph graph,
        string suffix,
        int? existingPartyId,
        LeaseManagementPartyRole role = LeaseManagementPartyRole.PrimaryTenant,
        bool enableInApp = true,
        bool enableEmail = true,
        bool enableSms = false,
        bool enablePush = true,
        DateTime? revokedAtUtc = null)
    {
        var seededAt = new DateTime(2026, 12, 15, 12, 0, 0, DateTimeKind.Utc);
        var normalized = suffix.ToUpperInvariant().Replace('-', '.');
        var user = new ApplicationUser
        {
            UserName = $"tenant-{suffix}@example.test",
            NormalizedUserName = $"TENANT-{normalized}@EXAMPLE.TEST",
            Email = $"tenant-{suffix}@example.test",
            NormalizedEmail = $"TENANT-{normalized}@EXAMPLE.TEST",
            PhoneNumber = "+15555550199",
            DisplayName = $"Tenant {suffix}",
            SecurityStamp = Guid.NewGuid().ToString("N"),
            ConcurrencyStamp = Guid.NewGuid().ToString("N"),
            CreatedAt = seededAt,
        };
        _ctx.Db.Users.Add(user);
        _ctx.Db.SaveChanges();

        var accessContext = new WorkspaceAccessContext
        {
            UserId = user.Id,
            PortfolioId = PortfolioId,
            Status = WorkspaceAccessContextStatus.Active,
            LastAuthorizedExperience = WorkspaceExperience.Tenant,
            CreatedAtUtc = seededAt,
            UpdatedAtUtc = seededAt,
        };
        _ctx.Db.WorkspaceAccessContexts.Add(accessContext);
        _ctx.Db.SaveChanges();

        var partyId = existingPartyId;
        if (partyId is null)
        {
            var tenant = new Tenant
            {
                PortfolioId = PortfolioId,
                FirstName = "Portal",
                LastName = suffix,
                Email = $"portal-{suffix}@example.test",
                Phone = "+15555550299",
                CreatedAt = seededAt,
                UpdatedAt = seededAt,
            };
            _ctx.Db.Tenants.Add(tenant);
            _ctx.Db.SaveChanges();
            var party = new LeaseManagementParty
            {
                PortfolioId = PortfolioId,
                LeaseManagementId = graph.LeaseManagementId,
                TenantId = tenant.Id,
                Role = role,
                EffectiveFrom = new DateOnly(2027, 01, 01),
                ChangeReason = "receipt notification test tenant access",
                CreatedAtUtc = seededAt,
                CreatedByUserId = _scope.UserId,
            };
            _ctx.Db.LeaseManagementParties.Add(party);
            _ctx.Db.SaveChanges();
            partyId = party.Id;
        }

        _ctx.Db.UserAlertPreferences.Add(new UserAlertPreference
        {
            PortfolioId = PortfolioId,
            UserId = user.Id,
            EnableInApp = enableInApp,
            EnableEmail = enableEmail,
            EnableSms = enableSms,
            EnableMobilePush = enablePush,
            CreatedAtUtc = seededAt,
            UpdatedAtUtc = seededAt,
        });
        string? pushToken = null;
        if (enablePush)
        {
            pushToken = $"push-token-{suffix}";
            _ctx.Db.DeviceTokens.Add(new DeviceToken
            {
                PortfolioId = PortfolioId,
                UserId = user.Id,
                Token = pushToken,
                Platform = "android",
                CreatedAt = seededAt,
                LastSeenAt = seededAt,
            });
        }
        _ctx.Db.TenantUserAccesses.Add(new TenantUserAccess
        {
            PublicId = Guid.NewGuid(),
            PortfolioId = PortfolioId,
            AccessContextId = accessContext.Id,
            ApplicationUserId = user.Id,
            LeaseManagementPartyId = partyId.Value,
            GrantedAtUtc = seededAt,
            RevokedAtUtc = revokedAtUtc,
            GrantedByUserId = _scope.UserId,
            RevokedByUserId = revokedAtUtc is null ? null : _scope.UserId,
            Reason = "receipt notification test tenant portal access",
        });
        _ctx.Db.SaveChanges();
        _ctx.Db.ChangeTracker.Clear();
        return new TenantPortalAccessGraph(
            user.Id,
            accessContext.Id,
            accessContext.AccessRevision,
            user.Email!,
            user.PhoneNumber,
            pushToken);
    }

    private TenantDepositGraph SeedTenantAccountWithOpenDepositCharge(string suffix, decimal amount)
    {
        var seededAt = new DateTime(2026, 12, 15, 12, 0, 0, DateTimeKind.Utc);
        var property = new Property
        {
            PortfolioId = PortfolioId,
            Name = $"Deposit Test Property {suffix}",
            AddressLine1 = "200 Deposit Street",
            City = "Columbus",
            State = "OH",
            PostalCode = "43215",
            CreatedAt = seededAt,
            UpdatedAt = seededAt,
        };
        var unit = new Unit
        {
            PortfolioId = PortfolioId,
            Property = property,
            UnitNumber = suffix,
            MarketRent = amount,
            CreatedAt = seededAt,
            UpdatedAt = seededAt,
        };
        var tenant = new Tenant
        {
            PortfolioId = PortfolioId,
            FirstName = "Deposit",
            LastName = suffix,
            CreatedAt = seededAt,
            UpdatedAt = seededAt,
        };
        var relationship = new LeaseManagement
        {
            PortfolioId = PortfolioId,
            Property = property,
            Unit = unit,
            RelationshipNumber = $"DLM-{suffix}",
            PossessionGivenAtUtc = seededAt,
            CreatedAtUtc = seededAt,
            UpdatedAtUtc = seededAt,
            CreatedByUserId = _scope.UserId,
            RowVersion = Guid.NewGuid(),
        };
        relationship.Parties.Add(new LeaseManagementParty
        {
            PortfolioId = PortfolioId,
            Tenant = tenant,
            Role = LeaseManagementPartyRole.PrimaryTenant,
            EffectiveFrom = new DateOnly(2027, 01, 01),
            ChangeReason = "Test setup",
            CreatedAtUtc = seededAt,
            CreatedByUserId = _scope.UserId,
        });
        var account = new TenantAccount
        {
            PortfolioId = PortfolioId,
            LeaseManagement = relationship,
            AccountNumber = $"DTA-{suffix}",
            Currency = "USD",
            OpenedAtUtc = seededAt,
            CreatedAtUtc = seededAt,
            CreatedByUserId = _scope.UserId,
        };
        var source = LegalDocumentSourceVersionTestData.BuiltIn(
            PortfolioId,
            _scope.UserId,
            seededAt,
            $"deposit-sim-clock-{suffix}");
        var agreement = new LeaseAgreement
        {
            PublicId = Guid.NewGuid(),
            PortfolioId = PortfolioId,
            LeaseManagement = relationship,
            VersionNumber = 1,
            AgreementNumber = $"DAGR-{suffix}",
            ChangeType = LeaseAgreementChangeType.Initial,
            TermType = LeaseAgreementTermType.FixedTerm,
            TermStartOn = new DateOnly(2027, 01, 01),
            TermEndOn = new DateOnly(2027, 12, 31),
            GoverningFromOn = new DateOnly(2027, 01, 01),
            BaseRentAmount = 1_000m,
            RentDueDay = 1,
            SecurityDepositObligation = amount,
            LateFeeAmount = 25m,
            GracePeriodDays = 5,
            Currency = "USD",
            TermsSchemaVersion = 1,
            TermsPayload = "{}",
            DocumentSourceVersion = source,
            CreatedAtUtc = seededAt,
            UpdatedAtUtc = seededAt,
            CreatedByUserId = _scope.UserId,
        };
        var depositAccount = new SecurityDepositAccount
        {
            PortfolioId = PortfolioId,
            TenantAccount = account,
            OriginatingAgreement = agreement,
            Currency = "USD",
            CreatedAtUtc = seededAt,
            CreatedByUserId = _scope.UserId,
        };
        var charge = new TenantLedgerEntry
        {
            PortfolioId = PortfolioId,
            TenantAccount = account,
            EntryType = TenantLedgerEntryType.DepositCharge,
            Direction = TenantLedgerDirection.Debit,
            Amount = amount,
            Currency = "USD",
            EffectiveOn = new DateOnly(2027, 01, 05),
            DueOn = new DateOnly(2027, 01, 05),
            PostedAtUtc = seededAt,
            Description = "Security deposit due",
            BusinessKey = $"deposit-charge-{suffix}",
            CreatedByUserId = _scope.UserId,
        };
        _ctx.Db.AddRange(property, unit, tenant, relationship, account, source, agreement,
            depositAccount, charge);
        _ctx.Db.SaveChanges();
        _ctx.Db.ChangeTracker.Clear();
        return new TenantDepositGraph(account.Id, depositAccount.Id, charge.Id, agreement.Id);
    }

    private ScanDraft SeedPaymentScanDraft(int tenantAccountId, long tenantLedgerEntryId)
    {
        var extractedFields = "{\"document_type\":{\"value\":\"Payment\"}}";
        var draft = new ScanDraft
        {
            PortfolioId = PortfolioId,
            TargetEntityType = "Payment",
            Status = "Reviewing",
            ExtractedFields = extractedFields,
            CreatedAt = SimulatedEntryAtUtc.AddMinutes(-10),
            CaptureAccessContextId = _scope.AccessContextId,
            CaptureAccessRevision = _scope.AccessRevision,
            CaptureTenantAccountId = tenantAccountId,
            CaptureTenantLedgerEntryId = tenantLedgerEntryId,
        };
        _ctx.Db.ScanDrafts.Add(draft);
        _ctx.Db.SaveChanges();
        _ctx.Db.ChangeTracker.Clear();
        return draft;
    }

    private RecordTenantReceiptCommand ReceiptCommand(
        int accountId,
        long targetChargeEntryId,
        decimal amount,
        string suffix,
        DateTime recordedAtUtc,
        string? externalReference = null,
        string? checkNumber = null,
        string? bankName = null) => new(
            PortfolioId,
            accountId,
            amount,
            new DateOnly(2027, 01, 05),
            "January 2027 rent receipt",
            "Bank transfer",
            externalReference ?? $"PMT-{suffix}",
            "Receipt Tenant",
            checkNumber,
            bankName,
            null,
            targetChargeEntryId,
            _scope.UserId,
            _scope.SessionId,
            _scope.AccessContextId,
            _scope.AccessRevision,
            CapabilityKeys.MoneyPaymentsManage,
            $"receipt-{suffix}",
            $"tenant-receipt:{PortfolioId}:{accountId}:{suffix}",
            recordedAtUtc);

    private FundSecurityDepositCommand FundDepositCommand(
        TenantDepositGraph graph,
        decimal amount,
        string suffix) => new(
            PortfolioId,
            graph.AccountId,
            graph.DepositAccountId,
            amount,
            new DateOnly(2027, 01, 05),
            "Security deposit received",
            "Money order",
            $"DEP-{suffix}",
            null,
            _scope.UserId,
            _scope.SessionId,
            _scope.AccessContextId,
            _scope.AccessRevision,
            CapabilityKeys.MoneyDepositsManage,
            $"deposit-fund-{suffix}",
            $"tenant-deposit:{PortfolioId}:{graph.AccountId}:{suffix}");

    private ConfirmScanDraftCommand ScanCommand(
        ScanDraft draft,
        int accountId,
        decimal amount,
        DateTime confirmedAtUtc,
        string checkNumber = "1001")
    {
        var fingerprint = ScanConfirmationDraftFingerprint.Create(
            draft.TargetEntityType,
            draft.SourceStoredFileId,
            draft.ExtractedFields,
            draft.SourceContentSha256,
            draft.CaptureAccessContextId,
            draft.CaptureAccessRevision,
            draft.CapturePropertyId,
            draft.CaptureUnitId,
            draft.CaptureLeaseManagementId,
            draft.CaptureLeaseAgreementId,
            draft.CaptureTenantAccountId,
            draft.CaptureTenantLedgerEntryId,
            draft.CaptureWorkOrderId,
            draft.CaptureApplicationId,
            draft.CaptureRentalListingId,
            draft.SourceLabel);
        return new ConfirmScanDraftCommand(
            PortfolioId,
            draft.Id,
            _scope.UserId,
            confirmedAtUtc,
            fingerprint,
            new ScanConfirmationTargetData(
                ScanConfirmationTargetKind.Payment,
                Payment: new ScanPaymentTargetData(
                    Receipt(amount, confirmedAtUtc, checkNumber),
                    accountId,
                    draft.CaptureTenantLedgerEntryId)),
            draft.SourceStoredFileId,
            _scope.SessionId,
            _scope.AccessContextId,
            _scope.AccessRevision,
            $"scan-confirm:{PortfolioId}:{draft.Id}:scan-sim-clock",
            draft.SourceContentSha256,
            draft.SourceLabel,
            new ScanCaptureContextData(
                null,
                draft.CaptureAccessContextId,
                draft.CaptureAccessRevision,
                draft.CapturePropertyId,
                draft.CaptureUnitId,
                draft.CaptureLeaseManagementId,
                draft.CaptureLeaseAgreementId,
                draft.CaptureTenantAccountId,
                draft.CaptureTenantLedgerEntryId,
                draft.CaptureWorkOrderId,
                draft.CaptureApplicationId,
                draft.CaptureRentalListingId,
                draft.SourceLabel));
    }

    private static ScanReceiptData Receipt(
        decimal amount,
        DateTime transactionAtUtc,
        string checkNumber = "1001") => new(
        VendorName: null,
        VendorAddress: null,
        VendorPhone: null,
        VendorWebsite: null,
        VendorTaxId: null,
        ReceiptNumber: null,
        TransactionDate: transactionAtUtc,
        Subtotal: amount,
        Tax: null,
        TaxRate: null,
        Tip: null,
        Discount: null,
        Shipping: null,
        Total: amount,
        PaymentMethod: "Scanned check",
        CardLast4: null,
        Category: null,
        DocumentKind: "Payment",
        Notes: "Scanned January rent check",
        DueDate: null,
        LineItems: [],
        PayerName: "Receipt Tenant",
        CheckNumber: checkNumber,
        BankName: "Test Bank",
        ExtraFields: []);

    private sealed class FixedTimeProvider(DateTime utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(utcNow);
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

    private sealed class NotificationFailureInterceptor : DbCommandInterceptor
    {
        public bool FailNotifications { get; set; }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            if (FailNotifications
                && command.CommandText.Contains("INSERT INTO \"Notifications\"", StringComparison.Ordinal))
            {
                throw new InvalidOperationException("injected tenant receipt notification failure");
            }

            return ValueTask.FromResult(result);
        }

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            if (FailNotifications
                && command.CommandText.Contains("INSERT INTO \"Notifications\"", StringComparison.Ordinal))
            {
                throw new InvalidOperationException("injected tenant receipt notification failure");
            }

            return ValueTask.FromResult(result);
        }
    }

    private sealed record TenantAccountGraph(
        int AccountId,
        long ChargeEntryId,
        int LeaseManagementId = 0,
        int PrimaryPartyId = 0);

    private sealed record TenantPortalAccessGraph(
        int UserId,
        int AccessContextId,
        long AccessRevision,
        string Email,
        string? PhoneNumber,
        string? PushToken);

    private sealed record TenantDepositGraph(
        int AccountId,
        int DepositAccountId,
        long DepositChargeEntryId,
        int LeaseAgreementId);
}
