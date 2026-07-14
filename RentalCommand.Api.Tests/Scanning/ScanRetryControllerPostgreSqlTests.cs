using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using RentalCommand.Api.Auth;
using RentalCommand.Api.Controllers;
using RentalCommand.Api.Tests;
using RentalCommand.Api.Tests.Domain;
using RentalCommand.Core.Atomic;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Interfaces;
using RentalCommand.Data;
using RentalCommand.TestCommon;

namespace RentalCommand.Api.Tests.Scanning;

[Collection(MigratedPostgreSqlCollection.Name)]
public sealed class ScanRetryControllerPostgreSqlTests : IAsyncLifetime
{
    private const int PortfolioId = 42;
    private static readonly Guid SessionId =
        Guid.Parse("22222222-2222-2222-2222-222222222222");

    private readonly MigratedPostgreSqlFixture _fixture;
    private MigratedPostgreSqlTestContext _ctx = null!;
    private RentalCommandDbContext _db = null!;
    private ServiceProvider _services = null!;
    private CanonicalScanTestAuthorization _authorization = null!;

    public ScanRetryControllerPostgreSqlTests(MigratedPostgreSqlFixture fixture)
    {
        _fixture = fixture;
    }

    public async Task InitializeAsync()
    {
        _ctx = await _fixture.CreateContextAsync();
        _db = _ctx.Db;
        _services = AtomicDomainTestKernel.CreateForScanRetryPostgreSql(_ctx.ConnectionString);
        SeedPortfolio(PortfolioId);
        _authorization = CanonicalScanAuthorizationTestData.SeedWorkspaceAdministrator(
            _db, PortfolioId, userId: 7, sessionId: SessionId);
    }

    public async Task DisposeAsync()
    {
        await _services.DisposeAsync();
        await _ctx.DisposeAsync();
    }

    [Fact]
    public async Task Retry_FailedDraft_RequeuesAndClearsStaleExtractionData()
    {
        var batch = SeedBatch(PortfolioId, fileCount: 1);
        var draft = SeedDraft(batch.Id, "Failed",
            extractedFields: """{"tenant_name":{"value":"Avery Ellis","confidence":0.9}}""",
            failureReason: "extraction interrupted (timeout or shutdown)");
        draft.ModelId = "claude-cli:sonnet";
        draft.TokensUsed = 1234;
        draft.CostUsd = 0.0123m;
        draft.ReviewedAt = DateTime.UtcNow;
        draft.ReviewedBy = "7";
        draft.ConfirmedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();

        var controller = CreateController();

        var result = await controller.Retry(
            draft.Id,
            $"scan-retry:{draft.Id}:test-success",
            CancellationToken.None);

        result.Should().BeOfType<OkResult>();

        _db.ChangeTracker.Clear();
        var reloaded = await _db.ScanDrafts.SingleAsync(d => d.Id == draft.Id);
        reloaded.Status.Should().Be("Pending");
        reloaded.ExtractedFields.Should().BeNull();
        reloaded.FailureReason.Should().BeNull();
        reloaded.ModelId.Should().BeNull();
        reloaded.TokensUsed.Should().BeNull();
        reloaded.CostUsd.Should().BeNull();
        reloaded.ReviewedAt.Should().BeNull();
        reloaded.ReviewedBy.Should().BeNull();
        reloaded.ConfirmedAt.Should().BeNull();
    }

    [Fact]
    public async Task Retry_NonFailedDraft_ReturnsBadRequest()
    {
        var batch = SeedBatch(PortfolioId, fileCount: 1);
        var draft = SeedDraft(batch.Id, "Reviewing");
        var controller = CreateController();

        var result = await controller.Retry(
            draft.Id,
            $"scan-retry:{draft.Id}:test-invalid-status",
            CancellationToken.None);

        result.Should().BeOfType<BadRequestObjectResult>();
        _db.ChangeTracker.Clear();
        (await _db.ScanDrafts.SingleAsync(d => d.Id == draft.Id)).Status.Should().Be("Reviewing");
    }

    [Fact]
    public async Task Retry_CrossPortfolioDraft_ReturnsNotFound()
    {
        const int otherPortfolioId = 99;
        SeedPortfolio(otherPortfolioId);
        var foreignBatch = SeedBatch(otherPortfolioId, fileCount: 1);
        var foreignDraft = SeedDraft(foreignBatch.Id, "Failed", portfolioId: otherPortfolioId);
        var controller = CreateController();

        var result = await controller.Retry(
            foreignDraft.Id,
            $"scan-retry:{foreignDraft.Id}:test-cross-portfolio",
            CancellationToken.None);

        result.Should().BeOfType<NotFoundObjectResult>();
        _db.ChangeTracker.Clear();
        (await _db.ScanDrafts.SingleAsync(d => d.Id == foreignDraft.Id)).Status.Should().Be("Failed");
    }

    private ScanController CreateController()
    {
        var controller = new ScanController(
            Mock.Of<IScanService>(),
            Mock.Of<IScanUploadService>(),
            _services.GetRequiredService<IAtomicUnitOfWork>(),
            _db,
            Mock.Of<IFileStorage>(),
            TimeProvider.System)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext(),
            },
        };
        controller.HttpContext.Items[CanonicalAccessContextHttpItem.Key] =
            _authorization.HttpAccessContext;
        return controller;
    }

    private void SeedPortfolio(int portfolioId)
    {
        _db.Portfolios.Add(new Portfolio
        {
            Id = portfolioId,
            Name = $"Portfolio {portfolioId}",
            ManagementCompanyName = "Test Co",
            TimeZone = "UTC",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        _db.SaveChanges();
    }

    private ScanBatch SeedBatch(int portfolioId, int fileCount)
    {
        var batch = new ScanBatch
        {
            PortfolioId = portfolioId,
            TargetEntityType = "LeaseAgreement",
            Status = ScanBatchStatus.Processing,
            FileCount = fileCount,
            CreatedAtUtc = DateTime.UtcNow,
        };
        _db.ScanBatches.Add(batch);
        _db.SaveChanges();
        return batch;
    }

    private ScanDraft SeedDraft(
        int batchId,
        string status,
        int portfolioId = PortfolioId,
        string? extractedFields = null,
        string? failureReason = null)
    {
        var draft = new ScanDraft
        {
            PortfolioId = portfolioId,
            BatchId = batchId,
            TargetEntityType = "LeaseAgreement",
            Status = status,
            ExtractedFields = extractedFields,
            FailureReason = failureReason,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        _db.ScanDrafts.Add(draft);
        _db.SaveChanges();
        return draft;
    }
}
