using System.Data.Common;
using FluentAssertions;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Moq;
using RentalCommand.Api.DTOs;
using RentalCommand.Api.Services.Domain;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Interfaces;
using RentalCommand.TestCommon;

namespace RentalCommand.Api.Tests.Domain;

public class LeaseServiceListTests : IDisposable
{
    private const int PortfolioId = 1;

    private readonly List<string> _commands = [];
    private readonly SqliteTestContext _ctx;
    private readonly LeaseService _sut;

    public LeaseServiceListTests()
    {
        _ctx = new SqliteTestContext([new RecordingCommandInterceptor(_commands)]);
        _sut = new LeaseService(
            _ctx.Db,
            Mock.Of<IDataUpdateService>(),
            Mock.Of<IFileStorage>(),
            Mock.Of<ILeaseAgreementPdfGenerator>(),
            Mock.Of<IAuditTrailService>(),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<LeaseService>.Instance,
            TimeProvider.System);
    }

    public void Dispose() => _ctx.Dispose();

    [Fact]
    public async Task ListPageAsync_FiltersSortsAndPagesInSql()
    {
        SeedLease("L-001", "Avery", "Ellis", LeaseStatus.Active);
        SeedLease("L-002", "Blair", "Kline", LeaseStatus.Active);
        SeedLease("L-003", "Casey", "Moss", LeaseStatus.Active);
        SeedLease("L-004", "Devon", "Nash", LeaseStatus.Draft);

        _commands.Clear();
        var result = await _sut.ListPageAsync(PortfolioId, new LeaseListQuery
        {
            Status = LeaseStatus.Active,
            Sort = "tenantName",
            Skip = 1,
            Take = 2,
        });

        result.TotalCount.Should().Be(3);
        result.Skip.Should().Be(1);
        result.Take.Should().Be(2);
        result.Items.Select(l => l.TenantName).Should().Equal("Blair Kline", "Casey Moss");
        result.Items.Should().OnlyContain(l => l.Status == LeaseStatus.Active);

        _commands.Should().Contain(sql =>
            sql.Contains("COUNT", StringComparison.OrdinalIgnoreCase) &&
            sql.Contains("FROM \"Leases\"", StringComparison.OrdinalIgnoreCase));
        _commands.Should().Contain(sql =>
            sql.Contains("ORDER BY", StringComparison.OrdinalIgnoreCase) &&
            sql.Contains("Tenants", StringComparison.OrdinalIgnoreCase) &&
            sql.Contains("LIMIT", StringComparison.OrdinalIgnoreCase) &&
            sql.Contains("OFFSET", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ListPageAsync_UnitIdFilter_ReturnsUnitLeaseHistoryDbSide()
    {
        var sharedUnit = SeedLeaseHistoryUnit();
        var otherUnit = SeedLeaseHistoryUnit("202");
        SeedLease(sharedUnit, "L-OLD", "Avery", "Ellis", LeaseStatus.Expired, new DateTime(2024, 1, 1), new DateTime(2024, 12, 31));
        SeedLease(sharedUnit, "L-NEW", "Blair", "Kline", LeaseStatus.Active, new DateTime(2025, 1, 1), new DateTime(2025, 12, 31));
        SeedLease(otherUnit, "L-OTHER", "Casey", "Moss", LeaseStatus.Active, new DateTime(2025, 1, 1), new DateTime(2025, 12, 31));

        _commands.Clear();
        var result = await _sut.ListPageAsync(PortfolioId, new LeaseListQuery
        {
            UnitId = sharedUnit.Id,
            Sort = "-startDate",
            Take = 20,
        });

        result.TotalCount.Should().Be(2);
        result.Items.Select(l => l.LeaseNumber).Should().Equal("L-NEW", "L-OLD");
        result.Items.Should().OnlyContain(l => l.UnitId == sharedUnit.Id);

        _commands.Should().Contain(sql =>
            sql.Contains("COUNT", StringComparison.OrdinalIgnoreCase) &&
            sql.Contains("\"UnitId\"", StringComparison.OrdinalIgnoreCase));
        _commands.Should().Contain(sql =>
            sql.Contains("ORDER BY", StringComparison.OrdinalIgnoreCase) &&
            sql.Contains("\"StartDate\"", StringComparison.OrdinalIgnoreCase) &&
            sql.Contains("LIMIT", StringComparison.OrdinalIgnoreCase));
    }

    private void SeedLease(string leaseNumber, string firstName, string lastName, LeaseStatus status)
    {
        var now = DateTime.UtcNow;
        var property = new Property
        {
            PortfolioId = PortfolioId,
            Name = $"{leaseNumber} Property",
            AddressLine1 = "100 Test Street",
            City = "Columbus",
            State = "OH",
            PostalCode = "43215",
            CreatedAt = now,
            UpdatedAt = now,
        };
        var unit = new Unit
        {
            Property = property,
            UnitNumber = leaseNumber[^1..],
            CreatedAt = now,
            UpdatedAt = now,
        };
        var tenant = new Tenant
        {
            PortfolioId = PortfolioId,
            FirstName = firstName,
            LastName = lastName,
            Email = $"{firstName.ToLowerInvariant()}@example.local",
            CreatedAt = now,
            UpdatedAt = now,
        };

        _ctx.Db.Leases.Add(new Lease
        {
            PortfolioId = PortfolioId,
            Property = property,
            Unit = unit,
            Tenant = tenant,
            LeaseNumber = leaseNumber,
            Status = status,
            StartDate = now.Date,
            EndDate = now.Date.AddYears(1),
            MonthlyRent = 1200m,
            SecurityDeposit = 1200m,
            CreatedAt = now,
            UpdatedAt = now,
        });
        _ctx.Db.SaveChanges();
    }

    private Unit SeedLeaseHistoryUnit(string unitNumber = "101")
    {
        var now = DateTime.UtcNow;
        var property = new Property
        {
            PortfolioId = PortfolioId,
            Name = $"History Property {unitNumber}",
            AddressLine1 = "100 History Street",
            City = "Columbus",
            State = "OH",
            PostalCode = "43215",
            CreatedAt = now,
            UpdatedAt = now,
        };
        var unit = new Unit
        {
            Property = property,
            UnitNumber = unitNumber,
            CreatedAt = now,
            UpdatedAt = now,
        };

        _ctx.Db.Units.Add(unit);
        _ctx.Db.SaveChanges();
        return unit;
    }

    private void SeedLease(Unit unit, string leaseNumber, string firstName, string lastName, LeaseStatus status, DateTime startDate, DateTime endDate)
    {
        var now = DateTime.UtcNow;
        var tenant = new Tenant
        {
            PortfolioId = PortfolioId,
            FirstName = firstName,
            LastName = lastName,
            Email = $"{firstName.ToLowerInvariant()}@example.local",
            CreatedAt = now,
            UpdatedAt = now,
        };

        _ctx.Db.Leases.Add(new Lease
        {
            PortfolioId = PortfolioId,
            PropertyId = unit.PropertyId,
            UnitId = unit.Id,
            Tenant = tenant,
            LeaseNumber = leaseNumber,
            Status = status,
            StartDate = startDate,
            EndDate = endDate,
            MonthlyRent = 1200m,
            SecurityDeposit = 1200m,
            CreatedAt = now,
            UpdatedAt = now,
        });
        _ctx.Db.SaveChanges();
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
