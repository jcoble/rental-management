using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Engine.Services;
using RentalCommand.TestCommon;

namespace RentalCommand.Engine.Tests.Automation;

/// <summary>
/// The debt-service worker turns active loans into the monthly amortization schedule. These tests
/// pin the spec §12 behavior and the §18 corrections at the worker level: idempotency per
/// (loan, period), the chained/immutable opening balance, catch-up across missed months, the
/// maturity stop (never a period past the term), and the cached-balance/status update.
/// </summary>
public class DebtServiceServiceTests : IDisposable
{
    private readonly SqliteTestContext _ctx;

    public DebtServiceServiceTests() => _ctx = new SqliteTestContext();

    public void Dispose() => _ctx.Dispose();

    private DebtServiceService BuildService()
    {
        // Pin the business zone to UTC so the test's "current month" math is deterministic.
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["App:TimeZone"] = "UTC" })
            .Build();
        return new DebtServiceService(_ctx.Db, config, NullLogger<DebtServiceService>.Instance);
    }

    private Property SeedProperty()
    {
        var now = DateTime.UtcNow;
        var property = new Property
        {
            PortfolioId = 1,
            Name = "Loan House",
            AddressLine1 = "1 Loan Ln",
            City = "Columbus",
            State = "OH",
            PostalCode = "43215",
            CreatedAt = now,
            UpdatedAt = now,
        };
        _ctx.Db.Properties.Add(property);
        _ctx.Db.SaveChanges();
        return property;
    }

    private Loan SeedLoan(int propertyId, DateTime startDate, int termMonths = 360,
        decimal original = 200_000m, decimal pi = 1199.10m, decimal escrow = 0m, decimal ratePct = 6m)
    {
        var now = DateTime.UtcNow;
        var loan = new Loan
        {
            PortfolioId = 1,
            PropertyId = propertyId,
            Lender = "Test Bank",
            OriginalAmount = original,
            CurrentBalance = original,
            AnnualInterestRatePct = ratePct,
            TermMonths = termMonths,
            StartDate = startDate,
            DayOfMonthDue = 1,
            MonthlyPrincipalInterest = pi,
            MonthlyEscrow = escrow,
            Status = LoanStatus.Active,
            CreatedAt = now,
            UpdatedAt = now,
        };
        _ctx.Db.Loans.Add(loan);
        _ctx.Db.SaveChanges();
        return loan;
    }

    [Fact]
    public async Task GeneratesFirstPeriod_WithCorrectSplit_AndIsIdempotent()
    {
        var property = SeedProperty();
        // Loan starts this month → exactly one period is due.
        var startThisMonth = new DateTime(DateTime.UtcNow.Year, DateTime.UtcNow.Month, 1, 0, 0, 0, DateTimeKind.Utc);
        var loan = SeedLoan(property.Id, startThisMonth);

        var sut = BuildService();

        (await sut.GenerateAsync()).Should().Be(1);
        (await sut.GenerateAsync()).Should().Be(0); // idempotent

        var payments = _ctx.Db.LoanPayments.OrderBy(p => p.PeriodKey).ToList();
        payments.Should().HaveCount(1);

        var p = payments[0];
        p.InterestAmount.Should().Be(1_000.00m);   // 200k * 0.5%
        p.PrincipalAmount.Should().Be(199.10m);
        p.TotalAmount.Should().Be(1_199.10m);
        p.BalanceAfter.Should().Be(199_800.90m);
        p.Status.Should().Be(LoanPaymentStatus.Scheduled);
        p.PeriodKey.Should().Be(startThisMonth.ToString("yyyy-MM"));

        // Cached balance mirrors the latest row.
        var refreshed = _ctx.Db.Loans.Single();
        refreshed.CurrentBalance.Should().Be(199_800.90m);
        refreshed.Status.Should().Be(LoanStatus.Active);
    }

    [Fact]
    public async Task CatchesUpMissedMonths_ChainingBalanceFromPriorPeriod()
    {
        var property = SeedProperty();
        // Loan started two months ago → 3 periods are due (this month + the two prior).
        var start = new DateTime(DateTime.UtcNow.Year, DateTime.UtcNow.Month, 1, 0, 0, 0, DateTimeKind.Utc).AddMonths(-2);
        SeedLoan(property.Id, start);

        var sut = BuildService();
        (await sut.GenerateAsync()).Should().Be(3);

        var rows = _ctx.Db.LoanPayments.OrderBy(p => p.PeriodKey).ToList();
        rows.Should().HaveCount(3);

        // Each period opens from the prior period's BalanceAfter (immutable chain, §18).
        rows[0].BalanceAfter.Should().Be(199_800.90m);
        // Period 2 interest is on 199,800.90 → 999.00 (rounded); principal 200.10.
        rows[1].InterestAmount.Should().Be(999.00m);
        rows[1].PrincipalAmount.Should().Be(200.10m);
        rows[1].BalanceAfter.Should().Be(199_600.80m);
        // Period 3 opens at period 2's BalanceAfter.
        rows[2].InterestAmount.Should().Be(998.00m);
    }

    [Fact]
    public async Task NeverGeneratesPastMaturity()
    {
        var property = SeedProperty();
        // A 2-month-term loan that started 5 months ago: only 2 periods may ever exist.
        var start = new DateTime(DateTime.UtcNow.Year, DateTime.UtcNow.Month, 1, 0, 0, 0, DateTimeKind.Utc).AddMonths(-5);
        SeedLoan(property.Id, start, termMonths: 2);

        var sut = BuildService();
        await sut.GenerateAsync();

        var rows = _ctx.Db.LoanPayments.ToList();
        rows.Should().HaveCount(2); // never a 3rd period despite 6 months elapsed
    }

    [Fact]
    public async Task PaysOffSmallLoan_StopsAndMarksLoanPaidOff()
    {
        var property = SeedProperty();
        // Small balance, big P&I, long elapsed window → pays off within a couple periods.
        var start = new DateTime(DateTime.UtcNow.Year, DateTime.UtcNow.Month, 1, 0, 0, 0, DateTimeKind.Utc).AddMonths(-6);
        SeedLoan(property.Id, start, termMonths: 360, original: 5_000m, pi: 2_000m);

        var sut = BuildService();
        await sut.GenerateAsync();

        var rows = _ctx.Db.LoanPayments.OrderBy(p => p.PeriodKey).ToList();
        rows.Last().BalanceAfter.Should().Be(0m);
        rows.Sum(r => r.PrincipalAmount).Should().Be(5_000m);

        var loan = _ctx.Db.Loans.Single();
        loan.Status.Should().Be(LoanStatus.PaidOff);
        loan.CurrentBalance.Should().Be(0m);

        // A subsequent run does nothing (loan no longer Active).
        (await sut.GenerateAsync()).Should().Be(0);
    }

    [Fact]
    public async Task IgnoresLoansBeforeTheirStartDate()
    {
        var property = SeedProperty();
        // Loan starts next month → nothing due yet.
        var start = new DateTime(DateTime.UtcNow.Year, DateTime.UtcNow.Month, 1, 0, 0, 0, DateTimeKind.Utc).AddMonths(1);
        SeedLoan(property.Id, start);

        var sut = BuildService();
        (await sut.GenerateAsync()).Should().Be(0);
        _ctx.Db.LoanPayments.Should().BeEmpty();
    }
}
