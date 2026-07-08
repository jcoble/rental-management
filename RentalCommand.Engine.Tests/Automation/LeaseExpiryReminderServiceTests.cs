using System.Data.Common;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
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
    private readonly List<string> _commands = [];
    private readonly SqliteTestContext _ctx;
    private readonly Mock<IMessagePublisher> _publisher;

    public LeaseExpiryReminderServiceTests()
    {
        _ctx       = new SqliteTestContext([new RecordingCommandInterceptor(_commands)]);
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

        await _ctx.Db.Entry(lease).ReloadAsync();
        lease.ExpiryReminderSentAt.Should().NotBeNull();

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

    [Fact]
    public async Task RemindAsync_FiltersDisabledPortfolioInSql()
    {
        SeedExpiringLease(daysUntilExpiry: 30, ownerEmail: "owner@x.com");

        var sut = BuildService(enable: false, reminderDays: 60);

        _commands.Clear();
        var count = await sut.RemindAsync();

        count.Should().Be(0);
        _publisher.Verify(
            p => p.PublishAsync(
                It.IsAny<int>(),
                It.IsAny<string>(),
                It.IsAny<object>(),
                It.IsAny<CancellationToken>()),
            Times.Never);
        _commands.Should().Contain(sql =>
            sql.Contains("NotificationSettings", StringComparison.OrdinalIgnoreCase) &&
            sql.Contains("Leases", StringComparison.OrdinalIgnoreCase) &&
            sql.Contains("EndDate", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task RemindAsync_FiltersReminderWindowInSql()
    {
        var near = SeedExpiringLease(daysUntilExpiry: 30, ownerEmail: "owner@x.com", leaseNumber: "L-NEAR");
        var far = SeedExpiringLease(daysUntilExpiry: 90, ownerEmail: "owner@x.com", leaseNumber: "L-FAR");

        var sut = BuildService(enable: true, reminderDays: 60);

        _commands.Clear();
        var count = await sut.RemindAsync();

        count.Should().Be(1);
        await _ctx.Db.Entry(near).ReloadAsync();
        await _ctx.Db.Entry(far).ReloadAsync();
        near.ExpiryReminderSentAt.Should().NotBeNull();
        far.ExpiryReminderSentAt.Should().BeNull();
        _commands.Should().Contain(sql =>
            sql.Contains("NotificationSettings", StringComparison.OrdinalIgnoreCase) &&
            sql.Contains("Leases", StringComparison.OrdinalIgnoreCase) &&
            sql.Contains("EndDate", StringComparison.OrdinalIgnoreCase));
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

        SeedNotificationSettings(enable, reminderDays);

        return new LeaseExpiryReminderService(
            _ctx.Db,
            _publisher.Object,
            new FakeNotificationSettingsService(cfg),
            Mock.Of<IDataUpdateService>(),
            TimeProvider.System,
            NullLogger<LeaseExpiryReminderService>.Instance);
    }

    private void SeedNotificationSettings(bool enable, int reminderDays)
    {
        var now = DateTime.UtcNow;
        var row = _ctx.Db.NotificationSettings.SingleOrDefault(s => s.PortfolioId == 1);
        if (row is null)
        {
            _ctx.Db.NotificationSettings.Add(new NotificationSettings
            {
                PortfolioId = 1,
                EnableLeaseExpiryReminders = enable,
                LeaseExpiryReminderDays = reminderDays,
                CreatedAt = now,
                UpdatedAt = now,
            });
        }
        else
        {
            row.EnableLeaseExpiryReminders = enable;
            row.LeaseExpiryReminderDays = reminderDays;
            row.UpdatedAt = now;
        }

        _ctx.Db.SaveChanges();
    }

    private Lease SeedExpiringLease(int daysUntilExpiry, string ownerEmail, string leaseNumber = "L-EXP-001")
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
            LeaseNumber           = leaseNumber,
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
