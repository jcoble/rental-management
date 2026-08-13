using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using System.Data.Common;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Models.Accounting;
using RentalCommand.Data.Accounting;
using RentalCommand.TestCommon;
using RentalCommand.Api.DTOs;
using RentalCommand.Api.Services.Domain;
using RentalCommand.Core.Authorization;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Leasing;
using RentalCommand.Core.Time;

namespace RentalCommand.Api.Tests.Domain;

[Collection(MigratedPostgreSqlCollection.Name)]
public sealed class AccountingReadModelPostgreSqlTests
{
    private readonly MigratedPostgreSqlFixture _fixture;

    public AccountingReadModelPostgreSqlTests(MigratedPostgreSqlFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task GeneralLedger_UsesServerSideRunningBalanceAndKeepsMixedAccountBalanceNull()
    {
        await using var setup = await _fixture.CreateContextAsync();
        var seed = new ChartOfAccountsSeedService(setup.Db);
        await seed.SeedAsync(1);
        await setup.Db.SaveChangesAsync();
        setup.Db.ChangeTracker.Clear();

        var scope = setup.Db.SeedAdministratorScope(1, nameof(GeneralLedger_UsesServerSideRunningBalanceAndKeepsMixedAccountBalanceNull));

        var accounts = await setup.Db.LedgerAccounts
            .Where(account => account.PortfolioId == 1)
            .OrderBy(account => account.Code)
            .Take(2)
            .ToArrayAsync();
        var proposal = new AccountingProposedEntry
        {
            PortfolioId = 1,
            EffectiveOn = new DateOnly(2026, 8, 1),
            Currency = "USD",
            Description = "Read model test",
            SourceType = JournalSourceType.OpeningBalance,
            SourceId = 8101,
            SourceBusinessKey = "read-model:8101",
            PostingRuleVersion = 1,
            AttemptId = Guid.NewGuid(),
            AtomicReceiptId = Guid.NewGuid(),
            UserId = 1,
            Lines =
            [
                new AccountingProposedLine { LedgerAccountId = accounts[0].Id, DebitAmount = 100m },
                new AccountingProposedLine { LedgerAccountId = accounts[1].Id, CreditAmount = 100m },
            ],
        };
        await new AccountingPostingService(setup.Db).PostAsync(proposal);
        await setup.Db.SaveChangesAsync();
        setup.Db.ChangeTracker.Clear();
        await setup.ActivateApiScopeAsync(scope);

        var service = new AccountingLedgerReadModelService(setup.Db);
        var accountPage = await service.GetGeneralLedgerAsync(scope, new GeneralLedgerQuery
        {
            AccountId = accounts[0].Id,
            Take = 20,
        }, CancellationToken.None);

        accountPage.TotalCount.Should().Be(1);
        accountPage.Items.Should().ContainSingle().Which.RunningBalance.Should().Be(100m);

        var mixedPage = await service.GetGeneralLedgerAsync(scope, new GeneralLedgerQuery
        {
            Take = 20,
        }, CancellationToken.None);

        mixedPage.Items.Should().HaveCount(2);
        mixedPage.Items.Should().OnlyContain(item => !item.RunningBalance.HasValue);
    }

    [Fact]
    public async Task GeneralLedger_UsesAtMostThreeSqlStatementsAndDoesNotAggregateInMemory()
    {
        var commands = new SqlCommandCounter();
        await using var setup = await _fixture.CreateContextAsync([commands]);
        var seed = new ChartOfAccountsSeedService(setup.Db);
        await seed.SeedAsync(1);
        await setup.Db.SaveChangesAsync();
        setup.Db.ChangeTracker.Clear();
        var scope = setup.Db.SeedAdministratorScope(1, nameof(GeneralLedger_UsesAtMostThreeSqlStatementsAndDoesNotAggregateInMemory));
        var cash = await AccountAsync(setup, "operating-cash");
        var income = await AccountAsync(setup, "rental-income");
        for (var index = 0; index < 20; index++)
        {
            await PostAsync(setup, 8_100 + index, new DateOnly(2026, 8, 1).AddDays(index),
                new AccountingProposedLine { LedgerAccountId = cash.Id, DebitAmount = 1m },
                new AccountingProposedLine { LedgerAccountId = income.Id, CreditAmount = 1m });
        }
        commands.Reset();
        await setup.ActivateApiScopeAsync(scope);
        commands.Reset();

        var service = new AccountingLedgerReadModelService(setup.Db);
        var page = await service.GetGeneralLedgerAsync(
            scope, new GeneralLedgerQuery { AccountId = cash.Id, Take = 20 }, CancellationToken.None);

        page.Items.Should().HaveCount(20);
        commands.Count.Should().BeLessThanOrEqualTo(3);
        commands.Sql.Should().Contain(sql => sql.Contains("JournalLines", StringComparison.Ordinal));
        commands.Sql.Should().Contain(sql => sql.Contains("SUM", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task TenantLedger_PageAssociatesEveryAllocationServerSideWithinThreeCommands()
    {
        var commands = new SqlCommandCounter();
        await using var setup = await _fixture.CreateContextAsync([commands]);
        var scope = setup.Db.SeedAdministratorScope(
            1, nameof(TenantLedger_PageAssociatesEveryAllocationServerSideWithinThreeCommands));
        var now = new DateTime(2027, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        var property = new Property
        {
            PortfolioId = 1,
            Name = "Allocation projection property",
            AddressLine1 = "1 Allocation Way",
            City = "Columbus",
            State = "OH",
            PostalCode = "43215",
            CreatedAt = now,
            UpdatedAt = now,
        };
        var unit = new Unit
        {
            PortfolioId = 1,
            Property = property,
            UnitNumber = "1A",
            MarketRent = 900m,
            CreatedAt = now,
            UpdatedAt = now,
        };
        var relationship = new LeaseManagement
        {
            PortfolioId = 1,
            Property = property,
            Unit = unit,
            RelationshipNumber = "ALLOC-PROJECTION",
            PossessionGivenAtUtc = now,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
            CreatedByUserId = scope.UserId,
            RowVersion = Guid.NewGuid(),
        };
        var account = new TenantAccount
        {
            PortfolioId = 1,
            LeaseManagement = relationship,
            AccountNumber = "ALLOC-PROJECTION",
            Currency = "USD",
            OpenedAtUtc = now,
            CreatedAtUtc = now,
            CreatedByUserId = scope.UserId,
        };
        setup.Db.AddRange(property, unit, relationship, account);
        await setup.Db.SaveChangesAsync();

        var charge = new TenantLedgerEntry
        {
            PublicId = Guid.NewGuid(),
            PortfolioId = 1,
            TenantAccountId = account.Id,
            EntryType = TenantLedgerEntryType.ManualCharge,
            Direction = TenantLedgerDirection.Debit,
            Amount = 900m,
            Currency = "USD",
            EffectiveOn = new DateOnly(2027, 1, 1),
            DueOn = new DateOnly(2027, 1, 1),
            PostedAtUtc = now,
            Description = "January rent",
            BusinessKey = "allocation-projection:charge",
            CreatedByUserId = scope.UserId,
        };
        var receipt = new TenantLedgerEntry
        {
            PublicId = Guid.NewGuid(),
            PortfolioId = 1,
            TenantAccountId = account.Id,
            EntryType = TenantLedgerEntryType.PaymentReceipt,
            Direction = TenantLedgerDirection.Credit,
            Amount = 900m,
            Currency = "USD",
            EffectiveOn = new DateOnly(2027, 1, 2),
            PostedAtUtc = now.AddDays(1),
            Description = "January payment",
            BusinessKey = "allocation-projection:receipt",
            CreatedByUserId = scope.UserId,
        };
        setup.Db.AddRange(charge, receipt);
        await setup.Db.SaveChangesAsync();

        var original = new TenantLedgerAllocation
        {
            PortfolioId = 1,
            TenantAccountId = account.Id,
            DebitEntryId = charge.Id,
            CreditEntryId = receipt.Id,
            Amount = 900m,
            AllocatedAtUtc = now,
            BusinessKey = "allocation-projection:original",
            CreatedByUserId = scope.UserId,
        };
        setup.Db.TenantLedgerAllocations.Add(original);
        await setup.Db.SaveChangesAsync();
        setup.Db.ChangeTracker.Clear();

        var compensating = new TenantLedgerAllocation
        {
            PortfolioId = 1,
            TenantAccountId = account.Id,
            DebitEntryId = charge.Id,
            CreditEntryId = receipt.Id,
            Amount = -900m,
            ReversesAllocationId = original.Id,
            AllocatedAtUtc = now.AddMinutes(1),
            BusinessKey = "allocation-projection:compensating",
            CreatedByUserId = scope.UserId,
        };
        setup.Db.TenantLedgerAllocations.Add(compensating);
        await setup.Db.SaveChangesAsync();
        setup.Db.ChangeTracker.Clear();
        FreezeBusinessClock(setup, new DateTime(2027, 1, 2, 12, 0, 0, DateTimeKind.Utc));
        await setup.ActivateApiScopeAsync(scope);
        commands.Reset();

        var page = await new AccountingLedgerReadModelService(setup.Db).GetTenantLedgerAsync(
            scope,
            account.Id,
            new TenantLedgerQuery { Take = 20, Sort = "effectiveOn" },
            CancellationToken.None);

        var chargeRow = page!.Items.Single(row => row.TenantLedgerEntryId == charge.Id);
        chargeRow.Allocations.Select(allocation => allocation.AllocationId)
            .Should().Equal(original.Id, compensating.Id);
        chargeRow.Allocations.Select(allocation => allocation.Amount)
            .Should().Equal(900m, -900m);
        page.Items.Single(row => row.TenantLedgerEntryId == receipt.Id)
            .Allocations.Select(allocation => allocation.AllocationId)
            .Should().Equal(original.Id, compensating.Id);
        page.Items.Single(row => row.TenantLedgerEntryId == charge.Id).ActionCapabilities
            .Should().Match<TenantLedgerActionCapabilities>(capabilities =>
                !capabilities.CanGiveCredit
                && !capabilities.CanAddRelatedCharge
                && !capabilities.CanReverseCharge
                && !capabilities.CanReviewPaymentAllocation);
        page.Items.Single(row => row.TenantLedgerEntryId == receipt.Id).ActionCapabilities
            .Should().Match<TenantLedgerActionCapabilities>(capabilities =>
                !capabilities.CanGiveCredit
                && capabilities.CanReviewPaymentAllocation);

        commands.Count.Should().BeLessThanOrEqualTo(3);
        commands.Sql.Should().Contain(sql =>
            sql.Contains("TenantLedgerAllocations", StringComparison.Ordinal)
            && sql.Contains("TenantLedgerEntries", StringComparison.Ordinal)
            && sql.Contains("LEFT JOIN LATERAL", StringComparison.OrdinalIgnoreCase)
            && sql.Contains("TenantAccountId", StringComparison.Ordinal)
            && sql.Contains("DebitEntryId", StringComparison.Ordinal)
            && sql.Contains("CreditEntryId", StringComparison.Ordinal)
            && sql.Contains("ORDER BY", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task TenantLedgerMonthSummary_ProjectsIndependentReviewFactAndServerGroupsRowsInOneAuthorizedSql()
    {
        var commands = new SqlCommandCounter();
        await using var setup = await _fixture.CreateContextAsync([commands]);
        await new ChartOfAccountsSeedService(setup.Db).SeedAsync(1);
        await setup.Db.SaveChangesAsync();
        var scope = setup.Db.SeedAdministratorScope(
            1, nameof(TenantLedgerMonthSummary_ProjectsIndependentReviewFactAndServerGroupsRowsInOneAuthorizedSql));
        var now = new DateTime(2027, 2, 1, 12, 0, 0, DateTimeKind.Utc);
        var property = new Property
        {
            PortfolioId = 1, Name = "Month projection property", AddressLine1 = "1 Month Way",
            City = "Columbus", State = "OH", PostalCode = "43215", CreatedAt = now, UpdatedAt = now,
        };
        var unit = new Unit
        {
            PortfolioId = 1, Property = property, UnitNumber = "M1", MarketRent = 1_000m,
            CreatedAt = now, UpdatedAt = now,
        };
        var relationship = new LeaseManagement
        {
            PortfolioId = 1, Property = property, Unit = unit, RelationshipNumber = "MONTH-PROJECTION",
            PossessionGivenAtUtc = now, CreatedAtUtc = now, UpdatedAtUtc = now,
            CreatedByUserId = scope.UserId, RowVersion = Guid.NewGuid(),
        };
        var account = new TenantAccount
        {
            PortfolioId = 1, LeaseManagement = relationship, AccountNumber = "MONTH-PROJECTION",
            Currency = "USD", OpenedAtUtc = now, CreatedAtUtc = now, CreatedByUserId = scope.UserId,
        };
        setup.Db.AddRange(property, unit, relationship, account);
        await setup.Db.SaveChangesAsync();

        var charge = new TenantLedgerEntry
        {
            PublicId = Guid.NewGuid(), PortfolioId = 1, TenantAccountId = account.Id,
            EntryType = TenantLedgerEntryType.ManualCharge, Direction = TenantLedgerDirection.Debit,
            Amount = 100m, Currency = "USD", EffectiveOn = new DateOnly(2027, 2, 1),
            DueOn = new DateOnly(2027, 2, 1), PostedAtUtc = now, Description = "Rent for February 2027",
            BusinessKey = "month-projection:charge", CreatedByUserId = scope.UserId,
        };
        setup.Db.TenantLedgerEntries.Add(charge);
        await setup.Db.SaveChangesAsync();
        var receivable = await AccountAsync(setup, "tenant-accounts-receivable");
        var income = await AccountAsync(setup, "rental-income");
        setup.Db.JournalEntries.Add(new JournalEntry
        {
            PublicId = Guid.NewGuid(), PortfolioId = 1, EffectiveOn = charge.EffectiveOn,
            PostedAtUtc = now, Currency = "USD", Description = "Mismatched rent posting",
            SourceType = JournalSourceType.TenantCharge, SourceId = charge.Id,
            SourceBusinessKey = "month-projection:journal", IdempotencyDigest = new string('a', 64), PostingRuleVersion = 1,
            AttemptId = Guid.NewGuid(), AtomicReceiptId = Guid.NewGuid(),
            Lines =
            [
                new JournalLine
                {
                    LedgerAccountId = receivable.Id, TenantAccountId = account.Id,
                    DebitAmount = 80m, CreditAmount = 0m,
                },
                new JournalLine { LedgerAccountId = income.Id, CreditAmount = 80m },
            ],
        });
        await setup.Db.SaveChangesAsync();
        await setup.ActivateApiScopeAsync(scope);
        commands.Reset();

        var summaries = await new AccountingLedgerReadModelService(setup.Db).GetTenantMonthSummaryAsync(
            scope, account.Id,
            new TenantMonthSummaryQuery
            {
                From = new DateOnly(2027, 2, 1),
                To = new DateOnly(2027, 2, 28),
                Take = 20,
            });

        summaries.Should().ContainSingle();
        summaries[0].NeedsReview.Should().BeTrue();
        summaries[0].Rows.Should().ContainSingle().Which.Should().Match<TenantLedgerRow>(row =>
            row.TenantLedgerEntryId == charge.Id
            && row.ActionCapabilities.CanGiveCredit
            && row.ActionCapabilities.CanAddRelatedCharge
            && row.ActionCapabilities.CanReverseCharge);
        commands.Count.Should().Be(1);
        commands.Sql.Single().Should().Contain("authorized_properties AS MATERIALIZED")
            .And.Contain("jsonb_agg")
            .And.Contain("ROW_NUMBER() OVER")
            .And.Contain("ReplacedByEntryId")
            .And.Contain("TenantLedgerAllocations")
            .And.Contain("ORDER BY running.\"Year\" DESC")
            .And.Contain("entry.\"PortfolioId\" = @")
            .And.Contain("entry.\"TenantAccountId\" = @");
    }

    [Fact]
    public async Task TenantLedger_JournalSourceTypeOwnsDetailsCapabilitiesAndMonthReviewFacts()
    {
        var commands = new SqlCommandCounter();
        await using var setup = await _fixture.CreateContextAsync([commands]);
        await new ChartOfAccountsSeedService(setup.Db).SeedAsync(1);
        await setup.Db.SaveChangesAsync();
        var scope = setup.Db.SeedAdministratorScope(
            1, nameof(TenantLedger_JournalSourceTypeOwnsDetailsCapabilitiesAndMonthReviewFacts));
        var now = new DateTime(2027, 3, 1, 12, 0, 0, DateTimeKind.Utc);
        var account = await CreateTenantAccountAsync(setup, scope, now, "SOURCE-TYPE-PROJECTION");

        TenantLedgerEntry NewEntry(
            decimal amount,
            DateOnly effectiveOn,
            string description,
            string businessKey) => new()
            {
                PublicId = Guid.NewGuid(),
                PortfolioId = 1,
                TenantAccountId = account.Id,
                EntryType = TenantLedgerEntryType.ManualCharge,
                Direction = TenantLedgerDirection.Debit,
                Amount = amount,
                Currency = "USD",
                EffectiveOn = effectiveOn,
                DueOn = effectiveOn,
                PostedAtUtc = now,
                Description = description,
                BusinessKey = businessKey,
                CreatedByUserId = scope.UserId,
            };

        var wrongSource = NewEntry(100m, new DateOnly(2027, 3, 1),
            "Wrong source journal charge", "source-type:wrong");
        var missingExpected = NewEntry(50m, new DateOnly(2027, 4, 1),
            "Missing expected journal charge", "source-type:missing");
        var correctSource = NewEntry(80m, new DateOnly(2027, 5, 1),
            "Correct source journal charge", "source-type:correct");
        setup.Db.TenantLedgerEntries.AddRange(wrongSource, missingExpected, correctSource);
        await setup.Db.SaveChangesAsync();

        var receivable = await AccountAsync(setup, "tenant-accounts-receivable");
        var income = await AccountAsync(setup, "rental-income");
        setup.Db.JournalEntries.AddRange(
            new JournalEntry
            {
                PublicId = Guid.NewGuid(),
                PortfolioId = 1,
                EffectiveOn = wrongSource.EffectiveOn,
                PostedAtUtc = now,
                Currency = "USD",
                Description = "Unrelated receipt with colliding source id",
                SourceType = JournalSourceType.TenantReceipt,
                SourceId = wrongSource.Id,
                SourceBusinessKey = "source-type:wrong-journal",
                IdempotencyDigest = new string('w', 64),
                PostingRuleVersion = 1,
                AttemptId = Guid.NewGuid(),
                AtomicReceiptId = Guid.NewGuid(),
                Lines =
                [
                    new JournalLine { LedgerAccountId = receivable.Id, TenantAccountId = account.Id, DebitAmount = 100m },
                    new JournalLine { LedgerAccountId = income.Id, CreditAmount = 100m },
                ],
            },
            new JournalEntry
            {
                PublicId = Guid.NewGuid(),
                PortfolioId = 1,
                EffectiveOn = correctSource.EffectiveOn,
                PostedAtUtc = now,
                Currency = "USD",
                Description = "Correct tenant charge posting",
                SourceType = JournalSourceType.TenantCharge,
                SourceId = correctSource.Id,
                SourceBusinessKey = "source-type:correct-journal",
                IdempotencyDigest = new string('c', 64),
                PostingRuleVersion = 1,
                AttemptId = Guid.NewGuid(),
                AtomicReceiptId = Guid.NewGuid(),
                Lines =
                [
                    new JournalLine { LedgerAccountId = receivable.Id, TenantAccountId = account.Id, DebitAmount = 80m },
                    new JournalLine { LedgerAccountId = income.Id, CreditAmount = 80m },
                ],
            });
        await setup.Db.SaveChangesAsync();
        await setup.ActivateApiScopeAsync(scope);

        var service = new AccountingLedgerReadModelService(setup.Db);
        commands.Reset();
        var ledger = await service.GetTenantLedgerAsync(scope, account.Id,
            new TenantLedgerQuery { Take = 20, Sort = "effectiveOn" });

        var wrongRow = ledger!.Items.Single(row => row.TenantLedgerEntryId == wrongSource.Id);
        wrongRow.JournalEntryPublicId.Should().BeNull();
        wrongRow.ActionCapabilities.Should().Match<TenantLedgerActionCapabilities>(capabilities =>
            !capabilities.CanGiveCredit && !capabilities.CanAddRelatedCharge
            && !capabilities.CanReverseCharge && !capabilities.CanReverseLedgerEntry);
        ledger.Items.Single(row => row.TenantLedgerEntryId == missingExpected.Id)
            .JournalEntryPublicId.Should().BeNull();
        ledger.Items.Single(row => row.TenantLedgerEntryId == correctSource.Id)
            .Should().Match<TenantLedgerRow>(row => row.JournalEntryPublicId.HasValue
                && row.ActionCapabilities.CanGiveCredit
                && row.ActionCapabilities.CanAddRelatedCharge
                && row.ActionCapabilities.CanReverseCharge);
        commands.Sql.Should().Contain(sql =>
            sql.Contains("JournalEntries", StringComparison.Ordinal)
            && sql.Contains("SourceType", StringComparison.Ordinal)
            && sql.Contains("TenantCharge", StringComparison.Ordinal)
            && sql.Contains("TenantReceipt", StringComparison.Ordinal)
            && sql.Contains("TenantConcession", StringComparison.Ordinal)
            && sql.Contains("OpeningBalance", StringComparison.Ordinal));

        commands.Reset();
        var summaries = await service.GetTenantMonthSummaryAsync(scope, account.Id,
            new TenantMonthSummaryQuery
            {
                From = new DateOnly(2027, 3, 1),
                To = new DateOnly(2027, 5, 31),
                Take = 20,
            });

        summaries.Should().HaveCount(3);
        summaries.Single(summary => summary.Month == 3).Should().Match<TenantMonthSummary>(summary =>
            summary.NeedsReview && summary.Rows.Single().TenantLedgerEntryId == wrongSource.Id
                && summary.Rows.Single().JournalEntryPublicId == null);
        summaries.Single(summary => summary.Month == 4).Should().Match<TenantMonthSummary>(summary =>
            summary.NeedsReview && summary.Rows.Single().TenantLedgerEntryId == missingExpected.Id
                && summary.Rows.Single().JournalEntryPublicId == null);
        summaries.Single(summary => summary.Month == 5).Should().Match<TenantMonthSummary>(summary =>
            !summary.NeedsReview && summary.Rows.Single().TenantLedgerEntryId == correctSource.Id
                && summary.Rows.Single().JournalEntryPublicId != null);
        commands.Count.Should().Be(1);
        commands.Sql.Single().Should().Contain("SourceType")
            .And.Contain("TenantCharge")
            .And.Contain("TenantReceipt")
            .And.Contain("TenantConcession")
            .And.Contain("OpeningBalance")
            .And.Contain("entry.\"PortfolioId\" =")
            .And.Contain("entry.\"TenantAccountId\" =");
    }

    [Fact]
    public async Task TenantLedgerMonthSummary_SettledOnlyMatchesLedgerForChargeAndNonChargeRows()
    {
        var commands = new SqlCommandCounter();
        await using var setup = await _fixture.CreateContextAsync([commands]);
        var scope = setup.Db.SeedAdministratorScope(
            1, nameof(TenantLedgerMonthSummary_SettledOnlyMatchesLedgerForChargeAndNonChargeRows));
        var now = new DateTime(2027, 6, 1, 12, 0, 0, DateTimeKind.Utc);
        var account = await CreateTenantAccountAsync(setup, scope, now, "SETTLED-PARITY");

        TenantLedgerEntry NewEntry(
            TenantLedgerEntryType entryType,
            TenantLedgerDirection direction,
            decimal amount,
            string businessKey) => new()
            {
                PublicId = Guid.NewGuid(),
                PortfolioId = 1,
                TenantAccountId = account.Id,
                EntryType = entryType,
                Direction = direction,
                Amount = amount,
                Currency = "USD",
                EffectiveOn = new DateOnly(2027, 6, 15),
                DueOn = entryType is TenantLedgerEntryType.RentCharge
                    or TenantLedgerEntryType.AddendumCharge
                    or TenantLedgerEntryType.LateFeeCharge
                    or TenantLedgerEntryType.DepositCharge
                    or TenantLedgerEntryType.ManualCharge
                    ? new DateOnly(2027, 6, 15)
                    : null,
                PostedAtUtc = now,
                Description = $"Settled parity {entryType}",
                BusinessKey = businessKey,
                CreatedByUserId = scope.UserId,
            };

        var settledCharge = NewEntry(TenantLedgerEntryType.ManualCharge, TenantLedgerDirection.Debit, 100m, "settled:charge");
        var openCharge = NewEntry(TenantLedgerEntryType.ManualCharge, TenantLedgerDirection.Debit, 120m, "settled:open-charge");
        var openingBalance = NewEntry(TenantLedgerEntryType.OpeningBalance, TenantLedgerDirection.Debit, 300m, "settled:opening");
        var refund = NewEntry(TenantLedgerEntryType.Refund, TenantLedgerDirection.Debit, 40m, "settled:refund");
        var transferIn = NewEntry(TenantLedgerEntryType.TransferIn, TenantLedgerDirection.Debit, 50m, "settled:transfer-in");
        var transferOut = NewEntry(TenantLedgerEntryType.TransferOut, TenantLedgerDirection.Debit, 60m, "settled:transfer-out");
        var reversal = NewEntry(TenantLedgerEntryType.Reversal, TenantLedgerDirection.Debit, 100m, "settled:reversal");
        var receipt = NewEntry(TenantLedgerEntryType.PaymentReceipt, TenantLedgerDirection.Credit, 100m, "settled:receipt");
        transferIn.TransferPublicId = Guid.NewGuid();
        transferOut.TransferPublicId = Guid.NewGuid();
        setup.Db.TenantLedgerEntries.AddRange(
            settledCharge, openCharge, openingBalance, refund, transferIn, transferOut, receipt);
        await setup.Db.SaveChangesAsync();
        reversal.ReversesEntryId = receipt.Id;
        setup.Db.TenantLedgerEntries.Add(reversal);
        await setup.Db.SaveChangesAsync();
        setup.Db.TenantLedgerAllocations.Add(new TenantLedgerAllocation
        {
            PortfolioId = 1,
            TenantAccountId = account.Id,
            DebitEntryId = settledCharge.Id,
            CreditEntryId = receipt.Id,
            Amount = 100m,
            AllocatedAtUtc = now,
            BusinessKey = "settled:allocation",
            CreatedByUserId = scope.UserId,
        });
        await setup.Db.SaveChangesAsync();
        FreezeBusinessClock(setup, new DateTime(2027, 6, 15, 12, 0, 0, DateTimeKind.Utc));
        await setup.ActivateApiScopeAsync(scope);
        var service = new AccountingLedgerReadModelService(setup.Db);

        var chargeBalances = await setup.Db.TenantChargeBalanceProjections
            .Where(row => row.TenantAccountId == account.Id)
            .ToDictionaryAsync(row => row.TenantLedgerEntryId);
        chargeBalances[settledCharge.Id].OpenAmount.Should().Be(0m);
        chargeBalances[openCharge.Id].OpenAmount.Should().Be(120m);

        commands.Reset();
        var ledger = await service.GetTenantLedgerAsync(scope, account.Id,
            new TenantLedgerQuery { SettledOnly = true, Take = 50, Sort = "effectiveOn" });
        var expectedIds = new[]
        {
            settledCharge.Id, openingBalance.Id, refund.Id, transferIn.Id,
            transferOut.Id, reversal.Id, receipt.Id,
        };
        ledger!.Items.Select(row => row.TenantLedgerEntryId).Should().BeEquivalentTo(expectedIds);
        ledger.Items.Select(row => row.TenantLedgerEntryId).Should().NotContain(openCharge.Id);

        commands.Reset();
        var summaries = await service.GetTenantMonthSummaryAsync(scope, account.Id,
            new TenantMonthSummaryQuery
            {
                From = new DateOnly(2027, 6, 1),
                To = new DateOnly(2027, 6, 30),
                SettledOnly = true,
                Take = 50,
            });
        var monthIds = summaries.SelectMany(summary => summary.Rows)
            .Select(row => row.TenantLedgerEntryId).ToArray();
        monthIds.Should().BeEquivalentTo(expectedIds);
        monthIds.Should().NotContain(openCharge.Id);
        commands.Count.Should().Be(1);
        commands.Sql.Single().Should().Contain("entry.\"Direction\" <> 'Debit'")
            .And.Contain("entry.\"EntryType\" NOT IN")
            .And.Contain("TenantLedgerAllocations")
            .And.Contain("authorized_accounts")
            .And.Contain("entry.\"PortfolioId\" =")
            .And.Contain("entry.\"TenantAccountId\" =");
    }

    [Fact]
    public async Task TargetedCreditEligibility_SharedAuthorityTranslatesBusinessDateCorrectionSumToSql()
    {
        await using var setup = await _fixture.CreateContextAsync();

        var sql = TargetedCreditEligibilityQuery.Build(
                setup.Db.TenantLedgerEntries.AsNoTracking()
                    .Where(entry => entry.PortfolioId == 1
                        && entry.TenantAccountId == 2
                        && entry.Id == 3),
                setup.Db.TenantLedgerEntries.AsNoTracking(),
                setup.Db.TenantAccountBalanceProjections.AsNoTracking())
            .Select(row => row.RemainingTargetableAmount)
            .ToQueryString();

        sql.Should().Contain("TenantLedgerEntries")
            .And.Contain("vw_tenant_account_balances")
            .And.Contain("sum(")
            .And.Contain("ReversesEntryId")
            .And.Contain("RelatedTenantLedgerEntryId")
            .And.MatchRegex("EffectiveOn\\\" <= [^\\n]*BusinessDate");
    }

    [Fact]
    public async Task TenantCreditTargets_UsesAuthorizedServerEligibilityOrderingPagingAndBoundedSql()
    {
        var commands = new SqlCommandCounter();
        await using var setup = await _fixture.CreateContextAsync([commands]);
        var scope = setup.Db.SeedAdministratorScope(
            1, nameof(TenantCreditTargets_UsesAuthorizedServerEligibilityOrderingPagingAndBoundedSql));
        var now = new DateTime(2027, 7, 1, 12, 0, 0, DateTimeKind.Utc);
        var account = await CreateTenantAccountAsync(setup, scope, now, "CREDIT-TARGETS");

        TenantLedgerEntry NewEntry(
            TenantLedgerEntryType entryType,
            decimal amount,
            DateOnly effectiveOn,
            string businessKey) => new()
            {
                PublicId = Guid.NewGuid(),
                PortfolioId = 1,
                TenantAccountId = account.Id,
                EntryType = entryType,
                Direction = TenantLedgerDirection.Debit,
                Amount = amount,
                Currency = "USD",
                EffectiveOn = effectiveOn,
                DueOn = effectiveOn,
                PostedAtUtc = now,
                Description = $"Credit target {entryType}",
                BusinessKey = businessKey,
                CreatedByUserId = scope.UserId,
            };

        var rent = NewEntry(TenantLedgerEntryType.ManualCharge, 100m, new DateOnly(2027, 7, 1), "credit-target:rent");
        var addendum = NewEntry(TenantLedgerEntryType.LateFeeCharge, 200m, new DateOnly(2027, 7, 2), "credit-target:addendum");
        var manual = NewEntry(TenantLedgerEntryType.ManualCharge, 300m, new DateOnly(2027, 7, 3), "credit-target:manual");
        var deposit = NewEntry(TenantLedgerEntryType.DepositCharge, 400m, new DateOnly(2027, 7, 4), "credit-target:deposit");
        var opening = NewEntry(TenantLedgerEntryType.OpeningBalance, 500m, new DateOnly(2027, 7, 5), "credit-target:opening");
        var corrected = NewEntry(TenantLedgerEntryType.LateFeeCharge, 60m, new DateOnly(2027, 7, 6), "credit-target:corrected");
        var partiallyCorrected = NewEntry(TenantLedgerEntryType.ManualCharge, 90m, new DateOnly(2027, 7, 6), "credit-target:partially-corrected");
        setup.Db.TenantLedgerEntries.AddRange(
            rent, addendum, manual, deposit, opening, corrected, partiallyCorrected);
        await setup.Db.SaveChangesAsync();
        setup.Db.TenantLedgerEntries.AddRange(
            new TenantLedgerEntry
            {
                PublicId = Guid.NewGuid(),
                PortfolioId = 1,
                TenantAccountId = account.Id,
                EntryType = TenantLedgerEntryType.Reversal,
                Direction = TenantLedgerDirection.Credit,
                Amount = corrected.Amount,
                Currency = "USD",
                EffectiveOn = new DateOnly(2027, 7, 7),
                PostedAtUtc = now,
                Description = "Corrected late fee",
                BusinessKey = "credit-target:correction",
                ReversesEntryId = corrected.Id,
                CreatedByUserId = scope.UserId,
            },
            new TenantLedgerEntry
            {
                PublicId = Guid.NewGuid(),
                PortfolioId = 1,
                TenantAccountId = account.Id,
                EntryType = TenantLedgerEntryType.Credit,
                Direction = TenantLedgerDirection.Credit,
                Amount = 30m,
                Currency = "USD",
                EffectiveOn = new DateOnly(2027, 7, 7),
                PostedAtUtc = now,
                Description = "Related credit for manual charge",
                BusinessKey = "credit-target:partial-correction",
                RelatedTenantLedgerEntryId = partiallyCorrected.Id,
                CreatedByUserId = scope.UserId,
            });
        await setup.Db.SaveChangesAsync();
        FreezeBusinessClock(setup, new DateTime(2027, 7, 6, 12, 0, 0, DateTimeKind.Utc));
        await setup.ActivateApiScopeAsync(scope);

        var service = new AccountingLedgerReadModelService(setup.Db);
        const string businessDateCorrectionPredicate = "\"EffectiveOn\" <=";
        commands.Reset();
        var beforeBoundary = await service.GetTenantCreditTargetsAsync(scope, account.Id,
            new TenantCreditTargetQuery { Take = 50 });

        beforeBoundary.Should().NotBeNull();
        beforeBoundary!.TotalCount.Should().Be(5);
        beforeBoundary.Items.Should().ContainSingle(row =>
            row.TenantLedgerEntryId == corrected.Id
            && row.RemainingTargetableAmount == 60m);
        beforeBoundary.Items.Should().ContainSingle(row =>
            row.TenantLedgerEntryId == partiallyCorrected.Id
            && row.RemainingTargetableAmount == 90m);
        commands.Count.Should().Be(2);
        commands.Sql.Should().OnlyContain(sql =>
            sql.Contains("TenantAccounts", StringComparison.Ordinal)
            && sql.Contains("LeaseManagements", StringComparison.Ordinal)
            && sql.Contains("TenantLedgerEntries", StringComparison.Ordinal)
            && sql.Contains("TenantAccountId", StringComparison.Ordinal)
            && sql.Contains("PortfolioId", StringComparison.Ordinal)
            && sql.Contains("Direction", StringComparison.Ordinal)
            && sql.Contains("EntryType", StringComparison.Ordinal)
            && sql.Contains("ReversesEntryId", StringComparison.Ordinal)
            && sql.Contains("RelatedTenantLedgerEntryId", StringComparison.Ordinal)
            && sql.Contains("vw_tenant_account_balances", StringComparison.Ordinal)
            && sql.Contains("BusinessDate", StringComparison.Ordinal)
            && sql.Contains(businessDateCorrectionPredicate, StringComparison.Ordinal));
        (commands.Sql[1].Split(businessDateCorrectionPredicate).Length - 1).Should().Be(2);

        FreezeBusinessClock(setup, new DateTime(2027, 7, 7, 12, 0, 0, DateTimeKind.Utc));
        commands.Reset();
        var onBoundary = await service.GetTenantCreditTargetsAsync(scope, account.Id,
            new TenantCreditTargetQuery { Take = 50 });

        onBoundary.Should().NotBeNull();
        onBoundary!.TotalCount.Should().Be(4);
        onBoundary.Items.Should().NotContain(row => row.TenantLedgerEntryId == corrected.Id);
        onBoundary.Items.Should().ContainSingle(row =>
            row.TenantLedgerEntryId == partiallyCorrected.Id
            && row.RemainingTargetableAmount == 60m);
        commands.Count.Should().Be(2);
        commands.Sql.Should().OnlyContain(sql =>
            sql.Contains("vw_tenant_account_balances", StringComparison.Ordinal)
            && sql.Contains("BusinessDate", StringComparison.Ordinal)
            && sql.Contains(businessDateCorrectionPredicate, StringComparison.Ordinal));
        (commands.Sql[1].Split(businessDateCorrectionPredicate).Length - 1).Should().Be(2);
        commands.Sql.Should().Contain(sql =>
            sql.Contains("ORDER BY", StringComparison.OrdinalIgnoreCase)
            && sql.Contains("OFFSET", StringComparison.OrdinalIgnoreCase)
            && sql.Contains("LIMIT", StringComparison.OrdinalIgnoreCase));

        commands.Reset();
        var preselected = await service.GetTenantCreditTargetsAsync(scope, account.Id,
            new TenantCreditTargetQuery { TargetEntryId = rent.Id, Take = 1 });
        preselected!.Items.Should().ContainSingle().Which.TenantLedgerEntryId.Should().Be(rent.Id);
    }

    [Fact]
    public async Task Transactions_PageKeysPrecedeHydrationAndStayWithinThreeStatements()
    {
        var commands = new SqlCommandCounter();
        await using var setup = await _fixture.CreateContextAsync([commands]);
        var scope = setup.Db.SeedAdministratorScope(
            1, nameof(Transactions_PageKeysPrecedeHydrationAndStayWithinThreeStatements));
        var now = DateTime.UtcNow;
        var property = new Property
        {
            PortfolioId = 1,
            Name = "Transaction plan property",
            AddressLine1 = "818 Plan Way",
            City = "Columbus",
            State = "OH",
            PostalCode = "43215",
            CreatedAt = now,
            UpdatedAt = now,
        };
        var expense = new Expense
        {
            PortfolioId = 1,
            OperationalScope = ExpenseOperationalScope.Property,
            Property = property,
            Category = ScheduleECategory.Repairs,
            Description = "Page-keyed repair",
            Status = ExpenseStatus.Paid,
            Amount = 818m,
            IncurredAt = now,
            CreatedAt = now,
            UpdatedAt = now,
        };
        setup.Db.AddRange(property, expense);
        await setup.Db.SaveChangesAsync();
        await setup.ActivateApiScopeAsync(scope);
        commands.Reset();

        var service = new AccountingService(
            setup.Db,
            new ScheduleEService(setup.Db),
            new YearEndPacketPdfGenerator(),
            TimeProvider.System);
        var result = await service.GetTransactionsAsync(
            scope,
            new AccountingTransactionsQuery
            {
                Kind = "Expense",
                PropertyId = property.Id,
                Take = 20,
            });

        result.Items.Should().ContainSingle().Which.Id.Should().Be(expense.Id);
        commands.Count.Should().Be(3);
        var countSql = commands.Sql[0];
        var pageSql = commands.Sql[1];
        var hydrationSql = commands.Sql[2];

        countSql.Should()
            .Contain("authorized_properties AS MATERIALIZED")
            .And.Contain("transaction_seed AS")
            .And.NotContain("vw_lease_management_lifecycle")
            .And.NotContain("\"Vendors\"")
            .And.NotContain("\"WorkOrders\"")
            .And.NotContain("\"StoredFiles\"")
            .And.NotContain("\"BankTransactions\"");
        pageSql.Should()
            .Contain("page_seed AS MATERIALIZED")
            .And.Contain("OFFSET @skip")
            .And.Contain("LIMIT @take")
            .And.Contain("row_number() OVER");
        pageSql.IndexOf("OFFSET @skip", StringComparison.Ordinal).Should().BeLessThan(
            pageSql.IndexOf("row_number() OVER", StringComparison.Ordinal),
            "the slim union must be paged before an ordinal is assigned to its keys");

        hydrationSql.Should()
            .Contain("page_expenses AS MATERIALIZED")
            .And.Contain("expense.\"Id\" = ANY(@expenseIds::integer[])")
            .And.Contain("file.\"EntityId\" = ANY(@expenseIds::bigint[])")
            .And.Contain("bank.\"MatchedExpenseId\" = ANY(@expenseIds::integer[])")
            .And.NotContain("page_ledger_entries AS MATERIALIZED")
            .And.NotContain("page_application_entries AS MATERIALIZED");
        hydrationSql.IndexOf(
            "expense.\"Id\" = ANY(@expenseIds::integer[])",
            StringComparison.Ordinal).Should().BeLessThan(
                hydrationSql.IndexOf("INNER JOIN authorized_properties", StringComparison.Ordinal),
                "the expense page-key predicate must execute before authorization and display joins");
    }

    [Fact]
    public async Task ChartJournalAndFinancialStatements_StayPortfolioScopedAndBalanced()
    {
        await using var setup = await _fixture.CreateContextAsync();
        var seed = new ChartOfAccountsSeedService(setup.Db);
        await seed.SeedAsync(1);
        await setup.Db.SaveChangesAsync();
        setup.Db.ChangeTracker.Clear();

        var scope = setup.Db.SeedAdministratorScope(1, nameof(ChartJournalAndFinancialStatements_StayPortfolioScopedAndBalanced));
        var accounts = await setup.Db.LedgerAccounts
            .Where(account => account.PortfolioId == 1 && (account.Code == "1000" || account.Code == "4000"))
            .OrderBy(account => account.Code)
            .ToArrayAsync();
        var entry = await new AccountingPostingService(setup.Db).PostAsync(new AccountingProposedEntry
        {
            PortfolioId = 1,
            EffectiveOn = new DateOnly(2026, 8, 1),
            Currency = "USD",
            Description = "Statement read model test",
            SourceType = JournalSourceType.TenantCharge,
            SourceId = 8102,
            SourceBusinessKey = "read-model:8102",
            PostingRuleVersion = 1,
            AttemptId = Guid.NewGuid(),
            AtomicReceiptId = Guid.NewGuid(),
            UserId = 1,
            Lines =
            [
                new AccountingProposedLine { LedgerAccountId = accounts[0].Id, DebitAmount = 250m },
                new AccountingProposedLine { LedgerAccountId = accounts[1].Id, CreditAmount = 250m },
            ],
        });
        await setup.Db.SaveChangesAsync();
        setup.Db.ChangeTracker.Clear();
        await setup.ActivateApiScopeAsync(scope);

        var service = new AccountingLedgerReadModelService(setup.Db);
        var chart = await service.GetChartOfAccountsAsync(1, new ChartOfAccountsQuery { Take = 200 });
        var detail = await service.GetJournalDetailAsync(scope, entry.PublicId);
        var trialBalance = await service.GetTrialBalanceAsync(scope, new StatementQuery
        {
            From = new DateOnly(2026, 8, 1),
            To = new DateOnly(2026, 8, 31),
            Currency = "USD",
        });
        var balanceSheet = await service.GetBalanceSheetAsync(scope, new StatementQuery
        {
            From = new DateOnly(2026, 8, 1),
            To = new DateOnly(2026, 8, 31),
            Currency = "USD",
        });
        var incomeStatement = await service.GetIncomeStatementAsync(scope, new StatementQuery
        {
            From = new DateOnly(2026, 8, 1),
            To = new DateOnly(2026, 8, 31),
            Currency = "USD",
        });

        chart.Items.Should().Contain(account => account.Code == "1000" && account.HasPostedLines);
        chart.Items.Should().Contain(account => account.Code == "4000" && account.HasPostedLines);
        detail.Should().NotBeNull();
        detail!.TotalDebits.Should().Be(250m);
        detail.TotalCredits.Should().Be(250m);
        detail.IsBalanced.Should().BeTrue();
        detail.Lines.Should().Contain(line =>
            line.AccountCode == "1000" && line.NormalBalance == NormalBalance.Debit);
        detail.Lines.Should().Contain(line =>
            line.AccountCode == "4000" && line.NormalBalance == NormalBalance.Credit);
        trialBalance.IsBalanced.Should().BeTrue();
        trialBalance.TotalDebits.Should().Be(250m);
        trialBalance.TotalCredits.Should().Be(250m);
        balanceSheet.Sections.Should().ContainSingle(section => section.Label == nameof(AccountType.Asset));
        incomeStatement.Totals.NetIncome.Should().Be(250m);
    }

    [Fact]
    public async Task GeneralLedger_CreditNormalRunningBalanceIsChronologicalBeforeNewestFirstPaging()
    {
        await using var setup = await _fixture.CreateContextAsync();
        await new ChartOfAccountsSeedService(setup.Db).SeedAsync(1);
        await setup.Db.SaveChangesAsync();
        var scope = setup.Db.SeedAdministratorScope(1, nameof(GeneralLedger_CreditNormalRunningBalanceIsChronologicalBeforeNewestFirstPaging));
        var cash = await AccountAsync(setup, "operating-cash");
        var income = await AccountAsync(setup, "rental-income");

        await PostAsync(setup, 8201, new DateOnly(2026, 8, 1),
            new AccountingProposedLine { LedgerAccountId = cash.Id, DebitAmount = 100m },
            new AccountingProposedLine { LedgerAccountId = income.Id, CreditAmount = 100m });
        await PostAsync(setup, 8202, new DateOnly(2026, 8, 2),
            new AccountingProposedLine { LedgerAccountId = income.Id, DebitAmount = 25m },
            new AccountingProposedLine { LedgerAccountId = cash.Id, CreditAmount = 25m });
        await setup.ActivateApiScopeAsync(scope);

        var page = await new AccountingLedgerReadModelService(setup.Db).GetGeneralLedgerAsync(
            scope,
            new GeneralLedgerQuery { AccountId = income.Id, Sort = "-effectiveOn", Take = 20 });

        page.Items.Select(row => row.EffectiveOn).Should().BeInDescendingOrder();
        page.Items.Select(row => row.RunningBalance).Should().Equal(75m, 100m);
        page.Items.Should().OnlyContain(row =>
            row.AccountType == AccountType.Income && row.NormalBalance == NormalBalance.Credit);
    }

    [Fact]
    public async Task TrialBalance_NetsEndingBalancesAndBalanceSheetIncludesCurrentEarnings()
    {
        var commands = new SqlCommandCounter();
        await using var setup = await _fixture.CreateContextAsync([commands]);
        await new ChartOfAccountsSeedService(setup.Db).SeedAsync(1);
        await setup.Db.SaveChangesAsync();
        var scope = setup.Db.SeedAdministratorScope(1, nameof(TrialBalance_NetsEndingBalancesAndBalanceSheetIncludesCurrentEarnings));
        var cash = await AccountAsync(setup, "operating-cash");
        var income = await AccountAsync(setup, "rental-income");
        var expense = await AccountAsync(setup, "repairs-and-maintenance");

        await PostAsync(setup, 8211, new DateOnly(2026, 8, 1),
            new AccountingProposedLine { LedgerAccountId = cash.Id, DebitAmount = 100m },
            new AccountingProposedLine { LedgerAccountId = income.Id, CreditAmount = 100m });
        await PostAsync(setup, 8212, new DateOnly(2026, 8, 2),
            new AccountingProposedLine { LedgerAccountId = expense.Id, DebitAmount = 40m },
            new AccountingProposedLine { LedgerAccountId = cash.Id, CreditAmount = 40m });
        await setup.ActivateApiScopeAsync(scope);

        var service = new AccountingLedgerReadModelService(setup.Db);
        var query = new StatementQuery { To = new DateOnly(2026, 8, 31), Currency = "USD" };
        commands.Reset();
        var trial = await service.GetTrialBalanceAsync(scope, query);
        commands.Count.Should().Be(1);
        commands.Sql.Single().ToUpperInvariant().Should().Contain("COUNT");
        var balanceSheet = await service.GetBalanceSheetAsync(scope, query);

        trial.Rows.Single(row => row.AccountId == cash.Id).Should().BeEquivalentTo(
            new { DebitBalance = 60m, CreditBalance = 0m });
        trial.TotalDebits.Should().Be(100m);
        trial.TotalCredits.Should().Be(100m);
        trial.IsBalanced.Should().BeTrue();
        trial.Rows.Single(row => row.AccountId == cash.Id).IsZeroBalance.Should().BeFalse();
        trial.Rows.Should().Contain(row => row.IsZeroBalance);
        trial.ZeroBalanceCount.Should().BeGreaterThan(0);
        balanceSheet.Totals.Assets.Should().Be(60m);
        balanceSheet.Totals.CurrentEarnings.Should().Be(60m);
        balanceSheet.Totals.LiabilitiesAndEquity.Should().Be(60m);
        balanceSheet.Totals.IsBalanced.Should().BeTrue();
    }

    [Fact]
    public async Task MoneyPosition_ReconcilesProtectedAccountsAndNetsCashTransfersPerJournal()
    {
        await using var setup = await _fixture.CreateContextAsync();
        await new ChartOfAccountsSeedService(setup.Db).SeedAsync(1);
        await setup.Db.SaveChangesAsync();
        var scope = setup.Db.SeedAdministratorScope(1, nameof(MoneyPosition_ReconcilesProtectedAccountsAndNetsCashTransfersPerJournal));
        var cash = await AccountAsync(setup, "operating-cash");
        var trustCash = await AccountAsync(setup, "security-deposit-trust-cash");
        var receivable = await AccountAsync(setup, "tenant-accounts-receivable");
        var deposits = await AccountAsync(setup, "tenant-security-deposits-payable");
        var income = await AccountAsync(setup, "rental-income");
        var expense = await AccountAsync(setup, "repairs-and-maintenance");

        await PostAsync(setup, 8221, new DateOnly(2026, 8, 1),
            new AccountingProposedLine { LedgerAccountId = receivable.Id, DebitAmount = 100m },
            new AccountingProposedLine { LedgerAccountId = income.Id, CreditAmount = 100m });
        await PostAsync(setup, 8222, new DateOnly(2026, 8, 2),
            new AccountingProposedLine { LedgerAccountId = cash.Id, DebitAmount = 100m },
            new AccountingProposedLine { LedgerAccountId = receivable.Id, CreditAmount = 100m });
        await PostAsync(setup, 8223, new DateOnly(2026, 8, 3),
            new AccountingProposedLine { LedgerAccountId = trustCash.Id, DebitAmount = 30m },
            new AccountingProposedLine { LedgerAccountId = deposits.Id, CreditAmount = 30m });
        await PostAsync(setup, 8224, new DateOnly(2026, 8, 4),
            new AccountingProposedLine { LedgerAccountId = expense.Id, DebitAmount = 20m },
            new AccountingProposedLine { LedgerAccountId = cash.Id, CreditAmount = 20m });
        await PostAsync(setup, 8225, new DateOnly(2026, 8, 5),
            new AccountingProposedLine { LedgerAccountId = trustCash.Id, DebitAmount = 10m },
            new AccountingProposedLine { LedgerAccountId = cash.Id, CreditAmount = 10m });
        await setup.ActivateApiScopeAsync(scope);

        var result = await new AccountingLedgerReadModelService(setup.Db).GetMoneyPositionAsync(
            scope,
            new MoneyPositionQuery { From = new DateOnly(2026, 8, 1), To = new DateOnly(2026, 8, 31) });

        result.TotalCashOnHand.Should().Be(110m);
        result.TenantDepositsHeld.Should().Be(30m);
        result.CashAfterTenantDeposits.Should().Be(80m);
        result.RentStillOwed.Should().Be(0m);
        result.BookEquity.Should().Be(80m);
        result.CashReceived.Should().Be(130m);
        result.CashPaid.Should().Be(20m);
        result.NetCashMovement.Should().Be(110m);
        result.ProfitOrLoss.Should().Be(80m);
    }

    [Fact]
    public async Task MoneyPosition_IncludesAuthorizedOperationalDepositLiabilityInOneSqlAggregate()
    {
        var commands = new SqlCommandCounter();
        await using var setup = await _fixture.CreateContextAsync([commands]);
        var scope = setup.Db.SeedAdministratorScope(
            1, nameof(MoneyPosition_IncludesAuthorizedOperationalDepositLiabilityInOneSqlAggregate));
        var now = DateTime.UtcNow;
        var property = new Property
        {
            PortfolioId = 1, Name = "Deposit bridge", AddressLine1 = "1 Deposit Way",
            City = "Columbus", State = "OH", PostalCode = "43215", CreatedAt = now, UpdatedAt = now,
        };
        var unit = new Unit
        {
            PortfolioId = 1, Property = property, UnitNumber = "1", MarketRent = 1_050m,
            CreatedAt = now, UpdatedAt = now,
        };
        var relationship = new LeaseManagement
        {
            PortfolioId = 1, Property = property, Unit = unit, RelationshipNumber = "DEPOSIT-BRIDGE",
            PossessionGivenAtUtc = now, CreatedAtUtc = now, UpdatedAtUtc = now,
            CreatedByUserId = scope.UserId, RowVersion = Guid.NewGuid(),
        };
        var tenantAccount = new TenantAccount
        {
            PortfolioId = 1, LeaseManagement = relationship, AccountNumber = "DEPOSIT-BRIDGE",
            Currency = "USD", OpenedAtUtc = now, CreatedAtUtc = now, CreatedByUserId = scope.UserId,
        };
        var source = LegalDocumentSourceVersionTestData.BuiltIn(
            1, scope.UserId, now, "money-position-deposit-bridge");
        var agreement = new LeaseAgreement
        {
            PublicId = Guid.NewGuid(), PortfolioId = 1, LeaseManagement = relationship,
            VersionNumber = 1, AgreementNumber = "DEPOSIT-BRIDGE", ChangeType = LeaseAgreementChangeType.Initial,
            TermType = LeaseAgreementTermType.FixedTerm, TermStartOn = DateOnly.FromDateTime(now.AddDays(-30)),
            TermEndOn = DateOnly.FromDateTime(now.AddYears(1)), GoverningFromOn = DateOnly.FromDateTime(now.AddDays(-30)),
            BaseRentAmount = 1_050m, RentDueDay = 1, SecurityDepositObligation = 1_050m,
            Currency = "USD", TermsSchemaVersion = 1, TermsPayload = "{}", DocumentSourceVersion = source,
            CreatedAtUtc = now, UpdatedAtUtc = now, CreatedByUserId = scope.UserId,
        };
        var depositAccount = new SecurityDepositAccount
        {
            PortfolioId = 1, TenantAccount = tenantAccount, OriginatingAgreement = agreement,
            Currency = "USD", CreatedAtUtc = now, CreatedByUserId = scope.UserId,
        };
        setup.Db.AddRange(property, unit, relationship, tenantAccount, source, agreement, depositAccount);
        await setup.Db.SaveChangesAsync();
        setup.Db.SecurityDepositEntries.Add(new SecurityDepositEntry
        {
            PortfolioId = 1, SecurityDepositAccountId = depositAccount.Id,
            EntryType = SecurityDepositEntryType.Receipt, Direction = SecurityDepositDirection.Increase,
            Amount = 1_050m, Currency = "USD", EffectiveOn = DateOnly.FromDateTime(now.AddDays(-1)),
            PostedAtUtc = now, BusinessKey = "deposit-bridge:receipt", Description = "Deposit received",
            LeaseAgreementId = agreement.Id, CreatedByUserId = scope.UserId,
        });
        await setup.Db.SaveChangesAsync();
        await setup.ActivateApiScopeAsync(scope);
        commands.Reset();

        var result = await new AccountingLedgerReadModelService(setup.Db).GetMoneyPositionAsync(
            scope, new MoneyPositionQuery { To = DateOnly.FromDateTime(now) });

        result.TenantDepositsHeld.Should().Be(1_050m);
        commands.Sql.Should().ContainSingle(sql =>
            sql.Contains("vw_security_deposit_balances", StringComparison.Ordinal)
            && sql.Contains("SUM", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task StartupSweep_BackfillsOperationalReceivableAndDepositOnce_AndMoneyPositionMatches()
    {
        await using var setup = await _fixture.CreateContextAsync();
        var seed = new ChartOfAccountsSeedService(setup.Db);
        await seed.SeedAllWithLockAsync();
        var scope = setup.Db.SeedAdministratorScope(
            1, nameof(StartupSweep_BackfillsOperationalReceivableAndDepositOnce_AndMoneyPositionMatches));
        var now = DateTime.UtcNow;
        var property = new Property
        {
            PortfolioId = 1, Name = "Opening journal property", AddressLine1 = "1 Opening Way",
            City = "Columbus", State = "OH", PostalCode = "43215", CreatedAt = now, UpdatedAt = now,
        };
        var unit = new Unit
        {
            PortfolioId = 1, Property = property, UnitNumber = "1", MarketRent = 3_500m,
            CreatedAt = now, UpdatedAt = now,
        };
        var relationship = new LeaseManagement
        {
            PortfolioId = 1, Property = property, Unit = unit, RelationshipNumber = "OPENING-JOURNAL",
            PossessionGivenAtUtc = now, CreatedAtUtc = now, UpdatedAtUtc = now,
            CreatedByUserId = scope.UserId, RowVersion = Guid.NewGuid(),
        };
        var tenantAccount = new TenantAccount
        {
            PortfolioId = 1, LeaseManagement = relationship, AccountNumber = "OPENING-JOURNAL",
            Currency = "USD", OpenedAtUtc = now, CreatedAtUtc = now, CreatedByUserId = scope.UserId,
        };
        var source = LegalDocumentSourceVersionTestData.BuiltIn(
            1, scope.UserId, now, "opening-journal-backfill");
        var agreement = new LeaseAgreement
        {
            PublicId = Guid.NewGuid(), PortfolioId = 1, LeaseManagement = relationship,
            VersionNumber = 1, AgreementNumber = "OPENING-JOURNAL", ChangeType = LeaseAgreementChangeType.Initial,
            TermType = LeaseAgreementTermType.FixedTerm, TermStartOn = DateOnly.FromDateTime(now.AddMonths(-1)),
            TermEndOn = DateOnly.FromDateTime(now.AddYears(1)), GoverningFromOn = DateOnly.FromDateTime(now.AddMonths(-1)),
            BaseRentAmount = 3_500m, RentDueDay = 1, SecurityDepositObligation = 900m,
            Currency = "USD", TermsSchemaVersion = 1, TermsPayload = "{}", DocumentSourceVersion = source,
            CreatedAtUtc = now, UpdatedAtUtc = now, CreatedByUserId = scope.UserId,
        };
        var depositAccount = new SecurityDepositAccount
        {
            PortfolioId = 1, TenantAccount = tenantAccount, OriginatingAgreement = agreement,
            Currency = "USD", CreatedAtUtc = now, CreatedByUserId = scope.UserId,
        };
        setup.Db.AddRange(property, unit, relationship, tenantAccount, source, agreement, depositAccount);
        await setup.Db.SaveChangesAsync();
        setup.Db.TenantLedgerEntries.AddRange(
            new TenantLedgerEntry
            {
                PublicId = Guid.NewGuid(), PortfolioId = 1, TenantAccountId = tenantAccount.Id,
                EntryType = TenantLedgerEntryType.ManualCharge, Direction = TenantLedgerDirection.Debit,
                Amount = 3_500m, Currency = "USD", EffectiveOn = DateOnly.FromDateTime(now.AddDays(-2)),
                DueOn = DateOnly.FromDateTime(now.AddDays(-2)),
                PostedAtUtc = now, Description = "Historical rent", BusinessKey = "opening-journal:charge",
                CreatedByUserId = scope.UserId,
            },
            new TenantLedgerEntry
            {
                PublicId = Guid.NewGuid(), PortfolioId = 1, TenantAccountId = tenantAccount.Id,
                EntryType = TenantLedgerEntryType.PaymentReceipt, Direction = TenantLedgerDirection.Credit,
                Amount = 215.52m, Currency = "USD", EffectiveOn = DateOnly.FromDateTime(now.AddDays(-1)),
                PostedAtUtc = now, Description = "Historical payment", BusinessKey = "opening-journal:payment",
                CreatedByUserId = scope.UserId,
            });
        setup.Db.SecurityDepositEntries.Add(new SecurityDepositEntry
        {
            PublicId = Guid.NewGuid(), PortfolioId = 1, SecurityDepositAccountId = depositAccount.Id,
            EntryType = SecurityDepositEntryType.Receipt, Direction = SecurityDepositDirection.Increase,
            Amount = 900m, Currency = "USD", EffectiveOn = DateOnly.FromDateTime(now.AddDays(-1)),
            PostedAtUtc = now, BusinessKey = "opening-journal:deposit", Description = "Historical deposit",
            LeaseAgreementId = agreement.Id, CreatedByUserId = scope.UserId,
        });
        await setup.Db.SaveChangesAsync();

        (await seed.SeedAllWithLockAsync()).Should().Be(0, "the chart was already present");
        (await seed.SeedAllWithLockAsync()).Should().Be(0, "the opening pass is idempotent");

        var openings = await setup.Db.JournalEntries.AsNoTracking()
            .Include(entry => entry.Lines).ThenInclude(line => line.LedgerAccount)
            .Where(entry => entry.PortfolioId == 1
                && entry.SourceType == JournalSourceType.OpeningBalance
                && entry.SourceBusinessKey.StartsWith("opening-balance:portfolio:1:"))
            .ToListAsync();
        openings.Should().HaveCount(2);
        openings.Should().OnlyContain(entry => entry.Lines.Count == 2);
        openings.SelectMany(entry => entry.Lines)
            .Where(line => line.LedgerAccount!.SystemKey == "tenant-accounts-receivable")
            .Should().ContainSingle().Which.DebitAmount.Should().Be(3_284.48m);
        openings.SelectMany(entry => entry.Lines)
            .Where(line => line.LedgerAccount!.SystemKey == "tenant-security-deposits-payable")
            .Should().ContainSingle().Which.CreditAmount.Should().Be(900m);

        await setup.ActivateApiScopeAsync(scope);
        var position = await new AccountingLedgerReadModelService(setup.Db).GetMoneyPositionAsync(
            scope, new MoneyPositionQuery { To = DateOnly.FromDateTime(now) });
        position.RentStillOwed.Should().Be(3_284.48m);
        position.TenantDepositsHeld.Should().Be(900m);
    }

    [Fact]
    public async Task SourceJournalsAndJournalDetail_ReturnAuthorizedSourceDocumentLinks()
    {
        await using var setup = await _fixture.CreateContextAsync();
        await new ChartOfAccountsSeedService(setup.Db).SeedAsync(1);
        await setup.Db.SaveChangesAsync();
        var scope = setup.Db.SeedAdministratorScope(1, nameof(SourceJournalsAndJournalDetail_ReturnAuthorizedSourceDocumentLinks));
        var cash = await AccountAsync(setup, "operating-cash");
        var income = await AccountAsync(setup, "rental-income");
        var sourceId = 8_401L;
        var journal = await new AccountingPostingService(setup.Db).PostAsync(new AccountingProposedEntry
        {
            PortfolioId = 1,
            EffectiveOn = new DateOnly(2026, 8, 10),
            Currency = "USD",
            Description = "Source document journal",
            SourceType = JournalSourceType.TenantCharge,
            SourceId = sourceId,
            SourceBusinessKey = $"tenant-charge:{sourceId}",
            PostingRuleVersion = 1,
            AttemptId = Guid.NewGuid(),
            AtomicReceiptId = Guid.NewGuid(),
            UserId = 1,
            Lines =
            [
                new AccountingProposedLine { LedgerAccountId = cash.Id, DebitAmount = 45m },
                new AccountingProposedLine { LedgerAccountId = income.Id, CreditAmount = 45m },
            ],
        });
        var file = new StoredFile
        {
            PortfolioId = 1,
            FileName = "charge.pdf",
            FilePath = "test/charge.pdf",
            ContentType = "application/pdf",
            FileSize = 100,
            EntityType = nameof(TenantLedgerEntry),
            EntityId = sourceId,
            UploadedAt = DateTime.UtcNow,
        };
        setup.Db.StoredFiles.Add(file);
        await setup.Db.SaveChangesAsync();
        setup.Db.ChangeTracker.Clear();
        await setup.ActivateApiScopeAsync(scope);

        var service = new AccountingLedgerReadModelService(setup.Db);
        var sources = await service.GetSourceJournalsAsync(scope, new SourceJournalQuery
        {
            SourceType = JournalSourceType.TenantCharge,
            SourceId = sourceId,
        });
        var detail = await service.GetJournalDetailAsync(scope, journal.PublicId);

        sources.Should().ContainSingle().Which.Should().BeEquivalentTo(new
        {
            journal.PublicId,
            TotalDebits = 45m,
            TotalCredits = 45m,
            IsReversal = false,
        });
        sources.Single().Lines.Should().HaveCount(2);
        sources.Single().Lines.Should().OnlyContain(line => line.DebitAmount > 0m || line.CreditAmount > 0m);
        detail.Should().NotBeNull();
        detail!.DocumentIds.Should().Equal(file.Id);
    }

    private static async Task<TenantAccount> CreateTenantAccountAsync(
        MigratedPostgreSqlTestContext setup,
        WorkspaceReadScope scope,
        DateTime now,
        string accountNumber)
    {
        var property = new Property
        {
            PortfolioId = 1,
            Name = $"{accountNumber} property",
            AddressLine1 = "1 Read Model Way",
            City = "Columbus",
            State = "OH",
            PostalCode = "43215",
            CreatedAt = now,
            UpdatedAt = now,
        };
        var unit = new Unit
        {
            PortfolioId = 1,
            Property = property,
            UnitNumber = "1A",
            MarketRent = 1_000m,
            CreatedAt = now,
            UpdatedAt = now,
        };
        var relationship = new LeaseManagement
        {
            PortfolioId = 1,
            Property = property,
            Unit = unit,
            RelationshipNumber = accountNumber,
            PossessionGivenAtUtc = now,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
            CreatedByUserId = scope.UserId,
            RowVersion = Guid.NewGuid(),
        };
        var account = new TenantAccount
        {
            PortfolioId = 1,
            LeaseManagement = relationship,
            AccountNumber = accountNumber,
            Currency = "USD",
            OpenedAtUtc = now,
            CreatedAtUtc = now,
            CreatedByUserId = scope.UserId,
        };
        setup.Db.AddRange(property, unit, relationship, account);
        await setup.Db.SaveChangesAsync();
        return account;
    }

    private static void FreezeBusinessClock(
        MigratedPostgreSqlTestContext setup,
        DateTime businessNowUtc)
    {
        var clock = setup.Db.SimulationClocks.SingleOrDefault(row => row.Id == 1);
        if (clock is null)
        {
            setup.Db.SimulationClocks.Add(new SimulationClock { Id = 1 });
            clock = setup.Db.SimulationClocks.Local.Single(row => row.Id == 1);
        }

        clock.Mode = ClockMode.Frozen;
        clock.SimAnchorUtc = businessNowUtc;
        clock.RealAnchorUtc = businessNowUtc;
        clock.TimeZoneId = "UTC";
        clock.UpdatedAtRealUtc = businessNowUtc;
        setup.Db.SaveChanges();
        setup.Db.ChangeTracker.Clear();
    }

    private static async Task<LedgerAccount> AccountAsync(
        MigratedPostgreSqlTestContext setup,
        string systemKey) => await setup.Db.LedgerAccounts.SingleAsync(account =>
            account.PortfolioId == 1 && account.SystemKey == systemKey);

    private static async Task PostAsync(
        MigratedPostgreSqlTestContext setup,
        long sourceId,
        DateOnly effectiveOn,
        params AccountingProposedLine[] lines)
    {
        await new AccountingPostingService(setup.Db).PostAsync(new AccountingProposedEntry
        {
            PortfolioId = 1,
            EffectiveOn = effectiveOn,
            Currency = "USD",
            Description = $"Read model {sourceId}",
            SourceType = JournalSourceType.OpeningBalance,
            SourceId = sourceId,
            SourceBusinessKey = $"read-model:{sourceId}",
            PostingRuleVersion = 1,
            AttemptId = Guid.NewGuid(),
            AtomicReceiptId = Guid.NewGuid(),
            UserId = 1,
            Lines = lines,
        });
        await setup.Db.SaveChangesAsync();
        setup.Db.ChangeTracker.Clear();
    }

    private sealed class SqlCommandCounter : DbCommandInterceptor
    {
        private readonly object _gate = new();
        private readonly List<string> _sql = [];

        public int Count
        {
            get { lock (_gate) return _sql.Count; }
        }

        public IReadOnlyList<string> Sql
        {
            get { lock (_gate) return _sql.ToArray(); }
        }

        public void Reset()
        {
            lock (_gate) _sql.Clear();
        }

        public override InterceptionResult<DbDataReader> ReaderExecuting(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
        {
            Add(command.CommandText);
            return base.ReaderExecuting(command, eventData, result);
        }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData,
            InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            Add(command.CommandText);
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }

        public override InterceptionResult<object> ScalarExecuting(
            DbCommand command, CommandEventData eventData, InterceptionResult<object> result)
        {
            Add(command.CommandText);
            return base.ScalarExecuting(command, eventData, result);
        }

        public override ValueTask<InterceptionResult<object>> ScalarExecutingAsync(
            DbCommand command, CommandEventData eventData,
            InterceptionResult<object> result, CancellationToken cancellationToken = default)
        {
            Add(command.CommandText);
            return base.ScalarExecutingAsync(command, eventData, result, cancellationToken);
        }

        private void Add(string sql)
        {
            lock (_gate) _sql.Add(sql);
        }
    }
}
