using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using RentalCommand.Api.DTOs;
using RentalCommand.Core.Atomic;
using RentalCommand.Core.Authorization;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;

namespace RentalCommand.Api.Services.Domain;

public enum AtomicMoneyDomain
{
    Expense,
    RecurringExpense,
    Loan,
    OwnerDistribution,
    CapitalAsset,
    PropertyDisposition,
}

public enum AtomicMoneyOperation { Create, Update, Delete, CapitalizeExpense }

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

public sealed record AtomicMoneyMutationResult(
    bool Found,
    bool Applied,
    int EntityId,
    string? ResponseJson = null) : IAtomicResultData;

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
        var times = await attempt.Persistence.ReadCommandTimesAsync(command.PortfolioId, ct);
        var now = times.WallClockUtc;
        var businessDateUtc = times.BusinessDate.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
        attempt.UseDatabaseWallClockForAudit(now);
        if (!await LiveAssignments(command, attempt.Persistence, now).AnyAsync(ct))
            throw Denied("Your workspace access changed. Refresh and try again.");
        if (RequiresDestructiveDisbursementAuthority(command) &&
            !await HasDestructiveDisbursementAuthorityAsync(command, attempt.Persistence, now, ct))
        {
            throw Denied("Destructive disbursement authority is required.");
        }

        return command.Domain switch
        {
            AtomicMoneyDomain.Expense => await MutateExpenseAsync(command, attempt, now, ct),
            AtomicMoneyDomain.RecurringExpense => await MutateRecurringExpenseAsync(command, attempt, now, ct),
            AtomicMoneyDomain.Loan => await MutateLoanAsync(command, attempt, now, businessDateUtc, ct),
            AtomicMoneyDomain.OwnerDistribution => await MutateDistributionAsync(command, attempt, now, ct),
            AtomicMoneyDomain.CapitalAsset => await MutateCapitalAssetAsync(command, attempt, now, ct),
            AtomicMoneyDomain.PropertyDisposition => await MutatePropertyDispositionAsync(command, attempt, now, ct),
            _ => throw new ArgumentOutOfRangeException(nameof(command.Domain)),
        };
    }

    public async Task AuthorizeReplayAsync(
        AtomicMoneyMutationCommand command, IAtomicPersistenceSession persistence, CancellationToken ct)
    {
        Validate(command);
        var now = await persistence.ReadDatabaseClockUtcAsync(ct);
        if (!await HasReplayAuthorityAsync(command, persistence, now, ct))
            throw Denied("Your workspace access changed. Refresh and try again.");
    }

    private static async Task<bool> HasReplayAuthorityAsync(
        AtomicMoneyMutationCommand command,
        IAtomicPersistenceSession persistence,
        DateTime now,
        CancellationToken ct)
    {
        if (command.Domain == AtomicMoneyDomain.OwnerDistribution)
        {
            return await HasWorkspaceAuthorityAsync(command, persistence, now, ct) &&
                (!RequiresDestructiveDisbursementAuthority(command) ||
                 await HasDestructiveDisbursementAuthorityAsync(command, persistence, now, ct));
        }

        if (command.Domain == AtomicMoneyDomain.Expense)
        {
            if (command.Operation == AtomicMoneyOperation.Create)
            {
                var request = Read<CreateExpenseRequest>(command);
                return await HasPropertyAuthorityAsync(command, persistence, now,
                    request.PropertyId, request.UnitId, request.WorkOrderId, ct);
            }

            var target = await persistence.Query<Expense>().IgnoreQueryFilters()
                .Where(row => row.Id == command.EntityId && row.PortfolioId == command.PortfolioId)
                .Select(row => new { row.PropertyId, row.UnitId, row.WorkOrderId })
                .SingleOrDefaultAsync(ct);
            return target is not null && await HasPropertyAuthorityAsync(command, persistence, now,
                target.PropertyId, target.UnitId, target.WorkOrderId, ct);
        }

        if (command.Domain == AtomicMoneyDomain.RecurringExpense)
        {
            if (command.Operation == AtomicMoneyOperation.Create)
            {
                var request = Read<CreateRecurringExpenseRequest>(command);
                return await HasPropertyAuthorityAsync(command, persistence, now,
                    request.PropertyId, request.UnitId, null, ct);
            }

            var target = await persistence.Query<RecurringExpense>().IgnoreQueryFilters()
                .Where(row => row.Id == command.EntityId && row.PortfolioId == command.PortfolioId)
                .Select(row => new { row.PropertyId, row.UnitId })
                .SingleOrDefaultAsync(ct);
            return target is not null && await HasPropertyAuthorityAsync(command, persistence, now,
                target.PropertyId, target.UnitId, null, ct);
        }

        if (command.Domain == AtomicMoneyDomain.CapitalAsset)
        {
            if (command.Operation == AtomicMoneyOperation.Create)
            {
                var request = Read<CreateCapitalAssetRequest>(command);
                return await HasPropertyAuthorityAsync(command, persistence, now,
                    request.PropertyId, request.UnitId, null, ct);
            }

            if (command.Operation == AtomicMoneyOperation.CapitalizeExpense)
            {
                var target = await persistence.Query<Expense>().IgnoreQueryFilters()
                    .Where(row => row.Id == command.EntityId && row.PortfolioId == command.PortfolioId)
                    .Select(row => new { row.PropertyId, row.UnitId })
                    .SingleOrDefaultAsync(ct);
                return target is not null && await HasPropertyAuthorityAsync(command, persistence, now,
                    target.PropertyId, target.UnitId, null, ct);
            }

            var assetTarget = await persistence.Query<CapitalAsset>().IgnoreQueryFilters()
                .Where(row => row.Id == command.EntityId && row.PortfolioId == command.PortfolioId)
                .Select(row => new { row.PropertyId, row.UnitId })
                .SingleOrDefaultAsync(ct);
            return assetTarget is not null && await HasPropertyAuthorityAsync(command, persistence, now,
                assetTarget.PropertyId, assetTarget.UnitId, null, ct);
        }

        if (command.Domain == AtomicMoneyDomain.PropertyDisposition)
        {
            var target = await persistence.Query<PropertyDisposition>().IgnoreQueryFilters()
                .Where(row => row.Id == command.EntityId && row.PortfolioId == command.PortfolioId)
                .Select(row => new { row.PropertyId })
                .SingleOrDefaultAsync(ct);
            return target is not null && await HasPropertyAuthorityAsync(command, persistence, now,
                target.PropertyId, null, null, ct);
        }

        if (command.Operation == AtomicMoneyOperation.Create)
        {
            var request = Read<CreateLoanRequest>(command);
            return await HasPropertyAuthorityAsync(command, persistence, now,
                request.PropertyId, null, null, ct);
        }

        var loanTarget = await persistence.Query<Loan>().IgnoreQueryFilters()
            .Where(row => row.Id == command.EntityId && row.PortfolioId == command.PortfolioId)
            .Select(row => new { row.PropertyId })
            .SingleOrDefaultAsync(ct);
        return loanTarget is not null && await HasPropertyAuthorityAsync(command, persistence, now,
            loanTarget.PropertyId, null, null, ct);
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
                query = query.Include(row => row.LineItems).Include(row => row.Allocations);
            entity = await query.SingleOrDefaultAsync(
                row => row.Id == command.EntityId && row.PortfolioId == command.PortfolioId && row.DeletedAt == null, ct);
            if (entity is null) return Missing();
            if (!await HasPropertyAuthorityAsync(command, persistence, now,
                    entity.PropertyId, entity.UnitId, entity.WorkOrderId, ct))
                return Missing();
        }

        if (command.Operation == AtomicMoneyOperation.Delete)
        {
            entity!.DeletedAt = now;
            entity.UpdatedAt = now;
            attempt.BindSemanticAudit(entity, Audit(command, nameof(Expense), AuditLogOperation.Deleted,
                $"Expense {entity.Id} deleted"));
            await attempt.FlushBusinessAsync(ct);
            StageDataUpdate(attempt, command, nameof(Expense), entity.Id, now, deleted: true);
            return Applied(entity.Id);
        }

        if (command.Operation == AtomicMoneyOperation.Create)
        {
            var request = Read<CreateExpenseRequest>(command);
            var operationalScope = ResolveExpenseOperationalScope(
                request.OperationalScope, request.PropertyId, request.UnitId, request.WorkOrderId);
            ValidateExpenseAllocations(request.Allocations, request.Amount);
            var references = await ExpenseReferencesExistAsync(
                command.PortfolioId, operationalScope, request.PropertyId, request.UnitId,
                request.VendorId, request.WorkOrderId, request.Allocations, persistence, ct);
            if (references is null) return Missing();
            if (!await HasPropertyAuthorityAsync(command, persistence, now,
                    references.PropertyId, references.UnitId, references.WorkOrderId, ct))
                return Missing();
            entity = new Expense
            {
                PortfolioId = command.PortfolioId, OperationalScope = operationalScope,
                PropertyId = references.PropertyId, UnitId = references.UnitId,
                VendorId = request.VendorId, WorkOrderId = references.WorkOrderId, Category = request.Category,
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
            foreach (var allocation in request.Allocations)
                entity.Allocations.Add(NewExpenseAllocation(
                    allocation, command.PortfolioId, now));
            persistence.Add(entity);
            attempt.BindSemanticAudit(entity, Audit(command, nameof(Expense), AuditLogOperation.Created,
                $"Expense {entity.Description} created", entityId: 0));
        }
        else
        {
            var request = Read<UpdateExpenseRequest>(command);
            var operationalScope = ResolveUpdatedExpenseOperationalScope(entity!, request);
            var (requestedPropertyId, requestedUnitId, requestedWorkOrderId) =
                ResolveUpdatedExpenseReferenceIds(entity, request, operationalScope);
            var effectiveAmount = request.Amount ?? entity.Amount;
            if (request.Allocations is not null)
                ValidateExpenseAllocations(request.Allocations, effectiveAmount);
            var references = await ExpenseReferencesExistAsync(
                command.PortfolioId, operationalScope, requestedPropertyId, requestedUnitId,
                request.VendorId ?? entity.VendorId, requestedWorkOrderId, request.Allocations,
                persistence, ct);
            if (references is null) return Missing();
            if (!await HasPropertyAuthorityAsync(command, persistence, now,
                    references.PropertyId, references.UnitId, references.WorkOrderId, ct))
                return Missing();
            entity!.OperationalScope = operationalScope;
            entity.PropertyId = references.PropertyId;
            entity.UnitId = references.UnitId;
            entity.WorkOrderId = references.WorkOrderId;
            if (request.VendorId.HasValue) entity!.VendorId = request.VendorId;
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
            if (request.Allocations is not null)
            {
                foreach (var existing in entity!.Allocations.ToArray()) persistence.Remove(existing);
                entity.Allocations.Clear();
                foreach (var allocation in request.Allocations)
                    entity.Allocations.Add(NewExpenseAllocation(
                        allocation, command.PortfolioId, now));
            }
            entity!.UpdatedAt = now;
            attempt.BindSemanticAudit(entity, Audit(command, nameof(Expense), AuditLogOperation.Updated,
                $"Expense {entity.Id} updated"));
        }

        await attempt.FlushBusinessAsync(ct);
        var responseJson = await SnapshotExpenseAsync(entity!.Id, command.PortfolioId, persistence, ct);
        StageDataUpdate(attempt, command, nameof(Expense), entity.Id, now, responseJson: responseJson);
        return Applied(entity.Id, responseJson);
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
            return Missing();
        if (command.Operation == AtomicMoneyOperation.Delete)
        {
            entity!.DeletedAt = now; entity.UpdatedAt = now;
            attempt.BindSemanticAudit(entity, Audit(command, nameof(RecurringExpense), AuditLogOperation.Deleted,
                $"Recurring expense {entity.Id} deleted"));
            await attempt.FlushBusinessAsync(ct);
            StageDataUpdate(attempt, command, nameof(RecurringExpense), entity.Id, now, deleted: true);
            return Applied(entity.Id);
        }
        if (command.Operation == AtomicMoneyOperation.Create)
        {
            var request = Read<CreateRecurringExpenseRequest>(command);
            if (!await PropertyUnitReferencesExistAsync(
                    command.PortfolioId, request.PropertyId, request.UnitId, persistence, ct)) return Missing();
            if (!await HasPropertyAuthorityAsync(command, persistence, now,
                    request.PropertyId, request.UnitId, null, ct))
                return Missing();
            var start = Utc(request.StartDate);
            entity = new RecurringExpense
            {
                PortfolioId = command.PortfolioId, PropertyId = request.PropertyId, UnitId = request.UnitId,
                Category = request.Category, Description = request.Description, Amount = request.Amount,
                Frequency = request.Frequency, StartDate = start, NextRunDate = Utc(request.NextRunDate) ?? start,
                Active = request.Active, Notes = request.Notes, CreatedAt = now, UpdatedAt = now,
            };
            persistence.Add(entity);
            attempt.BindSemanticAudit(entity, Audit(command, nameof(RecurringExpense), AuditLogOperation.Created,
                $"Recurring expense {entity.Description} created", entityId: 0));
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
                return Missing();
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
            attempt.BindSemanticAudit(entity, Audit(command, nameof(RecurringExpense), AuditLogOperation.Updated,
                $"Recurring expense {entity.Id} updated"));
        }
        await attempt.FlushBusinessAsync(ct);
        var responseJson = await SnapshotRecurringExpenseAsync(
            entity!.Id, command.PortfolioId, persistence, ct);
        StageDataUpdate(attempt, command, nameof(RecurringExpense), entity.Id, now, responseJson: responseJson);
        return Applied(entity.Id, responseJson);
    }

    private static async Task<AtomicMoneyMutationResult> MutateLoanAsync(
        AtomicMoneyMutationCommand command,
        IAtomicWriteAttempt attempt,
        DateTime now,
        DateTime businessDateUtc,
        CancellationToken ct)
    {
        var persistence = attempt.Persistence;
        var entity = command.Operation == AtomicMoneyOperation.Create ? null :
            await persistence.Query<Loan>().SingleOrDefaultAsync(row =>
                row.Id == command.EntityId && row.PortfolioId == command.PortfolioId && row.DeletedAt == null, ct);
        if (command.Operation != AtomicMoneyOperation.Create && entity is null) return Missing();
        if (entity is not null && !await HasPropertyAuthorityAsync(
                command, persistence, now, entity.PropertyId, null, null, ct))
            return Missing();
        if (command.Operation == AtomicMoneyOperation.Delete)
        {
            entity!.DeletedAt = now; entity.UpdatedAt = now;
            attempt.BindSemanticAudit(entity, Audit(command, nameof(Loan), AuditLogOperation.Deleted,
                $"Loan {entity.Id} deleted"));
            await attempt.FlushBusinessAsync(ct);
            StageDataUpdate(attempt, command, nameof(Loan), entity.Id, now, deleted: true);
            return Applied(entity.Id);
        }
        if (command.Operation == AtomicMoneyOperation.Create)
        {
            var request = Read<CreateLoanRequest>(command);
            if (!await persistence.Query<Property>().AnyAsync(
                    row => row.Id == request.PropertyId && row.PortfolioId == command.PortfolioId, ct)) return Missing();
            if (!await HasPropertyAuthorityAsync(command, persistence, now, request.PropertyId, null, null, ct))
                return Missing();
            entity = new Loan
            {
                PortfolioId = command.PortfolioId, PropertyId = request.PropertyId, Lender = request.Lender,
                OriginalAmount = request.OriginalAmount, CurrentBalance = request.CurrentBalance ?? request.OriginalAmount,
                AnnualInterestRatePct = request.AnnualInterestRatePct, TermMonths = request.TermMonths,
                StartDate = Utc(request.StartDate), DebtServiceAutomationStartDate = businessDateUtc,
                DayOfMonthDue = request.DayOfMonthDue,
                MonthlyPrincipalInterest = request.MonthlyPrincipalInterest, MonthlyEscrow = request.MonthlyEscrow,
                EscrowCoversTaxes = request.EscrowCoversTaxes, EscrowCoversInsurance = request.EscrowCoversInsurance,
                Status = request.Status, Notes = request.Notes, CreatedAt = now, UpdatedAt = now,
            };
            persistence.Add(entity);
            attempt.BindSemanticAudit(entity, Audit(command, nameof(Loan), AuditLogOperation.Created,
                $"Loan for {entity.Lender} created", entityId: 0));
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
            attempt.BindSemanticAudit(entity, Audit(command, nameof(Loan), AuditLogOperation.Updated,
                $"Loan {entity.Id} updated"));
        }
        await attempt.FlushBusinessAsync(ct);
        var responseJson = await SnapshotLoanAsync(entity!.Id, command.PortfolioId, persistence, ct);
        StageDataUpdate(attempt, command, nameof(Loan), entity.Id, now, responseJson: responseJson);
        return Applied(entity.Id, responseJson);
    }

    private static async Task<AtomicMoneyMutationResult> MutateCapitalAssetAsync(
        AtomicMoneyMutationCommand command, IAtomicWriteAttempt attempt, DateTime now, CancellationToken ct)
    {
        var persistence = attempt.Persistence;

        if (command.Operation == AtomicMoneyOperation.CapitalizeExpense)
        {
            var expense = await persistence.Query<Expense>().SingleOrDefaultAsync(row =>
                row.Id == command.EntityId && row.PortfolioId == command.PortfolioId
                && row.DeletedAt == null, ct);
            if (expense?.PropertyId is not int propertyId || expense.CapitalizedAssetId is not null)
                return Missing();
            if (!await HasPropertyAuthorityAsync(command, persistence, now,
                    propertyId, expense.UnitId, null, ct))
                return Missing();

            var request = Read<CapitalizeExpenseRequest>(command);
            var asset = new CapitalAsset
            {
                PortfolioId = command.PortfolioId,
                PropertyId = propertyId,
                UnitId = expense.UnitId,
                SourceExpenseId = expense.Id,
                Description = string.IsNullOrWhiteSpace(request.Description)
                    ? expense.Description
                    : request.Description.Trim(),
                CostBasis = expense.Amount,
                InServiceDate = Utc(request.InServiceDate),
                Method = request.Method,
                RecoveryYears = request.RecoveryYears,
                Convention = request.Convention,
                AccumulatedDepreciation = 0m,
                CreatedAt = now,
                UpdatedAt = now,
            };
            persistence.Add(asset);
            attempt.BindSemanticAudit(asset, Audit(command, nameof(CapitalAsset),
                AuditLogOperation.Created, $"Expense {expense.Id} capitalized", entityId: 0));
            await attempt.FlushBusinessAsync(ct);

            expense.CapitalizedAssetId = asset.Id;
            expense.UpdatedAt = now;
            attempt.BindSemanticAudit(expense, Audit(command, nameof(Expense),
                AuditLogOperation.Updated, $"Expense {expense.Id} linked to capital asset {asset.Id}", expense.Id));
            await attempt.FlushBusinessAsync(ct);

            StageDataUpdate(attempt, command, nameof(CapitalAsset), asset.Id, now);
            var expenseJson = await SnapshotExpenseAsync(expense.Id, command.PortfolioId, persistence, ct);
            StageDataUpdate(attempt, command, nameof(Expense), expense.Id, now, responseJson: expenseJson);
            return Applied(asset.Id);
        }

        CapitalAsset? entity = null;
        if (command.Operation != AtomicMoneyOperation.Create)
        {
            entity = await persistence.Query<CapitalAsset>().SingleOrDefaultAsync(row =>
                row.Id == command.EntityId && row.PortfolioId == command.PortfolioId
                && row.DeletedAt == null, ct);
            if (entity is null) return Missing();
            if (!await HasPropertyAuthorityAsync(command, persistence, now,
                    entity.PropertyId, entity.UnitId, null, ct))
                return Missing();
        }

        if (command.Operation == AtomicMoneyOperation.Delete)
        {
            Expense? sourceExpense = null;
            if (entity!.SourceExpenseId is int sourceExpenseId)
            {
                sourceExpense = await persistence.Query<Expense>().IgnoreQueryFilters()
                    .SingleOrDefaultAsync(row => row.Id == sourceExpenseId
                        && row.PortfolioId == command.PortfolioId
                        && row.DeletedAt == null
                        && row.CapitalizedAssetId == entity.Id, ct);
                if (sourceExpense is not null)
                {
                    sourceExpense.CapitalizedAssetId = null;
                    sourceExpense.UpdatedAt = now;
                    attempt.BindSemanticAudit(sourceExpense, Audit(command, nameof(Expense),
                        AuditLogOperation.Updated, $"Capital asset {entity.Id} unlinked", sourceExpense.Id));
                }
            }

            entity.DeletedAt = now;
            entity.UpdatedAt = now;
            attempt.BindSemanticAudit(entity, Audit(command, nameof(CapitalAsset),
                AuditLogOperation.Deleted, $"Capital asset {entity.Id} deleted"));
            await attempt.FlushBusinessAsync(ct);
            StageDataUpdate(attempt, command, nameof(CapitalAsset), entity.Id, now, deleted: true);
            if (sourceExpense is not null)
            {
                var expenseJson = await SnapshotExpenseAsync(
                    sourceExpense.Id, command.PortfolioId, persistence, ct);
                StageDataUpdate(attempt, command, nameof(Expense), sourceExpense.Id, now,
                    responseJson: expenseJson);
            }
            return Applied(entity.Id);
        }

        if (command.Operation == AtomicMoneyOperation.Create)
        {
            var request = Read<CreateCapitalAssetRequest>(command);
            if (!await PropertyUnitReferencesExistAsync(
                    command.PortfolioId, request.PropertyId, request.UnitId, persistence, ct))
                return Missing();
            if (!await HasPropertyAuthorityAsync(command, persistence, now,
                    request.PropertyId, request.UnitId, null, ct))
                return Missing();
            entity = new CapitalAsset
            {
                PortfolioId = command.PortfolioId,
                PropertyId = request.PropertyId,
                UnitId = request.UnitId,
                Description = request.Description.Trim(),
                CostBasis = request.CostBasis,
                InServiceDate = Utc(request.InServiceDate),
                Method = request.Method,
                RecoveryYears = request.RecoveryYears,
                Convention = request.Convention,
                AccumulatedDepreciation = request.AccumulatedDepreciation,
                DisposedOnDate = Utc(request.DisposedOnDate),
                CreatedAt = now,
                UpdatedAt = now,
            };
            persistence.Add(entity);
            attempt.BindSemanticAudit(entity, Audit(command, nameof(CapitalAsset),
                AuditLogOperation.Created, $"Capital asset {entity.Description} created", entityId: 0));
        }
        else if (command.Operation == AtomicMoneyOperation.Update)
        {
            var request = Read<UpdateCapitalAssetRequest>(command);
            var propertyId = request.PropertyId ?? entity!.PropertyId;
            var unitId = request.ClearUnit == true ? null : request.UnitId ?? entity.UnitId;
            if (!await PropertyUnitReferencesExistAsync(
                    command.PortfolioId, propertyId, unitId, persistence, ct, propertyId))
                return Missing();
            if (!await HasPropertyAuthorityAsync(command, persistence, now,
                    propertyId, unitId, null, ct))
                return Missing();

            entity.PropertyId = propertyId;
            entity.UnitId = unitId;
            if (request.Description is not null) entity.Description = request.Description.Trim();
            if (request.CostBasis.HasValue) entity.CostBasis = request.CostBasis.Value;
            if (request.InServiceDate.HasValue) entity.InServiceDate = Utc(request.InServiceDate.Value);
            if (request.Method.HasValue) entity.Method = request.Method.Value;
            if (request.RecoveryYears.HasValue) entity.RecoveryYears = request.RecoveryYears.Value;
            if (request.Convention.HasValue) entity.Convention = request.Convention.Value;
            if (request.AccumulatedDepreciation.HasValue)
                entity.AccumulatedDepreciation = request.AccumulatedDepreciation.Value;
            if (request.ClearDisposedOnDate == true) entity.DisposedOnDate = null;
            else if (request.DisposedOnDate.HasValue)
                entity.DisposedOnDate = Utc(request.DisposedOnDate.Value);
            entity.UpdatedAt = now;
            attempt.BindSemanticAudit(entity, Audit(command, nameof(CapitalAsset),
                AuditLogOperation.Updated, $"Capital asset {entity.Id} updated"));
        }
        else
        {
            throw new ArgumentException("Unsupported capital asset mutation operation.");
        }

        await attempt.FlushBusinessAsync(ct);
        StageDataUpdate(attempt, command, nameof(CapitalAsset), entity!.Id, now);
        return Applied(entity.Id);
    }

    private static async Task<AtomicMoneyMutationResult> MutatePropertyDispositionAsync(
        AtomicMoneyMutationCommand command, IAtomicWriteAttempt attempt, DateTime now, CancellationToken ct)
    {
        if (command.Operation is not (AtomicMoneyOperation.Update or AtomicMoneyOperation.Delete))
            throw new ArgumentException("Property disposition create uses its dedicated atomic command.");

        var persistence = attempt.Persistence;
        var entity = await persistence.Query<PropertyDisposition>().SingleOrDefaultAsync(row =>
            row.Id == command.EntityId && row.PortfolioId == command.PortfolioId
            && row.DeletedAt == null, ct);
        if (entity is null) return Missing();
        if (!await HasPropertyAuthorityAsync(command, persistence, now,
                entity.PropertyId, null, null, ct))
            return Missing();

        if (command.Operation == AtomicMoneyOperation.Delete)
        {
            entity.DeletedAt = now;
            entity.UpdatedAt = now;
            attempt.BindSemanticAudit(entity, Audit(command, nameof(PropertyDisposition),
                AuditLogOperation.Deleted, $"Property disposition {entity.Id} deleted"));
            await attempt.FlushBusinessAsync(ct);
            StageDataUpdate(attempt, command, nameof(PropertyDisposition), entity.Id, now, deleted: true);
            return Applied(entity.Id);
        }

        var request = Read<UpdatePropertyDispositionRequest>(command);
        if (request.ClosedOnDate.HasValue) entity.ClosedOnDate = Utc(request.ClosedOnDate.Value).Date;
        if (request.SalePrice.HasValue) entity.SalePrice = request.SalePrice.Value;
        if (request.SellingCosts.HasValue) entity.SellingCosts = request.SellingCosts.Value;
        if (request.BuyerName is not null) entity.BuyerName = Normalize(request.BuyerName);
        if (request.Memo is not null) entity.Memo = Normalize(request.Memo);
        entity.UpdatedAt = now;
        attempt.BindSemanticAudit(entity, Audit(command, nameof(PropertyDisposition),
            AuditLogOperation.Updated, $"Property disposition {entity.Id} updated"));
        await attempt.FlushBusinessAsync(ct);
        StageDataUpdate(attempt, command, nameof(PropertyDisposition), entity.Id, now);
        return Applied(entity.Id);
    }

    private static async Task<AtomicMoneyMutationResult> MutateDistributionAsync(
        AtomicMoneyMutationCommand command, IAtomicWriteAttempt attempt, DateTime now, CancellationToken ct)
    {
        var persistence = attempt.Persistence;
        if (command.Operation == AtomicMoneyOperation.Create)
        {
            var create = Read<CreateOwnerDistributionRequest>(command);
            await attempt.Locking.AcquireAsync(
                AtomicLockResource.OwnerEntity, create.OwnerEntityId, ct);
        }
        else
        {
            await attempt.Locking.AcquireAsync(
                AtomicLockResource.OwnerDistribution, command.EntityId, ct);
        }
        var entity = command.Operation == AtomicMoneyOperation.Create ? null :
            await persistence.Query<OwnerDistribution>().SingleOrDefaultAsync(row =>
                row.Id == command.EntityId && row.PortfolioId == command.PortfolioId && row.DeletedAt == null, ct);
        if (command.Operation != AtomicMoneyOperation.Create && entity is null) return Missing();
        if (!await HasWorkspaceAuthorityAsync(command, persistence, now, ct))
            throw Denied("Owner distributions require workspace payout authority.");
        if (command.Operation == AtomicMoneyOperation.Delete)
        {
            entity!.DeletedAt = now;
            entity.UpdatedAt = now;
            attempt.BindSemanticAudit(entity, Audit(command, nameof(OwnerDistribution),
                AuditLogOperation.Deleted, $"Owner distribution {entity.Id} deleted"));
            await attempt.FlushBusinessAsync(ct);
            StageDataUpdate(attempt, command, nameof(OwnerDistribution), entity.Id, now, deleted: true);
            return Applied(entity.Id);
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
            attempt.BindSemanticAudit(entity, Audit(command, nameof(OwnerDistribution),
                AuditLogOperation.Created, "Owner distribution recorded", entityId: 0));
        }
        else
        {
            var request = Read<UpdateOwnerDistributionRequest>(command);
            var current = entity!;
            var ownerId = request.OwnerEntityId ?? current.OwnerEntityId;
            await attempt.Locking.AcquireAsync(AtomicLockResource.OwnerEntity, ownerId, ct);
            var propertyId = request.ClearProperty == true ? null : request.PropertyId ?? current.PropertyId;
            if (!await DistributionReferencesExistAsync(
                    command.PortfolioId, ownerId, propertyId, persistence, ct)) return Missing();
            current.OwnerEntityId = ownerId; current.PropertyId = propertyId;
            if (request.Date.HasValue) current.Date = Utc(request.Date.Value);
            if (request.Amount.HasValue) current.Amount = request.Amount.Value;
            if (request.Method.HasValue) current.Method = request.Method.Value;
            if (request.Memo is not null) current.Memo = request.Memo;
            current.UpdatedAt = now;
            attempt.BindSemanticAudit(current, Audit(command, nameof(OwnerDistribution),
                AuditLogOperation.Updated, $"Owner distribution {current.Id} updated"));
        }
        await attempt.FlushBusinessAsync(ct);
        var snapshot = await SnapshotOwnerDistributionAsync(
            entity!.Id, command.PortfolioId, persistence, ct);
        StageDataUpdate(attempt, command, nameof(OwnerDistribution), entity.Id, now, snapshot);
        return Applied(entity.Id, snapshot);
    }

    private static async Task<string> SnapshotExpenseAsync(
        int entityId,
        int portfolioId,
        IAtomicPersistenceSession persistence,
        CancellationToken ct)
    {
        var response = await MoneyResponseProjection.ExpenseDetails(
                persistence.Query<Expense>().AsNoTracking().Where(expense =>
                    expense.Id == entityId && expense.PortfolioId == portfolioId),
                persistence.Query<StoredFile>().AsNoTracking())
            .SingleAsync(ct);
        return JsonSerializer.Serialize(response);
    }

    private static async Task<string> SnapshotLoanAsync(
        int entityId,
        int portfolioId,
        IAtomicPersistenceSession persistence,
        CancellationToken ct)
    {
        var response = await MoneyResponseProjection.Loans(
                persistence.Query<Loan>().AsNoTracking().Where(loan =>
                    loan.Id == entityId && loan.PortfolioId == portfolioId))
            .SingleAsync(ct);
        return JsonSerializer.Serialize(response);
    }

    private static async Task<string> SnapshotRecurringExpenseAsync(
        int entityId,
        int portfolioId,
        IAtomicPersistenceSession persistence,
        CancellationToken ct)
    {
        var response = await MoneyResponseProjection.RecurringExpenses(
                persistence.Query<RecurringExpense>().AsNoTracking().Where(expense =>
                    expense.Id == entityId && expense.PortfolioId == portfolioId))
            .SingleAsync(ct);
        return JsonSerializer.Serialize(response);
    }

    private static async Task<string> SnapshotOwnerDistributionAsync(
        int entityId,
        int portfolioId,
        IAtomicPersistenceSession persistence,
        CancellationToken ct)
    {
        var response = await persistence.Query<OwnerDistribution>()
            .AsNoTracking()
            .Where(distribution =>
                distribution.Id == entityId && distribution.PortfolioId == portfolioId)
            .Select(distribution => new OwnerDistributionResponse
            {
                Id = distribution.Id,
                PortfolioId = distribution.PortfolioId,
                OwnerEntityId = distribution.OwnerEntityId,
                OwnerName = distribution.OwnerEntity!.Name,
                PropertyId = distribution.PropertyId,
                PropertyName = distribution.Property == null ? null : distribution.Property.Name,
                Date = distribution.Date,
                Amount = distribution.Amount,
                Method = distribution.Method,
                Memo = distribution.Memo,
                CreatedAt = distribution.CreatedAt,
                UpdatedAt = distribution.UpdatedAt,
            })
            .SingleAsync(ct);
        return JsonSerializer.Serialize(response);
    }

    private static void StageDataUpdate(
        IAtomicWriteAttempt attempt,
        AtomicMoneyMutationCommand command,
        string entityType,
        int entityId,
        DateTime now,
        string? responseJson = null,
        bool deleted = false)
    {
        object data = responseJson is null
            ? new { }
            : JsonSerializer.Deserialize<JsonElement>(responseJson);
        attempt.StageOutbox(new OutboxMessage
        {
            PortfolioId = command.PortfolioId,
            MessageType = "data-update",
            Payload = JsonSerializer.Serialize(new
            {
                entityType,
                entityId,
                operation = deleted ? "delete" : "update",
                data,
            }),
            IdempotencyKey = $"money:{command.PortfolioId}:{command.AccessContextId}:" +
                $"{command.Domain}:{command.Operation}:{entityId}:{command.IdempotencyKey}:data-update",
            CreatedAtUtc = now,
            NextAttemptAtUtc = now,
        });
    }

    private static AtomicSemanticAudit Audit(
        AtomicMoneyMutationCommand command,
        string entityType,
        AuditLogOperation operation,
        string reason,
        int? entityId = null) => new(
            command.PortfolioId,
            entityType,
            entityId ?? command.EntityId,
            operation,
            UserId: command.ActorUserId,
            ChangeReason: reason);

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
        AtomicMoneyMutationCommand command, IAtomicPersistenceSession persistence, DateTime now)
    {
        var requiredTargetKind = RequiredTargetKind(command);
        return persistence.Query<MembershipRoleAssignment>().Where(assignment =>
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
                capability.CapabilityDefinition!.Key == command.RequiredCapability &&
                capability.CapabilityDefinition.AuthorizationTargetKind == requiredTargetKind));
    }

    private static Task<bool> HasWorkspaceAuthorityAsync(
        AtomicMoneyMutationCommand command, IAtomicPersistenceSession persistence, DateTime now, CancellationToken ct) =>
        LiveAssignments(command, persistence, now).AnyAsync(assignment =>
            assignment.ScopeKind == MembershipRoleAssignmentScopeKind.AllProperties &&
            assignment.RoleProfile!.Capabilities.Any(capability =>
                capability.CapabilityDefinition!.Key == command.RequiredCapability &&
                capability.CapabilityDefinition.AuthorizationTargetKind == CapabilityAuthorizationTargetKind.Workspace), ct);

    private static bool RequiresDestructiveDisbursementAuthority(AtomicMoneyMutationCommand command) =>
        command.Domain == AtomicMoneyDomain.OwnerDistribution &&
        command.Operation == AtomicMoneyOperation.Delete;

    private static Task<bool> HasDestructiveDisbursementAuthorityAsync(
        AtomicMoneyMutationCommand command,
        IAtomicPersistenceSession persistence,
        DateTime now,
        CancellationToken ct) =>
        LiveAssignments(command, persistence, now).AnyAsync(assignment =>
            assignment.ScopeKind == MembershipRoleAssignmentScopeKind.AllProperties &&
            assignment.RoleProfile!.Capabilities.Any(capability =>
                capability.CapabilityDefinition!.Key == CapabilityKeys.MoneyReconciliationDestructive &&
                capability.CapabilityDefinition.AuthorizationTargetKind ==
                    CapabilityAuthorizationTargetKind.Workspace), ct);

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

    private static async Task<ExpenseReferenceContext?> ExpenseReferencesExistAsync(
        int portfolioId,
        ExpenseOperationalScope operationalScope,
        int? propertyId,
        int? unitId,
        int? vendorId,
        int? workOrderId,
        IReadOnlyList<ExpenseAllocationRequest>? allocations,
        IAtomicPersistenceSession persistence,
        CancellationToken ct)
    {
        var allocationPropertyIds = new HashSet<int>();
        var allocationUnitIds = new HashSet<int>();
        var allocationOwnerIds = new HashSet<int>();
        if (allocations is not null)
        {
            foreach (var allocation in allocations)
            {
                if (allocation.PropertyId is int allocationPropertyId)
                    allocationPropertyIds.Add(allocationPropertyId);
                if (allocation.UnitId is int allocationUnitId)
                    allocationUnitIds.Add(allocationUnitId);
                if (allocation.OwnerEntityId is int allocationOwnerId)
                    allocationOwnerIds.Add(allocationOwnerId);
            }
        }

        var propertyTargets = allocationPropertyIds.ToArray();
        var unitTargets = allocationUnitIds.ToArray();
        var ownerTargets = allocationOwnerIds.ToArray();
        var portfolio = persistence.Query<Portfolio>().Where(row =>
            row.Id == portfolioId &&
            (vendorId == null || persistence.Query<Vendor>().Any(candidate =>
                candidate.Id == vendorId && candidate.PortfolioId == portfolioId)) &&
            (propertyTargets.Length == 0 || persistence.Query<Property>().Count(candidate =>
                candidate.PortfolioId == portfolioId && propertyTargets.Contains(candidate.Id)) ==
                propertyTargets.Length) &&
            (unitTargets.Length == 0 || persistence.Query<Unit>().Count(candidate =>
                candidate.PortfolioId == portfolioId && unitTargets.Contains(candidate.Id)) ==
                unitTargets.Length) &&
            (ownerTargets.Length == 0 || persistence.Query<OwnerEntity>().Count(candidate =>
                candidate.PortfolioId == portfolioId && ownerTargets.Contains(candidate.Id)) ==
                ownerTargets.Length));

        return operationalScope switch
        {
            ExpenseOperationalScope.Portfolio when
                propertyId is null && unitId is null && workOrderId is null =>
                await portfolio.Select(_ => new ExpenseReferenceContext(
                        ExpenseOperationalScope.Portfolio, null, null, null))
                    .SingleOrDefaultAsync(ct),

            ExpenseOperationalScope.Property when
                propertyId is not null && unitId is null && workOrderId is null =>
                await portfolio.SelectMany(_ => persistence.Query<Property>()
                        .Where(candidate =>
                            candidate.Id == propertyId && candidate.PortfolioId == portfolioId))
                    .Select(candidate => new ExpenseReferenceContext(
                        ExpenseOperationalScope.Property, candidate.Id, null, null))
                    .SingleOrDefaultAsync(ct),

            ExpenseOperationalScope.Unit when unitId is not null && workOrderId is null =>
                await portfolio.SelectMany(_ => persistence.Query<Unit>()
                        .Where(candidate =>
                            candidate.Id == unitId && candidate.PortfolioId == portfolioId &&
                            (propertyId == null || candidate.PropertyId == propertyId)))
                    .Select(candidate => new ExpenseReferenceContext(
                        ExpenseOperationalScope.Unit, candidate.PropertyId, candidate.Id, null))
                    .SingleOrDefaultAsync(ct),

            ExpenseOperationalScope.WorkOrder when workOrderId is not null =>
                await portfolio.SelectMany(_ => persistence.Query<WorkOrder>()
                        .Where(candidate =>
                            candidate.Id == workOrderId && candidate.PortfolioId == portfolioId &&
                            (propertyId == null || candidate.PropertyId == propertyId) &&
                            (unitId == null || candidate.UnitId == unitId)))
                    .Select(candidate => new ExpenseReferenceContext(
                        ExpenseOperationalScope.WorkOrder, candidate.PropertyId,
                        candidate.UnitId, candidate.Id))
                    .SingleOrDefaultAsync(ct),

            _ => null,
        };
    }

    private static ExpenseOperationalScope ResolveExpenseOperationalScope(
        ExpenseOperationalScope? requested,
        int? propertyId,
        int? unitId,
        int? workOrderId) =>
        requested ?? (workOrderId is not null
            ? ExpenseOperationalScope.WorkOrder
            : unitId is not null
                ? ExpenseOperationalScope.Unit
                : propertyId is not null
                    ? ExpenseOperationalScope.Property
                    : ExpenseOperationalScope.Portfolio);

    private static ExpenseOperationalScope ResolveUpdatedExpenseOperationalScope(
        Expense entity,
        UpdateExpenseRequest request)
    {
        if (request.OperationalScope.HasValue)
            return request.OperationalScope.Value;
        if (request.WorkOrderId.HasValue)
            return ExpenseOperationalScope.WorkOrder;
        if (request.UnitId.HasValue && entity.OperationalScope != ExpenseOperationalScope.WorkOrder)
            return ExpenseOperationalScope.Unit;
        if (request.PropertyId.HasValue &&
            entity.OperationalScope == ExpenseOperationalScope.Portfolio)
            return ExpenseOperationalScope.Property;
        return entity.OperationalScope;
    }

    private static (int? PropertyId, int? UnitId, int? WorkOrderId)
        ResolveUpdatedExpenseReferenceIds(
            Expense entity,
            UpdateExpenseRequest request,
            ExpenseOperationalScope operationalScope) =>
        operationalScope switch
        {
            ExpenseOperationalScope.Portfolio => (null, null, null),
            ExpenseOperationalScope.Property =>
                (request.PropertyId ?? entity.PropertyId, null, null),
            ExpenseOperationalScope.Unit =>
                (request.PropertyId ?? entity.PropertyId,
                    request.UnitId ?? entity.UnitId, null),
            ExpenseOperationalScope.WorkOrder =>
                (request.PropertyId ?? entity.PropertyId,
                    request.UnitId ?? entity.UnitId,
                    request.WorkOrderId ?? entity.WorkOrderId),
            _ => throw new InvalidOperationException("Unsupported expense operational scope."),
        };

    private static void ValidateExpenseAllocations(
        IReadOnlyList<ExpenseAllocationRequest> allocations,
        decimal expenseAmount)
    {
        decimal total = 0m;
        foreach (var allocation in allocations)
        {
            if (allocation.Amount <= 0m)
                throw new InvalidOperationException("Expense allocations must be positive.");
            var validTarget = allocation.TargetKind switch
            {
                ExpenseAllocationTargetKind.Property =>
                    allocation.PropertyId is not null &&
                    allocation.UnitId is null && allocation.OwnerEntityId is null,
                ExpenseAllocationTargetKind.Unit =>
                    allocation.PropertyId is null &&
                    allocation.UnitId is not null && allocation.OwnerEntityId is null,
                ExpenseAllocationTargetKind.OwnerEntity =>
                    allocation.PropertyId is null &&
                    allocation.UnitId is null && allocation.OwnerEntityId is not null,
                _ => false,
            };
            if (!validTarget)
                throw new InvalidOperationException(
                    "Each expense allocation must name exactly one target matching its target kind.");
            total += allocation.Amount;
        }

        if (allocations.Count > 0 && total != expenseAmount)
            throw new InvalidOperationException(
                "A nonempty expense allocation set must exactly equal the expense amount.");
    }

    private static ExpenseAllocation NewExpenseAllocation(
        ExpenseAllocationRequest request,
        int portfolioId,
        DateTime now) => new()
    {
        PortfolioId = portfolioId,
        TargetKind = request.TargetKind,
        PropertyId = request.PropertyId,
        UnitId = request.UnitId,
        OwnerEntityId = request.OwnerEntityId,
        Amount = request.Amount,
        CreatedAt = now,
    };

    private sealed record ExpenseReferenceContext(
        ExpenseOperationalScope OperationalScope,
        int? PropertyId,
        int? UnitId,
        int? WorkOrderId);

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
            (propertyId == null || persistence.Query<PropertyOwnership>().Any(ownership =>
                ownership.PropertyId == propertyId
                && ownership.PortfolioId == portfolioId
                && ownership.OwnerEntityId == ownerId
                && ownership.EffectiveToUtc == null)), ct);

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
    private static string? Normalize(string? value)
    {
        var trimmed = value?.Trim();
        return string.IsNullOrEmpty(trimmed) ? null : trimmed;
    }
    private static AtomicMoneyMutationResult Missing() => new(false, false, 0);
    private static AtomicMoneyMutationResult Applied(int id, string? responseJson = null) =>
        new(true, true, id, responseJson);
    private static CapabilityAuthorizationTargetKind RequiredTargetKind(AtomicMoneyMutationCommand command) =>
        command.Domain == AtomicMoneyDomain.OwnerDistribution
            ? CapabilityAuthorizationTargetKind.Workspace
            : CapabilityAuthorizationTargetKind.Property;
    private static UnauthorizedAccessException Denied(string message) => new(message);
}

public static class AtomicMoneyMutation
{
    public static readonly AtomicJsonResultCodec<AtomicMoneyMutationResult> Codec =
        new("money.scoped-mutation.v2");

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
