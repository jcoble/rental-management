using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using RentalCommand.Api.DTOs;
using RentalCommand.Api.Services.Domain;
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

[Collection(RoleAuthorityPostgreSqlCollection3.Name)]
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
    public void FiveConnectedCommands_PreserveFrozenLegacyFingerprintsAndExclusions()
    {
        var session = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var intent = new AdmitConnectedListingIntentCommand(
            1, 77, 1, session, 9, 3, 79, 78, 4, ConnectedListingIntentOperation.Prepare);
        var persist = new PersistConnectedListingResultCommand(
            1, 76, 77, 1, 79, 78, 4,
            Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"),
            "listing-workspace.connected.prepare.intent", "1:77:legacy-intent",
            "listing-workspace.connected-intent.result.v1", ListingPublicationStatus.Ready,
            "delivery", "Prepared", null, null, null, false, "Prepared legacy package");
        var providerResult = new ConnectedListingPersistenceResult(
            ListingWorkspaceMutationOutcome.Applied, 1, 76, 77, 78, 79,
            Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"), false, true,
            ListingPublicationStatus.Ready, "delivery", "Prepared", null, null, null);
        var apply = new ApplyConnectedListingResultCommand(
            1, 76, 77, 1, 4, Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"),
            "listing-workspace.connected.persist-result", "provider-result-key",
            "listing-workspace.connected-persistence.result.v1", providerResult, false,
            "Applied legacy package");
        var ingest = new IngestExternalListingSignalCommand(
            1, 77, 1, session, 9, 3, 79, "provider-message", "StatusChanged",
            "external-1", "https://provider.test/1", "Active");
        var confirm = new ConfirmExternalListingSignalCommand(
            1, 77, 1, session, 9, 3, 80, true);

        // Frozen legacy fingerprints. Never regenerate these constants from the current model.
        AtomicCommandFingerprint.Create(intent).Should().Be(
            "07a49b94414c23f74d55b607273c59dbc48561b3886fdb168eaf0f803370aae8");
        AtomicCommandFingerprint.Create(persist).Should().Be(
            "5322dc9a2cae31bbee362f184227ecd9ed37ba809ed501685d76aaf07fd7c546");
        AtomicCommandFingerprint.Create(apply).Should().Be(
            "55b067c660e248c1423b6cb57944cdddb70c3def191882985dda7b0db06bbac0");
        AtomicCommandFingerprint.Create(ingest).Should().Be(
            "2d3648a0663f42ca8ba53b4b9c5ba29fe7fbce024279e957449dedeaab373f75");
        AtomicCommandFingerprint.Create(confirm).Should().Be(
            "1226d3f51118f3cad17784e40ccf6dc6ea41a603e25b52ab945b3c49dd7c3bf0");

        AtomicCommandFingerprint.Create(intent with
        {
            AuthSessionId = Guid.NewGuid(), AccessContextId = 10, AccessRevision = 4,
        }).Should().Be(AtomicCommandFingerprint.Create(intent));
        AtomicCommandFingerprint.Create(ingest with
        {
            AuthSessionId = Guid.NewGuid(), AccessContextId = 10, AccessRevision = 4,
        }).Should().Be(AtomicCommandFingerprint.Create(ingest));
        AtomicCommandFingerprint.Create(confirm with
        {
            AuthSessionId = Guid.NewGuid(), AccessContextId = 10, AccessRevision = 4,
        }).Should().Be(AtomicCommandFingerprint.Create(confirm));
        AtomicCommandFingerprint.Create(intent with { PublicationId = 99 })
            .Should().NotBe(AtomicCommandFingerprint.Create(intent));
        AtomicCommandFingerprint.Create(persist with { DeliveryKey = "different" })
            .Should().NotBe(AtomicCommandFingerprint.Create(persist));
        AtomicCommandFingerprint.Create(apply with { Reason = "different" })
            .Should().NotBe(AtomicCommandFingerprint.Create(apply));

        var frozenPersist = new PersistConnectedListingResultCommand(
            1, 9102, 9103, 1, 9105, 9104, 4,
            Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"),
            "listing-workspace.connected.prepare.intent",
            "1:9103:070f040da6a0029e080015333eef05834de218e815f88daddb02746f9f66ae4b",
            "listing-workspace.connected-intent.result.v1", ListingPublicationStatus.Ready,
            "4:cHJlcGFyZWQtcGFja2FnZQ==", "Prepared", null, null, null, false,
            "Prepared Connected listing package");
        var frozenProvider = new ConnectedListingPersistenceResult(
            ListingWorkspaceMutationOutcome.Applied, 1, 9102, 9103, 9104, 9105,
            Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"), false, true,
            ListingPublicationStatus.Ready, "4:cHJlcGFyZWQtcGFja2FnZQ==", "Prepared",
            null, null, null);
        var frozenApply = new ApplyConnectedListingResultCommand(
            1, 9102, 9103, 1, 4,
            Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"),
            "listing-workspace.connected.persist-result",
            "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa:4c45e5506922a031d4f494946e70d9c4ea664edd91d046dd13bd8ec2db25de5e",
            "listing-workspace.connected-persistence.result.v1", frozenProvider, false,
            "Prepared Connected listing package");
        AtomicCommandFingerprint.Create(frozenPersist).Should().Be(
            "96fca52b0c49c5ea9b88f2252ea7debcec37a3d786e7636f5242ca6e0dba0df3");
        AtomicCommandFingerprint.Create(frozenApply).Should().Be(
            "e96b5eab560fb96300cffce9c728fd261c7c1cd9eed5df1a40b99e285be75140");
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

    [Fact]
    public async Task ConnectedProviderAndSignalWrites_ReplayExactly_WithoutRepeatingProviderCallsOrRows()
    {
        var (scope, unitId) = await SeedScopeAsync(DateTime.UtcNow.AddMinutes(-5));
        var adapter = new RecordingListingAdapter();
        await using var services = BuildServices(TimeProvider.System, adapter);
        await using var serviceScope = services.CreateAsyncScope();
        var sut = serviceScope.ServiceProvider.GetRequiredService<ListingWorkspaceService>();
        var scopedDb = serviceScope.ServiceProvider.GetRequiredService<RentalCommandDbContext>();
        adapter.HasLocalTransaction = () => scopedDb.Database.CurrentTransaction is not null;

        await sut.GenerateAsync(scope, unitId, "connected-generate");
        await sut.SaveAsync(scope, unitId, new SaveListingWorkspaceRequest
        {
            Headline = "Connected executor listing",
            Description = "Provider calls remain outside each receipt-backed local transaction.",
            Rent = 2100m,
        }, "connected-save");
        var publicationId = await _context.Db.ListingPublications.AsNoTracking()
            .Where(row => row.RentalListing!.UnitId == unitId
                && row.Mode == ListingPublicationMode.Connected)
            .Select(row => row.Id)
            .SingleAsync();

        await sut.PrepareConnectedAsync(scope, unitId, publicationId, " connected-prepare ");
        await sut.PrepareConnectedAsync(scope, unitId, publicationId, " connected-prepare ");
        var published = await sut.PublishConnectedAsync(scope, unitId, publicationId, "connected-publish");
        (await sut.PublishConnectedAsync(scope, unitId, publicationId, "connected-publish"))
            .Should().BeEquivalentTo(published);
        await sut.UpdateConnectedAsync(scope, unitId, publicationId, "connected-update");
        await sut.UpdateConnectedAsync(scope, unitId, publicationId, "connected-update");
        await sut.UnpublishConnectedAsync(scope, unitId, publicationId, "connected-unpublish");
        await sut.UnpublishConnectedAsync(scope, unitId, publicationId, "connected-unpublish");

        var signal = await sut.IngestSignalAsync(scope, unitId, publicationId,
            " provider-message ", new IngestExternalListingSignalRequest
            {
                SignalType = "StatusChanged",
                SuggestedExternalListingId = "external-1",
                SuggestedListingUrl = "https://provider.test/listing/external-1",
                SuggestedExternalStatus = "Active",
            });
        var signalReplay = await sut.IngestSignalAsync(scope, unitId, publicationId,
            " provider-message ", new IngestExternalListingSignalRequest
            {
                SignalType = "StatusChanged",
                SuggestedExternalListingId = "external-1",
                SuggestedListingUrl = "https://provider.test/listing/external-1",
                SuggestedExternalStatus = "Active",
            });
        signalReplay.Should().BeEquivalentTo(signal);
        await sut.ConfirmSignalAsync(scope, unitId, signal!.Id, true, "signal-confirm");
        await sut.ConfirmSignalAsync(scope, unitId, signal.Id, true, "signal-confirm");

        adapter.PrepareCalls.Should().Be(1);
        adapter.PublishCalls.Should().Be(1);
        adapter.UpdateCalls.Should().Be(1);
        adapter.UnpublishCalls.Should().Be(1);
        adapter.SawLocalTransaction.Should().BeFalse();
        (await _context.Db.ExternalListingSignals.AsNoTracking().CountAsync(row =>
            row.PortfolioId == scope.PortfolioId && row.ProviderMessageKey == "provider-message"))
            .Should().Be(1);
        (await _context.Db.AtomicCommandReceipts.AsNoTracking().CountAsync(row =>
            row.CommandType.StartsWith("listing-workspace.connected.")
            || row.CommandType.StartsWith("listing-workspace.signal."))).Should().Be(14);
        (await _context.Db.AtomicCommandReceipts.AsNoTracking().GroupBy(row => new
            {
                row.CommandType,
                row.IdempotencyKey,
            }).Where(group => group.Count() != 1).CountAsync()).Should().Be(0);
        var publication = await _context.Db.ListingPublications.AsNoTracking()
            .SingleAsync(row => row.Id == publicationId);
        publication.LastDeliveryAttemptAtUtc.Should().NotBeNull();
        publication.LastDeliveryAttemptAtUtc.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromMinutes(1));
    }

    [Fact]
    public async Task AdmissionOnlyPublishReplay_ResumesProviderOnceWithoutDuplicateState()
    {
        var (scope, unitId) = await SeedScopeAsync(DateTime.UtcNow.AddMinutes(-5));
        var adapter = new RecordingListingAdapter();
        await using var services = BuildServices(TimeProvider.System, adapter);
        await using var serviceScope = services.CreateAsyncScope();
        var sut = serviceScope.ServiceProvider.GetRequiredService<ListingWorkspaceService>();

        await sut.GenerateAsync(scope, unitId, "admission-only-generate");
        await sut.SaveAsync(scope, unitId, new SaveListingWorkspaceRequest
        {
            Headline = "Admission-only replay listing",
            Description = "A durable admission without downstream receipts resumes publishing.",
            Rent = 2100m,
        }, "admission-only-save");
        var target = await _context.Db.RentalListings.AsNoTracking()
            .Where(row => row.UnitId == unitId)
            .Select(row => new
            {
                Listing = row,
                Publication = row.Publications.Single(item => item.Mode == ListingPublicationMode.Connected),
            })
            .SingleAsync();
        await sut.PrepareConnectedAsync(
            scope, unitId, target.Publication.Id, "admission-only-prepare");

        const string operationKey = "admission-only-publish";
        var admissionKey = $"{scope.PortfolioId}:{unitId}:{Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(operationKey))).ToLowerInvariant()}";
        var admission = new AdmitConnectedListingIntentCommand(
            scope.PortfolioId, unitId, scope.UserId, scope.SessionId,
            scope.AccessContextId, scope.AccessRevision, target.Publication.Id,
            target.Listing.Id, target.Listing.ContentVersion, ConnectedListingIntentOperation.Publish);
        await serviceScope.ServiceProvider.GetRequiredService<IWriteExecutor>().ExecuteAsync(
            admissionKey, ConnectedListingWriteSupport.Write(admission,
                serviceScope.ServiceProvider.GetRequiredService<RentalCommandDbContext>()));

        var published = await sut.PublishConnectedAsync(
            scope, unitId, target.Publication.Id, operationKey);
        (await sut.PublishConnectedAsync(scope, unitId, target.Publication.Id, operationKey))
            .Should().BeEquivalentTo(published);

        adapter.PublishCalls.Should().Be(1);
        (await _context.Db.AtomicCommandReceipts.AsNoTracking().CountAsync(row =>
            row.CommandType == "listing-workspace.connected.publish.intent")).Should().Be(1);
        (await _context.Db.AtomicCommandReceipts.AsNoTracking().CountAsync(row =>
            row.CommandType == "listing-workspace.connected.persist-result")).Should().Be(2);
        (await _context.Db.AtomicCommandReceipts.AsNoTracking().CountAsync(row =>
            row.CommandType == "listing-workspace.connected.apply-result")).Should().Be(2);
        (await _context.Db.AtomicCommandReceipts.AsNoTracking().GroupBy(row => new
            {
                row.CommandType,
                row.IdempotencyKey,
            }).Where(group => group.Count() != 1).CountAsync()).Should().Be(0);
        (await _context.Db.ListingPublications.AsNoTracking().CountAsync(row =>
            row.Id == target.Publication.Id && row.Status == ListingPublicationStatus.Published))
            .Should().Be(1);
        (await _context.Db.RentalListings.AsNoTracking().CountAsync(row =>
            row.Id == target.Listing.Id)).Should().Be(1);
        (await _context.Db.ListingPublications.AsNoTracking().CountAsync(row =>
            row.RentalListingId == target.Listing.Id)).Should().Be(2);
    }

    [Fact]
    public async Task PersistedPublishReplay_AppliesStoredResultWithoutCallingProviderAgain()
    {
        var (scope, unitId) = await SeedScopeAsync(DateTime.UtcNow.AddMinutes(-5));
        var adapter = new RecordingListingAdapter();
        await using var services = BuildServices(TimeProvider.System, adapter);
        await using var serviceScope = services.CreateAsyncScope();
        var sut = serviceScope.ServiceProvider.GetRequiredService<ListingWorkspaceService>();

        await sut.GenerateAsync(scope, unitId, "persisted-replay-generate");
        await sut.SaveAsync(scope, unitId, new SaveListingWorkspaceRequest
        {
            Headline = "Persisted replay listing",
            Description = "A durable provider result resumes at the missing apply step.",
            Rent = 2100m,
        }, "persisted-replay-save");
        var target = await _context.Db.RentalListings.AsNoTracking()
            .Where(row => row.UnitId == unitId)
            .Select(row => new
            {
                Listing = row,
                Publication = row.Publications.Single(item => item.Mode == ListingPublicationMode.Connected),
            })
            .SingleAsync();
        await sut.PrepareConnectedAsync(
            scope, unitId, target.Publication.Id, "persisted-replay-prepare");

        const string operationKey = "persisted-replay-publish";
        var admissionKey = $"{scope.PortfolioId}:{unitId}:{Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(operationKey))).ToLowerInvariant()}";
        var admissionAttempt = Guid.Parse("12121212-1212-1212-1212-121212121212");
        var persistenceAttempt = Guid.Parse("34343434-3434-3434-3434-343434343434");
        var admission = new AdmitConnectedListingIntentCommand(
            scope.PortfolioId, unitId, scope.UserId, scope.SessionId,
            scope.AccessContextId, scope.AccessRevision, target.Publication.Id,
            target.Listing.Id, target.Listing.ContentVersion, ConnectedListingIntentOperation.Publish);
        var admissionResult = new ConnectedListingIntentResult(
            ListingWorkspaceMutationOutcome.Applied, scope.PortfolioId, target.Listing.PropertyId,
            unitId, target.Listing.Id, target.Publication.Id, target.Listing.ContentVersion);
        var persistenceKey = $"{admissionAttempt:N}:persisted-provider-result";
        var providerResult = new ConnectedListingPersistenceResult(
            ListingWorkspaceMutationOutcome.Applied, scope.PortfolioId, target.Listing.PropertyId,
            unitId, target.Listing.Id, target.Publication.Id, admissionAttempt,
            AppliedToCurrentPublication: false, ReconciliationRequired: true,
            ListingPublicationStatus.Published, "persisted-delivery", "Published", null,
            "external-persisted", "https://provider.test/listing/external-persisted");
        _context.Db.AtomicCommandReceipts.AddRange(
            CompletedReceipt(admissionAttempt, "listing-workspace.connected.publish.intent",
                admissionKey, "listing-workspace.connected-intent.result.v1",
                JsonSerializer.Serialize(admissionResult), AtomicCommandFingerprint.Create(admission)),
            CompletedReceipt(persistenceAttempt, "listing-workspace.connected.persist-result",
                persistenceKey, "listing-workspace.connected-persistence.result.v1",
                JsonSerializer.Serialize(providerResult)));
        await _context.Db.SaveChangesAsync();
        _context.Db.ChangeTracker.Clear();

        var published = await sut.PublishConnectedAsync(
            scope, unitId, target.Publication.Id, operationKey);
        (await sut.PublishConnectedAsync(scope, unitId, target.Publication.Id, operationKey))
            .Should().BeEquivalentTo(published);

        adapter.PublishCalls.Should().Be(0);
        (await _context.Db.AtomicCommandReceipts.AsNoTracking().CountAsync(row =>
            row.CommandType == "listing-workspace.connected.apply-result"
            && row.IdempotencyKey == persistenceAttempt.ToString("N"))).Should().Be(1);
        (await _context.Db.AtomicCommandReceipts.AsNoTracking().CountAsync(row =>
            row.CommandType == "listing-workspace.connected.persist-result"
            && row.ResultJson == JsonSerializer.Serialize(providerResult))).Should().Be(1);
        var publication = await _context.Db.ListingPublications.AsNoTracking()
            .SingleAsync(row => row.Id == target.Publication.Id);
        publication.Status.Should().Be(ListingPublicationStatus.Published);
        publication.LastDeliveryKey.Should().Be(providerResult.DeliveryKey);
        publication.ExternalListingId.Should().Be(providerResult.ExternalListingId);
        publication.PublishedContentVersion.Should().Be(target.Listing.ContentVersion);
        (await _context.Db.RentalListings.AsNoTracking().SingleAsync(row => row.Id == target.Listing.Id))
            .Status.Should().Be(RentalListingStatus.Published);
    }

    [Fact]
    public async Task FiveConnectedRules_ExecuteThroughRecorderWithExactLegacyLockSequences()
    {
        var now = DateTime.UtcNow.AddMinutes(-5);
        var (scope, unitId) = await SeedScopeAsync(now);
        await using var services = BuildServices(TimeProvider.System);
        await using var serviceScope = services.CreateAsyncScope();
        var sut = serviceScope.ServiceProvider.GetRequiredService<ListingWorkspaceService>();
        await sut.GenerateAsync(scope, unitId, "lock-generate");
        await sut.SaveAsync(scope, unitId, new SaveListingWorkspaceRequest
        {
            Headline = "Connected lock listing",
            Description = "Executor recorder proves the legacy lock sequence.",
            Rent = 1900m,
        }, "lock-save");
        var listing = await _context.Db.RentalListings.AsNoTracking()
            .SingleAsync(row => row.UnitId == unitId);
        var publication = await _context.Db.ListingPublications.AsNoTracking()
            .SingleAsync(row => row.RentalListingId == listing.Id
                && row.Mode == ListingPublicationMode.Connected);
        var recorder = new RecordingRuleExecutor(now);

        var intent = new AdmitConnectedListingIntentCommand(
            scope.PortfolioId, unitId, scope.UserId, scope.SessionId,
            scope.AccessContextId, scope.AccessRevision, publication.Id,
            listing.Id, listing.ContentVersion, ConnectedListingIntentOperation.Prepare);
        await recorder.ExecuteAsync("intent", ConnectedListingWriteSupport.Write(intent, _context.Db));
        recorder.Acquired.Should().Equal(
            $"AuthSession:{scope.SessionId}", $"WorkspaceAccessContext:{scope.AccessContextId}",
            $"Unit:{unitId}");

        var admissionAttempt = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
        var admissionKey = $"{scope.PortfolioId}:{unitId}:legacy-intent";
        _context.Db.AtomicCommandReceipts.Add(CompletedReceipt(
            admissionAttempt, "listing-workspace.connected.prepare.intent", admissionKey,
            "listing-workspace.connected-intent.result.v1",
            JsonSerializer.Serialize(new ConnectedListingIntentResult(
                ListingWorkspaceMutationOutcome.Applied, scope.PortfolioId, listing.PropertyId,
                unitId, listing.Id, publication.Id, listing.ContentVersion))));
        await _context.Db.SaveChangesAsync();
        var persist = new PersistConnectedListingResultCommand(
            scope.PortfolioId, listing.PropertyId, unitId, scope.UserId, publication.Id,
            listing.Id, listing.ContentVersion, admissionAttempt,
            "listing-workspace.connected.prepare.intent", admissionKey,
            "listing-workspace.connected-intent.result.v1", ListingPublicationStatus.Ready,
            "delivery", "Prepared", null, null, null, false, "Prepared");
        recorder.Clear();
        var persisted = await recorder.ExecuteAsync(
            "persist", ConnectedListingWriteSupport.Write(persist, _context.Db));
        recorder.Acquired.Should().BeEmpty();

        var providerAttempt = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
        var providerKey = $"{admissionAttempt:N}:legacy-provider";
        _context.Db.AtomicCommandReceipts.Add(CompletedReceipt(
            providerAttempt, "listing-workspace.connected.persist-result", providerKey,
            "listing-workspace.connected-persistence.result.v1",
            JsonSerializer.Serialize(persisted.Value)));
        await _context.Db.SaveChangesAsync();
        var apply = new ApplyConnectedListingResultCommand(
            scope.PortfolioId, listing.PropertyId, unitId, scope.UserId, listing.ContentVersion,
            providerAttempt, "listing-workspace.connected.persist-result", providerKey,
            "listing-workspace.connected-persistence.result.v1", persisted.Value, false, "Applied");
        recorder.Clear();
        await recorder.ExecuteAsync("apply", ConnectedListingWriteSupport.Write(apply, _context.Db));
        recorder.Acquired.Should().Equal($"Unit:{unitId}");
        _context.Db.ChangeTracker.Clear();

        var ingest = new IngestExternalListingSignalCommand(
            scope.PortfolioId, unitId, scope.UserId, scope.SessionId,
            scope.AccessContextId, scope.AccessRevision, publication.Id, "provider-message",
            "StatusChanged", null, null, "Active");
        recorder.Clear();
        await recorder.ExecuteAsync("ingest", ConnectedListingWriteSupport.Write(ingest, _context.Db));
        recorder.Acquired.Select(value => value.Split(':')[0]).Should().Equal(
            "ExternalListingSignal", "AuthSession", "WorkspaceAccessContext", "Unit");
        _context.Db.ChangeTracker.Clear();

        var signal = new ExternalListingSignal
        {
            PortfolioId = scope.PortfolioId,
            ListingPublicationId = publication.Id,
            ProviderMessageKey = "confirm-message",
            SignalType = "StatusChanged",
            Disposition = ExternalListingSignalDisposition.Unconfirmed,
            ReceivedAtUtc = now,
        };
        _context.Db.Add(signal);
        await _context.Db.SaveChangesAsync();
        _context.Db.ChangeTracker.Clear();
        var confirm = new ConfirmExternalListingSignalCommand(
            scope.PortfolioId, unitId, scope.UserId, scope.SessionId,
            scope.AccessContextId, scope.AccessRevision, signal.Id, false);
        recorder.Clear();
        await recorder.ExecuteAsync("confirm", ConnectedListingWriteSupport.Write(confirm, _context.Db));
        recorder.Acquired.Should().Equal(
            $"AuthSession:{scope.SessionId}", $"WorkspaceAccessContext:{scope.AccessContextId}",
            $"Unit:{unitId}");
        _context.Db.ChangeTracker.Clear();
    }

    [Fact]
    public async Task FrozenLegacyReceipts_ReplayThroughProductionCallersAndRealAuthorization()
    {
        var frozenNow = new DateTime(2026, 8, 21, 12, 0, 0, DateTimeKind.Utc);
        var sessionId = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var (scope, unitId) = await SeedScopeAsync(
            frozenNow.AddMinutes(-5), 9101, 9102, 9103, sessionId);
        var listing = new RentalListing
        {
            Id = 9104, PortfolioId = 1, PropertyId = 9102, UnitId = unitId,
            Headline = "Frozen connected listing", Description = "Frozen legacy replay fixture.",
            Rent = 2200m, ContentVersion = 4, Status = RentalListingStatus.ReadyToPublish,
            CreatedAt = frozenNow, UpdatedAt = frozenNow,
        };
        var publication = new ListingPublication
        {
            Id = 9105, PortfolioId = 1, RentalListing = listing,
            ProviderKey = ListingProviderKeys.Zillow, Mode = ListingPublicationMode.Connected,
            Status = ListingPublicationStatus.Draft, CreatedAt = frozenNow, UpdatedAt = frozenNow,
        };
        _context.Db.AddRange(listing, publication);

        const string intentKey =
            "1:9103:070f040da6a0029e080015333eef05834de218e815f88daddb02746f9f66ae4b";
        const string persistKey =
            "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa:4c45e5506922a031d4f494946e70d9c4ea664edd91d046dd13bd8ec2db25de5e";
        const string ingestKey =
            "1:9103:22422fd5bebcc7dcee412cc79fae8494e63d7fc985d0cb5653afb54a45514f30";
        const string confirmKey =
            "1:9103:69bd1d34a6074e79d112570f6300714c8f3646e106afe17302ac04db916350e0";
        const string intentJson =
            "{\"Outcome\":1,\"PortfolioId\":1,\"PropertyId\":9102,\"UnitId\":9103,\"RentalListingId\":9104,\"PublicationId\":9105,\"ContentVersion\":4}";
        const string providerJson =
            "{\"Outcome\":1,\"PortfolioId\":1,\"PropertyId\":9102,\"UnitId\":9103,\"RentalListingId\":9104,\"PublicationId\":9105,\"AdmissionAttemptId\":\"aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa\",\"AppliedToCurrentPublication\":false,\"ReconciliationRequired\":true,\"ProviderStatus\":1,\"DeliveryKey\":\"4:cHJlcGFyZWQtcGFja2FnZQ==\",\"DeliveryStatus\":\"Prepared\",\"DeliveryError\":null,\"ExternalListingId\":null,\"ListingUrl\":null}";

        // Frozen BASE receipts: literal operations/keys, hand-written JSON, and fingerprints that
        // must never be regenerated from the current model or caller helpers.
        _context.Db.AtomicCommandReceipts.AddRange(
            CompletedReceipt(Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"),
                "listing-workspace.connected.prepare.intent", intentKey,
                "listing-workspace.connected-intent.result.v1", intentJson,
                "9cef0c07b0b50cbd61593b49cdea9f05ad750546f7098d9ddd19e8ad67a1f4cd"),
            CompletedReceipt(Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"),
                "listing-workspace.connected.persist-result", persistKey,
                "listing-workspace.connected-persistence.result.v1", providerJson,
                "96fca52b0c49c5ea9b88f2252ea7debcec37a3d786e7636f5242ca6e0dba0df3"),
            CompletedReceipt(Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc"),
                "listing-workspace.connected.apply-result",
                "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
                "listing-workspace.connected-application.result.v1", providerJson,
                "e96b5eab560fb96300cffce9c728fd261c7c1cd9eed5df1a40b99e285be75140"),
            CompletedReceipt(Guid.Parse("dddddddd-dddd-dddd-dddd-dddddddddddd"),
                "listing-workspace.signal.ingest", ingestKey,
                "listing-workspace.signal-ingest.result.v1",
                "{\"Found\":true,\"SignalId\":9106,\"SignalType\":\"StatusChanged\",\"SuggestedExternalListingId\":\"external-legacy\",\"SuggestedListingUrl\":\"https://provider.test/legacy\",\"SuggestedExternalStatus\":\"Active\",\"Disposition\":0,\"ReceivedAtUtc\":\"2026-08-21T12:00:00Z\"}",
                "e8c122716822a73f156faa2b79f2190520a4d6838bb2d6922cc049bf57c9869c"),
            CompletedReceipt(Guid.Parse("eeeeeeee-eeee-eeee-eeee-eeeeeeeeeeee"),
                "listing-workspace.signal.confirm", confirmKey,
                "listing-workspace.mutation.result.v1",
                "{\"Outcome\":1,\"PortfolioId\":1,\"UnitId\":9103,\"RentalListingId\":9104}",
                "cc81c9c7f62740d9d90ccee029e8f65fb870c8b658792d21ad7cdb2a31a20cbb"));
        await _context.Db.SaveChangesAsync();
        _context.Db.ChangeTracker.Clear();

        var adapter = new RecordingListingAdapter();
        await using (var services = BuildServices(TimeProvider.System, adapter))
        await using (var serviceScope = services.CreateAsyncScope())
        {
            var sut = serviceScope.ServiceProvider.GetRequiredService<ListingWorkspaceService>();
            (await sut.PrepareConnectedAsync(scope, unitId, publication.Id, "legacy-prepare"))
                .Should().NotBeNull();
            (await sut.IngestSignalAsync(scope, unitId, publication.Id,
                "legacy-provider-message", new IngestExternalListingSignalRequest
                {
                    SignalType = "StatusChanged", SuggestedExternalListingId = "external-legacy",
                    SuggestedListingUrl = "https://provider.test/legacy",
                    SuggestedExternalStatus = "Active",
                }))!.Id.Should().Be(9106);
            (await sut.ConfirmSignalAsync(scope, unitId, 9106, true, "legacy-confirm"))
                .Should().NotBeNull();
        }
        adapter.PrepareCalls.Should().Be(0);
        (await _context.Db.RentalListings.CountAsync(row => row.Id == listing.Id)).Should().Be(1);
        (await _context.Db.ListingPublications.CountAsync(row => row.Id == publication.Id)).Should().Be(1);
        (await _context.Db.ExternalListingSignals.CountAsync()).Should().Be(0);
        (await _context.Db.AtomicCommandReceipts.CountAsync(row =>
            row.CommandType.StartsWith("listing-workspace.connected.")
            || row.CommandType.StartsWith("listing-workspace.signal."))).Should().Be(5);

        var session = await _context.Db.AuthSessions.SingleAsync(row => row.Id == sessionId);
        session.Status = AuthSessionStatus.Revoked;
        session.RevokedAtUtc = DateTime.UtcNow;
        await _context.Db.SaveChangesAsync();
        _context.Db.ChangeTracker.Clear();
        await using (var services = BuildServices(TimeProvider.System, adapter))
        await using (var serviceScope = services.CreateAsyncScope())
        {
            Func<Task> forbidden = () => serviceScope.ServiceProvider
                .GetRequiredService<ListingWorkspaceService>()
                .ConfirmSignalAsync(scope, unitId, 9106, true, "legacy-confirm");
            await forbidden.Should().ThrowAsync<UnauthorizedAccessException>();
        }
    }

    private static AtomicCommandReceipt CompletedReceipt(
        Guid attemptId, string operation, string key, string contract, string resultJson,
        string? fingerprint = null) => new()
    {
        Id = Guid.NewGuid(), AttemptId = attemptId, CommandType = operation,
        IdempotencyKey = key, RequestFingerprint = fingerprint ?? new string('0', 64),
        Status = AtomicCommandReceiptStatus.Completed, ResultContract = contract,
        ResultJson = resultJson, StartedAt = DateTime.UtcNow, CompletedAt = DateTime.UtcNow,
    };

    private async Task<(WorkspaceReadScope Scope, int UnitId)> SeedScopeAsync(
        DateTime now, int accessContextId = 0, int propertyId = 0, int unitId = 0,
        Guid? sessionId = null)
    {
        var user = await _context.Db.Users.SingleAsync(row => row.Id == 1);
        var accessContext = new WorkspaceAccessContext
        {
            Id = accessContextId,
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
            Id = sessionId ?? Guid.NewGuid(),
            UserId = user.Id,
            ActiveAccessContext = accessContext,
            Status = AuthSessionStatus.Active,
            CreatedAtUtc = now,
            LastSeenAtUtc = now,
            ExpiresAtUtc = now.AddDays(30),
        };
        var property = new Property
        {
            Id = propertyId,
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
            Id = unitId,
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

    private ServiceProvider BuildServices(
        TimeProvider timeProvider, IListingChannelAdapter? adapter = null)
    {
        var files = new Mock<IFileStorage>();
        files.Setup(storage => storage.UploadAtAsync(
                It.IsAny<Stream>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        var services = new ServiceCollection();
        services.AddSingleton(timeProvider);
        services.AddSingleton(files.Object);
        services.AddSingleton<IListingChannelAdapter>(
            adapter ?? new DisabledZillowListingChannelAdapter());
        services.AddSingleton<ILogger<ListingWorkspaceService>>(
            NullLogger<ListingWorkspaceService>.Instance);
        services.AddScoped<ICurrentActor, TestActor>();
        services.AddAtomicPersistenceKernel();
        services.AddPendingFileUploadStore();
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

    private sealed class RecordingListingAdapter : IListingChannelAdapter
    {
        public string ProviderKey => ListingProviderKeys.Zillow;
        public ListingChannelAvailability Availability => new(true, "Test", null);
        public Func<bool> HasLocalTransaction { get; set; } = static () => false;
        public bool SawLocalTransaction { get; private set; }
        public int PrepareCalls { get; private set; }
        public int PublishCalls { get; private set; }
        public int UpdateCalls { get; private set; }
        public int UnpublishCalls { get; private set; }

        public Task<ListingPreparedPackage> PrepareAsync(
            PrepareListingPublicationCommand command, CancellationToken ct)
        {
            PrepareCalls++;
            SawLocalTransaction |= HasLocalTransaction();
            return Task.FromResult(new ListingPreparedPackage("prepared-package", command.Package.ContentVersion));
        }

        public Task<ListingPublicationDelivery> PublishAsync(PublishListingCommand command, CancellationToken ct)
        {
            PublishCalls++;
            SawLocalTransaction |= HasLocalTransaction();
            return Task.FromResult(Delivered("publish-delivery"));
        }

        public Task<ListingPublicationDelivery> UpdateAsync(UpdateListingCommand command, CancellationToken ct)
        {
            UpdateCalls++;
            SawLocalTransaction |= HasLocalTransaction();
            return Task.FromResult(Delivered("update-delivery"));
        }

        public Task<ListingPublicationDelivery> UnpublishAsync(UnpublishListingCommand command, CancellationToken ct)
        {
            UnpublishCalls++;
            SawLocalTransaction |= HasLocalTransaction();
            return Task.FromResult(new ListingPublicationDelivery(
                "unpublish-delivery", "Removed", "external-1", null, null));
        }

        public Task<ListingReconciliationResult> ReconcileAsync(
            ReconcileListingCommand command, CancellationToken ct) => throw new NotSupportedException();

        public Task<ListingLeadReceipt> IngestLeadAsync(
            IngestListingLeadCommand command, CancellationToken ct) => throw new NotSupportedException();

        private static ListingPublicationDelivery Delivered(string key) => new(
            key, "Published", "external-1", "https://provider.test/listing/external-1", null);
    }

    private sealed class RecordingRuleExecutor(DateTime now) : IWriteExecutor
    {
        public List<string> Acquired { get; } = [];

        public void Clear() => Acquired.Clear();

        public async Task<AtomicCommandOutcome<TResult>> ExecuteAsync<TCommand, TResult>(
            string idempotencyKey, TransactionalWrite<TCommand, TResult> write,
            CancellationToken ct = default)
            where TCommand : notnull, IAtomicCommandData where TResult : notnull
        {
            var context = new Mock<IAtomicCommandContext>();
            context.Setup(value => value.ReadDatabaseClockUtcAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(now);
            context.Setup(value => value.AcquireLockAsync(
                    It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
                .Callback<string, int, CancellationToken>((name, id, _) =>
                    Acquired.Add($"{name}:{id}"))
                .Returns(Task.CompletedTask);
            context.Setup(value => value.AcquireLockAsync(
                    It.IsAny<string>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
                .Callback<string, Guid, CancellationToken>((name, id, _) =>
                    Acquired.Add($"{name}:{id}"))
                .Returns(Task.CompletedTask);
            context.Setup(value => value.FlushBusinessAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(new AtomicBusinessFlush(0, []));
            foreach (var writeLock in write.LockPlan.Locks)
                await writeLock.AcquireAsync(context.Object, ct);
            var result = await write.ExecuteAsync(write.Request, context.Object, ct);
            return new(result, AtomicCommandDisposition.Executed, Guid.NewGuid());
        }
    }

    private sealed class TestActor : ICurrentActor
    {
        public int? UserId => null;
        public string? ActorLabel => "integration:listing-metadata-crud";
        public string? IpAddress => "127.0.0.1";
    }
}
