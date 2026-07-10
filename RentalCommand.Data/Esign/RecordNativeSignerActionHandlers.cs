using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using RentalCommand.Core.Atomic;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Esign;

namespace RentalCommand.Data.Esign;

public sealed class RecordNativeSignatureHandler
    : IAtomicCommandHandler<RecordNativeSignatureCommand, NativeSignerActionResult>
{
    public async Task<NativeSignerActionResult> HandleAsync(
        RecordNativeSignatureCommand command,
        IAtomicWriteAttempt attempt,
        CancellationToken ct)
    {
        var requestId = await attempt.Persistence.Query<SignatureSigner>()
            .Where(signer => signer.Token == command.Token)
            .Select(signer => (int?)signer.SignatureRequestId)
            .SingleOrDefaultAsync(ct);
        if (requestId is null)
        {
            return Missing();
        }

        await attempt.Locking.AcquireAsync(AtomicLockResource.SignatureRequest, requestId.Value, ct);
        var signer = await attempt.Persistence.Query<SignatureSigner>()
            .Include(candidate => candidate.SignatureRequest!)
            .SingleAsync(candidate => candidate.Token == command.Token, ct);
        var request = signer.SignatureRequest!;

        if (signer.ExpiresAtUtc <= command.OccurredAtUtc
            || signer.Status is SignatureSignerStatus.Signed or SignatureSignerStatus.Declined
            || request.Status is SignatureRequestStatus.Completed
                or SignatureRequestStatus.Declined
                or SignatureRequestStatus.Voided)
        {
            return Unavailable(signer, request);
        }

        signer.Status = SignatureSignerStatus.Signed;
        signer.SignatureType = command.SignatureType;
        signer.TypedName = command.SignatureType == SignatureSignatureType.Typed ? command.TypedName : null;
        signer.DrawnSignatureImage = command.SignatureType == SignatureSignatureType.Drawn
            ? command.DrawnSignatureImage
            : null;
        signer.ConsentGiven = true;
        signer.SignedAtUtc = command.OccurredAtUtc;
        signer.IpAddress = command.IpAddress ?? signer.IpAddress;
        signer.UserAgent = command.UserAgent ?? signer.UserAgent;
        signer.ViewedAtUtc ??= command.OccurredAtUtc;

        var hasOtherUnsignedSigner = await attempt.Persistence.Query<SignatureSigner>()
            .AnyAsync(
                candidate => candidate.SignatureRequestId == request.Id
                    && candidate.Id != signer.Id
                    && candidate.Status != SignatureSignerStatus.Signed,
                ct);
        var allSigned = !hasOtherUnsignedSigner;
        request.Status = allSigned
            ? SignatureRequestStatus.ExecutionPending
            : SignatureRequestStatus.PartiallySigned;

        attempt.Persistence.Add(new SignatureAuditEvent
        {
            SignatureRequestId = request.Id,
            SignerId = signer.Id,
            Type = SignatureAuditEventType.Signed,
            AtUtc = command.OccurredAtUtc,
            IpAddress = command.IpAddress,
            UserAgent = command.UserAgent,
            Detail = $"{signer.Name} signed ({signer.SignatureType}).",
        });
        StageSignerAndRequestAudits(attempt, request, signer, "Signer captured an electronic signature.");

        return Applied(signer, request, allSigned);
    }

    private static NativeSignerActionResult Missing() => new(
        NativeSignerActionOutcome.NotFound, "This signing link is invalid.", 0, null, 0,
        SignatureSignerStatus.Pending, SignatureRequestStatus.Sent, false);

    private static NativeSignerActionResult Unavailable(SignatureSigner signer, SignatureRequest request) => new(
        NativeSignerActionOutcome.Expired,
        signer.Status == SignatureSignerStatus.Signed
            ? "You have already signed this document."
            : "This signing request is no longer active.",
        request.Id, request.PublicId, request.LeaseId, signer.Status, request.Status, false);

    private static NativeSignerActionResult Applied(
        SignatureSigner signer,
        SignatureRequest request,
        bool executionRequired) => new(
            NativeSignerActionOutcome.Applied, null, request.Id, request.PublicId, request.LeaseId,
            signer.Status, request.Status, executionRequired);

    internal static void StageSignerAndRequestAudits(
        IAtomicWriteAttempt attempt,
        SignatureRequest request,
        SignatureSigner signer,
        string reason)
    {
        attempt.StageSemanticEvent(new AtomicSemanticAudit(
            request.PortfolioId,
            nameof(SignatureSigner),
            signer.Id,
            AuditLogOperation.Updated,
            ActorLabel: "esign-signer",
            NewValues: JsonSerializer.Serialize(new
            {
                Status = signer.Status.ToString(),
                SignatureType = signer.SignatureType.ToString(),
                signer.ConsentGiven,
                signer.SignedAtUtc,
            }),
            ChangeReason: reason,
            IpAddress: signer.IpAddress));
        attempt.StageSemanticEvent(new AtomicSemanticAudit(
            request.PortfolioId,
            nameof(SignatureRequest),
            request.Id,
            AuditLogOperation.Updated,
            ActorLabel: "esign-signer",
            NewValues: JsonSerializer.Serialize(new { Status = request.Status.ToString() }),
            ChangeReason: reason,
            IpAddress: signer.IpAddress));
    }
}

public sealed class RecordNativeDeclineHandler
    : IAtomicCommandHandler<RecordNativeDeclineCommand, NativeSignerActionResult>
{
    public async Task<NativeSignerActionResult> HandleAsync(
        RecordNativeDeclineCommand command,
        IAtomicWriteAttempt attempt,
        CancellationToken ct)
    {
        var target = await attempt.Persistence.Query<SignatureSigner>()
            .Where(signer => signer.Token == command.Token)
            .Select(signer => new { signer.Id, signer.SignatureRequestId })
            .SingleOrDefaultAsync(ct);
        if (target is null)
        {
            return new NativeSignerActionResult(
                NativeSignerActionOutcome.NotFound, "This signing link is invalid.", 0, null, 0,
                SignatureSignerStatus.Pending, SignatureRequestStatus.Sent, false);
        }

        await attempt.Locking.AcquireAsync(AtomicLockResource.SignatureRequest, target.SignatureRequestId, ct);
        var signer = await attempt.Persistence.Query<SignatureSigner>()
            .Include(candidate => candidate.SignatureRequest!)
            .SingleAsync(candidate => candidate.Id == target.Id, ct);
        var request = signer.SignatureRequest!;
        if (signer.ExpiresAtUtc <= command.OccurredAtUtc
            || signer.Status is SignatureSignerStatus.Signed or SignatureSignerStatus.Declined
            || request.Status is SignatureRequestStatus.Completed
                or SignatureRequestStatus.Declined
                or SignatureRequestStatus.Voided)
        {
            return new NativeSignerActionResult(
                NativeSignerActionOutcome.Expired,
                "This signing request is no longer active.",
                request.Id, request.PublicId, request.LeaseId, signer.Status, request.Status, false);
        }

        signer.Status = SignatureSignerStatus.Declined;
        signer.IpAddress = command.IpAddress ?? signer.IpAddress;
        signer.UserAgent = command.UserAgent ?? signer.UserAgent;
        request.Status = SignatureRequestStatus.Declined;

        var lease = await attempt.Persistence.Query<Lease>()
            .SingleAsync(candidate => candidate.Id == request.LeaseId && candidate.PortfolioId == request.PortfolioId, ct);
        lease.EsignEnvelopeId = request.PublicId;
        lease.EsignStatus = EsignStatus.Declined;
        lease.UpdatedAt = command.OccurredAtUtc;
        attempt.BindSemanticAudit(lease, new AtomicSemanticAudit(
            request.PortfolioId,
            nameof(Lease),
            lease.Id,
            AuditLogOperation.Updated,
            ActorLabel: "esign-signer",
            NewValues: JsonSerializer.Serialize(new
            {
                esignStatus = lease.EsignStatus.ToString(),
                envelopeId = request.PublicId,
            }),
            ChangeReason: $"Lease #{lease.Id} signature declined.",
            IpAddress: command.IpAddress));

        attempt.Persistence.Add(new SignatureAuditEvent
        {
            SignatureRequestId = request.Id,
            SignerId = signer.Id,
            Type = SignatureAuditEventType.Declined,
            AtUtc = command.OccurredAtUtc,
            IpAddress = command.IpAddress,
            UserAgent = command.UserAgent,
            Detail = string.IsNullOrWhiteSpace(command.Reason)
                ? $"{signer.Name} declined to sign."
                : $"{signer.Name} declined to sign: {command.Reason.Trim()}",
        });
        RecordNativeSignatureHandler.StageSignerAndRequestAudits(
            attempt, request, signer, "Signer declined the electronic signature request.");

        return new NativeSignerActionResult(
            NativeSignerActionOutcome.Applied, null, request.Id, request.PublicId, request.LeaseId,
            signer.Status, request.Status, false);
    }
}
