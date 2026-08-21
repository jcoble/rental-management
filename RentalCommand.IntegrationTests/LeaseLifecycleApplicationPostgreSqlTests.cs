using System.Collections.Concurrent;
using System.Data.Common;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using RentalCommand.Api.DTOs;
using RentalCommand.Api.Services.Domain;
using RentalCommand.Core.Atomic;
using RentalCommand.Core.Authorization;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Interfaces;
using RentalCommand.Core.Leasing;
using RentalCommand.Data;
using RentalCommand.Data.Atomic;
using RentalCommand.Data.Leasing;
using RentalCommand.TestCommon;
using Xunit.Abstractions;

namespace RentalCommand.IntegrationTests;

[Collection(RoleAuthorityPostgreSqlCollection.Name)]
public sealed class LeaseLifecycleApplicationPostgreSqlTests : IAsyncLifetime
{
    private static readonly AtomicJsonResultCodec<TransferLeaseManagementResult> TransferCodec =
        new("lease-management.transfer-unit.v1");
    private static readonly AtomicJsonResultCodec<CloseTenantAccountResult> CloseCodec =
        new("tenant-account.close.v1");

    private readonly MigratedPostgreSqlFixture _fixture;
    private readonly ITestOutputHelper _output;
    private readonly SqlCaptureInterceptor _sqlCapture = new();
    private readonly AtomicAuditFailureInterceptor _failure = new();
    private MigratedPostgreSqlTestContext _context = null!;
    private ServiceProvider _services = null!;
    private IServiceScope _serviceScope = null!;

    public LeaseLifecycleApplicationPostgreSqlTests(
        MigratedPostgreSqlFixture fixture,
        ITestOutputHelper output)
    {
        _fixture = fixture;
        _output = output;
    }

    public async Task InitializeAsync()
    {
        _context = await _fixture.CreateContextAsync([_sqlCapture]);
        var services = new ServiceCollection();
        services.AddSingleton(TimeProvider.System);
        services.AddScoped<ICurrentActor, TestActor>();
        services.AddSingleton(_sqlCapture);
        services.AddSingleton(_failure);
        services.AddAtomicPersistenceKernel();
        services.AddAtomicCommandHandler<TransferLeaseManagementCommand,
            TransferLeaseManagementResult, TransferLeaseManagementHandler>();
        services.AddAtomicCommandHandler<CloseTenantAccountCommand,
            CloseTenantAccountResult, CloseTenantAccountHandler>();
        services.AddDbContext<RentalCommandDbContext>((provider, builder) =>
            builder.UseNpgsql(_context.ConnectionString)
                .AddInterceptors(provider.GetRequiredService<SqlCaptureInterceptor>())
                .AddInterceptors(provider.GetRequiredService<AtomicAuditFailureInterceptor>())
                .UseAtomicPersistenceKernel(provider));
        _services = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true,
        });
        _serviceScope = _services.CreateScope();
    }

    public async Task DisposeAsync()
    {
        _serviceScope?.Dispose();
        if (_services is not null) await _services.DisposeAsync();
        if (_context is not null) await _context.DisposeAsync();
    }

    [Fact]
    public async Task TransferAndClose_StayAtomicInPostgreSql()
    {
        var scenario = await SeedLifecycleScenarioAsync();

        var blockedClose = new CloseTenantAccountCommand(
            scenario.PortfolioId,
            scenario.Primary.SourceLeaseManagementId,
            scenario.Primary.SourceTenantAccountId,
            "TOO_EARLY",
            null,
            scenario.ActorUserId,
            scenario.SessionId,
            scenario.AccessContextId,
            scenario.AccessRevision,
            "tenant-account-close:before-possession-return");
        var blockedCloseOutcome = await Atomic.ExecuteAsync(
            new AtomicCommandIdentity(
                "tenant-account.close",
                $"{scenario.PortfolioId}:{scenario.Primary.SourceTenantAccountId}:blocked"),
            blockedClose,
            CloseCodec);
        blockedCloseOutcome.Value.Outcome.Should()
            .Be(CloseTenantAccountOutcome.PossessionNotReturned);
        _context.Db.ChangeTracker.Clear();
        (await _context.Db.TenantAccounts.AsNoTracking()
            .Where(row => row.Id == scenario.Primary.SourceTenantAccountId)
            .Select(row => row.ClosedAtUtc)
            .SingleAsync()).Should().BeNull();

        var denied = TransferCommand(
            scenario.Primary,
            scenario.CrossScopeDestinationUnitId,
            scenario,
            "cross-scope-denied");
        var beforeDenied = await LifecycleGraphCountsAsync();
        await FluentActions.Invoking(() => Atomic.ExecuteAsync(
                TransferIdentity(denied), denied, TransferCodec))
            .Should().ThrowAsync<UnauthorizedAccessException>();
        (await LifecycleGraphCountsAsync()).Should().BeEquivalentTo(beforeDenied);

        var rollback = TransferCommand(
            scenario.Rollback,
            scenario.Rollback.DestinationUnitId,
            scenario,
            "injected-rollback");
        var beforeRollback = await LifecycleGraphCountsAsync();
        _failure.Arm();
        var rollbackFailure = await FluentActions.Invoking(() => Atomic.ExecuteAsync(
                TransferIdentity(rollback), rollback, TransferCodec))
            .Should().ThrowAsync<DbUpdateException>();
        rollbackFailure.WithInnerException<InjectedAtomicAuditFailure>();
        _context.Db.ChangeTracker.Clear();
        (await LifecycleGraphCountsAsync()).Should().BeEquivalentTo(beforeRollback);
        (await _context.Db.LeaseManagements.AsNoTracking()
            .Where(row => row.Id == scenario.Rollback.SourceLeaseManagementId)
            .Select(row => row.PossessionReturnedAtUtc)
            .SingleAsync()).Should().BeNull();
        (await _context.Db.LeaseManagementParties.AsNoTracking()
            .Where(row => row.LeaseManagementId == scenario.Rollback.SourceLeaseManagementId)
            .Select(row => row.EffectiveThrough)
            .SingleAsync()).Should().BeNull();
        (await _context.Db.LeaseManagements.AsNoTracking()
            .CountAsync(row => row.TransferredFromLeaseManagementId ==
                scenario.Rollback.SourceLeaseManagementId)).Should().Be(0);
        (await _context.Db.AtomicCommandReceipts.AsNoTracking()
            .CountAsync(row => row.CommandType == TransferIdentity(rollback).CommandType
                && row.IdempotencyKey == TransferIdentity(rollback).IdempotencyKey)).Should().Be(0);

        var transfer = TransferCommand(
            scenario.Primary,
            scenario.Primary.DestinationUnitId,
            scenario,
            "successful-transfer");
        var identity = TransferIdentity(transfer);
        var first = await Atomic.ExecuteAsync(identity, transfer, TransferCodec);
        var replay = await Atomic.ExecuteAsync(identity, transfer, TransferCodec);

        first.Disposition.Should().Be(AtomicCommandDisposition.Executed);
        replay.Disposition.Should().Be(AtomicCommandDisposition.Replayed);
        replay.Value.Should().BeEquivalentTo(first.Value);
        first.Value.Outcome.Should().Be(TransferLeaseManagementOutcome.Transferred);
        first.Value.DestinationLeaseManagementId.Should().BePositive();
        first.Value.DestinationTenantAccountId.Should().BePositive();
        first.Value.DestinationAgreementId.Should().BePositive();
        first.Value.TurnoverPeriodId.Should().BePositive();

        _context.Db.ChangeTracker.Clear();
        var destination = await _context.Db.LeaseManagements.AsNoTracking()
            .Where(row => row.Id == first.Value.DestinationLeaseManagementId)
            .Select(row => new
            {
                row.TransferredFromLeaseManagementId,
                row.UnitId,
            })
            .SingleAsync();
        destination.TransferredFromLeaseManagementId.Should()
            .Be(scenario.Primary.SourceLeaseManagementId);
        destination.UnitId.Should().Be(scenario.Primary.DestinationUnitId);
        (await _context.Db.TenantAccounts.AsNoTracking()
            .CountAsync(row => row.Id == first.Value.DestinationTenantAccountId
                && row.LeaseManagementId == first.Value.DestinationLeaseManagementId)).Should().Be(1);
        (await _context.Db.LeaseAgreements.AsNoTracking()
            .CountAsync(row => row.Id == first.Value.DestinationAgreementId
                && row.LeaseManagementId == first.Value.DestinationLeaseManagementId)).Should().Be(1);
        (await _context.Db.LeaseManagementParties.AsNoTracking()
            .CountAsync(row => row.LeaseManagementId == first.Value.DestinationLeaseManagementId))
            .Should().Be(first.Value.DestinationPartyIds.Count);
        (await _context.Db.LeaseAgreementSigners.AsNoTracking()
            .CountAsync(row => row.LeaseAgreementId == first.Value.DestinationAgreementId))
            .Should().Be(first.Value.DestinationSignerIds.Count);
        (await _context.Db.UnitOperationalPeriods.AsNoTracking()
            .CountAsync(row => row.Id == first.Value.TurnoverPeriodId
                && row.SourceLeaseManagementId == scenario.Primary.SourceLeaseManagementId))
            .Should().Be(1);
        (await _context.Db.AtomicCommandReceipts.AsNoTracking()
            .CountAsync(row => row.CommandType == identity.CommandType
                && row.IdempotencyKey == identity.IdempotencyKey)).Should().Be(1);
        (await _context.Db.OutboxMessages.AsNoTracking()
            .CountAsync(row => row.IdempotencyKey == transfer.DeliveryIdempotencyKey)).Should().Be(1);

        var close = new CloseTenantAccountCommand(
            scenario.PortfolioId,
            scenario.Primary.SourceLeaseManagementId,
            scenario.Primary.SourceTenantAccountId,
            "TRANSFERRED",
            "Closed after atomic Unit transfer.",
            scenario.ActorUserId,
            scenario.SessionId,
            scenario.AccessContextId,
            scenario.AccessRevision,
            "tenant-account-close:after-transfer");
        var closeIdentity = new AtomicCommandIdentity(
            "tenant-account.close",
            $"{scenario.PortfolioId}:{scenario.Primary.SourceTenantAccountId}:after-transfer");
        var closed = await Atomic.ExecuteAsync(closeIdentity, close, CloseCodec);

        closed.Value.Outcome.Should().Be(CloseTenantAccountOutcome.Closed);
        _context.Db.ChangeTracker.Clear();
        var closedState = await _context.Db.LeaseManagements.AsNoTracking()
            .Where(row => row.Id == scenario.Primary.SourceLeaseManagementId)
            .Select(row => new
            {
                RelationshipPossessionReturnedAtUtc = row.PossessionReturnedAtUtc,
                RelationshipClosedAtUtc = row.AccountClosedAtUtc,
                TenantAccountClosedAtUtc = row.TenantAccount!.ClosedAtUtc,
                row.TenantAccount.CloseReasonCode,
            })
            .SingleAsync();
        closedState.RelationshipPossessionReturnedAtUtc.Should().NotBeNull();
        closedState.RelationshipClosedAtUtc.Should().NotBeNull();
        closedState.TenantAccountClosedAtUtc.Should()
            .Be(closedState.RelationshipClosedAtUtc);
        closedState.CloseReasonCode.Should().Be("TRANSFERRED");
    }

    [Fact]
    public async Task UnitApplications_SearchSortAndPageInPostgreSql()
    {
        var target = await SeedApplicationsAsync();
        var service = new ApplicationService(
            _context.Db,
            Mock.Of<IFileStorage>(),
            Mock.Of<IDataUpdateService>(),
            Mock.Of<IAuditTrailService>(),
            TimeProvider.System,
            Mock.Of<IAtomicUnitOfWork>(),
            Mock.Of<RentalCommand.Api.Writes.IRequestWriteExecutor>());

        _sqlCapture.Clear();
        var page = await service.ListPageAsync(
            target.PortfolioId,
            status: null,
            new ListQuery
            {
                Search = "casey",
                Sort = "lastName",
                Skip = 1,
                Take = 1,
            },
            target.UnitId);

        page.TotalCount.Should().Be(2);
        page.Skip.Should().Be(1);
        page.Take.Should().Be(1);
        page.Items.Should().ContainSingle();
        page.Items[0].LastName.Should().Be("Zulu");
        page.Items[0].UnitId.Should().Be(target.UnitId);
        page.Items[0].PropertyName.Should().Be("Application SQL Property");
        page.Items[0].UnitNumber.Should().Be("19");

        var applicationSql = CaptureSql()
            .Where(sql => sql.Contains("\"RentalApplications\"", StringComparison.Ordinal))
            .ToArray();
        applicationSql.Should().HaveCount(2, "the page is exactly one count and one page statement");
        var countSql = applicationSql.Single(sql =>
            sql.Contains("COUNT", StringComparison.OrdinalIgnoreCase));
        var pageSql = applicationSql.Single(sql =>
            !sql.Contains("COUNT", StringComparison.OrdinalIgnoreCase));

        _output.WriteLine($"L08 APPLICATION COUNT SQL:{Environment.NewLine}{countSql}");
        _output.WriteLine($"L08 APPLICATION PAGE SQL:{Environment.NewLine}{pageSql}");

        countSql.Should().Contain("\"PortfolioId\"");
        countSql.Should().Contain("\"UnitId\"");
        countSql.Should().Contain("ILIKE");
        pageSql.Should().Contain("\"PortfolioId\"");
        pageSql.Should().Contain("\"UnitId\"");
        pageSql.Should().Contain("ILIKE");
        pageSql.Should().Contain("ORDER BY");
        pageSql.Should().Contain("LIMIT");
        pageSql.Should().Contain("OFFSET");
        pageSql.Should().Contain("LEFT JOIN");
        pageSql.Should().Contain("\"PropertyName\"");
        pageSql.Should().Contain("\"UnitNumber\"");
        pageSql.Should().Contain("\"ApprovedTenantId\"");
        pageSql.Should().NotContain("\"CreatedAt\" AS");
        pageSql.Should().NotContain("\"UpdatedAt\" AS");
    }

    private IAtomicUnitOfWork Atomic =>
        _serviceScope.ServiceProvider.GetRequiredService<IAtomicUnitOfWork>();

    private IReadOnlyList<string> CaptureSql() => _sqlCapture.Commands;

    private static AtomicCommandIdentity TransferIdentity(TransferLeaseManagementCommand command) =>
        new("lease-management.transfer-unit",
            $"{command.PortfolioId}:{command.SourceLeaseManagementId}:{command.DeliveryIdempotencyKey}");

    private static TransferLeaseManagementCommand TransferCommand(
        TransferSeed seed,
        int destinationUnitId,
        LifecycleScenario scenario,
        string key) => new(
            scenario.PortfolioId,
            seed.SourceLeaseManagementId,
            seed.SourceUnitId,
            destinationUnitId,
            scenario.ActorUserId,
            scenario.SessionId,
            scenario.AccessContextId,
            scenario.AccessRevision,
            DateTime.UtcNow,
            Guid.NewGuid(),
            scenario.BusinessDate,
            null,
            false,
            null,
            scenario.DocumentTemplateId,
            false,
            false,
            $"Transfer {key}.",
            $"unit-transfer:{key}");

    private async Task<LifecycleScenario> SeedLifecycleScenarioAsync()
    {
        var db = _context.Db;
        var now = DateTime.UtcNow;
        var businessDate = DateOnly.FromDateTime(now);
        var actor = await db.Users.SingleAsync(user => user.Id == 1);
        var property = new Property
        {
            PortfolioId = 1,
            Name = "Lifecycle PostgreSQL Property",
            AddressLine1 = "19 Atomic Way",
            City = "Columbus",
            State = "OH",
            PostalCode = "43215",
            CreatedAt = now,
            UpdatedAt = now,
        };
        var otherPortfolio = new Portfolio
        {
            Name = "Cross Scope Decoy",
            ManagementCompanyName = "Decoy Management",
            TimeZone = "UTC",
            CreatedAt = now,
            UpdatedAt = now,
        };
        db.AddRange(property, otherPortfolio);
        await db.SaveChangesAsync();

        var context = new WorkspaceAccessContext
        {
            UserId = actor.Id,
            PortfolioId = 1,
            Status = WorkspaceAccessContextStatus.Active,
            LastAuthorizedExperience = WorkspaceExperience.Management,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
        };
        var membership = new WorkspaceMembership
        {
            AccessContext = context,
            PortfolioId = 1,
            Status = WorkspaceMembershipStatus.Active,
            DefaultExperience = WorkspaceExperience.Management,
            EffectiveFromUtc = now.AddMinutes(-1),
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
        };
        var assignment = new MembershipRoleAssignment
        {
            WorkspaceMembership = membership,
            PortfolioId = 1,
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
            User = actor,
            ActiveAccessContext = context,
            Status = AuthSessionStatus.Active,
            CreatedAtUtc = now,
            LastSeenAtUtc = now,
            ExpiresAtUtc = now.AddHours(1),
        };
        var template = new DocumentTemplate
        {
            PortfolioId = 1,
            Kind = DocumentTemplateKind.Lease,
            Status = DocumentTemplateStatus.Active,
            RenderMode = DocumentTemplateRenderMode.Restyle,
            Name = "Active transfer template",
            DraftHtml = "<p>Transfer lease</p>",
            Version = 1,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
        };
        db.AddRange(assignment, session, template);
        await db.SaveChangesAsync();

        var primary = await SeedTransferAsync(property, actor, businessDate, now, "primary");
        var rollback = await SeedTransferAsync(property, actor, businessDate, now, "rollback");
        var crossScopeDestination = new Unit
        {
            PortfolioId = otherPortfolio.Id,
            Property = new Property
            {
                PortfolioId = otherPortfolio.Id,
                Name = "Decoy Property",
                AddressLine1 = "99 Decoy Lane",
                City = "Columbus",
                State = "OH",
                PostalCode = "43215",
                CreatedAt = now,
                UpdatedAt = now,
            },
            UnitNumber = "X",
            CreatedAt = now,
            UpdatedAt = now,
        };
        db.Add(crossScopeDestination);
        await db.SaveChangesAsync();

        return new(
            1,
            actor.Id,
            session.Id,
            context.Id,
            context.AccessRevision,
            businessDate,
            template.Id,
            crossScopeDestination.Id,
            primary,
            rollback);
    }

    private async Task<TransferSeed> SeedTransferAsync(
        Property property,
        ApplicationUser actor,
        DateOnly businessDate,
        DateTime now,
        string suffix)
    {
        var db = _context.Db;
        var sourceUnit = Unit(property, $"{suffix}-source", now);
        var destinationUnit = Unit(property, $"{suffix}-destination", now);
        var tenant = new Tenant
        {
            PortfolioId = 1,
            FirstName = "Primary",
            LastName = suffix,
            Email = $"{suffix}@example.test",
            CreatedAt = now,
            UpdatedAt = now,
        };
        db.AddRange(sourceUnit, destinationUnit, tenant);
        await db.SaveChangesAsync();

        var relationship = new LeaseManagement
        {
            PublicId = Guid.NewGuid(),
            PortfolioId = 1,
            PropertyId = property.Id,
            UnitId = sourceUnit.Id,
            RelationshipNumber = $"LM-{suffix}",
            PossessionGivenAtUtc = now.AddDays(-30),
            CreatedAtUtc = now.AddDays(-60),
            UpdatedAtUtc = now,
            CreatedByUserId = actor.Id,
            RowVersion = Guid.NewGuid(),
        };
        var account = new TenantAccount
        {
            PublicId = Guid.NewGuid(),
            PortfolioId = 1,
            LeaseManagement = relationship,
            AccountNumber = $"TA-{suffix}",
            Currency = "USD",
            OpenedAtUtc = now.AddDays(-60),
            CreatedAtUtc = now.AddDays(-60),
            CreatedByUserId = actor.Id,
        };
        db.AddRange(relationship, account);
        await db.SaveChangesAsync();

        var party = new LeaseManagementParty
        {
            PortfolioId = 1,
            LeaseManagementId = relationship.Id,
            TenantId = tenant.Id,
            Role = LeaseManagementPartyRole.PrimaryTenant,
            EffectiveFrom = businessDate.AddDays(-60),
            ChangeReason = "Initial household",
            CreatedAtUtc = now.AddDays(-60),
            CreatedByUserId = actor.Id,
        };
        var sourceVersion = new LegalDocumentSourceVersion
        {
            PublicId = Guid.NewGuid(),
            PortfolioId = 1,
            SourceKind = LegalDocumentSourceKind.BuiltInRenderer,
            BusinessKey = $"source:{suffix}",
            RendererKey = $"test-{suffix}",
            RendererVersion = 1,
            SnapshotPayload = "{}",
            CreatedAtUtc = now.AddDays(-60),
            CreatedByUserId = actor.Id,
        };
        var issuedFile = StoredFile($"issued-{suffix}.pdf", now);
        var executedFile = StoredFile($"executed-{suffix}.pdf", now);
        db.AddRange(party, sourceVersion, issuedFile, executedFile);
        await db.SaveChangesAsync();

        var issuedArtifact = Artifact(
            issuedFile, LegalDocumentArtifactKind.IssuedAgreement, actor.Id, now, suffix);
        var executedArtifact = Artifact(
            executedFile, LegalDocumentArtifactKind.ExecutedAgreement, actor.Id, now, suffix);
        db.AddRange(issuedArtifact, executedArtifact);
        await db.SaveChangesAsync();

        var agreement = new LeaseAgreement
        {
            PublicId = Guid.NewGuid(),
            PortfolioId = 1,
            LeaseManagementId = relationship.Id,
            VersionNumber = 1,
            AgreementNumber = $"AGR-{suffix}",
            ChangeType = LeaseAgreementChangeType.Initial,
            TermType = LeaseAgreementTermType.MonthToMonth,
            TermStartOn = businessDate.AddDays(-60),
            GoverningFromOn = businessDate.AddDays(-60),
            BaseRentAmount = 1000,
            RentDueDay = 1,
            SecurityDepositObligation = 0,
            LateFeeAmount = 0,
            GracePeriodDays = 5,
            Currency = "USD",
            TermsSchemaVersion = 1,
            TermsPayload = "{}",
            DocumentSourceVersionId = sourceVersion.Id,
            CreatedAtUtc = now.AddDays(-60),
            CreatedByUserId = actor.Id,
            UpdatedAtUtc = now,
        };
        var signer = new LeaseAgreementSigner
        {
            PortfolioId = 1,
            LeaseAgreement = agreement,
            LeaseManagementPartyId = party.Id,
            TenantId = tenant.Id,
            SignerRole = LeaseLegalSignerRole.PrimaryTenant,
            NameSnapshot = $"{tenant.FirstName} {tenant.LastName}",
            EmailSnapshot = tenant.Email!,
            SigningOrder = 1,
            IsRequired = true,
        };
        db.AddRange(agreement, signer);
        await db.SaveChangesAsync();

        agreement.IssuedArtifactId = issuedArtifact.Id;
        agreement.IssuedAtUtc = now.AddDays(-59);
        agreement.ExecutedArtifactId = executedArtifact.Id;
        agreement.FullyExecutedAtUtc = now.AddDays(-58);
        await db.SaveChangesAsync();

        return new(
            relationship.Id,
            sourceUnit.Id,
            destinationUnit.Id,
            account.Id);
    }

    private async Task<ApplicationSeed> SeedApplicationsAsync()
    {
        var db = _context.Db;
        var now = DateTime.UtcNow;
        var property = new Property
        {
            PortfolioId = 1,
            Name = "Application SQL Property",
            AddressLine1 = "19 Search Way",
            City = "Columbus",
            State = "OH",
            PostalCode = "43215",
            CreatedAt = now,
            UpdatedAt = now,
        };
        var otherPortfolio = new Portfolio
        {
            Name = "Application Decoy Portfolio",
            ManagementCompanyName = "Application Decoy",
            TimeZone = "UTC",
            CreatedAt = now,
            UpdatedAt = now,
        };
        db.AddRange(property, otherPortfolio);
        await db.SaveChangesAsync();
        var unit = Unit(property, "19", now);
        var otherUnit = Unit(property, "20", now);
        var crossProperty = new Property
        {
            PortfolioId = otherPortfolio.Id,
            Name = "Cross Scope Application Property",
            AddressLine1 = "20 Cross Scope Way",
            City = "Columbus",
            State = "OH",
            PostalCode = "43215",
            CreatedAt = now,
            UpdatedAt = now,
        };
        var crossUnit = Unit(crossProperty, "X", now, otherPortfolio.Id);
        db.AddRange(unit, otherUnit, crossProperty, crossUnit);
        await db.SaveChangesAsync();
        db.RentalApplications.AddRange(
            Application(1, property.Id, unit.Id, "Casey", "Alpha", now.AddMinutes(-3)),
            Application(1, property.Id, unit.Id, "Casey", "Zulu", now.AddMinutes(-2)),
            Application(1, property.Id, otherUnit.Id, "Casey", "OtherUnit", now.AddMinutes(-1)),
            Application(otherPortfolio.Id, crossProperty.Id, crossUnit.Id,
                "Casey", "CrossScope", now));
        await db.SaveChangesAsync();
        return new(1, unit.Id);
    }

    private async Task<GraphCounts> LifecycleGraphCountsAsync()
    {
        _context.Db.ChangeTracker.Clear();
        return new(
            await _context.Db.LeaseManagements.CountAsync(),
            await _context.Db.TenantAccounts.CountAsync(),
            await _context.Db.LeaseAgreements.CountAsync(),
            await _context.Db.LeaseManagementParties.CountAsync(),
            await _context.Db.UnitOperationalPeriods.CountAsync(),
            await _context.Db.AtomicCommandReceipts.CountAsync(),
            await _context.Db.AtomicAuditLogs.CountAsync(),
            await _context.Db.OutboxMessages.CountAsync());
    }

    private static Unit Unit(
        Property property,
        string number,
        DateTime now,
        int portfolioId = 1) => new()
    {
        PortfolioId = portfolioId,
        Property = property,
        UnitNumber = number,
        CreatedAt = now,
        UpdatedAt = now,
    };

    private static StoredFile StoredFile(string name, DateTime now) => new()
    {
        PortfolioId = 1,
        FileName = name,
        FilePath = $"tests/{name}",
        ContentType = "application/pdf",
        FileSize = 10,
        UploadedAt = now,
    };

    private static LegalDocumentArtifact Artifact(
        StoredFile file,
        LegalDocumentArtifactKind kind,
        int actorUserId,
        DateTime now,
        string suffix) => new()
    {
        PublicId = Guid.NewGuid(),
        PortfolioId = 1,
        StoredFileId = file.Id,
        ArtifactKind = kind,
        StorageKey = file.FilePath,
        FileName = file.FileName,
        ContentType = file.ContentType,
        ByteLength = file.FileSize,
        ContentSha256 = new string(kind == LegalDocumentArtifactKind.IssuedAgreement ? 'a' : 'b', 64),
        LegalIssuanceFingerprint = kind == LegalDocumentArtifactKind.IssuedAgreement
            ? new string('c', 64)
            : null,
        CreatedAtUtc = now,
        CreatedByUserId = actorUserId,
    };

    private static RentalApplication Application(
        int portfolioId,
        int propertyId,
        int unitId,
        string firstName,
        string lastName,
        DateTime submittedAt) => new()
    {
        PortfolioId = portfolioId,
        PropertyId = propertyId,
        UnitId = unitId,
        FirstName = firstName,
        LastName = lastName,
        Email = $"{firstName}.{lastName}@example.test".ToLowerInvariant(),
        Status = ApplicationStatus.Submitted,
        SubmittedAtUtc = submittedAt,
        CreatedAt = submittedAt,
        UpdatedAt = submittedAt,
    };

    private sealed class SqlCaptureInterceptor : DbCommandInterceptor
    {
        private readonly ConcurrentQueue<string> _commands = new();
        public IReadOnlyList<string> Commands => _commands.ToArray();

        public void Clear()
        {
            while (_commands.TryDequeue(out _)) { }
        }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            _commands.Enqueue(command.CommandText);
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }
    }

    private sealed class AtomicAuditFailureInterceptor : DbCommandInterceptor
    {
        private int _armed;

        public void Arm() => Interlocked.Exchange(ref _armed, 1);

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            MaybeFail(command.CommandText);
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            MaybeFail(command.CommandText);
            return base.NonQueryExecutingAsync(command, eventData, result, cancellationToken);
        }

        private void MaybeFail(string sql)
        {
            if (Volatile.Read(ref _armed) == 1
                && sql.Contains("INSERT INTO \"AtomicAuditLogs\"", StringComparison.Ordinal)
                && Interlocked.Exchange(ref _armed, 0) == 1)
            {
                throw new InjectedAtomicAuditFailure();
            }
        }
    }

    private sealed class InjectedAtomicAuditFailure : Exception;

    private sealed class TestActor : ICurrentActor
    {
        public int? UserId => 1;
        public string? ActorLabel => "integration:l08-lifecycle-applications";
        public string? IpAddress => "127.0.0.1";
    }

    private sealed record LifecycleScenario(
        int PortfolioId,
        int ActorUserId,
        Guid SessionId,
        int AccessContextId,
        long AccessRevision,
        DateOnly BusinessDate,
        int DocumentTemplateId,
        int CrossScopeDestinationUnitId,
        TransferSeed Primary,
        TransferSeed Rollback);

    private sealed record TransferSeed(
        int SourceLeaseManagementId,
        int SourceUnitId,
        int DestinationUnitId,
        int SourceTenantAccountId);

    private sealed record ApplicationSeed(int PortfolioId, int UnitId);

    private sealed record GraphCounts(
        int LeaseManagements,
        int TenantAccounts,
        int LeaseAgreements,
        int Parties,
        int TurnoverPeriods,
        int Receipts,
        int Audits,
        int OutboxMessages);
}
