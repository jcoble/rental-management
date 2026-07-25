using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using RentalCommand.Core.Atomic;
using RentalCommand.Core.Authorization;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;

namespace RentalCommand.Api.Services.Import;

public sealed record AtomicPaymentCsvImportCommand(
    int PortfolioId,
    int ActorUserId,
    Guid AuthSessionId,
    int AccessContextId,
    long ExpectedAccessRevision,
    string ImportOperationDigest,
    string RowsJson) : IAtomicCommandData;

public sealed record AtomicPaymentCsvImportResult(
    AtomicPaymentCsvImportRowResult[] Rows,
    int TotalRows,
    int ValidRows,
    int CreatedRows,
    int DuplicateRows) : IAtomicResultData;

public static class AtomicPaymentCsvImport
{
    public static readonly AtomicJsonResultCodec<AtomicPaymentCsvImportResult> Codec =
        new("payment.csv-import.v1");

    public static AtomicCommandIdentity Identity(AtomicPaymentCsvImportCommand command) => new(
        "payment.csv-import",
        $"{command.PortfolioId}:{command.AccessContextId}:{command.ImportOperationDigest}");
}

/// <summary>One receipt-backed payment CSV command; PostgreSQL owns the entire row set.</summary>
public sealed class AtomicPaymentCsvImportHandler
    : IAtomicCommandHandler<AtomicPaymentCsvImportCommand, AtomicPaymentCsvImportResult>,
      IAtomicReplayAuthorizer<AtomicPaymentCsvImportCommand>
{
    public async Task<AtomicPaymentCsvImportResult> HandleAsync(
        AtomicPaymentCsvImportCommand command,
        IAtomicWriteAttempt attempt,
        CancellationToken ct)
    {
        Validate(command);
        await attempt.Locking.AcquireAsync(AtomicLockResource.AuthSession, command.AuthSessionId, ct);
        await attempt.Locking.AcquireAsync(
            AtomicLockResource.WorkspaceAccessContext, command.AccessContextId, ct);
        await attempt.Locking.AcquireAsync(AtomicLockResource.Portfolio, command.PortfolioId, ct);

        var now = await attempt.Persistence.ReadDatabaseClockUtcAsync(ct);
        var batch = await attempt.PaymentCsvImports.ImportAsync(Scope(command), command.RowsJson, now, ct);
        if (!batch.Authorized)
            throw new UnauthorizedAccessException("Workspace access changed. Refresh and try again.");

        var createdEntryIds = Array.ConvertAll(
            batch.CreatedRows.ToArray(), row => row.CreatedId!.Value);
        await attempt.TenantMoney.AllocateImportedReceiptsAsync(createdEntryIds, now, ct);

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

        attempt.StageOutbox(new OutboxMessage
        {
            PortfolioId = command.PortfolioId,
            MessageType = "data-update",
            Payload = JsonSerializer.Serialize(new
            {
                entityType = "Payment",
                entityId = 0,
                operation = "update",
                data = new { import = true },
            }),
            IdempotencyKey = $"payment-import:{command.ImportOperationDigest}",
            CreatedAtUtc = now,
            NextAttemptAtUtc = now,
        });

        return new AtomicPaymentCsvImportResult(
            batch.Rows.ToArray(), batch.TotalRows, batch.ValidRows,
            batch.CreatedCount, batch.DuplicateRows);
    }

    public async Task AuthorizeReplayAsync(
        AtomicPaymentCsvImportCommand command,
        IAtomicPersistenceSession persistence,
        CancellationToken ct)
    {
        Validate(command);
        var now = await persistence.ReadDatabaseClockUtcAsync(ct);
        if (!await IsAuthorizedAsync(command, persistence, now, ct))
            throw new UnauthorizedAccessException("Workspace access changed. Refresh and try again.");
    }

    private static Task<bool> IsAuthorizedAsync(
        AtomicPaymentCsvImportCommand command,
        IAtomicPersistenceSession persistence,
        DateTime now,
        CancellationToken ct) =>
        persistence.Query<AuthSession>().AsNoTracking().AnyAsync(session =>
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
                    && assignment.RoleProfile!.Capabilities.Any(grant =>
                        grant.CapabilityDefinition!.Key == CapabilityKeys.MoneyPaymentsManage
                        && grant.CapabilityDefinition.AuthorizationTargetKind ==
                           CapabilityAuthorizationTargetKind.Property))), ct);

    private static WorkspaceReadScope Scope(AtomicPaymentCsvImportCommand command) => new(
        command.PortfolioId, command.ActorUserId, command.AuthSessionId,
        command.AccessContextId, command.ExpectedAccessRevision);

    private static void Validate(AtomicPaymentCsvImportCommand command)
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
