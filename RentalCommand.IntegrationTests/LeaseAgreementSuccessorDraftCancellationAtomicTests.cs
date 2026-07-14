using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using RentalCommand.Core.Atomic;
using RentalCommand.Core.Authorization;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Esign;
using RentalCommand.Core.Interfaces;
using RentalCommand.Core.Leasing;
using RentalCommand.Data;
using RentalCommand.Data.Atomic;
using RentalCommand.Data.Esign;
using RentalCommand.Data.Leasing;
using Testcontainers.PostgreSql;

namespace RentalCommand.IntegrationTests;

/// <summary>PostgreSQL proof for abandoning an unissued canonical Agreement successor draft.</summary>
public sealed class LeaseAgreementSuccessorDraftCancellationAtomicTests : IAsyncLifetime
{
    private const int ActorUserId = 790;
    private static readonly AtomicJsonResultCodec<CancelLeaseAgreementSuccessorDraftResult> Codec =
        new("lease-agreement.successor-draft.cancel.v1");
    private static readonly AtomicJsonResultCodec<LeaseAgreementDraftMutationResult> RecoveryCodec =
        new("lease-agreement.issued-replacement.v1");
    private static readonly AtomicJsonResultCodec<IssueLeaseAgreementResult> IssueCodec =
        new("lease-agreement.issue.v1");

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
        services.AddAtomicCommandHandler<
            ReplaceIssuedAgreementWithDraftCommand,
            LeaseAgreementDraftMutationResult,
            ReplaceIssuedAgreementWithDraftHandler>();
        services.AddAtomicCommandHandler<
            IssueLeaseAgreementCommand,
            IssueLeaseAgreementResult,
            IssueLeaseAgreementHandler>();
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
        replay.Value.Should().BeEquivalentTo(first.Value);
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

    [SkippableFact]
    public async Task Cancel_rejects_revoked_session_without_reopening_successor_slot()
    {
        SkipIfNoDocker();
        await using (var revoke = NewContext())
        {
            var session = await revoke.AuthSessions.SingleAsync(
                candidate => candidate.Id == _scenario.SessionId);
            session.Status = AuthSessionStatus.Revoked;
            session.RevokedAtUtc = DateTime.UtcNow;
            session.RevocationReason = "Authorization race contract test.";
            await revoke.SaveChangesAsync();
        }

        var command = Command("revoked-session", "This mutation must not commit.");
        Func<Task> act = async () => await Atomic.ExecuteAsync(
            new AtomicCommandIdentity(
                "lease-agreement.successor-draft.cancel",
                command.DeliveryIdempotencyKey),
            command,
            Codec);
        await act.Should().ThrowAsync<UnauthorizedAccessException>();

        await using var assert = NewContext();
        var successor = await assert.LeaseAgreements.AsNoTracking()
            .SingleAsync(agreement => agreement.Id == _scenario.SuccessorAgreementId);
        successor.DraftCanceledAtUtc.Should().BeNull();
        (await assert.AtomicCommandReceipts.CountAsync(receipt =>
            receipt.CommandType == "lease-agreement.successor-draft.cancel"
            && receipt.IdempotencyKey == command.DeliveryIdempotencyKey)).Should().Be(0);
    }

    [SkippableFact]
    public async Task Voided_issued_replacement_is_atomic_replayable_and_preserves_history()
    {
        SkipIfNoDocker();
        var leaseManagementId = _scenario.LeaseManagementId + 1;
        await using (var arrange = NewContext())
        {
            var issued = await arrange.LeaseAgreements.SingleAsync(
                agreement => agreement.Id == _scenario.IssuedAgreementId);
            var predecessor = await arrange.LeaseAgreements.SingleAsync(
                agreement => agreement.Id == issued.ReplacesAgreementId);
            var predecessorArtifactId = await AddArtifactAsync(
                arrange, _scenario.PortfolioId, predecessor.Id, 30_003, DateTime.UtcNow);
            predecessor.IssuedArtifactId = predecessorArtifactId;
            predecessor.ExecutedArtifactId = predecessorArtifactId;
            predecessor.IssuedAtUtc = DateTime.UtcNow;
            predecessor.FullyExecutedAtUtc = DateTime.UtcNow;
            arrange.LeaseAgreementSigners.Add(RequiredTenantSigner(
                _scenario.PortfolioId,
                predecessor.Id,
                "executed-predecessor-signer@example.test"));
            issued.VoidedAtUtc = DateTime.UtcNow;
            issued.VoidReasonCode = "ISSUED_AGREEMENT_REPLACED";
            issued.VoidNote = "Incorrect resident legal name.";
            await arrange.SaveChangesAsync();
        }

        var command = new ReplaceIssuedAgreementWithDraftCommand(
            _scenario.PortfolioId,
            leaseManagementId,
            _scenario.IssuedAgreementId,
            null,
            "Correct the resident legal name before reissuing.",
            ActorUserId,
            _scenario.SessionId,
            _scenario.AccessContextId,
            _scenario.AccessRevision,
            "issued-replacement:atomic-replay");
        var identity = new AtomicCommandIdentity(
            "lease-agreement.issued-replacement-draft.create",
            command.DeliveryIdempotencyKey);

        var first = await Atomic.ExecuteAsync(identity, command, RecoveryCodec);
        var replay = await Atomic.ExecuteAsync(identity, command, RecoveryCodec);

        first.Disposition.Should().Be(AtomicCommandDisposition.Executed);
        replay.Disposition.Should().Be(AtomicCommandDisposition.Replayed);
        replay.Value.Should().Be(first.Value);
        first.Value.Outcome.Should().Be(LeaseAgreementDraftMutationOutcome.Applied);
        first.Value.SourceAgreementId.Should().Be(_scenario.IssuedAgreementId);

        int predecessorId;
        await using (var inspect = NewContext())
        {
            predecessorId = (await inspect.LeaseAgreements.AsNoTracking()
                .SingleAsync(agreement => agreement.Id == first.Value.LeaseAgreementId))
                .ReplacesAgreementId!.Value;
        }
        var issuance = await IssueAgreementAsync(
            leaseManagementId,
            first.Value.LeaseAgreementId,
            "issued-replacement:correction-issue");

        var execution = await ExecuteAgreementTransitionAsync(
            leaseManagementId,
            first.Value.LeaseAgreementId,
            issuance.IssuedArtifactId,
            DateTime.UtcNow);
        execution.Outcome.Should().Be(AtomicLegalExecutionTransitionOutcome.Applied);
        execution.PredecessorId.Should().Be(predecessorId);

        await using var assert = NewContext();
        var source = await assert.LeaseAgreements.AsNoTracking()
            .SingleAsync(agreement => agreement.Id == _scenario.IssuedAgreementId);
        var replacement = await assert.LeaseAgreements.AsNoTracking()
            .Include(agreement => agreement.Signers)
            .SingleAsync(agreement => agreement.Id == first.Value.LeaseAgreementId);
        source.IssuedArtifactId.Should().NotBeNull();
        source.VoidedAtUtc.Should().NotBeNull();
        source.VoidReasonCode.Should().Be("ISSUED_AGREEMENT_REPLACED");
        source.VoidNote.Should().Be("Incorrect resident legal name.");
        replacement.LeaseManagementId.Should().Be(leaseManagementId);
        replacement.VersionNumber.Should().Be(3);
        replacement.ChangeType.Should().Be(source.ChangeType);
        replacement.CorrectionReason.Should().Be(source.CorrectionReason);
        replacement.ReplacesAgreementId.Should().Be(predecessorId);
        replacement.ReissuesAgreementId.Should().Be(source.Id);
        replacement.ReissueReason.Should().Be("Correct the resident legal name before reissuing.");
        replacement.IssuedArtifactId.Should().Be(issuance.IssuedArtifactId);
        replacement.ExecutedArtifactId.Should().Be(issuance.IssuedArtifactId);
        replacement.FullyExecutedAtUtc.Should().NotBeNull();
        replacement.Signers.Should().ContainSingle();
        replacement.Signers[0].EmailSnapshot.Should().Be("issued-signer@example.test");
		var persistedPredecessor = await assert.LeaseAgreements.AsNoTracking()
			.SingleAsync(agreement => agreement.Id == predecessorId);
		persistedPredecessor.SupersededByAgreementId.Should().Be(replacement.Id);
        (await assert.LeaseAgreementStatusProjections.CountAsync(status =>
            status.PortfolioId == _scenario.PortfolioId
            && status.LeaseManagementId == leaseManagementId
            && status.IsGoverning)).Should().Be(1);
        (await assert.AtomicCommandReceipts.CountAsync(receipt =>
            receipt.CommandType == identity.CommandType
            && receipt.IdempotencyKey == identity.IdempotencyKey)).Should().Be(1);
        (await assert.AtomicAuditLogs.CountAsync(log =>
            log.EntityType == nameof(LeaseAgreement)
            && log.EntityId == replacement.Id)).Should().BeGreaterThan(0);
    }

    [SkippableFact]
    public async Task Issued_replacement_failure_rolls_back_void_and_draft_together()
    {
        SkipIfNoDocker();
        var leaseManagementId = _scenario.LeaseManagementId + 1;
        await using (var arrange = NewContext())
        {
            await arrange.Database.ExecuteSqlRawAsync($"""
                CREATE OR REPLACE FUNCTION fail_issued_replacement_signer_copy()
                RETURNS trigger LANGUAGE plpgsql AS $function$
                BEGIN
                    IF NEW."LeaseAgreementId" <> {_scenario.IssuedAgreementId}
                       AND NEW."EmailSnapshot" = 'issued-signer@example.test' THEN
                        RAISE EXCEPTION 'injected signer copy failure';
                    END IF;
                    RETURN NEW;
                END
                $function$;
                CREATE TRIGGER fail_issued_replacement_signer_copy
                BEFORE INSERT ON "LeaseAgreementSigners"
                FOR EACH ROW EXECUTE FUNCTION fail_issued_replacement_signer_copy();
                """);
        }
        var command = new ReplaceIssuedAgreementWithDraftCommand(
            _scenario.PortfolioId,
            leaseManagementId,
            _scenario.IssuedAgreementId,
            "Incorrect resident legal name.",
            "Correct the resident legal name before reissuing.",
            ActorUserId,
            _scenario.SessionId,
            _scenario.AccessContextId,
            _scenario.AccessRevision,
            "issued-replacement:rollback");
        var identity = new AtomicCommandIdentity(
            "lease-agreement.issued-replacement-draft.create",
            command.DeliveryIdempotencyKey);

        Func<Task> act = async () => await Atomic.ExecuteAsync(identity, command, RecoveryCodec);
        await act.Should().ThrowAsync<Exception>();

        await using var assert = NewContext();
        var source = await assert.LeaseAgreements.AsNoTracking()
            .SingleAsync(agreement => agreement.Id == _scenario.IssuedAgreementId);
        source.VoidedAtUtc.Should().BeNull();
        source.VoidReasonCode.Should().BeNull();
        source.VoidNote.Should().BeNull();
        (await assert.LeaseAgreements.CountAsync(agreement =>
            agreement.ReissuesAgreementId == source.Id)).Should().Be(0);
        (await assert.AtomicCommandReceipts.CountAsync(receipt =>
            receipt.CommandType == identity.CommandType
            && receipt.IdempotencyKey == identity.IdempotencyKey)).Should().Be(0);
    }

    [SkippableFact]
    public async Task Canceled_reissue_draft_releases_live_reissue_slot_for_retry()
    {
        SkipIfNoDocker();
        var leaseManagementId = _scenario.LeaseManagementId + 1;
        var firstCommand = new ReplaceIssuedAgreementWithDraftCommand(
            _scenario.PortfolioId,
            leaseManagementId,
            _scenario.IssuedAgreementId,
            "The issued copy must be replaced.",
            "Correct the resident legal name before reissuing.",
            ActorUserId,
            _scenario.SessionId,
            _scenario.AccessContextId,
            _scenario.AccessRevision,
            "issued-replacement:cancel-retry:first");
        var first = await Atomic.ExecuteAsync(
            new AtomicCommandIdentity(
                "lease-agreement.issued-replacement-draft.create",
                firstCommand.DeliveryIdempotencyKey),
            firstCommand,
            RecoveryCodec);

        var cancel = new CancelLeaseAgreementSuccessorDraftCommand(
            _scenario.PortfolioId,
            leaseManagementId,
            first.Value.LeaseAgreementId,
            "Abandon this recovery draft and try again.",
            ActorUserId,
            _scenario.SessionId,
            _scenario.AccessContextId,
            _scenario.AccessRevision,
            "successor-cancel:reissue-retry");
        var canceled = await Atomic.ExecuteAsync(
            new AtomicCommandIdentity(
                "lease-agreement.successor-draft.cancel",
                cancel.DeliveryIdempotencyKey),
            cancel,
            Codec);

        var retryCommand = firstCommand with
        {
            ReissueReason = "Correct the resident legal name using the verified spelling.",
            DeliveryIdempotencyKey = "issued-replacement:cancel-retry:second",
        };
        var retry = await Atomic.ExecuteAsync(
            new AtomicCommandIdentity(
                "lease-agreement.issued-replacement-draft.create",
                retryCommand.DeliveryIdempotencyKey),
            retryCommand,
            RecoveryCodec);

        first.Value.Outcome.Should().Be(LeaseAgreementDraftMutationOutcome.Applied);
        canceled.Value.Outcome.Should().Be(CancelLeaseAgreementSuccessorDraftOutcome.Canceled);
        retry.Value.Outcome.Should().Be(LeaseAgreementDraftMutationOutcome.Applied);
        retry.Value.LeaseAgreementId.Should().NotBe(first.Value.LeaseAgreementId);

        await using var assert = NewContext();
        var reissues = await assert.LeaseAgreements.AsNoTracking()
            .Where(agreement => agreement.ReissuesAgreementId == _scenario.IssuedAgreementId)
            .OrderBy(agreement => agreement.Id)
            .Select(agreement => new
            {
                agreement.Id,
                agreement.DraftCanceledAtUtc,
                agreement.ReissueReason,
            })
            .ToListAsync();
        reissues.Should().HaveCount(2);
        reissues.Should().ContainSingle(agreement => agreement.DraftCanceledAtUtc != null);
        reissues.Should().ContainSingle(agreement => agreement.DraftCanceledAtUtc == null
            && agreement.ReissueReason == retryCommand.ReissueReason);
    }

    [SkippableFact]
    public async Task Reissued_initial_executes_without_inventing_a_predecessor()
    {
        SkipIfNoDocker();
        var leaseManagementId = _scenario.LeaseManagementId + 10;
        const int sourceAgreementId = 20_030;
        await SeedIssuedInitialAsync(leaseManagementId, sourceAgreementId, 30_005);

        var command = new ReplaceIssuedAgreementWithDraftCommand(
            _scenario.PortfolioId,
            leaseManagementId,
            sourceAgreementId,
            "The issued copy contains a clerical error.",
            "Reissue the initial Agreement with the verified resident details.",
            ActorUserId,
            _scenario.SessionId,
            _scenario.AccessContextId,
            _scenario.AccessRevision,
            "issued-replacement:initial-execute");
        var recovery = await Atomic.ExecuteAsync(
            new AtomicCommandIdentity(
                "lease-agreement.issued-replacement-draft.create",
                command.DeliveryIdempotencyKey),
            command,
            RecoveryCodec);

        recovery.Value.Outcome.Should().Be(LeaseAgreementDraftMutationOutcome.Applied);
        await using (var inspect = NewContext())
        {
            var replacement = await inspect.LeaseAgreements.AsNoTracking().SingleAsync(
                agreement => agreement.Id == recovery.Value.LeaseAgreementId);
            replacement.ChangeType.Should().Be(LeaseAgreementChangeType.Initial);
            replacement.VersionNumber.Should().Be(2);
            replacement.ReplacesAgreementId.Should().BeNull();
            replacement.RenewsAgreementId.Should().BeNull();
            replacement.ReissuesAgreementId.Should().Be(sourceAgreementId);
        }
        var issuance = await IssueAgreementAsync(
            leaseManagementId,
            recovery.Value.LeaseAgreementId,
            "issued-replacement:initial-issue");

        var execution = await ExecuteAgreementTransitionAsync(
            leaseManagementId,
            recovery.Value.LeaseAgreementId,
            issuance.IssuedArtifactId,
            DateTime.UtcNow);
        execution.Outcome.Should().Be(AtomicLegalExecutionTransitionOutcome.Applied);
        execution.PredecessorId.Should().BeNull();

        await using var assert = NewContext();
        var rows = await assert.LeaseAgreements.AsNoTracking()
            .Where(agreement => agreement.LeaseManagementId == leaseManagementId)
            .OrderBy(agreement => agreement.VersionNumber)
            .ToListAsync();
        rows.Should().HaveCount(2);
        rows[0].VoidedAtUtc.Should().NotBeNull();
        rows[0].SupersededByAgreementId.Should().BeNull();
        rows[1].ExecutedArtifactId.Should().Be(issuance.IssuedArtifactId);
        rows[1].ReissuesAgreementId.Should().Be(sourceAgreementId);
        rows[1].ReplacesAgreementId.Should().BeNull();
        rows[1].RenewsAgreementId.Should().BeNull();
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

    private async Task<IssueLeaseAgreementResult> IssueAgreementAsync(
        int leaseManagementId,
        int leaseAgreementId,
        string operationKey)
    {
        LeaseAgreement draft;
        await using (var arrange = NewContext())
        {
            draft = await arrange.LeaseAgreements.AsNoTracking()
                .Include(agreement => agreement.Signers)
                .SingleAsync(agreement => agreement.Id == leaseAgreementId);
            var contentSha256 = new string('c', 64);
            var fileName = $"agreement-{leaseAgreementId}.pdf";
            var storageKey = $"agreements/{leaseAgreementId}/{operationKey}.pdf";
            var fingerprint = LegalDocumentIssuanceBinding.Create(
                nameof(LeaseAgreement),
                _scenario.PortfolioId,
                leaseManagementId,
                leaseAgreementId,
                draft.DraftRevision,
                draft.DocumentSourceVersionId,
                draft.TermsSchemaVersion,
                draft.TermsPayload,
                contentSha256,
                1,
                fileName);
            var pendingUploadId = Guid.NewGuid();
            arrange.PendingFileUploads.Add(new PendingFileUpload
            {
                Id = pendingUploadId,
                PortfolioId = _scenario.PortfolioId,
                ActorScopeId = ActorUserId,
                Purpose = LegalDocumentIssuanceBinding.AgreementUploadPurpose,
                OperationKeyHash = new string('d', 64),
                RequestFingerprint = fingerprint,
                StoragePath = storageKey,
                FileName = fileName,
                ContentType = "application/pdf",
                SizeBytes = 1,
                State = PendingFileUploadState.Prepared,
                CreatedAtUtc = DateTime.UtcNow,
                UpdatedAtUtc = DateTime.UtcNow,
            });
            await arrange.SaveChangesAsync();

            var command = new IssueLeaseAgreementCommand(
                pendingUploadId,
                draft.DocumentSourceVersionId,
                fingerprint,
                _scenario.PortfolioId,
                leaseManagementId,
                leaseAgreementId,
                draft.DraftRevision,
                operationKey,
                $"Agreement {draft.AgreementNumber}",
                storageKey,
                fileName,
                1,
                contentSha256,
                "https://rental-command.example.test",
                draft.Signers.OrderBy(signer => signer.Id)
                    .Select(signer => new NativeEsignSignerCommand(signer.Id))
                    .ToArray(),
                ActorUserId,
                _scenario.SessionId,
                _scenario.AccessContextId,
                _scenario.AccessRevision);
            var issued = await Atomic.ExecuteAsync(
                new AtomicCommandIdentity("lease-agreement.issue", operationKey),
                command,
                IssueCodec);
            issued.Disposition.Should().Be(AtomicCommandDisposition.Executed);
            return issued.Value;
        }
    }

    private async Task<AtomicLegalExecutionTransitionResult> ExecuteAgreementTransitionAsync(
        int leaseManagementId,
        int leaseAgreementId,
        int executedArtifactId,
        DateTime executedAtUtc)
    {
        await using var db = NewContext();
        await using var transaction = await db.Database.BeginTransactionAsync();
        var auditScope = new AtomicAuditScope(new AtomicPersistenceMode(false), TimeProvider.System);
        using var attempt = auditScope.BeginAttempt(
            new AtomicCommandIdentity(
                "test.issued-agreement-reissue-transition",
                Guid.NewGuid().ToString("N")),
            Guid.NewGuid());
        var persistence = new AtomicLeaseMutationPersistence(db, auditScope);

        var result = await persistence.ExecuteLegalArtifactTransitionAsync(
            _scenario.PortfolioId,
            leaseManagementId,
            leaseAgreementId,
            leaseAddendumId: null,
            executedArtifactId,
            executedAtUtc);
        await transaction.CommitAsync();
        return result;
    }

    private async Task SeedIssuedInitialAsync(
        int leaseManagementId,
        int sourceAgreementId,
        int issuedArtifactId)
    {
        await using var db = NewContext();
        var existingRelationship = await db.LeaseManagements.AsNoTracking()
            .SingleAsync(relationship => relationship.Id == _scenario.LeaseManagementId);
        var sourceVersionId = await db.LeaseAgreements.AsNoTracking()
            .Where(agreement => agreement.Id == _scenario.SourceAgreementId)
            .Select(agreement => agreement.DocumentSourceVersionId)
            .SingleAsync();
        var now = DateTime.UtcNow;
        var relationship = new LeaseManagement
        {
            Id = leaseManagementId,
            PublicId = Guid.NewGuid(),
            PortfolioId = _scenario.PortfolioId,
            PropertyId = existingRelationship.PropertyId,
            UnitId = existingRelationship.UnitId,
            RelationshipNumber = "REL-CANCEL-INITIAL-REISSUE",
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
            CreatedByUserId = ActorUserId,
            RowVersion = Guid.NewGuid(),
        };
        var source = NewAgreement(
            sourceAgreementId,
            relationship,
            sourceVersionId,
            1,
            LeaseAgreementChangeType.Initial,
            now);
        db.AddRange(relationship, source);
        db.LeaseAgreementSigners.Add(RequiredTenantSigner(
            _scenario.PortfolioId,
            sourceAgreementId,
            "initial-issued-signer@example.test"));
        await db.SaveChangesAsync();

        source.IssuedArtifactId = await AddArtifactAsync(
            db, _scenario.PortfolioId, sourceAgreementId, issuedArtifactId, now);
        source.IssuedAtUtc = now;
        await db.SaveChangesAsync();
    }

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
        issued.GoverningFromOn = new DateOnly(2026, 6, 1);
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
