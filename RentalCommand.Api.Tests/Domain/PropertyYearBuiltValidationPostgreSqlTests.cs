using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using RentalCommand.Api.DTOs;
using RentalCommand.Api.Services.Domain;
using RentalCommand.Core;
using RentalCommand.Core.Atomic;
using RentalCommand.Core.Authorization;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Interfaces;
using RentalCommand.Core.Time;
using RentalCommand.Data;
using RentalCommand.Data.Atomic;
using RentalCommand.Data.Auditing;
using RentalCommand.TestCommon;

namespace RentalCommand.Api.Tests.Domain;

[Collection(MigratedPostgreSqlCollection.Name)]
public sealed class PropertyYearBuiltValidationPostgreSqlTests : IAsyncLifetime
{
    private const int PortfolioId = 1;
    private static readonly DateOnly BusinessDate = new(2027, 7, 28);
    private static readonly DateTime BusinessNowUtc =
        BusinessDate.ToDateTime(new TimeOnly(14, 30), DateTimeKind.Utc);

    private readonly MigratedPostgreSqlFixture _fixture;
    private MigratedPostgreSqlTestContext _context = null!;
    private ServiceProvider _services = null!;
    private WorkspaceReadScope _scope;

    public PropertyYearBuiltValidationPostgreSqlTests(MigratedPostgreSqlFixture fixture) =>
        _fixture = fixture;

    public async Task InitializeAsync()
    {
        _context = await _fixture.CreateContextAsync();
        _scope = _context.Db.SeedAdministratorScope(PortfolioId, nameof(PropertyYearBuiltValidationPostgreSqlTests));
        SeedFrozenBusinessDate();
        _services = BuildServices(_context.ConnectionString, new FixedTimeProvider(new DateTimeOffset(BusinessNowUtc)));
    }

    public async Task DisposeAsync()
    {
        await _services.DisposeAsync();
        await _context.DisposeAsync();
    }

    [Fact]
    public async Task SetupAsync_RejectsYearBuiltAfterSimulationBusinessYearWithoutPartialMutation()
    {
        var service = CreateService();
        var before = await ReadMutationCountsAsync();

        Func<Task> act = async () => await service.SetupAsync(
            _scope,
            SingleRentalRequest(BusinessDate.Year + 1),
            "future-year-built-setup");

        await act.Should().ThrowAsync<DomainValidationException>()
            .WithMessage($"*portfolio business year ({BusinessDate.Year})*");

        var after = await ReadMutationCountsAsync();
        after.Should().BeEquivalentTo(before);
        (await _context.Db.Properties.AsNoTracking().CountAsync()).Should().Be(0);
        (await _context.Db.Units.AsNoTracking().CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task UpdateAsync_RejectsYearBuiltAfterSimulationBusinessYearWithoutPropertyAuditOrOutboxMutation()
    {
        var property = await SeedPropertyAsync(yearBuilt: 1999);
        var service = CreateService();
        var before = await ReadMutationCountsAsync();

        Func<Task> act = async () => await service.UpdateAsync(
            _scope,
            property.Id,
            new UpdatePropertyRequest
            {
                Name = "Future Year Attempt",
                YearBuilt = BusinessDate.Year + 1,
            },
            "future-year-built-update");

        await act.Should().ThrowAsync<DomainValidationException>()
            .WithMessage($"*portfolio business year ({BusinessDate.Year})*");

        _context.Db.ChangeTracker.Clear();
        var readback = await _context.Db.Properties.AsNoTracking()
            .Where(row => row.Id == property.Id)
            .Select(row => new { row.Name, row.YearBuilt, row.UpdatedAt })
            .SingleAsync();
        readback.Name.Should().Be(property.Name);
        readback.YearBuilt.Should().Be(1999);
        readback.UpdatedAt.Should().Be(property.UpdatedAt);

        var after = await ReadMutationCountsAsync();
        after.Should().BeEquivalentTo(before);
    }

    [Fact]
    public async Task SetupAndUpdateAsync_AllowNullAndCurrentSimulationBusinessYear()
    {
        var service = CreateService();

        var currentYear = await service.SetupAsync(
            _scope,
            SingleRentalRequest(BusinessDate.Year),
            "current-year-built-setup");
        var nullYear = await service.SetupAsync(
            _scope,
            SingleRentalRequest(yearBuilt: null, unitNumber: "Null"),
            "null-year-built-setup");

        currentYear!.Property.YearBuilt.Should().Be(BusinessDate.Year);
        nullYear!.Property.YearBuilt.Should().BeNull();

        var updated = await service.UpdateAsync(
            _scope,
            nullYear.Property.Id,
            new UpdatePropertyRequest { YearBuilt = BusinessDate.Year },
            "current-year-built-update");

        updated!.YearBuilt.Should().Be(BusinessDate.Year);
    }

    private PropertyService CreateService() => new(
        _context.Db,
        Mock.Of<IDataUpdateService>(),
        new FixedTimeProvider(new DateTimeOffset(BusinessNowUtc)),
        _services.GetRequiredService<IAtomicUnitOfWork>());

    private async Task<Property> SeedPropertyAsync(int? yearBuilt)
    {
        var property = new Property
        {
            PortfolioId = PortfolioId,
            Name = "Business Year Property",
            AddressLine1 = "28 Sim Lane",
            City = "Columbus",
            State = "OH",
            PostalCode = "43215",
            YearBuilt = yearBuilt,
            CreatedAt = BusinessNowUtc.AddDays(-1),
            UpdatedAt = BusinessNowUtc.AddDays(-1),
        };
        _context.Db.Properties.Add(property);
        await _context.Db.SaveChangesAsync();
        _context.Db.ChangeTracker.Clear();
        return property;
    }

    private void SeedFrozenBusinessDate()
    {
        var clock = _context.Db.SimulationClocks.SingleOrDefault(clock => clock.Id == 1);
        if (clock is null)
        {
            _context.Db.SimulationClocks.Add(new SimulationClock { Id = 1 });
            clock = _context.Db.SimulationClocks.Local.Single(clock => clock.Id == 1);
        }

        clock.Mode = ClockMode.Frozen;
        clock.SimAnchorUtc = BusinessNowUtc;
        clock.RealAnchorUtc = BusinessNowUtc;
        clock.TimeZoneId = "UTC";
        clock.UpdatedAtRealUtc = BusinessNowUtc;
        _context.Db.SaveChanges();
        _context.Db.ChangeTracker.Clear();
    }

    private async Task<MutationCounts> ReadMutationCountsAsync() => new(
        await _context.Db.AtomicAuditLogs.AsNoTracking().CountAsync(),
        await _context.Db.OutboxMessages.AsNoTracking().CountAsync(),
        await _context.Db.AtomicCommandReceipts.AsNoTracking().CountAsync());

    private static SetupPropertyRequest SingleRentalRequest(
        int? yearBuilt,
        string unitNumber = "Home") => new()
    {
        Property = new CreatePropertyRequest
        {
            Name = $"{unitNumber} Property",
            PropertyType = PropertyType.SingleFamily,
            RentalStructure = RentalStructure.SingleRental,
            AddressLine1 = "100 Proof Lane",
            City = "Columbus",
            State = "OH",
            PostalCode = "43215",
            YearBuilt = yearBuilt,
        },
        Units =
        [
            new SetupUnitRequest
            {
                UnitNumber = unitNumber,
                Bedrooms = 3,
                Bathrooms = 2,
                MarketRent = 1800m,
            },
        ],
    };

    private static ServiceProvider BuildServices(string connectionString, TimeProvider timeProvider)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(timeProvider);
        services.AddScoped<ICurrentActor, SystemCurrentActor>();
        services.AddAtomicPersistenceKernel();
        services.AddAtomicCommandHandler<
            AtomicCoreCrudMutationCommand,
            AtomicCoreCrudMutationResult,
            AtomicCoreCrudMutationHandler>();
        services.AddDbContext<RentalCommandDbContext>((provider, builder) =>
            builder.UseNpgsql(connectionString)
                .UseAtomicPersistenceKernel(provider));
        return services.BuildServiceProvider();
    }

    private sealed record MutationCounts(int AtomicAuditLogCount, int OutboxMessageCount, int ReceiptCount);

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }
}
