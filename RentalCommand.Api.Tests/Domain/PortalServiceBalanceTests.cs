using System.Data.Common;
using FluentAssertions;
using Microsoft.EntityFrameworkCore.Diagnostics;
using RentalCommand.Api.Services.Domain;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.TestCommon;

namespace RentalCommand.Api.Tests.Domain;

public class PortalServiceBalanceTests : IDisposable
{
    private const int PortfolioId = 1;

    private readonly List<string> _commands = [];
    private readonly SqliteTestContext _ctx;
    private readonly PortalService _sut;

    public PortalServiceBalanceTests()
    {
        _ctx = new SqliteTestContext([new RecordingCommandInterceptor(_commands)]);
        _sut = new PortalService(_ctx.Db, new NoopLeaseQaService());
    }

    public void Dispose() => _ctx.Dispose();

    [Fact]
    public async Task GetBalanceAsync_TreatsDueTodayAsOutstandingButNotOverdue()
    {
        var tenant = SeedTenant("Blake", "Hayes");
        var otherTenant = SeedTenant("Other", "Tenant");
        var lease = SeedLease(tenant);
        var otherLease = SeedLease(otherTenant);
        var todayUtc = DateTime.UtcNow.Date;

        SeedPayment(lease, PaymentStatus.Scheduled, 100m, todayUtc);
        SeedPayment(lease, PaymentStatus.Partial, 80m, todayUtc, amountPaid: 20m);
        SeedPayment(lease, PaymentStatus.Scheduled, 30m, todayUtc.AddDays(-1));
        SeedPayment(lease, PaymentStatus.Late, 40m, todayUtc);
        SeedPayment(lease, PaymentStatus.Paid, 25m, todayUtc);
        SeedPayment(otherLease, PaymentStatus.Scheduled, 999m, todayUtc.AddDays(-1));
        _ctx.Db.SaveChanges();

        _commands.Clear();

        var result = await _sut.GetBalanceAsync(PortfolioId, tenant.Id);

        result.Collected.Should().Be(45m);
        result.Outstanding.Should().Be(230m);
        result.Overdue.Should().Be(70m);
        result.OverdueCount.Should().Be(2);

        var balanceQueries = _commands
            .Where(sql => sql.Contains("FROM \"Payments\"", StringComparison.OrdinalIgnoreCase))
            .ToList();
        balanceQueries.Should().ContainSingle("portal balance should be one DB-side aggregate query");
        balanceQueries[0].Should().Contain("ef_sum");
        balanceQueries[0].Should().Contain("COUNT");
    }

    [Fact]
    public async Task GetBalanceAsync_CountsPastDueFailedPaymentAsOutstandingAndOverdue()
    {
        // A Failed charge collected nothing, so its full Amount is still owed — and once it is past
        // due it is overdue too (Outstanding + Overdue + overdueCount). This matches the payments UI,
        // which marks Failed as "still owed" and keeps it payable. A separate Paid payment is all that
        // lands in Collected; the failed amount is not double-counted there.
        var tenant = SeedTenant("Marcus", "Williams");
        var lease = SeedLease(tenant);
        var todayUtc = DateTime.UtcNow.Date;

        SeedPayment(lease, PaymentStatus.Failed, 1050m, todayUtc.AddDays(-1));
        SeedPayment(lease, PaymentStatus.Paid, 1050m, todayUtc.AddDays(-30));
        _ctx.Db.SaveChanges();

        _commands.Clear();

        var result = await _sut.GetBalanceAsync(PortfolioId, tenant.Id);

        result.Collected.Should().Be(1050m);
        result.Outstanding.Should().Be(1050m);
        result.Overdue.Should().Be(1050m);
        result.OverdueCount.Should().Be(1);

        var balanceQueries = _commands
            .Where(sql => sql.Contains("FROM \"Payments\"", StringComparison.OrdinalIgnoreCase))
            .ToList();
        balanceQueries.Should().ContainSingle("portal balance should remain one DB-side aggregate query");
    }

    private Tenant SeedTenant(string firstName, string lastName)
    {
        var now = DateTime.UtcNow;
        var tenant = new Tenant
        {
            PortfolioId = PortfolioId,
            FirstName = firstName,
            LastName = lastName,
            Email = $"{firstName.ToLowerInvariant()}.{lastName.ToLowerInvariant()}@example.local",
            CreatedAt = now,
            UpdatedAt = now,
        };

        _ctx.Db.Tenants.Add(tenant);
        _ctx.Db.SaveChanges();
        return tenant;
    }

    private Lease SeedLease(Tenant tenant)
    {
        var now = DateTime.UtcNow;
        var property = new Property
        {
            PortfolioId = PortfolioId,
            Name = $"{tenant.FirstName} Flats",
            AddressLine1 = "1188 Maple Ave",
            City = "Columbus",
            State = "OH",
            PostalCode = "43201",
            CreatedAt = now,
            UpdatedAt = now,
        };

        var lease = new Lease
        {
            PortfolioId = PortfolioId,
            Property = property,
            Unit = new Unit
            {
                Property = property,
                UnitNumber = "2B",
                Bedrooms = 2,
                Bathrooms = 1,
                MarketRent = 1200m,
                CreatedAt = now,
                UpdatedAt = now,
            },
            Tenant = tenant,
            LeaseNumber = $"L-{tenant.FirstName}",
            Status = LeaseStatus.Active,
            StartDate = now.Date.AddMonths(-1),
            EndDate = now.Date.AddYears(1),
            MonthlyRent = 1200m,
            CreatedAt = now,
            UpdatedAt = now,
        };

        _ctx.Db.Leases.Add(lease);
        _ctx.Db.SaveChanges();
        return lease;
    }

    private void SeedPayment(
        Lease lease,
        PaymentStatus status,
        decimal amount,
        DateTime dueDate,
        decimal? amountPaid = null)
    {
        var now = DateTime.UtcNow;
        _ctx.Db.Payments.Add(new Payment
        {
            PortfolioId = PortfolioId,
            Lease = lease,
            PaymentType = PaymentType.Rent,
            Status = status,
            Amount = amount,
            AmountPaid = amountPaid,
            DueDate = dueDate,
            PaidDate = status == PaymentStatus.Paid ? dueDate : null,
            CreatedAt = now,
            UpdatedAt = now,
        });
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
