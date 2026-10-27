using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using RentalCommand.Api.Scanning;
using RentalCommand.Core.Atomic;
using RentalCommand.Core.Configuration;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Interfaces;
using RentalCommand.Data;
using RentalCommand.Data.Scanning;
using RentalCommand.Engine.Workers;
using RentalCommand.TestCommon;

namespace RentalCommand.Engine.Tests.Automation;

public sealed class ScanProcessingWorkerJevClassificationTests : IDisposable
{
    private readonly SqliteConnection _connection = new("DataSource=:memory:");
    private readonly StubProvider _provider = new();
    private readonly StubResolver _resolver = new();
    private readonly StubOcr _ocr = new();
    private readonly StubUsageRecorder _usage = new();
    private readonly FakeJevHandler _jev = new();
    private readonly ServiceProvider _services;

    public ScanProcessingWorkerJevClassificationTests()
    {
        _connection.Open();
        var options = new DbContextOptionsBuilder<RentalCommandDbContext>()
            .UseSqlite(_connection)
            .Options;
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddScoped<RentalCommandDbContext>(_ => new ScanTestDbContext(options));
        services.AddScoped<IScanProcessingClaimStore, ClaimStore>();
        services.AddScoped<IWriteExecutor, TerminalWriteExecutor>();
        services.AddSingleton<IWorkspaceLlmExtractionProvider>(_provider);
        services.AddSingleton<IWorkspaceLlmCredentialResolver>(_resolver);
        services.AddSingleton<ILlmUsageEvidenceRecorder>(_usage);
        services.AddSingleton<IImageTextExtractor>(_ocr);
        services.AddSingleton(new JevDecisionClient(new HttpClient(_jev)));
        services.AddSingleton<IFileStorage, FileStorage>();
        services.AddSingleton<IDataUpdateService, DataUpdates>();
        services.AddSingleton<IOptions<AssistantConfig>>(Options.Create(new AssistantConfig()));
        services.AddSingleton<IHostEnvironment>(new HostEnvironment());
        services.AddSingleton(TimeProvider.System);
        _services = services.BuildServiceProvider();

        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RentalCommandDbContext>();
        db.Database.EnsureCreated();
        db.Database.ExecuteSqlRaw("""
            CREATE VIEW "vw_lease_management_lifecycle" AS
            SELECT management."PortfolioId", management."Id" AS "LeaseManagementId",
                   'Occupied' AS "Lifecycle", NULL AS "CurrentAgreementId",
                   NULL AS "CurrentPrimaryTenantId", NULL AS "CurrentPrimaryTenantName"
            FROM "LeaseManagements" AS management
            """);
        db.Portfolios.Add(new Portfolio
        {
            Id = 1, Name = "Test", ManagementCompanyName = "Test", TimeZone = "UTC",
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        });
        db.SaveChanges();
    }

    [Fact]
    public async Task ConfidentJevPick_SkipsImageClassifier_AndExtractsWithPickedType()
    {
        _jev.Reply = JevReply(0.93);
        var draftId = SeedDraft();

        await RunCycleAsync();

        var draft = await ReloadAsync(draftId);
        draft.Status.Should().Be("Reviewing");
        draft.TargetEntityType.Should().Be("Expense");
        _provider.Calls.Should().ContainSingle();
        _provider.Calls[0].Instructions.Should().Be(ReceiptExtractionSchema.Instructions);
        using var fields = JsonDocument.Parse(draft.ExtractedFields!);
        fields.RootElement.GetProperty("classification_reason").GetProperty("value")
            .GetString().Should().Be("Jev, 93% sure");
        _usage.Receipts.Should().ContainSingle(item =>
            item.Feature == "scan.classification" && item.Provider == "typesafe");
    }

    [Fact]
    public async Task UnsureJevPick_FallsBackToImageClassifier()
    {
        _jev.Reply = JevReply(0.6);
        var draftId = SeedDraft();

        await RunCycleAsync();

        (await ReloadAsync(draftId)).Status.Should().Be("Reviewing");
        _provider.Calls.Should().HaveCount(2);
        _provider.Calls[0].Instructions.Should().Be(DocumentClassificationExtractionSchema.Instructions);
    }

    [Fact]
    public async Task JevError_FallsBackToImageClassifier()
    {
        _jev.Status = HttpStatusCode.InternalServerError;
        var draftId = SeedDraft();

        await RunCycleAsync();

        (await ReloadAsync(draftId)).Status.Should().Be("Reviewing");
        _provider.Calls.Should().HaveCount(2);
        _provider.Calls[0].Instructions.Should().Be(DocumentClassificationExtractionSchema.Instructions);
    }

    [Fact]
    public async Task NoReadableText_SkipsJev()
    {
        _ocr.Text = null;
        SeedDraft();

        await RunCycleAsync();

        _jev.RequestCount.Should().Be(0);
        _provider.Calls.Should().HaveCount(2);
    }

    [Fact]
    public async Task NoDecisionsKey_SkipsJev()
    {
        _resolver.Decisions = null;
        SeedDraft();

        await RunCycleAsync();

        _jev.RequestCount.Should().Be(0);
        _provider.Calls.Should().HaveCount(2);
    }

    [Fact]
    public async Task ImageClassifier_ReceivesNoLookupList()
    {
        _resolver.Decisions = null;
        SeedDraft(seedVendor: true);

        await RunCycleAsync();

        _provider.Calls.Should().HaveCount(2);
        _provider.Calls[0].Grounding.Should().BeNull();
        _provider.Calls[1].Grounding.Should().Contain("Test Vendor");
    }

    public void Dispose()
    {
        _services.Dispose();
        _connection.Dispose();
    }

    private async Task RunCycleAsync() =>
        await new TestWorker(_services).RunOneCycleAsync(_services, CancellationToken.None);

    private int SeedDraft(bool seedVendor = false)
    {
        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RentalCommandDbContext>();
        var stored = new StoredFile
        {
            PortfolioId = 1, FileName = "receipt.jpg", FilePath = "scans/test",
            ContentType = "image/jpeg", FileSize = 4, UploadedAt = DateTime.UtcNow,
        };
        db.StoredFiles.Add(stored);
        db.SaveChanges();
        var draft = new ScanDraft
        {
            PortfolioId = 1, FilePath = stored.FilePath, SourceStoredFileId = stored.Id,
            TargetEntityType = string.Empty, Status = "Pending", CreatedAt = DateTime.UtcNow,
        };
        db.ScanDrafts.Add(draft);
        if (seedVendor)
        {
            db.Vendors.Add(new Vendor
            {
                PortfolioId = 1, Name = "Test Vendor", CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            });
        }
        db.SaveChanges();
        return draft.Id;
    }

    private async Task<ScanDraft> ReloadAsync(int id)
    {
        using var scope = _services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<RentalCommandDbContext>()
            .ScanDrafts.AsNoTracking().SingleAsync(item => item.Id == id);
    }

    private static string JevReply(double probability) => JsonSerializer.Serialize(new
    {
        answers = new
        {
            pick = new
            {
                choice = "Expense",
                probabilities = new Dictionary<string, double> { ["Expense"] = probability },
            },
        },
    });

    private sealed class TestWorker(IServiceProvider services)
        : ScanProcessingWorker(services, NullLogger<ScanProcessingWorker>.Instance);

    private sealed class StubResolver : IWorkspaceLlmCredentialResolver
    {
        public WorkspaceLlmRuntimeCredential Extraction { get; } =
            new(1, "test", "test-model", "extraction-key");
        public WorkspaceLlmRuntimeCredential? Decisions { get; set; } =
            new(1, "typesafe", "jev-1.13.0", "decision-key");

        public Task<WorkspaceLlmRuntimeCredential?> ResolveActiveAsync(
            int portfolioId,
            string purpose,
            CancellationToken ct = default) => Task.FromResult(
                purpose == WorkspaceLlmCredentialPurposes.Decisions ? Decisions : Extraction);
    }

    private sealed class StubProvider : IWorkspaceLlmExtractionProvider
    {
        public string ProviderKey => "test";
        public List<ProviderCall> Calls { get; } = [];

        public Task<ExtractedFields> ExtractWorkspaceAsync(
            WorkspaceLlmRuntimeCredential credential,
            byte[] documentBytes,
            string contentType,
            string instructions,
            IReadOnlyList<ExtractionFieldSpec> fields,
            string? groundingContext = null,
            CancellationToken ct = default)
        {
            Calls.Add(new ProviderCall(instructions, groundingContext));
            var result = new ExtractedFields { ModelId = "test-model" };
            if (fields.Any(field => field.Name == "target_entity_type"))
            {
                result.Fields["target_entity_type"] = new FieldExtraction { Value = "Expense", Confidence = 0.9m };
                result.Fields["document_kind"] = new FieldExtraction { Value = "Expense", Confidence = 0.9m };
                result.Fields["classification_reason"] = new FieldExtraction { Value = "Image", Confidence = 0.9m };
            }
            else
            {
                result.Fields["total"] = new FieldExtraction { Value = "42.00", Confidence = 0.9m };
            }
            return Task.FromResult(result);
        }
    }

    private sealed record ProviderCall(string Instructions, string? Grounding);

    private sealed class StubOcr : IImageTextExtractor
    {
        public string? Text { get; set; } = "Test Vendor invoice total amount due 42 dollars";
        public string? TryExtractText(byte[] documentBytes, string contentType) => Text;
    }

    private sealed class FakeJevHandler : HttpMessageHandler
    {
        public HttpStatusCode Status { get; set; } = HttpStatusCode.OK;
        public string Reply { get; set; } = JevReply(0.93);
        public int RequestCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestCount++;
            return Task.FromResult(new HttpResponseMessage(Status)
            {
                Content = new StringContent(Reply, Encoding.UTF8, "application/json"),
            });
        }
    }

    private sealed class StubUsageRecorder : ILlmUsageEvidenceRecorder
    {
        public List<UsageReceipt> Receipts { get; } = [];
        public Task RecordUsageAsync(
            int portfolioId, string provider, string modelId, string feature,
            int latencyMilliseconds, int inputUnits, int outputUnits, decimal estimatedCostUsd,
            CancellationToken ct = default, string? usageEventIdentity = null)
        {
            Receipts.Add(new UsageReceipt(provider, feature));
            return Task.CompletedTask;
        }
    }

    private sealed record UsageReceipt(string Provider, string Feature);

    private sealed class FileStorage : IFileStorage
    {
        public Task<string> UploadAsync(Stream content, string fileName, string contentType, CancellationToken ct = default) =>
            Task.FromResult("scans/test");
        public Task<Stream> DownloadAsync(string path, CancellationToken ct = default) =>
            Task.FromResult<Stream>(new MemoryStream([1, 2, 3, 4]));
        public Task DeleteAsync(string path, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class DataUpdates : IDataUpdateService
    {
        public Task BroadcastEntityUpdateAsync(int portfolioId, string entityType, int entityId, object data, CancellationToken ct = default) => Task.CompletedTask;
        public Task BroadcastEntityDeleteAsync(int portfolioId, string entityType, int entityId, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class HostEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Production;
        public string ApplicationName { get; set; } = "Tests";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = null!;
    }

    private sealed class ClaimStore(RentalCommandDbContext db) : IScanProcessingClaimStore
    {
        public async Task<IReadOnlyList<ScanProcessingClaim>> ClaimAsync(
            string owner, TimeSpan lease, int batchSize, CancellationToken ct = default)
        {
            var drafts = await db.ScanDrafts.Where(item => item.Status == "Pending")
                .OrderBy(item => item.Id).Take(batchSize).ToListAsync(ct);
            foreach (var draft in drafts)
            {
                draft.Status = "Processing";
                draft.ProcessingClaimOwner = owner;
                draft.ProcessingClaimToken = Guid.NewGuid();
                draft.ProcessingClaimExpiresAtUtc = DateTime.UtcNow.Add(lease);
            }
            await db.SaveChangesAsync(ct);
            return drafts.Select(draft => new ScanProcessingClaim(
                draft.Id, draft.PortfolioId, draft.FilePath, draft.SourceStoredFileId,
                draft.TargetEntityType, owner, draft.ProcessingClaimToken!.Value)).ToList();
        }
    }

    private sealed class TerminalWriteExecutor(RentalCommandDbContext db) : IWriteExecutor
    {
        public async Task<AtomicCommandOutcome<TResult>> ExecuteAsync<TCommand, TResult>(
            string key, TransactionalWrite<TCommand, TResult> write, CancellationToken ct = default)
            where TCommand : notnull, IAtomicCommandData where TResult : notnull
        {
            var command = (ScanProcessingTerminalCommand)(object)write.Request;
            var draft = await db.ScanDrafts.SingleAsync(item => item.Id == command.DraftId, ct);
            if (command.Status == "Reviewing")
            {
                var result = command.ReviewingResult!;
                draft.ExtractedFields = result.ExtractedFields;
                draft.ModelId = result.ModelId;
                draft.TargetEntityType = result.TargetEntityType;
            }
            else draft.FailureReason = command.FailureReason;
            draft.Status = command.Status;
            draft.ProcessingClaimOwner = null;
            draft.ProcessingClaimToken = null;
            draft.ProcessingClaimExpiresAtUtc = null;
            await db.SaveChangesAsync(ct);
            object resultValue = new ScanProcessingTerminalResult(true, draft.Id, draft.Status);
            return new AtomicCommandOutcome<TResult>((TResult)resultValue, AtomicCommandDisposition.Executed, Guid.NewGuid());
        }
    }
}
