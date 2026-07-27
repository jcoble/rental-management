using System.Text.Json;
using RentalCommand.Core.Atomic;
using RentalCommand.Core.Automation;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Services;

namespace RentalCommand.Data.Automation;

public sealed class ApplyClaimedDebtServiceBatchHandler
    : IAtomicCommandHandler<ApplyClaimedDebtServiceBatchCommand, ApplyScheduledFinanceBatchResult>
{
    private const int MaxOccurrencesPerSchedule = 36;

    public async Task<ApplyScheduledFinanceBatchResult> HandleAsync(
        ApplyClaimedDebtServiceBatchCommand command,
        IAtomicWriteAttempt attempt,
        CancellationToken ct)
    {
        var loans = await attempt.ScheduledFinance.LockDebtServiceClaimsAsync(
            command.LoanIds,
            command.ClaimToken,
            command.BusinessDateUtc,
            ct);
        if (loans.Count == 0)
        {
            throw new ScheduledFinanceClaimLostException("The debt-service claim is stale, expired, or owned by another worker.");
        }

        var tails = await attempt.ScheduledFinance.LoadLoanPaymentTailsAsync(
            loans.Select(loan => loan.Id).ToArray(), ct);
        var generated = new List<LoanPayment>();

        foreach (var loan in loans)
        {
            var startMonth = Month(loan.StartDate);
            var dueDay = DueDay(loan.DayOfMonthDue);
            var lastPeriodToGenerate = Math.Min(
                LastDuePeriodIndexOnOrBefore(command.BusinessDateUtc, startMonth, dueDay),
                loan.TermMonths);
            tails.TryGetValue(loan.Id, out var tail);
            var firstEligiblePeriod = FirstDuePeriodIndexOnOrAfter(
                Max(loan.StartDate, loan.CreatedAt), startMonth, dueDay);
            var tailPeriod = tail is null ? (int?)null : PeriodIndexFromKey(tail.PeriodKey, startMonth);
            // There is no explicit "imported existing loan" marker; the safe invariant is that
            // Engine never generates a period whose due date predates the loan record creation.
            var nextPeriod = tailPeriod is null
                ? firstEligiblePeriod
                : Math.Max(tailPeriod.Value + 1, firstEligiblePeriod);
            var openingBalance = tail is not null && tailPeriod.HasValue && tailPeriod.Value >= firstEligiblePeriod
                ? tail.BalanceAfter
                : loan.CurrentBalance;
            var lastBalance = openingBalance;
            var paidOff = false;
            var generatedForLoan = 0;

            var batchLastPeriod = Math.Min(
                lastPeriodToGenerate,
                checked(nextPeriod + MaxOccurrencesPerSchedule - 1));
            for (var period = nextPeriod; period <= batchLastPeriod; period++)
            {
                var periodMonth = startMonth.AddMonths(period - 1);
                var split = AmortizationCalculator.Split(
                    openingBalance,
                    loan.AnnualInterestRatePct,
                    loan.MonthlyPrincipalInterest,
                    loan.MonthlyEscrow);
                var payment = new LoanPayment
                {
                    PortfolioId = loan.PortfolioId,
                    LoanId = loan.Id,
                    PeriodKey = $"{periodMonth.Year:D4}-{periodMonth.Month:D2}",
                    DueDate = new DateTime(
                        periodMonth.Year,
                        periodMonth.Month,
                        Math.Min(dueDay, DateTime.DaysInMonth(periodMonth.Year, periodMonth.Month)),
                        0, 0, 0, DateTimeKind.Utc),
                    InterestAmount = split.Interest,
                    PrincipalAmount = split.Principal,
                    EscrowAmount = split.Escrow,
                    TotalAmount = split.Total,
                    BalanceAfter = split.BalanceAfter,
                    Status = LoanPaymentStatus.Scheduled,
                    PaymentDoesNotCoverInterest = split.DoesNotCoverInterest,
                    CreatedAt = command.AppliedAtUtc,
                };
                generated.Add(payment);
                generatedForLoan++;
                openingBalance = split.BalanceAfter;
                lastBalance = split.BalanceAfter;
                if (split.PaidOff)
                {
                    paidOff = true;
                    break;
                }
            }

            ClearClaim(loan);
            if (generatedForLoan > 0)
            {
                loan.CurrentBalance = lastBalance;
                loan.UpdatedAt = command.AppliedAtUtc;
                if (paidOff) loan.Status = LoanStatus.PaidOff;
            }

            attempt.BindSemanticAudit(loan, new AtomicSemanticAudit(
                loan.PortfolioId,
                nameof(Loan),
                loan.Id,
                AuditLogOperation.Updated,
                ActorLabel: "system:debt-service",
                NewValues: JsonSerializer.Serialize(new
                {
                    loan.CurrentBalance,
                    loan.Status,
                    GeneratedPayments = generatedForLoan,
                    ClaimReleased = true,
                }),
                ChangeReason: "Applied claimed debt-service schedule generation."));
        }

        attempt.Persistence.AddRange(generated);
        await attempt.FlushBusinessAsync(ct);
        foreach (var payment in generated)
        {
            attempt.StageSemanticEvent(new AtomicSemanticAudit(
                payment.PortfolioId,
                nameof(LoanPayment),
                payment.Id,
                AuditLogOperation.Created,
                ActorLabel: "system:debt-service",
                NewValues: JsonSerializer.Serialize(new
                {
                    payment.LoanId,
                    payment.PeriodKey,
                    payment.DueDate,
                    payment.TotalAmount,
                    payment.BalanceAfter,
                }),
                ChangeReason: "Generated a debt-service schedule occurrence."));
        }

        return new ApplyScheduledFinanceBatchResult(
            ScheduledFinanceApplyOutcome.Applied,
            loans.Count,
            generated.Count);
    }

    private static DateTime Month(DateTime date) =>
        new(date.Year, date.Month, 1, 0, 0, 0, DateTimeKind.Utc);

    private static int MonthsBetween(DateTime from, DateTime to) =>
        (to.Year - from.Year) * 12 + to.Month - from.Month;

    private static int DueDay(int dayOfMonthDue) => Math.Max(dayOfMonthDue, 1);

    private static int FirstDuePeriodIndexOnOrAfter(
        DateTime activationDate,
        DateTime startMonth,
        int dueDay)
    {
        var activationMonth = Month(activationDate);
        var firstDueMonth = DueDate(activationMonth, dueDay) < activationDate.Date
            ? activationMonth.AddMonths(1)
            : activationMonth;
        return Math.Max(1, MonthsBetween(startMonth, firstDueMonth) + 1);
    }

    private static int LastDuePeriodIndexOnOrBefore(
        DateTime businessDate,
        DateTime startMonth,
        int dueDay)
    {
        var businessMonth = Month(businessDate);
        var dueThroughMonth = DueDate(businessMonth, dueDay) > businessDate.Date
            ? businessMonth.AddMonths(-1)
            : businessMonth;
        return MonthsBetween(startMonth, dueThroughMonth) + 1;
    }

    private static DateTime DueDate(DateTime month, int dueDay) =>
        new(
            month.Year,
            month.Month,
            Math.Min(dueDay, DateTime.DaysInMonth(month.Year, month.Month)),
            0, 0, 0, DateTimeKind.Utc);

    private static DateTime Max(DateTime left, DateTime right) =>
        left >= right ? left : right;

    private static int PeriodIndexFromKey(string periodKey, DateTime startMonth)
    {
        if (periodKey.Length >= 7
            && int.TryParse(periodKey.AsSpan(0, 4), out var year)
            && int.TryParse(periodKey.AsSpan(5, 2), out var month))
        {
            return MonthsBetween(startMonth, new DateTime(year, month, 1)) + 1;
        }

        throw new InvalidOperationException($"Loan payment period key '{periodKey}' is invalid.");
    }

    private static void ClearClaim(Loan loan)
    {
        loan.WorkerClaimOwner = null;
        loan.WorkerClaimToken = null;
        loan.WorkerClaimExpiresAtUtc = null;
    }
}

public sealed class ApplyClaimedRecurringExpenseBatchHandler
    : IAtomicCommandHandler<ApplyClaimedRecurringExpenseBatchCommand, ApplyScheduledFinanceBatchResult>
{
    private const int MaxOccurrencesPerSchedule = 36;

    public async Task<ApplyScheduledFinanceBatchResult> HandleAsync(
        ApplyClaimedRecurringExpenseBatchCommand command,
        IAtomicWriteAttempt attempt,
        CancellationToken ct)
    {
        var templates = await attempt.ScheduledFinance.LockRecurringExpenseClaimsAsync(
            command.RecurringExpenseIds,
            command.ClaimToken,
            command.BusinessDateUtc,
            ct);
        if (templates.Count == 0)
        {
            throw new ScheduledFinanceClaimLostException("The recurring-expense claim is stale, expired, or owned by another worker.");
        }

        var generated = new List<Expense>();
        foreach (var template in templates)
        {
            var runDate = template.NextRunDate;
            var periods = 0;
            while (runDate <= command.BusinessDateUtc && periods < MaxOccurrencesPerSchedule)
            {
                var expense = new Expense
                {
                    PortfolioId = template.PortfolioId,
                    PropertyId = template.PropertyId,
                    UnitId = template.UnitId,
                    RecurringExpenseId = template.Id,
                    RecurringExpenseOccurrenceDate = runDate,
                    Category = template.Category,
                    Description = template.Description,
                    Status = ExpenseStatus.Pending,
                    Amount = template.Amount,
                    IncurredAt = runDate,
                    Notes = template.Notes,
                    CreatedAt = command.AppliedAtUtc,
                    UpdatedAt = command.AppliedAtUtc,
                };
                generated.Add(expense);
                attempt.Persistence.Add(expense);
                attempt.BindSemanticAudit(expense, new AtomicSemanticAudit(
                    template.PortfolioId,
                    nameof(Expense),
                    0,
                    AuditLogOperation.Created,
                    ActorLabel: "system:recurring-expense",
                    NewValues: JsonSerializer.Serialize(new
                    {
                        RecurringExpenseId = template.Id,
                        OccurrenceDate = runDate,
                        template.Amount,
                        template.Category,
                    }),
                    ChangeReason: "Generated a recurring-expense schedule occurrence."));
                runDate = Advance(runDate, template.Frequency);
                periods++;
            }

            var priorNextRunDate = template.NextRunDate;
            template.NextRunDate = runDate;
            template.UpdatedAt = command.AppliedAtUtc;
            ClearClaim(template);
            attempt.BindSemanticAudit(template, new AtomicSemanticAudit(
                template.PortfolioId,
                nameof(RecurringExpense),
                template.Id,
                AuditLogOperation.Updated,
                ActorLabel: "system:recurring-expense",
                OldValues: JsonSerializer.Serialize(new { NextRunDate = priorNextRunDate }),
                NewValues: JsonSerializer.Serialize(new
                {
                    template.NextRunDate,
                    GeneratedExpenses = periods,
                    ClaimReleased = true,
                }),
                ChangeReason: "Advanced a claimed recurring-expense schedule."));
        }

        return new ApplyScheduledFinanceBatchResult(
            ScheduledFinanceApplyOutcome.Applied,
            templates.Count,
            generated.Count);
    }

    private static DateTime Advance(DateTime date, RecurringExpenseFrequency frequency) => frequency switch
    {
        RecurringExpenseFrequency.Monthly => date.AddMonths(1),
        RecurringExpenseFrequency.Quarterly => date.AddMonths(3),
        RecurringExpenseFrequency.Annual => date.AddYears(1),
        _ => throw new InvalidOperationException($"Unsupported recurring expense frequency {frequency}."),
    };

    private static void ClearClaim(RecurringExpense template)
    {
        template.WorkerClaimOwner = null;
        template.WorkerClaimToken = null;
        template.WorkerClaimExpiresAtUtc = null;
    }
}

public sealed class ApplyClaimedRecurringMaintenanceBatchHandler
    : IAtomicCommandHandler<ApplyClaimedRecurringMaintenanceBatchCommand, ApplyScheduledFinanceBatchResult>
{
    public async Task<ApplyScheduledFinanceBatchResult> HandleAsync(
        ApplyClaimedRecurringMaintenanceBatchCommand command,
        IAtomicWriteAttempt attempt,
        CancellationToken ct)
    {
        var ids = command.RecurringMaintenanceTaskIds.Distinct().ToArray();
        if (ids.Length == 0 || ids.Length != command.RecurringMaintenanceTaskIds.Length ||
            command.ClaimToken == Guid.Empty || command.BusinessDateUtc == default ||
            command.AppliedAtUtc == default || string.IsNullOrWhiteSpace(command.BusinessTimeZoneId))
            throw new ArgumentException("A complete recurring-maintenance claim batch is required.", nameof(command));

        var businessTimeZone = TimeZoneInfo.FindSystemTimeZoneById(command.BusinessTimeZoneId);
        var tasks = await attempt.ScheduledFinance.LockRecurringMaintenanceClaimsAsync(
            ids, command.ClaimToken, command.BusinessDateUtc, ct);
        if (tasks.Count != ids.Length)
        {
            throw new ScheduledFinanceClaimLostException(
                "The recurring-maintenance claim batch is stale, expired, incomplete, or owned by another worker.");
        }

        var generated = new List<WorkOrder>(tasks.Count);
        foreach (var task in tasks)
        {
            var priorNextDueDate = task.NextDueDate;
            var nextDueDate = priorNextDueDate;
            do
            {
                nextDueDate = Advance(nextDueDate, task.RecurrenceInterval);
            }
            while (nextDueDate <= command.BusinessDateUtc);

            var workOrder = new WorkOrder
            {
                PortfolioId = task.PortfolioId,
                PropertyId = task.PropertyId,
                UnitId = task.UnitId,
                VendorId = task.VendorId,
                RecurringMaintenanceTaskId = task.Id,
                Title = task.Title,
                Description = string.IsNullOrWhiteSpace(task.Description) ? task.Title : task.Description,
                Category = string.IsNullOrWhiteSpace(task.Category) ? "General" : task.Category,
                Priority = task.Priority,
                Status = WorkOrderStatus.New,
                RequestedAt = command.AppliedAtUtc,
                ScheduledFor = ToScheduledUtc(priorNextDueDate, task.ScheduledTime, businessTimeZone),
                EstimatedCost = task.EstimatedCost,
                CreatedBy = "Recurring maintenance",
                UpdatedAt = command.AppliedAtUtc,
            };
            workOrder.StatusEvents.Add(new WorkOrderStatusEvent
            {
                PortfolioId = task.PortfolioId,
                FromStatus = null,
                ToStatus = WorkOrderStatus.New,
                Note = "Auto-created from recurring maintenance schedule",
                ChangedByUserId = null,
                ChangedByLabel = "System",
                CreatedAtUtc = command.AppliedAtUtc,
            });
            generated.Add(workOrder);
            attempt.Persistence.Add(workOrder);
            attempt.BindSemanticAudit(workOrder, new AtomicSemanticAudit(
                task.PortfolioId,
                nameof(WorkOrder),
                0,
                AuditLogOperation.Created,
                ActorLabel: "system:recurring-maintenance",
                NewValues: JsonSerializer.Serialize(new
                {
                    RecurringMaintenanceTaskId = task.Id,
                    task.PropertyId,
                    task.UnitId,
                    task.VendorId,
                    task.Title,
                    DueDate = priorNextDueDate,
                }),
                ChangeReason: "Generated a recurring-maintenance work order."));

            task.LastGeneratedAtUtc = command.AppliedAtUtc;
            task.NextDueDate = nextDueDate;
            task.UpdatedAt = command.AppliedAtUtc;
            ClearClaim(task);
            attempt.StageSemanticEvent(new AtomicSemanticAudit(
                task.PortfolioId,
                nameof(RecurringMaintenanceTask),
                task.Id,
                AuditLogOperation.Updated,
                ActorLabel: "system:recurring-maintenance",
                OldValues: JsonSerializer.Serialize(new { NextDueDate = priorNextDueDate }),
                NewValues: JsonSerializer.Serialize(new
                {
                    task.NextDueDate,
                    task.LastGeneratedAtUtc,
                    ClaimReleased = true,
                }),
                ChangeReason: "Advanced a claimed recurring-maintenance schedule."));
        }

        await attempt.FlushBusinessAsync(ct);
        foreach (var workOrder in generated)
        {
            attempt.StageOutbox(RentalCommand.Data.Operations.CreateWorkOrderHandler.DataUpdate(
                workOrder.PortfolioId,
                nameof(WorkOrder),
                workOrder.Id,
                $"recurring-maintenance-work-order:{command.ClaimToken:N}:{workOrder.RecurringMaintenanceTaskId}",
                command.AppliedAtUtc));
            attempt.StageOutbox(RentalCommand.Data.Operations.CreateWorkOrderHandler.DataUpdate(
                workOrder.PortfolioId,
                nameof(RecurringMaintenanceTask),
                workOrder.RecurringMaintenanceTaskId!.Value,
                $"recurring-maintenance-task-update:{command.ClaimToken:N}:{workOrder.RecurringMaintenanceTaskId}",
                command.AppliedAtUtc));
        }

        return new ApplyScheduledFinanceBatchResult(
            ScheduledFinanceApplyOutcome.Applied,
            tasks.Count,
            generated.Count);
    }

    private static DateTime Advance(DateTime date, RecurrenceInterval interval) => interval switch
    {
        RecurrenceInterval.Weekly => date.AddDays(7),
        RecurrenceInterval.Monthly => date.AddMonths(1),
        RecurrenceInterval.Quarterly => date.AddMonths(3),
        RecurrenceInterval.SemiAnnually => date.AddMonths(6),
        RecurrenceInterval.Annually => date.AddYears(1),
        _ => throw new InvalidOperationException($"Unsupported recurring-maintenance interval {interval}."),
    };

    private static DateTime? ToScheduledUtc(
        DateTime dueDate, TimeOnly? scheduledTime, TimeZoneInfo businessTimeZone)
    {
        if (!scheduledTime.HasValue) return null;
        var local = DateTime.SpecifyKind(
            dueDate.Date.Add(scheduledTime.Value.ToTimeSpan()), DateTimeKind.Unspecified);
        return TimeZoneInfo.ConvertTimeToUtc(local, businessTimeZone);
    }

    private static void ClearClaim(RecurringMaintenanceTask task)
    {
        task.WorkerClaimOwner = null;
        task.WorkerClaimToken = null;
        task.WorkerClaimExpiresAtUtc = null;
    }
}

public sealed class ScheduledFinanceClaimLostException : InvalidOperationException
{
    public ScheduledFinanceClaimLostException(string message) : base(message) { }
}
