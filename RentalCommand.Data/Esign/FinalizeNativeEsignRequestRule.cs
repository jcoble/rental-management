using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using RentalCommand.Core;
using RentalCommand.Core.Atomic;
using RentalCommand.Core.Entities;
using RentalCommand.Data.Leasing;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Esign;
using RentalCommand.Core.Leasing;

namespace RentalCommand.Data.Esign;

public sealed class FinalizeNativeEsignRequestRule
{
    private readonly RentalCommandDbContext _db;

    public FinalizeNativeEsignRequestRule(RentalCommandDbContext db) => _db = db;

    public async Task<FinalizeNativeEsignRequestResult> ExecuteAsync(
        FinalizeNativeEsignRequestCommand command, IAtomicCommandContext context, CancellationToken ct)
    {
        var request = await _db.Set<SignatureRequest>()
            .Include(item => item.LeaseAgreement)
            .Include(item => item.LeaseAddendum)
            .SingleOrDefaultAsync(item => item.Id == command.SignatureRequestId && item.PublicId == command.PublicId, ct)
            ?? throw new InvalidOperationException("The canonical signature request no longer exists.");
        if ((request.LeaseAgreement is null) == (request.LeaseAddendum is null))
            throw new DomainValidationException("A signature packet must identify exactly one Agreement or Addendum.");
        var leaseManagementId = request.LeaseAgreement?.LeaseManagementId
            ?? request.LeaseAddendum!.LeaseManagementId;
        await context.AcquireLockAsync("LeaseManagement", leaseManagementId, ct);
        if (request.Status == SignatureRequestStatus.Completed && request.ExecutedArtifactId.HasValue)
        {
            if (request.LeaseAgreementId is { } completedAgreementId)
            {
                var completedTimes = await RentalCommand.Data.AtomicCommandClock.ReadCommandTimesAsync(_db, request.PortfolioId, ct);
                await AtomicLeaseMutationPersistence.ReconcileInitialSecurityDepositChargeAsync(_db,
                    context, request.PortfolioId, completedAgreementId, completedTimes.EffectiveNowUtc, ct);
            }
            return new(request.PublicId, request.Id, request.LeaseAgreementId, request.LeaseAddendumId,
                request.ExecutedArtifactId.Value);
        }
        if (request.Status != SignatureRequestStatus.ExecutionPending)
            throw new DomainValidationException("The signature request is not ready for execution.");

        var times = await RentalCommand.Data.AtomicCommandClock.ReadCommandTimesAsync(_db, request.PortfolioId, ct);
        var securityNowUtc = times.WallClockUtc;
        var businessNowUtc = times.EffectiveNowUtc;
        if (request.ExecutionClaimToken != command.ClaimToken || request.ExecutionClaimExpiresAtUtc <= securityNowUtc)
            throw new NativeEsignExecutionClaimLostException(request.Id);
        if (await _db.Set<SignatureSigner>().AnyAsync(signer => signer.SignatureRequestId == request.Id
                && signer.IsRequired && signer.Status != SignatureSignerStatus.Signed, ct))
            throw new DomainValidationException("Every required signer must sign before execution.");

        var pending = await _db.Set<PendingFileUpload>()
            .SingleOrDefaultAsync(upload => upload.Id == command.PendingUploadId
                && upload.PortfolioId == request.PortfolioId && upload.State == PendingFileUploadState.Prepared
                && upload.CleanupClaimToken == null && upload.RequestFingerprint == command.RequestFingerprint, ct)
            ?? throw new DomainValidationException("The executed PDF admission is missing or changed.");
        if (pending.StoragePath != command.StorageKey || pending.FileName != command.FileName
            || pending.ContentType != "application/pdf")
            throw new DomainValidationException("The executed PDF does not match its admitted storage metadata.");

        var storedFile = new StoredFile
        {
            PortfolioId = request.PortfolioId,
            FileName = command.FileName,
            FilePath = command.StorageKey,
            ContentType = "application/pdf",
            FileSize = command.FileSize,
            EntityType = request.LeaseAgreementId.HasValue ? nameof(LeaseAgreement) : nameof(LeaseAddendum),
            EntityId = request.LeaseAgreementId ?? request.LeaseAddendumId!.Value,
            UploadedAt = businessNowUtc,
        };
        _db.Add(storedFile);
        await context.FlushBusinessAsync(ct);
        var artifact = new LegalDocumentArtifact
        {
            PortfolioId = request.PortfolioId,
            StoredFileId = storedFile.Id,
            ArtifactKind = request.LeaseAgreementId.HasValue
                ? LegalDocumentArtifactKind.ExecutedAgreement
                : LegalDocumentArtifactKind.ExecutedAddendum,
            StorageKey = command.StorageKey,
            FileName = command.FileName,
            ContentType = "application/pdf",
            ByteLength = command.FileSize,
            ContentSha256 = command.ContentSha256,
            CreatedAtUtc = businessNowUtc,
            CreatedByUserId = request.CreatedByUserId,
        };
        _db.Add(artifact);
        await context.FlushBusinessAsync(ct);

        pending.State = PendingFileUploadState.Finalized;
        pending.StoredFileId = storedFile.Id;
        pending.SizeBytes = command.FileSize;
        pending.UpdatedAtUtc = businessNowUtc;
        request.ExecutedArtifactId = artifact.Id;
        request.CompletedAtUtc = businessNowUtc;
        request.Status = SignatureRequestStatus.Completed;
        request.ExecutionClaimOwner = null;
        request.ExecutionClaimToken = null;
        request.ExecutionClaimExpiresAtUtc = null;
        request.LastError = null;
        var transition = await AtomicLeaseMutationPersistence.ExecuteLegalArtifactTransitionAsync(_db,
            context, request.PortfolioId,
            leaseManagementId,
            request.LeaseAgreementId,
            request.LeaseAddendumId,
            artifact.Id,
            businessNowUtc,
            ct);
        if (transition.Outcome != AtomicLegalExecutionTransitionOutcome.Applied)
        {
            throw new NativeEsignLegalTransitionConflictException(request.Id, transition.Outcome);
        }

        if (request.LeaseAgreementId is { } agreementId)
        {
            context.StageSemanticEvent(new AtomicSemanticAudit(request.PortfolioId,
                nameof(LeaseAgreement), agreementId, AuditLogOperation.Updated, ActorLabel: "esign-system",
                NewValues: JsonSerializer.Serialize(new { ExecutedArtifactId = artifact.Id, FullyExecutedAtUtc = businessNowUtc }),
                ChangeReason: "Finalized immutable executed Agreement artifact."), businessNowUtc);
            if (transition.PredecessorId is { } predecessorId)
            {
                context.StageSemanticEvent(new AtomicSemanticAudit(request.PortfolioId,
                    nameof(LeaseAgreement), predecessorId, AuditLogOperation.Updated, ActorLabel: "esign-system",
                    NewValues: JsonSerializer.Serialize(new
                    {
                        SupersededEffectiveOn = request.LeaseAgreement!.GoverningFromOn,
                        SupersededByAgreementId = agreementId,
                    }),
                    ChangeReason: "Executed successor Agreement recorded its governing transition."), businessNowUtc);
            }
        }
        else
        {
            var addendumId = request.LeaseAddendumId!.Value;
            context.StageSemanticEvent(new AtomicSemanticAudit(request.PortfolioId,
                nameof(LeaseAddendum), addendumId, AuditLogOperation.Updated, ActorLabel: "esign-system",
                NewValues: JsonSerializer.Serialize(new { ExecutedArtifactId = artifact.Id, FullyExecutedAtUtc = businessNowUtc }),
                ChangeReason: "Finalized immutable executed Addendum artifact."), businessNowUtc);
        }
        foreach (var supersededAddendumId in transition.SupersededAddendumIds)
        {
            context.StageSemanticEvent(new AtomicSemanticAudit(request.PortfolioId,
                nameof(LeaseAddendum), supersededAddendumId, AuditLogOperation.Updated,
                ActorLabel: "esign-system",
                ChangeReason: "Applied the executed legal artifact's atomic Addendum supersession transition."), businessNowUtc);
        }
        if (request.LeaseAgreement?.ChangeType is LeaseAgreementChangeType.Renewal
            or LeaseAgreementChangeType.MonthToMonth)
        {
            foreach (var reissuedAddendumId in transition.ReissuedAddendumIds)
            {
                context.StageSemanticEvent(new AtomicSemanticAudit(request.PortfolioId,
                    nameof(LeaseAddendum), reissuedAddendumId, AuditLogOperation.Updated,
                    ActorLabel: "esign-system",
                    ChangeReason: "Activated the fully executed Addendum reissue with its executed base renewal."), businessNowUtc);
            }
        }

        _db.Add(new SignatureAuditEvent
        {
            PortfolioId = request.PortfolioId,
            SignatureRequestId = request.Id,
            Type = SignatureAuditEventType.Completed,
            OccurredAtUtc = businessNowUtc,
            Detail = $"Every required signer signed; executed artifact SHA-256 {command.ContentSha256}.",
        });
        context.StageSemanticEvent(new AtomicSemanticAudit(request.PortfolioId,
            nameof(SignatureRequest), request.Id, AuditLogOperation.Updated, ActorLabel: "esign-system",
            NewValues: JsonSerializer.Serialize(new { Status = request.Status.ToString(), request.ExecutedArtifactId, request.CompletedAtUtc }),
            ChangeReason: "Completed canonical legal-artifact signature packet."), businessNowUtc);
        context.StageOutbox(new OutboxMessage
        {
            PortfolioId = request.PortfolioId,
            MessageType = "data-update",
            Payload = JsonSerializer.Serialize(new
            {
                entityType = request.LeaseAgreementId.HasValue ? nameof(LeaseAgreement) : nameof(LeaseAddendum),
                entityId = request.LeaseAgreementId ?? request.LeaseAddendumId,
                leaseManagementId,
                action = request.LeaseAgreementId.HasValue ? "agreement-executed" : "addendum-executed",
            }),
            IdempotencyKey = $"legal-artifact:{request.Id}:executed:{artifact.Id}",
            CreatedAtUtc = businessNowUtc,
            NextAttemptAtUtc = businessNowUtc,
        });
        return new(request.PublicId, request.Id, request.LeaseAgreementId, request.LeaseAddendumId, artifact.Id);
    }

    public async Task AuthorizeReplayAsync(
        FinalizeNativeEsignRequestCommand command,
        IAtomicCommandContext context,
        CancellationToken ct) => throw NativeEsignWriteSupport.RetiredPath();

    public async Task AuthorizeAsync(
        FinalizeNativeEsignRequestCommand command,
        IAtomicCommandContext context,
        CancellationToken ct)
    {
        Validate(command);

        var finalizedRequestExists = await _db.Set<SignatureRequest>()
            .AsNoTracking()
            .AnyAsync(request =>
                request.Id == command.SignatureRequestId &&
                request.PublicId == command.PublicId &&
                request.Status == SignatureRequestStatus.Completed &&
                request.ExecutedArtifactId != null &&
                _db.Set<LegalDocumentArtifact>().Any(artifact =>
                    artifact.Id == request.ExecutedArtifactId &&
                    artifact.StorageKey == command.StorageKey &&
                    artifact.FileName == command.FileName &&
                    artifact.ContentType == "application/pdf" &&
                    artifact.ByteLength == command.FileSize &&
                    artifact.ContentSha256 == command.ContentSha256),
                ct);
        if (!finalizedRequestExists)
        {
            throw new UnauthorizedAccessException("The finalized native e-sign request is unavailable.");
        }
    }

    private static void Validate(FinalizeNativeEsignRequestCommand command)
    {
        if (command.PendingUploadId == Guid.Empty ||
            command.SignatureRequestId <= 0 ||
            command.PublicId == Guid.Empty ||
            command.ClaimToken == Guid.Empty ||
            command.FileSize <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(command));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(command.RequestFingerprint);
        ArgumentException.ThrowIfNullOrWhiteSpace(command.StorageKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(command.FileName);
        ArgumentException.ThrowIfNullOrWhiteSpace(command.ContentSha256);
    }
}
