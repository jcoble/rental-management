using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Interfaces;
using RentalCommand.Data;
using RentalCommand.Api.Services.Domain;

namespace RentalCommand.Api.Tests.Domain;

public class LeaseLedgerServiceTests : IDisposable
{
    private const int PortfolioId = 1;

    private readonly SqliteConnection _conn;
    private readonly RentalCommandDbContext _db;
    private readonly LeaseService _sut;

    public LeaseLedgerServiceTests()
    {
        _conn = new SqliteConnection("DataSource=:memory:");
        _conn.Open();

        var options = new DbContextOptionsBuilder<RentalCommandDbContext>()
            .UseSqlite(_conn)
            .Options;

        _db = new AccountingServiceTestDbContext(options);
        _db.Database.EnsureCreated();

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

        _sut = new LeaseService(
            _db,
            new NoopDataUpdateService(),
            new LedgerInMemoryFileStorage(),
            new LeaseAgreementPdfGenerator(),
            new RentalCommand.Api.Services.AuditTrailService(_db, new RentalCommand.Data.Auditing.AuditScope()),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<LeaseService>.Instance);
    }

    public void Dispose()
    {
        _db.Dispose();
        _conn.Dispose();
    }

    [Fact]
    public async Task GetLedgerAsync_EnrichesEntriesWithPlainEnglishExplanations_AndComputesBalance()
    {
        var lease = SeedLease();

        // A paid rent (collected) and an outstanding overdue rent.
        _db.Payments.Add(new Payment
        {
            PortfolioId = PortfolioId,
            Lease = lease,
            PaymentType = PaymentType.Rent,
            Status = PaymentStatus.Paid,
            Amount = 1200m,
            DueDate = new DateTime(2026, 03, 01, 0, 0, 0, DateTimeKind.Utc),
            PaidDate = new DateTime(2026, 03, 03, 0, 0, 0, DateTimeKind.Utc),
            Method = "Check",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        _db.Payments.Add(new Payment
        {
            PortfolioId = PortfolioId,
            Lease = lease,
            PaymentType = PaymentType.Rent,
            Status = PaymentStatus.Late,
            Amount = 1200m,
            DueDate = new DateTime(2026, 04, 01, 0, 0, 0, DateTimeKind.Utc),
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        _db.SaveChanges();

        var ledger = await _sut.GetLedgerAsync(PortfolioId, lease.Id, ct: CancellationToken.None);

        ledger.Should().NotBeNull();
        ledger!.Entries.Should().HaveCount(2);

        var paid = ledger.Entries.Single(e => e.Status == "Paid");
        paid.Type.Should().Be("Payment");
        paid.Amount.Should().Be(1200m);
        paid.Explanation.Should().Be("Payment of $1,200 received by check on Mar 3.");

        var owed = ledger.Entries.Single(e => e.Status == "Late");
        owed.Type.Should().Be("Charge");
        owed.Amount.Should().Be(-1200m); // owed charges show negative on the tenant ledger
        owed.Explanation.Should().Be("Rent for April 2026 — $1,200 due Apr 1 — now past due.");

        // One rent paid, one rent still owed → $1,200 outstanding.
        ledger.TotalCharged.Should().Be(2400m);
        ledger.TotalPaid.Should().Be(1200m);
        ledger.Balance.Should().Be(1200m);
    }

    [Fact]
    public async Task GetLedgerAsync_ReturnsNull_WhenLeaseNotInPortfolio()
    {
        var ledger = await _sut.GetLedgerAsync(PortfolioId, id: 9999, ct: CancellationToken.None);
        ledger.Should().BeNull();
    }

    [Fact]
    public async Task GetLedgerAsync_ReturnsNull_WhenTenantRequestsAnotherTenantsLease()
    {
        var lease = SeedLease(); // belongs to the seeded tenant
        _db.SaveChanges();

        // A different tenant (id intentionally not the lease's tenant) must not read this ledger.
        var foreignTenantId = lease.TenantId + 1000;
        var ledger = await _sut.GetLedgerAsync(
            PortfolioId, lease.Id, restrictToTenantId: foreignTenantId, ct: CancellationToken.None);

        ledger.Should().BeNull("a tenant may only read their own lease's ledger");
    }

    private Lease SeedLease()
    {
        var now = DateTime.UtcNow;
        var property = new Property
        {
            PortfolioId = PortfolioId,
            Name = "General",
            AddressLine1 = "1 Main",
            City = "Columbus",
            State = "OH",
            PostalCode = "43219",
            CreatedAt = now,
            UpdatedAt = now,
        };
        var unit = new Unit
        {
            Property = property,
            UnitNumber = "12",
            MarketRent = 1200m,
            CreatedAt = now,
            UpdatedAt = now,
        };
        var tenant = new Tenant
        {
            PortfolioId = PortfolioId,
            FirstName = "Maria",
            LastName = "Tenant",
            CreatedAt = now,
            UpdatedAt = now,
        };
        var lease = new Lease
        {
            PortfolioId = PortfolioId,
            Property = property,
            Unit = unit,
            Tenant = tenant,
            LeaseNumber = "L-001",
            Status = LeaseStatus.Active,
            StartDate = now.AddMonths(-1),
            EndDate = now.AddYears(1),
            MonthlyRent = 1200m,
            SecurityDeposit = 1200m,
            LateFeeAmount = 50m,
            CreatedAt = now,
            UpdatedAt = now,
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

    private sealed class LedgerInMemoryFileStorage : IFileStorage
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
}
