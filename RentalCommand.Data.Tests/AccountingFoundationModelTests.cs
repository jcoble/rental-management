using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;

namespace RentalCommand.Data.Tests;

public sealed class AccountingFoundationModelTests
{
    [Fact]
    public void LedgerFoundation_UsesTheLockedAccountAndJournalShapes()
    {
        using var db = new RentalCommandDbContext(new DbContextOptionsBuilder<RentalCommandDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

        typeof(AccountType).GetEnumNames().Should().Equal("Asset", "Liability", "Equity", "Income", "Expense");
        typeof(NormalBalance).GetEnumNames().Should().Equal("Debit", "Credit");
        typeof(JournalSourceType).GetEnumNames().Should().Contain("TenantCharge", "OpeningBalance");

        var account = db.GetService<IDesignTimeModel>().Model.FindEntityType(typeof(LedgerAccount))!;
        var entry = db.GetService<IDesignTimeModel>().Model.FindEntityType(typeof(JournalEntry))!;
        var line = db.GetService<IDesignTimeModel>().Model.FindEntityType(typeof(JournalLine))!;

        account.FindProperty(nameof(LedgerAccount.PortfolioId)).Should().NotBeNull();
        account.GetIndexes().Should().Contain(index => index.IsUnique &&
            index.Properties.Select(property => property.Name).SequenceEqual(new[]
            {
                nameof(LedgerAccount.PortfolioId),
                nameof(LedgerAccount.Code),
            }));
        entry.GetIndexes().Should().Contain(index => index.IsUnique &&
            index.Properties.Select(property => property.Name).SequenceEqual(new[]
            {
                nameof(JournalEntry.PortfolioId),
                nameof(JournalEntry.SourceType),
                nameof(JournalEntry.SourceId),
                nameof(JournalEntry.PostingRuleVersion),
            }));
        line.FindProperty(nameof(JournalLine.DebitAmount))!.GetPrecision().Should().Be(18);
        line.FindProperty(nameof(JournalLine.DebitAmount))!.GetScale().Should().Be(2);
    }
}
