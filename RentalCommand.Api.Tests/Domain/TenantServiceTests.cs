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

public class TenantServiceTests : IDisposable
{
    private const int PortfolioId = 1;

    private readonly List<string> _commands = [];
    private readonly SqliteTestContext _ctx;
    private readonly TenantService _sut;

    public TenantServiceTests()
    {
        _ctx = new SqliteTestContext([new RecordingCommandInterceptor(_commands)]);
        _sut = new TenantService(_ctx.Db, Mock.Of<IDataUpdateService>());
    }

    public void Dispose() => _ctx.Dispose();

    [Fact]
    public async Task ListPageAsync_ReturnsSqlCountAndRequestedWindow()
    {
        SeedTenant("Avery", "Ellis", activeLeaseCount: 1);
        SeedTenant("Blair", "Kline", activeLeaseCount: 3);
        SeedTenant("Casey", "Moss", activeLeaseCount: 0);
        SeedTenant("Devon", "Nash", activeLeaseCount: 2);

        _commands.Clear();
        var result = await _sut.ListPageAsync(PortfolioId, new TenantListQuery
        {
            Sort = "-activeLeaseCount",
            Skip = 1,
            Take = 2,
        });

        result.TotalCount.Should().Be(4);
        result.Skip.Should().Be(1);
        result.Take.Should().Be(2);
        result.Items.Select(t => t.FirstName).Should().Equal("Devon", "Avery");
        result.Items.Select(t => t.ActiveLeaseCount).Should().Equal(2, 1);

        _commands.Should().Contain(sql =>
            sql.Contains("COUNT", StringComparison.OrdinalIgnoreCase) &&
            sql.Contains("FROM \"Tenants\"", StringComparison.OrdinalIgnoreCase));
        _commands.Should().Contain(sql =>
            sql.Contains("ORDER BY", StringComparison.OrdinalIgnoreCase) &&
            sql.Contains("COUNT", StringComparison.OrdinalIgnoreCase) &&
            sql.Contains("LIMIT", StringComparison.OrdinalIgnoreCase) &&
            sql.Contains("OFFSET", StringComparison.OrdinalIgnoreCase));
    }

    private void SeedTenant(string firstName, string lastName, int activeLeaseCount)
    {
        var now = DateTime.UtcNow;
        var tenant = new Tenant
        {
            PortfolioId = PortfolioId,
            FirstName = firstName,
            LastName = lastName,
            Email = $"{firstName.ToLowerInvariant()}@example.local",
            Phone = "555-0100",
            CreatedAt = now,
            UpdatedAt = now,
        };
        _ctx.Db.Tenants.Add(tenant);
        _ctx.Db.SaveChanges();

        for (var i = 0; i < activeLeaseCount; i++)
        {
            var property = new Property
            {
                PortfolioId = PortfolioId,
                Name = $"{firstName} Property {i}",
                AddressLine1 = $"{i} Main Street",
                City = "Columbus",
                State = "OH",
                PostalCode = "43215",
                CreatedAt = now,
                UpdatedAt = now,
            };
            var unit = new Unit
            {
                Property = property,
                UnitNumber = $"{i + 1}A",
                CreatedAt = now,
                UpdatedAt = now,
            };
            _ctx.Db.Leases.Add(new Lease
            {
                PortfolioId = PortfolioId,
                Property = property,
                Unit = unit,
                TenantId = tenant.Id,
                LeaseNumber = $"{firstName}-{i}",
                Status = LeaseStatus.Active,
                StartDate = now.Date,
                EndDate = now.Date.AddYears(1),
                MonthlyRent = 1200m,
                SecurityDeposit = 1200m,
                CreatedAt = now,
                UpdatedAt = now,
            });
        }

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
