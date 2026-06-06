using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using RentalCommand.Api.DTOs;
using RentalCommand.Api.Services;
using RentalCommand.Api.Services.Domain;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Interfaces;
using RentalCommand.Data;

namespace RentalCommand.Api.Tests.Domain;

/// <summary>
/// Tests the lease e-sign workflow: gating (a DISABLED provider returns "not configured" and never
/// changes lease state), the webhook signed-event advancing a PendingSignature lease to Active +
/// EsignStatus.Signed, and the status snapshot shape. Uses SQLite in-memory.
/// </summary>
public sealed class LeaseEsignServiceTests : IDisposable
{
    private const int PortfolioId = 1;

    private readonly SqliteConnection _conn;
    private readonly RentalCommandDbContext _db;
    private readonly InMemoryFileStorage _storage = new();

    public LeaseEsignServiceTests()
    {
        QuestPDF.Settings.License = QuestPDF.Infrastructure.LicenseType.Community;

        _conn = new SqliteConnection("DataSource=:memory:");
        _conn.Open();

        var options = new DbContextOptionsBuilder<RentalCommandDbContext>()
            .UseSqlite(_conn)
            .Options;

        _db = new EsignTestDbContext(options);
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
    }

    public void Dispose()
    {
        _db.Dispose();
        _conn.Dispose();
    }

    [Fact]
    public async Task SendForSignature_ProviderDisabled_ReturnsNotConfigured_AndDoesNotChangeLease()
    {
        var lease = SeedLeaseWithGraph(LeaseStatus.Draft);
        var sut = CreateService(new FakeEsignProvider { Configured = false });

        var result = await sut.SendForSignatureAsync(PortfolioId, lease.Id, new SendForSignatureRequest(), changedByUserId: 7, ipAddress: null);

        result.Outcome.Should().Be(SendForSignatureOutcome.NotConfigured);

        var reloaded = await _db.Leases.AsNoTracking().FirstAsync(l => l.Id == lease.Id);
        reloaded.Status.Should().Be(LeaseStatus.Draft, "a disabled provider must never change lease state");
        reloaded.EsignStatus.Should().Be(EsignStatus.None);
        reloaded.EsignEnvelopeId.Should().BeNull();
    }

    [Fact]
    public async Task SendForSignature_ProviderEnabled_SetsSent_AndPendingSignature()
    {
        var lease = SeedLeaseWithGraph(LeaseStatus.Draft);
        var provider = new FakeEsignProvider { Configured = true, EnvelopeId = "sig_abc123" };
        var sut = CreateService(provider);

        var result = await sut.SendForSignatureAsync(PortfolioId, lease.Id, new SendForSignatureRequest(), changedByUserId: 7, ipAddress: "127.0.0.1");

        result.Outcome.Should().Be(SendForSignatureOutcome.Sent);
        result.Status!.EsignStatus.Should().Be(EsignStatus.Sent);
        result.Status.LeaseStatus.Should().Be(LeaseStatus.PendingSignature);
        result.Status.EnvelopeId.Should().Be("sig_abc123");
        provider.SendCalls.Should().Be(1);

        var reloaded = await _db.Leases.AsNoTracking().FirstAsync(l => l.Id == lease.Id);
        reloaded.Status.Should().Be(LeaseStatus.PendingSignature);
        reloaded.EsignStatus.Should().Be(EsignStatus.Sent);
        reloaded.EsignEnvelopeId.Should().Be("sig_abc123");
    }

    [Fact]
    public async Task SendForSignature_TenantHasNoEmail_ReturnsMissingSigner()
    {
        var lease = SeedLeaseWithGraph(LeaseStatus.Draft, tenantEmail: null);
        var sut = CreateService(new FakeEsignProvider { Configured = true });

        var result = await sut.SendForSignatureAsync(PortfolioId, lease.Id, new SendForSignatureRequest(), changedByUserId: 1, ipAddress: null);

        result.Outcome.Should().Be(SendForSignatureOutcome.MissingSigner);
    }

    [Fact]
    public async Task HandleSignedEvent_FlipsPendingSignatureLeaseToActive_AndStoresSignedDocument()
    {
        var lease = SeedLeaseWithGraph(LeaseStatus.PendingSignature);
        lease.EsignEnvelopeId = "sig_signed_1";
        lease.EsignStatus = EsignStatus.Sent;
        await _db.SaveChangesAsync();

        var provider = new FakeEsignProvider { Configured = true, SignedPdf = SamplePdf() };
        var sut = CreateService(provider);

        var advanced = await sut.HandleSignedEventAsync("sig_signed_1");

        advanced.Should().BeTrue();

        var reloaded = await _db.Leases.AsNoTracking().FirstAsync(l => l.Id == lease.Id);
        reloaded.EsignStatus.Should().Be(EsignStatus.Signed);
        reloaded.Status.Should().Be(LeaseStatus.Active, "a signed PendingSignature lease becomes Active");
        reloaded.SignedDocumentStoredFileId.Should().NotBeNull();

        var stored = await _db.StoredFiles.AsNoTracking().FirstAsync(f => f.Id == reloaded.SignedDocumentStoredFileId!.Value);
        stored.EntityType.Should().Be("Lease");
        stored.EntityId.Should().Be(lease.Id);
        stored.ContentType.Should().Be("application/pdf");

        // The signed document is now streamable.
        var doc = await sut.GetSignedDocumentAsync(PortfolioId, lease.Id);
        doc.Should().NotBeNull();
        doc!.Value.ContentType.Should().Be("application/pdf");
    }

    [Fact]
    public async Task HandleSignedEvent_UnknownEnvelope_IsNoOp()
    {
        var sut = CreateService(new FakeEsignProvider { Configured = true });

        var advanced = await sut.HandleSignedEventAsync("sig_does_not_exist");

        advanced.Should().BeFalse();
    }

    [Fact]
    public async Task HandleSignedEvent_OnNonPendingLease_SetsSignedButDoesNotChangeLeaseStatus()
    {
        // A lease that is already Active (e.g. signed out-of-band) should record the signature but its
        // overall status is only flipped FROM PendingSignature.
        var lease = SeedLeaseWithGraph(LeaseStatus.Active);
        lease.EsignEnvelopeId = "sig_active_1";
        lease.EsignStatus = EsignStatus.Sent;
        await _db.SaveChangesAsync();

        var sut = CreateService(new FakeEsignProvider { Configured = true, SignedPdf = SamplePdf() });

        await sut.HandleSignedEventAsync("sig_active_1");

        var reloaded = await _db.Leases.AsNoTracking().FirstAsync(l => l.Id == lease.Id);
        reloaded.EsignStatus.Should().Be(EsignStatus.Signed);
        reloaded.Status.Should().Be(LeaseStatus.Active);
    }

    [Fact]
    public async Task GetSignatureStatus_ReturnsExpectedShape()
    {
        var lease = SeedLeaseWithGraph(LeaseStatus.PendingSignature);
        lease.EsignEnvelopeId = "sig_status_1";
        lease.EsignStatus = EsignStatus.Sent;
        await _db.SaveChangesAsync();

        // Disabled provider so the status read returns the stored snapshot without a remote refresh.
        var sut = CreateService(new FakeEsignProvider { Configured = false });

        var status = await sut.GetSignatureStatusAsync(PortfolioId, lease.Id);

        status.Should().NotBeNull();
        status!.LeaseId.Should().Be(lease.Id);
        status.EsignStatus.Should().Be(EsignStatus.Sent);
        status.LeaseStatus.Should().Be(LeaseStatus.PendingSignature);
        status.EnvelopeId.Should().Be("sig_status_1");
        status.HasSignedDocument.Should().BeFalse();
    }

    [Fact]
    public async Task GetSignatureStatus_LeaseNotInPortfolio_ReturnsNull()
    {
        var sut = CreateService(new FakeEsignProvider { Configured = false });

        var status = await sut.GetSignatureStatusAsync(PortfolioId, leaseId: 99999);

        status.Should().BeNull();
    }

    private LeaseEsignService CreateService(IEsignProvider provider)
    {
        var leaseService = new LeaseService(
            _db,
            new NoopDataUpdateService(),
            _storage,
            new LeaseAgreementPdfGenerator(),
            NullLogger<LeaseService>.Instance);

        return new LeaseEsignService(
            _db,
            leaseService,
            provider,
            _storage,
            new NoopDataUpdateService(),
            new AuditTrailService(_db, new RentalCommand.Data.Auditing.AuditScope()),
            new SandboxGuard(_db),
            NullLogger<LeaseEsignService>.Instance);
    }

    private static byte[] SamplePdf()
        // A minimal but valid-enough PDF byte sequence for storage/streaming round-trips.
        => System.Text.Encoding.ASCII.GetBytes("%PDF-1.4\n%signed-test\n");

    private Lease SeedLeaseWithGraph(LeaseStatus status, string? tenantEmail = "tenant@example.com")
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
            Email = tenantEmail,
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
            Status = status,
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

    /// <summary>Configurable fake provider — gating, send, status, and signed-file download are all controllable.</summary>
    private sealed class FakeEsignProvider : IEsignProvider
    {
        public bool Configured { get; init; }
        public string EnvelopeId { get; init; } = "sig_fake";
        public byte[]? SignedPdf { get; init; }
        public int SendCalls { get; private set; }

        public bool IsConfigured => Configured;

        public Task<EsignResult> SendForSignatureAsync(EsignRequest request, CancellationToken ct = default)
        {
            SendCalls++;
            return Task.FromResult(Configured
                ? EsignResult.Sent(EnvelopeId, "Sent")
                : EsignResult.NotConfigured());
        }

        public Task<EsignResult> GetStatusAsync(string envelopeId, CancellationToken ct = default)
            => Task.FromResult(Configured
                ? new EsignResult { EnvelopeId = envelopeId, Status = "Sent" }
                : EsignResult.NotConfigured());

        public Task<byte[]?> DownloadSignedDocumentAsync(string envelopeId, CancellationToken ct = default)
            => Task.FromResult(Configured ? SignedPdf : null);
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
    private sealed class EsignTestDbContext : RentalCommandDbContext
    {
        public EsignTestDbContext(DbContextOptions<RentalCommandDbContext> options) : base(options) { }

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
