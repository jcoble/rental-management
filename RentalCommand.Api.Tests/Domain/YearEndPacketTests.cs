using System.Text;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using RentalCommand.Api.Services.Domain;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Data;

namespace RentalCommand.Api.Tests.Domain;

/// <summary>
/// Covers the year-end accountant packet: that the gathered data reconciles with the existing
/// <see cref="ScheduleEService"/> computation for the same year, and that the generator produces a
/// non-empty PDF.
/// </summary>
public class YearEndPacketTests : IDisposable
{
    private const int PortfolioId = 1;
    private const int Year = 2025;

    private readonly SqliteConnection _conn;
    private readonly RentalCommandDbContext _db;
    private readonly ScheduleEService _scheduleE;
    private readonly AccountingService _sut;

    public YearEndPacketTests()
    {
        // QuestPDF community license (set in Program.cs at runtime; tests don't run Program).
        QuestPDF.Settings.License = QuestPDF.Infrastructure.LicenseType.Community;

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
            Name = "Frank's Rentals",
            ManagementCompanyName = "Frank Property Co",
            TimeZone = "UTC",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        _db.SaveChanges();

        _scheduleE = new ScheduleEService(_db);
        _sut = new AccountingService(_db, _scheduleE, new YearEndPacketPdfGenerator());
    }

    public void Dispose()
    {
        _db.Dispose();
        _conn.Dispose();
    }

    [Fact]
    public async Task GetYearEndPacketAsync_ReturnsNonEmptyPdfBytes()
    {
        SeedYear(Year);

        var pdf = await _sut.GetYearEndPacketAsync(PortfolioId, Year, CancellationToken.None);

        pdf.Should().NotBeNullOrEmpty();
        // Valid PDFs start with the "%PDF" magic header.
        Encoding.ASCII.GetString(pdf, 0, 4).Should().Be("%PDF");
    }

    [Fact]
    public async Task GetYearEndPacketAsync_RendersEvenWithNoActivity()
    {
        // No data seeded beyond the portfolio: the packet should still render a valid PDF.
        var pdf = await _sut.GetYearEndPacketAsync(PortfolioId, Year, CancellationToken.None);

        pdf.Should().NotBeNullOrEmpty();
        Encoding.ASCII.GetString(pdf, 0, 4).Should().Be("%PDF");
    }

    [Fact]
    public async Task GetYearEndPacketData_ScheduleETotalsMatchScheduleEService()
    {
        SeedYear(Year);

        var packet = await _sut.GetYearEndPacketDataAsync(PortfolioId, Year, CancellationToken.None);
        var scheduleE = await _scheduleE.GetReportAsync(PortfolioId, Year, CancellationToken.None);

        // The packet must embed the exact same Schedule E numbers the standalone report/CSV produces.
        packet.ScheduleE.Year.Should().Be(scheduleE.Year);
        packet.ScheduleE.TotalRentalIncome.Should().Be(scheduleE.TotalRentalIncome);
        packet.ScheduleE.TotalExpenses.Should().Be(scheduleE.TotalExpenses);
        packet.ScheduleE.NetIncome.Should().Be(scheduleE.NetIncome);
        packet.ScheduleE.Properties.Should().HaveCount(scheduleE.Properties.Count);
    }

    [Fact]
    public async Task GetYearEndPacketData_BuildsPnLCashFlowAndRentRoll()
    {
        SeedYear(Year);

        var packet = await _sut.GetYearEndPacketDataAsync(PortfolioId, Year, CancellationToken.None);

        packet.PortfolioName.Should().Be("Frank's Rentals");
        packet.ManagementCompanyName.Should().Be("Frank Property Co");

        // Per-property P&L: one property with $14,400 rent and $2,000 + $600 = $2,600 expenses.
        packet.Properties.Should().HaveCount(1);
        var pnl = packet.Properties[0];
        pnl.Income.Should().Be(14_400m);
        pnl.TotalExpenses.Should().Be(2_600m);
        pnl.Net.Should().Be(11_800m);
        pnl.ExpensesByCategory.Should().Contain(c => c.Category == "Repairs" && c.Amount == 2_000m);
        pnl.ExpensesByCategory.Should().Contain(c => c.Category == "Insurance" && c.Amount == 600m);

        // Cash flow: 12 months, year total money-in = rent collected, money-out = expenses paid.
        packet.CashFlow.Should().HaveCount(12);
        packet.CashFlowMoneyIn.Should().Be(14_400m);
        packet.CashFlowMoneyOut.Should().Be(2_600m);
        packet.CashFlowNet.Should().Be(11_800m);

        // Rent roll: the single active lease, with a $1,200 past-due balance.
        packet.RentRoll.Should().HaveCount(1);
        var row = packet.RentRoll[0];
        row.TenantName.Should().Be("Maria Tenant");
        row.MonthlyRent.Should().Be(1_200m);
        row.PastDueBalance.Should().Be(1_200m);
        row.LeaseStatus.Should().Be("Active");
    }

    /// <summary>
    /// Seeds one property/unit/tenant/active lease, 12 monthly $1,200 rent payments (paid in-year),
    /// a $2,000 repair and $600 insurance expense (in-year), and one past-due scheduled rent payment.
    /// </summary>
    private void SeedYear(int year)
    {
        var anchor = new DateTime(year, 1, 1, 12, 0, 0, DateTimeKind.Utc);

        var property = new Property
        {
            PortfolioId = PortfolioId,
            Name = "Maple Street Duplex",
            AddressLine1 = "10 Maple St",
            City = "Columbus",
            State = "OH",
            PostalCode = "43219",
            CreatedAt = anchor,
            UpdatedAt = anchor,
        };
        var unit = new Unit
        {
            Property = property,
            UnitNumber = "A",
            MarketRent = 1_200m,
            CreatedAt = anchor,
            UpdatedAt = anchor,
        };
        var tenant = new Tenant
        {
            PortfolioId = PortfolioId,
            FirstName = "Maria",
            LastName = "Tenant",
            CreatedAt = anchor,
            UpdatedAt = anchor,
        };
        var lease = new Lease
        {
            PortfolioId = PortfolioId,
            Property = property,
            Unit = unit,
            Tenant = tenant,
            LeaseNumber = "L-100",
            Status = LeaseStatus.Active,
            StartDate = new DateTime(year, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            EndDate = new DateTime(year + 1, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            MonthlyRent = 1_200m,
            SecurityDeposit = 1_200m,
            CreatedAt = anchor,
            UpdatedAt = anchor,
        };
        _db.Leases.Add(lease);
        _db.SaveChanges();

        // 12 paid monthly rent payments in the year.
        for (var month = 1; month <= 12; month++)
        {
            var paid = new DateTime(year, month, 1, 0, 0, 0, DateTimeKind.Utc);
            _db.Payments.Add(new Payment
            {
                PortfolioId = PortfolioId,
                Lease = lease,
                PaymentType = PaymentType.Rent,
                Status = PaymentStatus.Paid,
                Amount = 1_200m,
                DueDate = paid,
                PaidDate = paid,
                Method = "Check",
                CreatedAt = paid,
                UpdatedAt = paid,
            });
        }

        // One past-due scheduled rent payment (drives the rent-roll past-due balance).
        _db.Payments.Add(new Payment
        {
            PortfolioId = PortfolioId,
            Lease = lease,
            PaymentType = PaymentType.Rent,
            Status = PaymentStatus.Scheduled,
            Amount = 1_200m,
            DueDate = new DateTime(year, 6, 1, 0, 0, 0, DateTimeKind.Utc),
            CreatedAt = anchor,
            UpdatedAt = anchor,
        });

        // Expenses in-year.
        _db.Expenses.Add(new Expense
        {
            PortfolioId = PortfolioId,
            PropertyId = property.Id,
            Category = ScheduleECategory.Repairs,
            Description = "Roof repair",
            Status = ExpenseStatus.Paid,
            Amount = 2_000m,
            IncurredAt = new DateTime(year, 3, 15, 0, 0, 0, DateTimeKind.Utc),
            PaidAt = new DateTime(year, 3, 20, 0, 0, 0, DateTimeKind.Utc),
            CreatedAt = anchor,
            UpdatedAt = anchor,
        });
        _db.Expenses.Add(new Expense
        {
            PortfolioId = PortfolioId,
            PropertyId = property.Id,
            Category = ScheduleECategory.Insurance,
            Description = "Annual policy",
            Status = ExpenseStatus.Paid,
            Amount = 600m,
            IncurredAt = new DateTime(year, 1, 10, 0, 0, 0, DateTimeKind.Utc),
            PaidAt = new DateTime(year, 1, 10, 0, 0, 0, DateTimeKind.Utc),
            CreatedAt = anchor,
            UpdatedAt = anchor,
        });
        _db.SaveChanges();
    }
}
