using FluentAssertions;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using System.Text.Json;
using RentalCommand.Api.Simulation;
using RentalCommand.Core.Configuration;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Interfaces;
using RentalCommand.Engine.Services;
using RentalCommand.TestCommon;

namespace RentalCommand.Engine.Tests.Automation;

public class LateFeeServiceTests : IDisposable
{
    private readonly SqliteTestContext _ctx;
    private readonly Mock<IMessagePublisher> _publisher;

    public LateFeeServiceTests()
    {
        _ctx       = new SqliteTestContext();
        _publisher = new Mock<IMessagePublisher>();
    }

    public void Dispose() => _ctx.Dispose();

    // -----------------------------------------------------------------------

    [Fact]
    public async Task AssessesCappedLateFee_AndMarksRentLate()
    {
        var (lease, rentPayment) = SeedOverdueScenario();

        var sut = BuildService(enable: true, graceDays: 5, caState6Percent: true);
        var count = await sut.AssessAsync();

        count.Should().Be(1);

        // Exactly one LateFee payment should exist.
        var lateFees = _ctx.Db.Payments
            .Where(p => p.PaymentType == PaymentType.LateFee)
            .ToList();
        lateFees.Should().HaveCount(1);

        // Amount should be min(100, 6% of 1000) = min(100, 60) = 60.
        lateFees[0].Amount.Should().Be(60m);

        // The original rent payment should be flipped to Late.
        _ctx.Db.Payments.Find(rentPayment.Id)!.Status.Should().Be(PaymentStatus.Late);
    }

    [Fact]
    public async Task IsIdempotent()
    {
        SeedOverdueScenario();

        var sut = BuildService(enable: true, graceDays: 5, caState6Percent: true);

        var first  = await sut.AssessAsync();
        var second = await sut.AssessAsync();

        first.Should().Be(1);
        second.Should().Be(0);

        _ctx.Db.Payments
            .Count(p => p.PaymentType == PaymentType.LateFee)
            .Should().Be(1);
    }

    [Fact]
    public async Task SkipsEndedFixedTermLeasePayments()
    {
        var (lease, rentPayment) = SeedOverdueScenario();
        var today = DateTime.UtcNow.Date;
        lease.EndDate = today.AddDays(-1);
        lease.UpdatedAt = DateTime.UtcNow;
        _ctx.Db.SaveChanges();

        var sut = BuildService(enable: true, graceDays: 5, caState6Percent: true);
        var count = await sut.AssessAsync();

        count.Should().Be(0);
        _ctx.Db.Payments.Count(p => p.PaymentType == PaymentType.LateFee).Should().Be(0);
        _ctx.Db.Payments.Find(rentPayment.Id)!.Status.Should().Be(PaymentStatus.Scheduled);
    }

    [Fact]
    public async Task NotifyTenants_CreatesStaffAndTenantInAppNotifications_AndTargetsPush()
    {
        var (lease, _) = SeedOverdueScenario();
        var tenant = _ctx.Db.Tenants.Find(lease.TenantId)!;
        tenant.Email = "bob@example.test";
        tenant.Phone = "6145550102";
        SeedNotificationUsers(tenant.Id);
        var publisher = new CapturingPublisher();
        var cfg = new NotificationsConfig
        {
            EnableLateFees = true,
            LateFeeGraceDays = 5,
            NotifyTenants = true,
            StateLateFeeCaps = new Dictionary<string, LateFeeCap>
            {
                ["CA"] = new() { MaxPercentOfRent = 6m },
            },
            ChannelPreferences =
            {
                [NotificationType.LateFee] = new NotificationChannelPreference
                {
                    EnableInApp = true,
                    EnableEmail = false,
                    EnableSms = false,
                    EnablePush = true,
                },
            },
        };
        var sut = BuildService(cfg, publisher);

        var count = await sut.AssessAsync();

        count.Should().Be(1);
        _ctx.Db.Notifications
            .Where(n => n.Type == "LateFee")
            .Select(n => n.UserId)
            .Should().BeEquivalentTo(new int?[] { 10, 20 });
        _ctx.Db.Notifications.Single(n => n.UserId == 20).ActionUrl.Should().Be("/portal/payments");
        publisher.TargetedPushUserIds().Should().ContainEquivalentOf(new[] { 10 });
        publisher.TargetedPushUserIds().Should().ContainEquivalentOf(new[] { 20 });
    }

    // -----------------------------------------------------------------------
    // Helpers

    private LateFeeService BuildService(bool enable, int graceDays, bool caState6Percent)
    {
        var caps = new Dictionary<string, LateFeeCap>();
        if (caState6Percent)
        {
            caps["CA"] = new LateFeeCap { MaxPercentOfRent = 6m };
        }

        var cfg = new NotificationsConfig
        {
            EnableLateFees    = enable,
            LateFeeGraceDays  = graceDays,
            NotifyTenants     = false,
            StateLateFeeCaps  = caps,
        };

        return BuildService(cfg, _publisher.Object);
    }

    private LateFeeService BuildService(NotificationsConfig cfg, IMessagePublisher publisher)
    {
        return new LateFeeService(
            _ctx.Db,
            publisher,
            new FakeNotificationSettingsService(cfg),
            Mock.Of<IDataUpdateService>(),
            Options.Create(cfg),
            TimeProvider.System,
            new AppTimeZoneProvider(new ConfigurationBuilder().Build()),
            NullLogger<LateFeeService>.Instance);
    }

    /// <summary>
    /// Seeds: Portfolio 1, Property (State=CA), Unit, Tenant, Lease (rent 1000, late fee 100),
    /// and an overdue Rent payment (due 10 days ago).
    /// Returns the seeded Lease and the rent Payment.
    /// </summary>
    private (Lease lease, Payment rentPayment) SeedOverdueScenario()
    {
        var now   = DateTime.UtcNow;
        var today = now.Date;

        var property = new Property
        {
            PortfolioId  = 1,
            Name         = "CA Property",
            AddressLine1 = "1 Oak Ave",
            City         = "Los Angeles",
            State        = "CA",
            PostalCode   = "90001",
            CreatedAt    = now,
            UpdatedAt    = now,
        };
        _ctx.Db.Properties.Add(property);

        var unit = new Unit
        {
            Property   = property,
            UnitNumber = "101",
            MarketRent = 1000m,
            CreatedAt  = now,
            UpdatedAt  = now,
        };
        _ctx.Db.Units.Add(unit);

        var tenant = new Tenant
        {
            PortfolioId = 1,
            FirstName   = "Bob",
            LastName    = "Smith",
            CreatedAt   = now,
            UpdatedAt   = now,
        };
        _ctx.Db.Tenants.Add(tenant);

        _ctx.Db.SaveChanges(); // flush so FK ids are assigned

        var overdueDueDate = today.AddDays(-10);
        var periodKey      = overdueDueDate.ToString("yyyy-MM");

        var lease = new Lease
        {
            PortfolioId     = 1,
            PropertyId      = property.Id,
            UnitId          = unit.Id,
            TenantId        = tenant.Id,
            LeaseNumber     = "L-LATE-001",
            Status          = LeaseStatus.Active,
            StartDate       = today.AddMonths(-12),
            EndDate         = today.AddMonths(6),
            MonthlyRent     = 1000m,
            SecurityDeposit = 0m,
            LateFeeAmount   = 100m,
            RentDueDay      = 1,
            CreatedAt       = now,
            UpdatedAt       = now,
        };
        _ctx.Db.Leases.Add(lease);
        _ctx.Db.SaveChanges();

        var rentPayment = new Payment
        {
            PortfolioId = 1,
            LeaseId     = lease.Id,
            PaymentType = PaymentType.Rent,
            Status      = PaymentStatus.Scheduled,
            Amount      = 1000m,
            DueDate     = overdueDueDate,
            PeriodKey   = periodKey,
            CreatedAt   = now,
            UpdatedAt   = now,
        };
        _ctx.Db.Payments.Add(rentPayment);
        _ctx.Db.SaveChanges();

        return (lease, rentPayment);
    }

    private void SeedNotificationUsers(int tenantId)
    {
        _ctx.Db.Roles.AddRange(
            new IdentityRole<int>(nameof(UserRole.Admin))
            {
                Id = 1,
                NormalizedName = nameof(UserRole.Admin).ToUpperInvariant(),
            },
            new IdentityRole<int>(nameof(UserRole.Tenant))
            {
                Id = 2,
                NormalizedName = nameof(UserRole.Tenant).ToUpperInvariant(),
            });

        _ctx.Db.Users.AddRange(
            new ApplicationUser
            {
                Id = 10,
                PortfolioId = 1,
                UserName = "admin@example.test",
                NormalizedUserName = "ADMIN@EXAMPLE.TEST",
                Email = "admin@example.test",
                NormalizedEmail = "ADMIN@EXAMPLE.TEST",
            },
            new ApplicationUser
            {
                Id = 20,
                PortfolioId = 1,
                TenantId = tenantId,
                UserName = "bob@example.test",
                NormalizedUserName = "BOB@EXAMPLE.TEST",
                Email = "bob@example.test",
                NormalizedEmail = "BOB@EXAMPLE.TEST",
            });

        _ctx.Db.UserRoles.AddRange(
            new IdentityUserRole<int> { UserId = 10, RoleId = 1 },
            new IdentityUserRole<int> { UserId = 20, RoleId = 2 });
        _ctx.Db.SaveChanges();
    }

    private sealed class CapturingPublisher : IMessagePublisher
    {
        private readonly List<string> _pushPayloads = [];

        public Task PublishAsync<TPayload>(
            int portfolioId,
            string messageType,
            TPayload payload,
            CancellationToken ct = default)
        {
            if (messageType == "push")
            {
                _pushPayloads.Add(JsonSerializer.Serialize(payload));
            }

            return Task.CompletedTask;
        }

        public IReadOnlyList<int[]> TargetedPushUserIds() =>
            _pushPayloads
                .Select(payload =>
                {
                    using var doc = JsonDocument.Parse(payload);
                    return doc.RootElement.GetProperty("userIds")
                        .EnumerateArray()
                        .Select(e => e.GetInt32())
                        .ToArray();
                })
                .ToList();
    }
}
