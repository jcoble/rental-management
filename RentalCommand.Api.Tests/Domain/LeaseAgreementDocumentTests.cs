using System.Security.Cryptography;
using System.Text;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using PdfSharp.Pdf.Content;
using PdfSharp.Pdf.IO;
using QuestPDF.Fluent;
using RentalCommand.Api.Services.Domain;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Interfaces;
using RentalCommand.Data;

namespace RentalCommand.Api.Tests.Domain;

/// <summary>
/// Covers agreement rendering and the canonical legal-artifact graph. Issuance and execution belong
/// to LeaseManagement/LeaseAgreement; the retired mutable Lease document endpoints are intentionally
/// not represented here.
/// </summary>
public sealed class LeaseAgreementDocumentTests : IDisposable
{
    private const int PortfolioId = 1;
    private const int ActorUserId = 7;

    private readonly SqliteConnection _conn;
    private readonly RentalCommandDbContext _db;
    private readonly InMemoryFileStorage _storage = new();
    private readonly LeaseAgreementRenderer _renderer;

    public LeaseAgreementDocumentTests()
    {
        QuestPDF.Settings.License = QuestPDF.Infrastructure.LicenseType.Community;

        _conn = new SqliteConnection("DataSource=:memory:");
        _conn.Open();

        var options = new DbContextOptionsBuilder<RentalCommandDbContext>()
            .UseSqlite(_conn)
            .Options;

        _db = new LeaseDocumentTestDbContext(options);
        _db.Database.EnsureCreated();

        var now = DateTime.UtcNow;
        _db.Portfolios.Add(new Portfolio
        {
            Id = PortfolioId,
            Name = "Test Portfolio",
            ManagementCompanyName = "Acme Property Management LLC",
            TimeZone = "UTC",
            CreatedAt = now,
            UpdatedAt = now,
        });
        _db.Users.Add(new ApplicationUser
        {
            Id = ActorUserId,
            UserName = "agreement-document-test-user",
            NormalizedUserName = "AGREEMENT-DOCUMENT-TEST-USER",
            Email = "agreement-documents@example.test",
            NormalizedEmail = "AGREEMENT-DOCUMENTS@EXAMPLE.TEST",
            DisplayName = "Agreement Document Test User",
            PortfolioId = PortfolioId,
            SecurityStamp = Guid.NewGuid().ToString("N"),
            ConcurrencyStamp = Guid.NewGuid().ToString("N"),
            CreatedAt = now,
        });
        _db.SaveChanges();

        _renderer = new LeaseAgreementRenderer(
            _db,
            _storage,
            new LeaseAgreementPdfGenerator(),
            NullLogger<LeaseAgreementRenderer>.Instance);
    }

    public void Dispose()
    {
        _db.Dispose();
        _conn.Dispose();
    }

    [Fact]
    public async Task RenderAsync_WithoutOverlay_ReturnsBuiltInAgreementWithoutTemplateProvenance()
    {
        var fixture = await SeedExecutedAgreementGraphAsync();

        var rendered = await _renderer.RenderAsync(PortfolioId, fixture.RenderData);

        Encoding.ASCII.GetString(rendered.PdfBytes, 0, 4).Should().Be("%PDF");
        rendered.DocumentTemplateId.Should().BeNull();
        rendered.DocumentTemplateVersion.Should().BeNull();
        ExtractPdfText(rendered.PdfBytes).Should().Contain("Residential Lease Agreement");
    }

    [Fact]
    public async Task RenderAsync_ActiveOverlay_StampsAgreementSnapshotAndReturnsTemplateProvenance()
    {
        var fixture = await SeedExecutedAgreementGraphAsync();
        var template = await SeedActiveOverlayTemplateAsync(fixture.Property.Id);

        var rendered = await _renderer.RenderAsync(PortfolioId, fixture.RenderData);

        rendered.DocumentTemplateId.Should().Be(template.Id);
        rendered.DocumentTemplateVersion.Should().Be(template.Version);
        var text = ExtractPdfText(rendered.PdfBytes);
        text.Should().Contain("Custom Landlord Lease");
        text.Should().Contain("Marcus");
        text.Should().Contain("Williams");
        text.Should().Contain("$1,450.00");
        text.Should().NotContain("Residential Lease Agreement");
    }

    [Fact]
    public void PreviewLeasePdfAsync_UsesCanonicalAgreementQuery()
    {
        var templates = new DocumentTemplateService(_db, new DocumentTemplateFieldCatalog(), _storage, TimeProvider.System);

        var sql = templates.BuildAgreementPreviewQuery(PortfolioId, 123).ToQueryString();

        sql.Should().Contain("LeaseAgreements");
        sql.Should().Contain("LeaseManagements");
        sql.Should().Contain("LeaseAgreementSigners");
        sql.Should().NotContain("FROM \"Leases\"");
        sql.Should().NotContain("LeaseTenants");
    }

    [Fact]
    public async Task ExecutedAgreement_IsResolvedThroughCanonicalArtifactAndStoredFile()
    {
        var fixture = await SeedExecutedAgreementGraphAsync();

        var resolved = await _db.LeaseAgreements
            .AsNoTracking()
            .Where(agreement => agreement.PortfolioId == PortfolioId
                && agreement.LeaseManagementId == fixture.Management.Id
                && agreement.FullyExecutedAtUtc != null
                && agreement.VoidedAtUtc == null
                && agreement.ExecutedArtifact != null
                && agreement.ExecutedArtifact.ArtifactKind == LegalDocumentArtifactKind.ExecutedAgreement
                && agreement.ExecutedArtifact.StoredFile != null
                && agreement.ExecutedArtifact.StoredFile.PortfolioId == PortfolioId
                && agreement.ExecutedArtifact.StoredFile.DeletedAt == null)
            .Select(agreement => new
            {
                agreement.Id,
                agreement.AgreementNumber,
                agreement.ExecutedArtifactId,
                StoredFileId = agreement.ExecutedArtifact!.StoredFileId,
                agreement.ExecutedArtifact.StoredFile!.FileName,
            })
            .SingleAsync();

        resolved.Id.Should().Be(fixture.Agreement.Id);
        resolved.AgreementNumber.Should().Be("A-2026-7");
        resolved.ExecutedArtifactId.Should().Be(fixture.ExecutedArtifact.Id);
        resolved.StoredFileId.Should().Be(fixture.ExecutedFile.Id);
        resolved.FileName.Should().Be("A-2026-7-executed.pdf");
    }

    [Fact]
    public async Task SourceScanAttachment_DoesNotReplaceExecutedAgreementArtifact()
    {
        var fixture = await SeedExecutedAgreementGraphAsync();
        var sourceKey = await _storage.UploadAsync(
            new MemoryStream(Encoding.UTF8.GetBytes("source scan bytes")),
            "source-scan.pdf",
            "application/pdf");
        _db.StoredFiles.Add(new StoredFile
        {
            PortfolioId = PortfolioId,
            FileName = "source-scan.pdf",
            FilePath = sourceKey,
            ContentType = "application/pdf",
            FileSize = 17,
            EntityType = "LeaseAgreementSource",
            EntityId = fixture.Agreement.Id,
            UploadedAt = DateTime.UtcNow,
        });
        await _db.SaveChangesAsync();

        var executedFileId = await _db.LeaseAgreements
            .AsNoTracking()
            .Where(agreement => agreement.PortfolioId == PortfolioId
                && agreement.Id == fixture.Agreement.Id
                && agreement.ExecutedArtifact != null
                && agreement.ExecutedArtifact.ArtifactKind == LegalDocumentArtifactKind.ExecutedAgreement)
            .Select(agreement => agreement.ExecutedArtifact!.StoredFileId)
            .SingleAsync();

        executedFileId.Should().Be(fixture.ExecutedFile.Id);
    }

    [Fact]
    public async Task CanonicalFixture_PersistsPortfolioSafeRelationshipAccountPartyAndSigner()
    {
        var fixture = await SeedExecutedAgreementGraphAsync();

        var graph = await _db.LeaseManagements
            .AsNoTracking()
            .Where(management => management.Id == fixture.Management.Id
                && management.PortfolioId == PortfolioId)
            .Select(management => new
            {
                management.PropertyId,
                management.UnitId,
                UnitPortfolioId = management.Unit!.PortfolioId,
                AccountLeaseManagementId = management.TenantAccount!.LeaseManagementId,
                PrimaryParties = management.Parties.Count(party =>
                    party.Role == LeaseManagementPartyRole.PrimaryTenant),
                RequiredSigners = management.Agreements
                    .SelectMany(agreement => agreement.Signers)
                    .Count(signer => signer.IsRequired),
            })
            .SingleAsync();

        graph.PropertyId.Should().Be(fixture.Property.Id);
        graph.UnitId.Should().Be(fixture.Unit.Id);
        graph.UnitPortfolioId.Should().Be(PortfolioId);
        graph.AccountLeaseManagementId.Should().Be(fixture.Management.Id);
        graph.PrimaryParties.Should().Be(1);
        graph.RequiredSigners.Should().Be(1);
    }

    [Fact]
    public void RenderOverlayPreview_WhiteoutFieldsArePaintedBeforeAgreementValues()
    {
        var fields = new List<DocumentTemplateField>
        {
            new()
            {
                Id = 1,
                FieldKey = "pdf.whiteout",
                Label = "Erase PDF text",
                Kind = DocumentTemplateFieldKind.Whiteout,
                PageNumber = 1,
                XPct = 0.15,
                YPct = 0.31,
                WidthPct = 0.50,
                HeightPct = 0.06,
                SortOrder = 1,
            },
            new()
            {
                Id = 2,
                FieldKey = "tenant.fullName",
                Label = "Tenant full name",
                Kind = DocumentTemplateFieldKind.Text,
                PageNumber = 1,
                XPct = 0.18,
                YPct = 0.34,
                WidthPct = 0.62,
                HeightPct = 0.04,
                SortOrder = 2,
            },
        };
        var data = RenderData("OH", null);
        var sourcePdf = LeaseTemplateFixturePdf();
        var sourceContent = FirstPageContent(sourcePdf);

        var rendered = LeaseAgreementRenderer.RenderOverlayPreview(sourcePdf, fields, data);
        var content = FirstPageContent(rendered);

        CountOccurrences(content, "1 1 1 rg").Should().BeGreaterThan(
            CountOccurrences(sourceContent, "1 1 1 rg"),
            "whiteout fields must draw a white filled rectangle even though they have no agreement value");
    }

    [Fact]
    public void StateLeaseRules_Ohio_HasRealStatutoryFigures()
    {
        var rules = StateLeaseRules.For("OH");

        rules.GoverningLawLabel.Should().Be("the State of Ohio");
        rules.StatuteCitation.Should().Contain("5321");
        rules.DepositReturnText.Should().Contain("thirty (30) days");
        rules.EntryNoticeText.Should().Contain("twenty-four (24) hours");
    }

    [Theory]
    [InlineData("oh")]
    [InlineData(" OH ")]
    [InlineData("Oh")]
    public void StateLeaseRules_For_NormalizesCaseAndWhitespace(string input)
    {
        StateLeaseRules.For(input).GoverningLawLabel.Should().Be("the State of Ohio");
    }

    [Fact]
    public void StateLeaseRules_UnknownState_FallsBackToGenericButNamesJurisdiction()
    {
        var rules = StateLeaseRules.For("ZZ");

        rules.StateCode.Should().Be("ZZ");
        rules.GoverningLawLabel.Should().Be("the State of ZZ");
        rules.StatuteCitation.Should().BeNull();
        rules.DepositReturnText.Should().Contain("applicable law");
    }

    [Theory]
    [InlineData(1925, true)]
    [InlineData(1995, false)]
    [InlineData(null, false)]
    public async Task RenderAsync_LeadPaintDisclosureFollowsPropertyYear(int? yearBuilt, bool expected)
    {
        var fixture = await SeedExecutedAgreementGraphAsync(yearBuilt: yearBuilt);

        var rendered = await _renderer.RenderAsync(PortfolioId, fixture.RenderData);
        var text = ExtractPdfText(rendered.PdfBytes);

        if (expected)
        {
            text.Should().Contain("Lead-Based Paint");
            text.Should().Contain("Protect Your Family From Lead");
        }
        else
        {
            text.Should().NotContain("Lead-Based Paint");
        }

        text.Should().Contain("Required Disclosures");
    }

    private async Task<CanonicalAgreementFixture> SeedExecutedAgreementGraphAsync(
        string state = "OH",
        int? yearBuilt = null)
    {
        var now = DateTime.UtcNow;
        var property = new Property
        {
            PortfolioId = PortfolioId,
            Name = "Maple Court",
            AddressLine1 = "10 Maple Ct",
            City = "Columbus",
            State = state,
            PostalCode = "43215",
            YearBuilt = yearBuilt,
            CreatedAt = now,
            UpdatedAt = now,
        };
        var unit = new Unit
        {
            PortfolioId = PortfolioId,
            Property = property,
            UnitNumber = "2B",
            Bedrooms = 2,
            Bathrooms = 1,
            MarketRent = 1450m,
            CreatedAt = now,
            UpdatedAt = now,
        };
        var tenant = new Tenant
        {
            PortfolioId = PortfolioId,
            FirstName = "Marcus",
            LastName = "Williams",
            Email = "marcus@example.test",
            CreatedAt = now,
            UpdatedAt = now,
        };
        var management = new LeaseManagement
        {
            PublicId = Guid.NewGuid(),
            PortfolioId = PortfolioId,
            Property = property,
            Unit = unit,
            RelationshipNumber = "LM-2026-7",
            PlannedPossessionAtUtc = new DateTime(2026, 1, 1, 14, 0, 0, DateTimeKind.Utc),
            PossessionGivenAtUtc = new DateTime(2026, 1, 1, 14, 0, 0, DateTimeKind.Utc),
            CreatedAtUtc = now,
            CreatedByUserId = ActorUserId,
            UpdatedAtUtc = now,
            RowVersion = Guid.NewGuid(),
        };
        var party = new LeaseManagementParty
        {
            PortfolioId = PortfolioId,
            LeaseManagement = management,
            Tenant = tenant,
            Role = LeaseManagementPartyRole.PrimaryTenant,
            EffectiveFrom = new DateOnly(2026, 1, 1),
            ChangeReason = "Initial agreement",
            CreatedAtUtc = now,
            CreatedByUserId = ActorUserId,
        };
        var account = new TenantAccount
        {
            PublicId = Guid.NewGuid(),
            PortfolioId = PortfolioId,
            LeaseManagement = management,
            AccountNumber = "TA-2026-7",
            Currency = "USD",
            OpenedAtUtc = now,
            CreatedAtUtc = now,
            CreatedByUserId = ActorUserId,
        };
        var agreement = new LeaseAgreement
        {
            PublicId = Guid.NewGuid(),
            PortfolioId = PortfolioId,
            LeaseManagement = management,
            VersionNumber = 1,
            AgreementNumber = "A-2026-7",
            ChangeType = LeaseAgreementChangeType.Initial,
            TermType = LeaseAgreementTermType.FixedTerm,
            TermStartOn = new DateOnly(2026, 1, 1),
            TermEndOn = new DateOnly(2026, 12, 31),
            GoverningFromOn = new DateOnly(2026, 1, 1),
            BaseRentAmount = 1450m,
            RentDueDay = 1,
            SecurityDepositObligation = 1450m,
            LateFeeAmount = 75m,
            GracePeriodDays = 5,
            Currency = "USD",
            TermsSchemaVersion = 1,
            TermsPayload = "{}",
            CreatedAtUtc = now,
            CreatedByUserId = ActorUserId,
            UpdatedAtUtc = now,
            DraftRevision = 1,
        };
        var signer = new LeaseAgreementSigner
        {
            PortfolioId = PortfolioId,
            LeaseAgreement = agreement,
            LeaseManagementParty = party,
            Tenant = tenant,
            SignerRole = LeaseLegalSignerRole.PrimaryTenant,
            NameSnapshot = "Marcus Williams",
            EmailSnapshot = "marcus@example.test",
            SigningOrder = 1,
            IsRequired = true,
        };

        _db.AddRange(property, unit, tenant, management, party, account, agreement, signer);
        await _db.SaveChangesAsync();

        var renderData = RenderData(state, yearBuilt) with { PropertyId = property.Id };
        var pdf = new LeaseAgreementPdfGenerator().Generate(renderData);
        var issuedFile = await AddStoredPdfAsync(agreement.Id, "A-2026-7-issued.pdf", pdf, now);
        var executedFile = await AddStoredPdfAsync(agreement.Id, "A-2026-7-executed.pdf", pdf, now);
        var issuedArtifact = Artifact(issuedFile, pdf, LegalDocumentArtifactKind.IssuedAgreement, now);
        var executedArtifact = Artifact(executedFile, pdf, LegalDocumentArtifactKind.ExecutedAgreement, now);
        _db.LegalDocumentArtifacts.AddRange(issuedArtifact, executedArtifact);
        await _db.SaveChangesAsync();

        agreement.IssuedArtifactId = issuedArtifact.Id;
        agreement.IssuedAtUtc = now;
        agreement.ExecutedArtifactId = executedArtifact.Id;
        agreement.FullyExecutedAtUtc = now.AddMinutes(5);
        await _db.SaveChangesAsync();

        return new CanonicalAgreementFixture(
            property,
            unit,
            management,
            agreement,
            executedFile,
            executedArtifact,
            renderData);
    }

    private async Task<StoredFile> AddStoredPdfAsync(
        int agreementId,
        string fileName,
        byte[] bytes,
        DateTime now)
    {
        var storageKey = await _storage.UploadAsync(
            new MemoryStream(bytes),
            fileName,
            "application/pdf");
        var storedFile = new StoredFile
        {
            PortfolioId = PortfolioId,
            FileName = fileName,
            FilePath = storageKey,
            ContentType = "application/pdf",
            FileSize = bytes.Length,
            EntityType = "LeaseAgreement",
            EntityId = agreementId,
            UploadedAt = now,
        };
        _db.StoredFiles.Add(storedFile);
        await _db.SaveChangesAsync();
        return storedFile;
    }

    private static LegalDocumentArtifact Artifact(
        StoredFile file,
        byte[] bytes,
        LegalDocumentArtifactKind kind,
        DateTime now)
    {
        return new LegalDocumentArtifact
        {
            PublicId = Guid.NewGuid(),
            PortfolioId = PortfolioId,
            StoredFileId = file.Id,
            ArtifactKind = kind,
            StorageKey = file.FilePath,
            FileName = file.FileName,
            ContentType = file.ContentType,
            ByteLength = file.FileSize,
            ContentSha256 = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(),
            CreatedAtUtc = now,
            CreatedByUserId = ActorUserId,
        };
    }

    private static LeaseAgreementRenderData RenderData(string state, int? yearBuilt) => new()
    {
        PropertyId = 0,
        AgreementNumber = "A-2026-7",
        TermStartOn = new DateOnly(2026, 1, 1),
        TermEndOn = new DateOnly(2026, 12, 31),
        BaseRentAmount = 1450m,
        SecurityDepositObligation = 1450m,
        LateFeeAmount = 75m,
        RentDueDay = 1,
        LandlordName = "Acme Property Management LLC",
        TenantName = "Marcus Williams",
        TenantEmail = "marcus@example.test",
        PropertyName = "Maple Court",
        PropertyAddress = $"10 Maple Ct, Columbus, {state} 43215",
        UnitNumber = "2B",
        State = state,
        YearBuilt = yearBuilt,
    };

    private async Task<DocumentTemplate> SeedActiveOverlayTemplateAsync(int propertyId)
    {
        var pdfBytes = LeaseTemplateFixturePdf();
        var storageKey = await _storage.UploadAsync(
            new MemoryStream(pdfBytes),
            "custom-landlord-lease.pdf",
            "application/pdf");
        var stored = new StoredFile
        {
            PortfolioId = PortfolioId,
            FileName = "custom-landlord-lease.pdf",
            FilePath = storageKey,
            ContentType = "application/pdf",
            FileSize = pdfBytes.Length,
            EntityType = "DocumentTemplate",
            UploadedAt = DateTime.UtcNow,
        };
        var template = new DocumentTemplate
        {
            PortfolioId = PortfolioId,
            Kind = DocumentTemplateKind.Lease,
            Status = DocumentTemplateStatus.Active,
            RenderMode = DocumentTemplateRenderMode.Overlay,
            Name = "Custom landlord lease",
            OriginalStoredFile = stored,
            DefaultForPortfolio = true,
            PropertyId = propertyId,
            Version = 7,
            CreatedAtUtc = DateTime.UtcNow,
            UpdatedAtUtc = DateTime.UtcNow,
        };
        template.Fields.Add(new DocumentTemplateField
        {
            FieldKey = "tenant.fullName",
            Label = "Tenant full name",
            Kind = DocumentTemplateFieldKind.Text,
            PageNumber = 1,
            XPct = 0.18,
            YPct = 0.34,
            WidthPct = 0.62,
            HeightPct = 0.04,
            SortOrder = 1,
        });
        template.Fields.Add(new DocumentTemplateField
        {
            FieldKey = "lease.monthlyRent",
            Label = "Monthly rent",
            Kind = DocumentTemplateFieldKind.Currency,
            PageNumber = 1,
            XPct = 0.18,
            YPct = 0.42,
            WidthPct = 0.24,
            HeightPct = 0.04,
            SortOrder = 2,
        });

        _db.DocumentTemplates.Add(template);
        await _db.SaveChangesAsync();
        stored.EntityId = template.Id;
        await _db.SaveChangesAsync();
        return template;
    }

    private static byte[] LeaseTemplateFixturePdf()
    {
        var document = QuestPDF.Fluent.Document.Create(container =>
        {
            container.Page(page =>
            {
                page.Size(QuestPDF.Helpers.PageSizes.A4);
                page.Margin(40);
                page.DefaultTextStyle(t => t.FontSize(12));
                page.Content().Column(col =>
                {
                    col.Item().Text("Custom Landlord Lease").FontSize(18).Bold();
                    col.Item().PaddingTop(60).Text("Tenant:");
                    col.Item().PaddingTop(30).Text("Monthly rent:");
                });
            });
        });

        return document.GeneratePdf();
    }

    private static string ExtractPdfText(byte[] bytes)
    {
        var text = RentalCommand.Api.Scanning.PdfTextExtractor.TryExtractText(bytes);
        text.Should().NotBeNull("the rendered agreement should contain selectable text");
        return text!;
    }

    private static string FirstPageContent(byte[] pdfBytes)
    {
        using var ms = new MemoryStream(pdfBytes);
        using var pdf = PdfReader.Open(ms, PdfDocumentOpenMode.Import);
        return Encoding.ASCII.GetString(ContentReader.ReadContent(pdf.Pages[0]).ToContent());
    }

    private static int CountOccurrences(string source, string value)
    {
        var count = 0;
        var index = 0;
        while ((index = source.IndexOf(value, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += value.Length;
        }

        return count;
    }

    private sealed record CanonicalAgreementFixture(
        Property Property,
        Unit Unit,
        LeaseManagement Management,
        LeaseAgreement Agreement,
        StoredFile ExecutedFile,
        LegalDocumentArtifact ExecutedArtifact,
        LeaseAgreementRenderData RenderData);

    private sealed class InMemoryFileStorage : IFileStorage
    {
        private readonly Dictionary<string, byte[]> _files = new();

        public async Task<string> UploadAsync(
            Stream content,
            string fileName,
            string contentType,
            CancellationToken ct = default)
        {
            using var ms = new MemoryStream();
            await content.CopyToAsync(ms, ct);
            var key = $"{Guid.NewGuid():N}_{fileName}";
            _files[key] = ms.ToArray();
            return key;
        }

        public Task<Stream> DownloadAsync(string path, CancellationToken ct = default)
        {
            if (!_files.TryGetValue(path, out var bytes))
            {
                throw new FileNotFoundException(path);
            }

            return Task.FromResult<Stream>(new MemoryStream(bytes));
        }

        public Task DeleteAsync(string path, CancellationToken ct = default)
        {
            _files.Remove(path);
            return Task.CompletedTask;
        }
    }

    private sealed class LeaseDocumentTestDbContext : RentalCommand.TestCommon.SqliteCompatibleRentalCommandDbContext
    {
        public LeaseDocumentTestDbContext(DbContextOptions<RentalCommandDbContext> options) : base(options) { }
    }
}
