using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Moq;
using RentalCommand.Api.DTOs;
using RentalCommand.Api.Services.Domain;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Interfaces;
using RentalCommand.TestCommon;

namespace RentalCommand.Api.Tests.Domain;

/// <summary>
/// Portfolio-scoped CRUD for loans: create validates the property in-portfolio (rejects
/// out-of-portfolio refs — the IDOR guard), update/soft-delete behave, the amortization schedule is
/// readable and IDOR-guarded, and the soft-delete query filter hides removed loans.
/// </summary>
public class LoanServiceTests : IDisposable
{
    private const int PortfolioId = 1;

    private readonly SqliteTestContext _ctx = new();
    private readonly LoanService _sut;

    public LoanServiceTests()
    {
        _sut = new LoanService(_ctx.Db, Mock.Of<IDataUpdateService>(), TimeProvider.System);
    }

    public void Dispose() => _ctx.Dispose();

    [Fact]
    public async Task CreateAsync_PersistsLoan_DefaultsBalanceToOriginal()
    {
        var property = SeedProperty();

        var result = await _sut.CreateAsync(PortfolioId, new CreateLoanRequest
        {
            PropertyId = property.Id,
            Lender = "Acme Bank",
            OriginalAmount = 200_000m,
            AnnualInterestRatePct = 6m,
            TermMonths = 360,
            StartDate = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            MonthlyPrincipalInterest = 1199.10m,
            MonthlyEscrow = 350m,
            EscrowCoversTaxes = true,
        });

        result.Should().NotBeNull();
        result!.Lender.Should().Be("Acme Bank");
        result.CurrentBalance.Should().Be(200_000m);  // defaulted from original
        result.PropertyName.Should().Be(property.Name);
        result.Status.Should().Be(LoanStatus.Active);
        _ctx.Db.Loans.Should().HaveCount(1);
    }

    [Fact]
    public async Task CreateAsync_RejectsPropertyOutsidePortfolio()
    {
        SeedPortfolio(999);
        var foreign = SeedProperty(portfolioId: 999);

        var result = await _sut.CreateAsync(PortfolioId, new CreateLoanRequest
        {
            PropertyId = foreign.Id,
            Lender = "Should not link",
            OriginalAmount = 100_000m,
            TermMonths = 360,
            StartDate = DateTime.UtcNow,
            MonthlyPrincipalInterest = 600m,
        });

        result.Should().BeNull();
        _ctx.Db.Loans.Should().BeEmpty();
    }

    [Fact]
    public async Task UpdateAsync_ChangesFields()
    {
        var property = SeedProperty();
        var loan = SeedLoan(property.Id);

        var result = await _sut.UpdateAsync(PortfolioId, loan.Id, new UpdateLoanRequest
        {
            Lender = "Renamed Lender",
            Status = LoanStatus.Closed,
            MonthlyEscrow = 400m,
        });

        result.Should().NotBeNull();
        result!.Lender.Should().Be("Renamed Lender");
        result.Status.Should().Be(LoanStatus.Closed);
        result.MonthlyEscrow.Should().Be(400m);
    }

    [Fact]
    public async Task GetAsync_AndUpdate_RejectCrossPortfolio()
    {
        var property = SeedProperty();
        var loan = SeedLoan(property.Id);

        // A different portfolio cannot read or mutate this loan.
        (await _sut.GetAsync(portfolioId: 2, loan.Id)).Should().BeNull();
        (await _sut.UpdateAsync(portfolioId: 2, loan.Id, new UpdateLoanRequest { Lender = "hax" })).Should().BeNull();
        (await _sut.DeleteAsync(portfolioId: 2, loan.Id)).Should().BeFalse();
        (await _sut.GetPaymentsAsync(portfolioId: 2, loan.Id)).Should().BeNull();
    }

    [Fact]
    public async Task DeleteAsync_SoftDeletes_AndHidesFromReads()
    {
        var property = SeedProperty();
        var loan = SeedLoan(property.Id);

        (await _sut.DeleteAsync(PortfolioId, loan.Id)).Should().BeTrue();

        (await _sut.GetAsync(PortfolioId, loan.Id)).Should().BeNull();
        (await _sut.ListAsync(PortfolioId, propertyId: null, new ListQuery())).Should().BeEmpty();
        _ctx.Db.Loans.IgnoreQueryFilters().Should().HaveCount(1); // soft, not hard
    }

    [Fact]
    public async Task GetPaymentsAsync_ReturnsScheduleOrderedByPeriod()
    {
        var property = SeedProperty();
        var loan = SeedLoan(property.Id);

        _ctx.Db.LoanPayments.AddRange(
            MakePayment(loan, "2024-02", 199_600.80m),
            MakePayment(loan, "2024-01", 199_800.90m));
        _ctx.Db.SaveChanges();

        var schedule = await _sut.GetPaymentsAsync(PortfolioId, loan.Id);

        schedule.Should().NotBeNull();
        schedule!.Should().HaveCount(2);
        schedule[0].PeriodKey.Should().Be("2024-01"); // ordered ascending
        schedule[1].PeriodKey.Should().Be("2024-02");
    }

    // -----------------------------------------------------------------------
    // Helpers

    private void SeedPortfolio(int id)
    {
        var now = DateTime.UtcNow;
        _ctx.Db.Portfolios.Add(new Portfolio
        {
            Id = id,
            Name = $"Portfolio {id}",
            ManagementCompanyName = "Test Co",
            TimeZone = "UTC",
            CreatedAt = now,
            UpdatedAt = now,
        });
        _ctx.Db.SaveChanges();
    }

    private Property SeedProperty(int portfolioId = PortfolioId)
    {
        var now = DateTime.UtcNow;
        var property = new Property
        {
            PortfolioId = portfolioId,
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

    private Loan SeedLoan(int propertyId)
    {
        var now = DateTime.UtcNow;
        var loan = new Loan
        {
            PortfolioId = PortfolioId,
            PropertyId = propertyId,
            Lender = "Acme Bank",
            OriginalAmount = 200_000m,
            CurrentBalance = 200_000m,
            AnnualInterestRatePct = 6m,
            TermMonths = 360,
            StartDate = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            DayOfMonthDue = 1,
            MonthlyPrincipalInterest = 1199.10m,
            Status = LoanStatus.Active,
            CreatedAt = now,
            UpdatedAt = now,
        };
        _ctx.Db.Loans.Add(loan);
        _ctx.Db.SaveChanges();
        return loan;
    }

    private static LoanPayment MakePayment(Loan loan, string periodKey, decimal balanceAfter) => new()
    {
        PortfolioId = loan.PortfolioId,
        LoanId = loan.Id,
        PeriodKey = periodKey,
        DueDate = DateTime.UtcNow,
        InterestAmount = 1000m,
        PrincipalAmount = 199.10m,
        EscrowAmount = 0m,
        TotalAmount = 1199.10m,
        BalanceAfter = balanceAfter,
        Status = LoanPaymentStatus.Scheduled,
        CreatedAt = DateTime.UtcNow,
    };
}
