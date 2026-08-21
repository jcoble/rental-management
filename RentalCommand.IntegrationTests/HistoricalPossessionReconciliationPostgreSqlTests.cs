using System.Data.Common;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using RentalCommand.Core.Atomic;
using RentalCommand.Core.Auth;
using RentalCommand.Core.Time;
using RentalCommand.Core.Authorization;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Interfaces;
using RentalCommand.Core.Leasing;
using RentalCommand.Data;
using RentalCommand.Data.Atomic;
using RentalCommand.Data.Leasing;
using RentalCommand.TestCommon;

namespace RentalCommand.IntegrationTests;

[Collection(RoleAuthorityPostgreSqlCollection.Name)]
public sealed class HistoricalPossessionReconciliationPostgreSqlTests : IAsyncLifetime
{
    private readonly MigratedPostgreSqlFixture _fixture;
    private readonly OutboxFailureInterceptor _outboxFailure = new();
    private readonly QueryCaptureInterceptor _queries = new();
    private MigratedPostgreSqlTestContext _context = null!;
    private ServiceProvider _services = null!;
    private IServiceScope _serviceScope = null!;

    public HistoricalPossessionReconciliationPostgreSqlTests(MigratedPostgreSqlFixture fixture) =>
        _fixture = fixture;

    public async Task InitializeAsync()
    {
        _context = await _fixture.CreateContextAsync([_outboxFailure, _queries]);
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(TimeProvider.System);
        services.AddScoped<ICurrentActor, TestActor>();
        services.AddSingleton(_outboxFailure);
        services.AddSingleton(_queries);
        services.AddAtomicPersistenceKernel();
        services.AddDbContext<RentalCommandDbContext>((provider, builder) =>
            builder.UseNpgsql(_context.ConnectionString)
                .AddInterceptors(provider.GetRequiredService<OutboxFailureInterceptor>())
                .AddInterceptors(provider.GetRequiredService<QueryCaptureInterceptor>())
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
    public async Task ReconcileHistoricalPossession_FailsClosedForDatesAndAuthorization_ThenCommitsAndReplaysOnce()
    {
        var scenario = await SeedScenarioAsync();
        var validDate = scenario.TermStartOn;

        var future = Command(scenario, scenario.BusinessDate.AddDays(1), "future-date");
        var futureResult = await ExecuteAsync(future);
        futureResult.Value.Outcome.Should().Be(ReconcileHistoricalPossessionOutcome.DateAfterBusinessDate);
        await AssertNoMutationAsync(scenario.LeaseManagementId, future.DeliveryIdempotencyKey);

        var beforeTerm = Command(scenario, scenario.TermStartOn.AddDays(-1), "before-term");
        var beforeTermResult = await ExecuteAsync(beforeTerm);
        beforeTermResult.Value.Outcome.Should()
            .Be(ReconcileHistoricalPossessionOutcome.DateOutsideAgreementTerm);
        await AssertNoMutationAsync(scenario.LeaseManagementId, beforeTerm.DeliveryIdempotencyKey);

        var denied = Command(scenario with { AccessRevision = scenario.AccessRevision + 1 }, validDate,
            "stale-access");
        await FluentActions.Invoking(() => ExecuteAsync(denied))
            .Should().ThrowAsync<UnauthorizedAccessException>();
        await AssertNoMutationAsync(scenario.LeaseManagementId, denied.DeliveryIdempotencyKey);

        var rollback = Command(scenario, validDate, "rollback");
        _outboxFailure.FailNextOutboxInsert = true;
        await FluentActions.Invoking(() => ExecuteAsync(rollback))
            .Should().ThrowAsync<DbUpdateException>()
            .Where(exception => exception.InnerException is InjectedOutboxFailure);
        await AssertNoMutationAsync(scenario.LeaseManagementId, rollback.DeliveryIdempotencyKey);

        var reconcile = Command(scenario, validDate, "success");
        var identity = Identity(reconcile);
        var first = await ExecuteAsync(reconcile);
        var replay = await ExecuteAsync(reconcile);

        first.Disposition.Should().Be(AtomicCommandDisposition.Executed);
        replay.Disposition.Should().Be(AtomicCommandDisposition.Replayed);
        replay.Value.Should().BeEquivalentTo(first.Value);
        first.Value.Outcome.Should().Be(ReconcileHistoricalPossessionOutcome.Reconciled);
        first.Value.PossessionGivenAtUtc.Should()
            .Be(validDate.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc));

        _context.Db.ChangeTracker.Clear();
        var persisted = await _context.Db.LeaseManagements.AsNoTracking()
            .Where(row => row.Id == scenario.LeaseManagementId)
            .Select(row => new { row.PossessionGivenAtUtc, row.UpdatedAtUtc })
            .SingleAsync();
        persisted.PossessionGivenAtUtc.Should()
            .Be(validDate.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc));
        (await _context.Db.AtomicAuditLogs.AsNoTracking()
            .CountAsync(row => row.CommandType == identity.CommandType
                && row.CommandIdempotencyKey == identity.IdempotencyKey
                && row.EntityType == nameof(LeaseManagement)
                && row.EntityId == scenario.LeaseManagementId)).Should().Be(1);
        (await _context.Db.OutboxMessages.AsNoTracking()
            .CountAsync(row => row.IdempotencyKey == reconcile.DeliveryIdempotencyKey
                && row.MessageType == "data-update")).Should().Be(1);
        (await _context.Db.AtomicCommandReceipts.AsNoTracking()
            .CountAsync(row => row.CommandType == identity.CommandType
                && row.IdempotencyKey == identity.IdempotencyKey)).Should().Be(1);

        var lifecycle = await _context.Db.LeaseManagementLifecycleProjections.AsNoTracking()
            .Where(row => row.LeaseManagementId == scenario.LeaseManagementId)
            .Select(row => new
            {
                row.Lifecycle,
                row.HasGoverningAgreementWithoutPossession,
                row.HasReconciliationException,
            })
            .SingleAsync();
        lifecycle.Lifecycle.Should().Be("Occupied");
        lifecycle.HasGoverningAgreementWithoutPossession.Should().BeFalse();
        lifecycle.HasReconciliationException.Should().BeFalse();
    }

    [Fact]
    public async Task EligibilityQuery_TranslatesAuthorizationAndExactExceptionToOneSqlStatement()
    {
        var scenario = await SeedScenarioAsync();
        await using var scope = _services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<RentalCommandDbContext>();
        var sql = ReconcileHistoricalPossessionHandler.LoadTarget(
                Command(scenario, scenario.TermStartOn, "sql"),
                db,
                DateTime.UtcNow,
                DateTime.UtcNow)
            .ToQueryString();

        sql.Should().Contain("vw_lease_reconciliation_exceptions");
        sql.Should().Contain("GoverningAgreementWithoutPossession");
        sql.Should().Contain("LeaseAgreements");
        sql.Should().Contain("FullyExecutedAtUtc");
        sql.Should().Contain("WorkspaceAccessContexts");
        sql.Should().Contain("MembershipRoleAssignments");
        sql.Should().Contain("NOT EXISTS");
        sql.Should().NotContain("Enumerable");
    }

    private Task<AtomicCommandOutcome<ReconcileHistoricalPossessionResult>> ExecuteAsync(
        ReconcileHistoricalPossessionCommand command)
    {
        var db = _serviceScope.ServiceProvider.GetRequiredService<RentalCommandDbContext>();
        return _serviceScope.ServiceProvider.GetRequiredService<IWriteExecutor>().ExecuteAsync(
            Identity(command).IdempotencyKey,
            LeasingWriteSupport.Write<ReconcileHistoricalPossessionCommand,
                ReconcileHistoricalPossessionResult>(db, command));
    }

    private static AtomicCommandIdentity Identity(ReconcileHistoricalPossessionCommand command) =>
        new("lease-management.reconcile-historical-possession",
            $"{command.PortfolioId}:{command.LeaseManagementId}:{command.DeliveryIdempotencyKey}");

    private static ReconcileHistoricalPossessionCommand Command(
        Scenario scenario,
        DateOnly possessionGivenOn,
        string key) => new(
            scenario.PortfolioId,
            scenario.LeaseManagementId,
            scenario.UnitId,
            possessionGivenOn,
            scenario.ActorUserId,
            scenario.SessionId,
            scenario.AccessContextId,
            scenario.AccessRevision,
            DateTime.UtcNow,
            $"historical-possession:{key}");

    private async Task AssertNoMutationAsync(int leaseManagementId, string deliveryIdempotencyKey)
    {
        _context.Db.ChangeTracker.Clear();
        (await _context.Db.LeaseManagements.AsNoTracking()
            .Where(row => row.Id == leaseManagementId)
            .Select(row => row.PossessionGivenAtUtc)
            .SingleAsync()).Should().BeNull();
        (await _context.Db.AtomicAuditLogs.AsNoTracking()
            .CountAsync(row => row.EntityType == nameof(LeaseManagement)
                && row.EntityId == leaseManagementId
                && row.CommandIdempotencyKey.Contains(deliveryIdempotencyKey)))
            .Should().Be(0);
        (await _context.Db.OutboxMessages.AsNoTracking()
            .CountAsync(row => row.IdempotencyKey == deliveryIdempotencyKey))
            .Should().Be(0);
    }

    private async Task<Scenario> SeedScenarioAsync()
    {
        var db = _context.Db;
        var now = DateTime.UtcNow;
        var businessDate = DateOnly.FromDateTime(now);
        var termStart = businessDate.AddDays(-30);
        var termEnd = businessDate.AddDays(30);
        var actor = await db.Users.SingleAsync(user => user.Id == 1);
        var property = new Property
        {
            PortfolioId = 1,
            Name = $"Historical Possession {Guid.NewGuid():N}",
            AddressLine1 = "212 Reconcile Way",
            City = "Columbus",
            State = "OH",
            PostalCode = "43215",
            CreatedAt = now,
            UpdatedAt = now,
        };
        var unit = new Unit
        {
            PortfolioId = 1,
            Property = property,
            UnitNumber = $"HP-{Guid.NewGuid():N}"[..10],
            CreatedAt = now,
            UpdatedAt = now,
        };
        var tenant = new Tenant
        {
            PortfolioId = 1,
            FirstName = "Historical",
            LastName = "Resident",
            Email = $"historical-{Guid.NewGuid():N}@example.test",
            CreatedAt = now,
            UpdatedAt = now,
        };
        db.AddRange(property, unit, tenant);
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
        db.AddRange(assignment, session);
        await db.SaveChangesAsync();

        var relationship = new LeaseManagement
        {
            PublicId = Guid.NewGuid(),
            PortfolioId = 1,
            PropertyId = property.Id,
            UnitId = unit.Id,
            RelationshipNumber = $"LM-HP-{Guid.NewGuid():N}"[..18],
            PlannedPossessionAtUtc = termStart.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc),
            CreatedAtUtc = now.AddDays(-40),
            UpdatedAtUtc = now,
            CreatedByUserId = actor.Id,
            RowVersion = Guid.NewGuid(),
        };
        var account = new TenantAccount
        {
            PublicId = Guid.NewGuid(),
            PortfolioId = 1,
            LeaseManagement = relationship,
            AccountNumber = $"TA-HP-{Guid.NewGuid():N}"[..18],
            Currency = "USD",
            OpenedAtUtc = now.AddDays(-40),
            CreatedAtUtc = now.AddDays(-40),
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
            EffectiveFrom = termStart,
            ChangeReason = "Historical possession fixture",
            CreatedAtUtc = now.AddDays(-40),
            CreatedByUserId = actor.Id,
        };
        var source = new LegalDocumentSourceVersion
        {
            PublicId = Guid.NewGuid(),
            PortfolioId = 1,
            SourceKind = LegalDocumentSourceKind.BuiltInRenderer,
            BusinessKey = $"historical-possession:{Guid.NewGuid():N}",
            RendererKey = $"hp-{Guid.NewGuid():N}",
            RendererVersion = 1,
            SnapshotPayload = "{}",
            CreatedAtUtc = now.AddDays(-40),
            CreatedByUserId = actor.Id,
        };
        var issuedFile = StoredFile("issued", now);
        var executedFile = StoredFile("executed", now);
        db.AddRange(party, source, issuedFile, executedFile);
        await db.SaveChangesAsync();

        var issuedArtifact = Artifact(issuedFile, LegalDocumentArtifactKind.IssuedAgreement, actor.Id, now, true);
        var executedArtifact = Artifact(executedFile, LegalDocumentArtifactKind.ExecutedAgreement, actor.Id, now, false);
        db.AddRange(issuedArtifact, executedArtifact);
        await db.SaveChangesAsync();

        var agreement = new LeaseAgreement
        {
            PublicId = Guid.NewGuid(),
            PortfolioId = 1,
            LeaseManagementId = relationship.Id,
            VersionNumber = 1,
            AgreementNumber = $"AGR-HP-{Guid.NewGuid():N}"[..18],
            ChangeType = LeaseAgreementChangeType.Initial,
            TermType = LeaseAgreementTermType.FixedTerm,
            TermStartOn = termStart,
            TermEndOn = termEnd,
            GoverningFromOn = termStart,
            BaseRentAmount = 1000,
            RentDueDay = 1,
            SecurityDepositObligation = 0,
            LateFeeAmount = 0,
            GracePeriodDays = 5,
            Currency = "USD",
            TermsSchemaVersion = 1,
            TermsPayload = "{}",
            DocumentSourceVersionId = source.Id,
            CreatedAtUtc = now.AddDays(-40),
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
        agreement.IssuedAtUtc = now.AddDays(-39);
        agreement.ExecutedArtifactId = executedArtifact.Id;
        agreement.FullyExecutedAtUtc = now.AddDays(-38);
        await db.SaveChangesAsync();

        return new(1, relationship.Id, unit.Id, actor.Id, session.Id, context.Id,
            context.AccessRevision, businessDate, termStart);
    }

    private static StoredFile StoredFile(string suffix, DateTime now) => new()
    {
        PortfolioId = 1,
        FileName = $"{suffix}.pdf",
        FilePath = $"test/{Guid.NewGuid():N}/{suffix}.pdf",
        ContentType = "application/pdf",
        FileSize = 12,
        ContentSha256 = new string('a', 64),
        UploadedAt = now,
    };

    private static LegalDocumentArtifact Artifact(
        StoredFile file,
        LegalDocumentArtifactKind kind,
        int actorId,
        DateTime now,
        bool issued) => new()
    {
        PublicId = Guid.NewGuid(),
        PortfolioId = 1,
        StoredFileId = file.Id,
        ArtifactKind = kind,
        StorageKey = $"test/{Guid.NewGuid():N}/{file.FileName}",
        FileName = file.FileName,
        ContentType = "application/pdf",
        ByteLength = file.FileSize,
        ContentSha256 = issued ? new string('b', 64) : new string('c', 64),
        LegalIssuanceFingerprint = issued ? new string('d', 64) : null,
        CreatedAtUtc = now,
        CreatedByUserId = actorId,
    };

    private sealed record Scenario(
        int PortfolioId,
        int LeaseManagementId,
        int UnitId,
        int ActorUserId,
        Guid SessionId,
        int AccessContextId,
        long AccessRevision,
        DateOnly BusinessDate,
        DateOnly TermStartOn);

    private sealed class OutboxFailureInterceptor : DbCommandInterceptor
    {
        public bool FailNextOutboxInsert { get; set; }

        public override InterceptionResult<int> NonQueryExecuting(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<int> result)
        {
            if (FailNextOutboxInsert
                && command.CommandText.Contains("INSERT INTO \"OutboxMessages\"", StringComparison.Ordinal))
            {
                FailNextOutboxInsert = false;
                throw new InjectedOutboxFailure();
            }

            return result;
        }

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            if (FailNextOutboxInsert
                && command.CommandText.Contains("INSERT INTO \"OutboxMessages\"", StringComparison.Ordinal))
            {
                FailNextOutboxInsert = false;
                throw new InjectedOutboxFailure();
            }

            return ValueTask.FromResult(result);
        }

        public override InterceptionResult<DbDataReader> ReaderExecuting(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result)
        {
            if (FailNextOutboxInsert
                && command.CommandText.Contains("INSERT INTO \"OutboxMessages\"", StringComparison.Ordinal))
            {
                FailNextOutboxInsert = false;
                throw new InjectedOutboxFailure();
            }

            return result;
        }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            if (FailNextOutboxInsert
                && command.CommandText.Contains("INSERT INTO \"OutboxMessages\"", StringComparison.Ordinal))
            {
                FailNextOutboxInsert = false;
                throw new InjectedOutboxFailure();
            }

            return ValueTask.FromResult(result);
        }
    }

    private sealed class QueryCaptureInterceptor : DbCommandInterceptor;

    private sealed class InjectedOutboxFailure : Exception;

    private sealed class TestActor : ICurrentActor
    {
        public int? UserId => 1;
        public string? ActorLabel => "integration:historical-possession";
        public string? IpAddress => "127.0.0.1";
    }

}
