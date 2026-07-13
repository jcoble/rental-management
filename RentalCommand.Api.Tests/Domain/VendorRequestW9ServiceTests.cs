using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using RentalCommand.Api.Services.Domain;
using RentalCommand.Core.Atomic;
using RentalCommand.Core.Authorization;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Interfaces;
using RentalCommand.Core.Vendors;
using RentalCommand.Data;
using RentalCommand.Data.Auditing;
using RentalCommand.Data.Atomic;
using RentalCommand.Data.Vendors;
using RentalCommand.TestCommon;

namespace RentalCommand.Api.Tests.Domain;

public sealed class VendorRequestW9ServiceTests : IDisposable
{
    private const int PortfolioId = 1;
    private readonly SqliteTestContext _ctx = new();
    private readonly ServiceProvider _services;
    private readonly WorkspaceReadScope _scope;

    public VendorRequestW9ServiceTests()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(TimeProvider.System);
        services.AddScoped<ICurrentActor, SystemCurrentActor>();
        services.AddAtomicPersistenceKernel();
        services.AddAtomicCommandHandler<
            RequestVendorW9Command,
            RequestVendorW9Result,
            RequestVendorW9Handler>();
        services.AddDbContext<RentalCommandDbContext>((provider, options) =>
            options.UseSqlite(_ctx.ConnectionString).UseAtomicPersistenceKernel(provider));
        _services = services.BuildServiceProvider();
        _scope = _ctx.Db.SeedAdministratorScope(PortfolioId, nameof(VendorRequestW9ServiceTests));
    }

    public void Dispose()
    {
        _services.Dispose();
        _ctx.Dispose();
    }

    private VendorService CreateSut() => new(
        _ctx.Db,
        Mock.Of<IDataUpdateService>(),
        _services.GetRequiredService<IAtomicUnitOfWork>(),
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
    public async Task RequestW9_AtomicallyQueuesOneSmsAuditAndReceipt_AndReplays()
    {
        var vendor = SeedVendor("(614) 555-0142");
        var sut = CreateSut();

        var first = await sut.RequestW9Async(_scope, vendor.Id, "stable-op", 7);
        var replay = await sut.RequestW9Async(_scope, vendor.Id, "stable-op", 7);

        first.Should().BeEquivalentTo(RequestW9Result.Queued("+16145550142"));
        replay.Should().BeEquivalentTo(first);
        (await _ctx.Db.OutboxMessages.CountAsync()).Should().Be(1);
        (await _ctx.Db.AtomicAuditLogs.CountAsync()).Should().Be(1);
        (await _ctx.Db.AtomicCommandReceipts.CountAsync()).Should().Be(1);
        var outbox = await _ctx.Db.OutboxMessages.SingleAsync();
        outbox.MessageType.Should().Be("sms");
        using var payload = JsonDocument.Parse(outbox.Payload);
        payload.RootElement.GetProperty("to").GetString().Should().Be("+16145550142");
    }

    [Fact]
    public async Task RequestW9_DifferentOperationIds_AreDeliberateSeparateRequests()
    {
        var vendor = SeedVendor("+16145550142");
        var sut = CreateSut();

        await sut.RequestW9Async(_scope, vendor.Id, "first-action", 7);
        await sut.RequestW9Async(_scope, vendor.Id, "second-action", 7);

        (await _ctx.Db.OutboxMessages.CountAsync()).Should().Be(2);
        (await _ctx.Db.AtomicAuditLogs.CountAsync()).Should().Be(2);
        (await _ctx.Db.AtomicCommandReceipts.CountAsync()).Should().Be(2);
    }

    [Fact]
    public async Task RequestW9_ReturnsNoPhoneWithReceipt_AndNoIntentOrAudit()
    {
        var vendor = SeedVendor(null);

        var result = await CreateSut().RequestW9Async(_scope, vendor.Id, "no-phone", 7);

        result.Outcome.Should().Be(RequestW9Outcome.VendorHasNoPhone);
        (await _ctx.Db.AtomicCommandReceipts.CountAsync()).Should().Be(1);
        (await _ctx.Db.OutboxMessages.CountAsync()).Should().Be(0);
        (await _ctx.Db.AtomicAuditLogs.CountAsync()).Should().Be(0);
    }

    [Theory]
    [InlineData(1, 9999)]
    [InlineData(999, 1)]
    public async Task RequestW9_ReturnsNotFoundWithoutCrossPortfolioSideEffects(
        int portfolioId,
        int vendorSelector)
    {
        var vendor = SeedVendor("+16145550142");
        var vendorId = vendorSelector == 1 ? vendor.Id : vendorSelector;

        var scope = new WorkspaceReadScope(
            portfolioId,
            _scope.UserId,
            _scope.SessionId,
            _scope.AccessContextId,
            _scope.AccessRevision);
        var result = await CreateSut().RequestW9Async(
            scope, vendorId, $"not-found-{portfolioId}", 7);

        result.Outcome.Should().Be(RequestW9Outcome.NotFound);
        (await _ctx.Db.OutboxMessages.CountAsync()).Should().Be(0);
        (await _ctx.Db.AtomicAuditLogs.CountAsync()).Should().Be(0);
    }
}
