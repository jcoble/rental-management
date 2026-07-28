using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using RentalCommand.Core;
using RentalCommand.Core.Atomic;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Esign;
using RentalCommand.Core.Leasing;

namespace RentalCommand.Data.Esign;

public sealed class FinalizeNativeEsignRequestHandler
    : IAtomicCommandHandler<FinalizeNativeEsignRequestCommand, FinalizeNativeEsignRequestResult>
{
    public async Task<FinalizeNativeEsignRequestResult> HandleAsync(
        FinalizeNativeEsignRequestCommand command, IAtomicWriteAttempt attempt, CancellationToken ct)
    {
        await attempt.Locking.AcquireAsync(AtomicLockResource.SignatureRequest, command.SignatureRequestId, ct);
        var request = await attempt.Persistence.Query<SignatureRequest>()
            .Include(item => item.LeaseAgreement)
            .Include(item => item.LeaseAddendum)
            .SingleOrDefaultAsync(item => item.Id == command.SignatureRequestId && item.PublicId == command.PublicId, ct)
            ?? throw new InvalidOperationException("The canonical signature request no longer exists.");
        if ((request.LeaseAgreement is null) == (request.LeaseAddendum is null))
            throw new DomainValidationException("A signature packet must identify exactly one Agreement or Addendum.");
        var leaseManagementId = request.LeaseAgreement?.LeaseManagementId
            ?? request.LeaseAddendum!.LeaseManagementId;
        await attempt.Locking.AcquireAsync(AtomicLockResource.LeaseManagement, leaseManagementId, ct);
        if (request.Status == SignatureRequestStatus.Completed && request.ExecutedArtifactId.HasValue)
        {
            if (request.LeaseAgreementId is { } completedAgreementId)
            {
                var completedTimes = await attempt.Persistence.ReadCommandTimesAsync(request.PortfolioId, ct);
                await attempt.Leasing.ReconcileInitialSecurityDepositChargeAsync(
                    request.PortfolioId, completedAgreementId, completedTimes.EffectiveNowUtc, ct);
            }
            return new(request.PublicId, request.Id, request.LeaseAgreementId, request.LeaseAddendumId,
                request.ExecutedArtifactId.Value);
        }
        if (request.Status != SignatureRequestStatus.ExecutionPending)
            throw new DomainValidationException("The signature request is not ready for execution.");

        var times = await attempt.Persistence.ReadCommandTimesAsync(request.PortfolioId, ct);
        var securityNowUtc = times.WallClockUtc;
        var businessNowUtc = times.EffectiveNowUtc;
        if (request.ExecutionClaimToken != command.ClaimToken || request.ExecutionClaimExpiresAtUtc <= securityNowUtc)
            throw new NativeEsignExecutionClaimLostException(request.Id);
        if (await attempt.Persistence.Query<SignatureSigner>().AnyAsync(signer => signer.SignatureRequestId == request.Id
                && signer.IsRequired && signer.Status != SignatureSignerStatus.Signed, ct))
            throw new DomainValidationException("Every required signer must sign before execution.");

        var pending = await attempt.Persistence.Query<PendingFileUpload>()
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
        attempt.Persistence.Add(storedFile);
        await attempt.FlushBusinessAsync(ct);
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
        attempt.Persistence.Add(artifact);
        await attempt.FlushBusinessAsync(ct);

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
        var transition = await attempt.Leasing.ExecuteLegalArtifactTransitionAsync(
            request.PortfolioId,
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
            attempt.StageSemanticEvent(new AtomicSemanticAudit(request.PortfolioId,
                nameof(LeaseAgreement), agreementId, AuditLogOperation.Updated, ActorLabel: "esign-system",
                NewValues: JsonSerializer.Serialize(new { ExecutedArtifactId = artifact.Id, FullyExecutedAtUtc = businessNowUtc }),
                ChangeReason: "Finalized immutable executed Agreement artifact."), businessNowUtc);
            if (transition.PredecessorId is { } predecessorId)
            {
                attempt.StageSemanticEvent(new AtomicSemanticAudit(request.PortfolioId,
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
            attempt.StageSemanticEvent(new AtomicSemanticAudit(request.PortfolioId,
                nameof(LeaseAddendum), addendumId, AuditLogOperation.Updated, ActorLabel: "esign-system",
                NewValues: JsonSerializer.Serialize(new { ExecutedArtifactId = artifact.Id, FullyExecutedAtUtc = businessNowUtc }),
                ChangeReason: "Finalized immutable executed Addendum artifact."), businessNowUtc);
        }
        foreach (var supersededAddendumId in transition.SupersededAddendumIds)
        {
            attempt.StageSemanticEvent(new AtomicSemanticAudit(request.PortfolioId,
                nameof(LeaseAddendum), supersededAddendumId, AuditLogOperation.Updated,
                ActorLabel: "esign-system",
                ChangeReason: "Applied the executed legal artifact's atomic Addendum supersession transition."), businessNowUtc);
        }
        if (request.LeaseAgreement?.ChangeType is LeaseAgreementChangeType.Renewal
            or LeaseAgreementChangeType.MonthToMonth)
        {
            foreach (var reissuedAddendumId in transition.ReissuedAddendumIds)
            {
                attempt.StageSemanticEvent(new AtomicSemanticAudit(request.PortfolioId,
                    nameof(LeaseAddendum), reissuedAddendumId, AuditLogOperation.Updated,
                    ActorLabel: "esign-system",
                    ChangeReason: "Activated the fully executed Addendum reissue with its executed base renewal."), businessNowUtc);
            }
        }

        attempt.Persistence.Add(new SignatureAuditEvent
        {
            PortfolioId = request.PortfolioId,
            SignatureRequestId = request.Id,
            Type = SignatureAuditEventType.Completed,
            OccurredAtUtc = businessNowUtc,
            Detail = $"Every required signer signed; executed artifact SHA-256 {command.ContentSha256}.",
        });
        attempt.StageSemanticEvent(new AtomicSemanticAudit(request.PortfolioId,
            nameof(SignatureRequest), request.Id, AuditLogOperation.Updated, ActorLabel: "esign-system",
            NewValues: JsonSerializer.Serialize(new { Status = request.Status.ToString(), request.ExecutedArtifactId, request.CompletedAtUtc }),
            ChangeReason: "Completed canonical legal-artifact signature packet."), businessNowUtc);
        attempt.StageOutbox(new OutboxMessage
        {
            PortfolioId = request.PortfolioId,
            MessageType = "entity-update",
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
}
