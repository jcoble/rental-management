using System.Data.Common;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging;
using Moq;
using RentalCommand.Api.Auth;
using RentalCommand.Api.Controllers;
using RentalCommand.Api.DTOs;
using RentalCommand.Api.Services.Domain;
using RentalCommand.Core.Authorization;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Interfaces;
using RentalCommand.Data;
using RentalCommand.TestCommon;

namespace RentalCommand.Api.Tests.Domain;

/// <summary>
/// Proves that move-out statements are assembled from the canonical lease relationship and
/// append-only deposit subledger, including immutable reversals and account-keyed photos.
/// </summary>
public sealed class MoveOutStatementTests : IDisposable
{
    private const int PortfolioId = 1;
    private const int ActorUserId = 41;

    private static readonly byte[] OnePixelPng = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+M9QDwADhgGAWjR9awAAAABJRU5ErkJggg==");

    private readonly MoveOutStatementTestContext _ctx = new();
    private readonly InMemoryFileStorage _storage = new();
    private readonly CapturingPdfGenerator _pdf = new();

    public void Dispose() => _ctx.Dispose();

    [Fact]
    public async Task GetAsync_UsesCanonicalFactsAndNetsDeductionReversals()
    {
        var graph = SeedCanonicalChain();
        var carpetDeduction = AddEntry(
            graph,
            SecurityDepositEntryType.Deduction,
            SecurityDepositDirection.Decrease,
            150m,
            "Carpet cleaning",
            new DateOnly(2026, 6, 2));
        AddEntry(
            graph,
            SecurityDepositEntryType.Deduction,
            SecurityDepositDirection.Decrease,
            400m,
            "Unpaid final rent",
            new DateOnly(2026, 6, 3));
        AddEntry(
            graph,
            SecurityDepositEntryType.Reversal,
            SecurityDepositDirection.Increase,
            50m,
            "Correct carpet deduction",
            new DateOnly(2026, 6, 4),
            carpetDeduction.Id);
        AddEntry(
            graph,
            SecurityDepositEntryType.Refund,
            SecurityDepositDirection.Decrease,
            1_000m,
            "Refund paid by ACH",
            new DateOnly(2026, 6, 5),
            postedAtUtc: new DateTime(2026, 6, 5, 14, 30, 0, DateTimeKind.Utc));
        var matchingKey = await _storage.UploadAsync(
            new MemoryStream(OnePixelPng),
            "matching.png",
            "image/png");
        var otherKey = await _storage.UploadAsync(
            new MemoryStream(OnePixelPng),
            "other-account.png",
            "image/png");
        _ctx.Db.StoredFiles.AddRange(
            CreatePhoto(graph.DepositAccount.Id, "matching.png", matchingKey),
            CreatePhoto(graph.DepositAccount.Id + 10_000, "other-account.png", otherKey));
        await _ctx.Db.SaveChangesAsync();

        var scope = SeedAdministratorScope();
        _ctx.Commands.Clear();
        var result = await CreateSut().GetAsync(
            scope,
            graph.DepositAccount.TenantAccountId);

        result.Should().Equal(CapturingPdfGenerator.PdfBytes);
        _pdf.LastData.Should().NotBeNull();
        _pdf.LastData!.TenantName.Should().Be("Dana Reyes");
        _pdf.LastData.PropertyLine.Should().Contain("Maple Court");
        _pdf.LastData.UnitLine.Should().Be("Unit 2B");
        _pdf.LastData.LeaseNumber.Should().Be("AGR-1001-V1");
        _pdf.LastData.MoveOutDate.Should().Be(graph.Management.PossessionReturnedAtUtc);
        _pdf.LastData.StatementDate.Should().Be(new DateTime(2026, 6, 5, 14, 30, 0, DateTimeKind.Utc));
        _pdf.LastData.DepositHeld.Should().Be(1_500m);
        _pdf.LastData.Deductions.Should().BeEquivalentTo(
            [
                new DepositDeduction("Carpet cleaning", 100m, null),
                new DepositDeduction("Unpaid final rent", 400m, null),
            ],
            options => options.WithStrictOrdering());
        _pdf.LastData.Photos.Should().ContainSingle();
        _pdf.LastData.Photos[0].Should().Equal(OnePixelPng);

        _ctx.Commands.Should().HaveCount(3,
            "the PDF path is one authorized header query, one authorized deduction query, and one authorized photo-metadata query");
        _ctx.Commands.Should().OnlyContain(sql =>
            sql.Contains("AuthSessions", StringComparison.Ordinal) &&
            sql.Contains("AccessRevision", StringComparison.Ordinal) &&
            sql.Contains("RoleProfileCapabilities", StringComparison.Ordinal) &&
            sql.Contains("CapabilityDefinitions", StringComparison.Ordinal) &&
            sql.Contains("MembershipRoleAssignmentProperties", StringComparison.Ordinal) &&
            sql.Contains("Properties", StringComparison.Ordinal) &&
            sql.Contains("EXISTS", StringComparison.OrdinalIgnoreCase),
            "every move-out read must prove current capability and property scope in its SQL");
        _ctx.Commands.Count(sql => sql.Contains("SecurityDepositEntries", StringComparison.Ordinal))
            .Should().Be(2, "deductions are one set query and the header computes its refund date without per-row lookups");
        _ctx.Commands.Count(sql => sql.Contains("StoredFiles", StringComparison.Ordinal))
            .Should().Be(1, "all authorized photo metadata must load in one ordered query");
        var headerSql = _ctx.Commands[0];
        headerSql.Should().Contain("vw_security_deposit_balances",
            "the statement corpus must come from the canonical reversal-netted deposit view");
        headerSql.Should().Contain("HeldBalance");
        headerSql.Should().Contain("TotalDeductions");
        headerSql.Should().Contain("TotalRefunded");
        headerSql.Should().Contain("CASE",
            "the non-negative deposit corpus must be calculated in the translated header query, not after materialization");
        headerSql.Should().Contain("SecurityDepositAccounts");
        headerSql.Should().Contain("TenantAccounts");
        headerSql.Should().Contain("PortfolioId",
            "the translated aggregate and joins must remain portfolio-scoped");
    }

    [Fact]
    public async Task MoveOutStatement_OutOfScopeDepositReturns404WithoutReadingDeductionsOrPhotos()
    {
        var decoy = SeedCanonicalChain();
        AddEntry(
            decoy,
            SecurityDepositEntryType.Deduction,
            SecurityDepositDirection.Decrease,
            275m,
            "Decoy deduction",
            new DateOnly(2026, 6, 2));
        var photoKey = await _storage.UploadAsync(
            new MemoryStream(OnePixelPng),
            "decoy.png",
            "image/png");
        _ctx.Db.StoredFiles.Add(CreatePhoto(decoy.DepositAccount.Id, "decoy.png", photoKey));
        var allowedProperty = new Property
        {
            PortfolioId = PortfolioId,
            Name = "Authorized property",
            AddressLine1 = "99 Allowed Ave",
            City = "Columbus",
            State = "OH",
            PostalCode = "43215",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        _ctx.Db.Properties.Add(allowedProperty);
        _ctx.Db.SaveChanges();
        var scope = SeedSelectedPropertyManagerScope(allowedProperty.Id);
        var controller = new TenantAccountsController(
            Mock.Of<ITenantAccountQueryService>(),
            CreateSut())
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext(),
            },
        };
        controller.HttpContext.Items[CanonicalAccessContextHttpItem.Key] = new ActiveAccessContext(
            scope.SessionId,
            scope.UserId,
            scope.AccessContextId,
            scope.PortfolioId,
            scope.AccessRevision,
            WorkspaceExperience.Management,
            WorkspaceMembershipId: null,
            DefaultExperience: WorkspaceExperience.Management);
        _ctx.Commands.Clear();

        var result = await controller.DepositMoveOutStatement(
            decoy.DepositAccount.TenantAccountId,
            CancellationToken.None);

        result.Should().BeOfType<NotFoundObjectResult>(
            "an unauthorized deposit must be indistinguishable from a missing deposit");
        _ctx.Commands.Should().ContainSingle(
            "the denied header query must stop the PDF path before deduction and photo metadata reads");
        _ctx.Commands[0].Should().Contain("AuthSessions");
        _ctx.Commands[0].Should().Contain("AccessRevision");
        _ctx.Commands[0].Should().Contain("MembershipRoleAssignmentProperties");
        _ctx.Commands[0].Should().Contain("RoleProfileCapabilities");
        _ctx.Commands.Should().NotContain(sql => sql.Contains("StoredFiles", StringComparison.Ordinal));
        _storage.DownloadCount.Should().Be(0);
        _pdf.LastData.Should().BeNull();
    }

    private TenantAccountMoveOutStatementService CreateSut() => new(
        _ctx.Db,
        _storage,
        _pdf,
        Mock.Of<ILogger<TenantAccountMoveOutStatementService>>(),
        TimeProvider.System);

    private WorkspaceReadScope SeedAdministratorScope()
        => SeedWorkspaceScope(
            RoleProfileKeys.WorkspaceAdministrator,
            MembershipRoleAssignmentScopeKind.AllProperties,
            selectedPropertyId: null);

    private WorkspaceReadScope SeedSelectedPropertyManagerScope(int propertyId)
        => SeedWorkspaceScope(
            RoleProfileKeys.PropertyManager,
            MembershipRoleAssignmentScopeKind.SelectedProperties,
            propertyId);

    private WorkspaceReadScope SeedWorkspaceScope(
        string roleKey,
        MembershipRoleAssignmentScopeKind scopeKind,
        int? selectedPropertyId)
    {
        var now = DateTime.UtcNow;
        var context = new WorkspaceAccessContext
        {
            UserId = ActorUserId,
            PortfolioId = PortfolioId,
            Status = WorkspaceAccessContextStatus.Active,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
        };
        var membership = new WorkspaceMembership
        {
            AccessContext = context,
            PortfolioId = PortfolioId,
            Status = WorkspaceMembershipStatus.Active,
            DefaultExperience = WorkspaceExperience.Management,
            EffectiveFromUtc = now.AddMinutes(-1),
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
        };
        var assignment = new MembershipRoleAssignment
        {
            WorkspaceMembership = membership,
            PortfolioId = PortfolioId,
            RoleProfileId = AccessCatalog.Roles.Single(role => role.Key == roleKey).Id,
            Status = MembershipRoleAssignmentStatus.Active,
            ScopeKind = scopeKind,
            EffectiveFromUtc = now.AddMinutes(-1),
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
        };
        if (selectedPropertyId.HasValue)
        {
            assignment.SelectedProperties.Add(new MembershipRoleAssignmentProperty
            {
                MembershipRoleAssignment = assignment,
                PortfolioId = PortfolioId,
                PropertyId = selectedPropertyId.Value,
            });
        }
        var session = new AuthSession
        {
            Id = Guid.NewGuid(),
            UserId = ActorUserId,
            ActiveAccessContext = context,
            Status = AuthSessionStatus.Active,
            CreatedAtUtc = now,
            LastSeenAtUtc = now,
            ExpiresAtUtc = now.AddHours(1),
        };
        _ctx.Db.AddRange(assignment, session);
        _ctx.Db.SaveChanges();
        return new WorkspaceReadScope(
            PortfolioId, ActorUserId, session.Id, context.Id, context.AccessRevision);
    }

    private CanonicalDepositGraph SeedCanonicalChain()
    {
        var createdAtUtc = new DateTime(2025, 6, 1, 12, 0, 0, DateTimeKind.Utc);
        var possessionReturnedAtUtc = new DateTime(2026, 6, 1, 18, 0, 0, DateTimeKind.Utc);
        var actor = new ApplicationUser
        {
            Id = ActorUserId,
            UserName = "moveout-test@rentalcommand.local",
            NormalizedUserName = "MOVEOUT-TEST@RENTALCOMMAND.LOCAL",
            Email = "moveout-test@rentalcommand.local",
            NormalizedEmail = "MOVEOUT-TEST@RENTALCOMMAND.LOCAL",
            DisplayName = "Move-out Test Actor",
            CreatedAt = createdAtUtc,
        };
        var tenant = new Tenant
        {
            PortfolioId = PortfolioId,
            FirstName = "Dana",
            LastName = "Reyes",
            CreatedAt = createdAtUtc,
            UpdatedAt = createdAtUtc,
        };
        var property = new Property
        {
            PortfolioId = PortfolioId,
            Name = "Maple Court",
            AddressLine1 = "12 Maple Ct",
            City = "Columbus",
            State = "OH",
            PostalCode = "43215",
            CreatedAt = createdAtUtc,
            UpdatedAt = createdAtUtc,
        };
        var template = new DocumentTemplate
        {
            PortfolioId = PortfolioId,
            Kind = DocumentTemplateKind.Lease,
            Status = DocumentTemplateStatus.Active,
            RenderMode = DocumentTemplateRenderMode.Restyle,
            Name = "Canonical lease template",
            DraftHtml = "<p>Lease</p>",
            Version = 1,
            CreatedAtUtc = createdAtUtc,
            UpdatedAtUtc = createdAtUtc,
        };
        _ctx.Db.Users.Add(actor);
        _ctx.Db.Tenants.Add(tenant);
        _ctx.Db.Properties.Add(property);
        _ctx.Db.DocumentTemplates.Add(template);
        _ctx.Db.SaveChanges();
        var documentSourceVersion = new LegalDocumentSourceVersion
        {
            PublicId = Guid.NewGuid(),
            PortfolioId = PortfolioId,
            SourceKind = LegalDocumentSourceKind.AuthoredTemplateSnapshot,
            BusinessKey = $"template:{template.Id}:v{template.Version}",
            DocumentTemplateId = template.Id,
            DocumentTemplateVersion = template.Version,
            RendererKey = "lease-agreement-overlay",
            RendererVersion = 1,
            SnapshotPayload = "{}",
            CreatedAtUtc = createdAtUtc,
            CreatedByUserId = ActorUserId,
        };
        _ctx.Db.LegalDocumentSourceVersions.Add(documentSourceVersion);
        _ctx.Db.SaveChanges();

        var unit = new Unit
        {
            PortfolioId = PortfolioId,
            PropertyId = property.Id,
            UnitNumber = "2B",
            CreatedAt = createdAtUtc,
            UpdatedAt = createdAtUtc,
        };
        _ctx.Db.Units.Add(unit);
        _ctx.Db.SaveChanges();

        var management = new LeaseManagement
        {
            PublicId = Guid.NewGuid(),
            PortfolioId = PortfolioId,
            PropertyId = property.Id,
            UnitId = unit.Id,
            RelationshipNumber = "LM-1001",
            PlannedPossessionAtUtc = createdAtUtc,
            PossessionGivenAtUtc = createdAtUtc,
            PlannedMoveOutAtUtc = possessionReturnedAtUtc,
            PossessionReturnedAtUtc = possessionReturnedAtUtc,
            AccountClosedAtUtc = possessionReturnedAtUtc.AddHours(1),
            CreatedAtUtc = createdAtUtc,
            CreatedByUserId = ActorUserId,
            UpdatedAtUtc = possessionReturnedAtUtc,
            RowVersion = Guid.NewGuid(),
        };
        _ctx.Db.LeaseManagements.Add(management);
        _ctx.Db.SaveChanges();

        var agreement = new LeaseAgreement
        {
            PublicId = Guid.NewGuid(),
            PortfolioId = PortfolioId,
            LeaseManagementId = management.Id,
            VersionNumber = 1,
            AgreementNumber = "AGR-1001-V1",
            ChangeType = LeaseAgreementChangeType.Initial,
            TermType = LeaseAgreementTermType.FixedTerm,
            TermStartOn = new DateOnly(2025, 6, 1),
            TermEndOn = new DateOnly(2026, 5, 31),
            GoverningFromOn = new DateOnly(2025, 6, 1),
            BaseRentAmount = 1_500m,
            RentDueDay = 1,
            SecurityDepositObligation = 1_500m,
            LateFeeAmount = 50m,
            GracePeriodDays = 5,
            Currency = "USD",
            TermsSchemaVersion = 1,
            TermsPayload = "{}",
            DocumentSourceVersionId = documentSourceVersion.Id,
            CreatedAtUtc = createdAtUtc,
            CreatedByUserId = ActorUserId,
            UpdatedAtUtc = createdAtUtc,
        };
        var party = new LeaseManagementParty
        {
            PortfolioId = PortfolioId,
            LeaseManagementId = management.Id,
            TenantId = tenant.Id,
            Role = LeaseManagementPartyRole.PrimaryTenant,
            EffectiveFrom = new DateOnly(2025, 6, 1),
            ChangeReason = "Initial household",
            CreatedAtUtc = createdAtUtc,
            CreatedByUserId = ActorUserId,
        };
        var tenantAccount = new TenantAccount
        {
            PublicId = Guid.NewGuid(),
            PortfolioId = PortfolioId,
            LeaseManagementId = management.Id,
            AccountNumber = "TA-1001",
            Currency = "USD",
            OpenedAtUtc = createdAtUtc,
            ClosedAtUtc = possessionReturnedAtUtc.AddHours(1),
            CloseReasonCode = "MoveOutComplete",
            CreatedAtUtc = createdAtUtc,
            CreatedByUserId = ActorUserId,
        };
        _ctx.Db.LeaseAgreements.Add(agreement);
        _ctx.Db.LeaseManagementParties.Add(party);
        _ctx.Db.TenantAccounts.Add(tenantAccount);
        _ctx.Db.SaveChanges();

        var depositAccount = new SecurityDepositAccount
        {
            PortfolioId = PortfolioId,
            TenantAccountId = tenantAccount.Id,
            OriginatingAgreementId = agreement.Id,
            Currency = "USD",
            CreatedAtUtc = createdAtUtc,
            CreatedByUserId = ActorUserId,
        };
        _ctx.Db.SecurityDepositAccounts.Add(depositAccount);
        _ctx.Db.SaveChanges();

        var graph = new CanonicalDepositGraph(management, agreement, depositAccount);
        AddEntry(
            graph,
            SecurityDepositEntryType.Receipt,
            SecurityDepositDirection.Increase,
            1_500m,
            "Security deposit funded",
            new DateOnly(2025, 6, 1));

        return graph;
    }

    private SecurityDepositEntry AddEntry(
        CanonicalDepositGraph graph,
        SecurityDepositEntryType entryType,
        SecurityDepositDirection direction,
        decimal amount,
        string description,
        DateOnly effectiveOn,
        long? reversesEntryId = null,
        DateTime? postedAtUtc = null)
    {
        var entry = new SecurityDepositEntry
        {
            PublicId = Guid.NewGuid(),
            PortfolioId = PortfolioId,
            SecurityDepositAccountId = graph.DepositAccount.Id,
            EntryType = entryType,
            Direction = direction,
            Amount = amount,
            Currency = "USD",
            EffectiveOn = effectiveOn,
            PostedAtUtc = postedAtUtc ?? effectiveOn.ToDateTime(new TimeOnly(12, 0), DateTimeKind.Utc),
            BusinessKey = $"test:{entryType}:{Guid.NewGuid():N}",
            Description = description,
            LeaseAgreementId = graph.Agreement.Id,
            ReversesEntryId = reversesEntryId,
            CreatedByUserId = ActorUserId,
        };
        _ctx.Db.SecurityDepositEntries.Add(entry);
        _ctx.Db.SaveChanges();
        return entry;
    }

    private static StoredFile CreatePhoto(int accountId, string fileName, string filePath) => new()
    {
        PortfolioId = PortfolioId,
        FileName = fileName,
        FilePath = filePath,
        ContentType = "image/png",
        FileSize = OnePixelPng.Length,
        EntityType = nameof(SecurityDepositAccount),
        EntityId = accountId,
        UploadedAt = new DateTime(2026, 6, 1, 12, 0, 0, DateTimeKind.Utc),
    };

    private sealed record CanonicalDepositGraph(
        LeaseManagement Management,
        LeaseAgreement Agreement,
        SecurityDepositAccount DepositAccount);

    private sealed class CapturingPdfGenerator : IMoveOutStatementPdfGenerator
    {
        internal static readonly byte[] PdfBytes = "%PDF-canonical"u8.ToArray();

        internal MoveOutStatementData? LastData { get; private set; }

        public byte[] Generate(MoveOutStatementData data)
        {
            LastData = data;
            return PdfBytes;
        }
    }

    private sealed class InMemoryFileStorage : IFileStorage
    {
        private readonly Dictionary<string, byte[]> _files = [];
        internal int DownloadCount { get; private set; }

        public async Task<string> UploadAsync(
            Stream content,
            string fileName,
            string contentType,
            CancellationToken ct = default)
        {
            using var buffer = new MemoryStream();
            await content.CopyToAsync(buffer, ct);
            var key = $"{Guid.NewGuid():N}_{fileName}";
            _files[key] = buffer.ToArray();
            return key;
        }

        public Task<Stream> DownloadAsync(string path, CancellationToken ct = default)
        {
            DownloadCount++;
            if (!_files.TryGetValue(path, out var bytes))
                throw new FileNotFoundException(path);

            return Task.FromResult<Stream>(new MemoryStream(bytes));
        }

        public Task DeleteAsync(string path, CancellationToken ct = default)
        {
            _files.Remove(path);
            return Task.CompletedTask;
        }
    }

    private sealed class MoveOutStatementTestContext : IDisposable
    {
        private readonly SqliteConnection _connection;

        internal MoveOutStatementTestContext()
        {
            _connection = new SqliteConnection("DataSource=:memory:");
            _connection.Open();
            var options = new DbContextOptionsBuilder<RentalCommandDbContext>()
                .UseSqlite(_connection)
                .AddInterceptors(new MoveOutRecordingCommandInterceptor(Commands))
                .Options;
            Db = new MoveOutStatementTestDbContext(options);
            Db.Database.EnsureCreated();
            Db.Database.InstallCanonicalLeaseProjectionViewsForSqlite();
            Db.Portfolios.Add(new Portfolio
            {
                Id = PortfolioId,
                Name = "Test Portfolio",
                ManagementCompanyName = "Test Co",
                TimeZone = "UTC",
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            });
            Db.SaveChanges();
        }

        internal RentalCommandDbContext Db { get; }
        internal List<string> Commands { get; } = [];

        public void Dispose()
        {
            Db.Dispose();
            _connection.Dispose();
        }
    }

    private sealed class MoveOutRecordingCommandInterceptor(List<string> commands)
        : DbCommandInterceptor
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

    private sealed class MoveOutStatementTestDbContext(DbContextOptions<RentalCommandDbContext> options)
        : RentalCommand.TestCommon.SqliteCompatibleRentalCommandDbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);
            modelBuilder.Entity<SecurityDepositBalanceProjection>()
                .HasKey(row => new { row.PortfolioId, row.SecurityDepositAccountId });
        }
    }

    private const string SecurityDepositBalanceViewSqlite = """
        CREATE VIEW "vw_security_deposit_balances" AS
        WITH entry_reversals AS (
            SELECT reversal."PortfolioId",
                   reversal."SecurityDepositAccountId",
                   reversal."ReversesEntryId" AS "SecurityDepositEntryId",
                   SUM(reversal."Amount") AS "ReversedAmount"
            FROM "SecurityDepositEntries" AS reversal
            WHERE reversal."EntryType" = 'Reversal'
            GROUP BY reversal."PortfolioId", reversal."SecurityDepositAccountId", reversal."ReversesEntryId"
        ),
        effective_entries AS (
            SELECT entry."PortfolioId",
                   entry."SecurityDepositAccountId",
                   entry."EntryType",
                   entry."Direction",
                   MAX(entry."Amount" - COALESCE(entry_reversals."ReversedAmount", 0), 0) AS "NetAmount"
            FROM "SecurityDepositEntries" AS entry
            LEFT JOIN entry_reversals
              ON entry_reversals."PortfolioId" = entry."PortfolioId"
             AND entry_reversals."SecurityDepositAccountId" = entry."SecurityDepositAccountId"
             AND entry_reversals."SecurityDepositEntryId" = entry."Id"
            WHERE entry."EntryType" <> 'Reversal'
        ),
        totals AS (
            SELECT entry."PortfolioId",
                   entry."SecurityDepositAccountId",
                   SUM(CASE WHEN entry."EntryType" = 'Receipt' THEN entry."NetAmount" ELSE 0 END) AS "TotalReceived",
                   SUM(CASE WHEN entry."EntryType" = 'Deduction' THEN entry."NetAmount" ELSE 0 END) AS "TotalDeductions",
                   SUM(CASE WHEN entry."EntryType" = 'Refund' THEN entry."NetAmount" ELSE 0 END) AS "TotalRefunded",
                   SUM(CASE WHEN entry."EntryType" = 'TransferIn' THEN entry."NetAmount" ELSE 0 END) AS "TotalTransferredIn",
                   SUM(CASE WHEN entry."EntryType" = 'TransferOut' THEN entry."NetAmount" ELSE 0 END) AS "TotalTransferredOut",
                   SUM(CASE
                       WHEN entry."EntryType" = 'Adjustment' AND entry."Direction" = 'Increase' THEN entry."NetAmount"
                       WHEN entry."EntryType" = 'Adjustment' AND entry."Direction" = 'Decrease' THEN -entry."NetAmount"
                       ELSE 0 END) AS "NetAdjustments",
                   SUM(CASE WHEN entry."Direction" = 'Increase' THEN entry."NetAmount" ELSE -entry."NetAmount" END)
                       AS "HeldBalance"
            FROM effective_entries AS entry
            GROUP BY entry."PortfolioId", entry."SecurityDepositAccountId"
        )
        SELECT deposit."PortfolioId",
               account."LeaseManagementId",
               deposit."TenantAccountId",
               deposit."Id" AS "SecurityDepositAccountId",
               CURRENT_TIMESTAMP AS "EffectiveNowUtc",
               DATE('now') AS "BusinessDate",
               deposit."Currency",
               COALESCE(totals."TotalReceived", 0) AS "TotalReceived",
               COALESCE(totals."TotalDeductions", 0) AS "TotalDeductions",
               COALESCE(totals."TotalRefunded", 0) AS "TotalRefunded",
               COALESCE(totals."TotalTransferredIn", 0) AS "TotalTransferredIn",
               COALESCE(totals."TotalTransferredOut", 0) AS "TotalTransferredOut",
               COALESCE(totals."NetAdjustments", 0) AS "NetAdjustments",
               COALESCE(totals."HeldBalance", 0) AS "HeldBalance",
               CASE
                   WHEN COALESCE(totals."TotalReceived", 0) = 0 THEN 'NotFunded'
                   WHEN management."AccountClosedAtUtc" IS NULL OR COALESCE(totals."HeldBalance", 0) > 0 THEN 'Held'
                   WHEN COALESCE(totals."TotalDeductions", 0) > 0
                        AND COALESCE(totals."TotalRefunded", 0) = 0 THEN 'Withheld'
                   WHEN COALESCE(totals."TotalDeductions", 0) > 0 THEN 'PartiallyReturned'
                   WHEN COALESCE(totals."TotalRefunded", 0) > 0 THEN 'Returned'
                   ELSE 'Held'
               END AS "DepositStatus"
        FROM "SecurityDepositAccounts" AS deposit
        JOIN "TenantAccounts" AS account
          ON account."Id" = deposit."TenantAccountId"
         AND account."PortfolioId" = deposit."PortfolioId"
        JOIN "LeaseManagements" AS management
          ON management."Id" = account."LeaseManagementId"
         AND management."PortfolioId" = account."PortfolioId"
        LEFT JOIN totals
          ON totals."PortfolioId" = deposit."PortfolioId"
         AND totals."SecurityDepositAccountId" = deposit."Id";
        """;
}
