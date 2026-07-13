using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using RentalCommand.Api.DTOs;
using RentalCommand.Core.Atomic;
using RentalCommand.Core.Authorization;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;

namespace RentalCommand.Api.Services.Domain;

public enum AtomicMoneyDomain { Expense, RecurringExpense, Loan, OwnerDistribution }
public enum AtomicMoneyOperation { Create, Update, Delete }

public sealed record AtomicMoneyMutationCommand(
    int PortfolioId,
    int ActorUserId,
    Guid AuthSessionId,
    int AccessContextId,
    long ExpectedAccessRevision,
    string RequiredCapability,
    AtomicMoneyDomain Domain,
    AtomicMoneyOperation Operation,
    int EntityId,
    string IdempotencyKey,
    string RequestJson) : IAtomicCommandData;

public sealed record AtomicMoneyMutationResult(bool Found, bool Applied, int EntityId) : IAtomicResultData;

public sealed class AtomicMoneyMutationHandler
    : IAtomicCommandHandler<AtomicMoneyMutationCommand, AtomicMoneyMutationResult>,
      IAtomicReplayAuthorizer<AtomicMoneyMutationCommand>
{
    public async Task<AtomicMoneyMutationResult> HandleAsync(
        AtomicMoneyMutationCommand command, IAtomicWriteAttempt attempt, CancellationToken ct)
    {
        Validate(command);
        // Authority mutations use this same lock. Once acquired, the exact revision proven below
        // cannot change before this transaction's business write commits or rolls back.
        await attempt.Locking.AcquireAsync(AtomicLockResource.AuthSession, command.AuthSessionId, ct);
        await attempt.Locking.AcquireAsync(
            AtomicLockResource.WorkspaceAccessContext, command.AccessContextId, ct);
        await attempt.Locking.AcquireAsync(AtomicLockResource.Portfolio, command.PortfolioId, ct);
        var now = await attempt.Persistence.ReadDatabaseClockUtcAsync(ct);

        return command.Domain switch
        {
            AtomicMoneyDomain.Expense => await MutateExpenseAsync(command, attempt, now, ct),
            AtomicMoneyDomain.RecurringExpense => await MutateRecurringExpenseAsync(command, attempt, now, ct),
            AtomicMoneyDomain.Loan => await MutateLoanAsync(command, attempt, now, ct),
            AtomicMoneyDomain.OwnerDistribution => await MutateDistributionAsync(command, attempt, now, ct),
            _ => throw new ArgumentOutOfRangeException(nameof(command.Domain)),
        };
    }

    public async Task AuthorizeReplayAsync(
        AtomicMoneyMutationCommand command, IAtomicPersistenceSession persistence, CancellationToken ct)
    {
        Validate(command);
        var now = await persistence.ReadDatabaseClockUtcAsync(ct);
        if (!await HasLiveAccessAsync(command, persistence, now, ct))
            throw Denied("Your workspace access changed. Refresh and try again.");
    }

    private static async Task<AtomicMoneyMutationResult> MutateExpenseAsync(
        AtomicMoneyMutationCommand command, IAtomicWriteAttempt attempt, DateTime now, CancellationToken ct)
    {
        var persistence = attempt.Persistence;
        Expense? entity = null;
        if (command.Operation != AtomicMoneyOperation.Create)
        {
            var query = persistence.Query<Expense>();
            if (command.Operation == AtomicMoneyOperation.Update)
                query = query.Include(row => row.LineItems);
            entity = await query.SingleOrDefaultAsync(
                row => row.Id == command.EntityId && row.PortfolioId == command.PortfolioId && row.DeletedAt == null, ct);
            if (entity is null) return Missing();
            if (!await HasPropertyAuthorityAsync(command, persistence, now,
                    entity.PropertyId, entity.UnitId, entity.WorkOrderId, ct))
                throw Denied(command.Operation == AtomicMoneyOperation.Delete
                    ? "You can view this expense but cannot delete it."
                    : "You can view this expense but cannot change it.");
        }

        if (command.Operation == AtomicMoneyOperation.Delete)
        {
            entity!.DeletedAt = now;
            entity.UpdatedAt = now;
            await attempt.FlushBusinessAsync(ct);
            return Applied(entity.Id);
        }

        if (command.Operation == AtomicMoneyOperation.Create)
        {
            var request = Read<CreateExpenseRequest>(command);
            if (!await ExpenseReferencesExistAsync(command.PortfolioId, request.PropertyId, request.UnitId,
                    request.VendorId, request.WorkOrderId, persistence, ct)) return Missing();
            if (!await HasPropertyAuthorityAsync(command, persistence, now,
                    request.PropertyId, request.UnitId, request.WorkOrderId, ct))
                throw Denied("You cannot create expenses outside your assigned properties.");
            entity = new Expense
            {
                PortfolioId = command.PortfolioId, PropertyId = request.PropertyId, UnitId = request.UnitId,
                VendorId = request.VendorId, WorkOrderId = request.WorkOrderId, Category = request.Category,
                Description = request.Description, Status = request.Status, Amount = request.Amount,
                IncurredAt = Utc(request.IncurredAt), DueDate = Utc(request.DueDate), PaidAt = Utc(request.PaidAt),
                BillableToOwner = request.BillableToOwner, Notes = request.Notes, Subtotal = request.Subtotal,
                TaxAmount = request.TaxAmount, ReceiptData = request.ReceiptData, PaymentMethod = request.PaymentMethod,
                CardLast4 = request.CardLast4, DocumentKind = request.DocumentKind, CreatedAt = now, UpdatedAt = now,
            };
            foreach (var line in request.LineItems)
                entity.LineItems.Add(new ExpenseLineItem
                {
                    Description = line.Description ?? string.Empty, Quantity = line.Quantity,
                    UnitPrice = line.UnitPrice, Amount = line.Amount, LineNumber = line.LineNumber,
                });
            persistence.Add(entity);
        }
        else
        {
            var request = Read<UpdateExpenseRequest>(command);
            var effectivePropertyId = request.PropertyId ?? entity!.PropertyId;
            var effectiveUnitId = request.UnitId ?? entity.UnitId;
            var effectiveWorkOrderId = request.WorkOrderId ?? entity.WorkOrderId;
            if (!await ExpenseReferencesExistAsync(command.PortfolioId, effectivePropertyId, effectiveUnitId,
                    request.VendorId, effectiveWorkOrderId, persistence, ct)) return Missing();
            if (!await HasPropertyAuthorityAsync(command, persistence, now,
                    effectivePropertyId, effectiveUnitId, effectiveWorkOrderId, ct))
                throw Denied("You cannot move an expense outside your assigned properties.");
            if (request.PropertyId.HasValue) entity!.PropertyId = request.PropertyId;
            if (request.UnitId.HasValue) entity!.UnitId = request.UnitId;
            if (request.VendorId.HasValue) entity!.VendorId = request.VendorId;
            if (request.WorkOrderId.HasValue) entity!.WorkOrderId = request.WorkOrderId;
            if (request.Category.HasValue) entity!.Category = request.Category.Value;
            if (request.Description is not null) entity!.Description = request.Description;
            if (request.Status.HasValue) entity!.Status = request.Status.Value;
            if (request.Amount.HasValue) entity!.Amount = request.Amount.Value;
            if (request.IncurredAt.HasValue) entity!.IncurredAt = Utc(request.IncurredAt.Value);
            if (request.DueDate.HasValue) entity!.DueDate = Utc(request.DueDate);
            if (request.PaidAt.HasValue) entity!.PaidAt = Utc(request.PaidAt);
            if (request.BillableToOwner.HasValue) entity!.BillableToOwner = request.BillableToOwner.Value;
            if (request.Notes is not null) entity!.Notes = request.Notes;
            if (request.Subtotal.HasValue) entity!.Subtotal = request.Subtotal;
            if (request.TaxAmount.HasValue) entity!.TaxAmount = request.TaxAmount;
            if (request.ClearReceiptData == true) entity!.ReceiptData = null;
            else if (request.ReceiptData is not null) entity!.ReceiptData = request.ReceiptData;
            if (request.LineItems is not null)
            {
                foreach (var existing in entity!.LineItems.ToArray()) persistence.Remove(existing);
                entity.LineItems.Clear();
                for (var index = 0; index < request.LineItems.Count; index++)
                {
                    var line = request.LineItems[index];
                    entity.LineItems.Add(new ExpenseLineItem
                    {
                        Description = line.Description ?? string.Empty, Quantity = line.Quantity,
                        UnitPrice = line.UnitPrice, Amount = line.Amount, LineNumber = index + 1,
                    });
                }
            }
            entity!.UpdatedAt = now;
        }

        await attempt.FlushBusinessAsync(ct);
        return Applied(entity!.Id);
    }

    private static async Task<AtomicMoneyMutationResult> MutateRecurringExpenseAsync(
        AtomicMoneyMutationCommand command, IAtomicWriteAttempt attempt, DateTime now, CancellationToken ct)
    {
        var persistence = attempt.Persistence;
        var entity = command.Operation == AtomicMoneyOperation.Create ? null :
            await persistence.Query<RecurringExpense>().SingleOrDefaultAsync(row =>
                row.Id == command.EntityId && row.PortfolioId == command.PortfolioId && row.DeletedAt == null, ct);
        if (command.Operation != AtomicMoneyOperation.Create && entity is null) return Missing();
        if (entity is not null && !await HasPropertyAuthorityAsync(
                command, persistence, now, entity.PropertyId, entity.UnitId, null, ct))
            throw Denied(command.Operation == AtomicMoneyOperation.Delete
                ? "You can view this recurring expense but cannot delete it."
                : "You can view this recurring expense but cannot change it.");
        if (command.Operation == AtomicMoneyOperation.Delete)
        {
            entity!.DeletedAt = now; entity.UpdatedAt = now;
            await attempt.FlushBusinessAsync(ct); return Applied(entity.Id);
        }
        if (command.Operation == AtomicMoneyOperation.Create)
        {
            var request = Read<CreateRecurringExpenseRequest>(command);
            if (!await PropertyUnitReferencesExistAsync(
                    command.PortfolioId, request.PropertyId, request.UnitId, persistence, ct)) return Missing();
            if (!await HasPropertyAuthorityAsync(command, persistence, now,
                    request.PropertyId, request.UnitId, null, ct))
                throw Denied("You cannot create recurring expenses outside your assigned properties.");
            var start = Utc(request.StartDate);
            entity = new RecurringExpense
            {
                PortfolioId = command.PortfolioId, PropertyId = request.PropertyId, UnitId = request.UnitId,
                Category = request.Category, Description = request.Description, Amount = request.Amount,
                Frequency = request.Frequency, StartDate = start, NextRunDate = Utc(request.NextRunDate) ?? start,
                Active = request.Active, Notes = request.Notes, CreatedAt = now, UpdatedAt = now,
            };
            persistence.Add(entity);
        }
        else
        {
            var request = Read<UpdateRecurringExpenseRequest>(command);
            var effectiveProperty = request.PropertyId ?? entity!.PropertyId;
            var effectiveUnit = request.UnitId ?? entity.UnitId;
            if (!await PropertyUnitReferencesExistAsync(
                    command.PortfolioId, effectiveProperty, effectiveUnit, persistence, ct, effectiveProperty)) return Missing();
            if (!await HasPropertyAuthorityAsync(command, persistence, now,
                    effectiveProperty, effectiveUnit, null, ct))
                throw Denied("You cannot move a recurring expense outside your assigned properties.");
            if (request.PropertyId.HasValue) entity!.PropertyId = request.PropertyId;
            if (request.UnitId.HasValue) entity!.UnitId = request.UnitId;
            if (request.Category.HasValue) entity!.Category = request.Category.Value;
            if (request.Description is not null) entity!.Description = request.Description;
            if (request.Amount.HasValue) entity!.Amount = request.Amount.Value;
            if (request.Frequency.HasValue) entity!.Frequency = request.Frequency.Value;
            if (request.StartDate.HasValue) entity!.StartDate = Utc(request.StartDate.Value);
            if (request.NextRunDate.HasValue) entity!.NextRunDate = Utc(request.NextRunDate.Value);
            if (request.Active.HasValue) entity!.Active = request.Active.Value;
            if (request.Notes is not null) entity!.Notes = request.Notes;
            entity!.UpdatedAt = now;
        }
        await attempt.FlushBusinessAsync(ct); return Applied(entity!.Id);
    }

    private static async Task<AtomicMoneyMutationResult> MutateLoanAsync(
        AtomicMoneyMutationCommand command, IAtomicWriteAttempt attempt, DateTime now, CancellationToken ct)
    {
        var persistence = attempt.Persistence;
        var entity = command.Operation == AtomicMoneyOperation.Create ? null :
            await persistence.Query<Loan>().SingleOrDefaultAsync(row =>
                row.Id == command.EntityId && row.PortfolioId == command.PortfolioId && row.DeletedAt == null, ct);
        if (command.Operation != AtomicMoneyOperation.Create && entity is null) return Missing();
        if (entity is not null && !await HasPropertyAuthorityAsync(
                command, persistence, now, entity.PropertyId, null, null, ct))
            throw Denied(command.Operation == AtomicMoneyOperation.Delete
                ? "You can view this loan but cannot delete it."
                : "You can view this loan but cannot change it.");
        if (command.Operation == AtomicMoneyOperation.Delete)
        {
            entity!.DeletedAt = now; entity.UpdatedAt = now;
            await attempt.FlushBusinessAsync(ct); return Applied(entity.Id);
        }
        if (command.Operation == AtomicMoneyOperation.Create)
        {
            var request = Read<CreateLoanRequest>(command);
            if (!await persistence.Query<Property>().AnyAsync(
                    row => row.Id == request.PropertyId && row.PortfolioId == command.PortfolioId, ct)) return Missing();
            if (!await HasPropertyAuthorityAsync(command, persistence, now, request.PropertyId, null, null, ct))
                throw Denied("You cannot create loans outside your assigned properties.");
            entity = new Loan
            {
                PortfolioId = command.PortfolioId, PropertyId = request.PropertyId, Lender = request.Lender,
                OriginalAmount = request.OriginalAmount, CurrentBalance = request.CurrentBalance ?? request.OriginalAmount,
                AnnualInterestRatePct = request.AnnualInterestRatePct, TermMonths = request.TermMonths,
                StartDate = Utc(request.StartDate), DayOfMonthDue = request.DayOfMonthDue,
                MonthlyPrincipalInterest = request.MonthlyPrincipalInterest, MonthlyEscrow = request.MonthlyEscrow,
                EscrowCoversTaxes = request.EscrowCoversTaxes, EscrowCoversInsurance = request.EscrowCoversInsurance,
                Status = request.Status, Notes = request.Notes, CreatedAt = now, UpdatedAt = now,
            };
            persistence.Add(entity);
        }
        else
        {
            var request = Read<UpdateLoanRequest>(command);
            if (request.Lender is not null) entity!.Lender = request.Lender;
            if (request.OriginalAmount.HasValue) entity!.OriginalAmount = request.OriginalAmount.Value;
            if (request.CurrentBalance.HasValue) entity!.CurrentBalance = request.CurrentBalance.Value;
            if (request.AnnualInterestRatePct.HasValue) entity!.AnnualInterestRatePct = request.AnnualInterestRatePct.Value;
            if (request.TermMonths.HasValue) entity!.TermMonths = request.TermMonths.Value;
            if (request.StartDate.HasValue) entity!.StartDate = Utc(request.StartDate.Value);
            if (request.DayOfMonthDue.HasValue) entity!.DayOfMonthDue = request.DayOfMonthDue.Value;
            if (request.MonthlyPrincipalInterest.HasValue) entity!.MonthlyPrincipalInterest = request.MonthlyPrincipalInterest.Value;
            if (request.MonthlyEscrow.HasValue) entity!.MonthlyEscrow = request.MonthlyEscrow.Value;
            if (request.EscrowCoversTaxes.HasValue) entity!.EscrowCoversTaxes = request.EscrowCoversTaxes.Value;
            if (request.EscrowCoversInsurance.HasValue) entity!.EscrowCoversInsurance = request.EscrowCoversInsurance.Value;
            if (request.Status.HasValue) entity!.Status = request.Status.Value;
            if (request.Notes is not null) entity!.Notes = request.Notes;
            entity!.UpdatedAt = now;
        }
        await attempt.FlushBusinessAsync(ct); return Applied(entity!.Id);
    }

    private static async Task<AtomicMoneyMutationResult> MutateDistributionAsync(
        AtomicMoneyMutationCommand command, IAtomicWriteAttempt attempt, DateTime now, CancellationToken ct)
    {
        var persistence = attempt.Persistence;
        var entity = command.Operation == AtomicMoneyOperation.Create ? null :
            await persistence.Query<OwnerDistribution>().SingleOrDefaultAsync(row =>
                row.Id == command.EntityId && row.PortfolioId == command.PortfolioId && row.DeletedAt == null, ct);
        if (command.Operation != AtomicMoneyOperation.Create && entity is null) return Missing();
        if (!await HasWorkspaceAuthorityAsync(command, persistence, now, ct))
            throw Denied("Owner distributions require workspace payout authority.");
        if (command.Operation == AtomicMoneyOperation.Delete)
        {
            entity!.DeletedAt = now; entity.UpdatedAt = now;
            await attempt.FlushBusinessAsync(ct); return Applied(entity.Id);
        }
        if (command.Operation == AtomicMoneyOperation.Create)
        {
            var request = Read<CreateOwnerDistributionRequest>(command);
            if (!await DistributionReferencesExistAsync(
                    command.PortfolioId, request.OwnerEntityId, request.PropertyId, persistence, ct)) return Missing();
            entity = new OwnerDistribution
            {
                PortfolioId = command.PortfolioId, OwnerEntityId = request.OwnerEntityId,
                PropertyId = request.PropertyId, Date = Utc(request.Date), Amount = request.Amount,
                Method = request.Method, Memo = request.Memo, CreatedAt = now, UpdatedAt = now,
            };
            persistence.Add(entity);
        }
        else
        {
            var request = Read<UpdateOwnerDistributionRequest>(command);
            var current = entity!;
            var ownerId = request.OwnerEntityId ?? current.OwnerEntityId;
            var propertyId = request.ClearProperty == true ? null : request.PropertyId ?? current.PropertyId;
            if (!await DistributionReferencesExistAsync(
                    command.PortfolioId, ownerId, propertyId, persistence, ct)) return Missing();
            current.OwnerEntityId = ownerId; current.PropertyId = propertyId;
            if (request.Date.HasValue) current.Date = Utc(request.Date.Value);
            if (request.Amount.HasValue) current.Amount = request.Amount.Value;
            if (request.Method.HasValue) current.Method = request.Method.Value;
            if (request.Memo is not null) current.Memo = request.Memo;
            current.UpdatedAt = now;
        }
        await attempt.FlushBusinessAsync(ct); return Applied(entity!.Id);
    }

    private static IQueryable<Property> AuthorizedProperties(
        AtomicMoneyMutationCommand command, IAtomicPersistenceSession persistence, DateTime now)
    {
        var assignments = LiveAssignments(command, persistence, now);
        return persistence.Query<Property>().Where(property =>
            property.PortfolioId == command.PortfolioId && assignments.Any(assignment =>
                assignment.ScopeKind == MembershipRoleAssignmentScopeKind.AllProperties ||
                (assignment.ScopeKind == MembershipRoleAssignmentScopeKind.SelectedProperties &&
                 assignment.SelectedProperties.Any(scope =>
                     scope.PortfolioId == command.PortfolioId && scope.PropertyId == property.Id))));
    }

    private static IQueryable<MembershipRoleAssignment> LiveAssignments(
        AtomicMoneyMutationCommand command, IAtomicPersistenceSession persistence, DateTime now) =>
        persistence.Query<MembershipRoleAssignment>().Where(assignment =>
            assignment.PortfolioId == command.PortfolioId &&
            assignment.Status == MembershipRoleAssignmentStatus.Active && assignment.SuspendedAtUtc == null &&
            assignment.RevokedAtUtc == null && assignment.EffectiveFromUtc <= now &&
            (assignment.EffectiveToUtc == null || assignment.EffectiveToUtc > now) &&
            assignment.WorkspaceMembership!.AccessContextId == command.AccessContextId &&
            assignment.WorkspaceMembership.PortfolioId == command.PortfolioId &&
            assignment.WorkspaceMembership.Status == WorkspaceMembershipStatus.Active &&
            assignment.WorkspaceMembership.SuspendedAtUtc == null && assignment.WorkspaceMembership.RevokedAtUtc == null &&
            assignment.WorkspaceMembership.EffectiveFromUtc <= now &&
            (assignment.WorkspaceMembership.EffectiveToUtc == null || assignment.WorkspaceMembership.EffectiveToUtc > now) &&
            assignment.WorkspaceMembership.AccessContext!.UserId == command.ActorUserId &&
            assignment.WorkspaceMembership.AccessContext.PortfolioId == command.PortfolioId &&
            assignment.WorkspaceMembership.AccessContext.AccessRevision == command.ExpectedAccessRevision &&
            assignment.WorkspaceMembership.AccessContext.Status == WorkspaceAccessContextStatus.Active &&
            assignment.WorkspaceMembership.AccessContext.SuspendedAtUtc == null &&
            assignment.WorkspaceMembership.AccessContext.RevokedAtUtc == null &&
            persistence.Query<AuthSession>().Any(session =>
                session.Id == command.AuthSessionId && session.UserId == command.ActorUserId &&
                session.ActiveAccessContextId == command.AccessContextId && session.Status == AuthSessionStatus.Active &&
                session.RevokedAtUtc == null && session.ExpiresAtUtc > now) &&
            assignment.RoleProfile!.Capabilities.Any(capability =>
                capability.CapabilityDefinition!.Key == command.RequiredCapability));

    private static Task<bool> HasLiveAccessAsync(
        AtomicMoneyMutationCommand command, IAtomicPersistenceSession persistence, DateTime now, CancellationToken ct) =>
        LiveAssignments(command, persistence, now).AnyAsync(ct);

    private static Task<bool> HasWorkspaceAuthorityAsync(
        AtomicMoneyMutationCommand command, IAtomicPersistenceSession persistence, DateTime now, CancellationToken ct) =>
        LiveAssignments(command, persistence, now).AnyAsync(assignment =>
            assignment.ScopeKind == MembershipRoleAssignmentScopeKind.AllProperties &&
            assignment.RoleProfile!.Capabilities.Any(capability =>
                capability.CapabilityDefinition!.Key == command.RequiredCapability &&
                capability.CapabilityDefinition.AuthorizationTargetKind == CapabilityAuthorizationTargetKind.Workspace), ct);

    private static async Task<bool> HasPropertyAuthorityAsync(
        AtomicMoneyMutationCommand command, IAtomicPersistenceSession persistence, DateTime now,
        int? propertyId, int? unitId, int? workOrderId, CancellationToken ct)
    {
        if (propertyId is null && unitId is null && workOrderId is null)
            return await LiveAssignments(command, persistence, now).AnyAsync(assignment =>
                assignment.ScopeKind == MembershipRoleAssignmentScopeKind.AllProperties, ct);
        return await AuthorizedProperties(command, persistence, now).AnyAsync(property =>
            (propertyId == null || property.Id == propertyId) &&
            (unitId == null || persistence.Query<Unit>().Any(unit =>
                unit.Id == unitId && unit.PortfolioId == command.PortfolioId && unit.PropertyId == property.Id)) &&
            (workOrderId == null || persistence.Query<WorkOrder>().Any(order =>
                order.Id == workOrderId && order.PortfolioId == command.PortfolioId && order.PropertyId == property.Id)), ct);
    }

    private static Task<bool> ExpenseReferencesExistAsync(
        int portfolioId, int? propertyId, int? unitId, int? vendorId, int? workOrderId,
        IAtomicPersistenceSession persistence, CancellationToken ct) =>
        persistence.Query<Portfolio>().AnyAsync(portfolio =>
            portfolio.Id == portfolioId &&
            (propertyId == null || persistence.Query<Property>().Any(row =>
                row.Id == propertyId && row.PortfolioId == portfolioId)) &&
            (unitId == null || persistence.Query<Unit>().Any(row =>
                row.Id == unitId && row.PortfolioId == portfolioId &&
                (propertyId == null || row.PropertyId == propertyId))) &&
            (vendorId == null || persistence.Query<Vendor>().Any(row =>
                row.Id == vendorId && row.PortfolioId == portfolioId)) &&
            (workOrderId == null || persistence.Query<WorkOrder>().Any(row =>
                row.Id == workOrderId && row.PortfolioId == portfolioId &&
                (propertyId == null || row.PropertyId == propertyId) &&
                (unitId == null || row.UnitId == unitId))), ct);

    private static Task<bool> PropertyUnitReferencesExistAsync(
        int portfolioId, int? propertyId, int? unitId, IAtomicPersistenceSession persistence,
        CancellationToken ct, int? effectivePropertyId = null) =>
        persistence.Query<Portfolio>().AnyAsync(portfolio =>
            portfolio.Id == portfolioId &&
            (propertyId == null || persistence.Query<Property>().Any(row =>
                row.Id == propertyId && row.PortfolioId == portfolioId)) &&
            (unitId == null || persistence.Query<Unit>().Any(row =>
                row.Id == unitId && row.PortfolioId == portfolioId &&
                ((effectivePropertyId ?? propertyId) == null ||
                 row.PropertyId == (effectivePropertyId ?? propertyId)))), ct);

    private static Task<bool> DistributionReferencesExistAsync(
        int portfolioId, int ownerId, int? propertyId, IAtomicPersistenceSession persistence, CancellationToken ct) =>
        persistence.Query<OwnerEntity>().AnyAsync(owner =>
            owner.Id == ownerId && owner.PortfolioId == portfolioId &&
            (propertyId == null || persistence.Query<Property>().Any(row =>
                row.Id == propertyId && row.PortfolioId == portfolioId && row.OwnerEntityId == ownerId)), ct);

    private static T Read<T>(AtomicMoneyMutationCommand command) where T : class =>
        JsonSerializer.Deserialize<T>(command.RequestJson)
        ?? throw new ArgumentException("Money mutation request payload is invalid.");

    private static void Validate(AtomicMoneyMutationCommand command)
    {
        if (command.PortfolioId <= 0 || command.ActorUserId <= 0 || command.AuthSessionId == Guid.Empty ||
            command.AccessContextId <= 0 || command.ExpectedAccessRevision <= 0 ||
            string.IsNullOrWhiteSpace(command.RequiredCapability) || string.IsNullOrWhiteSpace(command.RequestJson) ||
            string.IsNullOrWhiteSpace(command.IdempotencyKey) || command.IdempotencyKey.Length > 128 ||
            (command.Operation != AtomicMoneyOperation.Create && command.EntityId <= 0))
            throw new ArgumentException("Portfolio, actor, access revision, capability, operation, and payload are required.");
    }

    private static DateTime Utc(DateTime value) => value.Kind == DateTimeKind.Utc ? value : value.ToUniversalTime();
    private static DateTime? Utc(DateTime? value) => value.HasValue ? Utc(value.Value) : null;
    private static AtomicMoneyMutationResult Missing() => new(false, false, 0);
    private static AtomicMoneyMutationResult Applied(int id) => new(true, true, id);
    private static UnauthorizedAccessException Denied(string message) => new(message);
}

public static class AtomicMoneyMutation
{
    public static readonly AtomicJsonResultCodec<AtomicMoneyMutationResult> Codec =
        new("money.scoped-mutation.v1");

    public static AtomicMoneyMutationCommand Command<TRequest>(
        WorkspaceReadScope scope, string capability, AtomicMoneyDomain domain,
        AtomicMoneyOperation operation, int entityId, string idempotencyKey, TRequest request) where TRequest : class =>
        new(scope.PortfolioId, scope.UserId, scope.SessionId, scope.AccessContextId, scope.AccessRevision,
            capability, domain, operation, entityId, idempotencyKey, JsonSerializer.Serialize(request));

    public static AtomicCommandIdentity Identity(AtomicMoneyMutationCommand command) =>
        new($"money.{command.Domain.ToString().ToLowerInvariant()}.{command.Operation.ToString().ToLowerInvariant()}",
            $"{command.PortfolioId}:{command.AccessContextId}:{command.Domain}:{command.Operation}:" +
            $"{command.EntityId}:{command.IdempotencyKey}");
}
