using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using RentalCommand.Core.Atomic;
using RentalCommand.Core.Authorization;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Outbox;
using RentalCommand.Core.Payments;
using RentalCommand.Data.Accounting;

namespace RentalCommand.Data.Payments;

public sealed class RecoverOpeningSecurityDepositsRule
{
    private readonly RentalCommandDbContext _db;

    public RecoverOpeningSecurityDepositsRule(RentalCommandDbContext db) => _db = db;

    public async Task<RecoverOpeningSecurityDepositsResult> ExecuteAsync(
        RecoverOpeningSecurityDepositsCommand command,
        IAtomicCommandContext context,
        CancellationToken ct)
    {
        Validate(command);
        var times = await RentalCommand.Data.AtomicCommandClock.ReadCommandTimesAsync(_db, command.PortfolioId, ct);
        await AuthorizeAsync(command, _db, times.WallClockUtc, ct);

        var recovery = await TenantMoneyPersistence.RecoverOpeningSecurityDepositsAsync(_db, context,
            command.PortfolioId,
            command.EffectiveOn,
            command.ExpectedAccountCount,
            command.ExpectedTotal,
            command.FinancialReference,
            command.ActorUserId,
            times.EffectiveNowUtc,
            ct);
        if (!recovery.IsValid)
            throw new InvalidOperationException(recovery.ValidationError);

        var reference = command.FinancialReference.Trim();
        var openingEntries = await _db.SecurityDepositEntries
            .Include(entry => entry.SecurityDepositAccount)
            .Where(entry => entry.PortfolioId == command.PortfolioId
                && entry.EntryType == SecurityDepositEntryType.Receipt
                && entry.Direction == SecurityDepositDirection.Increase
                && entry.BusinessKey.StartsWith($"opening-deposit:{reference}:account:"))
            .OrderBy(entry => entry.Id)
            .ToListAsync(ct);
        foreach (var openingEntry in openingEntries)
        {
            await MoneyAccountingPosting.PostOpeningSecurityDepositAsync(
                _db, context, openingEntry, command.ActorUserId, ct);
        }
        if (recovery.CreatedAccountCount > 0 || recovery.CreatedEntryCount > 0)
        {
            context.UseDatabaseWallClockForAudit(times.EffectiveNowUtc);
            context.StageSemanticEvent(new AtomicSemanticAudit(
                command.PortfolioId,
                nameof(Portfolio),
                command.PortfolioId,
                AuditLogOperation.Updated,
                command.ActorUserId,
                NewValues: JsonSerializer.Serialize(new
                {
                    command.EffectiveOn,
                    recovery.AccountCount,
                    recovery.CreatedAccountCount,
                    recovery.CreatedEntryCount,
                    recovery.ReconciledTotal,
                    FinancialReference = reference,
                }),
                ChangeReason: "Recovered exact Agreement-backed opening security-deposit position."),
                times.EffectiveNowUtc);
            context.StageOutbox(new OutboxMessage
            {
                PortfolioId = command.PortfolioId,
                MessageType = "data-update",
                Payload = JsonSerializer.Serialize(new
                {
                    entityType = nameof(Portfolio),
                    entityId = command.PortfolioId,
                    operation = "opening-balance-recovery",
                    data = new
                    {
                        recovery.AccountCount,
                        recovery.CreatedAccountCount,
                        recovery.CreatedEntryCount,
                        recovery.ReconciledTotal,
                        financialReference = reference,
                    },
                }),
                IdempotencyKey = OutboxIdempotency.Create(
                    "opening-security-deposits",
                    $"{command.PortfolioId}:{reference}"),
                CreatedAtUtc = times.EffectiveNowUtc,
                NextAttemptAtUtc = times.EffectiveNowUtc,
            });
        }

        return new RecoverOpeningSecurityDepositsResult(
            recovery.AccountCount,
            recovery.CreatedAccountCount,
            recovery.CreatedEntryCount,
            recovery.ReconciledTotal,
            reference);
    }

    public async Task AuthorizeAsync(
        RecoverOpeningSecurityDepositsCommand command, IAtomicCommandContext context, CancellationToken ct)
    {
        Validate(command);
        await AuthorizeAsync(
            command,
            _db,
            await _db.Database.SqlQuery<DateTime>($"SELECT clock_timestamp() AS \"Value\"").SingleAsync(ct),
            ct);
    }

    private static async Task AuthorizeAsync(
        RecoverOpeningSecurityDepositsCommand command,
        RentalCommandDbContext db,
        DateTime securityNowUtc,
        CancellationToken ct)
    {
        var assignments = db.Set<MembershipRoleAssignment>().Where(assignment =>
            assignment.Status == MembershipRoleAssignmentStatus.Active
            && assignment.ScopeKind == MembershipRoleAssignmentScopeKind.AllProperties
            && assignment.SuspendedAtUtc == null
            && assignment.RevokedAtUtc == null
            && assignment.EffectiveFromUtc <= securityNowUtc
            && (assignment.EffectiveToUtc == null
                || assignment.EffectiveToUtc > securityNowUtc)
            && assignment.RoleProfile!.Capabilities.Any(capability =>
                capability.CapabilityDefinition!.Key == command.RequiredCapability
                && capability.CapabilityDefinition.AuthorizationTargetKind
                    == CapabilityAuthorizationTargetKind.Property));
        var authorized = await db.Set<WorkspaceMembership>().AnyAsync(membership =>
            membership.PortfolioId == command.PortfolioId
            && membership.AccessContextId == command.AccessContextId
            && membership.Status == WorkspaceMembershipStatus.Active
            && membership.SuspendedAtUtc == null
            && membership.RevokedAtUtc == null
            && membership.EffectiveFromUtc <= securityNowUtc
            && (membership.EffectiveToUtc == null
                || membership.EffectiveToUtc > securityNowUtc)
            && assignments.Any(assignment =>
                assignment.WorkspaceMembershipId == membership.Id
                && assignment.PortfolioId == command.PortfolioId)
            && db.Set<AuthSession>().Any(session =>
                session.Id == command.AuthSessionId
                && session.UserId == command.ActorUserId
                && session.ActiveAccessContextId == command.AccessContextId
                && session.Status == AuthSessionStatus.Active
                && session.RevokedAtUtc == null
                && session.ExpiresAtUtc > securityNowUtc)
            && db.Set<WorkspaceAccessContext>().Any(context =>
                context.Id == command.AccessContextId
                && context.UserId == command.ActorUserId
                && context.PortfolioId == command.PortfolioId
                && context.AccessRevision == command.ExpectedAccessRevision
                && context.Status == WorkspaceAccessContextStatus.Active
                && context.SuspendedAtUtc == null
                && context.RevokedAtUtc == null),
            ct);
        if (!authorized)
            throw new UnauthorizedAccessException(
                "Opening deposit recovery requires active all-property deposit-management authority.");
    }

    private static void Validate(RecoverOpeningSecurityDepositsCommand command)
    {
        if (command.PortfolioId <= 0
            || command.EffectiveOn == default
            || command.ExpectedAccountCount <= 0
            || command.ExpectedTotal <= 0m
            || command.ActorUserId <= 0
            || command.AuthSessionId == Guid.Empty
            || command.AccessContextId <= 0
            || command.ExpectedAccessRevision <= 0
            || string.IsNullOrWhiteSpace(command.FinancialReference)
            || command.FinancialReference.Trim().Length > 80
            || string.IsNullOrWhiteSpace(command.DeliveryIdempotencyKey)
            || command.DeliveryIdempotencyKey.Length > 200)
            throw new ArgumentException(
                "Portfolio, opening control, actor, access, reference, and operation key are required.");
        if (command.RequiredCapability != CapabilityKeys.MoneyDepositsManage)
            throw new ArgumentException(
                "Opening security-deposit recovery requires deposit-management capability.");
    }
}
