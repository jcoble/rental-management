using System.Data.Common;
using System.Security.Cryptography;
using System.Text;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using RentalCommand.Core.Atomic;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Esign;
using RentalCommand.Core.Interfaces;
using RentalCommand.Core.Leasing;
using RentalCommand.Core.Time;
using RentalCommand.Data;
using RentalCommand.Data.Accounting;
using RentalCommand.Data.Atomic;
using RentalCommand.Data.Esign;
using RentalCommand.Data.Leasing;
using RentalCommand.TestCommon;

namespace RentalCommand.IntegrationTests;

public sealed class NativeEsignDepositChargePostgreSqlTests : IAsyncLifetime
{
    private const int ActorUserId = 19_300;
    private static readonly DateTime FrozenBusinessNow =
        new(2027, 1, 25, 5, 0, 0, DateTimeKind.Utc);

    private SharedPostgreSqlDatabase? _postgres;
    private ServiceProvider? _services;
    private IServiceScope? _serviceScope;
    private readonly DepositBatchCommandCounter _commandCounter = new();
    private bool _dockerAvailable;

    public async Task InitializeAsync()
    {
        try
        {
            _postgres = new SharedPostgreSqlDatabase(SharedPostgreSqlSchema.Migrated);
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
        services.AddDbContext<RentalCommandDbContext>((provider, options) =>
            options.UseNpgsql(_postgres!.GetConnectionString())
                .AddInterceptors(_commandCounter)
                .UseAtomicPersistenceKernel(provider));
        _services = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true,
        });
        _serviceScope = _services.CreateScope();

        await using var db = NewContext();
        await db.Database.MigrateAsync();
        await FreezeClockAsync(db);
    }

    public async Task DisposeAsync()
    {
        _serviceScope?.Dispose();
        if (_services is not null) await _services.DisposeAsync();
        if (_postgres is not null) await _postgres.DisposeAsync();
    }

    [SkippableFact]
    public async Task AppliedInitialExecution_PostsExactlyOneDepositChargeOnFrozenBusinessDate()
    {
        SkipIfNoDocker();
        var scenario = await SeedScenarioAsync("applied", 1_675m);

        var transition = await ExecuteTransitionAsync(scenario);
        transition.Outcome.Should().Be(AtomicLegalExecutionTransitionOutcome.Applied);

        await using var db = NewContext();
        var charge = await db.TenantLedgerEntries.SingleAsync(entry =>
            entry.TenantAccountId == scenario.TenantAccountId
            && entry.LeaseAgreementId == scenario.LeaseAgreementId
            && entry.EntryType == TenantLedgerEntryType.DepositCharge);
        charge.Direction.Should().Be(TenantLedgerDirection.Debit);
        charge.Amount.Should().Be(1_675m);
        charge.Currency.Should().Be("USD");
        charge.EffectiveOn.Should().Be(DateOnly.FromDateTime(FrozenBusinessNow));
        charge.DueOn.Should().Be(DateOnly.FromDateTime(FrozenBusinessNow));
        charge.PostedAtUtc.Should().Be(FrozenBusinessNow);
        charge.BusinessKey.Should().Be($"security-deposit:agreement:{scenario.AgreementPublicId}");

        var journalLines = await db.JournalLines.AsNoTracking()
            .Where(line => line.JournalEntry!.PortfolioId == scenario.PortfolioId
                && line.JournalEntry.SourceType == JournalSourceType.TenantCharge
                && line.JournalEntry.SourceId == charge.Id)
            .OrderBy(line => line.LedgerAccount!.Code)
            .Select(line => new
            {
                SystemKey = line.LedgerAccount!.SystemKey,
                line.DebitAmount,
                line.CreditAmount,
                line.TenantAccountId,
            })
            .ToListAsync();
        journalLines.Should().BeEquivalentTo([
            new
            {
                SystemKey = "tenant-accounts-receivable",
                DebitAmount = 1_675m,
                CreditAmount = 0m,
                TenantAccountId = (int?)scenario.TenantAccountId,
            },
            new
            {
                SystemKey = "tenant-security-deposits-payable",
                DebitAmount = 0m,
                CreditAmount = 1_675m,
                TenantAccountId = (int?)scenario.TenantAccountId,
            },
        ]);
    }

    [SkippableFact]
    public async Task CompletedAgreementReconciliation_IsIdempotentAndDoesNotChangeArtifacts()
    {
        SkipIfNoDocker();
        var scenario = await SeedScenarioAsync("completed-reconcile", 1_200m, completed: true);

        var command = new ReconcileNativeEsignAgreementFinancialsCommand(
            scenario.SignatureRequestId, scenario.SignatureRequestPublicId);
        var first = await Writes.ExecuteAsync("first",
            NativeEsignWriteSupport.Write<ReconcileNativeEsignAgreementFinancialsCommand,
                ReconcileNativeEsignAgreementFinancialsResult>(ScopedDb, command));
        var second = await Writes.ExecuteAsync("first",
            NativeEsignWriteSupport.Write<ReconcileNativeEsignAgreementFinancialsCommand,
                ReconcileNativeEsignAgreementFinancialsResult>(ScopedDb, command));

        first.Value.DepositChargeCount.Should().Be(1);
        second.Disposition.Should().Be(AtomicCommandDisposition.Replayed);
        second.Value.Should().BeEquivalentTo(first.Value);

        await using var db = NewContext();
        (await db.TenantLedgerEntries.CountAsync(entry =>
            entry.TenantAccountId == scenario.TenantAccountId
            && entry.BusinessKey == $"security-deposit:agreement:{scenario.AgreementPublicId}"))
            .Should().Be(1);
        var request = await db.SignatureRequests.AsNoTracking()
            .SingleAsync(row => row.Id == scenario.SignatureRequestId);
        request.ExecutedArtifactId.Should().Be(scenario.ExecutedArtifactId);
        request.Status.Should().Be(SignatureRequestStatus.Completed);
        (await db.AtomicAuditLogs.CountAsync(row =>
            row.EntityType == nameof(TenantAccount)
            && row.EntityId == scenario.TenantAccountId
            && row.ChangeReason == "Posted initial security-deposit charge at Agreement execution."))
            .Should().Be(1);
        (await CountDepositChargeOutboxMessagesAsync(
            db, [scenario.TenantAccountId]))
            .Should().Be(1);
    }

    [SkippableFact]
    public async Task CompletedAgreementBatchReconciliation_IsSetBasedAuditedAndExactReplayInsertsNothing()
    {
        SkipIfNoDocker();
        var firstScenario = await SeedScenarioAsync("completed-batch-a", 1_100m, completed: true);
        var secondScenario = await SeedScenarioAsync("completed-batch-b", 1_300m, completed: true);
        _ = await SeedScenarioAsync("completed-batch-zero", 0m, completed: true);

        _commandCounter.Reset();
        var firstCommand = new ReconcileNativeEsignAgreementFinancialsBatchCommand(Guid.NewGuid(), 10);
        var first = await Writes.ExecuteAsync("batch-first",
            NativeEsignWriteSupport.Write<ReconcileNativeEsignAgreementFinancialsBatchCommand,
                ReconcileNativeEsignAgreementFinancialsBatchResult>(ScopedDb, firstCommand));
        var mutationCommandCount = _commandCounter.DepositBatchMutationCommandCount;
        var second = await Writes.ExecuteAsync("batch-first",
            NativeEsignWriteSupport.Write<ReconcileNativeEsignAgreementFinancialsBatchCommand,
                ReconcileNativeEsignAgreementFinancialsBatchResult>(ScopedDb, firstCommand));

        first.Value.DepositChargeCount.Should().Be(2);
        mutationCommandCount.Should().Be(1);
        second.Disposition.Should().Be(AtomicCommandDisposition.Replayed);
        second.Value.Should().BeEquivalentTo(first.Value);

        await using var db = NewContext();
        var accountIds = new[] { firstScenario.TenantAccountId, secondScenario.TenantAccountId };
        (await db.TenantLedgerEntries.CountAsync(entry =>
            accountIds.Contains(entry.TenantAccountId)
            && entry.EntryType == TenantLedgerEntryType.DepositCharge))
            .Should().Be(2);
        (await db.AtomicAuditLogs.CountAsync(row =>
            row.EntityType == nameof(TenantAccount)
            && accountIds.Contains(row.EntityId)
            && row.ChangeReason == "Posted initial security-deposit charge at Agreement execution."))
            .Should().Be(2);
        (await CountDepositChargeOutboxMessagesAsync(
            db, [firstScenario.TenantAccountId, secondScenario.TenantAccountId]))
            .Should().Be(2);
    }

    [SkippableFact]
    public async Task ZeroObligation_DoesNotPostDepositCharge()
    {
        SkipIfNoDocker();
        var scenario = await SeedScenarioAsync("zero", 0m);

        (await ExecuteTransitionAsync(scenario)).Outcome.Should()
            .Be(AtomicLegalExecutionTransitionOutcome.Applied);

        await using var db = NewContext();
        (await db.TenantLedgerEntries.CountAsync(entry =>
            entry.TenantAccountId == scenario.TenantAccountId
            && entry.EntryType == TenantLedgerEntryType.DepositCharge))
            .Should().Be(0);
    }

    [SkippableFact]
    public async Task RollbackAndTargetConflict_DoNotLeaveDepositCharge()
    {
        SkipIfNoDocker();
        var rollback = await SeedScenarioAsync("rollback", 900m);

        await using (var db = NewContext())
        {
            await using var transaction = await db.Database.BeginTransactionAsync();
            var auditScope = new AtomicAuditScope(TimeProvider.System);
            var attemptId = Guid.NewGuid();
            var commandContext = new AtomicCommandContext(db, auditScope, TimeProvider.System);
            commandContext.BeginAttempt(attemptId);
            commandContext.BindReceipt(Guid.NewGuid());
            using var attempt = auditScope.BeginAttempt(
                new AtomicCommandIdentity("test.transition.rollback", Guid.NewGuid().ToString("N")),
                attemptId,
                db);
            (await AtomicLeaseMutationPersistence.ExecuteLegalArtifactTransitionAsync(
                db,
                commandContext,
                rollback.PortfolioId,
                rollback.LeaseManagementId,
                rollback.LeaseAgreementId,
                null,
                rollback.ExecutedArtifactId,
                FrozenBusinessNow)).Outcome.Should().Be(AtomicLegalExecutionTransitionOutcome.Applied);
            await transaction.RollbackAsync();
            commandContext.EndAttempt();
        }

        var conflict = await SeedScenarioAsync("conflict", 800m, voided: true);
        (await ExecuteTransitionAsync(conflict)).Outcome.Should()
            .Be(AtomicLegalExecutionTransitionOutcome.TargetChanged);

        await using var verify = NewContext();
        (await verify.TenantLedgerEntries.CountAsync(entry =>
            entry.LeaseAgreementId == rollback.LeaseAgreementId
            || entry.LeaseAgreementId == conflict.LeaseAgreementId))
            .Should().Be(0);
    }

    [SkippableFact]
    public async Task SignerSecurityUsesWallClockButBusinessFactsUseFrozenClock()
    {
        SkipIfNoDocker();
        var scenario = await SeedScenarioAsync("future-token", 500m, signed: false);

        var occurredAtUtc = DateTime.UtcNow;
        var command = new RecordNativeSignatureCommand(
                scenario.TokenHash,
                SignatureSignatureType.Typed,
                "Future Tenant",
                null,
                null,
                null,
                null,
                "127.0.0.1",
                "integration-test",
                occurredAtUtc);
        var result = await Writes.ExecuteAsync("future-token",
            NativeEsignWriteSupport.Write<RecordNativeSignatureCommand,
                NativeSignerActionResult>(ScopedDb, command));
        var replayCommand = new RecordNativeSignatureCommand(
            scenario.TokenHash, SignatureSignatureType.Typed, "Future Tenant",
            null, null, null, null, "127.0.0.1", "integration-test", occurredAtUtc);
        var replay = await Writes.ExecuteAsync("future-token",
            NativeEsignWriteSupport.Write<RecordNativeSignatureCommand,
                NativeSignerActionResult>(ScopedDb, replayCommand));

        result.Value.Outcome.Should().Be(NativeSignerActionOutcome.Applied);
        replay.Disposition.Should().Be(AtomicCommandDisposition.Replayed);
        replay.Value.Should().BeEquivalentTo(result.Value);
        await using var db = NewContext();
        var signer = await db.SignatureSigners.AsNoTracking()
            .SingleAsync(row => row.Id == scenario.SignatureSignerId);
        signer.SignedAtUtc.Should().Be(FrozenBusinessNow);
        signer.TokenExpiresAtUtc.Should().BeAfter(DateTime.UtcNow);
        (await db.Set<SignatureAuditEvent>().CountAsync(row =>
            row.SignatureSignerId == scenario.SignatureSignerId
            && row.Type == SignatureAuditEventType.Signed)).Should().Be(1);
    }

    [SkippableFact]
    public async Task SignRetryWithDifferentTimestamp_PreservesRecordedLegacyConflict()
    {
        SkipIfNoDocker();
        var scenario = await SeedScenarioAsync("volatile-sign-retry", 500m, signed: false);
        var occurredAtUtc = DateTime.UtcNow;
        var command = new RecordNativeSignatureCommand(
            scenario.TokenHash, SignatureSignatureType.Typed, "Future Tenant",
            null, null, null, null, "127.0.0.1", "integration-test", occurredAtUtc);
        await Writes.ExecuteAsync("volatile-sign-retry",
            NativeEsignWriteSupport.Write<RecordNativeSignatureCommand,
                NativeSignerActionResult>(ScopedDb, command));

        // Intentional parity with the recorded latent defect: volatile request facts remain fingerprinted.
        var retry = command with { OccurredAtUtc = occurredAtUtc.AddTicks(1) };
        var act = () => Writes.ExecuteAsync("volatile-sign-retry",
            NativeEsignWriteSupport.Write<RecordNativeSignatureCommand,
                NativeSignerActionResult>(ScopedDb, retry));

        await act.Should().ThrowAsync<AtomicIdempotencyConflictException>();
        await using var db = NewContext();
        (await db.Set<SignatureAuditEvent>().CountAsync(row =>
            row.SignatureSignerId == scenario.SignatureSignerId
            && row.Type == SignatureAuditEventType.Signed)).Should().Be(1);
    }

    [SkippableFact]
    public async Task ViewDeclineAndFinalize_ExecuteThroughRequestExecutorAndReplayWithoutDuplicateTransitions()
    {
        SkipIfNoDocker();
        var viewScenario = await SeedScenarioAsync("executor-view", 0m, signed: false);
        var viewAt = DateTime.UtcNow;
        var view = new RecordNativeEsignViewCommand(
            viewScenario.TokenHash, "127.0.0.1", "integration-test", viewAt);
        var viewed = await ExecuteAsync("executor-view", view);
        var viewReplay = await ExecuteAsync("executor-view", view with { });
        viewed.Disposition.Should().Be(AtomicCommandDisposition.Executed);
        viewReplay.Disposition.Should().Be(AtomicCommandDisposition.Replayed);

        var declineScenario = await SeedScenarioAsync("executor-decline", 0m, signed: false);
        var declineAt = DateTime.UtcNow;
        var decline = new RecordNativeDeclineCommand(
            declineScenario.TokenHash, "No longer proceeding", "127.0.0.1", "integration-test", declineAt);
        var declined = await ExecuteAsync("executor-decline", decline);
        var declineReplay = await ExecuteAsync("executor-decline", decline with { });
        declined.Disposition.Should().Be(AtomicCommandDisposition.Executed);
        declineReplay.Disposition.Should().Be(AtomicCommandDisposition.Replayed);

        var finalScenario = await SeedScenarioAsync("executor-finalize", 0m);
        var pendingId = Guid.NewGuid();
        var claimToken = Guid.NewGuid();
        const string finalFingerprint = "executor-finalize-fingerprint";
        const string finalStorageKey = "agreements/executor-finalized.pdf";
        const string finalFileName = "executor-finalized.pdf";
        var finalSha = new string('e', 64);
        await using (var arrangeFinal = NewContext())
        {
            var request = await arrangeFinal.SignatureRequests.SingleAsync(row =>
                row.Id == finalScenario.SignatureRequestId);
            request.ExecutionClaimOwner = "integration-finalize";
            request.ExecutionClaimToken = claimToken;
            request.ExecutionClaimExpiresAtUtc = DateTime.UtcNow.AddHours(1);
            arrangeFinal.PendingFileUploads.Add(new PendingFileUpload
            {
                Id = pendingId, PortfolioId = finalScenario.PortfolioId, ActorScopeId = ActorUserId,
                Purpose = "native-esign-executed", OperationKeyHash = new string('f', 64),
                RequestFingerprint = finalFingerprint, StoragePath = finalStorageKey,
                FileName = finalFileName, ContentType = "application/pdf", SizeBytes = 8,
                State = PendingFileUploadState.Prepared, CreatedAtUtc = DateTime.UtcNow,
                UpdatedAtUtc = DateTime.UtcNow,
            });
            await arrangeFinal.SaveChangesAsync();
        }
        var finalize = new FinalizeNativeEsignRequestCommand(
            pendingId, finalFingerprint, finalScenario.SignatureRequestId,
            finalScenario.SignatureRequestPublicId, claimToken, finalStorageKey,
            finalFileName, 8, finalSha);
        var finalized = await ExecuteAsync("executor-finalize", finalize);
        var finalizeReplay = await ExecuteAsync("executor-finalize", finalize with { });
        finalized.Disposition.Should().Be(AtomicCommandDisposition.Executed);
        finalizeReplay.Disposition.Should().Be(AtomicCommandDisposition.Replayed);

        await using var verify = NewContext();
        (await verify.Set<SignatureAuditEvent>().CountAsync(row =>
            row.SignatureSignerId == viewScenario.SignatureSignerId
            && row.Type == SignatureAuditEventType.Viewed)).Should().Be(1);
        (await verify.Set<SignatureAuditEvent>().CountAsync(row =>
            row.SignatureSignerId == declineScenario.SignatureSignerId
            && row.Type == SignatureAuditEventType.Declined)).Should().Be(1);
        (await verify.Set<SignatureAuditEvent>().CountAsync(row =>
            row.SignatureRequestId == finalScenario.SignatureRequestId
            && row.Type == SignatureAuditEventType.Completed)).Should().Be(1);
    }

    [SkippableFact]
    public async Task LegacyKernelReceipts_ReplayThroughMigratedExecutorForEveryResultShape()
    {
        SkipIfNoDocker();
        var signerScenario = await SeedScenarioAsync("legacy-signer-receipts", 0m);
        var signerAt = DateTime.UtcNow;

        var view = new RecordNativeEsignViewCommand(
            signerScenario.TokenHash, "127.0.0.1", "legacy-agent", signerAt);
        var storedView = new RecordNativeEsignViewResult(
            NativeEsignViewOutcome.Available, null, signerScenario.SignatureRequestId);
        await AssertLegacyReplayAsync("legacy-view", view, storedView);

        var sign = new RecordNativeSignatureCommand(
            signerScenario.TokenHash, SignatureSignatureType.Typed, "Future Tenant",
            null, null, null, null, "127.0.0.1", "legacy-agent", signerAt);
        var storedSignerAction = new NativeSignerActionResult(
            NativeSignerActionOutcome.Applied, null, signerScenario.SignatureRequestId,
            signerScenario.SignatureRequestPublicId, signerScenario.LeaseAgreementId, null,
            SignatureSignerStatus.Signed, SignatureRequestStatus.ExecutionPending, true);
        // Sign and decline share NativeSignerActionResult; sign is the representative decode shape.
        await AssertLegacyReplayAsync("legacy-sign", sign, storedSignerAction);

        var declineScenario = await SeedScenarioAsync("legacy-decline-receipt", 0m, signed: false);
        await using (var arrange = NewContext())
        {
            var signer = await arrange.SignatureSigners.SingleAsync(row =>
                row.Id == declineScenario.SignatureSignerId);
            var request = await arrange.SignatureRequests.SingleAsync(row =>
                row.Id == declineScenario.SignatureRequestId);
            signer.Status = SignatureSignerStatus.Declined;
            request.Status = SignatureRequestStatus.Declined;
            await arrange.SaveChangesAsync();
        }
        var decline = new RecordNativeDeclineCommand(
            declineScenario.TokenHash, "Stored reason", "127.0.0.1", "legacy-agent", signerAt);
        await AssertLegacyReplayAsync("legacy-decline", decline,
            storedSignerAction with
            {
                SignatureRequestId = declineScenario.SignatureRequestId,
                PublicId = declineScenario.SignatureRequestPublicId,
                LeaseAgreementId = declineScenario.LeaseAgreementId,
                SignerStatus = SignatureSignerStatus.Declined,
                RequestStatus = SignatureRequestStatus.Declined,
                ExecutionRequired = false,
            });

        var completed = await SeedScenarioAsync("legacy-finalize-receipt", 0m, completed: true);
        await using var artifactDb = NewContext();
        var artifact = await artifactDb.LegalDocumentArtifacts.AsNoTracking()
            .SingleAsync(row => row.Id == completed.ExecutedArtifactId);
        var finalize = new FinalizeNativeEsignRequestCommand(
            Guid.NewGuid(), "legacy-finalize-fingerprint", completed.SignatureRequestId,
            completed.SignatureRequestPublicId, Guid.NewGuid(), artifact.StorageKey,
            artifact.FileName, artifact.ByteLength, artifact.ContentSha256);
        await AssertLegacyReplayAsync("legacy-finalize", finalize,
            new FinalizeNativeEsignRequestResult(completed.SignatureRequestPublicId,
                completed.SignatureRequestId, completed.LeaseAgreementId, null, completed.ExecutedArtifactId));

        var reconcile = new ReconcileNativeEsignAgreementFinancialsCommand(
            completed.SignatureRequestId, completed.SignatureRequestPublicId);
        await AssertLegacyReplayAsync("legacy-reconcile", reconcile,
            new ReconcileNativeEsignAgreementFinancialsResult(completed.SignatureRequestPublicId,
                completed.SignatureRequestId, completed.LeaseAgreementId, 7));

        var batch = new ReconcileNativeEsignAgreementFinancialsBatchCommand(Guid.NewGuid(), 20);
        await AssertLegacyReplayAsync("legacy-batch", batch,
            new ReconcileNativeEsignAgreementFinancialsBatchResult(11));
    }

    private async Task<AtomicLegalExecutionTransitionResult> ExecuteTransitionAsync(Scenario scenario)
    {
        await using var db = NewContext();
        var originalAutoSavepointsEnabled = db.Database.AutoSavepointsEnabled;
        try
        {
            // This direct persistence harness mirrors AtomicTransactionRunner: journal-line
            // provenance is bound to the owner transaction XID, not an EF savepoint subtransaction.
            db.Database.AutoSavepointsEnabled = false;
            await using var transaction = await db.Database.BeginTransactionAsync();
            var auditScope = new AtomicAuditScope(TimeProvider.System);
            var attemptId = Guid.NewGuid();
            var commandContext = new AtomicCommandContext(db, auditScope, TimeProvider.System);
            commandContext.BeginAttempt(attemptId);
            commandContext.BindReceipt(Guid.NewGuid());
            using var attempt = auditScope.BeginAttempt(
                new AtomicCommandIdentity("test.native-esign.transition", Guid.NewGuid().ToString("N")),
                attemptId,
                db);
            var result = await AtomicLeaseMutationPersistence.ExecuteLegalArtifactTransitionAsync(
                db,
                commandContext,
                scenario.PortfolioId,
                scenario.LeaseManagementId,
                scenario.LeaseAgreementId,
                null,
                scenario.ExecutedArtifactId,
                FrozenBusinessNow);
            await transaction.CommitAsync();
            commandContext.EndAttempt();
            return result;
        }
        finally
        {
            db.Database.AutoSavepointsEnabled = originalAutoSavepointsEnabled;
        }
    }

    private async Task<Scenario> SeedScenarioAsync(
        string suffix,
        decimal depositObligation,
        bool completed = false,
        bool voided = false,
        bool signed = true)
    {
        await using var db = NewContext();
        var sequence = Math.Abs(suffix.GetHashCode(StringComparison.Ordinal)) % 10_000;
        var now = DateTime.UtcNow;
        var portfolio = new Portfolio
        {
            Id = 50_000 + sequence,
            Name = $"E-sign Deposit {suffix}",
            ManagementCompanyName = "Rental Command",
            TimeZone = "America/New_York",
            Currency = "USD",
            CreatedAt = now,
            UpdatedAt = now,
        };
        var user = new ApplicationUser
        {
            Id = ActorUserId + sequence,
            UserName = $"esign-deposit-{suffix}",
            NormalizedUserName = $"ESIGN-DEPOSIT-{suffix}".ToUpperInvariant(),
            Email = $"esign-deposit-{suffix}@example.test",
            NormalizedEmail = $"ESIGN-DEPOSIT-{suffix}@EXAMPLE.TEST".ToUpperInvariant(),
            DisplayName = "E-sign Deposit User",
            SecurityStamp = Guid.NewGuid().ToString("N"),
            ConcurrencyStamp = Guid.NewGuid().ToString("N"),
            CreatedAt = now,
        };
        var property = new Property
        {
            Id = 51_000 + sequence,
            Portfolio = portfolio,
            Name = $"Property {suffix}",
            AddressLine1 = "1 Main",
            City = "Columbus",
            State = "OH",
            PostalCode = "43215",
            CreatedAt = now,
            UpdatedAt = now,
        };
        var unit = new Unit
        {
            Id = 52_000 + sequence,
            PortfolioId = portfolio.Id,
            Property = property,
            UnitNumber = "1",
            CreatedAt = now,
            UpdatedAt = now,
        };
        var template = new DocumentTemplate
        {
            Id = 53_000 + sequence,
            Portfolio = portfolio,
            Kind = DocumentTemplateKind.Lease,
            Status = DocumentTemplateStatus.Active,
            RenderMode = DocumentTemplateRenderMode.Overlay,
            Name = $"Lease {suffix}",
            Version = 1,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
        };
        var source = new LegalDocumentSourceVersion
        {
            Id = 54_000 + sequence,
            PublicId = Guid.NewGuid(),
            Portfolio = portfolio,
            SourceKind = LegalDocumentSourceKind.AuthoredTemplateSnapshot,
            BusinessKey = $"template:{suffix}:v1",
            DocumentTemplate = template,
            DocumentTemplateVersion = template.Version,
            RendererKey = "lease-agreement-overlay",
            RendererVersion = 1,
            SnapshotPayload = "{}",
            CreatedAtUtc = now,
            CreatedByUserId = user.Id,
        };
        var relationship = new LeaseManagement
        {
            Id = 55_000 + sequence,
            PublicId = Guid.NewGuid(),
            Portfolio = portfolio,
            Property = property,
            Unit = unit,
            RelationshipNumber = $"REL-{suffix}",
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
            CreatedByUserId = user.Id,
            RowVersion = Guid.NewGuid(),
        };
        var account = new TenantAccount
        {
            Id = 56_000 + sequence,
            PublicId = Guid.NewGuid(),
            Portfolio = portfolio,
            LeaseManagement = relationship,
            AccountNumber = $"TA-{suffix}",
            Currency = "USD",
            OpenedAtUtc = now,
            CreatedAtUtc = now,
            CreatedByUserId = user.Id,
        };
        var agreementPublicId = Guid.NewGuid();
        var agreement = new LeaseAgreement
        {
            Id = 57_000 + sequence,
            PublicId = agreementPublicId,
            Portfolio = portfolio,
            LeaseManagement = relationship,
            VersionNumber = 1,
            AgreementNumber = $"AGR-{suffix}",
            ChangeType = LeaseAgreementChangeType.Initial,
            TermType = LeaseAgreementTermType.FixedTerm,
            TermStartOn = new DateOnly(2027, 2, 1),
            TermEndOn = new DateOnly(2028, 1, 31),
            GoverningFromOn = new DateOnly(2027, 2, 1),
            BaseRentAmount = 1_000,
            RentDueDay = 1,
            SecurityDepositObligation = depositObligation,
            LateFeeAmount = 50,
            GracePeriodDays = 5,
            Currency = "USD",
            TermsSchemaVersion = 1,
            TermsPayload = "{}",
            DocumentSourceVersion = source,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
            CreatedByUserId = user.Id,
        };
        var issuedStored = StoredFile(58_000 + sequence, portfolio, agreement.Id, $"issued-{suffix}.pdf", now);
        var issuedArtifact = Artifact(
            59_000 + sequence, portfolio, user.Id, issuedStored, LegalDocumentArtifactKind.IssuedAgreement, now);
        var executedStored = StoredFile(60_000 + sequence, portfolio, agreement.Id, $"executed-{suffix}.pdf", now);
        var executedArtifact = Artifact(
            61_000 + sequence, portfolio, user.Id, executedStored, LegalDocumentArtifactKind.ExecutedAgreement, now);
        var agreementSigner = new LeaseAgreementSigner
        {
            Id = 62_000 + sequence,
            Portfolio = portfolio,
            LeaseAgreement = agreement,
            SignerRole = LeaseLegalSignerRole.PrimaryTenant,
            NameSnapshot = "Future Tenant",
            EmailSnapshot = $"tenant-{suffix}@example.test",
            SigningOrder = 1,
            IsRequired = true,
        };
        var requestPublicId = Guid.NewGuid();
        var tokenHash = Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(suffix)))
            .ToLowerInvariant();
        var request = new SignatureRequest
        {
            Id = 63_000 + sequence,
            Portfolio = portfolio,
            PublicId = requestPublicId,
            LeaseAgreement = agreement,
            Provider = "native",
            IdempotencyKey = $"request-{suffix}",
            Status = completed ? SignatureRequestStatus.Completed : SignatureRequestStatus.ExecutionPending,
            Subject = $"Agreement {suffix}",
            IssuedArtifact = issuedArtifact,
            IssuedArtifactId = issuedArtifact.Id,
            ExecutedArtifact = completed ? executedArtifact : null,
            ExecutedArtifactId = completed ? executedArtifact.Id : null,
            PreparedAtUtc = now,
            ProviderAcceptedAtUtc = now,
            CompletedAtUtc = completed ? now : null,
            CreatedByUserId = user.Id,
        };
        var signer = new SignatureSigner
        {
            Id = 64_000 + sequence,
            PortfolioId = portfolio.Id,
            SignatureRequest = request,
            AgreementSigner = agreementSigner,
            NameSnapshot = agreementSigner.NameSnapshot,
            EmailSnapshot = agreementSigner.EmailSnapshot,
            SigningOrder = 1,
            IsRequired = true,
            TokenHash = tokenHash,
            TokenExpiresAtUtc = DateTime.UtcNow.AddDays(1),
            Status = signed ? SignatureSignerStatus.Signed : SignatureSignerStatus.Pending,
            SignatureType = signed ? SignatureSignatureType.Typed : SignatureSignatureType.None,
            TypedName = signed ? "Future Tenant" : null,
            ConsentGivenAtUtc = signed ? now : null,
            SignedAtUtc = signed ? now : null,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
        };

        db.AddRange(user, portfolio, property, unit, template, source, relationship, account,
            issuedStored, issuedArtifact, executedStored, executedArtifact, agreement,
            agreementSigner, request, signer);
        await db.SaveChangesAsync();

        await new ChartOfAccountsSeedService(db).SeedAsync(portfolio.Id);
        await db.SaveChangesAsync();

        agreement.IssuedArtifactId = issuedArtifact.Id;
        agreement.IssuedAtUtc = now;
        if (completed)
        {
            agreement.ExecutedArtifactId = executedArtifact.Id;
            agreement.FullyExecutedAtUtc = now;
        }
        if (voided)
        {
            agreement.VoidedAtUtc = now;
            agreement.VoidReasonCode = "test";
        }
        await db.SaveChangesAsync();

        return new Scenario(
            portfolio.Id,
            relationship.Id,
            account.Id,
            agreement.Id,
            agreementPublicId,
            request.Id,
            requestPublicId,
            signer.Id,
            tokenHash,
            executedArtifact.Id);
    }

    private static StoredFile StoredFile(
        int id,
        Portfolio portfolio,
        int agreementId,
        string fileName,
        DateTime now) => new()
    {
        Id = id,
        Portfolio = portfolio,
        FileName = fileName,
        FilePath = $"agreements/{fileName}",
        ContentType = "application/pdf",
        FileSize = 1,
        EntityType = nameof(LeaseAgreement),
        EntityId = agreementId,
        UploadedAt = now,
    };

    private static LegalDocumentArtifact Artifact(
        int id,
        Portfolio portfolio,
        int userId,
        StoredFile storedFile,
        LegalDocumentArtifactKind kind,
        DateTime now) => new()
    {
        Id = id,
        PublicId = Guid.NewGuid(),
        Portfolio = portfolio,
        StoredFile = storedFile,
        ArtifactKind = kind,
        StorageKey = storedFile.FilePath,
        FileName = storedFile.FileName,
        ContentType = "application/pdf",
        ByteLength = 1,
        ContentSha256 = new string(kind == LegalDocumentArtifactKind.IssuedAgreement ? 'a' : 'b', 64),
        LegalIssuanceFingerprint = kind == LegalDocumentArtifactKind.IssuedAgreement ? new string('c', 64) : null,
        CreatedAtUtc = now,
        CreatedByUserId = userId,
    };

    private static async Task FreezeClockAsync(RentalCommandDbContext db)
    {
        var clock = await db.SimulationClocks.SingleAsync(row => row.Id == 1);
        clock.Mode = ClockMode.Frozen;
        clock.SimAnchorUtc = FrozenBusinessNow;
        clock.RealAnchorUtc = DateTime.UtcNow;
        clock.TimeZoneId = "America/New_York";
        await db.SaveChangesAsync();
    }

    private RentalCommandDbContext NewContext() => new(
        new DbContextOptionsBuilder<RentalCommandDbContext>()
            .UseNpgsql(_postgres!.GetConnectionString())
            .AddInterceptors(_commandCounter)
            .Options);

    private IWriteExecutor Writes =>
        _serviceScope!.ServiceProvider.GetRequiredService<IWriteExecutor>();

    private RentalCommandDbContext ScopedDb =>
        _serviceScope!.ServiceProvider.GetRequiredService<RentalCommandDbContext>();

    private Task<AtomicCommandOutcome<RecordNativeEsignViewResult>> ExecuteAsync(
        string key,
        RecordNativeEsignViewCommand command) =>
        Writes.ExecuteAsync(key,
            NativeEsignWriteSupport.Write<RecordNativeEsignViewCommand,
                RecordNativeEsignViewResult>(ScopedDb, command));

    private Task<AtomicCommandOutcome<NativeSignerActionResult>> ExecuteAsync(
        string key,
        RecordNativeDeclineCommand command) =>
        Writes.ExecuteAsync(key,
            NativeEsignWriteSupport.Write<RecordNativeDeclineCommand,
                NativeSignerActionResult>(ScopedDb, command));

    private Task<AtomicCommandOutcome<FinalizeNativeEsignRequestResult>> ExecuteAsync(
        string key,
        FinalizeNativeEsignRequestCommand command) =>
        Writes.ExecuteAsync(key,
            NativeEsignWriteSupport.Write<FinalizeNativeEsignRequestCommand,
                FinalizeNativeEsignRequestResult>(ScopedDb, command));

    private async Task AssertLegacyReplayAsync<TCommand, TResult>(
        string key,
        TCommand command,
        TResult stored)
        where TCommand : notnull, IAtomicCommandData
        where TResult : notnull
    {
        var write = NativeEsignWriteSupport.Write<TCommand, TResult>(ScopedDb, command);
        var codec = new AtomicJsonResultCodec<TResult>(write.ResultContract);
        await using (var seed = NewContext())
        {
            seed.AtomicCommandReceipts.Add(new AtomicCommandReceipt
            {
                Id = Guid.NewGuid(),
                AttemptId = Guid.NewGuid(),
                CommandType = write.OperationName,
                IdempotencyKey = key,
                RequestFingerprint = AtomicCommandFingerprint.Create(command),
                Status = AtomicCommandReceiptStatus.Completed,
                ResultContract = write.ResultContract,
                ResultJson = codec.Serialize(stored),
                StartedAt = FrozenBusinessNow,
                CompletedAt = FrozenBusinessNow,
            });
            await seed.SaveChangesAsync();
        }

        var replay = await Writes.ExecuteAsync(key, write);

        replay.Disposition.Should().Be(AtomicCommandDisposition.Replayed);
        replay.Value.Should().BeEquivalentTo(stored);
    }

    private static async Task<int> CountDepositChargeOutboxMessagesAsync(
        RentalCommandDbContext db,
        IReadOnlyList<int> tenantAccountIds)
    {
        var rows = await db.Database.SqlQuery<int>($"""
            SELECT COUNT(*)::integer AS "Value"
            FROM "OutboxMessages"
            WHERE "MessageType" = 'data-update'
              AND "Payload"::text LIKE '%"EntryType": "DepositCharge"%'
              AND EXISTS (
                  SELECT 1
                  FROM unnest({tenantAccountIds.ToArray()}) AS account("Id")
                  WHERE "Payload"::text LIKE
                      '%"TenantAccountId": ' || account."Id"::text || '%')
            """).ToListAsync();
        return rows.Single();
    }

    private void SkipIfNoDocker() =>
        Skip.IfNot(_dockerAvailable, "Docker is unavailable.");

    private sealed class TestActor : ICurrentActor
    {
        public int? UserId => ActorUserId;
        public string? ActorLabel => null;
        public string? IpAddress => "127.0.0.1";
    }

    private sealed record Scenario(
        int PortfolioId,
        int LeaseManagementId,
        int TenantAccountId,
        int LeaseAgreementId,
        Guid AgreementPublicId,
        int SignatureRequestId,
        Guid SignatureRequestPublicId,
        int SignatureSignerId,
        string TokenHash,
        int ExecutedArtifactId);

    private sealed class DepositBatchCommandCounter : DbCommandInterceptor
    {
        public int DepositBatchMutationCommandCount { get; private set; }

        public void Reset() => DepositBatchMutationCommandCount = 0;

        public override InterceptionResult<DbDataReader> ReaderExecuting(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result)
        {
            Count(command);
            return base.ReaderExecuting(command, eventData, result);
        }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            Count(command);
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }

        private void Count(DbCommand command)
        {
            if (command.CommandText.Contains("FROM \"SignatureRequests\" AS request", StringComparison.Ordinal)
                && command.CommandText.Contains("INSERT INTO \"TenantLedgerEntries\"", StringComparison.Ordinal)
                && command.CommandText.Contains("INSERT INTO \"OutboxMessages\"", StringComparison.Ordinal)
                && command.CommandText.Contains("request.\"Status\" = 'Completed'", StringComparison.Ordinal))
            {
                DepositBatchMutationCommandCount++;
            }
        }
    }
}
