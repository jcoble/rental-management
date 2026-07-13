using System.Data.Common;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using RentalCommand.Api.Services.Domain;
using RentalCommand.Core.Authorization;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Data;

namespace RentalCommand.Api.Tests.Domain;

/// <summary>
/// Pins OwnerStatementService's DB-side aggregation (L-10): the per-property rental-income and expense
/// totals are summed in SQL, not by materializing payment/expense rows and grouping in memory. Runs
/// against the real (SQLite) query engine so a query that fails to translate — or that drifts from the
/// expected values — is caught. The per-owner report totals are the sum of the rounded per-line values
/// (so the statement foots to the cent); the heavy payment/expense aggregation still runs in SQL.
/// </summary>
public class OwnerStatementServiceTests : IDisposable
{
    private const int PortfolioId = 1;
    private const int Year = 2026;

    private readonly SqliteConnection _conn;
    private readonly List<string> _commands = [];
    private readonly RentalCommandDbContext _db;
    private readonly OwnerStatementService _sut;
    private readonly WorkspaceReadScope _scope;

    public OwnerStatementServiceTests()
    {
        _conn = new SqliteConnection("DataSource=:memory:");
        _conn.Open();

        var options = new DbContextOptionsBuilder<RentalCommandDbContext>()
            .UseSqlite(_conn)
            .AddInterceptors(new OwnerStatementRecordingCommandInterceptor(_commands))
            .Options;

        _db = new OwnerStatementTestDbContext(options);
        _db.Database.EnsureCreated();

        _db.Portfolios.Add(new Portfolio
        {
            Id = PortfolioId,
            Name = "Test Portfolio",
            ManagementCompanyName = "Test Co",
            TimeZone = "UTC",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        _db.SaveChanges();
        _scope = _db.SeedAdministratorScope(PortfolioId, nameof(OwnerStatementServiceTests));

        _sut = new OwnerStatementService(_db, TimeProvider.System);
    }

    public void Dispose()
    {
        _db.Dispose();
        _conn.Dispose();
    }

    [Fact]
    public async Task GetForOwnerAsync_SumsIncomeAndExpensesPerPropertyDbSide()
    {
        var owner = SeedOwner("Acme Holdings");

        // Property A: 10% mgmt fee. Two paid rent payments in-year ($1,200 + $1,200) and one expense
        // ($300). A third payment is NOT Paid and must be excluded from income.
        var propA = SeedProperty(owner.Id, "Maple Duplex", managementFeePercent: 10m);
        var leaseA = SeedLease(propA, "L-A");
        SeedRent(leaseA, 1200m, paidInYear: true);
        SeedRent(leaseA, 1200m, paidInYear: true);
        SeedRent(leaseA, 1200m, paidInYear: false); // scheduled, not collected → excluded
        SeedExpense(propA.Id, 300m);

        // Property B: 0% mgmt fee. One paid rent ($900) and two expenses ($100 + $50).
        var propB = SeedProperty(owner.Id, "Oak Cottage", managementFeePercent: 0m);
        var leaseB = SeedLease(propB, "L-B");
        SeedRent(leaseB, 900m, paidInYear: true);
        SeedExpense(propB.Id, 100m);
        SeedExpense(propB.Id, 50m);

        // A paid rent in a DIFFERENT year must be excluded entirely.
        SeedRent(leaseA, 5000m, paidInYear: true, year: Year - 1);

        _commands.Clear();

        var report = await _sut.GetForOwnerAsync(_scope, owner.Id, Year, CancellationToken.None);

        report.Should().NotBeNull();
        report!.Properties.Should().HaveCount(2);

        var lineA = report.Properties.Single(p => p.PropertyName == "Maple Duplex");
        lineA.RentalIncome.Should().Be(2400m);          // 1200 + 1200, scheduled + prior-year excluded
        lineA.Expenses.Should().Be(300m);
        lineA.ManagementFee.Should().Be(240m);          // 10% of 2400
        lineA.NetToOwner.Should().Be(1860m);            // 2400 - 300 - 240

        var lineB = report.Properties.Single(p => p.PropertyName == "Oak Cottage");
        lineB.RentalIncome.Should().Be(900m);
        lineB.Expenses.Should().Be(150m);               // 100 + 50
        lineB.ManagementFee.Should().Be(0m);
        lineB.NetToOwner.Should().Be(750m);             // 900 - 150 - 0

        report.TotalIncome.Should().Be(3300m);          // 2400 + 900
        report.TotalExpenses.Should().Be(450m);         // 300 + 150
        report.TotalManagementFee.Should().Be(240m);
        report.TotalNetToOwner.Should().Be(2610m);      // 1860 + 750
        report.TotalDistributed.Should().Be(0m);
        report.Undistributed.Should().Be(2610m);

        var propertyLineSql = _commands.FirstOrDefault(sql =>
            sql.Contains("FROM \"Properties\"", StringComparison.OrdinalIgnoreCase) &&
            sql.Contains("SUM", StringComparison.OrdinalIgnoreCase) &&
            sql.Contains("ORDER BY", StringComparison.OrdinalIgnoreCase));

        propertyLineSql.Should().NotBeNull("owner-statement property lines must be filtered, ordered, and aggregated in SQL");

        // The per-property payment/expense aggregation runs in SQL (asserted above). The report TOTALS
        // are the sum of the rounded per-line values, so the statement foots exactly to the cent — see
        // GetForOwnerAsync_TotalsReconcileWithRoundedPerLineFees for the sub-cent-drift guard.
        report.TotalIncome.Should().Be(report.Properties.Sum(p => p.RentalIncome));
        report.TotalExpenses.Should().Be(report.Properties.Sum(p => p.Expenses));
        report.TotalManagementFee.Should().Be(report.Properties.Sum(p => p.ManagementFee));
        report.TotalNetToOwner.Should().Be(report.Properties.Sum(p => p.NetToOwner));
    }

    [Fact]
    public async Task GetForOwnerAsync_SubtractsOwnerDistributionsWithoutCountingThemAsExpenses()
    {
        var owner = SeedOwner("Distribution Holdings");
        var prop = SeedProperty(owner.Id, "Willow Fourplex", managementFeePercent: 10m);
        SeedRent(SeedLease(prop, "L-D"), 2000m, paidInYear: true);
        SeedExpense(prop.Id, 300m);
        SeedOwnerDistribution(owner.Id, 1000m, propertyId: prop.Id);
        SeedOwnerDistribution(owner.Id, 250m);
        SeedOwnerDistribution(owner.Id, 500m, year: Year - 1, propertyId: prop.Id);

        var otherOwner = SeedOwner("Other Owner");
        SeedOwnerDistribution(otherOwner.Id, 750m);

        _commands.Clear();

        var report = await _sut.GetForOwnerAsync(_scope, owner.Id, Year, CancellationToken.None);

        report.Should().NotBeNull();
        report!.TotalIncome.Should().Be(2000m);
        report.TotalExpenses.Should().Be(300m, "owner payouts are distributions, not operating expenses");
        report.TotalManagementFee.Should().Be(200m);
        report.TotalNetToOwner.Should().Be(1500m);
        report.TotalDistributed.Should().Be(1250m,
            "propertyless distributions are visible when every property for the owner is authorized");
        report.Undistributed.Should().Be(250m);

        _commands.Should().Contain(command =>
            command.Contains("FROM \"OwnerDistributions\"", StringComparison.OrdinalIgnoreCase) &&
            command.Contains("SUM", StringComparison.OrdinalIgnoreCase),
            "recorded owner distributions must be summed in SQL, not materialized and summed in memory");
    }

    [Fact]
    public async Task OwnerDistributions_SelectedPropertyScope_IncludesOnlyLinkedAuthorizedProperty()
    {
        var owner = SeedOwner("Mixed Scope Holdings");
        var allowed = SeedProperty(owner.Id, "Allowed Property", managementFeePercent: 1m);
        SeedRent(SeedLease(allowed, "L-ALLOWED"), 333.33m, paidInYear: true);
        var decoy = SeedProperty(owner.Id, "Decoy Property", managementFeePercent: 10m);
        SeedRent(SeedLease(decoy, "L-DECOY"), 5_000m, paidInYear: true);

        SeedOwnerDistribution(owner.Id, 100m, propertyId: allowed.Id);
        SeedOwnerDistribution(owner.Id, 900m, propertyId: decoy.Id);
        SeedOwnerDistribution(owner.Id, 700m);

        var selectedScope = SeedSelectedPropertyScope(allowed.Id);
        _commands.Clear();

        var report = await _sut.GetForOwnerAsync(selectedScope, owner.Id, Year, CancellationToken.None);
        var summaries = await _sut.ListOwnersWithNetAsync(selectedScope, Year, CancellationToken.None);

        report.Should().NotBeNull();
        var line = report!.Properties.Should().ContainSingle().Subject;
        line.PropertyId.Should().Be(allowed.Id);
        line.ManagementFee.Should().Be(3.33m);
        report.TotalManagementFee.Should().Be(line.ManagementFee);
        report.TotalNetToOwner.Should().Be(line.NetToOwner);
        report.TotalDistributed.Should().Be(100m,
            "the decoy-property and unattributed distributions are outside selected-property authority");

        var summary = summaries.Should().ContainSingle().Subject;
        summary.OwnerId.Should().Be(owner.Id);
        summary.TotalDistributed.Should().Be(100m);

        _commands.Should().Contain(command =>
            command.Contains("FROM \"OwnerDistributions\"", StringComparison.OrdinalIgnoreCase) &&
            command.Contains("PropertyId", StringComparison.OrdinalIgnoreCase) &&
            command.Contains("SUM", StringComparison.OrdinalIgnoreCase),
            "distribution property authorization and totals must stay in the translated SQL query");
    }

    [Fact]
    public async Task GetForOwnerAsync_TotalsReconcileWithRoundedPerLineFees()
    {
        // Two properties whose per-line management fee carries a sub-cent fraction that rounds DOWN:
        //   round(333.33 * 1%) = round(3.3333) = 3.33   and   round(333.34 * 1%) = round(3.3334) = 3.33
        // Sum of the rounded per-line fees = 6.66. Summing the UNROUNDED fees and rounding once would
        // give round(3.3333 + 3.3334) = round(6.6667) = 6.67 — a cent of drift. The report totals must
        // equal the sum of the displayed (rounded) per-line values, i.e. 6.66, not 6.67.
        var owner = SeedOwner("Penny Holdings");

        var propA = SeedProperty(owner.Id, "Cent A", managementFeePercent: 1m);
        SeedRent(SeedLease(propA, "L-A"), 333.33m, paidInYear: true);

        var propB = SeedProperty(owner.Id, "Cent B", managementFeePercent: 1m);
        SeedRent(SeedLease(propB, "L-B"), 333.34m, paidInYear: true);

        _commands.Clear();
        var report = await _sut.GetForOwnerAsync(_scope, owner.Id, Year, CancellationToken.None);

        report.Should().NotBeNull();
        var lineA = report!.Properties.Single(p => p.PropertyName == "Cent A");
        var lineB = report.Properties.Single(p => p.PropertyName == "Cent B");
        lineA.ManagementFee.Should().Be(3.33m);
        lineB.ManagementFee.Should().Be(3.33m);

        // Totals reconcile exactly with the sum of the rounded per-line values (no cent drift).
        report.TotalManagementFee.Should().Be(6.66m);
        report.TotalManagementFee.Should().Be(lineA.ManagementFee + lineB.ManagementFee);
        report.TotalIncome.Should().Be(666.67m);
        report.TotalNetToOwner.Should().Be(660.01m);
        report.TotalNetToOwner.Should().Be(lineA.NetToOwner + lineB.NetToOwner);

        _commands.Should().ContainSingle(command =>
            command.Contains("round", StringComparison.OrdinalIgnoreCase) &&
            command.Contains("sum", StringComparison.OrdinalIgnoreCase),
            "display-line rounding and report totals must both execute in the database query");
    }

    [Fact]
    public async Task ListOwnersWithNetAsync_ComputesPerOwnerNetDbSide()
    {
        var owner1 = SeedOwner("Acme Holdings");
        var owner2 = SeedOwner("Beta Estates");

        var p1 = SeedProperty(owner1.Id, "Maple Duplex", managementFeePercent: 10m);
        SeedRent(SeedLease(p1, "L-1"), 2000m, paidInYear: true);
        SeedExpense(p1.Id, 500m);

        var p2 = SeedProperty(owner2.Id, "Oak Cottage", managementFeePercent: 0m);
        SeedRent(SeedLease(p2, "L-2"), 1000m, paidInYear: true);
        SeedExpense(p2.Id, 250m);

        _commands.Clear();

        var summaries = await _sut.ListOwnersWithNetAsync(_scope, Year, CancellationToken.None);

        summaries.Should().HaveCount(2);
        // Owner1: 2000 income - 500 expenses - 200 mgmt (10%) = 1300.
        summaries.Single(s => s.OwnerName == "Acme Holdings").NetToOwner.Should().Be(1300m);
        // Owner2: 1000 income - 250 expenses - 0 mgmt = 750.
        summaries.Single(s => s.OwnerName == "Beta Estates").NetToOwner.Should().Be(750m);
        summaries.Single(s => s.OwnerName == "Acme Holdings").Undistributed.Should().Be(1300m);
        summaries.Single(s => s.OwnerName == "Beta Estates").Undistributed.Should().Be(750m);

        var ownerSummarySql = _commands.FirstOrDefault(sql =>
            sql.Contains("FROM \"Properties\"", StringComparison.OrdinalIgnoreCase) &&
            sql.Contains("GROUP BY", StringComparison.OrdinalIgnoreCase) &&
            sql.Contains("SUM", StringComparison.OrdinalIgnoreCase) &&
            sql.Contains("ORDER BY", StringComparison.OrdinalIgnoreCase));

        ownerSummarySql.Should().NotBeNull("owner net summaries must group and sum per owner in SQL");
    }

    [Fact]
    public async Task ListOwnersWithNetAsync_IncludesDistributedAndUndistributedFromGroupedSql()
    {
        var owner1 = SeedOwner("Acme Holdings");
        var owner2 = SeedOwner("Beta Estates");

        var p1 = SeedProperty(owner1.Id, "Maple Duplex", managementFeePercent: 10m);
        SeedRent(SeedLease(p1, "L-1"), 2000m, paidInYear: true);
        SeedExpense(p1.Id, 500m);
        SeedOwnerDistribution(owner1.Id, 300m, propertyId: p1.Id);
        SeedOwnerDistribution(owner1.Id, 999m, year: Year - 1, propertyId: p1.Id);

        var p2 = SeedProperty(owner2.Id, "Oak Cottage", managementFeePercent: 0m);
        SeedRent(SeedLease(p2, "L-2"), 1000m, paidInYear: true);
        SeedExpense(p2.Id, 250m);
        SeedOwnerDistribution(owner2.Id, 100m, propertyId: p2.Id);

        _commands.Clear();

        var summaries = await _sut.ListOwnersWithNetAsync(_scope, Year, CancellationToken.None);

        var acme = summaries.Single(s => s.OwnerName == "Acme Holdings");
        acme.NetToOwner.Should().Be(1300m);
        acme.TotalDistributed.Should().Be(300m);
        acme.Undistributed.Should().Be(1000m);

        var beta = summaries.Single(s => s.OwnerName == "Beta Estates");
        beta.NetToOwner.Should().Be(750m);
        beta.TotalDistributed.Should().Be(100m);
        beta.Undistributed.Should().Be(650m);

        _commands.Should().Contain(command =>
            command.Contains("FROM \"OwnerDistributions\"", StringComparison.OrdinalIgnoreCase) &&
            command.Contains("GROUP BY", StringComparison.OrdinalIgnoreCase) &&
            command.Contains("SUM", StringComparison.OrdinalIgnoreCase),
            "distribution totals per owner must be grouped and summed in SQL");
    }

    [Fact]
    public async Task GetTotalNetToOwnersAsync_SumsPortfolioNetDbSide()
    {
        var owner1 = SeedOwner("Acme Holdings");
        var owner2 = SeedOwner("Beta Estates");

        var p1 = SeedProperty(owner1.Id, "Maple Duplex", managementFeePercent: 10m);
        SeedRent(SeedLease(p1, "L-1"), 2000m, paidInYear: true);
        SeedExpense(p1.Id, 500m);

        var p2 = SeedProperty(owner2.Id, "Oak Cottage", managementFeePercent: 0m);
        SeedRent(SeedLease(p2, "L-2"), 1000m, paidInYear: true);
        SeedExpense(p2.Id, 250m);

        _commands.Clear();

        var total = await _sut.GetTotalNetToOwnersAsync(_scope, Year, CancellationToken.None);

        total.Should().Be(2050m);
        _commands.Should().Contain(command =>
            command.Contains("FROM \"Properties\"", StringComparison.OrdinalIgnoreCase) &&
            command.Contains("SUM", StringComparison.OrdinalIgnoreCase) &&
            command.Contains("GROUP BY", StringComparison.OrdinalIgnoreCase),
            "portfolio-level owner distributions must be summed in SQL, not from owner summary DTOs");
    }

    // ── seed helpers ───────────────────────────────────────────────────────────────────────────
    private WorkspaceReadScope SeedSelectedPropertyScope(int propertyId)
    {
        var now = DateTime.UtcNow;
        var email = $"owner-statement-selected-{Guid.NewGuid():N}@example.test";
        var user = new ApplicationUser
        {
            UserName = email,
            NormalizedUserName = email.ToUpperInvariant(),
            Email = email,
            NormalizedEmail = email.ToUpperInvariant(),
            DisplayName = "Selected Property Owner Reporter",
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

    private OwnerEntity SeedOwner(string name)
    {
        var owner = new OwnerEntity
        {
            PortfolioId = PortfolioId,
            Name = name,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        _db.OwnerEntities.Add(owner);
        _db.SaveChanges();
        return owner;
    }

    private Property SeedProperty(int ownerId, string name, decimal managementFeePercent)
    {
        var property = new Property
        {
            PortfolioId = PortfolioId,
            OwnerEntityId = ownerId,
            Name = name,
            AddressLine1 = "1 Main",
            City = "Columbus",
            State = "OH",
            PostalCode = "43219",
            ManagementFeePercent = managementFeePercent,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        _db.Properties.Add(property);
        _db.SaveChanges();
        return property;
    }

    private TenantAccount SeedLease(Property property, string leaseNumber)
    {
        var unit = new Unit
        {
            Property = property,
            UnitNumber = "1",
            MarketRent = 1000m,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        var tenant = new Tenant
        {
            PortfolioId = PortfolioId,
            FirstName = "Pat",
            LastName = "Tenant",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        _db.AddRange(unit, tenant);
        _db.SaveChanges();
        var management = new LeaseManagement
        {
            PortfolioId = PortfolioId, PropertyId = property.Id, UnitId = unit.Id,
            RelationshipNumber = leaseNumber, PlannedPossessionAtUtc = new DateTime(Year, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            PossessionGivenAtUtc = new DateTime(Year, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            CreatedAtUtc = DateTime.UtcNow, UpdatedAtUtc = DateTime.UtcNow,
            CreatedByUserId = 1, RowVersion = Guid.NewGuid(),
        };
        _db.LeaseManagements.Add(management);
        _db.SaveChanges();
        var account = new TenantAccount
        {
            PortfolioId = PortfolioId, LeaseManagementId = management.Id,
            AccountNumber = $"TA-{management.Id}", Currency = "USD",
            OpenedAtUtc = management.PossessionGivenAtUtc!.Value, CreatedAtUtc = DateTime.UtcNow, CreatedByUserId = 1,
        };
        _db.AddRange(account, new LeaseManagementParty
        {
            PortfolioId = PortfolioId, LeaseManagementId = management.Id, TenantId = tenant.Id,
            Role = LeaseManagementPartyRole.PrimaryTenant, EffectiveFrom = new DateOnly(Year, 1, 1),
            ChangeReason = "Owner statement test", CreatedAtUtc = DateTime.UtcNow, CreatedByUserId = 1,
        });
        _db.SaveChanges();
        return account;
    }

    private void SeedRent(TenantAccount account, decimal amount, bool paidInYear, int? year = null)
    {
        var y = year ?? Year;
        var paidDate = new DateTime(y, 6, 15, 0, 0, 0, DateTimeKind.Utc);
        var charge = new TenantLedgerEntry
        {
            PortfolioId = PortfolioId, TenantAccountId = account.Id,
            EntryType = TenantLedgerEntryType.RentCharge, Direction = TenantLedgerDirection.Debit,
            Amount = amount, Currency = "USD", EffectiveOn = DateOnly.FromDateTime(paidDate),
            DueOn = DateOnly.FromDateTime(paidDate), PostedAtUtc = paidDate,
            Description = "Rent", BusinessKey = $"rent:{Guid.NewGuid():N}", CreatedByUserId = 1,
        };
        _db.TenantLedgerEntries.Add(charge);
        _db.SaveChanges();
        if (!paidInYear) return;
        var receipt = new TenantLedgerEntry
        {
            PortfolioId = PortfolioId, TenantAccountId = account.Id,
            EntryType = TenantLedgerEntryType.PaymentReceipt, Direction = TenantLedgerDirection.Credit,
            Amount = amount, Currency = "USD", EffectiveOn = DateOnly.FromDateTime(paidDate),
            PostedAtUtc = paidDate, Description = "Payment received",
            BusinessKey = $"receipt:{Guid.NewGuid():N}", CreatedByUserId = 1,
        };
        _db.TenantLedgerEntries.Add(receipt);
        _db.SaveChanges();
        _db.TenantLedgerAllocations.Add(new TenantLedgerAllocation
        {
            PortfolioId = PortfolioId, TenantAccountId = account.Id, DebitEntryId = charge.Id,
            CreditEntryId = receipt.Id, Amount = amount, AllocatedAtUtc = paidDate,
            BusinessKey = $"allocation:{Guid.NewGuid():N}", CreatedByUserId = 1,
        });
        _db.SaveChanges();
    }

    private void SeedExpense(int propertyId, decimal amount)
    {
        _db.Expenses.Add(new Expense
        {
            PortfolioId = PortfolioId,
            PropertyId = propertyId,
            Description = "Repair",
            Category = ScheduleECategory.Repairs,
            Status = ExpenseStatus.Paid,
            Amount = amount,
            IncurredAt = new DateTime(Year, 5, 5, 0, 0, 0, DateTimeKind.Utc),
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        _db.SaveChanges();
    }

    private void SeedOwnerDistribution(int ownerEntityId, decimal amount, int? year = null, int? propertyId = null)
    {
        var y = year ?? Year;
        _db.OwnerDistributions.Add(new OwnerDistribution
        {
            PortfolioId = PortfolioId,
            OwnerEntityId = ownerEntityId,
            PropertyId = propertyId,
            Date = new DateTime(y, 7, 1, 0, 0, 0, DateTimeKind.Utc),
            Amount = amount,
            Method = DistributionMethod.Ach,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        _db.SaveChanges();
    }
}

internal sealed class OwnerStatementTestDbContext : RentalCommand.TestCommon.SqliteCompatibleRentalCommandDbContext
{
    public OwnerStatementTestDbContext(DbContextOptions<RentalCommandDbContext> options) : base(options) { }
}

internal sealed class OwnerStatementRecordingCommandInterceptor(List<string> commands) : DbCommandInterceptor
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
