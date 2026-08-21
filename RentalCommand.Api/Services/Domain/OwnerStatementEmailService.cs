using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using RentalCommand.Core.Atomic;
using RentalCommand.Core.Authorization;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Time;
using RentalCommand.Data;
using RentalCommand.Data.Authorization;
using RentalCommand.Api.Services;
using RentalCommand.Api.Writes;

namespace RentalCommand.Api.Services.Domain;

/// <inheritdoc cref="IOwnerStatementEmailService"/>
public class OwnerStatementEmailService : IOwnerStatementEmailService
{
    private readonly RentalCommandDbContext _db;
    private readonly IOwnerStatementService _statements;
    private readonly ILogger<OwnerStatementEmailService> _logger;
    private readonly TimeProvider _timeProvider;
    private readonly IRequestWriteExecutor _writes;

    public OwnerStatementEmailService(
        RentalCommandDbContext db,
        IOwnerStatementService statements,
        ILogger<OwnerStatementEmailService> logger,
        TimeProvider timeProvider,
        IRequestWriteExecutor writes)
    {
        _db = db;
        _statements = statements;
        _logger = logger;
        _timeProvider = timeProvider;
        _writes = writes;
    }

    /// <inheritdoc/>
    public async Task<StatementEmailResult> SendOwnerStatementAsync(
        WorkspaceReadScope scope,
        int ownerId,
        int year,
        string idempotencyKey,
        CancellationToken ct = default)
    {
        var authorizedProperties = _db.Properties
            .AsNoTracking()
            .WhereAuthorized(
                _db,
                scope,
                CapabilityKeys.MoneyOwnerReportsRead,
                _timeProvider.UtcNow());
        var owner = await LoadOwnerAsync(scope.PortfolioId, ownerId, authorizedProperties, ct);
        if (owner is null)
            return new StatementEmailResult(false, "Owner not found in portfolio");
        var ownerEmail = owner.Email;
        if (string.IsNullOrWhiteSpace(ownerEmail))
            return new StatementEmailResult(false, "Owner has no email address");

        var report = await _statements.GetForOwnerAsync(scope, ownerId, year, ct);
        if (report is null)
            return new StatementEmailResult(false, "No statement data for that year");

        var command = new QueueOwnerStatementEmailCommand(
            scope.PortfolioId,
            scope.UserId,
            scope.SessionId,
            scope.AccessContextId,
            scope.AccessRevision,
            owner.Id,
            year,
            ownerEmail,
            $"Your {year} owner statement",
            RenderStatementText(report),
            idempotencyKey);
        var outcome = await _writes.ExecuteAsync(
            QueueOwnerStatementEmail.Identity(command).IdempotencyKey,
            QueueOwnerStatementEmail.Write(_db, command), ct);
        if (outcome.Value.Queued)
        {
            _logger.LogInformation(
                "Enqueued owner statement email for owner {OwnerId} ({Name}), year {Year}, portfolio {PortfolioId}.",
                owner.Id, owner.Name, year, scope.PortfolioId);
        }
        return new StatementEmailResult(outcome.Value.Queued, outcome.Value.Reason);
    }

    private Task<OwnerEmailRecipient?> LoadOwnerAsync(
        int portfolioId,
        int ownerId,
        IQueryable<Property> authorizedProperties,
        CancellationToken ct) =>
        _db.OwnerEntities
            .AsNoTracking()
            .Where(o =>
                o.PortfolioId == portfolioId &&
                o.Id == ownerId &&
                o.DeletedAt == null &&
                _db.PropertyOwnerships.Any(ownership =>
                    ownership.PortfolioId == portfolioId
                    && ownership.OwnerEntityId == o.Id
                    && ownership.EffectiveFromUtc <= _timeProvider.UtcNow()
                    && (ownership.EffectiveToUtc == null
                        || ownership.EffectiveToUtc > _timeProvider.UtcNow())
                    && authorizedProperties.Any(property => property.Id == ownership.PropertyId)))
            .Select(o => new OwnerEmailRecipient(o.Id, o.Name, o.Email))
            .FirstOrDefaultAsync(ct);

    private sealed record OwnerEmailRecipient(int Id, string Name, string? Email);

    // ── Plain-text renderer ──────────────────────────────────────────────────────────────────────

    private string RenderStatementText(DTOs.OwnerStatementReport report)
    {
        var sb = new StringBuilder();

        sb.AppendLine($"Owner Statement – {report.Year}");
        sb.AppendLine($"Owner: {report.OwnerName}");
        sb.AppendLine(new string('-', 60));

        if (report.Properties.Count == 0)
        {
            sb.AppendLine("No properties with activity for this year.");
        }
        else
        {
            // Column header
            sb.AppendLine($"{"Property",-30} {"Income",12} {"Expenses",12} {"Mgmt Fee",12} {"Net",12}");
            sb.AppendLine(new string('-', 80));

            foreach (var line in report.Properties)
            {
                var name = line.PropertyName.Length > 29
                    ? line.PropertyName[..29]
                    : line.PropertyName;

                sb.AppendLine(
                    $"{name,-30} {FormatMoney(line.RentalIncome),12} {FormatMoney(line.Expenses),12} " +
                    $"{FormatMoney(line.ManagementFee),12} {FormatMoney(line.NetToOwner),12}");
            }

            sb.AppendLine(new string('-', 80));
            sb.AppendLine(
                $"{"TOTAL",-30} {FormatMoney(report.TotalIncome),12} {FormatMoney(report.TotalExpenses),12} " +
                $"{FormatMoney(report.TotalManagementFee),12} {FormatMoney(report.TotalNetToOwner),12}");
            sb.AppendLine(
                $"{"DISTRIBUTED",-30} {"",12} {"",12} {"",12} {FormatMoney(report.TotalDistributed),12}");
            sb.AppendLine(
                $"{"UNDISTRIBUTED",-30} {"",12} {"",12} {"",12} {FormatMoney(report.Undistributed),12}");
        }

        sb.AppendLine();
        sb.AppendLine("This statement was generated by Rental Command.");

        return sb.ToString();
    }

    private string FormatMoney(decimal amount) => amount.ToString("C2",
        System.Globalization.CultureInfo.GetCultureInfo("en-US"));
}

public sealed record QueueOwnerStatementEmailCommand(
    int PortfolioId,
    int ActorUserId,
    [property: AtomicFingerprintIgnore]
    Guid AuthSessionId,
    [property: AtomicFingerprintIgnore]
    int AccessContextId,
    [property: AtomicFingerprintIgnore]
    long ExpectedAccessRevision,
    int OwnerEntityId,
    int Year,
    string ToEmail,
    string Subject,
    string Body,
    [property: AtomicFingerprintIgnore]
    string DeliveryIdempotencyKey) : IAtomicCommandData;

public sealed record QueueOwnerStatementEmailResult(
    bool Queued,
    string? Reason = null);

public sealed class QueueOwnerStatementEmailHandler
{
    private readonly RentalCommandDbContext _db;

    public QueueOwnerStatementEmailHandler(RentalCommandDbContext db) => _db = db;

    public async Task<QueueOwnerStatementEmailResult> ExecuteAsync(
        QueueOwnerStatementEmailCommand command,
        IAtomicCommandContext attempt,
        CancellationToken ct)
    {
        Validate(command);
        await attempt.AcquireLockAsync("AuthSession", command.AuthSessionId, ct);
        await attempt.AcquireLockAsync(
            "WorkspaceAccessContext", command.AccessContextId, ct);
        await attempt.AcquireLockAsync("Portfolio", command.PortfolioId, ct);
        await attempt.AcquireLockAsync("OwnerEntity", command.OwnerEntityId, ct);

        var now = await AtomicCommandDbClock.ReadDatabaseClockUtcAsync(_db, ct);
        attempt.UseDatabaseWallClockForAudit(now);
        await AuthorizeAsync(command, _db, now, ct);

        attempt.StageOutbox(new OutboxMessage
        {
            PortfolioId = command.PortfolioId,
            MessageType = "email",
            Payload = JsonSerializer.Serialize(new
            {
                to = command.ToEmail,
                subject = command.Subject,
                body = command.Body,
            }),
            IdempotencyKey = $"owner-statement:{command.PortfolioId}:{command.OwnerEntityId}:" +
                $"{command.Year}:{command.DeliveryIdempotencyKey}",
            CreatedAtUtc = now,
            NextAttemptAtUtc = now,
        });
        attempt.StageSemanticEvent(new AtomicSemanticAudit(
            command.PortfolioId,
            "OwnerStatementEmail",
            command.OwnerEntityId,
            AuditLogOperation.Created,
            UserId: command.ActorUserId,
            ChangeReason: $"Owner statement email queued for {command.Year}"), now);

        return new QueueOwnerStatementEmailResult(true);
    }

    public async Task AuthorizeAsync(
        QueueOwnerStatementEmailCommand command,
        IAtomicCommandContext context,
        CancellationToken ct)
    {
        Validate(command);
        await AuthorizeAsync(command, _db, await context.ReadDatabaseClockUtcAsync(ct), ct);
    }

    private async Task AuthorizeAsync(
        QueueOwnerStatementEmailCommand command,
        RentalCommandDbContext db,
        DateTime now,
        CancellationToken ct)
    {
        if (!await LiveAssignments(command, db, now, CapabilityKeys.DataExport,
                CapabilityAuthorizationTargetKind.Workspace).AnyAsync(ct))
            throw new UnauthorizedAccessException(
                "Your workspace export access changed. Refresh and try again.");

        var ownerIsAuthorized = await AuthorizedOwners(command, db, now).AnyAsync(ct);
        if (!ownerIsAuthorized)
            throw new UnauthorizedAccessException(
                "The owner is no longer available within your current reporting scope.");
    }

    internal static IQueryable<OwnerEntity> AuthorizedOwners(
        QueueOwnerStatementEmailCommand command,
        RentalCommandDbContext db,
        DateTime now) =>
        db.Set<OwnerEntity>().AsNoTracking().Where(owner =>
            owner.Id == command.OwnerEntityId && owner.PortfolioId == command.PortfolioId
            && owner.DeletedAt == null
            && db.Set<PropertyOwnership>().Any(ownership =>
                ownership.PortfolioId == command.PortfolioId
                && ownership.OwnerEntityId == owner.Id
                && ownership.EffectiveFromUtc <= now
                && (ownership.EffectiveToUtc == null || ownership.EffectiveToUtc > now)
                && AuthorizedOwnerReportProperties(command, db, now)
                    .Any(property => property.Id == ownership.PropertyId)));

    internal static IQueryable<Property> AuthorizedOwnerReportProperties(
        QueueOwnerStatementEmailCommand command,
        RentalCommandDbContext db,
        DateTime now)
    {
        var assignments = LiveAssignments(command, db, now,
            CapabilityKeys.MoneyOwnerReportsRead, CapabilityAuthorizationTargetKind.Property);
        return db.Set<Property>().AsNoTracking().Where(property =>
            property.PortfolioId == command.PortfolioId && property.DeletedAt == null
            && assignments.Any(assignment =>
                assignment.ScopeKind == MembershipRoleAssignmentScopeKind.AllProperties
                || (assignment.ScopeKind == MembershipRoleAssignmentScopeKind.SelectedProperties
                    && assignment.SelectedProperties.Any(scope =>
                        scope.PortfolioId == command.PortfolioId && scope.PropertyId == property.Id))));
    }

    internal static IQueryable<MembershipRoleAssignment> LiveAssignments(
        QueueOwnerStatementEmailCommand command,
        RentalCommandDbContext db,
        DateTime now,
        string capability,
        CapabilityAuthorizationTargetKind targetKind) =>
        db.AuthorizedAssignmentsForScope(
            new WorkspaceReadScope(
                command.PortfolioId,
                command.ActorUserId,
                command.AuthSessionId,
                command.AccessContextId,
                command.ExpectedAccessRevision),
            [capability],
            targetKind,
            now);

    private void Validate(QueueOwnerStatementEmailCommand command)
    {
        if (command.PortfolioId <= 0 || command.ActorUserId <= 0
            || command.AuthSessionId == Guid.Empty || command.AccessContextId <= 0
            || command.ExpectedAccessRevision <= 0 || command.OwnerEntityId <= 0
            || command.Year is < 1900 or > 9999 || string.IsNullOrWhiteSpace(command.ToEmail)
            || string.IsNullOrWhiteSpace(command.Subject) || string.IsNullOrWhiteSpace(command.Body)
            || string.IsNullOrWhiteSpace(command.DeliveryIdempotencyKey)
            || command.DeliveryIdempotencyKey.Length > 128)
            throw new ArgumentException(
                "Complete owner statement email details and a request key are required.");
    }
}

public static class QueueOwnerStatementEmail
{
    public static readonly AtomicJsonResultCodec<QueueOwnerStatementEmailResult> Codec =
        new("owner-statement.email.queue.v1");

    public static AtomicCommandIdentity Identity(QueueOwnerStatementEmailCommand command) =>
        new("owner-statement.email.queue",
            $"{command.PortfolioId}:{command.AccessContextId}:{command.OwnerEntityId}:" +
            $"{command.Year}:{command.DeliveryIdempotencyKey}");

    public static TransactionalWrite<QueueOwnerStatementEmailCommand, QueueOwnerStatementEmailResult> Write(
        RentalCommandDbContext db,
        QueueOwnerStatementEmailCommand command)
    {
        var handler = new QueueOwnerStatementEmailHandler(db);
        return new(
            Identity(command).CommandType,
            WriteIdempotencyPolicy.Required,
            command,
            Codec.ContractName,
            WriteLockPlan.None,
            handler.ExecuteAsync,
            handler.AuthorizeAsync);
    }
}
