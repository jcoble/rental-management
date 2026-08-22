using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using RentalCommand.Api.DTOs;
using RentalCommand.Api.Services.Domain;
using RentalCommand.Api.Writes;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Interfaces;
using RentalCommand.Core.Atomic;
using RentalCommand.Core.Operations;
using RentalCommand.Data;
using RentalCommand.Data.Atomic;
using RentalCommand.Data.Auditing;
using RentalCommand.Data.Operations;
using RentalCommand.TestCommon;

namespace RentalCommand.Api.Tests.Domain;

/// <summary>
/// Covers the inbound SMS clean replacement: only one uniquely matched vendor DONE can mutate state.
/// Tenant YES and ambiguous vendor events are acknowledged without touching work orders.
/// </summary>
public class SmsInboundRouterTests : IDisposable
{
    private const int PortfolioId = 1;

    private readonly SqliteTestContext _ctx = new();
    private readonly ServiceProvider _services;

    public SmsInboundRouterTests()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddScoped<ICurrentActor, SystemCurrentActor>();
        services.AddAtomicPersistenceKernel();
        services.AddScoped<IRequestWriteExecutor, RequestWriteExecutor>();
        services.AddDbContext<RentalCommandDbContext>((provider, builder) =>
            builder.UseSqlite(_ctx.ConnectionString).UseAtomicPersistenceKernel(provider));
        _services = services.BuildServiceProvider();
    }

    public void Dispose()
    {
        _services.Dispose();
        _ctx.Dispose();
    }

    private SmsInboundRouter CreateRouter()
    {
        var vendorDone = new SmsInboundVendorDoneService(
            _services.GetRequiredService<RentalCommandDbContext>(),
            Mock.Of<IDataUpdateService>(),
            _services.GetRequiredService<IRequestWriteExecutor>(),
            Mock.Of<ILogger<SmsInboundVendorDoneService>>());

        return new SmsInboundRouter(vendorDone);
    }

    [Fact]
    public async Task Route_VendorDone_ClosesWorkOrder_NotRent()
    {
        var property = SeedProperty();
        var vendor = SeedVendor("+16145550199");
        var workOrder = SeedWorkOrder(property);

        // An unrelated tenant shares the SAME phone as the vendor. The router must still prefer the
        // vendor-DONE path because there is an open dispatch + DONE keyword.
        SeedTenant("+16145550199");
        await _ctx.Db.SaveChangesAsync();

        // Seed the already-dispatched state; dispatch command atomicity has its own focused suite.
        workOrder.VendorId = vendor.Id;
        _ctx.Db.VendorDispatches.Add(new VendorDispatch
        {
            PortfolioId = PortfolioId,
            WorkOrderId = workOrder.Id,
            VendorId = vendor.Id,
            Status = VendorDispatchStatus.Dispatched,
            DispatchedAtUtc = DateTime.UtcNow,
            Message = "Reply DONE when complete.",
        });
        await _ctx.Db.SaveChangesAsync();

        var reply = await CreateRouter().RouteAsync(
            "SM-router-vendor-done", "+16145550199", "DONE", DateTime.UtcNow);

        reply.Should().Contain("complete");

        _ctx.Db.ChangeTracker.Clear();
        var reloadedWo = await _ctx.Db.WorkOrders.FindAsync(workOrder.Id);
        reloadedWo!.Status.Should().Be(WorkOrderStatus.Completed);

    }

    [Fact]
    public async Task Route_TenantYes_IsNoOpAndStoresReceipt()
    {
        SeedTenant("+16145550123");
        await _ctx.Db.SaveChangesAsync();

        var reply = await CreateRouter().RouteAsync(
            "SM-router-tenant-yes", "+16145550123", "YES", DateTime.UtcNow);

        reply.Should().Contain("Reply DONE");

        (await _ctx.Db.AtomicCommandReceipts.CountAsync(receipt =>
            receipt.CommandType == "sms.vendor-done")).Should().Be(1);
    }

    [Fact]
    public async Task Route_DoneWithTwoSamePortfolioMatches_ReceiptsNoOpAndMutatesNeither()
    {
        var property = SeedProperty();
        var firstVendor = SeedVendor("+16145550199");
        var secondVendor = SeedVendor("(614) 555-0199");
        var firstWorkOrder = SeedWorkOrder(property);
        var secondWorkOrder = SeedWorkOrder(property);
        await _ctx.Db.SaveChangesAsync();
        _ctx.Db.VendorDispatches.AddRange(
            NewDispatch(firstWorkOrder.Id, firstVendor.Id),
            NewDispatch(secondWorkOrder.Id, secondVendor.Id));
        await _ctx.Db.SaveChangesAsync();

        var reply = await CreateRouter().RouteAsync(
            "SM-router-ambiguous", "+16145550199", "DONE", DateTime.UtcNow);

        reply.Should().Contain("could not match");
        (await _ctx.Db.VendorDispatches.CountAsync(row =>
            row.Status == VendorDispatchStatus.Dispatched)).Should().Be(2);
        (await _ctx.Db.WorkOrders.CountAsync(row =>
            row.Status == WorkOrderStatus.Completed)).Should().Be(0);
        (await _ctx.Db.AtomicCommandReceipts.CountAsync(receipt =>
            receipt.CommandType == "sms.vendor-done")).Should().Be(1);
    }

    private static VendorDispatch NewDispatch(int workOrderId, int vendorId) => new()
    {
        PortfolioId = PortfolioId,
        WorkOrderId = workOrderId,
        VendorId = vendorId,
        Status = VendorDispatchStatus.Dispatched,
        DispatchedAtUtc = DateTime.UtcNow,
        Message = "Reply DONE when complete.",
    };

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

}
