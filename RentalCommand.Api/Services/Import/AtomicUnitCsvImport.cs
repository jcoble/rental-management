using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using RentalCommand.Core.Atomic;
using RentalCommand.Core.Import;
using RentalCommand.Data.Import;
using RentalCommand.Core.Authorization;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Api.Services;
using RentalCommand.Data;

namespace RentalCommand.Api.Services.Import;

public sealed record AtomicUnitCsvImportCommand(
    int PortfolioId,
    int ActorUserId,
    [property: AtomicFingerprintIgnore]
    Guid AuthSessionId,
    [property: AtomicFingerprintIgnore]
    int AccessContextId,
    [property: AtomicFingerprintIgnore]
    long ExpectedAccessRevision,
    string ImportOperationDigest,
    AtomicUnitImportRow[] Rows) : IAtomicCommandData;

public sealed record AtomicUnitCsvImportResult(
    AtomicUnitImportRowResult[] Rows,
    int TotalRows,
    int ValidRows,
    int CreatedRows,
    int DuplicateRows);

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
    : IAtomicCommandHandler<AtomicUnitCsvImportCommand, AtomicUnitCsvImportResult>
{
    private readonly RentalCommandDbContext _db;

    public AtomicUnitCsvImportHandler(RentalCommandDbContext db) => _db = db;

    public async Task<AtomicUnitCsvImportResult> HandleAsync(
        AtomicUnitCsvImportCommand command,
        IAtomicCommandContext attempt,
        CancellationToken ct)
    {
        Validate(command);
        await attempt.AcquireLockAsync("AuthSession", command.AuthSessionId, ct);
        await attempt.AcquireLockAsync(
            "WorkspaceAccessContext", command.AccessContextId, ct);
        await attempt.AcquireLockAsync("Portfolio", command.PortfolioId, ct);
        var now = await AtomicCommandDbClock.ReadDatabaseClockUtcAsync(_db, ct);
        var scope = new WorkspaceReadScope(
            command.PortfolioId, command.ActorUserId, command.AuthSessionId,
            command.AccessContextId, command.ExpectedAccessRevision);
        var batch = await AtomicUnitImportPersistence.ImportAsync(_db,
            attempt, scope, command.Rows, now, ct);
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

        StageCreatedRowUpdates(
            attempt, command.PortfolioId, command.ImportOperationDigest, batch.CreatedRows, now);
        return new AtomicUnitCsvImportResult(
            batch.Rows.ToArray(), batch.TotalRows, batch.ValidRows,
            batch.CreatedCount, batch.DuplicateRows);
    }

    internal static void StageCreatedRowUpdates(
        IAtomicCommandContext attempt,
        int portfolioId,
        string importOperationDigest,
        IReadOnlyList<AtomicUnitImportRowResult> createdRows,
        DateTime now)
    {
        foreach (var row in createdRows)
        {
            var entityId = row.CreatedId is > 0
                ? row.CreatedId.Value
                : throw new InvalidOperationException(
                    $"Created Unit CSV row {row.RowNumber} has no persisted entity id.");
            attempt.StageOutbox(new OutboxMessage
            {
                PortfolioId = portfolioId,
                MessageType = "data-update",
                Payload = JsonSerializer.Serialize(new
                {
                    entityType = nameof(Unit),
                    entityId,
                    operation = "update",
                    data = new { import = true, rowNumber = row.RowNumber, row.PropertyId },
                }),
                IdempotencyKey = $"unit-import:{importOperationDigest}:unit:{entityId}",
                CreatedAtUtc = now,
                NextAttemptAtUtc = now,
            });
        }
    }

    public async Task AuthorizeReplayAsync(
        AtomicUnitCsvImportCommand command,
        IAtomicCommandContext context,
        CancellationToken ct)
    {
        Validate(command);
        var now = await context.ReadDatabaseClockUtcAsync(ct);
        if (!await IsAuthorizedAsync(command, _db, now, ct))
            throw new UnauthorizedAccessException("Workspace access changed. Refresh and try again.");
    }

    private Task<bool> IsAuthorizedAsync(
        AtomicUnitCsvImportCommand command,
        RentalCommandDbContext db,
        DateTime now,
        CancellationToken ct)
    {
        var assignments = db.Set<MembershipRoleAssignment>().AsNoTracking();
        return db.Set<AuthSession>().AsNoTracking().AnyAsync(session =>
            session.Id == command.AuthSessionId
            && session.UserId == command.ActorUserId
            && session.ActiveAccessContextId == command.AccessContextId
            && session.Status == AuthSessionStatus.Active
            && session.RevokedAtUtc == null
            && session.ExpiresAtUtc > now
            && db.Set<WorkspaceAccessContext>().Any(context =>
                context.Id == command.AccessContextId
                && context.UserId == command.ActorUserId
                && context.PortfolioId == command.PortfolioId
                && context.AccessRevision == command.ExpectedAccessRevision
                && context.Status == WorkspaceAccessContextStatus.Active
                && context.SuspendedAtUtc == null
                && context.RevokedAtUtc == null)
            && db.Set<WorkspaceMembership>().Any(membership =>
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

    private void Validate(AtomicUnitCsvImportCommand command)
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
