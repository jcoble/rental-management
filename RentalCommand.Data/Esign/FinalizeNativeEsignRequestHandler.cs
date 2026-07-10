using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using RentalCommand.Core.Atomic;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Esign;

namespace RentalCommand.Data.Esign;

public sealed class FinalizeNativeEsignRequestHandler
    : IAtomicCommandHandler<FinalizeNativeEsignRequestCommand, FinalizeNativeEsignRequestResult>
{
    public async Task<FinalizeNativeEsignRequestResult> HandleAsync(
        FinalizeNativeEsignRequestCommand command,
        IAtomicWriteAttempt attempt,
        CancellationToken ct)
    {
        var requestId = await attempt.Persistence.Query<SignatureRequest>()
            .Where(request => request.PublicId == command.PublicId)
            .Select(request => (int?)request.Id)
            .SingleOrDefaultAsync(ct)
            ?? throw new InvalidOperationException("The native e-sign request no longer exists.");

        await attempt.Locking.AcquireAsync(AtomicLockResource.SignatureRequest, requestId, ct);
        var request = await attempt.Persistence.Query<SignatureRequest>()
            .SingleAsync(candidate => candidate.Id == requestId, ct);
        if (request.Status == SignatureRequestStatus.Completed && request.SignedStoredFileId.HasValue)
        {
            return new FinalizeNativeEsignRequestResult(
                request.PublicId, request.Id, request.LeaseId, request.SignedStoredFileId.Value);
        }

        if (request.Status != SignatureRequestStatus.ExecutionPending)
        {
            throw new InvalidOperationException("The native e-sign request is not ready for execution.");
        }

        var hasUnsignedSigner = await attempt.Persistence.Query<SignatureSigner>()
            .AnyAsync(
                signer => signer.SignatureRequestId == request.Id
                    && signer.Status != SignatureSignerStatus.Signed,
                ct);
        if (hasUnsignedSigner)
        {
            throw new InvalidOperationException("The executed document cannot be finalized before every signer signs.");
        }

        var lease = await attempt.Persistence.Query<Lease>()
            .SingleAsync(
                candidate => candidate.Id == request.LeaseId && candidate.PortfolioId == request.PortfolioId,
                ct);
        var storedFile = new StoredFile
        {
            PortfolioId = request.PortfolioId,
            FileName = command.FileName,
            FilePath = command.StorageKey,
            ContentType = "application/pdf",
            FileSize = command.FileSize,
            EntityType = nameof(Lease),
            EntityId = request.LeaseId,
            UploadedAt = command.CompletedAtUtc,
        };
        attempt.Persistence.Add(storedFile);
        await attempt.FlushBusinessAsync(ct);

        request.SignedStoredFileId = storedFile.Id;
        request.ContentSha256 = command.ContentSha256;
        request.CompletedAtUtc = command.CompletedAtUtc;
        request.Status = SignatureRequestStatus.Completed;
        lease.EsignEnvelopeId = request.PublicId;
        lease.EsignStatus = EsignStatus.Signed;
        lease.SignedDocumentStoredFileId = storedFile.Id;
        if (lease.Status == LeaseStatus.PendingSignature)
        {
            lease.Status = LeaseStatus.Active;
        }
        lease.UpdatedAt = command.CompletedAtUtc;

        attempt.Persistence.Add(new SignatureAuditEvent
        {
            SignatureRequestId = request.Id,
            Type = SignatureAuditEventType.Completed,
            AtUtc = command.CompletedAtUtc,
            Detail = $"All signers signed. Executed document SHA-256 {command.ContentSha256}.",
        });
        attempt.StageSemanticEvent(new AtomicSemanticAudit(
            request.PortfolioId,
            nameof(SignatureRequest),
            request.Id,
            AuditLogOperation.Updated,
            ActorLabel: "esign-system",
            NewValues: JsonSerializer.Serialize(new
            {
                Status = request.Status.ToString(),
                request.SignedStoredFileId,
                request.ContentSha256,
                request.CompletedAtUtc,
            }),
            ChangeReason: "Executed lease document finalized after every signer signed."));
        attempt.BindSemanticAudit(lease, new AtomicSemanticAudit(
            request.PortfolioId,
            nameof(Lease),
            lease.Id,
            AuditLogOperation.Updated,
            ActorLabel: "esign-system",
            NewValues: JsonSerializer.Serialize(new
            {
                esignStatus = lease.EsignStatus.ToString(),
                leaseStatus = lease.Status.ToString(),
                envelopeId = lease.EsignEnvelopeId,
                signedDocumentStoredFileId = lease.SignedDocumentStoredFileId,
            }),
            ChangeReason: $"Lease #{lease.Id} signed electronically and executed document attached."));

        return new FinalizeNativeEsignRequestResult(
            request.PublicId, request.Id, request.LeaseId, storedFile.Id);
    }
}
