using System.Data.Common;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using RentalCommand.Api.Controllers;
using RentalCommand.Api.DTOs;
using RentalCommand.Api.Services.Domain;
using RentalCommand.Api.Tests;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Data;
using RentalCommand.TestCommon;

namespace RentalCommand.Api.Tests.Domain;

public sealed class PortalServiceLeaseTests : IDisposable
{
    private const int PortfolioId = 1;
    private const int AccessContextId = 23;
    private const int TenantId = 41;

    private readonly RentalCommandDbContext _db = NewContext();
    private readonly PortalService _sut;

    public PortalServiceLeaseTests()
    {
        _sut = new PortalService(_db, new NoopLeaseQaService(), TimeProvider.System);
    }

    public void Dispose() => _db.Dispose();

    [Fact]
    public void BuildLeaseRelationshipQuery_IsOneCanonicalAccessScopedStatement()
    {
        var sql = _sut.BuildLeaseRelationshipQuery(
                PortfolioId, AccessContextId, TenantId)
            .ToQueryString();

        sql.Should().Contain("vw_effective_tenant_access");
        sql.Should().Contain("LeaseManagementParties");
        sql.Should().Contain("LeaseManagements");
        sql.Should().Contain("vw_lease_management_lifecycle");
        sql.Should().Contain("LeaseAgreements");
        sql.Should().Contain("vw_lease_agreement_status");
        sql.Should().Contain("LegalDocumentArtifacts");
        sql.Should().Contain("StoredFiles");
        sql.Should().Contain("FullyExecutedAtUtc");
        sql.Should().Contain("VoidedAtUtc");
        sql.Should().Contain("FileName");
        sql.Should().Contain("ContentType");
        sql.Should().Contain("ORDER BY");
        sql.Should().NotContain("FROM \"Leases\"");
        sql.Should().NotContain("LeaseTenants");
    }

    [Fact]
    public void BuildOwnedLeaseManagementQuery_ReturnsCanonicalRelationshipId()
    {
        var sql = _sut.BuildOwnedLeaseManagementQuery(
                PortfolioId, AccessContextId, TenantId, leaseManagementId: 77)
            .ToQueryString();

        sql.Should().Contain("vw_effective_tenant_access");
        sql.Should().Contain("LeaseManagementParties");
        sql.Should().Contain("LeaseManagements");
        sql.Should().Contain("vw_lease_management_lifecycle");
        sql.Should().Contain("CurrentAgreementId");
        sql.Should().NotContain("FROM \"Leases\"");
    }

    [Fact]
    public void PortalContract_UsesRelationshipAndAgreementDtosInsteadOfLegacyLeaseDto()
    {
        var method = typeof(IPortalService).GetMethod(nameof(IPortalService.GetLeasesAsync));

        method.Should().NotBeNull();
        method!.ReturnType.Should().Be(
            typeof(Task<IReadOnlyList<PortalLeaseRelationshipResponse>>));
        typeof(PortalLeaseRelationshipResponse)
            .GetProperty(nameof(PortalLeaseRelationshipResponse.Agreement))!
            .PropertyType.Should().Be(typeof(PortalLeaseAgreementResponse));
        typeof(PortalLeaseAgreementResponse)
            .GetProperty("ExecutedStoredFileId")
            .Should().BeNull("tenant portal DTOs must not expose stored file ids");
        typeof(PortalLeaseAgreementResponse)
            .GetProperty(nameof(PortalLeaseAgreementResponse.ExecutedDocumentAvailable))
            .Should().NotBeNull();
        typeof(PortalLeaseAgreementResponse)
            .GetProperty(nameof(PortalLeaseAgreementResponse.ExecutedDocumentFileName))
            .Should().NotBeNull();
        typeof(PortalLeaseAgreementResponse)
            .GetProperty(nameof(PortalLeaseAgreementResponse.ExecutedDocumentContentType))
            .Should().NotBeNull();
    }

    [Fact]
    public void BuildExecutedAgreementArtifactQuery_IsOneAccessScopedStatement()
    {
        var sql = _sut.BuildExecutedAgreementArtifactQuery(
                new PortalTenantReadScope(PortfolioId, UserId: 9, AccessContextId, AccessRevision: 12),
                leaseManagementId: 77,
                leaseAgreementId: 88)
            .ToQueryString();

        sql.Should().Contain("vw_effective_tenant_access");
        sql.Should().Contain("LeaseAgreements");
        sql.Should().Contain("LegalDocumentArtifacts");
        sql.Should().Contain("StoredFiles");
        sql.Should().Contain("FullyExecutedAtUtc");
        sql.Should().Contain("VoidedAtUtc");
        sql.Should().Contain("ArtifactKind");
        sql.Should().Contain("DeletedAt");
        sql.Should().NotContain("FROM \"Leases\"");
        sql.Should().NotContain("LeaseTenants");
    }

    [Fact]
    public void PortalController_ExecutedAgreementDownload_UsesTenantScopedDirectRoute()
    {
        var method = typeof(PortalController)
            .GetMethod(nameof(PortalController.DownloadExecutedAgreement));

        method.Should().NotBeNull();
        method!.GetCustomAttributes(typeof(HttpGetAttribute), inherit: false)
            .Cast<HttpGetAttribute>()
            .Single()
            .Template.Should().Be(
                "leases/{leaseManagementId:int}/agreements/{leaseAgreementId:int}/executed-document");
    }

    private static RentalCommandDbContext NewContext() =>
        new(new DbContextOptionsBuilder<RentalCommandDbContext>()
            .UseNpgsql("Host=localhost;Database=translation_only;Username=translation_only;Password=translation_only")
            .Options);
}

[Collection(MigratedPostgreSqlCollection.Name)]
public sealed class PortalServiceLeasePostgreSqlTests : IAsyncLifetime
{
    private const int PortfolioId = 1;
    private const int UserId = 1;
    private static readonly DateTime Now = new(2026, 7, 16, 12, 0, 0, DateTimeKind.Utc);
    private static readonly DateOnly BusinessDate = new(2026, 7, 16);

    private readonly MigratedPostgreSqlFixture _fixture;
    private readonly List<string> _commands = [];
    private MigratedPostgreSqlTestContext _ctx = null!;

    public PortalServiceLeasePostgreSqlTests(MigratedPostgreSqlFixture fixture)
    {
        _fixture = fixture;
    }

    public async Task InitializeAsync()
    {
        _ctx = await _fixture.CreateContextAsync([new RecordingCommandInterceptor(_commands)]);
    }

    public async Task DisposeAsync()
    {
        await _ctx.DisposeAsync();
    }

    [Fact]
    public async Task GetLeasesAsync_WhenExecutedAgreementArtifactIsValid_ReturnsAvailableDocumentMetadata()
    {
        var scenario = await SeedLeaseScenarioAsync("valid", ArtifactState.Valid);
        var sut = NewService();
        _commands.Clear();

        var leases = await sut.GetLeasesAsync(
            PortfolioId,
            scenario.Scope.AccessContextId,
            scenario.TenantId);
        var artifact = await sut.GetExecutedAgreementArtifactAsync(
            scenario.Scope,
            scenario.LeaseManagementId,
            scenario.LeaseAgreementId);

        var lease = leases.Should().ContainSingle().Subject;
        lease.LeaseManagementId.Should().Be(scenario.LeaseManagementId);
        lease.Agreement.Should().NotBeNull();
        lease.Agreement!.ExecutedDocumentAvailable.Should().BeTrue();
        lease.Agreement.ExecutedDocumentFileName.Should().Be(scenario.FileName);
        lease.Agreement.ExecutedDocumentContentType.Should().Be(scenario.ContentType);
        artifact.Should().NotBeNull();
        artifact!.FileName.Should().Be(scenario.FileName);
        artifact.ContentType.Should().Be(scenario.ContentType);
        artifact.StorageKey.Should().Be(scenario.StorageKey);

        _commands.Should().HaveCount(2);
        _commands.Should().OnlyContain(sql =>
            sql.Contains("vw_effective_tenant_access", StringComparison.OrdinalIgnoreCase));
        _commands[0].Should().Contain("vw_lease_management_lifecycle");
        _commands[0].Should().Contain("vw_lease_agreement_status");
        _commands[0].Should().Contain("LegalDocumentArtifacts");
        _commands[0].Should().Contain("StoredFiles");
        _commands[1].Should().Contain("LegalDocumentArtifacts");
        _commands[1].Should().Contain("StoredFiles");
    }

    [Fact]
    public async Task GetLeasesAsync_WhenExecutedAgreementArtifactIsNotTenantAvailable_ReturnsNullDocumentMetadata()
    {
        var scenarios = new[]
        {
            await SeedLeaseScenarioAsync("missing-artifact", ArtifactState.MissingArtifact),
            await SeedLeaseScenarioAsync("voided-agreement", ArtifactState.VoidedAgreement),
            await SeedLeaseScenarioAsync("deleted-file", ArtifactState.DeletedStoredFile),
            await SeedLeaseScenarioAsync("issued-only", ArtifactState.IssuedOnlyArtifact),
        };
        var sut = NewService();
        _commands.Clear();

        foreach (var scenario in scenarios)
        {
            var leases = await sut.GetLeasesAsync(
                PortfolioId,
                scenario.Scope.AccessContextId,
                scenario.TenantId);
            var artifact = await sut.GetExecutedAgreementArtifactAsync(
                scenario.Scope,
                scenario.LeaseManagementId,
                scenario.LeaseAgreementId);

            var lease = leases.Should().ContainSingle().Subject;
            (lease.Agreement?.ExecutedDocumentAvailable ?? false).Should().BeFalse();
            lease.Agreement?.ExecutedDocumentFileName.Should().BeNull();
            lease.Agreement?.ExecutedDocumentContentType.Should().BeNull();
            artifact.Should().BeNull();
        }

        _commands.Should().HaveCount(scenarios.Length * 2);
        _commands.Should().OnlyContain(sql =>
            sql.Contains("vw_effective_tenant_access", StringComparison.OrdinalIgnoreCase));
        _commands.Where(sql => sql.Contains("vw_lease_management_lifecycle", StringComparison.Ordinal))
            .Should().HaveCount(scenarios.Length);
        _commands.Where(sql => sql.Contains("LegalDocumentArtifacts", StringComparison.Ordinal))
            .Should().HaveCountGreaterThanOrEqualTo(scenarios.Length);
    }

    [Fact]
    public async Task GetLeasesAsync_WhenLeaseIsOutsideTenantAccessScope_ReturnsNoLeaseOrArtifact()
    {
        var allowed = await SeedLeaseScenarioAsync("allowed", ArtifactState.Valid);
        var forbidden = await SeedLeaseScenarioAsync(
            "forbidden",
            ArtifactState.Valid,
            grantTenantAccess: false);
        var sut = NewService();
        _commands.Clear();

        var leases = await sut.GetLeasesAsync(
            PortfolioId,
            allowed.Scope.AccessContextId,
            forbidden.TenantId);
        var artifact = await sut.GetExecutedAgreementArtifactAsync(
            allowed.Scope,
            forbidden.LeaseManagementId,
            forbidden.LeaseAgreementId);

        leases.Should().BeEmpty();
        artifact.Should().BeNull();
        _commands.Should().HaveCount(2);
        _commands.Should().OnlyContain(sql =>
            sql.Contains("vw_effective_tenant_access", StringComparison.OrdinalIgnoreCase));
        _commands[0].Should().Contain("LeaseManagementParties");
        _commands[0].Should().Contain("LeaseManagements");
        _commands[1].Should().Contain("LegalDocumentArtifacts");
        _commands[1].Should().Contain("StoredFiles");
    }

    private PortalService NewService() =>
        new(_ctx.Db, new NoopLeaseQaService(), TimeProvider.System);

    private async Task<LeaseScenario> SeedLeaseScenarioAsync(
        string suffix,
        ArtifactState artifactState,
        bool grantTenantAccess = true)
    {
        var accessContext = await GetOrCreateTenantAccessContextAsync();
        var property = new Property
        {
            PortfolioId = PortfolioId,
            Name = $"Lease property {suffix}",
            AddressLine1 = $"1 {suffix} Way",
            City = "Columbus",
            State = "OH",
            PostalCode = "43215",
            CreatedAt = Now,
            UpdatedAt = Now,
        };
        var tenant = new Tenant
        {
            PortfolioId = PortfolioId,
            FirstName = "Portal",
            LastName = $"Tenant {suffix}",
            Email = $"tenant-{suffix}@example.test",
            CreatedAt = Now,
            UpdatedAt = Now,
        };
        _ctx.Db.AddRange(property, tenant);
        await _ctx.Db.SaveChangesAsync();

        var unit = new Unit
        {
            PortfolioId = PortfolioId,
            PropertyId = property.Id,
            UnitNumber = suffix,
            CreatedAt = Now,
            UpdatedAt = Now,
        };
        _ctx.Db.Units.Add(unit);
        await _ctx.Db.SaveChangesAsync();

        var management = new LeaseManagement
        {
            PublicId = Guid.NewGuid(),
            PortfolioId = PortfolioId,
            PropertyId = property.Id,
            UnitId = unit.Id,
            RelationshipNumber = $"LM-{suffix}",
            PossessionGivenAtUtc = Now.AddDays(-30),
            CreatedAtUtc = Now,
            CreatedByUserId = UserId,
            UpdatedAtUtc = Now,
            RowVersion = Guid.NewGuid(),
        };
        _ctx.Db.LeaseManagements.Add(management);
        await _ctx.Db.SaveChangesAsync();

        var account = new TenantAccount
        {
            PublicId = Guid.NewGuid(),
            PortfolioId = PortfolioId,
            LeaseManagementId = management.Id,
            AccountNumber = $"TA-{suffix}",
            Currency = "USD",
            OpenedAtUtc = Now.AddDays(-30),
            CreatedAtUtc = Now,
            CreatedByUserId = UserId,
        };
        var party = new LeaseManagementParty
        {
            PortfolioId = PortfolioId,
            LeaseManagementId = management.Id,
            TenantId = tenant.Id,
            Role = LeaseManagementPartyRole.PrimaryTenant,
            EffectiveFrom = BusinessDate.AddDays(-30),
            ChangeReason = $"portal lease proof {suffix}",
            CreatedAtUtc = Now,
            CreatedByUserId = UserId,
        };
        _ctx.Db.AddRange(account, party);
        await _ctx.Db.SaveChangesAsync();

        var agreement = new LeaseAgreement
        {
            PublicId = Guid.NewGuid(),
            PortfolioId = PortfolioId,
            LeaseManagementId = management.Id,
            VersionNumber = 1,
            AgreementNumber = $"AGR-{suffix}",
            ChangeType = LeaseAgreementChangeType.Initial,
            TermType = LeaseAgreementTermType.FixedTerm,
            TermStartOn = BusinessDate.AddDays(-30),
            TermEndOn = BusinessDate.AddYears(1),
            GoverningFromOn = BusinessDate.AddDays(-30),
            BaseRentAmount = 1200m,
            RentDueDay = 1,
            SecurityDepositObligation = 1200m,
            LateFeeAmount = 50m,
            GracePeriodDays = 5,
            Currency = "USD",
            TermsSchemaVersion = 1,
            TermsPayload = "{}",
            DocumentSourceVersion = LegalDocumentSourceVersionTestData.BuiltIn(
                PortfolioId,
                UserId,
                Now),
            CreatedAtUtc = Now,
            CreatedByUserId = UserId,
            UpdatedAtUtc = Now,
        };
        _ctx.Db.LeaseAgreements.Add(agreement);
        await _ctx.Db.SaveChangesAsync();

        _ctx.Db.LeaseAgreementSigners.Add(new LeaseAgreementSigner
        {
            PortfolioId = PortfolioId,
            LeaseAgreementId = agreement.Id,
            LeaseManagementPartyId = party.Id,
            TenantId = tenant.Id,
            SignerRole = LeaseLegalSignerRole.PrimaryTenant,
            NameSnapshot = $"{tenant.FirstName} {tenant.LastName}",
            EmailSnapshot = tenant.Email,
            SigningOrder = 1,
            IsRequired = true,
        });

        if (grantTenantAccess)
        {
            _ctx.Db.TenantUserAccesses.Add(new TenantUserAccess
            {
                PublicId = Guid.NewGuid(),
                PortfolioId = PortfolioId,
                AccessContextId = accessContext.Id,
                ApplicationUserId = UserId,
                LeaseManagementPartyId = party.Id,
                GrantedAtUtc = Now,
                GrantedByUserId = UserId,
                Reason = $"portal lease proof {suffix}",
            });
        }

        var issuedFile = new StoredFile
        {
            PortfolioId = PortfolioId,
            FileName = $"agreement-{suffix}-issued.pdf",
            FilePath = $"test/agreements/agreement-{suffix}-issued.pdf",
            ContentType = "application/pdf",
            FileSize = 1024,
            UploadedAt = Now,
        };
        _ctx.Db.StoredFiles.Add(issuedFile);
        await _ctx.Db.SaveChangesAsync();

        var issuedArtifact = new LegalDocumentArtifact
        {
            PublicId = Guid.NewGuid(),
            PortfolioId = PortfolioId,
            StoredFileId = issuedFile.Id,
            ArtifactKind = LegalDocumentArtifactKind.IssuedAgreement,
            StorageKey = issuedFile.FilePath,
            FileName = issuedFile.FileName,
            ContentType = issuedFile.ContentType,
            ByteLength = issuedFile.FileSize,
            ContentSha256 = new string('e', 64),
            LegalIssuanceFingerprint = new string('f', 64),
            CreatedAtUtc = Now,
            CreatedByUserId = UserId,
        };
        _ctx.Db.LegalDocumentArtifacts.Add(issuedArtifact);
        await _ctx.Db.SaveChangesAsync();
        agreement.IssuedArtifactId = issuedArtifact.Id;
        agreement.IssuedAtUtc = Now.AddDays(-20);

        var fileName = $"agreement-{suffix}-executed.pdf";
        var contentType = "application/pdf";
        var storageKey = $"test/agreements/{fileName}";

        if (artifactState != ArtifactState.MissingArtifact)
        {
            var storedFile = new StoredFile
            {
                PortfolioId = PortfolioId,
                FileName = fileName,
                FilePath = storageKey,
                ContentType = contentType,
                FileSize = 2048,
                UploadedAt = Now,
                DeletedAt = artifactState == ArtifactState.DeletedStoredFile
                    ? Now.AddMinutes(1)
                    : null,
            };
            _ctx.Db.StoredFiles.Add(storedFile);
            await _ctx.Db.SaveChangesAsync();

            var artifact = new LegalDocumentArtifact
            {
                PublicId = Guid.NewGuid(),
                PortfolioId = PortfolioId,
                StoredFileId = storedFile.Id,
                ArtifactKind = artifactState == ArtifactState.IssuedOnlyArtifact
                    ? LegalDocumentArtifactKind.IssuedAgreement
                    : LegalDocumentArtifactKind.ExecutedAgreement,
                StorageKey = storageKey,
                FileName = fileName,
                ContentType = contentType,
                ByteLength = storedFile.FileSize,
                ContentSha256 = new string('d', 64),
                CreatedAtUtc = Now,
                CreatedByUserId = UserId,
            };
            _ctx.Db.LegalDocumentArtifacts.Add(artifact);
            await _ctx.Db.SaveChangesAsync();
            agreement.ExecutedArtifactId = artifact.Id;
        }

        if (artifactState != ArtifactState.MissingArtifact)
        {
            agreement.FullyExecutedAtUtc = Now.AddDays(-10);
        }

        if (artifactState == ArtifactState.VoidedAgreement)
        {
            agreement.VoidedAtUtc = Now.AddDays(-1);
            agreement.VoidReasonCode = "TEST_VOID";
        }

        await _ctx.Db.SaveChangesAsync();
        _ctx.Db.ChangeTracker.Clear();

        return new LeaseScenario(
            new PortalTenantReadScope(
                PortfolioId,
                UserId,
                accessContext.Id,
                accessContext.AccessRevision),
            tenant.Id,
            management.Id,
            agreement.Id,
            fileName,
            contentType,
            storageKey);
    }

    private async Task<WorkspaceAccessContext> GetOrCreateTenantAccessContextAsync()
    {
        var existing = await _ctx.Db.WorkspaceAccessContexts
            .SingleOrDefaultAsync(context =>
                context.UserId == UserId &&
                context.PortfolioId == PortfolioId);
        if (existing is not null)
        {
            return existing;
        }

        var accessContext = new WorkspaceAccessContext
        {
            UserId = UserId,
            PortfolioId = PortfolioId,
            Status = WorkspaceAccessContextStatus.Active,
            LastAuthorizedExperience = WorkspaceExperience.Tenant,
            CreatedAtUtc = Now,
            UpdatedAtUtc = Now,
        };
        _ctx.Db.WorkspaceAccessContexts.Add(accessContext);
        await _ctx.Db.SaveChangesAsync();
        return accessContext;
    }

    private enum ArtifactState
    {
        Valid,
        MissingArtifact,
        VoidedAgreement,
        DeletedStoredFile,
        IssuedOnlyArtifact,
    }

    private sealed record LeaseScenario(
        PortalTenantReadScope Scope,
        int TenantId,
        int LeaseManagementId,
        int LeaseAgreementId,
        string FileName,
        string ContentType,
        string StorageKey);

    private sealed class RecordingCommandInterceptor(List<string> commands) : DbCommandInterceptor
    {
        public override InterceptionResult<DbDataReader> ReaderExecuting(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result)
        {
            commands.Add(command.CommandText);
            return result;
        }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            commands.Add(command.CommandText);
            return ValueTask.FromResult(result);
        }
    }
}
