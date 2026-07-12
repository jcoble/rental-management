using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using RentalCommand.Core;
using RentalCommand.Core.Atomic;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Esign;

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
            .SingleOrDefaultAsync(item => item.Id == command.SignatureRequestId && item.PublicId == command.PublicId, ct)
            ?? throw new InvalidOperationException("The canonical signature request no longer exists.");
        if (request.LeaseAgreementId is null || request.LeaseAgreement is null)
            throw new DomainValidationException("This execution path only accepts Agreement signature packets.");
        if (request.Status == SignatureRequestStatus.Completed && request.ExecutedArtifactId.HasValue)
            return new(request.PublicId, request.Id, request.LeaseAgreementId.Value, request.ExecutedArtifactId.Value);
        if (request.Status != SignatureRequestStatus.ExecutionPending)
            throw new DomainValidationException("The signature request is not ready for execution.");

        var now = await attempt.Persistence.ReadDatabaseClockUtcAsync(ct);
        if (request.ExecutionClaimToken != command.ClaimToken || request.ExecutionClaimExpiresAtUtc <= now)
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
            || pending.ContentType != "application/pdf" || pending.SizeBytes != command.FileSize)
            throw new DomainValidationException("The executed PDF does not match its admitted storage metadata.");

        var storedFile = new StoredFile
        {
            PortfolioId = request.PortfolioId,
            FileName = command.FileName,
            FilePath = command.StorageKey,
            ContentType = "application/pdf",
            FileSize = command.FileSize,
            EntityType = nameof(LeaseAgreement),
            EntityId = request.LeaseAgreementId.Value,
            UploadedAt = now,
        };
        attempt.Persistence.Add(storedFile);
        await attempt.FlushBusinessAsync(ct);
        var artifact = new LegalDocumentArtifact
        {
            PortfolioId = request.PortfolioId,
            StoredFileId = storedFile.Id,
            ArtifactKind = LegalDocumentArtifactKind.ExecutedAgreement,
            StorageKey = command.StorageKey,
            FileName = command.FileName,
            ContentType = "application/pdf",
            ByteLength = command.FileSize,
            ContentSha256 = command.ContentSha256,
            CreatedAtUtc = now,
            CreatedByUserId = request.CreatedByUserId,
        };
        attempt.Persistence.Add(artifact);
        await attempt.FlushBusinessAsync(ct);

        pending.State = PendingFileUploadState.Finalized;
        pending.StoredFileId = storedFile.Id;
        pending.UpdatedAtUtc = now;
        request.ExecutedArtifactId = artifact.Id;
        request.CompletedAtUtc = now;
        request.Status = SignatureRequestStatus.Completed;
        request.ExecutionClaimOwner = null;
        request.ExecutionClaimToken = null;
        request.ExecutionClaimExpiresAtUtc = null;
        request.LastError = null;
        var agreement = request.LeaseAgreement;
        agreement.ExecutedArtifactId = artifact.Id;
        agreement.FullyExecutedAtUtc = now;
        agreement.UpdatedAtUtc = now;

        var predecessorId = agreement.ReplacesAgreementId ?? agreement.RenewsAgreementId;
        if (predecessorId.HasValue)
        {
            var predecessor = await attempt.Persistence.Query<LeaseAgreement>()
                .SingleAsync(item => item.Id == predecessorId && item.LeaseManagementId == agreement.LeaseManagementId
                    && item.PortfolioId == request.PortfolioId, ct);
            if (predecessor.SupersededByAgreementId.HasValue && predecessor.SupersededByAgreementId != agreement.Id)
                throw new DomainValidationException("A different Agreement successor already governs this relationship.");
            predecessor.SupersededEffectiveOn = agreement.GoverningFromOn;
            predecessor.SupersededByAgreementId = agreement.Id;
            predecessor.SupersessionRecordedAtUtc = now;
            predecessor.UpdatedAtUtc = now;
            attempt.BindSemanticAudit(predecessor, new AtomicSemanticAudit(request.PortfolioId,
                nameof(LeaseAgreement), predecessor.Id, AuditLogOperation.Updated, ActorLabel: "esign-system",
                NewValues: JsonSerializer.Serialize(new { predecessor.SupersededEffectiveOn, predecessor.SupersededByAgreementId }),
                ChangeReason: "Executed successor Agreement recorded its governing transition."));
        }

        attempt.Persistence.Add(new SignatureAuditEvent
        {
            PortfolioId = request.PortfolioId,
            SignatureRequestId = request.Id,
            Type = SignatureAuditEventType.Completed,
            OccurredAtUtc = now,
            Detail = $"Every required signer signed; executed artifact SHA-256 {command.ContentSha256}.",
        });
        attempt.BindSemanticAudit(agreement, new AtomicSemanticAudit(request.PortfolioId,
            nameof(LeaseAgreement), agreement.Id, AuditLogOperation.Updated, ActorLabel: "esign-system",
            NewValues: JsonSerializer.Serialize(new { agreement.ExecutedArtifactId, agreement.FullyExecutedAtUtc }),
            ChangeReason: "Finalized immutable executed Agreement artifact."));
        attempt.StageSemanticEvent(new AtomicSemanticAudit(request.PortfolioId,
            nameof(SignatureRequest), request.Id, AuditLogOperation.Updated, ActorLabel: "esign-system",
            NewValues: JsonSerializer.Serialize(new { Status = request.Status.ToString(), request.ExecutedArtifactId, request.CompletedAtUtc }),
            ChangeReason: "Completed canonical Agreement signature packet."), now);
        attempt.StageOutbox(new OutboxMessage
        {
            PortfolioId = request.PortfolioId,
            MessageType = "entity-update",
            Payload = JsonSerializer.Serialize(new { entityType = nameof(LeaseAgreement), entityId = agreement.Id,
                leaseManagementId = agreement.LeaseManagementId, action = "agreement-executed" }),
            IdempotencyKey = $"agreement:{agreement.Id}:executed:{artifact.Id}",
            CreatedAtUtc = now,
            NextAttemptAtUtc = now,
        });
        return new(request.PublicId, request.Id, agreement.Id, artifact.Id);
    }
}
