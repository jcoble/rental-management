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

    [Theory]
    [InlineData("1", false)]      // ?full=1    → original (the bug: a bool param 400'd on "1")
    [InlineData("true", false)]   // ?full=true → original
    [InlineData("True", false)]   // case-insensitive
    [InlineData("0", true)]       // ?full=0    → thumbnail (no 400)
    [InlineData("false", true)]   // ?full=false → thumbnail
    [InlineData(null, true)]      // no param   → thumbnail (default)
    [InlineData("", true)]        // empty      → thumbnail
    public async Task DownloadFile_FullFlag_SelectsOriginalOrThumbnail(string? full, bool expectThumbnail)
    {
        _db.Portfolios.Add(new Portfolio
        {
            Id = 42,
            Name = "Portfolio 42",
            ManagementCompanyName = "Test Co",
            TimeZone = "UTC",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        _db.ScanDrafts.Add(new ScanDraft
        {
            Id = 51,
            PortfolioId = 42,
            TargetEntityType = "Expense",
            Status = "Reviewing",
            FilePath = "uploads/scan-51.jpg",
            ThumbnailPath = "thumbs/scan-51.jpg",
            CreatedAt = DateTime.UtcNow,
        });
        _db.StoredFiles.Add(new StoredFile
        {
            PortfolioId = 42,
            FileName = "scan-51.jpg",
            FilePath = "uploads/scan-51.jpg",
            ContentType = "image/jpeg",
            FileSize = 1,
        });
        await _db.SaveChangesAsync();

        var files = new Mock<IFileStorage>(MockBehavior.Strict);
        files.Setup(f => f.DownloadAsync("thumbs/scan-51.jpg", It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new MemoryStream([1]));
        files.Setup(f => f.DownloadAsync("uploads/scan-51.jpg", It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new MemoryStream([2]));

        var controller = CreateController(Mock.Of<IScanService>(), files.Object);

        var result = await controller.DownloadFile(51, full, CancellationToken.None);

        result.Should().BeOfType<FileStreamResult>();
        var disposition = controller.Response.Headers["Content-Disposition"].ToString();

        if (expectThumbnail)
        {
            disposition.Should().Contain("scan-51-preview");
            files.Verify(f => f.DownloadAsync("thumbs/scan-51.jpg", It.IsAny<CancellationToken>()), Times.Once);
            files.Verify(f => f.DownloadAsync("uploads/scan-51.jpg", It.IsAny<CancellationToken>()), Times.Never);
        }
        else
        {
            disposition.Should().Contain("scan-51\"").And.NotContain("preview");
            files.Verify(f => f.DownloadAsync("uploads/scan-51.jpg", It.IsAny<CancellationToken>()), Times.Once);
            files.Verify(f => f.DownloadAsync("thumbs/scan-51.jpg", It.IsAny<CancellationToken>()), Times.Never);
        }
    }

    private ScanController CreateController(IScanService scan, IFileStorage? files = null)
    {
        var controller = new ScanController(scan, _db, files ?? Mock.Of<IFileStorage>())
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
