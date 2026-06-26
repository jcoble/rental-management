using System.Text;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
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
            new TenantService(_ctx.Db, noop),
            new PropertyService(_ctx.Db, noop),
            new UnitService(_ctx.Db, noop, Mock.Of<IAuditTrailService>()));
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
    // Templates + unsupported entity types
    // -------------------------------------------------------------------------

    [Fact]
    public void Template_ReturnsHeaderRow_PerEntityType()
    {
        _sut.GetTemplate("tenant").Should().Be("firstName,lastName,email,phone");
        _sut.GetTemplate("Property").Should().StartWith("name,addressLine1");
        _sut.GetTemplate("UNIT").Should().Contain("unitNumber");
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

    private sealed class NoopDataUpdateService : IDataUpdateService
    {
        public Task BroadcastEntityUpdateAsync(int portfolioId, string entityType, int entityId, object data, CancellationToken ct = default)
            => Task.CompletedTask;

        public Task BroadcastEntityDeleteAsync(int portfolioId, string entityType, int entityId, CancellationToken ct = default)
            => Task.CompletedTask;
    }
}
