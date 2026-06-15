using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using RentalCommand.Api.DTOs;
using RentalCommand.Api.Services.Domain;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Interfaces;
using RentalCommand.Data;

namespace RentalCommand.Api.Tests.Domain;

public class PaymentServiceTests : IDisposable
{
    private const int PortfolioId = 1;
    private const int LeaseId = 100;
    private const int OtherLeaseId = 101;

    private readonly SqliteConnection _conn;
    private readonly RentalCommandDbContext _db;
    private readonly PaymentService _sut;

    public PaymentServiceTests()
    {
        _conn = new SqliteConnection("DataSource=:memory:");
        _conn.Open();

        var options = new DbContextOptionsBuilder<RentalCommandDbContext>()
            .UseSqlite(_conn)
            .Options;

        _db = new PaymentServiceTestDbContext(options);
        _db.Database.EnsureCreated();

        SeedPortfolioAndLease();

        _sut = new PaymentService(_db, new NoopDataUpdateService(),
            new RentalCommand.Api.Services.AuditTrailService(_db, new RentalCommand.Data.Auditing.AuditScope()));
    }

    public void Dispose()
    {
        _db.Dispose();
        _conn.Dispose();
    }

    [Fact]
    public async Task CreateAsync_FromScannedRentCheck_PersistsPayerCheckBankMethodAndExtras()
    {
        var now = DateTime.UtcNow;
        var request = new CreatePaymentRequest
        {
            LeaseId           = LeaseId,
            PaymentType       = PaymentType.Rent,
            Status            = PaymentStatus.Paid,
            Amount            = 1200.00m,
            DueDate           = now,
            PaidDate          = now,
            Method            = "Check",
            ExternalReference = "1487",
            // New typed columns promoted from the scanned check + the full extraction superset.
            PayerName     = "Marcus Williams",
            CheckNumber   = "1487",
            BankName      = "First National",
            ExtractedData = """{"document_kind":{"value":"RentCheck"},"payer_name":{"value":"Marcus Williams"}}""",
        };

        var created = await _sut.CreateAsync(PortfolioId, request);

        created.Should().NotBeNull();
        created!.PayerName.Should().Be("Marcus Williams");
        created.CheckNumber.Should().Be("1487");
        created.BankName.Should().Be("First National");
        created.Method.Should().Be("Check");

        // The typed columns + jsonb extras are persisted to the DB.
        var fromDb = await _db.Payments.AsNoTracking().SingleAsync(p => p.Id == created.Id);
        fromDb.PayerName.Should().Be("Marcus Williams");
        fromDb.CheckNumber.Should().Be("1487");
        fromDb.BankName.Should().Be("First National");
        fromDb.Method.Should().Be("Check");
        fromDb.ExtractedData.Should().Contain("RentCheck");
    }

    [Fact]
    public async Task UpdateAsync_ReassignsLease_PersistsNewLeaseId()
    {
        // Regression for TSK-197: editing a payment used to drop the lease because UpdatePaymentRequest
        // carried no LeaseId, so the model binder discarded it and UpdateAsync never reassigned it.
        var now = DateTime.UtcNow;
        var created = await _sut.CreateAsync(PortfolioId, new CreatePaymentRequest
        {
            LeaseId = LeaseId,
            PaymentType = PaymentType.Rent,
            Status = PaymentStatus.Scheduled,
            Amount = 1200.00m,
            DueDate = now,
        });
        created.Should().NotBeNull();

        var updated = await _sut.UpdateAsync(PortfolioId, created!.Id, new UpdatePaymentRequest
        {
            LeaseId = OtherLeaseId,
        });

        updated.Should().NotBeNull();
        updated!.LeaseId.Should().Be(OtherLeaseId);

        var fromDb = await _db.Payments.AsNoTracking().SingleAsync(p => p.Id == created.Id);
        fromDb.LeaseId.Should().Be(OtherLeaseId);
    }

    [Fact]
    public async Task GetAsync_ProjectsLeaseNumberAndTenantName_SoViewModeCanShowTheLease()
    {
        // Regression for TSK-197 residual: the payment detail page's VIEW mode rendered "—" for the
        // lease because PaymentResponse carried no LeaseNumber/TenantName — GetAsync never joined the
        // lease. GetAsync must Include the lease + tenant and project both labels.
        var now = DateTime.UtcNow;
        var created = await _sut.CreateAsync(PortfolioId, new CreatePaymentRequest
        {
            LeaseId = LeaseId,
            PaymentType = PaymentType.Rent,
            Status = PaymentStatus.Scheduled,
            Amount = 1200.00m,
            DueDate = now,
        });
        created.Should().NotBeNull();

        var fetched = await _sut.GetAsync(PortfolioId, created!.Id);

        fetched.Should().NotBeNull();
        fetched!.LeaseNumber.Should().Be("L-1");
        fetched.TenantName.Should().Be("Marcus Williams");
    }

    [Fact]
    public async Task MarkPaidAsync_PersistsNotes()
    {
        // Regression: the mobile Mark Paid sheet captures + sends a Notes value, but MarkPaidRequest had
        // no Notes property and MarkPaidAsync never wrote entity.Notes — the note was silently dropped.
        var now = DateTime.UtcNow;
        var created = await _sut.CreateAsync(PortfolioId, new CreatePaymentRequest
        {
            LeaseId = LeaseId,
            PaymentType = PaymentType.Rent,
            Status = PaymentStatus.Scheduled,
            Amount = 1200.00m,
            DueDate = now,
        });
        created.Should().NotBeNull();

        var marked = await _sut.MarkPaidAsync(PortfolioId, created!.Id, new MarkPaidRequest
        {
            PaidDate = now,
            Method = "Check",
            ExternalReference = "1487",
            Notes = "Dropped in the night box, slightly torn",
        });

        marked.Should().NotBeNull();
        marked!.Status.Should().Be(PaymentStatus.Paid);
        marked.Notes.Should().Be("Dropped in the night box, slightly torn");

        var fromDb = await _db.Payments.AsNoTracking().SingleAsync(p => p.Id == created.Id);
        fromDb.Notes.Should().Be("Dropped in the night box, slightly torn");
    }

    [Fact]
    public async Task MarkPaidAsync_NullNotes_LeavesExistingNoteUnchanged()
    {
        // Mark-paid with no Notes must not wipe a note set at create time (nullable-means-untouched).
        var now = DateTime.UtcNow;
        var created = await _sut.CreateAsync(PortfolioId, new CreatePaymentRequest
        {
            LeaseId = LeaseId,
            PaymentType = PaymentType.Rent,
            Status = PaymentStatus.Scheduled,
            Amount = 1200.00m,
            DueDate = now,
            Notes = "Original note",
        });
        created.Should().NotBeNull();

        await _sut.MarkPaidAsync(PortfolioId, created!.Id, new MarkPaidRequest { PaidDate = now });

        var fromDb = await _db.Payments.AsNoTracking().SingleAsync(p => p.Id == created.Id);
        fromDb.Notes.Should().Be("Original note");
    }

    private void SeedPortfolioAndLease()
    {
        var now = DateTime.UtcNow;
        _db.Portfolios.Add(new Portfolio
        {
            Id = PortfolioId,
            Name = "Test Portfolio",
            ManagementCompanyName = "Test Co",
            TimeZone = "UTC",
            CreatedAt = now,
            UpdatedAt = now,
        });
        _db.Properties.Add(new Property
        {
            Id = 10,
            PortfolioId = PortfolioId,
            Name = "Maple Court",
            AddressLine1 = "10 Maple Ct",
            City = "Columbus",
            State = "OH",
            PostalCode = "43215",
            CreatedAt = now,
            UpdatedAt = now,
        });
        _db.Units.Add(new Unit
        {
            Id = 20,
            PropertyId = 10,
            UnitNumber = "1",
            Bedrooms = 2,
            Bathrooms = 1,
            MarketRent = 1200m,
            CreatedAt = now,
            UpdatedAt = now,
        });
        _db.Tenants.Add(new Tenant
        {
            Id = 30,
            PortfolioId = PortfolioId,
            FirstName = "Marcus",
            LastName = "Williams",
            CreatedAt = now,
            UpdatedAt = now,
        });
        _db.Leases.Add(new Lease
        {
            Id = LeaseId,
            PortfolioId = PortfolioId,
            PropertyId = 10,
            UnitId = 20,
            TenantId = 30,
            LeaseNumber = "L-1",
            Status = LeaseStatus.Active,
            StartDate = now.AddMonths(-1),
            EndDate = now.AddMonths(11),
            MonthlyRent = 1200m,
            RentDueDay = 1,
            CreatedAt = now,
            UpdatedAt = now,
        });
        _db.Leases.Add(new Lease
        {
            Id = OtherLeaseId,
            PortfolioId = PortfolioId,
            PropertyId = 10,
            UnitId = 20,
            TenantId = 30,
            LeaseNumber = "L-2",
            Status = LeaseStatus.Active,
            StartDate = now.AddMonths(-1),
            EndDate = now.AddMonths(11),
            MonthlyRent = 1300m,
            RentDueDay = 1,
            CreatedAt = now,
            UpdatedAt = now,
        });
        _db.SaveChanges();
    }

    private sealed class NoopDataUpdateService : IDataUpdateService
    {
        public Task BroadcastEntityUpdateAsync(int portfolioId, string entityType, int entityId, object data, CancellationToken ct = default)
            => Task.CompletedTask;

        public Task BroadcastEntityDeleteAsync(int portfolioId, string entityType, int entityId, CancellationToken ct = default)
            => Task.CompletedTask;
    }
}

internal sealed class PaymentServiceTestDbContext : RentalCommandDbContext
{
    public PaymentServiceTestDbContext(DbContextOptions<RentalCommandDbContext> options) : base(options) { }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.Entity<ScanDraft>().Property(e => e.ExtractedFields).HasColumnType("TEXT");
        modelBuilder.Entity<AuditLog>().Property(e => e.OldValues).HasColumnType("TEXT");
        modelBuilder.Entity<AuditLog>().Property(e => e.NewValues).HasColumnType("TEXT");
        modelBuilder.Entity<OutboxMessage>().Property(e => e.Payload).HasColumnType("TEXT");
        modelBuilder.Entity<QueuedJob>().Property(e => e.Payload).HasColumnType("TEXT");
        modelBuilder.Entity<Expense>().Property(e => e.ReceiptData).HasColumnType("TEXT");
        modelBuilder.Entity<Payment>().Property(e => e.ExtractedData).HasColumnType("TEXT");
        modelBuilder.Entity<Lease>().Property(e => e.ExtractedData).HasColumnType("TEXT");
        modelBuilder.Entity<WorkOrder>().Property(e => e.ExtractedData).HasColumnType("TEXT");
        // SQLite cannot execute the Lease check constraints (StartDate < EndDate, RentDueDay range).
        modelBuilder.Entity<Lease>().ToTable("Leases");
    }
}
