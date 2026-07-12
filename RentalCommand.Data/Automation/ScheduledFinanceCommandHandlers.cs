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
            var currentMonth = Month(command.BusinessDateUtc);
            var lastPeriodToGenerate = Math.Min(
                MonthsBetween(startMonth, currentMonth) + 1,
                loan.TermMonths);
            tails.TryGetValue(loan.Id, out var tail);
            var nextPeriod = tail is null
                ? 1
                : PeriodIndexFromKey(tail.PeriodKey, startMonth) + 1;
            var openingBalance = tail?.BalanceAfter ?? loan.OriginalAmount;
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
                var dueDay = Math.Min(
                    loan.DayOfMonthDue <= 0 ? 1 : loan.DayOfMonthDue,
                    DateTime.DaysInMonth(periodMonth.Year, periodMonth.Month));
                var payment = new LoanPayment
                {
                    PortfolioId = loan.PortfolioId,
                    LoanId = loan.Id,
                    PeriodKey = $"{periodMonth.Year:D4}-{periodMonth.Month:D2}",
                    DueDate = new DateTime(
                        periodMonth.Year, periodMonth.Month, dueDay, 0, 0, 0, DateTimeKind.Utc),
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

public sealed class ScheduledFinanceClaimLostException : InvalidOperationException
{
    public ScheduledFinanceClaimLostException(string message) : base(message) { }
}
