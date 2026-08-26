using System.Text;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using RentalCommand.Api.Services.Import;
using RentalCommand.Api.Tests.Domain;
using RentalCommand.Core.Atomic;
using RentalCommand.Data;
using RentalCommand.Data.Import;
using RentalCommand.TestCommon;

namespace RentalCommand.Api.Tests.Import;

[Collection(MigratedPostgreSqlCollection.Name1)]
public sealed class PropertyCsvImportPostgreSqlTests : IAsyncLifetime
{
    private readonly MigratedPostgreSqlFixture _fixture;
    private MigratedPostgreSqlTestContext _context = null!;
    private ServiceProvider _services = null!;
    private CsvImportService _sut = null!;
    private RentalCommand.Core.Authorization.WorkspaceReadScope _scope;

    public PropertyCsvImportPostgreSqlTests(MigratedPostgreSqlFixture fixture) =>
        _fixture = fixture;

    public async Task InitializeAsync()
    {
        _context = await _fixture.CreateContextAsync();
        _services = AtomicDomainTestKernel.CreateForCoreCrudPostgreSql(_context.ConnectionString);
        var db = _services.GetRequiredService<RentalCommandDbContext>();
        _scope = db.SeedAdministratorScope(1, nameof(PropertyCsvImportPostgreSqlTests));
        _sut = new CsvImportService(
            new AtomicUnitImportPersistence(db),
            new AtomicCoreCsvImportPersistence(db),
            new AtomicPaymentCsvImportPersistence(db),
            db,
            _services.GetRequiredService<IWriteExecutor>());
    }

    public async Task DisposeAsync()
    {
        await _services.DisposeAsync();
        await _context.DisposeAsync();
    }

    [Fact]
    public async Task LivePropertyImport_CreatesOneRow()
    {
        const string csv =
            "name,addressLine1,city,state,postalCode,type,rentalStructure,unitNumber\n" +
            "Maple House,1 Main St,Springfield,IL,62701,singlefamily,SingleRental,Home\n";
        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(csv));
        var commandContext = new CsvImportCommandContext(
            _scope.UserId, _scope.SessionId, _scope.AccessContextId,
            _scope.AccessRevision, "live-property-import");

        var result = await _sut.ImportAsync(
            _scope, "Property", stream, dryRun: false, commandContext);

        result.CreatedRows.Should().Be(1);
    }
}
