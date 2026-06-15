using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using RentalCommand.Api.DTOs;
using RentalCommand.Api.Services.Domain;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Data;
using Testcontainers.PostgreSql;
using Xunit;

namespace RentalCommand.IntegrationTests;

/// <summary>
/// The load-bearing proof for the accounting-transactions grid rewrite: <see
/// cref="AccountingService.GetTransactionsAsync"/> now reads the unified ledger from the
/// <c>vw_accounting_transactions</c> Postgres <b>view</b> (a <c>UNION ALL</c> of Payments + Expenses +
/// unmatched BankTransactions, mapped to the keyless <see cref="AccountingTransactionView"/> entity)
/// and searches it with Postgres <c>ILIKE</c>. Neither the view nor <c>ILIKE</c> exists/works under
/// SQLite — <c>EnsureCreated</c> does not run the raw-SQL migration that <c>CREATE VIEW</c>s, and SQLite
/// has no <c>ILIKE</c> operator — so these behaviors can only be verified against a real Postgres.
///
/// <para>This class spins its OWN Postgres (Testcontainers, same pattern as
/// <see cref="RlsTenantIsolationTests"/>), applies all migrations as the owner/superuser (which
/// <c>CREATE</c>s the view), seeds under one portfolio, and exercises the service. The queries run on
/// the owner connection (which bypasses RLS), so a plain <c>WHERE PortfolioId == x</c> — exactly what
/// <c>GetTransactionsAsync</c> applies — is the scope; RLS itself is proved separately by
/// <see cref="RlsTenantIsolationTests"/>.</para>
///
/// <para>Requires Docker. When Docker is unavailable the container fails to start and every test is
/// reported as <b>skipped</b> (via <c>[SkippableFact]</c> + <c>Skip.IfNot</c>) rather than failing.</para>
/// </summary>
public sealed class AccountingTransactionsViewTests : IAsyncLifetime
{
    // Built inside InitializeAsync (not as a field initializer): PostgreSqlBuilder.Build() validates
    // the Docker endpoint eagerly, so building it here lets a missing daemon be caught and skipped.
    private PostgreSqlContainer? _pg;

    private bool _dockerAvailable;
    private string _ownerConnString = string.Empty;
    private int _portfolioId;

    public async Task InitializeAsync()
    {
        try
        {
            _pg = new PostgreSqlBuilder()
                .WithImage("postgres:16-alpine")
                .WithDatabase("rentalcommand")
                .WithUsername("postgres")
                .WithPassword("postgres")
                .Build();
            await _pg.StartAsync();
            _dockerAvailable = true;
        }
        catch
        {
            // No Docker daemon (or image pull/build-validation failed) — tests report as skipped.
            _dockerAvailable = false;
            return;
        }

        _ownerConnString = _pg.GetConnectionString();

        // Apply all migrations as the owner. This CREATEs vw_accounting_transactions (the keyless
        // AccountingTransactionView entity reads it) plus the RLS roles/policies and every table.
        await using var ctx = NewContext(_ownerConnString);
        await ctx.Database.MigrateAsync();

        // One portfolio, captured by id, used by every test in this class.
        var portfolio = new Portfolio
        {
            Name = "Test Portfolio",
            ManagementCompanyName = "Test Co",
            TimeZone = "UTC",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        ctx.Portfolios.Add(portfolio);
        await ctx.SaveChangesAsync();
        _portfolioId = portfolio.Id;
    }

    public async Task DisposeAsync()
    {
        if (_pg is not null)
        {
            await _pg.DisposeAsync();
        }
    }

    // ─────────────────────────────────────────────────────────────────────────────────────────────
    // (a) Unified page with a scanned expense surfaced as a single Expense row.
    // ─────────────────────────────────────────────────────────────────────────────────────────────
    [SkippableFact]
    public async Task GetTransactionsAsync_ReturnsUnifiedPage_WithScannedExpense()
    {
        SkipIfNoDocker();
        await using var db = NewContext(_ownerConnString);
        var now = new DateTime(2026, 05, 25, 12, 0, 0, DateTimeKind.Utc);

        SeedPropertyLeaseAndPayment(db, _portfolioId, now);
        var scannedExpense = SeedExpense(
            db, _portfolioId,
            description: "ComfortZone HVAC",
            amount: 456.88m,
            incurredAt: now,
            category: ScheduleECategory.Repairs,
            status: ExpenseStatus.Pending);

        var page = await NewService(db).GetTransactionsAsync(
            _portfolioId,
            new AccountingTransactionsQuery { Take = 20 },
            CancellationToken.None);

        page.Items.Should().ContainSingle(t =>
            t.Kind == "Expense" &&
            t.Id == scannedExpense.Id &&
            t.Description == "ComfortZone HVAC" &&
            t.Category == "Repairs" &&
            t.Status == "Pending" &&
            t.DetailHref == $"/accounting/expenses/{scannedExpense.Id}");
    }

    // ─────────────────────────────────────────────────────────────────────────────────────────────
    // (b) Kind + status + property + ILIKE search filter, all through the view.
    // ─────────────────────────────────────────────────────────────────────────────────────────────
    [SkippableFact]
    public async Task GetTransactionsAsync_FiltersByKindStatusPropertyAndSearch()
    {
        SkipIfNoDocker();
        await using var db = NewContext(_ownerConnString);

        var property = SeedPropertyLeaseAndPayment(db, _portfolioId, DateTime.UtcNow);
        SeedExpense(
            db, _portfolioId,
            description: "ComfortZone HVAC",
            amount: 456.88m,
            incurredAt: new DateTime(2026, 05, 05, 0, 0, 0, DateTimeKind.Utc),
            category: ScheduleECategory.Repairs,
            status: ExpenseStatus.Pending,
            propertyId: property.Id);
        SeedExpense(
            db, _portfolioId,
            description: "Paid insurance",
            amount: 300m,
            incurredAt: new DateTime(2026, 05, 06, 0, 0, 0, DateTimeKind.Utc),
            category: ScheduleECategory.Insurance,
            status: ExpenseStatus.Paid,
            propertyId: property.Id);

        var page = await NewService(db).GetTransactionsAsync(
            _portfolioId,
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

    // ─────────────────────────────────────────────────────────────────────────────────────────────
    // (c) Bank rows surface, and a lowercase search term ILIKE-matches mixed-case text.
    // ─────────────────────────────────────────────────────────────────────────────────────────────
    [SkippableFact]
    public async Task GetTransactionsAsync_IncludesBankRows_AndSearchesCaseInsensitively()
    {
        SkipIfNoDocker();
        await using var db = NewContext(_ownerConnString);

        SeedBankTransaction(
            db, _portfolioId,
            description: "HOME DEPOT STORE",
            merchantName: "Home Depot",
            amount: -84.25m,
            postedAt: new DateTime(2026, 06, 01, 0, 0, 0, DateTimeKind.Utc),
            category: "Hardware",
            matchStatus: "Unmatched");

        var page = await NewService(db).GetTransactionsAsync(
            _portfolioId,
            new AccountingTransactionsQuery
            {
                Kind = "Bank",
                Search = "home depot", // lowercase → proves ILIKE case-insensitivity over "HOME DEPOT STORE"
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

    // ─────────────────────────────────────────────────────────────────────────────────────────────
    // (d) A confirmed (Matched) bank line marks the payment reconciled with the cleared fields.
    // ─────────────────────────────────────────────────────────────────────────────────────────────
    [SkippableFact]
    public async Task GetTransactionsAsync_MatchedBankLine_MarksPaymentReconciledWithClearedFields()
    {
        SkipIfNoDocker();
        await using var db = NewContext(_ownerConnString);
        var date = new DateTime(2026, 06, 01, 0, 0, 0, DateTimeKind.Utc);

        SeedPropertyLeaseAndPayment(db, _portfolioId, date);
        var paymentId = await db.Payments.Where(p => p.PortfolioId == _portfolioId).Select(p => p.Id).SingleAsync();

        SeedBankTransaction(
            db, _portfolioId,
            description: "Tenant ACH",
            merchantName: "Maria Tenant",
            amount: 1200m,
            postedAt: date,
            category: "Deposit",
            matchStatus: "Matched",
            matchedPaymentId: paymentId);

        var page = await NewService(db).GetTransactionsAsync(
            _portfolioId,
            new AccountingTransactionsQuery { Kind = "Payment", Take = 20 },
            CancellationToken.None);

        var paymentRow = page.Items.Single(t => t.Kind == "Payment" && t.Id == paymentId);
        paymentRow.Reconciled.Should().BeTrue();
        paymentRow.ClearedBankName.Should().Be("Sandbox Bank");
        paymentRow.ClearedAt.Should().Be(date);
        paymentRow.SuggestedBankMatch.Should().BeNull();
    }

    // ─────────────────────────────────────────────────────────────────────────────────────────────
    // (e) An unmatched bank line surfaces a suggestion on the payment row.
    // ─────────────────────────────────────────────────────────────────────────────────────────────
    [SkippableFact]
    public async Task GetTransactionsAsync_UnmatchedBankLine_SurfacesSuggestedBankMatchOnPayment()
    {
        SkipIfNoDocker();
        await using var db = NewContext(_ownerConnString);
        var date = new DateTime(2026, 06, 01, 0, 0, 0, DateTimeKind.Utc);

        SeedPropertyLeaseAndPayment(db, _portfolioId, date); // tenant "Maria Tenant", $1,200 due that day
        var paymentId = await db.Payments.Where(p => p.PortfolioId == _portfolioId).Select(p => p.Id).SingleAsync();

        SeedBankTransaction(
            db, _portfolioId,
            description: "ACH CREDIT",
            merchantName: "Maria Tenant",
            amount: 1200m,
            postedAt: date,
            category: "Deposit",
            matchStatus: "Unmatched");

        var page = await NewService(db).GetTransactionsAsync(
            _portfolioId,
            new AccountingTransactionsQuery { Kind = "Payment", Take = 20 },
            CancellationToken.None);

        var paymentRow = page.Items.Single(t => t.Kind == "Payment" && t.Id == paymentId);
        paymentRow.Reconciled.Should().BeFalse();
        paymentRow.SuggestedBankMatch.Should().NotBeNull();
        paymentRow.SuggestedBankMatch!.Amount.Should().Be(1200m);
        paymentRow.SuggestedBankMatch.Date.Should().Be(date);
        paymentRow.SuggestedBankMatch.Name.Should().Be("Maria Tenant");
        paymentRow.SuggestedBankMatch.Confidence.Should().BeGreaterThan(0m);
    }

    // ─────────────────────────────────────────────────────────────────────────────────────────────
    // (f) An unmatched withdrawal surfaces a suggestion on the expense row.
    // ─────────────────────────────────────────────────────────────────────────────────────────────
    [SkippableFact]
    public async Task GetTransactionsAsync_UnmatchedBankLine_SurfacesSuggestedBankMatchOnExpense()
    {
        SkipIfNoDocker();
        await using var db = NewContext(_ownerConnString);
        var date = new DateTime(2026, 06, 02, 0, 0, 0, DateTimeKind.Utc);

        var expense = SeedExpense(
            db, _portfolioId,
            description: "Hardware supply",
            amount: 84.25m,
            incurredAt: date,
            category: ScheduleECategory.Repairs,
            status: ExpenseStatus.Paid);

        SeedBankTransaction(
            db, _portfolioId,
            description: "HARDWARE STORE",
            merchantName: "Hardware Store",
            amount: -84.25m,
            postedAt: date,
            category: "Withdrawal",
            matchStatus: "Unmatched");

        var page = await NewService(db).GetTransactionsAsync(
            _portfolioId,
            new AccountingTransactionsQuery { Kind = "Expense", Take = 20 },
            CancellationToken.None);

        var expenseRow = page.Items.Single(t => t.Kind == "Expense" && t.Id == expense.Id);
        expenseRow.Reconciled.Should().BeFalse();
        expenseRow.SuggestedBankMatch.Should().NotBeNull();
        expenseRow.SuggestedBankMatch!.Amount.Should().Be(-84.25m);
    }

    // ─────────────────────────────────────────────────────────────────────────────────────────────
    // (g) No open bank line → the payment is unreconciled with no suggestion.
    // ─────────────────────────────────────────────────────────────────────────────────────────────
    [SkippableFact]
    public async Task GetTransactionsAsync_NoOpenBankLine_LeavesPaymentUnreconciledWithNoSuggestion()
    {
        SkipIfNoDocker();
        await using var db = NewContext(_ownerConnString);
        var date = new DateTime(2026, 06, 01, 0, 0, 0, DateTimeKind.Utc);

        SeedPropertyLeaseAndPayment(db, _portfolioId, date);
        var paymentId = await db.Payments.Where(p => p.PortfolioId == _portfolioId).Select(p => p.Id).SingleAsync();

        var page = await NewService(db).GetTransactionsAsync(
            _portfolioId,
            new AccountingTransactionsQuery { Kind = "Payment", Take = 20 },
            CancellationToken.None);

        var paymentRow = page.Items.Single(t => t.Kind == "Payment" && t.Id == paymentId);
        paymentRow.Reconciled.Should().BeFalse();
        paymentRow.SuggestedBankMatch.Should().BeNull();
    }

    // ─────────────────────────────────────────────────────────────────────────────────────────────
    // NEW — the load-bearing guarantee: soft-delete is enforced INSIDE the view. A payment whose lease
    // is soft-deleted must drop out of the grid (the view's INNER JOIN Leases ... AND DeletedAt IS NULL).
    // ─────────────────────────────────────────────────────────────────────────────────────────────
    [SkippableFact]
    public async Task GetTransactionsAsync_ExcludesPaymentsOfSoftDeletedLease()
    {
        SkipIfNoDocker();
        await using var db = NewContext(_ownerConnString);
        var date = new DateTime(2026, 06, 01, 0, 0, 0, DateTimeKind.Utc);

        SeedPropertyLeaseAndPayment(db, _portfolioId, date);
        var paymentId = await db.Payments.Where(p => p.PortfolioId == _portfolioId).Select(p => p.Id).SingleAsync();

        // Confirm the overdue payment is visible in the grid before the lease is soft-deleted.
        var before = await NewService(db).GetTransactionsAsync(
            _portfolioId,
            new AccountingTransactionsQuery { Kind = "Payment", Take = 20 },
            CancellationToken.None);
        before.Items.Should().Contain(t => t.Kind == "Payment" && t.Id == paymentId,
            "the payment is visible while its lease is live");

        // Soft-delete the lease (mark DeletedAt and persist on the owner context).
        var lease = await db.Leases.Where(l => l.PortfolioId == _portfolioId).SingleAsync();
        lease.DeletedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();

        var after = await NewService(db).GetTransactionsAsync(
            _portfolioId,
            new AccountingTransactionsQuery { Kind = "Payment", Take = 20 },
            CancellationToken.None);

        after.Items.Should().NotContain(t => t.Kind == "Payment" && t.Id == paymentId,
            "the view's INNER JOIN Leases ... AND DeletedAt IS NULL drops payments of a soft-deleted lease");
    }

    // ───────────────────────────────── helpers ─────────────────────────────────

    private void SkipIfNoDocker() =>
        Skip.IfNot(_dockerAvailable, "Docker is not available; accounting-view runtime verification skipped.");

    private static RentalCommandDbContext NewContext(string connString) =>
        new(new DbContextOptionsBuilder<RentalCommandDbContext>().UseNpgsql(connString).Options);

    private static AccountingService NewService(RentalCommandDbContext db) =>
        new(db, new ScheduleEService(db), new YearEndPacketPdfGenerator());

    /// <summary>
    /// Seeds a property + unit + tenant ("Maria Tenant") + active lease + one Scheduled rent payment
    /// (amount $1,200, due yesterday relative to <paramref name="now"/>). Returns the property; query
    /// <c>db.Payments</c> for the payment id afterward.
    /// </summary>
    private static Property SeedPropertyLeaseAndPayment(RentalCommandDbContext db, int portfolioId, DateTime now)
    {
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
        db.Payments.Add(new Payment
        {
            PortfolioId = portfolioId,
            Lease = lease,
            PaymentType = PaymentType.Rent,
            Status = PaymentStatus.Scheduled,
            Amount = 1200m,
            DueDate = now.AddDays(-1),
            CreatedAt = now,
            UpdatedAt = now,
        });
        db.SaveChanges();
        return property;
    }

    private static Expense SeedExpense(
        RentalCommandDbContext db,
        int portfolioId,
        string description,
        decimal amount,
        DateTime incurredAt,
        ScheduleECategory category,
        ExpenseStatus status,
        int? propertyId = null)
    {
        var expense = new Expense
        {
            PortfolioId = portfolioId,
            PropertyId = propertyId,
            Category = category,
            Description = description,
            Status = status,
            Amount = amount,
            IncurredAt = incurredAt,
            CreatedAt = incurredAt,
            UpdatedAt = incurredAt,
        };
        db.Expenses.Add(expense);
        db.SaveChanges();
        return expense;
    }

    private static BankTransaction SeedBankTransaction(
        RentalCommandDbContext db,
        int portfolioId,
        string description,
        string merchantName,
        decimal amount,
        DateTime postedAt,
        string category,
        string matchStatus,
        int? matchedPaymentId = null,
        int? matchedExpenseId = null)
    {
        var connection = db.BankConnections.FirstOrDefault(c => c.PortfolioId == portfolioId) ?? new BankConnection
        {
            PortfolioId = portfolioId,
            Provider = "Plaid",
            InstitutionName = "Sandbox Bank",
            AccountName = "Checking",
            Status = "Active",
            CreatedAt = postedAt,
            UpdatedAt = postedAt,
        };
        if (connection.Id == 0) db.BankConnections.Add(connection);

        var transaction = new BankTransaction
        {
            PortfolioId = portfolioId,
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
        db.BankTransactions.Add(transaction);
        db.SaveChanges();
        return transaction;
    }
}
