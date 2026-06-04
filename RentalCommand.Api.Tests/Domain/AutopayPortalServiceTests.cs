using FluentAssertions;
using RentalCommand.Api.Services.Domain;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.TestCommon;

namespace RentalCommand.Api.Tests.Domain;

/// <summary>
/// Tenant-facing autopay status/cancel on <see cref="PortalService"/>: status reflects the active
/// enrollment, cancel deactivates it, and both are ownership-checked (another tenant's lease → null,
/// which the controller maps to 404). No Stripe involved — purely local enrollment state.
/// </summary>
public class AutopayPortalServiceTests : IDisposable
{
    private const int PortfolioId = 1;

    private readonly SqliteTestContext _ctx = new();
    private readonly PortalService _sut;

    public AutopayPortalServiceTests()
    {
        _sut = new PortalService(_ctx.Db);
    }

    public void Dispose() => _ctx.Dispose();

    [Fact]
    public async Task Status_WhenEnrolled_ReportsActive()
    {
        var lease = SeedLease(tenantId: 10);
        SeedEnrollment(lease, tenantId: 10, active: true);

        var status = await _sut.GetAutopayStatusAsync(PortfolioId, tenantId: 10, lease.Id, CancellationToken.None);

        status.Should().NotBeNull();
        status!.LeaseId.Should().Be(lease.Id);
        status.Active.Should().BeTrue();
        status.EnrolledAt.Should().NotBeNull();
    }

    [Fact]
    public async Task Status_WhenNotEnrolled_ReportsInactive()
    {
        var lease = SeedLease(tenantId: 10);

        var status = await _sut.GetAutopayStatusAsync(PortfolioId, tenantId: 10, lease.Id, CancellationToken.None);

        status.Should().NotBeNull();
        status!.Active.Should().BeFalse();
    }

    [Fact]
    public async Task Status_ForAnotherTenantsLease_ReturnsNull()
    {
        var lease = SeedLease(tenantId: 10);
        SeedEnrollment(lease, tenantId: 10, active: true);

        // Tenant 20 may not read tenant 10's autopay status.
        var status = await _sut.GetAutopayStatusAsync(PortfolioId, tenantId: 20, lease.Id, CancellationToken.None);

        status.Should().BeNull();
    }

    [Fact]
    public async Task Cancel_DeactivatesEnrollment()
    {
        var lease = SeedLease(tenantId: 10);
        var enrollment = SeedEnrollment(lease, tenantId: 10, active: true);

        var result = await _sut.CancelAutopayAsync(PortfolioId, tenantId: 10, lease.Id, CancellationToken.None);

        result.Should().NotBeNull();
        result!.Active.Should().BeFalse();
        _ctx.Db.Entry(enrollment).Reload();
        enrollment.Active.Should().BeFalse();
    }

    [Fact]
    public async Task Cancel_ForAnotherTenantsLease_ReturnsNull_AndLeavesEnrollmentActive()
    {
        var lease = SeedLease(tenantId: 10);
        var enrollment = SeedEnrollment(lease, tenantId: 10, active: true);

        var result = await _sut.CancelAutopayAsync(PortfolioId, tenantId: 20, lease.Id, CancellationToken.None);

        result.Should().BeNull();
        _ctx.Db.Entry(enrollment).Reload();
        enrollment.Active.Should().BeTrue();
    }

    // -----------------------------------------------------------------------

    private Lease SeedLease(int tenantId)
    {
        var now = DateTime.UtcNow;

        var property = new Property
        {
            PortfolioId = PortfolioId,
            Name = "P",
            AddressLine1 = "1 St",
            City = "Town",
            State = "ST",
            PostalCode = "00000",
            CreatedAt = now,
            UpdatedAt = now,
        };
        _ctx.Db.Properties.Add(property);

        var unit = new Unit { Property = property, UnitNumber = $"U{tenantId}", CreatedAt = now, UpdatedAt = now };
        _ctx.Db.Units.Add(unit);

        var tenant = new Tenant
        {
            Id = tenantId,
            PortfolioId = PortfolioId,
            FirstName = "T",
            LastName = tenantId.ToString(),
            CreatedAt = now,
            UpdatedAt = now,
        };
        _ctx.Db.Tenants.Add(tenant);
        _ctx.Db.SaveChanges();

        var lease = new Lease
        {
            PortfolioId = PortfolioId,
            PropertyId = property.Id,
            UnitId = unit.Id,
            TenantId = tenantId,
            LeaseNumber = $"L-{tenantId}",
            Status = LeaseStatus.Active,
            StartDate = now.AddMonths(-6),
            EndDate = now.AddMonths(6),
            MonthlyRent = 1000m,
            CreatedAt = now,
            UpdatedAt = now,
        };
        _ctx.Db.Leases.Add(lease);
        _ctx.Db.SaveChanges();
        return lease;
    }

    private AutopayEnrollment SeedEnrollment(Lease lease, int tenantId, bool active)
    {
        var enrollment = new AutopayEnrollment
        {
            PortfolioId = PortfolioId,
            LeaseId = lease.Id,
            TenantId = tenantId,
            StripeCustomerId = "cus_test",
            StripePaymentMethodId = "pm_test",
            Active = active,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        _ctx.Db.AutopayEnrollments.Add(enrollment);
        _ctx.Db.SaveChanges();
        return enrollment;
    }
}
