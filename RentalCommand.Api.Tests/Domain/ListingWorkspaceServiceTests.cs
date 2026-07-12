using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using RentalCommand.Api.DTOs;
using RentalCommand.Api.Services.Domain;
using RentalCommand.Core.Atomic;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Interfaces;
using RentalCommand.TestCommon;
using RentalCommand.Data.Documents;

namespace RentalCommand.Api.Tests.Domain;

public sealed class ListingWorkspaceServiceTests : IDisposable
{
    private const int PortfolioId = 1;
    private readonly SqliteTestContext _context = new();
    private readonly ListingWorkspaceService _service;

    public ListingWorkspaceServiceTests()
        => _service = new ListingWorkspaceService(_context.Db, Mock.Of<IDataUpdateService>(),
            Mock.Of<IAuditTrailService>(), new PermissiveInfrastructureWriteGate(), Mock.Of<IFileStorage>(),
            Mock.Of<IPendingFileUploadStore>(), NullLogger<ListingWorkspaceService>.Instance, TimeProvider.System);

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
    public async Task GenerateAsync_ExistingWorkspaceSyncsUnitDetailsWithoutReplacingCustomizedContent()
    {
        var unit = SeedUnit();
        var generated = await _service.GenerateAsync(PortfolioId, unit.Id, 42);
        var customized = await _service.SaveAsync(PortfolioId, unit.Id, new SaveListingWorkspaceRequest
        {
            Headline = "Sunny corner apartment",
            Description = "User-written listing copy",
            Rent = 1725m,
            SecurityDeposit = 900m,
            LeaseTerms = "Flexible 10- or 12-month lease",
        }, 42);
        unit.Bedrooms = 3;
        unit.SquareFeet = 1100;
        unit.MarketRent = 1900m;
        await _context.Db.SaveChangesAsync();

        var synchronized = await _service.GenerateAsync(PortfolioId, unit.Id, 42);

        synchronized.Should().NotBeNull();
        synchronized!.Headline.Should().Be("Sunny corner apartment");
        synchronized.Description.Should().Be("User-written listing copy");
        synchronized.Rent.Should().Be(1725m);
        synchronized.SecurityDeposit.Should().Be(900m);
        synchronized.LeaseTerms.Should().Be("Flexible 10- or 12-month lease");
        synchronized.Bedrooms.Should().Be(3);
        synchronized.SquareFeet.Should().Be(1100);
        synchronized.ContentVersion.Should().Be(customized!.ContentVersion + 1);
    }

    [Fact]
    public async Task UpdatePhotoAsync_ChangesMetadataAndAdvancesListingContentVersion()
    {
        var unit = SeedUnit();
        var generated = await _service.GenerateAsync(PortfolioId, unit.Id, 42);
        var photo = generated!.PhotoManifest.First();

        var updated = await _service.UpdatePhotoAsync(PortfolioId, unit.Id, photo.Id,
            new UpdateListingPhotoRequest { Category = "Front exterior", Caption = "Street-facing view" }, 42);

        updated!.ContentVersion.Should().Be(generated.ContentVersion + 1);
        updated.PhotoManifest.First(item => item.Id == photo.Id).Should().Match<ListingPhotoResponse>(item =>
            item.Category == "Front exterior" && item.Caption == "Street-facing view");
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

    private sealed class PermissiveInfrastructureWriteGate : IAtomicInfrastructureWriteGate
    {
        public IDisposable BeginPendingFileUploadAdmission() => new Lease();
        public IDisposable BeginExternalListingSignalAdmission() => new Lease();
        private sealed class Lease : IDisposable { public void Dispose() { } }
    }
}
