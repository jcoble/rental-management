using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using RentalCommand.Core.Atomic;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Scanning;
using RentalCommand.Data.Atomic;
using RentalCommand.Data.Scanning;

namespace RentalCommand.Data.Tests.Atomic;

public sealed class AtomicScanConfirmationPersistenceTests
{
    [Fact]
    public void RejectAuthorizedAsync_BindsTheExactTrackedUpdateOperation()
    {
        var source = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "RentalCommand.Data",
            "Scanning",
            "AtomicScanConfirmationPersistence.cs"));
        var rejectionMethod = source[
            source.IndexOf("public async Task<bool> RejectAuthorizedAsync", StringComparison.Ordinal)..];
        rejectionMethod = rejectionMethod[
            ..rejectionMethod.IndexOf("private static string? Truncate", StringComparison.Ordinal)];

        rejectionMethod.Should().Contain("AuditLogOperation.Updated");
        rejectionMethod.Should().NotContain("AuditLogOperation.Rejected");
    }

    [Fact]
    public void Fingerprint_IsStableAcrossPostgresJsonbNormalization()
    {
        var original = "{\"z\":1,\"nested\":{\"b\":2,\"a\":[3,{\"y\":true,\"x\":null}]}}";
        var normalized = "{ \"nested\": { \"a\": [3, { \"x\": null, \"y\": true }], \"b\": 2 }, \"z\": 1 }";

        ScanConfirmationDraftFingerprint.Create("Expense", 12, original).Should().Be(
            ScanConfirmationDraftFingerprint.Create("Expense", 12, normalized));
    }

    [Fact]
    public async Task TryClaimAsync_ExactPreparedSnapshot_ClaimsReviewingDraft()
    {
        await using var db = CreateContext();
        var draft = SeedDraft(db);
        var expected = ScanConfirmationDraftFingerprint.Create(
            draft.TargetEntityType, draft.SourceStoredFileId, draft.ExtractedFields);
        db.ChangeTracker.Clear();

        var (persistence, auditScope, _) = CreatePersistence(db);
        using var attempt = auditScope.BeginAttempt(
            new AtomicCommandIdentity("scan.confirm", "exact"), Guid.NewGuid(), db);

        var claim = await persistence.TryClaimAsync(
            draft.PortfolioId, draft.Id, "Expense", expected, confirmedByUserId: 7);

        claim.Outcome.Should().Be(AtomicScanDraftClaimOutcome.Claimed);
        db.ChangeTracker.Entries<ScanDraft>().Single().Entity.Status.Should().Be("Confirming");
    }

    [Theory]
    [InlineData("extraction")]
    [InlineData("source")]
    [InlineData("target")]
    public async Task TryClaimAsync_ChangedPreparedFact_ReturnsStaleWithoutBusinessEffects(string changedFact)
    {
        await using var db = CreateContext();
        var draft = SeedDraft(db);
        var expected = ScanConfirmationDraftFingerprint.Create(
            draft.TargetEntityType, draft.SourceStoredFileId, draft.ExtractedFields);

        switch (changedFact)
        {
            case "extraction":
                draft.ExtractedFields = "{\"total\":{\"value\":99}}";
                break;
            case "source":
                draft.SourceStoredFileId = 501;
                break;
            case "target":
                draft.TargetEntityType = "Payment";
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(changedFact));
        }

        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        var (persistence, auditScope, _) = CreatePersistence(db);
        using var attempt = auditScope.BeginAttempt(
            new AtomicCommandIdentity("scan.confirm", $"stale-{changedFact}"), Guid.NewGuid(), db);

        var claim = await persistence.TryClaimAsync(
            draft.PortfolioId, draft.Id, "Expense", expected, confirmedByUserId: 7);

        claim.Outcome.Should().Be(AtomicScanDraftClaimOutcome.StalePreparation);
        var unchanged = db.ChangeTracker.Entries<ScanDraft>().Single();
        unchanged.Entity.Status.Should().Be("Reviewing");
        unchanged.State.Should().Be(EntityState.Unchanged);
        (await db.Expenses.CountAsync()).Should().Be(0);
        (await db.StoredFiles.CountAsync()).Should().Be(0);
        (await db.AtomicAuditLogs.CountAsync()).Should().Be(0);
        (await db.AtomicCommandReceipts.CountAsync()).Should().Be(0);
    }

    private static TestDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<RentalCommandDbContext>()
            .UseInMemoryDatabase($"scan-confirm-{Guid.NewGuid():N}")
            .Options;
        return new TestDbContext(options);
    }

    private static ScanDraft SeedDraft(RentalCommandDbContext db)
    {
        db.Portfolios.Add(new Portfolio
        {
            Id = 42,
            Name = "Scan confirmation test",
            ManagementCompanyName = "Test management",
            TimeZone = "UTC",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        var draft = new ScanDraft
        {
            Id = 17,
            PortfolioId = 42,
            TargetEntityType = "Expense",
            Status = "Reviewing",
            ExtractedFields = "{\"total\":{\"value\":42}}",
            CreatedAt = DateTime.UtcNow,
        };
        db.ScanDrafts.Add(draft);
        db.SaveChanges();
        return draft;
    }

    private static (
        AtomicScanConfirmationPersistence Persistence,
        AtomicAuditScope AuditScope,
        IAtomicCommandContext Context)
        CreatePersistence(RentalCommandDbContext db)
    {
        var auditScope = new AtomicAuditScope(
            TimeProvider.System);
        var context = new AtomicCommandContext(db, auditScope, TimeProvider.System);
        return (new AtomicScanConfirmationPersistence(db, auditScope, context), auditScope, context);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "RentalCommand.sln")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName
            ?? throw new DirectoryNotFoundException("Could not locate the repository root.");
    }

    private sealed class TestDbContext(DbContextOptions<RentalCommandDbContext> options)
        : RentalCommandDbContext(options);
}
