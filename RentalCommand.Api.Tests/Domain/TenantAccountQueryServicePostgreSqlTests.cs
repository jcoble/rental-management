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

    [Fact]
    public async Task ListAccountsPage_SelectedPropertyScopeExcludesOtherCurrentPropertyFromRowsAndCount()
    {
        var now = new DateTime(2027, 1, 15, 12, 0, 0, DateTimeKind.Utc);
        var selectedProperty = await _context.Db.Properties
            .SingleAsync(property => property.Id == _ledger.PropertyId);
        var excludedProperty = new Property
        {
            PortfolioId = PortfolioId,
            Name = "Excluded account property",
            AddressLine1 = "2 Scope Lane",
            City = "Columbus",
            State = "OH",
            PostalCode = "43215",
            CreatedAt = now,
            UpdatedAt = now,
        };
        var excludedUnit = new Unit
        {
            PortfolioId = PortfolioId,
            Property = excludedProperty,
            UnitNumber = "OUT-1",
            CreatedAt = now,
            UpdatedAt = now,
        };
        var excludedManagement = new LeaseManagement
        {
            PublicId = Guid.NewGuid(),
            PortfolioId = PortfolioId,
            Property = excludedProperty,
            Unit = excludedUnit,
            RelationshipNumber = "LM-ACCOUNT-SCOPE-EXCLUDED",
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
            CreatedByUserId = _scope.UserId,
            RowVersion = Guid.NewGuid(),
        };
        var excludedAccount = new TenantAccount
        {
            PublicId = Guid.NewGuid(),
            PortfolioId = PortfolioId,
            LeaseManagement = excludedManagement,
            AccountNumber = "TA-ACCOUNT-SCOPE-EXCLUDED",
            Currency = "USD",
            OpenedAtUtc = now,
            CreatedAtUtc = now,
            CreatedByUserId = _scope.UserId,
        };
        _context.Db.AddRange(excludedProperty, excludedUnit, excludedManagement, excludedAccount);
        await _context.Db.SaveChangesAsync();
        _context.Db.ChangeTracker.Clear();

        var selectedScope = _context.Db.SeedPropertyManagerScope(
            PortfolioId,
            selectedProperty.Id,
            nameof(ListAccountsPage_SelectedPropertyScopeExcludesOtherCurrentPropertyFromRowsAndCount));
        await _context.ActivateApiScopeAsync(selectedScope);
        _commands.Reset();

        var page = await new TenantAccountQueryService(_context.Db, TimeProvider.System)
            .ListAccountsPageAsync(selectedScope, new TenantAccountListQuery { Take = 20 });

        page.TotalCount.Should().Be(1,
            "the migrated PostgreSQL count must stay inside the selected-property authorization boundary");
        page.Items.Should().ContainSingle(item => item.TenantAccountId == _ledger.TenantAccountId);
        page.Items.Should().NotContain(item => item.TenantAccountId == excludedAccount.Id);
        _commands.Sql.Should().HaveCount(2, "the account page executes one count and one page statement");
        _commands.Sql.Should().OnlyContain(sql =>
            sql.Contains("rc_api_authorized_tenant_accounts", StringComparison.Ordinal),
            "both account statements must execute the migrated security-definer authorization function");
    }

    [Fact]
    public async Task AuthorizedTenantAccountsFunction_FailsClosedForBadCoordinatesAndHasExpectedAcl()
    {
        var missingSessionRows = await _context.Db.Database.SqlQuery<int>($"""
            SELECT COUNT(*)::integer AS "Value"
            FROM public.rc_api_authorized_tenant_accounts(
              {_scope.PortfolioId},
              NULL::uuid,
              {_scope.UserId},
              {_scope.AccessContextId},
              {_scope.AccessRevision},
              {new[] { CapabilityKeys.MoneyBalancesRead }})
            """).SingleAsync();
        var mismatchedSessionRows = await _context.Db.Database.SqlQuery<int>($"""
            SELECT COUNT(*)::integer AS "Value"
            FROM public.rc_api_authorized_tenant_accounts(
              {_scope.PortfolioId},
              {Guid.NewGuid()},
              {_scope.UserId},
              {_scope.AccessContextId},
              {_scope.AccessRevision},
              {new[] { CapabilityKeys.MoneyBalancesRead }})
            """).SingleAsync();

        missingSessionRows.Should().Be(0);
        mismatchedSessionRows.Should().Be(0);

        var metadata = await _context.Db.Database.SqlQuery<string>($"""
            SELECT concat_ws('|',
                owner.rolname,
                procedure.prosecdef::text,
                has_function_privilege('rentalcommand_api', procedure.oid, 'EXECUTE')::text,
                has_function_privilege('rentalcommand_engine', procedure.oid, 'EXECUTE')::text,
                COALESCE((
                    SELECT bool_or(acl.grantee = 0 AND acl.privilege_type = 'EXECUTE')
                    FROM aclexplode(procedure.proacl) AS acl), false)::text,
                pg_get_functiondef(procedure.oid)) AS "Value"
            FROM pg_proc AS procedure
            JOIN pg_roles AS owner ON owner.oid = procedure.proowner
            WHERE procedure.oid =
                'rc_api_authorized_tenant_accounts(integer,uuid,integer,integer,bigint,text[])'::regprocedure
            """).SingleAsync();

        metadata.Should().StartWith("rentalcommand_rls_authority|true|true|true|false|");
        metadata.Should().Contain("SECURITY DEFINER");
        metadata.Should().Contain("SET search_path TO 'pg_catalog', 'public'");
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
