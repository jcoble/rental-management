using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using RentalCommand.Api.DTOs;
using RentalCommand.Api.Services.Domain;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Data;

namespace RentalCommand.Api.Tests.Domain;

public class AccountingServiceTests : IDisposable
{
    private const int PortfolioId = 1;

    private readonly SqliteConnection _conn;
    private readonly RentalCommandDbContext _db;
    private readonly AccountingService _sut;

    public AccountingServiceTests()
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

        _sut = new AccountingService(_db);
    }

    public void Dispose()
    {
        _db.Dispose();
        _conn.Dispose();
    }

    [Fact]
    public async Task GetTransactionsAsync_ReturnsUnifiedDatabasePageWithScannedExpense()
    {
        var now = new DateTime(2026, 05, 25, 12, 0, 0, DateTimeKind.Utc);
        SeedPropertyLeaseAndPayment(now);
        SeedOlderExpenses(now);
        var scannedExpense = SeedExpense(
            description: "ComfortZone HVAC",
            amount: 456.88m,
            incurredAt: now,
            category: ScheduleECategory.Repairs,
            status: ExpenseStatus.Pending);

        var page = await _sut.GetTransactionsAsync(
            PortfolioId,
            new AccountingTransactionsQuery { Take = 20 },
            CancellationToken.None);

        page.TotalCount.Should().Be(122);
        page.Items.Should().HaveCount(20);
        page.Items.Should().ContainSingle(t =>
            t.Kind == "Expense" &&
            t.Id == scannedExpense.Id &&
            t.Description == "ComfortZone HVAC" &&
            t.Category == "Repairs" &&
            t.Status == "Pending" &&
            t.DetailHref == $"/accounting/expenses/{scannedExpense.Id}");
    }

    [Fact]
    public async Task GetSummaryAsync_ReturnsPlainEnglishMoneySnapshot()
    {
        var now = new DateTime(2026, 05, 25, 12, 0, 0, DateTimeKind.Utc);
        SeedPropertyLeaseAndPayment(now);
        SeedExpense(
            description: "ComfortZone HVAC",
            amount: 456.88m,
            incurredAt: now,
            category: ScheduleECategory.Repairs,
            status: ExpenseStatus.Paid);

        var summary = await _sut.GetSummaryAsync(PortfolioId, CancellationToken.None);

        summary.Snapshot.Title.Should().Be("Overdue rent needs attention");
        summary.Snapshot.Summary.Should().NotBeNullOrWhiteSpace();
        summary.Snapshot.Bullets.Should().Contain(b => b.Contains("overdue"));
        summary.Snapshot.Bullets.Should().Contain(b => b.Contains("Repairs"));
    }

    [Fact]
    public async Task GetTransactionsAsync_FiltersByKindStatusPropertyAndDescription()
    {
        var property = SeedPropertyLeaseAndPayment(DateTime.UtcNow);
        SeedExpense(
            description: "ComfortZone HVAC",
            amount: 456.88m,
            incurredAt: new DateTime(2026, 05, 05, 0, 0, 0, DateTimeKind.Utc),
            category: ScheduleECategory.Repairs,
            status: ExpenseStatus.Pending,
            propertyId: property.Id);
        SeedExpense(
            description: "Paid insurance",
            amount: 300m,
            incurredAt: new DateTime(2026, 05, 06, 0, 0, 0, DateTimeKind.Utc),
            category: ScheduleECategory.Insurance,
            status: ExpenseStatus.Paid,
            propertyId: property.Id);

        var page = await _sut.GetTransactionsAsync(
            PortfolioId,
            new AccountingTransactionsQuery
            {
                Kind = "Expense",
                Status = "Pending",
                PropertyId = property.Id,
                Search = "ComfortZone",
                Take = 20,
            },
            CancellationToken.None);

        page.TotalCount.Should().Be(1);
        page.Items.Single().Description.Should().Be("ComfortZone HVAC");
    }

    [Fact]
    public async Task GetTransactionsAsync_IncludesBankFeedRowsAndSearchesCaseInsensitively()
    {
        SeedBankTransaction(
            description: "HOME DEPOT STORE",
            merchantName: "Home Depot",
            amount: -84.25m,
            postedAt: new DateTime(2026, 06, 01, 0, 0, 0, DateTimeKind.Utc),
            category: "Hardware",
            matchStatus: "Unmatched");

        var page = await _sut.GetTransactionsAsync(
            PortfolioId,
            new AccountingTransactionsQuery
            {
                Kind = "Bank",
                Search = "home depot",
                Take = 20,
            },
            CancellationToken.None);

        page.TotalCount.Should().Be(1);
        page.Items.Single().Should().Match<AccountingTransactionResponse>(t =>
            t.Kind == "Bank" &&
            t.Description == "HOME DEPOT STORE" &&
            t.Amount == -84.25m &&
            t.Category == "Hardware" &&
            t.DetailHref == "/banking");
    }

    [Fact]
    public async Task GetSummaryAndReports_CountUnmatchedBankActivityWithoutDoubleCountingMatchedRows()
    {
        SeedPropertyLeaseAndPayment(new DateTime(2026, 06, 01, 0, 0, 0, DateTimeKind.Utc));
        SeedBankTransaction(
            description: "Tenant ACH",
            merchantName: "Tenant",
            amount: 1200m,
            postedAt: new DateTime(2026, 06, 01, 0, 0, 0, DateTimeKind.Utc),
            category: "Deposit",
            matchStatus: "Unmatched");
        SeedBankTransaction(
            description: "Hardware supply",
            merchantName: "Hardware Store",
            amount: -84.25m,
            postedAt: new DateTime(2026, 06, 02, 0, 0, 0, DateTimeKind.Utc),
            category: "Withdrawal",
            matchStatus: "Unmatched");
        SeedBankTransaction(
            description: "Matched duplicate deposit",
            merchantName: "Tenant",
            amount: 1200m,
            postedAt: new DateTime(2026, 06, 03, 0, 0, 0, DateTimeKind.Utc),
            category: "Deposit",
            matchStatus: "Matched",
            matchedPaymentId: 1);

        var summary = await _sut.GetSummaryAsync(PortfolioId, CancellationToken.None);
        var reports = await _sut.GetReportsAsync(PortfolioId, CancellationToken.None);

        summary.Payments.Collected.Should().Be(1200m);
        summary.TotalExpenses.Should().Be(84.25m);
        reports.TotalIncome.Should().Be(1200m);
        reports.TotalExpenses.Should().Be(84.25m);
        reports.Ledger.Should().ContainSingle(l => l.Type == "Bank" && l.Description == "Tenant ACH");
        reports.Ledger.Should().ContainSingle(l => l.Type == "Bank" && l.Description == "Hardware supply");
        reports.Ledger.Should().NotContain(l => l.Description == "Matched duplicate deposit");
    }

    private Property SeedPropertyLeaseAndPayment(DateTime now)
    {
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
        _db.Payments.Add(new Payment
        {
            PortfolioId = PortfolioId,
            Lease = lease,
            PaymentType = PaymentType.Rent,
            Status = PaymentStatus.Scheduled,
            Amount = 1200m,
            DueDate = now.AddDays(-1),
            CreatedAt = now,
            UpdatedAt = now,
        });
        _db.SaveChanges();
        return property;
    }

    private void SeedOlderExpenses(DateTime now)
    {
        for (var i = 0; i < 120; i++)
        {
            SeedExpense(
                description: $"Operating expense {i}",
                amount: 100 + i,
                incurredAt: now.AddDays(-30 - i),
                category: ScheduleECategory.Utilities,
                status: ExpenseStatus.Paid,
                save: false);
        }
        _db.SaveChanges();
    }

    private Expense SeedExpense(
        string description,
        decimal amount,
        DateTime incurredAt,
        ScheduleECategory category,
        ExpenseStatus status,
        int? propertyId = null,
        bool save = true)
    {
        var expense = new Expense
        {
            PortfolioId = PortfolioId,
            PropertyId = propertyId,
            Category = category,
            Description = description,
            Status = status,
            Amount = amount,
            IncurredAt = incurredAt,
            CreatedAt = incurredAt,
            UpdatedAt = incurredAt,
        };
        _db.Expenses.Add(expense);
        if (save) _db.SaveChanges();
        return expense;
    }

    private BankTransaction SeedBankTransaction(
        string description,
        string merchantName,
        decimal amount,
        DateTime postedAt,
        string category,
        string matchStatus,
        int? matchedPaymentId = null,
        int? matchedExpenseId = null)
    {
        var connection = _db.BankConnections.FirstOrDefault() ?? new BankConnection
        {
            PortfolioId = PortfolioId,
            Provider = "Plaid",
            InstitutionName = "Sandbox Bank",
            AccountName = "Checking",
            Status = "Active",
            CreatedAt = postedAt,
            UpdatedAt = postedAt,
        };
        if (connection.Id == 0) _db.BankConnections.Add(connection);

        var transaction = new BankTransaction
        {
            PortfolioId = PortfolioId,
            BankConnection = connection,
            ProviderTransactionId = Guid.NewGuid().ToString("N"),
            PostedAt = postedAt,
            Description = description,
            MerchantName = merchantName,
            Amount = amount,
            IsoCurrencyCode = "USD",
            Category = category,
            MatchStatus = matchStatus,
            MatchedPaymentId = matchedPaymentId,
            MatchedExpenseId = matchedExpenseId,
            CreatedAt = postedAt,
            UpdatedAt = postedAt,
        };
        _db.BankTransactions.Add(transaction);
        _db.SaveChanges();
        return transaction;
    }
}

internal sealed class AccountingServiceTestDbContext : RentalCommandDbContext
{
    public AccountingServiceTestDbContext(DbContextOptions<RentalCommandDbContext> options) : base(options) { }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.Entity<ScanDraft>().Property(e => e.ExtractedFields).HasColumnType("TEXT");
        modelBuilder.Entity<AuditLog>().Property(e => e.OldValues).HasColumnType("TEXT");
        modelBuilder.Entity<AuditLog>().Property(e => e.NewValues).HasColumnType("TEXT");
        modelBuilder.Entity<OutboxMessage>().Property(e => e.Payload).HasColumnType("TEXT");
        modelBuilder.Entity<QueuedJob>().Property(e => e.Payload).HasColumnType("TEXT");
        modelBuilder.Entity<Expense>().Property(e => e.ReceiptData).HasColumnType("TEXT");
        modelBuilder.Entity<Lease>().ToTable("Leases");
    }
}
