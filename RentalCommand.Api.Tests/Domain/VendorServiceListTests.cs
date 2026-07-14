using System.Data.Common;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Moq;
using RentalCommand.Api.DTOs;
using RentalCommand.Api.Services.Domain;
using RentalCommand.Core.Atomic;
using RentalCommand.Core.Authorization;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Interfaces;
using RentalCommand.TestCommon;

namespace RentalCommand.Api.Tests.Domain;

public class VendorServiceListTests : IDisposable
{
    private const int PortfolioId = 1;

    private readonly List<string> _commands = [];
    private readonly SqliteTestContext _ctx;
    private readonly VendorService _sut;
    private readonly WorkspaceReadScope _scope;

    public VendorServiceListTests()
    {
        _ctx = new SqliteTestContext([new RecordingCommandInterceptor(_commands)]);
        _scope = _ctx.Db.SeedAdministratorScope(PortfolioId, nameof(VendorServiceListTests));
        _sut = new VendorService(
            _ctx.Db,
            Mock.Of<IDataUpdateService>(),
            Mock.Of<IAtomicUnitOfWork>(),
            TimeProvider.System);
    }

    public void Dispose() => _ctx.Dispose();

    [Fact]
    public async Task ListPageAsync_ReturnsSqlCountAndRequestedWindow()
    {
        SeedVendor("Alpha Plumbing", "Plumbing");
        SeedVendor("Bravo Plumbing", "Plumbing");
        SeedVendor("Cedar Plumbing", "Plumbing");
        SeedVendor("Delta Cleaning", "Cleaning");

        _commands.Clear();
        var result = await _sut.ListPageAsync(_scope, new ListQuery
        {
            Sort = "name",
            Skip = 1,
            Take = 2,
        });

        result.TotalCount.Should().Be(4);
        result.Skip.Should().Be(1);
        result.Take.Should().Be(2);
        result.Items.Select(v => v.Name).Should().Equal("Bravo Plumbing", "Cedar Plumbing");

        _commands.Should().Contain(sql =>
            sql.Contains("COUNT", StringComparison.OrdinalIgnoreCase) &&
            sql.Contains("FROM \"Vendors\"", StringComparison.OrdinalIgnoreCase));
        _commands.Should().Contain(sql =>
            sql.Contains("ORDER BY", StringComparison.OrdinalIgnoreCase) &&
            sql.Contains("LIMIT", StringComparison.OrdinalIgnoreCase) &&
            sql.Contains("OFFSET", StringComparison.OrdinalIgnoreCase));
    }

    private void SeedVendor(
        string name,
        string serviceType,
        string? addressLine1 = null,
        string? city = null,
        string? state = null,
        string? postalCode = null)
    {
        var now = DateTime.UtcNow;
        _ctx.Db.Vendors.Add(new Vendor
        {
            PortfolioId = PortfolioId,
            Name = name,
            ServiceType = serviceType,
            Email = $"{name.Replace(" ", ".", StringComparison.Ordinal).ToLowerInvariant()}@example.local",
            AddressLine1 = addressLine1,
            City = city,
            State = state,
            PostalCode = postalCode,
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
