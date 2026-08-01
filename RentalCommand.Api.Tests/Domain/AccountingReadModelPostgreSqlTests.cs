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
        await using var setup = await _fixture.CreateContextAsync();
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
        var trial = await service.GetTrialBalanceAsync(scope, query);
        var balanceSheet = await service.GetBalanceSheetAsync(scope, query);

        trial.Rows.Single(row => row.AccountId == cash.Id).Should().BeEquivalentTo(
            new { DebitBalance = 60m, CreditBalance = 0m });
        trial.TotalDebits.Should().Be(100m);
        trial.TotalCredits.Should().Be(100m);
        trial.IsBalanced.Should().BeTrue();
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
        detail.Should().NotBeNull();
        detail!.DocumentIds.Should().Equal(file.Id);
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
