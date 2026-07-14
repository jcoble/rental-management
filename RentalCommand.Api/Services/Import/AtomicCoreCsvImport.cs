using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using RentalCommand.Core.Atomic;
using RentalCommand.Core.Authorization;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;

namespace RentalCommand.Api.Services.Import;

public sealed record AtomicCoreCsvImportCommand(
    int PortfolioId,
    int ActorUserId,
    Guid AuthSessionId,
    int AccessContextId,
    long ExpectedAccessRevision,
    AtomicCoreCsvImportDomain Domain,
    string ImportOperationDigest,
    string RowsJson) : IAtomicCommandData;

public sealed record AtomicCoreCsvImportResult(
    AtomicCoreCsvImportRowResult[] Rows,
    int TotalRows,
    int ValidRows,
    int CreatedRows,
    int DuplicateRows) : IAtomicResultData;

public static class AtomicCoreCsvImport
{
    public static readonly AtomicJsonResultCodec<AtomicCoreCsvImportResult> Codec =
        new("core.csv-import.v1");

    public static AtomicCommandIdentity Identity(AtomicCoreCsvImportCommand command) => new(
        $"{command.Domain.ToString().ToLowerInvariant()}.csv-import",
        $"{command.PortfolioId}:{command.AccessContextId}:{command.ImportOperationDigest}");
}

/// <summary>One receipt-backed Property or Tenant CSV command; PostgreSQL owns the entire row set.</summary>
public sealed class AtomicCoreCsvImportHandler
    : IAtomicCommandHandler<AtomicCoreCsvImportCommand, AtomicCoreCsvImportResult>,
      IAtomicReplayAuthorizer<AtomicCoreCsvImportCommand>
{
    public async Task<AtomicCoreCsvImportResult> HandleAsync(
        AtomicCoreCsvImportCommand command,
        IAtomicWriteAttempt attempt,
        CancellationToken ct)
    {
        Validate(command);
        await attempt.Locking.AcquireAsync(AtomicLockResource.AuthSession, command.AuthSessionId, ct);
        await attempt.Locking.AcquireAsync(
            AtomicLockResource.WorkspaceAccessContext, command.AccessContextId, ct);
        await attempt.Locking.AcquireAsync(AtomicLockResource.Portfolio, command.PortfolioId, ct);
        var now = await attempt.Persistence.ReadDatabaseClockUtcAsync(ct);
        var scope = Scope(command);
        var batch = await attempt.CoreCsvImports.ImportAsync(
            scope, command.Domain, command.RowsJson, now, ct);
        if (!batch.Authorized)
            throw new UnauthorizedAccessException("Workspace access changed. Refresh and try again.");

        var entityType = command.Domain.ToString();
        foreach (var row in batch.CreatedRows)
        {
            attempt.StageSemanticEvent(new AtomicSemanticAudit(
                command.PortfolioId,
                entityType,
                row.CreatedId!.Value,
                AuditLogOperation.Created,
                UserId: command.ActorUserId,
                ChangeReason: $"{entityType} created from CSV row {row.RowNumber}"), now);
            if (command.Domain == AtomicCoreCsvImportDomain.Property && row.RelatedId.HasValue)
            {
                attempt.StageSemanticEvent(new AtomicSemanticAudit(
                    command.PortfolioId,
                    nameof(Unit),
                    row.RelatedId.Value,
                    AuditLogOperation.Created,
                    UserId: command.ActorUserId,
                    ChangeReason: $"Canonical unit created from Property CSV row {row.RowNumber}"), now);
            }
        }

        attempt.StageOutbox(new OutboxMessage
        {
            PortfolioId = command.PortfolioId,
            MessageType = "data-update",
            Payload = JsonSerializer.Serialize(new
            {
                entityType,
                entityId = 0,
                operation = "update",
                data = new { import = true },
            }),
            IdempotencyKey = $"{entityType.ToLowerInvariant()}-import:{command.ImportOperationDigest}",
            CreatedAtUtc = now,
            NextAttemptAtUtc = now,
        });
        return new AtomicCoreCsvImportResult(
            batch.Rows.ToArray(), batch.TotalRows, batch.ValidRows,
            batch.CreatedCount, batch.DuplicateRows);
    }

    public async Task AuthorizeReplayAsync(
        AtomicCoreCsvImportCommand command,
        IAtomicPersistenceSession persistence,
        CancellationToken ct)
    {
        Validate(command);
        var now = await persistence.ReadDatabaseClockUtcAsync(ct);
        if (!await IsAuthorizedAsync(command, persistence, now, ct))
            throw new UnauthorizedAccessException("Workspace access changed. Refresh and try again.");
    }

    private static Task<bool> IsAuthorizedAsync(
        AtomicCoreCsvImportCommand command,
        IAtomicPersistenceSession persistence,
        DateTime now,
        CancellationToken ct)
    {
        string[] allowedCapabilities = command.Domain switch
        {
            AtomicCoreCsvImportDomain.Property => [CapabilityKeys.RentalsManage],
            AtomicCoreCsvImportDomain.Tenant =>
                [CapabilityKeys.RentalsManage, CapabilityKeys.LeasingOnboardingManage],
            AtomicCoreCsvImportDomain.Expense or AtomicCoreCsvImportDomain.Loan =>
                [CapabilityKeys.MoneyExpensesManage],
            _ => throw new ArgumentOutOfRangeException(nameof(command.Domain)),
        };
        return persistence.Query<AuthSession>().AsNoTracking().AnyAsync(session =>
            session.Id == command.AuthSessionId
            && session.UserId == command.ActorUserId
            && session.ActiveAccessContextId == command.AccessContextId
            && session.Status == AuthSessionStatus.Active
            && session.RevokedAtUtc == null
            && session.ExpiresAtUtc > now
            && persistence.Query<WorkspaceAccessContext>().Any(context =>
                context.Id == command.AccessContextId
                && context.UserId == command.ActorUserId
                && context.PortfolioId == command.PortfolioId
                && context.AccessRevision == command.ExpectedAccessRevision
                && context.Status == WorkspaceAccessContextStatus.Active
                && context.SuspendedAtUtc == null
                && context.RevokedAtUtc == null)
            && persistence.Query<WorkspaceMembership>().Any(membership =>
                membership.AccessContextId == command.AccessContextId
                && membership.PortfolioId == command.PortfolioId
                && membership.Status == WorkspaceMembershipStatus.Active
                && membership.SuspendedAtUtc == null
                && membership.RevokedAtUtc == null
                && membership.EffectiveFromUtc <= now
                && (membership.EffectiveToUtc == null || membership.EffectiveToUtc > now)
                && persistence.Query<MembershipRoleAssignment>().Any(assignment =>
                    assignment.WorkspaceMembershipId == membership.Id
                    && assignment.PortfolioId == command.PortfolioId
                    && assignment.Status == MembershipRoleAssignmentStatus.Active
                    && assignment.SuspendedAtUtc == null
                    && assignment.RevokedAtUtc == null
                    && assignment.EffectiveFromUtc <= now
                    && (assignment.EffectiveToUtc == null || assignment.EffectiveToUtc > now)
                    && (command.Domain == AtomicCoreCsvImportDomain.Expense
                        || command.Domain == AtomicCoreCsvImportDomain.Loan
                        || assignment.ScopeKind == MembershipRoleAssignmentScopeKind.AllProperties)
                    && assignment.RoleProfile!.Capabilities.Any(grant =>
                        allowedCapabilities.Contains(grant.CapabilityDefinition!.Key)
                        && grant.CapabilityDefinition.AuthorizationTargetKind ==
                           CapabilityAuthorizationTargetKind.Property))), ct);
    }

    private static WorkspaceReadScope Scope(AtomicCoreCsvImportCommand command) => new(
        command.PortfolioId, command.ActorUserId, command.AuthSessionId,
        command.AccessContextId, command.ExpectedAccessRevision);

    private static void Validate(AtomicCoreCsvImportCommand command)
    {
        if (command.PortfolioId <= 0 || command.ActorUserId <= 0
            || command.AuthSessionId == Guid.Empty || command.AccessContextId <= 0
            || command.ExpectedAccessRevision <= 0
            || string.IsNullOrWhiteSpace(command.ImportOperationDigest)
            || command.ImportOperationDigest.Length > 128
            || string.IsNullOrWhiteSpace(command.RowsJson))
            throw new ArgumentException("CSV import authority, operation digest, and rows are required.");
    }
}
