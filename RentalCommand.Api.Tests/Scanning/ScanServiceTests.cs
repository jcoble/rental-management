using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using RentalCommand.Api.DTOs;
using RentalCommand.Api.Scanning;
using RentalCommand.Api.Services.Domain;
using RentalCommand.Core;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Interfaces;
using RentalCommand.Core.Scanning;
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
        _audit        = new RecordingAuditService();

        _sut = new ScanService(
            _db,
            _filesMock.Object,
            _audit,
            NullLogger<ScanService>.Instance,
            TimeProvider.System);
    }

    public void Dispose()
    {
        _db.Dispose();
        _conn.Dispose();
    }

    [Fact]
    public async Task PrepareConfirmationAsync_AppliesReviewedOverridesIntoSealedExpenseCommand()
    {
        var draft = SeedDraft(
            "Reviewing",
            """{"vendor_name":{"value":"ACME","confidence":0.9},"total":{"value":"42.50","confidence":0.9}}""");

        var result = await _sut.PrepareConfirmationAsync(
            PortfolioId,
            draft.Id,
            userId: 7,
            overridesJson: """{"vendor_name":"Reviewed Vendor","total":55.25,"is_paid":false,"propertyId":10}""");

        result.Outcome.Should().Be(ScanConfirmationPreparationOutcome.Ready);
        result.Command.Should().NotBeNull();
        var command = result.Command!;
        command.PortfolioId.Should().Be(PortfolioId);
        command.DraftId.Should().Be(draft.Id);
        command.ConfirmedByUserId.Should().Be(7);
        command.ExpectedDraftFingerprint.Should().Be(
            ScanConfirmationDraftFingerprint.Create("Expense", null, draft.ExtractedFields));
        command.Target.Kind.Should().Be(ScanConfirmationTargetKind.Expense);
        command.Target.Expense.Should().NotBeNull();
        command.Target.Expense!.Receipt.VendorName.Should().Be("Reviewed Vendor");
        command.Target.Expense.Receipt.Total.Should().Be(55.25m);
        command.Target.Expense.IsPaid.Should().BeFalse();
        command.Target.Expense.PropertyId.Should().Be(10);
    }

    [Fact]
    public async Task PrepareConfirmationAsync_LeaseTarget_IsTemporarilyUnavailable()
    {
        var draft = SeedDraft("Reviewing", extractedFields: null, targetEntityType: "Lease");

        var result = await _sut.PrepareConfirmationAsync(
            PortfolioId, draft.Id, userId: 7, overridesJson: "{}");

        result.Outcome.Should().Be(ScanConfirmationPreparationOutcome.TemporarilyUnavailable);
        result.Command.Should().BeNull();
        result.Error.Should().Contain("temporarily unavailable");
    }

    [Fact]
    public async Task PrepareConfirmationAsync_InvalidOverrideJson_IsRejectedBeforeAtomicBoundary()
    {
        var draft = SeedDraft("Reviewing", extractedFields: null);

        var action = () => _sut.PrepareConfirmationAsync(
            PortfolioId, draft.Id, userId: 7, overridesJson: "not-json");

        await action.Should().ThrowAsync<ScanConfirmationValidationException>();
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
        rejectedDraft.FailureReason.Should().Be("Not a valid receipt");

        _audit.Calls.Should().HaveCount(1);
        _audit.Calls[0].operation.Should().Be(AuditLogOperation.Rejected);
    }

    [Fact]
    public async Task RejectDraftAsync_ReviewingDraft_WithBlankReason_PreservesExistingFailureReason()
    {
        var draft = SeedDraft("Reviewing", extractedFields: null);
        draft.FailureReason = "Extraction timed out";
        await _db.SaveChangesAsync();

        var rejected = await _sut.RejectDraftAsync(PortfolioId, draft.Id, userId: 3, reason: "   ");

        rejected.Should().BeTrue();

        var rejectedDraft = await _db.ScanDrafts.FindAsync(draft.Id);
        rejectedDraft!.Status.Should().Be("Rejected");
        rejectedDraft.FailureReason.Should().Be("Extraction timed out");
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

    // -------------------------------------------------------------------------
    // Test doubles
    // -------------------------------------------------------------------------

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
}

internal sealed class RentalCommandTestDbContext : RentalCommandDbContext
{
    public RentalCommandTestDbContext(DbContextOptions<RentalCommandDbContext> options) : base(options) { }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        // jsonb is not understood by SQLite — remap those columns to plain text.
        modelBuilder.Entity<ScanDraft>().Property(e => e.ExtractedFields).HasColumnType("TEXT");
        modelBuilder.Entity<Expense>().Property(e => e.ReceiptData).HasColumnType("TEXT");
        modelBuilder.Entity<Payment>().Property(e => e.ExtractedData).HasColumnType("TEXT");
        modelBuilder.Entity<Lease>().Property(e => e.ExtractedData).HasColumnType("TEXT");
        modelBuilder.Entity<WorkOrder>().Property(e => e.ExtractedData).HasColumnType("TEXT");

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
