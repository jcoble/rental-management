using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using RentalCommand.Core.Atomic;
using RentalCommand.Core.Auth;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;

namespace RentalCommand.Data.Auth;

public sealed class BootstrapAccountHandler
    : IAtomicCommandHandler<BootstrapAccountCommand, BootstrapAccountResult>
{
    public async Task<BootstrapAccountResult> HandleAsync(
        BootstrapAccountCommand command,
        IAtomicWriteAttempt attempt,
        CancellationToken ct)
    {
        Validate(command);
        await attempt.Locking.AcquireAsync(AtomicLockResource.ApplicationUser, command.EmailLockId, ct);
        if (await attempt.Persistence.Query<ApplicationUser>().AsNoTracking()
                .AnyAsync(user => user.NormalizedEmail == command.NormalizedEmail, ct))
        {
            return new BootstrapAccountResult(BootstrapAccountOutcome.DuplicateEmail, 0, 0, 0);
        }

        var now = await attempt.Persistence.ReadDatabaseClockUtcAsync(ct);
        attempt.UseDatabaseWallClockForAudit(now);
        var user = new ApplicationUser
        {
            UserName = command.Email,
            NormalizedUserName = command.NormalizedEmail,
            Email = command.Email,
            NormalizedEmail = command.NormalizedEmail,
            EmailConfirmed = command.EmailConfirmed,
            DisplayName = command.DisplayName,
            PasswordHash = command.PasswordHash,
            SecurityStamp = Guid.NewGuid().ToString("N"),
            ConcurrencyStamp = Guid.NewGuid().ToString("N"),
            CreatedAt = now,
        };
        attempt.Persistence.Add(user);
        await attempt.FlushBusinessAsync(ct);

        var workspace = await attempt.AccountSecurity.BootstrapInitialWorkspaceAsync(
            user.Id,
            command.PortfolioName,
            command.ManagementCompanyName,
            command.OwnerName,
            command.Email,
            now,
            ct);
        attempt.StageSemanticEvent(new AtomicSemanticAudit(
            workspace.PortfolioId,
            nameof(ApplicationUser),
            user.Id,
            AuditLogOperation.Created,
            user.Id,
            ActorLabel: "authentication:registration",
            NewValues: JsonSerializer.Serialize(new
            {
                UserId = user.Id,
                workspace.AccessContextId,
                command.EmailConfirmed,
                HasLocalPassword = command.PasswordHash is not null,
            }),
            ChangeReason: "Canonical account and initial workspace created"), now);

        return new BootstrapAccountResult(
            BootstrapAccountOutcome.Created,
            user.Id,
            workspace.PortfolioId,
            workspace.AccessContextId);
    }

    private static void Validate(BootstrapAccountCommand command)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(command.Email);
        ArgumentException.ThrowIfNullOrWhiteSpace(command.NormalizedEmail);
        ArgumentException.ThrowIfNullOrWhiteSpace(command.DisplayName);
        ArgumentException.ThrowIfNullOrWhiteSpace(command.PortfolioName);
        ArgumentException.ThrowIfNullOrWhiteSpace(command.ManagementCompanyName);
        ArgumentException.ThrowIfNullOrWhiteSpace(command.OwnerName);
        ValidateDigest(command.CredentialIntentHash, nameof(command.CredentialIntentHash));
        if (command.EmailLockId == Guid.Empty)
        {
            throw new ArgumentOutOfRangeException(nameof(command.EmailLockId));
        }
    }

    internal static void ValidateDigest(string digest, string parameterName)
    {
        if (digest.Length != 64 || !digest.All(Uri.IsHexDigit))
        {
            throw new ArgumentException("A SHA-256 hex intent digest is required.", parameterName);
        }
    }
}

public sealed class ConfirmAccountEmailHandler
    : IAtomicCommandHandler<ConfirmAccountEmailCommand, ConfirmAccountEmailResult>,
      IAtomicReplayAuthorizer<ConfirmAccountEmailCommand>
{
    public async Task<ConfirmAccountEmailResult> HandleAsync(
        ConfirmAccountEmailCommand command,
        IAtomicWriteAttempt attempt,
        CancellationToken ct)
    {
        Validate(command);
        await attempt.Locking.AcquireAsync(AtomicLockResource.ApplicationUser, command.UserId, ct);
        var user = await attempt.Persistence.Query<ApplicationUser>()
            .SingleOrDefaultAsync(candidate => candidate.Id == command.UserId, ct);
        if (user is null)
        {
            return new ConfirmAccountEmailResult(ConfirmAccountEmailOutcome.UserNotFound, command.UserId);
        }
        if (user.EmailConfirmed)
        {
            return new ConfirmAccountEmailResult(ConfirmAccountEmailOutcome.AlreadyConfirmed, user.Id);
        }
        if (!command.TokenWasValidated)
        {
            return new ConfirmAccountEmailResult(ConfirmAccountEmailOutcome.InvalidToken, user.Id);
        }
        EnsureStamp(user, command.ExpectedSecurityStamp);

        var now = await attempt.Persistence.ReadDatabaseClockUtcAsync(ct);
        var root = await RequireAuditRootAsync(user.Id, attempt.Persistence, now, ct);
        user.EmailConfirmed = true;
        user.ConcurrencyStamp = Guid.NewGuid().ToString("N");
        attempt.StageSemanticEvent(SecurityAudit(
            root.PortfolioId, user.Id, "EmailConfirmed", "Account email confirmed"), now);
        return new ConfirmAccountEmailResult(ConfirmAccountEmailOutcome.Confirmed, user.Id);
    }

    public async Task AuthorizeReplayAsync(
        ConfirmAccountEmailCommand command,
        IAtomicPersistenceSession persistence,
        CancellationToken ct)
    {
        Validate(command);
        var now = await persistence.ReadDatabaseClockUtcAsync(ct);
        _ = await RequireAuditRootAsync(command.UserId, persistence, now, ct);
    }

    private static void Validate(ConfirmAccountEmailCommand command)
    {
        if (command.UserId <= 0) throw new ArgumentOutOfRangeException(nameof(command.UserId));
        BootstrapAccountHandler.ValidateDigest(command.ConfirmationIntentHash, nameof(command.ConfirmationIntentHash));
    }

    internal static void EnsureStamp(ApplicationUser user, string expected)
    {
        if (!string.Equals(user.SecurityStamp ?? string.Empty, expected, StringComparison.Ordinal))
        {
            throw new DbUpdateConcurrencyException("The account security state changed before the command committed.");
        }
    }

    internal static async Task<AtomicEffectiveLoginContext> RequireAuditRootAsync(
        int userId,
        IAtomicPersistenceSession persistence,
        DateTime now,
        CancellationToken ct) =>
        await persistence.ReadEffectiveLoginContextRootAsync(userId, now, ct)
        ?? throw new UnauthorizedAccessException("The account has no current workspace authority.");

    internal static AtomicSemanticAudit SecurityAudit(
        int portfolioId,
        int userId,
        string securityEvent,
        string reason) => new(
        portfolioId,
        nameof(ApplicationUser),
        userId,
        AuditLogOperation.Updated,
        userId,
        ActorLabel: "authentication:account-security",
        NewValues: JsonSerializer.Serialize(new { SecurityEvent = securityEvent, TargetUserId = userId }),
        ChangeReason: reason);
}

public sealed class ResetAccountPasswordHandler
    : IAtomicCommandHandler<ResetAccountPasswordCommand, ResetAccountPasswordResult>,
      IAtomicReplayAuthorizer<ResetAccountPasswordCommand>
{
    public async Task<ResetAccountPasswordResult> HandleAsync(
        ResetAccountPasswordCommand command,
        IAtomicWriteAttempt attempt,
        CancellationToken ct)
    {
        Validate(command);
        await attempt.Locking.AcquireAsync(AtomicLockResource.ApplicationUser, command.UserId, ct);
        var user = await attempt.Persistence.Query<ApplicationUser>()
            .SingleOrDefaultAsync(candidate => candidate.Id == command.UserId, ct);
        if (user is null)
        {
            return new ResetAccountPasswordResult(ResetAccountPasswordOutcome.UserNotFound, command.UserId);
        }
        if (!command.TokenWasValidated)
        {
            return new ResetAccountPasswordResult(ResetAccountPasswordOutcome.InvalidToken, user.Id);
        }
        ConfirmAccountEmailHandler.EnsureStamp(user, command.ExpectedSecurityStamp);

        var now = await attempt.Persistence.ReadDatabaseClockUtcAsync(ct);
        var root = await ConfirmAccountEmailHandler.RequireAuditRootAsync(user.Id, attempt.Persistence, now, ct);
        user.PasswordHash = command.PasswordHash;
        user.SecurityStamp = Guid.NewGuid().ToString("N");
        user.ConcurrencyStamp = Guid.NewGuid().ToString("N");
        user.AccessFailedCount = 0;
        user.LockoutEnd = null;
        user.EmailConfirmed = true;
        attempt.StageSemanticEvent(ConfirmAccountEmailHandler.SecurityAudit(
            root.PortfolioId, user.Id, "PasswordReset", "Password reset completed"), now);
        return new ResetAccountPasswordResult(ResetAccountPasswordOutcome.Reset, user.Id);
    }

    public async Task AuthorizeReplayAsync(
        ResetAccountPasswordCommand command,
        IAtomicPersistenceSession persistence,
        CancellationToken ct)
    {
        Validate(command);
        var now = await persistence.ReadDatabaseClockUtcAsync(ct);
        _ = await ConfirmAccountEmailHandler.RequireAuditRootAsync(command.UserId, persistence, now, ct);
    }

    private static void Validate(ResetAccountPasswordCommand command)
    {
        if (command.UserId <= 0) throw new ArgumentOutOfRangeException(nameof(command.UserId));
        ArgumentException.ThrowIfNullOrWhiteSpace(command.PasswordHash);
        BootstrapAccountHandler.ValidateDigest(command.PasswordIntentHash, nameof(command.PasswordIntentHash));
    }
}

public sealed class ConfirmGoogleAccountEmailHandler
    : IAtomicCommandHandler<ConfirmGoogleAccountEmailCommand, ConfirmAccountEmailResult>
{
    public async Task<ConfirmAccountEmailResult> HandleAsync(
        ConfirmGoogleAccountEmailCommand command,
        IAtomicWriteAttempt attempt,
        CancellationToken ct)
    {
        if (command.UserId <= 0) throw new ArgumentOutOfRangeException(nameof(command.UserId));
        BootstrapAccountHandler.ValidateDigest(command.GoogleSubjectHash, nameof(command.GoogleSubjectHash));
        await attempt.Locking.AcquireAsync(AtomicLockResource.ApplicationUser, command.UserId, ct);
        var user = await attempt.Persistence.Query<ApplicationUser>()
            .SingleOrDefaultAsync(candidate => candidate.Id == command.UserId, ct);
        if (user is null) return new ConfirmAccountEmailResult(ConfirmAccountEmailOutcome.UserNotFound, command.UserId);
        if (user.EmailConfirmed) return new ConfirmAccountEmailResult(ConfirmAccountEmailOutcome.AlreadyConfirmed, user.Id);
        ConfirmAccountEmailHandler.EnsureStamp(user, command.ExpectedSecurityStamp);
        var now = await attempt.Persistence.ReadDatabaseClockUtcAsync(ct);
        var root = await ConfirmAccountEmailHandler.RequireAuditRootAsync(user.Id, attempt.Persistence, now, ct);
        user.EmailConfirmed = true;
        user.ConcurrencyStamp = Guid.NewGuid().ToString("N");
        attempt.StageSemanticEvent(ConfirmAccountEmailHandler.SecurityAudit(
            root.PortfolioId, user.Id, "GoogleEmailConfirmed", "Google-verified account email confirmed"), now);
        return new ConfirmAccountEmailResult(ConfirmAccountEmailOutcome.Confirmed, user.Id);
    }
}

public sealed class AuthEmailOutboxHandler
    : IAtomicCommandHandler<AuthEmailOutboxCommand, AuthEmailOutboxResult>,
      IAtomicReplayAuthorizer<AuthEmailOutboxCommand>
{
    public async Task<AuthEmailOutboxResult> HandleAsync(
        AuthEmailOutboxCommand command,
        IAtomicWriteAttempt attempt,
        CancellationToken ct)
    {
        Validate(command);
        await attempt.Locking.AcquireAsync(AtomicLockResource.ApplicationUser, command.UserId, ct);
        var now = await attempt.Persistence.ReadDatabaseClockUtcAsync(ct);
        var user = await attempt.Persistence.Query<ApplicationUser>().AsNoTracking()
            .SingleOrDefaultAsync(candidate => candidate.Id == command.UserId, ct)
            ?? throw new UnauthorizedAccessException("The account is unavailable.");
        ConfirmAccountEmailHandler.EnsureStamp(user, command.ExpectedSecurityStamp);
        var root = await ConfirmAccountEmailHandler.RequireAuditRootAsync(user.Id, attempt.Persistence, now, ct);
        if (command.ExpectedPortfolioId is { } expectedPortfolioId &&
            root.PortfolioId != expectedPortfolioId)
        {
            throw new UnauthorizedAccessException("The email command is outside the account workspace.");
        }

        attempt.StageOutbox(new OutboxMessage
        {
            PortfolioId = root.PortfolioId,
            MessageType = "email",
            Payload = command.PreparedEmailPayload,
            IdempotencyKey = command.DeliveryIdempotencyKey,
            CreatedAtUtc = now,
            NextAttemptAtUtc = now,
        });
        attempt.StageSemanticEvent(new AtomicSemanticAudit(
            root.PortfolioId,
            nameof(ApplicationUser),
            user.Id,
            AuditLogOperation.Updated,
            user.Id,
            ActorLabel: "authentication:email-outbox",
            NewValues: JsonSerializer.Serialize(new { command.EmailKind, TargetUserId = user.Id }),
            ChangeReason: "Transactional account email enqueued"), now);
        return new AuthEmailOutboxResult(true, user.Id, root.PortfolioId, command.EmailKind);
    }

    public async Task AuthorizeReplayAsync(
        AuthEmailOutboxCommand command,
        IAtomicPersistenceSession persistence,
        CancellationToken ct)
    {
        Validate(command);
        var now = await persistence.ReadDatabaseClockUtcAsync(ct);
        var root = await ConfirmAccountEmailHandler.RequireAuditRootAsync(command.UserId, persistence, now, ct);
        if (command.ExpectedPortfolioId is { } expectedPortfolioId &&
            root.PortfolioId != expectedPortfolioId)
        {
            throw new UnauthorizedAccessException("The email command is outside the account workspace.");
        }
    }

    private static void Validate(AuthEmailOutboxCommand command)
    {
        if (command.UserId <= 0 || command.ExpectedPortfolioId is <= 0)
            throw new ArgumentOutOfRangeException(nameof(command));
        ArgumentException.ThrowIfNullOrWhiteSpace(command.EmailKind);
        ArgumentException.ThrowIfNullOrWhiteSpace(command.PreparedEmailPayload);
        ArgumentException.ThrowIfNullOrWhiteSpace(command.DeliveryIdempotencyKey);
        BootstrapAccountHandler.ValidateDigest(command.EmailIntentHash, nameof(command.EmailIntentHash));
    }
}
