using RentalCommand.Core.Atomic;
using RentalCommand.Core.Authorization;
using RentalCommand.Core.Scanning;

namespace RentalCommand.Data.Scanning;

public sealed class RejectScanDraftHandler
    : IAtomicCommandHandler<RejectScanDraftCommand, RejectScanDraftResult>
{
    private readonly RentalCommandDbContext _db;

    public RejectScanDraftHandler(RentalCommandDbContext db) => _db = db;

    public async Task<RejectScanDraftResult> HandleAsync(
        RejectScanDraftCommand command,
        IAtomicCommandContext context,
        CancellationToken ct)
    {
        Validate(command);
        context.UseDatabaseWallClockForAudit(command.ReviewedAtUtc);
        var rejected = await AtomicScanConfirmationPersistence.RejectAuthorizedAsync(_db,
            context, Scope(command), command.DraftId, command.Reason, command.ReviewedAtUtc, ct);
        return new RejectScanDraftResult(
            rejected,
            command.DraftId,
            rejected ? command.ReviewedAtUtc : null);
    }

    public async Task AuthorizeReplayAsync(
        RejectScanDraftCommand command, IAtomicCommandContext context, CancellationToken ct)
    {
        Validate(command);
        var now = await context.ReadDatabaseClockUtcAsync(ct);
        var authorized = await AtomicScanAuthorizationQueries.IsAuthorizedForReviewAsync(_db,
            context, Scope(command), command.DraftId, now, ct);
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
            command.ExpectedAccessRevision <= 0 || command.ReviewedAtUtc.Kind != DateTimeKind.Utc)
        {
            throw new ArgumentOutOfRangeException(nameof(command));
        }
    }
}
