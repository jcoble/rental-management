using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using System.Security.Cryptography;
using System.Text;
using RentalCommand.Api.DTOs;
using RentalCommand.Api.Services.Domain;
using RentalCommand.Api.Services.Screening;
using RentalCommand.Api.Writes;
using RentalCommand.Core;
using RentalCommand.Core.Atomic;
using RentalCommand.Core.Authorization;
using RentalCommand.Core.Documents;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Interfaces;
using RentalCommand.Core.Screening;
using RentalCommand.Data;
using RentalCommand.Data.Documents;
using RentalCommand.Data.Screening;
using RentalCommand.TestCommon;

namespace RentalCommand.Api.Tests.Domain;

[Collection(MigratedPostgreSqlCollection.Name)]
public sealed class AdverseActionFinalizationPostgreSqlTests : IAsyncLifetime
{
    private const int PortfolioId = 1;
    private const int LegacyApplicationId = 1;
    private const int LegacyActorUserId = 2;
    private static readonly DateTime LegacyNow = new(2026, 8, 21, 12, 0, 0, DateTimeKind.Utc);

    // Frozen legacy fingerprints computed from the screening command DTO shapes at dee7b39b.
    // These values must never be regenerated from the current command model or a current helper.
    private const string TrackFingerprint = "3f05a9c853d39221a262b79e301ba7db8999a66b79a372f215c3166f16dfb01e";
    private const string PrepareIntegratedFingerprint = "75e9bea9cd8315c9575fc08a63b108cc95d36cb4035a271b07ef56f1e73aece8";
    private const string FinalizeIntegratedFingerprint = "c705993a8c59fcf3e8ce3b22039a10054feb7925fed5e5a842af2bc4c68a5338";
    private const string UpdateExternalFingerprint = "6868a0e2b89358a41ae459b66f1b7b72da3f44ba11d21da180208c645736fd29";
    private const string DecisionFingerprint = "53a1ed9a244f394d144737d0001fa386b7565c9b189275f7239b39f69d1650f3";
    private const string ProviderDeliveryFingerprint = "376ec0e3f4200fa1a968d36bff535835a38cfd76bf681ceaee0ad8b9d89edff0";
    private const string AdversePrepareFingerprint = "2b4aabf0f9a2f16a4c4546a2cb4748a51bec9bccbd8e8c5f030dd10b5d95a1ba";
    private const string AdverseFinalizeFingerprint = "27449a9c672a5fa66d0e5cb5d0fd11457219d4fcf1121d6468ae325d90c33b66";
    private readonly MigratedPostgreSqlFixture _fixture;
    private MigratedPostgreSqlTestContext _ctx = null!;
    private RentalCommandDbContext _db = null!;
    private ServiceProvider _services = null!;
    private WorkspaceReadScope _scope;
    private readonly RedTestStorage _storage = new();

    public AdverseActionFinalizationPostgreSqlTests(MigratedPostgreSqlFixture fixture)
    {
        _fixture = fixture;
        QuestPDF.Settings.License = QuestPDF.Infrastructure.LicenseType.Community;
    }

    public async Task InitializeAsync()
    {
        _ctx = await _fixture.CreateContextAsync();
        _db = _ctx.Db;
        _scope = _db.SeedAdministratorScope(PortfolioId, nameof(AdverseActionFinalizationPostgreSqlTests));
        _services = AtomicDomainTestKernel.CreateForScreeningPostgreSql(_ctx.ConnectionString);
    }

    public async Task DisposeAsync()
    {
        await _services.DisposeAsync();
        await _ctx.DisposeAsync();
    }

    [Fact]
    public async Task PreparedDecline_FinalizationRejectsLaterApprovalWithoutPersistingNotice()
    {
        var seeded = await SeedDeclinedCaseAsync("prepared-decline");
        var now = seeded.Now;
        var application = seeded.Application;
        var screening = seeded.Screening;

        var writes = _services.GetRequiredService<IRequestWriteExecutor>();
        var prepareCommand = new PrepareAdverseActionNoticeCommand(
                PortfolioId,
                application.Id,
                _scope.UserId,
                _scope.SessionId,
                _scope.AccessContextId,
                _scope.AccessRevision,
                "prepared-decline",
                null,
                true);
        var prepareHandler = new PrepareAdverseActionNoticeHandler(
            _services.GetRequiredService<RentalCommandDbContext>());
        var prepared = await writes.ExecuteAsync("prepared-decline",
            ScreeningWriteSupport.Write(prepareCommand, prepareHandler.ExecuteAsync,
                prepareHandler.AuthorizeAsync));

        prepared.Value.Outcome.Should().Be(ScreeningMutationOutcome.Applied);
        prepared.Value.ScreeningId.Should().Be(screening.Id);
        prepared.Value.DecisionRecordedAtUtc.Should().NotBeNull();
        prepared.Value.DecisionFingerprint.Should().NotBeNullOrWhiteSpace();
        var purpose = "adverse-action-pdf";
        var requestFingerprint = "adverse-action-test-fingerprint";
        var pending = await _services.GetRequiredService<IPendingFileUploadStore>().PrepareAsync(
            PortfolioId,
            _scope.UserId,
            purpose,
            "prepared-decline-finalize",
            requestFingerprint,
            prepared.Value.FileName!,
            "application/pdf",
            16,
            now);
        var finalization = new CreateAdverseActionNoticeCommand(
            PortfolioId,
            application.Id,
            prepared.Value.ScreeningId,
            prepared.Value.DecisionRecordedAtUtc!.Value,
            prepared.Value.DecisionFingerprint!,
            _scope.UserId,
            _scope.SessionId,
            _scope.AccessContextId,
            _scope.AccessRevision,
            prepared.Value.Reason!,
            prepared.Value.CreditReportingAgencyBlock!,
            pending.Id,
            purpose,
            PendingFileUploadStore.ComputeOperationKeyHash("prepared-decline-finalize"),
            requestFingerprint,
            pending.StoragePath,
            prepared.Value.FileName!,
            "application/pdf",
            16,
            true,
            "prepared-decline-finalize",
            prepared.Value.GeneratedAtUtc);

        // The external blob exists before the receipt-backed finalizer runs. A stale command must
        // reject before creating a StoredFile, leaving the pending row responsible for cleanup.
        await _storage.UploadAtAsync(new MemoryStream(new byte[16]), pending.StoragePath,
            prepared.Value.FileName!, "application/pdf");

        application.Status = ApplicationStatus.Approved;
        application.DecisionReason = null;
        screening.Decision = ScreeningDecision.Accept;
        screening.DecisionReason = null;
        screening.ConsumerReportUsedForDecision = false;
        screening.DecisionRecordedAtUtc = now;
        screening.UpdatedAt = now;
        await _db.SaveChangesAsync();

        var finalizeHandler = new CreateAdverseActionNoticeHandler(
            _services.GetRequiredService<RentalCommandDbContext>());
        var act = () => writes.ExecuteAsync("prepared-decline",
            ScreeningWriteSupport.Write(finalization, finalizeHandler.ExecuteAsync,
                finalizeHandler.AuthorizeAsync));

        var error = await act.Should().ThrowAsync<InvalidOperationException>();
        error.Which.Message.Should().Contain("decision");
        (await _db.Set<AdverseActionNotice>().CountAsync(notice => notice.ApplicationId == application.Id))
            .Should().Be(0);
        (await _db.StoredFiles.CountAsync(file => file.EntityType == "Application" && file.EntityId == application.Id))
            .Should().Be(0);
        (await _db.Set<PendingFileUpload>().AsNoTracking()
            .SingleAsync(upload => upload.Id == pending.Id))
            .State.Should().Be(PendingFileUploadState.Prepared);
        _storage.Contains(pending.StoragePath).Should().BeTrue();

        var pendingStore = _services.GetRequiredService<IPendingFileUploadStore>();
        var claims = await pendingStore.ClaimExpiredAsync(
            "tsk726-orphan-cleanup",
            TimeSpan.Zero,
            TimeSpan.FromMinutes(5),
            10);
        var claim = claims.Should().ContainSingle().Subject;
        claim.Id.Should().Be(pending.Id);
        await _storage.DeleteAsync(claim.StoragePath);
        (await pendingStore.MarkAbandonedAsync(claim.Id, claim.ClaimOwner, claim.ClaimToken))
            .Should().Be(1);
        (await _db.Set<PendingFileUpload>().AsNoTracking()
            .SingleAsync(upload => upload.Id == pending.Id))
            .State.Should().Be(PendingFileUploadState.Abandoned);
        _storage.Contains(pending.StoragePath).Should().BeFalse();
    }

    [Fact]
    public async Task FinalizationRetry_ReplaysReceiptWithoutDuplicateNoticeOrFile()
    {
        var seeded = await SeedDeclinedCaseAsync("retry");
        var application = seeded.Application;
        var writes = _services.GetRequiredService<IRequestWriteExecutor>();
        var prepareCommand = new PrepareAdverseActionNoticeCommand(
                PortfolioId,
                application.Id,
                _scope.UserId,
                _scope.SessionId,
                _scope.AccessContextId,
                _scope.AccessRevision,
                "retry-prepared",
                null,
                true);
        var prepareHandler = new PrepareAdverseActionNoticeHandler(
            _services.GetRequiredService<RentalCommandDbContext>());
        var prepared = await writes.ExecuteAsync("retry-prepared",
            ScreeningWriteSupport.Write(prepareCommand, prepareHandler.ExecuteAsync,
                prepareHandler.AuthorizeAsync));

        const string operationKey = "retry-finalize";
        const string purpose = "adverse-action-pdf";
        const string requestFingerprint = "adverse-action-retry-fingerprint";
        var pending = await _services.GetRequiredService<IPendingFileUploadStore>().PrepareAsync(
            PortfolioId,
            _scope.UserId,
            purpose,
            operationKey,
            requestFingerprint,
            prepared.Value.FileName!,
            "application/pdf",
            16,
            seeded.Now);
        await _storage.UploadAtAsync(new MemoryStream(new byte[16]), pending.StoragePath,
            prepared.Value.FileName!, "application/pdf");

        var command = new CreateAdverseActionNoticeCommand(
            PortfolioId,
            application.Id,
            prepared.Value.ScreeningId,
            prepared.Value.DecisionRecordedAtUtc!.Value,
            prepared.Value.DecisionFingerprint!,
            _scope.UserId,
            _scope.SessionId,
            _scope.AccessContextId,
            _scope.AccessRevision,
            prepared.Value.Reason!,
            prepared.Value.CreditReportingAgencyBlock!,
            pending.Id,
            purpose,
            PendingFileUploadStore.ComputeOperationKeyHash(operationKey),
            requestFingerprint,
            pending.StoragePath,
            prepared.Value.FileName!,
            "application/pdf",
            16,
            true,
            "adverse-action-retry-delivery",
            prepared.Value.GeneratedAtUtc);
        var handler = new CreateAdverseActionNoticeHandler(
            _services.GetRequiredService<RentalCommandDbContext>());
        var write = ScreeningWriteSupport.Write(command, handler.ExecuteAsync, handler.AuthorizeAsync);
        var first = await writes.ExecuteAsync("retry-finalize", write);
        var second = await writes.ExecuteAsync("retry-finalize", write);

        second.Value.Should().BeEquivalentTo(first.Value);
        (await _db.Set<AdverseActionNotice>().CountAsync(notice => notice.ApplicationId == application.Id))
            .Should().Be(1);
        var storedFile = await _db.StoredFiles.AsNoTracking()
            .SingleAsync(file => file.EntityType == "Application" && file.EntityId == application.Id);
        storedFile.Id.Should().Be(first.Value.StoredFileId);
        var finalized = await _db.Set<PendingFileUpload>().AsNoTracking()
            .SingleAsync(upload => upload.Id == pending.Id);
        finalized.State.Should().Be(PendingFileUploadState.Finalized);
        finalized.StoredFileId.Should().Be(storedFile.Id);
    }

    [Fact]
    public async Task ScreeningTransitionAndProviderDelivery_ExactRetriesDoNotDuplicateRows()
    {
        var application = await SeedReplayApplicationAsync("screening-retry");
        var db = _services.GetRequiredService<RentalCommandDbContext>();
        var service = new ScreeningService(
            db,
            new DisabledScreeningProvider(),
            _storage,
            new DeterministicAdverseActionPdfGenerator(),
            _services.GetRequiredService<IRequestWriteExecutor>(),
            _services.GetRequiredService<IPendingFileUploadStore>(),
            TimeProvider.System);
        var trackRequest = new TrackExternalScreeningRequest
        {
            OperationKey = "screening-transition-retry",
            ProviderDisplayName = "External provider",
            ProviderReference = "external-retry-42",
            Status = ApplicantScreeningStatus.Completed,
        };

        var first = await service.TrackExternalAsync(_scope, application.Id, trackRequest);
        var retry = await service.TrackExternalAsync(_scope, application.Id, trackRequest);

        retry!.Id.Should().Be(first!.Id);
        (await _db.ApplicantScreenings.AsNoTracking()
            .CountAsync(row => row.ApplicationId == application.Id)).Should().Be(1);

        var integrated = new ApplicantScreening
        {
            PortfolioId = PortfolioId,
            ApplicationId = application.Id,
            Mode = ScreeningMode.Integrated,
            Status = ApplicantScreeningStatus.AwaitingApplicant,
            ProviderKey = "provider-retry",
            ProviderDisplayName = "Provider retry",
            ProviderReference = "provider-reference-retry",
            OperationKey = "provider-operation-retry",
            ConsentConfirmed = true,
            ConsentAtUtc = application.ConsentAtUtc,
            InvitedAtUtc = DateTime.UtcNow.AddMinutes(-2),
            LastStatusAtUtc = DateTime.UtcNow.AddMinutes(-2),
            CreatedByUserId = _scope.UserId,
            CreatedAt = DateTime.UtcNow.AddMinutes(-2),
            UpdatedAt = DateTime.UtcNow.AddMinutes(-2),
        };
        _db.Add(integrated);
        await _db.SaveChangesAsync();
        var delivery = new ScreeningProviderStatusDelivery(
            "provider-retry", "delivery-retry-42", "provider-reference-retry",
            "screening.completed", ApplicantScreeningStatus.Completed, DateTime.UtcNow,
            null, "Example CRA", "1 Main Street", "555-0100");

        var delivered = await service.ApplyProviderDeliveryAsync(delivery);
        var deliveryRetry = await service.ApplyProviderDeliveryAsync(delivery);

        deliveryRetry!.Id.Should().Be(delivered!.Id);
        (await _db.ApplicantScreeningMilestones.AsNoTracking().CountAsync(row =>
            row.ApplicantScreeningId == integrated.Id && row.DeliveryId == delivery.DeliveryId))
            .Should().Be(1);
    }

    [Fact]
    public async Task EightLegacyReceiptContracts_ReplayThroughMigratedExecutorAndAuthorize()
    {
        var application = await SeedReplayApplicationAsync("legacy-replay");
        var db = _services.GetRequiredService<RentalCommandDbContext>();
        var writes = _services.GetRequiredService<IRequestWriteExecutor>();
        application.Id.Should().Be(LegacyApplicationId);
        _scope.UserId.Should().Be(LegacyActorUserId);

        var notFound = new ScreeningMutationResult(ScreeningMutationOutcome.NotFound);

        var trackCommand = new TrackExternalScreeningCommand(
            PortfolioId, LegacyApplicationId, LegacyActorUserId, _scope.SessionId,
            _scope.AccessContextId, _scope.AccessRevision, "legacy-track", "Legacy provider",
            null, null, null, null, null, ApplicantScreeningStatus.Created, "legacy-track-delivery");
        var trackHandler = new TrackExternalScreeningHandler(db);
        await ReplayLegacyReceiptAsync(db, writes, "screening.external.create",
            LegacyApplicationKey("legacy-track"), trackCommand, TrackFingerprint,
            "{\"Outcome\":1,\"Screening\":null}", ScreeningWriteSupport.MutationResultContract,
            notFound, trackHandler.AuthorizeAsync);

        var integratedCommand = new PrepareIntegratedScreeningCommand(
            PortfolioId, LegacyApplicationId, LegacyActorUserId, _scope.SessionId,
            _scope.AccessContextId, _scope.AccessRevision, "legacy-integrated", "legacy-provider",
            "Legacy provider", "legacy-integrated-delivery");
        var integratedResult = new PrepareIntegratedScreeningResult(
            ScreeningMutationOutcome.NotFound, null, LegacyApplicationId, "legacy-integrated", null, null, null);
        var integratedHandler = new PrepareIntegratedScreeningHandler(db);
        await ReplayLegacyReceiptAsync(db, writes, "screening.integrated.prepare",
            LegacyApplicationKey("legacy-integrated"), integratedCommand, PrepareIntegratedFingerprint,
            "{\"Outcome\":1,\"Screening\":null,\"ApplicationId\":1,\"OperationKey\":\"legacy-integrated\",\"ApplicantName\":null,\"ApplicantEmail\":null,\"ConsentAtUtc\":null}",
            ScreeningWriteSupport.IntegratedPrepareResultContract, integratedResult,
            integratedHandler.AuthorizeAsync);

        var finalizeIntegratedCommand = new FinalizeIntegratedScreeningCommand(
            PortfolioId, LegacyApplicationId, 71, LegacyActorUserId, _scope.SessionId,
            _scope.AccessContextId, _scope.AccessRevision, "legacy-integrated-finalize", "legacy-provider",
            true, "legacy-reference", "https://legacy.example.test/report", LegacyNow, null,
            "Legacy CRA", "1 Legacy Way", "555-0100", "legacy-integrated-finalize-delivery");
        var finalizeIntegratedHandler = new FinalizeIntegratedScreeningHandler(db);
        await ReplayLegacyReceiptAsync(db, writes, "screening.integrated.finalize",
            LegacyApplicationKey("legacy-integrated-finalize"), finalizeIntegratedCommand,
            FinalizeIntegratedFingerprint, "{\"Outcome\":1,\"Screening\":null}",
            ScreeningWriteSupport.MutationResultContract, notFound,
            finalizeIntegratedHandler.AuthorizeAsync);

        var updateCommand = new UpdateExternalScreeningCommand(
            PortfolioId, LegacyApplicationId, 72, LegacyActorUserId, _scope.SessionId,
            _scope.AccessContextId, _scope.AccessRevision, "legacy-external-update",
            ApplicantScreeningStatus.Completed, "legacy-updated-reference", null,
            "Legacy CRA", "1 Legacy Way", "555-0100", LegacyNow, "legacy-external-update-delivery");
        var updateHandler = new UpdateExternalScreeningHandler(db);
        await ReplayLegacyReceiptAsync(db, writes, "screening.external.update",
            LegacyApplicationKey("legacy-external-update"), updateCommand, UpdateExternalFingerprint,
            "{\"Outcome\":1,\"Screening\":null}", ScreeningWriteSupport.MutationResultContract,
            notFound, updateHandler.AuthorizeAsync);

        var decisionCommand = new RecordScreeningDecisionCommand(
            PortfolioId, LegacyApplicationId, 73, LegacyActorUserId, _scope.SessionId,
            _scope.AccessContextId, _scope.AccessRevision, "legacy-decision", ScreeningDecision.Decline,
            "Legacy decline", true, "legacy-decision-delivery");
        var decisionHandler = new RecordScreeningDecisionHandler(db);
        await ReplayLegacyReceiptAsync(db, writes, "screening.decision",
            LegacyApplicationKey("legacy-decision"), decisionCommand, DecisionFingerprint,
            "{\"Outcome\":1,\"Screening\":null}", ScreeningWriteSupport.MutationResultContract,
            notFound, decisionHandler.AuthorizeAsync);

        var providerCommand = new ApplyScreeningProviderDeliveryCommand(
            "legacy-provider", "legacy-delivery", "legacy-provider-reference", "screening.completed",
            ApplicantScreeningStatus.Completed, LegacyNow, null, "Legacy CRA", "1 Legacy Way", "555-0100",
            "legacy-provider-delivery");
        var providerHandler = new ApplyScreeningProviderDeliveryHandler(db);
        await ReplayLegacyReceiptAsync(db, writes, "screening.provider-delivery",
            LegacyProviderDeliveryKey("legacy-provider", "legacy-delivery"), providerCommand,
            ProviderDeliveryFingerprint, "{\"Outcome\":1,\"Screening\":null}",
            ScreeningWriteSupport.MutationResultContract, notFound, providerHandler.AuthorizeAsync);

        var adversePrepareCommand = new PrepareAdverseActionNoticeCommand(
            PortfolioId, LegacyApplicationId, LegacyActorUserId, _scope.SessionId,
            _scope.AccessContextId, _scope.AccessRevision, "legacy-adverse-prepare", null, false);
        var adversePrepareResult = new PrepareAdverseActionNoticeResult(
            Outcome: ScreeningMutationOutcome.NotFound,
            ApplicationId: LegacyApplicationId,
            ScreeningId: 0,
            ManagementCompanyName: null,
            PortfolioName: null,
            ApplicantName: null,
            PropertyName: null,
            PropertyAddressLine1: null,
            PropertyCity: null,
            PropertyState: null,
            PropertyPostalCode: null,
            Reason: null,
            CreditReportingAgencyName: null,
            CreditReportingAgencyAddress: null,
            CreditReportingAgencyPhone: null,
            CreditReportingAgencyBlock: null,
            FileName: null,
            DecisionRecordedAtUtc: null,
            DecisionFingerprint: null,
            SendToApplicant: false,
            GeneratedAtUtc: LegacyNow);
        var adversePrepareHandler = new PrepareAdverseActionNoticeHandler(db);
        await ReplayLegacyReceiptAsync(db, writes, "adverse-action.prepare",
            LegacyApplicationKey("legacy-adverse-prepare"), adversePrepareCommand,
            AdversePrepareFingerprint,
            "{\"Outcome\":1,\"ApplicationId\":1,\"ScreeningId\":0,\"ManagementCompanyName\":null,\"PortfolioName\":null,\"ApplicantName\":null,\"PropertyName\":null,\"PropertyAddressLine1\":null,\"PropertyCity\":null,\"PropertyState\":null,\"PropertyPostalCode\":null,\"Reason\":null,\"CreditReportingAgencyName\":null,\"CreditReportingAgencyAddress\":null,\"CreditReportingAgencyPhone\":null,\"CreditReportingAgencyBlock\":null,\"FileName\":null,\"DecisionRecordedAtUtc\":null,\"DecisionFingerprint\":null,\"SendToApplicant\":false,\"GeneratedAtUtc\":\"2026-08-21T12:00:00Z\"}",
            ScreeningWriteSupport.AdversePrepareResultContract, adversePrepareResult,
            adversePrepareHandler.AuthorizeAsync);

        var adverseFinalizeCommand = new CreateAdverseActionNoticeCommand(
            PortfolioId, LegacyApplicationId, 74, LegacyNow, "legacy-decision-fingerprint",
            LegacyActorUserId, _scope.SessionId, _scope.AccessContextId, _scope.AccessRevision, "Legacy reason",
            "Legacy CRA", Guid.Parse("22222222-2222-2222-2222-222222222222"),
            "adverse-action-pdf", "legacy-operation-hash",
            "legacy-request-fingerprint", "legacy/path.pdf", "legacy.pdf", "application/pdf",
            8, false, "legacy-adverse-finalize-delivery", LegacyNow);
        var adverseFinalizeResult = new CreateAdverseActionNoticeResult(
            42, LegacyApplicationId, "Legacy reason", "Legacy CRA", LegacyNow, 43, null);
        var adverseFinalizeHandler = new CreateAdverseActionNoticeHandler(db);
        await ReplayLegacyReceiptAsync(db, writes, "adverse-action.finalize",
            LegacyApplicationKey("legacy-adverse-finalize"), adverseFinalizeCommand,
            AdverseFinalizeFingerprint,
            "{\"NoticeId\":42,\"ApplicationId\":1,\"Reason\":\"Legacy reason\",\"CreditReportingAgency\":\"Legacy CRA\",\"GeneratedAtUtc\":\"2026-08-21T12:00:00Z\",\"StoredFileId\":43,\"SentAtUtc\":null}",
            ScreeningWriteSupport.AdverseFinalizeResultContract, adverseFinalizeResult,
            adverseFinalizeHandler.AuthorizeAsync);

        (await _db.ApplicantScreenings.AsNoTracking()
            .CountAsync(row => row.ApplicationId == application.Id)).Should().Be(0);
        (await _db.AdverseActionNotices.AsNoTracking()
            .CountAsync(row => row.ApplicationId == application.Id)).Should().Be(0);

        await db.AuthSessions.Where(session => session.Id == _scope.SessionId)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(session => session.Status, AuthSessionStatus.Revoked)
                .SetProperty(session => session.RevokedAtUtc, LegacyNow));
        var deniedReplay = () => writes.ExecuteAsync(
            LegacyApplicationKey("legacy-track"),
            ScreeningWriteSupport.Write<TrackExternalScreeningCommand, ScreeningMutationResult>(
                trackCommand,
                (_, _, _) => throw new InvalidOperationException("A stored receipt must not execute."),
                trackHandler.AuthorizeAsync));

        await deniedReplay.Should().ThrowAsync<UnauthorizedAccessException>();
    }

    [Fact]
    public async Task GenerateAdverseAction_RetryAfterCleanupReturnsConflictWithoutArtifacts()
    {
        var seeded = await SeedDeclinedCaseAsync("abandoned-retry");
        var service = new ScreeningService(
            _services.GetRequiredService<RentalCommandDbContext>(),
            new DisabledScreeningProvider(),
            _storage,
            new DeterministicAdverseActionPdfGenerator(),
            _services.GetRequiredService<IRequestWriteExecutor>(),
            _services.GetRequiredService<IPendingFileUploadStore>(),
            TimeProvider.System);
        _storage.BeforeUploadAsync = async _ =>
        {
            await using var cleanupScope = _services.CreateAsyncScope();
            var cleanupStore = cleanupScope.ServiceProvider.GetRequiredService<IPendingFileUploadStore>();
            var claims = await cleanupStore.ClaimExpiredAsync(
                "adverse-action-abandoned-cleanup",
                TimeSpan.Zero,
                TimeSpan.FromMinutes(5),
                10);
            _storage.Claims.AddRange(claims);
            foreach (var claim in claims)
                await cleanupStore.MarkAbandonedAsync(claim.Id, claim.ClaimOwner, claim.ClaimToken);
        };
        var request = new GenerateAdverseActionRequest
        {
            OperationKey = "adverse-action-abandoned-retry",
            SendToApplicant = true,
        };
        var emailBefore = await _db.OutboxMessages.AsNoTracking()
            .CountAsync(message => message.MessageType == "email");
        var dataUpdatesBefore = await _db.OutboxMessages.AsNoTracking()
            .CountAsync(message => message.MessageType == "data-update"
                && message.IdempotencyKey.Contains("adverse-action"));

        var firstError = await Record.ExceptionAsync(() => service.GenerateAdverseActionAsync(
            _scope, seeded.Application.Id, request));
        var secondError = await Record.ExceptionAsync(() => service.GenerateAdverseActionAsync(
            _scope, seeded.Application.Id, request));

        firstError.Should().BeOfType<DomainValidationException>(firstError?.ToString());
        secondError.Should().BeOfType<DomainValidationException>(secondError?.ToString());
        firstError!.Message.Should().Contain("retry with a new request key");
        secondError!.Message.Should().Contain("retry with a new request key");
        _storage.UploadCount.Should().Be(1);
        (await _db.Set<AdverseActionNotice>().AsNoTracking()
            .CountAsync(notice => notice.ApplicationId == seeded.Application.Id))
            .Should().Be(0);
        (await _db.StoredFiles.AsNoTracking()
            .CountAsync(file => file.EntityType == "Application" && file.EntityId == seeded.Application.Id))
            .Should().Be(0);
        (await _db.OutboxMessages.AsNoTracking()
            .CountAsync(message => message.MessageType == "email"))
            .Should().Be(emailBefore);
        (await _db.OutboxMessages.AsNoTracking()
            .CountAsync(message => message.MessageType == "data-update"
                && message.IdempotencyKey.Contains("adverse-action")))
            .Should().Be(dataUpdatesBefore);
        (await _db.PendingFileUploads.AsNoTracking()
            .SingleAsync(upload => upload.Purpose == "adverse-action-pdf"))
            .State.Should().Be(PendingFileUploadState.Abandoned);
    }

    private async Task<(RentalApplication Application, ApplicantScreening Screening, DateTime Now)>
        SeedDeclinedCaseAsync(string key)
    {
        var now = DateTime.UtcNow;
        var application = new RentalApplication
        {
            PortfolioId = PortfolioId,
            FirstName = "Prepared",
            LastName = key,
            Email = $"{key}@example.test",
            ConsentGiven = true,
            ConsentAtUtc = now.AddHours(-1),
            Status = ApplicationStatus.Declined,
            DecisionReason = "Income did not meet the stated requirement.",
            SubmittedAtUtc = now.AddHours(-2),
            ReviewedAtUtc = now.AddMinutes(-30),
            CreatedAt = now.AddHours(-2),
            UpdatedAt = now.AddMinutes(-30),
        };
        var screening = new ApplicantScreening
        {
            PortfolioId = PortfolioId,
            Application = application,
            Mode = ScreeningMode.External,
            Status = ApplicantScreeningStatus.Completed,
            ProviderDisplayName = "Manual checker",
            OperationKey = $"screening-{key}",
            ConsentConfirmed = true,
            ConsentAtUtc = application.ConsentAtUtc,
            CompletedAtUtc = now.AddHours(-1),
            LastStatusAtUtc = now.AddHours(-1),
            Decision = ScreeningDecision.Decline,
            DecisionReason = "Income did not meet the stated requirement.",
            DecisionRecordedByUserId = _scope.UserId,
            DecisionRecordedAtUtc = now.AddMinutes(-30),
            ConsumerReportUsedForDecision = true,
            CreditReportingAgencyName = "Example Reporting",
            CreditReportingAgencyAddress = "1 Main Street, Columbus, OH 43215",
            CreditReportingAgencyPhone = "555-0100",
            CreatedByUserId = _scope.UserId,
            CreatedAt = now.AddHours(-1),
            UpdatedAt = now.AddMinutes(-30),
        };
        _db.Add(screening);
        await _db.SaveChangesAsync();
        return (application, screening, now);
    }

    private async Task<RentalApplication> SeedReplayApplicationAsync(string suffix)
    {
        var now = DateTime.UtcNow;
        var application = new RentalApplication
        {
            PortfolioId = PortfolioId,
            FirstName = "Replay",
            LastName = suffix,
            Email = $"{suffix}@example.test",
            ConsentGiven = true,
            ConsentAtUtc = now.AddMinutes(-5),
            Status = ApplicationStatus.Submitted,
            SubmittedAtUtc = now.AddMinutes(-5),
            CreatedAt = now.AddMinutes(-5),
            UpdatedAt = now.AddMinutes(-5),
        };
        _db.Add(application);
        await _db.SaveChangesAsync();
        return application;
    }

    private static async Task ReplayLegacyReceiptAsync<TCommand, TResult>(
        RentalCommandDbContext db,
        IRequestWriteExecutor writes,
        string operation,
        string key,
        TCommand command,
        string legacyFingerprint,
        string legacyResultJson,
        string contract,
        TResult expected,
        Func<TCommand, IAtomicCommandContext, CancellationToken, Task> authorizeAsync)
        where TCommand : notnull, IAtomicCommandData
        where TResult : notnull
    {
        var write = ScreeningWriteSupport.Write<TCommand, TResult>(
            command,
            (_, _, _) => throw new InvalidOperationException("A stored receipt must not execute."),
            authorizeAsync);
        write.OperationName.Should().Be(operation);
        write.ResultContract.Should().Be(contract);

        db.AtomicCommandReceipts.Add(new AtomicCommandReceipt
        {
            Id = Guid.NewGuid(),
            AttemptId = Guid.NewGuid(),
            CommandType = operation,
            IdempotencyKey = key,
            RequestFingerprint = legacyFingerprint,
            Status = AtomicCommandReceiptStatus.Completed,
            ResultContract = contract,
            ResultJson = legacyResultJson,
            StartedAt = LegacyNow,
            CompletedAt = LegacyNow,
        });
        await db.SaveChangesAsync();

        var replay = await writes.ExecuteAsync(key, write);
        replay.Disposition.Should().Be(AtomicCommandDisposition.Replayed);
        replay.Value.Should().Be(expected);
    }

    // Independently reproduces the caller formulas frozen at dee7b39b.
    private static string LegacyApplicationKey(string operationKey) =>
        $"{PortfolioId}:{LegacyApplicationId}:{LegacyDigest(operationKey)}";

    private static string LegacyProviderDeliveryKey(string providerKey, string deliveryId) =>
        LegacyDigest($"{providerKey}:{deliveryId}");

    private static string LegacyDigest(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private sealed class RedTestStorage : IFileStorage
    {
        private readonly HashSet<string> _paths = new(StringComparer.Ordinal);

        public bool Contains(string path) => _paths.Contains(path);
        public Func<string, Task>? BeforeUploadAsync { get; set; }
        public List<PendingFileUploadCleanupClaim> Claims { get; } = [];
        public int UploadCount { get; private set; }

        public Task<string> UploadAsync(Stream content, string fileName, string contentType, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public async Task UploadAtAsync(Stream content, string storagePath, string fileName, string contentType, CancellationToken ct = default)
        {
            if (BeforeUploadAsync is not null)
                await BeforeUploadAsync(storagePath);
            using var buffer = new MemoryStream();
            await content.CopyToAsync(buffer, ct);
            _paths.Add(storagePath);
            UploadCount++;
        }

        public Task<Stream> DownloadAsync(string path, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task DeleteAsync(string path, CancellationToken ct = default)
        {
            _paths.Remove(path);
            return Task.CompletedTask;
        }
    }

    private sealed class DeterministicAdverseActionPdfGenerator : IAdverseActionNoticePdfGenerator
    {
        public byte[] Generate(AdverseActionNoticeData data) =>
            Encoding.UTF8.GetBytes($"{data.ApplicantName}|{data.NoticeDate:O}|{data.Reason}");
    }
}
