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
    : IAtomicCommandHandler<ChangePasswordCommand, ChangePasswordResult>,
      IAtomicReplayAuthorizer<ChangePasswordCommand>
{
    public async Task<ChangePasswordResult> HandleAsync(
        ChangePasswordCommand command,
        IAtomicWriteAttempt attempt,
        CancellationToken ct)
    {
        Validate(command);
        var authorization = await AuthorizeAsync(command, attempt.Persistence, ct);
        if (authorization is null)
        {
            return Result(ChangePasswordOutcome.AccessUnavailable, command);
        }

        await attempt.Locking.AcquireAsync(AtomicLockResource.ApplicationUser, command.UserId, ct);
        var user = await attempt.Persistence.Query<ApplicationUser>()
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
        // SaveChanges call out of the atomic attempt. The exact current hash is verified only after
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
        attempt.StageSemanticEvent(
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
                }),
                ChangeReason: "Password changed by account user."));

        return Result(ChangePasswordOutcome.Changed, command);
    }

    public async Task AuthorizeReplayAsync(
        ChangePasswordCommand command,
        IAtomicPersistenceSession persistence,
        CancellationToken ct)
    {
        Validate(command);
        _ = await AuthorizeAsync(command, persistence, ct)
            ?? throw new UnauthorizedAccessException("The current authentication session is unavailable.");
    }

    private static async Task<AuthorizationProjection?> AuthorizeAsync(
        ChangePasswordCommand command,
        IAtomicPersistenceSession persistence,
        CancellationToken ct)
    {
        var utcNow = await persistence.ReadDatabaseClockUtcAsync(ct);
        return await (
                from session in persistence.Query<AuthSession>().AsNoTracking()
                join context in persistence.Query<WorkspaceAccessContext>().AsNoTracking()
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
