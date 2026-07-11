using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using RentalCommand.Core.Atomic;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Esign;

namespace RentalCommand.Data.Esign;

/// <summary>Atomically records at most one first-view fact for a native signing session.</summary>
public sealed class RecordNativeEsignViewHandler
    : IAtomicCommandHandler<RecordNativeEsignViewCommand, RecordNativeEsignViewResult>
{
    public async Task<RecordNativeEsignViewResult> HandleAsync(
        RecordNativeEsignViewCommand command,
        IAtomicWriteAttempt attempt,
        CancellationToken ct)
    {
        var target = await attempt.Persistence.Query<SignatureSigner>()
            .Where(signer => signer.Token == command.Token)
            .Select(signer => new { signer.Id, signer.SignatureRequestId })
            .SingleOrDefaultAsync(ct);
        if (target is null)
        {
            return new RecordNativeEsignViewResult(
                NativeEsignViewOutcome.NotFound,
                "This signing link is invalid.",
                0);
        }

        await attempt.Locking.AcquireAsync(
            AtomicLockResource.SignatureRequest,
            target.SignatureRequestId,
            ct);

        // Re-read after obtaining the aggregate lock so a concurrent sign/decline decides first.
        var signer = await attempt.Persistence.Query<SignatureSigner>()
            .Include(candidate => candidate.SignatureRequest!)
            .SingleAsync(
                candidate => candidate.Id == target.Id
                    && candidate.SignatureRequestId == target.SignatureRequestId,
                ct);
        var request = signer.SignatureRequest!;

        if (signer.ExpiresAtUtc <= command.OccurredAtUtc
            && signer.Status is not (SignatureSignerStatus.Signed or SignatureSignerStatus.Declined)
            && request.Status is not (SignatureRequestStatus.Completed
                or SignatureRequestStatus.Declined
                or SignatureRequestStatus.Voided))
        {
            return new RecordNativeEsignViewResult(
                NativeEsignViewOutcome.Expired,
                "This signing link has expired. Please ask the sender for a new one.",
                request.Id);
        }

        if (signer.Status != SignatureSignerStatus.Pending)
        {
            return new RecordNativeEsignViewResult(
                NativeEsignViewOutcome.Available,
                null,
                request.Id);
        }

        signer.Status = SignatureSignerStatus.Viewed;
        signer.ViewedAtUtc = command.OccurredAtUtc;
        signer.IpAddress ??= command.IpAddress;
        signer.UserAgent ??= command.UserAgent;

        var requestAdvanced = request.Status == SignatureRequestStatus.Sent;
        if (requestAdvanced)
        {
            request.Status = SignatureRequestStatus.Viewed;
        }

        attempt.Persistence.Add(new SignatureAuditEvent
        {
            SignatureRequestId = request.Id,
            SignerId = signer.Id,
            Type = SignatureAuditEventType.Viewed,
            AtUtc = command.OccurredAtUtc,
            IpAddress = command.IpAddress,
            UserAgent = command.UserAgent,
            Detail = $"{signer.Name} opened the signing page.",
        });

        attempt.StageSemanticEvent(new AtomicSemanticAudit(
            request.PortfolioId,
            nameof(SignatureSigner),
            signer.Id,
            AuditLogOperation.Updated,
            ActorLabel: "esign-signer",
            NewValues: JsonSerializer.Serialize(new
            {
                Status = signer.Status.ToString(),
                signer.ViewedAtUtc,
            }),
            ChangeReason: "Signer opened the native electronic signing page.",
            IpAddress: signer.IpAddress));

        // SignatureSigner and SignatureRequest deliberately are not IAuditable/IPortfolioScoped.
        // Stage both semantic facts directly instead of binding them to the tracked-mutation
        // interceptor, which cannot infer the signer's portfolio and would reject the binding.
        // Keeping the pair unconditional also gives every genuine first view one stable two-row
        // command audit, even when another signer already advanced the request beyond Sent.
        attempt.StageSemanticEvent(new AtomicSemanticAudit(
            request.PortfolioId,
            nameof(SignatureRequest),
            request.Id,
            AuditLogOperation.Updated,
            ActorLabel: "esign-signer",
            NewValues: JsonSerializer.Serialize(new { Status = request.Status.ToString() }),
            ChangeReason: requestAdvanced
                ? "The native electronic signature request was first viewed."
                : "A signer first viewed the native electronic signature request.",
            IpAddress: signer.IpAddress));

        return new RecordNativeEsignViewResult(
            NativeEsignViewOutcome.Available,
            null,
            request.Id);
    }
}
