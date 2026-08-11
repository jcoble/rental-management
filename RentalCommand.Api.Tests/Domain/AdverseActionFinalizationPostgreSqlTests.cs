using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using System.Text;
using RentalCommand.Api.DTOs;
using RentalCommand.Api.Services.Domain;
using RentalCommand.Api.Services.Screening;
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
using RentalCommand.TestCommon;

namespace RentalCommand.Api.Tests.Domain;

[Collection(MigratedPostgreSqlCollection.Name)]
public sealed class AdverseActionFinalizationPostgreSqlTests : IAsyncLifetime
{
    private const int PortfolioId = 1;
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

        var atomic = _services.GetRequiredService<IAtomicUnitOfWork>();
        var prepareCodec = new AtomicJsonResultCodec<PrepareAdverseActionNoticeResult>("adverse-action.prepare.red.v1");
        var prepared = await atomic.ExecuteAsync(
            new AtomicCommandIdentity("adverse-action.prepare.red", "prepared-decline"),
            new PrepareAdverseActionNoticeCommand(
                PortfolioId,
                application.Id,
                _scope.UserId,
                _scope.SessionId,
                _scope.AccessContextId,
                _scope.AccessRevision,
                "prepared-decline",
                null,
                true),
            prepareCodec);

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

        var act = () => atomic.ExecuteAsync(
            new AtomicCommandIdentity("adverse-action.finalize.red", "prepared-decline"),
            finalization,
            new AtomicJsonResultCodec<CreateAdverseActionNoticeResult>("adverse-action.finalize.red.v1"));

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
        var atomic = _services.GetRequiredService<IAtomicUnitOfWork>();
        var prepared = await atomic.ExecuteAsync(
            new AtomicCommandIdentity("adverse-action.prepare.retry", "retry-prepared"),
            new PrepareAdverseActionNoticeCommand(
                PortfolioId,
                application.Id,
                _scope.UserId,
                _scope.SessionId,
                _scope.AccessContextId,
                _scope.AccessRevision,
                "retry-prepared",
                null,
                true),
            new AtomicJsonResultCodec<PrepareAdverseActionNoticeResult>("adverse-action.prepare.retry.v1"));

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
        var identity = new AtomicCommandIdentity("adverse-action.finalize.retry", "retry-finalize");
        var codec = new AtomicJsonResultCodec<CreateAdverseActionNoticeResult>("adverse-action.finalize.retry.v1");

        var first = await atomic.ExecuteAsync(identity, command, codec);
        var second = await atomic.ExecuteAsync(identity, command, codec);

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
    public async Task GenerateAdverseAction_RetryAfterCleanupReturnsConflictWithoutArtifacts()
    {
        var seeded = await SeedDeclinedCaseAsync("abandoned-retry");
        var service = new ScreeningService(
            _db,
            new DisabledScreeningProvider(),
            _storage,
            new DeterministicAdverseActionPdfGenerator(),
            _services.GetRequiredService<IAtomicUnitOfWork>(),
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
