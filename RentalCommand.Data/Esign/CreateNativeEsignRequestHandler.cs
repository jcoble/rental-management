using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using RentalCommand.Core;
using RentalCommand.Core.Atomic;
using RentalCommand.Core.Constants;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Esign;

namespace RentalCommand.Data.Esign;

/// <summary>Pure database handler for the native e-sign envelope package.</summary>
public sealed class CreateNativeEsignRequestHandler
    : IAtomicCommandHandler<CreateNativeEsignRequestCommand, CreateNativeEsignRequestResult>
{
    public async Task<CreateNativeEsignRequestResult> HandleAsync(
        CreateNativeEsignRequestCommand command,
        IAtomicWriteAttempt attempt,
        CancellationToken ct)
    {
        if (command.Signers.Count == 0)
        {
            throw new InvalidOperationException("At least one signer is required.");
        }

        var lease = await attempt.Persistence.Query<Lease>()
            .SingleOrDefaultAsync(
                candidate => candidate.Id == command.LeaseId && candidate.PortfolioId == command.PortfolioId,
                ct);
        if (lease is null)
        {
            throw new InvalidOperationException("The lease no longer exists in the requested portfolio.");
        }

        var signable = lease.Status is LeaseStatus.Draft or LeaseStatus.PendingSignature or LeaseStatus.Active;
        if (!signable || lease.EsignStatus == EsignStatus.Signed || lease.SignedDocumentStoredFileId.HasValue)
        {
            throw new DomainValidationException("The lease is no longer in a signable state.");
        }

        lease.EsignEnvelopeId = command.PublicId;
        lease.EsignStatus = EsignStatus.Sent;
        if (lease.Status != LeaseStatus.Active)
        {
            lease.Status = LeaseStatus.PendingSignature;
        }
        lease.UpdatedAt = command.CreatedAtUtc;
        attempt.BindSemanticAudit(lease, new AtomicSemanticAudit(
            command.PortfolioId,
            nameof(Lease),
            lease.Id,
            AuditLogOperation.Updated,
            NewValues: JsonSerializer.Serialize(new
            {
                esignStatus = lease.EsignStatus.ToString(),
                leaseStatus = lease.Status.ToString(),
                envelopeId = lease.EsignEnvelopeId,
            }),
            ChangeReason: $"Lease #{lease.Id} sent for electronic signature."));

        var storedFile = new StoredFile
        {
            PortfolioId = command.PortfolioId,
            FileName = command.DocumentName,
            FilePath = command.StorageKey,
            ContentType = "application/pdf",
            FileSize = command.FileSize,
            EntityType = "esign-original",
            EntityId = command.LeaseId,
            UploadedAt = command.CreatedAtUtc,
        };

        var signatureRequest = new SignatureRequest
        {
            PortfolioId = command.PortfolioId,
            PublicId = command.PublicId,
            LeaseId = command.LeaseId,
            DocumentName = command.DocumentName,
            Subject = command.Subject,
            OriginalStoredFile = storedFile,
            DocumentTemplateId = command.DocumentTemplateId,
            DocumentTemplateVersion = command.DocumentTemplateVersion,
            TemplateFieldSnapshotJson = command.TemplateFieldSnapshotJson,
            Status = SignatureRequestStatus.Sent,
            CreatedAtUtc = command.CreatedAtUtc,
        };

        foreach (var signer in command.Signers)
        {
            signatureRequest.Signers.Add(new SignatureSigner
            {
                Name = signer.Name,
                Email = signer.Email,
                Token = signer.Token,
                ExpiresAtUtc = command.LinkExpiresAtUtc,
                Status = SignatureSignerStatus.Pending,
            });
        }

        signatureRequest.AuditEvents.Add(new SignatureAuditEvent
        {
            Type = SignatureAuditEventType.Sent,
            AtUtc = command.CreatedAtUtc,
            Detail = $"Request sent to {command.Signers.Count} signer(s): " +
                     $"{string.Join(", ", command.Signers.Select(signer => signer.Email))}.",
        });

        attempt.Persistence.Add(signatureRequest);
        await attempt.FlushBusinessAsync(ct);

        attempt.StageSemanticEvent(new AtomicSemanticAudit(
            command.PortfolioId,
            nameof(SignatureRequest),
            signatureRequest.Id,
            AuditLogOperation.Created,
            NewValues: JsonSerializer.Serialize(new
            {
                signatureRequest.PublicId,
                signatureRequest.LeaseId,
                Status = signatureRequest.Status.ToString(),
                SignerCount = signatureRequest.Signers.Count,
            }),
            ChangeReason: "Native e-sign request created with signer invitations."));

        foreach (var signer in signatureRequest.Signers)
        {
            attempt.StageOutbox(CreateSigningEmail(command, signatureRequest, signer));
        }

        return new CreateNativeEsignRequestResult(
            signatureRequest.PublicId,
            signatureRequest.Id,
            storedFile.Id);
    }

    private static OutboxMessage CreateSigningEmail(
        CreateNativeEsignRequestCommand command,
        SignatureRequest request,
        SignatureSigner signer)
    {
        var link = $"{command.WebBaseUrl}/sign/{signer.Token}";
        var subject = string.IsNullOrWhiteSpace(request.Subject)
            ? "Please sign your lease agreement"
            : $"Please sign: {request.Subject}";
        var body = $"""
Hi {signer.Name},

You have a document ready to sign electronically: {request.Subject ?? request.DocumentName}.

Review and sign it here:
{link}

This secure link is unique to you and expires on {signer.ExpiresAtUtc:MMMM d, yyyy}. By signing you agree to
use electronic records and signatures (E-SIGN / UETA).

– Sent via Rental Command
""";

        return new OutboxMessage
        {
            PortfolioId = command.PortfolioId,
            MessageType = "email",
            Payload = JsonSerializer.Serialize(new
            {
                source = OutboxPayloadSources.LeaseEsignSigningLink,
                signatureRequestId = request.Id,
                leaseId = request.LeaseId,
                to = signer.Email,
                subject,
                body,
            }),
            IdempotencyKey = $"lease-esign:{request.Id}:signer:{signer.Id}:invite",
            CreatedAtUtc = command.CreatedAtUtc,
            NextAttemptAtUtc = command.CreatedAtUtc,
        };
    }
}
