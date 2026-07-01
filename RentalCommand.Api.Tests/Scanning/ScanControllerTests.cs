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
using RentalCommand.Core.Enums;
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

    [Fact]
    public async Task ListPage_IncludesCreatedUnitId_ForUnitTiedConfirmedRecords()
    {
        var now = DateTime.UtcNow;
        _db.Portfolios.Add(new Portfolio
        {
            Id = 42,
            Name = "Portfolio 42",
            ManagementCompanyName = "Test Co",
            TimeZone = "UTC",
            CreatedAt = now,
            UpdatedAt = now,
        });
        _db.Properties.Add(new Property
        {
            Id = 10,
            PortfolioId = 42,
            Name = "Test Property",
            AddressLine1 = "1 Main St",
            City = "Columbus",
            State = "OH",
            PostalCode = "43215",
            CreatedAt = now,
            UpdatedAt = now,
        });
        _db.Units.Add(new Unit
        {
            Id = 11,
            PropertyId = 10,
            UnitNumber = "A",
            CreatedAt = now,
            UpdatedAt = now,
        });
        _db.Tenants.Add(new Tenant
        {
            Id = 12,
            PortfolioId = 42,
            FirstName = "Test",
            LastName = "Tenant",
            CreatedAt = now,
            UpdatedAt = now,
        });
        _db.Leases.Add(new Lease
        {
            Id = 13,
            PortfolioId = 42,
            PropertyId = 10,
            UnitId = 11,
            TenantId = 12,
            LeaseNumber = "L-13",
            Status = LeaseStatus.Active,
            StartDate = now.Date,
            EndDate = now.Date.AddYears(1),
            MonthlyRent = 1200m,
            CreatedAt = now,
            UpdatedAt = now,
        });
        _db.Payments.Add(new Payment
        {
            Id = 14,
            PortfolioId = 42,
            LeaseId = 13,
            Amount = 1200m,
            DueDate = now.Date,
            CreatedAt = now,
            UpdatedAt = now,
        });
        _db.RentalApplications.Add(new RentalApplication
        {
            Id = 15,
            PortfolioId = 42,
            PropertyId = 10,
            UnitId = 11,
            FirstName = "Applicant",
            LastName = "One",
            CreatedAt = now,
            UpdatedAt = now,
            SubmittedAtUtc = now,
        });
        _db.ScanDrafts.AddRange(
            new ScanDraft
            {
                Id = 21,
                PortfolioId = 42,
                TargetEntityType = "Payment",
                Status = "Confirmed",
                FilePath = "uploads/payment.jpg",
                CreatedAt = now,
            },
            new ScanDraft
            {
                Id = 22,
                PortfolioId = 42,
                TargetEntityType = "Application",
                Status = "Confirmed",
                FilePath = "uploads/application.jpg",
                CreatedAt = now.AddSeconds(1),
            });
        _db.StoredFiles.AddRange(
            new StoredFile
            {
                PortfolioId = 42,
                FileName = "payment.jpg",
                FilePath = "uploads/payment.jpg",
                ContentType = "image/jpeg",
                FileSize = 1,
                EntityType = "Payment",
                EntityId = 14,
            },
            new StoredFile
            {
                PortfolioId = 42,
                FileName = "application.jpg",
                FilePath = "uploads/application.jpg",
                ContentType = "image/jpeg",
                FileSize = 1,
                EntityType = "Application",
                EntityId = 15,
            });
        await _db.SaveChangesAsync();

        var controller = CreateController(Mock.Of<IScanService>());

        var result = await controller.ListPage(new ListQuery { Skip = 0, Take = 20 }, "Confirmed", CancellationToken.None);

        var ok = result.Result.Should().BeOfType<OkObjectResult>().Subject;
        var body = ok.Value.Should().BeOfType<ScanDraftListResponse>().Subject;
        body.Items.Should().HaveCount(2);
        body.Items.Should().Contain(i =>
            i.CreatedEntityType == "Payment" &&
            i.CreatedEntityId == 14 &&
            i.CreatedUnitId == 11);
        body.Items.Should().Contain(i =>
            i.CreatedEntityType == "Application" &&
            i.CreatedEntityId == 15 &&
            i.CreatedUnitId == 11);
    }

    private ScanController CreateController(IScanService scan, IFileStorage? files = null)
    {
        var controller = new ScanController(scan, _db, files ?? Mock.Of<IFileStorage>(), TimeProvider.System)
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
