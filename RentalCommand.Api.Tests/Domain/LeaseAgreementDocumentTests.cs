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
/// Tests the "5-question generator": generating a residential lease agreement PDF from a lease's
/// captured terms, storing it as a StoredFile, and streaming it back. Uses SQLite in-memory.
/// </summary>
public sealed class LeaseAgreementDocumentTests : IDisposable
{
    private const int PortfolioId = 1;

    private readonly SqliteConnection _conn;
    private readonly RentalCommandDbContext _db;
    private readonly InMemoryFileStorage _storage = new();
    private readonly LeaseService _sut;

    public LeaseAgreementDocumentTests()
    {
        // QuestPDF refuses to render until a license tier is selected; Program.cs sets this for the
        // running app, so the test process must set it too (mirrors the other PDF test suites).
        QuestPDF.Settings.License = QuestPDF.Infrastructure.LicenseType.Community;

        _conn = new SqliteConnection("DataSource=:memory:");
        _conn.Open();

        var options = new DbContextOptionsBuilder<RentalCommandDbContext>()
            .UseSqlite(_conn)
            .Options;

        _db = new LeaseDocumentTestDbContext(options);
        _db.Database.EnsureCreated();

        _db.Portfolios.Add(new Portfolio
        {
            Id = PortfolioId,
            Name = "Test Portfolio",
            ManagementCompanyName = "Acme Property Management LLC",
            TimeZone = "UTC",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        _db.SaveChanges();

        _sut = new LeaseService(
            _db,
            new NoopDataUpdateService(),
            _storage,
            new LeaseAgreementPdfGenerator(),
            new RentalCommand.Api.Services.AuditTrailService(_db, new RentalCommand.Data.Auditing.AuditScope()),
            NullLogger<LeaseService>.Instance,
            new LeaseAgreementRenderer(
                _db,
                _storage,
                new LeaseAgreementPdfGenerator(),
                NullLogger<LeaseAgreementRenderer>.Instance));
    }

    public void Dispose()
    {
        _db.Dispose();
        _conn.Dispose();
    }

    [Fact]
    public async Task GenerateDocumentAsync_StoresPdf_AndReturnsRef()
    {
        var lease = SeedLeaseWithGraph();

        var doc = await _sut.GenerateDocumentAsync(PortfolioId, lease.Id);

        doc.Should().NotBeNull();
        doc!.LeaseId.Should().Be(lease.Id);
        doc.FileSize.Should().BeGreaterThan(0);
        doc.DownloadUrl.Should().Be($"/api/v1/leases/{lease.Id}/document");

        // A StoredFile row attached to the lease was created.
        var stored = await _db.StoredFiles
            .FirstOrDefaultAsync(f => f.Id == doc.StoredFileId);
        stored.Should().NotBeNull();
        stored!.EntityType.Should().Be("Lease");
        stored.EntityId.Should().Be(lease.Id);
        stored.ContentType.Should().Be("application/pdf");
        stored.PortfolioId.Should().Be(PortfolioId);
    }

    [Fact]
    public async Task GenerateDocumentAsync_ActiveDefaultOverlayTemplate_StampsLeaseValuesAndFreezesTemplateVersion()
    {
        var lease = SeedLeaseWithGraph();
        var template = await SeedActiveOverlayTemplateAsync(lease.PropertyId);

        var doc = await _sut.GenerateDocumentAsync(PortfolioId, lease.Id);

        doc.Should().NotBeNull();

        var reloaded = await _db.Leases.AsNoTracking().FirstAsync(l => l.Id == lease.Id);
        reloaded.DocumentTemplateId.Should().Be(template.Id);
        reloaded.DocumentTemplateVersion.Should().Be(template.Version);

        var text = await ExtractGeneratedPdfTextAsync(lease.Id);
        text.Should().Contain("Custom Landlord Lease");
        text.Should().Contain("Marcus");
        text.Should().Contain("Williams");
        text.Should().Contain("$1,450.00");
        text.Should().NotContain("Residential Lease Agreement", "the landlord's exact PDF should be the rendered source");
    }

    [Fact]
    public async Task PreviewLeasePdfAsync_UsesTemplatePdfAndLeaseValues()
    {
        var lease = SeedLeaseWithGraph();
        var template = await SeedActiveOverlayTemplateAsync(lease.PropertyId);
        var templates = new DocumentTemplateService(_db, new DocumentTemplateFieldCatalog(), _storage);

        var result = await templates.PreviewLeasePdfAsync(PortfolioId, template.Id, lease.Id);

        result.Outcome.Should().Be(DocumentTemplateOperationOutcome.Success);
        result.Value!.FileName.Should().Be($"lease-template-{template.Id}-lease-{lease.Id}-preview.pdf");

        var text = RentalCommand.Api.Scanning.PdfTextExtractor.TryExtractText(result.Value.PdfBytes);
        text.Should().NotBeNull();
        text!.Should().Contain("Custom Landlord Lease");
        text.Should().Contain("Marcus");
        text.Should().Contain("Williams");
        text.Should().Contain("$1,450.00");
        text.Should().NotContain("Residential Lease Agreement");
    }

    [Fact]
    public void RenderOverlayPreview_WhiteoutFieldsArePaintedBeforeLeaseValues()
    {
        var lease = SeedLeaseWithGraph();
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
        var data = new LeaseAgreementData
        {
            Lease = lease,
            LandlordName = "Acme Property Management LLC",
            TenantName = "Marcus Williams",
            PropertyName = "Maple Court",
            PropertyAddress = "10 Maple Ct, Columbus, OH 43215",
            UnitNumber = "2B",
            State = "OH",
        };

        var sourcePdf = LeaseTemplateFixturePdf();
        var sourceContent = FirstPageContent(sourcePdf);
        var rendered = LeaseAgreementRenderer.RenderOverlayPreview(sourcePdf, fields, data);
        var content = FirstPageContent(rendered);

        CountOccurrences(content, "1 1 1 rg").Should().BeGreaterThan(
            CountOccurrences(sourceContent, "1 1 1 rg"),
            "whiteout fields must draw a white filled rectangle even though they have no lease value");
    }

    [Fact]
    public async Task GetDocumentAsync_AfterGenerate_StreamsNonEmptyPdf()
    {
        var lease = SeedLeaseWithGraph();
        await _sut.GenerateDocumentAsync(PortfolioId, lease.Id);

        var file = await _sut.GetDocumentAsync(PortfolioId, lease.Id);

        file.Should().NotBeNull();
        file!.Value.ContentType.Should().Be("application/pdf");

        using var ms = new MemoryStream();
        await file.Value.Stream.CopyToAsync(ms);
        var bytes = ms.ToArray();
        bytes.Length.Should().BeGreaterThan(0);
        // Real PDF files start with the "%PDF" magic header.
        Encoding.ASCII.GetString(bytes, 0, 4).Should().Be("%PDF");
    }

    [Fact]
    public async Task GetDocumentAsync_NoGeneratedDocument_ReturnsNull()
    {
        var lease = SeedLeaseWithGraph();

        var file = await _sut.GetDocumentAsync(PortfolioId, lease.Id);

        file.Should().BeNull();
    }

    [Theory]
    [InlineData("scan-lease.pdf", "application/pdf")]
    [InlineData("scan-lease.jpg", "image/jpeg")]
    public async Task SourceScanAttachment_DoesNotCountAsGeneratedAgreement(string fileName, string contentType)
    {
        var lease = SeedLeaseWithGraph();
        var storageKey = await _storage.UploadAsync(
            new MemoryStream(Encoding.UTF8.GetBytes("source scan bytes")),
            fileName,
            contentType);
        _db.StoredFiles.Add(new StoredFile
        {
            PortfolioId = PortfolioId,
            FileName = fileName,
            FilePath = storageKey,
            ContentType = contentType,
            FileSize = 17,
            EntityType = "Lease",
            EntityId = lease.Id,
            UploadedAt = DateTime.UtcNow,
        });
        await _db.SaveChangesAsync();

        var status = await _sut.GetDocumentStatusAsync(PortfolioId, lease.Id);
        var file = await _sut.GetDocumentAsync(PortfolioId, lease.Id);

        status.Should().NotBeNull();
        status!.HasDocument.Should().BeFalse();
        status.StoredFileId.Should().BeNull();
        status.DownloadUrl.Should().BeNull();
        file.Should().BeNull();
    }

    [Fact]
    public async Task GetAsync_GeneratedAgreementOnly_DoesNotAdvertiseScannedSourceDocument()
    {
        var lease = SeedLeaseWithGraph();
        await _sut.GenerateDocumentAsync(PortfolioId, lease.Id);

        var detail = await _sut.GetAsync(PortfolioId, lease.Id);

        detail.Should().NotBeNull();
        detail!.HasScan.Should().BeFalse();
        detail.ScanIsImage.Should().BeFalse();
    }

    [Fact]
    public async Task GetDocumentStatusAsync_NoGeneratedDocument_ReturnsMissingStatus()
    {
        var lease = SeedLeaseWithGraph();

        var status = await _sut.GetDocumentStatusAsync(PortfolioId, lease.Id);

        status.Should().NotBeNull();
        status!.LeaseId.Should().Be(lease.Id);
        status.HasDocument.Should().BeFalse();
        status.StoredFileId.Should().BeNull();
        status.DownloadUrl.Should().BeNull();
    }

    [Fact]
    public async Task GetDocumentStatusAsync_AfterGenerate_ReturnsLatestDocumentMetadata()
    {
        var lease = SeedLeaseWithGraph();
        var doc = await _sut.GenerateDocumentAsync(PortfolioId, lease.Id);

        var status = await _sut.GetDocumentStatusAsync(PortfolioId, lease.Id);

        status.Should().NotBeNull();
        status!.HasDocument.Should().BeTrue();
        status.StoredFileId.Should().Be(doc!.StoredFileId);
        status.FileName.Should().Be(doc.FileName);
        status.FileSize.Should().Be(doc.FileSize);
        status.DownloadUrl.Should().Be($"/api/v1/leases/{lease.Id}/document");
        status.GeneratedAt.Should().Be(doc.GeneratedAt);
    }

    [Fact]
    public async Task GenerateDocumentAsync_LeaseNotInPortfolio_ReturnsNull()
    {
        var doc = await _sut.GenerateDocumentAsync(PortfolioId, id: 99999);

        doc.Should().BeNull();
    }

    [Fact]
    public async Task GetDocumentStatusAsync_LeaseNotInPortfolio_ReturnsNull()
    {
        var status = await _sut.GetDocumentStatusAsync(PortfolioId, id: 99999);

        status.Should().BeNull();
    }

    // ---- State-specific clauses (StateLeaseRules) ----

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
    [InlineData("")]
    [InlineData(null)]
    public void StateLeaseRules_BlankState_FallsBackToGenericWithNoCitation(string? input)
    {
        var rules = StateLeaseRules.For(input);

        rules.StatuteCitation.Should().BeNull();
        rules.GoverningLawLabel.Should().Be("the state where the Premises are located");
    }

    // ---- Rendered PDF: clauses, disclosures, disclaimer ----

    [Fact]
    public async Task Generate_OhioPre1978_RendersStateClausesLeadDisclosureAndDisclaimer()
    {
        var lease = SeedLeaseWithGraph(state: "OH", yearBuilt: 1925);
        await _sut.GenerateDocumentAsync(PortfolioId, lease.Id);

        var text = await ExtractGeneratedPdfTextAsync(lease.Id);

        // Real governing law instead of "State of XX".
        text.Should().Contain("State of Ohio");
        text.Should().NotContain("State of XX");
        // State-specific deposit + entry figures.
        text.Should().Contain("thirty (30) days");
        text.Should().Contain("twenty-four (24) hours");
        // Federal lead-paint disclosure (pre-1978).
        text.Should().Contain("Lead-Based Paint");
        text.Should().Contain("Protect Your Family From Lead");
        // Visible in-body disclaimer.
        text.Should().Contain("Required Disclosures");
        text.Should().Contain("NOT legal advice");
    }

    [Fact]
    public async Task Generate_Post1978Property_OmitsLeadPaintDisclosure()
    {
        var lease = SeedLeaseWithGraph(state: "OH", yearBuilt: 1995);
        await _sut.GenerateDocumentAsync(PortfolioId, lease.Id);

        var text = await ExtractGeneratedPdfTextAsync(lease.Id);

        text.Should().NotContain("Lead-Based Paint");
        // Disclaimer is always present regardless of year.
        text.Should().Contain("Required Disclosures");
    }

    [Fact]
    public async Task Generate_UnknownYearBuilt_OmitsLeadPaintDisclosure()
    {
        var lease = SeedLeaseWithGraph(state: "OH", yearBuilt: null);
        await _sut.GenerateDocumentAsync(PortfolioId, lease.Id);

        var text = await ExtractGeneratedPdfTextAsync(lease.Id);

        text.Should().NotContain("Lead-Based Paint");
    }

    /// <summary>Reads the stored lease PDF back and extracts its selectable text via PdfPig.</summary>
    private async Task<string> ExtractGeneratedPdfTextAsync(int leaseId)
    {
        var file = await _sut.GetDocumentAsync(PortfolioId, leaseId);
        file.Should().NotBeNull();

        using var ms = new MemoryStream();
        await file!.Value.Stream.CopyToAsync(ms);
        var text = RentalCommand.Api.Scanning.PdfTextExtractor.TryExtractText(ms.ToArray());
        text.Should().NotBeNull("the generated lease PDF should contain selectable text");
        return text!;
    }

    private Lease SeedLeaseWithGraph(string state = "OH", int? yearBuilt = null)
    {
        var property = new Property
        {
            PortfolioId = PortfolioId,
            Name = "Maple Court",
            AddressLine1 = "10 Maple Ct",
            City = "Columbus",
            State = state,
            PostalCode = "43215",
            YearBuilt = yearBuilt,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        _db.Properties.Add(property);
        _db.SaveChanges();

        var unit = new Unit
        {
            PropertyId = property.Id,
            UnitNumber = "2B",
            Bedrooms = 2,
            Bathrooms = 1,
            MarketRent = 1450m,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        _db.Units.Add(unit);

        var tenant = new Tenant
        {
            PortfolioId = PortfolioId,
            FirstName = "Marcus",
            LastName = "Williams",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        _db.Tenants.Add(tenant);
        _db.SaveChanges();

        var lease = new Lease
        {
            PortfolioId = PortfolioId,
            PropertyId = property.Id,
            UnitId = unit.Id,
            TenantId = tenant.Id,
            LeaseNumber = "L-2026-7",
            Status = LeaseStatus.Active,
            StartDate = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            EndDate = new DateTime(2026, 12, 31, 0, 0, 0, DateTimeKind.Utc),
            MonthlyRent = 1450m,
            SecurityDeposit = 1450m,
            LateFeeAmount = 75m,
            RentDueDay = 1,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        _db.Leases.Add(lease);
        _db.SaveChanges();
        return lease;
    }

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

    private sealed class NoopDataUpdateService : IDataUpdateService
    {
        public Task BroadcastEntityUpdateAsync(int portfolioId, string entityType, int entityId, object data, CancellationToken ct = default)
            => Task.CompletedTask;

        public Task BroadcastEntityDeleteAsync(int portfolioId, string entityType, int entityId, CancellationToken ct = default)
            => Task.CompletedTask;
    }

    private sealed class InMemoryFileStorage : IFileStorage
    {
        private readonly Dictionary<string, byte[]> _files = new();

        public async Task<string> UploadAsync(Stream content, string fileName, string contentType, CancellationToken ct = default)
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
                throw new FileNotFoundException(path);
            return Task.FromResult<Stream>(new MemoryStream(bytes));
        }

        public Task DeleteAsync(string path, CancellationToken ct = default)
        {
            _files.Remove(path);
            return Task.CompletedTask;
        }
    }

    /// <summary>SQLite-compatible context: strips Postgres-only DDL the same way other suites do.</summary>
    private sealed class LeaseDocumentTestDbContext : RentalCommandDbContext
    {
        public LeaseDocumentTestDbContext(DbContextOptions<RentalCommandDbContext> options) : base(options) { }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);
            modelBuilder.Entity<ScanDraft>().Property(e => e.ExtractedFields).HasColumnType("TEXT");
            modelBuilder.Entity<AuditLog>().Property(e => e.OldValues).HasColumnType("TEXT");
            modelBuilder.Entity<AuditLog>().Property(e => e.NewValues).HasColumnType("TEXT");
            modelBuilder.Entity<OutboxMessage>().Property(e => e.Payload).HasColumnType("TEXT");
            modelBuilder.Entity<QueuedJob>().Property(e => e.Payload).HasColumnType("TEXT");
            modelBuilder.Entity<Lease>().ToTable("Leases");
        }
    }
}
