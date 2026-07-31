using System.Text.Json;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using RentalCommand.Core.Atomic;
using RentalCommand.Core.Auth;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Data.Authorization;

namespace RentalCommand.Data.Auth;

public sealed class ChangePasswordHandler
    : IAtomicCommandHandler<ChangePasswordCommand, ChangePasswordResult>
{
    private readonly RentalCommandDbContext _db;

    public ChangePasswordHandler(RentalCommandDbContext db) => _db = db;

    public async Task<ChangePasswordResult> HandleAsync(
        ChangePasswordCommand command,
        IAtomicCommandContext context,
        CancellationToken ct)
    {
        Validate(command);
        var authorization = await AuthorizeAsync(command, _db, ct);
        if (authorization is null)
        {
            return Result(ChangePasswordOutcome.AccessUnavailable, command);
        }

        await context.AcquireLockAsync("ApplicationUser", command.UserId, ct);
        var user = await _db.Set<ApplicationUser>()
            .SingleOrDefaultAsync(candidate => candidate.Id == command.UserId, ct);
        if (user is null)
        {
            return Result(ChangePasswordOutcome.UserNotFound, command);
        }
        if (string.IsNullOrWhiteSpace(user.PasswordHash))
        {
            return Result(ChangePasswordOutcome.NoLocalPassword, command);
        }

        // Use the same ASP.NET Identity V3 primitive as UserManager, while keeping its store-level
        // SaveChanges call out of the atomic context. The exact current hash is verified only after
        // the user lock is held, eliminating a check-then-write race.
        var hasher = new PasswordHasher<ApplicationUser>();
        var verified = hasher.VerifyHashedPassword(user, user.PasswordHash, command.CurrentPassword);
        if (verified == PasswordVerificationResult.Failed)
        {
            return Result(ChangePasswordOutcome.CurrentPasswordIncorrect, command);
        }

        user.PasswordHash = hasher.HashPassword(user, command.NewPassword);
        user.SecurityStamp = Guid.NewGuid().ToString("N");
        user.ConcurrencyStamp = Guid.NewGuid().ToString("N");
        var now = await context.ReadDatabaseClockUtcAsync(ct);
        await AtomicAccountSecurityPersistence.RevokeOtherActiveSessionsForPasswordChangeAsync(_db,
            context, user.Id,
            command.AuthSessionId,
            now,
            "Password changed by account user",
            ct);
        context.StageSemanticEvent(
            new AtomicSemanticAudit(
                authorization.PortfolioId,
                nameof(ApplicationUser),
                user.Id,
                AuditLogOperation.Updated,
                user.Id,
                ActorLabel: "authentication:password-change",
                NewValues: JsonSerializer.Serialize(new
                {
                    SecurityEvent = "PasswordChanged",
                    TargetUserId = user.Id,
                    command.AccessContextId,
                    PreservedAuthSessionId = command.AuthSessionId,
                    OtherSessionsRevoked = true,
                }),
                ChangeReason: "Password changed by account user."));

        return Result(ChangePasswordOutcome.Changed, command);
    }

    public async Task AuthorizeReplayAsync(
        ChangePasswordCommand command, IAtomicCommandContext context, CancellationToken ct)
    {
        Validate(command);
        _ = await AuthorizeAsync(command, _db, ct)
            ?? throw new UnauthorizedAccessException("The current authentication session is unavailable.");
    }

    private static async Task<AuthorizationProjection?> AuthorizeAsync(
        ChangePasswordCommand command,
        RentalCommandDbContext db,
        CancellationToken ct)
    {
        var utcNow = await db.Database.SqlQuery<DateTime>($"SELECT clock_timestamp() AS \"Value\"").SingleAsync(ct);
        return await (
                from session in db.Set<AuthSession>().AsNoTracking()
                join context in db.Set<WorkspaceAccessContext>().AsNoTracking()
                    on new { AccessContextId = session.ActiveAccessContextId, session.UserId }
                    equals new { AccessContextId = context.Id, context.UserId }
                where session.Id == command.AuthSessionId
                    && session.UserId == command.UserId
                    && session.ActiveAccessContextId == command.AccessContextId
                    && session.Status == AuthSessionStatus.Active
                    && session.RevokedAtUtc == null
                    && session.ExpiresAtUtc > utcNow
                    && context.Id == command.AccessContextId
                    && context.AccessRevision == command.ExpectedAccessRevision
                    && AccessAuthorityDbFunctions.IsEffective(context.Id, command.UserId, utcNow)
                select new AuthorizationProjection(context.PortfolioId))
            .SingleOrDefaultAsync(ct);
    }

    private static ChangePasswordResult Result(
        ChangePasswordOutcome outcome,
        ChangePasswordCommand command) =>
        new(outcome, command.UserId, command.AccessContextId);

    private static void Validate(ChangePasswordCommand command)
    {
        if (command.AuthSessionId == Guid.Empty || command.UserId <= 0 ||
            command.AccessContextId <= 0 || command.ExpectedAccessRevision <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(command));
        }
        ArgumentException.ThrowIfNullOrWhiteSpace(command.CurrentPassword);
        ArgumentException.ThrowIfNullOrWhiteSpace(command.NewPassword);
        if (command.PasswordIntentHash.Length != 64 ||
            !command.PasswordIntentHash.All(Uri.IsHexDigit))
        {
            throw new ArgumentException("Password intent hash must be a SHA-256 hex digest.", nameof(command));
        }
    }

    private sealed record AuthorizationProjection(int PortfolioId);
}
