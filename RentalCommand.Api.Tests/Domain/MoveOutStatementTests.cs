using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;
using RentalCommand.Api.DTOs;
using RentalCommand.Api.Services.Domain;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Interfaces;
using RentalCommand.Data;

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
    public async Task GetMoveOutStatementAsync_UsesCanonicalFactsAndNetsDeductionReversals()
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

        var result = await CreateSut().GetMoveOutStatementAsync(PortfolioId, graph.DepositAccount.Id);

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
    }

    private SecurityDepositService CreateSut() => new(
        _ctx.Db,
        _storage,
        _pdf,
        Mock.Of<ILogger<SecurityDepositService>>(),
        TimeProvider.System);

    private CanonicalDepositGraph SeedCanonicalChain()
    {
        var createdAtUtc = new DateTime(2025, 6, 1, 12, 0, 0, DateTimeKind.Utc);
        var possessionReturnedAtUtc = new DateTime(2026, 6, 1, 18, 0, 0, DateTimeKind.Utc);
        var actor = new ApplicationUser
        {
            Id = ActorUserId,
            PortfolioId = PortfolioId,
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
            DocumentTemplateId = template.Id,
            DocumentTemplateVersion = 1,
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
        EntityType = "SecurityDeposit",
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
            _connection = new SqliteConnection($"Data Source=moveout-{Guid.NewGuid():N};Mode=Memory;Cache=Shared");
            _connection.Open();

            var options = new DbContextOptionsBuilder<RentalCommandDbContext>()
                .UseSqlite(_connection)
                .Options;
            Db = new MoveOutStatementTestDbContext(options);
            Db.Database.EnsureCreated();
            Db.Database.ExecuteSqlRaw(SecurityDepositBalanceViewSqlite);
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

        public void Dispose()
        {
            Db.Dispose();
            _connection.Dispose();
        }
    }

    private sealed class MoveOutStatementTestDbContext(DbContextOptions<RentalCommandDbContext> options)
        : RentalCommandDbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);

            foreach (var property in modelBuilder.Model.GetEntityTypes().SelectMany(entity => entity.GetProperties()))
            {
                if (property.GetColumnType() == "jsonb")
                    property.SetColumnType("TEXT");
            }

            // SQLite cannot parse PostgreSQL regex check constraints. These tables are populated
            // only with valid canonical facts in this test, so the production constraints remain
            // covered by PostgreSQL integration tests and are removed only from this local model.
            modelBuilder.Entity<TenantAccount>().ToTable("TenantAccounts");
            modelBuilder.Entity<TenantPaymentAttempt>().ToTable("TenantPaymentAttempts");
            modelBuilder.Entity<TenantLedgerEntry>().ToTable("TenantLedgerEntries");
            modelBuilder.Entity<SecurityDepositAccount>().ToTable("SecurityDepositAccounts");
            modelBuilder.Entity<SecurityDepositEntry>().ToTable("SecurityDepositEntries");
            modelBuilder.Entity<LegalDocumentArtifact>().ToTable("LegalDocumentArtifacts");
            modelBuilder.Entity<LeaseAgreement>().ToTable("LeaseAgreements");
            modelBuilder.Entity<LeaseAddendumFinancialEffect>().ToTable("LeaseAddendumFinancialEffects");
            modelBuilder.Entity<ApplicationFinancialAccount>().ToTable("ApplicationFinancialAccounts");
            modelBuilder.Entity<ApplicationFinancialEntry>().ToTable("ApplicationFinancialEntries");
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
