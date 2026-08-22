using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using RentalCommand.Core.Atomic;
using RentalCommand.Core.Auth;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Data.Accounting;
using RentalCommand.Data.Notifications;

namespace RentalCommand.Data.Auth;

public static class AuthSessionWriteSupport
{
    public static TransactionalWrite<TCommand, TResult> Write<TCommand, TResult>(
        TCommand command,
        Func<TCommand, IAtomicCommandContext, CancellationToken, Task<TResult>> executeAsync,
        Func<TCommand, IAtomicCommandContext, CancellationToken, Task> authorizeReplayAsync)
        where TCommand : notnull, IAtomicCommandData
        where TResult : notnull
    {
        var (operationName, resultContract) = command switch
        {
            BootstrapAccountCommand => ("auth.account.bootstrap", "auth-account-bootstrap-result:v1"),
            ConfirmAccountEmailCommand => ("auth.email.confirm", "auth-email-confirm-result:v1"),
            ResetAccountPasswordCommand => ("auth.password.reset", "auth-password-reset-result:v1"),
            ConfirmGoogleAccountEmailCommand => ("auth.email.google-confirm", "auth-email-confirm-result:v1"),
            AuthEmailOutboxCommand email => ($"auth.email.{email.EmailKind}", "auth-email-outbox-result:v1"),
            IssueLoginContextSelectionChallengeCommand => ("auth-context-selection:issue", "auth-context-selection-challenge-result:v1"),
            StartAuthSessionCommand => ("auth-session:start", "auth-session-start-result:v1"),
            IssueSessionRefreshCredentialCommand => ("session-refresh:issue", "session-refresh-mutation-result.v1"),
            RotateSessionRefreshCredentialCommand => ("session-refresh:rotate", "auth-session-refresh-rotation-result:v1"),
            SwitchAuthSessionContextCommand => ("auth-context:switch", "auth-session-context-switch-result:v1"),
            ChangePasswordCommand => ("auth.password.change", "auth-password-change-result:v1"),
            RevokeAuthSessionCommand => ("auth-session:revoke", "auth-session-revoke-result:v1"),
            _ => throw new ArgumentOutOfRangeException(nameof(command)),
        };

        return new TransactionalWrite<TCommand, TResult>(
            operationName,
            WriteIdempotencyPolicy.Required,
            command,
            resultContract,
            WriteLockPlan.None,
            executeAsync,
            authorizeReplayAsync);
    }

    internal static InvalidOperationException RetiredPath() => new(
        "Legacy auth/session writes are retired; use the shared write executor.");
}

public sealed class BootstrapAccountRule
{
    private readonly RentalCommandDbContext _db;

    public BootstrapAccountRule(RentalCommandDbContext db) => _db = db;

    public async Task<BootstrapAccountResult> ExecuteAsync(
        BootstrapAccountCommand command,
        IAtomicCommandContext context,
        CancellationToken ct)
    {
        Validate(command);
        await context.AcquireLockAsync("ApplicationUser", command.EmailLockId, ct);
        if (await _db.Set<ApplicationUser>().AsNoTracking()
                .AnyAsync(user => user.NormalizedEmail == command.NormalizedEmail, ct))
        {
            return new BootstrapAccountResult(BootstrapAccountOutcome.DuplicateEmail, 0, 0, 0);
        }

        var now = await context.ReadDatabaseClockUtcAsync(ct);
        context.UseDatabaseWallClockForAudit(now);
        var user = new ApplicationUser
        {
            UserName = command.Email,
            NormalizedUserName = command.NormalizedEmail,
            Email = command.Email,
            NormalizedEmail = command.NormalizedEmail,
            EmailConfirmed = command.EmailConfirmed,
            LockoutEnabled = true,
            DisplayName = command.DisplayName,
            PasswordHash = command.PasswordHash,
            SecurityStamp = Guid.NewGuid().ToString("N"),
            ConcurrencyStamp = Guid.NewGuid().ToString("N"),
            CreatedAt = now,
            TermsPrivacyAccepted = command.TermsPrivacyAccepted,
            TermsPrivacyAcceptedAtUtc = command.TermsPrivacyAccepted ? now : null,
            TermsPrivacyVersion = command.TermsPrivacyAccepted
                ? ApplicationUser.RegistrationTermsPrivacyVersion
                : null,
        };
        _db.Add(user);
        await context.FlushBusinessAsync(ct);

        var workspace = await AtomicAccountSecurityPersistence.BootstrapInitialWorkspaceAsync(_db,
            context, user.Id,
            command.PortfolioName,
            command.ManagementCompanyName,
            command.OwnerName,
            command.Email,
            now,
            ct);

        // New portfolios receive the locked chart in this same atomic registration transaction.
        await new ChartOfAccountsSeedService(_db).SeedAsync(workspace.PortfolioId, ct);

        var legalReplacementQuery =
                from latest in _db.Set<SystemNoticeTemplateVersion>()
                where latest.Version > SuppliedNoticeTemplateBaseline.Version
                    && !_db.Set<SystemNoticeTemplateVersion>().Any(candidate =>
                        candidate.SystemKey == latest.SystemKey && candidate.Version > latest.Version)
                join current in _db.Set<WorkspaceNoticeTemplateVersion>()
                    on new { latest.SystemKey, PortfolioId = workspace.PortfolioId }
                    equals new { current.SystemKey, current.PortfolioId }
                join policy in _db.Set<TenantNoticePolicy>()
                    on new
                    {
                        current.PortfolioId,
                        AutomationKey = current.SystemKey,
                        WorkspaceNoticeTemplateVersionId = current.Id,
                    }
                    equals new
                    {
                        policy.PortfolioId,
                        policy.AutomationKey,
                        policy.WorkspaceNoticeTemplateVersionId,
                    }
                where latest.Classification == NoticeClassification.Legal
                    && current.Version == SuppliedNoticeTemplateBaseline.Version
                    && !current.IsCustomized
                    && policy.Mode == TenantNoticeMode.Draft
                    && policy.Classification == NoticeClassification.Legal
                orderby latest.SystemKey
                select new { Latest = latest, Current = current, Policy = policy };

        legalReplacementQuery = legalReplacementQuery
            .TagWith("TSK-733 fresh-workspace latest legal notice template selection");

        var legalReplacementCount = await legalReplacementQuery.CountAsync(ct);
        var latestLegalDefinitionCount = SuppliedNoticeTemplateBaseline.V3Legal.Count;
        if (legalReplacementCount != latestLegalDefinitionCount)
        {
            throw new InvalidOperationException(
                "Fresh workspace notice bootstrap did not resolve both legal supplied-template replacements.");
        }

        var legalReplacements = await legalReplacementQuery.ToListAsync(ct);
        var replacementRows = legalReplacements.Select(row => new WorkspaceNoticeTemplateVersion
        {
            PortfolioId = workspace.PortfolioId,
            SystemKey = row.Latest.SystemKey,
            Version = row.Latest.Version,
            BasedOnSystemTemplateVersionId = row.Latest.Id,
            IsCustomized = false,
            Subject = row.Latest.Subject,
            Body = row.Latest.Body,
            JurisdictionCode = row.Latest.JurisdictionCode,
            JurisdictionReviewedAtUtc = null,
            JurisdictionReviewedByUserId = null,
            CreatedByUserId = user.Id,
            CreatedAtUtc = now,
        }).ToArray();
        _db.AddRange(replacementRows);
        await context.FlushBusinessAsync(ct);

        for (var index = 0; index < legalReplacements.Count; index++)
        {
            var selected = legalReplacements[index];
            var replacement = replacementRows[index];
            selected.Policy.WorkspaceNoticeTemplateVersionId = replacement.Id;
            selected.Policy.UpdatedAtUtc = now;

            context.StageSemanticEvent(new AtomicSemanticAudit(
                workspace.PortfolioId,
                nameof(WorkspaceNoticeTemplateVersion),
                replacement.Id,
                AuditLogOperation.Created,
                user.Id,
                ActorLabel: "authentication:registration",
                NewValues: JsonSerializer.Serialize(new
                {
                    replacement.SystemKey,
                    ReplacedWorkspaceTemplateVersionId = selected.Current.Id,
                    ReplacementWorkspaceTemplateVersionId = replacement.Id,
                    replacement.BasedOnSystemTemplateVersionId,
                    replacement.Version,
                }),
                ChangeReason: "Fresh workspace supplied legal template replaced with latest immutable version"), now);
            context.StageSemanticEvent(new AtomicSemanticAudit(
                workspace.PortfolioId,
                nameof(TenantNoticePolicy),
                selected.Policy.Id,
                AuditLogOperation.Updated,
                user.Id,
                ActorLabel: "authentication:registration",
                NewValues: JsonSerializer.Serialize(new
                {
                    selected.Policy.AutomationKey,
                    PreviousWorkspaceTemplateVersionId = selected.Current.Id,
                    WorkspaceTemplateVersionId = replacement.Id,
                    Mode = TenantNoticeMode.Draft,
                }),
                ChangeReason: "Fresh workspace Draft policy rebound to latest supplied legal template"), now);
        }

        await context.FlushBusinessAsync(ct);
        var deletedTemplateCount =
            await AtomicAccountSecurityPersistence.DeleteFreshWorkspaceSuppliedNoticeTemplateVersionsAsync(_db,
                context, user.Id,
                workspace.PortfolioId,
                legalReplacements.Select(row => row.Current.Id).ToArray(),
                ct);
        if (deletedTemplateCount != latestLegalDefinitionCount)
        {
            throw new InvalidOperationException(
                "Fresh workspace notice bootstrap did not remove both replaced v1 templates.");
        }

        var finalNoticeBootstrap = await _db.Set<Portfolio>()
            .Where(portfolio => portfolio.Id == workspace.PortfolioId)
            .Select(portfolio => new
            {
                TemplateCount = _db.Set<WorkspaceNoticeTemplateVersion>()
                    .Count(template => template.PortfolioId == portfolio.Id),
                PolicyCount = _db.Set<TenantNoticePolicy>()
                    .Count(policy => policy.PortfolioId == portfolio.Id),
                NonLatestTemplateCount = _db.Set<WorkspaceNoticeTemplateVersion>()
                    .Count(template => template.PortfolioId == portfolio.Id &&
                        _db.Set<SystemNoticeTemplateVersion>().Any(system =>
                            system.SystemKey == template.SystemKey && system.Version > template.Version)),
                IncorrectPolicyBindingCount = _db.Set<TenantNoticePolicy>()
                    .Count(policy => policy.PortfolioId == portfolio.Id &&
                        (policy.Mode != TenantNoticeMode.Draft ||
                         !_db.Set<WorkspaceNoticeTemplateVersion>().Any(template =>
                             template.Id == policy.WorkspaceNoticeTemplateVersionId &&
                             template.PortfolioId == policy.PortfolioId &&
                             !_db.Set<SystemNoticeTemplateVersion>().Any(system =>
                                 system.SystemKey == template.SystemKey &&
                                 system.Version > template.Version)))),
            })
            .TagWith("TSK-733 exact fresh-workspace latest notice bootstrap assertion")
            .SingleAsync(ct);

        if (finalNoticeBootstrap.TemplateCount != 5 ||
            finalNoticeBootstrap.PolicyCount != 5 ||
            finalNoticeBootstrap.NonLatestTemplateCount != 0 ||
            finalNoticeBootstrap.IncorrectPolicyBindingCount != 0)
        {
            throw new InvalidOperationException(
                "Fresh workspace notice bootstrap must contain exactly five latest templates and five Draft policies.");
        }

        context.StageSemanticEvent(new AtomicSemanticAudit(
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

    public Task AuthorizeReplayAsync(
        BootstrapAccountCommand command, IAtomicCommandContext context, CancellationToken ct) =>
        throw AuthSessionWriteSupport.RetiredPath();

    public async Task AuthorizeAsync(
        BootstrapAccountCommand command,
        IAtomicCommandContext context,
        CancellationToken ct)
    {
        Validate(command);

        var userExists = await _db.Set<ApplicationUser>()
            .IgnoreQueryFilters()
            .AsNoTracking()
            .AnyAsync(user =>
                user.NormalizedEmail == command.NormalizedEmail &&
                user.Email == command.Email,
                ct);
        if (!userExists)
        {
            throw new UnauthorizedAccessException("The original account bootstrap identity is unavailable.");
        }
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

public sealed class ConfirmAccountEmailRule
{
    private readonly RentalCommandDbContext _db;

    public ConfirmAccountEmailRule(RentalCommandDbContext db) => _db = db;

    public async Task<ConfirmAccountEmailResult> ExecuteAsync(
        ConfirmAccountEmailCommand command,
        IAtomicCommandContext context,
        CancellationToken ct)
    {
        Validate(command);
        await context.AcquireLockAsync("ApplicationUser", command.UserId, ct);
        var user = await _db.Set<ApplicationUser>()
            .SingleOrDefaultAsync(candidate => candidate.Id == command.UserId, ct);
        if (user is null)
        {
            return new ConfirmAccountEmailResult(ConfirmAccountEmailOutcome.UserNotFound, command.UserId);
        }
        if (!command.TokenWasValidated)
        {
            return new ConfirmAccountEmailResult(ConfirmAccountEmailOutcome.InvalidToken, user.Id);
        }
        if (user.EmailConfirmed)
        {
            return new ConfirmAccountEmailResult(ConfirmAccountEmailOutcome.AlreadyConfirmed, user.Id);
        }
        EnsureStamp(user, command.ExpectedSecurityStamp);

        var now = await context.ReadDatabaseClockUtcAsync(ct);
        var root = await RequireAuditRootAsync(user.Id, _db, now, ct);
        user.EmailConfirmed = true;
        user.ConcurrencyStamp = Guid.NewGuid().ToString("N");
        context.StageSemanticEvent(SecurityAudit(
            root,
            user.Id,
            "EmailConfirmed",
            command.ConfirmationIntentHash,
            "Account email confirmed"), now);
        return new ConfirmAccountEmailResult(ConfirmAccountEmailOutcome.Confirmed, user.Id);
    }

    public Task AuthorizeReplayAsync(
        ConfirmAccountEmailCommand command, IAtomicCommandContext context, CancellationToken ct) =>
        throw AuthSessionWriteSupport.RetiredPath();

    public async Task AuthorizeAsync(
        ConfirmAccountEmailCommand command, IAtomicCommandContext context, CancellationToken ct)
    {
        Validate(command);
        var now = await _db.Database.SqlQuery<DateTime>($"SELECT clock_timestamp() AS \"Value\"").SingleAsync(ct);
        _ = await RequireAuditRootAsync(command.UserId, _db, now, ct);
    }

    private static void Validate(ConfirmAccountEmailCommand command)
    {
        if (command.UserId <= 0) throw new ArgumentOutOfRangeException(nameof(command.UserId));
        BootstrapAccountRule.ValidateDigest(command.ConfirmationIntentHash, nameof(command.ConfirmationIntentHash));
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
        RentalCommandDbContext db,
        DateTime now,
        CancellationToken ct) =>
        await AtomicEffectiveLoginContextQueries.ReadRootAsync(db, userId, now, ct)
        ?? throw new UnauthorizedAccessException("The account has no current workspace authority.");

    internal static AtomicSemanticAudit SecurityAudit(
        AtomicEffectiveLoginContext root,
        int userId,
        string securityEvent,
        string securityIntentHash,
        string reason) => new(
        root.PortfolioId,
        nameof(ApplicationUser),
        userId,
        AuditLogOperation.Updated,
        userId,
        ActorLabel: "authentication:account-security",
        NewValues: JsonSerializer.Serialize(new
        {
            SecurityEvent = securityEvent,
            SecurityIntentHash = securityIntentHash,
            TargetUserId = userId,
            AuditRootAccessContextId = root.AccessContextId,
        }),
        ChangeReason: reason);
}

public sealed class ResetAccountPasswordRule
{
    private readonly RentalCommandDbContext _db;

    public ResetAccountPasswordRule(RentalCommandDbContext db) => _db = db;

    public async Task<ResetAccountPasswordResult> ExecuteAsync(
        ResetAccountPasswordCommand command,
        IAtomicCommandContext context,
        CancellationToken ct)
    {
        Validate(command);
        await context.AcquireLockAsync("ApplicationUser", command.UserId, ct);
        var user = await _db.Set<ApplicationUser>()
            .SingleOrDefaultAsync(candidate => candidate.Id == command.UserId, ct);
        if (user is null)
        {
            return new ResetAccountPasswordResult(ResetAccountPasswordOutcome.UserNotFound, command.UserId);
        }
        if (!command.TokenWasValidated)
        {
            return new ResetAccountPasswordResult(ResetAccountPasswordOutcome.InvalidToken, user.Id);
        }
        ConfirmAccountEmailRule.EnsureStamp(user, command.ExpectedSecurityStamp);

        var now = await context.ReadDatabaseClockUtcAsync(ct);
        var root = await ConfirmAccountEmailRule.RequireAuditRootAsync(user.Id, _db, now, ct);
        user.PasswordHash = command.PasswordHash;
        user.SecurityStamp = Guid.NewGuid().ToString("N");
        user.ConcurrencyStamp = Guid.NewGuid().ToString("N");
        user.AccessFailedCount = 0;
        user.LockoutEnd = null;
        user.EmailConfirmed = true;
        await AtomicAccountSecurityPersistence.RevokeActiveSessionsForPasswordResetAsync(_db,
            context, user.Id,
            now,
            "Password reset completed",
            ct);
        context.StageSemanticEvent(ConfirmAccountEmailRule.SecurityAudit(
            root,
            user.Id,
            "PasswordReset",
            command.PasswordIntentHash,
            "Password reset completed"), now);
        return new ResetAccountPasswordResult(ResetAccountPasswordOutcome.Reset, user.Id);
    }

    public Task AuthorizeReplayAsync(
        ResetAccountPasswordCommand command, IAtomicCommandContext context, CancellationToken ct) =>
        throw AuthSessionWriteSupport.RetiredPath();

    public async Task AuthorizeAsync(
        ResetAccountPasswordCommand command, IAtomicCommandContext context, CancellationToken ct)
    {
        Validate(command);
        var now = await _db.Database.SqlQuery<DateTime>($"SELECT clock_timestamp() AS \"Value\"").SingleAsync(ct);
        _ = await ConfirmAccountEmailRule.RequireAuditRootAsync(command.UserId, _db, now, ct);
    }

    private static void Validate(ResetAccountPasswordCommand command)
    {
        if (command.UserId <= 0) throw new ArgumentOutOfRangeException(nameof(command.UserId));
        ArgumentException.ThrowIfNullOrWhiteSpace(command.PasswordHash);
        BootstrapAccountRule.ValidateDigest(command.PasswordIntentHash, nameof(command.PasswordIntentHash));
    }
}

public sealed class ConfirmGoogleAccountEmailRule
{
    private readonly RentalCommandDbContext _db;

    public ConfirmGoogleAccountEmailRule(RentalCommandDbContext db) => _db = db;

    public async Task<ConfirmAccountEmailResult> ExecuteAsync(
        ConfirmGoogleAccountEmailCommand command,
        IAtomicCommandContext context,
        CancellationToken ct)
    {
        if (command.UserId <= 0) throw new ArgumentOutOfRangeException(nameof(command.UserId));
        BootstrapAccountRule.ValidateDigest(command.GoogleSubjectHash, nameof(command.GoogleSubjectHash));
        await context.AcquireLockAsync("ApplicationUser", command.UserId, ct);
        var user = await _db.Set<ApplicationUser>()
            .SingleOrDefaultAsync(candidate => candidate.Id == command.UserId, ct);
        if (user is null) return new ConfirmAccountEmailResult(ConfirmAccountEmailOutcome.UserNotFound, command.UserId);
        if (user.EmailConfirmed) return new ConfirmAccountEmailResult(ConfirmAccountEmailOutcome.AlreadyConfirmed, user.Id);
        ConfirmAccountEmailRule.EnsureStamp(user, command.ExpectedSecurityStamp);
        var now = await context.ReadDatabaseClockUtcAsync(ct);
        var root = await ConfirmAccountEmailRule.RequireAuditRootAsync(user.Id, _db, now, ct);
        user.EmailConfirmed = true;
        user.ConcurrencyStamp = Guid.NewGuid().ToString("N");
        context.StageSemanticEvent(ConfirmAccountEmailRule.SecurityAudit(
            root,
            user.Id,
            "GoogleEmailConfirmed",
            command.GoogleSubjectHash,
            "Google-verified account email confirmed"), now);
        return new ConfirmAccountEmailResult(ConfirmAccountEmailOutcome.Confirmed, user.Id);
    }

    public Task AuthorizeReplayAsync(
        ConfirmGoogleAccountEmailCommand command, IAtomicCommandContext context, CancellationToken ct) =>
        throw AuthSessionWriteSupport.RetiredPath();

    public async Task AuthorizeAsync(
        ConfirmGoogleAccountEmailCommand command, IAtomicCommandContext context, CancellationToken ct)
    {
        if (command.UserId <= 0) throw new ArgumentOutOfRangeException(nameof(command.UserId));
        BootstrapAccountRule.ValidateDigest(command.GoogleSubjectHash, nameof(command.GoogleSubjectHash));
        var now = await _db.Database.SqlQuery<DateTime>($"SELECT clock_timestamp() AS \"Value\"").SingleAsync(ct);
        _ = await ConfirmAccountEmailRule.RequireAuditRootAsync(command.UserId, _db, now, ct);
    }
}

public sealed class AuthEmailOutboxRule
{
    private readonly RentalCommandDbContext _db;

    public AuthEmailOutboxRule(RentalCommandDbContext db) => _db = db;

    public async Task<AuthEmailOutboxResult> ExecuteAsync(
        AuthEmailOutboxCommand command,
        IAtomicCommandContext context,
        CancellationToken ct)
    {
        Validate(command);
        await context.AcquireLockAsync("ApplicationUser", command.UserId, ct);
        var now = await context.ReadDatabaseClockUtcAsync(ct);
        var user = await _db.Set<ApplicationUser>().AsNoTracking()
            .SingleOrDefaultAsync(candidate => candidate.Id == command.UserId, ct)
            ?? throw new UnauthorizedAccessException("The account is unavailable.");
        ConfirmAccountEmailRule.EnsureStamp(user, command.ExpectedSecurityStamp);
        var root = await ConfirmAccountEmailRule.RequireAuditRootAsync(user.Id, _db, now, ct);
        if (command.ExpectedPortfolioId is { } expectedPortfolioId &&
            root.PortfolioId != expectedPortfolioId)
        {
            throw new UnauthorizedAccessException("The email command is outside the account workspace.");
        }

        context.StageOutbox(new OutboxMessage
        {
            PortfolioId = root.PortfolioId,
            MessageType = "email",
            Payload = command.PreparedEmailPayload,
            IdempotencyKey = command.DeliveryIdempotencyKey,
            CreatedAtUtc = now,
            NextAttemptAtUtc = now,
        });
        context.StageSemanticEvent(new AtomicSemanticAudit(
            root.PortfolioId,
            nameof(ApplicationUser),
            user.Id,
            AuditLogOperation.Updated,
            user.Id,
            ActorLabel: "authentication:email-outbox",
            NewValues: JsonSerializer.Serialize(new
            {
                command.EmailKind,
                TargetUserId = user.Id,
                AuditRootAccessContextId = root.AccessContextId,
                command.DeliveryIdempotencyKey,
            }),
            ChangeReason: "Transactional account email enqueued"), now);
        return new AuthEmailOutboxResult(true, user.Id, root.PortfolioId, command.EmailKind);
    }

    public Task AuthorizeReplayAsync(
        AuthEmailOutboxCommand command, IAtomicCommandContext context, CancellationToken ct) =>
        throw AuthSessionWriteSupport.RetiredPath();

    public async Task AuthorizeAsync(
        AuthEmailOutboxCommand command, IAtomicCommandContext context, CancellationToken ct)
    {
        Validate(command);
        var now = await _db.Database.SqlQuery<DateTime>($"SELECT clock_timestamp() AS \"Value\"").SingleAsync(ct);
        var root = await ConfirmAccountEmailRule.RequireAuditRootAsync(command.UserId, _db, now, ct);
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
        if (command.EmailKind is not ("email-confirmation" or "password-reset"))
            throw new ArgumentOutOfRangeException(nameof(command.EmailKind));
        ArgumentException.ThrowIfNullOrWhiteSpace(command.PreparedEmailPayload);
        ArgumentException.ThrowIfNullOrWhiteSpace(command.DeliveryIdempotencyKey);
        BootstrapAccountRule.ValidateDigest(command.EmailIntentHash, nameof(command.EmailIntentHash));
    }
}
