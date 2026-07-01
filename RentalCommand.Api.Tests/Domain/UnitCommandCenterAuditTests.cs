using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using RentalCommand.Api.DTOs;
using RentalCommand.Api.Services;
using RentalCommand.Api.Services.Auditing;
using RentalCommand.Api.Services.Domain;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Interfaces;
using RentalCommand.Data;
using RentalCommand.Data.Auditing;

namespace RentalCommand.Api.Tests.Domain;

public sealed class UnitCommandCenterAuditTests : IDisposable
{
    private const int PortfolioId = 1;

    private readonly SqliteConnection _conn;
    private readonly RentalCommandDbContext _db;

    public UnitCommandCenterAuditTests()
    {
        _conn = new SqliteConnection("DataSource=:memory:");
        _conn.Open();

        var options = new DbContextOptionsBuilder<RentalCommandDbContext>()
            .UseSqlite(_conn)
            .Options;

        _db = new AccountingServiceTestDbContext(options);
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
    }

    public void Dispose()
    {
        _db.Dispose();
        _conn.Dispose();
    }

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

    [Fact]
    public async Task UnitDocumentUploadAndDelete_WriteTimelineFileChanges()
    {
        var unit = SeedUnit(marketRent: 950m);

        var created = await CreateDocumentService().CreateAsync(
            PortfolioId,
            "Unit",
            unit.Id,
            "lease-photo.jpg",
            "image/jpeg",
            2048,
            "uploads/lease-photo.jpg");

        await CreateDocumentService().DeleteAsync(PortfolioId, created.Id);

        var timeline = await CreateDashboardService().GetTimelineAsync(PortfolioId, unit.Id, skip: 0, take: 10);

        timeline.Should().HaveCount(2);
        timeline.Should().Contain(r =>
            r.EntityType == "Unit" &&
            r.EntityId == unit.Id &&
            r.Operation == AuditLogOperation.Updated &&
            r.Changes.Any(c => c.Field == "Document" && c.OldValue == "—" && c.NewValue == "lease-photo.jpg"));
        timeline.Should().Contain(r =>
            r.EntityType == "Unit" &&
            r.EntityId == unit.Id &&
            r.Operation == AuditLogOperation.Updated &&
            r.Changes.Any(c => c.Field == "Document" && c.OldValue == "lease-photo.jpg" && c.NewValue == "—"));
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
            Status = UnitStatus.Vacant,
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

    private DocumentService CreateDocumentService() =>
        ActivatorUtilities.CreateInstance<DocumentService>(
            CreateServices(),
            _db);

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
