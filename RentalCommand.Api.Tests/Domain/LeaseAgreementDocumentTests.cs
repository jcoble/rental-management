using System.Text;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
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
            NullLogger<LeaseService>.Instance);
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

    [Fact]
    public async Task GenerateDocumentAsync_LeaseNotInPortfolio_ReturnsNull()
    {
        var doc = await _sut.GenerateDocumentAsync(PortfolioId, id: 99999);

        doc.Should().BeNull();
    }

    private Lease SeedLeaseWithGraph()
    {
        var property = new Property
        {
            PortfolioId = PortfolioId,
            Name = "Maple Court",
            AddressLine1 = "10 Maple Ct",
            City = "Columbus",
            State = "OH",
            PostalCode = "43215",
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
