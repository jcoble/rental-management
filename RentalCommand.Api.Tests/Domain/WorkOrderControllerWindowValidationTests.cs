using System.Security.Claims;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Moq;
using RentalCommand.Api.Controllers;
using RentalCommand.Api.DTOs;
using RentalCommand.Api.Services.Domain;
using RentalCommand.Core.Interfaces;
using RentalCommand.Data;

namespace RentalCommand.Api.Tests.Domain;

/// <summary>
/// The arrival-window invariant (end must be after start) is enforced at the controller as a 400 before
/// the service runs, mirroring the DTO-level validation idiom used across the API.
/// </summary>
public class WorkOrderControllerWindowValidationTests : IDisposable
{
    private readonly SqliteConnection _conn;
    private readonly RentalCommandDbContext _db;

    public WorkOrderControllerWindowValidationTests()
    {
        _conn = new SqliteConnection("DataSource=:memory:");
        _conn.Open();
        var options = new DbContextOptionsBuilder<RentalCommandDbContext>().UseSqlite(_conn).Options;
        _db = new WorkOrderWindowTestDbContext(options);
        _db.Database.EnsureCreated();
    }

    public void Dispose()
    {
        _db.Dispose();
        _conn.Dispose();
    }

    [Fact]
    public async Task Create_WindowEndBeforeStart_Returns400_AndDoesNotCallService()
    {
        var service = new Mock<IWorkOrderService>(MockBehavior.Strict);
        var controller = CreateController(service.Object);

        var result = await controller.Create(new CreateWorkOrderRequest
        {
            PropertyId = 1,
            Title = "Backwards window",
            Description = "End before start",
            ScheduledFor = new DateTimeOffset(2026, 6, 20, 16, 0, 0, TimeSpan.FromHours(-4)),
            ScheduledWindowEnd = new DateTimeOffset(2026, 6, 20, 14, 0, 0, TimeSpan.FromHours(-4)),
        }, CancellationToken.None);

        result.Result.Should().BeOfType<BadRequestObjectResult>();
        service.Verify(s => s.CreateAsync(It.IsAny<int>(), It.IsAny<CreateWorkOrderRequest>(),
            It.IsAny<int?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Create_WindowEndEqualsStart_Returns400()
    {
        var service = new Mock<IWorkOrderService>(MockBehavior.Strict);
        var controller = CreateController(service.Object);

        var same = new DateTimeOffset(2026, 6, 20, 14, 0, 0, TimeSpan.FromHours(-4));
        var result = await controller.Create(new CreateWorkOrderRequest
        {
            PropertyId = 1,
            Title = "Zero-length window",
            Description = "End equals start",
            ScheduledFor = same,
            ScheduledWindowEnd = same,
        }, CancellationToken.None);

        result.Result.Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task Update_WindowEndBeforeStart_Returns400_AndDoesNotCallService()
    {
        var service = new Mock<IWorkOrderService>(MockBehavior.Strict);
        var controller = CreateController(service.Object);

        var result = await controller.Update(5, new UpdateWorkOrderRequest
        {
            ScheduledFor = new DateTimeOffset(2026, 6, 20, 16, 0, 0, TimeSpan.FromHours(-4)),
            ScheduledWindowEnd = new DateTimeOffset(2026, 6, 20, 15, 0, 0, TimeSpan.FromHours(-4)),
        }, CancellationToken.None);

        result.Result.Should().BeOfType<BadRequestObjectResult>();
        service.Verify(s => s.UpdateAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<UpdateWorkOrderRequest>(),
            It.IsAny<int?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Create_ValidWindow_ReachesService()
    {
        var service = new Mock<IWorkOrderService>();
        service.Setup(s => s.CreateAsync(It.IsAny<int>(), It.IsAny<CreateWorkOrderRequest>(),
                It.IsAny<int?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new WorkOrderResponse { Id = 11, Title = "Valid window" });
        var controller = CreateController(service.Object);

        var result = await controller.Create(new CreateWorkOrderRequest
        {
            PropertyId = 1,
            Title = "Valid window",
            Description = "End after start",
            ScheduledFor = new DateTimeOffset(2026, 6, 20, 14, 0, 0, TimeSpan.FromHours(-4)),
            ScheduledWindowEnd = new DateTimeOffset(2026, 6, 20, 16, 0, 0, TimeSpan.FromHours(-4)),
        }, CancellationToken.None);

        result.Result.Should().BeOfType<CreatedAtActionResult>();
        service.Verify(s => s.CreateAsync(It.IsAny<int>(), It.IsAny<CreateWorkOrderRequest>(),
            It.IsAny<int?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Create_WindowEndWithoutStart_DoesNotReject()
    {
        // A window-end with no start is a partial shape; the invariant only fires when both are present.
        var service = new Mock<IWorkOrderService>();
        service.Setup(s => s.CreateAsync(It.IsAny<int>(), It.IsAny<CreateWorkOrderRequest>(),
                It.IsAny<int?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new WorkOrderResponse { Id = 12, Title = "End only" });
        var controller = CreateController(service.Object);

        var result = await controller.Create(new CreateWorkOrderRequest
        {
            PropertyId = 1,
            Title = "End only",
            Description = "No start",
            ScheduledWindowEnd = new DateTimeOffset(2026, 6, 20, 16, 0, 0, TimeSpan.FromHours(-4)),
        }, CancellationToken.None);

        result.Result.Should().BeOfType<CreatedAtActionResult>();
    }

    private WorkOrderController CreateController(IWorkOrderService service)
    {
        var controller = new WorkOrderController(
            service,
            Mock.Of<IVendorDispatchService>(),
            _db,
            Mock.Of<IFileStorage>())
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity([
                        new Claim("portfolioId", "42"),
                        new Claim(ClaimTypes.NameIdentifier, "7"),
                    ], "test")),
                },
            },
        };

        return controller;
    }
}

internal sealed class WorkOrderWindowTestDbContext : RentalCommand.TestCommon.SqliteCompatibleRentalCommandDbContext
{
    public WorkOrderWindowTestDbContext(DbContextOptions<RentalCommandDbContext> options) : base(options) { }
}
