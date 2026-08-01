using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Data.Accounting;
using RentalCommand.TestCommon;

namespace RentalCommand.Api.Tests.Domain;

[Collection(MigratedPostgreSqlCollection.Name)]
public sealed class AccountingConversionPostgreSqlTests
{
    private readonly MigratedPostgreSqlFixture _fixture;

    public AccountingConversionPostgreSqlTests(MigratedPostgreSqlFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task ConversionPostsPaidExpenseExactlyOnceAndReconcilesIt()
    {
        await using var setup = await _fixture.CreateContextAsync();
        setup.Db.Expenses.Add(new Expense
        {
            PortfolioId = 1,
            Category = ScheduleECategory.Repairs,
            Description = "Historical repair",
            Status = ExpenseStatus.Paid,
            Amount = 125m,
            IncurredAt = new DateTime(2027, 1, 10, 0, 0, 0, DateTimeKind.Utc),
            PaidAt = new DateTime(2027, 1, 11, 0, 0, 0, DateTimeKind.Utc),
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        await setup.Db.SaveChangesAsync();
        setup.Db.ChangeTracker.Clear();

        var conversion = new AccountingConversionService(setup.Db);
        await conversion.ConvertPortfolioAsync(1);
        await conversion.ConvertPortfolioAsync(1);

        var journal = await setup.Db.JournalEntries
            .Include(entry => entry.Lines)
            .SingleAsync(entry => entry.SourceType == JournalSourceType.ExpensePayment);
        journal.Lines.Should().Contain(line => line.DebitAmount == 125m);
        journal.Lines.Should().Contain(line => line.CreditAmount == 125m);
        (await setup.Db.JournalEntries.CountAsync(entry => entry.SourceType == JournalSourceType.ExpensePayment))
            .Should().Be(1);

        var reconciliation = await setup.Db.AccountingConversionReconciliations
            .SingleAsync(row => row.PortfolioId == 1 && row.SourceType == JournalSourceType.ExpensePayment);
        reconciliation.SourceTotal.Should().Be(125m);
        reconciliation.PostedDebitTotal.Should().Be(125m);
        reconciliation.PostedCreditTotal.Should().Be(125m);
        reconciliation.ImbalanceAmount.Should().Be(0m);
        reconciliation.MissingMappingCount.Should().Be(0);
        reconciliation.UnsupportedSourceCount.Should().Be(0);
    }

    [Fact]
    public async Task ConversionKeepsProviderReceiptsInUndepositedFundsUntilSettlement()
    {
        await using var setup = await _fixture.CreateContextAsync();
        var now = DateTime.UtcNow;
        var property = new Property
        {
            PortfolioId = 1,
            Name = "Provider receipt property",
            AddressLine1 = "1 Receipt Lane",
            City = "Columbus",
            State = "OH",
            PostalCode = "43215",
            CreatedAt = now,
            UpdatedAt = now,
        };
        var unit = new Unit
        {
            Property = property,
            UnitNumber = "1",
            MarketRent = 1000m,
            CreatedAt = now,
            UpdatedAt = now,
        };
        var management = new LeaseManagement
        {
            PortfolioId = 1,
            Property = property,
            Unit = unit,
            RelationshipNumber = "provider-receipt-lease",
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
            CreatedByUserId = 1,
            RowVersion = Guid.NewGuid(),
        };
        var account = new TenantAccount
        {
            PortfolioId = 1,
            LeaseManagement = management,
            AccountNumber = "provider-receipt-account",
            Currency = "USD",
            OpenedAtUtc = now,
            CreatedAtUtc = now,
            CreatedByUserId = 1,
        };
        var settledAttempt = ProviderAttempt(account, "settled-provider-attempt", 125m, now);
        var unsettledAttempt = ProviderAttempt(account, "unsettled-provider-attempt", 75m, now);
        var settledReceipt = Receipt(account, settledAttempt, "settled-receipt", 125m, now);
        var unsettledReceipt = Receipt(account, unsettledAttempt, "unsettled-receipt", 75m, now);
        var connection = new BankConnection
        {
            PortfolioId = 1,
            Provider = "Plaid",
            InstitutionName = "Settlement bank",
            AccountName = "Operating checking",
            Status = "Active",
            CreatedAt = now,
            UpdatedAt = now,
        };
        var settlement = new BankTransaction
        {
            PortfolioId = 1,
            BankConnection = connection,
            MatchedTenantAccount = account,
            MatchedTenantLedgerEntry = settledReceipt,
            ProviderTransactionId = "settlement-provider-receipt",
            PostedAt = now,
            Description = "Provider settlement",
            Amount = 125m,
            IsoCurrencyCode = "USD",
            MatchStatus = "Matched",
            CreatedAt = now,
            UpdatedAt = now,
        };
        setup.Db.AddRange(account, settledAttempt, unsettledAttempt, settledReceipt, unsettledReceipt, connection, settlement);
        await setup.Db.SaveChangesAsync();

        await new AccountingConversionService(setup.Db).ConvertPortfolioAsync(1);

        var receiptJournals = await setup.Db.JournalEntries
            .Include(entry => entry.Lines)
            .Where(entry => entry.SourceType == JournalSourceType.TenantReceipt)
            .OrderBy(entry => entry.SourceId)
            .ToListAsync();
        receiptJournals.Should().HaveCount(2);
        var undepositedId = await setup.Db.LedgerAccounts
            .Where(accountRow => accountRow.PortfolioId == 1 && accountRow.SystemKey == "undeposited-funds")
            .Select(accountRow => accountRow.Id)
            .SingleAsync();
        (await setup.Db.JournalLines
            .Where(line => line.LedgerAccountId == undepositedId
                && line.JournalEntry!.SourceType == JournalSourceType.TenantReceipt)
            .SumAsync(line => line.DebitAmount - line.CreditAmount))
            .Should().Be(200m);

        var settlementJournals = await setup.Db.JournalEntries
            .Where(entry => entry.SourceType == JournalSourceType.ProviderSettlement)
            .ToListAsync();
        settlementJournals.Should().ContainSingle();
        var operatingCashId = await setup.Db.LedgerAccounts
            .Where(accountRow => accountRow.PortfolioId == 1 && accountRow.SystemKey == "operating-cash")
            .Select(accountRow => accountRow.Id)
            .SingleAsync();
        (await setup.Db.JournalLines
            .Where(line => line.JournalEntry!.SourceType == JournalSourceType.ProviderSettlement
                && line.LedgerAccountId == operatingCashId)
            .SumAsync(line => line.DebitAmount - line.CreditAmount))
            .Should().Be(125m);
    }

    private static TenantPaymentAttempt ProviderAttempt(
        TenantAccount account,
        string idempotencyKey,
        decimal amount,
        DateTime now) => new()
        {
            PortfolioId = account.PortfolioId,
            TenantAccount = account,
            Provider = "stripe",
            IdempotencyKey = idempotencyKey,
            AttemptType = TenantPaymentAttemptType.UnappliedReceipt,
            State = TenantPaymentAttemptState.Succeeded,
            Amount = amount,
            Currency = "USD",
            PreparedAtUtc = now,
            SubmittedAtUtc = now,
            SettledAtUtc = now,
            UpdatedAtUtc = now,
            CreatedByUserId = 1,
        };

    private static TenantLedgerEntry Receipt(
        TenantAccount account,
        TenantPaymentAttempt attempt,
        string businessKey,
        decimal amount,
        DateTime now) => new()
        {
            PortfolioId = account.PortfolioId,
            TenantAccount = account,
            EntryType = TenantLedgerEntryType.PaymentReceipt,
            Direction = TenantLedgerDirection.Credit,
            Amount = amount,
            Currency = "USD",
            EffectiveOn = DateOnly.FromDateTime(now),
            PostedAtUtc = now,
            Description = businessKey,
            BusinessKey = businessKey,
            ProviderPaymentAttempt = attempt,
            CreatedByUserId = 1,
        };
}
