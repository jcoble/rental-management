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

        var service = new AccountingLedgerReadModelService(setup.Db);
        var accountPage = await service.GetGeneralLedgerAsync(1, new GeneralLedgerQuery
        {
            AccountId = accounts[0].Id,
            Take = 20,
        }, CancellationToken.None);

        accountPage.TotalCount.Should().Be(1);
        accountPage.Items.Should().ContainSingle().Which.RunningBalance.Should().Be(100m);

        var mixedPage = await service.GetGeneralLedgerAsync(1, new GeneralLedgerQuery
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
        var accountId = await setup.Db.LedgerAccounts
            .Where(account => account.PortfolioId == 1)
            .OrderBy(account => account.Code)
            .Select(account => account.Id)
            .FirstAsync();
        commands.Reset();

        var service = new AccountingLedgerReadModelService(setup.Db);
        await service.GetGeneralLedgerAsync(1, new GeneralLedgerQuery { AccountId = accountId, Take = 20 }, CancellationToken.None);

        commands.Count.Should().BeLessThanOrEqualTo(3);
        commands.Sql.Should().Contain(sql => sql.Contains("JournalLines", StringComparison.Ordinal));
        commands.Sql.Should().Contain(sql => sql.Contains("SUM", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ChartJournalAndFinancialStatements_StayPortfolioScopedAndSatisfyAccountingEquation()
    {
        var commands = new SqlCommandCounter();
        await using var setup = await _fixture.CreateContextAsync([commands]);
        var seed = new ChartOfAccountsSeedService(setup.Db);
        await seed.SeedAsync(1);
        await setup.Db.SaveChangesAsync();
        setup.Db.ChangeTracker.Clear();
        var accounts = await setup.Db.LedgerAccounts
            .Where(account => account.PortfolioId == 1
                && (account.Code == "1000" || account.Code == "2100" || account.Code == "4000"))
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
                new AccountingProposedLine { LedgerAccountId = accounts[2].Id, CreditAmount = 250m },
            ],
        });
        await new AccountingPostingService(setup.Db).PostAsync(new AccountingProposedEntry
        {
            PortfolioId = 1,
            EffectiveOn = new DateOnly(2026, 8, 1),
            Currency = "USD",
            Description = "Security deposit liability",
            SourceType = JournalSourceType.OpeningBalance,
            SourceId = 8103,
            SourceBusinessKey = "read-model:8103",
            PostingRuleVersion = 1,
            AttemptId = Guid.NewGuid(),
            AtomicReceiptId = Guid.NewGuid(),
            UserId = 1,
            Lines =
            [
                new AccountingProposedLine { LedgerAccountId = accounts[0].Id, DebitAmount = 50m },
                new AccountingProposedLine { LedgerAccountId = accounts[1].Id, CreditAmount = 50m },
            ],
        });
        await setup.Db.SaveChangesAsync();
        setup.Db.ChangeTracker.Clear();

        var service = new AccountingLedgerReadModelService(setup.Db);
        var chart = await service.GetChartOfAccountsAsync(1, new ChartOfAccountsQuery { Take = 200 });
        var detail = await service.GetJournalDetailAsync(1, entry.PublicId);
        var trialBalance = await service.GetTrialBalanceAsync(1, new StatementQuery
        {
            From = new DateOnly(2026, 8, 1),
            To = new DateOnly(2026, 8, 31),
            Currency = "USD",
        });
        commands.Reset();
        var balanceSheet = await service.GetBalanceSheetAsync(1, new StatementQuery
        {
            From = new DateOnly(2026, 8, 1),
            To = new DateOnly(2026, 8, 31),
            Currency = "USD",
        });
        var balanceSheetQueryCount = commands.Count;
        var balanceSheetSql = commands.Sql;
        var incomeStatement = await service.GetIncomeStatementAsync(1, new StatementQuery
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
        trialBalance.IsBalanced.Should().BeTrue();
        trialBalance.TotalDebits.Should().Be(300m);
        trialBalance.TotalCredits.Should().Be(300m);
        balanceSheet.Sections.Should().ContainSingle(section => section.Label == nameof(AccountType.Asset));
        balanceSheet.Sections.Should().ContainSingle(section => section.Label == nameof(AccountType.Liability))
            .Which.Rows.Should().ContainSingle(row => row.AccountName == "Tenant Security Deposits Payable" && row.Amount == 50m);
        balanceSheet.Sections.Should().ContainSingle(section => section.Label == nameof(AccountType.Equity))
            .Which.Rows.Should().ContainSingle(row => row.AccountId == 0 && row.AccountName == "Net income" && row.Amount == 250m);
        balanceSheet.Totals.Assets.Should().Be(300m);
        balanceSheet.Totals.LiabilitiesAndEquity.Should().Be(300m);
        balanceSheet.Totals.NetIncome.Should().Be(250m);
        balanceSheetQueryCount.Should().Be(4);
        balanceSheetSql.Should().OnlyContain(sql => sql.Contains("SUM", StringComparison.OrdinalIgnoreCase));
        incomeStatement.Totals.NetIncome.Should().Be(250m);
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
