using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using RentalCommand.Api.DTOs;
using RentalCommand.Api.Scanning;
using RentalCommand.Api.Services.Domain;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Interfaces;
using RentalCommand.Data;

namespace RentalCommand.Api.Tests.Scanning;

/// <summary>
/// Unit tests for <see cref="ScanService"/> using SQLite in-memory (required because
/// <c>ExecuteUpdateAsync</c> is not supported by the EF InMemory provider).
/// Each test opens its own connection so the schema is isolated.
/// </summary>
public class ScanServiceTests : IDisposable
{
    // Shared portfolio id used by all seeds in a test.
    private const int PortfolioId = 1;

    private readonly SqliteConnection _conn;
    private readonly RentalCommandDbContext _db;
    private readonly Mock<IScanFileService> _filesMock;
    private readonly RecordingExpenseService _expenses;
    private readonly Mock<IPaymentService> _paymentsMock;
    private readonly RecordingWorkOrderService _workOrders;
    private readonly RecordingAuditService _audit;
    private readonly ScanService _sut;

    public ScanServiceTests()
    {
        // Keep the connection open for the lifetime of the test so the in-memory DB persists.
        _conn = new SqliteConnection("DataSource=:memory:");
        _conn.Open();

        var options = new DbContextOptionsBuilder<RentalCommandDbContext>()
            .UseSqlite(_conn)
            .Options;

        _db = new RentalCommandTestDbContext(options);
        _db.Database.EnsureCreated();

        // Seed a portfolio row (FK required by ScanDraft + StoredFile).
        _db.Portfolios.Add(new Portfolio
        {
            Id = PortfolioId,
            Name = "Test Portfolio",
            ManagementCompanyName = "Test Co",
            TimeZone = "UTC",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        _db.SaveChanges();

        _filesMock    = new Mock<IScanFileService>(MockBehavior.Strict);
        _expenses     = new RecordingExpenseService();
        _paymentsMock = new Mock<IPaymentService>();
        _workOrders   = new RecordingWorkOrderService();
        _audit        = new RecordingAuditService();

        _sut = new ScanService(
            _db,
            _filesMock.Object,
            _expenses,
            _paymentsMock.Object,
            _workOrders,
            _audit,
            NullLogger<ScanService>.Instance);
    }

    public void Dispose()
    {
        _db.Dispose();
        _conn.Dispose();
    }

    // -------------------------------------------------------------------------
    // Confirm: happy path
    // -------------------------------------------------------------------------

    [Fact]
    public async Task ConfirmAndCreateAsync_ReviewingExpenseDraft_SucceedsAndReKeysStoredFile()
    {
        const string extractedJson =
            """{"vendor_name":{"value":"ACME","confidence":0.9},"amount":{"value":"42.50","confidence":0.8},"transaction_date":{"value":"2026-01-15","confidence":0.95},"category":{"value":"Repairs","confidence":0.7},"notes":{"value":"","confidence":0.0}}""";

        var draft = SeedDraft("Reviewing", extractedJson);
        var file  = SeedStoredFile(draft.FilePath);

        const int fixedExpenseId = 99;
        _expenses.SetupResponse(new ExpenseResponse { Id = fixedExpenseId, PortfolioId = PortfolioId });

        var result = await _sut.ConfirmAndCreateAsync(PortfolioId, draft.Id, userId: 7, overridesJson: "{}");

        // If the result failed, expose the error message to aid debugging.
        result.Error.Should().BeNull("ScanService returned an error: " + result.Error);
        result.Success.Should().BeTrue("Unexpected: " + result.Error);
        result.CreatedEntityId.Should().Be(fixedExpenseId);

        // Draft should be Confirmed. Query the DB directly via raw SQL on the shared connection,
        // bypassing EF's change tracker entirely.
        string? draftStatus;
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = $"SELECT Status FROM ScanDrafts WHERE Id = {draft.Id}";
            draftStatus = (string?)cmd.ExecuteScalar();
        }
        draftStatus.Should().Be("Confirmed");

        string? storedFileEntityType;
        int? storedFileEntityId;
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = $"SELECT EntityType, EntityId FROM StoredFiles WHERE Id = {file.Id}";
            using var reader = cmd.ExecuteReader();
            reader.Read();
            storedFileEntityType = reader.IsDBNull(0) ? null : reader.GetString(0);
            storedFileEntityId   = reader.IsDBNull(1) ? null : (int?)reader.GetInt32(1);
        }
        storedFileEntityType.Should().Be("Expense");
        storedFileEntityId.Should().Be(fixedExpenseId);

        // Audit log should have been called once with Created.
        _audit.Calls.Should().HaveCount(1);
        _audit.Calls[0].operation.Should().Be(AuditLogOperation.Created);
    }

    // -------------------------------------------------------------------------
    // Confirm: overrides win
    // -------------------------------------------------------------------------

    [Fact]
    public async Task ConfirmAndCreateAsync_WithAmountOverride_UsesOverrideAmount()
    {
        const string extractedJson =
            """{"vendor_name":{"value":"Old Vendor","confidence":0.5},"amount":{"value":"10.00","confidence":0.5},"transaction_date":{"value":"2026-01-01","confidence":0.5},"category":{"value":"Other","confidence":0.5},"notes":{"value":"","confidence":0.0}}""";

        var draft = SeedDraft("Reviewing", extractedJson);
        SeedStoredFile(draft.FilePath);

        _expenses.SetupResponse(new ExpenseResponse { Id = 55, PortfolioId = PortfolioId });

        var overrides = """{"amount":99.99}""";
        var result = await _sut.ConfirmAndCreateAsync(PortfolioId, draft.Id, userId: 1, overridesJson: overrides);

        result.Success.Should().BeTrue();

        // The request that reached IExpenseService should have the overridden amount.
        _expenses.LastRequest.Should().NotBeNull();
        _expenses.LastRequest!.Amount.Should().Be(99.99m);
    }

    // -------------------------------------------------------------------------
    // Confirm: snake_case vendor/date overrides are honored (regression guard)
    // The review UI keys edits by the extraction field names (vendor_name,
    // transaction_date); ApplyOverrides must apply them or a corrected vendor/date
    // is silently dropped — the exact trust-breaking bug for this human gate.
    // -------------------------------------------------------------------------

    [Fact]
    public async Task ConfirmAndCreateAsync_WithSnakeCaseVendorAndDateOverrides_AppliesThem()
    {
        const string extractedJson =
            """{"vendor_name":{"value":"Old Vendor","confidence":0.4},"amount":{"value":"10.00","confidence":0.9},"transaction_date":{"value":"2026-01-01","confidence":0.4},"category":{"value":"Other","confidence":0.9},"notes":{"value":"","confidence":0.0}}""";

        var draft = SeedDraft("Reviewing", extractedJson);
        SeedStoredFile(draft.FilePath);
        _expenses.SetupResponse(new ExpenseResponse { Id = 77, PortfolioId = PortfolioId });

        var overrides = """{"vendor_name":"Corrected Vendor","transaction_date":"2026-03-20"}""";
        var result = await _sut.ConfirmAndCreateAsync(PortfolioId, draft.Id, userId: 2, overridesJson: overrides);

        result.Success.Should().BeTrue("Unexpected: " + result.Error);
        _expenses.LastRequest.Should().NotBeNull();
        _expenses.LastRequest!.Description.Should().Be("Corrected Vendor");
        _expenses.LastRequest.IncurredAt.Should().Be(new DateTime(2026, 3, 20, 0, 0, 0, DateTimeKind.Utc));
    }

    // -------------------------------------------------------------------------
    // Confirm: already confirmed draft returns failure (idempotency guard)
    // -------------------------------------------------------------------------

    [Fact]
    public async Task ConfirmAndCreateAsync_AlreadyConfirmed_ReturnsFalse()
    {
        var draft = SeedDraft("Confirmed", extractedFields: null);

        var result = await _sut.ConfirmAndCreateAsync(PortfolioId, draft.Id, userId: 1, overridesJson: "{}");

        result.Success.Should().BeFalse();
        result.Error.Should().NotBeNullOrWhiteSpace();
        _expenses.LastRequest.Should().BeNull(); // expense service was never called
    }

    [Fact]
    public async Task ConfirmAndCreateAsync_ReviewingWorkOrderDraft_CreatesWorkOrder()
    {
        const string extractedJson =
            """{"target_entity_type":{"value":"WorkOrder","confidence":0.9},"property_id":{"value":"10","confidence":0.9},"unit_id":{"value":"20","confidence":0.7},"title":{"value":"Ceiling leak","confidence":0.9},"description":{"value":"Tenant says water is coming through the kitchen ceiling.","confidence":0.85},"category":{"value":"Plumbing","confidence":0.8},"priority":{"value":"Emergency","confidence":0.8},"estimated_cost":{"value":"250.00","confidence":0.4}}""";

        var draft = SeedDraft("Reviewing", extractedJson, targetEntityType: "WorkOrder");
        SeedStoredFile(draft.FilePath);
        _workOrders.SetupResponse(new WorkOrderResponse { Id = 123, PortfolioId = PortfolioId });

        var result = await _sut.ConfirmAndCreateAsync(PortfolioId, draft.Id, userId: 7, overridesJson: "{}");

        result.Success.Should().BeTrue("Unexpected: " + result.Error);
        result.EntityType.Should().Be("WorkOrder");
        result.CreatedEntityId.Should().Be(123);
        _workOrders.LastRequest.Should().NotBeNull();
        _workOrders.LastRequest!.PropertyId.Should().Be(10);
        _workOrders.LastRequest.UnitId.Should().Be(20);
        _workOrders.LastRequest.Title.Should().Be("Ceiling leak");
        _workOrders.LastRequest.Description.Should().Contain("kitchen ceiling");
        _workOrders.LastRequest.Category.Should().Be("Plumbing");
        _workOrders.LastRequest.Priority.Should().Be(WorkOrderPriority.Emergency);
        _workOrders.LastRequest.EstimatedCost.Should().Be(250.00m);

        string? draftStatus;
        using (var cmd = _conn.CreateCommand())
        {
            cmd.CommandText = $"SELECT Status FROM ScanDrafts WHERE Id = {draft.Id}";
            draftStatus = (string?)cmd.ExecuteScalar();
        }
        draftStatus.Should().Be("Confirmed");
        _audit.Calls.Should().Contain(c => c.entityType == "WorkOrder" && c.entityId == 123);
    }

    // -------------------------------------------------------------------------
    // Reject: happy path
    // -------------------------------------------------------------------------

    [Fact]
    public async Task RejectDraftAsync_ReviewingDraft_SetsRejectedAndLogsAudit()
    {
        var draft = SeedDraft("Reviewing", extractedFields: null);

        var rejected = await _sut.RejectDraftAsync(PortfolioId, draft.Id, userId: 3, reason: "Not a valid receipt");

        rejected.Should().BeTrue();

        var rejectedDraft = await _db.ScanDrafts.FindAsync(draft.Id);
        rejectedDraft!.Status.Should().Be("Rejected");
        rejectedDraft.ReviewedBy.Should().Be("3");

        _audit.Calls.Should().HaveCount(1);
        _audit.Calls[0].operation.Should().Be(AuditLogOperation.Rejected);
    }

    // -------------------------------------------------------------------------
    // Seed helpers
    // -------------------------------------------------------------------------

    private ScanDraft SeedDraft(string status, string? extractedFields, string targetEntityType = "Expense")
    {
        var draft = new ScanDraft
        {
            PortfolioId      = PortfolioId,
            FilePath         = $"uploads/test-{Guid.NewGuid():N}.jpg",
            TargetEntityType = targetEntityType,
            Status           = status,
            ExtractedFields  = extractedFields,
            CreatedAt        = DateTime.UtcNow,
        };
        _db.ScanDrafts.Add(draft);
        _db.SaveChanges();
        return draft;
    }

    private StoredFile SeedStoredFile(string filePath)
    {
        var file = new StoredFile
        {
            PortfolioId = PortfolioId,
            FileName    = "receipt.jpg",
            FilePath    = filePath,
            ContentType = "image/jpeg",
            FileSize    = 1024,
            EntityType  = "ScanDraft",
            EntityId    = null,
            UploadedAt  = DateTime.UtcNow,
        };
        _db.StoredFiles.Add(file);
        _db.SaveChanges();
        return file;
    }

    // -------------------------------------------------------------------------
    // Test doubles
    // -------------------------------------------------------------------------

    private sealed class RecordingExpenseService : IExpenseService
    {
        private ExpenseResponse? _response;

        public CreateExpenseRequest? LastRequest { get; private set; }

        public void SetupResponse(ExpenseResponse response) => _response = response;

        public Task<ExpenseResponse?> CreateAsync(int portfolioId, CreateExpenseRequest request, CancellationToken ct = default)
        {
            LastRequest = request;
            return Task.FromResult(_response);
        }

        public Task<IReadOnlyList<ExpenseResponse>> ListAsync(int portfolioId, int? propertyId, ListQuery query, CancellationToken ct = default)
            => throw new NotSupportedException("Not needed for ScanService tests.");

        public Task<ExpenseResponse?> GetAsync(int portfolioId, int id, CancellationToken ct = default)
            => throw new NotSupportedException("Not needed for ScanService tests.");

        public Task<ExpenseResponse?> UpdateAsync(int portfolioId, int id, UpdateExpenseRequest request, CancellationToken ct = default)
            => throw new NotSupportedException("Not needed for ScanService tests.");

        public Task<bool> DeleteAsync(int portfolioId, int id, CancellationToken ct = default)
            => throw new NotSupportedException("Not needed for ScanService tests.");
    }

    private sealed class RecordingAuditService : IAuditTrailService
    {
        public List<(int portfolioId, string entityType, int entityId, AuditLogOperation operation)> Calls { get; } = [];

        public Task LogAsync(
            int portfolioId, string entityType, int entityId, AuditLogOperation operation,
            int? userId = null, string? actorLabel = null, string? oldValues = null,
            string? newValues = null, string? changeReason = null, string? ipAddress = null,
            CancellationToken ct = default)
        {
            Calls.Add((portfolioId, entityType, entityId, operation));
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingWorkOrderService : IWorkOrderService
    {
        private WorkOrderResponse? _response;

        public CreateWorkOrderRequest? LastRequest { get; private set; }

        public void SetupResponse(WorkOrderResponse response) => _response = response;

        public Task<WorkOrderResponse?> CreateAsync(int portfolioId, CreateWorkOrderRequest request, CancellationToken ct = default)
        {
            LastRequest = request;
            return Task.FromResult(_response);
        }

        public Task<IReadOnlyList<WorkOrderResponse>> ListAsync(int portfolioId, int? propertyId, int? vendorId, ListQuery query, CancellationToken ct = default)
            => throw new NotSupportedException("Not needed for ScanService tests.");

        public Task<WorkOrderResponse?> GetAsync(int portfolioId, int id, CancellationToken ct = default)
            => throw new NotSupportedException("Not needed for ScanService tests.");

        public Task<WorkOrderResponse?> UpdateAsync(int portfolioId, int id, UpdateWorkOrderRequest request, CancellationToken ct = default)
            => throw new NotSupportedException("Not needed for ScanService tests.");

        public Task<bool> DeleteAsync(int portfolioId, int id, CancellationToken ct = default)
            => throw new NotSupportedException("Not needed for ScanService tests.");
    }
}

/// <summary>
/// Derived DbContext that overrides Postgres-specific configurations (jsonb column type,
/// check constraints) so the schema is valid on SQLite.
/// </summary>
internal sealed class RentalCommandTestDbContext : RentalCommandDbContext
{
    public RentalCommandTestDbContext(DbContextOptions<RentalCommandDbContext> options) : base(options) { }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        // jsonb is not understood by SQLite — remap those columns to plain text.
        modelBuilder.Entity<ScanDraft>().Property(e => e.ExtractedFields).HasColumnType("TEXT");

        // Remove Postgres-specific jsonb from AuditLog, OutboxMessage, QueuedJob.
        modelBuilder.Entity<AuditLog>()
            .Property(e => e.OldValues).HasColumnType("TEXT");
        modelBuilder.Entity<AuditLog>()
            .Property(e => e.NewValues).HasColumnType("TEXT");
        modelBuilder.Entity<OutboxMessage>()
            .Property(e => e.Payload).HasColumnType("TEXT");
        modelBuilder.Entity<QueuedJob>()
            .Property(e => e.Payload).HasColumnType("TEXT");

        // Remove check constraints that SQLite cannot execute (Lease StartDate < EndDate, RentDueDay).
        // EF Core lets us replace the table builder to drop all constraints.
        modelBuilder.Entity<Lease>().ToTable("Leases");
    }
}
