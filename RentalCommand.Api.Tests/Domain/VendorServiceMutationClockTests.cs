using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using RentalCommand.Api.DTOs;
using RentalCommand.Api.Services.Domain;
using RentalCommand.Api.Writes;
using RentalCommand.Core.Atomic;
using RentalCommand.Core.Authorization;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Interfaces;
using RentalCommand.TestCommon;

namespace RentalCommand.Api.Tests.Domain;

public sealed class VendorServiceMutationClockTests : IDisposable
{
    private const int PortfolioId = 1;

    private readonly SqliteTestContext _ctx = new();
    private readonly MutableTimeProvider _timeProvider = new(
        new DateTimeOffset(2027, 1, 4, 5, 0, 0, TimeSpan.Zero));
    private readonly ServiceProvider _services;
    private readonly VendorService _sut;
    private readonly WorkspaceReadScope _scope;

    public VendorServiceMutationClockTests()
    {
        _scope = _ctx.Db.SeedAdministratorScope(PortfolioId, nameof(VendorServiceMutationClockTests));
        var now = DateTime.UtcNow;
        _ctx.Db.Properties.Add(new Property
        {
            PortfolioId = PortfolioId,
            Name = "Vendor authorization property",
            AddressLine1 = "1 Scope Test Way",
            City = "Columbus",
            State = "OH",
            PostalCode = "43215",
            CreatedAt = now,
            UpdatedAt = now,
        });
        _ctx.Db.SaveChanges();
        _services = AtomicDomainTestKernel.CreateForCoreCrud(_ctx.ConnectionString, _timeProvider);
        _sut = new VendorService(
            _services.GetRequiredService<RentalCommand.Data.RentalCommandDbContext>(),
            Mock.Of<IDataUpdateService>(),
            _services.GetRequiredService<IAtomicUnitOfWork>(),
            _timeProvider,
            _services.GetRequiredService<IRequestWriteExecutor>());
    }

    public void Dispose()
    {
        _services.Dispose();
        _ctx.Dispose();
    }

    [Fact]
    public async Task CreateAndUpdateAsync_UseInjectedAppClockForVendorBusinessTimestamps()
    {
        var createdAtUtc = _timeProvider.GetUtcNow().UtcDateTime;

        var created = await _sut.CreateAsync(
            _scope,
            new CreateVendorRequest
            {
                Name = "Frozen Clock Plumbing",
                ServiceType = "Plumbing",
            },
            "vendor-clock-create");

        created.Should().NotBeNull();
        created!.CreatedAt.Should().Be(createdAtUtc);
        created.UpdatedAt.Should().Be(createdAtUtc);

        var updatedAtUtc = new DateTimeOffset(2027, 1, 5, 15, 30, 0, TimeSpan.Zero);
        _timeProvider.SetUtcNow(updatedAtUtc);

        var updated = await _sut.UpdateAsync(
            _scope,
            created.Id,
            new UpdateVendorRequest { Name = "Frozen Clock HVAC" },
            "vendor-clock-update");

        updated.Should().NotBeNull();
        updated!.CreatedAt.Should().Be(createdAtUtc);
        updated.UpdatedAt.Should().Be(updatedAtUtc.UtcDateTime);

        var persisted = await _ctx.Db.Vendors.SingleAsync(vendor => vendor.Id == created.Id);
        persisted.CreatedAt.Should().Be(createdAtUtc);
        persisted.UpdatedAt.Should().Be(updatedAtUtc.UtcDateTime);
    }

    private sealed class MutableTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        private DateTimeOffset _utcNow = utcNow;

        public override DateTimeOffset GetUtcNow() => _utcNow;

        public void SetUtcNow(DateTimeOffset utcNow) => _utcNow = utcNow;
    }
}
