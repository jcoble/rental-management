using FluentAssertions;
using Moq;
using RentalCommand.Api.DTOs;
using RentalCommand.Api.Services.Domain;
using RentalCommand.Core;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Interfaces;
using RentalCommand.TestCommon;

namespace RentalCommand.Api.Tests.Domain;

public sealed class UnitListingServiceTests : IDisposable
{
    private const int PortfolioId = 1;

    private readonly SqliteTestContext _ctx = new();
    private readonly UnitListingService _sut;

    public UnitListingServiceTests()
    {
        _sut = new UnitListingService(
            _ctx.Db,
            Mock.Of<IDataUpdateService>(),
            Mock.Of<IAuditTrailService>(),
            TimeProvider.System);
    }

    public void Dispose() => _ctx.Dispose();

    [Fact]
    public async Task GenerateForUnitAsync_CreatesZillowManualDraftFromUnitFacts()
    {
        var now = DateTime.UtcNow;
        var unit = SeedUnit("2A", "Cedar Point Flats", UnitStatus.Vacant, now);
        unit.Bedrooms = 2;
        unit.Bathrooms = 1.5m;
        unit.SquareFeet = 925;
        unit.MarketRent = 1450m;
        _ctx.Db.SaveChanges();

        var listing = await _sut.GenerateForUnitAsync(PortfolioId, unit.Id, userId: 42, CancellationToken.None);

        listing.Should().NotBeNull();
        listing!.UnitId.Should().Be(unit.Id);
        listing.PropertyId.Should().Be(unit.PropertyId);
        listing.Channel.Should().Be("ZillowManual");
        listing.Status.Should().Be("Draft");
        listing.Rent.Should().Be(1450m);
        listing.Bedrooms.Should().Be(2);
        listing.Bathrooms.Should().Be(1.5m);
        listing.SquareFeet.Should().Be(925);
        listing.Headline.Should().Contain("2 bed");
        listing.Headline.Should().Contain("1.5 bath");
        listing.Headline.Should().Contain("Cedar Point Flats");
        listing.Description.Should().Contain("Cedar Point Flats");
        listing.Description.Should().Contain("Unit 2A");
        listing.Description.Should().Contain("$1,450");
        listing.Description.Should().NotContain("family", "listing copy must avoid protected-class coded language");
        listing.ZillowListingUrl.Should().BeNull();

        _ctx.Db.UnitListings.Should().ContainSingle(l => l.UnitId == unit.Id && l.Channel == UnitListingChannel.ZillowManual);
    }

    [Fact]
    public async Task SaveAsync_StoresManualZillowStatusAndUrlsForExistingDraft()
    {
        var unit = SeedUnit("1B", "Maple House", UnitStatus.Vacant, DateTime.UtcNow);
        var generated = await _sut.GenerateForUnitAsync(PortfolioId, unit.Id, userId: 42, CancellationToken.None);
        generated.Should().NotBeNull();

        var saved = await _sut.SaveAsync(PortfolioId, unit.Id, new SaveUnitListingRequest
        {
            Status = "Posted",
            Headline = "Updated Zillow title",
            Description = "Updated Zillow description",
            Rent = 1525m,
            SecurityDeposit = 1525m,
            LeaseTerms = "12-month lease",
            PetPolicy = "Pets reviewed case by case",
            Utilities = "Tenant pays electric",
            Parking = "Off-street parking",
            Amenities = "Washer hookup, storage",
            PhotoNotes = "Add exterior and kitchen photos",
            ZillowListingUrl = "https://www.zillow.com/homedetails/example",
            ZillowApplicationUrl = "https://www.zillow.com/renter-hub/applications/example",
            PostedAtUtc = new DateTime(2026, 7, 1, 12, 0, 0, DateTimeKind.Utc),
        }, userId: 42, CancellationToken.None);

        saved.Should().NotBeNull();
        saved!.Status.Should().Be("Posted");
        saved.Headline.Should().Be("Updated Zillow title");
        saved.ZillowListingUrl.Should().Be("https://www.zillow.com/homedetails/example");
        saved.ZillowApplicationUrl.Should().Be("https://www.zillow.com/renter-hub/applications/example");
        saved.PostedAtUtc.Should().Be(new DateTime(2026, 7, 1, 12, 0, 0, DateTimeKind.Utc));
        saved.IsPosted.Should().BeTrue();
    }

    [Theory]
    [InlineData("   ", "description", "Listing headline is required.")]
    [InlineData("Headline", "   ", "Listing description is required.")]
    public async Task SaveAsync_RejectsBlankRequiredListingCopy(
        string headline,
        string description,
        string expectedMessage)
    {
        var unit = SeedUnit("3C", "Oak Flats", UnitStatus.Vacant, DateTime.UtcNow);
        var generated = await _sut.GenerateForUnitAsync(PortfolioId, unit.Id, userId: 42, CancellationToken.None);
        generated.Should().NotBeNull();

        Func<Task> act = () => _sut.SaveAsync(PortfolioId, unit.Id, new SaveUnitListingRequest
        {
            Headline = headline,
            Description = description,
        }, userId: 42, CancellationToken.None);

        var ex = await act.Should().ThrowAsync<DomainValidationException>();
        ex.Which.Message.Should().Be(expectedMessage);
    }

    [Fact]
    public async Task GenerateForUnitAsync_ReturnsNullForUnitOutsidePortfolio()
    {
        var foreignPortfolio = new Portfolio
        {
            Id = 99,
            Name = "Other Portfolio",
            ManagementCompanyName = "Other Co",
            TimeZone = "UTC",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        var foreignProperty = new Property
        {
            Portfolio = foreignPortfolio,
            Name = "Other Place",
            AddressLine1 = "1 Other St",
            City = "Columbus",
            State = "OH",
            PostalCode = "43215",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        var foreignUnit = new Unit
        {
            Property = foreignProperty,
            UnitNumber = "9",
            Bedrooms = 1,
            Bathrooms = 1,
            MarketRent = 900m,
            Status = UnitStatus.Vacant,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        _ctx.Db.AddRange(foreignPortfolio, foreignProperty, foreignUnit);
        _ctx.Db.SaveChanges();

        var result = await _sut.GenerateForUnitAsync(PortfolioId, foreignUnit.Id, userId: 42, CancellationToken.None);

        result.Should().BeNull();
        _ctx.Db.UnitListings.Should().BeEmpty();
    }

    private Unit SeedUnit(string unitNumber, string propertyName, UnitStatus status, DateTime now)
    {
        var property = new Property
        {
            PortfolioId = PortfolioId,
            Name = propertyName,
            AddressLine1 = "100 Main St",
            City = "Columbus",
            State = "OH",
            PostalCode = "43215",
            CreatedAt = now,
            UpdatedAt = now,
        };
        var unit = new Unit
        {
            Property = property,
            UnitNumber = unitNumber,
            Bedrooms = 1,
            Bathrooms = 1,
            MarketRent = 1000m,
            Status = status,
            CreatedAt = now,
            UpdatedAt = now,
        };

        _ctx.Db.AddRange(property, unit);
        _ctx.Db.SaveChanges();
        return unit;
    }
}
