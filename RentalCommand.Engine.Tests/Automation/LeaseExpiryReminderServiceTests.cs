using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using RentalCommand.Core.Configuration;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Interfaces;
using RentalCommand.Engine.Services;
using RentalCommand.TestCommon;

namespace RentalCommand.Engine.Tests.Automation;

public class LeaseExpiryReminderServiceTests : IDisposable
{
    private readonly SqliteTestContext _ctx;
    private readonly Mock<IMessagePublisher> _publisher;

    public LeaseExpiryReminderServiceTests()
    {
        _ctx       = new SqliteTestContext();
        _publisher = new Mock<IMessagePublisher>();
    }

    public void Dispose() => _ctx.Dispose();

    // -----------------------------------------------------------------------

    [Fact]
    public async Task SendsOnce_AndSetsMarker()
    {
        var lease = SeedExpiringLease(daysUntilExpiry: 30, ownerEmail: "owner@x.com");

        var sut = BuildService(enable: true, reminderDays: 60);

        // First call: should send one reminder and set the marker.
        var firstCount = await sut.RemindAsync();
        firstCount.Should().Be(1);

        _publisher.Verify(
            p => p.PublishAsync(
                It.IsAny<int>(),
                "email",
                It.IsAny<object>(),
                It.IsAny<CancellationToken>()),
            Times.Once);

        var reloaded = _ctx.Db.Leases.Find(lease.Id)!;
        reloaded.ExpiryReminderSentAt.Should().NotBeNull();

        // Second call: lease now has ExpiryReminderSentAt set → skipped.
        var secondCount = await sut.RemindAsync();
        secondCount.Should().Be(0);

        // Publisher must still have been called exactly once in total.
        _publisher.Verify(
            p => p.PublishAsync(
                It.IsAny<int>(),
                "email",
                It.IsAny<object>(),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    // -----------------------------------------------------------------------
    // Helpers

    private LeaseExpiryReminderService BuildService(bool enable, int reminderDays)
    {
        var cfg = new NotificationsConfig
        {
            EnableLeaseExpiryReminders = enable,
            LeaseExpiryReminderDays    = reminderDays,
            NotifyTenants              = false,
        };

        return new LeaseExpiryReminderService(
            _ctx.Db,
            _publisher.Object,
            Options.Create(cfg),
            NullLogger<LeaseExpiryReminderService>.Instance);
    }

    private Lease SeedExpiringLease(int daysUntilExpiry, string ownerEmail)
    {
        var now   = DateTime.UtcNow;
        var today = now.Date;

        var owner = new Owner
        {
            PortfolioId = 1,
            Name        = "Test Owner",
            Email       = ownerEmail,
            CreatedAt   = now,
            UpdatedAt   = now,
        };
        _ctx.Db.Owners.Add(owner);
        _ctx.Db.SaveChanges();

        var property = new Property
        {
            PortfolioId  = 1,
            OwnerId      = owner.Id,
            Name         = "Expiry Property",
            AddressLine1 = "99 Elm St",
            City         = "Portland",
            State        = "OR",
            PostalCode   = "97201",
            CreatedAt    = now,
            UpdatedAt    = now,
        };
        _ctx.Db.Properties.Add(property);

        var unit = new Unit
        {
            Property   = property,
            UnitNumber = "A",
            MarketRent = 1200m,
            CreatedAt  = now,
            UpdatedAt  = now,
        };
        _ctx.Db.Units.Add(unit);

        var tenant = new Tenant
        {
            PortfolioId = 1,
            FirstName   = "Alice",
            LastName    = "Walker",
            CreatedAt   = now,
            UpdatedAt   = now,
        };
        _ctx.Db.Tenants.Add(tenant);

        _ctx.Db.SaveChanges();

        var lease = new Lease
        {
            PortfolioId           = 1,
            PropertyId            = property.Id,
            UnitId                = unit.Id,
            TenantId              = tenant.Id,
            LeaseNumber           = "L-EXP-001",
            Status                = LeaseStatus.Active,
            StartDate             = today.AddMonths(-12),
            EndDate               = today.AddDays(daysUntilExpiry),
            MonthlyRent           = 1200m,
            SecurityDeposit       = 0m,
            LateFeeAmount         = 0m,
            RentDueDay            = 1,
            ExpiryReminderSentAt  = null,
            CreatedAt             = now,
            UpdatedAt             = now,
        };
        _ctx.Db.Leases.Add(lease);
        _ctx.Db.SaveChanges();

        return lease;
    }
}
