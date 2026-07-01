using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;
using RentalCommand.Api.DTOs;
using RentalCommand.Api.Services.Domain;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Interfaces;
using RentalCommand.TestCommon;

namespace RentalCommand.Api.Tests.Domain;

/// <summary>
/// Covers the inbound SMS router: a vendor's DONE (sender is a vendor with an open dispatch) closes the
/// work order via the vendor path, while a tenant's YES marks rent paid via the rent path — verifying
/// the two handlers don't cross-fire.
/// </summary>
public class SmsInboundRouterTests : IDisposable
{
    private const int PortfolioId = 1;

    private readonly SqliteTestContext _ctx = new();

    public void Dispose() => _ctx.Dispose();

    private SmsInboundRouter CreateRouter()
    {
        var vendorDone = new SmsInboundVendorDoneService(
            _ctx.Db,
            Mock.Of<IDataUpdateService>(),
            Mock.Of<IAuditTrailService>(),
            Mock.Of<ILogger<SmsInboundVendorDoneService>>(),
            TimeProvider.System);

        var rent = new SmsInboundRentConfirmationService(
            _ctx.Db,
            Mock.Of<IDataUpdateService>(),
            Mock.Of<IAuditTrailService>(),
            Mock.Of<IMessagePublisher>(),
            Mock.Of<ILlmProvider>(),
            Mock.Of<ILogger<SmsInboundRentConfirmationService>>(),
            TimeProvider.System);

        return new SmsInboundRouter(vendorDone, rent);
    }

    [Fact]
    public async Task Route_VendorDone_ClosesWorkOrder_NotRent()
    {
        var property = SeedProperty();
        var vendor = SeedVendor("+16145550199");
        var workOrder = SeedWorkOrder(property);

        // An unpaid rent item for a tenant who shares the SAME phone as the vendor — the router must
        // still prefer the vendor-DONE path because there is an open dispatch + DONE keyword.
        var tenant = SeedTenant("+16145550199");
        var lease = SeedLease(property, tenant);
        var rent = SeedRent(lease);
        await _ctx.Db.SaveChangesAsync();

        // Dispatch the work order to the vendor (creates an open dispatch).
        await new VendorDispatchService(
                _ctx.Db, Mock.Of<IDataUpdateService>(), Mock.Of<IAuditTrailService>(),
                Mock.Of<IMessagePublisher>(), Mock.Of<ILogger<VendorDispatchService>>(), TimeProvider.System)
            .DispatchAsync(PortfolioId, workOrder.Id, new DispatchWorkOrderRequest { VendorId = vendor.Id }, changedByUserId: 1);

        var reply = await CreateRouter().RouteAsync("+16145550199", "DONE", DateTime.UtcNow);

        reply.Should().Contain("complete");

        var reloadedWo = await _ctx.Db.WorkOrders.FindAsync(workOrder.Id);
        reloadedWo!.Status.Should().Be(WorkOrderStatus.Completed);

        // Rent was NOT touched by the vendor path.
        var reloadedRent = await _ctx.Db.Payments.FindAsync(rent.Id);
        reloadedRent!.Status.Should().Be(PaymentStatus.Scheduled);
    }

    [Fact]
    public async Task Route_TenantYes_MarksRentPaid_NotVendor()
    {
        var property = SeedProperty();
        var tenant = SeedTenant("+16145550123");
        var lease = SeedLease(property, tenant);
        var rent = SeedRent(lease);
        await _ctx.Db.SaveChangesAsync();

        var reply = await CreateRouter().RouteAsync("+16145550123", "YES", DateTime.UtcNow);

        reply.Should().Contain("recorded");

        var reloadedRent = await _ctx.Db.Payments.FindAsync(rent.Id);
        reloadedRent!.Status.Should().Be(PaymentStatus.Paid);
    }

    private Property SeedProperty()
    {
        var now = DateTime.UtcNow;
        var property = new Property
        {
            PortfolioId = PortfolioId,
            Name = "Maple Court",
            AddressLine1 = "12 Maple Ct",
            City = "Columbus",
            State = "OH",
            PostalCode = "43215",
            CreatedAt = now,
            UpdatedAt = now,
        };
        _ctx.Db.Properties.Add(property);
        _ctx.Db.SaveChanges();
        return property;
    }

    private Vendor SeedVendor(string phone)
    {
        var now = DateTime.UtcNow;
        var vendor = new Vendor
        {
            PortfolioId = PortfolioId,
            Name = "Ace Plumbing",
            ServiceType = "Plumbing",
            Phone = phone,
            CreatedAt = now,
            UpdatedAt = now,
        };
        _ctx.Db.Vendors.Add(vendor);
        _ctx.Db.SaveChanges();
        return vendor;
    }

    private WorkOrder SeedWorkOrder(Property property)
    {
        var now = DateTime.UtcNow;
        var workOrder = new WorkOrder
        {
            PortfolioId = PortfolioId,
            PropertyId = property.Id,
            Title = "Leaky faucet",
            Description = "Kitchen sink drips",
            Priority = WorkOrderPriority.Normal,
            Status = WorkOrderStatus.New,
            RequestedAt = now,
            UpdatedAt = now,
        };
        _ctx.Db.WorkOrders.Add(workOrder);
        _ctx.Db.SaveChanges();
        return workOrder;
    }

    private Tenant SeedTenant(string phone)
    {
        var now = DateTime.UtcNow;
        var tenant = new Tenant
        {
            PortfolioId = PortfolioId,
            FirstName = "Emily",
            LastName = "Chen",
            Phone = phone,
            CreatedAt = now,
            UpdatedAt = now,
        };
        _ctx.Db.Tenants.Add(tenant);
        _ctx.Db.SaveChanges();
        return tenant;
    }

    private Lease SeedLease(Property property, Tenant tenant)
    {
        var now = DateTime.UtcNow;
        var unit = new Unit
        {
            Property = property,
            UnitNumber = "1A",
            Bedrooms = 2,
            Bathrooms = 1,
            MarketRent = 1200m,
            CreatedAt = now,
            UpdatedAt = now,
        };
        var lease = new Lease
        {
            PortfolioId = PortfolioId,
            Property = property,
            Unit = unit,
            Tenant = tenant,
            LeaseNumber = "L2026-001",
            Status = LeaseStatus.Active,
            StartDate = new DateTime(2026, 01, 01, 0, 0, 0, DateTimeKind.Utc),
            EndDate = new DateTime(2026, 12, 31, 0, 0, 0, DateTimeKind.Utc),
            MonthlyRent = 1200m,
            RentDueDay = 1,
            CreatedAt = now,
            UpdatedAt = now,
        };
        _ctx.Db.Leases.Add(lease);
        _ctx.Db.SaveChanges();
        return lease;
    }

    private Payment SeedRent(Lease lease)
    {
        var now = DateTime.UtcNow;
        var payment = new Payment
        {
            PortfolioId = PortfolioId,
            Lease = lease,
            PaymentType = PaymentType.Rent,
            Status = PaymentStatus.Scheduled,
            Amount = 1200m,
            DueDate = new DateTime(2026, 06, 01, 0, 0, 0, DateTimeKind.Utc),
            CreatedAt = now,
            UpdatedAt = now,
        };
        _ctx.Db.Payments.Add(payment);
        _ctx.Db.SaveChanges();
        return payment;
    }
}
