using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using RentalCommand.Api.DTOs;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Interfaces;
using RentalCommand.Data;
using RentalCommand.Api.Services.Domain;

namespace RentalCommand.Api.Tests.Domain;

public class OpeningBalanceServiceTests : IDisposable
{
    private const int PortfolioId = 1;
    private const int OtherPortfolioId = 2;

    private readonly SqliteConnection _conn;
    private readonly RentalCommandDbContext _db;
    private readonly OpeningBalanceService _sut;
    private readonly LeaseService _leaseService;

    public OpeningBalanceServiceTests()
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
        _db.Portfolios.Add(new Portfolio
        {
            Id = OtherPortfolioId,
            Name = "Other Portfolio",
            ManagementCompanyName = "Other Co",
            TimeZone = "UTC",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        _db.SaveChanges();

        _sut = new OpeningBalanceService(_db, new NoopDataUpdateService(), TimeProvider.System);
        _leaseService = new LeaseService(
            _db,
            new NoopDataUpdateService(),
            new OpeningInMemoryFileStorage(),
            new LeaseAgreementPdfGenerator(),
            new RentalCommand.Api.Services.AuditTrailService(_db, new RentalCommand.Data.Auditing.AuditScope(), TimeProvider.System),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<LeaseService>.Instance,
            TimeProvider.System);
    }

    public void Dispose()
    {
        _db.Dispose();
        _conn.Dispose();
    }

    [Fact]
    public async Task CreateAsync_CreatesOpeningBalanceForLease()
    {
        var lease = SeedLease(PortfolioId);

        var (result, response) = await _sut.CreateAsync(PortfolioId, new CreateOpeningBalanceRequest
        {
            LeaseId = lease.Id,
            Amount = 750m,
            AsOfDate = new DateTime(2026, 01, 01, 0, 0, 0, DateTimeKind.Utc),
            Note = "Carried over from prior software",
        });

        result.Should().Be(CreateOpeningBalanceResult.Created);
        response.Should().NotBeNull();
        response!.Amount.Should().Be(750m);
        response.LeaseId.Should().Be(lease.Id);
        response.PortfolioId.Should().Be(PortfolioId);

        (await _db.OpeningBalances.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task CreateAsync_RejectsSecondOpeningBalanceForSameLease()
    {
        var lease = SeedLease(PortfolioId);

        var first = await _sut.CreateAsync(PortfolioId, new CreateOpeningBalanceRequest
        {
            LeaseId = lease.Id,
            Amount = 500m,
            AsOfDate = new DateTime(2026, 01, 01, 0, 0, 0, DateTimeKind.Utc),
        });
        first.Result.Should().Be(CreateOpeningBalanceResult.Created);

        var second = await _sut.CreateAsync(PortfolioId, new CreateOpeningBalanceRequest
        {
            LeaseId = lease.Id,
            Amount = 999m,
            AsOfDate = new DateTime(2026, 02, 01, 0, 0, 0, DateTimeKind.Utc),
        });

        second.Result.Should().Be(CreateOpeningBalanceResult.AlreadyExists);
        second.Response.Should().BeNull();

        // The duplicate must not have been persisted; the original is untouched.
        (await _db.OpeningBalances.CountAsync()).Should().Be(1);
        (await _db.OpeningBalances.SingleAsync()).Amount.Should().Be(500m);
    }

    [Fact]
    public async Task CreateAsync_RejectsLeaseInAnotherPortfolio()
    {
        // Lease lives in the OTHER portfolio; the caller is scoped to PortfolioId.
        var foreignLease = SeedLease(OtherPortfolioId);

        var (result, response) = await _sut.CreateAsync(PortfolioId, new CreateOpeningBalanceRequest
        {
            LeaseId = foreignLease.Id,
            Amount = 300m,
            AsOfDate = new DateTime(2026, 01, 01, 0, 0, 0, DateTimeKind.Utc),
        });

        result.Should().Be(CreateOpeningBalanceResult.LeaseNotFound);
        response.Should().BeNull();
        (await _db.OpeningBalances.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task GetLedgerAsync_ReturnsOpeningBalanceAsSeparateAnchor_AndFoldsIntoBalance()
    {
        var lease = SeedLease(PortfolioId);

        // A positive opening balance: the tenant already owed $800 when the books started.
        await _sut.CreateAsync(PortfolioId, new CreateOpeningBalanceRequest
        {
            LeaseId = lease.Id,
            Amount = 800m,
            AsOfDate = new DateTime(2026, 01, 01, 0, 0, 0, DateTimeKind.Utc),
        });

        // Plus a single paid rent in March.
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
        _db.SaveChanges();

        var ledger = await _leaseService.GetLedgerAsync(PortfolioId, lease.Id, ct: CancellationToken.None);

        ledger.Should().NotBeNull();
        // The opening balance is a SEPARATE anchor now (stable across paging), not mixed into the entries.
        ledger!.Entries.Should().HaveCount(1);
        ledger.Entries.Should().OnlyContain(e => e.Type != "Opening");

        var opening = ledger.Opening;
        opening.Should().NotBeNull();
        opening!.Amount.Should().Be(800m);
        opening.Status.Should().Be("Opening");
        opening.Date.Should().Be(new DateTime(2026, 01, 01, 0, 0, 0, DateTimeKind.Utc));
        opening.Explanation.Should().Be(
            "Opening balance carried over from before Rental Command — $800 as of Jan 1, 2026.");

        // $800 carried over + $1,200 rent charged = $2,000 charged; $1,200 paid → $800 still owed.
        ledger.TotalCharged.Should().Be(2000m);
        ledger.TotalPaid.Should().Be(1200m);
        ledger.Balance.Should().Be(800m);
    }

    [Fact]
    public async Task GetLedgerAsync_TreatsNegativeOpeningBalanceAsCredit()
    {
        var lease = SeedLease(PortfolioId);

        // A negative opening balance: the tenant carried a $150 credit.
        await _sut.CreateAsync(PortfolioId, new CreateOpeningBalanceRequest
        {
            LeaseId = lease.Id,
            Amount = -150m,
            AsOfDate = new DateTime(2026, 01, 01, 0, 0, 0, DateTimeKind.Utc),
        });

        var ledger = await _leaseService.GetLedgerAsync(PortfolioId, lease.Id, ct: CancellationToken.None);

        ledger.Should().NotBeNull();
        // A credit acts like a prepayment: nothing charged, $150 "paid", balance is -150 (in tenant's favor).
        ledger!.TotalCharged.Should().Be(0m);
        ledger.TotalPaid.Should().Be(150m);
        ledger.Balance.Should().Be(-150m);

        var opening = ledger.Opening;
        opening.Should().NotBeNull();
        opening!.Amount.Should().Be(-150m);
        opening.Explanation.Should().Contain("Opening credit");
    }

    [Fact]
    public async Task GetLedgerAsync_FormatsOpeningBalanceCentsInPlainEnglishExplanation()
    {
        var lease = SeedLease(PortfolioId);

        await _sut.CreateAsync(PortfolioId, new CreateOpeningBalanceRequest
        {
            LeaseId = lease.Id,
            Amount = 225.30m,
            AsOfDate = new DateTime(2026, 01, 01, 0, 0, 0, DateTimeKind.Utc),
        });

        var ledger = await _leaseService.GetLedgerAsync(PortfolioId, lease.Id, ct: CancellationToken.None);

        ledger.Should().NotBeNull();
        var opening = ledger!.Opening;
        opening.Should().NotBeNull();
        opening!.Explanation.Should().Be(
            "Opening balance carried over from before Rental Command — $225.30 as of Jan 1, 2026.");
    }

    private Lease SeedLease(int portfolioId)
    {
        var now = DateTime.UtcNow;
        var property = new Property
        {
            PortfolioId = portfolioId,
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
            PortfolioId = portfolioId,
            FirstName = "Maria",
            LastName = "Tenant",
            CreatedAt = now,
            UpdatedAt = now,
        };
        var lease = new Lease
        {
            PortfolioId = portfolioId,
            Property = property,
            Unit = unit,
            Tenant = tenant,
            LeaseNumber = $"L-{portfolioId:000}",
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

    private sealed class OpeningInMemoryFileStorage : IFileStorage
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
