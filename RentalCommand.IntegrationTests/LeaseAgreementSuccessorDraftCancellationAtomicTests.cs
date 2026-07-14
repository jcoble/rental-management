using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using RentalCommand.Core.Atomic;
using RentalCommand.Core.Authorization;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Interfaces;
using RentalCommand.Core.Leasing;
using RentalCommand.Data;
using RentalCommand.Data.Atomic;
using RentalCommand.Data.Leasing;
using Testcontainers.PostgreSql;

namespace RentalCommand.IntegrationTests;

/// <summary>PostgreSQL proof for abandoning an unissued canonical Agreement successor draft.</summary>
public sealed class LeaseAgreementSuccessorDraftCancellationAtomicTests : IAsyncLifetime
{
    private const int ActorUserId = 790;
    private static readonly AtomicJsonResultCodec<CancelLeaseAgreementSuccessorDraftResult> Codec =
        new("lease-agreement.successor-draft.cancel.v1");

    private PostgreSqlContainer? _postgres;
    private ServiceProvider? _services;
    private bool _dockerAvailable;
    private Scenario _scenario = default!;

    public async Task InitializeAsync()
    {
        try
        {
            _postgres = new PostgreSqlBuilder()
                .WithImage("postgres:16-alpine")
                .WithDatabase("rentalcommand_successor_cancel")
                .WithUsername("postgres")
                .WithPassword("postgres")
                .Build();
            await _postgres.StartAsync();
            _dockerAvailable = true;
        }
        catch
        {
            return;
        }

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(TimeProvider.System);
        services.AddScoped<ICurrentActor, TestActor>();
        services.AddAtomicPersistenceKernel();
        services.AddAtomicCommandHandler<
            CancelLeaseAgreementSuccessorDraftCommand,
            CancelLeaseAgreementSuccessorDraftResult,
            CancelLeaseAgreementSuccessorDraftHandler>();
        services.AddDbContext<RentalCommandDbContext>((provider, options) =>
            options.UseNpgsql(_postgres!.GetConnectionString())
                .UseAtomicPersistenceKernel(provider));
        _services = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true,
        });

        await using var db = NewContext();
        await db.Database.MigrateAsync();
        _scenario = await SeedAsync(db);
    }

    public async Task DisposeAsync()
    {
        if (_services is not null) await _services.DisposeAsync();
        if (_postgres is not null) await _postgres.DisposeAsync();
    }

    [SkippableFact]
    public async Task Cancel_commits_canonical_facts_releases_successor_slot_and_replays_once()
    {
        SkipIfNoDocker();
        var command = Command("abandon-correction", "The correction is no longer needed.");
        var identity = new AtomicCommandIdentity(
            "lease-agreement.successor-draft.cancel", command.DeliveryIdempotencyKey);

        var first = await Atomic.ExecuteAsync(identity, command, Codec);
        var replay = await Atomic.ExecuteAsync(identity, command, Codec);

        first.Disposition.Should().Be(AtomicCommandDisposition.Executed);
        replay.Disposition.Should().Be(AtomicCommandDisposition.Replayed);
        replay.Value.Should().Be(first.Value);
        first.Value.Outcome.Should().Be(CancelLeaseAgreementSuccessorDraftOutcome.Canceled);

        await using var db = NewContext();
        var canceled = await db.LeaseAgreements.AsNoTracking()
            .SingleAsync(agreement => agreement.Id == _scenario.SuccessorAgreementId);
        canceled.DraftCanceledAtUtc.Should().NotBeNull();
        canceled.DraftCanceledByUserId.Should().Be(ActorUserId);
        canceled.DraftCancellationReason.Should().Be("The correction is no longer needed.");
        (await db.AtomicCommandReceipts.CountAsync(receipt =>
            receipt.CommandType == identity.CommandType
            && receipt.IdempotencyKey == identity.IdempotencyKey)).Should().Be(1);

        db.LeaseAgreements.Add(NewSuccessor(
            _scenario.ReplacementAgreementId,
            3,
            LeaseAgreementChangeType.Restatement,
            correctionReason: null));
        await db.SaveChangesAsync();
        (await db.LeaseAgreements.CountAsync(agreement =>
            agreement.ReplacesAgreementId == _scenario.SourceAgreementId
            && agreement.DraftCanceledAtUtc == null)).Should().Be(1);
    }

    [SkippableFact]
    public async Task Issued_and_executed_successors_are_rejected_without_cancellation_facts()
    {
        SkipIfNoDocker();
        await AssertIssuedOrExecutedRejectedAsync(_scenario.IssuedAgreementId, "issued");
        await AssertIssuedOrExecutedRejectedAsync(_scenario.ExecutedAgreementId, "executed");

        await using var db = NewContext();
        var protectedRows = await db.LeaseAgreements.AsNoTracking()
            .Where(agreement => agreement.Id == _scenario.IssuedAgreementId
                || agreement.Id == _scenario.ExecutedAgreementId)
            .OrderBy(agreement => agreement.Id)
            .Select(agreement => new
            {
                agreement.DraftCanceledAtUtc,
                agreement.DraftCanceledByUserId,
                agreement.DraftCancellationReason,
            })
            .ToListAsync();
        protectedRows.Should().OnlyContain(row => row.DraftCanceledAtUtc == null
            && row.DraftCanceledByUserId == null && row.DraftCancellationReason == null);
    }

    private async Task AssertIssuedOrExecutedRejectedAsync(int agreementId, string suffix)
    {
        var command = Command($"reject-{suffix}", $"Do not cancel the {suffix} Agreement.") with
        {
            LeaseManagementId = _scenario.LeaseManagementId
                + (agreementId == _scenario.IssuedAgreementId ? 1 : 2),
            LeaseAgreementId = agreementId,
        };
        var outcome = await Atomic.ExecuteAsync(
            new AtomicCommandIdentity(
                "lease-agreement.successor-draft.cancel", command.DeliveryIdempotencyKey),
            command,
            Codec);

        outcome.Value.Outcome.Should().Be(CancelLeaseAgreementSuccessorDraftOutcome.IssuedOrExecuted);
    }

    private CancelLeaseAgreementSuccessorDraftCommand Command(string key, string reason) => new(
        _scenario.PortfolioId,
        _scenario.LeaseManagementId,
        _scenario.SuccessorAgreementId,
        reason,
        ActorUserId,
        _scenario.SessionId,
        _scenario.AccessContextId,
        _scenario.AccessRevision,
        $"successor-cancel:{key}");

    private IAtomicUnitOfWork Atomic => _services!.GetRequiredService<IAtomicUnitOfWork>();

    private RentalCommandDbContext NewContext() => new(
        new DbContextOptionsBuilder<RentalCommandDbContext>()
            .UseNpgsql(_postgres!.GetConnectionString())
            .Options);

    private async Task<Scenario> SeedAsync(RentalCommandDbContext db)
    {
        var now = DateTime.UtcNow;
        var user = new ApplicationUser
        {
            Id = ActorUserId,
            UserName = "successor-cancel-user",
            NormalizedUserName = "SUCCESSOR-CANCEL-USER",
            Email = "successor-cancel@example.test",
            NormalizedEmail = "SUCCESSOR-CANCEL@EXAMPLE.TEST",
            DisplayName = "Successor Cancel User",
            SecurityStamp = Guid.NewGuid().ToString("N"),
            ConcurrencyStamp = Guid.NewGuid().ToString("N"),
            CreatedAt = now,
        };
        var portfolio = new Portfolio
        {
            Name = "Successor Cancel Portfolio",
            ManagementCompanyName = "Successor Cancel Management",
            TimeZone = "UTC",
            CreatedAt = now,
            UpdatedAt = now,
        };
        var property = new Property
        {
            Portfolio = portfolio,
            Name = "Successor Cancel Property",
            AddressLine1 = "100 Test Ave",
            City = "Columbus",
            State = "OH",
            PostalCode = "43215",
            CreatedAt = now,
            UpdatedAt = now,
        };
        var unit = new Unit
        {
            Property = property,
            UnitNumber = "1",
            CreatedAt = now,
            UpdatedAt = now,
        };
        var template = new DocumentTemplate
        {
            Portfolio = portfolio,
            Kind = DocumentTemplateKind.Lease,
            Status = DocumentTemplateStatus.Active,
            RenderMode = DocumentTemplateRenderMode.Overlay,
            Name = "Successor cancel lease",
            Version = 1,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
        };
        var sourceVersion = new LegalDocumentSourceVersion
        {
            PublicId = Guid.NewGuid(),
            Portfolio = portfolio,
            SourceKind = LegalDocumentSourceKind.AuthoredTemplateSnapshot,
            BusinessKey = "successor-cancel-template:v1",
            DocumentTemplate = template,
            DocumentTemplateVersion = 1,
            RendererKey = "lease-agreement-overlay",
            RendererVersion = 1,
            SnapshotPayload = "{}",
            CreatedAtUtc = now,
            CreatedByUserId = ActorUserId,
        };

        var relationships = Enumerable.Range(0, 3).Select(index => new LeaseManagement
        {
            Id = 10_000 + index,
            PublicId = Guid.NewGuid(),
            Portfolio = portfolio,
            Property = property,
            Unit = unit,
            RelationshipNumber = $"REL-CANCEL-{index + 1}",
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
            CreatedByUserId = ActorUserId,
            RowVersion = Guid.NewGuid(),
        }).ToArray();

        db.AddRange(user, portfolio, property, unit, template, sourceVersion);
        db.AddRange(relationships);
        await db.SaveChangesAsync();

        var sourceIds = new[] { 20_000, 20_010, 20_020 };
        var successorIds = new[] { 20_001, 20_011, 20_021 };
        for (var index = 0; index < relationships.Length; index++)
        {
            db.LeaseAgreements.Add(NewAgreement(
                sourceIds[index], relationships[index], sourceVersion.Id, 1,
                LeaseAgreementChangeType.Initial, now));
            db.LeaseAgreements.Add(NewAgreement(
                successorIds[index], relationships[index], sourceVersion.Id, 2,
                LeaseAgreementChangeType.Correction, now, sourceIds[index]));
        }
        await db.SaveChangesAsync();

        var issuedArtifactId = await AddArtifactAsync(db, portfolio.Id, successorIds[1], 30_001, now);
        var executedArtifactId = await AddArtifactAsync(db, portfolio.Id, successorIds[2], 30_002, now);
        var issued = await db.LeaseAgreements.SingleAsync(agreement => agreement.Id == successorIds[1]);
        issued.IssuedArtifactId = issuedArtifactId;
        issued.IssuedAtUtc = now;
        var executed = await db.LeaseAgreements.SingleAsync(agreement => agreement.Id == successorIds[2]);
        executed.IssuedArtifactId = executedArtifactId;
        executed.IssuedAtUtc = now;
        executed.ExecutedArtifactId = executedArtifactId;
        executed.FullyExecutedAtUtc = now;
        executed.GoverningFromOn = new DateOnly(2026, 2, 1);
        var executedPredecessor = await db.LeaseAgreements.SingleAsync(
            agreement => agreement.Id == sourceIds[2]);
        executedPredecessor.SupersededEffectiveOn = executed.GoverningFromOn;
        executedPredecessor.SupersededByAgreementId = executed.Id;
        executedPredecessor.SupersessionRecordedAtUtc = now;
        db.LeaseAgreementSigners.AddRange(
            RequiredTenantSigner(portfolio.Id, successorIds[1], "issued-signer@example.test"),
            RequiredTenantSigner(portfolio.Id, successorIds[2], "executed-signer@example.test"));

        var accessContext = new WorkspaceAccessContext
        {
            User = user,
            PortfolioId = portfolio.Id,
            Status = WorkspaceAccessContextStatus.Active,
            LastAuthorizedExperience = WorkspaceExperience.Management,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
        };
        var membership = new WorkspaceMembership
        {
            AccessContext = accessContext,
            PortfolioId = portfolio.Id,
            Status = WorkspaceMembershipStatus.Active,
            DefaultExperience = WorkspaceExperience.Management,
            EffectiveFromUtc = now.AddMinutes(-1),
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
        };
        var assignment = new MembershipRoleAssignment
        {
            WorkspaceMembership = membership,
            PortfolioId = portfolio.Id,
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
            ActiveAccessContext = accessContext,
            Status = AuthSessionStatus.Active,
            CreatedAtUtc = now,
            LastSeenAtUtc = now,
            ExpiresAtUtc = now.AddHours(1),
        };
        db.AddRange(assignment, session);
        await db.SaveChangesAsync();

        return new Scenario(
            portfolio.Id,
            relationships[0].Id,
            sourceIds[0],
            successorIds[0],
            20_002,
            successorIds[1],
            successorIds[2],
            session.Id,
            accessContext.Id,
            accessContext.AccessRevision);
    }

    private static LeaseAgreementSigner RequiredTenantSigner(
        int portfolioId,
        int leaseAgreementId,
        string email) => new()
    {
        PortfolioId = portfolioId,
        LeaseAgreementId = leaseAgreementId,
        SignerRole = LeaseLegalSignerRole.PrimaryTenant,
        NameSnapshot = "Test Tenant",
        EmailSnapshot = email,
        SigningOrder = 1,
        IsRequired = true,
    };

    private LeaseAgreement NewSuccessor(
        int id,
        int version,
        LeaseAgreementChangeType changeType,
        string? correctionReason) => NewAgreement(
        id,
        new LeaseManagement
        {
            Id = _scenario.LeaseManagementId,
            PortfolioId = _scenario.PortfolioId,
        },
        documentSourceVersionId: 1,
        version,
        changeType,
        DateTime.UtcNow,
        _scenario.SourceAgreementId,
        correctionReason);

    private static LeaseAgreement NewAgreement(
        int id,
        LeaseManagement relationship,
        int documentSourceVersionId,
        int version,
        LeaseAgreementChangeType changeType,
        DateTime now,
        int? sourceAgreementId = null,
        string? correctionReason = "Correct the resident name.") => new()
        {
            Id = id,
            PublicId = Guid.NewGuid(),
            PortfolioId = relationship.PortfolioId,
            LeaseManagementId = relationship.Id,
            VersionNumber = version,
            AgreementNumber = $"AGR-{relationship.Id}-V{version}",
            ChangeType = changeType,
            CorrectionReason = changeType == LeaseAgreementChangeType.Correction ? correctionReason : null,
            ReplacesAgreementId = sourceAgreementId,
            TermType = LeaseAgreementTermType.FixedTerm,
            TermStartOn = new DateOnly(2026, 1, 1),
            TermEndOn = new DateOnly(2026, 12, 31),
            GoverningFromOn = new DateOnly(2026, 1, 1),
            BaseRentAmount = 1_000,
            RentDueDay = 1,
            SecurityDepositObligation = 1_000,
            LateFeeAmount = 50,
            GracePeriodDays = 5,
            Currency = "USD",
            TermsSchemaVersion = 1,
            TermsPayload = "{}",
            DocumentSourceVersionId = documentSourceVersionId,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
            CreatedByUserId = ActorUserId,
        };

    private static async Task<int> AddArtifactAsync(
        RentalCommandDbContext db,
        int portfolioId,
        int agreementId,
        int id,
        DateTime now)
    {
        var file = new StoredFile
        {
            Id = id,
            PortfolioId = portfolioId,
            FileName = $"agreement-{agreementId}.pdf",
            FilePath = $"agreements/{agreementId}.pdf",
            ContentType = "application/pdf",
            FileSize = 1,
            EntityType = nameof(LeaseAgreement),
            EntityId = agreementId,
            UploadedAt = now,
        };
        var artifact = new LegalDocumentArtifact
        {
            Id = id,
            PublicId = Guid.NewGuid(),
            PortfolioId = portfolioId,
            StoredFile = file,
            ArtifactKind = LegalDocumentArtifactKind.IssuedAgreement,
            StorageKey = file.FilePath,
            FileName = file.FileName,
            ContentType = file.ContentType,
            ByteLength = 1,
            ContentSha256 = new string('a', 64),
            LegalIssuanceFingerprint = new string('b', 64),
            CreatedAtUtc = now,
            CreatedByUserId = ActorUserId,
        };
        db.Add(artifact);
        await db.SaveChangesAsync();
        return artifact.Id;
    }

    private void SkipIfNoDocker() =>
        Skip.IfNot(_dockerAvailable, "Docker is not available; successor cancellation proof skipped.");

    private sealed class TestActor : ICurrentActor
    {
        public int? UserId => ActorUserId;
        public string? ActorLabel => null;
        public string? IpAddress => "127.0.0.1";
    }

    private sealed record Scenario(
        int PortfolioId,
        int LeaseManagementId,
        int SourceAgreementId,
        int SuccessorAgreementId,
        int ReplacementAgreementId,
        int IssuedAgreementId,
        int ExecutedAgreementId,
        Guid SessionId,
        int AccessContextId,
        long AccessRevision);
}
