using System.Text;
using System.Data.Common;
using FluentAssertions;
using Microsoft.EntityFrameworkCore.Diagnostics;
using RentalCommand.Api.Services.Domain;
using RentalCommand.Core.Authorization;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Data;
using RentalCommand.TestCommon;

namespace RentalCommand.Api.Tests.Domain;

/// <summary>
/// Covers the year-end accountant packet: that the gathered data reconciles with the existing
/// <see cref="ScheduleEService"/> computation for the same year, and that the generator produces a
/// non-empty PDF.
/// </summary>
[Collection(MigratedPostgreSqlCollection.Name)]
public class YearEndPacketTests : IAsyncLifetime
{
    private const int PortfolioId = 1;
    private const int Year = 2025;
    private const int ActorUserId = 1;

    private readonly MigratedPostgreSqlFixture _fixture;
    private readonly List<string> _commands = [];
    private MigratedPostgreSqlTestContext _context = null!;
    private RentalCommandDbContext _db = null!;
    private ScheduleEService _scheduleE = null!;
    private AccountingService _sut = null!;
    private WorkspaceReadScope _scope;

    public YearEndPacketTests(MigratedPostgreSqlFixture fixture)
    {
        _fixture = fixture;
    }

    public async Task InitializeAsync()
    {
        // QuestPDF community license (set in Program.cs at runtime; tests don't run Program).
        QuestPDF.Settings.License = QuestPDF.Infrastructure.LicenseType.Community;

        _context = await _fixture.CreateContextAsync(
            [new YearEndPacketRecordingCommandInterceptor(_commands)]);
        _db = _context.Db;
        var portfolio = _db.Portfolios.Single(candidate => candidate.Id == PortfolioId);
        portfolio.Name = "Frank's Rentals";
        portfolio.ManagementCompanyName = "Frank Property Co";
        _db.SaveChanges();
        _scope = _db.SeedAdministratorScope(PortfolioId, nameof(YearEndPacketTests));
        await _context.ActivateApiScopeAsync(_scope);

        _scheduleE = new ScheduleEService(_db);
        _sut = new AccountingService(_db, _scheduleE, new YearEndPacketPdfGenerator(), TimeProvider.System);
    }

    public async Task DisposeAsync()
    {
        await _context.DisposeAsync();
    }

    [Fact]
    public async Task GetYearEndPacketAsync_ReturnsNonEmptyPdfBytes()
    {
        SeedYear(Year);

        var pdf = await _sut.GetYearEndPacketAsync(_scope, Year, CancellationToken.None);

        pdf.Should().NotBeNullOrEmpty();
        // Valid PDFs start with the "%PDF" magic header.
        Encoding.ASCII.GetString(pdf, 0, 4).Should().Be("%PDF");
    }

    [Fact]
    public async Task GetYearEndPacketAsync_RendersEvenWithNoActivity()
    {
        // No data seeded beyond the portfolio: the packet should still render a valid PDF.
        var pdf = await _sut.GetYearEndPacketAsync(_scope, Year, CancellationToken.None);

        pdf.Should().NotBeNullOrEmpty();
        Encoding.ASCII.GetString(pdf, 0, 4).Should().Be("%PDF");
    }

    [Fact]
    public async Task GetYearEndPacketData_ScheduleETotalsMatchScheduleEService()
    {
        SeedYear(Year);

        _commands.Clear();

        var packet = await _sut.GetYearEndPacketDataAsync(_scope, Year, CancellationToken.None);
        var scheduleE = await _scheduleE.GetReportAsync(_scope, Year, ct: CancellationToken.None);

        // The packet must embed the exact same Schedule E numbers the standalone report/CSV produces.
        packet.ScheduleE.Year.Should().Be(scheduleE.Year);
        packet.ScheduleE.TotalRentalIncome.Should().Be(scheduleE.TotalRentalIncome);
        packet.ScheduleE.TotalExpenses.Should().Be(scheduleE.TotalExpenses);
        packet.ScheduleE.NetIncome.Should().Be(scheduleE.NetIncome);
        packet.ScheduleE.Properties.Should().HaveCount(scheduleE.Properties.Count);
    }

    [Fact]
    public async Task GetYearEndPacketData_BuildsPnLCashFlowAndRentRoll()
    {
        SeedYear(Year);

        var packet = await _sut.GetYearEndPacketDataAsync(_scope, Year, CancellationToken.None);

        packet.PortfolioName.Should().Be("Frank's Rentals");
        packet.ManagementCompanyName.Should().Be("Frank Property Co");

        // Per-property P&L: one property with $14,400 rent and $2,000 + $600 = $2,600 expenses.
        packet.Properties.Should().HaveCount(1);
        var pnl = packet.Properties[0];
        pnl.Income.Should().Be(14_400m);
        pnl.TotalExpenses.Should().Be(2_600m);
        pnl.Net.Should().Be(11_800m);
        pnl.ExpensesByCategory.Should().Contain(c => c.Category == "Repairs" && c.Amount == 2_000m);
        pnl.ExpensesByCategory.Should().Contain(c => c.Category == "Insurance" && c.Amount == 600m);

        // Cash flow: 12 months, year total money-in = rent collected, money-out = expenses paid.
        packet.CashFlow.Should().HaveCount(12);
        packet.CashFlowMoneyIn.Should().Be(14_400m);
        packet.CashFlowMoneyOut.Should().Be(2_600m);
        packet.CashFlowNet.Should().Be(11_800m);

        // Rent roll: the single occupied relationship, with a $1,200 past-due balance.
        packet.RentRoll.Should().HaveCount(1);
        var row = packet.RentRoll[0];
        row.TenantName.Should().Be("Maria Tenant");
        row.MonthlyRent.Should().Be(1_200m);
        row.PastDueBalance.Should().Be(1_200m);
        row.LeaseStatus.Should().Be("Expired");

        var sql = string.Join("\n---\n", _commands);
        sql.Should().Contain("EXISTS", "year-end packet P&L property filtering must happen in SQL");
        sql.Should().Contain("ORDER BY", "year-end packet rent-roll ordering must happen in SQL");
        (sql.Contains("SUM(", StringComparison.OrdinalIgnoreCase) || sql.Contains("ef_sum(", StringComparison.OrdinalIgnoreCase))
            .Should().BeTrue("year-end packet money and past-due totals must be summed in SQL");
    }

    [Fact]
    public async Task GetYearEndPacketData_ExcludesSecurityDepositsFromCashFlowMoneyIn()
    {
        var graph = SeedYear(Year);
        var paid = new DateTime(Year, 1, 2, 0, 0, 0, DateTimeKind.Utc);
        SeedSettledLedgerPair(
            graph,
            TenantLedgerEntryType.DepositCharge,
            1_200m,
            DateOnly.FromDateTime(paid),
            "security-deposit");

        var packet = await _sut.GetYearEndPacketDataAsync(_scope, Year, CancellationToken.None);

        packet.CashFlowMoneyIn.Should().Be(14_400m);
        packet.CashFlowNet.Should().Be(11_800m);
        packet.CashFlow.Single(m => m.Month == 1).MoneyIn.Should().Be(1_200m);
    }

    [Fact]
    public async Task GetYearEndPacketData_ProjectsRentRollPastDueWithRelationshipRowsInSql()
    {
        SeedYear(Year);
        _commands.Clear();

        var packet = await _sut.GetYearEndPacketDataAsync(_scope, Year, CancellationToken.None);

        packet.RentRoll.Should().ContainSingle();
        packet.RentRoll[0].PastDueBalance.Should().Be(1_200m);
        var rentRollSql = _commands.Single(sql =>
            sql.Contains("\"LeaseManagements\"", StringComparison.Ordinal)
            && sql.Contains("\"LeaseAgreements\"", StringComparison.Ordinal)
            && sql.Contains("\"PastDueAmount\"", StringComparison.Ordinal));
        rentRollSql.Should().Contain("LeaseManagements");
        rentRollSql.Should().Contain("LeaseAgreements");
        rentRollSql.Should().Contain("vw_lease_management_lifecycle");
        rentRollSql.Should().Contain("vw_unit_occupancy");
        rentRollSql.Should().Contain("vw_lease_agreement_status");
        rentRollSql.Should().Contain("vw_tenant_account_balances");
        rentRollSql.Should().Contain("PastDueAmount");
        rentRollSql.Should().Contain("ORDER BY");
    }

    [Fact]
    public async Task GetYearEndPacketData_ProjectsPropertyPnlRowsInSql()
    {
        SeedYear(Year);
        _commands.Clear();

        var packet = await _sut.GetYearEndPacketDataAsync(_scope, Year, CancellationToken.None);

        packet.Properties.Should().ContainSingle();
        var pnl = packet.Properties[0];
        pnl.PropertyName.Should().Be("Maple Street Duplex");
        pnl.Income.Should().Be(14_400m);
        pnl.TotalExpenses.Should().Be(2_600m);
        pnl.Net.Should().Be(11_800m);

        var propertyPnlCommands = _commands.Where(sql =>
                sql.Contains("TenantLedgerAllocations", StringComparison.Ordinal) &&
                sql.Contains("FROM \"Properties\"", StringComparison.Ordinal))
            .ToList();
        propertyPnlCommands.Should().NotBeEmpty();
        var propertyPnlSql = string.Join("\n---\n", propertyPnlCommands);
        propertyPnlSql.Should().Contain("TenantLedgerEntries");
        (propertyPnlSql.Contains("SUM(", StringComparison.OrdinalIgnoreCase) ||
         propertyPnlSql.Contains("ef_sum(", StringComparison.OrdinalIgnoreCase))
            .Should().BeTrue("property income must be summed in the database query");
        _commands.Where(IsStandaloneExpenseTotalByPropertyAggregate)
            .Should()
            .BeEmpty("packet property expense totals should be projected with each property row instead of joined from a materialized aggregate dictionary");
    }

    /// <summary>
    /// Seeds one canonical property/unit/tenant relationship, 12 settled $1,200 rent charges,
    /// a $2,000 repair and $600 insurance expense, and one open past-due rent charge.
    /// </summary>
    private CanonicalYearGraph SeedYear(int year)
    {
        var anchor = new DateTime(year, 1, 1, 12, 0, 0, DateTimeKind.Utc);

        var property = new Property
        {
            PortfolioId = PortfolioId,
            Name = "Maple Street Duplex",
            AddressLine1 = "10 Maple St",
            City = "Columbus",
            State = "OH",
            PostalCode = "43219",
            CreatedAt = anchor,
            UpdatedAt = anchor,
        };
        var unit = new Unit
        {
            Property = property,
            UnitNumber = "A",
            MarketRent = 1_200m,
            CreatedAt = anchor,
            UpdatedAt = anchor,
        };
        var tenant = new Tenant
        {
            PortfolioId = PortfolioId,
            FirstName = "Maria",
            LastName = "Tenant",
            CreatedAt = anchor,
            UpdatedAt = anchor,
        };
        _db.AddRange(property, unit, tenant);
        _db.SaveChanges();

        var management = new LeaseManagement
        {
            PublicId = Guid.NewGuid(),
            PortfolioId = PortfolioId,
            PropertyId = property.Id,
            UnitId = unit.Id,
            RelationshipNumber = "REL-YEAR-END-100",
            PlannedPossessionAtUtc = anchor,
            PossessionGivenAtUtc = anchor,
            CreatedAtUtc = anchor,
            CreatedByUserId = ActorUserId,
            UpdatedAtUtc = anchor,
            RowVersion = Guid.NewGuid(),
        };
        _db.LeaseManagements.Add(management);
        _db.SaveChanges();

        var termStart = new DateOnly(year, 1, 1);
        var termEnd = new DateOnly(year, 12, 31);
        var agreement = new LeaseAgreement
        {
            PublicId = Guid.NewGuid(),
            PortfolioId = PortfolioId,
            LeaseManagementId = management.Id,
            VersionNumber = 1,
            AgreementNumber = "AGR-100",
            ChangeType = LeaseAgreementChangeType.Initial,
            TermType = LeaseAgreementTermType.FixedTerm,
            TermStartOn = termStart,
            TermEndOn = termEnd,
            GoverningFromOn = termStart,
            BaseRentAmount = 1_200m,
            RentDueDay = 1,
            SecurityDepositObligation = 1_200m,
            LateFeeAmount = 50m,
            GracePeriodDays = 5,
            Currency = "USD",
            TermsSchemaVersion = 1,
            TermsPayload = "{}",
            DocumentSourceVersion = LegalDocumentSourceVersionTestData.BuiltIn(
                PortfolioId, ActorUserId, anchor),
            CreatedAtUtc = anchor,
            CreatedByUserId = ActorUserId,
            UpdatedAtUtc = anchor,
        };
        var party = new LeaseManagementParty
        {
            PortfolioId = PortfolioId,
            LeaseManagementId = management.Id,
            TenantId = tenant.Id,
            Role = LeaseManagementPartyRole.PrimaryTenant,
            EffectiveFrom = termStart,
            ChangeReason = "Canonical year-end fixture",
            CreatedAtUtc = anchor,
            CreatedByUserId = ActorUserId,
        };
        var account = new TenantAccount
        {
            PublicId = Guid.NewGuid(),
            PortfolioId = PortfolioId,
            LeaseManagementId = management.Id,
            AccountNumber = "TA-YEAR-END-100",
            Currency = "USD",
            OpenedAtUtc = anchor,
            CreatedAtUtc = anchor,
            CreatedByUserId = ActorUserId,
        };
        _db.AddRange(agreement, party, account);
        _db.SaveChanges();
        _db.MarkFullyExecuted(
            agreement,
            party,
            tenant,
            ActorUserId,
            anchor);

        var graph = new CanonicalYearGraph(property, unit, tenant, management, agreement, party, account);

        // 12 settled monthly rent charges in the year.
        for (var month = 1; month <= 12; month++)
        {
            SeedSettledLedgerPair(
                graph,
                TenantLedgerEntryType.RentCharge,
                1_200m,
                new DateOnly(year, month, 1),
                $"rent-{month}");
        }

        // One open past-due rent charge drives the rent-roll balance projection.
        _db.TenantLedgerEntries.Add(new TenantLedgerEntry
        {
            PublicId = Guid.NewGuid(),
            PortfolioId = PortfolioId,
            TenantAccountId = account.Id,
            EntryType = TenantLedgerEntryType.RentCharge,
            Direction = TenantLedgerDirection.Debit,
            Amount = 1_200m,
            Currency = "USD",
            EffectiveOn = new DateOnly(year, 6, 1),
            DueOn = new DateOnly(year, 6, 1),
            PostedAtUtc = anchor,
            Description = "Open rent charge",
            BusinessKey = "year-end:open-rent",
            LeaseAgreementId = agreement.Id,
            CreatedByUserId = ActorUserId,
        });
        // Expenses in-year.
        _db.Expenses.Add(new Expense
        {
            PortfolioId = PortfolioId,
            OperationalScope = ExpenseOperationalScope.Property,
            PropertyId = property.Id,
            Category = ScheduleECategory.Repairs,
            Description = "Roof repair",
            Status = ExpenseStatus.Paid,
            Amount = 2_000m,
            IncurredAt = new DateTime(year, 3, 15, 0, 0, 0, DateTimeKind.Utc),
            PaidAt = new DateTime(year, 3, 20, 0, 0, 0, DateTimeKind.Utc),
            CreatedAt = anchor,
            UpdatedAt = anchor,
        });
        _db.Expenses.Add(new Expense
        {
            PortfolioId = PortfolioId,
            OperationalScope = ExpenseOperationalScope.Property,
            PropertyId = property.Id,
            Category = ScheduleECategory.Insurance,
            Description = "Annual policy",
            Status = ExpenseStatus.Paid,
            Amount = 600m,
            IncurredAt = new DateTime(year, 1, 10, 0, 0, 0, DateTimeKind.Utc),
            PaidAt = new DateTime(year, 1, 10, 0, 0, 0, DateTimeKind.Utc),
            CreatedAt = anchor,
            UpdatedAt = anchor,
        });
        _db.SaveChanges();
        return graph;
    }

    private void SeedSettledLedgerPair(
        CanonicalYearGraph graph,
        TenantLedgerEntryType chargeType,
        decimal amount,
        DateOnly effectiveOn,
        string businessKey)
    {
        var postedAt = effectiveOn.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
        var charge = new TenantLedgerEntry
        {
            PublicId = Guid.NewGuid(),
            PortfolioId = PortfolioId,
            TenantAccountId = graph.Account.Id,
            EntryType = chargeType,
            Direction = TenantLedgerDirection.Debit,
            Amount = amount,
            Currency = "USD",
            EffectiveOn = effectiveOn,
            DueOn = effectiveOn,
            PostedAtUtc = postedAt,
            Description = chargeType.ToString(),
            BusinessKey = $"year-end:{businessKey}:charge",
            LeaseAgreementId = graph.Agreement.Id,
            CreatedByUserId = ActorUserId,
        };
        var receipt = new TenantLedgerEntry
        {
            PublicId = Guid.NewGuid(),
            PortfolioId = PortfolioId,
            TenantAccountId = graph.Account.Id,
            EntryType = TenantLedgerEntryType.PaymentReceipt,
            Direction = TenantLedgerDirection.Credit,
            Amount = amount,
            Currency = "USD",
            EffectiveOn = effectiveOn,
            PostedAtUtc = postedAt,
            Description = "Payment receipt",
            BusinessKey = $"year-end:{businessKey}:receipt",
            CreatedByUserId = ActorUserId,
        };
        _db.TenantLedgerEntries.AddRange(charge, receipt);
        _db.SaveChanges();

        _db.TenantLedgerAllocations.Add(new TenantLedgerAllocation
        {
            PortfolioId = PortfolioId,
            TenantAccountId = graph.Account.Id,
            DebitEntryId = charge.Id,
            CreditEntryId = receipt.Id,
            Amount = amount,
            AllocatedAtUtc = postedAt,
            BusinessKey = $"year-end:{businessKey}:allocation",
            CreatedByUserId = ActorUserId,
        });
        _db.SaveChanges();
    }

    private static bool IsStandaloneExpenseTotalByPropertyAggregate(string sql) =>
        sql.TrimStart().StartsWith("SELECT \"e\".\"PropertyId\"", StringComparison.Ordinal) &&
        sql.Contains("FROM \"Expenses\" AS \"e\"", StringComparison.Ordinal) &&
        sql.Contains("GROUP BY \"e\".\"PropertyId\"", StringComparison.Ordinal) &&
        !sql.Contains("\"e\".\"Category\"", StringComparison.Ordinal);

    private sealed record CanonicalYearGraph(
        Property Property,
        Unit Unit,
        Tenant Tenant,
        LeaseManagement Management,
        LeaseAgreement Agreement,
        LeaseManagementParty Party,
        TenantAccount Account);
}

internal sealed class YearEndPacketRecordingCommandInterceptor(List<string> commands) : DbCommandInterceptor
{
    public override InterceptionResult<DbDataReader> ReaderExecuting(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<DbDataReader> result)
    {
        commands.Add(command.CommandText);
        return base.ReaderExecuting(command, eventData, result);
    }

    public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<DbDataReader> result,
        CancellationToken cancellationToken = default)
    {
        commands.Add(command.CommandText);
        return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
    }
}
