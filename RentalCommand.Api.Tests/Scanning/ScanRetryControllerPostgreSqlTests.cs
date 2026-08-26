using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using RentalCommand.Api.Auth;
using RentalCommand.Api.Controllers;
using RentalCommand.Api.DTOs;
using RentalCommand.Api.Tests;
using RentalCommand.Api.Tests.Domain;
using RentalCommand.Api.Writes;
using RentalCommand.Core.Atomic;
using RentalCommand.Core.Authorization;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Interfaces;
using RentalCommand.Core.Scanning;
using RentalCommand.Data;
using RentalCommand.Data.Authorization;
using RentalCommand.TestCommon;

namespace RentalCommand.Api.Tests.Scanning;

[Collection(MigratedPostgreSqlCollection.Name2)]
public sealed class ScanRetryControllerPostgreSqlTests : IAsyncLifetime
{
    private const int PortfolioId = 42;
    private static readonly Guid SessionId =
        Guid.Parse("22222222-2222-2222-2222-222222222222");

    private readonly MigratedPostgreSqlFixture _fixture;
    private MigratedPostgreSqlTestContext _ctx = null!;
    private RentalCommandDbContext _db = null!;
    private ServiceProvider _services = null!;
    private AsyncServiceScope _serviceScope;
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
        _serviceScope = _services.CreateAsyncScope();
        SeedPortfolio(PortfolioId);
        _authorization = CanonicalScanAuthorizationTestData.SeedWorkspaceAdministrator(
            _db, PortfolioId, userId: 7, sessionId: SessionId);
    }

    public async Task DisposeAsync()
    {
        await _serviceScope.DisposeAsync();
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

    [Fact]
    public async Task SetPaymentAccount_ReviewingPaymentDraft_PersistsCaptureContextForReopen()
    {
        var account = SeedTenantAccount("ys038-account");
        var batch = SeedBatch(PortfolioId, fileCount: 1);
        var draft = SeedDraft(
            batch.Id,
            "Reviewing",
            targetEntityType: "Payment",
            extractedFields: """{"total":{"value":"1250.00","confidence":0.98}}""");
        draft.CaptureTenantAccountId.Should().BeNull();
        await _ctx.ActivateApiScopeAsync(_authorization.Scope);
        var now = DateTime.UtcNow;
        (await _db.ScanDrafts.AsNoTracking()
            .WhereAuthorized(_db, _authorization.Scope, now)
                .AnyAsync(candidate => candidate.Id == draft.Id))
            .Should().BeTrue();
        var writeDb = _serviceScope.ServiceProvider.GetRequiredService<RentalCommandDbContext>();
        await ActivateApiScopeAsync(writeDb, _authorization.Scope);
        var controller = CreateController();

        var result = await controller.SetPaymentAccount(
            draft.Id,
            new SetScanDraftPaymentAccountRequest
            {
                TenantAccountId = account.Id,
                ClientOperationId = $"ys038-payment-account-{draft.Id}-{Guid.NewGuid():N}",
            },
            CancellationToken.None);

        var ok = result.Result.Should().BeOfType<OkObjectResult>().Subject;
        var response = ok.Value.Should().BeOfType<ScanDraftResponse>().Subject;
        response.CaptureContext.Should().NotBeNull();
        response.CaptureContext!.TenantAccountId.Should().Be(account.Id);

        _db.ChangeTracker.Clear();
        var reloaded = await _db.ScanDrafts.SingleAsync(d => d.Id == draft.Id);
        reloaded.CaptureTenantAccountId.Should().Be(account.Id);
        reloaded.Status.Should().Be("Reviewing");

        var reopenedResult = await controller.Get(draft.Id, CancellationToken.None);
        var reopenedOk = reopenedResult.Result.Should().BeOfType<OkObjectResult>().Subject;
        var reopened = reopenedOk.Value.Should().BeOfType<ScanDraftResponse>().Subject;
        reopened.CaptureContext.Should().NotBeNull();
        reopened.CaptureContext!.TenantAccountId.Should().Be(account.Id);
    }

    [Fact]
    public async Task GlobalScanCreate_UsesRealSecurityClock_WhenBusinessClockIsFuture()
    {
        var realSecurityNow = DateTime.UtcNow;
        await SetAuthSessionExpiresAtAsync(realSecurityNow.AddDays(30));
        var simulatedBusinessNow = realSecurityNow.AddMonths(6);

        var authorized = await ScanDraftAuthorizationQuery.CanCreateGlobalDraftAsync(
            _db,
            _authorization.Scope,
            "Expense",
            simulatedBusinessNow,
            securityUtcNow: realSecurityNow);

        authorized.Should().BeTrue();
    }

    [Fact]
    public async Task GlobalScanCreate_DeniesActuallyExpiredSession_WhenBusinessClockIsFuture()
    {
        var realSecurityNow = DateTime.UtcNow;
        await SetAuthSessionExpiresAtAsync(realSecurityNow.AddSeconds(-1));
        var simulatedBusinessNow = realSecurityNow.AddMonths(6);

        var authorized = await ScanDraftAuthorizationQuery.CanCreateGlobalDraftAsync(
            _db,
            _authorization.Scope,
            "Expense",
            simulatedBusinessNow,
            securityUtcNow: realSecurityNow);

        authorized.Should().BeFalse();
    }

    [Fact]
    public async Task LeaseEndingNotice_ContextualCreateAndReview_UseCanonicalRentalsAuthorization()
    {
        var account = SeedTenantAccount("lease-ending-notice");
        var relationship = await _db.LeaseManagements.AsNoTracking()
            .SingleAsync(candidate => candidate.Id == account.LeaseManagementId);
        var businessNow = new DateTime(2027, 1, 14, 12, 0, 0, DateTimeKind.Utc);
        await _ctx.ActivateApiScopeAsync(_authorization.Scope);

        var canCreate = await ScanDraftAuthorizationQuery.CanCreateDraftAsync(
            _db,
            _authorization.Scope,
            "LeaseEndingNotice",
            relationship.PropertyId,
            businessNow,
            securityUtcNow: DateTime.UtcNow);

        canCreate.Should().BeTrue();
        ScanDraftAuthorizationQuery.CapabilitiesForTarget("LeaseEndingNotice")
            .Should().Equal(CapabilityKeys.RentalsManage);

        var batch = SeedBatch(PortfolioId, fileCount: 1);
        var draft = SeedDraft(batch.Id, "Reviewing", targetEntityType: "LeaseEndingNotice");
        draft.CapturePropertyId = relationship.PropertyId;
        draft.CaptureUnitId = relationship.UnitId;
        draft.CaptureLeaseManagementId = relationship.Id;
        await _db.SaveChangesAsync();
        _db.ChangeTracker.Clear();

        var authorizedDrafts = _db.ScanDrafts.AsNoTracking()
            .WhereAuthorized(
                _db,
                _authorization.Scope,
                businessNow,
                DateTime.UtcNow);

        authorizedDrafts.ToQueryString().Should().Contain("UNION ALL");
        (await authorizedDrafts.AnyAsync(candidate => candidate.Id == draft.Id))
            .Should().BeTrue();
    }

    private ScanController CreateController()
    {
        var controller = new ScanController(
            Mock.Of<IScanService>(),
            Mock.Of<IScanUploadService>(),
            _serviceScope.ServiceProvider.GetRequiredService<IRequestWriteExecutor>(),
            Mock.Of<IScanConfirmationTargetWriter>(),
            _serviceScope.ServiceProvider.GetRequiredService<RentalCommandDbContext>(),
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
        string targetEntityType = "LeaseAgreement",
        string? extractedFields = null,
        string? failureReason = null)
    {
        var draft = new ScanDraft
        {
            PortfolioId = portfolioId,
            BatchId = batchId,
            TargetEntityType = targetEntityType,
            Status = status,
            ExtractedFields = extractedFields,
            FailureReason = failureReason,
            CreatedAt = DateTime.UtcNow,
        };
        _db.ScanDrafts.Add(draft);
        _db.SaveChanges();
        return draft;
    }

    private TenantAccount SeedTenantAccount(string suffix)
    {
        var seededAt = new DateTime(2027, 1, 5, 12, 0, 0, DateTimeKind.Utc);
        var property = new Property
        {
            PortfolioId = PortfolioId,
            Name = $"YS038 Property {suffix}",
            AddressLine1 = "100 Review Street",
            City = "Columbus",
            State = "OH",
            PostalCode = "43215",
            CreatedAt = seededAt,
            UpdatedAt = seededAt,
        };
        var unit = new Unit
        {
            PortfolioId = PortfolioId,
            Property = property,
            UnitNumber = "A",
            MarketRent = 1250m,
            CreatedAt = seededAt,
            UpdatedAt = seededAt,
        };
        var tenant = new Tenant
        {
            PortfolioId = PortfolioId,
            FirstName = "Dana",
            LastName = "Garcia",
            CreatedAt = seededAt,
            UpdatedAt = seededAt,
        };
        var relationship = new LeaseManagement
        {
            PortfolioId = PortfolioId,
            Property = property,
            Unit = unit,
            RelationshipNumber = $"LM-{suffix}",
            PossessionGivenAtUtc = seededAt,
            CreatedAtUtc = seededAt,
            UpdatedAtUtc = seededAt,
            CreatedByUserId = _authorization.Scope.UserId,
            RowVersion = Guid.NewGuid(),
        };
        relationship.Parties.Add(new LeaseManagementParty
        {
            PortfolioId = PortfolioId,
            Tenant = tenant,
            Role = LeaseManagementPartyRole.PrimaryTenant,
            EffectiveFrom = new DateOnly(2027, 1, 1),
            ChangeReason = "Test setup",
            CreatedAtUtc = seededAt,
            CreatedByUserId = _authorization.Scope.UserId,
        });
        var account = new TenantAccount
        {
            PortfolioId = PortfolioId,
            LeaseManagement = relationship,
            AccountNumber = $"TA-{suffix}",
            Currency = "USD",
            OpenedAtUtc = seededAt,
            CreatedAtUtc = seededAt,
            CreatedByUserId = _authorization.Scope.UserId,
        };
        _db.AddRange(property, unit, tenant, relationship, account);
        _db.SaveChanges();
        _db.ChangeTracker.Clear();
        return account;
    }

    private async Task SetAuthSessionExpiresAtAsync(DateTime expiresAtUtc)
    {
        var session = await _db.AuthSessions.SingleAsync(candidate => candidate.Id == SessionId);
        if (session.CreatedAtUtc >= expiresAtUtc)
        {
            session.CreatedAtUtc = expiresAtUtc.AddMinutes(-1);
            session.LastSeenAtUtc = session.CreatedAtUtc;
        }
        session.ExpiresAtUtc = expiresAtUtc;
        await _db.SaveChangesAsync();
        _db.ChangeTracker.Clear();
    }

    private static async Task ActivateApiScopeAsync(
        RentalCommandDbContext db,
        WorkspaceReadScope scope)
    {
        await db.Database.OpenConnectionAsync();
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            SET SESSION AUTHORIZATION rentalcommand_api;
            SELECT set_config('app.current_portfolio_id', {scope.PortfolioId.ToString()}, false),
                   set_config('app.auth_session_id', {scope.SessionId.ToString()}, false),
                   set_config('app.current_user_id', {scope.UserId.ToString()}, false),
                   set_config('app.current_access_context_id', {scope.AccessContextId.ToString()}, false),
                   set_config('app.access_revision', {scope.AccessRevision.ToString()}, false);
            """);
    }
}
