using FluentAssertions;
using System.Data.Common;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using RentalCommand.Core;
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
    private readonly FakeFileStorage _files = new();
    private readonly PaymentService _sut;
    private readonly List<string> _commands = [];

    public PaymentServiceTests()
    {
        _conn = new SqliteConnection("DataSource=:memory:");
        _conn.Open();

        var options = new DbContextOptionsBuilder<RentalCommandDbContext>()
            .UseSqlite(_conn)
            .AddInterceptors(new RecordingCommandInterceptor(_commands))
            .Options;

        _db = new PaymentServiceTestDbContext(options);
        _db.Database.EnsureCreated();

        SeedPortfolioAndLease();

        _sut = new PaymentService(_db, new NoopDataUpdateService(),
            new RentalCommand.Api.Services.AuditTrailService(_db, new RentalCommand.Data.Auditing.AuditScope(), TimeProvider.System),
            _files, TimeProvider.System);
    }

    public void Dispose()
    {
        _db.Dispose();
        _conn.Dispose();
    }

    [Fact]
    public async Task ListPageAsync_ReturnsSqlCountAndRequestedWindow()
    {
        var now = DateTime.UtcNow;
        SeedPayment(LeaseId, "Rent A", now.AddDays(-4), 100m);
        SeedPayment(LeaseId, "Rent B", now.AddDays(-3), 200m);
        SeedPayment(LeaseId, "Rent C", now.AddDays(-2), 300m);
        SeedPayment(LeaseId, "Rent D", now.AddDays(-1), 400m);
        SeedPayment(OtherLeaseId, "Other lease", now, 500m);

        _commands.Clear();
        var page = await _sut.ListPageAsync(PortfolioId, LeaseId, new ListQuery
        {
            Sort = "dueDate",
            Skip = 1,
            Take = 2,
        });

        page.TotalCount.Should().Be(4);
        page.Skip.Should().Be(1);
        page.Take.Should().Be(2);
        page.Items.Select(p => p.ExternalReference).Should().Equal("Rent B", "Rent C");

        _commands.Should().Contain(sql =>
            sql.Contains("COUNT", StringComparison.OrdinalIgnoreCase) &&
            sql.Contains("FROM \"Payments\"", StringComparison.OrdinalIgnoreCase));
        _commands.Should().Contain(sql =>
            sql.Contains("ORDER BY", StringComparison.OrdinalIgnoreCase) &&
            sql.Contains("LIMIT", StringComparison.OrdinalIgnoreCase) &&
            sql.Contains("OFFSET", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ListPageAsync_SortsDueDateDescendingWhenRequested()
    {
        var now = DateTime.UtcNow;
        SeedPayment(LeaseId, "Old rent", now.AddDays(-10), 100m);
        SeedPayment(LeaseId, "Newest rent", now.AddDays(2), 200m);
        SeedPayment(LeaseId, "Middle rent", now.AddDays(-1), 300m);

        _commands.Clear();
        var page = await _sut.ListPageAsync(PortfolioId, LeaseId, new ListQuery
        {
            Sort = "-dueDate",
            Take = 3,
        });

        page.Items.Select(p => p.ExternalReference).Should().Equal("Newest rent", "Middle rent", "Old rent");
        _commands.Should().Contain(sql =>
            sql.Contains("ORDER BY", StringComparison.OrdinalIgnoreCase) &&
            sql.Contains("DESC", StringComparison.OrdinalIgnoreCase) &&
            sql.Contains("LIMIT", StringComparison.OrdinalIgnoreCase));
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
    public async Task UpdateAsync_ReassignsGeneratedPeriodPayment_ToLeaseWithSamePeriodPayment_ReturnsDomainConflict()
    {
        var now = DateTime.UtcNow;
        var source = new Payment
        {
            PortfolioId = PortfolioId,
            LeaseId = LeaseId,
            PaymentType = PaymentType.Rent,
            Status = PaymentStatus.Paid,
            Amount = 1200m,
            DueDate = new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc),
            PaidDate = new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc),
            PeriodKey = "2026-03",
            CreatedAt = now,
            UpdatedAt = now,
        };
        _db.Payments.AddRange(
            source,
            new Payment
            {
                PortfolioId = PortfolioId,
                LeaseId = OtherLeaseId,
                PaymentType = PaymentType.Rent,
                Status = PaymentStatus.Scheduled,
                Amount = 1300m,
                DueDate = new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc),
                PeriodKey = "2026-03",
                CreatedAt = now,
                UpdatedAt = now,
            });
        await _db.SaveChangesAsync();

        var act = async () => await _sut.UpdateAsync(PortfolioId, source.Id, new UpdatePaymentRequest
        {
            LeaseId = OtherLeaseId,
        });

        var ex = await act.Should().ThrowAsync<DomainValidationException>();
        ex.Which.StatusCode.Should().Be(409);
        ex.Which.Message.Should().Contain("already has");
        ex.Which.Message.Should().Contain("March 2026");

        var unchanged = await _db.Payments.AsNoTracking().SingleAsync(p => p.Id == source.Id);
        unchanged.LeaseId.Should().Be(LeaseId);
    }

    [Fact]
    public async Task GetAsync_ProjectsLeaseHomeContext_SoViewModeCanShowPropertyAndUnit()
    {
        // Landlords identify a payment by the home first: property + unit, not by an
        // internal lease number. The detail API must carry that context for view/edit labels.
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
        fetched.PropertyName.Should().Be("Maple Court");
        fetched.UnitNumber.Should().Be("1");
    }

    [Fact]
    public async Task GetAsync_ExposesUnitIdAndPropertyId_ResolvedViaLeaseJoin()
    {
        // TSK-457: the Command Center detail-folding needs to route a payment to its unit's tab, so the
        // Payment DTO carries unitId/propertyId resolved DB-side from the already-joined lease — no extra
        // query and no per-row lease fetch (HARD SQL rule).
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

        _commands.Clear();
        var fetched = await _sut.GetAsync(PortfolioId, created!.Id);

        fetched.Should().NotBeNull();
        // Seeded lease LeaseId=100 is on UnitId=20 / PropertyId=10.
        fetched!.UnitId.Should().Be(20);
        fetched.PropertyId.Should().Be(10);

        // SQL rule: the lease is JOINed into the same payment read — no standalone Leases fetch.
        _commands.Should().Contain(sql =>
            sql.Contains("FROM \"Payments\"", StringComparison.OrdinalIgnoreCase) &&
            sql.Contains("JOIN", StringComparison.OrdinalIgnoreCase) &&
            sql.Contains("\"Leases\"", StringComparison.OrdinalIgnoreCase));
        _commands.Should().NotContain(sql =>
            sql.Contains("FROM \"Leases\"", StringComparison.OrdinalIgnoreCase) &&
            !sql.Contains("\"Payments\"", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ListPageAsync_ExposesUnitIdAndPropertyId_ResolvedViaLeaseJoin()
    {
        var now = DateTime.UtcNow;
        SeedPayment(LeaseId, "Rent A", now.AddDays(-1), 100m);

        _commands.Clear();
        var page = await _sut.ListPageAsync(PortfolioId, LeaseId, new ListQuery { Take = 10 });

        var item = page.Items.Should().ContainSingle().Subject;
        item.UnitId.Should().Be(20);
        item.PropertyId.Should().Be(10);

        // SQL rule: the listing JOINs the lease in the same statement, not per-row.
        _commands.Should().Contain(sql =>
            sql.Contains("FROM \"Payments\"", StringComparison.OrdinalIgnoreCase) &&
            sql.Contains("JOIN", StringComparison.OrdinalIgnoreCase) &&
            sql.Contains("\"Leases\"", StringComparison.OrdinalIgnoreCase));
        _commands.Should().NotContain(sql =>
            sql.Contains("FROM \"Leases\"", StringComparison.OrdinalIgnoreCase) &&
            !sql.Contains("\"Payments\"", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task GetAsync_DoesNotAdvertiseScanWhenStoredFileBlobIsMissing()
    {
        // A StoredFiles row without a readable blob makes the web detail page request
        // /payment-file/{id}?thumb=true and log a 404. Detail DTO scan flags must reflect
        // file availability, not just database metadata.
        var now = DateTime.UtcNow;
        var created = await _sut.CreateAsync(PortfolioId, new CreatePaymentRequest
        {
            LeaseId = LeaseId,
            PaymentType = PaymentType.Rent,
            Status = PaymentStatus.Paid,
            Amount = 1200.00m,
            DueDate = now,
            PaidDate = now,
        });
        created.Should().NotBeNull();

        _db.StoredFiles.Add(new StoredFile
        {
            PortfolioId = PortfolioId,
            EntityType = "Payment",
            EntityId = created!.Id,
            FileName = "missing-check.jpg",
            FilePath = "missing-check.jpg",
            ContentType = "image/jpeg",
            FileSize = 1024,
            UploadedAt = now,
        });
        await _db.SaveChangesAsync();

        var fetched = await _sut.GetAsync(PortfolioId, created.Id);

        fetched.Should().NotBeNull();
        fetched!.HasScan.Should().BeFalse();
        fetched.ScanIsImage.Should().BeFalse();
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

    [Fact]
    public async Task UpdateAsync_NotesOnlyChange_IsCapturedInAuditSnapshot()
    {
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

        var updated = await _sut.UpdateAsync(PortfolioId, created!.Id, new UpdatePaymentRequest
        {
            Notes = "Updated note from detail view",
        });

        updated.Should().NotBeNull();
        updated!.Notes.Should().Be("Updated note from detail view");

        var log = await _db.AuditLogs.AsNoTracking().SingleOrDefaultAsync(a =>
            a.EntityType == "Payment" &&
            a.EntityId == created.Id &&
            a.Operation == AuditLogOperation.Updated);
        log.Should().NotBeNull("payment detail history must show notes edits, not just money/status changes");
        log!.OldValues.Should().Contain("\"notes\":\"Original note\"");
        log.NewValues.Should().Contain("\"notes\":\"Updated note from detail view\"");
        log.ChangeReason.Should().Contain("notes updated");
    }

    [Fact]
    public async Task MarkLeasePastDuePaidAsync_MarksOnlyPastDueRowsForThatLease()
    {
        var now = DateTime.UtcNow;
        var paidDate = now.Date.AddHours(14);
        var eligibleScheduled = SeedPayment(LeaseId, PaymentStatus.Scheduled, now.AddDays(-10), 1200m);
        var eligiblePartial = SeedPayment(LeaseId, PaymentStatus.Partial, now.AddDays(-3), 1200m, amountPaid: 300m);
        var eligibleLate = SeedPayment(LeaseId, PaymentStatus.Late, now.AddDays(5), 1200m);
        var alreadyPaid = SeedPayment(LeaseId, PaymentStatus.Paid, now.AddDays(-8), 1200m);
        var futureScheduled = SeedPayment(LeaseId, PaymentStatus.Scheduled, now.AddDays(5), 1200m);
        var otherLeaseLate = SeedPayment(OtherLeaseId, PaymentStatus.Late, now.AddDays(-10), 1300m);
        await _db.SaveChangesAsync();

        var result = await _sut.MarkLeasePastDuePaidAsync(PortfolioId, LeaseId, new MarkPaidRequest
        {
            PaidDate = paidDate,
            Method = "ACH",
            Notes = "Settled from past-due action",
        });

        result.Should().NotBeNull();
        result!.LeaseId.Should().Be(LeaseId);
        result.MarkedPaidCount.Should().Be(3);
        result.PaymentIds.Should().BeEquivalentTo([eligibleScheduled.Id, eligiblePartial.Id, eligibleLate.Id]);

        var payments = await _db.Payments.AsNoTracking().ToDictionaryAsync(p => p.Id);
        foreach (var id in result.PaymentIds)
        {
            payments[id].Status.Should().Be(PaymentStatus.Paid);
            payments[id].PaidDate.Should().Be(paidDate);
            payments[id].AmountPaid.Should().BeNull();
            payments[id].Method.Should().Be("ACH");
            payments[id].Notes.Should().Be("Settled from past-due action");
        }

        payments[alreadyPaid.Id].Status.Should().Be(PaymentStatus.Paid);
        payments[futureScheduled.Id].Status.Should().Be(PaymentStatus.Scheduled);
        payments[otherLeaseLate.Id].Status.Should().Be(PaymentStatus.Late);
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

    private void SeedPayment(int leaseId, string reference, DateTime dueDate, decimal amount)
    {
        _db.Payments.Add(new Payment
        {
            PortfolioId = PortfolioId,
            LeaseId = leaseId,
            PaymentType = PaymentType.Rent,
            Status = PaymentStatus.Scheduled,
            Amount = amount,
            DueDate = dueDate,
            ExternalReference = reference,
            CreatedAt = dueDate,
            UpdatedAt = dueDate,
        });
        _db.SaveChanges();
    }

    private Payment SeedPayment(
        int leaseId,
        PaymentStatus status,
        DateTime dueDate,
        decimal amount,
        decimal? amountPaid = null)
    {
        var payment = new Payment
        {
            PortfolioId = PortfolioId,
            LeaseId = leaseId,
            PaymentType = PaymentType.Rent,
            Status = status,
            Amount = amount,
            AmountPaid = amountPaid,
            DueDate = dueDate,
            PaidDate = status == PaymentStatus.Paid ? dueDate : null,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        _db.Payments.Add(payment);
        return payment;
    }

    private sealed class NoopDataUpdateService : IDataUpdateService
    {
        public Task BroadcastEntityUpdateAsync(int portfolioId, string entityType, int entityId, object data, CancellationToken ct = default)
            => Task.CompletedTask;

        public Task BroadcastEntityDeleteAsync(int portfolioId, string entityType, int entityId, CancellationToken ct = default)
            => Task.CompletedTask;
    }

    private sealed class FakeFileStorage : IFileStorage
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

    private sealed class RecordingCommandInterceptor(List<string> commands) : DbCommandInterceptor
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
