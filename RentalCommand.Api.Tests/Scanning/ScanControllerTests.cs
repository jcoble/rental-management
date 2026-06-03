using System.Security.Claims;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Moq;
using RentalCommand.Api.Controllers;
using RentalCommand.Api.DTOs;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Interfaces;
using RentalCommand.Data;

namespace RentalCommand.Api.Tests.Scanning;

public class ScanControllerTests : IDisposable
{
    private readonly SqliteConnection _conn;
    private readonly RentalCommandDbContext _db;

    public ScanControllerTests()
    {
        _conn = new SqliteConnection("DataSource=:memory:");
        _conn.Open();

        var options = new DbContextOptionsBuilder<RentalCommandDbContext>()
            .UseSqlite(_conn)
            .Options;

        _db = new ScanControllerTestDbContext(options);
        _db.Database.EnsureCreated();
    }

    public void Dispose()
    {
        _db.Dispose();
        _conn.Dispose();
    }

    [Fact]
    public async Task Upload_WithWorkOrderTarget_CreatesDraft()
    {
        var scan = new Mock<IScanService>(MockBehavior.Strict);
        scan.Setup(s => s.CreateDraftAsync(
                42,
                It.Is<byte[]>(b => b.SequenceEqual(new byte[] { 1, 2, 3 })),
                "image/jpeg",
                "WorkOrder",
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ScanDraft
            {
                Id = 17,
                PortfolioId = 42,
                TargetEntityType = "WorkOrder",
                Status = "Pending",
                FilePath = "uploads/work-order.jpg",
                CreatedAt = DateTime.UtcNow,
            });

        var controller = CreateController(scan.Object);
        var file = new FormFile(new MemoryStream([1, 2, 3]), 0, 3, "file", "work-order.jpg")
        {
            Headers = new HeaderDictionary(),
            ContentType = "image/jpeg",
        };

        var result = await controller.Upload(file, "WorkOrder", CancellationToken.None);

        var created = result.Result.Should().BeOfType<CreatedAtActionResult>().Subject;
        var body = created.Value.Should().BeOfType<ScanCreatedResponse>().Subject;
        body.DraftId.Should().Be(17);
        body.Status.Should().Be("Pending");
        scan.VerifyAll();
    }

    private ScanController CreateController(IScanService scan)
    {
        var files = Mock.Of<IFileStorage>();
        var controller = new ScanController(scan, _db, files)
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

internal sealed class ScanControllerTestDbContext : RentalCommandDbContext
{
    public ScanControllerTestDbContext(DbContextOptions<RentalCommandDbContext> options) : base(options) { }
}
