using RentalCommand.Core.Atomic;
using RentalCommand.Core.Authorization;
using RentalCommand.Core.Enums;

namespace RentalCommand.Data.Authorization;

public sealed class SelectWorkspaceExperienceHandler
    : IAtomicCommandHandler<SelectWorkspaceExperienceCommand, SelectWorkspaceExperienceResult>,
      IAtomicReplayAuthorizer<SelectWorkspaceExperienceCommand>
{
    public async Task<SelectWorkspaceExperienceResult> HandleAsync(
        SelectWorkspaceExperienceCommand command,
        IAtomicWriteAttempt attempt,
        CancellationToken ct)
    {
        Validate(command);
        await attempt.Locking.AcquireAsync(AtomicLockResource.AuthSession, command.AuthSessionId, ct);
        await attempt.Locking.AcquireAsync(
            AtomicLockResource.WorkspaceAccessContext, command.AccessContextId, ct);

        var updated = await attempt.WorkspaceExperiences.SelectAsync(
            new WorkspaceReadScope(
                command.PortfolioId,
                command.ActorUserId,
                command.AuthSessionId,
                command.AccessContextId,
                command.ExpectedAccessRevision),
            command.Experience,
            ct);
        if (!updated)
        {
            throw new UnauthorizedAccessException(
                "The workspace experience is no longer available in the active access context.");
        }

        return new SelectWorkspaceExperienceResult(command.Experience);
    }

    public async Task AuthorizeReplayAsync(
        SelectWorkspaceExperienceCommand command,
        IAtomicPersistenceSession persistence,
        CancellationToken ct)
    {
        Validate(command);
        var authorized = await persistence.IsWorkspaceExperienceAvailableAsync(
            new WorkspaceReadScope(
                command.PortfolioId,
                command.ActorUserId,
                command.AuthSessionId,
                command.AccessContextId,
                command.ExpectedAccessRevision),
            command.Experience,
            ct);
        if (!authorized)
        {
            throw new UnauthorizedAccessException(
                "The workspace experience is no longer available in the active access context.");
        }
    }

    private static void Validate(SelectWorkspaceExperienceCommand command)
    {
        if (command.PortfolioId <= 0
            || command.ActorUserId <= 0
            || command.AuthSessionId == Guid.Empty
            || command.AccessContextId <= 0
            || command.ExpectedAccessRevision <= 0
            || !Enum.IsDefined(command.Experience))
        {
            throw new ArgumentException(
                "Portfolio, actor, active session, access revision, and workspace experience are required.");
        }
    }
}
