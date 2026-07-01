using System.Data.Common;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using RentalCommand.Api.DTOs;
using RentalCommand.Api.Services.Domain;
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

    public VendorServiceListTests()
    {
        _ctx = new SqliteTestContext([new RecordingCommandInterceptor(_commands)]);
        _sut = new VendorService(
            _ctx.Db,
            Mock.Of<IDataUpdateService>(),
            Mock.Of<IMessagePublisher>(),
            Mock.Of<IAuditTrailService>(),
            NullLogger<VendorService>.Instance);
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
        var result = await _sut.ListPageAsync(PortfolioId, new ListQuery
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

    [Fact]
    public async Task CreateAsync_ReturnsStructuredAddressFields()
    {
        var created = await _sut.CreateAsync(PortfolioId, new CreateVendorRequest
        {
            Name = "Acme HVAC",
            ServiceType = "HVAC",
            Website = "https://acme.example.test",
            AddressLine1 = "123 Service Rd",
            City = "Columbus",
            State = "OH",
            PostalCode = "43215",
        });

        created.Website.Should().Be("https://acme.example.test");
        created.AddressLine1.Should().Be("123 Service Rd");
        created.City.Should().Be("Columbus");
        created.State.Should().Be("OH");
        created.PostalCode.Should().Be("43215");

        var saved = await _ctx.Db.Vendors.AsNoTracking().SingleAsync(v => v.Id == created.Id);
        saved.Website.Should().Be("https://acme.example.test");
        saved.AddressLine1.Should().Be("123 Service Rd");
        saved.City.Should().Be("Columbus");
        saved.State.Should().Be("OH");
        saved.PostalCode.Should().Be("43215");
    }

    [Fact]
    public async Task UpdateAsync_UpdatesStructuredAddressFields()
    {
        SeedVendor("Acme HVAC", "HVAC");
        var vendorId = await _ctx.Db.Vendors.Select(v => v.Id).SingleAsync();

        var updated = await _sut.UpdateAsync(PortfolioId, vendorId, new UpdateVendorRequest
        {
            Website = "https://repair.example.test",
            AddressLine1 = "456 Repair Ave",
            City = "Cincinnati",
            State = "OH",
            PostalCode = "45202",
        });

        updated.Should().NotBeNull();
        updated!.Website.Should().Be("https://repair.example.test");
        updated!.AddressLine1.Should().Be("456 Repair Ave");
        updated.City.Should().Be("Cincinnati");
        updated.State.Should().Be("OH");
        updated.PostalCode.Should().Be("45202");
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
