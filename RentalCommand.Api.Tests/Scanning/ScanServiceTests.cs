using System.Text.RegularExpressions;
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
using RentalCommand.Core.Atomic;
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

        _sut = new ScanService(
            _db,
            new ScanRejectAtomicUnitOfWork(_db),
            NullLogger<ScanService>.Instance,
            TimeProvider.System);
    }

    public void Dispose()
    {
        _db.Dispose();
        _conn.Dispose();
    }

    [Fact]
    public void CandidateQueries_KeepAuthorizationSearchSortAndPagingInSql()
    {
        ScanService.TechnicianCandidateCapabilityKeys.Should().BeEquivalentTo([
            CapabilityKeys.AssignedWorkUpdate,
            CapabilityKeys.WorkManage,
        ]);
        ScanService.TargetCandidateCapabilityKeys.Should().BeEquivalentTo([
            CapabilityKeys.RentalsManage,
            CapabilityKeys.WorkManage,
            CapabilityKeys.MoneyPaymentsManage,
            CapabilityKeys.MoneyExpensesManage,
            CapabilityKeys.LeasingApplicationsManage,
            CapabilityKeys.LeasingAgreementsPrepare,
        ]);

        using var translationDb = new RentalCommandDbContext(
            new DbContextOptionsBuilder<RentalCommandDbContext>()
                .UseNpgsql(
                    "Host=localhost;Database=translation_only;Username=translation_only;Password=translation_only")
                .Options);
        var translationService = new ScanService(
            translationDb,
            new ScanRejectAtomicUnitOfWork(translationDb),
            NullLogger<ScanService>.Instance,
            TimeProvider.System);

        var technicianSql = NormalizeSql(translationService.BuildTechnicianCandidateQuery(
                _scope,
                "plumbing",
                skip: 20,
                take: 20)
            .ToQueryString());
        var targetSql = NormalizeSql(translationService.BuildTargetCandidateQuery(
                _scope,
                "main",
                skip: 20,
                take: 20)
            .ToQueryString());

        AssertPropertyAuthorizationShape(technicianSql, "w1");
        AssertSqlContains(
            technicianSql,
            """
            ) AS s8 ON m2."WorkspaceMembershipId" = s8."Id"
                AND m2."PortfolioId" = s8."PortfolioId"
            INNER JOIN "RoleProfiles" AS r1 ON m2."RoleProfileId" = r1."Id"
            """);
        AssertSqlContains(
            technicianSql,
            """
            m2."ScopeKind" = 'AssignedWorkOrders'
                AND m2."Status" = 'Active'
                AND m2."SuspendedAtUtc" IS NULL
                AND m2."RevokedAtUtc" IS NULL
                AND m2."EffectiveFromUtc" <= @utcNow
                AND (m2."EffectiveToUtc" IS NULL OR m2."EffectiveToUtc" > @utcNow)
                AND s8."AccessContextId" = @scope_AccessContextId
                AND s8."PortfolioId" = @scope_PortfolioId
                AND s8."Status" = 'Active'
                AND s8."SuspendedAtUtc" IS NULL
                AND s8."RevokedAtUtc" IS NULL
                AND s8."EffectiveFromUtc" <= @utcNow
                AND (s8."EffectiveToUtc" IS NULL OR s8."EffectiveToUtc" > @utcNow)
                AND s8."UserId" = @scope_UserId
                AND s8."AccessRevision" = @scope_AccessRevision
                AND s8."Status0" = 'Active'
                AND s8."SuspendedAtUtc0" IS NULL
                AND s8."RevokedAtUtc0" IS NULL
            """);
        AssertSqlContains(
            technicianSql,
            """
            ) AS s9 ON a0."ActiveAccessContextId" = s9."Id" AND a0."UserId" = s9."UserId"
            WHERE s9."DeletedAt" IS NULL
                AND a0."Id" = @scope_SessionId
                AND a0."UserId" = @scope_UserId
                AND a0."ActiveAccessContextId" = @scope_AccessContextId
                AND a0."Status" = 'Active'
                AND a0."RevokedAtUtc" IS NULL
                AND a0."ExpiresAtUtc" > @utcNow
            """);
        AssertSqlContains(
            technicianSql,
            """
            FROM "RoleProfileCapabilities" AS r2
            INNER JOIN "CapabilityDefinitions" AS c0
                ON r2."CapabilityDefinitionId" = c0."Id"
            WHERE r1."Id" = r2."RoleProfileId"
                AND c0."Key" = ANY (@keys)
                AND c0."AuthorizationTargetKind" = 'WorkOrder'
            """);
        AssertSqlContains(
            technicianSql,
            """
            FROM "WorkOrderResponsibilities" AS w10
            INNER JOIN (
                SELECT w11."Id", w11."DeletedAt", w11."PortfolioId", w11."PropertyId"
                FROM "WorkOrders" AS w11
                WHERE w11."DeletedAt" IS NULL
            ) AS w12 ON w10."WorkOrderId" = w12."Id"
                AND w10."PropertyId" = w12."PropertyId"
                AND w10."PortfolioId" = w12."PortfolioId"
            """);
        AssertSqlContains(
            technicianSql,
            """
            w."Id" = w10."WorkOrderId"
                AND w."PropertyId" = w10."PropertyId"
                AND w."PortfolioId" = w10."PortfolioId"
                AND w10."PortfolioId" = w."PortfolioId"
                AND w10."WorkspaceMembershipId" = m2."WorkspaceMembershipId"
                AND w10."MembershipRoleAssignmentId" = m2."Id"
                AND w10."EffectiveFromUtc" <= @utcNow
                AND (w10."EffectiveToUtc" IS NULL OR w10."EffectiveToUtc" > @utcNow)
            """);
        AssertSqlContains(
            technicianSql,
            """
            p."Id" = w."PropertyId" AND p."PortfolioId" = w."PortfolioId"
            """);
        AssertSqlContains(
            technicianSql,
            """
            w."Title" ILIKE @pattern ESCAPE ''
                OR p17."Name" ILIKE @pattern ESCAPE ''
                OR (u0."Id" IS NOT NULL AND u0."UnitNumber" ILIKE @pattern ESCAPE '')
            """);
        AssertSqlContains(
            technicianSql,
            """
            ORDER BY COALESCE(w."ScheduledFor", TIMESTAMPTZ 'infinity'), w."Title", w."Id"
            LIMIT @p OFFSET @p
            """);

        AssertPropertyAuthorizationShape(targetSql, "w0");
        AssertSqlContains(
            targetSql,
            """
            FROM "Properties" AS p
            INNER JOIN (
                SELECT u."Id", u."PortfolioId", u."PropertyId", u."UnitNumber"
                FROM "Units" AS u
                WHERE u."DeletedAt" IS NULL
            ) AS u0 ON p."Id" = u0."PropertyId" AND p."PortfolioId" = u0."PortfolioId"
            """);
        AssertSqlContains(
            targetSql,
            """
            p."Name" ILIKE @pattern ESCAPE ''
                OR p."AddressLine1" ILIKE @pattern ESCAPE ''
                OR u0."UnitNumber" ILIKE @pattern ESCAPE ''
            """);
        AssertSqlContains(
            targetSql,
            """
            ORDER BY p."Name", u0."UnitNumber", u0."Id"
            LIMIT @p OFFSET @p
            """);
    }

    private static void AssertPropertyAuthorizationShape(string sql, string membershipAlias)
    {
        AssertSqlContains(
            sql,
            """
            FROM "AuthSessions" AS a
            INNER JOIN (
            """);
        AssertSqlContains(
            sql,
            """
            ) AS s ON a."ActiveAccessContextId" = s."Id" AND a."UserId" = s."UserId"
            LEFT JOIN (
            """);
        AssertSqlContains(
            sql,
            $"""
            FROM "WorkspaceMemberships" AS {membershipAlias}
            INNER JOIN (
            """);
        AssertSqlContains(
            sql,
            """
            ) AS s1 ON s."Id" = s1."AccessContextId"
                AND s."PortfolioId" = s1."PortfolioId"
            """);
        AssertSqlContains(
            sql,
            """
            a."Id" = @scope_SessionId
                AND a."UserId" = @scope_UserId
                AND a."ActiveAccessContextId" = @scope_AccessContextId
                AND a."Status" = 'Active'
                AND a."RevokedAtUtc" IS NULL
                AND a."ExpiresAtUtc" > @utcNow
                AND s."Id" = @scope_AccessContextId
                AND s."UserId" = @scope_UserId
                AND s."PortfolioId" = @scope_PortfolioId
                AND s."AccessRevision" = @scope_AccessRevision
                AND s."Status" = 'Active'
                AND s."SuspendedAtUtc" IS NULL
                AND s."RevokedAtUtc" IS NULL
                AND s1."Id" IS NOT NULL
                AND s1."PortfolioId" = p."PortfolioId"
                AND s1."Status" = 'Active'
                AND s1."SuspendedAtUtc" IS NULL
                AND s1."RevokedAtUtc" IS NULL
                AND s1."EffectiveFromUtc" <= @utcNow
                AND (s1."EffectiveToUtc" IS NULL OR s1."EffectiveToUtc" > @utcNow)
            """);
        AssertSqlContains(
            sql,
            """
            FROM "MembershipRoleAssignments" AS m
            INNER JOIN (
            """);
        AssertSqlContains(
            sql,
            """
            ) AS s3 ON m."WorkspaceMembershipId" = s3."Id"
                AND m."PortfolioId" = s3."PortfolioId"
            INNER JOIN "RoleProfiles" AS r ON m."RoleProfileId" = r."Id"
            """);
        AssertSqlContains(
            sql,
            """
            s1."Id" = m."WorkspaceMembershipId"
                AND s1."PortfolioId" = m."PortfolioId"
                AND m."PortfolioId" = p."PortfolioId"
                AND m."Status" = 'Active'
                AND m."SuspendedAtUtc" IS NULL
                AND m."RevokedAtUtc" IS NULL
                AND m."EffectiveFromUtc" <= @utcNow
                AND (m."EffectiveToUtc" IS NULL OR m."EffectiveToUtc" > @utcNow)
            """);
        AssertSqlContains(
            sql,
            """
            FROM "RoleProfileCapabilities" AS r0
            INNER JOIN "CapabilityDefinitions" AS c
                ON r0."CapabilityDefinitionId" = c."Id"
            WHERE r."Id" = r0."RoleProfileId"
                AND c."Key" = ANY (@keys)
                AND c."AuthorizationTargetKind" = 'Property'
            """);
        AssertSqlContains(
            sql,
            """
            m."ScopeKind" = 'AllProperties'
                OR (m."ScopeKind" = 'SelectedProperties' AND EXISTS (
                SELECT 1
                FROM "MembershipRoleAssignmentProperties" AS m0
            """);
        AssertSqlContains(
            sql,
            """
            ) AS p7 ON m0."PropertyId" = p7."Id"
                AND m0."PortfolioId" = p7."PortfolioId"
            """);
        AssertSqlContains(
            sql,
            """
            m."Id" = m0."MembershipRoleAssignmentId"
                AND m."PortfolioId" = m0."PortfolioId"
                AND m0."PortfolioId" = p."PortfolioId"
                AND m0."PropertyId" = p."Id"
            """);
    }

    private static void AssertSqlContains(string sql, string expected) =>
        sql.Should().Contain(NormalizeSql(expected));

    private static string NormalizeSql(string sql)
    {
        var normalizedParameters = Regex.Replace(
            sql,
            @"@([A-Za-z_]+)\d+\b",
            "@$1",
            RegexOptions.CultureInvariant,
            TimeSpan.FromSeconds(1));
        return Regex.Replace(
                normalizedParameters,
                @"\s+",
                " ",
                RegexOptions.CultureInvariant,
                TimeSpan.FromSeconds(1))
            .Trim();
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
                """{"reviewDisposition":"AlreadyFullySigned","tenantName":"Jordan Tenant","startDate":"2026-08-01","endDate":"2027-07-31","possessionGivenAtUtc":"2026-08-01","monthlyRent":1250,"rentDueDay":1}""");

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
        command.Target.LeaseAgreement.PossessionGivenAtUtc.Should()
            .Be(new DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc));
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
    public async Task PrepareConfirmationAsync_LeaseTarget_ExplicitCreateSentinelsClearCaptureContext()
    {
        var draft = SeedDraft("Reviewing", extractedFields: null, targetEntityType: "LeaseAgreement");
        draft.CapturePropertyId = 12;
        draft.CaptureUnitId = 34;
        await _db.SaveChangesAsync();

        var result = await _sut.PrepareConfirmationAsync(
            PortfolioId,
            draft.Id,
            userId: 7,
            overridesJson:
                """{"propertyId":0,"unitId":0,"rentalStructure":"MultiRental","reviewDisposition":"AlreadyFullySigned"}""");

        result.Outcome.Should().Be(ScanConfirmationPreparationOutcome.Ready);
        result.Command.Should().NotBeNull();
        result.Command!.Target.LeaseAgreement.Should().NotBeNull();
        result.Command.Target.LeaseAgreement!.PropertyId.Should().Be(0);
        result.Command.Target.LeaseAgreement.UnitId.Should().BeNull();
    }

    [Fact]
    public async Task PrepareConfirmationAsync_LeaseTarget_ExplicitNewUnitClearsCapturedUnit()
    {
        var draft = SeedDraft("Reviewing", extractedFields: null, targetEntityType: "LeaseAgreement");
        draft.CapturePropertyId = 12;
        draft.CaptureUnitId = 34;
        await _db.SaveChangesAsync();

        var result = await _sut.PrepareConfirmationAsync(
            PortfolioId,
            draft.Id,
            userId: 7,
            overridesJson:
                """{"propertyId":21,"unitId":0,"reviewDisposition":"AlreadyFullySigned"}""");

        result.Outcome.Should().Be(ScanConfirmationPreparationOutcome.Ready);
        result.Command.Should().NotBeNull();
        result.Command!.Target.LeaseAgreement.Should().NotBeNull();
        result.Command.Target.LeaseAgreement!.PropertyId.Should().Be(21);
        result.Command.Target.LeaseAgreement.UnitId.Should().BeNull();
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

    private sealed class ScanRejectAtomicUnitOfWork(RentalCommandDbContext db) : IAtomicUnitOfWork
    {
        public async Task<AtomicCommandOutcome<TResult>> ExecuteAsync<TCommand, TResult>(
            AtomicCommandIdentity identity,
            TCommand command,
            IAtomicResultCodec<TResult> resultCodec,
            CancellationToken ct = default)
            where TCommand : notnull, IAtomicCommandData
            where TResult : notnull
        {
            var reject = command.Should().BeOfType<RejectScanDraftCommand>().Subject;
            var draft = await db.ScanDrafts.SingleAsync(item => item.Id == reject.DraftId, ct);
            var rejected = draft.Status is not ("Confirmed" or "Rejected" or "Confirming");
            if (rejected)
            {
                draft.Status = "Rejected";
                draft.ReviewedAt = DateTime.UtcNow;
                draft.ReviewedBy = reject.UserId.ToString();
                if (!string.IsNullOrWhiteSpace(reject.Reason)) draft.FailureReason = reject.Reason;
                await db.SaveChangesAsync(ct);
            }
            var result = new RejectScanDraftResult(rejected, reject.DraftId);
            return new AtomicCommandOutcome<TResult>(
                (TResult)(object)result,
                AtomicCommandDisposition.Executed,
                Guid.NewGuid());
        }
    }
}

internal sealed class RentalCommandTestDbContext : RentalCommand.TestCommon.SqliteCompatibleRentalCommandDbContext
{
    public RentalCommandTestDbContext(DbContextOptions<RentalCommandDbContext> options) : base(options) { }
}
