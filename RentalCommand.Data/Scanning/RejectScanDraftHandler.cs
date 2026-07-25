using RentalCommand.Core.Atomic;
using RentalCommand.Core.Authorization;
using RentalCommand.Core.Scanning;

namespace RentalCommand.Data.Scanning;

public sealed class RejectScanDraftHandler
    : IAtomicCommandHandler<RejectScanDraftCommand, RejectScanDraftResult>,
      IAtomicReplayAuthorizer<RejectScanDraftCommand>
{
    public async Task<RejectScanDraftResult> HandleAsync(
        RejectScanDraftCommand command,
        IAtomicWriteAttempt attempt,
        CancellationToken ct)
    {
        Validate(command);
        var now = await attempt.Persistence.ReadDatabaseClockUtcAsync(ct);
        var rejected = await attempt.ScanConfirmation.RejectAuthorizedAsync(
            Scope(command), command.DraftId, command.Reason, now, ct);
        return new RejectScanDraftResult(rejected, command.DraftId);
    }

    public async Task AuthorizeReplayAsync(
        RejectScanDraftCommand command,
        IAtomicPersistenceSession persistence,
        CancellationToken ct)
    {
        Validate(command);
        var now = await persistence.ReadDatabaseClockUtcAsync(ct);
        var authorized = await persistence.IsScanDraftAuthorizedForReviewAsync(
            Scope(command), command.DraftId, now, ct);
        if (!authorized)
        {
            throw new UnauthorizedAccessException("The scan draft is outside the caller's current review scope.");
        }
    }

    private static WorkspaceReadScope Scope(RejectScanDraftCommand command) =>
        new(command.PortfolioId, command.UserId, command.AuthSessionId,
            command.AccessContextId, command.ExpectedAccessRevision);

    private static void Validate(RejectScanDraftCommand command)
    {
        if (command.PortfolioId <= 0 || command.DraftId <= 0 || command.UserId <= 0 ||
            command.AuthSessionId == Guid.Empty || command.AccessContextId <= 0 ||
            command.ExpectedAccessRevision <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(command));
        }
    }
}
