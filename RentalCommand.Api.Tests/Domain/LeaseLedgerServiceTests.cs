using System.Data.Common;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
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
    private readonly List<string> _executedSql = [];
    private readonly RentalCommandDbContext _db;
    private readonly LeaseService _sut;

    public LeaseLedgerServiceTests()
    {
        _conn = new SqliteConnection("DataSource=:memory:");
        _conn.Open();

        var options = new DbContextOptionsBuilder<RentalCommandDbContext>()
            .UseSqlite(_conn)
            .AddInterceptors(new RecordingCommandInterceptor(_executedSql))
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
    public async Task GetLedgerAsync_PartialPayment_ShowsCollectedPortionAsCompanionPaymentLine()
    {
        var lease = SeedLease();

        // A rent paid in full, plus a rent partially paid ($700 collected of $1,000 → $300 still owed).
        _db.Payments.Add(new Payment
        {
            PortfolioId = PortfolioId,
            Lease = lease,
            PaymentType = PaymentType.Rent,
            Status = PaymentStatus.Paid,
            Amount = 1000m,
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
            Status = PaymentStatus.Partial,
            Amount = 1000m,
            AmountPaid = 700m,
            DueDate = new DateTime(2026, 04, 01, 0, 0, 0, DateTimeKind.Utc),
            PaidDate = new DateTime(2026, 04, 02, 0, 0, 0, DateTimeKind.Utc),
            Method = "Cash",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        _db.SaveChanges();

        var ledger = await _sut.GetLedgerAsync(PortfolioId, lease.Id, ct: CancellationToken.None);

        ledger.Should().NotBeNull();

        // The Partial is still billed at its full amount as a charge (money owed, negative)...
        var partialCharge = ledger!.Entries.Single(e => e.Status == "Partial" && e.Type == "Charge");
        partialCharge.Amount.Should().Be(-1000m);

        // ...and the $700 already collected now appears as its own companion payment line (previously
        // absent — the collected portion was invisible on the ledger, BUG-4).
        var partialPayment = ledger.Entries.Single(e => e.Status == "Partial" && e.Type == "Payment");
        partialPayment.Amount.Should().Be(700m);
        partialPayment.Explanation.Should().Be("Payment of $700 received by cash on Apr 2 — $300 still owed.");

        // The partial's charge + companion net to exactly the still-owed remainder.
        (partialCharge.Amount + partialPayment.Amount).Should().Be(-300m);

        // Every collected dollar is now visible: the Payment-type lines sum to the headline Paid total.
        ledger.Entries.Where(e => e.Type == "Payment").Sum(e => e.Amount).Should().Be(ledger.TotalPaid);

        // Headline totals are unchanged by the companion line (no double-count — they're a separate DB
        // aggregate): Charged $2,000 · Paid $1,700 · Balance $300.
        ledger.TotalCharged.Should().Be(2000m);
        ledger.TotalPaid.Should().Be(1700m);
        ledger.Balance.Should().Be(300m);
    }

    [Fact]
    public async Task GetLedgerAsync_OrdersPaymentRowsInSql()
    {
        var lease = SeedLease();
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
        _db.Payments.Add(new Payment
        {
            PortfolioId = PortfolioId,
            Lease = lease,
            PaymentType = PaymentType.Rent,
            Status = PaymentStatus.Paid,
            Amount = 1200m,
            DueDate = new DateTime(2026, 03, 01, 0, 0, 0, DateTimeKind.Utc),
            PaidDate = new DateTime(2026, 03, 03, 0, 0, 0, DateTimeKind.Utc),
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        _db.SaveChanges();

        _executedSql.Clear();

        var ledger = await _sut.GetLedgerAsync(PortfolioId, lease.Id, ct: CancellationToken.None);

        ledger.Should().NotBeNull();
        ledger!.Entries.Select(e => e.Date).Should().BeInDescendingOrder();
        _executedSql.Should().Contain(command =>
            command.Contains("FROM \"Payments\"", StringComparison.OrdinalIgnoreCase)
            && command.Contains("ORDER BY", StringComparison.OrdinalIgnoreCase)
            && !command.Contains("GROUP BY", StringComparison.OrdinalIgnoreCase),
            "lease ledger transaction rows must be ordered by the database before materialization");
    }

    [Fact]
    public async Task GetLedgerAsync_PagesPaymentRowsInSql_AndReportsTotalPaymentCount()
    {
        var lease = SeedLease();
        // Six rent charges across six months — more than the two-row page we request below.
        for (var month = 1; month <= 6; month++)
        {
            _db.Payments.Add(new Payment
            {
                PortfolioId = PortfolioId,
                Lease = lease,
                PaymentType = PaymentType.Rent,
                Status = PaymentStatus.Scheduled,
                Amount = 1000m,
                DueDate = new DateTime(2026, month, 1, 0, 0, 0, DateTimeKind.Utc),
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            });
        }
        _db.SaveChanges();

        _executedSql.Clear();

        var page1 = await _sut.GetLedgerAsync(PortfolioId, lease.Id, skip: 0, take: 2, ct: CancellationToken.None);

        page1.Should().NotBeNull();
        // TotalCount is the whole payment set (the pageable unit), independent of the page size.
        page1!.TotalCount.Should().Be(6);
        page1.Skip.Should().Be(0);
        page1.Take.Should().Be(2);
        // Only the first page of rows was materialized (2 Scheduled charges → 2 rows, no companions).
        page1.Entries.Should().HaveCount(2);
        // Newest first: June then May.
        page1.Entries.Select(e => e.Date).Should().ContainInOrder(
            new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc),
            new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc));
        // Paging happens in SQL (LIMIT/OFFSET after ORDER BY), not by materializing the whole history.
        _executedSql.Should().Contain(command =>
            command.Contains("FROM \"Payments\"", StringComparison.OrdinalIgnoreCase)
            && command.Contains("ORDER BY", StringComparison.OrdinalIgnoreCase)
            && command.Contains("LIMIT", StringComparison.OrdinalIgnoreCase)
            && !command.Contains("GROUP BY", StringComparison.OrdinalIgnoreCase),
            "lease ledger rows must be paged (LIMIT) by the database, not sliced in memory");

        // The next page continues the descending sequence (April then March).
        var page2 = await _sut.GetLedgerAsync(PortfolioId, lease.Id, skip: 2, take: 2, ct: CancellationToken.None);
        page2!.Entries.Select(e => e.Date).Should().ContainInOrder(
            new DateTime(2026, 4, 1, 0, 0, 0, DateTimeKind.Utc),
            new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc));
        // Headline totals are the whole-set aggregate on every page (6 × $1,000 charged, nothing paid).
        page2.TotalCharged.Should().Be(6000m);
        page2.TotalPaid.Should().Be(0m);
    }

    [Fact]
    public async Task GetLedgerAsync_ComputesPastDueCountOverWholeSet_NotJustThePage()
    {
        var lease = SeedLease();
        // Two unmistakably past-due charges (a month ago) + one future + one already Paid.
        _db.Payments.Add(new Payment
        {
            PortfolioId = PortfolioId, Lease = lease, PaymentType = PaymentType.Rent,
            Status = PaymentStatus.Scheduled, Amount = 1000m,
            DueDate = DateTime.UtcNow.Date.AddMonths(-1),
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        });
        _db.Payments.Add(new Payment
        {
            PortfolioId = PortfolioId, Lease = lease, PaymentType = PaymentType.Rent,
            Status = PaymentStatus.Late, Amount = 1000m,
            DueDate = DateTime.UtcNow.Date.AddMonths(-2),
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        });
        _db.Payments.Add(new Payment
        {
            PortfolioId = PortfolioId, Lease = lease, PaymentType = PaymentType.Rent,
            Status = PaymentStatus.Scheduled, Amount = 1000m,
            DueDate = DateTime.UtcNow.Date.AddMonths(1),   // future → not past due
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        });
        _db.Payments.Add(new Payment
        {
            PortfolioId = PortfolioId, Lease = lease, PaymentType = PaymentType.Rent,
            Status = PaymentStatus.Paid, Amount = 1000m,
            DueDate = DateTime.UtcNow.Date.AddMonths(-3),   // paid → not past due
            PaidDate = DateTime.UtcNow.Date.AddMonths(-3),
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        });
        _db.SaveChanges();

        // Ask for a single-row page: the past-due count must still reflect ALL four payments, not the page.
        var ledger = await _sut.GetLedgerAsync(PortfolioId, lease.Id, skip: 0, take: 1, ct: CancellationToken.None);

        ledger.Should().NotBeNull();
        ledger!.Entries.Should().HaveCount(1);              // only the page materialized
        ledger.PastDueCount.Should().Be(2);                 // computed over the whole set, DB-side
    }

    [Fact]
    public async Task GetLedgerAsync_ReturnsOpeningBalanceAsSeparateAnchor_NotMixedIntoEntries()
    {
        var lease = SeedLease();
        _db.Payments.Add(new Payment
        {
            PortfolioId = PortfolioId, Lease = lease, PaymentType = PaymentType.Rent,
            Status = PaymentStatus.Paid, Amount = 1000m,
            DueDate = new DateTime(2026, 03, 01, 0, 0, 0, DateTimeKind.Utc),
            PaidDate = new DateTime(2026, 03, 02, 0, 0, 0, DateTimeKind.Utc),
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        });
        _db.OpeningBalances.Add(new OpeningBalance
        {
            PortfolioId = PortfolioId,
            LeaseId = lease.Id,
            Amount = 250m,   // tenant owed $250 carried over
            AsOfDate = new DateTime(2026, 01, 01, 0, 0, 0, DateTimeKind.Utc),
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        _db.SaveChanges();

        var ledger = await _sut.GetLedgerAsync(PortfolioId, lease.Id, ct: CancellationToken.None);

        ledger.Should().NotBeNull();
        // The opening balance is returned as its own anchor, not appended to the paged entries.
        ledger!.Opening.Should().NotBeNull();
        ledger.Opening!.Type.Should().Be("Opening");
        ledger.Opening.Amount.Should().Be(250m);
        ledger.Entries.Should().OnlyContain(e => e.Type != "Opening");
        ledger.Entries.Should().HaveCount(1);   // just the one payment row
        // The opening amount is still folded into the headline totals ($1,000 paid + $250 opening owed).
        ledger.TotalCharged.Should().Be(1250m);
        ledger.TotalPaid.Should().Be(1000m);
        ledger.Balance.Should().Be(250m);
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
