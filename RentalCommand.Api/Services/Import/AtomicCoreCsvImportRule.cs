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

public sealed record AtomicCoreCsvImportCommand(
    int PortfolioId,
    int ActorUserId,
    [property: AtomicFingerprintIgnore]
    Guid AuthSessionId,
    [property: AtomicFingerprintIgnore]
    int AccessContextId,
    [property: AtomicFingerprintIgnore]
    long ExpectedAccessRevision,
    AtomicCoreCsvImportDomain Domain,
    string ImportOperationDigest,
    string RowsJson) : IAtomicCommandData;

public sealed record AtomicCoreCsvImportResult(
    AtomicCoreCsvImportRowResult[] Rows,
    int TotalRows,
    int ValidRows,
    int CreatedRows,
    int DuplicateRows);

public static class AtomicCoreCsvImport
{
    public static readonly AtomicJsonResultCodec<AtomicCoreCsvImportResult> Codec =
        new("core.csv-import.v1");

    public static AtomicCommandIdentity Identity(AtomicCoreCsvImportCommand command) => new(
        $"{command.Domain.ToString().ToLowerInvariant()}.csv-import",
        $"{command.PortfolioId}:{command.AccessContextId}:{command.ImportOperationDigest}");

    public static TransactionalWrite<AtomicCoreCsvImportCommand, AtomicCoreCsvImportResult> Write(
        AtomicCoreCsvImportCommand command,
        Func<AtomicCoreCsvImportCommand, IAtomicCommandContext, CancellationToken,
            Task<AtomicCoreCsvImportResult>> executeAsync,
        Func<AtomicCoreCsvImportCommand, IAtomicCommandContext, CancellationToken, Task>
            authorizeReplayAsync)
    {
        var identity = Identity(command);
        return new(identity.CommandType, WriteIdempotencyPolicy.Required, command,
            Codec.ContractName,
            new WriteLockPlan(WriteLockProtocol.AuthorizationScope,
                command.AuthSessionId,
                command.AccessContextId,
                command.PortfolioId),
            executeAsync, authorizeReplayAsync);
    }

}

/// <summary>One receipt-backed Property or Tenant CSV command; PostgreSQL owns the entire row set.</summary>
public sealed class AtomicCoreCsvImportRule
{
    private readonly RentalCommandDbContext _db;

    public AtomicCoreCsvImportRule(RentalCommandDbContext db) => _db = db;

    internal async Task<AtomicCoreCsvImportResult> ExecuteAsync(
        AtomicCoreCsvImportCommand command,
        IAtomicCommandContext attempt,
        CancellationToken ct)
    {
        Validate(command);
        var times = await AtomicCommandDbClock.ReadCommandTimesAsync(_db, command.PortfolioId, ct);
        var now = times.WallClockUtc;
        var loanAutomationStartDateUtc = times.BusinessDate.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
        var scope = Scope(command);
        var batch = await AtomicCoreCsvImportPersistence.ImportAsync(_db,
            attempt, scope, command.Domain, command.RowsJson, now, loanAutomationStartDateUtc, ct);
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
                    ChangeReason: $"Unit created from Property CSV row {row.RowNumber}"), now);
            }
        }

        StageCreatedRowUpdates(
            attempt, command.PortfolioId, command.Domain,
            command.ImportOperationDigest, batch.CreatedRows, now);
        return new AtomicCoreCsvImportResult(
            batch.Rows.ToArray(), batch.TotalRows, batch.ValidRows,
            batch.CreatedCount, batch.DuplicateRows);
    }

    internal static void StageCreatedRowUpdates(
        IAtomicCommandContext attempt,
        int portfolioId,
        AtomicCoreCsvImportDomain domain,
        string importOperationDigest,
        IReadOnlyList<AtomicCoreCsvImportRowResult> createdRows,
        DateTime now)
    {
        var entityType = domain.ToString();
        foreach (var row in createdRows)
        {
            var entityId = row.CreatedId is > 0
                ? row.CreatedId.Value
                : throw new InvalidOperationException(
                    $"Created {entityType} CSV row {row.RowNumber} has no persisted entity id.");
            attempt.StageOutbox(new OutboxMessage
            {
                PortfolioId = portfolioId,
                MessageType = "data-update",
                Payload = JsonSerializer.Serialize(new
                {
                    entityType,
                    entityId,
                    operation = "update",
                    data = new { import = true, rowNumber = row.RowNumber, row.RelatedId },
                }),
                IdempotencyKey =
                    $"{entityType.ToLowerInvariant()}-import:{importOperationDigest}:{entityType.ToLowerInvariant()}:{entityId}",
                CreatedAtUtc = now,
                NextAttemptAtUtc = now,
            });
        }
    }

    internal async Task AuthorizeAsync(
        AtomicCoreCsvImportCommand command,
        IAtomicCommandContext context,
        CancellationToken ct)
    {
        Validate(command);
        var now = await context.ReadDatabaseClockUtcAsync(ct);
        if (!await IsAuthorizedAsync(command, _db, now, ct))
            throw new UnauthorizedAccessException("Workspace access changed. Refresh and try again.");
    }

    private Task<bool> IsAuthorizedAsync(
        AtomicCoreCsvImportCommand command,
        RentalCommandDbContext db,
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
                && db.Set<MembershipRoleAssignment>().Any(assignment =>
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

    private WorkspaceReadScope Scope(AtomicCoreCsvImportCommand command) => new(
        command.PortfolioId, command.ActorUserId, command.AuthSessionId,
        command.AccessContextId, command.ExpectedAccessRevision);

    private void Validate(AtomicCoreCsvImportCommand command)
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
