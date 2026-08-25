using Microsoft.EntityFrameworkCore;
using RentalCommand.Core;
using RentalCommand.Core.Atomic;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Esign;
using RentalCommand.Data.Leasing;

namespace RentalCommand.Data.Esign;

public sealed class ResendNativeEsignInvitationRule
{
    private readonly RentalCommandDbContext _db;

    public ResendNativeEsignInvitationRule(RentalCommandDbContext db) => _db = db;

    public async Task<ResendNativeEsignInvitationResult> ExecuteAsync(
        ResendNativeEsignInvitationCommand command,
        IAtomicCommandContext context,
        CancellationToken ct)
    {
        Validate(command);
        var times = await AtomicCommandClock.ReadCommandTimesAsync(_db, command.PortfolioId, ct);
        var target = await FindAuthorizedTarget(command, times.WallClockUtc).SingleOrDefaultAsync(ct)
            ?? throw Unauthorized();

        ValidateEligibility(target, times.WallClockUtc);
        var prefix = command.ParentKind == NativeEsignInvitationParentKind.LeaseAgreement
            ? "agreement-esign"
            : "addendum-esign";
        var originalKey = $"{prefix}:{target.SignatureRequestId}:signer:{target.SignatureSignerId}:invite";
        var originalPayload = await _db.OutboxMessages.AsNoTracking()
            .Where(message => message.PortfolioId == command.PortfolioId
                && message.MessageType == "email"
                && message.IdempotencyKey == originalKey)
            .Select(message => message.Payload)
            .SingleOrDefaultAsync(ct)
            ?? throw new DomainValidationException(
                "The original signing invitation is unavailable. Void and replace the packet before sending a new invitation.");
        var resendKey = $"{prefix}:{target.SignatureRequestId}:signer:{target.SignatureSignerId}:resend:{command.DeliveryIdempotencyKey}";

        context.StageOutbox(new OutboxMessage
        {
            PortfolioId = command.PortfolioId,
            MessageType = "email",
            Payload = originalPayload,
            IdempotencyKey = resendKey,
            CreatedAtUtc = times.EffectiveNowUtc,
            NextAttemptAtUtc = times.EffectiveNowUtc,
        });

        return new(target.SignatureRequestId, target.SignatureSignerId, resendKey);
    }

    public async Task AuthorizeAsync(
        ResendNativeEsignInvitationCommand command,
        IAtomicCommandContext context,
        CancellationToken ct)
    {
        Validate(command);
        var nowUtc = await _db.Database
            .SqlQuery<DateTime>($"SELECT clock_timestamp() AS \"Value\"")
            .SingleAsync(ct);
        if (!await FindAuthorizedTarget(command, nowUtc).AnyAsync(ct))
        {
            throw Unauthorized();
        }
    }

    private IQueryable<InvitationTarget> FindAuthorizedTarget(
        ResendNativeEsignInvitationCommand command,
        DateTime securityNowUtc)
    {
        var authorizedRelationships = LeaseAgreementDraftCommandSupport
            .AuthorizedRelationships(command, _db, securityNowUtc);
        var signers = _db.Set<SignatureSigner>().AsNoTracking()
            .Where(signer => signer.PortfolioId == command.PortfolioId
                && signer.SignatureRequest != null
                && signer.SignatureRequest.Provider == "native");

        if (command.ParentKind == NativeEsignInvitationParentKind.LeaseAgreement)
        {
            return signers
                .Where(signer => signer.AgreementSignerId == command.LegalSignerId
                    && signer.SignatureRequest!.LeaseAgreementId == command.ParentId
                    && authorizedRelationships.Any(relationship =>
                        relationship.Agreements.Any(agreement => agreement.Id == command.ParentId)))
                .Select(signer => new InvitationTarget(
                    signer.SignatureRequestId,
                    signer.Id,
                    signer.SignatureRequest!.Status,
                    signer.Status,
                    signer.TokenExpiresAtUtc,
                    signer.SignatureRequest.Signers.All(candidate =>
                        !candidate.IsRequired || candidate.Status == SignatureSignerStatus.Signed),
                    signer.SignatureRequest.Signers.Any(candidate =>
                        candidate.Status == SignatureSignerStatus.Declined),
                    signer.SignatureRequest.LeaseAgreement!.VoidedAtUtc != null
                        || signer.SignatureRequest.LeaseAgreement.DraftCanceledAtUtc != null));
        }

        return signers
            .Where(signer => signer.AddendumSignerId == command.LegalSignerId
                && signer.SignatureRequest!.LeaseAddendumId == command.ParentId
                && authorizedRelationships.Any(relationship =>
                    relationship.Addenda.Any(addendum => addendum.Id == command.ParentId)))
            .Select(signer => new InvitationTarget(
                signer.SignatureRequestId,
                signer.Id,
                signer.SignatureRequest!.Status,
                signer.Status,
                signer.TokenExpiresAtUtc,
                signer.SignatureRequest.Signers.All(candidate =>
                    !candidate.IsRequired || candidate.Status == SignatureSignerStatus.Signed),
                signer.SignatureRequest.Signers.Any(candidate =>
                    candidate.Status == SignatureSignerStatus.Declined),
                signer.SignatureRequest.LeaseAddendum!.VoidedAtUtc != null
                    || signer.SignatureRequest.LeaseAddendum.DraftCanceledAtUtc != null));
    }

    private static void ValidateEligibility(InvitationTarget target, DateTime wallClockUtc)
    {
        if (target.AllRequiredSignersSigned
            || target.RequestStatus is SignatureRequestStatus.ExecutionPending or SignatureRequestStatus.Completed)
        {
            throw new DomainValidationException("The signing packet is fully signed and cannot be resent.");
        }
        if (target.HasDeclinedSigner || target.RequestStatus == SignatureRequestStatus.Declined)
        {
            throw new DomainValidationException("The signing packet was declined and cannot be resent.");
        }
        if (target.ParentCanceledOrVoided || target.RequestStatus == SignatureRequestStatus.Voided)
        {
            throw new DomainValidationException("The signing packet was voided or canceled and cannot be resent.");
        }
        if (target.SignerStatus == SignatureSignerStatus.Signed)
        {
            throw new DomainValidationException(
                "This signer has already signed and cannot receive another invitation.");
        }
        if (target.SignerStatus == SignatureSignerStatus.Declined)
        {
            throw new DomainValidationException("The signing packet was declined and cannot be resent.");
        }
        if (target.RequestStatus is not (
            SignatureRequestStatus.AwaitingSignatures
            or SignatureRequestStatus.Viewed
            or SignatureRequestStatus.PartiallySigned
            or SignatureRequestStatus.DeliveryFailed))
        {
            throw new DomainValidationException(
                "Only a packet awaiting signatures or with failed delivery can be resent.");
        }
        if (target.TokenExpiresAtUtc <= wallClockUtc)
        {
            throw new DomainValidationException(
                "This signing link has expired. Void and replace the packet before sending a new invitation.");
        }
    }

    private static void Validate(ResendNativeEsignInvitationCommand command)
    {
        LeaseAgreementDraftCommandSupport.ValidateAuthorizationShape(command);
        if (!Enum.IsDefined(command.ParentKind) || command.ParentId <= 0 || command.LegalSignerId <= 0)
        {
            throw new ArgumentException("The signing invitation resend command is incomplete.");
        }
    }

    private static UnauthorizedAccessException Unauthorized() => new(
        "The signing invitation is outside the caller's current access scope.");

    private sealed record InvitationTarget(
        int SignatureRequestId,
        int SignatureSignerId,
        SignatureRequestStatus RequestStatus,
        SignatureSignerStatus SignerStatus,
        DateTime TokenExpiresAtUtc,
        bool AllRequiredSignersSigned,
        bool HasDeclinedSigner,
        bool ParentCanceledOrVoided);
}
