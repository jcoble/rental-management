using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;
using RentalCommand.Api.Services.Domain;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Interfaces;
using RentalCommand.TestCommon;

namespace RentalCommand.Api.Tests.Domain;

/// <summary>
/// Covers the W-9 text request: enqueues an SMS for a vendor with a phone on file, and refuses (no SMS)
/// when the vendor has no phone or does not exist.
/// </summary>
public class VendorRequestW9ServiceTests : IDisposable
{
    private const int PortfolioId = 1;

    private readonly SqliteTestContext _ctx = new();

    public void Dispose() => _ctx.Dispose();

    private VendorService CreateSut(Mock<IMessagePublisher>? publisher = null) => new(
        _ctx.Db,
        Mock.Of<IDataUpdateService>(),
        (publisher ?? new Mock<IMessagePublisher>()).Object,
        Mock.Of<IAuditTrailService>(),
        Mock.Of<ILogger<VendorService>>(),
        TimeProvider.System);

    private Vendor SeedVendor(string? phone)
    {
        var now = DateTime.UtcNow;
        var vendor = new Vendor
        {
            PortfolioId = PortfolioId,
            Name = "Ace Plumbing",
            ServiceType = "Plumbing",
            Phone = phone,
            Is1099Eligible = true,
            CreatedAt = now,
            UpdatedAt = now,
        };
        _ctx.Db.Vendors.Add(vendor);
        _ctx.Db.SaveChanges();
        return vendor;
    }

    [Fact]
    public async Task RequestW9_EnqueuesSms_WhenVendorHasPhone()
    {
        var vendor = SeedVendor("(614) 555-0142");
        var publisher = new Mock<IMessagePublisher>();
        var sut = CreateSut(publisher);

        var result = await sut.RequestW9Async(PortfolioId, vendor.Id, changedByUserId: 7);

        result.Outcome.Should().Be(RequestW9Outcome.Queued);
        result.Phone.Should().Be("+16145550142"); // normalized E.164

        publisher.Verify(p => p.PublishAsync(
            PortfolioId,
            "sms",
            It.IsAny<object>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task RequestW9_ReturnsNoPhone_AndDoesNotSend_WhenVendorHasNoPhone()
    {
        var vendor = SeedVendor(phone: null);
        var publisher = new Mock<IMessagePublisher>();
        var sut = CreateSut(publisher);

        var result = await sut.RequestW9Async(PortfolioId, vendor.Id, changedByUserId: 7);

        result.Outcome.Should().Be(RequestW9Outcome.VendorHasNoPhone);
        publisher.Verify(p => p.PublishAsync(
            It.IsAny<int>(), It.IsAny<string>(), It.IsAny<object>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task RequestW9_ReturnsNotFound_ForUnknownVendor()
    {
        var publisher = new Mock<IMessagePublisher>();
        var sut = CreateSut(publisher);

        var result = await sut.RequestW9Async(PortfolioId, id: 9999, changedByUserId: 7);

        result.Outcome.Should().Be(RequestW9Outcome.NotFound);
        publisher.Verify(p => p.PublishAsync(
            It.IsAny<int>(), It.IsAny<string>(), It.IsAny<object>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task RequestW9_DoesNotCrossPortfolios()
    {
        var vendor = SeedVendor("(614) 555-0142");
        var publisher = new Mock<IMessagePublisher>();
        var sut = CreateSut(publisher);

        // Same vendor id, but a different portfolio scope → not found, no SMS.
        var result = await sut.RequestW9Async(portfolioId: 999, vendor.Id, changedByUserId: 7);

        result.Outcome.Should().Be(RequestW9Outcome.NotFound);
        publisher.Verify(p => p.PublishAsync(
            It.IsAny<int>(), It.IsAny<string>(), It.IsAny<object>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }
}
