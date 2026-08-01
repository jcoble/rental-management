using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using RentalCommand.Core;
using RentalCommand.Core.Atomic;
using RentalCommand.Core.Authorization;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Sandbox;
using RentalCommand.Data.Sandbox;
using RentalCommand.Api.Services;
using RentalCommand.Data;

namespace RentalCommand.Api.Services.Auth;

public sealed class SandboxLifecycleCommandHandler
    : IAtomicCommandHandler<SandboxLifecycleCommand, SandboxLifecycleResult>
{
    private readonly RentalCommandDbContext _db;

    public SandboxLifecycleCommandHandler(RentalCommandDbContext db) => _db = db;

    public async Task<SandboxLifecycleResult> HandleAsync(
        SandboxLifecycleCommand command,
        IAtomicCommandContext attempt,
        CancellationToken ct)
    {
        Validate(command);
        await attempt.AcquireLockAsync(
            "WorkspaceAccessContext", command.AccessContextId, ct);
        await AuthorizeAsync(command, _db, ct);
        await attempt.AcquireLockAsync("Portfolio", command.PortfolioId, ct);

        var portfolio = await _db.Set<Portfolio>()
            .SingleOrDefaultAsync(row => row.Id == command.PortfolioId, ct);
        if (portfolio is null)
        {
            return Missing(command.PortfolioId);
        }

        return command.Operation switch
        {
            SandboxLifecycleOperation.ApplyOnboardingChoice =>
                await ApplyOnboardingChoiceAsync(command, attempt, portfolio, ct),
            SandboxLifecycleOperation.GoLive =>
                await GoLiveAsync(command, attempt, portfolio, ct),
            _ => throw new ArgumentOutOfRangeException(nameof(command.Operation)),
        };
    }

    public async Task AuthorizeReplayAsync(
        SandboxLifecycleCommand command,
        IAtomicCommandContext context,
        CancellationToken ct)
    {
        Validate(command);
        await AuthorizeAsync(command, _db, ct);
    }

    private async Task<SandboxLifecycleResult> ApplyOnboardingChoiceAsync(
        SandboxLifecycleCommand command,
        IAtomicCommandContext attempt,
        Portfolio portfolio,
        CancellationToken ct)
    {
        if (!SandboxOnboardingSettings.IsPending(portfolio.Settings))
        {
            return State(portfolio);
        }

        var choice = command.OnboardingChoice
            ?? throw new DomainValidationException("A Sandbox onboarding choice is required.");
        var now = command.BusinessNowUtc;
        if (choice == SandboxOnboardingChoice.Sandbox)
        {
            var seed = await DemoDataSeeder.SeedPortfolioCoreAsync(
                _db,
                new SeedDemoPortfolioCommand(
                    command.PortfolioId,
                    true,
                    command.BusinessNowUtc,
                    command.DeliveryIdempotencyKey),
                attempt,
                ct);
            attempt.StageSemanticEvent(new AtomicSemanticAudit(
                command.PortfolioId,
                nameof(Portfolio),
                command.PortfolioId,
                AuditLogOperation.Updated,
                command.ActorUserId,
                ChangeReason: seed.AlreadyPresent
                    ? "Canonical demo facts reconciled during Sandbox onboarding."
                    : "Rich demo portfolio graph seeded during Sandbox onboarding."));
            portfolio.IsSandbox = true;
            portfolio.SandboxSeededAtUtc = now;
            portfolio.Settings = SandboxOnboardingSettings.WriteChoice(
                portfolio.Settings,
                SandboxOnboardingChoice.Sandbox);
        }
        else
        {
            portfolio.IsSandbox = false;
            portfolio.SandboxSeededAtUtc = null;
            portfolio.Settings = SandboxOnboardingSettings.WriteChoice(
                portfolio.Settings,
                SandboxOnboardingChoice.Live);
        }

        portfolio.UpdatedAt = now;
        attempt.BindSemanticAudit(portfolio, Audit(
            command,
            AuditLogOperation.Updated,
            "Sandbox onboarding choice recorded."));
        StageDataUpdate(command, attempt, now, "onboarding-choice");
        await attempt.FlushBusinessAsync(ct);
        return State(portfolio);
    }

    private async Task<SandboxLifecycleResult> GoLiveAsync(
        SandboxLifecycleCommand command,
        IAtomicCommandContext attempt,
        Portfolio portfolio,
        CancellationToken ct)
    {
        if (portfolio.IsSandbox)
        {
            await SandboxLifecyclePersistence.WipePortfolioDataAsync(
                _db,
                attempt,
                command.PortfolioId,
                ct);
        }

        var now = command.BusinessNowUtc;
        portfolio.IsSandbox = false;
        portfolio.SandboxSeededAtUtc = null;
        portfolio.UpdatedAt = now;
        attempt.BindSemanticAudit(portfolio, Audit(
            command,
            AuditLogOperation.Updated,
            "Portfolio graduated from Sandbox to Live."));
        StageDataUpdate(command, attempt, now, "go-live");
        await attempt.FlushBusinessAsync(ct);
        return State(portfolio);
    }

    private async Task AuthorizeAsync(
        SandboxLifecycleCommand command,
        RentalCommandDbContext db,
        CancellationToken ct)
    {
        var securityNowUtc = await AtomicCommandDbClock.ReadDatabaseClockUtcAsync(db, ct);
        var memberships = db.Set<WorkspaceMembership>().AsNoTracking();
        var assignments = db.Set<MembershipRoleAssignment>().AsNoTracking();
        var authorized = await db.Set<WorkspaceAccessContext>()
            .AsNoTracking()
            .Where(context =>
                context.Id == command.AccessContextId &&
                context.UserId == command.ActorUserId &&
                context.PortfolioId == command.PortfolioId &&
                context.Status == WorkspaceAccessContextStatus.Active &&
                context.SuspendedAtUtc == null &&
                context.RevokedAtUtc == null &&
                context.AccessRevision == command.ExpectedAccessRevision &&
                db.Set<AuthSession>().Any(session =>
                    session.Id == command.AuthSessionId &&
                    session.UserId == command.ActorUserId &&
                    session.ActiveAccessContextId == context.Id &&
                    session.Status == AuthSessionStatus.Active &&
                    session.RevokedAtUtc == null &&
                    session.ExpiresAtUtc > securityNowUtc) &&
                memberships.Any(membership =>
                    membership.AccessContextId == context.Id &&
                    membership.PortfolioId == context.PortfolioId &&
                    membership.Status == WorkspaceMembershipStatus.Active &&
                    membership.SuspendedAtUtc == null &&
                    membership.RevokedAtUtc == null &&
                    membership.EffectiveFromUtc <= securityNowUtc &&
                    (membership.EffectiveToUtc == null ||
                     membership.EffectiveToUtc > securityNowUtc) &&
                    assignments.Any(assignment =>
                        assignment.WorkspaceMembershipId == membership.Id &&
                        assignment.PortfolioId == membership.PortfolioId &&
                        assignment.Status == MembershipRoleAssignmentStatus.Active &&
                        assignment.SuspendedAtUtc == null &&
                        assignment.RevokedAtUtc == null &&
                        assignment.EffectiveFromUtc <= securityNowUtc &&
                        (assignment.EffectiveToUtc == null ||
                         assignment.EffectiveToUtc > securityNowUtc) &&
                        assignment.ScopeKind ==
                            MembershipRoleAssignmentScopeKind.AllProperties &&
                        assignment.RoleProfile!.Capabilities.Any(profileCapability =>
                            profileCapability.CapabilityDefinition!.Key ==
                                CapabilityKeys.AccountDestructiveActions &&
                            profileCapability.CapabilityDefinition.AuthorizationTargetKind ==
                                CapabilityAuthorizationTargetKind.Workspace))))
            .AnyAsync(ct);
        if (!authorized)
        {
            throw new UnauthorizedAccessException(
                "The active workspace context cannot mutate this Sandbox lifecycle.");
        }
    }

    private void Validate(SandboxLifecycleCommand command)
    {
        if (command.PortfolioId <= 0
            || command.ActorUserId <= 0
            || command.AuthSessionId == Guid.Empty
            || command.AccessContextId <= 0
            || command.ExpectedAccessRevision <= 0
            || command.BusinessNowUtc.Kind != DateTimeKind.Utc
            || string.IsNullOrWhiteSpace(command.DeliveryIdempotencyKey)
            || command.DeliveryIdempotencyKey.Length > 200
            || !Enum.IsDefined(command.Operation))
        {
            throw new DomainValidationException("A complete Sandbox lifecycle command is required.");
        }

        if (command.Operation == SandboxLifecycleOperation.ApplyOnboardingChoice
            && (command.OnboardingChoice is null || !Enum.IsDefined(command.OnboardingChoice.Value)))
        {
            throw new DomainValidationException("A valid Sandbox onboarding choice is required.");
        }

        if (command.Operation == SandboxLifecycleOperation.GoLive && command.OnboardingChoice is not null)
        {
            throw new DomainValidationException("Go Live does not accept an onboarding choice.");
        }
    }

    private void StageDataUpdate(
        SandboxLifecycleCommand command,
        IAtomicCommandContext attempt,
        DateTime now,
        string suffix) =>
        attempt.StageOutbox(new OutboxMessage
        {
            PortfolioId = command.PortfolioId,
            MessageType = "data-update",
            Payload = JsonSerializer.Serialize(new
            {
                entityType = nameof(Portfolio),
                entityId = command.PortfolioId,
                operation = "update",
                data = new { },
            }),
            IdempotencyKey = $"{command.DeliveryIdempotencyKey}:{suffix}",
            CreatedAtUtc = now,
            NextAttemptAtUtc = now,
        });

    private AtomicSemanticAudit Audit(
        SandboxLifecycleCommand command,
        AuditLogOperation operation,
        string reason) =>
        new(
            command.PortfolioId,
            nameof(Portfolio),
            command.PortfolioId,
            operation,
            command.ActorUserId,
            ChangeReason: reason);

    private SandboxLifecycleResult State(Portfolio portfolio) =>
        new(
            true,
            portfolio.Id,
            portfolio.IsSandbox,
            portfolio.SandboxSeededAtUtc,
            SandboxOnboardingSettings.IsPending(portfolio.Settings));

    private SandboxLifecycleResult Missing(int portfolioId) =>
        new(false, portfolioId, false, null, false);
}

internal static class SandboxOnboardingSettings
{
    private const string OnboardingKey = "onboarding";
    private const string ChoiceKey = "choice";
    private const string PendingValue = "pending";
    private const string SandboxValue = "sandbox";
    private const string LiveValue = "live";

    private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };

    public static bool IsPending(string? settingsJson) => ReadChoice(settingsJson) == PendingValue;

    public static string WriteChoice(string? settingsJson, SandboxOnboardingChoice choice)
    {
        var root = ParseRoot(settingsJson);
        Dictionary<string, object?> onboarding;
        if (root.TryGetValue(OnboardingKey, out var existing)
            && existing is JsonElement element
            && element.ValueKind == JsonValueKind.Object)
        {
            onboarding = JsonSerializer.Deserialize<Dictionary<string, object?>>(element.GetRawText())
                ?? new Dictionary<string, object?>();
        }
        else if (existing is Dictionary<string, object?> existingDict)
        {
            onboarding = existingDict;
        }
        else
        {
            onboarding = new Dictionary<string, object?>();
        }

        onboarding[ChoiceKey] = choice == SandboxOnboardingChoice.Sandbox
            ? SandboxValue
            : LiveValue;
        root[OnboardingKey] = onboarding;
        return JsonSerializer.Serialize(root, WriteOptions);
    }

    private static string ReadChoice(string? settingsJson)
    {
        if (string.IsNullOrWhiteSpace(settingsJson))
        {
            return PendingValue;
        }

        try
        {
            using var doc = JsonDocument.Parse(settingsJson);
            if (doc.RootElement.TryGetProperty(OnboardingKey, out var onboarding)
                && onboarding.ValueKind == JsonValueKind.Object
                && onboarding.TryGetProperty(ChoiceKey, out var choice)
                && choice.ValueKind == JsonValueKind.String)
            {
                return choice.GetString()?.Trim().ToLowerInvariant() switch
                {
                    SandboxValue => SandboxValue,
                    LiveValue => LiveValue,
                    _ => PendingValue,
                };
            }
        }
        catch (JsonException)
        {
            return PendingValue;
        }

        return PendingValue;
    }

    private static Dictionary<string, object?> ParseRoot(string? settingsJson)
    {
        if (string.IsNullOrWhiteSpace(settingsJson))
        {
            return new Dictionary<string, object?>();
        }

        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, object?>>(settingsJson)
                ?? new Dictionary<string, object?>();
        }
        catch (JsonException)
        {
            return new Dictionary<string, object?>();
        }
    }
}
