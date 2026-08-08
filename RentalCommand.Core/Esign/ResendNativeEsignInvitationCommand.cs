using RentalCommand.Core.Atomic;
using RentalCommand.Core.Leasing;

namespace RentalCommand.Core.Esign;

public enum NativeEsignInvitationParentKind
{
    LeaseAgreement,
    LeaseAddendum,
}

/// <summary>Stages one new invitation delivery for an existing frozen native e-sign signer.</summary>
public sealed record ResendNativeEsignInvitationCommand(
    int PortfolioId,
    int LeaseManagementId,
    NativeEsignInvitationParentKind ParentKind,
    int ParentId,
    int LegalSignerId,
    int ActorUserId,
    [property: AtomicFingerprintIgnore] Guid AuthSessionId,
    [property: AtomicFingerprintIgnore] int AccessContextId,
    [property: AtomicFingerprintIgnore] long ExpectedAccessRevision,
    [property: AtomicFingerprintIgnore] string DeliveryIdempotencyKey) : ILeaseAgreementDraftCommand;

public sealed record ResendNativeEsignInvitationResult(
    int SignatureRequestId,
    int SignatureSignerId,
    string OutboxIdempotencyKey);
