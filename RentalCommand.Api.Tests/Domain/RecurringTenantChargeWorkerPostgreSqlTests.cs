using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using RentalCommand.Core.Atomic;
using RentalCommand.Core.Automation;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Data.Accounting;
using RentalCommand.Data.Payments;
using RentalCommand.TestCommon;

namespace RentalCommand.Api.Tests.Domain;

[Collection(MigratedPostgreSqlCollection.Name)]
public sealed class RecurringTenantChargeWorkerPostgreSqlTests
{
    private const int PortfolioId = 1;
    private static readonly DateTime JanuaryRunUtc =
        new(2027, 1, 31, 12, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime FebruaryRunUtc =
        new(2027, 2, 28, 12, 0, 0, DateTimeKind.Utc);
    private static readonly AtomicJsonResultCodec<ApplyRecurringTenantChargeBatchResult> Codec =
        new("scheduled-finance.recurring-tenant-charge.apply.v1");

    private readonly MigratedPostgreSqlFixture _fixture;

    public RecurringTenantChargeWorkerPostgreSqlTests(MigratedPostgreSqlFixture fixture) =>
        _fixture = fixture;

    [Fact]
    public async Task WorkerAcrossMonthBoundary_CreatesOneChargeAndJournalPerOccurrence()
    {
        await using var setup = await _fixture.CreateContextAsync();
        await new ChartOfAccountsSeedService(setup.Db).SeedAsync(PortfolioId);
        await setup.Db.SaveChangesAsync();
        var schedule = SeedSchedule(setup.Db);
        await setup.Db.SaveChangesAsync();

        await using var services = AtomicDomainTestKernel.CreateForScheduledTenantChargesPostgreSql(
            setup.ConnectionString);
        var db = services.GetRequiredService<RentalCommand.Data.RentalCommandDbContext>();
        var writes = services.GetRequiredService<IWriteExecutor>();
        var handler = new ApplyRecurringTenantChargeBatchHandler(db);

        var januaryCommand = new ApplyRecurringTenantChargeBatchCommand(
                Guid.Parse("6b4c66d1-6f41-4a6f-a0f5-0a1dbf11cb01"),
                JanuaryRunUtc,
                200);
        var februaryCommand = new ApplyRecurringTenantChargeBatchCommand(
                Guid.Parse("734b29db-27db-4f97-8e9e-a8d7c96a5902"),
                FebruaryRunUtc,
                200);
        var january = await writes.ExecuteAsync(
            "recurring-charge-january",
            TenantMoneyWriteSupport.Write(
                januaryCommand, handler.ExecuteAsync, handler.AuthorizeAsync));
        var february = await writes.ExecuteAsync(
            "recurring-charge-february",
            TenantMoneyWriteSupport.Write(
                februaryCommand, handler.ExecuteAsync, handler.AuthorizeAsync));
        var replay = await writes.ExecuteAsync(
            "recurring-charge-february",
            TenantMoneyWriteSupport.Write(
                februaryCommand, handler.ExecuteAsync, handler.AuthorizeAsync));

        january.Value.ChargeCount.Should().Be(1);
        february.Value.ChargeCount.Should().Be(1);
        replay.Disposition.Should().Be(AtomicCommandDisposition.Replayed);
        replay.Value.ChargeCount.Should().Be(1);

        setup.Db.ChangeTracker.Clear();
        var charges = await setup.Db.TenantLedgerEntries
            .AsNoTracking()
            .Where(entry => entry.TenantAccountId == schedule.TenantAccountId
                && entry.BusinessKey.StartsWith($"recurring-tenant-charge:{schedule.Id}:"))
            .OrderBy(entry => entry.EffectiveOn)
            .ToListAsync();
        charges.Should().HaveCount(2);
        charges.Select(entry => entry.EffectiveOn)
            .Should().Equal(new DateOnly(2027, 1, 31), new DateOnly(2027, 2, 28));
        charges.Select(entry => entry.Amount).Should().OnlyContain(amount => amount == 275m);

        var journals = await setup.Db.JournalEntries
            .AsNoTracking()
            .Where(entry => entry.SourceType == JournalSourceType.TenantCharge
                && charges.Select(charge => charge.Id).Contains(entry.SourceId))
            .ToListAsync();
        journals.Should().HaveCount(2);
        (await setup.Db.JournalEntries.CountAsync(entry =>
                entry.SourceType == JournalSourceType.TenantCharge
                && entry.SourceBusinessKey.StartsWith($"recurring-tenant-charge:{schedule.Id}:")))
            .Should().Be(2);

        var nextRunDate = await setup.Db.RecurringTenantCharges
            .Where(row => row.Id == schedule.Id)
            .Select(row => row.NextRunDate)
            .SingleAsync();
        nextRunDate.Should().Be(new DateOnly(2027, 3, 31));
    }

    [Fact]
    public async Task FrozenLegacyReceiptReplaysWithoutCreatingTenantCharge()
    {
        await using var setup = await _fixture.CreateContextAsync();
        await new ChartOfAccountsSeedService(setup.Db).SeedAsync(PortfolioId);
        await setup.Db.SaveChangesAsync();
        var schedule = SeedSchedule(setup.Db);
        await setup.Db.SaveChangesAsync();
        var token = Guid.Parse("2fc1eeb9-0400-4ea2-b5f3-4012e0468396");
        var command = new ApplyRecurringTenantChargeBatchCommand(token, JanuaryRunUtc, 200);
        var stored = new ApplyRecurringTenantChargeBatchResult(6);
        setup.Db.AtomicCommandReceipts.Add(new AtomicCommandReceipt
        {
            Id = Guid.NewGuid(),
            AttemptId = Guid.NewGuid(),
            CommandType = "scheduled-finance.recurring-tenant-charge.apply",
            IdempotencyKey = token.ToString("N"),
            RequestFingerprint = AtomicCommandFingerprint.Create(command),
            Status = AtomicCommandReceiptStatus.Completed,
            ResultContract = Codec.ContractName,
            ResultJson = Codec.Serialize(stored),
            StartedAt = JanuaryRunUtc,
            CompletedAt = JanuaryRunUtc,
        });
        await setup.Db.SaveChangesAsync();

        await using var services = AtomicDomainTestKernel.CreateForScheduledTenantChargesPostgreSql(
            setup.ConnectionString);
        var db = services.GetRequiredService<RentalCommand.Data.RentalCommandDbContext>();
        var handler = new ApplyRecurringTenantChargeBatchHandler(db);
        var replay = await services.GetRequiredService<IWriteExecutor>().ExecuteAsync(
            token.ToString("N"),
            TenantMoneyWriteSupport.Write(
                command, handler.ExecuteAsync, handler.AuthorizeAsync));

        replay.Disposition.Should().Be(AtomicCommandDisposition.Replayed);
        replay.Value.Should().Be(stored);
        setup.Db.ChangeTracker.Clear();
        (await setup.Db.TenantLedgerEntries.CountAsync(entry =>
            entry.TenantAccountId == schedule.TenantAccountId
            && entry.BusinessKey.StartsWith($"recurring-tenant-charge:{schedule.Id}:")))
            .Should().Be(0);
    }

    private static RecurringTenantCharge SeedSchedule(RentalCommand.Data.RentalCommandDbContext db)
    {
        var now = JanuaryRunUtc.AddDays(-10);
        var source = new LegalDocumentSourceVersion
        {
            PublicId = Guid.NewGuid(),
            PortfolioId = PortfolioId,
            SourceKind = LegalDocumentSourceKind.BuiltInRenderer,
            BusinessKey = $"recurring-charge-source:{Guid.NewGuid():N}",
            RendererKey = "recurring-charge-test",
            RendererVersion = 1,
            SnapshotPayload = "{}",
            CreatedAtUtc = now,
            CreatedByUserId = 1,
        };
        var property = new Property
        {
            PortfolioId = PortfolioId,
            Name = "Recurring Charge Property",
            AddressLine1 = "1 Recurring Lane",
            City = "Columbus",
            State = "OH",
            PostalCode = "43215",
            CreatedAt = now,
            UpdatedAt = now,
        };
        var unit = new Unit
        {
            PortfolioId = PortfolioId,
            Property = property,
            UnitNumber = "1",
            MarketRent = 1_000m,
            CreatedAt = now,
            UpdatedAt = now,
        };
        var management = new LeaseManagement
        {
            PortfolioId = PortfolioId,
            Property = property,
            Unit = unit,
            RelationshipNumber = $"RECURRING-{Guid.NewGuid():N}",
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
            CreatedByUserId = 1,
            RowVersion = Guid.NewGuid(),
        };
        var account = new TenantAccount
        {
            PortfolioId = PortfolioId,
            LeaseManagement = management,
            AccountNumber = $"TA-RECURRING-{Guid.NewGuid():N}",
            Currency = "USD",
            OpenedAtUtc = now,
            CreatedAtUtc = now,
            CreatedByUserId = 1,
        };
        var agreement = new LeaseAgreement
        {
            PublicId = Guid.NewGuid(),
            PortfolioId = PortfolioId,
            LeaseManagement = management,
            VersionNumber = 1,
            AgreementNumber = $"AGR-RECURRING-{Guid.NewGuid():N}",
            ChangeType = LeaseAgreementChangeType.Initial,
            TermType = LeaseAgreementTermType.FixedTerm,
            TermStartOn = new DateOnly(2027, 1, 1),
            TermEndOn = new DateOnly(2027, 12, 31),
            GoverningFromOn = new DateOnly(2027, 1, 1),
            BaseRentAmount = 1_000m,
            RentDueDay = 1,
            SecurityDepositObligation = 0m,
            LateFeeAmount = 0m,
            GracePeriodDays = 5,
            Currency = "USD",
            TermsSchemaVersion = 1,
            TermsPayload = "{}",
            DocumentSourceVersion = source,
            CreatedAtUtc = now,
            CreatedByUserId = 1,
            UpdatedAtUtc = now,
        };
        var incomeAccount = db.LedgerAccounts
            .Single(accountRow => accountRow.PortfolioId == PortfolioId && accountRow.Code == "4000");
        var schedule = new RecurringTenantCharge
        {
            PublicId = Guid.NewGuid(),
            PortfolioId = PortfolioId,
            TenantAccount = account,
            LeaseAgreement = agreement,
            DisplayName = "Monthly pet rent",
            Amount = 275m,
            Currency = "USD",
            LedgerAccountId = incomeAccount.Id,
            EffectiveStartOn = new DateOnly(2027, 1, 1),
            MonthlyDueDay = 31,
            NextRunDate = new DateOnly(2027, 1, 31),
            IsActive = true,
            Property = property,
            Unit = unit,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
        };

        db.AddRange(source, property, unit, management, account, agreement, schedule);
        return schedule;
    }

}
