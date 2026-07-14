using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using RentalCommand.Core.Atomic;
using RentalCommand.Core.Authorization;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;

namespace RentalCommand.Api.Services.Import;

public sealed record AtomicUnitCsvImportCommand(
    int PortfolioId,
    int ActorUserId,
    Guid AuthSessionId,
    int AccessContextId,
    long ExpectedAccessRevision,
    string ImportOperationDigest,
    AtomicUnitImportRow[] Rows) : IAtomicCommandData;

public sealed record AtomicUnitCsvImportResult(
    AtomicUnitImportRowResult[] Rows,
    int TotalRows,
    int ValidRows,
    int CreatedRows,
    int DuplicateRows) : IAtomicResultData;

public static class AtomicUnitCsvImport
{
    public static readonly AtomicJsonResultCodec<AtomicUnitCsvImportResult> Codec =
        new("unit.csv-import.v1");

    public static AtomicCommandIdentity Identity(AtomicUnitCsvImportCommand command) => new(
        "unit.csv-import",
        $"{command.PortfolioId}:{command.AccessContextId}:{command.ImportOperationDigest}");
}

/// <summary>One receipt-backed Unit CSV command; PostgreSQL owns the whole row set.</summary>
public sealed class AtomicUnitCsvImportHandler
    : IAtomicCommandHandler<AtomicUnitCsvImportCommand, AtomicUnitCsvImportResult>,
      IAtomicReplayAuthorizer<AtomicUnitCsvImportCommand>
{
    public async Task<AtomicUnitCsvImportResult> HandleAsync(
        AtomicUnitCsvImportCommand command,
        IAtomicWriteAttempt attempt,
        CancellationToken ct)
    {
        Validate(command);
        await attempt.Locking.AcquireAsync(AtomicLockResource.AuthSession, command.AuthSessionId, ct);
        await attempt.Locking.AcquireAsync(
            AtomicLockResource.WorkspaceAccessContext, command.AccessContextId, ct);
        await attempt.Locking.AcquireAsync(AtomicLockResource.Portfolio, command.PortfolioId, ct);
        var now = await attempt.Persistence.ReadDatabaseClockUtcAsync(ct);
        var scope = new WorkspaceReadScope(
            command.PortfolioId, command.ActorUserId, command.AuthSessionId,
            command.AccessContextId, command.ExpectedAccessRevision);
        var batch = await attempt.UnitImports.ImportAsync(
            scope, command.Rows, now, ct);
        if (!batch.Authorized)
            throw new UnauthorizedAccessException(
                "At least one Unit row is outside your assigned property scope.");
        foreach (var row in batch.CreatedRows)
        {
            attempt.StageSemanticEvent(new AtomicSemanticAudit(
                command.PortfolioId,
                "Unit",
                row.CreatedId!.Value,
                AuditLogOperation.Created,
                UserId: command.ActorUserId,
                NewValues: JsonSerializer.Serialize(new
                {
                    row.PropertyId,
                    row.UnitNumber,
                    row.Bedrooms,
                    row.Bathrooms,
                    row.MarketRent,
                }),
                ChangeReason: $"Unit created from CSV row {row.RowNumber}"), now);
        }

        attempt.StageOutbox(new OutboxMessage
        {
            PortfolioId = command.PortfolioId,
            MessageType = "data-update",
            Payload = JsonSerializer.Serialize(new
            {
                entityType = "Unit",
                entityId = 0,
                operation = "update",
                data = new { import = true },
            }),
            IdempotencyKey = $"unit-import:{command.ImportOperationDigest}",
            CreatedAtUtc = now,
            NextAttemptAtUtc = now,
        });
        return new AtomicUnitCsvImportResult(
            batch.Rows.ToArray(), batch.TotalRows, batch.ValidRows,
            batch.CreatedCount, batch.DuplicateRows);
    }

    public async Task AuthorizeReplayAsync(
        AtomicUnitCsvImportCommand command,
        IAtomicPersistenceSession persistence,
        CancellationToken ct)
    {
        Validate(command);
        var now = await persistence.ReadDatabaseClockUtcAsync(ct);
        if (!await IsAuthorizedAsync(command, persistence, now, ct))
            throw new UnauthorizedAccessException("Workspace access changed. Refresh and try again.");
    }

    private static Task<bool> IsAuthorizedAsync(
        AtomicUnitCsvImportCommand command,
        IAtomicPersistenceSession persistence,
        DateTime now,
        CancellationToken ct)
    {
        var assignments = persistence.Query<MembershipRoleAssignment>().AsNoTracking();
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
                && assignments.Any(assignment =>
                    assignment.WorkspaceMembershipId == membership.Id
                    && assignment.PortfolioId == command.PortfolioId
                    && assignment.Status == MembershipRoleAssignmentStatus.Active
                    && assignment.SuspendedAtUtc == null
                    && assignment.RevokedAtUtc == null
                    && assignment.EffectiveFromUtc <= now
                    && (assignment.EffectiveToUtc == null || assignment.EffectiveToUtc > now)
                    && assignment.RoleProfile!.Capabilities.Any(grant =>
                        grant.CapabilityDefinition!.Key == CapabilityKeys.RentalsManage
                        && grant.CapabilityDefinition.AuthorizationTargetKind ==
                           CapabilityAuthorizationTargetKind.Property))), ct);
    }

    private static void Validate(AtomicUnitCsvImportCommand command)
    {
        if (command.PortfolioId <= 0 || command.ActorUserId <= 0
            || command.AuthSessionId == Guid.Empty || command.AccessContextId <= 0
            || command.ExpectedAccessRevision <= 0
            || string.IsNullOrWhiteSpace(command.ImportOperationDigest)
            || command.ImportOperationDigest.Length > 200
            || command.Rows.Length == 0)
            throw new ArgumentException("Unit import authority, operation digest, and rows are required.");
    }
}
