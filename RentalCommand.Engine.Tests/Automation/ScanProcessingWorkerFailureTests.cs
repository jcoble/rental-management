using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Interfaces;
using RentalCommand.Data;
using RentalCommand.Engine.Workers;
using RentalCommand.TestCommon;

namespace RentalCommand.Engine.Tests.Automation;

/// <summary>
/// Hardening tests for the silent-empty-draft bug: a failed/empty/truncated/unparseable LLM
/// extraction must drive the draft to a terminal "Failed" (with a reason) — never get stored as a
/// "Reviewing" draft whose every field is blank with 0 confidence.
///
/// The pure-decision tests exercise <see cref="ScanProcessingWorker.GetExtractionFailureReason"/>
/// directly; the end-to-end tests run a real <see cref="ScanProcessingWorker"/> cycle against a
/// SQLite-backed <see cref="RentalCommandDbContext"/> with a stub <see cref="ILlmProvider"/>.
/// </summary>
public class ScanProcessingWorkerFailureTests : IDisposable
{
    private readonly SqliteConnection _conn;
    private readonly ServiceProvider _provider;
    private readonly StubLlmProvider _llm = new();

    public ScanProcessingWorkerFailureTests()
    {
        // One kept-alive in-memory SQLite connection shared by every resolved DbContext (the worker
        // opens a child DI scope for the fail-path write, so they must hit the same database).
        _conn = new SqliteConnection("DataSource=:memory:");
        _conn.Open();

        var options = new DbContextOptionsBuilder<RentalCommandDbContext>()
            .UseSqlite(_conn)
            .Options;

        var services = new ServiceCollection();
        services.AddLogging();
        // Scoped so each DI scope (including the worker's child fail-path scope) gets a fresh
        // DbContext over the one shared in-memory connection — matching production scoping.
        services.AddScoped<RentalCommandDbContext>(_ => new ScanTestDbContext(options));
        services.AddSingleton<ILlmProvider>(_llm);
        services.AddSingleton<IFileStorage, StubFileStorage>();
        services.AddSingleton<IDataUpdateService, StubDataUpdateService>();
        _provider = services.BuildServiceProvider();

        using var scope = _provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RentalCommandDbContext>();
        db.Database.EnsureCreated();
        db.Portfolios.Add(new Portfolio
        {
            Id = 1,
            Name = "Test Portfolio",
            ManagementCompanyName = "Test Co",
            TimeZone = "UTC",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        db.SaveChanges();
    }

    public void Dispose()
    {
        _provider.Dispose();
        _conn.Dispose();
    }

    // -----------------------------------------------------------------------
    // Pure decision: GetExtractionFailureReason
    // -----------------------------------------------------------------------

    [Fact]
    public void GetExtractionFailureReason_AllFieldsBlank_ReturnsNoFieldsReason()
    {
        var fields = new[]
        {
            new ExtractionFieldSpec("vendor_name", "string", "vendor"),
            new ExtractionFieldSpec("total", "number", "total"),
        };
        var extracted = Extracted(("vendor_name", "  "), ("total", ""));

        var reason = ScanProcessingWorker.GetExtractionFailureReason(extracted, fields);

        reason.Should().Be("no fields could be extracted from the document");
    }

    [Fact]
    public void GetExtractionFailureReason_OnlyClassifierField_TreatedAsEmpty()
    {
        // A model that read nothing still picks a document_kind; that classifier alone is not data.
        var fields = new[]
        {
            new ExtractionFieldSpec("document_kind", "enum", "kind"),
            new ExtractionFieldSpec("vendor_name", "string", "vendor"),
        };
        var extracted = Extracted(("document_kind", "Receipt"), ("vendor_name", ""));

        var reason = ScanProcessingWorker.GetExtractionFailureReason(extracted, fields);

        reason.Should().Be("no fields could be extracted from the document");
    }

    [Fact]
    public void GetExtractionFailureReason_ProviderFailureWins_EvenWithAValue()
    {
        var fields = new[] { new ExtractionFieldSpec("vendor_name", "string", "vendor") };
        var extracted = Extracted(("vendor_name", "Apex Plumbing"));
        extracted.FailureReason = "response truncated (hit max output tokens)";

        var reason = ScanProcessingWorker.GetExtractionFailureReason(extracted, fields);

        reason.Should().Be("response truncated (hit max output tokens)");
    }

    [Fact]
    public void GetExtractionFailureReason_HasRealValue_ReturnsNull()
    {
        var fields = new[]
        {
            new ExtractionFieldSpec("document_kind", "enum", "kind"),
            new ExtractionFieldSpec("vendor_name", "string", "vendor"),
        };
        var extracted = Extracted(("document_kind", "Receipt"), ("vendor_name", "Apex Plumbing"));

        ScanProcessingWorker.GetExtractionFailureReason(extracted, fields).Should().BeNull();
    }

    // -----------------------------------------------------------------------
    // End-to-end: a Pending draft through one worker cycle
    // -----------------------------------------------------------------------

    [Fact]
    public async Task Cycle_EmptyExtraction_MarksFailedWithReason_NotReviewing()
    {
        var draftId = SeedPendingDraft();
        _llm.Result = Extracted(
            ("document_kind", "Receipt"),  // classifier only — no real data
            ("vendor_name", ""),
            ("total", "   "));

        await RunCycleAsync();

        var draft = await ReloadAsync(draftId);
        draft.Status.Should().Be("Failed");
        draft.FailureReason.Should().Be("no fields could be extracted from the document");
        // The all-blank skeleton must NOT have been stored as a ready-to-review draft.
        draft.ExtractedFields.Should().BeNull();
    }

    [Fact]
    public async Task Cycle_TruncatedResponse_MarksFailedWithTruncationReason()
    {
        var draftId = SeedPendingDraft();
        var result = Extracted(("vendor_name", "Apex Plumbing"));
        result.Truncated = true;
        result.FailureReason = "response truncated (hit max output tokens)";
        _llm.Result = result;

        await RunCycleAsync();

        var draft = await ReloadAsync(draftId);
        draft.Status.Should().Be("Failed");
        draft.FailureReason.Should().Be("response truncated (hit max output tokens)");
    }

    [Fact]
    public async Task Cycle_GoodExtraction_MarksReviewingWithFields()
    {
        var draftId = SeedPendingDraft();
        _llm.Result = Extracted(("vendor_name", "Apex Plumbing"), ("total", "84.20"));

        await RunCycleAsync();

        var draft = await ReloadAsync(draftId);
        draft.Status.Should().Be("Reviewing");
        draft.FailureReason.Should().BeNull();
        draft.ExtractedFields.Should().NotBeNull();
        draft.ExtractedFields!.Should().Contain("Apex Plumbing");
    }

    // -----------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------

    private async Task RunCycleAsync()
    {
        var worker = new TestableScanProcessingWorker(_provider);
        await worker.RunOneCycleAsync(_provider, CancellationToken.None);
    }

    private int SeedPendingDraft()
    {
        using var scope = _provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RentalCommandDbContext>();
        var draft = new ScanDraft
        {
            PortfolioId = 1,
            FilePath = "scans/test-key",
            TargetEntityType = "Expense",
            Status = "Pending",
            CreatedAt = DateTime.UtcNow,
        };
        db.ScanDrafts.Add(draft);
        db.StoredFiles.Add(new StoredFile
        {
            PortfolioId = 1,
            FileName = "receipt.jpg",
            FilePath = "scans/test-key",
            ContentType = "image/jpeg",
            FileSize = 4,
            UploadedAt = DateTime.UtcNow,
        });
        db.SaveChanges();
        return draft.Id;
    }

    private async Task<ScanDraft> ReloadAsync(int draftId)
    {
        using var scope = _provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RentalCommandDbContext>();
        return await db.ScanDrafts.AsNoTracking().FirstAsync(d => d.Id == draftId);
    }

    private static ExtractedFields Extracted(params (string Name, string Value)[] values)
    {
        var result = new ExtractedFields { ModelId = "test-model" };
        foreach (var (name, value) in values)
            result.Fields[name] = new FieldExtraction { Value = value, Confidence = 0.9m };
        return result;
    }

    // ---- Test doubles -----------------------------------------------------

    /// <summary>Exposes the protected cycle so a test can run exactly one pass deterministically.</summary>
    private sealed class TestableScanProcessingWorker : ScanProcessingWorker
    {
        public TestableScanProcessingWorker(IServiceProvider sp)
            : base(sp, NullLogger<ScanProcessingWorker>.Instance) { }

        public Task<int> RunOneCycleAsync(IServiceProvider scoped, CancellationToken ct)
            => ExecuteCycleAsync(scoped, ct);
    }

    private sealed class StubLlmProvider : ILlmProvider
    {
        public ExtractedFields Result { get; set; } = new();

        public Task<ExtractedFields> ExtractAsync(
            byte[] documentBytes, string contentType, string instructions,
            IReadOnlyList<ExtractionFieldSpec> fields, string? groundingContext = null,
            CancellationToken ct = default) => Task.FromResult(Result);

        public Task<string> ChatAsync(string prompt, CancellationToken ct = default)
            => Task.FromResult(string.Empty);

        public Task<LlmToolResult> ChatWithToolsAsync(
            string systemPrompt, IReadOnlyList<LlmChatMessage> messages,
            IReadOnlyList<LlmToolSpec> tools, CancellationToken ct = default)
            => Task.FromResult(new LlmToolResult("end", null, Array.Empty<LlmToolCall>(), 0, 0, "test-model"));
    }

    private sealed class StubFileStorage : IFileStorage
    {
        public Task<string> UploadAsync(Stream content, string fileName, string contentType, CancellationToken ct = default)
            => Task.FromResult("scans/test-key");

        public Task<Stream> DownloadAsync(string path, CancellationToken ct = default)
            => Task.FromResult<Stream>(new MemoryStream(new byte[] { 1, 2, 3, 4 }));

        public Task DeleteAsync(string path, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class StubDataUpdateService : IDataUpdateService
    {
        public Task BroadcastEntityUpdateAsync(int portfolioId, string entityType, int entityId, object data, CancellationToken ct = default)
            => Task.CompletedTask;

        public Task BroadcastEntityDeleteAsync(int portfolioId, string entityType, int entityId, CancellationToken ct = default)
            => Task.CompletedTask;
    }
}

/// <summary>
/// SQLite-friendly DbContext for these tests: maps the Postgres jsonb columns this worker touches to
/// TEXT and drops Postgres-only table configuration the worker's queries don't need, mirroring
/// <see cref="SqliteTestContext"/>'s approach.
/// </summary>
internal sealed class ScanTestDbContext : RentalCommandDbContext
{
    public ScanTestDbContext(DbContextOptions<RentalCommandDbContext> options) : base(options) { }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<ScanDraft>().Property(e => e.ExtractedFields).HasColumnType("TEXT");
        modelBuilder.Entity<AuditLog>().Property(e => e.OldValues).HasColumnType("TEXT");
        modelBuilder.Entity<AuditLog>().Property(e => e.NewValues).HasColumnType("TEXT");
        modelBuilder.Entity<OutboxMessage>().Property(e => e.Payload).HasColumnType("TEXT");
        modelBuilder.Entity<QueuedJob>().Property(e => e.Payload).HasColumnType("TEXT");
        modelBuilder.Entity<Expense>().Property(e => e.ReceiptData).HasColumnType("TEXT");
        modelBuilder.Entity<SecurityDepositHolding>().Property(e => e.DeductionsJson).HasColumnType("TEXT");

        modelBuilder.Entity<Lease>().ToTable("Leases");
        modelBuilder.Entity<VendorRating>().ToTable("VendorRatings");

        modelBuilder.Entity<Payment>()
            .HasIndex(p => new { p.LeaseId, p.PaymentType, p.PeriodKey })
            .IsUnique()
            .HasFilter(null);

        modelBuilder.Entity<AutopayEnrollment>()
            .HasIndex(e => e.LeaseId)
            .IsUnique()
            .HasFilter(null);
    }
}
