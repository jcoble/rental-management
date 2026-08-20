using System.Text.RegularExpressions;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using RentalCommand.Api.DTOs;
using RentalCommand.Api.Scanning;
using RentalCommand.Api.Writes;
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
    private readonly MutableTimeProvider _timeProvider = new(new DateTimeOffset(
        new DateTime(2027, 2, 1, 5, 0, 0, DateTimeKind.Utc)));
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
            new ScanRejectRequestWriteExecutor(_db),
            NullLogger<ScanService>.Instance,
            _timeProvider);
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
            new ScanRejectRequestWriteExecutor(translationDb),
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

        technicianSql.Should().Contain(
            "public.rc_api_effective_capability_scopes(",
            Exactly.Twice());
        technicianSql.Should().Contain("maintenance.assigned-work.update");
        technicianSql.Should().Contain("work.manage");
        technicianSql.Should().Contain("'Property'");
        technicianSql.Should().Contain("'WorkOrder'");
        AssertSqlContains(
            technicianSql,
            """
            r."ScopeKind" = 'AllProperties'
                OR (r."ScopeKind" = 'SelectedProperties' AND r."PropertyId" = w."PropertyId")
            """);
        technicianSql.Should().Contain("""r0."ScopeKind" = 'AssignedWorkOrders'""");
        AssertSqlContains(
            technicianSql,
            """
            FROM "WorkOrderResponsibilities" AS w0
            INNER JOIN (
                SELECT w1."Id", w1."DeletedAt", w1."PortfolioId", w1."PropertyId"
                FROM "WorkOrders" AS w1
                WHERE w1."DeletedAt" IS NULL
            ) AS w2 ON w0."WorkOrderId" = w2."Id"
                AND w0."PropertyId" = w2."PropertyId"
                AND w0."PortfolioId" = w2."PortfolioId"
            """);
        AssertSqlContains(
            technicianSql,
            """
            w."Id" = w0."WorkOrderId"
                AND w."PropertyId" = w0."PropertyId"
                AND w."PortfolioId" = w0."PortfolioId"
                AND w0."PortfolioId" = w."PortfolioId"
                AND w0."WorkspaceMembershipId" = r0."WorkspaceMembershipId"
                AND w0."MembershipRoleAssignmentId" = r0."AssignmentId"
                AND w0."EffectiveFromUtc" <= @utcNow
                AND (w0."EffectiveToUtc" IS NULL OR w0."EffectiveToUtc" > @utcNow)
            """);
        AssertSqlContains(
            technicianSql,
            """
            w."Title" ILIKE @pattern ESCAPE ''
                OR p2."Name" ILIKE @pattern ESCAPE ''
                OR (u0."Id" IS NOT NULL AND u0."UnitNumber" ILIKE @pattern ESCAPE '')
            """);
        AssertSqlContains(
            technicianSql,
            """
            ORDER BY COALESCE(w."ScheduledFor", TIMESTAMPTZ 'infinity'), w."Title", w."Id"
            LIMIT @p OFFSET @p
            """);

        targetSql.Should().Contain(
            "public.rc_api_effective_capability_scopes(",
            Exactly.Once());
        targetSql.Should().Contain("rentals.manage");
        targetSql.Should().Contain("'Property'");
        AssertSqlContains(
            targetSql,
            """
            r."ScopeKind" = 'AllProperties'
                OR (r."ScopeKind" = 'SelectedProperties' AND r."PropertyId" = p."Id")
            """);
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
                """{"reviewDisposition":"AlreadyFullySigned","tenantName":"Jordan Tenant","startDate":"2026-08-01","endDate":"2027-07-31","possessionGivenAtUtc":"2026-08-01","monthlyRent":1250,"rentDueDay":1,"rentTrackingStartMode":"ForwardOnly"}""");

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
        command.Target.LeaseAgreement.RentTrackingStartMode.Should()
            .Be(RentTrackingStartMode.ForwardOnly);
        command.Target.LeaseAgreement.RentTrackingStartOn.Should().BeNull();
        command.Target.LeaseAgreement.PossessionGivenAtUtc.Should()
            .Be(new DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc));
    }

    [Fact]
    public async Task PrepareConfirmationAsync_ContinuingLeaseTarget_RequiresExplicitRentTrackingChoice()
    {
        var draft = SeedDraft("Reviewing", extractedFields: null, targetEntityType: "LeaseAgreement");
        _db.Users.Add(new ApplicationUser
        {
            Id = 7,
            UserName = "continuing-scan-reviewer@example.test",
            NormalizedUserName = "CONTINUING-SCAN-REVIEWER@EXAMPLE.TEST",
            Email = "continuing-scan-reviewer@example.test",
            NormalizedEmail = "CONTINUING-SCAN-REVIEWER@EXAMPLE.TEST",
            DisplayName = "Continuing Scan Reviewer",
        });
        _db.Properties.Add(new Property
        {
            Id = 12,
            PortfolioId = PortfolioId,
            Name = "Continuing Lease Property",
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
        draft.CapturePropertyId = 12;
        draft.CaptureUnitId = 34;
        draft.CaptureLeaseManagementId = 56;
        draft.CaptureTenantAccountId = 78;
        await _db.SaveChangesAsync();

        var action = () => _sut.PrepareConfirmationAsync(
            PortfolioId,
            draft.Id,
            userId: 7,
            overridesJson:
                """{"reviewDisposition":"AlreadyFullySigned","tenantName":"Jordan Tenant","startDate":"2026-08-01","endDate":"2027-07-31","monthlyRent":1250,"rentDueDay":1}""");

        await action.Should().ThrowAsync<ScanConfirmationValidationException>()
            .WithMessage("*rent starts today*");
    }

    [Fact]
    public async Task PrepareConfirmationAsync_LeaseTarget_PreservesCustomRentTrackingChoice()
    {
        var draft = SeedDraft("Reviewing", extractedFields: null, targetEntityType: "LeaseAgreement");

        var result = await _sut.PrepareConfirmationAsync(
            PortfolioId,
            draft.Id,
            userId: 7,
            overridesJson:
                """{"propertyId":0,"unitId":0,"reviewDisposition":"AlreadyFullySigned","rentTrackingStartMode":"CustomCutoffDate","rentTrackingStartOn":"2026-06-20"}""");

        result.Outcome.Should().Be(ScanConfirmationPreparationOutcome.Ready);
        result.Command!.Target.LeaseAgreement!.RentTrackingStartMode.Should()
            .Be(RentTrackingStartMode.CustomCutoffDate);
        result.Command.Target.LeaseAgreement.RentTrackingStartOn.Should()
            .Be(new DateOnly(2026, 6, 20));
    }

    [Fact]
    public async Task LeaseImport_RejectsConflictingExtractedIdsBeforePreviewAndConfirmation()
    {
        var draft = SeedDraft(
            "Reviewing",
            extractedFields:
                """
                {
                  "property_id":{"value":"12"},
                  "unit_id":{"value":"34"},
                  "property_name":{"value":"York Duplex"},
                  "property_address":{"value":"508 York Street"},
                  "property_city":{"value":"Columbus"},
                  "unit_number":{"value":"A"}
                }
                """,
            targetEntityType: "LeaseAgreement");
        _db.Properties.Add(new Property
        {
            Id = 12,
            PortfolioId = PortfolioId,
            Name = "Walnut Duplex",
            AddressLine1 = "491 Walnut Street",
            City = "Columbus",
            State = "OH",
            PostalCode = "43223",
        });
        _db.Units.Add(new Unit
        {
            Id = 34,
            PortfolioId = PortfolioId,
            PropertyId = 12,
            UnitNumber = "B",
        });
        await _db.SaveChangesAsync();

        var proposal = await _sut.BuildLeaseProposalAsync(
            PortfolioId, draft.Id, overridesJson: "{}");
        var preparation = await _sut.PrepareConfirmationAsync(
            PortfolioId,
            draft.Id,
            userId: 7,
            overridesJson: """{"reviewDisposition":"AlreadyFullySigned"}""");

        proposal.Should().NotBeNull();
        proposal!.Property.Action.Should().Be("select");
        proposal.Property.ExistingId.Should().BeNull();
        proposal.Property.Label.Should().Be("York Duplex");
        proposal.Unit.Action.Should().Be("select");
        proposal.Unit.ExistingId.Should().BeNull();
        preparation.Command!.Target.LeaseAgreement!.PropertyId.Should().Be(0);
        preparation.Command.Target.LeaseAgreement.UnitId.Should().BeNull();
    }

    [Fact]
    public async Task PrepareConfirmationAsync_LeaseTarget_CustomRentTrackingRequiresDate()
    {
        var draft = SeedDraft("Reviewing", extractedFields: null, targetEntityType: "LeaseAgreement");

        var action = () => _sut.PrepareConfirmationAsync(
            PortfolioId,
            draft.Id,
            userId: 7,
            overridesJson:
                """{"propertyId":0,"unitId":0,"reviewDisposition":"AlreadyFullySigned","rentTrackingStartMode":"CustomCutoffDate"}""");

        await action.Should().ThrowAsync<ScanConfirmationValidationException>()
            .WithMessage("*custom date*");
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
        _db.Properties.Add(new Property
        {
            Id = 12,
            PortfolioId = PortfolioId,
            Name = "Captured Property",
            AddressLine1 = "12 Capture Street",
            City = "Akron",
            State = "OH",
            PostalCode = "44301",
        });
        _db.Units.Add(new Unit
        {
            Id = 34,
            PortfolioId = PortfolioId,
            PropertyId = 12,
            UnitNumber = "Captured",
        });
        draft.CapturePropertyId = 12;
        draft.CaptureUnitId = 34;
        await _db.SaveChangesAsync();
        // These tracked values model the remaining Unit command-center context.
        // They need not be valid targets because an explicit premises choice
        // must clear them before the sealed command is created.
        draft.CaptureLeaseManagementId = 56;
        draft.CaptureTenantAccountId = 78;
        draft.CaptureLeaseAgreementId = 90;

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
        result.Command.Target.LeaseAgreement.LeaseManagementId.Should().BeNull();
        result.Command.Target.LeaseAgreement.TenantAccountId.Should().BeNull();
        result.Command.Target.LeaseAgreement.LeaseAgreementId.Should().BeNull();
    }

    [Fact]
    public async Task PrepareConfirmationAsync_LeaseTarget_ExplicitNewUnitClearsCapturedUnit()
    {
        var draft = SeedDraft("Reviewing", extractedFields: null, targetEntityType: "LeaseAgreement");
        _db.Properties.Add(new Property
        {
            Id = 12,
            PortfolioId = PortfolioId,
            Name = "Captured Property",
            AddressLine1 = "12 Capture Street",
            City = "Akron",
            State = "OH",
            PostalCode = "44301",
        });
        _db.Units.Add(new Unit
        {
            Id = 34,
            PortfolioId = PortfolioId,
            PropertyId = 12,
            UnitNumber = "Captured",
        });
        draft.CapturePropertyId = 12;
        draft.CaptureUnitId = 34;
        await _db.SaveChangesAsync();
        draft.CaptureLeaseManagementId = 56;
        draft.CaptureTenantAccountId = 78;
        draft.CaptureLeaseAgreementId = 90;

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
        result.Command.Target.LeaseAgreement.LeaseManagementId.Should().BeNull();
        result.Command.Target.LeaseAgreement.TenantAccountId.Should().BeNull();
        result.Command.Target.LeaseAgreement.LeaseAgreementId.Should().BeNull();
    }

    [Fact]
    public async Task PrepareConfirmationAsync_LeaseTarget_PropertyOverrideClearsExtractedUnitFromAnotherProperty()
    {
        var draft = SeedDraft(
            "Reviewing",
            extractedFields:
                """{"property_id":{"value":"12"},"unit_id":{"value":"34"}}""",
            targetEntityType: "LeaseAgreement");
        _db.Properties.AddRange(
            new Property
            {
                Id = 12,
                PortfolioId = PortfolioId,
                Name = "Extracted Property",
                AddressLine1 = "12 Extracted Street",
                City = "Akron",
                State = "OH",
                PostalCode = "44301",
            },
            new Property
            {
                Id = 21,
                PortfolioId = PortfolioId,
                Name = "Selected Property",
                AddressLine1 = "21 Selected Street",
                City = "Akron",
                State = "OH",
                PostalCode = "44301",
            });
        _db.Units.Add(new Unit
        {
            Id = 34,
            PortfolioId = PortfolioId,
            PropertyId = 12,
            UnitNumber = "Extracted",
        });
        await _db.SaveChangesAsync();

        var result = await _sut.PrepareConfirmationAsync(
            PortfolioId,
            draft.Id,
            userId: 7,
            overridesJson:
                """{"propertyId":21,"reviewDisposition":"AlreadyFullySigned"}""");

        result.Outcome.Should().Be(ScanConfirmationPreparationOutcome.Ready);
        result.Command.Should().NotBeNull();
        result.Command!.Target.LeaseAgreement.Should().NotBeNull();
        result.Command.Target.LeaseAgreement!.PropertyId.Should().Be(21);
        result.Command.Target.LeaseAgreement.UnitId.Should().BeNull();
    }

    [Fact]
    public async Task PrepareConfirmationAsync_LeaseTarget_RejectsExplicitUnitFromAnotherProperty()
    {
        var draft = SeedDraft("Reviewing", extractedFields: null, targetEntityType: "LeaseAgreement");
        _db.Properties.AddRange(
            new Property
            {
                Id = 12,
                PortfolioId = PortfolioId,
                Name = "Unit Property",
                AddressLine1 = "12 Unit Street",
                City = "Akron",
                State = "OH",
                PostalCode = "44301",
            },
            new Property
            {
                Id = 21,
                PortfolioId = PortfolioId,
                Name = "Selected Property",
                AddressLine1 = "21 Selected Street",
                City = "Akron",
                State = "OH",
                PostalCode = "44301",
            });
        _db.Units.Add(new Unit
        {
            Id = 34,
            PortfolioId = PortfolioId,
            PropertyId = 12,
            UnitNumber = "Wrong property",
        });
        await _db.SaveChangesAsync();

        var action = () => _sut.PrepareConfirmationAsync(
            PortfolioId,
            draft.Id,
            userId: 7,
            overridesJson:
                """{"propertyId":21,"unitId":34,"reviewDisposition":"AlreadyFullySigned"}""");

        await action.Should().ThrowAsync<ScanConfirmationValidationException>()
            .WithMessage("*selected Unit*selected Property*");
    }

    [Fact]
    public async Task PrepareConfirmationAsync_LeaseEndingNoticeTarget_SealsEndingWorkflowCommand()
    {
        var draft = SeedDraft(
            "Reviewing",
            """
            {
              "lease_management_id": {"value":"56","confidence":0.93},
              "unit_id": {"value":"34","confidence":0.94},
              "notice_given_date": {"value":"2027-01-14","confidence":0.92},
              "planned_move_out_date": {"value":"2027-02-28","confidence":0.9},
              "notice_type": {"value":"tenant non-renewal notice","confidence":0.95},
              "reason": {"value":"Tenant will not renew.","confidence":0.88}
            }
            """,
            targetEntityType: "LeaseEndingNotice");
        _db.StoredFiles.Add(new StoredFile
        {
            Id = 44,
            PortfolioId = PortfolioId,
            FileName = "scn-0935.pdf",
            FilePath = "uploads/scn-0935.pdf",
            ContentType = "application/pdf",
            FileSize = 1024,
        });
        draft.SourceStoredFileId = 44;
        draft.SourceContentSha256 = new string('b', 64);
        draft.SourceLabel = "SCN-0935";
        await _db.SaveChangesAsync();

        var result = await _sut.PrepareConfirmationAsync(
            PortfolioId,
            draft.Id,
            userId: 7,
            overridesJson:
                """{"noticeGivenDate":"2027-01-15","plannedMoveOutDate":"2027-03-01","reason":"Reviewed tenant notice."}""");

        result.Outcome.Should().Be(ScanConfirmationPreparationOutcome.Ready);
        result.Command.Should().NotBeNull();
        var command = result.Command!;
        command.SourceStoredFileId.Should().Be(44);
        command.SourceContentSha256.Should().Be(new string('b', 64));
        command.SourceLabel.Should().Be("SCN-0935");
        command.Target.Kind.Should().Be(ScanConfirmationTargetKind.LeaseEndingNotice);
        command.Target.LeaseEndingNotice.Should().NotBeNull();
        var notice = command.Target.LeaseEndingNotice!;
        notice.LeaseManagementId.Should().Be(56);
        notice.UnitId.Should().Be(34);
        notice.NoticeGivenAtUtc.Should().Be(new DateTime(2027, 1, 15, 0, 0, 0, DateTimeKind.Utc));
        notice.PlannedMoveOutAtUtc.Should().Be(new DateTime(2027, 3, 1, 0, 0, 0, DateTimeKind.Utc));
        notice.NoticeType.Should().Be("tenant non-renewal notice");
        notice.Reason.Should().Be("Reviewed tenant notice.");
        command.ExpectedDraftFingerprint.Should().Be(
            ScanConfirmationDraftFingerprint.Create(
                "LeaseEndingNotice",
                sourceStoredFileId: 44,
                extractedFields: draft.ExtractedFields,
                sourceContentSha256: new string('b', 64),
                sourceLabel: "SCN-0935"));
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
        rejectedDraft.ReviewedAt.Should().Be(_timeProvider.GetUtcNow().UtcDateTime);
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
            PortfolioId = PortfolioId,
            FilePath = $"uploads/test-{Guid.NewGuid():N}.jpg",
            TargetEntityType = targetEntityType,
            Status = status,
            ExtractedFields = extractedFields,
            CreatedAt = DateTime.UtcNow,
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
            AtomicJsonResultCodec<TResult> resultCodec,
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
                draft.ReviewedAt = reject.ReviewedAtUtc;
                draft.ReviewedBy = reject.UserId.ToString();
                if (!string.IsNullOrWhiteSpace(reject.Reason)) draft.FailureReason = reject.Reason;
                await db.SaveChangesAsync(ct);
            }
            var result = new RejectScanDraftResult(
                rejected,
                reject.DraftId,
                rejected ? reject.ReviewedAtUtc : null);
            return new AtomicCommandOutcome<TResult>(
                (TResult)(object)result,
                AtomicCommandDisposition.Executed,
                Guid.NewGuid());
        }
    }

    private sealed class ScanRejectRequestWriteExecutor(RentalCommandDbContext db) : IRequestWriteExecutor
    {
        public async Task<AtomicCommandOutcome<TResult>> ExecuteAsync<TCommand, TResult>(
            string idempotencyKey,
            TransactionalWrite<TCommand, TResult> write,
            CancellationToken ct = default)
            where TCommand : notnull, IAtomicCommandData
            where TResult : notnull
        {
            var reject = write.Request.Should().BeOfType<RejectScanDraftCommand>().Subject;
            var draft = await db.ScanDrafts.SingleAsync(item => item.Id == reject.DraftId, ct);
            var rejected = draft.Status is not ("Confirmed" or "Rejected" or "Confirming");
            if (rejected)
            {
                draft.Status = "Rejected";
                draft.ReviewedAt = reject.ReviewedAtUtc;
                draft.ReviewedBy = reject.UserId.ToString();
                if (!string.IsNullOrWhiteSpace(reject.Reason)) draft.FailureReason = reject.Reason;
                await db.SaveChangesAsync(ct);
            }
            var result = new RejectScanDraftResult(
                rejected, reject.DraftId, rejected ? reject.ReviewedAtUtc : null);
            return new AtomicCommandOutcome<TResult>(
                (TResult)(object)result, AtomicCommandDisposition.Executed, Guid.NewGuid());
        }
    }

    private sealed class MutableTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }
}

internal sealed class RentalCommandTestDbContext : RentalCommand.TestCommon.SqliteCompatibleRentalCommandDbContext
{
    public RentalCommandTestDbContext(DbContextOptions<RentalCommandDbContext> options) : base(options) { }
}
