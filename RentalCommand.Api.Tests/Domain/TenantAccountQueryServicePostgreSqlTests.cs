using System.Data;
using System.Data.Common;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using RentalCommand.Api.DTOs;
using RentalCommand.Api.Services.Domain;
using RentalCommand.Core.Authorization;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.TestCommon;
using Xunit.Abstractions;

namespace RentalCommand.Api.Tests.Domain;

[Collection(MigratedPostgreSqlCollection.Name)]
public sealed class TenantAccountQueryServicePostgreSqlTests : IAsyncLifetime
{
    private const int PortfolioId = 1;
    private readonly MigratedPostgreSqlFixture _fixture;
    private readonly ITestOutputHelper _output;
    private readonly SqlCommandRecorder _commands = new();
    private readonly TransactionRecorder _transactions = new();
    private MigratedPostgreSqlTestContext _context = null!;
    private WorkspaceReadScope _scope;
    private SeededLedger _ledger = null!;

    public TenantAccountQueryServicePostgreSqlTests(
        MigratedPostgreSqlFixture fixture,
        ITestOutputHelper output)
    {
        _fixture = fixture;
        _output = output;
    }

    public async Task InitializeAsync()
    {
        _context = await _fixture.CreateContextAsync([_commands, _transactions]);
        _scope = _context.Db.SeedAdministratorScope(
            PortfolioId,
            nameof(TenantAccountQueryServicePostgreSqlTests));
        _ledger = await SeedLedgerAsync();
        await _context.ActivateApiScopeAsync(_scope);
        _commands.Reset();
        _transactions.Reset();
    }

    public async Task DisposeAsync() => await _context.DisposeAsync();

    [Fact]
    public async Task ListEntriesPage_ComputesTotalsWithoutPerRowAggregates()
    {
        var service = new TenantAccountQueryService(_context.Db, TimeProvider.System);
        var query = new TenantLedgerEntryGlobalListQuery
        {
            PropertyId = _ledger.PropertyId,
            UnitId = _ledger.UnitId,
            Search = "ledger filter",
            Sort = "-effectiveOn",
            Take = 25,
        };

        var page = await service.ListEntriesPageAsync(_scope, query);
        var pageSql = _commands.Sql.ToArray();
        _transactions.IsolationLevels.Should().Equal(IsolationLevel.RepeatableRead);
        _commands.TransactionPresence.Should().HaveCount(3)
            .And.OnlyContain(isInsideTransaction => isInsideTransaction);
        foreach (var (sql, index) in pageSql.Select((sql, index) => (sql, index)))
        {
            _output.WriteLine($"--- GLOBAL ENTRY STATEMENT {index + 1} ---");
            _output.WriteLine(sql);
        }

        var directMonthTotals = await _context.Db.TenantLedgerEntries.AsNoTracking()
            .Where(entry => entry.PortfolioId == PortfolioId
                && entry.TenantAccountId == _ledger.TenantAccountId
                && EF.Functions.ILike(entry.Description, "%ledger filter%"))
            .GroupBy(entry => new { entry.EffectiveOn.Year, entry.EffectiveOn.Month })
            .Select(group => new
            {
                group.Key.Year,
                group.Key.Month,
                Charges = group
                    .Where(entry => entry.Direction == TenantLedgerDirection.Debit)
                    .Sum(entry => entry.Amount),
                Credits = group
                    .Where(entry => entry.Direction == TenantLedgerDirection.Credit)
                    .Sum(entry => entry.Amount),
            })
            .ToDictionaryAsync(total => (total.Year, total.Month));

        page.TotalCount.Should().Be(4);
        page.Items.Should().HaveCount(4);
        page.Items.Should().OnlyContain(item => item.FilteredTotalCount == 4);
        page.Items.Single(item => item.TenantLedgerEntryId == _ledger.ReversedEntryId)
            .HasReversal.Should().BeTrue();
        foreach (var item in page.Items)
        {
            var expected = directMonthTotals[(item.EffectiveOn.Year, item.EffectiveOn.Month)];
            item.MonthCharges.Should().Be(expected.Charges);
            item.MonthPaymentsAndCredits.Should().Be(expected.Credits);
        }

        _commands.Reset();
        var januaryOnly = await service.ListEntriesPageAsync(_scope,
            new TenantLedgerEntryGlobalListQuery
            {
                PropertyId = _ledger.PropertyId,
                UnitId = _ledger.UnitId,
                Search = "ledger filter",
                Sort = "effectiveOn",
                Take = 3,
            });
        januaryOnly.Items.Should().HaveCount(3);
        januaryOnly.Items.Should().OnlyContain(item => item.EffectiveOn.Month == 1);
        januaryOnly.Items.Should().OnlyContain(item =>
            item.MonthCharges == 100m && item.MonthPaymentsAndCredits == 125m,
            "represented-month totals must cover every filtered January entry, not only a page aggregate");
        _commands.Sql.Should().HaveCount(3);

        _commands.Reset();
        var outOfRange = await service.ListEntriesPageAsync(_scope,
            new TenantLedgerEntryGlobalListQuery
            {
                PropertyId = _ledger.PropertyId,
                UnitId = _ledger.UnitId,
                Search = "ledger filter",
                EntryType = TenantLedgerEntryType.ManualCharge,
                Skip = 2,
                Take = 25,
            });

        outOfRange.Items.Should().BeEmpty();
        outOfRange.TotalCount.Should().Be(2,
            "the filtered count must not depend on the page containing a first item");

        _commands.Reset();
        var defaultPage = await service.ListEntriesPageAsync(_scope,
            new TenantLedgerEntryGlobalListQuery
            {
                Sort = "-effectiveOn",
                Take = 25,
            });
        var defaultPageSql = _commands.Sql.ToArray();
        defaultPage.Items.Should().HaveCount(4);
        defaultPageSql.Should().HaveCount(3);
        defaultPageSql[0].Should().NotContain("vw_lease_management_lifecycle");
        defaultPageSql[1].Should().NotContain("vw_lease_management_lifecycle");
        defaultPageSql[2].Should().NotContain("search_lifecycle",
            "the represented-month scan must stay slim when search does not need display fields");

        pageSql.Should().HaveCount(3);
        pageSql[0].Should().ContainEquivalentOf("count(*)");
        pageSql[1].Should().Contain("ORDER BY").And.Contain("LIMIT").And.Contain("OFFSET");
        pageSql[1].Should().Contain("TenantLedgerEntryId");
        pageSql[2].Should().Contain("page_entries AS MATERIALIZED");
        pageSql[2].Should().Contain("page_months").And.Contain("month_totals");
        pageSql[2].Should().ContainEquivalentOf("sum(").And.Contain("GROUP BY");
        pageSql[2].Should().Contain("= ANY");
        pageSql[2].Should().Contain("array_position");
        pageSql[0].Should().NotContainEquivalentOf("sum(");
        pageSql[0].Should().NotContain("ReversesEntryId");
        pageSql[1].Should().NotContainEquivalentOf("sum(");
        pageSql[1].Should().NotContain("ReversesEntryId");
        pageSql[2].Should().Contain("ReversesEntryId").And.Contain("EXISTS");
        foreach (var sql in pageSql)
        {
            sql.Should().Contain("public.rc_api_effective_capability_scopes(", Exactly.Once(),
                "each statement must execute the authorized base query once");
        }
    }

    private async Task<SeededLedger> SeedLedgerAsync()
    {
        var seededAt = new DateTime(2026, 12, 15, 12, 0, 0, DateTimeKind.Utc);
        var property = new Property
        {
            PortfolioId = PortfolioId,
            Name = "Ledger filter property",
            AddressLine1 = "816 Query Lane",
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
            UnitNumber = "BUG-B",
            CreatedAt = seededAt,
            UpdatedAt = seededAt,
        };
        var management = new LeaseManagement
        {
            PortfolioId = PortfolioId,
            Property = property,
            Unit = unit,
            RelationshipNumber = "LM-LEDGER-FILTER",
            PossessionGivenAtUtc = seededAt,
            CreatedAtUtc = seededAt,
            UpdatedAtUtc = seededAt,
            CreatedByUserId = _scope.UserId,
            RowVersion = Guid.NewGuid(),
        };
        var account = new TenantAccount
        {
            PortfolioId = PortfolioId,
            LeaseManagement = management,
            AccountNumber = "TA-LEDGER-FILTER",
            Currency = "USD",
            OpenedAtUtc = seededAt,
            CreatedAtUtc = seededAt,
            CreatedByUserId = _scope.UserId,
        };
        var januaryCharge = Entry(
            account,
            TenantLedgerEntryType.ManualCharge,
            TenantLedgerDirection.Debit,
            100m,
            new DateOnly(2027, 1, 5),
            "january charge");
        var entries = new[]
        {
            januaryCharge,
            Entry(
                account,
                TenantLedgerEntryType.Credit,
                TenantLedgerDirection.Credit,
                25m,
                new DateOnly(2027, 1, 10),
                "january credit"),
            Entry(
                account,
                TenantLedgerEntryType.Reversal,
                TenantLedgerDirection.Credit,
                100m,
                new DateOnly(2027, 1, 20),
                "january reversal",
                januaryCharge),
            Entry(
                account,
                TenantLedgerEntryType.ManualCharge,
                TenantLedgerDirection.Debit,
                70m,
                new DateOnly(2027, 2, 5),
                "february charge"),
        };

        _context.Db.AddRange(property, unit, management, account);
        _context.Db.TenantLedgerEntries.AddRange(entries);
        await _context.Db.SaveChangesAsync();
        _context.Db.ChangeTracker.Clear();

        return new SeededLedger(property.Id, unit.Id, account.Id, januaryCharge.Id);
    }

    private TenantLedgerEntry Entry(
        TenantAccount account,
        TenantLedgerEntryType entryType,
        TenantLedgerDirection direction,
        decimal amount,
        DateOnly effectiveOn,
        string key,
        TenantLedgerEntry? reverses = null) => new()
    {
        PortfolioId = PortfolioId,
        TenantAccount = account,
        EntryType = entryType,
        Direction = direction,
        Amount = amount,
        Currency = "USD",
        EffectiveOn = effectiveOn,
        DueOn = direction == TenantLedgerDirection.Debit ? effectiveOn : null,
        PostedAtUtc = effectiveOn.ToDateTime(new TimeOnly(12, 0), DateTimeKind.Utc),
        Description = $"ledger filter {key}",
        BusinessKey = $"ledger-filter:{key}",
        ReversesEntry = reverses,
        CreatedByUserId = _scope.UserId,
    };

    private sealed record SeededLedger(
        int PropertyId,
        int UnitId,
        int TenantAccountId,
        long ReversedEntryId);

    private sealed class SqlCommandRecorder : DbCommandInterceptor
    {
        private readonly List<string> _sql = [];
        private readonly List<bool> _transactionPresence = [];

        public IReadOnlyList<string> Sql => _sql.ToArray();
        public IReadOnlyList<bool> TransactionPresence => _transactionPresence.ToArray();

        public void Reset()
        {
            _sql.Clear();
            _transactionPresence.Clear();
        }

        public override InterceptionResult<DbDataReader> ReaderExecuting(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result)
        {
            _sql.Add(command.CommandText);
            _transactionPresence.Add(command.Transaction is not null);
            return base.ReaderExecuting(command, eventData, result);
        }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            _sql.Add(command.CommandText);
            _transactionPresence.Add(command.Transaction is not null);
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }
    }

    private sealed class TransactionRecorder : DbTransactionInterceptor
    {
        private readonly List<IsolationLevel> _isolationLevels = [];

        public IReadOnlyList<IsolationLevel> IsolationLevels => _isolationLevels.ToArray();

        public void Reset() => _isolationLevels.Clear();

        public override ValueTask<InterceptionResult<DbTransaction>> TransactionStartingAsync(
            DbConnection connection,
            TransactionStartingEventData eventData,
            InterceptionResult<DbTransaction> result,
            CancellationToken cancellationToken = default)
        {
            _isolationLevels.Add(eventData.IsolationLevel);
            return ValueTask.FromResult(result);
        }
    }
}
