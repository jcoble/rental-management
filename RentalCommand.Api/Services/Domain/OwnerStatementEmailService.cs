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

namespace RentalCommand.Api.Services.Domain;

/// <inheritdoc cref="IOwnerStatementEmailService"/>
public class OwnerStatementEmailService : IOwnerStatementEmailService
{
    private readonly RentalCommandDbContext _db;
    private readonly IOwnerStatementService _statements;
    private readonly ILogger<OwnerStatementEmailService> _logger;
    private readonly TimeProvider _timeProvider;
    private readonly IAtomicUnitOfWork _atomic;

    public OwnerStatementEmailService(
        RentalCommandDbContext db,
        IOwnerStatementService statements,
        ILogger<OwnerStatementEmailService> logger,
        TimeProvider timeProvider,
        IAtomicUnitOfWork atomic)
    {
        _db = db;
        _statements = statements;
        _logger = logger;
        _timeProvider = timeProvider;
        _atomic = atomic;
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
        var outcome = await _atomic.ExecuteAsync(
            QueueOwnerStatementEmail.Identity(command), command, QueueOwnerStatementEmail.Codec, ct);
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

    private static string RenderStatementText(DTOs.OwnerStatementReport report)
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

    private static string FormatMoney(decimal amount) => amount.ToString("C2",
        System.Globalization.CultureInfo.GetCultureInfo("en-US"));
}

public sealed record QueueOwnerStatementEmailCommand(
    int PortfolioId,
    int ActorUserId,
    Guid AuthSessionId,
    int AccessContextId,
    long ExpectedAccessRevision,
    int OwnerEntityId,
    int Year,
    string ToEmail,
    string Subject,
    string Body,
    string DeliveryIdempotencyKey) : IAtomicCommandData;

public sealed record QueueOwnerStatementEmailResult(
    bool Queued,
    string? Reason = null) : IAtomicResultData;

public sealed class QueueOwnerStatementEmailHandler
    : IAtomicCommandHandler<QueueOwnerStatementEmailCommand, QueueOwnerStatementEmailResult>,
      IAtomicReplayAuthorizer<QueueOwnerStatementEmailCommand>
{
    public async Task<QueueOwnerStatementEmailResult> HandleAsync(
        QueueOwnerStatementEmailCommand command,
        IAtomicWriteAttempt attempt,
        CancellationToken ct)
    {
        Validate(command);
        await attempt.Locking.AcquireAsync(AtomicLockResource.AuthSession, command.AuthSessionId, ct);
        await attempt.Locking.AcquireAsync(
            AtomicLockResource.WorkspaceAccessContext, command.AccessContextId, ct);
        await attempt.Locking.AcquireAsync(AtomicLockResource.Portfolio, command.PortfolioId, ct);
        await attempt.Locking.AcquireAsync(AtomicLockResource.OwnerEntity, command.OwnerEntityId, ct);

        var now = await attempt.Persistence.ReadDatabaseClockUtcAsync(ct);
        attempt.UseDatabaseWallClockForAudit(now);
        await AuthorizeAsync(command, attempt.Persistence, now, ct);

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

    public async Task AuthorizeReplayAsync(
        QueueOwnerStatementEmailCommand command,
        IAtomicPersistenceSession persistence,
        CancellationToken ct)
    {
        Validate(command);
        await AuthorizeAsync(command, persistence, await persistence.ReadDatabaseClockUtcAsync(ct), ct);
    }

    private static async Task AuthorizeAsync(
        QueueOwnerStatementEmailCommand command,
        IAtomicPersistenceSession persistence,
        DateTime now,
        CancellationToken ct)
    {
        if (!await LiveAssignments(command, persistence, now, CapabilityKeys.DataExport,
                CapabilityAuthorizationTargetKind.Workspace).AnyAsync(ct))
            throw new UnauthorizedAccessException(
                "Your workspace export access changed. Refresh and try again.");

        var ownerIsAuthorized = await persistence.Query<OwnerEntity>().AsNoTracking().AnyAsync(owner =>
            owner.Id == command.OwnerEntityId && owner.PortfolioId == command.PortfolioId
            && owner.DeletedAt == null
            && persistence.Query<PropertyOwnership>().Any(ownership =>
                ownership.PortfolioId == command.PortfolioId
                && ownership.OwnerEntityId == owner.Id
                && ownership.EffectiveFromUtc <= now
                && (ownership.EffectiveToUtc == null || ownership.EffectiveToUtc > now)
                && AuthorizedOwnerReportProperties(command, persistence, now)
                    .Any(property => property.Id == ownership.PropertyId)), ct);
        if (!ownerIsAuthorized)
            throw new UnauthorizedAccessException(
                "The owner is no longer available within your current reporting scope.");
    }

    private static IQueryable<Property> AuthorizedOwnerReportProperties(
        QueueOwnerStatementEmailCommand command,
        IAtomicPersistenceSession persistence,
        DateTime now)
    {
        var assignments = LiveAssignments(command, persistence, now,
            CapabilityKeys.MoneyOwnerReportsRead, CapabilityAuthorizationTargetKind.Property);
        return persistence.Query<Property>().AsNoTracking().Where(property =>
            property.PortfolioId == command.PortfolioId && property.DeletedAt == null
            && assignments.Any(assignment =>
                assignment.ScopeKind == MembershipRoleAssignmentScopeKind.AllProperties
                || (assignment.ScopeKind == MembershipRoleAssignmentScopeKind.SelectedProperties
                    && assignment.SelectedProperties.Any(scope =>
                        scope.PortfolioId == command.PortfolioId && scope.PropertyId == property.Id))));
    }

    private static IQueryable<MembershipRoleAssignment> LiveAssignments(
        QueueOwnerStatementEmailCommand command,
        IAtomicPersistenceSession persistence,
        DateTime now,
        string capability,
        CapabilityAuthorizationTargetKind targetKind) =>
        persistence.Query<MembershipRoleAssignment>().AsNoTracking().Where(assignment =>
            assignment.PortfolioId == command.PortfolioId
            && assignment.Status == MembershipRoleAssignmentStatus.Active
            && assignment.SuspendedAtUtc == null && assignment.RevokedAtUtc == null
            && assignment.EffectiveFromUtc <= now
            && (assignment.EffectiveToUtc == null || assignment.EffectiveToUtc > now)
            && assignment.WorkspaceMembership!.AccessContextId == command.AccessContextId
            && assignment.WorkspaceMembership.PortfolioId == command.PortfolioId
            && assignment.WorkspaceMembership.Status == WorkspaceMembershipStatus.Active
            && assignment.WorkspaceMembership.SuspendedAtUtc == null
            && assignment.WorkspaceMembership.RevokedAtUtc == null
            && assignment.WorkspaceMembership.EffectiveFromUtc <= now
            && (assignment.WorkspaceMembership.EffectiveToUtc == null
                || assignment.WorkspaceMembership.EffectiveToUtc > now)
            && assignment.WorkspaceMembership.AccessContext!.UserId == command.ActorUserId
            && assignment.WorkspaceMembership.AccessContext.PortfolioId == command.PortfolioId
            && assignment.WorkspaceMembership.AccessContext.AccessRevision == command.ExpectedAccessRevision
            && assignment.WorkspaceMembership.AccessContext.Status == WorkspaceAccessContextStatus.Active
            && assignment.WorkspaceMembership.AccessContext.SuspendedAtUtc == null
            && assignment.WorkspaceMembership.AccessContext.RevokedAtUtc == null
            && persistence.Query<AuthSession>().Any(session =>
                session.Id == command.AuthSessionId && session.UserId == command.ActorUserId
                && session.ActiveAccessContextId == command.AccessContextId
                && session.Status == AuthSessionStatus.Active && session.RevokedAtUtc == null
                && session.ExpiresAtUtc > now)
            && assignment.RoleProfile!.Capabilities.Any(grant =>
                grant.CapabilityDefinition!.Key == capability
                && grant.CapabilityDefinition.AuthorizationTargetKind == targetKind));

    private static void Validate(QueueOwnerStatementEmailCommand command)
    {
        if (command.PortfolioId <= 0 || command.ActorUserId <= 0
            || command.AuthSessionId == Guid.Empty || command.AccessContextId <= 0
            || command.ExpectedAccessRevision <= 0 || command.OwnerEntityId <= 0
            || command.Year is < 1900 or > 9999 || string.IsNullOrWhiteSpace(command.ToEmail)
            || string.IsNullOrWhiteSpace(command.Subject) || string.IsNullOrWhiteSpace(command.Body)
            || string.IsNullOrWhiteSpace(command.DeliveryIdempotencyKey)
            || command.DeliveryIdempotencyKey.Length > 128)
            throw new ArgumentException(
                "Owner statement email identity, recipient, content, year, and idempotency key are required.");
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
}
