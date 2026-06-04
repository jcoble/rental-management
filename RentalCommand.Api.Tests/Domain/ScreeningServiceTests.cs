using System.Text;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using RentalCommand.Api.DTOs;
using RentalCommand.Api.Services.Domain;
using RentalCommand.Api.Services.Screening;
using RentalCommand.Core.Configuration;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Interfaces;
using RentalCommand.Data;

namespace RentalCommand.Api.Tests.Domain;

/// <summary>
/// Tests for <see cref="ScreeningService"/> (the FCRA screening flow) using SQLite in-memory. Covers the
/// FCRA consent gate (no consent → 400-equivalent), the gated provider (disabled → 503-equivalent with no
/// false "passed"), and the adverse-action notice generation (non-empty PDF + a stored notice row that is
/// readable).
/// </summary>
public class ScreeningServiceTests : IDisposable
{
    private const int PortfolioId = 1;

    private readonly SqliteConnection _conn;
    private readonly RentalCommandDbContext _db;
    private readonly RecordingPublisher _publisher = new();
    private readonly FakeFileStorage _storage = new();

    public ScreeningServiceTests()
    {
        QuestPDF.Settings.License = QuestPDF.Infrastructure.LicenseType.Community;

        _conn = new SqliteConnection("DataSource=:memory:");
        _conn.Open();

        var options = new DbContextOptionsBuilder<RentalCommandDbContext>()
            .UseSqlite(_conn)
            .Options;

        _db = new ApplicationTestDbContext(options);
        _db.Database.EnsureCreated();

        _db.Portfolios.Add(new Portfolio
        {
            Id = PortfolioId,
            Name = "Frank's Rentals",
            ManagementCompanyName = "Frank Property Management",
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

    private ScreeningService BuildService(IScreeningProvider provider) => new(
        _db,
        provider,
        Options.Create(new ScreeningConfig
        {
            ApiKey = provider.IsConfigured ? "test-key" : null,
            CreditReportingAgencyName = "TransUnion Consumer Solutions",
            CreditReportingAgencyAddress = "P.O. Box 2000, Chester, PA 19016-2000",
            CreditReportingAgencyPhone = "1-800-916-8800",
        }),
        _storage,
        new AdverseActionNoticePdfGenerator(),
        _publisher,
        Mock.Of<IDataUpdateService>(),
        new RecordingAuditService(),
        NullLogger<ScreeningService>.Instance);

    private async Task<RentalApplication> SeedApplicationAsync(bool consent, string? email = "applicant@example.com")
    {
        var now = DateTime.UtcNow;
        var app = new RentalApplication
        {
            PortfolioId = PortfolioId,
            FirstName = "Dana",
            LastName = "Lopez",
            Email = email,
            CurrentAddress = "1 Main St, Columbus, OH",
            ConsentGiven = consent,
            ConsentAtUtc = consent ? now : null,
            Status = ApplicationStatus.Submitted,
            SubmittedAtUtc = now,
            CreatedAt = now,
            UpdatedAt = now,
        };
        _db.RentalApplications.Add(app);
        await _db.SaveChangesAsync();
        return app;
    }

    [Fact]
    public async Task RequestScreening_WithoutConsent_ThrowsConsentRequired_AndScreensNothing()
    {
        var app = await SeedApplicationAsync(consent: false);
        var sut = BuildService(new StubConfiguredProvider());

        var act = async () => await sut.RequestScreeningAsync(PortfolioId, app.Id, userId: 7);

        await act.Should().ThrowAsync<ConsentRequiredException>();
        (await _db.ScreeningResults.CountAsync()).Should().Be(0, "no consent → no screening is ever run");

        var reloaded = await _db.RentalApplications.SingleAsync(a => a.Id == app.Id);
        reloaded.Status.Should().Be(ApplicationStatus.Submitted, "the application must not move to review without a screening");
    }

    [Fact]
    public async Task RequestScreening_ProviderDisabled_ThrowsNotConfigured_AndRecordsNoFalsePass()
    {
        var app = await SeedApplicationAsync(consent: true);
        var sut = BuildService(new DisabledScreeningProvider(NullLogger<DisabledScreeningProvider>.Instance));

        var act = async () => await sut.RequestScreeningAsync(PortfolioId, app.Id, userId: 7);

        var ex = await act.Should().ThrowAsync<ScreeningNotConfiguredException>();
        ex.Which.Message.Should().Be("screening not configured");

        // The gated provider must never produce a stored "passed" result.
        (await _db.ScreeningResults.CountAsync()).Should().Be(0);
        var reloaded = await _db.RentalApplications.SingleAsync(a => a.Id == app.Id);
        reloaded.Status.Should().Be(ApplicationStatus.Submitted);
    }

    [Fact]
    public async Task RequestScreening_Configured_CreatesResult_AndMovesToUnderReview()
    {
        var app = await SeedApplicationAsync(consent: true);
        var sut = BuildService(new StubConfiguredProvider());

        var result = await sut.RequestScreeningAsync(PortfolioId, app.Id, userId: 7);

        result.Should().NotBeNull();
        result!.Status.Should().Be("Completed");
        result.Recommendation.Should().Be("Decline");
        result.CreditScoreBand.Should().Be("Poor");

        // The result is stored and readable.
        var saved = await _db.ScreeningResults.SingleAsync();
        saved.ApplicationId.Should().Be(app.Id);
        saved.Status.Should().Be(ScreeningStatus.Completed);

        var read = await sut.GetScreeningResultsAsync(PortfolioId, app.Id);
        read.Should().NotBeNull();
        read!.Should().ContainSingle().Which.Id.Should().Be(saved.Id);

        var reloaded = await _db.RentalApplications.SingleAsync(a => a.Id == app.Id);
        reloaded.Status.Should().Be(ApplicationStatus.UnderReview);
    }

    [Fact]
    public async Task RequestScreening_UnknownApplication_ReturnsNull()
    {
        var sut = BuildService(new StubConfiguredProvider());
        var result = await sut.RequestScreeningAsync(PortfolioId, applicationId: 9999, userId: 7);
        result.Should().BeNull();
    }

    [Fact]
    public async Task GenerateAdverseAction_ProducesNonEmptyPdf_StoresFile_AndNoticeRow()
    {
        var app = await SeedApplicationAsync(consent: true);
        var sut = BuildService(new StubConfiguredProvider());

        var response = await sut.GenerateAdverseActionAsync(
            PortfolioId, app.Id, userId: 7, new GenerateAdverseActionRequest { Reason = "Insufficient credit history.", SendToApplicant = true });

        response.Should().NotBeNull();
        response!.StoredFileId.Should().NotBeNull();
        response.Reason.Should().Be("Insufficient credit history.");
        response.CreditReportingAgency.Should().Contain("TransUnion");
        response.SentAtUtc.Should().NotBeNull("the notice is enqueued to the applicant");

        // A notice row is stored and readable.
        var notice = await _db.AdverseActionNotices.SingleAsync();
        notice.ApplicationId.Should().Be(app.Id);
        notice.StoredFileId.Should().Be(response.StoredFileId);

        // A StoredFile row was created, attached to the application, with PDF bytes.
        var file = await _db.StoredFiles.SingleAsync(f => f.Id == response.StoredFileId);
        file.EntityType.Should().Be("Application");
        file.EntityId.Should().Be(app.Id);
        file.ContentType.Should().Be("application/pdf");
        file.FileSize.Should().BeGreaterThan(0);

        // The stored bytes are a real, non-empty PDF.
        var bytes = _storage.Files[file.FilePath];
        bytes.Length.Should().BeGreaterThan(0);
        Encoding.ASCII.GetString(bytes, 0, 5).Should().StartWith("%PDF");

        // It was enqueued to the applicant via the email outbox.
        _publisher.Published.Should().ContainSingle(p => p.messageType == "email");
    }

    [Fact]
    public async Task GenerateAdverseAction_NoEmail_DoesNotEnqueue()
    {
        var app = await SeedApplicationAsync(consent: true, email: null);
        var sut = BuildService(new StubConfiguredProvider());

        var response = await sut.GenerateAdverseActionAsync(
            PortfolioId, app.Id, userId: 7, new GenerateAdverseActionRequest { SendToApplicant = true });

        response.Should().NotBeNull();
        response!.SentAtUtc.Should().BeNull("there is no applicant email to send to");
        _publisher.Published.Should().BeEmpty();
    }

    // --- Test doubles ---------------------------------------------------------------------------

    /// <summary>A configured provider that returns a completed "decline" report (no real HTTP).</summary>
    private sealed class StubConfiguredProvider : IScreeningProvider
    {
        public bool IsConfigured => true;

        public Task<ScreeningProviderResult> RequestScreeningAsync(ScreeningRequest request, CancellationToken ct = default) =>
            Task.FromResult(new ScreeningProviderResult
            {
                IsConfigured = true,
                Completed = true,
                ProviderReference = "tu-123",
                CreditScoreBand = "Poor",
                HasCriminalRecord = false,
                HasEvictionRecord = true,
                Recommendation = ScreeningRecommendation.Decline,
                RawResultJson = "{\"id\":\"tu-123\"}",
            });
    }

    private sealed class FakeFileStorage : IFileStorage
    {
        public Dictionary<string, byte[]> Files { get; } = new();

        public async Task<string> UploadAsync(Stream content, string fileName, string contentType, CancellationToken ct = default)
        {
            using var ms = new MemoryStream();
            await content.CopyToAsync(ms, ct);
            var key = $"{Guid.NewGuid():N}/{fileName}";
            Files[key] = ms.ToArray();
            return key;
        }

        public Task<Stream> DownloadAsync(string path, CancellationToken ct = default) =>
            Task.FromResult<Stream>(new MemoryStream(Files[path]));

        public Task DeleteAsync(string path, CancellationToken ct = default)
        {
            Files.Remove(path);
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingPublisher : IMessagePublisher
    {
        public List<(int portfolioId, string messageType)> Published { get; } = [];

        public Task PublishAsync<TPayload>(int portfolioId, string messageType, TPayload payload, CancellationToken ct = default)
        {
            Published.Add((portfolioId, messageType));
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingAuditService : IAuditTrailService
    {
        public Task LogAsync(
            int portfolioId, string entityType, int entityId, AuditLogOperation operation,
            int? userId = null, string? actorLabel = null, string? oldValues = null,
            string? newValues = null, string? changeReason = null, string? ipAddress = null,
            CancellationToken ct = default) => Task.CompletedTask;
    }
}
