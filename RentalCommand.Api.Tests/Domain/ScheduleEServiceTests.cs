using System.Data.Common;
using FluentAssertions;
using Microsoft.EntityFrameworkCore.Diagnostics;
using RentalCommand.Api.Services.Domain;
using RentalCommand.Core.Authorization;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Services;
using RentalCommand.Data;
using RentalCommand.TestCommon;

namespace RentalCommand.Api.Tests.Domain;

/// <summary>
/// Schedule E is the tax return: these tests pin spec §10 with the §18 corrections — taxable income
/// uses actual cash received (no deposits), deducts mortgage INTEREST (from the loan split, principal
/// excluded) and computed DEPRECIATION, and applies the deterministic legacy double-count exclusion
/// (a loan present → drop manual MortgageInterest; computed depreciation → drop manual Depreciation).
/// </summary>
[Collection(MigratedPostgreSqlCollection.Name)]
public class ScheduleEServiceTests : IAsyncLifetime
{
    private const int PortfolioId = 1;
    private const int Year = 2025;

    private readonly MigratedPostgreSqlFixture _fixture;
    private readonly List<string> _commands = [];
    private MigratedPostgreSqlTestContext _context = null!;
    private RentalCommandDbContext _db = null!;
    private ScheduleEService _sut = null!;
    private WorkspaceReadScope _scope;

    public ScheduleEServiceTests(MigratedPostgreSqlFixture fixture)
    {
        _fixture = fixture;
    }

    public async Task InitializeAsync()
    {
        _context = await _fixture.CreateContextAsync(
            [new ScheduleERecordingCommandInterceptor(_commands)]);
        _db = _context.Db;
        _scope = _db.SeedAdministratorScope(PortfolioId, nameof(ScheduleEServiceTests));
        await _context.ActivateApiScopeAsync(_scope);

        _sut = new ScheduleEService(_db);
    }

    public async Task DisposeAsync()
    {
        await _context.DisposeAsync();
    }

    private static DateTime D(int y, int m, int d) => new(y, m, d, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task GetReportAsync_SelectedPropertyFiltersBeforeSqlAggregation()
    {
        var selected = new Property
        {
            PortfolioId = PortfolioId,
            Name = "Selected",
            AddressLine1 = "1 Selected St",
            City = "Columbus",
            State = "OH",
            PostalCode = "43215",
            CreatedAt = D(Year, 1, 1),
            UpdatedAt = D(Year, 1, 1),
        };
        var excluded = new Property
        {
            PortfolioId = PortfolioId,
            Name = "Excluded",
            AddressLine1 = "2 Excluded St",
            City = "Columbus",
            State = "OH",
            PostalCode = "43215",
            CreatedAt = D(Year, 1, 1),
            UpdatedAt = D(Year, 1, 1),
        };
        _db.Properties.AddRange(selected, excluded);
        _db.SaveChanges();
        _db.Expenses.AddRange(
            new Expense
            {
                PortfolioId = PortfolioId,
                OperationalScope = ExpenseOperationalScope.Property,
                PropertyId = selected.Id,
                Category = ScheduleECategory.Repairs,
                Description = "Selected repair",
                Status = ExpenseStatus.Paid,
                Amount = 125m,
                IncurredAt = D(Year, 3, 1),
                CreatedAt = D(Year, 3, 1),
                UpdatedAt = D(Year, 3, 1),
            },
            new Expense
            {
                PortfolioId = PortfolioId,
                OperationalScope = ExpenseOperationalScope.Property,
                PropertyId = excluded.Id,
                Category = ScheduleECategory.Repairs,
                Description = "Excluded repair",
                Status = ExpenseStatus.Paid,
                Amount = 900m,
                IncurredAt = D(Year, 3, 1),
                CreatedAt = D(Year, 3, 1),
                UpdatedAt = D(Year, 3, 1),
            });
        _db.SaveChanges();
        _commands.Clear();

        var report = await _sut.GetReportAsync(
            _scope,
            Year,
            propertyId: selected.Id,
            ct: CancellationToken.None);

        report.Properties.Should().ContainSingle().Which.PropertyId.Should().Be(selected.Id);
        report.TotalExpenses.Should().Be(125m);
        _commands.Should().OnlyContain(sql =>
                !sql.Contains("FROM \"Expenses\"", StringComparison.OrdinalIgnoreCase) ||
                sql.Contains("\"PropertyId\"", StringComparison.OrdinalIgnoreCase),
            "every expense aggregate must retain the selected-property predicate in translated SQL");
    }

    [Fact]
    public async Task GetReportAsync_WorkspaceAdministratorAttributesOperationalScopeAndReconcilesUnallocatedActivityInSql()
    {
        var property = SeedProperty("Attributed");
        var unit = SeedUnit(property, "1A");
        var workOrder = SeedWorkOrder(property, unit, "Furnace repair");
        var applicationAccount = SeedApplicationFinancialAccount();

        SeedApplicationIncome(applicationAccount, 100m, propertyId: property.Id);
        SeedApplicationIncome(applicationAccount, 200m, unitId: unit.Id);
        SeedApplicationIncome(applicationAccount, 300m);
        SeedExpense(10m, ScheduleECategory.Repairs, "Direct property", propertyId: property.Id);
        SeedExpense(20m, ScheduleECategory.Insurance, "Unit scoped", unitId: unit.Id);
        SeedExpense(30m, ScheduleECategory.Supplies, "Work order scoped", workOrderId: workOrder.Id);
        SeedExpense(40m, ScheduleECategory.Other, "Needs allocation");
        _db.SaveChanges();
        _commands.Clear();

        var report = await _sut.GetReportAsync(_scope, Year, ct: CancellationToken.None);

        var propertyReport = report.Properties.Should().ContainSingle().Subject;
        propertyReport.PropertyId.Should().Be(property.Id);
        propertyReport.RentalIncome.Should().Be(300m,
            "direct-property and Unit-attributable application income belongs on the property row");
        propertyReport.TotalExpenses.Should().Be(60m,
            "direct Property, Unit-to-Property, and WorkOrder-to-Property expenses are attributable");
        propertyReport.ExpensesByCategory.Should().BeEquivalentTo(
            [
                new { Category = nameof(ScheduleECategory.Insurance), Amount = 20m },
                new { Category = nameof(ScheduleECategory.Repairs), Amount = 10m },
                new { Category = nameof(ScheduleECategory.Supplies), Amount = 30m },
            ]);

        report.TotalRentalIncome.Should().Be(300m,
            "allocated IRS property totals must not absorb unresolved activity");
        report.TotalExpenses.Should().Be(60m);
        report.NetIncome.Should().Be(240m);
        report.UnallocatedActivity.CanView.Should().BeTrue();
        report.UnallocatedActivity.RequiresAllocation.Should().BeTrue();
        report.UnallocatedActivity.IncomeEntryCount.Should().Be(1);
        report.UnallocatedActivity.RentalIncome.Should().Be(300m);
        report.UnallocatedActivity.ExpenseCount.Should().Be(1);
        report.UnallocatedActivity.TotalExpenses.Should().Be(40m);
        report.UnallocatedActivity.ExpensesByCategory.Should().ContainSingle()
            .Which.Should().BeEquivalentTo(new
            {
                Category = nameof(ScheduleECategory.Other),
                Amount = 40m,
            });
        report.UnallocatedActivity.Warning.Should().NotBeNullOrWhiteSpace();
        report.ReconciledTotalRentalIncome.Should().Be(600m);
        report.ReconciledTotalExpenses.Should().Be(100m);
        report.ReconciledNetIncome.Should().Be(500m);

        _commands.Should().ContainSingle(
            "authorization, operational attribution, grouping, totals, and ordering must execute as one SQL statement");
        var sql = _commands.Single();
        sql.Should().Contain("ApplicationFinancialEntries");
        sql.Should().Contain("Units", "Unit attribution must be expressed in SQL");
        sql.Should().Contain("WorkOrders", "WorkOrder attribution must be expressed in SQL");
        sql.Should().Contain(
            "rc_api_effective_capability_scopes",
            "current administrator authorization must be evaluated by the canonical PostgreSQL capability function");
        sql.Should().Contain("GROUP BY", "category aggregation must be DB-side");
        sql.Should().Contain("ORDER BY", "flat report ordering must be DB-side");
        (sql.Contains("SUM(", StringComparison.OrdinalIgnoreCase) ||
         sql.Contains("ef_sum(", StringComparison.OrdinalIgnoreCase))
            .Should().BeTrue("allocated and unallocated money totals must be summed in SQL");
    }

    [Fact]
    public async Task GetReportAsync_SelectedPropertyManagerCannotObserveUnallocatedOrUnauthorizedDecoyActivity()
    {
        var allowed = SeedProperty("Allowed");
        var decoy = SeedProperty("Unauthorized decoy");
        var decoyUnit = SeedUnit(decoy, "D1");
        var decoyWorkOrder = SeedWorkOrder(decoy, decoyUnit, "Unauthorized work order");
        var applicationAccount = SeedApplicationFinancialAccount();
        SeedApplicationIncome(applicationAccount, 125m, propertyId: allowed.Id);
        SeedApplicationIncome(applicationAccount, 9_000m, unitId: decoyUnit.Id);
        SeedApplicationIncome(applicationAccount, 500m);
        SeedExpense(25m, ScheduleECategory.Repairs, "Allowed repair", propertyId: allowed.Id);
        SeedExpense(900m, ScheduleECategory.Repairs, "Decoy repair", workOrderId: decoyWorkOrder.Id);
        SeedExpense(75m, ScheduleECategory.Other, "Unallocated expense");
        _db.SaveChanges();
        var selectedPropertyScope = SeedSelectedPropertyScope(allowed.Id);
        await _context.ActivateApiScopeAsync(selectedPropertyScope);
        _commands.Clear();

        var report = await _sut.GetReportAsync(
            selectedPropertyScope,
            Year,
            ct: CancellationToken.None);

        var propertyReport = report.Properties.Should().ContainSingle().Subject;
        propertyReport.PropertyId.Should().Be(allowed.Id);
        propertyReport.RentalIncome.Should().Be(125m);
        propertyReport.TotalExpenses.Should().Be(25m);
        report.TotalRentalIncome.Should().Be(125m);
        report.TotalExpenses.Should().Be(25m);
        report.NetIncome.Should().Be(100m);
        report.UnallocatedActivity.CanView.Should().BeFalse();
        report.UnallocatedActivity.RequiresAllocation.Should().BeFalse();
        report.UnallocatedActivity.IncomeEntryCount.Should().Be(0);
        report.UnallocatedActivity.RentalIncome.Should().Be(0m);
        report.UnallocatedActivity.ExpenseCount.Should().Be(0);
        report.UnallocatedActivity.TotalExpenses.Should().Be(0m);
        report.UnallocatedActivity.ExpensesByCategory.Should().BeEmpty();
        report.UnallocatedActivity.Warning.Should().BeEmpty();
        report.ReconciledTotalRentalIncome.Should().Be(report.TotalRentalIncome);
        report.ReconciledTotalExpenses.Should().Be(report.TotalExpenses);
        report.ReconciledNetIncome.Should().Be(report.NetIncome);

        _commands.Should().ContainSingle(
            "limited authorization and all report aggregation must remain one bounded translated query");
        var sql = _commands.Single();
        // The selected-property boundary is enforced by PostgreSQL RLS under rentalcommand_api,
        // so its policy body is intentionally not repeated in the client command text. The
        // allowed/decoy assertions above are the end-to-end authorization proof.
        sql.Should().Contain("GROUP BY");
        sql.Should().Contain("ORDER BY");
    }

    [Fact]
    public async Task GetReportAsync_DeductsInterestAndDepreciation_ExcludesPrincipalAndDeposits_NoDoubleCount()
    {
        // Property with a depreciation basis: building 240k → full-year 8,727.27 (in service 2020).
        var property = new Property
        {
            PortfolioId = PortfolioId,
            Name = "Maple",
            AddressLine1 = "1 Maple",
            City = "Columbus",
            State = "OH",
            PostalCode = "43215",
            PurchasePrice = 300_000m,
            LandValue = 60_000m,
            InServiceDate = D(2020, 1, 1),
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        _db.Properties.Add(property);
        _db.SaveChanges();

        var unit = new Unit { PortfolioId = PortfolioId, PropertyId = property.Id, UnitNumber = "1", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
        var tenant = new Tenant { PortfolioId = PortfolioId, FirstName = "Ann", LastName = "Acre", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
        _db.Units.Add(unit);
        _db.Tenants.Add(tenant);
        _db.SaveChanges();

        var agreement = SeedAgreement(property, unit, tenant, "L-1", 1_000m, D(2025, 1, 1), D(2026, 1, 1));

        // Income: 12,000 rent (paid) + a 1,500 deposit (EXCLUDED).
        for (var m = 1; m <= 12; m++)
            SeedChargeAndReceipt(agreement, TenantLedgerEntryType.RentCharge, 1_000m, D(Year, m, 1), 1_000m);
        SeedChargeAndReceipt(agreement, TenantLedgerEntryType.DepositCharge, 1_500m, D(Year, 1, 1), 1_500m);

        // Expenses: 1,000 repairs (counts) + a 5,000 manual MortgageInterest + 3,000 manual Depreciation
        // — both must be EXCLUDED because the property has a modeled loan + computed depreciation.
        _db.Expenses.Add(new Expense { PortfolioId = PortfolioId, OperationalScope = ExpenseOperationalScope.Property, PropertyId = property.Id, Category = ScheduleECategory.Repairs, Description = "Repair", Status = ExpenseStatus.Paid, Amount = 1_000m, IncurredAt = D(Year, 6, 1), CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow });
        _db.Expenses.Add(new Expense { PortfolioId = PortfolioId, OperationalScope = ExpenseOperationalScope.Property, PropertyId = property.Id, Category = ScheduleECategory.MortgageInterest, Description = "Manual interest", Status = ExpenseStatus.Paid, Amount = 5_000m, IncurredAt = D(Year, 6, 1), CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow });
        _db.Expenses.Add(new Expense { PortfolioId = PortfolioId, OperationalScope = ExpenseOperationalScope.Property, PropertyId = property.Id, Category = ScheduleECategory.Depreciation, Description = "Manual depr", Status = ExpenseStatus.Paid, Amount = 3_000m, IncurredAt = D(Year, 6, 1), CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow });
        _db.SaveChanges();

        // A loan with 2 payments this year: interest 600 + 590 = 1,190 deducted; principal NOT deducted.
        var loan = new Loan
        {
            PortfolioId = PortfolioId,
            PropertyId = property.Id,
            Lender = "Bank",
            OriginalAmount = 100_000m,
            CurrentBalance = 100_000m,
            AnnualInterestRatePct = 6m,
            TermMonths = 360,
            StartDate = D(Year, 1, 1),
            DayOfMonthDue = 1,
            MonthlyPrincipalInterest = 600m,
            Status = LoanStatus.Active,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        _db.Loans.Add(loan);
        _db.SaveChanges();
        _db.LoanPayments.Add(new LoanPayment { PortfolioId = PortfolioId, LoanId = loan.Id, PeriodKey = $"{Year}-01", DueDate = D(Year, 1, 1), InterestAmount = 600m, PrincipalAmount = 100m, EscrowAmount = 0m, TotalAmount = 700m, BalanceAfter = 99_900m, Status = LoanPaymentStatus.Scheduled, CreatedAt = DateTime.UtcNow });
        _db.LoanPayments.Add(new LoanPayment { PortfolioId = PortfolioId, LoanId = loan.Id, PeriodKey = $"{Year}-02", DueDate = D(Year, 2, 1), InterestAmount = 590m, PrincipalAmount = 110m, EscrowAmount = 0m, TotalAmount = 700m, BalanceAfter = 99_790m, Status = LoanPaymentStatus.Scheduled, CreatedAt = DateTime.UtcNow });
        _db.SaveChanges();

        var report = await _sut.GetReportAsync(_scope, Year, ct: CancellationToken.None);

        report.Properties.Should().HaveCount(1);
        var p = report.Properties[0];

        p.RentalIncome.Should().Be(12_000m, "deposits are excluded from taxable income");
        p.MortgageInterest.Should().Be(1_190m, "interest comes from the loan split (principal excluded)");
        p.Depreciation.Should().Be(8_727.27m, "depreciation is computed from basis (240k / 27.5)");

        // Deductions = 1,000 repairs + 1,190 interest + 8,727.27 depreciation = 10,917.27.
        // The manual 5,000 interest + 3,000 depreciation are EXCLUDED (no double-count).
        p.TotalExpenses.Should().Be(10_917.27m);
        p.NetIncome.Should().Be(12_000m - 10_917.27m);

        // The category breakdown carries the modeled interest + depreciation, not the manual ones.
        p.ExpensesByCategory.Should().Contain(c => c.Category == "MortgageInterest" && c.Amount == 1_190m);
        p.ExpensesByCategory.Should().Contain(c => c.Category == "Depreciation" && c.Amount == 8_727.27m);
        p.ExpensesByCategory.Should().Contain(c => c.Category == "Repairs" && c.Amount == 1_000m);
        // Exactly one MortgageInterest + one Depreciation line (the manual ones were dropped).
        p.ExpensesByCategory.Count(c => c.Category == "MortgageInterest").Should().Be(1);
        p.ExpensesByCategory.Count(c => c.Category == "Depreciation").Should().Be(1);
    }

    [Fact]
    public async Task GetReportAsync_PartialRent_UsesAmountPaid()
    {
        var property = new Property { PortfolioId = PortfolioId, Name = "Oak", AddressLine1 = "2 Oak", City = "C", State = "OH", PostalCode = "43215", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
        _db.Properties.Add(property);
        _db.SaveChanges();
        var unit = new Unit { PortfolioId = PortfolioId, PropertyId = property.Id, UnitNumber = "1", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
        var tenant = new Tenant { PortfolioId = PortfolioId, FirstName = "Bo", LastName = "Birch", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
        _db.Units.Add(unit); _db.Tenants.Add(tenant); _db.SaveChanges();
        var agreement = SeedAgreement(property, unit, tenant, "L-2", 1_000m, D(2025, 1, 1), D(2026, 1, 1));

        // 1,000 paid + a partial paying 250 of 1,000 → taxable income 1,250 (not 2,000).
        SeedChargeAndReceipt(agreement, TenantLedgerEntryType.RentCharge, 1_000m, D(Year, 1, 1), 1_000m);
        SeedChargeAndReceipt(agreement, TenantLedgerEntryType.RentCharge, 1_000m, D(Year, 2, 1), 250m);
        _db.SaveChanges();

        _commands.Clear();

        var report = await _sut.GetReportAsync(_scope, Year, ct: CancellationToken.None);
        report.Properties.Single().RentalIncome.Should().Be(1_250m);

        var sql = string.Join("\n---\n", _commands);
        sql.Should().Contain("GROUP BY", "Schedule E income and category totals must be grouped in SQL");
        (sql.Contains("SUM(", StringComparison.OrdinalIgnoreCase) || sql.Contains("ef_sum(", StringComparison.OrdinalIgnoreCase))
            .Should().BeTrue("Schedule E raw money totals must be summed in SQL");

        var orderedPropertySql = _commands.FirstOrDefault(command =>
            command.Contains("FROM \"Properties\"", StringComparison.OrdinalIgnoreCase) &&
            command.Contains("ORDER BY", StringComparison.OrdinalIgnoreCase));
        orderedPropertySql.Should().NotBeNull("Schedule E property ordering must come from SQL before DTO shaping");
    }

    [Fact]
    public async Task GetReportAsync_CapitalizedExpenseDropsFromRepairsAndAddsAssetDepreciation()
    {
        var property = new Property
        {
            PortfolioId = PortfolioId,
            Name = "Roof House",
            AddressLine1 = "4 Roof",
            City = "Columbus",
            State = "OH",
            PostalCode = "43215",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        _db.Properties.Add(property);
        _db.SaveChanges();

        var roof = new CapitalAsset
        {
            PortfolioId = PortfolioId,
            PropertyId = property.Id,
            Description = "Roof replacement",
            CostBasis = 9_900m,
            InServiceDate = D(Year, 8, 1),
            Method = DepreciationMethod.StraightLine,
            RecoveryYears = RecoveryClass.ResidentialBuilding,
            Convention = DepreciationConvention.MidMonth,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        _db.CapitalAssets.Add(roof);
        _db.SaveChanges();

        _db.Expenses.AddRange(
            new Expense
            {
                PortfolioId = PortfolioId,
                OperationalScope = ExpenseOperationalScope.Property,
                PropertyId = property.Id,
                CapitalizedAssetId = roof.Id,
                Category = ScheduleECategory.Repairs,
                Description = "Capitalized roof invoice",
                Status = ExpenseStatus.Paid,
                Amount = 9_900m,
                IncurredAt = D(Year, 8, 1),
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            },
            new Expense
            {
                PortfolioId = PortfolioId,
                OperationalScope = ExpenseOperationalScope.Property,
                PropertyId = property.Id,
                Category = ScheduleECategory.Repairs,
                Description = "Small repair",
                Status = ExpenseStatus.Paid,
                Amount = 100m,
                IncurredAt = D(Year, 8, 2),
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            });
        _db.SaveChanges();

        var report = await _sut.GetReportAsync(_scope, Year, ct: CancellationToken.None);

        var p = report.Properties.Single();
        p.Depreciation.Should().Be(135.00m);
        p.TotalExpenses.Should().Be(235.00m);
        p.ExpensesByCategory.Should().Contain(c => c.Category == "Depreciation" && c.Amount == 135.00m);
        p.ExpensesByCategory.Should().Contain(c => c.Category == "Repairs" && c.Amount == 100m);
        p.ExpensesByCategory.Should().NotContain(c => c.Category == "Repairs" && c.Amount == 9_900m);
    }

    [Fact]
    public async Task GetReportAsync_ProjectsPropertyRowFactsInSql()
    {
        var property = new Property
        {
            PortfolioId = PortfolioId,
            Name = "Cedar",
            AddressLine1 = "3 Cedar",
            City = "Columbus",
            State = "OH",
            PostalCode = "43215",
            PurchasePrice = 300_000m,
            LandValue = 60_000m,
            InServiceDate = D(2020, 1, 1),
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        _db.Properties.Add(property);
        _db.SaveChanges();

        var unit = new Unit { PortfolioId = PortfolioId, PropertyId = property.Id, UnitNumber = "1", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
        var tenant = new Tenant { PortfolioId = PortfolioId, FirstName = "Cal", LastName = "Cedar", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
        _db.Units.Add(unit);
        _db.Tenants.Add(tenant);
        _db.SaveChanges();

        var agreement = SeedAgreement(property, unit, tenant, "L-3", 1_000m, D(Year, 1, 1), D(Year + 1, 1, 1));
        SeedChargeAndReceipt(agreement, TenantLedgerEntryType.RentCharge, 1_000m, D(Year, 1, 1), 1_000m, D(Year, 1, 5));
        _db.Expenses.Add(new Expense
        {
            PortfolioId = PortfolioId,
            OperationalScope = ExpenseOperationalScope.Property,
            PropertyId = property.Id,
            Category = ScheduleECategory.Repairs,
            Description = "Repair",
            Status = ExpenseStatus.Paid,
            Amount = 250m,
            IncurredAt = D(Year, 2, 1),
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        var loan = new Loan
        {
            PortfolioId = PortfolioId,
            PropertyId = property.Id,
            Lender = "Bank",
            OriginalAmount = 100_000m,
            CurrentBalance = 100_000m,
            AnnualInterestRatePct = 6m,
            TermMonths = 360,
            StartDate = D(Year, 1, 1),
            DayOfMonthDue = 1,
            MonthlyPrincipalInterest = 600m,
            Status = LoanStatus.Active,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        _db.Loans.Add(loan);
        _db.SaveChanges();
        _db.LoanPayments.Add(new LoanPayment
        {
            PortfolioId = PortfolioId,
            LoanId = loan.Id,
            PeriodKey = $"{Year}-01",
            DueDate = D(Year, 1, 1),
            InterestAmount = 400m,
            PrincipalAmount = 100m,
            EscrowAmount = 0m,
            TotalAmount = 500m,
            BalanceAfter = 99_900m,
            Status = LoanPaymentStatus.Scheduled,
            CreatedAt = DateTime.UtcNow,
        });
        _db.SaveChanges();
        _commands.Clear();

        var report = await _sut.GetReportAsync(_scope, Year, ct: CancellationToken.None);

        var row = report.Properties.Should().ContainSingle().Subject;
        row.RentalIncome.Should().Be(1_000m);
        row.MortgageInterest.Should().Be(400m);
        row.TotalExpenses.Should().Be(250m + 400m + 8_727.27m);

        _commands.Should().ContainSingle(
            "Schedule E must return its already-grouped flat property/category rows with one translated SQL statement");
        var scheduleESql = _commands.Single();
        scheduleESql.Should().Contain("LoanPaymentCorrections",
            "modeled mortgage interest must select the latest correction in the same SQL statement");
        scheduleESql.Should().Contain("LEFT JOIN",
            "income-only properties must survive the flat join without a synthetic client-side category");
        scheduleESql.Should().Contain("GROUP BY",
            "category amounts must be grouped before the flat rows are materialized");
        scheduleESql.Should().Contain("ORDER BY",
            "property and category order must be established by SQL before DTO reshaping");

        _commands.Where(IsStandaloneIncomeByPropertyAggregate)
            .Should()
            .BeEmpty("Schedule E property income should be projected with each property row instead of joined from a materialized aggregate dictionary");
        _commands.Where(IsStandaloneInterestByPropertyAggregate)
            .Should()
            .BeEmpty("modeled interest should be projected with each property row instead of joined from a materialized aggregate dictionary");
        _commands.Where(IsStandaloneDeductibleExpenseByPropertyAggregate)
            .Should()
            .BeEmpty("deductible expenses should be projected with each property row instead of joined from a materialized aggregate dictionary");
        _commands.Where(IsStandaloneLoanPropertyIdScan)
            .Should()
            .BeEmpty("loan-backed mortgage-interest exclusions should stay as a SQL subquery instead of a materialized property-id set");

        var depreciationSql = _commands.Where(IsDepreciationAggregate).ToList();
        depreciationSql.Should().ContainSingle(
            "property and capital-asset depreciation must be unioned, filtered, grouped, and totaled by one SQL statement");
        depreciationSql[0].Should().Contain("UNION ALL");
        depreciationSql[0].Should().Contain("GROUP BY");
        (depreciationSql[0].Contains("SUM(", StringComparison.OrdinalIgnoreCase) ||
         depreciationSql[0].Contains("ef_sum(", StringComparison.OrdinalIgnoreCase))
            .Should().BeTrue("both per-property and report depreciation totals must be summed in SQL");
    }

    private static bool IsStandaloneIncomeByPropertyAggregate(string sql) =>
        sql.Contains("FROM \"TenantLedgerAllocations\"", StringComparison.Ordinal) &&
        sql.Contains("GROUP BY", StringComparison.Ordinal) &&
        !sql.Contains("FROM \"Properties\"", StringComparison.Ordinal);

    private static bool IsStandaloneInterestByPropertyAggregate(string sql) =>
        sql.TrimStart().StartsWith("SELECT \"l1\".\"PropertyId\"", StringComparison.Ordinal) &&
        sql.Contains("FROM \"LoanPayments\" AS \"l\"", StringComparison.Ordinal) &&
        sql.Contains("GROUP BY \"l1\".\"PropertyId\"", StringComparison.Ordinal);

    private static bool IsStandaloneDeductibleExpenseByPropertyAggregate(string sql) =>
        sql.TrimStart().StartsWith("SELECT \"e0\".\"Key\" AS \"PropertyId\"", StringComparison.Ordinal) &&
        sql.Contains("FROM \"Expenses\" AS \"e\"", StringComparison.Ordinal) &&
        sql.Contains("GROUP BY \"e0\".\"Key\"", StringComparison.Ordinal) &&
        !sql.Contains("\"e\".\"Category\"", StringComparison.Ordinal);

    private static bool IsStandaloneLoanPropertyIdScan(string sql) =>
        sql.TrimStart().StartsWith("SELECT DISTINCT \"l\".\"PropertyId\"", StringComparison.Ordinal) &&
        sql.Contains("FROM \"Loans\" AS \"l\"", StringComparison.Ordinal);

    private static bool IsDepreciationAggregate(string sql) =>
        sql.Contains("FROM \"Properties\"", StringComparison.Ordinal) &&
        sql.Contains("CapitalAssets", StringComparison.Ordinal) &&
        sql.Contains("PurchasePrice", StringComparison.Ordinal) &&
        sql.Contains("RecoveryYears", StringComparison.Ordinal);

    private Property SeedProperty(string name)
    {
        var property = new Property
        {
            PortfolioId = PortfolioId,
            Name = name,
            AddressLine1 = $"{Guid.NewGuid():N} Main St",
            City = "Columbus",
            State = "OH",
            PostalCode = "43215",
            CreatedAt = D(Year, 1, 1),
            UpdatedAt = D(Year, 1, 1),
        };
        _db.Properties.Add(property);
        _db.SaveChanges();
        return property;
    }

    private Unit SeedUnit(Property property, string number)
    {
        var unit = new Unit
        {
            PortfolioId = PortfolioId,
            PropertyId = property.Id,
            UnitNumber = number,
            CreatedAt = D(Year, 1, 1),
            UpdatedAt = D(Year, 1, 1),
        };
        _db.Units.Add(unit);
        _db.SaveChanges();
        return unit;
    }

    private WorkOrder SeedWorkOrder(Property property, Unit unit, string title)
    {
        var workOrder = new WorkOrder
        {
            PortfolioId = PortfolioId,
            PropertyId = property.Id,
            UnitId = unit.Id,
            Title = title,
            Description = title,
            Category = "General",
            Priority = WorkOrderPriority.Normal,
            Status = WorkOrderStatus.New,
            RequestedAt = D(Year, 2, 1),
            UpdatedAt = D(Year, 2, 1),
        };
        _db.WorkOrders.Add(workOrder);
        _db.SaveChanges();
        return workOrder;
    }

    private ApplicationFinancialAccount SeedApplicationFinancialAccount()
    {
        var application = new RentalApplication
        {
            PortfolioId = PortfolioId,
            FirstName = "Alex",
            LastName = "Applicant",
            Status = ApplicationStatus.Submitted,
            SubmittedAtUtc = D(Year, 1, 1),
            CreatedAt = D(Year, 1, 1),
            UpdatedAt = D(Year, 1, 1),
        };
        _db.RentalApplications.Add(application);
        _db.SaveChanges();

        var account = new ApplicationFinancialAccount
        {
            PublicId = Guid.NewGuid(),
            PortfolioId = PortfolioId,
            RentalApplicationId = application.Id,
            Currency = "USD",
            OpenedAtUtc = D(Year, 1, 1),
            CreatedByUserId = _scope.UserId,
        };
        _db.ApplicationFinancialAccounts.Add(account);
        _db.SaveChanges();
        return account;
    }

    private void SeedApplicationIncome(
        ApplicationFinancialAccount account,
        decimal amount,
        int? propertyId = null,
        int? unitId = null)
    {
        _db.ApplicationFinancialEntries.Add(new ApplicationFinancialEntry
        {
            PublicId = Guid.NewGuid(),
            PortfolioId = PortfolioId,
            ApplicationFinancialAccountId = account.Id,
            PropertyId = propertyId,
            UnitId = unitId,
            EntryType = ApplicationFinancialEntryType.FeeCollection,
            Direction = ApplicationFinancialDirection.Increase,
            Amount = amount,
            Currency = "USD",
            EffectiveOn = new DateOnly(Year, 3, 1),
            OccurredAtUtc = D(Year, 3, 1),
            Description = "Application income",
            Source = ApplicationFinancialEntrySource.Manual,
            IdempotencyKey = $"schedule-e:{Guid.NewGuid():N}",
            CreatedByUserId = _scope.UserId,
        });
    }

    private void SeedExpense(
        decimal amount,
        ScheduleECategory category,
        string description,
        int? propertyId = null,
        int? unitId = null,
        int? workOrderId = null)
    {
        if (workOrderId.HasValue)
        {
            var context = _db.WorkOrders
                .Where(workOrder => workOrder.Id == workOrderId.Value)
                .Select(workOrder => new { workOrder.PropertyId, workOrder.UnitId })
                .Single();
            propertyId = context.PropertyId;
            unitId = context.UnitId;
        }
        else if (unitId.HasValue)
        {
            propertyId = _db.Units
                .Where(unit => unit.Id == unitId.Value)
                .Select(unit => unit.PropertyId)
                .Single();
        }

        _db.Expenses.Add(new Expense
        {
            PortfolioId = PortfolioId,
            OperationalScope = workOrderId.HasValue
                ? ExpenseOperationalScope.WorkOrder
                : unitId.HasValue
                    ? ExpenseOperationalScope.Unit
                    : propertyId.HasValue
                        ? ExpenseOperationalScope.Property
                        : ExpenseOperationalScope.Portfolio,
            PropertyId = propertyId,
            UnitId = unitId,
            WorkOrderId = workOrderId,
            Category = category,
            Description = description,
            Status = ExpenseStatus.Paid,
            Amount = amount,
            IncurredAt = D(Year, 4, 1),
            CreatedAt = D(Year, 4, 1),
            UpdatedAt = D(Year, 4, 1),
        });
    }

    private WorkspaceReadScope SeedSelectedPropertyScope(int propertyId)
    {
        var now = DateTime.UtcNow;
        var email = $"schedule-e-selected-{Guid.NewGuid():N}@example.test";
        var user = new ApplicationUser
        {
            UserName = email,
            NormalizedUserName = email.ToUpperInvariant(),
            Email = email,
            NormalizedEmail = email.ToUpperInvariant(),
            DisplayName = "Selected Property Reporter",
            SecurityStamp = Guid.NewGuid().ToString("N"),
            ConcurrencyStamp = Guid.NewGuid().ToString("N"),
            CreatedAt = now,
        };
        var accessContext = new WorkspaceAccessContext
        {
            User = user,
            PortfolioId = PortfolioId,
            Status = WorkspaceAccessContextStatus.Active,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
        };
        var membership = new WorkspaceMembership
        {
            AccessContext = accessContext,
            PortfolioId = PortfolioId,
            Status = WorkspaceMembershipStatus.Active,
            DefaultExperience = WorkspaceExperience.Management,
            EffectiveFromUtc = now.AddMinutes(-1),
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
        };
        var assignment = new MembershipRoleAssignment
        {
            WorkspaceMembership = membership,
            PortfolioId = PortfolioId,
            RoleProfileId = AccessCatalog.Roles.Single(role => role.Key == RoleProfileKeys.PropertyManager).Id,
            Status = MembershipRoleAssignmentStatus.Active,
            ScopeKind = MembershipRoleAssignmentScopeKind.SelectedProperties,
            EffectiveFromUtc = now.AddMinutes(-1),
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
        };
        assignment.SelectedProperties.Add(new MembershipRoleAssignmentProperty
        {
            MembershipRoleAssignment = assignment,
            PortfolioId = PortfolioId,
            PropertyId = propertyId,
        });
        var session = new AuthSession
        {
            Id = Guid.NewGuid(),
            User = user,
            ActiveAccessContext = accessContext,
            Status = AuthSessionStatus.Active,
            CreatedAtUtc = now,
            LastSeenAtUtc = now,
            ExpiresAtUtc = now.AddHours(1),
        };

        _db.AddRange(assignment, session);
        _db.SaveChanges();

        return new WorkspaceReadScope(
            PortfolioId, user.Id, session.Id, accessContext.Id, accessContext.AccessRevision);
    }

    private LeaseAgreement SeedAgreement(
        Property property,
        Unit unit,
        Tenant tenant,
        string number,
        decimal monthlyRent,
        DateTime start,
        DateTime end)
    {
        var now = DateTime.UtcNow;
        var management = new LeaseManagement
        {
            PortfolioId = PortfolioId,
            PropertyId = property.Id,
            UnitId = unit.Id,
            RelationshipNumber = number,
            PlannedPossessionAtUtc = start,
            PossessionGivenAtUtc = start,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
            CreatedByUserId = 1,
            RowVersion = Guid.NewGuid(),
        };
        _db.LeaseManagements.Add(management);
        _db.SaveChanges();

        var account = new TenantAccount
        {
            PortfolioId = PortfolioId,
            LeaseManagementId = management.Id,
            AccountNumber = $"TA-{number}",
            Currency = "USD",
            OpenedAtUtc = start,
            CreatedAtUtc = now,
            CreatedByUserId = 1,
        };
        var party = new LeaseManagementParty
        {
            PortfolioId = PortfolioId,
            LeaseManagementId = management.Id,
            TenantId = tenant.Id,
            Role = LeaseManagementPartyRole.PrimaryTenant,
            EffectiveFrom = DateOnly.FromDateTime(start),
            ChangeReason = "Schedule E test",
            CreatedAtUtc = now,
            CreatedByUserId = 1,
        };
        var agreement = new LeaseAgreement
        {
            PortfolioId = PortfolioId,
            LeaseManagementId = management.Id,
            VersionNumber = 1,
            AgreementNumber = number,
            ChangeType = LeaseAgreementChangeType.Initial,
            TermType = LeaseAgreementTermType.FixedTerm,
            TermStartOn = DateOnly.FromDateTime(start),
            TermEndOn = DateOnly.FromDateTime(end),
            GoverningFromOn = DateOnly.FromDateTime(start),
            BaseRentAmount = monthlyRent,
            RentDueDay = 1,
            SecurityDepositObligation = monthlyRent,
            Currency = "USD",
            TermsSchemaVersion = 1,
            TermsPayload = "{}",
            DocumentSourceVersion = LegalDocumentSourceVersionTestData.BuiltIn(
                PortfolioId, 1, now),
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
            CreatedByUserId = 1,
            LeaseManagement = management,
        };
        management.TenantAccount = account;
        _db.AddRange(account, party, agreement);
        _db.SaveChanges();
        _db.MarkFullyExecuted(
            agreement,
            party,
            tenant,
            actorUserId: 1,
            executedAtUtc: start);
        return agreement;
    }

    private void SeedChargeAndReceipt(
        LeaseAgreement agreement,
        TenantLedgerEntryType chargeType,
        decimal chargeAmount,
        DateTime dueDate,
        decimal amountReceived,
        DateTime? receivedDate = null)
    {
        var account = agreement.LeaseManagement!.TenantAccount!;
        var charge = new TenantLedgerEntry
        {
            PortfolioId = PortfolioId,
            TenantAccountId = account.Id,
            EntryType = chargeType,
            Direction = TenantLedgerDirection.Debit,
            Amount = chargeAmount,
            Currency = "USD",
            EffectiveOn = DateOnly.FromDateTime(dueDate),
            DueOn = DateOnly.FromDateTime(dueDate),
            PostedAtUtc = dueDate,
            Description = chargeType.ToString(),
            BusinessKey = $"charge:{Guid.NewGuid():N}",
            LeaseAgreementId = agreement.Id,
            CreatedByUserId = 1,
        };
        _db.TenantLedgerEntries.Add(charge);
        _db.SaveChanges();
        if (amountReceived <= 0m)
            return;

        var receivedAt = receivedDate ?? dueDate;
        var receipt = new TenantLedgerEntry
        {
            PortfolioId = PortfolioId,
            TenantAccountId = account.Id,
            EntryType = TenantLedgerEntryType.PaymentReceipt,
            Direction = TenantLedgerDirection.Credit,
            Amount = amountReceived,
            Currency = "USD",
            EffectiveOn = DateOnly.FromDateTime(receivedAt),
            PostedAtUtc = receivedAt,
            Description = "Payment received",
            BusinessKey = $"receipt:{Guid.NewGuid():N}",
            CreatedByUserId = 1,
        };
        _db.TenantLedgerEntries.Add(receipt);
        _db.SaveChanges();
        _db.TenantLedgerAllocations.Add(new TenantLedgerAllocation
        {
            PortfolioId = PortfolioId,
            TenantAccountId = account.Id,
            DebitEntryId = charge.Id,
            CreditEntryId = receipt.Id,
            Amount = amountReceived,
            AllocatedAtUtc = receivedAt,
            BusinessKey = $"allocation:{Guid.NewGuid():N}",
            CreatedByUserId = 1,
        });
        _db.SaveChanges();
    }
}

internal sealed class ScheduleERecordingCommandInterceptor(List<string> commands) : DbCommandInterceptor
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
