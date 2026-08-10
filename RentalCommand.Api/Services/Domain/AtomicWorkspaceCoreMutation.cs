using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using RentalCommand.Api.DTOs;
using RentalCommand.Core.Atomic;
using RentalCommand.Core.Authorization;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Api.Services;
using RentalCommand.Data;

namespace RentalCommand.Api.Services.Domain;

public enum AtomicWorkspaceCoreMutationOperation
{
    UpdatePortfolio,
    DeletePortfolio,
    RotateApplicationLink,
}

public sealed record AtomicWorkspaceCoreMutationCommand(
    int PortfolioId,
    int ActorUserId,
    [property: AtomicFingerprintIgnore]
    Guid AuthSessionId,
    [property: AtomicFingerprintIgnore]
    int AccessContextId,
    [property: AtomicFingerprintIgnore]
    long ExpectedAccessRevision,
    AtomicWorkspaceCoreMutationOperation Operation,
    string RequestJson,
    [property: AtomicFingerprintIgnore]
    string DeliveryIdempotencyKey) : IAtomicCommandData;

public sealed record AtomicWorkspaceCoreMutationResult(
    bool Found,
    bool Applied,
    string? ResponseJson = null,
    string? PublicApplicationToken = null);

public sealed class AtomicWorkspaceCoreMutationHandler
    : IAtomicCommandHandler<AtomicWorkspaceCoreMutationCommand, AtomicWorkspaceCoreMutationResult>
{
    private readonly RentalCommandDbContext _db;

    public AtomicWorkspaceCoreMutationHandler(RentalCommandDbContext db) => _db = db;

    public async Task<AtomicWorkspaceCoreMutationResult> HandleAsync(
        AtomicWorkspaceCoreMutationCommand command,
        IAtomicCommandContext attempt,
        CancellationToken ct)
    {
        Validate(command);
        await attempt.AcquireLockAsync("AuthSession", command.AuthSessionId, ct);
        await attempt.AcquireLockAsync(
            "WorkspaceAccessContext", command.AccessContextId, ct);
        await attempt.AcquireLockAsync("Portfolio", command.PortfolioId, ct);

        var now = await AtomicCommandDbClock.ReadDatabaseClockUtcAsync(_db, ct);
        if (!await IsAuthorizedAsync(command, _db, now, ct))
        {
            throw new UnauthorizedAccessException(
                "Workspace access changed or does not permit this operation. Refresh and try again.");
        }

        var portfolio = await _db.Set<Portfolio>()
            .SingleOrDefaultAsync(candidate => candidate.Id == command.PortfolioId, ct);
        if (portfolio is null)
        {
            return new AtomicWorkspaceCoreMutationResult(false, false);
        }

        attempt.UseDatabaseWallClockForAudit(now);
        return command.Operation switch
        {
            AtomicWorkspaceCoreMutationOperation.UpdatePortfolio =>
                await UpdatePortfolioAsync(command, portfolio, attempt, now, ct),
            AtomicWorkspaceCoreMutationOperation.DeletePortfolio =>
                await DeletePortfolioAsync(command, portfolio, attempt, now, ct),
            AtomicWorkspaceCoreMutationOperation.RotateApplicationLink =>
                await RotateApplicationLinkAsync(command, portfolio, attempt, now, ct),
            _ => throw new ArgumentOutOfRangeException(nameof(command.Operation)),
        };
    }

    public async Task AuthorizeReplayAsync(
        AtomicWorkspaceCoreMutationCommand command,
        IAtomicCommandContext context,
        CancellationToken ct)
    {
        Validate(command);
        var now = await context.ReadDatabaseClockUtcAsync(ct);
        if (!await IsAuthorizedAsync(command, _db, now, ct))
        {
            throw new UnauthorizedAccessException(
                "Workspace access changed or does not permit this operation. Refresh and try again.");
        }
    }

    private async Task<AtomicWorkspaceCoreMutationResult> UpdatePortfolioAsync(
        AtomicWorkspaceCoreMutationCommand command,
        Portfolio portfolio,
        IAtomicCommandContext attempt,
        DateTime now,
        CancellationToken ct)
    {
        var request = Read<UpdatePortfolioRequest>(command);
        var oldValues = JsonSerializer.Serialize(PortfolioResponse.FromEntity(portfolio));
        if (request.Name is not null) portfolio.Name = request.Name;
        if (request.Description is not null) portfolio.Description = request.Description;
        if (request.ManagementCompanyName is not null)
            portfolio.ManagementCompanyName = request.ManagementCompanyName;
        if (!string.IsNullOrWhiteSpace(request.TimeZone)) portfolio.TimeZone = request.TimeZone;
        if (request.Status.HasValue) portfolio.Status = request.Status.Value;
        if (!string.IsNullOrWhiteSpace(request.Currency)) portfolio.Currency = request.Currency;
        if (request.Settings is not null) portfolio.Settings = request.Settings;
        portfolio.UpdatedAt = now;

        await attempt.FlushBusinessAsync(ct);
        var response = PortfolioResponse.FromEntity(portfolio);
        attempt.StageSemanticEvent(new AtomicSemanticAudit(
            command.PortfolioId,
            nameof(Portfolio),
            portfolio.Id,
            AuditLogOperation.Updated,
            command.ActorUserId,
            OldValues: oldValues,
            NewValues: JsonSerializer.Serialize(response),
            ChangeReason: "Workspace settings updated"), now);
        StageDataUpdate(attempt, command, now, deleted: false);
        return new AtomicWorkspaceCoreMutationResult(
            true, true, ResponseJson: JsonSerializer.Serialize(response));
    }

    private async Task<AtomicWorkspaceCoreMutationResult> DeletePortfolioAsync(
        AtomicWorkspaceCoreMutationCommand command,
        Portfolio portfolio,
        IAtomicCommandContext attempt,
        DateTime now,
        CancellationToken ct)
    {
        portfolio.DeletedAt = now;
        portfolio.UpdatedAt = now;
        await attempt.FlushBusinessAsync(ct);
        attempt.StageSemanticEvent(new AtomicSemanticAudit(
            command.PortfolioId,
            nameof(Portfolio),
            portfolio.Id,
            AuditLogOperation.Deleted,
            command.ActorUserId,
            ChangeReason: "Workspace deleted"), now);
        StageDataUpdate(attempt, command, now, deleted: true);
        return new AtomicWorkspaceCoreMutationResult(true, true);
    }

    private async Task<AtomicWorkspaceCoreMutationResult> RotateApplicationLinkAsync(
        AtomicWorkspaceCoreMutationCommand command,
        Portfolio portfolio,
        IAtomicCommandContext attempt,
        DateTime now,
        CancellationToken ct)
    {
        var token = GenerateToken();
        portfolio.PublicApplicationToken = token;
        portfolio.UpdatedAt = now;
        await attempt.FlushBusinessAsync(ct);
        attempt.StageSemanticEvent(new AtomicSemanticAudit(
            command.PortfolioId,
            nameof(Portfolio),
            portfolio.Id,
            AuditLogOperation.Updated,
            command.ActorUserId,
            NewValues: JsonSerializer.Serialize(new { PublicApplicationLinkRotated = true }),
            ChangeReason: "Public application link rotated"), now);
        StageDataUpdate(attempt, command, now, deleted: false, suffix: "application-link");
        return new AtomicWorkspaceCoreMutationResult(
            true, true, PublicApplicationToken: token);
    }

    private Task<bool> IsAuthorizedAsync(
        AtomicWorkspaceCoreMutationCommand command,
        RentalCommandDbContext db,
        DateTime now,
        CancellationToken ct)
    {
        var capability = command.Operation == AtomicWorkspaceCoreMutationOperation.RotateApplicationLink
            ? CapabilityKeys.LeasingApplicationsManage
            : CapabilityKeys.AccountDestructiveActions;
        var targetKind = command.Operation == AtomicWorkspaceCoreMutationOperation.RotateApplicationLink
            ? CapabilityAuthorizationTargetKind.Property
            : CapabilityAuthorizationTargetKind.Workspace;

        return db.Set<MembershipRoleAssignment>().AsNoTracking().AnyAsync(assignment =>
            assignment.PortfolioId == command.PortfolioId
            && assignment.Status == MembershipRoleAssignmentStatus.Active
            && assignment.SuspendedAtUtc == null && assignment.RevokedAtUtc == null
            && assignment.EffectiveFromUtc <= now
            && (assignment.EffectiveToUtc == null || assignment.EffectiveToUtc > now)
            && assignment.ScopeKind == MembershipRoleAssignmentScopeKind.AllProperties
            && assignment.WorkspaceMembership!.AccessContextId == command.AccessContextId
            && assignment.WorkspaceMembership.PortfolioId == command.PortfolioId
            && assignment.WorkspaceMembership.Status == WorkspaceMembershipStatus.Active
            && assignment.WorkspaceMembership.SuspendedAtUtc == null
            && assignment.WorkspaceMembership.RevokedAtUtc == null
            && assignment.WorkspaceMembership.EffectiveFromUtc <= now
            && (assignment.WorkspaceMembership.EffectiveToUtc == null
                || assignment.WorkspaceMembership.EffectiveToUtc > now)
            && db.Set<WorkspaceAccessContext>().Any(context =>
                context.Id == command.AccessContextId && context.UserId == command.ActorUserId
                && context.PortfolioId == command.PortfolioId
                && context.AccessRevision == command.ExpectedAccessRevision
                && context.Status == WorkspaceAccessContextStatus.Active
                && context.SuspendedAtUtc == null && context.RevokedAtUtc == null)
            && db.Set<AuthSession>().Any(session =>
                session.Id == command.AuthSessionId && session.UserId == command.ActorUserId
                && session.ActiveAccessContextId == command.AccessContextId
                && session.Status == AuthSessionStatus.Active && session.RevokedAtUtc == null
                && session.ExpiresAtUtc > now)
            && assignment.RoleProfile!.Capabilities.Any(grant =>
                grant.CapabilityDefinition!.Key == capability
                && grant.CapabilityDefinition.AuthorizationTargetKind == targetKind), ct);
    }

    private void StageDataUpdate(
        IAtomicCommandContext attempt,
        AtomicWorkspaceCoreMutationCommand command,
        DateTime now,
        bool deleted,
        string suffix = "portfolio") =>
        attempt.StageOutbox(new OutboxMessage
        {
            PortfolioId = command.PortfolioId,
            MessageType = "data-update",
            Payload = JsonSerializer.Serialize(new
            {
                entityType = nameof(Portfolio),
                entityId = command.PortfolioId,
                operation = deleted ? "delete" : "update",
                data = new { },
            }),
            IdempotencyKey = $"{command.DeliveryIdempotencyKey}:{suffix}",
            CreatedAtUtc = now,
            NextAttemptAtUtc = now,
        });

    private T Read<T>(AtomicWorkspaceCoreMutationCommand command) where T : class =>
        JsonSerializer.Deserialize<T>(command.RequestJson)
        ?? throw new ArgumentException("The workspace update is invalid.");

    private string GenerateToken()
    {
        var bytes = RandomNumberGenerator.GetBytes(32);
        return Convert.ToBase64String(bytes)
            .Replace('+', '-')
            .Replace('/', '_')
            .TrimEnd('=');
    }

    private void Validate(AtomicWorkspaceCoreMutationCommand command)
    {
        if (command.PortfolioId <= 0 || command.ActorUserId <= 0
            || command.AuthSessionId == Guid.Empty || command.AccessContextId <= 0
            || command.ExpectedAccessRevision <= 0 || string.IsNullOrWhiteSpace(command.RequestJson)
            || string.IsNullOrWhiteSpace(command.DeliveryIdempotencyKey)
            || command.DeliveryIdempotencyKey.Length > 128)
        {
            throw new ArgumentException(
                "Portfolio, actor, access revision, request, and delivery identifiers are required.");
        }
    }
}

public static class AtomicWorkspaceCoreMutation
{
    public static readonly AtomicJsonResultCodec<AtomicWorkspaceCoreMutationResult> Codec =
        new("rental.workspace-core-mutation.v1");

    public static AtomicWorkspaceCoreMutationCommand Command<TRequest>(
        WorkspaceReadScope scope,
        AtomicWorkspaceCoreMutationOperation operation,
        string operationKey,
        TRequest request) => new(
            scope.PortfolioId,
            scope.UserId,
            scope.SessionId,
            scope.AccessContextId,
            scope.AccessRevision,
            operation,
            JsonSerializer.Serialize(request),
            operationKey);

    public static AtomicCommandIdentity Identity(AtomicWorkspaceCoreMutationCommand command) => new(
        $"rental.workspace.{command.Operation.ToString().ToLowerInvariant()}",
        $"{command.PortfolioId}:{command.AccessContextId}:{command.Operation}:" +
        command.DeliveryIdempotencyKey);
}
