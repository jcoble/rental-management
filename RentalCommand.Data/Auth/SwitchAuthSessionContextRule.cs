using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using RentalCommand.Core.Atomic;
using RentalCommand.Core.Auth;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Data.Authorization;

namespace RentalCommand.Data.Auth;

public sealed class SwitchAuthSessionContextRule
{
    private readonly RentalCommandDbContext _db;

    public SwitchAuthSessionContextRule(RentalCommandDbContext db) => _db = db;

    public async Task<SwitchAuthSessionContextResult> ExecuteAsync(
        SwitchAuthSessionContextCommand command,
        IAtomicCommandContext context,
        CancellationToken ct)
    {
        if (command.AuthSessionId == Guid.Empty || command.UserId <= 0 ||
            command.CurrentAccessContextId <= 0 || command.CurrentAccessRevision <= 0 ||
            command.SelectedAccessContextId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(command));
        }

        await context.AcquireLockAsync("AuthSession", command.AuthSessionId, ct);
        var session = await _db.Set<AuthSession>()
            .SingleOrDefaultAsync(item =>
                item.Id == command.AuthSessionId &&
                item.UserId == command.UserId &&
                item.ActiveAccessContextId == command.CurrentAccessContextId &&
                item.Status == AuthSessionStatus.Active &&
                item.RevokedAtUtc == null &&
                item.ExpiresAtUtc > command.ChangedAtUtc,
                ct)
            ?? throw new UnauthorizedAccessException("The current authentication session is unavailable.");

        var currentRevision = await _db.Set<WorkspaceAccessContext>()
            .WhereEffective()
            .Where(item => item.Id == command.CurrentAccessContextId && item.UserId == command.UserId)
            .Select(item => (long?)item.AccessRevision)
            .SingleOrDefaultAsync(ct);
        if (currentRevision != command.CurrentAccessRevision)
        {
            throw new UnauthorizedAccessException("The current access envelope is stale.");
        }

        var selected = await _db.Set<WorkspaceAccessContext>()
            .Where(item => item.Id == command.SelectedAccessContextId &&
                item.UserId == command.UserId &&
                AccessAuthorityDbFunctions.IsEffective(
                    item.Id,
                    command.UserId,
                    command.ChangedAtUtc))
            .Select(item => new { item.Id, item.PortfolioId, item.AccessRevision })
            .SingleOrDefaultAsync(ct)
            ?? throw new UnauthorizedAccessException("The selected access context is unavailable.");

        session.ActiveAccessContextId = selected.Id;
        session.LastSeenAtUtc = command.ChangedAtUtc;
        context.StageSemanticEvent(new AtomicSemanticAudit(
            selected.PortfolioId,
            nameof(AuthSession),
            selected.Id,
            AuditLogOperation.Updated,
            command.UserId,
            ActorLabel: "authentication:context-switch",
            NewValues: JsonSerializer.Serialize(new
            {
                command.AuthSessionId,
                command.CurrentAccessContextId,
                SelectedAccessContextId = selected.Id,
                selected.AccessRevision,
            }),
            ChangeReason: "Active workspace context switched"));

        return new SwitchAuthSessionContextResult(
            true,
            session.Id,
            command.UserId,
            selected.Id,
            selected.PortfolioId,
            selected.AccessRevision);
    }

    public Task AuthorizeReplayAsync(
        SwitchAuthSessionContextCommand command, IAtomicCommandContext context, CancellationToken ct) =>
        throw AuthSessionWriteSupport.RetiredPath();

    public async Task AuthorizeAsync(
        SwitchAuthSessionContextCommand command,
        IAtomicCommandContext context,
        CancellationToken ct)
    {
        if (command.AuthSessionId == Guid.Empty || command.UserId <= 0 ||
            command.CurrentAccessContextId <= 0 || command.CurrentAccessRevision <= 0 ||
            command.SelectedAccessContextId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(command));
        }

        var authorized = await _db.Set<AuthSession>()
            .IgnoreQueryFilters()
            .AsNoTracking()
            .AnyAsync(session =>
                session.Id == command.AuthSessionId &&
                session.UserId == command.UserId &&
                (session.ActiveAccessContextId == command.CurrentAccessContextId ||
                 session.ActiveAccessContextId == command.SelectedAccessContextId) &&
                _db.Set<WorkspaceAccessContext>()
                    .IgnoreQueryFilters()
                    .Any(current =>
                        current.Id == command.CurrentAccessContextId &&
                        current.UserId == command.UserId &&
                        current.AccessRevision == command.CurrentAccessRevision) &&
                _db.Set<WorkspaceAccessContext>()
                    .IgnoreQueryFilters()
                    .Any(selected =>
                        selected.Id == command.SelectedAccessContextId &&
                        selected.UserId == command.UserId),
                ct);
        if (!authorized)
        {
            throw new UnauthorizedAccessException("The original context-switch ownership is unavailable.");
        }
    }
}
