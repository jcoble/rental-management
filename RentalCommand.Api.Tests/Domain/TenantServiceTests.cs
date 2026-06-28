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

    [Fact]
    public async Task ListPageAsync_TokenizesHyphenatedSearchTermsInSql()
    {
        SeedTenant("Avery", "Ellis", activeLeaseCount: 0);
        SeedTenant("Avery", "Stone", activeLeaseCount: 0);
        SeedTenant("Blair", "Ellis", activeLeaseCount: 0);

        _commands.Clear();
        var result = await _sut.ListPageAsync(PortfolioId, new TenantListQuery
        {
            Search = "Avery-Ellis",
            Sort = "name",
            Skip = 0,
            Take = 20,
        });

        result.TotalCount.Should().Be(1);
        var tenant = result.Items.Should().ContainSingle().Subject;
        tenant.FirstName.Should().Be("Avery");
        tenant.LastName.Should().Be("Ellis");

        _commands.Should().HaveCount(2);
        _commands.Should().OnlyContain(sql =>
            sql.Contains("LIKE", StringComparison.OrdinalIgnoreCase) ||
            sql.Contains("ILIKE", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ListPageAsync_AvailableForLeaseExcludesTenantsAlreadyOccupyingLeasesInSql()
    {
        SeedTenant("Avery", "Available", activeLeaseCount: 0);
        SeedTenant("Blair", "Primary", activeLeaseCount: 1);
        var noticeTenant = SeedTenant("Casey", "Notice", activeLeaseCount: 0);
        var expiredTenant = SeedTenant("Devon", "Expired", activeLeaseCount: 0);
        var pendingTenant = SeedTenant("Emery", "Pending", activeLeaseCount: 0);
        SeedLeaseTenantMembership(noticeTenant, LeaseStatus.NoticeGiven, withSeparatePrimaryTenant: true);
        SeedLeaseTenantMembership(expiredTenant, LeaseStatus.Expired);
        SeedLeaseTenantMembership(pendingTenant, LeaseStatus.PendingSignature);

        _commands.Clear();
        var result = await _sut.ListPageAsync(PortfolioId, new TenantListQuery
        {
            AvailableForLease = true,
            Sort = "name",
            Skip = 0,
            Take = 20,
        });

        result.TotalCount.Should().Be(3);
        result.Items.Select(t => $"{t.FirstName} {t.LastName}")
            .Should().Equal("Avery Available", "Devon Expired", "Emery Pending");

        _commands.Should().HaveCount(2);
        _commands.Should().OnlyContain(sql =>
            sql.Contains("NOT EXISTS", StringComparison.OrdinalIgnoreCase) &&
            sql.Contains("Leases", StringComparison.OrdinalIgnoreCase));
        _commands.Should().Contain(sql =>
            sql.Contains("LeaseTenants", StringComparison.OrdinalIgnoreCase));
    }

    private Tenant SeedTenant(string firstName, string lastName, int activeLeaseCount)
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
        return tenant;
    }

    private void SeedLeaseTenantMembership(
        Tenant tenant,
        LeaseStatus status,
        bool withSeparatePrimaryTenant = false)
    {
        var now = DateTime.UtcNow;
        var primaryTenant = tenant;
        if (withSeparatePrimaryTenant)
        {
            primaryTenant = new Tenant
            {
                PortfolioId = PortfolioId,
                FirstName = $"{tenant.FirstName} Primary",
                LastName = "Holder",
                Email = $"{tenant.FirstName.ToLowerInvariant()}-primary@example.local",
                Phone = "555-0101",
                CreatedAt = now,
                UpdatedAt = now,
            };
            _ctx.Db.Tenants.Add(primaryTenant);
            _ctx.Db.SaveChanges();
        }

        var property = new Property
        {
            PortfolioId = PortfolioId,
            Name = $"{tenant.FirstName} Membership Property",
            AddressLine1 = "100 Main Street",
            City = "Columbus",
            State = "OH",
            PostalCode = "43215",
            CreatedAt = now,
            UpdatedAt = now,
        };
        var unit = new Unit
        {
            Property = property,
            UnitNumber = $"{tenant.FirstName[0]}1",
            CreatedAt = now,
            UpdatedAt = now,
        };
        var lease = new Lease
        {
            PortfolioId = PortfolioId,
            Property = property,
            Unit = unit,
            TenantId = primaryTenant.Id,
            LeaseNumber = $"{tenant.FirstName}-membership",
            Status = status,
            StartDate = now.Date.AddMonths(-1),
            EndDate = now.Date.AddMonths(11),
            MonthlyRent = 1200m,
            SecurityDeposit = 1200m,
            CreatedAt = now,
            UpdatedAt = now,
            LeaseTenants =
            [
                new LeaseTenant
                {
                    PortfolioId = PortfolioId,
                    TenantId = tenant.Id,
                    IsPrimary = !withSeparatePrimaryTenant,
                    CreatedAt = now,
                    UpdatedAt = now,
                },
            ],
        };

        _ctx.Db.Leases.Add(lease);
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
