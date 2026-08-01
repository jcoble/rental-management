using System.Data.Common;
using FluentAssertions;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Moq;
using RentalCommand.Api.DTOs;
using RentalCommand.Api.Services.Domain;
using RentalCommand.Core.Atomic;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.TestCommon;

namespace RentalCommand.Api.Tests.Domain;

/// <summary>
/// Portfolio-scoped loan reads, SQL-side paging, amortization schedule access, and the contract that
/// all business mutations flow through scoped receipt-backed commands.
/// </summary>
public class LoanServiceTests : IDisposable
{
    private const int PortfolioId = 1;

    private readonly List<string> _commands = [];
    private readonly SqliteTestContext _ctx;
    private readonly LoanService _sut;

    public LoanServiceTests()
    {
        _ctx = new SqliteTestContext([new RecordingCommandInterceptor(_commands)]);
        _sut = new LoanService(_ctx.Db, TimeProvider.System, Mock.Of<IAtomicUnitOfWork>());
    }

    public void Dispose() => _ctx.Dispose();

    [Fact]
    public async Task GetAsync_AndPayments_RejectCrossPortfolio()
    {
        var property = SeedProperty();
        var loan = SeedLoan(property.Id);

        // A different portfolio cannot read or mutate this loan.
        (await _sut.GetAsync(portfolioId: 2, loan.Id)).Should().BeNull();
        (await _sut.GetPaymentsAsync(portfolioId: 2, loan.Id)).Should().BeNull();
    }

    [Fact]
    public void MutationsExposeOnlyScopedReceiptBackedOverloads()
    {
        var mutationMethods = typeof(ILoanService).GetMethods()
            .Where(method => method.Name is "CreateAsync" or "UpdateAsync" or "DeleteAsync")
            .ToArray();

        mutationMethods.Should().HaveCount(3);
        mutationMethods.Should().OnlyContain(method =>
            method.GetParameters().First().ParameterType == typeof(RentalCommand.Core.Authorization.WorkspaceReadScope) &&
            method.GetParameters().Any(parameter => parameter.Name == "idempotencyKey"));
    }

    [Fact]
    public async Task ListPageAsync_FiltersByStartDate_AndReturnsPagedMetadata()
    {
        var property = SeedProperty();
        SeedLoan(property.Id, lender: "January Bank", startDate: new DateTime(2026, 1, 15, 0, 0, 0, DateTimeKind.Utc));
        SeedLoan(property.Id, lender: "February Bank", startDate: new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc));
        SeedLoan(property.Id, lender: "March Bank", startDate: new DateTime(2026, 3, 31, 0, 0, 0, DateTimeKind.Utc));

        var page = await _sut.ListPageAsync(PortfolioId, property.Id, new ListQuery
        {
            From = new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc),
            To = new DateTime(2026, 3, 31, 0, 0, 0, DateTimeKind.Utc),
            Sort = "-startDate",
            Skip = 0,
            Take = 1,
        });

        page.TotalCount.Should().Be(2);
        page.Skip.Should().Be(0);
        page.Take.Should().Be(1);
        page.Items.Should().ContainSingle();
        page.Items[0].Lender.Should().Be("March Bank");
        page.Items[0].PropertyName.Should().Be(property.Name);
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

    [Fact]
    public async Task GetPaymentsAsync_AppliesStatusDueDateSortAndPagingServerSide()
    {
        var property = SeedProperty();
        var loan = SeedLoan(property.Id);

        _ctx.Db.LoanPayments.AddRange(
            MakePayment(loan, "2024-01", 199_800.90m,
                dueDate: new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                status: LoanPaymentStatus.Paid),
            MakePayment(loan, "2024-02", 199_600.80m,
                dueDate: new DateTime(2024, 2, 1, 0, 0, 0, DateTimeKind.Utc)),
            MakePayment(loan, "2024-03", 199_400.70m,
                dueDate: new DateTime(2024, 3, 1, 0, 0, 0, DateTimeKind.Utc)),
            MakePayment(loan, "2024-04", 199_200.60m,
                dueDate: new DateTime(2024, 4, 1, 0, 0, 0, DateTimeKind.Utc)));
        _ctx.Db.SaveChanges();

        _commands.Clear();

        var schedule = await _sut.GetPaymentsAsync(PortfolioId, loan.Id, new LoanPaymentQuery
        {
            Status = LoanPaymentStatus.Scheduled,
            Sort = "dueDate",
            Skip = 1,
            Take = 1,
        });

        schedule.Should().NotBeNull();
        schedule!.Should().ContainSingle();
        schedule[0].PeriodKey.Should().Be("2024-03");
        _commands.Should().HaveCount(2,
            "the loan read uses one scope check and one effective schedule reader");
        var scheduleSql = _commands.Single(sql =>
            sql.Contains("\"LoanPaymentCorrections\"", StringComparison.OrdinalIgnoreCase));
        scheduleSql.Should()
            .Contain("\"LoanPayments\"")
            .And.Contain("\"LoanPaymentCorrections\"")
            .And.Contain("ORDER BY")
            .And.Contain("LIMIT")
            .And.Contain("OFFSET");
    }

    // -----------------------------------------------------------------------
    // Helpers

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

    private Loan SeedLoan(
        int propertyId,
        string lender = "Acme Bank",
        DateTime? startDate = null,
        decimal currentBalance = 200_000m)
    {
        var now = DateTime.UtcNow;
        var loan = new Loan
        {
            PortfolioId = PortfolioId,
            PropertyId = propertyId,
            Lender = lender,
            OriginalAmount = 200_000m,
            CurrentBalance = currentBalance,
            AnnualInterestRatePct = 6m,
            TermMonths = 360,
            StartDate = startDate ?? new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc),
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

    private static LoanPayment MakePayment(
        Loan loan,
        string periodKey,
        decimal balanceAfter,
        DateTime? dueDate = null,
        LoanPaymentStatus status = LoanPaymentStatus.Scheduled) => new()
    {
        PortfolioId = loan.PortfolioId,
        LoanId = loan.Id,
        PeriodKey = periodKey,
        DueDate = dueDate ?? DateTime.UtcNow,
        InterestAmount = 1000m,
        PrincipalAmount = 199.10m,
        EscrowAmount = 0m,
        TotalAmount = 1199.10m,
        BalanceAfter = balanceAfter,
        Status = status,
        CreatedAt = DateTime.UtcNow,
    };

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
