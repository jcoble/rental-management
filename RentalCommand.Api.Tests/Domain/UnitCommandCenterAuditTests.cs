using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using RentalCommand.Api.DTOs;
using RentalCommand.Api.Services;
using RentalCommand.Api.Services.Auditing;
using RentalCommand.Api.Services.Domain;
using RentalCommand.Api.Tests;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Interfaces;
using RentalCommand.Data;
using RentalCommand.Data.Auditing;
using RentalCommand.TestCommon;

namespace RentalCommand.Api.Tests.Domain;

[Collection(MigratedPostgreSqlCollection.Name)]
public sealed class UnitCommandCenterAuditTests : IAsyncLifetime
{
    private const int PortfolioId = 1;

    private readonly MigratedPostgreSqlFixture _fixture;
    private MigratedPostgreSqlTestContext _ctx = null!;
    private RentalCommandDbContext _db = null!;

    public UnitCommandCenterAuditTests(MigratedPostgreSqlFixture fixture)
    {
        _fixture = fixture;
    }

    public async Task InitializeAsync()
    {
        _ctx = await _fixture.CreateContextAsync();
        _db = _ctx.Db;
    }

    public async Task DisposeAsync() => await _ctx.DisposeAsync();

    [Fact]
    public async Task UnitUpdate_WritesAuditRowShownInUnitTimeline()
    {
        var unit = SeedUnit(marketRent: 950m);
        var unitService = CreateUnitService();

        await unitService.UpdateAsync(PortfolioId, unit.Id, new UpdateUnitRequest
        {
            MarketRent = 1_025m,
        });

        var timeline = await CreateDashboardService().GetTimelineAsync(PortfolioId, unit.Id, skip: 0, take: 10);

        var row = timeline.Should().ContainSingle(r =>
            r.EntityType == "Unit" &&
            r.EntityId == unit.Id &&
            r.Operation == AuditLogOperation.Updated).Subject;
        row.Description.Should().Be("Updated unit");
        row.Changes.Should().ContainSingle(c =>
            c.Field == "Market rent" &&
            c.OldValue == "950" &&
            c.NewValue == "1,025");
    }

    private Unit SeedUnit(decimal marketRent)
    {
        var now = DateTime.UtcNow;
        var property = new Property
        {
            PortfolioId = PortfolioId,
            Name = "Oak Ridge",
            AddressLine1 = "100 Oak",
            City = "Columbus",
            State = "OH",
            PostalCode = "43219",
            CreatedAt = now,
            UpdatedAt = now,
        };
        var unit = new Unit
        {
            Property = property,
            UnitNumber = "3B",
            MarketRent = marketRent,
            CreatedAt = now,
            UpdatedAt = now,
        };
        _db.AddRange(property, unit);
        _db.SaveChanges();
        return unit;
    }

    private UnitService CreateUnitService() =>
        ActivatorUtilities.CreateInstance<UnitService>(
            CreateServices(),
            _db,
            Mock.Of<IDataUpdateService>());

    private UnitDashboardService CreateDashboardService() =>
        new(_db, new AuditDescriber(), new AuditDiffBuilder(), TimeProvider.System);

    private IServiceProvider CreateServices()
    {
        var services = new ServiceCollection();
        services.AddSingleton(_db);
        services.AddSingleton<RentalCommandDbContext>(_db);
        services.AddScoped<IAuditScope, AuditScope>();
        services.AddScoped<IAuditTrailService, AuditTrailService>();
        services.AddSingleton(TimeProvider.System);
        return services.BuildServiceProvider();
    }
}
