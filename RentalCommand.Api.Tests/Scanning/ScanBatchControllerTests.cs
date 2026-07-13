using System.Data.Common;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Moq;
using RentalCommand.Api.Auth;
using RentalCommand.Api.Controllers;
using RentalCommand.Api.DTOs;
using RentalCommand.Core.Authorization;
using RentalCommand.Core.Atomic;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Interfaces;
using RentalCommand.Core.Scanning;
using RentalCommand.Data;

namespace RentalCommand.Api.Tests.Scanning;

/// <summary>
/// Tests for the bulk-scan batch endpoints on <see cref="ScanController"/>. Uses SQLite in-memory
/// so the translated conditional-count, paging, and projection SQL is exercised by a relational provider.
/// </summary>
public class ScanBatchControllerTests : IDisposable
{
    private const int PortfolioId = 42;

    private readonly SqliteConnection _conn;
    private readonly RentalCommandDbContext _db;
    private readonly List<string> _executedSql = [];

    public ScanBatchControllerTests()
    {
        _conn = new SqliteConnection("DataSource=:memory:");
        _conn.Open();

        var options = new DbContextOptionsBuilder<RentalCommandDbContext>()
            .UseSqlite(_conn)
            .AddInterceptors(new RecordingCommandInterceptor(_executedSql))
            .Options;

        _db = new RentalCommandTestDbContext(options);
        _db.Database.EnsureCreated();

        SeedPortfolio(PortfolioId);
    }

    public void Dispose()
    {
        _db.Dispose();
        _conn.Dispose();
    }

    // -------------------------------------------------------------------------
    // Batch upload: N files -> 1 batch + N Pending Lease drafts linked by BatchId
    // -------------------------------------------------------------------------

    [Fact]
    public async Task UploadBatch_WithThreeFiles_CreatesBatchAndThreePendingLeaseDrafts()
    {
        // A recording scan service that persists a Pending draft per call (mirrors production
        // ScanService.CreateBatchDraftAsync) so we can assert the rows it created.
        var controller = CreateController(
            Mock.Of<IScanService>(), new RecordingBatchScanUploadService(_db));

        var files = new List<IFormFile>
        {
            FakeFile("lease-1.pdf", [1, 2, 3]),
            FakeFile("lease-2.pdf", [4, 5, 6]),
            FakeFile("lease-3.pdf", [7, 8, 9]),
        };

        var result = await controller.UploadBatch(
            files, targetEntityType: null, name: "Spring imports", clientOperationId: "batch-one", CancellationToken.None);

        var created = result.Result.Should().BeOfType<CreatedAtActionResult>().Subject;
        var body = created.Value.Should().BeOfType<ScanBatchCreatedResponse>().Subject;

        body.FileCount.Should().Be(3);
        body.TargetEntityType.Should().Be("LeaseAgreement"); // default target
        body.Name.Should().Be("Spring imports");
        body.Status.Should().Be(nameof(ScanBatchStatus.Processing));
        body.DraftIds.Should().HaveCount(3);

        // One batch row, all drafts Pending + Lease + linked to the batch.
        var batch = await _db.ScanBatches.SingleAsync();
        batch.PortfolioId.Should().Be(PortfolioId);
        batch.FileCount.Should().Be(3);

        var drafts = await _db.ScanDrafts.Where(d => d.BatchId == batch.Id).ToListAsync();
        drafts.Should().HaveCount(3);
        drafts.Should().OnlyContain(d => d.Status == "Pending");
        drafts.Should().OnlyContain(d => d.TargetEntityType == "LeaseAgreement");
        drafts.Should().OnlyContain(d => d.PortfolioId == PortfolioId);
        drafts.Select(d => d.Id).Should().BeEquivalentTo(body.DraftIds);
    }

    [Fact]
    public async Task UploadBatch_WithNoFiles_ReturnsBadRequest()
    {
        var controller = CreateController(
            Mock.Of<IScanService>(), new RecordingBatchScanUploadService(_db));

        var result = await controller.UploadBatch(
            [], targetEntityType: null, name: null, clientOperationId: "batch-empty", CancellationToken.None);

        result.Result.Should().BeOfType<BadRequestObjectResult>();
        (await _db.ScanBatches.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task UploadBatch_WithInvalidTarget_ReturnsBadRequest()
    {
        var controller = CreateController(
            Mock.Of<IScanService>(), new RecordingBatchScanUploadService(_db));
        var files = new List<IFormFile> { FakeFile("doc.pdf", [1]) };

        var result = await controller.UploadBatch(
            files, targetEntityType: "Banana", name: null, clientOperationId: "batch-invalid", CancellationToken.None);

        var badRequest = result.Result.Should().BeOfType<BadRequestObjectResult>().Subject;
        var error = badRequest.Value!.GetType().GetProperty("error")!.GetValue(badRequest.Value) as string;
        error.Should().Contain("Application");
        (await _db.ScanBatches.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task UploadBatch_WhenDraftCreationFails_RollsBackBatchAndDraftRows()
    {
        var controller = CreateController(Mock.Of<IScanService>(), new FailingBatchScanUploadService());
        var files = new List<IFormFile>
        {
            FakeFile("lease-1.pdf", [1, 2, 3]),
            FakeFile("lease-2.pdf", [4, 5, 6]),
        };

        var result = await controller.UploadBatch(
            files, targetEntityType: "LeaseAgreement", name: "Bad batch", clientOperationId: "batch-fail", CancellationToken.None);

        result.Result.Should().BeOfType<BadRequestObjectResult>();
        (await _db.ScanBatches.CountAsync()).Should().Be(0);
        (await _db.ScanDrafts.CountAsync()).Should().Be(0);
    }

    // -------------------------------------------------------------------------
    // Batch list/detail: correct rollup counts
    // -------------------------------------------------------------------------

    [Fact]
    public async Task ListBatches_ReturnsRollupCounts()
    {
        var batch = SeedBatch(PortfolioId, fileCount: 4);
        SeedDraft(batch.Id, "Pending");
        SeedDraft(batch.Id, "Reviewing");
        SeedDraft(batch.Id, "Confirmed");
        SeedDraft(batch.Id, "Rejected");

        var controller = CreateController(Mock.Of<IScanService>());

        _executedSql.Clear();
        var result = await controller.ListBatches(skip: 0, take: 50, CancellationToken.None);

        var list = result.Result.Should().BeOfType<OkObjectResult>().Subject
            .Value.Should().BeAssignableTo<IReadOnlyList<ScanBatchSummaryResponse>>().Subject;

        list.Should().HaveCount(1);
        var summary = list[0];
        summary.Id.Should().Be(batch.Id);
        summary.Counts.Total.Should().Be(4);
        summary.Counts.Pending.Should().Be(1);
        summary.Counts.Reviewing.Should().Be(1);
        summary.Counts.Confirmed.Should().Be(1);
        summary.Counts.Rejected.Should().Be(1);
        // One draft is still Pending/Reviewing -> batch is not Completed.
        summary.Status.Should().Be(nameof(ScanBatchStatus.Reviewing));

        _executedSql.Should().ContainSingle(
            "batch paging, conditional counts, and the effective status must be one translated SQL statement");
        _executedSql[0].Should().ContainEquivalentOf("COUNT");
        _executedSql[0].Should().ContainEquivalentOf("ORDER BY");
        _executedSql[0].Should().ContainEquivalentOf("LIMIT");
    }

    [Fact]
    public async Task ListPage_ReturnsSqlCountAndRequestedWindow()
    {
        var batch = SeedBatch(PortfolioId, fileCount: 4);
        SeedDraft(batch.Id, "Reviewing", targetEntityType: "Application");
        SeedDraft(batch.Id, "Reviewing", targetEntityType: "Expense");
        SeedDraft(batch.Id, "Reviewing", targetEntityType: "LeaseAgreement");
        SeedDraft(batch.Id, "Reviewing", targetEntityType: "Payment");
        SeedDraft(batch.Id, "Failed", targetEntityType: "WorkOrder");

        var controller = CreateController(Mock.Of<IScanService>());

        _executedSql.Clear();
        var result = await controller.ListPage(
            new ListQuery { Sort = "targetEntityType", Skip = 1, Take = 2 },
            status: "Reviewing",
            CancellationToken.None);

        var page = result.Result.Should().BeOfType<OkObjectResult>().Subject
            .Value.Should().BeOfType<ScanDraftListResponse>().Subject;

        page.TotalCount.Should().Be(4);
        page.Skip.Should().Be(1);
        page.Take.Should().Be(2);
        page.Items.Select(d => d.TargetEntityType).Should().Equal("Expense", "LeaseAgreement");
        page.Items.Should().OnlyContain(d => d.Status == "Reviewing");

        _executedSql.Should().Contain(sql =>
            sql.Contains("COUNT", StringComparison.OrdinalIgnoreCase) &&
            sql.Contains("FROM \"ScanDrafts\"", StringComparison.OrdinalIgnoreCase));
        _executedSql.Should().Contain(sql =>
            sql.Contains("ORDER BY", StringComparison.OrdinalIgnoreCase) &&
            sql.Contains("LIMIT", StringComparison.OrdinalIgnoreCase) &&
            sql.Contains("OFFSET", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task GetBatch_ReturnsDraftsAndCounts_AndCompletesWhenAllFinalized()
    {
        var batch = SeedBatch(PortfolioId, fileCount: 2);
        SeedDraft(batch.Id, "Confirmed",
            extractedFields: """{"tenant_name":{"value":"Marcus Williams","confidence":0.9},"unit_id":{"value":"20","confidence":0.8},"start_date":{"value":"2026-01-01","confidence":0.9},"end_date":{"value":"2026-12-31","confidence":0.9}}""",
            confirmedEntityId: 731);
        SeedDraft(batch.Id, "Rejected");

        var controller = CreateController(Mock.Of<IScanService>());

        var result = await controller.GetBatch(batch.Id, CancellationToken.None);

        var detail = result.Result.Should().BeOfType<OkObjectResult>().Subject
            .Value.Should().BeOfType<ScanBatchDetailResponse>().Subject;

        detail.Counts.Total.Should().Be(2);
        detail.Counts.Confirmed.Should().Be(1);
        detail.Counts.Rejected.Should().Be(1);
        // Every draft confirmed/rejected -> batch rolls up to Completed.
        detail.Status.Should().Be(nameof(ScanBatchStatus.Completed));

        detail.Drafts.Should().HaveCount(2);
        var confirmed = detail.Drafts.Single(d => d.Status == "Confirmed");
        confirmed.Tenant.Should().Be("Marcus Williams");
        confirmed.Unit.Should().Be("20");
        confirmed.Term.Should().Be("2026-01-01 – 2026-12-31");
        confirmed.CreatedEntityId.Should().Be(731);
    }

    [Fact]
    public async Task GetBatch_UsesOneSummaryQueryAndOneProjectedDraftQuery()
    {
        var batch = SeedBatch(PortfolioId, fileCount: 5);
        SeedDraft(batch.Id, "Pending");
        SeedDraft(batch.Id, "Processing");
        SeedDraft(batch.Id, "Reviewing");
        SeedDraft(batch.Id, "Confirmed");
        SeedDraft(batch.Id, "Failed");

        var controller = CreateController(Mock.Of<IScanService>());

        _executedSql.Clear();
        var result = await controller.GetBatch(batch.Id, CancellationToken.None);

        var detail = result.Result.Should().BeOfType<OkObjectResult>().Subject
            .Value.Should().BeOfType<ScanBatchDetailResponse>().Subject;
        detail.Counts.Total.Should().Be(5);
        detail.Counts.Pending.Should().Be(2);
        detail.Counts.Reviewing.Should().Be(1);
        detail.Counts.Confirmed.Should().Be(1);
        detail.Counts.Failed.Should().Be(1);

        _executedSql.Should().HaveCount(2,
            "batch detail must use one SQL summary query plus one filtered, sorted draft projection");
        _executedSql[0].Should().ContainEquivalentOf("COUNT");
        _executedSql[1].Should().ContainEquivalentOf("ORDER BY");
        _executedSql.Should().NotContain(sql =>
            sql.Contains("StoredFiles", StringComparison.OrdinalIgnoreCase),
            "confirmed entity ids come directly from ScanDraft.ConfirmedEntityId");
    }

    [Fact]
    public async Task GetBatch_SummarizesUnitNumberFromLeaseExtraction()
    {
        var batch = SeedBatch(PortfolioId, fileCount: 1);
        SeedDraft(batch.Id, "Reviewing",
            extractedFields: """{"tenant_name":{"value":"Avery Ellis","confidence":0.9},"unit_number":{"value":"1A","confidence":0.9},"start_date":{"value":"2026-01-01","confidence":0.9},"end_date":{"value":"2027-01-01","confidence":0.9}}""");

        var controller = CreateController(Mock.Of<IScanService>());

        var result = await controller.GetBatch(batch.Id, CancellationToken.None);

        var detail = result.Result.Should().BeOfType<OkObjectResult>().Subject
            .Value.Should().BeOfType<ScanBatchDetailResponse>().Subject;
        detail.Drafts.Should().ContainSingle().Which.Unit.Should().Be("1A");
    }

    [Fact]
    public async Task Get_IncludesFailureReasonForFailedDraft()
    {
        var batch = SeedBatch(PortfolioId, fileCount: 1);
        var draft = SeedDraft(batch.Id, "Failed",
            targetEntityType: "Expense",
            failureReason: "extraction interrupted (timeout or shutdown)");

        var controller = CreateController(Mock.Of<IScanService>());

        var result = await controller.Get(draft.Id, CancellationToken.None);

        var response = result.Result.Should().BeOfType<OkObjectResult>().Subject
            .Value.Should().BeOfType<ScanDraftResponse>().Subject;
        response.Status.Should().Be("Failed");
        response.FailureReason.Should().Be("extraction interrupted (timeout or shutdown)");
    }

    [Fact]
    public async Task Retry_FailedDraft_RequeuesAndClearsStaleExtractionData()
    {
        var batch = SeedBatch(PortfolioId, fileCount: 1);
        var draft = SeedDraft(batch.Id, "Failed",
            extractedFields: """{"tenant_name":{"value":"Avery Ellis","confidence":0.9}}""",
            failureReason: "extraction interrupted (timeout or shutdown)");
        draft.ModelId = "claude-cli:sonnet";
        draft.TokensUsed = 1234;
        draft.CostUsd = 0.0123m;
        draft.ReviewedAt = DateTime.UtcNow;
        draft.ReviewedBy = "7";
        draft.ConfirmedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();

        var controller = CreateController(Mock.Of<IScanService>());

        var result = await controller.Retry(draft.Id, CancellationToken.None);

        result.Should().BeOfType<OkResult>();

        _db.ChangeTracker.Clear();
        var reloaded = await _db.ScanDrafts.SingleAsync(d => d.Id == draft.Id);
        reloaded.Status.Should().Be("Pending");
        reloaded.ExtractedFields.Should().BeNull();
        reloaded.FailureReason.Should().BeNull();
        reloaded.ModelId.Should().BeNull();
        reloaded.TokensUsed.Should().BeNull();
        reloaded.CostUsd.Should().BeNull();
        reloaded.ReviewedAt.Should().BeNull();
        reloaded.ReviewedBy.Should().BeNull();
        reloaded.ConfirmedAt.Should().BeNull();
    }

    [Fact]
    public async Task Retry_NonFailedDraft_ReturnsBadRequest()
    {
        var batch = SeedBatch(PortfolioId, fileCount: 1);
        var draft = SeedDraft(batch.Id, "Reviewing");
        var controller = CreateController(Mock.Of<IScanService>());

        var result = await controller.Retry(draft.Id, CancellationToken.None);

        result.Should().BeOfType<BadRequestObjectResult>();
        _db.ChangeTracker.Clear();
        (await _db.ScanDrafts.SingleAsync(d => d.Id == draft.Id)).Status.Should().Be("Reviewing");
    }

    [Fact]
    public async Task Retry_CrossPortfolioDraft_ReturnsNotFound()
    {
        const int otherPortfolioId = 99;
        SeedPortfolio(otherPortfolioId);
        var foreignBatch = SeedBatch(otherPortfolioId, fileCount: 1);
        var foreignDraft = SeedDraft(foreignBatch.Id, "Failed", portfolioId: otherPortfolioId);
        var controller = CreateController(Mock.Of<IScanService>());

        var result = await controller.Retry(foreignDraft.Id, CancellationToken.None);

        result.Should().BeOfType<NotFoundObjectResult>();
        _db.ChangeTracker.Clear();
        (await _db.ScanDrafts.SingleAsync(d => d.Id == foreignDraft.Id)).Status.Should().Be("Failed");
    }

    // -------------------------------------------------------------------------
    // IDOR: a batch (and its drafts) in another portfolio is not readable.
    // -------------------------------------------------------------------------

    [Fact]
    public async Task GetBatch_CrossPortfolio_ReturnsNotFound()
    {
        const int otherPortfolioId = 99;
        SeedPortfolio(otherPortfolioId);

        var foreignBatch = SeedBatch(otherPortfolioId, fileCount: 1);
        SeedDraft(foreignBatch.Id, "Reviewing", portfolioId: otherPortfolioId);

        // Caller is portfolio 42; the batch belongs to portfolio 99.
        var controller = CreateController(Mock.Of<IScanService>());

        var result = await controller.GetBatch(foreignBatch.Id, CancellationToken.None);

        result.Result.Should().BeOfType<NotFoundObjectResult>();
    }

    [Fact]
    public async Task ListBatches_DoesNotIncludeOtherPortfoliosBatches()
    {
        const int otherPortfolioId = 99;
        SeedPortfolio(otherPortfolioId);
        SeedBatch(otherPortfolioId, fileCount: 1);

        var controller = CreateController(Mock.Of<IScanService>());

        var result = await controller.ListBatches(skip: 0, take: 50, CancellationToken.None);

        var list = result.Result.Should().BeOfType<OkObjectResult>().Subject
            .Value.Should().BeAssignableTo<IReadOnlyList<ScanBatchSummaryResponse>>().Subject;

        list.Should().BeEmpty();
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private ScanController CreateController(
        IScanService scan,
        IScanUploadService? uploads = null)
    {
        var files = Mock.Of<IFileStorage>();
        var controller = new ScanController(
            scan, uploads ?? Mock.Of<IScanUploadService>(), Mock.Of<IAtomicUnitOfWork>(), _db, files)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext(),
            },
        };
        controller.HttpContext.Items[CanonicalAccessContextHttpItem.Key] = new ActiveAccessContext(
            Guid.NewGuid(), 7, 1, PortfolioId, 1,
            WorkspaceExperience.Management, 1, WorkspaceExperience.Management);
        return controller;
    }

    private static IFormFile FakeFile(string name, byte[] bytes) =>
        new FormFile(new MemoryStream(bytes), 0, bytes.Length, "files", name)
        {
            Headers = new HeaderDictionary(),
            ContentType = "application/pdf",
        };

    private void SeedPortfolio(int portfolioId)
    {
        _db.Portfolios.Add(new Portfolio
        {
            Id = portfolioId,
            Name = $"Portfolio {portfolioId}",
            ManagementCompanyName = "Test Co",
            TimeZone = "UTC",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        _db.SaveChanges();
    }

    private ScanBatch SeedBatch(int portfolioId, int fileCount)
    {
        var batch = new ScanBatch
        {
            PortfolioId = portfolioId,
            TargetEntityType = "LeaseAgreement",
            Status = ScanBatchStatus.Processing,
            FileCount = fileCount,
            CreatedAtUtc = DateTime.UtcNow,
        };
        _db.ScanBatches.Add(batch);
        _db.SaveChanges();
        return batch;
    }

    private ScanDraft SeedDraft(
        int batchId,
        string status,
        string? extractedFields = null,
        int? portfolioId = null,
        string targetEntityType = "LeaseAgreement",
        string? failureReason = null,
        int? confirmedEntityId = null)
    {
        var draft = new ScanDraft
        {
            PortfolioId = portfolioId ?? PortfolioId,
            BatchId = batchId,
            FilePath = $"uploads/{Guid.NewGuid():N}.pdf",
            TargetEntityType = targetEntityType,
            Status = status,
            ExtractedFields = extractedFields,
            FailureReason = failureReason,
            ConfirmedEntityId = confirmedEntityId,
            CreatedAt = DateTime.UtcNow,
        };
        _db.ScanDrafts.Add(draft);
        _db.SaveChanges();
        return draft;
    }

    /// <summary>
    /// Stand-in scan service that persists a Pending draft per CreateBatchDraftAsync call, exactly as
    /// production does, so the controller's batch wiring (link to batch, ids returned) can be asserted
    /// without touching real file storage / image resizing.
    /// </summary>
    private sealed class RecordingBatchScanUploadService : IScanUploadService
    {
        private readonly RentalCommandDbContext _db;

        public RecordingBatchScanUploadService(RentalCommandDbContext db) => _db = db;

        public async Task<FinalizeScanUploadResult> UploadAsync(
            int portfolioId,
            int userId,
            string clientOperationId,
            string targetEntityType,
            bool createBatch,
            string? batchName,
            IReadOnlyList<ScanUploadFilePayload> files,
            CancellationToken ct = default)
        {
            var batch = new ScanBatch
            {
                PortfolioId = portfolioId,
                Name = batchName,
                TargetEntityType = targetEntityType,
                Status = ScanBatchStatus.Processing,
                FileCount = files.Count,
                CreatedAtUtc = DateTime.UtcNow,
            };
            _db.ScanBatches.Add(batch);
            await _db.SaveChangesAsync(ct);
            var drafts = files.Select(_ => new ScanDraft
            {
                PortfolioId = portfolioId,
                BatchId = batch.Id,
                FilePath = $"uploads/{Guid.NewGuid():N}",
                TargetEntityType = targetEntityType,
                Status = "Pending",
                CreatedAt = DateTime.UtcNow,
            }).ToArray();
            _db.ScanDrafts.AddRange(drafts);
            await _db.SaveChangesAsync(ct);
            return new FinalizeScanUploadResult(
                batch.Id,
                batch.Name,
                batch.TargetEntityType,
                drafts.Select(draft => new FinalizedScanDraft(
                    draft.Id, draft.Status, draft.FilePath)).ToArray());
        }
    }

    private sealed class FailingBatchScanUploadService : IScanUploadService
    {
        public Task<FinalizeScanUploadResult> UploadAsync(
            int portfolioId,
            int userId,
            string clientOperationId,
            string targetEntityType,
            bool createBatch,
            string? batchName,
            IReadOnlyList<ScanUploadFilePayload> files,
            CancellationToken ct = default)
            => throw new ArgumentException("Second draft failed validation.");
    }

    private sealed class RecordingCommandInterceptor(List<string> commands) : DbCommandInterceptor
    {
        public override InterceptionResult<DbDataReader> ReaderExecuting(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result)
        {
            commands.Add(command.CommandText);
            return base.ReaderExecuting(command, eventData, result);
        }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            commands.Add(command.CommandText);
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }
    }
}
