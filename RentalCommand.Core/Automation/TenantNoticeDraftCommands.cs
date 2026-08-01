using RentalCommand.Core.Atomic;
using RentalCommand.Core.Notifications;

namespace RentalCommand.Core.Automation;

/// <summary>
/// Applies one token-fenced tenant-notice draft batch. The claim token is both the work fence and
/// the atomic receipt identity, so replay returns the first generated draft set without re-inserting.
/// </summary>
public sealed record ApplyClaimedTenantNoticeDraftBatchCommand(
    Guid ClaimToken) : IAtomicCommandData;

public sealed record ApplyClaimedTenantNoticeDraftBatchResult(
    int CreatedCount,
    AtomicGeneratedTenantNoticeDraft[] Drafts);

public static class TenantNoticeDraftAutomation
{
    public static readonly AtomicJsonResultCodec<ApplyClaimedTenantNoticeDraftBatchResult> Codec =
        new("tenant-notice-draft.claimed-batch.apply.v1");

    public static AtomicCommandIdentity Identity(ApplyClaimedTenantNoticeDraftBatchCommand command) =>
        new("tenant-notice-draft.claimed-batch.apply", command.ClaimToken.ToString("N"));
}
