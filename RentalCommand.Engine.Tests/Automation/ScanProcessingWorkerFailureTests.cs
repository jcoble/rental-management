using System.Text.Json;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using RentalCommand.Core.Configuration;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Interfaces;
using RentalCommand.Data;
using RentalCommand.Data.Scanning;
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
    private readonly StubUsageEvidenceRecorder _usage = new();
    private readonly StubWorkspaceCredentialResolver _credentials = new();
    private readonly AssistantConfig _assistant = new();
    private readonly StubHostEnvironment _environment = new();

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
        services.AddScoped<IScanProcessingClaimStore, TestScanProcessingClaimStore>();
        services.AddSingleton<ILlmProvider>(_llm);
        services.AddSingleton<IWorkspaceLlmExtractionProvider>(_llm);
        services.AddSingleton<IWorkspaceLlmCredentialResolver>(_credentials);
        services.AddSingleton<ILlmUsageEvidenceRecorder>(_usage);
        services.AddSingleton<IOptions<AssistantConfig>>(Options.Create(_assistant));
        services.AddSingleton<IHostEnvironment>(_environment);
        services.AddSingleton<IFileStorage, StubFileStorage>();
        services.AddSingleton<IDataUpdateService, StubDataUpdateService>();
        services.AddSingleton(TimeProvider.System);
        _provider = services.BuildServiceProvider();

        using var scope = _provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RentalCommandDbContext>();
        db.Database.EnsureCreated();
        db.Database.ExecuteSqlRaw("""
            CREATE VIEW "vw_lease_management_lifecycle" AS
            SELECT management."PortfolioId" AS "PortfolioId",
                   management."Id" AS "LeaseManagementId",
                   'Occupied' AS "Lifecycle",
                   agreement."Id" AS "CurrentAgreementId",
                   party."TenantId" AS "CurrentPrimaryTenantId",
                   trim(tenant."FirstName" || ' ' || tenant."LastName") AS "CurrentPrimaryTenantName"
            FROM "LeaseManagements" AS management
            LEFT JOIN "LeaseManagementParties" AS party
              ON party."PortfolioId" = management."PortfolioId"
             AND party."LeaseManagementId" = management."Id"
             AND party."Role" = 'PrimaryTenant'
             AND party."EffectiveThrough" IS NULL
            LEFT JOIN "Tenants" AS tenant
              ON tenant."PortfolioId" = party."PortfolioId"
             AND tenant."Id" = party."TenantId"
            LEFT JOIN "LeaseAgreements" AS agreement
              ON agreement."PortfolioId" = management."PortfolioId"
             AND agreement."LeaseManagementId" = management."Id"
             AND agreement."VoidedAtUtc" IS NULL
             AND agreement."DraftCanceledAtUtc" IS NULL
            WHERE management."CanceledAtUtc" IS NULL
              AND management."AccountClosedAtUtc" IS NULL
            """);
        db.Portfolios.Add(new Portfolio
        {
            Id = 1,
            Name = "Test Portfolio",
            ManagementCompanyName = "Test Co",
            TimeZone = "UTC",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        db.Users.Add(new ApplicationUser
        {
            Id = 1,
            UserName = "scan-worker@example.test",
            NormalizedUserName = "SCAN-WORKER@EXAMPLE.TEST",
            Email = "scan-worker@example.test",
            NormalizedEmail = "SCAN-WORKER@EXAMPLE.TEST",
            DisplayName = "Scan Worker",
            CreatedAt = DateTime.UtcNow,
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

    [Theory]
    [InlineData("Development", "claude-cli", true)]
    [InlineData("Development", "anthropic", false)]
    [InlineData("Production", "claude-cli", false)]
    public void ShouldUseDevelopmentSubscriptionProvider_RequiresDevelopmentAndClaudeCli(
        string environmentName,
        string provider,
        bool expected)
    {
        ScanProcessingWorker.ShouldUseDevelopmentSubscriptionProvider(
                environmentName,
                provider)
            .Should()
            .Be(expected);
    }

    [Theory]
    [InlineData("Development", "claude-cli", 210)]
    [InlineData("Development", "anthropic", 90)]
    [InlineData("Production", "claude-cli", 90)]
    public void ResolveStepTimeout_AllowsClaudeCliToFinishBeforeItsOwnTimeout(
        string environmentName,
        string provider,
        int expectedSeconds)
    {
        ScanProcessingWorker.ResolveStepTimeout(environmentName, provider)
            .Should()
            .Be(TimeSpan.FromSeconds(expectedSeconds));
    }

    [Theory]
    [InlineData("Development", "claude-cli", 240)]
    [InlineData("Development", "anthropic", 120)]
    [InlineData("Production", "claude-cli", 120)]
    public void ResolveClaimLease_OutlivesTheSelectedStepTimeout(
        string environmentName,
        string provider,
        int expectedSeconds)
    {
        ScanProcessingWorker.ResolveClaimLease(environmentName, provider)
            .Should()
            .Be(TimeSpan.FromSeconds(expectedSeconds));
    }

    [Fact]
    public async Task Cycle_DevelopmentClaudeCli_BypassesWorkspaceApiCredential()
    {
        _environment.EnvironmentName = Environments.Development;
        _assistant.Provider = "claude-cli";
        _assistant.ModelId = "sonnet";
        _credentials.Credential = null;
        var draftId = SeedPendingDraft();
        _llm.Result = Extracted(("total", "42.00"));

        await RunCycleAsync();

        var draft = await ReloadAsync(draftId);
        draft.Status.Should().Be("Reviewing");
        draft.ModelId.Should().Be("test-model");
        _llm.LastCredential.Should().BeNull();
        _usage.Receipts.Should().ContainSingle(receipt =>
            receipt.Provider == "claude-cli" &&
            receipt.EstimatedCostUsd == 0m);
    }

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
        _llm.Result.InputTokens = 120;
        _llm.Result.OutputTokens = 30;
        _llm.Result.TokensUsed = 150;

        await RunCycleAsync();

        var draft = await ReloadAsync(draftId);
        draft.Status.Should().Be("Reviewing");
        draft.FailureReason.Should().BeNull();
        draft.ExtractedFields.Should().NotBeNull();
        draft.ExtractedFields!.Should().Contain("Apex Plumbing");
        _llm.LastCredential.Should().NotBeNull();
        _llm.LastCredential!.PortfolioId.Should().Be(1);
        _llm.LastCredential.ApiKey.Should().Be("workspace-test-key");
        _usage.Receipts.Should().ContainSingle(receipt =>
            receipt.PortfolioId == 1 &&
            receipt.Provider == "test" &&
            receipt.ModelId == "test-model" &&
            receipt.Feature == "scan.extraction" &&
            receipt.InputUnits == 120 &&
            receipt.OutputUnits == 30 &&
            receipt.LatencyMilliseconds >= 0 &&
            receipt.EstimatedCostUsd > 0);
    }

    [Fact]
    public async Task Cycle_MissingWorkspaceCredential_MarksFailedWithoutSharedFallback()
    {
        var draftId = SeedPendingDraft();
        _credentials.Credential = null;

        await RunCycleAsync();

        var draft = await ReloadAsync(draftId);
        draft.Status.Should().Be("Failed");
        draft.FailureReason.Should().Contain("configure a workspace OpenAI or Anthropic credential");
        _llm.ExtractCalls.Should().Be(0);
        _usage.Receipts.Should().BeEmpty();
    }

    [Fact]
    public async Task Cycle_ReceiptWithIncompleteLineItems_RetriesOnceWithTargetedRepair()
    {
        var draftId = SeedPendingDraft(targetEntityType: "Expense");
        _llm.Results.Enqueue(Extracted(
            ("vendor_name", "Apex Plumbing"),
            ("total", "286.45"),
            ("document_kind", "Receipt"),
            ("line_items", """[{"description":"Washer hose"},{"description":"Pipe tape"}]""")));
        _llm.Results.Enqueue(Extracted(
            ("vendor_name", "Apex Plumbing"),
            ("total", "286.45"),
            ("document_kind", "Receipt"),
            ("line_items", """[{"description":"Washer hose","quantity":1,"unit_price":250.00,"amount":250.00},{"description":"Pipe tape","quantity":1,"unit_price":36.45,"amount":36.45}]""")));

        await RunCycleAsync();

        var draft = await ReloadAsync(draftId);
        draft.Status.Should().Be("Reviewing");
        using (var doc = JsonDocument.Parse(draft.ExtractedFields!))
        {
            var lineItemsJson = doc.RootElement
                .GetProperty("line_items")
                .GetProperty("value")
                .GetString();
            lineItemsJson.Should().Contain("\"amount\":250.00");
        }
        _llm.ExtractCalls.Should().Be(2);
        _usage.Receipts.Should().HaveCount(2);
        _llm.Instructions.Should().HaveCount(2);
        _llm.Instructions[1].Should().Contain("line item");
        _llm.Instructions[1].Should().Contain("amount");
    }

    [Fact]
    public async Task Cycle_ReceiptRepairProviderError_KeepsInitialReviewingDraft()
    {
        var draftId = SeedPendingDraft(targetEntityType: "Expense");
        _llm.Results.Enqueue(Extracted(
            ("vendor_name", "Apex Plumbing"),
            ("total", "286.45"),
            ("document_kind", "Receipt"),
            ("line_items", """[{"description":"Washer hose"},{"description":"Pipe tape"}]""")));
        _llm.Results.Enqueue(new InvalidOperationException("repair provider unavailable"));

        await RunCycleAsync();

        var draft = await ReloadAsync(draftId);
        draft.Status.Should().Be("Reviewing");
        draft.FailureReason.Should().BeNull();
        draft.ExtractedFields.Should().Contain("Washer hose");
        _llm.ExtractCalls.Should().Be(2);
        _usage.Receipts.Should().HaveCount(2);
        _usage.Receipts[1].Feature.Should().Be("scan.quality-repair");
        _usage.Receipts[1].InputUnits.Should().Be(0);
        _usage.Receipts[1].OutputUnits.Should().Be(0);
    }

    [Fact]
    public async Task Cycle_LeaseWithZeroBedBath_BlanksInvalidUnitCountsBeforeReview()
    {
        var draftId = SeedPendingDraft(targetEntityType: "LeaseAgreement");
        _llm.Result = Extracted(
            ("target_entity_type", "LeaseAgreement"),
            ("tenant_name", "Dana Brooks"),
            ("property_address", "742 Evergreen St"),
            ("unit_number", "3C"),
            ("unit_bedrooms", "0"),
            ("unit_bathrooms", "0"),
            ("start_date", "2026-02-01"),
            ("end_date", "2027-01-31"),
            ("monthly_rent", "1325.00"));

        await RunCycleAsync();

        var draft = await ReloadAsync(draftId);
        draft.Status.Should().Be("Reviewing");
        draft.ExtractedFields.Should().Contain("\"unit_bedrooms\":{\"value\":\"\",\"confidence\":0}");
        draft.ExtractedFields.Should().Contain("\"unit_bathrooms\":{\"value\":\"\",\"confidence\":0}");
    }

    [Fact]
    public async Task Cycle_WorkOrderExtraction_GroundingContextIncludesCurrentLeaseRelationship()
    {
        using (var scope = _provider.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<RentalCommandDbContext>();
            var now = DateTime.UtcNow;

            db.Properties.Add(new Property
            {
                Id = 10,
                PortfolioId = 1,
                Name = "Cedar Point Flats",
                AddressLine1 = "742 Evergreen St",
                City = "Columbus",
                State = "OH",
                PostalCode = "43200",
                CreatedAt = now,
                UpdatedAt = now,
            });
            db.Units.Add(new Unit
            {
                Id = 20,
                PortfolioId = 1,
                PropertyId = 10,
                UnitNumber = "1A",
                Bedrooms = 2,
                Bathrooms = 1,
                MarketRent = 1125m,
                CreatedAt = now,
                UpdatedAt = now,
            });
            db.Tenants.Add(new Tenant
            {
                Id = 30,
                PortfolioId = 1,
                FirstName = "Avery",
                LastName = "Ellis",
                CreatedAt = now,
                UpdatedAt = now,
            });
            db.LeaseManagements.Add(new LeaseManagement
            {
                Id = 40,
                PublicId = Guid.NewGuid(),
                PortfolioId = 1,
                PropertyId = 10,
                UnitId = 20,
                RelationshipNumber = "QA-2026-001-1A",
                PossessionGivenAtUtc = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                CreatedAtUtc = now,
                UpdatedAtUtc = now,
                CreatedByUserId = 1,
                RowVersion = Guid.NewGuid(),
            });
            db.TenantAccounts.Add(new TenantAccount
            {
                Id = 41,
                PublicId = Guid.NewGuid(),
                PortfolioId = 1,
                LeaseManagementId = 40,
                AccountNumber = "TA-QA-2026-001-1A",
                Currency = "USD",
                OpenedAtUtc = now,
                CreatedAtUtc = now,
                CreatedByUserId = 1,
            });
            db.LeaseManagementParties.Add(new LeaseManagementParty
            {
                Id = 42,
                PortfolioId = 1,
                LeaseManagementId = 40,
                TenantId = 30,
                Role = Core.Enums.LeaseManagementPartyRole.PrimaryTenant,
                EffectiveFrom = new DateOnly(2026, 1, 1),
                ChangeReason = "Canonical grounding fixture",
                CreatedAtUtc = now,
                CreatedByUserId = 1,
            });
            db.LeaseAgreements.Add(new LeaseAgreement
            {
                Id = 43,
                PublicId = Guid.NewGuid(),
                PortfolioId = 1,
                LeaseManagementId = 40,
                VersionNumber = 1,
                AgreementNumber = "QA-2026-001-1A",
                ChangeType = Core.Enums.LeaseAgreementChangeType.Initial,
                TermType = Core.Enums.LeaseAgreementTermType.FixedTerm,
                TermStartOn = new DateOnly(2026, 1, 1),
                TermEndOn = new DateOnly(2027, 1, 1),
                GoverningFromOn = new DateOnly(2026, 1, 1),
                BaseRentAmount = 1125m,
                RentDueDay = 1,
                SecurityDepositObligation = 1125m,
                LateFeeAmount = 0m,
                GracePeriodDays = 0,
                Currency = "USD",
                TermsSchemaVersion = 1,
                TermsPayload = "{}",
                DocumentSourceVersion = LegalDocumentSourceVersionTestData.BuiltIn(
                    1, 1, now),
                CreatedAtUtc = now,
                UpdatedAtUtc = now,
                CreatedByUserId = 1,
            });
            db.SaveChanges();
        }

        SeedPendingDraft(targetEntityType: "WorkOrder");
        _llm.Result = Extracted(
            ("target_entity_type", "WorkOrder"),
            ("title", "Front door lock sticks"),
            ("description", "Tenant Avery Ellis reports the lock sticks on lease QA-2026-001-1A."));

        await RunCycleAsync();

        _llm.LastGroundingContext.Should().NotBeNullOrWhiteSpace();
        _llm.LastGroundingContext.Should().Contain("\"leaseManagements\"");
        _llm.LastGroundingContext.Should().Contain("\"id\":40");
        _llm.LastGroundingContext.Should().Contain("\"leaseAgreementId\":43");
        _llm.LastGroundingContext.Should().Contain("\"relationshipNumber\":\"QA-2026-001-1A\"");
        _llm.LastGroundingContext.Should().Contain("QA-2026-001-1A");
        _llm.LastGroundingContext.Should().Contain("\"unitId\":20");
        _llm.LastGroundingContext.Should().Contain("\"tenantId\":30");
    }

    // -----------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------

    private async Task RunCycleAsync()
    {
        var worker = new TestableScanProcessingWorker(_provider);
        await worker.RunOneCycleAsync(_provider, CancellationToken.None);
    }

    private int SeedPendingDraft(string targetEntityType = "Expense")
    {
        using var scope = _provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RentalCommandDbContext>();
        var draft = new ScanDraft
        {
            PortfolioId = 1,
            FilePath = "scans/test-key",
            TargetEntityType = targetEntityType,
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

    private sealed class StubLlmProvider :
        ILlmProvider,
        IWorkspaceLlmExtractionProvider
    {
        public string ProviderKey => "test";
        public ExtractedFields Result { get; set; } = new();
        public Queue<object> Results { get; } = new();
        public WorkspaceLlmRuntimeCredential? LastCredential { get; private set; }
        public string? LastGroundingContext { get; private set; }
        public List<string> Instructions { get; } = new();
        public int ExtractCalls { get; private set; }

        public Task<ExtractedFields> ExtractAsync(
            byte[] documentBytes, string contentType, string instructions,
            IReadOnlyList<ExtractionFieldSpec> fields, string? groundingContext = null,
            CancellationToken ct = default)
        {
            ExtractCalls++;
            Instructions.Add(instructions);
            LastGroundingContext = groundingContext;
            if (Results.Count == 0)
                return Task.FromResult(Result);

            var next = Results.Dequeue();
            if (next is Exception ex)
                throw ex;

            return Task.FromResult((ExtractedFields)next);
        }

        public Task<ExtractedFields> ExtractWorkspaceAsync(
            WorkspaceLlmRuntimeCredential credential,
            byte[] documentBytes,
            string contentType,
            string instructions,
            IReadOnlyList<ExtractionFieldSpec> fields,
            string? groundingContext = null,
            CancellationToken ct = default)
        {
            LastCredential = credential;
            return ExtractAsync(
                documentBytes,
                contentType,
                instructions,
                fields,
                groundingContext,
                ct);
        }

        public Task<string> ChatAsync(string prompt, CancellationToken ct = default)
            => Task.FromResult(string.Empty);

        public Task<LlmToolResult> ChatWithToolsAsync(
            string systemPrompt, IReadOnlyList<LlmChatMessage> messages,
            IReadOnlyList<LlmToolSpec> tools, CancellationToken ct = default)
            => Task.FromResult(new LlmToolResult("end", null, Array.Empty<LlmToolCall>(), 0, 0, "test-model"));
    }

    private sealed class StubWorkspaceCredentialResolver : IWorkspaceLlmCredentialResolver
    {
        public WorkspaceLlmRuntimeCredential? Credential { get; set; } =
            new(1, "test", "test-model", "workspace-test-key");

        public Task<WorkspaceLlmRuntimeCredential?> ResolveActiveAsync(
            int portfolioId,
            CancellationToken ct = default) =>
            Task.FromResult<WorkspaceLlmRuntimeCredential?>(
                Credential is null
                    ? null
                    : Credential with { PortfolioId = portfolioId });
    }

    private sealed class StubHostEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Production;
        public string ApplicationName { get; set; } = "RentalCommand.Engine.Tests";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = null!;
    }

    private sealed class StubUsageEvidenceRecorder : ILlmUsageEvidenceRecorder
    {
        public List<UsageReceipt> Receipts { get; } = [];

        public Task RecordUsageAsync(
            int portfolioId,
            string provider,
            string modelId,
            string feature,
            int latencyMilliseconds,
            int inputUnits,
            int outputUnits,
            decimal estimatedCostUsd,
            CancellationToken ct = default)
        {
            Receipts.Add(new UsageReceipt(
                portfolioId,
                provider,
                modelId,
                feature,
                latencyMilliseconds,
                inputUnits,
                outputUnits,
                estimatedCostUsd));
            return Task.CompletedTask;
        }
    }

    private sealed record UsageReceipt(
        int PortfolioId,
        string Provider,
        string ModelId,
        string Feature,
        int LatencyMilliseconds,
        int InputUnits,
        int OutputUnits,
        decimal EstimatedCostUsd);

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

    /// <summary>
    /// SQLite-compatible test boundary for the worker's PostgreSQL claim store. Candidate filtering,
    /// ordering, and paging remain database-side; the fake only substitutes the PostgreSQL-specific
    /// SKIP LOCKED statement that SQLite cannot execute. Completion remains fenced by owner and token so
    /// these tests exercise the worker's real ownership contract.
    /// </summary>
    private sealed class TestScanProcessingClaimStore : IScanProcessingClaimStore
    {
        private readonly RentalCommandDbContext _db;

        public TestScanProcessingClaimStore(RentalCommandDbContext db) => _db = db;

        public async Task<IReadOnlyList<ScanProcessingClaim>> ClaimAsync(
            string claimOwner,
            TimeSpan leaseDuration,
            int batchSize,
            CancellationToken ct = default)
        {
            var nowUtc = DateTime.UtcNow;
            var candidates = await _db.ScanDrafts
                .Where(d => d.Status == "Pending")
                .OrderBy(d => d.CreatedAt)
                .ThenBy(d => d.Id)
                .Take(batchSize)
                .ToListAsync(ct);

            foreach (var draft in candidates)
            {
                draft.Status = "Processing";
                draft.ProcessingClaimOwner = claimOwner;
                draft.ProcessingClaimToken = Guid.NewGuid();
                draft.ProcessingClaimExpiresAtUtc = nowUtc.Add(leaseDuration);
                draft.ProcessingAttemptCount++;
                draft.ProcessingLastAttemptAtUtc = nowUtc;
                draft.FailureReason = null;
            }

            await _db.SaveChangesAsync(ct);

            return candidates.Select(d => new ScanProcessingClaim(
                d.Id,
                d.PortfolioId,
                d.FilePath,
                d.SourceStoredFileId,
                d.TargetEntityType,
                d.ProcessingClaimOwner!,
                d.ProcessingClaimToken!.Value)).ToList();
        }

        public async Task<int> MarkReviewingAsync(
            int id,
            string claimOwner,
            Guid claimToken,
            ScanProcessingResult result,
            CancellationToken ct = default)
        {
            var draft = await Owned(id, claimOwner, claimToken).SingleOrDefaultAsync(ct);
            if (draft is null) return 0;

            draft.ExtractedFields = result.ExtractedFields;
            draft.FailureReason = null;
            draft.ModelId = result.ModelId;
            draft.TokensUsed = result.TokensUsed;
            draft.CostUsd = result.CostUsd;
            draft.TargetEntityType = result.TargetEntityType;
            draft.Status = "Reviewing";
            draft.ReviewedAt = result.ReviewedAtUtc;
            ClearClaim(draft);
            await _db.SaveChangesAsync(ct);
            return 1;
        }

        public async Task<int> MarkFailedAsync(
            int id,
            string claimOwner,
            Guid claimToken,
            DateTime reviewedAtUtc,
            string? failureReason,
            CancellationToken ct = default)
        {
            var draft = await Owned(id, claimOwner, claimToken).SingleOrDefaultAsync(ct);
            if (draft is null) return 0;

            draft.Status = "Failed";
            draft.FailureReason = failureReason;
            draft.ReviewedAt = reviewedAtUtc;
            ClearClaim(draft);
            await _db.SaveChangesAsync(ct);
            return 1;
        }

        private IQueryable<ScanDraft> Owned(int id, string claimOwner, Guid claimToken) =>
            _db.ScanDrafts.Where(d =>
                d.Id == id &&
                d.Status == "Processing" &&
                d.ProcessingClaimOwner == claimOwner &&
                d.ProcessingClaimToken == claimToken);

        private static void ClearClaim(ScanDraft draft)
        {
            draft.ProcessingClaimOwner = null;
            draft.ProcessingClaimToken = null;
            draft.ProcessingClaimExpiresAtUtc = null;
        }
    }
}

/// <summary>
/// SQLite-friendly DbContext for these tests: maps the Postgres jsonb columns this worker touches to
/// TEXT and drops Postgres-only table configuration the worker's queries don't need, mirroring
/// <see cref="SqliteTestContext"/>'s approach.
/// </summary>
internal sealed class ScanTestDbContext : SqliteCompatibleRentalCommandDbContext
{
    public ScanTestDbContext(DbContextOptions<RentalCommandDbContext> options) : base(options) { }
}
