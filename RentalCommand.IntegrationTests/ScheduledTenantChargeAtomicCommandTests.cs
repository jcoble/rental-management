using System.Collections.Concurrent;
using System.Data.Common;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using RentalCommand.Core.Atomic;
using RentalCommand.Core.Automation;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Interfaces;
using RentalCommand.Core.Navigation;
using RentalCommand.Core.Outbox;
using RentalCommand.Core.Time;
using RentalCommand.Data;
using RentalCommand.Data.Atomic;
using RentalCommand.Data.Authorization;
using RentalCommand.Data.Payments;
using Testcontainers.PostgreSql;
using Xunit;

namespace RentalCommand.IntegrationTests;

/// <summary>
/// PostgreSQL proof for the destructive scheduled-rent and late-fee cutover. Candidate generation,
/// proration, duplicate suppression, ordering, paging, and posting all execute in the set-based SQL
/// owned by <see cref="IAtomicTenantMoneyPersistence"/>.
/// </summary>
public sealed class ScheduledTenantChargeAtomicCommandTests : IAsyncLifetime
{
    private static readonly AtomicJsonResultCodec<ApplyScheduledTenantChargeBatchResult> Codec =
        new("scheduled-tenant-charges.apply.v1");
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
        services.AddAtomicCommandHandler<
            ApplyScheduledTenantChargeBatchCommand,
            ApplyScheduledTenantChargeBatchResult,
            ApplyScheduledTenantChargeBatchHandler>();
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

        var executed = await Atomic.ExecuteAsync(identity, command, Codec);
        var replayed = await Atomic.ExecuteAsync(identity, command, Codec);
        var laterSweep = await Atomic.ExecuteAsync(
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
            Atomic.ExecuteAsync(
                Identity("scheduled-tenant-charges.rent.apply", "rent-race-a"),
                raceCommandA,
                Codec),
            Atomic.ExecuteAsync(
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
    public async Task RentBatch_RespectsBackfillCurrentAndCustomAccountCutoffs()
    {
        SkipIfNoDocker();
        var leaseStart = new DateOnly(2026, 5, 10);
        var backfill = await SeedScenarioAsync("rent-backfill", FrozenNow, leaseStart);
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

        var result = await Atomic.ExecuteAsync(
            Identity("scheduled-tenant-charges.rent.apply", "rent-cutoffs"),
            RentCommand("rent-cutoffs"),
            Codec);

        result.Value.RentChargeCount.Should().Be(6);
        await using var verify = NewContext();
        var rows = await verify.TenantLedgerEntries
            .Where(row =>
                row.TenantAccountId == backfill.AccountId
                || row.TenantAccountId == current.AccountId
                || row.TenantAccountId == custom.AccountId)
            .OrderBy(row => row.TenantAccountId)
            .ThenBy(row => row.EffectiveOn)
            .ToListAsync();

        rows.Where(row => row.TenantAccountId == backfill.AccountId)
            .Select(row => (row.EffectiveOn, row.Amount))
            .Should().Equal(
                (new DateOnly(2026, 5, 10), 2200m),
                (new DateOnly(2026, 6, 1), 3100m),
                (new DateOnly(2026, 7, 1), 3100m));
        rows.Where(row => row.TenantAccountId == current.AccountId)
            .Select(row => (row.EffectiveOn, row.Amount))
            .Should().Equal((new DateOnly(2026, 7, 15), 1700m));
        rows.Where(row => row.TenantAccountId == custom.AccountId)
            .Select(row => (row.EffectiveOn, row.Amount))
            .Should().Equal(
                (new DateOnly(2026, 6, 20), 1136.67m),
                (new DateOnly(2026, 7, 1), 3100m));
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

        var result = await Atomic.ExecuteAsync(identity, command, Codec);

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
    public async Task LateFeeBatch_UsesOpenChargeBusinessDateGraceAndStateCap_AndDoesNotMutateRent()
    {
        SkipIfNoDocker();
        var scenario = await SeedScenarioAsync("late-fee", FrozenNow);
        await Atomic.ExecuteAsync(
            Identity("scheduled-tenant-charges.rent.apply", "late-fee-rent"),
            RentCommand("late-fee-rent"),
            Codec);
        await FreezeAtAsync(new DateTime(2026, 7, 20, 12, 0, 0, DateTimeKind.Utc));

        var caps = "[{\"State\":\"OH\",\"MaxFlat\":500,\"MaxPercentOfRent\":5}]";
        var command = new ApplyScheduledTenantChargeBatchCommand(
            Guid.NewGuid(),
            new DateTime(2026, 7, 20, 12, 0, 0, DateTimeKind.Utc),
            200,
            false,
            true,
            caps);
        var first = await Atomic.ExecuteAsync(
            Identity("scheduled-tenant-charges.late-fee.apply", "late-fee-first"),
            command,
            Codec);
        var duplicate = await Atomic.ExecuteAsync(
            Identity("scheduled-tenant-charges.late-fee.apply", "late-fee-second"),
            command with { RunToken = Guid.NewGuid() },
            Codec);

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
    public async Task RentBatch_CompanionFailureRollsBackLedgerAuditOutboxAndReceipt_ThenRecovers()
    {
        SkipIfNoDocker();
        var scenario = await SeedScenarioAsync("rollback", FrozenNow);
        var identity = Identity("scheduled-tenant-charges.rent.apply", "rollback");
        var command = RentCommand("rollback");
        Failures.FailAtomicAudit = true;

        var failure = await FluentActions
            .Invoking(() => Atomic.ExecuteAsync(identity, command, Codec))
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
        var recovered = await Atomic.ExecuteAsync(identity, command, Codec);
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
            .Invoking(() => Atomic.ExecuteAsync(identity, command, Codec))
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
        var recovered = await Atomic.ExecuteAsync(identity, command, Codec);
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
        DateOnly? termEndOn = null)
    {
        await using var db = NewContext();
        var portfolio = await db.Portfolios.FirstOrDefaultAsync();
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
            db.SimulationClocks.Add(new SimulationClock
            {
                Id = 1,
                Mode = ClockMode.Frozen,
                SimAnchorUtc = frozenAtUtc,
                RealAnchorUtc = frozenAtUtc,
                TimeZoneId = "UTC",
                UpdatedAtRealUtc = frozenAtUtc,
            });
            await db.SaveChangesAsync();
        }
        else
        {
            await FreezeAtAsync(frozenAtUtc);
        }

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
            BaseRentAmount = 3100m,
            RentDueDay = 1,
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
            management.Id,
            party.Id,
            user.Id,
            accessContext.Id,
            accessContext.AccessRevision,
            null);
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

    private async Task FreezeAtAsync(DateTime instant)
    {
        await using var db = NewContext();
        var clock = await db.SimulationClocks.SingleAsync(row => row.Id == 1);
        clock.Mode = ClockMode.Frozen;
        clock.SimAnchorUtc = instant;
        clock.RealAnchorUtc = instant;
        clock.TimeZoneId = "UTC";
        clock.UpdatedAtRealUtc = instant;
        await db.SaveChangesAsync();
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

    private static ApplyScheduledTenantChargeBatchCommand RentCommand(
        string _,
        DateTime? businessNowUtc = null) => new(
        Guid.NewGuid(), businessNowUtc ?? FrozenNow, 200, true, false, "[]");

    private static AtomicCommandIdentity Identity(string type, string suffix) => new(type, suffix);

    private static string DestinationHash(string destination) =>
        Convert.ToHexString(
                System.Security.Cryptography.SHA256.HashData(
                    System.Text.Encoding.UTF8.GetBytes(destination.Trim().ToLowerInvariant())))
            .ToLowerInvariant();

    private IAtomicUnitOfWork Atomic => _services!.GetRequiredService<IAtomicUnitOfWork>();
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

    private sealed record Scenario(
        int PortfolioId,
        int AccountId,
        Guid AgreementPublicId,
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
