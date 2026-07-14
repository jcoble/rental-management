using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using RentalCommand.Api.DTOs;
using RentalCommand.Api.Scanning;
using RentalCommand.Api.Services.Domain;
using RentalCommand.Core;
using RentalCommand.Core.Authorization;
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
    private static readonly Guid SessionId =
        Guid.Parse("33333333-3333-3333-3333-333333333333");

    private readonly SqliteConnection _conn;
    private readonly RentalCommandDbContext _db;
    private readonly RecordingAuditService _audit;
    private readonly ScanService _sut;
    private readonly WorkspaceReadScope _scope;

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
        _scope = CanonicalScanAuthorizationTestData.SeedWorkspaceAdministrator(
            _db, PortfolioId, userId: 3, sessionId: SessionId).Scope;

        _audit        = new RecordingAuditService();

        _sut = new ScanService(
            _db,
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
    public async Task PrepareConfirmationAsync_LeaseTarget_SealsCanonicalExecutedImportChoice()
    {
        var draft = SeedDraft("Reviewing", extractedFields: null, targetEntityType: "LeaseAgreement");
        _db.Users.Add(new ApplicationUser
        {
            Id = 7,
            UserName = "scan-reviewer@example.test",
            NormalizedUserName = "SCAN-REVIEWER@EXAMPLE.TEST",
            Email = "scan-reviewer@example.test",
            NormalizedEmail = "SCAN-REVIEWER@EXAMPLE.TEST",
            DisplayName = "Scan Reviewer",
        });
        _db.Properties.Add(new Property
        {
            Id = 12,
            PortfolioId = PortfolioId,
            Name = "Imported Lease Property",
            AddressLine1 = "12 Test Street",
            City = "Akron",
            State = "OH",
            PostalCode = "44301",
        });
        _db.Units.Add(new Unit
        {
            Id = 34,
            PortfolioId = PortfolioId,
            PropertyId = 12,
            UnitNumber = "A",
        });
        _db.LeaseManagements.Add(new LeaseManagement
        {
            Id = 56,
            PortfolioId = PortfolioId,
            PropertyId = 12,
            UnitId = 34,
            RelationshipNumber = "LM-TEST-56",
            CreatedByUserId = 7,
        });
        _db.TenantAccounts.Add(new TenantAccount
        {
            Id = 78,
            PortfolioId = PortfolioId,
            LeaseManagementId = 56,
            AccountNumber = "TA-TEST-78",
            Currency = "USD",
            CreatedByUserId = 7,
        });
        _db.StoredFiles.Add(new StoredFile
        {
            Id = 44,
            PortfolioId = PortfolioId,
            FileName = "signed-lease.pdf",
            FilePath = "uploads/signed-lease.pdf",
            ContentType = "application/pdf",
            FileSize = 1024,
        });
        draft.SourceStoredFileId = 44;
        draft.SourceContentSha256 = new string('a', 64);
        draft.SourceLabel = "Zillow signed lease import";
        draft.CapturePropertyId = 12;
        draft.CaptureUnitId = 34;
        draft.CaptureLeaseManagementId = 56;
        draft.CaptureTenantAccountId = 78;
        await _db.SaveChangesAsync();

        var result = await _sut.PrepareConfirmationAsync(
            PortfolioId,
            draft.Id,
            userId: 7,
            overridesJson:
                """{"reviewDisposition":"AlreadyFullySigned","tenantName":"Jordan Tenant","startDate":"2026-08-01","endDate":"2027-07-31","monthlyRent":1250,"rentDueDay":1}""");

        result.Outcome.Should().Be(ScanConfirmationPreparationOutcome.Ready);
        result.Command.Should().NotBeNull();
        var command = result.Command!;
        command.SourceStoredFileId.Should().Be(44);
        command.SourceContentSha256.Should().Be(new string('a', 64));
        command.SourceLabel.Should().Be("Zillow signed lease import");
        command.Target.Kind.Should().Be(ScanConfirmationTargetKind.LeaseAgreement);
        command.Target.LeaseAgreement.Should().NotBeNull();
        command.Target.LeaseAgreement!.ReviewDisposition.Should().Be(LeaseScanReviewDisposition.AlreadyFullySigned);
        command.Target.LeaseAgreement.PropertyId.Should().Be(12);
        command.Target.LeaseAgreement.UnitId.Should().Be(34);
        command.Target.LeaseAgreement.LeaseManagementId.Should().Be(56);
        command.Target.LeaseAgreement.TenantAccountId.Should().Be(78);
        command.Target.LeaseAgreement.DocumentTemplateId.Should().BeNull();
    }

    [Fact]
    public async Task PrepareConfirmationAsync_LeaseTarget_RequiresExplicitSignatureDisposition()
    {
        var draft = SeedDraft("Reviewing", extractedFields: null, targetEntityType: "LeaseAgreement");

        var action = () => _sut.PrepareConfirmationAsync(
            PortfolioId, draft.Id, userId: 7, overridesJson: "{}");

        await action.Should().ThrowAsync<ScanConfirmationValidationException>()
            .WithMessage("*AlreadyFullySigned*NeedsSignatures*");
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

        var rejected = await _sut.RejectDraftAsync(
            _scope, draft.Id, userId: 3, reason: "Not a valid receipt");

        rejected.Should().BeTrue();

        _db.ChangeTracker.Clear();
        var rejectedDraft = await _db.ScanDrafts.AsNoTracking()
            .SingleAsync(candidate => candidate.Id == draft.Id);
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

        var rejected = await _sut.RejectDraftAsync(
            _scope, draft.Id, userId: 3, reason: "   ");

        rejected.Should().BeTrue();

        _db.ChangeTracker.Clear();
        var rejectedDraft = await _db.ScanDrafts.AsNoTracking()
            .SingleAsync(candidate => candidate.Id == draft.Id);
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

        public void EnsureAtomicCommand() { }

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

internal sealed class RentalCommandTestDbContext : RentalCommand.TestCommon.SqliteCompatibleRentalCommandDbContext
{
    public RentalCommandTestDbContext(DbContextOptions<RentalCommandDbContext> options) : base(options) { }
}
