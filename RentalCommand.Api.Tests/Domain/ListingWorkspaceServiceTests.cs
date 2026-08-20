using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using RentalCommand.Api.DTOs;
using RentalCommand.Api.Services.Domain;
using RentalCommand.Api.Writes;
using RentalCommand.Core;
using RentalCommand.Core.Atomic;
using RentalCommand.Core.Authorization;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Interfaces;
using RentalCommand.Core.Listings;
using RentalCommand.Data.Documents;
using RentalCommand.TestCommon;

namespace RentalCommand.Api.Tests.Domain;

public sealed class ListingWorkspaceServiceTests : IDisposable
{
    private const int PortfolioId = 1;
    private readonly SqliteTestContext _context = new();
    private readonly CapturingAtomicUnitOfWork _atomic = new();
    private readonly CapturingRequestWriteExecutor _writes = new();
    private readonly ListingWorkspaceService _service;

    public ListingWorkspaceServiceTests()
        => _service = new ListingWorkspaceService(
            _context.Db,
            Mock.Of<IFileStorage>(),
            Mock.Of<IPendingFileUploadStore>(),
            new ListingChannelAdapterResolver([new DisabledZillowListingChannelAdapter()]),
            NullLogger<ListingWorkspaceService>.Instance,
            TimeProvider.System,
            _atomic,
            _writes);

    public void Dispose() => _context.Dispose();

    [Fact]
    public async Task GenerateAsync_DelegatesOneAtomicCommandAndReturnsCurrentWorkspace()
    {
        var listing = SeedListing();
        var scope = Scope();

        var result = await _service.GenerateAsync(scope, listing.UnitId, "generate-1");

        result.Should().NotBeNull();
        _writes.LastOperationName.Should().Be("listing-workspace.generate");
        _writes.LastIdempotencyKey.Should().StartWith($"{PortfolioId}:{listing.UnitId}:");
        _writes.LastCommand.Should().BeEquivalentTo(new GenerateListingWorkspaceCommand(
            scope.PortfolioId, listing.UnitId, scope.UserId, scope.SessionId,
            scope.AccessContextId, scope.AccessRevision));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task GenerateAsync_RejectsMissingCallerOperationKey(string operationKey)
    {
        var listing = SeedListing();

        var action = () => _service.GenerateAsync(Scope(), listing.UnitId, operationKey);

        await action.Should().ThrowAsync<DomainValidationException>()
            .WithMessage("*Request key is required*");
        _writes.LastCommand.Should().BeNull();
    }

    [Fact]
    public async Task ReorderPhotosAsync_DelegatesTheCompleteManifestToKernelOwnedPersistence()
    {
        var listing = SeedListing();
        var scope = Scope();
        var requestedOrder = new[] { 31, 29, 30 };

        await _service.ReorderPhotosAsync(scope, listing.UnitId,
            new ReorderListingPhotosRequest { PhotoIds = requestedOrder }, "reorder-1");

        _writes.LastCommand.Should().BeEquivalentTo(new ReorderListingPhotosCommand(
            scope.PortfolioId, listing.UnitId, scope.UserId, scope.SessionId,
            scope.AccessContextId, scope.AccessRevision, requestedOrder));
    }

    [Fact]
    public async Task IngestSignalAsync_DelegatesProviderMessageKeyToTypedAtomicCommand()
    {
        var listing = SeedListing();
        var publicationId = listing.Publications.Single(item => item.Mode == ListingPublicationMode.Guided).Id;
        var request = new IngestExternalListingSignalRequest
        {
            SignalType = "StatusChanged",
            SuggestedExternalStatus = "Active",
        };

        await _service.IngestSignalAsync(
            Scope(), listing.UnitId, publicationId, "message-1", request);

        _atomic.LastIdentity!.CommandType.Should().Be("listing-workspace.signal.ingest");
        _atomic.LastCommand.Should().BeEquivalentTo(new IngestExternalListingSignalCommand(
            PortfolioId, listing.UnitId, Scope().UserId, Scope().SessionId,
            Scope().AccessContextId, Scope().AccessRevision, publicationId, "message-1",
            "StatusChanged", null, null, "Active"));
    }

    [Fact]
    public void WorkspaceReadShape_FiltersOrdersAndJoinsInOneServerSideQuery()
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

    [Fact]
    public void PhotoMutationReadShape_FiltersListingAndPhotoScopeInSql()
    {
        var query = _context.Db.ListingPhotos.AsNoTracking()
            .Where(photo => photo.Id == 19 && photo.PortfolioId == PortfolioId
                && photo.RentalListing != null && photo.RentalListing.UnitId == 27);

        var sql = query.ToQueryString();

        sql.Should().Contain("JOIN");
        sql.Should().Contain("ListingPhotos");
        sql.Should().Contain("RentalListings");
        sql.Should().Contain("PortfolioId");
        sql.Should().Contain("UnitId");
    }

    private RentalListing SeedListing()
    {
        var now = DateTime.UtcNow;
        var property = new Property
        {
            PortfolioId = PortfolioId,
            Name = "Cedar Point",
            AddressLine1 = "100 Main St",
            City = "Columbus",
            State = "OH",
            PostalCode = "43215",
            CreatedAt = now,
            UpdatedAt = now,
        };
        var unit = new Unit
        {
            PortfolioId = PortfolioId,
            Property = property,
            UnitNumber = "2A",
            Bedrooms = 2,
            Bathrooms = 1.5m,
            SquareFeet = 925,
            MarketRent = 1450m,
            CreatedAt = now,
            UpdatedAt = now,
        };
        _context.Db.AddRange(property, unit);
        _context.Db.SaveChanges();
        var listing = new RentalListing
        {
            PortfolioId = PortfolioId,
            PropertyId = property.Id,
            UnitId = unit.Id,
            Unit = unit,
            Headline = "Two bedroom apartment",
            Description = "A complete listing description.",
            Rent = 1450m,
            ContentVersion = 1,
            CreatedAt = now,
            UpdatedAt = now,
            Publications =
            [
                new ListingPublication
                {
                    PortfolioId = PortfolioId,
                    ProviderKey = ListingProviderKeys.Zillow,
                    Mode = ListingPublicationMode.Guided,
                    CreatedAt = now,
                    UpdatedAt = now,
                },
                new ListingPublication
                {
                    PortfolioId = PortfolioId,
                    ProviderKey = ListingProviderKeys.Zillow,
                    Mode = ListingPublicationMode.Connected,
                    CreatedAt = now,
                    UpdatedAt = now,
                },
            ],
        };
        _context.Db.Add(listing);
        _context.Db.SaveChanges();
        return listing;
    }

    private static WorkspaceReadScope Scope() =>
        new(PortfolioId, 42, Guid.Parse("18e99783-e913-401d-8158-a7feb002667a"), 71, 4);

    private sealed class CapturingAtomicUnitOfWork : IAtomicUnitOfWork
    {
        public AtomicCommandIdentity? LastIdentity { get; private set; }
        public object? LastCommand { get; private set; }

        public Task<AtomicCommandOutcome<TResult>> ExecuteAsync<TCommand, TResult>(
            AtomicCommandIdentity identity,
            TCommand command,
            AtomicJsonResultCodec<TResult> resultCodec,
            CancellationToken ct = default)
            where TCommand : notnull, IAtomicCommandData
            where TResult : notnull
        {
            LastIdentity = identity.BindRequest(command);
            LastCommand = command;
            object result = typeof(TResult) == typeof(ListingWorkspaceMutationResult)
                ? new ListingWorkspaceMutationResult(ListingWorkspaceMutationOutcome.Applied,
                    PortfolioId, ResolveUnitId(command), 1)
                : typeof(TResult) == typeof(IngestExternalListingSignalResult)
                    ? new IngestExternalListingSignalResult(
                        true, 7, "StatusChanged", null, null, "Active",
                        ExternalListingSignalDisposition.Unconfirmed, DateTime.UtcNow)
                    : throw new NotSupportedException($"Unexpected result type {typeof(TResult).Name}.");
            return Task.FromResult(new AtomicCommandOutcome<TResult>(
                (TResult)result, AtomicCommandDisposition.Executed, Guid.NewGuid()));
        }

        private static int ResolveUnitId<TCommand>(TCommand command) => command switch
        {
            IListingWorkspaceAtomicCommand listing => listing.UnitId,
            _ => throw new NotSupportedException($"Unexpected command type {typeof(TCommand).Name}."),
        };
    }

    private sealed class CapturingRequestWriteExecutor : IRequestWriteExecutor
    {
        public string? LastIdempotencyKey { get; private set; }
        public string? LastOperationName { get; private set; }
        public object? LastCommand { get; private set; }

        public Task<AtomicCommandOutcome<TResult>> ExecuteAsync<TCommand, TResult>(
            string idempotencyKey,
            TransactionalWrite<TCommand, TResult> write,
            CancellationToken ct = default)
            where TCommand : notnull, IAtomicCommandData
            where TResult : notnull
        {
            LastIdempotencyKey = idempotencyKey;
            LastOperationName = write.OperationName;
            LastCommand = write.Request;
            object result = typeof(TResult) == typeof(ListingWorkspaceMutationResult)
                ? new ListingWorkspaceMutationResult(
                    ListingWorkspaceMutationOutcome.Applied,
                    PortfolioId,
                    ((IListingWorkspaceAtomicCommand)(object)write.Request).UnitId,
                    1)
                : throw new NotSupportedException($"Unexpected result type {typeof(TResult).Name}.");
            return Task.FromResult(new AtomicCommandOutcome<TResult>(
                (TResult)result, AtomicCommandDisposition.Executed, Guid.NewGuid()));
        }
    }
}
