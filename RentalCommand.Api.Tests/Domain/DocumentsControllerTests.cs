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
        var unitId = SeedDocumentTarget(nameof(StoredDocumentTarget.Unit));
        var documents = new Mock<IDocumentService>();
        documents
            .Setup(d => d.FindAsync(PortfolioId, 7, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new StoredFile
            {
                Id = 7,
                PortfolioId = PortfolioId,
                EntityType = "Unit",
                EntityId = unitId,
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

    [Fact]
    public async Task GetFile_WithAuthorizedDocumentTemplateSource_ReturnsSourcePdf()
    {
        var (storedFile, template, _) = SeedDocumentTemplateSource();
        var documents = new Mock<IDocumentService>();
        documents
            .Setup(d => d.FindAsync(PortfolioId, storedFile.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(storedFile);

        var storage = new Mock<IFileStorage>();
        storage
            .Setup(s => s.DownloadAsync(storedFile.FilePath, It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new MemoryStream("%PDF-1.7 source"u8.ToArray()));

        var controller = CreateController(
            documents.Object,
            storage.Object,
            isManagement: true);

        var result = await controller.GetFile(storedFile.Id, thumb: false, CancellationToken.None);

        result.Should().BeOfType<FileStreamResult>()
            .Which.ContentType.Should().Be("application/pdf");
        controller.Response.Headers.ContentDisposition.ToString()
            .Should().Be($"inline; filename=\"{storedFile.FileName}\"");
        storage.Verify(s => s.DownloadAsync(storedFile.FilePath, It.IsAny<CancellationToken>()), Times.Once);
        template.OriginalStoredFileId.Should().Be(storedFile.Id);
    }

    [Fact]
    public async Task GetFile_WithDocumentTemplateOutsideSelectedPropertyScope_DeniesBeforeStorage()
    {
        var (storedFile, _, authorizedPropertyId) = SeedDocumentTemplateSource();
        var foreignPropertyId = SeedProperty("Foreign template scope").Id;
        var documents = new Mock<IDocumentService>();
        documents
            .Setup(d => d.FindAsync(PortfolioId, storedFile.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(storedFile);

        var storage = new Mock<IFileStorage>();
        var controller = CreateController(
            documents.Object,
            storage.Object,
            isManagement: true);
        RestrictManagementAssignmentToSelectedProperty(RoleProfileKeys.LeasingAgent, foreignPropertyId);

        var result = await controller.GetFile(storedFile.Id, thumb: false, CancellationToken.None);

        result.Should().BeOfType<NotFoundObjectResult>();
        storage.Verify(s => s.DownloadAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        authorizedPropertyId.Should().NotBe(foreignPropertyId);
    }

    [Fact]
    public async Task GetFile_WithDocumentTemplateEntityButUnlinkedStoredFile_DeniesBeforeStorage()
    {
        var (storedFile, template, _) = SeedDocumentTemplateSource();
        var unlinkedFile = new StoredFile
        {
            PortfolioId = PortfolioId,
            EntityType = "DocumentTemplate",
            EntityId = template.Id,
            FileName = "unlinked-source.pdf",
            FilePath = "stored/unlinked-source.pdf",
            ContentType = "application/pdf",
            FileSize = 17,
            UploadedAt = DateTime.UtcNow,
        };
        _ctx.Db.StoredFiles.Add(unlinkedFile);
        _ctx.Db.SaveChanges();

        var documents = new Mock<IDocumentService>();
        documents
            .Setup(d => d.FindAsync(PortfolioId, unlinkedFile.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(unlinkedFile);

        var storage = new Mock<IFileStorage>();
        var controller = CreateController(
            documents.Object,
            storage.Object,
            isManagement: true);

        var result = await controller.GetFile(unlinkedFile.Id, thumb: false, CancellationToken.None);

        result.Should().BeOfType<NotFoundObjectResult>();
        storage.Verify(s => s.DownloadAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        storedFile.Id.Should().Be(template.OriginalStoredFileId);
    }

    public static IEnumerable<object[]> StaffUploadTargets()
    {
        foreach (var target in Enum.GetNames<StoredDocumentTarget>())
            yield return [target];
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
                It.Is<WorkspaceReadScope?>(scope =>
                    scope.HasValue &&
                    scope.Value.PortfolioId == PortfolioId &&
                    scope.Value.UserId == 7),
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
            It.Is<WorkspaceReadScope?>(scope =>
                scope.HasValue &&
                scope.Value.PortfolioId == PortfolioId &&
                scope.Value.UserId == 7),
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
                (WorkspaceReadScope?)null,
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
            (WorkspaceReadScope?)null,
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
        var workOrder = _ctx.Db.WorkOrders.Single(candidate => candidate.Id == workOrderId);
        workOrder.LeaseManagementId = _ctx.Db.EffectiveTenantAccess
            .Single(access => access.TenantId == tenantId)
            .LeaseManagementId;
        _ctx.Db.SaveChanges();

        var result = await controller.List("WorkOrder", workOrderId, CancellationToken.None);

        result.Result.Should().BeOfType<OkObjectResult>()
            .Which.Value.Should().BeSameAs(expected);
    }

    [Fact]
    public async Task GetFile_WithSameTenantButDifferentRelationship_DeniesBeforeStorageAndAllowsExactRelationship()
    {
        var workOrderId = SeedDocumentTarget("WorkOrder");
        var workOrder = _ctx.Db.WorkOrders.Single(candidate => candidate.Id == workOrderId);
        var tenantId = workOrder.TenantId!.Value;
        var documents = new Mock<IDocumentService>();
        documents.Setup(d => d.FindAsync(PortfolioId, 46, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new StoredFile
            {
                Id = 46,
                PortfolioId = PortfolioId,
                EntityType = "WorkOrder",
                EntityId = workOrderId,
                FileName = "repair.jpg",
                FilePath = "stored/repair.jpg",
                ContentType = "image/jpeg",
                FileSize = TestPng.Length,
                UploadedAt = DateTime.UtcNow,
            });
        var storage = new Mock<IFileStorage>();
        storage.Setup(s => s.DownloadAsync(
                "stored/repair.jpg", It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new MemoryStream(TestPng));
        var controller = CreateController(
            documents.Object,
            storage.Object,
            isManagement: false,
            new Claim("tenantId", tenantId.ToString()));
        var accessRelationshipId = _ctx.Db.EffectiveTenantAccess
            .Single(access => access.TenantId == tenantId)
            .LeaseManagementId;
        var differentRelationship = AddAndSave(new LeaseManagement
        {
            PublicId = Guid.NewGuid(),
            PortfolioId = PortfolioId,
            PropertyId = workOrder.PropertyId,
            UnitId = workOrder.UnitId!.Value,
            RelationshipNumber = $"DOC-DIFFERENT-{Guid.NewGuid():N}",
            CreatedAtUtc = DateTime.UtcNow,
            CreatedByUserId = 7,
            UpdatedAtUtc = DateTime.UtcNow,
            RowVersion = Guid.NewGuid(),
        });
        workOrder.LeaseManagementId = differentRelationship.Id;
        _ctx.Db.SaveChanges();

        var denied = await controller.GetFile(46, thumb: false, CancellationToken.None);

        denied.Should().BeOfType<NotFoundObjectResult>();
        storage.Verify(s => s.DownloadAsync(
            "stored/repair.jpg", It.IsAny<CancellationToken>()), Times.Never);

        workOrder.LeaseManagementId = accessRelationshipId;
        _ctx.Db.SaveChanges();

        var allowed = await controller.GetFile(46, thumb: false, CancellationToken.None);

        allowed.Should().BeOfType<FileStreamResult>();
        storage.Verify(s => s.DownloadAsync(
            "stored/repair.jpg", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task List_WithCanonicalStaffContext_AllowsPortfolioEntity()
    {
        var unitId = SeedDocumentTarget(nameof(StoredDocumentTarget.Unit));
        var expected = new[] { new DocumentDto { Id = 45, EntityType = "Unit", EntityId = unitId } };
        var documents = new Mock<IDocumentService>();
        documents.Setup(d => d.ListAsync(PortfolioId, "Unit", unitId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(expected);
        var controller = CreateController(
            documents.Object,
            Mock.Of<IFileStorage>(),
            isManagement: true);

        var result = await controller.List("Unit", unitId, CancellationToken.None);

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
        var activeContext = isManagement
            ? EnsureManagementAuthorization(context, user)
            : new ActiveAccessContext(
                Guid.NewGuid(), user.Id, context.Id, PortfolioId, context.AccessRevision,
                WorkspaceExperience.Tenant, null, WorkspaceExperience.Tenant);

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
        controller.HttpContext.Items[CanonicalAccessContextHttpItem.Key] = activeContext;

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

    private ActiveAccessContext EnsureManagementAuthorization(
        WorkspaceAccessContext context,
        ApplicationUser user)
    {
        var now = DateTime.UtcNow;
        var membership = new WorkspaceMembership
        {
            AccessContext = context,
            PortfolioId = PortfolioId,
            Status = WorkspaceMembershipStatus.Active,
            DefaultExperience = WorkspaceExperience.Management,
            EffectiveFromUtc = now.AddMinutes(-1),
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
        };
        var assignment = new MembershipRoleAssignment
        {
            WorkspaceMembership = membership,
            PortfolioId = PortfolioId,
            RoleProfileId = AccessCatalog.Roles.Single(role =>
                role.Key == RoleProfileKeys.WorkspaceAdministrator).Id,
            Status = MembershipRoleAssignmentStatus.Active,
            ScopeKind = MembershipRoleAssignmentScopeKind.AllProperties,
            EffectiveFromUtc = now.AddMinutes(-1),
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
        };
        var session = new AuthSession
        {
            Id = Guid.NewGuid(),
            User = user,
            ActiveAccessContext = context,
            Status = AuthSessionStatus.Active,
            CreatedAtUtc = now,
            LastSeenAtUtc = now,
            ExpiresAtUtc = now.AddHours(1),
        };
        _ctx.Db.AddRange(assignment, session);
        _ctx.Db.SaveChanges();

        return new ActiveAccessContext(
            session.Id,
            user.Id,
            context.Id,
            PortfolioId,
            context.AccessRevision,
            WorkspaceExperience.Management,
            membership.Id,
            membership.DefaultExperience);
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

    private long SeedDocumentTarget(string entityType)
    {
        var now = DateTime.UtcNow;
        var user = EnsureUser();
        var owner = new OwnerEntity
        {
            PortfolioId = PortfolioId,
            Name = $"Owner {entityType}",
            OwnerEntityType = OwnerEntityType.Person,
            CreatedAt = now,
            UpdatedAt = now,
        };
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
            Ownerships =
            [
                new PropertyOwnership
                {
                    PortfolioId = PortfolioId,
                    OwnerEntity = owner,
                    OwnershipSharePercent = 100m,
                    EffectiveFromUtc = now,
                    StatementRecipientName = owner.Name,
                    PayeeName = owner.Name,
                },
            ],
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
        _ctx.Db.AddRange(owner, property, unit, tenant);
        _ctx.Db.SaveChanges();

        if (entityType is nameof(StoredDocumentTarget.Tenant)
            or nameof(StoredDocumentTarget.LeaseAgreement)
            or nameof(StoredDocumentTarget.LegalDocumentArtifact)
            or nameof(StoredDocumentTarget.TenantAccount)
            or nameof(StoredDocumentTarget.TenantLedgerEntry)
            or nameof(StoredDocumentTarget.SecurityDepositAccount))
        {
            var canonical = SeedCanonicalLeaseDocumentTargets(property, unit, tenant, user, now);
            return entityType switch
            {
                nameof(StoredDocumentTarget.Tenant) => tenant.Id,
                nameof(StoredDocumentTarget.LeaseAgreement) => canonical.Agreement.Id,
                nameof(StoredDocumentTarget.LegalDocumentArtifact) => canonical.Artifact.Id,
                nameof(StoredDocumentTarget.TenantAccount) => canonical.Account.Id,
                nameof(StoredDocumentTarget.TenantLedgerEntry) => canonical.LedgerEntry.Id,
                nameof(StoredDocumentTarget.SecurityDepositAccount) => canonical.DepositAccount.Id,
                _ => throw new InvalidOperationException("The canonical target set is incomplete."),
            };
        }

        return entityType switch
        {
            nameof(StoredDocumentTarget.Property) => property.Id,
            nameof(StoredDocumentTarget.Unit) => unit.Id,
            nameof(StoredDocumentTarget.WorkOrder) => AddAndSave(new WorkOrder
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
            nameof(StoredDocumentTarget.Appointment) => AddAndSave(new Appointment
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
            nameof(StoredDocumentTarget.Inspection) => AddAndSave(new Inspection
            {
                PortfolioId = PortfolioId,
                PropertyId = property.Id,
                UnitId = unit.Id,
                ScheduledFor = now.AddDays(1),
                CreatedAt = now,
                UpdatedAt = now,
            }).Id,
            nameof(StoredDocumentTarget.Expense) => AddAndSave(new Expense
            {
                PortfolioId = PortfolioId,
                PropertyId = property.Id,
                UnitId = unit.Id,
                Description = "Document target expense",
                Amount = 25m,
                IncurredAt = now,
                CreatedAt = now,
                UpdatedAt = now,
            }).Id,
            nameof(StoredDocumentTarget.Vendor) => SeedVendorTarget(property, unit, tenant, now),
            nameof(StoredDocumentTarget.OwnerEntity) => owner.Id,
            _ => throw new ArgumentOutOfRangeException(nameof(entityType), entityType, null),
        };
    }

    private CanonicalLeaseDocumentTargets SeedCanonicalLeaseDocumentTargets(
        Property property,
        Unit unit,
        Tenant tenant,
        ApplicationUser user,
        DateTime now)
    {
        var storedFile = new StoredFile
        {
            PortfolioId = PortfolioId,
            FileName = $"agreement-{Guid.NewGuid():N}.pdf",
            FilePath = $"stored/agreement-{Guid.NewGuid():N}.pdf",
            ContentType = "application/pdf",
            FileSize = 1,
            UploadedAt = now,
        };
        _ctx.Db.StoredFiles.Add(storedFile);
        _ctx.Db.SaveChanges();

        var artifact = new LegalDocumentArtifact
        {
            PublicId = Guid.NewGuid(),
            PortfolioId = PortfolioId,
            StoredFileId = storedFile.Id,
            ArtifactKind = LegalDocumentArtifactKind.IssuedAgreement,
            StorageKey = storedFile.FilePath,
            FileName = storedFile.FileName,
            ContentType = storedFile.ContentType,
            ByteLength = storedFile.FileSize,
            ContentSha256 = new string('a', 64),
            LegalIssuanceFingerprint = new string('b', 64),
            CreatedAtUtc = now,
            CreatedByUserId = user.Id,
        };
        var source = new LegalDocumentSourceVersion
        {
            PublicId = Guid.NewGuid(),
            PortfolioId = PortfolioId,
            SourceKind = LegalDocumentSourceKind.BuiltInRenderer,
            BusinessKey = $"documents-test:{Guid.NewGuid():N}",
            RendererKey = $"documents-test-{Guid.NewGuid():N}",
            RendererVersion = 1,
            SnapshotPayload = "{}",
            CreatedAtUtc = now,
            CreatedByUserId = user.Id,
        };
        var management = new LeaseManagement
        {
            PublicId = Guid.NewGuid(),
            PortfolioId = PortfolioId,
            PropertyId = property.Id,
            UnitId = unit.Id,
            RelationshipNumber = $"DOC-{Guid.NewGuid():N}",
            CreatedAtUtc = now,
            CreatedByUserId = user.Id,
            UpdatedAtUtc = now,
            RowVersion = Guid.NewGuid(),
        };
        _ctx.Db.AddRange(artifact, source, management);
        _ctx.Db.SaveChanges();

        var agreement = new LeaseAgreement
        {
            PublicId = Guid.NewGuid(),
            PortfolioId = PortfolioId,
            LeaseManagementId = management.Id,
            VersionNumber = 1,
            AgreementNumber = $"AGR-{Guid.NewGuid():N}",
            ChangeType = LeaseAgreementChangeType.Initial,
            TermType = LeaseAgreementTermType.FixedTerm,
            TermStartOn = DateOnly.FromDateTime(now),
            TermEndOn = DateOnly.FromDateTime(now.AddYears(1)),
            GoverningFromOn = DateOnly.FromDateTime(now),
            BaseRentAmount = 1_000m,
            RentDueDay = 1,
            SecurityDepositObligation = 1_000m,
            LateFeeAmount = 50m,
            GracePeriodDays = 5,
            Currency = "USD",
            TermsSchemaVersion = 1,
            TermsPayload = "{}",
            DocumentSourceVersionId = source.Id,
            IssuedArtifactId = artifact.Id,
            IssuedAtUtc = now,
            CreatedAtUtc = now,
            CreatedByUserId = user.Id,
            UpdatedAtUtc = now,
        };
        var party = new LeaseManagementParty
        {
            PortfolioId = PortfolioId,
            LeaseManagementId = management.Id,
            TenantId = tenant.Id,
            Role = LeaseManagementPartyRole.PrimaryTenant,
            EffectiveFrom = DateOnly.FromDateTime(now),
            ChangeReason = "Document target test",
            CreatedAtUtc = now,
            CreatedByUserId = user.Id,
        };
        var account = new TenantAccount
        {
            PublicId = Guid.NewGuid(),
            PortfolioId = PortfolioId,
            LeaseManagementId = management.Id,
            AccountNumber = $"TA-{Guid.NewGuid():N}",
            Currency = "USD",
            OpenedAtUtc = now,
            CreatedAtUtc = now,
            CreatedByUserId = user.Id,
        };
        _ctx.Db.AddRange(agreement, party, account);
        _ctx.Db.SaveChanges();

        var ledgerEntry = new TenantLedgerEntry
        {
            PublicId = Guid.NewGuid(),
            PortfolioId = PortfolioId,
            TenantAccountId = account.Id,
            EntryType = TenantLedgerEntryType.OpeningBalance,
            Direction = TenantLedgerDirection.Debit,
            Amount = 100m,
            Currency = "USD",
            EffectiveOn = DateOnly.FromDateTime(now),
            PostedAtUtc = now,
            Description = "Document target opening balance",
            BusinessKey = $"documents-test:{Guid.NewGuid():N}",
            CreatedByUserId = user.Id,
        };
        var depositAccount = new SecurityDepositAccount
        {
            PortfolioId = PortfolioId,
            TenantAccountId = account.Id,
            OriginatingAgreementId = agreement.Id,
            Currency = "USD",
            CreatedAtUtc = now,
            CreatedByUserId = user.Id,
        };
        _ctx.Db.AddRange(ledgerEntry, depositAccount);
        _ctx.Db.SaveChanges();

        return new CanonicalLeaseDocumentTargets(
            agreement, artifact, account, ledgerEntry, depositAccount);
    }

    private long SeedVendorTarget(Property property, Unit unit, Tenant tenant, DateTime now)
    {
        var vendor = AddAndSave(new Vendor
        {
            PortfolioId = PortfolioId,
            Name = "Document Target Vendor",
            ServiceType = "General",
            CreatedAt = now,
            UpdatedAt = now,
        });
        AddAndSave(new WorkOrder
        {
            PortfolioId = PortfolioId,
            PropertyId = property.Id,
            UnitId = unit.Id,
            TenantId = tenant.Id,
            VendorId = vendor.Id,
            Title = "Vendor document relationship",
            Description = "Vendor document relationship",
            RequestedAt = now,
            UpdatedAt = now,
        });
        return vendor.Id;
    }

    private (StoredFile StoredFile, DocumentTemplate Template, int PropertyId) SeedDocumentTemplateSource()
    {
        var now = DateTime.UtcNow;
        var property = SeedProperty("Template source property");
        var storedFile = new StoredFile
        {
            PortfolioId = PortfolioId,
            EntityType = "DocumentTemplate",
            FileName = "lease-template-source.pdf",
            FilePath = "stored/lease-template-source.pdf",
            ContentType = "application/pdf",
            FileSize = 17,
            UploadedAt = now,
        };
        _ctx.Db.StoredFiles.Add(storedFile);
        _ctx.Db.SaveChanges();

        var template = new DocumentTemplate
        {
            PortfolioId = PortfolioId,
            PropertyId = property.Id,
            Kind = DocumentTemplateKind.Lease,
            Status = DocumentTemplateStatus.Draft,
            RenderMode = DocumentTemplateRenderMode.Overlay,
            Name = "Lease template source",
            OriginalStoredFileId = storedFile.Id,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
        };
        _ctx.Db.DocumentTemplates.Add(template);
        _ctx.Db.SaveChanges();

        storedFile.EntityId = template.Id;
        _ctx.Db.SaveChanges();
        return (storedFile, template, property.Id);
    }

    private Property SeedProperty(string name)
    {
        var now = DateTime.UtcNow;
        var owner = new OwnerEntity
        {
            PortfolioId = PortfolioId,
            Name = $"Owner {name}",
            OwnerEntityType = OwnerEntityType.Person,
            CreatedAt = now,
            UpdatedAt = now,
        };
        var property = new Property
        {
            PortfolioId = PortfolioId,
            Name = name,
            AddressLine1 = "200 Template",
            City = "Columbus",
            State = "OH",
            PostalCode = "43215",
            CreatedAt = now,
            UpdatedAt = now,
            Ownerships =
            [
                new PropertyOwnership
                {
                    PortfolioId = PortfolioId,
                    OwnerEntity = owner,
                    OwnershipSharePercent = 100m,
                    EffectiveFromUtc = now,
                    StatementRecipientName = owner.Name,
                    PayeeName = owner.Name,
                },
            ],
        };
        _ctx.Db.AddRange(owner, property);
        _ctx.Db.SaveChanges();
        return property;
    }

    private void RestrictManagementAssignmentToSelectedProperty(string roleProfileKey, int propertyId)
    {
        var assignment = _ctx.Db.MembershipRoleAssignments.Single();
        assignment.RoleProfileId = AccessCatalog.Roles.Single(role => role.Key == roleProfileKey).Id;
        assignment.ScopeKind = MembershipRoleAssignmentScopeKind.SelectedProperties;
        assignment.UpdatedAtUtc = DateTime.UtcNow;
        _ctx.Db.MembershipRoleAssignmentProperties.Add(new MembershipRoleAssignmentProperty
        {
            MembershipRoleAssignmentId = assignment.Id,
            PortfolioId = PortfolioId,
            PropertyId = propertyId,
        });
        _ctx.Db.SaveChanges();
    }

    private sealed record CanonicalLeaseDocumentTargets(
        LeaseAgreement Agreement,
        LegalDocumentArtifact Artifact,
        TenantAccount Account,
        TenantLedgerEntry LedgerEntry,
        SecurityDepositAccount DepositAccount);

    private T AddAndSave<T>(T entity) where T : class
    {
        _ctx.Db.Add(entity);
        _ctx.Db.SaveChanges();
        return entity;
    }
}
