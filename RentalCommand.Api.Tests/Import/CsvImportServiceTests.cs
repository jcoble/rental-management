using System.Text;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using RentalCommand.Api.Services.Domain;
using RentalCommand.Api.Services.Import;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Interfaces;
using RentalCommand.TestCommon;

namespace RentalCommand.Api.Tests.Import;

public class CsvImportServiceTests : IDisposable
{
    private const int PortfolioId = 1;

    private readonly SqliteTestContext _ctx;
    private readonly CsvImportService _sut;

    public CsvImportServiceTests()
    {
        _ctx = new SqliteTestContext();
        var noop = new NoopDataUpdateService();
        _sut = new CsvImportService(
            _ctx.Db,
            new TenantService(_ctx.Db, noop, new NoopTenantPortalProvisioningService(), NullLogger<TenantService>.Instance, TimeProvider.System),
            new PropertyService(_ctx.Db, noop, TimeProvider.System),
            new UnitService(_ctx.Db, noop, Mock.Of<IAuditTrailService>(), TimeProvider.System),
            new PaymentService(
                _ctx.Db,
                noop,
                TimeProvider.System),
            new ExpenseService(_ctx.Db, noop, Mock.Of<IFileStorage>(), TimeProvider.System),
            new LoanService(_ctx.Db, noop, TimeProvider.System));
    }

    public void Dispose() => _ctx.Dispose();

    private static Stream Csv(string text) => new MemoryStream(Encoding.UTF8.GetBytes(text));

    // -------------------------------------------------------------------------
    // Tenant dry-run: reports valid/invalid rows and creates nothing
    // -------------------------------------------------------------------------

    [Fact]
    public async Task TenantDryRun_ReportsValidAndInvalidRows_CreatesNothing()
    {
        const string csv =
            "firstName,lastName,email,phone\n" +
            "Frank,Coble,frank@example.com,555-1212\n" +   // valid
            ",NoFirst,bad@example.com,555-0000\n" +         // invalid: missing firstName
            "Jane,Doe,not-an-email,\n";                     // invalid: bad email

        var result = await _sut.ImportAsync(PortfolioId, "tenant", Csv(csv), dryRun: true);

        result.EntityType.Should().Be("Tenant");
        result.DryRun.Should().BeTrue();
        result.TotalRows.Should().Be(3);
        result.ValidRows.Should().Be(1);
        result.CreatedRows.Should().Be(0);

        result.Rows.Should().HaveCount(3);
        result.Rows[0].RowNumber.Should().Be(2); // header is row 1
        result.Rows[0].Valid.Should().BeTrue();
        result.Rows[0].CreatedId.Should().BeNull();
        result.Rows[1].Valid.Should().BeFalse();
        result.Rows[1].Errors.Should().NotBeEmpty();
        result.Rows[2].Valid.Should().BeFalse();
        result.Rows[2].Errors.Should().NotBeEmpty();

        (await _ctx.Db.Tenants.CountAsync()).Should().Be(0);
    }

    // -------------------------------------------------------------------------
    // Tenant commit: creates the valid rows, skips invalid ones
    // -------------------------------------------------------------------------

    [Fact]
    public async Task TenantCommit_CreatesValidRows_SkipsInvalidRows()
    {
        const string csv =
            "firstName,lastName,email,phone\n" +
            "Frank,Coble,frank@example.com,555-1212\n" +   // valid
            ",NoFirst,,\n" +                                 // invalid: missing firstName
            "Jane,Doe,,555-9999\n";                          // valid

        var result = await _sut.ImportAsync(PortfolioId, "Tenant", Csv(csv), dryRun: false);

        result.TotalRows.Should().Be(3);
        result.ValidRows.Should().Be(2);
        result.CreatedRows.Should().Be(2);

        result.Rows[0].Valid.Should().BeTrue();
        result.Rows[0].CreatedId.Should().NotBeNull();
        result.Rows[1].Valid.Should().BeFalse();
        result.Rows[1].CreatedId.Should().BeNull();
        result.Rows[2].Valid.Should().BeTrue();
        result.Rows[2].CreatedId.Should().NotBeNull();

        var tenants = await _ctx.Db.Tenants.OrderBy(t => t.Id).ToListAsync();
        tenants.Should().HaveCount(2);
        tenants.Select(t => t.FirstName).Should().BeEquivalentTo(["Frank", "Jane"]);
        tenants.Should().OnlyContain(t => t.PortfolioId == PortfolioId);
    }

    // -------------------------------------------------------------------------
    // Unit: resolves the owning property by name (case-insensitive, in-portfolio)
    // -------------------------------------------------------------------------

    [Fact]
    public async Task UnitImport_ResolvesPropertyByName_AndCreatesUnit()
    {
        var property = SeedProperty("Maple Court");

        const string csv =
            "propertyName,unitNumber,bedrooms,bathrooms,marketRent\n" +
            "maple court,101,2,1.5,\"$1,200\"\n";   // name differs only in case; quoted currency rent

        var result = await _sut.ImportAsync(PortfolioId, "Unit", Csv(csv), dryRun: false);

        result.TotalRows.Should().Be(1);
        result.ValidRows.Should().Be(1);
        result.CreatedRows.Should().Be(1);
        result.Rows[0].Valid.Should().BeTrue();
        result.Rows[0].CreatedId.Should().NotBeNull();

        var unit = await _ctx.Db.Units.SingleAsync();
        unit.PropertyId.Should().Be(property.Id);
        unit.UnitNumber.Should().Be("101");
        unit.Bedrooms.Should().Be(2m);
        unit.Bathrooms.Should().Be(1.5m);
        unit.MarketRent.Should().Be(1200m);
    }

    [Fact]
    public async Task UnitImport_UnknownPropertyName_IsAClearPerRowError()
    {
        SeedProperty("Maple Court");

        const string csv =
            "propertyName,unitNumber,bedrooms,bathrooms,marketRent\n" +
            "Nonexistent Place,101,2,1,1000\n";

        var result = await _sut.ImportAsync(PortfolioId, "Unit", Csv(csv), dryRun: false);

        result.ValidRows.Should().Be(0);
        result.CreatedRows.Should().Be(0);
        result.Rows[0].Valid.Should().BeFalse();
        result.Rows[0].Errors.Should().ContainMatch("*No property named*");
        (await _ctx.Db.Units.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task UnitImport_AmbiguousPropertyName_IsAClearPerRowError()
    {
        SeedProperty("Maple Court");
        SeedProperty("Maple Court"); // duplicate name → ambiguous

        const string csv =
            "propertyName,unitNumber,bedrooms,bathrooms,marketRent\n" +
            "Maple Court,101,2,1,1000\n";

        var result = await _sut.ImportAsync(PortfolioId, "Unit", Csv(csv), dryRun: true);

        result.Rows[0].Valid.Should().BeFalse();
        result.Rows[0].Errors.Should().ContainMatch("*ambiguous*");
    }

    // -------------------------------------------------------------------------
    // Property: type column defaults sensibly and parses case-insensitively
    // -------------------------------------------------------------------------

    [Fact]
    public async Task PropertyImport_DefaultsTypeWhenBlank_AndParsesTypeName()
    {
        const string csv =
            "name,addressLine1,city,state,postalCode,type\n" +
            "House A,1 Main St,Springfield,IL,62701,singlefamily\n" +   // explicit type, lowercase
            "House B,2 Main St,Springfield,IL,62701,\n";                 // blank type → default

        var result = await _sut.ImportAsync(PortfolioId, "Property", Csv(csv), dryRun: false);

        result.CreatedRows.Should().Be(2);
        var props = await _ctx.Db.Properties.OrderBy(p => p.Name).ToListAsync();
        props[0].PropertyType.Should().Be(PropertyType.SingleFamily);
        props[1].PropertyType.Should().Be(PropertyType.MultiFamily); // CreatePropertyRequest default
    }

    // -------------------------------------------------------------------------
    // Financial imports: create through domain services and skip natural-key duplicates
    // -------------------------------------------------------------------------

    [Fact]
    public async Task PaymentImport_ResolvesActiveLeaseByPropertyUnit_AndSkipsDuplicateReimport()
    {
        var lease = SeedLease("L-100", propertyName: "Maple Court", unitNumber: "101");

        const string csv =
            "propertyName,unitNumber,paymentType,amount,paidDate,method,externalReference,notes\n" +
            "Maple Court,101,Rent,1200,2025-01-05,ACH,bank-1,January rent\n" +
            "Maple Court,101,Rent,1200,2025-01-05,ACH,bank-1,January rent duplicate\n";

        var first = await _sut.ImportAsync(PortfolioId, "payments", Csv(csv), dryRun: false);

        first.EntityType.Should().Be("Payment");
        first.ValidRows.Should().Be(2);
        first.CreatedRows.Should().Be(1);
        first.DuplicateRows.Should().Be(1);
        first.Rows[1].IsDuplicate.Should().BeTrue();

        var stored = await _ctx.Db.Payments.SingleAsync();
        stored.LeaseId.Should().Be(lease.Id);
        stored.PaymentType.Should().Be(PaymentType.Rent);
        stored.Status.Should().Be(PaymentStatus.Paid);
        stored.Amount.Should().Be(1200m);
        stored.ExternalReference.Should().Be("bank-1");

        var second = await _sut.ImportAsync(PortfolioId, "payment", Csv(csv), dryRun: false);

        second.ValidRows.Should().Be(2);
        second.CreatedRows.Should().Be(0);
        second.DuplicateRows.Should().Be(2);
        second.Rows[0].IsDuplicate.Should().BeTrue();
        second.Rows[0].SkipReason.Should().Contain("duplicate");
        (await _ctx.Db.Payments.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task ExpenseImport_CreatesMortgageInterestExpense_AndSkipsDuplicateReimport()
    {
        SeedProperty("Maple Court");

        const string csv =
            "propertyName,category,description,amount,incurredAt,paidAt,notes\n" +
            "Maple Court,MortgageInterest,January mortgage interest,800,2025-01-15,2025-01-15,Imported history\n" +
            "Maple Court,MortgageInterest,January mortgage interest,800,2025-01-15,2025-01-15,Duplicate row\n";

        var first = await _sut.ImportAsync(PortfolioId, "expenses", Csv(csv), dryRun: false);

        first.EntityType.Should().Be("Expense");
        first.CreatedRows.Should().Be(1);
        first.DuplicateRows.Should().Be(1);
        first.Rows[1].IsDuplicate.Should().BeTrue();

        var stored = await _ctx.Db.Expenses.SingleAsync();
        stored.Category.Should().Be(ScheduleECategory.MortgageInterest);
        stored.Status.Should().Be(ExpenseStatus.Paid);
        stored.Amount.Should().Be(800m);

        var second = await _sut.ImportAsync(PortfolioId, "mortgage payments", Csv(csv), dryRun: false);

        second.CreatedRows.Should().Be(0);
        second.DuplicateRows.Should().Be(2);
        second.Rows[0].IsDuplicate.Should().BeTrue();
        (await _ctx.Db.Expenses.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task LoanImport_CreatesLoan_AndSkipsDuplicateReimport()
    {
        SeedProperty("Maple Court");

        const string csv =
            "propertyName,lender,originalAmount,currentBalance,annualInterestRatePct,termMonths,startDate,dayOfMonthDue,monthlyPrincipalInterest,monthlyEscrow\n" +
            "Maple Court,Acme Bank,200000,198500,6.25,360,2024-01-01,1,1231.43,350\n" +
            "Maple Court,Acme Bank,200000,198500,6.25,360,2024-01-01,1,1231.43,350\n";

        var first = await _sut.ImportAsync(PortfolioId, "mortgages", Csv(csv), dryRun: false);

        first.EntityType.Should().Be("Loan");
        first.CreatedRows.Should().Be(1);
        first.DuplicateRows.Should().Be(1);
        first.Rows[1].IsDuplicate.Should().BeTrue();

        var stored = await _ctx.Db.Loans.SingleAsync();
        stored.Lender.Should().Be("Acme Bank");
        stored.OriginalAmount.Should().Be(200000m);
        stored.CurrentBalance.Should().Be(198500m);

        var second = await _sut.ImportAsync(PortfolioId, "loan", Csv(csv), dryRun: false);

        second.CreatedRows.Should().Be(0);
        second.DuplicateRows.Should().Be(2);
        second.Rows[0].IsDuplicate.Should().BeTrue();
        (await _ctx.Db.Loans.CountAsync()).Should().Be(1);
    }

    // -------------------------------------------------------------------------
    // Templates + unsupported entity types
    // -------------------------------------------------------------------------

    [Fact]
    public void Template_ReturnsHeaderRow_PerEntityType()
    {
        _sut.GetTemplate("tenant").Should().Be("firstName,lastName,email,phone");
        _sut.GetTemplate("Property").Should().StartWith("name,addressLine1");
        _sut.GetTemplate("UNIT").Should().Contain("unitNumber");
        _sut.GetTemplate("payments").Should().Contain("leaseNumber");
        _sut.GetTemplate("expenses").Should().Contain("incurredAt");
        _sut.GetTemplate("mortgages").Should().Contain("originalAmount");
    }

    [Fact]
    public async Task UnsupportedEntityType_Throws()
    {
        var act = async () => await _sut.ImportAsync(PortfolioId, "vendor", Csv("x\n"), dryRun: true);
        await act.Should().ThrowAsync<ArgumentException>();
    }

    private Property SeedProperty(string name)
    {
        var now = DateTime.UtcNow;
        var property = new Property
        {
            PortfolioId = PortfolioId,
            Name = name,
            PropertyType = PropertyType.MultiFamily,
            Status = PropertyStatus.Active,
            AddressLine1 = "1 Test St",
            City = "Town",
            State = "ST",
            PostalCode = "00000",
            CreatedAt = now,
            UpdatedAt = now,
        };
        _ctx.Db.Properties.Add(property);
        _ctx.Db.SaveChanges();
        return property;
    }

    private Lease SeedLease(string leaseNumber, string propertyName, string unitNumber)
    {
        var now = DateTime.UtcNow;
        var property = SeedProperty(propertyName);
        var unit = new Unit
        {
            PropertyId = property.Id,
            UnitNumber = unitNumber,
            Bedrooms = 2,
            Bathrooms = 1,
            MarketRent = 1200m,
            Status = UnitStatus.Occupied,
            CreatedAt = now,
            UpdatedAt = now,
        };
        var tenant = new Tenant
        {
            PortfolioId = PortfolioId,
            FirstName = "Taylor",
            LastName = "Tenant",
            CreatedAt = now,
            UpdatedAt = now,
        };
        _ctx.Db.Units.Add(unit);
        _ctx.Db.Tenants.Add(tenant);
        _ctx.Db.SaveChanges();

        var lease = new Lease
        {
            PortfolioId = PortfolioId,
            PropertyId = property.Id,
            UnitId = unit.Id,
            TenantId = tenant.Id,
            LeaseNumber = leaseNumber,
            Status = LeaseStatus.Active,
            StartDate = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            EndDate = new DateTime(2025, 12, 31, 0, 0, 0, DateTimeKind.Utc),
            MonthlyRent = 1200m,
            SecurityDeposit = 1200m,
            CreatedAt = now,
            UpdatedAt = now,
        };
        _ctx.Db.Leases.Add(lease);
        _ctx.Db.SaveChanges();
        return lease;
    }

    private sealed class NoopDataUpdateService : IDataUpdateService
    {
        public Task BroadcastEntityUpdateAsync(int portfolioId, string entityType, int entityId, object data, CancellationToken ct = default)
            => Task.CompletedTask;

        public Task BroadcastEntityDeleteAsync(int portfolioId, string entityType, int entityId, CancellationToken ct = default)
            => Task.CompletedTask;
    }
}
