using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Moq;
using RentalCommand.Api.DTOs;
using RentalCommand.Api.Services.Domain;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Interfaces;
using RentalCommand.TestCommon;

namespace RentalCommand.Api.Tests.Domain;

public sealed class ListingWorkspaceServiceTests : IDisposable
{
    private const int PortfolioId = 1;
    private readonly SqliteTestContext _context = new();
    private readonly ListingWorkspaceService _service;

    public ListingWorkspaceServiceTests()
        => _service = new ListingWorkspaceService(_context.Db, Mock.Of<IDataUpdateService>(),
            Mock.Of<IAuditTrailService>(), TimeProvider.System);

    public void Dispose() => _context.Dispose();

    [Fact]
    public async Task GenerateAsync_CreatesOneProviderNeutralListingWithBothZillowModes()
    {
        var unit = SeedUnit();

        var result = await _service.GenerateAsync(PortfolioId, unit.Id, 42);

        result.Should().NotBeNull();
        result!.Headline.Should().Contain("2 bed");
        result.PhotoManifest.Select(photo => photo.Position).Should().BeInAscendingOrder();
        result.Publications.Should().ContainSingle(item => item.ProviderKey == "Zillow" && item.Mode == "Guided");
        result.Publications.Should().ContainSingle(item => item.ProviderKey == "Zillow" && item.Mode == "Connected");
        result.SignedLeaseImportUrl.Should().Contain($"unitId={unit.Id}");
        _context.Db.RentalListings.Should().ContainSingle(item => item.UnitId == unit.Id);
    }

    [Fact]
    public async Task SaveAsync_ContentChangeMarksPreviouslyPublishedGuidedVersionForRepublish()
    {
        var unit = SeedUnit();
        var generated = await _service.GenerateAsync(PortfolioId, unit.Id, 42);
        await _service.SaveAsync(PortfolioId, unit.Id, new SaveListingWorkspaceRequest
        {
            ZillowGuided = new SaveGuidedPublicationRequest { MarkCurrentVersionPublished = true },
        }, 42);

        var saved = await _service.SaveAsync(PortfolioId, unit.Id,
            new SaveListingWorkspaceRequest { Headline = "Updated title" }, 42);

        saved!.ContentVersion.Should().Be(generated!.ContentVersion + 1);
        saved.Publications.Single(item => item.Mode == "Guided").NeedsRepublish.Should().BeTrue();
    }

    [Fact]
    public async Task IngestSignalAsync_DeduplicatesUntrustedProviderMessageKey()
    {
        var unit = SeedUnit();
        var workspace = await _service.GenerateAsync(PortfolioId, unit.Id, 42);
        var publicationId = workspace!.Publications.Single(item => item.Mode == "Guided").Id;
        var request = new IngestExternalListingSignalRequest
        {
            ProviderMessageKey = "message-1",
            SignalType = "StatusChanged",
            SuggestedExternalStatus = "Active",
        };

        var first = await _service.IngestSignalAsync(PortfolioId, unit.Id, publicationId, request);
        var replay = await _service.IngestSignalAsync(PortfolioId, unit.Id, publicationId, request);

        replay!.Id.Should().Be(first!.Id);
        _context.Db.ExternalListingSignals.Should().ContainSingle();
    }

    [Fact]
    public void WorkspaceReadShape_HasServerSideScopeOrderingAndSignalFilter()
    {
        var query = _context.Db.RentalListings.AsNoTracking()
            .Where(listing => listing.PortfolioId == PortfolioId && listing.UnitId == 27)
            .Include(listing => listing.Photos.OrderBy(photo => photo.Position))
            .Include(listing => listing.Publications.OrderBy(publication => publication.Mode))
                .ThenInclude(publication => publication.ExternalSignals
                    .Where(signal => signal.Disposition == ExternalListingSignalDisposition.Unconfirmed)
                    .OrderByDescending(signal => signal.ReceivedAtUtc))
            .AsSingleQuery();

        var sql = query.ToQueryString();
        sql.Should().Contain("ORDER BY");
        sql.Should().Contain("PortfolioId");
        sql.Should().Contain("Disposition");
    }

    private Unit SeedUnit()
    {
        var now = DateTime.UtcNow;
        var property = new Property
        {
            PortfolioId = PortfolioId, Name = "Cedar Point", AddressLine1 = "100 Main St",
            City = "Columbus", State = "OH", PostalCode = "43215", CreatedAt = now, UpdatedAt = now,
        };
        var unit = new Unit
        {
            PortfolioId = PortfolioId, Property = property, UnitNumber = "2A", Bedrooms = 2,
            Bathrooms = 1.5m, SquareFeet = 925, MarketRent = 1450m, Status = UnitStatus.Vacant,
            CreatedAt = now, UpdatedAt = now,
        };
        _context.Db.AddRange(property, unit);
        _context.Db.SaveChanges();
        return unit;
    }
}
