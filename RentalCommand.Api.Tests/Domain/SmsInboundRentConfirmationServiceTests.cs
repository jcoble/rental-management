using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;
using RentalCommand.Api.Services;
using RentalCommand.Api.Services.Domain;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Interfaces;
using RentalCommand.TestCommon;

namespace RentalCommand.Api.Tests.Domain;

public class SmsInboundRentConfirmationServiceTests : IDisposable
{
    private readonly SqliteTestContext _ctx = new();

    public void Dispose() => _ctx.Dispose();

    // The side-effect dependencies (realtime broadcast, audit, outbox, LLM) are no-op doubles:
    // these tests assert the core mark-paid behavior, and the service runs all notifications/
    // audit inside a best-effort wrapper. With no LLM configured the classifier falls back to
    // the deterministic Y/YES match, which is what these cases exercise.
    private SmsInboundRentConfirmationService CreateSut() => new(
        _ctx.Db,
        Mock.Of<IDataUpdateService>(),
        Mock.Of<IAuditTrailService>(),
        Mock.Of<IMessagePublisher>(),
        Mock.Of<ILlmProvider>(),
        Mock.Of<ILogger<SmsInboundRentConfirmationService>>(),
        TimeProvider.System);

    [Fact]
    public async Task HandleAsync_YesFromTenantPhone_MarksOldestUnpaidRentPaid()
    {
        var tenant = SeedTenant(phone: "(614) 555-0101");
        var lease = SeedLease(tenant);
        var newer = SeedPayment(lease, new DateTime(2026, 06, 01, 0, 0, 0, DateTimeKind.Utc), 1200m, PaymentStatus.Scheduled);
        var older = SeedPayment(lease, new DateTime(2026, 05, 01, 0, 0, 0, DateTimeKind.Utc), 1200m, PaymentStatus.Late);
        await _ctx.Db.SaveChangesAsync();

        var sut = CreateSut();

        var result = await sut.HandleAsync(
            fromPhone: "+16145550101",
            body: " YES ",
            receivedAtUtc: new DateTime(2026, 06, 03, 12, 0, 0, DateTimeKind.Utc));

        result.Handled.Should().BeTrue();
        result.PaymentId.Should().Be(older.Id);
        result.ResponseMessage.Should().Contain("recorded");

        var updatedOlder = await _ctx.Db.Payments.FindAsync(older.Id);
        updatedOlder!.Status.Should().Be(PaymentStatus.Paid);
        updatedOlder.PaidDate.Should().Be(new DateTime(2026, 06, 03, 12, 0, 0, DateTimeKind.Utc));
        updatedOlder.Method.Should().Be("SMS confirmation");
        updatedOlder.ExternalReference.Should().Be("+16145550101");

        var updatedNewer = await _ctx.Db.Payments.FindAsync(newer.Id);
        updatedNewer!.Status.Should().Be(PaymentStatus.Scheduled);
    }

    [Fact]
    public async Task HandleAsync_NonYesBody_DoesNotChangePayment()
    {
        var tenant = SeedTenant(phone: "+16145550101");
        var lease = SeedLease(tenant);
        var payment = SeedPayment(lease, new DateTime(2026, 06, 01, 0, 0, 0, DateTimeKind.Utc), 1200m, PaymentStatus.Scheduled);
        await _ctx.Db.SaveChangesAsync();

        var sut = CreateSut();

        var result = await sut.HandleAsync("+16145550101", "not yet", DateTime.UtcNow);

        result.Handled.Should().BeFalse();
        result.ResponseMessage.Should().Contain("Reply YES");

        var unchanged = await _ctx.Db.Payments.FindAsync(payment.Id);
        unchanged!.Status.Should().Be(PaymentStatus.Scheduled);
        unchanged.PaidDate.Should().BeNull();
    }

    private Tenant SeedTenant(string phone)
    {
        var tenant = new Tenant
        {
            PortfolioId = 1,
            FirstName = "Emily",
            LastName = "Chen",
            Phone = phone,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        _ctx.Db.Tenants.Add(tenant);
        return tenant;
    }

    private Lease SeedLease(Tenant tenant)
    {
        var property = new Property
        {
            PortfolioId = 1,
            Name = "Short North Condo",
            AddressLine1 = "100 High St",
            City = "Columbus",
            State = "OH",
            PostalCode = "43215",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        var unit = new Unit
        {
            Property = property,
            UnitNumber = "4B",
            Bedrooms = 2,
            Bathrooms = 1,
            MarketRent = 1200m,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        var lease = new Lease
        {
            PortfolioId = 1,
            Property = property,
            Unit = unit,
            Tenant = tenant,
            LeaseNumber = "L2026-001",
            Status = LeaseStatus.Active,
            StartDate = new DateTime(2026, 01, 01, 0, 0, 0, DateTimeKind.Utc),
            EndDate = new DateTime(2026, 12, 31, 0, 0, 0, DateTimeKind.Utc),
            MonthlyRent = 1200m,
            RentDueDay = 1,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        _ctx.Db.Leases.Add(lease);
        return lease;
    }

    private Payment SeedPayment(Lease lease, DateTime dueDate, decimal amount, PaymentStatus status)
    {
        var payment = new Payment
        {
            PortfolioId = 1,
            Lease = lease,
            PaymentType = PaymentType.Rent,
            Status = status,
            Amount = amount,
            DueDate = dueDate,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        _ctx.Db.Payments.Add(payment);
        return payment;
    }
}
