using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using RentalCommand.Api.DTOs;
using RentalCommand.Core;
using RentalCommand.Core.Atomic;
using RentalCommand.Core.Authorization;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Money;
using RentalCommand.Api.Services;
using RentalCommand.Data;
using RentalCommand.Data.Accounting;

namespace RentalCommand.Api.Services.Domain;

public sealed class AtomicMoneyMutationHandler
    : IAtomicCommandHandler<AtomicMoneyMutationCommand, AtomicMoneyMutationResult>
{
    private readonly RentalCommandDbContext _db;

    public AtomicMoneyMutationHandler(RentalCommandDbContext db) => _db = db;

    public async Task<AtomicMoneyMutationResult> HandleAsync(
        AtomicMoneyMutationCommand command, IAtomicCommandContext attempt, CancellationToken ct)
    {
        Validate(command);
        // Authority mutations use this same lock. Once acquired, the exact revision proven below
        // cannot change before this transaction's business write commits or rolls back.
        await attempt.AcquireLockAsync("AuthSession", command.AuthSessionId, ct);
        await attempt.AcquireLockAsync(
            "WorkspaceAccessContext", command.AccessContextId, ct);
        await attempt.AcquireLockAsync("Portfolio", command.PortfolioId, ct);
        var times = await AtomicCommandDbClock.ReadCommandTimesAsync(_db, command.PortfolioId, ct);
        var securityNowUtc = times.WallClockUtc;
        var businessNowUtc = command.BusinessNowUtc;
        var businessDateUtc = times.BusinessDate.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
        attempt.UseDatabaseWallClockForAudit(businessNowUtc);
        if (!await LiveAssignments(command, _db, securityNowUtc).AnyAsync(ct))
            throw Denied("Your workspace access changed. Refresh and try again.");
        if (RequiresDestructiveDisbursementAuthority(command) &&
            !await HasDestructiveDisbursementAuthorityAsync(command, _db, securityNowUtc, ct))
        {
            throw Denied("Destructive disbursement authority is required.");
        }

        return command.Domain switch
        {
            AtomicMoneyDomain.Expense => await MutateExpenseAsync(
                command, attempt, securityNowUtc, businessNowUtc, ct),
            AtomicMoneyDomain.RecurringExpense => await MutateRecurringExpenseAsync(
                command, attempt, securityNowUtc, businessNowUtc, ct),
            AtomicMoneyDomain.Loan => await MutateLoanAsync(
                command, attempt, securityNowUtc, businessNowUtc, businessDateUtc, ct),
            AtomicMoneyDomain.OwnerDistribution => await MutateDistributionAsync(
                command, attempt, securityNowUtc, businessNowUtc, businessDateUtc, ct),
            AtomicMoneyDomain.OwnerContribution => await MutateContributionAsync(
                command, attempt, securityNowUtc, businessNowUtc, businessDateUtc, ct),
            AtomicMoneyDomain.CapitalAsset => await MutateCapitalAssetAsync(
                command, attempt, securityNowUtc, businessNowUtc, ct),
            AtomicMoneyDomain.PropertyDisposition => await MutatePropertyDispositionAsync(
                command, attempt, securityNowUtc, businessNowUtc, ct),
            _ => throw new ArgumentOutOfRangeException(nameof(command.Domain)),
        };
    }

    public async Task AuthorizeReplayAsync(
        AtomicMoneyMutationCommand command, IAtomicCommandContext context, CancellationToken ct)
    {
        Validate(command);
        var securityNowUtc = await context.ReadDatabaseClockUtcAsync(ct);
        if (!await HasReplayAuthorityAsync(command, _db, securityNowUtc, ct))
            throw Denied("Your workspace access changed. Refresh and try again.");
    }

    private async Task<bool> HasReplayAuthorityAsync(
        AtomicMoneyMutationCommand command,
        RentalCommandDbContext db,
        DateTime securityNowUtc,
        CancellationToken ct)
    {
        if (command.Domain is AtomicMoneyDomain.OwnerDistribution or AtomicMoneyDomain.OwnerContribution)
        {
            return await HasWorkspaceAuthorityAsync(command, db, securityNowUtc, ct) &&
                (!RequiresDestructiveDisbursementAuthority(command) ||
                 await HasDestructiveDisbursementAuthorityAsync(command, db, securityNowUtc, ct));
        }

        if (command.Domain == AtomicMoneyDomain.Expense)
        {
            if (command.Operation == AtomicMoneyOperation.Create)
            {
                var request = Read<CreateExpenseRequest>(command);
                return await HasPropertyAuthorityAsync(command, db, securityNowUtc,
                    request.PropertyId, request.UnitId, request.WorkOrderId, ct);
            }

            var target = await db.Set<Expense>().IgnoreQueryFilters()
                .Where(row => row.Id == command.EntityId && row.PortfolioId == command.PortfolioId)
                .Select(row => new { row.PropertyId, row.UnitId, row.WorkOrderId })
                .SingleOrDefaultAsync(ct);
            return target is not null && await HasPropertyAuthorityAsync(command, db, securityNowUtc,
                target.PropertyId, target.UnitId, target.WorkOrderId, ct);
        }

        if (command.Domain == AtomicMoneyDomain.RecurringExpense)
        {
            if (command.Operation == AtomicMoneyOperation.Create)
            {
                var request = Read<CreateRecurringExpenseRequest>(command);
                return await HasPropertyAuthorityAsync(command, db, securityNowUtc,
                    request.PropertyId, request.UnitId, null, ct);
            }

            var target = await db.Set<RecurringExpense>().IgnoreQueryFilters()
                .Where(row => row.Id == command.EntityId && row.PortfolioId == command.PortfolioId)
                .Select(row => new { row.PropertyId, row.UnitId })
                .SingleOrDefaultAsync(ct);
            return target is not null && await HasPropertyAuthorityAsync(command, db, securityNowUtc,
                target.PropertyId, target.UnitId, null, ct);
        }

        if (command.Domain == AtomicMoneyDomain.CapitalAsset)
        {
            if (command.Operation == AtomicMoneyOperation.Create)
            {
                var request = Read<CreateCapitalAssetRequest>(command);
                return await HasPropertyAuthorityAsync(command, db, securityNowUtc,
                    request.PropertyId, request.UnitId, null, ct);
            }

            if (command.Operation == AtomicMoneyOperation.CapitalizeExpense)
            {
                var target = await db.Set<Expense>().IgnoreQueryFilters()
                    .Where(row => row.Id == command.EntityId && row.PortfolioId == command.PortfolioId)
                    .Select(row => new { row.PropertyId, row.UnitId })
                    .SingleOrDefaultAsync(ct);
                return target is not null && await HasPropertyAuthorityAsync(command, db, securityNowUtc,
                    target.PropertyId, target.UnitId, null, ct);
            }

            var assetTarget = await db.Set<CapitalAsset>().IgnoreQueryFilters()
                .Where(row => row.Id == command.EntityId && row.PortfolioId == command.PortfolioId)
                .Select(row => new { row.PropertyId, row.UnitId })
                .SingleOrDefaultAsync(ct);
            return assetTarget is not null && await HasPropertyAuthorityAsync(command, db, securityNowUtc,
                assetTarget.PropertyId, assetTarget.UnitId, null, ct);
        }

        if (command.Domain == AtomicMoneyDomain.PropertyDisposition)
        {
            var target = await db.Set<PropertyDisposition>().IgnoreQueryFilters()
                .Where(row => row.Id == command.EntityId && row.PortfolioId == command.PortfolioId)
                .Select(row => new { row.PropertyId })
                .SingleOrDefaultAsync(ct);
            return target is not null && await HasPropertyAuthorityAsync(command, db, securityNowUtc,
                target.PropertyId, null, null, ct);
        }

        if (command.Operation == AtomicMoneyOperation.PostPayment)
        {
            var request = Read<PostLoanPaymentRequest>(command);
            var paymentTarget = await db.Set<LoanPayment>()
                .Where(row =>
                    row.Id == command.EntityId &&
                    row.LoanId == request.LoanId &&
                    row.PortfolioId == command.PortfolioId)
                .Select(row => new { row.Loan!.PropertyId })
                .SingleOrDefaultAsync(ct);
            return paymentTarget is not null && await HasPropertyAuthorityAsync(command, db, securityNowUtc,
                paymentTarget.PropertyId, null, null, ct);
        }

        if (command.Operation == AtomicMoneyOperation.Create)
        {
            var request = Read<CreateLoanRequest>(command);
            return await HasPropertyAuthorityAsync(command, db, securityNowUtc,
                request.PropertyId, null, null, ct);
        }

        var loanTarget = await db.Set<Loan>().IgnoreQueryFilters()
            .Where(row => row.Id == command.EntityId && row.PortfolioId == command.PortfolioId)
            .Select(row => new { row.PropertyId })
            .SingleOrDefaultAsync(ct);
        return loanTarget is not null && await HasPropertyAuthorityAsync(command, db, securityNowUtc,
            loanTarget.PropertyId, null, null, ct);
    }

    private async Task<AtomicMoneyMutationResult> MutateExpenseAsync(
        AtomicMoneyMutationCommand command,
        IAtomicCommandContext attempt,
        DateTime securityNowUtc,
        DateTime businessNowUtc,
        CancellationToken ct)
    {
        var db = _db;
        Expense? entity = null;
        if (command.Operation != AtomicMoneyOperation.Create)
        {
            IQueryable<Expense> query = db.Set<Expense>();
            if (command.Operation == AtomicMoneyOperation.Update)
                query = query.Include(row => row.LineItems).Include(row => row.Allocations);
            entity = await query.SingleOrDefaultAsync(
                row => row.Id == command.EntityId && row.PortfolioId == command.PortfolioId && row.DeletedAt == null, ct);
            if (entity is null) return Missing();
            if (!await HasPropertyAuthorityAsync(command, db, securityNowUtc,
                    entity.PropertyId, entity.UnitId, entity.WorkOrderId, ct))
                return Missing();
        }
        var previousStatus = entity?.Status;

        if (command.Operation == AtomicMoneyOperation.Delete)
        {
            entity!.DeletedAt = businessNowUtc;
            entity.UpdatedAt = businessNowUtc;
            attempt.BindSemanticAudit(entity, Audit(command, nameof(Expense), AuditLogOperation.Deleted,
                $"Expense {entity.Id} deleted"));
            await attempt.FlushBusinessAsync(ct);
            await MoneyAccountingPosting.ReverseExpenseJournalsAsync(
                db, attempt, entity, command.ActorUserId, ct);
            StageDataUpdate(attempt, command, nameof(Expense), entity.Id, businessNowUtc, deleted: true);
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
                request.VendorId, request.WorkOrderId, request.Allocations, db, ct);
            if (references is null) return Missing();
            if (!await HasPropertyAuthorityAsync(command, db, securityNowUtc,
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
                CardLast4 = request.CardLast4, DocumentKind = request.DocumentKind,
                CreatedAt = businessNowUtc, UpdatedAt = businessNowUtc,
            };
            foreach (var line in request.LineItems)
                entity.LineItems.Add(new ExpenseLineItem
                {
                    Description = line.Description ?? string.Empty, Quantity = line.Quantity,
                    UnitPrice = line.UnitPrice, Amount = line.Amount, LineNumber = line.LineNumber,
                });
            foreach (var allocation in request.Allocations)
                entity.Allocations.Add(NewExpenseAllocation(
                    allocation, command.PortfolioId, businessNowUtc));
            db.Add(entity);
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
                db, ct);
            if (references is null) return Missing();
            if (!await HasPropertyAuthorityAsync(command, db, securityNowUtc,
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
                foreach (var existing in entity!.LineItems.ToArray()) db.Remove(existing);
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
                foreach (var existing in entity!.Allocations.ToArray()) db.Remove(existing);
                entity.Allocations.Clear();
                foreach (var allocation in request.Allocations)
                    entity.Allocations.Add(NewExpenseAllocation(
                        allocation, command.PortfolioId, businessNowUtc));
            }
            entity!.UpdatedAt = businessNowUtc;
            attempt.BindSemanticAudit(entity, Audit(command, nameof(Expense), AuditLogOperation.Updated,
                $"Expense {entity.Id} updated"));
        }

        await attempt.FlushBusinessAsync(ct);
        if (command.Operation == AtomicMoneyOperation.Create)
        {
            await MoneyAccountingPosting.PostExpenseOccurrenceAsync(
                db, attempt, entity!, command.ActorUserId, ct);
        }
        else if (!await MoneyAccountingPosting.CorrectExpenseAsync(
                     db, attempt, entity!, command.ActorUserId, ct)
                 && previousStatus is ExpenseStatus.Pending or ExpenseStatus.Approved
                 && entity!.Status == ExpenseStatus.Paid)
        {
            await MoneyAccountingPosting.PostBillPaymentAsync(
                db, attempt, entity, entity.Id, $"bill-payment:expense:{entity.Id}",
                command.ActorUserId, ct);
        }
        var responseJson = await SnapshotExpenseAsync(entity!.Id, command.PortfolioId, db, ct);
        StageDataUpdate(
            attempt, command, nameof(Expense), entity.Id, businessNowUtc, responseJson: responseJson);
        return Applied(entity.Id, responseJson);
    }

    private async Task<AtomicMoneyMutationResult> MutateRecurringExpenseAsync(
        AtomicMoneyMutationCommand command,
        IAtomicCommandContext attempt,
        DateTime securityNowUtc,
        DateTime businessNowUtc,
        CancellationToken ct)
    {
        var db = _db;
        var entity = command.Operation == AtomicMoneyOperation.Create ? null :
            await db.Set<RecurringExpense>().SingleOrDefaultAsync(row =>
                row.Id == command.EntityId && row.PortfolioId == command.PortfolioId && row.DeletedAt == null, ct);
        if (command.Operation != AtomicMoneyOperation.Create && entity is null) return Missing();
        if (entity is not null && !await HasPropertyAuthorityAsync(
                command, db, securityNowUtc, entity.PropertyId, entity.UnitId, null, ct))
            return Missing();
        if (command.Operation == AtomicMoneyOperation.Delete)
        {
            entity!.DeletedAt = businessNowUtc;
            entity.UpdatedAt = businessNowUtc;
            attempt.BindSemanticAudit(entity, Audit(command, nameof(RecurringExpense), AuditLogOperation.Deleted,
                $"Recurring expense {entity.Id} deleted"));
            await attempt.FlushBusinessAsync(ct);
            StageDataUpdate(
                attempt, command, nameof(RecurringExpense), entity.Id, businessNowUtc, deleted: true);
            return Applied(entity.Id);
        }
        if (command.Operation == AtomicMoneyOperation.Create)
        {
            var request = Read<CreateRecurringExpenseRequest>(command);
            if (!await PropertyUnitReferencesExistAsync(
                    command.PortfolioId, request.PropertyId, request.UnitId, db, ct)) return Missing();
            if (!await HasPropertyAuthorityAsync(command, db, securityNowUtc,
                    request.PropertyId, request.UnitId, null, ct))
                return Missing();
            var start = Utc(request.StartDate);
            entity = new RecurringExpense
            {
                PortfolioId = command.PortfolioId, PropertyId = request.PropertyId, UnitId = request.UnitId,
                Category = request.Category, Description = request.Description, Amount = request.Amount,
                Frequency = request.Frequency, StartDate = start, NextRunDate = Utc(request.NextRunDate) ?? start,
                Active = request.Active, Notes = request.Notes,
                CreatedAt = businessNowUtc, UpdatedAt = businessNowUtc,
            };
            db.Add(entity);
            attempt.BindSemanticAudit(entity, Audit(command, nameof(RecurringExpense), AuditLogOperation.Created,
                $"Recurring expense {entity.Description} created", entityId: 0));
        }
        else
        {
            var request = Read<UpdateRecurringExpenseRequest>(command);
            var effectiveProperty = request.PropertyId ?? entity!.PropertyId;
            var effectiveUnit = request.UnitId ?? entity.UnitId;
            if (!await PropertyUnitReferencesExistAsync(
                    command.PortfolioId, effectiveProperty, effectiveUnit, db, ct, effectiveProperty)) return Missing();
            if (!await HasPropertyAuthorityAsync(command, db, securityNowUtc,
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
            entity!.UpdatedAt = businessNowUtc;
            attempt.BindSemanticAudit(entity, Audit(command, nameof(RecurringExpense), AuditLogOperation.Updated,
                $"Recurring expense {entity.Id} updated"));
        }
        await attempt.FlushBusinessAsync(ct);
        var responseJson = await SnapshotRecurringExpenseAsync(
            entity!.Id, command.PortfolioId, db, ct);
        StageDataUpdate(
            attempt, command, nameof(RecurringExpense), entity.Id, businessNowUtc,
            responseJson: responseJson);
        return Applied(entity.Id, responseJson);
    }

    private async Task<AtomicMoneyMutationResult> MutateLoanAsync(
        AtomicMoneyMutationCommand command,
        IAtomicCommandContext attempt,
        DateTime securityNowUtc,
        DateTime businessNowUtc,
        DateTime businessDateUtc,
        CancellationToken ct)
    {
        var db = _db;
        if (command.Operation == AtomicMoneyOperation.PostPayment)
        {
            var request = Read<PostLoanPaymentRequest>(command);
            var lockedPaymentId = await db.Database.SqlQuery<int>($"""
                SELECT payment."Id" AS "Value"
                FROM "LoanPayments" AS payment
                WHERE payment."Id" = {command.EntityId}
                  AND payment."LoanId" = {request.LoanId}
                  AND payment."PortfolioId" = {command.PortfolioId}
                FOR UPDATE
                """).SingleOrDefaultAsync(ct);
            if (lockedPaymentId == 0) return Missing();

            var payment = await db.Set<LoanPayment>()
                .Include(row => row.Loan)
                .SingleOrDefaultAsync(row =>
                    row.Id == lockedPaymentId &&
                    row.LoanId == request.LoanId &&
                    row.PortfolioId == command.PortfolioId &&
                    row.Loan != null &&
                    row.Loan.DeletedAt == null, ct);
            if (payment?.Loan is null) return Missing();
            if (!await HasPropertyAuthorityAsync(
                    command, db, securityNowUtc, payment.Loan.PropertyId, null, null, ct))
                return Missing();

            var effective = await LoanPaymentEffectiveQuery.From(db)
                .SingleAsync(row => row.Id == payment.Id && row.PortfolioId == command.PortfolioId, ct);
            if (effective.Status != LoanPaymentStatus.Paid)
            {
                var hasEarlierUnpaid = await LoanPaymentEffectiveQuery.From(db)
                    .AnyAsync(row =>
                        row.LoanId == payment.LoanId &&
                        row.PortfolioId == command.PortfolioId &&
                        (row.DueDate < effective.DueDate ||
                         (row.DueDate == effective.DueDate && row.Id < payment.Id)) &&
                        row.Status != LoanPaymentStatus.Paid, ct);
                if (hasEarlierUnpaid)
                    throw new InvalidOperationException("Earlier scheduled loan payments must be posted first.");

                var paidDate = Utc(request.PaidDate ?? businessDateUtc);
                var correction = new LoanPaymentCorrection
                {
                    PortfolioId = payment.PortfolioId,
                    LoanPaymentId = payment.Id,
                    AttemptId = attempt.AttemptId,
                    DueDate = effective.DueDate,
                    PaidDate = paidDate,
                    InterestAmount = effective.InterestAmount,
                    PrincipalAmount = effective.PrincipalAmount,
                    EscrowAmount = effective.EscrowAmount,
                    TotalAmount = effective.TotalAmount,
                    BalanceAfter = effective.BalanceAfter,
                    Status = LoanPaymentStatus.Paid,
                    PaymentDoesNotCoverInterest = effective.PaymentDoesNotCoverInterest,
                    CreatedAtUtc = businessNowUtc,
                };
                db.Add(correction);
                payment.Loan.CurrentBalance = effective.BalanceAfter;
                payment.Loan.UpdatedAt = businessNowUtc;
                if (effective.BalanceAfter == 0m)
                    payment.Loan.Status = LoanStatus.PaidOff;

                attempt.BindSemanticAudit(correction, Audit(command, nameof(LoanPaymentCorrection),
                    AuditLogOperation.Created, $"Loan payment {payment.Id} effective snapshot posted", 0));
                attempt.BindSemanticAudit(payment.Loan, Audit(command, nameof(Loan),
                    AuditLogOperation.Updated, $"Loan payment {payment.Id} reduced the live balance",
                    payment.Loan.Id));
                await attempt.FlushBusinessAsync(ct);
            }

            // The effective row includes the latest correction.  Build a detached posting
            // snapshot so a corrected payment uses its corrected principal, interest, escrow,
            // and total rather than the original scheduled split.
            var postingPayment = new LoanPayment
            {
                Id = payment.Id,
                PortfolioId = payment.PortfolioId,
                LoanId = payment.LoanId,
                DueDate = effective.DueDate,
                PaidDate = effective.PaidDate,
                InterestAmount = effective.InterestAmount,
                PrincipalAmount = effective.PrincipalAmount,
                EscrowAmount = effective.EscrowAmount,
                TotalAmount = effective.TotalAmount,
                BalanceAfter = effective.BalanceAfter,
                Status = effective.Status,
                Loan = payment.Loan,
            };
            await MoneyAccountingPosting.PostLoanPaymentAsync(
                db,
                attempt,
                postingPayment,
                command.ActorUserId,
                ct,
                sourceId: effective.CorrectionId ?? payment.Id,
                sourceBusinessKey: effective.CorrectionId is { } correctionId
                    ? $"loan-payment-correction:{correctionId}"
                    : $"loan-payment:{payment.Id}");

            var paymentResponseJson = await SnapshotLoanPaymentAsync(
                payment.Id, command.PortfolioId, db, ct);
            StageDataUpdate(attempt, command, nameof(LoanPayment), payment.Id, businessNowUtc,
                responseJson: paymentResponseJson);
            StageDataUpdate(attempt, command, nameof(Loan), payment.Loan.Id, businessNowUtc);
            return Applied(payment.Id, paymentResponseJson);
        }

        var entity = command.Operation == AtomicMoneyOperation.Create ? null :
            await db.Set<Loan>().SingleOrDefaultAsync(row =>
                row.Id == command.EntityId && row.PortfolioId == command.PortfolioId && row.DeletedAt == null, ct);
        if (command.Operation != AtomicMoneyOperation.Create && entity is null) return Missing();
        if (entity is not null && !await HasPropertyAuthorityAsync(
                command, db, securityNowUtc, entity.PropertyId, null, null, ct))
            return Missing();
        if (command.Operation == AtomicMoneyOperation.Delete)
        {
            entity!.DeletedAt = businessNowUtc;
            entity.UpdatedAt = businessNowUtc;
            attempt.BindSemanticAudit(entity, Audit(command, nameof(Loan), AuditLogOperation.Deleted,
                $"Loan {entity.Id} deleted"));
            await attempt.FlushBusinessAsync(ct);
            StageDataUpdate(attempt, command, nameof(Loan), entity.Id, businessNowUtc, deleted: true);
            return Applied(entity.Id);
        }
        if (command.Operation == AtomicMoneyOperation.Create)
        {
            var request = Read<CreateLoanRequest>(command);
            if (!await db.Set<Property>().AnyAsync(
                    row => row.Id == request.PropertyId && row.PortfolioId == command.PortfolioId, ct)) return Missing();
            if (!await HasPropertyAuthorityAsync(
                    command, db, securityNowUtc, request.PropertyId, null, null, ct))
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
                Status = request.Status, Notes = request.Notes,
                CreatedAt = businessNowUtc, UpdatedAt = businessNowUtc,
            };
            db.Add(entity);
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
            entity!.UpdatedAt = businessNowUtc;
            attempt.BindSemanticAudit(entity, Audit(command, nameof(Loan), AuditLogOperation.Updated,
                $"Loan {entity.Id} updated"));
        }
        await attempt.FlushBusinessAsync(ct);
        var responseJson = await SnapshotLoanAsync(entity!.Id, command.PortfolioId, db, ct);
        StageDataUpdate(
            attempt, command, nameof(Loan), entity.Id, businessNowUtc, responseJson: responseJson);
        return Applied(entity.Id, responseJson);
    }

    private async Task<AtomicMoneyMutationResult> MutateCapitalAssetAsync(
        AtomicMoneyMutationCommand command,
        IAtomicCommandContext attempt,
        DateTime securityNowUtc,
        DateTime businessNowUtc,
        CancellationToken ct)
    {
        var db = _db;

        if (command.Operation == AtomicMoneyOperation.CapitalizeExpense)
        {
            var expense = await db.Set<Expense>().SingleOrDefaultAsync(row =>
                row.Id == command.EntityId && row.PortfolioId == command.PortfolioId
                && row.DeletedAt == null, ct);
            if (expense?.PropertyId is not int propertyId || expense.CapitalizedAssetId is not null)
                return Missing();
            if (!await HasPropertyAuthorityAsync(command, db, securityNowUtc,
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
                CreatedAt = businessNowUtc,
                UpdatedAt = businessNowUtc,
            };
            db.Add(asset);
            attempt.BindSemanticAudit(asset, Audit(command, nameof(CapitalAsset),
                AuditLogOperation.Created, $"Expense {expense.Id} capitalized", entityId: 0));
            await attempt.FlushBusinessAsync(ct);

            expense.CapitalizedAssetId = asset.Id;
            expense.UpdatedAt = businessNowUtc;
            attempt.BindSemanticAudit(expense, Audit(command, nameof(Expense),
                AuditLogOperation.Updated, $"Expense {expense.Id} linked to capital asset {asset.Id}", expense.Id));
            await attempt.FlushBusinessAsync(ct);
            await MoneyAccountingPosting.PostCapitalPurchaseAsync(
                db, attempt, asset, command.ActorUserId, ct);

            StageDataUpdate(attempt, command, nameof(CapitalAsset), asset.Id, businessNowUtc);
            var expenseJson = await SnapshotExpenseAsync(expense.Id, command.PortfolioId, db, ct);
            StageDataUpdate(
                attempt, command, nameof(Expense), expense.Id, businessNowUtc,
                responseJson: expenseJson);
            return Applied(asset.Id);
        }

        CapitalAsset? entity = null;
        var capitalPurchaseFactsChanged = false;
        if (command.Operation != AtomicMoneyOperation.Create)
        {
            entity = await db.Set<CapitalAsset>().SingleOrDefaultAsync(row =>
                row.Id == command.EntityId && row.PortfolioId == command.PortfolioId
                && row.DeletedAt == null, ct);
            if (entity is null) return Missing();
            if (!await HasPropertyAuthorityAsync(command, db, securityNowUtc,
                    entity.PropertyId, entity.UnitId, null, ct))
                return Missing();
        }

        if (command.Operation == AtomicMoneyOperation.Delete)
        {
            Expense? sourceExpense = null;
            if (entity!.SourceExpenseId is int sourceExpenseId)
            {
                sourceExpense = await db.Set<Expense>().IgnoreQueryFilters()
                    .SingleOrDefaultAsync(row => row.Id == sourceExpenseId
                        && row.PortfolioId == command.PortfolioId
                        && row.DeletedAt == null
                        && row.CapitalizedAssetId == entity.Id, ct);
                if (sourceExpense is not null)
                {
                    sourceExpense.CapitalizedAssetId = null;
                    sourceExpense.UpdatedAt = businessNowUtc;
                    attempt.BindSemanticAudit(sourceExpense, Audit(command, nameof(Expense),
                        AuditLogOperation.Updated, $"Capital asset {entity.Id} unlinked", sourceExpense.Id));
                }
            }

            await MoneyAccountingPosting.ReverseCapitalPurchaseAsync(
                db, attempt, entity, command.ActorUserId, ct);

            entity.DeletedAt = businessNowUtc;
            entity.UpdatedAt = businessNowUtc;
            attempt.BindSemanticAudit(entity, Audit(command, nameof(CapitalAsset),
                AuditLogOperation.Deleted, $"Capital asset {entity.Id} deleted"));
            await attempt.FlushBusinessAsync(ct);
            StageDataUpdate(
                attempt, command, nameof(CapitalAsset), entity.Id, businessNowUtc, deleted: true);
            if (sourceExpense is not null)
            {
                var expenseJson = await SnapshotExpenseAsync(
                    sourceExpense.Id, command.PortfolioId, db, ct);
                StageDataUpdate(attempt, command, nameof(Expense), sourceExpense.Id, businessNowUtc,
                    responseJson: expenseJson);
            }
            return Applied(entity.Id);
        }

        if (command.Operation == AtomicMoneyOperation.Create)
        {
            var request = Read<CreateCapitalAssetRequest>(command);
            if (!await PropertyUnitReferencesExistAsync(
                    command.PortfolioId, request.PropertyId, request.UnitId, db, ct))
                return Missing();
            if (!await HasPropertyAuthorityAsync(command, db, securityNowUtc,
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
                CreatedAt = businessNowUtc,
                UpdatedAt = businessNowUtc,
            };
            db.Add(entity);
            attempt.BindSemanticAudit(entity, Audit(command, nameof(CapitalAsset),
                AuditLogOperation.Created, $"Capital asset {entity.Description} created", entityId: 0));
        }
        else if (command.Operation == AtomicMoneyOperation.Update)
        {
            var request = Read<UpdateCapitalAssetRequest>(command);
            var propertyId = request.PropertyId ?? entity!.PropertyId;
            var unitId = request.ClearUnit == true ? null : request.UnitId ?? entity.UnitId;
            var inServiceDate = request.InServiceDate.HasValue
                ? Utc(request.InServiceDate.Value)
                : entity.InServiceDate;
            if (!await PropertyUnitReferencesExistAsync(
                    command.PortfolioId, propertyId, unitId, db, ct, propertyId))
                return Missing();
            if (!await HasPropertyAuthorityAsync(command, db, securityNowUtc,
                    propertyId, unitId, null, ct))
                return Missing();

            capitalPurchaseFactsChanged = propertyId != entity.PropertyId
                || unitId != entity.UnitId
                || inServiceDate != entity.InServiceDate
                || (request.CostBasis.HasValue && request.CostBasis.Value != entity.CostBasis);

            entity.PropertyId = propertyId;
            entity.UnitId = unitId;
            if (request.Description is not null) entity.Description = request.Description.Trim();
            if (request.CostBasis.HasValue) entity.CostBasis = request.CostBasis.Value;
            entity.InServiceDate = inServiceDate;
            if (request.Method.HasValue) entity.Method = request.Method.Value;
            if (request.RecoveryYears.HasValue) entity.RecoveryYears = request.RecoveryYears.Value;
            if (request.Convention.HasValue) entity.Convention = request.Convention.Value;
            if (request.AccumulatedDepreciation.HasValue)
                entity.AccumulatedDepreciation = request.AccumulatedDepreciation.Value;
            if (request.ClearDisposedOnDate == true) entity.DisposedOnDate = null;
            else if (request.DisposedOnDate.HasValue)
                entity.DisposedOnDate = Utc(request.DisposedOnDate.Value);
            entity.UpdatedAt = businessNowUtc;
            attempt.BindSemanticAudit(entity, Audit(command, nameof(CapitalAsset),
                AuditLogOperation.Updated, $"Capital asset {entity.Id} updated"));
        }
        else
        {
            throw new ArgumentException("Unsupported capital asset mutation operation.");
        }

        await attempt.FlushBusinessAsync(ct);
        if (command.Operation == AtomicMoneyOperation.Create)
        {
            await MoneyAccountingPosting.PostCapitalPurchaseAsync(
                db, attempt, entity!, command.ActorUserId, ct);
        }
        else if (capitalPurchaseFactsChanged)
        {
            await MoneyAccountingPosting.CorrectCapitalPurchaseAsync(
                db, attempt, entity!, command.ActorUserId, ct);
        }
        StageDataUpdate(attempt, command, nameof(CapitalAsset), entity!.Id, businessNowUtc);
        return Applied(entity.Id);
    }

    private async Task<AtomicMoneyMutationResult> MutatePropertyDispositionAsync(
        AtomicMoneyMutationCommand command,
        IAtomicCommandContext attempt,
        DateTime securityNowUtc,
        DateTime businessNowUtc,
        CancellationToken ct)
    {
        if (command.Operation is not (AtomicMoneyOperation.Update or AtomicMoneyOperation.Delete))
            throw new ArgumentException("Property disposition create uses its dedicated atomic command.");

        var db = _db;
        var entity = await db.Set<PropertyDisposition>().SingleOrDefaultAsync(row =>
            row.Id == command.EntityId && row.PortfolioId == command.PortfolioId
            && row.DeletedAt == null, ct);
        if (entity is null) return Missing();
        if (!await HasPropertyAuthorityAsync(command, db, securityNowUtc,
                entity.PropertyId, null, null, ct))
            return Missing();

        if (command.Operation == AtomicMoneyOperation.Delete)
        {
            entity.DeletedAt = businessNowUtc;
            entity.UpdatedAt = businessNowUtc;
            attempt.BindSemanticAudit(entity, Audit(command, nameof(PropertyDisposition),
                AuditLogOperation.Deleted, $"Property disposition {entity.Id} deleted"));
            await attempt.FlushBusinessAsync(ct);
            StageDataUpdate(
                attempt, command, nameof(PropertyDisposition), entity.Id, businessNowUtc, deleted: true);
            return Applied(entity.Id);
        }

        var request = Read<UpdatePropertyDispositionRequest>(command);
        if (request.ClosedOnDate.HasValue) entity.ClosedOnDate = Utc(request.ClosedOnDate.Value).Date;
        if (request.SalePrice.HasValue) entity.SalePrice = request.SalePrice.Value;
        if (request.SellingCosts.HasValue) entity.SellingCosts = request.SellingCosts.Value;
        if (request.BuyerName is not null) entity.BuyerName = Normalize(request.BuyerName);
        if (request.Memo is not null) entity.Memo = Normalize(request.Memo);
        entity.UpdatedAt = businessNowUtc;
        attempt.BindSemanticAudit(entity, Audit(command, nameof(PropertyDisposition),
            AuditLogOperation.Updated, $"Property disposition {entity.Id} updated"));
        await attempt.FlushBusinessAsync(ct);
        StageDataUpdate(attempt, command, nameof(PropertyDisposition), entity.Id, businessNowUtc);
        return Applied(entity.Id);
    }

    private async Task<AtomicMoneyMutationResult> MutateDistributionAsync(
        AtomicMoneyMutationCommand command,
        IAtomicCommandContext attempt,
        DateTime securityNowUtc,
        DateTime businessNowUtc,
        DateTime businessDateUtc,
        CancellationToken ct)
    {
        var db = _db;
        if (command.Operation == AtomicMoneyOperation.Create)
        {
            var create = Read<CreateOwnerDistributionRequest>(command);
            await attempt.AcquireLockAsync(
                "OwnerEntity", create.OwnerEntityId, ct);
        }
        else
        {
            await attempt.AcquireLockAsync(
                "OwnerDistribution", command.EntityId, ct);
        }
        var entity = command.Operation == AtomicMoneyOperation.Create ? null :
            await db.Set<OwnerDistribution>().SingleOrDefaultAsync(row =>
                row.Id == command.EntityId && row.PortfolioId == command.PortfolioId && row.DeletedAt == null, ct);
        if (command.Operation != AtomicMoneyOperation.Create && entity is null) return Missing();
        if (!await HasWorkspaceAuthorityAsync(command, db, securityNowUtc, ct))
            throw Denied("Owner distributions require workspace payout authority.");
        if (command.Operation == AtomicMoneyOperation.Delete)
        {
            if (entity!.Status != OwnerDistributionStatus.Draft)
                throw Conflict("Only a draft owner distribution can be deleted.");
            entity!.DeletedAt = businessNowUtc;
            entity.UpdatedAt = businessNowUtc;
            attempt.BindSemanticAudit(entity, Audit(command, nameof(OwnerDistribution),
                AuditLogOperation.Deleted, $"Owner distribution {entity.Id} deleted"));
            await attempt.FlushBusinessAsync(ct);
            StageDataUpdate(
                attempt, command, nameof(OwnerDistribution), entity.Id, businessNowUtc, deleted: true);
            return Applied(entity.Id);
        }
        if (command.Operation == AtomicMoneyOperation.Create)
        {
            var request = Read<CreateOwnerDistributionRequest>(command);
            if (!await DistributionReferencesExistAsync(
                    command.PortfolioId, request.OwnerEntityId, request.PropertyId, db, ct)) return Missing();
            entity = new OwnerDistribution
            {
                PortfolioId = command.PortfolioId, OwnerEntityId = request.OwnerEntityId,
                PropertyId = request.PropertyId, Date = Utc(request.Date), Amount = request.Amount,
                Method = request.Method, Status = OwnerDistributionStatus.Draft,
                Memo = Normalize(request.Memo),
                CreatedAt = businessNowUtc, UpdatedAt = businessNowUtc,
            };
            db.Add(entity);
            attempt.BindSemanticAudit(entity, Audit(command, nameof(OwnerDistribution),
                AuditLogOperation.Created, "Owner distribution draft created", entityId: 0));
        }
        else if (command.Operation == AtomicMoneyOperation.Approve)
        {
            var request = Read<ApproveOwnerDistributionRequest>(command);
            var current = entity!;
            if (current.Status != OwnerDistributionStatus.Draft)
                throw Conflict("Only a draft owner distribution can be approved.");
            var bankReference = Normalize(request.BankReference);
            var exportReference = Normalize(request.ExportReference);
            if (bankReference is null || exportReference is null)
                throw Conflict("Bank reference and export reference are required before approving a distribution.");
            current.Status = OwnerDistributionStatus.Approved;
            current.ApprovedAt = businessNowUtc;
            current.ApprovedBusinessDate = businessDateUtc;
            current.ApprovedByUserId = command.ActorUserId;
            current.BankReference = bankReference;
            current.ExportReference = exportReference;
            current.ExportedAt = Utc(request.ExportedAt) ?? businessNowUtc;
            current.UpdatedAt = businessNowUtc;
            attempt.BindSemanticAudit(current, Audit(command, nameof(OwnerDistribution),
                AuditLogOperation.Updated, $"Owner distribution {current.Id} approved"));
        }
        else if (command.Operation == AtomicMoneyOperation.Reject)
        {
            var request = Read<RejectOwnerDistributionRequest>(command);
            var current = entity!;
            if (current.Status != OwnerDistributionStatus.Draft)
                throw Conflict("Only a draft owner distribution can be rejected.");
            current.Status = OwnerDistributionStatus.Rejected;
            current.RejectedAt = businessNowUtc;
            current.RejectedByUserId = command.ActorUserId;
            current.RejectionReason = Normalize(request.Reason);
            current.UpdatedAt = businessNowUtc;
            attempt.BindSemanticAudit(current, Audit(command, nameof(OwnerDistribution),
                AuditLogOperation.Updated, $"Owner distribution {current.Id} rejected"));
        }
        else
        {
            var request = Read<UpdateOwnerDistributionRequest>(command);
            var current = entity!;
            if (current.Status != OwnerDistributionStatus.Draft)
                throw Conflict("Only a draft owner distribution can be edited.");
            var ownerId = request.OwnerEntityId ?? current.OwnerEntityId;
            await attempt.AcquireLockAsync("OwnerEntity", ownerId, ct);
            var propertyId = request.ClearProperty == true ? null : request.PropertyId ?? current.PropertyId;
            if (!await DistributionReferencesExistAsync(
                    command.PortfolioId, ownerId, propertyId, db, ct)) return Missing();
            current.OwnerEntityId = ownerId; current.PropertyId = propertyId;
            if (request.Date.HasValue) current.Date = Utc(request.Date.Value);
            if (request.Amount.HasValue) current.Amount = request.Amount.Value;
            if (request.Method.HasValue) current.Method = request.Method.Value;
            if (request.Memo is not null) current.Memo = Normalize(request.Memo);
            current.UpdatedAt = businessNowUtc;
            attempt.BindSemanticAudit(current, Audit(command, nameof(OwnerDistribution),
                AuditLogOperation.Updated, $"Owner distribution {current.Id} updated"));
        }
        await attempt.FlushBusinessAsync(ct);
        if (command.Operation == AtomicMoneyOperation.Approve)
        {
            await MoneyAccountingPosting.PostOwnerDistributionAsync(
                db, attempt, entity!, command.ActorUserId, ct);
        }
        var snapshot = await SnapshotOwnerDistributionAsync(
            entity!.Id, command.PortfolioId, db, ct);
        StageDataUpdate(
            attempt, command, nameof(OwnerDistribution), entity.Id, businessNowUtc, snapshot);
        return Applied(entity.Id, snapshot);
    }

    private async Task<AtomicMoneyMutationResult> MutateContributionAsync(
        AtomicMoneyMutationCommand command,
        IAtomicCommandContext attempt,
        DateTime securityNowUtc,
        DateTime businessNowUtc,
        DateTime businessDateUtc,
        CancellationToken ct)
    {
        var db = _db;
        if (command.Operation == AtomicMoneyOperation.Create)
        {
            var create = Read<CreateOwnerContributionRequest>(command);
            await attempt.AcquireLockAsync("OwnerEntity", create.OwnerEntityId, ct);
        }
        else
        {
            await attempt.AcquireLockAsync("OwnerContribution", command.EntityId, ct);
        }

        var entity = command.Operation == AtomicMoneyOperation.Create ? null :
            await db.Set<OwnerContribution>().SingleOrDefaultAsync(row =>
                row.Id == command.EntityId && row.PortfolioId == command.PortfolioId && row.DeletedAt == null,
                ct);
        if (command.Operation != AtomicMoneyOperation.Create && entity is null)
            return Missing();
        if (!await HasWorkspaceAuthorityAsync(command, db, securityNowUtc, ct))
            throw Denied("Owner contributions require workspace funding authority.");

        if (command.Operation == AtomicMoneyOperation.Delete)
        {
            if (entity!.Status != OwnerDistributionStatus.Draft)
                throw Conflict("Only a draft owner contribution can be deleted.");
            entity.DeletedAt = businessNowUtc;
            entity.UpdatedAt = businessNowUtc;
            attempt.BindSemanticAudit(entity, Audit(command, nameof(OwnerContribution),
                AuditLogOperation.Deleted, $"Owner contribution {entity.Id} deleted"));
            await attempt.FlushBusinessAsync(ct);
            StageDataUpdate(attempt, command, nameof(OwnerContribution), entity.Id, businessNowUtc, deleted: true);
            return Applied(entity.Id);
        }

        if (command.Operation == AtomicMoneyOperation.Create)
        {
            var request = Read<CreateOwnerContributionRequest>(command);
            if (!await DistributionReferencesExistAsync(
                    command.PortfolioId, request.OwnerEntityId, request.PropertyId, db, ct))
            {
                return Missing();
            }

            entity = new OwnerContribution
            {
                PortfolioId = command.PortfolioId,
                OwnerEntityId = request.OwnerEntityId,
                PropertyId = request.PropertyId,
                Date = Utc(request.Date),
                Amount = request.Amount,
                Method = request.Method,
                Status = OwnerDistributionStatus.Draft,
                Memo = Normalize(request.Memo),
                CreatedAt = businessNowUtc,
                UpdatedAt = businessNowUtc,
            };
            db.Add(entity);
            attempt.BindSemanticAudit(entity, Audit(command, nameof(OwnerContribution),
                AuditLogOperation.Created, "Owner contribution draft created", entityId: 0));
        }
        else if (command.Operation == AtomicMoneyOperation.Approve)
        {
            var request = Read<ApproveOwnerContributionRequest>(command);
            var current = entity!;
            if (current.Status != OwnerDistributionStatus.Draft)
                throw Conflict("Only a draft owner contribution can be approved.");
            var bankReference = Normalize(request.BankReference);
            var exportReference = Normalize(request.ExportReference);
            if (bankReference is null || exportReference is null)
            {
                throw Conflict(
                    "Bank reference and export reference are required before approving a contribution.");
            }

            current.Status = OwnerDistributionStatus.Approved;
            current.ApprovedAt = businessNowUtc;
            current.ApprovedBusinessDate = businessDateUtc;
            current.ApprovedByUserId = command.ActorUserId;
            current.BankReference = bankReference;
            current.ExportReference = exportReference;
            current.ExportedAt = Utc(request.ExportedAt) ?? businessNowUtc;
            current.UpdatedAt = businessNowUtc;
            attempt.BindSemanticAudit(current, Audit(command, nameof(OwnerContribution),
                AuditLogOperation.Updated, $"Owner contribution {current.Id} approved"));
        }
        else if (command.Operation == AtomicMoneyOperation.Reject)
        {
            var request = Read<RejectOwnerContributionRequest>(command);
            var current = entity!;
            if (current.Status != OwnerDistributionStatus.Draft)
                throw Conflict("Only a draft owner contribution can be rejected.");
            current.Status = OwnerDistributionStatus.Rejected;
            current.RejectedAt = businessNowUtc;
            current.RejectedByUserId = command.ActorUserId;
            current.RejectionReason = Normalize(request.Reason);
            current.UpdatedAt = businessNowUtc;
            attempt.BindSemanticAudit(current, Audit(command, nameof(OwnerContribution),
                AuditLogOperation.Updated, $"Owner contribution {current.Id} rejected"));
        }
        else if (command.Operation == AtomicMoneyOperation.Update)
        {
            var request = Read<UpdateOwnerContributionRequest>(command);
            var current = entity!;
            if (current.Status != OwnerDistributionStatus.Draft)
                throw Conflict("Only a draft owner contribution can be edited.");
            var ownerId = request.OwnerEntityId ?? current.OwnerEntityId;
            await attempt.AcquireLockAsync("OwnerEntity", ownerId, ct);
            var propertyId = request.ClearProperty == true ? null : request.PropertyId ?? current.PropertyId;
            if (!await DistributionReferencesExistAsync(
                    command.PortfolioId, ownerId, propertyId, db, ct))
            {
                return Missing();
            }

            current.OwnerEntityId = ownerId;
            current.PropertyId = propertyId;
            if (request.Date.HasValue) current.Date = Utc(request.Date.Value);
            if (request.Amount.HasValue) current.Amount = request.Amount.Value;
            if (request.Method.HasValue) current.Method = request.Method.Value;
            if (request.Memo is not null) current.Memo = Normalize(request.Memo);
            current.UpdatedAt = businessNowUtc;
            attempt.BindSemanticAudit(current, Audit(command, nameof(OwnerContribution),
                AuditLogOperation.Updated, $"Owner contribution {current.Id} updated"));
        }
        else
        {
            throw new ArgumentException("Unsupported owner contribution mutation operation.");
        }

        await attempt.FlushBusinessAsync(ct);
        if (command.Operation == AtomicMoneyOperation.Approve)
        {
            await MoneyAccountingPosting.PostOwnerContributionAsync(
                db, attempt, entity!, command.ActorUserId, ct);
        }
        var snapshot = await SnapshotOwnerContributionAsync(entity!.Id, command.PortfolioId, db, ct);
        StageDataUpdate(attempt, command, nameof(OwnerContribution), entity.Id, businessNowUtc, snapshot);
        return Applied(entity.Id, snapshot);
    }

    private async Task<string> SnapshotExpenseAsync(
        int entityId,
        int portfolioId,
        RentalCommandDbContext db,
        CancellationToken ct)
    {
        var response = await MoneyResponseProjection.ExpenseDetails(
                db.Set<Expense>().AsNoTracking().Where(expense =>
                    expense.Id == entityId && expense.PortfolioId == portfolioId),
                db.Set<StoredFile>().AsNoTracking())
            .SingleAsync(ct);
        return JsonSerializer.Serialize(response);
    }

    private async Task<string> SnapshotLoanAsync(
        int entityId,
        int portfolioId,
        RentalCommandDbContext db,
        CancellationToken ct)
    {
        var response = await MoneyResponseProjection.Loans(
                db.Set<Loan>().AsNoTracking().Where(loan =>
                    loan.Id == entityId && loan.PortfolioId == portfolioId))
            .SingleAsync(ct);
        return JsonSerializer.Serialize(response);
    }

    private async Task<string> SnapshotLoanPaymentAsync(
        int entityId,
        int portfolioId,
        RentalCommandDbContext db,
        CancellationToken ct)
    {
        var response = await LoanPaymentEffectiveQuery.From(db)
            .Where(payment => payment.Id == entityId && payment.PortfolioId == portfolioId)
            .Select(payment => new LoanPaymentResponse
            {
                Id = payment.Id,
                LoanId = payment.LoanId,
                PeriodKey = payment.PeriodKey,
                DueDate = payment.DueDate,
                PaidDate = payment.PaidDate,
                InterestAmount = payment.InterestAmount,
                PrincipalAmount = payment.PrincipalAmount,
                EscrowAmount = payment.EscrowAmount,
                TotalAmount = payment.TotalAmount,
                BalanceAfter = payment.BalanceAfter,
                Status = payment.Status,
                PaymentDoesNotCoverInterest = payment.PaymentDoesNotCoverInterest,
            })
            .SingleAsync(ct);
        return JsonSerializer.Serialize(response);
    }

    private async Task<string> SnapshotRecurringExpenseAsync(
        int entityId,
        int portfolioId,
        RentalCommandDbContext db,
        CancellationToken ct)
    {
        var response = await MoneyResponseProjection.RecurringExpenses(
                db.Set<RecurringExpense>().AsNoTracking().Where(expense =>
                    expense.Id == entityId && expense.PortfolioId == portfolioId))
            .SingleAsync(ct);
        return JsonSerializer.Serialize(response);
    }

    private async Task<string> SnapshotOwnerDistributionAsync(
        int entityId,
        int portfolioId,
        RentalCommandDbContext db,
        CancellationToken ct)
    {
        var response = await db.Set<OwnerDistribution>()
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
                Status = distribution.Status,
                ApprovedAt = distribution.ApprovedAt,
                ApprovedBusinessDate = distribution.ApprovedBusinessDate,
                ApprovedByUserId = distribution.ApprovedByUserId,
                RejectedAt = distribution.RejectedAt,
                RejectedByUserId = distribution.RejectedByUserId,
                RejectionReason = distribution.RejectionReason,
                BankReference = distribution.BankReference,
                ExportReference = distribution.ExportReference,
                ExportedAt = distribution.ExportedAt,
                Memo = distribution.Memo,
                CreatedAt = distribution.CreatedAt,
                UpdatedAt = distribution.UpdatedAt,
            })
            .SingleAsync(ct);
        return JsonSerializer.Serialize(response);
    }

    private async Task<string> SnapshotOwnerContributionAsync(
        int entityId,
        int portfolioId,
        RentalCommandDbContext db,
        CancellationToken ct)
    {
        var response = await db.Set<OwnerContribution>()
            .AsNoTracking()
            .Where(contribution =>
                contribution.Id == entityId && contribution.PortfolioId == portfolioId)
            .Select(contribution => new OwnerContributionResponse
            {
                Id = contribution.Id,
                PortfolioId = contribution.PortfolioId,
                OwnerEntityId = contribution.OwnerEntityId,
                OwnerName = contribution.OwnerEntity!.Name,
                PropertyId = contribution.PropertyId,
                PropertyName = contribution.Property == null ? null : contribution.Property.Name,
                Date = contribution.Date,
                Amount = contribution.Amount,
                Method = contribution.Method,
                Status = contribution.Status,
                ApprovedAt = contribution.ApprovedAt,
                ApprovedBusinessDate = contribution.ApprovedBusinessDate,
                ApprovedByUserId = contribution.ApprovedByUserId,
                RejectedAt = contribution.RejectedAt,
                RejectedByUserId = contribution.RejectedByUserId,
                RejectionReason = contribution.RejectionReason,
                BankReference = contribution.BankReference,
                ExportReference = contribution.ExportReference,
                ExportedAt = contribution.ExportedAt,
                Memo = contribution.Memo,
                CreatedAt = contribution.CreatedAt,
                UpdatedAt = contribution.UpdatedAt,
            })
            .SingleAsync(ct);
        return JsonSerializer.Serialize(response);
    }

    private void StageDataUpdate(
        IAtomicCommandContext attempt,
        AtomicMoneyMutationCommand command,
        string entityType,
        int entityId,
        DateTime businessNowUtc,
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
                $"{command.Domain}:{command.Operation}:{entityType}:{entityId}:{command.IdempotencyKey}:data-update",
            CreatedAtUtc = businessNowUtc,
            NextAttemptAtUtc = businessNowUtc,
        });
    }

    private AtomicSemanticAudit Audit(
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

    private IQueryable<Property> AuthorizedProperties(
        AtomicMoneyMutationCommand command,
        RentalCommandDbContext db,
        DateTime securityNowUtc)
    {
        var assignments = LiveAssignments(command, db, securityNowUtc);
        return db.Set<Property>().Where(property =>
            property.PortfolioId == command.PortfolioId && assignments.Any(assignment =>
                assignment.ScopeKind == MembershipRoleAssignmentScopeKind.AllProperties ||
                (assignment.ScopeKind == MembershipRoleAssignmentScopeKind.SelectedProperties &&
                 assignment.SelectedProperties.Any(scope =>
                     scope.PortfolioId == command.PortfolioId && scope.PropertyId == property.Id))));
    }

    private IQueryable<MembershipRoleAssignment> LiveAssignments(
        AtomicMoneyMutationCommand command,
        RentalCommandDbContext db,
        DateTime securityNowUtc)
    {
        var requiredTargetKind = RequiredTargetKind(command);
        return db.Set<MembershipRoleAssignment>().Where(assignment =>
            assignment.PortfolioId == command.PortfolioId &&
            assignment.Status == MembershipRoleAssignmentStatus.Active && assignment.SuspendedAtUtc == null &&
            assignment.RevokedAtUtc == null && assignment.EffectiveFromUtc <= securityNowUtc &&
            (assignment.EffectiveToUtc == null || assignment.EffectiveToUtc > securityNowUtc) &&
            assignment.WorkspaceMembership!.AccessContextId == command.AccessContextId &&
            assignment.WorkspaceMembership.PortfolioId == command.PortfolioId &&
            assignment.WorkspaceMembership.Status == WorkspaceMembershipStatus.Active &&
            assignment.WorkspaceMembership.SuspendedAtUtc == null && assignment.WorkspaceMembership.RevokedAtUtc == null &&
            assignment.WorkspaceMembership.EffectiveFromUtc <= securityNowUtc &&
            (assignment.WorkspaceMembership.EffectiveToUtc == null ||
             assignment.WorkspaceMembership.EffectiveToUtc > securityNowUtc) &&
            assignment.WorkspaceMembership.AccessContext!.UserId == command.ActorUserId &&
            assignment.WorkspaceMembership.AccessContext.PortfolioId == command.PortfolioId &&
            assignment.WorkspaceMembership.AccessContext.AccessRevision == command.ExpectedAccessRevision &&
            assignment.WorkspaceMembership.AccessContext.Status == WorkspaceAccessContextStatus.Active &&
            assignment.WorkspaceMembership.AccessContext.SuspendedAtUtc == null &&
            assignment.WorkspaceMembership.AccessContext.RevokedAtUtc == null &&
            db.Set<AuthSession>().Any(session =>
                session.Id == command.AuthSessionId && session.UserId == command.ActorUserId &&
                session.ActiveAccessContextId == command.AccessContextId && session.Status == AuthSessionStatus.Active &&
                session.RevokedAtUtc == null && session.ExpiresAtUtc > securityNowUtc) &&
            assignment.RoleProfile!.Capabilities.Any(capability =>
                capability.CapabilityDefinition!.Key == command.RequiredCapability &&
                capability.CapabilityDefinition.AuthorizationTargetKind == requiredTargetKind));
    }

    private Task<bool> HasWorkspaceAuthorityAsync(
        AtomicMoneyMutationCommand command,
        RentalCommandDbContext db,
        DateTime securityNowUtc,
        CancellationToken ct) =>
        LiveAssignments(command, db, securityNowUtc).AnyAsync(assignment =>
            assignment.ScopeKind == MembershipRoleAssignmentScopeKind.AllProperties &&
            assignment.RoleProfile!.Capabilities.Any(capability =>
                capability.CapabilityDefinition!.Key == command.RequiredCapability &&
                capability.CapabilityDefinition.AuthorizationTargetKind == CapabilityAuthorizationTargetKind.Workspace), ct);

    private bool RequiresDestructiveDisbursementAuthority(AtomicMoneyMutationCommand command) =>
        (command.Domain is AtomicMoneyDomain.OwnerDistribution or AtomicMoneyDomain.OwnerContribution) &&
        command.Operation == AtomicMoneyOperation.Delete;

    private Task<bool> HasDestructiveDisbursementAuthorityAsync(
        AtomicMoneyMutationCommand command,
        RentalCommandDbContext db,
        DateTime securityNowUtc,
        CancellationToken ct) =>
        LiveAssignments(command, db, securityNowUtc).AnyAsync(assignment =>
            assignment.ScopeKind == MembershipRoleAssignmentScopeKind.AllProperties &&
            assignment.RoleProfile!.Capabilities.Any(capability =>
                capability.CapabilityDefinition!.Key == CapabilityKeys.MoneyReconciliationDestructive &&
                capability.CapabilityDefinition.AuthorizationTargetKind ==
                    CapabilityAuthorizationTargetKind.Workspace), ct);

    private async Task<bool> HasPropertyAuthorityAsync(
        AtomicMoneyMutationCommand command,
        RentalCommandDbContext db,
        DateTime securityNowUtc,
        int? propertyId, int? unitId, int? workOrderId, CancellationToken ct)
    {
        if (propertyId is null && unitId is null && workOrderId is null)
            return await LiveAssignments(command, db, securityNowUtc).AnyAsync(assignment =>
                assignment.ScopeKind == MembershipRoleAssignmentScopeKind.AllProperties, ct);
        return await AuthorizedProperties(command, db, securityNowUtc).AnyAsync(property =>
            (propertyId == null || property.Id == propertyId) &&
            (unitId == null || db.Set<Unit>().Any(unit =>
                unit.Id == unitId && unit.PortfolioId == command.PortfolioId && unit.PropertyId == property.Id)) &&
            (workOrderId == null || db.Set<WorkOrder>().Any(order =>
                order.Id == workOrderId && order.PortfolioId == command.PortfolioId && order.PropertyId == property.Id)), ct);
    }

    private async Task<ExpenseReferenceContext?> ExpenseReferencesExistAsync(
        int portfolioId,
        ExpenseOperationalScope operationalScope,
        int? propertyId,
        int? unitId,
        int? vendorId,
        int? workOrderId,
        IReadOnlyList<ExpenseAllocationRequest>? allocations,
        RentalCommandDbContext db,
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
        var portfolio = db.Set<Portfolio>().Where(row =>
            row.Id == portfolioId &&
            (vendorId == null || db.Set<Vendor>().Any(candidate =>
                candidate.Id == vendorId && candidate.PortfolioId == portfolioId)) &&
            (propertyTargets.Length == 0 || db.Set<Property>().Count(candidate =>
                candidate.PortfolioId == portfolioId && propertyTargets.Contains(candidate.Id)) ==
                propertyTargets.Length) &&
            (unitTargets.Length == 0 || db.Set<Unit>().Count(candidate =>
                candidate.PortfolioId == portfolioId && unitTargets.Contains(candidate.Id)) ==
                unitTargets.Length) &&
            (ownerTargets.Length == 0 || db.Set<OwnerEntity>().Count(candidate =>
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
                await portfolio.SelectMany(_ => db.Set<Property>()
                        .Where(candidate =>
                            candidate.Id == propertyId && candidate.PortfolioId == portfolioId))
                    .Select(candidate => new ExpenseReferenceContext(
                        ExpenseOperationalScope.Property, candidate.Id, null, null))
                    .SingleOrDefaultAsync(ct),

            ExpenseOperationalScope.Unit when unitId is not null && workOrderId is null =>
                await portfolio.SelectMany(_ => db.Set<Unit>()
                        .Where(candidate =>
                            candidate.Id == unitId && candidate.PortfolioId == portfolioId &&
                            (propertyId == null || candidate.PropertyId == propertyId)))
                    .Select(candidate => new ExpenseReferenceContext(
                        ExpenseOperationalScope.Unit, candidate.PropertyId, candidate.Id, null))
                    .SingleOrDefaultAsync(ct),

            ExpenseOperationalScope.WorkOrder when workOrderId is not null =>
                await portfolio.SelectMany(_ => db.Set<WorkOrder>()
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

    private ExpenseOperationalScope ResolveExpenseOperationalScope(
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

    private ExpenseOperationalScope ResolveUpdatedExpenseOperationalScope(
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

    private (int? PropertyId, int? UnitId, int? WorkOrderId)
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

    private void ValidateExpenseAllocations(
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

    private ExpenseAllocation NewExpenseAllocation(
        ExpenseAllocationRequest request,
        int portfolioId,
        DateTime businessNowUtc) => new()
    {
        PortfolioId = portfolioId,
        TargetKind = request.TargetKind,
        PropertyId = request.PropertyId,
        UnitId = request.UnitId,
        OwnerEntityId = request.OwnerEntityId,
        Amount = request.Amount,
        CreatedAt = businessNowUtc,
    };

    private sealed record ExpenseReferenceContext(
        ExpenseOperationalScope OperationalScope,
        int? PropertyId,
        int? UnitId,
        int? WorkOrderId);

    private Task<bool> PropertyUnitReferencesExistAsync(
        int portfolioId, int? propertyId, int? unitId, RentalCommandDbContext db,
        CancellationToken ct, int? effectivePropertyId = null) =>
        db.Set<Portfolio>().AnyAsync(portfolio =>
            portfolio.Id == portfolioId &&
            (propertyId == null || db.Set<Property>().Any(row =>
                row.Id == propertyId && row.PortfolioId == portfolioId)) &&
            (unitId == null || db.Set<Unit>().Any(row =>
                row.Id == unitId && row.PortfolioId == portfolioId &&
                ((effectivePropertyId ?? propertyId) == null ||
                 row.PropertyId == (effectivePropertyId ?? propertyId)))), ct);

    private Task<bool> DistributionReferencesExistAsync(
        int portfolioId, int ownerId, int? propertyId, RentalCommandDbContext db, CancellationToken ct) =>
        db.Set<OwnerEntity>().AnyAsync(owner =>
            owner.Id == ownerId && owner.PortfolioId == portfolioId &&
            (propertyId == null || db.Set<PropertyOwnership>().Any(ownership =>
                ownership.PropertyId == propertyId
                && ownership.PortfolioId == portfolioId
                && ownership.OwnerEntityId == ownerId
                && ownership.EffectiveToUtc == null)), ct);

    private T Read<T>(AtomicMoneyMutationCommand command) where T : class =>
        JsonSerializer.Deserialize<T>(command.RequestJson)
        ?? throw new ArgumentException("Money mutation request payload is invalid.");

    private void Validate(AtomicMoneyMutationCommand command)
    {
        if (command.PortfolioId <= 0 || command.ActorUserId <= 0 || command.AuthSessionId == Guid.Empty ||
            command.AccessContextId <= 0 || command.ExpectedAccessRevision <= 0 ||
            string.IsNullOrWhiteSpace(command.RequiredCapability) || string.IsNullOrWhiteSpace(command.RequestJson) ||
            string.IsNullOrWhiteSpace(command.IdempotencyKey) || command.IdempotencyKey.Length > 128 ||
            (command.Operation != AtomicMoneyOperation.Create && command.EntityId <= 0) ||
            command.BusinessNowUtc == default)
            throw new ArgumentException(
                "Portfolio, actor, access revision, capability, operation, payload, and business clock are required.");

        if (command.BusinessNowUtc.Kind != DateTimeKind.Utc)
        {
            throw new ArgumentException("Money mutation business clock must be UTC.");
        }
    }

    private DateTime Utc(DateTime value) => value.Kind == DateTimeKind.Utc ? value : value.ToUniversalTime();
    private DateTime? Utc(DateTime? value) => value.HasValue ? Utc(value.Value) : null;
    private string? Normalize(string? value)
    {
        var trimmed = value?.Trim();
        return string.IsNullOrEmpty(trimmed) ? null : trimmed;
    }
    private AtomicMoneyMutationResult Missing() => new(false, false, 0);
    private AtomicMoneyMutationResult Applied(int id, string? responseJson = null) =>
        new(true, true, id, responseJson);
    private DomainValidationException Conflict(string message) => new(message, 409);
    private CapabilityAuthorizationTargetKind RequiredTargetKind(AtomicMoneyMutationCommand command) =>
        command.Domain is AtomicMoneyDomain.OwnerDistribution or AtomicMoneyDomain.OwnerContribution
            ? CapabilityAuthorizationTargetKind.Workspace
            : CapabilityAuthorizationTargetKind.Property;
    private UnauthorizedAccessException Denied(string message) => new(message);
}

public static class AtomicMoneyMutation
{
    public static readonly AtomicJsonResultCodec<AtomicMoneyMutationResult> Codec =
        new("money.scoped-mutation.v2");

    public static AtomicMoneyMutationCommand Command<TRequest>(
        WorkspaceReadScope scope, string capability, AtomicMoneyDomain domain,
        AtomicMoneyOperation operation, int entityId, string idempotencyKey, TRequest request,
        DateTime businessNowUtc) where TRequest : class =>
        new(scope.PortfolioId, scope.UserId, scope.SessionId, scope.AccessContextId, scope.AccessRevision,
            capability, domain, operation, entityId, idempotencyKey, JsonSerializer.Serialize(request), businessNowUtc);

    public static AtomicCommandIdentity Identity(AtomicMoneyMutationCommand command) =>
        new($"money.{command.Domain.ToString().ToLowerInvariant()}.{command.Operation.ToString().ToLowerInvariant()}",
            $"{command.PortfolioId}:{command.AccessContextId}:{command.Domain}:{command.Operation}:" +
            $"{command.EntityId}:{command.IdempotencyKey}");
}
