using System.Data.Common;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using RentalCommand.Core.Atomic;
using RentalCommand.Core.Authorization;
using RentalCommand.Core.Automation;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Payments;
using RentalCommand.Data.Security;
using RentalCommand.Data.Accounting;
using RentalCommand.Core.Time;
using RentalCommand.TestCommon;

namespace RentalCommand.Api.Tests.Domain;

[Collection(MigratedPostgreSqlCollection.Name)]
public sealed class ScheduledTenantChargePostgreSqlTests : IAsyncLifetime
{
    private const int PortfolioId = 1;
    private const string EnginePassword = "scheduled-rent-engine-role-test-password";
    private static readonly DateTime SeededAtUtc =
        new(2027, 01, 08, 14, 30, 00, DateTimeKind.Utc);
    private static readonly AtomicJsonResultCodec<ApplyScheduledRentChargeBatchResult> Codec =
        new("scheduled-tenant-charges.rent.apply.v1");
    private static readonly AtomicJsonResultCodec<ApplyScheduledLateFeeChargeBatchResult> LateFeeCodec =
        new("scheduled-tenant-charges.late-fee.apply.v1");

    private readonly MigratedPostgreSqlFixture _fixture;
    private MigratedPostgreSqlTestContext _ctx = null!;
    private ServiceProvider _services = null!;
    private IAtomicUnitOfWork _atomic = null!;

    public ScheduledTenantChargePostgreSqlTests(MigratedPostgreSqlFixture fixture)
    {
        _fixture = fixture;
    }

    public async Task InitializeAsync()
    {
        _ctx = await _fixture.CreateContextAsync();
        await new ChartOfAccountsSeedService(_ctx.Db).SeedAsync(PortfolioId);
        await _ctx.Db.SaveChangesAsync();
        _services = AtomicDomainTestKernel.CreateForScheduledTenantChargesPostgreSql(
            _ctx.ConnectionString);
        _atomic = _services.GetRequiredService<IAtomicUnitOfWork>();
    }

    public async Task DisposeAsync()
    {
        await _services.DisposeAsync();
        await _ctx.DisposeAsync();
    }

    [Fact]
    public async Task RentBatch_DoesNotPostSecondJanuaryRentForCorrectionAfterInitialAgreementWasCharged()
    {
        var graph = SeedCorrectedAgreementWithExistingJanuaryRent();

        var result = await _atomic.ExecuteAsync(
            new AtomicCommandIdentity("scheduled-tenant-charges.rent.apply", Guid.NewGuid().ToString("N")),
            new ApplyScheduledRentChargeBatchCommand(
                Guid.NewGuid(),
                SeededAtUtc,
                200),
            Codec);

        result.Value.RentChargeCount.Should().Be(0);
        _ctx.Db.ChangeTracker.Clear();
        var rows = await _ctx.Db.TenantLedgerEntries
            .AsNoTracking()
            .Where(entry => entry.TenantAccountId == graph.TenantAccountId
                && entry.EntryType == TenantLedgerEntryType.RentCharge)
            .OrderBy(entry => entry.Id)
            .Select(entry => new
            {
                entry.Amount,
                entry.DueOn,
                entry.LeaseAgreementId,
                entry.BusinessKey,
            })
            .ToListAsync();

        rows.Should().ContainSingle();
        rows[0].Amount.Should().Be(1_300m);
        rows[0].DueOn.Should().Be(new DateOnly(2027, 01, 01));
        rows[0].LeaseAgreementId.Should().Be(graph.InitialAgreementId);
        rows[0].BusinessKey.Should().Be($"rent:{graph.InitialAgreementPublicId}:2027-01");
    }

    [Fact]
    public async Task RentBatch_EngineRole_PostsAndReplaysWithRestrictedRuntimeGrants()
    {
        await AssertRuntimeTenantMoneyGrantMigrationAppliedAsync(DatabaseRuntimeIdentity.EngineRole);
        await _ctx.Db.Database.ExecuteSqlRawAsync(
            $"ALTER ROLE {DatabaseRuntimeIdentity.EngineRole} PASSWORD '{EnginePassword}';");
        var graph = SeedInitialAgreementReadyForJanuaryRent();
        var role = new RestrictedRoleConnectionInterceptor(DatabaseRuntimeIdentity.EngineRole);
        await using var services = AtomicDomainTestKernel.CreateForScheduledTenantChargesPostgreSql(
            RuntimeConnectionString(DatabaseRuntimeIdentity.EngineRole, EnginePassword),
            [role]);
        var atomic = services.GetRequiredService<IAtomicUnitOfWork>();
        var identity = new AtomicCommandIdentity(
            "scheduled-tenant-charges.rent.apply",
            "engine-role-scheduled-rent");
        var command = new ApplyScheduledRentChargeBatchCommand(
            Guid.Parse("3f718e3e-6d72-48f3-84fb-33f392744d45"),
            SeededAtUtc,
            200);

        var first = await atomic.ExecuteAsync(identity, command, Codec);
        var replay = await atomic.ExecuteAsync(identity, command, Codec);

        role.OpenCount.Should().BeGreaterThan(0);
        first.Disposition.Should().Be(AtomicCommandDisposition.Executed);
        first.Value.RentChargeCount.Should().Be(1);
        replay.Disposition.Should().Be(AtomicCommandDisposition.Replayed);
        replay.Value.Should().Be(first.Value);

        _ctx.Db.ChangeTracker.Clear();
        var rows = await _ctx.Db.TenantLedgerEntries
            .AsNoTracking()
            .Where(entry => entry.TenantAccountId == graph.TenantAccountId
                && entry.EntryType == TenantLedgerEntryType.RentCharge)
            .Select(entry => new
            {
                entry.Amount,
                entry.DueOn,
                entry.LeaseAgreementId,
                entry.BusinessKey,
            })
            .ToListAsync();
        rows.Should().ContainSingle();
        rows[0].Amount.Should().Be(1_300m);
        rows[0].DueOn.Should().Be(new DateOnly(2027, 01, 01));
        rows[0].LeaseAgreementId.Should().Be(graph.LeaseAgreementId);
        rows[0].BusinessKey.Should().Be($"rent:{graph.LeaseAgreementPublicId}:2027-01");
    }

    [Fact]
    public async Task RentBatch_BackfillFromLeaseStart_PostsEveryDueMonthThroughCurrent()
    {
        var graph = SeedInitialAgreementReadyForJanuaryRent();
        var marchBusinessDate = new DateTime(2027, 03, 08, 12, 00, 00, DateTimeKind.Utc);
        SetFrozenBusinessDate(marchBusinessDate);

        var result = await _atomic.ExecuteAsync(
            new AtomicCommandIdentity(
                "scheduled-tenant-charges.rent.apply",
                "h4-backfill-from-lease-start"),
            new ApplyScheduledRentChargeBatchCommand(
                Guid.Parse("a2c0d5c8-4ba6-4e59-a348-2e13a7f5f601"),
                marchBusinessDate,
                200),
            Codec);

        result.Value.RentChargeCount.Should().Be(3);
        _ctx.Db.ChangeTracker.Clear();
        var rows = await _ctx.Db.TenantLedgerEntries
            .AsNoTracking()
            .Where(entry => entry.TenantAccountId == graph.TenantAccountId
                && entry.EntryType == TenantLedgerEntryType.RentCharge)
            .OrderBy(entry => entry.DueOn)
            .Select(entry => new { entry.BusinessKey, entry.DueOn })
            .ToListAsync();

        rows.Select(row => row.BusinessKey).Should().Equal(
            $"rent:{graph.LeaseAgreementPublicId}:2027-01",
            $"rent:{graph.LeaseAgreementPublicId}:2027-02",
            $"rent:{graph.LeaseAgreementPublicId}:2027-03");
        rows.Select(row => row.DueOn).Should().Equal(
            new DateOnly(2027, 01, 01),
            new DateOnly(2027, 02, 01),
            new DateOnly(2027, 03, 01));
    }

    [Fact]
    public async Task RentBatch_UsesOneSetBasedSqlSeriesFromTrackingStartToCurrentMonth()
    {
        var graph = SeedInitialAgreementReadyForJanuaryRent();
        var marchBusinessDate = new DateTime(2027, 03, 08, 12, 00, 00, DateTimeKind.Utc);
        SetFrozenBusinessDate(marchBusinessDate);
        var capture = new RentSqlCaptureInterceptor();
        await using var services = AtomicDomainTestKernel.CreateForScheduledTenantChargesPostgreSql(
            _ctx.ConnectionString,
            [capture]);
        var atomic = services.GetRequiredService<IAtomicUnitOfWork>();

        var result = await atomic.ExecuteAsync(
            new AtomicCommandIdentity(
                "scheduled-tenant-charges.rent.apply",
                "h4-generated-sql"),
            new ApplyScheduledRentChargeBatchCommand(
                Guid.Parse("c4bb4ba8-03d1-4523-bd5d-0eabf0d96110"),
                marchBusinessDate,
                200),
            Codec);

        result.Value.RentChargeCount.Should().Be(3);
        var sql = capture.Commands.Single(command => command.Contains("generate_series", StringComparison.Ordinal));
        sql.Should().Contain("INSERT INTO \"TenantLedgerEntries\"");
        sql.Should().Contain("ON CONFLICT (\"TenantAccountId\", \"BusinessKey\") DO NOTHING");
        sql.Should().Contain("account.\"RentTrackingStartOn\"");
        sql.Should().Contain("date_trunc('month', effective_date.business_date::timestamp)");
        sql.Should().NotContain("effective_date.business_date::timestamp)::date");
        var seriesStart = sql.IndexOf("CROSS JOIN LATERAL generate_series", StringComparison.Ordinal);
        Console.WriteLine($"H4_RENT_SQL={sql[seriesStart..Math.Min(sql.Length, seriesStart + 900)]}");

        _ctx.Db.ChangeTracker.Clear();
        (await _ctx.Db.TenantLedgerEntries.CountAsync(entry =>
            entry.TenantAccountId == graph.TenantAccountId
            && entry.EntryType == TenantLedgerEntryType.RentCharge)).Should().Be(3);
    }

    [Fact]
    public async Task RentBatch_ForwardOnlyRetroactiveLease_DoesNotBackfillBeforeTrackingStart()
    {
        var graph = SeedInitialAgreementReadyForJanuaryRent();
        var marchBusinessDate = new DateTime(2027, 03, 08, 12, 00, 00, DateTimeKind.Utc);
        await SetRentTrackingStartOnAsync(graph.TenantAccountId, DateOnly.FromDateTime(marchBusinessDate));
        SetFrozenBusinessDate(marchBusinessDate);

        var result = await _atomic.ExecuteAsync(
            new AtomicCommandIdentity(
                "scheduled-tenant-charges.rent.apply",
                "h4-forward-only-retroactive"),
            new ApplyScheduledRentChargeBatchCommand(
                Guid.Parse("bd9c58ae-66cd-4f79-8e1b-25d2cdb3fd02"),
                marchBusinessDate,
                200),
            Codec);

        result.Value.RentChargeCount.Should().Be(1);
        _ctx.Db.ChangeTracker.Clear();
        var rows = await _ctx.Db.TenantLedgerEntries
            .AsNoTracking()
            .Where(entry => entry.TenantAccountId == graph.TenantAccountId
                && entry.EntryType == TenantLedgerEntryType.RentCharge)
            .Select(entry => new { entry.BusinessKey, entry.DueOn })
            .ToListAsync();

        rows.Should().ContainSingle();
        rows[0].BusinessKey.Should().Be($"rent:{graph.LeaseAgreementPublicId}:2027-03");
        rows[0].DueOn.Should().Be(new DateOnly(2027, 03, 08));
    }

    [Fact]
    public async Task RentBatch_CustomCutoffDateBackdated_PostsFromCutoffThroughCurrent()
    {
        var graph = SeedInitialAgreementReadyForJanuaryRent();
        var marchBusinessDate = new DateTime(2027, 03, 08, 12, 00, 00, DateTimeKind.Utc);
        await SetRentTrackingStartOnAsync(graph.TenantAccountId, new DateOnly(2027, 02, 01));
        SetFrozenBusinessDate(marchBusinessDate);

        var result = await _atomic.ExecuteAsync(
            new AtomicCommandIdentity(
                "scheduled-tenant-charges.rent.apply",
                "h4-custom-cutoff"),
            new ApplyScheduledRentChargeBatchCommand(
                Guid.Parse("dbd7b9c0-dc02-40df-98f1-67982d9ec703"),
                marchBusinessDate,
                200),
            Codec);

        result.Value.RentChargeCount.Should().Be(2);
        _ctx.Db.ChangeTracker.Clear();
        var rows = await _ctx.Db.TenantLedgerEntries
            .AsNoTracking()
            .Where(entry => entry.TenantAccountId == graph.TenantAccountId
                && entry.EntryType == TenantLedgerEntryType.RentCharge)
            .OrderBy(entry => entry.DueOn)
            .Select(entry => new { entry.BusinessKey, entry.DueOn })
            .ToListAsync();

        rows.Select(row => row.BusinessKey).Should().Equal(
            $"rent:{graph.LeaseAgreementPublicId}:2027-02",
            $"rent:{graph.LeaseAgreementPublicId}:2027-03");
        rows.Select(row => row.DueOn).Should().Equal(
            new DateOnly(2027, 02, 01),
            new DateOnly(2027, 03, 01));
    }

    [Fact]
    public async Task RentBatch_AfterOutageAcrossMonthBoundary_PostsAllMissedMonths()
    {
        var graph = SeedInitialAgreementReadyForJanuaryRent();
        var januaryBusinessDate = new DateTime(2027, 01, 08, 12, 00, 00, DateTimeKind.Utc);
        await SetRentTrackingStartOnAsync(graph.TenantAccountId, new DateOnly(2027, 01, 08));
        SetFrozenBusinessDate(januaryBusinessDate);

        var first = await _atomic.ExecuteAsync(
            new AtomicCommandIdentity(
                "scheduled-tenant-charges.rent.apply",
                "h4-outage-before-boundary"),
            new ApplyScheduledRentChargeBatchCommand(
                Guid.Parse("3d9a4c2d-c3f4-4dd1-b684-7edc53a05704"),
                januaryBusinessDate,
                200),
            Codec);

        first.Value.RentChargeCount.Should().Be(1);
        var marchBusinessDate = new DateTime(2027, 03, 08, 12, 00, 00, DateTimeKind.Utc);
        SetFrozenBusinessDate(marchBusinessDate);
        var recovery = await _atomic.ExecuteAsync(
            new AtomicCommandIdentity(
                "scheduled-tenant-charges.rent.apply",
                "h4-outage-after-boundary"),
            new ApplyScheduledRentChargeBatchCommand(
                Guid.Parse("e2b70213-8d4a-4c4f-957b-8fac44a3f605"),
                marchBusinessDate,
                200),
            Codec);

        recovery.Value.RentChargeCount.Should().Be(2);
        _ctx.Db.ChangeTracker.Clear();
        var rows = await _ctx.Db.TenantLedgerEntries
            .AsNoTracking()
            .Where(entry => entry.TenantAccountId == graph.TenantAccountId
                && entry.EntryType == TenantLedgerEntryType.RentCharge)
            .OrderBy(entry => entry.DueOn)
            .Select(entry => entry.BusinessKey)
            .ToListAsync();

        rows.Should().Equal(
            $"rent:{graph.LeaseAgreementPublicId}:2027-01",
            $"rent:{graph.LeaseAgreementPublicId}:2027-02",
            $"rent:{graph.LeaseAgreementPublicId}:2027-03");
    }

    [Fact]
    public async Task RentBatch_RerunUsesPerMonthBusinessKeyAndCreatesNoDuplicates()
    {
        var graph = SeedInitialAgreementReadyForJanuaryRent();
        var marchBusinessDate = new DateTime(2027, 03, 08, 12, 00, 00, DateTimeKind.Utc);
        SetFrozenBusinessDate(marchBusinessDate);
        var command = new ApplyScheduledRentChargeBatchCommand(
            Guid.Parse("e3b8a631-7ee0-44ea-a76d-190fb8c8a606"),
            marchBusinessDate,
            200);

        var first = await _atomic.ExecuteAsync(
            new AtomicCommandIdentity(
                "scheduled-tenant-charges.rent.apply",
                "h4-rerun-first"),
            command,
            Codec);
        var rerun = await _atomic.ExecuteAsync(
            new AtomicCommandIdentity(
                "scheduled-tenant-charges.rent.apply",
                "h4-rerun-second"),
            command with { RunToken = Guid.Parse("f3e0bc0e-a7b0-4ee9-9f12-91d684a4d607") },
            Codec);

        first.Value.RentChargeCount.Should().Be(3);
        rerun.Value.RentChargeCount.Should().Be(0);
        (await _ctx.Db.TenantLedgerEntries.CountAsync(entry =>
            entry.TenantAccountId == graph.TenantAccountId
            && entry.EntryType == TenantLedgerEntryType.RentCharge)).Should().Be(3);
        (await _ctx.Db.TenantLedgerEntries
            .Where(entry => entry.TenantAccountId == graph.TenantAccountId
                && entry.EntryType == TenantLedgerEntryType.RentCharge)
            .GroupBy(entry => entry.BusinessKey)
            .Select(group => group.Count())
            .ToListAsync()).Should().OnlyContain(count => count == 1);
    }

    [Fact]
    public async Task LateFeeBatch_BackfilledRentWaitsForGraceFromPostingTime()
    {
        var graph = SeedInitialAgreementReadyForJanuaryRent();
        var marchBusinessDate = new DateTime(2027, 03, 08, 12, 00, 00, DateTimeKind.Utc);
        SetFrozenBusinessDate(marchBusinessDate);
        _ctx.Db.TenantLedgerEntries.AddRange(
            BackfilledRent(new DateOnly(2027, 01, 01), graph, marchBusinessDate),
            BackfilledRent(new DateOnly(2027, 02, 01), graph, marchBusinessDate),
            BackfilledRent(new DateOnly(2027, 03, 01), graph, marchBusinessDate));
        await _ctx.Db.SaveChangesAsync();

        var sameDayLateFees = await _atomic.ExecuteAsync(
            new AtomicCommandIdentity(
                "scheduled-tenant-charges.late-fee.apply",
                "h4-late-fee-backfill-same-day"),
            new ApplyScheduledLateFeeChargeBatchCommand(
                Guid.Parse("f91537a3-54d6-4d0b-9f12-43af5e7bd609"),
                marchBusinessDate,
                200,
                StateLateFeeCapsJson: "[]"),
            LateFeeCodec);

        sameDayLateFees.Value.LateFeeChargeCount.Should().Be(0);
        (await _ctx.Db.TenantLedgerEntries.CountAsync(entry =>
            entry.TenantAccountId == graph.TenantAccountId
            && entry.EntryType == TenantLedgerEntryType.LateFeeCharge)).Should().Be(0);

        var afterGrace = new DateTime(2027, 03, 14, 12, 00, 00, DateTimeKind.Utc);
        SetFrozenBusinessDate(afterGrace);
        var afterGraceLateFees = await _atomic.ExecuteAsync(
            new AtomicCommandIdentity(
                "scheduled-tenant-charges.late-fee.apply",
                "h4-late-fee-backfill-after-grace"),
            new ApplyScheduledLateFeeChargeBatchCommand(
                Guid.Parse("e0e6b9de-f270-4d64-94dc-eaa8bb2f760a"),
                afterGrace,
                200,
                StateLateFeeCapsJson: "[]"),
            LateFeeCodec);

        afterGraceLateFees.Value.LateFeeChargeCount.Should().Be(3);

        TenantLedgerEntry BackfilledRent(
            DateOnly dueOn,
            InitialLeaseGraph lease,
            DateTime postedAtUtc) => new()
        {
            PortfolioId = PortfolioId,
            TenantAccountId = lease.TenantAccountId,
            LeaseAgreementId = lease.LeaseAgreementId,
            EntryType = TenantLedgerEntryType.RentCharge,
            Direction = TenantLedgerDirection.Debit,
            Amount = 1_300m,
            Currency = "USD",
            EffectiveOn = dueOn,
            DueOn = dueOn,
            PostedAtUtc = postedAtUtc,
            Description = $"Backfilled rent due {dueOn:MMM d, yyyy}",
            BusinessKey = $"rent:{lease.LeaseAgreementPublicId}:{dueOn:yyyy-MM}",
            CreatedByUserId = 1,
        };
    }

    [Fact]
    public async Task LateFeeBatch_UsesGoverningCorrection_KeepsPeriodKey_AndRollsBackBeforeReplay()
    {
        var graph = SeedCorrectedAgreementWithExistingJanuaryRent();
        var failure = new AuditInsertFailureInterceptor { FailAtomicAudit = true };
        await using var services = AtomicDomainTestKernel.CreateForScheduledTenantChargesPostgreSql(
            _ctx.ConnectionString,
            [failure]);
        var atomic = services.GetRequiredService<IAtomicUnitOfWork>();
        var identity = new AtomicCommandIdentity(
            "scheduled-tenant-charges.late-fee.apply",
            "corrected-january-late-fee");
        var command = new ApplyScheduledLateFeeChargeBatchCommand(
            Guid.Parse("33c1695a-da03-42c9-a6f5-cb6244eb0d45"),
            SeededAtUtc,
            200,
            StateLateFeeCapsJson: "[]");

        await FluentActions.Invoking(() => atomic.ExecuteAsync(identity, command, LateFeeCodec))
            .Should().ThrowAsync<Exception>();

        _ctx.Db.ChangeTracker.Clear();
        (await _ctx.Db.TenantLedgerEntries.CountAsync(entry =>
            entry.TenantAccountId == graph.TenantAccountId
            && entry.EntryType == TenantLedgerEntryType.LateFeeCharge)).Should().Be(0);
        (await _ctx.Db.AtomicCommandReceipts.CountAsync(receipt =>
            receipt.CommandType == identity.CommandType
            && receipt.IdempotencyKey == identity.IdempotencyKey)).Should().Be(0);

        failure.FailAtomicAudit = false;
        var first = await atomic.ExecuteAsync(identity, command, LateFeeCodec);
        var replay = await atomic.ExecuteAsync(identity, command, LateFeeCodec);
        var laterSweep = await atomic.ExecuteAsync(
            new AtomicCommandIdentity(
                "scheduled-tenant-charges.late-fee.apply",
                "corrected-january-late-fee-later-sweep"),
            command with { RunToken = Guid.Parse("16dd515f-fc1e-4467-9c4d-90c203ccbd4b") },
            LateFeeCodec);

        first.Disposition.Should().Be(AtomicCommandDisposition.Executed);
        first.Value.LateFeeChargeCount.Should().Be(1);
        replay.Disposition.Should().Be(AtomicCommandDisposition.Replayed);
        replay.Value.Should().Be(first.Value);
        laterSweep.Value.LateFeeChargeCount.Should().Be(0);

        _ctx.Db.ChangeTracker.Clear();
        var lateFee = await _ctx.Db.TenantLedgerEntries
            .AsNoTracking()
            .SingleAsync(entry =>
                entry.TenantAccountId == graph.TenantAccountId
                && entry.EntryType == TenantLedgerEntryType.LateFeeCharge);
        lateFee.Amount.Should().Be(50m);
        lateFee.LeaseAgreementId.Should().Be(graph.CorrectionAgreementId);
        lateFee.BusinessKey.Should()
            .Be($"late-fee:rent:{graph.InitialAgreementPublicId}:2027-01");
    }

    [Fact]
    public async Task LateFeeBatch_BecomesEligibleExactlyOnDueDatePlusGraceDays_AndRemainsIdempotent()
    {
        var graph = SeedInitialAgreementReadyForJanuaryRent();
        var februaryRent = new TenantLedgerEntry
        {
            PortfolioId = PortfolioId,
            TenantAccountId = graph.TenantAccountId,
            EntryType = TenantLedgerEntryType.RentCharge,
            Direction = TenantLedgerDirection.Debit,
            Amount = 1_300m,
            Currency = "USD",
            EffectiveOn = new DateOnly(2027, 02, 01),
            DueOn = new DateOnly(2027, 02, 01),
            PostedAtUtc = new DateTime(2027, 02, 01, 09, 15, 00, DateTimeKind.Utc),
            Description = "Rent due Feb 1, 2027",
            BusinessKey = $"rent:{graph.LeaseAgreementPublicId}:2027-02",
            LeaseAgreementId = graph.LeaseAgreementId,
            CreatedByUserId = 1,
        };
        _ctx.Db.TenantLedgerEntries.Add(februaryRent);
        await _ctx.Db.SaveChangesAsync();

        var caps = "[]";
        SetFrozenBusinessDate(new DateTime(2027, 02, 05, 09, 15, 00, DateTimeKind.Utc));
        var beforeBoundary = await _atomic.ExecuteAsync(
            new AtomicCommandIdentity(
                "scheduled-tenant-charges.late-fee.apply",
                "february-late-fee-before-grace-boundary"),
            new ApplyScheduledLateFeeChargeBatchCommand(
                Guid.Parse("526524dc-afd8-46ae-ad93-c8cd1c696476"),
                new DateTime(2027, 02, 05, 09, 15, 00, DateTimeKind.Utc),
                200,
                caps),
            LateFeeCodec);

        beforeBoundary.Value.LateFeeChargeCount.Should().Be(0);

        var boundaryIdentity = new AtomicCommandIdentity(
            "scheduled-tenant-charges.late-fee.apply",
            "february-late-fee-on-grace-boundary");
        var boundaryCommand = new ApplyScheduledLateFeeChargeBatchCommand(
            Guid.Parse("70ffb98d-9249-4463-9597-1ae55967243a"),
            new DateTime(2027, 02, 06, 09, 15, 00, DateTimeKind.Utc),
            200,
            caps);
        SetFrozenBusinessDate(boundaryCommand.BusinessNowUtc);

        var boundary = await _atomic.ExecuteAsync(
            boundaryIdentity,
            boundaryCommand,
            LateFeeCodec);
        var replay = await _atomic.ExecuteAsync(
            boundaryIdentity,
            boundaryCommand,
            LateFeeCodec);
        var laterSweep = await _atomic.ExecuteAsync(
            new AtomicCommandIdentity(
                "scheduled-tenant-charges.late-fee.apply",
                "february-late-fee-after-grace-boundary"),
            boundaryCommand with
            {
                RunToken = Guid.Parse("fce14d0d-3109-4372-b2f0-048340beab54"),
            },
            LateFeeCodec);

        boundary.Disposition.Should().Be(AtomicCommandDisposition.Executed);
        boundary.Value.LateFeeChargeCount.Should().Be(1);
        replay.Disposition.Should().Be(AtomicCommandDisposition.Replayed);
        replay.Value.Should().Be(boundary.Value);
        laterSweep.Value.LateFeeChargeCount.Should().Be(0);

        _ctx.Db.ChangeTracker.Clear();
        var lateFee = await _ctx.Db.TenantLedgerEntries
            .AsNoTracking()
            .SingleAsync(entry =>
                entry.TenantAccountId == graph.TenantAccountId
                && entry.EntryType == TenantLedgerEntryType.LateFeeCharge);
        lateFee.Amount.Should().Be(75m);
        lateFee.EffectiveOn.Should().Be(new DateOnly(2027, 02, 06));
        lateFee.DueOn.Should().Be(new DateOnly(2027, 02, 06));
        lateFee.BusinessKey.Should().Be($"late-fee:{februaryRent.BusinessKey}");
    }

    [Fact]
    public async Task RentBatch_PostsTwentyOccurrencesWithBoundedJournalPostingReads()
    {
        for (var index = 0; index < 20; index++)
        {
            SeedInitialAgreementReadyForJanuaryRent();
        }

        var counter = new ScheduledJournalPostingReadCounter();
        await using var services = AtomicDomainTestKernel.CreateForScheduledTenantChargesPostgreSql(
            _ctx.ConnectionString,
            [counter]);
        var atomic = services.GetRequiredService<IAtomicUnitOfWork>();
        counter.Reset();

        var result = await atomic.ExecuteAsync(
            new AtomicCommandIdentity(
                "scheduled-tenant-charges.rent.apply",
                "twenty-occurrence-journal-read-bound"),
            new ApplyScheduledRentChargeBatchCommand(
                Guid.Parse("b20da7dc-5023-4dc2-a667-d1d1a82c41bf"),
                SeededAtUtc,
                20),
            Codec);

        result.Value.RentChargeCount.Should().Be(20);
        counter.ReadCount.Should().BeLessThanOrEqualTo(3,
            "system accounts, existing posting identities, and account/currency validation must be prefetched once per batch");
        Console.WriteLine($"scheduled_journal_posting_reads={counter.ReadCount}");
    }

    [Fact]
    public async Task LateFeeRecovery_ReversesAllocatedWrongFee_ReplacesAndReplaysExactResult()
    {
        var scope = _ctx.Db.SeedAdministratorScope(
            PortfolioId,
            nameof(LateFeeRecovery_ReversesAllocatedWrongFee_ReplacesAndReplaysExactResult));
        var graph = SeedCorrectedAgreementWithExistingJanuaryRent();
        var wrongLateFee = new TenantLedgerEntry
        {
            PortfolioId = PortfolioId,
            TenantAccountId = graph.TenantAccountId,
            EntryType = TenantLedgerEntryType.LateFeeCharge,
            Direction = TenantLedgerDirection.Debit,
            Amount = 75m,
            Currency = "USD",
            EffectiveOn = new DateOnly(2027, 01, 08),
            DueOn = new DateOnly(2027, 01, 08),
            PostedAtUtc = SeededAtUtc,
            Description = "Wrong late fee",
            BusinessKey = $"late-fee:rent:{graph.InitialAgreementPublicId}:2027-01",
            LeaseAgreementId = graph.InitialAgreementId,
            CreatedByUserId = scope.UserId,
        };
        var receipt = new TenantLedgerEntry
        {
            PortfolioId = PortfolioId,
            TenantAccountId = graph.TenantAccountId,
            EntryType = TenantLedgerEntryType.PaymentReceipt,
            Direction = TenantLedgerDirection.Credit,
            Amount = 50m,
            Currency = "USD",
            EffectiveOn = new DateOnly(2027, 01, 08),
            PostedAtUtc = SeededAtUtc,
            Description = "Late fee payment",
            BusinessKey = $"manual-receipt:{Guid.NewGuid():N}",
            CreatedByUserId = scope.UserId,
        };
        var alreadyReversedLateFee = new TenantLedgerEntry
        {
            PortfolioId = PortfolioId,
            TenantAccountId = graph.TenantAccountId,
            EntryType = TenantLedgerEntryType.LateFeeCharge,
            Direction = TenantLedgerDirection.Debit,
            Amount = 75m,
            Currency = "USD",
            EffectiveOn = new DateOnly(2027, 01, 08),
            DueOn = new DateOnly(2027, 01, 08),
            PostedAtUtc = SeededAtUtc,
            Description = "Already reversed wrong late fee",
            BusinessKey = $"late-fee:already-reversed:{Guid.NewGuid():N}",
            LeaseAgreementId = graph.InitialAgreementId,
            CreatedByUserId = scope.UserId,
        };
        _ctx.Db.TenantLedgerEntries.AddRange(wrongLateFee, receipt, alreadyReversedLateFee);
        await _ctx.Db.SaveChangesAsync();
        var existingReversal = new TenantLedgerEntry
        {
            PortfolioId = PortfolioId,
            TenantAccountId = graph.TenantAccountId,
            EntryType = TenantLedgerEntryType.Reversal,
            Direction = TenantLedgerDirection.Credit,
            Amount = 75m,
            Currency = "USD",
            EffectiveOn = new DateOnly(2027, 01, 08),
            PostedAtUtc = SeededAtUtc,
            Description = "Earlier reversal",
            BusinessKey = $"late-fee:already-reversed-reversal:{Guid.NewGuid():N}",
            LeaseAgreementId = graph.InitialAgreementId,
            ReversesEntryId = alreadyReversedLateFee.Id,
            CreatedByUserId = scope.UserId,
        };
        _ctx.Db.TenantLedgerEntries.Add(existingReversal);
        _ctx.Db.TenantLedgerAllocations.Add(new TenantLedgerAllocation
        {
            PortfolioId = PortfolioId,
            TenantAccountId = graph.TenantAccountId,
            DebitEntryId = wrongLateFee.Id,
            CreditEntryId = receipt.Id,
            Amount = 50m,
            AllocatedAtUtc = SeededAtUtc,
            BusinessKey = $"{receipt.BusinessKey}:{wrongLateFee.Id}",
            CreatedByUserId = scope.UserId,
        });
        await _ctx.Db.SaveChangesAsync();
        _ctx.Db.ChangeTracker.Clear();

        var identity = new AtomicCommandIdentity(
            "late-fee-charges.recover",
            "late-fee-recovery-test");
        var command = new RecoverLateFeeChargesCommand(
            PortfolioId,
            [
                new RecoverLateFeeChargeRow(
                    graph.TenantAccountId,
                    wrongLateFee.Id,
                    75m,
                    50m,
                    AlreadyReversed: false),
                new RecoverLateFeeChargeRow(
                    graph.TenantAccountId,
                    alreadyReversedLateFee.Id,
                    75m,
                    50m,
                    AlreadyReversed: true),
            ],
            ExpectedReviewedChargeCount: 2,
            ExpectedReversedChargeCount: 1,
            ExpectedReplacementChargeCount: 2,
            ExpectedReversedTotal: 75m,
            ExpectedReplacementTotal: 100m,
            FinancialReference: "FIN-LATE-FEE-TEST",
            ActorUserId: scope.UserId,
            AuthSessionId: scope.SessionId,
            AccessContextId: scope.AccessContextId,
            ExpectedAccessRevision: scope.AccessRevision,
            RequiredCapability: CapabilityKeys.MoneyChargesManage,
            DeliveryIdempotencyKey: identity.IdempotencyKey);
        var codec = new AtomicJsonResultCodec<RecoverLateFeeChargesResult>(
            "late-fee-charges.recover.v1");

        var first = await _atomic.ExecuteAsync(identity, command, codec);
        var replay = await _atomic.ExecuteAsync(identity, command, codec);

        first.Disposition.Should().Be(AtomicCommandDisposition.Executed);
        first.Value.Should().Be(new RecoverLateFeeChargesResult(
            1, 2, 1, 1, 75m, 100m, 50m, 50m, "FIN-LATE-FEE-TEST"));
        replay.Disposition.Should().Be(AtomicCommandDisposition.Replayed);
        replay.Value.Should().Be(first.Value);

        _ctx.Db.ChangeTracker.Clear();
        var entries = await _ctx.Db.TenantLedgerEntries
            .AsNoTracking()
            .Where(entry => entry.TenantAccountId == graph.TenantAccountId
                && (entry.Id == wrongLateFee.Id
                    || entry.Id == alreadyReversedLateFee.Id
                    || entry.Id == existingReversal.Id
                    || entry.ReversesEntryId == wrongLateFee.Id
                    || entry.ReversesEntryId == alreadyReversedLateFee.Id
                    || entry.BusinessKey.StartsWith("late-fee-recovery:FIN-LATE-FEE-TEST:replacement:")))
            .OrderBy(entry => entry.Id)
            .Select(entry => new
            {
                entry.EntryType,
                entry.Direction,
                entry.Amount,
                entry.ReversesEntryId,
                entry.BusinessKey,
            })
            .ToListAsync();
        entries.Should().HaveCount(6);
        entries.Should().Contain(entry => entry.EntryType == TenantLedgerEntryType.Reversal
            && entry.Direction == TenantLedgerDirection.Credit
            && entry.Amount == 75m
            && entry.ReversesEntryId == wrongLateFee.Id);
        entries.Count(entry => entry.EntryType == TenantLedgerEntryType.LateFeeCharge
            && entry.Direction == TenantLedgerDirection.Debit
            && entry.Amount == 50m
            && entry.BusinessKey.StartsWith("late-fee-recovery:FIN-LATE-FEE-TEST:replacement:"))
            .Should().Be(2);

        var allocationSummary = await _ctx.Db.TenantLedgerAllocations
            .AsNoTracking()
            .Where(allocation => allocation.TenantAccountId == graph.TenantAccountId
                && allocation.BusinessKey.StartsWith("late-fee-recovery:FIN-LATE-FEE-TEST:"))
            .GroupBy(_ => 1)
            .Select(group => new
            {
                Count = group.Count(),
                Net = group.Sum(row => row.Amount),
                Reversed = group.Sum(row => row.ReversesAllocationId == null ? 0m : -row.Amount),
                Replacement = group.Sum(row => row.ReversesAllocationId == null ? row.Amount : 0m),
            })
            .SingleAsync();
        allocationSummary.Count.Should().Be(2);
        allocationSummary.Net.Should().Be(0m);
        allocationSummary.Reversed.Should().Be(50m);
        allocationSummary.Replacement.Should().Be(50m);
    }

    [Fact]
    public async Task LateFeeRecovery_TwoAllocatedSameAccountFees_MapOneToOneWithoutCrossProduct()
    {
        var scope = _ctx.Db.SeedAdministratorScope(
            PortfolioId,
            nameof(LateFeeRecovery_TwoAllocatedSameAccountFees_MapOneToOneWithoutCrossProduct));
        var graph = SeedCorrectedAgreementWithExistingJanuaryRent();
        var firstFee = LateFee("cross-product-first", 75m);
        var secondFee = LateFee("cross-product-second", 40m);
        var firstReceipt = Receipt("cross-product-first", 30m);
        var secondReceipt = Receipt("cross-product-second", 20m);
        _ctx.Db.TenantLedgerEntries.AddRange(
            firstFee, secondFee, firstReceipt, secondReceipt);
        await _ctx.Db.SaveChangesAsync();

        _ctx.Db.TenantLedgerAllocations.AddRange(
            Allocation(firstFee.Id, firstReceipt.Id, 30m, "cross-product-first"),
            Allocation(secondFee.Id, secondReceipt.Id, 20m, "cross-product-second"));
        await _ctx.Db.SaveChangesAsync();
        _ctx.Db.ChangeTracker.Clear();

        var identity = new AtomicCommandIdentity(
            "late-fee-charges.recover",
            "late-fee-recovery-cross-product-regression");
        var command = new RecoverLateFeeChargesCommand(
            PortfolioId,
            [
                new RecoverLateFeeChargeRow(
                    graph.TenantAccountId, firstFee.Id, 75m, 50m, AlreadyReversed: false),
                new RecoverLateFeeChargeRow(
                    graph.TenantAccountId, secondFee.Id, 40m, 25m, AlreadyReversed: false),
            ],
            ExpectedReviewedChargeCount: 2,
            ExpectedReversedChargeCount: 2,
            ExpectedReplacementChargeCount: 2,
            ExpectedReversedTotal: 115m,
            ExpectedReplacementTotal: 75m,
            FinancialReference: "FIN-LATE-FEE-CROSS-PRODUCT",
            ActorUserId: scope.UserId,
            AuthSessionId: scope.SessionId,
            AccessContextId: scope.AccessContextId,
            ExpectedAccessRevision: scope.AccessRevision,
            RequiredCapability: CapabilityKeys.MoneyChargesManage,
            DeliveryIdempotencyKey: identity.IdempotencyKey);
        var codec = new AtomicJsonResultCodec<RecoverLateFeeChargesResult>(
            "late-fee-charges.recover.v1");

        var first = await _atomic.ExecuteAsync(identity, command, codec);
        var replay = await _atomic.ExecuteAsync(identity, command, codec);

        first.Value.ReplacementAllocationCount.Should().Be(2);
        first.Value.ReplacementAllocationTotal.Should().Be(50m);
        replay.Disposition.Should().Be(AtomicCommandDisposition.Replayed);
        replay.Value.Should().Be(first.Value);

        var replacementMappings = await (
                from allocation in _ctx.Db.TenantLedgerAllocations.AsNoTracking()
                join replacement in _ctx.Db.TenantLedgerEntries.AsNoTracking()
                    on new
                    {
                        Id = allocation.DebitEntryId,
                        allocation.PortfolioId,
                        allocation.TenantAccountId,
                    }
                    equals new
                    {
                        Id = replacement.Id,
                        replacement.PortfolioId,
                        replacement.TenantAccountId,
                    }
                where allocation.PortfolioId == PortfolioId
                    && allocation.TenantAccountId == graph.TenantAccountId
                    && allocation.BusinessKey.StartsWith(
                        "late-fee-recovery:FIN-LATE-FEE-CROSS-PRODUCT:replacement-allocation:")
                orderby allocation.Id
                select new
                {
                    allocation.CreditEntryId,
                    allocation.Amount,
                    replacement.BusinessKey,
                })
            .ToListAsync();

        replacementMappings.Should().HaveCount(2, "two sources must produce two, not four, allocations");
        replacementMappings.Should().ContainSingle(row =>
            row.CreditEntryId == firstReceipt.Id
            && row.Amount == 30m
            && row.BusinessKey.EndsWith($":{firstFee.Id}"));
        replacementMappings.Should().ContainSingle(row =>
            row.CreditEntryId == secondReceipt.Id
            && row.Amount == 20m
            && row.BusinessKey.EndsWith($":{secondFee.Id}"));

        TenantLedgerEntry LateFee(string suffix, decimal amount) => new()
        {
            PortfolioId = PortfolioId,
            TenantAccountId = graph.TenantAccountId,
            EntryType = TenantLedgerEntryType.LateFeeCharge,
            Direction = TenantLedgerDirection.Debit,
            Amount = amount,
            Currency = "USD",
            EffectiveOn = new DateOnly(2027, 01, 08),
            DueOn = new DateOnly(2027, 01, 08),
            PostedAtUtc = SeededAtUtc,
            Description = suffix,
            BusinessKey = $"late-fee:{suffix}:{Guid.NewGuid():N}",
            LeaseAgreementId = graph.InitialAgreementId,
            CreatedByUserId = scope.UserId,
        };

        TenantLedgerEntry Receipt(string suffix, decimal amount) => new()
        {
            PortfolioId = PortfolioId,
            TenantAccountId = graph.TenantAccountId,
            EntryType = TenantLedgerEntryType.PaymentReceipt,
            Direction = TenantLedgerDirection.Credit,
            Amount = amount,
            Currency = "USD",
            EffectiveOn = new DateOnly(2027, 01, 08),
            PostedAtUtc = SeededAtUtc,
            Description = suffix,
            BusinessKey = $"manual-receipt:{suffix}:{Guid.NewGuid():N}",
            CreatedByUserId = scope.UserId,
        };

        TenantLedgerAllocation Allocation(
            long debitEntryId,
            long creditEntryId,
            decimal amount,
            string suffix) => new()
        {
            PortfolioId = PortfolioId,
            TenantAccountId = graph.TenantAccountId,
            DebitEntryId = debitEntryId,
            CreditEntryId = creditEntryId,
            Amount = amount,
            AllocatedAtUtc = SeededAtUtc,
            BusinessKey = $"allocation:{suffix}:{Guid.NewGuid():N}",
            CreatedByUserId = scope.UserId,
        };
    }

    private InitialLeaseGraph SeedInitialAgreementReadyForJanuaryRent()
    {
        EnsureFrozenBusinessDate();
        EnsureAutomationSettings();

        var source = new LegalDocumentSourceVersion
        {
            PublicId = Guid.NewGuid(),
            PortfolioId = PortfolioId,
            SourceKind = LegalDocumentSourceKind.BuiltInRenderer,
            BusinessKey = $"scheduled-rent-engine-source:{Guid.NewGuid():N}",
            RendererKey = $"test-lease-{Guid.NewGuid():N}",
            RendererVersion = 1,
            SnapshotPayload = "{}",
            CreatedAtUtc = SeededAtUtc,
            CreatedByUserId = 1,
        };
        var property = new Property
        {
            PortfolioId = PortfolioId,
            Name = "Scheduled Rent Engine Property",
            AddressLine1 = "200 Test Street",
            City = "Columbus",
            State = "OH",
            PostalCode = "43215",
            CreatedAt = SeededAtUtc,
            UpdatedAt = SeededAtUtc,
        };
        var unit = new Unit
        {
            PortfolioId = PortfolioId,
            Property = property,
            UnitNumber = "B",
            MarketRent = 1_300m,
            CreatedAt = SeededAtUtc,
            UpdatedAt = SeededAtUtc,
        };
        var relationship = new LeaseManagement
        {
            PortfolioId = PortfolioId,
            Property = property,
            Unit = unit,
            RelationshipNumber = $"LM-ENGINE-{Guid.NewGuid():N}",
            PossessionGivenAtUtc = SeededAtUtc.AddDays(-7),
            CreatedAtUtc = SeededAtUtc.AddDays(-7),
            UpdatedAtUtc = SeededAtUtc,
            CreatedByUserId = 1,
            RowVersion = Guid.NewGuid(),
        };
        var tenant = new Tenant
        {
            PortfolioId = PortfolioId,
            FirstName = "Engine",
            LastName = "Tenant",
            Email = "engine.tenant@example.test",
            CreatedAt = SeededAtUtc,
            UpdatedAt = SeededAtUtc,
        };
        var party = new LeaseManagementParty
        {
            PortfolioId = PortfolioId,
            LeaseManagement = relationship,
            Tenant = tenant,
            Role = LeaseManagementPartyRole.PrimaryTenant,
            EffectiveFrom = new DateOnly(2027, 01, 01),
            ChangeReason = "Test setup",
            CreatedAtUtc = SeededAtUtc,
            CreatedByUserId = 1,
        };
        var account = new TenantAccount
        {
            PortfolioId = PortfolioId,
            LeaseManagement = relationship,
            AccountNumber = $"TA-ENGINE-{Guid.NewGuid():N}",
            Currency = "USD",
            OpenedAtUtc = SeededAtUtc.AddDays(-7),
            CreatedAtUtc = SeededAtUtc.AddDays(-7),
            CreatedByUserId = 1,
        };
        var agreement = NewAgreement(
            relationship,
            source,
            versionNumber: 1,
            changeType: LeaseAgreementChangeType.Initial,
            governingFromOn: new DateOnly(2027, 01, 01),
            termStartOn: new DateOnly(2027, 01, 01),
            agreementNumber: $"AGR-ENGINE-{Guid.NewGuid():N}");

        _ctx.Db.AddRange(source, property, unit, relationship, tenant, party, account, agreement);
        _ctx.Db.SaveChanges();
        AddTenantSigner(agreement, party, tenant);
        ExecuteAgreement(agreement, "engine");
        return new InitialLeaseGraph(account.Id, agreement.Id, agreement.PublicId);
    }

    private async Task AssertRuntimeTenantMoneyGrantMigrationAppliedAsync(string role)
    {
        var applied = await _ctx.Db.Database.SqlQuery<int>($"""
            SELECT CASE WHEN EXISTS (
                SELECT 1
                FROM "__EFMigrationsHistory"
                WHERE "MigrationId" = '20260728235000_GrantRuntimeTenantMoneyDml')
              AND EXISTS (
                SELECT 1
                FROM "__EFMigrationsHistory"
                WHERE "MigrationId" = '20260728235500_GrantRuntimeTenantMoneyRowLockUpdates')
              AND has_table_privilege({role}, '"TenantAccounts"', 'SELECT')
              AND has_table_privilege({role}, '"TenantLedgerEntries"', 'SELECT')
              AND has_table_privilege({role}, '"TenantLedgerEntries"', 'INSERT')
              AND has_table_privilege({role}, '"TenantLedgerEntries"', 'UPDATE')
              AND has_table_privilege({role}, '"TenantLedgerAllocations"', 'SELECT')
              AND has_table_privilege({role}, '"TenantLedgerAllocations"', 'INSERT')
              AND has_sequence_privilege({role}, '"TenantLedgerEntries_Id_seq"', 'USAGE')
              AND has_sequence_privilege({role}, '"TenantLedgerAllocations_Id_seq"', 'USAGE')
            THEN 1 ELSE 0 END AS "Value"
            """).SingleAsync();

        applied.Should().Be(1);
    }

    private CorrectedLeaseGraph SeedCorrectedAgreementWithExistingJanuaryRent()
    {
        EnsureFrozenBusinessDate();
        EnsureAutomationSettings();

        var source = new LegalDocumentSourceVersion
        {
            PublicId = Guid.NewGuid(),
            PortfolioId = PortfolioId,
            SourceKind = LegalDocumentSourceKind.BuiltInRenderer,
            BusinessKey = $"scheduled-rent-test-source:{Guid.NewGuid():N}",
            RendererKey = "test-lease",
            RendererVersion = 1,
            SnapshotPayload = "{}",
            CreatedAtUtc = SeededAtUtc,
            CreatedByUserId = 1,
        };
        var property = new Property
        {
            PortfolioId = PortfolioId,
            Name = "Scheduled Rent Test Property",
            AddressLine1 = "100 Test Street",
            City = "Columbus",
            State = "OH",
            PostalCode = "43215",
            CreatedAt = SeededAtUtc,
            UpdatedAt = SeededAtUtc,
        };
        var unit = new Unit
        {
            PortfolioId = PortfolioId,
            Property = property,
            UnitNumber = "A",
            MarketRent = 1_300m,
            CreatedAt = SeededAtUtc,
            UpdatedAt = SeededAtUtc,
        };
        var relationship = new LeaseManagement
        {
            PortfolioId = PortfolioId,
            Property = property,
            Unit = unit,
            RelationshipNumber = $"LM-SCHEDULED-{Guid.NewGuid():N}",
            PossessionGivenAtUtc = SeededAtUtc.AddDays(-7),
            CreatedAtUtc = SeededAtUtc.AddDays(-7),
            UpdatedAtUtc = SeededAtUtc,
            CreatedByUserId = 1,
            RowVersion = Guid.NewGuid(),
        };
        var tenant = new Tenant
        {
            PortfolioId = PortfolioId,
            FirstName = "Scheduled",
            LastName = "Tenant",
            Email = "scheduled.tenant@example.test",
            CreatedAt = SeededAtUtc,
            UpdatedAt = SeededAtUtc,
        };
        var party = new LeaseManagementParty
        {
            PortfolioId = PortfolioId,
            LeaseManagement = relationship,
            Tenant = tenant,
            Role = LeaseManagementPartyRole.PrimaryTenant,
            EffectiveFrom = new DateOnly(2027, 01, 01),
            ChangeReason = "Test setup",
            CreatedAtUtc = SeededAtUtc,
            CreatedByUserId = 1,
        };
        var account = new TenantAccount
        {
            PortfolioId = PortfolioId,
            LeaseManagement = relationship,
            AccountNumber = $"TA-SCHEDULED-{Guid.NewGuid():N}",
            Currency = "USD",
            OpenedAtUtc = SeededAtUtc.AddDays(-7),
            CreatedAtUtc = SeededAtUtc.AddDays(-7),
            CreatedByUserId = 1,
        };
        var initial = NewAgreement(
            relationship,
            source,
            versionNumber: 1,
            changeType: LeaseAgreementChangeType.Initial,
            governingFromOn: new DateOnly(2027, 01, 01),
            termStartOn: new DateOnly(2027, 01, 01),
            agreementNumber: $"AGR-SCHEDULED-{Guid.NewGuid():N}");

        _ctx.Db.AddRange(source, property, unit, relationship, tenant, party, account, initial);
        _ctx.Db.SaveChanges();

        AddTenantSigner(initial, party, tenant);
        ExecuteAgreement(initial, "initial");

        var correction = NewAgreement(
            relationship,
            source,
            versionNumber: 2,
            changeType: LeaseAgreementChangeType.Correction,
            governingFromOn: new DateOnly(2027, 01, 08),
            termStartOn: new DateOnly(2027, 01, 01),
            agreementNumber: $"{initial.AgreementNumber}-V2");
        correction.ReplacesAgreementId = initial.Id;
        correction.CorrectionReason = "Correct late fee amount in imported lease";
        _ctx.Db.LeaseAgreements.Add(correction);
        _ctx.Db.SaveChanges();

        AddTenantSigner(correction, party, tenant);
        initial.SupersededEffectiveOn = new DateOnly(2027, 01, 08);
        initial.SupersededByAgreementId = correction.Id;
        initial.SupersessionRecordedAtUtc = SeededAtUtc;
        _ctx.Db.SaveChanges();

        ExecuteAgreement(correction, "correction");

        var januaryRent = new TenantLedgerEntry
        {
            PortfolioId = PortfolioId,
            TenantAccount = account,
            EntryType = TenantLedgerEntryType.RentCharge,
            Direction = TenantLedgerDirection.Debit,
            Amount = 1_300m,
            Currency = "USD",
            EffectiveOn = new DateOnly(2027, 01, 01),
            DueOn = new DateOnly(2027, 01, 01),
            PostedAtUtc = SeededAtUtc.AddDays(-7),
            Description = "Rent due Jan 1, 2027",
            BusinessKey = $"rent:{initial.PublicId}:2027-01",
            LeaseAgreement = initial,
            CreatedByUserId = 1,
        };
        _ctx.Db.TenantLedgerEntries.Add(januaryRent);
        _ctx.Db.SaveChanges();
        _ctx.Db.ChangeTracker.Clear();

        return new CorrectedLeaseGraph(
            account.Id,
            initial.Id,
            initial.PublicId,
            correction.Id);
    }

    private static LeaseAgreement NewAgreement(
        LeaseManagement relationship,
        LegalDocumentSourceVersion source,
        int versionNumber,
        LeaseAgreementChangeType changeType,
        DateOnly governingFromOn,
        DateOnly termStartOn,
        string agreementNumber) => new()
    {
        PublicId = Guid.NewGuid(),
        PortfolioId = PortfolioId,
        LeaseManagement = relationship,
        VersionNumber = versionNumber,
        AgreementNumber = agreementNumber,
        ChangeType = changeType,
        TermType = LeaseAgreementTermType.FixedTerm,
        TermStartOn = termStartOn,
        TermEndOn = new DateOnly(2027, 12, 31),
        GoverningFromOn = governingFromOn,
        BaseRentAmount = 1_300m,
        RentDueDay = 1,
        SecurityDepositObligation = 1_300m,
        LateFeeAmount = changeType == LeaseAgreementChangeType.Correction ? 50m : 75m,
        GracePeriodDays = 5,
        Currency = "USD",
        TermsSchemaVersion = 1,
        TermsPayload = "{}",
        DocumentSourceVersion = source,
        CreatedAtUtc = SeededAtUtc,
        UpdatedAtUtc = SeededAtUtc,
        CreatedByUserId = 1,
    };

    private void AddTenantSigner(
        LeaseAgreement agreement,
        LeaseManagementParty party,
        Tenant tenant)
    {
        _ctx.Db.LeaseAgreementSigners.Add(new LeaseAgreementSigner
        {
            PortfolioId = PortfolioId,
            LeaseAgreement = agreement,
            LeaseManagementParty = party,
            Tenant = tenant,
            SignerRole = LeaseLegalSignerRole.PrimaryTenant,
            NameSnapshot = $"{tenant.FirstName} {tenant.LastName}",
            EmailSnapshot = tenant.Email!,
            SigningOrder = 1,
            IsRequired = true,
        });
        _ctx.Db.SaveChanges();
    }

    private void ExecuteAgreement(LeaseAgreement agreement, string suffix)
    {
        var issuedFile = StoredFile($"agreement-{suffix}-{Guid.NewGuid():N}-issued.pdf");
        var executedFile = StoredFile($"agreement-{suffix}-{Guid.NewGuid():N}-executed.pdf");
        _ctx.Db.StoredFiles.AddRange(issuedFile, executedFile);
        _ctx.Db.SaveChanges();

        var issuedArtifact = AgreementArtifact(
            issuedFile,
            LegalDocumentArtifactKind.IssuedAgreement,
            new string('a', 64),
            new string('b', 64));
        var executedArtifact = AgreementArtifact(
            executedFile,
            LegalDocumentArtifactKind.ExecutedAgreement,
            new string('c', 64),
            null);
        _ctx.Db.LegalDocumentArtifacts.AddRange(issuedArtifact, executedArtifact);
        _ctx.Db.SaveChanges();

        agreement.IssuedArtifactId = issuedArtifact.Id;
        agreement.IssuedAtUtc = SeededAtUtc;
        agreement.ExecutedArtifactId = executedArtifact.Id;
        agreement.FullyExecutedAtUtc = SeededAtUtc;
        _ctx.Db.SaveChanges();
    }

    private static StoredFile StoredFile(string fileName) => new()
    {
        PortfolioId = PortfolioId,
        FileName = fileName,
        FilePath = $"test/{fileName}",
        ContentType = "application/pdf",
        FileSize = 1024,
        UploadedAt = SeededAtUtc,
    };

    private static LegalDocumentArtifact AgreementArtifact(
        StoredFile file,
        LegalDocumentArtifactKind kind,
        string contentSha,
        string? issuanceFingerprint) => new()
    {
        PublicId = Guid.NewGuid(),
        PortfolioId = PortfolioId,
        StoredFileId = file.Id,
        ArtifactKind = kind,
        StorageKey = file.FilePath,
        FileName = file.FileName,
        ContentType = file.ContentType,
        ByteLength = file.FileSize,
        ContentSha256 = contentSha,
        LegalIssuanceFingerprint = issuanceFingerprint,
        CreatedAtUtc = SeededAtUtc,
        CreatedByUserId = 1,
    };

    private void EnsureFrozenBusinessDate()
        => SetFrozenBusinessDate(SeededAtUtc);

    private async Task SetRentTrackingStartOnAsync(int tenantAccountId, DateOnly? rentTrackingStartOn)
    {
        _ctx.Db.ChangeTracker.Clear();
        var account = await _ctx.Db.TenantAccounts.SingleAsync(row => row.Id == tenantAccountId);
        account.RentTrackingStartOn = rentTrackingStartOn;
        await _ctx.Db.SaveChangesAsync();
        _ctx.Db.ChangeTracker.Clear();
    }

    private void SetFrozenBusinessDate(DateTime frozenAtUtc)
    {
        var clock = _ctx.Db.SimulationClocks.SingleOrDefault(clock => clock.Id == 1);
        if (clock is null)
        {
            _ctx.Db.SimulationClocks.Add(new SimulationClock
            {
                Id = 1,
                Mode = ClockMode.Frozen,
                SimAnchorUtc = frozenAtUtc,
                RealAnchorUtc = frozenAtUtc,
                TimeZoneId = "UTC",
                UpdatedAtRealUtc = frozenAtUtc,
            });
        }
        else
        {
            clock.Mode = ClockMode.Frozen;
            clock.SimAnchorUtc = frozenAtUtc;
            clock.RealAnchorUtc = frozenAtUtc;
            clock.TimeZoneId = "UTC";
            clock.UpdatedAtRealUtc = frozenAtUtc;
        }

        _ctx.Db.SaveChanges();
    }

    private void EnsureAutomationSettings()
    {
        var existing = _ctx.Db.AutomationSettings
            .SingleOrDefault(settings => settings.PortfolioId == PortfolioId);
        if (existing is not null)
        {
            existing.EnableRentCharges = true;
            existing.EnableLateFees = true;
            existing.UpdatedAtUtc = SeededAtUtc;
            _ctx.Db.SaveChanges();
            return;
        }

        _ctx.Db.AutomationSettings.Add(new AutomationSettings
        {
            PortfolioId = PortfolioId,
            EnableRentCharges = true,
            EnableLateFees = true,
            RentChargeLeadDays = 5,
            CreatedAtUtc = SeededAtUtc,
            UpdatedAtUtc = SeededAtUtc,
        });
        _ctx.Db.SaveChanges();
    }

    private sealed record CorrectedLeaseGraph(
        int TenantAccountId,
        int InitialAgreementId,
        Guid InitialAgreementPublicId,
        int CorrectionAgreementId);

    private sealed record InitialLeaseGraph(
        int TenantAccountId,
        int LeaseAgreementId,
        Guid LeaseAgreementPublicId);

    private string RuntimeConnectionString(string role, string password) =>
        new NpgsqlConnectionStringBuilder(_ctx.ConnectionString)
        {
            Username = role,
            Password = password,
            Pooling = false,
        }.ConnectionString;

    private sealed class RestrictedRoleConnectionInterceptor(string expectedRole) : DbConnectionInterceptor
    {
        public int OpenCount { get; private set; }

        public override async Task ConnectionOpenedAsync(
            DbConnection connection,
            ConnectionEndEventData eventData,
            CancellationToken cancellationToken = default)
        {
            await DatabaseRuntimeIdentity.ValidateOpenedConnectionAsync(
                connection,
                expectedRole,
                cancellationToken);
            OpenCount++;
        }
    }

    private sealed class AuditInsertFailureInterceptor : DbCommandInterceptor
    {
        public bool FailAtomicAudit { get; set; }

        public override InterceptionResult<DbDataReader> ReaderExecuting(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result)
        {
            ThrowIfConfigured(command);
            return result;
        }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            ThrowIfConfigured(command);
            return ValueTask.FromResult(result);
        }

        private void ThrowIfConfigured(DbCommand command)
        {
            if (FailAtomicAudit
                && command.CommandText.Contains(
                    "INSERT INTO \"AtomicAuditLogs\"",
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Injected late-fee audit failure.");
            }
        }
    }

    private sealed class ScheduledJournalPostingReadCounter : DbCommandInterceptor
    {
        public int ReadCount { get; private set; }

        public void Reset() => ReadCount = 0;

        public override InterceptionResult<DbDataReader> ReaderExecuting(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result)
        {
            Count(command);
            return base.ReaderExecuting(command, eventData, result);
        }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            Count(command);
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }

        private void Count(DbCommand command)
        {
            if (command.CommandText.Contains("FROM \"LedgerAccounts\"", StringComparison.Ordinal)
                || command.CommandText.Contains("FROM \"JournalEntries\"", StringComparison.Ordinal))
            {
                ReadCount++;
            }
        }
    }

    private sealed class RentSqlCaptureInterceptor : DbCommandInterceptor
    {
        public List<string> Commands { get; } = [];

        public override InterceptionResult<DbDataReader> ReaderExecuting(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result)
        {
            Commands.Add(command.CommandText);
            return result;
        }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            Commands.Add(command.CommandText);
            return ValueTask.FromResult(result);
        }
    }
}
