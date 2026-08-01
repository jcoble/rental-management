using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using RentalCommand.Core.Atomic;
using RentalCommand.Core.Authorization;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Leasing;

namespace RentalCommand.Data.Leasing;

public sealed class CreatePropertyDispositionHandler
    : IAtomicCommandHandler<CreatePropertyDispositionCommand, CreatePropertyDispositionResult>
{
    private readonly RentalCommandDbContext _db;

    public CreatePropertyDispositionHandler(RentalCommandDbContext db) => _db = db;

    public async Task<CreatePropertyDispositionResult> HandleAsync(
        CreatePropertyDispositionCommand command, IAtomicCommandContext context, CancellationToken ct)
    {
        Validate(command);
        await context.AcquireLockAsync("Property", command.PropertyId, ct);
        var times = await RentalCommand.Data.AtomicCommandClock.ReadCommandTimesAsync(_db, command.PortfolioId, ct);
        var now = times.WallClockUtc;
        await AuthorizeAsync(command, _db, now, ct);

        var mutation = await AtomicLeaseMutationPersistence.CreatePropertyDispositionAsync(_db,
            context, command.PortfolioId, command.PropertyId, command.ClosedOnDate,
            command.SalePrice, command.SellingCosts, command.BuyerName, command.Memo,
            command.ActorUserId, now, times.BusinessDate, ct);
        if (mutation is null)
            return new(CreatePropertyDispositionOutcome.PropertyNotFoundOrAlreadyDisposed, null, 0, 0);

        Stage(context, command, now, nameof(PropertyDisposition), mutation.DispositionId,
            AuditLogOperation.Created, "Recorded property disposition.");
        Stage(context, command, now, nameof(Property), command.PropertyId,
            AuditLogOperation.Updated, "Property made inactive by disposition.");
        StageMany(context, command, now, nameof(LeaseManagement), mutation.LeaseManagementIds,
            "Returned possession or canceled the planned relationship and closed its account lifecycle.");
        StageMany(context, command, now, nameof(TenantAccount), mutation.TenantAccountIds,
            "Closed Tenant Account because the property was disposed.");
        StageMany(context, command, now, nameof(TenantAutopayEnrollment), mutation.AutopayEnrollmentIds,
            "Canceled autopay because the property was disposed.");
        StageMany(context, command, now, nameof(LeaseManagementParty), mutation.PartyIds,
            "Ended party membership because the property was disposed.");
        StageMany(context, command, now, nameof(TenantUserAccess), mutation.RevokedAccessIds,
            "Revoked tenant access because the property was disposed.");
        StageMany(context, command, now, nameof(WorkspaceAccessContext), mutation.AccessContextIds,
            "Advanced access revision after property disposition.");
        foreach (var id in mutation.ManagementHoldIds)
            Stage(context, command, now, nameof(UnitOperationalPeriod), id, AuditLogOperation.Created,
                "Opened management hold because the property was disposed.");
        StageMany(context, command, now, nameof(Unit), mutation.UnitIds,
            "Unit removed from operation because the property was disposed.");
        StageMany(context, command, now, nameof(CapitalAsset), mutation.CapitalAssetIds,
            "Capital asset disposed with its property.");

        context.StageOutbox(new OutboxMessage
        {
            PortfolioId = command.PortfolioId,
            MessageType = "data-update",
            Payload = JsonSerializer.Serialize(new
            {
                entityType = nameof(PropertyDisposition),
                entityId = mutation.DispositionId,
                data = new { eventName = "property-disposed", propertyId = command.PropertyId },
            }),
            IdempotencyKey = command.DeliveryIdempotencyKey,
            CreatedAtUtc = now,
            NextAttemptAtUtc = now,
        });
        return new(CreatePropertyDispositionOutcome.Created, mutation.DispositionId,
            mutation.LeaseManagementIds.Count, mutation.TenantAccountIds.Count);
    }

    public async Task AuthorizeReplayAsync(CreatePropertyDispositionCommand command, IAtomicCommandContext context, CancellationToken ct)
    {
        Validate(command);
        var now = await _db.Database.SqlQuery<DateTime>($"SELECT clock_timestamp() AS \"Value\"").SingleAsync(ct);
        await AuthorizeAsync(command, _db, now, ct);
    }

    private static async Task AuthorizeAsync(CreatePropertyDispositionCommand command,
        RentalCommandDbContext db, DateTime now, CancellationToken ct)
    {
        var memberships = db.Set<WorkspaceMembership>().Where(membership =>
            db.Set<AuthSession>().Any(session => session.Id == command.AuthSessionId
                && session.UserId == command.ActorUserId
                && session.ActiveAccessContextId == command.AccessContextId
                && session.Status == AuthSessionStatus.Active && session.RevokedAtUtc == null
                && session.ExpiresAtUtc > now)
            && db.Set<WorkspaceAccessContext>().Any(context =>
                context.Id == command.AccessContextId && context.UserId == command.ActorUserId
                && context.PortfolioId == command.PortfolioId
                && context.AccessRevision == command.ExpectedAccessRevision
                && context.Status == WorkspaceAccessContextStatus.Active
                && context.SuspendedAtUtc == null && context.RevokedAtUtc == null)
            && membership.AccessContextId == command.AccessContextId
            && membership.PortfolioId == command.PortfolioId
            && membership.Status == WorkspaceMembershipStatus.Active
            && membership.SuspendedAtUtc == null && membership.RevokedAtUtc == null
            && membership.EffectiveFromUtc <= now
            && (membership.EffectiveToUtc == null || membership.EffectiveToUtc > now));
        var assignments = db.Set<MembershipRoleAssignment>().Where(assignment =>
            assignment.PortfolioId == command.PortfolioId
            && assignment.Status == MembershipRoleAssignmentStatus.Active
            && assignment.SuspendedAtUtc == null && assignment.RevokedAtUtc == null
            && assignment.EffectiveFromUtc <= now
            && (assignment.EffectiveToUtc == null || assignment.EffectiveToUtc > now));
        var authorized = await db.Set<Property>().AnyAsync(property =>
            property.Id == command.PropertyId && property.PortfolioId == command.PortfolioId
            && memberships.Any(membership => assignments.Any(assignment =>
                assignment.WorkspaceMembershipId == membership.Id
                && (assignment.ScopeKind == MembershipRoleAssignmentScopeKind.AllProperties
                    || (assignment.ScopeKind == MembershipRoleAssignmentScopeKind.SelectedProperties
                        && assignment.SelectedProperties.Any(scope =>
                            scope.PropertyId == property.Id && scope.PortfolioId == command.PortfolioId)))
                && assignment.RoleProfile!.Capabilities.Any(capability =>
                    capability.CapabilityDefinition!.Key == CapabilityKeys.RentalsManage
                    && capability.CapabilityDefinition.AuthorizationTargetKind
                        == CapabilityAuthorizationTargetKind.Property))), ct);
        if (!authorized)
            throw new UnauthorizedAccessException("The property is not authorized in the current workspace scope.");
    }

    private static void StageMany(IAtomicCommandContext context, CreatePropertyDispositionCommand command,
        DateTime now, string entityType, IReadOnlyList<int> ids, string reason)
    {
        foreach (var id in ids)
            Stage(context, command, now, entityType, id, AuditLogOperation.Updated, reason);
    }

    private static void Stage(IAtomicCommandContext context, CreatePropertyDispositionCommand command,
        DateTime now, string entityType, int entityId, AuditLogOperation operation, string reason) =>
        context.StageSemanticEvent(new AtomicSemanticAudit(command.PortfolioId, entityType, entityId,
            operation, UserId: command.ActorUserId, ChangeReason: reason), now);

    private static void Validate(CreatePropertyDispositionCommand command)
    {
        if (command.PortfolioId <= 0 || command.PropertyId <= 0 || command.ActorUserId <= 0
            || command.AuthSessionId == Guid.Empty || command.AccessContextId <= 0
            || command.ExpectedAccessRevision <= 0
            || string.IsNullOrWhiteSpace(command.DeliveryIdempotencyKey)
            || command.DeliveryIdempotencyKey.Length > 200)
            throw new ArgumentException("Property, actor, access, and delivery identifiers are required.");
    }
}
