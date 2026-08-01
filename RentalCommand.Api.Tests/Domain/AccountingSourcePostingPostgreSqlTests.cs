using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Moq;
using RentalCommand.Core.Atomic;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Data;
using RentalCommand.Data.Accounting;
using RentalCommand.TestCommon;

namespace RentalCommand.Api.Tests.Domain;

[Collection(MigratedPostgreSqlCollection.Name)]
public sealed class AccountingSourcePostingPostgreSqlTests
{
    private readonly MigratedPostgreSqlFixture _fixture;

    public AccountingSourcePostingPostgreSqlTests(MigratedPostgreSqlFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task PaidExpensePostsTheMappedExpenseAndCashLines()
    {
        await using var setup = await _fixture.CreateContextAsync();
        await SeedChartAsync(setup.Db);
        var expense = new Expense
        {
            PortfolioId = 1,
            Category = ScheduleECategory.Repairs,
            Description = "Replace leaking valve",
            Status = ExpenseStatus.Paid,
            Amount = 125m,
            IncurredAt = new DateTime(2027, 1, 10, 0, 0, 0, DateTimeKind.Utc),
            PaidAt = new DateTime(2027, 1, 11, 0, 0, 0, DateTimeKind.Utc),
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        setup.Db.Expenses.Add(expense);
        await setup.Db.SaveChangesAsync();

        await MoneyAccountingPosting.PostExpenseOccurrenceAsync(
            setup.Db, AtomicContext(), expense, 1);
        await setup.Db.SaveChangesAsync();

        var lines = await LinesAsync(setup.Db, JournalSourceType.ExpensePayment, expense.Id);
        lines.Should().Equal(
            ("repairs-and-maintenance", 125m, 0m),
            ("operating-cash", 0m, 125m));
    }

    [Fact]
    public async Task BankMatchDoesNotPostAnotherBillPaymentWhenTheBillWasAlreadyPaid()
    {
        await using var setup = await _fixture.CreateContextAsync();
        await SeedChartAsync(setup.Db);
        var expense = new Expense
        {
            PortfolioId = 1,
            Category = ScheduleECategory.Repairs,
            Description = "Already paid bill",
            Status = ExpenseStatus.Pending,
            Amount = 125m,
            IncurredAt = new DateTime(2027, 1, 12, 0, 0, 0, DateTimeKind.Utc),
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        var connection = new BankConnection
        {
            PortfolioId = 1,
            Provider = "Manual",
            InstitutionName = "Expense bank",
            AccountName = "Checking",
            Status = "Active",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        setup.Db.Add(expense);
        await setup.Db.SaveChangesAsync();
        await MoneyAccountingPosting.PostExpenseOccurrenceAsync(
            setup.Db, AtomicContext(), expense, 1);
        await setup.Db.SaveChangesAsync();

        var originalPayment = await MoneyAccountingPosting.PostBillPaymentAsync(
            setup.Db, AtomicContext(), expense, expense.Id, $"bill-payment:expense:{expense.Id}", 1);
        await setup.Db.SaveChangesAsync();
        setup.Db.Add(new BankTransaction
        {
            PortfolioId = 1,
            BankConnection = connection,
            ProviderTransactionId = "already-paid-bill-sequence-row",
            PostedAt = new DateTime(2027, 1, 13, 0, 0, 0, DateTimeKind.Utc),
            Description = "Sequence row",
            Amount = -1m,
            IsoCurrencyCode = "USD",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        await setup.Db.SaveChangesAsync();
        var transaction = new BankTransaction
        {
            PortfolioId = 1,
            BankConnection = connection,
            ProviderTransactionId = "already-paid-bill",
            PostedAt = new DateTime(2027, 1, 13, 0, 0, 0, DateTimeKind.Utc),
            Description = "Matched bill payment",
            Amount = -125m,
            IsoCurrencyCode = "USD",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        setup.Db.Add(transaction);
        await setup.Db.SaveChangesAsync();

        var matched = await MoneyAccountingPosting.PostBankMatchedExpenseAsync(
            setup.Db, AtomicContext(), expense, transaction, 1);

        matched.Id.Should().Be(originalPayment.Id);
        await setup.Db.SaveChangesAsync();
        (await setup.Db.JournalEntries.CountAsync(entry =>
            entry.PortfolioId == 1 && entry.SourceType == JournalSourceType.BillPayment
            && entry.Lines.Any(line => line.SourceLineId == expense.Id))).Should().Be(1);
    }

    [Fact]
    public async Task BankMatchOfPaidExpenseKeepsOneCashCredit()
    {
        await using var setup = await _fixture.CreateContextAsync();
        await SeedChartAsync(setup.Db);
        var expense = new Expense
        {
            PortfolioId = 1,
            Category = ScheduleECategory.Repairs,
            Description = "Paid expense matched by bank",
            Status = ExpenseStatus.Paid,
            Amount = 125m,
            IncurredAt = new DateTime(2027, 1, 14, 0, 0, 0, DateTimeKind.Utc),
            PaidAt = new DateTime(2027, 1, 14, 0, 0, 0, DateTimeKind.Utc),
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        setup.Db.Add(expense);
        await setup.Db.SaveChangesAsync();
        var originalPayment = await MoneyAccountingPosting.PostExpenseOccurrenceAsync(
            setup.Db, AtomicContext(), expense, 1);
        await setup.Db.SaveChangesAsync();
        var connection = new BankConnection
        {
            PortfolioId = 1,
            Provider = "Manual",
            InstitutionName = "Paid expense bank",
            AccountName = "Checking",
            Status = "Active",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        var transaction = new BankTransaction
        {
            PortfolioId = 1,
            BankConnection = connection,
            ProviderTransactionId = "paid-expense-bank-match",
            PostedAt = new DateTime(2027, 1, 14, 0, 0, 0, DateTimeKind.Utc),
            Description = "Paid expense match",
            Amount = -125m,
            IsoCurrencyCode = "USD",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        setup.Db.Add(transaction);
        await setup.Db.SaveChangesAsync();

        var matched = await MoneyAccountingPosting.PostBankMatchedExpenseAsync(
            setup.Db, AtomicContext(), expense, transaction, 1);

        matched.Id.Should().Be(originalPayment!.Id);
        await setup.Db.SaveChangesAsync();
        (await setup.Db.JournalLines.CountAsync(line =>
            line.JournalEntry!.SourceType == JournalSourceType.ExpensePayment
            && line.JournalEntry.SourceId == expense.Id
            && line.CreditAmount > 0m)).Should().Be(1);
    }

    [Fact]
    public async Task LoanPaymentPostsPrincipalInterestEscrowAndCashSeparately()
    {
        await using var setup = await _fixture.CreateContextAsync();
        await SeedChartAsync(setup.Db);
        var property = NewProperty("Loan posting property");
        var loan = new Loan
        {
            PortfolioId = 1,
            Property = property,
            Lender = "Test lender",
            OriginalAmount = 100_000m,
            CurrentBalance = 99_000m,
            AnnualInterestRatePct = 6m,
            TermMonths = 360,
            StartDate = new DateTime(2027, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            MonthlyPrincipalInterest = 600m,
            MonthlyEscrow = 50m,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        var payment = new LoanPayment
        {
            PortfolioId = 1,
            Loan = loan,
            PeriodKey = "2027-01",
            DueDate = new DateTime(2027, 1, 15, 0, 0, 0, DateTimeKind.Utc),
            PaidDate = new DateTime(2027, 1, 15, 0, 0, 0, DateTimeKind.Utc),
            PrincipalAmount = 500m,
            InterestAmount = 100m,
            EscrowAmount = 50m,
            TotalAmount = 650m,
            BalanceAfter = 98_500m,
            Status = LoanPaymentStatus.Paid,
            CreatedAt = DateTime.UtcNow,
        };
        setup.Db.Add(payment);
        await setup.Db.SaveChangesAsync();

        await MoneyAccountingPosting.PostLoanPaymentAsync(
            setup.Db, AtomicContext(), payment, 1);
        await setup.Db.SaveChangesAsync();

        var lines = await LinesAsync(setup.Db, JournalSourceType.LoanPayment, payment.Id);
        lines.Should().Equal(
            ("mortgage-payable", 500m, 0m),
            ("mortgage-interest", 100m, 0m),
            ("mortgage-escrow-asset", 50m, 0m),
            ("operating-cash", 0m, 650m));
    }

    [Fact]
    public async Task LoanPaymentOmitsZeroEscrowComponentLine()
    {
        await using var setup = await _fixture.CreateContextAsync();
        await SeedChartAsync(setup.Db);
        var property = NewProperty("Interest-only loan posting property");
        var loan = new Loan
        {
            PortfolioId = 1,
            Property = property,
            Lender = "Interest-only lender",
            OriginalAmount = 100_000m,
            CurrentBalance = 99_500m,
            AnnualInterestRatePct = 6m,
            TermMonths = 360,
            StartDate = new DateTime(2027, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            MonthlyPrincipalInterest = 600m,
            MonthlyEscrow = 0m,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        var payment = new LoanPayment
        {
            PortfolioId = 1,
            Loan = loan,
            PeriodKey = "2027-02",
            DueDate = new DateTime(2027, 2, 15, 0, 0, 0, DateTimeKind.Utc),
            PaidDate = new DateTime(2027, 2, 15, 0, 0, 0, DateTimeKind.Utc),
            PrincipalAmount = 500m,
            InterestAmount = 100m,
            EscrowAmount = 0m,
            TotalAmount = 600m,
            BalanceAfter = 99_000m,
            Status = LoanPaymentStatus.Paid,
            CreatedAt = DateTime.UtcNow,
        };
        setup.Db.Add(payment);
        await setup.Db.SaveChangesAsync();

        await MoneyAccountingPosting.PostLoanPaymentAsync(
            setup.Db, AtomicContext(), payment, 1);
        await setup.Db.SaveChangesAsync();

        var lines = await LinesAsync(setup.Db, JournalSourceType.LoanPayment, payment.Id);
        lines.Should().Equal(
            ("mortgage-payable", 500m, 0m),
            ("mortgage-interest", 100m, 0m),
            ("operating-cash", 0m, 600m));
    }

    [Fact]
    public async Task UnpaidLoanPaymentCorrectionPostsNoJournal()
    {
        await using var setup = await _fixture.CreateContextAsync();
        await SeedChartAsync(setup.Db);
        var property = NewProperty("Unpaid correction property");
        var loan = new Loan
        {
            PortfolioId = 1,
            Property = property,
            Lender = "Unpaid correction lender",
            OriginalAmount = 100_000m,
            CurrentBalance = 99_500m,
            AnnualInterestRatePct = 6m,
            TermMonths = 360,
            StartDate = new DateTime(2027, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            MonthlyPrincipalInterest = 600m,
            MonthlyEscrow = 0m,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        var payment = new LoanPayment
        {
            PortfolioId = 1,
            Loan = loan,
            PeriodKey = "2027-03",
            DueDate = new DateTime(2027, 3, 15, 0, 0, 0, DateTimeKind.Utc),
            PrincipalAmount = 500m,
            InterestAmount = 100m,
            TotalAmount = 600m,
            BalanceAfter = 99_000m,
            Status = LoanPaymentStatus.Scheduled,
            CreatedAt = DateTime.UtcNow,
        };
        setup.Db.Add(payment);
        await setup.Db.SaveChangesAsync();
        var correction = new LoanPaymentCorrection
        {
            PortfolioId = 1,
            LoanPaymentId = payment.Id,
            DueDate = payment.DueDate,
            PrincipalAmount = 450m,
            InterestAmount = 100m,
            EscrowAmount = 0m,
            TotalAmount = 550m,
            BalanceAfter = 99_050m,
            Status = LoanPaymentStatus.Scheduled,
            CreatedAtUtc = DateTime.UtcNow,
        };
        setup.Db.Add(correction);
        await setup.Db.SaveChangesAsync();

        var journal = await MoneyAccountingPosting.PostLoanPaymentCorrectionAsync(
            setup.Db, AtomicContext(), payment, correction, 1);

        journal.Should().BeNull();
        await setup.Db.SaveChangesAsync();
        (await setup.Db.JournalEntries.CountAsync(entry =>
            entry.PortfolioId == 1 && entry.SourceType == JournalSourceType.LoanPayment))
            .Should().Be(0);
    }

    [Fact]
    public async Task CapitalPurchasePostsBuildingsAndCash()
    {
        await using var setup = await _fixture.CreateContextAsync();
        await SeedChartAsync(setup.Db);
        var property = NewProperty("Capital posting property");
        var asset = new CapitalAsset
        {
            PortfolioId = 1,
            Property = property,
            Description = "Replace roof",
            CostBasis = 8_500m,
            InServiceDate = new DateTime(2027, 1, 20, 0, 0, 0, DateTimeKind.Utc),
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        setup.Db.CapitalAssets.Add(asset);
        await setup.Db.SaveChangesAsync();

        await MoneyAccountingPosting.PostCapitalPurchaseAsync(
            setup.Db, AtomicContext(), asset, 1);
        await setup.Db.SaveChangesAsync();

        var lines = await LinesAsync(setup.Db, JournalSourceType.CapitalPurchase, asset.Id);
        lines.Should().Equal(
            ("buildings-and-improvements", 8_500m, 0m),
            ("operating-cash", 0m, 8_500m));
    }

    [Fact]
    public async Task CapitalizingPaidExpenseReversesExpenseAndLeavesOneCapitalCashCredit()
    {
        await using var setup = await _fixture.CreateContextAsync();
        await SeedChartAsync(setup.Db);
        var property = NewProperty("Paid capitalized expense property");
        var expense = new Expense
        {
            PortfolioId = 1,
            OperationalScope = ExpenseOperationalScope.Property,
            Property = property,
            Category = ScheduleECategory.Repairs,
            Description = "Replace roof",
            Status = ExpenseStatus.Paid,
            Amount = 8_500m,
            IncurredAt = new DateTime(2027, 1, 19, 0, 0, 0, DateTimeKind.Utc),
            PaidAt = new DateTime(2027, 1, 19, 0, 0, 0, DateTimeKind.Utc),
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        setup.Db.Expenses.Add(expense);
        await setup.Db.SaveChangesAsync();
        await MoneyAccountingPosting.PostExpenseOccurrenceAsync(
            setup.Db, AtomicContext(), expense, 1);
        await setup.Db.SaveChangesAsync();

        var asset = new CapitalAsset
        {
            PortfolioId = 1,
            PropertyId = property.Id,
            SourceExpenseId = expense.Id,
            Description = expense.Description,
            CostBasis = expense.Amount,
            InServiceDate = expense.IncurredAt,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        setup.Db.CapitalAssets.Add(asset);
        await setup.Db.SaveChangesAsync();
        await MoneyAccountingPosting.PostCapitalPurchaseAsync(
            setup.Db, AtomicContext(), asset, 1);
        await setup.Db.SaveChangesAsync();

        (await setup.Db.JournalEntries.CountAsync(entry =>
            entry.PortfolioId == 1
            && entry.SourceType == JournalSourceType.ExpensePayment
            && entry.ReversesJournalEntryId != null)).Should().Be(1);
        (await setup.Db.JournalLines.Where(line =>
                line.JournalEntry!.PortfolioId == 1
                && line.JournalEntry.SourceType == JournalSourceType.CapitalPurchase
                && line.CreditAmount > 0m)
            .SumAsync(line => line.CreditAmount)).Should().Be(8_500m);
        (await setup.Db.JournalLines.Where(line =>
                line.JournalEntry!.PortfolioId == 1
                && line.JournalEntry.SourceType == JournalSourceType.ExpensePayment
                && line.LedgerAccount!.SystemKey == "repairs-and-maintenance")
            .SumAsync(line => line.DebitAmount - line.CreditAmount)).Should().Be(0m);
    }

    [Fact]
    public async Task CapitalizingUnpaidExpenseUsesAccountsPayableFunding()
    {
        await using var setup = await _fixture.CreateContextAsync();
        await SeedChartAsync(setup.Db);
        var property = NewProperty("Unpaid capitalized expense property");
        var expense = new Expense
        {
            PortfolioId = 1,
            OperationalScope = ExpenseOperationalScope.Property,
            Property = property,
            Category = ScheduleECategory.Repairs,
            Description = "Roof invoice",
            Status = ExpenseStatus.Pending,
            Amount = 9_250m,
            IncurredAt = new DateTime(2027, 1, 20, 0, 0, 0, DateTimeKind.Utc),
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        setup.Db.Expenses.Add(expense);
        await setup.Db.SaveChangesAsync();
        await MoneyAccountingPosting.PostExpenseOccurrenceAsync(
            setup.Db, AtomicContext(), expense, 1);
        await setup.Db.SaveChangesAsync();

        var asset = new CapitalAsset
        {
            PortfolioId = 1,
            PropertyId = property.Id,
            SourceExpenseId = expense.Id,
            Description = expense.Description,
            CostBasis = expense.Amount,
            InServiceDate = expense.IncurredAt,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        setup.Db.CapitalAssets.Add(asset);
        await setup.Db.SaveChangesAsync();
        await MoneyAccountingPosting.PostCapitalPurchaseAsync(
            setup.Db, AtomicContext(), asset, 1);
        await setup.Db.SaveChangesAsync();

        (await setup.Db.JournalEntries.CountAsync(entry =>
            entry.PortfolioId == 1
            && entry.SourceType == JournalSourceType.BillIncurred
            && entry.ReversesJournalEntryId != null)).Should().Be(1);
        (await LinesAsync(setup.Db, JournalSourceType.CapitalPurchase, asset.Id))
            .Should().Equal(
                ("buildings-and-improvements", 9_250m, 0m),
                ("accounts-payable", 0m, 9_250m));
    }

    [Fact]
    public async Task CapitalPurchaseCanUseLandAndAccountsPayableMappings()
    {
        await using var setup = await _fixture.CreateContextAsync();
        await SeedChartAsync(setup.Db);
        var property = NewProperty("Land purchase property");
        var asset = new CapitalAsset
        {
            PortfolioId = 1,
            Property = property,
            Description = "Land purchase",
            CostBasis = 12_000m,
            InServiceDate = new DateTime(2027, 1, 25, 0, 0, 0, DateTimeKind.Utc),
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        setup.Db.CapitalAssets.Add(asset);
        await setup.Db.SaveChangesAsync();

        await MoneyAccountingPosting.PostCapitalPurchaseAsync(
            setup.Db,
            AtomicContext(),
            asset,
            1,
            assetSystemKey: "land",
            fundingSystemKey: "accounts-payable");
        await setup.Db.SaveChangesAsync();

        var lines = await LinesAsync(setup.Db, JournalSourceType.CapitalPurchase, asset.Id);
        lines.Should().Equal(
            ("land", 12_000m, 0m),
            ("accounts-payable", 0m, 12_000m));
    }

    [Fact]
    public async Task OwnerDistributionPostsOwnerDimensionAndCash()
    {
        await using var setup = await _fixture.CreateContextAsync();
        await SeedChartAsync(setup.Db);
        var owner = new OwnerEntity
        {
            PortfolioId = 1,
            OwnerEntityType = OwnerEntityType.Person,
            Name = "Posting owner",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        var property = NewProperty("Distribution posting property");
        var distribution = new OwnerDistribution
        {
            PortfolioId = 1,
            OwnerEntity = owner,
            Property = property,
            Date = new DateTime(2027, 1, 21, 0, 0, 0, DateTimeKind.Utc),
            Amount = 2_000m,
            Method = DistributionMethod.Check,
            Status = OwnerDistributionStatus.Approved,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        setup.Db.Add(distribution);
        await setup.Db.SaveChangesAsync();

        await MoneyAccountingPosting.PostOwnerDistributionAsync(
            setup.Db, AtomicContext(), distribution, 1);
        await setup.Db.SaveChangesAsync();

        var lines = await setup.Db.JournalLines.AsNoTracking()
            .Where(line => line.JournalEntry!.SourceType == JournalSourceType.OwnerDistribution
                && line.JournalEntry.SourceId == distribution.Id)
            .OrderBy(line => line.Id)
            .Select(line => new
            {
                SystemKey = line.LedgerAccount!.SystemKey!,
                line.DebitAmount,
                line.CreditAmount,
                line.OwnerEntityId,
            })
            .ToListAsync();
        lines.Should().Equal(
            new { SystemKey = "owner-distributions", DebitAmount = 2_000m, CreditAmount = 0m, OwnerEntityId = (int?)owner.Id },
            new { SystemKey = "operating-cash", DebitAmount = 0m, CreditAmount = 2_000m, OwnerEntityId = (int?)owner.Id });
    }

    [Fact]
    public async Task OwnerContributionPostsCashAndOwnerSpecificContributionEquity()
    {
        await using var setup = await _fixture.CreateContextAsync();
        await SeedChartAsync(setup.Db);
        var owner = new OwnerEntity
        {
            PortfolioId = 1,
            OwnerEntityType = OwnerEntityType.Person,
            Name = "Contribution owner",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        var property = NewProperty("Contribution posting property");
        var contribution = new OwnerContribution
        {
            PortfolioId = 1,
            OwnerEntity = owner,
            Property = property,
            Date = new DateTime(2027, 1, 21, 0, 0, 0, DateTimeKind.Utc),
            Amount = 2_000m,
            Method = DistributionMethod.Ach,
            Status = OwnerDistributionStatus.Approved,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        setup.Db.Add(contribution);
        await setup.Db.SaveChangesAsync();

        await MoneyAccountingPosting.PostOwnerContributionAsync(
            setup.Db, AtomicContext(), contribution, 1);
        await setup.Db.SaveChangesAsync();

        var lines = await setup.Db.JournalLines.AsNoTracking()
            .Where(line => line.JournalEntry!.SourceType == JournalSourceType.OwnerContribution
                && line.JournalEntry.SourceId == contribution.Id)
            .OrderBy(line => line.Id)
            .Select(line => new
            {
                SystemKey = line.LedgerAccount!.SystemKey!,
                line.DebitAmount,
                line.CreditAmount,
                line.OwnerEntityId,
            })
            .ToListAsync();
        lines.Should().Equal(
            new { SystemKey = "operating-cash", DebitAmount = 2_000m, CreditAmount = 0m, OwnerEntityId = (int?)owner.Id },
            new { SystemKey = "owner-contributions", DebitAmount = 0m, CreditAmount = 2_000m, OwnerEntityId = (int?)owner.Id });
    }

    [Fact]
    public async Task BankClearDoesNotReverseOwnerDistributionApprovalJournal()
    {
        await using var setup = await _fixture.CreateContextAsync();
        await SeedChartAsync(setup.Db);
        var owner = new OwnerEntity
        {
            PortfolioId = 1,
            OwnerEntityType = OwnerEntityType.Person,
            Name = "Bank-clear owner",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        var distribution = new OwnerDistribution
        {
            PortfolioId = 1,
            OwnerEntity = owner,
            Date = new DateTime(2027, 1, 22, 0, 0, 0, DateTimeKind.Utc),
            Amount = 1_100m,
            Status = OwnerDistributionStatus.Approved,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        setup.Db.Add(distribution);
        await setup.Db.SaveChangesAsync();
        await MoneyAccountingPosting.PostOwnerDistributionAsync(
            setup.Db, AtomicContext(), distribution, 1);
        await setup.Db.SaveChangesAsync();

        var reversal = await MoneyAccountingPosting.ReverseBankMatchedSourceAsync(
            setup.Db,
            AtomicContext(),
            1,
            JournalSourceType.OwnerDistribution,
            distribution.Id,
            1);

        reversal.Should().BeNull();
        await setup.Db.SaveChangesAsync();
        (await setup.Db.JournalEntries.CountAsync(entry =>
            entry.PortfolioId == 1
            && entry.SourceType == JournalSourceType.OwnerDistribution
            && entry.ReversesJournalEntryId != null)).Should().Be(0);
    }

    [Fact]
    public async Task BankTransferUsingOneConfiguredLedgerAccountHasNoAccountingEffect()
    {
        await using var setup = await _fixture.CreateContextAsync();
        await SeedChartAsync(setup.Db);
        var connection = new BankConnection
        {
            PortfolioId = 1,
            Provider = "Manual",
            InstitutionName = "Transfer bank",
            AccountName = "Operating checking",
            Status = "Active",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        var source = new BankTransaction
        {
            PortfolioId = 1,
            BankConnection = connection,
            ProviderTransactionId = "transfer-source-same-ledger",
            PostedAt = new DateTime(2027, 1, 22, 0, 0, 0, DateTimeKind.Utc),
            Description = "Transfer out",
            Amount = -300m,
            IsoCurrencyCode = "USD",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        var destination = new BankTransaction
        {
            PortfolioId = 1,
            BankConnection = connection,
            ProviderTransactionId = "transfer-destination-same-ledger",
            PostedAt = new DateTime(2027, 1, 22, 0, 0, 0, DateTimeKind.Utc),
            Description = "Transfer in",
            Amount = 300m,
            IsoCurrencyCode = "USD",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        setup.Db.BankTransactions.AddRange(source, destination);
        await setup.Db.SaveChangesAsync();

        var journal = await MoneyAccountingPosting.PostBankTransferAsync(
            setup.Db, AtomicContext(), destination, source, 1);

        journal.Should().BeNull("a transfer between accounts tracked under one ledger account has no accounting effect");
        await setup.Db.SaveChangesAsync();
        (await setup.Db.JournalEntries.CountAsync(entry =>
            entry.SourceType == JournalSourceType.BankTransfer
            && entry.SourceId == Math.Min(source.Id, destination.Id))).Should().Be(0);
    }

    [Fact]
    public async Task BankTransferAcrossConfiguredLedgerAccountsPostsDestinationAndSourceCash()
    {
        await using var setup = await _fixture.CreateContextAsync();
        await SeedChartAsync(setup.Db);
        var connection = new BankConnection
        {
            PortfolioId = 1,
            Provider = "Manual",
            InstitutionName = "Cross-ledger transfer bank",
            AccountName = "Trust checking",
            Status = "Active",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        var source = new BankTransaction
        {
            PortfolioId = 1,
            BankConnection = connection,
            ProviderTransactionId = "transfer-source-cross-ledger",
            PostedAt = new DateTime(2027, 1, 23, 0, 0, 0, DateTimeKind.Utc),
            Description = "Transfer out",
            Amount = -300m,
            IsoCurrencyCode = "USD",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        var destination = new BankTransaction
        {
            PortfolioId = 1,
            BankConnection = connection,
            ProviderTransactionId = "transfer-destination-cross-ledger",
            PostedAt = new DateTime(2027, 1, 23, 0, 0, 0, DateTimeKind.Utc),
            Description = "Transfer in",
            Amount = 300m,
            IsoCurrencyCode = "USD",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        setup.Db.BankTransactions.AddRange(source, destination);
        await setup.Db.SaveChangesAsync();

        await MoneyAccountingPosting.PostBankTransferAsync(
            setup.Db, AtomicContext(), destination, source, 1,
            sourceSystemKey: "operating-cash",
            destinationSystemKey: "security-deposit-trust-cash");
        await setup.Db.SaveChangesAsync();

        var lines = await LinesAsync(setup.Db, JournalSourceType.BankTransfer,
            Math.Min(source.Id, destination.Id));
        lines.Should().Equal(
            ("security-deposit-trust-cash", 300m, 0m),
            ("operating-cash", 0m, 300m));
    }

    private static async Task SeedChartAsync(RentalCommandDbContext db)
    {
        await new ChartOfAccountsSeedService(db).SeedAsync(1);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
    }

    private static Property NewProperty(string name) => new()
    {
        PortfolioId = 1,
        Name = name,
        AddressLine1 = "1 Accounting Lane",
        City = "Columbus",
        State = "OH",
        PostalCode = "43215",
        CreatedAt = DateTime.UtcNow,
        UpdatedAt = DateTime.UtcNow,
    };

    private static IAtomicCommandContext AtomicContext()
    {
        var context = new Mock<IAtomicCommandContext>();
        context.SetupGet(value => value.IsActive).Returns(true);
        context.SetupGet(value => value.AttemptId).Returns(Guid.NewGuid());
        context.SetupGet(value => value.AtomicReceiptId).Returns(Guid.NewGuid());
        return context.Object;
    }

    private static async Task<List<(string? SystemKey, decimal Debit, decimal Credit)>> LinesAsync(
        RentalCommandDbContext db,
        JournalSourceType sourceType,
        long sourceId)
    {
        var rows = await db.JournalLines.AsNoTracking()
            .Where(line => line.JournalEntry!.SourceType == sourceType
                && line.JournalEntry.SourceId == sourceId)
            .OrderBy(line => line.Id)
            .Select(line => new
            {
                SystemKey = line.LedgerAccount!.SystemKey,
                Debit = line.DebitAmount,
                Credit = line.CreditAmount,
            })
            .ToListAsync();
        return rows.Select(line => (line.SystemKey, line.Debit, line.Credit)).ToList();
    }
}
