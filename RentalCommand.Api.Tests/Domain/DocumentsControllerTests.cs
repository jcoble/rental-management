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
using RentalCommand.Api.Auth;
using RentalCommand.Core.Authorization;
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
            isManagement: true);

        var result = await controller.GetFile(7, thumb: true, CancellationToken.None);

        var file = result.Should().BeOfType<FileContentResult>().Subject;
        file.ContentType.Should().Be("image/jpeg");
        file.FileContents.Should().NotBeEquivalentTo(TestPng);
        controller.Response.Headers.ContentDisposition.ToString().Should().Be("inline; filename=\"inspection-thumb.jpg\"");
    }

    public static IEnumerable<object[]> StaffUploadTargets()
    {
        yield return ["Unit"];
        yield return ["LeaseAgreement"];
        yield return ["LegalDocumentArtifact"];
        yield return ["TenantAccount"];
        yield return ["TenantLedgerEntry"];
        yield return ["WorkOrder"];
        yield return ["Appointment"];
        yield return ["Tenant"];
        yield return ["OwnerEntity"];
        yield return ["SecurityDepositAccount"];
        yield return ["Inspection"];
    }

    [Theory]
    [MemberData(nameof(StaffUploadTargets))]
    public async Task Upload_WithCanonicalStaffContext_AllowsPortfolioDocumentTargets(string entityType)
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
                null,
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
            isManagement: true);
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
            null,
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

    [Theory]
    [InlineData("Lease")]
    [InlineData("Payment")]
    [InlineData("SecurityDeposit")]
    public async Task Upload_RejectsRemovedLegacyDocumentTargets(string entityType)
    {
        var documents = new Mock<IDocumentService>();
        var storage = new Mock<IFileStorage>();
        var controller = CreateController(
            documents.Object,
            storage.Object,
            isManagement: true);

        var result = await controller.Upload(
            FormFile("test-upload.txt", "text/plain", "legacy target"),
            entityType,
            1,
            category: null,
            clientOperationId: "legacy-target-operation",
            ct: CancellationToken.None);

        result.Result.Should().BeOfType<BadRequestObjectResult>();
        documents.VerifyNoOtherCalls();
        storage.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Upload_WithTenantRelationshipContext_AllowsOwnedWorkOrder()
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
            isManagement: false,
            new Claim("tenantId", tenantId.ToString()));
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
    public async Task Upload_WithTenantRelationshipContext_RejectsKnownInvalidTargetBeforeBlobIo()
    {
        const string entityType = "Unit";
        var entityId = SeedDocumentTarget(entityType);
        var tenantId = _ctx.Db.Tenants.OrderByDescending(tenant => tenant.Id).Select(tenant => tenant.Id).First();

        var documents = new Mock<IDocumentService>();
        var storage = new Mock<IFileStorage>();
        var controller = CreateController(
            documents.Object,
            storage.Object,
            isManagement: false,
            new Claim("tenantId", tenantId.ToString()));
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

    [Fact]
    public async Task List_WithoutCanonicalStaffOrTenantRelationship_FailsClosed()
    {
        var documents = new Mock<IDocumentService>();
        var controller = CreateController(
            documents.Object,
            Mock.Of<IFileStorage>(),
            isManagement: false);

        var result = await controller.List("Unit", 10, CancellationToken.None);

        result.Result.Should().BeOfType<OkObjectResult>()
            .Which.Value.Should().BeAssignableTo<IReadOnlyList<DocumentDto>>()
            .Which.Should().BeEmpty();
        documents.Verify(d => d.ListAsync(
            It.IsAny<int>(), It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task List_WithTenantRelationshipContext_AllowsOwnedWorkOrder()
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
            isManagement: false,
            new Claim("tenantId", tenantId.ToString()));

        var result = await controller.List("WorkOrder", workOrderId, CancellationToken.None);

        result.Result.Should().BeOfType<OkObjectResult>()
            .Which.Value.Should().BeSameAs(expected);
    }

    [Fact]
    public async Task List_WithCanonicalStaffContext_AllowsPortfolioEntity()
    {
        var expected = new[] { new DocumentDto { Id = 45, EntityType = "Unit", EntityId = 10 } };
        var documents = new Mock<IDocumentService>();
        documents.Setup(d => d.ListAsync(PortfolioId, "Unit", 10, It.IsAny<CancellationToken>()))
            .ReturnsAsync(expected);
        var controller = CreateController(
            documents.Object,
            Mock.Of<IFileStorage>(),
            isManagement: true);

        var result = await controller.List("Unit", 10, CancellationToken.None);

        result.Result.Should().BeOfType<OkObjectResult>()
            .Which.Value.Should().BeSameAs(expected);
    }

    [Theory]
    [InlineData("Lease")]
    [InlineData("Payment")]
    [InlineData("SecurityDeposit")]
    public async Task List_RejectsRemovedLegacyDocumentTargets(string entityType)
    {
        var documents = new Mock<IDocumentService>();
        var controller = CreateController(
            documents.Object,
            Mock.Of<IFileStorage>(),
            isManagement: true);

        var result = await controller.List(entityType, 1, CancellationToken.None);

        result.Result.Should().BeOfType<BadRequestObjectResult>();
        documents.VerifyNoOtherCalls();
    }

    private static byte[] BuildTestPng()
    {
        using var bitmap = new SKBitmap(4, 4);
        bitmap.Erase(SKColors.Teal);
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    private DocumentsController CreateController(
        IDocumentService documents,
        IFileStorage storage,
        bool isManagement,
        params Claim[] claims)
    {
        EnsureRelationshipProjectionView();
        var user = EnsureUser();
        var context = EnsureAccessContext(user.Id);
        var tenantClaim = claims.SingleOrDefault(claim => claim.Type == "tenantId")?.Value;
        if (int.TryParse(tenantClaim, out var tenantId))
            EnsureTenantRelationship(context, user, tenantId);

        var baseClaims = new List<Claim>
        {
            new("portfolioId", PortfolioId.ToString()),
            new(ClaimTypes.NameIdentifier, user.Id.ToString()),
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
        controller.HttpContext.Items[CanonicalAccessContextHttpItem.Key] = new ActiveAccessContext(
            Guid.NewGuid(), user.Id, context.Id, PortfolioId, 1,
            isManagement ? WorkspaceExperience.Management : WorkspaceExperience.Tenant,
            isManagement ? 1 : null,
            isManagement ? WorkspaceExperience.Management : WorkspaceExperience.Tenant);

        return controller;
    }

    private ApplicationUser EnsureUser()
    {
        var user = _ctx.Db.Users.SingleOrDefault(existing => existing.UserName == "documents-test-user");
        if (user is not null)
            return user;

        user = new ApplicationUser
        {
            Id = 7,
            UserName = "documents-test-user",
            NormalizedUserName = "DOCUMENTS-TEST-USER",
            Email = "documents@example.test",
            NormalizedEmail = "DOCUMENTS@EXAMPLE.TEST",
            DisplayName = "Documents Test User",
            SecurityStamp = Guid.NewGuid().ToString("N"),
            ConcurrencyStamp = Guid.NewGuid().ToString("N"),
            CreatedAt = DateTime.UtcNow,
        };
        _ctx.Db.Users.Add(user);
        _ctx.Db.SaveChanges();
        return user;
    }

    private WorkspaceAccessContext EnsureAccessContext(int userId)
    {
        var existing = _ctx.Db.WorkspaceAccessContexts.SingleOrDefault(context =>
            context.UserId == userId && context.PortfolioId == PortfolioId);
        if (existing is not null)
            return existing;

        var now = DateTime.UtcNow;
        var context = new WorkspaceAccessContext
        {
            UserId = userId,
            PortfolioId = PortfolioId,
            Status = WorkspaceAccessContextStatus.Active,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
        };
        _ctx.Db.WorkspaceAccessContexts.Add(context);
        _ctx.Db.SaveChanges();
        return context;
    }

    private void EnsureTenantRelationship(
        WorkspaceAccessContext context,
        ApplicationUser user,
        int tenantId)
    {
        if (_ctx.Db.TenantUserAccesses.Any(access =>
                access.AccessContextId == context.Id && access.RevokedAtUtc == null))
            return;

        var target = _ctx.Db.WorkOrders
            .Where(candidate => candidate.PortfolioId == PortfolioId && candidate.TenantId == tenantId)
            .OrderBy(candidate => candidate.Id)
            .Select(candidate => new { candidate.PropertyId, candidate.UnitId })
            .FirstOrDefault();
        if (target is null)
        {
            if (!_ctx.Db.Tenants.Any(tenant => tenant.Id == tenantId && tenant.PortfolioId == PortfolioId))
                return;

            target = _ctx.Db.Units
                .Where(unit => unit.Property!.PortfolioId == PortfolioId)
                .OrderByDescending(unit => unit.Id)
                .Select(unit => new { unit.PropertyId, UnitId = (int?)unit.Id })
                .FirstOrDefault();
            if (target is null)
                return;
        }

        var now = DateTime.UtcNow;
        var relationship = new LeaseManagement
        {
            PublicId = Guid.NewGuid(),
            PortfolioId = PortfolioId,
            PropertyId = target.PropertyId,
            UnitId = target.UnitId!.Value,
            RelationshipNumber = $"DOC-{tenantId}-{Guid.NewGuid():N}",
            CreatedAtUtc = now,
            CreatedByUserId = user.Id,
            UpdatedAtUtc = now,
            RowVersion = Guid.NewGuid(),
        };
        var party = new LeaseManagementParty
        {
            PortfolioId = PortfolioId,
            LeaseManagement = relationship,
            TenantId = tenantId,
            Role = LeaseManagementPartyRole.PrimaryTenant,
            EffectiveFrom = DateOnly.FromDateTime(now.AddDays(-1)),
            ChangeReason = "Document access test",
            CreatedAtUtc = now,
            CreatedByUserId = user.Id,
        };
        _ctx.Db.TenantUserAccesses.Add(new TenantUserAccess
        {
            PublicId = Guid.NewGuid(),
            PortfolioId = PortfolioId,
            AccessContext = context,
            ApplicationUser = user,
            LeaseManagementParty = party,
            GrantedAtUtc = now,
            GrantedByUserId = user.Id,
            Reason = "Document access test",
        });
        _ctx.Db.SaveChanges();
    }

    private void EnsureRelationshipProjectionView()
    {
        using var command = _ctx.Connection.CreateCommand();
        command.CommandText = """
            DROP VIEW IF EXISTS "vw_effective_tenant_access";
            CREATE VIEW "vw_effective_tenant_access" AS
            SELECT context."Id" AS "AccessContextId", context."UserId", context."PortfolioId",
                   context."AccessRevision", access."Id" AS "TenantUserAccessId",
                   party."Id" AS "LeaseManagementPartyId", party."TenantId",
                   party."LeaseManagementId", NULL AS "TenantAccountId",
                   relationship."PropertyId", relationship."UnitId"
            FROM "WorkspaceAccessContexts" context
            JOIN "TenantUserAccesses" access
              ON access."AccessContextId" = context."Id"
             AND access."ApplicationUserId" = context."UserId"
             AND access."PortfolioId" = context."PortfolioId"
            JOIN "LeaseManagementParties" party
              ON party."Id" = access."LeaseManagementPartyId"
             AND party."PortfolioId" = access."PortfolioId"
            JOIN "LeaseManagements" relationship
              ON relationship."Id" = party."LeaseManagementId"
             AND relationship."PortfolioId" = party."PortfolioId"
            WHERE context."Status" = 'Active'
              AND context."SuspendedAtUtc" IS NULL
              AND context."RevokedAtUtc" IS NULL
              AND access."RevokedAtUtc" IS NULL
              AND party."EffectiveFrom" <= date('now')
              AND (party."EffectiveThrough" IS NULL OR party."EffectiveThrough" >= date('now'));
            """;
        command.ExecuteNonQuery();
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
            "LeaseAgreement" or "LegalDocumentArtifact" or "TenantAccount" or "TenantLedgerEntry" => property.Id,
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
            "SecurityDepositAccount" => property.Id,
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
