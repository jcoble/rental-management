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
            .Include(item => item.LeaseAddendum)
            .SingleOrDefaultAsync(item => item.Id == command.SignatureRequestId && item.PublicId == command.PublicId, ct)
            ?? throw new InvalidOperationException("The canonical signature request no longer exists.");
        if ((request.LeaseAgreement is null) == (request.LeaseAddendum is null))
            throw new DomainValidationException("A signature packet must identify exactly one Agreement or Addendum.");
        if (request.Status == SignatureRequestStatus.Completed && request.ExecutedArtifactId.HasValue)
            return new(request.PublicId, request.Id, request.LeaseAgreementId, request.LeaseAddendumId,
                request.ExecutedArtifactId.Value);
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
            EntityType = request.LeaseAgreementId.HasValue ? nameof(LeaseAgreement) : nameof(LeaseAddendum),
            EntityId = request.LeaseAgreementId ?? request.LeaseAddendumId!.Value,
            UploadedAt = now,
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
        if (request.LeaseAgreement is { } agreement)
        {
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
            attempt.BindSemanticAudit(agreement, new AtomicSemanticAudit(request.PortfolioId,
                nameof(LeaseAgreement), agreement.Id, AuditLogOperation.Updated, ActorLabel: "esign-system",
                NewValues: JsonSerializer.Serialize(new { agreement.ExecutedArtifactId, agreement.FullyExecutedAtUtc }),
                ChangeReason: "Finalized immutable executed Agreement artifact."));
        }
        else
        {
            var addendum = request.LeaseAddendum!;
            addendum.ExecutedArtifactId = artifact.Id;
            addendum.FullyExecutedAtUtc = now;
            addendum.UpdatedAtUtc = now;
            if (addendum.ReplacesAddendumId.HasValue)
            {
                var predecessor = await attempt.Persistence.Query<LeaseAddendum>()
                    .SingleAsync(item => item.Id == addendum.ReplacesAddendumId
                        && item.LeaseManagementId == addendum.LeaseManagementId
                        && item.SeriesPublicId == addendum.SeriesPublicId
                        && item.PortfolioId == request.PortfolioId, ct);
                if (predecessor.SupersededByAddendumId.HasValue
                    && predecessor.SupersededByAddendumId != addendum.Id)
                    throw new DomainValidationException("A different Addendum correction already supersedes this version.");
                predecessor.SupersededEffectiveOn = addendum.EffectiveFromOn;
                predecessor.SupersededByAddendumId = addendum.Id;
                predecessor.SupersessionRecordedAtUtc = now;
                predecessor.UpdatedAtUtc = now;
                attempt.BindSemanticAudit(predecessor, new AtomicSemanticAudit(request.PortfolioId,
                    nameof(LeaseAddendum), predecessor.Id, AuditLogOperation.Updated, ActorLabel: "esign-system",
                    NewValues: JsonSerializer.Serialize(new { predecessor.SupersededEffectiveOn, predecessor.SupersededByAddendumId }),
                    ChangeReason: "Executed Addendum correction recorded its immutable supersession transition."));
            }
            attempt.BindSemanticAudit(addendum, new AtomicSemanticAudit(request.PortfolioId,
                nameof(LeaseAddendum), addendum.Id, AuditLogOperation.Updated, ActorLabel: "esign-system",
                NewValues: JsonSerializer.Serialize(new { addendum.ExecutedArtifactId, addendum.FullyExecutedAtUtc }),
                ChangeReason: "Finalized immutable executed Addendum artifact."));
        }

        attempt.Persistence.Add(new SignatureAuditEvent
        {
            PortfolioId = request.PortfolioId,
            SignatureRequestId = request.Id,
            Type = SignatureAuditEventType.Completed,
            OccurredAtUtc = now,
            Detail = $"Every required signer signed; executed artifact SHA-256 {command.ContentSha256}.",
        });
        attempt.StageSemanticEvent(new AtomicSemanticAudit(request.PortfolioId,
            nameof(SignatureRequest), request.Id, AuditLogOperation.Updated, ActorLabel: "esign-system",
            NewValues: JsonSerializer.Serialize(new { Status = request.Status.ToString(), request.ExecutedArtifactId, request.CompletedAtUtc }),
            ChangeReason: "Completed canonical legal-artifact signature packet."), now);
        attempt.StageOutbox(new OutboxMessage
        {
            PortfolioId = request.PortfolioId,
            MessageType = "entity-update",
            Payload = JsonSerializer.Serialize(new
            {
                entityType = request.LeaseAgreementId.HasValue ? nameof(LeaseAgreement) : nameof(LeaseAddendum),
                entityId = request.LeaseAgreementId ?? request.LeaseAddendumId,
                leaseManagementId = request.LeaseAgreement?.LeaseManagementId
                    ?? request.LeaseAddendum!.LeaseManagementId,
                action = request.LeaseAgreementId.HasValue ? "agreement-executed" : "addendum-executed",
            }),
            IdempotencyKey = $"legal-artifact:{request.Id}:executed:{artifact.Id}",
            CreatedAtUtc = now,
            NextAttemptAtUtc = now,
        });
        return new(request.PublicId, request.Id, request.LeaseAgreementId, request.LeaseAddendumId, artifact.Id);
    }
}
