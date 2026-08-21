using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using RentalCommand.Api.DTOs;
using RentalCommand.Api.Services.Domain;
using RentalCommand.Api.Writes;
using RentalCommand.Core.Atomic;
using RentalCommand.Core.Authorization;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Interfaces;
using RentalCommand.Core.Listings;
using RentalCommand.Data;
using RentalCommand.Data.Atomic;
using RentalCommand.Data.Documents;
using RentalCommand.Data.Listings;
using RentalCommand.TestCommon;

namespace RentalCommand.IntegrationTests;

[Collection(RoleAuthorityPostgreSqlCollection.Name)]
public sealed class ListingMetadataCrudWritePostgreSqlTests : IAsyncLifetime
{
    private readonly MigratedPostgreSqlFixture _fixture;
    private MigratedPostgreSqlTestContext _context = null!;

    public ListingMetadataCrudWritePostgreSqlTests(MigratedPostgreSqlFixture fixture) =>
        _fixture = fixture;

    public async Task InitializeAsync() =>
        _context = await _fixture.CreateContextAsync();

    public async Task DisposeAsync() =>
        await _context.DisposeAsync();

    [Fact]
    public void GenerateCommand_PreservesFrozenLegacyFingerprint()
    {
        var command = new GenerateListingWorkspaceCommand(
            1, 42, 7, Guid.Parse("11111111-1111-1111-1111-111111111111"), 9, 3);

        AtomicCommandFingerprint.Create(command).Should().Be(
            "3f61806ec80b08544889b7a1963d70f06cba389b6b7cd1559acb4eccc0f79e86");
    }

    [Fact]
    public async Task SixLocalMutations_ReplayExactly_UseDatabaseAuditClock_AndPreservePhotoBoundaries()
    {
        var seededAt = DateTime.UtcNow.AddMinutes(-5);
        var (scope, unitId) = await SeedScopeAsync(seededAt);
        var fakeClock = new DateTime(2099, 8, 20, 12, 0, 0, DateTimeKind.Utc);

        await using var services = BuildServices(new FixedTimeProvider(fakeClock));
        await using var serviceScope = services.CreateAsyncScope();
        var sut = serviceScope.ServiceProvider.GetRequiredService<ListingWorkspaceService>();

        var generated = (await sut.GenerateAsync(scope, unitId, "listing-generate"))!;
        (await sut.GenerateAsync(scope, unitId, "listing-generate"))
            .Should().BeEquivalentTo(generated);

        var save = new SaveListingWorkspaceRequest
        {
            Headline = "Executor-owned listing",
            Description = "The six local listing mutations share one executor boundary.",
            Rent = 1875m,
            Amenities = "Parking, storage",
        };
        var saved = await sut.SaveAsync(scope, unitId, save, "listing-save");
        (await sut.SaveAsync(scope, unitId, save, "listing-save"))
            .Should().BeEquivalentTo(saved);

        var firstPhotoId = saved!.PhotoManifest[0].Id;
        var secondPhotoId = saved.PhotoManifest[1].Id;
        var attached = await sut.AttachPhotoAsync(
            scope, unitId, firstPhotoId, "listing-photo-finalize",
            "exterior.jpg", "image/jpeg", [1, 2, 3, 4]);
        (await sut.AttachPhotoAsync(
                scope, unitId, firstPhotoId, "listing-photo-finalize",
                "exterior.jpg", "image/jpeg", [1, 2, 3, 4]))
            .Should().BeEquivalentTo(attached);

        var update = new UpdateListingPhotoRequest
        {
            Category = "Living room",
            Caption = "Bright main living space",
        };
        var updated = await sut.UpdatePhotoAsync(
            scope, unitId, secondPhotoId, update, "listing-photo-update");
        (await sut.UpdatePhotoAsync(
                scope, unitId, secondPhotoId, update, "listing-photo-update"))
            .Should().BeEquivalentTo(updated);

        var reversedIds = updated!.PhotoManifest.Select(photo => photo.Id).Reverse().ToArray();
        var reordered = await sut.ReorderPhotosAsync(
            scope, unitId, new ReorderListingPhotosRequest { PhotoIds = reversedIds },
            "listing-photo-reorder");
        (await sut.ReorderPhotosAsync(
                scope, unitId, new ReorderListingPhotosRequest { PhotoIds = reversedIds },
                "listing-photo-reorder"))
            .Should().BeEquivalentTo(reordered);

        var removed = await sut.RemovePhotoAsync(
            scope, unitId, firstPhotoId, "listing-photo-remove");
        (await sut.RemovePhotoAsync(
                scope, unitId, firstPhotoId, "listing-photo-remove"))
            .Should().BeEquivalentTo(removed);

        _context.Db.ChangeTracker.Clear();
        var listing = await _context.Db.RentalListings.AsNoTracking()
            .SingleAsync(row => row.Id == generated.Id);
        listing.Headline.Should().Be(save.Headline);
        listing.Rent.Should().Be(save.Rent);
        listing.CreatedAt.Should().NotBe(fakeClock);

        var generateReceipt = await _context.Db.AtomicCommandReceipts.AsNoTracking()
            .SingleAsync(row => row.CommandType == "listing-workspace.generate");
        generateReceipt.ResultContract.Should().Be("listing-workspace.mutation.result.v1");
        generateReceipt.RequestFingerprint.Should().Be(AtomicCommandFingerprint.Create(
            new GenerateListingWorkspaceCommand(
                scope.PortfolioId, unitId, scope.UserId, scope.SessionId,
                scope.AccessContextId, scope.AccessRevision)));
        var audit = await _context.Db.AtomicAuditLogs.AsNoTracking().SingleAsync(row =>
            row.CommandType == generateReceipt.CommandType
            && row.CommandIdempotencyKey == generateReceipt.IdempotencyKey
            && row.EntityType == nameof(RentalListing)
            && row.EntityId == listing.Id);
        audit.Timestamp.Should().Be(listing.CreatedAt);
        audit.Timestamp.Should().NotBe(fakeClock);

        (await _context.Db.AtomicCommandReceipts.AsNoTracking().CountAsync(row =>
            row.CommandType.StartsWith("listing-workspace.")
            && !row.CommandType.Contains("connected")
            && !row.CommandType.Contains("signal"))).Should().Be(6);
        var orderedIds = await _context.Db.ListingPhotos.AsNoTracking()
            .Where(photo => photo.RentalListingId == listing.Id)
            .OrderBy(photo => photo.Position)
            .Select(photo => photo.Id)
            .ToArrayAsync();
        orderedIds.Should().Equal(reversedIds);
        var removedPhoto = await _context.Db.ListingPhotos.AsNoTracking()
            .SingleAsync(photo => photo.Id == firstPhotoId);
        removedPhoto.StoredFileId.Should().BeNull();
        var stored = await _context.Db.StoredFiles.IgnoreQueryFilters().AsNoTracking()
            .SingleAsync(file => file.EntityType == nameof(ListingPhoto)
                && file.EntityId == firstPhotoId);
        stored.DeletedAt.Should().NotBeNull();
        (await _context.Db.OutboxMessages.AsNoTracking().CountAsync(message =>
            message.MessageType == "blob-delete"
            && message.IdempotencyKey == $"listing-photo-remove:{stored.Id}"))
            .Should().Be(1);
        (await _context.Db.PendingFileUploads.AsNoTracking().SingleAsync(upload =>
            upload.StoredFileId == stored.Id)).State.Should().Be(PendingFileUploadState.Finalized);
    }

    [Fact]
    public async Task ExactReplay_RejectsStaleAuthorization()
    {
        var (scope, unitId) = await SeedScopeAsync(DateTime.UtcNow.AddMinutes(-5));
        const string operationKey = "listing-stale-replay";
        await using (var services = BuildServices(TimeProvider.System))
        await using (var serviceScope = services.CreateAsyncScope())
        {
            await serviceScope.ServiceProvider.GetRequiredService<ListingWorkspaceService>()
                .GenerateAsync(scope, unitId, operationKey);
        }

        var session = await _context.Db.AuthSessions.SingleAsync(row => row.Id == scope.SessionId);
        session.Status = AuthSessionStatus.Revoked;
        session.RevokedAtUtc = DateTime.UtcNow;
        await _context.Db.SaveChangesAsync();
        _context.Db.ChangeTracker.Clear();

        await using var staleServices = BuildServices(TimeProvider.System);
        await using var staleScope = staleServices.CreateAsyncScope();
        Func<Task> replay = () => staleScope.ServiceProvider
            .GetRequiredService<ListingWorkspaceService>()
            .GenerateAsync(scope, unitId, operationKey);
        await replay.Should().ThrowAsync<UnauthorizedAccessException>()
            .WithMessage("The listing is outside the caller's current property scope.");
    }

    private async Task<(WorkspaceReadScope Scope, int UnitId)> SeedScopeAsync(DateTime now)
    {
        var user = await _context.Db.Users.SingleAsync(row => row.Id == 1);
        var accessContext = new WorkspaceAccessContext
        {
            UserId = user.Id,
            PortfolioId = 1,
            Status = WorkspaceAccessContextStatus.Active,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
        };
        var membership = new WorkspaceMembership
        {
            AccessContext = accessContext,
            PortfolioId = 1,
            Status = WorkspaceMembershipStatus.Active,
            DefaultExperience = WorkspaceExperience.Management,
            EffectiveFromUtc = now.AddMinutes(-5),
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
        };
        var assignment = new MembershipRoleAssignment
        {
            WorkspaceMembership = membership,
            PortfolioId = 1,
            RoleProfileId = AccessCatalog.Roles.Single(role =>
                role.Key == RoleProfileKeys.WorkspaceAdministrator).Id,
            Status = MembershipRoleAssignmentStatus.Active,
            ScopeKind = MembershipRoleAssignmentScopeKind.AllProperties,
            EffectiveFromUtc = now.AddMinutes(-5),
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
        };
        var session = new AuthSession
        {
            Id = Guid.NewGuid(),
            UserId = user.Id,
            ActiveAccessContext = accessContext,
            Status = AuthSessionStatus.Active,
            CreatedAtUtc = now,
            LastSeenAtUtc = now,
            ExpiresAtUtc = now.AddDays(30),
        };
        var property = new Property
        {
            PortfolioId = 1,
            Name = "Family 5b property",
            AddressLine1 = "5 Listing Way",
            City = "Columbus",
            State = "OH",
            PostalCode = "43215",
            CreatedAt = now,
            UpdatedAt = now,
        };
        var unit = new Unit
        {
            PortfolioId = 1,
            Property = property,
            UnitNumber = "5B",
            Bedrooms = 2,
            Bathrooms = 1.5m,
            SquareFeet = 975,
            MarketRent = 1800m,
            CreatedAt = now,
            UpdatedAt = now,
        };
        _context.Db.AddRange(accessContext, membership, assignment, session, property, unit);
        await _context.Db.SaveChangesAsync();
        _context.Db.ChangeTracker.Clear();
        return (new WorkspaceReadScope(
            1, user.Id, session.Id, accessContext.Id, accessContext.AccessRevision), unit.Id);
    }

    private ServiceProvider BuildServices(TimeProvider timeProvider)
    {
        var files = new Mock<IFileStorage>();
        files.Setup(storage => storage.UploadAtAsync(
                It.IsAny<Stream>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        var services = new ServiceCollection();
        services.AddSingleton(timeProvider);
        services.AddSingleton(files.Object);
        services.AddSingleton<IListingChannelAdapterResolver>(
            new ListingChannelAdapterResolver([new DisabledZillowListingChannelAdapter()]));
        services.AddSingleton<ILogger<ListingWorkspaceService>>(
            NullLogger<ListingWorkspaceService>.Instance);
        services.AddScoped<ICurrentActor, TestActor>();
        services.AddAtomicPersistenceKernel();
        services.AddPendingFileUploadStore();
        services.AddScoped<IRequestWriteExecutor, RequestWriteExecutor>();
        services.AddScoped<ListingWorkspaceService>();
        services.AddDbContext<RentalCommandDbContext>((provider, options) =>
            options.UseNpgsql(_context.ConnectionString)
                .UseAtomicPersistenceKernel(provider));
        return services.BuildServiceProvider(
            new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
    }

    private sealed class FixedTimeProvider(DateTime now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(now);
    }

    private sealed class TestActor : ICurrentActor
    {
        public int? UserId => null;
        public string? ActorLabel => "integration:listing-metadata-crud";
        public string? IpAddress => "127.0.0.1";
    }
}
