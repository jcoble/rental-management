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

public sealed record AtomicPaymentCsvImportCommand(
    int PortfolioId,
    int ActorUserId,
    [property: AtomicFingerprintIgnore]
    Guid AuthSessionId,
    [property: AtomicFingerprintIgnore]
    int AccessContextId,
    [property: AtomicFingerprintIgnore]
    long ExpectedAccessRevision,
    string ImportOperationDigest,
    string RowsJson) : IAtomicCommandData;

public sealed record AtomicPaymentCsvImportResult(
    AtomicPaymentCsvImportRowResult[] Rows,
    int TotalRows,
    int ValidRows,
    int CreatedRows,
    int DuplicateRows);

public static class AtomicPaymentCsvImport
{
    public static readonly AtomicJsonResultCodec<AtomicPaymentCsvImportResult> Codec =
        new("payment.csv-import.v1");

    public static AtomicCommandIdentity Identity(AtomicPaymentCsvImportCommand command) => new(
        "payment.csv-import",
        $"{command.PortfolioId}:{command.AccessContextId}:{command.ImportOperationDigest}");

    public static TransactionalWrite<AtomicPaymentCsvImportCommand, AtomicPaymentCsvImportResult> Write(
        AtomicPaymentCsvImportCommand command,
        Func<AtomicPaymentCsvImportCommand, IAtomicCommandContext, CancellationToken,
            Task<AtomicPaymentCsvImportResult>> executeAsync,
        Func<AtomicPaymentCsvImportCommand, IAtomicCommandContext, CancellationToken, Task>
            authorizeReplayAsync)
    {
        var identity = Identity(command);
        return new(identity.CommandType,  command,
            Codec.ContractName,
            new WriteLockPlan(WriteLockProtocol.AuthorizationScope,
                command.AuthSessionId,
                command.AccessContextId,
                command.PortfolioId),
            executeAsync, authorizeReplayAsync);
    }

}

/// <summary>One receipt-backed payment CSV command; PostgreSQL owns the entire row set.</summary>
public sealed class AtomicPaymentCsvImportRule
{
    private readonly RentalCommandDbContext _db;

    public AtomicPaymentCsvImportRule(RentalCommandDbContext db) => _db = db;

    internal async Task<AtomicPaymentCsvImportResult> ExecuteAsync(
        AtomicPaymentCsvImportCommand command,
        IAtomicCommandContext attempt,
        CancellationToken ct)
    {
        Validate(command);
        var now = await AtomicCommandDbClock.ReadDatabaseClockUtcAsync(_db, ct);
        var batch = await AtomicPaymentCsvImportPersistence.ImportAsync(
            _db, attempt, Scope(command), command.RowsJson, now, ct);
        if (!batch.Authorized)
            throw new UnauthorizedAccessException("Workspace access changed. Refresh and try again.");

        foreach (var row in batch.CreatedRows)
        {
            attempt.StageSemanticEvent(new AtomicSemanticAudit(
                command.PortfolioId,
                nameof(TenantAccount),
                row.TenantAccountId!.Value,
                AuditLogOperation.Updated,
                UserId: command.ActorUserId,
                NewValues: JsonSerializer.Serialize(new { ledgerEntryId = row.CreatedId }),
                ChangeReason: $"Payment receipt imported from CSV row {row.RowNumber}"), now);
        }

        StageCreatedRowUpdates(
            attempt, command.PortfolioId, command.ImportOperationDigest, batch.CreatedRows, now);

        return new AtomicPaymentCsvImportResult(
            batch.Rows.ToArray(), batch.TotalRows, batch.ValidRows,
            batch.CreatedCount, batch.DuplicateRows);
    }

    internal static void StageCreatedRowUpdates(
        IAtomicCommandContext attempt,
        int portfolioId,
        string importOperationDigest,
        IReadOnlyList<AtomicPaymentCsvImportRowResult> createdRows,
        DateTime now)
    {
        foreach (var row in createdRows)
        {
            var entityId = row.TenantAccountId is > 0
                ? row.TenantAccountId.Value
                : throw new InvalidOperationException(
                    $"Created Payment CSV row {row.RowNumber} has no persisted tenant account id.");
            attempt.StageOutbox(new OutboxMessage
            {
                PortfolioId = portfolioId,
                MessageType = "data-update",
                Payload = JsonSerializer.Serialize(new
                {
                    entityType = nameof(TenantAccount),
                    entityId,
                    operation = "update",
                    data = new { import = true, rowNumber = row.RowNumber, ledgerEntryId = row.CreatedId },
                }),
                IdempotencyKey = $"payment-import:{importOperationDigest}:tenant-account:{entityId}:row:{row.RowNumber}",
                CreatedAtUtc = now,
                NextAttemptAtUtc = now,
            });
        }
    }

    internal async Task AuthorizeAsync(
        AtomicPaymentCsvImportCommand command,
        IAtomicCommandContext context,
        CancellationToken ct)
    {
        Validate(command);
        var now = await context.ReadDatabaseClockUtcAsync(ct);
        if (!await IsAuthorizedAsync(command, _db, now, ct))
            throw new UnauthorizedAccessException("Workspace access changed. Refresh and try again.");
    }

    private Task<bool> IsAuthorizedAsync(
        AtomicPaymentCsvImportCommand command,
        RentalCommandDbContext db,
        DateTime now,
        CancellationToken ct) =>
        db.Set<AuthSession>().AsNoTracking().AnyAsync(session =>
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
                    && assignment.RoleProfile!.Capabilities.Any(grant =>
                        grant.CapabilityDefinition!.Key == CapabilityKeys.MoneyPaymentsManage
                        && grant.CapabilityDefinition.AuthorizationTargetKind ==
                           CapabilityAuthorizationTargetKind.Property))), ct);

    private WorkspaceReadScope Scope(AtomicPaymentCsvImportCommand command) => new(
        command.PortfolioId, command.ActorUserId, command.AuthSessionId,
        command.AccessContextId, command.ExpectedAccessRevision);

    private void Validate(AtomicPaymentCsvImportCommand command)
    {
        if (command.PortfolioId <= 0 || command.ActorUserId <= 0
            || command.AuthSessionId == Guid.Empty || command.AccessContextId <= 0
            || command.ExpectedAccessRevision <= 0
            || string.IsNullOrWhiteSpace(command.ImportOperationDigest)
            || command.ImportOperationDigest.Length > 128
            || string.IsNullOrWhiteSpace(command.RowsJson))
            throw new ArgumentException("CSV import authority, operation digest, and rows are required.");

        using var rows = JsonDocument.Parse(command.RowsJson);
        if (rows.RootElement.ValueKind != JsonValueKind.Array
            || rows.RootElement.GetArrayLength() is < 1 or > 256)
            throw new ArgumentException("Payment CSV imports require between 1 and 256 rows.");
    }
}
