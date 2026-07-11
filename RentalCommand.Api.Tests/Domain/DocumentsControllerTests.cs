using System.Security.Claims;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using RentalCommand.Api.Controllers;
using RentalCommand.Api.DTOs;
using RentalCommand.Api.Services.Domain;
using RentalCommand.Core.Configuration;
using RentalCommand.Core.Documents;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Interfaces;
using RentalCommand.TestCommon;
using RentalCommand.Data.Documents;
using SkiaSharp;

namespace RentalCommand.Api.Tests.Domain;

public sealed class DocumentsControllerTests : IDisposable
{
    private const int PortfolioId = 1;

    private static readonly byte[] TestPng = BuildTestPng();

    private readonly SqliteTestContext _ctx = new();

    public void Dispose() => _ctx.Dispose();

    [Fact]
    public async Task GetFile_WithThumbForImage_ReturnsJpegThumbnail()
    {
        var documents = new Mock<IDocumentService>();
        documents
            .Setup(d => d.FindAsync(PortfolioId, 7, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new StoredFile
            {
                Id = 7,
                PortfolioId = PortfolioId,
                EntityType = "Unit",
                EntityId = 3,
                FileName = "inspection.png",
                FilePath = "stored/inspection.png",
                ContentType = "image/png",
                FileSize = TestPng.Length,
                UploadedAt = DateTime.UtcNow,
            });

        var storage = new Mock<IFileStorage>();
        storage
            .Setup(s => s.DownloadAsync("stored/inspection.png", It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new MemoryStream(TestPng));

        var controller = CreateController(
            documents.Object,
            storage.Object,
            new Claim(ClaimTypes.Role, nameof(UserRole.Admin)));

        var result = await controller.GetFile(7, thumb: true, CancellationToken.None);

        var file = result.Should().BeOfType<FileContentResult>().Subject;
        file.ContentType.Should().Be("image/jpeg");
        file.FileContents.Should().NotBeEquivalentTo(TestPng);
        controller.Response.Headers.ContentDisposition.ToString().Should().Be("inline; filename=\"inspection-thumb.jpg\"");
    }

    public static IEnumerable<object[]> StaffUploadTargets()
    {
        yield return ["Unit"];
        yield return ["Lease"];
        yield return ["WorkOrder"];
        yield return ["Appointment"];
        yield return ["Tenant"];
        yield return ["OwnerEntity"];
        yield return ["SecurityDeposit"];
        yield return ["Inspection"];
    }

    [Theory]
    [MemberData(nameof(StaffUploadTargets))]
    public async Task Upload_WithStaffRoleAndTenantClaim_AllowsPortfolioDocumentTargets(string entityType)
    {
        var entityId = SeedDocumentTarget(entityType);

        var documents = new Mock<IDocumentService>();
        var admission = new PendingFileUploadAdmission(
            Guid.NewGuid(), "stored/test-upload.txt", PendingFileUploadState.Prepared, null,
            "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
        documents.Setup(d => d.PrepareUploadAsync(
                PortfolioId, 7, "upload-operation", It.IsAny<string>(), "test-upload.txt",
                "text/plain", It.IsAny<long>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(admission);
        documents.Setup(d => d.GetFinalizedUploadAsync(
                PortfolioId, admission, It.IsAny<CancellationToken>()))
            .ReturnsAsync((DocumentDto?)null);
        documents
            .Setup(d => d.CreateAsync(
                admission.Id,
                PortfolioId,
                Enum.Parse<StoredDocumentTarget>(entityType),
                entityId,
                7,
                6,
                true,
                "upload-operation",
                It.IsAny<string>(),
                It.IsAny<string>(),
                "test-upload.txt",
                "text/plain",
                It.IsAny<long>(),
                "stored/test-upload.txt",
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DocumentDto
            {
                Id = 101,
                EntityType = entityType,
                EntityId = entityId,
                FileName = "test-upload.txt",
                ContentType = "text/plain",
                SizeBytes = 12,
                UploadedAt = DateTime.UtcNow,
            });

        var storage = new Mock<IFileStorage>();
        storage
            .Setup(s => s.UploadAtAsync(
                It.IsAny<Stream>(),
                "stored/test-upload.txt",
                "test-upload.txt",
                "text/plain",
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var controller = CreateController(
            documents.Object,
            storage.Object,
            new Claim("tenantId", "6"),
            new Claim(ClaimTypes.Role, nameof(UserRole.Admin)));
        var file = FormFile("test-upload.txt", "text/plain", "hello upload");

        var result = await controller.Upload(
            file, entityType, entityId, category: null, clientOperationId: "upload-operation", ct: CancellationToken.None);

        var created = result.Result.Should().BeOfType<CreatedAtActionResult>().Subject;
        created.Value.Should().BeOfType<DocumentDto>()
            .Which.Should().Match<DocumentDto>(d => d.EntityType == entityType && d.EntityId == entityId);
        storage.Verify(s => s.UploadAtAsync(
            It.IsAny<Stream>(),
            "stored/test-upload.txt",
            "test-upload.txt",
            "text/plain",
            It.IsAny<CancellationToken>()), Times.Once);
        documents.Verify(d => d.CreateAsync(
            admission.Id,
            PortfolioId,
            Enum.Parse<StoredDocumentTarget>(entityType),
            entityId,
            7,
            6,
            true,
            "upload-operation",
            It.IsAny<string>(),
            It.IsAny<string>(),
            "test-upload.txt",
            "text/plain",
            It.IsAny<long>(),
            "stored/test-upload.txt",
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Upload_WithTenantOnlyRole_AllowsOwnedWorkOrder()
    {
        const string entityType = "WorkOrder";
        var entityId = SeedDocumentTarget(entityType);
        var tenantId = _ctx.Db.WorkOrders.Single(w => w.Id == entityId).TenantId!.Value;

        var documents = new Mock<IDocumentService>();
        var admission = new PendingFileUploadAdmission(
            Guid.NewGuid(), "stored/test-upload.txt", PendingFileUploadState.Prepared, null,
            "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb");
        documents.Setup(d => d.PrepareUploadAsync(
                PortfolioId, 7, "tenant-upload-operation", It.IsAny<string>(), "test-upload.txt",
                "text/plain", It.IsAny<long>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(admission);
        documents.Setup(d => d.GetFinalizedUploadAsync(
                PortfolioId, admission, It.IsAny<CancellationToken>()))
            .ReturnsAsync((DocumentDto?)null);
        documents
            .Setup(d => d.CreateAsync(
                admission.Id,
                PortfolioId,
                StoredDocumentTarget.WorkOrder,
                entityId,
                7,
                tenantId,
                false,
                "tenant-upload-operation",
                It.IsAny<string>(),
                It.IsAny<string>(),
                "test-upload.txt",
                "text/plain",
                It.IsAny<long>(),
                "stored/test-upload.txt",
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DocumentDto
            {
                Id = 102,
                EntityType = entityType,
                EntityId = entityId,
                FileName = "test-upload.txt",
                ContentType = "text/plain",
                SizeBytes = 12,
                UploadedAt = DateTime.UtcNow,
            });

        var storage = new Mock<IFileStorage>();
        storage
            .Setup(s => s.UploadAtAsync(
                It.IsAny<Stream>(),
                "stored/test-upload.txt",
                "test-upload.txt",
                "text/plain",
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var controller = CreateController(
            documents.Object,
            storage.Object,
            new Claim("tenantId", tenantId.ToString()),
            new Claim(ClaimTypes.Role, nameof(UserRole.Tenant)));
        var file = FormFile("test-upload.txt", "text/plain", "hello upload");

        var result = await controller.Upload(
            file, entityType, entityId, category: null, clientOperationId: "tenant-upload-operation", ct: CancellationToken.None);

        result.Result.Should().BeOfType<CreatedAtActionResult>();
        storage.Verify(s => s.UploadAtAsync(
            It.IsAny<Stream>(),
            "stored/test-upload.txt",
            "test-upload.txt",
            "text/plain",
            It.IsAny<CancellationToken>()), Times.Once);
        documents.Verify(d => d.CreateAsync(
            admission.Id,
            PortfolioId,
            StoredDocumentTarget.WorkOrder,
            entityId,
            7,
            tenantId,
            false,
            "tenant-upload-operation",
            It.IsAny<string>(),
            It.IsAny<string>(),
            "test-upload.txt",
            "text/plain",
            It.IsAny<long>(),
            "stored/test-upload.txt",
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Upload_WithTenantOnlyRole_RejectsKnownInvalidTargetBeforeBlobIo()
    {
        const string entityType = "Unit";
        var entityId = SeedDocumentTarget(entityType);

        var documents = new Mock<IDocumentService>();
        var storage = new Mock<IFileStorage>();
        var controller = CreateController(
            documents.Object,
            storage.Object,
            new Claim("tenantId", "6"),
            new Claim(ClaimTypes.Role, nameof(UserRole.Tenant)));
        var file = FormFile("test-upload.txt", "text/plain", "hello upload");

        var result = await controller.Upload(
            file, entityType, entityId, category: null, clientOperationId: "denied-upload-operation", ct: CancellationToken.None);

        result.Result.Should().BeOfType<NotFoundObjectResult>();
        storage.Verify(s => s.UploadAtAsync(
            It.IsAny<Stream>(),
            It.IsAny<string>(),
            It.IsAny<string>(),
            It.IsAny<string>(),
            It.IsAny<CancellationToken>()), Times.Never);
        documents.Verify(d => d.PrepareUploadAsync(
            It.IsAny<int>(),
            It.IsAny<int>(),
            It.IsAny<string>(),
            It.IsAny<string>(),
            It.IsAny<string>(),
            It.IsAny<string>(),
            It.IsAny<long>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("MaintenanceTechnician")]
    [InlineData("UnknownRole")]
    public async Task List_WithoutExplicitStaffRole_FailsClosed(string? role)
    {
        var documents = new Mock<IDocumentService>();
        Claim[] claims = role is null ? [] : [new Claim(ClaimTypes.Role, role)];
        var controller = CreateController(documents.Object, Mock.Of<IFileStorage>(), claims);

        var result = await controller.List("Unit", 10, CancellationToken.None);

        result.Result.Should().BeOfType<OkObjectResult>()
            .Which.Value.Should().BeAssignableTo<IReadOnlyList<DocumentDto>>()
            .Which.Should().BeEmpty();
        documents.Verify(d => d.ListAsync(
            It.IsAny<int>(), It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task List_WithTenantClaim_AllowsOwnedWorkOrder()
    {
        var workOrderId = SeedDocumentTarget("WorkOrder");
        var tenantId = _ctx.Db.WorkOrders.Single(workOrder => workOrder.Id == workOrderId).TenantId!.Value;
        var expected = new[] { new DocumentDto { Id = 44, EntityType = "WorkOrder", EntityId = workOrderId } };
        var documents = new Mock<IDocumentService>();
        documents.Setup(d => d.ListAsync(
                PortfolioId, "WorkOrder", workOrderId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(expected);
        var controller = CreateController(
            documents.Object,
            Mock.Of<IFileStorage>(),
            new Claim("tenantId", tenantId.ToString()),
            new Claim(ClaimTypes.Role, nameof(UserRole.Tenant)));

        var result = await controller.List("WorkOrder", workOrderId, CancellationToken.None);

        result.Result.Should().BeOfType<OkObjectResult>()
            .Which.Value.Should().BeSameAs(expected);
    }

    [Fact]
    public async Task List_WithExplicitStaffRole_AllowsPortfolioEntity()
    {
        var expected = new[] { new DocumentDto { Id = 45, EntityType = "Unit", EntityId = 10 } };
        var documents = new Mock<IDocumentService>();
        documents.Setup(d => d.ListAsync(PortfolioId, "Unit", 10, It.IsAny<CancellationToken>()))
            .ReturnsAsync(expected);
        var controller = CreateController(
            documents.Object,
            Mock.Of<IFileStorage>(),
            new Claim(ClaimTypes.Role, nameof(UserRole.Admin)));

        var result = await controller.List("Unit", 10, CancellationToken.None);

        result.Result.Should().BeOfType<OkObjectResult>()
            .Which.Value.Should().BeSameAs(expected);
    }

    private static byte[] BuildTestPng()
    {
        using var bitmap = new SKBitmap(4, 4);
        bitmap.Erase(SKColors.Teal);
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    private DocumentsController CreateController(IDocumentService documents, IFileStorage storage, params Claim[] claims)
    {
        var baseClaims = new List<Claim>
        {
            new("portfolioId", PortfolioId.ToString()),
            new(ClaimTypes.NameIdentifier, "7"),
        };
        baseClaims.AddRange(claims);

        var controller = new DocumentsController(
            documents,
            storage,
            _ctx.Db,
            Options.Create(new UploadSettings
            {
                MaxFileSizeBytes = 52_428_800,
                AllowedMimeTypes = ["image/jpeg", "image/png", "application/pdf"],
            }),
            NullLogger<DocumentsController>.Instance)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(baseClaims, "test")),
                },
            },
        };

        return controller;
    }

    private static FormFile FormFile(string fileName, string contentType, string contents)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(contents);
        return new FormFile(new MemoryStream(bytes), 0, bytes.Length, "file", fileName)
        {
            Headers = new HeaderDictionary(),
            ContentType = contentType,
        };
    }

    private int SeedDocumentTarget(string entityType)
    {
        var now = DateTime.UtcNow;
        var property = new Property
        {
            PortfolioId = PortfolioId,
            Name = $"Property {entityType}",
            AddressLine1 = "100 Test",
            City = "Columbus",
            State = "OH",
            PostalCode = "43215",
            CreatedAt = now,
            UpdatedAt = now,
        };
        var unit = new Unit
        {
            Property = property,
            UnitNumber = $"U-{entityType}",
            CreatedAt = now,
            UpdatedAt = now,
        };
        var tenant = new Tenant
        {
            PortfolioId = PortfolioId,
            FirstName = "Test",
            LastName = "Tenant",
            CreatedAt = now,
            UpdatedAt = now,
        };
        _ctx.Db.AddRange(property, unit, tenant);
        _ctx.Db.SaveChanges();

        return entityType switch
        {
            "Property" => property.Id,
            "Unit" => unit.Id,
            "Tenant" => tenant.Id,
            "Lease" => AddAndSave(new Lease
            {
                PortfolioId = PortfolioId,
                PropertyId = property.Id,
                UnitId = unit.Id,
                TenantId = tenant.Id,
                LeaseNumber = $"L-{entityType}",
                Status = LeaseStatus.Active,
                StartDate = now.Date,
                EndDate = now.Date.AddYears(1),
                MonthlyRent = 1_200m,
                CreatedAt = now,
                UpdatedAt = now,
            }).Id,
            "WorkOrder" => AddAndSave(new WorkOrder
            {
                PortfolioId = PortfolioId,
                PropertyId = property.Id,
                UnitId = unit.Id,
                TenantId = tenant.Id,
                Title = "Test work order",
                Description = "Test work order",
                RequestedAt = now,
                UpdatedAt = now,
            }).Id,
            "Appointment" => AddAndSave(new Appointment
            {
                PortfolioId = PortfolioId,
                PropertyId = property.Id,
                UnitId = unit.Id,
                TenantId = tenant.Id,
                Title = "Test appointment",
                ScheduledStart = now.AddDays(1),
                CreatedAt = now,
                UpdatedAt = now,
            }).Id,
            "Inspection" => AddAndSave(new Inspection
            {
                PortfolioId = PortfolioId,
                PropertyId = property.Id,
                UnitId = unit.Id,
                ScheduledFor = now.AddDays(1),
                CreatedAt = now,
                UpdatedAt = now,
            }).Id,
            "SecurityDeposit" => AddAndSave(new SecurityDepositHolding
            {
                PortfolioId = PortfolioId,
                LeaseId = AddAndSave(new Lease
                {
                    PortfolioId = PortfolioId,
                    PropertyId = property.Id,
                    UnitId = unit.Id,
                    TenantId = tenant.Id,
                    LeaseNumber = $"L-{entityType}",
                    Status = LeaseStatus.Active,
                    StartDate = now.Date,
                    EndDate = now.Date.AddYears(1),
                    MonthlyRent = 1_200m,
                    SecurityDeposit = 1_200m,
                    CreatedAt = now,
                    UpdatedAt = now,
                }).Id,
                Amount = 1_200m,
                HeldAt = now,
                CreatedAt = now,
                UpdatedAt = now,
            }).Id,
            "OwnerEntity" => AddAndSave(new OwnerEntity
            {
                PortfolioId = PortfolioId,
                Name = "Test Owner",
                OwnerEntityType = OwnerEntityType.Person,
                CreatedAt = now,
                UpdatedAt = now,
            }).Id,
            _ => throw new ArgumentOutOfRangeException(nameof(entityType), entityType, null),
        };
    }

    private T AddAndSave<T>(T entity) where T : class
    {
        _ctx.Db.Add(entity);
        _ctx.Db.SaveChanges();
        return entity;
    }
}
