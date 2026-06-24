using System.Data.Common;
using FluentAssertions;
using Microsoft.EntityFrameworkCore.Diagnostics;
using RentalCommand.Api.Services.Domain;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.TestCommon;

namespace RentalCommand.Api.Tests.Domain;

public class PortalServiceLeaseTests : IDisposable
{
    private const int PortfolioId = 1;

    private readonly List<string> _commands = [];
    private readonly SqliteTestContext _ctx;
    private readonly PortalService _sut;

    public PortalServiceLeaseTests()
    {
        _ctx = new SqliteTestContext([new RecordingCommandInterceptor(_commands)]);
        _sut = new PortalService(_ctx.Db, new NoopLeaseQaService());
    }

    public void Dispose() => _ctx.Dispose();

    [Fact]
    public async Task GetLeasesAsync_ReturnsTenantLeaseLabelsFromSingleSqlProjection()
    {
        var tenant = SeedLease("QA-2026-002-2B", "Riverside Flats", "2B", "Blake", "Hayes");
        SeedLease("OTHER-1A", "Other Property", "1A", "Other", "Tenant");

        _commands.Clear();

        var result = await _sut.GetLeasesAsync(PortfolioId, tenant.Id);

        result.Should().ContainSingle();
        var lease = result.Single();
        lease.LeaseNumber.Should().Be("QA-2026-002-2B");
        lease.PropertyName.Should().Be("Riverside Flats");
        lease.UnitNumber.Should().Be("2B");
        lease.TenantName.Should().Be("Blake Hayes");

        var leaseQueries = _commands
            .Where(sql => sql.Contains("FROM \"Leases\"", StringComparison.OrdinalIgnoreCase))
            .ToList();
        leaseQueries.Should().ContainSingle("portal leases should be projected in one DB query");
        leaseQueries[0].Should().Contain("\"Properties\"");
        leaseQueries[0].Should().Contain("\"Units\"");
        leaseQueries[0].Should().Contain("\"Tenants\"");
    }

    private Tenant SeedLease(
        string leaseNumber,
        string propertyName,
        string unitNumber,
        string firstName,
        string lastName)
    {
        var now = DateTime.UtcNow;
        var property = new Property
        {
            PortfolioId = PortfolioId,
            Name = propertyName,
            AddressLine1 = "1188 Maple Ave",
            City = "Columbus",
            State = "OH",
            PostalCode = "43201",
            CreatedAt = now,
            UpdatedAt = now,
        };
        var unit = new Unit
        {
            Property = property,
            UnitNumber = unitNumber,
            Bedrooms = 2,
            Bathrooms = 1,
            MarketRent = 1200m,
            CreatedAt = now,
            UpdatedAt = now,
        };
        var tenant = new Tenant
        {
            PortfolioId = PortfolioId,
            FirstName = firstName,
            LastName = lastName,
            Email = $"{firstName.ToLowerInvariant()}.{lastName.ToLowerInvariant()}@example.local",
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
            Status = LeaseStatus.Active,
            StartDate = now.Date,
            EndDate = now.Date.AddYears(1),
            MonthlyRent = 1200m,
            SecurityDeposit = 1200m,
            LateFeeAmount = 75m,
            RentDueDay = 1,
            CreatedAt = now,
            UpdatedAt = now,
        });
        _ctx.Db.SaveChanges();

        return tenant;
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
