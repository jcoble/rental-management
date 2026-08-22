using RentalCommand.Core.Atomic;
using RentalCommand.Core.Authorization;
using RentalCommand.Core.Enums;

namespace RentalCommand.Data.Authorization;

public sealed class SelectWorkspaceExperienceHandler
{
    private readonly RentalCommandDbContext _db;

    public SelectWorkspaceExperienceHandler(RentalCommandDbContext db) => _db = db;

    public static TransactionalWrite<SelectWorkspaceExperienceCommand, SelectWorkspaceExperienceResult> Write(
        RentalCommandDbContext db,
        SelectWorkspaceExperienceCommand command)
    {
        var handler = new SelectWorkspaceExperienceHandler(db);
        return new TransactionalWrite<SelectWorkspaceExperienceCommand, SelectWorkspaceExperienceResult>(
            "workspace-experience.select", WriteIdempotencyPolicy.Required, command,
            "workspace-experience-select-result:v1", WriteLockPlan.None,
            handler.ExecuteAsync, handler.AuthorizeReplayAsync);
    }

    public async Task<SelectWorkspaceExperienceResult> ExecuteAsync(
        SelectWorkspaceExperienceCommand command,
        IAtomicCommandContext context,
        CancellationToken ct)
    {
        Validate(command);
        await context.AcquireLockAsync("AuthSession", command.AuthSessionId, ct);
        await context.AcquireLockAsync(
            "WorkspaceAccessContext", command.AccessContextId, ct);

        var updated = await AtomicWorkspaceExperiencePersistence.SelectAsync(_db,
            context, new WorkspaceReadScope(
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
        SelectWorkspaceExperienceCommand command, IAtomicCommandContext context, CancellationToken ct)
    {
        Validate(command);
        var authorized = await AtomicWorkspaceExperiencePersistence.IsAvailableAsync(_db,
            context,
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
